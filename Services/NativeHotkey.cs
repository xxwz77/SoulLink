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
            return RegisterKeyboard(spec.ToModifiers(), spec.Key, spec.ToString(), onPressed);
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

        private static NativeHotkey? RegisterKeyboard(uint mods, uint vk, string label, Action onPressed)
        {
            Window? helper = null;
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
                var src = PresentationSource.FromVisual(helper) as HwndSource;
                if (src == null) { helper.Close(); return null; }
                int id = ++_nextId;
                if (!RegisterHotKey(src.Handle, id, mods | MOD_NOREPEAT, vk)) { helper.Close(); return null; }
                var hk = new NativeHotkey(src, id, onPressed) { _owner = helper, Registered = label };
                src.AddHook(hk.WndProc);
                helper.Hide();
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
            try { _owner?.Close(); } catch { }
            _owner = null;
        }
    }
}
