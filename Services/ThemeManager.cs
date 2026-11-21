using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Effects;
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
                if (node is SoulLink.Controls.AuroraBackdrop bd) bd.RefreshAnimationState();
                int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < n; i++)
                    RefreshEffectsIn(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
            }
            catch { }
        }

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
            SetBrush(styles, "TextMuted", IsDark ? Color.FromRgb(165, 171, 205) : Color.FromRgb(92, 76, 120));
            SetBrush(styles, "StrokeSoft", IsDark ? Color.FromArgb(36, 255, 255, 255) : Color.FromArgb(42, 102, 66, 139));
            SetBrush(styles, "StrokeMid", IsDark ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(64, 102, 66, 139));
            SetBrush(styles, "CyanBrush", (Color)resources["Cyan"]);
            SetBrush(styles, "VioletBrush", (Color)resources["Violet"]);
            SetBrush(styles, "MintBrush", (Color)resources["Mint"]);
            SetBrush(styles, "AmberBrush", (Color)resources["Amber"]);
            SetBrush(styles, "RoseBrush", (Color)resources["Rose"]);
            SetBrush(styles, "MagentaBrush", (Color)resources["Magenta"]);
            // 月莓星轨使用近黑玻璃；云端棱镜使用乳白磨砂，不用反色滤镜。
            SetBrush(styles, "NavBackground", IsDark ? Color.FromArgb(226, 13, 15, 35) : Color.FromArgb(230, 255, 251, 255));
            SetBrush(styles, "PanelBackground", IsDark ? Color.FromArgb(38, 27, 25, 57) : Color.FromArgb(158, 255, 255, 255));
            SetBrush(styles, "FieldBackground", IsDark ? Color.FromArgb(118, 10, 12, 33) : Color.FromArgb(205, 255, 255, 255));
            SetBrush(styles, "WindowBackground", IsDark ? Color.FromRgb(9, 9, 25) : Color.FromRgb(251, 247, 254));
            SetBrush(styles, "DialogSurface", IsDark ? Color.FromRgb(17, 17, 41) : Color.FromRgb(251, 247, 255));
            SetBrush(styles, "PopupBackground", IsDark ? Color.FromArgb(250, 27, 25, 53) : Color.FromArgb(252, 255, 252, 255));
            SetBrush(styles, "HoverFill", IsDark ? Color.FromArgb(36, 255, 255, 255) : Color.FromArgb(32, 123, 91, 175));
            SetBrush(styles, "PressedFill", IsDark ? Color.FromArgb(58, 255, 255, 255) : Color.FromArgb(45, 119, 87, 167));
            SetBrush(styles, "SelectedFill", IsDark ? Color.FromArgb(56, 201, 183, 244) : Color.FromArgb(45, 157, 122, 204));
            SetBrush(styles, "ButtonBackground", IsDark ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(176, 255, 255, 255));
            SetBrush(styles, "ButtonBorder", IsDark ? Color.FromArgb(42, 255, 255, 255) : Color.FromArgb(60, 119, 88, 159));
            // ★ 3.1.11：不可用按钮的中性灰。深色用淡白叠加；浅色用带紫调的灰，
            //   否则叠在近白玻璃卡片上完全看不出来（用户原话"没有变灰色"）。
            SetBrush(styles, "DisabledFill", IsDark ? Color.FromArgb(61, 255, 255, 255) : Color.FromArgb(58, 118, 110, 140));
            SetBrush(styles, "DisabledStroke", IsDark ? Color.FromArgb(92, 255, 255, 255) : Color.FromArgb(86, 118, 110, 140));
            SetBrush(styles, "ControlAccentText", Colors.White);
            SetBrush(styles, "SubtleSurface", IsDark ? Color.FromArgb(16, 255, 255, 255) : Color.FromArgb(20, 106, 81, 153));
            SetBrush(styles, "ScrollThumb", IsDark ? Color.FromArgb(85, 207, 195, 239) : Color.FromArgb(100, 128, 103, 167));
            // ResourceDictionary 中的画刷与 Effect 默认冻结；必须替换实例，不可原地改色。
            styles["GlassFill"] = Gradient(new Point(.0,.0),new Point(.7,1),
                (IsDark?Color.FromArgb(53,41,36,76):Color.FromArgb(188,255,255,255),0),
                (IsDark?Color.FromArgb(27,23,27,57):Color.FromArgb(133,255,255,255),.35),
                (IsDark?Color.FromArgb(22,29,38,65):Color.FromArgb(108,240,235,255),1));
            styles["GlassFillStrong"] = Gradient(new Point(.0,.0),new Point(.7,1),
                (IsDark?Color.FromArgb(74,34,30,67):Color.FromArgb(216,255,255,255),0),
                (IsDark?Color.FromArgb(48,23,27,55):Color.FromArgb(173,255,255,255),.4),
                (IsDark?Color.FromArgb(33,29,36,66):Color.FromArgb(153,242,237,255),1));
            styles["GlassStroke"] = Gradient(new Point(0,0),new Point(1,1),
                (IsDark?Color.FromArgb(102,231,213,255):Color.FromArgb(140,155,122,191),0),
                (IsDark?Color.FromArgb(36,255,255,255):Color.FromArgb(65,133,108,176),.45),
                (IsDark?Color.FromArgb(18,255,255,255):Color.FromArgb(50,128,161,188),1));
            // ★★ 2.17.47：卡片阴影改为按【档位 + 主题】决定。★★
            //
            //   实测（ETW + Microsoft 符号 + 二分，2560×1368、LaunchPage、12 秒稳态）：
            //     · 保留 CardShadow（DropShadowEffect BlurRadius=34）：52.5% 单核
            //     · 关闭阴影：                                       38.2% 单核
            //     → 阴影单独占约 14 个单核百分点。
            //   热点函数是 CHwRasterizer::RasterizeEdges —— WPF 的 Effect 全部在
            //   CPU 上算高斯模糊，卡片内容一变就整块重算。
            //
            //   视觉验证（像素级 diff，放大 6 倍）：
            //     · 深色主题 #02040A/55% 叠在 #090919 上 —— 卡片轮廓完全看不出来，
            //       这 14 个点是纯浪费，关闭零视觉损失。
            //     · 亮色主题 #6650508C/20% 叠在 #FBF7FE 上 —— 可见（轻微紫晕），保留。
            //
            //   最终策略（用户选定）：
            //     低档（EffectTier=2）→ 一律关闭。选低档就是要性能。
            //     中/高档           → 亮色保留（缩小模糊半径）；深色仍关闭。
            int shadowTier = App.Config?.Settings?.EffectTier ?? 1;
            bool shadowOn = shadowTier <= Models.AppSettings.EffectTierLevel.Medium && !IsDark;

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
            styles["AccentGradient"] = Gradient(new Point(0,0),new Point(1,1),
                (IsDark?Color.FromRgb(146,112,240):Color.FromRgb(157,119,204),0),
                (IsDark?Color.FromRgb(118,206,241):Color.FromRgb(108,160,206),.55),
                (IsDark?Color.FromRgb(255,139,207):Color.FromRgb(215,133,179),1));
            if (Application.Current.MainWindow is SoulLink.Views.MainWindow mainWindow)
            {
                Brush nav = (Brush)styles["NavBackground"];
                Brush stroke = (Brush)styles["StrokeMid"];
                mainWindow.NavRail.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "NavBackground");
                // 旧 Popup 面板已移除，侧栏只有一个 Border（NavRail）
                mainWindow.NavRail.BorderBrush = stroke;

                // ★ 收起态：图标栏是独立的圆角卡片，四个角都要圆。
                //   展开态：它变成整张卡片的左半截，右侧两角必须切成直角去接面板，
                //   否则两段圆角之间会夹出一个豁口。
                mainWindow.UpdateNavSurfaceShape(mainWindow.IsNavCollapsedForTheme);
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
            // 文字与状态色均由动态资源或明确状态画刷负责；不再覆盖浅色文字。
            foreach (Window window in Application.Current.Windows)
                if (window is SoulLink.Views.MainWindow main) main.Shell.Background = (Brush)Application.Current.FindResource("WindowBackground");
        }

        public static void ApplyWindow(Window window)
        {
            window.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
            if (!window.IsLoaded) return;
            ApplyToTree(window);
            RefreshSurfaces(window);
            // 保留业务状态色；不把所有浅色文字统一替换成次要文字色。
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
            ("WindowBackground", System.Windows.Controls.Border.BackgroundProperty),
            ("DialogSurface", System.Windows.Controls.Border.BackgroundProperty),
            ("PopupBackground", System.Windows.Controls.Border.BackgroundProperty),
            ("PopupBackground", System.Windows.Controls.ContextMenu.BackgroundProperty),
            ("StrokeMid", System.Windows.Controls.ContextMenu.BorderBrushProperty),
            ("FieldBackground", System.Windows.Controls.Border.BackgroundProperty),
            ("SubtleSurface", System.Windows.Controls.Border.BackgroundProperty),
            ("ControlAccentText", System.Windows.Controls.TextBlock.ForegroundProperty)
        };

        private static void ApplyToOpenWindows()
        {
            if (Application.Current == null) return;
            foreach (Window window in Application.Current.Windows)
            {
                window.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
                ApplyToTree(window);
                RefreshSurfaces(window);
            }
        }

        private static void RefreshSurfaces(DependencyObject root)
        {
            if (root is ContextMenu menu)
            {
                menu.SetResourceReference(ContextMenu.BackgroundProperty, "PopupBackground");
                menu.SetResourceReference(ContextMenu.BorderBrushProperty, "StrokeMid");
            }
            for (int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
                RefreshSurfaces(VisualTreeHelper.GetChild(root,i));
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

        private static LinearGradientBrush Gradient(Point start, Point end, params (Color Color,double Offset)[] stops)
        {
            var brush=new LinearGradientBrush{StartPoint=start,EndPoint=end};
            foreach(var stop in stops)brush.GradientStops.Add(new GradientStop(stop.Color,stop.Offset));
            brush.Freeze();return brush;
        }

        private static void SetBrush(ResourceDictionary resources, string key, Color color)
        {
            // XAML 中的资源画刷可能已冻结，直接替换实例，不能修改其 Color。
            resources[key] = new SolidColorBrush(color);
        }
    }
}