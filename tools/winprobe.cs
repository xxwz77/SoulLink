
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public class WinProbe
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int sz);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] public static extern int DwmGetWindowAttributeInt(IntPtr h, int a, out int v, int sz);

    public struct RECT { public int L, T, R, B; }
    public struct POINT { public int X, Y; }

    public static string Run(int targetPid)
    {
        SetProcessDPIAware();
        var found = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if ((int)pid == targetPid && IsWindowVisible(h))
            {
                RECT r;
                GetWindowRect(h, out r);
                if (r.R - r.L > 400 && r.B - r.T > 300)
                {
                    var sb = new StringBuilder(256);
                    GetClassName(h, sb, 256);
                    if (sb.ToString().StartsWith("HwndWrapper"))
                        found.Add(h);
                }
            }
            return true;
        }, IntPtr.Zero);

        if (found.Count == 0) return "NO_WINDOW pid=" + targetPid;

        var sb2 = new StringBuilder();
        foreach (var h in found)
        {
            RECT wr, cr;
            GetWindowRect(h, out wr);
            GetClientRect(h, out cr);
            var pt = new POINT();
            ClientToScreen(h, ref pt);

            int style = GetWindowLong(h, -16);
            int exstyle = GetWindowLong(h, -20);

            sb2.AppendLine("hwnd = 0x" + h.ToInt64().ToString("X"));
            sb2.AppendLine(string.Format("WindowRect = {0},{1} .. {2},{3}   size {4} x {5}",
                wr.L, wr.T, wr.R, wr.B, wr.R - wr.L, wr.B - wr.T));
            sb2.AppendLine(string.Format("ClientRect = {0},{1} .. {2},{3}   size {4} x {5}",
                cr.L, cr.T, cr.R, cr.B, cr.R - cr.L, cr.B - cr.T));
            sb2.AppendLine(string.Format("ClientOrigin(screen) = {0},{1}", pt.X, pt.Y));
            sb2.AppendLine("--- 非客户区厚度（系统口径）---");
            sb2.AppendLine("  left   = " + (pt.X - wr.L));
            sb2.AppendLine("  top    = " + (pt.Y - wr.T));
            sb2.AppendLine("  right  = " + (wr.R - (pt.X + cr.R)));
            sb2.AppendLine("  bottom = " + (wr.B - (pt.Y + cr.B)));
            sb2.AppendLine("--- style ---");
            sb2.AppendLine("  WS_POPUP      = " + ((style & 0x80000000L) != 0));
            sb2.AppendLine("  WS_THICKFRAME = " + ((style & 0x00040000) != 0));
            sb2.AppendLine("  WS_CAPTION    = " + ((style & 0x00C00000) == 0x00C00000));
            sb2.AppendLine("  WS_BORDER     = " + ((style & 0x00800000) != 0));
            sb2.AppendLine("  style=0x" + style.ToString("X8") + " exstyle=0x" + exstyle.ToString("X8"));
            sb2.AppendLine("--- 系统度量 ---");
            sb2.AppendLine("  SM_CXSIZEFRAME(32)=" + GetSystemMetrics(32) + " SM_CYSIZEFRAME(33)=" + GetSystemMetrics(33)
                + " SM_CXPADDEDBORDER(92)=" + GetSystemMetrics(92) + " SM_CXBORDER(5)=" + GetSystemMetrics(5));
            sb2.AppendLine("  SM_CXFRAME(32alt)=" + GetSystemMetrics(32) + " SM_CYFRAME(33)=" + GetSystemMetrics(33));

            RECT ext;
            int hr = DwmGetWindowAttribute(h, 9, out ext, 16);
            sb2.AppendLine("--- DWM ---");
            sb2.AppendLine(string.Format("  EXTENDED_FRAME_BOUNDS hr={0}  {1},{2}..{3},{4}", hr, ext.L, ext.T, ext.R, ext.B));
            int v;
            DwmGetWindowAttributeInt(h, 33, out v, 4);
            sb2.AppendLine("  CORNER_PREFERENCE = " + v);
            DwmGetWindowAttributeInt(h, 34, out v, 4);
            sb2.AppendLine("  BORDER_COLOR = 0x" + v.ToString("X8"));
            DwmGetWindowAttributeInt(h, 2, out v, 4);
            sb2.AppendLine("  NCRENDERING_POLICY = " + v);
            sb2.AppendLine();
        }
        return sb2.ToString();
    }
}
