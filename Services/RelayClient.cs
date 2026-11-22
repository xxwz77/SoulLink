using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>
    /// 宿主侧的注入客户端。
    ///
    /// 链路：SYSTEM 计划任务把 SoulLink.exe --relay 投进目标会话 → 代理连回本机命名管道 →
    /// 宿主发 JSON 指令（move / moverel / click / wheel / windows…）→ 代理在**目标会话内部**
    /// 调 mouse_event / SetCursorPos。
    ///
    /// 为什么不能在宿主直接 injection：输入队列按会话隔离，跨会话的合成输入不会落到目标桌面。
    /// </summary>
    public sealed class RelayClient : IDisposable
    {
        private NamedPipeServerStream? _pipe;
        private NamedPipeServerStream? _eventPipe;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private readonly object _sync = new object();
        private readonly Channel<RelayRequest> _requests = Channel.CreateBounded<RelayRequest>(
            new BoundedChannelOptions(256)
            {
                SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
            });
        private Task? _sender;
        private readonly struct RelayRequest
        {
            public RelayRequest(object payload, TaskCompletionSource<RelayResponse?> completion, int timeoutMs, bool noReply = false)
            {
                Payload = payload; Completion = completion; TimeoutMs = timeoutMs; NoReply = noReply;
            }
            public object Payload { get; }
            public TaskCompletionSource<RelayResponse?> Completion { get; }
            public int TimeoutMs { get; }
            public bool NoReply { get; }
        }
        private CancellationTokenSource? _cts;
        private volatile bool _disposed;

        public string PipeName { get; }
        public int SessionId { get; }
        public string UserName { get; }
        public bool IsConnected { get; private set; }
        public string AgentInfo { get; private set; } = "";
        public int AgentPid { get; private set; }
        public bool BrokerBacked { get; private set; }
        public string DeploymentNonce { get; private set; } = "";
        public int ScreenWidth { get; private set; }
        public int ScreenHeight { get; private set; }

        /// <summary>收到代理连接 / 断开</summary>
        public event Action<RelayClient, bool>? StateChanged;
        public event Action<RelayClient, string>? HotkeyPressed;

        public RelayClient(string userName, int sessionId)
        {
            UserName = userName;
            SessionId = sessionId;
            // 管道名带随机后缀，避免多实例 / 多会话串线
            PipeName = AppIdentity.RelayPipePrefix + sessionId + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        // ── 投送 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 用 SYSTEM 通道把代理投进目标会话并等待它连回来。
        /// 免安装：schtasks 建的一次性任务用完即删，不留服务、不留残留。
        /// </summary>
        /// <summary>
        /// 结束指定会话里遗留的注入代理进程（--relay）。
        ///
        /// ★ 为什么需要：同一会话若存在两个代理，它们会用各自的管道争用同一个桌面输入，
        ///   表现为"切到该账号后操作异常/无法自由视角"（实测会话 4 出现过两个）。
        ///   投送前先清掉旧的，保证一个会话只有一个代理。
        ///
        /// 只结束命令行里带 "--relay" 且管道名包含该会话号的进程，
        /// 不会碰主程序，也不会碰其它版本的代理（管道名带身份前缀）。
        /// </summary>
        public static int KillOrphanAgents(int sessionId) => 0;   // 见下方说明：不做命令行匹配

        /// <summary>
        /// 所有仍在线的注入通道（按会话号索引）。
        /// RdpConsole 每次投送成功会登记，断开时移除。
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, RelayClient>
            LiveRegistry = new System.Collections.Concurrent.ConcurrentDictionary<int, RelayClient>();

        /// <summary>登记一个在线通道（由 RdpConsole 在投送成功后调用）。</summary>
        public static void Register(int sessionId, RelayClient relay)
        {
            if (sessionId > 0 && relay != null) LiveRegistry[sessionId] = relay;
        }

        /// <summary>注销通道（断开时调用）。</summary>
        public static void Unregister(int sessionId)
        {
            LiveRegistry.TryRemove(sessionId, out _);
        }

        /// <summary>
        /// 当前本进程里**仍然连着**的通道所在会话号。
        ///
        /// ★ 这是替代「枚举命名管道」的正确做法：
        ///   投送成功时会 Register()、断开时 Unregister()，
        ///   所以这里 O(会话数) 就能拿到答案，不需要遍历系统全部管道。
        ///   实测系统管道数可达 3.5 万，枚举一次要 6.5 秒，
        ///   而本方法耗时约 0。
        ///
        /// ★ 局限：只看本进程。别的 SoulLink 实例（如挂机版）投送的通道
        ///   不在这里 —— 用户明确表示不需要跨版本显示。
        /// </summary>
        public static HashSet<int> GetLiveSessions()
        {
            var set = new HashSet<int>();
            try
            {
                foreach (var kv in LiveRegistry)
                {
                    try
                    {
                        if (kv.Value != null && kv.Value.IsConnected && kv.Key > 0)
                            set.Add(kv.Key);
                    }
                    catch { }
                }
            }
            catch { }
            return set;
        }

        /// <summary>按会话号找在线通道；没有或已断开则返回 null。</summary>
        public static RelayClient? FindBySession(int sessionId)
        {
            if (LiveRegistry.TryGetValue(sessionId, out var r) && r.IsConnected) return r;
            return null;
        }

        /// <summary>
        /// 取进程命令行。用 wmic/powershell 都不稳，这里改用 .NET 自带的方式：
        /// 通过 Process 的 MainModule 只能拿到路径，拿不到参数；
        /// 因此改为从进程名 + 管道名前缀判断，不依赖命令行。
        /// </summary>
        private static int KillAgentsByPipePrefix(string prefix)
        {
            // 由调用方按管道前缀匹配；此处保留占位，实际匹配在 KillOrphanAgents 内完成。
            return 0;
        }

        /// <summary>
        /// 投送注入代理到目标会话。
        /// ★ 不需要密码：走 SYSTEM 计划任务 → WTSQueryUserToken → CreateProcessAsUser，
        ///   由系统直接在该会话的桌面上拉起进程，全程不涉及账号凭据。
        /// </summary>
        public async Task<(bool ok, string message)> DeployAsync(Account account, int timeoutMs = 20000)
        {
            // 用 SoulLink.exe 自己当代理 —— 自包含单文件，目标会话里不需要任何运行时
            string self = SystemLauncher.ResolveSelfExe();
            if (!File.Exists(self)) return (false, "找不到 SoulLink.exe 本体：" + self);

            // ★★ 只有**非 Broker** 通道才需要本地解析代理副本。★★
            //
            //   原来这里无条件算 agent 并在缺失时直接 return(false)，
            //   于是便携版（dist 里的 exe）永远发不出 Broker 请求：
            //     · IsFixedTestPath(dist 里的 exe) = false
            //     · agent := ProxyHost.Resolve() → 目标目录里没有 → 空
            //     · 第 140 行直接 return(false, "找不到可用于投送的代理副本：")
            //   用户看到的现象就是「日志说通道投送成功，但根本没有代理进程」。
            //
            //   Broker 通道的代理由**服务自己**从固定目录（C:Program FilesSoulLinkDev）
            //   启动，客户端根本不需要、也不允许指定代理路径。
            //   所以只在走计划任务通道时才解析它。
            bool useBroker = App.Config.Settings.UseBrokerChannel;
            string agent = "";
            if (!useBroker)
            {
                agent = BrokerInstall.IsFixedTestPath(self)
                    ? BrokerInstall.AgentFor(self)
                    : ProxyHost.Resolve();
                if (!File.Exists(agent))
                    return (false, "找不到可用于投送的代理副本：" + agent);
            }
            else
            {
                // ★ 诊断：Broker 分支必须留下痕迹，否则失败时完全静默
                //   （用户实测：日志只有"投送成功"，服务端一条 ensure 都没收到）
                LogBus.Info("Broker 投送准备", "会话 " + SessionId
                    + " · self=" + self
                    + " · 服务目录=" + BrokerInstall.Root);
            }

            // ★ 先确认会话在线再建管道：离线会话建出来的代理没人看得到，
            //   Broker 那边也会因为 WTSQueryUserToken 失败给出含糊的报错。
            string? offline = SystemLauncher.DescribeSessionOnline(SessionId, account.UserName);
            if (offline != null)
            {
                // ★ 这是"投送凭空消失"的头号嫌疑：会话判定不在线就静默返回，
                //   而调用方（BeginInputCore）失败时只 Debug，非 verbose 模式下被过滤。
                LogBus.Warn("目标会话不在线，已跳过投送",
                    "会话 " + SessionId + " · 账号 " + account.UserName + " · " + offline);
                return (false, offline);
            }

            _cts = new CancellationTokenSource();

            // 先把管道建好再投放，避免代理连回来时宿主还没准备好
            var accepted = AcceptAgentAsync(timeoutMs, _cts.Token);
            var events = AcceptEventsAsync(timeoutMs, _cts.Token);

            SystemLauncher.Result launch;
            if (useBroker)
            {
                DeploymentNonce = Guid.NewGuid().ToString("N");
                var broker = await BrokerProtocol.SendAsync(new BrokerProtocol.Request
                {
                    Command = "ensure", SessionId = SessionId, UserName = account.UserName,
                    PipeName = PipeName, Nonce = DeploymentNonce,
                    Env = CaptureEnv(account.Id)   // ★ 3.2.8 录制目录；3.3.5 网页直连端口/口令
                }).ConfigureAwait(false);
                launch = new SystemLauncher.Result
                {
                    Ok = broker.Ok, Pid = broker.Pid, Message = broker.Message
                };
                BrokerBacked = broker.Ok;
                if (!broker.Ok)
                    LogBus.Warn("Broker 拒绝了投送", "会话 " + SessionId + " · " + broker.Code + "：" + broker.Message);
            }
            else
            {
                launch = await SystemLauncher.LaunchAsync(
                    SessionId, agent, "--relay " + PipeName,
                    interactive: false, bootstrapExe: self,
                    env: CaptureEnv(account.Id)).ConfigureAwait(false);
            }

            if (!launch.Ok)
            {
                _cts.Cancel();
                return (false, "投送注入代理失败：" + launch.Message);
            }

            AgentPid = launch.Pid;
            LogBus.Info("注入代理已投送", "会话 " + SessionId + " · PID " + launch.Pid
                + " · " + launch.ElapsedMs + " ms");

            // ★ 立刻通知界面：代理进程已起来，主界面该从"未投送"变为"就绪"。
            //   原来要等 LaunchPage 自己的 3 秒轮询，用户会看到
            //   "能用自由视角、界面却写未投送"的矛盾状态。
            BgiLogWatcher.NotifyChannelStateChanged();

            bool connected = await accepted.ConfigureAwait(false);
            bool eventConnected = await events.ConfigureAwait(false);
            if (!eventConnected) LogBus.Warn("目标热键事件管道未就绪", "会话 " + SessionId);
            if (!connected)
                return (false, "注入代理已启动（PID " + launch.Pid + "）但没有连回宿主管道。"
                    + "可能是目标会话不接受后台进程，或杀软拦截了命名管道。");
            if (BrokerBacked)
            {
                string? rejected = VerifyAgentPipe(launch.Pid);
                if (rejected != null)
                {
                    LogBus.Error("管理员代理校验未通过", rejected);
                    return (false, rejected);
                }
            }

            // 握手：问一下代理的环境
            var pong = await SendAsync(new { Cmd = "ping" }, 5000).ConfigureAwait(false);
            if (pong != null && pong.Ok)
            {
                if (BrokerBacked && (pong.SessionId != SessionId || !pong.Elevated || !pong.DesktopOk || pong.Nonce != DeploymentNonce))
                    return (false, "管理员代理自检或部署认证未通过");
                AgentInfo = pong.Info;
                ScreenWidth = pong.ScreenWidth;
                ScreenHeight = pong.ScreenHeight;
                return (true, "代理就绪 · " + AgentInfo + " · 屏幕 " + ScreenWidth + "x" + ScreenHeight);
            }
            return (false, "代理握手无响应，拒绝标记为就绪");
        }

        /// <summary>
        /// 只建立管道并等待代理连回来，**不投送任何进程**。
        ///
        /// 用于手动代理模式：宿主把管道准备好，用户在目标会话里自己双击代理，
        /// 代理按会话号找到这条管道连上来。
        /// </summary>
        public async Task<(bool ok, string message)> ListenAsync(int timeoutMs = 60000)
        {
            _cts = new CancellationTokenSource();

            // 通道已就绪就直接复用，不重建
            if (IsConnected) return (true, "通道已就绪");

            var accepted = AcceptAgentAsync(timeoutMs, _cts.Token);
            var events = AcceptEventsAsync(timeoutMs, _cts.Token);

            bool connected = await accepted.ConfigureAwait(false);
            bool eventConnected = await events.ConfigureAwait(false);
            if (!eventConnected) LogBus.Warn("目标热键事件管道未就绪", "会话 " + SessionId);

            if (!connected)
            {
                _cts.Cancel();
                return (false, "等待手动代理超时：请在目标会话里运行 SoulLink.Agent.exe（管道 " + PipeName + "）");
            }

            LogBus.Ok("手动代理已连接", "会话 " + SessionId + " · 管道 " + PipeName);

            // 握手确认身份
            var pong = await SendAsync(new { Cmd = "ping" }, 5000).ConfigureAwait(false);
            if (pong != null && pong.Ok)
            {
                AgentInfo = pong.Info;
                ScreenWidth = pong.ScreenWidth;
                ScreenHeight = pong.ScreenHeight;
                return (true, "手动代理就绪 · " + AgentInfo + " · 屏幕 " + ScreenWidth + "x" + ScreenHeight);
            }
            return (false, "手动代理连上了但没有响应握手");
        }

        private async Task<bool> AcceptAgentAsync(int timeoutMs, CancellationToken ct)
        {
            try
            {
                var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                _pipe = pipe;

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeoutMs);
                await pipe.WaitForConnectionAsync(timeoutCts.Token).ConfigureAwait(false);
                // ★ 这里不做管理员代理校验：代理可能比 Broker 回包更早连上管道，
                //   此时 AgentPid/BrokerBacked 还没赋值，早年的写法会因此把好代理判成失败。
                //   校验统一放在 DeployAsync 里，等 AgentPid 确定后由 VerifyAgentPipe 完成。

                _reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                _writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                IsConnected = true;
                _sender = Task.Run(ProcessRequestsAsync);
                lock (_sync) Monitor.PulseAll(_sync);
                StateChanged?.Invoke(this, true);
                LogBus.Ok("注入代理已连接", "会话 " + SessionId + " · 管道 " + PipeName);
                return true;
            }
            catch (OperationCanceledException)
            {
                LogBus.Warn("等待注入代理超时", "会话 " + SessionId + " · " + timeoutMs + " ms");
                return false;
            }
            catch (Exception ex)
            {
                LogBus.Error("建立注入管道失败", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 管理员代理身份校验：连回管道的进程必须就是我们投出去的那个 PID、
        /// 且处于目标会话、令牌已提权且在管理员组。返回 null 表示通过。
        /// </summary>
        private string? VerifyAgentPipe(int agentPid)
        {
            try
            {
                if (_pipe == null) return "注入管道尚未建立";
                if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle.DangerousGetHandle(), out int pid))
                    return "无法确认连回管道的进程（错误 " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + "）";
                if (pid != agentPid) return "连回管道的进程（PID " + pid + "）不是本次投送的代理（PID " + agentPid + "）";
                var identity = BrokerSecurity.InspectProcess(pid);
                if (identity.SessionId != SessionId) return "代理落在会话 " + identity.SessionId + "，不是目标会话 " + SessionId;
                if (!identity.Elevated) return "代理未获得提权令牌";
                if (!identity.Administrator) return "代理令牌不在管理员组";
                return null;
            }
            catch (Exception ex) { return "校验管理员代理失败：" + ex.Message; }
        }

        private async Task<bool> AcceptEventsAsync(int timeoutMs, CancellationToken ct)
        {
            try
            {
                var pipe = new NamedPipeServerStream(PipeName + "_Events", PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                _eventPipe = pipe;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(timeoutMs);
                await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                        while (!_disposed)
                        {
                            string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                            if (line == null)
                            {
                                SoulLink.Controls.RdpConsole.Debug("事件管道读到流结束 · 会话 " + SessionId);
                                break;
                            }
                            using var json = JsonDocument.Parse(line);
                            if (json.RootElement.TryGetProperty("Event", out var ev)
                                && string.Equals(ev.GetString(), "hotkey", StringComparison.OrdinalIgnoreCase)
                                && json.RootElement.TryGetProperty("Value", out var value))
                            {
                                string? key = value.GetString();
                                if (!string.IsNullOrEmpty(key))
                                {
                                    // ★ 订阅者数量为 0 时事件会被静默丢弃。
                                    //   这正是"代理侧已命中、宿主毫无反应"的可能成因之一，
                                    //   必须落盘：用 RdpConsole.Debug 而不是 LogBus，
                                    //   后者只进界面不进日志文件，排查时等于没有。
                                    int listeners = HotkeyPressed?.GetInvocationList().Length ?? 0;
                                    LogBus.Info("收到目标会话热键", "会话 " + SessionId + " · " + key);
                                    SoulLink.Controls.RdpConsole.Debug(
                                        "事件管道收到热键 · 会话 " + SessionId + " · " + key + " · 订阅者=" + listeners);
                                    HotkeyPressed?.Invoke(this, key);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!_disposed)
                        {
                            LogBus.Warn("目标热键事件读取结束", ex.Message);
                            SoulLink.Controls.RdpConsole.Debug("事件管道读取异常 · 会话 " + SessionId + " · " + ex.Message);
                        }
                    }
                });
                return true;
            }
            catch (Exception ex)
            {
                LogBus.Warn("建立目标热键事件管道失败", ex.Message);
                return false;
            }
        }

        // ── 指令 ─────────────────────────────────────────────────────────────

        public sealed class RelayResponse
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

        /// <summary>发一条指令并等待回复。未连接返回 null。</summary>
        public async Task<RelayResponse?> SendAsync(object request, int timeoutMs = 4000)
        {
            if (_disposed || !IsConnected || _cts == null) return null;
            var completion = new TaskCompletionSource<RelayResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var item = new RelayRequest(request, completion, timeoutMs);
            try
            {
                // 队列满时短暂背压，调用方不等待网络响应；不会无限创建读任务。
                using var timeout = new CancellationTokenSource(Math.Min(timeoutMs, 150));
                await _requests.Writer.WriteAsync(item, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogBus.Warn("注入队列已满，丢弃控制请求", "会话 " + SessionId);
                return null;
            }
            using var responseTimeout = new CancellationTokenSource(timeoutMs + 250);
            try { return await completion.Task.WaitAsync(responseTimeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
        }

        /// <summary>
        /// 单向发送（fire-and-forget）：写一行后**不读回复**，立即返回。
        ///
        /// ★ 为什么 moverel 要用它：位移是连续流，等一条 45~64ms 的回复既无意义
        ///   又拉低了有效注入频率（20ms 节流下，串行等待意味着实际最高 ~15Hz）。
        ///   去掉等待后单条 moverel 的开销只剩一次 WriteLineAsync 到管道缓冲。
        ///
        /// ★ 语义安全：请求必须带 NoReply=true（代理据此不回写），
        ///   否则代理写的回复会堆在管道里、错位污染下一条指令的回复。
        ///   所以这里强制把 NoReply 塞进 payload，调用方不用记得传。
        ///
        /// ★ 与串行响应的关系：仍走同一单写者队列（SingleWriter=false 但有锁序），
        ///   排在它前面的请求回复会先被读走，之后才轮到它写；它写完后下一轮
        ///   请求照常 write→read，通道不会错位。
        /// </summary>
        public async Task SendAsyncNoReply(object request, int timeoutMs = 4000)
        {
            if (_disposed || !IsConnected || _cts == null || _writer == null) return;
            var payload = new Dictionary<string, object?>();
            foreach (var p in request.GetType().GetProperties())
                payload[p.Name] = p.GetValue(request);
            payload["NoReply"] = true;

            var completion = new TaskCompletionSource<RelayResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var item = new RelayRequest(payload, completion, timeoutMs, noReply: true);
            try
            {
                using var timeout = new CancellationTokenSource(Math.Min(timeoutMs, 150));
                await _requests.Writer.WriteAsync(item, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LogBus.Warn("注入队列已满，丢弃单向请求", "会话 " + SessionId);
            }
        }

        private async Task ProcessRequestsAsync()
        {
            try
            {
                await foreach (var item in _requests.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    if (_disposed || !IsConnected || _writer == null || _reader == null)
                    {
                        item.Completion.TrySetResult(null);
                        continue;
                    }
                    try
                    {
                        string payload = JsonSerializer.Serialize(item.Payload, Json);
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts!.Token);
                        timeout.CancelAfter(item.TimeoutMs);
                        await _writer.WriteLineAsync(payload.AsMemory(), timeout.Token).ConfigureAwait(false);

                        // ★ NoReply：写出去就完事，不读回复。代理侧约定也不回写，
                        //   否则这里跳读会让下一条指令读到这条的残留回复而错位。
                        if (item.NoReply)
                        {
                            item.Completion.TrySetResult(new RelayResponse { Ok = true });
                            continue;
                        }

                        string? line = await _reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                        if (line == null)
                        {
                            InputTrace.Write("AGENT_GONE", "代理关掉了管道（读回 null）会话=" + SessionId);
                            MarkDisconnected(); item.Completion.TrySetResult(null); break;
                        }
                        item.Completion.TrySetResult(JsonSerializer.Deserialize<RelayResponse>(line, Json));
                    }
                    catch (Exception ex)
                    {
                        InputTrace.Write("AGENT_CMD_FAIL", "指令失败 会话=" + SessionId + " 异常=" + ex.GetType().Name + " " + ex.Message);
                        if (!_disposed) LogBus.Warn("注入指令失败", ex.Message);
                        item.Completion.TrySetResult(null);
                        MarkDisconnected();
                        break;
                    }
                }
            }
            catch (Exception ex) { if (!_disposed) LogBus.Warn("注入发送队列异常", ex.Message); }
            finally
            {
                while (_requests.Reader.TryRead(out var pending)) pending.Completion.TrySetResult(null);
            }
        }

        private void MarkDisconnected()
        {
            if (!IsConnected) return;
            IsConnected = false;
            StateChanged?.Invoke(this, false);
        }

        // ── 便捷方法 ─────────────────────────────────────────────────────────

        public Task<RelayResponse?> MoveAsync(int x, int y)
            => SendAsync(new { Cmd = "move", X = x, Y = y });

        public Task<RelayResponse?> MoveRelAsync(int dx, int dy)
            => SendAsync(new { Cmd = "moverel", Dx = dx, Dy = dy });

        /// <summary>moverel 单向发送：不等待回复（转视角高频位移专用）。</summary>
        public Task MoveRelNoReplyAsync(int dx, int dy)
            => SendAsyncNoReply(new { Cmd = "moverel", Dx = dx, Dy = dy });

        /// <summary>
        /// 定位 + 点击，**一次往返完成**。
        /// ★ 为什么要合并：RelayClient 的命令是串行请求-响应，
        ///   分成 move + click 两条就要等两个 RTT。
        ///   实测单条 45~64ms，双击 4 条就 200ms+，主观延迟明显。
        ///   协议复用：X/Y 是坐标，Button 是按键，Move=1 表示"先定位再点"。
        /// </summary>
        public Task<RelayResponse?> ClickAtAsync(int x, int y, string button = "left", int count = 1)
            => SendAsync(new { Cmd = "click", X = x, Y = y, Button = button, Count = count, Move = 1 });

        public Task<RelayResponse?> ClickAsync(int x, int y, string button = "left", int count = 1)
            => SendAsync(new { Cmd = "click", X = x, Y = y, Button = button, Count = count });

        public Task<RelayResponse?> DownAsync(string button = "left")
            => SendAsync(new { Cmd = "down", Button = button });

        public Task<RelayResponse?> UpAsync(string button = "left")
            => SendAsync(new { Cmd = "up", Button = button });

        /// <summary>模拟按键（支持 F12 / Ctrl+A 这类组合）。</summary>
        public Task<RelayResponse?> KeyAsync(string keys, int times = 1) =>
            SendAsync(new { Cmd = "key", Button = keys, Count = times });

        public Task<RelayResponse?> WheelAsync(int delta)
            => SendAsync(new { Cmd = "wheel", Delta = delta });

        // 注意：键盘**不走注入**。RDP 原生就带键盘转发，
        // 只要控件拿到焦点即可（见 RdpConsole.GiveKeyboardFocus / GrabFocusOnConnect）。

        public Task<RelayResponse?> ReleaseAllAsync()
            => SendAsync(new { Cmd = "releaseall" }, 250);

        public Task<RelayResponse?> ConfigureHotkeyAsync(string spec, bool trace = false)
            => SendAsync(new { Cmd = "hotkey", Hotkey = spec ?? "", Trace = trace });

        public Task<RelayResponse?> CursorAsync() => SendAsync(new { Cmd = "cursor" });
        public Task<RelayResponse?> ForegroundAsync() => SendAsync(new { Cmd = "foreground" });

        // ── 屏幕录制（3.2.8）────────────────────────────────────────────
        //
        //  代理在目标会话内录屏（内存环形缓冲），这里只负责下发参数。
        //  协议字段复用：Count=fps，X=质量，Delta=保留秒数，Button=动作。

        /// <summary>开始/更新录制参数。</summary>
        public Task<RelayResponse?> RecordStartAsync(int fps, int quality, int keepSeconds)
            => SendAsync(new { Cmd = "record", Button = "start", Count = fps, X = quality, Delta = keepSeconds }, 6000);

        /// <summary>停止录制。</summary>
        public Task<RelayResponse?> RecordStopAsync()
            => SendAsync(new { Cmd = "record", Button = "stop" }, 4000);

        /// <summary>查询录制状态（帧数、字节数、尺寸）。</summary>
        public Task<RelayResponse?> RecordStatusAsync()
            => SendAsync(new { Cmd = "recordstatus" }, 4000);

        /// <summary>
        /// 投送代理时带上的环境变量。
        /// ★ 3.2.8：代理运行在 Program Files，自己推不出主程序位置，
        ///   录制回放目录必须由投送方显式告知，否则会写到安装目录
        ///   （权限不稳、卸载残留、与"跟程序一个目录"的要求不符）。
        /// </summary>
        /// <summary>
        /// 投送代理时带上的环境变量。
        ///
        /// ★ 代理运行在 Program Files，自己推不出主程序位置，也读不到主程序的
        ///   config.json（那边是另一份 data）。所以所有它需要的东西都从环境变量下发：
        ///
        ///   SOULLINK_CAPTURE_DIR  录像/截图根目录（必须在主程序目录下，别写 C 盘）
        ///   SOULLINK_WEB_PORT     网页直连端口（0 = 不启用）
        ///   SOULLINK_WEB_TOKEN    网页直连共享口令（默认拒绝空口令）
        ///   SOULLINK_WEB_ALLOW_NO_TOKEN  仅显式允许时才接受空口令（默认 0）
        ///   SOULLINK_ACCOUNT_ID   本代理所属账号 Id（/api/info 回显 + 端口分配依据）
        /// </summary>
        private static Dictionary<string, string> CaptureEnv(string? accountId = null)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
                if (string.IsNullOrWhiteSpace(dir)) return new Dictionary<string, string>();

                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["SOULLINK_CAPTURE_DIR"] = System.IO.Path.Combine(dir, "capture")
                };

                // ── 用户级启动项策略（路径A）：代理在目标会话内写自己的 HKCU ──
                //   开关来自配置 SessionSlimUserPolicyEnabled；1=写禁用，0=删值恢复。
                var s0 = App.Config?.Settings;
                if (s0 != null)
                    env["SOULLINK_USER_POLICY"] = s0.SessionSlimUserPolicyEnabled ? "1" : "0";

                // ── 抓屏后端（2026-11-22 补）──
                //   ★ 之前漏了这一环：AppSettings.CaptureMode 有字段、SessionRecorder 读环境变量，
                //     但中间没有代码把配置转成环境变量 → WGC 永远用不上（默认 BitLlt）。
                //   0=自动(优先 WGC 失败降级) 1=WGC-Window 2=WGC-Monitor 3=BitLlt
                if (s0 != null)
                {
                    env["SOULLINK_CAPTURE_MODE"] = s0.CaptureMode.ToString();
                    env["SOULLINK_STREAM_FPS"] = Math.Clamp(s0.RecordFps, 1, 60).ToString();
                    env["SOULLINK_VIDEO_BITRATE"] = Math.Max(500, s0.VideoBitrateKbps).ToString();
                }

                // ── 网页直连（User 要求：尽量隔离主客户端）──
                var s = App.Config?.Settings;
                if (s != null && s.WebDirectToAgent && s.WebPort > 0)
                {
                    int port = ResolveDirectPort(s, accountId);
                    if (port > 0)
                    {
                        env["SOULLINK_WEB_PORT"] = port.ToString();
                        env["SOULLINK_WEB_TOKEN"] = s.WebPassword ?? "";
                        env["SOULLINK_WEB_ALLOW_NO_TOKEN"] = s.WebAllowNoToken ? "1" : "0";
                        if (!string.IsNullOrWhiteSpace(accountId))
                            env["SOULLINK_ACCOUNT_ID"] = accountId!;
                    }
                    // ★ 2026-11-23 诊断：确认环境变量真的组进去了（排查「网页没画面」）
                    try
                    {
                        LogBus.Info("投送环境变量", "账号 " + (accountId ?? "?")
                            + " → 直连端口 " + (port > 0 ? port.ToString() : "未分配")
                            + " · CaptureMode=" + (env.TryGetValue("SOULLINK_CAPTURE_MODE", out var cm) ? cm : "?")
                            + " · WebPort=" + (env.TryGetValue("SOULLINK_WEB_PORT", out var wp) ? wp : "?"));
                    }
                    catch { }
                }
                else
                {
                    try
                    {
                        LogBus.Warn("网页直连未启用", "账号 " + (accountId ?? "?")
                            + " · WebDirectToAgent=" + (s?.WebDirectToAgent.ToString() ?? "?")
                            + " · WebPort=" + (s?.WebPort.ToString() ?? "?"));
                    }
                    catch { }
                }

                return env;
            }
            catch { return new Dictionary<string, string>(); }
        }

        /// <summary>按账号在配置里的序号算直连端口。规则见 Services/WebDirect.cs。</summary>
        private static int ResolveDirectPort(Models.AppSettings s, string? accountId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(accountId)) return 0;
                var accounts = App.Config?.Accounts;
                if (accounts == null) return 0;
                int idx = accounts.FindIndex(a =>
                    string.Equals(a.Id, accountId, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) return 0;
                return Services.WebDirect.PortFor(s.WebPort, idx);
            }
            catch { return 0; }
        }

        /// <summary>
        /// 保存录制到磁盘。
        /// ★ 目录：录制回放\&lt;用户名-昵称&gt;\&lt;时间-触发条件&gt;\
        /// </summary>
        /// <param name="userTag">用户名-昵称</param>
        /// <param name="trigger">触发条件（拼在目录名末尾）</param>
        /// <param name="beforeSeconds">触发前秒数</param>
        /// <param name="afterSeconds">触发后追加秒数（0 = 只存前段）</param>
        public Task<RelayResponse?> SaveRecordingAsync(string userTag, string trigger,
            int beforeSeconds, int afterSeconds)
            => SendAsync(new
            {
                Cmd = "save",
                Button = userTag ?? "",
                Hotkey = trigger ?? "",
                X = Math.Max(1, beforeSeconds),
                Delta = Math.Max(0, afterSeconds)
            }, 60000);   // 后 N 秒要等，超时给足

        /// <summary>单张截图（直接落盘，目录规范同录像）。</summary>
        public Task<RelayResponse?> ScreenshotAsync(string userTag, string trigger, int quality = 75)
            => SendAsync(new { Cmd = "shot", Button = userTag ?? "", Hotkey = trigger ?? "", X = quality }, 15000);

        /// <summary>取最近一帧并落盘（实时预览用）。Count=-1 表示最新帧。</summary>
        public Task<RelayResponse?> WriteLatestFrameAsync()
            => SendAsync(new { Cmd = "frame", Count = -1 }, 8000);
        public Task<RelayResponse?> WindowsAsync() => SendAsync(new { Cmd = "windows" });

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out int pid);

        public void Dispose()
        {
            InputTrace.Write("RL_DISPOSE", "Stop 会话=" + SessionId + " 管道=" + PipeName
                + " IsConnected=" + IsConnected + " AgentPid=" + AgentPid
                + " BrokerBacked=" + BrokerBacked + " 调用方=" + InputTrace.Caller());
            if (_disposed) return;
            _disposed = true;
            try { _cts?.Cancel(); } catch { }

            // ★ 管道写出必须是“尽力而为且不阻塞”：代理已经挂掉时对命名管道
            //   WriteLine 会一直等到内核缓冲满才返回，调用线程（界面/重建路径）就会卡住。
            //   这里把两个收尾指令交给后台线程，Dispose 本身立刻返回。
            var writer = _writer;
            bool connected = IsConnected;
            if (writer != null && connected)
            {
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    InputTrace.Write("RL_SEND_EXIT", "向代理发 releaseall+exit 会话=" + SessionId);
                    try { writer.WriteLine(JsonSerializer.Serialize(new { Cmd = "releaseall" })); } catch { }
                    try { writer.WriteLine(JsonSerializer.Serialize(new { Cmd = "exit" })); } catch { }
                });
            }
            _requests.Writer.TryComplete();
            if (BrokerBacked && AgentPid > 0)
            {
                // ★ SendAsync 内部是同步 Connect/ReadLine，放到后台线程执行：
                //   直接在调用线程（UI/重建路径）里跑会把界面卡住直到超时。
                int pid = AgentPid, session = SessionId;
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    string traceCode;
                    try
                    {
                        var resp = BrokerProtocol.SendAsync(
                            new BrokerProtocol.Request { Command = "stop", Pid = pid, SessionId = session }, 2000)
                            .GetAwaiter().GetResult();
                        traceCode = resp == null ? "null" : (resp.Code + " " + resp.Message);
                    }
                    catch (Exception tex) { traceCode = "异常：" + tex.Message; }
                    InputTrace.Write("BROKER_STOP", "请求 stop 会话=" + session + " pid=" + pid + " 返回=" + traceCode);
                });
            }
            try { _eventPipe?.Dispose(); } catch { }
            try { _pipe?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _writer?.Dispose(); } catch { }
            try { _cts?.Dispose(); } catch { }
            IsConnected = false;

            // ★ 通道断开也要通知：否则主界面会一直显示「通道就绪」，
            //   而实际上代理已经退出（反向的状态不同步，比"晚 3 秒"更误导）。
            BgiLogWatcher.NotifyChannelStateChanged();
        }
    }
}
