using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SoulLink.Services;

internal static class T10Scenarios
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
        await ScenarioThreeStates();      // 验收2：三态切换（含回正常）
        await ScenarioCrossDayIdle();     // 验收3：跨天 idle 1 条 + dur 自动
        await ScenarioRestartContinue();  // 验收4：重启沿用进行中段
        await ScenarioDurSoFarAndCheck(); // 进行中段已持续时长 + 一致性自检
        return _fail;
    }

    private static string Db()
        => Path.Combine(Path.GetTempPath(), "t10-verify-" + Guid.NewGuid().ToString("N")[..8] + ".db");


    // ── 验收2：三态切换（含回到正常）────────────────────────────
    private static async Task<int> ScenarioThreeStates()
    {
        Console.WriteLine("==== T10-1 三态切换 ====");
        string db = Db();
        await RunRecordStore.UseIsolatedForTest(db, (t, ex) => { if (ex != null) Console.WriteLine("[diag] " + t + ": " + ex.Message); });
        RunRecordCollector.ResetForTest();
        // running：日志事件
        var log = new StringBuilder();
        log.AppendLine("[23:00:00.000] [INF] [Primary:S3:P1:T1] ScriptService");
        log.AppendLine("配置组 \"时段组A\" 加载完成，共1个脚本，开始执行");
        RunRecordCollector.Feed(Acct, log.ToString(), "better-genshin-impact20261124.log");
        await RunRecordStore.FlushAsync();
        var spans = await RunRecordStore.QuerySpansAsync(Acct, 1);
        Check(spans.Count(s => s.State == "running") == 1, "① 有日志产出 → running 开段");

        // idle：静默 60s + 进程活
        RunRecordCollector.BackdateLastFeedForTest(Acct, 61_000);
        RunRecordCollector.ObserveBgiState(Acct, 0, true, "better-genshin-impact20261124.log");
        await RunRecordStore.FlushAsync();
        spans = await RunRecordStore.QuerySpansAsync(Acct, 1);
        Check(spans.Any(s => s.State == "idle" && s.EndAtMs == null), "② 静默60s+进程活 → idle 段开启");
        Check(spans.First(s => s.State == "running").EndAtMs != null, "running 段已闭（切换才写库）");

        // not_running：进程退出
        RunRecordCollector.ObserveBgiState(Acct, 0, false, "");
        await RunRecordStore.FlushAsync();
        spans = await RunRecordStore.QuerySpansAsync(Acct, 1);
        Check(spans.Any(s => s.State == "not_running"), "③ 进程退出 → not_running");

        // 回到正常：BGI 重新写日志
        var log2 = new StringBuilder();
        log2.AppendLine("[23:30:00.000] [INF] [Primary:S3:P1:T1] ScriptService");
        log2.AppendLine("交互或拾取：\"游医\"");
        RunRecordCollector.Feed(Acct, log2.ToString(), "better-genshin-impact20261124.log");
        await RunRecordStore.FlushAsync();
        spans = await RunRecordStore.QuerySpansAsync(Acct, 1);
        Check(spans.Count(s => s.State == "running") >= 1, "④ 重新写日志 → 回 running");
        Console.WriteLine("  段序列：" + string.Join(" → ", spans.OrderBy(s => s.StartAtMs).Select(s => s.State)));
        // 只在切换时写库：4 次状态 = 4 段（不是每秒一条）
        Check(spans.Count == 4, "切换才写库：恰 4 段（running/idle/not_running/running）");
        return _fail;
    }

    // ── 验收3：跨天 idle 1 条 + dur 自动 ────────────────────────
    private static async Task<int> ScenarioCrossDayIdle()
    {
        Console.WriteLine("==== T10-2 跨天 idle ====");
        string db = Db();
        await RunRecordStore.UseIsolatedForTest(db, (t, e) => { });
        RunRecordCollector.ResetForTest();
        // 构造：23:00 开 idle 段（直接用 store，状态机时序由 T10-1 验证）
        long day1 = 1795478400000L;   // 2026-11-24 00:00 本地
        long start = day1 + 23 * 3600_000;      // 23:00
        long end = day1 + 86_400_000 + 3600_000; // 次日 01:00
        RunRecordStore.AddStateSpan(new StateSpan
        {
            Account = Acct, State = "idle", StartAtMs = start,
            SrcFile = "better-genshin-impact20261124.log",
        });
        await RunRecordStore.FlushAsync();
        RunRecordStore.CloseStateSpan(Acct, end);
        await RunRecordStore.FlushAsync();
        var spans = await RunRecordStore.QuerySpansAsync(Acct, 1);
        var idle = spans.FirstOrDefault(s => s.State == "idle");
        Check(idle != null, "跨天 idle 段存在");
        if (idle != null)
        {
            Console.WriteLine($"  idle: {Iso(idle.StartAtMs)}→{Iso(idle.EndAtMs ?? 0)} dur={idle.DurMs}ms");
            Check(idle.DurMs == 7_200_000, "dur_ms=7200000 自动算（2h，不是 NULL 不是 0）");
            Check(idle.EndAtMs > idle.StartAtMs && DayOf(idle.StartAtMs) != DayOf(idle.EndAtMs ?? 0L),
                "跨天：end 在次日");
        }
        return _fail;
    }

    private static string DayOf(long ms)
        => new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMilliseconds(ms).ToString("yyyy-MM-dd");
    private static string Iso(long ms)
        => new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMilliseconds(ms).ToString("yyyy-MM-dd HH:mm:ss");

    // ── 验收4：重启沿用进行中段 ─────────────────────────────────
    private static async Task<int> ScenarioRestartContinue()
    {
        Console.WriteLine("==== T10-3 重启沿用 ====");
        string db = Db();
        await RunRecordStore.UseIsolatedForTest(db, (t, e) => { });
        RunRecordCollector.ResetForTest();
        // 开一段 idle（进行中）
        var log = new StringBuilder();
        log.AppendLine("[22:00:00.000] [INF] [Primary:S3:P1:T1] ScriptService");
        log.AppendLine("配置组 \"时段组B\" 加载完成，共1个脚本，开始执行");
        RunRecordCollector.Feed(Acct, log.ToString(), "better-genshin-impact20261124.log");
        RunRecordCollector.BackdateLastFeedForTest(Acct, 61_000);
        RunRecordCollector.ObserveBgiState(Acct, 0, true, "better-genshin-impact20261124.log");
        await RunRecordStore.FlushAsync();
        var before = await RunRecordStore.QuerySpansAsync(Acct, 1);
        var idleBefore = before.FirstOrDefault(s => s.State == "idle");
        Check(idleBefore != null && idleBefore.IsOngoing, "重启前：进行中 idle 段");

        // 模拟程序重启：内存清空 + EnsureSpanBootAsync 沿用
        RunRecordCollector.ResetForTest();
        await RunRecordCollector.EnsureSpanBootAsync();
        await RunRecordStore.FlushAsync();
        // 重启后再次静默判活（不再开新段，沿用旧段）
        RunRecordCollector.ObserveBgiState(Acct, 0, true, "");
        await RunRecordStore.FlushAsync();
        var after = await RunRecordStore.QuerySpansAsync(Acct, 1);
        var idleAfter = after.Where(s => s.State == "idle").ToList();
        Console.WriteLine($"  重启后 idle 段数={idleAfter.Count}（期望 1：沿用不新开）");
        Check(idleAfter.Count == 1 && idleAfter[0].SpanId == idleBefore?.SpanId,
            "重启沿用同一段（SpanId 相同，不凭空开新段）");
        return _fail;
    }

    // ── 进行中段已持续时长 + 一致性自检 ─────────────────────────
    private static async Task<int> ScenarioDurSoFarAndCheck()
    {
        Console.WriteLine("==== T10-4 DurMsSoFar + 一致性自检 ====");
        string db = Db();
        await RunRecordStore.UseIsolatedForTest(db, (t, e) => { });
        RunRecordCollector.ResetForTest();
        // 开一段进行中 idle
        RunRecordStore.AddStateSpan(new StateSpan
        {
            Account = Acct, State = "idle",
            StartAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 5000,   // 5 秒前开始
            SrcFile = "x.log",
        });
        await RunRecordStore.FlushAsync();
        var spans = await RunRecordStore.QuerySpansAsync(Acct, 1);
        var ongoing = spans.FirstOrDefault(s => s.IsOngoing);
        Check(ongoing != null, "进行中段存在");
        if (ongoing != null)
        {
            Console.WriteLine($"  进行中段：DurMs={ongoing.DurMs} DurMsSoFar={ongoing.DurMsSoFar}");
            Check(ongoing.DurMsSoFar >= 5000 && ongoing.DurMsSoFar < 60_000,
                "DurMsSoFar≈已持续 5s（自动算，调用方零减法）");
        }
        // 一致性自检：0 条不一致
        var bad = await RunRecordStore.QueryDurInconsistenciesAsync();
        Console.WriteLine($"  dur 一致性：{bad.Count} 条不一致" + (bad.Count > 0 ? " → " + string.Join(";", bad.Take(3)) : ""));
        Check(bad.Count == 0, "run/route/state_span 三表 dur_ms == end-start（0 不一致）");
        return _fail;
    }
}

