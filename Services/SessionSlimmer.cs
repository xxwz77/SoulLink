using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace SoulLink.Services
{
    /// <summary>
    /// 挂机会话瘦身：清理 RDP 会话里与游戏/挂机无关的外围进程，
    /// 并可针对配置中的账号做注册表级的启动项优化。
    ///
    /// ★★ 为什么要做：★★
    ///   Windows 每个会话首次登录会自动拉起一批外围组件，实测单个会话
    ///   63 个进程 / 2.79 GB，其中游戏与必需进程只占约 250 MB。
    ///   挂机时没人看这些，纯属浪费内存、CPU 与电量。
    ///
    /// ★★ 安全边界（务必遵守）：★★
    ///   1. **白名单制**：只结束明确列出的进程名，其余一律不动。
    ///   2. **绝不碰** explorer / dwm / csrss / winlogon / svchost / SoulLink 自身。
    ///   3. **输入法保留**：ctfmon / ChsIME / TextInputHost / TabTip。
    ///      挂机时用不到，但用户随时可能连进去操作，杀了他会立刻发现打不了字。
    ///   4. **注册表只动配置里的账号**：见 ApplyStartupPolicy。
    ///   5. **不杀会自动重启的**：SearchHost / Widgets 杀了 3 秒后自己回来，
    ///      只能靠注册表根治，单纯杀毫无意义（实测）。
    ///   6. 本类**不引用 System.Management**（项目未引入该包），
    ///      所有进程信息走 Process API，注册表走 reg.exe 子进程。
    public static class SessionSlimmer
    {
        /// <summary>可安全结束、且不会自动重启的进程名。</summary>
        private static readonly string[] SafeToKill =
        {
            // Edge 浏览器本体 —— 单会话实测 8 个 / 391 MB，是最大的一块，且不会自动重启
            "msedge",
            // 手机连接 / 跨设备（实测不会立刻重启）
            "PhoneExperienceHost",
            "CrossDeviceService",
            "CrossDeviceResume",
            // 游戏栏（挂机不需要，且会在后台常驻）
            "GameBar",
            "GameBarFTServer",
            "GameBarPresenceWriter",
            // 触摸键盘（实测杀掉不重启，约 65 MB/会话）
            "TabTip",
            // Windows 首次登录引导（登录完成后就没用了，约 11 MB）
            "UserOOBEBroker",
            // OneDrive 文件协作授权（挂机用不到，约 21 MB/会话）
            "FileCoAuth",
        };

        // ★★ 实测「杀了会自动重启」—— 清理无效，只能靠注册表策略 ★★
        //
        //   TextInputHost   杀后 20 秒内自动回来（77 MB）
        //   ShellHost       杀后 20 秒内自动回来（54 MB）
        //   SearchHost      同上（112 MB）
        //   Widgets         同上（54 MB）
        //
        //   这些不放进清理名单：杀了立刻重启，不仅没省下内存，
        //   反而多一次进程创建的 CPU 开销。界面上显示在
        //   「需启动项优化」一栏，靠注册表根治。

        // ★★ 明确不清理的「看起来无用但其实危险」的进程 ★★
        //
        //   以下这些在单个会话里也有实例，看似可以清，实测风险很高：
        //
        //   nvcontainer / NVDisplay.Container
        //     NVIDIA 驱动框架的宿主。虽然会话里也有一份，但杀掉会导致
        //     驱动框架重启 —— 游戏画面黑屏、DXGI 设备丢失。
        //     显存回收根本不会因为杀它而变快。
        //
        //   RtkAudUService64
        //     音频服务。挂机确实不出声，但杀掉可能让游戏音频初始化失败，
        //     部分游戏会因此卡在加载界面。
        //
        //   FileCoAuth
        //     OneDrive 文件协作。杀掉会触发一连串重试与重新拉起。
        //
        //   AppActions
        //     系统应用操作代理。它会随系统 UI 自动重启，杀了没有净收益。

        /// <summary>明确不清理的高风险进程（供界面说明）。</summary>
        public static readonly string[] RiskyNotRecommended =
        {
            "nvcontainer", "NVDisplay.Container",
            "RtkAudUService64", "FileCoAuth", "AppActions",
        };

        /// <summary>内置可清理进程名（始终生效）。</summary>
        public static IReadOnlyList<string> BuiltInKillNames => SafeToKill;

        /// <summary>系统保护名单（只读，供界面展示）。</summary>
        public static IReadOnlyList<string> ProtectedNames => NeverKill.OrderBy(x => x).ToList();

        /// <summary>
        /// 当前生效的清理名单。
        ///
        /// ★ 由三部分组成：
        ///   ① 内置名单 SafeToKill
        ///   ② 减去用户在设置里**移除**的项（SessionSlimDisabled）
        ///   ③ 加上用户**追加**的项（SessionSlimKillList）
        ///   保护名单 NeverKill 优先级最高，任何来源都不会让它生效。
        ///
        /// ★ 内置项也允许移除 —— 用户明确要求"默认选中的东西也要能去掉"。
        ///   原来内置项在界面上没有移除按钮，等于绑架用户。
        /// </summary>
        public static List<string> EffectiveKillNames()
        {
            var set = new List<string>();
            var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var cfg = App.Config?.Settings;
                if (cfg?.SessionSlimDisabled != null)
                    foreach (var raw in cfg.SessionSlimDisabled)
                    {
                        string d = Normalize(raw);
                        if (d.Length > 0) disabled.Add(d);
                    }
            }
            catch { }

            foreach (var name in SafeToKill)
            {
                if (disabled.Contains(name)) continue;
                if (NeverKill.Contains(name)) continue;
                set.Add(name);
            }

            try
            {
                var cfg = App.Config?.Settings;
                if (cfg?.SessionSlimKillList != null)
                {
                    foreach (var raw in cfg.SessionSlimKillList)
                    {
                        string name = Normalize(raw);
                        if (name.Length == 0) continue;
                        if (NeverKill.Contains(name)) continue;     // 保护名单优先
                        if (disabled.Contains(name)) continue;      // 用户已移除
                        if (!set.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)))
                            set.Add(name);
                    }
                }
            }
            catch { }
            return set;
        }

        /// <summary>该项是否属于内置（仅供界面标注，判断的是"原始名单"）。</summary>
        public static bool IsBuiltIn(string name)
        {
            string n = Normalize(name);
            return SafeToKill.Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>从清理名单移除一项（内置的会记入 disabled，自定义的直接删）。</summary>
        public static bool RemoveFromList(string name)
        {
            string n = Normalize(name);
            if (n.Length == 0) return false;
            try
            {
                var cfg = App.Config?.Settings;
                if (cfg == null) return false;

                bool changed = false;
                // 自定义项：直接删
                int removed = cfg.SessionSlimKillList?.RemoveAll(
                    x => string.Equals(Normalize(x), n, StringComparison.OrdinalIgnoreCase)) ?? 0;
                if (removed > 0) changed = true;

                // 内置项：记入 disabled，避免下次又被加回来
                if (IsBuiltIn(n))
                {
                    cfg.SessionSlimDisabled ??= new List<string>();
                    if (!cfg.SessionSlimDisabled.Any(
                            x => string.Equals(Normalize(x), n, StringComparison.OrdinalIgnoreCase)))
                    {
                        cfg.SessionSlimDisabled.Add(n);
                        changed = true;
                    }
                }

                if (changed) ConfigStore.Save(App.Config);
                return changed;
            }
            catch { return false; }
        }

        /// <summary>恢复默认：清空自定义追加项与内置禁用项。</summary>
        public static void ResetList()
        {
            try
            {
                var cfg = App.Config?.Settings;
                if (cfg == null) return;
                cfg.SessionSlimKillList?.Clear();
                cfg.SessionSlimDisabled?.Clear();
                ConfigStore.Save(App.Config);
            }
            catch { }
        }

        /// <summary>规范化进程名：去空格、去 .exe 后缀。</summary>
        public static string Normalize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string s = raw.Trim();
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(0, s.Length - 4);
            return s;
        }

        /// <summary>这个名字是否受保护（不允许清理）。</summary>
        public static bool IsProtected(string name)
            => NeverKill.Contains(Normalize(name));

        /// <summary>
        /// 会自动重启、杀进程无意义的外围组件。
        /// 仅用于「统计可优化空间」，实际清理要靠注册表策略。
        /// </summary>
        private static readonly string[] NeedsPolicyFixes =
        {
            "SearchHost", "Widgets", "WidgetService",
            // 实测会自动重启的两个（2026-11-21 验证，20 秒内回来）
            "TextInputHost", "ShellHost",
        };

        /// <summary>绝对不能结束的进程名。即使配置写错也不动。</summary>
        private static readonly HashSet<string> NeverKill = new(StringComparer.OrdinalIgnoreCase)
        {
            // 系统关键
            "explorer", "dwm", "csrss", "winlogon", "wininit", "services", "lsass",
            "svchost", "System", "Idle", "smss", "fontdrvhost",
            // RDP 基础设施
            "rdpclip", "rdpinput", "RdpInput", "TermSrv",
            // 本程序
            "SoulLink", "SoulLink.Agent", "SoulLink-Stable", "SoulLink.Updater",
            // 输入法核心（用户随时要打字，这两个绝不能杀）
            // ★ TextInputHost / TabTip（触摸键盘）**不在此列**：
            //   挂机场景用不到触摸键盘，已移入可清理名单（用户可自行移除）。
        };

        /// <summary>瘦身结果。</summary>
        public sealed class Result
        {
            public int Killed { get; set; }
            public long FreedBytes { get; set; }
            public List<string> Names { get; } = new();
            public string Message { get; set; } = "";
            public bool Any => Killed > 0;
        }

        // ── 统计 ────────────────────────────────────────────────────────

        /// <summary>会话进程概况。</summary>
        public sealed class SessionStat
        {
            public int SessionId { get; set; }
            public int Total { get; set; }
            public long TotalBytes { get; set; }
            /// <summary>可立即清理（白名单内、不会重启）。</summary>
            public int Killable { get; set; }
            public long KillableBytes { get; set; }
            /// <summary>需注册表策略才能优化的（会重启）。</summary>
            public int PolicyOnly { get; set; }
            public long PolicyOnlyBytes { get; set; }
            public Dictionary<string, (int Count, long Bytes)> KillableDetail { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>统计指定会话的进程构成（不杀任何进程）。</summary>
        public static SessionStat Count(int sessionId)
        {
            var s = new SessionStat { SessionId = sessionId };
            var killSet = EffectiveKillNames();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.SessionId != sessionId) continue;
                    string name = p.ProcessName;
                    long mem = 0;
                    try { mem = p.WorkingSet64; } catch { }

                    s.Total++;
                    s.TotalBytes += mem;

                    if (killSet.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        s.Killable++;
                        s.KillableBytes += mem;
                        if (!s.KillableDetail.TryGetValue(name, out var d)) d = (0, 0);
                        s.KillableDetail[name] = (d.Count + 1, d.Bytes + mem);
                    }
                    else if (NeedsPolicyFixes.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        s.PolicyOnly++;
                        s.PolicyOnlyBytes += mem;
                    }
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
            return s;
        }

        // ── 清理 ────────────────────────────────────────────────────────

        /// <summary>
        /// 结束指定会话里的外围进程。失败不抛异常。
        /// </summary>
        /// <param name="dryRun">true = 只统计不杀。</param>
        public static Result Slim(int sessionId, bool dryRun = false)
        {
            var r = new Result();
            if (sessionId <= 0) { r.Message = "无效的会话号"; return r; }

            try
            {
                var killSet = EffectiveKillNames();
                var targets = new List<Process>();
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.SessionId != sessionId) { p.Dispose(); continue; }
                        string name = p.ProcessName;
                        if (NeverKill.Contains(name)) { p.Dispose(); continue; }
                        if (!killSet.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)))
                        {
                            p.Dispose();
                            continue;
                        }
                        targets.Add(p);
                    }
                    catch { try { p.Dispose(); } catch { } }
                }

                if (targets.Count == 0)
                {
                    r.Message = "会话 " + sessionId + " 没有可立即清理的外围进程";
                    return r;
                }

                foreach (var p in targets)
                {
                    try
                    {
                        long mem = p.WorkingSet64;
                        string name = p.ProcessName;
                        if (!dryRun) p.Kill(entireProcessTree: false);
                        r.Killed++;
                        r.FreedBytes += mem;
                        if (!r.Names.Contains(name)) r.Names.Add(name);
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }

                string mb = Math.Round(r.FreedBytes / 1024.0 / 1024.0).ToString();
                r.Message = dryRun
                    ? "会话 " + sessionId + " 可清理 " + r.Killed + " 个进程，约 " + mb + " MB"
                    : "会话 " + sessionId + " 已清理 " + r.Killed + " 个外围进程，释放约 " + mb + " MB";
            }
            catch (Exception ex) { r.Message = "清理失败：" + ex.Message; }
            return r;
        }

        // ── 注册表策略（仅限配置中的账号）────────────────────────────────

        /// <summary>单个账户的策略应用结果。</summary>
        public sealed class PolicyResult
        {
            public string UserName { get; set; } = "";
            public bool Ok { get; set; }
            public string Message { get; set; } = "";
        }

        /// <summary>
        /// 对指定用户应用「关闭外围组件自动启动」的注册表策略。
        ///
        /// ★★ 只影响该用户，绝不影响其他账户。★★
        ///   做法：临时挂载该用户的 NTUSER.DAT 到 HKUSoulLink_&lt;user&gt;，
        ///   只写这个分支下的 Policies 键，然后立即卸除。
        ///
        /// ★ 用户正在登录时 NTUSER.DAT 被独占，无法挂载 —— 此时直接跳过并提示，
        ///   不做任何强制操作。
        /// </summary>
        public static PolicyResult ApplyStartupPolicy(string userName)
        {
            var res = new PolicyResult { UserName = userName };
            if (string.IsNullOrWhiteSpace(userName))
            {
                res.Message = "用户名为空";
                return res;
            }

            string hivePath = Path.Combine(@"C:\Users", userName, "NTUSER.DAT");
            if (!File.Exists(hivePath))
            {
                res.Message = "找不到用户配置单元（可能从未登录过此账户）";
                return res;
            }

            // 已登录用户的 NTUSER.DAT 被独占，不能挂载
            if (IsUserLoggedOn(userName))
            {
                res.Message = "该用户正在登录，请先注销后再应用";
                return res;
            }

            string mountName = "SoulLink_" + userName;
            string mountKey = @"HKU\" + mountName;

            // 清理可能残留的挂载
            TryUnload(mountKey);

            try
            {
                var load = RunReg(new[] { "load", mountKey, hivePath });
                if (load.ExitCode != 0)
                {
                    res.Message = "挂载注册表失败：" + load.Output;
                    return res;
                }
            }
            catch (Exception ex)
            {
                res.Message = "挂载注册表异常：" + ex.Message;
                return res;
            }

            try
            {
                // 小组件
                WriteDword(mountKey + @"\SOFTWARE\Policies\Microsoft\Dsh",
                           "AllowNewsAndInterests", 0);
                // Edge 预启动 / 后台
                WriteDword(mountKey + @"\SOFTWARE\Policies\Microsoft\Edge",
                           "StartupBoostEnabled", 0);
                WriteDword(mountKey + @"\SOFTWARE\Policies\Microsoft\Edge",
                           "BackgroundModeEnabled", 0);
                // 手机连接（用户级开关）
                WriteDword(mountKey + @"\SOFTWARE\Policies\Microsoft\Windows\System",
                           "EnableMmx", 0);

                res.Ok = true;
                res.Message = "已关闭小组件 / Edge 预启动 / 手机连接的自动启动（下次登录生效）";
            }
            catch (Exception ex)
            {
                res.Message = "写入策略失败：" + ex.Message;
            }
            finally
            {
                // 必须卸除，否则配置单元一直挂在那里
                try { TryUnload(mountKey); } catch { }
            }

            return res;
        }

        /// <summary>撤销上面应用过的策略（恢复系统默认）。</summary>
        public static PolicyResult RevertStartupPolicy(string userName)
        {
            var res = new PolicyResult { UserName = userName };
            string hivePath = Path.Combine(@"C:\Users", userName, "NTUSER.DAT");
            if (!File.Exists(hivePath)) { res.Message = "找不到用户配置单元"; return res; }
            if (IsUserLoggedOn(userName)) { res.Message = "该用户正在登录，请先注销后再操作"; return res; }

            string mountKey = @"HKU\SoulLink_" + userName;
            TryUnload(mountKey);
            try
            {
                var load = RunReg(new[] { "load", mountKey, hivePath });
                if (load.ExitCode != 0) { res.Message = "挂载注册表失败：" + load.Output; return res; }
            }
            catch (Exception ex) { res.Message = "挂载注册表异常：" + ex.Message; return res; }

            try
            {
                DeleteValue(mountKey + @"\SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests");
                DeleteValue(mountKey + @"\SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled");
                DeleteValue(mountKey + @"\SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled");
                DeleteValue(mountKey + @"\SOFTWARE\Policies\Microsoft\Windows\System", "EnableMmx");
                res.Ok = true;
                res.Message = "已撤销策略，恢复系统默认";
            }
            catch (Exception ex) { res.Message = "撤销失败：" + ex.Message; }
            finally { try { TryUnload(mountKey); } catch { } }
            return res;
        }

        /// <summary>
        /// 该账户当前是否已登录（有活动会话）。
        ///
        /// ★ 判定依据：会话列表里出现该用户名且会话号 > 0。
        ///   用户已登录时 NTUSER.DAT 被独占，reg load 会失败，
        ///   所以提前拦住并给出明确提示，而不是让底层报错。
        /// ★ 判断本身失败时**返回 true**（保守）：宁可拒绝操作，
        ///   也不要在状态不明时去改别人的注册表。
        /// </summary>
        public static bool IsUserLoggedOn(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName)) return true;
            try
            {
                var sessions = SessionService.QueryAsync().GetAwaiter().GetResult();
                foreach (var s in sessions)
                {
                    if (s == null || s.Id <= 0) continue;
                    if (string.Equals(s.UserName, userName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            catch { return true; }
        }

        // ── 内部工具 ────────────────────────────────────────────────────

        private static void TryUnload(string mountKey)
        {
            try { RunReg(new[] { "unload", mountKey }); } catch { }
        }

        private static void WriteDword(string keyFullPath, string name, int value)
        {
            RunReg(new[] { "add", keyFullPath, "/v", name, "/t", "REG_DWORD", "/d", value.ToString(), "/f" });
        }

        private static void DeleteValue(string keyFullPath, string name)
        {
            RunReg(new[] { "delete", keyFullPath, "/v", name, "/f" });
        }

        private sealed class RegOut { public int ExitCode; public string Output = ""; }

        private static RegOut RunReg(string[] args)
        {
            var psi = new ProcessStartInfo("reg.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            var o = p?.StandardOutput.ReadToEnd() ?? "";
            var e = p?.StandardError.ReadToEnd() ?? "";
            p?.WaitForExit(8000);
            return new RegOut
            {
                ExitCode = p?.ExitCode ?? -1,
                Output = (o + " " + e).Trim()
            };
        }
    }
}
