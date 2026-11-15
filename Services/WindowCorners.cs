using System;
using System.Runtime.InteropServices;

namespace SoulLink.Services
{
    /// <summary>
    /// 窗口圆角 / 阴影控制。
    ///
    /// 为什么需要：SoulLink 主窗口必须是非分层窗口（RDP ActiveX 限制，见 HANDOFF 10.4），
    /// 而非分层窗口的 <c>Border.CornerRadius</c> **不会真的裁掉窗口四角**，
    /// 结果就是圆角外还露出直角和阴影尖角。
    ///
    /// 正确做法是让 DWM 自己做圆角（Windows 11 原生支持）：
    ///   DWMWA_WINDOW_CORNER_PREFERENCE = 33
    /// </summary>
    public static class WindowCorners
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Windows 11 圆角偏好
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_DEFAULT = 0;
        private const int DWMWCP_DONOTROUND = 1;
        private const int DWMWCP_ROUND = 2;
        private const int DWMWCP_ROUNDSMALL = 3;

        // 窗口边框颜色
        private const int DWMWA_BORDER_COLOR = 34;

        /// <summary>给窗口加系统级圆角。返回是否成功（Win10 及更早会失败，属正常）。</summary>
        public static bool ApplyRounded(IntPtr hwnd, bool small = false)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                int pref = small ? DWMWCP_ROUNDSMALL : DWMWCP_ROUND;
                int hr = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
                return hr == 0;
            }
            catch { return false; }
        }

        /// <summary>去掉系统圆角（需要直角时）。</summary>
        public static bool RemoveRounded(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                int pref = DWMWCP_DONOTROUND;
                return DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)) == 0;
            }
            catch { return false; }
        }

        /// <summary>设置窗口边框颜色（含标题栏和四周细边）。传 0xFFFFFFFF 表示用系统默认。</summary>
        public static bool SetBorderColor(IntPtr hwnd, uint bgr)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                int color = unchecked((int)bgr);
                return DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref color, sizeof(int)) == 0;
            }
            catch { return false; }
        }
    }
}
