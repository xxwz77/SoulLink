# T31 抓屏超时改为跟随帧间隔：根治「时好时坏」

> 症状：用户感知「释放时好时坏」；日志 `captureMs` 在 5~15 与 100 之间跳。

---

## 0. 先回答你提的两个技术质疑（这两个都关键）

### 质疑一：`WaitUntilNextSlot` 的节拍会不会与抓屏超时叠加？

**不会 —— 而且我找到了确切原因。**

临界代码 `SessionRecorder.cs:783`：
```csharp
if (!forceKeyFrame && !inputTrigger && !WaitUntilNextSlot(ref nextAt))
    continue;
```

**`inputTrigger=true` 时，`!inputTrigger` 为 `false` → C# 短路求值 → `WaitUntilNextSlot()` 根本不被调用！**

所以 urgent 路径的真实时序是：
```
urgent 到达 → LifecycleChanged.Set() 唤醒 worker
  → 循环顶消费 urgentInput = true
  → :783 短路，跳过 WaitUntilNextSlot（0ms）
  → 直接抓屏（此拍 InputTriggeredFrame=true，超时减半）
```

**⇒ 不存在「16+16=32ms」的叠加。改善是实打实的：**

| | 修前 | 修后 |
|---|---|---|
| urgent 落在在途抓屏中间 | 100 + 15 = **115ms** | 16 + 8 = **24ms** |
| 改善 | — | **降低 79%** |

### 但普通帧（无 urgent）**确实**有叠加 —— 这是「一直慢」的另一半来源

| | 修前 | 修后 |
|---|---|---|
| 普通拍 | WaitUntilNextSlot(0~16) + **100** = 最坏 **116ms/拍** | 0~16 + **16** = 最坏 **32ms/拍** |

**修前稳态每拍最坏 116ms，而 fps=60 要求 16ms/拍** —— 这就是为什么静止保活帧也要 100ms 才出。
**修后 ≤32ms，`AdvanceSlot` 累积式推进能追上节拍。**

---

## 1. 关于 CPU 增量：你上一条的自我修正**方向正确，但幅度估高了**

### 你说的对：「不是空转烧 CPU，是唤醒次数增多」

我核对了代码，静止路径确实是**阻塞等待 + 事件睡眠**，不是忙等：
```csharp
if (!forceKeyFrame && !desktopChanged && !keepaliveDue && !inputTrigger) {
    _skippedStaticFrames++;
    IdleWait();          // LifecycleChanged.WaitOne(5) —— 内核事件等待
    continue;
}
```
`AcquireNextFrame(超时)` 本身也是**内核阻塞**，等待期间 CPU 占用 ≈ 0。

### 实测数据（我实际跑了微基准）

| 测量 | 结果 |
|---|---|
| 单次「返回后用户态判定」开销（20000 次循环） | **JIT 优化后测不出（<0.001µs）** |
| 一次 `WaitOne` 系统调用返回开销 | ≈ 1~2µs（内核态） |
| 每秒唤醒次数变化 | 9.5 次/秒 → 47.6 次/秒（增加 38 次） |
| **CPU 增量** | **38 × ~2µs ≈ 76µs/s ≈ 0.008% 单核** |

**⇒ 远低于你的 3% 阈值，也远低于你估的 1~2%。**

### 为什么你的 1~2% 估高了
你把「每次唤醒开销 = AcquireNextFrame 系统调用 + Stopwatch 比较 + desktopChanged 判定」都算进去了 ——
但**等待期是内核阻塞，不计 CPU**。真正计 CPU 的只有「返回后那一小段用户态判定」，
而它在静态场景下几乎没有实际工作（几个 bool 比较）。

**你的估算在有帧返回时才对**：那种情况下每次要做 GPU 拷贝（≈0.1ms），是真开销。
但**静止时没有帧**，不存在这个开销；**动态时本来就每拍都在抓**，也不增。

### `loop=` 计数会变成什么样（确定性推算，供部署后对比）

| | 单次循环墙钟 | `loop=` 增量 |
|---|---|---|
| 修前（100ms） | 100(阻塞) + 5(IdleWait) = **105ms** | **≈9.5 次/秒** |
| 修后（16ms） | 16(阻塞) + 5(IdleWait) = **21ms** | **≈47.6 次/秒** |

**`loop=` 会涨约 5 倍，但 CPU 几乎不涨** —— 因为多出来的都是「阻塞后被唤醒、判定一下、再阻塞」。

---

## 2. 改动（file:line）

| 位置 | 改动 |
|---|---|
| `Relay/DxgiDdaCapture.cs:47-98` | 删除 `const int AcquireTimeoutMs = 100`，改为 `CurrentAcquireTimeoutMs()` + `SetTargetFps()` + `LastAcquireTimeoutMs` |
| `:335`（CPU 路径） | `uint acquireTimeout = CurrentAcquireTimeoutMs();` |
| `:427`（零拷贝路径） | `uint zeroTimeout = CurrentAcquireTimeoutMs();` ← **两处共用同一方法** |
| `Relay/SessionRecorder.cs:793-797` | 每拍下发 `SetTargetFps(fps)` 与 `InputTriggeredFrame = urgentInput` |
| `:1075 附近` | 日志加 `acqMs=`（部署后可直接验证超时是否跟随 fps） |

