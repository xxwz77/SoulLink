# 数据中心旁路审计：日志、进程与目录访问

> 日期：2026-11-23
> 范围：主程序 `App.xaml.cs`、`Services/`、`Views/`、`Controls/` 的全部 `.cs`；独立工具/测试源码单列，不算生产调用。
> 方法：全仓检索日志读取、`Process.GetProcesses*`、目录枚举/`AllDirectories`、`FileSystemWatcher`，再读调用方判断活性和线程路径。
> 性质：纯静态只读审计。未改业务代码、未构建、未读取/操作运行进程。
> 基线：`DATA-CENTER-SINGLE-SOURCE-20261123.md` 与 `DATAFLOW-AUDIT-20261122.md`。本次按当前代码重新核实，不照搬旧结论。

## 0. 结论

1. **BGI 日志字节读取主路径仍集中在 `BgiLogCenter`**；但 `BgiTaskPreparer.WaitForScriptStoppedAsync` 仍是活跃的文件尾读旁路，同时每 250ms 调用一次 BGI 进程/日志目录发现。
2. **`BgiPathResolver.ResolveCore` 的 `DiscoverAll()` 是独立、未缓存的第二遍 BGI 枚举**；设置页/更新页的路径解析会放大此开销。
3. **设置页激活存在多组同步递归目录统计**：BGI 备份、路线备份、路线缓存、旧数据迁移。这些不是数据中心管辖的旁路，但确实违反“页面激活不递归扫目录”，属于高优先级 UI 风险。
4. **`ActionEditorWindow` 有 UI 线程配置目录枚举及进程发现**；`ProcessPage.RefreshSlimSummary` 也有 UI 线程全进程枚举。
5. **不应把独立功能塞进日志数据中心**。路线更新、BGI 备份、数据迁移、会话瘦身有自己的数据域；应保留功能，只把重 IO 移到后台、按需执行并缓存结果。
6. 历史报告将 `DailyStatsCache.Get` 列为死代码；**当前已被 `/api/bgi/stats` 使用**，不能再按死代码删除。

## 1. 管辖判定与风险标准

数据中心管辖：BGI 日志状态、账号/BGI 状态、BGI 配置与组名、今日经验/摩拉/拾取、配置组状态与台账。合法实现者：`BgiLogCenter` / `AccountStateCenter` / `BgiDataCenter` / `DailyStatsCache` / `RouteLedger` / `ScriptGroupTracker`。

- **高**：UI/页面激活或 1 秒级循环中重复全量/递归 IO；或日志解析口径可能漂移。
- **中**：后台按需重复 IO、未缓存的第二遍进程发现、API 重复读取同一批台账。
- **低**：小目录/单文件按需读取，或虽有实现但当前无生产调用方。
- **合规**：数据中心内部实现；不是绕过数据中心，也不代表零成本。

## 2. BGI 日志文件读取：所有生产调用点

| 调用点 | 活性 / 数据归属 | 线程与重复成本 | 分级与建议 |
|---|---|---|---|
| `BgiLogCenter.Pump` → `FileStream`（`Services/BgiLogCenter.cs:590-633`） | 活跃，日志唯一读取者 | watcher 唤醒 + 游标增量，预算封顶 | **合规**。保持唯一字节读取入口。 |
| `BgiLogCenter.Step` → `FindTodayLog`（`:529`；`BgiLogWatcher.cs:666-720`） | 活跃，日志发现 | 枚举文件并看时间，不读内容 | **合规**。发现输入仍由中心调用。 |
| `BgiTaskPreparer.WaitForScriptStoppedAsync` → `TailLines`（`BgiTaskPreparer.cs:323-342`；`BgiLogWatcher.cs:452-473`） | **旁路**：停止判定属于日志状态 | `BgiStartGuard` 调用（`:72`）；后台等待，但每 250ms 重复发现 + 读约 108KB 尾部，10s 最多 40 次 | **高**。改读 `BgiLogCenter` 的游标增量/结构化事件或带版本的尾窗；保留“新日志而非旧日志”基准，避免中心快照边界漏判。 |
| `BgiLogWatcher.TailLinesDeep`（`:506`）、`CountDailyPicksByGroup`（`:612`）、`CountDailyErrors`（`:738`） | 数据中心旧派生功能；无生产调用方 | `LogFollower` 自身仍调用 TailLinesDeep；但 LogFollower 无生产实例 | **低/清理项**。随 LogFollower 一并删除或明确封存。 |
| `BgiLogWatcher.LogFollower`（`:786-884`） | 独立日志读取 + watcher；无生产实例 | 若误启用会形成第二条读取链 | **低/清理项**。当前 `LaunchPage` 只有注释，无 `new LogFollower`。 |
| `DailyStatsCache.RebuildFromFile`（`Services/DailyStatsCache.cs:206-239`） | 无生产调用方 | 潜在整文件重建 | **低/删除或限为测试/恢复工具**，不得变成 API 常规路径。 |
| `AccountStateCenter.FallbackReadHead/ReadLogFull`（`Services/AccountStateCenter.cs:379-407,651`） | 无生产调用方 | 潜在日志读取 | **低/删除死入口**。 |
| `FlowRunner.FindBgi`（`Services/FlowRunner.cs:764-772`） | 无生产调用方 | 潜在发现调用 | **低/删除**。 |

