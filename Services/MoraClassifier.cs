using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace SoulLink.Services
{
    /// <summary>
    /// 击杀归类引擎（t17）：把米游社 my_ledger_entry 原始摩拉记录按 rule_version 换算成
    /// 精英/小怪/传奇击杀类别，并归属到 BGI 项目/路线。
    ///
    /// ── 纪律（t15/t18 契约）────────────────────────────────────────
    ///   · 归类是**消费方规则不是事实**：原始记录原样保留在 my_ledger_entry，
    ///     结果写独立表 my_mora_class，按 rule_version 可重算可对比。
    ///   · kill_count **恒为 1**（用户裁定「击杀数不算，按 1 来」）；
    ///     汇总口径 = 各类别**记录条数**（record_count）。
    ///     ★ 绝不拿 details 里的 count 乘算击杀数。
    ///   · single_morale 缺失（NULL）→ category=unknown，不猜（可用 ReclassifyAsync 补算）。
    ///   · 归属失败 → attribution=unattributed，保留原始记录，不硬塞给最近路线。
    ///
    /// ── 线程模型 ─────────────────────────────────────────────────────
    ///   读写全部经 MiyousheStore.RunAsync（单连接信号量串行，后台线程）；
    ///   无 DispatcherTimer；触发点=米游社拉取完成后（MiyousheService 调 ClassifyAsync）
    ///   与 /api/miyoushe/mora/classify 手动触发。
    /// </summary>
    internal static class MoraClassifier
    {
        // ── 规则版本与规则表（数据驱动：调档位改这里，不动采集代码）────────
        /// <summary>当前规则 v4.2：action 击杀前置过滤 + v4.1 的 single_morale 数值规则。</summary>
        internal const int RuleVersion = 42;   // 展示层可格式化为 "4.2"
        internal const int PreviousRuleVersion = 41;

        /// <summary>精英档位；800 虽仅 55/86641=0.06%，极稀有但合法，绝不能删。</summary>
        internal static readonly long[] EliteTierSet = { 200, 400, 600, 800, 1200, 3000 };

        /// <summary>
        /// t20 解包 + t21 真实数据出现过的击杀 action。action 与金额是两个独立维度：
        /// 宝箱200/其他3000不能因命中 EliteTierSet 被算成精英。
        /// </summary>
        internal static readonly string[] KillActions =
        { "精英怪", "小怪", "击杀怪物奖励", "普通精英怪", "中级精英怪", "高级精英怪" };

        /// <summary>传奇精英档位（规则①）。</summary>
        internal static readonly long[] LegendaryEliteTiers = { 800, 1200, 3000 };

        /// <summary>普通精英档位（规则④兜底：200/400/600 其余情况）。</summary>
        // <150 是安全界而非数据边界：真实小怪最小32，32~147密集，148/149/150全空档；
        // <150 占99.86%次数(432687/433284)。保留阈值契约，不把150伪称真实档位。
        internal const long LegendaryMinionThreshold = 150;

        /// <summary>类别枚举字符串（落库口径，小写下划线）。</summary>
        internal const string
            CatLegendaryElite = "legendary_elite",   // 传奇精英
            CatElite = "elite",                      // 普通精英
            CatLegendaryMinion = "legendary_minion", // 传奇小怪
            CatMinion = "minion",                    // 小怪
            CatNonKill = "non_kill",                // v4.2：非击杀 action，绝不进击杀类别
            CatUnknown = "unknown";                  // single_morale 缺失，不猜

        /// <summary>v4.2 唯一 action 前置判据。未列出的 action 一律 non_kill，不做模糊包含猜测。</summary>
        internal static bool IsKillAction(string? action)
        {
            if (string.IsNullOrWhiteSpace(action)) return false;
            string normalized = action.Trim();
            return Array.Exists(KillActions, x => x.Equals(normalized, StringComparison.Ordinal));
        }

        /// <summary>action 与数值交叉诊断；只输出事实标签，不输出原始行、不改判据。</summary>
        internal static string ActionTierDiagnostic(string? action, long? singleMora)
        {
            bool kill = IsKillAction(action);
            bool eliteAction = action is "精英怪" or "普通精英怪" or "中级精英怪" or "高级精英怪";
            bool tier = singleMora.HasValue && Array.IndexOf(EliteTierSet, singleMora.Value) >= 0;
            if (eliteAction && (!singleMora.HasValue || !tier)) return "elite_action_tier_mismatch";
            if (!kill && tier) return "non_kill_action_elite_tier";
            return "";
        }

        /// <summary>v4.2 严格入口：先 action 后数值，缺失 single_morale 仍 unknown。</summary>
        internal static string Classify(string action, long? singleMora, string? routeName)
        {
            if (!IsKillAction(action)) return CatNonKill;
            return Classify(singleMora, routeName);
        }

        /// <summary>v4.2 fallback 入口；count 仅反推单次数值，绝不是击杀数。</summary>
        internal static string ClassifyWithFallback(string action, long? singleMora, long? count,
            long? morale, string? routeName)
        {
            if (!IsKillAction(action)) return CatNonKill;
            return ClassifyWithFallback(singleMora, count, morale, routeName);
        }

        /// <summary>v4.2 路线细分入口，与最终写库路径共用同一 action 过滤。</summary>
        internal static string ClassifyValue(string action, long value, string routeName)
        {
            if (!IsKillAction(action)) return CatNonKill;
            return ClassifyValue(value, routeName);
        }

        /// <summary>归属枚举字符串。</summary>
        internal const string
            AttrProject = "project",          // 项目级（group_name+project_name，高置信）
            AttrRoute = "route",              // 路线文件级（低置信，分钟窗口）
            AttrUnattributed = "unattributed"; // 匹配不上，保留原始记录
        // ── v4.1 数值纯函数保留作对照；v4.2 生产统一带 action 前置过滤 ──────

        /// <summary>
        /// v4.1 兼容入口（无 action，生产禁用）。类别判定。single_morale 缺失 → unknown（不猜，t18 §6.3 的回退只在
        /// ClassifyWithFallback 里做近似，本函数保持严格）。
        /// 路线名只在规则②③（600 的传奇/普通细分）使用；**不因路线名缺关键词
        /// 而否定 single_morale 的判定**（t18 实测 A4 路线名全无「精英/传奇」字面量）。
        /// </summary>
        internal static string Classify(long? singleMora, string? routeName)
        {
            if (!singleMora.HasValue) return CatUnknown;
            return ClassifyValue(singleMora.Value, routeName ?? "");
        }

        /// <summary>v4.1 数值路径，仅供旧规则对照/回归；生产必须用带 action 的 v4.2 重载。</summary>
        internal static string ClassifyValue(long value, string routeName)
        {
            // ① 传奇精英：命中 800/1200/3000
            if (Array.IndexOf(LegendaryEliteTiers, value) >= 0) return CatLegendaryElite;

            // ② 600 + 路线名含「传奇」→ 传奇精英（可选细分）
            if (value == 600 && routeName.Contains("传奇", StringComparison.Ordinal))
                return CatLegendaryElite;

            // ③ 600 + 路线名含「精英」（不含传奇）→ 普通精英（可选细分）
            if (value == 600 && routeName.Contains("精英", StringComparison.Ordinal))
                return CatElite;

            // ④ 普通精英：200/400/600 其余情况
            if (Array.IndexOf(EliteTierSet, value) >= 0) return CatElite;

            // ⑤ 非枚举且 >150 → 传奇小怪
            if (value > LegendaryMinionThreshold) return CatLegendaryMinion;

            // ⑥ 其余（≤150，含 0/负数/巨大值落入⑤之外的正数规则按值判断）→ 小怪
            return CatMinion;
        }

        /// <summary>
        /// single_morale 缺失时的近似回退（t18 §6.3）：morale / count 整除可信才用，
        /// 仍不可得 → unknown。★ count 只用于反推单次值，绝不用于乘算击杀数。
        /// </summary>
        internal static string ClassifyWithFallback(long? singleMora, long? count, long? morale, string? routeName)
        {
            if (singleMora.HasValue) return ClassifyValue(singleMora.Value, routeName ?? "");
            if (morale.HasValue && count.HasValue && count.Value > 0)
            {
                long total = morale.Value, c = count.Value;
                if (total >= 0 && total % c == 0)
                    return ClassifyValue(total / c, routeName ?? "");
            }
            return CatUnknown;
        }
        // ── 归属窗口（t18 §5 两档）────────────────────────────────────────

        /// <summary>BGI ExecutionRecords 的项目窗口（纳秒 ISO 时间已转 ms）。</summary>
        internal sealed class ProjectWindow
        {
            public string GroupName = "";
            public string ProjectName = "";
            public long StartMs;
            public long EndMs;
            public bool Successful;
        }

        /// <summary>日志正文的路线窗口（进入路线循环 → 脚本执行结束，秒级）。</summary>
        internal sealed class RouteWindow
        {
            public string RouteName = "";
            public string GroupName = "";
            public long StartMs;
            public long EndMs;
        }

        /// <summary>
        /// 项目级归属（档一，高置信）：ts 落在唯一窗口 → 命中；0 条 → unattributed；
        /// 多条重叠 → 取 start 最大（最近）那条，confidence=medium。
        /// </summary>
        internal static string AttributeProject(IReadOnlyList<ProjectWindow> windows, long tsMs,
            out ProjectWindow? hit, out string confidence)
        {
            hit = null; confidence = "";
            List<ProjectWindow> matches = new();
            foreach (var w in windows)
                if (tsMs >= w.StartMs && tsMs <= w.EndMs) matches.Add(w);
            if (matches.Count == 0) { confidence = "none"; return AttrUnattributed; }
            if (matches.Count == 1) { hit = matches[0]; confidence = "high"; return AttrProject; }
            hit = matches.OrderByDescending(w => w.StartMs).First();
            confidence = "medium";   // 重叠：取最近，如实标注
            return AttrProject;
        }

        /// <summary>
        /// 路线文件级归属（档二，低置信）：分钟 [ts, ts+60s) 与秒级路线窗求交；
        /// 唯一命中 → 命中（medium）；0 条 → unattributed；多条（同分钟跨路线）→
        /// unattributed + mark=minute_overlap（无法细分，绝不硬塞）。
        /// </summary>
        internal static string AttributeRoute(IReadOnlyList<RouteWindow> windows, long tsMs,
            out RouteWindow? hit, out string confidence, out string mark)
        {
            hit = null; confidence = ""; mark = "";
            long minuteEnd = tsMs + 60_000;
            List<RouteWindow> matches = new();
            foreach (var w in windows)
                if (minuteEnd > w.StartMs && tsMs < w.EndMs) matches.Add(w);
            if (matches.Count == 0) { confidence = "none"; return AttrUnattributed; }
            if (matches.Count == 1) { hit = matches[0]; confidence = "medium"; return AttrRoute; }
            confidence = "none"; mark = "minute_overlap";   // 同分钟跨路线，无法细分
            return AttrUnattributed;
        }
        // ── 结果表 DDL（独立于原始表，可按 rule_version 重算共存）──────────
        private const string ClassDdl = @"
CREATE TABLE IF NOT EXISTS my_mora_class(
  account TEXT NOT NULL,game_uid TEXT NOT NULL,
  entry_rowid INTEGER NOT NULL,           -- my_ledger_entry 的 rowid（原始记录引用，不复制内容）
  rule_version INTEGER NOT NULL,          -- 41=v4.1, 42=v4.2(action前置)
  category TEXT NOT NULL,                 -- 四类 + non_kill + unknown
  kill_count INTEGER NOT NULL DEFAULT 1,  -- ★ 用户裁定恒 1，绝不乘算
  attribution TEXT NOT NULL,              -- project/route/unattributed
  confidence TEXT NOT NULL DEFAULT '',    -- high/medium/none
  attr_mark TEXT NOT NULL DEFAULT '',     -- minute_overlap 等细分标记
  group_name TEXT NOT NULL DEFAULT '',    -- 归属到的配置组（project/route 档）
  project_name TEXT NOT NULL DEFAULT '',
  route_name TEXT NOT NULL DEFAULT '',    -- 归属到的路线文件名（route 档）
  entry_ts_ms INTEGER,                    -- 冗余原始分钟时间戳（汇总免 join）
  entry_day TEXT NOT NULL DEFAULT '',     -- yyyy-MM-dd（冗余）
  entry_action TEXT NOT NULL DEFAULT '',
  entry_single_mora INTEGER,              -- 冗余判定依据（核对用）
  entry_count INTEGER,                    -- action×档位桶内次数(8~129等)，非一只怪/绝不乘击杀数
  entry_morale INTEGER,                   -- 冗余总额（汇总摩拉用）
  classified_at INTEGER NOT NULL,
  PRIMARY KEY(entry_rowid,rule_version));
CREATE INDEX IF NOT EXISTS ix_mora_class_account_day ON my_mora_class(account,entry_day);
CREATE INDEX IF NOT EXISTS ix_mora_class_group ON my_mora_class(account,group_name);
CREATE INDEX IF NOT EXISTS ix_mora_class_route ON my_mora_class(account,route_name);
CREATE TABLE IF NOT EXISTS my_mora_class_run(
  run_id INTEGER PRIMARY KEY AUTOINCREMENT,
  rule_version INTEGER NOT NULL,account TEXT NOT NULL,
  entries INTEGER NOT NULL,classified INTEGER NOT NULL,unknown INTEGER NOT NULL,
  project_hits INTEGER NOT NULL,route_hits INTEGER NOT NULL,unattributed INTEGER NOT NULL,
  started_at INTEGER NOT NULL,finished_at INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS my_mora_summary(
  account TEXT NOT NULL,game_uid TEXT NOT NULL,day TEXT NOT NULL,
  rule_version INTEGER NOT NULL,group_name TEXT NOT NULL DEFAULT '',route_name TEXT NOT NULL DEFAULT '',
  legendary_elite INTEGER NOT NULL DEFAULT 0,elite INTEGER NOT NULL DEFAULT 0,
  legendary_minion INTEGER NOT NULL DEFAULT 0,minion INTEGER NOT NULL DEFAULT 0,
  unknown INTEGER NOT NULL DEFAULT 0,non_kill INTEGER NOT NULL DEFAULT 0,
  record_count INTEGER NOT NULL DEFAULT 0,kill_record_count INTEGER NOT NULL DEFAULT 0,total_morale INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY(account,game_uid,day,rule_version,group_name,route_name));";

        /// <summary>确保结果表存在（幂等，经 store 单线程执行）。</summary>
        private static bool EnsureTables(SqliteConnection db)
        {
            try
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = ClassDdl;
                cmd.ExecuteNonQuery();
                EnsureColumn(db,"my_mora_summary","non_kill","INTEGER NOT NULL DEFAULT 0");
                EnsureColumn(db,"my_mora_summary","kill_record_count","INTEGER NOT NULL DEFAULT 0");
                return true;
            }
            catch { return false; }
        }
        private static void EnsureColumn(SqliteConnection db,string table,string column,string ddl)
        {
            using var check=db.CreateCommand();check.CommandText="PRAGMA table_info("+table+");";
            using(var reader=check.ExecuteReader())while(reader.Read())
                if(reader.GetString(1).Equals(column,StringComparison.OrdinalIgnoreCase))return;
            using var alter=db.CreateCommand();alter.CommandText="ALTER TABLE "+table+" ADD COLUMN "+column+" "+ddl+";";
            alter.ExecuteNonQuery();
        }

        /// <summary>分类结果行（写库前的内存形态）。</summary>
        private sealed class ClassRow
        {
            public long EntryRowid;
            public string GameUid = "";
            public string Category = CatUnknown;
            public string Attribution = AttrUnattributed;
            public string Confidence = "none";
            public string Mark = "";
            public string GroupName = "", ProjectName = "", RouteName = "";
            public long? TsMs;
            public string Day = "", Action = "";
            public long? SingleMora, Count, Morale;
        }

        /// <summary>
        /// 对某账号（可选限制时间范围）跑一轮分类。步骤：
        ///   ① 读 my_ledger_entry 原始行（kind=2 摩拉）；
        ///   ② 读 BGI ExecutionRecords 项目窗 + 日志路线窗（若可解析）；
        ///   ③ 逐条 ClassifyWithFallback + 两档归属；
        ///   ④ INSERT OR REPLACE my_mora_class（同 rule_version 幂等；其他版本结果不动）；
        ///   ⑤ 重算该账号该版本全部 my_mora_summary（先删后写，程序算好查询零减法）。
        /// 全部在 MiyousheStore.RunAsync 的串行 gate 内，参数化 SQL。
        /// </summary>
        internal static Task<MoraClassRunResult> ClassifyAsync(string account,
            CancellationToken ct = default)
            => MiyousheStore.Shared.RunAsync(db => ClassifyCore(db, account), MoraClassRunResult.Failed, ct);

        private static MoraClassRunResult ClassifyCore(SqliteConnection db, string account)
        {
            long started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!EnsureTables(db)) return MoraClassRunResult.Failed;

            // ① 原始 entries（只取 kind=2 摩拉流水）
            var entries = new List<(long rowid, string uid, string action, long? single, long? count,
                long? num, long? ts, string timeText, string raw)>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = @"SELECT rowid,game_uid,action,single_mora,count,num,ts_ms,time_text,raw_json
FROM my_ledger_entry WHERE account=$a AND kind=2 ORDER BY COALESCE(ts_ms,0),rowid;";
                cmd.Parameters.AddWithValue("$a", account);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    entries.Add((r.GetInt64(0), r.GetString(1), r.GetString(2),
                        OptLong(r, 3), OptLong(r, 4), OptLong(r, 5), OptLong(r, 6),
                        r.GetString(7), r.GetString(8)));
                }
            }
            if (entries.Count == 0)
                return new MoraClassRunResult(true, RuleVersion, account, 0, 0, 0, 0, 0, 0, started, started);

            // ② 归属窗口（读文件 IO 已在 RunAsync 串行段外完成——见 BuildWindows）
            var (projects, routes) = BuildWindows(account);

            // ③ 分类 + 归属
            var rows = new List<ClassRow>(entries.Count);
            foreach (var e in entries)
            {
                string category = ClassifyWithFallback(e.action, e.single, e.count, e.num, null);
                string attr = AttrUnattributed, conf = "none", mark = "";
                string g = "", p = "", rn = "";

                // 非击杀事实保留 non_kill，但不进入击杀路线/项目归属。
                if (category != CatNonKill && e.ts.HasValue)
                {
                    string pAttr = AttributeProject(projects, e.ts.Value, out var pw, out string pConf);
                    if (pw != null) { g = pw.GroupName; p = pw.ProjectName; }
                    string rAttr = AttributeRoute(routes, e.ts.Value, out var rw, out string rConf, out string rMark);
                    if (rw != null)
                    {
                        rn = rw.RouteName;
                        if (g.Length == 0) g = rw.GroupName;
                    }
                    // 归属优先路线档；路线档失败则项目档；都失败 unattributed
                    if (rAttr == AttrRoute) { attr = AttrRoute; conf = rConf; }
                    else if (pAttr == AttrProject) { attr = AttrProject; conf = pConf; mark = rMark; }
                    else { attr = AttrUnattributed; conf = "none"; mark = rMark; }
                }

                // 路线名只在 600 细分用（v4.1：路线名降级为可选信号）
                string finalCategory = e.single.HasValue
                    ? ClassifyValue(e.action, e.single.Value, rn.Length > 0 ? rn : (p.Length > 0 ? p : ""))
                    : category;

                string diagnostic = ActionTierDiagnostic(e.action, e.single);
                if (diagnostic.Length > 0) mark = mark.Length > 0 ? mark + ";" + diagnostic : diagnostic;
                rows.Add(new ClassRow
                {
                    EntryRowid = e.rowid,
                    GameUid = e.uid,
                    Category = finalCategory,
                    Attribution = attr,
                    Confidence = conf,
                    Mark = mark,
                    GroupName = g,
                    ProjectName = p,
                    RouteName = rn,
                    TsMs = e.ts,
                    Day = DayOf(e.ts),
                    Action = e.action,
                    SingleMora = e.single,
                    Count = e.count,
                    Morale = e.num,
                });
            }

        // TODO(E): 汇总查询
            // ④ 写 my_mora_class（INSERT OR REPLACE：同 (rowid,rule_version) 幂等重算）
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using (var tx = db.BeginTransaction())
            {
                using (var cmd = db.CreateCommand())
                {
                    cmd.CommandText = @"INSERT OR REPLACE INTO my_mora_class(account,game_uid,entry_rowid,
rule_version,category,kill_count,attribution,confidence,attr_mark,group_name,project_name,route_name,
entry_ts_ms,entry_day,entry_action,entry_single_mora,entry_count,entry_morale,classified_at)
VALUES($a,$u,$rid,$rv,$cat,1,$attr,$conf,$mark,$g,$p,$rn,$ts,$day,$act,$sm,$cnt,$mor,$at);";
                    var p = cmd.Parameters;
                    p.AddWithValue("$a", account); p.AddWithValue("$rv", RuleVersion); p.AddWithValue("$at", now);
                    p.Add("$u", SqliteType.Text); p.Add("$rid", SqliteType.Integer); p.Add("$cat", SqliteType.Text);
                    p.Add("$attr", SqliteType.Text); p.Add("$conf", SqliteType.Text); p.Add("$mark", SqliteType.Text);
                    p.Add("$g", SqliteType.Text); p.Add("$p", SqliteType.Text); p.Add("$rn", SqliteType.Text);
                    p.Add("$ts", SqliteType.Integer); p.Add("$day", SqliteType.Text); p.Add("$act", SqliteType.Text);
                    p.Add("$sm", SqliteType.Integer); p.Add("$cnt", SqliteType.Integer); p.Add("$mor", SqliteType.Integer);
                    foreach (var row in rows)
                    {
                        SetText(p, "$u", row.GameUid);
                        SetLong(p, "$rid", row.EntryRowid);
                        SetText(p, "$cat", row.Category);
                        SetText(p, "$attr", row.Attribution);
                        SetText(p, "$conf", row.Confidence);
                        SetText(p, "$mark", row.Mark);
                        SetText(p, "$g", row.GroupName);
                        SetText(p, "$p", row.ProjectName);
                        SetText(p, "$rn", row.RouteName);
                        SetLong(p, "$ts", row.TsMs);
                        SetText(p, "$day", row.Day);
                        SetText(p, "$act", row.Action);
                        SetLong(p, "$sm", row.SingleMora);
                        SetLong(p, "$cnt", row.Count);
                        SetLong(p, "$mor", row.Morale);
                        cmd.ExecuteNonQuery();
                    }
                }
                tx.Commit();
            }

            // ⑤ 重算汇总（先删该账号该版本，再全量写；程序算好，查询零减法）
            WriteSummary(db, account, rows);

            var result = new MoraClassRunResult(true, RuleVersion, account,
                entries.Count,
                rows.Count(x => x.Category != CatUnknown && x.Category != CatNonKill),
                rows.Count(x => x.Category == CatUnknown),
                rows.Count(x => x.Attribution == AttrProject),
                rows.Count(x => x.Attribution == AttrRoute),
                rows.Count(x => x.Attribution == AttrUnattributed),
                started, now);

            using (var log = db.CreateCommand())
            {
                log.CommandText = @"INSERT INTO my_mora_class_run(rule_version,account,entries,classified,
unknown,project_hits,route_hits,unattributed,started_at,finished_at)
VALUES($rv,$a,$e,$c,$u,$ph,$rh,$ua,$s,$f);";
                log.Parameters.AddWithValue("$rv", RuleVersion);
                log.Parameters.AddWithValue("$a", account);
                log.Parameters.AddWithValue("$e", result.Entries);
                log.Parameters.AddWithValue("$c", result.Classified);
                log.Parameters.AddWithValue("$u", result.Unknown);
                log.Parameters.AddWithValue("$ph", result.ProjectHits);
                log.Parameters.AddWithValue("$rh", result.RouteHits);
                log.Parameters.AddWithValue("$ua", result.Unattributed);
                log.Parameters.AddWithValue("$s", result.StartedAt);
                log.Parameters.AddWithValue("$f", result.FinishedAt);
                log.ExecuteNonQuery();
            }
            return result;
        }

        private static void WriteSummary(SqliteConnection db, string account, List<ClassRow> rows)
        {
            using var tx = db.BeginTransaction();
            using (var del = db.CreateCommand())
            {
                del.CommandText = "DELETE FROM my_mora_summary WHERE account=$a AND rule_version=$rv;";
                del.Parameters.AddWithValue("$a", account);
                del.Parameters.AddWithValue("$rv", RuleVersion);
                del.ExecuteNonQuery();
            }
            // 分组键：(uid, day, group, route)
            var groups = rows.GroupBy(x => (x.GameUid, x.Day, x.GroupName, x.RouteName));
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = @"INSERT OR REPLACE INTO my_mora_summary(account,game_uid,day,rule_version,
group_name,route_name,legendary_elite,elite,legendary_minion,minion,unknown,non_kill,record_count,kill_record_count,total_morale)
VALUES($a,$u,$d,$rv,$g,$rn,$le,$e,$lm,$m,$uk,$nk,$rc,$krc,$tm);";
                var p = cmd.Parameters;
                p.AddWithValue("$a", account); p.AddWithValue("$rv", RuleVersion);
                p.Add("$u", SqliteType.Text); p.Add("$d", SqliteType.Text); p.Add("$g", SqliteType.Text);
                p.Add("$rn", SqliteType.Text); p.Add("$le", SqliteType.Integer); p.Add("$e", SqliteType.Integer);
                p.Add("$lm", SqliteType.Integer); p.Add("$m", SqliteType.Integer); p.Add("$uk", SqliteType.Integer);
                p.Add("$nk", SqliteType.Integer); p.Add("$rc", SqliteType.Integer);
                p.Add("$krc", SqliteType.Integer); p.Add("$tm", SqliteType.Integer);
                foreach (var g in groups)
                {
                    SetText(p, "$u", g.Key.GameUid);
                    SetText(p, "$d", g.Key.Item2);
                    SetText(p, "$g", g.Key.Item3);
                    SetText(p, "$rn", g.Key.Item4);
                    SetLong(p, "$le", g.Count(x => x.Category == CatLegendaryElite));
                    SetLong(p, "$e", g.Count(x => x.Category == CatElite));
                    SetLong(p, "$lm", g.Count(x => x.Category == CatLegendaryMinion));
                    SetLong(p, "$m", g.Count(x => x.Category == CatMinion));
                    SetLong(p, "$uk", g.Count(x => x.Category == CatUnknown));
                    SetLong(p, "$nk", g.Count(x => x.Category == CatNonKill));
                    SetLong(p, "$rc", g.Count());
                    SetLong(p, "$krc", g.Count(x => x.Category != CatNonKill && x.Category != CatUnknown));
                    SetLong(p, "$tm", g.Sum(x => x.Morale ?? 0));
                    cmd.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
        // ── 窗口构建（文件 IO 只在这里，且经后台线程调用）─────────────────

        /// <summary>测试注入点：窗口来源可替换（不读真实文件）。</summary>
        internal static Func<string, (IReadOnlyList<ProjectWindow>, IReadOnlyList<RouteWindow>)>? WindowSourceForTest;

        private static (IReadOnlyList<ProjectWindow>, IReadOnlyList<RouteWindow>) BuildWindows(string account)
        {
            if (WindowSourceForTest != null) return WindowSourceForTest(account);
            try
            {
                var projects = LoadProjectWindows(account);
                var routes = LoadRouteWindows(account);
                return (projects, routes);
            }
            catch { return (Array.Empty<ProjectWindow>(), Array.Empty<RouteWindow>()); }
        }

        /// <summary>读 BGI ExecutionRecords/YYYYMMDD.json（项目窗，近 8 天）。</summary>
        private static List<ProjectWindow> LoadProjectWindows(string account)
        {
            var result = new List<ProjectWindow>();
            string? exe = TryBgiExe(account);
            if (string.IsNullOrWhiteSpace(exe)) return result;
            string dir = Path.Combine(Path.GetDirectoryName(exe) ?? "", "log", "ExecutionRecords");
            if (!Directory.Exists(dir)) return result;
            foreach (string file in Directory.GetFiles(dir, "*.json")
                         .OrderByDescending(f => f).Take(8))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    if (!doc.RootElement.TryGetProperty("execution_records", out var arr)) continue;
                    foreach (var item in arr.EnumerateArray())
                    {
                        string? start = TryString(item, "start_time");
                        string? end = TryString(item, "end_time");
                        if (start == null) continue;
                        long s = ParseIsoMs(start);
                        long e = end != null && end.StartsWith("0001-") ? 0 : ParseIsoMs(end ?? "");
                        if (e <= 0) e = s + 6 * 3600_000;   // 未结束：宽容 6h 窗口
                        result.Add(new ProjectWindow
                        {
                            GroupName = TryString(item, "group_name") ?? "",
                            ProjectName = TryString(item, "project_name") ?? "",
                            StartMs = s, EndMs = e,
                            Successful = TryBool(item, "is_successful"),
                        });
                    }
                }
                catch { }
            }
            return result;
        }

        private static string? TryBgiExe(string account)
        {
            try { return BgiPathResolver.GetBoundPath(account); }
            catch { return null; }
        }

        /// <summary>读 BGI 当日日志的路线窗（进入路线循环 → 脚本执行结束，近 2 天）。</summary>
        private static List<RouteWindow> LoadRouteWindows(string account)
        {
            var result = new List<RouteWindow>();
            string? exe = TryBgiExe(account);
            if (string.IsNullOrWhiteSpace(exe)) return result;
            string dir = Path.Combine(Path.GetDirectoryName(exe) ?? "", "log");
            if (!Directory.Exists(dir)) return result;
            foreach (string file in Directory.GetFiles(dir, "better-genshin-impact*.log")
                         .OrderByDescending(f => f).Take(2))
            {
                try { result.AddRange(MoraRouteWindowScanner.Scan(file)); }
                catch { }
            }
            return result;
        }

        private static string? TryString(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static bool TryBool(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        /// <summary>ISO8601（含纳秒/时区）→ epoch ms。</summary>
        private static long ParseIsoMs(string iso)
        {
            if (DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dto))
                return dto.ToUnixTimeMilliseconds();
            return 0;
        }

        // ── 小工具 ─────────────────────────────────────────────────────
        private static long? OptLong(SqliteDataReader r, int i)
            => r.IsDBNull(i) ? null : r.GetInt64(i);
        private static void SetText(SqliteParameterCollection p, string name, string? v)
            => p[name].Value = v ?? "";
        private static void SetLong(SqliteParameterCollection p, string name, long? v)
            => p[name].Value = v.HasValue ? (object)v.Value : DBNull.Value;
        private static string DayOf(long? tsMs)
            => tsMs.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(tsMs.Value)
                .ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
        // ── 结果与查询 API ────────────────────────────────────────────────

        internal sealed record MoraClassRunResult(bool Ok, int RuleVersion, string Account,
            int Entries, int Classified, int Unknown, int ProjectHits, int RouteHits, int Unattributed,
            long StartedAt, long FinishedAt)
        {
            internal static readonly MoraClassRunResult Failed =
                new(false, 0, "", 0, 0, 0, 0, 0, 0, 0, 0);
        }

        /// <summary>归类明细行（API 返回形态）。</summary>
        internal sealed record MoraClassRow(long EntryRowid, string Category, int KillCount,
            string Attribution, string Confidence, string Mark, string GroupName, string ProjectName,
            string RouteName, long? TsMs, string Day, string Action, long? SingleMora, long? Count, long? Morale);

        /// <summary>汇总行（API 返回形态，四类 + 摩拉总额，全部程序算好）。</summary>
        internal sealed record MoraSummaryRow(string Account, string GameUid, string Day, int RuleVersion,
            string GroupName, string RouteName, int LegendaryElite, int Elite, int LegendaryMinion,
            int Minion, int Unknown, int NonKill, int RecordCount, int KillRecordCount, long TotalMorale);

        /// <summary>按账号/日期范围取归类明细（rule_version 默认当前，可选旧版本对比）。</summary>
        internal static Task<IReadOnlyList<MoraClassRow>> QueryRowsAsync(string account, int days,
            int ruleVersion, CancellationToken ct = default)
            => MiyousheStore.Shared.RunAsync<IReadOnlyList<MoraClassRow>>(db =>
            {
                if (!EnsureTables(db)) return Array.Empty<MoraClassRow>();
                long cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 365)).ToUnixTimeMilliseconds();
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"SELECT entry_rowid,category,kill_count,attribution,confidence,attr_mark,
group_name,project_name,route_name,entry_ts_ms,entry_day,entry_action,entry_single_mora,entry_count,entry_morale
FROM my_mora_class WHERE account=$a AND rule_version=$rv AND COALESCE(entry_ts_ms,0)>=$c
ORDER BY COALESCE(entry_ts_ms,0),entry_rowid LIMIT 20000;";
                cmd.Parameters.AddWithValue("$a", account);
                cmd.Parameters.AddWithValue("$rv", ruleVersion);
                cmd.Parameters.AddWithValue("$c", cutoff);
                var list = new List<MoraClassRow>();
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(new(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetString(3),
                        r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8),
                        OptLong(r, 9), r.GetString(10), r.GetString(11), OptLong(r, 12), OptLong(r, 13), OptLong(r, 14)));
                return (IReadOnlyList<MoraClassRow>)list;
            }, Array.Empty<MoraClassRow>(), ct);

        /// <summary>按账号/日期范围/可选组名/路线名取汇总（rule_version 默认当前）。</summary>
        internal static Task<IReadOnlyList<MoraSummaryRow>> QuerySummaryAsync(string account, int days,
            string? groupName, string? routeName, int ruleVersion, CancellationToken ct = default)
            => MiyousheStore.Shared.RunAsync<IReadOnlyList<MoraSummaryRow>>(db =>
            {
                if (!EnsureTables(db)) return Array.Empty<MoraSummaryRow>();
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"SELECT account,game_uid,day,rule_version,group_name,route_name,
legendary_elite,elite,legendary_minion,minion,unknown,non_kill,record_count,kill_record_count,total_morale
FROM my_mora_summary WHERE account=$a AND rule_version=$rv AND day>=$d"
                    + (string.IsNullOrEmpty(groupName) ? "" : " AND group_name=$g")
                    + (string.IsNullOrEmpty(routeName) ? "" : " AND route_name=$rn")
                    + " ORDER BY day DESC,group_name,route_name LIMIT 20000;";
                cmd.Parameters.AddWithValue("$a", account);
                cmd.Parameters.AddWithValue("$rv", ruleVersion);
                cmd.Parameters.AddWithValue("$d", CutoffDay(days));
                if (!string.IsNullOrEmpty(groupName)) cmd.Parameters.AddWithValue("$g", groupName);
                if (!string.IsNullOrEmpty(routeName)) cmd.Parameters.AddWithValue("$rn", routeName);
                var list = new List<MoraSummaryRow>();
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3),
                        r.GetString(4), r.GetString(5), r.GetInt32(6), r.GetInt32(7), r.GetInt32(8),
                        r.GetInt32(9), r.GetInt32(10), r.GetInt32(11), r.GetInt32(12), r.GetInt32(13), r.GetInt64(14)));
                return (IReadOnlyList<MoraSummaryRow>)list;
            }, Array.Empty<MoraSummaryRow>(), ct);

        private static string CutoffDay(int days)
            => DateTime.Now.AddDays(-Math.Clamp(days, 1, 365)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        /// <summary>列出全部已跑过的规则版本（对比用）。</summary>
        internal static Task<IReadOnlyList<int>> QueryVersionsAsync(string account, CancellationToken ct = default)
            => MiyousheStore.Shared.RunAsync<IReadOnlyList<int>>(db =>
            {
                if (!EnsureTables(db)) return Array.Empty<int>();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT DISTINCT rule_version FROM my_mora_class WHERE account=$a ORDER BY rule_version;";
                cmd.Parameters.AddWithValue("$a", account);
                var list = new List<int>();
                using var r = cmd.ExecuteReader();
                while (r.Read()) list.Add(r.GetInt32(0));
                return (IReadOnlyList<int>)list;
            }, Array.Empty<int>(), ct);

        internal static void ResetForTest() { WindowSourceForTest = null; }
    }
}
