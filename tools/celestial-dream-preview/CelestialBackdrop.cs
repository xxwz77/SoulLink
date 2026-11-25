using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace SoulLink.CelestialDreamPreview
{
    /// <summary>
    /// 双模式梦幻星空：无网格、无爱心。预冻结星云与远近星野，
    /// 每帧仅更新晶体星球的轨道、亮星与流光；不在帧循环中生成图片或特效。
    /// </summary>
    public sealed class CelestialBackdrop : FrameworkElement
    {
        private sealed class Star
        {
            public double X, Y, Radius, Speed, Phase, Drift;
            public int Color;
        }

        private sealed class Moon
        {
            public double Rx, Ry, Rotation, Phase, Speed, Radius;
        }

        private sealed class Theme
        {
            public Color Top, Middle, Bottom, Primary, Secondary, Accent, Ring, Comet;
            public Color PlanetLight, PlanetMid, PlanetShade, PlanetDeep, Moon, Vignette;
            public double Nebula, Stars, CometAlpha, RingAlpha, VignetteAlpha;
            public Theme(bool light)
            {
                if (light)
                {
                    Top = C("FFF9FD"); Middle = C("F4F3FF"); Bottom = C("EEF7FF");
                    Primary = C("E5A7D5"); Secondary = C("99CFF1"); Accent = C("B6A7EC");
                    Ring = C("8B75C7"); Comet = C("638CD5");
                    PlanetLight = C("FFF5FF"); PlanetMid = C("D7B9EF");
                    PlanetShade = C("9473C7"); PlanetDeep = C("536392");
                    Moon = C("E8CBFA"); Vignette = C("929ED3");
                    Nebula = 0.58; Stars = 0.50; CometAlpha = 0.49;
                    RingAlpha = 0.49; VignetteAlpha = 0.16;
                }
                else
                {
                    Top = C("0B081D"); Middle = C("171332"); Bottom = C("081329");
                    Primary = C("DF72B9"); Secondary = C("52C5EB"); Accent = C("9277EB");
                    Ring = C("B6A0F4"); Comet = C("9CE8FF");
                    PlanetLight = C("FBE8FF"); PlanetMid = C("B899EC");
                    PlanetShade = C("7350AB"); PlanetDeep = C("202D5B");
                    Moon = C("B2CFFF"); Vignette = C("050615");
                    Nebula = 0.34; Stars = 0.78; CometAlpha = 0.65;
                    RingAlpha = 0.48; VignetteAlpha = 0.46;
                }
            }
        }

        public static readonly DependencyProperty IsLightProperty = DependencyProperty.Register(
            nameof(IsLight), typeof(bool), typeof(CelestialBackdrop),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender,
                (sender, _) => ((CelestialBackdrop)sender).OnThemeChanged()));

        public bool IsLight
        {
            get => (bool)GetValue(IsLightProperty);
            set => SetValue(IsLightProperty, value);
        }

        private readonly Star[] _near = new Star[96];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Moon[] _moons = { new(), new() };
        private readonly Dictionary<(Color, int), SolidColorBrush> _solids = new();
        private TimeSpan _last;
        private double _time;
        private bool _running;
        private Window? _window;
        private Theme _theme = new(false);
        private double _width, _height;
        private DrawingBrush? _starfield;
        private Brush? _vignette;
        private DrawingGroup? _planet;
        private StreamGeometry? _ring, _wideRing, _frontArc, _diamond;
        private Pen? _ringPen, _ringGlow, _wideRingPen;
        private Pen? _rim;
        private double _planetRadius;

        public CelestialBackdrop()
        {
            IsHitTestVisible = false;
            Focusable = false;
            var random = new Random(20261119);
            for (int i = 0; i < _near.Length; i++)
            {
                _near[i] = new Star
                {
                    X = random.NextDouble(), Y = random.NextDouble(),
                    Radius = 0.45 + random.NextDouble() * 1.3,
                    Speed = 0.0015 + random.NextDouble() * 0.0045,
                    Phase = random.NextDouble() * Math.PI * 2,
                    Drift = 0.002 + random.NextDouble() * 0.006,
                    Color = random.Next(3)
                };
            }
            Loaded += (_, _) =>
            {
                _window = Window.GetWindow(this);
                if (_window != null) _window.StateChanged += (_, __) => SyncAnimation();
                SyncAnimation();
            };
            Unloaded += (_, _) =>
            {
                if (_running) CompositionTarget.Rendering -= OnFrame;
                _running = false;
            };
            IsVisibleChanged += (_, _) => SyncAnimation();
        }

        private void OnThemeChanged()
        {
            _theme = new Theme(IsLight);
            _solids.Clear();
            RebuildScene();
            InvalidateVisual();
        }

        private void SyncAnimation()
        {
            bool run = IsLoaded && IsVisible && ActualWidth > 1 && ActualHeight > 1
                       && (_window == null || _window.WindowState != WindowState.Minimized);
            if (run == _running) return;
            _running = run;
            if (run) { _last = _clock.Elapsed; CompositionTarget.Rendering += OnFrame; }
            else CompositionTarget.Rendering -= OnFrame;
        }

        internal void PauseForCapture()
        {
            if (_running) CompositionTarget.Rendering -= OnFrame;
            _running = false;
        }

        internal void ResumeAfterCapture() => SyncAnimation();

        private void OnFrame(object? sender, EventArgs e)
        {
            var now = _clock.Elapsed;
            if (now - _last < TimeSpan.FromMilliseconds(33)) return;
            double dt = Math.Min((now - _last).TotalSeconds, 0.08);
            _last = now;
            _time += dt;
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
            _width = ActualWidth; _height = ActualHeight;
            if (_width < 2 || _height < 2) return;
            _starfield = BuildStarfield(_width, _height);
            _vignette = BuildVignette();
            _planet = BuildPlanet();
            _rim = new Pen(Solid(IsLight ? _theme.Accent : _theme.PlanetLight, IsLight ? 0.45 : 0.33), 0.8);
            _ring = Ellipse(_planetRadius * 2.9, _planetRadius * 0.77, false);
            _wideRing = Ellipse(_planetRadius * 3.55, _planetRadius * 1.05, false);
            _frontArc = Ellipse(_planetRadius * 2.9, _planetRadius * 0.77, true);
            _diamond = Diamond();
            _ringPen = new Pen(Gradient(_theme.Ring, _theme.Secondary, 0.9, _theme.RingAlpha), 0.9);
            _ringGlow = new Pen(Solid(_theme.Primary, IsLight ? 0.12 : 0.07), 5.0);
            _wideRingPen = new Pen(Gradient(_theme.Secondary, _theme.Accent, 0.7, _theme.RingAlpha * 0.42), 0.7);
            _ringPen.Freeze(); _ringGlow.Freeze(); _wideRingPen.Freeze();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 2 || h < 2) return;
            if (_starfield == null || w != _width || h != _height) RebuildScene();
            dc.DrawRectangle(_starfield, null, new Rect(0, 0, w, h));

            // 极缓慢移动的远近星群，不用任何地板/透视线。
            for (int i = 0; i < _near.Length; i++)
            {
                var star = _near[i];
                double x = (star.X + Math.Sin(_time * 0.11 + star.Phase) * star.Drift) * w;
                double y = ((star.Y - _time * star.Speed) % 1 + 1) % 1 * h;
                double twinkle = 0.5 + Math.Sin(_time * 0.8 + star.Phase) * 0.5;
                Color color = star.Color == 0 ? _theme.Secondary : star.Color == 1 ? _theme.Primary : _theme.Accent;
                double alpha = _theme.Stars * (0.35 + twinkle * 0.65);
                var point = new Point(x, y);
                dc.DrawEllipse(Solid(color, alpha), null, point, star.Radius, star.Radius);
                if (i % 12 == 0) Sparkle(dc, point, 3.2 + twinkle * 2.5, color, alpha);
            }

            DrawComet(dc, w, h, _time * 0.032 + 0.12, _theme.Primary, 0.54);
            DrawComet(dc, w, h, _time * 0.026 + 0.58, _theme.Secondary, 0.76);
            DrawPlanetSystem(dc, w, h);
            if (_vignette != null) dc.DrawRectangle(_vignette, null, new Rect(0, 0, w, h));
        }

        private DrawingBrush BuildStarfield(double w, double h)
        {
            var drawing = new DrawingGroup();
            using (var dc = drawing.Open())
            {
                var background = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
                    GradientStops = new GradientStopCollection
                    {
                        new(_theme.Top, 0), new(_theme.Middle, 0.52), new(_theme.Bottom, 1)
                    }
                };
                dc.DrawRectangle(background, null, new Rect(0, 0, w, h));

                // 两个无边界的大星云，不在动画帧里重新创建。
                Glow(dc, w * 0.17, h * 0.28, Math.Max(w, h) * 0.45, _theme.Accent, _theme.Nebula * 0.65);
                Glow(dc, w * 0.81, h * 0.38, Math.Max(w, h) * 0.34, _theme.Primary, _theme.Nebula * 0.70);
                Glow(dc, w * 0.92, h * 0.11, Math.Max(w, h) * 0.32, _theme.Secondary, _theme.Nebula * 0.55);
                Glow(dc, w * 0.39, h * 0.98, Math.Max(w, h) * 0.42, _theme.Secondary, _theme.Nebula * 0.38);
                DrawMilkyWay(dc, w, h);

                var random = new Random(41371);
                for (int i = 0; i < 610; i++)
                {
                    double x = random.NextDouble() * w;
                    double y = random.NextDouble() * h;
                    double r = random.NextDouble();
                    double size = r > 0.97 ? 1.05 + random.NextDouble() * 0.6 : 0.22 + random.NextDouble() * 0.6;
                    double alpha = _theme.Stars * (0.12 + random.NextDouble() * 0.46);
                    Color color = random.Next(5) == 0 ? _theme.Primary : random.Next(3) == 0 ? _theme.Secondary : _theme.Accent;
                    dc.DrawEllipse(Solid(color, alpha), null, new Point(x, y), size, size);
                    if (i % 81 == 0) Sparkle(dc, new Point(x, y), 4 + random.NextDouble() * 3, color, alpha * 0.9);
                }
            }
            drawing.Freeze();
            var brush = new DrawingBrush(drawing) { Stretch = Stretch.Fill };
            brush.Freeze();
            return brush;
        }

        private void DrawMilkyWay(DrawingContext dc, double w, double h)
        {
            var random = new Random(99401);
            const int count = 620;
            for (int i = 0; i < count; i++)
            {
                double p = random.NextDouble();
                double spread = (random.NextDouble() + random.NextDouble() + random.NextDouble() - 1.5);
                double x = (-0.08 + p * 1.20) * w;
                double centerY = h * (0.70 - p * 0.23 + Math.Sin(p * Math.PI * 2) * 0.055);
                double y = centerY + spread * h * 0.17;
                double strength = 1 - Math.Min(1, Math.Abs(spread) * 1.25);
                Color color = i % 4 == 0 ? _theme.Primary : i % 5 == 0 ? _theme.Secondary : _theme.Accent;
                double radius = 0.3 + random.NextDouble() * 0.8;
                double alpha = _theme.Stars * (0.025 + strength * random.NextDouble() * 0.22);
                dc.DrawEllipse(Solid(color, alpha), null, new Point(x, y), radius, radius);
                if (i % 13 == 0)
                    Glow(dc, x, y, 9 + random.NextDouble() * 14, color, _theme.Nebula * 0.07 * strength);
            }
        }

        private void DrawPlanetSystem(DrawingContext dc, double w, double h)
        {
            if (_planet == null || _ring == null || _wideRing == null || _frontArc == null || _diamond == null) return;
            double r = _planetRadius;
            double cx = w * 0.755 + Math.Sin(_time * 0.15) * 5;
            double cy = h * 0.45 + Math.Cos(_time * 0.12) * 6;
            double tilt = -25 + Math.Sin(_time * 0.13) * 3;
            Glow(dc, cx, cy, r * 3.4, _theme.Accent, _theme.Nebula * 0.50);
            Glow(dc, cx, cy, r * 1.6, _theme.Primary, _theme.Nebula * 0.48);

            // 稀疏刻度是星轨标记，不构成任何网格。
            var tick = Solid(_theme.Ring, _theme.RingAlpha * 0.56);
            for (int i = 0; i < 48; i++)
            {
                double a = i / 48.0 * Math.PI * 2 + _time * 0.014;
                double inner = r * 1.94;
                double length = i % 8 == 0 ? 6 : i % 4 == 0 ? 3.5 : 1.6;
                dc.DrawLine(new Pen(tick, 0.7),
                    new Point(cx + Math.Cos(a) * inner, cy + Math.Sin(a) * inner),
                    new Point(cx + Math.Cos(a) * (inner + length), cy + Math.Sin(a) * (inner + length)));
            }

            dc.PushTransform(new TranslateTransform(cx, cy));
            dc.PushTransform(new RotateTransform(35 + _time * 0.045));
            dc.DrawGeometry(null, _wideRingPen, _wideRing);
            dc.Pop(); dc.Pop();

            dc.PushTransform(new TranslateTransform(cx, cy));
            dc.PushTransform(new RotateTransform(tilt));
            dc.DrawGeometry(null, _ringGlow, _ring);
            dc.DrawGeometry(null, _ringPen, _ring);
            dc.Pop(); dc.Pop();

            _moons[0].Rx = r * 3.55; _moons[0].Ry = r * 1.05;
            _moons[0].Rotation = 35 + _time * 0.045; _moons[0].Phase = _time * 0.22 + 2.6;
            _moons[0].Speed = 0.22; _moons[0].Radius = r * 0.15;
            _moons[1].Rx = r * 2.9; _moons[1].Ry = r * 0.77;
            _moons[1].Rotation = tilt; _moons[1].Phase = -_time * 0.28 + 0.7;
            _moons[1].Speed = 0.28; _moons[1].Radius = r * 0.10;
            DrawMoon(dc, cx, cy, _moons[0], _theme.Secondary);

            dc.PushTransform(new TranslateTransform(cx, cy));
            dc.DrawDrawing(_planet);
            dc.Pop();
            dc.PushTransform(new TranslateTransform(cx, cy));
            dc.PushTransform(new RotateTransform(tilt));
            dc.DrawGeometry(null, _ringGlow, _frontArc);
            dc.DrawGeometry(null, _ringPen, _frontArc);
            dc.Pop(); dc.Pop();
            DrawMoon(dc, cx, cy, _moons[1], _theme.Moon);

            // 细小的菱形卫星与星芒，替代上一版的爱心。
            double mx = cx + r * 2.4, my = cy - r * 1.66;
            double pulse = 0.5 + Math.Sin(_time * 0.8) * 0.5;
            Glow(dc, mx, my, 22, _theme.Secondary, _theme.Nebula * 0.42);
            dc.PushTransform(new TranslateTransform(mx, my));
            dc.PushTransform(new RotateTransform(_time * 12));
            dc.DrawGeometry(Solid(_theme.Secondary, 0.74), new Pen(Solid(_theme.PlanetLight, 0.60), 0.6), _diamond);
            dc.Pop(); dc.Pop();
            Sparkle(dc, new Point(mx, my), 5 + pulse * 3, _theme.PlanetLight, _theme.Stars);
        }

        private DrawingGroup BuildPlanet()
        {
            _planetRadius = Math.Min(ActualWidth, ActualHeight) * 0.072;
            double r = _planetRadius;
            var group = new DrawingGroup();
            using (var dc = group.Open())
            {
                var sphere = new RadialGradientBrush
                {
                    Center = new Point(0.30, 0.23), GradientOrigin = new Point(0.30, 0.23),
                    RadiusX = 0.84, RadiusY = 0.84,
                    GradientStops = new GradientStopCollection
                    {
                        new(_theme.PlanetLight, 0), new(_theme.PlanetMid, 0.27),
                        new(_theme.PlanetShade, 0.65), new(_theme.PlanetDeep, 1)
                    }
                };
                dc.DrawEllipse(sphere, null, new Point(0, 0), r, r);

                // 晶体切面：不同透明度的三角面构成星球，不含爱心图案。
                Facet(dc, new[] { P(-0.72,-0.30), P(-0.20,-0.91), P(0.12,-0.08), P(-0.21,0.25) },
                    _theme.PlanetLight, 0.26);
                Facet(dc, new[] { P(-0.20,-0.91), P(0.54,-0.69), P(0.50,-0.10), P(0.12,-0.08) },
                    _theme.PlanetLight, 0.18);
                Facet(dc, new[] { P(0.12,-0.08), P(0.50,-0.10), P(0.76,0.45), P(0.12,0.88) },
                    _theme.PlanetLight, 0.15);
                Facet(dc, new[] { P(-0.72,-0.30), P(-0.21,0.25), P(-0.58,0.76), P(-0.90,0.26) },
                    _theme.PlanetDeep, 0.16);
                Facet(dc, new[] { P(-0.21,0.25), P(0.12,-0.08), P(0.12,0.88), P(-0.40,0.84) },
                    _theme.PlanetMid, 0.30);
                var seam = new Pen(Solid(_theme.PlanetLight, 0.20), 0.65);
                dc.DrawLine(seam, P(-0.20,-0.91), P(0.12,-0.08));
                dc.DrawLine(seam, P(0.12,-0.08), P(0.50,-0.10));
                dc.DrawLine(seam, P(0.12,-0.08), P(-0.21,0.25));
                dc.DrawLine(seam, P(-0.21,0.25), P(0.12,0.88));
                dc.DrawEllipse(Solid(_theme.PlanetLight, 0.22), null,
                    new Point(-r * 0.27, -r * 0.39), r * 0.29, r * 0.10);
                dc.DrawEllipse(null, _rim, new Point(0, 0), r, r);
            }
            group.Freeze();
            return group;
        }

        private void Facet(DrawingContext dc, Point[] points, Color color, double alpha)
        {
            var geometry = Polygon(points);
            dc.DrawGeometry(Solid(color, alpha), null, geometry);
        }

        private Point P(double x, double y) => new(x * _planetRadius, y * _planetRadius);

        private void DrawMoon(DrawingContext dc, double cx, double cy, Moon moon, Color color)
        {
            double a = moon.Phase;
            double rad = moon.Rotation * Math.PI / 180;
            double x = Math.Cos(a) * moon.Rx, y = Math.Sin(a) * moon.Ry;
            double px = cx + x * Math.Cos(rad) - y * Math.Sin(rad);
            double py = cy + x * Math.Sin(rad) + y * Math.Cos(rad);
            for (int i = 10; i >= 1; i--)
            {
                double tail = a - i * 0.024 * (moon.Speed > 0 ? 1 : -1);
                x = Math.Cos(tail) * moon.Rx; y = Math.Sin(tail) * moon.Ry;
                double tx = cx + x * Math.Cos(rad) - y * Math.Sin(rad);
                double ty = cy + x * Math.Sin(rad) + y * Math.Cos(rad);
                dc.DrawEllipse(Solid(color, (1 - i / 11.0) * _theme.CometAlpha * 0.4), null,
                    new Point(tx, ty), 0.6, 0.6);
            }
            Glow(dc, px, py, moon.Radius * 4.6, color, _theme.Nebula * 0.55);
            dc.DrawEllipse(new RadialGradientBrush(_theme.PlanetLight, color), null,
                new Point(px, py), moon.Radius, moon.Radius);
        }

        private void DrawComet(DrawingContext dc, double w, double h, double progress, Color color, double baseline)
        {
            double p = progress % 1.18;
            if (p < 0) p += 1.18;
            if (p > 1.06) return;
            for (int i = 20; i >= 1; i--)
            {
                double p1 = p - i * 0.0025;
                double p2 = p - (i - 1) * 0.0025;
                double strength = 1 - i / 21.0;
                var pen = new Pen(Solid(color, strength * strength * _theme.CometAlpha * 0.66), 0.3 + strength * 1.35);
                dc.DrawLine(pen, CometPoint(p1, w, h, baseline), CometPoint(p2, w, h, baseline));
            }
            var head = CometPoint(p, w, h, baseline);
            Glow(dc, head.X, head.Y, 18, color, _theme.Nebula * 0.65);
            Sparkle(dc, head, 4.2, color, _theme.Stars);
        }

        private static Point CometPoint(double p, double w, double h, double baseline) =>
            new((-0.10 + p * 1.20) * w, (baseline - p * 0.20 + Math.Sin(p * Math.PI * 2) * 0.038) * h);

        private Brush BuildVignette()
        {
            var brush = new RadialGradientBrush
            {
                Center = new Point(0.52, 0.45), GradientOrigin = new Point(0.52, 0.45),
                RadiusX = 0.78, RadiusY = 0.78,
                GradientStops = new GradientStopCollection
                {
                    new(Color.FromArgb(0, _theme.Vignette.R, _theme.Vignette.G, _theme.Vignette.B), 0),
                    new(Color.FromArgb(0, _theme.Vignette.R, _theme.Vignette.G, _theme.Vignette.B), 0.60),
                    new(WithAlpha(_theme.Vignette, _theme.VignetteAlpha), 1)
                }
            };
            brush.Freeze();
            return brush;
        }

        private static StreamGeometry Ellipse(double rx, double ry, bool front)
        {
            var points = new List<Point>();
            int count = front ? 64 : 128;
            double start = front ? 0.05 : 0;
            double end = front ? Math.PI * 0.94 : Math.PI * 2;
            for (int i = 0; i <= count; i++)
            {
                double a = start + (end - start) * i / count;
                points.Add(new Point(Math.Cos(a) * rx, Math.Sin(a) * ry));
            }
            return Polygon(points.ToArray(), !front);
        }

        private static StreamGeometry Diamond() => Polygon(new[]
        {
            new Point(0, -7), new Point(4.5, 0), new Point(0, 7), new Point(-4.5, 0)
        });

        private static StreamGeometry Polygon(Point[] points, bool closed = true)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(points[0], true, closed);
                var rest = new List<Point>(points.Length - 1);
                for (int i = 1; i < points.Length; i++) rest.Add(points[i]);
                ctx.PolyLineTo(rest, true, true);
            }
            geometry.Freeze();
            return geometry;
        }

        private void Sparkle(DrawingContext dc, Point center, double radius, Color color, double alpha)
        {
            dc.DrawEllipse(Solid(color, alpha * 0.19), null, center, radius * 1.9, radius * 1.9);
            var pen = new Pen(Solid(_theme.PlanetLight, alpha * 0.85), 0.75);
            dc.DrawLine(pen, new Point(center.X - radius, center.Y), new Point(center.X + radius, center.Y));
            dc.DrawLine(pen, new Point(center.X, center.Y - radius), new Point(center.X, center.Y + radius));
            dc.DrawEllipse(Solid(_theme.PlanetLight, alpha), null, center, 0.8, 0.8);
        }

        private static void Glow(DrawingContext dc, double x, double y, double radius, Color color, double alpha)
        {
            if (alpha < 0.001 || radius < 1) return;
            var brush = new RadialGradientBrush
            {
                GradientStops = new GradientStopCollection
                {
                    new(WithAlpha(color, alpha), 0),
                    new(WithAlpha(color, alpha * 0.44), 0.42),
                    new(WithAlpha(color, 0), 1)
                }
            };
            dc.DrawEllipse(brush, null, new Point(x, y), radius, radius);
        }

        private static Brush Gradient(Color a, Color b, double width, double alpha)
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(1, 0),
                GradientStops = new GradientStopCollection
                {
                    new(Color.FromArgb(0, a.R, a.G, a.B), 0),
                    new(WithAlpha(a, alpha), 0.35),
                    new(WithAlpha(b, alpha * 0.82), 0.74),
                    new(Color.FromArgb(0, b.R, b.G, b.B), 1)
                }
            };
            brush.Freeze();
            return brush;
        }

        private SolidColorBrush Solid(Color color, double alpha)
        {
            int q = (int)Math.Round(Math.Clamp(alpha, 0, 1) * 96);
            var key = (color, q);
            if (_solids.TryGetValue(key, out var brush)) return brush;
            brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(q * 255 / 96.0), color.R, color.G, color.B));
            brush.Freeze();
            _solids[key] = brush;
            return brush;
        }

        private static Color WithAlpha(Color color, double alpha) =>
            Color.FromArgb((byte)(255 * Math.Clamp(alpha, 0, 1)), color.R, color.G, color.B);

        private static Color C(string hex) => (Color)ColorConverter.ConvertFromString("#" + hex)!;
    }
}
