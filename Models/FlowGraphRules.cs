using System;
using System.Collections.Generic;
using System.Linq;

namespace SoulLink.Models
{
    public enum EdgeConnectResult { Added = 0, Replaced = 1, Duplicate = 2, Self = 3, Cycle = 4, Invalid = 5 }

    /// <summary>PLC 单出口连线规则：允许扇出和汇入；NOT 在触点上，不在连线上。</summary>
    public static class FlowGraphRules
    {
        public static string NormalizeBranch(FlowNode from, string? branch) => "";
        public static EdgeConnectResult Connect(AutomationFlow? flow, FlowNode? from, FlowNode? to,
            string? branch, out FlowEdge? replaced)
        {
            replaced = null;
            if (flow == null || from == null || to == null || !flow.Nodes.Contains(from) || !flow.Nodes.Contains(to))
                return EdgeConnectResult.Invalid;
            if (from.Id == to.Id) return EdgeConnectResult.Self;
            if (from.Kind == FlowNodeKind.Terminate) return EdgeConnectResult.Invalid;
            if (flow.Edges.Any(e => e.From == from.Id && e.To == to.Id)) return EdgeConnectResult.Duplicate;
            if (CanReach(flow, to.Id, from.Id)) return EdgeConnectResult.Cycle;
            flow.Edges.Add(new FlowEdge { From = from.Id, To = to.Id, Branch = "" });
            if (to.IsContact && to.Logic == FlowLogicOp.LD) to.Logic = FlowLogicOp.AND;
            return EdgeConnectResult.Added;
        }
        public static bool CanReach(AutomationFlow? flow, string start, string target)
        {
            if (flow == null) return false;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>(); stack.Push(start);
            while (stack.Count > 0)
            {
                string cur = stack.Pop();
                if (cur == target) return true;
                if (!seen.Add(cur)) continue;
                foreach (var e in flow.Edges) if (e.From == cur) stack.Push(e.To);
            }
            return false;
        }
    }
}