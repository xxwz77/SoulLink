using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace SoulLink.Services
{
    /// <summary>
    /// 采集游标持久化（t45 统一化采集）。独立 SQLite 文件，不碰 RunRecordStore 的 run.db
    /// 表结构（t44 正在改那里）——这里自己建 ingest_cursor 表。
    ///
    /// 数据库：&lt;DataDir&gt;/run-db/ingest.db（与 run.db 同目录、不同文件，更新包不包含）。
    ///
    /// ── 为什么单独立库 ─────────────────────────────────────────────
    ///   RunRecordStore 的表结构由 t44 并行改动；共享同一 DB 会让两个任务在
    ///   EnsureSchema 上互相踩。单独立库 + 独立表，零冲突。
    ///
    /// ── 幂等与节流 ─────────────────────────────────────────────────
    ///   游标写盘是「位置推进」不是「业务数据」，丢了最多多扫一段（幂等兜底），
    ///   所以允许节流/批量写，不必每 4MB 一次。阈值：推进 ≥1MB 或距上次 ≥30s。
    /// </summary>
    public static class IngestCursorStore
    {
        // ── 节流参数 ────────────────────────────────────────────────
        /// <summary>距上次持久化推进 ≥ 该字节数才写盘（避免每 4MB 一次 IO）。</summary>
        private const long PersistAdvanceBytes = 1_048_576;   // 1 MB
        /// <summary>距上次持久化 ≥ 该秒数也强制写盘（即使推进不足 1MB）。</summary>
        private const long PersistMaxDelaySeconds = 30;

        /// <summary>数据库文件路径（独立于 run.db）。</summary>
        private static string DbPath => Path.Combine(AppIdentity.DataDir, "run-db", "ingest.db");

        // 测试隔离：临时目录替换数据库路径（仅测试工程在 %TEMP% 副本上跑）。
        internal static string? TestDbPath;

        private static readonly object Gate = new();
        private static SqliteConnection? _conn;

        // 内存缓存：账号|文件 → 上次已落盘的 (cursor, ticks, wallMs)
        private sealed class SavedState
        {
            public long Cursor;
            public long WriteTicks;
            public long SavedWallMs;   // 最近一次写盘墙钟（Unix ms）
        }
        private static readonly Dictionary<string, SavedState> Saved
            = new(StringComparer.OrdinalIgnoreCase);

        private static string Key(string account, string srcFile)
            => (account ?? "") + "\u001f" + BaseName(srcFile);

        private static string BaseName(string p)
            => string.IsNullOrWhiteSpace(p) ? "" : Path.GetFileName(p);

        /// <summary>读回某账号+文件的游标；无记录返回 -1。</summary>
        public static long LoadCursor(string account, string srcFile)
        {
            if (string.IsNullOrWhiteSpace(account)) return -1;
            try
            {
                lock (Gate)
                {
                    var db = Open();
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = "SELECT cursor FROM ingest_cursor WHERE account=$a AND src_file=$f";
                    cmd.Parameters.AddWithValue("$a", account);
                    cmd.Parameters.AddWithValue("$f", BaseName(srcFile));
                    var v = cmd.ExecuteScalar();
                    if (v == null || v is DBNull) return -1;
                    return Convert.ToInt64(v);
                }
            }
            catch { return -1; }
        }

        /// <summary>
        /// 推进游标（节流写盘）。只在「推进 ≥1MB 或距上次 ≥30s」时真正落盘。
        /// 幂等：同 account+file 的 cursor 只前进不回退（除非显式 Reset）。
        /// </summary>
        public static void Advance(string account, string srcFile, long cursor, long fileLength, long writeTicks)
        {
            if (string.IsNullOrWhiteSpace(account) || cursor < 0) return;
            string key = Key(account, srcFile);
            long now = UnixMs();
            lock (Gate)
            {
                if (!Saved.TryGetValue(key, out var st))
                {
                    st = new SavedState { Cursor = -1, WriteTicks = 0, SavedWallMs = 0 };
                    Saved[key] = st;
                }
                long advance = cursor - st.Cursor;
                bool due = advance >= PersistAdvanceBytes || (now - st.SavedWallMs) >= PersistMaxDelaySeconds * 1000;
                if (!due) return;
                try
                {
                    Upsert(account, BaseName(srcFile), cursor, fileLength, writeTicks);
                    st.Cursor = cursor;
                    st.WriteTicks = writeTicks;
                    st.SavedWallMs = now;
                }
                catch { /* 游标写失败不阻塞解析主流程；下次再试 */ }
            }
        }

        /// <summary>强制落盘（退出前调用，保证断点不丢）。</summary>
        public static void Flush(string account, string srcFile, long cursor, long fileLength, long writeTicks)
        {
            if (string.IsNullOrWhiteSpace(account)) return;
            string key = Key(account, srcFile);
            lock (Gate)
            {
                try { Upsert(account, BaseName(srcFile), cursor, fileLength, writeTicks); }
                catch { }
                if (Saved.TryGetValue(key, out var st)) { st.Cursor = cursor; st.WriteTicks = writeTicks; st.SavedWallMs = UnixMs(); }
            }
        }

        /// <summary>轮转/文件变小：显式重置游标为 0，并保留一条轮转痕迹。</summary>
        public static void Reset(string account, string srcFile)
        {
            if (string.IsNullOrWhiteSpace(account)) return;
            string key = Key(account, srcFile);
            lock (Gate)
            {
                try { Upsert(account, BaseName(srcFile), 0, 0, 0); }
                catch { }
                Saved[key] = new SavedState { Cursor = 0, WriteTicks = 0, SavedWallMs = UnixMs() };
            }
        }

        /// <summary>全量状态（可观测 API 用）：每账号+文件的 cursor/长度/更新时间。</summary>
        public static IReadOnlyList<IngestCursorStatus> All()
        {
            var list = new List<IngestCursorStatus>();
            try
            {
                lock (Gate)
                {
                    var db = Open();
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = "SELECT account, src_file, cursor, file_length, file_write_ticks, updated_at_ms, first_seen_ms FROM ingest_cursor ORDER BY account, src_file";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        list.Add(new IngestCursorStatus(
                            r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3),
                            r.GetInt64(4), r.GetInt64(5), r.GetInt64(6)));
                    }
                }
            }
            catch { }
            return list;
        }

        /// <summary>测试隔离：切换到临时 DB 并清空内存缓存。</summary>
        internal static void UseIsolatedForTest(string path)
        {
            lock (Gate)
            {
                Close();
                TestDbPath = path;
                Saved.Clear();
            }
        }

        internal static void ShutdownForTest()
        {
            lock (Gate) { Close(); TestDbPath = null; Saved.Clear(); }
        }

        private static void Close()
        {
            try { _conn?.Dispose(); } catch { }
            _conn = null;
        }

        private static SqliteConnection Open()
        {
            lock (Gate)
            {
                if (_conn != null) return _conn;
                string path = Path.GetFullPath(TestDbPath ?? DbPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var csb = new SqliteConnectionStringBuilder
                {
                    DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate,
                    Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 5
                };
                var db = new SqliteConnection(csb.ToString());
                db.Open();
                using (var pragma = db.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                    pragma.ExecuteNonQuery();
                }
                EnsureSchema(db);
                _conn = db;
                return db;
            }
        }

        private static void EnsureSchema(SqliteConnection db)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS ingest_cursor (
 account TEXT NOT NULL,
 src_file TEXT NOT NULL,
 cursor INTEGER NOT NULL DEFAULT 0,
 file_length INTEGER NOT NULL DEFAULT 0,
 file_write_ticks INTEGER NOT NULL DEFAULT 0,
 updated_at_ms INTEGER NOT NULL DEFAULT 0,
 first_seen_ms INTEGER NOT NULL DEFAULT 0,
 PRIMARY KEY(account, src_file)
);";
            cmd.ExecuteNonQuery();
        }

        private static void Upsert(string account, string srcFile, long cursor, long fileLength, long writeTicks)
        {
            var db = Open();
            long now = UnixMs();
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
INSERT INTO ingest_cursor(account, src_file, cursor, file_length, file_write_ticks, updated_at_ms, first_seen_ms)
VALUES($a,$f,$c,$l,$w,$u,$u)
ON CONFLICT(account, src_file) DO UPDATE SET
 cursor=$c, file_length=$l, file_write_ticks=$w, updated_at_ms=$u;";
            cmd.Parameters.AddWithValue("$a", account);
            cmd.Parameters.AddWithValue("$f", srcFile);
            cmd.Parameters.AddWithValue("$c", cursor);
            cmd.Parameters.AddWithValue("$l", fileLength);
            cmd.Parameters.AddWithValue("$w", writeTicks);
            cmd.Parameters.AddWithValue("$u", now);
            cmd.ExecuteNonQuery();
        }

        private static long UnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>ingest_cursor 的可观测投影（/api/bgi/ingest/status）。</summary>
    public sealed record IngestCursorStatus(
        string Account, string SrcFile, long Cursor, long FileLength,
        long FileWriteTicks, long UpdatedAtMs, long FirstSeenMs)
    {
        /// <summary>待补字节数（当前文件长度 - 已读游标）。</summary>
        public long PendingBytes => Math.Max(0, FileLength - Cursor);
    }
}
