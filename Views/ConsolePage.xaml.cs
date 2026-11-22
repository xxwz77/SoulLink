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

        /// <summary>最近一次"用户点击接管"的时间，用于抑制紧随其后的焦点跟随。</summary>
        private DateTime _lastTakeoverAt = DateTime.MinValue;

        /// <summary>当前是否处于全屏模式。</summary>
        private bool _isFullScreen;

        public MainWindow? MainWindowRef { get; set; }
        public event Action? AddRequested;

        public ConsolePage()
        {
            InitializeComponent();

            // 全屏热键（均可在「RDP 通用设置」里改）。
            //
            // ★★ 3.2.4：必须有两条逃生通道 ★★
            //   实测：全屏后 F11 有概率被 RDP 控件抢走（RDP 自己的键盘处理
            //   在某些时刻会先于本机 LL 钩子），用户"进去了出不来"；
            //   而顶栏热区又要求窗口激活，两条通道同时失效。
            //   现在：
            //     · FullScreenHotkey（默认 F11）——进/出全屏
            //     · ExitFullScreenHotkey（默认 Esc）——全屏时无条件退出，
            //       非全屏完全不拦截（不吞键）
            //   两者都走本机 LL 钩子（RDP 子 HWND 持焦点时 WPF 收不到键盘事件）。
            InstallFullScreenHooks();
            FullScreenHotkeysChanged -= OnFullScreenHotkeysChanged;
            FullScreenHotkeysChanged += OnFullScreenHotkeysChanged;

            // ★ 录制设置变更：勾上的账号立刻开始录，取消的停
            RecordingSettingsChanged -= OnRecordingSettingsChanged;
            RecordingSettingsChanged += OnRecordingSettingsChanged;

            // ★ 3.2.8：保存请求看门狗（独立定时器，不依赖会话监控是否在跑）
            EnsureSaveRequestWatch();

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
                        SetShieldReleased(false);
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
            ViewportHost.SizeChanged += (_, __) =>
            {
                SyncBusyOverlaySize();
                SyncShieldSize();
                // 多画面要跟着重排；单画面只是重新铺满，不会触发远端分辨率协商
                // （协商由 RdpConsole 自己的防抖队列负责）。
                OnViewportResized();
            };
            Loaded += (_, __) => { EnsureBusyOverlayWindow(); EnsureShieldOverlayWindow(); EnsureShieldWatch(); };

            // ★ 主窗口最小化时也要藏原生窗口：
            //   最小化只改窗口状态，WindowsFormsHost 的独立 HWND 不会跟着消失。
            Loaded += (_, __) =>
            {
                var win = Window.GetWindow(this);
                if (win == null) return;
                win.StateChanged += (_, __) =>
                {
                    try
                    {
                        bool minimized = win.WindowState == WindowState.Minimized;
                        if (minimized)
                        {
                            // 最小化：把原生窗口移到屏幕外（不 SW_HIDE，否则回来是黑屏）。
                            Debug("窗口最小化 → 隐藏 RDP 原生窗口");
                            foreach (var t in _tiles)
                                t.Console?.SetNativeVisible(false);
                        }
                        else
                        {
                            // ★ 还原/最大化：必须显式恢复，否则窗口永远停在屏幕外
                            //   —— 这就是"最小化到任务栏再回来，RDP 窗口消失"的原因。
                            //   走 SyncNativeVisibility 而不是逐格 SetNativeVisible(true)：
                            //   它只恢复"当前应该显示"的那些画面，不会把隐藏的格子也翻出来。
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    SyncNativeVisibility();
                                    // WPF 布局在还原后可能还没跑完，再补一次。
                                    Dispatcher.BeginInvoke(new Action(SyncNativeVisibility),
                                        System.Windows.Threading.DispatcherPriority.Loaded);
                                }
                                catch { }
                            }), System.Windows.Threading.DispatcherPriority.Background);
                        }
                    }
                    catch { }
                };
            };
            Unloaded += (_, __) =>
            {
                _shieldWatch?.Stop();
                _shieldWatch = null;
                try { _fsKeyHook?.Dispose(); _fsKeyHook = null; } catch { }
                try { _fsEscHook?.Dispose(); _fsEscHook = null; } catch { }
                try { _fsMouseToggleHook?.Dispose(); _fsMouseToggleHook = null; } catch { }
                try { _fsMouseExitHook?.Dispose(); _fsMouseExitHook = null; } catch { }
                try { FullScreenHotkeysChanged -= OnFullScreenHotkeysChanged; } catch { }
            };
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

                        // ★ 恢复必须与隐藏**完全对称**。
                        //   原来这里加了 "if (_viewing?.Console != null && IsConnected)" 的前置条件，
                        //   而隐藏那侧是无条件的 —— 只要还原时 _viewing 恰好为空
                        //   （最小化/还原、注销后重开都会出现），就永远不恢复，
                        //   表现为"画面全空，必须切页再回来才好"。
                        //   现在无条件走恢复路径，具体谁能显示由 ApplyLayout 按 IsConnected 决定。
                        SetHiddenForPageSwitch(false);
                        ApplyLayout();
                        _viewing?.Console?.OnShown();

                        // ★ 页面重新可见时必须**显式**恢复当前画面的前台资格。
                        //
                        //   原来这里只恢复了 RDP 原生窗口的显示，没管 _activeState：
                        //   切页/关弹窗后 Console 变回可见，可 _activeState 还停在 1，
                        //   于是 HotkeyArmed=False，按侧键全被拒绝
                        //   （热键日志表现为"事件到了却丢弃"）。
                        //
                        //   前台资格的唯一权威是这里：只有 ConsolePage 知道
                        //   "当前该由哪一行接管输入"（_viewing）。
                        try
                        {
                            InputTrace.Write("PAGE_VISIBLE", "页面重新可见 → 恢复当前画面前台资格："
                                + (_viewing?.Account.Caption ?? "null"));
                            _viewing?.Console?.SetForegroundActive(true);
                        }
                        catch (Exception ex) { Debug("恢复前台资格失败：" + ex.Message); }
                    }
                    else
                    {
                        // ★ 切走时必须显式隐藏 RDP 的原生 HWND（见 SetHiddenForPageSwitch）。
                        //   只改 WPF Visibility 不够：WindowsFormsHost 是独立窗口，
                        //   会继续浮在最上层盖住导航和其它页面（用户实测过）。
                        //   注意这里**不**摘控件、不 Dispose —— ActiveX 与连接全程保留。
                        Debug("页面不可见 → 隐藏 RDP 原生窗口（pageHidden）");
                        SetHiddenForPageSwitch(true);
                        _busyOverlayWindow?.HideForOwnerState();
                        SetShieldReleased(false);
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
            _busyOverlayWindow = new OwnedOverlayWindow(owner, OverlayAnchor, _busyOverlayContent, "连接动画", allowInactiveOwner: true);
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
                    + " mounted=" + (console != null && _tiles.Any(t => ReferenceEquals(t.Console, console)))
                    + " released=" + _shieldReleased
                    + " pending=" + _shieldPendingAfterBusy
                    + " busy=" + _busyVisible
                    + " view=" + ViewPage.Visibility
                    + " pageVisible=" + IsVisible
                    + " ownerActive=" + (owner?.IsActive == true)
                    + " anchor=" + ViewportHost.ActualWidth.ToString("0") + "x" + ViewportHost.ActualHeight.ToString("0")
                    + " locked=" + (console?.ShieldLocked == true)
                    + " enforced=" + (console?.ShieldEnforced == true);
                System.IO.Directory.CreateDirectory(ConfigStore.ConfigDir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(ConfigStore.ConfigDir, "shield-trace.log"), text + Environment.NewLine);
            }
            catch { }
        }

        // ── 页面生命周期：切走 / 切回 ──────────────────────────────
        //
        // ★★ 为什么需要这个：★★
        //   保护层看门狗是 150ms 定时器（每秒 6.7 次），原本只在页面
        //   Loaded 时启动、Unloaded 时停止。但本页在 SetActivePage 里
        //   只切 Visibility，**永远不会触发 Unloaded** ——
        //   于是切到「在线会话」等其他页面后，它继续以 150ms 空转，
        //   每秒六七次唤醒线程池，是空闲 CPU 的主要来源之一
        //   （实测未连任何 RDP 时本进程仍有 25% 单核）。
        //
        //   RDP 画面看不见的时候，保护层检查毫无意义，直接停掉即可。

        /// <summary>
        /// 按设置决定是否给这个账号开始录制。
        ///
        /// ★ 3.2.8：录制**由用户在 RDP 通用设置里按账号勾选**开启，
        ///   这里只是把勾选结果翻译成一条 record 命令发给目标会话的代理。
        ///   未勾选的账号完全不录。
        /// </summary>
        private async Task TryStartRecordingAsync(Account account)
        {
            try
            {
                if (account == null) return;
                var s = App.Config.Settings;
                // ★ 临时诊断：把判定过程写进日志，确认到底卡在哪一步
                LogBus.Info("录屏判定", account.Caption + " id=" + account.Id
                    + " 配置列表=[" + string.Join(",", s.RecordAccountIds ?? new System.Collections.Generic.List<string>()) + "]"
                    + " 命中=" + (s.RecordAccountIds != null && s.RecordAccountIds.Contains(account.Id)));
                if (s.RecordAccountIds == null || !s.RecordAccountIds.Contains(account.Id)) return;

                int sid = AccountStateCenter.Get(account.Id)?.SessionId ?? 0;
                if (sid <= 0)
                {
                    LogBus.Warn("录屏跳过", account.Caption + " 没有会话号");
                    return;
                }

                var relay = SoulLink.Services.RelayClient.FindBySession(sid);
                if (relay == null || !relay.IsConnected)
                {
                    LogBus.Warn("录屏跳过", account.Caption + " 注入通道未就绪（会话 " + sid + "）");
                    return;
                }

                var r = await relay.RecordStartAsync(
                    Math.Max(1, s.RecordFps),
                    Math.Max(30, s.RecordQuality),
                    Math.Max(5, s.RecordKeepSeconds)).ConfigureAwait(false);

                // ★ 用 LogBus：Debug() 会被日志过滤器丢掉（不含失败/异常等关键词），
                //   排查时看不到任何痕迹（实测踩过）。
                if (r?.Ok == true)
                    LogBus.Ok("录屏已开始", account.Caption + " · " + (r.Message ?? ""));
                else
                    LogBus.Error("录屏启动失败", account.Caption + " · " + (r?.Message ?? "无响应"));
            }
            catch (Exception ex) { LogBus.Error("录屏启动异常", ex.Message); }
        }

        /// <summary>设置里改了录制选项后，把所有已投送的账号同步一遍。</summary>
        public static void NotifyRecordingSettingsChanged() => RecordingSettingsChanged?.Invoke();
        private static event Action? RecordingSettingsChanged;

        private void OnRecordingSettingsChanged()
        {
            try
            {
                var ids = App.Config.Settings.RecordAccountIds;
                foreach (var t in _tiles)
                {
                    var acc = t.Row?.Account;
                    if (acc == null) continue;

                    bool wanted = ids != null && ids.Contains(acc.Id);
                    if (wanted)
                    {
                        _ = TryStartRecordingAsync(acc);   // 勾上 → 开始/更新参数
                    }
                    else
                    {
                        // 取消勾选 → 停止录制（释放内存缓冲）
                        _ = StopRecordingAsync(acc);
                    }
                }
            }
            catch { }
        }

        /// <summary>停止某账号的录制。</summary>
        private async Task StopRecordingAsync(Account account)
        {
            try
            {
                int sid = AccountStateCenter.Get(account.Id)?.SessionId ?? 0;
                if (sid <= 0) return;
                var relay = SoulLink.Services.RelayClient.FindBySession(sid);
                if (relay == null || !relay.IsConnected) return;
                var r = await relay.RecordStopAsync().ConfigureAwait(false);
                if (r?.Ok == true) LogBus.Info("录屏已停止", account.Caption);
            }
            catch (Exception ex) { LogBus.Error("录屏停止异常", ex.Message); }
        }

        /// <summary>
        /// 调试/测试用：如果程序目录下存在 capture\save-request.txt，
        /// 就把所有勾选账号的录制缓冲立即保存到磁盘，然后删除该文件。
        ///
        /// ★ 为什么需要它：保存操作必须经由 RelayClient（进程内注册表），
        ///   外部进程拿不到那个静态表，所以只能让主程序自己执行。
        ///   现在没有 UI 的"保存"按钮，先用这个文件触发来验证录制内容。
        /// </summary>
        private void PollSaveRequest()
        {
            try
            {
                string root = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? ".", "capture");
                string req = System.IO.Path.Combine(root, "save-request.txt");
                if (!System.IO.File.Exists(req)) return;
                LogBus.Error("PollSaveRequest 命中", "文件=" + req);   // 用 Error 级别确保不被打断
                try { System.IO.File.Delete(req); } catch { }

                LogBus.Info("收到保存请求", "开始保存已勾选账号的录制缓冲");

                // ★ 不能遍历 _tiles：那只是"打开过画面"的账号，
                //   后台投送的账号（只建通道不建画面）不在里面，会漏掉。
                //   直接按配置里的勾选列表驱动。
                var ids = App.Config.Settings.RecordAccountIds ?? new System.Collections.Generic.List<string>();
                LogBus.Info("保存范围", "共 " + ids.Count + " 个账号");
                foreach (string id in ids)
                {
                    var acc = App.Config.Accounts.FirstOrDefault(a => a.Id == id);
                    if (acc == null) { LogBus.Warn("保存跳过", "账号不存在：" + id); continue; }
                    _ = SaveRecordingAsync(acc);
                }
            }
            catch { }
        }

        /// <summary>把某账号的内存缓冲保存到磁盘。</summary>
        private async Task SaveRecordingAsync(Account account)
        {
            try
            {
                int sid = AccountStateCenter.Get(account.Id)?.SessionId ?? 0;
                if (sid <= 0) { LogBus.Warn("保存失败", account.Caption + " 无会话"); return; }
                var relay = SoulLink.Services.RelayClient.FindBySession(sid);
                if (relay == null || !relay.IsConnected) { LogBus.Warn("保存失败", account.Caption + " 通道未就绪"); return; }

                // 目录：录制回放\<用户名-昵称>\<时间-手动>
                string tag = string.IsNullOrWhiteSpace(account.DisplayName)
                    ? account.UserName
                    : account.UserName + "-" + account.DisplayName;
                var r = await relay.SaveRecordingAsync(tag, "手动", 30, 0).ConfigureAwait(false);
                if (r?.Ok == true)
                    LogBus.Ok("缓冲已保存", account.Caption + " · " + r.Message);
                else
                    LogBus.Error("保存失败", account.Caption + " · " + (r?.Message ?? "无响应"));
            }
            catch (Exception ex) { LogBus.Error("保存异常", ex.Message); }
        }

        /// <summary>
        /// 调试/测试用：如果程序目录下存在 capture\save-request.txt，
        /// 就把所有勾选账号的录制缓冲立即保存到磁盘，然后删除该文件。
        ///
        /// ★ 为什么需要它：保存操作必须经由 RelayClient（进程内静态注册表），
        ///   外部进程拿不到，所以只能让主程序自己执行。
        /// ★ 用独立定时器驱动，不挂在会话监控上 ——
        ///   会话监控在投送完成后可能停摆，导致钩子不触发（实测踩过）。
        /// </summary>
        private System.Windows.Threading.DispatcherTimer? _saveReqWatch;

        private void EnsureSaveRequestWatch()
        {
            if (_saveReqWatch != null) return;
            _saveReqWatch = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromSeconds(2) };
            _saveReqWatch.Tick += (_, __) => PollSaveRequest();
            _saveReqWatch.Start();
        }

        /// <summary>切到本页：恢复看门狗。</summary>
        public void OnPageActivated()
        {
            try
            {
                EnsureShieldWatch();
                if (_shieldWatch != null && !_isFullScreen && IsVisible) _shieldWatch.Start();
            }
            catch { }
        }

        /// <summary>切走本页：暂停看门狗（画面不可见时检查没有意义）。</summary>
        public void OnPageDeactivated()
        {
            try
            {
                _shieldWatch?.Stop();
                // 全屏菜单定时器同理：只有本页可见时才需要
                EnsureFullScreenMenuWatch(false);
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
                        return;
                    }

                    bool connected = console.IsConnected;
                    IntPtr nativeHandle = console.NativeViewportHandle;
                    bool nativeReady = nativeHandle != IntPtr.Zero
                        && _tiles.Any(t => ReferenceEquals(t.Console, console) && t.Chrome.Visibility == Visibility.Visible)
                        && console.IsVisible;
                    bool canReceiveInput = connected || nativeReady;
                    if (canReceiveInput && (!ReferenceEquals(_shieldWatchedConsole, console)
                        || _shieldWatchedGeneration != console.ConnectionGeneration
                        || _shieldWatchedHandle != nativeHandle))
                    {
                        // ★★ 判断顺序很关键：先比对，再更新快照 ★★
                        bool hadIdentity = ReferenceEquals(_shieldWatchedConsole, console);
                        bool generationChanged = _shieldWatchedGeneration != console.ConnectionGeneration;
                        bool handleChanged = _shieldWatchedHandle != nativeHandle;

                        _shieldWatchedConsole = console;
                        _shieldWatchedGeneration = console.ConnectionGeneration;
                        _shieldWatchedHandle = nativeHandle;

                        // ★★ 用户已接管时，单纯的 A/B 切换画面不该重新上锁 ★★
                        //
                        //   事故（用户实测）：解锁后点 B 切焦点，立刻又被锁回。
                        //   原因：这里的"身份"用 _viewing?.Console 判定，
                        //   而切焦点必然改 _viewing → 身份"变了" → 无条件重新上锁。
                        //
                        //   真正需要重新保护的只有两种情况：
                        //     · 重连（ConnectionGeneration 变）
                        //     · 原生句柄变了（ActiveX 重建）
                        //   单纯换焦点不算，用户已经在操作了。
                        bool mustReprotect = !hadIdentity || generationChanged || handleChanged;

                        if (mustReprotect && !_shieldReleased)
                        {
                            SetShieldReleased(false);
                            console.ReleasePointerForRearm();
                            Debug(hadIdentity ? "检测到 RDP 原生画面变化 → 立即重新保护" : "检测到当前 RDP 原生画面 → 自动保护");
                            UpdateShield();
                        }
                    }

                    // ★ 自愈：确认"该锁的时候宿主窗口真的被禁用了"。
                    //   保护层是"拦截"能力，失效必须能自愈，不能指望某次回调。
                    console.EnsureShieldEnforced();

                    // 鼠标离开画布 / 窗口失活 → 重新上锁（全屏时不锁）。
                    if (!_isFullScreen && _shieldReleased && ShouldRelockByPointer(Window.GetWindow(this)))
                    {
                        Debug("鼠标离开画布或窗口失活 → 重新上锁");
                        RearmShield(force: true);
                    }

                    // 只有解锁态才跟随 RDP 焦点（锁定态焦点会残留原处）。
                    if (_shieldReleased && !IsShieldLockedNow()) FollowRdpFocus();

                    // 持续满足保护条件就保持锁定，不依赖连接/动画的某次回调。
                    if (!_shieldReleased && !console.ShieldLocked)
                    {
                        UpdateShield();
                        if ((DateTime.UtcNow - _lastShieldRecoveryLog).TotalSeconds > 5)
                        {
                            _lastShieldRecoveryLog = DateTime.UtcNow;
                            Debug("保护层看门狗恢复锁定（无需切页）");
                        }
                    }
                    RefreshInputIndicator();
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
            SetShieldReleased(false);
            // 保护层没有 HWND 了，注销时只需解掉禁用状态，
            // 否则新会话的宿主窗口会一上来就是禁用的。
            try { _viewing?.Console?.UnlockShield("注销后复位"); } catch { }
            EnsureShieldWatch();
        }

        // ★ 保护层不再有原生浮层窗口。
        //
        //   历史：半透明浮层 = 分层窗口(WS_EX_LAYERED)，压到 mstscax 的
        //   DirectComposition 表面上会把那块区域踢出硬件合成 → 整块黑屏。
        //   现在改为禁用 RDP 宿主窗口（见 RdpConsole.LockShield），
        //   不新建任何窗口，也就不会再黑屏，也不碰 0x119 的高风险组合。
        private void EnsureShieldOverlayWindow()
        {
            // 保留方法名是为了不动其它调用点；这里不再创建任何东西。
            UpdateShield();
        }

        /// <summary>
        /// 键鼠接管指示灯。
        /// ★ 只反映"当前是否已接管"，不再弹一次性文字：
        ///   原来用 TextBlock 显示消息，会被后续状态直接覆盖，反而看不出当前状态。
        /// </summary>
        private void UpdateInputIndicator(bool taken, bool locked = false)
        {
            try
            {
                // 三态：
                //   已接管（taken）      → 亮青
                //   锁定中（locked）     → 琥珀『点击画面接管』
                //   未连接/不可用        → 灰『未接管』
                if (InputLabel != null)
                    InputLabel.Text = taken ? "已接管" : (locked ? "点击画面接管" : "未接管");
                if (InputDot != null)
                    InputDot.Fill = new SolidColorBrush(taken
                        ? Color.FromRgb(0x41, 0xF0, 0xC0)   // 亮青 = 已接管
                        : (locked
                            ? Color.FromRgb(0xF0, 0xC0, 0x41) // 琥珀 = 等待点击接管
                            : Color.FromRgb(0x6B, 0x7C, 0x9C))); // 灰 = 未接管
                if (InputIndicator != null)
                {
                    InputIndicator.Background = new SolidColorBrush(taken
                        ? Color.FromArgb(0x2E, 0x41, 0xF0, 0xC0)
                        : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
                    InputIndicator.BorderBrush = new SolidColorBrush(taken
                        ? Color.FromArgb(0x77, 0x41, 0xF0, 0xC0)
                        : Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
                }
            }
            catch { }
        }

        /// <summary>
        /// 刷新键鼠接管指示灯。
        /// ★ 单独入口，不依赖 UpdateShield 是否被调用：
        ///   原来只在 UpdateShield 里更新，而它有多条提前 return 的路径，
        ///   导致指示灯亮起后再也不熄灭（用户实测）。
        /// </summary>
        private void RefreshInputIndicator()
        {
            try
            {
                bool connected = _viewing?.Console?.IsConnected == true;
                var win = Window.GetWindow(this);
                bool pageVisible = win != null && win.IsVisible
                                   && win.WindowState != WindowState.Minimized
                                   && ViewPage.Visibility == Visibility.Visible
                                   && ConfigPage.Visibility != Visibility.Visible;

                // ★ 读**真实**锁定态，不再自己按 _shieldReleased 推一遍。
                //
                //   原实现用 needShield = ... && !_shieldReleased 反推，
                //   于是锁定态永远显示"未接管"，用户根本看不出保护层在工作
                //   （实测：解锁后也一直显示未接管）。
                //   唯一真源是 console.ShieldLocked —— 宿主窗口是否真被禁用。
                var console = _viewing?.Console;
                bool locked = console?.ShieldLocked == true;
                bool armed = locked && !_shieldReleased;

                UpdateInputIndicator(connected && pageVisible && !armed, armed);
            }
            catch { }
        }

        /// <summary>
        /// 修改保护层锁存状态的唯一入口。
        /// ★ 所有"重新上锁 / 解锁"都必须走这里，否则指示灯会和实际状态脱节。
        /// </summary>
        private void SetShieldReleased(bool released,
            [System.Runtime.CompilerServices.CallerMemberName] string from = "")
        {
            if (RdpConsole.VerboseLog && _shieldReleased != released)
                Debug("锁存 " + (_shieldReleased ? "已接管" : "锁定") + " → "
                      + (released ? "已接管" : "锁定") + "  caller=" + from);
            _shieldReleased = released;
            RefreshInputIndicator();
        }

        /// <summary>保护层是否已被用户点掉（离开视口或切页后重置）。</summary>
        private bool _shieldReleased;

        /// <summary>
        /// 同步保护层显示状态。
        /// 条件：勾选「点击画面才捕获」+ 已连接 + 非自由视角 + 未点掉 + 主窗口在前台。
        /// 真实 Win32 Owner 保证浮层不会越过主窗口；失活时同步隐藏并重新锁定。
        /// </summary>
        /// <summary>
        /// 同步保护层状态：锁定全部可见画面（禁用其 RDP 宿主窗口）。
        ///
        /// ★ 保护层现在是"禁用 RDP 原生宿主窗口"，不是半透明浮层：
        ///   浮层是分层窗口，压在 mstscax 的 DirectComposition 表面上会让
        ///   那块区域退出硬件合成 → 整块黑（2026-11-19 定论）。
        /// ★ 全屏时不启用保护层（用户全屏就是为了打游戏）。
        /// </summary>
        private void UpdateShield()
        {
            try
            {
                var console = _viewing?.Console;
                bool connected = console?.IsConnected == true;
                var win = Window.GetWindow(this);
                bool visible = win != null && win.IsVisible && win.WindowState != WindowState.Minimized;

                // 需要锁定 = 勾选了点击捕获 + 有画面 + 非自由视角 + 用户未接管 + 页面可见 + 非全屏
                bool want = !_isFullScreen
                            && App.Config.Settings.RdpCaptureOnClick
                            && console != null
                            && !console.IsLooking
                            && !_shieldReleased
                            && ViewPage.Visibility == Visibility.Visible
                            && ConfigPage.Visibility != Visibility.Visible
                            && visible;

                UpdateInputIndicator(connected && !want);
                ShieldTrace(want ? "lock" : "unlock", _viewing);

                // ★★ 锁**全部**可见画面，不能只锁焦点那个 ★★
                //   保护层的语义是"整个画面区都还没接管"。
                ApplyShieldToAllTiles(want);
            }
            catch (Exception ex) { App.AppendErrorLog("UpdateShield", ex); }
        }

        /// <summary>
        /// 把锁定态应用到所有应当被保护的画面。
        ///
        /// ★ 只处理"当前真正显示着 RDP"的格子：未连接/折叠的没有可交互画面，
        ///   锁它没有意义，还会在它们连上时留下"一上来就被禁用"的怪状态。
        /// </summary>
        private void ApplyShieldToAllTiles(bool want)
        {
            foreach (var t in _tiles)
            {
                var c = t.Console;
                if (c == null) continue;

                bool reachable = t.Chrome != null
                                 && t.Chrome.Visibility == Visibility.Visible
                                 && c.IsConnected;
                try
                {
                    if (want && reachable)
                    {
                        if (!c.ShieldLocked) c.LockShield("UpdateShield");
                        else c.EnsureShieldEnforced();
                    }
                    else if (c.ShieldLocked)
                    {
                        c.UnlockShield("UpdateShield");
                    }
                }
                catch (Exception ex) { App.AppendErrorLog("ApplyShieldToAllTiles", ex); }
            }

            // ★ 锁定态变化后必须重画边框，否则保护层框不会出现/消失。
            //   原来这里没有这一步，用户看到的是"锁着但没有框"。
            try { UpdateTileChrome(); } catch { }
        }

        /// <summary>当前是否处于锁定态（任一格子锁着即算）。</summary>
        private bool IsShieldLockedNow()
        {
            try { return _tiles.Any(t => t.Console?.ShieldLocked == true); }
            catch { return false; }
        }

        /// <summary>
        /// 解锁态下，把焦点切到"真正持有键盘焦点的那个 RDP 画面"。
        ///
        /// ★ 解锁后宿主窗口恢复启用，点击会被 RDP 吞掉，WPF 收不到 MouseLeftButtonUp，
        ///   只能靠系统焦点状态反推用户点了哪一格。
        /// </summary>
        private void FollowRdpFocus()
        {
            try
            {
                if (CurrentLayoutMode == ConsoleLayoutMode.Single) return;
                // 用户刚点完，那一拍系统焦点可能还没沉降，跟随会把焦点拉回去。
                if (DateTime.UtcNow - _lastTakeoverAt < TimeSpan.FromMilliseconds(600)) return;

                Tile? owner = null;
                foreach (var t in _tiles)
                {
                    if (t.Console?.HasKeyboardFocus == true) { owner = t; break; }
                }
                if (owner == null) return;
                if (ReferenceEquals(owner, _focusTile)) return;

                SetFocusFromShield(owner);
                Debug("跟随 RDP 焦点 → " + owner.Row.Account.Caption);
            }
            catch { }
        }

        /// <summary>
        /// 解锁态下是否该重新上锁：鼠标移出画布，或主窗口不在前台。
        /// ★ 用画布矩形而不是单格：多画面在格子间移动不该重新上锁。
        /// </summary>
        private bool ShouldRelockByPointer(Window? win)
        {
            try
            {
                if (win == null) return true;
                if (!win.IsActive) return true;
                if (win.WindowState == WindowState.Minimized) return true;

                var rect = CanvasScreenRect();
                if (rect == null) return true;

                var cursor = System.Windows.Forms.Cursor.Position;
                bool inside = cursor.X >= rect.Value.left && cursor.X < rect.Value.left + rect.Value.width
                           && cursor.Y >= rect.Value.top && cursor.Y < rect.Value.top + rect.Value.height;
                return !inside;
            }
            catch { return false; }
        }


        private void SyncShieldSize()
        {
            // 保护层没有浮层窗口了，不需要同步几何；保留空方法避免动调用点。
        }

        /// <summary>当前鼠标位置落在哪个格子（多画面用）。</summary>
        private Tile? HitTestTileUnderMouse()
        {
            try
            {
                var p = System.Windows.Forms.Control.MousePosition;
                foreach (var t in _tiles)
                {
                    if (t.Chrome.Visibility != Visibility.Visible) continue;
                    if (t.Chrome.ActualWidth < 1 || t.Chrome.ActualHeight < 1) continue;
                    var tl = t.Chrome.PointToScreen(new Point(0, 0));
                    var br = t.Chrome.PointToScreen(new Point(t.Chrome.ActualWidth, t.Chrome.ActualHeight));
                    if (p.X >= tl.X && p.X <= br.X && p.Y >= tl.Y && p.Y <= br.Y) return t;
                }
            }
            catch { }
            return null;
        }

        /// <summary>保护层里选中焦点：只切焦点、不改布局、不重新上锁。</summary>
        private void SetFocusFromShield(Tile tile)
        {
            if (ReferenceEquals(_focusTile, tile)) return;
            _focusTile = tile;
            _viewing = tile.Row;
            foreach (var t in _tiles)
                t.Console.SetForegroundActive(ReferenceEquals(t, tile));
            UpdateTileChrome();
            RenderTabs();
            Debug("保护层点击 → 焦点切到 " + tile.Row.Account.Caption);
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

            // ★ 多画面时，这一下点击既要"解锁"，也要"选中点到的那个画面"。
            //   否则用户点第二格，焦点还停在第一格，键鼠会投错账号。
            //   这次点击已被保护层完整消费，不会透传到远端 RDP。
            if (CurrentLayoutMode != ConsoleLayoutMode.Single)
            {
                var hit = HitTestTileUnderMouse();
                if (hit != null) SetFocusFromShield(hit);
            }

            SetShieldReleased(true);
            try { _viewing?.Console?.OnShieldReleased(); } catch { }
            UpdateInputIndicator(true);
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
                if (!_tiles.Any(t => ReferenceEquals(t.Row, row))) return;
                UpdateShield();
                SyncShieldSize();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>光标离开视口、失活或切页后统一回到锁定态。</summary>
        private void RearmShield(bool force = false)
        {
            if (!force && !_shieldReleased) return;
            SetShieldReleased(false);
            UpdateShield();
        }

        public void Activate()
        {
            RebuildUserList();
            RenderTabs();
            _initialized = true;
            UpdateLayoutUi();
            ApplyLayout();
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

            // ★ Broker 服务被安装 / 更新后，立刻重置投送状态并重试。
            //
            //   用户实测：「安装服务后没有自动更新并启用投送」。
            //   原因：安装器把服务换成新版后，中控台完全不知道；
            //   那些账号早被记进 _deployHandled（之前投送"成功"过），
            //   于是每轮扫描都跳过，必须重启程序才会重投。
            BrokerProtocol.ServiceUpdated += OnBrokerServiceUpdated;
        }

        /// <summary>Broker 服务更新后：清投送记录 + 清版本缓存 + 立刻重扫。</summary>
        private void OnBrokerServiceUpdated()
        {
            try
            {
                if (Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        Debug("Broker 服务已更新 → 重置投送状态并重新投送");

                        // ① 清掉"已投送"记录与稳定期计时，让每个账号重新走一遍投送
                        _deployHandled.Clear();
                        _sessionSeen.Clear();

                        // ② 让旧代理连接失效（它们指向的是旧服务的登记表）
                        //
                        //   ★★ 这里**绝对不能**再调 SetForegroundActive(false)。★★
                        //
                        //   实测（input-trace，2026-11-20 03:12）：
                        //     03:12:16 broker STOPPED（用户更新了服务）
                        //     03:12:23 SET_ACTIVE active=False 调用方=<OnBrokerServiceUpdated>b__90_0
                        //     03:12:38 HOTKEY_DENY 原因=HotkeyArmed=False IsConnected=True
                        //   之后每次按侧键都被拒，直到用户手动切了一次画面
                        //   （03:19:19 ARMED False → True 由 ShowViewPageAsync 恢复）才好。
                        //
                        //   原因：SetForegroundActive 是**双职责**的 ——
                        //   它既停键鼠注入（_activeState），又会关掉 HotkeyArmed、
                        //   释放捕获、卸载钩子。而这里真正想要的只是"旧连接失效"，
                        //   前台资格与代理连接无关，不该被牵连。
                        //
                        //   改用 StopInputAsync()：它只处置通道、递增 generation，
                        //   完全不碰 _activeState / HotkeyArmed / 捕获状态。
                        foreach (var t in _tiles)
                        {
                            try { t.Console?.InvalidateAgentConnection(); } catch { }
                        }

                        // ③ 立刻扫一次，不等下一个 2 秒 tick
                        await ScanAndDeployAsync();
                    }
                    catch (Exception ex) { App.AppendErrorLog("OnBrokerServiceUpdated", ex); }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch { }
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
                    // ★ 从 2 秒放宽到 5 秒。
                    //   每跳都要 query user + 枚举命名管道；后者在第三方软件
                    //   泄漏管道时（实测 2.9 万个）单次就要几百毫秒，
                    //   2 秒一跳会把主线程占满（实测 166% 单核、界面卡死）。
                    //   配合 3 秒稳定期，会话出现后约 8 秒投送出通道，
                    //   对挂机场景无感。
                    Interval = TimeSpan.FromSeconds(5)
                };
                int tickCount = 0;
                _sessionWatch.Tick += async (_, __) =>
                {
                    tickCount++;
                    try
                    {
                        await ScanAndDeployAsync();
                        PollSaveRequest();      // ★ 3.2.8 调试钩子：capture\save-request.txt
                        if (tickCount % 6 == 1) Debug("会话监控 tick#" + tickCount + " 完成");
                    }
                    catch (Exception ex) { Debug("会话监控 tick#" + tickCount + " 异常：" + ex.Message); }
                };
                _sessionWatch.Start();
                Debug("会话监控已启动（每 5 秒扫描，命中后等 " +
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

        /// <summary>手动模式提示只记一次，避免每轮扫描刷日志。</summary>
        private bool _manualModeNoticeShown;

        /// <summary>上次"死代理检测"的时刻（节流用，避免每 2 秒枚举全进程）。</summary>
        private DateTime _lastDeadAgentCheck = DateTime.MinValue;

        private async Task ScanAndDeployAsync()
        {
            try
            {
                // ★★ 手动代理模式下绝不自动投送。★★
                //
                //   用户实测：「关闭手动后好像没恢复自动」。
                //   反过来查才发现这条路径**从来没有检查过这个开关** ——
                //   ManualAgentMode.Enabled 只在 App 启动时用过一次，
                //   所以手动模式下照样会自动投送，把用户的手动代理顶掉。
                //
                //   注意：这里直接 return 不等于"什么都不做"，
                //   手动模式自己的等待逻辑在 ManualAgentMode 里，与本方法互不影响。
                if (ManualAgentMode.Enabled)
                {
                    if (!_manualModeNoticeShown)
                    {
                        _manualModeNoticeShown = true;
                        Debug("手动代理模式已开启 → 跳过自动投送");

                        // ★ 关键：进入手动模式时清空"已处理"记录。
                        //   否则手动期间代理被停掉，切回自动后这些账号仍在
                        //   _deployHandled 里，会被永远跳过 ——
                        //   表现为「切回自动后完全没有投送日志」（用户实测）。
                        //   同时清掉稳定期计时，让切回自动后重新走"等满稳定期"的流程。
                        _deployHandled.Clear();
                        _sessionSeen.Clear();
                        Debug("手动代理模式：已清空投送记录，切回自动后会重新投送");
                    }
                    return;
                }

                if (_manualModeNoticeShown)
                {
                    // 刚从手动模式切回自动：再清一次，确保没有残留
                    _manualModeNoticeShown = false;
                    _deployHandled.Clear();
                    _sessionSeen.Clear();
                    Debug("已退出手动代理模式 → 清空投送记录，恢复自动投送");
                }

                // ★★ 已投送但代理已经死了 → 必须重投。★★
                //
                //   用户实测：02:10:28 两条"通道投送成功"，之后代理进程消失、
                //   relay 管道也没了，但日志再没有任何投送记录 ——
                //   因为 _deployHandled 里记着"已处理"，监控从此永久跳过。
                //
                //   标记只代表"曾经发起过"，不代表"现在还有效"。
                //   这里每轮扫一遍仍标记为已处理的账号，确认代理还活着；
                //   死了就把标记清掉，让它重新走投送流程。
                // ★ 节流：死代理检测最多每 10 秒做一次。
                //   DiscoverLiveAgentSessions 要枚举全部进程 + 全部命名管道，
                //   每 2 秒跑一次会明显占用 UI 线程（实测出现过日志停摆）。
                bool checkDead = _deployHandled.Count > 0
                    && (DateTime.UtcNow - _lastDeadAgentCheck).TotalSeconds >= 10;
                if (checkDead)
                {
                    _lastDeadAgentCheck = DateTime.UtcNow;
                    var live = BgiLogWatcher.DiscoverLiveAgentSessions();

                    // ★★ 会话列表只查**一次**。★★
                    //   原来在 foreach 里对每个已处理账号各调一次 QueryAsync，
                    //   而 QueryAsync 每次都起一个 query.exe 子进程（~28ms），
                    //   本方法又每 2 秒被调用一次 → UI 线程被持续占满，
                    //   表现为日志停摆、界面卡顿（实测：单线程烧了 3 分钟 CPU）。
                    var sessionsForCheck = await SessionService.QueryAsync().ConfigureAwait(true);

                    var dead = new List<string>();
                    foreach (var handledId in _deployHandled)
                    {
                        // 找到该账号当前会话，判断该会话还有没有活代理
                        UserRow? hit = null;
                        foreach (var r0 in _rows)
                            if (string.Equals(r0.Account.Id, handledId, StringComparison.Ordinal)) { hit = r0; break; }
                        if (hit == null) continue;

                        var s0 = SessionService.Find(sessionsForCheck, hit.Account.UserName);
                        if (s0 == null || s0.Id <= 0) continue;      // 没会话，交给下面的逻辑处理
                        if (!live.Contains(s0.Id)) dead.Add(handledId);
                    }

                    if (dead.Count > 0)
                    {
                        foreach (var d in dead)
                        {
                            _deployHandled.Remove(d);
                            _sessionSeen.Remove(d);
                        }
                        Debug("检测到 " + dead.Count + " 个账号的注入代理已失效 → 清除标记，准备重投");
                    }
                }

                var ids = App.Config.Settings.AutoConnectAccountIds;
                if (ids == null || ids.Count == 0) return;

                // ★ 先查会话，用最新状态决定"要不要继续盯着"。
                //   原来在查会话之前就用 _deployHandled 过滤掉了已投送的账号，
                //   导致"A2 连上过一次、之后掉线"时监控直接跳过它，
                //   永远不再重连（用户实测：A3 拉起成功、A2 一直 Disc）。
                var sessionsNow = await SessionService.QueryAsync().ConfigureAwait(true);

                // ★★ 只看本进程的通道，不再枚举系统命名管道。★★
                //
                //   原来的 DiscoverChannelSessions() 遍历系统全部管道（实测 3.5 万个，
                //   单次 6.5 秒），目的是"任一版本已为该会话投送过就不重复投送"。
                //
                //   但投送目标全部来自本进程的 AutoConnectAccountIds 配置，
                //   根本不会出现"别的版本管了同一个会话"的情况（用户已确认）。
                //   而这个"活着"的判断自己的注册表就够 —— RelayClient 在
                //   投送成功时 Register、断开时 Unregister，O(1) 就能答。
                var existingChannels = BgiLogWatcher.DiscoverLiveAgentSessions();

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

                    // 任一版本已为该会话投送过，就不重复创建代理。
                    if (cur != null && cur.Id > 0 && existingChannels.Contains(cur.Id))
                    { _deployHandled.Add(id); continue; }
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

                                        // ★★ 3.2.8：这是**真正的自动连接路径**（会话监控拉起）。
                                        //   另一套 AutoConnectAsync（约 1350 行）是旧实现，
                                        //   实际不会走到 —— 之前把录制触发加在那里，所以一直没生效。
                                        //   通道建好后按设置决定是否开始录制。
                                        await TryStartRecordingAsync(row.Account).ConfigureAwait(false);
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
                            InputTrace.Write("SCAN_SETACTIVE", "监控投送前置为后台：" + caption
                                + " 会话=" + row.Console?.CurrentSessionId
                                + " 当前_viewing=" + (_viewing?.Account.Caption ?? "null")
                                + " 是否同一行=" + ReferenceEquals(row, _viewing));
                            // ★ 同上：只降级"不是当前画面"的那个。
                            if (!ReferenceEquals(row, _viewing))
                                row.Console?.SetForegroundActive(false);

                            var console = await Dispatcher.InvokeAsync(() => row.Console);
                            if (console == null) return;

                            bool ok = await console.StartInputBySessionAsync().ConfigureAwait(false);
                            Debug(ok ? "会话监控：通道投送成功 " + caption
                                     : "会话监控：通道投送跳过 " + caption);

                            // ★★ 3.2.8：这是**会话监控自动投送**的主路径。
                            //   启动、重连、代理重投全走这里，是录制触发最该挂的位置。
                            if (ok) await TryStartRecordingAsync(row.Account).ConfigureAwait(false);

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

                // ★ 3.2.8：第一个账号走的是"打开画面"路径，不经过下面的投送循环，
                //   所以录制要在这里单独触发一次，否则勾选第一个账号永远不录。
                _ = Task.Run(async () =>
                {
                    await Task.Delay(4000).ConfigureAwait(false);   // 等通道建好
                    await TryStartRecordingAsync(snapshot[0].row.Account).ConfigureAwait(false);
                });

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
                            InputTrace.Write("BG_DEPLOY_SETACTIVE", "后台投送前置为后台：" + caption
                                + " 会话=" + row.Console?.CurrentSessionId
                                + " 当前_viewing=" + (_viewing?.Account.Caption ?? "null")
                                + " 是否同一行=" + ReferenceEquals(row, _viewing));
                            // ★ 只给"不是当前画面"的那个降级。
                            //   原来是无条件 false —— 万一这里遍历到的正是 _viewing，
                            //   就会把用户正在看的画面降成后台，热键随即失效
                            //   （与 OnBrokerServiceUpdated 同一个坑）。
                            if (!ReferenceEquals(row, _viewing))
                                row.Console?.SetForegroundActive(false);
                        });

                        var console = await Dispatcher.InvokeAsync(() => row.Console);
                        if (console != null)
                        {
                            bool ok = await console.StartInputBySessionAsync().ConfigureAwait(false);
                            Debug(ok ? "通道投送成功：" + caption
                                     : "通道投送跳过：" + caption + "（无会话）");
                            // ★ 3.2.8：投送成功且该账号在设置里勾了「录制」→ 下发录制
                            if (ok) await TryStartRecordingAsync(row.Account).ConfigureAwait(false);
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

            // 只为「打开过」的用户显示页签，没打开的不占位。
            // 顺序跟随画面流式布局的顺序，保证"标签顺序 == 双列里的顺序"。
            var order = App.Config.Settings.RdpViewOrder;
            var opened = _rows.Where(r => _openedIds.Contains(r.Account.Id)).ToList();
            opened.Sort((a, b) =>
            {
                int ia = order.IndexOf(a.Account.Id);
                int ib = order.IndexOf(b.Account.Id);
                if (ia < 0) ia = int.MaxValue;
                if (ib < 0) ib = int.MaxValue;
                return ia.CompareTo(ib);
            });
            foreach (var row in opened)
                TabBar.Children.Add(MakeChip(row.Account.Caption, row));
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
                else if (CurrentLayoutMode != ConsoleLayoutMode.Single && _openedIds.Contains(row.Account.Id))
                {
                    // 多画面下点标签 = 把焦点移到那一格，布局不变
                    _showGeneralSettings = false;
                    ConfigPage.Visibility = Visibility.Collapsed;
                    ViewPage.Visibility = Visibility.Visible;
                    ViewOps.Visibility = Visibility.Visible;
                    SetFocus(row, reason: "tab-click");
                }
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
            SetShieldReleased(false);
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
            SetShieldReleased(false);
            _shieldPendingAfterBusy = false;
            _shieldPendingNewTab = row;
            _shieldWatchedConsole = null;
            _shieldWatchedGeneration = -1;
            _shieldWatchedHandle = IntPtr.Zero;
            ShieldTrace("new-tab", row);
        }

        private async Task ShowViewPageAsync(UserRow row)
        {
            InputTrace.Write("SHOW_VIEW", "打开画面：" + row.Account.Caption + " 会话=" + row.Console?.CurrentSessionId
                + " 已有Console=" + (row.Console != null));
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

            if (row.Console == null) InitializeConsole(row);

            // 焦点格子立刻固定下来：即使还没连上，布局也知道该把谁放第一位。
            _focusTile = EnsureTile(row);
            UpdateTileChrome();
            ApplyLayout();

            // ★ 这里原来在下面 alreadyConnected 分支里又调了一次 ApplyLayout()，
            //   两次测量可能读到不同的视口宽度（滚动条/布局尚未收敛），
            //   导致画面宽度在两个值之间跳一下 —— 用户看到的就是"切标签时左右晃"。
            //   已改为：需要时就地重排一次，且测量前保证布局已收敛。

            // ★ 关键：RDP 控件是 WindowsFormsHost（独立 HWND），
            //   一旦可见就会立刻显示它的"启动画面"（黑底 + 转圈动画），
            //   而且这个原生子窗口会**盖住所有 WPF 元素**（含左侧导航、顶栏）。
            //   所以：未连接时先不显示 Tile，等连接成功后再显示，
            //   期间显示我们自己的加载提示（纯 WPF，不会盖住任何东西）。
            //
            // ★ 控件本身已经在 Tile 里且**从不移除**，这里只切 Tile 可见性。
            bool alreadyConnected = row.Console.IsConnected;
            if (alreadyConnected)
            {
                // 已连接：格子只可能在上面 ApplyLayout 时才第一次可见，
                // 这里把布局更新推到布局阶段之后，避免同一拍内重复测量。
                row.Console.OnShown();

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!ReferenceEquals(row, _viewing)) return;
                    ApplyLayout();
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }

            row.Console.SetForegroundActive(true);
            SetShieldReleased(false);
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
                    EnsureTile(row);
                    ApplyLayout();
                    row.Console.OnShown();
                    if (ReferenceEquals(_shieldPendingNewTab, row))
                    {
                        // 新标签只登记待保护，必须等真正的 RDP 画面挂载后再消费。
                        _shieldPendingNewTab = null;
                        SetShieldReleased(false);
                        ShieldTrace("native-mounted", row);
                        UpdateShield();
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
                    SetShieldReleased(false);
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
                _busyOverlayWindow.ShowOverlay(OverlayAnchorWidth(), OverlayAnchorHeight());
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
                    SetShieldReleased(false);
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

        internal void RebuildUserList()
        {
            var accounts = AccountScope.Accounts(App.Config).Select((a, index) => new { a, index })
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

            RemoveTile(row);
            if (ReferenceEquals(_viewing, row))
            {
                _viewing = null;
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
            RemoveTile(row);
            if (ReferenceEquals(_viewing, row)) { _viewing = null; ShowConfigPage(); }
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
                Dispatcher.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(row, _viewing)) return;
                    // 接管状态由保护层直接驱动，这里不再用一次性文字覆盖它
                });
            c.InputModeChanged += (_, m) =>
                Dispatcher.InvokeAsync(() => { if (ReferenceEquals(row, _viewing)) ApplyModeUi(m); });
            // ★★ 用户点了这个 RDP 画面 → 当拍切焦点。★★
            //
            //   RdpConsole 在 WM_MOUSEACTIVATE 里抛 ShieldClicked，这是"点击落到画面里"
            //   的唯一可靠通知：原生窗口会吞掉鼠标消息，WPF 的格子 Border 收不到点击。
            //
            //   原代码**从未订阅过这个事件**，于是：
            //     · 开启保护层时：靠 EnterTakeover → _shieldReleased=true → FollowRdpFocus
            //       轮询补救，所以看起来正常；
            //     · 未开启保护层时：ShieldActive=false，_shieldReleased 永远是 false，
            //       FollowRdpFocus 那条补救路径根本不执行 ——
            //       表现就是「双列模式下点画面，焦点框不跟着切」（用户实测）。
            //
            //   这里直接订阅，两种状态都走同一条路径，不再依赖保护层是否开启。
            c.ShieldClicked += _ =>
                Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        // ★★ 这里**不能**用 "row 是不是 _viewing" 来过滤。★★
                        //
                        //   row 是这个 RdpConsole 所属的账号；用户点"另一个"画面时，
                        //   row 是新画面而 _viewing 还是旧的 —— 那条判断会把
                        //   "切到另一个画面"这种**最需要处理**的情况直接挡掉，
                        //   于是 _focusTile 永远不变、紫框永远不动。
                        //   这是 2.17.5 里"点画面切不了焦点"的真实根因。
                        //
                        //   正确的过滤是「本页是否可见」，而不是比对账号身份。

                        // 单画面下点击画面不需要切焦点（本来就只有它一个）
                        if (CurrentLayoutMode == ConsoleLayoutMode.Single) return;

                        // 页面不可见时（切到别的导航页）不抢焦点
                        if (!IsVisible) return;

                        // ★ 锁定态点击不在这里切焦点。
                        //   Tile 的 MouseLeftButtonUp → EnterTakeover 已完成
                        //   「置锁存 → 解锁全部 → 切焦点」，顺序经实测固定，
                        //   这里再切一次会和它打架（Tiles.cs 第 140 行的老坑）。
                        //   本订阅只负责"保护层未启用"的情况。
                        if (c.ShieldLocked) return;

                        // 已经是焦点格：不用动
                        var tile = _tiles.FirstOrDefault(t => ReferenceEquals(t.Row, row));
                        if (tile != null && ReferenceEquals(tile, _focusTile)) return;

                        SetFocus(row, reason: "native-click");
                    }
                    catch (Exception ex) { App.AppendErrorLog("ShieldClicked", ex); }
                });

            // 光标移出画面 → 重新挂保护层
            c.ShieldRearmRequested += _ =>
                Dispatcher.InvokeAsync(() => { if (ReferenceEquals(row, _viewing)) RearmShield(); });
            // 进入自由视角 → 隐藏保护层；退出 → 复位后重新挂上
            c.ShieldVisibleRequested += (_, visible) =>
                Dispatcher.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(row, _viewing)) return;

                    // ★★ 3.2.6：自由视角切换后**重挂全屏热键钩子** ★★
                    //   实测故障：全屏 → 开自由视角 → 关自由视角 → F11 再也退不出。
                    //   原因：自由视角会改变焦点归属（RDP 子 HWND / 目标会话接收器
                    //   参与抢键），本机 LL 钩子的先后顺序被打乱，虽然 IsHooked 仍为
                    //   true，但回调不再命中原有热键。
                    //   Windows 的 LL 钩子链按安装顺序排队，重挂才能回到链首
                    //   （LowLevelKeyboardHook.Rearm 的注释也是这个意思）。
                    try { InstallFullScreenHooks(); } catch { }

                    if (!visible)
                    {
                        return;
                    }
                    // 退出自由视角：视为"操作完毕"，重新进入需点击状态
                    SetShieldReleased(false);
                    UpdateShield();
                });

            // ★ 重新上锁以「画布范围」为准，不是单个 RDP 画面的矩形。
            c.CanvasScreenRectProvider = CanvasScreenRect;

            // ★ 恢复原生窗口位置时，用该 Console 所在格子的屏幕矩形当唯一来源。
            //   绝不能读宿主自己的 PointToScreen —— 它被移到屏幕外后会算出屏幕外坐标。
            var ownerRow = row;
            c.HostScreenRectProvider = () => TileScreenRect(ownerRow);

            // ★ 连接状态变化必须触发重排。
            //   Tile 的可见性由 IsConnected 决定，而连接是异步完成的。
            //   只在 ShowViewPageAsync 里排一次布局，会出现"连接完成时那次布局还看不到它，
            //   之后再没人排"→ 格子永远 Collapsed → RDP 原生窗口停在 (0,0) 不可见。
            c.ConnectionChanged += (_, connected) =>
                Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        if (!_initialized) return;
                        Debug("连接状态变化 → 重排：" + ownerRow.Account.Caption
                              + " connected=" + connected);
                        ApplyLayout();
                        if (connected) SyncNativeVisibility();
                    }
                    catch { }
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

        // ★ 自由视角不再是按钮，只做状态指示（原来误触会让视角乱掉）。
        //   切换到自由视角仍然通过热键完成，这里只负责把当前状态显示出来。

        private void ApplyModeUi(ConsoleInputMode mode)
        {
            string hk = _viewing?.Console?.ToggleHotkey ?? "Ctrl+Alt+Q";
            if (mode == ConsoleInputMode.FreeLook)
            {
                if (ModeLabel != null) ModeLabel.Text = "自由视角中";
                if (ModeDot != null) ModeDot.Fill = new SolidColorBrush(Color.FromRgb(0x7C, 0x5C, 0xFF));
                if (ModeIndicator != null)
                {
                    ModeIndicator.Background = new SolidColorBrush(Color.FromArgb(0x2E, 0x7C, 0x5C, 0xFF));
                    ModeIndicator.BorderBrush = new SolidColorBrush(Color.FromArgb(0x77, 0x7C, 0x5C, 0xFF));
                }
                UpdateInputIndicator(true);
            }
            else
            {
                if (ModeLabel != null) ModeLabel.Text = "指针模式";
                if (ModeDot != null) ModeDot.Fill = new SolidColorBrush(Color.FromRgb(0x6B, 0x7C, 0x9C));
                if (ModeIndicator != null)
                {
                    ModeIndicator.Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
                    ModeIndicator.BorderBrush = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
                }
                UpdateInputIndicator(false);
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
            if (c == null) { LogBus.Warn("自由视角", "先打开一个用户画面"); return; }
            App.Config.Settings.LockHotkey = spec.ToString();
            try { ConfigStore.Save(App.Config); } catch { }
            // 与通用设置相同：所有已打开控制台实时更新，而不是只改当前实例。
            foreach (Window window in Application.Current.Windows)
                ApplyHotkeyToVisualTree(window, spec.ToString());
            HotkeyBtn.SetSpec(spec);
            LogBus.Info("自由视角", "热键已实时生效 → " + spec.ToString());
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

                // SetInputMode 现在是同步直达的"退出"分支（不再等管道），
                // 退出路径永远当场完成，不会让注销流程卡在这里。
                try { row.Console?.SetInputMode(ConsoleInputMode.Pointer); } catch { }
                try { row.Console?.Disconnect(); } catch { }

                var (ok, msg) = await SessionPrepare.LogoffAsync(s.Id);
                if (!ok) throw new InvalidOperationException(msg);
                LogBus.Ok("已注销 " + row.Account.Caption, msg);

                // 过场仍盖住画面时销毁 RDP，避免露出 WindowsFormsHost 白底。
                _openedIds.Remove(row.Account.Id);
                RemoveTile(row);
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
                LogBus.Error("注销失败", ex.Message);
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
            RemoveTile(row);
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

        public void RefreshBadges() { RenderTabs(); UpdateLayoutUi(); }

        public void DisposeConsoles()
        {
            foreach (var r in _rows) { try { r.Console?.Dispose(); } catch { } }
            _rows.Clear();
            foreach (var t in _tiles.ToList())
            {
                try { TileHost.Children.Remove(t.Shell); } catch { }
            }
            _tiles.Clear();
            _focusTile = null;
        }

        // ══════════════════════════════════════════════════════════════════
        // 全屏模式（方案 B：顶部菜单自动出现，占真实行高，不是浮层）
        //
        // ★ 设计要点（都是实测约束）：
        //   ① 全屏不启用保护层 —— 用户全屏就是为了打游戏。
        //   ② 菜单**不能是浮层**：RDP 是 DirectComposition 表面，
        //      任何分层窗口压上去都会让那块区域退出硬件合成 → 变黑。
        //      所以菜单占真实 Grid 行，RDP 同步缩小。
        //   ③ 平时菜单高度为 0（画面满屏），鼠标移到顶部才撑开。
        //   ④ 切到别的应用**不退出全屏**（用户明确要求）。
        //   ⑤ 不用快捷键退出 —— 按键会被 RDP 吞掉，只能点菜单。
        // ══════════════════════════════════════════════════════════════════

        /// <summary>菜单条高度（DIP）。</summary>
        private const double FullScreenMenuHeight = 46;

        /// <summary>
        /// 鼠标距顶部多少像素以内算"想用菜单"。
        /// ★ 3.2.4：从 6 放宽到 12 —— 6px 太苛刻，实测用户很难命中，
        ///   配合"窗口激活"判定失效，导致全屏后完全呼不出菜单。
        /// </summary>
        private const double FullScreenHotZone = 24;   // 3.2.7：12→24，更接近任务栏自动隐藏手感

        private System.Windows.Threading.DispatcherTimer? _fullScreenMenuWatch;
        private DateTime _fullScreenMenuHideAt = DateTime.MinValue;
        private bool _fullScreenMenuVisible;
        private WindowState _preFullScreenState = WindowState.Normal;
        private WindowStyle _preFullScreenStyle = WindowStyle.None;
        private ResizeMode _preFullScreenResize = ResizeMode.CanResize;
        private Thickness _preFullScreenMargin;
        private double _preFsLeft = double.NaN;
        private double _preFsTop;
        private double _preFsWidth = double.NaN;
        private double _preFsHeight;
        private bool _preFsTopmost;
        private System.Windows.Shell.WindowChrome? _chrome;

        /// <summary>
        /// F11 全屏开关：本机低级键盘钩子。
        /// ★ 必须用 LL 钩子而不是 WPF KeyDown——键盘焦点常在 RDP 的子 HWND 里，
        ///   WPF 的键盘事件收不到。CanTrigger 绑定本页可见性：其它应用里 F11 原样透传。
        /// </summary>
        private LowLevelKeyboardHook? _fsKeyHook;

        /// <summary>全屏时的退出热键（键盘版）。只在全屏生效，非全屏不吞键。</summary>
        private LowLevelKeyboardHook? _fsEscHook;

        /// <summary>全屏热键的**鼠标版**（侧键等）。WH_MOUSE_LL。</summary>
        private LowLevelMouseHook? _fsMouseToggleHook;
        /// <summary>退出全屏热键的鼠标版。</summary>
        private LowLevelMouseHook? _fsMouseExitHook;

        /// <summary>全局设置里的全屏热键改了 → 所有画面重新装钩子。</summary>
        public static void NotifyFullScreenHotkeysChanged()
        {
            try { FullScreenHotkeysChanged?.Invoke(); } catch { }
        }
        private static event Action? FullScreenHotkeysChanged;

        /// <summary>设置里改了全屏热键 → 重新装钩子（必须在 UI 线程）。</summary>
        private void OnFullScreenHotkeysChanged()
        {
            try
            {
                if (Dispatcher.CheckAccess()) InstallFullScreenHooks();
                else Dispatcher.BeginInvoke(new Action(InstallFullScreenHooks));
            }
            catch { }
        }

        /// <summary>
        /// 本页当前是否应当响应全屏热键。
        ///
        /// ★★ 3.2.6 修正（第二次）★★
        ///   第一次改成 Visibility == Visible 仍然失效，原因：
        ///   本页真实可见性由三层叠加决定 ——
        ///     ① MainWindow 的 PageConsole 容器（SetActivePage 切这个）
        ///     ② 本页内部的 ViewPage / ConfigPage（点和面切换）
        ///     ③ 本页自身
        ///   而且 IsVisible 要求整条视觉树都可见；全屏 + 自由视角切换时
        ///   会有瞬时折叠，导致钩子"装了但 canTrigger=false"，按键直接被丢弃
        ///   （HotkeyMatcher.Key 第 102 行：!canTrigger 就 return false，连匹配都不算）。
        ///
        ///   现在改为：**只要本进程窗口没最小化、且本页加载过就允许触发**。
        ///   理由：这个钩子只在「控制台页」这一份 ConsolePage 上安装，
        ///   而它的作用就是全屏开关 —— 没有其它窗口会因此误触发。
        ///   宁可多响应一点，也不能在全屏里按不动。
        /// </summary>
        private bool IsPageUsable()
        {
            try
            {
                if (!IsLoaded) return false;
                var win = Window.GetWindow(this);
                if (win == null) return false;
                if (win.WindowState == WindowState.Minimized) return false;
                // 全屏时（本页主导）一定允许；非全屏时也允许，交给下面的额外约束
                if (_isFullScreen) return true;
                // 非全屏：要求本页确实是当前显示的页（含内部 ViewPage 判断）
                return Visibility == Visibility.Visible
                       && ViewPage != null && ViewPage.Visibility == Visibility.Visible;
            }
            catch { return false; }
        }

        /// <summary>
        /// 按当前配置（重新）安装全屏热键。
        ///
        /// ★★ 3.2.6：键盘键和鼠标键走**两套不同的钩子** ★★
        ///   用户实测反馈：「鼠标侧键5，无论是否启用自由视角，均不生效快捷键」。
        ///   根因：原来只装了 LowLevelKeyboardHook(WH_KEYBOARD_LL)，
        ///   而 HotkeySpec 支持鼠标键（X1/X2…），录进去后钩子永远收不到事件，
        ///   装是"成功"的（IsHooked=True），按键却完全无效 —— 静默失效最难查。
        ///
        ///   现在按 spec.IsMouse 分派：
        ///     · 键盘键 → LowLevelKeyboardHook（原有行为）
        ///     · 鼠标键 → LowLevelMouseHook(WH_MOUSE_LL) + HotkeySpec.MatchesMouse
        ///   WH_MOUSE_LL 能收到侧键（WM_XBUTTONDOWN），不会吞掉普通移动/点击，
        ///   只在命中热键时返回 true 吞掉该次事件。
        /// </summary>
        private void InstallFullScreenHooks()
        {
            try { _fsKeyHook?.Dispose(); _fsKeyHook = null; } catch { }
            try { _fsEscHook?.Dispose(); _fsEscHook = null; } catch { }
            try { _fsMouseToggleHook?.Dispose(); _fsMouseToggleHook = null; } catch { }
            try { _fsMouseExitHook?.Dispose(); _fsMouseExitHook = null; } catch { }

            string toggleKey = App.Config.Settings.FullScreenHotkey;
            string exitKey = App.Config.Settings.ExitFullScreenHotkey;
            var toggleSpec = HotkeySpec.Parse(toggleKey);
            var exitSpec = HotkeySpec.Parse(exitKey);

            // 同一个键不重复装（否则一次按下会触发两次）
            bool exitIsSameAsToggle = exitSpec != null
                && string.Equals(exitKey, toggleKey, StringComparison.OrdinalIgnoreCase);

            // ── ① 进入/退出全屏 ─────────────────────────────────────
            if (toggleSpec != null)
            {
                try
                {
                    InstallOneFullScreenHotkey(toggleSpec, "toggle",
                        () => IsPageUsable(),
                        () =>
                        {
                            RdpConsole.Debug("全屏热键命中：" + toggleSpec + "（" + (toggleSpec.IsMouse ? "鼠标" : "键盘")
                                + "）→ 切换全屏，当前 " + (_isFullScreen ? "全屏" : "非全屏"));
                            Dispatcher.BeginInvoke(new Action(ToggleFullScreen));
                        });
                }
                catch (Exception ex) { App.AppendErrorLog("ConsolePage.FullScreenHook", ex); }
            }

            // ── ② 退出全屏（可选）──────────────────────────────────
            if (exitSpec != null && !exitIsSameAsToggle)
            {
                try
                {
                    InstallOneFullScreenHotkey(exitSpec, "exit",
                        () => _isFullScreen && IsPageUsable(),
                        () =>
                        {
                            RdpConsole.Debug("退出全屏热键命中：" + exitSpec);
                            Dispatcher.BeginInvoke(new Action(() => ExitFullScreen("ExitHotkey")));
                        });
                }
                catch (Exception ex) { App.AppendErrorLog("ConsolePage.ExitFullScreenHook", ex); }
            }
        }

        /// <summary>
        /// 安装**一个**全屏热键：鼠标键走 WH_MOUSE_LL，键盘键走 WH_KEYBOARD_LL。
        /// role = "toggle" | "exit"，用于分配字段与日志。
        /// </summary>
        private void InstallOneFullScreenHotkey(HotkeySpec spec, string role,
            Func<bool> canTrigger, Action pressed)
        {
            if (spec.IsMouse)
            {
                var hook = new LowLevelMouseHook();
                bool swallowUp = false;   // 按下被吞过 → 抬起也要吞，避免目标程序收到孤立抬起
                hook.Handler = ev =>
                {
                    try
                    {
                        if (ev.IsInjected) return false;          // 注入事件不触发，避免回环

                        // ★ 抬起必须与按下成对处理：
                        //   只吞 Down 会让目标程序/游戏收到一个孤立的 Up，
                        //   侧键绑定的动作可能因此"粘住"（实测这类 bug 很隐蔽）。
                        if (ev.IsXButtonUp)
                        {
                            bool swallowThisUp = swallowUp;
                            swallowUp = false;
                            return swallowThisUp;
                        }

                        if (!canTrigger()) return false;
                        if (!spec.MatchesMouse(ev)) return false;   // 只看 Down
                        if (spec.ToModifiers() != LowLevelKeyboardHook.CurrentModifiers) return false;

                        swallowUp = true;
                        pressed();
                        return true;                               // 吞掉这一次侧键按下
                    }
                    catch { return false; }
                };
                if (hook.Install())
                {
                    if (role == "exit") _fsMouseExitHook = hook; else _fsMouseToggleHook = hook;
                    RdpConsole.Debug("全屏热键已安装：" + spec + "（鼠标，WH_MOUSE_LL）role=" + role);
                }
                else hook.Dispose();
                return;
            }

            var kb = new LowLevelKeyboardHook
            {
                Spec = spec,
                CanTrigger = canTrigger,
                Pressed = pressed,
            };
            if (kb.Install())
            {
                if (role == "exit") _fsEscHook = kb; else _fsKeyHook = kb;
                RdpConsole.Debug("全屏热键已安装：" + spec + "（键盘，WH_KEYBOARD_LL）role=" + role);
            }
            else kb.Dispose();
        }

        private void BtnFullScreen_Click(object sender, RoutedEventArgs e)
        {
            if (_isFullScreen) ExitFullScreen();
            else EnterFullScreen();
        }

        /// <summary>F11 全屏开关。本机 LL 钩子在子 HWND 持有焦点时也能收到键。</summary>
        private void ToggleFullScreen()
        {
            if (_viewing?.Console == null) return;   // 没有正在查看的画面时不动作
            if (_isFullScreen) ExitFullScreen();
            else EnterFullScreen();
        }

        /// <summary>若处于全屏则立即退出（供切页 / 关闭等外部场景调用）。</summary>
        public void ExitFullScreenIfActive()
        {
            if (_isFullScreen) ExitFullScreen();
        }

        // 全屏用的最小 Win32 通道。
        // ★ WPF 的 Left/Top/Width/Height 是 DIP 且被 WindowChrome 的隐形
        //   WS_THICKFRAME 外扩一圈（4K/150% 下 +26px），永远差一点盖不满。
        //   进入全屏后用 SetWindowPos 直接下物理像素矩形，一次到位。
        private static class FsNative
        {
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                int x, int y, int cx, int cy, uint flags);
            public const uint SWP_NOZORDER = 0x0004;
            public const uint SWP_NOACTIVATE = 0x0010;
            public const uint SWP_FRAMECHANGED = 0x0020;
        }

        private void EnterFullScreen()
        {
            try
            {
                var win = Window.GetWindow(this);
                if (win == null || _viewing?.Console == null) return;

                _preFullScreenState = win.WindowState;
                _preFullScreenStyle = win.WindowStyle;
                _preFullScreenResize = win.ResizeMode;
                _preFsTopmost = win.Topmost;
                if (win.Content is FrameworkElement root) _preFullScreenMargin = root.Margin;

                // ★ 显式记住几何：退出时若之前是 Normal，必须回到原位置原尺寸。
                //   旧实现退出时只调 WindowState=Normal，窗口会停留在全屏大小上。
                _preFsLeft = win.Left;
                _preFsTop = win.Top;
                _preFsWidth = win.Width;
                _preFsHeight = win.Height;

                _isFullScreen = true;
                RdpConsole.Debug("进入全屏 · 钩子 hooked=" + (_fsKeyHook?.IsHooked == true)
                    + " canTrigger=" + IsPageUsable());

                // 全屏不保护：直接解锁。
                try { _viewing.Console.UnlockShield("进入全屏"); } catch { }
                SetShieldReleased(true);

                win.WindowStyle = WindowStyle.None;
                win.ResizeMode = ResizeMode.NoResize;
                if (win.Content is FrameworkElement r) r.Margin = new Thickness(0);

                // 主窗口 chrome（标题栏 / 侧栏 / 内边距）全部收纳。
                var mw = MainWindowRef ?? win as MainWindow;
                mw?.SetChromeFullscreen(true);

                // ★ 真·全屏：盖住任务栏。
                //   旧实现铺 VirtualScreen（跨全部显示器，且任务栏依然浮在上面）。
                //   改为「当前显示器整屏矩形 + Topmost」，物理像素换算成 DIP。
                var hwnd = new System.Windows.Interop.WindowInteropHelper(win).EnsureHandle();
                double sx = 1.0, sy = 1.0;
                var target = System.Windows.PresentationSource.FromVisual(win)?.CompositionTarget;
                if (target != null)
                {
                    var m = target.TransformFromDevice;
                    sx = m.M11;
                    sy = m.M22;
                }
                var scr = System.Windows.Forms.Screen.FromHandle(hwnd).Bounds;   // 物理像素

                // ★★★ 3.2.7：几何只设一次，避免"顶部空一块再全屏"的两段式跳变 ★★★
                //
                //   踩过两次坑，都在这里：
                //     ① 原来先 SetWindowPos 再归零 ResizeBorderThickness →
                //        客户区 = 整屏 + 那圈边框 → 画面比屏幕大。
                //     ② 后来又加 `win.Width = scr.Width / sx`（DIP）与物理像素混用 →
                //        窗口被改大 26px，再靠"自检 + 二次校正"救回来 →
                //        用户看到「先空一块、再全屏」。
                //   现在：**先归零边框，再一次性 SetWindowPos**，之后不再碰 WPF 的
                //   Width/Height（收到 WM_SIZE 它会自己同步，额外赋值只会打架）。
                _chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(win);
                if (_chrome != null) _chrome.ResizeBorderThickness = new Thickness(0);

                win.WindowState = WindowState.Normal;
                FsNative.SetWindowPos(hwnd, IntPtr.Zero, scr.Left, scr.Top, scr.Width, scr.Height,
                    FsNative.SWP_NOZORDER | FsNative.SWP_NOACTIVATE);

                // 几何稳定后再改层级与 DWM（不触发尺寸重排）
                win.Topmost = true;
                SoulLink.Services.WindowCorners.RemoveRounded(hwnd);

                // ★ 全屏铺满：拉伸模式（SmartSizing + 连接前已开的 EnableZoom）
                //   由控件按客户区自动放大铺满，远端分辨率不动。

                // ★★ 3.2.7：进入全屏**不主动显示菜单条** ★★
                //   用户原话：「或许一开始就不应该显示顶栏，顶栏是退出用的。」
                //   原来进入时先撑开菜单占 46px，3 秒后再收起 ——
                //   既造成"顶部空一块"，又多一次 RDP 尺寸变更。
                //   现在菜单一开始就是 0 高，鼠标移到顶部时热区看门狗自然撑开。
                SetFullScreenMenuRowImmediate(false);
                if (TopBar != null) TopBar.Visibility = Visibility.Collapsed;

                EnsureFullScreenMenuWatch(true);

                // 强制本帧落定（TopBar 隐藏 + 菜单 0 高 + RDP 新尺寸一起生效），
                // 只走一次布局，不再排队第二次。
                UpdateLayout();
                ApplyLayout();

                Debug("进入全屏 " + scr.Width + "x" + scr.Height
                      + " dpi=" + sx.ToString("0.##"));
            }
            catch (Exception ex) { App.AppendErrorLog("EnterFullScreen", ex); }
        }

        private void BtnExitFullScreen_Click(object sender, RoutedEventArgs e) => ExitFullScreen();

        private void ExitFullScreen(
            [System.Runtime.CompilerServices.CallerMemberName] string from = "")
        {
            try
            {
                RdpConsole.Debug("退出全屏 · from=" + from + " 当前_isFullScreen=" + _isFullScreen);
                if (!_isFullScreen) return;
                _isFullScreen = false;

                EnsureFullScreenMenuWatch(false);
                SetFullScreenMenuVisible(false);


                // ★★★ 3.2.7：退出全屏顺序 —— 先几何，最后才恢复窗口样式 ★★★
                //
                //   用户反馈「退出好像还是会变两次位置」。
                //   原因：WindowStyle 从 None 变回 SingleBorderWindow 时，
                //   非客户区（标题栏/边框）重新出现，Windows 会按"当前客户区"
                //   重算一次窗口外框 —— 这是第 1 次位移；
                //   紧接着再设 Left/Top/Width/Height —— 第 2 次位移。
                //   所以必须**先几何、后样式**，让样式引发的重算基于已正确的几何。
                var win = Window.GetWindow(this);
                if (win != null)
                {
                    // ① 先恢复几何（此时 WindowStyle 仍是 None，无非客户区干扰）
                    win.Topmost = _preFsTopmost;
                    if (_preFullScreenState == WindowState.Normal && !double.IsNaN(_preFsWidth))
                    {
                        win.WindowState = WindowState.Normal;
                        win.Left = _preFsLeft;
                        win.Top = _preFsTop;
                        win.Width = _preFsWidth;
                        win.Height = _preFsHeight;
                    }
                    else
                    {
                        win.WindowState = _preFullScreenState;
                    }

                    // ② 再恢复边框与样式（非客户区回来，但几何已就位）
                    if (_chrome != null) _chrome.ResizeBorderThickness = new Thickness(6);
                    win.ResizeMode = _preFullScreenResize;
                    win.WindowStyle = _preFullScreenStyle;

                    // ③ 其余恢复（不改几何，不会引起位置跳变）
                    if (win.Content is FrameworkElement re) re.Margin = _preFullScreenMargin;
                    if (TopBar != null) TopBar.Visibility = Visibility.Visible;
                    (MainWindowRef ?? win as MainWindow)?.SetChromeFullscreen(false);

                    var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                    SoulLink.Services.WindowCorners.ApplyRounded(hwnd);
                }

                // ④ 只走一次布局（原来这里还排队第二次 ApplyLayout，
                //    那正是"退出时重排好几次"的来源，已删）。
                UpdateLayout();
                ApplyLayout();

                // ⑤ 保护层状态延后一拍：它只影响遮罩显隐，不参与画面布局，
                //    放同步路径里会让上面的布局白做一次。
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { UpdateShield(); } catch { }
                }), System.Windows.Threading.DispatcherPriority.Background);

                Debug("退出全屏");
            }
            catch (Exception ex) { App.AppendErrorLog("ExitFullScreen", ex); }
        }

        private void EnsureFullScreenMenuWatch(bool on)
        {
            try
            {
                if (!on)
                {
                    _fullScreenMenuWatch?.Stop();
                    _fullScreenMenuWatch = null;
                    return;
                }
                // 3.2.7：120→60ms（每拍只读一次光标位置，开销可忽略）
                _fullScreenMenuWatch ??= new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(60) };
                _fullScreenMenuWatch.Tick -= OnFullScreenMenuTick;
                _fullScreenMenuWatch.Tick += OnFullScreenMenuTick;
                _fullScreenMenuWatch.Start();
            }
            catch (Exception ex) { App.AppendErrorLog("EnsureFullScreenMenuWatch", ex); }
        }

        private void OnFullScreenMenuTick(object? sender, EventArgs e)
        {
            try
            {
                if (!_isFullScreen) { EnsureFullScreenMenuWatch(false); return; }
                var win = Window.GetWindow(this);
                // ★★ 3.2.4 修「鼠标移到顶部呼不出退出栏」★★
                //   原来这里要求 win.IsActive —— 但全屏时焦点常在 RDP 的子 HWND 里，
                //   用户点到别的应用后 IsActive 也会变 false，于是**永远不会响应**，
                //   表现为"进去了出不来"。只看窗口是否可见即可。
                if (win == null || !win.IsVisible) return;

                var cursor = System.Windows.Forms.Cursor.Position;
                var topLeft = PointToScreen(new Point(0, 0));
                double y = cursor.Y - topLeft.Y;

                // 热区放宽到 12px（6px 太苛刻，手抖就错过）
                bool inHotZone = y >= -FullScreenHotZone && y <= FullScreenHotZone;
                if (inHotZone)
                {
                    SetFullScreenMenuVisible(true);
                    _fullScreenMenuHideAt = DateTime.UtcNow.AddSeconds(0.3);
                    return;
                }

                // 菜单显示中且鼠标停在菜单上 → 保持
                if (_fullScreenMenuVisible && y >= 0 && y <= FullScreenMenuHeight)
                {
                    _fullScreenMenuHideAt = DateTime.UtcNow.AddSeconds(0.3);
                    return;
                }

                if (_fullScreenMenuVisible && DateTime.UtcNow >= _fullScreenMenuHideAt)
                    SetFullScreenMenuVisible(false);
            }
            catch { }
        }

        /// <summary>
        /// 只设菜单条占位高度与可见性，**不触发 ApplyLayout**。
        /// 用于进入全屏时先让布局落定，再与其它元素一起刷新。
        /// </summary>
        private void SetFullScreenMenuRowImmediate(bool visible)
        {
            try
            {
                _fullScreenMenuVisible = visible;
                if (FullScreenMenuBar != null)
                    FullScreenMenuBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (FullScreenMenuRow != null)
                    FullScreenMenuRow.Height = visible
                        ? new GridLength(FullScreenMenuHeight)
                        : new GridLength(0);
            }
            catch { }
        }

        /// <summary>显示/收起菜单条：改行高，RDP 会跟着缩放（不是浮层遮挡）。</summary>
        private void SetFullScreenMenuVisible(bool visible)
        {
            try
            {
                if (_fullScreenMenuVisible == visible) return;
                _fullScreenMenuVisible = visible;

                if (FullScreenMenuBar != null)
                    FullScreenMenuBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (FullScreenMenuRow != null)
                    FullScreenMenuRow.Height = visible
                        ? new GridLength(FullScreenMenuHeight)
                        : new GridLength(0);

                // ★ 3.2.7：立刻强制布局，让 RDP 宿主尺寸在本帧内就位。
                //   原来直接 ApplyLayout()，但 RDP 原生窗口要等布局跑完才知道新尺寸，
                //   中间会空一拍 —— 用户感知就是"菜单慢慢出来/慢慢隐藏"。
                UpdateLayout();
                ApplyLayout();
            }
            catch (Exception ex) { App.AppendErrorLog("SetFullScreenMenuVisible", ex); }
        }

        private void BtnFullScreenSwitch_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RebuildFullScreenSwitchMenu();
                if (FullScreenSwitchMenu == null) return;
                FullScreenSwitchMenu.PlacementTarget = BtnFullScreenSwitch;
                FullScreenSwitchMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                FullScreenSwitchMenu.IsOpen = true;
            }
            catch (Exception ex) { App.AppendErrorLog("BtnFullScreenSwitch_Click", ex); }
        }

        private void RebuildFullScreenSwitchMenu()
        {
            try
            {
                if (FullScreenSwitchMenu == null) return;
                FullScreenSwitchMenu.Items.Clear();

                foreach (var t in _tiles)
                {
                    if (t.Console?.IsConnected != true) continue;
                    var mi = new MenuItem
                    {
                        Header = t.Row.Account.Caption,
                        IsCheckable = true,
                        IsChecked = ReferenceEquals(t, _focusTile),
                    };
                    var tile = t;
                    mi.Click += (_, __) =>
                    {
                        SetFocus(tile.Row, reason: "fullscreen-switch");
                        ApplyLayout();
                        FullScreenTitle.Text = tile.Row.Account.Caption;
                    };
                    FullScreenSwitchMenu.Items.Add(mi);
                }

                if (FullScreenSwitchMenu.Items.Count > 0)
                    FullScreenSwitchMenu.Items.Add(new Separator());
                var exitItem = new MenuItem { Header = "退出全屏" };
                exitItem.Click += (_, __) => ExitFullScreen();
                FullScreenSwitchMenu.Items.Add(exitItem);

                FullScreenTitle.Text = _viewing?.Account.Caption ?? "全屏";
            }
            catch (Exception ex) { App.AppendErrorLog("RebuildFullScreenSwitchMenu", ex); }
        }
    }
}
