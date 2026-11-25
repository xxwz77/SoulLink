# 数据中心 SQLite 存储层（t2 交付）

> 日期：2026-11-25  
> 实现：`Services/RunRecordStore.cs`  
> 契约：`DATA-CENTER-RUN-DB-20261125.md`

## 1. 交付范围

- 官方 `Microsoft.Data.Sqlite 11.0.0`，与 `Server/SoulLink.Server.csproj` 一致。
- 数据库：`AppIdentity.DataDir/run-db/run.db`。不写 `dist`，不创建任何源树内数据库。
- 契约规定的 `run / route / err` 三表、索引、WAL、busy_timeout=5000、synchronous=NORMAL、外键级联、增量 vacuum。
- 有界 `BlockingCollection<DbOp>`（8192）+ 专用后台线程 `RunDbStore.Writer`；写 API 只校验、复制不可变 DTO、`TryAdd`。
- 查询通过同一队列串行执行，返回 Task；调用方 `await`，不阻塞 UI 线程，也不用 `.Result`。
- 只使用参数化 SQL。表名/列名拼接仅限源码内固定 migration 常量。
- 故障在后台线程记录一次 `RunRecordStore.Writer` 警告并降级；不冒泡到日志采集线程。`RetryDatabase()` 显式重连。
- **schema migration** 是幂等补列/建索引。**JSONL 历史数据搬迁、Feed 归因、TickHub、留存设置字段**均留给 t4，未冒充本任务已完成。

## 2. 公共 API

命名空间 `SoulLink.Services`，静态类 `RunRecordStore`。

### 写入（均 void，不在调用线程做 SQLite IO）

```csharp
void AddRun(RunRecord row);
void AddRoute(RouteRecord row);
void AddError(ErrorRecord row);
void CloseRun(RunClose close);
void CloseRoute(RouteClose close);
void PurgeOlderThan(int days);
```

`AddRun` 为 INSERT OR IGNORE：同一自然键重扫不覆盖既有 open/closed 行。
`AddRoute` 可重复喂当前拾取快照；已结束路线不被旧快照重新打开。
`AddError` 的 ContentHash 为空时自动取 SHA-256(`ts_ms|level|detail`) 的前 16 hex。
`CloseRun/CloseRoute` 仅更新 open 行。CloseRun 保存 Exp/Mora/Picks 与跨天/补全字段。

### 查询与生命周期

```csharp
Task<IReadOnlyList<RunRecord>> LoadOpenRunsAsync(string? account = null, CancellationToken ct = default);
Task<RunRecord?> FindOpenRunAsync(string account, string groupName, CancellationToken ct = default);
Task<IReadOnlyList<RunRecord>> QueryRunsAsync(string account, int days = 1, CancellationToken ct = default);
Task<IReadOnlyList<RouteRecord>> QueryRoutesAsync(string runId, CancellationToken ct = default);
Task<IReadOnlyList<ErrorRecord>> QueryErrorsAsync(string runId, CancellationToken ct = default);
Task<IReadOnlyList<RunGroupSummary>> QueryGroupsAsync(string account, int days = 7, CancellationToken ct = default);
Task<int> PurgeOlderThanAsync(int days, CancellationToken ct = default);
Task FlushAsync(CancellationToken ct = default);
void RetryDatabase();
```

- `FindOpenRunAsync` 按 account + group 取最新 open 行，用于重启后结束事件接续原 run。
- `QueryRunsAsync` 按 `COALESCE(end_at_ms,start_at_ms)` 过滤近 N 天，因此含 open 行。
- `PurgeOlderThan(days)`：≤0 回退默认 7，最大 365。删除已过期行及其路线/报错。
- `FlushAsync` 是此前操作的 FIFO 屏障，不代表故障库恢复成功；查看 `IsHealthy`。
- `IsHealthy / DatabasePath / DiagnosticSink` 为状态与诊断接口。

### ID 与 DTO

```csharp
string MakeRunId(string account, string groupName, long startAtMs);
string MakeRouteId(string runId, string kind, string name, long startAtMs);
string MakeErrorContentHash(long tsMs, string? level, string? detail);
```

`RunRecord`：RunId, Account, GroupName, StartAtMs, EndAtMs, DurMs, State, ScriptCount,
Exp, Mora, Picks, SrcFileStart, SrcFileEnd, StartDay, EndDay, IsCrossDay, IsBackfilled, Detail。

