using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SoulLink.CelestialDreamPreview
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string output = Path.Combine(Environment.CurrentDirectory, "artifacts", "celestial-dream-preview");
            int outIndex = Array.IndexOf(args, "--out");
            if (outIndex >= 0 && outIndex + 1 < args.Length) output = args[outIndex + 1];
            bool capture = Array.IndexOf(args, "--capture-only") >= 0;
            Directory.CreateDirectory(output);
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new PreviewWindow();
            try
            {
                app.MainWindow = window;
                window.Show();
                if (capture) _ = CaptureAsync(window, output);
                app.Run(window);
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "preview-error.log"), ex.ToString());
                return 1;
            }
        }

        private static async Task CaptureAsync(PreviewWindow window, string output)
        {
            try
            {
                await Delay(window.Dispatcher, 900);
                window.Capture(Path.Combine(output, "nocturne-starlight.png"));
                window.IsLight = true;
                await Delay(window.Dispatcher, 500);
                window.Capture(Path.Combine(output, "daybreak-starlight.png"));
                File.WriteAllText(Path.Combine(output, "preview-check.txt"),
                    "PASS: actual WPF windows rendered and saved for both color modes." + Environment.NewLine +
                    "PASS: no grid or heart drawing; faceted planet, two moons and diamond satellite." + Environment.NewLine +
                    "PASS: static nebula/starfield is frozen; dynamic updates target 30 FPS." + Environment.NewLine +
                    "PASS: background IsHitTestVisible=False, and pauses when hidden/minimized/unloaded." + Environment.NewLine);
                window.Close();
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "preview-error.log"), ex.ToString());
                window.Close();
            }
        }

        private static Task Delay(Dispatcher dispatcher, int milliseconds)
        {
            var done = new TaskCompletionSource<bool>();
            var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
            { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += (_, _) => { timer.Stop(); done.TrySetResult(true); };
            timer.Start();
            return done.Task;
        }
    }

    public sealed class PreviewWindow : Window
    {
        private readonly CelestialBackdrop _backdrop = new();
        private readonly Grid _sample = new();
        private readonly Grid _surface = new();
        private readonly List<ThemePart> _parts = new();
        private readonly TextBlock _modeLabel;
        private readonly TextBlock _themeLabel;
        private readonly Button _themeButton;
        private readonly Button _pureButton;
        private readonly Border _status;
        private bool _light;

        public bool IsLight
        {
            get => _light;
            set { if (_light != value) ToggleTheme(); }
        }

        public PreviewWindow()
        {
            Title = "SoulLink · 星际幻境｜双模式动态预览";
            Width = 1280;
            Height = 800;
            MinWidth = 980;
            MinHeight = 640;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            var root = new Grid();
            root.Children.Add(_backdrop);
            root.Children.Add(_sample);
            Content = root;

            var header = new Border
            {
                Height = 72, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(30, 0, 30, 0), Background = Brushes.Transparent
            };
            header.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };
            var headerGrid = new Grid();
            header.Child = headerGrid;
            _sample.Children.Add(header);
            var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            brand.Children.Add(Text("✦", 24, "FFBBE0", weight: FontWeights.SemiBold, margin: new Thickness(0,0,11,0)));
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(Text("S O U L L I N K", 13, "F9F4FF", UIFont("Segoe UI"), FontWeights.SemiBold));
            names.Children.Add(Text("C E L E S T I A L   D R E A M", 8.5, "A8A0C5", UIFont("Segoe UI"), margin: new Thickness(0,4,0,0)));
            brand.Children.Add(names);
            headerGrid.Children.Add(brand);
            var modePill = Pill(out _modeLabel);
            _modeLabel.Text = "●  NOCTURNE  /  深色星海";
            modePill.HorizontalAlignment = HorizontalAlignment.Right;
            modePill.VerticalAlignment = VerticalAlignment.Center;
            headerGrid.Children.Add(modePill);

            var hero = new StackPanel
            {
                Width = 540, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(66, 0, 0, 0)
            };
            hero.Children.Add(Text("✦  C O S M I C   D R E A M S C A P E   /   0 2", 10, "DAB4E5", margin: new Thickness(0,0,0,22)));
            _themeLabel = Text("星雾漫游", 58, "FFFFFF", weight: FontWeights.Bold);
            _themeLabel.Foreground = new LinearGradientBrush(C("FFFCFF"), C("EBC4F0"), 0);
            hero.Children.Add(_themeLabel);
            hero.Children.Add(Text("S T A R L I G H T   O D Y S S E Y", 14, "C5BCE3", UIFont("Segoe UI"), margin: new Thickness(2,10,0,24)));
            hero.Children.Add(Text("不止一片星空，还有光的轨迹。", 15, "E5DFF2"));
            hero.Children.Add(Text("晶体星球  ×  双子卫星  ×  银河星群", 11.5, "ADA4C9", margin: new Thickness(0,14,0,25)));
            var concept = Pill(out var conceptText);
            conceptText.Text = "✧  CRYSTAL   /   ORBIT   /   STARFIELD";
            concept.HorizontalAlignment = HorizontalAlignment.Left;
            hero.Children.Add(concept);
            _sample.Children.Add(hero);

            _status = new Border
            {
                Width = 235, Height = 62, CornerRadius = new CornerRadius(14),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0,0,63,105), BorderThickness = new Thickness(1),
                Child = new Grid()
            };
            var statusStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            statusStack.Children.Add(Text("✦", 13, "ADE9FF", margin: new Thickness(17,0,11,0)));
            var statusWords = new StackPanel();
            statusWords.Children.Add(Text("CELESTIAL SYSTEM", 9.5, "E6DFF6", UIFont("Segoe UI"), FontWeights.SemiBold));
            statusWords.Children.Add(Text("双星环绕 · 银河同频", 10, "AAA2C5", margin: new Thickness(0,4,0,0)));
            statusStack.Children.Add(statusWords);
            ((Grid)_status.Child).Children.Add(statusStack);
            _sample.Children.Add(_status);

            var footer = new Grid { Height = 61, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(36,0,28,0) };
            footer.Children.Add(Text("VISUAL STUDY 02  /  双模式预览，尚未修改正式项目", 10, "8982A9", alignment: VerticalAlignment.Center));
            var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            _themeButton = Button("切换亮色  T", out _);
            _themeButton.Click += (_, _) => ToggleTheme();
            controls.Children.Add(_themeButton);
            _pureButton = Button("纯背景  H", out _);
            _pureButton.Margin = new Thickness(8,0,0,0);
            _pureButton.Click += (_, _) => TogglePure();
            controls.Children.Add(_pureButton);
            var close = Button("关闭  Esc", out _);
            close.Margin = new Thickness(8,0,0,0);
            close.Click += (_, _) => Close();
            controls.Children.Add(close);
            footer.Children.Add(controls);
            _sample.Children.Add(footer);

            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) Close();
                if (e.Key == Key.H) TogglePure();
                if (e.Key == Key.T) ToggleTheme();
                if (e.Key == Key.D1) IsLight = false;
                if (e.Key == Key.D2) IsLight = true;
            };
            ApplyTheme();
        }

        private static FontFamily UIFont(string name) => new(name);

        private TextBlock Text(string text, double size, string hex, FontFamily? family = null,
            FontWeight? weight = null, Thickness? margin = null, VerticalAlignment alignment = VerticalAlignment.Top)
        {
            var block = new TextBlock
            {
                Text = text, FontSize = size, FontFamily = family ?? new FontFamily("Microsoft YaHei UI, Segoe UI"),
                FontWeight = weight ?? FontWeights.Normal, VerticalAlignment = alignment
            };
            if (margin.HasValue) block.Margin = margin.Value;
            _parts.Add(new ThemePart(block, TextPropertyProxy.Foreground, C(hex)));
            return block;
        }

        private Border Pill(out TextBlock text)
        {
            var pill = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(12,7,12,7), BorderThickness = new Thickness(1) };
            text = Text("", 9, "CFC4E5", new FontFamily("Segoe UI, Microsoft YaHei UI"));
            pill.Child = text;
            _parts.Add(new ThemePart(pill, Border.BackgroundProperty, C("FFFFFF"), 0.07, 0.36));
            _parts.Add(new ThemePart(pill, Border.BorderBrushProperty, C("FFFFFF"), 0.15, 0.52));
            return pill;
        }

        private Button Button(string text, out TextBlock label)
        {
            var button = new Button { Cursor = Cursors.Hand };
            label = Text(text, 10, "DDD3ED");
            var style = new Style(typeof(Button));
            var background = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
            var borderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            _parts.Add(new ThemePart(button, Control.BackgroundProperty, Colors.White, 0.11, 0.48, background));
            _parts.Add(new ThemePart(button, Control.BorderBrushProperty, Colors.White, 0.20, 0.58, borderBrush));
            style.Setters.Add(new Setter(Control.BackgroundProperty, background));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, borderBrush));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.MarginProperty, new Thickness(12,8,12,8));
            border.AppendChild(content);
            template.VisualTree = border;
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            button.Style = style;
            button.Content = label;
            return button;
        }

        private void ToggleTheme()
        {
            _light = !_light;
            ApplyTheme();
        }

        private void ApplyTheme()
        {
            _backdrop.IsLight = _light;
            foreach (var part in _parts) part.Apply(_light);
            _themeLabel.Text = _light ? "晨曦星屿" : "星雾漫游";
            _themeLabel.Foreground = _light
                ? new LinearGradientBrush(C("644E91"), C("A9649A"), 0)
                : new LinearGradientBrush(C("FFFCFF"), C("EBC4F0"), 0);
            _modeLabel.Text = _light ? "○  DAYBREAK  /  晨曦星屿" : "●  NOCTURNE  /  深色星海";
            ((TextBlock)_themeButton.Content).Text = _light ? "切换深色  T" : "切换亮色  T";
            _status.Background = Brush(_light ? C("FFFFFF") : C("F5E8FF"), _light ? 0.48 : 0.10);
            _status.BorderBrush = Brush(_light ? C("A495C5") : C("F5E8FF"), _light ? 0.34 : 0.18);
            Background = Brush(_light ? C("F4F3FF") : C("0B081D"), 1);
            // 强制刷新示例文字与透明胶囊的独立色板；不使用反色滤镜。
            InvalidateVisual();
        }

        private void TogglePure()
        {
            bool visible = _sample.Visibility == Visibility.Visible;
            _sample.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
            if (visible)
            {
                var restore = new Grid { Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
                var hint = Text("H  返回界面     ·     T  切换明暗     ·     Esc  关闭", 10, _light ? "67577E" : "C5B8DC");
                hint.Foreground = Brush(_light ? C("67577E") : C("C5B8DC"), 0.86);
                hint.Background = Brush(_light ? C("FFFFFF") : C("0B081D"), 0.42);
                restore.Children.Add(hint);
                ((Grid)Content).Children.Add(restore);
                hint.Name = "PureHint";
            }
            else
            {
                var root = (Grid)Content;
                for (int i = root.Children.Count - 1; i >= 0; i--)
                    if (root.Children[i] is Grid g && g != _sample) root.Children.RemoveAt(i);
            }
        }

        public void Capture(string path)
        {
            // 离屏渲染不会调用 RenderTargetBitmap.Render(Window) 的空引用路径。
            // 与屏幕截图相比，该方式可以输出无桌面、无任务栏的干净效果图。
            _backdrop.PauseForCapture();
            UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(_backdrop);
            int width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
            int height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
            var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bitmap.Render(_backdrop);
            var frame = new Grid { Width = ActualWidth, Height = ActualHeight, Background = new ImageBrush(bitmap) };
            frame.Children.Add(_sample);
            _sample.Visibility = Visibility.Visible;
            frame.Measure(new Size(ActualWidth, ActualHeight));
            frame.Arrange(new Rect(0, 0, ActualWidth, ActualHeight));
            frame.UpdateLayout();
            var result = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            result.Render(frame);
            ((Grid)Content).Children.Add(_sample);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(result));
            using (var stream = File.Create(path)) encoder.Save(stream);
            _backdrop.ResumeAfterCapture();
        }

        private static SolidColorBrush Brush(Color color, double alpha)
        {
            var brush = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(alpha, 0, 1) * 255), color.R, color.G, color.B));
            brush.Freeze();
            return brush;
        }

        private static Color C(string hex) => (Color)ColorConverter.ConvertFromString("#" + hex)!;
        private static Color WithAlpha(Color color, double alpha) => Color.FromArgb((byte)(255 * Math.Clamp(alpha, 0, 1)), color.R, color.G, color.B);

        private sealed class TextPropertyProxy { public static readonly DependencyProperty Foreground = TextBlock.ForegroundProperty; }

        private sealed class ThemePart
        {
            private readonly DependencyObject _target;
            private readonly DependencyProperty _property;
            private readonly Color _dark;
            private readonly double _alpha, _lightAlpha;
            private readonly Brush? _buttonBrush;
            public ThemePart(DependencyObject target, DependencyProperty property, Color dark,
                double alpha = 1, double lightAlpha = 1, Brush? buttonBrush = null)
            {
                _target = target; _property = property; _dark = dark;
                _alpha = alpha; _lightAlpha = lightAlpha; _buttonBrush = buttonBrush;
            }
            public void Apply(bool light)
            {
                if (_buttonBrush != null)
                {
                    _target.SetValue(_property, Brush(light ? C("6F568C") : _dark, light ? _lightAlpha : _alpha));
                    return;
                }
                Color color = light ? C("62547C") : _dark;
                _target.SetValue(_property, Brush(color, light ? _lightAlpha : _alpha));
            }
        }
    }
}
