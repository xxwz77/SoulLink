using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SoulLink.Theme;

internal sealed class DesktopPortraitArt
{
    private sealed record Composition(Point Face, Point Light, Point Anchor, double Width, Thickness Fade);

    private static readonly Composition[] Compositions =
    [
        new(new(.545,.315), new(.637,.363), new(1057,252), 1080, new(260,105,110,119)),
        new(new(.531,.505), new(.555,.531), new(660,60), 1070, new(112,95,240,145)),
        new(new(.443,.319), new(.496,.436), new(681,345), 1480, new(219,104,118,117)),
        new(new(.642,.410), new(.613,.203), new(585,539), 1300, new(108,133,277,114)),
        new(new(.353,.335), new(.288,.527), new(132,515), 1085, new(105,101,380,120)),
    ];

    private static readonly Geometry Petal = Shape("M -7,2 C -5,-4 -1,-6 3,-4.5 C 5,-5 8,-2 8,0.5 C 5,4 0,6 -7,2Z");
    private static readonly Geometry Feather = Shape("M -11,10 Q 0,-13 8,-15 Q 17,-5 -8,8Z");
    private static readonly Geometry FeatherRibs = Shape("M -14,13 Q 1,2 9,-14 M -7,7 Q -5,3 -3,-2 M -5,4 Q 0,5 3,1 M -3,2 Q -1,-2 1,-7 M 0,-2 Q 5,-1 7,-5 M 2,-5 Q 4,-8 6,-11");
    private readonly DrawingGroup _picture = new();
    private readonly int _kind;
    private readonly Point _face;
    private readonly Point _light;
    private readonly double _scale;

    internal Size Size { get; }

    internal DesktopPortraitArt(string id, BitmapSource image, Size size)
    {
        Size = size;
        _kind = id[1] - '1';
        var composition = Compositions[_kind];
        _scale = Math.Min(size.Width / 1240, size.Height / 771);
        _face = new Point(composition.Anchor.X * size.Width / 1240, composition.Anchor.Y * size.Height / 771);
        double width = composition.Width * _scale;
        double height = width * image.PixelHeight / image.PixelWidth;
        var frame = new Rect(_face.X - width * composition.Face.X, _face.Y - height * composition.Face.Y, width, height);
        _light = new Point(frame.X + width * composition.Light.X, frame.Y + height * composition.Light.Y);

        using (DrawingContext background = _picture.Open())
        {
            background.PushOpacityMask(FadeMask(true, composition.Fade.Left * _scale / width, composition.Fade.Right * _scale / width));
            background.PushOpacityMask(FadeMask(false, composition.Fade.Top * _scale / height, composition.Fade.Bottom * _scale / height));
            background.DrawImage(image, frame);
            background.Pop();
            background.Pop();
        }
        _picture.Freeze();
    }

    internal void Draw(DrawingContext drawing, double time)
    {
        drawing.DrawDrawing(_picture);
        switch (_kind)
        {
            case 0: DrawBird(drawing, time); break;
            case 1: DrawPetals(drawing, time); break;
            case 2: DesktopRibbonEcho.Draw(drawing, _light, _scale, time); break;
            case 3: DrawBokeh(drawing, time); break;
            case 4: DrawFeathers(drawing, time); break;
        }
    }

    private void DrawBird(DrawingContext dc, double time)
    {
        var ice = Color.FromRgb(185, 232, 255);
        double breath = .55 + .45 * Math.Pow(Math.Sin(time * 1.75 + .2), 2);
        Glow(dc, ice, _light, 53 * _scale, 40 * _scale, .35 * breath);
        Glow(dc, Colors.White, _light, 16 * _scale, 13 * _scale, .39 * breath);
        for (int i = 0; i < 12; i++)
        {
            double age = Cycle(time * (.14 + i % 4 * .011) + i * .391);
            double wave = Math.Cos(age * 4.2 + i * 2.6) * (.57 + i % 3 * .22);
            var center = At(_light, (10 + 57 * age) * wave, -7 - (55 + i % 4 * 9) * age);
            double opacity = Math.Sin(Math.PI * age) * (.40 + .58 * Math.Pow(Math.Sin(time * 3.31 + i), 2));
            if (i % 3 == 0) Glow(dc, ice, center, 8 * _scale, 9 * _scale, opacity * .61);
            Dot(dc, Colors.White, center, (.82 + i % 3 * .26) * _scale, opacity);
        }
    }

