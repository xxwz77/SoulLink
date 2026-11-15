using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using SoulLink.Models;
using SoulLink.Services;

namespace SoulLink.Views
{
    internal static class VisualTreeExtensions
    {
        public static System.Collections.Generic.IEnumerable<T> FindChildren<T>(this DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is T found) yield return found;
                foreach (var nested in child.FindChildren<T>()) yield return nested;
            }
        }
    }

    public partial class MainWindow : Window
    {
        private readonly Dictionary<string, UIElement> _pages = new Dictionary<string, UIElement>();
        private bool _closingToTray;
        private bool _initialized;
        private bool _suppressNav;
        private AutomationScheduler? _automationScheduler;

        public MainWindow()
        {
            InitializeComponent();

            // ── ★ 必须是非分层窗口 ──
            // 实测：AllowsTransparency=True 的分层窗口里，RDP ActiveX 控件能连上、
            // 会话也是 Active，但**画面永远全黑**（WPF 空域限制在分层窗口下的表现）。
            // 所以这里默认不开透明，改用不透明的深色底 + 圆角裁剪。
            ApplyOpaqueMode();

            if (Environment.GetEnvironmentVariable("SOULLINK_NOANIM") == "1")
                Backdrop.Visibility = Visibility.Collapsed;

            _pages["automation"] = PageAutomation;
            _pages["launch"] = PageLaunch;
            _pages["console"] = PageConsole;
            _pages["sessions"] = PageSessions;
            _pages["procs"] = PageProcs;
            _pages["cli"] = PageCli;
            _pages["settings"] = PageSettings;

            var state = ConfigStore.LoadState();
            if (state.Width > 900 && state.Height > 600)
            {
                Width = state.Width;
                Height = state.Height;
            }

            SourceInitialized += (_, __) =>
            {
                ApplyDwmCorners();
                HookWindowChrome();
                // 保留系统主题色边框；顶部额外横条由 WM_NCCALCSIZE 的非客户区高度处理。
            };

            Loaded += MainWindow_Loaded;
            ShowVersion();
            // ★ 非默认身份（如挂机验证版 SoulLink-Stable.exe）在标题上加标识，
            //   避免两个窗口标题完全相同、分不清哪个是哪个。
            try
            {
                if (!string.Equals(AppIdentity.Name, "SoulLink", StringComparison.OrdinalIgnoreCase))
                    Title = "SoulLink · 挂机验证版";
            }
            catch { }
            Loaded += (_, __) => ThemeManager.Apply();
            Closing += MainWindow_Closing;
            StateChanged += (_, __) => UpdateMaxGlyph();

            SelfSessionText.Text = "Session " + SessionService.CurrentSessionId;
            SelfUserText.Text = SessionService.CurrentUser;

            LaunchView.StatusChanged += OnEngineStatus;
            SettingsView.StatusChanged += OnEngineStatus;
            LaunchView.MainWindowRef = this;


            SessionsView.OpenProcessesRequested += s => OpenProcessesOf(s);
            SessionsView.SaveAsActionRequested += s => SaveSessionAsAction(s);
            SessionsView.ConnectConsoleRequested += s => ConnectToConsole(s);
            ConsoleView.MainWindowRef = this;
            ConsoleView.AddRequested += () => NavigateTo("sessions");

            UpdateMaxGlyph();
            UpdateThemeGlyph();
            foreach (var button in RailIcons.FindChildren<Button>())
            {
                button.MouseEnter += RailIcon_HoverChanged;
                button.MouseLeave += RailIcon_HoverChanged;
            }
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, __) =>
            {
                if (App.Config.Settings.ThemeMode == 0)
                    Dispatcher.Invoke(() => { ThemeManager.Apply(); Background = (System.Windows.Media.Brush)Application.Current.Resources["WindowBackground"]; });

                // 强调色会随用户换主题/换壁纸变化，重新取一次，不用等下次激活。
                try { Dispatcher.BeginInvoke(new Action(UpdateBorderColor)); } catch { }
            };

            // 恢复上次的侧栏偏好
            try
            {
                var st0 = ConfigStore.LoadState();
                _autoHide = st0.NavAutoHide;
                AutoHideNavCheck.IsChecked = _autoHide;

                if (_autoHide) SetNavCollapsed(true, "启动恢复（自动收起）");
                else SetNavCollapsed(st0.NavCollapsed, "启动恢复");
                // 确保首帧文字状态与宽度一致（否则收起态文字仍可见，会压到右侧内容）
                ApplyNavTextVisibility();
            }
            catch { }

            _automationScheduler = new AutomationScheduler();
            _initialized = true;

            if (!string.IsNullOrEmpty(App.StartupPage)) SelectPage(App.StartupPage);
        }

        /// <summary>
        /// 用 DWM 给窗口做系统级圆角（Windows 11）。
        /// 非分层窗口下这是唯一能得到真圆角的方式。
        /// </summary>
        private void ApplyDwmCorners()
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                bool ok = SoulLink.Services.WindowCorners.ApplyRounded(hwnd);
                UpdateBorderColor();
                App.Breadcrumb("DWM 圆角: " + (ok ? "已应用" : "不支持（Win10 或更早）"));
            }
            catch (Exception ex)
            {
                App.AppendErrorLog("MainWindow.ApplyDwmCorners", ex);
            }
        }

        /// <summary>
        /// 激活时使用系统主题强调色，未激活时交回系统原生边框，符合 Windows 原生行为。
        ///
        /// ★ 关键修复（边框没有用上系统强调色）：
        ///   DWMWA_BORDER_COLOR 要的是 COLORREF（0x00BBGGRR，alpha 必须是 0x00）。
        ///   旧代码直接把 0xFFRRGGBB 的 ARGB 传进去，DWM 一律返回 E_INVALIDARG，
        ///   边框从来没被改过色 —— 于是永远显示系统默认边框。
        ///   颜色换算与校验都在 WindowCorners.SetBorderColor 里完成。
        ///
        /// ★ 未激活时传 DWMWA_COLOR_DEFAULT 而不是写死的深灰：
        ///   这样失焦后的边框和资源管理器等原生窗口完全一致（本机实测 #ABABAB）。
        /// </summary>
        private void UpdateBorderColor()
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                if (!IsActive)
                {
                    SoulLink.Services.WindowCorners.ResetBorderColor(hwnd);
                    return;
                }

                uint accent = SoulLink.Services.WindowCorners.GetSystemAccentColor();
                SoulLink.Services.WindowCorners.SetBorderColor(hwnd, accent);
                App.Breadcrumb($"边框色 accent=#{accent & 0xFFFFFF:X6}");
            }
            catch (Exception ex)
            {
                App.AppendErrorLog("MainWindow.UpdateBorderColor", ex);
            }
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            UpdateBorderColor();
        }

        protected override void OnDeactivated(EventArgs e)
        {
            base.OnDeactivated(e);
            UpdateBorderColor();
        }

        /// <summary>
        /// 非分层窗口没有 WindowChrome，靠这段 P/Invoke 让窗口可拖动 / 可缩放。
        /// 挂在 HwndSource 上处理 WM_NCHITTEST。
        /// </summary>
        private void HookWindowChrome()
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                var src = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
                src?.AddHook(WndProc);
            }
            catch (Exception ex)
            {
                App.AppendErrorLog("MainWindow.HookWindowChrome", ex);
            }
        }

        private const int WM_NCHITTEST = 0x0084;
        private const int WM_NCCALCSIZE = 0x0083;
        private const int WM_NCPAINT = 0x0085;
        private const int HTCLIENT = 1;
        private const int HTCAPTION = 2;
        private const int HTLEFT = 10;
        private const int HTRIGHT = 11;
        private const int HTTOP = 12;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15;
        private const int HTBOTTOMLEFT = 16;
        private const int HTBOTTOMRIGHT = 17;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // 顶部非客户区标题横条不属于应用内容，去掉多余高度，保留系统细边框。
            if (msg == WM_NCCALCSIZE && wParam != IntPtr.Zero)
            {
                handled = true;
                return IntPtr.Zero;
            }

            if (msg != WM_NCHITTEST) return IntPtr.Zero;

            try
            {
                // 屏幕坐标 → 客户区坐标
                int lp = lParam.ToInt32();
                var pt = PointFromScreen(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));

                const int grip = 6;
                double w = ActualWidth, h = ActualHeight;

                bool left = pt.X <= grip, right = pt.X >= w - grip;
                bool top = pt.Y <= grip, bottom = pt.Y >= h - grip;

                if (top && left) { handled = true; return (IntPtr)HTTOPLEFT; }
                if (top && right) { handled = true; return (IntPtr)HTTOPRIGHT; }
                if (bottom && left) { handled = true; return (IntPtr)HTBOTTOMLEFT; }
                if (bottom && right) { handled = true; return (IntPtr)HTBOTTOMRIGHT; }
                if (left) { handled = true; return (IntPtr)HTLEFT; }
                if (right) { handled = true; return (IntPtr)HTRIGHT; }
                if (top) { handled = true; return (IntPtr)HTTOP; }
                if (bottom) { handled = true; return (IntPtr)HTBOTTOM; }

                // 标题栏区域（避开右上角按钮）
                if (pt.Y <= 64 && pt.X > 120 && pt.X < w - 260)
                {
                    handled = true;
                    return (IntPtr)HTCAPTION;
                }
            }
            catch { }

            return IntPtr.Zero;
        }

        /// <summary>
        /// 关闭分层窗口。RDP 画面必须跑在非分层窗口里，否则全黑。
        /// 视觉上用「深色底 + 圆角 + 自绘边框」替代原来的透明发光层。
        /// </summary>
        private void ApplyOpaqueMode()
        {
            try
            {
                AllowsTransparency = false;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.CanResize;

                var deep = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x05, 0x07, 0x0F));
                Background = (System.Windows.Media.Brush)Application.Current.Resources["WindowBackground"];

                // ★ 非分层窗口的圆角只能靠 DWM。
                //   WindowChrome 的 CornerRadius=0 会把窗口角做成直角，覆盖 DWM 的圆角，
                //   所以必须摘掉 WindowChrome（我们本来就是自绘标题栏，不依赖它）。
                WindowChrome.SetWindowChrome(this, null);
                WindowChrome.SetIsHitTestVisibleInChrome(this, false);

                // 窗口底色 + DWM 圆角。
                // ★ 内容区必须留出边距，否则标题栏会把方形背景顶到圆角外面
                //   （用户反馈：底部圆角正常、顶部是方的）。
                RootLayer.Effect = null;
                RootLayer.Margin = new Thickness(0);

                // 关键：不给 Shell 铺满整个窗口，让 DWM 的圆角能被看到
                Shell.CornerRadius = new CornerRadius(0);
                Shell.BorderThickness = new Thickness(0);
                Shell.SetResourceReference(BackgroundProperty, "WindowBackground");
                Background = (System.Windows.Media.Brush)Application.Current.Resources["WindowBackground"];
            }
            catch (Exception ex)
            {
                App.AppendErrorLog("MainWindow.ApplyOpaqueMode", ex);
            }
        }

        /// <summary>按名字切页（启动参数 --page 用）。</summary>
        public void SelectPage(string tag)
        {
            NavigateTo(tag);
        }


        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            App.Breadcrumb("MainWindow.Loaded");
            TryBeginStoryboard(FindResource("FadeIn") as Storyboard);

            Backdrop.Visibility = Visibility.Visible;

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                bool ok = await System.Threading.Tasks.Task.Run(() =>
                {
                    SystemLauncher.EnablePrivileges();
                    return SystemLauncher.IsElevated;
                }).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() =>
                {
                    OnEngineStatus(ok, ok ? "SYSTEM 投送通道" : "需要管理员权限");
                    LaunchView.RefreshSessions();
                });
            });

            try
            {
                LaunchView.Activate();
                App.Breadcrumb("LaunchView activated");
            }
            catch (Exception ex)
            {
                App.AppendErrorLog("LaunchView.Activate", ex);
                App.Breadcrumb("LaunchView.Activate FAILED: " + ex.Message);
            }
        }

        /// <summary>标题栏副标题显示版本号（从程序集读取，升版本后自动跟随）。</summary>
        private void ShowVersion()
        {
            try
            {
                if (VersionText == null) return;
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var ver = asm.GetName().Version;
                // ★ 单文件发布时 asm.Location 为空，不能直接用 FileVersionInfo。
                //   优先读 InformationalVersion（csproj 的 <Version>），退回程序集版本。
                string? info = null;
                try
                {
                    var attrs = asm.GetCustomAttributes(
                        typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
                    if (attrs != null && attrs.Length > 0)
                        info = ((System.Reflection.AssemblyInformationalVersionAttribute)attrs[0])
                                   .InformationalVersion;
                }
                catch { }
                string text;
                if (!string.IsNullOrWhiteSpace(info))
                {
                    int plus = info!.IndexOf('+');      // 去掉 SourceRevisionId 后缀
                    if (plus > 0) info = info.Substring(0, plus);
                    text = "v" + TrimRevision(info);
                }
                else text = "v" + TrimRevision(ver?.ToString() ?? "1.0.0");
                VersionText.Text = text;
                VersionText.ToolTip = "SoulLink " + text + " · 多用户挂机中控台";
            }
            catch { if (VersionText != null) VersionText.Text = "v1.0.0"; }
        }

        private static string TrimRevision(string version)
        {
            var parts = (version ?? "").Split('.');
            if (parts.Length >= 3) return parts[0] + "." + parts[1] + "." + parts[2];
            return version;
        }

        private void OnEngineStatus(bool ok, string detail)
        {
            EngineDot.Color = ok ? System.Windows.Media.Color.FromRgb(0x41, 0xF0, 0xC0)
                                 : System.Windows.Media.Color.FromRgb(0xFF, 0xC4, 0x6B);
            EngineText.Text = ok ? "引擎就绪" : "引擎待配置";
            EngineText.ToolTip = detail;
        }

        private void TryBeginStoryboard(Storyboard? sb)
        {
            try { sb?.Begin(this); } catch { }
        }

        // ── 侧栏折叠 ─────────────────────────────────────────────────────────

        /// <summary>侧栏是否启用「鼠标悬停展开」。</summary>
        private bool _autoHide = true;

        /// <summary>侧栏浮层当前是否展开。</summary>
        private bool _navCollapsed = true;

        /// <summary>悬停 2 秒后才展开的定时器。</summary>
        private System.Windows.Threading.DispatcherTimer? _hoverTimer;

        /// <summary>离开后延迟收起的定时器。</summary>
        private System.Windows.Threading.DispatcherTimer? _hideTimer;

        // ── 侧栏 ─────────────────────────────────────────────────────────────

        private void AutoHideNav_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialized) return;
            _autoHide = AutoHideNavCheck.IsChecked == true;
            if (_autoHide) SetNavCollapsed(true, "开启自动收起");
            SaveNavPref();
        }

        /// <summary>导航行点击（Grid 行，Tag 为页面标识）。</summary>
        private void NavRow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string tag)
            {
                NavigateTo(tag);
                SetNavCollapsed(true, "点图标切页");
            }
        }

        private void BtnToggleNav_Click(object sender, RoutedEventArgs e)
        {
            SetNavCollapsed(!_navCollapsed, "手动切换");
            SaveNavPref();
        }

        private void RailIcon_HoverChanged(object sender, MouseEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string tag) return;
            bool hovered = button.IsMouseOver;
            if (button.Content is TextBlock glyph)
                glyph.Foreground = hovered ? (Brush)FindResource("CyanBrush") : (Brush)FindResource("TextMuted");
            // 展开态文字已在 NavRail 内，处理同一批元素
        }

        private void RailIcon_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string tag)
            {
                NavigateTo(tag);
                SetNavCollapsed(true, "点图标切页");
            }
        }

        private void BtnTheme_Click(object sender, RoutedEventArgs e)
        {
            int mode = App.Config.Settings.ThemeMode;
            App.Config.Settings.ThemeMode = (mode + 1) % 3;
            ConfigStore.Save(App.Config);
            ThemeManager.Apply();
            Background = (System.Windows.Media.Brush)Application.Current.Resources["WindowBackground"];
            UpdateThemeGlyph();
            LogBus.Info("主题模式", App.Config.Settings.ThemeMode == 0 ? "跟随系统" : App.Config.Settings.ThemeMode == 1 ? "浅色" : "深色");
        }

        private void UpdateThemeGlyph()
        {
            if (ThemeGlyphBottom == null || BtnThemeBottom == null) return;
            int mode = App.Config.Settings.ThemeMode;
            ThemeGlyphBottom.Text = mode == 0 ? "\uE793" : mode == 1 ? "\uE706" : "\uE708";
            BtnThemeBottom.ToolTip = "主题：" + (mode == 0 ? "跟随系统" : mode == 1 ? "浅色" : "深色") + "（点击切换）";
        }

        private void GeneralSettings_Click(object sender, RoutedEventArgs e)
        {
            SetNavCollapsed(true, "打开通用设置");
            NavigateTo("settings");
        }

        /// <summary>
        /// 鼠标进入图标栏：**悬停 2 秒才展开**，避免鼠标划过就弹出来。
        /// 图标本身可以直接点击，不需要等。
        /// </summary>
        private void NavRail_MouseEnter(object sender, MouseEventArgs e)
        {
            if (!_autoHide || !_navCollapsed) return;
            CancelHideTimer();
            CancelHoverTimer();

            _hoverTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1200)
            };
            _hoverTimer.Tick += (_, __) =>
            {
                CancelHoverTimer();
                if (!_autoHide) return;
                if (!NavRail.IsMouseOver) return;
                SetNavCollapsed(false, "悬停 2 秒");
            };
            _hoverTimer.Start();
        }

        private void NavRail_MouseLeave(object sender, MouseEventArgs e)
        {
            CancelHoverTimer();
            if (!_autoHide) return;
            StartHideTimer();
        }

        private void NavPanel_MouseEnter(object sender, MouseEventArgs e)
        {
            CancelHideTimer();
        }

        private void NavPanel_MouseLeave(object sender, MouseEventArgs e)
        {
            if (!_autoHide) return;
            StartHideTimer();
        }

        private void StartHideTimer()
        {
            CancelHideTimer();
            _hideTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(320)
            };
            _hideTimer.Tick += (_, __) =>
            {
                CancelHideTimer();
                if (!_autoHide) return;
                if (NavRail.IsMouseOver) return;
                SetNavCollapsed(true, "鼠标离开");
            };
            _hideTimer.Start();
        }

        private void CancelHideTimer()
        {
            var t = _hideTimer; _hideTimer = null;
            if (t != null) { try { t.Stop(); } catch { } }
        }

        private void CancelHoverTimer()
        {
            var t = _hoverTimer; _hoverTimer = null;
            if (t != null) { try { t.Stop(); } catch { } }
        }

        /// <summary>
        /// 展开 / 收起侧栏。
        ///
        /// ★ 用 Popup 而不是内嵌 Border：
        ///   中控台里的 RDP 是 WindowsFormsHost（WPF 空域），
        ///   任何 WPF 元素的 ZIndex 都盖不住它，只有独立 HWND 才行。
        /// </summary>
        private void SetNavCollapsed(bool collapsed, string reason)
        {
            CancelHideTimer();
            CancelHoverTimer();

            bool changed = _navCollapsed != collapsed;
            _navCollapsed = collapsed;

            try
            {
                // ★ 不再开第二个窗口：图标栏自身变宽，展开时显示文字。

                const double collapsedWidth = 56;
                const double expandedWidth = 180;
                double target = collapsed ? collapsedWidth : expandedWidth;

                // 侧栏变宽会挤压右侧内容，用动画让过程平滑
                var anim = new System.Windows.Media.Animation.DoubleAnimation
                {
                    To = target,
                    Duration = TimeSpan.FromMilliseconds(collapsed ? 140 : 170),
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                    { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                };
                NavRail.BeginAnimation(FrameworkElement.WidthProperty, anim);

                // ★ 列宽必须一起变：NavRail 在 Column 0，列宽固定 76 时，
                //   加宽后的侧栏会被 Column 1 的内容压住（用户实测"被别的界面挡住"）。
                // ★ 列宽固定 76 不变：侧栏已改为 ColumnSpan=2 覆盖在内容之上，
                //   不再挤压右侧内容（用户明确要求"覆盖，不挤压"）。

                // ★ 文字显隐必须与宽度同步，且要在动画**开始前**就定好。
                //   收起时立刻隐藏（否则文字会溢出栏外压到右侧内容，实测）；
                //   展开时立刻显示（栏宽跟着动画涨，文字有空间）。
                _navTextVisible = !collapsed;
                ApplyNavTextVisibility();
            }
            catch (Exception ex)
            {
                App.AppendErrorLog("MainWindow.SetNavCollapsed", ex);
            }

            UpdateNavSurfaceShape(collapsed);

            if (HamburgerGlyph != null)
                HamburgerGlyph.Text = collapsed ? "\uE700" : "\uE76B";

            if (BtnHamburger != null)
                BtnHamburger.Background = collapsed
                    ? System.Windows.Media.Brushes.Transparent
                    : new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromArgb(0x33, 0x7C, 0x5C, 0xFF));

            if (changed) App.Breadcrumb("侧栏 " + (collapsed ? "收起" : "展开") + " · " + reason);
        }

        /// <summary>供 ThemeManager 读取当前侧栏状态（主题刷新时要用）。</summary>
        internal bool IsNavCollapsedForTheme => _navCollapsed;

        /// <summary>侧栏文字是否应显示（与栏宽同步）。</summary>
        private bool _navTextVisible;

        /// <summary>按当前状态刷新侧栏文字显隐。</summary>
        private void ApplyNavTextVisibility()
        {
            var vis = _navTextVisible ? Visibility.Visible : Visibility.Collapsed;
            foreach (var t in new FrameworkElement?[]
            {
                NavTitleText, NavTextLaunch, NavTextConsole,
                NavTextSessions, NavTextProcs, NavTextCli
            })
            {
                if (t != null) t.Visibility = vis;
            }
        }

        /// <summary>
        /// 侧栏两态的圆角：
        ///   收起 = 独立卡片，四角全圆；
        ///   展开 = 图标栏 + Popup 面板拼成一张卡，图标栏右侧两角必须变直角，
        ///          否则面板的直角边会紧贴着图标栏的圆角，中间露出豁口。
        /// </summary>
        internal void UpdateNavSurfaceShape(bool collapsed)
        {
            try
            {
                // ★ 单窗口方案：NavRail 就是一个整体卡片，展开/收起只是宽度变化，
                //   圆角始终一致，不再需要"拼接两个窗口"的直角/去边框处理。
                if (NavRail != null)
                    NavRail.CornerRadius = new CornerRadius(14);

                // 旧的 Popup 面板已停用，这里不再做任何定位计算。
            }
            catch (Exception ex)
            {
                App.AppendErrorLog("MainWindow.UpdateNavSurfaceShape", ex);
            }
        }

        private void NavPopup_Opened(object sender, EventArgs e) { }
        private void NavPopup_Closed(object sender, EventArgs e)
        {
            _navCollapsed = true;
            UpdateNavSurfaceShape(true);
        }

        private void SaveNavPref()
        {
            try
            {
                var st = ConfigStore.LoadState();
                st.NavCollapsed = _navCollapsed;
                st.NavAutoHide = _autoHide;
                ConfigStore.SaveState(st);
            }
            catch { }
        }

        // ── 窗口按钮 ──────────────────────────────────────────────────────────

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleMax();
                return;
            }
            try { DragMove(); } catch { }
        }

        private void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void BtnMax_Click(object sender, RoutedEventArgs e) => ToggleMax();

        private void ToggleMax()
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            RootLayer.Margin = WindowState == WindowState.Maximized ? new Thickness(2) : new Thickness(14);
        }

        private void UpdateMaxGlyph()
        {
            if (BtnMax?.Content is TextBlock tb)
                tb.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            if (_closingToTray) return;

            var state = ConfigStore.LoadState();
            if (WindowState == WindowState.Normal)
            {
                state.Width = Width;
                state.Height = Height;
            }
            state.Maximized = WindowState == WindowState.Maximized;
            ConfigStore.SaveState(state);

            // 关闭窗口时一定要放开鼠标、恢复系统设置
            try { SoulLink.Services.PointerAcceleration.ForceRestore(); } catch { }
            try { System.Windows.Forms.Cursor.Show(); } catch { }

            if (App.Config.Settings.MinimizeToTray && Application.Current != null)
            {
                // 关闭 = 收进托盘常驻，后台命令依然可执行
                e.Cancel = true;
                _closingToTray = false;
                Hide();
                LogBus.Info("已最小化到托盘，后台命令仍然可用。");
            }
        }

        public void ForceClose()
        {
            _closingToTray = true;
            _automationScheduler?.Dispose();
            try { ConsoleView.DisposeConsoles(); } catch { }
            Close();
        }

        /// <summary>从“在线会话”跳到“会话进程”。</summary>
        public void OpenProcessesOf(SessionInfo session)
        {
            foreach (var kv in _pages)
                kv.Value.Visibility = kv.Key == "procs" ? Visibility.Visible : Visibility.Collapsed;
            NavigateTo("procs");
            ProcsView.FocusSession(session);
        }

        private void SaveSessionAsAction(SessionInfo session)
        {
            var draft = new ActionItem
            {
                Name = "",
                Kind = ActionKind.Launch
            };
            var dlg = new ActionEditorWindow(null, App.Config) { Owner = this };
            dlg.PresetAccount(session.UserName);
            if (dlg.ShowDialog() == true)
            {
                App.Config.Actions.Add(dlg.Result);
                ConfigStore.Save(App.Config);
                LaunchView.ReloadActions();
                LaunchView.Activate();
                LogBus.Ok("已保存动作「" + dlg.Result.Name + "」");
            }
        }

        // ── 导航 ──────────────────────────────────────────────────────────────

        /// <summary>当前所在页面（用于导航按钮高亮）。</summary>
        private string _activePage = "launch";

        /// <summary>切页：只改 Visibility + 高亮图标，不重建页面。</summary>
        private void SetActivePage(string tag)
        {
            if (!_pages.TryGetValue(tag, out var page)) return;

            foreach (var kv in _pages)
                kv.Value.Visibility = kv.Key == tag ? Visibility.Visible : Visibility.Collapsed;

            _activePage = tag;
            UpdateRailHighlight();

            switch (tag)
            {
                case "automation": AutomationView.Activate(); break;
                case "console": ConsoleView.Activate(); break;
                case "sessions": SessionsView.Activate(); break;
                case "procs": ProcsView.Activate(); break;
                case "cli": CliView.Activate(); break;
                case "settings": SettingsView.Activate(); break;
                default: LaunchView.Activate(); break;
            }
        }

        /// <summary>高亮左侧图标栏里当前所在的页。</summary>
        private void UpdateRailHighlight()
        {
            try
            {
                if (RailIcons == null) return;

                var primary = (System.Windows.Media.Brush)FindResource("TextPrimary");
                var muted = (System.Windows.Media.Brush)FindResource("TextMuted");
                // 图标颜色：未选中用次级色，选中用主色。缺这行时深色主题下图标是黑的（用户实测）。
                // 选中底色：与旧版一致的紫色半透明
                var activeBg = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(0x33, 0x7C, 0x5C, 0xFF));

                // ★ 导航行现在是 Grid（图标+文字），不再是 Button。
                //   原来只处理 Button，导致选中高亮完全失效（用户实测）。
                foreach (var child in RailIcons.Children)
                {
                    if (child is not Grid row || row.Tag is not string t) continue;

                    bool on = t == _activePage;
                    row.Background = on ? activeBg : System.Windows.Media.Brushes.Transparent;

                    // 图标是第 0 列的 TextBlock，文字是第 1 列
                    foreach (var el in row.Children)
                    {
                        if (el is System.Windows.Controls.TextBlock tb)
                            tb.Foreground = on ? primary : muted;
                    }
                }
            }
            catch { }
        }


        /// <summary>供控制台页跳回某个页面。</summary>
        /// <summary>
        /// 切页。
        /// ★ 只改 Visibility，绝不重建页面——中控台里挂着 RDP 控件和注入通道，
        ///   重建会让画面和注入一起失效（第一版切回来就点不进画面，就是这个原因）。
        /// </summary>
        /// <summary>
        /// 切页。
        /// ★ 只改 Visibility，绝不重建页面——中控台里挂着 RDP 控件和注入通道。
        /// </summary>
        public void NavigateTo(string tag)
        {
            SetActivePage(tag);
        }


        /// <summary>从「在线会话」直接把某个会话接进中控台。</summary>
        private async void ConnectToConsole(SessionInfo session)
        {
            try
            {
                NavigateTo("console");
                await ConsoleView.AddSessionAsync(session);
            }
            catch (Exception ex)
            {
                App.AppendErrorLog("MainWindow.ConnectToConsole", ex);
                LogBus.Error("接入中控台失败", ex.Message);
            }
        }
    }
}