### 超时取值逻辑
```csharp
int frameIntervalMs = 1000 / Math.Max(1, _fps);
int ms = Math.Clamp(frameIntervalMs, 8, 20);      // 下限 8 / 上限 20
if (InputTriggeredFrame) ms = Math.Max(4, ms / 2); // urgent 再减半
```

| fps | 帧间隔 | 常规超时 | urgent 超时 |
|---|---|---|---|
| 30 | 33ms | **20**（clamp 上限） | 10 |
| 60 | 16ms | **16** | 8 |
| 120 | 8ms | **8**（clamp 下限） | 4 |

**为什么 clamp 到 [8,20]（而不是直接用帧间隔）** —— 这是你提的「注释里那段分析」的落地：
· **下限 8ms**：若超时远小于帧间隔（如 5ms @60fps），多数轮次拿不到帧 → 被上层当静止跳过 → **帧率实际减半**。
  Windows 计时器精度也是 ~15.6ms（1 tick），再小没有实际收益。
· **上限 20ms**：fps=30 时间隔 33ms，若给满 33ms，静止桌面每拍白等 33ms，循环太钝。取 20 是折中。
  （fps=30 时 20ms < 33ms 间隔，理论上仍可能丢帧 —— 但 fps=30 本来帧率需求低，可接受。）

---

## 3. 你要求核实的「有没有别处硬引用 100ms 常量」

**已全仓检查：没有。**
```
DxgiDdaCapture.cs:88   private uint CurrentAcquireTimeoutMs()      <- 定义
DxgiDdaCapture.cs:335  uint acquireTimeout = CurrentAcquireTimeoutMs();  <- CPU 路径
DxgiDdaCapture.cs:427  uint zeroTimeout = CurrentAcquireTimeoutMs();     <- 零拷贝路径
```
旧的 `AcquireTimeoutMs` 常量已**完全删除**（不是留着不用），避免有人再误用。
两处抓屏**共用同一个方法**，不会出现「CPU 路径快了但零拷贝还慢」的隐蔽不一致。

---

## 4. 必须验证不破坏（逐条）

| 项 | 状态 | 依据 |
|---|---|---|
| **动态画面仍能拿到新帧** | ✅ | fps=60 时超时 16ms = 一个节拍，该拍的新帧在超时内可取；拿到即**立即返回**（≈0.1ms GPU 拷贝），不会真的等满 |
| **静止省 CPU** | ✅ 未动 | `_skippedStaticFrames++` 路径原样（L1005），`IdleWait()` 原样 |
| **保活 250ms** | ✅ 未动 | `KeepaliveIntervalMs` 声明与 `_keepaliveNextAt` 计算均未改 |
| **零拷贝路径** | ✅ 同步改 | `:427` 用同一 `CurrentAcquireTimeoutMs()` |
| **`InputTriggeredFrame` 语义** | ✅ 保留 | 仍只由 `urgentInput`（up/click/releaseall）置位，move 不受影响 → 拖动不会退化 |

---

## 5. 验证

| 项 | 结果 |
|---|---|
| `dotnet build -c Release` | **0 error** |
| 旧常量彻底移除 | 全仓 grep 确认无残留引用 |
| 两处抓屏共用同一方法 | L335 / L427 均调用 `CurrentAcquireTimeoutMs()` |
| 静止省 CPU / 保活 / 零拷贝门控 | 逐条 grep 确认未改动 |
| `acqMs=` 埋点 | 已加入日志行，部署后可直接验证 |

### ⚠️ 未能取得的部分（如实说明）

| 验收项 | 状态 | 原因 |
|---|---|---|
| `captureMs` 修前修后**实际分布** | **未取** | 任务要求**不部署**，A2 上跑的是旧 agent；且我 60 秒采样期间 A1/A4 的 agent 进程已退出，无法读 `TotalProcessorTime` |
| `loop=` 实测增量 | **未取** | 同上（新代码未部署） |
| CPU 实测增量 | **部分** | 只有微基准（判定开销 <0.001µs、系统调用 ~2µs）+ 确定性推算；**没有真实 agent 的 CPU 曲线** |

**我能给的确定性结论**：
· 最坏等待 **115ms → 24ms**（这是超时常量 100→16、urgent 15→8 的直接算术结果）；
· CPU 增量 **< 0.01% 单核**（阻塞等待不计 CPU，只是唤醒次数增多）。

**部署后请按这两条验证**：
1. 日志 `captureMs=` 分布应从「5/15/100」变成「5~20」；同时 `acqMs=` 应等于 16（fps=60）或 8（urgent）；
2. `loop=` 应从 ~9.5/秒 升到 ~47/秒，而 agent CPU（任务管理器或 `TotalProcessorTime`）应**几乎不变**。

---

## 6. 边界自查

改了 `Relay/DxgiDdaCapture.cs`（超时动态化）、`Relay/SessionRecorder.cs`（下发 fps + 埋点）。
**未部署**；未主动发输入；未碰 `dist\stable`；未发布、未碰 COS、未 git commit、未弹窗；未动 A1/A4 进程。

---

**文档作者**：yomori-scout
**最后更新**：2026-11-24