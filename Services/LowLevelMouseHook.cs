using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace SoulLink.Services
{
    /// <summary>
    /// 全局低级鼠标钩子（WH_MOUSE_LL）。
    ///
    /// 为什么必须用它（实测结论）：
    ///   RDP 的 ActiveX 控件把鼠标输入**全部吞掉**了——
    ///     · WinForms 的 MouseDown → 抛 NotSupportedException「此 ActiveX 控件上无效」
    ///     · WPF 的 WindowsFormsHost.PreviewMouseXxx → 根本不触发
    ///   所以想在画面区域拦截鼠标，只能挂系统级钩子。
    ///
    /// 钩子里能拿到**屏幕坐标**，配合 RDP 控件的屏幕矩形就能判断是否落在画面内。
    /// </summary>
    public sealed class LowLevelMouseHook : IDisposable
    {
        private const int WH_MOUSE_LL = 14;

        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_MBUTTONUP = 0x0208;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_XBUTTONDOWN = 0x020B;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private IntPtr _hook = IntPtr.Zero;
        private LowLevelMouseProc? _proc;      // 必须保持引用，否则会被 GC 回收导致回调崩溃
        private int _lastX = int.MinValue, _lastY = int.MinValue;

        /// <summary>鼠标事件回调。返回 true 表示"吞掉"这个事件（不再传给系统）。</summary>
        public Func<MouseHookEvent, bool>? Handler { get; set; }

        /// <summary>是否已挂上</summary>
        public bool IsHooked => _hook != IntPtr.Zero;

        public bool Install()
        {
            if (_hook != IntPtr.Zero) return true;
            _proc = Callback;
            try
            {
                IntPtr hMod = GetModuleHandle(null);
                _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, hMod, 0);
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
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

                var ev = new MouseHookEvent
                {
                    Message = msg,
                    X = info.pt.X,
                    Y = info.pt.Y,
                    WheelDelta = msg == WM_MOUSEWHEEL ? (int)(short)((info.mouseData >> 16) & 0xFFFF) : 0,
                    IsXButton1 = ((info.mouseData >> 16) & 0xFFFF) == 1
                };

                // 逐点差分：两个相邻鼠标事件之间的位移。
                // 注意 LL 钩子拿到的坐标本身**已受指针加速影响**，
                // 所以上层还会按需要再做灵敏度补偿（见 SessionInput.Sensitivity）。
                if (msg == WM_MOUSEMOVE && _lastX != int.MinValue)
                {
                    ev.HasRawDelta = true;
                    ev.RawDeltaX = info.pt.X - _lastX;
                    ev.RawDeltaY = info.pt.Y - _lastY;
                }
                _lastX = info.pt.X;
                _lastY = info.pt.Y;

                var handler = Handler;
                if (handler != null && handler(ev))
                    return (IntPtr)1;      // 吞掉，画面不会动、也不会传到宿主桌面
            }
            catch { }

            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose() => Uninstall();
    }

    /// <summary>一次鼠标事件的坐标与类型（屏幕坐标）。</summary>
    public sealed class MouseHookEvent
    {
        public int Message { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int WheelDelta { get; set; }

        /// <summary>
        /// 原始物理位移（未经指针加速）。WH_MOUSE_LL 的 MSLLHOOKSTRUCT 里没有这个字段，
        /// 所以由我们自己用「上一次的坐标」算——但要注意我们做的是**未加速**的差分。
        /// </summary>
        public bool HasRawDelta { get; set; }
        public int RawDeltaX { get; set; }
        public int RawDeltaY { get; set; }

        public bool IsMove => Message == 0x0200;
        public bool IsLeftDown => Message == 0x0201;
        public bool IsLeftUp => Message == 0x0202;
        public bool IsRightDown => Message == 0x0204;
        public bool IsRightUp => Message == 0x0205;
        public bool IsMiddleDown => Message == 0x0207;
        public bool IsMiddleUp => Message == 0x0208;
        public bool IsWheel => Message == 0x020A;

        /// <summary>侧键编号：true = X1（后退），false = X2（前进）</summary>
        public bool IsXButton1 { get; set; }

        public bool IsXButton1Down => Message == 0x020B && IsXButton1;
        public bool IsXButton2Down => Message == 0x020B && !IsXButton1;

        /// <summary>侧键抬起（WM_XBUTTONUP = 0x020C）。</summary>
        public bool IsXButtonUp => Message == 0x020C;
    }
}
