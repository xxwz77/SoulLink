using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SoulLink.Models;
using System.Threading;

namespace SoulLink.Services
{
    /// <summary>PLC 扫描引擎的系统适配器：读取触点、调用 OUT。布尔运算见 Models/PlcLogic.cs。</summary>
    public static class FlowRunner
    {
        public static event Action<AutomationFlow>? Scanned;

        /// <summary>
        /// 正在等待注入通道（动作标题, 提示文字）。提示文字为空 = 等待结束。
        ///
        /// ★ 用户要求"标黄灯，保持提示"：等待期间灯要显示为黄（等待中），
        ///   而不是红色失败。界面订阅这个事件更新流程的等待态。
        /// </summary>
        public static event Action<string, string>? ReportChannelWait;
        public static DateTime? LastScanAt { get; private set; }
        private static readonly HashSet<string> Running = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Lock Sync = new();

        public sealed class RunResult
        {
            public bool Triggered;
            public bool Passed;
            public string Message = "";
            public List<string> Steps = new();
            public Dictionary<string, bool> ContactLit { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        public static async Task<RunResult> RunAsync(AutomationFlow flow, int sessionId, AppConfig cfg, bool manual = false)
        {
            var account = cfg.Accounts.FirstOrDefault(a => a.Id == flow.AccountId);
            // ★★ 这里**不**按 AccountScope 拦截。★★
            //
            //   「仅管理挂机账号」的语义是「界面只显示挂机账号、不把主用户当投送目标」，
            //   不是「禁止主用户运行自动化」。
            //
            //   用户原话：「不能停啊，它是主控」
            //   —— 主用户是操作者自己的桌面，它的自动化恰恰是最常用的控制入口，
            //      拦掉等于把主控废了（而且定时触发会静默失败，极难发现）。
            //
            //   真正需要拦的是「把主用户的桌面当挂机目标去投送」，
            //   那在 SystemLauncher / ActionService 的启动路径上已经拦了。
            flow.EnsurePlcModel();
            if (flow.MigrationNotes.Count > 0)
                LogBus.Warn("自动化「" + flow.DisplayName + "」已升级流程格式", string.Join("\n", flow.MigrationNotes));
            if (!manual)
            {
                var validation = FlowValidator.Validate(flow, cfg);
                if (validation.HasError) return new RunResult { Message = "流程结构不完整，无法运行\n" + validation.ToText() };
            }
            lock (Sync) { if (!Running.Add(flow.Id)) return new RunResult { Message = "流程正在扫描，已跳过本次重入" }; }
            try
            {
                DateTime time = DateTime.Now;
                string stateBefore = System.Text.Json.JsonSerializer.Serialize(flow);
                if (ScheduleRunnerHost.IsShuttingDown) return new RunResult { Message = "应用正在退出，已跳过扫描" };

                // ★★ 2.17.33：通道门控 —— 先建立通道，**再做条件判断** ★★
                //
                //   用户实测反馈："运行条件没用跟随，运行条件应该在建立通道后进行判断。
                //   现在是运行条件先判断，通道完成立刻执行。"
                //
                //   原来是先 ScanAsync（条件判定 + 动作执行），通道没就绪才在
                //   RunInputAsync 里等 —— 这等于：
                //     · 条件用的是**通道建立之前**的采样值；
                //     · 通道一就绪就立刻把"当时判定通过"的动作补发出去。
                //   对「定时·间隔」这种脉冲型触点尤其致命：间隔到点的脉冲
                //   在通道还没好时就已经点亮并锁存（SatisfiedContacts / ContactPulseAt），
                //   通道建好后立刻补执行一次 —— 这次执行并不代表"现在条件满足"。
                //
                //   正确语义：本流程要用到注入通道时，**先确保通道就绪**，
                //   再在通道就绪的**当前时刻**重新判定条件；
                //   判定不通过就什么都不做（不许补发）。
                //
                //   注意：只对"真的要用通道"的流程做门控（见 UsesInjection），
                //   纯本地动作（等待/启动进程/关 BGI 等）不该被通道阻塞。
                if (!manual && UsesInjection(flow))
                {
                    var gate = await EnsureChannelBeforeScanAsync(flow, sessionId, cfg).ConfigureAwait(false);
                    if (!gate.Ready)
                    {
                        // 通道还没好：**本轮不判定条件、不执行动作**，
                        // 只把等待态挂到流程上让界面显示黄灯 + 提示。
                        flow.EnsurePlcModel();
                        var skipped = new RunResult { Message = gate.Message };
                        LastScanAt = DateTime.Now;
                        return skipped;
                    }
                    // ★ 通道刚建立：必须重新取时间，让条件在"通道就绪之后"判定。
                    time = DateTime.Now;
                }

                var r = await PlcScanEngine.ScanAsync(flow,
                    n => ReadContactAsync(n, flow, sessionId, cfg, manual, time, mutable: true),
                    n => WriteOutputAsync(n, flow, sessionId, cfg), manual,
                    now: time, checkpoint: async _ => { if (cfg.Automations.Contains(flow)) await Task.Run(() => ConfigStore.Save(cfg)).ConfigureAwait(false); }).ConfigureAwait(false);
                flow.EnsurePlcModel();
                string stateAfter = System.Text.Json.JsonSerializer.Serialize(flow);
                if (stateBefore != stateAfter && cfg.Automations.Contains(flow)) ConfigStore.Save(cfg);
                LastScanAt = DateTime.Now;
                Scanned?.Invoke(flow);
                if (manual && cfg.Automations.Contains(flow)) ConfigStore.Save(cfg);
                return r;
            }
            finally { lock (Sync) Running.Remove(flow.Id); }
        }

        /// <summary>
        /// 本流程是否**真的要用注入通道**（模拟按键 / 鼠标移动 / 点击 / 滚轮）。
        ///
        /// ★ 只有这类动作才需要通道门控；纯本地动作（等待、启动进程、关 BGI、
        ///   启动一条龙……）不该因为通道没建好就整体卡住。
        /// </summary>
        private static bool UsesInjection(AutomationFlow flow)
        {
            foreach (var n in flow.Nodes)
            {
                if (n.Kind != FlowNodeKind.Action) continue;
                if (n.Type == FlowNodeType.ActKeyPress || n.Type == FlowNodeType.ActMouseMove
                    || n.Type == FlowNodeType.ActMouseClick || n.Type == FlowNodeType.ActMouseWheel)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 扫描前确保目标会话的注入通道就绪。
        ///
        /// 返回 Ready=false 表示"本轮跳过条件判定"：调用方**不得**执行任何动作，
        /// 只把等待态挂到流程上（界面黄灯 + 提示）。
        /// </summary>
        private static async Task<(bool Ready, string Message)> EnsureChannelBeforeScanAsync(
            AutomationFlow flow, int flowSessionId, AppConfig cfg)
        {
            // 找本流程里第一个注入类动作，取它的真实目标会话号（可能指定了别的账号）。
            FlowNode? target = null;
            foreach (var n in flow.Nodes)
            {
                if (n.Kind != FlowNodeKind.Action) continue;
                if (n.Type == FlowNodeType.ActKeyPress || n.Type == FlowNodeType.ActMouseMove
                    || n.Type == FlowNodeType.ActMouseClick || n.Type == FlowNodeType.ActMouseWheel)
                { target = n; break; }
            }
            if (target == null) return (true, "");

            string accountId = TargetAccountId(target, flow.AccountId, cfg);
            var state = AccountStateCenter.Get(accountId) ?? AccountStateCenter.GetByUser(
                cfg.Accounts.FirstOrDefault(a => a.Id == accountId)?.UserName ?? "");
            int sid = TargetSessionId(target, flow.AccountId, flowSessionId, cfg);
            if (sid <= 0) sid = state?.SessionId ?? 0;

            // ★★ 会话不存在 ≠ 可以放行 ★★
            //
            //   实测（2026-11-20 17:16）：A2 的会话号取不到时这里直接 return(true)，
            //   于是条件照样判定、动作照样执行 → "目标账号没有会话" 失败，
            //   拦不住"先判定条件、通道完成再补执行"的问题。
            //   没有会话就说明**通道不可能就绪**，必须挡住，
            //   并且给出明确原因（而不是让动作层报一句听不懂的"没有会话"）。
            if (sid <= 0)
            {
                string who = cfg.Accounts.FirstOrDefault(a => a.Id == accountId)?.UserName ?? accountId;
                flow.WaitingChannel = "等待「" + who + "」的会话上线 / 通道建立…";
                bool hasSession = state != null && state.HasSession;
                return (false, hasSession
                    ? "目标账号的会话号尚未就绪，本轮不判定条件（不会补发）"
                    : "目标账号当前没有登录会话，本轮不判定条件（不会补发）");
            }

            var relay = RelayClient.FindBySession(sid);
            if (relay != null && relay.IsConnected)
            {
                flow.WaitingChannel = "";
                return (true, "");
            }

            flow.WaitingChannel = "等待注入通道建立（会话 " + sid + "）…";
            FlowRunner.ReportChannelWait?.Invoke(target.Title, "等待注入通道建立…");
            LogBus.Info("等待注入通道（门控）",
                "「" + flow.DisplayName + "」本轮先不判定条件，等会话 " + sid + " 的通道就绪");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ChannelWaitMs)
            {
                if (ScheduleRunnerHost.IsShuttingDown)
                {
                    flow.WaitingChannel = "";
                    return (false, "应用正在退出，已取消等待通道");
                }
                await Task.Delay(250).ConfigureAwait(false);
                relay = RelayClient.FindBySession(sid);
                if (relay != null && relay.IsConnected)
                {
                    flow.WaitingChannel = "";
                    FlowRunner.ReportChannelWait?.Invoke(target.Title, "");
                    LogBus.Info("通道已就绪（门控）",
                        "「" + flow.DisplayName + "」等待 " + sw.ElapsedMilliseconds + "ms 后开始判定条件");
                    return (true, "");
                }
            }

            flow.WaitingChannel = "";
            FlowRunner.ReportChannelWait?.Invoke(target.Title, "");
            return (false,
                "等待注入通道超时（" + (ChannelWaitMs / 1000) + " 秒）：会话 " + sid
                + " 仍未建立通道，本轮不判定条件（不会补发）");
        }

        internal static Task<PlcSignal> ReadPreviewContactAsync(FlowNode n, AutomationFlow flow, int sessionId, AppConfig cfg, DateTime time)
            => ReadContactAsync(n, flow, sessionId, cfg, false, time, mutable: false);

        private static async Task<PlcSignal> ReadContactAsync(FlowNode n, AutomationFlow flow, int flowSessionId, AppConfig cfg, bool manual, DateTime time, bool mutable)
        {
            string accountId = TargetAccountId(n, flow.AccountId, cfg);
            int sid = TargetSessionId(n, flow.AccountId, flowSessionId, cfg);
            string user = UserOf(accountId, cfg);
            if (n.Get("user").Length > 0 && accountId.Length == 0) return new PlcSignal(false,false);
            var state = AccountStateCenter.Get(accountId) ?? AccountStateCenter.GetByUser(user);
            if (state == null && n.Type != FlowNodeType.TrigSchedule && n.Type != FlowNodeType.TrigInterval && n.Type != FlowNodeType.TrigManual && n.Type != FlowNodeType.CondTimeRange && n.Type != FlowNodeType.CondCompare && n.Type != FlowNodeType.CondLastResult && n.Type != FlowNodeType.CondChoice)
                return new PlcSignal(false,false);
            var now = time;
            switch (n.Type)
            {
                case FlowNodeType.TrigSchedule:
                {
                    // ★★ 2.17.41：定时·时刻支持「递增延时 + 复位周期」★★
                    //
                    //   用户需求原话：
                    //     "定时时刻里应该增加一个 下次执行延时，然后还要一个复位周期，
                    //      多少天可用清空这个延时。比如 4:00 4:01 4:02 4:03...
                    //      多少天后重置为 4:00。时刻需要精确到秒"
                    //
                    //   计算方式：
                    //     第 N 次触发（N 从 0 开始）的时刻 = 基准时刻 + N × 延时步长
                    //     N 达到「复位周期」后归零 → 回到基准时刻
                    //   例：基准 04:00:00、延时 60 秒、复位周期 3 →
                    //     第1次 04:00:00，第2次 04:01:00，第3次 04:02:00，
                    //     第4次又回 04:00:00。
                    //
                    //   为什么用"次数"而不是"按天累加"：
                    //     用户的真实诉求是"每次比上次晚一点，攒够一批就回到原点"，
                    //     用次数最直观，也不受补跑/漏跑影响。
                    if (!TryParseClock(n.Get("time", "04:00:00"), out var baseAt)) return false;

                    int stepSeconds = Math.Max(0, n.GetInt("stepSeconds", 0));
                    int resetAfter = Math.Max(0, n.GetInt("resetAfter", 0));

                    // 当前轮次（同一业务日内有效；跨日归零）
                    string dayKey = PlcLogic.DayKey(now, flow.DayBoundaryHour);
                    int fireCount = 0;
                    if (flow.ScheduleCycleDay.TryGetValue(n.Id, out var cycle)
                        && string.Equals(cycle, dayKey, StringComparison.Ordinal))
                        fireCount = flow.ScheduleFireCount.TryGetValue(n.Id, out var fc) ? fc : 0;

                    // 复位周期到了就回到基准时刻
                    if (resetAfter > 0 && fireCount >= resetAfter) fireCount = 0;

                    var target = now.Date.Add(baseAt).AddSeconds((double)fireCount * stepSeconds);
                    if (now < target || (flow.CreatedAt.HasValue && target < flow.CreatedAt.Value)) return false;

                    int catchUp = n.GetInt("catchUpHours", 12);
                    if (catchUp > 0 && (now - target).TotalHours > catchUp) return false;

                    // ★ 记录"这一轮已经触发了"，供下一轮计算延时用。
                    //   只在真正满足条件时递增，避免不满足时也被计数。
                    if (stepSeconds > 0)
                    {
                        flow.ScheduleCycleDay[n.Id] = dayKey;
                        flow.ScheduleFireCount[n.Id] = fireCount + 1;
                    }

                    // 电平持续亮，OUT 的上升沿负责只执行一次；END 负责整日锁存。
                    return true;
                }
                case FlowNodeType.TrigInterval:
                {
                    if (flow.RunDayKey.Length == 0) return false; // 只读预览不能修改计时间隔游标。
                    int seconds = Math.Max(1, n.GetInt("intervalSeconds", 600));
                    if (flow.ContactPulseAt.TryGetValue(n.Id, out var pulse) && (now - pulse).TotalSeconds < 2) return true;
                    if (flow.ContactPulseAt.TryGetValue(n.Id, out var last) && (now - last).TotalSeconds < seconds) return false;
                    flow.ContactPulseAt[n.Id] = now;
                    return true;
                }
                case FlowNodeType.TrigManual: return manual;
                case FlowNodeType.TrigSessionOnline:
                    return state?.HasSession == true && !string.Equals(state.SessionState, "Disc", StringComparison.OrdinalIgnoreCase);
                case FlowNodeType.TrigBgiIdle:
                    return state?.BgiRunning == true && (now - state.BgiLogWrite).TotalSeconds >= n.GetInt("idleSeconds", 300);
                case FlowNodeType.TrigScriptGroup:
                case FlowNodeType.CondScriptGroupState:
                    return GroupMatches(n, accountId, state, flow, now);
                case FlowNodeType.TrigDogfoodRoute:
                case FlowNodeType.CondDogfoodRoute:
                {
                    // ★ 狗粮保持 04:00 业务日（用户确认），数据直接从 AccountState 取。
                    var route = state?.DogfoodRoute ?? DogfoodEndingRoute.Unknown;
                    return route != DogfoodEndingRoute.Unknown && (route == DogfoodEndingRoute.EndingA) == n.GetBool("expectA", false);
                }
                case FlowNodeType.CondBgiRunning: return state != null && (state.BgiRunning == n.GetBool("expect", true));
                case FlowNodeType.CondBgiActive: return state?.BgiRunning == true && (now - state.BgiLogWrite).TotalSeconds <= n.GetInt("withinSeconds", 120);
                case FlowNodeType.CondGenshinRunning:
                    if (state == null) return false;
                    return ProcessOwnerSnapshot.Exists("YuanShen", user, sid) == n.GetBool("expect", true);
                case FlowNodeType.CondChoice: return true;
                case FlowNodeType.CondTimeRange:
                {
                    if (!TimeSpan.TryParse(n.Get("from", "00:00"), out var from) || !TimeSpan.TryParse(n.Get("to", "23:59"), out var to)) return false;
                    return from <= to ? now.TimeOfDay >= from && now.TimeOfDay <= to : now.TimeOfDay >= from || now.TimeOfDay <= to;
                }
                case FlowNodeType.TrigLogMatch:
                case FlowNodeType.CondLogContains:
                case FlowNodeType.CondLogMissing:
                {
                    // ★ 统一从 BgiLogCenter 的尾窗取原文（唯一保留原文的地方）。
                    //   未绑定 BGI 路径 → 没有数据 → 条件不满足（编辑器里已不列出这类账号）。
                    string text = state?.RecentLog ?? "";
                    int within = Math.Max(1, n.GetInt("withinSeconds", n.Type == FlowNodeType.TrigLogMatch ? 10 : 120));

                    // ★★ 事件锁存（用户方案）：TrigLogMatch 改成「出现即锁存，动作消费后清空」，
                    //   不再按最近 N 秒时间窗判断，解决「抓日志只能触发一次」。
                    //   CondLogContains / CondLogMissing 保持电平语义（见 LOG-LATCH-DESIGN）。
                    if (n.Type == FlowNodeType.TrigLogMatch)
                    {
                        var tail0 = state?.LogTail ?? Array.Empty<BgiLogLine>();
                        var refs = new LogLineRef[tail0.Count];
                        for (int i = 0; i < tail0.Count; i++) refs[i] = new LogLineRef(tail0[i].Seq, tail0[i].Level, tail0[i].Text);
                        return LogLatch.Scan(flow, n, refs, state?.LogTailSeq ?? 0, now, mutable);
                    }

                    if (text.Length == 0 || !PlcLogMatcher.ValidPattern(n.Get("pattern"), n.GetBool("isRegex", false))) return new PlcSignal(false,false);
                    bool hit = PlcLogMatcher.Contains(text, n.Get("pattern"), n.GetBool("isRegex", false), n.Get("level"), within, now);
                    return n.Type == FlowNodeType.CondLogMissing ? !hit : hit;
                }
                case FlowNodeType.CondLogHasError:
                {
                    string text = state?.RecentLog ?? "";
                    return PlcLogMatcher.HasError(text, Math.Max(1, n.GetInt("withinSeconds", 300)), now);
                }
                case FlowNodeType.CondProcessExists:
                {
                    string p = n.Get("process").Trim(); if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) p = p[..^4];
                    bool exists = ProcessOwnerSnapshot.Exists(p, user);
                    return exists != n.Get("expect", "存在").Equals("不存在", StringComparison.OrdinalIgnoreCase);
                }
                case FlowNodeType.CondBgiLiveState:
                {
                    bool running = state?.BgiRunning == true;
                    bool output = running && (now - state!.BgiLogWrite).TotalMinutes <= Math.Max(1,n.GetInt("idleMinutes",5));
                    return n.Get("expect", "running") switch { "running" => output, "idle" => running && !output, "stopped" => !running, _ => false };
                }
                case FlowNodeType.CondSessionState:
                {
                    if (state == null || !state.HasSession) return n.Get("expect", "active") == "none";
                    bool active = !string.Equals(state.SessionState, "Disc", StringComparison.OrdinalIgnoreCase);
                    return n.Get("expect", "active") == "active" ? active : n.Get("expect") == "disc" ? !active : false;
                }
                case FlowNodeType.CondWindowForeground:
                {
                    if (sid <= 0) return false;
                    var relay = RelayClient.FindBySession(sid); if (relay == null) return false;
                    var response = await relay.ForegroundAsync().ConfigureAwait(false);
                    return response?.Ok == true && (n.Get("title").Length == 0 || (response.Message ?? "").Contains(n.Get("title"), StringComparison.OrdinalIgnoreCase));
                }
                case FlowNodeType.CondLastResult:
                {
                    var last = PreviousAction(flow, n);
                    if (last == null) return false;
                    return n.Get("expect", "ok") == "any" || (n.Get("expect") == "ok") == last.Ok;
                }
                case FlowNodeType.CondCompare:
                {
                    string left = n.Get("left", "now"); double lv;
                    if (left == "now" || left == "nowTime") lv = now.Hour * 60 + now.Minute;
                    else if (left == "lastElapsed") { var last = PreviousAction(flow, n); if (last == null) return false; lv = last.ElapsedSeconds; }
                    else if (!double.TryParse(left, out lv)) return false;
                    double rv; if (left == "now" || left == "nowTime") { if (!TryParseTime(n.Get("right"), out rv)) return false; }
                    else if (!double.TryParse(n.Get("right"), out rv)) return false;
                    return n.Get("op", ">") switch { ">" => lv > rv, ">=" => lv >= rv, "<" => lv < rv, "<=" => lv <= rv, "==" => Math.Abs(lv-rv)<.0001, "!=" => Math.Abs(lv-rv)>=.0001, _ => false };
                }
                default: return false;
            }
        }

        private static bool GroupMatches(FlowNode n, string accountId, AccountState? state, AutomationFlow flow, DateTime now)
        {
            string expect = n.Get("expect", "finished");
            if (state == null || n.Get("group").Length == 0) return false;
            // ★ 配置组状态来自 BgiLogCenter 快照（只读列表，不再每次重建投影）。
            string name = n.Get("group");
            var hit = state.Groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
            var value = hit?.State ?? ScriptGroupState.Never;
            return expect switch { "never" => value == ScriptGroupState.Never, "running" => value == ScriptGroupState.Running,
                "finished" or "done" => value == ScriptGroupState.Finished,
                "failed" => value == ScriptGroupState.Failed || value == ScriptGroupState.Cancelled || value == ScriptGroupState.Interrupted, _ => false };
        }

        private static FlowActionResult? PreviousAction(AutomationFlow flow, FlowNode contact)
        {
            foreach (var id in flow.SourcesOf(contact.Id))
            {
                var source = flow.FindNode(id);
                if (source != null && flow.ActionResults.TryGetValue(source.Id, out var direct)) return direct;
            }
            return null; // 多上游时没有唯一“上一步”，禁止任选一个结果污染支路。
        }

        private static async Task<FlowActionResult> WriteOutputAsync(FlowNode n, AutomationFlow flow, int sid, AppConfig cfg)
        {
            string account = TargetAccountId(n, flow.AccountId, cfg);
            int targetSid = TargetSessionId(n, flow.AccountId, sid, cfg);
            var (ok, message) = await RunActionAsync(n, account, targetSid, cfg, flow).ConfigureAwait(false);
            return new FlowActionResult { Ok = ok, Message = message };
        }

        /// <summary>
        /// 解析"时刻"：支持 HH:mm:ss（精确到秒）、HH:mm、H:mm:ss。
        ///
        /// ★ 2.17.41：用户要求"时刻需要精确到秒"，所以不能再用 TimeSpan.TryParse
        ///   的单一路径 —— 它接受 "4" 这种非法输入（解析成 4 天）。
        ///   这里严格按 时:分[:秒] 解析，非法输入直接返回 false（流程校验会拦下）。
        /// </summary>
        private static bool TryParseClock(string text, out TimeSpan value)
        {
            value = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text.Trim().Split(':');
            if (parts.Length < 2 || parts.Length > 3) return false;

            if (!int.TryParse(parts[0], out int hh) || hh < 0 || hh > 23) return false;
            if (!int.TryParse(parts[1], out int mm) || mm < 0 || mm > 59) return false;
            int ss = 0;
            if (parts.Length == 3 && (!int.TryParse(parts[2], out ss) || ss < 0 || ss > 59)) return false;

            value = new TimeSpan(hh, mm, ss);
            return true;
        }

        private static bool TryParseTime(string s, out double minutes)
        {
            minutes = 0;
            if (!TimeSpan.TryParse(s, out var t)) return false;
            minutes = t.TotalMinutes; return true;
        }


        // ── 执行器 ──────────────────────────────────────────────────

        private static async Task<(bool ok, string msg)> RunActionAsync(FlowNode a, string accountId, int sessionId, AppConfig cfg, AutomationFlow? flow = null)
        {
            try
            {
                switch (a.Type)
                {
                    case FlowNodeType.ActRunAction:
                    {
                        var act = cfg.Actions.FirstOrDefault(x => x.Id == a.Get("actionId"));
                        if (act == null) return (false, "找不到动作");
                        // 节点目标用户优先；快捷动作自身的配置保留。
                        if (a.Get("user").Length > 0) act = CloneActionForAccount(act, accountId);
                        var r = await ActionService.ExecuteAsync(act, cfg).ConfigureAwait(false);
                        return (r.Ok, r.Message);
                    }
                    case FlowNodeType.ActStopBgi:
                    {
                        var p = await BgiStartGuard.BeforeStartAsync(sessionId, BgiExeOf(accountId, cfg)).ConfigureAwait(false);
                        return (p.Ok, p.Message);
                    }
                    case FlowNodeType.ActStartBgi:
                    {
                        string exe = BgiExeOf(accountId, cfg);
                        if (!File.Exists(exe)) return (false, "找不到 BetterGI.exe");
                        var guard = await BgiStartGuard.BeforeStartAsync(sessionId, exe).ConfigureAwait(false);
                        if (!guard.Ok) return (false, guard.Message);
                        var r = await SystemLauncher.LaunchAsync(sessionId, exe, a.Get("args")).ConfigureAwait(false);
                        return (r.Ok, r.Message);
                    }
                    case FlowNodeType.ActStartOneDragon:
                    case FlowNodeType.ActStartGroups:
                    {
                        string exe = BgiExeOf(accountId, cfg);
                        if (!File.Exists(exe)) return (false, "找不到 BetterGI.exe");
                        string args;
                        if (a.Type == FlowNodeType.ActStartOneDragon)
                        {
                            string name = a.Get("name").Trim().Replace("\"", "");
                            args = name.Length == 0 ? "--startOneDragon" : "--startOneDragon \"" + name + "\"";
                        }
                        else
                        {
                            string groups = a.Get("groups").Trim();
                            if (groups.Length == 0) return (false, "没有填配置组名"); // 校验先于停止旧任务
                            args = "--startGroups \"" + groups.Replace("\"", "") + "\"";
                        }
                        var p = await BgiStartGuard.BeforeStartAsync(sessionId, exe).ConfigureAwait(false);
                        if (!p.Ok) return (false, "停止旧任务失败：" + p.Message);
                        var r = await SystemLauncher.LaunchAsync(sessionId, exe, args).ConfigureAwait(false);
                        return (r.Ok, r.Message);
                    }
                    case FlowNodeType.ActCloseGenshin:
                    {
                        var p = await BgiTaskPreparer.PrepareAsync(sessionId, "", false, true, 1).ConfigureAwait(false);
                        return (p.Ok, p.Message);
                    }
                    case FlowNodeType.ActMouseClick:
                    case FlowNodeType.ActMouseMove:
                    case FlowNodeType.ActMouseWheel:
                    case FlowNodeType.ActKeyPress: return await RunInputAsync(a, sessionId, flow).ConfigureAwait(false);
                    case FlowNodeType.ActJoinRoom:
                    {
                        string code = a.Get("code").Trim(), userName = UserOf(accountId, cfg);
                        if (code.Length == 0) return (false, "没有填写房间码");
                        string display = cfg.Accounts.FirstOrDefault(x => x.Id == accountId)?.DisplayName;
                        if (string.IsNullOrWhiteSpace(display)) display = userName;
                        var r = await LobbyApi.JoinAsync(code, display, "").ConfigureAwait(false);
                        return (r.Ok, r.Message + (r.Room != null ? "（" + r.Room.Title + "）" : ""));
                    }
                    case FlowNodeType.ActLaunchProcess:
                    {
                        string path = a.Get("path").Trim();
                        if (!File.Exists(path)) return (false, "程序不存在：" + path);
                        var r = await SystemLauncher.LaunchAsync(sessionId, path, a.Get("args")).ConfigureAwait(false);
                        return (r.Ok, r.Message);
                    }
                    case FlowNodeType.ActKillProcess:
                    {
                        string name = a.Get("process").Trim();
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                        if (name.Length == 0) return (false, "没有填写进程名");
                        int killed = 0;
                        foreach (var p in Process.GetProcessesByName(name))
                        {
                            using (p)
                            {
                                try
                                {
                                    bool mine = sessionId > 0 && p.SessionId == sessionId;
                                    if (!mine && a.Get("user").Length > 0) mine = ProcessOwnerSnapshot.Items.Any(x => x.Pid == p.Id
                        && string.Equals(SessionService.BareName(x.UserName), SessionService.BareName(a.Get("user")), StringComparison.OrdinalIgnoreCase));
                                    if (!mine) continue;
                                    if (a.GetBool("force", true)) { p.Kill(); p.WaitForExit(5000); }
                                    else if (!p.CloseMainWindow()) continue;
                                    killed++;
                                }
                                catch { }
                            }
                        }
                        // 已经是目标状态也算成功；OUT 不应因“没有可关闭进程”永久失败。
                        return (true, killed > 0 ? "已请求结束 " + killed + " 个 " + name + " 进程" : "目标账号下没有 " + name + " 进程");
                    }
                    // ★★ 3.2.8：录制回放执行器 ★★
                    case FlowNodeType.ActScreenshot:
                    {
                        string tag = UserTagOf(accountId, cfg);
                        string trig = a.Get("trigger").Trim();
                        var relay0 = SoulLink.Services.RelayClient.FindBySession(sessionId);
                        if (relay0 == null || !relay0.IsConnected)
                            return (false, "注入通道未就绪，无法截图");
                        var r0 = await relay0.ScreenshotAsync(tag, trig).ConfigureAwait(false);
                        return (r0?.Ok == true, r0?.Ok == true ? (r0.Message ?? "已截图") : (r0?.Message ?? "截图失败"));
                    }

                    case FlowNodeType.ActSaveRecording:
                    {
                        string tag = UserTagOf(accountId, cfg);
                        string trig = a.Get("trigger").Trim();

                        // ★ 用户要求：前后时长不得大于 RDP 通用设置里的保留时长。
                        //   前 N + 后 N = 2N，所以 N 最大 = 保留时长 / 2。
                        int keep = Math.Max(5, App.Config.Settings.RecordKeepSeconds);
                        int maxN = Math.Max(1, keep / 2);
                        int n = Math.Clamp(a.GetInt("seconds", 10), 1, maxN);
                        string clamped = n < a.GetInt("seconds", 10)
                            ? "（已按保留时长 " + keep + " 秒限制为 " + n + " 秒）" : "";

                        var relay1 = SoulLink.Services.RelayClient.FindBySession(sessionId);
                        if (relay1 == null || !relay1.IsConnected)
                            return (false, "注入通道未就绪，无法保存录像");

                        var r1 = await relay1.SaveRecordingAsync(tag, trig, n, n).ConfigureAwait(false);
                        return (r1?.Ok == true,
                            (r1?.Ok == true ? (r1.Message ?? "已保存录像") : (r1?.Message ?? "保存失败")) + clamped);
                    }

                    case FlowNodeType.ActDelay:
                    {
                        int seconds = Math.Clamp(a.GetInt("seconds", 10), 0, 3600);
                        if (seconds > 0) await Task.Delay(TimeSpan.FromSeconds(seconds), ScheduleRunnerHost.ShutdownCts.Token).ConfigureAwait(false);
                        return (true, "已等待 " + seconds + " 秒");
                    }
                    default: return (false, "未知的执行器类型");
                }
            }
            catch (OperationCanceledException) { return (false, "应用退出，动作已取消"); }
            catch (Exception ex) { return (false, ex.Message); }
        }

        private static ActionItem CloneActionForAccount(ActionItem source, string accountId)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(source);
            var copy = System.Text.Json.JsonSerializer.Deserialize<ActionItem>(json) ?? source;
            copy.AccountId = accountId;
            return copy;
        }

