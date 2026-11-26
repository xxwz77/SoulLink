# 数据中心代码系统审查：多次启动卡死根因（只读审查）

> 日期：2026-11-26
> 审查人：architect（只读审查，未运行任何程序、未改任何生产代码）
> 范围：相对基线 `4612406` 新增的数据中心 6 个文件
> 结论性质：**静态审查结论**，未做运行期复现（硬约束禁止启动程序）

---

## 0. 一句话结论

用户的最新决定性实验证据：

  - **无配置（0 账号）→ 完全流畅**（BgiLogCenter.Tick 空转、RunRecordCollector 不处理任何日志）；
  - **有配置（3 账号 + 4 个 BgiPaths + 250MB 历史回填）→ 卡死**。

这把范围**钉死在 `BgiLogCenter` + `RunRecordCollector` + 回填逻辑**上，与渲染/位图无关（位图死循环是后来的事，且已回退）。

**元凶不是单个死循环，而是一条「首扫超量 + 串行阻塞」的放大链**（详见第 2 节），它解释了为什么「有账号就卡死、无账号就流畅」。

---

## 1. 问题清单（P0/P1/P2 分级）

### P0-1 · 首次全量扫描每 tick 最多 32MB × 串行遍历所有账号（负载爆炸的直接源头）

- **位置**：`Services/BgiLogCenter.cs:40`（`MaxBytesPerTick = 32MB`）、`:864-946`（`Pump()` 读循环）、`:549`（`ServiceTick` 串行 foreach）。
- **触发条件**：有真实账号 + 日志文件存在（用户「有配置」场景）。首次全量扫描（`Primed=false`）时，`budget = min(len - cursor, 32MB)`；`Pump` 用 4MB 缓冲循环读满整个 budget。
- **为什么卡死**：
  1. `Step → Pump → ProcessChunk` 里跑 **5 个解析器**（`e.Status.Update` / `e.Groups.Update` / `DailyStatsCache.Accumulate` / `RouteLedger.Feed` / `RunRecordCollector.Feed`），其中 `RunRecordCollector.Feed → ScanEvents` 内部还有 **约 15 个已编译正则** + 每行 `AnchorOf`（`new List<string>(4)`）+ 每 chunk 两个 `Dictionary<int,long/string>`。
  2. **3 个账号 × 每 tick 各 32MB = 96MB 文本 × 15 正则 × 5 解析器** —— 这就是 captain 说的「爆炸」。一个 4MB 真实 A1 chunk 实测（代码注释）扫描约 1.3s；32MB 就是 ~10s，3 个账号串行就是 ~30s。
  3. `ServiceTick` 是**串行 foreach，无任何超时/让出**；`Tick()` 由 `AccountStateCenter.RefreshOnce` 的 `System.Threading.Timer` 线程同步调用（`AccountStateCenter.cs:609`），`RefreshOnce` 只有重入保护（`_refreshing`），**没有单轮超时**。
- **修法**：把 `MaxBytesPerTick` 从 32MB 降到与回填一致的量级（如 1~4MB），并给 `ServiceTick` 加「单轮总耗时预算」——超预算就 return，剩下的下 tick 再读（`Pump` 已经按字节游标幂等，天然支持续读）。

### P0-2 · 回填 `PumpBackfill` 的同步 `WaitForWritesDrain(10s)` 阻塞串行 Tick 循环

- **位置**：`Services/BgiLogCenter.cs:695` 调 `RunRecordStore.WaitForWritesDrain(TimeSpan.FromSeconds(10))`；实现 `Services/RunRecordStore.cs:1424-1431`（`op.Completion.Task.Wait(timeout)`）。
- **触发条件**：历史回填每处理一个 1MB 块就调一次（≈每 tick 一次）。
- **为什么卡死**：
  1. `Wait(timeout)` 是**同步阻塞当前线程，最多 10 秒**。它跑在 `ServiceTick`（TickHub 后台 1 秒一拍）与 `Step`（AccountStateCenter Timer 线程）上。
  2. `Step` 在 `Tick` 的 `foreach` 里是**串行**的：**A2 的回填阻塞 10 秒 → A3/A1 的实时采集全部被拖住**（captain 的推断成立）。
  3. 写线程此时正在做 `AddRawEvents` 的单事务逐条 `INSERT OR IGNORE`（回填 1MB 可产生数千行 raw_event），写线程一慢，调用线程就整拍挂起。多账号回填 = 逐账号串行 Wait = 累计数十秒。
- **修法**：把 `WaitForWritesDrain` 改成 `WaitForWritesDrainAsync` 并 `await`（TickHub 后台线程本来就是异步友好），或按「队列深度超阈值就让出」做异步背压，而不是每块硬等。

### P0-3 · FileSystemWatcher 未在所有移除路径 Dispose（资源累积）

