using System;
using System.Diagnostics;
using System.Linq;
using SoulLink;
using SoulLink.Services;

/// <summary>
/// 通道识别回归：界面"通道就绪/未投送"依赖 DiscoverLiveAgentSessions，
/// 它必须认得 Broker 通道的代理进程名（SoulLink.Agent），否则投送成功也显示未投送。
/// 用法：ChannelDetectCheck.exe [--deploy]
///   --deploy 会真的投送一次再回收，用于端到端确认。
/// </summary>
public static class ChannelDetectCheck
{
    private static int _fails;
    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
        if (!ok) _fails++;
    }

    public static int Main(string[] args)
    {
        bool deploy = args.Any(a => a.Equals("--deploy", StringComparison.OrdinalIgnoreCase));

        // 1) Broker 代理的进程名必须被识别为"本版本代理"
        string agentName = BrokerInstall.AgentProcessName;
        Console.WriteLine("BrokerInstall.AgentProcessName = " + agentName);
        Check(agentName == "SoulLink.Agent", "Broker 代理进程名为 SoulLink.Agent");

        // 2) 关键回归：DiscoverLiveAgentSessions 必须把 SoulLink.Agent 算进去。
        //    以前只认 SoulLink / SoulLink.proxy，Broker 通道永远显示"未投送"。
        var source = System.IO.File.ReadAllText(System.IO.Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "Services", "BgiLogWatcher.cs"));
        Check(source.Contains("AgentProcessName"), "通道识别引用了 Broker 代理进程名");

        // 3) 现网状态：若已有代理在跑，DiscoverLiveAgentSessions 应能报出来。
        var live = BgiLogWatcher.DiscoverLiveAgentSessions();
        Console.WriteLine("当前 live 通道会话：" + (live.Count == 0 ? "（无）" : string.Join(",", live.OrderBy(x => x))));

        if (!deploy)
        {
            Console.WriteLine("提示：加 --deploy 做端到端确认。");
            return _fails;
        }

        // 4) 端到端：投送 → 通道必须被识别 → 回收
        var sessions = SessionService.QueryAsync().GetAwaiter().GetResult();
        foreach (var user in new[] { "A2", "A3" })
        {
            var s = SessionService.Find(sessions, user);
            if (s == null || s.Id <= 0) { Console.WriteLine("SKIP " + user + "：无会话"); continue; }
            string? offline = SystemLauncher.DescribeSessionOnline(s.Id, user);
            if (offline != null) { Console.WriteLine("SKIP " + user + "：不在线（" + offline + "）"); continue; }

            Console.WriteLine();
            Console.WriteLine("=== " + user + " 会话 " + s.Id + " ===");
            var prev = App.Config.Settings.UseBrokerChannel;
            App.Config.Settings.UseBrokerChannel = true;
            var relay = new RelayClient(user, s.Id);
            try
            {
                var (ok, msg) = relay.DeployAsync(new SoulLink.Models.Account { UserName = user }, 25000)
                    .GetAwaiter().GetResult();
                Console.WriteLine("投送：" + (ok ? "成功" : "失败") + " · " + msg);
                if (!ok) { _fails++; continue; }

                // ★ 这是本次修复的关键断言
                var liveNow = BgiLogWatcher.DiscoverLiveAgentSessions();
                bool seen = liveNow.Contains(s.Id);
                Console.WriteLine("通道识别：live 集合 = {"
                    + string.Join(",", liveNow.OrderBy(x => x)) + "} 包含会话 " + s.Id + " = " + seen);
                Check(seen, user + " 投送后界面能识别到通道（不再显示未投送）");
            }
            catch (Exception ex) { Console.WriteLine("异常：" + ex.Message); _fails++; }
            finally
            {
                relay.Dispose();
                App.Config.Settings.UseBrokerChannel = prev;
                System.Threading.Thread.Sleep(800);
            }
        }
        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "RESULT=OK 全部通过" : "RESULT=FAIL 失败 " + _fails + " 项");
        return _fails;
    }
}
