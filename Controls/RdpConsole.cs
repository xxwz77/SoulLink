using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using SoulLink.Models;
using SoulLink.Services;

namespace SoulLink.Controls
{
    /// <summary>
    /// 一个内嵌的 RDP 控制台画面。
    ///
    /// 关键技术点（都是实测出来的，别改）：
    /// 1. 本机没有 aximp，用 <see cref="RdpAxHost"/> 手写 AxHost，CLSID 用 v9；
    /// 2. 密码必须走原生 IDispatch 按 DISPID=1 写入（见 <see cref="NativeDispatch"/>）；
    /// 3. 连接前先把目标会话 tsdiscon，否则 RDP 会新建一个空会话（HANDOFF 10.3）；
    /// 4. 初始化顺序：ServerInfo → Static → Redirect → Display → Performance → 密码 → Connect。
    /// </summary>
    public sealed class RdpConsole : Grid, IDisposable
    {
        /// <summary>Microsoft RDP Client Control - version 9</summary>
        public const string ClsidV9 = "{A3BC03A0-041D-42E3-AD22-882B7865C9C5}";
        /// <summary>version 10（v9 不可用时的备选）</summary>
        public const string ClsidV10 = "{8B918B82-7985-4C24-89DF-C33AD2BBFBCD}";
        /// <summary>version 11（再备选）</summary>
        public const string ClsidV11 = "{A0C63C30-F08D-4AB4-907C-34905D770C7D}";

        private static string[] CandidateClsids => new[] { ClsidV9, ClsidV10, ClsidV11 };

        private readonly WindowsFormsHost _host = new WindowsFormsHost();
        private RdpAxHost? _ax;
        private dynamic? _ocx;
        private bool _disposed;

        public Account? Account { get; private set; }

        /// <summary>当前会话的输入控制器（注入代理 + 坐标换算）。没连上时为 null。</summary>
        public SessionInput? Input { get; private set; }
        /// <summary>当前会话号（注入要用）</summary>
        public int CurrentSessionId { get; private set; }

        /// <summary>转视角是否启用（按住右键拖动）</summary>
        public bool LookModeEnabled { get; set; } = true;
        /// <summary>注入是否启用（关掉就只用 RDP 原生键鼠）</summary>
        public bool InjectEnabled { get; set; } = true;
        /// <summary>开启后，非锁定状态只在用户点击画面后允许远端键鼠，移出立即释放。</summary>
        public bool CaptureOnClick { get; set; }
        public bool PointerCaptureEnabled { get; private set; } = true;
        private bool _pointerCaptured;
        private System.Windows.Threading.DispatcherTimer? _captureWatch;

        /// <summary>注入活动文字，给界面显示</summary>
        public event Action<RdpConsole, string>? InputActivity;

        /// <summary>连接状态变化：true=画面已连上</summary>
        public event Action<RdpConsole, bool>? ConnectionChanged;
        /// <summary>一条人类可读的状态文字</summary>
        public event Action<RdpConsole, string>? StatusChanged;

        /// <summary>光标移出画面：请外层重新挂上保护层。</summary>
        public event Action<RdpConsole>? ShieldRearmRequested;

        /// <summary>视口内发生左键按下：请外层撤掉保护层。</summary>
        public event Action<RdpConsole>? ShieldReleaseRequested;

        /// <summary>请求外层显示/隐藏保护层（进入自由视角时隐藏，退出时恢复）。</summary>
        public event Action<RdpConsole, bool>? ShieldVisibleRequested;

        /// <summary>保护层被点掉：把键鼠真正交给 RDP。</summary>
        public void OnShieldReleased()
        {
            try
            {
                _pointerCaptured = true;
                PointerCaptureEnabled = true;
                GiveKeyboardFocus();
                Debug("保护层已解除（用户点击）");
            }
            catch { }
        }

        public bool IsConnected
        {
            get
            {
                try { return _ocx != null && (int)_ocx.Connected == 1; }
                catch { return false; }
            }
        }

        public RdpConsole()
        {
            Background = System.Windows.Media.Brushes.Black;

            // ★ 让 WPF 别争夺键盘：所有按键都应当直达 ActiveX 控件
            Focusable = false;
            System.Windows.Input.KeyboardNavigation.SetIsTabStop(_host, false);
            _host.Focusable = false;

            Children.Add(_host);
            HookInput();
        }

        // ── 鼠标 → 远端注入 ──────────────────────────────────────────────────

        // ── 输入状态 ─────────────────────────────────────────────────────────

        /// <summary>画面是否处于「自由视角」捕获状态。</summary>
        private bool _captured;
        /// <summary>锁定期间补焦点的重试次数（避免无限制调用 Focus）。</summary>
        private int _keyboardFocusRetry = 20;
        private int _captureCenterX;
        private int _captureCenterY;
        private RawMouseInput? _rawInput;
        private LookInputQueue? _lookQueue;
        private readonly object _lookQueueSync = new();
        private int _lastMoveX = int.MinValue;
        private int _lastMoveY = int.MinValue;

        /// <summary>锁定看门狗：自由视角下长时间没有任何输入事件就自动解锁。</summary>
        private System.Windows.Threading.DispatcherTimer? _lockWatchdog;
        private DateTime _lastInputAt = DateTime.MinValue;
        private const int LockWatchdogSeconds = 45;

        /// <summary>
        /// 看门狗：自由视角下若连续 N 秒没有真实输入，自动解锁。
        /// 目的是"人已经离开/卡住"时也能恢复，不依赖键盘是否送到前台。
        /// </summary>
        private void StartLockWatchdog()
        {
            _lockWatchdog ??= new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _lockWatchdog.Tick -= OnWatchdogTick;
            _lockWatchdog.Tick += OnWatchdogTick;
            _lockWatchdog.Start();
        }

        private void StopLockWatchdog()
        {
            try { _lockWatchdog?.Stop(); } catch { }
            if (_lockWatchdog != null) _lockWatchdog.Tick -= OnWatchdogTick;
        }

        private void OnWatchdogTick(object? sender, EventArgs e)
        {
            if (!_captured)
            {
                StopLockWatchdog();
                return;
            }
            double idle = (DateTime.UtcNow - _lastInputAt).TotalSeconds;
            if (idle < LockWatchdogSeconds) return;
            // 同步落盘：这是关键安全事件，必须能在异步日志队列未刷盘时查到。
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(ConfigStore.ConfigDir, "watchdog.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " 自动解锁 idle=" + (int)idle + "s" + Environment.NewLine,
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
            Debug("看门狗：自由视角空闲 " + (int)idle + " 秒，自动解锁");
            InputActivity?.Invoke(this, "自由视角空闲超时，已自动解除锁定");
            ReleaseCapture("看门狗超时");
        }

        private LookInputQueue LookQueue
        {
            get
            {
                lock (_lookQueueSync)
                    return _lookQueue ??= new LookInputQueue(() => Input!, () => LookSensitivity, Debug);
            }
        }

        /// <summary>Raw Input 位移回调：只入队，不做 IO。</summary>
        private void OnRawDelta(int dx, int dy)
        {
            if (!_captured) return;
            _lastInputAt = DateTime.UtcNow;
            LookQueue.EnqueueMove(dx, dy);
        }

        private void ResetLookQueue()
        {
            LookInputQueue? queue;
            lock (_lookQueueSync)
            {
                queue = _lookQueue;
                _lookQueue = null;
            }
            // Dispose 内部会先取消位移、保留按键释放语义；不阻塞 UI。
            queue?.Dispose();
            _lastMoveX = _lastMoveY = int.MinValue;
        }

        private uint _scaleFactor = 100;
        private bool _hasConnected;
        private LowLevelMouseHook? _mouseHook;
        private Window? _ownerWindow;
        private bool _ownerHooked;

        /// <summary>系统热键：开启锁定时注册，按一下就退出（默认 Ctrl+Alt+Q）。</summary>
        private NativeHotkey? _escapeHotkey;
        internal bool HotkeyArmed { get; set; }
        private int _hotkeyToggleVersion;
        private readonly System.Collections.Generic.HashSet<string> _injectedButtons
            = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _inputUsable;
        private int _inputGeneration;
        private int _connectionGeneration;
        private int _inputStartPending;
        private bool _nativeRelativeMouse;
        private int _activeState; // 0=前台允许输入，1=后台/已撤销
        public void SetForegroundActive(bool active)
        {
            System.Threading.Interlocked.Exchange(ref _activeState, active ? 0 : 1);
            HotkeyArmed = active;
            if (!active)
            {
                _captured = false;
                InputMode = ConsoleInputMode.Pointer;
                InputModeChanged?.Invoke(this, InputMode);
                ReleaseCapture("离开当前控制台");
                UninstallMouseHook();
                // ★ 不要在这里停注入代理。
                //   切到配置页只是"看不见"，会话和代理都还活着；
                //   停掉后切回来不会自动重建，表现为"按侧键没反应/进不去自由视角"。
                //   真正需要停止的场景（断开、销毁）由 Disconnect/Dispose 负责。
            }
        }

        private bool InputAllowed => _activeState == 0 && !_disposed && IsVisible;

        /// <summary>切回画面时调用：如果输入通道已断开，重建一次。</summary>
        public void EnsureInputRestored()
        {
            try
            {
                if (_disposed || !IsConnected) return;
                if (Input?.Relay.IsConnected == true) return;
                if (CurrentSessionId <= 0) return;
                var acc = Account;
                if (acc == null) return;
                _ = StopInputAsync();
                BeginInput(CurrentSessionId, acc, ConfigStore.GetPassword(acc));
                Debug("切回画面：重建输入通道 会话=" + CurrentSessionId);
            }
            catch (Exception ex) { Debug("重建输入通道失败：" + ex.Message); }
        }

        // ── 通道自动重建 ─────────────────────────────────────────────────────

        private System.Windows.Threading.DispatcherTimer? _rebuildTimer;
        private int _rebuildAttempt;
        private const int MaxRebuildAttempts = 5;

        /// <summary>
        /// 安排一次通道重建（指数退避，最多 5 次）。
        /// 触发来源：relay 断开事件、连接后探测失败。
        /// </summary>
        private void ScheduleInputRebuild(string reason)
        {
            try
            {
                if (_disposed || !IsConnected) return;
                if (Input?.Relay.IsConnected == true) return;   // 已经好了
                if (CurrentSessionId <= 0) return;

                if (_rebuildAttempt >= MaxRebuildAttempts)
                {
                    Debug("通道重建已达上限（" + MaxRebuildAttempts + " 次），停止重试：" + reason);
                    InputActivity?.Invoke(this, "注入通道重建失败，请点「重新连接」");
                    return;
                }
                _rebuildAttempt++;

                // 退避：2s、4s、8s、16s、30s
                int delayMs = Math.Min(30000, 2000 * (1 << (_rebuildAttempt - 1)));
                Debug("安排通道重建 #" + _rebuildAttempt + "（" + reason + "），" + delayMs + "ms 后执行");

                _rebuildTimer?.Stop();
                _rebuildTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(delayMs)
                };
                _rebuildTimer.Tick += (_, __) =>
                {
                    _rebuildTimer?.Stop();
                    try
                    {
                        if (_disposed || !IsConnected) return;
                        if (Input?.Relay.IsConnected == true) { _rebuildAttempt = 0; return; }
                        Debug("执行通道重建 #" + _rebuildAttempt);
                        InputActivity?.Invoke(this, "正在重建注入通道…");
                        EnsureInputRestored();
                    }
                    catch (Exception ex) { Debug("通道重建异常：" + ex.Message); }
                };
                _rebuildTimer.Start();
            }
            catch (Exception ex) { Debug("安排重建失败：" + ex.Message); }
        }

