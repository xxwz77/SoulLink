using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>
    /// 每轮读取实时输入；只有瞬时信号跨扫描保留。
    /// 有 END：完整收尾后写入 END 的日界锁存，当日不再扫描。
    /// 无 END：完整收尾后只清本轮状态，立刻重新获取条件，可循环触发。
    /// </summary>
    public static class PlcScanEngine
    {
        public delegate Task<PlcSignal> ContactReader(FlowNode node);
        public delegate Task<FlowActionResult> OutputWriter(FlowNode node);

        public static async Task<FlowRunner.RunResult> ScanAsync(AutomationFlow flow, ContactReader read, OutputWriter write,
            bool manual = false, DateTime? now = null, Func<FlowActionResult, Task>? checkpoint = null)
        {
            var r = new FlowRunner.RunResult();
            var time = now ?? DateTime.Now;
            flow.EnsurePlcModel();

            // 用户手动切换 END 的“今日运行状态”，两种方向都支持。
            bool todayReopened = false;
            if (flow.ToggleTodayRequested.HasValue || flow.RunResetRequested)
            {
                bool wantRun = flow.ToggleTodayRequested ?? true;
                todayReopened = wantRun && PlcLogic.HasEnd(flow);
                if (PlcLogic.HasEnd(flow))
                {
                    PlcLogic.SetTodayRun(flow, time, wantRun);
                    if (wantRun) { FlowRunLifecycle.StartRun(flow, time); flow.RunComplete = false; }
                }
                else if (flow.RunResetRequested) FlowRunLifecycle.StartRun(flow, time);
                flow.ToggleTodayRequested = null;
                flow.RunResetRequested = false;
                if (checkpoint != null)
                    await checkpoint(new FlowActionResult { Ok=true, Message=wantRun ? "已设置为今日运行" : "已设置为今日不运行" }).ConfigureAwait(false);
                if (!wantRun)
                {
                    await ReadContacts(flow, read, liveOnly: true, r).ConfigureAwait(false);
                    r.Message = "已设置为今日不运行，日界 " + PlcLogic.EndBoundaryHour(flow).ToString("00") + ":00 后恢复";
                    return r;
                }
            }

            if (!flow.Enabled) { r.Message = "流程未启用"; return r; }
            if (flow.Paused) { r.Message = "流程已暂停"; return r; }

            bool hasEnd = PlcLogic.HasEnd(flow);
            // 有 END 且今日已执行：当天不再扫描；到日界后自动清锁存并重新开始。
            if (hasEnd)
            {
                bool latchedToday = PlcLogic.IsDailyComplete(flow, time);
                if (!latchedToday && flow.DailyLatchKey.Length > 0)
                {
                    // 跨过 END 日界：清空昨日标记，开启新一轮。
                    PlcLogic.ClearDailyLatch(flow);
                    FlowRunLifecycle.StartRun(flow, time);
                    flow.RunComplete = false;
                    if (checkpoint != null) await checkpoint(new FlowActionResult { Ok=true, Message="日界已到，开始新一轮" }).ConfigureAwait(false);
                }
                if (latchedToday)
                {
                    await ReadContacts(flow, read, liveOnly: true, r).ConfigureAwait(false);
                    r.Message = "今日已完成，日界 " + PlcLogic.EndBoundaryHour(flow).ToString("00") + ":00 重置";
                    return r;
                }
            }
            else if (flow.RunComplete)
            {
                // 历史遗留的完成标记不能阻止循环流程。
                flow.RunComplete = false;
            }

            if (flow.RunDayKey.Length == 0) FlowRunLifecycle.StartRun(flow, time);

            var liveValues = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var signals = await ReadContacts(flow, read, liveOnly: false, r, liveValues).ConfigureAwait(false);
            var states = new Dictionary<string, PlcNodeState>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in PlcLogic.Topological(flow))
            {
                var sources = flow.SourcesOf(n.Id).Where(states.ContainsKey).Select(id => states[id]).ToList();
                if (n.IsContact)
                {
                    var signal = signals[n.Id];
                    var state = PlcLogic.Evaluate(n, sources, signal.Value);
                    state.Available = signal.Available;
                    // 灯只看实时值；锁存只影响本轮是否导通。
                    state.Value = liveValues.TryGetValue(n.Id, out bool liveNow) ? liveNow : signal.Value;
                    states[n.Id] = state;
                    if (state.Output) PlanSuccessors(flow, n, states);
                    r.Steps.Add(n.Title + "：" + (!signal.Available ? "数据不可用" : signal.Value ? "满足" : "不满足")
                        + "，" + (state.Output ? "继续" : "停止"));
                    continue;
                }

                bool input = sources.Count > 0 && sources.Any(s => s.Output);
                bool selected = SelectPath(flow, n, input);
                bool previous = flow.PlcPower.TryGetValue(n.Id, out var p) && p;
                if (n.Kind == FlowNodeKind.Terminate)
                {
                    flow.PlcPower[n.Id] = input && selected;
                    states[n.Id] = new PlcNodeState { Value = input, Input = input, Output = input && selected };
                    continue;
                }

                if (n.Type == FlowNodeType.ActDelay)
                {
                    if (!input || !selected) flow.DelayUntil.Remove(n.Id);
                    else if (!flow.DelayUntil.TryGetValue(n.Id, out _))
                        flow.DelayUntil[n.Id] = time.AddSeconds(Math.Clamp(n.GetInt("seconds", 10), 0, 3600));
                    if (input && selected && flow.DelayUntil.TryGetValue(n.Id, out var until) && time >= until)
                    {
                        flow.ActionResults[n.Id] = new FlowActionResult { Ok = true, At = time, Message = "等待完成" };
                        flow.RunningActions[n.Id] = flow.ActionResults[n.Id];
                        r.Steps.Add(n.Title + "：等待完成");
                    }
                    bool output = input && selected && flow.ActionResults.TryGetValue(n.Id, out var delayResult) && delayResult.Ok;
                    flow.PlcPower[n.Id] = input && selected;
                    states[n.Id] = new PlcNodeState { Value = input, Input = input, Output = output };
                    if (output) PlanSuccessors(flow, n, states);
                    continue;
                }

                if ((!input || !selected) && flow.ActionResults.TryGetValue(n.Id, out var old) && !old.Ok)
                    flow.ActionResults.Remove(n.Id);
                bool unknownResult = flow.RunningActions.TryGetValue(n.Id, out var pending)
                    && !pending.Ok && pending.Message == "结果待确认";
                if (input && selected && unknownResult && !manual)
                {
                    r.Message = "上次「" + n.Title + "」的外部结果未确认，未自动重复；请核对后手动运行";
                    r.Steps.Add(r.Message);
                    flow.PlcPower[n.Id] = input;
                    states[n.Id] = new PlcNodeState { Value = input, Input = input, Output = false };
                    continue;
                }
                // ★ 手动运行 = 用户点了「立即运行一次」：即使上一次已有结果（成功或失败），
                //   也必须重新执行，否则失败过一次就再也点不动（原来只有"结果待确认"
                //   允许被手动覆盖）。普通扫描仍然严格走上升沿，不会重复执行。
                //
                // ★★ 2.17.24 修「失败一次后永久卡死」★★
                //
                //   原来只有"成功"的结果会阻止重复执行 —— 但实际上**失败**也会：
                //   失败结果写进 ActionResults 后，只要触点一直为真（input=true）、
                //   PlcPower 一直是 true（previous=true），
                //   `!previous || !ContainsKey` 这两条就永远为 false → 动作再也不会执行。
                //   而上面那行只在 `!input || !selected` 时才清掉失败结果，
                //   可「定时·间隔」这类触点几乎一直是真（瞬时信号有 2 秒保留窗口，
                //   间隔 ≤2 秒时每轮读到都算真）→ 永远等不到那个"断开"的时机。
                //
                //   实测（2026-11-20 16:40，真实引擎 + 真实流程，连扫 8 轮）：
                //     每轮都是 msg="动作失败；断开后可重试"，PlcPower 恒 True，
                //     ActionResults 恒为 1 条失败记录，Triggered 恒 False。
                //     用户看到的正是"点了没反应 / 自动执行不动了"。
                //
                //   现在：**失败的结果不阻挡下一次执行**（每轮扫描重试，直到成功）。
                //   成功结果仍然阻挡，严格走上沿，不会重复执行。
                bool lastFailed = flow.ActionResults.TryGetValue(n.Id, out var lastRes) && !lastRes.Ok;
                if (input && selected && (manual || lastFailed || !previous || !flow.ActionResults.ContainsKey(n.Id)))
                {
                    flow.RunningActions[n.Id] = new FlowActionResult { Ok=false, At=time, Message="结果待确认" };
                    if (checkpoint != null) await checkpoint(new FlowActionResult { Ok = true, Message = "准备执行：" + n.Title }).ConfigureAwait(false);
                    var result = await write(n).ConfigureAwait(false);
                    flow.RunningActions.Remove(n.Id);
                    result.At = time;
                    flow.ActionResults[n.Id] = result;
                    r.Triggered = true;
                    r.Steps.Add("执行「" + n.Title + "」：" + result.Message);
                    r.Message = result.Ok ? "已执行：" + n.Title : "执行失败：" + n.Title + " · " + result.Message;

                    // ★ 2.17.23：状态灯要显示"执行成功=绿 / 执行失败=红"，
                    //   所以每次动作执行完就把结果记到流程上（不只等整轮收尾）。
                    //   原来只有 complete 分支才写 LastRunAt/RunCount，失败路径什么都不留，
                    //   界面只能看到一个永远不会变的状态。
                    flow.LastRunOk = result.Ok;
                    flow.LastRunError = result.Ok ? "" : (result.Message ?? "");
                    flow.LastRunAction = n.Title;
                    flow.LastActionResultAt = time;

                    if (checkpoint != null) await checkpoint(result).ConfigureAwait(false);
                }
                if (flow.ActionResults.TryGetValue(n.Id, out var actionResult) && actionResult.Ok)
                    flow.RunningActions[n.Id] = actionResult;
                flow.PlcPower[n.Id] = input && selected;
                states[n.Id] = new PlcNodeState { Value = input, Input = input, Output = input && selected };
                if (input && selected) PlanSuccessors(flow, n, states);
            }

            var selectedActions = flow.WaitingActions
                .Select(flow.FindNode).Where(n => n != null && n.Kind == FlowNodeKind.Action).ToList();
            var selectedTerminals = flow.WaitingActions.Select(flow.FindNode)
                .Where(n => n != null && FlowRunLifecycle.IsTerminal(flow, n)).ToList();
            bool failed = selectedActions.Any(n => flow.ActionResults.TryGetValue(n!.Id, out var result) && !result.Ok)
                || flow.RunningActions.Values.Any(v => !v.Ok && v.Message == "结果待确认");
            bool complete = selectedTerminals.Count > 0
                && FlowRunLifecycle.AllActivePathsComplete(flow, flow.ActionResults,
                    new HashSet<string>(selectedTerminals.Select(n => n!.Id), StringComparer.OrdinalIgnoreCase), time);
            if (complete && !failed)
            {
                if (checkpoint != null) await checkpoint(new FlowActionResult { Ok = true, Message = "准备收尾" }).ConfigureAwait(false);
                // 用户刚恢复今天运行时，本次执行不再立刻重新锁死；下次条件满足才结束今天。
                FlowRunLifecycle.CompleteRun(flow, time, hasEnd && !todayReopened);
                flow.LastRunAt = time;
                flow.RunCount++;
                r.Triggered = r.Passed = true;
                r.Message = hasEnd
                    ? (todayReopened
                        ? "已恢复今日运行并完成一次；可继续执行，直至再次到达「每天完成」"
                        : "今日已完成，日界 " + PlcLogic.EndBoundaryHour(flow).ToString("00") + ":00 后重新开始")
                    : "本轮已完成，等待条件重新满足后再次执行";
                r.Steps.Add(todayReopened
                    ? "本次已恢复今日运行，未重新锁存"
                    : hasEnd ? "写入 END 今日标记；当日不再扫描" : "无 END：清空本轮状态并重新获取条件");
                if (checkpoint != null) await checkpoint(new FlowActionResult { Ok = true, Message = r.Message }).ConfigureAwait(false);
            }
            else if (r.Triggered) r.Passed = !failed;
            if (!r.Triggered && r.Message.Length == 0) r.Message = failed ? "动作失败；断开后可重试" : "等待本轮条件或动作完成";

            flow.RuntimeStates = states;
            flow.Disarm();
            return r;
        }

        private static async Task<Dictionary<string, PlcSignal>> ReadContacts(AutomationFlow flow,
            ContactReader read, bool liveOnly, FlowRunner.RunResult r,
            Dictionary<string, bool>? liveValues = null)
        {
            var signals = new Dictionary<string, PlcSignal>(StringComparer.OrdinalIgnoreCase);
            var states = new Dictionary<string, PlcNodeState>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in PlcLogic.Topological(flow).Where(n => n.IsContact))
            {
                PlcSignal signal;
                try { signal = await read(n).ConfigureAwait(false); }
                catch (Exception ex) { signal = new PlcSignal(false, false); r.Steps.Add(n.Title + "：读取失败 · " + ex.Message); }
                bool live = signal.Available && (n.Negated ? !signal.Value : signal.Value);

                // ★★ 「手动」触发**不参与**跨扫描的瞬时锁存。★★
                //
                //   瞬时锁存是为了"日志匹配/定时/间隔"这类**发生在扫描之外**的事件：
                //   本次扫描没看到，也要留给下一轮，等其它条件凑齐。
                //   但 TrigManual 的"事件"就是**这一次手动扫描本身**——
                //   把它写进 SatisfiedContacts 后，手动触点永远没有"断开"的时机：
                //   之后每次普通扫描都把它算成"满足"，动作节点的 PlcPower 一直是 true、
                //   ActionResults 一直留着上一次的结果，于是再点「立即运行一次」
                //   只会重复报上一次的失败（实测 2026-11-20 15:52：
                //   弹窗仍是 02:28 的"该会话没有已连接的注入通道"，按键根本没再发出）。
                bool latchable = IsMomentary(n) && n.Type != FlowNodeType.TrigManual;
                if (!liveOnly && live && latchable) flow.SatisfiedContacts.Add(n.Id);
                bool effective = !liveOnly && latchable && flow.SatisfiedContacts.Contains(n.Id) ? true : live;
                signal = new PlcSignal(effective, signal.Available);
                signals[n.Id] = signal;
                var sources = flow.SourcesOf(n.Id).Where(states.ContainsKey).Select(id => states[id]).ToList();
                var state = PlcLogic.Evaluate(n, sources, effective);
                state.Value = live;
                state.Available = signal.Available;
                states[n.Id] = state;
                if (liveValues != null) liveValues[n.Id] = live;
                if (live) r.ContactLit[n.Id] = true;
            }
            flow.RuntimeStates = states;
            return signals;
        }

        private static void PlanSuccessors(AutomationFlow flow, FlowNode source,
            Dictionary<string, PlcNodeState> states)
        {
            var targets = flow.TargetsOf(source.Id).Select(flow.FindNode).Where(n => n != null).ToList();
            if (targets.Count == 0) return;
            bool choice = source.BranchMode == FlowBranchMode.Choice && targets.Count > 1;
            if (choice)
            {
                var target = targets.FirstOrDefault(n => states.TryGetValue(n!.Id, out var s) && s.Output);
                if (target != null) SelectSubtree(flow, target.Id);
                return;
            }
            foreach (var target in targets) SelectSubtree(flow, target!.Id);
        }

        private static void SelectSubtree(AutomationFlow flow, string id)
        {
            var node = flow.FindNode(id);
            if (node == null) return;
            if (!node.IsContact) flow.WaitingActions.Add(id);
            foreach (string next in flow.TargetsOf(id)) SelectSubtree(flow, next);
        }

        private static bool SelectPath(AutomationFlow flow, FlowNode node, bool input)
        {
            if (!input) return false;
            if (flow.WaitingActions.Contains(node.Id)) return true;
            if (flow.WaitingActions.Count > 0 && !IsDownstreamOfSelection(flow, node.Id)) return false;
            flow.WaitingActions.Add(node.Id);
            return true;
        }

        private static bool IsDownstreamOfSelection(AutomationFlow flow, string target)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>(flow.WaitingActions);
            while (pending.Count > 0)
            {
                string id = pending.Pop();
                if (!seen.Add(id)) continue;
                if (id == target) return true;
                foreach (string next in flow.TargetsOf(id)) pending.Push(next);
            }
            return false;
        }

        private static bool IsMomentary(FlowNode n) => n.Type == FlowNodeType.TrigLogMatch
            || n.Type == FlowNodeType.CondLogContains || n.Type == FlowNodeType.CondLogHasError
            || n.Type == FlowNodeType.TrigInterval || n.Type == FlowNodeType.TrigSchedule || n.Type == FlowNodeType.TrigManual;

        public static string InstructionOf(FlowNode n) => n.IsContact && n.Negated
            ? (n.Logic == FlowLogicOp.LD ? "LDI" : n.Logic == FlowLogicOp.OR ? "ORI" : "ANI")
            : (n.Logic == FlowLogicOp.LD ? "LD" : n.Logic == FlowLogicOp.OR ? "OR" : "AND");
    }
}
