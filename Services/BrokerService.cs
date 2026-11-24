using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SoulLink.Services
{
    /// <summary>LocalSystem 服务：仅部署测试版管理员代理，不执行任何输入。</summary>
    public static class BrokerService
    {
        internal static void Trace(string stage, string detail = "")
        {
            try
            {
                Directory.CreateDirectory(BrokerInstall.Root);
                File.AppendAllText(Path.Combine(BrokerInstall.Root, "broker.log"), DateTime.Now.ToString("O") + " " + stage + " " + detail + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }

        public static async Task RunAsync(CancellationToken ct)
        {
            Trace("START", "broker entry");
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var self = new BrokerSecurity.ProcessIdentity { Sid = identity.User?.Value ?? "", SessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId };
                if (self.SessionId != 0 || self.Sid != "S-1-5-18")
                    throw new InvalidOperationException("Broker 只能以会话 0 的 LocalSystem 运行");
                Trace("IDENTITY", self.Sid + " session=" + self.SessionId);
            }
            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddWindowsService(o => o.ServiceName = BrokerProtocol.ServiceName);
            builder.Services.AddHostedService<Worker>();
            builder.Logging.ClearProviders();
            var host = builder.Build();
            Trace("SCM_READY", "host created");
            await host.RunAsync(ct).ConfigureAwait(false);
            Trace("STOPPED", "host stopped");
        }

        private sealed class Worker : BackgroundService
        {
            private readonly SemaphoreSlim _gate = new(1, 1);
            private readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _agents = new();
            protected override async Task ExecuteAsync(CancellationToken ct)
            {
                Trace("WORKER_READY", "pipe accept loop");
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        using var pipe = NamedPipeServerStreamAcl.Create(BrokerProtocol.PipeName,
                            PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous, 0, 0, BrokerProtocol.AdministratorPipeSecurity());
                        await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                        Trace("CLIENT_ACCEPTED", "pipe connected");
                        // 在当前服务后台任务直接处理，避免额外线程调度吞掉管道生命周期。
                        await ServeAsync(pipe, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { Trace("ACCEPT_ERROR", ex.Message); await Task.Delay(500, ct).ConfigureAwait(false); }
                }
            }

            private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
            {
                var response = new BrokerProtocol.Response();
                try
                {
                    using (pipe)
                    {
                        var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
                        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                        Trace("READ_BEGIN", "waiting request line");
                        string? line = await Task.Run(() => reader.ReadLine(), ct).ConfigureAwait(false);
                        Trace("READ_DONE", "request bytes=" + (line?.Length ?? 0));
                        var request = JsonSerializer.Deserialize<BrokerProtocol.Request>(line ?? "");
                        if (request?.Command != "health")
                        {
                            // ★ 逐项记录认证过程：以前只写一行 AUTH_FAIL，看不出到底是
                            //   OpenProcess / OpenProcessToken / 管理员判定 哪一步失败。
                            Trace("AUTH_BEGIN", "deploy client verification");
                            string? denied = BrokerSecurity.ValidateClient(pipe, allowSystem: true, out int clientPid);
                            Trace("AUTH_CLIENT", BrokerSecurity.DescribeProcess(clientPid));
                            if (denied != null)
                            {
                                Trace("AUTH_FAIL", denied);
                                throw new UnauthorizedAccessException(denied);
                            }
                            Trace("AUTH_OK", "client verified");
                        }
                        if (request == null || request.Version != BrokerProtocol.Version)
                            response = BrokerProtocol.Fail("VERSION", "不支持的 Broker 协议");
                        else if (request.Command == "health")
                        {
                            Trace("HEALTH", "request received");
                            // ★ 服务端也做同一套检查（与客户端共用 MinimumServiceVersion）。
                            //   这样两端对"什么算兼容"不会出现分歧。
                            var selfVersion = BrokerProtocol.SelfAppVersion;
                            var (versionOk, versionMsg) = BrokerProtocol.CheckServiceVersion(selfVersion);

                            response = new BrokerProtocol.Response
                            {
                                Ok = true,          // 服务本身是健康的
                                Code = versionOk ? "READY" : "OUTDATED",
                                Message = versionOk
                                    ? "LocalSystem Broker 已就绪"
                                    : (versionMsg ?? "服务版本过低"),
                                AppVersion = selfVersion
                            };
                            if (!versionOk)
                                Trace("OUTDATED", "service " + selfVersion
                                    + " < min " + BrokerProtocol.MinimumServiceVersion);
                        }
                        else if (request.Command == "stop")
                        {
                            InputTrace.Write("BROKER_STOP_RECV", "收到 stop 请求 会话=" + request.SessionId
                                + " pid=" + request.Pid
                                + " 本服务登记 pid=" + (_agents.TryGetValue(request.SessionId, out int tp) ? tp.ToString() : "无"));
                            if (!_agents.TryGetValue(request.SessionId, out int ownedPid) || ownedPid != request.Pid)
                            { response = BrokerProtocol.Fail("STOP_DENIED", "只能停止本服务登记的代理"); await writer.WriteLineAsync(JsonSerializer.Serialize(response)); return; }
                            var process = System.Diagnostics.Process.GetProcessById(request.Pid);
                            var identity = BrokerSecurity.InspectProcess(request.Pid);
                            if (identity.SessionId != request.SessionId || !identity.Elevated)
                                response = BrokerProtocol.Fail("STOP_DENIED", "只允许停止指定会话的管理员代理");
                            else
                            {
                                InputTrace.Write("BROKER_STOP_KILL", "★ Broker 正在杀死代理 会话=" + request.SessionId + " pid=" + request.Pid);
                                process.Kill();
                                _agents.TryRemove(request.SessionId, out _);
                                response = new BrokerProtocol.Response { Ok = true, Code = "STOPPED" };
                            }
                        }
                        else if (request.Command == "ensure")
                        {
                            await _gate.WaitAsync(ct).ConfigureAwait(false);
                            try { response = Ensure(request); }
                            finally { _gate.Release(); }
                        }
                        else response = BrokerProtocol.Fail("COMMAND", "不支持的 Broker 命令");
                        await writer.WriteLineAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
                        await writer.FlushAsync(ct).ConfigureAwait(false);
                        Trace("RESPONSE_SENT", response.Code);
                    }
                }
                catch (Exception ex)
                {
                    response = BrokerProtocol.Fail(ex is UnauthorizedAccessException ? "CLIENT_DENIED" : "BROKER_ERROR", ex.Message);
                    // ★ t16（T7b）：Broker 是无 UI 服务，只落 broker.log；LogBus 是 WPF（System.Windows.Media）
                    //   在这里本就只会写进 UI 日志总线（服务进程无 UI，等于白写）。改成 Trace 落盘并去掉重复行。
                    Trace("REQUEST_ERROR", response.Code + " " + response.Message);
                }
                // 错误在关闭前尽力返回；不会把异常当作已启动。
                try
                {
                    if (pipe.IsConnected)
                    {
                        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                        await writer.WriteLineAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
                    }
                }
                catch { }
            }

            private BrokerProtocol.Response Ensure(BrokerProtocol.Request request)
            {
                if (request.SessionId <= 0 || string.IsNullOrWhiteSpace(request.UserName))
                    return BrokerProtocol.Fail("SESSION", "无效目标会话或用户");

                // ★ 先确认目标会话确实在线：A2/A3 断开时不要建代理。
                //   离线会话要么 WTSQueryUserToken 失败，要么代理落在没人看的桌面上，
                //   用户看到的就是"投送失败"或"代理在跑但没反应"。
                string? online = SystemLauncher.DescribeSessionOnline(request.SessionId, request.UserName);
                Trace("DEPLOY_SESSION", "session=" + request.SessionId + " user=" + request.UserName
                    + " => " + (online ?? "Active"));
                if (online != null) return BrokerProtocol.Fail("OFFLINE", online);

                bool pipeOk = BrokerProtocol.ValidPipeName(request.PipeName);
                bool nonceOk = request.Nonce?.Length == 32;
                if (!pipeOk || !nonceOk)
                {
                    Trace("DEPLOY_REJECT", "pipe=" + pipeOk + " nonceLength=" + (request.Nonce?.Length ?? 0));
                    return BrokerProtocol.Fail("DEPLOYMENT", "代理管道或 nonce 无效");
                }

                string exe = BrokerInstall.ServiceExe;
                if (!File.Exists(exe) || !string.Equals(SystemLauncher.ResolveSelfExe(), exe, StringComparison.OrdinalIgnoreCase))
                    return BrokerProtocol.Fail("INSTALL_INVALID", "Broker 必须从固定测试版目录运行");
                string agent = BrokerInstall.AgentExe;
                if (!File.Exists(agent) || !BrokerInstall.ValidAgent(exe, agent))
                    return BrokerProtocol.Fail("AGENT_INVALID", "固定目录中的管理员代理缺失或版本不符");

                Trace("DEPLOY_BEGIN", "session=" + request.SessionId + " user=" + request.UserName
                    + " pipe=" + request.PipeName);
                var result = SystemLauncher.LaunchAdminAgentFromBroker(
                    request.SessionId, request.PipeName, request.Nonce, request.Env);
                Trace("DEPLOY_RESULT", "ok=" + result.Ok + " pid=" + result.Pid + " " + result.Message);
                if (!result.Ok) return BrokerProtocol.Fail("CREATE_AGENT", result.Message);

                // ★ 身份校验只认令牌事实，不再比对 "DOMAIN\user" 字符串：
                //   这里跑在 LocalSystem 里，WindowsIdentity/IsInRole 会因缺少 SeTcbPrivilege 抛“拒绝访问”，
                //   以前正是它把已经建好的代理杀掉并报 VERIFY_AGENT。
                BrokerSecurity.ProcessIdentity identity;
                try { identity = BrokerSecurity.InspectProcess(result.Pid); }
                catch (Exception ex)
                {
                    try { System.Diagnostics.Process.GetProcessById(result.Pid).Kill(); } catch { }
                    return BrokerProtocol.Fail("VERIFY_AGENT", "代理身份读取失败：" + ex.Message);
                }
                Trace("DEPLOY_IDENTITY", BrokerSecurity.DescribeProcess(result.Pid));
                string? mismatch = DescribeAgentMismatch(identity, request.SessionId, request.UserName);
                if (mismatch != null)
                {
                    try { System.Diagnostics.Process.GetProcessById(result.Pid).Kill(); } catch { }
                    return BrokerProtocol.Fail("VERIFY_AGENT", mismatch);
                }
                _agents[request.SessionId] = result.Pid;
                return new BrokerProtocol.Response
                {
                    Ok = true, Code = "AGENT_STARTED", Pid = result.Pid,
                    SessionId = identity.SessionId, Message = "管理员代理已启动并完成身份校验"
                };
            }

            /// <summary>
            /// 代理身份校验：会话号、提权、管理员组、目标用户名。
            /// 用户名比对同时接受 "DOMAIN\user" 与纯用户名；取不到名字时只认令牌事实，
            /// 不因为名字解析失败就杀掉一个已经通过 SID/会话/提权校验的代理。
            /// </summary>
            private static string? DescribeAgentMismatch(BrokerSecurity.ProcessIdentity identity, int sessionId, string userName)
            {
                if (identity.SessionId != sessionId)
                    return "代理落点会话 " + identity.SessionId + "，不是目标会话 " + sessionId;
                if (!identity.Elevated) return "代理未获得提权令牌";
                if (!identity.Administrator) return "代理令牌不在管理员组";
                string expected = (userName ?? "").Trim();
                if (expected.Length == 0) return null;
                string actual = identity.User ?? "";
                if (actual.Length == 0 || actual == identity.Sid) return null;   // 名字解析不可用，令牌事实已通过
                string bare = actual.Contains('\\') ? actual.Substring(actual.LastIndexOf('\\') + 1) : actual;
                if (string.Equals(bare, expected, StringComparison.OrdinalIgnoreCase)) return null;
                if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) return null;
                return "代理用户 " + actual + "，不是目标用户 " + expected;
            }
        }
    }
}