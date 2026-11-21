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
using System.Threading;

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

        // ★ 2.17.51（.NET 11 迁移）：原来每次访问都 new 一个数组，每重连一遍就多分配一次。
        //   候选表是常量，缓存成静态只读即可（3 个 CLSID 全程不变）。
        private static readonly string[] CandidateClsids = { ClsidV9, ClsidV10, ClsidV11 };

        private readonly WindowsFormsHost _host = new WindowsFormsHost();
        private RdpAxHost? _ax;
        private dynamic? _ocx;
        private bool _disposed;

        public Account? Account { get; private set; }

        /// <summary>
        /// 绑定账号但不连接 RDP。
        ///
        /// ★ 必须单独提供：Account 原来只在 ConnectAsync 里赋值，
        ///   而"按会话号投送通道"的场景下这个 Console 可能从未连接过 RDP，
        ///   于是 Account 为 null → 投送被静默跳过（用户实测：监控说"开始投送"
        ///   却始终没有代理进程）。
        /// </summary>
        private Account? _boundAccount;

        public void BindAccount(Account account)
        {
            if (account == null) return;
            _boundAccount = account;
            Account ??= account;
        }

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

        /// <summary>
        /// 画布（多用户画面区）的屏幕矩形（物理像素）。由 ConsolePage 注入。
        ///
        /// ★ 重新上锁的判据必须是**画布范围**，不是单个 RDP 画面的矩形。
        ///   多画面时 RDP 只占画布的一格，光标在格子之间移动时会一直"移出 RDP"，
        ///   于是每跨一格就误判成一次"离开画面"而反复上锁。
        ///   以画布为界：只要还在画布内，就持续保持解锁状态。
        /// </summary>
        public Func<(int left, int top, int width, int height)?>? CanvasScreenRectProvider { get; set; }

        /// <summary>
        /// 画面格子（RdpConsole 的父容器）的屏幕矩形。
        /// 这是纯 WPF 元素，宿主窗口被移到屏幕外也不影响它 —— 用它做恢复坐标的唯一来源。
        /// </summary>
        public Func<(int left, int top, int width, int height)?>? HostScreenRectProvider { get; set; }

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

        // ══════════════════════════════════════════════════════════════════
        // 保护层：禁用 RDP 原生窗口（2026-11-19 第三版实现）
        //
        // ★ 演进过程（三次都踩过，别再走回头路）：
        //   ① 半透明浮层压在画面上 → 分层窗口(WS_EX_LAYERED)压到
        //      mstscax 的 DirectComposition 表面上 → 整块黑屏。已废弃。
        //   ② 低级鼠标钩子只吞左键 Down/Up → 鼠标**移动**照样进 RDP，
        //      远程光标跟着动、悬停/拖动全生效，等于没保护。已废弃。
        //   ③ 当前：EnableWindow(宿主窗口, FALSE)。
        //      禁用只影响输入路由（WS_DISABLED + WM_ENABLE），
        //      **不新建/不销毁窗口、不动合成表面**，所以既不会黑屏，
        //      也不具备 0x119（VIDEO_SCHEDULER_INTERNAL_ERROR）的触发条件
        //      ——那次事故的成因是多窗口挂载/ActiveX 反复进出容器。
        //
        //   ★ 已知限制：禁用期间键盘完全进不去（这是"未接管"的应有语义）；
        //     恢复时要显式把焦点交回 RDP，否则用户会觉得"点了没反应"。
        // ══════════════════════════════════════════════════════════════════

        /// <summary>是否生效（勾选「点击画面才捕获键鼠」才有保护层）。</summary>
        public bool ShieldActive => CaptureOnClick;

        /// <summary>当前是否处于锁定态（键鼠尚未交给 RDP）。</summary>
        public bool ShieldLocked { get; private set; }

        /// <summary>锁定态变化：true=已锁，false=已交给 RDP。</summary>
        public event Action<RdpConsole, bool>? ShieldLockChanged;

        /// <summary>用户点击锁定中的画面 = 请求接管。</summary>
        public event Action<RdpConsole>? ShieldClicked;

        /// <summary>锁定时鼠标是否真的被拦住（宿主窗口确实处于禁用态）。</summary>
        public bool ShieldEnforced => !ShieldLocked || _hostDisabled;

        /// <summary>原生宿主窗口当前是否被我们禁用。</summary>
        private volatile bool _hostDisabled;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool EnableWindow(IntPtr hWnd, bool bEnable);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool IsWindowEnabled(IntPtr hWnd);


        /// <summary>
        /// 锁定：禁用承载 ActiveX 的原生窗口。
        /// 幂等 —— 看门狗 150ms 就会调一次。
        /// </summary>
        public void LockShield(string reason)
        {
            if (_disposed) return;
            bool changed = !ShieldLocked;
            ShieldLocked = true;
            _pointerCaptured = false;
            PointerCaptureEnabled = false;
            if (changed)
            {
                try { System.Windows.Input.Keyboard.ClearFocus(); } catch { }
                Debug("保护层锁定（" + reason + "）→ 禁用 RDP 原生窗口");
            }
            ApplyHostEnabled(false);
            if (changed) { try { ShieldLockChanged?.Invoke(this, true); } catch { } }
        }

        /// <summary>解锁：恢复原生窗口，把键鼠交给 RDP。</summary>
        public void UnlockShield(string reason)
        {
            bool was = ShieldLocked || _hostDisabled;
            ShieldLocked = false;
            _pointerCaptured = true;
            PointerCaptureEnabled = true;
            ApplyHostEnabled(true);
            if (was)
            {
                Debug("保护层解锁（" + reason + "）→ 恢复 RDP 原生窗口");
                try { ShieldLockChanged?.Invoke(this, false); } catch { }
            }
            // ★ 恢复后必须显式把键盘焦点交回 RDP：
            //   禁用期间 SetFocus 全部失败，不补这一步用户会觉得"点了没反应"。
            try { GiveKeyboardFocus(); } catch { }
        }

        /// <summary>
        /// 禁用/恢复承载 ActiveX 的原生窗口。
        ///
        /// ★ 用 _host.Handle（WindowsFormsHost 的原生 HWND），不是 _ax.Handle：
        ///   禁用宿主的整个子树（含 ActiveX 与 IHWindowClass）一并生效。
        /// </summary>
        private void ApplyHostEnabled(bool enabled)
        {
            try
            {
                IntPtr handle = IntPtr.Zero;
                try { handle = _host?.Handle ?? IntPtr.Zero; } catch { handle = IntPtr.Zero; }
                if (handle == IntPtr.Zero)
                {
                    // 句柄还没建好：先记状态，看门狗下一轮再来。
                    if (enabled) _hostDisabled = false;
                    return;
                }

                // 已经是目标状态就别重复调：每调一次都会发一条 WM_ENABLE。
                bool want = enabled;
                bool now = IsWindowEnabled(handle);
                if (now == want)
                {
                    _hostDisabled = !want;
                    return;
                }

                bool ok = EnableWindow(handle, want);
                _hostDisabled = !want;
                Debug("保护层：" + (want ? "恢复" : "禁用") + " RDP 宿主窗口 hwnd=" + handle
                      + " ok=" + ok + " nowEnabled=" + IsWindowEnabled(handle));
            }
            catch (Exception ex)
            {
                _hostDisabled = false;
                App.AppendErrorLog("保护层.ApplyHostEnabled", ex);
            }
        }

        /// <summary>
        /// 自愈：确保"该禁用时确实禁用"。
        /// 由 ConsolePage 的 150ms 看门狗周期调用，不依赖某一次回调。
        /// </summary>
        public void EnsureShieldEnforced()
        {
            if (_disposed) return;
            if (!ShieldActive)
            {
                if (ShieldLocked) UnlockShield("保护层已关闭");
                return;
            }
            if (ShieldLocked && !_hostDisabled) ApplyHostEnabled(false);
            else if (!ShieldLocked && _hostDisabled) ApplyHostEnabled(true);
        }

        // ── 焦点归属识别 ────────────────────────────────────────────────────
        //
        // 解锁后 RDP 原生窗口恢复启用，鼠标点击被它自己吞掉，
        // WPF 的格子 Border 根本收不到 MouseLeftButtonUp。
        // 所以"点画面切焦点"不能靠 WPF 点击事件，只能**问系统**：
        // 当前键盘焦点到底落在哪个 RDP 窗口里。

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public int rcCaretLeft, rcCaretTop, rcCaretRight, rcCaretBottom;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GetGUIThreadInfo(int threadId, ref GUITHREADINFO info);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

        /// <summary>
        /// 本画面（的 ActiveX 子树）当前是否持有键盘焦点。
        ///
        /// ★ 判定要同时看三处，缺一不可（都实测过）：
        ///   · hwndFocus —— 光标真正落入的那个子窗口（通常是 IHWindowClass）；
        ///   · hwndActive —— 前台线程的活动窗口（点子窗口时可能只有它变）；
        ///   · 前台窗口本身 —— 用户刚点进来时焦点还没完全沉降。
        ///
        /// 只看 AxHost.ContainsFocus 不够：RDP 的焦点常落在它自己创建的子窗口上。
        /// </summary>
        public bool HasKeyboardFocus
        {
            get
            {
                try
                {
                    if (_disposed) return false;
                    if (_ax == null || !_ax.IsHandleCreated) return false;
                    IntPtr root = _host?.Handle ?? IntPtr.Zero;
                    if (root == IntPtr.Zero) return false;

                    // ★ 被我们禁用的宿主窗口不算"持有焦点"。
                    //   禁用只是让消息进不来，键盘焦点**仍然停在原来的子窗口上**
                    //   （Windows 不会因为 EnableWindow(FALSE) 就转移焦点）。
                    //   不排除它的话，锁定态点 B 之后看门狗会把焦点拉回 A
                    //   —— 用户实测："从 A 到 B，焦点窗会自动回 A"。
                    if (_hostDisabled) return false;
                    if (!IsWindowEnabled(root)) return false;

                    // ① AxHost 自己的判断（最快，但不一定准）
                    if (_ax.ContainsFocus) return true;

                    // ② 前台窗口是否属于本画面
                    IntPtr fg = GetForegroundWindow();
                    if (fg != IntPtr.Zero && (fg == root || IsChild(root, fg))) return true;

                    // ③ 本进程 UI 线程的焦点窗口是否属于本画面（最可靠）
                    try
                    {
                        var gi = new GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>() };
                        int tid = (int)GetWindowThreadProcessId(root, IntPtr.Zero);
                        if (tid != 0 && GetGUIThreadInfo(tid, ref gi))
                        {
                            if (gi.hwndFocus != IntPtr.Zero
                                && (gi.hwndFocus == root || IsChild(root, gi.hwndFocus))) return true;
                            if (gi.hwndActive != IntPtr.Zero
                                && (gi.hwndActive == root || IsChild(root, gi.hwndActive))) return true;
                        }
                    }
                    catch { }

                    return false;
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// RDP 控件收到 WM_MOUSEACTIVATE：用户点了这个画面。
        /// 立即广播给外层，让它当拍把焦点切过来（不等看门狗轮询）。
        /// </summary>
        private void OnAxMouseActivated(RdpAxHost ax)
        {
            try
            {
                // 只广播，不做 UI 操作 —— 这里还在 RDP 的窗口过程里。
                ShieldClicked?.Invoke(this);
            }
            catch { }
        }

        /// <summary>
        /// 把**真实键盘焦点**转移给本画面的 RDP 控件。
        ///
        /// ★ 与 GiveKeyboardFocus 的区别：后者在保护层锁定时会主动放弃
        ///   （CaptureOnClick &amp;&amp; !_pointerCaptured 时直接 return）。
        ///   而这里用于"用户刚刚点了这一格"的场景，必须无条件生效 ——
        ///   否则 A 的原生窗口会一直持有系统焦点，看门狗又把焦点拉回 A
        ///   （用户实测："焦点框回落到 A"）。
        /// ★ 必须在宿主窗口**已恢复启用**之后调用，否则 SetFocus 会失败。
        /// </summary>
        public void ForceKeyboardFocus()
        {
            try
            {
                if (_disposed || _ax == null || !_ax.IsHandleCreated) return;
                if (_hostDisabled) return;   // 还锁着就不抢焦点

                _host.Focusable = false;
                var form = System.Windows.Forms.Control.FromChildHandle(_ax.Handle);
                IntPtr target = form?.Handle ?? _ax.Handle;

                _ax.Focus();
                SetFocus(target);
            }
            catch { }
        }

        /// <summary>用户在锁定中的画面上点了鼠标 = 请求接管。</summary>
        public void RequestTakeover()
        {
            if (_disposed) return;
            UnlockShield("用户点击画面");
            try { ShieldClicked?.Invoke(this); } catch { }
        }
        /// <summary>连接代次：每次主动重连递增，后台恢复时可据此重新锁定。</summary>
        public int ConnectionGeneration => _connectionGeneration;

        /// <summary>实际承载 ActiveX 的原生窗口；重连后句柄可能变化。</summary>
        public IntPtr NativeViewportHandle
        {
            get
            {
                try { return _ax?.IsHandleCreated == true ? _ax.Handle : IntPtr.Zero; }
                catch { return IntPtr.Zero; }
            }
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

            // ★★ 2.17.17：WindowsFormsHost 不再在构造时就挂进视觉树。★★
            //
            //   WindowsFormsHost 是**独立 HWND**，不参与 WPF/DWM 的圆角裁剪。
            //   一旦它进入视觉树，即使还没连接任何账号（Child 为空），
            //   也会在窗口上铺出一块方形不透明区域，把 DWM 的圆角盖成直角，
            //   并在窗口内侧露出一条与圆角错位的边。
            //
            //   实测（2026-11-20，本机）：
            //     · 高画质启动：AuroraBackdrop 每帧全量重绘，把这块方形区域刷成
            //       与周围一致的像素，视觉上被掩盖 → 看不到异常；
            //     · 中/低画质启动：背景不再重绘，方形块边界暴露 → 一圈灰/白边、
            //       圆角变直角、底边与右边还因为布局尚未完成而"缺口"；
            //     · 关掉 RDP 自启（不创建 Console）→ 完全正常；
            //     · 切后台再回来 → 触发一次重排重绘，整圈又被画出来。
            //   四个现象与"独立 HWND 盖住圆角"完全吻合。
            //
            //   现在改为：只在真正需要时（EnsureHost 成功创建 OCX 后）才挂上。
            //   未连接的账号不再影响窗口外观。
            _host.Visibility = Visibility.Collapsed;
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
        private readonly Lock _lookQueueSync = new();
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
        private bool _hotkeyArmed;
        internal bool HotkeyArmed
        {
            get => _hotkeyArmed;
            set
            {
                if (_hotkeyArmed == value) return;
                // ★ 临时诊断：记录每一次翻转，用来回答"热键为什么判成非当前控制台"。
                InputTrace.Write("ARMED", "实例=" + _inst + " 会话=" + CurrentSessionId + " HotkeyArmed " + _hotkeyArmed + " → " + value
                    + " _activeState=" + _activeState + " IsVisible=" + _uiVisibleCache
                    + " ownerActive=" + _ownerActiveCache + " 调用方=" + InputTrace.Caller(3));
                _hotkeyArmed = value;
            }
        }
        private int _hotkeyToggleVersion;
        private readonly System.Collections.Generic.HashSet<string> _injectedButtons
            = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _inputUsable;
        private int _inputGeneration;
        private int _connectionGeneration;
        private int _inputStartPending;

        // ★★ 临时诊断（交接文档 4.1 取证）★★ 定位代理建连约 2 秒后被断开的原因。
        // 只记录、不参与控制流。结论落定后连同 Services/InputTrace.cs 一起删除。
        private System.Diagnostics.Stopwatch? _traceSw;
        private static string TraceId(object? o) => o == null ? "null" : "#" + o.GetHashCode().ToString("X8");
        private bool _nativeRelativeMouse;
        private int _activeState; // 0=前台允许输入，1=后台/已撤销

        // ★ 临时诊断：每个 RdpConsole 一个稳定短标识，用来区分"到底哪个实例"。
        private readonly string _inst = "C" + Guid.NewGuid().ToString("N").Substring(0, 4);

        /// <summary>
        /// ★ 诊断专用：UI 属性的缓存副本。
        ///
        /// 为什么需要：IsVisible / _ownerWindow.IsActive 都是 **UI 线程专属**，
        /// 而 SET_ACTIVE / ARMED 这些埋点会在后台线程被调用（例如 Broker 服务更新后的
        /// InvalidateAgentConnection 路径）。直接读会抛
        /// "The calling thread cannot access this object because a different thread owns it."
        /// —— 实测中它把一次成功的投送判成了失败。
        ///
        /// 所以埋点**只读这两个 volatile 缓存**，由 UI 线程在 OnIsVisibleChanged /
        /// owner 状态变化时更新。读缓存永远不会抛异常。
        /// </summary>
        private volatile bool _uiVisibleCache;
        private volatile bool _ownerActiveCache;

        private void RefreshUiCache()
        {
            try
            {
                _uiVisibleCache = IsVisible;
                _ownerActiveCache = _ownerWindow?.IsActive == true;
            }
            catch { /* 不在 UI 线程时保持旧值 */ }
        }
        public void SetForegroundActive(bool active)
        {
            InputTrace.Write("SET_ACTIVE", "实例=" + _inst + " 会话=" + CurrentSessionId + " active=" + active
                + " 旧_activeState=" + _activeState + " 可见缓存=" + _uiVisibleCache
                + " 调用方=" + InputTrace.Caller(3));
            System.Threading.Interlocked.Exchange(ref _activeState, active ? 0 : 1);
            HotkeyArmed = active;
            if (!active)
            {
                // ★★ 自由视角期间必须整体跳过。★★
                //
                //   实测（rdp-debug 2026-11-20 03:33）：
                //     03:33:26~03:33:34 连续 5 次都是"进入自由视角 captured=True"，
                //     没有一次是"退出" —— 说明每次进入后 _captured 立刻又被清回 false。
                //
                //   链路：进入自由视角 → 撤保护层 → 画面控件 IsVisible=false
                //        → IsVisibleChanged 触发 → SetForegroundActive(false)
                //        → 这里的 _captured = false 把刚设上的捕获清掉
                //        → 下一次按侧键又变成"进入"，永远出不来；
                //          同时 HotkeyArmed 被打成 false，热键逐渐失效。
                //
                //   守卫必须排在 _captured = false 之前：
                //   自由视角是"用户主动接管输入"的状态，与"画面是否可见"无关。
                //   画面不可见只影响鼠标钩子（看不见就别吞输入），仅此而已。
                if (_captured)
                {
                    InputTrace.Write("SET_ACTIVE_SKIP", "实例=" + _inst + " 会话=" + CurrentSessionId
                        + " 正在自由视角 → 不撤销捕获/热键资格（_activeState 已置 1）");
                    UninstallMouseHook();
                    return;
                }
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

        /// <summary>
        /// 按「用户名 → 会话号」投送注入通道，**不需要先连 RDP**。
        ///
        /// 用途：多账号挂机时，没打开画面的账号也要有注入通道。
        /// RelayClient 只依赖会话号，与 RDP 控件是否连接无关。
        ///
        /// 返回 true = 已投送；false = 该账号当前无会话（未登录）。
        /// </summary>
        /// <summary>
        /// 会话发现后，等待多久再投送（给会话留出稳定时间）。
        ///
        /// ★ 从 12 秒降到 3 秒的依据（实测）：
        ///   · schtasks 创建任务 39ms、删除 23ms
        ///   · 代理启动到连回管道约 26ms
        ///   投送本身合计 &lt; 100ms，真正要等的是"会话桌面就绪"。
        ///   3 秒足够覆盖这段，且配合 2 秒扫描，会话出现后约 5 秒即可通道就绪。
        /// </summary>
        public const int SessionSettleSeconds = 3;

        public async Task<bool> StartInputBySessionAsync()
        {
            try
            {
                if (_disposed) { Debug("投送通道：已销毁"); return false; }
                if (!InjectEnabled) { Debug("投送通道：注入被禁用"); return false; }
                // ★ Account 在未连过 RDP 的 Console 上是 null（它只在 ConnectAsync 里赋值）。
                //   多账号挂机时后台账号可能从未连接，这里从外部绑定的账号兜底。
                var acc = Account ?? _boundAccount;
                if (acc == null) { Debug("投送通道：Account 为空（未绑定）"); return false; }
                if (Input?.Relay.IsConnected == true)
                {
                    Debug("投送通道：已有连接，直接复用");
                    return true;
                }
                Debug("投送通道：开始查询会话（账号 " + acc.UserName + "）");

                // ★ 这里只做「查会话 + 投送」，**不在这里等待**。
                //   "在线满 12 秒"由会话监控层统一计时（见 ConsolePage.ScanAndDeployAsync），
                //   否则两处各自计时会互相打架、重复等待。
                var sessions = await SessionService.QueryAsync().ConfigureAwait(true);
                var s = SessionService.Find(sessions, acc.UserName);
                if (s == null || s.Id <= 0)
                {
                    Debug("投送通道：「" + acc.UserName + "」当前无会话，跳过");
                    return false;
                }

                CurrentSessionId = s.Id;
                Debug("投送通道：「" + acc.UserName + "」→ 会话 " + s.Id);

                // ★★ 不能立刻 return true。★★
                //
                //   原来这里 BeginInput（异步 void）一发出就返回 true，
                //   调用方（会话监控）据此记「通道投送成功」，并写进 _deployHandled。
                //   但投送是异步的：代理进程没起来、管道没建立、甚至失败了，
                //   日志里照样是"投送成功"。
                //
                //   用户实测：日志 01:53:59 两条"通道投送成功"，
                //   而实际 Get-Process SoulLink.Agent 为空、relay 管道也没有。
                //
                //   现在：真正发出投送，然后等一次结果（最多 8 秒），按真实结果返回。
                //   等不到也不算失败 —— 仍返回 true，但会记一条"已发起未确认"，
                //   由调用方的 6 秒去抖与状态刷新兜底。

                // ★★ 这一行绝不能少：它才是真正发起投送的地方。★★
                //   上一轮改"等待确认"时把它误删了，结果只等不投 ——
                //   表现为日志一直"已发起未确认"，代理进程永远不存在。
                BeginInput(s.Id, acc);

                bool started = await WaitForRelayConnectedAsync(timeoutMs: 8000).ConfigureAwait(false);
                if (started)
                {
                    Debug("投送通道：「" + acc.UserName + "」已确认连接（会话 " + s.Id + "）");
                    return true;
                }

                Debug("投送通道：「" + acc.UserName + "」已发起，但未在 8 秒内确认连接");
                return true;   // 仍然算"已发起"，不阻塞监控循环；状态由实际探测决定
            }
            catch (Exception ex)
            {
                Debug("投送通道失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 等待注入通道真正连上（代理进程 + relay 管道都就绪）。
        /// 只做轮询观察，不主动探测管道（探测会占掉代理的单连接名额）。
        /// </summary>
        private async Task<bool> WaitForRelayConnectedAsync(int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (_disposed) return false;
                if (Input?.Relay.IsConnected == true) return true;

                // ★ ConfigureAwait(false)：绝不能回 UI 线程。
                //   投送会被并发触发（监控、切页、重建三条路径），
                //   每个等待循环若每 200ms 往 UI 线程排一次队，
                //   叠起来会把 UI 线程占满 —— 实测单线程累计 98 秒 CPU、
                //   日志停摆、界面卡顿。
                //   这里只读取一个字段，不需要回到 UI 线程。
                await Task.Delay(200).ConfigureAwait(false);
            }
            return Input?.Relay.IsConnected == true;
        }

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
                InputTrace.Write("ENSURE_RESTORE", "切回画面触发重建 会话=" + CurrentSessionId
                    + " 旧Input=" + TraceId(Input) + " IsConnected=" + IsConnected
                    + " 调用方=" + InputTrace.Caller(3));
                _ = StopInputAsync();
                BeginInput(CurrentSessionId, acc);
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
                    // ★ 临时诊断：每个拒绝分支都留痕，否则"按了没反应"无从排查。
                    RefreshUiCache();   // 本属性由 UI 线程读取，刷新缓存供埋点使用
                    string deny = null;
                    if (_disposed || !IsVisible) deny = "_disposed=" + _disposed + " IsVisible=" + IsVisible;
                    else if (_ownerWindow == null || !_ownerWindow.IsActive) deny = "_ownerWindow=" + (_ownerWindow == null ? "null" : "有") + " IsActive=" + (_ownerWindow?.IsActive);
                    else if (_ownerWindow.WindowState == WindowState.Minimized) deny = "窗口已最小化";
                    else if (!(HotkeyArmed && IsConnected)) deny = "HotkeyArmed=" + HotkeyArmed + " IsConnected=" + IsConnected + " _activeState=" + _activeState;
                    if (deny != null)
                    {
                        InputTrace.Write("HOTKEY_DENY", "实例=" + _inst + " 会话=" + CurrentSessionId + " 原因=" + deny
                            + " 调用方=" + InputTrace.Caller(3));
                        return false;
                    }
                    // ActiveX 不会让 WPF 稳定看到 ContainsFocus / Mouse.DirectlyOver。
                    // 不能用这两个条件阻断热键，否则用户按了也被判为"不属于画面"。
                    return true;
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
                // ★ 不再直接 RequestResize()。
                //   原来每次 SizeChanged 都立刻 UpdateSessionDisplaySettings：
                //   · 拖动主窗口边缘 → 每帧一次远端分辨率重协商；
                //   · 切标签时控件会被摘出/挂回视觉树，SizeChanged 连续触发，
                //     于是「切一次标签就改一次远端分辨率」；
                //   · 事故报告把「拖动/缩放时反复 UpdateSessionDisplaySettings」
                //     列为高风险组合（0x119）。
                //   改为：只记目标，停手 ResizeSettleMs 后由串行队列统一协商。
                NoteViewportSizeChanged();

                // ★ 拉伸模式：窗口变大后必须让控件重新按宿主尺寸铺开。
                //
                //   SmartSizing 是**连接时**的设备属性，窗口尺寸变化**不会**自动重算，
                //   于是远端 1920x1080 的内容停在左上角，多出来的部分露出底色
                //   —— 这就是用户看到的"最大化后画面不跟着放大、四周有灰边"。
                //   这里在尺寸稳定后重新应用一次，不改远端分辨率（用户要求固定 1 模式）。
                ScheduleStretchRefit();
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
                RefreshUiCache();
                // ★ 恢复分支**不能**再要求 _activeState == 0。★★
                //
                //   实测（input-trace 2026-11-20 03:37）：
                //     03:37:41.506 SetForegroundActive(false) → _activeState=1、HotkeyArmed=False
                //     03:38:00.383 画面重新可见，_activeState 仍是 1
                //     03:38:02.162 HOTKEY_DENY 原因=HotkeyArmed=False
                //   隐藏分支把 _activeState 置 1，恢复分支又要求它是 0 ——
                //   **一旦隐藏过一次就永远恢复不了**，这是个自锁。
                //
                //   现在：变可见时无条件装上钩子、确保热键已注册。
                //   至于"谁是前台"，由 ConsolePage 在页面恢复时显式指定
                //   （见 ConsolePage.IsVisibleChanged 里的 SetForegroundActive(true)），
                //   本类不再自己推断，避免两处互相打架。
                if (IsVisible) { InstallMouseHook(); EnsureToggleHotkey(); }
                else if (!IsVisible)
                {
                    // ★★ 自由视角期间画面"本来就该不可见"，绝不能因此撤销捕获。★★
                    //
                    //   实测（input-trace 2026-11-20 03:23）：
                    //     03:23:07 LOCAL_HOTKEY 进入自由视角
                    //     03:23:09 ARMED True → False  调用方=<HookInput>b__172_2 < OnIsVisibleChanged
                    //   进自由视角 → 撤保护层 → 画面控件 IsVisible=false → 触发本订阅
                    //   → SetForegroundActive(false) 里有 _captured = false
                    //   → **刚设上的 _captured 被自己抹掉**，自由视角从未真正生效；
                    //     同时 HotkeyArmed 被打成 false，之后每次按侧键都被拒。
                    //
                    //   这是"进入动作触发退出逻辑"的自毁回路。
                    //   画面不可见只影响鼠标钩子（看不见就别吞输入），
                    //   与"是否处于自由视角""热键资格"无关。
                    if (_captured)
                    {
                        InputTrace.Write("VISIBLE_SKIP", "画面不可见但正在自由视角 → 不撤销捕获 会话=" + CurrentSessionId
                            + " _activeState=" + _activeState + " HotkeyArmed=" + HotkeyArmed);
                        UninstallMouseHook();
                        return;
                    }
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
            ReleasePointerCapture();
            try { ShieldRearmRequested?.Invoke(this); } catch { }
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
        public void SetInputMode(ConsoleInputMode mode)
        {
            // ★★ 这里曾经是同步方法，内部用 GetAwaiter().GetResult() 等管道探测，
            //   CheckUsableAsync 串行发 4 个 relay 请求、每条故障超时 4.25s ——
            //   代理假死（TCP 还在、对端不响应）时会把 UI 线程按住十几秒，
            //   而调用它的正是热键"解锁退出"这条救命通道。
            //   现在：热点路径（退出/已就绪）同步直达，只有"需要重新探测"才异步。
            InputTrace.Write("SET_MODE", "请求切换模式 会话=" + CurrentSessionId + " target=" + mode
                + " 当前=" + InputMode + " _captured=" + _captured
                + " _inputUsable=" + _inputUsable + " Input=" + TraceId(Input)
                + " relayConnected=" + (Input?.Relay.IsConnected) + " 调用方=" + InputTrace.Caller(3));

            var (done, ok, message) = TrySetInputModeNow(mode);
            if (done)
            {
                if (!ok) InputActivity?.Invoke(this, message);
                return;
            }

            // 只可能是 FreeLook 且 _inputUsable 为 false：放到后台探测，UI 永不阻塞
            SetInputModeAsync(mode);
        }

        /// <summary>
        /// 能当场决定的部分。返回 done=false 表示"需要异步探测"。
        /// 退出（Pointer）分支永远 done=true —— 无论如何都要让人能挣脱。
        /// </summary>
        private (bool done, bool ok, string message) TrySetInputModeNow(ConsoleInputMode mode)
        {
            if (mode == InputMode && (mode != ConsoleInputMode.FreeLook || _captured))
                return (true, true, mode == ConsoleInputMode.FreeLook ? "已进入自由视角" : "已切回指针模式");

            if (mode != ConsoleInputMode.FreeLook)
            {
                // ★ 退出无条件放行（原语义保留）：不检查连接、不检查标志。
                ReleaseCapture("手动切换");
                InputMode = mode;
                InputModeChanged?.Invoke(this, mode);
                return (true, true, "已切回指针模式");
            }

            // 相对注入不可用时不能吞掉本机鼠标，但也不能被一个"过期缓存"永久挡住。
            if (!IsConnected || Input == null || !Input.Relay.IsConnected)
                return (true, false, "相对输入通道尚未连接，不能进入自由视角。请等待「注入就绪」。");

            if (!_inputUsable)
            {
                Debug("输入标志为 false，转后台探测");
                return (false, false, "");
            }

            if (!BeginCapture("手动切换"))
                return (true, false, "画面不在可用状态，无法进入自由视角");

            InputMode = mode;
            InputModeChanged?.Invoke(this, mode);
            return (true, true, "已进入自由视角");
        }

        /// <summary>后台探测后完成进入自由视角；探测期间 UI 线程可以做任何事。</summary>
        private async void SetInputModeAsync(ConsoleInputMode mode)
        {
            bool usable = false;
            string why = "";
            var probing = Input;

            try
            {
                if (probing == null) return;
                (usable, why) = await probing.CheckUsableAsync().ConfigureAwait(true);
            }
            catch (Exception ex) { why = "异常：" + ex.Message; }
            Debug("重新探测结果 usable=" + usable + " · " + why);

            if (_disposed) return;

            // ★ 探测期间通道可能被替换、会话可能切走、用户可能已经放弃进入：
            //   结果只对"发起探测的那条通道"有效，否则会把旧结论写进新状态。
            if (!ReferenceEquals(Input, probing))
            {
                Debug("探测结果作废：通道已更换");
                return;
            }
            if (mode != ConsoleInputMode.FreeLook)
            {
                Debug("探测结果作废：用户已切回指针模式");
                return;
            }

            _inputUsable = usable;
            if (!usable)
            {
                InputActivity?.Invoke(this, "相对输入通道尚未就绪，不能进入自由视角。请点「重新连接」后再试。");
                Debug("拒绝进入自由视角：探测后仍不可用");
                return;
            }
            if (InputMode == ConsoleInputMode.FreeLook && _captured) return;
            if (!BeginCapture("异步探测完成"))
            {
                InputActivity?.Invoke(this, "画面不在可用状态，无法进入自由视角");
                return;
            }
            InputMode = mode;
            InputModeChanged?.Invoke(this, mode);
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
            InputTrace.Write("LOCAL_HOTKEY", "实例=" + _inst + " 会话=" + CurrentSessionId
                + " _captured=" + _captured + " _activeState=" + _activeState
                + " HotkeyArmed=" + HotkeyArmed + " 调用方=" + InputTrace.Caller(3));
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
            SetInputMode(target);
            Debug("热键切换结果 captured=" + _captured + " mode=" + InputMode);
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
            // ★ 只有**退出自由视角**才需要请求恢复保护层。
            //
            //   原实现无条件发 ShieldVisibleRequested(true)，而这条消息的含义是
            //   "请重新上锁"。于是多画面切换焦点时（旧焦点 Console 会收到
            //   SetForegroundActive(false) → ReleaseCapture），整块画面区被重新锁住，
            //   表现就是"点 B 之后焦点再也切不回 A，必须重新上锁一次"（用户实测）。
            //
            //   自由视角期间本来就隐藏保护层，所以 wasCaptured 才是需要恢复的场景。
            if (!wasCaptured) return;   // 非自由视角：不动保护层状态，直接返回
            try { ShieldVisibleRequested?.Invoke(this, true); } catch { }
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

            // ★ 优先用「整块画布」的矩形判断是否离开。
            //   代价：光标在画布内格子之间移动不会重新上锁（正确行为）。
            //   兜底：没有注入画布矩形时退回单个 RDP 画面矩形（兼容旧路径）。
            var rect = CanvasScreenRectProvider?.Invoke() ?? ViewportScreenRect();
            if (rect == null) { ReleasePointerCapture(); return; }
            var cursor = System.Windows.Forms.Cursor.Position;
            bool inside = cursor.X >= rect.Value.left && cursor.X < rect.Value.left + rect.Value.width
                       && cursor.Y >= rect.Value.top && cursor.Y < rect.Value.top + rect.Value.height;
            bool wasCaptured = _pointerCaptured;
            bool ownerInactive = _ownerWindow != null && !_ownerWindow.IsActive;
            if (!inside || ownerInactive) ReleasePointerCapture();

            // 移出视口或主窗口失活，都回到锁定态。Alt+Tab 时光标往往仍
            // 位于 RDP 矩形内，不能只检查 inside，否则切回窗口会残留解锁态。
            if (wasCaptured && !_pointerCaptured && (!inside || ownerInactive))
                ShieldRearmRequested?.Invoke(this);
        }

        /// <summary>失焦/原生画面变化时完整撤销输入权限，必须重新点击才能恢复。</summary>
        public void ReleasePointerForRearm()
        {
            _pointerCaptured = false;
            PointerCaptureEnabled = false;
            _captured = false;
            InputMode = ConsoleInputMode.Pointer;
            try { System.Windows.Input.Keyboard.ClearFocus(); } catch { }
            try { CursorClip.Release(); } catch { }
            try { _rawInput?.Stop(); } catch { }
            ReleaseInjectedButtonsAsync();
            ResetLookQueue();
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
                    // 不在低级钩子里解锁：MouseDown 时提前撤掉保护层，会让同一次
                    // 点击的后续事件穿透到远端游戏。首次 Down/Up 由保护层完整消费，
                    // MouseUp 后才调用 OnShieldReleased。这里不吞任何本机鼠标事件。
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

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool UpdateWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

        private const int SwHide = 0;
        private const int SwShowNoActivate = 4;
        private const uint RdwInvalidate = 0x0001;
        private const uint RdwErase = 0x0004;
        private const uint RdwAllChildren = 0x0080;
        private const uint RdwUpdateNow = 0x0100;

        private const uint SwpNosize = 0x0001;
        private const uint SwpNomove = 0x0002;
        private const uint SwpNozorder = 0x0004;
        private const uint SwpNoactivate = 0x0010;
        /// <summary>藏到屏幕外的坐标（远在虚拟屏左边，用户绝对看不到）。</summary>
        private const int ParkX = -32000;
        private const int ParkY = -32000;

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        /// <summary>
        /// 显式显示/隐藏 RDP 的原生窗口。
        ///
        /// ★ 为什么必须这么做（实测过的真实故障）：
        ///   RdpConsole 内部是 WindowsFormsHost → 独立 HWND，
        ///   WPF 的 Visibility.Collapsed **不会**隐藏它的原生子窗口。
        ///   主窗口切到启动台/设置时只改页面 Visibility，RDP 会继续浮在最上层，
        ///   盖住左侧导航和其它页面内容。
        ///
        /// ★ 为什么不"把控件摘出视觉树"：
        ///   那会让 ActiveX 在容器之间反复进出，正是 0x119 事故的高风险形态。
        ///   这里只动原生窗口的显示状态，控件树结构完全不变。
        /// </summary>
        /// <summary>
        /// 上次设置的可见状态。null = 还没设置过。
        /// ★ 用于幂等：状态没变就**不要**再动窗口。
        /// </summary>
        private bool? _nativeVisibleState;

        public void SetNativeVisible(bool visible)
        {
            if (_disposed) return;

            // ★★ 幂等短路（2.17.3 修）。★★
            //
            //   用户实测：「中控页面 RDP 画面之间切换也会晃动一下，配置菜单切 RDP 也会缩放一下」。
            //
            //   根因就在这里：SyncNativeVisibility 每次切页会对**所有**画面无差别调用本方法，
            //   于是本来就在原位、正显示着的画面也会被「移到屏幕外(-32000) → 再移回」，
            //   而移回要调 RestoreHostToLayout()，它内部强制 UpdateLayout() 同步重排整棵树。
            //   几何最终不变（实测切页前后窗口/内容区尺寸完全一致），但中间有瞬移+重排+重绘，
            //   视觉上就是「按左上角缩放了一下」。
            //
            //   状态没变就什么都不做 —— 既不移动窗口，也不触发重新布局。
            if (_nativeVisibleState == visible) return;
            _nativeVisibleState = visible;

            // ★ 不再用 SW_HIDE 来"藏"RDP 窗口。
            //
            //   实测结论：SW_HIDE 会让 RDP 丢弃绘制表面，之后 SW_SHOW 回来是纯黑，
            //   只有远端产生新画面才恢复（InvalidateRect / RedrawWindow 都救不回来）。
            //
            //   改用「移到屏幕外」：窗口始终处于显示状态，绘制表面一直活着，
            //   移回来立刻就是正确画面。视觉上与隐藏完全一样（用户看不到）。
            try
            {
                var hostHwnd = _host?.Handle ?? IntPtr.Zero;
                if (hostHwnd == IntPtr.Zero) return;

                if (visible)
                {
                    // ★ 恢复：必须让 WPF 重新把宿主窗口放回它自己的布局位置。
                    //   之前这里写的是 SWP_NOMOVE（"不要移动"），结果窗口停在
                    //   屏幕外的 -32000 一直不动 —— 表现为"最小化再回来，画面消失"。
                    RestoreHostToLayout(hostHwnd);
                }
                else
                {
                    // 藏到屏幕外：尺寸保持（SWP_NOSIZE），避免触发 RDP 重协商。
                    SetWindowPos(hostHwnd, IntPtr.Zero, ParkX, ParkY, 0, 0,
                        SwpNosize | SwpNozorder | SwpNoactivate);
                }
            }
            catch { }

            if (visible)
            {
                // 移回来之后刷新一次，确保立刻呈现而不是等下一次远端更新。
                try { RefreshViewport(); } catch { }
            }

            Debug("RDP 原生窗口 " + (visible ? "移回原位" : "移到屏幕外"));
        }

        /// <summary>
        /// 把宿主窗口放回 WPF 为它安排的位置。
        ///
        /// 做法：先让 WPF 失效布局（宿主被移到屏幕外后，WPF 并不知道位置变了），
        /// 再读宿主相对屏幕的真实位置，用 SetWindowPos 显式纠正。
        /// 这样即使 WPF 之后自己重排，也不会把窗口留在屏幕外。
        /// </summary>
        private void RestoreHostToLayout(IntPtr hostHwnd)
        {
            try
            {
                // ★★ 不要在这里无条件 InvalidateMeasure/Arrange + UpdateLayout。★★
                //
                //   用户实测（精确复现序列）：
                //     「配置切 A 不晃 / 切配置切 A 不晃 / 切配置切 B 晃 / 再切配置切 B 不晃 / 切配置切 A 晃」
                //   —— 即「某画面第一次从隐藏恢复成可见」时才晃，同一画面再次恢复就不晃。
                //
                //   原因就在这里：WPF 布局槽位从来没变过，变的只是原生 HWND 的屏幕坐标
                //   （被 SetWindowPos 移到了屏幕外的 ParkX/ParkY）。
                //   PointToScreen 返回的是 WPF 的布局坐标，与原生窗口当前在哪无关，
                //   所以**不需要**先失效布局就能读到正确位置。
                //
                //   无条件 UpdateLayout() 会在窗口"正要显示"的这一拍强制重排整棵可视化树，
                //   视觉上就是画面抖一下。第一次恢复时布局可能真的还没跑过（IsArrangeValid=false），
                //   所以只在确实需要时才补一次 —— 需要时也只影响这一个宿主。
                bool needLayout = false;
                try
                {
                    needLayout = !_host.IsMeasureValid || !_host.IsArrangeValid;
                }
                catch { needLayout = true; }

                if (needLayout)
                {
                    try { _host.InvalidateMeasure(); } catch { }
                    try { _host.InvalidateArrange(); } catch { }
                    try { UpdateLayout(); } catch { }
                }

                // ② 读 WPF 认为它应该在的屏幕位置（物理像素）。
                int x = 0, y = 0;
                try
                {
                    if (_host.IsVisible && _host.ActualWidth > 0 && _host.ActualHeight > 0)
                    {
                        var tl = _host.PointToScreen(new Point(0, 0));
                        x = (int)Math.Round(tl.X);
                        y = (int)Math.Round(tl.Y);

                        // PointToScreen 在窗口最小化时会返回垃圾值，丢掉。
                        if (x < -20000 || y < -20000) { x = 0; y = 0; }
                    }
                }
                catch { }

                // ③ 显式移回（SWP_NOZORDER | SWP_NOSIZE | SWP_NOACTIVATE）。
                SetWindowPos(hostHwnd, IntPtr.Zero, x, y, 0, 0,
                    SwpNosize | SwpNozorder | SwpNoactivate);

                Debug("RDP 宿主窗口移回 (" + x + "," + y + ")");
            }
            catch (Exception ex) { Debug("恢复宿主窗口位置失败：" + ex.Message); }
        }

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
                RefreshUiCache();
                InputTrace.Write("RELAY_HOTKEY_DROP", "丢弃目标会话热键 会话=" + CurrentSessionId
                    + " _captured=" + _captured + " IsVisible=" + _uiVisibleCache
                    + " ownerActive=" + _ownerActiveCache + " HotkeyArmed=" + HotkeyArmed
                    + " IsConnected=" + IsConnected + " _activeState=" + _activeState);
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
        /// <summary>
        /// 运行日志（正常状态**不落盘**）。
        ///
        /// ★ 长期挂机不能把每秒的正常状态都写进文件：
        ///   原来每次热键注册、钩子安装、焦点切换、监控 tick 都写一行，
        ///   实测 1 小时 4593 行（442KB），一年可达数 GB。
        ///
        /// 现在只记录真正有价值的内容：
        ///   · 异常 / 失败 / 拒绝 / 超时 / 跳过（含"失败""异常""拒绝""超时""错误"等字样）
        ///   · 关键状态跃迁（连接成功、投送成功、进入/退出自由视角）
        /// 其余一律丢弃。
        ///
        /// 需要完整调试时，命令行加 --verbose-log 即可恢复全量。
        /// </summary>
        internal static void Debug(string message)
        {
            if (message == null) return;
            try
            {
                if (!VerboseLog && !IsImportant(message)) return;
                // 按日期分文件：rdp-debug-20261116.log
                AsyncLog.Append(
                    ConfigStore.DailyLogPath("rdp-debug"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>是否记录全量日志（--verbose-log 或环境变量 SOULLINK_VERBOSE_LOG=1）。</summary>
        internal static bool VerboseLog { get; } = DetectVerbose();

        private static bool DetectVerbose()
        {
            try
            {
                foreach (var a in Environment.GetCommandLineArgs())
                    if (a != null && a.Equals("--verbose-log", StringComparison.OrdinalIgnoreCase)) return true;
                string? env = Environment.GetEnvironmentVariable("SOULLINK_VERBOSE_LOG");
                return env == "1";
            }
            catch { return false; }
        }

        /// <summary>判断一条日志是否值得落盘（异常/关键跃迁）。</summary>
        private static bool IsImportant(string m)
        {
            // ① 异常类关键词
            if (m.Contains("失败") || m.Contains("异常") || m.Contains("拒绝")
                || m.Contains("超时") || m.Contains("错误") || m.Contains("不可用")
                || m.Contains("跳过") || m.Contains("警告") || m.Contains("不匹配")
                || m.Contains("无响应") || m.Contains("未就绪"))
                return true;

            // ② 关键状态跃迁
            if (m.Contains("注入代理已投送") || m.Contains("注入代理已连接")
                || m.Contains("进入自由视角") || m.Contains("退出自由视角")
                || m.Contains("真实会话号") || m.Contains("通道投送成功")
                || m.Contains("已连上") || m.Contains("连接成功"))
                return true;

            // ③ 其余（焦点切换、钩子安装、热键注册、监控 tick、边框色…）丢弃
            return false;
        }

        /// <summary>创建 OCX（幂等）。</summary>
        private void EnsureHost()
        {
            if (_ax != null) return;

            Exception? last = null;
            var failures = new List<string>();
            foreach (var clsid in CandidateClsids)
            {
                RdpAxHost? ax = null;
                try
                {
                    ax = new RdpAxHost(clsid);
                    // ★ 点击瞬间就广播"我被点了"，外层据此当拍切焦点。
                    //   解锁态下 RDP 会吞掉鼠标消息，只靠看门狗轮询会慢半拍
                    //   （用户实测：第一下点击不切焦点）。
                    ax.MouseActivated += OnAxMouseActivated;

                    // ★★ 顺序不能改：先"挂进视觉树 + 可见"，再创建 OCX。★★
                    //
                    //   RdpAxHost.EnsureCreated() 内部是 Control.CreateControl()，
                    //   而 WinForms 的 CreateControl() 在控件**不可见**时是空操作：
                    //   宿主 WindowsFormsHost 保持 Visibility=Collapsed 且没有父窗口
                    //   时，AxHost 的窗口根本不会被创建，AttachInterfaces 也不会被
                    //   调用 → GetOcx() 恒为 null → 每个候选都"创建后 OCX 为空"，
                    //   最终报"无法创建 RDP 控件"（2.17.17 回归，2026-11-20 实测）。
                    //
                    //   本机 .NET 8 实测对照（同一台机器、同一 CLSID）：
                    //     · host 分离 + Collapsed → OCX 为空，且**不抛任何异常**
                    //     · host 分离 + Visible   → OCX 正常
                    //     · host 已挂 + Collapsed → OCX 正常（要求外层已在窗口里）
                    //   失败时下方会把宿主收回，未连接的 Console 依然不会在窗口上
                    //   露出方形 HWND（见构造函数里关于 DWM 圆角的说明）。
                    MountHostForCreation();
                    _host.Child = ax;

                    ax.EnsureCreated();
                    if (ax.Ocx == null)
                    {
                        failures.Add(clsid + "：控件创建后 OCX 为空（控件未创建，宿主不可见或 COM 未激活）");
                        DetachControl(ax);
                        continue;
                    }

                    _ax = ax;
                    _ocx = ax.Ocx;
                    LogBus.Info("RDP 控件就绪", "CLSID " + clsid);
                    return;
                }
                catch (Exception ex)
                {
                    failures.Add(clsid + "：" + ex.GetType().Name
                                 + " 0x" + ex.HResult.ToString("X8") + " " + ex.Message);
                    last = ex;
                    DetachControl(ax);
                }
            }

            // ★ 全部候选都失败：把宿主收回（保持未挂载、不可见）。
            //   未连接的 Console 不该在窗口上留下一块方形 HWND。
            UnmountHostAfterFailure();

            // ★ 每个候选的失败原因都要带出来：
            //   原来只保留最后一条，v9/v10 的真实 HRESULT 被 v11 的
            //   "OCX 为空"覆盖，排查时完全看不出是"没注册类"还是"不可见"。
            throw new InvalidOperationException(
                "无法创建 RDP 控件。系统可能缺少远程桌面 ActiveX 组件。\n"
                + string.Join("\n", failures), last);
        }

        /// <summary>
        /// 创建 OCX 前把宿主挂进视觉树并置为可见 —— 这是 CreateControl() 真正创建
        /// ActiveX 窗口的前提条件（见 EnsureHost 里的实测说明）。
        /// </summary>
        private void MountHostForCreation()
        {
            try
            {
                if (!Children.Contains(_host)) Children.Add(_host);
                if (_host.Visibility != Visibility.Visible) _host.Visibility = Visibility.Visible;
                _host.UpdateLayout();
            }
            catch { }
        }

        /// <summary>放弃这个候选控件：摘掉并释放，避免残留半成品 HWND / RCW。</summary>
        private void DetachControl(RdpAxHost? ax)
        {
            if (ax == null) return;
            try { if (ReferenceEquals(_host.Child, ax)) _host.Child = null; } catch { }
            try { ax.MouseActivated -= OnAxMouseActivated; } catch { }
            try { ax.Dispose(); } catch { }
        }

        /// <summary>创建失败后恢复"未挂载、不可见"状态，保住 DWM 圆角。</summary>
        private void UnmountHostAfterFailure()
        {
            try
            {
                _host.Child = null;
                if (Children.Contains(_host)) Children.Remove(_host);
                _host.Visibility = Visibility.Collapsed;
            }
            catch { }
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

            // ── 0. 地址：**完全按配置**（用户明确要求：填多少就是多少）──
            //
            //   ★★ 2.17.44：撤销 2.17.43 的"按实时会话号纠正地址"★★
            //
            //   用户原话："你不能修改我的配置地址啊，它会涉及别的服务，填多少就是多少"。
            //
            //   这条必须记住：RdpHost 不只是"内嵌 RDP 连哪"这一个用途，
            //   它还可能被其它服务/流程引用。程序**无权擅自改写**用户的配置。
            //   曾经为了让"账号重登后地址过期"自愈而自动纠写，属于越界。
            //
            //   现在的行为（与 2.17.42 及之前一致）：
            //     · 配置有地址 → 就用它，原样使用，不纠正、不回写；
            //     · 配置为空   → 才去探测（RdpHostProbe）。
            //   地址是否过期由用户自己维护；程序只在**连接结果**上做判断。
            bool autoHost = string.IsNullOrWhiteSpace(account.RdpHost);
            string? probed = null;
            if (autoHost)
            {
                SetStatus("探测可用的 RDP 地址 …");
                probed = await RdpHostProbe.ProbeAsync(sessionId, null).ConfigureAwait(true);
            }

            string host = account.ResolveHost(sessionId, probed);
            string hostSource = autoHost
                ? (probed == null ? "自动探测（没找到）" : "自动探测")
                : "手动配置";

            string user = account.EffectiveRdpUserName;
            string password = ConfigStore.GetPassword(account);

            SetStatus("准备会话 " + sessionId + " …");
            LogBus.Info("连接 RDP " + host + " ← " + user,
                "会话 " + sessionId + "｜" + hostSource);

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

                // ★ 事件触发：RDP **连接成功之后**才查会话。
                //   连接过程中会话状态还在变，那时查没有意义；
                //   连上之后会话号已确定，此时刷新最准确。
                try { Services.AccountStateCenter.Kick(App.Config, "RDP 连接成功 会话" + activeSessionId); } catch { }
                            LogBus.Ok("RDP 已连上 " + account.Caption,
                                "地址 " + host + " · 耗时 " + sw.ElapsedMilliseconds + " ms");
                            // ★ 连接成功后**不再直接投送**：投送统一由会话监控负责
                            //   （它在会话稳定 12 秒后触发），避免与监控重复投送。
                            //   注意：非自动连接场景（手动打开画面）仍需要通道，
                            //   所以这里只在没配自动连接时才投送。
                            bool autoManaged = App.Config.Settings.AutoConnectAccountIds?.Count > 0;
                            if (!autoManaged && connection == _connectionGeneration)
                                BeginInput(activeSessionId, account);
                            else if (autoManaged)
                                Debug("连接成功；通道交由会话监控统一投送（会话 " + activeSessionId + "）");
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
            // SmartSizing 由下面的缩放模式统一决定（见 scaleMode 那段），
            // 这里不再写死 false —— 两处冲突会让"拉伸"模式实际不生效。
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
            bool smartOk = false;
            try
            {
                adv2.SmartSizing = stretch;
                // 回读确认 OCX 真的接受了这个值（属性可能是只写的）。
                smartOk = (bool)adv2.SmartSizing;
            }
            catch (Exception ex) { Debug("SmartSizing 设置失败：" + ex.Message); }
            Debug("Desktop = " + w + "x" + h + " 色深=" + depth
                  + " Mode=" + scaleMode + " SmartSizing=" + stretch + "(回读 " + smartOk + ")");

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
        // ── 跟随窗口模式的尺寸协商（防抖 + 全局串行）────────────────────────
        //
        // ★ 为什么必须防抖（实测 + 事故报告）：
        //   1. 拖动窗口边缘时 SizeChanged 每帧触发，直接回调会把
        //      UpdateSessionDisplaySettings 打成高频调用；
        //   2. 切标签/切布局会让控件短暂离开视觉树再挂回，尺寸会经历
        //      "变小 → 归零 → 恢复"，直接回调等于每次都重新协商一次；
        //   3. 0x119 事故中「拖动/缩放时反复 UpdateSessionDisplaySettings」
        //      被列为高风险组合，必须收敛成"停手之后只协商一次"。
        //
        // ★ 为什么必须串行：同屏多画面时 4 个画面会同时重协商，
        //   而 Minitorch 的显示驱动路径经不起并发重协商。全局信号量保证
        //   同一时刻只有一个画面在改远端分辨率。

        /// <summary>尺寸停止变化后等待多久才协商（毫秒）。</summary>
        public const int ResizeSettleMs = 500;

        private System.Windows.Threading.DispatcherTimer? _resizeDebounce;
        private (int width, int height) _pendingViewportPixels;

        /// <summary>全局串行：同一时刻只允许一个画面重协商远端分辨率。</summary>
        private static readonly System.Threading.SemaphoreSlim ResizeGate = new(1, 1);

        /// <summary>
        /// 视口尺寸变了：只记录目标，重置防抖计时器。
        /// 真正的协商在 <see cref="CommitPendingResizeAsync"/> 里做。
        /// </summary>
        public void NoteViewportSizeChanged()
        {
            if (_disposed || !_hasConnected || _ocx == null) return;
            if ((Account?.RdpScaleMode ?? App.Config.Settings.RdpScaleMode) != 0) return;

            var rect = ViewportScreenRect();
            if (rect == null) return;
            if (rect.Value.width < 200 || rect.Value.height < 200) return;

            _pendingViewportPixels = (rect.Value.width, rect.Value.height);

            if (_resizeDebounce == null)
            {
                _resizeDebounce = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(ResizeSettleMs) };
                _resizeDebounce.Tick += async (_, __) =>
                {
                    _resizeDebounce!.Stop();
                    await CommitPendingResizeAsync().ConfigureAwait(true);
                };
            }
            _resizeDebounce.Stop();
            _resizeDebounce.Start();
        }

        /// <summary>防抖到期后真正协商一次远端分辨率。</summary>
        private async Task CommitPendingResizeAsync()
        {
            if (_disposed || !_hasConnected || _ocx == null) return;
            if ((Account?.RdpScaleMode ?? App.Config.Settings.RdpScaleMode) != 0) return;

            // 鼠标还按着（多半在拖边框）就先不协商，等用户停手。
            for (int i = 0; i < 20 && System.Windows.Input.Mouse.LeftButton
                     == System.Windows.Input.MouseButtonState.Pressed; i++)
                await Task.Delay(300).ConfigureAwait(true);

            int w = _pendingViewportPixels.width;
            int h = _pendingViewportPixels.height;
            if (w < 200 || h < 200) return;

            // 尺寸没变就不协商（切回来、布局微调都可能落在这里）。
            try
            {
                if (Math.Abs((int)_ocx.DesktopWidth - w) <= 1 &&
                    Math.Abs((int)_ocx.DesktopHeight - h) <= 1) return;
            }
            catch { }

            await ResizeGate.WaitAsync().ConfigureAwait(true);
            try
            {
                // 排队期间可能已经断开/销毁，重新确认。
                if (_disposed || !_hasConnected || _ocx == null) return;
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
            finally
            {
                ResizeGate.Release();
            }
        }

        /// <summary>
        /// 立即协商（兼容旧调用点）。不做防抖，仅供显式全屏/自适应切换使用。
        /// 拖动与切标签走 <see cref="NoteViewportSizeChanged"/>。
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
        private async void BeginInput(int sessionId, Account account)
        {
            // ★ 不能要求 InputAllowed（前台 + 可见）。
            //   多账号挂机时后台账号的 Console 不在 ViewportHost 里、也不是前台，
            //   InputAllowed 恒为 false，导致通道根本建不起来 → 监控层反复重试
            //   → 同一账号出现多个 --relay 进程（会话 3 一度有两个），
            //   表现为"无法自由切换账号"（用户实测）。
            //   通道属于连接状态，与"是否正在看这个画面"解耦；前台限制只用于拦截键鼠消息。
            // ★★ 每一种提前返回都必须留痕。★★
            //   原来这三条判断合并成一个 return，一条日志都没有 ——
            //   用户看到的现象是「监控说投送成功，但根本没有代理进程」，
            //   而我花了很久才定位到是这个静默 return。
            if (!InjectEnabled)
            {
                Debug("投送跳过：注入被禁用（会话 " + sessionId + "）");
                return;
            }
            if (_disposed)
            {
                Debug("投送跳过：Console 已销毁（会话 " + sessionId + "）");
                return;
            }
            if (System.Threading.Interlocked.CompareExchange(ref _inputStartPending, 1, 0) != 0)
            {
                // 上一次投送还没结束（DeployAsync 最长等 20 秒）。
                // 记一条，否则"永远投送不出去"完全无从排查。
                Debug("投送跳过：上一轮投送仍在进行中（会话 " + sessionId + "）");
                return;
            }

            // ★ 已有可用通道且会话号相同 → 直接复用，绝不重复投送。
            //   RelayClient 每次投送都会新建一条命名管道和一个代理进程，
            //   重复投送会在同一会话里留下多个 --relay（实测会话 4 出现三个），
            //   它们争用同一个桌面输入，表现为"切到该账号后操作异常 / 无法切换"。
            if (Input?.Relay.IsConnected == true && CurrentSessionId == sessionId)
            {
                System.Threading.Interlocked.Exchange(ref _inputStartPending, 0);
                Debug("通道已存在且会话相同（" + sessionId + "），跳过重复投送");
                return;
            }

            // ★ 同一会话正在投送中 → 直接放弃。
            //   _inputStartPending 只防同一 Console 重入，挡不住不同路径
            //   （监控投送 / 切页重建 / 连接后投送）同时进入同一会话。
            if (!DeployingSessions.TryAdd(sessionId, 0))
            {
                System.Threading.Interlocked.Exchange(ref _inputStartPending, 0);
                Debug("会话 " + sessionId + " 正在投送中，本次跳过");
                return;
            }
            try
            {
                // ★ 硬超时：DeployAsync 内部虽有 20 秒超时，
                //   但异常路径下可能不返回，导致 _inputStartPending 永久为 1、
                //   后续所有投送被永久静默挡掉（用户实测过这个症状）。
                var core = BeginInputCore(sessionId, account);
                var finished = await Task.WhenAny(core, Task.Delay(45000));
                if (finished != core)
                    Debug("投送超时（45 秒未返回）会话 " + sessionId + "，强制释放标志");
                else
                    await core;   // 传播内部异常
            }
            catch (Exception ex)
            {
                Debug("投送异常：" + ex.Message);
            }
            finally
            {
                DeployingSessions.TryRemove(sessionId, out _);
                // ★ 必须在这里兜底重置：BeginInputCore 内部若提前 return，
                //   它自己的 finally 只覆盖内部逻辑；这条外层路径要保证标志一定归零，
                //   否则后续所有投送都会被 "已在进行中" 挡掉（表现为通道再也建不起来）。
                System.Threading.Interlocked.Exchange(ref _inputStartPending, 0);
            }
        }

        /// <summary>正在投送的会话号集合（跨 Console 实例，按会话去重）。</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte>
            DeployingSessions = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();

        private async Task BeginInputCore(int sessionId, Account account)
        {
            _traceSw = System.Diagnostics.Stopwatch.StartNew();
            RefreshUiCache();
            InputTrace.Write("BIC_ENTER", "实例=" + _inst + " 会话=" + sessionId + " 本Console.Input=" + TraceId(Input)
                + " 当前跟踪会话=" + CurrentSessionId + " _activeState=" + _activeState
                + " IsVisible=" + _uiVisibleCache + " IsConnected=" + IsConnected
                + " 调用方=" + InputTrace.Caller());
            await StopInputAsync().ConfigureAwait(true);
            int generation = System.Threading.Interlocked.Increment(ref _inputGeneration);

            // ★ 投送前清理该会话遗留的孤儿代理进程。
            //   重复投送会在同一会话留下多个 --relay，它们争用同一桌面输入，
            //   表现为切到该账号后操作异常（实测会话 4 出现两个）。
            try { Services.RelayClient.KillOrphanAgents(sessionId); } catch { }

            CurrentSessionId = sessionId;
            try
            {
                if (!string.IsNullOrWhiteSpace(App.Config.Settings.LockHotkey))
                    _toggleHotkey = App.Config.Settings.LockHotkey;

                var relay = new RelayClient(account.UserName, sessionId);
                // ★ ConfigureAwait(false)：投送包含 schtasks 建任务 + 等代理连回管道，
                //   最长 20 秒。若回到 UI 线程继续，界面会在这段时间无法响应
                //   （用户实测：多账号自动投送时卡死）。后续对控件的操作会显式切回 UI。
                // ★ 事件触发：投送前后会话状态会变（代理起来、通道建立）。
                try { Services.AccountStateCenter.Kick(App.Config, "投送开始 会话" + sessionId); } catch { }
                var (ok, msg) = await relay.DeployAsync(account).ConfigureAwait(false);
                try { Services.AccountStateCenter.Kick(App.Config, "投送" + (ok ? "成功" : "失败") + " 会话" + sessionId); } catch { }
                InputTrace.Write("DEPLOY_RESULT", "会话=" + sessionId + " ok=" + ok
                    + " AgentPid=" + relay.AgentPid + " BrokerBacked=" + relay.BrokerBacked
                    + " IsConnected=" + relay.IsConnected + " msg=" + msg
                    + " 距投送开始=" + (_traceSw == null ? "n/a" : _traceSw.ElapsedMilliseconds + "ms"));

                // 只保留"失败"与"关键身份"这类有用信息，正常成功路径不刷日志。
                if (!ok) Debug("投送失败：会话 " + sessionId + " · " + msg);

                if (!ok)
                {
                    relay.Dispose();
                    Debug("注入代理投送失败：" + msg);
                    InputActivity?.Invoke(this, "注入不可用：" + msg);
                    // ★ 投送失败同样重试：会话刚登录/杀软临时拦截都可能是一过性的
                    ScheduleInputRebuild("投送失败");
                    return;
                }

                // ★ 这里只判"是否过期/已销毁"，不判 InputAllowed。
                //   否则后台账号投送成功后会被立刻丢弃，代理成了孤儿进程，
                //   监控层又重试 → 同一会话出现多个 --relay（实测会话 3 有两个）。
                if (generation != _inputGeneration || _disposed)
                {
                    relay.Dispose();
                    return;
                }

                // ★ 登记到全局注册表：自动化流程的鼠键执行器靠它拿到通道
                RelayClient.Register(sessionId, relay);

                // 投送在后台线程完成，这里切回 UI 线程再继续操作控件
                await Dispatcher.InvokeAsync(() => { }).Task.ConfigureAwait(true);
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

                if (!ReferenceEquals(Input, input))
                {
                    InputTrace.Write("BIC_ABANDON", "通道已被替换，放弃本次投送 会话=" + sessionId
                        + " 当前Input=" + TraceId(Input) + " 本次Input=" + TraceId(input)
                        + " 距投送开始=" + (_traceSw == null ? "n/a" : _traceSw.ElapsedMilliseconds + "ms"));
                    return;
                }
                // ★★ 这里**绝对不能**再按 InputAllowed 停通道。★★
                //
                //   2.17.20 取证结论（交接文档 4.1 的根因）：
                //   会话监控投送前会显式 SetForegroundActive(false)（#1079），
                //   于是后台账号的 _activeState 恒为 1、IsVisible 恒为 false，
                //   InputAllowed 永远为 false —— 而这条分支正好在
                //   "投送成功 + 握手完成"之后执行，等于把刚建好、已经连上的代理当场杀掉。
                //
                //   实测日志（input-trace）：
                //     02:54:42.200 DEPLOY_RESULT ok=True IsConnected=True
                //     02:54:43.708 KILL_INPUTALLOWED _activeState=1 IsVisible=False   ← 在这里被杀
                //     02:54:43.710 RL_SEND_EXIT
                //     代理侧：02:54:42.021 已连上宿主管道 → 02:54:43.713 宿主断开（存活 1.7 秒）
                //
                //   通道属于**连接状态**，与"是否正在看这个画面"解耦 ——
                //   这与 BeginInput 顶部（#2576）以及 SetForegroundActive（#554）的注释完全一致，
                //   是这一条判断把它们推翻了。前台限制只用于拦截键鼠消息，
                //   InputAllowed 在消息处理路径里仍然生效（#870 / #1388）。
                //
                //   真正的清理仍然由 Disconnect / Dispose 负责。
                RefreshUiCache();
                InputTrace.Write("PROBE", "实例=" + _inst + " 会话=" + sessionId + " usable=" + usable + " why=" + why
                    + " _activeState=" + _activeState + " IsVisible=" + _uiVisibleCache
                    + " 距投送开始=" + (_traceSw == null ? "n/a" : _traceSw.ElapsedMilliseconds + "ms"));
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

                InputTrace.Write("BIC_OK", "实例=" + _inst + " 会话=" + sessionId + " usable=" + usable
                    + " Input=" + TraceId(Input) + " 距投送开始=" + (_traceSw == null ? "n/a" : _traceSw.ElapsedMilliseconds + "ms"));

                // ★★ 这里曾经有一段"兜底恢复前台资格"的代码，已删除。★★
                //   它读 IsVisible 并调 SetForegroundActive —— 两者都是 UI 线程专属，
                //   而本方法在后台线程继续（DeployAsync 用 ConfigureAwait(false)）。
                //   实测抛出 "The calling thread cannot access this object because a
                //   different thread owns it."，被外层 catch 判成"建立输入通道异常"，
                //   于是投送明明成功却被当作失败并触发重建，HotkeyArmed 永远停在 false
                //   （表现为"重装服务后按侧键完全没反应"）。
                //
                //   前台资格的归属由 ConsolePage 负责（ShowViewPageAsync / SetFocusFromShield），
                //   这里不该越权修改，更不该在后台线程碰 UI。
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

        /// <summary>
        /// 只让**当前这条注入连接失效**，用于 Broker 服务更新等场景。
        ///
        /// ★ 为什么不用 SetForegroundActive(false)：
        ///   那是"前台/后台"语义，会一并改 _activeState、关掉 HotkeyArmed、
        ///   释放捕获、卸载鼠标钩子。而这些与"代理连接指向旧服务登记表"毫无关系。
        ///   实测（2026-11-20 03:12）服务更新后就是这么把热键打死、且无人恢复的。
        ///
        /// 这里只处置通道本身：StopInputAsync 会递增 generation、注销注册表、
        /// Dispose 旧 RelayClient（向旧服务发 stop），前台状态一概不碰。
        /// </summary>
        public void InvalidateAgentConnection()
        {
            try
            {
                if (Input == null) return;
                InputTrace.Write("INVALIDATE_AGENT", "仅失效代理连接（不碰前台资格）会话=" + CurrentSessionId
                    + " Input=" + TraceId(Input) + " HotkeyArmed=" + HotkeyArmed
                    + " _activeState=" + _activeState);
                _ = StopInputAsync();
            }
            catch (Exception ex) { Debug("失效代理连接异常：" + ex.Message); }
        }

        private async Task StopInputAsync()
        {
            var traceOld = Input;
            // ★ 后台线程也会走到这里（InvalidateAgentConnection / Dispose），
            //   所以只读缓存，绝不碰 IsVisible 本体。
            InputTrace.Write("STOP_ENTER", "实例=" + _inst + " 谁调的=" + InputTrace.Caller()
                + " 将被停的Input=" + TraceId(traceOld)
                + " 跟踪会话=" + CurrentSessionId + " _activeState=" + _activeState
                + " IsVisible=" + _uiVisibleCache + " IsConnected=" + IsConnected
                + " _disposed=" + _disposed
                + " 距本Console投送开始=" + (_traceSw == null ? "n/a" : _traceSw.ElapsedMilliseconds + "ms"));
            System.Threading.Interlocked.Increment(ref _inputGeneration);
            // ★ 停掉等待中的重建定时器：否则"主动停止"会被当成"通道断开"，
            //   重建 → 再停 → 再重建，形成死循环。
            _rebuildTimer?.Stop();
            _rebuildTimer = null;

            var old = Input;
            Input = null;
            _inputUsable = false;

            // 从全局注册表注销，避免自动化流程拿到已断开的通道
            try { if (CurrentSessionId > 0) RelayClient.Unregister(CurrentSessionId); } catch { }

            if (old == null)
            {
                InputTrace.Write("STOP_NOOP", "没有可停的通道（Input 已为 null）跟踪会话=" + CurrentSessionId);
                return;
            }
            await Task.Run(() =>
            {
                InputTrace.Write("STOP_DISPOSE", "开始 Dispose 会话=" + CurrentSessionId + " Input=" + TraceId(old)
                    + " 距投送开始=" + (_traceSw == null ? "n/a" : _traceSw.ElapsedMilliseconds + "ms"));
                try { old.Dispose(); } catch { }
            }).ConfigureAwait(true);
            InputTrace.Write("STOP_DONE", "已停会话=" + CurrentSessionId + " 距投送开始=" + (_traceSw == null ? "n/a" : _traceSw.ElapsedMilliseconds + "ms"));
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

        private System.Windows.Threading.DispatcherTimer? _stretchRefit;

        /// <summary>
        /// 拉伸模式下的重新铺满（防抖 200ms）。
        ///
        /// 只重新应用 SmartSizing，**不调用 UpdateSessionDisplaySettings**：
        /// 用户明确要求「不改远端分辨率，只有本地显示缩放」。
        /// </summary>
        private void ScheduleStretchRefit()
        {
            try
            {
                if (_disposed) return;
                // 只有 拉伸(1) / 原大小(2) 之外不处理；跟随窗口(0) 走分辨率协商。
                int mode = Account?.RdpScaleMode ?? App.Config.Settings.RdpScaleMode;
                if (mode != 1 && mode != 3) return;

                _stretchRefit ??= new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(200) };
                _stretchRefit.Tick -= OnStretchRefitTick;
                _stretchRefit.Tick += OnStretchRefitTick;
                _stretchRefit.Stop();
                _stretchRefit.Start();
            }
            catch { }
        }

        private void OnStretchRefitTick(object? sender, EventArgs e)
        {
            try
            {
                _stretchRefit?.Stop();
                ApplyStretchRefit();
            }
            catch { }
        }

        /// <summary>把控件重新按当前宿主尺寸铺满（不改远端分辨率）。</summary>
        public void ApplyStretchRefit()
        {
            try
            {
                if (_disposed || _ocx == null || !_hasConnected) return;
                int mode = Account?.RdpScaleMode ?? App.Config.Settings.RdpScaleMode;
                if (mode != 1 && mode != 3) return;

                if (_host == null || _host.ActualWidth < 4 || _host.ActualHeight < 4) return;

                // ★★ 不能再"先关再开"（2.17.3 修）。★★
                //
                //   原写法：
                //       adv2.SmartSizing = false;   ← 这一拍控件按 1:1 显示原始 1920x1080
                //       adv2.SmartSizing = true;    ← 再按客户区缩放
                //
                //   用户录屏抓到了中间那一帧：RDP 内层渲染窗口是 1920x1080，
                //   而外层 WindowsFormsHost 只有 771x395 —— 画面整个放大溢出窗口，
                //   就是用户描述的"明显是缩放了"。
                //
                //   「属性值没变不触发重算」这个担心是对的，但代价是必然露出一帧未缩放的原始画面。
                //   改用**不改变语义的刷新方式**：
                //     · 已经是 true 时，直接调用控件的重绘 + 尺寸重设，不碰 SmartSizing；
                //     · 只有当前状态确实是 false 才打开它（那本来就需要一帧）。
                dynamic adv2 = _ocx.AdvancedSettings2;

                bool current = true;
                try { current = (bool)adv2.SmartSizing; } catch { current = true; }

                if (!current)
                {
                    // 确实没开：打开它（这一帧本来就需要重算，无法避免）
                    try { adv2.SmartSizing = true; } catch { }
                }

                // 已经是拉伸状态：用"宿主尺寸没变也强制重铺"的替代手段 ——
                // 让 WinForms 宿主把尺寸重设一次，ActiveX 会按新客户区重新布局内容，
                // 全程 SmartSizing 保持开启，不会露出未缩放的中间帧。
                try
                {
                    var h = _host;
                    if (h != null && h.ActualWidth > 4 && h.ActualHeight > 4)
                    {
                        double w = h.Width, ht = h.Height;
                        // 微调 1 DIP 再改回：触发 WM_SIZE，控件按当前客户区重铺内容。
                        h.Width = w - 1;
                        h.Height = ht - 1;
                        h.Width = w;
                        h.Height = ht;
                    }
                }
                catch { }

                Debug("拉伸重铺 hwnd=" + (_host.Handle) + " " + _host.ActualWidth.ToString("0") + "x" + _host.ActualHeight.ToString("0"));
            }
            catch { }
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
            try { _resizeDebounce?.Stop(); } catch { }
            _resizeDebounce = null;
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
