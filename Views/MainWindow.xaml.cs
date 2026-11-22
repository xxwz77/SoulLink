using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Threading.Tasks;
using System.Windows.Threading;
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
        private SoulLink.Services.OwnedOverlayWindow? _navOverlayWindow;
        private bool _navDockedInOverlay;
        private DispatcherTimer? _navAnimationSync;

        /// <summary>浮层上一次同步过的尺寸。用于判定"动画已停"，停掉 16ms 定时器。</summary>
        private double _navLastSyncedWidth = -1;
        private double _navLastSyncedHeight = -1;
        private DateTime _navRailExpandStartedAt;
        private bool _navOverlayHidePending;
        private int _navTransitionVersion;
        private bool _navStartupLayout = true;

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
            _pages["update"] = PageUpdate;
            _pages["routeupdate"] = PageRouteUpdate;
            _pages["datacenter"] = PageDataCenter;
            _pages["remote"] = PageRemote;
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
                HookMaximizeWorkArea();
                // 客户区与缩放交给 WPF WindowChrome；这里只修正最大化工作区。
            };

            Loaded += MainWindow_Loaded;
            ShowVersion();

            SilentUpdateCheck();
            // ★ 非默认身份（如挂机验证版 SoulLink-Stable.exe）在标题上加标识，
            //   避免两个窗口标题完全相同、分不清哪个是哪个。
            try
            {
                if (!string.Equals(AppIdentity.Name, "SoulLink", StringComparison.OrdinalIgnoreCase))
                {
                    Title = "SoulLink · 挂机验证版";
                    // 左上角同时挂可见标识 —— 标题栏文字在最大化时容易被忽略，
                    // 而两个程序的操作按钮几乎一样，靠这个一眼区分。
                    if (IdentityBadge != null) IdentityBadge.Visibility = Visibility.Visible;
                }
            }
            catch { }
            Loaded += (_, __) => ThemeManager.Apply();
            Closing += MainWindow_Closing;
            StateChanged += (_, __) => { UpdateMaxGlyph(); SyncUiVisible(); };
            // ★ 最小化到托盘走的是 Hide()，它**不触发 StateChanged**，
            //   必须单独监听 IsVisibleChanged，否则"隐藏后还在重绘"的 bug 又回来了。
            IsVisibleChanged += (_, __) => SyncUiVisible();

            SelfSessionText.Text = "Session " + SessionService.CurrentSessionId;
            SelfUserText.Text = SessionService.CurrentUser;

            LaunchView.StatusChanged += OnEngineStatus;
            SettingsView.StatusChanged += OnEngineStatus;
            LaunchView.MainWindowRef = this;


            // 联机大厅是独立页面，不再回连会话相关的动作。
            // （原来的在线会话页已被 LobbyPage 取代）
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

                // 构造阶段主窗口还没有前台 HWND，不能展开独立原生浮层。
                // 先以 56px 图标栏完成布局，Loaded 后再恢复用户的展开偏好。
                _navCollapsed = true;
                _navTextVisible = false;
                ApplyNavTextVisibility();
                if (!_autoHide && !st0.NavCollapsed)
                    Loaded += (_, __) => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (IsActive) SetNavCollapsed(false, "启动恢复");
                        else Activated += RestoreExpandedNavOnFirstActivation;
                    }));
                // 确保首帧文字状态与宽度一致（否则收起态文字仍可见，会压到右侧内容）
                ApplyNavTextVisibility();
            }
            catch { }

            _navStartupLayout = false;
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

        // ═══ RDP 控制台全屏：chrome 收纳 ═══════════════════════════════════
        //
        // 全屏的真源状态在 ConsolePage._isFullScreen；这里只负责把窗口自身的
        // chrome（标题栏 / 侧栏 / 内边距）收起来或还原。窗口几何、Topmost、
        // DWM 圆角由 ConsolePage 在同一拍里设置（它要做显示器矩形协商）。

        private Thickness _fsChromeMargin;
        private GridLength _fsTitleHeight;
        private GridLength _fsNavWidth;
        private Visibility _fsNavRailVisibility;
        private bool _fsNavWasCollapsed;
        private bool _chromeFullscreen;

        /// <summary>是否处于全屏 chrome 收纳状态。</summary>
        public bool IsChromeFullscreen => _chromeFullscreen;

        /// <summary>
        /// 收纳 / 还原主窗口 chrome，让中控台画面铺满整个窗口。
        ///
        /// ★ 侧栏若正展开在浮层里，先按「收起」处理：全屏下没有任何入口能再
        ///   展开它，而挂在浮层里的 NavRail 是独立 HWND，不受 WPF Visibility
        ///   管，会一直浮在画面上方。退出全屏后侧栏保持收起态（用户重新展开即可）。
        /// </summary>
        public void SetChromeFullscreen(bool on)
        {
            if (on)
            {
                if (_chromeFullscreen) return;
                _chromeFullscreen = true;

                _fsChromeMargin = ChromeGrid.Margin;
                _fsTitleHeight = TitleRow.Height;
                _fsNavWidth = NavColumn.Width;
                _fsNavRailVisibility = NavRail.Visibility;
                _fsNavWasCollapsed = _navCollapsed;

                if (!_navCollapsed) SetNavCollapsed(true, "进入全屏");

                ChromeGrid.Margin = new Thickness(0);
                TitleRow.Height = new GridLength(0);
                NavColumn.Width = new GridLength(0);
                NavRail.Visibility = Visibility.Collapsed;
            }
            else
            {
                if (!_chromeFullscreen) return;
                _chromeFullscreen = false;

                ChromeGrid.Margin = _fsChromeMargin;
                TitleRow.Height = _fsTitleHeight;
                NavColumn.Width = _fsNavWidth;
                NavRail.Visibility = _fsNavRailVisibility;

                if (!_fsNavWasCollapsed) SetNavCollapsed(false, "退出全屏");
            }
        }

        /// <summary>
        /// 边框固定使用系统主题强调色；只在创建窗口、系统强调色变化时设置。
        ///
        /// 不要在每次激活/失活时切换 DWMWA_BORDER_COLOR（尤其不要切到
        /// DWMWA_COLOR_NONE）：窗口切换焦点会反复重设非客户区，产生可见闪框。
        ///
        /// ★ 关键修复（边框没有用上系统强调色）：
        ///   DWMWA_BORDER_COLOR 要的是 COLORREF（0x00BBGGRR，alpha 必须是 0x00）。
        ///   旧代码直接把 0xFFRRGGBB 的 ARGB 传进去，DWM 一律返回 E_INVALIDARG，
        ///   边框从来没被改过色 —— 于是永远显示系统默认边框。
        ///   颜色换算与校验都在 WindowCorners.SetBorderColor 里完成。
        ///
        /// 焦点变化不再调用这里，避免自定义窗口随激活状态反复重设 DWM 属性。
        /// </summary>
        /// <summary>供外部（如档位切换）主动刷新窗口边框色。</summary>
        public static void RefreshWindowBorder()
        {
            try
            {
                if (Application.Current?.MainWindow is MainWindow mw)
                    mw.Dispatcher.BeginInvoke(new Action(mw.UpdateBorderColor));
            }
            catch { }
        }

        private void UpdateBorderColor()
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                uint accent = SoulLink.Services.WindowCorners.GetSystemAccentColor();
                bool ok = SoulLink.Services.WindowCorners.SetBorderColor(hwnd, accent);
                App.Breadcrumb("边框色 固定系统强调色 accent=#" + (accent & 0xFFFFFF).ToString("X6") + " ok=" + ok);
            }
            catch (Exception ex)
            {
                App.AppendErrorLog("MainWindow.UpdateBorderColor", ex);
            }
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            InputTrace.Write("MAIN_ACT", "主窗口激活 IsVisible=" + IsVisible + " state=" + WindowState);
            // 不再随焦点切换修改 DWM 边框属性。
        }

        /// <summary>★ 临时诊断：记录每一次 WindowState 变化，定位"装完服务后按 X 主窗口消失"。</summary>
        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            InputTrace.Write("MAIN_STATE", "主窗口状态=" + WindowState + " IsVisible=" + IsVisible
                + " Active=" + IsActive + " 调用方=" + InputTrace.Caller(4));
        }

        protected override void OnDeactivated(EventArgs e)
        {
            base.OnDeactivated(e);
            InputTrace.Write("MAIN_DEACT", "主窗口失活 IsVisible=" + IsVisible
                + " state=" + WindowState + " 调用方=" + InputTrace.Caller(3));
            _navOverlayWindow?.HideForOwnerState();
            if (_autoHide) SetNavCollapsed(true, "主窗口失活");
            // 不再随焦点切换修改 DWM 边框属性。
        }



        private void HookMaximizeWorkArea()
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
            source?.AddHook(ResizeBorderHook);
            source?.AddHook(MaximizeWorkAreaHook);
        }

        private const int WM_GETMINMAXINFO = 0x0024;
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_SETCURSOR = 0x0020;
        private const int HTCLIENT = 1;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SetCursor(IntPtr cursor);

        private IntPtr ResizeBorderHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_NCHITTEST && WindowState == WindowState.Normal && ResizeMode == ResizeMode.CanResize)
            {
                int packed = lParam.ToInt32();
                var screen = new Point((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
                var point = PointFromScreen(screen);
                double grip = WindowChrome.GetWindowChrome(this)?.ResizeBorderThickness.Left ?? 6;
                bool left = point.X <= grip, right = point.X >= ActualWidth - grip;
                bool top = point.Y <= grip, bottom = point.Y >= ActualHeight - grip;
                int hit = top && left ? 13 : top && right ? 14
                    : bottom && left ? 16 : bottom && right ? 17
                    : left ? 10 : right ? 11 : top ? 12 : bottom ? 15 : HTCLIENT;
                if (hit != HTCLIENT)
                {
                    handled = true;
                    return new IntPtr(hit);
                }
            }

            if (msg == WM_SETCURSOR && WindowState == WindowState.Normal && ResizeMode == ResizeMode.CanResize)
            {
                int hit = (int)lParam & 0xFFFF;
                int cursor = hit is 10 or 11 ? 32644 : hit is 12 or 15 ? 32645
                    : hit is 13 or 17 ? 32643 : hit is 14 or 16 ? 32642 : 0;
                if (cursor != 0)
                {
                    SetCursor(LoadCursor(IntPtr.Zero, new IntPtr(cursor)));
                    handled = true;
                    return new IntPtr(1);
                }
            }
            return IntPtr.Zero;
        }

        private struct NativePoint { public int X, Y; }
        private struct NativeRect { public int Left, Top, Right, Bottom; }
        private struct MinMaxInfo
        {
            public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
        }
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor, WorkArea;
            public int Flags;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        private IntPtr MaximizeWorkAreaHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
            var monitor = MonitorFromWindow(hwnd, 2);
            var info = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

            var mmi = (MinMaxInfo)System.Runtime.InteropServices.Marshal.PtrToStructure(
                lParam, typeof(MinMaxInfo));
            var work = info.WorkArea;
            var monitorBounds = info.Monitor;
            mmi.MaxPosition.X = work.Left - monitorBounds.Left;
            mmi.MaxPosition.Y = work.Top - monitorBounds.Top;
            mmi.MaxSize.X = work.Right - work.Left;
            mmi.MaxSize.Y = work.Bottom - work.Top;

            // MinWidth/MinHeight 是 DIP；原生拖动边界必须使用当前显示器的物理像素。
            // 否则 WM_GETMINMAXINFO 被标记为已处理后，窗口仍可能缩得比布局下限更小，
            // 导致标题栏被裁掉、RDP 原生画面越界，边框也无法再拖大恢复。
            var dpi = VisualTreeHelper.GetDpi(this);
            mmi.MinTrackSize.X = (int)Math.Ceiling(MinWidth * dpi.DpiScaleX);
            mmi.MinTrackSize.Y = (int)Math.Ceiling(MinHeight * dpi.DpiScaleY);

            System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, false);
            handled = true;
            return IntPtr.Zero;
        }

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

                // 圆角仍交给 DWM；缩放、客户区与最大化使用 WPF 自带的 WindowChrome。
                // 不再另行拦截 WM_NCCALCSIZE / WM_NCHITTEST，避免两套几何互相覆盖。
                WindowChrome.SetWindowChrome(this, new WindowChrome
                {
                    CaptionHeight = 0,
                    ResizeBorderThickness = new Thickness(6),
                    GlassFrameThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(0),
                    UseAeroCaptionButtons = false
                });
                WindowChrome.SetIsHitTestVisibleInChrome(this, true);

                // 窗口底色 + DWM 圆角。
                // ★ 内容区必须留出边距，否则方形内容会铺到 DWM 圆角之外，
                //   在窗口边缘露出一圈残留的浅色边（用户反馈：
                //   「以中/低画质启动会有一圈灰框，切到高画质就消失」）。
                //
                //   这里原来写的是 Thickness(0) —— 与上面这条注释自相矛盾，
                //   也解释了为什么只有低档位才看得见：
                //   高档位有 AuroraBackdrop 每帧全量重绘，把那圈残余盖住了；
                //   关掉动画后底层不再重绘，露出的窗口底色就暴露出来。
                //
                //   现在与 ToggleMax() 保持同一套边距规则，启动路径不再漏设。
                RootLayer.Effect = null;
                // ★★ 回到 0：不要把内容内缩。★★
                //
                //   DWM 圆角裁的是**窗口**四角，WPF 内容铺满客户区即可跟随；
                //   一旦给 RootLayer 留 Margin，那圈露出的就是窗口 Background，
                //   在深色主题下表现为窗口内侧一圈浅色细边
                //   （用户反馈：「高画质没事，中低画质启动就有一圈灰框」）。
                //
                //   之前误以为是"内容顶到圆角外"，实际恰恰相反：
                //   内缩才会露边。ToggleMax 里的 2/14 同理已一并移除。
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

            // ★ 只有"没被 --page 指定过页面"时才强制回到启动台。
            //   原来这里无条件 Activate()，而它在 --page 处理之后执行，
            //   会把命令行指定的页面覆盖掉（实测 --page=console 无效，
            //   启动后仍停在启动台，中控台从未初始化 → RDP 画面永远不显示）。
            if (!_startupPageApplied)
            {
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
            else
            {
                App.Breadcrumb("startup page preserved (--page)");
            }
        }

        private bool _startupPageApplied;

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

        // ── 云端更新 ────────────────────────────────────────────────
        //
        // 启动后延迟静默检查一次，之后**每小时**再查一次；有新版就把版本号染成
        // 琥珀色 + 显示圆点 + 明写「有新版本 vX.Y.Z」。点击版本号可随时手动检查。
        //
        // ★ 3.2.3：原来是"只在启动时查一次"，长时间挂机的人永远看不到新版本
        //   （用户要求：「要定期检查更新，先设置为一小时」）。
        // ★ 周期任务走 TickHub，不新建 DispatcherTimer（AGENTS.md 性能约束：
        //   曾因 6 个独立 1 秒定时器把空闲 CPU 打到 27%）。
        private Services.CloudUpdater.Manifest? _pendingUpdate;
        private bool _updateChecking;
        private IDisposable? _updateTimer;
        private bool _updateFirstCheckDone;

        /// <summary>检查更新的间隔（秒）。用户要求先设为一小时。</summary>
        private const int UpdateCheckIntervalSeconds = 3600;

        /// <summary>启动后的静默检查 + 之后每小时一次的定期检查（失败静默）。</summary>
        private async void SilentUpdateCheck()
        {
            try
            {
                // 延迟几秒，避开启动时的加载高峰
                await Task.Delay(TimeSpan.FromSeconds(6));
                if (!IsLoaded) return;

                await CheckUpdateQuietAsync();
                _updateFirstCheckDone = true;

                // 之后按 TickHub 节拍定期检查
                _updateTimer = TickHub.Register("UpdateCheck", UpdateCheckIntervalSeconds, () =>
                {
                    if (!_updateFirstCheckDone) return;   // 首次检查还没回来，跳过这一拍
                    _ = CheckUpdateQuietAsync();
                });
            }
            catch { /* 静默检查失败不提示 */ }
        }

        /// <summary>静默查一次；有新版就挂标记。已经有待更新或正在检查时跳过。</summary>
        private async Task CheckUpdateQuietAsync()
        {
            if (_updateChecking || _pendingUpdate != null) return;   // 已发现更新就不再重复查
            _updateChecking = true;
            try
            {
                var m = await Services.CloudUpdater.CheckAsync();
                if (m == null) return;
                _pendingUpdate = m;
                ApplyUpdateBadge(true);
            }
            catch { /* 静默检查失败不提示 */ }
            finally { _updateChecking = false; }
        }

        /// <summary>把版本号标记为「有更新」。不暴露任何发布地址。</summary>
        private void ApplyUpdateBadge(bool hasUpdate)
        {
            try
            {
                if (VersionText == null) return;
                if (hasUpdate)
                {
                    string ver = _pendingUpdate?.Version ?? "";
                    VersionText.Foreground = (Brush)FindResource("AmberBrush");
                    VersionText.ToolTip = "发现新版本 " + ver + "，点击查看";
                    if (UpdateDot != null) UpdateDot.Visibility = Visibility.Visible;

                    // ★ 3.2.3：用文字明确告诉用户"有新版本"，不再只靠一个圆点。
                    if (UpdateHintText != null)
                    {
                        UpdateHintText.Text = ver.Length > 0 ? "有新版本 v" + ver : "有新版本";
                        UpdateHintText.Visibility = Visibility.Visible;
                    }
                }
                else
                {
                    VersionText.Foreground = (Brush)FindResource("TextMuted");
                    VersionText.ToolTip = "点击检查更新";
                    if (UpdateDot != null) UpdateDot.Visibility = Visibility.Collapsed;
                    if (UpdateHintText != null) UpdateHintText.Visibility = Visibility.Collapsed;
                }
            }
            catch { }
        }

        private async void VersionText_Click(object sender, RoutedEventArgs e)
        {
            if (_updateChecking) return;

            // 已检测到更新：直接弹窗
            if (_pendingUpdate != null)
            {
                ShowUpdateDialog(_pendingUpdate);
                return;
            }

            _updateChecking = true;
            try
            {
                VersionText.Text = "检查更新…";
                var m = await Services.CloudUpdater.CheckAsync();
                if (m == null)
                {
                    ShowVersion();
                    Views.ThemedMessage.Info(this, "已是最新版本",
                        "当前版本 v" + Services.CloudUpdater.CurrentVersion + " 已是最新。");
                    return;
                }
                _pendingUpdate = m;
                ShowVersion();
                ApplyUpdateBadge(true);
                ShowUpdateDialog(m);
            }
            finally
            {
                _updateChecking = false;
            }
        }

        private void ShowUpdateDialog(Services.CloudUpdater.Manifest m)
        {
            bool ok = Views.UpdateWindow.ShowFor(this, m);
            if (!ok) return;

            // 用户确认更新后，UpdateWindow 已经启动更新器并要求退出。
            // 这里只负责把主程序干净地关掉（保存配置等）。
            try { Close(); } catch { }
            Application.Current?.Shutdown();
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
                App.Breadcrumb("NavRow_Click -> " + tag);
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
        /// 展开 / 收起侧栏。展开时使用归主窗口所有的独立 HWND 浮层，
        /// 收起时把同一个 NavRail 放回主窗口，不复制导航按钮或业务逻辑。
        /// </summary>
        private void RestoreExpandedNavOnFirstActivation(object? sender, EventArgs e)
        {
            Activated -= RestoreExpandedNavOnFirstActivation;
            if (!_autoHide && _navCollapsed) SetNavCollapsed(false, "启动恢复");
        }

        /// <summary>
        /// 当前页面是否含 HWND 空域（RDP 的 WindowsFormsHost）。
        ///
        /// ★ 只有中控台页才有。其余页面是纯 WPF，NavRail 留在主窗口里就能正常盖住内容，
        ///   根本不需要独立 HWND 浮层。
        ///
        /// ★ 为什么必须区分：用户实测「从别的菜单页切过来也会晃动，以左上角为原点缩放了一下」。
        ///   根因就是无论在哪一页，点导航都会触发侧栏悬停展开，
        ///   而展开会把 NavRail 从主窗口摘下、塞进一个 owned 独立 HWND
        ///   （OwnedOverlayWindow，见其注释：WPF ZIndex 盖不住 HWND 子窗口）。
        ///   主窗口本身是 AllowsTransparency=True 的分层窗口，
        ///   再挂一个带 alpha 的 owned 窗口会让 DWM 重算整块重定向表面，
        ///   右侧内容就整体跳一下 —— 看起来就是「按左上角缩了一下」。
        ///
        ///   2.11.0 没有这套浮层侧栏，所以没有这个现象。
        /// </summary>
        private bool PageNeedsOverlayNav()
        {
            return string.Equals(_activePage, "console", StringComparison.OrdinalIgnoreCase);
        }

        private void EnsureNavOverlayWindow()
        {
            if (NavHomeHost == null) return;
            if (_navOverlayWindow == null)
            {
                // 此时 NavRail 仍在主窗口，用隐藏的原位 Grid 保存物理定位锚点。
                SideNavHost.Children.Remove(NavRail);
                NavRail.VerticalAlignment = VerticalAlignment.Stretch;
                NavHomeHost.Height = SideNavHost.ActualHeight;
                _navOverlayWindow = new SoulLink.Services.OwnedOverlayWindow(
                    this, NavHomeHost, NavRail, "展开导航浮层");
                _navDockedInOverlay = true;
                return;
            }
            if (_navDockedInOverlay) return;

            // 快速收展可能让一次 Hide() 的排队在再次展开后才完成。
            // 原生浮层存活期间只隐藏内容；重新显示前必须显式重新挂载 NavRail。
            if (SideNavHost.Children.Contains(NavRail)) SideNavHost.Children.Remove(NavRail);
            NavRail.VerticalAlignment = VerticalAlignment.Stretch;
            NavHomeHost.Height = SideNavHost.ActualHeight;
            _navOverlayWindow.Content = NavRail;
            _navDockedInOverlay = true;
        }

        private void RestoreNavRail()
        {
            if (!_navDockedInOverlay) return;
            _navOverlayWindow?.HideOverlay();
            if (_navOverlayWindow != null) _navOverlayWindow.Content = null;
            NavRail.ClearValue(FrameworkElement.WidthProperty);
            NavRail.Width = 56;
            Grid.SetColumn(NavRail, 0);
            Grid.SetColumnSpan(NavRail, 2);
            SideNavHost.Children.Add(NavRail);
            _navDockedInOverlay = false;
        }

        private void BeginNavOverlaySync(bool expanding)
        {
            _navTransitionVersion++;
            if (_navAnimationSync == null)
            {
                _navAnimationSync = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                _navAnimationSync.Tick += (_, __) =>
                {
                    if (_navCollapsed)
                    {
                        if (_navOverlayWindow?.IsVisible == true)
                        {
                            if (_navOverlayHidePending) return;
                            _navOverlayHidePending = true;
                            int transition = _navTransitionVersion;
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (transition != _navTransitionVersion || !_navCollapsed) return;
                                RestoreNavRail();
                            }), System.Windows.Threading.DispatcherPriority.Render);
                        }
                        else if (_navDockedInOverlay) RestoreNavRail();
                        if (!_navDockedInOverlay)
                        {
                            _navAnimationSync?.Stop();
                            _navOverlayHidePending = false;
                        }
                        return;
                    }
                    EnsureNavOverlayWindow();
                    NavHomeHost.Height = SideNavHost.ActualHeight;
                    _navOverlayWindow?.UpdateOverlaySize(NavRail.ActualWidth, SideNavHost.ActualHeight);
                    _navOverlayWindow?.BringToFront();
                    if (_navRailExpandStartedAt == DateTime.MinValue) _navRailExpandStartedAt = DateTime.UtcNow;

                    // ★ 稳定即停表：动画跑完、浮层尺寸不再变化之后继续按 16ms 唤醒
                    //   只是在空转（每帧 EnsureNavOverlayWindow + BringToFront），
                    //   而这个 UI 线程同时还要喂 RDP 控制台的焦点/热键路径。
                    //   停表后只要尺寸真变了，SizeChanged / 失活 / 重排会重新起来。
                    double w = NavRail.ActualWidth;
                    double h = SideNavHost.ActualHeight;
                    bool sized = Math.Abs(w - _navLastSyncedWidth) < 0.5 && Math.Abs(h - _navLastSyncedHeight) < 0.5;
                    bool animationSettled = (DateTime.UtcNow - _navRailExpandStartedAt).TotalMilliseconds > 450;
                    if (animationSettled && sized)
                    {
                        _navAnimationSync?.Stop();
                        _navRailExpandStartedAt = DateTime.MinValue;
                        // 清除动画的 hold 值，避免下一轮展开继承上一次中间帧宽度。
                        NavRail.BeginAnimation(FrameworkElement.WidthProperty, null);
                        NavRail.Width = w;
                        _navOverlayWindow?.UpdateOverlaySize(w, h);
                        _navLastSyncedWidth = w;
                        _navLastSyncedHeight = h;
                        return;
                    }
                    _navLastSyncedWidth = w;
                    _navLastSyncedHeight = h;
                };
            }
            _navOverlayHidePending = false;
            _navRailExpandStartedAt = expanding ? DateTime.UtcNow : DateTime.MinValue;
            if (expanding)
            {
                EnsureNavOverlayWindow();
                NavHomeHost.Height = SideNavHost.ActualHeight;
                // Show the 56px rail immediately, before the first animation frame.
                // Waiting for ActualWidth > 56 leaves the requested overlay hidden
                // indefinitely because its content is not in the main visual tree yet.
                double initialWidth = NavRail.ActualWidth > 0 ? NavRail.ActualWidth : 56;
                _navOverlayWindow?.ShowOverlay(initialWidth, SideNavHost.ActualHeight);
                _navOverlayWindow?.BringToFront();
            }
            _navAnimationSync.Start();
        }

        /// <summary>连接/注销动画显示或重排时调用，保持展开导航位于最上层。</summary>
        internal void BringNavigationToFront()
        {
            if (!_navCollapsed && _navDockedInOverlay)
            {
                _navOverlayWindow?.BringToFront();
                // ★ 定时器现在会在稳定后自停，重排时必须显式再拉起来，
                //   否则浮层尺寸会停在旧值上（视觉上是"侧栏压住内容"）。
                RestartNavOverlaySync();
            }
        }

        /// <summary>按需重新启动浮层同步（已停才启动；正在跑就让它自己跑完）。</summary>
        private void RestartNavOverlaySync()
        {
            if (_navAnimationSync == null) return;
            if (_navCollapsed) return;
            if (_navAnimationSync.IsEnabled) return;
            _navRailExpandStartedAt = DateTime.MinValue;
            _navAnimationSync.Start();
        }

        private void SetNavCollapsed(bool collapsed, string reason)
        {
            if (_navStartupLayout) return;
            CancelHideTimer();
            CancelHoverTimer();

            bool changed = _navCollapsed != collapsed;
            _navCollapsed = collapsed;

            try
            {
                // ★ 只有含 RDP 空域的页面才用独立 HWND 浮层。
                //   其它页面在主窗口内直接改宽度，完全不碰窗口合成表面 ——
                //   这样切页/悬停都不会再出现"整体按左上角缩放一下"。
                bool useOverlay = PageNeedsOverlayNav();
                if (!collapsed && useOverlay) EnsureNavOverlayWindow();
                if (collapsed && !useOverlay) RestoreNavRail();

                const double collapsedWidth = 56;
                const double expandedWidth = 180;
                double target = collapsed ? collapsedWidth : expandedWidth;

                // 侧栏变宽会挤压右侧内容，用动画让过程平滑
                // Freeze the current visual width before replacing an animation, so
                // rapid expand/collapse never jumps back to a stale base value.
                double currentWidth = NavRail.ActualWidth > 0 ? NavRail.ActualWidth : target;
                NavRail.BeginAnimation(FrameworkElement.WidthProperty, null);
                NavRail.Width = currentWidth;
                var anim = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = currentWidth,
                    To = target,
                    FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
                    Duration = TimeSpan.FromMilliseconds(collapsed ? 140 : 170),
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                    { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                };
                anim.Completed += (_, __) =>
                {
                    if (_navCollapsed != collapsed) return;
                    NavRail.BeginAnimation(FrameworkElement.WidthProperty, null);
                    NavRail.Width = target;
                    if (!collapsed && useOverlay)
                        _navOverlayWindow?.UpdateOverlaySize(target, SideNavHost.ActualHeight);
                };
                NavRail.BeginAnimation(FrameworkElement.WidthProperty, anim);

                // 只有浮层模式才需要 16ms 的同步定时器（它一直在 BringToFront）。
                // 普通页面没有浮层，启动它只会白白刷新窗口。
                if (useOverlay) BeginNavOverlaySync(!collapsed);

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
            if (AutoHideNavCheck != null) AutoHideNavCheck.Visibility = vis;
            if (NavExpandedDivider != null) NavExpandedDivider.Visibility = vis;
            if (NavSessionPanel != null) NavSessionPanel.Visibility = vis;
            foreach (var t in new FrameworkElement?[]
            {
                NavTitleText, NavTextLaunch, NavTextConsole,
                NavTextDataCenter, NavTextUpdate, NavTextRouteUpdate, NavTextRemote, NavTextSessions, NavTextProcs, NavTextCli
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

        /// <summary>
        /// 把当前窗口的可见性告诉状态中心。
        /// ★ 隐藏时它会停止通知 UI（但仍继续刷新数据，否则挂机自动化会停），
        ///   恢复时立刻强制刷一轮，保证用户回来看到的是真实状态而不是旧数据。
        /// </summary>
        private void SyncUiVisible()
        {
            try
            {
                bool visible = IsVisible && WindowState != WindowState.Minimized;
                Services.AccountStateCenter.SetUiVisible(visible);
            }
            catch { }
        }

        private void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void BtnMax_Click(object sender, RoutedEventArgs e) => ToggleMax();

        private void ToggleMax()
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            // ★ 不要再改 RootLayer.Margin：任何非 0 边距都会在内侧露出一圈窗口底色。
            //   圆角由 DWM 负责，内容铺满即可。
            RootLayer.Margin = new Thickness(0);
        }

        private void UpdateMaxGlyph()
        {
            if (BtnMax?.Content is TextBlock tb)
                tb.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            _navAnimationSync?.Stop();
            _navOverlayWindow?.HideOverlay();
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
            // ★ 3.2.3：定期检查更新的节拍也要停掉，否则退出后 TickHub 还留着这项订阅。
            try { _updateTimer?.Dispose(); _updateTimer = null; } catch { }
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

            // ★ 离开控制台页必须先退全屏：全屏下导航已收纳，但自动化/热键仍可能
            //   触发切页——那样其它页面会在全屏窗口里铺开且没有返回入口。
            if (tag != "console")
            {
                try { ConsoleView.ExitFullScreenIfActive(); } catch { }
            }

            foreach (var kv in _pages)
                kv.Value.Visibility = kv.Key == tag ? Visibility.Visible : Visibility.Collapsed;

            _activePage = tag;
            UpdateRailHighlight();



            // ★ 离开含 RDP 空域的页面时，把导航从独立 HWND 浮层收回主窗口。
            //   否则中控台把 NavRail 挂在浮层里，切到别的页后它会继续浮在最上面，
            //   而且那条 16ms 的 BringToFront 同步也会一直跑。
            if (!PageNeedsOverlayNav() && _navDockedInOverlay)
            {
                try { RestoreNavRail(); } catch (Exception ex) { App.AppendErrorLog("RestoreNavRail on switch", ex); }
            }

            // ★ 控制台页切走时暂停它的 150ms 保护层看门狗：
            //   本页只切 Visibility，不会触发 Unloaded，看门狗会一直空转
            //   （每秒 6.7 次唤醒线程池）。RDP 画面不可见时检查毫无意义。
            if (tag != "console")
            {
                try { ConsoleView.OnPageDeactivated(); } catch { }
            }

            // ★ 联机大厅页切走时停止房间列表轮询 + 释放「正在看」活动
            //   （按需访问：不看联机页就不发请求）。在房间里时心跳仍由
            //   LobbyConnection.SetInRoom 单独保持。
            if (tag != "sessions")
            {
                try { LobbyView.OnPageDeactivated(); } catch { }
            }

            switch (tag)
            {
                case "automation": AutomationView.Activate(); break;
                case "console":
                    // ★ Activate() 里有初始化逻辑（建列表、挂会话监控、Broker 重置），
                    //   不能因为要恢复看门狗就跳过它。
                    ConsoleView.Activate();
                    ConsoleView.OnPageActivated();
                    break;
                case "datacenter": DataCenterView.Activate(); break;
                case "remote": RemoteView.ReloadAsync(); break;
                case "update": UpdateView.Reload(); break;
                case "routeupdate": RouteUpdateView.Reload(); break;
                case "sessions":
                    LobbyView.OnPageActivated();
                    break;
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

                // ★ 文字与图标要分开取色：
                //   原来两者都用 muted（#7A8CB4E0，仅 48% 不透明），
                //   在展开的深色面板上文字几乎糊在一起（用户反馈"文字颜色不对"）。
                //   图标用 muted 作次级元素，文字用 secondary 保证可读。
                var textIdle = (System.Windows.Media.Brush)FindResource("TextSecondary");
                // 图标和选中底色由菜单专用装饰层读取现有主题画刷，不修改主题资源。

                // ★ 导航行现在是 Grid（图标+文字），不再是 Button。
                //   原来只处理 Button，导致选中高亮完全失效（用户实测）。
                foreach (var child in RailIcons.Children)
                {
                    if (child is not Grid row || row.Tag is not string t) continue;

                    bool on = t == _activePage;
                    // A 方案：装饰只作用于本菜单行，原 Grid 的尺寸和点击处理不变。
                    row.Background = System.Windows.Media.Brushes.Transparent;
                    // 只更新既有装饰层的显示状态；悬停与尺寸由侧栏内部的纯样式绑定提供。
                    foreach (var el in row.Children)
                    {
                        if (el is SoulLink.Controls.NavigationOrnament navGlyph)
                        {
                            navGlyph.IsSelected = on;
                            navGlyph.IsLight = !SoulLink.Services.ThemeManager.IsDark;
                        }
                        else if (el is System.Windows.Controls.TextBlock tb)
                        {
                            tb.Foreground = on ? primary : textIdle;
                        }
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

        /// <summary>启动时直接跳到指定页（供调试/命令行使用）。</summary>
        public void NavigateToStartup(string tag)
        {
            try
            {
                _startupPageApplied = true;
                SetActivePage(tag);
                App.Breadcrumb("NavigateToStartup -> " + tag);
            }
            catch (Exception ex) { App.Breadcrumb("NavigateToStartup FAILED: " + ex.Message); }
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