# 数据流审计：BGI/日志/游戏状态数据的「数据中心统一调度」现状

> 审计人：dataflow-auditor
> 日期：2026-11-22
> 目标：回答用户架构级要求——**「数据必须让数据中心统一调度，别的地方按需取用」**，把现状全部摸清并如实报告。
> 范围：只读审计 + 本报告一份文档。未改任何业务代码、未动 dist/、未提交 git。
> 结论全部附 `file:line`，未核实的不写。

---

## 0. 一句话结论（先给答案）

- **日志文件（`better-genshin-impact*.log`）的增量读取已经高度收敛**：真正的字节级读取点只有一个 `BgiLogCenter.Pump`（`Services/BgiLogCenter.cs:563-665`），游标只进不退、单遍流式、按账号字节预算封顶。
- **但仍存在少量「活的」旁路**：`BgiTaskPreparer.WaitForScriptStoppedAsync` 直接 `TailLines` 读日志尾部（自动化停止判定）；`WebServer.ApiSessions` 每次请求重新 `new BgiStatus().Update()` 解析快照文本。
- **大量疑似旁路其实是死代码**（grep 全仓确认无调用方）：`CountDailyPicksByGroup`、`CountDailyErrors`、`TailLinesDeep`、`LogFollower`、`DailyStatsCache.RebuildFromFile`、`AccountStateCenter.ReadLogFull`、`FlowRunner.FindBgi`、`MyDepartWindow.OldListBgiConfigs`。
- **config.json / 配置目录列表**是第二个数据域（`BgiDataCenter` 是权威），但 `BgiTaskPreparer.ReadCancelHotkey` 有兜底直读 config.json、`ActionEditorWindow.ListConfigNames` 直接扫目录。
- **一个真实的一致性隐患**：快照里的 `Status` 和 `Today` 是**活引用**（非拷贝），而它们在锁外被并发修改（评审已指出的线索，本报告坐实到具体行）。

---

## 1. 数据中心职责边界（ASCII 图）

```
                    ┌────────────────────────────────────────────────────┐
                    │  账号配置 / 路径来源（不是日志数据域）                  │
                    │  BgiPathResolver（路径解析，1s 缓存）                  │
                    └───────────────────────┬────────────────────────────┘
                                            │  GetBoundPath / IsPinned
                                            ▼
  BGI 进程发现（一次进程枚举，3s 缓存） ──────────────────────────────────────
  BgiLogWatcher.Discover / DiscoverAll / FindTodayLog
  Services/BgiLogWatcher.cs:301 / 398 / 666
        │ 返回 Instance{ LogFile, Pid, LastWrite }
        │
        ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                    ★ 数据中心（唯一日志读取者）★                            │
│                          BgiLogCenter（static）                           │
│                                                                         │
│  ① 发现日志文件   Step()        Services/BgiLogCenter.cs:466             │
│     · BgiLogWatcher.FindTodayLog(exe)                     :516           │
│     · DiscoverForSession（仅补 PID）                       :526           │
│     · EnsureWatcher → FileSystemWatcher（事件置 Dirty 标记） :241/316     │
│                                                                         │
│  ② 增量读取       Pump()        Services/BgiLogCenter.cs:563             │
│     · 字节游标 Cursor 只进不退，FileStream 流式读          :590-598       │
│     · MaxBytesPerTick = 32MB/拍，budget 封顶               :39/583        │
│     · 残行 Pending 拼接，读过的字节永不再读                 :604-633       │
│                                                                         │
│  ③ 解析           ProcessChunk()  Services/BgiLogCenter.cs:668           │
│     · e.Status.Update(chunk)        → BgiStatusParser       :676         │
│     · e.Groups.Update(chunk)        → ScriptGroupTracker    :677         │
│     · DailyStatsCache.Accumulate(Today, chunk)              :678         │
│     · 逐行：粘性配置组 / 拾取分组 / 报错计数 / 尾窗          :681-786     │
│                                                                         │
│  ④ 缓存           不可变快照 + 版本号  Services/BgiLogCenter.cs:117-121   │
│     · Entry.Version 变化 +1，快照按版本缓存 Cached()        :832-838     │
│                                                                         │
│  ⑤ 分发（对外读接口，全走不可变快照，无 IO）                                │
│     · Snapshot(accountId)               :190                            │
│     · SnapshotByUser(userName)          :196                            │
│     · RecentText(accountId)             :208                            │
│     · ResidentChars()                   :218                            │
│     · SharedInstances()（Discover 2s 缓存）:821                           │
│     · event Changed（后台线程触发）      :151                            │
└─────────────────────────────────────────────────────────────────────────┘
        │ 快照被 AccountStateCenter 每 1s 抄进 AccountState（结构拷贝）
        ▼
   消费侧（只读快照，不碰文件）
   · AccountStateCenter.RefreshAccount        Services/AccountStateCenter.cs:751-789
   · WebServer.ApiSessions                    Services/WebServer.cs:768-785
   · LaunchPage（MergeStructuralFrom 结构拷贝）Views/LaunchPage.xaml.cs:785-795
```

