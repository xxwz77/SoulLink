using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Threading;

namespace SoulLink.Services
{
    /// <summary>
    /// 统一的节拍源（1 秒一跳）。
    ///
    /// ★★ 为什么要有它：★★
    ///   原来有 5 个各自独立的 DispatcherTimer，间隔都是 1 秒左右：
    ///     ScheduleRunner / RouteUpdateScheduler / BgiLogWatcher
    ///     / LaunchPage(卡片) / LaunchPage(会话,3s) / LobbyPage(5s)
    ///   它们互相独立地唤醒 Dispatcher，导致线程池**永远处于"有事要做"**的状态。
    ///   .NET 线程池在这种状态下会保留自旋线程而不立即阻塞，
    ///   实测空闲时 CPU 因此高达 25%~27%（未连任何 RDP）。
    ///
    ///   合并成一个 1 秒节拍后，每秒只唤醒一次，各订阅者用计数器分频。
    ///
    /// ★ 设计要点：
    ///   · 订阅者按"每 N 拍执行一次"注册，N=1 即每秒；
    ///   · 订阅可以随时取消（Dispose 返回的句柄）；
    ///   · 单个订阅者抛异常不影响其它订阅者；
    ///   · 节拍本身在 Dispatcher 线程上（与原来 DispatcherTimer 一致），
    ///     订阅者若要做重活，自己切到后台线程（原有代码就是这么做的）。
    /// </summary>
    public static partial class TickHub
    {
        private sealed class Sub
        {
            public string Name = "";
            public int Every;
            public Action Callback = () => { };
            public long Counter;
            public bool Removed;
            /// <summary>后台订阅：回调在线程池执行（而非 UI 线程）。</summary>
            public bool Background;
            /// <summary>后台订阅的防重入标记：1 = 上一拍回调还没跑完。</summary>
            public int Running;
        }

        private static readonly Lock Gate = new();
        private static readonly List<Sub> Subs = new();
        private static DispatcherTimer? _timer;
        private static Dispatcher? _dispatcher;
        private static long _ticks;
        private static bool _pumping;

        /// <summary>已跑过的拍数（诊断用）。</summary>
        public static long Ticks => Interlocked.Read(ref _ticks);
        /// <summary>当前订阅者数量（诊断用）。</summary>
        public static int SubscriberCount { get { lock (Gate) return Subs.Count; } }

        /// <summary>
        /// 注册一个周期任务。返回的句柄 Dispose 后即取消订阅。
        /// </summary>
        /// <param name="name">诊断用名称。</param>
        /// <param name="everySeconds">每几秒执行一次（≥1）。</param>
        public static IDisposable Register(string name, int everySeconds, Action callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            int every = Math.Max(1, everySeconds);

            var s = new Sub { Name = name, Every = every, Callback = callback, Background = false };
            lock (Gate)
            {
                Subs.Add(s);
                EnsureTimerOnDispatcher();
            }
            return new Handle(s);
        }

        /// <summary>
        /// 注册一个**后台**周期任务：回调在 .NET 线程池执行，不占用 UI 线程。
        ///
        /// ★ 为什么需要它：★★
        ///   Register() 的回调在 Dispatcher（UI）线程跑。BgiLogCenter / BgiLogWatcher /
        ///   ScheduleRunner / RouteUpdateScheduler 这四个订阅者内部是**同步文件 IO**
        ///   （读日志 / 写配置），挂在 UI 线程上就是「每秒一次的卡顿」——
        ///   用户点按钮、切页、拖窗口都要排队等这些 IO。
        ///
        ///   把它们迁到后台后，UI 线程每个节拍只剩「把回调投递到线程池」这一下，
        ///   纳秒级完成，不再被磁盘 IO 阻塞。
        ///
        /// ★ 语义与 Register() 完全一致：节拍统一、按 N 拍分频、单个订阅者
        ///   抛异常不影响其它订阅者。额外多一条：**防重入** —— 上一拍的回调
        ///   还没跑完时，本拍直接跳过（不排队堆积），等价于 UI 版 Pump 的 _pumping。
        ///
        /// ★ 回调里若要更新界面，必须自己 Dispatcher.BeginInvoke 回到 UI 线程，
        ///   并检查 Dispatcher.HasShutdownStarted。
        /// </summary>
        /// <param name="name">诊断用名称。</param>
        /// <param name="everySeconds">每几秒执行一次（≥1）。</param>
        public static IDisposable RegisterBackground(string name, int everySeconds, Action callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            int every = Math.Max(1, everySeconds);

            var s = new Sub { Name = name, Every = every, Callback = callback, Background = true };
            lock (Gate)
            {
                Subs.Add(s);
                EnsureTimerOnDispatcher();
            }
            return new Handle(s);
        }

        private sealed class Handle(Sub sub) : IDisposable
        {
            private readonly Sub _sub = sub;
            public void Dispose()
            {
                lock (Gate)
                {
                    _sub.Removed = true;
                    Subs.Remove(_sub);
                }
            }
        }

        private static void EnsureTimerOnDispatcher()
        {
            try
            {
                var disp = System.Windows.Application.Current?.Dispatcher;
                if (disp == null) return;

                if (_timer != null && ReferenceEquals(_dispatcher, disp)) return;

                // 换 Dispatcher（极少见）或首次创建
                _dispatcher = disp;
                _timer?.Stop();
                _timer = new DispatcherTimer(DispatcherPriority.Background, disp)
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _timer.Tick += (_, __) => Pump();
                _timer.Start();
            }
            catch { }
        }

        private static void Pump()
        {
            // 防重入：某一拍特别慢时不叠加
            if (_pumping) return;
            _pumping = true;
            try
            {
                Interlocked.Increment(ref _ticks);

                // 快照后遍历：订阅者在回调里增删订阅不会破坏遍历
                Sub[] list;
                lock (Gate) list = Subs.ToArray();

                foreach (var s in list)
                {
                    if (s.Removed) continue;
                    s.Counter++;
                    if (s.Counter % s.Every != 0) continue;

                    if (s.Background)
                    {
                        // ★ 后台订阅：把回调投递到线程池，UI 线程立刻返回。
                        //   防重入：上一拍还没跑完就跳过（后台回调可能慢于节拍）。
                        if (Interlocked.CompareExchange(ref s.Running, 1, 0) != 0) continue;
                        try
                        {
                            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                            {
                                try { s.Callback(); }
                                catch (Exception ex)
                                {
                                    try { App.AppendErrorLog("TickHub: 订阅者异常 " + s.Name, ex); }
                                    catch { }
                                }
                                finally { Interlocked.Exchange(ref s.Running, 0); }
                            });
                        }
                        catch { Interlocked.Exchange(ref s.Running, 0); }
                        continue;
                    }

                    try { s.Callback(); }
                    catch (Exception ex)
                    {
                        try
                        {
                            App.AppendErrorLog("TickHub: 订阅者异常 " + s.Name, ex);
                        }
                        catch { }
                    }
                }
            }
            finally { _pumping = false; }
        }

        /// <summary>停止节拍（程序退出时可选调用）。</summary>
        public static void Stop()
        {
            try
            {
                _timer?.Stop();
                _timer = null;
            }
            catch { }
        }
    }
}
