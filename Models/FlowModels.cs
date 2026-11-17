using System;
using System.Collections.Generic;

namespace SoulLink.Models
{
    /// <summary>
    /// 单个账号的自动化流程（节点图）。
    ///
    /// 设计约束（用户明确要求）：
    ///   · 每个账号有自己独立的一组流程，**一一对应、互不干扰**；
    ///   · 一个流程**只有一个触发器**（Trigger）；
    ///   · 触发器后面可接多个条件（Condition），支持"全部满足(AND)"或"任一满足(OR)"；
    ///   · 条件后面接执行器（Action），按连线顺序执行；
    ///   · 全部模块化：节点可拖动、可连线、一对多连接。
    /// </summary>
    public class AutomationFlow
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>流程名称（用户可自定义）。</summary>
        public string Name { get; set; } = "新的自动化";

        /// <summary>绑定的账号 Id（一一对应，不与其他账号共享）。</summary>
        public string AccountId { get; set; } = "";

        /// <summary>是否启用。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>是否暂停（暂停后不触发，但保留图）。</summary>
        public bool Paused { get; set; }

        /// <summary>暂停原因（显示用）。</summary>
        public string PauseReason { get; set; } = "";

        /// <summary>节点表。</summary>
        public List<FlowNode> Nodes { get; set; } = new List<FlowNode>();

        /// <summary>连线表（有向：From → To）。</summary>
        public List<FlowEdge> Edges { get; set; } = new List<FlowEdge>();

        /// <summary>
        /// 流程是否处于「已触发、正在等条件」的状态。
        ///
        /// ★ 用户要求的状态机模型：
        ///   触发器命中 → 武装(Active=true) → 各条件独立判定、**满足就记住** →
        ///   全部必需条件都满足 → 执行器触发 → 流程退出、条件清零。
        ///   所以条件的满足顺序无所谓（可以先满足 3、2，再满足 1）。
        /// </summary>
        public bool Active { get; set; }

        /// <summary>武装时刻（用于超时与显示）。</summary>
        public DateTime? ArmedAt { get; set; }

        /// <summary>
        /// 武装超时（分钟）。触发器命中后，若条件在这么多分钟内一直凑不齐，
        /// 会自动退出流程并清空条件，等下一次触发器重新开始。
        ///
        /// ★ 为什么需要：触发器命中后流程进入"等待条件"状态，
        ///   如果某个条件永远不满足（游戏卡死、BGI 没启动），
        ///   没有超时就会永久卡在这个状态，条件节点一直显示"已满足"。
        /// ★ 0 或负数 = 不超时（保持旧行为）。
        /// </summary>
        public int ArmTimeoutMinutes { get; set; } = 60;

        /// <summary>
        /// 已满足并锁存的条件节点 Id。
        /// 只有退出流程（执行完 / 手动结束 / 超时）时才清空。
        /// </summary>
        public List<string> Latched { get; set; } = new List<string>();

        /// <summary>
        /// 是否持久化条件状态（默认开启）。
        ///
        /// ★ 开启：条件满足后写进配置文件，程序重启也不丢，
        ///   适合"等一整天凑齐条件"这类长周期流程。
        /// ★ 关闭：条件只存在内存里，程序一退出就清空，
        ///   适合"每次启动都重新判断"的短周期流程。
        /// </summary>
        public bool PersistLatched { get; set; } = true;

        /// <summary>上次触发时间。</summary>
        public DateTime? LastRunAt { get; set; }

        /// <summary>累计触发次数。</summary>
        public int RunCount { get; set; }

        /// <summary>
        /// 多个条件怎么聚合：
        ///   All = 全部满足（AND）  Any = 任一满足（OR）
        /// </summary>
        public FlowConditionMode ConditionMode { get; set; } = FlowConditionMode.All;

        // ── 便捷访问 ──────────────────────────────────────────────

