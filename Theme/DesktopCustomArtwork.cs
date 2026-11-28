using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SoulLink.Models;
using SoulLink.Services;

namespace SoulLink.Theme;

internal readonly record struct ArtworkCrop(double Zoom, double CenterX, double CenterY)
{
    internal static ArtworkCrop Centered => new(1, .5, .5);
    internal const double MaximumZoom = 8;

    internal ArtworkCrop Normalize() => new(
        double.IsFinite(Zoom) ? Math.Clamp(Zoom, 1, MaximumZoom) : 1,
        double.IsFinite(CenterX) ? Math.Clamp(CenterX, 0, 1) : .5,
        double.IsFinite(CenterY) ? Math.Clamp(CenterY, 0, 1) : .5);

    internal Rect Project(Size viewport, Size source)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0 || source.Width <= 0 || source.Height <= 0)
            return Rect.Empty;
        var crop = Normalize();
        double scale = Math.Max(viewport.Width / source.Width, viewport.Height / source.Height) * crop.Zoom;
        double width = Math.Max(viewport.Width, source.Width * scale);
        double height = Math.Max(viewport.Height, source.Height * scale);
        double x = Math.Clamp(viewport.Width / 2 - crop.CenterX * width, viewport.Width - width, 0);
        double y = Math.Clamp(viewport.Height / 2 - crop.CenterY * height, viewport.Height - height, 0);
        return new Rect(x, y, width, height);
    }

    internal ArtworkCrop Drag(Size viewport, Size source, Vector displacement)
    {
        var projected = Project(viewport, source);
        if (projected.IsEmpty) return Normalize();
        double x = Math.Clamp(projected.X + displacement.X, viewport.Width - projected.Width, 0);
        double y = Math.Clamp(projected.Y + displacement.Y, viewport.Height - projected.Height, 0);
        return new ArtworkCrop(Zoom, (viewport.Width / 2 - x) / projected.Width,
            (viewport.Height / 2 - y) / projected.Height).Normalize();
    }

    internal ArtworkCrop ZoomAt(Size viewport, Size source, Point cursor, double zoom)
    {
        Rect before = Project(viewport, source);
        ArtworkCrop after = (this with { Zoom = zoom }).Normalize();
        Rect scaled = after.Project(viewport, source);
        if (before.IsEmpty || scaled.IsEmpty) return after;
        double u = (cursor.X - before.X) / before.Width;
        double v = (cursor.Y - before.Y) / before.Height;
        return (after with
        {
            CenterX = u + (viewport.Width / 2 - cursor.X) / scaled.Width,
            CenterY = v + (viewport.Height / 2 - cursor.Y) / scaled.Height
        }).Normalize();
    }
}

internal static class DesktopCustomArtwork
{
    internal const string Id = "CustomArtwork";
    private static readonly Dictionary<Color, SolidColorBrush> DrawBrushes = new();
    private static string? _loadedKey;
    private static BitmapSource? _loadedImage;
    private static ArtworkCrop _crop;

    internal static CustomDesktopArtwork? Setting(AppSettings? settings, bool dark)
        => dark ? settings?.DarkCustomArtwork : settings?.LightCustomArtwork;

    internal static ArtworkCrop Crop(CustomDesktopArtwork? value)
        => value == null ? ArtworkCrop.Centered : new ArtworkCrop(value.Zoom, value.CenterX, value.CenterY).Normalize();

    internal static DesktopPalette Resolve(AppSettings? settings, bool dark, string? selected)
    {
        if (!string.Equals(selected, Id, StringComparison.Ordinal))
        {
            _loadedImage = null;
            _loadedKey = null;
            return DesktopPalette.Resolve(dark, selected);
        }
        var stored = Setting(settings, dark);
        if (stored != null && TryColor(stored.Accent, out var accent) && TryColor(stored.Surface, out var surface))
        {
            var localPath = CustomArtworkStore.PathFor(stored.ImageKey);
            if (localPath != null && System.IO.File.Exists(localPath))
            {
                if (_loadedKey != localPath || _loadedImage == null)
                {
                    _loadedImage = CustomArtworkStore.TryLoad(stored.ImageKey);
                    _loadedKey = localPath;
                }
                if (_loadedImage != null)
                {
                    _crop = Crop(stored);
                    return Palette(dark, accent, surface);
                }
            }
        }
        _loadedImage = null;
        _loadedKey = null;
        return DesktopPalette.Resolve(dark, dark ? "P1" : "P2");
    }

