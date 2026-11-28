using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SoulLink.Theme;

internal sealed class MainPageCards : DependencyObject
{
    private static readonly DependencyProperty BackgroundOpacityProperty = DependencyProperty.Register(
        "BackgroundOpacity", typeof(double), typeof(MainPageCards), new PropertyMetadata(1.0));

    private static readonly string[] BackgroundKeys =
    [
        "DesktopPanel", "GlassFill", "GlassFillStrong", "PanelBackground",
        "OverviewAccountsCard", "OverviewActionsCard", "OverviewAutomationCard", "OverviewLogCard",
    ];

    internal void ApplyTheme(ResourceDictionary resources)
    {
        foreach (string key in BackgroundKeys)
        {
            if (Application.Current.TryFindResource(key) is not Brush themeBrush) continue;
            Brush background = themeBrush.CloneCurrentValue();

            // Each theme supplies the RGB and gradient shape. The setting supplies the full alpha.
            if (background is SolidColorBrush solid)
                solid.Color = DesktopPalette.WithAlpha(solid.Color, 255);
            else if (background is GradientBrush gradient)
                foreach (GradientStop stop in gradient.GradientStops)
                    stop.Color = DesktopPalette.WithAlpha(stop.Color, 255);

            BindingOperations.SetBinding(background, Brush.OpacityProperty, new Binding
            {
                Source = this,
                Path = new PropertyPath(BackgroundOpacityProperty),
                Mode = BindingMode.OneWay,
            });
            resources[key] = background;
        }
    }

    internal void PreviewTransparency(int transparency) => SetValue(BackgroundOpacityProperty, 1 - transparency / 100.0);
}
