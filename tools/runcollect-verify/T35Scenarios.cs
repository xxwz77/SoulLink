using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SoulLink.Services;

internal static class T35Scenarios
{
    private static int _fail;
    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "  PASS " : "  FAIL ") + name);
        if (!ok) _fail++;
    }

    public static async Task<int> RunAll()
    {
        _fail = 0;
        await ScenarioCrossChunkCollision();
        await ScenarioA4MultiChunk();
        await ScenarioRescanIdempotent();
        await ScenarioOldDbMigration();
        return _fail;
    }

    private static string Db()
        => Path.Combine(Path.GetTempPath(), "t35-" + Guid.NewGuid().ToString("N")[..8] + ".db");

    private static async Task<int> ScenarioCrossChunkCollision()
    {
        Console.WriteLine("==== T35-1 跨 chunk 碰撞 ====");
        string db = Db();
        await RunRecordStore.UseIsolatedForTest(db, (t, ex) => { if (ex != null) Console.WriteLine("[diag] " + t + ": " + ex.Message); });
        RunRecordCollector.ResetForTest();
        string f = "better-genshin-impact20261124.log";
        // 两 chunk 结构完全相同：同 lineStart、同 ts（23:30:00）、同物品、同 kind。
        // 组行也用 23:30（避免同文本重喂时 23:00 触发天轴跨日回绕的无关副作用）。
        string chunk = "[23:30:00.000] [INF] [Primary:S3:P1:T1] ScriptService\n"
                     + "配置组 \"碰撞组\" 加载完成，共1个脚本，开始执行\n"
                     + "[23:30:00.000] [INF] [Primary:S3:P1:T1] Pickup\n"
                     + "交互或拾取：\"游医\"\n";
        RunRecordCollector.Feed("A4", chunk, f);
        // feed2：同结构同 lineStart 同 ts 同 kind（跨 chunk 碰撞用例本体）
        RunRecordCollector.Feed("A4", chunk, f);
        await RunRecordStore.FlushAsync();

        var runs = (await RunRecordStore.QueryRunsAsync("A4", 7)).Where(r => r.GroupName == "碰撞组").ToList();
        var run = runs.FirstOrDefault();
        Check(run != null, "run 存在");
        if (run != null)
        {
            var picks = await RunRecordStore.QueryPickEventsAsync(run.RunId);
            var picked = picks.Where(p => p.Kind == "picked").ToList();
            Console.WriteLine("  picked pick_event 行数=" + picked.Count + "（chunkOffset: " + string.Join(",", picked.Select(p => p.ChunkOffset)) + "）");
            Check(picked.Count == 2, "★ 两 chunk 同 lineStart+同 ts+同 kind → 两条都落库（修复前 1 条）");
            if (picked.Count == 2)
                Check(picked[0].ChunkOffset != picked[1].ChunkOffset, "两条 chunk_offset 不同（去重维度生效）");
            // 逐物品计数（durable 层=pick_event 明细）：游医×2（同组重跑语义下 run.Picks 归属最新内存窗口，不作断言口径）
            Check(picked.Count(p => p.Item == "游医") == 2, "逐物品计数不变：游医 pick_event=2");
        }
        return _fail;
    }

    private static string? _rescanDb; private static string? _rescanFile;

    private static async Task<int> ScenarioA4MultiChunk()
    {
        Console.WriteLine("==== T35-2 A4 分段喂入 ====");
        string path = @"D:\AAA挂机专区\A4\BetterGI\log\better-genshin-impact20261124.log";
        if (!File.Exists(path)) { Console.WriteLine("  SKIP: 日志不存在"); return 0; }
        string db = Db();
        await RunRecordStore.UseIsolatedForTest(db, (t, ex) => { });
        RunRecordCollector.ResetForTest();

        int logPicked = 0, logNoTarget = 0, logSkipped = 0;
        var all = new List<string>();
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan))
        using (var sr = new StreamReader(fs, Encoding.UTF8))
        {
            string? l;
            while ((l = sr.ReadLine()) != null)
            {
                all.Add(l);
                if (l.Contains("交互或拾取：\"") || l.Contains("交互或拾取:\"")) logPicked++;
                else if (l.Contains("无有效结果，不交互")) logNoTarget++;
                else if (l.Contains("，不交互") && l.Contains("识别到\"")) logSkipped++;
            }
        }
        int logTotal = logPicked + logNoTarget + logSkipped;
        Console.WriteLine("  日志现算：picked=" + logPicked + " no_target=" + logNoTarget + " skipped=" + logSkipped + " 合计=" + logTotal);

        var sb = new StringBuilder();
        string file = Path.GetFileName(path);
        for (int i = 0; i < all.Count; i++)
        {
            sb.Append(all[i]); if (i < all.Count - 1) sb.Append('\n');
            if ((i + 1) % 2000 == 0 || i == all.Count - 1)
            { RunRecordCollector.Feed("A4", sb.ToString(), file); sb.Clear(); }
        }
        await RunRecordStore.FlushAsync();

        var runs = await RunRecordStore.QueryRunsAsync("A4", 30);
        var dbPicks = new List<PickEvent>();
        foreach (var r in runs) dbPicks.AddRange(await RunRecordStore.QueryPickEventsAsync(r.RunId));
        int dbTotal = dbPicks.Count;
        var orphan = RunRecordCollector.GetOrphanPicksForTest("A4");
        int orphanTotal = orphan.picked + orphan.skipped + orphan.noTarget;
        Console.WriteLine("  DB pick_event=" + dbTotal + " + 孤儿=" + orphanTotal + " = " + (dbTotal + orphanTotal) + " vs 日志 " + logTotal);
        Check(dbTotal + orphanTotal == logTotal, "验收3：分段喂入后 pick_event+孤儿 == 三类行总数（无漏行）");

        var byItem = dbPicks.Where(p => p.Kind == "picked")
            .GroupBy(p => p.Item).ToDictionary(g => g.Key, g => g.Count());
        var logByItem = new Dictionary<string, int>();
        foreach (var l in all)
        {
            int q1 = l.IndexOf("交互或拾取：\"");
            if (q1 < 0) q1 = l.IndexOf("交互或拾取:\"");
            if (q1 < 0) continue;
            int s = l.IndexOf('"', q1 + 5);
            if (s < 0) continue;
            int e = l.IndexOf('"', s + 1);
            if (e <= s) continue;
            string item = l.Substring(s + 1, e - s - 1);
            logByItem.TryGetValue(item, out int c); logByItem[item] = c + 1;
        }
        var top = logByItem.OrderByDescending(kv => kv.Value).Take(3).ToList();
        bool itemsMatch = true;
        foreach (var kv in top)
        {
            byItem.TryGetValue(kv.Key, out int dbCnt);
            Console.WriteLine("    " + kv.Key + ": 日志=" + kv.Value + " DB=" + dbCnt);
            if (kv.Value != dbCnt) itemsMatch = false;
        }
        Check(itemsMatch, "验收4：逐物品计数不变（Top3 全等）");
        _rescanDb = db; _rescanFile = file;
        return _fail;
    }

    private static async Task<int> ScenarioRescanIdempotent()
    {
        Console.WriteLine("==== T35-3 重扫幂等 ====");
        string path = @"D:\AAA挂机专区\A4\BetterGI\log\better-genshin-impact20261124.log";
        if (!File.Exists(path) || _rescanDb == null) { Console.WriteLine("  SKIP"); return 0; }
        var before = await RunRecordStore.QueryRunsAsync("A4", 30);
        int beforePicks = 0;
        foreach (var r in before) beforePicks += (await RunRecordStore.QueryPickEventsAsync(r.RunId)).Count;

        RunRecordCollector.ResetForTest();
        // 与首扫完全相同的分块（重启后游标=0 从头按同尺寸重喂 → chunk 序列一致 → 同自然键幂等）
        string[] lines = await File.ReadAllLinesAsync(path, Encoding.UTF8);
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            sb.Append(lines[i]); if (i < lines.Length - 1) sb.Append('\n');
            if ((i + 1) % 2000 == 0 || i == lines.Length - 1)
            { RunRecordCollector.Feed("A4", sb.ToString(), _rescanFile!); sb.Clear(); }
        }
        await RunRecordStore.FlushAsync();

        var after = await RunRecordStore.QueryRunsAsync("A4", 30);
        int afterPicks = 0;
        foreach (var r in after) afterPicks += (await RunRecordStore.QueryPickEventsAsync(r.RunId)).Count;
        Console.WriteLine("  pick_event：重扫前=" + beforePicks + " 重扫后=" + afterPicks);
        Check(afterPicks == beforePicks, "验收5：重扫幂等（行数不变）");
        return _fail;
    }

    private static async Task<int> ScenarioOldDbMigration()
    {
        Console.WriteLine("==== T35-4 旧库迁移 ====");
        string db = Path.Combine(Path.GetTempPath(), "t35-old-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        // 手工建「旧 schema」库：无 chunk_offset + 旧 5 维唯一索引 + 1 行旧数据
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = db, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
        {
            conn.Open();
            Exec(conn, "CREATE TABLE run (run_id TEXT PRIMARY KEY,account TEXT NOT NULL,group_name TEXT NOT NULL," +
                "start_at_ms INTEGER NOT NULL,end_at_ms INTEGER,dur_ms INTEGER NOT NULL DEFAULT 0,state TEXT NOT NULL," +
                "script_count INTEGER NOT NULL DEFAULT 0,exp INTEGER NOT NULL DEFAULT 0,mora INTEGER NOT NULL DEFAULT 0," +
                "picks INTEGER NOT NULL DEFAULT 0,src_file_start TEXT NOT NULL DEFAULT '',src_file_end TEXT NOT NULL DEFAULT ''," +
                "start_day TEXT NOT NULL DEFAULT '',end_day TEXT NOT NULL DEFAULT '',is_cross_day INTEGER NOT NULL DEFAULT 0," +
                "is_backfilled INTEGER NOT NULL DEFAULT 0,detail TEXT NOT NULL DEFAULT '');");
            Exec(conn, "CREATE TABLE pick_event (pick_id INTEGER PRIMARY KEY AUTOINCREMENT," +
                "run_id TEXT NOT NULL REFERENCES run(run_id) ON DELETE CASCADE,route_id TEXT,kind TEXT NOT NULL," +
                "item TEXT NOT NULL DEFAULT '',ts_ms INTEGER NOT NULL,line_no INTEGER NOT NULL," +
                "line_offset INTEGER NOT NULL,src_file TEXT NOT NULL DEFAULT '');");
            Exec(conn, "INSERT INTO run(run_id,account,group_name,start_at_ms,state) VALUES('oldrun','A4','旧组',1000000,'Running')");
            Exec(conn, "INSERT INTO pick_event(run_id,kind,item,ts_ms,line_no,line_offset,src_file) " +
                "VALUES('oldrun','picked','游医',1000000,0,5,'x.log')");
            Exec(conn, "CREATE UNIQUE INDEX ux_pick_dedup ON pick_event(run_id,ts_ms,line_no,line_offset,kind)");
        }
        // 启动 → EnsureSchema 自动迁移（EnsureColumn 加列 + DROP/重建索引）
        await RunRecordStore.UseIsolatedForTest(db, (t, ex) => { if (ex != null) Console.WriteLine("[diag] " + t + ": " + ex.Message); });
        RunRecordCollector.ResetForTest();
        await RunRecordStore.FlushAsync();

        // 断言：列存在、索引 6 维、旧行保留（chunk_offset=0）、重复迁移幂等
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            conn.Open();
            var cols = ReadCol(conn, "PRAGMA table_info(pick_event)", 1);
            Check(cols.Contains("chunk_offset"), "旧库加列 chunk_offset");
            var idxCols = ReadCol(conn, "SELECT name FROM pragma_index_info('ux_pick_dedup')", 0);
            Console.WriteLine("  新索引列：" + string.Join(",", idxCols));
            Check(idxCols.SequenceEqual(new[] { "run_id", "ts_ms", "chunk_offset", "line_no", "line_offset", "kind" }),
                "唯一索引改为 6 维（含 chunk_offset）");
        }
        var old = await RunRecordStore.QueryPickEventsAsync("oldrun");
        Check(old.Count == 1 && old[0].Item == "游医" && old[0].ChunkOffset == 0,
            "旧行保留（chunk_offset=0 默认值）");
        try
        {
            await RunRecordStore.UseIsolatedForTest(db, (t, ex) => { if (ex != null) throw ex!; });
            await RunRecordStore.FlushAsync();
            Check(true, "重复迁移幂等（无异常）");
        }
        catch (Exception ex) { Check(false, "重复迁移幂等：" + ex.Message); }
        return _fail;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static List<string> ReadCol(SqliteConnection conn, string sql, int col)
    {
        var list = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetString(col));
        return list;
    }
}
