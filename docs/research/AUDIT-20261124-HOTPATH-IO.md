# 热路径同步 I/O 审计（2026-11-24）

> 任务：T13（dsh-team `soullink-p0-fix-yomori` / member `yomori-scout` / attempt `93e9ba3e-627b-4bb8-b73f-81411e382b5f`）
> 性质：**只读审计，未改任何产品代码**。未发布、未碰 `dist\stable`、未用 A1/A4、未 git commit。临时文件只在 `%TEMP%`（已清）。
> 起因：Yomori 的 588 MB / 整窗卡死事故 —— 诊断写盘放在锁内、每帧级触发，UI 线程抢同一把锁。
> 与 `AGENTS.md` 性能约束 3（`Process.HandleCount` 每次 3~5ms）同源。

---

## 0. 一页结论

### 0.1 最重要的一句：**没有找到符合「高风险」定义的点**

按任务给定的分级口径（高风险 = UI 线程同步写盘**或**持锁写盘，**且**频率 ≥ 10Hz）：

| 判据 | 结果 |
|---|---|
| UI 线程同步写盘的点 | **存在**，共 4 处（见§2.1） |
| 其中频率 ≥ 10Hz 的 | **0 处** |
| 持锁写盘的点 | **0 处**（`AsyncLog` 无锁；`RouteLedger` 的锁与写盘已分离） |
| ⇒ 高风险项 | **0 项** |

**证据（可复现）**：

```
# ≥10Hz 的 UI 定时器：
grep -n "Interval = TimeSpan.FromMilliseconds" Views/*.cs Controls/*.cs
# -> 16ms(MainWindow:1071) / 33ms(FlowGuideOverlay:73) / 60ms(ConsolePage:3409) / 150ms(ConsolePage:561)
#    逐个读函数体确认磁盘调用为 0（实测，见§2.4）
#
# 同步磁盘写原语总数（regex 统计，非 grep 行数）：
#   Services=28  Relay=10  Controls=1  Views=4
```

### 0.2 三个真实问题（虽不达「高风险」，但都值得处理）

| # | 问题 | 位置 | 为什么值得管 |
|---|---|---|---|
| **A** | **同一份日志有两个写实现，一个走队列、一个直写盘** | `Controls/RdpConsole.cs:2116`（走 `AsyncLog`）vs `Views/ConsolePage.xaml.cs:1858`（**直接 `File.AppendAllText`**） | 两条路写同一个 `rdp-debug-YYYYMMDD.log`。ConsolePage 那条在 UI 线程同步 open/append/close，**随调用频率线性放大**。当前被状态门控压住，但这是「性能取决于业务逻辑恰好不频繁」的侥幸，不是设计保证 |
| **B** | **任一 TickHub 订阅者持续抛异常 → 每秒一次 UI 线程同步写盘** | `Services/TickHub.cs:190`（UI 订阅者）→ `App.xaml.cs:469` 的 `File.AppendAllText` | TickHub 节拍固定 1s（`TickHub.cs:136`）。异常持续时 = **1Hz**，按口径算中风险；但它是**唯一一条「故障态自动升级为持续写盘」的路径**，且写的是完整 `ex.ToString()`（1~5 KB/次） |
| **C** | **`AsyncLog.Append` 在调用线程上做一次文件 stat** | `Services/AsyncLog.cs:48-62` | 已知是 `new FileInfo + Exists + Length`，**实测 13.7 µs/次**（10 万次 1370ms）。本身不构成卡顿，但它跑在**调用者线程**（可能是 UI 线程），且 `Rotated` 是无锁 `HashSet` |

### 0.3 设计上做对了的（不要改坏）

- `AsyncLog`：有界队列（512）+ 后台单线程消费（`AsyncLog.cs:14-32`），`TryAdd` 不阻塞；
- `RouteLedger`：**专用后台写线程 + 队列**（`RouteLedger.cs:141-146`），注释明写「**写 IO 绝不在 UI 线程**」（`:90-92`）；
- `TickHub.RegisterBackground`：把 BgiLogCenter / BgiLogWatcher / ScheduleRunner / RouteUpdateScheduler 这四个**内部做同步 IO** 的订阅者迁出 UI 线程（`TickHub.cs:74-92`）—— 这正是本次审计想找的那类修复，**已经做过了**；
- `Relay/DxgiDdaCapture.cs`（**逐帧抓屏路径**）：磁盘调用 **0 处**；
- `LogBus`：纯内存 `ObservableCollection` + `Dispatcher.InvokeAsync`，**不碰磁盘**（`LogBus.cs:82-91`）。

