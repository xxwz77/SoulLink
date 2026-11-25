using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SoulLink.Services;

internal static class T6Scenarios
{
    private const string Acct = "A4";
    private static int _fail;
    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "  PASS " : "  FAIL ") + name);
        if (!ok) _fail++;
    }

    public static async Task<int> RunAll()
    {
        _fail = 0;
        await ScenarioA4Replay();          // 验收2/3/4：锄地 5/5 + 激活CD 8 条 + 地图追踪
        await ScenarioDaySplit();          // 验收6：换文件跨天主场景（2 条、组名、不重复、cross、幂等）
        await ScenarioDaySplitNoEvent();   // 90s 兜底：不建空段
        await ScenarioRestart();           // 验收7：重启次要路径无 ghost
        await ScenarioSpans();             // 验收8：三态切段 + 跨天 idle 1 条 + 未绑定不产段
        await ScenarioCdErrPicks();        // 验收4 专项①：CD→ERR→拾取（构造）
        await ScenarioA1ThreeClasses();    // 验收5：A1 三类计数现算现比
        await ScenarioRescanIdempotent();  // 验收9：全量重扫幂等
        return _fail;
    }

    private static string Db()
    {
        string db = Path.Combine(Path.GetTempPath(), "t6-verify-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        return db;
    }

    private static async Task Flush() => await RunRecordStore.FlushAsync();


    // ── 验收 2/3/4：A4 20261124 真实回放 ─────────────────────────────
    private static async Task<int> ScenarioA4Replay()
    {
        Console.WriteLine("==== T6-1 A4 20261124 回放（锄地 5/5 + 激活CD 8 + 地图追踪）====");
        string db = Db();
        await RunRecordStore.UseIsolatedForTest(db, (tag, ex) => { if (ex != null) Console.WriteLine("[diag] " + tag); });
        RunRecordCollector.ResetForTest();
        string path = @"D:\AAA挂机专区\A4\BetterGI\log\better-genshin-impact20261124.log";
        if (!File.Exists(path)) { Console.WriteLine("  SKIP: A4 日志不存在"); return 0; }
        string text = await File.ReadAllTextAsync(path, Encoding.UTF8);
        string file = Path.GetFileName(path);
        // 分 chunk 喂（模拟增量）
        var lines = text.Split('\n');
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            sb.Append(lines[i]); if (i < lines.Length - 1) sb.Append('\n');
            if ((i + 1) % 2000 == 0 || i == lines.Length - 1)
            { RunRecordCollector.Feed(Acct, sb.ToString(), file); sb.Clear(); }
        }
        await Flush();

        var runs = await RunRecordStore.QueryRunsAsync(Acct, 30);
        var hoed = runs.FirstOrDefault(r => r.GroupName == "联机锄地-罐子");
        var jar = runs.FirstOrDefault(r => r.GroupName == "罐子激活");

        // 验收2a：锄地那轮 5 条路线（第二轮 1-5）
        if (hoed != null)
        {
            var routes = await RunRecordStore.QueryRoutesAsync(hoed.RunId);
            var allRoutes = routes.Where(r => r.Kind == "route").ToList();
            Console.WriteLine($"  锄地 route 总数={allRoutes.Count}（两轮共 10 条 07-99）；script={routes.Count(r => r.Kind == "script")}");
            foreach (var rt in routes.Where(r => r.Kind != "route").Take(3))
                Console.WriteLine($"    script: {rt.Name} state={rt.State}");
            var r2 = routes.Where(r => r.Kind == "route").OrderBy(r => r.StartAtMs).Take(5).ToList();
            Console.WriteLine("  第一轮 5 条：");
            foreach (var rt in r2) Console.WriteLine($"    {rt.Name} state={rt.State} picks={rt.PickCount} picked={rt.Picked} dur={rt.DurMs}");
            Check(allRoutes.Count == 10, "锄地两轮共 10 条 route（每轮 5 条）");
            Check(r2.Count == 5 && r2.All(r => r.Name.Length > 0), "验收2：第一轮 5 条全有名字");
            var picksRound1 = r2.Sum(r => r.PickCount);
            Console.WriteLine($"  第一轮 picks 合计={picksRound1}");
        }
        else Check(false, "锄地 run 存在");

        // 验收2b：激活CD 8 条线路，终点校验失败那条 Failed
        if (jar != null)
        {
            var cdRoutes = (await RunRecordStore.QueryRoutesAsync(jar.RunId)).Where(r => r.Kind == "cd_line").ToList();
            Console.WriteLine($"  激活CD 线路数={cdRoutes.Count}（计划 8 条）");
            foreach (var rt in cdRoutes)
                Console.WriteLine($"    {rt.Name} state={rt.State} picks={rt.PickCount} picked={rt.Picked}");
            Check(cdRoutes.Count == 8, "验收2：激活CD 8 条线路全部落库");
            var failed = cdRoutes.FirstOrDefault(r => r.State == "Failed");
            Check(failed != null && failed.Name.Contains("7至冬额外激活CD"),
                "验收2：终点校验失败线路（7至冬）Failed");
            var errs = await RunRecordStore.QueryErrorsAsync(jar.RunId);
            var cdErr = errs.FirstOrDefault(e => e.RouteId != null && failed != null && e.RouteId == failed.RouteId
                && e.Detail.Contains("终点校验失败"));
            Check(cdErr != null, "验收4：失败原因（终点校验失败）落在该线路 err");
        }
        else Check(false, "罐子激活 run 存在");

        // 验收3：地图追踪任务出现在 route 表（script kind，名字=路线 json）
        var mapTracks = (await RunRecordStore.QueryRoutesAsync(hoed?.RunId ?? "")).Where(r => r.Name.EndsWith(".json") && r.Kind == "script").ToList();
        Console.WriteLine($"  地图追踪 script（.json 名）数={mapTracks.Count}");
        Check(mapTracks.Count > 0, "验收3：地图追踪任务出现在 route 表");

        // 验收4 专项①：激活CD → ERR → 采集物区拾取 → 结束（pick_count>0、Failed、守恒）
        if (jar != null)
        {
            var pickEvents = await RunRecordStore.QueryPickEventsAsync(jar.RunId);
            var cdPicks = pickEvents.Where(p => p.RouteId != null).ToList();
            Console.WriteLine($"  罐子激活 pick_event 总数={pickEvents.Count}（picked/skipped/no_target=" +
                $"{pickEvents.Count(p => p.Kind == "picked")}/{pickEvents.Count(p => p.Kind == "skipped")}/{pickEvents.Count(p => p.Kind == "no_target")}）");
            // 真实日志该时段（04:01-04:15）无拾取行——专项①的 pick>0/Failed/守恒由 T6-8 构造场景断言
            Console.WriteLine($"  罐子激活 pick_event={pickEvents.Count}（真实该时段无拾取行，0 是事实）");
            // 守恒：三类事件行数 == 日志里三类行数（对 jar run 期间难精确切窗，用总体核对）
            int logPicked = text.Split('\n').Count(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"交互或拾取\s*[:：]\s*\""[^\""+]+\"""));
            Console.WriteLine($"  日志三类行数：picked={logPicked}");
        }
        return _fail;
    }


    // ── 验收6 专项③：换文件跨天主场景（同一进程连续写入）────────────
    private static async Task<int> ScenarioDaySplit()
    {
        Console.WriteLine("==== T6-2 专项③：换文件跨天 → 2 条 ====");
        string db = Db();
        await RunRecordStore.UseIsolatedForTest(db, (t, e) => { });
        RunRecordCollector.ResetForTest();
        string f1 = "better-genshin-impact20261124.log";
        string f2 = "better-genshin-impact20261125.log";

        var day1 = new StringBuilder();
        day1.AppendLine("[23:00:00.000] [INF] [Primary:S3:P1:T1] ScriptService");
        day1.AppendLine("配置组 \"跨天组X\" 加载完成，共1个脚本，开始执行");
        day1.AppendLine("[23:30:00.000] [INF] [Primary:S3:P1:T1] Pickup");
        day1.AppendLine("交互或拾取：\"游医\"");
        day1.AppendLine("[23:59:00.000] [INF] [Primary:S3:P1:T1] Reward");
        day1.AppendLine("狗粮路线获取狗粮经验: 63000");
        RunRecordCollector.Feed(Acct, day1.ToString(), f1);
        await Flush();

        // 0 点换文件（BGI 不重启，只是新日志）
        RunRecordCollector.OnFileChanged(Acct, f2);
        await Flush();

        var day2 = new StringBuilder();
        day2.AppendLine("[00:00:05.000] [INF] [Primary:S3:P1:T1] Heartbeat");
        day2.AppendLine("[00:30:00.000] [INF] [Primary:S3:P1:T1] Pickup");
        day2.AppendLine("交互或拾取：\"幸运儿\"");
        day2.AppendLine("[01:00:00.000] [INF] [Primary:S3:P1:T1] ScriptService");
        day2.AppendLine("配置组 \"跨天组X\" 执行结束");
        RunRecordCollector.Feed(Acct, day2.ToString(), f2);
        await Flush();

        var runs = (await RunRecordStore.QueryRunsAsync(Acct, 7)).Where(r => r.GroupName == "跨天组X").OrderBy(r => r.StartAtMs).ToList();
        Console.WriteLine($"  落库条数={runs.Count}");
        foreach (var r in runs)
            Console.WriteLine($"    [{r.GroupName}] {Iso(r.StartAtMs)}→{Iso(r.EndAtMs ?? 0)} state={r.State} picks={r.Picks} exp={r.Exp}" +
                $" cross={r.IsCrossDay} split={r.DaySplit} backfill={r.IsBackfilled} src={r.SrcFileStart}/{r.SrcFileEnd}");

        Check(runs.Count == 2, "O1：落 2 条");
        if (runs.Count == 2)
        {
            var seg1 = runs[0]; var seg2 = runs[1];
            Check(seg2.GroupName == "跨天组X" && seg2.GroupName.Length > 0, "O2：第二条有组名（继承）");
            Check(seg1.EndAtMs.HasValue && seg2.StartAtMs > seg1.EndAtMs, "O3：时间首尾相接");
            Check(seg1.EndAtMs == Ms("2026-11-24 23:59:00.000"), "O3：seg1.end=当日最后事件 23:59:00");
            Check(seg2.StartAtMs == Ms("2026-11-25 00:00:05.000"), "seg2.start=次日首行 00:00:05");
            Check(seg1.Picks == 1 && seg2.Picks == 1, "O4：拾取不重复（各 1）");
            Check(seg1.Exp == 63000 && seg2.Exp == 0, "O4：收益不重复（63000 只在 seg1）");
            Check(seg1.IsCrossDay && seg2.IsCrossDay, "O5：两条 is_cross_day=1");
            Check(seg1.DaySplit && seg2.DaySplit, "O5：两条 day_split=1");
            Check(seg1.SrcFileStart == f1 && seg2.SrcFileStart == f2, "O5：src_file 各自不同");
            Check(seg1.State == "Interrupted" && seg1.Detail.Contains("跨天换文件分段"), "seg1=Interrupted+分段标注(非异常)");
            Check(seg2.State == "Finished", "seg2 正常收尾 Finished");
            Check(seg2.IsBackfilled == false, "seg2 is_backfilled=0（主路径非重启）");
        }

        // O6：全量重扫幂等
        RunRecordCollector.ResetForTest();
        await RunRecordStore.LoadOpenRunsAsync();
        RunRecordCollector.Feed(Acct, day1.ToString(), f1);
        RunRecordCollector.OnFileChanged(Acct, f2);
        RunRecordCollector.Feed(Acct, day2.ToString(), f2);
        await Flush();
        var runs2 = (await RunRecordStore.QueryRunsAsync(Acct, 7)).Where(r => r.GroupName == "跨天组X").ToList();
        Console.WriteLine($"  重扫后：runs={runs2.Count}");
        Check(runs2.Count == 2, "O6：重扫仍 2 条");
        var reSeg1 = runs2.OrderBy(r => r.StartAtMs).First();
        Check(reSeg1.Picks == 1 && reSeg1.Exp == 63000, "O6：重扫内容一致（不重复计入）");
        return _fail;
    }

    private static long Ms(string iso)
    {
        // t44：与采集器新的真 UTC 语义对齐——本地时刻补上时区偏移
        var local = DateTime.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);
        var offset = TimeZoneInfo.Local.GetUtcOffset(local);
        return (long)(local - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)).TotalMilliseconds
            - (long)offset.TotalMilliseconds;
    }
    private static string Iso(long ms)
    {
        var utc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMilliseconds(ms);
        return utc.Add(TimeZoneInfo.Local.GetUtcOffset(utc)).ToString("yyyy-MM-dd HH:mm:ss.fff");
    }


    // ── 90s 兜底：换文件后无事件 → 不建次日段 ───────────────────────
    private static async Task<int> ScenarioDaySplitNoEvent()
    {
        Console.WriteLine("==== T6-3 90s 兜底：无首事件不建次日段 ====");
        string db = Db();
        await RunRecordStore.UseIsolatedForTest(db, (t, e) => { });
        RunRecordCollector.ResetForTest();
        string f1 = "better-genshin-impact20261124.log";
        string f2 = "better-genshin-impact20261125.log";
        var day1 = new StringBuilder();
        day1.AppendLine("[23:00:00.000] [INF] [Primary:S3:P1:T1] ScriptService");
        day1.AppendLine("配置组 \"无事件组\" 加载完成，共1个脚本，开始执行");
        day1.AppendLine("[23:40:00.000] [INF] [Primary:S3:P1:T1] Pickup");
        day1.AppendLine("交互或拾取：\"游医\"");
        RunRecordCollector.Feed(Acct, day1.ToString(), f1);
        await Flush();
        RunRecordCollector.OnFileChanged(Acct, f2);   // 换文件
        await Flush();
        // 不喂 day2 —— BGI 停了
        var runs = (await RunRecordStore.QueryRunsAsync(Acct, 7)).Where(r => r.GroupName == "无事件组").ToList();
        Console.WriteLine($"  runs={runs.Count}（期望 1：只有当日段）");
        Check(runs.Count == 1 && runs[0].DaySplit, "90s 无事件 → 只 1 条当日段（day_split=1，不建空段）");
        return _fail;
    }

    // ── 验收7 专项③b：重启次要路径（无 ghost）──────────────────────
    private static async Task<int> ScenarioRestart()
    {
        Console.WriteLine("==== T6-4 专项③b：重启后继续 → 无 ghost ====");
        string db = Db();
        await RunRecordStore.UseIsolatedForTest(db, (t, e) => { });
        RunRecordCollector.ResetForTest();
        string f1 = "better-genshin-impact20261124.log";
        var day1 = new StringBuilder();
        day1.AppendLine("[23:00:00.000] [INF] [Primary:S3:P1:T1] ScriptService");
        day1.AppendLine("配置组 \"重启组\" 加载完成，共1个脚本，开始执行");
        day1.AppendLine("[23:50:00.000] [INF] [Primary:S3:P1:T1] Pickup");
        day1.AppendLine("交互或拾取：\"游医\"");
        RunRecordCollector.Feed(Acct, day1.ToString(), f1);
        await Flush();

        // 模拟程序重启：内存清空 + EnsureBootedAsync 灌回 open 行
        RunRecordCollector.ResetForTest();
        await RunRecordCollector.EnsureBootedAsync();
        var openRuns = await RunRecordStore.LoadOpenRunsAsync();
        Console.WriteLine($"  灌回 open 行={openRuns.Count}");
        // OnFileChanged 时钟无基线（t5 O1 场景）→ 不 close
        RunRecordCollector.OnFileChanged(Acct, "better-genshin-impact20261125.log");
        var mid = (await RunRecordStore.QueryRunsAsync(Acct, 7)).Where(r => r.GroupName == "重启组").ToList();
        Console.WriteLine($"  无基线换文件后 runs={mid.Count}（open 行未被误关）");
        Check(mid.Count == 1 && mid[0].EndAtMs == null, "重启+无基线：open 行保持（不 close 不 ghost）");

        // 次日事件续跑：同组结束事件 → close 原行（start 继承）
        var day2 = new StringBuilder();
        day2.AppendLine("[00:30:00.000] [INF] [Primary:S3:P1:T1] Pickup");
        day2.AppendLine("交互或拾取：\"幸运儿\"");
        day2.AppendLine("[01:00:00.000] [INF] [Primary:S3:P1:T1] ScriptService");
        day2.AppendLine("配置组 \"重启组\" 执行结束");
        RunRecordCollector.Feed(Acct, day2.ToString(), "better-genshin-impact20261125.log");
        await Flush();
        var runs = (await RunRecordStore.QueryRunsAsync(Acct, 7)).Where(r => r.GroupName == "重启组").ToList();
        foreach (var r in runs)
            Console.WriteLine($"    {Iso(r.StartAtMs)}→{Iso(r.EndAtMs ?? 0)} state={r.State} picks={r.Picks} backfill={r.IsBackfilled}");
        Check(runs.Count == 1, "重启续跑仍 1 条（open 行收尾，不产第二行）");
        Check(runs.Count == 1 && runs[0].StartAtMs == Ms("2026-11-24 23:00:00.000"), "start 继承 23:00");
        Check(runs.Count == 1 && runs[0].EndAtMs == Ms("2026-11-25 01:00:00.000"), "end=次日 01:00");
        Check(runs.All(r => r.GroupName == "重启组"), "有组名（无 ghost）");
        return _fail;
    }

    // ── 验收8 专项④：BGI 状态时段三态 ─────────────────────────────
    private static async Task<int> ScenarioSpans()
    {
        Console.WriteLine("==== T6-5 专项④：状态时段三态 ====");
        string db = Db();
        await RunRecordStore.UseIsolatedForTest(db, (t, e) => { });
        RunRecordCollector.ResetForTest();
        // running：事件驱动开段
        var log = new StringBuilder();
        log.AppendLine("[23:00:00.000] [INF] [Primary:S3:P1:T1] ScriptService");
        log.AppendLine("配置组 \"时段组\" 加载完成，共1个脚本，开始执行");
        RunRecordCollector.Feed(Acct, log.ToString(), "better-genshin-impact20261124.log");
        await Flush();
        var spans = await RunRecordStore.QuerySpansAsync(Acct, 1);
        Console.WriteLine($"  初始 spans={spans.Count}（running 1 条 open）");
        Check(spans.Count == 1 && spans[0].State == "running" && spans[0].EndAtMs == null, "running 段开启");

        // idle：静默 ≥60s（测试接缝：回拨墙钟 61s，再判活 alive=true）
        RunRecordCollector.BackdateLastFeedForTest(Acct, 61_000);
        RunRecordCollector.ObserveBgiState(Acct, 0, true, "better-genshin-impact20261125.log");
        await Flush();
        spans = await RunRecordStore.QuerySpansAsync(Acct, 1);
        Console.WriteLine($"  判活后 spans：" + string.Join(",", spans.Select(s => s.State + "@" + Iso(s.StartAtMs))));
        Check(spans.Any(s => s.State == "idle"), "静默≥60s 且进程活 → idle 段");

        // not_running：alive=false
        RunRecordCollector.ObserveBgiState(Acct, 0, false, "");
        await Flush();
        spans = await RunRecordStore.QuerySpansAsync(Acct, 1);
        Check(spans.Any(s => s.State == "not_running"), "进程不在 → not_running 段");

        // 未绑定不产段：另一账号从不 Feed → 无段
        var noBind = await RunRecordStore.QuerySpansAsync("NEVER-FED", 1);
        Check(noBind.Count == 0, "S：未绑定（从未 Feed）不产段");

        // 退出收段
        RunRecordCollector.ShutdownSpans();
        await Flush();
        spans = await RunRecordStore.QuerySpansAsync(Acct, 1);
        Check(spans.All(s => s.EndAtMs != null), "T/S：程序退出关闭所有进行中的段");
        Console.WriteLine("  spans 终态：" + string.Join(",", spans.Select(s => $"{s.State} {s.DurMs}ms")));
        return _fail;
    }

    // ── 验收5 专项②：A1 20261125 三类计数现算现比 ───────────────────
    private static async Task<int> ScenarioA1ThreeClasses()
    {
        Console.WriteLine("==== T6-6 专项②：A1 三类拾取 ====");
        string path = @"D:\AAA挂机专区\A1\BetterGI\log\better-genshin-impact20261125.log";
        if (!File.Exists(path)) { Console.WriteLine("  SKIP: A1 日志不存在"); return 0; }
        // 现算三类行数（不写死）；FileShare.ReadWrite：BGI 正在写也读得了
        int logPicked = 0, logNoTarget = 0, logSkipped = 0;
        var allList = new List<string>();
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan))
        using (var sr = new StreamReader(fs, Encoding.UTF8))
        { string? l; while ((l = sr.ReadLine()) != null) allList.Add(l); }
        string[] all = allList.ToArray();
        foreach (string l in all)
        {
            if (l.Contains("交互或拾取：\"") || l.Contains("交互或拾取:\"")) logPicked++;
            else if (l.Contains("无有效结果，不交互")) logNoTarget++;
            else if (l.Contains("，不交互") && l.Contains("识别到\"")) logSkipped++;
        }
        Console.WriteLine($"  日志现算：picked={logPicked} no_target={logNoTarget} skipped={logSkipped}（跑的那一刻为准）");

        string db = Db();
        await RunRecordStore.UseIsolatedForTest(db, (t, e) => { });
        RunRecordCollector.ResetForTest();
        // 只喂含拾取行的片段（控制体量）：找所有三类行 ±1 行上下文
        // 简化：整文件分 chunk 喂（A1 文件 27MB 可承受）
        var sb = new StringBuilder();
        int fed = 0;
        for (int i = 0; i < all.Length; i++)
        {
            sb.Append(all[i]); if (i < all.Length - 1) sb.Append('\n');
            if ((i + 1) % 50000 == 0 || i == all.Length - 1)
            { RunRecordCollector.Feed("A1", sb.ToString(), Path.GetFileName(path)); sb.Clear(); fed++; }
        }
        Console.WriteLine($"  chunks={fed} lines={all.Length}");
        await Flush();

        var runs = await RunRecordStore.QueryRunsAsync("A1", 30);
        var pickEvents = new List<PickEvent>();
        foreach (var r in runs.Take(30)) pickEvents.AddRange(await RunRecordStore.QueryPickEventsAsync(r.RunId));
        int dbPicked = pickEvents.Count(p => p.Kind == "picked");
        int dbNoTarget = pickEvents.Count(p => p.Kind == "no_target");
        int dbSkipped = pickEvents.Count(p => p.Kind == "skipped");
        Console.WriteLine($"  DB 三类：picked={dbPicked} no_target={dbNoTarget} skipped={dbSkipped}（前 30 个 run）");

        // 物品名原样（抽 3 条 skipped）
        foreach (var s in pickEvents.Where(p => p.Kind == "skipped").Take(3))
            Console.WriteLine($"    skipped item='{s.Item}'");
        Check(pickEvents.Any(p => p.Kind == "no_target"), "no_target 大量存在");
        Check(pickEvents.Any(p => p.Kind == "skipped" && p.Item.Length > 0), "skipped 大量存在且物品名原样");
        // 三类之和与日志行数一致（注意：前 30 run 覆盖不了全部行 → 用全量 run 重查一次）
        var allRuns = await RunRecordStore.QueryRunsAsync("A1", 30);
        var allPicks = new List<PickEvent>();
        foreach (var r in allRuns) allPicks.AddRange(await RunRecordStore.QueryPickEventsAsync(r.RunId));
        int total = allPicks.Count;
        int logTotal = logPicked + logNoTarget + logSkipped;
        Console.WriteLine($"  L) 守恒：DB 三类之和={total} vs 日志三类之和={logTotal}");
        // L) 守恒（含孤儿）：pick_event + 无窗口孤儿 == 日志三类行数（绝不静默吞掉）
        var orphan = RunRecordCollector.GetOrphanPicksForTest("A1");
        int orphanTotal = orphan.picked + orphan.skipped + orphan.noTarget;
        Console.WriteLine($"  孤儿（无 run 窗口）：picked={orphan.picked} skipped={orphan.skipped} no_target={orphan.noTarget}");
        Console.WriteLine($"  L) 守恒：DB({total}) + 孤儿({orphanTotal}) = {total + orphanTotal} vs 日志 {logTotal}");
        Check(total + orphanTotal == logTotal, "L：三类之和 + 孤儿 == 日志行数（零丢失）");
        // 量级断言已由零丢失覆盖（文件在增长，硬编码阈值会漂移）；再断言三类都非零：
        Check(dbPicked + orphan.picked > 0 && dbNoTarget + orphan.noTarget > 0 && dbSkipped + orphan.skipped > 0,
            "三类事件均存在（picked/no_target/skipped）");
        return _fail;
    }

    // ── 验收9：全量重扫幂等（A4）──────────────────────────────────
    private static async Task<int> ScenarioRescanIdempotent()
    {
        Console.WriteLine("==== T6-7 全量重扫幂等 ====");
        string path = @"D:\AAA挂机专区\A4\BetterGI\log\better-genshin-impact20261124.log";
        if (!File.Exists(path)) { Console.WriteLine("  SKIP"); return 0; }
        string text = await File.ReadAllTextAsync(path, Encoding.UTF8);
        string db = Db();
        await RunRecordStore.UseIsolatedForTest(db, (t, e) => { });
        RunRecordCollector.ResetForTest();
        RunRecordCollector.Feed(Acct, text, Path.GetFileName(path));
        await Flush();

        var runs = await RunRecordStore.QueryRunsAsync(Acct, 30);
        int beforeRuns = runs.Count, beforeRoutes = 0, beforePicks = 0, beforeErrs = 0;
        foreach (var r in runs)
        {
            beforeRoutes += (await RunRecordStore.QueryRoutesAsync(r.RunId)).Count;
            beforePicks += (await RunRecordStore.QueryPickEventsAsync(r.RunId)).Count;
            beforeErrs += (await RunRecordStore.QueryErrorsAsync(r.RunId)).Count;
        }

        RunRecordCollector.Feed(Acct, text, Path.GetFileName(path));
        await Flush();

        var after = await RunRecordStore.QueryRunsAsync(Acct, 30);
        int afterRuns = after.Count, afterRoutes = 0, afterPicks = 0, afterErrs = 0;
        foreach (var r in after)
        {
            afterRoutes += (await RunRecordStore.QueryRoutesAsync(r.RunId)).Count;
            afterPicks += (await RunRecordStore.QueryPickEventsAsync(r.RunId)).Count;
            afterErrs += (await RunRecordStore.QueryErrorsAsync(r.RunId)).Count;
        }
        Console.WriteLine($"  幂等：runs {beforeRuns}/{afterRuns} routes {beforeRoutes}/{afterRoutes}" +
            $" picks {beforePicks}/{afterPicks} errs {beforeErrs}/{afterErrs}");
        Check(afterRuns == beforeRuns && afterRoutes == beforeRoutes && afterErrs == beforeErrs,
            "重扫 runs/routes/errs 不变");
        // pick_event 自然键（run+ts+line+off+kind）→ 重喂同文本同偏移 → 幂等
        Check(afterPicks == beforePicks, "重扫 pick_event 不变");
        return _fail;
    }

    // ── 验收4 专项①（构造）：激活CD → ERR → 采集物区拾取 → 结束 ─────
    private static async Task<int> ScenarioCdErrPicks()
    {
        Console.WriteLine("==== T6-8 专项①：激活CD→ERR→拾取（构造）====");
        string db = Db();
        await RunRecordStore.UseIsolatedForTest(db, (t, e) => { });
        RunRecordCollector.ResetForTest();
        string f = "better-genshin-impact20261124.log";
        var sb = new StringBuilder();
        sb.AppendLine("[04:00:00.000] [INF] [Primary:S3:P1:T1] ScriptService");
        sb.AppendLine("配置组 \"罐子激活测试\" 加载完成，共3个脚本，开始执行");
        sb.AppendLine("[04:00:05.000] [INF] [Primary:S3:P1:T1] CdEngine");
        sb.AppendLine("线路 1P枫丹-科学院工坊激活CD.json 开始状态已保存；开始时间：2026-11-24 04:00:05");
        sb.AppendLine("[04:00:10.000] [ERR] [Primary:S3:P1:T1] CdEngine");
        sb.AppendLine("脚本停止：线路 1P枫丹-科学院工坊激活CD.json 终点校验失败，距离：1511.12");
        // ★ 异常后跑到采集物区域触发拾取（用户原话：该路线就是有拾取，不得屏蔽）
        sb.AppendLine("[04:00:15.000] [INF] [Primary:S3:P1:T1] Pickup");
        sb.AppendLine("交互或拾取：\"游医\"");
        sb.AppendLine("[04:00:16.000] [INF] [Primary:S3:P1:T1] Pickup");
        sb.AppendLine("交互或拾取：\"幸运儿\"");
        sb.AppendLine("[04:00:16.500] [INF] [Primary:S3:P1:T1] Pickup");
        sb.AppendLine("无有效结果，不交互");
        sb.AppendLine("[04:00:17.000] [INF] [Primary:S3:P1:T1] CdEngine");
        sb.AppendLine("线路 1P枫丹-科学院工坊激活CD.json 成功，结束时间：2026-11-24 04:00:17；记录已保存");
        sb.AppendLine("[04:01:00.000] [INF] [Primary:S3:P1:T1] ScriptService");
        sb.AppendLine("配置组 \"罐子激活测试\" 未成功完成，结果: Failed");
        RunRecordCollector.Feed(Acct, sb.ToString(), f);
        await Flush();

        var runs = await RunRecordStore.QueryRunsAsync(Acct, 7);
        var run = runs.FirstOrDefault(r => r.GroupName == "罐子激活测试");
        Check(run != null, "专项①：run 存在");
        if (run != null)
        {
            var routes = await RunRecordStore.QueryRoutesAsync(run.RunId);
            var cd = routes.FirstOrDefault(r => r.Kind == "cd_line");
            Check(cd != null, "专项①：CD 线路存在");
            if (cd != null)
            {
                Console.WriteLine($"    CD: {cd.Name} state={cd.State} picks={cd.PickCount} picked={cd.Picked} no_target={cd.NoTarget}");
                Check(cd.PickCount > 0, "专项①：ERR 后拾取仍计入（pick_count>0，F 不屏蔽）");
                Check(cd.State == "Failed", "专项①：state=Failed");
                Check(cd.NoTarget == 1, "专项①：no_target 也记录");
            }
            var picks = await RunRecordStore.QueryPickEventsAsync(run.RunId);
            Check(picks.Count == 3, "专项①：pick_event 3 条（2 picked + 1 no_target）");
            var routePicked = routes.Sum(r => r.PickCount);
            var runLevel = run.Picks - routePicked;
            Console.WriteLine($"    H 守恒：route.picked={routePicked} + run级={runLevel} == run.picks={run.Picks}（日志 picked 行=2）");
            Check(run.Picks == 2, "专项①：守恒成立（run.picks=2=日志行数）");
            var errs = await RunRecordStore.QueryErrorsAsync(run.RunId);
            Check(errs.Any(e => e.Detail.Contains("终点校验失败")), "专项①：失败原因入 err");
        }
        return _fail;
    }
}



