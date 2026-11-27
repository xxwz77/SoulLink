using SoulLink.Services;

static void Check(bool ok, string detail)
{
    if (!ok) throw new Exception(detail);
    Console.WriteLine("PASS " + detail);
}
var root = Directory.CreateTempSubdirectory("SoulLink-MissingLog-");
try
{
    var day = new DateTime(2026, 12, 31);
    var exe = Path.Combine(root.FullName, "BetterGI.exe");
    string path = BgiDailyLogProbe.ExpectedPath(exe, day);
    int stats = 0;
    var probe = new BgiDailyLogProbe(p => {
        Interlocked.Increment(ref stats);
        var file = new FileInfo(p);
        return file.Exists ? file : null;
    });
    FileInfo? Find(string p, long now) => probe.Find(p, now, day, out _);
    Check(Find("", 0) == null && stats == 0, "empty path: zero probes");
    Check(Find(path, 0) == null && stats == 1, "missing log directory: one probe");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(BgiDailyLogProbe.ExpectedPath(exe, day.AddDays(-1)), "old");
    Parallel.For(0, 1000, _ => { if (Find(path, 1) != null) throw new Exception(); });
    Check(stats == 1, "1000 concurrent misses: no extra metadata probes, no yesterday fallback");
    Check(Find(path, 30_000) == null && stats == 2, "still absent: one retry at 30 seconds");
    File.WriteAllText(path, "restored");
    Check(Find(path, 59_999) == null && stats == 2, "no early retry");
    Check(Find(path, 60_000)?.Length == 8 && stats == 3, "same-day recovery at retry");
    File.Delete(path);
    Check(Find(path, 60_001) == null && stats == 4, "deletion after recovery starts fresh missing interval");
    string next = BgiDailyLogProbe.ExpectedPath(exe, day.AddDays(1));
    File.WriteAllText(next, "new year");
    Check(Path.GetFileName(next) == "better-genshin-impact20270101.log" && Find(next, 60_002)?.FullName == next, "new day bypasses previous day's backoff");
    string other = BgiDailyLogProbe.ExpectedPath(Path.Combine(root.FullName, "other", "BetterGI.exe"), day);
    Directory.CreateDirectory(Path.GetDirectoryName(other)!);
    File.WriteAllText(other, "other");
    Check(Find(other, 60_002)?.FullName == other, "rebind to another installation bypasses previous backoff");
    string cache = Path.Combine(root.FullName, "confirmed-missing.txt");
    string old = BgiDailyLogProbe.ExpectedPath(exe, day.AddDays(-2));
    int reads = 0;
    FileInfo Stat(string p) { Interlocked.Increment(ref reads); var file = new FileInfo(p); _ = file.Length; return file; }
    var history = new BgiDailyLogProbe(Stat, cache);
    Check(history.Find(old, 0, day, out bool missing) == null && missing, "old file absent in reachable directory: confirmed");
    Parallel.For(0, 1000, i => history.Find(old, i * 60_000L, day, out _));
    Check(reads == 1, "historical file: no further metadata operations across retries");
    history = new BgiDailyLogProbe(Stat, cache);
    Check(history.Find(old, 0, day, out missing) == null && missing && reads == 1, "cold probe instance: persisted confirmation, zero source IO");
    string offline = BgiDailyLogProbe.ExpectedPath(Path.Combine(root.FullName, "offline", "BetterGI.exe"), day.AddDays(-2));
    Check(history.Find(offline, 0, day, out missing) == null && !missing, "unavailable log directory: no permanent record");
    Directory.CreateDirectory(Path.GetDirectoryName(offline)!);
    File.WriteAllText(offline, "available");
    Check(history.Find(offline, 30_000, day, out _)?.Length == 9, "other installation recovers despite same historical date");
    var denied = new BgiDailyLogProbe(p => throw new UnauthorizedAccessException(), cache);
    Check(denied.Find(offline, 0, day, out missing) == null && !missing, "access denied: never marked absent");
    Check(new BgiDailyLogProbe(Stat, cache).Find(offline, 0, day, out _) != null, "reentry after access error recovers");
    var visited = new List<string>();
    var backfill = new BgiDailyLogProbe(p => { visited.Add(p); return Stat(p); }, cache);
    string yesterday = BgiDailyLogProbe.ExpectedPath(exe, day.AddDays(-1));
    Check(backfill.RecentHistory(exe, 3, 0, day).Select(f => f.FullName).SequenceEqual(new[] { yesterday }) && visited.SequenceEqual(new[] { yesterday }), "backfill on restart: only existing yesterday is probed; today and known absent history receive zero IO");
    Check(!BgiDailyLogProbe.IsHistory(path, day) && BgiDailyLogProbe.IsHistory(path, day.AddDays(1)), "midnight changes classification of stale path");
}
finally { root.Delete(true); }
