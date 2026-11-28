using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SoulLink.Services;

internal static class Program
{
    private const string Acct = "A4";
    private static string Q(string s) => "\"" + s + "\"";   // wrap in quotes

    private static async Task<int> Main()
    {
        string db = Path.Combine(Path.GetTempPath(), "runcollect-verify-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        Console.OutputEncoding = Encoding.UTF8;
        await RunRecordStore.UseIsolatedForTest(db, (tag, ex) =>
        {
            if (ex != null) Console.WriteLine("[store-diag] " + tag + ": " + ex.Message);
        });
        RunRecordCollector.ResetForTest();

        // store smoke: AddError roundtrip
        {
            RunRecordStore.AddRun(new RunRecord { RunId = "smoke", Account = Acct, GroupName = "smoke-g", StartAtMs = 1000 });
            RunRecordStore.AddError(new ErrorRecord { RunId = "smoke", TsMs = 2000, Level = "ERR", Message = "m", Detail = "d" });
            await Task.Delay(400);
            var se = await RunRecordStore.QueryErrorsAsync("smoke");
            Console.WriteLine("store-smoke errs=" + se.Count);
        }

        int failures = 0;
        if (Environment.GetEnvironmentVariable("RUN_COLLECT_ISOLATION_ONLY") == "1")
            return await ScenarioInterleavedAccountsAndDays();
        if (Environment.GetEnvironmentVariable("T44_ONLY") == "1")
        { return await T44Bench.Run(); }
        if (Environment.GetEnvironmentVariable("T35_ONLY") == "1")
        { failures += await T35Scenarios.RunAll(); Console.WriteLine(); Console.WriteLine(failures == 0 ? "T35-ALL-PASS" : "T35-FAILURES=" + failures); return failures == 0 ? 0 : 1; }
        failures += await T35Scenarios.RunAll();  // t35 pick_event 跨 chunk 去重修复验收
        failures += await T10Scenarios.RunAll();  // t10 状态时段接线验收
        failures += await T6Scenarios.RunAll();   // t6 全套（每场景独立隔离 DB）
        await RunRecordStore.UseIsolatedForTest(db, (tag, ex) => { if (ex != null) Console.WriteLine("[store-diag] " + tag); });
        RunRecordCollector.ResetForTest();
        failures += await ScenarioInterleavedAccountsAndDays();
        failures += await ScenarioCrossDay();
        failures += await ScenarioCrossDayRestart();
        failures += await ScenarioRealLog();
        failures += await ScenarioIdempotentRescan();
        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL-PASS" : "FAILURES=" + failures);
        return failures == 0 ? 0 : 1;
    }

    private static async Task<int> ScenarioInterleavedAccountsAndDays()
    {
        Console.WriteLine("=== Realtime and historical logs interleaved across accounts and days ===");
        const string firstAccount = "audit-a", secondAccount = "audit-b", group = "audit-shared";
        var today = DateTime.Today;
        var yesterday = today.AddDays(-1);
        string todayFile = $"better-genshin-impact{today:yyyyMMdd}.log";
        string yesterdayFile = $"better-genshin-impact{yesterday:yyyyMMdd}.log";
        string Header(string time, string logger) => $"[{time}.000] [INF] [Primary:S3:P1:T1] {logger}\n";
        string start = Header("00:00:01", "ScriptService") + $"配置组 {Q(group)} 加载完成，共1个脚本，开始执行\n";
        string pick = Header("00:00:02", "TemplatePickupService") + $"交互或拾取：{Q("游医")}\n";
        string laterPick = Header("00:00:03", "TemplatePickupService") + $"交互或拾取：{Q("幸运儿")}\n";
        string end = Header("00:00:04", "ScriptService") + $"配置组 {Q(group)} 执行结束\n";

        RunRecordCollector.OnFileChanged(firstAccount, todayFile);
        RunRecordCollector.Feed(firstAccount, start + pick, todayFile);
        using (RunRecordCollector.EnterBackfillMode())
        {
            RunRecordCollector.OnFileChanged(firstAccount, yesterdayFile);
            RunRecordCollector.Feed(firstAccount, start + pick + end, yesterdayFile);
            RunRecordCollector.CloseOpenForBackfill(firstAccount, yesterdayFile);
        }
        RunRecordCollector.OnFileChanged(secondAccount, todayFile);
        RunRecordCollector.Feed(secondAccount, start + pick + end, todayFile);
        RunRecordCollector.Feed(firstAccount, laterPick + end, todayFile);
        await RunRecordStore.FlushAsync();

        var firstPass = (await RunRecordStore.QueryRunsAsync(firstAccount, 7))
            .Where(r => r.GroupName == group).ToDictionary(r => r.RunId);
        var pickCounts = new Dictionary<string, int>();
        foreach (var run in firstPass.Values)
            pickCounts[run.RunId] = (await RunRecordStore.QueryPickEventsAsync(run.RunId)).Count;

        // Repeat the exact historical batch as after a restart. The live state's prior
        // run and the other account's identically named run must be left alone.
        RunRecordCollector.ResetForTest();
        using (RunRecordCollector.EnterBackfillMode())
        {
            RunRecordCollector.OnFileChanged(firstAccount, yesterdayFile);
            RunRecordCollector.Feed(firstAccount, start + pick + end, yesterdayFile);
            RunRecordCollector.CloseOpenForBackfill(firstAccount, yesterdayFile);
        }
        await RunRecordStore.FlushAsync();

        var firstRuns = (await RunRecordStore.QueryRunsAsync(firstAccount, 7)).Where(r => r.GroupName == group).ToArray();
        var secondRuns = (await RunRecordStore.QueryRunsAsync(secondAccount, 7)).Where(r => r.GroupName == group).ToArray();
        bool IsRun(RunRecord row, DateTime day, int picks) =>
            row.StartDay == $"{day:yyyy-MM-dd}" && row.EndDay == row.StartDay && row.Picks == picks && row.State == "Finished";
        bool passed = firstRuns.Length == 2 && firstRuns.Any(r => IsRun(r, yesterday, 1))
            && firstRuns.Any(r => IsRun(r, today, 2)) && secondRuns.Length == 1 && IsRun(secondRuns[0], today, 1);
        bool replayUnchanged = firstRuns.Length == firstPass.Count;
        foreach (var row in firstRuns)
        {
            replayUnchanged &= firstPass.TryGetValue(row.RunId, out var before) &&
                row.StartAtMs == before.StartAtMs && row.EndAtMs == before.EndAtMs &&
                row.Picks == before.Picks &&
                (await RunRecordStore.QueryPickEventsAsync(row.RunId)).Count == pickCounts[row.RunId];
        }
        Console.WriteLine(passed ? "PASS: account/day isolation" : "FAIL: account/day isolation");
        Console.WriteLine(replayUnchanged ? "PASS: historical replay is idempotent" : "FAIL: historical replay changed results");
        passed &= replayUnchanged;
        if (!passed)
            foreach (var row in firstRuns.Concat(secondRuns)) Console.WriteLine($"{row.Account} {Describe(row)}");
        return passed ? 0 : 1;
    }

    // ── 手工跨天样本 ─────────────────────────────────────────────
    private static string Day1Log()
    {
        var sb = new StringBuilder();
        sb.AppendLine("[22:59:50.000] [INF] [Primary:S3:P1:T1] BetterGenshinImpact.Service.ScriptService");
        sb.AppendLine("配置组任务执行: 1/1");
        sb.AppendLine("[23:00:00.000] [INF] [Primary:S3:P1:T1] BetterGenshinImpact.Service.ScriptService");
        sb.AppendLine("配置组 " + Q("联机锄地-罐子") + " 加载完成，共1个脚本，开始执行");
        sb.AppendLine("[23:00:10.000] [INF] [Primary:S3:P1:T1] BetterGenshinImpact.Service.ScriptService");
        sb.AppendLine("→ 开始执行JS脚本: " + Q("锄地一条龙（联机）"));
        sb.AppendLine("[23:46:17.240] [INF] [Primary:S3:P1:T1] AutoHoeingTask");
        sb.AppendLine("[DEBUG] 进入路线循环，count=1，route=" + Q("07【额外】至冬-旧基捷城西盗罐者6.json"));
        sb.AppendLine("[23:47:26.142] [INF] [Primary:S3:P1:T1] TemplatePickupService");
        sb.AppendLine("交互或拾取：" + Q("游医"));
        sb.AppendLine("[23:59:59.000] [INF] [Primary:S3:P1:T1] TemplatePickupService");
        sb.AppendLine("交互或拾取：" + Q("幸运儿"));
        return sb.ToString();
    }

    private static string Day2Log()
    {
        var sb = new StringBuilder();
        sb.AppendLine("[00:10:00.000] [INF] [Primary:S3:P1:T1] TemplatePickupService");
        sb.AppendLine("交互或拾取：" + Q("游医"));
        sb.AppendLine("[00:59:30.000] [INF] [Primary:S3:P1:T1] AutoHoeingTask");
        sb.AppendLine("[DEBUG] 进入路线循环，count=2，route=" + Q("99-ZZZZ996璃月-明蕴镇神像.json"));
        sb.AppendLine("[01:00:00.000] [INF] [Primary:S3:P1:T1] AutoHoeingTask");
        sb.AppendLine("→ 脚本执行结束: " + Q("99-ZZZZ996璃月-明蕴镇神像.json"));
        sb.AppendLine("[01:00:00.500] [INF] [Primary:S3:P1:T1] ScriptService");
        sb.AppendLine("→ 脚本执行结束: " + Q("锄地一条龙（联机）") + ", 耗时: 119分50.5秒");
        sb.AppendLine("[01:00:01.000] [INF] [Primary:S3:P1:T1] ScriptService");
        sb.AppendLine("配置组 " + Q("联机锄地-罐子") + " 执行结束");
        return sb.ToString();
    }

    private static async Task<int> ScenarioCrossDay()
    {
        Console.WriteLine("=== Scenario 1: cross-day 23:00 -> next-day 01:00 ===");
        RunRecordCollector.ResetForTest();
        string f1 = "better-genshin-impact20261124.log";
        string f2 = "better-genshin-impact20261125.log";
        RunRecordCollector.Feed(Acct, Day1Log(), f1);
        RunRecordCollector.OnFileChanged(Acct, f2);
        RunRecordCollector.Feed(Acct, Day2Log(), f2);
        await Task.Delay(600);

        var runs = (await RunRecordStore.QueryRunsAsync(Acct, 7))
            .Where(r => r.GroupName == "联机锄地-罐子").OrderBy(r => r.StartAtMs).ToList();
        int fail = 0;
        // ★ t6 语义更新：换文件 = 拆 2 段（O1-O6 见 T6Scenarios 全量断言）
        if (runs.Count != 2) { Console.WriteLine("FAIL: 换文件应落 2 段，实际 " + runs.Count); return 1; }
        var seg1 = runs[0]; var seg2 = runs[1];
        if (seg1.StartAtMs != Ms("2026-11-24 23:00:00.000")) { fail++; Console.WriteLine("FAIL seg1.start=" + Iso(seg1.StartAtMs)); }
        if (seg2.GroupName.Length == 0) { fail++; Console.WriteLine("FAIL seg2 组名丢失"); }
        if (seg1.Picks + seg2.Picks != 3) { fail++; Console.WriteLine("FAIL picks 拆分=" + seg1.Picks + "+" + seg2.Picks + " (expect 合计 3)"); }
        Console.WriteLine("seg1: " + Describe(seg1));
        Console.WriteLine("seg2: " + Describe(seg2));
        return fail;
    }

    private static string Day2RestartLog()
    {
        var sb = new StringBuilder();
        sb.AppendLine("[00:30:00.000] [INF] [Primary:S3:P1:T1] TemplatePickupService");
        sb.AppendLine("交互或拾取：" + Q("幸运儿"));
        sb.AppendLine("[01:00:00.000] [INF] [Primary:S3:P1:T1] ScriptService");
        sb.AppendLine("配置组 " + Q("重启续跑组") + " 执行结束");
        return sb.ToString();
    }

    private static async Task<int> ScenarioCrossDayRestart()
    {
        Console.WriteLine("=== Scenario 2: restart, rescan day2 (is_backfilled=1) ===");
        // 重建「23:00 起跑后程序崩溃」的 DB 状态：喂 day1 前缀（只有 start，无收尾），
        // 然后清内存模拟重启，再喂 day2。
        var day1Prefix = new StringBuilder();
        day1Prefix.AppendLine("[23:00:00.000] [INF] [Primary:S3:P1:T1] ScriptService");
        day1Prefix.AppendLine("配置组 " + Q("重启续跑组") + " 加载完成，共1个脚本，开始执行");
        day1Prefix.AppendLine("[23:30:00.000] [INF] [Primary:S3:P1:T1] TemplatePickupService");
        day1Prefix.AppendLine("交互或拾取：" + Q("游医"));
        RunRecordCollector.ResetForTest();
        RunRecordCollector.Feed(Acct, day1Prefix.ToString(), "better-genshin-impact20261124.log");
        await Task.Delay(500);
        RunRecordCollector.ResetForTest();   // ← 模拟重启：内存窗口全丢，DB open 行仍在
        RunRecordCollector.Feed(Acct, Day2RestartLog(), "better-genshin-impact20261125.log");
        await Task.Delay(600);

        var runs = await RunRecordStore.QueryRunsAsync(Acct, 7);
        var run = runs.FirstOrDefault(r => r.GroupName == "重启续跑组" && r.EndAtMs != null);
        int fail = 0;
        if (run == null) { Console.WriteLine("FAIL: closed run missing"); return 1; }
        if (run.StartAtMs != Ms("2026-11-24 23:00:00.000")) { fail++; Console.WriteLine("FAIL start=" + Iso(run.StartAtMs) + " (expect 24 23:00)"); }
        if (run.IsBackfilled != true) { fail++; Console.WriteLine("FAIL is_backfilled=" + run.IsBackfilled + " (expect true)"); }
        if (run.IsCrossDay != true) { fail++; Console.WriteLine("FAIL is_cross_day"); }
        // 重启前窗口内的拾取只在内存，重启即丢（与 RouteLedger JSONL 同语义，契约 §7.2 只承诺起止时间与补全）
        if (run.Picks != 0) { fail++; Console.WriteLine("FAIL picks=" + run.Picks + " (expect 0: in-memory only)"); }
        Console.WriteLine("backfilled run: " + Describe(run));
        return fail;
    }

    private static async Task<int> ScenarioRealLog()
    {
        Console.WriteLine("=== Scenario 3: real log 20261124 (read-only) ===");
        RunRecordCollector.ResetForTest();
        string path = @"D:\AAA挂机专区\A4\BetterGI\log\better-genshin-impact20261124.log";
        if (!File.Exists(path)) { Console.WriteLine("SKIP: real log not found"); return 0; }
        string text = await File.ReadAllTextAsync(path, Encoding.UTF8);
        string file = Path.GetFileName(path);
        var lines = text.Split('\n');
        var sb = new StringBuilder();
        int fed = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            sb.Append(lines[i]);
            if (i < lines.Length - 1) sb.Append('\n');
            if ((i + 1) % 2000 == 0 || i == lines.Length - 1)
            {
                RunRecordCollector.Feed(Acct, sb.ToString(), file);
                sb.Clear();
                fed++;
            }
        }
        Console.WriteLine("chunks fed=" + fed + ", lines=" + lines.Length);
        await Task.Delay(800);

        var runs = await RunRecordStore.QueryRunsAsync(Acct, 30);
        int fail = 0;
        Console.WriteLine("runs captured: " + runs.Count);
        foreach (var r in runs) Console.WriteLine("  " + Describe(r));

        var hoedRun = runs.FirstOrDefault(r => r.GroupName == "联机锄地-罐子" && r.StartAtMs == Ms("2026-11-24 21:45:17.239"));
        if (hoedRun != null)
        {
            var routes = await RunRecordStore.QueryRoutesAsync(hoedRun.RunId);
            Console.WriteLine("routes under 联机锄地-罐子: " + routes.Count + "（t6 语义：1 script + 10 route + 10 地图追踪 script 触达）");
            foreach (var rt in routes.Take(12)) Console.WriteLine("  " + Describe(rt));
            // t6 语义：地图追踪 script 与 route 同名成对（不再 11）；幂等由 S4/T6-7 断言
            var errs = await RunRecordStore.QueryErrorsAsync(hoedRun.RunId);
            Console.WriteLine("errs under 联机锄地-罐子: " + errs.Count);
            foreach (var er in errs) Console.WriteLine("  " + Describe(er));
        }
        var jarRun = runs.FirstOrDefault(r => r.GroupName == "罐子激活");
        if (jarRun != null)
        {
            var jroutes = await RunRecordStore.QueryRoutesAsync(jarRun.RunId);
            Console.WriteLine("routes under 罐子激活: " + jroutes.Count + "（t6 语义：3 script + 8 cd_line）");
            foreach (var rt in jroutes) Console.WriteLine("  " + Describe(rt));
            var jerrs = await RunRecordStore.QueryErrorsAsync(jarRun.RunId);
            Console.WriteLine("errs under 罐子激活: " + jerrs.Count + " (expect >0 with full stack)");
            foreach (var er in jerrs.Take(4)) Console.WriteLine("  " + Describe(er));
            var withStack = jerrs.FirstOrDefault(e => e.Detail.Contains("ScriptEngineException"));
            if (withStack == null) { fail++; Console.WriteLine("FAIL: no err with full ClearScript stack"); }
            else
            {
                Console.WriteLine("FULL-STACK err detailLines=" + withStack.Detail.Split('\n').Length + " type=" + withStack.ExceptionType);
                Console.WriteLine("  msg=" + withStack.Message);
                Console.WriteLine("  detail head: " + Trunc(withStack.Detail, 160).Replace("\n", " | "));
            }
            var scriptWithErr = jroutes.FirstOrDefault(r => r.State == "Failed");
            if (scriptWithErr == null) { fail++; Console.WriteLine("FAIL: no Failed script route"); }
            else Console.WriteLine("Failed script: " + scriptWithErr.Name);
        }
        if (jarRun != null && jarRun.State != "Failed") { fail++; Console.WriteLine("FAIL jarRun state=" + jarRun.State); }
        if (hoedRun != null && hoedRun.State != "Finished") { fail++; Console.WriteLine("FAIL hoedRun state=" + hoedRun.State); }
        return fail;
    }

