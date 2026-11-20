using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SoulLink.Services;   // ★ 临时诊断：InputTrace（交接文档 4.1 取证，结论后连同文件一起删除）

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
        private static string? _deploymentNonce;
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
        private const uint MOUSEEVENTF_XDOWN = 0x0080;
        private const uint MOUSEEVENTF_XUP = 0x0100;
        private const uint XBUTTON1 = 0x0001;
        private const uint XBUTTON2 = 0x0002;

        /// <summary>当前处于按下状态的鼠标键。只在真正 down 过之后才记录。</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> PressedButtons
            = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // ── 键盘模拟（自动化流程用）──────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        // ★★ 联合体必须含 MOUSEINPUT —— 它是三个成员里最大的。
        //
        //   Windows 的 sizeof(INPUT) 是 40（x64），它按联合体里**最大**的成员算。
        //   原来这里只有 KEYBDINPUT（24），Marshal.SizeOf 得出 32，
        //   于是 SendInput 收到 cbSize=32 ≠ 40，直接返回 0 并置
        //   ERROR_INVALID_PARAMETER(87)，**一个键都不会被注入**。
        //   更糟的是调用方从不检查返回值 —— 代理照样回「按键完成」，
        //   用户看到"成功"而目标窗口什么都没收到（2026-11-20 实测坐实）。
        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYINPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, KEYINPUT[] pInputs, int cbSize);

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        /// <summary>发送 "F12" / "Ctrl+A" / "Alt+F4" 这类按键组合。</summary>
        private static bool SendKeyCombo(string spec, out string error)
        {
            error = "";
            try
            {
                var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) { error = "没有按键"; return false; }

                var vks = new List<ushort>();
                foreach (var raw in parts)
                {
                    ushort vk = ParseVk(raw.Trim());
                    if (vk == 0) { error = "无法解析按键名：" + raw.Trim(); return false; }
                    vks.Add(vk);
                }

                foreach (var vk in vks)
                    if (!SendVk(vk, false)) { error = "按下 " + spec + " 失败（SendInput 被系统拒绝）"; return false; }
                Thread.Sleep(20);
                for (int i = vks.Count - 1; i >= 0; i--)
                    if (!SendVk(vks[i], true)) { error = "抬起 " + spec + " 失败（SendInput 被系统拒绝）"; return false; }
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        /// <summary>发送一个虚拟键；返回是否真的被系统接受。</summary>
        private static bool SendVk(ushort vk, bool up)
        {
            var input = new KEYINPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        dwFlags = up ? KEYEVENTF_KEYUP : 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
            // ★ 必须检查返回值：0 表示一个事件都没注入（cbSize 不符、UIPI 拦截、
            //   桌面不可交互等），而不是"发送成功"。
            uint sent = SendInput(1, new[] { input }, Marshal.SizeOf<KEYINPUT>());
            if (sent == 1) return true;
            int err = Marshal.GetLastWin32Error();
            Log("SendInput 失败 vk=0x" + vk.ToString("X2") + " up=" + up
                + " cbSize=" + Marshal.SizeOf<KEYINPUT>() + " err=" + err);
            return false;
        }

        /// <summary>按键名 → 虚拟键码。</summary>
        private static ushort ParseVk(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return 0;
            string u = name.Trim().ToUpperInvariant();

            switch (u)
            {
                case "CTRL": case "CONTROL": return 0x11;
                case "ALT": return 0x12;
                case "SHIFT": return 0x10;
                case "WIN": return 0x5B;
                case "ENTER": case "RETURN": return 0x0D;
                case "ESC": case "ESCAPE": return 0x1B;
                case "SPACE": return 0x20;
                case "TAB": return 0x09;
                case "BACKSPACE": return 0x08;
                case "DELETE": case "DEL": return 0x2E;
                case "INSERT": case "INS": return 0x2D;
                case "HOME": return 0x24;
                case "END": return 0x23;
                case "PAGEUP": return 0x21;
                case "PAGEDOWN": return 0x22;
                case "UP": return 0x26;
                case "DOWN": return 0x28;
                case "LEFT": return 0x25;
                case "RIGHT": return 0x27;

                // ★ 2.17.34：符号键。编辑器里的按键捕获会产出这些**字符**，
                //   原来这里一个都没有 → 录了 "-" 或 "/" 会静默返回 0，
                //   表现为"录进去了但按下去没反应"（最坑的失败形态）。
                //   用 OemKey 的虚拟键码，与英文键盘布局一致。
                case "-": return 0xBD;   // VK_OEM_MINUS
                case "=": return 0xBB;   // VK_OEM_PLUS
                case ",": return 0xBC;   // VK_OEM_COMMA
                case ".": return 0xBE;   // VK_OEM_PERIOD
                case "/": return 0xBF;   // VK_OEM_2
                case ";": return 0xBA;   // VK_OEM_1
                case "'": return 0xDE;   // VK_OEM_7
                case "[": return 0xDB;   // VK_OEM_4
                case "]": return 0xDD;   // VK_OEM_6
                case "\\": return 0xDC;  // VK_OEM_5
                case "`": return 0xC0;   // VK_OEM_3
            }

            if (u.Length >= 2 && u[0] == 'F' && int.TryParse(u.Substring(1), out int fn)
                && fn >= 1 && fn <= 24)
                return (ushort)(0x6F + fn);

            if (u.Length == 1 && u[0] >= '0' && u[0] <= '9') return (ushort)u[0];
            if (u.Length == 1 && u[0] >= 'A' && u[0] <= 'Z') return (ushort)u[0];

            if (u.StartsWith("NUMPAD") && int.TryParse(u.Substring(6), out int np)
                && np >= 0 && np <= 9)
                return (ushort)(0x60 + np);

            return 0;
        }

        private static void MouseButton(string button, bool down)
        {
            button = (button ?? "left").ToLowerInvariant();
            uint flags;
            uint data = 0;
            switch (button)
            {
                case "right": flags = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP; break;
                case "middle": flags = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP; break;
                case "x1": flags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; data = XBUTTON1; break;
                case "x2": flags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; data = XBUTTON2; break;
                default: flags = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP; break;
            }
            mouse_event(flags, 0, 0, data, IntPtr.Zero);

            if (down) PressedButtons[button] = true;
            else PressedButtons.TryRemove(button, out _);
        }

        /// <summary>
        /// 兜底释放：**只抬起真正按下过的键**。
        ///
        /// ★ 原来无条件发送 5 个抬起事件（左/右/中/X1/X2）。
        ///   孤立的 RIGHTUP 会让目标窗口弹出右键菜单，孤立的 XUP 会被当成
        ///   "后退/前进"导航 —— 用户实测：每次退出自由视角，各会话桌面都会弹菜单。
        ///   而且这个代理在每个账号会话里都有一个，所以"所有用户都弹"。
        /// </summary>
        private static void ReleaseAllMouseButtons()
        {
            try
            {
                foreach (var kv in PressedButtons)
                {
                    if (!kv.Value) continue;
                    try { MouseButton(kv.Key, false); } catch { }
                }
                PressedButtons.Clear();
            }
            catch { }
        }

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        private static RelayHotkeyReceiver? _hotkeys;
        private static NamedPipeClientStream? _eventPipe;
        private static StreamWriter? _eventWriter;
        private static readonly object EventWriteLock = new object();
        private static readonly System.Collections.Concurrent.BlockingCollection<string> HotkeyEvents
            = new(128);
        private static Task? _eventSender;

        private static void SendHotkey(string value)
        {
            // 钩子线程只入队，不执行序列化/管道 IO；队列满则丢弃过期通知，不阻塞系统输入。
            bool added = false;
            try { added = HotkeyEvents.TryAdd(value); } catch { }
            // ★ 入队结果必须记录：以前这里静默丢弃，
            //   出现"代理已命中但宿主收不到"时完全看不出是哪一环断的。
            Log("热键入队 value=" + value + " ok=" + added
                + " writer=" + (_eventWriter != null) + " sender=" + (_eventSender != null));
        }

        private static async Task SendHotkeysAsync()
        {
            try
            {
                foreach (string value in HotkeyEvents.GetConsumingEnumerable())
                {
                    try
                    {
                        if (_eventWriter == null)
                        {
                            Log("热键发送跳过：_eventWriter 为空 value=" + value);
                            continue;
                        }
                        await _eventWriter.WriteLineAsync(JsonSerializer.Serialize(new
                        {
                            Event = "hotkey", Value = value
                        }, Json)).ConfigureAwait(false);
                        Log("热键已写入事件管道 value=" + value);
                    }
                    catch (Exception ex) { Log("热键事件回传异常：" + ex.Message); }
                }
            }
            catch { }
        }

        private static string _logPath = "";
        private static readonly StringBuilder LogBuf = new StringBuilder();
        private static readonly object LogLock = new object();

        private static void Log(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg;
            lock (LogLock)
            {
                LogBuf.AppendLine(line);
                if (LogBuf.Length > 20000) LogBuf.Remove(0, LogBuf.Length - 16000);
            }
            // 不在低级钩子回调中打开/重写日志文件。
            if (_logPath.Length > 0) SoulLink.Services.AsyncLog.Append(_logPath, line + Environment.NewLine);
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
            public string Hotkey { get; set; } = "";
            public bool Trace { get; set; }
        }

        private sealed class RelayEvent
        {
            public string Event { get; set; } = "";
            public string Value { get; set; } = "";
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
            public int SessionId { get; set; }
            public bool Elevated { get; set; }
            public bool DesktopOk { get; set; }
            public string Nonce { get; set; } = "";
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
            _deploymentNonce = Services.BrokerArguments.NonceFromArgs(args);
            _deploymentNonce = Services.BrokerArguments.NonceFromArgs(args);
            try { _logPath = Path.Combine(Path.GetTempPath(), "soullink-relay-" + Process.GetCurrentProcess().Id + ".log"); }
            catch { }

            try { SetProcessDPIAware(); } catch { }
            if (args.Any(a => a == "--require-elevated"))
            {
                string? failure = AgentSecurity.Validate(Process.GetCurrentProcess().SessionId,
                    Services.BrokerArguments.NonceFromArgs(args));
                if (failure != null) { Log("拒绝启动：" + failure); return 5; }
            }

            Log("relay 启动 pid=" + Process.GetCurrentProcess().Id + " pipe=" + pipeName
                + " session=" + Process.GetCurrentProcess().SessionId);
            _hotkeys = new RelayHotkeyReceiver(SendHotkey, Log);
            try
            {
                var eventPipe = new NamedPipeClientStream(".", pipeName + "_Events", PipeDirection.Out, PipeOptions.Asynchronous);
                eventPipe.Connect(3000);
                _eventPipe = eventPipe;
                _eventWriter = new StreamWriter(eventPipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                _eventSender = Task.Run(SendHotkeysAsync);
                Log("热键事件管道已连接");
            }
            catch (Exception ex) { Log("热键事件管道连接失败：" + ex.Message); }

            var stop = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };

            int idleRounds = 0;
            AppDomain.CurrentDomain.ProcessExit += (_, __) => ReleaseAllMouseButtons();
            while (!stop.IsSet)
            {
                try
                {
                    using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None))
                    {
                        // 第一次连不上的话快速失败，交给宿主重试
                        pipe.Connect(3000);
                        _traceRelaySw = Stopwatch.StartNew();
                        Log("已连上宿主管道");
                        InputTrace.Write("AGENT_CONNECT", "已连上宿主管道 pid=" + Process.GetCurrentProcess().Id
                            + " 会话=" + Process.GetCurrentProcess().SessionId + " 管道=" + pipeName);
                        idleRounds = 0;
                        ServePipe(pipe, stop);
                        break; // 宿主消失后不保留孤儿代理和输入状态。
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

            InputTrace.Write("AGENT_EXIT", "relay 退出 pid=" + Process.GetCurrentProcess().Id
                + " 会话=" + Process.GetCurrentProcess().SessionId
                + " 握手后存活=" + _traceRelaySw.ElapsedMilliseconds + "ms");
            ReleaseAllMouseButtons();
            try { _hotkeys?.Dispose(); } catch { }
            _hotkeys = null;
            HotkeyEvents.CompleteAdding();
            try { _eventWriter?.Dispose(); } catch { }
            try { _eventPipe?.Dispose(); } catch { }
            _eventWriter = null;
            _eventPipe = null;
            Log("relay 退出");
            return 0;
        }

        // ★ 临时诊断（交接文档 4.1 取证）：记录从"连上宿主"到"被断开"的真实时长。
        private static Stopwatch _traceRelaySw = Stopwatch.StartNew();
        private static int _traceCmdCount;

        private static void ServePipe(Stream pipe, ManualResetEventSlim stop)
        {
            var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

            while (!stop.IsSet)
            {
                string? line = null;
                try { line = reader.ReadLine(); }
                catch (Exception rex)
                {
                    InputTrace.Write("AGENT_PIPE_ERR", "读管道异常 pid=" + Process.GetCurrentProcess().Id
                        + " 会话=" + Process.GetCurrentProcess().SessionId + " 异常=" + rex.GetType().Name + " " + rex.Message);
                    break;
                }
                if (line == null)
                {
                    InputTrace.Write("AGENT_EOF", "宿主关掉了管道（EOF）pid=" + Process.GetCurrentProcess().Id
                        + " 会话=" + Process.GetCurrentProcess().SessionId + " 握手后存活=" + _traceRelaySw.ElapsedMilliseconds + "ms");
                    break;
                }
                if (line.Length == 0) continue;
                // 降温：自由视角/探测期间指令会连着来，只记前若干条与收尾指令，避免日志被刷爆。
                bool traceAlways = line.IndexOf("releaseall", StringComparison.OrdinalIgnoreCase) >= 0
                                || line.IndexOf("\"exit\"", StringComparison.OrdinalIgnoreCase) >= 0;
                if (traceAlways || _traceCmdCount < 30)
                {
                    if (!traceAlways) _traceCmdCount++;
                    InputTrace.Write("AGENT_RECV_CMD", "收到指令 pid=" + Process.GetCurrentProcess().Id
                        + " cmd=" + (line.Length > 120 ? line.Substring(0, 120) : line)
                        + " 握手后=" + _traceRelaySw.ElapsedMilliseconds + "ms");
                }

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

            ReleaseAllMouseButtons();
            Log("宿主断开，释放鼠标并退出");
        }

        private static Response Fail(string message) => new Response { Ok = false, Message = message };

        // ── 指令实现 ─────────────────────────────────────────────────────────

        private static Response Execute(Request req)
        {
            switch ((req.Cmd ?? "").ToLowerInvariant())
            {
                case "hotkey":
                    try
                    {
                        string message = _hotkeys?.Configure(req.Hotkey, req.Trace) ?? "接收器未启动";
                        return new Response { Ok = message == "已安装" || message.StartsWith("鼠标") || message == "配置已排队", Message = message };
                    }
                    catch (Exception ex) { return Fail("配置热键失败：" + ex.Message); }

                case "ping":
                    return new Response
                    {
                        Ok = true,
                        Message = "pong",
                        SessionId = Process.GetCurrentProcess().SessionId,
                        Elevated = Environment.IsPrivilegedProcess,
                        DesktopOk = AgentSecurity.Validate(Process.GetCurrentProcess().SessionId, _deploymentNonce) == null,
                        Nonce = _deploymentNonce ?? "",
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

                        // ★ 不再做"贴边回中"。
                        //   原实现：远端光标一旦贴边就 SetCursorPos 到屏幕中心。
                        //   游戏看到光标位置突变，表现为"画面被猛地拽一下" —— 快速移动时最明显。
                        //   这与"给游戏真实鼠标数据"的原则冲突，已移除。
                        GetCursorPos(out POINT pNow);

                        return new Response
                        {
                            Ok = true,
                            X = pNow.X,
                            Y = pNow.Y,
                            Message = "相对移动 " + totalDx + "," + totalDy + " (steps=" + steps + ")"
                        };
                    }

                case "click":
                    {
                        int count = req.Count <= 0 ? 1 : Math.Min(req.Count, 5);
                        for (int i = 0; i < count; i++)
                        {
                            MouseButton(req.Button, true);
                            Thread.Sleep(18);
                            MouseButton(req.Button, false);
                            if (i + 1 < count) Thread.Sleep(90);
                        }
                        GetCursorPos(out POINT pc);
                        return new Response { Ok = true, X = pc.X, Y = pc.Y, Message = (req.Button ?? "left") + " 点击 x" + count };
                    }

                // 按下/抬起分开控制：转视角时要「按住右键 + 相对移动 + 松开」
                case "down":
                    MouseButton(req.Button, true);
                    return new Response { Ok = true, Message = (req.Button ?? "left") + " 按下" };

                case "up":
                    MouseButton(req.Button, false);
                    return new Response { Ok = true, Message = (req.Button ?? "left") + " 抬起" };

                case "releaseall":
                    ReleaseAllMouseButtons();
                    return new Response { Ok = true, Message = "全部鼠标按键已释放" };

                case "wheel":
                    {
                        int delta = req.Delta == 0 ? 120 : req.Delta;
                        mouse_event(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)delta), IntPtr.Zero);
                        return new Response { Ok = true, Message = "滚轮 " + delta };
                    }

                // ★ 模拟按键（自动化流程用）。
                //   req.Button 字段复用为"按键名"，支持单键（F12）和组合（Ctrl+A）。
                case "key":
                    {
                        string spec = req.Button ?? "";
                        if (string.IsNullOrWhiteSpace(spec)) return Fail("缺少按键");

                        int times = Math.Max(1, Math.Min(req.Count <= 0 ? 1 : req.Count, 10));
                        for (int i = 0; i < times; i++)
                        {
                            if (!SendKeyCombo(spec, out string keyError)) return Fail(keyError);
                            if (i + 1 < times) Thread.Sleep(80);
                        }
                        return new Response { Ok = true, Message = "按键 " + spec + " x" + times };
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
                    InputTrace.Write("AGENT_RECV", "收到 exit 指令（宿主让我们退出）pid=" + Process.GetCurrentProcess().Id);
                    Log("收到退出指令");
                    ReleaseAllMouseButtons();
                    return new Response { Ok = true, Message = "bye" };

                default:
                    return Fail("未知指令：" + req.Cmd);
            }
        }
    }
}