    internal static bool TryColor(string? text, out Color color)
    {
        color = default;
        string value = (text ?? "").Trim().TrimStart('#');
        if (value.Length != 6 || !int.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int rgb))
            return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    internal static string ColorText(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    internal static DesktopPalette Palette(bool dark, Color accent, Color card)
    {
        var foreground = dark ? Colors.White : Color.FromRgb(20, 22, 28);
        var appropriate = dark ? Colors.Black : Colors.White;
        Color surface = ContrastColor(card, foreground, appropriate, 7);
        Color text = foreground;
        Color muted = ContrastColor(DesktopPalette.Mix(text, surface, .3), surface, text, 4.6);
        Color basis = DesktopPalette.Mix(surface, appropriate, .2);
        Color usableAccent = ContrastColor(accent, surface, text, 4.6);
        Color tint = DesktopPalette.Mix(basis, usableAccent, .12);
        return new(Id, "自定义原图", dark, basis, tint, surface, text, muted, usableAccent,
            usableAccent, DesktopPalette.Mix(usableAccent, text, .36), 11,
            surface, surface, 255, 255, true);
    }

    internal static Color TextOnAccent(Color accent) =>
        Contrast(Colors.Black, accent) >= Contrast(Colors.White, accent) ? Colors.Black : Colors.White;

    private static Color ContrastColor(Color original, Color background, Color toward, double required)
    {
        if (Contrast(original, background) >= required) return original;
        double low = 0, high = 1;
        for (int pass = 0; pass < 12; pass++)
        {
            double mid = (low + high) / 2;
            if (Contrast(DesktopPalette.Mix(original, toward, mid), background) >= required) high = mid;
            else low = mid;
        }
        return DesktopPalette.Mix(original, toward, high);
    }

    private static double Contrast(Color first, Color second)
    {
        static double Linear(byte value)
        {
            double fraction = value / 255.0;
            return fraction <= .04045 ? fraction / 12.92 : Math.Pow((fraction + .055) / 1.055, 2.4);
        }
        static double Luminance(Color color) => .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    internal static void DrawCurrent(DrawingContext drawing, Rect bounds, DesktopPalette palette, bool visible)
        => Draw(drawing, bounds, visible ? _loadedImage : null, _crop, palette);

    internal static void Draw(DrawingContext drawing, Rect bounds, BitmapSource? image, ArtworkCrop crop, DesktopPalette palette)
    {
        drawing.PushClip(new RectangleGeometry(bounds));
        drawing.DrawRectangle(Brush(palette.Base), null, bounds);
        if (image != null)
        {
            var target = crop.Project(bounds.Size, PixelSize(image));
            if (!target.IsEmpty)
            {
                target.Offset(bounds.X, bounds.Y);
                drawing.DrawImage(image, target);
                drawing.DrawRectangle(Brush(DesktopPalette.WithAlpha(palette.Base, palette.Dark ? (byte)74 : (byte)46)), null, bounds);
            }
        }
        drawing.Pop();
    }

    internal static Size PixelSize(BitmapSource source) => new(source.PixelWidth, source.PixelHeight);

    private static SolidColorBrush Brush(Color color)
    {
        if (DrawBrushes.TryGetValue(color, out var cached)) return cached;
        if (DrawBrushes.Count >= 64) DrawBrushes.Clear();
        var value = new SolidColorBrush(color);
        value.Freeze();
        DrawBrushes[color] = value;
        return value;
    }
}