### 1.1 第二数据域：BGI 配置/目录（不是日志，是另一个中心）

```
   BgiDataCenter（static）  Services/BgiDataCenter.cs
   · Get / Refresh / ListConfigs / SetPath / Invalidate        :46-72
   · ReadFiles：读 User\config.json + 扫 User\{OneDragon|ScriptGroup}\*.json
                                                                :135-158 / 229-234
   · Stamp()：1 秒内缓存命中不扫目录，超时才算 Directory.GetFiles 比对 :110-131
   · AccountDataChanged 事件                                   :44
```

### 1.2 明确回答：有没有第二处也在读同一个日志文件？

**【现状事实】有，但只有一处是「活的」旁路**：

| 位置 | 状态 |
|---|---|
| `Services/BgiTaskPreparer.cs:336` `BgiLogWatcher.TailLines(inst.LogFile, 250)` | **活的旁路**：自动化「停止脚本」判定时，每 250ms 直接读日志尾部约 100KB，10 秒超时窗口内最多读 ~40 次。绕过了 `BgiLogCenter`。 |
| `Services/BgiLogWatcher.cs:786` `LogFollower`（独立 FileSystemWatcher 跟随器） | **死代码**：LaunchPage 已把 `new LogFollower(...)` 全部改成「读数据中心快照 + 订阅 Changed」，全仓无实例化点（仅注释残留 `Views/LaunchPage.xaml.cs:1077/1342`）。 |
| `Services/BgiLogWatcher.cs:452` `TailLines` / `:506` `TailLinesDeep` / `:612` `CountDailyPicksByGroup` / `:738` `CountDailyErrors` | 除 `TailLines` 被 BgiTaskPreparer 调用外，其余**无调用方**（死代码）。 |
| `Services/DailyStatsCache.cs:206` `RebuildFromFile`、`Services/AccountStateCenter.cs:638` `ReadLogFull`、`Services/FlowRunner.cs:752` `FindBgi` | **死代码**（全仓无调用）。 |

---

## 2. 旁路读取清单表

> 只列「读 BGI 数据 / 日志 / 配置」的旁路；读录像 meta、RDP、broker、更新包等其它数据域不在此列（第 5 节单独说明）。

