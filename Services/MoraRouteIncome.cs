using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace SoulLink.Services;

/// <summary>Incrementally links recent Mora ledger entries to closed BGI route executions.</summary>
internal static class MoraRouteIncome
{
    private const int BatchSize = 128;
    private const int MaxBatchesPerPull = 32;
    private const long MinuteMs = 60_000;

    private sealed record Entry(long RowId, long Time, long Amount);
    private sealed record Match(Entry Entry, RunRecordStore.LedgerWindow? Route);

    internal static async Task UpdateAsync(string account, string uid, CancellationToken ct)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long cutoff = now - 48 * 60 * MinuteMs;
        List<RunRecordStore.LedgerWindow>? windows = null;
        bool retryUnmatched = false;

        for (int batch = 0; batch < MaxBatchesPerPull; batch++)
        {
            var entries = await ReadBatchAsync(account, uid, cutoff, now, retryUnmatched, ct).ConfigureAwait(false);
            if (entries == null) return;
            if (entries.Count == 0 && !retryUnmatched)
            {
                retryUnmatched = true;
                entries = await ReadBatchAsync(account, uid, cutoff, now, retryUnmatched, ct).ConfigureAwait(false);
            }
            if (entries == null || entries.Count == 0) return;

            // Route windows come from the BGI database. Do not retain the ledger database gate while reading them.
            windows ??= await RunRecordStore.QueryLedgerWindowsAsync(account, cutoff, now + MinuteMs, includeCollectCd: true, ct)
                .ConfigureAwait(false);
            if (windows == null) return;

            var matches = MatchBatch(entries, windows);
            if (!await WriteBatchAsync(account, uid, now, matches, ct).ConfigureAwait(false)) return;
        }
    }

    private static Task<List<Entry>?> ReadBatchAsync(string account, string uid, long cutoff, long now,
        bool retryUnmatched, CancellationToken ct)
        => MiyousheStore.Shared.RunAsync<List<Entry>?>(db =>
        {
            using var cmd = db.CreateCommand();
            string[] actionParameters = MoraClassifier.KillActions.Select((_, index) => "$action" + index).ToArray();
            cmd.CommandText = @"
SELECT e.rowid,e.ts_ms,e.num FROM my_ledger_entry e
LEFT JOIN my_mora_route_income m ON m.entry_rowid=e.rowid
WHERE e.account=$account AND e.game_uid=$uid AND e.kind=2 AND e.num IS NOT NULL AND e.pull_confirmed=1
  AND e.ts_ms >= $cutoff AND e.ts_ms <= $now
  AND TRIM(e.action) IN (" + string.Join(",", actionParameters) + ") AND " + (retryUnmatched
                ? "m.route_id IS NULL AND m.checked_at_ms < $retry"
                : "m.entry_rowid IS NULL") + @"
ORDER BY e.ts_ms DESC,e.rowid DESC LIMIT $limit;";
            cmd.Parameters.AddWithValue("$account", account);
            cmd.Parameters.AddWithValue("$uid", uid);
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$retry", now - 15 * MinuteMs);
            cmd.Parameters.AddWithValue("$limit", BatchSize);
            for (int index = 0; index < actionParameters.Length; index++)
                cmd.Parameters.AddWithValue(actionParameters[index], MoraClassifier.KillActions[index]);
            using var reader = cmd.ExecuteReader();
            var rows = new List<Entry>(BatchSize);
            while (reader.Read()) rows.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)));
            return rows;
        }, null, ct);

    private static List<Match> MatchBatch(List<Entry> entries, List<RunRecordStore.LedgerWindow> windows)
    {
        var matches = new List<Match>(entries.Count);
        var active = new List<RunRecordStore.LedgerWindow>();
        int next = 0;
        foreach (var entry in entries.OrderBy(e => e.Time))
        {
            long minute = entry.Time - entry.Time % MinuteMs;
            long end = minute + MinuteMs;
            while (next < windows.Count && windows[next].Start < end) active.Add(windows[next++]);
            active.RemoveAll(window => window.End <= minute);
            var (run, route) = DailyMoraLedger.ResolveWindows(active, eligible: true);
            // The ledger only resolves to a minute. Keep boundary minutes unassigned.
            if (run == null || run.Start > minute || run.End < end
                || route == null || route.Start > minute || route.End < end)
                route = null;
            matches.Add(new(entry, route));
        }
        return matches;
    }

    private static Task<bool> WriteBatchAsync(string account, string uid, long now, List<Match> matches, CancellationToken ct)
        => MiyousheStore.Shared.RunAsync(db =>
        {
            using var transaction = db.BeginTransaction();
            using var cmd = db.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
INSERT INTO my_mora_route_income(entry_rowid,account,game_uid,entry_ts_ms,amount,
 run_id,route_id,route_name,route_start_ms,route_end_ms,checked_at_ms)
VALUES($id,$account,$uid,$ts,$amount,$run,$route,$name,$start,$end,$now)
ON CONFLICT(entry_rowid) DO UPDATE SET run_id=excluded.run_id,route_id=excluded.route_id,
 route_name=excluded.route_name,route_start_ms=excluded.route_start_ms,route_end_ms=excluded.route_end_ms,
 checked_at_ms=excluded.checked_at_ms WHERE my_mora_route_income.route_id IS NULL;";
            cmd.Parameters.AddWithValue("$account", account);
            cmd.Parameters.AddWithValue("$uid", uid);
            cmd.Parameters.AddWithValue("$now", now);
            foreach (string name in new[] { "$id", "$ts", "$amount", "$run", "$route", "$name", "$start", "$end" })
                cmd.Parameters.Add(new SqliteParameter { ParameterName = name });

            foreach (var match in matches)
            {
                ct.ThrowIfCancellationRequested();
                cmd.Parameters["$id"].Value = match.Entry.RowId;
                cmd.Parameters["$ts"].Value = match.Entry.Time;
                cmd.Parameters["$amount"].Value = match.Entry.Amount;
                cmd.Parameters["$run"].Value = (object?)match.Route?.RunId ?? DBNull.Value;
                cmd.Parameters["$route"].Value = (object?)match.Route?.RouteId ?? DBNull.Value;
                cmd.Parameters["$name"].Value = (object?)match.Route?.RouteName ?? DBNull.Value;
                cmd.Parameters["$start"].Value = (object?)match.Route?.Start ?? DBNull.Value;
                cmd.Parameters["$end"].Value = (object?)match.Route?.End ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }
            transaction.Commit();
            return true;
        }, false, ct);
}