- **位置**：`Services/BgiLogCenter.cs:517`（`Tick` 清理已删账号直接 `Entries.Remove(id)`）、`:753`（`Step` 未绑定分支清空字段但不 `StopWatcher`）。
- **触发条件**：账号增删 / 绑定路径切换 / BGI 路径变化。
- **为什么是问题**：`EnsureWatcher`（:299-313）创建的 `FileSystemWatcher` 持有内核句柄 + 线程池回调，且 `onChanged` 闭包捕获 `Entry e` → watcher 不 Dispose 就钉住整个 Entry（64KB `Pending` + `Tail` + `PicksByGroup`）。`StopWatcher` 本身实现正确（:329-338），但这两条移除路径**没调用它**。
- **为什么符合「累积」语义**：托盘常驻进程里反复「改配置 → 账号列表变化」会只增不减。进程退出时 OS 会回收，所以它是**单进程内累积**，不是跨进程——但配合 P0-1/P0-2 的卡顿，会让句柄与内存持续爬升。
- **修法**：在 :517 与 :753 两处先 `StopWatcher(e)` 再移除/清字段。

### P1-1 · 首扫触发 LOH 压缩 + 全代 GC（启动黑屏的加重项）

- **位置**：`Services/BgiLogCenter.cs:944-946`（`GCLargeObjectHeapCompactionMode.CompactOnce` + `GC.Collect(2, Optimized, blocking:false, compacting:true)`）。
- **触发条件**：某账号首次全量扫描且 `budget > 8MB`。
- **为什么是问题**：`GC.Collect(2)` 即使非阻塞也会有一次 STW；它发生在 `Pump()` 里，而 `Pump` 在 `Step`/ServiceTick 路径上——首扫大日志时把该线程卡住，叠加「启动黑屏」。
- **修法**：LOH 压缩只在**空闲期**做一次，不要在每次首扫 >8MB 都做；或去掉主动 `GC.Collect(2)`，只设置 `CompactOnce` 让下一次自然 GC 生效。

### P1-2 · 回填 `new byte[1MB]` / tick + 每行 `new List<string>(4)`（回填期 GC 压力）

- **位置**：`Services/BgiLogCenter.cs:658`（回填 `byte[] buf = new byte[want]`）；`Services/RunRecordCollector.cs:482`（`AnchorOf` 每行 `new List<string>(4)`）、`:757-761`（每 chunk `SplitLines` + 两个大字典）。
- **触发条件**：回填持续进行（250MB 历史，约 4 分钟）。
- **为什么是问题**：短生命周期对象分配速率高（A1 日志 4MB ≈ 1.5 万行 → 1.5 万个 List + 两个 ~1.5 万项字典），长时间回填让 GC 频繁触发，间接拖慢 UI/采集。
- **修法**：回填缓冲池化复用；`AnchorOf` 改用复用的 List；`AnchorOfLine/TimestampOf` 的 O(事件×行) 反查改成事件直接携带行号（t44 已把最坏 O(n²) 改掉，这里残留 O(事件×行)）。

### P1-3 · SQLite WAL 无退出 checkpoint + 噪音 VACUUM 未接入生产（唯一能跨进程累积的项）

- **位置**：`Services/RunRecordStore.cs:1525-1529`（`journal_mode=WAL; auto_vacuum=INCREMENTAL`）、`:1343`（checkpoint 只在 `Purge()` 且 deleted>0 时）、`:1082-1099`（`PurgeNoiseRawEventsAsync` 里的 `VACUUM` **全仓库无生产调用点**）。
- **触发条件**：连续回填/实时写入数百 MB；多次启动。
- **为什么与「多次启动」相关**：这是唯一能**跨进程累积**的磁盘状态。多次启动/回填后 run.db 与 WAL 只增不缩（噪音 raw_event 长期留着），下次启动 `EnsureOpen→EnsureSchema→建索引` 与 `EnsureBootedAsync→LoadOpenRuns` 越来越慢。
- **修法**：退出路径加 `PRAGMA wal_checkpoint(TRUNCATE)`；把 `PurgeNoiseRawEventsAsync` 接到低频清理 tick（与每小时清理同拍）。

### P2-1 · 写队列 65536 的内存上界（非 OOM，但注释低估）

- **位置**：`Services/RunRecordStore.cs:1371`。
- **评估**：`AddRawEvents` 的闭包捕获整份 `RawEvent[] copy`（:304-310）。最坏洪峰下 65536 个 DbOp 可达几十~上百 MB（有界，不无限涨），但「每个 DbOp 几百字节、可忽略」低估了批量 raw 写。
- **修法**：批量 raw 写单独按字节估算背压，不按条数。

### P2-2 · 静态去重字典有界但永不清理

- **位置**：`Services/RunRecordStore.cs:744-751`（`DaySpanWarnings`）、`:1248-1249`（`NegativeDurationWarned`）；`RunRecordCollector.cs:246-310`（`OpenRuns`/`Clocks`/`ErrBlocks`/`SpanStates`/`PendingSplits`/`FirstLineTs`/`OrphanPicks`/`RawByteOffsets`/`RawChunkSequences`）。
- **评估**：都以 account（+file/day/id）为键，账号数固定即有界，量级小，不构成卡死，但长期挂机缓慢增长。
- **修法**：给去重字典加超时/上限清理。

