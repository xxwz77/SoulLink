using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using SoulLink.Services;

namespace SoulLink.ViewDemo
{
    /// <summary>
    /// 中控台「多标签 + 多画面」交互样张。
    ///
    /// ★ 完全不连接 RDP、不加载 ActiveX。
    ///   每个"画面"都是纯 WPF 绘制的假桌面，用来观察：
    ///     · 单个 / 双列 / 网格 三种排列
    ///     · 点格子 / 点标签 切换焦点
    ///     · 保护层首击消费
    ///     · 双列下的滚动浏览（4/6/9 个画面）
    ///   格子位置由正式工程的 ViewLayout.Compute 计算，与真机布局一致。
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static int Main()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Startup += (_, __) =>
            {
                try
                {
                    var w = new DemoWindow();
                    var mode = ConsoleLayoutMode.Single;
                    int count = 6;
                    bool shield = true;
                    foreach (var a in Environment.GetCommandLineArgs().Skip(1))
                    {
                        if (a.StartsWith("--mode=", StringComparison.OrdinalIgnoreCase)
                            && int.TryParse(a.Substring(7), out int m))
                            mode = (ConsoleLayoutMode)Math.Clamp(m, 0, 2);
                        else if (a.StartsWith("--count=", StringComparison.OrdinalIgnoreCase)
                            && int.TryParse(a.Substring(8), out int n))
                            count = Math.Clamp(n, 1, 9);
                        else if (a.Equals("--noshield", StringComparison.OrdinalIgnoreCase))
                            shield = false;
                    }
                    w.Configure(mode, count, shield);
                    w.Show();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.ToString(), "ViewDemo 启动失败");
                    app.Shutdown(1);
                }
            };
            return app.Run();
        }
    }

    internal sealed class FakeScreen
    {
        public string User = "";
        public string Title = "";
        public Color Accent;
        public string Scene = "idle";
        public double Progress;
        public DateTime RotateAt;
    }

    internal sealed class DemoTile
    {
        public FakeScreen Screen = null!;
        public Border Chrome = null!;
        public Border Header = null!;
        public TextBlock HeaderText = null!;
        public Canvas ScreenCanvas = null!;
        public Grid Root = null!;
    }

    public sealed class DemoWindow : Window
    {
        private readonly List<FakeScreen> _screens = new();
        private readonly List<DemoTile> _tiles = new();
        private DemoTile? _focus;

        private readonly Canvas _tileHost = new();
        private readonly ScrollViewer _scroller = new();
        private readonly StackPanel _tabBar = new();
        private readonly TextBlock _hint = new();
        private readonly TextBlock _layoutLabel = new();
        private readonly Grid _shield = new();
        private readonly DispatcherTimer _anim = new() { Interval = TimeSpan.FromMilliseconds(90) };

        private ConsoleLayoutMode _mode = ConsoleLayoutMode.Single;
        public ConsoleLayoutMode InitialMode { get; set; } = ConsoleLayoutMode.Single;
        public int InitialCount { get; set; } = 6;
        public bool ShowShield { get; set; } = true;

        /// <summary>
        /// 构造完成后再套用启动参数。
        /// ★ 必须在这里做：构造函数里已经跑过一次布局，
        ///   如果在构造之后再改字段，界面不会跟着变。
        /// </summary>
        public void Configure(ConsoleLayoutMode mode, int count, bool shield)
        {
            ShowShield = shield;
            while (_screens.Count > count) RemoveLastNoLayout();
            if (_screens.Count < count) { SeedScreens(count - _screens.Count); BuildTiles(); }
            _mode = mode;
            if (_focus == null) _focus = _tiles.FirstOrDefault();
            ApplyLayout();
            RenderTabs();
            UpdateHint();
        }

        private void RemoveLastNoLayout()
        {
            if (_screens.Count <= 1) return;
            var s = _screens[^1];
            var t = _tiles.FirstOrDefault(x => ReferenceEquals(x.Screen, s));
            if (t != null)
            {
                _tileHost.Children.Remove(t.Chrome);
                _tiles.Remove(t);
                if (ReferenceEquals(_focus, t)) _focus = _tiles.FirstOrDefault();
            }
            _screens.Remove(s);
        }

        private static readonly Color[] Palette =
        {
            Color.FromRgb(0x7C, 0x5C, 0xFF), Color.FromRgb(0x41, 0xF0, 0xC0),
            Color.FromRgb(0xFF, 0x6B, 0x9D), Color.FromRgb(0x4F, 0xC3, 0xF7),
            Color.FromRgb(0xFF, 0xC8, 0x5B), Color.FromRgb(0xB3, 0x8B, 0xFF),
            Color.FromRgb(0x6E, 0xE7, 0xA4), Color.FromRgb(0xFF, 0x8A, 0x5B),
            Color.FromRgb(0x9A, 0xB4, 0xFF)
        };

        public DemoWindow()
        {
            Title = "SoulLink 多标签 / 多画面 交互样张（纯模拟，不连 RDP）";
            Width = 1480; Height = 900;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(0x08, 0x0B, 0x14));

            BuildChrome();
            SeedScreens(6);
            BuildTiles();
            ApplyLayout();
            RenderTabs();
            UpdateHint();

            _anim.Tick += (_, __) => StepAnimation();
            _anim.Start();
            SizeChanged += (_, __) => { ApplyLayout(); SyncShield(); };
        }

        // ── 外壳 ─────────────────────────────────────────────────────────

        private void BuildChrome()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 顶栏：标签 + 布局切换
            var top = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(10, 8, 10, 8)
            };
            var topGrid = new Grid();
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _tabBar
            };
            _tabBar.Orientation = Orientation.Horizontal;
            Grid.SetColumn(scroll, 0);

            var ops = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var btnLayout = MakeButton("\uE8A9", "切换排列（单个 / 双列 / 网格）", () => CycleLayout());
            var btnAdd = MakeButton("\uE710", "再加一个模拟画面", () => { if (_screens.Count < 9) { SeedScreens(1); BuildTiles(); ApplyLayout(); RenderTabs(); UpdateHint(); } });
            var btnRemove = MakeButton("\uE711", "移除最后一个画面", () => RemoveLast());
            Grid.SetColumn(ops, 1);
            ops.Children.Add(btnLayout);
            ops.Children.Add(_layoutLabel);
            ops.Children.Add(btnAdd);
            ops.Children.Add(btnRemove);

            topGrid.Children.Add(scroll);
            topGrid.Children.Add(ops);
            top.Child = topGrid;
            Grid.SetRow(top, 0);

            // 画面区
            var frame = new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(Color.FromRgb(0x04, 0x06, 0x0D)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(12),
                ClipToBounds = true
            };
            var frameGrid = new Grid();
            _scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scroller.Content = _tileHost;
            _tileHost.ClipToBounds = true;
            frameGrid.Children.Add(_scroller);

            // 保护层：半透明遮罩 + 首击消费
            _shield.Background = new SolidColorBrush(Color.FromArgb(1, 0xFF, 0xFF, 0xFF));
            _shield.Cursor = Cursors.Arrow;
            var shieldText = new TextBlock
            {
                Text = "点击此处接管键鼠", FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            _shield.Children.Add(shieldText);
            _shield.MouseLeftButtonDown += (_, e) => e.Handled = true;   // 吞掉首次 Down
            _shield.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;                                        // 吞掉首次 Up
                if (_mode != ConsoleLayoutMode.Single)
                {
                    var hit = HitTestTileUnderMouse();
                    if (hit != null) SetFocus(hit, "shield-click");
                }
                _shield.Visibility = Visibility.Collapsed;
                UpdateHint();
            };
            frameGrid.Children.Add(_shield);

            frame.Child = frameGrid;
            Grid.SetRow(frame, 1);

            // 底栏提示
            var bottom = new Border
            {
                Padding = new Thickness(14, 8, 14, 10),
                Child = _hint
            };
            _hint.Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xAA, 0xBB, 0xCC));
            _hint.FontSize = 11.5;
            _hint.TextWrapping = TextWrapping.Wrap;
            Grid.SetRow(bottom, 2);

            root.Children.Add(top);
            root.Children.Add(frame);
            root.Children.Add(bottom);
            Content = root;
        }

        private static Button MakeButton(string glyph, string tip, Action onClick)
        {
            var b = new Button
            {
                Content = new TextBlock
                {
                    Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12
                },
                Width = 34, Height = 28, Margin = new Thickness(4, 0, 0, 0),
                ToolTip = tip,
                Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Foreground = new SolidColorBrush(Color.FromArgb(0xEE, 0xEE, 0xF2, 0xFF)),
                Cursor = Cursors.Hand
            };
            b.Click += (_, __) => onClick();
            return b;
        }

        // ── 数据 ─────────────────────────────────────────────────────────

        private void SeedScreens(int count)
        {
            string[] users = { "A1", "A2", "A3", "A4", "A5", "A6", "A7", "A8", "A9" };
            string[] titles = { "原神 · 提瓦特", "BGI · 一条龙", "每日委托", "深境螺旋", "树脂刷取", "联机跑图", "钓鱼", "秘境", "矿点巡游" };
            for (int i = 0; i < count && _screens.Count < 9; i++)
            {
                int idx = _screens.Count;
                _screens.Add(new FakeScreen
                {
                    User = users[idx % users.Length],
                    Title = titles[idx % titles.Length],
                    Accent = Palette[idx % Palette.Length],
                    Scene = idx % 3 == 0 ? "combat" : idx % 3 == 1 ? "run" : "idle",
                    RotateAt = DateTime.UtcNow.AddSeconds(3 + idx)
                });
            }
        }

        private void RemoveLast()
        {
            if (_screens.Count <= 1) return;
            RemoveLastNoLayout();
            ApplyLayout(); RenderTabs(); UpdateHint();
        }

        private void BuildTiles()
        {
            foreach (var screen in _screens)
            {
                if (_tiles.Any(t => ReferenceEquals(t.Screen, screen))) continue;
                _tiles.Add(MakeTile(screen));
            }
            if (_focus == null) _focus = _tiles.FirstOrDefault();
        }

        private DemoTile MakeTile(FakeScreen screen)
        {
            var chrome = new Border
            {
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)),
                Background = new SolidColorBrush(Color.FromRgb(0x05, 0x08, 0x10)),
                ClipToBounds = true
            };

            var headerText = new TextBlock
            {
                Text = screen.User + "  ·  " + screen.Title,
                FontSize = 11,
                Margin = new Thickness(10, 5, 10, 5),
                Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xE6, 0xEC, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center
            };
            var dot = new Ellipse
            {
                Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0),
                Fill = new SolidColorBrush(Color.FromRgb(0x41, 0xF0, 0xC0)),
                VerticalAlignment = VerticalAlignment.Center
            };
            var headerInner = new StackPanel { Orientation = Orientation.Horizontal };
            headerInner.Children.Add(dot);
            headerInner.Children.Add(headerText);

            var header = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                Cursor = Cursors.Hand,
                Child = headerInner,
                Visibility = Visibility.Collapsed
            };

            var canvas = new Canvas { Background = new SolidColorBrush(Color.FromRgb(0x07, 0x0A, 0x14)) };

            var stack = new Grid();
            stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(header, 0);
            Grid.SetRow(canvas, 1);
            stack.Children.Add(header);
            stack.Children.Add(canvas);
            chrome.Child = stack;

            var tile = new DemoTile
            {
                Screen = screen, Chrome = chrome, Header = header,
                HeaderText = headerText, ScreenCanvas = canvas, Root = stack
            };

            chrome.MouseLeftButtonUp += (_, e) => { SetFocus(tile, "tile"); e.Handled = true; };
            chrome.MouseRightButtonUp += (_, e) =>
            {
                SetFocus(tile, "tile-rightclick");
                _mode = ConsoleLayoutMode.Single;
                ApplyLayout(); RenderTabs(); UpdateHint();
                e.Handled = true;
            };
            canvas.SizeChanged += (_, __) => DrawScreen(tile);

            _tileHost.Children.Add(chrome);
            return tile;
        }

        // ── 交互 ─────────────────────────────────────────────────────────

        private void CycleLayout()
        {
            _mode = _mode switch
            {
                ConsoleLayoutMode.Single => ConsoleLayoutMode.TwoColumn,
                ConsoleLayoutMode.TwoColumn => ConsoleLayoutMode.Grid,
                _ => ConsoleLayoutMode.Single
            };
            if (_mode == ConsoleLayoutMode.Single && _focus == null) _focus = _tiles.FirstOrDefault();
            ApplyLayout(); RenderTabs(); UpdateHint();
        }

        private void SetFocus(DemoTile tile, string reason)
        {
            if (ReferenceEquals(_focus, tile)) return;
            _focus = tile;
            if (ShowShield) _shield.Visibility = Visibility.Visible;
            ApplyLayout(); RenderTabs(); UpdateHint();
        }

        private void ApplyLayout()
        {
            if (_tiles.Count == 0) return;

            double vw = _scroller.ActualWidth;
            double vh = _scroller.ActualHeight;
            if (_mode != ConsoleLayoutMode.Single)
                vw -= SystemParameters.VerticalScrollBarWidth;
            if (vw < 4 || vh < 4) { Dispatcher.BeginInvoke(ApplyLayout, DispatcherPriority.Loaded); return; }

            int w = (int)Math.Round(vw);
            int h = (int)Math.Round(vh);

            if (_mode == ConsoleLayoutMode.Single)
            {
                foreach (var t in _tiles)
                {
                    bool on = ReferenceEquals(t, _focus);
                    t.Chrome.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                    t.Header.Visibility = Visibility.Collapsed;
                    if (on)
                    {
                        Canvas.SetLeft(t.Chrome, 0); Canvas.SetTop(t.Chrome, 0);
                        t.Chrome.Width = w; t.Chrome.Height = h;
                    }
                }
                _tileHost.Width = w; _tileHost.Height = h;
                _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                UpdateChrome();
                SyncShield();
                return;
            }

            var ordered = Ordered();
            var result = ViewLayout.Compute(w, h, _mode, ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                var t = ordered[i];
                var r = result.Tiles[i];
                t.Chrome.Visibility = Visibility.Visible;
                t.Header.Visibility = Visibility.Visible;
                Canvas.SetLeft(t.Chrome, r.X);
                Canvas.SetTop(t.Chrome, r.Y);
                t.Chrome.Width = r.Width;
                t.Chrome.Height = r.Height;
            }
            _tileHost.Width = w;
            _tileHost.Height = Math.Max(h, result.ContentHeight);
            _scroller.VerticalScrollBarVisibility =
                result.NeedsScroll ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;

            UpdateChrome();
            SyncShield();
        }

        private List<DemoTile> Ordered()
        {
            var list = new List<DemoTile>(_tiles);
            return list;   // 顺序固定 = 添加顺序，焦点不改变位置
        }

        private void UpdateChrome()
        {
            bool multi = _mode != ConsoleLayoutMode.Single;
            foreach (var t in _tiles)
            {
                bool focus = ReferenceEquals(t, _focus);
                t.Chrome.BorderBrush = new SolidColorBrush(focus
                    ? Color.FromArgb(0xCC, t.Screen.Accent.R, t.Screen.Accent.G, t.Screen.Accent.B)
                    : Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
                t.Chrome.BorderThickness = new Thickness(focus && multi ? 2 : 1);
            }
            _layoutLabel.Text = _mode switch
            {
                ConsoleLayoutMode.TwoColumn => "双列（可滚动）",
                ConsoleLayoutMode.Grid => "网格",
                _ => "单个"
            };
            _layoutLabel.Foreground = new SolidColorBrush(Color.FromArgb(0xDD, 0x9A, 0xB4, 0xFF));
            _layoutLabel.FontSize = 11.5;
            _layoutLabel.VerticalAlignment = VerticalAlignment.Center;
            _layoutLabel.Margin = new Thickness(10, 0, 4, 0);
        }

        private void RenderTabs()
        {
            _tabBar.Children.Clear();
            foreach (var t in Ordered())
            {
                bool on = ReferenceEquals(t, _focus);
                var dot = new Ellipse
                {
                    Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0),
                    Fill = new SolidColorBrush(t.Screen.Accent),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var label = new TextBlock
                {
                    Text = t.Screen.User + " · " + t.Screen.Title,
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(on
                        ? Color.FromRgb(0xFF, 0xFF, 0xFF)
                        : Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF))
                };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(dot);
                sp.Children.Add(label);

                var chip = new Border
                {
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(11, 6, 11, 6),
                    Margin = new Thickness(0, 0, 6, 0),
                    Cursor = Cursors.Hand,
                    Child = sp,
                    Background = new SolidColorBrush(on
                        ? Color.FromArgb(0x2E, 0x7C, 0x5C, 0xFF)
                        : Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
                    BorderBrush = new SolidColorBrush(on
                        ? Color.FromArgb(0x77, 0x7C, 0x5C, 0xFF)
                        : Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1)
                };
                var captured = t;
                chip.MouseLeftButtonUp += (_, __) =>
                {
                    if (_mode == ConsoleLayoutMode.Single) SetFocus(captured, "tab");
                    else
                    {
                        SetFocus(captured, "tab");
                        ScrollIntoView(captured);
                    }
                };
                _tabBar.Children.Add(chip);
            }
        }

        private void ScrollIntoView(DemoTile tile)
        {
            double top = Canvas.GetTop(tile.Chrome);
            if (double.IsNaN(top)) return;
            double bottom = top + tile.Chrome.ActualHeight;
            double viewTop = _scroller.VerticalOffset;
            double viewBottom = viewTop + _scroller.ViewportHeight;
            if (top < viewTop) _scroller.ScrollToVerticalOffset(top);
            else if (bottom > viewBottom) _scroller.ScrollToVerticalOffset(bottom - _scroller.ViewportHeight);
        }

        /// <summary>
        /// 保护层：**整块画布一个**，不按格子拆。
        /// 与正式实现一致：多画面下点一次 = 解锁 + 选中点到的那一格（不透传）。
        /// </summary>
        private void SyncShield()
        {
            if (_shield == null) return;
            _shield.Visibility = ShowShield ? Visibility.Visible : Visibility.Collapsed;
            _shield.HorizontalAlignment = HorizontalAlignment.Stretch;
            _shield.VerticalAlignment = VerticalAlignment.Stretch;
            _shield.Width = double.NaN;
            _shield.Height = double.NaN;
            _shield.Margin = new Thickness(0);
        }

        /// <summary>鼠标当前落在哪个格子（与正式实现的 HitTestTileUnderMouse 同逻辑）。</summary>
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT p);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        private DemoTile? HitTestTileUnderMouse()
        {
            try
            {
                if (!GetCursorPos(out POINT mp)) return null;
                foreach (var t in _tiles)
                {
                    if (t.Chrome.Visibility != Visibility.Visible) continue;
                    var tl = t.Chrome.PointToScreen(new Point(0, 0));
                    var br = t.Chrome.PointToScreen(new Point(t.Chrome.ActualWidth, t.Chrome.ActualHeight));
                    if (mp.X >= tl.X && mp.X <= br.X && mp.Y >= tl.Y && mp.Y <= br.Y) return t;
                }
            }
            catch { }
            return null;
        }

        private static FrameworkElement? VisualParentOf(DependencyObject d)
            => VisualTreeHelper.GetParent(d) as FrameworkElement;

        // ── 假画面绘制 ────────────────────────────────────────────────────

        private void StepAnimation()
        {
            var now = DateTime.UtcNow;
            foreach (var s in _screens)
            {
                s.Progress += 0.006;
                if (s.Progress > 1) s.Progress = 0;
                if (now >= s.RotateAt)
                {
                    s.Scene = s.Scene switch { "idle" => "run", "run" => "combat", _ => "idle" };
                    s.RotateAt = now.AddSeconds(6);
                }
            }
            foreach (var t in _tiles)
                if (t.Chrome.Visibility == Visibility.Visible) DrawScreen(t);
        }

        private void DrawScreen(DemoTile tile)
        {
            var c = tile.ScreenCanvas;
            double w = c.ActualWidth, h = c.ActualHeight;
            if (w < 8 || h < 8) return;

            c.Children.Clear();
            var s = tile.Screen;

            // 顶部渐变背景
            var bg = new Rectangle
            {
                Width = w, Height = h,
                Fill = new LinearGradientBrush(
                    Color.FromRgb(0x0B, 0x12, 0x24),
                    Color.FromRgb(0x14, 0x0C, 0x22), 90)
            };
            c.Children.Add(bg);

            // 「透视网格地面」：制造一点游戏画面感
            double horizon = h * 0.62;
            for (int i = 0; i < 7; i++)
            {
                double t = i / 6.0;
                double y = horizon + Math.Pow(t, 1.6) * (h - horizon);
                var line = new Line
                {
                    X1 = 0, X2 = w, Y1 = y, Y2 = y,
                    Stroke = new SolidColorBrush(Color.FromArgb((byte)(26 + 60 * t), s.Accent.R, s.Accent.G, s.Accent.B)),
                    StrokeThickness = 1
                };
                c.Children.Add(line);
            }
            for (int i = -4; i <= 4; i++)
            {
                var line = new Line
                {
                    X1 = w / 2 + i * w * 0.13, Y1 = horizon,
                    X2 = w / 2 + i * w * 0.42, Y2 = h,
                    Stroke = new SolidColorBrush(Color.FromArgb(0x22, s.Accent.R, s.Accent.G, s.Accent.B)),
                    StrokeThickness = 1
                };
                c.Children.Add(line);
            }

            // 玩家角色（随进度左右移动）
            double px = w * (0.22 + 0.4 * (0.5 + 0.5 * Math.Sin(s.Progress * Math.PI * 2)));
            double py = horizon - h * 0.16;
            var halo = new Ellipse
            {
                Width = 72, Height = 72,
                Fill = new RadialGradientBrush(
                    Color.FromArgb(0x66, s.Accent.R, s.Accent.G, s.Accent.B),
                    Color.FromArgb(0x00, s.Accent.R, s.Accent.G, s.Accent.B))
            };
            Canvas.SetLeft(halo, px - 36); Canvas.SetTop(halo, py - 36);
            c.Children.Add(halo);

            var body = new Ellipse
            {
                Width = 26, Height = 34,
                Fill = new SolidColorBrush(s.Accent),
                Stroke = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1.5
            };
            Canvas.SetLeft(body, px - 13); Canvas.SetTop(body, py - 17);
            c.Children.Add(body);

            // 场景标签
            string scene = s.Scene switch { "combat" => "战斗中 · 普通攻击", "run" => "移动中 · 冲刺", _ => "待机 · 拾取范围" };
            var tag = new TextBlock
            {
                Text = scene, FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF)),
                Background = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00)),
                Padding = new Thickness(7, 3, 7, 3)
            };
            Canvas.SetLeft(tag, 12); Canvas.SetTop(tag, 12);
            c.Children.Add(tag);

            // 假 HUD 血条
            double barW = Math.Min(200, w * 0.42);
            var barBg = new Rectangle
            {
                Width = barW, Height = 7,
                Fill = new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x00, 0x00)),
                RadiusX = 3, RadiusY = 3
            };
            Canvas.SetLeft(barBg, 12); Canvas.SetTop(barBg, h - 26);
            c.Children.Add(barBg);

            var bar = new Rectangle
            {
                Width = barW * Math.Max(0.12, 0.85 - 0.4 * Math.Sin(s.Progress * Math.PI)),
                Height = 7,
                Fill = new SolidColorBrush(Color.FromRgb(0x4F, 0xE0, 0x8A)),
                RadiusX = 3, RadiusY = 3
            };
            Canvas.SetLeft(bar, 12); Canvas.SetTop(bar, h - 26);
            c.Children.Add(bar);

            // 右下角信息
            var info = new TextBlock
            {
                Text = "虚拟会话 · " + (int)(w) + "×" + (int)(h) + "  |  布局：" + _mode,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(0x88, 0xCC, 0xD6, 0xEE))
            };
            Canvas.SetLeft(info, 12); Canvas.SetTop(info, h - 46);
            c.Children.Add(info);
        }

        private void UpdateHint()
        {
            _hint.Text = "当前排列：「" + (_mode switch
            {
                ConsoleLayoutMode.TwoColumn => "双列（可上下滚动）",
                ConsoleLayoutMode.Grid => "网格（等分，不滚动）",
                _ => "单个"
            }) + "」    "
            + "· 点格子 / 点标签 = 切换焦点（布局不重排）    "
            + "· 右键格子 = 单独看这个画面    "
            + "· 焦点格子外圈高亮，点它一次会先被保护层吃掉（首击不透传）    "
            + "· 这是纯 WPF 模拟，格子的位置由正式工程 ViewLayout.Compute 算出";
        }
    }
}
