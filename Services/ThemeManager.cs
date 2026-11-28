using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Effects;
using Microsoft.Win32;
using SoulLink.Theme;

namespace SoulLink.Services
{
    /// <summary>解析显示模式、保存的主题和 WPF 主题资源。</summary>
    public static class ThemeManager
    {
        public static bool IsDark { get; private set; } = true;
        internal static DesktopPalette Palette { get; private set; } = DesktopPalette.DarkThemes[0];
        internal static event Action? AppearanceChanged;
        internal static bool AnimationsDisabled =>
            App.Config?.Settings.EffectTier >= Models.AppSettings.EffectTierLevel.Low ||
            Environment.GetEnvironmentVariable("SOULLINK_NOANIM") == "1" || !SystemParameters.ClientAreaAnimation;

        /// <summary>档位切换后让所有背景元素重新计算动画开关（无需重启程序）。</summary>
        public static void RefreshEffects()
        {
            try
            {
                if (System.Windows.Application.Current == null) return;
                foreach (System.Windows.Window w in System.Windows.Application.Current.Windows)
                {
                    if (w == null) continue;
                    // ★ 档位切换会让焦点短暂移出主窗口（点 ComboBox 时），
                    //   而失焦会把 DWM 边框色恢复成系统默认灰边，看起来像"多了一圈灰框"。
                    //   这里显式重设一次，避免用户以为档位改变了窗口外观。
                    try { SoulLink.Views.MainWindow.RefreshWindowBorder(); } catch { }
                    if (w.Content is not System.Windows.DependencyObject root) continue;
                    RefreshEffectsIn(root);
                }
            }
            catch { }
        }

        private static void RefreshEffectsIn(System.Windows.DependencyObject node)
        {
            try
            {
                if (node is SoulLink.Controls.GlowDot dot) dot.RefreshPulse();
                int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < n; i++)
                    RefreshEffectsIn(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
            }
            catch { }
        }

