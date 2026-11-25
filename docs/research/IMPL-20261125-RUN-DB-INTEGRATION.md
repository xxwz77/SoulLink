# 运行记录数据库 —— 集成落地（t4，2026-11-25 / 3.3.41）

> 承接 [DATA-CENTER-RUN-DB-20261125.md](DATA-CENTER-RUN-DB-20261125.md)（契约）与
> [RUN-RECORD-STORE-20261125.md](RUN-RECORD-STORE-20261125.md)（存储层）。
> 本文记录**挂载点、留存配置、周期清理、只读 API** 四处集成的实现与实测证据。
> 前置：[DATA-CENTER-SINGLE-SOURCE-20261123.md](DATA-CENTER-SINGLE-SOURCE-20261123.md)（唯一入口规则）。

## 1. 改动清单（本任务 t4）

**t4 新增 1 个文件**：

| 文件 | 动作 | 行数 |
|---|---|---|
| `Services/RunRecordHost.cs` | **新增**：启动重建、留存清理、小时拍、退出排空 | 167 |

**t4 修改 6 个既有文件**（`git diff --numstat`）：

| 文件 | 动作 | 增/删 |
|---|---|---|
| `Services/WebServer.cs` | 三个只读 API + 路由注册 | +218 / 0 |
| `Services/BgiLogCenter.cs` | 挂载：ProcessChunk 一处 Feed（:709）、Step 一处 OnFileChanged（:571） | +14 / 0 |
| `Models/Models.cs` | `AppSettings.RunDbRetentionDays`（默认 7，[1,365]，零 UI） | +14 / 0 |
| `App.xaml.cs` | 启动 `RunRecordHost.Start()`、退出 `RunRecordHost.Shutdown()` | +14 / 0 |
| `SoulLink.csproj` | `<Version>` 3.3.40 → **3.3.41** | +1 / -1 |
| `docs/research/README.md` | 登记本文 | +5 / 0 |

**依赖任务交付、t4 只加了一个只读查询门面**：

| 文件 | 来源 | t4 动作 |
|---|---|---|
| `Services/RunRecordStore.cs`（774 行） | t2 | 增 `FindRunAsync(runId)` +14（`/api/bgi/runs/detail` 按 id 精查用），其余未动 |
| `Services/RunRecordCollector.cs`（952 行） | t3 | **一行未改**，按 t3 给出的挂载契约调用 `Feed` / `EnsureBootedAsync` / `OnFileChanged` |

**没有**新增 XAML、WPF 页面、Web 页面、DispatcherTimer、FileSystemWatcher、进程枚举、第二条日志扫描链。

## 2. 挂载点（file:line，3.3.41 改后行号）

### 2.1 增量日志 → 归因（Services/BgiLogCenter.cs:709）

    try { RouteLedger.Feed(e.UserName, chunk); } catch { }              // :705 既有台账
    try { RunRecordCollector.Feed(e.UserName, chunk, e.LogFile); } catch { }   // :709 新增

- 位于 `ProcessChunk` 内，**紧邻** RouteLedger.Feed，吃同一个 `chunk`、同一份字节游标
  （`Pump` :583-684 不变）→ 不新增扫描链，只多若干已编译正则 + 内存状态机 + 入队。
- 传入账号 `e.UserName`、chunk、以及**日志文件路径** `e.LogFile`：采集器用文件名里的
  `YYYYMMDD` 建立天轴（跨日 +1 天推断），文件路径只做字符串解析，不读文件。
- 双层 try/catch：`Feed` 内部第一层（RunRecordCollector.cs:357），挂载点第二层 → 异常绝不冒泡到日志解析。
- 线程：`ProcessChunk` 由 `ServiceTick`（TickHub.RegisterBackground，BgiLogCenter.cs:421）
  与 `EnsureWatcher` 的 1 秒合并窗口驱动，**在线程池**；即使被 UI 相关路径调用，采集也只做内存归因。

### 2.2 文件滚动 → 跨天补全（Services/BgiLogCenter.cs:571）

    if (!string.Equals(e.LogFile, logFile, ...)) {
        e.ResetForNewFile(logFile); e.Version++;
        try { RunRecordCollector.OnFileChanged(e.UserName, logFile); } catch { }   // :571
    }

- 语义（契约 §4.4）：内存有 open 窗口且 gap ≤ 300s → **续跑**（start 时刻保留，跨天仍落 1 条）；
  gap > 300s → `close(Interrupted)` 兜底，不虚构成跨天完成。