`BgiLogWatcher.DiscoverCore/DiscoverAll` 只枚举日志目录与元数据，不读取日志内容；它们是发现实现，不应与内容读取混为一谈。

## 3. 进程枚举：生产调用点与判定

| 调用点 | 归属 / 调用环境 | 分级与建议 |
|---|---|---|
| `BgiLogWatcher.DiscoverCore`（`Services/BgiLogWatcher.cs:301-389`）与 `DiscoverAll`（`:398-440`） | BGI 发现，数据中心的输入。Discover 有 3s TTL；DiscoverAll 无缓存。 | **合规实现/中风险**。共享同一份发现快照；不要另开枚举。 |
| `BgiPathResolver.ResolveCore`（`Services/BgiPathResolver.cs:135,191-200,374`） | 账号状态/BGI 路径；每个账号可 `Discover + DiscoverAll`，后者重复扫进程和日志目录。设置页点击虽已 `Task.Run`（`SettingsPage.xaml.cs:448`），但 `BgiUpdatePage` 的 `ResolveMany` 在列表构建中调用（`:74`）；`AccountStateCenter.RefreshAccount` 每轮也走 Resolve（`:702`）。 | **高**。给 DiscoverAll 与 Discover 共享缓存，或中心一次产出所有实例；列表页改后台加载，严禁每账号重复枚举。 |
| `BgiTaskPreparer.WaitForScriptStoppedAsync`（`:334`） | 日志停止等待里重复进程发现 | **高**。随日志旁路一并消除。 |
| `BgiTaskPreparer.FindProcessesInSession/FindBgiPid/HasRunningProcess/CloseProcessesAsync`（`:101,424,456,494`） | 自动化启停/清场，按会话+exe 精确筛选；经 `BgiStartGuard`，后台按需 | **中/保留功能**。共享进程发现结果；不得为状态展示直接移到 UI。 |
| `ActionEditorWindow.FindRunningBgiPath`（`Views/ActionEditorWindow.xaml.cs:833-850,894-917`） | BGI 路径发现；按钮 UI 线程，先同步等 `SessionService.QueryAsync`，再枚举 BetterGI | **高**。改为 `async` 按钮 + 中心快照；不自行枚举进程。 |
| `AccountStateCenter.RefreshAll` → `BgiLogWatcher.Discover`（`Services/AccountStateCenter.cs:591`） | 账号状态中心内部，后台周期 | **合规**。保留 TTL；API 不应触发 RefreshAll。 |
| `FlowRunner`（`:537,776`） | 自动化进程控制；按需后台 | **中/独立控制**，不是 BGI 状态读取旁路；复用共享快照，控制时仍精确匹配。 |
| `ProcessOwnerSnapshot.Refresh`（`Services/ProcessOwnerSnapshot.cs:82-112`） | 通用自动化条件；`AccountStateCenter` 在 `:603` 仅 IsNeeded 时刷新 | **合规/中**。保持按需；不能与 BGI 专用枚举叠加。 |
| `ProcessService.List`（`Services/ProcessService.cs:16-44`） | 会话进程列表，独立数据域。`ProcessPage` 列表用 `Task.Run`（`:130`）；`ActionService` 在流程动作里使用（`:100,118`） | **中/保留后台调用**。全量枚举、每进程路径/标题访问不应进 UI。 |
| `SessionSlimmer.Count/Slim`（`Services/SessionSlimmer.cs:275-307,316-358`） | 会话瘦身独立数据域。Slim 后台（`ProcessPage.xaml.cs:392`）；但 `RefreshSlimSummary` 同步 Count（`:350-355`），由 Activate 和列表刷新触发（`:27-33,66,130-133`） | **高 UI/合法功能**。Count 改后台并复用当次 `ProcessService.List` 或共享进程快照，避免同一轮双枚举。 |
| `BgiUpdater` 进程检查（`Services/BgiUpdater.cs:495`） | 更新/关闭自身 BGI，独立功能 | **中/保留后台按需**。 |
| `ManualAgentMode.CleanAutoAgents`（`Services/ManualAgentMode.cs:213-234`） | 代理生命周期；`App.xaml.cs:368` 启动路径，全进程枚举 | **中/合法生命周期**。后台并精准按 proxy 名称；不要扩大范围。 |
| `RdpWrapService`（`:773`） | RDP 服务维护，非数据中心 | **低/独立功能**，保持后台按需。 |

