using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SoulLink.Core;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>
    /// 配置读写。默认放在 %LOCALAPPDATA%\SoulLink\config.json（无需管理员写权限），
    /// 也支持用 SOULLINK_CONFIG 环境变量或 exe 同目录的 config.portable.json 做便携模式。
    /// </summary>
    public static class ConfigStore
    {
        private static readonly JsonSerializerOptions Opt = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() }
        };

        public static string ConfigPath
        {
            get
            {
                string? env = Environment.GetEnvironmentVariable("SOULLINK_CONFIG");
                if (!string.IsNullOrWhiteSpace(env)) return env!;

                string portable = Path.Combine(AppContext.BaseDirectory, "config.portable.json");
                if (File.Exists(portable)) return portable;

                return Path.Combine(ConfigDir, "config.json");
            }
        }

        // ★ 配置目录同样按身份隔离，两个版本互不干扰各自的账号与设置。
        public static string ConfigDir => AppIdentity.ConfigDir;

        public static string StatePath => Path.Combine(ConfigDir, "state.json");

        /// <summary>
        /// 按日期分文件的日志路径：rdp-debug-20261116.log
        ///
        /// ★ 为什么按天分：长期挂机日志量虽已大幅压缩，但单文件久了仍然不好查，
        ///   也不利于用户只发某一天的文件过来。
        /// </summary>
        public static string DailyLogPath(string prefix)
        {
            try
            {
                return Path.Combine(ConfigDir, prefix + "-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            }
            catch { return Path.Combine(ConfigDir, prefix + ".log"); }
        }

        /// <summary>清掉超过 N 天的历史日志（保留最近 N 天）。</summary>
        public static void PruneDailyLogs(string prefix, int keepDays = 7)
        {
            try
            {
                if (!Directory.Exists(ConfigDir)) return;
                var cutoff = DateTime.Now.Date.AddDays(-keepDays);
                foreach (var f in Directory.GetFiles(ConfigDir, prefix + "-*.log"))
                {
                    try
                    {
                        string name = Path.GetFileNameWithoutExtension(f);   // rdp-debug-20261116
                        int dash = name.LastIndexOf('-');
                        if (dash < 0) continue;
                        string day = name.Substring(dash + 1);
                        if (day.Length != 8) continue;
                        if (!DateTime.TryParseExact(day, "yyyyMMdd", null,
                                System.Globalization.DateTimeStyles.None, out var d)) continue;
                        if (d < cutoff) File.Delete(f);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static readonly Lock ConfigIo = new();

        // ── 后台串行保存队列 ──────────────────────────────────────────
        //
        // ★ 为什么要有它（用户反馈「点按钮/切页要等」+ 历史事故「配置被旧值覆盖」）：
        //
        //   1. 原来 Save() 在**调用线程**同步做「序列化 + 写临时文件 + File.Replace」，
        //      而调用方既有 UI 线程（点按钮/拖滑块），也有后台调度器（每秒/每 5 秒）。
        //      写盘是同步 IO，挂在 UI 线程上就是卡顿的直接来源之一。
        //
        //   2. 历史事故（ConsolePage.xaml.cs:240 有记录）：滑块初始化时 ValueChanged
        //      误触发 Save，把用户已保存的值覆盖掉。根因是「谁最后写谁赢」+ 无保序。
        //
        //   这里改成**后台串行队列**：
        //     · 入队前先在调用线程把配置序列化成**不可变字符串**（快照），
        //       彻底杜绝后台线程读到一个「正在被 UI 改到一半」的活对象；
        //     · 单消费者按入队顺序逐个原子写盘（临时文件 + File.Replace），
        //       严格 FIFO —— 后入队的值一定写在先入队的之后，旧值不可能盖新值；
        //     · 序列化（纯内存）留在调用线程，真正的磁盘 IO 全在后台线程；
        //     · 退出前 Flush() 排空，ProcessExit 再兜底同步写一遍，保证最后一次保存不丢。

        private sealed record SaveItem(string Json, TaskCompletionSource? Done);

        private static readonly BlockingCollection<SaveItem> SaveQueue =
            new(new ConcurrentQueue<SaveItem>());

        private static int _exiting;

        private static readonly Task Writer = Task.Run(() =>
        {
            try
            {
                foreach (var item in SaveQueue.GetConsumingEnumerable())
                {
                    try { WriteAtomic(item.Json); }
                    catch (Exception ex)
                    {
                        // 后台写失败无法抛回调用方（可能已经是几拍前的事了），
                        // 记录到 error.log，保证「不丢保存」的失败是可观测的。
                        try { App.AppendErrorLog("ConfigStore.SaveQueue", ex); } catch { }
                    }
                    finally { item.Done?.TrySetResult(); }
                }
            }
            catch { }
        });

        /// <summary>
        /// 序列化一份配置（纯内存，调用线程执行，耗时极小）。
        /// </summary>
        private static string Serialize(AppConfig cfg) => JsonSerializer.Serialize(cfg, Opt);

        /// <summary>
        /// 原子写盘：临时文件 + File.Replace。内部加锁，供队列与同步路径共用。
        /// </summary>
        private static void WriteAtomic(string json)
        {
            lock (ConfigIo)
            {
                string path = ConfigPath;
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                try
                {
                    if (File.Exists(path)) File.Replace(tmp, path, null);
                    else File.Move(tmp, path);
                }
                catch
                {
                    // 落盘失败必须让调用者知道，不能宣称 END 已可靠持久化。
                    File.Copy(tmp, path, true); File.Delete(tmp);
                }
            }
        }

        static ConfigStore()
        {
            // 兜底：任何退出路径（含直接 Shutdown 没走 Flush 的）都不丢最后一次保存。
            AppDomain.CurrentDomain.ProcessExit += (_, __) =>
            {
                try { DrainSynchronously(); } catch { }
            };
        }

        /// <summary>
        /// 同步排空队列：拒绝新入队，把剩余项逐个写盘。退出前调用。
        /// 幂等；之后 Save() 会退化为同步写，仍不丢数据。
        /// </summary>
        public static void Flush()
        {
            DrainSynchronously();
        }

        private static void DrainSynchronously()
        {
            if (Interlocked.Exchange(ref _exiting, 1) == 1) return;
            try { SaveQueue.CompleteAdding(); } catch { }
            // ★ 不自己并发 TryTake：那会和 Writer 从同一个 FIFO 队列抢项，
            //   两个线程的写盘顺序可能倒置（旧值反而后写）。
            //   正确做法是让 Writer 独占地把队列按序排空，这里只等它完成。
            try { Writer.Wait(2000); } catch { }
        }

        public static AppConfig Load()
        {
            string originalJson = "";
            try
            {
                if (!File.Exists(ConfigPath)) return Defaults();
                string json = File.ReadAllText(ConfigPath, Encoding.UTF8); originalJson = json;
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, Opt);
                if (cfg == null) return Defaults();
                if (cfg.Settings == null) cfg.Settings = new AppSettings();
                if (cfg.Settings.RouteUpdate == null) cfg.Settings.RouteUpdate = new RouteUpdateSettings();
                if (cfg.Accounts == null) cfg.Accounts = new List<Account>();
                if (cfg.Actions == null) cfg.Actions = new List<ActionItem>();
            if (cfg.AutomationProfiles == null) cfg.AutomationProfiles = new List<AutomationProfile>();
                if (cfg.Automations == null) cfg.Automations = new List<AutomationFlow>();
                bool migrated = false;

                // 2.14.0 只有单目标字段 RouteUpdatePath；2.15 起是多目标 + 云端 + 定时。
                if (cfg.Settings.RouteUpdate.MigrateFromLegacy(cfg.Settings)) migrated = true;

                // ★★ 服务器地址外部化：把旧的三处分散地址归一进 ServerBaseUrl ★★
                //   · 优先用新的 ServerBaseUrl；为空则依次回填 RouteUpdate.CloudUrl、LobbyServerUrl；
                //   · 回填后清空旧字段，避免两处地址不一致。
                string baseUrl = ServerEndpoints.NormalizeBase(cfg.Settings.ServerBaseUrl);
                if (baseUrl.Length == 0)
                {
                    string route = ServerEndpoints.NormalizeBase(cfg.Settings.RouteUpdate?.CloudUrl);
                    string lobby = ServerEndpoints.NormalizeBase(cfg.Settings.LobbyServerUrl);
                    // 路线旧地址是完整清单 URL（…/pathing/update.txt），需裁回基地址：
                    // 联机旧地址本身就是基地址（…/api/…），两者结构不同，优先联机地址。
                    if (lobby.Length > 0) baseUrl = lobby;
                    else if (route.Length > 0)
                    {
                        string marker = "/pathing/";
                        int i = route.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                        baseUrl = i > 0 ? route.Substring(0, i) : route;
                    }
                }
                if (baseUrl.Length > 0)
                {
                    cfg.Settings.ServerBaseUrl = baseUrl;
                    if (cfg.Settings.RouteUpdate != null && cfg.Settings.RouteUpdate.CloudUrl != baseUrl)
                    {
                        cfg.Settings.RouteUpdate.CloudUrl = "";
                        migrated = true;
                    }
                    if (!string.Equals(cfg.Settings.LobbyServerUrl, baseUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        cfg.Settings.LobbyServerUrl = "";
                        migrated = true;
                    }
                }

                // ★★ 3.2.4 修正：清掉误设的 Esc 备用热键 ★★
                //   3.2.4 第一版把「退出全屏备用热键」默认写成了 "Escape"，
                //   而游戏里 Esc 是菜单/取消键，全屏时被钩子吞掉会让游戏没法用。
                //   用户明确指出这是设计错误（「必须是自定义，游戏需要 esc」）。
                //   现在默认留空；已落盘的旧值必须清掉，否则升级后照样吞键。
                if (string.Equals(cfg.Settings.ExitFullScreenHotkey, "Escape", StringComparison.OrdinalIgnoreCase))
                {
                    cfg.Settings.ExitFullScreenHotkey = "";
                    migrated = true;
                }

                foreach (var flow in cfg.Automations)
                {
                    if (flow.EngineVersion < 2)
                    {
                        if (flow.CreatedAt == null) flow.CreatedAt = DateTime.Now;
                        flow.EnsurePlcModel(); migrated = true;
                    }
                    else
                    {
                        flow.EnsurePlcModel();
                        if (flow.LifecycleVersion < 1)
                        {
                            // 旧实验状态不能推断外部副作用是否完成；保留已明确的 END 锁存，其余本轮状态重置。
                            if (flow.RunDayKey.Length == 0)
                            {
                                flow.RunDayKey = PlcLogic.DayKey(DateTime.Now, flow.EndBoundaryHour ?? 0);
                                flow.RunComplete = false; flow.PlcPower.Clear(); flow.ActionResults.Clear();
                                flow.SatisfiedContacts.Clear(); flow.RunningActions.Clear(); flow.WaitingActions.Clear(); flow.DelayUntil.Clear();
                                flow.MigrationNotes.Add("旧运行进度已安全重置；END 的完成日保留，日界默认 00:00，可在编辑器选择 04:00。");
                            }
                            flow.LifecycleVersion = 1; migrated = true;
                        }
                    }
                }
                if (migrated)
                {
                    // 备份成功后才允许保存新格式；写盘失败抛给外层，禁止把整套配置重置成默认。
                    string? dir = Path.GetDirectoryName(ConfigPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    string backup = Path.Combine(dir ?? "", "config-before-flow-migration.json");
                    File.WriteAllText(backup, originalJson, new UTF8Encoding(false));
                    // ★ 迁移是启动期一次性路径（非 UI 线程），Save 内部会等写盘完成再返回，
                    //   调用方返回后立即读盘校验新格式也拿得到新内容。
                    Save(cfg);
                }
                return cfg;
            }
            catch (Exception ex)
            {
                // 迁移/保存失败不是配置损坏：保留原内容并继续旧格式，绝不覆盖原文件或重置账号。
                App.AppendErrorLog("ConfigStore.LoadOrMigrate", ex);
                if (originalJson.Length > 0)
                    try { return JsonSerializer.Deserialize<AppConfig>(originalJson, Opt) ?? Defaults(); } catch { }
                return Defaults();
            }
        }

        public static void Save(AppConfig cfg)
        {
            if (cfg == null) return;

            // ★ 先序列化（纯内存、调用线程执行）：把「此刻」的配置固化成字符串，
            //   之后后台线程按入队顺序写盘。既保证 FIFO，也避免后台线程读到
            //   一个正被 UI 改到一半的活对象。
            string json;
            try { json = Serialize(cfg); }
            catch (Exception ex)
            {
                App.AppendErrorLog("ConfigStore.Save(serialize)", ex);
                return;
            }

            // 退出阶段：队列已排空，退化为同步写，最后一次保存仍落盘。
            if (Volatile.Read(ref _exiting) == 1)
            {
                try { WriteAtomic(json); } catch (Exception ex) { App.AppendErrorLog("ConfigStore.Save", ex); }
                return;
            }

            // ★ 只有 UI 线程才「入队后立刻返回」（这才是 P0 要消除的同步 IO）。
            //   其它线程（后台调度器 / FlowRunner / 无 WPF 的测试工具）保持同步语义：
            //   等自己的这一项写完再返回，调用方「写完才读回」的既有行为不变。
            //   —— 保序由单一 FIFO 队列保证，任何线程类型之间都不会倒置。
            if (OnUiThread())
            {
                try { SaveQueue.Add(new SaveItem(json, null)); }
                catch { try { WriteAtomic(json); } catch { } }
                return;
            }

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                SaveQueue.Add(new SaveItem(json, done));
                done.Task.GetAwaiter().GetResult();
            }
            catch
            {
                try { WriteAtomic(json); } catch { }
            }
        }

        /// <summary>
        /// 是否正运行在 WPF UI 线程上（无 WPF Application 的测试工具返回 false）。
        /// </summary>
        private static bool OnUiThread()
        {
            try
            {
                var app = System.Windows.Application.Current;
                var d = app?.Dispatcher;
                return d != null && d.CheckAccess();
            }
            catch { return false; }
        }

        public static AppConfig Defaults()
        {
            var cfg = new AppConfig();

            return cfg;
        }

        // ── 账号密码（仅用于 RDP 连接自动登录）──────────────────────────────

        public static string GetPassword(Account a) => SecretBox.Unprotect(a.PasswordCipher);

        public static void SetPassword(Account a, string plain)
        {
            a.PasswordCipher = string.IsNullOrEmpty(plain) ? "" : SecretBox.Protect(plain);
        }

        // ── 运行期状态（窗口位置、上次选中的动作）──────────────────────────

        public class UiState
        {
            public double Width { get; set; } = 1180;
            public double Height { get; set; } = 780;
            public string SelectedActionId { get; set; } = "";
            public string SelectedAccountId { get; set; } = "";
            public bool Maximized { get; set; }
            /// <summary>左侧导航是否收起</summary>
            public bool NavCollapsed { get; set; }
            /// <summary>左侧导航是否启用「自动收起」（鼠标悬停展开）</summary>
            public bool NavAutoHide { get; set; }
        }

        public static UiState LoadState()
        {
            try
            {
                if (!File.Exists(StatePath)) return new UiState();
                return JsonSerializer.Deserialize<UiState>(File.ReadAllText(StatePath, Encoding.UTF8), Opt) ?? new UiState();
            }
            catch { return new UiState(); }
        }

        public static void SaveState(UiState s)
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                File.WriteAllText(StatePath, JsonSerializer.Serialize(s, Opt), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}