        public static void Apply()
        {
            var settings = App.Config?.Settings;
            int mode = settings?.ThemeMode ?? 0;
            IsDark = mode == 2 || (mode == 0 && !SystemUsesLightTheme());
            Palette = DesktopCustomArtwork.Resolve(settings, IsDark, IsDark ? settings?.DarkThemeId : settings?.LightThemeId);
            var palette = Palette;
            Color Accent(byte alpha) => DesktopPalette.WithAlpha(palette.Accent, alpha);
            Color Surface(byte alpha) => DesktopPalette.WithAlpha(palette.Surface, alpha);
            var resources = Application.Current?.Resources;
            if (resources == null) return;
            ResourceDictionary styles = resources;
            foreach (var merged in resources.MergedDictionaries)
                if (merged.Contains("TextPrimary")) styles = merged;

            resources["Bg0"] = palette.Base;
            resources["Bg1"] = palette.Tint;
            resources["Cyan"] = palette.Accent;
            resources["Violet"] = palette.Motif;
            resources["Magenta"] = IsDark ? Color.FromRgb(255, 132, 211) : Color.FromRgb(164, 42, 111);
            resources["Mint"] = IsDark ? Color.FromRgb(74, 224, 136) : Color.FromRgb(0, 125, 61);
            resources["Amber"] = IsDark ? Color.FromRgb(255, 196, 107) : Color.FromRgb(156, 93, 0);
            resources["Rose"] = IsDark ? Color.FromRgb(255, 137, 163) : Color.FromRgb(177, 34, 70);

            SetBrush(styles, "TextPrimary", palette.Text);
            SetBrush(styles, "TextSecondary", palette.Secondary);
            SetBrush(styles, "TextMuted", palette.Secondary);
            SetBrush(styles, "StrokeSoft", Accent(42));
            SetBrush(styles, "StrokeMid", Accent(77));
            SetBrush(styles, "CyanBrush", palette.Accent);
            SetBrush(styles, "VioletBrush", palette.Motif);
            SetBrush(styles, "MintBrush", (Color)resources["Mint"]);
            SetBrush(styles, "AmberBrush", (Color)resources["Amber"]);
            SetBrush(styles, "RoseBrush", (Color)resources["Rose"]);
            SetBrush(styles, "MagentaBrush", (Color)resources["Magenta"]);
            SetBrush(styles, "DesktopAccent", palette.Accent);
            SetBrush(styles, "DesktopChrome", palette.Tint);
            ApplyDesktopSurfaces(styles, palette);
            SetBrush(styles, "DesktopRow", Accent(IsDark ? (byte)29 : (byte)18));
            SetBrush(styles, "DesktopSelection", Accent(IsDark ? (byte)48 : (byte)34));
            styles["DesktopPopupAnimation"] = AnimationsDisabled ? PopupAnimation.None : PopupAnimation.Fade;
            SetBrush(styles, "PanelBackground", Surface(130));
            SetBrush(styles, "FieldBackground", Surface(195));
            SetBrush(styles, "WindowBackground", palette.Base);
            styles["DialogUnderlay"] = palette.IsPlain ? Ink(palette.Base) : Gradient(new Point(0, 0), new Point(1, 1),
                (palette.Base, 0), (palette.Tint, .57), (DesktopPalette.Mix(palette.Surface, palette.Motif, .22), 1));
            SetBrush(styles, "DialogSurface", palette.Surface);
            SetBrush(styles, "PopupBackground", palette.Surface);
            SetBrush(styles, "HoverFill", Accent(29));
            SetBrush(styles, "PressedFill", Accent(51));
            SetBrush(styles, "SelectedFill", Accent(48));
            SetBrush(styles, "ButtonBackground", Surface(170));
            SetBrush(styles, "ButtonBorder", Accent(64));
            SetBrush(styles, "DisabledFill", DesktopPalette.WithAlpha(palette.Secondary, 55));
            SetBrush(styles, "DisabledStroke", DesktopPalette.WithAlpha(palette.Secondary, 85));
            SetBrush(styles, "ControlAccentText", palette.Id == DesktopCustomArtwork.Id
                ? DesktopCustomArtwork.TextOnAccent(palette.Accent) : IsDark ? palette.Base : Colors.White);
            SetBrush(styles, "SubtleSurface", Accent(18));
            SetBrush(styles, "ScrollThumb", DesktopPalette.WithAlpha(palette.Secondary, 105));
            SetBrush(styles, "InputBg", palette.IsPlain ? palette.Surface : Surface(213));
            styles["GlassFill"] = palette.IsPlain ? Ink(palette.Surface) : Gradient(new Point(0, 0), new Point(.7, 1),
                (DesktopPalette.WithAlpha(palette.MaterialStart, 213), 0), (DesktopPalette.WithAlpha(palette.MaterialEnd, 180), 1));
            styles["GlassFillStrong"] = palette.IsPlain ? Ink(palette.Surface) : Gradient(new Point(0, 0), new Point(.7, 1),
                (DesktopPalette.WithAlpha(palette.MaterialStart, 230), 0), (DesktopPalette.WithAlpha(palette.MaterialEnd, 182), 1));
            styles["GlassStroke"] = palette.IsPlain ? Ink(Accent(81)) : Gradient(new Point(0, 0), new Point(1, 1),
                (DesktopPalette.WithAlpha(palette.Highlight, 132), 0), (Accent(65), .45), (Accent(38), 1));
            styles["FieldFill"] = palette.IsPlain ? Ink(palette.Surface) : Gradient(new Point(0, 0), new Point(0, 1),
                (Surface(239), 0), (Surface(196), 1));
            styles["AccentGradientSoft"] = palette.IsPlain ? Ink(Accent(36)) : Gradient(new Point(0, 0), new Point(1, 1),
                (DesktopPalette.WithAlpha(palette.Highlight, 53), 0), (Accent(37), .55), (Accent(20), 1));
            styles["RingGradient"] = palette.IsPlain ? Ink(Accent(69)) : Gradient(new Point(0, 0), new Point(1, 1),
                (Accent(0), 0), (DesktopPalette.WithAlpha(palette.Highlight, 105), .47), (Accent(0), 1));
            Color danger = (Color)resources["Rose"];
            styles["DangerGradient"] = palette.IsPlain ? Ink(danger) : Gradient(new Point(0, 0), new Point(1, 1),
                (danger, 0), (DesktopPalette.Mix(danger, palette.Motif, .16), .72),
                (DesktopPalette.Mix(danger, (Color)resources["Amber"], .24), 1));
            bool glowOn = !palette.IsPlain && !AnimationsDisabled;
            styles["GlowCyan"] = Glow(palette.Accent, glowOn ? .56 : 0, 19);
            styles["GlowViolet"] = Glow(palette.Motif, glowOn ? .51 : 0, 18);
            styles["SoftGlow"] = Glow(palette.Highlight, glowOn ? .39 : 0, 13);
            int shadowTier = App.Config?.Settings?.EffectTier ?? 1;
            bool shadowOn = shadowTier <= Models.AppSettings.EffectTierLevel.Medium && !IsDark && !palette.IsPlain;

            styles["CardShadow"] = shadowOn
                ? new DropShadowEffect
                {
                    Color = Color.FromRgb(102, 80, 140),
                    Opacity = .20,
                    ShadowDepth = 5,
                    // 34 → 20：模糊半径决定 CPU 光栅化面积（半径² 量级）。
                    // 亮色下 20 与 34 视觉几乎无差（阴影本就只有 20% 不透明度）。
                    BlurRadius = 20,
                    Direction = 270
                }
                : new DropShadowEffect
                {
                    // 等效于「没有阴影」：不参与任何光栅化预算
                    Color = Colors.Transparent,
                    Opacity = 0,
                    ShadowDepth = 0,
                    BlurRadius = 0,
                    Direction = 270
                };
            styles["AccentGradient"] = palette.IsPlain ? Ink(palette.Accent) : Gradient(new Point(0, 0), new Point(1, 1),
                (palette.Accent, 0), (palette.Accent, .68), (palette.Motif, 1));
            if (Application.Current.MainWindow is SoulLink.Views.MainWindow mainWindow)
            {
                mainWindow.ApplyMainPageCardTheme();
                mainWindow.NavRail.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "NavBackground");
                mainWindow.NavRail.SetResourceReference(Border.BorderBrushProperty, "StrokeMid");
                mainWindow.UpdateNavSurfaceShape(mainWindow.IsNavCollapsedForTheme);
            }
            AppearanceChanged?.Invoke();
        }

