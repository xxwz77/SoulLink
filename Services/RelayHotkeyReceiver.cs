using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using SoulLink.Services;

namespace SoulLink.Relay
{
    /// <summary>
    /// 目标会话的只读热键接收器：安装和回调在同一线程消息循环。
    /// 不吞键、不暂存、不补发、不注入键盘；游戏快捷键冲突由用户自行避开。
    /// </summary>
    internal sealed class RelayHotkeyReceiver : IDisposable
    {
        private readonly BlockingCollection<Action> _work = new();
        private readonly Thread _thread;
        private readonly Action<string> _notify;
        private readonly Action<string> _log;
        private HotkeySpec? _spec;
        private IntPtr _hook;
        private IntPtr _modifierHook;
        private HookProc? _modifierProc;
        private IntPtr _mouseHook;
        private HookProc? _proc;
        private HookProc? _mouseProc;
        private uint _threadId;
        private bool _disposed;
        private bool _trace;
        private bool _keyDown;
        private bool _ctrl, _alt, _shift, _win;

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode, scanCode, flags, time;
            public IntPtr dwExtraInfo;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData, flags, time;
            public IntPtr dwExtraInfo;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam, lParam;
            public uint time;
            public POINT pt;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint thread);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? name);
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostThreadMessage(uint thread, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private const int WH_KEYBOARD_LL = 13;
        private const int WH_MOUSE_LL = 14;
        private const uint WM_APP_WORK = 0x8001;
        private const uint WM_QUIT = 0x0012;

        public RelayHotkeyReceiver(Action<string> notify, Action<string> log)
        {
            _notify = notify;
            _log = log;
            _thread = new Thread(Pump) { IsBackground = true, Name = "SoulLink session hotkey receiver" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public string Configure(string label, bool trace = false)
        {
            Post(() =>
            {
                _trace = trace;
                Uninstall();
                _spec = HotkeySpec.Parse(label);
                if (_spec == null)
                {
                    return;
                }
                if (_spec.IsMouse)
                {
                    if (_spec.ToModifiers() != 0)
                    {
                        _modifierProc = Callback;
                        _modifierHook = SetWindowsHookEx(WH_KEYBOARD_LL, _modifierProc, GetModuleHandle(null), 0);
                    }
                    _mouseProc = MouseCallback;
                    _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0);
                    int mouseError = _mouseHook == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
                    _log("目标会话鼠标热键安装=" + (_mouseHook != IntPtr.Zero) + " thread=" + _threadId
                        + " error=" + mouseError + " spec=" + label);
                    return;
                }
                _proc = Callback;
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
                int error = _hook == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
                _log("目标会话热键安装=" + (_hook != IntPtr.Zero) + " thread=" + _threadId
                    + " error=" + error + " spec=" + label);
            });
            return "配置已排队";
        }

        private void Post(Action action)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RelayHotkeyReceiver));
            _work.Add(action);
            if (_threadId != 0) PostThreadMessage(_threadId, WM_APP_WORK, IntPtr.Zero, IntPtr.Zero);
        }

        private void Pump()
        {
            _threadId = GetCurrentThreadId();
            _log("热键接收线程启动 thread=" + _threadId + " session=" + Process.GetCurrentProcess().SessionId);
            PostThreadMessage(_threadId, WM_APP_WORK, IntPtr.Zero, IntPtr.Zero);
            try
            {
                while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.message == WM_QUIT) break;
                    if (msg.message == WM_APP_WORK)
                    {
                        while (_work.TryTake(out var action))
                        {
                            try { action(); }
                            catch (Exception ex) { _log("热键接收线程任务异常=" + ex); }
                        }
                        continue;
                    }
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            finally { Uninstall(); _work.Dispose(); }
        }

        private static bool Down(uint vk) => (GetAsyncKeyState((int)vk) & 0x8000) != 0;

        private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0 || _spec == null)
                return CallNextHookEx(_hook, nCode, wParam, lParam);
            var spec = _spec;
            try
            {
                int msg = wParam.ToInt32();
                bool down = msg == 0x0100 || msg == 0x0104;
                bool up = msg == 0x0101 || msg == 0x0105;
                if (!down && !up) return CallNextHookEx(_hook, nCode, wParam, lParam);
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                uint vk = info.vkCode;

                // 目标会话收到的事件可能带 injected 标记，不能按本机脚本过滤。
                // 记录目标会话自身的修饰键按下/抬起，不假定事件到达顺序。
                switch (vk)
                {
                    case 0x11: case 0xA2: case 0xA3: _ctrl = down; break;
                    case 0x12: case 0xA4: case 0xA5: _alt = down; break;
                    case 0x10: case 0xA0: case 0xA1: _shift = down; break;
                    case 0x5B: case 0x5C: _win = down; break;
                }

                if (up && vk == spec.Key) _keyDown = false;
                if (down && !spec.IsMouse && vk == spec.Key && !_keyDown)
                {
                    _keyDown = true;
                    bool modifiers = _ctrl == spec.Ctrl && _alt == spec.Alt
                        && _shift == spec.Shift && _win == spec.Win;
                    if (_trace)
                        _log("目标热键候选 vk=" + vk + " ctrl=" + _ctrl + " alt=" + _alt
                            + " shift=" + _shift + " win=" + _win + " flags=0x" + info.flags.ToString("X"));
                    if (modifiers)
                    {
                        _log("目标会话热键命中=" + spec + " flags=0x" + info.flags.ToString("X"));
                        try { _notify("toggle"); }
                        catch (Exception ex) { _log("热键通知前台失败=" + ex.Message); }
                    }
                }
            }
            catch (Exception ex) { _log("热键监听异常，原样放行=" + ex.Message); }

            // ★ 不吞掉任何键，无论是否命中热键。游戏原始输入流不被改变。
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _spec?.IsMouse == true)
            {
                try
                {
                    int msg = wParam.ToInt32();
                    var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    bool matched = _spec.MouseButton switch
                    {
                        "Left" => msg == 0x0201,
                        "Right" => msg == 0x0204,
                        "Middle" => msg == 0x0207,
                        "X1" => msg == 0x020B && ((info.mouseData >> 16) & 0xFFFF) == 1,
                        "X2" => msg == 0x020B && ((info.mouseData >> 16) & 0xFFFF) == 2,
                        _ => false
                    };
                    if (matched)
                    {
                        bool modifiers = _ctrl == _spec.Ctrl && _alt == _spec.Alt
                            && _shift == _spec.Shift && _win == _spec.Win;
                        if (_trace) _log("目标鼠标热键候选=" + _spec + " modifiers=" + modifiers);
                        if (modifiers)
                        {
                            _log("目标会话鼠标热键命中=" + _spec);
                            try { _notify("toggle"); }
                            catch (Exception ex) { _log("鼠标热键通知前台失败=" + ex.Message); }
                        }
                    }
                }
                catch (Exception ex) { _log("鼠标热键监听异常=" + ex.Message); }
            }
            // 配置的热键只用于通知，不重复注入为游戏鼠标按键。
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private void Uninstall()
        {
            if (_modifierHook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_modifierHook); } catch { }
                _modifierHook = IntPtr.Zero;
                _modifierProc = null;
            }
            if (_mouseHook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_mouseHook); } catch { }
                _mouseHook = IntPtr.Zero;
                _mouseProc = null;
            }
            if (_hook == IntPtr.Zero) return;
            try { UnhookWindowsHookEx(_hook); } catch { }
            _hook = IntPtr.Zero;
            _proc = null;
            _keyDown = false;
            _ctrl = _alt = _shift = _win = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero); } catch { }
            // 销毁由代理进程退出兜底；不在宿主管道线程中等待消息循环。
            if (!_thread.Join(100)) _log("热键接收线程正在退出");
        }
    }
}
