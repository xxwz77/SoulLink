using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using SoulLink.Models;
using SoulLink.Services;

/// <summary>
/// 「事件锁存 + 消费」语义的最小复现/回归测试。
/// 直接编译生产源码 PlcScanEngine + LogLatch + PlcLogMatcher + FlowRunLifecycle，
/// 不做任何复刻。read 委托对 TrigLogMatch 走 LogLatch.Scan（与 FlowRunner.ReadContactAsync
/// 的日志分支同一逻辑），对 CondLogMissing 走 PlcLogMatcher.Contains 的 !hit（电平语义）。
/// </summary>
public static class LogLatchCheck
{
    static int fails;
    static void Check(bool ok, string text) { Console.WriteLine((ok ? "ok   " : "FAIL ") + text); if (!ok) fails++; }

    static string LogLine(DateTime at, string level, string msg)
        => "[" + at.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "] [" + level + "] " + msg;

    // 合成日志流：递增行序号。
    sealed class LogStream
    {
        public readonly List<LogLineRef> Tail = new();
        long _next = 1;
        public long Seq { get; private set; }
        public void Append(string text, string level = "INF")
        {
            var line = new LogLineRef(_next++, level, text);
            Tail.Add(line);
            Seq = _next - 1;
        }
        public LogLineRef[] View() => Tail.ToArray();
    }

    static AutomationFlow Flow(FlowNodeType contactType, bool withEnd)
    {
        var flow = new AutomationFlow();
        var trig = new FlowNode { Id = "log", Kind = FlowNodeKind.Condition, Type = contactType };
        trig.Set("pattern", "任务结束");
        trig.Set("isRegex", "false");
        trig.Set("withinSeconds", "10");
        var act = new FlowNode { Id = "act", Kind = FlowNodeKind.Action, Type = FlowNodeType.ActRunAction };
        flow.Nodes.Add(trig);
        flow.Nodes.Add(act);
        flow.Edges.Add(new FlowEdge { From = "log", To = "act" });
        if (withEnd)
        {
            var end = new FlowNode { Id = "end", Kind = FlowNodeKind.Terminate, Type = FlowNodeType.ActEndFlow, DayBoundaryHour = 4 };
            flow.Nodes.Add(end);
            flow.Edges.Add(new FlowEdge { From = "act", To = "end" });
        }
        flow.EnsurePlcModel();
        return flow;
    }

    // 触发 + 一个 AND 门控条件 → 动作（用于「流程在等待别的步骤」场景）。
    static AutomationFlow FlowWithGate()
    {
        var flow = new AutomationFlow();
        var trig = new FlowNode { Id = "log", Kind = FlowNodeKind.Condition, Type = FlowNodeType.TrigLogMatch };
        trig.Set("pattern", "任务结束"); trig.Set("isRegex", "false"); trig.Set("withinSeconds", "10");
        var gate = new FlowNode { Id = "gate", Kind = FlowNodeKind.Condition, Type = FlowNodeType.CondTimeRange, Logic = FlowLogicOp.AND };
        var act = new FlowNode { Id = "act", Kind = FlowNodeKind.Action, Type = FlowNodeType.ActRunAction };
        flow.Nodes.Add(trig); flow.Nodes.Add(gate); flow.Nodes.Add(act);
        flow.Edges.Add(new FlowEdge { From = "log", To = "gate" });
        flow.Edges.Add(new FlowEdge { From = "gate", To = "act" });
        flow.EnsurePlcModel();
        return flow;
    }

