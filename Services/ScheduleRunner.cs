using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>
    /// 进程内定时调度器。
    ///
    /// 适用场景：动作的定时计划没有勾选"写入 Windows 计划任务"，
    /// 或勾了但当前 SoulLink 正在运行 —— 由本调度器负责按时触发。
    ///
    /// 设计要点：
    ///   1. 每 5 秒扫一遍所有动作，判断下次执行时间是否已到。
    ///   2. 触发后立刻 MarkRunCompleted 累计 RunCount，避免重复触发。
    ///   3. 用 _running 集合防重入。
    ///   4. 暂停的动作直接跳过。
    ///   5. 执行都走 ActionService.ExecuteAsync，只影响目标账号。
    /// </summary>
    /// <summary>
    /// 全局单例持有者：保证整个进程只有一个调度器，
    /// 无论窗口重建多少次都不会重复计时。
    /// </summary>
    public static class ScheduleRunnerHost
    {
        private static ScheduleRunner? _instance;
        private static readonly object _lock = new object();

        /// <summary>
        /// 全局取消：应用退出时置位。
        /// 正在跑的流程会尽快中止，不再往下执行节点。
        /// </summary>
        public static readonly System.Threading.CancellationTokenSource ShutdownCts
            = new System.Threading.CancellationTokenSource();

        public static bool IsShuttingDown => ShutdownCts.IsCancellationRequested;

        public static void StartOnce(Func<AppConfig> getter)
        {
            lock (_lock)
            {
                if (_instance != null) { _instance.Start(); return; }
                _instance = new ScheduleRunner(getter);
                _instance.Start();
            }
        }

        public static void Shutdown()
        {
            lock (_lock)
            {
                try { ShutdownCts.Cancel(); } catch { }   // 中断正在执行的流程
                try { _instance?.Dispose(); } catch { }
                _instance = null;
            }
        }
    }

    public sealed class ScheduleRunner : IDisposable
    {
        private readonly System.Windows.Threading.DispatcherTimer _timer;
        private readonly Func<AppConfig> _configGetter;
        private readonly HashSet<string> _running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _sync = new object();
        private bool _disposed;

        /// <summary>
        /// 扫描间隔（秒）。
        /// ★ 自动化流程的"定时触发"要精确到秒（容差 5 秒），
        ///   所以这里降到 1 秒扫描；开销很小（只遍历内存里的配置）。
        /// </summary>
        public const int TickSeconds = 1;

        public ScheduleRunner(Func<AppConfig> configGetter)
        {
            _configGetter = configGetter;
            _timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(TickSeconds)
            };
            _timer.Tick += (_, __) => Tick();
        }

        public void Start()
        {
            try { if (!_timer.IsEnabled) _timer.Start(); } catch { }
        }

        public void Stop()
        {
            try { _timer.Stop(); } catch { }
        }

        public void Dispose()
        {
            _disposed = true;
            Stop();
        }

        /// <summary>
        /// 启动时清理：未开启持久化的流程，条件状态全部复位。
        /// 这样"关掉持久化"的流程每次重启都从头判断。
        /// </summary>
        private static bool _startupCleaned;

        private static void CleanupNonPersisted(AppConfig cfg)
        {
            if (_startupCleaned) return;
            _startupCleaned = true;

            try
            {
                bool changed = false;
                foreach (var f in cfg.Automations)
                {
                    if (f.PersistLatched) continue;
                    if (!f.Active && (f.Latched == null || f.Latched.Count == 0)) continue;
                    f.Disarm();
                    changed = true;
                }
                if (changed) ConfigStore.Save(cfg);
            }
            catch { }
        }

        /// <summary>
        /// 检查所有自动化流程，满足触发器的就执行。
        ///
        /// ★ 与"定时动作"的区别：
        ///   · 定时动作只认时间；
        ///   · 自动化流程可以按 会话上线 / BGI空闲 / 日志匹配 / 手动 触发，
        ///     并且先过条件（AND/OR）再执行动作链。
        /// </summary>
        private void TickAutomations(AppConfig cfg, DateTime now)
        {
            try
            {
                if (cfg.Automations == null || cfg.Automations.Count == 0) return;

                foreach (var flow in cfg.Automations.ToList())
                {
                    if (!flow.Enabled || flow.Paused) continue;

                    string id = "flow:" + flow.Id;
                    lock (_sync) { if (_running.Contains(id)) continue; }

                    lock (_sync) { _running.Add(id); }

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // 找到该流程绑定账号的会话号
                            int sid = 0;
                            string user = "";
                            foreach (var a in cfg.Accounts)
                                if (a.Id == flow.AccountId) { user = a.UserName; break; }

                            if (user.Length > 0)
                            {
                                var sessions = await SessionService.QueryAsync().ConfigureAwait(false);
                                var s = SessionService.Find(sessions, user);
                                if (s != null) sid = s.Id;
                            }

                            // ★ 运行前校验：不合法的流程一律不执行
                            //   （用户要求：可以保存但有提示，但绝对不能运行）
                            var vr = FlowValidator.Validate(flow, cfg);
                            if (vr.HasError)
                            {
                                LogBus.Warn("自动化「" + flow.DisplayName + "」结构不完整，已跳过",
                                    vr.ToText());
                                return;
                            }

                            var result = await FlowRunner.RunAsync(flow, sid, cfg, manual: false)
                                                       .ConfigureAwait(false);

                            if (result.Triggered)
                            {
                                LogBus.Write(result.Passed ? LogLevel.Ok : LogLevel.Info,
                                    "自动化「" + flow.DisplayName + "」" + (result.Passed ? "已执行" : "条件未通过"),
                                    result.Message + "\n" + string.Join("\n", result.Steps));

                                // ★ 条件锁存也要落盘：
                                //   PersistLatched=true  → 保存（重启后保持满足状态）
                                //   PersistLatched=false → 不保存（只活在内存里）
                                if (flow.PersistLatched || result.Passed)
                                    ConfigStore.Save(cfg);
                            }
                        }
                        catch (Exception ex)
                        {
                            LogBus.Error("自动化「" + flow.DisplayName + "」异常：" + ex.Message);
                        }
                        finally
                        {
                            lock (_sync) { _running.Remove(id); }
                        }
                    });
                }
            }
            catch { }
        }

        /// <summary>一轮扫描：找出到点该执行的动作 + 该触发的自动化流程。</summary>
        private void Tick()
        {
            if (_disposed) return;
            try
            {
                var cfg = _configGetter();
                if (cfg == null) return;
                var now = DateTime.Now;

                CleanupNonPersisted(cfg);
                TickAutomations(cfg, now);

                foreach (var action in cfg.Actions.ToList())
                {
                    var spec = action.Schedule;
                    if (spec == null || !spec.Enabled || spec.Paused) continue;
                    // 勾了"写入计划任务"的交给系统，进程内不抢着执行
                    // UseWindowsTask 已废弃，永远为 false；保留判断以防旧配置

                    string id = action.Id ?? action.Name;
                    lock (_sync) { if (_running.Contains(id)) continue; }

                    var next = ScheduleCalculator.NextRun(spec, now);
                    if (next == null) continue;
                    if (now < next.Value.AddSeconds(-2)) continue;

                    lock (_sync) { _running.Add(id); }

                    DateTime? following = null;
                    try
                    {
                        following = ScheduleCalculator.MarkRunCompleted(spec, now);
                        ConfigStore.Save(cfg);
                    }
                    catch { }

                    LogBus.Info("定时触发「" + action.Name + "」",
                        "计划：" + spec.Display
                        + (following != null ? "　下次：" + following.Value.ToString("MM-dd HH:mm:ss") : ""));

                    var act = action;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var result = await ActionService.ExecuteAsync(act, cfg).ConfigureAwait(false);
                            LogBus.Write(result.Ok ? LogLevel.Ok : LogLevel.Error,
                                "定时执行「" + act.Name + "」" + (result.Ok ? "成功" : "失败"),
                                result.Message);
                        }
                        catch (Exception ex)
                        {
                            LogBus.Error("定时执行「" + act.Name + "」异常：" + ex.Message);
                        }
                        finally
                        {
                            lock (_sync) { _running.Remove(id); }
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                LogBus.Error("定时调度异常：" + ex.Message);
            }
        }
    }
}
