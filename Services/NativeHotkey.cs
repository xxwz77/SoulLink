using System;
using System.Windows;
using SoulLink.Controls;

namespace SoulLink.Services
{
    /// <summary>当前 RDP 控制台的热键：键盘走本机 Raw Input，鼠标走 WH_MOUSE_LL。</summary>
    public sealed class NativeHotkey : IDisposable
    {
        private LowLevelMouseHook? _mouseHook;
        private LowLevelKeyboardHook? _keyboard;
        private bool _disposed;

        public string Registered { get; private set; } = "";
        private NativeHotkey(string label) => Registered = label;

        public static bool HotkeysSuppressed { get; private set; }
        private static int _recordingCount;

        public static int BeginRecording()
        {
            HotkeysSuppressed = true;
            return System.Threading.Interlocked.Increment(ref _recordingCount);
        }

        public static void EndRecording()
        {
            if (System.Threading.Interlocked.Decrement(ref _recordingCount) <= 0)
            {
                System.Threading.Interlocked.Exchange(ref _recordingCount, 0);
                HotkeysSuppressed = false;
            }
        }

        public static NativeHotkey? RegisterFromString(string? label, Action pressed)
        {
            var spec = HotkeySpec.Parse(label ?? "Ctrl+Alt+Q") ?? HotkeySpec.Parse("Ctrl+Alt+Q")!;
            return Register(spec, pressed);
        }

        public static NativeHotkey? Register(Action pressed) => RegisterFromString("Ctrl+Alt+Q", pressed);

        public static NativeHotkey? Register(HotkeySpec spec, Action pressed)
        {
            // 通用调用保留低级键盘钩子；RDP 控制台必须走 RegisterForConsole 的 Raw Input。
            var self = new NativeHotkey(spec.ToString());
            var hook = new LowLevelKeyboardHook
            {
                Spec = spec.Clone(),
                CanTrigger = () => !HotkeysSuppressed,
                Pressed = () =>
                {
                    try { Application.Current?.Dispatcher.BeginInvoke(pressed); } catch { }
                }
            };
            if (!hook.Install()) { hook.Dispose(); return null; }
            self._legacyKeyboard = hook;
            return self;
        }

        private LowLevelKeyboardHook? _legacyKeyboard;

        public static NativeHotkey? RegisterForConsole(HotkeySpec spec, Action pressed, RdpConsole console)
        {
            var self = new NativeHotkey(spec.ToString());
            try
            {
                // 键盘和鼠标热键均由目标会话接收器监听；本机不吞热键。

                // 键盘由目标会话 RelayHotkeyReceiver 接收；本机不安装键盘钩子。
                // 这里保留一个已注册标记，使生命周期和设置热更新保持统一。
                return self;
            }
            catch (Exception ex)
            {
                RdpConsole.Debug("控制台热键安装异常：" + ex.Message);
                self.Dispose();
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _mouseHook?.Dispose(); } catch { }
            try { _keyboard?.Dispose(); } catch { }
            try { _legacyKeyboard?.Dispose(); } catch { }
            _mouseHook = null;
            _keyboard = null;
            _legacyKeyboard = null;
        }
    }
}