- 纯内存判定，无文件 IO，在 `Step` 的 `lock (Sync)` 内——采集器用自己的 `StateLock`，
  两级锁不重入同一把，无死锁路径（采集器从不回调 BgiLogCenter）。

### 2.3 启动（App.xaml.cs:698）与退出（App.xaml.cs:180）

    Services.RunRecordHost.Start();      // ShowMainWindow，紧邻 AccountStateCenter.Start 之前
    Services.RunRecordHost.Shutdown();   // Exit 事件里，WebServer.Stop 之后

`Start()` 必须在 BgiLogCenter 开始喂日志**之前**——否则「重启后跨天续跑」会先看到新文件而
没有 DB open 行可继承，start 时刻就丢了。

## 3. 留存配置（Models/Models.cs:405-417）

    /// 数据中心「配置组运行记录」（SQLite）的留存天数 —— 用户可自行设置留存时间。
    /// ★ 默认 7 天。合法范围 [1, 365]：缺失/≤0/非数字 → 7；>365 → 365。
    /// ★ 与 BgiBackupKeep（0 = 不限制）语义不同：本字段 0 不表示「无限」，否则库无界增长。
    /// ★ 刻意不做界面（用户要求不展示）：要改就手改 config.json。
    public int RunDbRetentionDays { get; set; } = 7;

- **旧配置兼容**：逆序列化缺字段时保留属性初始值 7（实测断言见 §6.1），无需迁移代码、无需改 ConfigStore。
- **归一**：`RunRecordHost.NormalizeRetentionDays`：`<=0 → 7`，`>365 → 365`（与存储层 Purge 的 clamp 同一口径）。
- **不做 UI**：无 XAML、无绑定、无设置页条目。改值 = 手改 `config.json` 的 `Settings.RunDbRetentionDays`。

## 4. 周期清理（Services/RunRecordHost.cs）

    ① 启动后一次（后台 Task.Run）：EnsureBootedAsync（open 行灌回内存窗口）+ PurgeOnceAsync("启动")
    ② TickHub.RegisterBackground("RunRecordHost.Cleanup", 3600, ...) → PurgeOnceAsync("周期")
    ③ 退出：RunRecordStore.FlushAsync().Wait(2000)（有界排空，失败静默）

- **不新建 DispatcherTimer**（AGENTS.md 性能约束 1）；**不新增日期轮询**：跨天由「每小时一次」覆盖。
- **幂等**：`PurgeOlderThanAsync` 是纯 DELETE，无过期行时返回 0、不做任何写放大（实测第二次删除 0 行）。
  存储层只在删除行数 > 0 时才 wal_checkpoint，且空闲页占比 > 20% 才 incremental_vacuum。
- **失败只记一次警告**：`_warnLatch` 闩锁，连续失败只落 `LogBus.Warn` + `error.log` 各一条；
  任一次成功即复位，下次真故障还能再报。
- 清理在**写线程**执行（`EnqueueWrite`/`EnqueueQuery` 同一串行队列），不与写入竞争，TickHub 只投递。

## 5. 只读查询 API（Services/WebServer.cs:361 注册，:582 起实现）

三个接口全部在 `IsAuthorized` 之后（WebServer.cs:348 的鉴权闸门），且**只返回数据、不做任何展示**。