## 4. 目录递归/枚举：生产调用点完整归类

### 4.1 数据中心与 UI 旁路

| 调用点 | 归属与路径 | 分级与建议 |
|---|---|---|
| `BgiDataCenter.Stamp/ReadConfigNames`（`Services/BgiDataCenter.cs:110-158,233`） | BGI 配置中心自身；Get 1s 缓存，Refresh 强制重读 | **合规/中**。保持调用方按需；避免 RefreshAll、避免 UI 强制 Refresh。 |
| `ActionEditorWindow.ListConfigNames`（`Views/ActionEditorWindow.xaml.cs:787-809`） | BGI 配置组目录旁路；由 `ExeBox_TextChanged`（`:813-821`）及下拉选项（`:760,973`）触发 | **高**。改用 `BgiDataCenter.ListConfigs`；列表缓存，输入变化不直接 IO。 |
| `MyDepartWindow.OldListBgiConfigs`（`Views/MyDepartWindow.xaml.cs:117-140`） | 无生产调用方；正式路径已用 ListConfigs | **低/删除**。 |
| `RouteLedger.LoadExistingKeys/Query`（`Services/RouteLedger.cs:371-391,423-477`） | 台账中心自身；后台写线程去重 + 鉴权 GET 查询 | **合规/中**。见 §5 API 重复读取。 |
| `WebServer.ApiList`（`Services/WebServer.cs:582-618`） | 录像回放独立数据域；HTTP 线程、按需 | **中/保留**。可缓存 meta 与失效；不必并入 BGI 数据中心。 |
| `BgiLogWatcher.DiscoverCore/DiscoverAll`（`:356,415`） | BGI 日志发现，非递归、目录元数据 | **合规但高放大**，见 §3。 |
| `BgiLogWatcher.GetPipePaths`（`:98-124`）与 `ManualAgent`（`:46`） | 命名管道发现，非 BGI 数据；30s 缓存 | **合规/中**。保持缓存；管道泄漏时可能极慢，不可在 UI 调用。 |

### 4.2 路线更新（独立数据域）

`RouteUpdater` 的 `:122,148,161,966,1098,1121,1265` 分别覆盖缓存清理/大小、路线树/备份大小/更新文件统计、压缩包内容与恢复/遍历。它们属于路线更新，不是日志旁路。手动/定时更新走 `RunAsync` 后台任务（`Views/RouteUpdatePage.xaml.cs:787-802`、`Services/RouteUpdateScheduler.cs:161`），**功能合规**。

例外是 `Views/SettingsPage.xaml.cs:212-249 → ShowDataDirInfo:981-1027`：在页面激活同步调用 `GetBackupSize`（`RouteUpdater.cs:154-168`）和 `GetCacheSize`（`:142-150`）。**高 UI 风险**；后台缓存统计，页面只读最后一次值。手动 `PruneCache_Click`（`:1137-1152`）和 `PruneBackups_Click`（`:1166-1184`）也同步执行递归清理并随后重扫，应改为 async 后台任务。

### 4.3 BGI 备份/更新（独立数据域）

`BgiUpdater` 的 `:322,351-364,447-458,573-579,621-624,662,749` 覆盖更新包检查、备份裁剪、旧目录迁移、占用统计、User 目录压缩。它们读自己的更新/备份数据，**不是数据中心旁路**。

但 `SettingsPage.Activate → ShowDataDirInfo:1001` 同步调用 `BgiUpdater.GetBackupSize`（`BgiUpdater.cs:655-669`）递归遍历所有备份。**高 UI 风险**，同 §4.2 改为后台缓存。更新动作走 `BgiUpdatePage` 的异步批处理，备份转换/迁移按钮应继续后台执行。

### 4.4 数据迁移与启动清理（独立数据域）