    public static async Task<int> Main()
    {
        var now = new DateTime(2026, 11, 18, 12, 0, 0);

        // ── 用例 1 + 4：日志出现一次 → 执行一次；同一条日志出现两次 → 执行两次 ──
        {
            var flow = Flow(FlowNodeType.TrigLogMatch, withEnd: false);
            var log = new LogStream();
            int ran = 0;
            DateTime cur = now;
            Task<FlowRunner.RunResult> Scan() => PlcScanEngine.ScanAsync(flow,
                n => Task.FromResult<PlcSignal>(LogLatch.Scan(flow, n, log.View(), log.Seq, cur, mutable: true)),
                n => { ran++; return Task.FromResult(new FlowActionResult { Ok = true, Message = "ok", At = cur }); },
                now: cur);

            // 先空扫一轮，建立游标（= 不补历史）。
            await Scan();
            Check(ran == 0, "用例1/4：空尾窗建立游标，动作不执行");

            log.Append(LogLine(cur, "INF", "任务结束"));
            await Scan();
            Check(ran == 1, "用例1：日志出现一次 → 动作执行一次");

            await Scan();
            await Scan();
            Check(ran == 1, "用例3：动作执行后再扫（无新日志）→ 不重复执行（消费确认）");

            log.Append(LogLine(cur.AddSeconds(5), "INF", "任务结束"));
            await Scan();
            Check(ran == 2, "用例4：同一条日志再出现一次 → 再次执行（两次）");
        }

        // ── 用例 2：日志出现一次，中间隔了很多轮（等别的条件）→ 仍然记得，最终执行 ──
        {
            var flow = FlowWithGate();
            var log = new LogStream();
            int ran = 0;
            DateTime cur = now;
            bool gate = false;
            Task<FlowRunner.RunResult> Scan() => PlcScanEngine.ScanAsync(flow,
                n => Task.FromResult<PlcSignal>(n.Id == "gate" ? gate : LogLatch.Scan(flow, n, log.View(), log.Seq, cur, mutable: true)),
                n => { ran++; return Task.FromResult(new FlowActionResult { Ok = true, Message = "ok", At = cur }); },
                now: cur);

            await Scan();                       // 空扫建游标
            log.Append(LogLine(cur, "INF", "任务结束"));
            await Scan();                       // 日志命中锁存，但 gate=false → 不执行
            Check(ran == 0 && flow.LogTriggers.TryGetValue("log", out var st) && st.Events.Count > 0,
                "用例2：日志命中后事件被锁存（等别的条件）");

            // 中间隔很多轮，gate 一直 false —— 事件不丢。
            for (int i = 0; i < 50; i++) { cur = cur.AddSeconds(1); await Scan(); }
            Check(ran == 0, "用例2：隔 50 轮扫描（gate 仍 false）→ 事件仍被记住、不丢");

            gate = true;
            await Scan();
            Check(ran == 1, "用例2：其它条件凑齐 → 动作最终执行（旧实现做不到：时间窗早过期）");

            await Scan();
            Check(ran == 1, "用例3（连带）：消费后不再重复执行");
        }

        // ── 用例 5：有 END 的流程当天完成后 → 新日志不再触发 ──
        {
            var flow = Flow(FlowNodeType.TrigLogMatch, withEnd: true);
            var log = new LogStream();
            int ran = 0;
            DateTime cur = now;
            Task<FlowRunner.RunResult> Scan() => PlcScanEngine.ScanAsync(flow,
                n => Task.FromResult<PlcSignal>(LogLatch.Scan(flow, n, log.View(), log.Seq, cur, mutable: true)),
                n => { ran++; return Task.FromResult(new FlowActionResult { Ok = true, Message = "ok", At = cur }); },
                now: cur);

            await Scan();                       // 空扫建游标
            log.Append(LogLine(cur, "INF", "任务结束"));
            await Scan();                       // 命中 → 执行 → 收尾写 END 日界锁存
            Check(ran == 1 && flow.DailyLatchKey == "2026-11-18", "用例5：有 END 命中执行一次并写日界锁存");

            log.Append(LogLine(cur.AddSeconds(5), "INF", "任务结束"));
            await Scan();                       // 新日志到达，但当日已完成 → 不再判断
            Check(ran == 1, "用例5：有 END 当天完成后，新日志不再触发（日界锁存未被破坏）");
        }

        // ── 用例 6：CondLogMissing 仍保持电平语义（不被事件化破坏）──
        {
            var flow = Flow(FlowNodeType.CondLogMissing, withEnd: false);
            int ran = 0;
            DateTime cur = now;
            string recentLog = "";
            Task<FlowRunner.RunResult> Scan() => PlcScanEngine.ScanAsync(flow,
                n =>
                {
                    // 与 FlowRunner.ReadContactAsync 的 CondLogMissing 分支一致：电平查询。
                    int within = Math.Max(1, n.GetInt("withinSeconds", 120));
                    if (recentLog.Length == 0 || !PlcLogMatcher.ValidPattern(n.Get("pattern"), n.GetBool("isRegex", false)))
                        return Task.FromResult(new PlcSignal(false, false));
                    bool hit = PlcLogMatcher.Contains(recentLog, n.Get("pattern"), n.GetBool("isRegex", false), n.Get("level"), within, cur);
                    return Task.FromResult(new PlcSignal(!hit, true));
                },
                n => { ran++; return Task.FromResult(new FlowActionResult { Ok = true, Message = "ok", At = cur }); },
                now: cur);

            recentLog = LogLine(cur.AddSeconds(-1), "INF", "任务结束") + "\n";
            await Scan();
            Check(ran == 0, "用例6：CondLogMissing 窗口内出现 → 不满足（保持电平语义）");

            cur = cur.AddSeconds(200);
            recentLog = LogLine(cur.AddSeconds(-150), "INF", "任务结束") + "\n"; // 已过期
            await Scan();
            Check(ran == 1, "用例6：CondLogMissing 窗口内未出现 → 满足（未被事件化破坏）");
        }

        Console.WriteLine(fails == 0 ? "ALL PASSED" : "FAILURES=" + fails);
        return fails;
    }
}
