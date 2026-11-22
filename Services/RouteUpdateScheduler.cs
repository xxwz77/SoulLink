using System;
using System.Threading;
using System.Threading.Tasks;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>
    /// 路线更新的定时调度器。
    ///
    /// ★ 与动作调度器（ScheduleRunner）分开：
    ///   动作调度管的是「执行程序」，这里管的是「替换路线文件夹」，
    ///   两者失败互不影响，日志也能分开看。
    ///
    /// ★ 复用 ScheduleCalculator 与 ScheduleSpec —— 项目里已有的定时模型，
    ///   不再另造一套时间计算。
    ///
    /// 设计要点：
    ///   1. 每 5 秒扫一次；到点才跑。
    ///   2. 触发后立刻 MarkRunCompleted + 存盘，避免同一时刻重复触发。
    ///   3. _running 防重入：上一轮还没跑完就跳过本轮（下载可能很慢）。
    ///   4. 只依赖 App.Config.Settings.RouteUpdate，不额外持有状态。
    /// </summary>
    public static class RouteUpdateSchedulerHost
    {
        private static RouteUpdateScheduler? _instance;
        private static readonly Lock Lock = new();

        public static void StartOnce()
        {
            lock (Lock)
            {
                if (_instance != null) { _instance.Start(); return; }
                _instance = new RouteUpdateScheduler();
                _instance.Start();
            }
        }

        public static void Shutdown()
        {
            lock (Lock)
            {
                try { _instance?.Dispose(); } catch { }
                _instance = null;
            }
        }

        /// <summary>设置变化后让调度器立刻重新读一遍（目前每次扫描都现读配置，这里只做唤醒）。</summary>
        public static void Wake() => _instance?.Start();
    }

    public sealed class RouteUpdateScheduler : IDisposable
    {
        /// <summary>扫描间隔（秒）。路线更新不需要秒级精度，5 秒足够。</summary>
        public const int TickSeconds = 5;

        private IDisposable? _tickSub;
        private readonly Lock _sync = new();
        private bool _running;
        private bool _disposed;
        private bool _startupCleaned;

        public RouteUpdateScheduler()
        {
            // ★ 改用统一节拍（TickHub），不再自建 DispatcherTimer。
            //   原来每个 1 秒级定时器都独立唤醒 Dispatcher，线程池因此长期自旋。
            // ★ 后台订阅：Tick 内含 ConfigStore.Save（写盘），是同步 IO，
            //   挪到线程池；LogBus/App.AppendErrorLog 自身已做 UI 切换。
            _tickSub = TickHub.RegisterBackground("RouteUpdateScheduler", TickSeconds, Tick);
        }

        public void Start()
        {
            // 订阅在构造函数里已注册；TickHub 统一驱动节拍。
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

        private void Tick()
        {
            if (_disposed) return;

            try
            {
                var cfg = App.Config;
                var settings = cfg?.Settings?.RouteUpdate;
                if (settings == null) return;

                var spec = settings.Schedule;
                if (spec == null) return;

                // 未启用 / 已暂停 / 没有目标 → 什么都不做
                if (!spec.Enabled || spec.Paused) return;
                if (settings.ActiveTargets().Count == 0) return;

                // 上一轮还在跑（可能在下载大包）→ 跳过本轮，不排队堆积
                lock (_sync) { if (_running) return; }

                var now = DateTime.Now;

                // ★ Interval 不能直接用 ScheduleCalculator.NextRun：
                //   它在 next <= now 时会返回 now + interval（那是「执行完成后延时」的语义），
                //   于是每次都算成未来，调度器永远追不上 —— 2.15.0 开发期实测「启用后永不触发」。
                //   这里自己按「距上次执行满 N 秒」判断，触发才推进基准。
                if (spec.Kind == ScheduleKind.Interval)
                {
                    int iv = Math.Max(1, spec.IntervalSeconds)
                           + Math.Max(0, spec.PostRunDelaySeconds);

                    if (spec.LastRunAt == null)
                    {
                        spec.LastRunAt = now;
                        try { ConfigStore.Save(cfg); } catch { }
                        LogBus.Info("路线更新定时已启动",
                            "每 " + Math.Max(1, spec.IntervalSeconds / 3600) + " 小时，首次 "
                            + now.AddSeconds(iv).ToString("MM-dd HH:mm") + " 执行");
                        return;
                    }

                    if (now < spec.LastRunAt.Value.AddSeconds(iv)) return;
                }
                else
                {
                    var next = ScheduleCalculator.NextRun(spec, now);
                    if (next == null) return;
                    if (now < next.Value.AddSeconds(-2)) return;
                }

                // 先记账再执行，保证同一时刻只触发一次
                DateTime? following = null;
                try
                {
                    following = ScheduleCalculator.MarkRunCompleted(spec, now);
                    settings.LastRunAt = now;
                    ConfigStore.Save(cfg);
                }
                catch (Exception ex)
                {
                    App.AppendErrorLog("RouteUpdateScheduler.MarkRun", ex);
                }

                lock (_sync) { _running = true; }

                LogBus.Info("定时触发「路线更新」",
                    "计划：" + spec.Display
                    + (following != null ? "　下次：" + following.Value.ToString("MM-dd HH:mm:ss") : ""));

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var result = await RouteUpdater.RunAsync(
                            settings,
                            line => LogBus.Info("路线更新", line),
                            progress: null,
                            CancellationToken.None).ConfigureAwait(false);

                        settings.LastResult = result.Message;
                        try { ConfigStore.Save(cfg); } catch { }

                        LogBus.Write(result.Ok ? LogLevel.Ok : LogLevel.Error,
                            "定时「路线更新」" + (result.Ok ? "成功" : "失败"),
                            result.Message);
                    }
                    catch (Exception ex)
                    {
                        settings.LastResult = "异常：" + ex.Message;
                        try { ConfigStore.Save(cfg); } catch { }
                        LogBus.Error("定时「路线更新」异常：" + ex.Message);
                    }
                    finally
                    {
                        lock (_sync) { _running = false; }
                    }
                });
            }
            catch (Exception ex)
            {
                if (!_startupCleaned) { _startupCleaned = true; App.AppendErrorLog("RouteUpdateScheduler.Tick", ex); }
            }
        }
    }
}
