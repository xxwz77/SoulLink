using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

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

        private void OnWindowActivityChanged(object? sender, EventArgs e) => SyncAnimation();

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
                CompositionTarget.Rendering += OnFrame;
            }
            else CompositionTarget.Rendering -= OnFrame;
        }

        /// <summary>
        /// 非「高」档位的整窗重绘间隔。
        /// 只在窗口可见、非最小化时跑，所以挂机丢到托盘后仍然零开销。
        /// </summary>
        private static TimeSpan RepaintInterval(int tier)
            => TimeSpan.FromMilliseconds(tier >= Models.AppSettings.EffectTierLevel.Low ? 1000 : 500);

        /// <summary>档位切换后由 ThemeManager 调用，重新判定是否播放。</summary>
        public void RefreshAnimationState() => SyncAnimation();

        private void Stop()
        {
            if (!_running) return;
            _running = false;
            CompositionTarget.Rendering -= OnFrame;
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

            // 「高」档且窗口在前台：33ms 推进动画时间，画面是动的。
            // 失焦时降为低频重绘（挂机场景不烧 CPU）。
            if (animated && active)
            {
                if (now - _last < TimeSpan.FromMilliseconds(33)) return;
                _time += Math.Min((now - _last).TotalSeconds, .08);
                _last = now;
                InvalidateVisual();
                return;
            }

            // 「中/低」档：不推进 _time（画面静止），只按慢速节奏整窗重绘一次，
            // 把窗口外沿那圈没被覆盖的区域刷成内容色。
            // 失焦时同样要刷 —— 那圈区域正是在失焦态最明显。
            if (now - _last < RepaintInterval(tier)) return;
            _last = now;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 2 || h < 2) return;
            _scene.Render(dc, w, h, _time, !Services.ThemeManager.IsDark);
        }
    }
}