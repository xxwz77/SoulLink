using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace SoulLink.Services
{
    /// <summary>
    /// 本机物理键盘钩子。只负责识别热键，不向 RDP 或目标会话发送输入。
    /// WH_KEYBOARD_LL 在 RDP 的窗口键盘处理之前运行，可阻止热键泄漏给游戏。
    /// </summary>
    public sealed class LowLevelKeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const uint LLKHF_INJECTED = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private IntPtr _hook;
        private LowLevelKeyboardProc? _proc;
        private HotkeySpec? _spec;
        private readonly HotkeyMatcher _matcher = new HotkeyMatcher(null);
        private uint _modifiers;
        private static uint _sharedModifiers;
        private static int _installedCount;

        /// <summary>安装/重挂时同步物理修饰键；回调内不读取异步按键状态。</summary>
        public static uint CurrentModifiers => Volatile.Read(ref _installedCount) > 0
            ? Volatile.Read(ref _sharedModifiers)
            : ReadPhysicalModifiers();

        public static uint ReadPhysicalModifiers()
        {
            uint mask = 0;
            if ((GetAsyncKeyState(0x11) & 0x8000) != 0) mask |= HotkeySpec.CtrlMask;
            if ((GetAsyncKeyState(0x12) & 0x8000) != 0) mask |= HotkeySpec.AltMask;
            if ((GetAsyncKeyState(0x10) & 0x8000) != 0) mask |= HotkeySpec.ShiftMask;
            if ((GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0)
                mask |= HotkeySpec.WinMask;
            return mask;
        }

        public HotkeySpec? Spec
        {
            get => _spec;
            set
            {
                _spec = value;
                _matcher.Spec = value;
            }
        }

        /// <summary>是否处于可使用该热键的 RDP 画面。回调必须轻量且不能操作输入。</summary>
        public Func<bool>? CanTrigger { get; set; }

        /// <summary>自由视角中允许吞掉组合键的修饰键，避免 Alt/Ctrl 半截泄漏。</summary>
        public Func<bool>? CanSuppressModifiers { get; set; }

        public Action? Pressed { get; set; }
        public bool IsHooked => _hook != IntPtr.Zero;

        public bool Install()
        {
            if (_hook != IntPtr.Zero) return true;
            _proc = Callback;
            try
            {
                // 新安装的 LL 钩子会排在已安装的 RDP 钩子之前；重挂见 Rearm()。
                _modifiers = ReadPhysicalModifiers();
                Volatile.Write(ref _sharedModifiers, _modifiers);
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
                if (_hook == IntPtr.Zero) return false;
                Interlocked.Increment(ref _installedCount);
                return true;
            }
            catch
            {
                _hook = IntPtr.Zero;
                return false;
            }
        }

        /// <summary>
        /// RDP 获得焦点后重挂本机钩子，修正两个 LL 钩子的先后顺序。
        /// 只在 UI 消息处理路径调用，不在另一个钩子回调里嵌套重挂。
        /// </summary>
        public bool Rearm()
        {
            if (_hook == IntPtr.Zero) return false;
            Uninstall();
            return Install();
        }

        public void Uninstall()
        {
            if (_hook == IntPtr.Zero) return;
            try { UnhookWindowsHookEx(_hook); } catch { }
            _hook = IntPtr.Zero;
            Interlocked.Decrement(ref _installedCount);
            _modifiers = 0;
            Volatile.Write(ref _sharedModifiers, 0);

        }

        private IntPtr Next(int nCode, IntPtr wParam, IntPtr lParam)
            => CallNextHookEx(_hook, nCode, wParam, lParam);

        private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return Next(nCode, wParam, lParam);
            try
            {
                int msg = wParam.ToInt32();
                bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                if (!isDown && !isUp) return Next(nCode, wParam, lParam);

                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                bool swallow = _matcher.Key(info.vkCode, isDown,
                    (info.flags & LLKHF_INJECTED) != 0,
                    CanTrigger?.Invoke() == true,
                    CanSuppressModifiers?.Invoke() == true,
                    out bool matched);
                _modifiers = _matcher.Modifiers;
                Volatile.Write(ref _sharedModifiers, _modifiers);
                if (matched)
                {
                    SoulLink.Controls.RdpConsole.Debug("本机热键命中：" + _spec
                        + " vk=" + info.vkCode + " mods=" + _modifiers + " injected=False");
                    try { Pressed?.Invoke(); } catch { }
                }
                return swallow ? (IntPtr)1 : Next(nCode, wParam, lParam);
            }
            catch (Exception ex)
            {
                // 识别失败时放行，绝不能让键盘失效。
                SoulLink.Controls.RdpConsole.Debug("键盘热键识别异常：" + ex.Message);
                return Next(nCode, wParam, lParam);
            }
        }

        public void Dispose() => Uninstall();
    }
}
