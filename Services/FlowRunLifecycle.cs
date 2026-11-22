using System;
using System.Collections.Generic;
using System.Linq;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>一次运行只收尾实际选中的支路；并行支路全部成功才完成。</summary>
    public static class FlowRunLifecycle
    {
        public static bool IsTerminal(AutomationFlow flow, FlowNode node)
            => node.Kind == FlowNodeKind.Action || node.Kind == FlowNodeKind.Terminate;

        public static bool AllActivePathsComplete(AutomationFlow flow,
            Dictionary<string, FlowActionResult> results, HashSet<string> selected,
            DateTime? now = null)
        {
            foreach (var n in flow.Nodes.Where(n=>IsTerminal(flow,n)))
            {
                if (selected.Count > 0 && !selected.Contains(n.Id)) continue;
                if (n.Kind == FlowNodeKind.Terminate) continue;
                if (results.TryGetValue(n.Id, out var r) && r.Ok) continue;
                return false;
            }
            return true;
        }

        public static void StartRun(AutomationFlow flow, DateTime? now = null)
        {
            flow.RunComplete = false;
            flow.RunDayKey = PlcLogic.DayKey(now ?? DateTime.Now,
                PlcLogic.HasEnd(flow) ? PlcLogic.EndBoundaryHour(flow) : 0);
            flow.SatisfiedContacts.Clear(); flow.WaitingActions.Clear(); flow.RunningActions.Clear();
            flow.PlcPower.Clear(); flow.ActionResults.Clear(); flow.DelayUntil.Clear();
            // ★ 事件锁存：新一轮开始，丢弃上一轮残留的待消费日志事件与游标。
            //   游标会在下一轮扫描按「当前尾窗末尾」重建（= 不补历史，重启/跨日语义）。
            flow.LogTriggers.Clear();
        }

        public static void CompleteRun(AutomationFlow flow, DateTime now, bool daily)
        {
            bool hasEnd = PlcLogic.HasEnd(flow);
            flow.RunComplete = hasEnd;
            flow.RunDayKey = PlcLogic.DayKey(now,
                hasEnd ? PlcLogic.EndBoundaryHour(flow) : 0);
            flow.SatisfiedContacts.Clear(); flow.WaitingActions.Clear(); flow.RunningActions.Clear();
            flow.PlcPower.Clear(); flow.DelayUntil.Clear(); flow.ActionResults.Clear();
            if (daily) PlcLogic.CompleteToday(flow, now);
        }
    }
}