| # | 位置 (file:line) | 读的是什么 | 为什么不走数据中心 | 开销 | 风险 | 建议 |
|---|---|---|---|---|---|---|
| 1 | `Services/BgiTaskPreparer.cs:336` `BgiLogWatcher.TailLines(inst.LogFile, 250).text` | BGI 日志文件尾部 ~100KB | 自动化停止判定要「按键名证据」，需要比 1 秒快照更细的即时采样；历史遗留未并入 | 每 250ms 一次 FileStream 尾部读；10s 内最多 ~40 次/次停止判定 | **中** | **并入数据中心**：加一个 `BgiLogCenter.RecentTextFromCursor(sessionId)` 或直接读 `Snapshot().Tail`（尾窗已含停止锚点）；见第 4 节 P1 |
| 2 | `Services/WebServer.cs:774-775` `new BgiStatus(); status.Update(sb.ToString())` | 数据中心快照的尾窗文本（内存，非文件） | 网页需要与桌面版一致的 BgiStatus 展示字段，直接复用解析器 | 每次 `/api/sessions` 对每个绑定账号重新正则解析 ≤800 行尾窗；纯内存无 IO | **低** | **保留但优化**：改为读 `snap.Status` 直接取字段（数据中心已解析好），或缓存上次解析结果；见 P2 |
| 3 | `Services/BgiTaskPreparer.cs:603-657` `ReadCancelHotkey`（FileStream+StreamReader 读 `User\config.json`） | BGI `User\config.json` 的 cancelTaskHotkey | 兜底路径：会话映射不到账号时才走这里；主路径已走 `BgiDataCenter.Refresh` | 仅在「账号映射失败」这种罕见情况发生；一次读一个小 json | **低** | **保留（兜底合理）**：但应加注释说明它是 BgiDataCenter 的降级路径，避免被当成第二真相来源 |
| 4 | `Views/ActionEditorWindow.xaml.cs:787-809` `ListConfigNames`（`Directory.GetFiles(target, "*.json")`） | BGI `User\{OneDragon|ScriptGroup}\*.json` 文件名列表 | 编辑器 ExeBox 文本框变化时现场扫候选列表；未接 BgiDataCenter | 目录通常几十个 json；UI 线程触发，但仅在文本框变化时 | **低** | **并入 BgiDataCenter.ListConfigs**：`FlowEditorWindow.xaml.cs:2299`、`MyDepartWindow.xaml.cs:114` 已经走 `BgiDataCenter.ListConfigs`，唯独 ActionEditor 还自己扫 |
| 5 | `Views/MyDepartWindow.xaml.cs:117-140` `OldListBgiConfigs` | 同上目录 | — | 无 | — | **删除（死代码）**：全仓无调用方 |
| 6 | `Services/WebServer.cs:544-597` `ApiList`（`Directory.GetDirectories` + `File.ReadAllText(meta.json)` + `Directory.GetFiles("*.jpg")`） | 录像回放目录 `capture\录制回放\` 下的 meta.json / 目录 | 录像列表不属于 BgiLogCenter/BgiDataCenter 职责；`/api/list` 只在网页展开录像面板时才请求 | 递归两层扫目录 + 读每个 meta.json；`WebPages.cs:974` 确认仅展开时触发 | **中**（评审已指出范围过大） | **保留（合理：按需 + 懒加载）**，但建议把「每目录读 meta.json」改成后台缓存 + 增量失效；见 P2 |
| 7 | `Services/BgiLogWatcher.cs:612` `CountDailyPicksByGroup` / `:738` `CountDailyErrors` / `:506` `TailLinesDeep` | 整天日志全文/深尾扫描 | 2.17.45 重写后已被 BgiLogCenter 的单遍增量替代 | 无调用方 | — | **删除（死代码）**，见 P1 |
| 8 | `Services/DailyStatsCache.cs:206` `RebuildFromFile` / `:101` `Calculate` / `:122` `Update(userName, logText)` | 日志文件整份 / 整份文本 | 已被 `Accumulate`（增量）替代 | 无调用方（`Calculate`/`Update`/`Get` 均无外部调用） | — | **删除（死代码）**；`Accumulate` 是唯一被 BgiLogCenter 使用的入口（`:678`） |
| 9 | `Services/AccountStateCenter.cs:366` `FallbackReadHead` / `:638` `ReadLogFull` | 日志文件尾部 4MB | 兜底读，主流程已走快照 | 无调用方 | — | **删除（死代码）** |
| 10 | `Services/FlowRunner.cs:752-758` `FindBgi`（`BgiLogWatcher.Discover()` 后备查找） | BGI 进程日志实例 | 后备查找（已知线索） | 无调用方 | — | **删除（死代码）**；若未来需要，应走 `BgiLogCenter.SharedInstances()` |

---

## 3. 「按需取用」现状评估

### 3.1 数据中心的对外接口（取用入口）

**【现状事实】BgiLogCenter 对外只读接口（全部无锁读不可变快照，不引发新 IO）**：

| 接口 | file:line | 是否只读快照 | 是否引发新 IO/解析 |
|---|---|---|---|
| `Snapshot(accountId)` | `BgiLogCenter.cs:190` | ✅ 版本缓存快照 | ❌ 无 IO；仅首次构建时 `Build` 做 `ToArray`/字典拷贝（`:840-871`） |
| `SnapshotByUser(userName)` | `:196` | ✅ | ❌ |
| `RecentText(accountId)` | `:208` | ✅ 从 Tail 拼字符串 | ❌ 无 IO；拼 ≤800 行文本（有分配） |
| `ResidentChars()` | `:218` | ✅ | ❌ |
| `SharedInstances()` | `:821` | ✅ Discover 2s 缓存 | ⚠️ 缓存过期会触发一次进程枚举（23.7ms 级），但有 TTL |
| `event Changed` | `:151` | ✅ 通知 | ❌ |

**【现状事实】BgiDataCenter 对外接口**：

| 接口 | file:line | 语义 |
|---|---|---|
| `Get(user)` | `:46` | 1s 内缓存命中直接返回（Clone）；超时算 `Stamp()`（Directory.GetFiles 扫 3 个目录）比对，变了才 `ReadFiles` |
| `Refresh(user)` | `:47` | 强制重读 |
| `ListConfigs(user, kind)` | `:48` | 走 Get，纯内存 |
| `SetPath / Invalidate` | `:58/66` | 写入口，会失效缓存 + 通知 |

**结论**：`Get` 是合格的「按需取用」——1 秒内命中缓存零 IO，超时才做轻量目录比对。但注意：**`BgiDataCenter.Get` 本身不读日志文件，它读的是 config.json 和目录列表**，这是第二个数据域，与日志数据域分离。

### 3.2 有没有「取用一次就全量重算」的接口？

**【现状事实】**：

1. `WebServer.ApiSessions`（`:774-775`）——每次请求对每个绑定账号 `new BgiStatus()` 后把整个尾窗 `sb.ToString()` 喂进 `Update()` 重新正则解析。这是「取用即重算」，但算的是**内存里的尾窗文本**（≤800 行、≤200KB），不是文件，开销可控，属于可优化项（见 P2）。
2. `DailyStatsCache.RebuildFromFile`（`:206`）——从文件整份重建统计，这是「全量重算」接口，但**已无调用方**（死代码）。
3. `DailyStatsCache.Calculate(logText, ...)`（`:101`）——「传整份日志文本进来」的接口，**已无外部调用方**；唯一活着的入口是 `Accumulate(st, chunk)`（增量），由 `BgiLogCenter:678` 调用。✅ 符合「按需取用、增量推进」要求。

---

## 4. 一致性问题

### 4.1 【现状事实·高风险】快照中的 `Status` / `Today` 是活引用，且在锁外被并发修改

- `Services/BgiLogCenter.cs:832-838` `Cached(e)` → `Build(e)` 在 `lock (Sync)` 内被调用（由 `Snapshot`/`SnapshotByUser` 的 `lock (Sync)` 包裹）。
- 但 `Build` 返回的快照里（`:840-871`）：
  - `Status = e.Status`（`:861`）——**直接引用，未拷贝**
  - `Today = e.Today`（`:863`）——**直接引用，未拷贝**
  - `Groups = e.Groups.All()`（`:862`）——新 `List`，但 `ScriptGroupRun` 是引用对象（浅拷贝）
  - `PicksByGroup`（`:842-844`）、`Tail`（`:868` `ToArray`）——深拷贝/新数组，安全
- 而写入侧 `ProcessChunk`（`:668`）在**无锁状态**下修改这些引用：
  - `:671` `e.Version++`
  - `:676` `e.Status.Update(chunk)`（`BgiStatusParser.cs:329` 无锁写字段）
  - `:677` `e.Groups.Update(chunk)`（`ScriptGroupTracker.cs:109` 内部有 `_sync` 锁，但 `e.Groups` 对象本身被外部读 `All()`）
  - `:678` `DailyStatsCache.Accumulate(e.Today, chunk)`（`DailyStatsCache.cs:140` 内部 `Apply` 写 `st.Picks` 等，`Accumulate` 走的是 `BgiLogCenter` 传入的 `e.Today`，**不经过 `DailyStatsCache._sync` 锁**）
  - `:704-708` `e.ErrorCount/WarnCount/FailCount++`；`:719-724` `e.CurrentGroup/LastGroup/StoppedAt/OneDragon`；`:785` `e.Tail.Enqueue`

**后果**：一个线程正在 `Build` 读 `e.Status`/`e.Today` 时，另一个线程的 `ProcessChunk` 正在无锁写同一对象，可能读到中间态（例如 `RoutePicks` 字典正被 `RoutePicks.Clear()` + 逐项 Add，见 `BgiStatusParser.cs:526-531`）。这与评审已指出的 `BgiLogCenter.cs:636/671/676-678`、`832-871` 线索完全一致，本报告坐实到具体行。

**严重度**：中（.NET 下 Dictionary 并发读写可能抛「集合已修改」异常，虽 `Update` 外层 `try{}catch{}` 吞掉了 `BgiLogCenter.cs:676` 的异常，但吞异常意味着状态可能静默丢失一次更新；界面偶尔「闪一下」或「少一条拾取」可能是这个原因）。

### 4.2 【现状事实】`AccountStateCenter` 快照与 `BgiLogCenter` 快照之间的「二次拷贝」时序

- `AccountStateCenter.RefreshAccount`（`:751-789`）把 `BgiLogCenter.Snapshot` 的结构字段抄进 `AccountState`（`st.CurrentGroup/LastGroup/...`）。
- `st.Today = snap.Today`（`:777`）、`st.Groups = snap.Groups`（`:776`）——**再次直接引用同一个活对象**，把 4.1 的活引用问题又传播了一层到 `AccountState` 及其消费者。

**后果**：所有通过 `AccountStateCenter.Get()` 拿到 `AccountState.Today/Groups` 的模块，拿到的仍是数据中心内部被锁外修改的同一对象。

### 4.3 【现状事实·低风险】`DailyStatsCache.Accumulate` 的锁问题

- `DailyStatsCache.Accumulate`（`:140`）→ `Apply`（`:141`）**没有 `lock (_sync)`**（对比 `Update` 方法 `:128` 有锁）。
- 它直接写传入的 `DailyStats st`（即 `e.Today`）。由于 `e.Today` 本身在 `BgiLogCenter` 里只在 `ProcessChunk`（单线程 ServiceTick/Tick 时序）写，并发面较小，但一旦 4.1 的多线程读叠加，就同属活引用问题。

---

## 5. 性能影响

| 旁路点 | 单次开销 | 触发频率 | UI 线程 IO？ |
|---|---|---|---|
| `BgiTaskPreparer.TailLines(250)` | FileStream 尾部读 ~100KB + UTF8 解码 + Split | 停止判定时每 250ms，10s 窗口内最多 ~40 次 | ❌ 后台 async（`BgiStartGuard` 流程） |
| `WebServer.ApiList` | 递归两层扫目录 + 每目录读 meta.json + 可能数 .jpg | 仅网页展开录像面板时（`WebPages.cs:974` 确认懒加载） | ❌ HTTP 请求线程 |
| `WebServer.ApiSessions` | 每账号正则解析 ≤800 行尾窗 | 每次 `/api/sessions` 请求 | ❌ HTTP 请求线程 |
| `ActionEditorWindow.ListConfigNames` | `Directory.GetFiles` 扫几十个 json | UI 文本框变化时 | ⚠️ **是**（`TextChanged` 事件处理器内同步扫目录）。目录小，实测影响可忽略，但严格说违反了「UI 线程禁文件 IO」 |
| `BgiLogCenter.Snapshot` 首次 Build | `Tail.ToArray` + 字典拷贝 | 版本变化后首次取用 | 消费方调用线程（多为后台） |

**死代码不产生运行时开销**（第 2 节 #5/#7/#8/#9/#10 均无调用方），但保留它们会：
1. 误导后续开发者以为「还有别的读取路径可用」；
2. 增大 grep 排查面，削弱「唯一读取者」这一架构保证的可信度。

---

## 6. 改造建议（分优先级，明确「必须并入」vs「可保留」）

> 原则：**不为架构洁癖大重构**。所有建议都以「准确 + 可维护」为准，能保留的明确说保留并给理由。

### P0（必须，直接关系「准确抓数据 + 正确执行」）

1. **消除 4.1 的活引用并发写**（`BgiLogCenter.Build` / `ProcessChunk`）。
   - `Build` 里 `Status = e.Status`、`Today = e.Today` 改为**拷贝**（至少拷贝 `BgiStatus` 的结构字段和 `DailyStats` 的 `Picks` 字典，参考 `PicksByGroup` 已经做的深拷贝 `:842-844`）。
   - 或让 `ProcessChunk` 的字段写入统一进 `lock (Sync)`（但注意 `Update` 内部正则开销大，锁内做会阻塞读侧，更推荐「快照拷贝」方案）。
   - **这不是架构洁癖**：当前吞异常（`:676 try{}catch{}`）可能让一次日志更新静默丢失，直接违反用户「准确抓到数据」的担心。

2. **`BgiTaskPreparer.WaitForScriptStoppedAsync` 改读数据中心快照**（`Services/BgiTaskPreparer.cs:334-336`）。
   - 现状它每 250ms 直接 `TailLines` 读文件，绕过了 `BgiLogCenter`，是**唯一活着的日志文件旁路**。
   - 改为读 `BgiLogCenter.Snapshot(accountId).Tail`（尾窗已含停止锚点，`BgiLogCenter.RxStopped` 与 `BgiStatusParser` 的停止判定口径一致），或新增一个「按 sessionId → 快照」的入口。
   - 好处：停止判定与界面/状态中心同源，消除「数据中心判停了、自动化还读文件重判」的口径漂移风险。

### P1（应该，低风险、纯减负）

3. **删除死代码**（全仓无调用方，已逐项 grep 确认）：
   - `BgiLogWatcher.CountDailyPicksByGroup`（`:612`）、`CountDailyErrors`（`:738`）、`TailLinesDeep`（`:506`）
   - `BgiLogWatcher.LogFollower`（`:786`，含 `TailLines` 里仅被它用到的路径 —— 需先确认 `TailLines` 仅剩 `BgiTaskPreparer` 一个调用方，见 P0-2 改造后再清理）
   - `DailyStatsCache.RebuildFromFile`（`:206`）、`Calculate`（`:101`）、`Update(userName,...)`（`:122`）、`Get`（`:107`）
   - `AccountStateCenter.FallbackReadHead`（`:366`）+ `ReadLogFull`（`:638`）
   - `FlowRunner.FindBgi`（`:752`）
   - `MyDepartWindow.OldListBgiConfigs`（`:117`）
   - 理由：**删掉死读取入口，才能让「唯一读取者」这一架构保证可信、可 grep、可维护**。这些函数名字会持续误导后来者「这里有第二条读文件的合法通道」。

### P2（可保留，理由明确；选择性优化）

4. **`WebServer.ApiList`（`WebServer.cs:544-597`）——保留，理由是「录像数据域」与「BGI 日志数据域」是两回事**，它读的是 `capture\录制回放\` 的录像 meta，不属于 BgiLogCenter/BgiDataCenter 职责，且前端已做懒加载（`WebPages.cs:974` 只在展开录像面板时请求）。可选优化：给「每目录读 meta.json」加后台缓存 + `AccountDataChanged` 式失效，避免频繁展开时反复扫盘。
5. **`WebServer.ApiSessions`（`:774`）——保留，但优化**：现在每次请求重新 `new BgiStatus().Update(尾窗文本)`。既然 `snap.Status` 已被数据中心解析好，可直接取 `snap.Status.TaskName/ConfigGroup/...`，省掉每请求重复正则。理由：保留「网页也能看到运行详情」这一功能，但不必每次全量重解析。
6. **`BgiTaskPreparer.ReadCancelHotkey` 的 config.json 兜底（`:630-643`）——保留**：它是「会话→账号映射失败」时的降级路径，主路径已经走 `BgiDataCenter.Refresh`（`:585`）。理由：兜底直读一个几十 KB 的 config.json，换来「极端情况下自动化仍能拿到停止快捷键」，可靠性和成本都划算。建议仅加一行注释标明「这是 BgiDataCenter 的降级，不是第二真相来源」。
7. **`ActionEditorWindow.ListConfigNames`（`:787`）——并入 `BgiDataCenter.ListConfigs`**：`FlowEditorWindow`、`MyDepartWindow` 都已走数据中心，唯独这里还自己扫目录。虽然开销小，但统一后「配置目录列表」只有一个真相来源，且顺带消除 UI 线程文件 IO。

### 明确「不建议」的事

- **不建议**把录像/RDP 帧/broker/更新包等其它数据域的读取也塞进 BgiLogCenter/BgiDataCenter。那些是独立数据域，硬塞反而让数据中心职责模糊。
- **不建议**为了「唯一读取」把 `BgiLogWatcher.Discover()`（进程枚举 + 找日志文件）也禁掉——它是「发现日志文件」这一环，是数据中心的输入而非旁路读取；`BgiLogCenter.SharedInstances()` 已经用 2s TTL 统一了它的调用入口，界面应继续走这个共享缓存而非各自 Discover。

---

## 7. 现状事实 vs 建议（汇总标注）

### 【现状事实】

1. 日志文件字节级增量读取点唯一：`BgiLogCenter.Pump`（`Services/BgiLogCenter.cs:563-665`），游标只进不退、单遍流式、32MB/拍封顶。
2. 唯一活着的日志文件旁路：`BgiTaskPreparer.cs:336` 的 `TailLines` 直读。
3. 快照 `Status`/`Today` 是活引用（`BgiLogCenter.cs:861/863`），在锁外被 `ProcessChunk`（`:676/678` 等）并发修改 → 一致性隐患。
4. `WebServer.ApiSessions` 每次请求重新解析尾窗文本（`:774-775`）。
5. `WebServer.ApiList` 扫录像目录 + 读 meta.json（`:544-597`），前端懒加载（`WebPages.cs:974`）。
6. `BgiDataCenter.Get` 1s 缓存命中零 IO，超时才扫目录比对（`:87-102`）——合格的「按需取用」。
7. `DailyStatsCache.Accumulate` 是唯一活着的日志统计入口（`BgiLogCenter:678` 调用，增量）；`Calculate`/`Update`/`RebuildFromFile`/`Get` 均无调用方（死代码）。
8. 以下均为死代码（无调用方）：`CountDailyPicksByGroup`、`CountDailyErrors`、`TailLinesDeep`、`LogFollower`、`ReadLogFull`/`FallbackReadHead`、`FlowRunner.FindBgi`、`OldListBgiConfigs`。
9. `ActionEditorWindow.ListConfigNames` 在 UI 线程 `Directory.GetFiles` 扫 BGI 配置目录（`:787-809`）。
10. `BgiTaskPreparer.ReadCancelHotkey` 有 config.json 直读兜底（`:630-643`），主路径走 `BgiDataCenter.Refresh`（`:585`）。

### 【建议】

- P0：修活引用并发写（拷贝快照）；`WaitForScriptStoppedAsync` 改读数据中心快照。
- P1：删除第 7/8 条全部死代码。
- P2：`ApiList` 保留（可选缓存）；`ApiSessions` 保留（改直接取 `snap.Status`）；`ReadCancelHotkey` 兜底保留（加注释）；`ActionEditorWindow.ListConfigNames` 并入 `BgiDataCenter.ListConfigs`。
- 不建议：跨数据域硬并入数据中心；不建议禁掉 `Discover()`（它是数据中心输入）。

---

## 附：本报告引用的关键 file:line 索引

- `Services/BgiLogCenter.cs`：`190/196/208`（读接口）、`241/316`（watcher）、`466/516`（发现）、`563-665`（Pump 增量读）、`668-786`（ProcessChunk 解析）、`832-871`（Cached/Build，活引用点：`861/863`）、`676/678`（锁外写）
- `Services/BgiLogWatcher.cs`：`301/398/666`（Discover/FindTodayLog）、`452`（TailLines）、`506`（TailLinesDeep）、`612`（CountDailyPicksByGroup）、`738`（CountDailyErrors）、`786`（LogFollower）
- `Services/BgiStatusParser.cs`：`329`（Update 无锁写）、`526-531`（RoutePicks.Clear+Add）
- `Services/BgiDataCenter.cs`：`46-48`（Get/Refresh/ListConfigs）、`74-108`（Read 缓存逻辑）、`135-158`（ReadFiles）、`229-234`（ReadConfigNames）
- `Services/DailyStatsCache.cs`：`101`（Calculate 死）、`122`（Update 死）、`140`（Accumulate 活）、`206`（RebuildFromFile 死）
- `Services/AccountStateCenter.cs`：`366/638`（FallbackReadHead/ReadLogFull 死）、`751-789`（快照二次拷贝，`776/777` 活引用）
- `Services/BgiTaskPreparer.cs`：`334-336`（TailLines 旁路）、`562-601`（ReadCancelHotkeyFor 主路径）、`603-657`（ReadCancelHotkey 兜底）
- `Services/FlowRunner.cs`：`752-758`（FindBgi 死）
- `Services/WebServer.cs`：`544-597`（ApiList 扫录像）、`726-825`（ApiSessions）、`774-775`（重解析）
- `Views/LaunchPage.xaml.cs`：`785-795`（MergeStructuralFrom 拷贝）、`1077/1342`（旧 LogFollower 注释）
- `Views/ActionEditorWindow.xaml.cs`：`787-809`（ListConfigNames 扫目录）
- `Views/MyDepartWindow.xaml.cs`：`117-140`（OldListBgiConfigs 死）
