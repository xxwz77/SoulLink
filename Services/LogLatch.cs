using System;
using System.Collections.Generic;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>
    /// 日志触点的「事件锁存 + 消费」引擎。
    ///
    /// 语义（用户原话）：
    ///   1. 只要流程在运行，每出现一次，条件达到就记住 —— 日志出现匹配即记下一个待处理事件，不依赖时间窗；
    ///   2. 用完就清空 —— 动作执行成功后消费掉事件，不会重复触发；
    ///   3. 有 end 当天不再判断 —— 有 END 的流程当日完成后由 DailyLatchKey 锁存，本引擎不再接收新事件。
    ///
    /// 与旧「最近 N 秒电平」的本质区别：
    ///   旧实现每轮重扫尾窗快照并按行时间戳过滤 [now-seconds, now+2s]，一条日志窗口一过就永远丢。
    ///   新实现用**行序号游标**（类似文件偏移）识别「新增行」，只扫游标之后的新行；
    ///   匹配的记入待处理事件表，动作消费后才清空，所以即使流程当时在等别的步骤，
    ///   事件也会被记住，直到被消费。
    /// </summary>
    public static class LogLatch
    {
        /// <summary>
        /// 扫描该账号从游标开始的**新增**日志行，把匹配的记入待处理事件表。
        /// 返回当前触点是否「有条件满足待执行」。
        ///
        /// ★ 只读预览：caller 传入 <paramref name="mutable"/> = false 时，不推进游标、不写事件，
        ///   只根据「已锁存事件 + 尾窗新行命中」返回实时电平。
        /// </summary>
        public static bool Scan(AutomationFlow flow, FlowNode contact,
            IReadOnlyList<LogLineRef> tail, long tailSeq, DateTime now, bool mutable)
        {
            if (flow == null || contact == null) return false;
            tail ??= Array.Empty<LogLineRef>();

            // 有 END 且当日已完成：不再判断（不建状态、不推进游标、不锁存新事件）。
            // ★ 满足「有 end 当天不再判断」；对「今日不运行」同样成立（DailyLatchKey 已写当日）。
            if (PlcLogic.HasEnd(flow) && PlcLogic.IsDailyComplete(flow, now)) return false;

            if (!flow.LogTriggers.TryGetValue(contact.Id, out var state))
            {
                if (!mutable)
                {
                    // 只读预览：不建状态，仅按当前尾窗报实时电平（不推进、不锁存）。
                    foreach (var line in tail)
                        if (PlcLogMatcher.MatchText(line.Text, contact.Get("pattern"), contact.GetBool("isRegex", false), contact.Get("level")))
                            return true;
                    return false;
                }
                state = new LogTriggerState { Cursor = tailSeq }; // 首次建立：不补历史（重启/新增节点均如此）
                flow.LogTriggers[contact.Id] = state;
            }

            // 流被整体重置（行序号回退，如账号删除后重建）→ 游标归零，按新流重新扫描。
            if (tailSeq < state.Cursor) state.Cursor = 0;

            // 待消费事件还在 → 条件持续满足（等动作执行 + 成功后才消费）。
            if (state.Events.Count > 0) return true;

            bool hit = false;
            foreach (var line in tail)
            {
                if (line.Seq <= state.Cursor) continue;
                if (!PlcLogMatcher.MatchText(line.Text, contact.Get("pattern"), contact.GetBool("isRegex", false), contact.Get("level")))
                    continue;
                hit = true;
                if (mutable) state.Events.Add(new LogLatchEvent { Line = line.Text, Seq = line.Seq });
                state.Cursor = line.Seq; // 只推进到这条命中的行；其后未扫描的命中行留待下一轮
                break;
            }
            // 本轮尾窗里没有任何命中：把游标推到末尾，非命中行一并消费确认，不重复扫描。
            if (!hit && mutable) state.Cursor = tailSeq;
            return hit;
        }

        /// <summary>
        /// 动作执行**成功**后调用：清掉该触点的待消费事件，表示「用完就清空」。
        /// ★ 只清事件，游标保留，这样同一行不会再次触发，但之后的新行还能继续触发。
        ///   动作失败不调用本方法 —— 事件保留，下一轮按 lastFailed 重试（与既有语义一致）。
        /// </summary>
        public static void Consume(AutomationFlow flow, string contactId)
        {
            if (flow == null || !flow.LogTriggers.TryGetValue(contactId, out var state)) return;
            state.Events.Clear();
        }

        /// <summary>
        /// 动作执行成功后，消费掉「上游所有日志触点」的待消费事件。
        /// ★ 只消费真正导通该动作的日志触点：并行支路各自独立，A 支路执行成功
        ///   不会误清 B 支路还没执行的事件。
        /// </summary>
        public static void ConsumeUpstreamOf(AutomationFlow flow, FlowNode action)
        {
            if (flow == null || action == null) return;
            foreach (var contact in UpstreamContacts(flow, action.Id))
                if (IsLogTriggerContact(contact)) Consume(flow, contact.Id);
        }

        private static bool IsLogTriggerContact(FlowNode n)
            => n != null && n.IsContact && n.Type == FlowNodeType.TrigLogMatch;

        private static IEnumerable<FlowNode> UpstreamContacts(AutomationFlow flow, string start)
        {
            // ★ 递归穿越中间触点（DAG，seen 防环）：动作可能隔着 CondTimeRange/CondChoice 等
            //   门控条件才接到日志触点，必须一路回溯到源头，只消费其中的 TrigLogMatch。
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                string id = stack.Pop();
                if (!seen.Add(id)) continue;
                foreach (string src in flow.SourcesOf(id))
                {
                    var n = flow.FindNode(src);
                    if (n == null) continue;
                    if (n.IsContact) yield return n;
                    stack.Push(src);
                }
            }
        }
    }
}
