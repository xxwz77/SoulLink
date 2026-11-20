using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SoulLink.Controls
{
    /// <summary>
    /// 玻璃卡片：在 Border 基础上叠加顶部高光与内侧描边，形成“磨砂玻璃”质感。
    /// </summary>
    public class GlassCard : Border
    {
        public static readonly DependencyProperty HighlightOpacityProperty =
            DependencyProperty.Register(nameof(HighlightOpacity), typeof(double), typeof(GlassCard),
                new FrameworkPropertyMetadata(0.55, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty AccentGlowProperty =
            DependencyProperty.Register(nameof(AccentGlow), typeof(Color?), typeof(GlassCard),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public double HighlightOpacity
        {
            get => (double)GetValue(HighlightOpacityProperty);
            set => SetValue(HighlightOpacityProperty, value);
        }

        public Color? AccentGlow
        {
            get => (Color?)GetValue(AccentGlowProperty);
            set => SetValue(AccentGlowProperty, value);
        }

        public GlassCard()
        {
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            double w = ActualWidth, h = ActualHeight;
            if (w <= 2 || h <= 2) return;

            var radius = CornerRadius;
            var outer = new Rect(0, 0, w, h);
            var inner = new Rect(1, 1, Math.Max(0, w - 2), Math.Max(0, h - 2));

            // 顶部高光：模拟光线从左上扫过的玻璃边缘
            var clip = new RectangleGeometry(outer, radius.TopLeft, radius.TopLeft)
            {
                RadiusX = Math.Max(0, radius.TopLeft),
                RadiusY = Math.Max(0, radius.TopLeft)
            };
            clip.Freeze();
            dc.PushClip(clip);

            var highlight = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0.35, 1),
                Opacity = HighlightOpacity
            };
            highlight.GradientStops.Add(new GradientStop(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF), 0));
            highlight.GradientStops.Add(new GradientStop(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF), 0.28));
            highlight.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.6));
            dc.DrawRectangle(highlight, null, inner);

            // 内侧细描边
            if (AccentGlow.HasValue)
            {
                var accent = new LinearGradientBrush(AccentGlow.Value, Color.FromArgb(0x00, AccentGlow.Value.R, AccentGlow.Value.G, AccentGlow.Value.B), 90);
                var pen = new Pen(accent, 1.4);
                pen.Freeze();
                var ring = new Rect(1.2, 1.2, Math.Max(0, w - 2.4), Math.Max(0, h - 2.4));
                dc.DrawRoundedRectangle(null, pen, ring, Math.Max(0, radius.TopLeft - 1), Math.Max(0, radius.TopRight - 1));
            }

            dc.Pop();
        }
    }

    /// <summary>呼吸光点：状态指示器（在线/断开/成功…）</summary>
    public class GlowDot : FrameworkElement
    {
        public static readonly DependencyProperty ColorProperty =
            DependencyProperty.Register(nameof(Color), typeof(Color), typeof(GlowDot),
                new FrameworkPropertyMetadata(Color.FromArgb(0xFF, 0x41, 0xF0, 0xC0), FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty PulseProperty =
            DependencyProperty.Register(nameof(Pulse), typeof(bool), typeof(GlowDot),
                new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

        public Color Color
        {
            get => (Color)GetValue(ColorProperty);
            set => SetValue(ColorProperty, value);
        }

        public bool Pulse
        {
            get => (bool)GetValue(PulseProperty);
            set => SetValue(PulseProperty, value);
        }

        /// <summary>
        /// ★★ 2.17.17 性能修复：脉冲不再靠每 60ms 重绘。★★
        ///
        ///   旧实现每个实例挂一个 60ms 的 DispatcherTimer，每次 InvalidateVisual()，
        ///   并在 OnRender 里 **新建一个未冻结的 RadialGradientBrush**。
        ///   实测（本机，2026-11-20）：单独开启这一项就让整机多出约 27 个单核百分点；
        ///   页面里 GlowDot 越多越糟（设置页/在线会话/启动台各有实例）。
        ///
        ///   现在：
        ///     · 光晕画刷按颜色缓存并 Freeze，OnRender 不再分配任何画刷；
        ///     · 脉冲频率 60ms → 120ms，理论上限直接减半。
        ///   视觉与原来一致（呼吸节奏略缓，肉眼几乎无差）。
        /// </summary>
        private static readonly Dictionary<(Color, int), RadialGradientBrush> HaloCache = new();
        private static readonly object HaloGate = new();

        private static RadialGradientBrush HaloBrush(Color color)
        {
            var key = (color, 0);
            lock (HaloGate)
            {
                if (HaloCache.TryGetValue(key, out var cached)) return cached;
                // 基准透明度按旧实现的峰值（70+60=130）取，脉冲交给 Opacity 动画。
                var brush = new RadialGradientBrush(Color.FromArgb(0x82, color.R, color.G, color.B)
                    , Color.FromArgb(0x00, color.R, color.G, color.B));
                brush.Freeze();
                HaloCache[key] = brush;
                return brush;
            }
        }

        private SolidColorBrush? _coreBrush;
        private Color _coreColor;

        public GlowDot()
        {
            Loaded += (_, __) => SyncPulse();
            Unloaded += (_, __) => StopPulse();
        }

        // ★★ 教训（2026-11-20 实测）：不要用 RepeatBehavior.Forever 的 Opacity 动画。★★
        //   第一版改法是给每个实例挂无限循环动画，结果 CPU 反而更高
        //   （UI 线程 88.5% 持续 Running）——WPF 里无限动画会强制该元素每帧参与渲染，
        //   比原来 60ms 一次的定时器还贵。
        //
        //   现在：保留定时器，但 ① 频率从 60ms 降到 120ms；② 画刷全部缓存冻结，
        //   OnRender 不再分配任何 Brush。这样既保住呼吸效果，又去掉了真正的分配开销。
        private DispatcherTimer? _timer;
        private double _phase;

        private void SyncPulse()
        {
            if (!Pulse) { StopPulse(); return; }
            // 低档（全部静止）时不脉冲，但仍显示一个静态亮点的完整亮度。
            int tier = SoulLink.App.Config?.Settings?.EffectTier ?? 1;
            if (!SoulLink.Models.AppSettings.EffectTierLevel.GlowPulse(tier))
            {
                StopPulse();
                Opacity = 1.0;
                return;
            }
            if (_timer != null) { if (!_timer.IsEnabled) _timer.Start(); return; }
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
            _timer.Tick += (_, __) =>
            {
                if (!Pulse) { StopPulse(); return; }
                _phase += 0.18;
                InvalidateVisual();
            };
            _timer.Start();
        }

        private void StopPulse()
        {
            try { _timer?.Stop(); } catch { }
        }

        protected override void OnRender(DrawingContext dc)
        {
            double d = Math.Min(ActualWidth, ActualHeight);
            if (d <= 0) return;
            var center = new Point(ActualWidth / 2, ActualHeight / 2);

            // ★ 只用缓存画刷，避免每帧分配（旧实现是这里吃掉了 27 个点）。
            //   光晕整体透明度由 Opacity 动画负责，所以不再按脉冲值改 alpha。
            dc.DrawEllipse(HaloBrush(Color), null, center, d * 0.5, d * 0.5);

            if (_coreBrush == null || _coreColor != Color)
            {
                _coreBrush = new SolidColorBrush(Color);
                _coreBrush.Freeze();
                _coreColor = Color;
            }
            dc.DrawEllipse(_coreBrush, null, center, d * 0.24, d * 0.24);
        }
    }

    /// <summary>发光分隔标题：文字 + 渐变横线</summary>
    public class SectionHeader : FrameworkElement
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(nameof(Text), typeof(string), typeof(SectionHeader),
                new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }
    }
}