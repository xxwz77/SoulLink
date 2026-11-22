using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SoulLink.Services
{
    /// <summary>
    /// BGI 日志的**唯一读取者**与**不可变快照**。
    ///
    /// ── 设计原则（用户的硬性要求）────────────────────────────────────
    ///   · 只在启动时全量扫一遍今日日志，之后只吃增量，**扫过的字节不再读第二遍**；
    ///   · 不保留任何日��原文：只有"关键信息 + 一个很小的尾窗"；
    ///   · 只读**已绑定** BGI 路径的账号，未绑定的一律零 IO；
    ///   · 所有需要 BGI 日志的地方统一从这里取，不再各自读文件；
    ///   · 内存及时释放：单遍流式扫描、字节预算封顶、丢弃后压缩 LOH。
    ///
    /// ── 为什么不是"每次重建整份投影"（2.17.44 的教训）──────────────
    ///   旧实现 BusinessDayData 在日志每写一次就把「今天 + 昨天」拼接成
    ///   88 MB 字符串再逐行正则 + Split，1 Hz 循环里干这件事。
    ///   实测：88% 单核、1.1 GB 工作集、每秒 1.1 GB 垃圾对象。
    ///   现在：749k 行 / 53 MB 的日志单遍流式扫描 130 ms、分配 1 MB。
    ///
    /// ── 当前配置组为什么做成"粘性快照"──────────────────────────────
    ///   用户原话：「当前运行配置组名字做个快照，出现新的丢弃它」。
    ///   即：保留**最近一次出现的**配置组名，直到日志里出现新的配置组为止。
    ///   这样跨天滚动（BGI 00:00 生成新日志，新日志里没有当前配置组的行）
    ///   时状态天然延续，不会突然变成"—"。
    /// </summary>
    public sealed class BgiLogSnapshot
    {
        /// <summary>账号 Id（Account.Id）。</summary>
        public string AccountId { get; init; } = "";
        /// <summary>Windows 用户名。</summary>
        public string UserName { get; init; } = "";

        /// <summary>是否已绑定 BGI 路径。false 时调用方不该期待任何数据。</summary>
        public bool Bound { get; init; }
        /// <summary>BGI.exe 完整路径。未绑定为空。</summary>
        public string BgiExe { get; init; } = "";
        /// <summary>当前跟随的日志文件。没有日志也为空。</summary>
        public string LogFile { get; init; } = "";

        /// <summary>已消费到的字节位置。文件变短（滚动）时归零。</summary>
        public long Cursor { get; init; }
        /// <summary>已消费文件的总长度（= 上次看到的文件大小）。</summary>
        public long FileLength { get; init; }
        /// <summary>日志最后写入时间（用于"BGI 是否活跃"判断）。</summary>
        public DateTime LastWrite { get; init; }

        /// <summary>
        /// **当前运行中的配置组名**（粘性快照：保留最近一次出现的，出现新的才替换）。
        /// ★ 跨天滚动时**继承**——这是用户明确要求的行为。
        /// </summary>
        public string CurrentGroup { get; init; } = "";
        /// <summary>CurrentGroup 最后一次被日志确认的时间（日志里的时间戳）。</summary>
        public DateTime CurrentGroupAt { get; init; }

        /// <summary>
        /// 最近一次结束/停止的配置组（**不代表在运行**，仅供参考）。
        /// ★ 与 CurrentGroup 的区别：CurrentGroup 空 = 现在没有组在跑。
        /// </summary>
        public string LastGroup { get; init; } = "";

        /// <summary>最近一次判定为"停止"的时间；MinValue = 从未停止过。</summary>
        public DateTime StoppedAt { get; init; } = DateTime.MinValue;

        /// <summary>
        /// 当前是否**真的有一个配置组在跑**。
        /// ★ 用户要求：日志出现停止即判为停止，不再保留运行状态。
        /// </summary>
        public bool GroupRunning => CurrentGroup.Length > 0;
        /// <summary>当前一条龙配置名。</summary>
        public string OneDragon { get; init; } = "";
        /// <summary>当前任务名 / 路线 / 进度等结构化状态。</summary>
        public BgiStatus Status { get; init; } = new BgiStatus();

        /// <summary>今日各配置组的运行记录（只存状态，不存原文）。</summary>
        public IReadOnlyList<ScriptGroupRun> Groups { get; init; } = Array.Empty<ScriptGroupRun>();

        /// <summary>今日累计统计（经验/摩拉/拾取/狗粮路线）。</summary>
        public DailyStats Today { get; init; } = new DailyStats();

        /// <summary>全天按配置组分类的拾取（配置组 → 物品 → 次数）。</summary>
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> PicksByGroup
        { get; init; } = new Dictionary<string, IReadOnlyDictionary<string, int>>();

        /// <summary>今日报错/告警/失败计数。</summary>
        public int ErrorCount { get; init; }
        public int WarnCount { get; init; }
        public int FailCount { get; init; }

        /// <summary>
        /// 最近的若干行原文（唯一保留原文的地方）。
        /// ★ 只有这些行会分配字符串；总量封顶 <see cref="BgiLogCenter.TailBudgetChars"/> 字符。
        /// </summary>
        public IReadOnlyList<BgiLogLine> Tail { get; init; } = Array.Empty<BgiLogLine>();

        /// <summary>
        /// 该账号日志流**当前最大**行序号（含已经滚出尾窗的行）。
        /// ★ 触点事件游标用它判断「重启后从哪开始重新建立」；空流时为 0。
        /// </summary>
        public long LastSeq { get; init; }

        /// <summary>快照生成时间。</summary>
        public DateTime UpdatedAt { get; init; } = DateTime.Now;

        /// <summary>读取失败原因（权限/文件占用等），空 = 正常。</summary>
        public string Error { get; init; } = "";

        /// <summary>数据中心自报占用的内存（字符数 × 2），用于界面哨兵。</summary>
        public long ResidentChars
        {
            get
            {
                long n = 0;
                if (Tail != null) foreach (var l in Tail) n += l.Text.Length + 24;   // 24 ≈ 对象头 + 字段
                n += CurrentGroup.Length + OneDragon.Length + 64;
                if (Today != null) { n += 64 + Today.Picks.Count * 32; }
                return n;
            }
        }

        public bool HasData => Cursor > 0 || Tail.Count > 0 || LastSeq > 0;
    }

    /// <summary>尾窗里的一行日志。level 取自 [INF]/[WRN]/[ERR]，无则空。</summary>
    public readonly struct BgiLogLine
    {
        public readonly DateTime At;
        public readonly string Level;
        public readonly string Text;
        /// <summary>
        /// 该账号日志流里单调递增的行序号（跨天滚动/文件重建后不归零）。
        /// ★ 日志触点的「事件锁存」游标据此推进，而不是靠时间窗；
        ///   快照每 refresh 一次，序号更大的行就是「新增」。
        /// </summary>
        public readonly long Seq;
        public BgiLogLine(DateTime at, string level, string text, long seq)
        { At = at; Level = level ?? ""; Text = text ?? ""; Seq = seq; }
    }
}