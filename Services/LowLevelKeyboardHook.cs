using System;
using System.Runtime.InteropServices;

namespace SoulLink.Services
{
    /// <summary>
    /// 全局低级键盘钩子（WH_KEYBOARD_LL）。
    ///
    /// ★ 为什么必须用它，不能只靠 RegisterHotKey：
    ///   RDP 控件的 KeyboardHookMode=1（"发送到远端"）会在输入链路里抢占按键，
    ///   用户按 Ctrl+Alt+K 时键值被直接送进远端会话，前台（本进程）收不到，
    ///   RegisterHotKey 的 WM_HOTKEY 也就永远不触发 ——
    ///   现象就是"热键好像直接作用在 RDP 上，前台没反应"（用户实测反馈）。
    ///
    ///   低级键盘钩子在输入链路里比窗口和热键处理都更靠前，
    ///   且后安装的钩子会先于先安装的执行，正好能抢在 RDP 之前拿到按键。
    ///   这与本项目鼠标那边用 WH_MOUSE_LL 的原因是同一个（见 LowLevelMouseHook）。
    /// </summary>
    public sealed class LowLevelKeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

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
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private IntPtr _hook = IntPtr.Zero;
        private LowLevelKeyboardProc? _proc;   // 必须保持引用，否则会被 GC 回收导致回调崩溃
        private bool _keyDown;

        // 自维护的修饰键状态（钩子里 GetAsyncKeyState 不可靠，见 Callback 注释）
        private bool _ctrl, _alt, _shift, _win;

        /// <summary>要监听的热键（仅键盘组合；鼠标键走 LowLevelMouseHook）。</summary>
        public HotkeySpec? Spec { get; set; }

        /// <summary>热键被按下时触发（在钩子线程里，回调要尽量轻）。</summary>
        public Action? Pressed { get; set; }

        public bool IsHooked => _hook != IntPtr.Zero;

        public bool Install()
        {
            if (_hook != IntPtr.Zero) return true;
            _proc = Callback;
            try
            {
                IntPtr hMod = GetModuleHandle(null);
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, hMod, 0);
                return _hook != IntPtr.Zero;
            }
            catch
            {
                _hook = IntPtr.Zero;
                return false;
            }
        }

        public void Uninstall()
        {
            if (_hook == IntPtr.Zero) return;
            try { UnhookWindowsHookEx(_hook); } catch { }
            _hook = IntPtr.Zero;
        }

        private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);

            try
            {
                int msg = wParam.ToInt32();
                bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                uint vk = info.vkCode;
                bool injected = (info.flags & 0x10) != 0;

                // ★ 自己维护修饰键状态，不要用 GetAsyncKeyState。
                //   原因：钩子回调发生在按键事件**分发过程中**，
                //   GetAsyncKeyState 在这个时刻读修饰键并不可靠，
                //   实测会返回 False，导致热键永远判定"修饰键不匹配"→ 按了没反应。
                //   钩子能看到每一个 KEYDOWN/KEYUP，自己记账才是准的。
                if (!injected)
                {
                    if (vk == 0x11) _ctrl = isDown;          // Ctrl
                    else if (vk == 0x12) _alt = isDown;      // Alt
                    else if (vk == 0x10) _shift = isDown;    // Shift
                    else if (vk == 0x5B || vk == 0x5C) _win = isDown;  // Win
                }

                var spec = Spec;
                if (spec == null || spec.IsMouse || spec.Key == 0)
                    return CallNextHookEx(_hook, nCode, wParam, lParam);

                // 临时诊断：记录所有按键
                if (isDown)
                {
                    SoulLink.Controls.RdpConsole.Debug("KB vk=" + vk
                        + "(" + HotkeySpec.KeyName(vk) + ")"
                        + " 监听=" + spec.Key + "(" + HotkeySpec.KeyName(spec.Key) + ")"
                        + " ctrl=" + _ctrl + " alt=" + _alt
                        + " 注入=" + injected);
                }

                if (vk != spec.Key)
                    return CallNextHookEx(_hook, nCode, wParam, lParam);

                // 修饰键是否匹配（用自维护状态）
                bool modsOk = _ctrl == spec.Ctrl && _alt == spec.Alt
                           && _shift == spec.Shift && _win == spec.Win;

                if (isDown && !_keyDown)
                {
                    _keyDown = true;
                    if (modsOk)
                    {
                        SoulLink.Controls.RdpConsole.Debug("热键命中 vk=" + vk
                            + "(" + HotkeySpec.KeyName(vk) + ") 注入=" + injected);
                        try { Pressed?.Invoke(); } catch { }
                        return (IntPtr)1;   // 吞掉，别再传给 RDP
                    }
                }
                else if (isUp)
                {
                    _keyDown = false;
                    if (modsOk) return (IntPtr)1;
                }
            }
            catch { }

            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose() => Uninstall();
    }
}