    private static async Task<int> ScenarioIdempotentRescan()
    {
        Console.WriteLine("=== Scenario 4: full rescan must not duplicate ===");
        string path = @"D:\AAA挂机专区\A4\BetterGI\log\better-genshin-impact20261124.log";
        if (!File.Exists(path)) { Console.WriteLine("SKIP: real log not found"); return 0; }
        string text = await File.ReadAllTextAsync(path, Encoding.UTF8);
        string file = Path.GetFileName(path);

        var before = await RunRecordStore.QueryRunsAsync(Acct, 30);
        int beforeRoutes = 0, beforeErrs = 0;
        foreach (var r in before)
        {
            beforeRoutes += (await RunRecordStore.QueryRoutesAsync(r.RunId)).Count;
            beforeErrs += (await RunRecordStore.QueryErrorsAsync(r.RunId)).Count;
        }

        RunRecordCollector.Feed(Acct, text, file);   // 全量重喂（模拟重启 cursor=0）
        await Task.Delay(800);

        var after = await RunRecordStore.QueryRunsAsync(Acct, 30);
        int afterRoutes = 0, afterErrs = 0;
        foreach (var r in after)
        {
            afterRoutes += (await RunRecordStore.QueryRoutesAsync(r.RunId)).Count;
            afterErrs += (await RunRecordStore.QueryErrorsAsync(r.RunId)).Count;
        }

        int fail = 0;
        if (after.Count != before.Count) { fail++; Console.WriteLine("FAIL runs " + before.Count + " -> " + after.Count); }
        if (afterErrs != beforeErrs) { fail++; Console.WriteLine("FAIL errs " + beforeErrs + " -> " + afterErrs); }
        if (afterRoutes != beforeRoutes) { fail++; Console.WriteLine("FAIL routes " + beforeRoutes + " -> " + afterRoutes); }
        Console.WriteLine("idempotent: runs=" + before.Count + "/" + after.Count
            + " routes=" + beforeRoutes + "/" + afterRoutes
            + " errs=" + beforeErrs + "/" + afterErrs);
        return fail;
    }