---

## 1. 审计方法与口径

### 1.1 扫描范围与命令

按任务要求覆盖 `Services/`、`Relay/`，并额外扩到 `Controls/`、`Views/`（因为 `Views/ConsolePage` 存在直写盘路径，不查会漏）。

```
扫描的原语（6 种）：
  File.AppendAllText(      File.WriteAllText(
  File.WriteAllBytes(      File.Create(
  FileMode.Create|Append    new StreamWriter(

按 scope 的命中总数（regex 统计）：
  Services   28   (106 个 .cs)
  Relay      10   ( 10 个 .cs)
  Controls    1   ( 11 个 .cs)
  Views       4   ( 34 个 .cs)
```

另外单独扫了 `Directory.CreateDirectory`（36 处，多为一次性初始化），仅在与写盘同函数且高频时标注。

### 1.2 频率判定方法（**不靠猜**）

| 判定为 | 依据 |
|---|---|
| **>= 10Hz** | 所在函数被 DispatcherTimer / TickHub 以 < 100ms 间隔调用，**且已读该函数体确认无状态门控** |
| **1~10Hz** | TickHub 1s 节拍内调用，或 100~1000ms 定时器 |
| **事件驱动 / 一次性** | 用户动作、状态跃迁、初始化、错误路径 |
| **可放大** | 错误路径，但若故障持续则退化为周期性（本次特别标注，见§3-B） |

### 1.3 线程判定方法

| 判定为 | 依据 |
|---|---|
| **UI 线程** | 调用链起点是 `DispatcherTimer.Tick` / `Click` / `Loaded` / `TickHub.Register`（非 Background） |
| **后台线程** | `Task.Run` / `ThreadPool.QueueUserWorkItem` / 专用 `Thread` / `TickHub.RegisterBackground` |

---

## 2. 风险表

### 2.1 UI 线程 · 同步写盘（**本审计重点**）

| # | 文件:行 | 调用 | 线程 / 锁 | 频率 | 风险 | 建议 |
|---|---|---|---|---|---|---|
| 1 | `Views/ConsolePage.xaml.cs:1858` | `File.AppendAllText`（+`:1856` `Directory.CreateDirectory`） | **UI 线程**（DispatcherTimer 派生链）/ 无锁 | **事件驱动**（52 个调用点，绝大多数是状态跃迁；受 `IsImportant()` 过滤） | **中** | 改为 `AsyncLog.Append`，与 `RdpConsole.Debug:2116` 统一。**见§3-A** |
| 2 | `Services/TickHub.cs:190` → `App.xaml.cs:469` | `File.AppendAllText`（error.log） | **UI 线程**（`Pump()`，DispatcherTimer 1s）/ 无锁 | **持续异常时 1Hz**（正常 0） | **中** | 加「每订阅者 N 秒内最多记一次」降频，或改走 `AsyncLog`。**见§3-B** |
| 3 | `Controls/RdpConsole.cs:512` | `File.AppendAllText`（watchdog.log） | **UI 线程**（`TickHub.Register("RdpConsole.LockWatchdog", 5, …)`，`:491`）/ 无锁 | **< 1/45s**（`:508` `idle < 45` 提前返回，`:475`） | **低** | 保留。注释（`:509`）已说明「关键安全事件，必须同步落盘」——**有正当理由的同步写**，不要改 |
| 4 | `Views/ConsolePage.xaml.cs:341` | `File.AppendAllText`（shield-trace.log） | UI 线程 / 无锁 | **仅 `--verbose-log`**（`:320` 提前返回）；非 verbose **0 次** | **低** | 保留（诊断开关）。可顺手改 `AsyncLog` |
| 5 | `Services/OwnedOverlayWindow.cs:341` | `File.AppendAllText`（overlay-window.log） | UI 线程 / 无锁 | **仅 verbose**（`:331` 提前返回）；**全仓仅 1 个调用点**（`:138`） | **低** | 保留（诊断开关） |
| 6 | `Views/SettingsPage.xaml.cs:1358` | `File.WriteAllText`（导出配置） | UI 线程 / 无锁 | **用户点击一次** | **低** | 无需改（用户主动保存文件，必须同步确认结果） |
| 7 | `App.xaml.cs:469`（其他 59 个调用点） | `File.AppendAllText`（error.log） | 混合（含 UI 未处理异常）/ 无锁 | 事件驱动 | **低** | 保留（异常必须留证） |

