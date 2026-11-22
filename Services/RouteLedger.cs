using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace SoulLink.Services
{
    /// <summary>一条配置组运行台账记录（JSONL 文件中的一行）。</summary>
    public sealed class RouteLedgerRecord
    {
        /// <summary>Windows 用户名（账号）。</summary>
        public string Account { get; set; } = "";
        /// <summary>归属日 yyyy-MM-dd（= 结束事件的日历日）。</summary>
        public string Day { get; set; } = "";
        /// <summary>配置组名。</summary>
        public string Group { get; set; } = "";
        /// <summary>Finished / Failed / Cancelled / Interrupted。</summary>
        public string State { get; set; } = "";
        /// <summary>开始时刻 HH:mm:ss.fff（跨午夜时指前一日的时刻，配合 Day 理解）。</summary>
        public string Start { get; set; } = "";
        /// <summary>结束时刻 HH:mm:ss.fff。</summary>
        public string End { get; set; } = "";
        /// <summary>耗时（秒）。</summary>
        public int DurSec { get; set; }
        /// <summary>脚本数量（开始执行时日志给出）。</summary>
        public int ScriptCount { get; set; }
        /// <summary>本轮收益经验。</summary>
        public long Exp { get; set; }
        /// <summary>本轮收益摩拉。</summary>
        public long Mora { get; set; }
        /// <summary>本轮拾取次数。</summary>
        public int Picks { get; set; }
        /// <summary>失败原因原文（结果 token，如 Failed / Cancelled）。</summary>
        public string Detail { get; set; } = "";

        /// <summary>是否 0 收益（既没经验也没摩拉）。</summary>
        public bool ZeroReward => Exp == 0 && Mora == 0;
        /// <summary>是否非正常结束（失败/取消/中断）。</summary>
        public bool IsFailure => State == "Failed" || State == "Cancelled" || State == "Interrupted";

        /// <summary>去重用的自然键（同一自然键 = 同一条记录）。</summary>
        internal string NaturalKey => Group + "\u0001" + State + "\u0001" + Day + "\u0001" + Start + "\u0001" + End;
    }

    /// <summary>按组名的聚合结果（近 N 天）。</summary>
    public sealed class RouteLedgerGroupSummary
    {
        public string Group { get; set; } = "";
        public int Runs { get; set; }
        public int Failures { get; set; }
        public long AvgExp { get; set; }
        public long AvgMora { get; set; }
        public long TotalExp { get; set; }
        public long TotalMora { get; set; }
    }

    /// <summary>
    /// 配置组 / 路线级的收益与报错台账。
    ///
    /// ── 为什么要有它 ──────────────────────────────────────────────────
    ///   DailyStatsCache 只有"账号级今日累计"，ScriptGroupTracker 只有"配置组状态机"，
    ///   两者都在内存里、跨天清空、互不关联。用户要的是：
    ///   「0 收益的组、失败的组、某组最近 7 天平均收益」能**一次查出来**，
    ///   而不是去一个个配置组里翻。于是把「配置组运行事件」和「收益事件」
    ///   按时间窗关联成一条条可落盘、可跨天查询的记录。
    ///
    /// ── 归因规则（已在 21 天真实日志样本核实，见 ROUTE-LEDGER-DESIGN）──
    ///   收益行总是出现在「配置组 X …开始执行」之后、「配置组 X 执行结束/未成功完成」之前，
    ///   样本中 0 条收益落在组窗口之外。故用「最近一个 Started 且未 Ended 的组」作窗口：
    ///     · 开始执行 → 若上一窗口仍开着且是别的组，先记为 Interrupted 落盘，再开新窗口；
    ///     · 收益行    → 累加到当前打开的窗口；
    ///     · 执行结束/未成功完成/取消 → 关窗口，落盘 Finished/Failed/Cancelled；
    ///     · 无开始却有结束（重启后半途接手）→ 记 0 收益的 ghost 记录，不丢。
    ///
    /// ── 落盘 ──────────────────────────────────────────────────────────
    ///   JSONL 追加写，一行一条，写完即 Flush（崩溃最多丢最后一行）。
    ///   路径：<AppIdentity.DataDir>\ledger\<账号>\<yyyy-MM-dd>.jsonl
    ///   ★ 跟随运行时数据目录（便携 data\ 或 %LOCALAPPDATA% 回退），绝不在源码 dist\。
    ///   坏行容错：查询时逐行反序列化，失败跳过该行，不崩。
    ///
    /// ── 读写契约 ──────────────────────────────────────────────────────
    ///   唯一写入者 = 本类（由 BgiLogCenter.ProcessChunk 的增量路径喂入，不新增定时器）。
    ///   只读者     = 网页 / APP / 统计页，通过 Query* 读取。
    ///   ★ 写 IO 绝不在 UI 线程：Feed 只做内存归因，把成品记录丢进队列，
    ///     由专用后台线程 RouteLedger.Writer 落盘（ProcessChunk 会在 UI 线程跑，
    ///     见 TickHub 的 ServiceTick）。
    ///
    /// ── 重启/重扫去重 ─────────────────────────────────────────────────
    ///   BgiLogCenter 重启后会把当天日志从头重扫一遍（cursor=0），会再次产生
    ///   同样的记录。写线程在首次写入某 (账号,日) 文件前，先读回该文件里已有的
    ///   自然键集合，之后只追加"没见过"的记录 —— 跨重启也不重复。
    /// </summary>
    public static class RouteLedger
    {
        // ── 配置组事件锚点（与 ScriptGroupTracker 同一套，实测 20261117）──
        private static readonly Regex RxStart = new(
            @"配置组\s*""(?<g>[^""]+)""\s*加载完成[，,]\s*共\s*(?<n>\d+)\s*个脚本[，,]\s*开始执行",
            RegexOptions.Compiled);
        private static readonly Regex RxFinish = new(
            @"配置组\s*""(?<g>[^""]+)""\s*执行结束", RegexOptions.Compiled);
        private static readonly Regex RxFail = new(
            @"配置组\s*""(?<g>[^""]+)""\s*未成功完成[，,]?\s*结果\s*[:：]\s*(?<r>\S*)",
            RegexOptions.Compiled);
        private static readonly Regex RxCancel = new(
            @"取消执行配置组\s*[:：]\s*""(?<g>[^""]+)""", RegexOptions.Compiled);

        // ── 收益锚点（与 DailyStatsCache 同一套，20261114 实测）────────
        private static readonly Regex RxExp = new(
            @"(?<!JS 通知关闭，消息被拦截:\s*)(?:狗粮路线获取狗粮经验\s*[:：]\s*(?<v1>\d+)|[\u4e00-\u9fa5]{1,6}可获得(?<v2>\d+)经验)",
            RegexOptions.Compiled);
        private static readonly Regex RxMora = new(
            @"(?<!JS 通知关闭，消息被拦截:\s*)(?:狗粮路线获取摩拉\s*[:：]\s*(?<v1>\d+)|[\u4e00-\u9fa5]{1,6}获得(?<v2>\d+)摩拉(?!鼬))",
            RegexOptions.Compiled);
        private static readonly Regex RxPick = new(
            @"交互或拾取\s*[:：]\s*""(?<item>[^""]+)""", RegexOptions.Compiled);

        private static readonly Regex RxTs = new(
            @"^\[(?<date>\d{4}-\d{2}-\d{2}[ T])?(?<time>\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?)\]",
            RegexOptions.Compiled);

        // ── 状态机：账号 → 当前打开的运行窗口（每组串行，同一账号同时只跑一组）──
        private sealed class OpenRun
        {
            public string Group = "";
            public DateTime StartedAt = DateTime.MinValue;
            public int ScriptCount;
            public long Exp, Mora;
            public int Picks;
        }

        private static readonly Lock StateLock = new();
        private static readonly Dictionary<string, OpenRun> OpenRuns
            = new(StringComparer.OrdinalIgnoreCase);

        // ── 落盘队列 + 专用后台写线程（唯一写文件的地方）────────────────
        private static readonly BlockingCollection<RouteLedgerRecord> Outbox
            = new(new ConcurrentQueue<RouteLedgerRecord>());
        private static readonly Thread Writer = new(WriteLoop)
        { IsBackground = true, Name = "RouteLedger.Writer" };
        static RouteLedger() { Writer.Start(); }

        private static readonly JsonSerializerOptions JsonOpt = new()
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>台账根目录（运行时数据目录下，非源码 dist/）。</summary>
        public static string RootDir => Path.Combine(AppIdentity.DataDir, "ledger");

        private static string SafeAccount(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName)) return "_unknown";
            return string.Join("_", userName.Split(Path.GetInvalidFileNameChars()));
        }

        private static string DayKey(DateTime d)
            => (d == DateTime.MinValue ? DateTime.Now : d).ToString("yyyy-MM-dd");

        // ═══════════════════════════════════════════════════════════════
        //  写入侧：由 BgiLogCenter.ProcessChunk 增量喂入（不做文件 IO）
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 用一段**新增**日志文本更新台账。只做内存归因，绝不碰文件系统。
        /// ★ 调用方只喂新内容（BgiLogCenter 的字节游标保证）；重复喂也安全
        ///   （写线程按自然键去重）。
        /// </summary>
        public static void Feed(string userName, string logText)
        {
            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(logText)) return;

            var events = new List<(int pos, Action apply)>();
            var now = DateTime.Now;

            foreach (Match m in RxStart.Matches(logText))
            {
                string g = m.Groups["g"].Value.Trim();
                int n = int.TryParse(m.Groups["n"].Value, out int c) ? c : 0;
                if (!ValidGroup(g)) continue;
                DateTime at = StampOf(logText, m.Index, now);
                events.Add((m.Index, () => StartGroup(userName, g, n, at)));
            }
            foreach (Match m in RxFinish.Matches(logText))
            {
                string g = m.Groups["g"].Value.Trim();
                if (!ValidGroup(g)) continue;
                DateTime at = StampOf(logText, m.Index, now);
                events.Add((m.Index, () => EndGroup(userName, g, "Finished", "", at)));
            }
            foreach (Match m in RxFail.Matches(logText))
            {
                string g = m.Groups["g"].Value.Trim();
                string r = m.Groups["r"].Value.Trim();
                if (!ValidGroup(g)) continue;
                DateTime at = StampOf(logText, m.Index, now);
                string state = string.Equals(r, "Cancelled", StringComparison.OrdinalIgnoreCase)
                    ? "Cancelled" : "Failed";
                events.Add((m.Index, () => EndGroup(userName, g, state, r, at)));
            }
            foreach (Match m in RxCancel.Matches(logText))
            {
                string g = m.Groups["g"].Value.Trim();
                if (!ValidGroup(g)) continue;
                DateTime at = StampOf(logText, m.Index, now);
                events.Add((m.Index, () => EndGroup(userName, g, "Cancelled", "Cancelled", at)));
            }
            foreach (Match m in RxExp.Matches(logText))
            {
                long v = ParseV(m);
                if (v <= 0) continue;
                events.Add((m.Index, () => Accumulate(userName, addExp: v)));
            }
            foreach (Match m in RxMora.Matches(logText))
            {
                long v = ParseV(m);
                if (v <= 0) continue;
                events.Add((m.Index, () => Accumulate(userName, addMora: v)));
            }
            foreach (Match m in RxPick.Matches(logText))
            {
                string item = m.Groups["item"].Value.Trim();
                if (item.Length == 0 || item == "无有效结果") continue;
                events.Add((m.Index, () => Accumulate(userName, addPicks: 1)));
            }

            if (events.Count == 0) return;

            events.Sort((a, b) => a.pos.CompareTo(b.pos));

            lock (StateLock)
            {
                foreach (var (_, apply) in events) apply();
            }
        }

        private static void Accumulate(string user, long addExp = 0, long addMora = 0, int addPicks = 0)
        {
            if (!OpenRuns.TryGetValue(user, out var run) || run == null) return;
            run.Exp += addExp;
            run.Mora += addMora;
            run.Picks += addPicks;
        }

        private static long ParseV(Match m)
        {
            if (long.TryParse(m.Groups["v1"].Value, out var a) && a > 0) return a;
            if (long.TryParse(m.Groups["v2"].Value, out var b) && b > 0) return b;
            return 0;
        }

        // 注意：Start/End 这些闭包在 StateLock 内执行，且每次直接读写 OpenRuns，
        // 所以上面的 `run` 局部变量只在收益/拾取累加里用到；组边界事件自己重取。
        private static void StartGroup(string user, string g, int n, DateTime at)
        {
            DateTime ts = at == DateTime.MinValue ? DateTime.Now : at;
            if (OpenRuns.TryGetValue(user, out var prev) && prev != null)
            {
                if (!string.Equals(prev.Group, g, StringComparison.OrdinalIgnoreCase))
                {
                    OpenRuns.Remove(user);
                    Emit(user, prev, "Interrupted", "后续配置组已开始，未记录正常结束", ts);
                }
                else
                {
                    // 同组重跑（未正常收尾）：旧窗口作废，不额外落一条中断（避免噪音）。
                    OpenRuns.Remove(user);
                }
            }
            OpenRuns[user] = new OpenRun { Group = g, StartedAt = ts, ScriptCount = n };
        }

        private static void EndGroup(string user, string g, string state, string detail, DateTime at)
        {
            DateTime ts = at == DateTime.MinValue ? DateTime.Now : at;
            if (OpenRuns.TryGetValue(user, out var run) && run != null
                && string.Equals(run.Group, g, StringComparison.OrdinalIgnoreCase))
            {
                OpenRuns.Remove(user);
                Emit(user, run, state, detail, ts);
            }
            else
            {
                // 没有打开窗口却有结束事件：重启后半途接手，照记 0 收益 ghost 记录。
                Emit(user, new OpenRun { Group = g, StartedAt = ts }, state, detail, ts);
            }
        }

        private static void Emit(string user, OpenRun run, string state, string detail, DateTime endedAt)
        {
            var start = run.StartedAt == DateTime.MinValue ? endedAt : run.StartedAt;
            int dur = endedAt > start ? (int)(endedAt - start).TotalSeconds : 0;
            var rec = new RouteLedgerRecord
            {
                Account = user,
                Day = DayKey(endedAt),
                Group = run.Group,
                State = state,
                Start = start.ToString("HH:mm:ss.fff"),
                End = endedAt.ToString("HH:mm:ss.fff"),
                DurSec = dur,
                ScriptCount = run.ScriptCount,
                Exp = run.Exp,
                Mora = run.Mora,
                Picks = run.Picks,
                Detail = detail,
            };
            Outbox.Add(rec);
        }

        // ── 写线程 ───────────────────────────────────────────────────────
        //  已写入自然键缓存：key = account + "\u0001" + day。首次写入前读回文件已有的键。
        private static readonly Dictionary<string, HashSet<string>> WrittenKeys
            = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Lock WrittenLock = new();

        private static void WriteLoop()
        {
            foreach (var rec in Outbox.GetConsumingEnumerable())
            {
                try { AppendDeduped(rec); }
                catch
                {
                    // 落盘失败不能崩主程序：丢弃这条继续（诊断可后续加）。
                }
            }
        }

        private static void AppendDeduped(RouteLedgerRecord rec)
        {
            string day = string.IsNullOrEmpty(rec.Day) ? DayKey(DateTime.Now) : rec.Day;
            string file = Path.Combine(RootDir, SafeAccount(rec.Account), day + ".jsonl");
            string cacheKey = rec.Account + "\u0001" + day;

            HashSet<string> keys;
            lock (WrittenLock)
            {
                if (!WrittenKeys.TryGetValue(cacheKey, out keys!))
                {
                    keys = LoadExistingKeys(file);
                    WrittenKeys[cacheKey] = keys;
                }
            }

            string nk = rec.NaturalKey;
            lock (WrittenLock)
            {
                if (keys.Contains(nk)) return;   // 重启重扫/重复喂 → 已写过，跳过
            }

            string dir = Path.GetDirectoryName(file) ?? RootDir;
            Directory.CreateDirectory(dir);
            string line = JsonSerializer.Serialize(rec, JsonOpt) + "\n";
            using (var fs = new FileStream(file, FileMode.Append, FileAccess.Write,
                       FileShare.Read, 4096, FileOptions.WriteThrough))
            {
                var bytes = Encoding.UTF8.GetBytes(line);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush();
            }

            lock (WrittenLock) keys.Add(nk);
        }

        /// <summary>读回某天文件里已有的自然键（坏行跳过）。</summary>
        private static HashSet<string> LoadExistingKeys(string file)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(file)) return set;
                foreach (var line in File.ReadLines(file, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var rec = JsonSerializer.Deserialize<RouteLedgerRecord>(line, JsonOpt);
                        if (rec != null && !string.IsNullOrEmpty(rec.Group))
                            set.Add(rec.NaturalKey);
                    }
                    catch { /* 坏行：跳过 */ }
                }
            }
            catch { }
            return set;
        }

        internal static bool ValidGroup(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return !name.Equals("The operation was canceled.", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("OperationCanceledException", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Exception", StringComparison.OrdinalIgnoreCase);
        }

        private static DateTime StampOf(string text, int position, DateTime now)
        {
            int start = text.LastIndexOf('\n', Math.Max(0, position - 1));
            start = start < 0 ? 0 : start + 1;
            string prefix = text.Substring(start, Math.Min(120, Math.Max(0, position - start)));
            var m = RxTs.Match(prefix);
            if (!m.Success) return DateTime.MinValue;
            string date = m.Groups["date"].Success ? m.Groups["date"].Value.Trim() : "";
            string time = m.Groups["time"].Value;
            string stamp = (date.Length > 0 ? date + " " : now.ToString("yyyy-MM-dd") + " ") + time;
            if (!DateTime.TryParse(stamp, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces, out var at)) return DateTime.MinValue;
            if (date.Length == 0 && at > now.AddSeconds(2)) at = at.AddDays(-1);
            return at;
        }

        // ═══════════════════════════════════════════════════════════════
        //  读取侧：网页 / APP / 统计页只读
        // ═══════════════════════════════════════════════════════════════

        /// <summary>读某账号最近 N 天的全部记录（新→旧）。坏行跳过，不崩。</summary>
        public static List<RouteLedgerRecord> Query(string userName, int days = 1)
        {
            var result = new List<RouteLedgerRecord>();
            if (string.IsNullOrWhiteSpace(userName)) return result;
            int n = Math.Clamp(days, 1, 365);
            string dir = Path.Combine(RootDir, SafeAccount(userName));
            List<string> files;
            try
            {
                if (!Directory.Exists(dir)) return result;
                files = Directory.GetFiles(dir, "*.jsonl")
                    .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                    .Take(n).ToList();
            }
            catch { return result; }

            foreach (var f in files)
            {
                string day = Path.GetFileNameWithoutExtension(f);
                string[] lines;
                try { lines = File.ReadAllLines(f, Encoding.UTF8); }
                catch { continue; }
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var rec = JsonSerializer.Deserialize<RouteLedgerRecord>(line, JsonOpt);
                        if (rec == null || string.IsNullOrEmpty(rec.Group)) continue;
                        rec.Account = userName;
                        if (string.IsNullOrEmpty(rec.Day)) rec.Day = day;
                        result.Add(rec);
                    }
                    catch { /* 坏行：跳过这一行，继续读 */ }
                }
            }

            result.Sort((a, b) => string.CompareOrdinal(b.Day + " " + b.End, a.Day + " " + a.End));
            return result;
        }

        /// <summary>今日全部记录。</summary>
        public static List<RouteLedgerRecord> QueryToday(string userName) => Query(userName, 1);

        /// <summary>今日重点排查对象：0 收益的组 + 失败的组（并集）。</summary>
        public static List<RouteLedgerRecord> QueryTodayProblems(string userName)
            => Query(userName, 1).Where(r => r.ZeroReward || r.IsFailure).ToList();

        /// <summary>近 N 天 0 收益的组。</summary>
        public static List<RouteLedgerRecord> QueryZeroReward(string userName, int days)
            => Query(userName, days).Where(r => r.ZeroReward).ToList();

        /// <summary>近 N 天失败的组（Failed/Cancelled/Interrupted）。</summary>
        public static List<RouteLedgerRecord> QueryFailed(string userName, int days)
            => Query(userName, days).Where(r => r.IsFailure).ToList();

        /// <summary>近 N 天按组名聚合：平均收益、失败次数、总次数（最小预聚合形态）。</summary>
        public static List<RouteLedgerGroupSummary> GroupSummary(string userName, int days)
        {
            var recs = Query(userName, days);
            return recs
                .GroupBy(r => r.Group, StringComparer.OrdinalIgnoreCase)
                .Select(g => new RouteLedgerGroupSummary
                {
                    Group = g.Key,
                    Runs = g.Count(),
                    Failures = g.Count(r => r.IsFailure),
                    AvgExp = (long)Math.Round(g.Average(r => (double)r.Exp)),
                    AvgMora = (long)Math.Round(g.Average(r => (double)r.Mora)),
                    TotalExp = g.Sum(r => r.Exp),
                    TotalMora = g.Sum(r => r.Mora),
                })
                .OrderByDescending(s => s.Failures)
                .ThenBy(s => s.Group, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