        /// <summary>通道恢复正常时清零重试计数。</summary>
        private void ResetRebuildCounter()
        {
            _rebuildAttempt = 0;
        }

        /// <summary>只有前台可见控制台可接收热键；退出自由视角不受连接状态限制。</summary>
        public bool CanUseToggleHotkey
        {
            get
            {
                try
                {
                    // ★ 退出永远放行：只要正在自由视角，任何条件都不能挡住解锁。
                    //   这是"进了出不来"事故的直接教训——锁定状态下必须无条件可退。
                    if (_captured) return true;
                    if (_disposed || !IsVisible) return false;
                    if (_ownerWindow == null || !_ownerWindow.IsActive) return false;
                    if (_ownerWindow.WindowState == WindowState.Minimized) return false;
                    // ActiveX 不会让 WPF 稳定看到 ContainsFocus / Mouse.DirectlyOver。
                    // 不能用这两个条件阻断热键，否则用户按了也被判为"不属于画面"。
                    return HotkeyArmed && IsConnected;
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// 自由视角灵敏度倍率。
        /// 实测：高 DPI 鼠标（1600+）在原神里用 1.0 明显偏慢，默认给 4.0 比较跟手。
        /// </summary>
        public double LookSensitivity { get; set; } = 4.0;

        /// <summary>把全局 RDP 交互设置立即应用到已打开的画面。</summary>
        public void ApplyGlobalSettings()
        {
            LookSensitivity = App.Config.Settings.LookSensitivity;
            ToggleHotkey = App.Config.Settings.LockHotkey;
            CaptureOnClick = App.Config.Settings.RdpCaptureOnClick;
            if (!CaptureOnClick)
            {
                _captureWatch?.Stop();
                _pointerCaptured = true;
                PointerCaptureEnabled = true;
            }
            else if (_captureWatch == null || !_captureWatch.IsEnabled)
            {
                _captureWatch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
                _captureWatch.Tick += (_, __) => WatchPointerCapture();
                _captureWatch.Start();
            }
        }

        private string _toggleHotkey = "Ctrl+Alt+Q";

        /// <summary>锁定开关的热键。改了会重新注册。</summary>
        public string ToggleHotkey
        {
            get => _toggleHotkey;
            set
            {
                if (string.IsNullOrWhiteSpace(value) || value == _toggleHotkey) return;
                _toggleHotkey = value.Trim();

                var old = _escapeHotkey;
                _escapeHotkey = null;
                try { old?.Dispose(); } catch { }

                EnsureToggleHotkey();
            }
        }

        public ConsoleInputMode InputMode { get; private set; } = ConsoleInputMode.Pointer;

        public event Action<RdpConsole, ConsoleInputMode>? InputModeChanged;

        /// <summary>
        /// 接管画面区域的鼠标。
        ///
        /// ★ 为什么用全局钩子：RDP 的 ActiveX 控件吞掉全部鼠标输入，
        ///   WinForms/WPF 事件都收不到（见 HANDOFF 11.8）。
        ///
        /// ★ 安全底线（第一版踩过的坑，务必保持）：
        ///   1. 默认**不接管**（完全放行给 RDP），用户主动按热键才进入锁定；
        ///   2. 捕获期间注册系统热键 Ctrl+Alt+Q，任何时候都能逃出来；
        ///   3. 窗口最小化 / 失去焦点 / 不可见 → 立刻释放；
        ///   4. 钩子单例，重复安装直接忽略（第一版泄漏了 8 个）。
        /// </summary>
        private void HookInput()
        {
            _host.SizeChanged += (s, e) =>
            {
                RefreshViewport();
                RequestResize();
            };

            Loaded += (_, __) =>
            {
                // ★ 必须走 ApplyGlobalSettings 而不是只赋 CaptureOnClick：
                //   热键（ToggleHotkey）和灵敏度都在里面同步。
                //   原来这里只设了 CaptureOnClick，导致 ToggleHotkey 永远是字段初值
                //   "Ctrl+Alt+Q"，用户在通用设置里改的热键根本不生效（实测日志：
                //   EnsureToggleHotkey spec='Ctrl+Alt+Q' parsed=Ctrl+Alt+Q，而配置里是 Ctrl+Alt+K）。
                ApplyGlobalSettings();

                AttachOwnerWindow();
                InstallMouseHook();
                EnsureToggleHotkey();
            };

            IsVisibleChanged += (_, __) =>
            {
                if (IsVisible && _activeState == 0) { InstallMouseHook(); EnsureToggleHotkey(); }
                else if (!IsVisible)
                {
                    SetForegroundActive(false);
                }
            };

            Unloaded += (_, __) =>
            {
                ReleaseCapture("卸载");
                UninstallMouseHook();
                DetachOwnerWindow();
            };
        }

        private void AttachOwnerWindow()
        {
            var w = Window.GetWindow(this);
            if (w == null || (ReferenceEquals(w, _ownerWindow) && _ownerHooked)) return;

            DetachOwnerWindow();
            _ownerWindow = w;
            _ownerHooked = true;
            _ownerWindow.Activated += OnOwnerActivated;
            _ownerWindow.Deactivated += OnOwnerDeactivated;
            _ownerWindow.StateChanged += OnOwnerStateChanged;
            _ownerWindow.Closing += OnOwnerClosing;
        }

        private void DetachOwnerWindow()
        {
            if (_ownerWindow != null && _ownerHooked)
            {
                _ownerWindow.Activated -= OnOwnerActivated;
                _ownerWindow.Deactivated -= OnOwnerDeactivated;
                _ownerWindow.StateChanged -= OnOwnerStateChanged;
                _ownerWindow.Closing -= OnOwnerClosing;
            }
            _ownerHooked = false;
        }

        private void OnOwnerActivated(object? sender, EventArgs e)
        {
            if (IsVisible) EnsureToggleHotkey();
        }
        private void OnOwnerDeactivated(object? sender, EventArgs e)
        {
            _captured = false;
            InputMode = ConsoleInputMode.Pointer;
            ReleaseCapture("窗口失去焦点");
            UninstallMouseHook();
        }
        private void OnOwnerStateChanged(object? sender, EventArgs e)
        {
            if (_ownerWindow?.WindowState != WindowState.Normal) ReleaseCapture("窗口状态变化");
        }
        private void OnOwnerClosing(object? sender, System.ComponentModel.CancelEventArgs e) => ReleaseCapture("窗口关闭");

        /// <summary>
        /// 画面在屏幕上的有效矩形（**全部是物理像素**）。
        ///
        /// ★ 必须用 PointToScreen 算尺寸，不能用 ActualWidth：
        ///   ActualWidth 是 DIP，本机 150% DPI 下只有物理像素的 2/3。
        ///   混用会让换算比例错掉，表现就是「点下去光标跳到别处」。
        /// </summary>
        private (int left, int top, int width, int height)? ViewportScreenRect()
        {
            try
            {
                if (_host == null || !IsVisible || !_host.IsVisible) return null;
                if (_host.ActualWidth < 4 || _host.ActualHeight < 4) return null;

                var w = _ownerWindow ?? Window.GetWindow(this);
                if (w == null || !w.IsVisible) return null;
                if (w.WindowState == WindowState.Minimized) return null;

                var tl = _host.PointToScreen(new Point(0, 0));
                var br = _host.PointToScreen(new Point(_host.ActualWidth, _host.ActualHeight));

                int left = (int)Math.Round(tl.X);
                int top = (int)Math.Round(tl.Y);
                int width = (int)Math.Round(br.X - tl.X);
                int height = (int)Math.Round(br.Y - tl.Y);

                // 窗口最小化时 PointToScreen 会返回 -3xxxx 这种垃圾值
                if (left < -20000 || top < -20000) return null;
                if (width < 4 || height < 4) return null;
                if (left + width <= 0 || top + height <= 0) return null;

                var screen = new Rect(0, 0,
                    System.Windows.Forms.SystemInformation.VirtualScreen.Width,
                    System.Windows.Forms.SystemInformation.VirtualScreen.Height);
                if (!screen.IntersectsWith(new Rect(left, top, width, height))) return null;

                return (left, top, width, height);
            }
            catch { return null; }
        }

        private void InstallMouseHook()
        {
            if (_disposed || !IsVisible || _ownerWindow == null || !_ownerWindow.IsActive) return;
            if (_mouseHook != null) return;   // ★ 单例，避免泄漏多个钩子
            try
            {
                var hook = new LowLevelMouseHook();
                hook.Handler = OnGlobalMouse;
                if (!hook.Install()) { Debug("全局鼠标钩子安装失败"); return; }
                _mouseHook = hook;
                Debug("全局鼠标钩子已安装");
            }
            catch (Exception ex) { Debug("安装鼠标钩子异常：" + ex.Message); }
        }

        private void UninstallMouseHook()
        {
            // 本方案不隐藏、不定位、不裁剪本机光标，也不修改系统鼠标加速。

            var hk = _escapeHotkey;
            _escapeHotkey = null;
            try { hk?.Dispose(); } catch { }

            var h = _mouseHook;
            _mouseHook = null;
            try { h?.Dispose(); } catch { }

            // 钩子卸载后看门狗已无意义，必须停掉，避免隐藏页继续计时。
            StopLockWatchdog();
        }

        /// <summary>切换输入模式。进入自由视角前会先确认一切就绪。</summary>
        public (bool ok, string message) SetInputMode(ConsoleInputMode mode)
        {
            if (mode == InputMode && (mode != ConsoleInputMode.FreeLook || _captured))
                return (true, mode == ConsoleInputMode.FreeLook ? "已进入自由视角" : "已切回指针模式");
            if (mode == ConsoleInputMode.FreeLook)
            {
                // ★ 相对注入不可用时不能吞掉本机鼠标，但也不能被一个"过期缓存"永久挡住。
                //   _inputUsable 只在代理首次探测时写过一次；之后 StopInputAsync 会置 false，
                //   而代理重连后没有任何地方恢复它 —— 曾导致"热键收到了却进不去"。
                //   这里：管道连通但标志为 false 时，当场重新探测一次。
                if (!IsConnected || Input == null || !Input.Relay.IsConnected)
                {
                    const string unavailable = "相对输入通道尚未连接，不能进入自由视角。请等待「注入就绪」。";
                    InputActivity?.Invoke(this, unavailable);
                    Debug("拒绝进入自由视角：通道未连接");
                    return (false, unavailable);
                }
                if (!_inputUsable)
                {
                    Debug("输入标志为 false，当场重新探测一次");
                    try
                    {
                        var (usable, why) = Input.CheckUsableAsync().GetAwaiter().GetResult();
                        _inputUsable = usable;
                        Debug("重新探测结果 usable=" + usable + " · " + why);
                    }
                    catch (Exception ex) { Debug("重新探测异常：" + ex.Message); }
                }
                if (!_inputUsable)
                {
                    const string unavailable = "相对输入通道尚未就绪，不能进入自由视角。请点「重新连接」后再试。";
                    InputActivity?.Invoke(this, unavailable);
                    Debug("拒绝进入自由视角：探测后仍不可用");
                    return (false, unavailable);
                }
                if (!BeginCapture("手动切换"))
                    return (false, "画面不在可用状态，无法进入自由视角");
            }
            else
            {
                ReleaseCapture("手动切换");
            }

            InputMode = mode;
            InputModeChanged?.Invoke(this, mode);
            return (true, mode == ConsoleInputMode.FreeLook ? "已进入自由视角" : "已切回指针模式");
        }

        private bool BeginCapture(string reason)
        {
            if (_captured) return true;

            var rect = ViewportScreenRect();
            if (rect == null)
            {
                Debug("拒绝进入自由视角：画面不可用（" + reason + "） hostVisible=" + _host.IsVisible
                    + " size=" + _host.ActualWidth + "x" + _host.ActualHeight
                    + " owner=" + (_ownerWindow?.IsVisible) + " state=" + _ownerWindow?.WindowState);
                return false;
            }
            var r = rect.Value;

            _captureCenterX = r.left + r.width / 2;
            _captureCenterY = r.top + r.height / 2;

            // 中心点必须落在虚拟屏幕内，否则绝对不捕获
            var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
            if (_captureCenterX < vs.Left || _captureCenterX > vs.Right - 1 ||
                _captureCenterY < vs.Top || _captureCenterY > vs.Bottom - 1)
            {
                Debug("拒绝进入自由视角：中心点非法 " + _captureCenterX + "," + _captureCenterY);
                return false;
            }

            // 已实测：本机 RelativeMouseMode 仅连接前可设置，连接后修改返回 E_FAIL。
            // 当前仍使用已验证的目标会话相对注入；后续用专用相对模式连接解决光标捕获。
            _captured = true;
            InputMode = ConsoleInputMode.FreeLook;
            InputModeChanged?.Invoke(this, InputMode);
            // 自由视角要接管鼠标，请外层撤掉保护层（否则 Popup 会盖在上面挡住操作）
            try { ShieldVisibleRequested?.Invoke(this, false); } catch { }

            // 不在自由视角开关时修改系统鼠标加速。
            // SystemParametersInfo 会向所有顶层窗口广播设置变化，实测可造成系统级卡顿。
            // 灵敏度只在目标会话的相对位移上计算，不污染用户全局设置。

            // ★★★ 不碰光标！★★★
            //
            //   用户明确要求"真实状态"：转视角时游戏自己隐藏光标，
            //   任务/地图/桌面时游戏自己显示光标。我们**绝不能**制造假光标。
            //
            //   所以这里什么都不做：
            //     · 不 ClipCursor（那会在画面里造出一个被约束的光标）
            //     · 不 Cursor.Hide（那是假的隐藏，游戏并不知道）
            //     · 不 SetCursorPos 居中（那是假的定位）
            //
            //   真正的转视角靠 RelayAgent 在原地调 mouse_event 相对位移，
            //   游戏读 Raw Input 后自己转、自己决定光标显不显示 ——
            //   和"人在那个会话里手动转视角"完全一样。
            _captureCenterX = r.left + r.width / 2;
            _captureCenterY = r.top + r.height / 2;

            // ── 采集硬件级相对位移（只读，不拦截、不动光标）──
            // 失败不影响进入：回退到旧的坐标差分。
            try
            {
                var win = _ownerWindow ?? Window.GetWindow(this);
                if (win != null)
                {
                    _rawInput ??= RawMouseInput.TryCreate(win);
                    if (_rawInput != null)
                    {
                        _rawInput.Diagnostic = Debug;
                        _rawInput.Moved = OnRawDelta;
                        if (!_rawInput.Start())
                        {
                            _rawInput.Dispose();
                            _rawInput = null;
                        }
                    }
                }
            }
            catch (Exception ex) { Debug("Raw Input 初始化失败（回退差分）：" + ex.Message); }

            // ── 把光标约束在 RDP 视口内（不缩小区域、不钉中心）──
            // 位移已由 Raw Input 提供，这里只防止光标跑到别的窗口。
            try
            {
                if (CursorClip.Apply(r.left, r.top, r.width, r.height))
                    Debug("光标已约束在视口内：" + r.left + "," + r.top + " " + r.width + "x" + r.height);
                else
                    Debug("光标约束失败（不影响转视角）");
            }
            catch (Exception ex) { Debug("光标约束异常：" + ex.Message); }

            _lastInputAt = DateTime.UtcNow;
            StartLockWatchdog();
            EnsureToggleHotkey();

            // ★ 把键盘焦点交回 RDP 控件。
            //   转视角期间键盘应该照常送到游戏（我们只接管鼠标）。
            GiveKeyboardFocus();

            // 兜底：个别情况下焦点同步要等一帧，稍后再补一次
            Dispatcher.InvokeAsync(() => GiveKeyboardFocus(),
                System.Windows.Threading.DispatcherPriority.Input);

            Debug("进入自由视角（" + reason + "）中心=" + _captureCenterX + "," + _captureCenterY
                  + " 焦点=" + (_ax?.ContainsFocus == true));
            return true;
        }

        /// <summary>
        /// 确保「锁定开关」热键已注册。
        /// 这个热键**常驻**（不只在锁定时），所以既能上锁也能解锁。
        /// </summary>
        private void EnsureToggleHotkey()
        {
            // ★ 幂等判断要看"已注册用的规格"是否等于当前想要的热键。
            //   原来只判断 _escapeHotkey != null 就返回，导致：
            //   · 第一次用默认 Ctrl+Alt+Q 注册成功
            //   · 用户改成 Ctrl+Alt+J 后，这里直接 return，新热键永远没注册
            //   现在：规格不一致就先释放再重注册；一致才跳过。
            if (_disposed || !IsVisible || _ownerWindow == null || !_ownerWindow.IsActive) return;
            if (_escapeHotkey != null && _hotkeySpec == ToggleHotkey && IsVisible) return;

            var old = _escapeHotkey;
            _escapeHotkey = null;
            try { old?.Dispose(); } catch { }

            _hotkeySpec = ToggleHotkey;

            var spec = HotkeySpec.Parse(ToggleHotkey);
            if (spec == null || spec.Key == 0 && string.IsNullOrEmpty(spec.MouseButton))
            {
                Debug("热键配置无效：" + ToggleHotkey);
                return;
            }
            _escapeHotkey = NativeHotkey.RegisterForConsole(spec, OnToggleHotkey, this);
            SyncRelayHotkey("配置更新");

            if (_escapeHotkey == null)
                Debug("热键注册失败（可能被占用）：" + ToggleHotkey);
            else
                Debug("锁定热键已注册：" + ToggleHotkey);
        }

        private string _hotkeySpec = "";

        private void OnToggleHotkey()
        {
            Debug("执行热键切换 · guard=" + _hotkeyToggleVersion + " UI线程=" + Dispatcher.CheckAccess());
            // 只有空闲状态(0)才能进入；原实现把比较结果当成 -1 标记，导致所有事件直接返回。
            if (System.Threading.Interlocked.CompareExchange(ref _hotkeyToggleVersion, 1, 0) != 0) return;
            try { ToggleFromHotkey(); }
            finally { System.Threading.Interlocked.Exchange(ref _hotkeyToggleVersion, 0); }
        }

        private void ToggleFromHotkey()
        {
            var target = _captured ? ConsoleInputMode.Pointer : ConsoleInputMode.FreeLook;

            Debug("热键触发: target=" + target + " IsConnected=" + IsConnected + " _captured=" + _captured);

            // ★ 退出必须无条件放行。
            //   热键是脱离转视角状态的主要通道，任何前置条件（IsConnected / Input 就绪）
            //   一旦不成立就可能把用户卡在模式里 —— 所以退出分支放在最前面。
            if (target == ConsoleInputMode.Pointer)
            {
                SetInputMode(ConsoleInputMode.Pointer);
                return;
            }

            if (!IsConnected)
            {
                InputActivity?.Invoke(this, "画面尚未连接，无法进入自由视角");
                return;
            }
            var (ok, message) = SetInputMode(target);
            Debug("热键切换结果 ok=" + ok + " message=" + message + " captured=" + _captured + " mode=" + InputMode);
            if (!ok && _captured) ReleaseCapture("热键兜底");
        }

        private void ReleaseCapture(string reason)
        {
            bool wasCaptured = _captured;
            _captured = false;
            InputMode = ConsoleInputMode.Pointer;
            InputModeChanged?.Invoke(this, InputMode);
            StopLockWatchdog();
            // 任何退出路径都必须松开光标约束（含失焦、切页、断开、看门狗、异常）
            try { CursorClip.Release(); } catch { }
            try { _rawInput?.Stop(); } catch { }
            ReleasePointerCapture();
            ReleaseInjectedButtonsAsync();
            // 先排队抬起；代理断开仍会 releaseall，不把释放指令自己清掉。
            ResetLookQueue();
            // ★ 无论之前是否在自由视角，都要恢复保护层。
            //   原实现在 !wasCaptured 时直接 return，导致"关掉自由视角后不会重新上锁"。
            try { ShieldVisibleRequested?.Invoke(this, true); } catch { }
            if (!wasCaptured) return;
            _keyboardFocusRetry = 20;
            Debug("退出自由视角（" + reason + "）");
        }

        private void ReleaseInjectedButtonsAsync()
        {
            _injectedButtons.Clear();
            var input = Input;
            if (input?.Relay.IsConnected != true) return;
            // 退出/断开/销毁三条路径都走这里：无条件释放，避免远端留下按下态。
            _ = input.Relay.ReleaseAllAsync();
            Debug("已请求代理释放所有鼠标按键");
        }

        private void WatchPointerCapture()
        {
            if (!IsConnected || !IsVisible) { ReleasePointerCapture(); return; }
            var rect = ViewportScreenRect();
            if (rect == null) { ReleasePointerCapture(); return; }
            var cursor = System.Windows.Forms.Cursor.Position;
            bool inside = cursor.X >= rect.Value.left && cursor.X < rect.Value.left + rect.Value.width
                       && cursor.Y >= rect.Value.top && cursor.Y < rect.Value.top + rect.Value.height;
            bool wasCaptured = _pointerCaptured;
            if (!inside || (_ownerWindow != null && !_ownerWindow.IsActive)) ReleasePointerCapture();

            // 光标移出画面：通知外层重新挂保护层（只报一次）
            if (wasCaptured && !_pointerCaptured && !inside)
                ShieldRearmRequested?.Invoke(this);
        }

        private void ReleasePointerCapture()
        {
            if (!_pointerCaptured) return;
            _pointerCaptured = false;
            PointerCaptureEnabled = false;
            try { System.Windows.Input.Keyboard.ClearFocus(); } catch { }
            InputActivity?.Invoke(this, "键鼠已释放 · 点击画面可重新捕获");
        }

        /// <summary>
        /// 全局鼠标回调。返回 true = 吞掉（不传给宿主桌面和 RDP 控件）。
        ///
        /// ★ 两种模式，语义要清楚：
        ///   锁定关闭（默认）：**完全放行**，鼠标事件走 RDP 原生通道，行为跟直接开游戏窗口一样。
        ///   自由视角：吞掉本机鼠标，在目标会话内做相对位移与 down/up 注入；
        ///             不隐藏、不定位、不裁剪本机光标。
        ///
        /// 任何异常都立刻放行，绝不让用户动不了鼠标。
        /// </summary>
        /// <summary>硬兜底：自由视角持续时，窗口失焦超过该时长就自动退出。</summary>
        private DateTime _captureEnteredAt = DateTime.MinValue;

        private bool OnGlobalMouse(MouseHookEvent ev)
        {
            try
            {
                // ★★★ 自由视角期间：退出热键必须在本机完成 ★★★
                //
                // 事故根因：进入自由视角后本机钩子吞掉鼠标，A2 会话永远收不到侧键，
                // 于是"目标会话接收器"那条退出通道彻底失效 —— 必然进得去出不来。
                // 因此锁定时直接在本机判定并解锁，不再依赖目标会话。
                if (_captured && !ev.IsInjected)
                {
                    var spec = HotkeySpec.Parse(App.Config.Settings.LockHotkey);
                    bool isMouseSpec = spec?.IsMouse == true;
                    bool down = ev.IsLeftDown || ev.IsRightDown || ev.IsMiddleDown
                                || ev.IsXButton1Down || ev.IsXButton2Down;

                    // ① 配置的热键就是鼠标键：本机判定
                    if (isMouseSpec && down && spec != null
                        && spec.MatchesMouse(ev) && spec.ModifiersMatch())
                    {
                        Debug("自由视角中，本机热键鼠标退出：" + spec);
                        _localHotkeyHandledAt = DateTime.UtcNow;   // 让目标会话忽略回声
                        _captured = false;
                        InputMode = ConsoleInputMode.Pointer;
                        Dispatcher.BeginInvoke(new Action(() => ReleaseCapture("本机热键退出")));
                        return false;
                    }

                    // ② 固定应急出口：XButton1（鼠标侧键4）始终可退出。
                    //    热键配成键盘时键盘可能直达 RDP，这条是唯一可靠的本机出口。
                    if (ev.IsXButton1Down)
                    {
                        Debug("自由视角中，本机应急侧键退出（XButton1）");
                        _localHotkeyHandledAt = DateTime.UtcNow;   // 让目标会话忽略回声
                        _captured = false;
                        InputMode = ConsoleInputMode.Pointer;
                        Dispatcher.BeginInvoke(new Action(() => ReleaseCapture("本机应急侧键退出")));
                        return false;
                    }

                    // 热键鼠标按钮不注入游戏，避免重复触发
                    if (isMouseSpec && spec != null && (spec.MatchesMouse(ev)
                        || (spec.MouseButton == "X1" && ev.Message == 0x020C)))
                        return false;
                }

                // ── 硬兜底（与 RDP/热键完全无关）──
                // 自由视角下若窗口已不是前台，说明用户在别处；
                // 这里直接解锁并放行，避免"进了出不来"。
                // 任何真实鼠标事件都算"有人在操作"，刷新看门狗
                if (_captured) _lastInputAt = DateTime.UtcNow;

                if (_captured && _ownerWindow != null && !_ownerWindow.IsActive)
                {
                    _captured = false;
                    InputMode = ConsoleInputMode.Pointer;
                    // 立即解除约束，不等 UI 线程
                    try { CursorClip.Release(); } catch { }
                    Dispatcher.BeginInvoke(new Action(() => ReleaseCapture("窗口不在前台（兜底）")));
                    return false;
                }
                if (_disposed) { _captured = false; return false; }
                if (IsConfiguredHotkeyMouse(ev))
                {
                    // 该按钮已分配给 SoulLink 模式开关，不再交给 RDP 或游戏注入。
                    // 这里只吞掉热键按钮自己的 down/up，不转换成任何其他鼠标键。
                    return _captured && _activeState == 0;
                }
                if (ev.IsInjected) return false;
                if (_captured && _disposed) { _captured = false; return false; }
                if (_captured && _ownerWindow != null && !_ownerWindow.IsActive)
                {
                    _captured = false;
                    InputMode = ConsoleInputMode.Pointer;
                    Dispatcher.BeginInvoke(new Action(() => ReleaseCapture("窗口失焦")));
                    return false;
                }
                if (!_captured && CaptureOnClick)
                {
                    var rect = ViewportScreenRect();
                    if (rect == null) return false;
                    bool inside = ev.X >= rect.Value.left && ev.X < rect.Value.left + rect.Value.width
                               && ev.Y >= rect.Value.top && ev.Y < rect.Value.top + rect.Value.height;
                    // ★ 这里**绝不能吞移动事件**。
                    //   实测：WH_MOUSE_LL 里 return true 会让 Windows 连本机光标一起冻住
                    //   （光标停在原地、外面也进不来）。挡鼠标交给上层的 Popup 保护层去做，
                    //   Popup 是真实窗口，光标可以自由移动。
                    //
                    //   这个分支只负责"点击解锁"：第一次左键按下时把控制权交给 RDP。
                    if (!_pointerCaptured && inside && ev.Message == 0x0201)
                    {
                        _pointerCaptured = true;
                        PointerCaptureEnabled = true;
                        Dispatcher.InvokeAsync(GiveKeyboardFocus);
                        InputActivity?.Invoke(this, "键鼠已捕获 · 移出画面即释放");
                        Debug("钩子检测到视口内左键按下 → 请求解除保护层");
                        // 通知外层把 Popup 收起来（保护层可能因 z 序/透明没收到自己的点击事件）
                        try { ShieldReleaseRequested?.Invoke(this); } catch { }
                        return false;
                    }
                    // 其余事件一律放行（含移动），由 Popup 保护层负责不送到 RDP
                    return false;
                }
                // 原生相对模式由 RDP 自己捕获窗口内鼠标并发送位移；
                // 不再吞掉移动，也不再重复调用 moverel。
                if (_nativeRelativeMouse && _captured) return false;

                // ── 未锁定：放行，交给 RDP 自己处理 ──
                if (!_captured)
                    return false;

                // ── 自由视角：只向目标会话喂相对鼠标输入，不再走 RDP 绝对坐标 ──
                // 游戏自行决定转视角、显示光标与处理菜单，我们不造虚拟光标。
                if (ev.IsMove)
                {
                    // ── 自由视角转视角 ──────────────────────────────────────────
                    //
                    // ★ 为什么必须在**目标会话内部**做相对位移：
                    //   游戏读 Raw Input（原始输入），只认硬件式的相对移动。
                    //   RDP 原生通道送过去的是绝对坐标（SetCursorPos 语义），
                    //   游戏视角完全不转 —— 这就是"视角无法转动"的根因。
                    //   唯一有效的办法是让 RelayAgent 在那个会话里调 mouse_event(MOVE)，
                    //   和人在那台机器上真实转鼠标完全等价。
                    //
                    // ★ 光标：我们**不碰**。
                    //   转视角时游戏自己隐藏光标（它认为鼠标正在被"锁定"），
                    //   任务/地图时游戏自己显示光标 —— 这是真实状态。
                    //   之前用 ClipCursor / Cursor.Hide 造出来的都是假光标，用户明确否决。
                    //
                    // ★ 不做任何"这个场景该不该转"的判断。
                    //
                    //   用户明确：转不转是**游戏自己**决定的（任务界面不该转、大地图该转，
                    //   游戏比我们清楚）。我们唯一要做的是把**真实鼠标数据**喂给它，
                    //   做法和人在那台机器上真实移动鼠标完全一致：
                    //     · 相对位移 → mouse_event(MOVE, dx, dy)（Raw Input 认这个）
                    //     · 按键     → mouse_event(DOWN/UP)
                    //   全部在目标会话内部执行（RelayAgent），所以游戏收到的是
                    //   完整的真实输入流，它自己决定转视角还是拖拽、要不要显示光标。
                    // Raw Input 生效时：位移已由 OnRawDelta 处理，
                    // 这里只吞掉旧式移动，避免 RDP 再发一份绝对坐标。
                    if (_rawInput?.IsActive == true) return true;

                    // 回退路径：Raw Input 不可用时才用坐标差分。
                    if (_lastMoveX == int.MinValue) { _lastMoveX = ev.X; _lastMoveY = ev.Y; return true; }
                    int dwx = ev.X - _lastMoveX;
                    int dwy = ev.Y - _lastMoveY;
                    _lastMoveX = ev.X; _lastMoveY = ev.Y;
                    if (dwx == 0 && dwy == 0) return true;
                    LookQueue.EnqueueMove(dwx, dwy);
                    return true;
                }

                // ── 按键：同样走注入，让游戏收到完整的真实输入流 ──
                //
                // ★ 为什么不能放行给 RDP 原生通道：
                //   那条路发的是**绝对坐标**语义，而我们的光标是由相对位移驱动的，
                //   两边位置对不上，点击会落错地方。走注入才能保证 down/up
                //   发生在游戏认为的光标位置上。
                if (ev.IsLeftDown || ev.IsRightDown || ev.IsMiddleDown)
                {
                    if (!IsRelativeInputReady)
                    {
                        _captured = false;
                        InputMode = ConsoleInputMode.Pointer;
                        Dispatcher.BeginInvoke(new Action(() => ReleaseCapture("相对输入通道断开")));
                        return false;
                    }
                    string button = ButtonNameOf(ev);
                    _injectedButtons.Add(button);
                    LookQueue.EnqueueButton(button, true);
                    return true;
                }
                if (ev.IsLeftUp || ev.IsRightUp || ev.IsMiddleUp)
                {
                    string button = ButtonNameOf(ev);
                    if (Input?.Relay.IsConnected == true)
                    {
                        LookQueue.EnqueueButton(button, false);
                        _injectedButtons.Remove(button);
                        return true;
                    }
                    _injectedButtons.Clear();
                    _captured = false;
                    InputMode = ConsoleInputMode.Pointer;
                    Dispatcher.BeginInvoke(new Action(() => ReleaseCapture("相对输入通道断开")));
                    return false;
                }

                // 滚轮也注入当前远端位置，不能让 RDP 再补一份本机绝对坐标。
                if (ev.IsWheel && IsRelativeInputReady)
                {
                    LookQueue.EnqueueWheel(ev.WheelDelta);
                    return true;
                }
                return true;
            }
            catch (Exception ex)
            {
                // 低级钩子中不等待 UI、不写文件。但**必须**解除光标约束，否则鼠标被困。
                _captured = false;
                InputMode = ConsoleInputMode.Pointer;
                try { CursorClip.Release(); } catch { }
                try { Dispatcher.BeginInvoke(new Action(() => ReleaseCapture("回调异常：" + ex.GetType().Name))); } catch { }
                return false;
            }
        }

        private static bool IsConfiguredHotkeyMouse(MouseHookEvent ev)
        {
            var spec = HotkeySpec.Parse(App.Config.Settings.LockHotkey);
            if (spec == null || !spec.IsMouse) return false;
            return spec.MouseButton switch
            {
                "Left" => ev.IsLeftDown || ev.IsLeftUp,
                "Right" => ev.IsRightDown || ev.IsRightUp,
                "Middle" => ev.IsMiddleDown || ev.IsMiddleUp,
                "X1" => ev.Message is 0x020B or 0x020C && ev.IsXButton1,
                "X2" => ev.Message is 0x020B or 0x020C && !ev.IsXButton1,
                _ => false
            };
        }

        private async Task InjectClickAsync(double uiX, double uiY, string button = "left")
        {
            var input = Input;
            if (input == null) return;
            try
            {
                var (rx, ry) = input.Mapper.ToRemote(uiX, uiY);
                Debug("注入点击 UI(" + (int)uiX + "," + (int)uiY + ") → 远端(" + rx + "," + ry + ") " + button);
                await input.ClickUiAsync(uiX, uiY, button).ConfigureAwait(false);

                // 点完立刻回读远端光标，确认真的落在那
                var after = await input.Relay.CursorAsync().ConfigureAwait(false);
                if (after != null)
                    Debug("  远端光标实际 = " + after.X + "," + after.Y);
            }
            catch (Exception ex) { Debug("注入点击失败：" + ex.Message); }
        }

        /// <summary>把钩子事件映射成注入通道认识的按钮名。</summary>
        private static string ButtonNameOf(MouseHookEvent ev)
        {
            if (ev.IsRightDown || ev.IsRightUp) return "right";
            if (ev.IsMiddleDown || ev.IsMiddleUp) return "middle";
            return "left";
        }

        /// <summary>页面重新显示时调用：重新确认可见性并补装钩子。</summary>
        public void OnShown()
        {
            try
            {
                AttachOwnerWindow();
                InstallMouseHook();
                EnsureToggleHotkey();
                RefreshViewport();
                GiveKeyboardFocus();
            }
            catch { }
        }

        /// <summary>
        /// 把键盘焦点交给 RDP 控件。
        ///
        /// ★ 只调 AxHost.Focus() 是不够的（实测）：
        ///   Win32 层面焦点会落在 WPF 的 IHWindowClass（Input Capture Window）上，
        ///   按键被 WPF 截走后不会转给 RDP 控件，游戏里按 ESC 就没反应。
        ///
        /// 正确做法：
        ///   1. 让 WPF 别插手键盘（KeyboardNavigation 关掉、不设 Focusable）
        ///   2. 直接对 OCX 的 HWND 调原生 SetFocus
        /// </summary>
        public void GiveKeyboardFocus()
        {
            try
            {
                if (CaptureOnClick && !_pointerCaptured && !_captured) return;
                if (!IsVisible || _ax == null || !_ax.IsHandleCreated) return;

                _host.Focusable = false;

                var form = System.Windows.Forms.Control.FromChildHandle(_ax.Handle);
                IntPtr target = _ax.ContainsFocus
                    ? _ax.Handle
                    : (form?.Handle ?? _ax.Handle);

                // 先进 WinForms 层拿焦点，再对 OCX 句柄原生 SetFocus
                _ax.Focus();
                SetFocus(target);

                Debug("键盘焦点 -> RDP 控件 (ax.ContainsFocus=" + _ax.ContainsFocus
                      + " hwnd=" + target + ")");
            }
            catch (Exception ex) { Debug("设置键盘焦点失败：" + ex.Message); }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        private bool SetNativeRelativeMouse(bool enabled)
        {
            if (_ocx == null) return false;
            try
            {
                dynamic settings = _ocx.AdvancedSettings6;
                settings.RelativeMouseMode = enabled;
                bool actual = settings.RelativeMouseMode;
                _nativeRelativeMouse = actual;
                Debug("RDP 原生相对鼠标模式=" + actual + " requested=" + enabled
                    + " connected=" + IsConnected);
                return actual == enabled;
            }
            catch (Exception ex)
            {
                _nativeRelativeMouse = false;
                Debug("设置 RDP 原生相对鼠标失败：" + ex.Message);
                return false;
            }
        }

        public bool IsLooking => _captured;
        private bool IsRelativeInputReady => Input?.Relay.IsConnected == true && _inputUsable;

        private int _hotkeyConfigVersion;

        private async void SyncRelayHotkey(string reason)
        {
            var input = Input;
            if (input == null || !input.Relay.IsConnected) return;
            int version = System.Threading.Interlocked.Increment(ref _hotkeyConfigVersion);
            string label = ToggleHotkey;
            try
            {
                // 热键配置不需要 trace；避免每个按键都同步写目标用户日志。
                var response = await input.Relay.ConfigureHotkeyAsync(label, trace: false);
                if (version != _hotkeyConfigVersion || !ReferenceEquals(Input, input)) return;
                Debug("目标热键配置(" + reason + ")=" + (response?.Message ?? "无响应") + " · " + ToggleHotkey);
                if (response?.Ok != true)
                    InputActivity?.Invoke(this, "目标热键接收器未就绪，可暂时使用工具栏切换");
            }
            catch (Exception ex) { Debug("目标热键配置异常：" + ex.Message); }
        }

        /// <summary>本机刚处理过热键的时间点，用于让目标会话忽略同一次按键。</summary>
        private DateTime _localHotkeyHandledAt = DateTime.MinValue;

        private void OnRelayHotkey()
        {
            // ★ 同一次侧键会被两条通道各收一次：
            //   本机钩子（立即生效）+ A2 会话接收器（IPC 转发）。
            //   本机处理过之后，目标会话的回声必须在短时间内忽略，
            //   否则会出现"退出后马上又被锁回去"（按钮闪一下）。
            if ((DateTime.UtcNow - _localHotkeyHandledAt).TotalMilliseconds < 400)
            {
                Debug("忽略目标会话热键回声（本机刚处理过）");
                return;
            }
            // 锁定状态下的退出无条件放行；只有"进入"才需要当前控制台资格。
            if (!_captured && !CanUseToggleHotkey)
            {
                Debug("丢弃非当前控制台热键事件（非锁定态）");
                return;
            }
            Debug("收到目标会话热键事件 · UI线程=" + Dispatcher.CheckAccess()
                + " connected=" + IsConnected + " input=" + _inputUsable + " captured=" + _captured);
            OnToggleHotkey();
        }

        private void SetStatus(string text)
        {
            try { Dispatcher.InvokeAsync(() => StatusChanged?.Invoke(this, text)); } catch { }
        }

        /// <summary>把连接过程的细节落到 %LOCALAPPDATA%\SoulLink\rdp-debug.log，方便排查。</summary>
        internal static void Debug(string message)
        {
            AsyncLog.Append(
                System.IO.Path.Combine(ConfigStore.ConfigDir, "rdp-debug.log"),
                DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
        }

        /// <summary>创建 OCX（幂等）。</summary>
        private void EnsureHost()
        {
            if (_ax != null) return;

            Exception? last = null;
            foreach (var clsid in CandidateClsids)
            {
                try
                {
                    var ax = new RdpAxHost(clsid);
                    _host.Child = ax;
                    ax.EnsureCreated();
                    if (ax.Ocx == null) { last = new InvalidOperationException("控件创建后 OCX 为空（" + clsid + "）"); continue; }
                    _ax = ax;
                    _ocx = ax.Ocx;
                    LogBus.Info("RDP 控件就绪", "CLSID " + clsid);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }
            throw new InvalidOperationException(
                "无法创建 RDP 控件。系统可能缺少远程桌面 ActiveX 组件。\n" + (last?.Message ?? ""), last);
        }

        /// <summary>
        /// 连接到指定账号的会话。会按设置决定是否先 tsdiscon。
        /// </summary>
        public async Task<(bool ok, string message)> ConnectAsync(Account account, AppSettings settings, int sessionId)
        {
            // 已经连着就别再连：同一会话的第二个客户端会把第一个踢下线
            if (IsConnected)
            {
                Debug("已有连接，跳过重复 ConnectAsync");
                return (true, "已连接");
            }

            int connection = System.Threading.Interlocked.Increment(ref _connectionGeneration);
            _ = StopInputAsync();

            Account = account;
            CaptureOnClick = App.Config.Settings.RdpCaptureOnClick;
            if (CaptureOnClick) ReleasePointerCapture();
            try
            {
                EnsureHost();
            }
            catch (Exception ex)
            {
                App.AppendErrorLog("RdpConsole.EnsureHost", ex);
                SetStatus("控件不可用");
                return (false, ex.Message);
            }

            // ── 0. 地址：配置优先，其次探测（环回地址无法靠会话号推算，见 HANDOFF 10.6）──
            bool autoHost = string.IsNullOrWhiteSpace(account.RdpHost);
            string? probed = null;
            if (autoHost)
            {
                SetStatus("探测可用的 RDP 地址 …");
                probed = await RdpHostProbe.ProbeAsync(sessionId, null).ConfigureAwait(true);
            }

            string host = account.ResolveHost(sessionId, probed);
            string user = account.EffectiveRdpUserName;
            string password = ConfigStore.GetPassword(account);

            SetStatus("准备会话 " + sessionId + " …");
            LogBus.Info("连接 RDP " + host + " ← " + user,
                "会话 " + sessionId + (autoHost ? (probed == null ? "｜自动探测（没找到）" : "｜自动探测") : "｜手动配置"));

            // ── 1. 会话准备 ──
            // 只有目标**还连着（Active）**时才需要踢下线，否则 RDP 会新建空会话。
            // 已经 Disc 的会话绝对不能再 tsdiscon —— 那会把它直接注销掉（HANDOFF 10.7）。
            if (account.RdpAutoDisconnect && sessionId > 0)
            {
                var (prepOk, prepMsg) = await SessionPrepare.DisconnectAsync(sessionId).ConfigureAwait(true);
                Debug("SessionPrepare: " + prepMsg);
                LogBus.Write(prepOk ? LogLevel.Info : LogLevel.Warn, "会话准备：" + prepMsg);
                if (!prepOk) SetStatus(prepMsg);
            }

            // ── 2. 配置控件（顺序很重要）──
            try
            {
                Configure(account, settings, host, user, password);
            }
            catch (Exception ex)
            {
                App.AppendErrorLog("RdpConsole.Configure", ex);
                SetStatus("配置失败");
                return (false, "配置 RDP 控件失败：" + ex.Message);
            }

            // ── 2.5 挂连接事件（比轮询 Connected 准确得多，1Remote 就是这么做的）──
            AttachRdpEvents();

            // ── 3. 连接 ──
            try
            {
                SetStatus("正在连接 " + host + " …");
                try
                {
                    var c = (System.Windows.Forms.Control)_ax!;
                    Debug("控件尺寸=" + c.Width + "x" + c.Height + " created=" + c.IsHandleCreated
                          + " visible=" + c.Visible + " 宿主尺寸=" + ActualWidth + "x" + ActualHeight
                          + " 宿主可见=" + IsVisible);
                }
                catch (Exception ex) { Debug("尺寸读取失败: " + ex.Message); }


                _ocx!.Connect();
                Debug("Connect() 返回");
            }
            catch (Exception ex)
            {
                Debug("Connect() 抛异常: " + ex.Message);
                SetStatus("连接失败");
                return (false, "Connect() 失败：" + ex.Message);
            }

            // ── 4. 等它连上 ──
            var sw = Stopwatch.StartNew();
            int lastReason = -1;
            int stableHits = 0;
            while (sw.ElapsedMilliseconds < 30000)
            {
                await Task.Delay(300).ConfigureAwait(true);
                if (_disposed) return (false, "已取消");

                // 诊断：把扩展断开原因记下来（0 = 没有错误）
                try
                {
                    int reason = (int)_ocx!.ExtendedDisconnectReason;
                    if (reason != lastReason)
                    {
                        lastReason = reason;
                        string desc = "";
                        try { desc = (string)_ocx.GetErrorDescription((uint)reason, (uint)(int)_ocx.Connected); } catch { }
                        Debug("sec=" + (sw.ElapsedMilliseconds / 1000) + " connected=" + (int)_ocx.Connected
                              + " reason=" + reason + " desc=" + desc);
                    }
                }
                catch (Exception ex) { Debug("读状态失败: " + ex.Message); }

                // ★ 必须连续两次观测到 connected==1 才算真连上。
                //   实测：连接瞬间会有一个「已连上又立刻断开」的假信号（服务端会回一句「会话已结束」），
                //   只看一次会把失败当成功。
                {
                    int curState = 0;
                    try { curState = (int)_ocx!.Connected; } catch { }

                    if (curState == 1)
                    {
                        stableHits++;
                        if (stableHits >= 2)
                        {
                            RdpHostProbe.Remember(sessionId, host);
                            _hasConnected = true;

                            ApplyDpiScale();
                            GiveKeyboardFocus();
                            ConnectionChanged?.Invoke(this, true);
                            // 自动登录会新建会话：连接成功后必须重新解析真实会话号，不能继续用 0。
                            // ★ 必须轮询等会话出现：刚 Connect() 成功时，
                            //   RDP 服务端可能还没把新会话登记到会话列表里，
                            //   立刻查询会拿不到 → sessionId 仍是 0 →
                            //   BeginInput(0) 报「无效的会话 ID：0」（用户实测）。
                            int activeSessionId = sessionId;
                            for (int attempt = 0; attempt < 20; attempt++)
                            {
                                try
                                {
                                    var activeSessions = await SessionService.QueryAsync().ConfigureAwait(true);
                                    var activeSession = SessionService.Find(activeSessions, account.UserName);
                                    if (activeSession != null && activeSession.Id > 0)
                                    {
                                        activeSessionId = activeSession.Id;
                                        Debug("连接后真实会话号=" + activeSessionId
                                              + (sessionId != activeSessionId ? "（由 " + sessionId + " 更新）" : "")
                                              + (attempt > 0 ? "（等待 " + attempt + " 次）" : ""));
                                        break;
                                    }
                                }
                                catch (Exception ex) { Debug("回读会话号失败（第 " + attempt + " 次）: " + ex.Message); }

                                if (attempt == 0) Debug("会话尚未登记，开始轮询等待…");
                                await Task.Delay(500).ConfigureAwait(true);
                            }
                            if (activeSessionId <= 0)
                                Debug("警告：等待 10 秒后仍未拿到会话号，注入代理可能无法投送");

                            // ★ 账号校验：环回地址与会话的对应关系随启动顺序变化，
                            //   地址配错时连接仍会"成功"，但连到的是别人 —— 表现为
                            //   "注入代理投送失败 / 无法进入自由视角"，且没有任何提示。
                            //   这里连上后按实际会话号反查登录用户，与配置比对。
                            try
                            {
                                var verifySessions = await SessionService.QueryAsync().ConfigureAwait(true);
                                SessionInfo? actual = null;
                                foreach (var s in verifySessions)
                                    if (s.Id == activeSessionId) { actual = s; break; }
                                string actualUser = actual?.UserName ?? "";
                                bool mismatch = !string.IsNullOrWhiteSpace(actualUser)
                                    && !string.Equals(actualUser, account.UserName, StringComparison.OrdinalIgnoreCase);

                                if (mismatch)
                                {
                                    // 定位目标账号真正所在的会话，自动纠正配置的地址。
                                    SessionInfo? target = SessionService.Find(verifySessions, account.UserName);
                                    string hint = target != null
                                        ? "该账号当前在会话 " + target.Id + "，地址应为 127.0.0." + target.Id
                                        : "该账号当前未登录";
                                    Debug("账号不匹配：配置=" + account.UserName + " 实际=" + actualUser
                                          + " 地址=" + host + " 会话=" + activeSessionId + " · " + hint);
                                    LogBus.Warn("RDP 地址与账号不匹配",
                                        host + " 实际登录的是 " + actualUser + "，不是 " + account.UserName + " · " + hint);

                                    // 自动写回正确地址，下次连接直接生效
                                    if (target != null && target.Id > 0)
                                    {
                                        string fixedHost = "127.0.0." + target.Id;
                                        try
                                        {
                                            account.RdpHost = fixedHost;
                                            ConfigStore.Save(App.Config);
                                            Debug("已自动修正地址：" + host + " → " + fixedHost);
                                        }
                                        catch (Exception ex) { Debug("自动修正地址失败：" + ex.Message); }

                                        SetStatus("地址已自动更正为 " + fixedHost + " · 请点「重新连接」");
                                        LogBus.Ok("已自动更正 RDP 地址",
                                            account.Caption + " → " + fixedHost + "（会话 " + target.Id + "）");
                                        return (false, "RDP 地址与账号不符，已自动更正为 " + fixedHost
                                                     + "，请点「重新连接」。");
                                    }
                                    SetStatus("地址不匹配 · " + host + " 实际是 " + actualUser);
                                }
                            }
                            catch (Exception ex) { Debug("账号校验失败: " + ex.Message); }

                            SetStatus("已连接 · 会话 " + activeSessionId + " · " + account.UserName);
                            LogBus.Ok("RDP 已连上 " + account.Caption,
                                "地址 " + host + " · 耗时 " + sw.ElapsedMilliseconds + " ms");
                            if (connection == _connectionGeneration)
                                BeginInput(activeSessionId, account, password);
                            return (true, "已连接（" + sw.ElapsedMilliseconds + " ms）");
                        }
                    }
                    else stableHits = 0;
                }
            }

            RdpHostProbe.Forget(sessionId, host);
            ConnectionChanged?.Invoke(this, false);
            SetStatus("连接超时");
            return (false, "连接超时（30 秒）。地址 " + host + " 可能无法落到该会话；"
                         + "请在账号设置里手动指定 RDP 地址（例如 127.0.0.3），或用 1Remote 里能连的那个地址。");
        }

        private void Configure(Account account, AppSettings settings, string host, string user, string password)
        {
            var ocx = _ocx!;
            Debug("── Configure 开始 host=" + host + " user=" + user);

            // ── 服务器信息 ──
            ocx.Server = host;
            ocx.UserName = user;
            try { ocx.Domain = ""; } catch (Exception ex) { Debug("Domain 设置失败：" + ex.Message); }
            ocx.Domain = "";
            // 账号级设置优先，没配（=0）才用全局
            int depth = account.RdpColorDepth > 0 ? account.RdpColorDepth
                : (settings.RdpColorDepth > 0 ? settings.RdpColorDepth : 32);
            ocx.ColorDepth = depth;

            // ── 静态设置 ──
            dynamic adv2 = ocx.AdvancedSettings2;
            adv2.RDPPort = account.RdpPort > 0 ? account.RdpPort : 3389;
            adv2.EncryptionEnabled = 1;
            adv2.SmartSizing = false;          // 关闭：跟随窗口缩放会让远端分辨率协商异常
            // ★ 必须是 true：否则连接后控件拿不到键盘焦点，按键根本进不去。
            //   （之前设成 false 是怕抢焦点，但中控台就是要抢——这是它的本职工作）
            adv2.GrabFocusOnConnect = true;

            // ── 音频、组合键和设备重定向：默认关闭设备共享，账号可单独开启 ──
            adv2.RedirectClipboard = account.RdpRedirectClipboard;
            adv2.RedirectPrinters = account.RdpRedirectPrinters;
            adv2.RedirectDrives = account.RdpRedirectDrives;
            adv2.RedirectSmartCards = account.RdpRedirectSmartCards;
            // 本机 AdvancedSettings2 有 RedirectPorts，没有旧版 RDP 文件的 RedirectComPorts。
            adv2.RedirectPorts = account.RdpRedirectPorts;
            try
            {
                dynamic secured = ocx.SecuredSettings3;
                secured.AudioRedirectionMode = Clamp(account.RdpAudioMode, 0, 2);
                // 普通游戏键盘保持 RDP 原生转发；目标会话接收器只拦配置的热键。
                secured.KeyboardHookMode = Clamp(account.RdpKeyboardHookMode, 0, 2);
            }
            catch (Exception ex) { Debug("音频/组合键属性失败: " + ex.Message); }

            // ── 连接栏 / 全屏 ──
            try
            {
                dynamic a6 = ocx.AdvancedSettings6;
                // 已实测：RelativeMouseMode 连接后可读、写入返回 E_FAIL；
                // 连接前强开会在部分环境让热键/输入链路异常，不再默认改写。
                try { Debug("RelativeMouseMode 当前=" + (bool)a6.RelativeMouseMode); } catch { }
                a6.PublicMode = false;
                a6.DisplayConnectionBar = false;

                // ★ 必须为 0。
                //   =1 时控件会自己处理「退出全屏」，**把 ESC 当成自己的命令**，
                //   导致游戏里按 ESC 关不掉地图（实测反馈）。
                //   我们是窗口内嵌，不需要控件的全屏处理。
                a6.ContainerHandledFullScreen = 0;
            }
            catch { }

            try
            {
                dynamic a7 = ocx.AdvancedSettings7;
                a7.ConnectToAdministerServer = false;
            }
            catch { }

            try
            {
                dynamic a8 = ocx.AdvancedSettings8;
                a8.BitmapPersistence = 0;      // 修错误码 4360
                a8.CachePersistenceActive = 0;
                a8.AudioCaptureRedirectionMode = account.RdpAudioCapture;
                if (account.RdpNetworkConnectionType >= 1 && account.RdpNetworkConnectionType <= 6)
                    a8.NetworkConnectionType = account.RdpNetworkConnectionType;
                if (account.RdpAudioMode == 0)
                    a8.AudioQualityMode = 0;
            }
            catch (Exception ex) { Debug("录音/网络/音频质量属性失败: " + ex.Message); }

            try
            {
                dynamic a9 = ocx.AdvancedSettings9;
                a9.EnableCredSspSupport = true;
                a9.AuthenticationLevel = 0;    // 证书问题也照连
                try { a9.EnableAutoReconnect = true; } catch (Exception ex) { Debug("自动重连属性: " + ex.Message); }
                // 预设只影响 RDP 视觉特效，不覆盖用户已经选定的色深。
                int flags = account.RdpLowBandwidth ? 0x6F : account.RdpPerformanceMode switch
                {
                    1 => 0x6F,       // 壁纸、拖动、动画、主题、光标阴影/闪烁
                    2 => 0x18F,      // 均衡 + 字体平滑、桌面合成
                    3 => 0x180,      // 高画质：字体平滑 + 桌面合成
                    _ => 0           // 自动，交给 RDP 协商
                };
                a9.PerformanceFlags = flags;
                Debug("性能: Network=" + account.RdpNetworkConnectionType + " PerformanceFlags=0x" + flags.ToString("X"));
            }
            catch (Exception ex) { Debug("性能标志设置失败: " + ex.Message); }

            _scaleFactor = GetDpiScaleFactor();
            Debug("系统 DPI 缩放 = " + _scaleFactor + "%");

            // ── 分辨率（必须在 Connect 之前，范围 200~8192）──
            int w = Clamp(account.RdpWidth > 0 ? account.RdpWidth
                        : (settings.RdpWidth > 0 ? settings.RdpWidth : 1280), 200, 8192);
            int h = Clamp(account.RdpHeight > 0 ? account.RdpHeight
                        : (settings.RdpHeight > 0 ? settings.RdpHeight : 720), 200, 8192);
            ocx.DesktopWidth = w;
            ocx.DesktopHeight = h;

            // SmartSizing 只在“拉伸”模式下开；跟随窗口模式必须关，
            // 否则远端分辨率跟着变 + 再拉一次，画面会糊（1Remote 的做法）。
            int scaleMode = account.RdpScaleMode > 0 || account.RdpScaleMode == 0
                ? account.RdpScaleMode
                : settings.RdpScaleMode;
            bool stretch = scaleMode == 1 || scaleMode == 3;
            adv2.SmartSizing = stretch;
            Debug("Desktop = " + w + "x" + h + " 色深=" + depth + " Mode=" + scaleMode + " SmartSizing=" + stretch);

            // ── 密码 ──
            // 实测：目标已登录时根本不需要密码；目标已注销时 Connect() 也会用 UserName 自动登录。
            // ClearTextPassword 在 .NET Core 上拿不到（见 HANDOFF 10.2），不设也不影响。
            if (!string.IsNullOrEmpty(password))
            {
                bool ok;
                string pwdDetail;
                try
                {
                    // 与 1Remote 相同：IMsTscNonScriptable.ClearTextPassword，只写、不能回读密码。
                    // 1Remote 的接口是 IUnknown（不是 Dual），ClearTextPassword 通过 vtable 调用。
                    ((IMsTscNonScriptable)ocx).ClearTextPassword = password;
                    ok = true;
                    pwdDetail = "IMsTscNonScriptable.ClearTextPassword 已设置";
                }
                catch (Exception ex)
                {
                    ok = false;
                    pwdDetail = ex.Message;
                    Debug("ClearTextPassword 设置失败：" + ex.Message);
                }
                Debug("ClearTextPassword ok=" + ok + " · " + pwdDetail);
            }
            else
            {
                Debug("没有保存密码（目标已登录时不影响）");
            }

            // 回读校验：写错属性时能第一时间发现
            try
            {
                Debug("回读: Server=" + (string)ocx.Server + " User=" + (string)ocx.UserName
                      + " Desktop=" + (int)ocx.DesktopWidth + "x" + (int)ocx.DesktopHeight
                      + " Port=" + (int)ocx.AdvancedSettings2.RDPPort
                      + " Audio=" + (int)ocx.SecuredSettings3.AudioRedirectionMode
                      + " Keys=" + (int)ocx.SecuredSettings3.KeyboardHookMode
                      + " Capture=" + (bool)ocx.AdvancedSettings8.AudioCaptureRedirectionMode
                      + " Network=" + (int)ocx.AdvancedSettings8.NetworkConnectionType
                      + " Perf=" + (int)ocx.AdvancedSettings9.PerformanceFlags
                      + " Clipboard=" + (bool)ocx.AdvancedSettings2.RedirectClipboard
                      + " Printers=" + (bool)ocx.AdvancedSettings2.RedirectPrinters
                      + " Drives=" + (bool)ocx.AdvancedSettings2.RedirectDrives
                      + " SmartCards=" + (bool)ocx.AdvancedSettings2.RedirectSmartCards
                      + " Ports=" + (bool)ocx.AdvancedSettings2.RedirectPorts);
            }
            catch (Exception ex) { Debug("回读失败: " + ex.Message); }

            Debug("── Configure 完成");
        }
        private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);

        // ── RDP 事件 ─────────────────────────────────────────────────────────

        private void AttachRdpEvents()
        {
            if (_eventsAttached || _ax == null || _ocx == null) return;
            _eventsAttached = true;
            try
            {
                // AxHost 的 ActiveX 事件在 .NET Core 下不能直接 +=，
                // 这里用轮询做兜底，但把"连上"的判定改得更严格（下面 ConnectAsync 里两次采样）。
                // 1Remote 用 OnConnected 回调是因为它有 aximp 生成的包装类型；
                // 我们是手写 AxHost，没有事件接口，只能轮询——但已经比只采样一次稳。
                Debug("RDP 事件：手写 AxHost 无事件接口，使用加固轮询");
            }
            catch { }
        }

        private bool _eventsAttached;

        /// <summary>
        /// 连接成功后设置远端 DPI 缩放。
        /// 1Remote 在 RdpInitDisplay（连接前）就设，但我们实测连接前必定 E_UNEXPECTED，
        /// connect 之后设才成功——用 vtable 直调 IMsRdpExtendedSettings::set_Property。
        /// </summary>
        private void ApplyDpiScale()
        {
            // 实测结论（见 HANDOFF 12.4）：IMsRdpExtendedSettings::set_Property 在本机
            // 对 DesktopScaleFactor / DeviceScaleFactor 一律返回 E_UNEXPECTED，
            // 无论连接前后、无论 Stdcall 还是 Winapi 约定。
            // 接口本身是通的（QI 成功、slot 存在），是 RDP 控件自身不接受这两个属性。
            //
            // 所以 DPI 不做远端设置：远端始终按 100% 渲染，
            // 宿主侧用 CoordinateMapper 做「UI 像素 ↔ 远端像素」换算来消化本机 150% 缩放。
            Debug("本机 DPI=" + _scaleFactor + "%，远端保持 100%（属性设置不受支持，见 HANDOFF 12.4）");
        }

        /// <summary>读取系统 DPI 缩放（100 = 100%）。</summary>
        private static uint GetDpiScaleFactor()
        {
            try
            {
                using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
                double dpi = g.DpiX;
                if (dpi <= 0) return 100;
                uint scale = (uint)Math.Round(dpi / 96.0 * 100.0);
                // RDP 只接受 100~500 的档位
                if (scale < 100) scale = 100;
                if (scale > 500) scale = 500;
                return scale;
            }
            catch { return 100; }
        }

        /// <summary>
        /// 窗口尺寸变化后热更新远端分辨率（照 1Remote 的 ReSizeRdpToControlSize）。
        /// ★ 拖动窗口时鼠标按住会连续触发，必须等松手再改，否则闪屏。
        /// </summary>
        public async void RequestResize(Version _ = default)
        {
            try
            {
                if (!_hasConnected || _ocx == null) return;
                if ((Account?.RdpScaleMode ?? App.Config.Settings.RdpScaleMode) != 0) return;
                // 远端分辨率用物理像素，不能使用 DIP，否则 150% DPI 下会缩小成 2/3。
                var rect = ViewportScreenRect();
                if (rect == null) return;

                // 等鼠标松开
                for (int i = 0; i < 20; i++)
                {
                    if (System.Windows.Input.Mouse.LeftButton != System.Windows.Input.MouseButtonState.Pressed) break;
                    await Task.Delay(300).ConfigureAwait(true);
                }

                int w = rect.Value.width;
                int h = rect.Value.height;
                if (w < 200 || h < 200) return;

                // 控件默认宽是 288，不能拿它当目标
                try
                {
                    if (Math.Abs((int)_ocx.DesktopWidth - w) <= 1 &&
                        Math.Abs((int)_ocx.DesktopHeight - h) <= 1) return;
                }
                catch { }

                _ocx.UpdateSessionDisplaySettings(w, h, w, h, 0, _scaleFactor, 100);
                Debug("热更新分辨率 -> " + w + "x" + h + " scale=" + _scaleFactor);
            }
            catch (Exception ex)
            {
                // 0x8000FFFF 在未连接完成时会抛，忽略即可
                Debug("热更新分辨率失败：" + ex.Message);
            }
        }

        // ── 输入注入 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 画面连上后投送注入代理。
        /// 注意：这一步是**可选增强**——失败了画面照样能用（只是不能转视角），不阻塞连接。
        /// </summary>
        private async void BeginInput(int sessionId, Account account, string password)
        {
            if (!InjectEnabled || _disposed || !InputAllowed || System.Threading.Interlocked.CompareExchange(ref _inputStartPending, 1, 0) != 0)
                return;
            await StopInputAsync().ConfigureAwait(true);
            int generation = System.Threading.Interlocked.Increment(ref _inputGeneration);

            CurrentSessionId = sessionId;
            try
            {
                if (!string.IsNullOrWhiteSpace(App.Config.Settings.LockHotkey))
                    _toggleHotkey = App.Config.Settings.LockHotkey;

                var relay = new RelayClient(account.UserName, sessionId);
                var (ok, msg) = await relay.DeployAsync(account, password).ConfigureAwait(true);

                if (!ok)
                {
                    relay.Dispose();
                    Debug("注入代理投送失败：" + msg);
                    InputActivity?.Invoke(this, "注入不可用：" + msg);
                    // ★ 投送失败同样重试：会话刚登录/杀软临时拦截都可能是一过性的
                    ScheduleInputRebuild("投送失败");
                    return;
                }

                if (generation != _inputGeneration || _disposed || !InputAllowed)
                {
                    relay.Dispose();
                    return;
                }
                var input = new SessionInput(relay) { Sensitivity = 1.0 };
                LookSensitivity = App.Config.Settings.LookSensitivity > 0
                    ? App.Config.Settings.LookSensitivity
                    : 4.0;

                // 先设远端尺寸，再按物理视口算比例
                input.Mapper.RemoteWidth = relay.ScreenWidth;
                input.Mapper.RemoteHeight = relay.ScreenHeight;
                Input = input;
                input.Activity += (i, text) => InputActivity?.Invoke(this, text);
                input.Relay.HotkeyPressed += (_, key) =>
                {
                    try { Dispatcher.BeginInvoke(new Action(OnRelayHotkey)); } catch { }
                };

                // ★ 通道断开后自动重建。
                //   原来通道只在"连接成功那一刻"建一次，代理中途挂掉或首次失败后
                //   没有任何自愈能力，只能靠用户手动点「重新连接」——
                //   表现为"按热键没反应 / 无法进入自由视角"（实测多次）。
                input.Relay.StateChanged += (_, up) =>
                {
                    if (up) return;
                    // ★ 只有当前这条通道断开才重试。
                    //   若 Input 已被替换/置空（主动重连、切会话），说明是我们自己
                    //   在停它，不应再触发重建（否则会无限循环）。
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (!ReferenceEquals(Input, input)) return;
                            ScheduleInputRebuild("通道断开");
                        }));
                    }
                    catch { }
                };
                SyncMapperViewport();

                // 等画面真正稳定再探测。刚连上时远端桌面还没切过来，
                // 立刻探测会误判成「不可交互」（实测）。
                await Task.Delay(1500).ConfigureAwait(true);

                var (usable, why) = await input.CheckUsableAsync().ConfigureAwait(true);
                if (!usable)
                {
                    // 再给一次机会：有些会话要 2~3 秒才把桌面切过来
                    await Task.Delay(2000).ConfigureAwait(true);
                    if (ReferenceEquals(Input, input))
                        (usable, why) = await input.CheckUsableAsync().ConfigureAwait(true);
                }

                if (!ReferenceEquals(Input, input)) return;
                if (!InputAllowed)
                {
                    _ = StopInputAsync();
                    return;
                }
                _inputUsable = usable;
                SyncRelayHotkey("代理就绪");

                if (usable)
                {
                    // 通道真正可用 → 清零重试计数
                    ResetRebuildCounter();
                }
                else
                {
                    // 通道建立了但探测不可用（会话桌面还没切过来等），也安排重试
                    ScheduleInputRebuild("探测不可用");
                }

                InputActivity?.Invoke(this, usable ? ("注入就绪 · " + why) : ("注入待命 · " + why));
                Debug("输入探测：" + (usable ? "就绪 · " : "待命 · ") + why);

                // ★ 注入就绪后**不自动进入自由视角**。
                //   第一版就是自动捕获，用户一进去就被困住，只能 Win+D 逃命。
                //   现在默认停留在指针模式，由用户点按钮主动进入。
            }
            catch (Exception ex)
            {
                Debug("建立输入通道异常：" + ex.Message);
                InputActivity?.Invoke(this, "注入异常：" + ex.Message);
                ScheduleInputRebuild("建立异常");
            }
            finally { System.Threading.Interlocked.Exchange(ref _inputStartPending, 0); }
        }

        private async Task StopInputAsync()
        {
            System.Threading.Interlocked.Increment(ref _inputGeneration);
            // ★ 停掉等待中的重建定时器：否则"主动停止"会被当成"通道断开"，
            //   重建 → 再停 → 再重建，形成死循环。
            _rebuildTimer?.Stop();
            _rebuildTimer = null;

            var old = Input;
            Input = null;
            _inputUsable = false;
            if (old == null) return;
            await Task.Run(() =>
            {
                try { old.Dispose(); } catch { }
            }).ConfigureAwait(true);
        }

        /// <summary>
        /// 用**物理像素**同步坐标换算的比例。
        /// 不能用 ActualWidth（DIP）：本机 150% DPI 下会小 1/3，
        /// 换算出来的远端坐标就整体偏大 → 点击位置向右下偏移。
        /// </summary>
        private void SyncMapperViewport()
        {
            var input = Input;
            if (input == null) return;

            var rect = ViewportScreenRect();
            if (rect == null) return;
            var r = rect.Value;

            int remoteW = input.Mapper.RemoteWidth > 0 ? input.Mapper.RemoteWidth : 1280;
            int remoteH = input.Mapper.RemoteHeight > 0 ? input.Mapper.RemoteHeight : 720;

            input.Mapper.Update(remoteW, remoteH, r.width, r.height);
            Debug("坐标视口(物理像素)=" + r.width + "x" + r.height
                  + " 远端=" + remoteW + "x" + remoteH + " · " + input.Mapper.Describe());
        }

        /// <summary>画面尺寸变化时同步坐标换算。</summary>
        public void RefreshViewport()
        {
            var input = Input;
            if (input == null) return;

            // 尺寸变化后立即重算（用物理像素）
            var rect = ViewportScreenRect();
            if (rect != null)
            {
                var r = rect.Value;
                input.Mapper.Update(
                    input.Mapper.RemoteWidth > 0 ? input.Mapper.RemoteWidth : 1280,
                    input.Mapper.RemoteHeight > 0 ? input.Mapper.RemoteHeight : 720,
                    r.width, r.height);
            }

            SyncMapperViewport();
        }

        /// <summary>鼠标在画面内的位置 → 远端坐标。</summary>
        public (int x, int y) MapPoint(double uiX, double uiY)
        {
            if (Input == null) return (-1, -1);
            return Input.Mapper.ToRemote(uiX, uiY);
        }

        /// <summary>断开画面连接（不注销目标会话）。</summary>
        public void Disconnect()
        {
            System.Threading.Interlocked.Increment(ref _connectionGeneration);
            ReleaseCapture("断开画面");
            UninstallMouseHook();
            _ = StopInputAsync();
            try
            {
                if (_ocx != null && IsConnected) _ocx.Disconnect();
            }
            catch { }
            ConnectionChanged?.Invoke(this, false);
        }

        /// <summary>全屏/自适应时更新远端分辨率。</summary>
        public void UpdateDisplaySize(int width, int height)
        {
            try
            {
                if (_ocx == null || !IsConnected) return;
                if (width < 200 || height < 200) return;
                _ocx.UpdateSessionDisplaySettings(width, height, width, height, 0, 100, 100);
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseCapture("释放控制台");
            ReleaseInjectedButtonsAsync();   // 兜底：即使 ReleaseCapture 提前返回也要释放
            UninstallMouseHook();
            DetachOwnerWindow();
            _captureWatch?.Stop();
            ResetLookQueue();
            _ = StopInputAsync();
            try
            {
                if (_ocx != null) _ocx.Disconnect();
            }
            catch { }
            try
            {
                if (_ax != null) { _host.Child = null; _ax.Dispose(); }
            }
            catch { }
            try { _host.Dispose(); } catch { }
            var ocx = _ocx;
            _ocx = null;
            if (ocx != null && System.Runtime.InteropServices.Marshal.IsComObject(ocx))
            {
                try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(ocx); } catch { }
            }
            _ax = null;
        }
    }
}
