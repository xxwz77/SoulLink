using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace SoulLink.Services
{
    /// <summary>原始日志行：事实源，不含业务筛选/聚合；raw_truncated 明示是否被限长。</summary>
    public sealed record RawEvent
    {
        public string RawId { get; set; } = "";
        public string Account { get; set; } = "";
        public string SrcFile { get; set; } = "";
        public long ChunkOffset { get; set; }
        public int LineOffset { get; set; }
        public long LineNo { get; set; }
        public long? TsMs { get; set; }
        public string Level { get; set; } = "";
        public string Logger { get; set; } = "";
        public string RawText { get; set; } = "";
        public int RawChars { get; set; }
        public bool RawTruncated { get; set; }
        public string Anchor { get; set; } = "";
        public long CollectedAtMs { get; set; }
        public string ContentHash { get; set; } = "";
        public int Occurrence { get; set; }
        public string SourceDay { get; set; } = "";
        public long ByteOffset { get; set; }
        public int ByteLength { get; set; }
        public long FileSizeAtCollection { get; set; }
        public long FileWriteAtCollectionTicks { get; set; }
    }

    /// <summary>配置组运行记录。RunId 由 <see cref="RunRecordStore.MakeRunId"/> 生成。</summary>
    public sealed record RunRecord
    {
        public string RunId { get; set; } = "";
        public string Account { get; set; } = "";
        public string GroupName { get; set; } = "";
        public long StartAtMs { get; set; }
        public long? EndAtMs { get; set; }
        public long DurMs { get; set; }
        public string State { get; set; } = "Running";
        public int ScriptCount { get; set; }
        public long Exp { get; set; }
        public long Mora { get; set; }
        public int Picks { get; set; }
        public string SrcFileStart { get; set; } = "";
        public string SrcFileEnd { get; set; } = "";
        public string StartDay { get; set; } = "";
        public string EndDay { get; set; } = "";
        public bool IsCrossDay { get; set; }
        public bool IsBackfilled { get; set; }
        /// <summary>t6：计划路线数（只有数量没有名字的锚点落这里）。</summary>
        public int PlannedRoutes { get; set; }
        /// <summary>t6：跨天换文件分段标记（两段都置 1）。</summary>
        public bool DaySplit { get; set; }
        /// <summary>t6：拾取守恒差异（unattributed 数，不为 0 时也在 detail 注明）。</summary>
        public int PickUnattributed { get; set; }

        // ── 轮次（2026-11-26 用户要求：「有轮次要记录轮次，没轮次只记录配置组启动次数」）──
        //   锄地类日志会写 `[第 N/M 轮 房主名]` / `[多世界] 第 N/M 轮，房主: "X"`，
        //   一次配置组启动内部可能循环多轮（用户明确：仍算「1 次运行」，只是带上轮次信息）。
        //   好感/CD采集等无轮次锚点的组，三个字段保持 0/空，界面按「启动次数」展示。
        /// <summary>第几轮（N）；0 = 该组无轮次概念。</summary>
        public int RoundIndex { get; set; }
        /// <summary>共几轮（M）；0 = 该组无轮次概念。</summary>
        public int RoundTotal { get; set; }
        /// <summary>本轮房主名（锄地多世界）；空 = 无。</summary>
        public string RoundHost { get; set; } = "";
        /// <summary>本 run 内实际完成的轮次数（同一启动内可跑多轮）。</summary>
        public int RoundCount { get; set; }

        public string Detail { get; set; } = "";
    }

    /// <summary>脚本或其中的路线。PickDetail 为 {"物品":次数} JSON。</summary>
    public sealed record RouteRecord
    {
        public string RouteId { get; set; } = "";
        public string RunId { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Name { get; set; } = "";
        public string? ParentRouteId { get; set; }
        public long StartAtMs { get; set; }
        public long? EndAtMs { get; set; }
        public long DurMs { get; set; }
        public string State { get; set; } = "Running";
        public int PickCount { get; set; }
        public string PickDetail { get; set; } = "{}";
        public string SrcFile { get; set; } = "";
        /// <summary>t6：picked 计数（=PickCount，语义不变）。</summary>
        public int Picked { get; set; }
        /// <summary>t6：识别到物品但跳过（识别到"X"，不交互）次数。</summary>
        public int Skipped { get; set; }
        /// <summary>t6：无有效结果（没拾到）次数。</summary>
        public int NoTarget { get; set; }
    }

    /// <summary>路线/配置组内的错误。ContentHash 留空时由存储层确定性地计算。</summary>
    public sealed record ErrorRecord
    {
        public long ErrId { get; set; }
        public string RunId { get; set; } = "";
        public string? RouteId { get; set; }
        public long TsMs { get; set; }
        public string Level { get; set; } = "ERR";
        public string ExceptionType { get; set; } = "";
        public string Message { get; set; } = "";
        public string Detail { get; set; } = "";
        public string Source { get; set; } = "";
        public string ContentHash { get; set; } = "";
        public string SrcFile { get; set; } = "";
    }

    /// <summary>t6：拾取事件投影（三类：picked/no_target/skipped，item 原话照抄）。</summary>
    public sealed record PickEvent
    {
        public string RunId { get; set; } = "";
        public string? RouteId { get; set; }
        /// <summary>picked / no_target / skipped。</summary>
        public string Kind { get; set; } = "";
        public string Item { get; set; } = "";
        public long TsMs { get; set; }
        public long LineNo { get; set; }
        public int LineOffset { get; set; }
        public string SrcFile { get; set; } = "";
        /// <summary>
        /// t35：chunk 序号（与 raw_event 的 ChunkOffset 同语义：per user+file 递增）。
        /// ★ LineNo 是 chunk 内偏移，跨 chunk 会重复；缺这一维时唯一索引会把
        /// 「同 ts+同 chunk 内偏移+同 kind」的两条静默吃掉（INSERT OR IGNORE）。
        /// </summary>
        public long ChunkOffset { get; set; }
    }

    /// <summary>t6：BGI 状态时段（三态 running/idle/not_running，只在切换时开/闭段）。</summary>
    public sealed record StateSpan
    {
        public long SpanId { get; set; }
        public string Account { get; set; } = "";
        public string State { get; set; } = "";
        public long StartAtMs { get; set; }
        public long? EndAtMs { get; set; }
        public long DurMs { get; set; }
        public string SrcFile { get; set; } = "";
        /// <summary>t10：段是否仍在进行（end_at_ms IS NULL）。</summary>
        public bool IsOngoing { get; set; }
        /// <summary>
        /// t10：可直接展示的时长——已结束段 = DurMs；进行中段 = now - start（自动算，
        /// 用户原话「不要让用户去猜」：调用方零减法）。
        /// </summary>
        public long DurMsSoFar { get; set; }
    }

    /// <summary>幂等结束配置组；已结束的行不会被重扫覆盖。</summary>
    public sealed record RunClose
    {
        public string RunId { get; set; } = "";
        public long EndAtMs { get; set; }
        public string State { get; set; } = "Finished";
        public string Detail { get; set; } = "";
        public string SrcFileEnd { get; set; } = "";
        public string EndDay { get; set; } = "";
        public bool IsCrossDay { get; set; }
        public bool IsBackfilled { get; set; }
        /// <summary>t6：跨天换文件分段（两段都置 1）。</summary>
        public bool DaySplit { get; set; }
        /// <summary>t6：拾取守恒差异。</summary>
        public int PickUnattributed { get; set; }
        /// <summary>t6：计划路线数（数量型锚点落 run）。</summary>
        public int PlannedRoutes { get; set; }
        /// <summary>轮次（关闭时落最终值；0 = 该组无轮次概念）。</summary>
        public int RoundIndex { get; set; }
        public int RoundTotal { get; set; }
        public string RoundHost { get; set; } = "";
        public int RoundCount { get; set; }
        public long Exp { get; set; }
        public long Mora { get; set; }
        public int Picks { get; set; }
    }

    /// <summary>幂等结束路线，同时保存最终拾取快照。</summary>
    public sealed record RouteClose
    {
        public string RouteId { get; set; } = "";
        public long EndAtMs { get; set; }
        public long DurMs { get; set; }
        public string State { get; set; } = "Finished";
        public int PickCount { get; set; }
        public string PickDetail { get; set; } = "{}";
        /// <summary>t6：三类计数（picked=PickCount 语义不变）。</summary>
        public int Picked { get; set; }
        public int Skipped { get; set; }
        public int NoTarget { get; set; }
    }

    /// <summary>按配置组聚合的运行统计。</summary>
    public sealed record RunGroupSummary
    {
        public string Group { get; set; } = "";
        public int Runs { get; set; }
        public int Failures { get; set; }
        public long AvgExp { get; set; }
        public long AvgMora { get; set; }
        public long TotalExp { get; set; }
        public long TotalMora { get; set; }
        public long TotalPicks { get; set; }
        public long TotalDurMs { get; set; }
        public long? LastEndAtMs { get; set; }
    }

    /// <summary>
    /// 运行记录 SQLite 存储。公共方法均不在调用线程执行文件 IO。
    /// 一个专用后台线程串行执行写入和查询；调用方只入队或 await 查询结果。
    /// 数据库位于 AppIdentity.DataDir/run-db/run.db，更新包不包含该目录。
    /// 集成（Feed、TickHub 和留存设置）由后续任务负责。
    /// </summary>
    public static class RunRecordStore
    {
        private const char KeySeparator = '\u001f';
        private static Lazy<RunRecordStoreCore> Core = CreateDefaultCore();

        private static Lazy<RunRecordStoreCore> CreateDefaultCore() => new(
            () => new RunRecordStoreCore(() => Path.Combine(AppIdentity.DataDir, "run-db", "run.db")),
            LazyThreadSafetyMode.ExecutionAndPublication);

#if RUN_DB_TEST
        // 仅临时验证工程定义 RUN_DB_TEST；产品构建不包含此入口。
        internal static async Task UseIsolatedForTest(string path, Action<string,Exception?> sink)
        {
            if (Core.IsValueCreated) await Core.Value.ShutdownAsync().ConfigureAwait(false);
            DiagnosticSink = sink;
            Core = new(() => new RunRecordStoreCore(() => path), LazyThreadSafetyMode.ExecutionAndPublication);
        }
#endif

        /// <summary>诊断/测试回调。默认在后台线程写 App.AppendErrorLog。</summary>
        public static Action<string, Exception?>? DiagnosticSink { get; set; }

        /// <summary>false 表示已经降级，待 <see cref="RetryDatabase"/> 后重试。</summary>
        public static bool IsHealthy => Core.Value.IsHealthy;

        /// <summary>目标路径；仅供诊断，IO 仍只在后台线程执行。</summary>
        public static string DatabasePath => Path.Combine(AppIdentity.DataDir, "run-db", "run.db");

        public static string MakeRunId(string account, string groupName, long startAtMs)
            => string.Join(KeySeparator, account ?? "", groupName ?? "",
                startAtMs.ToString(CultureInfo.InvariantCulture));

        public static string MakeRouteId(string runId, string kind, string name, long startAtMs)
            => string.Join(KeySeparator, runId ?? "", kind ?? "", name ?? "",
                startAtMs.ToString(CultureInfo.InvariantCulture));

        public static string MakeErrorContentHash(long tsMs, string? level, string? detail)
        {
            string text = tsMs.ToString(CultureInfo.InvariantCulture) + "|" + (level ?? "") + "|" + (detail ?? "");
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
        }

        public static void AddRun(RunRecord row)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.RunId)
                || string.IsNullOrWhiteSpace(row.Account) || string.IsNullOrWhiteSpace(row.GroupName)
                || row.StartAtMs < 0) return;
            var copy = row with
            {
                State = string.IsNullOrWhiteSpace(row.State) ? "Running" : row.State.Trim(),
                StartDay = string.IsNullOrWhiteSpace(row.StartDay) ? DayOf(row.StartAtMs) : row.StartDay,
                EndDay = row.EndAtMs.HasValue && string.IsNullOrWhiteSpace(row.EndDay)
                    ? DayOf(row.EndAtMs.Value) : row.EndDay ?? "",
                DurMs = row.EndAtMs.HasValue
                    ? Math.Max(0, row.EndAtMs.Value - row.StartAtMs) : Math.Max(0, row.DurMs)
            };
            Core.Value.EnqueueWrite(db => WriteRun(db, copy));
        }


        /// <summary>
        /// ★ t55：等待后台写队列排空（最多 timeout）。
        ///
        /// 用途：历史回填按 4MB 分块喂数据，块与块之间必须等写队列消化完 ——
        /// 否则 pick_event 的 run_id 外键会因 run 还没写进去而失败（实测 SqliteException 19）。
        /// 正常 UI/实时路径**不要**调这个（会阻塞）；只给「回填」这种可控批处理用。
        /// </summary>
        public static bool WaitForWritesDrain(TimeSpan timeout)
        {
            try { return Core.Value.WaitForWritesDrain(timeout); } catch { return false; }
        }

        /// <summary>
        /// ★ t63：当前写队列积压条数。用于**非阻塞背压** ——
        /// 回填/批量导入时用它判断「该让出了没」，而不是 Task.Wait 阻塞线程。
        /// 0 表示没有积压（或是降级状态，此时一并当作 0，由 IsHealthy 单独判断）。
        /// </summary>
        public static int PendingWriteCount
        {
            get { try { return Core.Value.PendingWriteCount; } catch { return 0; } }
        }


        /// <summary>
        /// ★ t68：查某账号在指定日期（yyyy-MM-dd，本地时间）有多少条 run 记录。
        ///
        /// 用途：回填前置检查「数据库是否缺失」—— 用户明确要求回填必须有依据：
        ///   「需要日志存在且数据库缺失」。
        /// 只有日志、没有库记录时才回填，不是无脑重复扫描。
        ///
        /// 与 QueryRunsAsync 的区别：这个只 COUNT(*)，不拉数据，开销极小；
        /// 而且是**同步**的（回填扫描在 ServiceTick 里，异步反而不方便）。
        /// </summary>
        public static Task<int> CountRunsOnDayAsync(string account, string day, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(day))
                return Task.FromResult(0);
            try { return Core.Value.CountRunsOnDayAsync(account, day, ct); }
            catch { return Task.FromResult(0); }
        }

        public static void AddRoute(RouteRecord row)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.RouteId) || string.IsNullOrWhiteSpace(row.RunId)
                || string.IsNullOrWhiteSpace(row.Kind) || string.IsNullOrWhiteSpace(row.Name)
                || row.StartAtMs < 0) return;
            var copy = row with { State = string.IsNullOrWhiteSpace(row.State) ? "Running" : row.State.Trim() };
            Core.Value.EnqueueWrite(db => WriteRoute(db, copy));
        }

        public static void AddError(ErrorRecord row)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.RunId) || row.TsMs < 0) return;
            string level = string.IsNullOrWhiteSpace(row.Level) ? "ERR" : row.Level.Trim();
            string message = row.Message ?? "";
            var copy = row with
            {
                Level = level,
                Message = message.Length <= 500 ? message : message[..500],
                ContentHash = string.IsNullOrWhiteSpace(row.ContentHash)
                    ? MakeErrorContentHash(row.TsMs, level, row.Detail) : row.ContentHash.Trim()
            };
            Core.Value.EnqueueWrite(db => WriteError(db, copy));
        }

        /// <summary>原始行入队。只追加/幂等，不做“有效性”筛选；IO 仅后台线程执行。</summary>
        public static void AddRawEvents(IReadOnlyList<RawEvent> rows)
        {
            if (rows == null || rows.Count == 0) return;
            var copy = rows.Where(r => r != null && !string.IsNullOrWhiteSpace(r.RawId)
                && !string.IsNullOrWhiteSpace(r.Account)).Select(r => r with { }).ToArray();
            if (copy.Length == 0) return;
            Core.Value.EnqueueWrite(db => WriteRawEvents(db, copy));
        }

        /// <summary>事实查询：按账号、文件、时间范围取原始行；不做业务筛选。</summary>
        public static Task<IReadOnlyList<RawEvent>> QueryRawEventsAsync(string account,
            string? srcFile = null, int limit = 5000, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(account)) return Task.FromResult<IReadOnlyList<RawEvent>>(Array.Empty<RawEvent>());
            return Core.Value.EnqueueQuery(db =>
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"SELECT raw_id,account,src_file,chunk_offset,line_offset,line_no,ts_ms,level,logger,
 raw_text,raw_chars,raw_truncated,anchor,collected_at_ms,content_hash,occurrence,source_day,byte_offset,byte_length,file_size_at_collection,file_write_at_ticks FROM raw_event
 WHERE account=$account" + (string.IsNullOrEmpty(srcFile) ? "" : " AND src_file=$file") +
 " ORDER BY collected_at_ms,raw_id LIMIT $limit;";
                cmd.Parameters.AddWithValue("$account", account);
                if (!string.IsNullOrEmpty(srcFile)) cmd.Parameters.AddWithValue("$file", srcFile);
                cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100000));
                var result = new List<RawEvent>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) result.Add(new RawEvent
                {
                    RawId = reader.GetString(0), Account = reader.GetString(1), SrcFile = reader.GetString(2),
                    ChunkOffset = reader.GetInt64(3), LineOffset = reader.GetInt32(4), LineNo = reader.GetInt64(5),
                    TsMs = reader.IsDBNull(6) ? null : reader.GetInt64(6), Level = reader.GetString(7),
                    Logger = reader.GetString(8), RawText = reader.GetString(9), RawChars = reader.GetInt32(10),
                    RawTruncated = reader.GetInt64(11) != 0, Anchor = reader.GetString(12),
                    CollectedAtMs = reader.GetInt64(13), ContentHash = reader.GetString(14), Occurrence = reader.GetInt32(15), SourceDay = reader.FieldCount > 16 && !reader.IsDBNull(16) ? reader.GetString(16) : "",
                    ByteOffset = reader.GetInt64(17), ByteLength = reader.GetInt32(18), FileSizeAtCollection = reader.GetInt64(19), FileWriteAtCollectionTicks = reader.GetInt64(20)
                });
                return (IReadOnlyList<RawEvent>)result;
            }, Array.Empty<RawEvent>(), ct);
        }

    /// <summary>网页原始片段：text 只有真实回读成功才有值，缺失/替换/仍在写入均显式标记。</summary>
    public sealed record RawLogExcerpt(RawEvent Event, string Text, string Status, string Detail);
    public sealed record RawLogExcerptResult(IReadOnlyList<RawLogExcerpt> Items, int ElapsedMs, int FileOpens);

    /// <summary>
    /// 按需回读原始日志（后台线程，一次开文件+排序 seek，连续区间合并读取）。
    /// 日常结构化查询不依赖此 API；只服务用户点开日志详情。
    /// </summary>
    public static async Task<RawLogExcerptResult> ReadRawLogExcerptsAsync(
        string account, string? srcFile = null, string? runId = null,
        string? routeId = null, int limit = 500, CancellationToken ct = default)
    {
        IReadOnlyList<RawEvent> events;
        if (runId == null && routeId == null) events = await QueryRawEventsAsync(account, srcFile, limit, ct).ConfigureAwait(false);
        else events = await Core.Value.EnqueueQuery(db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"SELECT DISTINCT e.raw_id,e.account,e.src_file,e.chunk_offset,e.line_offset,e.line_no,
 e.ts_ms,e.level,e.logger,e.raw_text,e.raw_chars,e.raw_truncated,e.anchor,e.collected_at_ms,e.content_hash,e.occurrence,
 e.source_day,e.byte_offset,e.byte_length,e.file_size_at_collection,e.file_write_at_ticks
FROM raw_event e JOIN run r ON r.account=e.account
WHERE r.account=$account
AND (($run<>'' AND r.run_id=$run AND (e.src_file=r.src_file_start OR e.src_file=r.src_file_end))
 OR ($route<>'' AND EXISTS(SELECT 1 FROM route x WHERE x.run_id=r.run_id AND x.route_id=$route AND x.src_file=e.src_file)))
ORDER BY e.chunk_offset,e.line_offset LIMIT $limit;";
            cmd.Parameters.AddWithValue("$account",account); cmd.Parameters.AddWithValue("$run",runId??"");
            cmd.Parameters.AddWithValue("$route",routeId??""); cmd.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,5000));
            var rows=new List<RawEvent>(); using var reader=cmd.ExecuteReader();
            while(reader.Read()) rows.Add(ReadRawEvent(reader));
            return (IReadOnlyList<RawEvent>)rows;
        }, Array.Empty<RawEvent>(), ct).ConfigureAwait(false);
        return await Task.Run(() => ReadExcerpts(events, account, ct), ct).ConfigureAwait(false);
    }

    private static RawEvent ReadRawEvent(SqliteDataReader reader) => new()
    {
        RawId=reader.GetString(0),Account=reader.GetString(1),SrcFile=reader.GetString(2),
        ChunkOffset=reader.GetInt64(3),LineOffset=reader.GetInt32(4),LineNo=reader.GetInt64(5),
        TsMs=reader.IsDBNull(6)?null:reader.GetInt64(6),Level=reader.GetString(7),Logger=reader.GetString(8),
        RawText=reader.GetString(9),RawChars=reader.GetInt32(10),RawTruncated=reader.GetInt64(11)!=0,
        Anchor=reader.GetString(12),CollectedAtMs=reader.GetInt64(13),ContentHash=reader.GetString(14),Occurrence=reader.GetInt32(15),
        SourceDay=reader.IsDBNull(16)?"":reader.GetString(16),ByteOffset=reader.GetInt64(17),ByteLength=reader.GetInt32(18),
        FileSizeAtCollection=reader.GetInt64(19),FileWriteAtCollectionTicks=reader.GetInt64(20)
    };

    private static RawLogExcerptResult ReadExcerpts(IReadOnlyList<RawEvent> events, string account, CancellationToken ct)
    {
        var sw=System.Diagnostics.Stopwatch.StartNew(); var output=new List<RawLogExcerpt>(events.Count); int opens=0;
        foreach(var fileGroup in events.GroupBy(e=>e.SrcFile,StringComparer.OrdinalIgnoreCase))
        {
            string file=fileGroup.Key;
            if(file.Contains('/')||file.Contains('\\')||file==".."||file=="."||file.Length==0)
            { foreach(var e in fileGroup) output.Add(new(e,"","unavailable","原始日志路径无效，仅保留索引信息")); continue; }
            string exe=BgiPathResolver.GetBoundPath(account);
            string path=string.IsNullOrWhiteSpace(exe)?"":Path.Combine(Path.GetDirectoryName(exe)??"","log",file);
            FileInfo info;
            try{info=new FileInfo(path);}catch{info=null!;}
            if(info==null||!info.Exists)
            { foreach(var e in fileGroup) output.Add(new(e,"","missing","原始日志已被清理；索引和结构化事实仍可用")); continue; }
            try
            {
                using var fs=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete); opens++;
                bool changed=(info.Length!=fileGroup.First().FileSizeAtCollection && fileGroup.First().FileSizeAtCollection>0)
                    || (info.LastWriteTimeUtc.Ticks!=fileGroup.First().FileWriteAtCollectionTicks && fileGroup.First().FileWriteAtCollectionTicks>0);
                var pending=new List<(RawEvent Event,long Start,int Length)>(); long pendingEnd=-1;
                void FlushPending()
                {
                    if(pending.Count==0)return;
                    long start=pending[0].Start;
                    byte[] bytes=new byte[pendingEnd-start]; fs.Seek(start,SeekOrigin.Begin); int total=0;
                    while(total<bytes.Length){int n=fs.Read(bytes,total,bytes.Length-total);if(n==0)break;total+=n;}
                    foreach(var (item,itemStart,itemLength) in pending)
                    {
                        int available=Math.Max(0,Math.Min(itemLength,total-(int)(itemStart-start)));
                        string status=changed?"changed":available<itemLength?"growing":"ok";
                        string detail=status=="ok"?"":status=="changed"?"日志文件已变更，原文可能不对应":"日志仍在写入，已读取当前文件末尾";
                        output.Add(new(item,Encoding.UTF8.GetString(bytes,(int)(itemStart-start),available),status,detail));
                    }
                    pending.Clear();
                }
                foreach(var e in fileGroup.OrderBy(x=>x.ByteOffset))
                {
                    ct.ThrowIfCancellationRequested();
                    if(e.ByteLength<=0 && e.RawText.Length>0){output.Add(new(e,e.RawText,"legacy","旧库原文（迁移前记录）"));continue;}
                    if(e.ByteOffset<0||e.ByteLength<=0)
                    { output.Add(new(e,"","growing","日志仍在写入或偏移已失效，无法回读该行")); continue; }
                    if(e.ByteOffset>fs.Length)
                    {
                        // 超出当前文件长度：明确降级，但保留当前末尾可见内容，不把它冒充目标行。
                        int tail=(int)Math.Min(256,fs.Length); byte[] tailBytes=new byte[tail];
                        fs.Seek(fs.Length-tail,SeekOrigin.Begin); fs.ReadExactly(tailBytes);
                        output.Add(new(e,Encoding.UTF8.GetString(tailBytes),"growing","日志仍在写入，目标偏移超界；以下为当前末尾片段"));
                        continue;
                    }
                    int count=(int)Math.Min(e.ByteLength,fs.Length-e.ByteOffset);
                    if(pending.Count>0 && e.ByteOffset==pendingEnd){pending.Add((e,e.ByteOffset,count));pendingEnd=e.ByteOffset+count;continue;}
                    FlushPending();
                    pending.Add((e,e.ByteOffset,count));pendingEnd=e.ByteOffset+count;
                }
                FlushPending();
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
            { foreach(var e in fileGroup) output.Add(new(e,"","unavailable","原始日志暂不可读；索引和结构化事实仍可用")); }
        }
        sw.Stop(); return new(output,(int)sw.ElapsedMilliseconds,opens);
    }

        private static void WriteRawEvents(SqliteConnection db, IReadOnlyList<RawEvent> rows)
        {
            using var tx = db.BeginTransaction();
            using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = @"INSERT OR IGNORE INTO raw_event(raw_id,account,src_file,chunk_offset,line_offset,line_no,
 ts_ms,level,logger,raw_text,raw_chars,raw_truncated,anchor,collected_at_ms,content_hash,occurrence,source_day,byte_offset,byte_length,file_size_at_collection,file_write_at_ticks)
 VALUES($id,$account,$file,$chunk,$lineOffset,$lineNo,$ts,$level,$logger,$raw,$chars,$truncated,$anchor,$collected,$hash,$occurrence,$sourceDay,$byteOffset,$byteLength,$fileSize,$fileWrite);";
            string[] names = { "$id","$account","$file","$chunk","$lineOffset","$lineNo",
                "$ts","$level","$logger","$raw","$chars","$truncated","$anchor","$collected","$hash","$occurrence","$sourceDay","$byteOffset","$byteLength","$fileSize","$fileWrite" };
            foreach (string name in names) cmd.Parameters.Add(name, SqliteType.Text);
            foreach (var row in rows)
            {
                object?[] values = { row.RawId,row.Account,row.SrcFile,row.ChunkOffset,row.LineOffset,row.LineNo,
                    row.TsMs,row.Level,row.Logger,row.RawText,row.RawChars,row.RawTruncated?1:0,
                    row.Anchor,row.CollectedAtMs,row.ContentHash,row.Occurrence,row.SourceDay,row.ByteOffset,row.ByteLength,row.FileSizeAtCollection,row.FileWriteAtCollectionTicks };
                for (int i = 0; i < names.Length; i++) cmd.Parameters[names[i]].Value = values[i] ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        public static void CloseRun(RunClose close)
        {
            if (close == null || string.IsNullOrWhiteSpace(close.RunId) || close.EndAtMs < 0) return;
            var copy = close with { EndDay = string.IsNullOrWhiteSpace(close.EndDay) ? DayOf(close.EndAtMs) : close.EndDay };
            Core.Value.EnqueueWrite(db => WriteRunClose(db, copy));
        }

        public static void CloseRoute(RouteClose close)
        {
            if (close == null || string.IsNullOrWhiteSpace(close.RouteId) || close.EndAtMs < 0) return;
            Core.Value.EnqueueWrite(db => WriteRouteClose(db, close with { }));
        }

        // ── t6：拾取事件（三类投影；t35 自然键加 chunk_offset：run_id+ts+chunk+line_no+offset+kind 幂等）──

        /// <summary>拾取事件投影入库（picked/no_target/skipped；item 原话照抄）。INSERT OR IGNORE 幂等。</summary>
        public static void AddPickEvent(PickEvent row)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.RunId) || row.TsMs < 0
                || string.IsNullOrWhiteSpace(row.Kind)) return;
            Core.Value.EnqueueWrite(db =>
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"INSERT OR IGNORE INTO pick_event
                    (run_id,route_id,kind,item,ts_ms,line_no,line_offset,src_file,chunk_offset)
                    VALUES($run,$route,$kind,$item,$ts,$line,$offset,$src,$chunk);";
                Add(cmd, "$run", row.RunId); Add(cmd, "$route", row.RouteId); Add(cmd, "$kind", row.Kind);
                Add(cmd, "$item", row.Item); Add(cmd, "$ts", row.TsMs);
                Add(cmd, "$line", row.LineNo); Add(cmd, "$offset", row.LineOffset); Add(cmd, "$src", row.SrcFile);
                Add(cmd, "$chunk", row.ChunkOffset);
                cmd.ExecuteNonQuery();
            });
        }

        /// <summary>按 run 取拾取事件（可选路线过滤；三类聚合在查询端零负担）。</summary>
        public static Task<IReadOnlyList<PickEvent>> QueryPickEventsAsync(string runId,
            string? routeId = null, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(runId))
                return Task.FromResult<IReadOnlyList<PickEvent>>(Array.Empty<PickEvent>());
            return Core.Value.EnqueueQuery(db =>
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT run_id,route_id,kind,item,ts_ms,line_no,line_offset,src_file,chunk_offset FROM pick_event WHERE run_id=$id"
                    + (string.IsNullOrEmpty(routeId) ? "" : " AND route_id=$route")
                    + " ORDER BY ts_ms,chunk_offset,line_no,line_offset;";
                cmd.Parameters.AddWithValue("$id", runId);
                if (!string.IsNullOrEmpty(routeId)) cmd.Parameters.AddWithValue("$route", routeId);
                var result = new List<PickEvent>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    result.Add(new PickEvent
                    {
                        RunId = reader.GetString(0),
                        RouteId = reader.IsDBNull(1) ? null : reader.GetString(1),
                        Kind = reader.GetString(2), Item = reader.GetString(3),
                        TsMs = reader.GetInt64(4), LineNo = reader.GetInt64(5),
                        LineOffset = reader.GetInt32(6), SrcFile = reader.GetString(7),
                        ChunkOffset = reader.GetInt64(8),
                    });
                return (IReadOnlyList<PickEvent>)result;
            }, Array.Empty<PickEvent>(), ct);
        }

    /// <summary>三类拾取事实计数。只读汇总视图，原始 pick_event 不变。</summary>
    public sealed record PickFactCounts(long Picked, long NoTarget, long Skipped);

    /// <summary>分页事实查询：run/route/kind/账号日期都是调用方指定的读取条件。</summary>
    public static Task<IReadOnlyList<PickEvent>> QueryPickFactsAsync(
        string? runId = null, string? routeId = null, string? kind = null,
        string? account = null, int days = 7, int offset = 0, int limit = 1000,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runId) && string.IsNullOrWhiteSpace(account))
            return Task.FromResult<IReadOnlyList<PickEvent>>(Array.Empty<PickEvent>());
        string normalizedKind = kind is "picked" or "no_target" or "skipped" ? kind : "";
        long cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 365)).ToUnixTimeMilliseconds();
        return Core.Value.EnqueueQuery(db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"SELECT p.run_id,p.route_id,p.kind,p.item,p.ts_ms,p.line_no,p.line_offset,p.src_file
FROM pick_event p JOIN run r ON r.run_id=p.run_id
WHERE ($run='' OR p.run_id=$run) AND ($route='' OR p.route_id=$route)
AND ($kind='' OR p.kind=$kind) AND ($account='' OR r.account=$account) AND p.ts_ms>=$cutoff
ORDER BY p.ts_ms,p.pick_id LIMIT $limit OFFSET $offset;";
            cmd.Parameters.AddWithValue("$run", runId ?? "");
            cmd.Parameters.AddWithValue("$route", routeId ?? "");
            cmd.Parameters.AddWithValue("$kind", normalizedKind);
            cmd.Parameters.AddWithValue("$account", account ?? "");
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10000));
            cmd.Parameters.AddWithValue("$offset", Math.Max(0, offset));
            var rows = new List<PickEvent>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) rows.Add(new PickEvent
            {
                RunId = reader.GetString(0), RouteId = reader.IsDBNull(1) ? null : reader.GetString(1),
                Kind = reader.GetString(2), Item = reader.GetString(3), TsMs = reader.GetInt64(4),
                LineNo = reader.GetInt64(5), LineOffset = reader.GetInt32(6), SrcFile = reader.GetString(7)
            });
            return (IReadOnlyList<PickEvent>)rows;
        }, Array.Empty<PickEvent>(), ct);
    }

    /// <summary>按 run 或 route 取三类事实计数；route 行同时给出投影列用于对账。</summary>
    public static Task<PickFactCounts> QueryPickCountsAsync(
        string? runId = null, string? routeId = null, string? account = null, int days = 7,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runId) && string.IsNullOrWhiteSpace(routeId) && string.IsNullOrWhiteSpace(account))
            return Task.FromResult(new PickFactCounts(0, 0, 0));
        long cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 365)).ToUnixTimeMilliseconds();
        return Core.Value.EnqueueQuery(db =>
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"SELECT
 COALESCE(SUM(CASE WHEN p.kind='picked' THEN 1 ELSE 0 END),0),
 COALESCE(SUM(CASE WHEN p.kind='no_target' THEN 1 ELSE 0 END),0),
 COALESCE(SUM(CASE WHEN p.kind='skipped' THEN 1 ELSE 0 END),0)
FROM pick_event p JOIN run r ON r.run_id=p.run_id
WHERE ($run='' OR p.run_id=$run) AND ($route='' OR p.route_id=$route)
AND ($account='' OR r.account=$account) AND p.ts_ms>=$cutoff;";
            cmd.Parameters.AddWithValue("$run", runId ?? "");
            cmd.Parameters.AddWithValue("$route", routeId ?? "");
            cmd.Parameters.AddWithValue("$account", account ?? "");
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? new PickFactCounts(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2))
                : new PickFactCounts(0, 0, 0);
        }, new PickFactCounts(0, 0, 0), ct);
    }

    /// <summary>状态时段事实汇总：派生视图非事实源；业务判定归消费方。</summary>
    public sealed record SpanFactTotals(long IdleMs, long NotRunningMs);

    /// <summary>按账号和毫秒日期范围取状态段（含进行中段已持续时长；按范围裁剪）。</summary>
    public static Task<IReadOnlyList<StateSpan>> QuerySpansRangeAsync(
        string account, long startAtMs, long endAtMs, long? nowMs = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(account) || endAtMs <= startAtMs)
            return Task.FromResult<IReadOnlyList<StateSpan>>(Array.Empty<StateSpan>());
        return Core.Value.EnqueueQuery(db =>
        {
            using var cmd=db.CreateCommand();
            cmd.CommandText=@"SELECT span_id,account,state,start_at_ms,end_at_ms,dur_ms,src_file
FROM state_span WHERE account=$a AND start_at_ms<$end AND COALESCE(end_at_ms,$now)>$start ORDER BY start_at_ms;";
            long now=nowMs??DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Add(cmd,"$a",account);Add(cmd,"$start",startAtMs);Add(cmd,"$end",endAtMs);Add(cmd,"$now",now);
            var rows=new List<StateSpan>();using var reader=cmd.ExecuteReader();
            while(reader.Read())
            {
                bool ongoing=reader.IsDBNull(4);long start=reader.GetInt64(3);
                long end=ongoing?now:reader.GetInt64(4);
                rows.Add(new StateSpan{SpanId=reader.GetInt64(0),Account=reader.GetString(1),State=reader.GetString(2),
                    StartAtMs=Math.Max(start,startAtMs),EndAtMs=ongoing?null:Math.Min(end,endAtMs),
                    DurMs=reader.GetInt64(5),SrcFile=reader.GetString(6),IsOngoing=ongoing,
                    DurMsSoFar=Math.Max(0,Math.Min(end,endAtMs)-Math.Max(start,startAtMs))});
            }
            return (IReadOnlyList<StateSpan>)rows;
        },Array.Empty<StateSpan>(),ct);
    }

    /// <summary>idle/not_running 事实汇总（派生视图非事实源；业务判定归消费方）。</summary>
    public static Task<SpanFactTotals> QuerySpanTotalsAsync(
        string account, long startAtMs, long endAtMs, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(account) || endAtMs <= startAtMs)
            return Task.FromResult(new SpanFactTotals(0,0));
        return Core.Value.EnqueueQuery(db =>
        {
            using var cmd=db.CreateCommand();
            cmd.CommandText=@"SELECT
COALESCE(SUM(CASE WHEN state='idle' THEN MAX(0,MIN(COALESCE(end_at_ms,$now),$end)-MAX(start_at_ms,$start)) ELSE 0 END),0),
COALESCE(SUM(CASE WHEN state='not_running' THEN MAX(0,MIN(COALESCE(end_at_ms,$now),$end)-MAX(start_at_ms,$start)) ELSE 0 END),0)
FROM state_span WHERE account=$a AND start_at_ms<$end AND COALESCE(end_at_ms,$now)>$start;";
            Add(cmd,"$a",account);Add(cmd,"$start",startAtMs);Add(cmd,"$end",endAtMs);
            Add(cmd,"$now",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            using var reader=cmd.ExecuteReader();
            return reader.Read()?new SpanFactTotals(reader.GetInt64(0),reader.GetInt64(1)):new SpanFactTotals(0,0);
        },new SpanFactTotals(0,0),ct);
    }

    /// <summary>全天事实桶：跨天按日裁切，未明确状态的时间记 unknown，不猜 running。</summary>
    public sealed record DaySpan
    {
        public string Account { get; set; } = "";
        public string Day { get; set; } = "";
        public long RunningMs { get; set; }
        public long IdleMs { get; set; }
        public long NotRunningMs { get; set; }
        public long UnknownMs { get; set; }
        public long ComputedAtMs { get; set; }
        public bool IsFinal { get; set; }
    }

        // ── t6：BGI 状态时段（三态，只在切换时开/闭段）──────────────────────────

        /// <summary>开一段状态（INSERT OR IGNORE 幂等：同 account+state+start 不会重复）。</summary>
        private const int DefaultDaySpanLookbackDays = 7;

        /// <summary>按账号增量重算最近N天：状态互斥 running>not_running>idle>unknown，封盘日不重算。</summary>
        public static Task<int> UpdateDaySpansForAccountAsync(string account, DateTimeOffset? now = null,
            CancellationToken ct = default, int recentDays = DefaultDaySpanLookbackDays)
        {
            if(string.IsNullOrWhiteSpace(account))return Task.FromResult(0);
            var at=now??DateTimeOffset.Now;
            recentDays=Math.Clamp(recentDays,1,365);
            return Core.Value.EnqueueQuery(db =>
            {
                long since=ToMs(at.LocalDateTime.Date.AddDays(-(recentDays-1)));
                long until=ToMs(at.LocalDateTime.Date.AddDays(1));
                using var cmd=db.CreateCommand();
                cmd.CommandText=@"SELECT span_id,state,start_at_ms,end_at_ms FROM state_span
WHERE account=$a AND (end_at_ms IS NULL OR end_at_ms>=$since)
UNION ALL
SELECT 0,'running',start_at_ms,end_at_ms FROM run
WHERE account=$a AND (end_at_ms IS NULL OR end_at_ms>=$since)
ORDER BY start_at_ms;";
                Add(cmd,"$a",account);Add(cmd,"$since",since);
                var spans=new List<(long Id,string State,long Start,long? End)>();
                using(var reader=cmd.ExecuteReader())while(reader.Read())spans.Add((reader.GetInt64(0),reader.GetString(1),reader.GetInt64(2),reader.IsDBNull(3)?null:reader.GetInt64(3)));
                var days=new SortedSet<string>(StringComparer.Ordinal);
                foreach(var span in spans)
                {
                    long end=span.End??at.ToUnixTimeMilliseconds();
                    for(long ts=ToMs(DayStart(span.Start));ts<end && ts<until;ts+=86400000)
                        if(ts>=since)days.Add(DayOf(ts));
                }
                int changed=0;
                foreach(string day in days)
                {
                    if(IsDayFinal(db,account,day))continue;
                    if(!HasAnySpanOnDay(db,account,day))continue;
                    changed+=WriteDaySpan(db,account,day,spans,at);
                }
                return changed;
            },0,ct);
        }

        private static DateTime DayStart(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.Date;

        private static int WriteDaySpan(SqliteConnection db,string account,string day,
            IReadOnlyList<(long Id,string State,long Start,long? End)> spans,DateTimeOffset now)
        {
            long start=ToMs(DateTime.ParseExact(day,"yyyy-MM-dd",CultureInfo.InvariantCulture).Date),end=start+86400000;
            var intervals=new List<(long Start,long End,string State)>();
            foreach(var span in spans)
            {
                long from=Math.Max(span.Start,start),to=Math.Min(span.End??now.ToUnixTimeMilliseconds(),end);
                if(to>from)intervals.Add((from,to,span.State));
            }
            long running=SumExclusive(intervals,"running",start);
            long notRunning=SumExclusive(intervals,"not_running",start);
            long idle=SumExclusive(intervals,"idle",start);
            long unknown=86400000-running-idle-notRunning;
            if(unknown<0 || running+idle+notRunning+unknown!=86400000)
            {
                WarnDaySpanOnce(account,day,"day_span 区间合并异常，拒绝写入错误全天桶");
                return 0;
            }
            bool final=end<=now.ToUnixTimeMilliseconds();
            using var cmd=db.CreateCommand();
            cmd.CommandText=@"INSERT INTO day_span(account,day,running_ms,idle_ms,not_running_ms,unknown_ms,computed_at_ms,is_final)
VALUES($a,$d,$r,$i,$n,$u,$c,$f) ON CONFLICT(account,day) DO UPDATE SET
running_ms=excluded.running_ms,idle_ms=excluded.idle_ms,not_running_ms=excluded.not_running_ms,
unknown_ms=excluded.unknown_ms,computed_at_ms=excluded.computed_at_ms,is_final=excluded.is_final
WHERE day_span.is_final=0;";
            Add(cmd,"$a",account);Add(cmd,"$d",day);Add(cmd,"$r",running);Add(cmd,"$i",idle);
            Add(cmd,"$n",notRunning);Add(cmd,"$u",unknown);Add(cmd,"$c",now.ToUnixTimeMilliseconds());Add(cmd,"$f",final?1:0);
            return cmd.ExecuteNonQuery();
        }

        private static readonly Dictionary<string,DateTime> DaySpanWarnings = new(StringComparer.OrdinalIgnoreCase);
        private static void WarnDaySpanOnce(string account,string day,string message)
        {
            string key=account+"|"+day;
            lock(DaySpanWarnings)
            {
                if(DaySpanWarnings.TryGetValue(key,out var last) && DateTime.UtcNow-last<TimeSpan.FromHours(1))return;
                DaySpanWarnings[key]=DateTime.UtcNow;
            }
            App.AppendErrorLog("RunRecordStore.DaySpan",new InvalidOperationException(message));
        }

        private static long SumExclusive(List<(long Start,long End,string State)> input,string state,long dayStart)
        {
            // 状态优先级：running(1) > not_running(2) > idle(3) > unknown(空段)。
            // 每个高优先级先独立合并，低优先级只取不与所有高优先级重叠的部分。
            int priority=state=="running"?1:state=="not_running"?2:3;
            long total=0;
            foreach(var same in MergeSame(input.Where(x=>x.State==state)))
            {
                var parts=new List<(long Start,long End)>{(same.Start,same.End)};
                foreach(var higher in MergeSame(input.Where(x=>Priority(x.State)<priority))
                    .OrderBy(x=>x.Start))
                {
                    var next=new List<(long Start,long End)>();
                    foreach(var part in parts)next.AddRange(Subtract(part,higher));
                    parts=next;
                }
                foreach(var part in parts)total+=Math.Max(0,part.End-part.Start);
            }
            return total;
        }

        private static int Priority(string state) => state=="running"?1:state=="not_running"?2:3;
        private static IEnumerable<(long Start,long End)> MergeSame(IEnumerable<(long Start,long End,string State)> input)
        {
            long start=0,end=0;bool any=false;
            foreach(var span in input.OrderBy(x=>x.Start).ThenBy(x=>x.End))
            {
                if(!any){start=span.Start;end=span.End;any=true;continue;}
                if(span.Start<=end){end=Math.Max(end,span.End);continue;}
                yield return (start,end);start=span.Start;end=span.End;
            }
            if(any)yield return (start,end);
        }
        private static IEnumerable<(long Start,long End)> Subtract((long Start,long End) part,(long Start,long End) cut)
        {
            if(cut.End<=part.Start || cut.Start>=part.End){yield return part;yield break;}
            if(part.Start<cut.Start)yield return (part.Start,cut.Start);
            if(part.End>cut.End)yield return (cut.End,part.End);
        }


        /// <summary>事实日桶查询；从未有记录的日期返回 null（无记录），不是伪造全天0。</summary>
        public static Task<DaySpan?> QueryDaySpanAsync(string account,string day,CancellationToken ct=default)
        {
            if(string.IsNullOrWhiteSpace(account)||string.IsNullOrWhiteSpace(day))return Task.FromResult<DaySpan?>(null);
            return Core.Value.EnqueueQuery<DaySpan?>(db =>
            {
                using var cmd=db.CreateCommand();cmd.CommandText=@"SELECT account,day,running_ms,idle_ms,not_running_ms,unknown_ms,computed_at_ms,is_final
FROM day_span WHERE account=$a AND day=$d;";
                Add(cmd,"$a",account);Add(cmd,"$d",day);using var reader=cmd.ExecuteReader();
                return reader.Read()?new DaySpan{Account=reader.GetString(0),Day=reader.GetString(1),RunningMs=reader.GetInt64(2),
                    IdleMs=reader.GetInt64(3),NotRunningMs=reader.GetInt64(4),UnknownMs=reader.GetInt64(5),
                    ComputedAtMs=reader.GetInt64(6),IsFinal=reader.GetInt64(7)!=0}:null;
            },null,ct);
        }

        private static bool HasAnySpanOnDay(SqliteConnection db,string account,string day)
        {
            long start=ToMs(DateTime.ParseExact(day,"yyyy-MM-dd",CultureInfo.InvariantCulture).Date),end=start+86400000;
            using var cmd=db.CreateCommand();
            cmd.CommandText=@"SELECT EXISTS(SELECT 1 FROM state_span WHERE account=$a AND start_at_ms<$end AND COALESCE(end_at_ms,$now)>$start)
 OR EXISTS(SELECT 1 FROM run WHERE account=$a AND start_at_ms<$end AND COALESCE(end_at_ms,$now)>$start);";
            Add(cmd,"$a",account);Add(cmd,"$start",start);Add(cmd,"$end",end);Add(cmd,"$now",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return Convert.ToInt32(cmd.ExecuteScalar()??0,CultureInfo.InvariantCulture)==1;
        }

        private static bool IsDayFinal(SqliteConnection db,string account,string day)
        {
            using var cmd=db.CreateCommand();cmd.CommandText="SELECT is_final FROM day_span WHERE account=$a AND day=$d;";
            Add(cmd,"$a",account);Add(cmd,"$d",day);
            return Convert.ToInt32(cmd.ExecuteScalar()??0,CultureInfo.InvariantCulture)==1;
        }

        /// <summary>当前有状态事实的账号列表，供小时拍增量日汇总，不全表重算历史。</summary>
        public static Task<IReadOnlyList<string>> QuerySpanAccountsAsync(CancellationToken ct=default)
            => Core.Value.EnqueueQuery<IReadOnlyList<string>>(db =>
            {
                using var cmd=db.CreateCommand();cmd.CommandText="SELECT DISTINCT account FROM state_span ORDER BY account;";
                var result=new List<string>();using var reader=cmd.ExecuteReader();while(reader.Read())result.Add(reader.GetString(0));return result;
            },Array.Empty<string>(),ct);

        /// <summary>近两天有 run 覆盖事实的账号，供 running 日汇总，不猜进程活性。</summary>
        public static Task<IReadOnlyList<string>> QueryRunAccountsForDaySpanAsync(CancellationToken ct=default)
            => Core.Value.EnqueueQuery<IReadOnlyList<string>>(db =>
            {
                using var cmd=db.CreateCommand();
                cmd.CommandText="SELECT DISTINCT account FROM run WHERE COALESCE(end_at_ms,start_at_ms)>=$c ORDER BY account;";
                Add(cmd,"$c",DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds());
                var result=new List<string>();using var reader=cmd.ExecuteReader();while(reader.Read())result.Add(reader.GetString(0));return result;
            },Array.Empty<string>(),ct);

        public static void AddStateSpan(StateSpan span)
        {
            if (span == null || string.IsNullOrWhiteSpace(span.Account) || span.StartAtMs < 0
                || string.IsNullOrWhiteSpace(span.State)) return;
            Core.Value.EnqueueWrite(db =>
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"INSERT OR IGNORE INTO state_span(account,state,start_at_ms,src_file)
                    VALUES($a,$s,$start,$src);";
                Add(cmd, "$a", span.Account); Add(cmd, "$s", span.State);
                Add(cmd, "$start", span.StartAtMs); Add(cmd, "$src", span.SrcFile);
                cmd.ExecuteNonQuery();
            });
        }

        /// <summary>收段（只关 open 段：end IS NULL，幂等；dur 由写线程算）。</summary>
        public static void CloseStateSpan(string account, long endAtMs)
        {
            if (string.IsNullOrWhiteSpace(account) || endAtMs < 0) return;
            Core.Value.EnqueueWrite(db =>
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"UPDATE state_span SET end_at_ms=$end,
                    dur_ms=MAX(0,$end-start_at_ms) WHERE account=$a AND end_at_ms IS NULL;";
                Add(cmd, "$a", account); Add(cmd, "$end", endAtMs);
                cmd.ExecuteNonQuery();
            });
        }

        /// <summary>t10 重启沿用：取每账号最后一条 end 为 NULL 的段（account+state）。</summary>
        public static Task<IReadOnlyList<(string Account, string State)>> LoadOpenSpanAccountsAsync(
            CancellationToken ct = default)
            => Core.Value.EnqueueQuery<IReadOnlyList<(string, string)>>(db =>
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"SELECT account,state FROM state_span s
 WHERE end_at_ms IS NULL AND span_id=(SELECT MAX(span_id) FROM state_span x
  WHERE x.account=s.account AND x.end_at_ms IS NULL);";
                var result = new List<(string, string)>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) result.Add((reader.GetString(0), reader.GetString(1)));
                return (IReadOnlyList<(string, string)>)result;
            }, Array.Empty<(string, string)>(), ct);

        /// <summary>
        /// t10 一致性自检：run/route/state_span 三表中凡 start 与 end 都有的行，
        /// dur_ms 必须等于 end-start。返回不一致的表名+主键+实际差值（0 条 = 通过）。
        /// 只读，不修数；历史偏差由迁移任务一次性 UPDATE。
        /// </summary>
        public static Task<IReadOnlyList<string>> QueryDurInconsistenciesAsync(CancellationToken ct = default)
            => Core.Value.EnqueueQuery<IReadOnlyList<string>>(db =>
            {
                var result = new List<string>();
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"
SELECT 'run', run_id, dur_ms, (end_at_ms - start_at_ms) FROM run
 WHERE end_at_ms IS NOT NULL AND dur_ms != (end_at_ms - start_at_ms)
UNION ALL
SELECT 'route', route_id, dur_ms, (end_at_ms - start_at_ms) FROM route
 WHERE end_at_ms IS NOT NULL AND dur_ms != (end_at_ms - start_at_ms)
UNION ALL
SELECT 'state_span', 'span#'||span_id, dur_ms, (end_at_ms - start_at_ms) FROM state_span
 WHERE end_at_ms IS NOT NULL AND dur_ms != (end_at_ms - start_at_ms);";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    result.Add($"{reader.GetString(0)} {reader.GetString(1)}: dur={reader.GetInt64(2)} end-start={reader.GetInt64(3)} diff={reader.GetInt64(2) - reader.GetInt64(3)}");
                return (IReadOnlyList<string>)result;
            }, Array.Empty<string>(), ct);

        /// <summary>
        /// 按账号查状态段。t10：进行中段（end NULL）返回 IsOngoing=true 且 DurMsSoFar=now-start
        /// （自动算已持续时长，调用方零减法）；已结束段 DurMsSoFar=DurMs=end-start。
        /// </summary>
        public static Task<IReadOnlyList<StateSpan>> QuerySpansAsync(string account, int days = 1,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(account))
                return Task.FromResult<IReadOnlyList<StateSpan>>(Array.Empty<StateSpan>());
            long cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 30)).ToUnixTimeMilliseconds();
            return Core.Value.EnqueueQuery(db =>
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"SELECT span_id,account,state,start_at_ms,end_at_ms,dur_ms,src_file
                    FROM state_span WHERE account=$a AND start_at_ms>=$c ORDER BY start_at_ms;";
                cmd.Parameters.AddWithValue("$a", account);
                cmd.Parameters.AddWithValue("$c", cutoff);
                var result = new List<StateSpan>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    bool ongoing = reader.IsDBNull(4);
                    long start = reader.GetInt64(3);
                    long end = ongoing ? 0 : reader.GetInt64(4);
                    result.Add(new StateSpan
                    {
                        SpanId = reader.GetInt64(0), Account = reader.GetString(1), State = reader.GetString(2),
                        StartAtMs = start,
                        EndAtMs = ongoing ? null : end,
                        DurMs = reader.GetInt64(5), SrcFile = reader.GetString(6),
                        IsOngoing = ongoing,
                        DurMsSoFar = ongoing ? Math.Max(0, now - start) : reader.GetInt64(5),
                    });
                }
                return (IReadOnlyList<StateSpan>)result;
            }, Array.Empty<StateSpan>(), ct);
        }

        public static Task<IReadOnlyList<RunRecord>> LoadOpenRunsAsync(
            string? account = null, CancellationToken ct = default)
            => Core.Value.EnqueueQuery<IReadOnlyList<RunRecord>>(db => ReadOpenRuns(db, account), Array.Empty<RunRecord>(), ct);

        /// <summary>
        /// 找该账号该组名「未关闭」的 run。
        ///
        /// ★★ t72 修复（实测：今天正在跑的「联机锄地-混合精英」被历史 END 锚点关掉）：
        ///
        ///   原实现只按 account+group_name 查，**完全不管时间**。
        ///   回填历史日志（11-20…11-25）时，扫到那些文件里「混合精英」的结束锚点，
        ///   EndGroup 在内存里找不到窗口 → 走 BackfillCloseAsync → 这里按名字查到
        ///   **今天** 08:32:20 那条 open 行 → 用历史的 endMs 把它关了（负时长被夹到 0，
        ///   状态 Interrupted）。而它其实一直在跑（日志 09:55 还在写该组）。
        ///
        ///   修法：加**时间窗口**约束 —— 只匹配 start_at_ms 落在 [fromMs, toMs) 的 open 行。
        ///   调用方（BackfillCloseAsync）用「源文件当天」作为窗口，
        ///   这样历史 END 只能关历史行，绝不可能误伤今天的运行。
        /// </summary>
        public static Task<RunRecord?> FindOpenRunAsync(
            string account, string groupName, CancellationToken ct = default,
            long fromMs = 0, long toMs = 0)
        {
            if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(groupName))
                return Task.FromResult<RunRecord?>(null);
            return Core.Value.EnqueueQuery<RunRecord?>(db =>
            {
                using var cmd = db.CreateCommand();
                // 时间窗口：fromMs<=start<toMs。传 0 表示不限（保持旧行为）。
                cmd.CommandText = RunColumns + @" WHERE account=$account COLLATE NOCASE
                    AND group_name=$group COLLATE NOCASE AND end_at_ms IS NULL
                    AND ($from = 0 OR start_at_ms >= $from)
                    AND ($to = 0 OR start_at_ms < $to)
                    ORDER BY start_at_ms DESC LIMIT 1;";
                cmd.Parameters.AddWithValue("$account", account);
                cmd.Parameters.AddWithValue("$group", groupName);
                cmd.Parameters.AddWithValue("$from", fromMs);
                cmd.Parameters.AddWithValue("$to", toMs);
                using var reader = cmd.ExecuteReader();
                return reader.Read() ? ReadRun(reader) : null;
            }, null, ct);
        }

        public static Task<IReadOnlyList<RunRecord>> QueryRunsAsync(
            string account, int days = 1, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(account)) return Task.FromResult<IReadOnlyList<RunRecord>>(Array.Empty<RunRecord>());
            long cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 365)).ToUnixTimeMilliseconds();
            return Core.Value.EnqueueQuery<IReadOnlyList<RunRecord>>(db =>
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = RunColumns + @" WHERE account=$account COLLATE NOCASE
                    AND COALESCE(end_at_ms,start_at_ms) >= $cutoff
                    ORDER BY COALESCE(end_at_ms,start_at_ms) DESC;";
                cmd.Parameters.AddWithValue("$account", account);
                cmd.Parameters.AddWithValue("$cutoff", cutoff);
                return ReadRunList(cmd);
            }, Array.Empty<RunRecord>(), ct);
        }

        /// <summary>按 run_id 精确取一行（只读查询门面，供 /api/bgi/runs/detail 用）；取不到返回 null。</summary>
        public static Task<RunRecord?> FindRunAsync(string runId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(runId)) return Task.FromResult<RunRecord?>(null);
            return Core.Value.EnqueueQuery<RunRecord?>(db =>
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = RunColumns + " WHERE run_id=$id LIMIT 1;";
                cmd.Parameters.AddWithValue("$id", runId);
                using var reader = cmd.ExecuteReader();
                return reader.Read() ? ReadRun(reader) : null;
            }, null, ct);
        }

        public static Task<IReadOnlyList<RouteRecord>> QueryRoutesAsync(string runId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(runId)) return Task.FromResult<IReadOnlyList<RouteRecord>>(Array.Empty<RouteRecord>());
            return Core.Value.EnqueueQuery(db =>
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT route_id,run_id,kind,name,parent_route_id,start_at_ms,end_at_ms,dur_ms,state,pick_count,pick_detail,src_file,picked,skipped,no_target FROM route WHERE run_id=$id ORDER BY start_at_ms,kind,name;";
                cmd.Parameters.AddWithValue("$id", runId);
                var result = new List<RouteRecord>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) result.Add(ReadRoute(reader));
                return (IReadOnlyList<RouteRecord>)result;
            }, Array.Empty<RouteRecord>(), ct);
        }

        public static Task<IReadOnlyList<ErrorRecord>> QueryErrorsAsync(string runId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(runId)) return Task.FromResult<IReadOnlyList<ErrorRecord>>(Array.Empty<ErrorRecord>());
            return Core.Value.EnqueueQuery(db =>
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT err_id,run_id,route_id,ts_ms,level,exception_type,message,detail,source,content_hash,src_file FROM err WHERE run_id=$id ORDER BY ts_ms,err_id;";
                cmd.Parameters.AddWithValue("$id", runId);
                var result = new List<ErrorRecord>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) result.Add(ReadError(reader));
                return (IReadOnlyList<ErrorRecord>)result;
            }, Array.Empty<ErrorRecord>(), ct);
        }

        public static Task<IReadOnlyList<RunGroupSummary>> QueryGroupsAsync(
            string account, int days = 7, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(account)) return Task.FromResult<IReadOnlyList<RunGroupSummary>>(Array.Empty<RunGroupSummary>());
            long cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 365)).ToUnixTimeMilliseconds();
            return Core.Value.EnqueueQuery(db =>
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = @"SELECT group_name,COUNT(*) AS runs,
                    SUM(CASE WHEN state IN ('Failed','Cancelled','Interrupted') THEN 1 ELSE 0 END) AS failures,
                    CAST(ROUND(AVG(exp)) AS INTEGER),CAST(ROUND(AVG(mora)) AS INTEGER),
                    SUM(exp),SUM(mora),SUM(picks),SUM(dur_ms),MAX(end_at_ms)
                    FROM run WHERE account=$account COLLATE NOCASE
                    AND COALESCE(end_at_ms,start_at_ms) >= $cutoff
                    GROUP BY group_name COLLATE NOCASE
                    ORDER BY failures DESC,group_name COLLATE NOCASE;";
                cmd.Parameters.AddWithValue("$account", account);
                cmd.Parameters.AddWithValue("$cutoff", cutoff);
                var result = new List<RunGroupSummary>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) result.Add(new RunGroupSummary
                {
                    Group = reader.GetString(0), Runs = reader.GetInt32(1), Failures = reader.GetInt32(2),
                    AvgExp = reader.GetInt64(3), AvgMora = reader.GetInt64(4),
                    TotalExp = reader.GetInt64(5), TotalMora = reader.GetInt64(6),
                    TotalPicks = reader.GetInt64(7), TotalDurMs = reader.GetInt64(8),
                    LastEndAtMs = reader.IsDBNull(9) ? null : reader.GetInt64(9)
                });
                return (IReadOnlyList<RunGroupSummary>)result;
            }, Array.Empty<RunGroupSummary>(), ct);
        }

        /// <summary>异步入队清理；days 小于 1 取默认 7，最大 365。</summary>
        public static void PurgeOlderThan(int days)
            => Core.Value.EnqueueWrite(db => Purge(db, days, DateTimeOffset.UtcNow));

        public static Task<int> PurgeOlderThanAsync(int days, CancellationToken ct = default)
            => Core.Value.EnqueueQuery(db => Purge(db, days, DateTimeOffset.UtcNow), 0, ct);

        /// <summary>
        /// t49 存储策略A：删除无锚点且非错误/警告的 raw_event 噪音行，并 VACUUM 收缩文件。
        /// 返回删除行数；VACUUM 必须在事务外执行（用 EnqueueWrite 在单线程队列里串行跑）。
        /// ★ 只影响 raw_event 噪音行，绝不触碰 run/route/err/pick_event 结构化数据。
        /// </summary>
        public static Task<long> PurgeNoiseRawEventsAsync(CancellationToken ct = default)
            => Core.Value.EnqueueQuery(db =>
            {
                long deleted = 0;
                using (var del = db.CreateCommand())
                {
                    del.CommandText = "DELETE FROM raw_event WHERE anchor='' AND level NOT IN ('ERR','WRN','FTL');";
                    deleted = del.ExecuteNonQuery();
                }
                // ★ SQLite 删除行不自动缩文件，必须 VACUUM（事务外）。
                //   EnqueueWrite 的写循环不在显式事务里，直接执行即可。
                using (var vac = db.CreateCommand())
                {
                    vac.CommandText = "VACUUM;";
                    vac.ExecuteNonQuery();
                }
                return deleted;
            }, 0L, ct);

        /// <summary>等待此前入队的操作完成；不保证数据库可用，只保证入队动作已处理。</summary>
        public static Task FlushAsync(CancellationToken ct = default)
            => Core.Value.EnqueueQuery<object?>(_ => null, null, ct);

        /// <summary>故障修复后显式重连；普通写入/查询不会自动刷错误日志。</summary>
        public static void RetryDatabase() => Core.Value.Retry();

        private static long ToMs(DateTime at) => new DateTimeOffset(at.ToUniversalTime()).ToUnixTimeMilliseconds();

        private static string DayOf(long ms)
            => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private const string RunColumns = @"SELECT run_id,account,group_name,start_at_ms,end_at_ms,dur_ms,state,
            script_count,exp,mora,picks,src_file_start,src_file_end,start_day,end_day,is_cross_day,is_backfilled,detail,
            planned_routes,day_split,pick_unattributed,round_index,round_total,round_host,round_count FROM run";

        private static List<RunRecord> ReadOpenRuns(SqliteConnection db, string? account)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = RunColumns + (string.IsNullOrWhiteSpace(account)
                ? " WHERE end_at_ms IS NULL ORDER BY account,start_at_ms;"
                : " WHERE end_at_ms IS NULL AND account=$account COLLATE NOCASE ORDER BY start_at_ms;");
            if (!string.IsNullOrWhiteSpace(account)) cmd.Parameters.AddWithValue("$account", account);
            return ReadRunList(cmd);
        }

        private static List<RunRecord> ReadRunList(SqliteCommand cmd)
        {
            var result = new List<RunRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) result.Add(ReadRun(reader));
            return result;
        }

        private static RunRecord ReadRun(SqliteDataReader r) => new()
        {
            RunId = r.GetString(0), Account = r.GetString(1), GroupName = r.GetString(2),
            StartAtMs = r.GetInt64(3), EndAtMs = r.IsDBNull(4) ? null : r.GetInt64(4),
            DurMs = r.GetInt64(5), State = r.GetString(6), ScriptCount = r.GetInt32(7),
            Exp = r.GetInt64(8), Mora = r.GetInt64(9), Picks = r.GetInt32(10),
            SrcFileStart = r.GetString(11), SrcFileEnd = r.GetString(12),
            StartDay = r.GetString(13), EndDay = r.GetString(14),
            IsCrossDay = r.GetInt64(15) != 0, IsBackfilled = r.GetInt64(16) != 0, Detail = r.GetString(17),
            PlannedRoutes = r.FieldCount > 18 && !r.IsDBNull(18) ? r.GetInt32(18) : 0,
            DaySplit = r.FieldCount > 19 && !r.IsDBNull(19) && r.GetInt64(19) != 0,
            PickUnattributed = r.FieldCount > 20 && !r.IsDBNull(20) ? r.GetInt32(20) : 0,
            RoundIndex = r.FieldCount > 21 && !r.IsDBNull(21) ? r.GetInt32(21) : 0,
            RoundTotal = r.FieldCount > 22 && !r.IsDBNull(22) ? r.GetInt32(22) : 0,
            RoundHost = r.FieldCount > 23 && !r.IsDBNull(23) ? r.GetString(23) : "",
            RoundCount = r.FieldCount > 24 && !r.IsDBNull(24) ? r.GetInt32(24) : 0
        };

        private static RouteRecord ReadRoute(SqliteDataReader r) => new()
        {
            RouteId = r.GetString(0), RunId = r.GetString(1), Kind = r.GetString(2), Name = r.GetString(3),
            ParentRouteId = r.IsDBNull(4) ? null : r.GetString(4), StartAtMs = r.GetInt64(5),
            EndAtMs = r.IsDBNull(6) ? null : r.GetInt64(6), DurMs = r.GetInt64(7), State = r.GetString(8),
            PickCount = r.GetInt32(9), PickDetail = r.GetString(10), SrcFile = r.GetString(11),
            Picked = r.FieldCount > 12 && !r.IsDBNull(12) ? r.GetInt32(12) : 0,
            Skipped = r.FieldCount > 13 && !r.IsDBNull(13) ? r.GetInt32(13) : 0,
            NoTarget = r.FieldCount > 14 && !r.IsDBNull(14) ? r.GetInt32(14) : 0
        };

        private static ErrorRecord ReadError(SqliteDataReader r) => new()
        {
            ErrId = r.GetInt64(0), RunId = r.GetString(1), RouteId = r.IsDBNull(2) ? null : r.GetString(2),
            TsMs = r.GetInt64(3), Level = r.GetString(4), ExceptionType = r.GetString(5),
            Message = r.GetString(6), Detail = r.GetString(7), Source = r.GetString(8),
            ContentHash = r.GetString(9), SrcFile = r.GetString(10)
        };

        private static void WriteRun(SqliteConnection db, RunRecord row)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"INSERT OR IGNORE INTO run
                (run_id,account,group_name,start_at_ms,end_at_ms,dur_ms,state,script_count,exp,mora,picks,
                 src_file_start,src_file_end,start_day,end_day,is_cross_day,is_backfilled,detail,
                 planned_routes,day_split,pick_unattributed,
                 round_index,round_total,round_host,round_count)
                VALUES($id,$account,$group,$start,$end,$dur,$state,$scripts,$exp,$mora,$picks,
                 $srcStart,$srcEnd,$startDay,$endDay,$cross,$backfilled,$detail,
                 $planned,$daySplit,$pickUnattr,
                 $rdIdx,$rdTotal,$rdHost,$rdCount);";
            Add(cmd,"$id",row.RunId); Add(cmd,"$account",row.Account); Add(cmd,"$group",row.GroupName);
            Add(cmd,"$start",row.StartAtMs); Add(cmd,"$end",row.EndAtMs); Add(cmd,"$dur",row.DurMs);
            Add(cmd,"$state",row.State); Add(cmd,"$scripts",row.ScriptCount); Add(cmd,"$exp",row.Exp);
            Add(cmd,"$mora",row.Mora); Add(cmd,"$picks",row.Picks); Add(cmd,"$srcStart",row.SrcFileStart);
            Add(cmd,"$srcEnd",row.SrcFileEnd); Add(cmd,"$startDay",row.StartDay); Add(cmd,"$endDay",row.EndDay);
            Add(cmd,"$cross",row.IsCrossDay ? 1 : 0); Add(cmd,"$backfilled",row.IsBackfilled ? 1 : 0);
            Add(cmd,"$planned",row.PlannedRoutes); Add(cmd,"$daySplit",row.DaySplit ? 1 : 0);
            Add(cmd,"$pickUnattr",row.PickUnattributed);
            Add(cmd,"$rdIdx",row.RoundIndex); Add(cmd,"$rdTotal",row.RoundTotal);
            Add(cmd,"$rdHost",row.RoundHost); Add(cmd,"$rdCount",row.RoundCount);
            Add(cmd,"$detail",row.Detail); cmd.ExecuteNonQuery();
        }

        private static void WriteRoute(SqliteConnection db, RouteRecord row)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"INSERT INTO route
                (route_id,run_id,kind,name,parent_route_id,start_at_ms,end_at_ms,dur_ms,state,pick_count,pick_detail,src_file,
                 picked,skipped,no_target)
                VALUES($id,$run,$kind,$name,$parent,$start,$end,$dur,$state,$picks,$pickDetail,$src,
                 $pickedCnt,$skippedCnt,$noTargetCnt)
                ON CONFLICT(route_id) DO UPDATE SET
                    end_at_ms=COALESCE(route.end_at_ms,excluded.end_at_ms),
                    dur_ms=CASE WHEN route.end_at_ms IS NULL THEN excluded.dur_ms ELSE route.dur_ms END,
                    state=CASE WHEN route.end_at_ms IS NULL THEN excluded.state ELSE route.state END,
                    pick_count=CASE WHEN route.end_at_ms IS NULL THEN excluded.pick_count ELSE route.pick_count END,
                    pick_detail=CASE WHEN route.end_at_ms IS NULL THEN excluded.pick_detail ELSE route.pick_detail END,
                    src_file=CASE WHEN route.end_at_ms IS NULL THEN excluded.src_file ELSE route.src_file END,
                    picked=CASE WHEN route.end_at_ms IS NULL THEN excluded.picked ELSE route.picked END,
                    skipped=CASE WHEN route.end_at_ms IS NULL THEN excluded.skipped ELSE route.skipped END,
                    no_target=CASE WHEN route.end_at_ms IS NULL THEN excluded.no_target ELSE route.no_target END;";
            Add(cmd,"$id",row.RouteId); Add(cmd,"$run",row.RunId); Add(cmd,"$kind",row.Kind);
            Add(cmd,"$name",row.Name); Add(cmd,"$parent",row.ParentRouteId); Add(cmd,"$start",row.StartAtMs);
            Add(cmd,"$end",row.EndAtMs); Add(cmd,"$dur",row.DurMs); Add(cmd,"$state",row.State);
            Add(cmd,"$picks",row.PickCount); Add(cmd,"$pickDetail",row.PickDetail); Add(cmd,"$src",row.SrcFile);
            Add(cmd,"$pickedCnt",row.Picked); Add(cmd,"$skippedCnt",row.Skipped); Add(cmd,"$noTargetCnt",row.NoTarget);
            cmd.ExecuteNonQuery();
        }

        private static void WriteError(SqliteConnection db, ErrorRecord row)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"INSERT OR IGNORE INTO err
                (run_id,route_id,ts_ms,level,exception_type,message,detail,source,content_hash,src_file)
                VALUES($run,$route,$ts,$level,$type,$message,$detail,$source,$hash,$src);";
            Add(cmd,"$run",row.RunId); Add(cmd,"$route",row.RouteId); Add(cmd,"$ts",row.TsMs);
            Add(cmd,"$level",row.Level); Add(cmd,"$type",row.ExceptionType); Add(cmd,"$message",row.Message);
            Add(cmd,"$detail",row.Detail); Add(cmd,"$source",row.Source); Add(cmd,"$hash",row.ContentHash);
            Add(cmd,"$src",row.SrcFile); cmd.ExecuteNonQuery();
        }

        private static void WriteRunClose(SqliteConnection db, RunClose close)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"UPDATE run SET end_at_ms=$end,dur_ms=MAX(0,$end-start_at_ms),state=$state,
                detail=$detail,src_file_end=$src,end_day=$endDay,
                is_cross_day=CASE WHEN start_day<>$endDay OR $cross=1 THEN 1 ELSE 0 END,
                is_backfilled=CASE WHEN $backfilled=1 THEN 1 ELSE is_backfilled END,
                day_split=CASE WHEN $daySplit=1 THEN 1 ELSE day_split END,
                pick_unattributed=$pickUnattr,
                planned_routes=CASE WHEN $planned>0 THEN $planned ELSE planned_routes END,
                round_index=CASE WHEN $rdIdx>0 THEN $rdIdx ELSE round_index END,
                round_total=CASE WHEN $rdTotal>0 THEN $rdTotal ELSE round_total END,
                round_host=CASE WHEN $rdHost<>'' THEN $rdHost ELSE round_host END,
                round_count=CASE WHEN $rdCount>0 THEN $rdCount ELSE round_count END,
                exp=$exp,mora=$mora,picks=$picks WHERE run_id=$id AND end_at_ms IS NULL;";
            Add(cmd,"$id",close.RunId); Add(cmd,"$end",close.EndAtMs); Add(cmd,"$state",close.State);
            Add(cmd,"$detail",close.Detail); Add(cmd,"$src",close.SrcFileEnd); Add(cmd,"$endDay",close.EndDay);
            Add(cmd,"$cross",close.IsCrossDay ? 1 : 0); Add(cmd,"$backfilled",close.IsBackfilled ? 1 : 0);
            Add(cmd,"$daySplit",close.DaySplit ? 1 : 0); Add(cmd,"$pickUnattr",close.PickUnattributed);
            Add(cmd,"$planned",close.PlannedRoutes);
            Add(cmd,"$rdIdx",close.RoundIndex); Add(cmd,"$rdTotal",close.RoundTotal);
            Add(cmd,"$rdHost",close.RoundHost); Add(cmd,"$rdCount",close.RoundCount);
            Add(cmd,"$exp",close.Exp); Add(cmd,"$mora",close.Mora); Add(cmd,"$picks",close.Picks);
            cmd.ExecuteNonQuery();
        }

        // ── t54：负时长防线（去重限流，同一 id 只报一次，避免刷屏）──
        private static readonly ConcurrentDictionary<string, byte> NegativeDurationWarned =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 记录一次「end &lt; start」的数据错误。只记一次（按 id 去重），
        /// 写进 App 错误日志（用户在设置里能看到），绝不静默。
        /// </summary>
        private static void WarnNegativeDurationOnce(string id, string kind, long startMs, long endMs, string srcFile)
        {
            if (string.IsNullOrEmpty(id) || !NegativeDurationWarned.TryAdd(id, 0)) return;
            try
            {
                long diff = endMs - startMs;
                string msg = "[" + kind + "] 负时长数据错误（已夹到0，请反馈）：id=" + id
                    + " start=" + DateTimeOffset.FromUnixTimeMilliseconds(startMs).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    + " end=" + DateTimeOffset.FromUnixTimeMilliseconds(endMs).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    + " diff=" + (diff / 1000) + "s"
                    + (srcFile.Length > 0 ? " src=" + srcFile : "");
                try { App.AppendErrorLog("RunRecordStore.NegativeDuration", new InvalidOperationException(msg)); } catch { }
                try { DiagnosticSink?.Invoke(msg, null); } catch { }
            }
            catch { }
        }

        private static void WriteRouteClose(SqliteConnection db, RouteClose close)
        {
            // ★★ t54 防线：end < start 是数据错误（曾因跨日误判产生 57 条 route 负时长）。
            //   不静默写库 —— 记警告（按 route_id 去重，避免同一 run 刷屏），
            //   同时把 end 夹到 start（dur=0），保证库里不再出现 end<start 的脏数据。
            //   ★ 警告只是「兜底」：根因已在 RunRecordCollector.ResolveTs 修掉（跨日需 lastHour>=20 && curHour<=4）。
            if (close.EndAtMs > 0)
            {
                long startAt;
                using (var q = db.CreateCommand())
                {
                    q.CommandText = "SELECT start_at_ms FROM route WHERE route_id=$id;";
                    Add(q, "$id", close.RouteId);
                    object? v = q.ExecuteScalar();
                    startAt = v == null || v == DBNull.Value ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
                }
                if (startAt > 0 && close.EndAtMs < startAt)
                {
                    WarnNegativeDurationOnce(close.RouteId, "route", startAt, close.EndAtMs, "");
                    close = close with { EndAtMs = startAt, DurMs = 0 };
                }
            }
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"UPDATE route SET end_at_ms=$end,
                dur_ms=CASE WHEN $dur>0 THEN $dur ELSE MAX(0,$end-start_at_ms) END,
                state=$state,pick_count=$picks,pick_detail=$pickDetail,
                picked=CASE WHEN $pickedCnt>0 THEN $pickedCnt ELSE picked END,
                skipped=$skippedCnt,no_target=$noTargetCnt
                WHERE route_id=$id AND end_at_ms IS NULL;";
            Add(cmd,"$id",close.RouteId); Add(cmd,"$end",close.EndAtMs); Add(cmd,"$dur",close.DurMs);
            Add(cmd,"$state",close.State); Add(cmd,"$picks",close.PickCount); Add(cmd,"$pickDetail",close.PickDetail);
            Add(cmd,"$pickedCnt",close.Picked); Add(cmd,"$skippedCnt",close.Skipped); Add(cmd,"$noTargetCnt",close.NoTarget);
            cmd.ExecuteNonQuery();
        }

        private static int Purge(SqliteConnection db, int days, DateTimeOffset now)
        {
            days = days <= 0 ? 7 : Math.Min(days, 365);
            long cutoff = now.AddDays(-days).ToUnixTimeMilliseconds();
            int deleted;
            using (var tx = db.BeginTransaction())
            {
                using var cmd = db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"DELETE FROM run WHERE
                    (end_at_ms IS NOT NULL AND end_at_ms<$cutoff)
                    OR (end_at_ms IS NULL AND start_at_ms<$cutoff);";
                Add(cmd,"$cutoff",cutoff);
                deleted = cmd.ExecuteNonQuery();
                // state_span/pick_event/day_span 与 run 同期留存；pick_event 走 run 外键级联。
                cmd.CommandText=@"DELETE FROM state_span WHERE COALESCE(end_at_ms,start_at_ms)<$cutoff;
DELETE FROM day_span WHERE day<$dayCutoff;
DELETE FROM raw_event WHERE
                    (source_day<>'' AND source_day<$dayCutoff)
                    OR (source_day='' AND collected_at_ms<$cutoff);";
                cmd.Parameters.Clear();Add(cmd,"$cutoff",cutoff);
                Add(cmd,"$dayCutoff",now.AddDays(-days).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));
                cmd.ExecuteNonQuery();
                // raw 与 run 同期留存；source_day 缺省才使用采集时刻。
                cmd.CommandText = @"DELETE FROM raw_event WHERE
                    (source_day<>'' AND source_day<$dayCutoff)
                    OR (source_day='' AND collected_at_ms<$cutoff);";
                cmd.Parameters.Clear();
                Add(cmd,"$dayCutoff",now.AddDays(-days).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));
                Add(cmd,"$cutoff",cutoff);
                cmd.ExecuteNonQuery();
                tx.Commit();
            }
            if (deleted > 0)
            {
                using var checkpoint = db.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
                checkpoint.ExecuteNonQuery();
                using var free = db.CreateCommand();
                free.CommandText = "PRAGMA freelist_count;";
                long freePages = Convert.ToInt64(free.ExecuteScalar(), CultureInfo.InvariantCulture);
                using var count = db.CreateCommand();
                count.CommandText = "PRAGMA page_count;";
                long pages = Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (pages > 0 && freePages * 5 > pages)
                {
                    using var vacuum = db.CreateCommand();
                    vacuum.CommandText = "PRAGMA incremental_vacuum(128);";
                    vacuum.ExecuteNonQuery();
                }
            }
            return deleted;
        }

        private static void Add(SqliteCommand cmd, string name, object? value)
            => cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

        // 内部实例使隔离数据库和“重启”测试无需依赖全局状态。生产只使用上方静态门面。
        internal sealed class RunRecordStoreCore
        {
            private readonly Func<string> _getPath;
            // ★ t55：8192 太小 —— 历史回填时 4MB 日志块可产生上万事件，队列溢出会静默丢弃
        //   （实测「运行记录队列已满，本条已丢弃」→ AddRun 丢失 → AddRoute 外键失败）。
        //   放宽到 65536（约 8 倍），内存代价可忽略（每个 DbOp 几百字节）。
        private readonly BlockingCollection<DbOp> _queue = new(new ConcurrentQueue<DbOp>(), 65536);
            private readonly Lock _healthLock = new();
            private readonly Thread _writer;
            private SqliteConnection? _db;
            private bool _disabled;
            private bool _warningWritten;
            private int _queueWarning;

            internal RunRecordStoreCore(Func<string> getPath)
            {
                _getPath = getPath;
                _writer = new Thread(WriteLoop) { IsBackground = true, Name = "RunDbStore.Writer" };
                _writer.Start();
            }

            internal bool IsHealthy { get { lock (_healthLock) return !_disabled; } }

            internal void EnqueueWrite(Action<SqliteConnection> write)
            {
                if (!IsHealthy) return;
                TryEnqueue(new DbOp { Execute = db => { write(db); return null; } });
            }

            internal Task<T> EnqueueQuery<T>(Func<SqliteConnection,T> query, T fallback, CancellationToken ct)
            {
                if (ct.IsCancellationRequested) return Task.FromCanceled<T>(ct);
                if (ct.CanBeCanceled) ct.ThrowIfCancellationRequested();
                if (!IsHealthy) return Task.FromResult(fallback);
                var op = new DbOp { Execute = db => query(db), Fallback = fallback };
                if (!TryEnqueue(op)) return Task.FromResult(fallback);
                return AwaitQuery(op, fallback, ct);
            }

            private static async Task<T> AwaitQuery<T>(DbOp op, T fallback, CancellationToken ct)
            {
                try
                {
                    // 不在调用线程注册回调；后台消费时检查令牌，避免与完成回调竞态。
                    object? value = await op.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
                    return value is T typed ? typed : fallback;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    if (op.Fallback != null) op.Completion.TrySetResult(op.Fallback);
                    else op.Completion.TrySetCanceled();
                    throw;
                }
            }

            /// <summary>
            /// ★ t55：把「排空」实现为一个排到队尾的同步 op —— 它执行完说明之前入队的写都已落库。
            /// 不引入额外锁，复用同一个单线程写队列，天然有序。
            /// </summary>
            /// <summary>★ t63：队列当前积压条数（阻塞队列的 Count 是 O(1)）。</summary>
            internal int PendingWriteCount
            {
                get { try { return _queue.Count; } catch { return 0; } }
            }


            /// <summary>
            /// ★ t68：某账号某天的 run 条数（回填的「库缺失」判据）。
            /// ★ 走 EnqueueQuery（与其它查询一致的串行队列），**不自己开连接** ——
            ///   避免与写线程抢 SQLite 锁，也复用同一套降级/重试。
            /// </summary>
            internal Task<int> CountRunsOnDayAsync(string account, string day, CancellationToken ct = default)
                => EnqueueQuery<int>(db =>
                {
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = "SELECT COUNT(*) FROM run WHERE account=$a AND start_day=$d;";
                    cmd.Parameters.AddWithValue("$a", account);
                    cmd.Parameters.AddWithValue("$d", day);
                    object? o = cmd.ExecuteScalar();
                    return o == null || o == DBNull.Value ? 0 : Convert.ToInt32(o);
                }, 0, ct);

            internal bool WaitForWritesDrain(TimeSpan timeout)
            {
                if (!IsHealthy) return false;
                var op = new DbOp { Execute = db => null, Fallback = 0 };
                if (!TryEnqueue(op)) return false;
                try { return op.Completion.Task.Wait(timeout); }
                catch { return false; }
            }
            internal void Retry()
            {
                var op = new DbOp { Retry = true, Fallback = 0 };
                if (!_queue.IsAddingCompleted) _queue.TryAdd(op);
            }

            internal Task ShutdownAsync()
            {
                var op = new DbOp { Stop = true, Fallback = 0, Completion = new(TaskCreationOptions.RunContinuationsAsynchronously) };
                if (!_queue.IsAddingCompleted) _queue.TryAdd(op);
                return op.Completion.Task;
            }

            private bool TryEnqueue(DbOp op)
            {
                try
                {
                    if (_queue.TryAdd(op)) return true;
                }
                catch (InvalidOperationException) { }
                if (Interlocked.Exchange(ref _queueWarning, 1) == 0)
                {
                    ThreadPool.QueueUserWorkItem(_ => ReportWarning(
                        "RunRecordStore.Queue", new InvalidOperationException("运行记录队列已满，本条已丢弃。")));
                }
                return false;
            }

            private void WriteLoop()
            {
                try
                {
                    foreach (var op in _queue.GetConsumingEnumerable())
                    {
                        try
                        {
                            if (op.Stop)
                            {
                                _queue.CompleteAdding();
                                op.Completion.TrySetResult(0);
                                break;
                            }
                            if (op.Retry)
                            {
                                _db?.Dispose(); _db = null;
                                lock (_healthLock) _disabled = false;
                                _warningWritten = false;
                                EnsureOpen();
                                op.Completion.TrySetResult(0);
                                continue;
                            }
                            if (_disabled)
                            {
                                op.Completion.TrySetResult(op.Fallback);
                                continue;
                            }
                            EnsureOpen();
                            object? result = op.Execute?.Invoke(_db!);
                            op.Completion.TrySetResult(result);
                        }
                        catch (Exception ex) when (ex is SqliteException or IOException
                            or UnauthorizedAccessException or InvalidOperationException or DataException)
                        {
                            op.Completion.TrySetResult(op.Fallback);
                            Disable(ex);
                        }
                        catch (Exception ex)
                        {
                            // 屏障：任何存储问题都不能终结写线程或冒泡到日志解析线程。
                            op.Completion.TrySetResult(op.Fallback);
                            Disable(ex);
                        }
                    }
                }
                finally { _db?.Dispose(); _db = null; }
            }

            private void EnsureOpen()
            {
                if (_db != null) return;
                string path = Path.GetFullPath(_getPath());
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var csb = new SqliteConnectionStringBuilder
                {
                    DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate,
                    Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 5
                };
                var db = new SqliteConnection(csb.ToString());
                try
                {
                    db.Open();
                    using (var pragma = db.CreateCommand())
                    {
                        pragma.CommandText = @"PRAGMA busy_timeout=5000;
                            PRAGMA journal_mode=WAL;
                            PRAGMA synchronous=NORMAL;
                            PRAGMA foreign_keys=ON;
                            PRAGMA auto_vacuum=INCREMENTAL;";
                        pragma.ExecuteNonQuery();
                    }
                    EnsureSchema(db);
                    _db = db;
                    lock (_healthLock) _disabled = false;
                }
                catch { db.Dispose(); throw; }
            }

            private void Disable(Exception ex)
            {
                lock (_healthLock) _disabled = true;
                try { _db?.Dispose(); } catch { }
                _db = null;
                if (!_warningWritten)
                {
                    _warningWritten = true;
                    ReportWarning("RunRecordStore.Writer", ex);
                }
            }

            private static void ReportWarning(string source, Exception ex)
            {
                try
                {
                    var sink = DiagnosticSink;
                    if (sink != null) sink(source, ex);
                    else App.AppendErrorLog(source, ex);
                }
                catch { }
            }
        }

        private sealed class DbOp
        {
            internal Func<SqliteConnection,object?>? Execute;
            internal object? Fallback;
            internal bool Retry;
            internal bool Stop;
            internal TaskCompletionSource<object?> Completion { get; init; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

        }

        private static void EnsureSchema(SqliteConnection db)
        {
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS run (
 run_id TEXT PRIMARY KEY,account TEXT NOT NULL,group_name TEXT NOT NULL,start_at_ms INTEGER NOT NULL,
 end_at_ms INTEGER,dur_ms INTEGER NOT NULL DEFAULT 0,state TEXT NOT NULL,
 script_count INTEGER NOT NULL DEFAULT 0,exp INTEGER NOT NULL DEFAULT 0,mora INTEGER NOT NULL DEFAULT 0,
 picks INTEGER NOT NULL DEFAULT 0,src_file_start TEXT NOT NULL DEFAULT '',src_file_end TEXT NOT NULL DEFAULT '',
 start_day TEXT NOT NULL DEFAULT '',end_day TEXT NOT NULL DEFAULT '',is_cross_day INTEGER NOT NULL DEFAULT 0,
 is_backfilled INTEGER NOT NULL DEFAULT 0,detail TEXT NOT NULL DEFAULT '',
 round_index INTEGER NOT NULL DEFAULT 0,round_total INTEGER NOT NULL DEFAULT 0,
 round_host TEXT NOT NULL DEFAULT '',round_count INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS route (
 route_id TEXT PRIMARY KEY,run_id TEXT NOT NULL REFERENCES run(run_id) ON DELETE CASCADE,
 kind TEXT NOT NULL,name TEXT NOT NULL,parent_route_id TEXT,start_at_ms INTEGER NOT NULL,end_at_ms INTEGER,
 dur_ms INTEGER NOT NULL DEFAULT 0,state TEXT NOT NULL,pick_count INTEGER NOT NULL DEFAULT 0,
 pick_detail TEXT NOT NULL DEFAULT '{}',src_file TEXT NOT NULL DEFAULT '');
CREATE TABLE IF NOT EXISTS err (
 err_id INTEGER PRIMARY KEY AUTOINCREMENT,run_id TEXT NOT NULL REFERENCES run(run_id) ON DELETE CASCADE,
 route_id TEXT,ts_ms INTEGER NOT NULL,level TEXT NOT NULL DEFAULT 'ERR',
 exception_type TEXT NOT NULL DEFAULT '',message TEXT NOT NULL DEFAULT '',detail TEXT NOT NULL DEFAULT '',
 source TEXT NOT NULL DEFAULT '',content_hash TEXT NOT NULL,src_file TEXT NOT NULL DEFAULT '');
-- t6：拾取事件投影（picked/no_target/skipped 三类，原话照抄；自然键含行序号+偏移保幂等）
CREATE TABLE IF NOT EXISTS pick_event (
 pick_id INTEGER PRIMARY KEY AUTOINCREMENT,run_id TEXT NOT NULL REFERENCES run(run_id) ON DELETE CASCADE,
 route_id TEXT,kind TEXT NOT NULL,item TEXT NOT NULL DEFAULT '',ts_ms INTEGER NOT NULL,
 line_no INTEGER NOT NULL,line_offset INTEGER NOT NULL,src_file TEXT NOT NULL DEFAULT '',
 chunk_offset INTEGER NOT NULL DEFAULT 0);
-- t6：BGI 状态时段（三态 running/idle/not_running，只在切换时开/闭段）
CREATE TABLE IF NOT EXISTS state_span (
 span_id INTEGER PRIMARY KEY AUTOINCREMENT,account TEXT NOT NULL,
 state TEXT NOT NULL,start_at_ms INTEGER NOT NULL,end_at_ms INTEGER,dur_ms INTEGER NOT NULL DEFAULT 0,
 src_file TEXT NOT NULL DEFAULT '');
-- t11/t12：按自然日裁切的全天事实桶，未明确状态归 unknown。
CREATE TABLE IF NOT EXISTS day_span (
 account TEXT NOT NULL,day TEXT NOT NULL,running_ms INTEGER NOT NULL DEFAULT 0,
 idle_ms INTEGER NOT NULL DEFAULT 0,not_running_ms INTEGER NOT NULL DEFAULT 0,
 unknown_ms INTEGER NOT NULL DEFAULT 86400000,computed_at_ms INTEGER NOT NULL,is_final INTEGER NOT NULL DEFAULT 0,
 PRIMARY KEY(account,day),
 CHECK(running_ms>=0 AND idle_ms>=0 AND not_running_ms>=0 AND unknown_ms>=0),
 CHECK(running_ms+idle_ms+not_running_ms+unknown_ms=86400000));
CREATE TRIGGER IF NOT EXISTS trg_day_span_nonnegative_insert BEFORE INSERT ON day_span
WHEN NEW.running_ms<0 OR NEW.idle_ms<0 OR NEW.not_running_ms<0 OR NEW.unknown_ms<0
 OR NEW.running_ms+NEW.idle_ms+NEW.not_running_ms+NEW.unknown_ms<>86400000
BEGIN SELECT RAISE(ABORT,'day_span invariant violated'); END;
CREATE TRIGGER IF NOT EXISTS trg_day_span_nonnegative_update BEFORE UPDATE ON day_span
WHEN NEW.running_ms<0 OR NEW.idle_ms<0 OR NEW.not_running_ms<0 OR NEW.unknown_ms<0
 OR NEW.running_ms+NEW.idle_ms+NEW.not_running_ms+NEW.unknown_ms<>86400000
BEGIN SELECT RAISE(ABORT,'day_span invariant violated'); END;
-- t8：原始日志事实，不能因内容“无效”而丢弃。
CREATE TABLE IF NOT EXISTS raw_event (
 raw_id TEXT PRIMARY KEY,account TEXT NOT NULL,src_file TEXT NOT NULL,
 chunk_offset INTEGER NOT NULL,line_offset INTEGER NOT NULL,line_no INTEGER NOT NULL,
 ts_ms INTEGER,level TEXT NOT NULL DEFAULT '',logger TEXT NOT NULL DEFAULT '',
 raw_text TEXT NOT NULL,raw_chars INTEGER NOT NULL,raw_truncated INTEGER NOT NULL DEFAULT 0,
 anchor TEXT NOT NULL DEFAULT '',collected_at_ms INTEGER NOT NULL,
 content_hash TEXT NOT NULL,occurrence INTEGER NOT NULL DEFAULT 0,source_day TEXT NOT NULL DEFAULT '',
 byte_offset INTEGER NOT NULL DEFAULT 0,byte_length INTEGER NOT NULL DEFAULT 0,
 file_size_at_collection INTEGER NOT NULL DEFAULT 0,file_write_at_ticks INTEGER NOT NULL DEFAULT 0);";
                cmd.ExecuteNonQuery();
            }

            EnsureColumn(db,"day_span","is_final","INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(db,"raw_event","source_day","TEXT NOT NULL DEFAULT ''");
            EnsureColumn(db,"raw_event","byte_offset","INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(db,"raw_event","byte_length","INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(db,"raw_event","file_size_at_collection","INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(db,"raw_event","file_write_at_ticks","INTEGER NOT NULL DEFAULT 0");
            // t6：路线三分类拾取计数（picked 语义不变=pick_count；另两列补记「没拾到」）
            EnsureColumn(db,"route","picked","INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(db,"route","skipped","INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(db,"route","no_target","INTEGER NOT NULL DEFAULT 0");
            // t6：计划对账（只有数量没有名字的锚点落到 run 行）
            EnsureColumn(db,"run","planned_routes","INTEGER NOT NULL DEFAULT 0");
            // t6：跨天换文件分段标记（O5 两条都置 1 的「参与跨天分段」语义）
            EnsureColumn(db,"run","day_split","INTEGER NOT NULL DEFAULT 0");
            // t6：拾取守恒差异（sum(route.picked)+run级 ≠ 实际行数时写 N，不许静默吞掉）
            EnsureColumn(db,"run","pick_unattributed","INTEGER NOT NULL DEFAULT 0");
            // 轮次（2026-11-26）：锄地类 `[第 N/M 轮 房主]`；无轮次的组保持 0/空。
            EnsureColumn(db,"run","round_index","INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(db,"run","round_total","INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(db,"run","round_host","TEXT NOT NULL DEFAULT ''");
            EnsureColumn(db,"run","round_count","INTEGER NOT NULL DEFAULT 0");
            // 初版已使用自然键；若部署过草稿库，仅补缺失列，不删除已有行。
            EnsureColumn(db,"run","src_file_end","TEXT NOT NULL DEFAULT ''");
            EnsureColumn(db,"run","end_day","TEXT NOT NULL DEFAULT ''");
            EnsureColumn(db,"run","is_cross_day","INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(db,"run","is_backfilled","INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(db,"route","parent_route_id","TEXT");
            EnsureColumn(db,"route","pick_count","INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(db,"route","pick_detail","TEXT NOT NULL DEFAULT '{}'");
            EnsureColumn(db,"err","content_hash","TEXT NOT NULL DEFAULT ''");
            EnsureColumn(db,"err","src_file","TEXT NOT NULL DEFAULT ''");
            // t35：pick_event 跨 chunk 去重维度（LineNo 是 chunk 内偏移，跨 chunk 重复）
            EnsureColumn(db,"pick_event","chunk_offset","INTEGER NOT NULL DEFAULT 0");
            // ★ 2026-11-26：chunk_offset 是进程内自增序号（RawChunkSequences），重启/断点续传
            //   会归零，导致同一物理行以不同 chunk_offset 重复入库（实测 11426 组重复）。
            //   去重键改为 (run_id,ts_ms,line_no,line_offset,kind) —— 全部由日志内容派生、
            //   跨重启稳定。chunk_offset 列保留（读侧 ORDER BY 仍用），但不再参与唯一约束。

            using var index = db.CreateCommand();
            index.CommandText = @"
CREATE INDEX IF NOT EXISTS ix_run_account_end ON run(account,end_at_ms DESC);
CREATE INDEX IF NOT EXISTS ix_run_group ON run(group_name);
CREATE INDEX IF NOT EXISTS ix_run_end_day ON run(end_day);
CREATE INDEX IF NOT EXISTS ix_run_open ON run(account) WHERE end_at_ms IS NULL;
CREATE INDEX IF NOT EXISTS ix_route_run ON route(run_id);
CREATE INDEX IF NOT EXISTS ix_err_run ON err(run_id);
CREATE INDEX IF NOT EXISTS ix_err_route ON err(route_id);
CREATE INDEX IF NOT EXISTS ix_err_ts ON err(ts_ms);
CREATE UNIQUE INDEX IF NOT EXISTS ux_err_dedup ON err(run_id,ts_ms,level,content_hash);
DROP INDEX IF EXISTS ux_pick_dedup;
CREATE UNIQUE INDEX IF NOT EXISTS ux_pick_dedup ON pick_event(run_id,ts_ms,line_no,line_offset,kind);
CREATE INDEX IF NOT EXISTS ix_pick_run ON pick_event(run_id);
CREATE INDEX IF NOT EXISTS ix_pick_route ON pick_event(route_id);
CREATE INDEX IF NOT EXISTS ix_pick_kind_ts ON pick_event(kind,ts_ms);
CREATE INDEX IF NOT EXISTS ix_span_account ON state_span(account,start_at_ms);
CREATE INDEX IF NOT EXISTS ix_day_span_day ON day_span(account,day);
CREATE INDEX IF NOT EXISTS ix_raw_account_file ON raw_event(account,src_file);
CREATE INDEX IF NOT EXISTS ix_raw_ts ON raw_event(ts_ms);
CREATE INDEX IF NOT EXISTS ix_raw_source_day ON raw_event(source_day);
PRAGMA user_version=1;";
            index.ExecuteNonQuery();
        }

        private static void EnsureColumn(SqliteConnection db, string table, string column, string ddl)
        {
            // table/column/ddl 仅来自上方常量；用户数据全部走参数，绝不拼 SQL。
            bool exists = false;
            using (var check = db.CreateCommand())
            {
                check.CommandText = "PRAGMA table_info(" + table + ");";
                using var reader = check.ExecuteReader();
                while (reader.Read())
                    if (string.Equals(reader.GetString(1),column,StringComparison.OrdinalIgnoreCase)) exists = true;
            }
            if (exists) return;
            using var alter = db.CreateCommand();
            alter.CommandText = "ALTER TABLE " + table + " ADD COLUMN " + column + " " + ddl + ";";
            alter.ExecuteNonQuery();
        }
    }
}