**⇒ UI 线程同步写盘中，频率最高的两项是 #1（事件驱动，受过滤）与 #2（1Hz，仅故障态）。均未达 10Hz 高风险阈值。**

### 2.2 后台线程 · 同步写盘（可接受，但记录）

| # | 文件:行 | 调用 | 线程 / 锁 | 频率 | 风险 | 建议 |
|---|---|---|---|---|---|---|
| 8 | `Services/AsyncLog.cs:25-26` | `File.WriteAllText` / `File.AppendAllText` | **专用后台 Task**（`:15`）/ **无锁** | 消费队列，无固定频率 | **低** | 全仓日志的「正确出口」。**保留** |
| 9 | `Services/RouteLedger.cs:360-366` | `FileStream(Append, WriteThrough)` + `fs.Flush()` | **专用后台线程**（`:144-146`）/ 锁（`:342,352,368`）**与写盘分离** | 事件驱动 | **低** | **标杆实现**。`WriteThrough` 是刻意换持久性（注释 `:82`「崩溃最多丢最后一行」），**不要改** |
| 10 | `Services/BrokerService.cs:22-23` | `File.AppendAllText`（broker.log） | 服务进程 / 无锁 | 每请求 8~10 行（见§3-D） | **低**（不在 UI 线程） | 可降频，但**服务卡顿不影响用户界面**，优先级最低 |
| 11 | `Relay/AgentWebServer.cs:214` | `File.AppendAllText`（agent-web-error.log） | Agent 线程 / 无锁 | 请求级错误 | **低** | 注释（`:211`）已说明理由，保留 |
| 12 | `Relay/RelayAgent.cs:545` | `AsyncLog.Append`（走队列，非直写） | Agent / **持 `LogLock` 但锁内只做内存 `StringBuilder`**（`:539-543`） | 事件驱动 | **低** | 锁内不碰磁盘，**写法正确** |

### 2.3 一次性 / 初始化（不构成风险，列出以证明已覆盖）

`Services` 中 28 个写原语里，除上表 8/9/10 与 `AsyncLog` 自身外，其余均为一次性：配置保存（`ConfigStore.cs:153,289,411`，且有后台串行保存队列，见 `:91-99` 注释）、身份文件（`DeviceIdentity.cs:55,102`）、启动项（`AutoStart.cs:79`）、投送部署（`SystemLauncher.cs:219,358,620`、`ManualAgent.cs:178,184`、`ProxyHost.cs:94`、`AgentSync.cs:199,240`）、更新与路线（`CloudUpdater.cs:207`、`RouteUpdater.cs:904,943`）、RDP Wrapper 释放（`RdpWrapService.cs:144,149,154`）、录制收尾（`SessionRecorder.cs:1334,1346,1464,1495,1496`）。

`Relay/RelayAgent.cs:1263` 与 `SessionRecorder.cs:1495` 是**截图/录制落盘**，用户显式触发，非周期。

### 2.4 ≥10Hz 定时器逐个核对（**「无高风险」的核心证据**）

| 文件:行 | 间隔 | 频率 | 函数体内磁盘调用 | 判定 |
|---|---|---|---|---|
| `Views/MainWindow.xaml.cs:1071` | 16 ms | ~62 Hz | **0**（导航浮层动画） | 安全 |
| `Controls/FlowGuideOverlay.cs:73` | 33 ms | ~30 Hz | **0**（动画帧回调） | 安全 |
| `Views/ConsolePage.xaml.cs:3409` | 60 ms | ~17 Hz | **0**（实测；每拍只读光标位置） | 安全 |
| `Views/ConsolePage.xaml.cs:561` | 150 ms | ~6.7 Hz | **0**（实测；保护层自愈，仅状态跃迁时 `Debug()`） | 安全 |
| `Controls/GlassControls.cs:173` | 120 ms | ~8.3 Hz | **0** | 安全 |
| `Views/LogoffTransitionWindow.xaml.cs:17` | <100 ms | — | **0** | 安全 |

