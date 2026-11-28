using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SoulLink.Models;
using System.Threading;

namespace SoulLink.Services
{
    /// <summary>
    /// 全局自动化调度器。窗口重建沿用同一个实例。
    /// </summary>
    public static class ScheduleRunnerHost
    {
        private static ScheduleRunner? _instance;
        private static readonly Lock _lock = new();

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
        private IDisposable? _tickSub;
        private readonly Func<AppConfig> _configGetter;
        private readonly HashSet<string> _running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Lock _sync = new();
        private bool _disposed;
        private readonly HashSet<string> _invalidLogged = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 扫描间隔（秒）。
        /// ★ 自动化流程的"定时触发"要精确到秒（容差 5 秒），
        ///   所以这里降到 1 秒扫描；开销很小（只遍历内存里的配置）。
        /// </summary>
        public const int TickSeconds = 1;

        public ScheduleRunner(Func<AppConfig> configGetter)
        {
            _configGetter = configGetter;
            // ★ 改用统一节拍（TickHub）。每秒只唤醒一次 Dispatcher，
            //   而不是每个定时器各唤醒一次。
            // ★ 后台订阅：Tick 内含 ConfigStore.Save（写盘）+ 遍历自动化流程，
            //   是同步 IO，挪到线程池；LogBus/App.AppendErrorLog 自身已做 UI 切换。
            _tickSub = TickHub.RegisterBackground("ScheduleRunner", TickSeconds, Tick);
        }

        public void Start()
        {
            // 订阅在构造函数里已注册，TickHub 自己管理节拍。
        }

        public void Stop()
        {
            try { _tickSub?.Dispose(); _tickSub = null; } catch { }
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
            foreach (var f in cfg.Automations) { f.EnsurePlcModel(); f.Disarm(); }
        }

        private void TickAutomations(AppConfig cfg)
        {
            try
            {
                if (cfg.Automations == null || cfg.Automations.Count == 0) return;

                foreach (var flow in cfg.Automations.ToList())
                {
                    flow.EnsurePlcModel();
                    if (!flow.Enabled || flow.Paused) continue;

                    string id = "flow:" + flow.Id;
                    lock (_sync) { if (_running.Contains(id)) continue; }

                    lock (_sync) { _running.Add(id); }

                    _ = Task.Run(async () =>
                    {
                        // ★ T12：任务执行期间跳过工作集回收（但推进静置计时），
                        //   结束后从当前时刻重新计 10 秒静置再回收。
                        WorkingSetTrim.BeginTask();
                        try
                        {
                            // 使用状态中心当前快照，不为每个流程再跑一次 query user。
                            int sid = AccountStateCenter.Get(flow.AccountId)?.SessionId ?? 0;

                            // ★ 运行前校验：不合法的流程一律不执行
                            //   （用户要求：可以保存但有提示，但绝对不能运行）
                            var vr = FlowValidator.Validate(flow, cfg);
                            if (vr.HasError)
                            {
                                if (_invalidLogged.Add(flow.Id))
                                    LogBus.Warn("自动化「" + flow.DisplayName + "」结构不完整，已跳过", vr.ToText());
                                return;
                            }

                            var result = await FlowRunner.RunAsync(flow, sid, cfg, manual: false)
                                                       .ConfigureAwait(false);

                            _invalidLogged.Remove(flow.Id);
                            if (result.Triggered)
                            {
                                LogBus.Write(result.Passed ? LogLevel.Ok : LogLevel.Info,
                                    "自动化「" + flow.DisplayName + "」" + (result.Passed ? "已执行" : "条件未通过"),
                                    result.Message + "\n" + string.Join("\n", result.Steps));

                                // FlowRunner 按通断/OUT/END 状态差异保存，不在每次扫描写全配置。
                            }
                        }
                        catch (Exception ex)
                        {
                            LogBus.Error("自动化「" + flow.DisplayName + "」异常：" + ex.Message);
                        }
                        finally
                        {
                            lock (_sync) { _running.Remove(id); }
                            WorkingSetTrim.EndTask();
                        }
                    });
                }
            }
            catch (Exception ex) { LogBus.Error("自动化流程调度异常", ex.Message); }
        }

        private void Tick()
        {
            if (_disposed) return;
            try
            {
                var cfg = _configGetter();
                if (cfg == null) return;
                CleanupNonPersisted(cfg);
                TickAutomations(cfg);
            }
            catch (Exception ex)
            {
                LogBus.Error("定时调度异常：" + ex.Message);
            }
        }
    }
}
