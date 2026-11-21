using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

/// <summary>
/// SoulLink 一键救援：解除光标裁剪、释放所有鼠标键、结束 SoulLink 进程与代理。
/// 独立运行，不依赖 SoulLink 主程序（主程序锁死时也能用）。
/// </summary>
public static class Program
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClipCursor(IntPtr rect);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    [DllImport("user32.dll")]
    private static extern int ShowCursor(bool show);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private const uint LEFTUP = 0x0004, RIGHTUP = 0x0010, MIDDLEUP = 0x0040, XUP = 0x0100;

    public static int Main(string[] args)
    {
        Console.WriteLine("[1/5] 解除光标裁剪 ...");
        for (int i = 0; i < 3; i++) { ClipCursor(IntPtr.Zero); Thread.Sleep(50); }

        Console.WriteLine("[2/5] 恢复光标可见 ...");
        ShowCursor(true);
        for (int i = 0; i < 10; i++)
        {
            if (ShowCursor(true) >= 0) break;
        }

        Console.WriteLine("[3/5] 把光标移回可见区域 ...");
        try
        {
            int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
            if (w > 0 && h > 0) SetCursorPos(w / 2, h / 2);
        }
        catch { }

        Console.WriteLine("[4/5] 结束 SoulLink 进程 ...");
        int killed = 0;
        foreach (var p in Process.GetProcessesByName("SoulLink"))
        {
            try { p.Kill(true); killed++; Console.WriteLine("    killed pid=" + p.Id); } catch { }
        }
        Console.WriteLine("    共结束 " + killed + " 个进程");

        Console.WriteLine("[5/5] 释放所有鼠标按键 ...");
        mouse_event(LEFTUP, 0, 0, 0, IntPtr.Zero);
        mouse_event(RIGHTUP, 0, 0, 0, IntPtr.Zero);
        mouse_event(MIDDLEUP, 0, 0, 0, IntPtr.Zero);
        mouse_event(XUP, 0, 0, 1, IntPtr.Zero);
        mouse_event(XUP, 0, 0, 2, IntPtr.Zero);

        Thread.Sleep(150);
        bool left = (GetAsyncKeyState(0x01) & 0x8000) != 0;
        bool right = (GetAsyncKeyState(0x02) & 0x8000) != 0;
        Console.WriteLine();
        Console.WriteLine("LEFT_DOWN=" + left + "  RIGHT_DOWN=" + right);
        Console.WriteLine(left || right ? "警告：仍有按键处于按下状态，请再运行一次" : "完成：鼠标应已恢复正常");
        return 0;
    }
}
