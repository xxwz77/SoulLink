# UI 线程减负：周期 IO 迁移 + RdpConsole 定时器收编 + 同步 IO 分类

> 日期：2026-11-23
> 成员：ui-perf（dsh-team / task `task_mws3fi0r851gl`）
> 范围：`Services/TickHub.cs`、`Services/ConfigStore.cs`、`Services/BgiLogCenter.cs`、
> `Services/BgiLogWatcher.cs`、`Services/ScheduleRunner.cs`、`Services/RouteUpdateScheduler.cs`、
> `Controls/RdpConsole.cs`、`App.xaml.cs`（仅 ExitApp 加 1 行 Flush）。
> 未碰 `Relay/`、`MfFrameEncoder`、`RelayClient`、`WebPages`、`web-index.html`、`SoulLink.csproj`、`tools/`。

---

## 0. 一句话结论

「点按钮/切页要等」的直接来源 —— 每秒发生在 UI 线程上的同步文件 IO —— 已全部挪到后台线程；
「拖/缩放窗口卡」的直接来源 —— RdpConsole 的 5 个 DispatcherTimer —— 已全部收编/事件驱动化（0 个残留）。
`ConfigStore.Save` 改为「调用线程快照序列化 + 后台串行队列 + 原子写」，UI 线程不再等磁盘。

---

## 1. P0：TickHub 后台订阅 + 4 个订阅者迁移

### 1.1 改动

`Services/TickHub.cs`：

- 新增 `RegisterBackground(name, everySeconds, callback)`：回调在 .NET 线程池执行。
- 语义与 `Register()` 完全一致：统一节拍、按 N 拍分频、单订阅者异常隔离。
- 额外一条：后台订阅**防重入**（上一拍回调未跑完则本拍跳过），等价于 UI 版 Pump 的 `_pumping`，
  避免后台回调慢于节拍时排队堆积。

### 1.2 迁移的 4 个订阅者（全部从 `Register` → `RegisterBackground`）

| 订阅者 | 原线程 | 内部同步 IO | 现线程 | UI 回切 |
|---|---|---|---|---|
| `BgiLogCenter.ServiceTick` | UI | FileStream 读日志 + UTF8 解码 + 行解析 + IsAlive | 线程池 | `Changed` 事件本就声明「后台线程抛出」，唯一订阅方 LaunchPage.OnLogCenterChanged 已用 `Dispatcher.InvokeAsync` |
| `BgiLogWatcher.Pump`（LogFollower.Fallback） | UI | FileInfo.Length + FileStream.Read(≤256KB) + UTF8.GetString | 线程池 | `_dispatcher.BeginInvoke` + 新增 `HasShutdownStarted` 守卫 |
| `ScheduleRunner.Tick` | UI | ConfigStore.Save（写盘）+ 遍历流程 | 线程池 | LogBus / AppendErrorLog 自身已切 UI |
| `RouteUpdateScheduler.Tick` | UI | ConfigStore.Save（写盘） | 线程池 | 同上 |

> `LaunchPage / LobbyPage / MainWindow.UpdateCheck / RdpGeneralSettings.recordstatus` 属于 UI 层，
> 本任务不迁（Lead 分析已注明「另议」）。它们的回调内无同步文件 IO，留在 UI 线程符合语义。

---

## 2. P1：RdpConsole 5 个 DispatcherTimer 收编（0 残留）

| 定时器 | 原间隔 | 处置 | 理由 |
|---|---|---|---|
| `_captureWatch` | **60ms** | **改事件驱动**：在 `OnGlobalMouse`（全局鼠标钩子，UI 线程）里，`ev.IsMove` 时调用 `WatchPointerCaptureEventDriven(ev.X, ev.Y)` | 原来每 60ms 空转读 `Cursor.Position` + 算画布矩形，光标不动也在烧；改事件驱动后光标不动零开销，出界判定延迟从「≤60ms」变「下一次移动事件」。画布矩形加 120ms 缓存，避免拖动时每帧 `PointToScreen`。语义不变（释放态移出画布 → 释放 + 请求重新上锁） |
| `_lockWatchdog` | 5s | **收编进 TickHub**（`Register`，保留 UI 线程） | 每拍只读两个字段比较，无 IO；超时那一拍才同步落 watchdog.log（安全关键，必须同步可查）+ `ReleaseCapture` 动 UI 状态。放后台会引入跨线程可见性问题，收益≈0，故收编不迁后台 |
| `_rebuildTimer` | 指数退避 2s~30s | **改 `CancellationTokenSource` + `Task.Delay`**，到点 `Dispatcher.BeginInvoke` 回 UI 执行 | 一次性退避不是周期任务，用后台 Task.Delay 等待、CTS 取消替代；重建动作仍在 UI 线程（碰 `Input`/`BeginInput`） |
| `_resizeDebounce` | 500ms | **改 `CancellationTokenSource` + `Task.Delay`**，到点 `Dispatcher.InvokeAsync` 回 UI 协商 | 「拖/缩放卡」最直接来源。原来 UI 线程挂一个空等 500ms 的定时器；现在后台等停手，UI 线程不占。防抖语义（停手 500ms 才协商一次）不变 |
| `_stretchRefit` | 16ms | **改「版本号 + `Dispatcher.BeginInvoke(Background)`」** | 一次性防抖。每次尺寸变化版本号+1，只让最新版本在下一拍执行，等价于「16ms 内多次 SizeChanged 只跑最后一次」，不再挂定时器 |

