using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SoulLink.Controls;

/// <summary>月莓星轨 / 云端棱镜：柔和星海与晶体星核的双模式动态背景。</summary>
public sealed class DreamScene
{
    private sealed class Palette
    {
        public Color Top, Middle, Bottom, Pink, Lilac, Blue, Mint, Ink, White, Shade;
        public double Dust, Mist, Line;
        public Palette(bool light)
        {
            if (light)
            {
                Top = C("FFF9FD"); Middle = C("F7F1FF"); Bottom = C("EAF7FF");
                Pink = C("EF9FC6"); Lilac = C("B39AE5"); Blue = C("8ACCEB");
                Mint = C("9BDFD4"); Ink = C("776298"); White = C("FFFFFF"); Shade = C("9E8DC6");
                Dust = .42; Mist = .62; Line = .40;
            }
            else
            {
                Top = C("090919"); Middle = C("17132F"); Bottom = C("091A2C");
                Pink = C("FF8BCF"); Lilac = C("B397FF"); Blue = C("7FE7FF");
                Mint = C("93F5DA"); Ink = C("E6D9FF"); White = C("FFF5FF"); Shade = C("020613");
                Dust = .88; Mist = .52; Line = .48;
            }
        }
    }

    private readonly Dictionary<(Color, int), SolidColorBrush> _solids = new();
    private readonly Dictionary<(Color, int), ImageBrush> _glows = new();
    private readonly StreamGeometry _heart = Heart();
    private readonly StreamGeometry _star = Star();
    private readonly StreamGeometry _petal = Petal();
    private readonly StreamGeometry _moon = Crescent();
    private DrawingGroup? _nightBase;
    private DrawingGroup? _dayBase;
    private double _baseWidth, _baseHeight;

    // ★★ 2.17.46 性能缓存。★★
    //   实测（2026-11-21，交接文档第 7 节）：用户 EffectTier=2（低档），
    //   动态背景早已关闭，但 CPU 仍有 45–50%，且最小化后掉到 11.7%
    //   —— 全部是 WPF 渲染（RenderRecursive 19.6% + PrecomputeRecursive）。
    //
    //   根因：低档每 1000ms 仍会 InvalidateVisual 一次（目的是刷窗口外沿那圈
    //   没被内容覆盖的 8px，见 AuroraBackdrop 注释），而 OnRender 会把整个
    //   场景**从零重建**：3 条贝塞尔曲线、3×24 层描边（每层一个
    //   LinearGradientBrush + Pen）、76 颗粒子、轨道系统的 2 个 120 点椭圆、
    //   56 条刻度线、EdgeShade 的渐变画刷、Grain 的 ImageBrush Clone……
    //   低档下 _time 根本不推进，所以**重建出来的画面逐像素完全相同**，纯浪费。
    //
    //   现在：静止档把整帧烘焙成冻结的 DrawingGroup，重绘退化为一次
    //   DrawDrawing；动效档则把「不随时间变化的几何/画刷」全部缓存，每帧只剩
    //   若干 PushTransform + DrawDrawing。
    private static readonly Palette NightPalette = new(false);
    private static readonly Palette DayPalette = new(true);

    /// <summary>丝带烘焙结果，按 (宽,高,亮/暗) 缓存。丝带几何只依赖尺寸，与时间无关。</summary>
    private readonly Dictionary<(int, int, bool), DrawingGroup[]> _ribbonCache = new();
    /// <summary>轨道月亮头部径向渐变，按颜色缓存（原来每帧 new 一个）。</summary>
    private readonly Dictionary<Color, RadialGradientBrush> _orbitHeadCache = new();
    private RadialGradientBrush? _shadeNight, _shadeDay;
    private ImageBrush? _grainSoft, _grainStrong;

    private DrawingGroup? _staticFrame;
    private double _staticWidth, _staticHeight;
    private bool _staticLight;

    private static Color C(string value) => (Color)ColorConverter.ConvertFromString("#" + value)!;
    private static byte Alpha(double value) => (byte)(Math.Clamp(value, 0, 1) * 255);
    private static Color A(Color color, double alpha) => Color.FromArgb(Alpha(alpha), color.R, color.G, color.B);
    private static double Wave(double t, double speed = 1) => Math.Sin(t * speed);
    private static double Mod(double value) => (value % 1 + 1) % 1;
    private static double Hash(int i, int salt)
    {
        unchecked
        {
            uint n = (uint)(i * 2654435761u + salt * 2246822519u);
            n ^= n >> 15; n *= 2246822519u; n ^= n >> 13;
            return (n & 0xFFFFFF) / 16777216.0;
        }
    }

