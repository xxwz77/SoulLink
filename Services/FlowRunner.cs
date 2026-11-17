using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>
    /// 自动化流程执行引擎。
    ///
    /// 执行模型（严格按用户要求）：
    ///   1. 一个流程**只有一个触发器**；
    ///   2. 触发器判定通过 → 取出它连出的所有条件节点；
    ///   3. 条件节点按 ConditionMode 聚合（All=全部满足 / Any=任一满足）；
    ///   4. 聚合通过 → 顺序执行连出的执行器节点（可多个）；
    ///   5. 全部操作只针对**该流程绑定的账号**，互不干扰。
    ///
    /// ★ 限制最大深度，防止用户连出环导致死循环。
    /// </summary>
    public static class FlowRunner
    {
        private const int MaxDepth = 32;

        /// <summary>最近一个执行器是否成功（供"上一步结果"条件使用）。</summary>
        [ThreadStatic] private static bool LastActionOk;

        /// <summary>最近一个执行器耗时（秒），供数值比较使用。</summary>
        [ThreadStatic] private static double LastActionElapsedSeconds;

        /// <summary>解析 HH:mm 或 HH:mm:ss 为"当天第几分钟"。</summary>
        private static bool TryParseTime(string s, out double minutes)
        {
            minutes = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var parts = s.Split(':');
            if (parts.Length < 2) return false;
            if (!int.TryParse(parts[0], out int h)) return false;
            if (!int.TryParse(parts[1], out int m)) return false;
            int sec = 0;
            if (parts.Length >= 3 && int.TryParse(parts[2], out int sc)) sec = sc;
            minutes = h * 60 + m + sec / 60.0;
            return true;
        }

        /// <summary>一次执行的结果。</summary>
        public sealed class RunResult
        {
            public bool Triggered;
            public bool Passed;
            public string Message = "";
            public List<string> Steps = new List<string>();
        }

        /// <summary>
        /// 评估并（条件通过时）执行一个流程。
        /// sessionId 为目标账号的当前会话号（0 表示无会话）。
        /// </summary>
        public static async Task<RunResult> RunAsync(
            AutomationFlow flow, int sessionId, AppConfig cfg, bool manual = false)
        {
            var result = new RunResult();
            try
            {
                if (flow == null) { result.Message = "流程为空"; return result; }
                if (!flow.Enabled) { result.Message = "流程未启用"; return result; }
                if (flow.Paused) { result.Message = "流程已暂停"; return result; }

                // ★ 第二道保险：结构不合法就不执行。
                //   调度器那边已经校验过，这里防"手动调用忘了校验"的情况。
                var guard = FlowValidator.Validate(flow, cfg);
                if (guard.HasError)
                {
                    result.Message = "流程结构不完整，无法运行\n" + guard.ToText();
                    return result;
                }

                // ══════════════════════════════════════════════════════
                // 状态机模型（用户要求）：
                //   触发器命中 → 武装 Active=true、清空锁存
                //   → 每轮检查所有条件，**满足就锁存**（顺序无关）
                //   → 所有必需条件都锁存 → 执行器触发 → 退出流程、条件清零
                //   ★ 只有"退出流程"才清零：执行完成 / 手动结束 / 超时。
                // ══════════════════════════════════════════════════════

                var trigger = flow.Trigger;
                if (trigger == null) { result.Message = "没有触发器"; return result; }

                // ── ① 触发器判定 ──
                bool trigOk = await CheckTriggerAsync(trigger, flow.AccountId, sessionId, cfg, manual)
                                   .ConfigureAwait(false);

                if (trigOk && !flow.Active)
                {
                    // 第一次命中：武装
                    flow.Arm();
                    result.Triggered = true;
                    result.Steps.Add("触发器命中，开始等待条件");
                }
                else if (flow.Active)
                {
                    // ── 武装超时判定 ──
                    // 触发器命中后若条件长期凑不齐，自动退出，避免永久卡住。
                    if (flow.ArmTimeoutMinutes > 0 && flow.ArmedAt.HasValue)
                    {
                        var elapsed = DateTime.Now - flow.ArmedAt.Value;
                        // 时钟回拨等异常情况下 elapsed 可能为负，忽略
                        if (elapsed.TotalMinutes >= flow.ArmTimeoutMinutes)
                        {
                            int latchedCount = flow.Latched?.Count ?? 0;
                            flow.Disarm();
                            result.Triggered = true;
                            result.Passed = false;
                            result.Message = "条件等待超时（已等 " + (int)elapsed.TotalMinutes
                                           + " 分钟，要求 " + flow.ArmTimeoutMinutes
                                           + " 分钟内凑齐，已满足 " + latchedCount + " 个）— 已退出流程并清空条件";
                            result.Steps.Add("超时退出：" + flow.ArmTimeoutMinutes + " 分钟未凑齐条件，条件已清零");
                            return result;
                        }

                        // 未超时：把剩余时间写进提示，方便看日志时判断
                        int remain = flow.ArmTimeoutMinutes - (int)elapsed.TotalMinutes;
                        result.Steps.Add("距超时还有 " + Math.Max(0, remain) + " 分钟");
                    }

                    // 已武装：继续检查条件（触发器不必再命中）
                    result.Triggered = true;
                }
                else
                {
                    result.Message = "触发器未满足";
                    return result;
                }

                // ── ② 收集所有条件节点并锁存 ──
                var allConds = new List<FlowNode>();
                foreach (var n in flow.Nodes)
                    if (n.Kind == FlowNodeKind.Condition) allConds.Add(n);

                foreach (var c in allConds)
                {
                    if (ScheduleRunnerHost.IsShuttingDown)
                    {
                        result.Message = "应用正在退出，已停止检查条件";
                        return result;
                    }

                    if (flow.IsLatched(c.Id))
                    {
                        result.Steps.Add("条件：" + c.Title + " → 已满足（保持）");
                        continue;
                    }

                    bool ok = await CheckConditionAsync(c, flow.AccountId, sessionId, cfg)
                                   .ConfigureAwait(false);
                    if (ok)
                    {
                        flow.Latch(c.Id);
                        result.Steps.Add("条件：" + c.Title + " → 刚满足 ✔");
                    }
                    else
                    {
                        result.Steps.Add("条件：" + c.Title + " → 未满足");
                    }
                }

                // ── ③ 判定"是否全部必需条件都满足" ──
                //   ★ 串联/并联由**连线结构**决定：
                //     · 条件 A 连到条件 B → 串联（B 只有在 A 之后才参与判定）
                //     · 条件 A、B 都连到同一个执行器 → 并联
                //   为了直观，这里按"到达执行器前必须满足的全部上游条件"来判。
                //
                //   简化实现：取所有能到达「执行器」的条件集合，
                //   要求它们全部锁存（ConditionMode=All）或任一锁存（Any）。
                var actionNodes = new List<FlowNode>();
                foreach (var n in flow.Nodes)
                    if (n.Kind == FlowNodeKind.Action || n.Kind == FlowNodeKind.Terminate)
                        actionNodes.Add(n);

                bool conditionsReady;
                string pendingText = "";

                if (allConds.Count == 0)
                {
                    conditionsReady = true;
                }
                else if (flow.ConditionMode == FlowConditionMode.Any)
                {
                    // 并联：任一满足即可
                    conditionsReady = false;
                    foreach (var c in allConds)
                        if (flow.IsLatched(c.Id)) { conditionsReady = true; break; }

                    if (!conditionsReady)
                    {
                        var names = new List<string>();
                        foreach (var c in allConds) if (!flow.IsLatched(c.Id)) names.Add(c.Title);
                        pendingText = string.Join(" / ", names);
                    }
                }
                else
                {
                    // 串联：全部满足
                    conditionsReady = true;
                    var names = new List<string>();
                    foreach (var c in allConds)
                        if (!flow.IsLatched(c.Id)) { conditionsReady = false; names.Add(c.Title); }
                    if (names.Count > 0) pendingText = string.Join(" / ", names);
                }

                if (!conditionsReady)
                {
                    result.Passed = false;
                    result.Message = "等待条件：" + pendingText
                                   + "（已满足 " + flow.Latched.Count + "/" + allConds.Count + "）";
                    return result;
                }

                result.Passed = true;
                result.Steps.Add("条件已齐，开始执行");


                // ── ③ 从触发器开始沿连线遍历执行 ────────────────────
                // ★ 改为图遍历：执行器后面可以再接条件（判断执行结果），
                //   条件后面又能接执行器，支持任意深度的链。
                //   遇到「结束流程」节点立即停止。
                LastActionOk = true;
                LastActionElapsedSeconds = 0;

                var visited = new HashSet<string>();
                var queue = new Queue<(FlowNode node, int depth)>();

                // 触发器的所有下游入队（条件和执行器都行）
                foreach (var id in flow.TargetsOf(trigger.Id))
                {
                    var n = flow.FindNode(id);
                    if (n != null) queue.Enqueue((n, 0));
                }

                // 已锁存的条件直接放行（前面已经聚合判定过了）
                foreach (var c in allConds) if (flow.IsLatched(c.Id)) visited.Add(c.Id);

                bool flowEnded = false;

                while (queue.Count > 0)
                {
                    // ★ 应用退出 → 立刻中止，不再执行任何节点
                    if (ScheduleRunnerHost.IsShuttingDown)
                    {
                        flow.Disarm();
                        result.Message = "应用正在退出，流程已中止";
                        return result;
                    }

                    var (node, depth) = queue.Dequeue();
                    if (depth > MaxDepth)
                    {
                        result.Steps.Add("⚠ 链路过深，已中止（可能有环）");
                        break;
                    }
                    if (node.Id == trigger.Id) continue;
                    if (node.Kind == FlowNodeKind.Trigger) continue;

                    // ── 条件节点：现场判定 ──
                    if (node.Kind == FlowNodeKind.Condition)
                    {
                        if (visited.Contains(node.Id))
                        {
                            // 已在前面聚合过且通过 → 继续往下走
                            foreach (var t in flow.TargetsOf(node.Id))
                            {
                                var tn = flow.FindNode(t);
                                if (tn != null) queue.Enqueue((tn, depth + 1));
                            }
                            continue;
                        }

                        bool ok = await CheckConditionAsync(node, flow.AccountId, sessionId, cfg)
                                       .ConfigureAwait(false);
                        result.Steps.Add("条件：" + node.Title + " → " + (ok ? "通过" : "不满足"));
                        if (!ok) continue;    // 不满足 → 这条分支不走

                        foreach (var t in flow.TargetsOf(node.Id))
                        {
                            var tn = flow.FindNode(t);
                            if (tn != null) queue.Enqueue((tn, depth + 1));
                        }
                        continue;
                    }

                    // ★ 「结束流程」不是真正的执行器，必须在调 RunActionAsync **之前**拦截。
                    //   否则它会走到 default 分支返回"未知的执行器类型"（用户实测报错）。
                    if (node.Type == FlowNodeType.ActEndFlow)
                    {
                        flowEnded = true;
                        flow.Disarm();     // 结束流程 → 条件清零
                        result.Steps.Add("执行：" + node.Title + " → 流程已结束");
                        result.Steps.Add("流程已结束（条件已清零）");
                        break;
                    }

                    // ── 执行器节点 ──
                    string actAcc = TargetAccountId(node, flow.AccountId, cfg);
                    int actSid = TargetSessionId(node, flow.AccountId, sessionId, cfg);

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var (aok, amsg) = await RunActionAsync(node, actAcc, actSid, cfg)
                                             .ConfigureAwait(false);
                    sw.Stop();

                    LastActionOk = aok;
                    LastActionElapsedSeconds = sw.Elapsed.TotalSeconds;

                    result.Steps.Add("执行：" + node.Title + " → " + amsg);

                    if (!aok)
                    {
                        // 执行失败也属于"退出流程"，条件清零
                        flow.Disarm();
                        result.Message = "执行失败：" + node.Title + " · " + amsg;
                        return result;
                    }

                    // 继续往下走
                    foreach (var t in flow.TargetsOf(node.Id))
                    {
                        var tn = flow.FindNode(t);
                        if (tn != null) queue.Enqueue((tn, depth + 1));
                    }
                }

                // ★ 流程执行完成 → 退出流程，条件全部清零（等待下一次触发）
                flow.Disarm();
                flow.LastRunAt = DateTime.Now;
                flow.RunCount++;

                int stepCount = 0;
                foreach (var s in result.Steps) if (s.StartsWith("执行：")) stepCount++;

                result.Message = flowEnded
                    ? ("流程已结束（执行 " + stepCount + " 步）")
                    : (stepCount == 0 ? "已触发（没有执行器）" : ("执行完成，共 " + stepCount + " 步"));
                return result;
            }
            catch (Exception ex)
            {
                result.Message = "执行异常：" + ex.Message;
                return result;
            }
        }

        // ── 触发器 ──────────────────────────────────────────────────

        private static async Task<bool> CheckTriggerAsync(
            FlowNode t, string accountId, int sessionId, AppConfig cfg, bool manual)
        {
            switch (t.Type)
            {
                case FlowNodeType.TrigManual:
                    return manual;   // 只有手动点才触发

                case FlowNodeType.TrigSchedule:
                {
                    int h = t.GetInt("hour", 4);
                    int m = t.GetInt("minute", 0);
                    int s = t.GetInt("second", 0);
                    int interval = t.GetInt("intervalSeconds", 0);
                    var now = DateTime.Now;

                    if (interval > 0)
                    {
                        // 间隔模式：距上次执行超过 interval 就触发
                        // （上次时间由调用方维护在 flow.LastRunAt，这里用近似：整分钟对齐）
                        long slot = (long)(now.TimeOfDay.TotalSeconds) / interval;
                        long lastSlot = (long)((now.TimeOfDay.TotalSeconds - 5)) / interval;
                        return slot != lastSlot;   // 刚跨过间隔边界
                    }

                    // 每天固定时刻：允许 5 秒容差
                    var target = now.Date.AddHours(h).AddMinutes(m).AddSeconds(s);
                    var delta = now - target;
                    return delta.TotalSeconds >= 0 && delta.TotalSeconds < 5;
                }

                case FlowNodeType.TrigSessionOnline:
                    return sessionId > 0;

                case FlowNodeType.TrigLogMatch:
                {
                    // 日志监控触发器：最近 10 秒内日志里出现过匹配内容
                    string pattern = t.Get("pattern");
                    if (string.IsNullOrWhiteSpace(pattern)) return false;
                    return LogMatched(accountId, cfg, pattern,
                        t.GetBool("isRegex", false), t.Get("level"), 10);
                }

                case FlowNodeType.TrigBgiIdle:
                {
                    int idleSec = t.GetInt("idleSeconds", 300);
                    var inst = FindBgi(accountId, cfg);
                    if (inst == null) return false;
                    double idle = (DateTime.Now - inst.Value.lastWrite).TotalSeconds;
                    return idle >= idleSec;
                }

                default:
                    await Task.CompletedTask.ConfigureAwait(false);
                    return false;
            }
        }

        // ── 条件 ────────────────────────────────────────────────────

        private static async Task<bool> CheckConditionAsync(
            FlowNode c, string accountId, int sessionId, AppConfig cfg)
        {
            string user = UserOf(accountId, cfg);
            switch (c.Type)
            {
                case FlowNodeType.CondBgiRunning:
                {
                    bool running = FindBgi(accountId, cfg) != null;
                    return running == c.GetBool("expect", true);
                }

                case FlowNodeType.CondBgiActive:
                {
                    int within = c.GetInt("withinSeconds", 120);
                    var inst = FindBgi(accountId, cfg);
                    if (inst == null) return false;
                    return (DateTime.Now - inst.Value.lastWrite).TotalSeconds <= within;
                }

                case FlowNodeType.CondGenshinRunning:
                {
                    bool running = ProcessOfSession("YuanShen", sessionId) != null;
                    return running == c.GetBool("expect", true);
                }

                case FlowNodeType.CondTimeRange:
                {
                    var now = DateTime.Now.TimeOfDay;
                    if (!TimeSpan.TryParse(c.Get("from", "00:00"), out var from)) from = TimeSpan.Zero;
                    if (!TimeSpan.TryParse(c.Get("to", "23:59"), out var to)) to = TimeSpan.FromDays(1);
                    // 支持跨零点（如 22:00 - 06:00）
                    if (from <= to) return now >= from && now <= to;
                    return now >= from || now <= to;
                }

                case FlowNodeType.CondLogContains:
                {
                    string pattern = c.Get("pattern");
                    if (string.IsNullOrWhiteSpace(pattern)) return false;
                    int within = Math.Max(5, c.GetInt("withinSeconds", 120));
                    return LogMatched(accountId, cfg, pattern, c.GetBool("isRegex", false), "", within);
                }

                case FlowNodeType.CondLogMissing:
                {
                    string pattern = c.Get("pattern");
                    if (string.IsNullOrWhiteSpace(pattern)) return false;
                    int within = Math.Max(5, c.GetInt("withinSeconds", 120));
                    return !LogMatched(accountId, cfg, pattern, c.GetBool("isRegex", false), "", within);
                }

                case FlowNodeType.CondLogHasError:
                {
                    int within = Math.Max(5, c.GetInt("withinSeconds", 300));
                    return LogHasError(accountId, cfg, within);
                }

                case FlowNodeType.CondProcessExists:
                {
                    string pname = c.Get("process").Trim();
                    if (pname.Length == 0) return false;
                    // 去掉 .exe 后缀（Process.GetProcessesByName 不要后缀）
                    if (pname.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        pname = pname.Substring(0, pname.Length - 4);

                    // ★ 优先按"该节点指定的用户名"查（可跨用户），
                    //   没有用户名就按会话查，再退到全系统。
                    bool exists;
                    string nodeUser = c.Get("user").Trim();
                    if (nodeUser.Length > 0)
                        exists = ProcessExistsForUser(pname, nodeUser);
                    else if (sessionId > 0)
                        exists = ProcessOfSession(pname, sessionId) != null;
                    else
                        exists = ProcessExistsAnywhere(pname);

                    string expect = c.Get("expect", "存在").Trim();
                    bool want = !expect.Equals("不存在", StringComparison.Ordinal)
                                && !expect.Equals("false", StringComparison.OrdinalIgnoreCase);
                    return exists == want;
                }

                case FlowNodeType.CondBgiLiveState:
                {
                    string expect = c.Get("expect", "running").Trim().ToLowerInvariant();

                    var inst = FindBgi(accountId, cfg);
                    bool hasProc = inst != null;
                    bool hasOutput = false;
                    if (hasProc)
                    {
                        // 日志在最近 5 分钟内还在写 → 认为"有产出"
                        hasOutput = (DateTime.Now - inst!.Value.lastWrite).TotalMinutes <= 5;
                    }

                    return expect switch
                    {
                        "running" => hasProc && hasOutput,
                        "idle" => hasProc && !hasOutput,
                        "stopped" => !hasProc,
                        _ => false
                    };
                }

                case FlowNodeType.CondSessionState:
                {
                    string expect = c.Get("expect", "active").Trim().ToLowerInvariant();

                    var sessions = SessionService.QueryAsync().GetAwaiter().GetResult();
                    var s = SessionService.Find(sessions, user);

                    if (s == null || s.Id <= 0)
                        return expect == "none";

                    bool active = !string.Equals(s.State, "Disc", StringComparison.OrdinalIgnoreCase);
                    return expect switch
                    {
                        "active" => active,
                        "disc" => !active,
                        "none" => false,
                        _ => false
                    };
                }

                case FlowNodeType.CondWindowForeground:
                {
                    string title = c.Get("title").Trim();
                    try
                    {
                        var relay = RelayClient.FindBySession(sessionId);
                        if (relay == null) return false;
                        var resp = relay.ForegroundAsync().GetAwaiter().GetResult();
                        if (resp == null || !resp.Ok) return false;
                        if (title.Length == 0) return true;
                        return (resp.Message ?? "").IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    catch { return false; }
                }

                case FlowNodeType.CondLastResult:
                {
                    // 判断上一步执行器的结果
                    string expect = c.Get("expect", "ok").Trim().ToLowerInvariant();
                    if (expect == "any") return true;
                    bool ok = LastActionOk;
                    return expect == "ok" ? ok : !ok;
                }

                case FlowNodeType.CondCompare:
                {
                    string left = c.Get("left", "now").Trim();
                    string op = c.Get("op", ">").Trim();
                    string right = c.Get("right", "").Trim();

                    // 解析左值
                    double lv;
                    bool lIsTime = false;
                    if (left.Equals("now", StringComparison.OrdinalIgnoreCase)
                        || left.Equals("nowTime", StringComparison.OrdinalIgnoreCase))
                    {
                        var t = DateTime.Now;
                        lv = t.Hour * 60 + t.Minute;   // 分钟数
                        lIsTime = true;
                    }
                    else if (left.Equals("lastElapsed", StringComparison.OrdinalIgnoreCase))
                    {
                        lv = LastActionElapsedSeconds;
                    }
                    else if (!double.TryParse(left, out lv))
                    {
                        return false;
                    }

                    // 解析右值
                    double rv;
                    if (TryParseTime(right, out var mins))
                    {
                        rv = mins;
                        lIsTime = true;
                    }
                    else if (!double.TryParse(right, out rv))
                    {
                        return false;
                    }

                    // 若有一边是时间，另一边也按分钟理解
                    if (lIsTime && !TryParseTime(left, out _))
                    {
                        // left 已是分钟数，right 也按分钟
                    }

                    return op switch
                    {
                        ">" => lv > rv,
                        ">=" => lv >= rv,
                        "<" => lv < rv,
                        "<=" => lv <= rv,
                        "==" => Math.Abs(lv - rv) < 0.0001,
                        "!=" => Math.Abs(lv - rv) >= 0.0001,
                        _ => false
                    };
                }

                default:
                    await Task.CompletedTask.ConfigureAwait(false);
                    return false;
            }
        }

        // ── 执行器 ──────────────────────────────────────────────────

        private static async Task<(bool ok, string msg)> RunActionAsync(
            FlowNode a, string accountId, int sessionId, AppConfig cfg)
        {
            try
            {
                switch (a.Type)
                {
                    case FlowNodeType.ActRunAction:
                    {
                        string actId = a.Get("actionId");
                        ActionItem? act = null;
                        foreach (var x in cfg.Actions) if (x.Id == actId) { act = x; break; }
                        if (act == null) return (false, "找不到动作");

                        var r = await ActionService.ExecuteAsync(act, cfg).ConfigureAwait(false);
                        return (r.Ok, r.Message);
                    }

                    case FlowNodeType.ActStopBgi:
                    {
                        string exe = BgiExeOf(accountId, cfg);
                        var prep = await BgiTaskPreparer.PrepareAsync(
                            sessionId, exe, stopBgi: true, closeGenshin: false,
                            extraWaitSeconds: 1).ConfigureAwait(false);
                        return (prep.Ok, prep.Message);
                    }

                    case FlowNodeType.ActStartBgi:
                    {
                        string exe = BgiExeOf(accountId, cfg);
                        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                            return (false, "找不到 BetterGI.exe");

                        var launch = await SystemLauncher.LaunchAsync(
                            sessionId, exe, a.Get("args")).ConfigureAwait(false);
                        return (launch.Ok, launch.Message);
                    }

                    case FlowNodeType.ActStartOneDragon:
                    {
                        string exe = BgiExeOf(accountId, cfg);
                        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                            return (false, "找不到 BetterGI.exe");

                        // 先停掉正在跑的脚本（一条龙要求 BGI 空闲）
                        var prep = await BgiTaskPreparer.PrepareAsync(
                            sessionId, exe, stopBgi: true, closeGenshin: false,
                            extraWaitSeconds: 2).ConfigureAwait(false);
                        if (!prep.Ok) return (false, "停止旧任务失败：" + prep.Message);

                        string name = a.Get("name").Trim();
                        string args = name.Length == 0
                            ? "--startOneDragon"
                            : "--startOneDragon \"" + name + "\"";

                        var launch = await SystemLauncher.LaunchAsync(sessionId, exe, args).ConfigureAwait(false);
                        return (launch.Ok, launch.Message);
                    }

                    case FlowNodeType.ActStartGroups:
                    {
                        string exe = BgiExeOf(accountId, cfg);
                        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                            return (false, "找不到 BetterGI.exe");

                        var prep = await BgiTaskPreparer.PrepareAsync(
                            sessionId, exe, stopBgi: true, closeGenshin: false,
                            extraWaitSeconds: 2).ConfigureAwait(false);
                        if (!prep.Ok) return (false, "停止旧任务失败：" + prep.Message);

                        string groups = a.Get("groups").Trim();
                        if (groups.Length == 0) return (false, "没有填配置组名");
                        string args = "--startGroups " + groups;

                        var launch = await SystemLauncher.LaunchAsync(sessionId, exe, args).ConfigureAwait(false);
                        return (launch.Ok, launch.Message);
                    }

                    case FlowNodeType.ActCloseGenshin:
                    {
                        var prep = await BgiTaskPreparer.PrepareAsync(
                            sessionId, "", stopBgi: false, closeGenshin: true,
                            extraWaitSeconds: 1).ConfigureAwait(false);
                        return (prep.Ok, prep.Message);
                    }

                    case FlowNodeType.ActMouseClick:
                    case FlowNodeType.ActMouseMove:
                    case FlowNodeType.ActMouseWheel:
                    case FlowNodeType.ActKeyPress:
                        return await RunInputAsync(a, sessionId, cfg).ConfigureAwait(false);

                    case FlowNodeType.ActJoinRoom:
                    {
                        string code = a.Get("code").Trim();
                        if (code.Length == 0) return (false, "没有填写房间码");

                        // 这个节点作用到哪个账号
                        string userName = UserOf(accountId, cfg);
                        if (userName.Length == 0) return (false, "找不到目标账号");

                        // 显示名：优先昵称
                        string display = userName;
                        foreach (var acc in cfg.Accounts)
                            if (string.Equals(acc.UserName, userName, StringComparison.OrdinalIgnoreCase))
                            {
                                if (!string.IsNullOrWhiteSpace(acc.DisplayName))
                                    display = acc.DisplayName;
                                break;
                            }

                        // 调服务端加入房间
                        var join = await LobbyApi.JoinAsync(code, display, "").ConfigureAwait(false);
                        if (!join.Ok) return (false, join.Message);

                        return (true, join.Message
                            + (join.Room != null ? "（" + join.Room.Title + "）" : ""));
                    }

                    case FlowNodeType.ActLaunchProcess:
                    {
                        string path = a.Get("path").Trim();
                        if (path.Length == 0) return (false, "没有填写程序路径");
                        if (!File.Exists(path)) return (false, "程序不存在：" + path);

                        var launch = await SystemLauncher.LaunchAsync(
                            sessionId, path, a.Get("args")).ConfigureAwait(false);
                        return (launch.Ok, launch.Message);
                    }

                    case FlowNodeType.ActKillProcess:
                    {
                        string pname = a.Get("process").Trim();
                        if (pname.Length == 0) return (false, "没有填写进程名");
                        if (pname.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            pname = pname.Substring(0, pname.Length - 4);

                        bool force = a.GetBool("force", true);
                        string nodeUser = a.Get("user").Trim();
                        int killed = 0;

                        // ★ 只结束目标账号会话里的进程，避免误杀别人
                        try
                        {
                            foreach (var p in Process.GetProcessesByName(pname))
                            {
                                try
                                {
                                    bool mine = sessionId > 0 && p.SessionId == sessionId;

                                    if (!mine && nodeUser.Length > 0)
                                    {
                                        // 没有会话号时按"该进程属于哪个用户"判断
                                        var psi = new ProcessStartInfo("tasklist.exe",
                                            "/FI \"PID eq " + p.Id + "\" /V /FO CSV /NH")
                                        {
                                            UseShellExecute = false,
                                            RedirectStandardOutput = true,
                                            CreateNoWindow = true,
                                            StandardOutputEncoding = SystemLauncher.ConsoleEncoding()
                                        };
                                        using var tp = Process.Start(psi);
                                        string owner = tp?.StandardOutput.ReadToEnd() ?? "";
                                        tp?.WaitForExit(4000);
                                        mine = owner.IndexOf(SessionService.BareName(nodeUser),
                                                   StringComparison.OrdinalIgnoreCase) >= 0;
                                    }

                                    if (!mine) continue;

                                    if (force) p.Kill();
                                    else p.CloseMainWindow();
                                    killed++;
                                }
                                catch { }
                            }
                        }
                        catch (Exception ex) { return (false, ex.Message); }

                        return (killed > 0, killed > 0
                            ? ("已结束 " + killed + " 个 " + pname + " 进程")
                            : ("目标账号下没有 " + pname + " 进程"));
                    }

                    case FlowNodeType.ActDelay:
                    {
                        int sec = Math.Max(0, Math.Min(3600, a.GetInt("seconds", 10)));
                        if (sec > 0) await Task.Delay(TimeSpan.FromSeconds(sec)).ConfigureAwait(false);
                        return (true, "已等待 " + sec + " 秒");
                    }

                    default:
                        return (false, "未知的执行器类型");
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        /// <summary>
        /// 执行鼠键模拟：通过该账号的注入通道在目标会话桌面上操作。
        /// ★ 只作用于目标账号的会话，不影响其他用户。
        /// </summary>
        private static async Task<(bool ok, string msg)> RunInputAsync(
            FlowNode a, int sessionId, AppConfig cfg)
        {
            try
            {
                if (sessionId <= 0) return (false, "目标账号没有会话");

                // 找该会话已有的注入通道
                var relay = RelayClient.FindBySession(sessionId);
                if (relay == null) return (false, "该会话没有注入通道（请先投送）");
                if (!relay.IsConnected) return (false, "注入通道未连接");

                switch (a.Type)
                {
                    case FlowNodeType.ActMouseMove:
                    {
                        int x = a.GetInt("x", 960);
                        int y = a.GetInt("y", 540);
                        var r = await relay.MoveAsync(x, y).ConfigureAwait(false);
                        return (r?.Ok == true, "鼠标移动到 (" + x + "," + y + ")");
                    }

                    case FlowNodeType.ActMouseClick:
                    {
                        int x = a.GetInt("x", 960);
                        int y = a.GetInt("y", 540);
                        string btn = a.Get("button", "left");
                        int cnt = Math.Max(1, Math.Min(5, a.GetInt("count", 1)));

                        await relay.MoveAsync(x, y).ConfigureAwait(false);
                        await Task.Delay(60).ConfigureAwait(false);
                        var r = await relay.ClickAsync(x, y, btn, cnt).ConfigureAwait(false);
                        return (r?.Ok == true, btn + " 点击 x" + cnt + " @(" + x + "," + y + ")");
                    }

                    case FlowNodeType.ActMouseWheel:
                    {
                        int d = a.GetInt("delta", 120);
                        var r = await relay.WheelAsync(d).ConfigureAwait(false);
                        return (r?.Ok == true, "滚轮 " + d);
                    }

                    case FlowNodeType.ActKeyPress:
                    {
                        string keys = a.Get("keys").Trim();
                        if (keys.Length == 0) return (false, "没填按键");
                        int times = Math.Max(1, Math.Min(10, a.GetInt("times", 1)));
                        var r = await relay.KeyAsync(keys, times).ConfigureAwait(false);
                        return (r?.Ok == true, "按键 " + keys + " x" + times);
                    }
                }
                return (false, "未知的输入类型");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        // ── 辅助 ────────────────────────────────────────────────────

        /// <summary>
        /// 在目标账号 BGI 日志的**最近 N 秒**内容里查找匹配。
        /// 用于日志触发器与"日志包含/不含"条件。
        /// </summary>
        private static bool LogMatched(string accountId, AppConfig cfg,
            string pattern, bool isRegex, string level, int withinSeconds)
        {
            try
            {
                var inst = FindBgi(accountId, cfg);
                if (inst == null) return false;

                // 读日志尾部（足够覆盖最近 N 秒）
                string text = BgiLogWatcher.TailLinesDeep(inst.Value.file, 400);
                if (string.IsNullOrEmpty(text)) return false;

                string levelTag = (level ?? "").Trim();
                bool hasLevel = levelTag.Length > 0;

                System.Text.RegularExpressions.Regex? rx = null;
                if (isRegex)
                {
                    try { rx = new System.Text.RegularExpressions.Regex(pattern,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase); }
                    catch { return false; }
                }

                // 只检查最近的若干行（按时间过滤较麻烦，用行数近似）
                var lines = text.Split('\n');
                int from = Math.Max(0, lines.Length - 200);
                for (int i = from; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line.Length == 0) continue;
                    if (hasLevel && line.IndexOf("[" + levelTag + "]", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    bool hit = rx != null
                        ? rx.IsMatch(line)
                        : line.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (hit) return true;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>日志最近 N 秒内是否有错误/失败。</summary>
        private static bool LogHasError(string accountId, AppConfig cfg, int withinSeconds)
        {
            try
            {
                var inst = FindBgi(accountId, cfg);
                if (inst == null) return false;

                // 日志文件长期没更新 → 谈不上"最近的错误"
                if ((DateTime.Now - inst.Value.lastWrite).TotalSeconds > withinSeconds) return false;

                string text = BgiLogWatcher.TailLinesDeep(inst.Value.file, 300);
                if (string.IsNullOrEmpty(text)) return false;

                var lines = text.Split('\n');
                int from = Math.Max(0, lines.Length - 150);
                for (int i = from; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line.Length == 0) continue;
                    if (line.IndexOf("[ERR]", StringComparison.Ordinal) >= 0
                        || line.IndexOf("执行失败", StringComparison.Ordinal) >= 0
                        || line.IndexOf("未成功完成", StringComparison.Ordinal) >= 0
                        || line.IndexOf("发生异常", StringComparison.Ordinal) >= 0)
                        return true;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// 解析某个节点真正要作用的账号 Id。
        /// ★ 节点参数里有 user（用户名）就优先用它，否则用流程绑定的账号。
        ///   用户要求：所有节点都要能指定用户，默认本用户。
        /// </summary>
        private static string TargetAccountId(FlowNode node, string flowAccountId, AppConfig cfg)
        {
            try
            {
                string u = node.Get("user").Trim();
                if (u.Length == 0) return flowAccountId;

                string bare = SessionService.BareName(u);
                foreach (var a in cfg.Accounts)
                    if (string.Equals(a.UserName, bare, StringComparison.OrdinalIgnoreCase))
                        return a.Id;

                // 配置里没有这个账号 → 视为未指定，回退
                return flowAccountId;
            }
            catch { return flowAccountId; }
        }

        /// <summary>解析某个节点的目标会话号（0=该账号没会话）。</summary>
        private static int TargetSessionId(FlowNode node, string flowAccountId, int flowSessionId, AppConfig cfg)
        {
            string id = TargetAccountId(node, flowAccountId, cfg);
            if (id == flowAccountId) return flowSessionId;

            try
            {
                string user = UserOf(id, cfg);
                if (user.Length == 0) return 0;
                var sessions = SessionService.QueryAsync().GetAwaiter().GetResult();
                var s = SessionService.Find(sessions, user);
                return s?.Id ?? 0;
            }
            catch { return 0; }
        }

        private static string UserOf(string accountId, AppConfig cfg)
        {
            foreach (var a in cfg.Accounts) if (a.Id == accountId) return a.UserName;
            return "";
        }

        private static string BgiExeOf(string accountId, AppConfig cfg)
        {
            string user = UserOf(accountId, cfg);
            if (string.IsNullOrEmpty(user)) return "";
            try
            {
                var paths = cfg.Settings.BgiPaths;
                if (paths != null && paths.TryGetValue(user, out var p)) return p ?? "";
            }
            catch { }
            return "";
        }

        /// <summary>找该账号的 BGI 实例（进程 + 日志写入时间）。</summary>
        private static (string file, DateTime lastWrite)? FindBgi(string accountId, AppConfig cfg)
        {
            try
            {
                string user = UserOf(accountId, cfg);
                if (string.IsNullOrEmpty(user)) return null;

                var sessions = SessionService.QueryAsync().GetAwaiter().GetResult();
                var s = SessionService.Find(sessions, user);
                if (s == null) return null;

                var all = BgiLogWatcher.Discover();
                if (!all.TryGetValue(s.Id, out var inst)) return null;
                return (inst.LogFile, inst.LastWrite);
            }
            catch { return null; }
        }

        /// <summary>
        /// 检查系统里有没有这个进程（任何用户 / 任何会话）。
        /// ★ Process.GetProcessesByName 只看得到"当前会话"之外的大部分进程，
        ///   但跨用户查询权限受限；这里用 WMI 兜底，能拿到全部进程及其所属用户。
        /// </summary>
        private static bool ProcessExistsAnywhere(string name)
        {
            // 先试标准 API（快）
            try
            {
                var arr = Process.GetProcessesByName(name);
                if (arr.Length > 0)
                {
                    foreach (var p in arr) { try { p.Dispose(); } catch { } }
                    return true;
                }
            }
            catch { }

            // tasklist 兜底：不受会话/用户限制
            try
            {
                var psi = new ProcessStartInfo("tasklist.exe",
                    "/FI \"IMAGENAME eq " + name + ".exe\" /NH /FO CSV")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = SystemLauncher.ConsoleEncoding()
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);

                // 有匹配时输出形如 "YuanShen.exe","1234","Console","1","1,234 K"
                return outp.IndexOf(name + ".exe", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { }

            return false;
        }

        /// <summary>
        /// 检查某个用户名下有没有这个进程（跨会话、跨用户）。
        /// 用 tasklist 的 /V /FO CSV 输出，第五列就是用户名。
        /// </summary>
        private static bool ProcessExistsForUser(string processName, string userName)
        {
            try
            {
                string bare = SessionService.BareName(userName);

                var psi = new ProcessStartInfo("tasklist.exe",
                    "/FI \"IMAGENAME eq " + processName + ".exe\" /V /FO CSV /NH")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = SystemLauncher.ConsoleEncoding()
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(8000);

                foreach (var line in outp.Split('\n'))
                {
                    if (line.IndexOf(processName + ".exe", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (line.IndexOf(bare, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
            catch { }
            return false;
        }

        private static Process? ProcessOfSession(string name, int sessionId)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { if (p.SessionId == sessionId) return p; } catch { }
                }
            }
            catch { }
            return null;
        }
    }
}
