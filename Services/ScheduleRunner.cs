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

        /// <summary>扫描间隔（秒）。5 秒对"精确到秒"够用。</summary>
        public const int TickSeconds = 5;

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

        /// <summary>一轮扫描：找出到点该执行的动作。</summary>
        private void Tick()
        {
            if (_disposed) return;
            try
            {
                var cfg = _configGetter();
                if (cfg == null) return;
                var now = DateTime.Now;

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
