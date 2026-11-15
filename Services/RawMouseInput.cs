using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SoulLink.Services
{
    /// <summary>
    /// 自由视角用的 Raw Input 相对位移采集器（无驱动）。
    ///
    /// ★ 与上一版事故版本的关键区别（务必保留）：
    ///   1. **只注册，不做任何拦截**：不设置 RIDEV_NOLEGACY，不吞消息，
    ///      因此不会切断旧式鼠标消息，退出链路永远可用。
    ///   2. **不移动/不隐藏/不裁剪光标**：只读取 lLastX/lLastY。
    ///   3. 位移由系统累积，不再依赖光标在屏幕上的绝对坐标，
    ///      所以不会因为光标顶到屏幕边缘而失去位移。
    ///
    /// 注册标志只用 RIDEV_INPUTSINK（窗口不在前台也能收到），
    /// 不碰 NOLEGACY / CAPTUREMOUSE —— 这两个正是上次锁死的根因。
    /// </summary>
    public sealed class RawMouseInput : IDisposable
    {
        private const int WM_INPUT = 0x00FF;
        private const uint RIDEV_REMOVE = 0x00000001;
        private const uint RIDEV_INPUTSINK = 0x00000100;
        private const uint RID_INPUT = 0x10000003;
        private const int RIM_TYPEMOUSE = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTHEADER
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWMOUSE
        {
            public ushort usFlags;
            public ushort usButtonFlags;
            public short usButtonData;
            public uint ulRawButtons;
            public int lLastX;
            public int lLastY;
            public uint ulExtraInformation;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUT
        {
            public RAWINPUTHEADER header;
            public RAWMOUSE mouse;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data,
            ref uint size, uint headerSize);

        private readonly HwndSource _source;
        private bool _registered;

        /// <summary>相对位移回调。</summary>
        public Action<int, int>? Moved { get; set; }
        public Action<string>? Diagnostic { get; set; }
        public bool IsActive => _registered;

        private RawMouseInput(HwndSource source) => _source = source;

        public static RawMouseInput? TryCreate(Window window)
        {
            if (PresentationSource.FromVisual(window) is not HwndSource source) return null;
            return new RawMouseInput(source);
        }

        /// <summary>开始接收 Raw Input。失败返回 false。</summary>
        public bool Start()
        {
            if (_registered) return true;
            var devices = new[]
            {
                new RAWINPUTDEVICE
                {
                    usUsagePage = 0x01,
                    usUsage = 0x02,
                    // 只加 INPUTSINK：窗口失焦也能收到，且**不影响**旧式消息。
                    dwFlags = RIDEV_INPUTSINK,
                    hwndTarget = _source.Handle
                }
            };
            if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            {
                int err = Marshal.GetLastWin32Error();
                Diagnostic?.Invoke("Raw Input 注册失败 error=" + err);
                return false;
            }
            _source.AddHook(WndProc);
            _registered = true;
            Diagnostic?.Invoke("Raw Input 相对位移采集已开启（仅 INPUTSINK，不拦截）");
            return true;
        }

        public void Stop()
        {
            if (!_registered) return;
            _registered = false;
            try { _source.RemoveHook(WndProc); } catch { }
            try
            {
                var remove = new[]
                {
                    new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero }
                };
                RegisterRawInputDevices(remove, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            }
            catch { }
            Diagnostic?.Invoke("Raw Input 采集已关闭");
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_INPUT || !_registered) return IntPtr.Zero;
            try
            {
                uint size = (uint)Marshal.SizeOf<RAWINPUT>();
                IntPtr buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    uint got = GetRawInputData(lParam, RID_INPUT, buffer, ref size,
                        (uint)Marshal.SizeOf<RAWINPUTHEADER>());
                    if (got == uint.MaxValue) return IntPtr.Zero;
                    var input = Marshal.PtrToStructure<RAWINPUT>(buffer);
                    if (input.header.dwType != RIM_TYPEMOUSE) return IntPtr.Zero;
                    if ((input.mouse.usFlags & 0x01) != 0) return IntPtr.Zero; // 绝对模式跳过
                    int dx = input.mouse.lLastX, dy = input.mouse.lLastY;
                    if (dx == 0 && dy == 0) return IntPtr.Zero;
                    Moved?.Invoke(dx, dy);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            catch (Exception ex) { Diagnostic?.Invoke("Raw Input 处理异常：" + ex.Message); }
            // 不设置 handled：消息继续按原样流转，绝不干扰系统鼠标。
            return IntPtr.Zero;
        }

        public void Dispose() => Stop();
    }
}