        /// <summary>触发器节点（一个流程最多一个）。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public FlowNode? Trigger
        {
            get
            {
                foreach (var n in Nodes)
                    if (n.Kind == FlowNodeKind.Trigger) return n;
                return null;
            }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "未命名流程" : Name;

        /// <summary>从某节点连出的所有目标节点 Id。</summary>
        public List<string> TargetsOf(string nodeId)
        {
            var list = new List<string>();
            foreach (var e in Edges)
                if (e.From == nodeId && !list.Contains(e.To)) list.Add(e.To);
            return list;
        }

        /// <summary>连入某节点的所有源节点 Id。</summary>
        public List<string> SourcesOf(string nodeId)
        {
            var list = new List<string>();
            foreach (var e in Edges)
                if (e.To == nodeId && !list.Contains(e.From)) list.Add(e.From);
            return list;
        }

        public FlowNode? FindNode(string id)
        {
            foreach (var n in Nodes) if (n.Id == id) return n;
            return null;
        }

        /// <summary>某条件是否已经锁存（满足过）。</summary>
        public bool IsLatched(string nodeId) => Latched != null && Latched.Contains(nodeId);

        /// <summary>锁存一个条件（幂等）。</summary>
        public bool Latch(string nodeId)
        {
            Latched ??= new List<string>();
            if (Latched.Contains(nodeId)) return false;
            Latched.Add(nodeId);
            return true;
        }

        /// <summary>武装：进入等待条件状态，清空所有锁存。</summary>
        public void Arm()
        {
            Active = true;
            ArmedAt = DateTime.Now;
            Latched = new List<string>();
        }

        /// <summary>退出流程：条件全部清零。</summary>
        public void Disarm()
        {
            Active = false;
            ArmedAt = null;
            Latched = new List<string>();
        }
    }

    /// <summary>多个条件之间的聚合方式。</summary>
    public enum FlowConditionMode
    {
        /// <summary>全部满足（AND）</summary>
        All = 0,
        /// <summary>任一满足（OR）</summary>
        Any = 1
    }

    /// <summary>节点类别。</summary>
    public enum FlowNodeKind
    {
        /// <summary>触发器（唯一）</summary>
        Trigger = 0,
        /// <summary>条件判断</summary>
        Condition = 1,
        /// <summary>执行动作</summary>
        Action = 2,
        /// <summary>
        /// 结束（终止本次流程）。
        /// ★ 用户要求：结束应该独立出来、用不同颜色，不要混在执行器里。
        /// </summary>
        Terminate = 3
    }

    /// <summary>
    /// 节点类型。
    /// 每种类型的参数放在 Node.Params 里（简单字符串字典，避免过多子类）。
    /// </summary>
    public enum FlowNodeType
    {
        // ── 触发器（只能有一个）─────────────────────────────
        /// <summary>定时：到点触发。参数 hour/minute/second/intervalSeconds/mode</summary>
        TrigSchedule = 0,
        /// <summary>会话上线时触发。参数无</summary>
        TrigSessionOnline = 1,
        /// <summary>BGI 空闲超过 N 秒时触发。参数 idleSeconds</summary>
        TrigBgiIdle = 2,
        /// <summary>手动触发（点"立即运行"）。参数无</summary>
        TrigManual = 3,
        /// <summary>
        /// 日志监控：日志里出现/匹配到指定内容时触发。
        /// 参数 pattern（关键字或正则）、isRegex、level（级别过滤，可空）
        /// </summary>
        TrigLogMatch = 4,

        // ── 条件 ────────────────────────────────────────────
        /// <summary>BGI 是否在运行。参数 expect = true/false</summary>
        CondBgiRunning = 10,
        /// <summary>BGI 日志是否在 N 秒内有更新。参数 withinSeconds</summary>
        CondBgiActive = 11,
        /// <summary>原神是否在运行。参数 expect</summary>
        CondGenshinRunning = 12,
        /// <summary>当前时间在某个区间内。参数 from/to（HH:mm）</summary>
        CondTimeRange = 13,
        /// <summary>
        /// 日志里最近 N 秒内出现过指定内容。
        /// 参数 pattern、isRegex、withinSeconds
        /// </summary>
        CondLogContains = 14,
        /// <summary>
        /// 日志里最近 N 秒内**没有**出现指定内容（异常检测用）。
        /// 参数 pattern、isRegex、withinSeconds
        /// </summary>
        CondLogMissing = 15,
        /// <summary>
        /// 日志里是否出现过错误/失败（复用 BgiStatus 的统计）。
        /// 参数 withinSeconds
        /// </summary>
        CondLogHasError = 16,
        /// <summary>
        /// 数值/时间比较。参数 left（左值来源）、op（大于/小于/等于…）、right（右值）
        /// 用于"判断执行结果"，例如：上一步运行时长 &gt; 300、当前时刻 &gt; 04:00
        /// </summary>
        CondCompare = 17,
        /// <summary>
        /// 上一步执行结果判断。参数 expect（ok/fail/any）
        /// ★ 用户要求：执行模块之后也可以放条件，用来判断执行结果。
        /// </summary>
        CondLastResult = 18,
        /// <summary>
        /// 任意进程检查：指定用户是否有某个进程在跑。
        /// 参数 process（进程名，可带 .exe）、expect（true=存在）
        /// </summary>
        CondProcessExists = 19,
        /// <summary>
        /// BGI 活跃状态：区分"在运行"、"空闲"、"未运行"。
        /// 参数 expect（running=在跑且有产出 / idle=进程在但没产出 / stopped=没进程）
        /// </summary>
        CondBgiLiveState = 20,
        /// <summary>
        /// 会话连接状态：Active（在线）/ Disc（离线）/ 无会话（未连接）。
        /// 参数 expect（active / disc / none）
        /// </summary>
        CondSessionState = 21,
        /// <summary>
        /// 游戏窗口是否在前台（判断是否卡在某个界面）。
        /// 参数 expect（true=在前台）
        /// </summary>
        CondWindowForeground = 22,