验收：`grep DispatcherTimer Controls/RdpConsole.cs` 现在只剩注释，无任何 `new DispatcherTimer`。

---

## 3. P2：ConfigStore.Save 后台串行队列

### 3.1 设计

```
调用线程（UI / 后台）                        单消费者后台线程
     │                                          │
  Serialize(cfg) ── 不可变 JSON 字符串 ────────▶ BlockingCollection (FIFO)
     │                                          │
  UI线程: 入队后立刻返回                   按入队顺序逐个 WriteAtomic()
  非UI线程: 入队并等自己的项写完再返回          │
                                    temp 文件 → File.Replace（原子替换）
```

关键决策：

1. **入队前先序列化**（纯内存，调用线程执行）。这既保证 FIFO，又彻底消除
   「后台线程读到一个正被 UI 改到一半的活对象」的竞态。
2. **严格 FIFO**：单消费者 `BlockingCollection`，`GetConsumingEnumerable` 按入队顺序写盘。
   后入队的值一定写在先入队之后 —— 旧值不可能盖新值（历史事故见 ConsolePage.xaml.cs:240）。
3. **原子写**：`temp` 文件 → `File.Replace`（原逻辑保留）；失败回退 `File.Copy + Delete`。
4. **只有 UI 线程才 fire-and-forget**（这才是 P0 要消除的同步 IO）；
   其它线程（后台调度器 / FlowRunner / 无 WPF 的测试工具）保持同步语义 —— 等自己的项写完再返回。
   这保住了 `Save()` → `Load()`/`ReadAllText` 的 read-your-writes 契约（`tools/layout-check/PlcConfigCheck` 依赖它）。
5. **退出 flush**：`App.ExitApp()` 加 `ConfigStore.Flush()`（排空队列）；
   静态构造器再挂 `ProcessExit` 兜底同步排空，任何退出路径都不丢最后一次保存。

### 3.2 保序论证（为什么不会出现「配置被旧值覆盖」）

- 旧实现 `lock(ConfigIo)` 内序列化 + 写盘：单次保存是原子的，但**两次保存的顺序取决于谁先拿到锁**，
  而锁竞争顺序不由「调用发生的先后」决定 —— UI 点一下、调度器每秒写一下，
  就可能出现「先调用的后拿锁」→ 旧值后落盘。
- 新实现：序列化在调用线程按真实时间先后完成，字符串进**单一 FIFO 队列**，
  单消费者严格按队列顺序写盘。因此「调用 A 先于调用 B」⇒「A 的落盘先于 B 的落盘」恒成立。
- 并发压测：`tools/layout-check/PlcConfigCheck` 覆盖了 `Save` → 立即 `Load` 读回
  （迁移 + 普通保存两条路径），全部 PASSED，证明非 UI 线程同步写回语义未被破坏。

---

## 4. P3：94 处同步 IO 分类清单

> 统计口径：`Views/` + `Controls/` 下的 `ConfigStore.Save / File.* / Directory.GetFiles /
> GetProcessesByName / Thread.Sleep`。总数与 Lead 给的 94 处基本一致（本次 grep 到 93 处 + 若干间接）。

### 4.1 三大类处置原则

| 类别 | 处置 | 本任务动作 |
|---|---|---|
| **用户点击触发**（File.Exists 校验、目录/文件选择器、导入导出、删除用户、保存按钮） | **保留**，不迁 | 无（点击才发生一次，不是卡顿来源） |
| **周期性/高频**（每秒/每几秒的读日志、写配置、进程枚举、命名管道枚举） | **必须迁走** | 已迁（见 §1/§2/§3） |
| **已有后台**（已 `Task.Run` / `ConfigureAwait(false)` / 后台队列） | **保持** | 无 |

### 4.2 周期性/高频的完整清单（已迁或本就后台）

