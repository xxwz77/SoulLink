using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace SoulLink.Services;

/// <summary>只读日账本。金额取米游社原始流水 num，run/route 只提供归属窗口。</summary>
internal static class DailyMoraLedger
{
    internal sealed record Entry(string Uid, long Minute, long? Amount, bool Eligible);
    internal sealed record Bin(string Day, string GameUid, string RunId, string GroupName, string RouteId,
        string RouteName, int RecordCount, long? TotalMorale, int MissingAmountCount);
    internal sealed record Total(long Amount, bool IsPartial);
    internal enum IncomeKind { Elite, Minion, Chest, Jar, Other, Unknown }
    internal sealed record IncomeEntry(IncomeKind Kind, long TimeMs, string Action, long? Amount, string? RouteName);
    internal sealed record IncomePart(IncomeKind Kind, long Amount, int RecordCount, long MonsterCount)
    {
        internal int? MonsterDailyLimit => Kind switch
        {
            IncomeKind.Elite => 400,
            IncomeKind.Minion => 2000,
            _ => null
        };
        internal bool MonsterCountInvalid => MonsterDailyLimit is int limit && MonsterCount > limit;
    }
    internal sealed record EliteTier(long Value, int RecordCount);
    internal sealed record Composition(Total? Total, long RecordedAmount, int MissingAmountCount,
        IReadOnlyList<IncomePart> Parts, IReadOnlyList<IncomeEntry> Entries, IReadOnlyList<EliteTier> EliteTiers);

    private const string DayEntries = "account=$a COLLATE NOCASE AND kind=2 AND ts_ms >= $lo AND ts_ms < $hi";

    private static (long Start, long End) DayRange(DateTime day, int boundaryHour = 0)
    {
        var start = day.Date.AddHours(boundaryHour);
        return (new DateTimeOffset(start).ToUnixTimeMilliseconds(),
            new DateTimeOffset(start.AddDays(1)).ToUnixTimeMilliseconds());
    }

