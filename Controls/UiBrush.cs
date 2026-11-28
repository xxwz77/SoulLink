using System.Collections.Generic;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SoulLink.Services;

namespace SoulLink.Controls;

/// <summary>Live theme colors for controls created in code, including translucent status fills.</summary>
internal static class UiBrush
{
    private readonly record struct Slot(string Key, byte? Alpha);
    private sealed record Entry(Swatch Swatch, SolidColorBrush Brush);

    private sealed class Swatch : DependencyObject
    {
        internal static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
            nameof(Color), typeof(Color), typeof(Swatch));

        public Color Color
        {
            get => (Color)GetValue(ColorProperty);
            set => SetValue(ColorProperty, value);
        }
    }

    private static readonly Dictionary<Slot, Entry> Entries = new();

    static UiBrush() => ThemeManager.AppearanceChanged += Refresh;

    internal static SolidColorBrush Get(string resource, byte? alpha = null)
    {
        var slot = new Slot(resource, alpha);
        if (Entries.TryGetValue(slot, out var entry)) return entry.Brush;

        var swatch = new Swatch { Color = Resolve(slot) };
        var brush = new SolidColorBrush();
        // A bound brush cannot freeze when WPF shares it in a template or a DrawingVisual.
        BindingOperations.SetBinding(brush, SolidColorBrush.ColorProperty,
            new Binding(nameof(Swatch.Color)) { Source = swatch, Mode = BindingMode.OneWay });
        Entries.Add(slot, new Entry(swatch, brush));
        return brush;
    }

    internal static void BindColor(DependencyObject target, DependencyProperty property, string resource)
        => BindingOperations.SetBinding(target, property,
            new Binding(nameof(SolidColorBrush.Color)) { Source = Get(resource), Mode = BindingMode.OneWay });

    private static void Refresh()
    {
        foreach (var (slot, entry) in Entries) entry.Swatch.Color = Resolve(slot);
    }

    private static Color Resolve(Slot slot)
    {
        Color color = Application.Current?.TryFindResource(slot.Key) is SolidColorBrush brush
            ? brush.Color : SystemColors.WindowTextColor;
        if (slot.Alpha is byte alpha) color.A = alpha;
        return color;
    }
}