| 位置 | 调用 | 原状态 | 现状态 |
|---|---|---|---|
| BgiLogCenter.ServiceTick | FileStream 读日志（每秒） | UI 线程（TickHub Register） | **后台**（RegisterBackground） |
| BgiLogWatcher.LogFollower.Pump | FileStream 读≤256KB（每秒兜底） | UI 线程 | **后台**（RegisterBackground） |
| ScheduleRunner.Tick | ConfigStore.Save（每秒） | UI 线程 | **后台**（RegisterBackground） |
| RouteUpdateScheduler.Tick | ConfigStore.Save（每5秒） | UI 线程 | **后台**（RegisterBackground） |
| LaunchPage.RefreshSessions | SessionService.QueryAsync + DiscoverLiveAgentSessions | 已 `Task.Run` + `InvokeAsync` | 保持（已有后台） |
| LaunchPage.ApplySessions | BgiLogWatcher.Discover（快照过期才枚举） | `Task.Run` 内 | 保持 |
| AccountStateCenter.RefreshOnce | SessionService + BgiLogWatcher.Discover + BgiLogCenter.Tick | System.Threading.Timer（后台） | 保持（本就后台） |
| ConfigStore.Save（UI 侧约 60 处） | 序列化 + 写盘 | UI 线程同步 | **UI 线程只序列化 + 入队，写盘后台** |

### 4.3 保留（用户点击触发）示例

- `SettingsPage` 的 16 处 `ConfigStore.Save`：全是用户改设置/选文件/导入导出后触发，单次。
- `ProcessPage` 的 6 处 Save：用户切开关/点清理。
- `LaunchPage` 的 Save：用户暂停/恢复定时、编辑动作后。
- 各页面 `File.Exists(vm.BgiPath)` / `File.Exists(exe)`：绑定/选择路径时单次校验。
- `ActionEditorWindow.GetProcessesByName("BetterGI")`、`FlowEditorWindow.Directory.GetFiles`：
  打开编辑器时单次枚举。

> 结论：真正需要迁走的「周期性/高频」已被 §1/§2/§3 全覆盖；
> 剩余全是点击级单次 IO，保留是正确选择，一刀切反而引入不必要的复杂度与风险。

---

## 5. 验证

### 5.1 构建

- `dotnet build -c Release -f net11.0-windows` → **0 error**
- `dotnet build -c Release -f net11.0-windows10.0.26100.0` → **0 error**

### 5.2 回归测试

- `tools/layout-check/PlcConfigCheck` → **ALL PASSED**（Save→Load 保序/迁移/原子写语义）
- `tools/layout-check/LogTailAppendCheck` → **ALL PASSED**（Changed→增量追加路径未受影响）

### 5.3 实测对比（CPU 数字）

> **环境约束（收到 Lead 即时通知后调整）**：禁止用 session 1 的 dist 主程序采样
> （用户说 dist 很卡，再挂采样器更卡）；采样对象只能是本工作区自己构建的产物，
> 干净环境跑，单次 ≤30 秒，跑完立刻收。系统刚因过载关机过，宁可少测不冒险。

- **改前基线**（沿用 PERF-3.3.4 实测，不改动其数字）：空闲挂机态 UI 线程 ~3.6% 单核 +
  BgiLogWatcher/BgiLogCenter 的 `Discover`/`DiscoverCore`/`DiscoverCached` 合计 ~17% 单核
  （其中相当比例落在 UI 线程同步 IO 的等待上）。
- **改后实测**：本任务按约束**未对 dist 做采样**。由于「启动第二个主程序实例」会抢端口
  19080 / RDP 会话，且系统刚过载关机，未敢起独立实例压测。
  **结论标注为：逻辑/静态论证 + 回归测试已过；CPU 数字未实测**（这是 Lead 明确允许的降级路径）。

改后 UI 线程每个 1 秒节拍只剩「4 次 `ThreadPool.QueueUserWorkItem` 投递」（纳秒级），
原「每秒同步读日志 + 写配置」的 IO 全部落到后台线程。定性结论：UI 线程的周期性阻塞 IO 已归零。

---

## 6. 已知遗留（需要 Lead / 后续跟进）

1. **`tools/layout-check/ChannelDiscoveryCheck` 构建失败（预存在，非本任务引入）**：
   它引用 `BgiLogWatcher.DiscoverChannelSessions()`（当前源码已改名为 `DiscoverLiveAgentSessions`），
   且 csproj 缺 `App`/`TickHub`/`RelayClient` 桩。该测试在本任务改动前就已编译不过。
   属 `tools/` 范围，不在我的文件清单内，未修 —— 请 Lead 决定是否让别的成员顺手修。
2. `MainWindow`/`LaunchPage`/`LobbyPage`/`RdpGeneralSettings` 的 UI 层 TickHub 订阅者（刷新卡片/状态灯）
   回调内无同步 IO，本次不迁。若后续要榨 UI 线程，可评估把「卡片重建」这类 WPF 元素操作
   外的纯计算也挪走 —— 但那是 P3 渲染侧，需先有火焰图证据。
3. `RdpGeneralSettings.recordstatus`（5s，`RefreshBackendStatusAsync`）与
   `MainWindow.UpdateCheck` 若内部有网络/文件 IO，应审计 —— 本次未深查（属 UI 层「另议」）。
