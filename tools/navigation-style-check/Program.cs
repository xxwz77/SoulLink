using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SoulLink.Controls;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "design", "navigation-icons"));
        Directory.CreateDirectory(root);
        string[] keys = { "launch", "console", "datacenter", "update", "routeupdate", "remote", "sessions", "procs", "cli" };
        int count = 0;
        foreach (bool light in new[] { false, true })
        {
            foreach (bool expanded in new[] { false, true })
            {
                var panel = new StackPanel { Width = expanded ? 174 : 50, Background = new SolidColorBrush(light ? Color.FromRgb(251, 247, 254) : Color.FromRgb(13, 15, 35)) };
                foreach (string key in keys)
                {
                    var row = new Grid { Height = 38 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var icon = new NavigationOrnament
                    {
                        PageKey = key,
                        Width = 42,
                        IsSelected = key == "launch",
                        IsHovered = key == "remote",
                        IsLight = light,
                        MutedBrush = Frozen(light ? Color.FromRgb(92, 76, 120) : Color.FromRgb(165, 171, 205)),
                        TextBrush = Frozen(light ? Color.FromRgb(57, 43, 86) : Color.FromRgb(194, 232, 255)),
                        CyanBrush = Frozen(light ? Color.FromRgb(0, 135, 167) : Color.FromRgb(79, 227, 255)),
                        VioletBrush = Frozen(light ? Color.FromRgb(118, 73, 196) : Color.FromRgb(139, 107, 255)),
                    };
                    if (icon.IsHitTestVisible || icon.Focusable)
                        throw new InvalidOperationException("The visual layer must never intercept menu input.");
                    var rowStyle = new NavigationOrnament
                    {
                        SelectionOnly = true,
                        IsSelected = key == "launch",
                        IsHovered = key == "remote",
                        IsLight = light,
                        CyanBrush = Frozen(light ? Color.FromRgb(0, 135, 167) : Color.FromRgb(79, 227, 255)),
                    };
                    Grid.SetColumnSpan(rowStyle, 2);
                    row.Children.Add(rowStyle);
                    row.Children.Add(icon);
                    if (expanded)
                    {
                        var label = new TextBlock { Text = key, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                            Foreground = Frozen(light ? Color.FromRgb(48, 37, 76) : Color.FromRgb(242, 245, 255)), FontSize = 12.5 };
                        Grid.SetColumn(label, 1);
                        row.Children.Add(label);
                    }
                    panel.Children.Add(row);
                }
                panel.Measure(new Size(panel.Width, panel.Children.Count * 38));
                panel.Arrange(new Rect(0, 0, panel.Width, panel.Children.Count * 38));
                panel.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)panel.Width * 2, (int)panel.ActualHeight * 2, 192, 192, PixelFormats.Pbgra32);
                bitmap.Render(panel);
                string name = "actual-" + (light ? "light" : "dark") + "-" + (expanded ? "expanded" : "collapsed") + ".png";
                using var stream = File.Create(Path.Combine(root, name));
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(stream);
                count++;
            }
        }
        Console.WriteLine("PASS: rendered " + count + " WPF states; input remains with the existing menu rows.");
        return 0;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
