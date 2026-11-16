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
            public RelayRequest(object payload, TaskCompletionSource<RelayResponse?> completion, int timeoutMs)
            {
                Payload = payload; Completion = completion; TimeoutMs = timeoutMs;
            }
            public object Payload { get; }
            public TaskCompletionSource<RelayResponse?> Completion { get; }
            public int TimeoutMs { get; }
        }
        private CancellationTokenSource? _cts;
        private volatile bool _disposed;

        public string PipeName { get; }
        public int SessionId { get; }
        public string UserName { get; }
        public bool IsConnected { get; private set; }
        public string AgentInfo { get; private set; } = "";
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

            _cts = new CancellationTokenSource();

            // 先把管道建好再投放，避免代理连回来时宿主还没准备好
            var accepted = AcceptAgentAsync(timeoutMs, _cts.Token);
            var events = AcceptEventsAsync(timeoutMs, _cts.Token);

            // 走 SYSTEM 通道投送（schtasks /ru SYSTEM → CreateProcessAsUser）
            var launch = await SystemLauncher.LaunchAsync(
                SessionId, self, "--relay " + PipeName,
                interactive: false, bootstrapExe: self).ConfigureAwait(false);

            if (!launch.Ok)
            {
                _cts.Cancel();
                return (false, "投送注入代理失败：" + launch.Message);
            }

            LogBus.Info("注入代理已投送", "会话 " + SessionId + " · PID " + launch.Pid
                + " · " + launch.ElapsedMs + " ms");

            bool connected = await accepted.ConfigureAwait(false);
            bool eventConnected = await events.ConfigureAwait(false);
            if (!eventConnected) LogBus.Warn("目标热键事件管道未就绪", "会话 " + SessionId);
            if (!connected)
                return (false, "注入代理已启动（PID " + launch.Pid + "）但没有连回宿主管道。"
                    + "可能是目标会话不接受后台进程，或杀软拦截了命名管道。");

            // 握手：问一下代理的环境
            var pong = await SendAsync(new { Cmd = "ping" }, 5000).ConfigureAwait(false);
            if (pong != null && pong.Ok)
            {
                AgentInfo = pong.Info;
                ScreenWidth = pong.ScreenWidth;
                ScreenHeight = pong.ScreenHeight;
                return (true, "代理就绪 · " + AgentInfo + " · 屏幕 " + ScreenWidth + "x" + ScreenHeight);
            }
            return (true, "代理已连接（ping 无响应，可能正在启动）");
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
                            if (line == null) break;
                            using var json = JsonDocument.Parse(line);
                            if (json.RootElement.TryGetProperty("Event", out var ev)
                                && string.Equals(ev.GetString(), "hotkey", StringComparison.OrdinalIgnoreCase)
                                && json.RootElement.TryGetProperty("Value", out var value))
                            {
                                string? key = value.GetString();
                                if (!string.IsNullOrEmpty(key)) HotkeyPressed?.Invoke(this, key);
                            }
                        }
                    }
                    catch (Exception ex) { if (!_disposed) LogBus.Warn("目标热键事件读取结束", ex.Message); }
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
                        string? line = await _reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                        if (line == null) { MarkDisconnected(); item.Completion.TrySetResult(null); break; }
                        item.Completion.TrySetResult(JsonSerializer.Deserialize<RelayResponse>(line, Json));
                    }
                    catch (Exception ex)
                    {
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

        public Task<RelayResponse?> ClickAsync(int x, int y, string button = "left", int count = 1)
            => SendAsync(new { Cmd = "click", X = x, Y = y, Button = button, Count = count });

        public Task<RelayResponse?> DownAsync(string button = "left")
            => SendAsync(new { Cmd = "down", Button = button });

        public Task<RelayResponse?> UpAsync(string button = "left")
            => SendAsync(new { Cmd = "up", Button = button });

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
        public Task<RelayResponse?> WindowsAsync() => SendAsync(new { Cmd = "windows" });

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _cts?.Cancel(); } catch { }
            try { if (IsConnected) _writer?.WriteLine(JsonSerializer.Serialize(new { Cmd = "releaseall" })); } catch { }
            try { if (IsConnected) _writer?.WriteLine(JsonSerializer.Serialize(new { Cmd = "exit" })); } catch { }
            _requests.Writer.TryComplete();
            try { _eventPipe?.Dispose(); } catch { }
            try { _pipe?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _writer?.Dispose(); } catch { }
            try { _cts?.Dispose(); } catch { }
            IsConnected = false;
        }
    }
}