    internal static async Task<List<Bin>> QueryAsync(string account, DateTime day)
    {
        var (lo, hi) = DayRange(day);
        var entries = await MiyousheStore.Shared.RunAsync<List<Entry>?>(db =>
        {
            using var cmd = db.CreateCommand();
            // BGI 窗口只记 Windows 账号。选中的默认角色可排除没有流水的绑定角色。
            cmd.CommandText = @"WITH ledger_roles AS (SELECT DISTINCT game_uid FROM my_ledger_entry WHERE account=$a COLLATE NOCASE),
                roles AS (SELECT game_uid FROM my_role WHERE account=$a COLLATE NOCASE UNION SELECT game_uid FROM ledger_roles)
                SELECT game_uid,ts_ms,num,action,(SELECT count(*) FROM ledger_roles)=1
                    AND (game_uid=$default OR (SELECT count(*) FROM roles)=1) FROM my_ledger_entry
                WHERE " + DayEntries + " ORDER BY ts_ms,rowid";
            cmd.Parameters.AddWithValue("$a",account);
            cmd.Parameters.AddWithValue("$default",MiyousheService.GetDefaultRole(account));
            cmd.Parameters.AddWithValue("$lo",lo);
            cmd.Parameters.AddWithValue("$hi",hi);
            using var reader = cmd.ExecuteReader();
            var rows = new List<Entry>();
            while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.GetBoolean(4) &&
                reader.GetString(0).Length > 0 && MoraClassifier.IsKillAction(reader.GetString(3))));
            return rows;
        }, null).ConfigureAwait(false) ?? throw new InvalidOperationException("米游社流水查询失败");
        if (entries.Count == 0) return new();
        var windows = await RunRecordStore.QueryLedgerWindowsAsync(account, lo, hi).ConfigureAwait(false)
            ?? throw new InvalidOperationException("运行窗口查询失败");
        return Attribute(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), entries, windows);
    }

    internal static Task<Dictionary<(string Account, string Role), Total>?> QueryTotalsAsync(
        IReadOnlyList<(string Account, string Role)> accounts, DateTime day, int boundaryHour)
    {
        var (lo, hi) = DayRange(day, boundaryHour);
        return MiyousheStore.Shared.RunAsync<Dictionary<(string Account, string Role), Total>?>(db =>
        {
            using var cmd = db.CreateCommand();
            // A sole known role also covers accounts with imported history and no role selection yet.
            cmd.CommandText = "WITH daily AS (SELECT game_uid,num FROM my_ledger_entry WHERE " + DayEntries + @"),
                roles AS (SELECT game_uid FROM my_role WHERE account=$a COLLATE NOCASE AND game_uid<>''
                    UNION SELECT game_uid FROM daily WHERE game_uid<>'')
                SELECT SUM(num),COUNT(*)-COUNT(num) FROM daily
                WHERE game_uid=COALESCE(NULLIF($uid,''),(SELECT MIN(game_uid) FROM roles HAVING COUNT(*)=1))";
            var accountParam = cmd.Parameters.AddWithValue("$a", "");
            var roleParam = cmd.Parameters.AddWithValue("$uid", "");
            cmd.Parameters.AddWithValue("$lo", lo);
            cmd.Parameters.AddWithValue("$hi", hi);

            var totals = new Dictionary<(string Account, string Role), Total>();
            foreach (var account in accounts)
            {
                accountParam.Value = account.Account;
                roleParam.Value = account.Role;
                using var reader = cmd.ExecuteReader();
                if (reader.Read() && !reader.IsDBNull(0)) totals[account] = new(reader.GetInt64(0), reader.GetInt64(1) > 0);
            }
            return totals;
        }, null);
    }

    internal static async Task<Dictionary<(string Account, string Role), Total>?> QueryDiaryTotalsAsync(
        IReadOnlyList<(string Account, string Role)> accounts, DateTime day)
    {
        var compositions = await QueryDiaryCompositionsAsync(accounts, day).ConfigureAwait(false);
        return compositions?.Where(pair => pair.Value.Total != null)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Total!);
    }

    internal static Task<Dictionary<(string Account, string Role), Composition>?> QueryDiaryCompositionsAsync(
        IReadOnlyList<(string Account, string Role)> accounts, DateTime day)
    {
        var (lo, hi) = DayRange(day, DogfoodRecordReader.DayBoundaryHour);
        return MiyousheStore.Shared.RunAsync<Dictionary<(string Account, string Role), Composition>?>(db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"WITH daily AS (SELECT rowid,account,game_uid,num,ts_ms,action,single_mora,count
                    FROM my_ledger_entry WHERE " + DayEntries + @"),
                official AS (SELECT game_uid,mora,fetched_at_ms FROM my_ledger_day
                    WHERE account=$a COLLATE NOCASE AND day=$day AND source='ys_ledger/monthInfo' AND mora IS NOT NULL
                        AND fetched_at_ms >= $lo AND fetched_at_ms < $hi),
                roles AS (SELECT game_uid FROM my_role WHERE account=$a COLLATE NOCASE AND game_uid<>''
                    UNION SELECT game_uid FROM daily WHERE game_uid<>''
                    UNION SELECT game_uid FROM official WHERE game_uid<>''),
                selected(uid) AS (SELECT COALESCE(NULLIF($uid,''),(SELECT MIN(game_uid) FROM roles HAVING COUNT(*)=1)))
                SELECT 0 AS source,mora,NULL AS ts_ms,NULL,NULL,NULL,NULL,0 AS rowid FROM (
                    SELECT mora FROM official WHERE game_uid=(SELECT uid FROM selected)
                    ORDER BY fetched_at_ms DESC LIMIT 1)
                UNION ALL
                SELECT 1,d.num,d.ts_ms,d.action,d.single_mora,d.count,m.route_name,d.rowid FROM daily d
                LEFT JOIN my_mora_route_income m ON m.entry_rowid=d.rowid AND m.account=d.account
                    AND m.game_uid=d.game_uid AND m.entry_ts_ms=d.ts_ms AND m.amount=d.num AND m.route_id<>''
                WHERE d.game_uid=(SELECT uid FROM selected)
                ORDER BY source,ts_ms DESC,rowid DESC";
            var accountParam = cmd.Parameters.AddWithValue("$a", "");
            var roleParam = cmd.Parameters.AddWithValue("$uid", "");
            cmd.Parameters.AddWithValue("$lo", lo);
            cmd.Parameters.AddWithValue("$hi", hi);
            cmd.Parameters.AddWithValue("$day", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            var compositions = new Dictionary<(string Account, string Role), Composition>();
            foreach (var account in accounts)
            {
                accountParam.Value = account.Account;
                roleParam.Value = account.Role;
                using var reader = cmd.ExecuteReader();
                var entries = new List<IncomeEntry>();
                var amounts = new long[Enum.GetValues<IncomeKind>().Length];
                var counts = new int[amounts.Length];
                var monsters = new long[amounts.Length];
                var eliteTiers = new Dictionary<long, int>();
                long? official = null;
                long recorded = 0;
                int missing = 0;
                while (reader.Read())
                {
                    if (reader.GetInt32(0) == 0)
                    {
                        official = reader.GetInt64(1);
                        continue;
                    }
                    long? amount = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                    string action = reader.GetString(3).Trim();
                    long? single = reader.IsDBNull(4) ? null : reader.GetInt64(4);
                    long? count = reader.IsDBNull(5) ? null : reader.GetInt64(5);
                    string? routeName = reader.IsDBNull(6) ? null : reader.GetString(6);
                    var (category, eliteTier, monsterCount) = Categorize(action, single, count, amount, routeName);
                    entries.Add(new(category, reader.GetInt64(2), action, amount,
                        category == IncomeKind.Jar ? routeName : null));
                    counts[(int)category]++;
                    monsters[(int)category] += monsterCount;
                    if (eliteTier is long tier) eliteTiers[tier] = eliteTiers.GetValueOrDefault(tier) + 1;
                    if (amount is long value)
                    {
                        amounts[(int)category] = checked(amounts[(int)category] + value);
                        recorded = checked(recorded + value);
                    }
                    else missing++;
                }
                if (!official.HasValue && entries.Count == 0) continue;
                Total? total = official is long diary ? new(diary, false)
                    : entries.Count > missing ? new(recorded, true) : null;
                var parts = Enum.GetValues<IncomeKind>().Select(kind => new IncomePart(kind,
                    amounts[(int)kind], counts[(int)kind], monsters[(int)kind])).ToArray();
                var tiers = eliteTiers.OrderByDescending(pair => pair.Key)
                    .Select(pair => new EliteTier(pair.Key, pair.Value)).ToArray();
                compositions[account] = new(total, recorded, missing, parts, entries, tiers);
            }
            return compositions;
        }, null);
    }

    private static (IncomeKind Kind, long? EliteTier, int MonsterCount) Categorize(string action, long? single,
        long? count, long? amount, string? routeName)
    {
        if (action.Length == 0) return (IncomeKind.Unknown, null, 0);
        if (action == "宝箱奖励") return (IncomeKind.Chest, null, 0);
        if (MoraRouteIncome.IsJarReceipt(action, amount) && MoraRouteIncome.IsJarRoute(routeName))
            return (IncomeKind.Jar, null, 0);

        // 国服击杀流水的简版格式每笔仅提供 num。概览与移动端按该笔数值归档。
        long? unitValue = action == "击杀怪物奖励" && !single.HasValue && !count.HasValue
            ? amount : MoraClassifier.ResolveSingleMora(single, count, amount);
        string classification = MoraClassifier.Classify(action, unitValue, routeName);
        return classification switch
        {
            MoraClassifier.CatLegendaryElite or MoraClassifier.CatElite =>
                (IncomeKind.Elite, unitValue, EliteMonsterCount(unitValue, classification)),
            MoraClassifier.CatLegendaryMinion or MoraClassifier.CatMinion => (IncomeKind.Minion, null, 1),
            MoraClassifier.CatNonKill => (IncomeKind.Other, null, 0),
            _ => (IncomeKind.Unknown, null, 0)
        };
    }

    private static int EliteMonsterCount(long? tier, string classification) => tier switch
    {
        600 when classification == MoraClassifier.CatLegendaryElite => 3,
        800 => 4,
        1200 => 2,
        3000 => 3,
        _ => 1
    };

    // Both inputs are ordered by time. Sweep the windows once and retain only the active minute.
    internal static List<Bin> Attribute(string day, List<Entry> entries, List<RunRecordStore.LedgerWindow> windows)
    {
        var active = new List<RunRecordStore.LedgerWindow>();
        var bins = new Dictionary<(string Uid, string Run, string Route), Bin>();
        int next = 0;
        foreach (var entry in entries)
        {
            long start = entry.Minute - entry.Minute % 60_000;
            long end = start + 60_000;
            while (next < windows.Count && windows[next].Start < end) active.Add(windows[next++]);
            active.RemoveAll(w => w.End <= start);
            var (run, route) = ResolveWindows(active, entry.Eligible);
            var key = (entry.Uid, run?.RunId ?? "", route?.RouteId ?? "");
            if (!bins.TryGetValue(key, out var bin)) bin = new(day, entry.Uid, key.Item2,
                run?.GroupName ?? "", key.Item3, route?.RouteName ?? "", 0, null, 0);
            bins[key] = bin with { RecordCount = bin.RecordCount + 1,
                TotalMorale = entry.Amount.HasValue ? checked((bin.TotalMorale ?? 0) + entry.Amount.Value) : bin.TotalMorale,
                MissingAmountCount = bin.MissingAmountCount + (entry.Amount.HasValue ? 0 : 1) };
        }
        return bins.Values.ToList();
    }

    internal static (RunRecordStore.LedgerWindow? Run, RunRecordStore.LedgerWindow? Route) ResolveWindows(
        List<RunRecordStore.LedgerWindow> active, bool eligible)
    {
        if (!eligible) return (null, null);
        var runs = active.Where(w => w.RouteId.Length == 0).Take(2).ToArray();
        if (runs.Length != 1) return (null, null);
        var run = runs[0];
        var routes = active.Where(w => w.RouteId.Length > 0 && w.RunId == run.RunId);
        var files = routes.Where(w => w.Kind != "cd_line").Take(2).ToArray();
        if (files.Length > 0) return (run, files.Length == 1 ? files[0] : null);
        var collectCd = routes.Where(w => w.Kind == "cd_line").Take(2).ToArray();
        return (run, collectCd.Length == 1 ? collectCd[0] : null);
    }
}
