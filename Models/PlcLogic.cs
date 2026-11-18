using System;
using System.Collections.Generic;
using System.Linq;

namespace SoulLink.Models
{
    /// <summary>一个扫描周期内某节点的三个状态：自身值、入线电流、出线电流。</summary>
    public sealed class PlcNodeState
    {
        public bool Value;
        public bool Input;
        public bool Output;
        /// <summary>false=数据源未知/错误，不能因 NOT 被视为正常真。</summary>
        public bool Available = true;
    }

    /// <summary>
    /// 无 UI、无 IO 的 PLC 布尔求值器。
    /// 触点：LD=raw，AND=input AND raw，OR=input OR raw；NOT 只反 raw。
    /// OUT/END 的多条入线是独立梯级的 OR 汇入；动作没有上游时绝不自启动。
    /// </summary>
    public static class PlcLogic
    {
        public static Dictionary<string, PlcNodeState> Scan(AutomationFlow flow, Func<FlowNode, bool> read)
        {
            flow.EnsurePlcModel();
            var result = flow.Nodes.ToDictionary(n => n.Id, _ => new PlcNodeState(), StringComparer.OrdinalIgnoreCase);
            foreach (var node in Topological(flow))
            {
                var raw = node.IsContact ? (read(node) != node.Negated) : true;
                result[node.Id] = Evaluate(node, flow.SourcesOf(node.Id).Select(id => result[id]).ToList(), raw);
            }
            return result;
        }

        public static PlcNodeState Evaluate(FlowNode node, IReadOnlyList<PlcNodeState> sources, bool raw)
        {
            var state = new PlcNodeState { Value = raw };
            if (node.IsContact && sources.Count == 0) { state.Input = true; state.Output = raw; return state; }
            bool any = sources.Any(s => s.Output);
            bool all = sources.Count > 0 && sources.All(s => s.Output);
            state.Input = node.IsContact && node.Logic == FlowLogicOp.AND ? all : any;
            state.Output = node.IsContact
                ? node.Logic == FlowLogicOp.OR ? state.Input || raw : state.Input && raw
                : state.Input;
            return state;
        }

        public static List<FlowNode> Topological(AutomationFlow flow)
        {
            var indegree = flow.Nodes.ToDictionary(n => n.Id, _ => 0, StringComparer.OrdinalIgnoreCase);
            foreach (var e in flow.Edges) if (indegree.ContainsKey(e.To) && indegree.ContainsKey(e.From)) indegree[e.To]++;
            var ready = flow.Nodes.Where(n => indegree[n.Id] == 0).ToList();
            var ordered = new List<FlowNode>();
            while (ready.Count > 0)
            {
                var n = ready[0]; ready.RemoveAt(0); ordered.Add(n);
                foreach (var id in flow.TargetsOf(n.Id))
                    if (indegree.ContainsKey(id) && --indegree[id] == 0) ready.Add(flow.FindNode(id)!);
            }
            // 非法环也返回全部节点，结果保持断开；校验器会阻止运行。
            foreach (var n in flow.Nodes) if (!ordered.Contains(n)) ordered.Add(n);
            return ordered;
        }

        public static int NormalizeBoundaryHour(int hour) => hour == 4 ? 4 : 0;
        public static DateTime BusinessDay(DateTime now, int boundaryHour = 0)
            => NormalizeBoundaryHour(boundaryHour) == 4 && now.Hour < 4 ? now.Date.AddDays(-1) : now.Date;
        public static string DayKey(DateTime now, int boundaryHour = 0)
            => BusinessDay(now, boundaryHour).ToString("yyyy-MM-dd");
        /// <summary>END 节点独立持有日界；没有 END 的流程是循环流程。</summary>
        public static bool HasEnd(AutomationFlow flow)
            => flow.Nodes != null && flow.Nodes.Any(n => n.Kind == FlowNodeKind.Terminate);
        public static int EndBoundaryHour(AutomationFlow flow)
            => flow.Nodes.FirstOrDefault(n => n.Kind == FlowNodeKind.Terminate)?.DayBoundaryHour ?? 0;
        public static bool IsDailyComplete(AutomationFlow flow, DateTime now)
            => HasEnd(flow) && flow.DailyLatchKey == DayKey(now, EndBoundaryHour(flow));
        public static void CompleteToday(AutomationFlow flow, DateTime now)
            => flow.DailyLatchKey = DayKey(now, EndBoundaryHour(flow));
        /// <summary>有 END 才在当日停止扫描；无 END 完成一轮后立即重新获取条件。</summary>
        public static bool IsRunComplete(AutomationFlow flow, DateTime now)
            => IsDailyComplete(flow, now);
        /// <summary>清除 END 今日标记：到日界时自动清除，用户也可以手动清除后当天重跑。</summary>
        public static void ClearDailyLatch(AutomationFlow flow)
            => flow.DailyLatchKey = "";
        /// <summary>把 END 今日标记直接写成当前业务日（今日不运行）。</summary>
        public static void LatchToday(AutomationFlow flow, DateTime now)
            => CompleteToday(flow, now);
        /// <summary>手动切换今日运行状态：true=今日可运行，false=今日不运行。</summary>
        public static void SetTodayRun(AutomationFlow flow, DateTime now, bool enabled)
        {
            if (!HasEnd(flow)) return;
            if (enabled) { ClearDailyLatch(flow); flow.RunComplete = false; }
            else { LatchToday(flow, now); flow.RunComplete = true; }
        }
    }
}