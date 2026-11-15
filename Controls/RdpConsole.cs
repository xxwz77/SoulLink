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
using SoulLink.Services;   // IMsRdpExtendedSettings 在 SoulLink.Services 命名空间

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
        private int _accelToken = -1;
        /// <summary>锁定期间补焦点的重试次数（避免无限制调用 Focus）。</summary>
        private int _keyboardFocusRetry = 20;
        private int _captureCenterX;
        private int _captureCenterY;
        private DateTime _lastInject = DateTime.MinValue;

        /// <summary>自由视角下上一次鼠标事件的位置（用于相邻差分）。</summary>
        private int _lastMoveX;
        private int _lastMoveY;

        /// <summary>待发送的转视角累积位移（合并高频事件，避免丢角度）。</summary>
        private int _pendLookX;
        private int _pendLookY;
        private uint _scaleFactor = 100;
        private bool _hasConnected;
        private LowLevelMouseHook? _mouseHook;
        private Window? _ownerWindow;
        private bool _ownerHooked;

        /// <summary>系统热键：开启锁定时注册，按一下就退出（默认 Ctrl+Alt+Q）。</summary>
        private NativeHotkey? _escapeHotkey;

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

                _hotkeyTried = false;      // 允许用新组合重新注册
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
                if (IsVisible) { InstallMouseHook(); EnsureToggleHotkey(); }
                else { ReleaseCapture("离开页面"); UninstallMouseHook(); }
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

        private void OnOwnerActivated(object? sender, EventArgs e) { }
        private void OnOwnerDeactivated(object? sender, EventArgs e) => ReleaseCapture("窗口失去焦点");
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
            try { System.Windows.Forms.Cursor.Show(); } catch { }
            ReleaseAccel();

            var hk = _escapeHotkey;
            _escapeHotkey = null;
            try { hk?.Dispose(); } catch { }

            var h = _mouseHook;
            _mouseHook = null;
            try { h?.Dispose(); } catch { }
        }

        /// <summary>切换输入模式。进入自由视角前会先确认一切就绪。</summary>
        public (bool ok, string message) SetInputMode(ConsoleInputMode mode)
        {
            if (mode == ConsoleInputMode.FreeLook)
            {
                if (Input == null) return (false, "注入未就绪，无法进入自由视角");
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
            if (rect == null) { Debug("拒绝进入自由视角：画面不可用（" + reason + "）"); return false; }
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

            _captured = true;

            if (_accelToken < 0) _accelToken = PointerAcceleration.PushDisabled();

            NativeInput.SetCursorPos(_captureCenterX, _captureCenterY);
            // ★ 基准位必须同步成中心点，否则第一次移动会把"旧光标 → 中心"这段
            //   跳变当成真实位移，视角会猛地甩一下。
            _lastMoveX = _captureCenterX;
            _lastMoveY = _captureCenterY;
            _pendLookX = 0;
            _pendLookY = 0;
            try { System.Windows.Forms.Cursor.Hide(); } catch { }

            EnsureToggleHotkey();

            // ★ 关键：SetCursorPos + Cursor.Hide 会把键盘焦点从 RDP 控件上抢走，
            //   导致锁定后键盘完全没反应（用户反馈）。
            //   锁定不影响键盘通道，所以这里必须把焦点重新交回控件。
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
            // 幂等：已注册就跳过（否则会自己和自己抢，第二次必失败）
            if (_escapeHotkey != null) return;
            if (_hotkeyTried && _hotkeySpec == ToggleHotkey) return;

            _hotkeyTried = true;
            _hotkeySpec = ToggleHotkey;

            _escapeHotkey = NativeHotkey.RegisterFromString(ToggleHotkey, OnToggleHotkey);

            if (_escapeHotkey == null)
                Debug("热键注册失败（可能被占用）：" + ToggleHotkey);
            else
                Debug("锁定热键已注册：" + ToggleHotkey);
        }

        private bool _hotkeyTried;
        private string _hotkeySpec = "";

        private void OnToggleHotkey()
        {
            // 正在锁 → 解锁；否则 → 上锁
            var target = _captured ? ConsoleInputMode.Pointer : ConsoleInputMode.FreeLook;
            if (!IsConnected)
            {
                InputActivity?.Invoke(this, "画面尚未连接，无法进入自由视角");
                return;
            }
            var (ok, _) = SetInputMode(target);
            if (!ok && _captured) ReleaseCapture("热键兜底");
        }

        private void ReleaseCapture(string reason)
        {
            ReleasePointerCapture();
            if (!_captured) { ReleaseAccel(); return; }
            _captured = false;
            _keyboardFocusRetry = 20;

            try { System.Windows.Forms.Cursor.Show(); } catch { }
            ReleaseAccel();

            Debug("退出鼠标锁定（" + reason + "）");
        }

        private void WatchPointerCapture()
        {
            if (!IsConnected || !IsVisible) { ReleasePointerCapture(); return; }
            var rect = ViewportScreenRect();
            if (rect == null) { ReleasePointerCapture(); return; }
            var cursor = System.Windows.Forms.Cursor.Position;
            bool inside = cursor.X >= rect.Value.left && cursor.X < rect.Value.left + rect.Value.width
                       && cursor.Y >= rect.Value.top && cursor.Y < rect.Value.top + rect.Value.height;
            if (!inside || (_ownerWindow != null && !_ownerWindow.IsActive)) ReleasePointerCapture();
        }

        private void ReleasePointerCapture()
        {
            if (!_pointerCaptured) return;
            _pointerCaptured = false;
            PointerCaptureEnabled = false;
            try { System.Windows.Input.Keyboard.ClearFocus(); } catch { }
            InputActivity?.Invoke(this, "键鼠已释放 · 点击画面可重新捕获");
        }

        private void ReleaseAccel()
        {
            int token = _accelToken;
            if (token < 0) return;
            _accelToken = -1;
            try { PointerAcceleration.PopRestore(token); } catch { }
        }

        /// <summary>
        /// 全局鼠标回调。返回 true = 吞掉（不传给宿主桌面和 RDP 控件）。
        ///
        /// ★ 两种模式，语义要清楚：
        ///   锁定关闭（默认）：**完全放行**，鼠标事件走 RDP 原生通道，行为跟直接开游戏窗口一样。
        ///   锁定开启：光标钉在画面中心并隐藏，移动即转视角，点击在当前位置生效。
        ///
        /// 任何异常都立刻放行，绝不让用户动不了鼠标。
        /// </summary>
        private bool OnGlobalMouse(MouseHookEvent ev)
        {
            try
            {
                if (!_captured && CaptureOnClick)
                {
                    var rect = ViewportScreenRect();
                    if (rect == null) return false;
                    bool inside = ev.X >= rect.Value.left && ev.X < rect.Value.left + rect.Value.width
                               && ev.Y >= rect.Value.top && ev.Y < rect.Value.top + rect.Value.height;
                    if (!_pointerCaptured && inside && ev.Message == 0x0201)
                    {
                        _pointerCaptured = true;
                        PointerCaptureEnabled = true;
                        Dispatcher.InvokeAsync(GiveKeyboardFocus);
                        InputActivity?.Invoke(this, "键鼠已捕获 · 移出画面即释放");
                        // 激活点击必须送达 RDP，控件才能获得键盘焦点；只拦截未激活的移动。
                        return false;
                    }
                    if (!_pointerCaptured && inside) return true;
                }
                // ── 未锁定：放行，交给 RDP 自己处理 ──
                if (!_captured)
                    return false;

                // ── 锁定中（自由视角）─────────────────────────────────────────────
                //
                // ★ 核心约束（用户反馈，务必保持）：
                //   自由视角**只负责转视角，绝不能影响鼠标落点**。
                //   要能正常拖动地图、点击传送。
                //
                // ★ 为什么按键不能"放行给 RDP 原生通道"（曾经这么写，是错的）：
                //   自由视角下我们把本地光标 SetCursorPos 钉在画面中心，
                //   RDP 原生通道发出去的点击坐标因此永远等于中心点 ——
                //   用户点哪里都落到屏幕正中，拖拽也拖不动，看起来就是"点不中/拖不动"。
                //
                //   正确做法：按键改走注入通道（down/up），远端光标停在它自己的位置，
                //   和视角转动解耦。视角照转，落点照旧，两者互不干扰。
                if (ev.IsMove)
                {
                    // ★ 用「与上一次事件的差」而不是「与中心的差」。
                    //   与中心做差看起来更直观，但 SetCursorPos 回中心后钩子仍会收到
                    //   中间过渡事件（系统把光标从 A 挪到中心也是一次移动），
                    //   同一个物理位移会被反复算进去 —— 表现为视角"飘"、指针抖动。
                    //   相邻差分只取真实位移，回中心的过程不参与计算。
                    int dx = ev.X - _lastMoveX;
                    int dy = ev.Y - _lastMoveY;
                    _lastMoveX = ev.X;
                    _lastMoveY = ev.Y;

                    // 光标回中心，保证可以无限转（不回中心会撞到屏幕边缘）
                    NativeInput.SetCursorPos(_captureCenterX, _captureCenterY);
                    // 回中心之后，下一次事件要按中心重新起算，否则会把
                    // "中心 → 新位置"这段跳变算成一次巨大的位移
                    _lastMoveX = _captureCenterX;
                    _lastMoveY = _captureCenterY;

                    if (dx == 0 && dy == 0) return true;

                    // 极少数情况下焦点会被外部抢走，锁定期间持续保住
                    if (_ax != null && !_ax.ContainsFocus && _keyboardFocusRetry-- > 0)
                    {
                        try { _ax.Focus(); } catch { }
                    }

                    // ★ 不再用 5ms 节流丢弃位移：丢弃会让转视角一顿一顿的。
                    //   累积到阈值再发一次，既减少 IPC 次数又不丢角度。
                    _pendLookX += dx;
                    _pendLookY += dy;
                    var now = DateTime.UtcNow;
                    bool big = Math.Abs(_pendLookX) >= 3 || Math.Abs(_pendLookY) >= 3;
                    if (big || (now - _lastInject).TotalMilliseconds >= 8)
                    {
                        int sx = _pendLookX, sy = _pendLookY;
                        _pendLookX = 0;
                        _pendLookY = 0;
                        _lastInject = now;
                        _ = InjectLookAsync(sx, sy);
                    }
                    return true;
                }

                // 滚轮：放行（RDP 原生处理，不影响落点）
                if (ev.IsWheel) return false;

                // 鼠标按键：改走注入，绝不让本地中心的坐标污染远端落点。
                if (ev.IsLeftDown || ev.IsRightDown || ev.IsMiddleDown || ev.IsXButton1Down || ev.IsXButton2Down)
                {
                    _ = InjectButtonAsync(ButtonNameOf(ev), true);
                    return true;
                }
                if (ev.IsLeftUp || ev.IsRightUp || ev.IsMiddleUp || ev.Message == 0x020C /*WM_XBUTTONUP*/)
                {
                    _ = InjectButtonAsync(ButtonNameOf(ev), false);
                    return true;
                }

                return false;
            }
            catch
            {
                ReleaseCapture("回调异常");
                return false;
            }
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

        /// <summary>
        /// 自由视角下的点击：**在光标当前位置直接点，不做任何定位**。
        ///
        /// ★ 不能走 ClickUiAsync —— 那个会先 SetCursorPos 再点，
        ///   而捕获状态下游标本来就锁在中心，结果每次点击都跳回中心（实测反馈）。
        ///   自由视角里"准星"就是当前光标位置，原地按下抬起即可。
        /// </summary>
        private async Task InjectClickAtCursorAsync(string button)
        {
            var input = Input;
            if (input == null) return;
            try
            {
                var r = await input.Relay.ClickAsync(0, 0, button, 1).ConfigureAwait(false);
                if (r?.Ok != true) Debug("自由视角点击失败：" + (r?.Message ?? "无响应"));
            }
            catch (Exception ex) { Debug("自由视角点击异常：" + ex.Message); }
        }

        /// <summary>把钩子事件映射成注入通道认识的按钮名。</summary>
        private static string ButtonNameOf(MouseHookEvent ev)
        {
            if (ev.IsRightDown || ev.IsRightUp) return "right";
            if (ev.IsMiddleDown || ev.IsMiddleUp) return "middle";
            if (ev.IsXButton1Down || ev.IsXButton2Down || ev.IsXButtonUp)
                return ev.IsXButton1 ? "x1" : "x2";
            return "left";
        }

        /// <summary>
        /// 自由视角下的按键注入（按下/抬起分离）。
        ///
        /// ★ 必须走 down/up 而不是 click：拖拽地图要的是"按住不放 + 移动 + 松开"，
        ///   一次性 click 根本拖不动。
        /// ★ 远端光标保持在它自己的位置，不受本地被钉在中心的影响。
        /// </summary>
        private async Task InjectButtonAsync(string button, bool down)
        {
            var input = Input;
            if (input == null) return;
            try
            {
                if (down) await input.Relay.DownAsync(button).ConfigureAwait(false);
                else await input.Relay.UpAsync(button).ConfigureAwait(false);
            }
            catch (Exception ex) { Debug("自由视角按键注入失败：" + ex.Message); }
        }

        private async Task InjectLookAsync(int dx, int dy)
        {
            var input = Input;
            if (input == null) return;
            try
            {
                // 灵敏度直接作用在注入前的位移量上（不依赖缩放，避免高 DPI 变慢）
                double sens = LookSensitivity;
                int sx = (int)Math.Round(dx * sens);
                int sy = (int)Math.Round(dy * sens);
                await input.LookRawAsync(sx, sy).ConfigureAwait(false);
            }
            catch { }
        }

        private async Task InjectWheelAsync(int delta)
        {
            var input = Input;
            if (input == null) return;
            try { await input.WheelAsync(delta).ConfigureAwait(false); } catch { }
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

        public bool IsLooking => _captured;

        private void SetStatus(string text)
        {
            try { Dispatcher.InvokeAsync(() => StatusChanged?.Invoke(this, text)); } catch { }
        }

        /// <summary>把连接过程的细节落到 %LOCALAPPDATA%\SoulLink\rdp-debug.log，方便排查。</summary>
        internal static void Debug(string message)
        {
            try
            {
                System.IO.Directory.CreateDirectory(ConfigStore.ConfigDir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(ConfigStore.ConfigDir, "rdp-debug.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine,
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
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
                            int activeSessionId = sessionId;
                            try
                            {
                                var activeSessions = await SessionService.QueryAsync().ConfigureAwait(true);
                                var activeSession = SessionService.Find(activeSessions, account.UserName);
                                if (activeSession != null && activeSession.Id > 0)
                                {
                                    activeSessionId = activeSession.Id;
                                    Debug("连接后真实会话号=" + activeSessionId + (sessionId != activeSessionId ? "（由 " + sessionId + " 更新）" : ""));
                                }
                            }
                            catch (Exception ex) { Debug("回读会话号失败: " + ex.Message); }
                            SetStatus("已连接 · 会话 " + activeSessionId);
                            LogBus.Ok("RDP 已连上 " + account.Caption,
                                "地址 " + host + " · 耗时 " + sw.ElapsedMilliseconds + " ms");
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
                secured.KeyboardHookMode = Clamp(account.RdpKeyboardHookMode, 0, 2);
            }
            catch (Exception ex) { Debug("音频/组合键属性失败: " + ex.Message); }

            // ── 连接栏 / 全屏 ──
            try
            {
                dynamic a6 = ocx.AdvancedSettings6;
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
            if (!InjectEnabled) return;
            await StopInputAsync().ConfigureAwait(true);

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
                SyncMapperViewport();

                // 等画面真正稳定再探测。刚连上时远端桌面还没切过来，
                // 立刻探测会误判成「不可交互」（实测）。
                await Task.Delay(1500).ConfigureAwait(true);

                var (usable, why) = await input.CheckUsableAsync().ConfigureAwait(true);
                if (!usable)
                {
                    // 再给一次机会：有些会话要 2~3 秒才把桌面切过来
                    await Task.Delay(2000).ConfigureAwait(true);
                    (usable, why) = await input.CheckUsableAsync().ConfigureAwait(true);
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
            }
        }

        private async Task StopInputAsync()
        {
            var old = Input;
            Input = null;
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
            try { StopInputAsync().Wait(800); } catch { }
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
            _ocx = null;
            _ax = null;
        }
    }
}
