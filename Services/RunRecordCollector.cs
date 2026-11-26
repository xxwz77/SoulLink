using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Services
{
    /// <summary>
    /// 采集器：把 BgiLogCenter 的增量日志文本归因成「配置组运行 / 路线 / 报错」记录，
    /// 经 RunRecordStore 队列落库（本类自身**零文件 IO**）。
    ///
    /// ── 分层（契约 docs/research/DATA-CENTER-RUN-DB-20261125.md）──────────
    ///   run    ：配置组「加载完成…开始执行」→ start 事件，INSERT open 行；
    ///            执行结束/未成功完成/取消 → end 事件，UPDATE 收尾。
    ///   route  ：两层 —— script（→ 开始执行JS脚本: "名"，带「耗时」结束）
    ///            与 route（联机锄地「进入路线循环 route="xxx.json"」，无耗时结束）。
    ///            一条龙路线不是配置组，别混。
    ///   err    ：[ERR] 头行 + 其后无时间戳续行（ClearScript 多行栈）→ 完整详情。
    ///
    /// ── 幂等 ─────────────────────────────────────────────────────────
    ///   run_id/route_id 为确定性自然键，RunRecordStore 写线程 INSERT OR IGNORE；
    ///   重启重扫（BgiLogCenter cursor=0 全量重喂）不产生重复行。
    ///
    /// ── 线程模型 ─────────────────────────────────────────────────────
    ///   Feed 由 ProcessChunk 在调用线程调用，只做正则 + 内存状态机 + 入队；
    ///   启动重建（EnsureBootedAsync）读取 DB open 行灌内存窗口，异步执行。
    /// </summary>
    public static class RunRecordCollector
    {
        // ── 常量 ──────────────────────────────────────────────────────
        private const char KeySep = '\u001f';
        /// <summary>跨天连续性阈值（契约 §4.3）：≤300s 视为同一运行续跑。</summary>
        private const long ContinuityGapMs = 300_000;

        // ── t6 常量 ──────────────────────────────────────────────────────
        /// <summary>
        /// 换文件后次日段惰性开窗兜底：BGI 正常 0 点换文件首行几秒内就到；
        /// 90s 覆盖时钟偏差/大文件首扫延迟。超过视为「BGI 已停」→ 不建次日段
        /// （旧段已 close，不产空窗；若事件迟到走陈旧 open 行兜底找回）。
        /// </summary>
        internal const long DaySegmentFirstEventTimeoutMs = 90_000;
        /// <summary>BGI 状态时段：静默 ≥60s 判 idle（契约 P，常量+注释）。</summary>
        internal const long IdleSilenceMs = 60_000;
        /// <summary>同账号两次判活最小间隔（契约 Q：用 LastAliveCheck 节流）。</summary>
        internal const long AliveCheckMinIntervalMs = 30_000;

        /// <summary>换文件待定状态（per account）：day-2 段尚未获得首事件。</summary>
        private sealed class PendingDaySplit
        {
            public string Group = "";         // 继承的组名（O2 核心：绝不能丢）
            public long OldSegEndMs;           // 第一段收尾时刻（=当日最后事件时刻）
            public string NewFile = "";
            public long DeadlineMs;            // 90s 兜底
        }

        /// <summary>
        /// ★ t56：当前是否处于「历史回填」模式。
        ///
        /// 回填是历史数据，墙钟判据（如 PendingSplit 的 90s 兜底）会误杀 ——
        /// 因为几十秒内要跑完几天的日志。回填期间这些判据要放宽。
        /// 用 AsyncLocal 保证不跨线程污染（回填在采集线程，实时也在采集线程，但分时）。
        /// </summary>
        private static readonly AsyncLocal<bool> BackfillMode = new();

        /// <summary>进入/退出回填模式（成对使用；只能用 using 或 try/finally 保证恢复）。</summary>
        public static IDisposable EnterBackfillMode()
        {
            bool prev = BackfillMode.Value;
            BackfillMode.Value = true;
            return new BackfillScope(prev);
        }

        private sealed class BackfillScope : IDisposable
        {
            private readonly bool _prev;
            public BackfillScope(bool prev) { _prev = prev; }
            public void Dispose() { BackfillMode.Value = _prev; }
        }

        private static readonly Dictionary<string, PendingDaySplit> PendingSplits
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>BGI 状态时段三态跟踪（per account；只在切换时开/闭段）。</summary>
        private sealed class SpanState
        {
            public string Current = "";       // "" 未开始 / running / idle / not_running
            public long LastEventMs;          // 最近日志事件（日志时钟）
            public long LastFeedWallMs;       // 最近一次 Feed 的墙钟（判静默用——回放日志时钟与墙钟差异大）
            public long LastAliveCheckMs;     // 判活节流（Q：≥30s）
            public bool Alive;               // 最近一次 BgiPid 判活结果
        }
        private static readonly Dictionary<string, SpanState> SpanStates
            = new(StringComparer.OrdinalIgnoreCase);

        // ── 正则锚点（契约 §1；全部来自实测日志样本）──────────────────

        // 配置组边界（与 RouteLedger/ScriptGroupTracker 同源）
        private static readonly Regex RxGroupStart = new(
            @"配置组\s*""(?<g>[^""]+)""\s*加载完成[，,]\s*共\s*(?<n>\d+)\s*个脚本[，,]\s*开始执行",
            RegexOptions.Compiled);
        private static readonly Regex RxGroupFinish = new(
            @"配置组\s*""(?<g>[^""]+)""\s*执行结束", RegexOptions.Compiled);
        private static readonly Regex RxGroupFail = new(
            @"配置组\s*""(?<g>[^""]+)""\s*未成功完成[，,]?\s*结果\s*[:：]\s*(?<r>\S*)",
            RegexOptions.Compiled);
        private static readonly Regex RxGroupCancel = new(
            @"取消执行配置组\s*[:：]\s*""(?<g>[^""]+)""", RegexOptions.Compiled);

        // 脚本边界：结束带「耗时: …」
        private static readonly Regex RxScriptStart = new(
            @"→\s*开始执行(?:JS脚本|独立任务)\s*[:：]\s*""(?<name>[^""]+)""", RegexOptions.Compiled);
        private static readonly Regex RxScriptEndDur = new(
            @"→\s*脚本执行结束\s*[:：]\s*""(?<name>[^""]+)""\s*[,，]?\s*耗时\s*[:：]\s*(?<dur>[^，,]+)",
            RegexOptions.Compiled);

        // 联机锄地路线循环（契约 §1.3）：
        //   当前组 路径组1 共 5 条路线…（组头，不计为路线，只刷新上下文）
        //   [DEBUG] 开始遍历路线，共 5 条，startIndex=0
        //   [DEBUG] 进入路线循环，count=1，route="07【额外】….json"
        private static readonly Regex RxPathGroupHeader = new(
            @"当前组\s+(?<path>\S+)\s+共\s+(?<n>\d+)\s*条路线", RegexOptions.Compiled);
        private static readonly Regex RxTraverseStart = new(
            @"开始遍历路线[，,]?\s*共\s*(?<n>\d+)\s*条", RegexOptions.Compiled);
        private static readonly Regex RxRouteLoopEnter = new(
            @"进入路线循环\s*[,，]?\s*count\s*=\s*(?<c>\d+)\s*[,，]?\s*route\s*=\s*""(?<name>[^""]+)""",
            RegexOptions.Compiled);

        // ── t6 A) 路线锚点族补齐 ────────────────────────────────────────────
        //   地图追踪任务（script 层）：→ 开始执行地图追踪任务: "X.json"（实测 A4 20261124 L2449/2965/3844/4291）
        private static readonly Regex RxMapTrackingStart = new(
            @"→\s*开始执行地图追踪任务\s*[:：]\s*""(?<name>[^""]+)""", RegexOptions.Compiled);

        //   激活CD 线路层（罐子激活，A4 20261124 L220/232/349/556…共 8 条）
        //     开始：线路 X.json 开始状态已保存 / 无历史记录，将首次执行
        //     成功：线路 X.json 成功，结束时间：…；记录已保存
        //     失败：脚本停止：线路 X.json 终点校验失败，距离：N（ERR 块归并）
        private static readonly Regex RxCdLineStart = new(
            @"线路\s*(?<name>[^\s，,；;]+?\.json)\s*(?:开始状态已保存|无历史记录，将首次执行)",
            RegexOptions.Compiled);
        private static readonly Regex RxCdLineOk = new(
            @"线路\s*(?<name>[^\s，,；;]+?\.json)\s*成功[，,]\s*结束时间", RegexOptions.Compiled);

        //   锄地补充锚点（数量型 → run.planned_routes；名字型 → 路线边界交叉校验）
        private static readonly Regex RxRoutePlanCount = new(
            @"(?:共加载\s*(?<n1>\d+)\s*条路线|上报路线列表[:：]\s*(?<n3>\d+)\s*条)",
            RegexOptions.Compiled);
        private static readonly Regex RxRouteIdxStart = new(
            @"开始第\s*(?<idx>\d+)/(?<total>\d+)\s*条线路[:：]\s*""(?<name>[^""]+)""", RegexOptions.Compiled);
        private static readonly Regex RxRouteIdxDone = new(
            @"完成第\s*(?<idx>\d+)/(?<total>\d+)\s*条线路[:：]\s*""(?<name>[^""]+)""", RegexOptions.Compiled);

        // 路线结束：无「耗时」的脚本执行结束，name 等于当前打开的路线
        private static readonly Regex RxScriptEndPlain = new(
            @"→\s*脚本执行结束\s*[:：]\s*""(?<name>[^""]+)""", RegexOptions.Compiled);

        // 拾取 / 收益（与 RouteLedger 同源）
        private static readonly Regex RxPick = new(
            @"交互或拾取\s*[:：]\s*""(?<item>[^""]+)""", RegexOptions.Compiled);

        // ── t6 硬要求二：三类拾取事件（「没拾到」也必须记录）────────────────
        //   picked   = 交互或拾取:"物品"（RxPick，语义不变）
        //   no_target= 无有效结果，不交互（实测 A1 20261125 无引号格式，2680+ 次）
        //   skipped  = 识别到"X"，不交互（物品名原话照抄，1698+ 次）
        private static readonly Regex RxNoTarget = new(
            @"无有效结果\s*[,，]\s*不交互", RegexOptions.Compiled);
        private static readonly Regex RxSkipped = new(
            @"识别到\s*""(?<item>[^""]+)""\s*[,，]\s*不交互", RegexOptions.Compiled);
        private static readonly Regex RxExp = new(
            @"(?<!JS 通知关闭，消息被拦截:\s*)(?:狗粮路线获取狗粮经验\s*[:：]\s*(?<v1>\d+)|[\u4e00-\u9fa5]{1,6}可获得(?<v2>\d+)经验)",
            RegexOptions.Compiled);
        private static readonly Regex RxMora = new(
            @"(?<!JS 通知关闭，消息被拦截:\s*)(?:狗粮路线获取摩拉\s*[:：]\s*(?<v1>\d+)|[\u4e00-\u9fa5]{1,6}获得(?<v2>\d+)摩拉(?!鼬))",
            RegexOptions.Compiled);

        // 行首时间戳 [HH:mm:ss.fff]（无日期；日期来自文件名 YYYYMMDD）
        private static readonly Regex RxLineTs = new(
            @"^\[(?<date>\d{4}-\d{2}-\d{2}[ T])?(?<time>\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?)\]\s*(?:\[(?<level>[A-Za-z]{3,5})\])?",
            RegexOptions.Compiled);
        // 错误头：[HH:mm:ss.fff] [ERR] ... <logger/category>
        private static readonly Regex RxErrHeader = new(
            @"^\[\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?\]\s*\[ERR\]", RegexOptions.Compiled);

        // ── 时间戳推断（契约 §1.6：日期 = 文件名 YYYYMMDD + 单调递增跨日）────
        // LogText 天轴：file day 00:00 起，行时刻 time-of-day；若比上一行回退
        // （如 23:59 → 00:00）则 +1 天 —— 这样 23:59:59 后的 00:00:01 落在次日。
        // ── 内存窗口模型（契约 §3.1）────────────────────────────────────

        /// <summary>当前打开的配置组运行窗口（每账号至多 1 个）。</summary>
        private sealed class OpenRun
        {
            public string Account = "";
            public string Group = "";
            public long StartAtMs;
            public string RunId = "";
            public int ScriptCount;
            public string SrcFileStart = "";
            public long LastEventMs;
            public long Exp, Mora;
            public int Picks;
            public OpenRoute? Route;      // 当前路线/脚本窗口
            public bool DirtyRoute;       // 拾取后需重投 route 行
            public bool DirtyRun;         // 收益后需重投 run 行
            public bool FromDb;           // 启动重建/跨天补全来源（is_backfilled 语义）
            // ── t6 ─────────────────────────────────────────────────────────
            public int Picked;           // run 级三类计数（守恒自检 H/L 用）
            public int Skipped;
            public int NoTarget;
            public int PlannedRoutes;    // C) 计划对账（数量型锚点）
            public int PickUnattributed; // H) 守恒差异（不该非 0；Feed 末尾自检）
            /// <summary>t6 惰性开窗：换文件后待定 start（首个事件到达才 AddRun）。</summary>
            public bool LazyStart;
            /// <summary>t6 换文件兜底：90s 无首事件则不建次日段。</summary>
            public long LazyDeadlineMs;
            /// <summary>t6：本段由跨天换文件拆出（收尾时 is_cross_day/day_split 置 1）。</summary>
            public bool DaySplitSeg;
        }

        /// <summary>当前打开的路线/脚本窗口（嵌套：route 挂在 script 下）。</summary>
        private sealed class OpenRoute
        {
            public string RouteId = "";
            public string RunId = "";
            public string Kind = "";      // "script" | "route"
            public string Name = "";
            public string? ParentRouteId;
            public long StartAtMs;
            public string SrcFile = "";
            public int PickCount;
            public readonly Dictionary<string, int> PickDetail = new(StringComparer.Ordinal);
            public bool HasError;
            // ── t6 硬要求二：三类拾取计数（picked=PickCount 语义不变）────────
            public int Picked;
            public int Skipped;
            public int NoTarget;
            /// <summary>t6 B)：锄地「第 x/y 条线路」序号（跳过推断用）。</summary>
            public int Idx;
            public int Total;
        }

        private static readonly Lock StateLock = new();
        private static readonly Dictionary<string, OpenRun> OpenRuns
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>t6：每账号最近一个 chunk 的首行时间戳（惰性开窗 start=次日日志首行时刻用）。</summary>
        private static readonly Dictionary<string, long> FirstLineTs
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// t6 H)：无 run 窗口时的三类「孤儿」计数（不静默吞掉——用于守恒对账：
        /// pick_event 总数 + 孤儿数 == 日志三类行数）。按 (account, kind) 累计，只增不减。
        /// </summary>
        private static readonly Dictionary<string, int[]> OrphanPicks
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>测试/自检：读某账号的三类孤儿计数 [picked,skipped,no_target]。</summary>
        internal static (int picked, int skipped, int noTarget) GetOrphanPicksForTest(string user)
        {
            lock (StateLock)
            {
                return OrphanPicks.TryGetValue(user, out var c)
                    ? (c[0], c[1], c[2]) : (0, 0, 0);
            }
        }

        /// <summary>测试：把某账号的「最近 Feed 墙钟」回拨 ms（模拟静默 ≥60s，免真等）。</summary>
        internal static void BackdateLastFeedForTest(string user, long ms)
        {
            lock (StateLock)
            {
                if (SpanStates.TryGetValue(user, out var st)) st.LastFeedWallMs -= ms;
            }
        }

        /// <summary>每账号的天轴：fileDay(00:00 epoch ms) + 上一个带时间戳行的绝对时刻。</summary>
        private sealed class DayClock
        {
            public long FileDayMs;       // 文件名日期 00:00 本地时间
            public long LastMs;          // 最近一个解析出的绝对时刻（单调基线）
            public long LastRawMs;       // 最近一行的“当日时刻”用于跨日判定
        }
        private static readonly Dictionary<string, DayClock> Clocks
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>跨 chunk 的 [ERR] 块累积器（key=account）。</summary>
        private sealed class ErrAccum
        {
            public long TsMs;
            public string Source = "";
            public string HeaderLevel = "ERR";
            public StringBuilder Detail = new();
            public bool InBlock;
            public int TrailingBlank;
            public string CurrentFile = "";
            /// <summary>异常正文模式（ClearScript 栈接在 DBG/INF 头后）。</summary>
            public bool PendingException;
            /// <summary>最近一次 ERR 头的时刻/来源（异常栈归并目标）。</summary>
            public long LastErrTsMs;
            public string LastErrSource = "";
            /// <summary>最近一次已结算 ERR 事件的 detail 构建器（异常栈追加目标）。</summary>
            public StringBuilder? LastErrDetail;
            /// <summary>块起始行在 chunk 内的位置（事件排序用）。</summary>
            public int StartPos;
        }
        private static readonly Dictionary<string, ErrAccum> ErrBlocks
            = new(StringComparer.OrdinalIgnoreCase);
        // ── 时间戳工具 ──────────────────────────────────────────────────

        private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

        /// <summary>
        /// ★ t44 修复（时间偏 8h）：本地墙钟 → **真实 UTC epoch ms**。
        /// 原实现 (local - UnixEpoch) 把本地时刻当 UTC 存——存储值是「墙钟假 UTC」，
        /// 任何按真 UTC 解释的消费方（网页 JS new Date(ms)、DateTimeOffset.FromUnixTimeMilliseconds）
        /// 都会 +8h（东八区）。改为补上本地时区偏移，存真 UTC；显示端 new Date() 直接正确。
        /// </summary>
        private static long ToMs(DateTime local)
        {
            var offset = TimeZoneInfo.Local.GetUtcOffset(local);
            return (long)(local - UnixEpoch).TotalMilliseconds - (long)offset.TotalMilliseconds;
        }

        /// <summary>t44：与 ToMs 配套——真 UTC ms → 本地日期（跨天判定用本地日）。</summary>
        private static string DayOf(long ms)
        {
            var local = UnixEpoch.AddMilliseconds(ms);
            var offset = TimeZoneInfo.Local.GetUtcOffset(local);
            return local.Add(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        /// <summary>从日志文件名解析日期锚（better-genshin-impactYYYYMMDD.log）。</summary>
        internal static bool TryFileDay(string srcFile, out long fileDayMs)
        {
            fileDayMs = 0;
            if (string.IsNullOrWhiteSpace(srcFile)) return false;
            string name = System.IO.Path.GetFileNameWithoutExtension(srcFile);
            int at = name.LastIndexOf("impact", StringComparison.OrdinalIgnoreCase);
            string digits = at >= 0 && at + 6 <= name.Length
                ? new string(name.Substring(at + 6).TakeWhile(char.IsDigit).ToArray())
                : "";
            if (digits.Length != 8) return false;
            if (!int.TryParse(digits[..4], out int y)
                || !int.TryParse(digits.Substring(4, 2), out int mo)
                || !int.TryParse(digits.Substring(6, 2), out int d)) return false;
            try
            {
                var day = new DateTime(y, mo, d, 0, 0, 0, DateTimeKind.Unspecified);
                if (day.Year < 2000 || day.Year > 2999) return false;
                fileDayMs = ToMs(day);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 把行内 [HH:mm:ss.fff] 解析成绝对时刻：基于天轴单调推断跨日。
        /// 返回 false = 行无时间戳（续行，沿用上一时刻）。
        /// </summary>
        /// <summary>
        /// 【t54 根本重写 · 用户明确原则】「同一份日志就是同一天，不同日志才存在跨天」。
        ///
        /// 实测验证（A1 20261125.log）：
        ///   · 文件名 better-genshin-impact20261125.log → 日期唯一权威 = 2026-11-25；
        ///   · 文件内时间戳从 [00:00:00.535] 到 [23:58:34.392]，整天都在这一份文件里；
        ///   · 所以 FileDayMs + 当日时刻 就是绝对时刻，不存在跨天推断的必要。
        ///
        /// 历史教训（为什么以前会错）：
        ///   · t44/t54 之前用「时间回退约24h → +1 天」推断跨日 —— 但那是错的前提：
        ///     同一份日志内时间戳本来就单调，回退 24h 在同一文件内不可能发生；
        ///   · 一旦发生，只可能是「增量喂入时基线来自另一段/另一个文件」或「乱序行」，
        ///     此时 +1 天会让 start 或 end 跑到次日 → 负时长(57 route) 或 24小时假时长(31 route)。
        ///
        /// 新逻辑（简单到不会错）：
        ///   · 绝对时刻 = FileDayMs + 当日时刻，永不 +1 天；
        ///   · LastRawMs 只用于时钟倒退的可观测（不参与计算），保留给跨文件/重扫诊断；
        ///   · 真跨天由 OnFileChanged（换文件）处理 —— 那是唯一合法的跨天边界，已有实现。
        /// </summary>
        private static bool ResolveTs(DayClock clock, string timeText, out long ms)
        {
            ms = 0;
            if (!TimeSpan.TryParse(timeText, CultureInfo.InvariantCulture, out var ts)) return false;
            // 同一份日志 = 同一天：文件名日期 + 行内时刻 = 绝对时刻，绝不 +1 天。
            // 跨天只可能发生在「换文件」时（由 OnFileChanged 处理）。
            long raw = clock.FileDayMs + (long)ts.TotalMilliseconds;
            clock.LastRawMs = raw;
            clock.LastMs = raw;
            ms = raw;
            return true;
        }

        /// <summary>定位某位置所在行行首的 [时间戳]，返回其绝对毫秒。</summary>
        private static bool StampAt(string text, int position, DayClock clock, out long ms)
        {
            ms = 0;
            int start = text.LastIndexOf('\n', Math.Max(0, position - 1));
            start = start < 0 ? 0 : start + 1;
            int len = Math.Min(80, text.Length - start);
            if (len <= 0) return false;
            string prefix = text.Substring(start, len);
            var m = RxLineTs.Match(prefix);
            if (!m.Success) return false;
            string date = m.Groups["date"].Success ? m.Groups["date"].Value.TrimEnd(' ', 'T') : "";
            string time = m.Groups["time"].Value;
            if (date.Length > 0)
            {
                // 行内显式日期（罕见）：权威，直接解析
                if (DateTime.TryParse(date + " " + time, CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var at))
                { ms = ToMs(at); clock.LastMs = ms; return true; }
            }
            return ResolveTs(clock, time, out ms);
        }
        // ── t8 原始事实归档（同 chunk、同一次逐行遍历，不读文件）────────────
        private const int RawLineCharLimit = 16 * 1024;
        private static readonly Dictionary<string,long> RawByteOffsets = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string,long> RawChunkSequences = new(StringComparer.OrdinalIgnoreCase);
        private static string RawHashOf(string text)
            => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];

        private static void ArchiveRawLine(string user, string raw, int lineStart, long lineMs,
            string level, string srcFile, List<RawEvent> output, long chunkOffset, string anchor,
            string currentChunkText, int currentChunkLength, long fileSize, long fileWriteTicks)
        {
            string line = raw.TrimEnd('\r');
            string hash = RawHashOf(line);
            long collected = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string rawId = string.Join('\u001f', user, BaseName(srcFile), "chunk", chunkOffset, lineStart);
            int terminatorBytes = 0;
            if (lineStart + raw.Length < currentChunkLength)
            {
                bool crlf = lineStart + raw.Length + 1 < currentChunkLength
                    && currentChunkText[lineStart + raw.Length] == '\r'
                    && currentChunkText[lineStart + raw.Length + 1] == '\n';
                terminatorBytes = crlf ? 2 : 1;
            }
            long byteOffset;
            lock (StateLock)
            {
                string key = user + "\u001f" + BaseName(srcFile);
                byteOffset = RawByteOffsets.TryGetValue(key, out long prior) ? prior : 0;
                RawByteOffsets[key] = byteOffset + Encoding.UTF8.GetByteCount(raw) + terminatorBytes;
            }
            string logger = "";
            var m = RxLineTs.Match(line);
            if (m.Success)
            {
                int close = line.IndexOf(']', m.Index + m.Length);
                if (close > m.Index + m.Length)
                    logger = line[(m.Index + m.Length)..close].Trim();
            }
            // ★ t49 存储策略A：raw_event 只存「有锚点的行 + 错误/警告行」。
            //   RawByteOffsets 已在上面 lock(StateLock) 里推进，即使本行不入库也绝不能 return。
            //   否则被过滤行的字节数不累加 → 后续行 byte_offset 全部偏移（静默错数据）。
            bool keep = !string.IsNullOrEmpty(anchor) || level is "ERR" or "WRN" or "FTL";
            if (keep)
            {
            output.Add(new RawEvent
            {
                RawId = rawId, Account = user, SrcFile = BaseName(srcFile),
                ChunkOffset = chunkOffset, LineOffset = lineStart, LineNo = lineStart,
                TsMs = lineMs > 0 ? lineMs : null, Level = level, Logger = logger,
                // ★ t44 修复：原硬编码 RawText="" 导致 raw_text 全空（raw_chars 却有值，
                //   11.7 万条 A1 事件全不可诊断）。存原文并按 RawLineCharLimit 截断。
                RawText = line.Length > RawLineCharLimit ? line[..RawLineCharLimit] : line,
                RawChars = line.Length, RawTruncated = line.Length > RawLineCharLimit,
                ByteOffset = byteOffset, ByteLength = Encoding.UTF8.GetByteCount(raw) + terminatorBytes,
                FileSizeAtCollection = fileSize, FileWriteAtCollectionTicks = fileWriteTicks,
                Anchor = anchor, CollectedAtMs = collected, ContentHash = hash, Occurrence = 0,
                SourceDay = TryFileDay(srcFile, out long rawFileDay) ? DayOf(rawFileDay) : ""
            });
            }
        }


        private static string AnchorOf(string line)
        {
            // 只复用采集器已有锚点定义，不在 Store 重跑正则。
            var tags = new List<string>(4);
            void Add(bool hit, string tag) { if (hit && !tags.Contains(tag)) tags.Add(tag); }
            Add(RxGroupStart.IsMatch(line),"run_start"); Add(RxGroupFinish.IsMatch(line),"run_finish");
            Add(RxGroupFail.IsMatch(line),"run_fail"); Add(RxGroupCancel.IsMatch(line),"run_cancel");
            Add(RxScriptStart.IsMatch(line),"script_start"); Add(RxScriptEndDur.IsMatch(line)||RxScriptEndPlain.IsMatch(line),"script_end");
            Add(RxRouteLoopEnter.IsMatch(line),"route_start"); Add(RxMapTrackingStart.IsMatch(line),"map_tracking");
            Add(RxCdLineStart.IsMatch(line)||RxCdLineOk.IsMatch(line),"cd_line");
            Add(RxRouteIdxStart.IsMatch(line)||RxRouteIdxDone.IsMatch(line),"route_index");
            Add(RxRoutePlanCount.IsMatch(line),"route_plan"); Add(RxPathGroupHeader.IsMatch(line)||RxTraverseStart.IsMatch(line),"route_context");
            Add(RxPick.IsMatch(line),"picked"); Add(RxNoTarget.IsMatch(line),"no_target"); Add(RxSkipped.IsMatch(line),"skipped");
            Add(RxExp.IsMatch(line),"exp"); Add(RxMora.IsMatch(line),"mora");
            return string.Join(",",tags);
        }

        private static bool IsPickRawLine(string line)
            => RxPick.IsMatch(line) || RxNoTarget.IsMatch(line) || RxSkipped.IsMatch(line);

        // ── 对外 API ────────────────────────────────────────────────────

        /// <summary>
        /// 启动重建：把 DB 里的 open 行灌回内存窗口（跨重启续跑的锚点）。
        /// 幂等，线程安全；只应被集成层调用一次（不新增定时器）。
        /// </summary>
        public static async Task EnsureBootedAsync(CancellationToken ct = default)
        {
            List<RunRecord> rows;
            try
            {
                var open = await RunRecordStore.LoadOpenRunsAsync(null, ct).ConfigureAwait(false);
                rows = open?.ToList() ?? new List<RunRecord>();
            }
            catch { return; }   // DB 暂不可用：等下次调用再试

            // ★★ t58 修复（残留 Running 的根因）：**陈旧的 open 行必须在启动时清掉**。
            //
            //   原行为：把 DB 里所有 open 行灌回内存，不管它有多老。
            //   后果：几天前的 open 行永远等不到关闭事件（日志已轮转、BGI 已重启），
            //   于是永久停在 Running —— 实测残留 7 条（11-22 ~ 11-25 的联机锄地/混合精英）。
            //
            //   新行为：按「文件日期」判断 —— 如果 open 行属于**今天之外的日志**，
            //   它不可能还有后续事件（日志已经关闭轮转），直接 close 掉：
            //     · end = 该 run 的最后已知时刻（StartAtMs，没有更多信息）；
            //     · state = Interrupted；
            //     · detail 明确写「进程重启时日志已轮转，该组无后续事件」。
            //   今天的 open 行仍然保留（那可能是真在跑的，等事件来续）。
            long todayStart = ToMs(DateTime.Now.Date);
            int staleClosed = 0;
            lock (StateLock)
            {
                foreach (var row in rows)
                {
                    if (string.IsNullOrWhiteSpace(row.Account) || string.IsNullOrWhiteSpace(row.GroupName))
                        continue;

                    // 属于更早日期 → 陈旧，直接收尾（不进 OpenRuns）
                    if (row.StartAtMs > 0 && row.StartAtMs < todayStart)
                    {
                        try
                        {
                            RunRecordStore.CloseRun(new RunClose
                            {
                                RunId = row.RunId,
                                EndAtMs = row.StartAtMs,
                                State = "Interrupted",
                                Detail = "进程重启时该组所属日志已轮转，无后续事件（自动收尾）",
                                Exp = row.Exp,
                                Mora = row.Mora,
                                Picks = row.Picks,
                                PickUnattributed = 0,
                            });
                            staleClosed++;
                        }
                        catch { }
                        continue;
                    }

                    var run = new OpenRun
                    {
                        Account = row.Account,
                        Group = row.GroupName,
                        StartAtMs = row.StartAtMs,
                        RunId = row.RunId,
                        ScriptCount = row.ScriptCount,
                        SrcFileStart = row.SrcFileStart,
                        LastEventMs = row.StartAtMs,
                        Exp = row.Exp,
                        Mora = row.Mora,
                        Picks = row.Picks,
                        FromDb = true,
                    };
                    OpenRuns[row.Account] = run;
                }
            }
            if (staleClosed > 0)
            {
                try
                {
                    LogBus.Ok("运行记录·陈旧收尾", staleClosed + " 条历史 Running 已自动关闭（日志已轮转）");
                }
                catch { }
            }
        }

        /// <summary>
        /// 文件滚动 / 新文件开始：跨天补全入口（契约 §4.4）。
        /// Feed 前调用一次（集成层在 BgiLogCenter 检测到 ResetForNewFile 时）。
        /// 内存有 open 窗口且 gap ≤ 300s → 续跑（不改名不改时刻）；
        /// gap &gt; 300s → close(Interrupted)，窗口移除。
        /// </summary>
        public static void OnFileChanged(string userName, string srcFile)
        {
            if (string.IsNullOrWhiteSpace(userName)) return;

            long fileDayMs = TryFileDay(srcFile, out long day) ? day : 0;

            lock (StateLock)
            {
                var clock = GetClock(userName);
                if (fileDayMs > 0)
                {
                    // 天轴跳到新文件日期；LastRawMs 用 -∞ 重置，避免跨日误判
                    clock.FileDayMs = fileDayMs;
                    clock.LastRawMs = long.MinValue;
                }

                // ★ t6 硬要求三主路径：换文件 = 拆两段（不是续跑也不是 ghost）。
                //   第一段：end=当日最后事件时刻（兜底 23:59:59.999）→ close Interrupted
                //           + detail 标「跨天换文件分段(非异常)」；day_split=1。
                //   第二段：惰性开窗——先记 PendingSplit（继承组名），首个事件到达时
                //           才 AddRun（start=首行 ts，确定性可重扫复现）；90s 无事件不建。
                if (OpenRuns.TryGetValue(userName, out var run) && run != null
                    && fileDayMs > 0 && clock.LastMs > 0
                    && DayOf(clock.LastMs) != DayOf(fileDayMs))   // 日期确实换了
                {
                    // ★★ t71 修复（实测：今天正在跑的「联机锄地-混合精英」被误判为中断）：
                    //
                    //   场景：回填历史日志（11-20…11-25）时，OnFileChanged 被逐份文件调用。
                    //   当处理到比「当前 open run」更早的文件时，上面的日期不等条件成立，
                    //   于是把**今天的** run 当成「跨天分段」关掉了：
                    //     start=08:32:20  end=08:32:20  state=Interrupted
                    //     detail=「跨天换文件分段(非异常)；结束时刻早于开始（乱序行），已夹齐」
                    //   而它其实一直在跑（日志 09:48 仍在写该组）。
                    //
                    //   根因：跨天分段的语义是「时间往前推进到新的一天」，
                    //   **绝不能用一份更早的文件去关闭一个更晚的 run**。
                    //   回填历史时 srcFile 比 run 旧，方向反了，必须直接跳过。
                    if (run.StartAtMs >= fileDayMs)
                    {
                        // run 的开始日期 >= 当前文件日期 → 这个 run 比文件还新，
                        // 是「回填历史文件时遇到的今天的 run」，不能关它。
                        // 只更新时钟基线到该文件日期，让后续解析落在那一天。
                        return;
                    }

                    long segEnd = run.LastEventMs > 0 ? run.LastEventMs : clock.LastMs;
                    // 兜底：当日最后事件晚于新文件 0 点（时钟漂移）就夹到 23:59:59.999
                    long dayEnd = fileDayMs - 1;
                    if (segEnd >= fileDayMs) segEnd = dayEnd;
                    // ★★ t56 修复（src_file_end 错误）：
                    //   原来用 FindPriorFile(srcFile) 猜「上一份日志」，但那是按目录里日期最大的
                    //   更早文件猜的，在历史回填（连续跨多份文件）时不可靠 —— 实测把 src_file_end
                    //   写成了 20261119.log（当时 srcFile 已不是相邻那份）。
                    //   正确做法：这条 run 本来就有 SrcFileStart（它开始时的文件名），那才是权威来源。
                    //   只有 SrcFileStart 为空（极端异常）才退回推断。
                    string endFile = run.SrcFileStart.Length > 0 ? run.SrcFileStart : FindPriorFile(srcFile);
                    CloseRunLocked(run, "Interrupted", "跨天换文件分段(非异常)",
                        segEnd, endFile,
                        backfilled: false, daySplit: true);
                    OpenRuns.Remove(userName);

                    PendingSplits[userName] = new PendingDaySplit
                    {
                        Group = run.Group,                 // O2：组名必须继承，绝不能丢
                        OldSegEndMs = segEnd,
                        NewFile = BaseName(srcFile),
                        DeadlineMs = NowMs() + DaySegmentFirstEventTimeoutMs,
                    };
                    return;
                }

                // 同一天内的文件重建（罕见）：维持原有续跑/陈旧兜底语义
                long firstTs = clock.LastMs;
                if (!OpenRuns.TryGetValue(userName, out var run2) || run2 == null) return;

                // ★ t5 O1 订正：重启后时钟无基线（LastMs=0）→ 不 close open 行
                //   （等首个事件来重建基线；若组已结束走 EndGroup/backfill，绝不 ghost）。
                if (firstTs <= 0) return;

                long gap = firstTs > 0 && run2.LastEventMs > 0 ? firstTs - run2.LastEventMs : long.MaxValue;
                if (gap <= ContinuityGapMs)
                {
                    // 续跑：组名/开始时刻保持权威。is_backfilled 仅在「重启后靠 DB open 行恢复」
                    // 或「结束事件时内存无窗口走 DB 补全」时置 1（契约 §4.5：正常续跑 = 0）。
                    return;
                }
                // 陈旧 open 兜底：close Interrupted，end = 最近归因时刻
                CloseRunLocked(run2, "Interrupted", "跨天时间断档，未正常结束",
                    run2.LastEventMs, srcFile, backfilled: true);
                OpenRuns.Remove(userName);
            }
        }

        /// <summary>同目录按日期找上一份日志文件名（srcFile 的前一天）。</summary>
        private static string FindPriorFile(string srcFile)
        {
            try
            {
                string? dir = System.IO.Path.GetDirectoryName(srcFile);
                if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) return BaseName(srcFile);
                // 文件名形如 better-genshin-impactYYYYMMDD.log：取日期更小的最近一份
                string? best = null; long bestDay = 0;
                if (!TryFileDay(srcFile, out long curDay)) return BaseName(srcFile);
                foreach (string f in System.IO.Directory.GetFiles(dir, "better-genshin-impact*.log"))
                {
                    if (TryFileDay(f, out long d) && d < curDay && d > bestDay) { bestDay = d; best = f; }
                }
                return best != null ? BaseName(best) : BaseName(srcFile);
            }
            catch { return BaseName(srcFile); }
        }

        /// <summary>
        /// 用一段**新增**日志文本更新记录。只做内存归因 + 入队，零文件 IO。
        /// 重喂（重启重扫）安全：自然键确定性，写线程 INSERT OR IGNORE。
        /// </summary>
        public static void Feed(string userName, string logText, string srcFile,
            long fileSize = 0, long fileWriteTicks = 0)
        {
            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(logText)) return;

            try
            {
                List<Ev> events = ScanEvents(userName, logText, srcFile, fileSize, fileWriteTicks);
                if (events.Count > 0)
                {
                    events.Sort((a, b) => a.Pos.CompareTo(b.Pos));
                    lock (StateLock)
                    {
                        // ★ t6 惰性开窗：换文件后首个事件（无论什么类型）实体化次日段。
                        //   用第一条事件的时刻建段，随后的 StartGroup 等会并入该段。
                        if (PendingSplits.ContainsKey(userName) && !OpenRuns.ContainsKey(userName))
                        {
                            MaterializePendingSplitAt(userName, events, srcFile);
                        }
                        foreach (var ev in events) ev.Apply?.Invoke();
                    }
                }
                // t6 P)：有产出 = running（状态时段观测；不额外记段）
                ObserveBgiState(userName, clockLastMs(userName), null, srcFile);

                // ★ t6 H/L 守恒自检：sum(route.picked)+run级 picked == run.picks（pick 口径）。
                //   三类之和守恒在验证工程对 pick_event 表断言（行数=日志行数）。
                //   差异写 run.detail/pick_unattributed，绝不静默吞掉。
                CheckPickConservation(userName);
            }
            catch { /* 解析失败不能影响日志主流程 */ }
        }

        /// <summary>位置有序事件（同 chunk 内按出现顺序应用到状态机）。</summary>
        private delegate void EvApply();
        private sealed class Ev
        {
            public int Pos;
            public EvApply? Apply;
            /// <summary>t6：事件时刻（惰性开窗取首事件 ts 用）。</summary>
            public long TsMs;
            /// <summary>t9/raw分级：命中的锚点类别，只决定原文保留档位。</summary>
            public string Anchor = "";
        }

        /// <summary>扫一个 chunk：逐行解析时间戳 + 错误块 + 全部锚点匹配。</summary>
        private static List<Ev> ScanEvents(string user, string logText, string srcFile, long fileSize = 0, long fileWriteTicks = 0)
        {
            var events = new List<Ev>();
            var rawEvents = new List<RawEvent>();
            string rawFileKey = user + "\u001f" + BaseName(srcFile);
            long rawChunkOffset;
            lock (StateLock)
            {
                // 整文件重扫时（DayClock 基线尚未建立或 LastMs=0）从 0 开始；
                // 普通连续 chunk 顺序递增，同一次扫描与重扫的 rawId 完全一致。
                bool rawRescan = !Clocks.TryGetValue(user, out var previousClock) || previousClock.LastMs <= 0;
                rawChunkOffset = rawRescan ? 0 : RawChunkSequences.TryGetValue(rawFileKey, out long seq) ? seq + 1 : 0;
                RawChunkSequences[rawFileKey] = rawChunkOffset;
            }

            DayClock clock;
            lock (StateLock) { clock = GetClock(user); }

            // ★★ t77 修复（实测：今天的日志里出现 11-24 的时间戳，导致数据串天）：
            //
            //   原逻辑：`if (clock.FileDayMs == 0 && !TryFileDay(...))` —— 只在**没设置过**时才设。
            //   而回填历史文件时 OnFileChanged 会把 clock.FileDayMs 改到历史那一天（如 11-24），
            //   回填完成后**没有恢复**。此后喂今天的日志，绝对时刻 = 11-24 + [10:12:27]，
            //   于是今天的数据被算成 11-24 —— 实测 route 表里 4 条今天写入的记录落在 11-24。
            //
            //   修法：**每次 Feed 都按 srcFile 校验**。文件名日期与 clock 不一致就重置天轴
            //   （LastRawMs 也归零，避免时钟倒退误判）。日志文件名自带日期是权威依据。
            if (TryFileDay(srcFile, out long feedFileDay) && feedFileDay > 0)
            {
                if (clock.FileDayMs != feedFileDay)
                {
                    lock (StateLock)
                    {
                        clock.FileDayMs = feedFileDay;
                        clock.LastRawMs = long.MinValue;
                    }
                }
            }
            else if (clock.FileDayMs == 0)
            {
                // 无文件名日期：退化为当天（只影响时刻精度，不影响归因正确性）
                lock (StateLock) { clock.FileDayMs = ToMs(DateTime.Now.Date); }
            }

            long lastMs = clock.LastMs;
            var lines = SplitLines(logText);
            var lineMsByStart = new Dictionary<int, long>(lines.Count);
            // ★ t44：每行的锚点（行首 → AnchorOf 结果）。事件在**行循环结束时一次性**盖章，
            //   替代原先「每处理一行就遍历全部历史事件」的 O(n²) 写法（4MB chunk 61s → 1.3s 的元凶）。
            var lineAnchorByStart = new Dictionary<int, string>(lines.Count);
            int pos = 0;
            bool firstTsLineCaptured = false;

            foreach (var (raw, lineStart) in lines)
            {
                string line = raw.TrimEnd('\r');
                long lineMs = lastMs;
                string level = "";

                if (line.Length > 0)
                {
                    var m = RxLineTs.Match(line);
                    if (m.Success)
                    {
                        level = m.Groups["level"].Value;
                        if (ResolveTs(clock, m.Groups["time"].Value, out long ms))
                        {
                            if (ms < lastMs - 12 * 3600_000)
                            {
                                rawChunkOffset = 0;
                                lock (StateLock) { RawChunkSequences[rawFileKey] = 0; RawByteOffsets[rawFileKey] = 0; }
                            }
                            lineMs = ms; lastMs = ms;
                            if (!firstTsLineCaptured)
                            {
                                firstTsLineCaptured = true;
                                // t6：chunk 首行时间戳（供惰性开窗取「次日日志首行时刻」）
                                FirstLineTs[user] = ms;
                            }
                        }
                    }
                }

                lineMsByStart[lineStart] = lineMs;
                lineAnchorByStart[lineStart] = AnchorOf(line);

                // t8：同一行原文先入归档；无锚点、异常样式组名、无有效结果均不丢。
                ArchiveRawLine(user, raw, lineStart, lineMs, level, srcFile, rawEvents, rawChunkOffset, lineAnchorByStart[lineStart], logText, logText.Length, fileSize, fileWriteTicks);
                // [ERR] 块累积：头行开块，续行（无时间戳头）附加
                ScanErrBlock(user, line, lineMs, level, srcFile, lineStart, events);

                // 其余锚点：单行匹配 → 生成事件
                ScanAnchors(user, line, lineMs, lineStart, srcFile, events, rawChunkOffset);

                // ★ t44 修复：原写法 foreach(ev in events) ev.Anchor=AnchorOf(line) 在**行循环内**
                //   遍历全部历史事件（O(n²)，4MB 真实 A1 chunk 实测 61s，34MB 首扫≈9 分钟 CPU 满载）；
                //   且语义错误——把先前事件的锚点改写为当前行。改为循环后按事件自身行盖章。
                pos = lineStart + raw.Length;
            }

            clock.LastMs = lastMs;
            // 单 chunk 批量入队：后台线程单事务写入；原始事实不经过状态机筛选。
            RunRecordStore.AddRawEvents(rawEvents);
            // t6：给每个事件标时刻（事件 Pos → 所在行的 lineMs；供惰性开窗取首事件 ts）
            // t44：锚点同帧盖章——事件按自身所在行取 anchor（O(事件数)，替代 O(行×事件)）
            foreach (var ev in events)
            {
                ev.TsMs = TimestampOf(ev, lineMsByStart);
                ev.Anchor = AnchorOfLine(ev.Pos, lineAnchorByStart);
            }
            return events;
        }

        /// <summary>t44：按事件 Pos 反查所在行的锚点（与 TimestampOf 同一思路，O(事件数)）。</summary>
        private static string AnchorOfLine(int pos, Dictionary<int, string> lineAnchorByStart)
        {
            int best = -1;
            foreach (var kv in lineAnchorByStart)
                if (kv.Key <= pos && kv.Key > best) best = kv.Key;
            return best >= 0 && lineAnchorByStart.TryGetValue(best, out string? a) ? a : "";
        }

        private static long TimestampOf(Ev ev, Dictionary<int, long> lineMsByStart)
        {
            // 用事件的 Pos 反查所在行起点；找不到（err 块 Pos 为块起点，也在行内）取最近的行时刻
            int best = -1;
            foreach (var kv in lineMsByStart)
                if (kv.Key <= ev.Pos && kv.Key > best) best = kv.Key;
            return best >= 0 && lineMsByStart.TryGetValue(best, out long ms) ? ms : 0;
        }

        private static List<(string raw, int start)> SplitLines(string text)
        {
            var list = new List<(string, int)>(Math.Min(4096, text.Length / 64 + 8));
            int start = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i < text.Length && text[i] == '\n')
                {
                    list.Add((text.Substring(start, i - start), start));
                    start = i + 1;
                }
            }
            return list;
        }

        private static DayClock GetClock(string user)
        {
            if (!Clocks.TryGetValue(user, out var c))
            {
                c = new DayClock { FileDayMs = 0, LastMs = 0, LastRawMs = long.MinValue };
                Clocks[user] = c;
            }
            return c;
        }

        /// <summary>单行锚点匹配（除 ERR 块外全部边界事件）。</summary>
        private static void ScanAnchors(string user, string line, long lineMs, int lineStart,
            string srcFile, List<Ev> events, long chunkOffset)
        {
            if (line.Length == 0) return;

            foreach (Match m in RxGroupStart.Matches(line))
            {
                string g = m.Groups["g"].Value.Trim();
                int n = int.TryParse(m.Groups["n"].Value, out int c) ? c : 0;
                if (!ValidGroup(g)) continue;
                events.Add(new Ev { Pos = lineStart + m.Index, Apply = () => StartGroup(user, g, n, lineMs, srcFile) });
            }
            foreach (Match m in RxGroupFinish.Matches(line))
            {
                string g = m.Groups["g"].Value.Trim();
                if (!ValidGroup(g)) continue;
                events.Add(new Ev { Pos = lineStart + m.Index, Apply = () => EndGroup(user, g, "Finished", "", lineMs, srcFile) });
            }
            foreach (Match m in RxGroupFail.Matches(line))
            {
                string g = m.Groups["g"].Value.Trim();
                string r = m.Groups["r"].Value.Trim();
                if (!ValidGroup(g)) continue;
                string state = string.Equals(r, "Cancelled", StringComparison.OrdinalIgnoreCase)
                    ? "Cancelled" : "Failed";
                events.Add(new Ev { Pos = lineStart + m.Index, Apply = () => EndGroup(user, g, state, r, lineMs, srcFile) });
            }
            foreach (Match m in RxGroupCancel.Matches(line))
            {
                string g = m.Groups["g"].Value.Trim();
                if (!ValidGroup(g)) continue;
                events.Add(new Ev { Pos = lineStart + m.Index, Apply = () => EndGroup(user, g, "Cancelled", "Cancelled", lineMs, srcFile) });
            }

            foreach (Match m in RxScriptStart.Matches(line))
            {
                string name = m.Groups["name"].Value.Trim();
                if (name.Length == 0) continue;
                events.Add(new Ev { Pos = lineStart + m.Index, Apply = () => StartRoute(user, "script", name, null, lineMs, srcFile) });
            }

            // 脚本结束（带耗时）→ 关 script 窗口；不带耗时且名字 == 当前打开路线 → 关 route 窗口
            var meDur = RxScriptEndDur.Match(line);
            if (meDur.Success)
            {
                string name = meDur.Groups["name"].Value.Trim();
                if (name.Length > 0)
                    events.Add(new Ev { Pos = lineStart + meDur.Index, Apply = () => EndRoute(user, name, "script", lineMs, meDur.Groups["dur"].Value) });
            }
            else
            {
                foreach (Match m in RxScriptEndPlain.Matches(line))
                {
                    string name = m.Groups["name"].Value.Trim();
                    if (name.Length == 0) continue;
                    events.Add(new Ev { Pos = lineStart + m.Index, Apply = () => EndRoute(user, name, null, lineMs, "") });
                }
            }

            foreach (Match m in RxRouteLoopEnter.Matches(line))
            {
                string name = m.Groups["name"].Value.Trim();
                if (name.Length == 0) continue;
                events.Add(new Ev { Pos = lineStart + m.Index, Apply = () => StartRoute(user, "route", name, CurrentScriptId(user), lineMs, srcFile) });
            }

            // ── t6 A) 新锚点族：地图追踪任务（script 层，第三类 script）────────
            //   ★ 与「进入路线循环 route="X"」成对出现（相隔约 12 行，同一事实的两个阶段）：
            //     若当前窗口已是同名路线 → 视为该路线的执行阶段（Touch），不另开 script 窗。
            foreach (Match m in RxMapTrackingStart.Matches(line))
            {
                string name = m.Groups["name"].Value.Trim();
                if (name.Length == 0) continue;
                bool sameAsOpenRoute = false;
                lock (StateLock)
                {
                    if (OpenRuns.TryGetValue(user, out var r) && r?.Route != null
                        && string.Equals(r.Route.Name, name, StringComparison.OrdinalIgnoreCase)
                        && r.Route.Kind != "script")
                        sameAsOpenRoute = true;
                }
                if (sameAsOpenRoute)
                    events.Add(new Ev { Pos = lineStart + m.Index, Apply = () => TouchRun(user, lineMs) });
                else
                {
                    // ★ t51 问题2：名字以 .json 结尾 = **路线文件**，不是脚本。
                    //   真正的脚本是「全自动周一」「世界权限」「等待到指定时间」这类无扩展名中文名。
                    //   原来一律记成 script，导致同一个 .json 既出现在 route 行、又出现在 script 行。
                    //   （若上面 StartRoute 的实时同名合并已生效，这一条会被吞掉不重复落库；
                    //     这里只保证**单独出现**的 map 锚点也不会被误标成 script。）
                    bool isRouteFile = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
                    string mapKind = isRouteFile ? "route" : "script";
                    events.Add(new Ev { Pos = lineStart + m.Index, Apply = () => StartRoute(user, mapKind, name, null, lineMs, srcFile) });
                }
            }

            // ── t6 A) 激活CD 线路层（kind="cd_line"；独立于 script/route 两层）──
            //   开始：挤掉前一线路（结构性边界）；成功：正常收尾 Finished。
            //   失败由 ERR 块归并（HasError→Failed + detail 由 err 表承载原因）。
            foreach (Match m in RxCdLineStart.Matches(line))
            {
                string name = m.Groups["name"].Value.Trim();
                if (name.Length == 0) continue;
                events.Add(new Ev { Pos = lineStart + m.Index, Apply = () => StartRoute(user, "cd_line", name, CurrentScriptId(user), lineMs, srcFile) });
            }
            foreach (Match m in RxCdLineOk.Matches(line))
            {
                string name = m.Groups["name"].Value.Trim();
                if (name.Length == 0) continue;
                events.Add(new Ev { Pos = lineStart + m.Index, Apply = () => EndRoute(user, name, null, lineMs, "") });
            }

            // ── t6 锄地补充锚点 ─────────────────────────────────────────────
            //   名字型「开始第 x/y 条线路」：与「进入路线循环」同语义（route 开窗/挤掉），带序号供 Skipped 推断
            foreach (Match m in RxRouteIdxStart.Matches(line))
            {
                string name = m.Groups["name"].Value.Trim();
                if (name.Length == 0) continue;
                int idx = int.TryParse(m.Groups["idx"].Value, out int i1) ? i1 : 0;
                int total = int.TryParse(m.Groups["total"].Value, out int i2) ? i2 : 0;
                events.Add(new Ev
                {
                    Pos = lineStart + m.Index,
                    Apply = () => StartRouteIdx(user, name, CurrentScriptId(user), lineMs, srcFile, idx, total),
                });
            }
            //   「完成第 x/y 条线路」：正常收尾（Finished；HasError→Failed）
            foreach (Match m in RxRouteIdxDone.Matches(line))
            {
                string name = m.Groups["name"].Value.Trim();
                if (name.Length == 0) continue;
                events.Add(new Ev { Pos = lineStart + m.Index, Apply = () => EndRoute(user, name, null, lineMs, "") });
            }
            //   数量型（共加载 N 条路线 / 上报路线列表 N 条）：落到 run.planned_routes（C）
            var plan = RxRoutePlanCount.Match(line);
            if (plan.Success)
            {
                int n = 0;
                if (plan.Groups["n1"].Success) n = int.TryParse(plan.Groups["n1"].Value, out int v1) ? v1 : 0;
                else if (plan.Groups["n3"].Success) n = int.TryParse(plan.Groups["n3"].Value, out int v3) ? v3 : 0;
                if (n > 0)
                    events.Add(new Ev { Pos = lineStart + plan.Index, Apply = () => SetPlannedRoutes(user, n, srcFile) });
            }
            // 路径组头 / 开始遍历：仅记录上下文（不生成路线行）
            if (RxPathGroupHeader.IsMatch(line) || RxTraverseStart.IsMatch(line))
            {
                events.Add(new Ev { Pos = lineStart, Apply = () => TouchRun(user, lineMs) });
            }

            // ★ t6 硬要求二：三类拾取事件全投递（picked/no_target/skipped）。
            //   picked 语义不变（=原 RxPick）；no_target/skipped 原 100% 丢弃，现在必须记录。
            foreach (Match m in RxPick.Matches(line))
            {
                string item = m.Groups["item"].Value.Trim();
                if (item.Length == 0) continue;   // 空 capture 无物品字段；原文仍已由 raw_event 保留
                events.Add(new Ev
                {
                    Pos = lineStart + m.Index,
                    Apply = () => AccumPick(user, item, lineMs, "picked", lineStart, m.Index, srcFile, chunkOffset),
                });
            }
            foreach (Match m in RxNoTarget.Matches(line))
            {
                events.Add(new Ev
                {
                    Pos = lineStart + m.Index,
                    Apply = () => AccumPick(user, "无有效结果", lineMs, "no_target", lineStart, m.Index, srcFile, chunkOffset),
                });
            }
            foreach (Match m in RxSkipped.Matches(line))
            {
                string item = m.Groups["item"].Value.Trim();
                events.Add(new Ev
                {
                    Pos = lineStart + m.Index,
                    Apply = () => AccumPick(user, item, lineMs, "skipped", lineStart, m.Index, srcFile, chunkOffset),
                });
            }
            foreach (Match m in RxExp.Matches(line))
            {
                long v = ParseV(m);
                if (v <= 0) continue;
                events.Add(new Ev { Pos = lineStart + m.Index, Apply = () => AccumReward(user, v, 0, lineMs, srcFile) });
            }
            foreach (Match m in RxMora.Matches(line))
            {
                long v = ParseV(m);
                if (v <= 0) continue;
                events.Add(new Ev { Pos = lineStart + m.Index, Apply = () => AccumReward(user, 0, v, lineMs, srcFile) });
            }
        }

        private static long ParseV(Match m)
        {
            if (long.TryParse(m.Groups["v1"].Value, out var a) && a > 0) return a;
            if (long.TryParse(m.Groups["v2"].Value, out var b) && b > 0) return b;
            return 0;
        }

        private static string? CurrentScriptId(string user)
        {
            if (!OpenRuns.TryGetValue(user, out var run) || run?.Route == null) return null;
            return run.Route.Kind == "script" ? run.Route.RouteId : run.Route.ParentRouteId;
        }

        /// <summary>
        /// 配置组名校验（**采集层过滤**）。
        ///
        /// ★ 为什么必须在这里拦：BGI 在「取消执行配置组: "X"」里会把**框架异常消息**
        ///   当成组名塞进来。实测生产库（run.db，2026-11-20 ~ 11-25）里出现了
        ///   "The operation was canceled." ×11、"A task was canceled." ×1 ——
        ///   它们一旦进 run 表就成了「一个配置组」，数据中心只能显示成一堆假卡片。
        /// ★ 只拦**明确的**异常文本（整串相等）与异常类名/取消短语，
        ///   不做其它语义猜测 —— 正常组名（中文 / 英文 / 带数字符号）一律放行。
        /// ★ raw_event 里那一行原文照旧保留（raw 不做有效性筛选），只是不再生成 run 行。
        /// </summary>
        internal static bool ValidGroup(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string n = name.Trim();
            foreach (string bad in BogusGroupNames)
                if (string.Equals(n, bad, StringComparison.OrdinalIgnoreCase)) return false;
            if (n.Contains("Exception", StringComparison.OrdinalIgnoreCase)) return false;
            if (n.Contains("was canceled", StringComparison.OrdinalIgnoreCase)
                || n.Contains("was cancelled", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        /// <summary>实测被误当成配置组名的框架异常消息（大小写不敏感整串匹配）。</summary>
        private static readonly string[] BogusGroupNames =
        {
            "The operation was canceled.",
            "The operation was cancelled.",
            "A task was canceled.",
            "A task was cancelled.",
            "OperationCanceledException",
            "TaskCanceledException",
        };

        /// <summary>
        /// [ERR] 多行块累积（契约 §1.5）：
        ///   头行（[ts] [ERR] … logger）开块；其后所有**无时间戳头**的行是消息体/栈，
        ///   直到出现下一个带时间戳头的行（任意级别）或块超过 512 行 / 64 KB 兜底。
        ///   块结束（换块/换文件/收尾）时一次性投递 ErrorRecord（含完整 detail）。
        /// </summary>
        private static void ScanErrBlock(string user, string line, long lineMs, string level,
            string srcFile, int lineStart, List<Ev> events)
        {
            bool hasTsHead = line.Length > 0 && line[0] == '[' && RxLineTs.Match(line).Success;
            var acc = GetErrAccum(user);

            if (hasTsHead)
            {
                // 新头行：先结算旧块（如果有）
                if (acc.InBlock) FlushErrBlock(user, acc, events);
                if (level == "ERR")
                {
                    acc.InBlock = true;
                    acc.TsMs = lineMs;
                    acc.HeaderLevel = "ERR";
                    acc.Detail.Clear();
                    acc.TrailingBlank = 0;
                    acc.CurrentFile = srcFile;
                    acc.Source = ExtractSource(line);
                    acc.LastErrTsMs = lineMs;
                    acc.LastErrSource = acc.Source;
                    acc.StartPos = lineStart;
                    AppendDetail(acc, line);
                }
                return;
            }

            if (!acc.InBlock)
            {
                // 未开块：检测「执行脚本时发生异常」/「XyzException: …」内容行 ——
                // 这是 ERR 头引用的完整异常文本（ClearScript 栈横跨十几行），必须并入最近 ERR 的 detail。
                if (LooksLikeExceptionBody(line))
                {
                    acc.InBlock = true;
                    acc.PendingException = true;
                    acc.TsMs = acc.LastErrTsMs > 0 ? acc.LastErrTsMs : lineMs;
                    acc.HeaderLevel = "ERR";
                    acc.Detail.Clear();
                    acc.TrailingBlank = 0;
                    acc.CurrentFile = srcFile;
                    acc.StartPos = lineStart;
                    if (acc.Source.Length == 0) acc.Source = acc.LastErrSource;
                    AppendDetail(acc, line);
                }
                return;
            }
            if (line.Length == 0)
            {
                acc.TrailingBlank++;
                return;   // 尾部空行不落 detail（日志为 [ts] 头 + 内容 + 空行 三段式）
            }
            while (acc.TrailingBlank > 0) { AppendDetail(acc, ""); acc.TrailingBlank--; }  // 块内空行保留
            AppendDetail(acc, line);
            if (acc.Detail.Length > 64 * 1024 || acc.TrailingBlank > 512) FlushErrBlock(user, acc, events);
        }

        private static ErrAccum GetErrAccum(string user)
        {
            if (!ErrBlocks.TryGetValue(user, out var a))
            {
                a = new ErrAccum();
                ErrBlocks[user] = a;
            }
            return a;
        }

        private static void AppendDetail(ErrAccum acc, string line)
        {
            if (acc.Detail.Length > 0) acc.Detail.Append('\n');
            acc.Detail.Append(line);
            if (acc.Detail.Length > 128 * 1024) acc.Detail.Length = 128 * 1024;   // 兜底防炸内存
        }

        /// <summary>头行尾部取 logger/category（最后一个空格后的标识符段）。</summary>
        /// <summary>异常正文特征：执行脚本时发生异常 / XxxException: … / at …（栈帧行由 InBlock 捕获）。</summary>
        private static bool LooksLikeExceptionBody(string line)
        {
            if (line.Length == 0) return false;
            if (line.StartsWith("执行脚本时发生异常", StringComparison.Ordinal)) return true;
            int at = line.IndexOf(' ');   // "Microsoft.ClearScript.ScriptEngineException: ..."
            string head = at > 0 ? line[..at] : line;
            return head.EndsWith("Exception", StringComparison.Ordinal)
                && line.Length > head.Length && line[head.Length] == ':';
        }

        private static string ExtractSource(string headerLine)
        {
            int idx = headerLine.LastIndexOf(']');
            if (idx < 0 || idx + 1 >= headerLine.Length) return "";
            return headerLine[(idx + 1)..].Trim();
        }

        /// <summary>结算当前累积块 → 投 ErrorRecord（含异常类型/消息提取）。</summary>
        private static void FlushErrBlock(string user, ErrAccum acc, List<Ev> events)
        {
            if (!acc.InBlock) return;
            acc.InBlock = false;
            string detail = acc.Detail.ToString();
            if (detail.Length == 0) return;

            string exceptionType = ExtractExceptionType(detail);
            string message = ExtractFirstMessage(detail);
            long ts = acc.TsMs;
            string source = acc.Source;
            string file = acc.CurrentFile;

            if (acc.PendingException)
            {
                // 异常栈：追加进最近一条 ERR 的 detail（同一条报错的完整详情），不再单开一行
                acc.PendingException = false;
                if (acc.LastErrDetail != null)
                {
                    acc.LastErrDetail.Append('\n').Append(detail);
                    return;
                }
            }

            var builder = new StringBuilder(detail);
            acc.LastErrDetail = builder;

            // ★ Pos = 块起始行位置：Apply 顺序落在「组开始之后、组结束之前」——
            //   EmitError 在 Apply 时从 OpenRuns 查窗口（归因正确），HasError 在 EndRoute 前置位。
            int pos = acc.StartPos;
            events.Add(new Ev
            {
                Pos = pos,
                Apply = () => EmitError(user, ts, exceptionType, message, builder.ToString(), source, file)
            });
        }

        private static string ExtractExceptionType(string detail)
        {
            var m = Regex.Match(detail, @"(?<type>[A-Za-z0-9_.]+Exception)");
            return m.Success ? m.Groups["type"].Value : "";
        }

        /// <summary>消息 = 头行之后第一条内容行（截断 500 字由 store 兜底）。</summary>
        private static string ExtractFirstMessage(string detail)
        {
            foreach (var raw in detail.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                // 跳过头行（带 [ts] 前缀）：消息取头行之后第一条内容行
                if (line[0] == '[' && RxLineTs.Match(line).Success) continue;
                int close = line.IndexOf(']');
                return close >= 0 && close + 1 < line.Length ? line[(close + 1)..].Trim() : line;
            }
            return "";
        }

        // ── 状态机动作（全部在 StateLock 内执行）────────────────────────

        /// <summary>
        /// Feed 侧惰性开窗：用首个事件的时刻建次日段（start=次日日志首个锚点行 ts，确定性）。
        /// 事件无时刻（不可能：每行都有 lineMs 兜底）→ 丢弃 pending 不建段。
        /// </summary>
        private static void MaterializePendingSplitAt(string user, List<Ev> events, string srcFile)
        {
            long start = 0;
            // 优先「chunk 首行时间戳」（O3：start=次日日志首行时刻——不要求首行是锚点行）
            if (FirstLineTs.TryGetValue(user, out long firstLine) && firstLine > 0) start = firstLine;
            // 锚点事件更早（首行无时间戳的极端）→ 用最早事件
            foreach (var ev in events)
                if (ev.TsMs > 0 && (start == 0 || ev.TsMs < start)) start = ev.TsMs;
            if (start <= 0) { PendingSplits.Remove(user); return; }
            MaterializePendingSplit(user, start, srcFile);
        }

        /// <summary>
        /// t6 惰性开窗实体化：PendingSplit 存在且事件未超 90s 兜底 → 用本事件时刻建次日段
        /// （AddRun start=atMs 确定性、组名继承、day_split=1、is_backfilled=0）。
        /// 超时 → 丢弃 pending（不建空段；迟到事件走陈旧 open 行兜底）。
        /// ★ 幂等：run_id 含 start_at_ms；重扫时同样首个事件重建同样时刻 → 同 id → INSERT OR IGNORE。
        /// </summary>
        private static void MaterializePendingSplit(string user, long atMs, string srcFile)
        {
            if (!PendingSplits.TryGetValue(user, out var p) || p == null) return;
            PendingSplits.Remove(user);
            if (atMs <= 0) return;
            // ★★ t56 修复（历史回填丢 run 的根因）：
            //   原来的 90s 兜底用「真实墙钟」（NowMs() > p.DeadlineMs）判断「事件是不是来晚了」。
            //   这个判据只对**实时采集**成立 —— 实时时事件确实在几秒内到达。
            //   但**历史回填**是几天的数据在几十秒内跑完：
            //     · p.DeadlineMs 是「切分那一刻的墙钟 + 90s」；
            //     · 回填一份 37MB 日志要 60~90s，处理到下一份时墙钟早过 deadline；
            //     · 于是后续每份文件的 PendingSplit 全被判「来晚了」→ **不建 run**。
            //   实测后果：11-21 及之后的 run 全部缺失，今天的「正在运行」也消失。
            //   修法：回填/重扫路径显式跳过墙钟检查（只看时间戳关系，不看真实时钟）。
            if (!BackfillMode.Value && NowMs() > p.DeadlineMs
                && atMs < p.OldSegEndMs + DaySegmentFirstEventTimeoutMs)
            {
                // 时间戳早于兜底线：可能是一次全量重扫的旧行，不据此建段（重扫幂等由自然键保证）
                return;
            }

            long startMs = atMs;
            string runId = RunRecordStore.MakeRunId(user, p.Group, startMs);
            var run = new OpenRun
            {
                Account = user,
                Group = p.Group,
                StartAtMs = startMs,
                RunId = runId,
                SrcFileStart = p.NewFile.Length > 0 ? p.NewFile : BaseName(srcFile),
                LastEventMs = startMs,
                LazyStart = false,
                DaySplitSeg = true,
            };
            OpenRuns[user] = run;

            RunRecordStore.AddRun(new RunRecord
            {
                RunId = runId,
                Account = user,
                GroupName = p.Group,
                StartAtMs = startMs,
                State = "Running",
                SrcFileStart = run.SrcFileStart,
                StartDay = DayOf(startMs),
                DaySplit = true,             // O5：两条都置 1
                IsCrossDay = true,           // O5：参与跨天分段
            });
        }

        private static void StartGroup(string user, string g, int n, long atMs, string srcFile)
        {
            // ★ t6 惰性开窗落地：换文件后的次日段在这里/首个事件处实体化。
            //   PendingSplit 存在 → 先尝试用「本事件时刻」建次日段（组名继承自 PendingSplit），
            //   再按普通 StartGroup 处理（若同组则并入该段，不重复开窗）。
            MaterializePendingSplit(user, atMs, srcFile);

            if (OpenRuns.TryGetValue(user, out var prev) && prev != null)
            {
                // ★★ t75 修复（实测证据：close-trace.log + PumpBackfill 调用栈）：
                //
                //   回填历史日志时，OpenRuns[user] 里混着**今天**正在跑的窗口，
                //   而 PumpBackfill 喂进来的是 11-23 等历史文件的事件。
                //   下面的逻辑会用历史事件去关闭今天的窗口 ——
                //   实测：「CD采集-全采集」start=18:07:26（11-23 的事）被用来关闭
                //   今天正在跑的那条，detail 写成「下一个配置组已开始」。
                //
                //   t73 我用「atMs < prev.StartAtMs」判断，**没能拦住** ——
                //   因为历史事件的时刻（18:07）比今天的 start（08:32）更大，条件不成立。
                //
                //   正确判据：**看事件所属的「文件日期」与 open run 的「开始日期」是否同一天**。
                //   不同天 = 跨文件串扰，必须忽略。
                //   （日志文件名自带日期，TryFileDay 解析，这是最权威的依据 ——
                //     用户原话「每份日志文件名自带日期」。）
                long eventDayMs = TryFileDay(srcFile, out long fd) ? fd : 0;
                long runDayMs = prev.StartAtMs > 0
                    ? new DateTimeOffset(DateTime.SpecifyKind(
                        DateTimeOffset.FromUnixTimeMilliseconds(prev.StartAtMs).LocalDateTime.Date,
                        DateTimeKind.Local)).ToUnixTimeMilliseconds()
                    : 0;
                if (eventDayMs > 0 && runDayMs > 0 && eventDayMs != runDayMs)
                {
                    // 跨文件串扰：历史文件的事件不得操作今天的窗口（反之亦然）
                    return;
                }
                // 兼容旧判据：同一文件内，事件早于窗口开始也不该关（乱序行）
                if (prev.StartAtMs > 0 && atMs > 0 && atMs < prev.StartAtMs) return;

                if (!string.Equals(prev.Group, g, StringComparison.OrdinalIgnoreCase))
                {
                    // 上一窗口是别的组：Interrupted 收尾（不丢 start 事实）
                    // ★ t56：文案改准确 —— 这不是「用户中断」，而是日志里没有上一个组的结束锚点，
                    //   只能用下一个组的开始来推断边界。BGI 在「程序退出/断电/跨天轮转」时不会
                    //   写「执行结束」，所以这种情况无法避免，但至少要让用户看懂原因。
                    CloseRunLocked(prev, "Interrupted",
                        "日志无结束锚点（推断边界：下一个配置组已开始）",
                        atMs, srcFile, backfilled: false);
                }
                else
                {
                    // ★★ t67 修复「负时长 run」的根因（实测 11-23 的 CD采集-全采集）：
                    //
                    //   原逻辑：同组重跑未收尾时**不做任何事**，直接新建 run。
                    //   后果：旧的 open 行永久停在 Running，下次别的组的结束锚点到来时，
                    //   `FindOpenRunAsync(user, g)` 按「用户名+组名」查到它，
                    //   却用**当前事件的时间**当结束时刻 —— 如果当前时间早于该行的 start，
                    //   就得到负时长（实测：00:47:01 开始的 run 被 00:35:51 的结束锚点收尾）。
                    //
                    //   而且 BGI 日志里「未成功完成」不一定有对应的「加载完成」
                    //   （跨天分段时开始锚点在上一个文件里），所以这种配对错位很常见。
                    //
                    //   修法：同组重跑时**显式收尾旧窗口**，用 prev.LastEventMs 作结束时刻
                    //   （它一定是旧窗口内部的时间，绝不可能晚于新 start，从根上杜绝负时长）。
                    //   状态记 Interrupted 而非 Finished —— 因为我们确实没看到它的结束锚点。
                    long prevEnd = prev.LastEventMs > 0 ? prev.LastEventMs : prev.StartAtMs;
                    CloseRunLocked(prev, "Interrupted",
                        "同组重跑未收尾（日志无该次结束锚点，按最后一次事件时刻收尾）",
                        prevEnd, srcFile, backfilled: false);
                }
            }
            long startMs = atMs > 0 ? atMs : NowMs();
            string runId = RunRecordStore.MakeRunId(user, g, startMs);
            var run = new OpenRun
            {
                Account = user,
                Group = g,
                StartAtMs = startMs,
                RunId = runId,
                ScriptCount = n,
                SrcFileStart = BaseName(srcFile),
                LastEventMs = startMs,
            };
            OpenRuns[user] = run;

            RunRecordStore.AddRun(new RunRecord
            {
                RunId = runId,
                Account = user,
                GroupName = g,
                StartAtMs = startMs,
                State = "Running",
                ScriptCount = n,
                SrcFileStart = BaseName(srcFile),
                StartDay = DayOf(startMs),
            });
        }

        private static void EndGroup(string user, string g, string state, string detail, long atMs, string srcFile)
        {
            if (OpenRuns.TryGetValue(user, out var run) && run != null
                && string.Equals(run.Group, g, StringComparison.OrdinalIgnoreCase))
            {
                // ★★ t75：与 StartGroup 同理 —— 跨文件串扰必须拦住。
                //   回填历史文件（如 11-23）时扫到的结束锚点，绝不能关闭今天的窗口。
                //   判据：事件所属文件日期 vs open run 的开始日期，不同天就忽略。
                long evDay = TryFileDay(srcFile, out long efd) ? efd : 0;
                long rnDay = run.StartAtMs > 0
                    ? new DateTimeOffset(DateTime.SpecifyKind(
                        DateTimeOffset.FromUnixTimeMilliseconds(run.StartAtMs).LocalDateTime.Date,
                        DateTimeKind.Local)).ToUnixTimeMilliseconds()
                    : 0;
                if (evDay > 0 && rnDay > 0 && evDay != rnDay) return;
                if (run.StartAtMs > 0 && atMs > 0 && atMs < run.StartAtMs) return;

                CloseRunLocked(run, state, detail, atMs, srcFile, run.FromDb, run.DaySplitSeg);
                OpenRuns.Remove(user);
                return;
            }

            // 内存无窗口却有结束事件（重启/首捕）→ 走 DB open 行补全（异步查询，回调收尾）
            BackfillCloseAsync(user, g, state, detail, atMs, srcFile);
        }

        /// <summary>
        /// backfill 场景：查 DB open 行；有 → UPDATE 收尾（继承 start_at_ms，is_backfilled=1）；
        /// 无 → ghost 行（start=end，detail 标注缺开始）。
        /// </summary>
        private static void BackfillCloseAsync(string user, string g, string state, string detail,
            long endMs, string srcFile)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    // ★★ t72：用「源文件当天」作时间窗口，历史 END 只能关历史行。
                    //   在此之前只按名字查，回填 11-20~11-25 的历史锚点会把
                    //   今天正在跑的 open 行关掉（实测误伤「联机锄地-混合精英」）。
                    long winFrom = 0, winTo = 0;
                    if (TryFileDay(srcFile, out long fileDay))
                    {
                        winFrom = fileDay;
                        winTo = fileDay + 24L * 60 * 60 * 1000;   // 次日 0 点
                    }
                    var row = await RunRecordStore.FindOpenRunAsync(user, g, default, winFrom, winTo)
                        .ConfigureAwait(false);
                    if (row != null)
                    {
                        RunRecordStore.CloseRun(new RunClose
                        {
                            RunId = row.RunId,
                            EndAtMs = endMs,
                            State = state,
                            Detail = detail,
                            SrcFileEnd = BaseName(srcFile),
                            EndDay = DayOf(endMs),
                            IsCrossDay = row.StartDay != DayOf(endMs),
                            IsBackfilled = true,
                            // 继承 open 行已累计的收益（写线程 close 是整行覆盖语义）
                            Exp = row.Exp,
                            Mora = row.Mora,
                            Picks = row.Picks,
                        });
                    }
                    else
                    {
                        // ghost：无任何 open 行（DB 被清/历史首捕）
                        long start = endMs;
                        string runId = RunRecordStore.MakeRunId(user, g, start);
                        RunRecordStore.AddRun(new RunRecord
                        {
                            RunId = runId,
                            Account = user,
                            GroupName = g,
                            StartAtMs = start,
                            EndAtMs = endMs,
                            State = state,
                            Detail = detail + "（缺开始事件，ghost）",
                            IsBackfilled = true,
                            StartDay = DayOf(start),
                            EndDay = DayOf(endMs),
                        });
                    }
                }
                catch { /* 补全失败不阻塞主流程 */ }
            });
        }

        /// <summary>关 run：先 Interrupted 关掉仍开着的路线，再投 CloseRun。</summary>
        private static void CloseRunLocked(OpenRun run, string state, string detail, long endMs,
            string srcFile, bool backfilled, bool daySplit = false)
        {
            // ★★ t74：诊断 —— 记录每次关闭的调用来源，直接写文件（LogBus 不落盘）。
            //   用户已多次反馈同一现象，之前靠推断改了 3 次都没改对，这次必须拿到证据。
            try
            {
                var st = new System.Diagnostics.StackTrace(1, false);
                var frames = new List<string>();
                for (int fi = 0; fi < Math.Min(5, st.FrameCount); fi++)
                {
                    var m = st.GetFrame(fi)?.GetMethod();
                    if (m != null) frames.Add(m.DeclaringType?.Name + "." + m.Name);
                }
                string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  CLOSE  " + run.Group
                    + "  state=" + state
                    + "  start=" + DateTimeOffset.FromUnixTimeMilliseconds(run.StartAtMs).ToLocalTime().ToString("HH:mm:ss")
                    + "  end=" + (endMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(endMs).ToLocalTime().ToString("HH:mm:ss") : "0")
                    + "  by=" + string.Join(" <- ", frames)
                    + "  detail=" + detail;
                string p = Path.Combine(AppContext.BaseDirectory ?? ".", "close-trace.log");
                File.AppendAllText(p, line + Environment.NewLine);
            }
            catch { }
            long end = endMs > 0 ? endMs : NowMs();
            // ★ t44 兜底：结束时刻早于开始（乱序行残余）→ 夹到开始，明细注明（不产负时长/假跨日）
            if (end < run.StartAtMs)
            {
                end = run.StartAtMs;
                detail = (detail.Length > 0 ? detail + "；" : "") + "结束时刻早于开始（乱序行），已夹齐";
            }
            if (run.Route != null)
            {
                CloseRouteLocked(run, run.Route, "Interrupted", end, 0);
                run.Route = null;
            }
            RunRecordStore.CloseRun(new RunClose
            {
                RunId = run.RunId,
                EndAtMs = end,
                State = state,
                Detail = detail,
                SrcFileEnd = BaseName(srcFile),
                DaySplit = daySplit,
                PlannedRoutes = run.PlannedRoutes,
                PickUnattributed = run.PickUnattributed,
                EndDay = DayOf(end),
                // O5：跨天分段两段都置 1（「参与跨天分段」语义，非纯日历判断）
                IsCrossDay = daySplit || DayOf(run.StartAtMs) != DayOf(end),
                IsBackfilled = backfilled,
                Exp = run.Exp,
                Mora = run.Mora,
                Picks = run.Picks,
            });
        }

        /// <summary>
        /// 锄地「开始第 x/y 条线路」入口：带序号。若上一条 idx < 本条 idx-1，中间序号被跳过 →
        /// B) 未被观察到的路线补 Skipped 行（有 HasError 的窗口保持 Failed 优先）。
        /// </summary>
        private static void StartRouteIdx(string user, string name, string? parentRouteId,
            long atMs, string srcFile, int idx, int total)
        {
            if (!OpenRuns.TryGetValue(user, out var run) || run == null) return;

            // ★★ t76 补漏：StartRouteIdx 的「同名去重 / 跳过推断」两段都在调用
            //   StartRoute（其内部有 IsSameDayAsOpenRun 防护）**之前**执行，且跳过推断
            //   会直接 AddRoute 写库。回填历史文件（如 11-23）时，OpenRuns[user] 里是
            //   今天正在跑的窗口，扫到历史的「开始第 x/y 条线路」锚点会在这里用今天的 run
            //   + 历史 srcFile 生成 #skipped-N 坏行（实测 30 条：ts=今天、src_file=11-23）。
            //   与 AccumPick / StartRoute / AccumReward 同一根因：文件日期是唯一权威。
            if (!IsSameDayAsOpenRun(run, srcFile)) return;

            // ★ 同名去重：「进入路线循环」与「开始第 x/y 条线路」是同一事实的两个锚点（实测成对出现，
            //   相隔约 3 行）。若当前窗口已是同名路线 → 只补 idx，绝不开新窗（否则会自我挤掉、dur=0）。
            if (run.Route != null && string.Equals(run.Route.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                run.Route.Idx = idx;
                run.Route.Total = total;
                run.LastEventMs = Math.Max(run.LastEventMs, atMs);
                return;
            }

            // 跳过推断：上一条有序号且本条 idx 跳过了中间序号 → 中间路线整段无锚点 → Skipped
            if (run.Route != null && run.Route.Idx > 0 && idx > run.Route.Idx + 1
                && run.Route.Total == total)
            {
                long skipStart = run.Route.StartAtMs;
                for (int skipped = run.Route.Idx + 1; skipped < idx; skipped++)
                {
                    string skipId = RunRecordStore.MakeRouteId(run.RunId, "route",
                        "#skipped-" + skipped, skipStart);
                    RunRecordStore.AddRoute(new RouteRecord
                    {
                        RouteId = skipId, RunId = run.RunId, Kind = "route",
                        Name = "(跳过 第" + skipped + "/" + total + "条)",
                        ParentRouteId = parentRouteId,
                        StartAtMs = skipStart, EndAtMs = skipStart, DurMs = 0,
                        State = "Skipped", SrcFile = BaseName(srcFile),
                    });
                }
            }
            StartRoute(user, "route", name, parentRouteId, atMs, srcFile);
            if (OpenRuns.TryGetValue(user, out var run2) && run2?.Route != null)
            {
                run2.Route.Idx = idx;
                run2.Route.Total = total;
            }
        }

        private static void StartRoute(string user, string kind, string name, string? parentRouteId,
            long atMs, string srcFile)
        {
            if (!OpenRuns.TryGetValue(user, out var run) || run == null) return;

            // ★★ t76：跨文件串扰保护 —— 历史文件的路线不能挂到今天的 run 上。
            if (!IsSameDayAsOpenRun(run, srcFile)) return;

            // ★★ t51 问题2 修复：同一次执行的「两种锚点」合并 ★★
            //
            //   实测 A4 日志 10/10 配对（见 docs 报告）：
            //     L2437  [DEBUG] 进入路线循环 count=1 route="07【额外】….json"    ← route 锚点
            //     L2449  → 开始执行地图追踪任务: "07【额外】….json"              ← map 锚点（12 行后）
            //   两者指向**同一个文件、同一次执行**，但 kind 不同 → 原来落库两条，
            //   界面上同一个 .json 出现两行（一行"路线"一行"脚本"）。
            //
            //   为什么原来没合并：ScanEvents 里那段 sameAsOpenRoute 判断是在**扫描期**做的，
            //   而那时 OpenRuns 还停在这批事件之前的状态（route 事件尚未 Apply）→ 永远 false。
            //
            //   现在改在 Apply 期判断：**当前打开的 route 同名且不是纯脚本**
            //   → 视为同一次执行，只做 Touch（刷新 LastEventMs），不新建 record。
            if (run.Route != null
                && string.Equals(run.Route.Name, name, StringComparison.OrdinalIgnoreCase)
                && run.Route.Kind != "script"
                && kind != "route")          // route 锚点自身仍然开新窗（它是真正的边界）
            {
                run.LastEventMs = Math.Max(run.LastEventMs, atMs);
                return;
            }

            // ★ t6 B) 结构性边界挤掉：下一路线/脚本/线路开始而本条未完成 →
            //   紧邻错误（HasError）→ Failed；否则 Interrupted。end=本条开始后的最近事件时刻
            //   （atMs 之前的 run.LastEventMs，即「上一事件」——推断边界）。
            //   G) 永不因 ERR/拾取/杂项关窗：只有结构性开始锚点走到这里。
            if (run.Route != null)
            {
                string evictState = run.Route.HasError ? "Failed" : "Interrupted";

                // ★★ t69 修复（route 层负时长，实测 -1651s / -28047s）：
                //
                //   原算法：evictEnd = min(run.LastEventMs, atMs)
                //   意图是对的（不能晚于新路线的开始），但**漏了下界**。
                //   回填历史日志时 run.LastEventMs 可能远早于 run.Route.StartAtMs
                //   （跨天分段、日志乱序），于是 evictEnd < StartAtMs → 负时长。
                //   实测：start=09:11:06 / end=08:43:35（diff -1651s）；
                //        最严重一条 diff -28047s（7.8 小时）。
                //   虽然有「夹到 0」的防线不至于脏库，但每秒一条 ERROR 日志刷屏，
                //   而且真实时长信息本来就丢了（本该 >0 的被记成 0）。
                //
                //   修法：**下界钳到 StartAtMs**。语义上「这条路线至少持续了 0 秒」，
                //   永远不会算出负数；同时保留 min(LastEventMs, atMs) 的上界语义。
                long evictEnd = atMs;
                if (run.Route.StartAtMs > 0 && run.LastEventMs > 0)
                    evictEnd = Math.Min(run.LastEventMs, atMs);
                if (run.Route.StartAtMs > 0 && evictEnd < run.Route.StartAtMs)
                    evictEnd = run.Route.StartAtMs;   // ★ 下界：绝不早于开始

                CloseRouteLocked(run, run.Route, evictState, evictEnd, 0);
                run.Route = null;
            }

            long startMs = atMs > 0 ? atMs : NowMs();
            string routeId = RunRecordStore.MakeRouteId(run.RunId, kind, name, startMs);
            var route = new OpenRoute
            {
                RouteId = routeId,
                RunId = run.RunId,
                Kind = kind,
                Name = name,
                ParentRouteId = parentRouteId,
                StartAtMs = startMs,
                SrcFile = BaseName(srcFile),
            };
            run.Route = route;
            run.LastEventMs = Math.Max(run.LastEventMs, startMs);

            RunRecordStore.AddRoute(new RouteRecord
            {
                RouteId = routeId,
                RunId = run.RunId,
                Kind = kind,
                Name = name,
                ParentRouteId = parentRouteId,
                StartAtMs = startMs,
                State = "Running",
                PickCount = 0,
                PickDetail = "{}",
                SrcFile = BaseName(srcFile),
            });
        }

        /// <summary>
        /// 路线/脚本结束。kindHint="script"（带耗时）只关 script 窗口；
        /// kindHint=null 时：name 匹配当前 route 窗口 → 关 route；匹配 script → 关 script。
        /// </summary>
        private static void EndRoute(string user, string name, string? kindHint, long atMs, string durText)
        {
            if (!OpenRuns.TryGetValue(user, out var run) || run?.Route == null) return;
            var route = run.Route;
            if (!string.Equals(route.Name, name, StringComparison.OrdinalIgnoreCase)) return;

            long end = atMs > 0 ? atMs : NowMs();
            // 窗口内出现过 [ERR] → Failed（契约 §3.3）
            string state = route.HasError ? "Failed" : "Finished";
            long dur = ParseDur(durText, route.StartAtMs, end);

            CloseRouteLocked(run, route, state, end, dur);
            run.Route = null;
        }


        /// <summary>
        /// ★ t56：历史回填专用 —— 一份历史日志处理完时，强制收尾它的 open run。
        ///
        /// 为什么需要：回填是「按文件推进」的，跨文件时上一个文件里最后一个没有「执行结束」锚点的
        /// 组会永远停在 Running（实测残留 4 条 end=NULL）。实时采集不会遇到这个问题，
        /// 因为 BGI 的下一个锚点会自然关闭它。
        ///
        /// 收尾语义：
        ///   · state = Finished（该文件已经结束，组内没报错就是正常跑完）；
        ///   · end = 该 run 的 LastEventMs（最后一个事件时刻），不伪造更晚的时间；
        ///   · detail 标注「历史回填收尾（日志文件结束，未见结束锚点）」—— 让用户知道这不是真的中断。
        /// </summary>
        public static void CloseOpenForBackfill(string userName, string srcFile, bool isLastFile = false)
        {
            if (string.IsNullOrWhiteSpace(userName)) return;
            lock (StateLock)
            {
                // ★★ t56 修复：**必须同时清掉 PendingSplits**。
                //   跨天拆分会把 run 拆成「前一段(已 close) + 后一段(PendingSplit 待建)」。
                //   回填处理到下一个文件时，后一段的 PendingSplit 如果没被消费，
                //   就会永远挂在那里 —— 表现为「今天的组正在跑，但库里没有 Running 记录」
                //   （实测 11-26 CD采集-全采集 只有 Interrupted 的前半段，后半段凭空消失）。
                //
                //   回填语义：历史文件处理完，如果**后面还有文件**，它的 PendingSplit 不该延到未来
                //   （下一个文件会有自己的 OnFileChanged）。清掉即可（不丢已 close 的前段）。
                //   ★ 但**最后一份**（今天那份）不能清 —— 那正是「正在运行」的组，要留给实时 Pump 消费。
                if (!isLastFile) PendingSplits.Remove(userName);

                if (!OpenRuns.TryGetValue(userName, out var run) || run == null) return;

                // ★★ t75：回填收尾同样要跨文件判定 ——
                //   处理 11-23 的文件时绝不能把今天正在跑的窗口收尾。
                long bfDay = TryFileDay(srcFile, out long bfd) ? bfd : 0;
                long runDay = run.StartAtMs > 0
                    ? new DateTimeOffset(DateTime.SpecifyKind(
                        DateTimeOffset.FromUnixTimeMilliseconds(run.StartAtMs).LocalDateTime.Date,
                        DateTimeKind.Local)).ToUnixTimeMilliseconds()
                    : 0;
                if (bfDay > 0 && runDay > 0 && bfDay != runDay) return;

                long end = run.LastEventMs > 0 ? run.LastEventMs : NowMs();
                // 先关 route（如有），再关 run
                if (run.Route != null)
                {
                    long rEnd = Math.Max(end, run.Route.StartAtMs);
                    CloseRouteLocked(run, run.Route, "Finished", rEnd, 0);
                    run.Route = null;
                }
                CloseRunLocked(run, "Finished", "历史回填收尾（日志文件结束，未见结束锚点）",
                    end, srcFile, backfilled: false);
                OpenRuns.Remove(userName);
            }
        }

        private static void CloseRouteLocked(OpenRun run, OpenRoute route, string state, long endMs, long durMs)
        {
            // ★★ t69 兜底：**所有**关路线的路径都在这里收口，
            //   所以在这里做最后一道下界钳制 —— 哪怕上游算错了，也绝不写负时长。
            //   （上游 StartRoute 的挤掉逻辑已单独修，这里是防御性的第二层。）
            if (route.StartAtMs > 0 && endMs < route.StartAtMs) endMs = route.StartAtMs;
            RunRecordStore.CloseRoute(new RouteClose
            {
                RouteId = route.RouteId,
                EndAtMs = endMs,
                DurMs = durMs > 0 ? durMs : Math.Max(0, endMs - route.StartAtMs),
                State = state,
                PickCount = route.PickCount,
                PickDetail = SerializePicks(route.PickDetail),
                Picked = route.Picked,
                Skipped = route.Skipped,
                NoTarget = route.NoTarget,
            });
        }

        /// <summary>「18分20.948秒」/「0分1.431秒」→ 毫秒；解析失败用 end-start。</summary>
        private static long ParseDur(string durText, long startMs, long endMs)
        {
            if (!string.IsNullOrEmpty(durText))
            {
                var m = Regex.Match(durText, @"(?:(?<h>\d+)时)?(?:(?<m>\d+)分)?(?<s>[\d.]+)秒");
                if (m.Success)
                {
                    int h = ParseGroup(m, "h"), mi = ParseGroup(m, "m");
                    if (double.TryParse(m.Groups["s"].Value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double s))
                    {
                        return (long)((h * 3600 + mi * 60 + s) * 1000);
                    }
                }
            }
            return Math.Max(0, endMs - startMs);
        }

        private static int ParseGroup(Match m, string name)
            => m.Groups[name].Success && int.TryParse(m.Groups[name].Value, out int v) ? v : 0;


        /// <summary>
        /// ★★ t76：判断「事件所属文件」与「当前 open run 的开始日期」是否同一天。
        ///
        /// 为什么需要：回填历史日志时，OpenRuns[user] 里是**今天**正在跑的窗口，
        /// 而 PumpBackfill 喂进来的是 11-22/11-23/11-24 等历史文件的事件。
        /// 如果不加隔离，历史文件的 route / pick / 收益会全部记到今天的 run 上 ——
        /// 实测「联机锄地-混合精英」一条 run 被塞进 4 天的数据：
        ///   route 3421 条（11-22:1004 + 11-23:1140 + 11-24:1213 + 11-26:64）
        ///   pick_event 42648 条（11-22:24906 + 11-23:8251 + 11-24:9206 + 11-26:287）
        ///   picks 12008（实际今天只有 1）
        ///
        /// 用户原则：「数据中心应该记录最真实最可靠的结果」。
        /// 串台比缺失更糟 —— 缺失能看出来，串台看不出来。
        ///
        /// ★ 判据用**日志文件名自带的日期**（TryFileDay），这是最权威的依据。
        /// </summary>
        private static bool IsSameDayAsOpenRun(OpenRun run, string srcFile)
        {
            try
            {
                if (run == null || run.StartAtMs <= 0) return true;   // 无基线时不拦（保持旧行为）
                if (!TryFileDay(srcFile, out long fileDay)) return true;  // 解析不出日期时不拦
                long runDay = new DateTimeOffset(DateTime.SpecifyKind(
                    DateTimeOffset.FromUnixTimeMilliseconds(run.StartAtMs).LocalDateTime.Date,
                    DateTimeKind.Local)).ToUnixTimeMilliseconds();
                return fileDay == runDay;
            }
            catch { return true; }
        }

        /// <summary>
        /// t6 三类拾取：kind=picked/no_target/skipped。
        /// picked 计 run.Picks 与 route.PickCount（语义不变）；no_target/skipped 只进三分类计数与
        /// pick_event 投影（不进 Picks——「拾取次数」语义保持命中口径）。F) 不按路线类型筛选，
        /// 有 route 打开就计入（含激活CD 线路），不论 kind。M) 行序号+偏移入自然键。
        /// </summary>
        private static void AccumPick(string user, string item, long atMs, string kind,
            int lineStart, int matchOffset, string srcFile, long chunkOffset)
        {
            if (!OpenRuns.TryGetValue(user, out var run) || run == null)
            {
                // H)：无 run 窗口（组外拾取，如自动拾取循环期间）→ 孤儿计数，不静默吞掉
                lock (StateLock)
                {
                    if (!OrphanPicks.TryGetValue(user, out var c)) { c = new int[3]; OrphanPicks[user] = c; }
                    c[kind == "picked" ? 0 : kind == "skipped" ? 1 : 2]++;
                }
                return;
            }

            // ★★ t76：**跨文件串扰保护** —— 历史文件（11-22 等）的拾取绝不能记到今天的 run 上。
            //   实测：这条保护缺失导致「联机锄地-混合精英」一天就记了 12008 次拾取
            //   （实际只有 1 次），route 3421 条横跨 4 天。
            //   不记入 OrphanPicks 也不算丢弃 —— 历史那天的 run 会在回填自己那天时重建。
            if (!IsSameDayAsOpenRun(run, srcFile)) return;

            run.LastEventMs = Math.Max(run.LastEventMs, atMs);

            string? routeId = null;
            if (run.Route != null)
            {
                routeId = run.Route.RouteId;
                switch (kind)
                {
                    case "picked":
                        run.Route.Picked++;
                        run.Route.PickCount++;                     // K) pick_count=picked 语义不变
                        run.Route.PickDetail.TryGetValue(item, out int c);
                        run.Route.PickDetail[item] = c + 1;
                        break;
                    case "skipped": run.Route.Skipped++; break;
                    case "no_target": run.Route.NoTarget++; break;
                }
                run.DirtyRoute = true;
            }
            switch (kind)
            {
                case "picked":
                    run.Picked++;
                    run.Picks++;                                    // H) run.picks=picked 口径
                    run.DirtyRun = true;
                    break;
                case "skipped": run.Skipped++; break;
                case "no_target": run.NoTarget++; break;
            }

            // N) pick_event 结构化投影（幂等：run+ts+line+off+kind 唯一索引）
            RunRecordStore.AddPickEvent(new PickEvent
            {
                RunId = run.RunId,
                RouteId = routeId,
                Kind = kind,
                Item = item,          // J) 原话照抄（no_target 空串）
                TsMs = atMs,
                LineNo = lineStart,   // M) 行序号（chunk 内偏移）
                LineOffset = matchOffset,
                SrcFile = BaseName(srcFile),
                ChunkOffset = chunkOffset,   // t35：跨 chunk 去重维度（与 raw_event 同语义）
            });
        }

        private static void AccumReward(string user, long exp, long mora, long atMs, string srcFile = "")
        {
            if (!OpenRuns.TryGetValue(user, out var run) || run == null) return;
            // ★★ t76：跨文件串扰保护 —— 历史文件的收益不能记到今天的 run 上。
            if (srcFile.Length > 0 && !IsSameDayAsOpenRun(run, srcFile)) return;
            run.Exp += exp;
            run.Mora += mora;
            run.LastEventMs = Math.Max(run.LastEventMs, atMs);
            run.DirtyRun = true;
        }

        /// <summary>路径组头/开始遍历：刷新续跑时间基线（跨天连续性判定用）。</summary>
        private static void TouchRun(string user, long atMs)
        {
            if (!OpenRuns.TryGetValue(user, out var run) || run == null) return;
            run.LastEventMs = Math.Max(run.LastEventMs, atMs);
        }

        /// <summary>C) 计划对账：数量型锚点（共加载 N 条路线等）落到 run.planned_routes（取最大值）。</summary>
        private static void SetPlannedRoutes(string user, int n, string srcFile = "")
        {
            if (!OpenRuns.TryGetValue(user, out var run) || run == null) return;
            // ★★ t76 补漏：历史文件的「共加载 N 条路线」不能改写今天 run 的计划数。
            if (srcFile.Length > 0 && !IsSameDayAsOpenRun(run, srcFile)) return;
            if (n > run.PlannedRoutes) run.PlannedRoutes = n;
        }

        /// <summary>[ERR] 块结算 → ErrorRecord（归到当前路线/脚本，无则 route_id=NULL）。</summary>
        private static void EmitError(string user, long ts, string exceptionType, string message,
            string detail, string source, string srcFile)
        {
            string runId = "";
            string? routeId = null;
            lock (StateLock)
            {
                if (OpenRuns.TryGetValue(user, out var run) && run != null)
                {
                    // ★★ t76 补漏：ERR 块结算同样要跨文件隔离 —— 历史文件的错误不能挂到
                    //   今天的 run 上（反之亦然）。实测 err 表 18 条坏行：回填历史文件时
                    //   用今天的 run 记了历史的错误。与 AccumPick / StartRoute / AccumReward
                    //   同一根因：文件日期是唯一权威。
                    if (!IsSameDayAsOpenRun(run, srcFile)) return;
                    runId = run.RunId;
                    run.LastEventMs = Math.Max(run.LastEventMs, ts);
                    if (run.Route != null)
                    {
                        run.Route.HasError = true;   // 路线收尾时 → Failed
                        routeId = run.Route.RouteId;
                    }
                }
            }
            if (runId.Length == 0) return;   // 无打开运行：错误不落（契约 err.run_id NOT NULL）

            // 异常类型在 Apply 时从完整 detail（含合并栈）提取，闭包参数只是初值
            string finalType = ExtractExceptionType(detail);
            if (finalType.Length == 0) finalType = exceptionType;

            RunRecordStore.AddError(new ErrorRecord
            {
                RunId = runId,
                RouteId = routeId,
                TsMs = ts,
                Level = "ERR",
                ExceptionType = finalType,
                Message = message,
                Detail = detail,
                Source = source,
                SrcFile = BaseName(srcFile),
            });
        }

        private static string SerializePicks(Dictionary<string, int> picks)
        {
            if (picks.Count == 0) return "{}";
            var sb = new StringBuilder(64 * picks.Count + 2).Append('{');
            bool first = true;
            foreach (var kv in picks)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(kv.Key.Replace("\\", "\\\\").Replace("\"", "\\\""))
                  .Append("\":").Append(kv.Value.ToString(CultureInfo.InvariantCulture));
            }
            return sb.Append('}').ToString();
        }

        private static string BaseName(string path)
            => string.IsNullOrWhiteSpace(path) ? "" : System.IO.Path.GetFileName(path);

        private static long NowMs() => (long)DateTime.UtcNow.Subtract(UnixEpoch).TotalMilliseconds;

        private static long clockLastMs(string user)
        {
            return Clocks.TryGetValue(user, out var c) ? c.LastMs : 0;
        }

        /// <summary>
        /// H) 拾取守恒：sum(已收路线.picked) + 当前路线.picked + run级(无路线时) == run.Picks。
        /// 由于路线行是流式 upsert（CloseRoute 时带最终计数），这里对**当前 open run** 做逐次校验：
        /// run.Picked（累计含路线期+无路线期）与 run.Picks 应相等（两者都只由 picked 事件驱动）。
        /// 不等 → PickUnattributed 记差异（防未来改动破坏守恒），写入 detail 由 CloseRun 落库。
        /// </summary>
        private static void CheckPickConservation(string user)
        {
            if (!OpenRuns.TryGetValue(user, out var run) || run == null) return;
            int expected = run.Picks;
            int actual = run.Picked;
            if (expected != actual)
            {
                run.PickUnattributed = Math.Abs(expected - actual);
            }
        }

        /// <summary>测试/诊断用：清空内存窗口（不碰 DB）。</summary>
        internal static void ResetForTest()
        {
            lock (StateLock)
            {
                OpenRuns.Clear();
                RawChunkSequences.Clear();
                RawByteOffsets.Clear();
                Clocks.Clear();
                ErrBlocks.Clear();
                PendingSplits.Clear();
                SpanStates.Clear();
                FirstLineTs.Clear();
                OrphanPicks.Clear();
            }
        }

        // ── t6 硬要求四：BGI 状态时段（三态，只在切换时开/闭段）────────────
        //
        //  P) running=有产出（不额外记段）；idle=无产出但进程活；not_running=无产出且进程不在。
        //     静默 ≥60s 判 idle（IdleSilenceMs 常量+注释）。
        //  Q) 判活由调用方注入（复用 BgiLogCenter.Entry.BgiPid + Process.GetProcessById 的既有低成本
        //     判活，同账号 ≥30s 节流在调用方 LastAliveCheck 完成——本类不枚举进程，绝不 Process.GetProcesses）。
        //  R) 只在状态切换时开/闭段（open 行模型）；跨天 23:00→次日01:00 的 idle 只落 1 条。
        //  S) 未绑定不产段（无 Feed 事件 → 本方法不会被调）；程序退出由 ShutdownSpans() 收段。
        //  T) dur 由写线程算（CloseStateSpan MAX(0,end-start)）；进行中段 EndAtMs=NULL 可现算。

        /// <summary>
        /// 状态观测入口（Feed 每次调用，另可由宿主在判活时调用）。
        /// tsMs=本次事件时刻（≤0 表示仅判活不推进时钟）；alive=null 表示本次不判活只推进时钟。
        /// </summary>
        internal static void ObserveBgiState(string user, long tsMs, bool? alive, string srcFile)
        {
            if (string.IsNullOrWhiteSpace(user)) return;
            lock (StateLock)
            {
                var st = GetSpanState(user);
                long nowMs = NowMs();
                if (tsMs > 0)
                {
                    // ★ 只有真实事件才推进「最近 Feed 墙钟」（判静默基准）——
                    //   判活专用调用（tsMs=0）不刷新它，否则永不静默。
                    st.LastFeedWallMs = Math.Max(st.LastFeedWallMs, nowMs);
                    st.LastEventMs = Math.Max(st.LastEventMs, tsMs);
                    // 有产出 = running：若当前不是 running → 切换（先闭旧段再开新段）
                    if (st.Current != "running")
                    {
                        CloseSpanOpen(user, tsMs);
                        st.Current = "running";
                        RunRecordStore.AddStateSpan(new StateSpan
                        {
                            Account = user, State = "running", StartAtMs = tsMs, SrcFile = BaseName(srcFile),
                        });
                    }
                }
                if (alive.HasValue) st.Alive = alive.Value;

                // 静默 ≥60s → idle/not_running。★ 用墙钟（LastFeedWallMs）而非日志时钟：
                //   回放/重扫时日志时钟可能是昨天，直接比会把刚 running 的段立刻判静默。
                bool silent = st.LastFeedWallMs > 0 && nowMs - st.LastFeedWallMs >= IdleSilenceMs;
                if (silent && st.Current == "running")
                {
                    string next = st.Alive ? "idle" : "not_running";
                    CloseSpanOpen(user, nowMs);
                    st.Current = next;
                    RunRecordStore.AddStateSpan(new StateSpan
                    {
                        Account = user, State = next, StartAtMs = nowMs, SrcFile = BaseName(srcFile),
                    });
                }
                else if (!st.Alive && st.Current == "idle")
                {
                    // idle 期间进程消失 → 切 not_running
                    CloseSpanOpen(user, nowMs);
                    st.Current = "not_running";
                    RunRecordStore.AddStateSpan(new StateSpan
                    {
                        Account = user, State = "not_running", StartAtMs = nowMs, SrcFile = BaseName(srcFile),
                    });
                }
            }
        }

        /// <summary>
        /// t10 重启语义：从 DB 读回该账号最后一条 end 为 NULL 的段继续沿用
        /// （不凭空开新段；若无则按「无进行中段」初始化——首次事件/判活会开新段）。
        /// </summary>
        internal static async Task EnsureSpanBootAsync(CancellationToken ct = default)
        {
            try
            {
                var accounts = await RunRecordStore.LoadOpenSpanAccountsAsync(ct).ConfigureAwait(false);
                lock (StateLock)
                {
                    foreach (var (account, state) in accounts)
                    {
                        var st = GetSpanState(account);
                        // 沿用：Current=旧段状态，墙钟基线=0（首个事件重建）
                        st.Current = state;
                        st.LastFeedWallMs = 0;
                    }
                }
            }
            catch { /* 启动补全失败不阻塞；首次事件会重开段 */ }
        }

        /// <summary>程序退出：关闭所有进行中的段（S）。</summary>
        internal static void ShutdownSpans()
        {
            lock (StateLock)
            {
                long now = NowMs();
                foreach (var user in SpanStates.Keys) CloseSpanOpen(user, now);
            }
        }

        private static SpanState GetSpanState(string user)
        {
            if (!SpanStates.TryGetValue(user, out var st))
            {
                st = new SpanState();
                SpanStates[user] = st;
            }
            return st;
        }

        private static void CloseSpanOpen(string user, long endMs)
            => RunRecordStore.CloseStateSpan(user, endMs);
    }
}