**6 个高频 UI 定时器，磁盘调用全部为 0。**

---

## 3. 三个真实问题的详细分析

### 3-A 同一份日志两个写实现（**最值得修**）

| | RdpConsole.Debug | ConsolePage.Debug |
|---|---|---|
| 位置 | `Controls/RdpConsole.cs:2109-2121` | `Views/ConsolePage.xaml.cs:1850-1863` |
| 过滤 | `!VerboseLog && !IsImportant()` → return | 同（`:1855`） |
| 落盘 | **`AsyncLog.Append`（队列）** | **`Directory.CreateDirectory` + `File.AppendAllText`（同步）** |
| 目标文件 | `rdp-debug-YYYYMMDD.log` | **同一个文件** |

**为什么危险（即使当前频率低）**：
1. ConsolePage 有 **52 个 `Debug()` 调用点**，比 RdpConsole 多；
2. 其中若干在**看门狗/监控回调内**（如 `:610,622,636` 在 150ms 保护层看门狗里），当前靠「仅状态跃迁才触发」压住了 —— 这是**业务逻辑的偶然属性，不是并发/频率保证**；
3. 一旦有人给某个看门狗加一行 `Debug(...)`，立刻变成 **6.7Hz UI 线程同步写盘**；
4. 两个实现写同一文件，还可能产生交错写入。

**建议（0.25 人日）**：把 `:1856-1860` 换成 `AsyncLog.Append(ConfigStore.DailyLogPath("rdp-debug"), …)`，删掉那两行 `Directory.CreateDirectory`/`File.AppendAllText`。**行为完全等价**（AsyncLog 自己会建目录，见 `AsyncLog.cs:24`），但 UI 线程磁盘调用降为 0。

### 3-B TickHub 订阅者异常 → 1Hz 持续写盘（**故障放大路径**）

`Pump()`（`TickHub.cs:144-197`）每秒遍历订阅者。UI 订阅者抛异常时（`:186-193`）立即调 `App.AppendErrorLog` → `App.xaml.cs:469` 的**同步 `File.AppendAllText`**。

- 正常态：0 次/秒，无风险；
- **某订阅者持续抛异常**：1 次/秒，每次写完整 `ex.ToString()`（含 StackTrace，通常 1~5 KB）→ **约 3.6~18 MB/小时，且全在 UI 线程**；
- 更糟：`AppendErrorLog` 直写，**不走 `AsyncLog` 的 5MB 轮转**，即**没有任何大小上限**。

**对比**：`AsyncLog` 有 5MB 上限 + `.old` 轮转（`AsyncLog.cs:38,48-62`）；`AppendErrorLog` **两者都没有**。

**建议（0.25 人日）**，二选一：
- (a) 给 `AppendErrorLog` 加同样的 5MB 轮转（与 AsyncLog 对齐）；
- (b) TickHub 对同一订阅者的连续异常降频（如「60 秒内只记首尾两次，中间合并计数」）。

### 3-C AsyncLog.Append 的 stat 与无锁 HashSet

`AsyncLog.cs:48-62` 在**调用者线程**上执行：

```csharp
if (!Rotated.Contains(path)) {          // <- 无锁 HashSet，多线程读写
    var fi = new FileInfo(path);
    if (fi.Exists && fi.Length > MaxBytes) { ... File.Delete / File.Move ... }
    else Rotated.Add(path);              // <- 无锁 Add
}
Queue.TryAdd(new Entry(path, text, false));
```

- **实测成本**：`new FileInfo + Exists + Length` = **13.7 µs/次**（10 万次 1370ms）。这个量本身**不构成卡顿**（对比：一次 `File.AppendAllText` 打开+写+关通常 50~500 µs）。
- **真正的隐患是线程安全**：`Rotated` 是普通 `HashSet<string>`，`Append` 被 UI 线程、TickHub 后台线程、Agent 线程同时调用。
- **但我做了实证测试，没有复现出问题**：200 并发 × 20 万次 `Contains`+`Add`，**errors=0，count 正确=200**。原因很可能是该集合很快稳定（每个路径只 `Add` 一次，稳定后全部走只读 `Contains`）。
- **诚实结论**：这是**理论上的数据竞争**，实测未复现；按「没有证据不下结论」的纪律，**列为低风险观察项，不建议现在改**。