    private SolidColorBrush Solid(Color color, double alpha)
    {
        int step = (int)Math.Round(Math.Clamp(alpha, 0, 1) * 128);
        var key = (color, step);
        if (_solids.TryGetValue(key, out var brush)) return brush;
        brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(step * 255.0 / 128), color.R, color.G, color.B));
        brush.Freeze(); _solids[key] = brush;
        return brush;
    }

    private ImageBrush GlowBrush(Color color, double alpha)
    {
        int step=(int)Math.Round(Math.Clamp(alpha,0,1)*40);
        var key=(color,step);
        if(_glows.TryGetValue(key,out var brush))return brush;
        const int size=96;
        var pixels=new byte[size*size*4];
        double strength=step/40.0,falloff=Math.Exp(-5.5);
        for(int y=0;y<size;y++)for(int x=0;x<size;x++)
        {
            double dx=(x+.5)/size*2-1,dy=(y+.5)/size*2-1;
            double d=Math.Min(1,Math.Sqrt(dx*dx+dy*dy));
            double opacity=Math.Max(0,(Math.Exp(-d*d*5.5)-falloff)/(1-falloff))*strength;
            int a=(int)Math.Round(opacity*255),i=(y*size+x)*4;
            pixels[i]=(byte)(color.B*a/255);pixels[i+1]=(byte)(color.G*a/255);
            pixels[i+2]=(byte)(color.R*a/255);pixels[i+3]=(byte)a;
        }
        var bitmap=BitmapSource.Create(size,size,96,96,PixelFormats.Pbgra32,null,pixels,size*4);
        bitmap.Freeze();brush=new ImageBrush(bitmap){Stretch=Stretch.Fill};brush.Freeze();
        _glows[key]=brush;return brush;
    }

    private void Glow(DrawingContext dc, double x, double y, double radius, Color color, double alpha)
    {
        if (radius < .5 || alpha < .002) return;
        dc.DrawRectangle(GlowBrush(color,alpha),null,new Rect(x-radius,y-radius,radius*2,radius*2));
    }

    private static StreamGeometry Curve(IList<Point> points)
    {
        var g = new StreamGeometry();
        using (var dc = g.Open())
        {
            dc.BeginFigure(points[0], false, false);
            for (int i = 1; i + 2 < points.Count; i += 3)
                dc.BezierTo(points[i], points[i + 1], points[i + 2], true, true);
        }
        g.Freeze(); return g;
    }

    private static StreamGeometry Polygon(IList<Point> points, bool closed = true)
    {
        var g = new StreamGeometry();
        using (var dc = g.Open())
        {
            dc.BeginFigure(points[0], true, closed);
            for (int i = 1; i < points.Count; i++) dc.LineTo(points[i], true, true);
        }
        g.Freeze(); return g;
    }

    private static StreamGeometry Ellipse(double rx, double ry, double from = 0, double to = Math.PI * 2)
    {
        var points = new List<Point>();
        for (int i = 0; i <= 120; i++)
        {
            double angle = from + (to - from) * i / 120;
            points.Add(new Point(Math.Cos(angle) * rx, Math.Sin(angle) * ry));
        }
        return Polygon(points, Math.Abs(to - from - Math.PI * 2) < .01);
    }

    private static StreamGeometry Crescent()
    {
        var g=new StreamGeometry();
        using(var dc=g.Open())
        {
            dc.BeginFigure(new Point(.28,-1),true,true);
            dc.BezierTo(new Point(-.95,-.65),new Point(-.95,.65),new Point(.28,1),true,true);
            dc.BezierTo(new Point(-.45,.58),new Point(-.45,-.58),new Point(.28,-1),true,true);
        }
        g.Freeze();return g;
    }

    private static StreamGeometry Heart()
    {
        var points = new List<Point>();
        for (int i = 0; i < 96; i++)
        {
            double a = i / 96.0 * Math.PI * 2;
            points.Add(new Point(16 * Math.Pow(Math.Sin(a), 3) / 18,
                -(13 * Math.Cos(a) - 5 * Math.Cos(2 * a) - 2 * Math.Cos(3 * a) - Math.Cos(4 * a)) / 18));
        }
        return Polygon(points);
    }

    private static StreamGeometry Star() => Polygon(new[]
    {
        new Point(0,-1), new Point(.11,-.22), new Point(1,0), new Point(.11,.22),
        new Point(0,1), new Point(-.11,.22), new Point(-1,0), new Point(-.11,-.22)
    });

    private static StreamGeometry Petal()
    {
        var g = new StreamGeometry();
        using (var dc = g.Open())
        {
            dc.BeginFigure(new Point(0,-1), true, true);
            dc.BezierTo(new Point(.68,-.8), new Point(.65,.35), new Point(.16,.85), true, true);
            dc.LineTo(new Point(0,.65), true, true);
            dc.LineTo(new Point(-.16,.85), true, true);
            dc.BezierTo(new Point(-.65,.35), new Point(-.68,-.8), new Point(0,-1), true, true);
        }
        g.Freeze(); return g;
    }

    private void Sparkle(DrawingContext dc, double x, double y, double size, double angle, Color color, double alpha, Palette p)
    {
        Glow(dc, x, y, size * 2.5, color, alpha * .28);
        dc.PushTransform(new TranslateTransform(x,y));
        dc.PushTransform(new RotateTransform(angle));
        dc.PushTransform(new ScaleTransform(size,size));
        dc.DrawGeometry(Solid(color,alpha),null,_star);
        dc.Pop(); dc.Pop(); dc.Pop();
        dc.DrawEllipse(Solid(p.White, alpha * .85),null,new Point(x,y),.65,.65);
    }

    private static Brush Linear(Color a, Color b, Color c, double alpha, bool vertical = false)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0,0), EndPoint = vertical ? new Point(0,1) : new Point(1,0),
            GradientStops = new GradientStopCollection
            {
                new(A(a,0),0), new(A(a,alpha),.16), new(A(b,alpha),.48),
                new(A(c,alpha * .86),.78), new(A(c,0),1)
            }
        };
        brush.Freeze(); return brush;
    }

    /// <summary>
    /// 把一条丝带烘焙成冻结的 DrawingGroup（24 层描边）。
    ///
    /// ★ 2.17.46：原来每帧都 new 24 个 LinearGradientBrush + 24 个 Pen 再逐层
    ///   DrawGeometry，三条丝带 = 每帧 72 次画刷/画笔构造 + 72 次几何描边。
    ///   丝带的形状和颜色都只依赖尺寸，与时间无关；时间只影响外层的
    ///   TranslateTransform。所以烘焙一次、终身复用，每帧只剩 Push/Pop + DrawDrawing。
    /// </summary>
    private DrawingGroup BakeRibbon(StreamGeometry curve, Palette p, double opacity, double width = 1)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            for(int i=0;i<24;i++)
            {
                double inner=i/23.0;
                double thickness=(8+124*(1-inner))*width;
                double alpha=opacity*(.0025+.0145*inner*inner);
                var pen=new Pen(Linear(p.Blue,p.Lilac,p.Pink,alpha),thickness)
                { StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round };
                pen.Freeze();
                // 外层几何缓存后不能再被冻结画刷以外的对象引用，这里直接复用同一份 curve。
                dc.DrawGeometry(null,pen,curve);
            }
        }
        group.Freeze();
        return group;
    }

    private DrawingGroup[] Ribbons(double w, double h, bool light, Palette p)
    {
        var key = ((int)Math.Round(w), (int)Math.Round(h), light);
        if (_ribbonCache.TryGetValue(key, out var cached)) return cached;

        DrawingGroup[] baked;
        if (light)
        {
            var a=Curve(new[]{P(-.12,.77,w,h),P(.14,.47,w,h),P(.35,.82,w,h),P(.58,.61,w,h),P(.78,.40,w,h),P(.96,.48,w,h),P(1.13,.28,w,h)});
            var b=Curve(new[]{P(-.08,.95,w,h),P(.22,.64,w,h),P(.44,.93,w,h),P(.71,.78,w,h),P(.92,.64,w,h),P(.98,.70,w,h),P(1.14,.60,w,h)});
            baked = new[] { BakeRibbon(a,p,.35,.80), BakeRibbon(b,p,.26,.60) };
        }
        else
        {
            var a=Curve(new[]{P(-.14,.72,w,h),P(.15,.20,w,h),P(.30,.95,w,h),P(.58,.64,w,h),P(.79,.39,w,h),P(.89,.43,w,h),P(1.14,.19,w,h)});
            var b=Curve(new[]{P(-.13,.89,w,h),P(.17,.59,w,h),P(.37,1.03,w,h),P(.67,.78,w,h),P(.89,.60,w,h),P(.96,.80,w,h),P(1.13,.53,w,h)});
            var c=Curve(new[]{P(.37,-.13,w,h),P(.57,.20,w,h),P(.74,-.02,w,h),P(.90,.18,w,h),P(1.05,.37,w,h),P(1.02,.17,w,h),P(1.16,.38,w,h)});
            baked = new[] { BakeRibbon(a,p,.96), BakeRibbon(b,p,.78), BakeRibbon(c,p,.50,.62) };
        }

        // 尺寸会随窗口拖拽变化，缓存不能无限增长。
        if (_ribbonCache.Count > 6) _ribbonCache.Clear();
        _ribbonCache[key] = baked;
        return baked;
    }

    private DrawingGroup Base(double w, double h, Palette p, bool light)
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            var background = new LinearGradientBrush
            {
                StartPoint = new Point(0,0), EndPoint = new Point(1,1),
                GradientStops = new GradientStopCollection
                {
                    new(p.Top,0), new(p.Middle,.48), new(p.Bottom,1)
                }
            };
            dc.DrawRectangle(background,null,new Rect(0,0,w,h));
            double unit = Math.Max(w,h);
            Glow(dc,w*.13,h*.19,unit*.49,p.Lilac,p.Mist*.62);
            Glow(dc,w*.82,h*.44,unit*.45,p.Pink,p.Mist*.55);
            Glow(dc,w*.95,h*.05,unit*.40,p.Blue,p.Mist*.70);
            Glow(dc,w*.36,h*1.00,unit*.40,light?p.Mint:p.Blue,p.Mist*.45);
            if(!light)
            {
                // 斜向银河：彩色星雾 + 中央星尘带 + 稀疏明亮恒星，全部按尺寸缓存。
                dc.PushTransform(new RotateTransform(-19,w*.51,h*.47));
                Glow(dc,w*.48,h*.48,w*.58,p.Lilac,.13);
                Glow(dc,w*.58,h*.48,w*.40,p.Blue,.11);
                Glow(dc,w*.43,h*.49,w*.32,p.Pink,.09);
                dc.Pop();
                for(int i=0;i<620;i++)
                {
                    double x=Hash(i,613)*w;
                    double center=h*(.73-x/w*.35);
                    double spread=(Hash(i,617)+Hash(i,619)+Hash(i,631)-1.5);
                    double y=center+spread*h*.24;
                    double strength=1-Math.Min(1,Math.Abs(spread)/1.5);
                    double radius=.25+Hash(i,641)*.62;
                    dc.DrawEllipse(Solid(i%5==0?p.Pink:i%3==0?p.Blue:p.Lilac,.075+strength*.30),null,new Point(x,y),radius,radius);
                }
                SpiralGalaxy(dc,w*.28,h*.64,Math.Min(w,h)*.205,p);
                SpiralGalaxy(dc,w*.86,h*.15,Math.Min(w,h)*.07,p);
            }
            if (light)
            {
                // Day mode is pearlescent, not an inverted star field.
                Glow(dc,w*.53,h*.30,unit*.25,p.White,.54);
                Glow(dc,w*.76,h*.65,unit*.23,p.White,.32);
            }
            else
            {
                DrawConstellation(dc,w*.10,h*.23,Math.Min(w,h)*.09,p);
                DrawConstellation(dc,w*.92,h*.76,Math.Min(w,h)*.058,p);
            }
            int count = light ? 115 : 300;
            for (int i=0;i<count;i++)
            {
                double x=Hash(i,15)*w, y=Hash(i,37)*h;
                double radius=.25+Hash(i,53)*(light?.65:1.05);
                var color=i%4==0?p.Pink:i%3==0?p.Blue:p.Lilac;
                dc.DrawEllipse(Solid(color,p.Dust*(.08+Hash(i,91)*.30)),null,new Point(x,y),radius,radius);
            }
            // No bitmap texture: off-screen WPF tile rendering can introduce artifacts.
        }
        drawing.Freeze(); return drawing;
    }

    private void SpiralGalaxy(DrawingContext dc,double cx,double cy,double r,Palette p)
    {
        // 斜向旋涡星系：双旋臂、中央辉光与沿臂散开的细碎恒星。
        dc.PushTransform(new TranslateTransform(cx,cy));
        dc.PushTransform(new RotateTransform(-27));
        dc.PushTransform(new ScaleTransform(1,.47));
        Glow(dc,0,0,r*1.05,p.Lilac,.20);
        Glow(dc,0,0,r*.72,p.Blue,.14);
        Glow(dc,0,0,r*.25,p.White,.44);
        Glow(dc,0,0,r*.10,p.Pink,.37);
        for(int i=0;i<420;i++)
        {
            double radial=Math.Sqrt(Hash(i,701));
            double arm=i%2;
            double angle=radial*5.5+arm*Math.PI+(Hash(i,709)-.5)*.40;
            double scatter=(Hash(i,719)+Hash(i,727)-1)*r*.13*(.3+radial);
            double x=Math.Cos(angle)*radial*r+Math.Cos(angle+Math.PI/2)*scatter;
            double y=Math.Sin(angle)*radial*r+Math.Sin(angle+Math.PI/2)*scatter;
            double core=1-radial;
            var color=i%5==0?p.Blue:i%3==0?p.Pink:p.White;
            double alpha=.10+core*.32+Hash(i,733)*.14;
            dc.DrawEllipse(Solid(color,alpha),null,new Point(x,y),.25+Hash(i,739)*.72,.25+Hash(i,739)*.72);
        }
        dc.Pop();dc.Pop();dc.Pop();
        Sparkle(dc,cx,cy,3.4,18,p.White,.58,p);
    }

    private void DrawConstellation(DrawingContext dc, double x, double y, double r, Palette p)
    {
        var points = new[]
        {
            new Point(x-r,y+r*.12), new Point(x-r*.4,y-r*.42), new Point(x+r*.08,y+r*.12),
            new Point(x+r*.62,y-r*.55), new Point(x+r,y+r*.04)
        };
        var line = new Pen(Solid(p.Lilac,.12),.7); line.Freeze();
        var geo = Polygon(points,false);
        dc.DrawGeometry(null,line,geo);
        foreach(var point in points)
        {
            dc.DrawEllipse(Solid(p.Lilac,.16),null,point,3.0,3.0);
            dc.DrawEllipse(Solid(p.White,.63),null,point,.8,.8);
        }
    }

    private static ImageBrush? _grain;
    private void Grain(DrawingContext dc, double w, double h, bool light)
    {
        if (_grain == null)
        {
            const int size = 128;
            var pixels = new byte[size*size*4];
            var random = new Random(9341);
            for(int i=0;i<size*size;i++)
            {
                byte value=(byte)random.Next(140,255);
                pixels[i*4]=value; pixels[i*4+1]=value; pixels[i*4+2]=value;
                pixels[i*4+3]=(byte)random.Next(0,18);
            }
            var bitmap=BitmapSource.Create(size,size,96,96,PixelFormats.Bgra32,null,pixels,size*4);
            bitmap.Freeze();
            _grain=new ImageBrush(bitmap){TileMode=TileMode.Tile,Viewport=new Rect(0,0,size,size),ViewportUnits=BrushMappingMode.Absolute,Stretch=Stretch.None};
            _grain.Freeze();
        }
        // ★ 2.17.46：只在两个固定不透明度之间切换，Clone 一次缓存即可。
        if (light)
        {
            _grainSoft ??= BuildGrain(.25);
            dc.DrawRectangle(_grainSoft,null,new Rect(0,0,w,h));
        }
        else
        {
            _grainStrong ??= BuildGrain(.40);
            dc.DrawRectangle(_grainStrong,null,new Rect(0,0,w,h));
        }
    }

    private static ImageBrush BuildGrain(double opacity)
    {
        var brush=(ImageBrush)_grain!.Clone(); brush.Opacity=opacity; brush.Freeze(); return brush;
    }

    // ══════════════════════════════════════════════════════════════════
    //  ★★ 3.0.1 性能修复：静止档改用「位图」而不是「矢量 DrawingGroup」★★
    //
    //  实测（2026-11-21，本机 i9-13900HX / 2560×1368）：
    //    窗口显示、低档（静止）：92.8% 单核，其中 WPF 渲染线程独占 75%
    //    把 AuroraBackdrop 换成空 Canvas：24.5% 单核
    //    → 背景元素单独吃掉 68 个单核百分点
    //
    //  根因不在“重绘频率”而在“单帧成本”：
    //    dc.DrawDrawing(_staticFrame) 虽然复用了冻结的 DrawingGroup，
    //    但每次调用仍要把整个矢量场景**重新光栅化**一遍
    //    （3 条丝带 × 24 层描边 + 76 颗粒子 + 56 条刻度 + 边缘阴影）。
    //    这个成本与 DrawingGroup 是否冻结无关 —— 冻结只省了“构建”，
    //    省不掉“栅格化”。
    //
    //  反直觉现象也由此解释：低档 1Hz 最贵、高档 30fps 最便宜（55%），
    //  因为高档靠持续重绘让 WPF 保持脏区增量，低档每次都是整幅冷栅格化。
    //
    //  修法：静止画面本来就与时间无关，直接光栅化成 RenderTargetBitmap，
    //  之后每次重绘只是一次 DrawImage（位图贴图，GPU 直接走纹理）。
    //  再叠加「画面没变就完全不重绘」（见 AuroraBackdrop 的静止档），
    //  实际每秒重绘次数从 1 次降到 0 次。
    // ══════════════════════════════════════════════════════════════════

    private BitmapSource? _staticBitmap;
    private double _staticBmpWidth, _staticBmpHeight;
    private bool _staticBmpLight;

    /// <summary>
    /// 静止档：把整帧**光栅化成位图**缓存，之后每次重绘 = 一次 DrawImage。
    ///
    /// ★ 3.0.1 新增。原来是 DrawDrawing(冻结的 DrawingGroup)，
    ///   省了构建但省不掉光栅化，实测仍占 68 个单核百分点。
    ///
    /// ★ 位图尺寸按 DPI 放大：用 96 DPI 的位图在高 DPI 屏幕上会糊。
    ///   这里取源 DPI 换算，保证物理像素与屏幕一致。
    /// </summary>
    public bool TryDrawStaticBitmap(DrawingContext dc, double width, double height, bool light)
    {
        if (width < 2 || height < 2) return false;

        if (_staticBitmap == null
            || Math.Abs(_staticBmpWidth - width) > 0.5
            || Math.Abs(_staticBmpHeight - height) > 0.5
            || _staticBmpLight != light)
        {
            try
            {
                var p = light ? DayPalette : NightPalette;

                // 按屏幕 DPI 放大位图，避免高 DPI 下模糊
                double dpiX = 96, dpiY = 96;
                try
                {
                    var src = PresentationSource.FromVisual(_dpiProbe);
                    if (src?.CompositionTarget != null)
                    {
                        dpiX = 96.0 * src.CompositionTarget.TransformToDevice.M11;
                        dpiY = 96.0 * src.CompositionTarget.TransformToDevice.M22;
                    }
                }
                catch { }
                if (dpiX <= 0 || dpiX > 1000) dpiX = 96;
                if (dpiY <= 0 || dpiY > 1000) dpiY = 96;

                int pw = Math.Max(1, (int)Math.Ceiling(width * dpiX / 96.0));
                int ph = Math.Max(1, (int)Math.Ceiling(height * dpiY / 96.0));

                // 上限保护：超大位图会吃显存/内存，超过 4K 就退回矢量绘制
                if ((long)pw * ph > 4096L * 4096L)
                    return false;

                var visual = new DrawingVisual();
                using (var inner = visual.RenderOpen())
                    DrawScene(inner, width, height, 0, p, light);

                var bmp = new RenderTargetBitmap(pw, ph, dpiX, dpiY, PixelFormats.Pbgra32);
                bmp.Render(visual);
                bmp.Freeze();

                _staticBitmap = bmp;
                _staticBmpWidth = width;
                _staticBmpHeight = height;
                _staticBmpLight = light;
            }
            catch
            {
                // 位图渲染失败（显存不足/远程桌面等）就放弃，调用方退回矢量路径
                return false;
            }
        }

        dc.DrawImage(_staticBitmap, new Rect(0, 0, width, height));
        return true;
    }

    /// <summary>
    /// 静止档的**矢量回退**：位图路径不可用时（超大画布 / 显存不足 / RDP 环境）
    /// 退回原来的 DrawingGroup 方案。
    ///
    /// ★ 3.0.1：保留此方法作为兜底。它比位图贵（每次重绘都要栅格化），
    ///   但永远能用，不会因为 RenderTargetBitmap 失败而白屏。
    /// </summary>
    public bool TryDrawStatic(DrawingContext dc, double width, double height, bool light)
    {
        if (width < 2 || height < 2) return false;
        if (_staticFrame == null || _staticWidth != width || _staticHeight != height || _staticLight != light)
        {
            var p = light ? DayPalette : NightPalette;
            var frame = new DrawingGroup();
            using (var inner = frame.Open()) DrawScene(inner, width, height, 0, p, light);
            frame.Freeze();
            _staticFrame = frame; _staticWidth = width; _staticHeight = height; _staticLight = light;
        }
        dc.DrawDrawing(_staticFrame);
        return true;
    }

    /// <summary>用于取 DPI 的锚点元素（Visual 树里的任意元素都行）。</summary>
    private Visual? _dpiProbe;

    /// <summary>由 AuroraBackdrop 注入自身，供 TryDrawStaticBitmap 取 DPI。</summary>
    public void AttachDpiProbe(Visual probe) => _dpiProbe = probe;

    /// <summary>主题/尺寸变化时丢弃位图缓存。</summary>
    public void InvalidateStaticBitmap()
    {
        _staticBitmap = null;
    }

    private void DrawScene(DrawingContext dc, double width, double height, double time, Palette p, bool light)
    {
        if(_baseWidth!=width||_baseHeight!=height){_nightBase=null;_dayBase=null;_baseWidth=width;_baseHeight=height;}
        if(light){_dayBase??=Base(width,height,p,true);dc.DrawDrawing(_dayBase);}
        else{_nightBase??=Base(width,height,p,false);dc.DrawDrawing(_nightBase);}
        if(light) RenderDay(dc,width,height,time,p);
        else RenderNight(dc,width,height,time,p);
        EdgeShade(dc,width,height,p,light);
    }

    /// <summary>
    /// 精简度：0 = 完整，1 = 中等（中档动画用），2 = 最小。
    ///
    /// ★★ 3.0.1 新增，针对一个反直觉的实测结论：★★
    ///
    ///   中档（10fps）CPU 76.2%，高档（30fps）只有 27.8% —— 帧率低反而更贵。
    ///   根因不在帧数，而在**WPF 只有连续 60Hz 渲染时才启用帧间增量**；
    ///   10fps 这种「半动」节奏每帧都是整幅冷栅格化，成本 ≈ 高档的 3 倍/帧。
    ///
    ///   既然降帧率救不了，就降**单帧复杂度**：
    ///   中档把粒子 76→28、刻度 56→24、跳过星尘与流星，
    ///   视觉上仍是流动背景，但栅格化面积显著减少。
    /// </summary>
    public int Detail { get; set; }

    public void Render(DrawingContext dc, double width, double height, double time, bool light)
    {
        if(width<2||height<2)return;
        // ★ 2.17.46：Palette 原来是每帧 new 一个（含 10 个 Color 转换），
        //   但它只由 light/dark 决定，跟时间无关 —— 改成两个静态实例。
        DrawScene(dc, width, height, time, light ? DayPalette : NightPalette, light);
    }

    private void RenderNight(DrawingContext dc, double w, double h, double t, Palette p)
    {
        double u=Math.Min(w,h);
        // Silk aurora crosses the scene at different depths.
        // ★ 2.17.46：丝带已烘焙成冻结 DrawingGroup，每帧只推动变换。
        var ribbons = Ribbons(w,h,false,p);
        dc.PushTransform(new TranslateTransform(0,Wave(t,.22)*9)); dc.DrawDrawing(ribbons[0]); dc.Pop();
        dc.PushTransform(new TranslateTransform(0,Wave(t+.8,.19)*8)); dc.DrawDrawing(ribbons[1]); dc.Pop();
        dc.PushTransform(new TranslateTransform(Wave(t,.16)*5,0)); dc.DrawDrawing(ribbons[2]); dc.Pop();

        // Drifting, breathing motes.
        // ★ 3.0.1：按 Detail 分级 —— 中档减半、最小档再减。
        //   粒子是场景里栅格化成本最高的一块（每个都可能是渐变圆 + Sparkle）。
        int motes = Detail >= 2 ? 12 : Detail == 1 ? 28 : 76;
        int sparkleEvery = Detail >= 2 ? 0 : 13;   // 0 = 不画星芒
        for(int i=0;i<motes;i++)
        {
            double x=(Hash(i,141)+Wave(t+Hash(i,143)*6,.24)*.012)*w;
            double y=Mod(Hash(i,151)-t*(.003+Hash(i,157)*.006))*h;
            double twinkle=.5+.5*Wave(t+Hash(i,163)*6.28,1.05);
            var color=i%4==0?p.Pink:i%3==0?p.Blue:p.Lilac;
            double alpha=.13+twinkle*.58, r=.45+Hash(i,167)*1.1;
            if(i%4==0)Glow(dc,x,y,r*4,color,alpha*.23);
            dc.DrawEllipse(Solid(color,alpha),null,new Point(x,y),r,r);
            if(sparkleEvery > 0 && i%sparkleEvery==0)
                Sparkle(dc,x,y,3.0+twinkle*4.5,20+Wave(t,.2)*12,color,alpha*.84,p);
        }
        OrbitalSystem(dc,w,h,t,p);
        if (Detail == 0) ShootingStar(dc,w,h,t,p);   // 流星最贵，中/低档跳过

        // A handful of heart-shaped stardust; restrained rather than cute wallpaper.
        if (Detail == 0)
        {
            for(int i=0;i<5;i++)
            {
                double progress=Mod(Hash(i,193)+t*.008);
                double x=(.13+Hash(i,197)*.74+Wave(t+i,.18)*.014)*w;
                double y=(1.10-progress*1.18)*h;
                double alpha=Math.Sin(progress*Math.PI)*.28;
                Shape(dc,_heart,x,y,5+Hash(i,199)*4,Wave(t+i,.4)*18,Solid(i%2==0?p.Pink:p.Lilac,alpha),null);
            }
        }
    }

    private static Point P(double x,double y,double w,double h)=>new(x*w,y*h);

    private void OrbitalSystem(DrawingContext dc,double w,double h,double t,Palette p)
    {
        double cx=w*.775+Wave(t,.16)*5,cy=h*.455+Wave(t+.8,.13)*6;
        double unit=Math.Min(w,h),radius=unit*.245,r=unit*(.073+Wave(t,.75)*.0012);
        Glow(dc,cx,cy,radius*1.28,p.Lilac,.19);
        Glow(dc,cx,cy,r*2.0,p.Pink,.20);
        var fine=new Pen(Linear(p.Lilac,p.White,p.Blue,.44),.85); fine.Freeze();
        var glow=new Pen(Solid(p.Pink,.08),6); glow.Freeze();
        var dotted=new Pen(Solid(p.Blue,.18),.7){DashStyle=new DashStyle(new[]{1.0,5.0},0)}; dotted.Freeze();
        var ring=Ellipse(radius,radius*.32);
        var wide=Ellipse(radius*1.20,radius*.43);
        dc.PushTransform(new TranslateTransform(cx,cy));
        dc.PushTransform(new RotateTransform(34+t*.027));
        dc.DrawGeometry(null,dotted,wide); dc.Pop();dc.Pop();
        dc.PushTransform(new TranslateTransform(cx,cy));
        dc.PushTransform(new RotateTransform(-26+Wave(t,.14)*3));
        dc.DrawGeometry(null,glow,ring);dc.DrawGeometry(null,fine,ring);dc.Pop();dc.Pop();

        // ★ 3.0.1：刻度数量按 Detail 分级（56 / 24 / 12）。
        var ticks=new Pen(Solid(p.Lilac,.24),.7);ticks.Freeze();
        int tickCount = Detail >= 2 ? 12 : Detail == 1 ? 24 : 56;
        for(int i=0;i<tickCount;i++)
        {
            double angle=i/(double)tickCount*Math.PI*2+t*.014;
            double inner=radius*.77,length=i%7==0?7:i%2==0?3:1.5;
            dc.DrawLine(ticks,new Point(cx+Math.Cos(angle)*inner,cy+Math.Sin(angle)*inner),
                new Point(cx+Math.Cos(angle)*(inner+length),cy+Math.Sin(angle)*(inner+length)));
        }
        OrbitMoon(dc,cx,cy,radius*1.20,radius*.43,34+t*.027,t*.24+2.6,unit*.011,p.Blue,p);
        Crystal(dc,cx,cy,r,t,p);
        dc.PushTransform(new TranslateTransform(cx,cy));dc.PushTransform(new RotateTransform(-26+Wave(t,.14)*3));
        dc.DrawGeometry(null,glow,Ellipse(radius,radius*.32,.05,Math.PI*.94));
        dc.DrawGeometry(null,fine,Ellipse(radius,radius*.32,.05,Math.PI*.94));dc.Pop();dc.Pop();
        // Slowly travelling luminous arc gives the star system a softer sense of motion.
        double arcPhase=t*.12;
        var arcPen=new Pen(Solid(p.White,.54),1.1){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};arcPen.Freeze();
        dc.PushTransform(new TranslateTransform(cx,cy));dc.PushTransform(new RotateTransform(-26));
        dc.DrawGeometry(null,arcPen,Ellipse(radius*1.09,radius*.37,arcPhase,arcPhase+.72));dc.Pop();dc.Pop();
        Glow(dc,cx+Math.Cos(arcPhase+.72)*radius,cy+Math.Sin(arcPhase+.72)*radius*.4,16,p.Pink,.25);
        OrbitMoon(dc,cx,cy,radius,radius*.32,-26+Wave(t,.14)*3,-t*.30+.8,unit*.008,p.Pink,p);
        Sparkle(dc,cx+radius*.65,cy-radius*.70,7+Wave(t,.8)*1.6,t*8,p.White,.88,p);
        Sparkle(dc,cx-radius*.81,cy+radius*.24,4.2,-t*7,p.Pink,.64,p);
    }

    private void Crystal(DrawingContext dc,double cx,double cy,double r,double t,Palette p)
    {
        dc.PushTransform(new TranslateTransform(cx,cy));
        dc.PushTransform(new ScaleTransform(r,r));
        var sphere=new RadialGradientBrush
        {
            Center=new Point(.31,.25),GradientOrigin=new Point(.31,.25),RadiusX=.8,RadiusY=.8,
            GradientStops=new GradientStopCollection
            {
                new(C("FFF2FF"),0),new(C("E6B9F2"),.24),new(C("9C78D5"),.61),new(C("3E396F"),1)
            }
        };sphere.Freeze();
        dc.DrawEllipse(sphere,null,new Point(),1,1);
        Facet(dc,new[]{new Point(-.80,-.24),new Point(-.20,-.91),new Point(.13,-.10),new Point(-.23,.28)},p.White,.22);
        Facet(dc,new[]{new Point(-.20,-.91),new Point(.55,-.66),new Point(.53,-.10),new Point(.13,-.10)},p.White,.16);
        Facet(dc,new[]{new Point(.13,-.10),new Point(.53,-.10),new Point(.80,.39),new Point(.11,.91)},p.Blue,.15);
        Facet(dc,new[]{new Point(-.23,.28),new Point(.13,-.10),new Point(.11,.91),new Point(-.44,.81)},p.Pink,.13);
        var seam=new Pen(Solid(p.White,.25),.009);seam.Freeze();
        dc.DrawGeometry(null,seam,Polygon(new[]{new Point(-.20,-.91),new Point(.13,-.10),new Point(.53,-.10)},false));
        dc.DrawGeometry(null,seam,Polygon(new[]{new Point(.13,-.10),new Point(-.23,.28),new Point(.11,.91)},false));
        dc.DrawEllipse(Solid(p.White,.17),null,new Point(-.26,-.40),.30,.10);
        // 纯晶体切面与冷色高光，不在星球内部绘制爱心或其他图案。
        Facet(dc,new[]{new Point(-.32,-.65),new Point(.02,-.22),new Point(-.35,.20)},p.White,.12);
        dc.DrawEllipse(null,new Pen(Solid(p.White,.36),.009),new Point(),1,1);
        dc.Pop();dc.Pop();
    }

    private void OrbitMoon(DrawingContext dc,double cx,double cy,double rx,double ry,double degrees,double phase,double r,Color color,Palette p)
    {
        double angle=degrees*Math.PI/180;
        Point PointAt(double a)
        {
            double x=Math.Cos(a)*rx,y=Math.Sin(a)*ry;
            return new Point(cx+x*Math.Cos(angle)-y*Math.Sin(angle),cy+x*Math.Sin(angle)+y*Math.Cos(angle));
        }
        for(int i=16;i>=1;i--)
        {
            var point=PointAt(phase-i*.025);
            double strength=1-i/17.0;
            dc.DrawEllipse(Solid(color,strength*.47),null,point,.5+strength*.75,.5+strength*.75);
        }
        var head=PointAt(phase);
        Glow(dc,head.X,head.Y,r*4,color,.38);
        // ★ 2.17.46：原来每帧 new 一个 RadialGradientBrush（含 2 个 GradientStop）。
        //   颜色只取决于 color，缓存后终身复用。
        if (!_orbitHeadCache.TryGetValue(color, out var headBrush))
        {
            headBrush = new RadialGradientBrush(p.White, color);
            headBrush.Freeze();
            _orbitHeadCache[color] = headBrush;
        }
        dc.DrawEllipse(headBrush,null,head,r,r);
        Sparkle(dc,head.X,head.Y,r*.9,20,color,.75,p);
    }

    private void ShootingStar(DrawingContext dc,double w,double h,double t,Palette p)
    {
        double phase=(t+1.3)%11;
        if(phase>2.0)return;
        double progress=phase/2,alpha=Math.Sin(progress*Math.PI);
        double x=w*(.08+progress*.60),y=h*(.10+progress*.27);
        for(int i=22;i>=1;i--)
        {
            double strength=1-i/23.0;
            var pen=new Pen(Solid(i%4==0?p.Pink:p.Blue,strength*alpha*.62),.4+strength*1.25);pen.Freeze();
            dc.DrawLine(pen,new Point(x-i*7,y-i*2.6),new Point(x-(i-1)*7,y-(i-1)*2.6));
        }
        Sparkle(dc,x,y,7*alpha,20,p.White,alpha*.9,p);
    }

    private void RenderDay(DrawingContext dc,double w,double h,double t,Palette p)
    {
        double unit=Math.Min(w,h);
        // Broad, translucent daylight washes move slowly like clouds.
        Glow(dc,w*(.19+Wave(t,.09)*.022),h*.33,unit*.32,p.Pink,.16);
        Glow(dc,w*(.81+Wave(t+2,.11)*.022),h*.49,unit*.29,p.Lilac,.16);
        Glow(dc,w*.76,h*(.83+Wave(t,.10)*.020),unit*.25,p.Mint,.18);
        PrismRays(dc,w,h,t,p);

        // Fine iridescent threads replace the night sky's bright aurora.
        // ★ 2.17.46：同上，已烘焙复用。
        var ribbons = Ribbons(w,h,true,p);
        dc.PushTransform(new TranslateTransform(0,Wave(t,.17)*7));dc.DrawDrawing(ribbons[0]);dc.Pop();
        dc.PushTransform(new TranslateTransform(0,Wave(t+1,.15)*6));dc.DrawDrawing(ribbons[1]);dc.Pop();

        // Bubbles and petals are exclusive to the light composition.
        for(int i=0;i<24;i++)
        {
            double progress=Mod(Hash(i,241)+t*(.004+Hash(i,251)*.004));
            double x=(Hash(i,257)+Wave(t+Hash(i,263)*6,.13)*.018)*w;
            double y=(1.10-progress*1.22)*h;
            double radius=8+Hash(i,269)*25;
            double alpha=Math.Sin(progress*Math.PI)*(.27+Hash(i,271)*.23);
            var fill=new RadialGradientBrush
            {
                Center=new Point(.34,.28),GradientOrigin=new Point(.34,.28),RadiusX=.73,RadiusY=.73,
                GradientStops=new GradientStopCollection
                {
                    new(A(p.White,alpha*.90),0),new(A(i%2==0?p.Pink:p.Blue,alpha*.16),.55),
                    new(A(p.Lilac,alpha*.12),.82),new(A(p.White,alpha*.52),1)
                }
            };fill.Freeze();
            dc.DrawEllipse(fill,new Pen(Solid(p.Lilac,alpha*.40),.85),new Point(x,y),radius,radius);
            dc.DrawEllipse(Solid(p.White,alpha*.72),null,new Point(x-radius*.32,y-radius*.38),radius*.11,radius*.065);
        }
        Bubble(dc,w*.89,h*.69,32+Wave(t,.5)*2,p.White,p.Blue,p.Lilac,.38);
        Bubble(dc,w*.665,h*.205,19+Wave(t,.4),p.White,p.Pink,p.Lilac,.32);
        for(int i=0;i<32;i++)
        {
            double progress=Mod(Hash(i,281)+t*(.005+Hash(i,283)*.005));
            // 樱花自上而下缓慢飘落，横向摆幅小而柔和，不做上飘气泡式运动。
            double x=(Hash(i,293)+Wave(t+Hash(i,307)*6,.23)*(.018+Hash(i,317)*.018))*w;
            double y=(-.10+progress*1.23)*h;
            double alpha=Math.Sin(progress*Math.PI)*(.64+Hash(i,323)*.22);
            bool near=i%5==0;
            double size=near?12+Hash(i,311)*8:7+Hash(i,311)*7;
            var brush=new LinearGradientBrush(A(p.White,alpha*.96),A(i%4==0?p.Lilac:p.Pink,alpha),0.0);
            brush.Freeze();
            var edge=new Pen(Solid(p.Pink,alpha*.28),.045);edge.Freeze();
            Shape(dc,_petal,x,y,size,Wave(t+Hash(i,313)*6,.42)*35+i*23+progress*35,brush,edge,.72+Wave(t+i,.8)*.17);
            if(near)Glow(dc,x,y,size*1.65,p.Pink,alpha*.09);
        }
        for(int i=0;i<22;i++)
        {
            double x=Hash(i,331)*w,y=Hash(i,337)*h;
            double twinkle=Math.Pow(.5+.5*Wave(t+Hash(i,347)*6.28,.70),2);
            if(i%3==0)Sparkle(dc,x,y,2.5+twinkle*4,t*5,i%2==0?p.Lilac:p.Pink,.24+twinkle*.36,p);
            else dc.DrawEllipse(Solid(p.Lilac,.13+twinkle*.22),null,new Point(x,y),.75,.75);
        }
        PrismSystem(dc,w,h,t,p);
        // Pearlescent secondary glints and two quiet crescents distinguish day from night.
        Glow(dc,w*(.24+Wave(t,.08)*.018),h*.77,unit*.13,p.Pink,.12);
        Sparkle(dc,w*.24,h*.22,5+Wave(t,.65)*1.3,12,p.White,.66,p);
        Sparkle(dc,w*.90,h*.72,6+Wave(t,.60)*1.1,-12,p.White,.60,p);
        Shape(dc,_moon,w*.92,h*.17,10,-22,Solid(p.Lilac,.23),null);
        Shape(dc,_moon,w*.18,h*.82,8,26,Solid(p.Pink,.20),null);
    }

    private void Bubble(DrawingContext dc,double x,double y,double r,Color white,Color tint,Color rim,double alpha)
    {
        var brush=new RadialGradientBrush
        {
            Center=new Point(.35,.27),GradientOrigin=new Point(.35,.27),RadiusX=.8,RadiusY=.8,
            GradientStops=new GradientStopCollection
            {
                new(A(white,alpha*.55),0),new(A(tint,alpha*.18),.45),new(A(rim,alpha*.20),.77),new(A(white,alpha*.62),1)
            }
        };brush.Freeze();
        dc.DrawEllipse(brush,new Pen(Solid(rim,alpha*.65),.95),new Point(x,y),r,r);
        dc.DrawEllipse(Solid(white,alpha*.95),null,new Point(x-r*.30,y-r*.38),r*.12,r*.065);
    }

    private void PrismRays(DrawingContext dc,double w,double h,double t,Palette p)
    {
        double cx=w*.765,cy=h*.455,unit=Math.Min(w,h);
        var colors=new[]{p.Pink,p.Lilac,p.Blue};
        for(int i=0;i<3;i++)
        {
            var points=new[]{new Point(cx-unit*.04,cy+unit*.04),new Point(w*1.05,h*(.02+i*.08)),new Point(w*1.05,h*(.15+i*.10))};
            dc.DrawGeometry(Solid(colors[i],.075),null,Polygon(points));
        }
        for(int i=0;i<3;i++)
        {
            var pen=new Pen(Solid(colors[i],.14-i*.025),1.1);pen.Freeze();
            var arc=Ellipse(unit*(.30+i*.032),unit*(.21+i*.024),Math.PI*1.07,Math.PI*1.89);
            dc.PushTransform(new TranslateTransform(cx,cy));dc.PushTransform(new RotateTransform(-12));
            dc.DrawGeometry(null,pen,arc);dc.Pop();dc.Pop();
        }
    }

    private void PrismSystem(DrawingContext dc,double w,double h,double t,Palette p)
    {
        double cx=w*.765+Wave(t,.15)*5,cy=h*.455+Wave(t+1,.13)*7;
        double unit=Math.Min(w,h),r=unit*(.112+Wave(t,.72)*.0015);
        Glow(dc,cx,cy,r*2.3,p.White,.70);
        Glow(dc,cx,cy,r*1.7,p.Lilac,.14);
        var ring=Ellipse(r*1.85,r*.61);
        var ringPen=new Pen(Linear(p.Pink,p.Lilac,p.Blue,.50),1);ringPen.Freeze();
        dc.PushTransform(new TranslateTransform(cx,cy));dc.PushTransform(new RotateTransform(-24+Wave(t,.13)*2));
        dc.DrawGeometry(null,ringPen,ring);dc.Pop();dc.Pop();
        dc.PushTransform(new TranslateTransform(cx,cy));
        dc.PushTransform(new RotateTransform(-7+Wave(t,.22)*2));
        dc.PushTransform(new ScaleTransform(r,r));
        Facet(dc,new[]{new Point(0,-1),new Point(-.73,0),new Point(0,.13)},p.White,.68);
        Facet(dc,new[]{new Point(0,-1),new Point(.73,0),new Point(0,.13)},p.Lilac,.52);
        Facet(dc,new[]{new Point(-.73,0),new Point(0,1),new Point(0,.13)},p.Blue,.38);
        Facet(dc,new[]{new Point(.73,0),new Point(0,1),new Point(0,.13)},p.Pink,.48);
        var edge=new Pen(Linear(p.White,p.Lilac,p.Blue,.65),.007);edge.Freeze();
        var diamond=Polygon(new[]{new Point(0,-1),new Point(.73,0),new Point(0,1),new Point(-.73,0)});
        dc.DrawGeometry(null,edge,diamond);
        dc.DrawGeometry(null,edge,Polygon(new[]{new Point(-.73,0),new Point(0,.13),new Point(.73,0)},false));
        dc.DrawGeometry(null,edge,Polygon(new[]{new Point(0,-1),new Point(0,.13),new Point(0,1)},false));
        Facet(dc,new[]{new Point(0,-.87),new Point(-.13,-.12),new Point(-.57,-.02)},p.White,.48);
        dc.Pop();dc.Pop();dc.Pop();
        OrbitMoon(dc,cx,cy,r*1.85,r*.61,-24+Wave(t,.13)*2,t*.22+1.4,unit*.009,p.Pink,p);
        Shape(dc,_petal,cx+r*1.39,cy-r*.66,11+Wave(t,.8),28+Wave(t,.4)*10,Solid(p.Pink,.74),new Pen(Solid(p.White,.82),.6));
        Shape(dc,_petal,cx-r*1.20,cy+r*.60,9, -35+Wave(t,.4)*9,Solid(p.Lilac,.60),null,.78);
        Sparkle(dc,cx+r*.76,cy-r*.94,7+Wave(t,.8)*1.5,t*5,p.White,.88,p);
        Sparkle(dc,cx-r*.85,cy+r*.55,4.5,-t*4,p.Lilac,.56,p);
    }

    private void Facet(DrawingContext dc,IList<Point> points,Color color,double alpha)
        =>dc.DrawGeometry(Solid(color,alpha),null,Polygon(points));

    private static void Shape(DrawingContext dc,StreamGeometry geometry,double x,double y,double size,double rotation,Brush fill,Pen? pen,double squash=1)
    {
        dc.PushTransform(new TranslateTransform(x,y));dc.PushTransform(new RotateTransform(rotation));
        dc.PushTransform(new ScaleTransform(size,size*squash));dc.DrawGeometry(fill,pen,geometry);
        dc.Pop();dc.Pop();dc.Pop();
    }

    private void EdgeShade(DrawingContext dc,double w,double h,Palette p,bool light)
    {
        // ★ 2.17.46：只依赖亮/暗两态，缓存成两个冻结画刷。
        if (light)
        {
            _shadeDay ??= BuildShade(p, true);
            dc.DrawRectangle(_shadeDay, null, new Rect(0,0,w,h));
        }
        else
        {
            _shadeNight ??= BuildShade(p, false);
            dc.DrawRectangle(_shadeNight, null, new Rect(0,0,w,h));
        }
    }

    private static RadialGradientBrush BuildShade(Palette p, bool light)
    {
        var brush=new RadialGradientBrush
        {
            Center=new Point(.50,.43),GradientOrigin=new Point(.50,.43),RadiusX=.76,RadiusY=.76,
            GradientStops=new GradientStopCollection
            {
                new(A(p.Shade,0),0),new(A(p.Shade,0),.59),new(A(p.Shade,light?.10:.37),1)
            }
        };brush.Freeze();return brush;
    }
}