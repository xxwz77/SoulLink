using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace SoulLink.Services
{
    /// <summary>
    /// 系统级热键。支持键盘组合（Ctrl+Alt+Q）和鼠标按键（中键 / 侧键）。
    /// 鼠标键走 WH_MOUSE_LL，因为 RegisterHotKey 不支持鼠标。
    /// </summary>
    public sealed class NativeHotkey : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private static int _nextId = 0xB000;

        private readonly int _id;
        private readonly HwndSource? _source;
        private readonly Action _callback;
        private Window? _owner;
        private LowLevelMouseHook? _mouseHook;
        private LowLevelKeyboardHook? _keyHook;
        private bool _disposed;

        public string Registered { get; private set; } = "";

        private NativeHotkey(HwndSource? source, int id, Action callback)
        {
            _source = source;
            _id = id;
            _callback = callback;
        }

        public static NativeHotkey? Register(Action onPressed)
            => RegisterFromString("Ctrl+Alt+Q", onPressed);

        public static NativeHotkey? RegisterFromString(string? spec, Action onPressed)
        {
            var parsed = HotkeySpec.Parse(spec ?? "Ctrl+Alt+Q") ?? HotkeySpec.Parse("Ctrl+Alt+Q")!;
            return Register(parsed, onPressed);
        }

        public static NativeHotkey? Register(HotkeySpec spec, Action onPressed)
        {
            if (spec.IsMouse) return RegisterMouse(spec, onPressed);
            // ★ 直接把 spec 传进去，不要只传 label 再在内部 Parse 一次。
            //   二次解析是多余的往返，任何格式偏差都会让"注册的键"和"钩子监听的键"对不上。
            return RegisterKeyboard(spec, onPressed);
        }

        private static NativeHotkey? RegisterMouse(HotkeySpec spec, Action onPressed)
        {
            try
            {
                var self = new NativeHotkey(null, 0, onPressed) { Registered = spec.ToString() };
                var hook = new LowLevelMouseHook();
                hook.Handler = ev =>
                {
                    if (!spec.MatchesMouse(ev)) return false;
                    if (!spec.ModifiersMatch()) return false;
                    try { onPressed(); } catch { }
                    return true;
                };
                if (!hook.Install()) { hook.Dispose(); return null; }
                self._mouseHook = hook;
                return self;
            }
            catch { return null; }
        }

        private static NativeHotkey? RegisterKeyboard(HotkeySpec spec, Action onPressed)
        {
            uint mods = spec.ToModifiers();
            uint vk = spec.Key;
            string label = spec.ToString();
            Window? helper = null;
            HwndSource? src = null;
            bool hotkeyOk = false;
            try
            {
                helper = new Window
                {
                    Width = 0, Height = 0,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    AllowsTransparency = true,
                    Opacity = 0,
                    ShowActivated = false,
                    Top = -32000, Left = -32000
                };
                helper.Show();
                src = PresentationSource.FromVisual(helper) as HwndSource;
                if (src == null) { helper.Close(); return null; }
                int id = ++_nextId;
                hotkeyOk = RegisterHotKey(src.Handle, id, mods | MOD_NOREPEAT, vk);

                var hk = new NativeHotkey(hotkeyOk ? src : null, id, onPressed) { _owner = helper, Registered = label };
                src.AddHook(hk.WndProc);

                // ★ 不要 Hide()！
                //   RegisterHotKey 的 WM_HOTKEY 是投递到该窗口的消息队列的，
                //   窗口一旦被 Hide（SW_HIDE），部分系统版本就不再分发这条消息，
                //   表现为"注册成功但按了没反应"（实测：WndProc 完全收不到 WM_HOTKEY）。
                helper.Show();
                helper.Width = 1;
                helper.Height = 1;
                helper.Left = -32000;
                helper.Top = -32000;
                helper.Opacity = 0;

                // ★ 双保险：再装一个全局低级键盘钩子。
                //   RDP 控件的 KeyboardHookMode=1 会在输入链路里抢走按键，
                //   那时 RegisterHotKey 的 WM_HOTKEY 根本不会来（用户实测：
                //   "快捷键好像直接作用在 RDP，前台收不到"）。
                //   低级钩子比窗口/热键处理都靠前，能把它拦下来。
                if (spec.Key != 0)
                {
                    hk._keyHook = new LowLevelKeyboardHook { Spec = spec };
                    hk._keyHook.Pressed = () =>
                    {
                        // 钩子线程 → UI 线程
                        try { hk._owner?.Dispatcher.BeginInvoke(new Action(() => { try { onPressed(); } catch { } })); }
                        catch { }
                    };
                    if (!hk._keyHook.Install()) { hk._keyHook.Dispose(); hk._keyHook = null; }
                }

                // 两条通道都没建立才算失败
                if (!hotkeyOk && hk._keyHook == null)
                {
                    try { helper.Close(); } catch { }
                    return null;
                }
                return hk;
            }
            catch { try { helper?.Close(); } catch { } return null; }
        }

        public static bool TryParse(string spec, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            var p = HotkeySpec.Parse(spec);
            if (p == null || p.IsMouse) return false;
            mods = p.ToModifiers(); vk = p.Key;
            return mods != 0 && vk != 0;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
            {
                handled = true;
                try { _callback(); } catch { }
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { if (_source != null) UnregisterHotKey(_source.Handle, _id); } catch { }
            try { _source?.RemoveHook(WndProc); } catch { }
            var mh = _mouseHook; _mouseHook = null;
            try { mh?.Dispose(); } catch { }
            var kh = _keyHook; _keyHook = null;
            try { kh?.Dispose(); } catch { }
            try { _owner?.Close(); } catch { }
            _owner = null;
        }
    }
}