        /// <summary>
        /// 等待注入通道就绪的最长时间（毫秒）。
        ///
        /// ★ 用户要求："执行要加个判断，需要等待通道建立，标黄灯，保持提示，
        ///   等到通道真正建立。"
        ///   RDP 刚连上时注入代理还在投送中（投送 + 连管道通常 1~5 秒，
        ///   Broker 模式还要等服务起进程），这段时间通道 `IsConnected=false`。
        ///   原来直接 return(false) → 动作判失败 → 灯变红 → 用户以为坏了。
        ///   现在改成"等待"：在窗口内轮询，通道一就绪立刻继续执行。
        /// </summary>
        internal const int ChannelWaitMs = 20000;

        /// <summary>
        /// 在 ChannelWaitMs 内等目标会话的注入通道就绪。
        /// 返回 null = 就绪；非 null = 等待超时的原因。
        /// </summary>
        private static async Task<string?> WaitForChannelAsync(int sid, string actionTitle, AutomationFlow? flow = null)
        {
            var relay = RelayClient.FindBySession(sid);
            if (relay != null && relay.IsConnected) return null;

            if (flow != null) flow.WaitingChannel = "等待注入通道建立（会话 " + sid + "）…";
            FlowRunner.ReportChannelWait?.Invoke(actionTitle, "等待注入通道建立…");
            LogBus.Info("等待注入通道", "会话 " + sid + " · 动作「" + actionTitle + "」排队等待通道就绪");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ChannelWaitMs)
            {
                if (ScheduleRunnerHost.IsShuttingDown) return "应用正在退出，已取消等待通道";
                await Task.Delay(250).ConfigureAwait(false);
                relay = RelayClient.FindBySession(sid);
                if (relay != null && relay.IsConnected)
                {
                    if (flow != null) flow.WaitingChannel = "";
                    FlowRunner.ReportChannelWait?.Invoke(actionTitle, "");
                    LogBus.Info("通道已就绪", "会话 " + sid + " · 等待 " + sw.ElapsedMilliseconds + "ms 后继续执行「" + actionTitle + "」");
                    return null;
                }
            }

