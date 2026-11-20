using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace SoulLink.Controls
{
    /// <summary>
    /// 月莓星轨 / 云端棱镜。静态星野按尺寸和主题缓存，动画只更新轻量几何。
    /// 不拦截鼠标；隐藏、最小化和卸载时暂停，后台驻留托盘不会继续刷新。
    /// </summary>
    public sealed class AuroraBackdrop : FrameworkElement
    {
        private readonly DreamScene _scene = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private TimeSpan _last;
        private double _time = 3.2;
        private bool _running;
        private Window? _window;

        // ★★ 2.17.46：中/低档改用 DispatcherTimer，不再订阅 CompositionTarget.Rendering。★★
        //
        //   CompositionTarget.Rendering 是**每帧都回调**（60Hz → 每秒 60 次）。
        //   原来中/低档在回调里只做一件事：比较两个 TimeSpan，攒够 500ms/1000ms
        //   再 InvalidateVisual 一次 —— 也就是说 59/60 次回调纯属空转。
        //   而它同时会把整个合成器维持在「每帧都在推进」的状态。
        //   改用 DispatcherTimer（Background 优先级，不影响交互）后，
        //   低档每秒只有 1 次唤醒，高档仍走 Rendering 保证帧率平滑。
        private DispatcherTimer? _staticTimer;

        /// <summary>静止档的重绘节奏。见 RepaintInterval 的说明。</summary>
        private void StartStaticTimer(int tier)
        {
            var interval = RepaintInterval(tier);
            if (_staticTimer == null)
            {
                _staticTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
                _staticTimer.Tick += OnStaticTick;
            }
            else if (_staticTimer.Interval != interval) _staticTimer.Interval = interval;
            if (!_staticTimer.IsEnabled) _staticTimer.Start();
        }

        private void StopStaticTimer()
        {
            if (_staticTimer == null || !_staticTimer.IsEnabled) return;
            _staticTimer.Stop();
        }

        private void OnStaticTick(object? sender, EventArgs e)
        {
            if (!IsLoaded || !IsVisible) return;
            if (_window != null && (!_window.IsVisible || _window.WindowState == WindowState.Minimized)) return;
            InvalidateVisual();
        }

        public AuroraBackdrop()
        {
            IsHitTestVisible = false;
            Focusable = false;
            SnapsToDevicePixels = true;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            IsVisibleChanged += (_, _) => SyncAnimation();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _window = Window.GetWindow(this);
            if (_window != null)
            {
                _window.StateChanged += OnWindowActivityChanged;
                _window.IsVisibleChanged += OnWindowVisibilityChanged;

                // ★ 焦点事件仍然订阅。原因见 SyncAnimation 里的说明：
                //   实测「焦点变化触发的那次重绘」是把窗口外沿那圈刷干净的关键，
                //   去掉它白带会回来（2026-11-20 做过对照，见交接文档）。
                _window.Activated += OnWindowActivityChanged;
                _window.Deactivated += OnWindowActivityChanged;
            }
            SyncAnimation();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Stop();
            if (_window != null)
            {
                _window.StateChanged -= OnWindowActivityChanged;
                _window.IsVisibleChanged -= OnWindowVisibilityChanged;
                _window.Activated -= OnWindowActivityChanged;
                _window.Deactivated -= OnWindowActivityChanged;
            }
            _window = null;
        }

        private void OnWindowActivityChanged(object? sender, EventArgs e)
        {
            // 高档在失焦时要降为慢速重绘（见 OnFrame），重绘节奏取决于焦点。
            // SyncAnimation 只在 canRender 变化时才动驱动，焦点变化不会触发它，
            // 所以这里显式刷新一次。
            if (_running) InvalidateVisual();
            SyncAnimation();
        }

        private void OnWindowVisibilityChanged(object? sender, DependencyPropertyChangedEventArgs e) => SyncAnimation();

        /// <summary>
        /// ★★ 2.17.17 性能修复。★★
        ///
        /// 实测（本机，2026-11-20）：这个背景动画单独一项就占约 **61 个单核百分点**。
        /// 原因：每 33ms 触发一次 CompositionTarget.Rendering → InvalidateVisual →
        /// 全量重绘（3 条丝带 × 24 层描边 + 76 颗粒子 + 轨道系统 56 条刻度）。
        ///
        /// 现在的策略（见 SyncAnimation）：
        ///   ① 用户可在设置里关闭动画背景（AppSettings.AnimateBackdrop，默认开）；
        ///   ② 窗口失焦即暂停 —— 挂机是主要场景，用户不看的时候一帧都不画；
        ///   ③ 最小化、隐藏、卸载时照旧停止。
        /// 这样"看的时候有动画，挂机时零开销"。
        /// </summary>
        private void SyncAnimation()
        {
            // ★★ 2.17.18：订阅条件**不再看窗口是否激活**。★★
            //
            //   根因（2026-11-20 实测）：窗口外沿有一圈约 8px 的区域，
            //   只有在「整窗重绘」时才会被内容覆盖住。高画质因为每 33ms 全量重绘，
            //   那一圈每帧都被顺手刷成内容色，所以看不见；中/低档不重绘，
            //   于是露出残留色 —— 聚焦时 #B4B4B4 灰、失焦时 #F4F7FC 白，
            //   表现为「窗口内侧一圈粗框、缺口在底边和右边」。
            //
            //   ⚠ 第一版修法把 ownerForeground 也当作订阅前提，结果窗口一失焦
            //     重绘就停了，而白框恰恰是失焦时才最明显 —— 等于在最需要的时候
            //     把它关掉了。现在订阅只看「可见且非最小化」，与焦点无关。
            //
            //   动画本身仍然只在窗口激活时推进（见 OnFrame），省电的逻辑不变。
            bool canRender = IsLoaded && IsVisible && ActualWidth > 2 && ActualHeight > 2
                             && (_window == null || (_window.IsVisible && _window.WindowState != WindowState.Minimized));

            if (canRender == _running) return;
            _running = canRender;
            if (canRender)
            {
                _last = _clock.Elapsed;
                ApplyTierMode();
            }
            else
            {
                CompositionTarget.Rendering -= OnFrame;
                StopStaticTimer();
            }
        }

        /// <summary>
        /// 按当前档位选择驱动方式：高 → CompositionTarget.Rendering（每帧推进）；
        /// 中/低 → DispatcherTimer（慢速整窗重绘）。档位可能在运行中改变，所以要能来回切。
        ///
        /// ★ 2.17.46：原来中/低档也订阅 Rendering，每秒 60 次回调里 59 次是空转。
        /// </summary>
        private void ApplyTierMode()
        {
            int tier = SoulLink.App.Config?.Settings?.EffectTier ?? 1;
            bool animated = Models.AppSettings.EffectTierLevel.Backdrop(tier);
            if (animated)
            {
                StopStaticTimer();
                if (!_renderingSubscribed) { CompositionTarget.Rendering += OnFrame; _renderingSubscribed = true; }
            }
            else
            {
                if (_renderingSubscribed) { CompositionTarget.Rendering -= OnFrame; _renderingSubscribed = false; }
                StartStaticTimer(tier);
            }
        }

        private bool _renderingSubscribed;

        /// <summary>
        /// 非「高」档位的整窗重绘间隔。
        /// 只在窗口可见、非最小化时跑，所以挂机丢到托盘后仍然零开销。
        /// </summary>
        private static TimeSpan RepaintInterval(int tier)
            => TimeSpan.FromMilliseconds(tier >= Models.AppSettings.EffectTierLevel.Low ? 1000 : 500);

        /// <summary>
        /// 动画帧间隔。★ 2.17.46：原来写死 33ms（只服务「高」档），
        /// 现在取自档位定义，中档 = 100ms（10fps），低档 = 0（不播放）。
        /// </summary>
        private static TimeSpan AnimationInterval(int tier)
        {
            int ms = Models.AppSettings.EffectTierLevel.BackdropFrameMs(tier);
            return ms > 0 ? TimeSpan.FromMilliseconds(ms) : TimeSpan.FromMilliseconds(int.MaxValue);
        }

        /// <summary>档位切换后由 ThemeManager 调用，重新判定是否播放。</summary>
        public void RefreshAnimationState()
        {
            SyncAnimation();
            // 档位从「高」切到「中/低」（或反向）时，驱动方式要跟着换。
            if (_running) ApplyTierMode();
            InvalidateVisual();
        }

        private void Stop()
        {
            if (!_running) return;
            _running = false;
            CompositionTarget.Rendering -= OnFrame;
            _renderingSubscribed = false;
            StopStaticTimer();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);
            InvalidateVisual();
            SyncAnimation();
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            var now = _clock.Elapsed;

            int tier = SoulLink.App.Config?.Settings?.EffectTier ?? 1;
            bool animated = Models.AppSettings.EffectTierLevel.Backdrop(tier);
            bool active = _window == null || _window.IsActive;

            // 有动画的档位（高 30fps / 中 10fps）且窗口在前台：按档位节奏推进时间。
            // 失焦时降为低频重绘（挂机场景不烧 CPU）。
            if (animated && active)
            {
                if (now - _last < AnimationInterval(tier)) return;
                _time += Math.Min((now - _last).TotalSeconds, .08);
                _last = now;
                InvalidateVisual();
                return;
            }

            // ★ 只有「高档且窗口在前台」时 OnFrame 才会被订阅（见 ApplyTierMode），
            //   所以走到这里说明是「高档但窗口刚失焦」的过渡帧。
            //   ⚠ 不能在这里按 33ms 继续推进 —— 挂机时窗口正是失焦态，
            //     那会让最该省电的场景变成每秒 30 次全窗重绘。
            //   降为慢速重绘，只负责把窗口外沿那圈刷成内容色，等重新聚焦再恢复动画。
            if (now - _last < RepaintInterval(tier)) return;
            _last = now;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 2 || h < 2) return;

            // ★★ 2.17.46：静止档直接画预烘焙的整帧，跳过一切重建。★★
            //   实测依据（交接文档第 7 节）：用户 EffectTier=2（低档），
            //   动态背景早已关闭，CPU 却仍有 45–50%，最小化后掉到 11.7%
            //   —— 全是 WPF 渲染，且每次重绘都在把同一幅静止画面从零重建。
            int tier = SoulLink.App.Config?.Settings?.EffectTier ?? 1;
            bool light = !Services.ThemeManager.IsDark;
            if (!Models.AppSettings.EffectTierLevel.Backdrop(tier))
            {
                if (_scene.TryDrawStatic(dc, w, h, light)) return;
            }
            _scene.Render(dc, w, h, _time, light);
        }
    }
}