        // ── 执行器 ──────────────────────────────────────────
        /// <summary>执行一个快捷动作。参数 actionId</summary>
        ActRunAction = 20,
        /// <summary>停止目标账号的 BGI。参数无</summary>
        ActStopBgi = 21,
        /// <summary>启动 BGI（可带参数）。参数 args</summary>
        ActStartBgi = 22,
        /// <summary>执行一条龙。参数 name（一条龙配置名，空=页面选定）</summary>
        ActStartOneDragon = 25,
        /// <summary>执行配置组。参数 groups（空格分隔的组名）</summary>
        ActStartGroups = 26,
        /// <summary>模拟鼠标点击。参数 x、y（屏幕坐标）、button（left/right）、count</summary>
        ActMouseClick = 27,
        /// <summary>模拟鼠标移动。参数 x、y</summary>
        ActMouseMove = 28,
        /// <summary>模拟按键。参数 keys（如 F12、Ctrl+A）、times</summary>
        ActKeyPress = 29,
        /// <summary>模拟滚轮。参数 delta（正=上滚）</summary>
        ActMouseWheel = 30,
        /// <summary>结束本次流程（不再继续后续节点）。参数无</summary>
        ActEndFlow = 31,
        /// <summary>打开任意程序。参数 path（可执行文件路径）、args</summary>
        ActLaunchProcess = 32,
        /// <summary>关闭任意进程。参数 process（进程名）、force（是否强制）</summary>
        ActKillProcess = 33,
        /// <summary>
        /// 上线房间：输入房间码，与服务器沟通后把自己上线到指定房间。
        /// 参数 code（房间码，相当于通行证）
        /// </summary>
        ActJoinRoom = 34,
        /// <summary>关闭原神。参数无</summary>
        ActCloseGenshin = 23,
        /// <summary>等待 N 秒。参数 seconds</summary>
        ActDelay = 24
    }

    /// <summary>一个流程节点。</summary>
    public class FlowNode
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public FlowNodeKind Kind { get; set; }

        public FlowNodeType Type { get; set; }

        /// <summary>画布坐标（拖动后保存）。</summary>
        public double X { get; set; }
        public double Y { get; set; }

        /// <summary>节点参数（键值对，按 Type 解释）。</summary>
        public Dictionary<string, string> Params { get; set; } = new Dictionary<string, string>();

        [System.Text.Json.Serialization.JsonIgnore]
        public string Title => FlowNodeCatalog.TitleOf(Type);

        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsTrigger => Kind == FlowNodeKind.Trigger;

        public string Get(string key, string fallback = "")
            => Params != null && Params.TryGetValue(key, out var v) ? (v ?? fallback) : fallback;

        public int GetInt(string key, int fallback)
            => int.TryParse(Get(key), out int v) ? v : fallback;

        public bool GetBool(string key, bool fallback)
        {
            string s = Get(key);
            if (string.IsNullOrEmpty(s)) return fallback;
            return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        public void Set(string key, string value)
        {
            Params ??= new Dictionary<string, string>();
            Params[key] = value ?? "";
        }
    }

    /// <summary>一条有向连线。</summary>
    public class FlowEdge
    {
        public string From { get; set; } = "";
        public string To { get; set; } = "";
    }
}
