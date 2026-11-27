using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SoulLink.Services;

namespace SoulLink.MoraVerify;

/// <summary>t17 击杀归类引擎验收：规则 v4.1 边界 / kill_count=1 / 归属两档 / 重算 / 汇总。</summary>
public static class MoraVerify
{
    private static int _fail;
    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "  PASS " : "  FAIL ") + name);
        if (!ok) _fail++;
    }

    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string dbDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "mora-verify-" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(dbDir);
        Environment.SetEnvironmentVariable("SOULLINK_DATA_DIR", dbDir);
        // App.Config 反射初始化（rundb-check 同款）
        var cfgType = typeof(App).GetProperty("Config", BindingFlags.Public | BindingFlags.Static);
        try { cfgType?.SetValue(null, null); } catch { }

        // ★ 指向隔离 DB：MiyousheStore.Shared 可 set
        MiyousheStore.Shared = new MiyousheStore(() => System.IO.Path.Combine(dbDir, "miyoushe", "miyoushe.db"));

        if (args.Contains("--daily"))
        {
            if (!string.Equals(AppIdentity.DataDir, dbDir, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("测试库未隔离");
            return ScenarioDaily().GetAwaiter().GetResult();
        }
        int failures = 0;
        failures += ScenarioRules();       // 3) 边界用例 + 4) NULL→unknown
        failures += ScenarioEndToEnd();    // 2) 逐条列单 + 5) 归属两档 + unattributed
        failures += ScenarioReclassify();  // 6) 重算：旧版本保留、新版本生成、可对比
        failures += ScenarioSummary();     // 7) 汇总核对
        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL-PASS" : "FAILURES=" + failures);
        try { System.IO.Directory.Delete(dbDir, true); } catch { }
        return failures == 0 ? 0 : 1;
    }


    private static async Task<int> ScenarioDaily()
    {
        var day = new DateTime(2026, 11, 24);
        long d = new DateTimeOffset(day).ToUnixTimeMilliseconds();
        const string account = "ledger-test";
        void Run(string id, long from, long to) => RunRecordStore.AddRun(new() { Account = account,
            RunId = id, GroupName = "same-name", StartAtMs = d+from, EndAtMs = d+to, State="Finished" });
        void Route(string id,long from,long to) => RunRecordStore.AddRoute(new() { RunId="early", RouteId=id,
            Name = "same-route", Kind="route", StartAtMs=d+from,EndAtMs=d+to });
        Run("late",360000,86460000); Run("previous",-60000,60000); Run("early",60000,360000);
        Route("first",60000,150000); Route("second",150000,360000);
        var runs = await RunRecordStore.QueryRunDayAsync(account, day);
        Check(runs.Select(r=>r.RunId).SequenceEqual(new[]{"early","late"}), "开始日隔离，升序，同名组保留独立运行");
        var next = await RunRecordStore.QueryRunDayAsync(account,day.AddDays(1));
        Check(next.Count==0,"跨日轮次不在次日重复列出");
        var entries = new List<MiyousheLedgerEntry>();
        void Fact(long offset,long? amount,string action="精英怪",string uid="u1") => entries.Add(new(account,uid,"r","2026-11",2,
            entries.Count+1,action,amount,999,17,"",d+offset,"{}","test",d,entries.Count));
        Fact(-60000,10000); Fact(0,5); Fact(60000,31); Fact(120000,17); Fact(180000,null); Fact(240000,7,"任务"); Fact(86400000,9000);
        await MiyousheStore.Shared.SaveLedgerFactsAsync(entries,Array.Empty<MiyousheLedgerDay>(),default);
        var bins = await DailyMoraLedger.QueryAsync(account,day);
        Check(bins.Sum(b=>b.RecordCount)==5 && bins.Sum(b=>b.TotalMorale)==60,"只对当天 5 笔流水，已知金额守恒 60");
        Check(bins.Single(b=>b.RunId=="previous").TotalMorale==5,"凌晨流水可归属前日开始的窗口");
        Check(bins.Single(b=>b.RouteId=="first").TotalMorale==31,"唯一具体路线，使用原始 num");
        Check(bins.Single(b=>b.RunId=="early" && b.RouteId=="").TotalMorale==17,"同分钟换路线，只保留组归属");
        Check(bins.Single(b=>b.RouteId=="second") is {TotalMorale:null,MissingAmountCount:1},"缺失金额保留 null");
        Check(bins.Single(b=>b.RunId=="").TotalMorale==7,"非击杀收入保留为未归属");
        entries.Clear(); Fact(180000,23,"精英怪","u2");
        await MiyousheStore.Shared.SaveLedgerFactsAsync(entries,Array.Empty<MiyousheLedgerDay>(),default);
        var multi = await DailyMoraLedger.QueryAsync(account,day);
        Check(multi.Count==2 && multi.All(b=>b.RunId=="") && multi.Sum(b=>b.TotalMorale)==83,"多角色不串账，依 UID 对账 60+23");
        return _fail;
    }

    // ── 场景 1：规则 v4.1 边界用例（含 NULL → unknown）────────────
    private static int ScenarioRules()
    {
        Console.WriteLine("==== 场景1 规则 v4.1 边界 ====");
        // (value, routeName, expected)
        var cases = new (long? value, string route, string expect)[]
        {
            (200, "", "elite"), (400, "", "elite"),
            (600, "", "elite"),                          // ④ 其余 600 → 普通精英
            (800, "", "legendary_elite"), (1200, "", "legendary_elite"), (3000, "", "legendary_elite"),
            (600, "某传奇路线.json", "legendary_elite"),  // ②
            (600, "某精英路线.json", "elite"),             // ③
            (600, "07【额外】至冬-旧基捷城西盗罐者6.json", "elite"), // A4 真实路线名无关键词 → ④
            (151, "", "legendary_minion"),                // ⑤ 边界 >150
            (150, "", "minion"), (149, "", "minion"),   // ⑥ 边界
            (38, "", "minion"), (40, "", "minion"), (45, "", "minion"),  // 截图小值
            (0, "", "minion"), (-5, "", "minion"),      // 0/负数
            (int.MaxValue, "", "legendary_minion"),       // 巨大值
            (null, "", "unknown"),                        // 4) NULL 不猜
        };
        foreach (var (value, route, expect) in cases)
        {
            string got = MoraClassifier.Classify(value, route);
            string v = value?.ToString() ?? "null";
            Check(got == expect, $"single_morale={v} route='{route}' → {got} (expect {expect})");
        }
        // 回退：single NULL + morale=600,count=1 整除 → 600
        Check(MoraClassifier.ClassifyWithFallback(null, 1, 600, "") == "elite",
            "fallback morale=600/count=1 整除 → elite");
        Check(MoraClassifier.ClassifyWithFallback(null, null, 600, "") == "unknown",
            "fallback 无 count → unknown（不猜）");
        return _fail;
    }


    // ── 场景 2：端到端（构造数据 + 两档归属 + unattributed）────────
    private static int ScenarioEndToEnd()
    {
        Console.WriteLine("==== 场景2 端到端：归类 + 归属两档 ====");
        string account = "A4";
        // 时间轴：2026-11-24 当天（本地）
        long day = 1795478400000L; // 2026-11-24 00:00 本地
        var entries = new List<MiyousheLedgerEntry>();
        void Add(long tsMin, long? single, int? count, long? num, string action, int ordinal)
        {
            long tsMs = day + tsMin * 60_000;
            string timeText = DateTimeOffset.FromUnixTimeMilliseconds(tsMs).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            entries.Add(new MiyousheLedgerEntry(account, "100000001", "cn_gf01", "2026-11", 2,
                37, action, num, single, count, timeText, tsMs,
                "{\"action\":\"" + action + "\",\"single_morale\":" + (single?.ToString() ?? "null") + ",\"count\":" + (count?.ToString() ?? "null") + "}",
                "ys_ledger/monthDetail", 1796000000000L, ordinal));
        }
        // 构造 12 条：覆盖 4 类 + unknown + 两档归属 + unattributed + 同分钟跨路线
        Add(21 * 60 + 46, 147, 4, 588, "击杀怪物奖励", 1);     // 1) 07 路线窗内（分钟 21:46 只碰 07）→ minion
        Add(21 * 60 + 48, 46, 1, 46, "击杀怪物奖励", 2);       // 2) minion
        Add(21 * 60 + 50, 200, 2, 400, "击杀怪物奖励", 3);     // 3) elite
        Add(21 * 60 + 55, 3000, 1, 3000, "击杀怪物奖励", 4);   // 4) legendary_elite（项目窗内）
        Add(22 * 60 + 30, 600, 1, 600, "击杀怪物奖励", 5);     // 5) 项目窗内 elite
        Add(21 * 60 + 47, 152, 1, 152, "击杀怪物奖励", 6);     // 6) 同分钟跨 07/08 两窗 → minute_overlap
        Add(21 * 60 + 47, 153, 1, 153, "击杀怪物奖励", 7);     // 7) 同分钟跨 07/08 两窗 → minute_overlap
        Add(2 * 60, 40, 1, 40, "击杀怪物奖励", 8);             // 8) 凌晨 02:00 无窗口 → unattributed
        Add(12 * 60, null, null, 999, "击杀怪物奖励", 9);      // 9) NULL → unknown + unattributed
        Add(21 * 60 + 46, 800, 1, 800, "击杀怪物奖励", 10);    // 10) 路线窗前 1 分钟 → 项目档
        Add(21 * 60 + 46, 1000, 1, 1000, "击杀怪物奖励", 11);  // 11) legendary_minion
        Add(21 * 60 + 49, 45, 1, 45, "击杀怪物奖励", 12);      // 12) minion 路线档

        var t0 = MiyousheStore.Shared.SaveLedgerFactsAsync(entries,
            Array.Empty<MiyousheLedgerDay>(), default).GetAwaiter().GetResult();
        Console.WriteLine($"  seeded entries={entries.Count} inserted={t0}");

        // 注入窗口：项目窗（锄地一条龙 21:45~22:03）+ 路线窗（07盗罐者 21:46:17~21:47:33）
        var projects = new List<MoraClassifier.ProjectWindow>
        {
            new() { GroupName = "联机锄地-罐子", ProjectName = "锄地一条龙（联机）",
                StartMs = day + (21 * 3600 + 45 * 60) * 1000, EndMs = day + (22 * 3600 + 3 * 60 + 38) * 1000, Successful = true },
            new() { GroupName = "罐子激活", ProjectName = "圣遗物调查点纯激活",
                StartMs = day + 4 * 3600_000 + 1 * 60_000, EndMs = day + 4 * 3600_000 + 15 * 60_000, Successful = false },
        };
        var routes = new List<MoraClassifier.RouteWindow>
        {
            new() { RouteName = "07【额外】至冬-旧基捷城西盗罐者6.json", GroupName = "联机锄地-罐子",
                StartMs = day + (21 * 3600 + 46 * 60 + 17) * 1000, EndMs = day + (21 * 3600 + 47 * 60 + 33) * 1000 },
            new() { RouteName = "08【额外】至冬-旧基捷城东盗罐者17.json", GroupName = "联机锄地-罐子",
                StartMs = day + (21 * 3600 + 47 * 60 + 30) * 1000, EndMs = day + (21 * 3600 + 50 * 60) * 1000 },
        };
        MoraClassifier.WindowSourceForTest = _ => (projects, routes);

        var run = MoraClassifier.ClassifyAsync(account, default).GetAwaiter().GetResult();
        Console.WriteLine($"  run ok={run.Ok} entries={run.Entries} classified={run.Classified} unknown={run.Unknown}");
        Console.WriteLine($"  project_hits={run.ProjectHits} route_hits={run.RouteHits} unattributed={run.Unattributed}");
        Console.WriteLine("  逐条明细（single_morale + 路线 → category / kill_count / attribution）：");
        var rows = MoraClassifier.QueryRowsAsync(account, 30, MoraClassifier.RuleVersion, default)
            .GetAwaiter().GetResult();
        foreach (var r in rows.OrderBy(x => x.EntryRowid))
            Console.WriteLine($"    #{r.EntryRowid} single={r.SingleMora?.ToString() ?? "null"} " +
                $"route='{(r.RouteName.Length > 0 ? r.RouteName : "-")}' → {r.Category} / kill={r.KillCount} / " +
                $"{r.Attribution}({r.Confidence}){(r.Mark.Length > 0 ? "[" + r.Mark + "]" : "")} group={r.GroupName}");

        Check(run.Ok && run.Entries == 12, "12 条全部处理");
        Check(run.Unknown == 1, "NULL→unknown 恰 1 条");
        Check(rows.All(r => r.KillCount == 1), "kill_count 全为 1（恒 1，不乘算）");
        var mos = rows.Where(r => r.Mark == "minute_overlap").ToList();
        Check(mos.Count == 2, "同分钟跨两窗恰 2 条标记 minute_overlap");
        // t18 §5.3：路线档细分失败 → route 不硬塞（route_name 空），可回退项目档但 mark 保留
        Check(mos.All(m => m.RouteName.Length == 0), "minute_overlap 条目 route 不硬塞");
        Check(mos.All(m => m.Attribution == "project" || m.Attribution == "unattributed"),
            "minute_overlap 回退项目档或 unattributed（保留原始记录）");
        var trueUn = rows.Count(r => r.Attribution == "unattributed" && r.Mark.Length == 0);
        Check(trueUn == 3, "真无窗口（凌晨/NULL）→ unattributed 保留原始（3 条）");
        var un = rows.Count(r => r.Attribution == "unattributed");
        Console.WriteLine($"  归属分布：project={run.ProjectHits} route={run.RouteHits} unattributed={un}");
        // 项目级匹配率、路线级匹配率（构造数据：project 6 条含 minute_overlap 2、route 3 条）
        Console.WriteLine($"  项目级匹配率 = {run.ProjectHits}/{run.Entries} = {(100.0 * run.ProjectHits / run.Entries):F0}%");
        Console.WriteLine($"  路线级匹配率 = {run.RouteHits}/{run.Entries} = {(100.0 * run.RouteHits / run.Entries):F0}%");
        return _fail;
    }


    // ── 场景 3：重算（旧版本保留、新版本生成、可对比）────────────
    private static int ScenarioReclassify()
    {
        Console.WriteLine("==== 场景3 重算：旧版本保留可对比 ====");
        string account = "A4";
        _ = MiyousheStore.Shared.RunAsync<object?>(db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"INSERT OR REPLACE INTO my_mora_class(account,game_uid,entry_rowid,rule_version,
category,kill_count,attribution,confidence,attr_mark,group_name,project_name,route_name,
entry_ts_ms,entry_day,entry_action,entry_single_mora,entry_count,entry_morale,classified_at)
VALUES('A4','100000001',1,40,'minion',1,'unattributed','none','','','','',1795478400000+78420000,'2026-11-24','','814',NULL,814,0);";
            cmd.ExecuteNonQuery();
            return null;
        }, null, default).GetAwaiter().GetResult();

        var run = MoraClassifier.ClassifyAsync(account, default).GetAwaiter().GetResult();
        Check(run.Ok && run.Entries == 12, "重跑仍处理 12 条");

        var versions = MoraClassifier.QueryVersionsAsync(account, default).GetAwaiter().GetResult();
        Console.WriteLine("  versions=" + string.Join(",", versions));
        Check(versions.Contains(40) && versions.Contains(41), "v40 保留 + v4.1(41) 共存");

        var v40 = MoraClassifier.QueryRowsAsync(account, 30, 40, default).GetAwaiter().GetResult();
        var v41 = MoraClassifier.QueryRowsAsync(account, 30, 41, default).GetAwaiter().GetResult();
        Console.WriteLine($"  v40 rows={v40.Count}（旧）  v41 rows={v41.Count}（新）");
        var old1 = v40.FirstOrDefault(r => r.EntryRowid == 1);
        var new1 = v41.FirstOrDefault(r => r.EntryRowid == 1);
        if (old1 != null && new1 != null)
            Console.WriteLine($"  对比 entry#1: v40={old1.Category} → v4.1={new1.Category}（single=147）");
        Check(old1 != null && new1 != null, "两版本均可独立查询（可对比）");
        return _fail;
    }

    // ── 场景 4：汇总核对（四类记录条数与原始 entries 一致）──────────
    private static int ScenarioSummary()
    {
        Console.WriteLine("==== 场景4 汇总核对 ====");
        string account = "A4";
        var rows = MoraClassifier.QueryRowsAsync(account, 30, MoraClassifier.RuleVersion, default)
            .GetAwaiter().GetResult();
        var summary = MoraClassifier.QuerySummaryAsync(account, 30, null, null, MoraClassifier.RuleVersion, default)
            .GetAwaiter().GetResult();
        Console.WriteLine($"  rows={rows.Count}  summary 行数={summary.Count}");
        foreach (var s in summary)
            Console.WriteLine($"    {s.Day} group='{s.GroupName}' route='{(s.RouteName.Length > 0 ? s.RouteName : "-")}' " +
                $"传奇精英={s.LegendaryElite} 精英={s.Elite} 传奇小怪={s.LegendaryMinion} 小怪={s.Minion} " +
                $"unknown={s.Unknown} 记录数={s.RecordCount} 摩拉={s.TotalMorale}");

        int le = rows.Count(r => r.Category == "legendary_elite");
        int el = rows.Count(r => r.Category == "elite");
        int lm = rows.Count(r => r.Category == "legendary_minion");
        int mi = rows.Count(r => r.Category == "minion");
        int uk = rows.Count(r => r.Category == "unknown");
        int sumLe = summary.Sum(s => s.LegendaryElite);
        int sumEl = summary.Sum(s => s.Elite);
        int sumLm = summary.Sum(s => s.LegendaryMinion);
        int sumMi = summary.Sum(s => s.Minion);
        int sumUk = summary.Sum(s => s.Unknown);
        int sumRc = summary.Sum(s => s.RecordCount);
        Console.WriteLine($"  手工：传奇精英={le} 精英={el} 传奇小怪={lm} 小怪={mi} unknown={uk} 合计={le+el+lm+mi+uk}");
        Console.WriteLine($"  汇总：传奇精英={sumLe} 精英={sumEl} 传奇小怪={sumLm} 小怪={sumMi} unknown={sumUk} 合计={sumRc}");
        Check(sumLe == le && sumEl == el && sumLm == lm && sumMi == mi && sumUk == uk,
            "四类 + unknown 汇总与明细一致");
        Check(sumRc == le + el + lm + mi + uk, "record_count 合计 == 明细条数");
        Check(sumRc == rows.Count, "汇总条数 == 原始 entries 数");
        long totalMorale = summary.Sum(s => s.TotalMorale);
        Console.WriteLine($"  摩拉总额汇总={totalMorale}（明细 num 之和应为 {rows.Sum(r => r.Morale ?? 0)}）");
        Check(totalMorale == rows.Sum(r => r.Morale ?? 0), "摩拉总额与明细一致");
        return _fail;
    }
}



