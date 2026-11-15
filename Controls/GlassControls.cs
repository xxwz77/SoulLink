using System;
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

        private double _phase;

        public GlowDot()
        {
            if (Pulse)
            {
                var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(60) };
                timer.Tick += (_, __) =>
                {
                    if (!Pulse) { timer.Stop(); return; }
                    _phase += 0.09;
                    InvalidateVisual();
                };
                Loaded += (_, __) => timer.Start();
                Unloaded += (_, __) => timer.Stop();
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            double d = Math.Min(ActualWidth, ActualHeight);
            if (d <= 0) return;
            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            double pulse = Pulse ? (Math.Sin(_phase) * 0.5 + 0.5) : 0.5;

            var halo = new RadialGradientBrush(Color.FromArgb((byte)(70 + 60 * pulse), Color.R, Color.G, Color.B),
                                               Color.FromArgb(0, Color.R, Color.G, Color.B));
            dc.DrawEllipse(halo, null, center, d * 0.5, d * 0.5);

            var core = new SolidColorBrush(Color);
            core.Freeze();
            dc.DrawEllipse(core, null, center, d * 0.24, d * 0.24);
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