### P2-3 · watcher.Error 回调里自 Dispose 反模式

- **位置**：`Services/BgiLogCenter.cs:314-317`（Error 回调 `StopWatcher(e); EnsureWatcher(e);`）。
- **评估**：在 watcher 自己的事件回调线程 Dispose 同一 watcher，是已知反模式；有 try/catch 兜底，风险低。
- **修法**：Error 只置「需重建」标记，下一 tick 由 ServiceTick 重建。

---

## 2. 明确指出元凶：一条「首扫超量 + 串行阻塞」的放大链

captain 推测的机制**基本成立**，但我要修正一个关键点：**第一推动力不是回填的 Wait，而是首次全量扫描的 32MB/tick 超量读取**。真实链条如下：

```
启动，有 3 个真实账号
  ↓
Tick → foreach 串行 Step(acc)     （AccountStateCenter 的 Timer 线程，无单轮超时）
  ↓
Step → Pump：首次全量扫描 budget=min(len, 32MB)   ← P0-1 超量
  → ProcessChunk：5 解析器 × ~15 正则 × 32MB        ← 单账号 ~10s
  ↓
Step 里还调 PumpBackfill：1MB + WaitForWritesDrain(10s)  ← P0-2 同步阻塞
  ↓
一个账号的 10s 阻塞 = 整轮 Tick 卡 10s → 其余账号实时日志积压
  ↓
下一 tick 要读更多（积压 + 首扫未完）→ 连锁恶化
  ↓
队列/内存/CPU 全部拉满 → 界面无响应 → 全屏黑屏
```

**为什么「无配置就流畅」**：`Tick` 里 `accounts` 为空直接 return（`BgiLogCenter.cs:487-488`），`Pump`/`PumpBackfill`/`ProcessChunk` 一个都不会进，所以零负载。这**精确对应**用户实验。

**结论**：P0-1（首扫 32MB/串行）+ P0-2（回填同步 Wait 10s）两者叠加，是「有账号就卡死」的元凶；P0-3（watcher 泄漏）是长期累积放大器；已修的 Bug 1（DataCenterPage 递归）与 Bug 2（位图重绘）在「有账号」场景下会进一步把 UI 线程钉死，让黑屏更彻底。

---

## 3. 最小验证方法（不用启动完整程序就能证明）

由于硬约束禁止启动 SoulLink，给出**可独立运行、只加载纯逻辑（不加载 WPF）**的验证：

1. **用现成的 `tools/runcollect-verify` 工程**（已存在，`T44Bench.cs` 就喂 4MB A1 日志）扩展一个基准：连续 `RunRecordCollector.Feed` 3 个账号 × 32MB 文本，测单线程耗时。期望观察到：单账号 32MB 扫描 ≈ 8~12s（验证 P0-1 的量级）。
2. **写队列背压验证**（纯 console，`%TEMP%`）：构造一个慢 `RunRecordStoreCore`（写线程每 op 加 50ms 延迟），并发 `EnqueueWrite` 灌入 10000 个小写 + 若干 `AddRawEvents` 大批量，测 `WaitForWritesDrain(10s)` 的实际阻塞时长与队列深度。期望观察到：阻塞可累计到数秒~10 秒（验证 P0-2）。
3. **watcher 泄漏验证**（纯 console，不加载 WPF）：循环「新建 Entry → EnsureWatcher → 只 `Entries.Remove` 不 StopWatcher」，用 `Process.HandleCount`（GetProcessHandleCount）观测句柄数随循环递增（验证 P0-3）。

以上三种都不需要启动 SoulLink 主程序，不需要 WPF UI，跑完即删（%TEMP%）。

---

## 4. 附：审查读过的关键路径

- `BgiLogCenter.cs` 全量（1229 行）：Entry/EnsureWatcher/StopWatcher/Tick/ServiceTick/Step/Pump/ProcessChunk/PumpBackfill/Clear。
- `RunRecordStore.cs` 全量（1707 行）：facade/写队列 RunRecordStoreCore/WriteLoop/EnsureOpen/EnsureSchema/Purge/WriteRawEvents/WaitForWritesDrain。
- `RunRecordCollector.cs` 全量（2006 行）：静态字典/ScanEvents/ArchiveRawLine/EnsureBootedAsync/OnFileChanged/StartGroup/CloseRunLocked/ObserveBgiState/ResetForTest。
- `RunRecordHost.cs` 全量（180 行）：Start/PurgeOnceAsync/Shutdown。
- `IngestCursorStore.cs` 全量（246 行）：Open/Advance/Flush/Upsert。
- `DataCenterPage.xaml.cs` 关键段：Rebuild/BuildSignature/timer tick/EnsureSqliteTodayRunsAsync。
- 调用链：`AccountStateCenter.RefreshOnce → BgiLogCenter.Tick → Step → Pump/PumpBackfill`；`App.xaml.cs → RunRecordHost.Start`。

（只读审查：未启动任何程序、未构建运行、未修改任何生产代码。）
