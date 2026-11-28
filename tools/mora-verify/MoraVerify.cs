using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SoulLink.Models;
using SoulLink.Services;
using SoulLink.Views;

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

        if (args.Contains("--daily") || args.Contains("--route-income") || args.Contains("--overview") || args.Contains("--resume"))
        {
            if (!string.Equals(AppIdentity.DataDir, dbDir, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("测试库未隔离");
            if (args.Contains("--daily")) return ScenarioDaily().GetAwaiter().GetResult();
            if (args.Contains("--overview")) return ScenarioOverview().GetAwaiter().GetResult();
            if (args.Contains("--resume")) return ScenarioLedgerResume(dbDir).GetAwaiter().GetResult();
            return ScenarioDelayedRouteIncome(dbDir).GetAwaiter().GetResult();
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

    private static async Task<int> ScenarioLedgerResume(string dbDir)
    {
        const string uid = "uid-resume";
        var month = new DateTime(2026, 11, 1);
        var since = new DateTime(2026, 11, 24, 4, 0, 0);
        var credential = new MiyousheCredential { AccountId = "test", DeviceId = "fixture", CookieToken = "fixture" };

        MiyousheLedgerScope Scope(string account) => new(account, uid, "cn_fixture", "2026-11", 2,
            new DateTimeOffset(since).ToUnixTimeMilliseconds(), new DateTimeOffset(since.AddDays(1)).ToUnixTimeMilliseconds());
        async Task<int> Pull(string account, ResumeLedgerHttp fixture, DateTime? start = null, List<int> progress = null)
        {
            using var http = new HttpClient(fixture);
            var client = new MiyousheClient("fixture", http);
            client.ConfigureTestDelay(0);
            int inserted = 0;
            await client.PullLedgerAsync(account, credential, new(account, uid, "cn_fixture", "", 60), month,
                diaryDay: null, (pages, count, _) =>
                {
                    progress?.Add(pages);
                    inserted = count;
                    return Task.CompletedTask;
                }, default, detailsSince: start ?? since, skipMonthInfo: true);
            return inserted;
        }
        async Task Interrupt(string account, ResumeLedgerHttp fixture)
        {
            try { await Pull(account, fixture); }
            catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException) { return; }
            throw new InvalidOperationException("模拟中断未触发");
        }
        void Reopen() => MiyousheStore.Shared = new MiyousheStore(() =>
            System.IO.Path.Combine(dbDir, "miyoushe", "miyoushe.db"));

        const string unchanged = "same-stream";
        await Interrupt(unchanged, new ResumeLedgerHttp { ErrorPage = 4 });
        var before = await MiyousheStore.Shared.QueryAsync(unchanged);
        Check((await MiyousheStore.Shared.LoadLedgerCursorAsync(Scope(unchanged), default))?.Page == 3 &&
            before.Entries.Count == 4, "中断后已提交的三页和断点均可从 SQLite 读取");
        Reopen();
        var resumed = new ResumeLedgerHttp();
        var progress = new List<int>();
        int added = await Pull(unchanged, resumed, progress: progress);
        var archive = await MiyousheStore.Shared.QueryAsync(unchanged);
        Check(added == 2 && resumed.Pages.SequenceEqual(new[] { 1, 3, 4, 5 }) &&
            progress.SequenceEqual(new[] { 1, 4, 5, 6 }),
            "新客户端与数据库连接只核对首页、断点页，再从下一页继续");
        Check(archive.Entries.Where(entry => entry.Num == 150).Select(entry => entry.SourceOrdinal)
            .Order().SequenceEqual(new[] { 0, 1 }), "同一秒的相同真流水跨页续传保留原始序号和两笔金额");
        Check(archive.Days.Single().Mora == 1540 &&
            await MiyousheStore.Shared.LoadLedgerCursorAsync(Scope(unchanged), default) == null,
            "完整日期的衍生汇总包含前缀和后缀，空页后删除断点");

        const string changedHead = "changed-head";
        await Interrupt(changedHead, new ResumeLedgerHttp { ErrorPage = 4 });
        Reopen();
        var headReplay = new ResumeLedgerHttp { ChangeHead = true };
        Check(await Pull(changedHead, headReplay) == 3 && headReplay.Pages.SequenceEqual(new[] { 1, 2, 3, 4, 5 }),
            "首页有新流水导致指纹改变时，从第一页安全重读并去重");

        const string changedTail = "changed-tail";
        await Interrupt(changedTail, new ResumeLedgerHttp { ErrorPage = 4 });
        Reopen();
        var tailReplay = new ResumeLedgerHttp { ChangeTail = true };
        Check(await Pull(changedTail, tailReplay) == 3 && tailReplay.Pages.SequenceEqual(new[] { 1, 3, 2, 3, 4, 5 }),
            "断点页指纹改变时重读原始前缀");

        const string changedScope = "changed-window";
        await Interrupt(changedScope, new ResumeLedgerHttp { ErrorPage = 4 });
        Reopen();
        var newWindow = new ResumeLedgerHttp();
        Check(await Pull(changedScope, newWindow, since.AddHours(1)) == 2 &&
            newWindow.Pages.SequenceEqual(new[] { 1, 2, 3, 4, 5 }), "业务窗口发生变化时独立重读");

        const string wrongOrder = "changed-order";
        await Interrupt(wrongOrder, new ResumeLedgerHttp { ErrorPage = 4 });
        Reopen();
        var disordered = new ResumeLedgerHttp { ChangeOrder = true };
        Check(await Pull(wrongOrder, disordered) == 2 &&
            disordered.Pages.SequenceEqual(new[] { 1, 3, 4, 1, 2, 3, 4, 5 }) &&
            await MiyousheStore.Shared.LoadLedgerCursorAsync(Scope(wrongOrder), default) == null,
            "续传后出现逆序时先丢弃断点并完整重读，逆序前缀不留下断点");

        const string large = "large-stream";
        await Interrupt(large, new ResumeLedgerHttp { LongStream = true });
        Check((await MiyousheStore.Shared.LoadLedgerCursorAsync(Scope(large), default))?.Page == 512,
            "单次分页上限保留第 512 页的安全断点");
        Reopen();
        var afterCap = new ResumeLedgerHttp { LongStream = true };
        Check(await Pull(large, afterCap) == 2 && afterCap.Pages.SequenceEqual(new[] { 1, 512, 513, 514, 515 }),
            "续传每轮只限制新增页数，可超过原分页上限");

        const string atomic = "atomic-stream";
        await MiyousheStore.Shared.RunAsync(db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"CREATE TRIGGER abort_cursor BEFORE INSERT ON my_ledger_cursor
                WHEN NEW.account='atomic-stream' BEGIN SELECT RAISE(ABORT,'fixture'); END;";
            return cmd.ExecuteNonQuery();
        }, 0);
        await Interrupt(atomic, new ResumeLedgerHttp());
        Check((await MiyousheStore.Shared.QueryAsync(atomic)).Entries.Count == 0 &&
            await MiyousheStore.Shared.LoadLedgerCursorAsync(Scope(atomic), default) == null,
            "断点事务写入失败时同一页流水也回滚");
        return _fail;
    }

    private sealed class ResumeLedgerHttp : HttpMessageHandler
    {
        private const string RepeatedReceipt = """{"time":"11/24 09:00:02","action_id":4,"action":"其他","num":150}""";
        internal int ErrorPage { get; init; }
        internal bool ChangeHead { get; init; }
        internal bool ChangeTail { get; init; }
        internal bool ChangeOrder { get; init; }
        internal bool LongStream { get; init; }
        internal List<int> Pages { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var args = request.RequestUri!.Query.TrimStart('?').Split('&')
                .Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => part[1]);
            if (args["type"] == "1") return Reply("");
            int page = int.Parse(args["page"]);
            Pages.Add(page);
            if (ErrorPage == page) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            if (LongStream)
            {
                string time = new DateTime(2026, 11, 24, 18, 0, 0).AddMinutes(-page).ToString("MM/dd HH:mm:ss");
                return Reply(page <= 514 ? Entry(time, 1) : "");
            }
            return Reply(page switch
            {
                1 => (ChangeHead ? Entry("11/24 13:00", 123) + "," : "") + Entry("11/24 12:00", 1000),
                2 => Entry("11/24 11:00", 90),
                3 => Entry("11/24 10:00", 80) + "," + (ChangeTail ? Entry("11/24 09:30", 50) + "," : "") + RepeatedReceipt,
                4 => RepeatedReceipt + "," + Entry(ChangeOrder ? "11/24 10:30" : "11/24 08:00", 70),
                _ => ""
            });
        }

        private static string Entry(string time, int amount)
            => System.Text.Json.JsonSerializer.Serialize(new { time, num = amount });

        private static Task<HttpResponseMessage> Reply(string entries) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"retcode\":0,\"data\":{\"list\":[" + entries + "]}}")
        });
    }

    private static async Task<int> ScenarioOverview()
    {
        var below = OverviewPage.PlaceMoraComposition(850, 590, 220, 115, 135, 320);
        var above = OverviewPage.PlaceMoraComposition(850, 590, 760, 440, 460, 320);
        var middle = OverviewPage.PlaceMoraComposition(640, 500, 450, 235, 255, 420);
        var narrow = OverviewPage.PlaceMoraComposition(240, 230, 220, 105, 131, 600);
        Check(below == (220, 140, 342, 320) && above == (500, 115, 342, 320),
            "收益构成优先贴着入口下侧，底部不够时向上展开，右侧保持在窗口内");
        Check(middle == (290, 72, 342, 420) && narrow == (8, 8, 224, 214),
            "上下空间不足时在页内保留最大可用高度，窄窗口限制宽高");

        const string account = "mora-overview-fixture", second = "mora-other-fixture", role = "900000001";
        const string manual = "mora-manual-fixture";
        var config = new AppConfig();
        config.Settings.MiyousheDefaultRole[account] = role;
        config.Settings.MiyousheDefaultRole[second] = role;
        typeof(App).GetProperty("Config", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, config);
        config.Settings.MiyousheDefaultRole[manual] = role;
        foreach (string name in new[] { account, second, manual })
            await MiyousheCredentialStore.SaveAsync(name, new MiyousheCredential
            {
                AccountId = "offline-fixture", CookieToken = "offline-fixture", DeviceId = Guid.NewGuid().ToString("D")
            });
        var now = new DateTime(2027, 1, 1, 3, 30, 0, DateTimeKind.Local);
        var day = DogfoodRecordReader.CurrentBusinessDay(now);
        var fixture = new OverviewLedgerHttp();
        using var http = new HttpClient(fixture);
        var client = new MiyousheClient("offline-fixture", http);
        client.ConfigureTestDelay(0);
        int events = 0;
        bool summaryReadyBeforeDetails = false;
        void OnLedger(string who, string uid)
        {
            if (who != account || uid != role) return;
            events++;
            if (fixture.MoraPages.Count == 0)
                summaryReadyBeforeDetails = ReadDiaryTotal(account).GetAwaiter().GetResult()?.Amount == 513517;
        }
        async Task<DailyMoraLedger.Total> ReadTotal()
            => (await DailyMoraLedger.QueryTotalsAsync(new[] { (account, role) }, day, DogfoodRecordReader.DayBoundaryHour))
                ?.GetValueOrDefault((account, role));
        async Task<DailyMoraLedger.Total> ReadDiaryTotal(string name)
            => (await DailyMoraLedger.QueryDiaryTotalsAsync(new[] { (name, role) }, day))
                ?.GetValueOrDefault((name, role));
        Task<MiyousheService.WorkResult> Pull(string name = account)
            => MiyousheService.RunForAccountAsync(name, false, true, default, expectedLedgerUid: role);

        MiyousheService.ClientFactoryForTest = _ => client;
        MiyousheService.LedgerNowForTest = () => now;
        MiyousheService.DelayForTest = _ => Task.CompletedTask;
        MiyousheService.LedgerRefreshed += OnLedger;
        try
        {
            long old = new DateTimeOffset(day.AddHours(8)).ToUnixTimeMilliseconds();
            await MiyousheStore.Shared.SaveLedgerFactsAsync(new[] {
                new MiyousheLedgerEntry(account, role, "cn_fixture", "2026-12", 2, 5, "任务", 20,
                    null, null, "2026-12-31 08:00", old, "{}", "fixture", old)
            }, Array.Empty<MiyousheLedgerDay>());
            var before = await ReadTotal();
            var updated = await Pull();
            var fetched = await ReadTotal();
            Check(before?.Amount == 20 && updated.ok && fetched?.Amount == 200, "真实拉取逻辑从隔离 HTTP 替身入库，04 点游戏日从 20 更新到 200");
            var paginatedArchive = await MiyousheStore.Shared.QueryAsync(account);
            long previousYearWithSeconds = new DateTimeOffset(new DateTime(2026, 12, 30, 23, 59, 26)).ToUnixTimeMilliseconds();
            Check(paginatedArchive.Entries.Any(entry => entry.Kind == 2 && entry.TimeText == "1/01 00:30" && entry.Num == 150) &&
                paginatedArchive.Entries.Any(entry => entry.Kind == 2 && entry.TsMs == previousYearWithSeconds && entry.Num == 100),
                "国服 page/limit 请求将第二页的摩拉原始流水分别存入本月和上月");
            Check(fixture.Months.SequenceEqual(new[] { 1, 12 }), "元旦 04 点前拉取当前月和上年 12 月");
            Check(fixture.MoraPages.SequenceEqual(new[] { (1, 1), (1, 2), (1, 3), (12, 1), (12, 2) }),
                "无分页大小的非空短页继续拉取，跨过最近日期范围后结束");
            Check(events > 0 && summaryReadyBeforeDetails, "拉取明细前可从通知读到已入库的札记今日获得 513517");
            var acrossMidnight = (await DailyMoraLedger.QueryDiaryCompositionsAsync(new[] { (account, role) }, day))
                ?.GetValueOrDefault((account, role));
            Check(acrossMidnight is { Total.Amount: 513517, Total.IsPartial: false, RecordedAmount: 200 } &&
                acrossMidnight.Entries.Count == 3,
                "元旦 04 点前的最新官方汇总归上年 12 月 31 日，构成纳入午夜前后两月流水");
            Check(OverviewPage.MoraReceipt(updated, true, true, before, fetched) == "今日摩拉已更新", "概览显示今日金额已更新");

            var unchanged = await Pull();
            Check(unchanged.ok && unchanged.entries == 0 && await ReadTotal() == fetched &&
                OverviewPage.MoraReceipt(unchanged, true, true, fetched, fetched).Contains("未变化"),
                "重复分页不翻倍，回执明确已拉取但今日金额未变化");

            fixture.BlockRoles = true;
            var running = Pull();
            await fixture.RoleRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var busy = await Pull(second);
            Check(busy.state == "queued" && OverviewPage.MoraReceipt(busy, true, true, null, null).Contains("其他任务"),
                "占用网络闸时回报忙碌且不发送第二个请求");
            config.Settings.MiyousheDefaultRole[account] = "900000002";
            int priorRequests = fixture.Months.Count;
            fixture.ReleaseRoles.TrySetResult(true);
            var changed = await running;
            Check(changed.state == "changed" && fixture.Months.Count == priorRequests, "角色在拉取途中更改，一律停止该旧任务的流水请求");
            config.Settings.MiyousheDefaultRole[account] = role;

            fixture.BlockRoles = false;
            fixture.RejectLogin = true;
            var unauthorized = await Pull();
            Check(unauthorized.state == "needs_login" && await ReadTotal() == fetched &&
                OverviewPage.MoraReceipt(unauthorized, true, true, fetched, fetched).Contains("重新登录"),
                "授权失效会提示重新登录并保留已入库金额");
            Check(OverviewPage.MoraReceipt(new(false, state: "default_role"), true, false, null, null).Contains("默认原神角色") &&
                OverviewPage.MoraReceipt(new(false, state: "failed"), true, false, fetched, fetched).Contains("保留"),
                "无默认角色及普通失败各有回执");
            Check(MiyousheService.LedgerMonthsForBusinessDay(now.Date.AddHours(4)).Single().Month == 1 &&
                MiyousheService.LedgerMonthsForBusinessDay(now.AddDays(1)).Single().Month == 1,
                "过月初 04 点或普通日期不会额外拉历史月");

            fixture.RejectLogin = false;
            fixture.RepeatFirstMoraPage = true;
            int priorDetailRequests = fixture.MoraPages.Count;
            var repeatedPage = await Pull(second);
            var archive = await MiyousheStore.Shared.QueryAsync(second);
            Check(!repeatedPage.ok && archive.Entries.Count == 1 &&
                fixture.MoraPages.Skip(priorDetailRequests).SequenceEqual(new[] { (1, 1), (1, 2) }),
                "重复整页会中止并报告流水未读完整");
            Check(await ReadDiaryTotal(second) is { Amount: 513517, IsPartial: false },
                "明细拉取失败时仍保留刚拉到的札记今日获得");

            fixture.RepeatFirstMoraPage = false;
            long occurred = new DateTimeOffset(now.Date.AddMinutes(25)).ToUnixTimeMilliseconds();
            await MiyousheStore.Shared.SaveRoleAsync(new(manual, role, "cn_fixture", "", 60), "{}", occurred);
            await MiyousheStore.Shared.SaveLedgerFactsAsync(new[] {
                new MiyousheLedgerEntry(manual, role, "cn_fixture", "2027-01", 2, 5, "击杀怪物奖励", 103728,
                    null, null, "2027-01-01 00:25", occurred, "{}", "fixture", occurred)
            }, new[] { new MiyousheLedgerDay(manual, role, now.Date.ToString("yyyy-MM-dd"),
                0, null, 777777, "{}", "ys_ledger/monthInfo", occurred) }, stableOccurrences: true);
            RunRecordStore.AddRun(new() { Account = manual, RunId = "manual-route-run", GroupName = "组",
                StartAtMs = occurred - 120_000, EndAtMs = occurred + 120_000, State = "Finished" });
            RunRecordStore.AddRoute(new() { RunId = "manual-route-run", RouteId = "manual-route", Name = "路线",
                Kind = "route", StartAtMs = occurred - 60_000, EndAtMs = occurred + 60_000 });

            var partial = await ReadDiaryTotal(manual);
            int priorMonthRequests = fixture.Months.Count;
            int priorRoles = fixture.RoleRequests;
            priorDetailRequests = fixture.DetailRequests;
            var summary = await MiyousheService.RefreshDailyMoraAsync(manual, role, default);
            Check(partial is { Amount: 103728, IsPartial: true } && summary.ok &&
                await ReadDiaryTotal(manual) is { Amount: 513517, IsPartial: false },
                "手动刷新用札记 513517 替换已采集流水 103728");
            Check(fixture.Months.Skip(priorMonthRequests).SequenceEqual(new[] { 1 }) &&
                fixture.RoleRequests == priorRoles && fixture.DetailRequests == priorDetailRequests,
                "已缓存角色的快速阶段只请求一次本月汇总，完成时未读取明细");

            var details = await MiyousheService.RefreshMoraDetailsAsync(manual, role, summary.summaryAt!.Value, default);
            var link = await MiyousheStore.Shared.RunAsync(db =>
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT amount FROM my_mora_route_income WHERE account=$account AND route_id='manual-route'";
                cmd.Parameters.AddWithValue("$account", manual);
                return cmd.ExecuteScalar() as long?;
            }, null);
            Check(details.ok && link == 103728 && fixture.Months.Skip(priorMonthRequests).SequenceEqual(new[] { 1, 12 }),
                "明细阶段复用本月汇总请求，并把已确认的摩拉写入对应路线");

            fixture.OfficialMora = null;
            var missing = await MiyousheService.RefreshDailyMoraAsync(manual, role, default);
            var repeatedDetails = await MiyousheService.RefreshMoraDetailsAsync(manual, role, missing.summaryAt!.Value, default);
            Check(missing.state == "summary_unavailable" && repeatedDetails.ok && repeatedDetails.entries == 0 &&
                await ReadDiaryTotal(manual) is { Amount: 513517, IsPartial: false },
                "未收到今日汇总仍同步并去重明细，保留上次官方统计");

            var afterRollover = now.Date.AddHours(DogfoodRecordReader.DayBoundaryHour);
            async Task<DailyMoraLedger.Total> ReadNewDay()
                => (await DailyMoraLedger.QueryDiaryTotalsAsync(new[] { (manual, role) }, afterRollover.Date))
                    ?.GetValueOrDefault((manual, role));
            Check(await ReadNewDay() is { Amount: 900, IsPartial: true },
                "旧版本误记到新自然日的 04 点前快照不作为新业务日官方总额");
            fixture.OfficialMora = 4321;
            var beforeRollover = now;
            now = afterRollover;
            var afterRolloverDetails = await MiyousheService.RefreshMoraDetailsAsync(manual, role, missing.summaryAt!.Value, default);
            now = beforeRollover;
            Check(afterRolloverDetails.ok && await ReadNewDay() is { Amount: 4321, IsPartial: false } &&
                await ReadDiaryTotal(manual) is { Amount: 513517, IsPartial: false },
                "后台明细跨过 04 点时重新取本月汇总，并保留上一业务日");

            config.Settings.MiyousheDefaultRole.Remove(manual);
            priorMonthRequests = fixture.Months.Count;
            priorRoles = fixture.RoleRequests;
            var noRole = await MiyousheService.RefreshDailyMoraAsync(manual, "", default);
            Check(noRole.state == "default_role" && fixture.Months.Count == priorMonthRequests &&
                fixture.RoleRequests == priorRoles, "已登录但无默认角色时只返回本地状态");

            const string compositionAccount = "mora-composition-fixture", compositionRole = "uid-composition";
            MiyousheLedgerEntry Fact(string uid, int minute, string action, long? amount,
                long? single = null, int? count = null)
            {
                var at = day.AddHours(DogfoodRecordReader.DayBoundaryHour).AddMinutes(minute);
                long time = new DateTimeOffset(at).ToUnixTimeMilliseconds();
                return new(compositionAccount, uid, "cn_fixture", at.ToString("yyyy-MM"), 2, minute, action,
                    amount, single, count, at.ToString("yyyy-MM-dd HH:mm"), time, "{}", "fixture", time);
            }
            await MiyousheStore.Shared.SaveLedgerFactsAsync(new[]
            {
                Fact(compositionRole, 1, "击杀怪物奖励", 400, single: 200),
                Fact(compositionRole, 2, "击杀怪物奖励", 100, count: 2),
                Fact(compositionRole, 3, "宝箱奖励", 200),
                Fact(compositionRole, 4, "任务奖励", 3000),
                Fact(compositionRole, 5, "击杀怪物奖励", 600, count: 7),
                Fact(compositionRole, 6, "击杀怪物奖励", 40),
                Fact(compositionRole, 7, "击杀怪物奖励", 200),
                Fact(compositionRole, 9, "击杀怪物奖励", 3000),
                Fact(compositionRole, 10, "击杀怪物奖励", 2400, single: 1200, count: 2),
                Fact(compositionRole, 11, "击杀怪物奖励", 1200, count: 2),
                Fact(compositionRole, 24 * 60 - 1, "宝箱奖励", null),
                Fact(compositionRole, -1, "任务奖励", 15_000),
                Fact(compositionRole, 24 * 60, "任务奖励", 15_000),
                Fact("uid-other", 8, "任务奖励", 15_000)
            }, new[] { new MiyousheLedgerDay(compositionAccount, compositionRole, day.ToString("yyyy-MM-dd"),
                0, null, 20_000, "{}", "ys_ledger/monthInfo", occurred) });
            var reports = await DailyMoraLedger.QueryDiaryCompositionsAsync(
                new[] { (compositionAccount, compositionRole), (compositionAccount, "") }, day);
            var composition = reports.GetValueOrDefault((compositionAccount, compositionRole));
            Check(composition is { Total.Amount: 20000, Total.IsPartial: false, RecordedAmount: 11140, MissingAmountCount: 1 } &&
                composition.Entries.Count == 11 && !reports.ContainsKey((compositionAccount, "")),
                "业务日构成只取指定角色，04 点范围外或其他 UID 不计入，缺少金额和汇总差额独立保留");
            Check(composition?.Parts.Select(part => (part.Kind, part.Amount, part.RecordCount)).SequenceEqual(new[]
            {
                (DailyMoraLedger.IncomeKind.Elite, 7200L, 5),
                (DailyMoraLedger.IncomeKind.Minion, 140L, 2),
                (DailyMoraLedger.IncomeKind.Chest, 200L, 2),
                (DailyMoraLedger.IncomeKind.Jar, 0L, 0),
                (DailyMoraLedger.IncomeKind.Other, 3000L, 1),
                (DailyMoraLedger.IncomeKind.Unknown, 600L, 1)
            }) == true, "国服简版 40/200 击杀分别计入小怪/精英，无法整除的聚合流水保持未分类");
            Check(composition?.EliteTiers.Select(tier => (tier.Value, tier.RecordCount)).SequenceEqual(new[]
            {
                (3000L, 1), (1200L, 1), (600L, 1), (200L, 2)
            }) == true, "精英档位按来源条数降序呈现，沿用归类数值，省略 800/400 等空档");
        }
        finally
        {
            MiyousheService.LedgerRefreshed -= OnLedger;
            MiyousheService.ResetTest();
        }
        return _fail;
    }

    private sealed class OverviewLedgerHttp : HttpMessageHandler
    {
        internal bool BlockRoles { get; set; }
        internal bool RejectLogin { get; set; }
        internal bool RepeatFirstMoraPage { get; set; }
        internal long? OfficialMora { get; set; } = 513517;
        internal int RoleRequests { get; private set; }
        internal int DetailRequests { get; private set; }
        internal List<int> Months { get; } = new();
        internal List<(int Month, int Page)> MoraPages { get; } = new();
        internal TaskCompletionSource<bool> RoleRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ReleaseRoles { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method != HttpMethod.Get) throw new InvalidOperationException("概览刷新不得执行签到");
            if (path.EndsWith("/getUserGameRolesByCookie", StringComparison.Ordinal))
            {
                RoleRequests++;
                if (RejectLogin) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                if (BlockRoles)
                {
                    RoleRequested.TrySetResult(true);
                    await ReleaseRoles.Task.WaitAsync(cancellationToken);
                }
                return Reply("""{"list":[{"game_uid":"900000001","region":"cn_fixture"}]}""");
            }
            var query = request.RequestUri.Query.TrimStart('?').Split('&')
                .Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => part[1]);
            int month = int.Parse(query["month"]);
            if (path.EndsWith("/monthInfo", StringComparison.Ordinal))
            {
                Months.Add(month);
                if (month == 12) return Reply("""{"day_data":{"current_mora":40000}}""");
                return OfficialMora.HasValue ? Reply(System.Text.Json.JsonSerializer.Serialize(
                    new { day_data = new { current_mora = OfficialMora.Value } }))
                    : Reply("""{"day_data":{}}""");
            }
            if (!path.EndsWith("/monthDetail", StringComparison.Ordinal))
                throw new InvalidOperationException("未知的模拟请求");
            DetailRequests++;
            if (query.ContainsKey("current_page") || query.ContainsKey("page_size") ||
                !query.TryGetValue("limit", out var limit) || limit != "20" ||
                !query.TryGetValue("page", out var pageText) || !int.TryParse(pageText, out int page) || page < 1)
                throw new InvalidOperationException("国服明细只接受 page 和 limit 分页参数");
            if (query["type"] == "1") return Reply("""{"list":[]}""");
            MoraPages.Add((month, page));
            if (RepeatFirstMoraPage) return Reply("""{"list":[{"time":"1/01 04:00","num":900}]}""");
            return (month, page) switch
            {
                (1, 1) => Reply("""{"page":1,"list":[{"time":"1/01 04:00","num":900}]}"""),
                (1, 2) => Reply("""{"page":"2","list":[{"time":"1/01 00:30","num":150}]}"""),
                (12, 1) => Reply("""{"list":[{"time":"12/31 23:55","num":30}]}"""),
                (12, 2) => Reply("""{"current_page":2,"list":[{"time":"12/31 03:59","num":800},{"time":"12/30 23:59:26","num":100}]}"""),
                _ => Reply("""{"list":[]}""")
            };
        }

        private static HttpResponseMessage Reply(string data)
            => new(HttpStatusCode.OK) { Content = new StringContent("{\"retcode\":0,\"data\":" + data + "}") };
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

    private static async Task<int> ScenarioDelayedRouteIncome(string dbDir)
    {
        var checkedAt = new DateTimeOffset(new DateTime(2026, 11, 28, 0, 40, 0));
        DateTimeOffset occurredAt = checkedAt.AddHours(-1);
        long occurred = occurredAt.ToUnixTimeMilliseconds();
        long received = checkedAt.ToUnixTimeMilliseconds();
        const string account = "delayed-ledger", other = "other-ledger", multi = "multi-ledger";
        const long minute = 60_000;

        void Run(string owner, string id, long from, long to) => RunRecordStore.AddRun(new()
        {
            Account = owner, RunId = id, GroupName = "group", StartAtMs = from, EndAtMs = to, State = "Finished"
        });
        void Route(string run, string id, long from, long? to, string kind = "route") => RunRecordStore.AddRoute(new()
        {
            RunId = run, RouteId = id, Name = id, Kind = kind, StartAtMs = from, EndAtMs = to
        });
        Run(account, "earlier", occurred - 3 * minute, occurred + 6 * minute);
        Route("earlier", "earlier-route", occurred - minute, occurred + minute);
        Route("earlier", "cd-before-replay", occurred + 2 * minute, occurred + 3 * minute, "cd_line");
        Route("earlier", "open-regular", occurred + 90_000, null);
        Route("earlier", "part-minute", occurred + 4 * minute + 10_000, occurred + 5 * minute);
        Route("earlier", "split-left", occurred + 5 * minute, occurred + 5 * minute + 25_000);
        Route("earlier", "split-right", occurred + 5 * minute + 25_000, occurred + 6 * minute);
        Route("earlier", "skipped-marker", occurred + 10_000, occurred + 10_000);
        Run(account, "arrival", received - minute, received + 2 * minute);
        Route("arrival", "arrival-route", received - minute, received + 2 * minute);
        Run(other, "other-run", occurred - minute, occurred + 2 * minute);
        Route("other-run", "other-route", occurred - minute, occurred + 2 * minute);
        Run(multi, "multi-run", occurred - minute, occurred + 2 * minute);
        Route("multi-run", "multi-route", occurred - minute, occurred + 2 * minute);

        var facts = new List<MiyousheLedgerEntry>();
        void Fact(string owner, string uid, long ts, long amount) => facts.Add(new(owner, uid, "cn_gf01",
            "2026-11", 2, facts.Count + 1, "精英怪", amount, amount, 1,
            DateTimeOffset.FromUnixTimeMilliseconds(ts).ToLocalTime().ToString("yyyy-MM-dd HH:mm"), ts,
            "{}", "test", received));
        Fact(account, "uid1", occurred, 200);
        Fact(account, "uid1", occurred + 2 * minute, 400);
        Fact(account, "uid1", occurred + 4 * minute, 600);
        Fact(account, "uid1", occurred + 5 * minute, 200);
        Fact(other, "uid1", occurred, 200);
        Fact(multi, "uid1", occurred, 200);
        Fact(multi, "uid2", occurred, 400);
        await MiyousheStore.Shared.SaveLedgerFactsAsync(facts, Array.Empty<MiyousheLedgerDay>(), default,
            stableOccurrences: true);
        foreach (string owner in new[] { other, multi })
        {
            await MiyousheStore.Shared.SaveRoleAsync(new(owner, "uid1", "cn_gf01", "default", 60), "{}", received);
            await MiyousheStore.Shared.SaveRoleAsync(new(owner, "uid2", "cn_qd01", "secondary", 10), "{}", received);
        }
        var config = new AppConfig();
        config.Settings.MiyousheDefaultRole[other] = "uid1";
        config.Settings.MiyousheDefaultRole[multi] = "uid1";
        typeof(App).GetProperty("Config", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, config);

        Task<List<(string Account, long Time, string Route)>> Links()
            => MiyousheStore.Shared.RunAsync(db =>
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT account,entry_ts_ms,COALESCE(route_id,'') FROM my_mora_route_income;";
                using var reader = cmd.ExecuteReader();
                var rows = new List<(string, long, string)>();
                while (reader.Read()) rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
                return rows;
            }, null, default);

        await MoraRouteIncome.UpdateAsync(account, "uid1", default, checkedAt);
        var links = await Links();
        Check(links.Count == 4 && links.All(x => x.Account == account), "拉取只处理指定 Windows 账号与 UID");
        Check(links.Contains((account, occurred, "earlier-route")), "跨午夜延迟一小时，匹配前一日原始时间且忽略零时长窗口");
        Check(links.Contains((account, occurred + 2 * minute, "")), "普通路线仍未收尾时，暂不写入当分钟的激活 CD 归属");

        RunRecordStore.CloseRoute(new()
        {
            RouteId = "open-regular", EndAtMs = occurred + 3 * minute, State = "Finished"
        });
        var retryAt = checkedAt.AddMinutes(16);
        await MoraRouteIncome.UpdateAsync(account, "uid1", default, retryAt);
        await MoraRouteIncome.UpdateAsync(other, "uid1", default, retryAt);
        await MoraRouteIncome.UpdateAsync(multi, "uid1", default, retryAt);
        await MoraRouteIncome.UpdateAsync(multi, "uid2", default, retryAt);
        links = await Links();
        Check(links.Contains((account, occurred + 2 * minute, "open-regular")), "日志补齐后，重试归属到已结束的普通路线");
        Check(links.Contains((account, occurred + 4 * minute, "")), "路线只覆盖部分分钟则保留未归属");
        Check(links.Contains((account, occurred + 5 * minute, "")), "同一个分钟内切换路线时保留未归属");
        Check(links.Contains((other, occurred, "other-route")), "同一账号绑定尚无流水的角色时，默认角色收益仍正确写入路线");
        var defaultBins = await DailyMoraLedger.QueryAsync(other, occurredAt.LocalDateTime.Date);
        Check(defaultBins.Count == 1 && defaultBins[0].RouteId == "other-route" && defaultBins[0].TotalMorale == 200,
            "日账本按同一规则显示默认角色的路线收益");
        Check(links.All(x => x.Account != multi), "同一 Windows 账号下两个游戏 UID 不争用同一 BGI 窗口");
        var multiBins = await DailyMoraLedger.QueryAsync(multi, occurredAt.LocalDateTime.Date);
        Check(multiBins.Count == 2 && multiBins.Sum(x => x.TotalMorale) == 600,
            "多游戏 UID 的未归属收入按各自 UID 完整保留");

        string file = System.IO.Path.Combine(dbDir, "better-genshin-impact20261127.log");
        System.IO.File.WriteAllText(file, """
            [23:38:00] 配置组 "group" 加载完成
            [23:39:00.250] [DBG] 进入路线循环，count=1，route="earlier.json"
            [12:01:01] 乱序普通日志
            [23:41:00.500] → 脚本执行结束: "earlier.json"
            [23:57:00] 进入路线循环，count=1，route="open.json"
            [2026-11-28 00:04:00] → 脚本执行结束: "open.json"
            """);
        var windows = MoraRouteWindowScanner.Scan(file);
        Check(windows.Count == 2 && windows[0].GroupName == "group"
            && windows[0].StartMs == occurred - 59_750 && windows[0].EndMs == occurred + 60_500,
            "文件扫描保留旧路线锚点与秒级时间，乱序日志不推到次日");
        Check(windows.Count == 2 && windows[1].EndMs == received - 40 * minute,
            "未结束文件窗口只覆盖本地日志当天");
        Check(MoraClassifier.AttributeRoute(windows, occurred, out var hit, out _, out _) == MoraClassifier.AttrRoute
            && hit.RouteName == "earlier.json", "手动归类扫描与原始米游社时间使用同一时间轴");

        const string jars = "jar-ledger";
        Run(jars, "jar-run", occurred, occurred + 6 * minute);
        Route("jar-run", "旧基捷城西南盗罐者5.json", occurred + 10_000, occurred + 30_000);
        Route("jar-run", "普通后段", occurred + 45_000, occurred + 52_000);
        Route("jar-run", "分支罐子A", occurred + 70_000, occurred + 105_000);
        Route("jar-run", "分支罐子B", occurred + 75_000, occurred + 101_000);
        Route("jar-run", "普通完整路线", occurred + 2 * minute, occurred + 3 * minute);
        Route("jar-run", "凡爷屋顶罐子1", occurred + 3 * minute, occurred + 3 * minute + 20_000);
        Route("jar-run", "砸罐收尾", occurred + 4 * minute, occurred + 5 * minute);

        MiyousheLedgerEntry JarFact(long delta, string action = "其他", bool seconds = true,
            long amount = 150, long? single = null, int? count = null)
        {
            long at = occurred + delta;
            string time = DateTimeOffset.FromUnixTimeMilliseconds(at).LocalDateTime
                .ToString(seconds ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm");
            return new(jars, "uid1", "cn_gf01", "2026-11", 2, (int)delta, action, amount,
                single, count, time, at, "{}", "test", received);
        }
        await MiyousheStore.Shared.SaveLedgerFactsAsync(new[]
        {
            JarFact(0, seconds: false),
            JarFact(20_000),
            JarFact(21_000, single: 75, count: 2),
            JarFact(22_000, amount: 300),
            JarFact(80_000),
            JarFact(130_000),
            JarFact(135_000, action: "击杀怪物奖励"),
            JarFact(180_000),
            JarFact(240_000, seconds: false)
        }, Array.Empty<MiyousheLedgerDay>(), stableOccurrences: true);
        await MoraRouteIncome.UpdateAsync(jars, "uid1", default, checkedAt);
        links = await Links();
        Check(links.Contains((jars, occurred + 20_000, "旧基捷城西南盗罐者5.json")) &&
            links.Contains((jars, occurred + 180_000, "凡爷屋顶罐子1")) &&
            links.Contains((jars, occurred + 240_000, "砸罐收尾")),
            "秒级罐子流水精确归属（含 :00），分钟流水仅整分钟覆盖时归属");
        Check(links.Contains((jars, occurred, "")) && links.Contains((jars, occurred + 80_000, "")) &&
            links.Contains((jars, occurred + 130_000, "")) && links.Contains((jars, occurred + 135_000, "普通完整路线")) &&
            links.All(link => link.Account != jars || (link.Time != occurred + 21_000 && link.Time != occurred + 22_000)),
            "模糊、重叠、普通路线 150 和聚合流水不计罐子，普通击杀仍保留路线归属");
        var jarComposition = (await DailyMoraLedger.QueryDiaryCompositionsAsync(new[] { (jars, "uid1") },
            DogfoodRecordReader.CurrentBusinessDay(occurredAt.LocalDateTime)))?.GetValueOrDefault((jars, "uid1"));
        Check(jarComposition is { RecordedAmount: 1500, EliteTiers.Count: 0 } &&
            jarComposition.Parts.Single(p => p.Kind == DailyMoraLedger.IncomeKind.Jar) is { Amount: 450, RecordCount: 3 } &&
            jarComposition.Parts.Single(p => p.Kind == DailyMoraLedger.IncomeKind.Minion) is { Amount: 150, RecordCount: 1 } &&
            jarComposition.Parts.Single(p => p.Kind == DailyMoraLedger.IncomeKind.Other) is { Amount: 900, RecordCount: 5 },
            "本地构成按已确认的罐子路线重分配金额，不重复计算也不吞掉其他收益");

        const string monsters = "monster-counts";
        Run(monsters, "monster-run", occurred, occurred + 10 * minute);
        Route("monster-run", "普通精英路线", occurred, occurred + minute);
        Route("monster-run", "传奇精英路线", occurred + minute, occurred + 2 * minute);
        facts.Clear();
        long[] amounts = [600, 600, 600, 200, 400, 800, 1200, 3000, 40];
        for (int index = 0; index < amounts.Length; index++) Fact(monsters, "uid1", occurred + index * minute, amounts[index]);
        await MiyousheStore.Shared.SaveLedgerFactsAsync(facts, Array.Empty<MiyousheLedgerDay>(), stableOccurrences: true);
        await MoraRouteIncome.UpdateAsync(monsters, "uid1", default, checkedAt);
        links = await Links();
        var monsterComposition = (await DailyMoraLedger.QueryDiaryCompositionsAsync(new[] { (monsters, "uid1") },
            DogfoodRecordReader.CurrentBusinessDay(occurredAt.LocalDateTime)))?.GetValueOrDefault((monsters, "uid1"));
        Check(links.Contains((monsters, occurred, "普通精英路线")) &&
            links.Contains((monsters, occurred + minute, "传奇精英路线")) &&
            links.Contains((monsters, occurred + 2 * minute, "")) &&
            monsterComposition is { RecordedAmount: 7440 } &&
            monsterComposition.Parts.Single(p => p.Kind == DailyMoraLedger.IncomeKind.Elite) is
                { Amount: 7400, RecordCount: 8, MonsterCount: 16 } &&
            monsterComposition.Parts.Single(p => p.Kind == DailyMoraLedger.IncomeKind.Minion) is
                { Amount: 40, RecordCount: 1, MonsterCount: 1 } &&
            monsterComposition.EliteTiers.Single(tier => tier.Value == 600).RecordCount == 3,
            "普通、传奇和未归属的 600 分别计 1/3/1 只，其他档位按映射累计，原始金额与档位条数保留");

        var atEliteLimit = new DailyMoraLedger.IncomePart(DailyMoraLedger.IncomeKind.Elite, 64000, 300, 400);
        var atMinionLimit = new DailyMoraLedger.IncomePart(DailyMoraLedger.IncomeKind.Minion, 60000, 2000, 2000);
        Check(!atEliteLimit.MonsterCountInvalid && !atMinionLimit.MonsterCountInvalid &&
            (atEliteLimit with { MonsterCount = 401 }).MonsterCountInvalid &&
            (atMinionLimit with { MonsterCount = 2001 }).MonsterCountInvalid,
            "每日精英 400 和小怪 2000 只正好达上限有效，超过一只才标数据错误");
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
        int currentVersion = MoraClassifier.RuleVersion;
        Check(versions.Contains(40) && versions.Contains(currentVersion), "历史版本与当前版本共存");

        var v40 = MoraClassifier.QueryRowsAsync(account, 30, 40, default).GetAwaiter().GetResult();
        var current = MoraClassifier.QueryRowsAsync(account, 30, currentVersion, default).GetAwaiter().GetResult();
        Console.WriteLine($"  v40 rows={v40.Count}（旧）  v{currentVersion} rows={current.Count}（新）");
        var old1 = v40.FirstOrDefault(r => r.EntryRowid == 1);
        var new1 = current.FirstOrDefault(r => r.EntryRowid == 1);
        if (old1 != null && new1 != null)
            Console.WriteLine($"  对比 entry#1: v40={old1.Category} → v{currentVersion}={new1.Category}（single=147）");
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