| 路由 | 参数 | 返回 |
|---|---|---|
| `GET /api/bgi/runs` | `user`（必填）、`days`（默认 7，clamp [1,365]） | `{ok,user,days,source:"run-db",retentionDays,count,items[]}`，items = 运行记录（开始/结束时刻、时长、状态、跨天/补全标记、收益、拾取数、来源文件） |
| `GET /api/bgi/runs/detail` | `id`（runId）或 `user`+`latest=1` | `{ok,runId,found,run,routes[],errors[],routeCount,errorCount}`，routes 含 parentRouteId/拾取详情，errors 含异常类型/消息/**完整堆栈** |
| `GET /api/bgi/runs/groups` | `user`（必填）、`days` | `{ok,user,days,count,items[]}`，按组聚合（次数/失败数/平均与总计收益/总拾取/最后结束时刻） |

- 时间字段同时给 `startAt`（本地 `yyyy-MM-dd HH:mm:ss` 字符串，人读）与 `startAtMs`（原始毫秒，机读）。
- 来源文件名只给 basename（`Path.GetFileName`）—— **不泄露本地绝对路径**，与 `ApiBgiLedger` 口径一致。
- 查询本身也入队给存储层的专用后台线程，HttpListener 线程零磁盘 IO。
- **老接口零改动**：`/api/bgi/ledger`（RouteLedger/JSONL）与 `/api/bgi/stats` 的返回结构一字未动（实测见 §6.3）。

## 6. 验收实测（2026-11-25，独立验证工程 tools/rundb-check）

验证工程 `tools/rundb-check/RunDbCheck.csproj`（ProjectReference 到 SoulLink.csproj，零实现复制），
沙箱 = `%TEMP%\sl-rundb-*\data`（SOULLINK_DATA_DIR / SOULLINK_CONFIG），
日志 = **真实日志副本**（`D:\AAA挂机专区\A4\BetterGI\log\better-genshin-impact2026112{4,5}.log`）。

### 6.1 构建

    dotnet build SoulLink.csproj -c Release  →  0 个错误（双 TFM，326 个既有警告不变）

### 6.2 配置 + 宿主（hostonly）

    ok 留存 7 原样 / ok 旧配置缺字段/0 → 默认 7 / ok 负数 → 默认 7 / ok 365 保留 / ok >365 → 365
    ok RunRecordHost.Start 幂等（StartCount=1）        ok RetentionDays 读到配置 7
    ok 反序列化旧配置（无该字段）→ 默认 7              ok 数据库健康（建库成功）
    purge@start rows=0 at=14:33:34 healthy=True

### 6.3 挂载点产出真实数据（feed，走 BgiLogCenter.Tick → Pump → ProcessChunk → Feed 唯一入口）

A4 / 罐子激活（20261125 日志）：

    runs=1
      RUN group=罐子激活 start=11-25 12:01:23.316 end=11-25 12:16:26.056 state=Failed
          scripts=3 picks=0 cross=False backfill=False durSec=902
      ROUTE script 圣遗物调查点纯激活 Failed durSec=898 / script 自动莫酱 Finished / script 退出游戏 Failed
    ERR type=Microsoft.ClearScript.ScriptEngineException  detailLen=5694
    ERR type=Microsoft.ClearScript.ScriptEngineException  detailLen=12130   ← 跨十几行完整栈
    routes=3 errors=4          ok 挂载点产出运行记录 / ok 明细落库 / ok 报错详情落库
    after rescan runs=1 routes=3 errs=4   ok 幂等（三项都不变）

A2 / 联机锄地（20261124 日志，含拾取）：

    runs=2
      RUN group=联机锄地-罐子 start=11-26 05:45:17 end=11-26 06:03:39 state=Finished picks=73 durSec=1102
      RUN group=罐子激活     start=11-25 12:01:22 end=11-25 12:15:24 state=Failed   scripts=3
      ROUTE route 07【额外】至冬-旧基捷城西盗罐者6.json picks=4  detail={"游医":2,"幸运儿":2} parent=y
      ROUTE route 08【额外】至冬-旧基捷城东盗罐者17.json picks=16 detail={"游医":7,"幸运儿":4,"冒险家":5}
      ... （共 11 条路线 + 3 个脚本）
    routes=14 routesWithPicks=8 totalPicks=73 errors=4
    ok 逐路线拾取详情落库（pickCount>0）      after rescan runs=2 routes=14 errs=4  ok 幂等

### 6.4 留存清理生效（purge）

    （造数：8 天前 Finished、6 天前 Finished、10 天前 open 行）
    清理前 run 行=3
    PurgeOlderThanAsync(7) 删除行=2  LastPurgedRows=2
    清理后 run 行=1   →  只剩「新·六天前」
    ok 8 天前 + 10 天前 open 行被删除 / ok 6 天前保留 / ok 8 天前已删 / ok 陈旧 open 已删
    再跑一次删除行=0   ok 清理幂等

### 6.5 只读 API（Invoke-WebRequest 实测，起真实 HttpListener 于 127.0.0.1:10871，特意设口令）

未登录：

    GET /api/bgi/runs?user=A2            → 401 未登录
    GET /api/bgi/runs/detail?id=x        → 401 未登录
    GET /api/bgi/runs/groups?user=A2     → 401 未登录

登录后 `GET /api/bgi/runs?user=A2&days=7`：

    {"ok":true,"user":"A2","days":7,"source":"run-db","retentionDays":7,"count":2,"items":[
      {"runId":"A2\u001F联机锄地-罐子\u001F1795643117239","account":"A2","group":"联机锄地-罐子",
       "startAt":"2026-11-26 05:45:17","startAtMs":1795643117239,
       "endAt":"2026-11-26 06:03:39","endAtMs":1795644219801,"durSec":1102.562,
       "state":"Finished","scriptCount":1,"exp":0,"mora":0,"picks":73,
       "startDay":"2026-11-25","endDay":"2026-11-25","crossDay":false,"backfilled":false,
       "srcFile":"better-genshin-impact20261125.log"},
      {"runId":"A2\u001F罐子激活\u001F1795579282916","group":"罐子激活","state":"Failed",
       "startAt":"2026-11-25 12:01:22","endAt":"2026-11-25 12:15:24","durSec":841.429,"scriptCount":3}]}

`GET /api/bgi/runs/detail?id=<罐子激活 runId>`：

    ok=True found=True group=罐子激活 state=Failed routes=3 errors=4
      ERR type=                                             msg=脚本停止：线路 7至冬额外激活CD.json 终点校验失败，距离：1511.12  detailLen=143
      ERR type=Microsoft.ClearScript.ScriptEngineException   msg=激活脚本结束：激活脚本执行失败…                      detailLen=5694
      ERR type=Microsoft.ClearScript.ScriptEngineException   msg=执行脚本时发生异常: "Error: 激活脚本执行失败…"        detailLen=12008
      ERR type=                                             msg=执行脚本时发生异常: "Error: A task was canceled."   detailLen=138
      第一个报错 detail 前 3 行（完整多行栈已落库）：
        [04:15:19.687] [ERR] [Primary:S3:P24500:T1795464022398] BetterGenshinImpact.Core.Script.Dependence.Log
        激活脚本结束：激活脚本执行失败，详见上方日志与 records 记录
        执行脚本时发生异常

`GET /api/bgi/runs/groups?user=A2&days=7`：

    {"ok":true,"user":"A2","days":7,"count":2,"items":[
      {"group":"罐子激活","runs":1,"failures":1,"totalPicks":0,"totalDurSec":841.429,"lastEndAt":"2026-11-25 12:15:24"},
      {"group":"联机锄地-罐子","runs":1,"failures":0,"totalPicks":73,"totalDurSec":1102.562,"lastEndAt":"2026-11-26 06:03:39"}]}

老接口兼容（同一进程同一库）：

    GET /api/bgi/ledger?user=A2&days=7 → ok=True 顶层字段=ok,user,days,records,groups,zeroReward,failed
                                          records=2 groups=2 failed=1
    GET /api/bgi/stats?user=A2         → ok=True user=A2 pickTotal=0

## 7. 硬约束逐条对照

| 约束 | 落实 |
|---|---|
| 唯一入口（不得在 UI 线程/页面激活路径扫日志或枚举进程） | 采集只挂在 ProcessChunk（既有唯一增量扫描链）；RunRecordHost/Collector 零 File/Directory/Process 调用（grep 验证） |
| 不得新增第二个日志扫描链 | BgiLogCenter.Pump 的字节游标唯一；采集器吃同一个 chunk 字符串，自己不开文件 |
| 不得新增周期 DispatcherTimer | 只有 `TickHub.RegisterBackground("RunRecordHost.Cleanup", 3600, …)` |
| 异常绝不影响日志解析 | 挂载点两层 try/catch；存储层写失败降级丢弃 + 一次警告 |
| 不做任何界面展示 | 无 XAML/页面/绑定；配置只落 config.json；API 只返回 JSON |
| 保持 /api/bgi/ledger 兼容 | 一行未改，实测结构不变 |
| 版本号唯一来源 | 只改 SoulLink.csproj 的 `<Version>` → 3.3.41 |

## 8. 已知边界（如实记录）

1. **拾取只在「路线循环」型配置组出现**：罐子激活（纯激活脚本）本身不产生拾取，故该组 pickCount=0；
   联机锄地类组才有 73 次拾取与 8 条带拾取的路线（见 §6.3）。这是日志事实，不是漏采。
2. **历史 JSONL 未搬迁**：按契约 §5.4，JSONL 冷备保留不删，迁移留待后续；`/api/bgi/ledger` 仍读 JSONL，
   新 API 读 SQLite，两条数据源并存、互不破坏。
3. **未在真实生产进程里跑长周期**：本次实测用独立验证工程驱动**同一份产品代码**
   （BgiLogCenter.Tick → 真实文件 → 真实 Feed → 真实 SQLite）；小时级清理拍的注册路径已由
   `RunRecordHost.Start` 实测（StartCount / 清理时刻可见），但 1 小时节拍本身未做长时间观测。
4. **未触碰 A1/A4 生产进程与 dist/stable**：所有实测沙箱在 `%TEMP%`，测试进程已停并清理。
