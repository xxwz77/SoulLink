using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SoulLink.Services;

internal static class T44Bench
{
    public static async Task<int> Run()
    {
        // t44 验收：真实 A1 全文件（时间一致性 + 无假跨日 + raw_text）
        string a1 = @"D:\AAA挂机专区\A1\BetterGI\log\better-genshin-impact20261125.log";
        if (File.Exists(a1))
        {
            Console.WriteLine("==== T44-ACCEPT A1 全文件 ====");
            string db1 = Path.Combine(Path.GetTempPath(), "t44a1-" + Guid.NewGuid().ToString("N")[..8] + ".db");
            await RunRecordStore.UseIsolatedForTest(db1, (t, ex) => { if (ex != null) Console.WriteLine("[diag] " + t); });
            RunRecordCollector.ResetForTest();
            var swA = System.Diagnostics.Stopwatch.StartNew();
            // 分 4MB 块喂（模拟 Pump 的真实节奏）
            var lines = new List<string>();
            using (var fs = new FileStream(a1, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            { string? l; while ((l = sr.ReadLine()) != null) lines.Add(l); }
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                sb.Append(lines[i]); if (i < lines.Count - 1) sb.Append('\n');
                if ((i + 1) % 50000 == 0 || i == lines.Count - 1)
                { RunRecordCollector.Feed("A1", sb.ToString(), "better-genshin-impact20261125.log"); sb.Clear(); }
            }
            swA.Stop();
            Console.WriteLine("A1 全文件 Feed 耗时=" + swA.Elapsed.TotalSeconds.ToString("F1") + "s（" + lines.Count.ToString("N0") + " 行）");
            await RunRecordStore.FlushAsync();
            var runs = await RunRecordStore.QueryRunsAsync("A1", 30);
            Console.WriteLine("runs=" + runs.Count);
            var bad = runs.Where(r => r.EndAtMs.HasValue && r.EndAtMs.Value - r.StartAtMs > 20 * 3600_000 && r.State == "Finished").ToList();
            Console.WriteLine("疑似假跨日(>20h Finished)=" + bad.Count);
            foreach (var r in runs.Where(r => r.GroupName == "等待至4-00"))
                Console.WriteLine("  等待至4-00: " + IsoLocal(r.StartAtMs) + "→" + (r.EndAtMs.HasValue ? IsoLocal(r.EndAtMs.Value) : "OPEN") + " state=" + r.State);
            var raw = await RunRecordStore.QueryRawEventsAsync("A1", null, 30);
            int nonEmpty = System.Linq.Enumerable.Count(raw, r => !string.IsNullOrEmpty(r.RawText));
            Console.WriteLine("raw 抽样=" + raw.Count + " 非空=" + nonEmpty);
            var longest = runs.OrderByDescending(r => r.DurMs).Take(3);
            foreach (var r in longest) Console.WriteLine("  最长: " + r.GroupName + " " + (r.DurMs / 3600000.0).ToString("F1") + "h state=" + r.State);
        }
        return await RunBench();
    }

    private static string IsoLocal(long ms)
    {
        var utc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMilliseconds(ms);
        return utc.Add(TimeZoneInfo.Local.GetUtcOffset(utc)).ToString("yyyy-MM-dd HH:mm:ss");
    }

    private static async Task<int> RunBench()
    {
        string bin = Path.Combine(Path.GetTempPath(), "t44-a1-4mb.bin");
        if (!File.Exists(bin)) { Console.WriteLine("SKIP: no 4mb slice"); return 0; }
        string chunk = File.ReadAllText(bin, Encoding.UTF8);
        int nl = chunk.Split('\n').Length;
        Console.WriteLine("chunk chars=" + chunk.Length.ToString("N0") + " lines=" + nl.ToString("N0"));
        string db = Path.Combine(Path.GetTempPath(), "t44-bench-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        await RunRecordStore.UseIsolatedForTest(db, (t, ex) => { if (ex != null) Console.WriteLine("[diag] " + t + ": " + ex.Message); });
        RunRecordCollector.ResetForTest();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        RunRecordCollector.Feed("A1", chunk, "better-genshin-impact20261125.log");
        sw.Stop();
        Console.WriteLine("Feed(4MB) elapsed=" + sw.Elapsed.TotalSeconds.ToString("F2") + "s");
        await RunRecordStore.FlushAsync();
        var runs = await RunRecordStore.QueryRunsAsync("A1", 7);
        Console.WriteLine("runs=" + runs.Count);
        // t44：验证 raw_text 不再为空（RawText 修复）
        var raw = await RunRecordStore.QueryRawEventsAsync("A1", null, 5);
        int nonEmpty = System.Linq.Enumerable.Count(raw, r => !string.IsNullOrEmpty(r.RawText));
        Console.WriteLine("raw sample=" + raw.Count + " nonEmptyRawText=" + nonEmpty
            + " first=" + (raw.Count > 0 ? (raw[0].RawText ?? "").Substring(0, Math.Min(60, (raw[0].RawText ?? "").Length)) : "-"));
        return 0;
    }
}
