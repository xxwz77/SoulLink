using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;

namespace SoulLink.Models
{
    /// <summary>一个账号的 PLC 式自动化程序。实时条件逐轮求值，瞬时信号按本轮运行保留。</summary>
    public class AutomationFlow
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "新的自动化";
        public bool? AutoName { get; set; }
        public string AccountId { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public bool Paused { get; set; }
        public string PauseReason { get; set; } = "";
        public List<FlowNode> Nodes { get; set; } = new();
        public List<FlowEdge> Edges { get; set; } = new();
        public DateTime? CreatedAt { get; set; }
        public DateTime? LastRunAt { get; set; }
        public int RunCount { get; set; }

        /// <summary>
        /// 最近一次**真正执行了动作**的结果（成功/失败）。空 = 还没执行过。
        ///
        /// ★ 2.17.23：状态灯要区分"执行成功=绿 / 执行失败=红"，
        ///   而原来的 RunCount 只在"整轮收尾"时才加，失败路径根本不写任何结果，
        ///   界面无从判断上一次是成功还是失败。
        ///   这里在**每次动作执行后**就落盘，与 RunCount 的含义分开。
        /// </summary>
        public bool? LastRunOk { get; set; }
        /// <summary>最近一次执行失败的原因（成功时清空）。</summary>
        public string LastRunError { get; set; } = "";
        /// <summary>最近一次执行里被执行的动作标题（用于提示）。</summary>
        public string LastRunAction { get; set; } = "";
        /// <summary>最近一次动作执行时刻（与 LastRunAt 区分：后者只在整轮收尾时更新）。</summary>
        public DateTime? LastActionResultAt { get; set; }

        // ── 今日执行成功/失败次数（2.17.31）──────────────────────────────
        //
        //   用户要求："在流程名称后面，要小字显示今日成功次数与失败次数。
        //   成功用绿色数字，失败用红色数字。"
        //
        //   ★ 必须按**业务日**重置，不能是累计值：RunCount 是累计的（重启也不清），
        //     直接拿它显示"今日"是错的。
        //   ★ 计数点在"每次动作真正执行完"，与 LastRunOk 同处写入，
        //     这样成功/失败次数之和 = 今天真正跑过的动作次数。

        /// <summary>计数所属业务日（yyyy-MM-dd）。换日时把下面两个计数清零。</summary>
        public string StatDayKey { get; set; } = "";
        /// <summary>今日执行成功次数。</summary>
        public int TodayOkCount { get; set; }
        /// <summary>今日执行失败次数。</summary>
        public int TodayFailCount { get; set; }

        /// <summary>当前业务日键（与 RecordRunResult 同一口径）。</summary>
        public string CurrentStatDayKey(DateTime now)
            => PlcLogic.DayKey(now, PlcLogic.HasEnd(this) ? PlcLogic.EndBoundaryHour(this) : 0);

        /// <summary>
        /// 读取「今日」成功/失败次数。
        /// ★ 2026-11-26：清零原来只发生在下一次执行动作时（RecordRunResult），
        ///   当天没再执行的流程会一直显示好几天前的次数。读取时按业务日判断：
        ///   存的日期不是当前业务日 → 视为 0。界面必须走这里，不要直接读两个字段。
        /// </summary>
        public (int ok, int fail) TodayCounts(DateTime now)
            => string.Equals(StatDayKey, CurrentStatDayKey(now), StringComparison.Ordinal)
                ? (TodayOkCount, TodayFailCount) : (0, 0);

        public bool HasExecutedToday(DateTime now)
        {
            var (ok, fail) = TodayCounts(now);
            if (ok > 0 || fail > 0) return true;

            string day = CurrentStatDayKey(now);
            return (LastActionResultAt is DateTime action && CurrentStatDayKey(action) == day)
                || (LastRunAt is DateTime run && CurrentStatDayKey(run) == day);
        }

        public void ToggleAutomaticRun()
        {
            Paused = Enabled && !Paused;
            Enabled = true;
            ResetScheduleScan();
        }

        /// <summary>
        /// 记录一次执行结果（自动按业务日重置计数）。由 PlcScanEngine 每次动作执行后调用。
        /// </summary>
        public void RecordRunResult(bool ok, DateTime now)
        {
            string key = CurrentStatDayKey(now);
            if (!string.Equals(StatDayKey, key, StringComparison.Ordinal))
            {
                StatDayKey = key;
                TodayOkCount = 0;
                TodayFailCount = 0;
            }
            if (ok) TodayOkCount++; else TodayFailCount++;
        }

        /// <summary>
        /// 正在等待注入通道时的提示（空 = 不在等待）。
        ///
        /// ★ 2.17.30：用户要求"执行要加个判断，需要等待通道建立，标黄灯，保持提示"。
        ///   等待期间灯显示黄色、摘要显示这句话；通道就绪后清空并继续执行。
        ///   [JsonIgnore]：这是瞬时运行态，不该写进配置。
        /// </summary>
        [JsonIgnore] public string WaitingChannel { get; set; } = "";

        /// <summary>2 = 单出口 PLC 扫描模型；旧配置在加载/打开时迁移。</summary>
        public int EngineVersion { get; set; } = 1;
        /// <summary>本轮运行状态格式；1=按业务日持久化，0=需要保守迁移。</summary>
        public int LifecycleVersion { get; set; } = 1;
        /// <summary>兼容旧配置的整体日界；只有 END 节点的 dayBoundaryHour 参与运行判定。</summary>
        public int DayBoundaryHour { get; set; } = 0;
        /// <summary>有 END 的流程在今天完成过；仅由 END 锁存驱动。</summary>
        public bool RunComplete { get; set; }
        /// <summary>本轮运行所属日期；无 END 循环流程用它隔离不同轮次。</summary>
        public string RunDayKey { get; set; } = "";
        /// <summary>兼容旧的“清除今日标记”请求；新界面使用 ToggleTodayRequested。</summary>
        public bool RunResetRequested { get; set; }
        /// <summary>
        /// 用户在 END 节点里手动切换“今日运行状态”的请求：
        /// true=恢复今日运行（清标记）；false=今日不运行（写入今日标记）。
        /// 由下一次扫描消费后清空，保证可重复设置。
        /// </summary>
        public bool? ToggleTodayRequested { get; set; }
        public Dictionary<string, FlowActionResult> RunningActions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> WaitingActions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>兼容旧字段。</summary>
        public string DailyLatchKey { get; set; } = "";

        /// <summary>本轮动作的上次输入；持久化用于等待中的分支恢复，实时条件不入表。</summary>
        public Dictionary<string, bool> PlcPower { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>各 OUT 最近一次结果，供下游“上一步结果/耗时”触点使用。</summary>
        public Dictionary<string, FlowActionResult> ActionResults { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>每个间隔触点的上次脉冲时刻，避免重启或其它支路动作改变计时间隔。</summary>
        public Dictionary<string, DateTime> ContactPulseAt { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // ── 定时·时刻的"递增延时 + 复位周期"（2.17.41）────────────────────
        //
        //   用户需求（原话）：
        //     "定时时刻里应该增加一个 下次执行延时，然后还要一个复位周期，
        //      多少天可用清空这个延时。比如 4:00 4:01 4:02 4:03...多少天后重置为 4:00。
        //      时刻需要精确到秒"
        //
        //   语义：
        //     · 每触发一次，下次时刻 = 基准时刻 + (已触发次数 × 延时步长)
        //     · 累计次数达到"复位周期"后归零，重新从基准时刻开始
        //     · 时刻精确到秒（HH:mm:ss）
        //
        //   为什么要持久化：重启程序后不能把"已经递增到第几次"忘掉，
        //   否则重启就跳回 4:00，延迟策略失效。

        /// <summary>定时触点已触发的累计次数（用于计算递增延时）。</summary>
        public Dictionary<string, int> ScheduleFireCount { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>旧版定时触点的业务日，用于保留升级当天的触发记录。</summary>
        public Dictionary<string, string> ScheduleCycleDay { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>定时触点最后触发的计划基准日。实际时刻可以受递增延时影响。</summary>
        public Dictionary<string, DateTime> ScheduleLastTriggeredDay { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        private long _lastScheduleScanTicks;

        internal DateTime? RecordScheduleScan(DateTime now)
        {
            long previous = Interlocked.Exchange(ref _lastScheduleScanTicks, now.Ticks);
            return previous == 0 ? null : new DateTime(previous, now.Kind);
        }

        internal void ResetScheduleScan() => Interlocked.Exchange(ref _lastScheduleScanTicks, 0);

        /// <summary>TON 等待线圈的截止时刻；输入断开后清除。</summary>
        public Dictionary<string, DateTime> DelayUntil { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>本轮已命中的日志/定时/手动等瞬时信号；等待其它条件时不设超时。</summary>
        public HashSet<string> SatisfiedContacts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 日志触点的「事件锁存」状态：按触点节点 Id 记一个待消费事件 + 已扫描游标。
        /// ★ [JsonIgnore] 只驻内存：用户要求「只要流程在运行，每出现一次就记住」——
        ///   「运行」= 本次进程内。重启后从当前尾窗末尾重新建立游标，不补历史，
        ///   避免重启把当天一堆旧日志重新触发（这也是任务给定的建议）。
        /// </summary>
        [JsonIgnore]
        public Dictionary<string, LogTriggerState> LogTriggers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>本轮实时值，仅用于界面亮/灭及导线通/断，不是历史条件锁存。</summary>
        [JsonIgnore] public Dictionary<string, PlcNodeState> RuntimeStates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        [JsonIgnore] public List<string> MigrationNotes { get; set; } = new();

        // 仅用于读取旧 JSON；新引擎只保留瞬时信号，不锁存实时状态条件。
        public bool Active { get; set; }
        public DateTime? ArmedAt { get; set; }
        public int ArmTimeoutMinutes { get; set; } = 60;
        public List<string> Latched { get; set; } = new();
        public bool PersistLatched { get; set; } = true;
        public FlowConditionMode ConditionMode { get; set; } = FlowConditionMode.All;

        [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "未命名流程" : Name;
        /// <summary>仅“有 END”的流程存在单日锁存；无 END 永远不锁存。</summary>
        [JsonIgnore]
        public FlowNode? EndNode => (Nodes ?? new List<FlowNode>())
            .FirstOrDefault(n => n.Kind == FlowNodeKind.Terminate);
        [JsonIgnore] public bool HasEnd => EndNode != null;
        [JsonIgnore] public int? EndBoundaryHour => EndNode == null ? null : EndNode.DayBoundaryHour;
        [JsonIgnore] public bool IsDailyComplete => HasEnd && PlcLogic.IsDailyComplete(this, DateTime.Now);
        [JsonIgnore] public bool IsRunCompleteToday => IsDailyComplete;
        [JsonIgnore] public string EndLatchText
            => !HasEnd ? "无 END：循环执行" : IsDailyComplete
                ? "今日已执行，日界 " + (EndBoundaryHour ?? 0).ToString("00") + ":00 重置"
                : "今日未执行";

        /// <summary>
        /// 面向用户的“今天还会不会跑”结论。所有界面共用同一套措辞，避免各处说法不一致。
        /// 只描述运行资格，不触发任何动作。
        /// </summary>
        /// <summary>本流程是否只有「手动」触点（= 永远不会自己跑，必须点「立即运行一次」）。</summary>
        [JsonIgnore]
        public bool NeedsManualTrigger
        {
            get
            {
                EnsurePlcModel();
                var contacts = Nodes.Where(n => n.IsContact).ToList();
                return contacts.Count > 0 && contacts.All(n => n.Type == FlowNodeType.TrigManual);
            }
        }

        public (bool WillRun, string Text) TodayRunState(DateTime? now = null)
        {
            var time = now ?? DateTime.Now;
            if (!Enabled) return (false, "今日不会运行：流程未启用");
            if (Paused) return (false, "今日不会运行：流程已暂停" + (string.IsNullOrWhiteSpace(PauseReason) ? "" : "（" + PauseReason + "）"));
            if (HasEnd && PlcLogic.IsDailyComplete(this, time))
                return (false, "今日不会再运行：已完成，日界 " + (EndBoundaryHour ?? 0).ToString("00") + ":00 后恢复");

            // ★ 2.17.22：只有「手动」触点的流程永远不会自己跑。
            //   用户反馈"添加任务之后需要手动按一次开关才会自动执行"——
            //   新建流程默认给的就是「手动」触点，而界面只说"今日会运行"，
            //   完全没提示"它其实要你每轮点一下"，导致以为自动执行坏了。
            if (NeedsManualTrigger)
                return (true, "需要手动触发：只连了「手动」触点，点「立即运行一次」才执行");

            if (!HasEnd)
                return (true, "今日会运行：无「每天完成」，条件满足即可重复执行");
            return (true, "今日还会运行：每天一次，尚未执行");
        }

        /// <summary>所有无上游的触点；每一个都从逻辑母线开始（LD/LD NOT）。</summary>
        [JsonIgnore]
        public List<FlowNode> EntryPoints => Nodes.Where(n => n.IsContact && SourcesOf(n.Id).Count == 0).ToList();

        [JsonIgnore]
        public FlowNode? Trigger
        {
            get { EnsurePlcModel(); return EntryPoints.FirstOrDefault(); }
        }

        [JsonIgnore] public bool TriggerIsImplicit => false;

        public List<string> TargetsOf(string id) => Edges.Where(e => e.From == id)
            .Select(e => e.To).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        public List<string> TargetsOf(string id, bool? result) => TargetsOf(id);
        public List<string> SourcesOf(string id) => Edges.Where(e => e.To == id)
            .Select(e => e.From).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        public FlowNode? FindNode(string id) => Nodes.FirstOrDefault(n => n.Id == id);

        internal AutomationFlow CopyCurrentState() => (AutomationFlow)MemberwiseClone();

        public bool IsLatched(string id) => false;
        public bool Latch(string id) => false;
        public void Arm() { }
        public void Disarm()
        {
            Active = false; ArmedAt = null; Latched.Clear();
        }

        /// <summary>把旧触发器/是非分支图转换为单出口触点图，可重复调用。</summary>
        public void EnsurePlcModel()
        {
            Nodes ??= new List<FlowNode>();
            Edges ??= new List<FlowEdge>();
            PlcPower ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            ActionResults ??= new Dictionary<string, FlowActionResult>(StringComparer.OrdinalIgnoreCase);
            ContactPulseAt ??= new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            ScheduleFireCount ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            ScheduleCycleDay ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ScheduleLastTriggeredDay ??= new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            DelayUntil ??= new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            SatisfiedContacts ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            LogTriggers ??= new Dictionary<string, LogTriggerState>(StringComparer.OrdinalIgnoreCase);
            Latched ??= new List<string>();
            RunningActions ??= new Dictionary<string, FlowActionResult>(StringComparer.OrdinalIgnoreCase);
            WaitingActions ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (DayBoundaryHour != 4) DayBoundaryHour = 0;

            foreach (var n in Nodes)
            {
                n.Params ??= new Dictionary<string, string>();
                if (n.Kind == FlowNodeKind.Trigger) n.Kind = FlowNodeKind.Condition;
                if (n.Type == FlowNodeType.CondChoice) n.BranchMode = FlowBranchMode.Choice;
                if (n.Kind == FlowNodeKind.Terminate && n.DayBoundaryHour != 4) n.DayBoundaryHour = 0;
            }

            if (EngineVersion < 2)
            {
                // 一个旧判断同时有是/否线时，复制同一数据源，否侧变成 LD NOT/AND NOT/OR NOT。
                // 不复制系统状态；NOT 只改变该触点的布尔值，不是第二条路由。
                foreach (var source in Nodes.ToList())
                {
                    if (!source.IsContact) continue;
                    var outgoing = Edges.Where(e => e.From == source.Id).ToList();
                    var noEdges = outgoing.Where(e => string.Equals(e.Branch, "false", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (noEdges.Count == 0) continue;
                    MigrationNotes.Add("「"+source.Title+"」的旧反向分支已改为「条件不满足时继续」，请检查连接方式。");
                    bool hasPositive = outgoing.Any(e => !string.Equals(e.Branch, "false", StringComparison.OrdinalIgnoreCase));
                    FlowNode negative = source;
                    if (hasPositive)
                    {
                        negative = new FlowNode
                        {
                            Id = Guid.NewGuid().ToString("N"), Kind = FlowNodeKind.Condition,
                            Type = source.Type, X = source.X + 36, Y = source.Y + 105,
                            Params = new Dictionary<string, string>(source.Params),
                            Logic = source.Logic
                        };
                        Nodes.Add(negative);
                    }
                    negative.Negated = !negative.Negated;
                    foreach (var e in noEdges) { e.From = negative.Id; e.Branch = ""; }
                }

                foreach (var e in Edges) e.Branch = "";
                bool wasActive = Active; int oldLatchCount = Latched.Count;
                Active = false; ArmedAt = null; Latched.Clear();
                PlcPower.Clear(); ActionResults.Clear();
                if (ConditionMode == FlowConditionMode.Any) MigrationNotes.Add("旧版「任一满足」已改为按连线和每个条件的组合方式判断，请检查各支路。");
                if (wasActive || oldLatchCount > 0) MigrationNotes.Add("旧条件锁存与等待状态已清除；实时条件逐轮求值，瞬时信号只在本轮运行内保留。");
                MigrationNotes.Add("已升级为单出口流程模型。");
                EngineVersion = 2;
            }

            // 端口无分支；非触点的 OUT/END 一律 AND 汇入。
            foreach (var n in Nodes)
            {
                if (!n.IsContact) n.Logic = FlowLogicOp.AND;
                else if (SourcesOf(n.Id).Count == 0) n.Logic = FlowLogicOp.LD;
                else if (n.Logic == FlowLogicOp.LD) n.Logic = FlowLogicOp.AND;
            }
        }
    }

    public enum FlowConditionMode { All = 0, Any = 1 }
    public enum FlowBranchMode { Parallel = 0, Choice = 1 }

    /// <summary>触点指令。LD 仅用于母线入口；AND/OR 决定如何接入已有电路。</summary>
    public enum FlowLogicOp { LD = 0, AND = 1, OR = 2 }

    /// <summary>Trigger 仅为读取旧数据保留，加载时统一成为 Condition。</summary>
    public enum FlowNodeKind { Trigger = 0, Condition = 1, Action = 2, Terminate = 3 }

    public enum FlowNodeType
    {
        TrigSchedule = 0, TrigSessionOnline = 1, TrigBgiIdle = 2, TrigManual = 3,
        TrigLogMatch = 4, TrigDogfoodRoute = 5, TrigScriptGroup = 6, TrigInterval = 7,
        CondBgiRunning = 10, CondBgiActive = 11, CondGenshinRunning = 12,
        CondTimeRange = 13, CondLogContains = 14, CondLogMissing = 15,
        CondLogHasError = 16, CondCompare = 17, CondLastResult = 18,
        CondProcessExists = 19, ActRunAction = 20, ActStopBgi = 21,
        ActStartBgi = 22, ActCloseGenshin = 23, ActDelay = 24,
        ActStartOneDragon = 25, ActStartGroups = 26, ActMouseClick = 27,
        ActMouseMove = 28, ActKeyPress = 29, ActMouseWheel = 30,
        ActEndFlow = 31, ActLaunchProcess = 32, ActKillProcess = 33,
        ActJoinRoom = 34, CondChoice = 39, CondScriptGroupState = 40, CondDogfoodRoute = 41,
        CondBgiLiveState = 42, CondSessionState = 43, CondWindowForeground = 44,
        // ★ 3.2.8：录制回放执行器
        /// <summary>触发即截一张图，存进「录制回放用户名-昵称时间-触发条件」。</summary>
        ActScreenshot = 45,
        /// <summary>保存触发点前 N 秒 + 后 N 秒的录像（N 不得大于保留时长的一半）。</summary>
        ActSaveRecording = 46,
        // ── 3.3.49：会话操作（自动化执行器）──────────────────────────
        /// <summary>登录目标会话（用保存凭据经 RDP 建立/恢复会话）。</summary>
        ActSessionLogin = 47,
        /// <summary>注销目标会话（关闭其中所有程序，不可恢复）。</summary>
        ActSessionLogoff = 48,
        /// <summary>重连目标会话（连接/恢复 RDP 画面）。</summary>
        ActSessionReconnect = 49
    }

    public class FlowNode
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public FlowNodeKind Kind { get; set; }
        public FlowNodeType Type { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public Dictionary<string, string> Params { get; set; } = new();
        public FlowLogicOp Logic { get; set; } = FlowLogicOp.AND;
        /// <summary>仅 END：日界 00:00/04:00；到达日界清空今日锁存并开始新一轮扫描。</summary>
        public int DayBoundaryHour { get; set; } = 4;
        /// <summary>LD NOT / AND NOT / OR NOT：取反的是本触点，不是另一条出口。</summary>
        public bool Negated { get; set; }
        /// <summary>多个出口的显式语义：并行全部执行，或只走选择的一路。</summary>
        public FlowBranchMode BranchMode { get; set; } = FlowBranchMode.Parallel;

        [JsonIgnore] public string Title => FlowNodeCatalog.TitleOf(Type);
        [JsonIgnore] public bool IsTrigger => Kind == FlowNodeKind.Trigger;
        [JsonIgnore] public bool IsContact => Kind == FlowNodeKind.Trigger || Kind == FlowNodeKind.Condition;
        public string Get(string key, string fallback = "") => Params != null && Params.TryGetValue(key, out var v) ? v ?? fallback : fallback;
        public int GetInt(string key, int fallback) => int.TryParse(Get(key), out int v) ? v : fallback;
        /// <summary>读小数参数（图片点选存的是归一化比例 0~1）。</summary>
        public double GetDouble(string key, double fallback)
            => double.TryParse(Get(key), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : fallback;
        /// <summary>写小数参数（固定用 InvariantCulture，避免区域设置把小数点变成逗号）。</summary>
        public void SetDouble(string key, double value)
            => Set(key, value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture));
        public bool GetBool(string key, bool fallback)
        {
            string s = Get(key);
            if (string.IsNullOrEmpty(s)) return fallback;
            return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        public void Set(string key, string value) { Params ??= new(); Params[key] = value ?? ""; }
    }

    /// <summary>唯一一种连线：上方输入、下方输出。Branch 仅为读取旧数据保留。</summary>
    public class FlowEdge
    {
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public string Branch { get; set; } = "";
    }

    public readonly struct PlcSignal
    {
        public bool Value { get; }
        public bool Available { get; }
        public PlcSignal(bool value, bool available = true) { Value = value; Available = available; }
        internal DateTime? ScheduleDay { get; init; }
        public static implicit operator PlcSignal(bool value) => new(value, true);
    }

    /// <summary>
    /// 日志触点「事件锁存」消费的最小行视图：序号 + 级别 + 原文。
    /// ★ 刻意不引用 BgiLogSnapshot.BgiLogLine（它带 BgiStatus/ScriptGroupRun/DailyStats 等重依赖），
    ///   这样 LogLatch 及其测试可以只编译 FlowModels + PlcLogMatcher 即可。
    /// </summary>
    public readonly struct LogLineRef
    {
        public readonly long Seq;
        public readonly string Level;
        public readonly string Text;
        public LogLineRef(long seq, string level, string text) { Seq = seq; Level = level ?? ""; Text = text ?? ""; }
    }

    public sealed class FlowActionResult
    {
        public bool Ok { get; set; }
        public double ElapsedSeconds { get; set; }
        public string Message { get; set; } = "";
        public DateTime At { get; set; }
    }

    /// <summary>
    /// 一个日志触点的锁存状态（事件锁存 + 消费语义）。
    /// 生命周期：触点命中新日志 → 记下一个待消费事件；动作执行完一轮收尾 → 消费（清空）。
    /// </summary>
    public sealed class LogTriggerState
    {
        /// <summary>日志流里**已扫描到**的最大行序号（游标）。</summary>
        public long Cursor { get; set; }
        /// <summary>已锁存、待消费的匹配事件（可为空 = 没有待处理事件）。</summary>
        public List<LogLatchEvent> Events { get; set; } = new();
    }

    /// <summary>锁存下来的一条日志匹配事件（含序号指纹，供界面/调试展示）。</summary>
    public sealed class LogLatchEvent
    {
        /// <summary>命中行的内容（截断后）。</summary>
        public string Line { get; set; } = "";
        /// <summary>该行在日志流里的序号。</summary>
        public long Seq { get; set; }
    }
}
