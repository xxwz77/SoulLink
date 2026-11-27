using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace SoulLink.Services
{
    public sealed record MiyousheRole(string Account, string GameUid, string Region, string Nickname, int Level);
    public sealed record MiyousheSignResult(string Account, string GameUid, string Date, string State,
        int Retcode, string Message, string RewardName, int RewardCount, long TsMs);
    public sealed record MiyousheLedgerEntry(string Account, string GameUid, string Region, string YearMonth,
        int Kind, int ActionId, string Action, long? Num, long? SingleMora, int? Count, string TimeText, long? TsMs,
        string RawJson, string Source, long FetchedAtMs, int SourceOrdinal = 0);
    public sealed record MiyousheLedgerDay(string Account, string GameUid, string Day, int Kind,
        long? Primogems, long? Mora, string RawJson, string Source, long FetchedAtMs);
    public sealed record MiyousheLedgerMonth(string Account, string GameUid, string YearMonth,
        long? CurrentPrimogems, long? CurrentMora, long? LastPrimogems, long? LastMora,
        int? PrimogemRate, int? MoraRate, string RawJson, string Source, long FetchedAtMs);
    public sealed record MiyousheCredentialState(string Account, string State, string Detail, long UpdatedAtMs);
    public sealed record MiyousheArchive(IReadOnlyList<MiyousheRole> Roles,
        IReadOnlyList<MiyousheLedgerEntry> Entries, IReadOnlyList<MiyousheLedgerDay> Days,
        IReadOnlyList<MiyousheLedgerMonth> Months, IReadOnlyList<MiyousheSignResult> Signs,
        MiyousheCredentialState? CredentialState);

    /// <summary>独立米游社事实库。原始流水不做分类、过滤或击杀数推断。</summary>
    internal sealed class MiyousheStore
    {
        internal static MiyousheStore Shared { get; set; } = new(() =>
            System.IO.Path.Combine(AppIdentity.DataDir, "miyoushe", "miyoushe.db"));
        private readonly Func<string> _path;
        private bool _migrationChecked;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private SqliteConnection? _db;
        private bool _warned;
        private bool _healthy;
        private Exception? _lastFailure;
        internal string LastErrorForTest { get; private set; } = "";        internal bool IsHealthy => _healthy && _db != null;
        internal Exception? LastFailure => _lastFailure;
        internal int ErrorCountForTest { get; private set; }
        internal MiyousheStore(Func<string> path) { _path = path; }

        internal async Task<T> RunAsync<T>(Func<SqliteConnection, T> action, T fallback, CancellationToken ct = default)
        {
            try
            {
                await _gate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }

            Exception? failure = null;
            T result = fallback;
            try
            {
                // SemaphoreSlim.WaitAsync may complete synchronously when the gate is free.
                // Always run SQLite open/query work on a worker so UI callers never execute it inline.
                result = await Task.Run(() =>
                {
                    EnsureOpen();
                    return action(_db!);
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                failure = ex;
                ErrorCountForTest++;
                LastErrorForTest=ex.GetType().Name+": "+ex.Message;
                _healthy=false; _lastFailure=ex;
                try { _db?.Dispose(); } catch { }
                _db = null;
            }
            finally { _gate.Release(); }

            if (failure != null && !_warned)
            {
                _warned = true;
                App.AppendErrorLog("MiyousheStore", new InvalidOperationException("米游社数据库暂不可用"));
            }
            return result;
        }

        private void EnsureOpen()
        {
            if (_db != null) return;
            string path = _path();
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            _db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 5
            }.ToString());
            _db.Open();
            _healthy=true;
            PreserveLegacyTables();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"PRAGMA busy_timeout=5000;PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;PRAGMA foreign_keys=ON;
CREATE TABLE IF NOT EXISTS my_role(account TEXT NOT NULL,game_uid TEXT NOT NULL,region TEXT NOT NULL,
 nickname TEXT NOT NULL DEFAULT '',level INTEGER NOT NULL DEFAULT 0,raw_json TEXT NOT NULL DEFAULT '{}',
 fetched_at_ms INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(account,game_uid));
CREATE TABLE IF NOT EXISTS my_sign_log(id INTEGER PRIMARY KEY AUTOINCREMENT,account TEXT NOT NULL,
 game_uid TEXT NOT NULL DEFAULT '',sign_date TEXT NOT NULL,state TEXT NOT NULL,retcode INTEGER NOT NULL DEFAULT 0,
 message TEXT NOT NULL DEFAULT '',reward_name TEXT NOT NULL DEFAULT '',reward_cnt INTEGER NOT NULL DEFAULT 0,
 ts_ms INTEGER NOT NULL,UNIQUE(account,game_uid,sign_date));
CREATE TABLE IF NOT EXISTS my_ledger_entry(account TEXT NOT NULL,game_uid TEXT NOT NULL,region TEXT NOT NULL DEFAULT '',
 year_month TEXT NOT NULL,kind INTEGER NOT NULL,action_id INTEGER NOT NULL,action TEXT NOT NULL DEFAULT '',
 num INTEGER,single_mora INTEGER,count INTEGER,time_text TEXT NOT NULL DEFAULT '',ts_ms INTEGER,raw_json TEXT NOT NULL,source TEXT NOT NULL,
 fetched_date TEXT NOT NULL DEFAULT '',source_ordinal INTEGER NOT NULL DEFAULT 0,
 fetched_at_ms INTEGER NOT NULL,PRIMARY KEY(account,game_uid,kind,action_id,time_text,raw_json,source_ordinal));
CREATE TABLE IF NOT EXISTS my_ledger_day(account TEXT NOT NULL,game_uid TEXT NOT NULL,day TEXT NOT NULL,kind INTEGER NOT NULL,
 primogems INTEGER,mora INTEGER,raw_json TEXT NOT NULL,source TEXT NOT NULL,fetched_at_ms INTEGER NOT NULL,
 PRIMARY KEY(account,game_uid,day));
CREATE TABLE IF NOT EXISTS my_ledger_month(account TEXT NOT NULL,game_uid TEXT NOT NULL,year_month TEXT NOT NULL,
 current_primogems INTEGER,current_mora INTEGER,last_primogems INTEGER,last_mora INTEGER,
 primogem_rate INTEGER,mora_rate INTEGER,raw_json TEXT NOT NULL,source TEXT NOT NULL,fetched_at_ms INTEGER NOT NULL,
 PRIMARY KEY(account,game_uid,year_month));
CREATE TABLE IF NOT EXISTS my_cred_state(account TEXT PRIMARY KEY,state TEXT NOT NULL DEFAULT 'ok',
 detail TEXT NOT NULL DEFAULT '',updated_at_ms INTEGER NOT NULL);
CREATE INDEX IF NOT EXISTS ix_my_entry_account_time ON my_ledger_entry(account,game_uid,ts_ms);
CREATE TABLE IF NOT EXISTS my_pull_log(batch_id TEXT PRIMARY KEY,account TEXT NOT NULL,game_uid TEXT NOT NULL,
state TEXT NOT NULL,detail TEXT NOT NULL DEFAULT '',pages INTEGER NOT NULL DEFAULT 0,inserted INTEGER NOT NULL DEFAULT 0,
started_at_ms INTEGER NOT NULL,updated_at_ms INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS my_sign_batch(batch_id TEXT PRIMARY KEY,account TEXT NOT NULL,state TEXT NOT NULL,
started_at_ms INTEGER NOT NULL,ended_at_ms INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS my_sign_task(batch_id TEXT NOT NULL,account TEXT NOT NULL,region TEXT NOT NULL,task_id TEXT NOT NULL,
game_uid TEXT NOT NULL DEFAULT '',state TEXT NOT NULL,retcode INTEGER NOT NULL DEFAULT 0,detail TEXT NOT NULL DEFAULT '',
attempts INTEGER NOT NULL DEFAULT 0,duration_ms INTEGER NOT NULL DEFAULT 0,ts_ms INTEGER NOT NULL,
PRIMARY KEY(batch_id,account,region,task_id));
CREATE INDEX IF NOT EXISTS ix_my_sign_task_account ON my_sign_task(account,ts_ms);
CREATE INDEX IF NOT EXISTS ix_my_sign_date ON my_sign_log(account,sign_date);";
            cmd.ExecuteNonQuery();
            EnsureColumn("my_role", "raw_json", "TEXT NOT NULL DEFAULT '{}'");
            EnsureColumn("my_role", "fetched_at_ms", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("my_ledger_entry", "source_ordinal", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("my_ledger_entry", "fetched_date", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn("my_ledger_entry", "single_mora", "INTEGER");
            EnsureColumn("my_ledger_entry", "count", "INTEGER");
            EnsureColumn("my_ledger_month", "year_month", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn("my_ledger_month", "raw_json", "TEXT NOT NULL DEFAULT '{}'");
            EnsureColumn("my_ledger_month", "source", "TEXT NOT NULL DEFAULT 'ys_ledger/monthInfo'");
            if (!_migrationChecked) { CopyLegacyFacts(); _migrationChecked=true; }
            cmd.CommandText = "PRAGMA user_version=4;";
            cmd.ExecuteNonQuery();
        }

        private void PreserveLegacyTables()
        {
            // t14 草稿月表只有 month(1..12)，不能冒充带年份的新事实；改名冷备再迁移。
            if (HasColumn("my_ledger_month","month") && !HasColumn("my_ledger_month","year_month"))
                Exec("ALTER TABLE my_ledger_month RENAME TO my_ledger_month_legacy;");
            if (HasTable("my_ledger_day") && !HasColumn("my_ledger_day","raw_json"))
                Exec("ALTER TABLE my_ledger_day RENAME TO my_ledger_day_legacy;");
        }
        private void CopyLegacyFacts()
        {
            if (HasTable("my_ledger_month_legacy"))
            {
                EnsureColumn("my_ledger_month","year_month","TEXT NOT NULL DEFAULT ''");
                Exec(@"INSERT OR IGNORE INTO my_ledger_month(account,game_uid,year_month,current_primogems,current_mora,
last_primogems,last_mora,primogem_rate,mora_rate,raw_json,source,fetched_at_ms)
SELECT account,game_uid,'legacy-unknown-year-'||printf('%02d',month),current_primogems,current_mora,
last_primogems,last_mora,primogem_rate,mora_rate,categories,'legacy/my_ledger_month',fetched_at_ms FROM my_ledger_month_legacy;");
            }
            if (HasTable("my_ledger_day_legacy"))
            {
                Exec(@"INSERT OR IGNORE INTO my_ledger_day(account,game_uid,day,kind,primogems,mora,raw_json,source,fetched_at_ms)
SELECT account,game_uid,day,kind,CASE WHEN kind='primogem' THEN amount ELSE NULL END,
CASE WHEN kind='mora' THEN amount ELSE NULL END,'{}','legacy/my_ledger_day',ts_ms FROM my_ledger_day_legacy;");
            }
        }
        private bool HasTable(string table)
        {
            using var cmd=_db!.CreateCommand();cmd.CommandText="SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$n;";
            cmd.Parameters.AddWithValue("$n",table);return Convert.ToInt32(cmd.ExecuteScalar())>0;
        }
        private bool HasColumn(string table,string column)
        {
            if(!HasTable(table))return false;
            using var cmd=_db!.CreateCommand();cmd.CommandText="PRAGMA table_info("+table+");";
            using var reader=cmd.ExecuteReader();while(reader.Read())if(reader.GetString(1).Equals(column,StringComparison.OrdinalIgnoreCase))return true;
            return false;
        }
        private void Exec(string sql){using var cmd=_db!.CreateCommand();cmd.CommandText=sql;cmd.ExecuteNonQuery();}

        private void EnsureColumn(string table, string column, string ddl)
        {
            bool exists = false;
            using (var cmd = _db!.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(" + table + ");";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) exists = true;
            }
            if (exists) return;
            using var alter = _db.CreateCommand();
            alter.CommandText = "ALTER TABLE " + table + " ADD COLUMN " + column + " " + ddl + ";";
            alter.ExecuteNonQuery();
        }
        internal Task<int> SaveRoleAsync(MiyousheRole role, string raw, long fetchedAt, CancellationToken ct = default)
            => RunAsync(db =>
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"INSERT INTO my_role(account,game_uid,region,nickname,level,raw_json,fetched_at_ms)
VALUES($a,$u,$r,$n,$l,$raw,$at) ON CONFLICT(account,game_uid) DO UPDATE SET region=excluded.region,
nickname=excluded.nickname,level=excluded.level,raw_json=excluded.raw_json,fetched_at_ms=excluded.fetched_at_ms;";
                Add(cmd,"$a",role.Account); Add(cmd,"$u",role.GameUid); Add(cmd,"$r",role.Region);
                Add(cmd,"$n",role.Nickname); Add(cmd,"$l",role.Level); Add(cmd,"$raw",raw); Add(cmd,"$at",fetchedAt);
                return cmd.ExecuteNonQuery();
            }, 0, ct);

        internal Task<int> SaveSignAsync(MiyousheSignResult row, CancellationToken ct = default)
            => RunAsync(db =>
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"INSERT INTO my_sign_log(account,game_uid,sign_date,state,retcode,message,
reward_name,reward_cnt,ts_ms) VALUES($a,$u,$d,$s,$r,$m,$rn,$rc,$ts)
ON CONFLICT(account,game_uid,sign_date) DO UPDATE SET state=excluded.state,retcode=excluded.retcode,
message=excluded.message,reward_name=excluded.reward_name,reward_cnt=excluded.reward_cnt,ts_ms=excluded.ts_ms
WHERE my_sign_log.state NOT IN ('success','already');";
                Add(cmd,"$a",row.Account); Add(cmd,"$u",row.GameUid); Add(cmd,"$d",row.Date);
                Add(cmd,"$s",row.State); Add(cmd,"$r",row.Retcode); Add(cmd,"$m",row.Message);
                Add(cmd,"$rn",row.RewardName); Add(cmd,"$rc",row.RewardCount); Add(cmd,"$ts",row.TsMs);
                return cmd.ExecuteNonQuery();
            }, 0, ct);

        internal Task<bool> HasSignAsync(string account, string uid, string date, CancellationToken ct = default)
            => RunAsync(db =>
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"SELECT EXISTS(SELECT 1 FROM my_sign_log WHERE account=$a AND game_uid=$u
AND sign_date=$d AND state IN('success','already'));";
                Add(cmd,"$a",account); Add(cmd,"$u",uid); Add(cmd,"$d",date);
                return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
            }, false, ct);

        internal Task<MiyousheCredentialState?> GetCredentialStateAsync(string account, CancellationToken ct)
            => RunAsync(db => ReadCredentialState(db, account), null, ct);

        private static MiyousheCredentialState? ReadCredentialState(SqliteConnection db, string account)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT account,state,detail,updated_at_ms FROM my_cred_state WHERE account=$a;";
            Add(cmd,"$a",account);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetInt64(3)) : null;
        }

        internal Task SetCredentialStateAsync(string account, string state, string detail, long now, CancellationToken ct = default)
            => RunAsync(db =>
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"INSERT INTO my_cred_state(account,state,detail,updated_at_ms) VALUES($a,$s,$d,$at)
ON CONFLICT(account) DO UPDATE SET state=excluded.state,detail=excluded.detail,updated_at_ms=excluded.updated_at_ms;";
                Add(cmd,"$a",account); Add(cmd,"$s",state); Add(cmd,"$d",detail); Add(cmd,"$at",now);
                return cmd.ExecuteNonQuery();
            }, 0, ct);

        internal Task<int> SaveLedgerMonthAsync(MiyousheLedgerMonth row, CancellationToken ct = default)
            => RunAsync(db =>
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"INSERT INTO my_ledger_month(account,game_uid,year_month,current_primogems,current_mora,
last_primogems,last_mora,primogem_rate,mora_rate,raw_json,source,fetched_at_ms)
VALUES($a,$u,$ym,$p,$m,$lp,$lm,$pr,$mr,$raw,$src,$at)
ON CONFLICT(account,game_uid,year_month) DO UPDATE SET current_primogems=excluded.current_primogems,
current_mora=excluded.current_mora,last_primogems=excluded.last_primogems,last_mora=excluded.last_mora,
primogem_rate=excluded.primogem_rate,mora_rate=excluded.mora_rate,raw_json=excluded.raw_json,
source=excluded.source,fetched_at_ms=excluded.fetched_at_ms;";
                Add(cmd,"$a",row.Account); Add(cmd,"$u",row.GameUid); Add(cmd,"$ym",row.YearMonth);
                Add(cmd,"$p",row.CurrentPrimogems); Add(cmd,"$m",row.CurrentMora); Add(cmd,"$lp",row.LastPrimogems);
                Add(cmd,"$lm",row.LastMora); Add(cmd,"$pr",row.PrimogemRate); Add(cmd,"$mr",row.MoraRate);
                Add(cmd,"$raw",row.RawJson); Add(cmd,"$src",row.Source); Add(cmd,"$at",row.FetchedAtMs);
                return cmd.ExecuteNonQuery();
            }, 0, ct);
        internal Task<int> SaveLedgerFactsAsync(IReadOnlyList<MiyousheLedgerEntry> entries,
            IReadOnlyList<MiyousheLedgerDay> days, CancellationToken ct = default)
            => RunAsync(db =>
            {
                using var tx = db.BeginTransaction();
                int inserted = 0;
                foreach (var row in entries)
                {
                    using var cmd = db.CreateCommand(); cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT OR IGNORE INTO my_ledger_entry(account,game_uid,region,year_month,kind,
action_id,action,num,single_mora,count,time_text,ts_ms,raw_json,source,fetched_at_ms,fetched_date,source_ordinal)
VALUES($a,$u,$r,$ym,$k,$ai,$an,$n,$sm,$cnt,$tt,$ts,$raw,$src,$at,$fd,$ord);";
                    Add(cmd,"$a",row.Account); Add(cmd,"$u",row.GameUid); Add(cmd,"$r",row.Region);
                    Add(cmd,"$ym",row.YearMonth); Add(cmd,"$k",row.Kind); Add(cmd,"$ai",row.ActionId);
                    Add(cmd,"$an",row.Action); Add(cmd,"$n",row.Num); Add(cmd,"$sm",row.SingleMora); Add(cmd,"$cnt",row.Count);
                    Add(cmd,"$tt",row.TimeText); Add(cmd,"$ts",row.TsMs);
                    Add(cmd,"$raw",row.RawJson); Add(cmd,"$src",row.Source); Add(cmd,"$at",row.FetchedAtMs);
                    Add(cmd,"$fd",row.TsMs.HasValue?DateTimeOffset.FromUnixTimeMilliseconds(row.TsMs.Value).LocalDateTime.ToString("yyyy-MM-dd"):"");
                    Add(cmd,"$ord",row.SourceOrdinal);
                    inserted += cmd.ExecuteNonQuery();
                }
                foreach (var row in days)
                {
                    using var cmd = db.CreateCommand(); cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT INTO my_ledger_day(account,game_uid,day,kind,primogems,mora,raw_json,source,fetched_at_ms)
VALUES($a,$u,$d,$k,$p,$m,$raw,$src,$at) ON CONFLICT(account,game_uid,day) DO UPDATE SET
primogems=COALESCE(excluded.primogems,my_ledger_day.primogems),mora=COALESCE(excluded.mora,my_ledger_day.mora),raw_json=excluded.raw_json,source=excluded.source,fetched_at_ms=excluded.fetched_at_ms;";
                    Add(cmd,"$a",row.Account); Add(cmd,"$u",row.GameUid); Add(cmd,"$d",row.Day); Add(cmd,"$k",row.Kind);
                    Add(cmd,"$p",row.Primogems); Add(cmd,"$m",row.Mora); Add(cmd,"$raw",row.RawJson);
                    Add(cmd,"$src",row.Source); Add(cmd,"$at",row.FetchedAtMs); cmd.ExecuteNonQuery();
                }
                tx.Commit(); return inserted;
            }, 0, ct);

        internal Task<IReadOnlyList<string>> ListAccountsAsync(CancellationToken ct = default)
            => RunAsync<IReadOnlyList<string>>(db =>
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"SELECT account FROM my_role UNION SELECT account FROM my_cred_state
UNION SELECT account FROM my_sign_log ORDER BY account COLLATE NOCASE;";
                var list = new List<string>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) list.Add(reader.GetString(0));
                return list;
            }, Array.Empty<string>(), ct);

        internal async Task<MiyousheArchive> QueryAsync(string account, int limit = 5000, CancellationToken ct = default)
        {
            limit = Math.Clamp(limit, 1, 50000);
            var roles = await RunAsync<IReadOnlyList<MiyousheRole>>(db =>
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT account,game_uid,region,nickname,level FROM my_role WHERE account=$a ORDER BY game_uid;";
                Add(cmd,"$a",account); var list = new List<MiyousheRole>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) list.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetInt32(4)));
                return list;
            }, Array.Empty<MiyousheRole>(), ct).ConfigureAwait(false);
            var entries = await RunAsync<IReadOnlyList<MiyousheLedgerEntry>>(db =>
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"SELECT account,game_uid,region,year_month,kind,action_id,action,num,single_mora,count,time_text,ts_ms,raw_json,source,fetched_at_ms,source_ordinal
FROM my_ledger_entry WHERE account=$a ORDER BY COALESCE(ts_ms,0) DESC,rowid DESC LIMIT $limit;";
                Add(cmd,"$a",account); Add(cmd,"$limit",limit); var list = new List<MiyousheLedgerEntry>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) list.Add(ReadEntry(reader));
                return list;
            }, Array.Empty<MiyousheLedgerEntry>(), ct).ConfigureAwait(false);
            var days = await RunAsync<IReadOnlyList<MiyousheLedgerDay>>(db =>
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"SELECT account,game_uid,day,kind,primogems,mora,raw_json,source,fetched_at_ms
FROM my_ledger_day WHERE account=$a ORDER BY day DESC LIMIT $limit;";
                Add(cmd,"$a",account); Add(cmd,"$limit",limit); var list = new List<MiyousheLedgerDay>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) list.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetInt32(3),
                    OptLong(reader,4),OptLong(reader,5),reader.GetString(6),reader.GetString(7),reader.GetInt64(8)));
                return list;
            }, Array.Empty<MiyousheLedgerDay>(), ct).ConfigureAwait(false);
            var months = await RunAsync<IReadOnlyList<MiyousheLedgerMonth>>(db =>
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"SELECT account,game_uid,year_month,current_primogems,current_mora,last_primogems,last_mora,
primogem_rate,mora_rate,raw_json,source,fetched_at_ms FROM my_ledger_month WHERE account=$a ORDER BY year_month DESC;";
                Add(cmd,"$a",account); var list = new List<MiyousheLedgerMonth>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) list.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),
                    OptLong(reader,3),OptLong(reader,4),OptLong(reader,5),OptLong(reader,6),OptInt(reader,7),OptInt(reader,8),
                    reader.GetString(9),reader.GetString(10),reader.GetInt64(11)));
                return list;
            }, Array.Empty<MiyousheLedgerMonth>(), ct).ConfigureAwait(false);
            var signs = await RunAsync<IReadOnlyList<MiyousheSignResult>>(db =>
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"SELECT account,game_uid,sign_date,state,retcode,message,reward_name,reward_cnt,ts_ms
FROM my_sign_log WHERE account=$a ORDER BY ts_ms DESC LIMIT 400;";
                Add(cmd,"$a",account); var list = new List<MiyousheSignResult>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) list.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),
                    reader.GetInt32(4),reader.GetString(5),reader.GetString(6),reader.GetInt32(7),reader.GetInt64(8)));
                return list;
            }, Array.Empty<MiyousheSignResult>(), ct).ConfigureAwait(false);
            var cred = await GetCredentialStateAsync(account,ct).ConfigureAwait(false);
            return new MiyousheArchive(roles,entries,days,months,signs,cred);
        }

        private static MiyousheLedgerEntry ReadEntry(SqliteDataReader r)
            => new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetInt32(4),r.GetInt32(5),r.GetString(6),
                OptLong(r,7),OptLong(r,8),OptInt(r,9),r.GetString(10),OptLong(r,11),r.GetString(12),r.GetString(13),r.GetInt64(14),r.GetInt32(15));
        private static long? OptLong(SqliteDataReader r,int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
        private static int? OptInt(SqliteDataReader r,int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

        internal Task SavePullStatusAsync(MiyoushePullStatus row,CancellationToken ct=default)
            => RunAsync(db =>
            {
                using var cmd=db.CreateCommand();
                cmd.CommandText=@"INSERT INTO my_pull_log(batch_id,account,game_uid,state,detail,pages,inserted,started_at_ms,updated_at_ms)
VALUES($id,$a,$u,$s,$d,$p,$i,$st,$at) ON CONFLICT(batch_id) DO UPDATE SET state=excluded.state,detail=excluded.detail,
pages=excluded.pages,inserted=excluded.inserted,updated_at_ms=excluded.updated_at_ms;";
                Add(cmd,"$id",row.BatchId); Add(cmd,"$a",row.Account); Add(cmd,"$u",row.GameUid);
                Add(cmd,"$s",row.State); Add(cmd,"$d",row.Detail); Add(cmd,"$p",row.Pages); Add(cmd,"$i",row.Inserted);
                Add(cmd,"$st",row.StartedAtMs); Add(cmd,"$at",row.UpdatedAtMs); return cmd.ExecuteNonQuery();
            },0,ct);

        internal Task<int> SaveBatchAsync(MiyousheBatch row,CancellationToken ct=default)
            => RunAsync(db=>{using var cmd=db.CreateCommand();cmd.CommandText=@"INSERT INTO my_sign_batch(batch_id,account,state,started_at_ms,ended_at_ms)
VALUES($id,$a,$s,$st,$end) ON CONFLICT(batch_id) DO UPDATE SET state=excluded.state,ended_at_ms=excluded.ended_at_ms;";
            Add(cmd,"$id",row.BatchId);Add(cmd,"$a",row.Account);Add(cmd,"$s",row.State);Add(cmd,"$st",row.StartedAtMs);Add(cmd,"$end",row.EndedAtMs);
            return cmd.ExecuteNonQuery();},0,ct);
        internal Task<int> SaveTaskResultAsync(MiyousheTaskExecution row,CancellationToken ct=default)
            => RunAsync(db=>{using var cmd=db.CreateCommand();cmd.CommandText=@"INSERT INTO my_sign_task(batch_id,account,region,task_id,game_uid,state,retcode,detail,attempts,duration_ms,ts_ms)
VALUES($b,$a,$r,$t,$u,$s,$code,$d,$n,$dur,$ts) ON CONFLICT(batch_id,account,region,task_id) DO UPDATE SET state=excluded.state,
retcode=excluded.retcode,detail=excluded.detail,attempts=excluded.attempts,duration_ms=excluded.duration_ms,ts_ms=excluded.ts_ms;";
            Add(cmd,"$b",row.BatchId);Add(cmd,"$a",row.Account);Add(cmd,"$r",row.Region);Add(cmd,"$t",row.TaskId);Add(cmd,"$u",row.GameUid);
            Add(cmd,"$s",row.State);Add(cmd,"$code",row.Retcode);Add(cmd,"$d",row.Detail);Add(cmd,"$n",row.Attempts);Add(cmd,"$dur",row.DurationMs);Add(cmd,"$ts",row.TsMs);
            return cmd.ExecuteNonQuery();},0,ct);
        internal Task<IReadOnlyList<MiyousheTaskExecution>> QueryTasksAsync(string account,int limit=1000,CancellationToken ct=default)
            => RunAsync<IReadOnlyList<MiyousheTaskExecution>>(db=>{using var cmd=db.CreateCommand();cmd.CommandText=@"SELECT batch_id,account,region,task_id,game_uid,state,retcode,detail,attempts,duration_ms,ts_ms
FROM my_sign_task WHERE account=$a ORDER BY ts_ms DESC LIMIT $n;";Add(cmd,"$a",account);Add(cmd,"$n",Math.Clamp(limit,1,10000));
            var list=new List<MiyousheTaskExecution>();using var reader=cmd.ExecuteReader();while(reader.Read())list.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetInt32(6),reader.GetString(7),reader.GetInt32(8),reader.GetInt64(9),reader.GetInt64(10)));return list;},Array.Empty<MiyousheTaskExecution>(),ct);

        internal Task<bool> HasTaskSuccessAsync(string account,string taskId,string day,CancellationToken ct=default)
            => RunAsync(db=>{using var cmd=db.CreateCommand();cmd.CommandText=@"SELECT EXISTS(SELECT 1 FROM my_sign_task
WHERE account=$a AND task_id=$t AND ts_ms>=$start AND ts_ms<$end AND state IN('success','already'));";
            Add(cmd,"$a",account);Add(cmd,"$t",taskId);
            long start=DateTimeOffset.Parse(day+"T00:00:00").ToUnixTimeMilliseconds();Add(cmd,"$start",start);Add(cmd,"$end",start+86400000);
            return Convert.ToInt32(cmd.ExecuteScalar())==1;},false,ct);

        private static void Add(SqliteCommand cmd, string name, object? value)
            => cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}