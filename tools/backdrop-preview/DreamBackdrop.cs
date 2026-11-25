using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SoulLink.BackdropPreview
{
    /// <summary>
    /// 「樱雾星云」独立视觉预览：柔雾极光、丝绸光带、星环与少量心形星屑。
    /// 静态星野和噪点按尺寸缓存；动画帧只更新轻量几何，不创建位图特效。
    /// </summary>
    public sealed class DreamBackdrop : FrameworkElement
    {
        private struct Mote
        {
            public double X, Y, Size, Speed, Phase, Drift;
            public int Color;
        }

        private sealed class Ribbon
        {
            public StreamGeometry Geometry = null!;
            public Pen[] Pens = Array.Empty<Pen>();
            public double Phase, Amplitude, Speed;
        }

        private static readonly Color[] Palette = { Pal.Lilac, Pal.Pink, Pal.Ice, Pal.Gold };
        private static readonly Dictionary<(Color Color, int Alpha), SolidColorBrush> Solids = new();

        private readonly Mote[] _motes = new Mote[76];
        private readonly Mote[] _hearts = new Mote[7];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<Ribbon> _ribbons = new();
        private readonly Pen[] _pinkTrail = MakeTrail(Pal.Pink);
        private readonly Pen[] _cyanTrail = MakeTrail(Pal.Ice);
        private TimeSpan _lastFrame;
        private double _time;
        private bool _running;
        private Window? _window;
        private Size _cachedSize;
        private ImageBrush? _baseBrush;
        private Brush? _vignetteBrush;
        private Brush? _pearlBrush;
        private Pen? _pearlRim;
        private Pen? _orbitPen;
        private Pen? _orbitGlow;
        private Pen? _tickPen;
        private StreamGeometry? _orbitA;
        private StreamGeometry? _orbitB;
        private StreamGeometry? _orbitC;
        private StreamGeometry? _frontArc;
        private double _orbitRadius;

        public DreamBackdrop()
        {
            IsHitTestVisible = false;
            Focusable = false;
            SnapsToDevicePixels = true;
            var random = new Random(20261119);
            Seed(_motes, random, false);
            Seed(_hearts, random, true);
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            IsVisibleChanged += (_, _) => SyncAnimation();
        }

        private static void Seed(Mote[] motes, Random random, bool hearts)
        {
            for (int i = 0; i < motes.Length; i++)
            {
                motes[i] = new Mote
                {
                    X = random.NextDouble(),
                    Y = random.NextDouble(),
                    Size = hearts ? 4.5 + random.NextDouble() * 5 : 0.65 + random.NextDouble() * 1.65,
                    Speed = hearts ? 0.007 + random.NextDouble() * 0.006 : 0.003 + random.NextDouble() * 0.009,
                    Phase = random.NextDouble() * Math.PI * 2,
                    Drift = 0.006 + random.NextDouble() * 0.014,
                    Color = random.Next(hearts ? 2 : Palette.Length)
                };
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _window = Window.GetWindow(this);
            if (_window != null) _window.StateChanged += OnWindowStateChanged;
            SyncAnimation();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Stop();
            if (_window != null) _window.StateChanged -= OnWindowStateChanged;
            _window = null;
        }

        private void OnWindowStateChanged(object? sender, EventArgs e) => SyncAnimation();

        private void SyncAnimation()
        {
            bool shouldRun = IsLoaded && IsVisible && ActualWidth > 1 && ActualHeight > 1
                             && (_window == null || _window.WindowState != WindowState.Minimized);
            if (shouldRun) Start();
            else Stop();
        }

        private void Start()
        {
            if (_running) return;
            _running = true;
            _lastFrame = _clock.Elapsed;
            CompositionTarget.Rendering += OnFrame;
        }

        private void Stop()
        {
            if (!_running) return;
            _running = false;
            CompositionTarget.Rendering -= OnFrame;
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            var now = _clock.Elapsed;
            if (now - _lastFrame < TimeSpan.FromMilliseconds(33)) return;
            double delta = Math.Min((now - _lastFrame).TotalSeconds, 0.08);
            _lastFrame = now;
            _time += delta;
            InvalidateVisual();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);
            RebuildScene();
            SyncAnimation();
        }

        private void RebuildScene()
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 2 || h < 2) return;
            _cachedSize = new Size(w, h);
            _baseBrush = BuildBase(w, h);
            _vignetteBrush = BuildVignette();
            _ribbons.Clear();
            _ribbons.Add(MakeRibbon(w, h, 0, Pal.HotPink, Pal.Lilac, Pal.Ice));
            _ribbons.Add(MakeRibbon(w, h, 1, Pal.Violet, Pal.Pink, Pal.Lilac));
            _ribbons.Add(MakeRibbon(w, h, 2, Pal.Ice, Pal.Lilac, Pal.Pink));

            _orbitRadius = Math.Min(w, h) * 0.235;
            _orbitA = MakeEllipse(_orbitRadius, _orbitRadius * 0.34);
            _orbitB = MakeEllipse(_orbitRadius * 0.79, _orbitRadius * 0.29);
            _orbitC = MakeEllipse(_orbitRadius * 1.12, _orbitRadius * 0.43);
            _frontArc = MakeArc(_orbitRadius, _orbitRadius * 0.34, 0.06, Math.PI * 0.91);
            _orbitPen = GradientPen(Pal.Lilac, Pal.Ice, 0.85, 0.34);
            _orbitGlow = GradientPen(Pal.Pink, Pal.Lilac, 7.0, 0.045);
            _tickPen = new Pen(Solid(Pal.Lilac, 0.21), 0.75);
            _tickPen.Freeze();

            _pearlBrush = new RadialGradientBrush
            {
                Center = new Point(0.32, 0.25),
                GradientOrigin = new Point(0.32, 0.25),
                RadiusX = 0.76,
                RadiusY = 0.76,
                GradientStops = new GradientStopCollection
                {
                    new(Color.FromRgb(0xFF, 0xF3, 0xFF), 0),
                    new(Color.FromRgb(0xEF, 0xC6, 0xFF), 0.20),
                    new(Color.FromRgb(0xB8, 0x82, 0xD9), 0.55),
                    new(Color.FromRgb(0x55, 0x3B, 0x84), 0.83),
                    new(Color.FromRgb(0x24, 0x20, 0x4B), 1)
                }
            };
            _pearlBrush.Freeze();
            _pearlRim = new Pen(Solid(Pal.Pink, 0.43), 0.9);
            _pearlRim.Freeze();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 2 || h < 2) return;
            if (_cachedSize != new Size(w, h) || _baseBrush == null) RebuildScene();

            dc.DrawRectangle(_baseBrush, null, new Rect(0, 0, w, h));
            DrawNebula(dc, w, h);
            DrawRibbons(dc);
            DrawComet(dc, w, h, _time * 0.035 + 0.18, _pinkTrail, Pal.Pink);
            DrawComet(dc, w, h, _time * 0.024 + 0.68, _cyanTrail, Pal.Ice);
            DrawOrbitSystem(dc, w, h);
            DrawMotes(dc, w, h);
            if (_vignetteBrush != null) dc.DrawRectangle(_vignetteBrush, null, new Rect(0, 0, w, h));
        }

        private static ImageBrush BuildBase(double w, double h)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var background = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 1),
                    GradientStops = new GradientStopCollection
                    {
                        new(Color.FromRgb(0x0A, 0x08, 0x19), 0),
                        new(Color.FromRgb(0x13, 0x10, 0x2A), 0.48),
                        new(Color.FromRgb(0x07, 0x0D, 0x21), 1)
                    }
                };
                background.Freeze();
                dc.DrawRectangle(background, null, new Rect(0, 0, w, h));

                double horizon = h * 0.76;
                var gridPen = new Pen(Solid(Pal.Ice, 0.045), 0.7);
                gridPen.Freeze();
                for (int row = 0; row <= 10; row++)
                {
                    double p = row / 10.0;
                    double y = horizon + Math.Pow(p, 2.25) * (h - horizon);
                    dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
                }
                for (int col = -15; col <= 15; col++)
                {
                    dc.DrawLine(gridPen,
                        new Point(w * 0.62 + col * 5, horizon),
                        new Point(w * 0.62 + col * w / 12.0, h));
                }

                var random = new Random(8137);
                for (int i = 0; i < 170; i++)
                {
                    double x = random.NextDouble() * w;
                    double y = random.NextDouble() * h;
                    double a = 0.08 + random.NextDouble() * 0.24;
                    double radius = 0.3 + random.NextDouble() * 0.8;
                    dc.DrawEllipse(Solid(Palette[random.Next(Palette.Length)], a), null,
                        new Point(x, y), radius, radius);
                }
                DrawConstellation(dc, w * 0.10, h * 0.18, Math.Min(w, h) * 0.075);
                DrawConstellation(dc, w * 0.91, h * 0.78, Math.Min(w, h) * 0.055);
                Fx.Grain(dc, w, h, 0, 0.16);
            }

            var dpi = VisualTreeHelper.GetDpi(visual);
            var bitmap = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(w * dpi.DpiScaleX)),
                Math.Max(1, (int)Math.Ceiling(h * dpi.DpiScaleY)),
                96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            var brush = new ImageBrush(bitmap) { Stretch = Stretch.Fill };
            brush.Freeze();
            return brush;
        }

        private static void DrawConstellation(DrawingContext dc, double x, double y, double scale)
        {
            Point[] points =
            {
                new(x - scale, y + scale * 0.2), new(x - scale * 0.42, y - scale * 0.38),
                new(x + scale * 0.12, y + scale * 0.12), new(x + scale * 0.60, y - scale * 0.60),
                new(x + scale, y + scale * 0.04)
            };
            var pen = new Pen(Solid(Pal.Lilac, 0.11), 0.65);
            pen.Freeze();
            for (int i = 1; i < points.Length; i++) dc.DrawLine(pen, points[i - 1], points[i]);
            foreach (var point in points)
            {
                dc.DrawEllipse(Solid(Pal.Lilac, 0.30), null, point, 3.3, 3.3);
                dc.DrawEllipse(Solid(Pal.White, 0.72), null, point, 0.9, 0.9);
            }
        }

        private void DrawNebula(DrawingContext dc, double w, double h)
        {
            double unit = Math.Max(w, h);
            Fx.Blob(dc, w * (0.18 + Math.Sin(_time * 0.10) * 0.035), h * 0.26,
                unit * 0.41, Pal.Violet, 0.19);
            Fx.Blob(dc, w * (0.76 + Math.Cos(_time * 0.12) * 0.025), h * 0.47,
                unit * 0.34, Pal.HotPink, 0.17);
            Fx.Blob(dc, w * 0.91, h * (0.16 + Math.Sin(_time * 0.15) * 0.035),
                unit * 0.32, Pal.Ice, 0.13);
            Fx.Blob(dc, w * 0.39, h * (0.93 + Math.Cos(_time * 0.09) * 0.025),
                unit * 0.34, Pal.Pink, 0.11);
        }

        private void DrawRibbons(DrawingContext dc)
        {
            foreach (var ribbon in _ribbons)
            {
                double y = Math.Sin(_time * ribbon.Speed + ribbon.Phase) * ribbon.Amplitude;
                double opacity = 0.76 + Math.Sin(_time * 0.20 + ribbon.Phase) * 0.18;
                dc.PushTransform(new TranslateTransform(0, y));
                dc.PushOpacity(opacity);
                foreach (var pen in ribbon.Pens) dc.DrawGeometry(null, pen, ribbon.Geometry);
                dc.Pop();
                dc.Pop();
            }
        }

        private void DrawOrbitSystem(DrawingContext dc, double w, double h)
        {
            if (_orbitA == null || _orbitB == null || _orbitC == null || _pearlBrush == null) return;
            double cx = w * 0.755 + Math.Sin(_time * 0.16) * 5;
            double cy = h * 0.465 + Math.Cos(_time * 0.13) * 6;
            double r = _orbitRadius;
            double pearl = Math.Min(w, h) * 0.069;

            Fx.Blob(dc, cx, cy, r * 1.18, Pal.Lilac, 0.15);
            Fx.Blob(dc, cx, cy, pearl * 2.2, Pal.Pink, 0.22);

            if (_tickPen != null)
            {
                for (int i = 0; i < 64; i++)
                {
                    double angle = i / 64.0 * Math.PI * 2 + _time * 0.012;
                    double inner = r * 0.65;
                    double outer = inner + (i % 8 == 0 ? 7 : i % 4 == 0 ? 4 : 2);
                    dc.DrawLine(_tickPen,
                        new Point(cx + Math.Cos(angle) * inner, cy + Math.Sin(angle) * inner),
                        new Point(cx + Math.Cos(angle) * outer, cy + Math.Sin(angle) * outer));
                }
            }

            DrawOrbit(dc, _orbitC, cx, cy, -16 + Math.Sin(_time * 0.08) * 2);
            DrawOrbit(dc, _orbitB, cx, cy, 58 + _time * 0.10);
            DrawOrbit(dc, _orbitA, cx, cy, -28 + Math.Sin(_time * 0.11) * 3);

            dc.DrawEllipse(_pearlBrush, _pearlRim, new Point(cx, cy), pearl, pearl);
            dc.DrawEllipse(Solid(Pal.White, 0.18), null,
                new Point(cx - pearl * 0.20, cy - pearl * 0.36), pearl * 0.31, pearl * 0.12);
            Fx.Blob(dc, cx, cy, pearl * 0.75, Pal.Pink, 0.26);
            Fx.Heart(dc, cx, cy + 1, pearl * 0.38, -7, Pal.Pink, 0.78);
            Fx.Heart(dc, cx - 1, cy - 1, pearl * 0.23, -7, Pal.White, 0.56);

            if (_frontArc != null && _orbitGlow != null && _orbitPen != null)
            {
                dc.PushTransform(new TranslateTransform(cx, cy));
                dc.PushTransform(new RotateTransform(-28 + Math.Sin(_time * 0.11) * 3));
                dc.DrawGeometry(null, _orbitGlow, _frontArc);
                dc.DrawGeometry(null, _orbitPen, _frontArc);
                dc.Pop();
                dc.Pop();
            }

            DrawOrbitalComet(dc, cx, cy, r, r * 0.34, -28, _time * 0.32 + 0.9, Pal.Pink);
            DrawOrbitalComet(dc, cx, cy, r * 0.79, r * 0.29, 58 + _time * 0.10, -_time * 0.26 + 3.1, Pal.Ice);
            Fx.Sparkle(dc, cx + r * 0.64, cy - r * 0.63, 8.5, _time * 8, Pal.White, 0.80);
            Fx.Sparkle(dc, cx - r * 0.77, cy + r * 0.31, 5.5, -_time * 6, Pal.Pink, 0.62);
        }

        private void DrawOrbit(DrawingContext dc, StreamGeometry geometry, double cx, double cy, double rotation)
        {
            if (_orbitGlow == null || _orbitPen == null) return;
            dc.PushTransform(new TranslateTransform(cx, cy));
            dc.PushTransform(new RotateTransform(rotation));
            dc.DrawGeometry(null, _orbitGlow, geometry);
            dc.DrawGeometry(null, _orbitPen, geometry);
            dc.Pop();
            dc.Pop();
        }

        private static void DrawOrbitalComet(DrawingContext dc, double cx, double cy,
            double rx, double ry, double rotation, double phase, Color color)
        {
            double rad = rotation * Math.PI / 180.0;
            for (int i = 13; i >= 0; i--)
            {
                double a = phase - i * 0.026;
                double x = Math.Cos(a) * rx, y = Math.Sin(a) * ry;
                double px = cx + x * Math.Cos(rad) - y * Math.Sin(rad);
                double py = cy + x * Math.Sin(rad) + y * Math.Cos(rad);
                double strength = 1 - i / 14.0;
                dc.DrawEllipse(Solid(color, strength * 0.54), null, new Point(px, py),
                    0.45 + strength * 1.25, 0.45 + strength * 1.25);
            }
            double hx = Math.Cos(phase) * rx, hy = Math.Sin(phase) * ry;
            double xHead = cx + hx * Math.Cos(rad) - hy * Math.Sin(rad);
            double yHead = cy + hx * Math.Sin(rad) + hy * Math.Cos(rad);
            Fx.Blob(dc, xHead, yHead, 16, color, 0.48);
            dc.DrawEllipse(Solid(Pal.White, 0.94), null, new Point(xHead, yHead), 1.8, 1.8);
        }

        private void DrawMotes(DrawingContext dc, double w, double h)
        {
            for (int i = 0; i < _motes.Length; i++)
            {
                var mote = _motes[i];
                double y = ((mote.Y - _time * mote.Speed) % 1 + 1) % 1;
                double x = mote.X + Math.Sin(_time * 0.24 + mote.Phase) * mote.Drift;
                double twinkle = 0.5 + 0.5 * Math.Sin(_time * 1.1 + mote.Phase);
                double alpha = 0.15 + twinkle * 0.54;
                var point = new Point(x * w, y * h);
                var color = Palette[mote.Color];
                if (i % 3 == 0) Fx.Blob(dc, point.X, point.Y, mote.Size * 4.0, color, alpha * 0.24);
                dc.DrawEllipse(Solid(color, alpha), null, point, mote.Size, mote.Size);
                if (i % 11 == 0)
                    Fx.Sparkle(dc, point.X, point.Y, 3.0 + twinkle * 3.5, mote.Phase * 30, color, alpha * 0.75);
            }

            foreach (var mote in _hearts)
            {
                double y = ((mote.Y - _time * mote.Speed) % 1 + 1) % 1;
                double x = mote.X + Math.Sin(_time * 0.20 + mote.Phase) * mote.Drift;
                double alpha = 0.10 + (0.5 + 0.5 * Math.Sin(_time * 0.55 + mote.Phase)) * 0.20;
                Fx.Heart(dc, x * w, y * h, mote.Size, Math.Sin(_time * 0.2 + mote.Phase) * 18,
                    mote.Color == 0 ? Pal.Pink : Pal.Lilac, alpha);
            }
        }

        private static void DrawComet(DrawingContext dc, double w, double h,
            double progress, Pen[] trail, Color color)
        {
            double p = progress % 1.20;
            if (p < 0) p += 1.20;
            if (p > 1.08) return;
            for (int i = trail.Length - 1; i >= 1; i--)
            {
                double p1 = p - i * 0.0027;
                double p2 = p - (i - 1) * 0.0027;
                dc.DrawLine(trail[i], CometPoint(p1, w, h), CometPoint(p2, w, h));
            }
            var head = CometPoint(p, w, h);
            Fx.Blob(dc, head.X, head.Y, 23, color, 0.48);
            Fx.Sparkle(dc, head.X, head.Y, 7, 20, color, 0.82);
        }

        private static Point CometPoint(double p, double w, double h) =>
            new((-0.12 + p * 1.24) * w,
                (0.80 - p * 0.24 + Math.Sin(p * Math.PI * 2) * 0.035) * h);

        private Ribbon MakeRibbon(double w, double h, int index, Color a, Color b, Color c)
        {
            Point[] points;
            if (index == 0)
            {
                points = new[]
                {
                    new Point(-w * 0.14, h * 0.64), new Point(w * 0.13, h * 0.27),
                    new Point(w * 0.36, h * 0.91), new Point(w * 0.64, h * 0.57),
                    new Point(w * 0.85, h * 0.30), new Point(w * 1.00, h * 0.57),
                    new Point(w * 1.15, h * 0.25)
                };
            }
            else if (index == 1)
            {
                points = new[]
                {
                    new Point(-w * 0.14, h * 0.85), new Point(w * 0.18, h * 0.60),
                    new Point(w * 0.40, h * 1.04), new Point(w * 0.71, h * 0.75),
                    new Point(w * 0.91, h * 0.57), new Point(w * 1.02, h * 0.88),
                    new Point(w * 1.16, h * 0.60)
                };
            }
            else
            {
                points = new[]
                {
                    new Point(w * 0.40, -h * 0.10), new Point(w * 0.54, h * 0.17),
                    new Point(w * 0.75, -h * 0.02), new Point(w * 0.91, h * 0.22),
                    new Point(w * 1.03, h * 0.39), new Point(w * 1.04, h * 0.21),
                    new Point(w * 1.15, h * 0.42)
                };
            }

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(points[0], false, false);
                ctx.BezierTo(points[1], points[2], points[3], true, true);
                ctx.BezierTo(points[4], points[5], points[6], true, true);
            }
            geometry.Freeze();
            double scale = index == 2 ? 0.60 : 1;
            double[] widths = { 110 * scale, 48 * scale, 14 * scale, 1.05 };
            double[] alphas = { 0.032, 0.068, 0.11, 0.43 };
            var pens = new Pen[widths.Length];
            for (int i = 0; i < pens.Length; i++) pens[i] = GradientPen(a, b, c, widths[i], alphas[i]);
            return new Ribbon
            {
                Geometry = geometry,
                Pens = pens,
                Phase = index * 1.9,
                Amplitude = index == 2 ? 5 : 10,
                Speed = 0.20 + index * 0.035
            };
        }

        private static Pen GradientPen(Color a, Color b, double width, double alpha) =>
            GradientPen(a, b, b, width, alpha);

        private static Pen GradientPen(Color a, Color b, Color c, double width, double alpha)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0),
                GradientStops = new GradientStopCollection
                {
                    new(Color.FromArgb(0, a.R, a.G, a.B), 0),
                    new(WithAlpha(a, alpha * 0.75), 0.19),
                    new(WithAlpha(b, alpha), 0.51),
                    new(WithAlpha(c, alpha * 0.72), 0.81),
                    new(Color.FromArgb(0, c.R, c.G, c.B), 1)
                }
            };
            brush.Freeze();
            var pen = new Pen(brush, width)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();
            return pen;
        }

        private static Pen[] MakeTrail(Color color)
        {
            var pens = new Pen[26];
            for (int i = 0; i < pens.Length; i++)
            {
                double strength = 1 - i / (double)pens.Length;
                pens[i] = new Pen(Solid(color, strength * strength * 0.45), 0.35 + strength * 1.7)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round
                };
                pens[i].Freeze();
            }
            return pens;
        }

        private static StreamGeometry MakeEllipse(double rx, double ry)
        {
            var points = new List<Point>();
            for (int i = 0; i <= 128; i++)
            {
                double a = i / 128.0 * Math.PI * 2;
                points.Add(new Point(Math.Cos(a) * rx, Math.Sin(a) * ry));
            }
            return Polyline(points, true);
        }

        private static StreamGeometry MakeArc(double rx, double ry, double start, double end)
        {
            var points = new List<Point>();
            for (int i = 0; i <= 72; i++)
            {
                double a = start + (end - start) * i / 72.0;
                points.Add(new Point(Math.Cos(a) * rx, Math.Sin(a) * ry));
            }
            return Polyline(points, false);
        }

        private static StreamGeometry Polyline(List<Point> points, bool closed)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(points[0], false, closed);
                ctx.PolyLineTo(points.GetRange(1, points.Count - 1), true, true);
            }
            geometry.Freeze();
            return geometry;
        }

        private static Brush BuildVignette()
        {
            var brush = new RadialGradientBrush
            {
                Center = new Point(0.53, 0.46),
                GradientOrigin = new Point(0.53, 0.46),
                RadiusX = 0.76,
                RadiusY = 0.78,
                GradientStops = new GradientStopCollection
                {
                    new(Color.FromArgb(0, 0x03, 0x04, 0x12), 0),
                    new(Color.FromArgb(0, 0x03, 0x04, 0x12), 0.56),
                    new(Color.FromArgb(0x72, 0x03, 0x04, 0x12), 1)
                }
            };
            brush.Freeze();
            return brush;
        }

        private static Color WithAlpha(Color color, double alpha) =>
            Color.FromArgb((byte)(255 * Math.Clamp(alpha, 0, 1)), color.R, color.G, color.B);

        private static SolidColorBrush Solid(Color color, double alpha)
        {
            int q = (int)Math.Round(Math.Clamp(alpha, 0, 1) * 96);
            var key = (color, q);
            if (Solids.TryGetValue(key, out var brush)) return brush;
            brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(q * 255 / 96.0), color.R, color.G, color.B));
            brush.Freeze();
            Solids[key] = brush;
            return brush;
        }
    }
}
