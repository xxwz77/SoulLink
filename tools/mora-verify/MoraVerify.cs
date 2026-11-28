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

        if (args.Contains("--daily") || args.Contains("--route-income") || args.Contains("--overview"))
        {
            if (!string.Equals(AppIdentity.DataDir, dbDir, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("测试库未隔离");
            if (args.Contains("--daily")) return ScenarioDaily().GetAwaiter().GetResult();
            if (args.Contains("--overview")) return ScenarioOverview().GetAwaiter().GetResult();
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

    private static async Task<int> ScenarioOverview()
    {
        const string account = "mora-overview-fixture", second = "mora-other-fixture", role = "900000001";
        var config = new AppConfig();
        config.Settings.MiyousheDefaultRole[account] = role;
        config.Settings.MiyousheDefaultRole[second] = role;
        typeof(App).GetProperty("Config", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, config);
        foreach (string name in new[] { account, second })
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
        bool committedAtNotification = false;
        void OnLedger(string who, string uid)
        {
            if (who != account || uid != role) return;
            events++;
            committedAtNotification = ReadTotal().GetAwaiter().GetResult()?.Amount == 200;
        }
        async Task<DailyMoraLedger.Total> ReadTotal()
            => (await DailyMoraLedger.QueryTotalsAsync(new[] { (account, role) }, day, DogfoodRecordReader.DayBoundaryHour))
                ?.GetValueOrDefault((account, role));
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
            Check(fixture.Months.SequenceEqual(new[] { 1, 12 }), "元旦 04 点前拉取当前月和上年 12 月");
            Check(events == 1 && committedAtNotification, "自动/手动共用通知发出时流水已能读回");
            Check(OverviewPage.MoraReceipt(updated, true, true, before, fetched) == "今日摩拉已更新", "概览显示今日金额已更新");

            var unchanged = await Pull();
            Check(unchanged.ok && unchanged.entries == 0 && await ReadTotal() == fetched && events == 1 &&
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
        internal List<int> Months { get; } = new();
        internal TaskCompletionSource<bool> RoleRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ReleaseRoles { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method != HttpMethod.Get) throw new InvalidOperationException("概览刷新不得执行签到");
            if (path.EndsWith("/getUserGameRolesByCookie", StringComparison.Ordinal))
            {
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
                return Reply("""{"day_data":{"current_mora":0}}""");
            }
            if (!path.EndsWith("/monthDetail", StringComparison.Ordinal))
                throw new InvalidOperationException("未知的模拟请求");
            if (query["type"] == "1") return Reply("""{"list":[]}""");
            return month == 1
                ? Reply("""{"list":[{"time":"1/01 00:30","num":150},{"time":"1/01 04:00","num":900}]}""")
                : Reply("""{"list":[{"time":"12/31 03:59","num":800},{"time":"12/31 23:55","num":30}]}""");
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
        Check(links.Contains((other, occurred, "other-route")), "另一 Windows 账号同一分钟单独归属");
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



