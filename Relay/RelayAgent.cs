using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Relay
{
    /// <summary>
    /// 目标会话内的注入代理。
    ///
    /// 为什么需要它：输入队列按会话隔离，必须**在目标会话内部**调用 mouse_event / SetCursorPos
    /// 才能真正作用到那个会话的窗口（HANDOFF 3.1）。
    ///
    /// 宿主通过 SYSTEM 计划任务把它投进目标会话（免安装），它连回宿主的命名管道收指令。
    /// 本程序就是 SoulLink.exe 自己，用 --relay 进入此模式，不需要额外打包。
    /// </summary>
    internal static class RelayAgent
    {
        // ── Win32 ────────────────────────────────────────────────────────────
        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT p);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

        // mouse_event 标志位
        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;
        private const uint MOUSEEVENTF_HWHEEL = 0x01000;
        private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        private static string _logPath = "";
        private static readonly StringBuilder LogBuf = new StringBuilder();
        private static readonly object LogLock = new object();

        private static void Log(string msg)
        {
            lock (LogLock)
            {
                try
                {
                    LogBuf.AppendLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg);
                    if (LogBuf.Length > 20000) LogBuf.Remove(0, LogBuf.Length - 16000);
                    if (_logPath.Length > 0) File.WriteAllText(_logPath, LogBuf.ToString(), new UTF8Encoding(false));
                }
                catch { }
            }
        }

        // ── 消息协议 ─────────────────────────────────────────────────────────

        private sealed class Request
        {
            public string Cmd { get; set; } = "";
            public int X { get; set; }
            public int Y { get; set; }
            public int Dx { get; set; }
            public int Dy { get; set; }
            public int Delta { get; set; }
            public string Button { get; set; } = "";
            public int Count { get; set; } = 1;
        }

        private sealed class Response
        {
            public bool Ok { get; set; }
            public string Message { get; set; } = "";
            public int X { get; set; }
            public int Y { get; set; }
            public int ScreenWidth { get; set; }
            public int ScreenHeight { get; set; }
            public string Info { get; set; } = "";
            public List<string> Windows { get; set; } = new List<string>();
        }

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        // ── 入口 ─────────────────────────────────────────────────────────────

        /// <summary>被 SoulLink.exe 以 --relay 启动时调用。参数：--relay &lt;管道名&gt;</summary>
        public static int Run(string[] args)
        {
            string pipeName = args.Length > 1 ? args[1] : "SoulLink.Relay";
            try { _logPath = Path.Combine(Path.GetTempPath(), "soullink-relay-" + Process.GetCurrentProcess().Id + ".log"); }
            catch { }

            try { SetProcessDPIAware(); } catch { }

            Log("relay 启动 pid=" + Process.GetCurrentProcess().Id + " pipe=" + pipeName
                + " session=" + Process.GetCurrentProcess().SessionId);

            var stop = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };

            int idleRounds = 0;
            while (!stop.IsSet)
            {
                try
                {
                    using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None))
                    {
                        // 第一次连不上的话快速失败，交给宿主重试
                        pipe.Connect(3000);
                        Log("已连上宿主管道");
                        idleRounds = 0;
                        ServePipe(pipe, stop);
                    }
                }
                catch (TimeoutException)
                {
                    idleRounds++;
                    if (idleRounds > 40) { Log("连不上宿主，退出"); break; }
                    Thread.Sleep(500);
                }
                catch (Exception ex)
                {
                    Log("管道异常：" + ex.Message);
                    Thread.Sleep(500);
                }
            }

            Log("relay 退出");
            return 0;
        }

        private static void ServePipe(Stream pipe, ManualResetEventSlim stop)
        {
            var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

            while (!stop.IsSet)
            {
                string? line = null;
                try { line = reader.ReadLine(); }
                catch { break; }
                if (line == null) break;                 // 宿主断开
                if (line.Length == 0) continue;

                Response resp;
                try
                {
                    var req = JsonSerializer.Deserialize<Request>(line, Json);
                    resp = req == null ? Fail("空指令") : Execute(req);
                }
                catch (Exception ex)
                {
                    resp = Fail("执行异常：" + ex.Message);
                }

                try { writer.WriteLine(JsonSerializer.Serialize(resp, Json)); }
                catch { break; }
            }

            Log("宿主断开，等待重连");
        }

        private static Response Fail(string message) => new Response { Ok = false, Message = message };

        // ── 指令实现 ─────────────────────────────────────────────────────────

        private static Response Execute(Request req)
        {
            switch ((req.Cmd ?? "").ToLowerInvariant())
            {
                case "ping":
                    return new Response
                    {
                        Ok = true,
                        Message = "pong",
                        ScreenWidth = GetSystemMetrics(SM_CXSCREEN),
                        ScreenHeight = GetSystemMetrics(SM_CYSCREEN),
                        Info = "pid=" + Process.GetCurrentProcess().Id + " session=" + Process.GetCurrentProcess().SessionId
                    };

                case "cursor":
                    GetCursorPos(out POINT p0);
                    return new Response
                    {
                        Ok = true,
                        X = p0.X,
                        Y = p0.Y,
                        ScreenWidth = GetSystemMetrics(SM_CXSCREEN),
                        ScreenHeight = GetSystemMetrics(SM_CYSCREEN)
                    };

                // 绝对定位：点击/悬停都走这个
                case "move":
                    {
                        bool ok = SetCursorPos(req.X, req.Y);
                        GetCursorPos(out POINT p1);
                        return new Response
                        {
                            Ok = ok, X = p1.X, Y = p1.Y,
                            Message = ok ? ("已定位到 " + p1.X + "," + p1.Y) : "SetCursorPos 失败",
                            ScreenWidth = GetSystemMetrics(SM_CXSCREEN),
                            ScreenHeight = GetSystemMetrics(SM_CYSCREEN)
                        };
                    }

                // 相对位移：转游戏视角（游戏读 Raw Input，只有这个有用）
                case "moverel":
                    {
                        // 大位移要拆成多段，一次性给人过大的 dx 会被系统截断
                        int totalDx = req.Dx, totalDy = req.Dy;
                        int steps = Math.Max(1, Math.Min(20, (Math.Abs(totalDx) + Math.Abs(totalDy)) / 120 + 1));
                        for (int i = 0; i < steps; i++)
                        {
                            int sx = totalDx / steps;
                            int sy = totalDy / steps;
                            mouse_event(MOUSEEVENTF_MOVE, sx, sy, 0, IntPtr.Zero);
                            if (steps > 4) Thread.Sleep(6);
                        }
                        // 余数补上
                        int remX = totalDx - (totalDx / steps) * steps;
                        int remY = totalDy - (totalDy / steps) * steps;
                        if (remX != 0 || remY != 0) mouse_event(MOUSEEVENTF_MOVE, remX, remY, 0, IntPtr.Zero);

                        // ★ 光标贴着屏幕边缘时，相对位移会被"吃掉"（鼠标撞墙就动不了）。
                        //   转视角时这是致命问题：往右转到头就再也转不动了。
                        //   办法：撞墙后把光标挪回屏幕中心，再让宿主下一个指令继续。
                        GetCursorPos(out POINT pNow);
                        int sw = GetSystemMetrics(SM_CXSCREEN);
                        int sh = GetSystemMetrics(SM_CYSCREEN);
                        bool atEdge = pNow.X <= 1 || pNow.Y <= 1 || pNow.X >= sw - 2 || pNow.Y >= sh - 2;

                        string extra = "";
                        if (atEdge)
                        {
                            int cx = sw / 2, cy = sh / 2;
                            SetCursorPos(cx, cy);
                            extra = " · 光标贴边已回中";
                        }

                        return new Response
                        {
                            Ok = true,
                            X = pNow.X,
                            Y = pNow.Y,
                            Message = "相对移动 " + totalDx + "," + totalDy + " (steps=" + steps + ")" + extra
                        };
                    }

                case "click":
                    {
                        int count = req.Count <= 0 ? 1 : Math.Min(req.Count, 5);
                        uint down, up;
                        switch ((req.Button ?? "left").ToLowerInvariant())
                        {
                            case "right": down = MOUSEEVENTF_RIGHTDOWN; up = MOUSEEVENTF_RIGHTUP; break;
                            case "middle": down = MOUSEEVENTF_MIDDLEDOWN; up = MOUSEEVENTF_MIDDLEUP; break;
                            default: down = MOUSEEVENTF_LEFTDOWN; up = MOUSEEVENTF_LEFTUP; break;
                        }
                        for (int i = 0; i < count; i++)
                        {
                            mouse_event(down, 0, 0, 0, IntPtr.Zero);
                            Thread.Sleep(18);
                            mouse_event(up, 0, 0, 0, IntPtr.Zero);
                            if (i + 1 < count) Thread.Sleep(90);
                        }
                        GetCursorPos(out POINT pc);
                        return new Response { Ok = true, X = pc.X, Y = pc.Y, Message = (req.Button ?? "left") + " 点击 x" + count };
                    }

                // 按下/抬起分开控制：转视角时要「按住右键 + 相对移动 + 松开」
                case "down":
                    {
                        uint f = (req.Button ?? "left").ToLowerInvariant() == "right" ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN;
                        mouse_event(f, 0, 0, 0, IntPtr.Zero);
                        return new Response { Ok = true, Message = (req.Button ?? "left") + " 按下" };
                    }

                case "up":
                    {
                        uint f = (req.Button ?? "left").ToLowerInvariant() == "right" ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP;
                        mouse_event(f, 0, 0, 0, IntPtr.Zero);
                        return new Response { Ok = true, Message = (req.Button ?? "left") + " 抬起" };
                    }

                case "wheel":
                    {
                        int delta = req.Delta == 0 ? 120 : req.Delta;
                        mouse_event(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)delta), IntPtr.Zero);
                        return new Response { Ok = true, Message = "滚轮 " + delta };
                    }

                case "foreground":
                    {
                        IntPtr h = GetForegroundWindow();
                        if (h == IntPtr.Zero) return Fail("没有前台窗口");
                        int len = GetWindowTextLength(h);
                        var sb = new StringBuilder(len + 1);
                        GetWindowText(h, sb, sb.Capacity);
                        GetWindowThreadProcessId(h, out uint pid);
                        string title = sb.ToString();
                        GetWindowRect(h, out RECT r);
                        return new Response
                        {
                            Ok = true,
                            Message = title,
                            Info = "pid=" + pid + " rect=" + r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom
                        };
                    }

                case "windows":
                    {
                        var list = new List<string>();
                        var seen = new HashSet<string>();
                        EnumWindows((h, l) =>
                        {
                            if (!IsWindowVisible(h)) return true;
                            int len = GetWindowTextLength(h);
                            if (len <= 0) return true;
                            var sb = new StringBuilder(len + 1);
                            GetWindowText(h, sb, sb.Capacity);
                            GetWindowThreadProcessId(h, out uint pid);
                            GetWindowRect(h, out RECT r);
                            string t = sb.ToString();
                            if (t.Length == 0) return true;

                            string entry = pid + "|" + r.Left + "," + r.Top + "," +
                                           (r.Right - r.Left) + "x" + (r.Bottom - r.Top) + "|" + t;
                            // 同一个窗口会被 EnumWindows 多次回报（不同 owner 层），去重
                            if (seen.Add(entry)) list.Add(entry);
                            return true;
                        }, IntPtr.Zero);
                        return new Response { Ok = true, Message = "共 " + list.Count + " 个窗口", Windows = list };
                    }

                case "exit":
                    Log("收到退出指令");
                    Environment.Exit(0);
                    return new Response { Ok = true, Message = "bye" };

                default:
                    return Fail("未知指令：" + req.Cmd);
            }
        }
    }
}
