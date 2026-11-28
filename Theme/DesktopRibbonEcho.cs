using System;
using System.Windows;
using System.Windows.Media;

namespace SoulLink.Theme;

internal static class DesktopRibbonEcho
{
    private readonly record struct Route(Vector Lift, Vector Bend, Vector End);
    private readonly record struct Particle(Route Route, DrawingGroup Sprite, double Phase, double Rate,
        double Size, double Drift, double Twist);

    private static readonly Color Frost = Color.FromRgb(160, 227, 255);
    private static readonly Color Blush = Color.FromRgb(255, 177, 222);
    private static readonly Brush HandHalo = Halo(Frost, 92);
    private static readonly Brush HandCore = Halo(Colors.White, 163);
    private static readonly Geometry Silk = CreateSilk();
    private static readonly Particle[] Particles = CreateParticles();

    internal static void Draw(DrawingContext drawing, Point light, double scale, double time)
    {
        double pulse = .5 + .5 * Math.Sin(time * 2.1);
        drawing.PushOpacity(.64 + .36 * pulse);
        drawing.DrawEllipse(HandHalo, null, light, 47 * scale, 36 * scale);
        drawing.DrawEllipse(HandCore, null, light, 16 * scale, 13 * scale);
        drawing.Pop();

        foreach (Particle particle in Particles)
        {
            double phase = time * particle.Rate + particle.Phase;
            double age = phase - Math.Floor(phase);
            double opacity = Ease(age / .17) * Ease((1 - age) / .39);
            if (opacity < .025) continue;

            double distance = age * (.85 + .15 * age);
            double rest = 1 - distance;
            Route route = particle.Route;
            Vector center = 3 * rest * rest * distance * route.Lift +
                3 * rest * distance * distance * route.Bend + distance * distance * distance * route.End;
            Vector tangent = rest * rest * route.Lift + 2 * rest * distance * (route.Bend - route.Lift) +
                distance * distance * (route.End - route.Bend);
            tangent.Normalize();
            Vector normal = new(-tangent.Y, tangent.X);
            double twist = age * 8 + particle.Twist;
            center += normal * (particle.Drift + 13 * Math.Sin(twist)) * distance;
            center *= scale;

            double face = Math.Abs(Math.Cos(twist));
            double length = scale * particle.Size * (.88 + .12 * Math.Sin(twist));
            double width = length * (.46 + .54 * face);
            drawing.PushOpacity(opacity * (.78 + .22 * face));
            drawing.PushTransform(new MatrixTransform(new Matrix(tangent.X * length, tangent.Y * length,
                normal.X * width, normal.Y * width, light.X + center.X, light.Y + center.Y)));
            drawing.DrawDrawing(particle.Sprite);
            drawing.Pop();
            drawing.Pop();
        }
    }

    private static Particle[] CreateParticles()
    {
        // Trace the three rightward ribbons and the shorter fold below the palm.
        Route[] routes =
        [
            new(new(38, -86), new(146, -96), new(286, -282)),
            new(new(62, -40), new(165, -118), new(366, -129)),
            new(new(61, 53), new(184, 86), new(352, 69)),
            new(new(-48, 51), new(-127, 112), new(-203, 103)),
        ];
        DrawingGroup frost = CreateSprite(Frost);
        DrawingGroup blush = CreateSprite(Blush);
        const int perRoute = 5;
        var particles = new Particle[routes.Length * perRoute];
        var random = new Random(302);
        for (int route = 0; route < routes.Length; route++)
        {
            for (int index = 0; index < perRoute; index++)
            {
                double phase = (index + random.NextDouble() * .7) / perRoute + route * .13;
                double rate = .145 + random.NextDouble() * .079;
                double size = .52 + random.NextDouble() * .75;
                double drift = (random.NextDouble() - .5) * 78;
                double twist = random.NextDouble() * Math.Tau;
                particles[route * perRoute + index] = new Particle(routes[route], (index + route) % 3 == 1 ? blush : frost,
                    phase, rate, size, drift, twist);
            }
        }
        return particles;
    }

    private static Geometry CreateSilk()
    {
        Geometry geometry = Geometry.Parse("M -11,1 Q -4,-.8 2,-1.8 Q 5,-1.8 6,0 Q 3,2 -11,1 Z");
        geometry.Freeze();
        return geometry;
    }

    private static DrawingGroup CreateSprite(Color color)
    {
        var sprite = new DrawingGroup();
        using (DrawingContext drawing = sprite.Open())
        {
            drawing.DrawEllipse(Halo(color, 138), null, new Point(0, 0), 13, 5.8);
            var silk = new LinearGradientBrush(DesktopPalette.WithAlpha(color, 0), Colors.White, 0);
            silk.GradientStops.Insert(1, new GradientStop(color, .68));
            drawing.DrawGeometry(silk, null, Silk);
        }
        sprite.Freeze();
        return sprite;
    }

    private static Brush Halo(Color color, byte alpha)
    {
        var brush = new RadialGradientBrush(DesktopPalette.WithAlpha(color, alpha), DesktopPalette.WithAlpha(color, 0));
        brush.Freeze();
        return brush;
    }

    private static double Ease(double progress)
    {
        double value = Math.Clamp(progress, 0, 1);
        return value * value * (3 - 2 * value);
    }
}