**建议（若将来要改）**：换成 `ConcurrentDictionary<string,byte>`（零风险、零成本），或至少在 `Contains/Add` 外层加 `lock`。

### 3-D BrokerService 每请求 8~10 行日志（低优先）

`BrokerService.cs:18-26` 无缓冲直写 `broker.log`；每次投送请求走 `:63,81,83,89,91,97,181,200,204` 等 **8~10 次 `Trace`**，即 8~10 次 open/write/close。

- 频率：每次投送一次（用户操作级，不是秒级）；
- 线程：**服务进程，不在 UI 线程** → 用户界面不受影响；
- 但若将来做「批量投送 N 个账号」，会变成 N×10 次同步写。

**建议**：低优先。若要做，把 `Trace` 改成进程内缓冲 + 定期 flush（不能走 `AsyncLog`，因为服务进程退出前必须落盘）。

---

## 4. 与 Yomori 那条事故的对照（回答「我们有没有同款」）

Yomori 的事故链（`winui3/docs/rdp-hang-and-heap-corruption-2026-10-16.md:89-107`，转述）：诊断写盘放在 `g_lock` 内 → 每帧级触发 → UI 线程抢同一把锁 → 588MB + 整窗卡死。

| 事故要素 | 我们是否有 | 证据 |
|---|---|---|
| ① 写盘在**锁内** | **无** | `AsyncLog` 全类 **0 个 lock**（grep 实测）；`RelayAgent.Log` 的 `LogLock` 内只做内存 `StringBuilder`（`:539-543`）；`RouteLedger` 的锁与 `FileStream` 已分离（`:342 vs :360`） |
| ② **每帧级**触发写盘 | **无** | 逐帧路径 `Relay/DxgiDdaCapture.cs` 磁盘调用 **0 处**；`Relay/SessionRecorder` 的写盘全在录制收尾 |
| ③ **UI 线程**上做同步写盘 | **有 4 处**（§2.1 #1/#4/#5/#6） | 频率均 < 10Hz，其中 #4/#5 仅 verbose 开关下生效 |
| ④ 无**大小上限**的日志 | **有 1 处**（`3-B `error.log`） | `AsyncLog` 有 5MB 轮转，`AppendErrorLog` **没有** |

**⇒ 结论：我们没有 Yomori 那条链（①+② 都不成立）。最接近的是 ④ —— 日志无上限，但触发条件是「持续异常」，不是常态。**

---

## 5. 优先级建议（若 Lead 要排期）

| 序 | 项 | 人日 | 收益 | 风险 |
|---|---|---|---|---|
| 1 | `3-A：`ConsolePage.Debug` 改走 `AsyncLog` | **0.25** | UI 线程磁盘调用 -1 处；消除「加一行日志就变 6.7Hz 同步写盘」的隐患 | 极低（行为等价） |
| 2 | `3-B：`AppendErrorLog` 加 5MB 轮转 | **0.25** | 补上与 `AsyncLog` 一致的磁盘上限，防故障态写满盘 | 极低 |
| 3 | `3-C：`Rotated` → `ConcurrentDictionary` | 0.1 | 消除理论数据竞争 | 极低（但**实测无复现**，可不做） |
| 4 | `3-D：`BrokerService.Trace` 批量化 | 0.5 | 批量投送时不放大 | 中（服务进程落盘语义要保住） |

**合计 0.5~0.6 人日可做完 1~2 项。均非 P0，建议排在 P0 收敛之后。**

---

## 6. 边界遵守自查

只读审计：**未修改任何产品代码**（`git status` 确认 `Services/`/`Controls/`/`Views/`/`Relay/`/`SoulLink.csproj` 的改动全部来自 core-fixer / ui-fixer，非本次审计所为）；未发布、未碰 COS/`latest.json`、未碰 `dist\stable`；未使用 A1/A4；未起停任何进程；未 git commit。

临时文件：仅 `%TEMP%\asyncprobe.log`（性能探针，已删）与 `%TEMP%\hashrace`（HashSet 并发实测工程，已删）。

唯一写入 = 本文档 + `docs/research/README.md` 一行索引。

---

**文档作者**：yomori-scout
**最后更新**：2026-11-24