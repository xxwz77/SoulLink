using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SoulLink.Controls;
using SoulLink.Models;
using SoulLink.Services;

namespace SoulLink.Views
{
    /// <summary>
    /// 中控台。结构分两层：
    ///   ① 配置页：左侧用户列表 + 右侧该用户的 RDP 参数
    ///   ② 画面页：每个用户一个，顶部标签卡切换
    /// 
    /// 关键设计：
    ///   · 打开页面**不自动连接**，用户点「打开画面」才接；
    ///   · 画面页控件常驻，切走只改 Visibility，不重建；
    ///   · 配置改动即时保存，但画面要重连才生效（会在界面提示）。
    /// </summary>
    public partial class ConsolePage : UserControl
    {
        private sealed class UserRow
        {
            public Account Account = new Account();
            public RdpConsole? Console;
            public Border? Chip;
            public TextBlock? ChipText;
            public TextBlock? ChipDot;
        }

        private readonly List<UserRow> _rows = new List<UserRow>();
        private UserRow? _editing;    // 配置页正在编辑的用户
        private UserRow? _viewing;    // 画面页正在显示的用户
        private bool _initialized;
        private bool _logoffInProgress;
        private bool _busyVisible;
        private OwnedOverlayWindow? _busyOverlayWindow;
        private Border? _busyOverlayContent;
        private TextBlock? _busyText;
        private Button? _busyReconnectButton;
        private bool _shieldPendingAfterBusy;
        private System.Windows.Threading.DispatcherTimer? _shieldWatch;
        private RdpConsole? _shieldWatchedConsole;
        private int _shieldWatchedGeneration = -1;
        private IntPtr _shieldWatchedHandle;
        private DateTime _lastShieldRecoveryLog = DateTime.MinValue;
        private UserRow? _shieldPendingNewTab;
        private OwnedOverlayWindow? _shieldOverlayWindow;
        private Border? _shieldOverlayContent;

        public MainWindow? MainWindowRef { get; set; }
        public event Action? AddRequested;

        public ConsolePage()
        {
            InitializeComponent();
            Loaded += (_, __) =>
            {
                var window = Window.GetWindow(this);
                if (window != null)
                {
                    window.Deactivated += (_, __) =>
                    {
                        // 只释放输入，不隐藏保护层。真实 Win32 Owner + 非 Topmost
                        // 已保证它随主窗口沉到其他应用下面；Hide() 会让保护层消失。
                        _busyOverlayWindow?.HideForOwnerState();
                        _shieldReleased = false;
                        _viewing?.Console?.ReleasePointerForRearm();
                        UpdateShield();
                    };
                    window.Activated += (_, __) => Dispatcher.InvokeAsync(() =>
                    {
                        if (_busyVisible) SetBusyVisible(true);
                        UpdateShield();
                    });
                    // 窗口位置/大小变化时，Popup 需要重新贴到视口上
                    window.LocationChanged += (_, __) => UpdateShield();
                    window.SizeChanged += (_, __) => UpdateShield();
                }
            };
            // ★ 连接遮罩必须跟着「画面视口」走，不是跟着整个页面走。
            //   原来固定 900x620、只在 ConsolePage.SizeChanged 时更新，
            //   切页/侧栏展开/窗口缩放时视口变了它不变，遮罩就和画面不重合
            //   （用户反馈：连接动画没有自适应窗口大小）。
            //   改成监听 ViewportHost 自身的 SizeChanged，并补一次首帧对齐。
            ViewportHost.SizeChanged += (_, __) => { SyncBusyOverlaySize(); SyncShieldSize(); };
            Loaded += (_, __) => { EnsureBusyOverlayWindow(); EnsureShieldOverlayWindow(); EnsureShieldWatch(); };
            Unloaded += (_, __) => { _shieldWatch?.Stop(); _shieldWatch = null; };
            Loaded += (_, __) => { SyncBusyOverlaySize(); SyncShieldSize(); };

            // ★ WindowsFormsHost 是独立 HWND，**不受 WPF Visibility 控制**：
            //   本页被切走（Collapsed）时，RDP 控件仍会浮在最上层，
            //   盖住启动台/设置等其他页面的内容（用户实测"RDP 覆盖别的界面"）。
            //   所以切走时主动把控件从视觉树摘下来，切回来再挂上。
            IsVisibleChanged += (_, __) =>
            {
                try
                {
                    if (IsVisible)
                    {
                        // 切回本页：如果当前正在看某个账号且已连接，重新挂上
                        _shieldOverlayWindow?.HideOverlay();
                        if (_viewing?.Console != null && _viewing.Console.IsConnected
                            && !ViewportHost.Children.Contains(_viewing.Console))
                        {
                            ViewportHost.Children.Clear();
                            ViewportHost.Children.Add(_viewing.Console);
                            _viewing.Console.OnShown();
                        }
                    }
                    else
                    {
                        // 切走：摘掉控件（连接状态保留，不 Dispose）
                        ViewportHost.Children.Clear();
                        _busyOverlayWindow?.HideForOwnerState();
                        _shieldReleased = false;
                        _shieldOverlayWindow?.HideOverlay();
                    }
                }
                catch { }
            };
            Loaded += (_, __) => Activate();

            double sens = App.Config.Settings.LookSensitivity > 0 ? App.Config.Settings.LookSensitivity : 4.0;
            if (SensSlider != null)
            {
                // ★ 必须先屏蔽 ValueChanged 再赋值：
                //   工具栏浮层里的 SensSlider 和「RDP 通用设置」共用同一个设置项，
                //   而 XAML 里写死 Value="4"。构造时直接赋值会触发 SensSlider_ValueChanged，
                //   它无脑 ConfigStore.Save(...) —— 结果用户保存的灵敏度在下次启动时被覆盖。
                //   （实测：写入 9.5 → 重启后变成 1/4）
                _suppressSensSave = true;
                SensSlider.Value = Math.Min(12, Math.Max(1, sens));
                if (SensText != null) SensText.Text = sens.ToString("0.0") + "x";
                _suppressSensSave = false;
            }
            if (HotkeyBtn != null)
            {
                HotkeyBtn.SetSpec(HotkeySpec.Parse(App.Config.Settings.LockHotkey)
                    ?? HotkeySpec.Parse("Ctrl+Alt+Q"));
                HotkeyBtn.Recorded += OnHotkeyRecorded;
            }
        }

