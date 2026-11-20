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
        // ★ DWMWA_BORDER_COLOR 的取值是 **COLORREF**（0x00BBGGRR）：
        //   低 24 位按 B、G、R 排列，最高字节是 alpha 且**必须为 0x00**。
        //   传 0xFFFFFFFF / 0xFFRRGGBB 都不行：前者是 DWM 约定的"默认/无边框"标记，
        //   后者会被 DWM 判为非法参数并返回 E_INVALIDARG(0x80070057)，边框保持原样。
        //   —— 这正是"应用边框没有正确使用系统强调色"的根因（旧代码一直传 ARGB）。
        private const int DWMWA_BORDER_COLOR = 34;

        /// <summary>DWMWA_COLOR_DEFAULT：恢复系统原生边框。</summary>
        public const uint ColorDefault = 0xFFFFFFFFu;

        /// <summary>DWMWA_COLOR_NONE：不画边框。</summary>
        public const uint ColorNone = 0xFFFFFFFEu;

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

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetColorizationColor(out uint colorization, out bool opaqueBlend);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int GetCurrentThemeName(System.Text.StringBuilder theme, int length, System.Text.StringBuilder color, int colorLength, System.Text.StringBuilder size, int sizeLength);

        /// <summary>
        /// 读取系统强调色（用于激活窗口的边框），返回 **ARGB**（0xAARRGGBB）。
        ///
        /// ★ 取值顺序很关键（已实测）：
        ///   1. 注册表 DWM\ColorizationColor —— 这才是原生窗口边框实际用的颜色。
        ///      本机实测 #F091C1，与资源管理器激活态边框**逐像素一致**。
        ///   2. DwmGetColorizationColor —— **不能**用作首选！
        ///      开启透明效果时它返回的是混合后的着色（本机 #E48AB7，blend=true），
        ///      比真正的强调色偏暗偏灰，直接拿来画边框就是"颜色不对"。
        ///   3. DWM\AccentColor —— 用户自定义强调色（COLORREF 0xAABBGGRR）。
        /// </summary>
        public static uint GetSystemAccentColor()
        {
            // 1. 原生边框真正使用的颜色
            try
            {
                object? v = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "ColorizationColor", null);
                if (v is int i && (i & 0xFFFFFF) != 0) return 0xFF000000u | ((uint)i & 0xFFFFFFu);
            }
            catch { }

            // 2. DWM 当前着色（可能是混合后的，仅作兜底）
            try
            {
                if (DwmGetColorizationColor(out uint c, out _) == 0 && (c & 0xFFFFFFu) != 0)
                    return 0xFF000000u | (c & 0xFFFFFFu);
            }
            catch { }

            // 3. 用户强调色
            try
            {
                object? v = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null);
                if (v is int i && (i & 0xFFFFFF) != 0) return ColorRefToArgb((uint)i);
            }
            catch { }

            return 0xFF4F7CD8; // 系统取不到时使用接近 Windows 默认的蓝色
        }

        /// <summary>COLORREF（0x00BBGGRR）→ ARGB（0xFFRRGGBB）。</summary>
        private static uint ColorRefToArgb(uint colorRef)
        {
            uint r = colorRef & 0xFFu;
            uint g = (colorRef >> 8) & 0xFFu;
            uint b = (colorRef >> 16) & 0xFFu;
            return 0xFF000000u | (r << 16) | (g << 8) | b;
        }

        /// <summary>ARGB（0xFFRRGGBB）→ COLORREF（0x00BBGGRR）。alpha 字节必须是 0。</summary>
        private static int ArgbToColorRef(uint argb)
        {
            uint r = (argb >> 16) & 0xFFu;
            uint g = (argb >> 8) & 0xFFu;
            uint b = argb & 0xFFu;
            return (int)(r | (g << 8) | (b << 16));
        }

        /// <summary>
        /// 设置窗口边框颜色（含标题栏和四周细边）。参数是 ARGB（0xFFRRGGBB）；
        /// 也接受 <see cref="ColorDefault"/> / <see cref="ColorNone"/> 两个特殊标记。
        /// </summary>
        public static bool SetBorderColor(IntPtr hwnd, uint color)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                int value = (color == ColorDefault || color == ColorNone)
                    ? unchecked((int)color)
                    : ArgbToColorRef(color);
                return DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref value, sizeof(int)) == 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// 未激活时**完全不画** DWM 边框。
        ///
        /// ★ 原来用的是 ColorDefault（恢复系统原生边框），
        ///   而系统原生未激活边框是白色的 —— 在深色主题下，
        ///   窗口内侧会露出一条明显的白边（实测 #f4f7fc，厚 8px，
        ///   且因为 DWM 只画在有内容的地方，表现为"上左完整、下右残缺"）。
        ///   改画 ColorNone 后该边完全消失，与原生深色应用表现一致。
        /// </summary>
        public static bool ResetBorderColor(IntPtr hwnd) => SetBorderColor(hwnd, ColorNone);
    }
}