`RouteRecord`：RouteId, RunId, Kind, Name, ParentRouteId, StartAtMs, EndAtMs, DurMs,
State, PickCount, PickDetail, SrcFile。

`ErrorRecord`：ErrId, RunId, RouteId, TsMs, Level, ExceptionType, Message, Detail,
Source, ContentHash, SrcFile。

`RunClose`：RunId, EndAtMs, State, Detail, SrcFileEnd, EndDay, IsCrossDay, IsBackfilled, Exp, Mora, Picks。

`RouteClose`：RouteId, EndAtMs, DurMs, State, PickCount, PickDetail。

`RunGroupSummary`：Group, Runs, Failures, AvgExp, AvgMora, TotalExp, TotalMora, TotalPicks,
TotalDurMs, LastEndAtMs。

## 3. 可执行验证

临时 console **直接链接生产 `RunRecordStore.cs` 与 `AppIdentity.cs`**，只桩 App.AppendErrorLog。
所有数据库位于 `%TEMP%\soullink-run-db-t2-data`；验证后整个临时工程与数据目录删除。
`RUN_DB_TEST` 条件段仅在该临时工程启用，用于切换真实独立存储实例；产品不编译此入口。

### 主项目

```text
> dotnet build SoulLink.csproj -c Release --nologo
    326 个警告
    0 个错误
已用时间 00:00:11.23
```

两个 TFM 均编译：`net11.0-windows`、`net11.0-windows10.0.26100.0`。
现有项目有基线 nullable/未使用字段警告；最后一次构建无 `RunRecordStore.cs` 警告。

### 单文件与原生库

```powershell
dotnet publish RunRecordStoreProbe.csproj -c Release -r win-x64 --self-contained false ` -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o publish --nologo
.\publish\RunRecordStoreProbe.exe
```

```text
RUNTIME native SQLite=3.53.4 framework=11.0.0
PUBLISH single-file=True process=...\publish\RunRecordStoreProbe.exe
RESULT ALL CHECKS PASSED; warnings=2
SINGLE_FILE_EXIT=0
```

结论：官方包默认 SQLitePCLRaw bundle 已正确打包/释放 e_sqlite3；**无需 Core 包或手工原生 DLL 方案**。
探针发布目录只有 `RunRecordStoreProbe.exe`（2,415,970 B），从该 exe 完成全套建库、插入和读回。

### 全部检查点（同一次发布 exe 运行）

```text
PASS 建库与首次写入
PASS 数据库路径位于 DataDir/run-db
PASS open 行查询
PASS 按账号和配置组补全查询
PASS 路线拾取快照查询
PASS 报错入库与自动哈希
PASS 跨天 23:00 至次日 01:00 保持一条且耗时 7200000ms
PASS CloseRun 重扫不覆盖结束状态
PASS 参数化 SQL 保留注入形状文本
PASS 路线/错误重启重扫去重
PASS 按组聚合查询
PASS 留存清理删除 3 条过期 run
PASS 留存范围内运行保留
PASS 外键已启用
PASS 留存清理通过外键级联路线和报错
PASS 三表 schema 已建
PASS 关闭存储前持久化 open 行
PASS 关闭并重新打开存储后重扫不重复
PASS 旧 schema 打开迁移未降级
PASS 旧 schema 补列并建立唯一索引（run/route/err 列数 18/12/11）
PASS 损坏库仅警告一次且所有调用安全降级
PASS 真实写锁超时后安全降级
PASS 释放占用后 RetryDatabase 恢复写入
```

跨天样本：start_day=2026-11-24、end_day=2026-11-25、dur_ms=7,200,000、is_cross_day=1、is_backfilled=1。
占用测试由第二个真实 SqliteConnection 持有写事务，busy_timeout 到期后才断言降级，再回滚验证恢复。

## 4. 交接边界

- t3 只负责采集状态机，调用以上 API。
- t4 必须接 `AppSettings.RunDbRetentionDays`（默认 7）、启动/每小时 TickHub 清理与 `RetryDatabase`、历史 JSONL 一次性搬迁，以及 RouteLedger 查询兼容层。
- 未改 BgiLogCenter、RouteLedger、Server/Db.cs、UI/XAML、data、dist；未新增 DispatcherTimer；未发布版本。
