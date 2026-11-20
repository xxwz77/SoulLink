using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using SoulLink;
using SoulLink.Models;
using SoulLink.Services;

/// <summary>
/// 端到端投送验证：走真实 Broker 通道把管理员代理投进指定账号的会话。
/// 只做投送 + ping 自检 + 立即回收；不碰 RDP 连接、不注销用户、不重启任何客户端。
/// 用法：BrokerDeployCheck.exe A2 [A3]
/// </summary>
public static class BrokerDeployCheck
{
    public static async Task<int> Main(string[] args)
    {
        // 命令行里的 --identity=... 是给 AppIdentity 的，不当作账号名。
        string[] users = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (users.Length == 0) users = new[] { "A2", "A3" };
        Console.WriteLine("runtime: " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        var sessions = await SessionService.QueryAsync();
        Console.WriteLine("sessions: " + string.Join(" | ", sessions.Select(s => s.UserName + "#" + s.Id + " " + s.State)));
        AdminAgentCheck.Run();
        int fails = 0;
        foreach (var user in users)
        {
            var session = SessionService.Find(sessions, user);
            if (session == null) { Console.WriteLine("FAIL " + user + "：没有会话"); fails++; continue; }
            Console.WriteLine();
            Console.WriteLine("=== " + user + " 会话 " + session.Id + " 状态 " + session.State + " ===");
            string? offline = SystemLauncher.DescribeSessionOnline(session.Id, user);
            Console.WriteLine("在线判定：" + (offline ?? "在线，可以投送"));
            if (offline != null) { Console.WriteLine("SKIP " + user + "：会话不在线，按设计要求不投送"); continue; }
            if (!AdminAgentCheck.HasSystemBroker())
            {
                Console.WriteLine("FAIL " + user + "：Broker 服务未就绪");
                fails++;
                continue;
            }
            var previous = App.Config.Settings.UseBrokerChannel;
            App.Config.Settings.UseBrokerChannel = true;
            var relay = new RelayClient(user, session.Id);
            try
            {
                var (ok, message) = await relay.DeployAsync(new Account { UserName = user, DisplayName = user }, 25000);
                Console.WriteLine("投送结果：" + (ok ? "成功" : "失败") + " · " + message);
                Console.WriteLine("通道连接：" + relay.IsConnected + " · 代理 PID " + relay.AgentPid
                                  + " · Broker 通道 " + relay.BrokerBacked);
                if (!ok) fails++;
                else
                {
                    var pong = await relay.SendAsync(new { Cmd = "ping" }, 5000);
                    Console.WriteLine("代理自检：ok=" + (pong?.Ok) + " session=" + pong?.SessionId
                                      + " elevated=" + pong?.Elevated + " desktopOk=" + pong?.DesktopOk
                                      + " nonce匹配=" + (pong?.Nonce == relay.DeploymentNonce)
                                      + " info=" + pong?.Info);
                    if (pong == null || !pong.Ok) fails++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL " + user + "：异常 " + ex);
                fails++;
            }
            finally
            {
                relay.Dispose();
                App.Config.Settings.UseBrokerChannel = previous;
                await Task.Delay(600);
                Console.WriteLine("代理已回收（Dispose 走 stop 命令，不动其他进程）");
            }
        }
        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "RESULT=OK 全部通过" : "RESULT=FAIL 失败 " + fails + " 项");
        return fails;
    }

    private static class AdminAgentCheck
    {
        public static bool HasSystemBroker()
        {
            try
            {
                var services = Process.GetProcessesByName("SoulLink")
                    .Where(p => { try { return p.SessionId == 0; } catch { return false; } });
                return services.Any();
            }
            catch { return false; }
        }
        public static void Run()
        {
            var health = BrokerProtocol.SendAsync(new BrokerProtocol.Request { Command = "health" }, 2500)
                .GetAwaiter().GetResult();
            Console.WriteLine("Broker 健康：" + health.Code + " · " + health.Message);
            if (!health.Ok) Console.WriteLine("警告：Broker 未就绪，投送会失败");
        }
    }
}