- `DataMigration.Inspect`（`Services/DataMigration.cs:60-108`）在 `SettingsPage.Activate → ShowDataDirInfo:1022` 同步递归统计旧目录。**高 UI/合法功能**；点击迁移前再后台 Inspect，Migrate 本体已有 `Task.Run`（`SettingsPage.xaml.cs:1258-1270`）。
- `DataMigration` 的复制/备份枚举（`:188,262`）在后台迁移流程，**合规**。
- `LaunchSweeper.SweepFiles`（`Services/LaunchSweeper.cs:49-70`）递归扫本应用临时目录；`App.xaml.cs:239-240` 在启动路径同步调用。**中/启动风险/合法清理**；与启动备份裁剪一样移到后台（后者已 `Task.Run`，`:245-267`）。

### 4.5 其余独立目录/文件调用

| 调用点 | 判定 |
|---|---|
| `SystemLauncher.FindMtExe`（`Services/SystemLauncher.cs:388-406`） | 启动清单工具发现，非数据中心；递归 Windows SDK 目录，**中启动风险**，可缓存或移后台。 |
| `ConfigStore`（`:70`）、`ProxyHost`（`:153`）、`Mp4Writer`（`:143`）、`WebServer`（`:399,478,528,598,1061`）、`DogfoodRecordReader`（`:115,133`） | 分别属于自身配置、代理、录像封装、网页/录像、狗粮记录数据域；无证据表明是 BGI 日志旁路。仍需遵守各自 UI 禁 IO，不在本报告改造。 |
| `RouteLedger.RootDir` 下的 `*.jsonl` | 台账自身的合法只读文件存储，不是扫描 BGI 日志；不得返回文件路径。 |

`Controls/` 无目标 API 命中。`tools/` 中的 `broker-setup`、`updater`、`rescue`、`layout-check` 是独立工具/测试；其进程/目录调用**不属于主程序运行时**，不计入主程序旁路或卡顿结论。

## 5. FileSystemWatcher 完整清单

| 调用点 | 活性 / 建议 |
|---|---|
| `BgiLogCenter.Entry.Watcher` 与 `EnsureWatcher`（`Services/BgiLogCenter.cs:58-65,246-284`） | **活跃且合规**：中心自己的 watcher，只置 Dirty，由中心读取。保持每账号复用与 Dispose。 |
| `BgiLogWatcher.LogFollower._watcher`（`Services/BgiLogWatcher.cs:788,825`） | 无生产实例；**死入口/清理项**，防止未来再建第二 watcher。 |
| `Views/LaunchPage.xaml.cs:1018,1077` | 仅历史注释，无 watcher 实例；**不是实际旁路**。 |
| `Services/AccountStateCenter.cs:416` | 注释，不是 watcher 创建点。 |

## 6. 台账/今日统计 API：不是旁路，但有重复读放大

`WebServer.ApiBgiLedger`（`Services/WebServer.cs:545-561`）调用 `RouteLedger.Query`、`GroupSummary`、`QueryZeroReward`、`QueryFailed`：后三个各自再次 `Query`，合计**同账号/天数四次目录枚举与文件反序列化**。这在鉴权后台 HTTP 线程，且不扫描 BGI 日志，**不是日志旁路**，但 `days` 最大 365 时是中等开销。

建议：一次 `Query` 后共享 records，内存中 GroupBy/Where 产出其余三项；不要新建周期任务或 RefreshAll。

`ApiBgiStats`（`:564-579`）调用 `DailyStatsCache.Get`，是当前活入口。复制集合是正确方向，但若缓存确实由 BGI 增量线程写入，读侧仍需一个中心提供的原子快照，不能只靠复制消除所有竞态。

## 7. 整改优先级

1. **P0：停掉停止等待的重复日志读取**。将 `WaitForScriptStoppedAsync` 接中心事件/游标快照；与状态展示共享判定口径。
2. **P0：清掉 UI/页面激活同步重 IO**。设置页 ShowDataDirInfo 的备份/缓存/迁移统计；ActionEditor 的目录与进程发现；ProcessPage 的 Count。
3. **P1：BGI 发现只产一份共享快照**。Discover/DiscoverAll 同缓存；路径解析、账号中心、自动化和 UI 不各自重复枚举。
4. **P1：ledger API 一次读取，内存派生**。
5. **P2：删除无生产调用的旧日志读取/LogFollower/OldListBgiConfigs**。注意 `DailyStatsCache.Get` 现已是活 API 入口，不能误删。
6. **独立功能保留后台按需**。不要把 RouteUpdater/BgiUpdater/DataMigration/SessionSlimmer 强行并入日志中心；只消除 UI 直扫、重复扫描和无界增长。

## 8. 边界声明

- 本报告只依据当前源码静态调用链，未测实际耗时，风险等级不冒充基准结果。
- “无生产调用方”指主程序源码检索无调用；`tools/` 测试代码另行排除。
- 未修改业务代码、未 build、未操作运行进程。