            if (flow != null) flow.WaitingChannel = "";
            FlowRunner.ReportChannelWait?.Invoke(actionTitle, "");
            return "等待注入通道超时（" + (ChannelWaitMs / 1000) + " 秒）：该会话仍未建立通道，请检查投送状态后重试";
        }

        private static async Task<(bool ok, string msg)> RunInputAsync(FlowNode a, int sid, AutomationFlow? flow = null)
        {
            try
            {
                if (sid <= 0) return (false, "目标账号没有会话");

                // ★ 先等通道就绪（黄灯 + 提示），而不是立刻判失败。
                string? waitError = await WaitForChannelAsync(sid, a.Title, flow).ConfigureAwait(false);
                if (waitError != null) return (false, waitError);

                var relay = RelayClient.FindBySession(sid);
                if (relay == null || !relay.IsConnected) return (false, "该会话没有已连接的注入通道（请先投送）");
                switch (a.Type)
                {
                    case FlowNodeType.ActMouseMove:
                    {
                        var (mx, my) = ResolvePoint(a, relay);
                        var r = await relay.MoveAsync(mx, my).ConfigureAwait(false);
                        return (r?.Ok == true, r?.Ok == true ? "鼠标已移动到 " + mx + "," + my : r?.Message ?? "鼠标移动失败");
                    }
                    case FlowNodeType.ActMouseClick:
                    {
                        var (cx, cy) = ResolvePoint(a, relay);
                        int x = cx, y = cy, count = Math.Clamp(a.GetInt("count", 1), 1, 5);
                        var moved = await relay.MoveAsync(x, y).ConfigureAwait(false);
                        if (moved?.Ok != true) return (false, moved?.Message ?? "点击前移动鼠标失败");
                        await Task.Delay(60, ScheduleRunnerHost.ShutdownCts.Token).ConfigureAwait(false);
                        var r = await relay.ClickAsync(x, y, a.Get("button", "left"), count).ConfigureAwait(false);
                        return (r?.Ok == true, r?.Ok == true ? "点击完成" : r?.Message ?? "点击失败");
                    }
                    case FlowNodeType.ActMouseWheel:
                    {
                        var r = await relay.WheelAsync(a.GetInt("delta", 120)).ConfigureAwait(false);
                        return (r?.Ok == true, r?.Ok == true ? "滚轮完成" : r?.Message ?? "滚轮失败");
                    }
                    case FlowNodeType.ActKeyPress:
                    {
                        if (a.Get("keys").Trim().Length == 0) return (false, "没填按键");
                        var r = await relay.KeyAsync(a.Get("keys"), Math.Clamp(a.GetInt("times", 1), 1, 10)).ConfigureAwait(false);
                        return (r?.Ok == true, r?.Ok == true ? "按键完成" : r?.Message ?? "按键失败");
                    }
                }
                return (false, "未知输入类型");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        /// <summary>
        /// 解析动作的目标像素坐标。
        ///
        /// ★★ 2.17.37：优先用"图片点选"的归一化比例 × 目标会话实际分辨率 ★★
        ///
        ///   用户在参照截图上点的位置会存成 0~1 的比例（pickX/pickY），
        ///   这里乘以**目标会话当前的真实分辨率**（代理上报的 ScreenWidth/ScreenHeight），
        ///   于是换机器、换分辨率、换截图都还能点对地方 —— 这正是用户要的"自动计算"。
        ///
        ///   没配截图时退回旧的手填 x/y，保持兼容。
        /// </summary>
        private static (int x, int y) ResolvePoint(FlowNode a, RelayClient? relay)
        {
            double nx = a.GetDouble("pickX", -1), ny = a.GetDouble("pickY", -1);
            if (nx >= 0 && nx <= 1 && ny >= 0 && ny <= 1)
            {
                int rw = relay?.ScreenWidth ?? 0, rh = relay?.ScreenHeight ?? 0;
                if (rw <= 0 || rh <= 0) { rw = 1920; rh = 1080; }   // 拿不到分辨率时的兜底
                int px = (int)Math.Round(nx * rw);
                int py = (int)Math.Round(ny * rh);
                // 夹到有效范围，避免点出屏幕
                px = Math.Clamp(px, 0, rw - 1);
                py = Math.Clamp(py, 0, rh - 1);
                return (px, py);
            }
            return (a.GetInt("x", 960), a.GetInt("y", 540));
        }

        private static string TargetAccountId(FlowNode node, string flowAccountId, AppConfig cfg)
        {
            string user = node.Get("user").Trim();
            if (user.Length == 0) return flowAccountId;
            string bare = SessionService.BareName(user);
            // 严格目标解析：配错账号宁可不动作，也不能悄悄操作流程账号。
            return cfg.Accounts.FirstOrDefault(a => string.Equals(a.UserName, bare, StringComparison.OrdinalIgnoreCase))?.Id ?? "";
        }

        private static int TargetSessionId(FlowNode node, string flowAccountId, int flowSid, AppConfig cfg)
        {
            string id = TargetAccountId(node, flowAccountId, cfg);
            if (id == flowAccountId) return flowSid;
            var state = AccountStateCenter.Get(id);
            if (state != null) return state.SessionId;
            return 0;
        }

        private static string UserOf(string id, AppConfig cfg) => cfg.Accounts.FirstOrDefault(a => a.Id == id)?.UserName ?? "";

        /// <summary>录制回放目录用的标签：用户名-昵称。</summary>
        private static string UserTagOf(string id, AppConfig cfg)
        {
            var acc = cfg.Accounts.FirstOrDefault(a => a.Id == id);
            if (acc == null) return "";
            string user = acc.UserName ?? "";
            string nick = acc.DisplayName ?? "";
            if (string.IsNullOrWhiteSpace(nick)) return user;
            return user + "-" + nick;
        }
        private static string BgiExeOf(string id, AppConfig cfg) => BgiPathResolver.GetPath(UserOf(id, cfg));

        private static (string file, DateTime lastWrite)? FindBgi(string id, AppConfig cfg)
        {
            var state = AccountStateCenter.Get(id);
            if (state != null && state.BgiRunning && state.BgiLogFile.Length > 0) return (state.BgiLogFile, state.BgiLogWrite);
            int sid = state?.SessionId ?? 0;
            if (sid <= 0) return null;
            return BgiLogWatcher.Discover().TryGetValue(sid, out var found) ? (found.LogFile, found.LastWrite) : null;
        }

        private static bool ProcessExistsForUser(string name, string user)
        {
            // 兼容旧动作执行器；触点路径全部使用 ProcessOwnerSnapshot，不再逐进程查所有者。
            foreach (var p in Process.GetProcessesByName(name))
                using (p) { if (ProcessOwnerSnapshot.Items.Any(x => x.Pid == p.Id && string.Equals(SessionService.BareName(x.UserName), SessionService.BareName(user), StringComparison.OrdinalIgnoreCase))) return true; }
            return false;
        }

        private static bool ProcessOwnerMatches(int pid, string user)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("tasklist.exe", "/FI \"PID eq " + pid + "\" /V /FO CSV /NH")
                { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, StandardOutputEncoding = SystemLauncher.ConsoleEncoding() });
                string text = p?.StandardOutput.ReadToEnd() ?? ""; p?.WaitForExit(4000);
                return text.Contains(SessionService.BareName(user), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool ProcessOfSession(string name, int sid) => ProcessOwnerSnapshot.Exists(name, "", sid);

        internal static DateTime? GetLastRunAt(string flowId, string nodeId) => null;
        internal static void MarkLastRunAt(string flowId, string nodeId) { }
        internal static void ClearSeenStates(string flowId)
        {
            // 间隔触点游标已持久化在 flow.ContactPulseAt；无全局旧边沿字典需要清理。
        }
    }
}