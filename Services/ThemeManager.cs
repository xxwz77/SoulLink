using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using SoulLink.Services;

namespace SoulLink.Services
{
    /// <summary>三态主题：跟随系统、浅色、深色。主题变化只影响 SoulLink，不影响 RDP 内容。</summary>
    public static class ThemeManager
    {
        public static bool IsDark { get; private set; } = true;
        private static readonly Dictionary<string, SolidColorBrush> _previous = new(StringComparer.Ordinal);
        private static bool _stylesConverted;

        public static void Apply()
        {
            int mode = App.Config?.Settings?.ThemeMode ?? 0;
            IsDark = mode == 2 || (mode == 0 && !SystemUsesLightTheme());
            var resources = Application.Current?.Resources;
            if (resources == null) return;
            ResourceDictionary styles = resources;
            foreach (var merged in resources.MergedDictionaries)
                if (merged.Contains("TextPrimary")) styles = merged;

            resources["Bg0"] = IsDark ? Color.FromRgb(5, 6, 12) : Color.FromRgb(250, 242, 252);
            resources["Bg1"] = IsDark ? Color.FromRgb(11, 15, 34) : Color.FromRgb(243, 235, 252);
            resources["Cyan"] = IsDark ? Color.FromRgb(79, 227, 255) : Color.FromRgb(0, 135, 167);
            resources["Violet"] = IsDark ? Color.FromRgb(139, 107, 255) : Color.FromRgb(118, 73, 196);
            resources["Magenta"] = IsDark ? Color.FromRgb(255, 95, 210) : Color.FromRgb(200, 52, 145);
            resources["Mint"] = IsDark ? Color.FromRgb(65, 240, 192) : Color.FromRgb(0, 136, 105);
            resources["Amber"] = IsDark ? Color.FromRgb(255, 196, 107) : Color.FromRgb(176, 107, 0);
            resources["Rose"] = IsDark ? Color.FromRgb(255, 107, 139) : Color.FromRgb(192, 39, 83);

            SetBrush(styles, "TextPrimary", IsDark ? Color.FromRgb(242, 245, 255) : Color.FromRgb(48, 37, 76));
            SetBrush(styles, "TextSecondary", IsDark ? Color.FromArgb(180, 194, 232, 255) : Color.FromRgb(57, 43, 86));
            SetBrush(styles, "TextMuted", IsDark ? Color.FromArgb(122, 140, 180, 224) : Color.FromRgb(74, 59, 102));
            SetBrush(styles, "StrokeSoft", IsDark ? Color.FromArgb(36, 255, 255, 255) : Color.FromArgb(42, 102, 66, 139));
            SetBrush(styles, "StrokeMid", IsDark ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(64, 102, 66, 139));
            SetBrush(styles, "CyanBrush", (Color)resources["Cyan"]);
            SetBrush(styles, "VioletBrush", (Color)resources["Violet"]);
            SetBrush(styles, "MintBrush", (Color)resources["Mint"]);
            SetBrush(styles, "AmberBrush", (Color)resources["Amber"]);
            SetBrush(styles, "RoseBrush", (Color)resources["Rose"]);
            SetBrush(styles, "MagentaBrush", (Color)resources["Magenta"]);
            SetBrush(styles, "NavBackground", IsDark ? Color.FromArgb(242, 10, 16, 32) : Color.FromArgb(248, 249, 241, 255));
            SetBrush(styles, "PanelBackground", IsDark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(115, 255, 255, 255));
            SetBrush(styles, "FieldBackground", IsDark ? Color.FromArgb(38, 10, 18, 38) : Color.FromArgb(160, 255, 255, 255));
            SetBrush(styles, "WindowBackground", IsDark ? Color.FromRgb(7, 10, 22) : Color.FromRgb(249, 242, 253));
            if (Application.Current.MainWindow is SoulLink.Views.MainWindow mainWindow)
            {
                Brush nav = (Brush)styles["NavBackground"];
                Brush stroke = (Brush)styles["StrokeMid"];
                mainWindow.NavRail.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "NavBackground");
                mainWindow.NavPanel.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "NavBackground");
                mainWindow.NavRail.BorderBrush = stroke;
                mainWindow.NavPanel.BorderBrush = stroke;
                mainWindow.NavRail.CornerRadius = new CornerRadius(14, 0, 0, 14);
                mainWindow.NavPanel.CornerRadius = new CornerRadius(0, 14, 14, 0);
            }
            foreach (Window window in Application.Current.Windows)
                if (window is SoulLink.Views.MainWindow main)
                    main.Shell.Background = (Brush)Application.Current.FindResource("WindowBackground");
            if (!_stylesConverted)
            {
                foreach (var pair in _previous) UpdateStyleReferences(styles, pair.Key, pair.Value);
                _stylesConverted = true;
            }
            ApplyToOpenWindows();
            if (!IsDark)
                foreach (Window window in Application.Current.Windows) ImproveLightReadability(window);
            foreach (Window window in Application.Current.Windows)
                if (window is SoulLink.Views.MainWindow main) main.Shell.Background = (Brush)Application.Current.FindResource("WindowBackground");
        }

        public static bool SystemUsesLightTheme()
        {
            try
            {
                object? value = Registry.GetValue(@"HKEY_CURRENT_USERSoftwareMicrosoftWindowsCurrentVersionThemesPersonalize", "AppsUseLightTheme", 0);
                return value is int i && i != 0;
            }
            catch { return false; }
        }

        private static readonly (string Key, DependencyProperty Property)[] BrushedProperties =
        {
            ("TextPrimary", System.Windows.Controls.Control.ForegroundProperty),
            ("TextSecondary", System.Windows.Controls.Control.ForegroundProperty),
            ("TextMuted", System.Windows.Controls.Control.ForegroundProperty),
            ("TextPrimary", System.Windows.Controls.TextBlock.ForegroundProperty),
            ("TextSecondary", System.Windows.Controls.TextBlock.ForegroundProperty),
            ("TextMuted", System.Windows.Controls.TextBlock.ForegroundProperty),
            ("CyanBrush", System.Windows.Controls.Control.ForegroundProperty),
            ("PanelBackground", System.Windows.Controls.Control.BackgroundProperty),
            ("NavBackground", System.Windows.Controls.Control.BackgroundProperty),
            ("PanelBackground", System.Windows.Controls.Border.BackgroundProperty),
            ("StrokeSoft", System.Windows.Controls.Border.BorderBrushProperty),
            ("StrokeMid", System.Windows.Controls.Border.BorderBrushProperty),
            ("WindowBackground", System.Windows.Controls.Border.BackgroundProperty)
        };

        private static void ApplyToOpenWindows()
        {
            if (Application.Current == null) return;
            foreach (Window window in Application.Current.Windows)
            {
                window.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
                ApplyToTree(window);
            }
        }

        private static void ApplyToTree(DependencyObject element)
        {
            foreach (var (key, property) in BrushedProperties)
            {
                object value = element.GetValue(property);
                if (value is SolidColorBrush old)
                {
                    var brush = Application.Current.FindResource(key) as SolidColorBrush;
                    if (brush != null && old.Color == brush.Color)
                        if (element is FrameworkElement fe) fe.SetResourceReference(property, key);
                        else if (element is FrameworkContentElement fce) fce.SetResourceReference(property, key);
                }
            }
            int count = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < count; i++) ApplyToTree(VisualTreeHelper.GetChild(element, i));
        }

        private static void UpdateStyleReferences(ResourceDictionary resources, string key, SolidColorBrush old)
        {
            foreach (var style in resources.Values.OfType<Style>())
                foreach (var setter in style.Setters.OfType<Setter>())
                    if (ReferenceEquals(setter.Value, old)) setter.Value = new DynamicResourceExtension(key);
        }

        private static void ImproveLightReadability(DependencyObject root)
        {
            if (root is System.Windows.Controls.TextBlock text)
                if (text.Foreground is SolidColorBrush brush &&
                    (brush.Color.A < 210 || (brush.Color.R > 170 && brush.Color.G > 170 && brush.Color.B > 170)))
                    text.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextSecondary");
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                ImproveLightReadability(VisualTreeHelper.GetChild(root, i));
        }

        private static void SetBrush(ResourceDictionary resources, string key, Color color)
        {
            // XAML 中的资源画刷可能已冻结，直接替换实例，不能修改其 Color。
            resources[key] = new SolidColorBrush(color);
        }
    }
}