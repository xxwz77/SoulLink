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

        /// <summary>
        /// 静止档的驱动。
        ///
        /// ★★ 3.0.1：静止档**完全不启动周期定时器**。★★
        ///
        ///  原来（2.17.46 起）低档每 1000ms 仍会 InvalidateVisual 一次，
        ///  目的是「刷窗口外沿那圈 8px 残留色」。
        ///  实测这个每秒 1 次的重绘要花掉 **60+ 个单核百分点** ——
        ///  因为每次都要把整幅矢量场景重新光栅化。
        ///
        ///  现在：静止画面与时间无关，只在「需要时」画一次即可。
        ///  需要重绘的时机全部已有明确来源：
        ///    · 尺寸变化 → OnRenderSizeChanged → InvalidateVisual
        ///    · 主题切换 → ThemeManager.RefreshAnimationState → InvalidateVisual
        ///    · 窗口激活/失焦 → OnWindowActivityChanged → InvalidateVisual
        ///    · 可见性变化 → IsVisibleChanged → SyncAnimation
        ///  所以周期定时器是多余的，直接不建。
        ///
        ///  ⚠ 如果将来发现「窗口外沿残留色」重新出现，说明某个触发点漏了，
        ///    应该去补那个触发点，而不是把定时器加回来。
        /// </summary>
        private void StartStaticTimer(int tier)
        {
            // 静止档不需要周期性重绘 —— 见上面的说明。
            StopStaticTimer();
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
                // ★ 3.0.1：重新开始渲染时把目标时刻也对齐到"现在"，
                //   否则窗口从最小化恢复后，_nextFrame 还停在很久以前，
                //   会导致第一帧立刻推进（画面跳一下）。
                _nextFrame = _last;
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

                // ★★ 3.0.1：动画档按档位降低单帧复杂度。★★
                //   实测中档（10fps）比高档（30fps）更贵（76% vs 28%），
                //   因为 WPF 只在连续 60Hz 渲染时才做帧间增量；
                //   10fps 每帧都是整幅冷栅格化。
                //   降帧率救不了，就降单帧成本：中档用精简场景。
                _scene.Detail = tier == Models.AppSettings.EffectTierLevel.Medium ? 1 : 0;

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

        // ★ 3.0.1：移除了 AnimationInterval()。
        //   帧节奏改由 OnFrame 里的 _nextFrame 目标时刻驱动，
        //   它同时支持「限帧档位（中档 30fps）」和「每帧推进（高档 60fps）」，
        //   旧的 TimeSpan 封装已经没有调用点。

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

        /// <summary>
        /// 下一次允许推进的时间点。
        ///
        /// ★★ 3.0.1 修复帧节奏抖动：原来用 now - _last < interval 判断。★★
        ///
        ///   以 60Hz 显示器 + 30fps 目标为例，回调实际间隔 16.67ms：
        ///     第1帧 16.67 &lt; 33 → 跳过
        ///     第2帧 33.34 ≥ 33 → 推进（间隔 33.34ms）
        ///     第3帧 16.67 &lt; 33 → 跳过
        ///     第4帧 33.34 ≥ 33 → 推进
        ///   看着是 30fps，但**每次都要等 2 帧才动**；一旦某帧迟到（系统卡顿），
        ///   节奏就被打乱，出现「该动没动、下一帧补上」的顿挫。
        ///   在 144Hz 屏上更糟：6.94ms × 5 = 34.7ms，永远对不齐 33ms。
        ///
        ///   改成维护「下一个目标时刻」，每帧只判断 now >= 目标时刻，
        ///   并用目标时刻累加（而不是用 now）推进，误差不会累积。
        /// </summary>
        private TimeSpan _nextFrame;

        private void OnFrame(object? sender, EventArgs e)
        {
            var now = _clock.Elapsed;

            int tier = SoulLink.App.Config?.Settings?.EffectTier ?? 1;
            bool animated = Models.AppSettings.EffectTierLevel.Backdrop(tier);
            bool active = _window == null || _window.IsActive;

            // 有动画的档位且窗口在前台：按档位节奏推进时间。
            // 失焦时降为低频重绘（挂机场景不烧 CPU）。
            if (animated && active)
            {
                int intervalMs = Models.AppSettings.EffectTierLevel.BackdropFrameMs(tier);

                if (intervalMs > 0)
                {
                    // 限帧档位（中档 30fps）：等到目标时刻才推进
                    if (now < _nextFrame) return;

                    // ★ 用「目标时刻」而不是 now 累加，避免长期漂移；
                    //   但如果已经落后超过一个完整间隔（比如窗口刚恢复、系统卡顿），
                    //   就从 now 重新起算，不做补偿性跳帧 —— 补偿会让画面猛跳一下。
                    var next = _nextFrame + TimeSpan.FromMilliseconds(intervalMs);
                    _nextFrame = (next > now)
                        ? next
                        : now + TimeSpan.FromMilliseconds(intervalMs);
                }
                // intervalMs == 0（高档）：每帧都推进，跟随 CompositionTarget.Rendering

                // ★ 单帧推进上限 0.08s：窗口被遮挡/系统卡顿后恢复时，
                //   不能让 _time 一次跳很远（那会让画面瞬移）。
                _time += Math.Min((now - _last).TotalSeconds, .08);
                _last = now;
                InvalidateVisual();
                return;
            }

            // ★ 走到这里说明「有动画的档位但窗口失焦」。
            //   ⚠ 不能在这里按动画节奏继续推进 —— 挂机时窗口正是失焦态，
            //     那会让最该省电的场景变成每秒几十次全窗重绘。
            //   降为慢速重绘，只负责把窗口外沿那圈刷成内容色，等重新聚焦再恢复动画。
            if (now - _last < RepaintInterval(tier)) return;
            _last = now;
            _nextFrame = now;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 2 || h < 2) return;

            // ★★ 3.0.1：静止档改为「位图缓存 + 完全不重绘」★★
            //
            //  实测（2026-11-21）：低档（静止）仍有 92.8% 单核，
            //  把背景换成空 Canvas 后掉到 24.5% —— 背景自己吃 68 个百分点。
            //
            //  根因是 dc.DrawDrawing(冻结的 DrawingGroup) 每次都要整幅
            //  重新光栅化；冻结只省“构建”不省“栅格化”。
            //
            //  现在两级降本：
            //    ① OnRender 走位图（DrawImage = GPU 纹理绘制，见 DreamScene）
            //    ② 静止档根本不再周期性 InvalidateVisual（见 StartStaticTimer 注释）
            int tier = SoulLink.App.Config?.Settings?.EffectTier ?? 1;
            bool light = !Services.ThemeManager.IsDark;
            if (!Models.AppSettings.EffectTierLevel.Backdrop(tier))
            {
                _scene.AttachDpiProbe(this);
                if (_scene.TryDrawStaticBitmap(dc, w, h, light)) return;

                // 位图路径不可用（超大画布/显存不足）时退回矢量绘制
                if (_scene.TryDrawStatic(dc, w, h, light)) return;
            }
            _scene.Render(dc, w, h, _time, light);
        }

        /// <summary>
        /// 主题或尺寸变化后清掉静止帧缓存。
        /// ★ 3.0.1：位图缓存按尺寸/主题失效，但尺寸变化会走 OnRenderSizeChanged，
        ///   主题变化由 ThemeManager.RefreshAnimationState 调进来。
        /// </summary>
        public void InvalidateStaticFrame()
        {
            _scene.InvalidateStaticBitmap();
            InvalidateVisual();
        }
    }
}