using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace SoulLink.BackdropPreview
{
    /// <summary>预览用色板（少女感 + 科技感）：薰衣草紫 / 樱花粉 / 冰蓝 / 薄荷 / 星光金。</summary>
    internal static class Pal
    {
        public static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

        public static readonly Color Lilac = C("#C6A8FF");
        public static readonly Color Violet = C("#8B6BFF");
        public static readonly Color Pink = C("#FF9EC9");
        public static readonly Color HotPink = C("#FF6FB5");
        public static readonly Color Ice = C("#8FE9FF");
        public static readonly Color Cyan = C("#4FE3FF");
        public static readonly Color Mint = C("#A8FFE4");
        public static readonly Color Gold = C("#FFE3B0");
        public static readonly Color White = Colors.White;
    }

    internal static class Fx
    {
        private static readonly Dictionary<(uint, int), Brush> RadCache = new();
        private static StreamGeometry? _heart, _star4;

        /// <summary>#RRGGBB 便捷取色。</summary>
        public static Color C(string hex) => Pal.C(hex);

        public static Color Lerp(Color a, Color b, double t)
        {
            if (t <= 0) return a;
            if (t >= 1) return b;
            return Color.FromRgb(
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));
        }

        /// <summary>柔光球形画刷（径向渐变，中心亮、边缘透明），按颜色+透明度量化缓存。</summary>
        public static Brush GlowBrush(Color c, double alpha, double midStop = 0.42)
        {
            int q = (int)Math.Round(Math.Clamp(alpha, 0, 1) * 64);
            var key = (((uint)c.R << 24) | ((uint)c.G << 16) | ((uint)c.B << 8) | 0xFFu, q);
            if (RadCache.TryGetValue(key, out var cached)) return cached;
            double a = q / 64.0;
            var b = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.5),
                GradientOrigin = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5
            };
            b.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * a), c.R, c.G, c.B), 0));
            b.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(150 * a), c.R, c.G, c.B), midStop));
            b.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(40 * a), c.R, c.G, c.B), 0.78));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
            b.Freeze();
            RadCache[key] = b;
            return b;
        }

        public static void Blob(DrawingContext dc, double cx, double cy, double r, Color c, double alpha, double midStop = 0.42)
        {
            if (alpha <= 0.002 || r <= 0.5) return;
            dc.DrawEllipse(GlowBrush(c, alpha, midStop), null, new Point(cx, cy), r, r);
        }

        public static void Dot(DrawingContext dc, double cx, double cy, double r, Color c, double alpha)
        {
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(alpha, 0, 1)), c.R, c.G, c.B)), null,
                           new Point(cx, cy), r, r);
        }

        public static StreamGeometry HeartGeometry()
        {
            if (_heart != null) return _heart;
            var pts = new List<Point>();
            const int n = 96;
            for (int i = 0; i < n; i++)
            {
                double a = i / (double)n * Math.PI * 2;
                double x = 16 * Math.Pow(Math.Sin(a), 3);
                double y = -(13 * Math.Cos(a) - 5 * Math.Cos(2 * a) - 2 * Math.Cos(3 * a) - Math.Cos(4 * a));
                pts.Add(new Point(x / 18.0, y / 18.0));
            }
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(pts[0], true, true);
                ctx.PolyLineTo(pts.GetRange(1, pts.Count - 1), true, false);
            }
            g.Freeze();
            _heart = g;
            return g;
        }

        public static StreamGeometry StarGeometry()
        {
            if (_star4 != null) return _star4;
            // 四角星（十字闪光）：长轴 1，腰 0.26，带一点内凹弧线感
            var pts = new List<Point>
            {
                new(0, -1), new(0.13, -0.30), new(1, 0), new(0.13, 0.30),
                new(0, 1), new(-0.13, 0.30), new(-1, 0), new(-0.13, -0.30)
            };
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(pts[0], true, true);
                ctx.PolyLineTo(pts.GetRange(1, pts.Count - 1), true, true);
            }
            g.Freeze();
            _star4 = g;
            return g;
        }

        public static void Heart(DrawingContext dc, double cx, double cy, double size, double rot, Color c, double alpha)
        {
            if (alpha <= 0.003 || size <= 0.5) return;
            dc.PushTransform(new TransformGroup
            {
                Children =
                {
                    new RotateTransform(rot),
                    new ScaleTransform(size, size),
                    new TranslateTransform(cx, cy)
                }
            });
            var brush = new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(alpha, 0, 1)), c.R, c.G, c.B));
            brush.Freeze();
            dc.DrawGeometry(brush, null, HeartGeometry());
            dc.Pop();
        }

        public static void Star(DrawingContext dc, double cx, double cy, double size, double rot, Color c, double alpha)
        {
            if (alpha <= 0.003 || size <= 0.3) return;
            dc.PushTransform(new TransformGroup
            {
                Children =
                {
                    new RotateTransform(rot),
                    new ScaleTransform(size, size),
                    new TranslateTransform(cx, cy)
                }
            });
            var brush = new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(alpha, 0, 1)), c.R, c.G, c.B));
            brush.Freeze();
            dc.DrawGeometry(brush, null, StarGeometry());
            dc.Pop();
        }

        /// <summary>闪光星：中心核 + 柔光晕 + 四角星芒。</summary>
        public static void Sparkle(DrawingContext dc, double x, double y, double size, double rot, Color c, double alpha)
        {
            if (alpha <= 0.004) return;
            Blob(dc, x, y, size * 2.6, c, alpha * 0.35, 0.30);
            Star(dc, x, y, size, rot, c, alpha * 0.9);
            Star(dc, x, y, size * 0.55, rot + 45, Pal.White, alpha * 0.55);
        }

        /// <summary>加一条柔和的光带（贝塞尔/折线），用三层描边模拟发光。</summary>
        public static void GlowPolyline(DrawingContext dc, IList<Point> pts, Color c, double width, double alpha)
        {
            if (pts.Count < 2) return;
            var rest = new List<Point>(pts.Count - 1);
            for (int i = 1; i < pts.Count; i++) rest.Add(pts[i]);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(pts[0], false, false);
                ctx.PolyLineTo(rest, true, true);
            }
            g.Freeze();
            for (int layer = 0; layer < 3; layer++)
            {
                double wMul = layer == 0 ? 5.0 : layer == 1 ? 2.2 : 1.0;
                double aMul = layer == 0 ? 0.10 : layer == 1 ? 0.22 : 0.85;
                var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(alpha * aMul, 0, 1)), c.R, c.G, c.B)), width * wMul)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round
                };
                pen.Freeze();
                dc.DrawGeometry(null, pen, g);
            }
        }

        /// <summary>细颗粒噪点（消除渐变色带，提升"电影感"）。</summary>
        private static ImageBrush? _grain;
        public static ImageBrush GrainBrush()
        {
            if (_grain != null) return _grain;
            const int n = 128;
            var pixels = new byte[n * n * 4];
            var rnd = new Random(9173);
            for (int i = 0; i < n * n; i++)
            {
                byte v = (byte)rnd.Next(120, 255);
                pixels[i * 4 + 0] = v;
                pixels[i * 4 + 1] = v;
                pixels[i * 4 + 2] = v;
                pixels[i * 4 + 3] = (byte)rnd.Next(0, 26);
            }
            var bmp = System.Windows.Media.Imaging.BitmapSource.Create(n, n, 96, 96, PixelFormats.Bgra32, null, pixels, n * 4);
            bmp.Freeze();
            var ib = new ImageBrush(bmp)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, n, n),
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.None
            };
            ib.Freeze();
            _grain = ib;
            return ib;
        }

        public static void Grain(DrawingContext dc, double w, double h, double t, double alpha)
        {
            var brush = GrainBrush().Clone();
            brush.Transform = new TranslateTransform((t * 53) % 128, (t * 37) % 128);
            brush.Opacity = alpha;
            brush.Freeze();
            dc.DrawRectangle(brush, null, new Rect(0, 0, w, h));
        }

        public static LinearGradientBrush VBase(Color top, Color mid, Color bottom, double midOffset = 0.5)
        {
            var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            b.GradientStops.Add(new GradientStop(top, 0));
            b.GradientStops.Add(new GradientStop(mid, midOffset));
            b.GradientStops.Add(new GradientStop(bottom, 1));
            b.Freeze();
            return b;
        }

        /// <summary>四边压暗的暗角，让中间的内容卡片更跳。</summary>
        public static void Vignette(DrawingContext dc, double w, double h, double strength = 0.0)
        {
            var b = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.5),
                GradientOrigin = new Point(0.5, 0.5),
                RadiusX = 0.78,
                RadiusY = 0.78
            };
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.55));
            b.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(150 + 60 * strength), 0, 0, 0), 1));
            b.Freeze();
            dc.DrawRectangle(b, null, new Rect(0, 0, w, h));
        }


        // ── 柔边彩带 ──
        // 色阶产生的原因：嵌套多边形的**边界**处透明度突变（Δalpha 很大）。
        // 解法：层数够多（24 层）+ 宽度几何递增 + 透明度线性收尾，
        //      相邻两层之间 Δalpha ≈ total/24，边界对比度低到看不出来；
        //      横向颜色由同一支画刷给出，所以叠多少层颜色都一致，不会串色。
        private const int RibbonPasses = 24;
        private const double RibbonMaxScale = 3.1;

        public static void SoftRibbon(DrawingContext dc, Point[] mid, double[] halfW,
            Color ca, Color cb, Color cc, double alpha)
        {
            int n = mid.Length;
            if (n < 2 || alpha <= 0.002) return;

            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, ca.R, ca.G, ca.B), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xEE, ca.R, ca.G, ca.B), 0.12));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, ca.R, ca.G, ca.B), 0.30));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, cb.R, cb.G, cb.B), 0.50));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, cc.R, cc.G, cc.B), 0.72));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, cc.R, cc.G, cc.B), 1));
            brush.Freeze();

            // 由外向内逐层叠加：每层都覆盖上一层，累计透明度逼近"中心亮、边缘透明"
            for (int p = RibbonPasses - 1; p >= 0; p--)
            {
                double u = p / (double)(RibbonPasses - 1);          // 0=内层(窄) 1=外层(宽)
                double scale = Math.Pow(RibbonMaxScale, u);
                double layerAlpha = alpha * 1.9 / RibbonPasses * (1.0 - u * 0.88);

                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(new Point(mid[0].X, mid[0].Y - halfW[0] * scale), true, true);
                    var tr = new List<Point>(n - 1);
                    for (int i = 1; i < n; i++) tr.Add(new Point(mid[i].X, mid[i].Y - halfW[i] * scale));
                    ctx.PolyLineTo(tr, true, true);
                    var br = new List<Point>(n);
                    for (int i = n - 1; i >= 0; i--) br.Add(new Point(mid[i].X, mid[i].Y + halfW[i] * scale));
                    ctx.PolyLineTo(br, true, true);
                }
                geo.Freeze();

                dc.PushOpacity(layerAlpha);
                dc.DrawGeometry(brush, null, geo);
                dc.Pop();
            }

            // 核心亮线：只在中轴附近，随两端自然消失
            var core = new List<Point>(mid);
            double chw = halfW[halfW.Length / 2];
            Fx.GlowPolyline(dc, core, Lerp(cb, Colors.White, 0.45), Math.Max(0.8, chw * 0.05), alpha * 0.22);
        }

        /// <summary>生成一条彩带的中心线与半宽（两端收窄，中段饱满）。</summary>
        public static (Point[] mid, double[] halfW) BuildRibbon(double w, double y0, double amp,
            double freq, double phase, double thick, int n = 64)
        {
            var mid = new Point[n];
            var halfW = new double[n];
            for (int i = 0; i < n; i++)
            {
                double u = i / (double)(n - 1);
                double x = -w * 0.30 + u * w * 1.60;
                double y = y0
                    + Math.Sin(u * freq * Math.PI * 2 + phase) * amp
                    + Math.Sin(u * freq * 2.6 * Math.PI * 2 + phase * 1.7) * amp * 0.26
                    + Math.Sin(u * freq * 5.3 * Math.PI * 2 + phase * 2.9) * amp * 0.08;
                mid[i] = new Point(x, y);
                double taper = Math.Pow(Math.Sin(u * Math.PI), 0.65);
                halfW[i] = thick * 0.5 * (0.10 + 0.90 * taper);
            }
            return (mid, halfW);
        }

        /// <summary>樱花花瓣几何（0..1 归一化，朝上）。</summary>
        private static StreamGeometry? _petal;
        public static StreamGeometry PetalGeometry()
        {
            if (_petal != null) return _petal;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(0, -1), true, true);
                ctx.BezierTo(new Point(0.62, -0.85), new Point(0.56, 0.30), new Point(0.07, 1.0), true, true);
                // 花瓣顶端的小缺口
                ctx.LineTo(new Point(0.0, 0.80), true, true);
                ctx.LineTo(new Point(-0.07, 1.0), true, true);
                ctx.BezierTo(new Point(-0.56, 0.30), new Point(-0.62, -0.85), new Point(0, -1), true, true);
            }
            g.Freeze();
            _petal = g;
            return g;
        }

        public static void Petal(DrawingContext dc, double x, double y, double size, double rot,
            Color inner, Color outer, double alpha, double squash = 1.0)
        {
            if (alpha <= 0.004) return;
            dc.PushTransform(new TransformGroup
            {
                Children =
                {
                    new RotateTransform(rot),
                    new ScaleTransform(size, size * squash),
                    new TranslateTransform(x, y)
                }
            });
            var b = new LinearGradientBrush { StartPoint = new Point(0.15, 0), EndPoint = new Point(0.55, 1) };
            b.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * Math.Clamp(alpha, 0, 1)), inner.R, inner.G, inner.B), 0));
            b.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * Math.Clamp(alpha * 0.80, 0, 1)), outer.R, outer.G, outer.B), 1));
            b.Freeze();
            var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(alpha * 0.55, 0, 1)),
                (byte)Math.Min(255, inner.R + 40), (byte)Math.Min(255, inner.G + 30), (byte)Math.Min(255, inner.B + 30))), 0.055);
            pen.Freeze();
            dc.DrawGeometry(b, pen, PetalGeometry());
            dc.Pop();
        }

        public static double Hash(int i, int salt)
        {
            unchecked
            {
                uint x = (uint)(i * 2654435761u + salt * 40503u);
                x ^= x >> 13; x *= 2654435761u; x ^= x >> 17;
                return (x & 0xFFFFFF) / (double)0x1000000;
            }
        }
    }
}