    // ── 工具 ──────────────────────────────────────────────────────
    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private static long Ms(string iso)
    {
        // t44：对齐采集器真 UTC 语义（本地时刻补时区偏移）
        var local = DateTime.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);
        var off = TimeZoneInfo.Local.GetUtcOffset(local);
        return (long)(local - Epoch).TotalMilliseconds - (long)off.TotalMilliseconds;
    }
    private static string Iso(long ms)
    {
        var utc = Epoch.AddMilliseconds(ms);
        return utc.Add(TimeZoneInfo.Local.GetUtcOffset(utc)).ToString("yyyy-MM-dd HH:mm:ss.fff");
    }

    private static string Describe(RunRecord r)
        => $"[{r.GroupName}] {Iso(r.StartAtMs)}→{(r.EndAtMs.HasValue ? Iso(r.EndAtMs.Value) : "OPEN")}"
         + $" state={r.State} scripts={r.ScriptCount} picks={r.Picks} exp={r.Exp} mora={r.Mora}"
         + $" cross={r.IsCrossDay} backfill={r.IsBackfilled} dur={r.DurMs}ms src={r.SrcFileStart}/{r.SrcFileEnd}";

    private static string Describe(RouteRecord t)
        => $"<{t.Kind}> {t.Name} {Iso(t.StartAtMs)}→{(t.EndAtMs.HasValue ? Iso(t.EndAtMs.Value) : "OPEN")}"
         + $" state={t.State} picks={t.PickCount} detail={t.PickDetail} dur={t.DurMs}ms";

    private static string Describe(ErrorRecord e)
        => $"ERR@{Iso(e.TsMs)} type={e.ExceptionType} route={(e.RouteId ?? "-")}"
         + $" msg={Trunc(e.Message, 80)} detailLines={e.Detail.Split('\n').Length} src={e.Source}";

    private static string Trunc(string s, int n)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}
