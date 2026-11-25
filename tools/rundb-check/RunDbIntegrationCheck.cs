using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using SoulLink;
using SoulLink.Models;
using SoulLink.Services;

/// <summary>
/// 集成验收（t4）：RunRecordHost（启动重建 / 留存清理）+ BgiLogCenter 挂载点 + 只读查询落库结果。
///
/// ★ 只用产品代码（ProjectReference SoulLink.csproj），不复制实现、不做桩替换。
/// ★ 隔离：SOULLINK_DATA_DIR / SOULLINK_CONFIG 由调用方指向 %TEMP% 沙箱；
///   账号绑定的 BGI 路径也指向 %TEMP% 假目录（log 下放真实日志内容的副本）。
///
/// 用法：
///   RunDbCheck.exe hostonly
///   RunDbCheck.exe feed  &lt;账号&gt; &lt;假BGI.exe&gt; [expectPicks]
///   RunDbCheck.exe purge &lt;账号&gt; &lt;假BGI.exe&gt;
/// </summary>
public static class RunDbIntegrationCheck
{
    static int fails;
    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + what);
        if (!ok) fails++;
    }

    static void UseConfig(string user, int retention)
    {
        var cfg = new AppConfig();
        cfg.Settings.RunDbRetentionDays = retention;
        if (!string.IsNullOrEmpty(user)) cfg.Accounts.Add(new Account { Id = "acc-" + user, UserName = user });
        typeof(App).GetProperty("Config", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, cfg);
    }

    public static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "hostonly";
        try
        {
            if (mode == "hostonly") return HostOnly();
            if (mode == "feed") return Feed(args[1], args[2],
                args.Length > 3 && args[3].Equals("expectPicks", StringComparison.OrdinalIgnoreCase));
            if (mode == "purge") return Purge(args[1], args[2]);
            if (mode == "web") return Web(args[1], args[2], int.Parse(args[3]), args[4]);
            Console.WriteLine("unknown mode: " + mode);
            return 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine("EXCEPTION: " + ex);
            return 3;
        }
    }

    // ── ① 宿主启动 + 配置归一 ───────────────────────────────────────
    static int HostOnly()
    {
        UseConfig("A4", 7);
        Console.WriteLine("==== 配置归一（RunRecordHost.NormalizeRetentionDays）====");
        Check(RunRecordHost.NormalizeRetentionDays(7) == 7, "留存 7 原样");
        Check(RunRecordHost.NormalizeRetentionDays(0) == 7, "旧配置缺字段/0 → 默认 7");
        Check(RunRecordHost.NormalizeRetentionDays(-5) == 7, "留存负数 → 默认 7");
        Check(RunRecordHost.NormalizeRetentionDays(365) == 365, "留存 365 保留");
        Check(RunRecordHost.NormalizeRetentionDays(9999) == 365, "留存 >365 → 365");

        Console.WriteLine("==== 宿主启动（幂等）====");
        RunRecordHost.Start();
        RunRecordHost.Start();
        Check(RunRecordHost.StartCount == 1, "RunRecordHost.Start 幂等（StartCount=1）");
        Check(RunRecordHost.RetentionDays == 7, "RetentionDays 读到配置 7");

        // 缺失字段的老配置行为：默认值 7（属性初始值，JsonSerializer 缺失即保留）
        var json = "{\"Version\":1,\"Settings\":{}}";
        var parsed = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(json);
        Check(parsed!.Settings.RunDbRetentionDays == 7, "反序列化旧配置（无该字段）→ 默认 7");

        Thread.Sleep(1500);
        Console.WriteLine("purge@start rows=" + RunRecordHost.LastPurgedRows
            + " at=" + RunRecordHost.LastPurgeAt.ToString("HH:mm:ss") + " healthy=" + RunRecordStore.IsHealthy);
        Console.WriteLine("db=" + RunRecordStore.DatabasePath);
        Check(RunRecordStore.IsHealthy, "数据库健康（建库成功）");
        RunRecordHost.Shutdown();
        Console.WriteLine(fails == 0 ? "ALL PASSED" : "FAILURES=" + fails);
        return fails;
    }

    // ── ② 走唯一日志入口驱动采集，然后回查库 ─────────────────────────
    static int Feed(string user, string fakeExe, bool expectPicks)
    {
        UseConfig(user, 7);
        RunRecordHost.Start();
        Thread.Sleep(800);

        Console.WriteLine("==== 绑定与唯一入口 ====");
        BgiPathResolver.Pin(user, fakeExe);
        Check(BgiPathResolver.IsPinned(user), "账号 " + user + " 已绑定（IsPinned=true）");
        string todayLog = BgiLogWatcher.FindTodayLog(fakeExe);
        Check(todayLog.Length > 0, "FindTodayLog 命中今天日志: " + todayLog);

        var accounts = new List<Account> { App.Config.Accounts[0] };
        for (int i = 0; i < 12; i++) { BgiLogCenter.Tick(accounts); Thread.Sleep(250); }
        RunRecordStore.FlushAsync().Wait(10000);

        Console.WriteLine("==== 查库（RunRecordStore 查询门面）====");
        var runs = RunRecordStore.QueryRunsAsync(user, 7).Result;
        Console.WriteLine("runs=" + runs.Count);
        foreach (var r in runs)
            Console.WriteLine("  RUN group=" + r.GroupName + " start=" + Ms(r.StartAtMs)
                + " end=" + (r.EndAtMs.HasValue ? Ms(r.EndAtMs.Value) : "-")
                + " state=" + r.State + " scripts=" + r.ScriptCount + " picks=" + r.Picks
                + " exp=" + r.Exp + " mora=" + r.Mora + " cross=" + r.IsCrossDay
                + " backfill=" + r.IsBackfilled + " durSec=" + (r.DurMs / 1000));
        Check(runs.Count > 0, "挂载点产出运行记录（run 行 > 0）");

        int routes = 0, withPicks = 0, errs = 0, totalPicks = 0;
        foreach (var r in runs)
        {
            foreach (var rt in RunRecordStore.QueryRoutesAsync(r.RunId).Result)
            {
                routes++; totalPicks += rt.PickCount;
                if (rt.PickCount > 0) withPicks++;
                Console.WriteLine("  ROUTE kind=" + rt.Kind + " name=" + Trunc(rt.Name, 42)
                    + " state=" + rt.State + " picks=" + rt.PickCount
                    + " detail=" + Trunc(rt.PickDetail, 70)
                    + " parent=" + (string.IsNullOrEmpty(rt.ParentRouteId) ? "-" : "y")
                    + " durSec=" + (rt.DurMs / 1000));
            }
            foreach (var e in RunRecordStore.QueryErrorsAsync(r.RunId).Result)
            {
                errs++;
                Console.WriteLine("  ERR " + e.Level + " type=" + Trunc(e.ExceptionType, 40)
                    + " msg=" + Trunc(e.Message, 56) + " detailLen=" + e.Detail.Length);
            }
        }
        Console.WriteLine("routes=" + routes + " routesWithPicks=" + withPicks
            + " totalPicks=" + totalPicks + " errors=" + errs);
        Check(routes > 0, "路线/脚本明细落库（route 行 > 0）");
        Check(errs > 0, "报错详情落库（err 行 > 0）");
        if (expectPicks) Check(withPicks > 0, "逐路线拾取详情落库（pickCount>0 的路线存在）");

        Console.WriteLine("==== 重启重扫幂等 ====");
        int runsBefore = runs.Count, routesBefore = routes, errsBefore = errs;
        BgiLogCenter.Clear();
        for (int i = 0; i < 12; i++) { BgiLogCenter.Tick(accounts); Thread.Sleep(250); }
        RunRecordStore.FlushAsync().Wait(10000);
        int runsAfter = 0, routesAfter = 0, errsAfter = 0;
        foreach (var r in RunRecordStore.QueryRunsAsync(user, 7).Result)
        {
            runsAfter++;
            routesAfter += RunRecordStore.QueryRoutesAsync(r.RunId).Result.Count;
            errsAfter += RunRecordStore.QueryErrorsAsync(r.RunId).Result.Count;
        }
        Console.WriteLine("after rescan runs=" + runsAfter + " routes=" + routesAfter + " errs=" + errsAfter);
        Check(runsAfter == runsBefore, "重扫后 run 行数不变 (" + runsBefore + ")");
        Check(routesAfter == routesBefore, "重扫后 route 行数不变 (" + routesBefore + ")");
        Check(errsAfter == errsBefore, "重扫后 err 行数不变 (" + errsBefore + ")");

        RunRecordHost.Shutdown();
        Console.WriteLine(fails == 0 ? "ALL PASSED" : "FAILURES=" + fails);
        return fails;
    }

    // ── ③ 留存清理：造 7 天前与 7 天内的数据，跑一次 PurgeOlderThan ──
    static int Purge(string user, string fakeExe)
    {
        UseConfig(user, 7);
        RunRecordHost.Start();

        // 等启动清理跑完，再播种 —— 否则「启动清理」和「显式清理」会抢同一批行，
        // 删除数被前一次吃掉（这是测试竞态，不是产品缺陷）。
        var waitStart = DateTime.UtcNow;
        while (RunRecordHost.LastPurgeAt == DateTime.MinValue && DateTime.UtcNow - waitStart < TimeSpan.FromSeconds(15))
            Thread.Sleep(100);
        Console.WriteLine("启动清理已完成: rows=" + RunRecordHost.LastPurgedRows
            + " at=" + RunRecordHost.LastPurgeAt.ToString("HH:mm:ss"));
        RunRecordStore.FlushAsync().Wait(10000);

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long day = 86_400_000L;

        // 有意造三组：8 天前（该删）、6 天前（该留）、10 天前的陈旧 open 行（该删）
        Seed(user, "旧·八天前", now - 8 * day, now - 8 * day + 3_000_000L, "Finished");
        Seed(user, "新·六天前", now - 6 * day, now - 6 * day + 3_000_000L, "Finished");
        Seed(user, "陈旧·十天前未结束", now - 10 * day, null, "Running");
        RunRecordStore.FlushAsync().Wait(10000);

        var before = RunRecordStore.QueryRunsAsync(user, 365).Result;
        Console.WriteLine("清理前 run 行=" + before.Count);
        foreach (var r in before)
            Console.WriteLine("  " + r.GroupName + " end=" + (r.EndAtMs.HasValue ? Ms(r.EndAtMs.Value) : "open")
                + " state=" + r.State);

        int deleted = RunRecordHost.PurgeOnceAsync("验收").GetAwaiter().GetResult();
        RunRecordStore.FlushAsync().Wait(10000);
        var after = RunRecordStore.QueryRunsAsync(user, 365).Result;
        Console.WriteLine("PurgeOlderThanAsync(7) 删除行=" + deleted
            + "  LastPurgedRows=" + RunRecordHost.LastPurgedRows);
        Console.WriteLine("清理后 run 行=" + after.Count);
        foreach (var r in after)
            Console.WriteLine("  " + r.GroupName + " end=" + (r.EndAtMs.HasValue ? Ms(r.EndAtMs.Value) : "open")
                + " state=" + r.State);

        Check(deleted >= 2, "8 天前 + 10 天前 open 行被删除（deleted>=2，实际 " + deleted + "）");
        bool kept6 = false, removed8 = true, removedOpen = true;
        foreach (var r in after)
        {
            if (r.GroupName == "新·六天前") kept6 = true;
            if (r.GroupName == "旧·八天前") removed8 = false;
            if (r.GroupName == "陈旧·十天前未结束") removedOpen = false;
        }
        Check(kept6, "6 天前的记录保留（在留存期内）");
        Check(removed8, "8 天前的记录已删除（超出 7 天留存）");
        Check(removedOpen, "10 天前的陈旧 open 行已删除");

        // 幂等：再跑一次不应再删任何东西
        int again = RunRecordHost.PurgeOnceAsync("验收-幂等").GetAwaiter().GetResult();
        Console.WriteLine("再跑一次删除行=" + again);
        Check(again == 0, "清理幂等（第二次删除 0 行）");

        RunRecordHost.Shutdown();
        Console.WriteLine(fails == 0 ? "ALL PASSED" : "FAILURES=" + fails);
        return fails;
    }

    // ── ④ 起网页服务，供 curl / Invoke-WebRequest 实测只读 API ─────────
    static int Web(string user, string fakeExe, int port, string password)
    {
        UseConfig(user, 7);
        App.Config.Settings.WebPort = port;
        App.Config.Settings.WebPassword = password;      // 特意设口令：验证新接口确实走鉴权
        App.Config.Settings.UseBrokerChannel = false;

        RunRecordHost.Start();
        Thread.Sleep(800);
        BgiPathResolver.Pin(user, fakeExe);

        var accounts = new List<Account> { App.Config.Accounts[0] };
        for (int i = 0; i < 12; i++) { BgiLogCenter.Tick(accounts); Thread.Sleep(250); }
        RunRecordStore.FlushAsync().Wait(10000);

        int runs = RunRecordStore.QueryRunsAsync(user, 7).Result.Count;
        Console.WriteLine("seeded runs=" + runs + " user=" + user);

        WebServer.Apply(port);
        Console.WriteLine("webPort=" + WebServer.RunningPort + " lan=" + WebServer.LanUrl(port));
        if (WebServer.RunningPort != port) { Console.WriteLine("FAIL 网页服务没起来"); return 1; }

        Console.WriteLine("READY");     // 外部脚本看到这行就开始请求
        Thread.Sleep(TimeSpan.FromSeconds(150));
        WebServer.Stop();
        RunRecordHost.Shutdown();
        Console.WriteLine("done");
        return 0;
    }

    static void Seed(string account, string group, long startMs, long? endMs, string state)
    {
        string runId = RunRecordStore.MakeRunId(account, group, startMs);
        RunRecordStore.AddRun(new RunRecord
        {
            RunId = runId, Account = account, GroupName = group, StartAtMs = startMs,
            EndAtMs = endMs, State = state, DurMs = endMs.HasValue ? endMs.Value - startMs : 0,
            SrcFileStart = "seed.log",
        });
        string routeId = RunRecordStore.MakeRouteId(runId, "script", "种子脚本", startMs);
        RunRecordStore.AddRoute(new RouteRecord
        {
            RouteId = routeId, RunId = runId, Kind = "script", Name = "种子脚本",
            StartAtMs = startMs, EndAtMs = endMs, State = state, PickCount = 3,
            PickDetail = "{\"种子\":3}", SrcFile = "seed.log",
        });
        RunRecordStore.AddError(new ErrorRecord
        {
            RunId = runId, RouteId = routeId, TsMs = startMs + 1000, Level = "ERR",
            ExceptionType = "SeedException", Message = "种子报错", Detail = "seed stack",
            Source = "Seed", SrcFile = "seed.log",
        });
    }

    static string Ms(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("MM-dd HH:mm:ss.fff");
    static string Trunc(string s, int n) { s = s ?? ""; return s.Length <= n ? s : s.Substring(0, n) + "…"; }
}
