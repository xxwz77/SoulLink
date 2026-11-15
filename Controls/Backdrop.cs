using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace SoulLink.Controls
{
    /// <summary>
    /// 动态梦幻背景：深空底色 + 极光光斑 + 透视网格 + 漂浮粒子。
    /// 纯 WPF 绘制，不需要任何图片资源；窗口最小化时自动停止刷新以省电。
    /// </summary>
    public class AuroraBackdrop : FrameworkElement
    {
        private struct Particle
        {
            public double X, Y, R, Speed, Phase, Drift;
            public int Chroma;
        }

        private readonly Particle[] _particles;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private TimeSpan _last;
        private bool _running;

        private static readonly Color Cyan = Color.FromRgb(0x4F, 0xE3, 0xFF);
        private static readonly Color Violet = Color.FromRgb(0x8B, 0x6B, 0xFF);
        private static readonly Color Magenta = Color.FromRgb(0xFF, 0x5F, 0xD2);
        private static readonly Color Mint = Color.FromRgb(0x41, 0xF0, 0xC0);

        public AuroraBackdrop()
        {
            IsHitTestVisible = false;
            _particles = new Particle[64];
            var rnd = new Random(20261114);
            for (int i = 0; i < _particles.Length; i++)
            {
                _particles[i] = new Particle
                {
                    X = rnd.NextDouble(),
                    Y = rnd.NextDouble(),
                    R = 0.6 + rnd.NextDouble() * 2.0,
                    Speed = 0.004 + rnd.NextDouble() * 0.014,
                    Phase = rnd.NextDouble() * Math.PI * 2,
                    Drift = (rnd.NextDouble() - 0.5) * 0.006,
                    Chroma = rnd.Next(4)
                };
            }
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);
            EnsureAnimation();
        }

        private void EnsureAnimation()
        {
            if (_running) return;
            _running = true;
            CompositionTarget.Rendering += OnFrame;
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            var now = _clock.Elapsed;
            if (now - _last < TimeSpan.FromMilliseconds(33)) return; // ~30fps 足够
            _last = now;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            double t = _clock.Elapsed.TotalSeconds;
            var full = new Rect(0, 0, w, h);

            // 1) 深空底色
            var baseBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            bool dark = SoulLink.Services.ThemeManager.IsDark;
            baseBrush.GradientStops.Add(new GradientStop(dark ? Color.FromRgb(0x05, 0x06, 0x0C) : Color.FromRgb(0xFF, 0xF8, 0xFD), 0));
            baseBrush.GradientStops.Add(new GradientStop(dark ? Color.FromRgb(0x0A, 0x0E, 0x24) : Color.FromRgb(0xEE, 0xE9, 0xFF), 0.45));
            baseBrush.GradientStops.Add(new GradientStop(dark ? Color.FromRgb(0x0D, 0x09, 0x1F) : Color.FromRgb(0xFF, 0xEC, 0xF5), 1));
            baseBrush.Freeze();
            dc.DrawRectangle(baseBrush, null, full);

            // 2) 极光光斑
            DrawBlob(dc, w * (0.18 + 0.06 * Math.Sin(t * 0.21)), h * (0.20 + 0.05 * Math.Cos(t * 0.17)),
                     Math.Max(w, h) * 0.55, Violet, 0.32);
            DrawBlob(dc, w * (0.82 + 0.05 * Math.Cos(t * 0.19)), h * (0.16 + 0.06 * Math.Sin(t * 0.23)),
                     Math.Max(w, h) * 0.48, Cyan, 0.26);
            DrawBlob(dc, w * (0.62 + 0.07 * Math.Sin(t * 0.13 + 1.2)), h * (0.86 + 0.04 * Math.Cos(t * 0.15)),
                     Math.Max(w, h) * 0.52, Magenta, 0.20);
            DrawBlob(dc, w * (0.30 + 0.06 * Math.Cos(t * 0.11 + 2.1)), h * (0.78 + 0.05 * Math.Sin(t * 0.18)),
                     Math.Max(w, h) * 0.40, Mint, 0.14);

            // 3) 透视网格（科技感底线）
            var gridBrush = new LinearGradientBrush(Color.FromArgb(0x24, 0x7E, 0xD8, 0xFF),
                                                    Color.FromArgb(0x00, 0x7E, 0xD8, 0xFF), 90);
            gridBrush.Freeze();
            var gridPen = new Pen(gridBrush, 1);
            gridPen.Freeze();

            double horizon = h * 0.62;
            int rows = 14;
            for (int i = 0; i <= rows; i++)
            {
                double p = i / (double)rows;
                double y = horizon + Math.Pow(p, 2.2) * (h - horizon) + Math.Sin(t * 0.6 + p * 4) * 1.2;
                if (y > h) continue;
                dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
            }
            int cols = 22;
            for (int i = -cols; i <= cols; i++)
            {
                double xBottom = w / 2 + i * (w / cols) * 1.35;
                dc.DrawLine(gridPen, new Point(w / 2 + i * 6, horizon), new Point(xBottom, h));
            }

            // 4) 漂浮粒子
            for (int i = 0; i < _particles.Length; i++)
            {
                var p = _particles[i];
                double y = (p.Y - (t * p.Speed) % 1.0 + 1.0) % 1.0;
                double x = (p.X + Math.Sin(t * 0.5 + p.Phase) * 0.012 + p.Drift * (t % 10)) % 1.0;
                if (x < 0) x += 1;

                double px = x * w, py = y * h;
                double alpha = 0.28 + 0.42 * (Math.Sin(t * 1.4 + p.Phase) * 0.5 + 0.5);

                Color c;
                switch (p.Chroma)
                {
                    case 0: c = Cyan; break;
                    case 1: c = Violet; break;
                    case 2: c = Magenta; break;
                    default: c = Mint; break;
                }

                var glow = new SolidColorBrush(Color.FromArgb((byte)(alpha * 60), c.R, c.G, c.B));
                glow.Freeze();
                dc.DrawEllipse(glow, null, new Point(px, py), p.R * 4.5, p.R * 4.5);

                var core = new SolidColorBrush(Color.FromArgb((byte)(alpha * 220), 0xFF, 0xFF, 0xFF));
                core.Freeze();
                dc.DrawEllipse(core, null, new Point(px, py), p.R * 0.7, p.R * 0.7);
            }

            // 5) 顶部/底部渐隐，让玻璃卡片更突出
            var vignette = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };
            vignette.GradientStops.Add(new GradientStop(Color.FromArgb(0x50, 0x00, 0x00, 0x00), 0));
            vignette.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x00, 0x00, 0x00), 0.30));
            vignette.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x00, 0x00, 0x00), 0.70));
            vignette.GradientStops.Add(new GradientStop(Color.FromArgb(0x60, 0x00, 0x00, 0x00), 1));
            vignette.Freeze();
            if (dark) dc.DrawRectangle(vignette, null, full);
        }

        private static void DrawBlob(DrawingContext dc, double cx, double cy, double radius, Color color, double opacity)
        {
            var brush = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.5),
                GradientOrigin = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5
            };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * opacity), color.R, color.G, color.B), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(120 * opacity), color.R, color.G, color.B), 0.42));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
            brush.Freeze();

            dc.DrawEllipse(brush, null, new Point(cx, cy), radius, radius);
        }
    }
}
