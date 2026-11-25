using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

/// <summary>
/// UI 卡顿监视器：持续探测目标进程的主窗口是否响应，
/// 一旦超过阈值就记录时间点 + 当时的进程线程状态。
/// 用来定位"窗口能显示但点击无反应"发生在哪一刻。
/// </summary>
public static class Program
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr h);

    public static int Main(string[] args)
    {
        string procName = args.Length > 0 ? args[0] : "SoulLink-Stable";
        int seconds = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 60;
        string outPath = args.Length > 2 ? args[2] : "hangwatch.txt";

        var sb = new StringBuilder();
        sb.AppendLine("time,pid,hwnd,visible,elapsedMs,status");

        var sw = Stopwatch.StartNew();
        int lastPid = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            int pid = 0;
            IntPtr hwnd = IntPtr.Zero;
            bool visible = false;
            try
            {
                var ps = Process.GetProcessesByName(procName);
                foreach (var p in ps)
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        pid = p.Id;
                        hwnd = p.MainWindowHandle;
                        visible = IsWindowVisible(hwnd);
                        break;
                    }
                }
            }
            catch { }

            if (hwnd == IntPtr.Zero)
            {
                sb.AppendLine($"{DateTime.Now:HH:mm:ss.fff},{pid},0,False,0,NO_WINDOW");
                Thread.Sleep(200);
                continue;
            }

            var t = Stopwatch.StartNew();
            IntPtr res;
            var ok = SendMessageTimeout(hwnd, 0, IntPtr.Zero, IntPtr.Zero, 2, 2000, out res);
            t.Stop();
            bool hung = ok == IntPtr.Zero;
            string status = hung ? "HUNG" : "OK";
            sb.AppendLine($"{DateTime.Now:HH:mm:ss.fff},{pid},{hwnd},{visible},{t.ElapsedMilliseconds},{status}");

            if (hung) sb.AppendLine($"  !! 卡顿 detected at {DateTime.Now:HH:mm:ss.fff}");
            if (pid != lastPid) { sb.AppendLine($"  -- pid changed {lastPid} -> {pid}"); lastPid = pid; }

            Thread.Sleep(200);
        }

        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine("WROTE " + outPath);
        return 0;
    }
}
