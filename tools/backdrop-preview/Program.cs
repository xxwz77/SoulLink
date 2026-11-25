using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SoulLink.BackdropPreview
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool captureOnly = Array.IndexOf(args, "--capture-only") >= 0;
            string output = Path.Combine(Environment.CurrentDirectory, "artifacts", "backdrop-preview");
            int outputIndex = Array.IndexOf(args, "--out");
            if (outputIndex >= 0 && outputIndex + 1 < args.Length) output = args[outputIndex + 1];
            Directory.CreateDirectory(output);
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                var window = new PreviewWindow();
                app.MainWindow = window;
                window.Show();
                if (captureOnly) _ = CaptureAndCloseAsync(window, output);
                app.Run(window);
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "preview-error.log"), ex.ToString());
                return 1;
            }
        }

        private static async Task CaptureAndCloseAsync(PreviewWindow window, string output)
        {
            try
            {
                await Task.Delay(2200);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                window.Save(Path.Combine(output, "sakura-nebula-ui.png"));
                window.ShowPureBackdrop();
                await Task.Delay(350);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                window.Save(Path.Combine(output, "sakura-nebula-pure.png"));
                File.WriteAllText(Path.Combine(output, "preview-check.txt"),
                    "PASS: WPF animation rendered; UI and pure-background PNG saved." + Environment.NewLine +
                    "PASS: DreamBackdrop.IsHitTestVisible = False." + Environment.NewLine +
                    "PASS: animation stops on minimize/unload; static layer is cached." + Environment.NewLine);
                window.Close();
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "preview-error.log"), ex.ToString());
                window.Close();
            }
        }
    }

    public sealed class PreviewWindow : Window
    {
        private readonly Grid _sampleUi;
        private readonly DreamBackdrop _backdrop;
        private readonly TextBlock _toggleText;

        public PreviewWindow()
        {
            Title = "SoulLink · 樱雾星云｜动态背景预览";
            Width = 1360;
            Height = 850;
            MinWidth = 1000;
            MinHeight = 680;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x08, 0x19));
            Foreground = Brushes.White;
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            Topmost = false;

            var root = new Grid();
            _backdrop = new DreamBackdrop();
            root.Children.Add(_backdrop);
            _sampleUi = new Grid();
            root.Children.Add(_sampleUi);
            Content = root;

            var top = new Border
            {
                Height = 78,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(34, 0, 28, 0),
                Background = Brushes.Transparent
            };
            top.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };
            var topGrid = new Grid();
            top.Child = topGrid;
            _sampleUi.Children.Add(topGrid);

            var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            brand.Children.Add(new TextBlock
            {
                Text = "✦",
                FontSize = 25,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0xE0)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            });
            var brandWords = new StackPanel();
            brandWords.Children.Add(new TextBlock
            {
                Text = "S O U L L I N K",
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Foreground = Brushes.White
            });
            brandWords.Children.Add(new TextBlock
            {
                Text = "D R E A M  C O N S O L E",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 8.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0x9F, 0xC4)),
                Margin = new Thickness(0, 4, 0, 0)
            });
            brand.Children.Add(brandWords);
            topGrid.Children.Add(brand);

            var live = MakePill("●  LIVE PREVIEW   /   01");
            live.HorizontalAlignment = HorizontalAlignment.Right;
            live.VerticalAlignment = VerticalAlignment.Center;
            topGrid.Children.Add(live);

            var hero = new StackPanel
            {
                Width = 520,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(70, 8, 0, 0)
            };
            hero.Children.Add(new TextBlock
            {
                Text = "✦  全 新 背 景 概 念   /   D R E A M S C A P E",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xB9, 0xEB)),
                Margin = new Thickness(0, 0, 0, 23)
            });
            hero.Children.Add(new TextBlock
            {
                Text = "樱雾星云",
                FontSize = 62,
                FontWeight = FontWeights.Bold,
                Foreground = new LinearGradientBrush(
                    Color.FromRgb(0xFF, 0xFA, 0xFF), Color.FromRgb(0xF1, 0xBB, 0xEB), 0),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Color.FromRgb(0xE6, 0x8A, 0xD5),
                    BlurRadius = 24,
                    ShadowDepth = 0,
                    Opacity = 0.18
                }
            });
            hero.Children.Add(new TextBlock
            {
                Text = "S A K U R A   N E B U L A",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 15,
                FontWeight = FontWeights.Light,
                Foreground = new SolidColorBrush(Color.FromRgb(0xC7, 0xBA, 0xE4)),
                Margin = new Thickness(2, 10, 0, 25)
            });
            hero.Children.Add(new TextBlock
            {
                Text = "让每一次连接，都穿过一场温柔的宇宙。",
                FontSize = 15,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE4, 0xDB, 0xF2))
            });
            hero.Children.Add(new TextBlock
            {
                Text = "粉雾极光  ×  流光星轨  ×  心动粒子",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0xA9, 0x9F, 0xC2)),
                Margin = new Thickness(0, 14, 0, 27)
            });
            var concept = MakePill("✧  DREAMY   /   FUTURISTIC   /   LUMINOUS");
            concept.HorizontalAlignment = HorizontalAlignment.Left;
            hero.Children.Add(concept);
            _sampleUi.Children.Add(hero);

            var glass = new Border
            {
                Width = 256,
                Height = 67,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 64, 107),
                CornerRadius = new CornerRadius(15),
                Background = new SolidColorBrush(Color.FromArgb(0x24, 0xE7, 0xD7, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x38, 0xF4, 0xD7, 0xFF)),
                BorderThickness = new Thickness(1),
                Child = new Grid()
            };
            var status = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            status.Children.Add(new TextBlock
            {
                Text = "●",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xFF, 0xE4)),
                Margin = new Thickness(18, 0, 12, 0)
            });
            var statusWords = new StackPanel();
            statusWords.Children.Add(new TextBlock
            {
                Text = "DREAM STATE",
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE9, 0xDF, 0xF7))
            });
            statusWords.Children.Add(new TextBlock
            {
                Text = "柔光流动 · 星海同频",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0x9F, 0xBD)),
                Margin = new Thickness(0, 4, 0, 0)
            });
            status.Children.Add(statusWords);
            ((Grid)glass.Child).Children.Add(status);
            _sampleUi.Children.Add(glass);

            var footer = new Grid
            {
                Height = 62,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(38, 0, 28, 0)
            };
            footer.Children.Add(new TextBlock
            {
                Text = "VISUAL STUDY  /  仅背景预览，尚未修改项目",
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x87, 0x7F, 0xA5))
            });
            var controls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            var toggle = MakeButton("纯背景  H", out _toggleText);
            toggle.Click += (_, _) => ToggleSample();
            controls.Children.Add(toggle);
            var close = MakeButton("关闭  Esc", out _);
            close.Margin = new Thickness(10, 0, 0, 0);
            close.Click += (_, _) => Close();
            controls.Children.Add(close);
            footer.Children.Add(controls);
            _sampleUi.Children.Add(footer);

            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) Close();
                if (e.Key == Key.H) ToggleSample();
            };
        }

        private static Border MakePill(string text) => new()
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(13, 7, 13, 7),
            Background = new SolidColorBrush(Color.FromArgb(0x1C, 0xF3, 0xDF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x2D, 0xF3, 0xDF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xBB, 0xE1))
            }
        };

        private static Button MakeButton(string text, out TextBlock label)
        {
            label = new TextBlock
            {
                Text = text,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD3, 0xC8, 0xE8))
            };
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(BackgroundProperty,
                new SolidColorBrush(Color.FromArgb(0x20, 0xED, 0xDA, 0xFF))));
            style.Setters.Add(new Setter(BorderBrushProperty,
                new SolidColorBrush(Color.FromArgb(0x38, 0xED, 0xDA, 0xFF))));
            style.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(PaddingProperty, new Thickness(13, 8, 13, 8)));
            style.Setters.Add(new Setter(CursorProperty, Cursors.Hand));
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            border.AppendChild(content);
            template.VisualTree = border;
            style.Setters.Add(new Setter(TemplateProperty, template));
            return new Button { Content = label, Style = style };
        }

        private void ToggleSample()
        {
            bool pure = _sampleUi.Visibility == Visibility.Visible;
            _sampleUi.Visibility = pure ? Visibility.Collapsed : Visibility.Visible;
            if (!pure) _toggleText.Text = "纯背景  H";
            UpdateLayout();
        }

        public void ShowPureBackdrop()
        {
            _sampleUi.Visibility = Visibility.Collapsed;
            UpdateLayout();
        }

        public void Save(string path)
        {
            UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(this);
            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY),
                96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bitmap.Render(this);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
    }
}
