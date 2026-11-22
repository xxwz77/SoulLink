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

        /// <summary>录屏默认参数（可用环境变量覆盖，主程序也能通过 record 命令改）。</summary>
        private static int RecordingDefaultFps
            => int.TryParse(Environment.GetEnvironmentVariable("SOULLINK_REC_FPS"), out int v) && v > 0 ? v : 10;
        private static int RecordingDefaultQuality
            => int.TryParse(Environment.GetEnvironmentVariable("SOULLINK_REC_QUALITY"), out int v) && v > 0 ? v : 75;
        private static int RecordingDefaultKeepSeconds
            => int.TryParse(Environment.GetEnvironmentVariable("SOULLINK_REC_KEEP"), out int v) && v > 0 ? v : 30;
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

        /// <summary>
        /// 当前处于按下状态的**键盘虚拟键**（keydown 按下后记录，keyup/释放后移除）。
        ///
        /// ★ 会话隔离：代理进程只在**一个目标会话**内运行（宿主按会话各投一个代理），
        ///   所以这份静态集合天然就是"该会话按住的所有键"；不同会话各有一个代理进程，
        ///   互不共享。断线释放（ReleaseAllKeys）就靠它。
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ushort, bool> PressedKeys
            = new System.Collections.Concurrent.ConcurrentDictionary<ushort, bool>();

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
        private const uint KEYEVENTF_UNICODE = 0x0004;

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

        /// <summary>
        /// 发送一个 Unicode 标量值（1 或 2 个 UTF-16 码元，成对的代理对）。
        ///
        /// ★ KEYEVENTF_UNICODE 必须与 KEYEVENTF_KEYUP 组合使用：
        ///   按下时 wScan 放 UTF-16 码元、dwFlags=KEYEVENTF_UNICODE；
        ///   抬起时再 OR KEYEVENTF_KEYUP。代理对要 down+up 连续发送，不拆开。
        /// </summary>
        private static bool SendUnicode(string unit)
        {
            for (int i = 0; i < unit.Length; i++)
            {
                for (int phase = 0; phase < 2; phase++)
                {
                    uint flags = KEYEVENTF_UNICODE | (phase == 1 ? KEYEVENTF_KEYUP : 0);
                    var input = new KEYINPUT
                    {
                        type = INPUT_KEYBOARD,
                        u = new INPUTUNION
                        {
                            ki = new KEYBDINPUT
                            {
                                wVk = 0,
                                wScan = unit[i],
                                dwFlags = flags,
                                dwExtraInfo = IntPtr.Zero
                            }
                        }
                    };
                    uint sent = SendInput(1, new[] { input }, Marshal.SizeOf<KEYINPUT>());
                    if (sent != 1)
                    {
                        int err = Marshal.GetLastWin32Error();
                        Log("SendUnicode 失败 char=0x" + ((ushort)unit[i]).ToString("X4")
                            + " phase=" + phase + " err=" + err);
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>按键名 → 虚拟键码。统一走 Services.InputKeys.ParseVk，保证与编辑器词表一致。</summary>
        private static ushort ParseVk(string name) => Services.InputKeys.ParseVk(name);

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

        /// <summary>
        /// 兜底释放：遍历「当前按住的键集合」，逐个发 KEYEVENTF_KEYUP。
        ///
        /// ★ 防「摇杆推着时手机断线，角色一直往前跑」：代理与宿主的管道断开
        ///   （ServePipe 退出）或进程退出时，这里把 keydown 期间按住的键全部抬起。
        ///   只抬真正按过的键，不无条件扫全键盘（孤立 KEYUP 可能误触别的窗口）。
        /// </summary>
        private static void ReleaseAllKeys()
        {
            try
            {
                foreach (var kv in PressedKeys)
                {
                    if (!kv.Value) continue;
                    try { SendVk(kv.Key, true); } catch { }
                }
                PressedKeys.Clear();
            }
            catch { }
        }

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        private static RelayHotkeyReceiver? _hotkeys;
        private static NamedPipeClientStream? _eventPipe;
        private static StreamWriter? _eventWriter;
        private static readonly Lock EventWriteLock = new();
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
        private static readonly Lock LogLock = new();

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

        /// <summary>供 SessionSlimmer 等 Services 层在不引用 Relay 内部状态时安静记日志。</summary>
        internal static void LogQuiet(string msg)
        {
            try { Log(msg); } catch { }
        }

        /// <summary>
        /// 代理启动时按环境变量写用户级启动项策略（路径A）。
        ///
        /// ★ 环境变量 SOULLINK_USER_POLICY：1=写禁用（默认）；0=删值恢复；缺失=不动作。
        /// ★ 写失败静默降级：策略失败绝不能拖垮注入代理，只能记日志。
        /// </summary>
        private static void ApplyStartupUserPolicy()
        {
            try
            {
                string raw = Environment.GetEnvironmentVariable("SOULLINK_USER_POLICY") ?? "";
                if (raw.Length == 0) { Log("用户级策略：未下发开关，跳过"); return; }
                bool enable = raw != "0";
                SoulLink.Services.SessionSlimmer.ApplyUserPolicy(enable);
                Log("用户级策略：已" + (enable ? "写入禁用" : "删值恢复默认") + "（Edge 预启动/后台）");
            }
            catch (Exception ex)
            {
                Log("用户级策略写入异常（已忽略）：" + ex.Message);
            }
        }

        /// <summary>
        /// 起代理侧的网页直连服务（端口/口令来自主程序投送时下发的环境变量）。
        ///
        /// ★ 参数一律走环境变量，不走命令行：
        ///   命令行会出现在任务管理器/进程列表里，口令写在那儿等于公开。
        /// </summary>
        private static void StartWebDirect()
        {
            try
            {
                string portText = Environment.GetEnvironmentVariable("SOULLINK_WEB_PORT") ?? "";
                if (!int.TryParse(portText, out int port) || port <= 0)
                {
                    Log("网页直连：未配置端口，跳过");
                    return;
                }

                string token = Environment.GetEnvironmentVariable("SOULLINK_WEB_TOKEN") ?? "";
                string accountId = Environment.GetEnvironmentVariable("SOULLINK_ACCOUNT_ID") ?? "";

                AgentWebServer.Start(port, token, accountId, Log);
            }
            catch (Exception ex) { Log("网页直连启动异常：" + ex.Message); }
        }

        // ── 消息协议 ─────────────────────────────────────────────────────────

        internal sealed class Request
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

            /// <summary>
            /// 非 0 表示"先定位到 X,Y 再点击"。
            /// ★ 合并成一条命令是为了省一次往返：RelayClient 是串行请求-响应，
            ///   拆成 move + click 要等两个 RTT（实测每次 45~64ms，双击就 200ms+）。
            /// </summary>
            public int Move { get; set; }

            /// <summary>
            /// true = 单向指令：代理执行后**不回写一行回复**，宿主也不等待。
            ///
            /// ★ 用途：moverel 这类高频位移不需要确认，等回复纯属浪费一个 RTT。
            ///   协议对齐保证：宿主每条请求都写一行，代理每条都读一行；
            ///   代理只在非 NoReply 指令后写一行。NoReply 指令双方都不收发额外字节，
            ///   所以下一条指令的回复仍然是它自己的回复，通道不会错位。
            ///
            /// ⚠ 代理端 ServePipe 读到 NoReply 指令后必须跳过 WriteLine，
            ///   否则宿主读到的那行会错位（见 ServePipe 的 continue 分支）。
            /// </summary>
            public bool NoReply { get; set; }
        }

        private sealed class RelayEvent
        {
            public string Event { get; set; } = "";
            public string Value { get; set; } = "";
        }

        internal sealed class Response
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
            /// <summary>通用计数（录制保存时回传帧数等）。</summary>
            public int Count { get; set; }
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

            // ★★ 3.3.x：代理启动时写当前会话（本账号）的用户级策略 ★★
            //
            //   宿主挂载已登录用户的 NTUSER.DAT 会因文件被独占而失败，所以
            //   class=User 的策略（Edge 预启动/后台）改由**代理**在目标会话内
            //   直接写自己的 HKCU —— 天然就是目标账号，绕开挂载死结（路径A）。
            //   是否执行由主程序投送时的环境变量 SOULLINK_USER_POLICY 决定；
            //   写失败静默降级，绝不阻塞代理进入注入循环。
            ApplyStartupUserPolicy();

            // ★★ 3.3.5：网页直连代理 ★★
            //
            //   用户要求：「网页端尽量隔离主客户端」「模拟按键也有独立出来」。
            //   这里让代理自己开一个 HTTP 监听，网页直接连它取画面/录像/注入按键，
            //   主程序完全不参与流量（只提供"账号 → 端口"的映射表）。
            //
            //   端口和口令由主程序投送时通过环境变量下发（见 RelayClient.CaptureEnv）。
            //   没设 SOULLINK_WEB_PORT 或端口为 0 表示用户没开网页访问，不启动。
            StartWebDirect();

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

            // ★★ 3.2.8：录屏由**用户在设置里按账号开启**，不在代理里无条件自动开始 ★★
            //
            //   用户明确纠正：
            //     「我不是说在 RDP 通用设置里增加选项吗？指定的用户开启。」
            //   所以代理启动时**不自动录**，等主程序下发 record 命令才开始。
            //
            //   ★ 生命周期语义（用户纠正过）：
            //     代理活 → 录；代理断开 → 停。
            //     断开就没有画面可言，不搞"脱离宿主继续录"。
            //
            //   ★ 只会录制**设置里勾选的账号**：主程序按配置决定要不要给这个会话
            //     下发 record 命令，代理本身不做账号判断。

            int idleRounds = 0;
            AppDomain.CurrentDomain.ProcessExit += (_, __) => { ReleaseAllMouseButtons(); ReleaseAllKeys(); };
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
            // ★ 代理退出前收掉录制线程：断开就没有画面，不该留孤儿线程
            try { SessionRecorder.Shutdown(); } catch { }
            ReleaseAllMouseButtons();
            ReleaseAllKeys();
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
                bool noReply = false;
                try
                {
                    var req = JsonSerializer.Deserialize<Request>(line, Json);
                    resp = req == null ? Fail("空指令") : Execute(req);
                    noReply = req != null && req.NoReply;
                }
                catch (Exception ex)
                {
                    resp = Fail("执行异常：" + ex.Message);
                }

                // ★ NoReply 指令：代理不回写任何字节，宿主也不等。
                //   若这里照常 WriteLine，宿主读到的行会错位（宿主根本没在等这一行）。
                if (noReply) continue;
                try { writer.WriteLine(JsonSerializer.Serialize(resp, Json)); }
                catch { break; }
            }

            ReleaseAllMouseButtons();
            ReleaseAllKeys();
            Log("宿主断开，释放鼠标/键盘并退出");
        }

        private static Response Fail(string message) => new Response { Ok = false, Message = message };

        // ── 指令实现 ─────────────────────────────────────────────────────────

        internal static Response Execute(Request req)
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
                        // ★ 原注释「大位移一次性给会被系统截断」经查证**不成立**：
                        //   MOUSEINPUT.dx/dy 是 LONG（32 位带符号），微软文档没有
                        //   任何"超过 N 像素被截断"的说明。文档明确的副作用只有
                        //   **指针加速**（相对位移按系统指针速度放大，最多 ×4），
                        //   方向与"截断"恰好相反。原实现的拆段 + if(steps>4) Sleep(6)
                        //   会给 20 段注入引入约 120ms 的人工延迟，是转视角手感的
                        //   最大单一延迟源，直接去掉。
                        //
                        //   保留单段直发：mouse_event 相对位移经系统"指针速度"加速，
                        //   与真实鼠标一致，游戏读 Raw Input 得到完整增量。
                        int totalDx = req.Dx, totalDy = req.Dy;
                        mouse_event(MOUSEEVENTF_MOVE, totalDx, totalDy, 0, IntPtr.Zero);

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
                            Message = "相对移动 " + totalDx + "," + totalDy
                        };
                    }

                case "click":
                    {
                        // ★ Move=1：在同一条命令里先定位再点，省一次往返。
                        if (req.Move != 0) SetCursorPos(req.X, req.Y);

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
                    ReleaseAllKeys();
                    return new Response { Ok = true, Message = "全部鼠标/键盘按键已释放" };

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

                // ★★ 3.3.x 手机端虚拟摇杆：键盘按下 / 抬起分开控制 ★★
                //   原来只有点按式 key（SendKeyCombo 全按下 → Sleep → 全抬起），
                //   无法表达"按住方向键不放"。keydown/keyup 复用 SendVk 的键码映射，
                //   由动作决定 KEYEVENTF_KEYUP 标志，按键名走 req.Button（与 key 一致）。
                case "keydown":
                case "keyup":
                    {
                        bool isDown = (req.Cmd ?? "").Equals("keydown", StringComparison.OrdinalIgnoreCase);
                        string spec = req.Button ?? "";
                        if (string.IsNullOrWhiteSpace(spec)) return Fail("缺少按键");

                        ushort vk = ParseVk(spec);
                        if (vk == 0) return Fail("无法解析按键名：" + spec);

                        if (!SendVk(vk, up: !isDown))
                            return Fail((isDown ? "按下 " : "抬起 ") + spec + " 失败（SendInput 被系统拒绝）");

                        if (isDown) PressedKeys[vk] = true;
                        else PressedKeys.TryRemove(vk, out _);

                        return new Response { Ok = true, Message = (isDown ? "按下 " : "抬起 ") + spec };
                    }

                // ★★ 3.3.x 中文输入：KEYEVENTF_UNICODE 逐字符注入 ★★
                //   代理对（emoji）必须成对发送；长度上限由 UnicodeInput.TryValidate 把关。
                case "text":
                    {
                        string text = req.Button ?? "";
                        if (!Services.UnicodeInput.TryValidate(text, out string textError))
                            return Fail(textError);

                        var units = Services.UnicodeInput.SplitUtf16(text);
                        foreach (var unit in units)
                        {
                            if (!SendUnicode(unit))
                                return Fail("文本注入失败（SendInput 被系统拒绝）");
                        }
                        return new Response { Ok = true, Message = "已输入 " + units.Count + " 字符" };
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

                // ★★ 3.2.8：屏幕录制 / 截图 ★★
                //   录屏必须由**目标会话内的代理**执行：别的会话截不到这个桌面，
                //   投 SYSTEM 计划任务也不行（会话 0 没有交互式桌面，全黑，实测）。
                //   录好的帧写进共享目录，主程序直接读文件，不走投送管道传二进制。
                // ══════════ 录屏 / 截图（3.2.8）══════════
                //
                //   ★ 尺寸取**代理所在会话**（目标 RDP 会话），不是主程序桌面。
                //   ★ 录制走内存环形缓冲，只留最近 N 秒；SSD 平时零写入。
                //   ★ 保存时才落盘到 <程序目录>\capture\s<会话>\...

                case "record":
                    {
                        int sid = Process.GetCurrentProcess().SessionId;
                        string action = (req.Button ?? "").Trim().ToLowerInvariant();
                        if (action == "stop")
                        {
                            SessionRecorder.Stop();
                            return new Response { Ok = true, Message = "已请求停止录制", SessionId = sid };
                        }
                        // 参数复用：Count=fps，X=质量，Delta=环形保留秒数
                        bool ok = SessionRecorder.Start(
                            req.Count > 0 ? req.Count : 15,
                            req.X > 0 ? req.X : 75,
                            req.Delta > 0 ? req.Delta : 30,
                            sid, out string msg);
                        return new Response { Ok = ok, Message = msg, SessionId = sid };
                    }

                case "shot":
                    {
                        // 字段复用：Button = 用户名-昵称，Hotkey = 触发条件，X = 质量
                        int sid = Process.GetCurrentProcess().SessionId;
                        bool ok = SessionRecorder.CaptureOnce(
                            req.Button ?? "", req.Hotkey ?? "", req.X > 0 ? req.X : 75,
                            sid, out string path, out string msg);
                        return new Response
                        {
                            Ok = ok,
                            Message = ok ? ("已截图：" + path) : msg,
                            Info = path,
                            SessionId = sid
                        };
                    }

                case "save":
                    {
                        // 保存录制。字段复用：
                        //   Button = 用户名-昵称（目录一级）
                        //   Hotkey = 触发条件（拼在目录名末尾）
                        //   X      = 触发前秒数
                        //   Delta  = 触发后秒数
                        int sid = Process.GetCurrentProcess().SessionId;
                        string userTag = req.Button ?? "";
                        string trigger = req.Hotkey ?? "";
                        double before = req.X > 0 ? req.X : 30;
                        double after = req.Delta > 0 ? req.Delta : 0;
                        string? dir = SessionRecorder.SaveRecording(userTag, trigger, before, after, sid,
                            out int frames, out string msg);
                        return new Response
                        {
                            Ok = dir != null,
                            Message = msg,
                            Info = dir ?? "",
                            Count = frames,
                            SessionId = sid
                        };
                    }

                case "recordstatus":
                    return new Response
                    {
                        Ok = true,
                        Message = SessionRecorder.StatusText(),
                        SessionId = Process.GetCurrentProcess().SessionId
                    };

                case "frame":
                    {
                        // 取最近一帧并**直接落盘**，返回路径（主程序读文件即可，
                        // 不用在协议里传二进制）。Count 复用为"取第几帧"，-1=最新。
                        int sid = Process.GetCurrentProcess().SessionId;
                        byte[]? jpeg = req.Count >= 0
                            ? SessionRecorder.GetFrameAt(req.Count)
                            : SessionRecorder.GetLatestJpeg();
                        if (jpeg == null)
                            return new Response { Ok = false, Message = "缓冲里没有帧", SessionId = sid };
                        try
                        {
                            string dir = Path.Combine(SessionRecorder.CaptureRoot, "s" + sid, "live");
                            Directory.CreateDirectory(dir);
                            string file = Path.Combine(dir, "latest.jpg");
                            File.WriteAllBytes(file, jpeg);
                            return new Response
                            {
                                Ok = true,
                                Message = "已写出最新帧（" + jpeg.Length / 1024 + " KB）",
                                Info = file,
                                SessionId = sid
                            };
                        }
                        catch (Exception ex)
                        {
                            return new Response { Ok = false, Message = "写帧失败：" + ex.Message, SessionId = sid };
                        }
                    }

                case "exit":
                    InputTrace.Write("AGENT_RECV", "收到 exit 指令（宿主让我们退出）pid=" + Process.GetCurrentProcess().Id);
                    Log("收到退出指令");
                    ReleaseAllMouseButtons();
                    ReleaseAllKeys();
                    return new Response { Ok = true, Message = "bye" };

                default:
                    return Fail("未知指令：" + req.Cmd);
            }
        }
    }
}
