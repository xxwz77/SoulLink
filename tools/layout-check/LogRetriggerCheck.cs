using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using SoulLink.Models;
using SoulLink.Services;

/// <summary>
/// 复现「无 END + 日志触点 只触发一次」的最小测试。
/// 直接编译生产源码 PlcScanEngine + PlcLogMatcher，不做任何复刻。
/// read 委托真实走 PlcLogMatcher（与 FlowRunner.ReadContactAsync 的日志分支同一逻辑）。
/// </summary>
public static class LogRetriggerCheck
{
    static int fails;
    static void Check(bool ok, string text){ Console.WriteLine((ok?"ok   ":"FAIL ")+text); if(!ok) fails++; }

    static string LogLine(DateTime at, string level, string msg)
        => "[" + at.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "] [" + level + "] " + msg;

    public static async Task<int> Main()
    {
        // 无 END + TrigLogMatch（withinSeconds=10）→ 动作
        var flow = new AutomationFlow();
        var trig = new FlowNode { Id="log", Kind=FlowNodeKind.Condition, Type=FlowNodeType.TrigLogMatch };
        trig.Set("pattern", "任务结束");
        trig.Set("isRegex", "false");
        trig.Set("withinSeconds", "10");
        var act = new FlowNode { Id="act", Kind=FlowNodeKind.Action, Type=FlowNodeType.ActRunAction };
        flow.Nodes.AddRange(new[]{ trig, act });
        flow.Edges.Add(new FlowEdge { From="log", To="act" });
        flow.EnsurePlcModel();

        string recentLog = "";
        var now = new DateTime(2026,11,18,12,0,0);
        int ran = 0;

        Task<FlowRunner.RunResult> Scan(DateTime at) =>
            PlcScanEngine.ScanAsync(flow,
                n => {
                    string text = recentLog;
                    int within = Math.Max(1, n.GetInt("withinSeconds", 10));
                    if (text.Length==0 || !PlcLogMatcher.ValidPattern(n.Get("pattern"), n.GetBool("isRegex", false)))
                        return Task.FromResult(new PlcSignal(false, false));
                    bool hit = PlcLogMatcher.Contains(text, n.Get("pattern"), n.GetBool("isRegex", false), n.Get("level"), within, at);
                    return Task.FromResult(new PlcSignal(hit, true));
                },
                n => { ran++; return Task.FromResult(new FlowActionResult{ Ok=true, Message="ok", At=at }); },
                now: at);

        // 场景1：日志行 1 秒前出现 → 命中并执行一次
        recentLog = LogLine(now.AddSeconds(-1), "INF", "任务结束") + "\n";
        await Scan(now);
        Check(ran==1 && !flow.IsDailyComplete, "第1段日志命中，动作执行1次，无END不写日界锁存");

        // 场景2：同一行仍在 10 秒窗口内（电平持续命中）→ 无END每轮收尾后应再次触发
        await Scan(now.AddSeconds(3));
        Check(ran==2, "同一行日志窗口内仍命中 → 无END循环可再次触发（排除引擎锁存卡死）");

        // 场景3：窗口过期（>10秒）→ 同一行不再命中
        await Scan(now.AddSeconds(20));
        Check(ran==2, "窗口过期后同一行日志不再命中（不触发）——这正是『只触发一次』的数据层原因");

        // 场景4：出现一条**新**日志行 → 再次命中触发
        var later = now.AddSeconds(20);
        recentLog = LogLine(later, "INF", "任务结束") + "\n";
        await Scan(later.AddSeconds(1));
        Check(ran==3, "新日志行出现 → 再次触发（证明不是永久卡死，只是旧行过期）");

        // 场景5：模拟「动作执行后 PlcPower 残留」担忧 —— 无END收尾已清空，确认状态干净
        Check(!flow.PlcPower.ContainsKey("act") && !flow.ActionResults.ContainsKey("act")
              && !flow.SatisfiedContacts.Contains("log") && !flow.RunComplete,
              "无END完成一轮后 PlcPower/ActionResults/SatisfiedContacts/RunComplete 均已清空");

        Console.WriteLine(fails==0 ? "ALL PASSED" : "FAILURES="+fails);
        return fails;
    }
}