        /// <summary>
        /// 让连接遮罩严格铺满画面视口。
        /// Popup 的内容不会自动跟随 PlacementTarget 伸缩，必须手动同步宽高，
        /// 否则遮罩会比画面小一圈或大一圈（用户反馈：没有自适应窗口大小）。
        /// </summary>
        private void EnsureBusyOverlayWindow()
        {
            if (_busyOverlayWindow != null) return;
            var owner = Window.GetWindow(this);
            if (owner == null) return;
            _busyText = new TextBlock
            {
                Text = "正在连接…", FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
                LineHeight = 20, TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Brush)FindResource("TextSecondary")
            };
            _busyReconnectButton = new Button
            {
                Content = "重新连接", Visibility = Visibility.Collapsed,
                Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(22,8,22,8),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0,18,0,0)
            };
            _busyReconnectButton.Click += BtnBusyReconnect_Click;
            var panel = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 460
            };
            panel.Children.Add(_busyText);
            panel.Children.Add(new ProgressBar
            {
                IsIndeterminate = true, Width = 180, Height = 3, Margin = new Thickness(0,10,0,0),
                Foreground = (Brush)FindResource("CyanBrush"), Background = new SolidColorBrush(Color.FromArgb(0x22,0xFF,0xFF,0xFF)),
                BorderThickness = new Thickness(0)
            });
            panel.Children.Add(_busyReconnectButton);
            _busyOverlayContent = new Border { Background = new SolidColorBrush(Color.FromArgb(0xF2,0x05,0x09,0x13)), Child = panel };
            _busyOverlayWindow = new OwnedOverlayWindow(owner, ViewportHost, _busyOverlayContent, "连接动画", allowInactiveOwner: true);
        }

        private void SyncBusyOverlaySize()
        {
            try
            {
                double w = ViewportHost.ActualWidth;
                double h = ViewportHost.ActualHeight;
                if (w <= 0 || h <= 0) return;

                EnsureBusyOverlayWindow();
                if (_busyOverlayContent != null)
                {
                    _busyOverlayContent.Width = w;
                    _busyOverlayContent.Height = h;
                }
                if (_busyOverlayWindow?.IsRequested == true)
                    _busyOverlayWindow.UpdateOverlaySize(w, h);
            }
            catch { }
        }

        // ── 保护层（点击画面才把键鼠交给 RDP）──────────────────────────────

        private void ShieldTrace(string stage, UserRow? row = null)
        {
            if (!RdpConsole.VerboseLog) return;
            try
            {
                var console = row?.Console ?? _viewing?.Console;
                var owner = Window.GetWindow(this);
                string text = DateTime.Now.ToString("HH:mm:ss.fff")
                    + " stage=" + stage
                    + " user=" + (row?.Account.Caption ?? _viewing?.Account.Caption ?? "-")
                    + " setting=" + App.Config.Settings.RdpCaptureOnClick
                    + " connected=" + (console?.IsConnected == true)
                    + " mounted=" + (console != null && ViewportHost.Children.Contains(console))
                    + " released=" + _shieldReleased
                    + " pending=" + _shieldPendingAfterBusy
                    + " busy=" + _busyVisible
                    + " view=" + ViewPage.Visibility
                    + " pageVisible=" + IsVisible
                    + " ownerActive=" + (owner?.IsActive == true)
                    + " anchor=" + ViewportHost.ActualWidth.ToString("0") + "x" + ViewportHost.ActualHeight.ToString("0")
                    + " requested=" + (_shieldOverlayWindow?.IsRequested == true)
                    + " visible=" + (_shieldOverlayWindow?.NativeVisible == true);
                System.IO.Directory.CreateDirectory(ConfigStore.ConfigDir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(ConfigStore.ConfigDir, "shield-trace.log"), text + Environment.NewLine);
            }
            catch { }
        }

        private void EnsureShieldWatch()
        {
            if (_shieldWatch != null) return;
            _shieldWatch = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(150) };
            _shieldWatch.Tick += (_, __) =>
            {
                try
                {
                    var console = _viewing?.Console;
                    if (console == null || ViewPage.Visibility != Visibility.Visible || !IsVisible)
                    {
                        _shieldWatchedConsole = null;
                        _shieldWatchedGeneration = -1;
                        _shieldWatchedHandle = IntPtr.Zero;
                        _shieldOverlayWindow?.HideForOwnerState();
                        return;
                    }

                    bool connected = console.IsConnected;
                    IntPtr nativeHandle = console.NativeViewportHandle;
                    bool nativeReady = nativeHandle != IntPtr.Zero
                        && ViewportHost.Children.Contains(console) && console.IsVisible;
                    bool canReceiveInput = connected || nativeReady;
                    if (canReceiveInput && (!ReferenceEquals(_shieldWatchedConsole, console)
                        || _shieldWatchedGeneration != console.ConnectionGeneration
                        || _shieldWatchedHandle != nativeHandle))
                    {
                        bool hadIdentity = ReferenceEquals(_shieldWatchedConsole, console);
                        _shieldWatchedConsole = console;
                        _shieldWatchedGeneration = console.ConnectionGeneration;
                        _shieldWatchedHandle = nativeHandle;
                        _shieldReleased = false;
                        console.ReleasePointerForRearm();
                        Debug(hadIdentity ? "检测到 RDP 原生画面变化 → 立即重新保护" : "检测到当前 RDP 原生画面 → 自动保护");
                        // 新画面身份在同一个 Tick 内显示，不再等下一轮。
                        UpdateShield();
                    }

                    // 持续满足保护条件就保持显示，不依赖连接/动画的某次回调。
                    // 用户已主动点击解锁时不抢回鼠标。
                    if (!_shieldReleased && _shieldOverlayWindow?.NativeVisible != true)
                    {
                        UpdateShield();
                        if ((DateTime.UtcNow - _lastShieldRecoveryLog).TotalSeconds > 5)
                        {
                            _lastShieldRecoveryLog = DateTime.UtcNow;
                            Debug("保护层看门狗恢复显示（无需切页）");
                        }
                    }
                    else if (_shieldReleased) _shieldOverlayWindow?.HideForOwnerState();
                }
                catch (Exception ex) { App.AppendErrorLog("保护层看门狗", ex); }
            };
            _shieldWatch.Start();
        }

        private void RecreateShieldAfterConsoleDisposal()
        {
            // 注销销毁整个 ActiveX 时同步销毁保护层 HWND，下次新建不复用旧状态。
            _shieldWatch?.Stop();
            _shieldWatch = null;
            _shieldWatchedConsole = null;
            _shieldWatchedGeneration = -1;
            _shieldWatchedHandle = IntPtr.Zero;
            _shieldPendingNewTab = null;
            _shieldPendingAfterBusy = false;
            _shieldReleased = false;
            var old = _shieldOverlayWindow;
            _shieldOverlayWindow = null;
            _shieldOverlayContent = null;
            if (old != null) { try { old.HideOverlay(); old.Close(); } catch { } }
            EnsureShieldOverlayWindow();
            EnsureShieldWatch();
        }

        private void EnsureShieldOverlayWindow()
        {
            if (_shieldOverlayWindow != null) return;
            var owner = Window.GetWindow(this);
            if (owner == null) return;
            _shieldOverlayContent = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)),
                Cursor = Cursors.Arrow,
                Focusable = false,
                Child = new TextBlock
                {
                    Text = "点击此处接管键鼠",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                    IsHitTestVisible = false
                }
            };
            // 捕获 MouseDown 是为了可靠地吞掉第一次点击。MouseUp 才解锁，
            // 所以“用于解锁的那一次点击”不会继续进入 RDP 或远端游戏。
            _shieldOverlayContent.MouseLeftButtonDown += ShieldOverlay_MouseDown;
            _shieldOverlayContent.MouseLeftButtonUp += ShieldOverlay_MouseUp;
            _shieldOverlayWindow = new OwnedOverlayWindow(
                owner, ViewportHost, _shieldOverlayContent, "RDP 保护层", allowInactiveOwner: true);
            UpdateShield();
        }

        /// <summary>保护层是否已被用户点掉（离开视口或切页后重置）。</summary>
        private bool _shieldReleased;

        /// <summary>
        /// 同步保护层显示状态。
        /// 条件：勾选「点击画面才捕获」+ 已连接 + 非自由视角 + 未点掉 + 主窗口在前台。
        /// 真实 Win32 Owner 保证浮层不会越过主窗口；失活时同步隐藏并重新锁定。
        /// </summary>
        private void UpdateShield()
        {
            try
            {
                EnsureShieldOverlayWindow();
                if (_shieldOverlayWindow == null) return;
                var win = Window.GetWindow(this);
                bool visible = win != null && win.IsVisible && win.WindowState != WindowState.Minimized;

                // 中控台处在任意 RDP 标签（不是“配置”）就保护。
                // 新建/重连/注销后重开都不依赖 Connected、标签创建回调或 IsActive。
                // Owner + 非 Topmost 保证层级；用户点击解锁或切回配置页才解除。
                bool want = App.Config.Settings.RdpCaptureOnClick
                            && _viewing?.Console != null
                            && !_viewing.Console.IsLooking
                            && !_shieldReleased
                            && ViewPage.Visibility == Visibility.Visible
                            && ConfigPage.Visibility != Visibility.Visible
                            && visible;
                ShieldTrace(want ? "show" : "suppress", _viewing);
                if (want)
                {
                    SyncShieldSize();
                    _shieldOverlayWindow.ShowOverlay(ViewportHost.ActualWidth, ViewportHost.ActualHeight);
                }
                else _shieldOverlayWindow.HideForOwnerState();
            }
            catch (Exception ex) { App.AppendErrorLog("UpdateShield", ex); }
        }

        private void SyncShieldSize()
        {
            try
            {
                double w = ViewportHost.ActualWidth;
                double h = ViewportHost.ActualHeight;
                if (w <= 0 || h <= 0) return;
                if (_shieldOverlayContent != null) { _shieldOverlayContent.Width = w; _shieldOverlayContent.Height = h; }
                if (_shieldOverlayWindow?.IsRequested == true) _shieldOverlayWindow.UpdateOverlaySize(w, h);
            }
            catch { }
        }

        /// <summary>用户点击保护层：撤掉它，把键鼠交给 RDP。</summary>
        private void ShieldOverlay_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // 只接收按下，不解锁。保护层继续截获 Up，首次点击不透传到远端。
            e.Handled = true;
        }

        private void ShieldOverlay_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_shieldReleased) { e.Handled = true; return; }
            e.Handled = true;
            _shieldReleased = true;
            _shieldOverlayWindow?.HideOverlay();
            try { _viewing?.Console?.OnShieldReleased(); } catch { }
            if (InputText != null) InputText.Text = "已接管键鼠 · 光标移出画面后重新保护";
            Debug("保护层已解除（完整消费首次 Down/Up 后解锁）");
        }

        /// <summary>
        /// RDP 加入视觉树后，必须等一次布局再显示保护层。
        /// ConnectAsync 返回时 Console 尚未挂载，此时 UpdateShield 没有可见锚点；
        /// 第二个及后续用户又不会重走首次页面 Loaded，必须在这里显式刷新。
        /// </summary>
        private void RefreshShieldAfterLayout(UserRow row)
        {
            if (!ReferenceEquals(row, _viewing)) return;
            UpdateShield();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(row, _viewing)) return;
                if (!ViewportHost.Children.Contains(row.Console)) return;
                UpdateShield();
                SyncShieldSize();
                _shieldOverlayWindow?.SyncToAnchor();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>光标离开视口、失活或切页后统一回到锁定态。</summary>
        private void RearmShield(bool force = false)
        {
            if (!force && !_shieldReleased) return;
            _shieldReleased = false;
            UpdateShield();
        }

        public void Activate()
        {
            RebuildUserList();
            RenderTabs();
            _initialized = true;
            SyncBusyOverlaySize();

            if (!_autoConnectTried)
            {
                _autoConnectTried = true;
                // 清理超过 7 天的历史日志，避免长期挂机堆积
                try { ConfigStore.PruneDailyLogs("rdp-debug", 7); } catch { }
                // ★ 后台执行，绝不 await：开机自启时界面必须立即可操作。
                //   上次卡死的教训：任何会话查询/连接上的同步等待都会冻住 UI。
                // ★ 只保留会话监控这一条投送路径。
                //   原来的 AutoConnectAsync 会和监控同时投送，导致同一会话出现
                //   多个 --relay 代理（实测会话 4 出现 2~3 个），互相争用输入。
                //   监控已经覆盖"启动即连接 + 晚登录账号"两种场景，无需旧路径。
                StartSessionWatch();
            }
        }

        private bool _autoConnectTried;
        private bool _autoConnectRunning;

        // ── 会话监控：配置里的账号一出现就投送通道 ──────────────────────────

        private System.Windows.Threading.DispatcherTimer? _sessionWatch;
        /// <summary>已经投送过（或正在等待）的账号 Id，避免重复触发。</summary>
        private readonly HashSet<string> _deployHandled = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// 启动会话监控：每隔几秒查一次在线会话，
        /// 命中「在自动连接列表里、且尚未投送」的账号就发起投送。
        /// 这样不依赖启动瞬间的快照 —— 账号晚一点登录也能被接上。
        /// </summary>
        private void StartSessionWatch()
        {
            try
            {
                if (_sessionWatch != null) return;
                _sessionWatch = new System.Windows.Threading.DispatcherTimer
                {
                    // 2 秒扫描：query user 实测仅 ~28ms，开销可接受。
                    // 配合 3 秒稳定期，会话出现后约 5 秒就能投送出通道。
                    Interval = TimeSpan.FromSeconds(2)
                };
                int tickCount = 0;
                _sessionWatch.Tick += async (_, __) =>
                {
                    tickCount++;
                    try
                    {
                        await ScanAndDeployAsync();
                        if (tickCount % 6 == 1) Debug("会话监控 tick#" + tickCount + " 完成");
                    }
                    catch (Exception ex) { Debug("会话监控 tick#" + tickCount + " 异常：" + ex.Message); }
                };
                _sessionWatch.Start();
                Debug("会话监控已启动（每 2 秒扫描，命中后等 " +
                      RdpConsole.SessionSettleSeconds + " 秒稳定再投送）");
            }
            catch (Exception ex) { Debug("启动会话监控失败：" + ex.Message); }
        }

        /// <summary>
        /// 会话首次被看到的时间（账号 Id → 该会话号的起始时刻）。
        /// 用途：只有"同一个会话号连续在线满 SessionSettleSeconds 秒"才投送通道。
        /// 会话号变了（重新登录）就重新计时。
        /// </summary>
        private readonly Dictionary<string, (int sessionId, DateTime since)> _sessionSeen
            = new Dictionary<string, (int, DateTime)>(StringComparer.Ordinal);

        /// <summary>已经主动发起过 RDP 登录的账号（每账号只试一次，避免反复重连）。</summary>
        private readonly HashSet<string> _loginRequested = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>自动连接串行闸门：切画面是全局操作，必须一个个来。</summary>
        private readonly System.Threading.SemaphoreSlim _autoConnectGate
            = new System.Threading.SemaphoreSlim(1, 1);

        private async Task ScanAndDeployAsync()
        {
            try
            {
                var ids = App.Config.Settings.AutoConnectAccountIds;
                if (ids == null || ids.Count == 0) return;

                // ★ 先查会话，用最新状态决定"要不要继续盯着"。
                //   原来在查会话之前就用 _deployHandled 过滤掉了已投送的账号，
                //   导致"A2 连上过一次、之后掉线"时监控直接跳过它，
                //   永远不再重连（用户实测：A3 拉起成功、A2 一直 Disc）。
                var sessionsNow = await SessionService.QueryAsync().ConfigureAwait(true);

                var watching = new List<(UserRow row, string caption)>();
                foreach (var id in ids)
                {
                    // 取出该账号当前会话
                    SessionInfo? cur = null;
                    UserRow? hit = null;
                    foreach (var r in _rows)
                        if (string.Equals(r.Account.Id, id, StringComparison.Ordinal))
                        { hit = r; cur = SessionService.Find(sessionsNow, r.Account.UserName); break; }
                    if (hit == null) continue;

                    // 会话掉了（无会话 / Disc）→ 无论是否投送过，都要重新盯着
                    bool alive = cur != null && cur.Id > 0
                                 && !string.Equals(cur.State, "Disc", StringComparison.OrdinalIgnoreCase);
                    if (!alive)
                    {
                        _deployHandled.Remove(id);   // 允许重新投送
                        watching.Add((hit, hit.Account.Caption));
                        continue;
                    }

                    // 会话活着：已投送过的就不再盯
                    if (_deployHandled.Contains(id)) continue;
                    watching.Add((hit, hit.Account.Caption));
                }
                // 清理已从配置移除的账号的计时
                var stale = new List<string>();
                foreach (var k in _sessionSeen.Keys)
                    if (!ids.Contains(k)) stale.Add(k);
                foreach (var k in stale) _sessionSeen.Remove(k);

                if (watching.Count == 0) return;

                var sessions = await SessionService.QueryAsync().ConfigureAwait(true);
                var now = DateTime.UtcNow;

                foreach (var (row, caption) in watching)
                {
                    var s = SessionService.Find(sessions, row.Account.UserName);
                    // ★ 没有会话 **或** 会话是断开（Disc）状态 → 都要主动连接。
                    //   Disc 只是"用户还登录着但没连"，RDP 连上即可恢复桌面；
                    //   只处理"完全没会话"会漏掉绝大多数挂机场景（用户实测：
                    //   账号一直显示"断开"，程序从不去拉起）。
                    bool needConnect = s == null || s.Id <= 0
                                       || string.Equals(s.State, "Disc", StringComparison.OrdinalIgnoreCase);
                    if (needConnect)
                    {
                        if (_sessionSeen.Remove(row.Account.Id))
                            Debug("会话监控：「" + caption + "」会话不可用，计时清零");

                        // 每个账号只主动登录一次，避免反复重连把会话搅乱
                        if (_loginRequested.Add(row.Account.Id))
                        {
                            Debug("会话监控：「" + caption + "」会话不可用（"
                                  + (s == null ? "无会话" : s.State) + "），主动发起 RDP 连接");
                            _ = Task.Run(async () =>
                            {
                                // ★ 串行化：切画面是全局操作，多个账号同时切会互相打断。
                                await _autoConnectGate.WaitAsync();
                                try
                                {
                                    // ★ 必须先切到这个账号的画面才能连接。
                                    //   实测：后台账号的 Console 不在 ViewportHost 里，
                                    //   WindowsFormsHost 没有父窗口，ActiveX 创建失败
                                    //   （错误：无法创建 RDP 控件）。
                                    //   所以自动连接必须串行：切页 → 连接 → 下一个。
                                    int sid = s?.Id ?? 0;
                                    // ★ 必须在 UI 线程上跑（切画面、建控件），
                                    //   并且要**真正等它完成**再取结果写日志。
                                    //   原来写成 InvokeAsync(async () => ...)，那是 async void：
                                    //   InvokeAsync 立刻返回，ok/msg 还没赋值就打了日志，
                                    //   于是出现 "自动连接失败：A2 → " 后面空着（用户反馈）。
                                    var (ok, msg) = await Dispatcher
                                        .InvokeAsync(() => ConnectOnUiAsync(row))
                                        .Task.Unwrap();

                                    Debug(ok ? ("自动连接成功：" + caption + "（会话 " + sid + "）")
                                             : ("自动连接失败：" + caption + " → " + msg));

                                    if (ok)
                                    {
                                        _openedIds.Add(row.Account.Id);
                                        _deployHandled.Remove(row.Account.Id);
                                    }
                                    else
                                    {
                                        _loginRequested.Remove(row.Account.Id);
                                    }
                                    await Dispatcher.InvokeAsync(RenderTabs);
                                }
                                catch (Exception ex) { Debug("自动登录异常：" + caption + " → " + ex.Message); }
                                finally { _autoConnectGate.Release(); }
                            });
                        }
                        continue;
                    }

                    // 会话号变化 → 重新计时
                    if (!_sessionSeen.TryGetValue(row.Account.Id, out var rec) || rec.sessionId != s.Id)
                    {
                        _sessionSeen[row.Account.Id] = (s.Id, now);
                        Debug("会话监控：「" + caption + "」会话 " + s.Id + " 上线，开始计时（需满 "
                              + RdpConsole.SessionSettleSeconds + " 秒）");
                        continue;
                    }

                    int onlineSec = (int)(now - rec.since).TotalSeconds;
                    if (onlineSec < RdpConsole.SessionSettleSeconds)
                    {
                        // 还没满稳定期，继续等
                        continue;
                    }

                    // ★ 已满稳定期 → 投送
                    _deployHandled.Add(row.Account.Id);
                    Debug("会话监控：「" + caption + "」已在线 " + onlineSec + " 秒，开始投送通道");

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (row.Console == null)
                                await Dispatcher.InvokeAsync(() => InitializeConsole(row));
                            row.Console?.SetForegroundActive(false);

                            var console = await Dispatcher.InvokeAsync(() => row.Console);
                            if (console == null) return;

                            bool ok = await console.StartInputBySessionAsync().ConfigureAwait(false);
                            Debug(ok ? "会话监控：通道投送成功 " + caption
                                     : "会话监控：通道投送跳过 " + caption);
                            await Dispatcher.InvokeAsync(RenderTabs);
                        }
                        catch (Exception ex)
                        {
                            Debug("会话监控投送异常：" + ex.Message);
                            _deployHandled.Remove(row.Account.Id);   // 允许下轮重试
                            _sessionSeen.Remove(row.Account.Id);
                        }
                    });
                }
            }
            catch (Exception ex) { Debug("会话扫描异常：" + ex.Message); }
        }

        /// <summary>
        /// 在 UI 线程上完成"切画面 → 连接"，返回 (是否连上, 失败原因)。
        /// ★ 切画面与创建 RDP 控件都必须在 UI 线程执行。
        /// </summary>
        private async Task<(bool ok, string reason)> ConnectOnUiAsync(UserRow row)
        {
            try
            {
                if (row.Console == null) InitializeConsole(row);
                await ShowViewPageAsync(row);

                var console = row.Console;
                if (console == null) return (false, "Console 为空");
                console.BindAccount(row.Account);

                // ShowViewPageAsync 内部可能已经连上
                if (console.IsConnected) return (true, "");

                var cr = await ConnectAsync(row);
                if (cr.ok && console.IsConnected) return (true, "");
                return (false, string.IsNullOrWhiteSpace(cr.reason) ? "连接未建立" : cr.reason);
            }
            catch (Exception ex) { return (false, "异常：" + ex.Message); }
        }

        /// <summary>
        /// 启动后自动连接配置的 RDP 用户（可多个）。
        ///
        /// 设计要点：
        ///   · 通道投送只依赖会话号，不需要先连 RDP（用户思路）；
        ///   · 全部在后台线程跑，UI 不参与任何等待；
        ///   · 只把第一个账号打开画面，其余留在标签卡，切页即可看到。
        /// </summary>
        private async Task AutoConnectAsync()
        {
            if (_autoConnectRunning) return;
            _autoConnectRunning = true;
            try
            {
                var ids = App.Config.Settings.AutoConnectAccountIds;
                if (ids == null || ids.Count == 0) { Debug("自动连接：未配置账号，跳过"); return; }

                await Task.Delay(1200).ConfigureAwait(false);

                // 收集目标（读 App.Config 和 _rows 需回 UI 线程，用 Dispatcher 取快照）
                var snapshot = await Dispatcher.InvokeAsync(() =>
                {
                    var list = new List<(UserRow row, string caption)>();
                    foreach (var id in ids)
                        foreach (var r in _rows)
                            if (string.Equals(r.Account.Id, id, StringComparison.Ordinal))
                            { list.Add((r, r.Account.Caption)); break; }
                    return list;
                });

                if (snapshot.Count == 0) { Debug("自动连接：配置的账号都不存在"); return; }
                Debug("自动连接：共 " + snapshot.Count + " 个账号");

                // ① 第一个：打开画面（走完整连接流程，本身是 async，不阻塞）
                await Dispatcher.InvokeAsync(() => _ = ShowViewPageAsync(snapshot[0].row));

                // ② 其余：只投送通道，不开画面
                for (int i = 1; i < snapshot.Count; i++)
                {
                    try
                    {
                        var (row, caption) = snapshot[i];
                        Debug("自动连接（投送通道）：" + caption);

                        await Dispatcher.InvokeAsync(() =>
                        {
                            _openedIds.Add(row.Account.Id);
                            if (row.Console == null) InitializeConsole(row);
                            row.Console?.SetForegroundActive(false);
                        });

                        var console = await Dispatcher.InvokeAsync(() => row.Console);
                        if (console != null)
                        {
                            bool ok = await console.StartInputBySessionAsync().ConfigureAwait(false);
                            Debug(ok ? "通道投送成功：" + caption
                                     : "通道投送跳过：" + caption + "（无会话）");
                        }
                    }
                    catch (Exception ex) { Debug("投送通道失败：" + ex.Message); }
                }

                await Dispatcher.InvokeAsync(RenderTabs);
            }
            catch (Exception ex) { Debug("自动连接失败：" + ex.Message); }
            finally { _autoConnectRunning = false; }
        }

        // ── 标签卡 ───────────────────────────────────────────────────────────

        /// <summary>已经打开过画面的用户（用户要求：打开之后才出现标签卡）。</summary>
        private readonly HashSet<string> _openedIds = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>多选：被勾选的用户（可以批量打开画面）。</summary>
        private readonly HashSet<string> _checkedIds = new HashSet<string>(StringComparer.Ordinal);

        private void RenderTabs()
        {
            TabBar.Children.Clear();

            // 「配置」页签始终在第一位
            TabBar.Children.Add(MakeChip("配置", null));

            // 只为「打开过」的用户显示页签，没打开的不占位
            foreach (var row in _rows)
            {
                if (!_openedIds.Contains(row.Account.Id)) continue;
                TabBar.Children.Add(MakeChip(row.Account.Caption, row));
            }
        }

        private UIElement MakeChip(string text, UserRow? row)
        {
            bool on = row == null ? _viewing == null : ReferenceEquals(_viewing, row);

            var border = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(11, 6, 11, 6),
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(on
                    ? Color.FromArgb(0x2E, 0x7C, 0x5C, 0xFF)
                    : Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(on
                    ? Color.FromArgb(0x77, 0x7C, 0x5C, 0xFF)
                    : Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1)
            };

            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            if (row != null)
            {
                var dot = new TextBlock
                {
                    Text = "\u25CF",
                    FontSize = 7,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                    Foreground = new SolidColorBrush(row.Console?.IsConnected == true
                        ? Color.FromRgb(0x41, 0xF0, 0xC0)
                        : Color.FromRgb(0x6B, 0x7C, 0x9C))
                };
                sp.Children.Add(dot);
                row.ChipDot = dot;
            }

            var label = new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = on ? (Brush)FindResource("TextPrimary") : (Brush)FindResource("TextSecondary")
            };
            sp.Children.Add(label);
            border.Child = sp;

            border.MouseLeftButtonUp += (_, __) =>
            {
                if (row == null) ShowConfigPage();
                else { _showGeneralSettings = false; _ = ShowViewPageAsync(row); }
            };

            if (row != null) { row.Chip = border; row.ChipText = label; }
            return border;
        }

        // ── 页面切换 ─────────────────────────────────────────────────────────

        private void ShowConfigPage()
        {
            foreach (var row in _rows)
                row.Console?.SetForegroundActive(false);
            _viewing = null;
            _shieldReleased = false;
            _shieldOverlayWindow?.HideOverlay();
            ConfigPage.Visibility = Visibility.Visible;
            SetBusyVisible(false);
            ViewPage.Visibility = Visibility.Collapsed;
            ViewOps.Visibility = Visibility.Collapsed;
            RenderTabs();
            RebuildUserList();
        }

        private void ArmShieldForNewTab(UserRow row)
        {
            // 只有 _openedIds 首次加入才调用：这是“新标签创建”，不是重复重绘。
            _shieldReleased = false;
            _shieldPendingAfterBusy = false;
            _shieldPendingNewTab = row;
            _shieldWatchedConsole = null;
            _shieldWatchedGeneration = -1;
            _shieldWatchedHandle = IntPtr.Zero;
            ShieldTrace("new-tab", row);
        }

        private async Task ShowViewPageAsync(UserRow row)
        {
            foreach (var item in _rows)
                item.Console?.SetForegroundActive(ReferenceEquals(item, row));
            _viewing = row;
            bool newTab = _openedIds.Add(row.Account.Id); // 首次打开才创建新标签
            ConfigPage.Visibility = Visibility.Collapsed;
            ViewPage.Visibility = Visibility.Visible;
            ViewOps.Visibility = Visibility.Visible;
            SetBusyVisible(!row.Console?.IsConnected ?? true);
            RenderTabs();
            if (newTab) ArmShieldForNewTab(row);

            ViewportHost.Children.Clear();
            if (row.Console == null) InitializeConsole(row);

            // ★ 关键：RDP 控件是 WindowsFormsHost（独立 HWND），
            //   一旦加入视觉树就会立刻显示它的"启动画面"（黑底 + 转圈动画），
            //   而且这个原生子窗口会**盖住所有 WPF 元素**（含左侧导航、顶栏）。
            //   所以：未连接时先不加入，等连接成功后再挂上去，
            //   期间显示我们自己的加载提示（纯 WPF，不会盖住任何东西）。
            bool alreadyConnected = row.Console.IsConnected;
            if (alreadyConnected)
            {
                ViewportHost.Children.Add(row.Console);
                row.Console.OnShown();
            }

            row.Console.SetForegroundActive(true);
            _shieldReleased = false;
            if (!newTab) _shieldPendingAfterBusy = !alreadyConnected;
            UpdateShield();
            row.Console.EnsureInputRestored();

            if (!alreadyConnected)
            {
                var (ok, _) = await ConnectAsync(row);
                // 连接完成（无论成功失败）才挂上控件；
                // 失败时控件保持不显示，用户能看到我们的错误提示而不是黑屏。
                if (ok && ReferenceEquals(row, _viewing))
                {
                    ViewportHost.Children.Clear();
                    ViewportHost.Children.Add(row.Console);
                    row.Console.OnShown();
                    if (ReferenceEquals(_shieldPendingNewTab, row))
                    {
                        // 新标签只登记待保护，必须等真正的 RDP 画面挂载后再消费。
                        _shieldPendingNewTab = null;
                        _shieldReleased = false;
                        ShieldTrace("native-mounted", row);
                        UpdateShield();
                        _shieldOverlayWindow?.SyncToAnchor();
                    }
                    SetBusyVisible(false);
                    RefreshShieldAfterLayout(row);
                }
            }
        }

        /// <summary>
        /// 连接指定账号。返回 (是否成功, 失败原因)。
        /// ★ 返回原因是为了让自动连接能记录"为什么失败"，
        ///   否则日志只剩一个空的 "自动连接失败：A2 → "（用户反馈看不出原因）。
        /// </summary>
        private async Task<(bool ok, string reason)> ConnectAsync(UserRow row)
        {
            if (row.Console == null) return (false, "Console 未初始化");
            SetBusyVisible(true);
            _busyText!.Text = "正在连接 " + row.Account.Caption + " …";

            try
            {
                var sessions = await SessionService.QueryAsync();
                // 诊断：把查询到的会话列表打出来，便于定位"会话查不到"的根因
                Debug("查询会话：共 " + sessions.Count + " 条 · "
                      + string.Join(" | ", System.Linq.Enumerable.Select(sessions,
                          x => x.UserName + "#" + x.Id + "=" + x.State)));
                var s = SessionService.Find(sessions, row.Account.UserName);
                Debug("匹配账号「" + row.Account.UserName + "」→ "
                      + (s != null ? ("ID=" + s.Id + " 状态=" + s.State) : "未找到"));

                // ★ 会话暂未出现时**要等一会儿**，不要立刻当成"未登录"。
                //   实测：用户刚点登录，会话还没建立完（query user 里登录时间比
                //   我们查询晚 1 秒），此时拿到 sessionId=0，
                //   后续投送注入代理就会失败：无效的会话 ID：0（用户实测）。
                //   这里轮询最多 15 秒，等会话真正出现。
                if (s == null)
                {
                    _busyText!.Text = "等待用户「" + row.Account.Caption + "」的会话就绪…";
                    Debug("会话暂未出现，开始轮询等待（最多 15 秒）");
                    for (int i = 0; i < 15 && s == null; i++)
                    {
                        await Task.Delay(1000);
                        try
                        {
                            var again = await SessionService.QueryAsync();
                            s = SessionService.Find(again, row.Account.UserName);
                            if (s != null)
                                Debug("会话已出现：ID=" + s.Id + "（等待 " + (i + 1) + " 秒）");
                        }
                        catch { }
                    }
                }

                // ★ 会话不存在**不是错误**——RDP 会用它自己的登录流程新建一个会话。
                //   （HANDOFF 10.2 实测：用户注销时 Connect() 会自动登录并建出新会话）
                int sessionId = s?.Id ?? 0;

                if (s == null)
                {
                    _busyText!.Text = "用户「" + row.Account.Caption + "」当前未登录，正在尝试用保存的凭据登录…";
                    Debug("等待 15 秒后仍无会话，走 RDP 自动登录流程");
                }

                var (ok, msg) = await row.Console.ConnectAsync(row.Account, App.Config.Settings, sessionId);
                // 先在动画仍关闭不了 RDP 输入时锁定，再由 SetBusyVisible(false)
                // 在动画结束边界立即把保护层交接到真实视口。
                if (ok && ReferenceEquals(row, _viewing))
                {
                    _shieldPendingAfterBusy = true;
                    _shieldReleased = false;
                }
                SetBusyVisible(!ok);
                if (!ok) _busyText!.Text = msg;
                if (ReferenceEquals(row, _viewing)) UpdateShield();
                RenderTabs();
                return (ok, ok ? "" : (string.IsNullOrWhiteSpace(msg) ? "未知原因（ConnectAsync 未返回消息）" : msg));
            }
            catch (Exception ex)
            {
                _busyText!.Text = "连接出错：" + ex.Message;
                ShowBusyError();
                App.AppendErrorLog("ConsolePage.Connect", ex);
                return (false, "异常：" + ex.Message);
            }
        }

        private void SetBusyVisible(bool visible)
        {
            if (visible) SyncBusyOverlaySize();   // 显示前再对一次，避免用了旧尺寸
            _busyVisible = visible;
            var owner = Window.GetWindow(this);
            EnsureBusyOverlayWindow();
            bool show = visible && ViewPage.Visibility == Visibility.Visible
                && owner != null && owner.IsVisible && owner.WindowState != WindowState.Minimized;
            if (show && _busyOverlayWindow != null)
            {
                _busyOverlayWindow.ShowOverlay(ViewportHost.ActualWidth, ViewportHost.ActualHeight);
                // 动画随后显示，必须主动恢复导航的兄弟浮层层级。
                MainWindowRef?.BringNavigationToFront();
            }
            else _busyOverlayWindow?.HideOverlay();
            if (!visible)
            {
                _busyReconnectButton!.Visibility = Visibility.Collapsed;
                // 启动动画结束就是保护层接管输入的时刻。先同步请求显示，
                // 再等 Loaded 布局校准，不能等下一次切页或偶发的生命周期事件。
                if (_shieldPendingAfterBusy && _viewing != null)
                {
                    var row = _viewing;
                    _shieldPendingAfterBusy = false;
                    _shieldReleased = false;
                    RefreshShieldAfterLayout(row);
                    Debug("启动动画结束 → 立即挂保护层");
                }
                else if (_viewing != null) UpdateShield();
            }
        }

        /// <summary>
        /// 控制台页日志。与 RdpConsole.Debug 同样的分级策略：
        /// 正常状态不落盘，只记录异常/关键跃迁（详见那边的说明）。
        /// </summary>
        private static void Debug(string msg)
        {
            if (msg == null) return;
            try
            {
                if (!RdpConsole.VerboseLog && !IsImportant(msg)) return;
                System.IO.Directory.CreateDirectory(ConfigStore.ConfigDir);
                // 按日期分文件，与 RdpConsole 保持一致
                System.IO.File.AppendAllText(
                    ConfigStore.DailyLogPath("rdp-debug"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  [Console] " + msg + Environment.NewLine);
            }
            catch { }
        }

        private static bool IsImportant(string m)
        {
            if (m.Contains("失败") || m.Contains("异常") || m.Contains("拒绝")
                || m.Contains("超时") || m.Contains("错误") || m.Contains("不可用")
                || m.Contains("跳过") || m.Contains("警告") || m.Contains("不匹配")
                || m.Contains("无响应") || m.Contains("未就绪"))
                return true;
            if (m.Contains("注入代理已投送") || m.Contains("注入代理已连接")
                || m.Contains("进入自由视角") || m.Contains("退出自由视角")
                || m.Contains("真实会话号") || m.Contains("通道投送成功")
                || m.Contains("已连上") || m.Contains("连接成功")
                || m.Contains("自动连接"))
                return true;
            return false;
        }

        /// <summary>
        /// 连接失败时把遮罩变成「错误态」：允许退出、给一个重连按钮。
        /// 否则用户会卡在全黑画面上不知道发生了什么。
        /// </summary>
        private void ShowBusyError()
        {
            SetBusyVisible(true);
            _busyReconnectButton!.Visibility = Visibility.Visible;
            SetBusyVisible(true);
        }

        private async void BtnBusyReconnect_Click(object sender, RoutedEventArgs e)
        {
            _busyReconnectButton!.Visibility = Visibility.Collapsed;
            _busyText!.Text = "正在重试…";
            var row = _viewing;
            if (row == null) return;
            try { row.Console?.Disconnect(); } catch { }
            await ConnectAsync(row);
        }

        // ── 用户列表 ─────────────────────────────────────────────────────────

        private void RebuildUserList()
        {
            var accounts = App.Config.Accounts.Select((a, index) => new { a, index })
                .OrderBy(x => x.a.SortOrder <= 0 ? x.index + 1 : x.a.SortOrder).Select(x => x.a).ToList();

            // 保留已有 Console，按账号 Id 对齐
            var old = _rows.ToDictionary(r => r.Account.Id);
            _rows.Clear();
            foreach (var a in accounts)
            {
                if (old.TryGetValue(a.Id, out var r)) { r.Account = a; _rows.Add(r); }
                else _rows.Add(new UserRow { Account = a });
            }

            if (_editing != null && !_rows.Contains(_editing)) _editing = null;
            if (_viewing != null && !_rows.Contains(_viewing)) _viewing = null;

            var panel = new StackPanel();

            // ★ 第一条固定是「通用设置」卡片，样式与用户卡片一致
            panel.Children.Add(BuildSettingsRow());

            foreach (var row in _rows) panel.Children.Add(BuildRow(row));
            UserList.ItemsSource = new[] { panel };

            NoUserHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // 多选批量按钮只在有勾选时出现
            if (BtnOpenSelected != null)
            {
                BtnOpenSelected.Visibility = _checkedIds.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                if (OpenSelectedText != null)
                    OpenSelectedText.Text = "打开勾选的 " + _checkedIds.Count + " 个画面";
            }

            if (_showGeneralSettings) GeneralSettingsPanel.Visibility = Visibility.Visible;
            else if (_editing != null) LoadConfigFields(_editing);
            else ShowNoSelection();
        }

        private UIElement BuildRow(UserRow row)
        {
            bool on = ReferenceEquals(row, _editing);
            bool isChecked = _checkedIds.Contains(row.Account.Id);

            var border = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(8, 7, 8, 7),
                Margin = new Thickness(0, 0, 0, 5),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(on
                    ? Color.FromArgb(0x2E, 0x7C, 0x5C, 0xFF)
                    : Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(on
                    ? Color.FromArgb(0x77, 0x7C, 0x5C, 0xFF)
                    : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                // 去掉焦点虚线框（用户反馈）
                FocusVisualStyle = null,
                AllowDrop = true
            };

            // ★ 三列布局，各司其职：
            //   [勾选框 26px] [用户名+地址 *] [状态灯 Auto]
            //   状态灯之前和勾选框挤在同一格，看起来像"勾选框里有个点"。
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });

            // ── 第 1 列：勾选框（大一点，好点）──
            var check = new CheckBox
            {
                Width = 28,
                Height = 32,
                IsChecked = isChecked,
                Style = (Style)FindResource("GlassCheckBox"),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = isChecked ? "取消选择 " + row.Account.Caption : "选择 " + row.Account.Caption,
            };
            check.Click += (_, ev) =>
            {
                ToggleCheck(row.Account.Id);
                ev.Handled = true;
            };
            System.Windows.Automation.AutomationProperties.SetName(check, "选择 " + row.Account.Caption);
            Grid.SetColumn(check, 0);
            grid.Children.Add(check);

            // ── 第 2 列：用户名 + 地址（只显示一次）──
            var textCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textCol.Children.Add(new TextBlock
            {
                Text = row.Account.Caption,
                FontSize = 11.5,
                FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)FindResource(on ? "TextPrimary" : "TextSecondary")
            });

            string hostText = string.IsNullOrWhiteSpace(row.Account.RdpHost)
                ? "自动地址"
                : row.Account.RdpHost + ":" + (row.Account.RdpPort > 0 ? row.Account.RdpPort : 3389);
            textCol.Children.Add(new TextBlock
            {
                Text = hostText,
                FontSize = 9,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)FindResource("TextMuted")
            });
            Grid.SetColumn(textCol, 1);
            grid.Children.Add(textCol);

            // ── 第 3 列：连接状态灯（独立位置，不再占用勾选框）──
            var dot = new Border
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 2, 0),
                Background = new SolidColorBrush(row.Console?.IsConnected == true
                    ? Color.FromRgb(0x41, 0xF0, 0xC0)
                    : Color.FromArgb(0x55, 0x8B, 0x9C, 0xC8)),
                ToolTip = row.Console?.IsConnected == true ? "画面已连接" : "未连接"
            };
            Grid.SetColumn(dot, 2);
            grid.Children.Add(dot);

            var dragHandle = new Border
            {
                Width = 24,
                Height = 32,
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeAll,
                ToolTip = "拖动调整顺序",
                AllowDrop = true
            };
            System.Windows.Automation.AutomationProperties.SetName(dragHandle, "拖动排序 " + row.Account.Caption);
            dragHandle.Child = new TextBlock
            {
                Text = "⋮⋮",
                FontSize = 13,
                Foreground = (Brush)FindResource("TextMuted"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(dragHandle, 3);
            grid.Children.Add(dragHandle);
            dragHandle.PreviewMouseLeftButtonDown += (_, ev) =>
            {
                ev.Handled = true;
                try { DragDrop.DoDragDrop(dragHandle, new DataObject("SoulLinkAccount", row.Account.Id), DragDropEffects.Move); } catch { }
            };

            // 单独设置空白卡片的焦点样式；内部 CheckBox 不能替换为 null 模板。
            border.FocusVisualStyle = (Style)FindResource("NoFocusVisual");
            border.Child = grid;

            // 卡片空白区可拖动排序；单击、勾选和右键仍保持各自行为。
            Point dragStart = default;
            border.PreviewMouseLeftButtonDown += (_, ev) =>
            {
                dragStart = ev.GetPosition(border);
            };
            border.MouseMove += (_, ev) =>
            {
                if (ev.LeftButton != MouseButtonState.Pressed || ev.Handled) return;
                var delta = ev.GetPosition(border) - dragStart;
                if (Math.Abs(delta.X) < 7 && Math.Abs(delta.Y) < 7) return;
                try { DragDrop.DoDragDrop(border, row.Account.Id, DragDropEffects.Move); } catch { }
            };
            border.DragOver += (_, ev) =>
            {
                if (ev.Data.GetDataPresent("SoulLinkAccount"))
                {
                    ev.Effects = DragDropEffects.Move;
                    border.BorderBrush = (Brush)FindResource("CyanBrush");
                }
                ev.Handled = true;
            };
            border.DragLeave += (_, __) => border.BorderBrush = new SolidColorBrush(on ? Color.FromArgb(0x77, 0x7C, 0x5C, 0xFF) : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            border.Drop += (_, ev) =>
            {
                if (!ev.Data.GetDataPresent("SoulLinkAccount")) return;
                string sourceId = (string)ev.Data.GetData("SoulLinkAccount");
                int from = App.Config.Accounts.FindIndex(a => a.Id == sourceId);
                int to = App.Config.Accounts.FindIndex(a => a.Id == row.Account.Id);
                if (from < 0 || to < 0 || from == to) return;
                var moved = App.Config.Accounts[from];
                App.Config.Accounts.RemoveAt(from);
                App.Config.Accounts.Insert(to, moved);
                for (int i = 0; i < App.Config.Accounts.Count; i++) App.Config.Accounts[i].SortOrder = i + 1;
                ConfigStore.Save(App.Config);
                RebuildUserList();
                LogBus.Ok("用户顺序已保存", moved.Caption + " → 第 " + (to + 1) + " 位");
                ev.Handled = true;
            };

            // ── 行点击 ──
            border.MouseLeftButtonUp += (_, ev) =>
            {
                // Ctrl / Shift = 切换勾选
                if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0)
                {
                    ToggleCheck(row.Account.Id);
                    return;
                }

                _showGeneralSettings = false;
                GeneralSettingsPanel.Visibility = Visibility.Collapsed;
                _editing = row;
                LoadConfigFields(row);
                RebuildUserList();
            };

            border.MouseLeftButtonDown += (_, ev) =>
            {
                if (ev.ClickCount == 2)
                {
                    ev.Handled = true;
                    _ = ShowViewPageAsync(row);
                }
            };

            // ── 右键菜单（用户要求：复制 / 删除）──
            border.ContextMenu = BuildUserContextMenu(row);

            return border;
        }

        /// <summary>
        /// 用户列表第一条：通用设置。样式跟用户卡片一样，点了右侧显示设置内容。
        /// </summary>
        private bool _showGeneralSettings;
        private UIElement BuildSettingsRow()
        {
            bool on = _showGeneralSettings;

            var border = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = Cursors.Hand,
                FocusVisualStyle = (Style)FindResource("NoFocusVisual"),
                Background = new SolidColorBrush(on
                    ? Color.FromArgb(0x2E, 0x7C, 0x5C, 0xFF)
                    : Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(on
                    ? Color.FromArgb(0x77, 0x7C, 0x5C, 0xFF)
                    : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1)
            };

            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock
            {
                Text = "\uE713",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 9, 0),
                Foreground = (Brush)FindResource(on ? "TextPrimary" : "TextSecondary")
            });
            sp.Children.Add(new TextBlock
            {
                Text = "通用设置",
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = (Brush)FindResource(on ? "TextPrimary" : "TextSecondary")
            });

            border.Child = sp;
            border.MouseLeftButtonUp += (_, __) =>
            {
                ShowGeneralSettings();
            };
            return border;
        }

        /// <summary>
        /// 在右侧显示「通用设置」。
        /// 复用配置区的表单，但隐藏所有"用户级"字段，只留灵敏度与热键。
        /// </summary>
        public void ShowGeneralSettings()
        {
            _showGeneralSettings = true;
            _editing = null;
            _viewing = null;
            ConfigPage.Visibility = Visibility.Visible;
            ViewPage.Visibility = Visibility.Collapsed;
            ViewOps.Visibility = Visibility.Collapsed;
            NoSelectionHint.Visibility = Visibility.Collapsed;
            ConfigPanel.Visibility = Visibility.Collapsed;
            GeneralSettingsPanel.Visibility = Visibility.Visible;
            SetBusyVisible(false);
            RebuildUserList();
            RenderTabs();
        }

        /// <summary>切换某个用户的勾选状态（只改勾选，不触发打开画面）。</summary>
        private void ToggleCheck(string accountId)
        {
            if (_checkedIds.Contains(accountId)) _checkedIds.Remove(accountId);
            else _checkedIds.Add(accountId);
            RebuildUserList();
        }

        /// <summary>用户行的右键菜单。</summary>
        private ContextMenu BuildUserContextMenu(UserRow row)
        {
            var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };

            var miOpen = new MenuItem { Header = "打开画面" };
            miOpen.Click += (_, __) => _ = ShowViewPageAsync(row);
            menu.Items.Add(miOpen);

            var miConfig = new MenuItem { Header = "编辑配置" };
            miConfig.Click += (_, __) => { _showGeneralSettings = false; GeneralSettingsPanel.Visibility = Visibility.Collapsed; _editing = row; RebuildUserList(); };
            menu.Items.Add(miConfig);

            menu.Items.Add(new Separator());

            var miCopy = new MenuItem { Header = "复制一份" };
            miCopy.Click += (_, __) => DuplicateUser(row);
            menu.Items.Add(miCopy);

            var miCheck = new MenuItem
            {
                Header = _checkedIds.Contains(row.Account.Id) ? "取消勾选" : "勾选"
            };
            miCheck.Click += (_, __) => ToggleCheck(row.Account.Id);
            menu.Items.Add(miCheck);

            menu.Items.Add(new Separator());

            var miDel = new MenuItem { Header = "删除用户" };
            miDel.Click += (_, __) => RemoveUser(row);
            menu.Items.Add(miDel);

            return menu;
        }

        /// <summary>复制一个用户配置（便于批量建号）。</summary>
        private void DuplicateUser(UserRow row)
        {
            var src = row.Account;
            var copy = new Account
            {
                UserName = src.UserName,
                DisplayName = src.Caption + " (副本)",
                PasswordCipher = src.PasswordCipher,
                SessionId = src.SessionId,
                Note = src.Note,
                RdpHost = src.RdpHost,
                RdpPort = src.RdpPort,
                RdpUserName = src.RdpUserName,
                IconBase64 = src.IconBase64,
                RdpWidth = src.RdpWidth,
                RdpHeight = src.RdpHeight,
                RdpScaleMode = src.RdpScaleMode,
                RdpColorDepth = src.RdpColorDepth,
                RdpAutoDisconnect = src.RdpAutoDisconnect,
                RdpLowBandwidth = src.RdpLowBandwidth,
                RdpAudioMode = src.RdpAudioMode,
                RdpAudioCapture = src.RdpAudioCapture,
                RdpNetworkConnectionType = src.RdpNetworkConnectionType,
                RdpPerformanceMode = src.RdpPerformanceMode,
                RdpKeyboardHookMode = src.RdpKeyboardHookMode,
                RdpRedirectPrinters = src.RdpRedirectPrinters,
                RdpRedirectClipboard = src.RdpRedirectClipboard,
                RdpRedirectSmartCards = src.RdpRedirectSmartCards,
                RdpRedirectPorts = src.RdpRedirectPorts,
                RdpRedirectDrives = src.RdpRedirectDrives
            };

            App.Config.Accounts.Add(copy);
            ConfigStore.Save(App.Config);

            var newRow = new UserRow { Account = copy };
            _rows.Add(newRow);
            _editing = newRow;
            RebuildUserList();
            LogBus.Ok("已复制用户配置：" + copy.DisplayName);
        }

        private void ShowNoSelection()
        {
            NoSelectionHint.Visibility = Visibility.Visible;
            ConfigPanel.Visibility = Visibility.Collapsed;
            GeneralSettingsPanel.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// 用户配置页里"改了就立刻存"的控件。
        ///
        /// ★ 为什么需要：这些下拉框/复选框原来**没有绑定任何事件**，
        ///   改完不点「保存配置」就等于没改，用户反馈"设置好了下次打开又没了"。
        ///   这里统一挂上自动保存，跟通用设置页的行为保持一致。
        /// </summary>
        private void HookAutoSave()
        {
            if (_autoSaveHooked) return;
            _autoSaveHooked = true;

            foreach (var box in new[] { DepthCombo, ScaleCombo, PerformanceCombo, NetworkCombo, AudioCombo, KeyboardCombo })
                box.SelectionChanged += (_, __) => AutoSaveConfig();

            foreach (var check in new[] { AutoDiscCheck, AudioCaptureCheck, ClipboardCheck, PrintersCheck,
                                          SmartCardsCheck, PortsCheck, DrivesCheck })
            {
                check.Checked += (_, __) => AutoSaveConfig();
                check.Unchecked += (_, __) => AutoSaveConfig();
            }
        }

        private bool _autoSaveHooked;
        private bool _suppressAutoSave;
        private bool _loadingConfig;

        /// <summary>
        /// 把当前表单里的"非文本"选项写回账号并保存。
        /// 文本类字段（名字/地址/端口/分辨率）仍需点「保存配置」——避免边输边存把密码框清空。
        /// </summary>
        private void AutoSaveConfig()
        {
            if (_suppressAutoSave || _loadingConfig) return;
            var row = _editing;
            if (row == null) return;

            var a = row.Account;
            a.RdpColorDepth = TagOf(DepthCombo, a.RdpColorDepth > 0 ? a.RdpColorDepth : 32);
            a.RdpScaleMode = TagOf(ScaleCombo, a.RdpScaleMode);
            a.RdpPerformanceMode = TagOf(PerformanceCombo, a.RdpPerformanceMode);
            a.RdpNetworkConnectionType = TagOf(NetworkCombo, a.RdpNetworkConnectionType);
            a.RdpAudioMode = TagOf(AudioCombo, a.RdpAudioMode);
            a.RdpKeyboardHookMode = TagOf(KeyboardCombo, a.RdpKeyboardHookMode);

            a.RdpAutoDisconnect = AutoDiscCheck.IsChecked == true;
            a.RdpAudioCapture = AudioCaptureCheck.IsChecked == true;
            a.RdpRedirectClipboard = ClipboardCheck.IsChecked == true;
            a.RdpRedirectPrinters = PrintersCheck.IsChecked == true;
            a.RdpRedirectSmartCards = SmartCardsCheck.IsChecked == true;
            a.RdpRedirectPorts = PortsCheck.IsChecked == true;
            a.RdpRedirectDrives = DrivesCheck.IsChecked == true;

            ConfigStore.Save(App.Config);
            try { ConsoleView_ApplyToOpenConsoles(a); } catch { }
            CfgSub.Text = "已保存 · " + DateTime.Now.ToString("HH:mm:ss");
        }

        /// <summary>让已打开的画面立刻用上新参数（只对同一账号生效）。</summary>
        private void ConsoleView_ApplyToOpenConsoles(SoulLink.Models.Account account)
        {
            foreach (Window window in Application.Current.Windows)
                foreach (var console in FindConsoles(window))
                    if (console.Account?.Id == account.Id) console.ApplyGlobalSettings();
        }

        private static System.Collections.Generic.IEnumerable<RdpConsole> FindConsoles(DependencyObject root)
        {
            if (root is RdpConsole console) yield return console;
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var nested in FindConsoles(System.Windows.Media.VisualTreeHelper.GetChild(root, i)))
                    yield return nested;
        }

        private void LoadConfigFields(UserRow row)
        {
            NoSelectionHint.Visibility = Visibility.Collapsed;
            GeneralSettingsPanel.Visibility = Visibility.Collapsed;
            _showGeneralSettings = false;
            ConfigPanel.Visibility = Visibility.Visible;

            HookAutoSave();

            // 填表期间不要触发自动保存，否则会把刚读出来的值原样写回去、
            // 还可能把 CfgSub 改成"已保存"。
            _loadingConfig = true;

            var a = row.Account;
            CfgTitle.Text = a.Caption;
            CfgSub.Text = "用户 " + a.UserName;

            // 从通用设置切回用户配置时，恢复所有输入框
            NameBox.IsEnabled = true;
            HostBox.IsEnabled = true;
            PortBox.IsEnabled = true;
            UserBox.IsEnabled = true;

            NameBox.Text = a.DisplayName ?? "";
            HostBox.Text = a.RdpHost ?? "";
            PortBox.Text = (a.RdpPort > 0 ? a.RdpPort : 3389).ToString();
            UserBox.Text = a.EffectiveRdpUserName;
            WidthBox.Text = (a.RdpWidth > 0 ? a.RdpWidth : 1280).ToString();
            HeightBox.Text = (a.RdpHeight > 0 ? a.RdpHeight : 720).ToString();

            SelectByTag(DepthCombo, (a.RdpColorDepth > 0 ? a.RdpColorDepth : 32).ToString());
            SelectByTag(ScaleCombo, (a.RdpScaleMode >= 0 ? a.RdpScaleMode : 0).ToString());
            SelectByTag(PerformanceCombo, a.RdpPerformanceMode.ToString());
            SelectByTag(NetworkCombo, a.RdpNetworkConnectionType.ToString());
            SelectByTag(AudioCombo, a.RdpAudioMode.ToString());
            SelectByTag(KeyboardCombo, a.RdpKeyboardHookMode.ToString());
            AudioCaptureCheck.IsChecked = a.RdpAudioCapture;
            ClipboardCheck.IsChecked = a.RdpRedirectClipboard;
            PrintersCheck.IsChecked = a.RdpRedirectPrinters;
            SmartCardsCheck.IsChecked = a.RdpRedirectSmartCards;
            PortsCheck.IsChecked = a.RdpRedirectPorts;
            DrivesCheck.IsChecked = a.RdpRedirectDrives;

            AutoDiscCheck.IsChecked = a.RdpAutoDisconnect;
            PasswordBox.Password = "";

            _loadingConfig = false;
        }

        private static void SelectByTag(ComboBox box, string tag)
        {
            foreach (var it in box.Items.OfType<ComboBoxItem>())
                if ((it.Tag as string) == tag) { box.SelectedItem = it; return; }
            if (box.Items.Count > 0) box.SelectedIndex = 0;
        }

        private static int TagOf(ComboBox box, int fallback)
        {
            if (box.SelectedItem is ComboBoxItem it && it.Tag is string s && int.TryParse(s, out int v)) return v;
            return fallback;
        }

        // ── 配置操作 ─────────────────────────────────────────────────────────

        private void SaveConfig_Click(object sender, RoutedEventArgs e)
        {
            var row = _editing;
            if (row == null) return;
            var a = row.Account;

            string login = (UserBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(login))
            {
                CfgSub.Text = "请先填写 Windows / RDP 登录名";
                UserBox.Focus();
                return;
            }
            a.UserName = login;
            a.RdpUserName = login;
            if (PasswordBox.Password.Length > 0)
            {
                ConfigStore.SetPassword(a, PasswordBox.Password);
            }
            PasswordBox.Password = "";
            a.DisplayName = (NameBox.Text ?? "").Trim();
            a.RdpHost = (HostBox.Text ?? "").Trim();
            a.RdpPort = int.TryParse((PortBox.Text ?? "").Trim(), out int p) && p > 0 && p <= 65535 ? p : 3389;

            string u = (UserBox.Text ?? "").Trim();
            a.RdpUserName = u.Length == 0 ? a.UserName : u;

            a.RdpWidth = int.TryParse((WidthBox.Text ?? "").Trim(), out int w) && w >= 200 && w <= 8192 ? w : 1280;
            a.RdpHeight = int.TryParse((HeightBox.Text ?? "").Trim(), out int h) && h >= 200 && h <= 8192 ? h : 720;
            a.RdpColorDepth = TagOf(DepthCombo, 32);
            a.RdpScaleMode = TagOf(ScaleCombo, 0);
            a.RdpAutoDisconnect = AutoDiscCheck.IsChecked == true;
            a.RdpPerformanceMode = TagOf(PerformanceCombo, 3);
            a.RdpNetworkConnectionType = TagOf(NetworkCombo, 6);
            a.RdpAudioMode = TagOf(AudioCombo, 0);
            a.RdpKeyboardHookMode = TagOf(KeyboardCombo, 1);
            a.RdpAudioCapture = AudioCaptureCheck.IsChecked == true;
            a.RdpRedirectClipboard = ClipboardCheck.IsChecked == true;
            a.RdpRedirectPrinters = PrintersCheck.IsChecked == true;
            a.RdpRedirectSmartCards = SmartCardsCheck.IsChecked == true;
            a.RdpRedirectPorts = PortsCheck.IsChecked == true;
            a.RdpRedirectDrives = DrivesCheck.IsChecked == true;

            ConfigStore.Save(App.Config);
            RebuildUserList();
            RenderTabs();
            LogBus.Ok("已保存 " + a.Caption + " 的配置", "画面参数需要重连后生效");
        }

        /// <summary>删除一个用户配置（右键菜单与按钮共用）。</summary>
        private void RemoveUser(UserRow row)
        {
            if (!Dialogs.Confirm("确定移除用户「" + row.Account.Caption + "」吗？\n\n只删除这里的配置，不影响该用户的实际会话。", "移除用户", true)) return;

            try { row.Console?.Dispose(); } catch { }
            _openedIds.Remove(row.Account.Id);
            _checkedIds.Remove(row.Account.Id);
            App.Config.Accounts.RemoveAll(x => x.Id == row.Account.Id);
            ConfigStore.Save(App.Config);

            if (ReferenceEquals(_viewing, row))
            {
                _viewing = null;
                ViewportHost.Children.Clear();
                ShowConfigPage();
            }
            if (ReferenceEquals(_editing, row)) _editing = null;

            _rows.Remove(row);
            RebuildUserList();
            RenderTabs();
            LogBus.Ok("已删除用户配置：" + row.Account.Caption);
        }

        private void RemoveUser_Click(object sender, RoutedEventArgs e)
        {
            var row = _editing;
            if (row == null) return;
            if (!Dialogs.Confirm("要从列表移除 " + row.Account.Caption + " 吗？\n只删除配置，不影响该用户会话。", "移除用户", true)) return;

            try { row.Console?.Dispose(); } catch { }
            _openedIds.Remove(row.Account.Id);
            App.Config.Accounts.RemoveAll(x => x.Id == row.Account.Id);
            ConfigStore.Save(App.Config);
            _editing = null;
            if (ReferenceEquals(_viewing, row)) { _viewing = null; ViewportHost.Children.Clear(); ShowConfigPage(); }
            RebuildUserList();
            RenderTabs();
        }

        private void BtnOpenView_Click(object sender, RoutedEventArgs e)
        {
            if (_editing != null) _ = ShowViewPageAsync(_editing);
        }

        /// <summary>为所有勾选的用户同时打开画面（逐个接入，最后一个留在前台）。</summary>
        private async void BtnOpenSelected_Click(object sender, RoutedEventArgs e)
        {
            var targets = _rows.Where(r => _checkedIds.Contains(r.Account.Id)).ToList();
            if (targets.Count == 0) return;

            LogBus.Info("批量接入 " + targets.Count + " 个用户画面");

            // 统一走 ShowViewPageAsync：它负责挂载 RDP，并在布局完成后刷新保护层。
            // 旧路径在后台 ConnectAsync，却不把控件挂到 ViewportHost，第二个及
            // 后续用户的保护层只能等下一次切页才出现。
            foreach (var row in targets)
            {
                try { await ShowViewPageAsync(row); }
                catch (Exception ex) { LogBus.Error("接入 " + row.Account.Caption + " 失败", ex.Message); }
            }
            RenderTabs();
        }

        /// <summary>创建一个用户对应的 RdpConsole（尚未连接）。</summary>
        public static event Action? GlobalSettingsChanged;
        public static void NotifyGlobalSettingsChanged() => GlobalSettingsChanged?.Invoke();

        private void InitializeConsole(UserRow row)
        {
            GlobalSettingsChanged -= OnGlobalSettingsChanged;
            GlobalSettingsChanged += OnGlobalSettingsChanged;
            var c = new RdpConsole();
            c.InputActivity += (_, t) =>
                Dispatcher.InvokeAsync(() => { if (ReferenceEquals(row, _viewing)) InputText.Text = t; });
            c.InputModeChanged += (_, m) =>
                Dispatcher.InvokeAsync(() => { if (ReferenceEquals(row, _viewing)) ApplyModeUi(m); });
            // 光标移出画面 → 重新挂保护层
            c.ShieldRearmRequested += _ =>
                Dispatcher.InvokeAsync(() => { if (ReferenceEquals(row, _viewing)) RearmShield(); });
            // 进入自由视角 → 隐藏保护层；退出 → 复位后重新挂上
            c.ShieldVisibleRequested += (_, visible) =>
                Dispatcher.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(row, _viewing)) return;
                    if (!visible)
                    {
                        _shieldOverlayWindow?.HideOverlay();
                        return;
                    }
                    // 退出自由视角：视为"操作完毕"，重新进入需点击状态
                    _shieldReleased = false;
                    UpdateShield();
                });
            row.Console = c;
        }

        /// <summary>
        /// 新增一个用户配置条目（不是从在线会话里挑，用户明确要求过）。
        /// 新建后直接进入编辑态，填完保存即可。
        /// </summary>
        private void BtnAddUser_Click(object sender, RoutedEventArgs e)
        {
            var account = new Account
            {
                UserName = "新用户",
                DisplayName = "新用户",
                SortOrder = App.Config.Accounts.Count + 1,
                RdpHost = "127.0.0.3",
                RdpPort = 3389,
                RdpUserName = ""
            };

            App.Config.Accounts.Add(account);
            ConfigStore.Save(App.Config);

            // 重建列表并选中新条目 → 右侧展开空白配置
            var row = new UserRow { Account = account };
            _rows.Add(row);
            _editing = row;
            RebuildUserList();

            // 确保右侧是配置页（不是画面页）
            if (_viewing != null) { _viewing = null; ShowConfigPage(); }

            // 焦点放到用户名输入框
            Dispatcher.InvokeAsync(() =>
            {
                NameBox?.Focus();
                NameBox?.SelectAll();
            }, System.Windows.Threading.DispatcherPriority.Background);

            LogBus.Info("已新增用户配置，填写后点「保存配置」");
        }

        // ── 画面工具栏 ───────────────────────────────────────────────────────

        private void BtnMode_Click(object sender, RoutedEventArgs e)
        {
            var c = _viewing?.Console;
            if (c == null) return;
            var next = c.InputMode == ConsoleInputMode.FreeLook
                ? ConsoleInputMode.Pointer : ConsoleInputMode.FreeLook;
            var (ok, msg) = c.SetInputMode(next);
            if (!ok) { InputText.Text = msg; return; }
            ApplyModeUi(next);
        }

        private void ApplyModeUi(ConsoleInputMode mode)
        {
            string hk = _viewing?.Console?.ToggleHotkey ?? "Ctrl+Alt+Q";
            if (mode == ConsoleInputMode.FreeLook)
            {
                BtnMode.Style = (Style)FindResource("PrimaryButton");
                ModeLabel.Text = "自由视角中";
                InputText.Text = "自由视角 · 相对注入已接管鼠标 · " + hk + " 切回指针";
            }
            else
            {
                BtnMode.Style = (Style)FindResource("GhostButton");
                ModeLabel.Text = "自由视角";
                InputText.Text = "";
            }
        }

        private void BtnSens_Click(object sender, RoutedEventArgs e) => ShowGeneralSettings();

        /// <summary>构造/同步期间屏蔽工具栏灵敏度滑块的写回，避免把默认值覆盖掉已保存的设置。</summary>
        private bool _suppressSensSave;

        private void OnGlobalSettingsChanged()
        {
            try
            {
                HotkeyBtn.SetSpec(HotkeySpec.Parse(App.Config.Settings.LockHotkey));
                _suppressSensSave = true;
                SensSlider.Value = App.Config.Settings.LookSensitivity;
                SensText.Text = App.Config.Settings.LookSensitivity.ToString("0.0") + "x";
                if (_viewing?.Console != null) ApplyModeUi(_viewing.Console.InputMode);
            }
            finally { _suppressSensSave = false; }
        }

        private void SensSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SensText == null) return;
            double v = Math.Round(e.NewValue, 1);
            SensText.Text = v.ToString("0.0") + "x";
            if (_suppressSensSave) return;

            if (_viewing?.Console != null) _viewing.Console.LookSensitivity = v;
            try { App.Config.Settings.LookSensitivity = v; ConfigStore.Save(App.Config); } catch { }
            GlobalSettingsChanged?.Invoke();
        }

        private void OnHotkeyRecorded(HotkeySpec? spec)
        {
            if (spec == null) return;
            var c = _viewing?.Console;
            if (c == null) { InputText.Text = "先打开一个用户画面"; return; }
            App.Config.Settings.LockHotkey = spec.ToString();
            try { ConfigStore.Save(App.Config); } catch { }
            // 与通用设置相同：所有已打开控制台实时更新，而不是只改当前实例。
            foreach (Window window in Application.Current.Windows)
                ApplyHotkeyToVisualTree(window, spec.ToString());
            HotkeyBtn.SetSpec(spec);
            InputText.Text = "自由视角热键已实时生效 → " + spec.ToString();
        }

        private static void ApplyHotkeyToVisualTree(DependencyObject root, string hotkey)
        {
            if (root is RdpConsole console) console.ToggleHotkey = hotkey;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                ApplyHotkeyToVisualTree(VisualTreeHelper.GetChild(root, i), hotkey);
        }

        private async void BtnReconnect_Click(object sender, RoutedEventArgs e)
        {
            var row = _viewing;
            if (row == null) return;
            try { row.Console?.Disconnect(); } catch { }
            await ConnectAsync(row);
        }

        /// <summary>
        /// 注销用户并关闭其所有程序。
        /// ★ 这是不可恢复操作，必须二次确认，并且明确告诉用户会丢什么。
        /// </summary>
        private async void BtnLogoff_Click(object sender, RoutedEventArgs e)
        {
            var row = _viewing;
            if (row == null || _logoffInProgress) return;
            _logoffInProgress = true;
            BtnLogoff.IsEnabled = false;

            SessionInfo? s = null;
            try
            {
                var sessions = await SessionService.QueryAsync();
                s = SessionService.Find(sessions, row.Account.UserName);
            }
            catch (Exception ex)
            {
                LogBus.Error("注销前读取会话失败", ex.Message);
            }
            if (s == null)
            {
                // 没有可确认的有效会话时，注销按钮只关闭画面，不执行 logoff。
                CloseViewPage(row);
                _logoffInProgress = false;
                BtnLogoff.IsEnabled = true;
                return;
            }

            var confirm = new LogoffConfirmWindow(row.Account.Caption, row.Account.UserName, s.Id)
            {
                Owner = Window.GetWindow(this)
            };
            if (confirm.ShowDialog() != true)
            {
                _logoffInProgress = false;
                BtnLogoff.IsEnabled = true;
                return;
            }

            ViewOps.IsEnabled = false;
            LogoffTransitionWindow? transition = null;
            try
            {
                // 独立 HWND 才能盖住 RDP 空域；先淡入，再断开和销毁控件，遮住默认白底。
                transition = new LogoffTransitionWindow(ViewportHost, row.Account.Caption, () => ReferenceEquals(_viewing, row));
                await transition.FadeInAsync();

                try { row.Console?.SetInputMode(ConsoleInputMode.Pointer); } catch { }
                try { row.Console?.Disconnect(); } catch { }

                var (ok, msg) = await SessionPrepare.LogoffAsync(s.Id);
                if (!ok) throw new InvalidOperationException(msg);
                LogBus.Ok("已注销 " + row.Account.Caption, msg);

                // 过场仍盖住画面时销毁 RDP，避免露出 WindowsFormsHost 白底。
                _openedIds.Remove(row.Account.Id);
                ViewportHost.Children.Clear();
                try { row.Console?.Dispose(); } catch { }
                row.Console = null;
                RecreateShieldAfterConsoleDisposal();
                if (ReferenceEquals(_viewing, row))
                {
                    _viewing = null;
                    SetBusyVisible(false);
                    ShowConfigPage();
                }
                transition.SetCompleted(row.Account.Caption);
                await Task.Delay(220);
            }
            catch (Exception ex)
            {
                LogBus.Error("注销失败", ex.Message);
                InputText.Text = "注销失败：" + ex.Message;
            }
            finally
            {
                if (transition != null) await transition.FadeOutAndCloseAsync();
                _logoffInProgress = false;
                ViewOps.IsEnabled = true;
                BtnLogoff.IsEnabled = true;
            }

            RebuildUserList();
            RenderTabs();
        }

        private void CloseViewPage(UserRow row)
        {
            try { row.Console?.SetInputMode(ConsoleInputMode.Pointer); } catch { }
            try { row.Console?.Disconnect(); } catch { }
            SetBusyVisible(false);
            _openedIds.Remove(row.Account.Id);
            ViewportHost.Children.Clear();
            try { row.Console?.Dispose(); } catch { }
            row.Console = null;
            RecreateShieldAfterConsoleDisposal();
            if (ReferenceEquals(_viewing, row))
            {
                _viewing = null;
                ShowConfigPage();
            }
            RebuildUserList();
            RenderTabs();
        }

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            if (_viewing == null) return;
            CloseViewPage(_viewing);
        }

        /// <summary>从「在线会话」页加一个用户进来。</summary>
        public async Task AddSessionAsync(SessionInfo session)
        {
            var account = App.Config.Accounts.FirstOrDefault(x =>
                string.Equals(x.UserName, session.UserName, StringComparison.OrdinalIgnoreCase));
            if (account == null)
            {
                account = new Account
                {
                    UserName = session.UserName,
                    RdpUserName = session.UserName,
                    DisplayName = session.UserName,
                    RdpHost = session.Id > 1 && session.Id < 10 ? "127.0.0." + session.Id : "127.0.0.3"
                };
                App.Config.Accounts.Add(account);
                ConfigStore.Save(App.Config);
            }
            Activate();
            var row = _rows.FirstOrDefault(r => r.Account.Id == account.Id);
            if (row != null) { _editing = row; RebuildUserList(); }
        }

        public void RefreshBadges() { RenderTabs(); }

        public void DisposeConsoles()
        {
            foreach (var r in _rows) { try { r.Console?.Dispose(); } catch { } }
            _rows.Clear();
            ViewportHost.Children.Clear();
        }
    }
}
