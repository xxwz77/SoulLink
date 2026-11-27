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

    internal static async Task<List<Bin>> QueryAsync(string account, DateTime day)
    {
        long lo = new DateTimeOffset(day.Date).ToUnixTimeMilliseconds();
        long hi = new DateTimeOffset(day.Date.AddDays(1)).ToUnixTimeMilliseconds();
        var entries = await MiyousheStore.Shared.RunAsync<List<Entry>?>(db =>
        {
            using var cmd = db.CreateCommand();
            // BGI 窗口没有游戏 UID。账号有多个已知角色时保留每个 UID 的账目，无法据此归组。
            cmd.CommandText = @"WITH roles AS (SELECT game_uid FROM my_role WHERE account=$a COLLATE NOCASE
                UNION SELECT game_uid FROM my_ledger_entry WHERE account=$a COLLATE NOCASE)
                SELECT game_uid,ts_ms,num,action,(SELECT count(*) FROM roles)=1 FROM my_ledger_entry
                WHERE account=$a COLLATE NOCASE AND kind=2 AND ts_ms >= $lo AND ts_ms < $hi ORDER BY ts_ms,rowid";
            cmd.Parameters.AddWithValue("$a",account);
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