        public static void ApplyWindow(Window window)
        {
            if (DependencyPropertyHelper.GetValueSource(window, Window.BackgroundProperty).BaseValueSource == BaseValueSource.Default)
                window.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
        }

        public static bool SystemUsesLightTheme()
        {
            try
            {
                object? value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0);
                return value is int i && i != 0;
            }
            catch { return false; }
        }

        private static void ApplyDesktopSurfaces(ResourceDictionary styles, DesktopPalette palette)
        {
            styles["DesktopCardRadius"] = new CornerRadius(palette.Radius);
            if (palette.IsPlain)
            {
                SetBrush(styles, "DesktopPanel", palette.Surface);
                SetBrush(styles, "OverviewAccountsCard", palette.Surface);
                SetBrush(styles, "OverviewActionsCard", palette.Surface);
                SetBrush(styles, "OverviewAutomationCard", palette.Surface);
                SetBrush(styles, "OverviewLogCard", palette.Surface);
                SetBrush(styles, "NavBackground", palette.Tint);
                return;
            }

            Color Top(byte alpha) => DesktopPalette.WithAlpha(palette.MaterialStart, alpha);
            Color End(byte alpha) => DesktopPalette.WithAlpha(palette.MaterialEnd, alpha);
            Color Nav(byte alpha) => DesktopPalette.WithAlpha(palette.Tint, alpha);
            Color Surface(byte alpha) => DesktopPalette.WithAlpha(palette.Surface, alpha);
            Brush defaultCoat = palette.Id is "L2" or "L5" or "L8" or "D2" or "D4"
                ? Radial(new Point(.83, .02), .92, 1.2, (Top(palette.MaterialStartAlpha), 0),
                    (Surface(234), .54), (End(palette.MaterialEndAlpha), 1))
                : Gradient(new Point(0, .2), new Point(1, .86), (Top(palette.MaterialStartAlpha), 0),
                    (Surface(234), .54), (End(palette.MaterialEndAlpha), 1));
            styles["DesktopPanel"] = Gradient(new Point(0, 0), new Point(1, 1),
                (Top(227), 0), (Surface(208), .48), (End(168), 1));
            styles["OverviewAccountsCard"] = palette.Id switch
            {
                "P2" => Radial(new Point(.56, .12), .69, .89, (Top(73), 0), (Top(136), .61), (Top(238), 1)),
                "P3" => Radial(new Point(.77, .84), .63, 1.02, (Top(81), 0), (Top(169), .57), (End(232), 1)),
                "P4" => Radial(new Point(.39, .92), .61, .96, (Top(73), 0), (End(146), .56), (Top(231), 1)),
                "P5" => Radial(new Point(.03, .45), .73, .89, (Top(88), 0), (Top(164), .59), (End(229), 1)),
                _ => defaultCoat,
            };
            styles["OverviewActionsCard"] = palette.Id == "P1"
                ? Radial(new Point(.60, 1), .67, 1.01, (Top(57), 0), (Top(132), .58), (End(218), 1))
                : defaultCoat;
            styles["OverviewAutomationCard"] = palette.Id switch
            {
                "P1" => Radial(new Point(.65, .08), .71, 1.05, (Top(57), 0), (Top(124), .60), (End(216), 1)),
                "P3" => Radial(new Point(.36, .40), .69, .76, (Top(128), 0), (Surface(161), .56), (End(224), 1)),
                _ => defaultCoat,
            };
            styles["OverviewLogCard"] = palette.Id == "P4"
                ? Radial(new Point(.33, .51), .70, 1.18, (Top(13), 0), (Top(78), .58), (End(216), 1))
                : Gradient(new Point(0, .25), new Point(1, .61), (Top(243), 0),
                    (Surface(palette.Id is "P1" or "P3" ? (byte)145 : (byte)212), .43), (End(43), .84), (End(22), 1));
            styles["NavBackground"] = palette.Id == "P5"
                ? Gradient(new Point(0, 0), new Point(0, 1), (Nav(249), 0), (Nav(224), .46), (Nav(70), .58),
                    (Nav(18), .69), (Nav(57), .89), (Nav(125), 1))
                : Gradient(new Point(0, .15), new Point(1, 1), (Nav(249), 0), (Nav(224), .64), (Nav(177), 1));
        }

        private static RadialGradientBrush Radial(Point center, double radiusX, double radiusY, params (Color Color, double Offset)[] stops)
        {
            var brush = new RadialGradientBrush { Center = center, GradientOrigin = center, RadiusX = radiusX, RadiusY = radiusY };
            foreach (var stop in stops) brush.GradientStops.Add(new GradientStop(stop.Color, stop.Offset));
            brush.Freeze();
            return brush;
        }

        private static SolidColorBrush Ink(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static DropShadowEffect Glow(Color color, double opacity, double blur)
        {
            var effect = new DropShadowEffect { Color = color, Opacity = opacity, ShadowDepth = 0, BlurRadius = blur };
            effect.Freeze();
            return effect;
        }
        private static LinearGradientBrush Gradient(Point start, Point end, params (Color Color,double Offset)[] stops)
        {
            var brush=new LinearGradientBrush{StartPoint=start,EndPoint=end};
            foreach(var stop in stops)brush.GradientStops.Add(new GradientStop(stop.Color,stop.Offset));
            brush.Freeze();return brush;
        }

        private static void SetBrush(ResourceDictionary resources, string key, Color color)
        {
            if (resources[key] is SolidColorBrush { IsFrozen: false } brush) brush.Color = color;
            else resources[key] = new SolidColorBrush(color);
        }
    }
}