    private void DrawPetals(DrawingContext dc, double time)
    {
        for (int i = 0; i < 13; i++)
        {
            double age = Cycle(time * (.051 + i % 4 * .006) + i * .279);
            int side = i % 2 == 0 ? 1 : -1;
            double roll = .25 + .75 * Math.Abs(Math.Sin(time * 1.29 + i * 1.47));
            double depth = (.75 + i % 4 * .16) * _scale;
            double opacity = Math.Sin(Math.PI * age) * (.52 + i % 4 * .11);
            var center = At(_face, side * (105 + i % 5 * 44 - 29 * age) + 31 * Math.Sin(age * 6.4 + i * 2.9),
                -70 + 273 * age + 16 * Math.Sin(i * 2.9 + age * 3.9));
            PushParticle(dc, center, depth, roll, i * 63 + age * 264);
            dc.DrawGeometry(Ink(Color.FromRgb(255, i % 3 == 0 ? (byte)167 : (byte)214, 223), opacity),
                new Pen(Ink(Color.FromRgb(255, 241, 248), opacity * .78), .80), Petal);
            dc.Pop();
            dc.Pop();
            dc.Pop();
        }
    }

    private void DrawBokeh(DrawingContext dc, double time)
    {
        var gold = Color.FromRgb(255, 198, 137);
        for (int i = 0; i < 18; i++)
        {
            double t = time * (.26 + i % 4 * .051) + i * 2.7;
            double shift = (i % 3 == 1 ? 1 : -1) * (126 + i % 7 * 37);
            Point center = At(_light, shift + 10 * Math.Sin(t * 1.37),
                297 * Cycle(.917 * i + t * .012) - 40 + 12 * Math.Cos(t));
            double opacity = .24 + .76 * Math.Pow(Math.Sin(t * 1.16), 2);
            double radius = (5.5 + i % 6 * 2.9) * _scale;
            Glow(dc, gold, center, radius, radius * .93, opacity * .56);
            if (i % 5 == 0) Dot(dc, gold, center, 1.4 * _scale, opacity * .8);
        }
    }

    private void DrawFeathers(DrawingContext dc, double time)
    {
        var silk = Color.FromRgb(255, 225, 246);
        for (int i = 0; i < 8; i++)
        {
            double age = Cycle(time * (.055 + .008 * (i % 3)) + i * .437);
            double drift = time * .83 + i * 2.81;
            Point center = At(_light, -71 + (i % 5) * 72 + 20 * Math.Sin(drift + age * 2.1),
                -132 + age * 268 + 12 * Math.Cos(drift * 1.7));
            double opacity = Math.Sin(Math.PI * age) * (.56 + i % 4 * .10);
            double scale = (.63 + i % 5 * .18) * _scale;
            PushParticle(dc, center, scale, .62 + .38 * Math.Pow(Math.Cos(drift * 1.7), 2), -24 + 36 * Math.Sin(drift));
            dc.DrawGeometry(Ink(silk, opacity * .62), null, Feather);
            dc.DrawGeometry(null, new Pen(Ink(Colors.White, opacity), .68), FeatherRibs);
            dc.Pop();
            dc.Pop();
            dc.Pop();
            if (i % 2 == 0) Glow(dc, silk, center, 13 * _scale, 16 * _scale, opacity * .44);
        }
    }

    private Point At(Point origin, double x, double y) => new(origin.X + x * _scale, origin.Y + y * _scale);
    private static double Cycle(double time) => time - Math.Floor(time);

    private static void PushParticle(DrawingContext dc, Point center, double scale, double roll, double angle)
    {
        dc.PushTransform(new TranslateTransform(center.X, center.Y));
        dc.PushTransform(new RotateTransform(angle));
        dc.PushTransform(new ScaleTransform(scale, scale * roll));
    }

    private static Geometry Shape(string markup)
    {
        Geometry geometry = Geometry.Parse(markup);
        geometry.Freeze();
        return geometry;
    }

    private static Brush FadeMask(bool horizontal, double start, double end)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = horizontal ? new Point(1, 0) : new Point(0, 1)
        };
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        brush.GradientStops.Add(new GradientStop(Colors.Black, Math.Min(.49, start)));
        brush.GradientStops.Add(new GradientStop(Colors.Black, 1 - Math.Min(.49, end)));
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        brush.Freeze();
        return brush;
    }

    internal static SolidColorBrush Ink(Color color, double opacity = 1)
    {
        var brush = new SolidColorBrush(DesktopPalette.WithAlpha(color, (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255)));
        brush.Freeze();
        return brush;
    }

    private static void Dot(DrawingContext dc, Color color, Point point, double radius, double opacity) =>
        dc.DrawEllipse(Ink(color, opacity), null, point, radius, radius);

    private static void Glow(DrawingContext dc, Color color, Point point, double x, double y, double opacity)
    {
        var brush = new RadialGradientBrush(DesktopPalette.WithAlpha(color, (byte)(255 * Math.Clamp(opacity, 0, 1))), Colors.Transparent);
        dc.DrawEllipse(brush, null, point, x, y);
    }
}
