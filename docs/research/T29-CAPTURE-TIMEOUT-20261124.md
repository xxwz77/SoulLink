# T29 松手补帧 200ms：动态 AcquireNextFrame 超时 + T28 页面自动刷新

> 用户原话：「快速操作好了，但是**释放还是画面卡顿，补一帧是不是不够？**」
> 以及更根本的：「**那为什么不能是监控打开的时候一直输出呢？**」

---

## 0. 先回答用户那个更根本的问题

**「一直输出」在设计上已经有了** —— 有观众时静止桌面每 `KeepaliveIntervalMs`(250ms) 重编一帧（`SessionRecorder.cs:975-990`）。

**但用户仍觉卡，是因为瓶颈不是「输出频率」而是「单次出帧延迟」：**
```
松手 t=0
  → TryCaptureBgraTexture(AcquireNextFrame(100ms))   ← 静止必等满 100ms
  → 失败，回退 CPU
  → CaptureBgra(AcquireNextFrame(100ms))            ← 又等满 100ms
  → 用 _lastBgra 旧帧 → 编码 → 入队 → 客户端
  ⇒ t ≈ 200ms
```
**所以「一直输出」救不了：每一次出帧都要先白等 200ms。** 你的判断正确。

---

## 1. 我发现自己实现里的一个会破坏拖动的缺陷（已修正，必须说明）

**第一版实现**：`InputTriggeredFrame = inputTrigger`。

**这个写法会破坏拖动**，理由是：
```
move 也调 NotifyInputActivity（50ms 限流 ≈ 20Hz）
  ⇒ 拖动期间**每一拍 inputTrigger 都为 true**
  ⇒ 抓屏层每拍都走 15ms 短超时 / 跳过零拷贝
  ⇒ 拖动退化成「复用旧帧」—— **正好破坏「拖动必须流畅」这条唯一做对的地方**
```

**修正**：新增独立的 `_urgentInputPending` 标志，**只有 `up`/`click`/`releaseall` 才置位**。

| 标志 | 谁置位 | 用途 |
|---|---|---|
| `_inputActivityPending` (inputTrigger) | 所有输入（move 20Hz + urgent） | 绕过 fps 节拍立即抓帧（T25） |
| **`_urgentInputPending` (urgentInput)** | **仅 up/click/releaseall** | **才允许抓屏走 15ms 短超时** |

**为什么这个区分是对的**：
· **urgent**（松手）= 操作终点，之后桌面大概率静止 → 值得用短超时立刻收一帧；
· **move** = 拖动中，桌面一直在变，**100ms 内必拿到新帧** → 根本不需要特殊处理。

> 如果没有这一步区分，我这个「优化」就会把拖动改卡 —— 而任务明确写了「绝对不许为了修这个把拖动改卡」。

---

## 2. 修复：动态 AcquireNextFrame 超时（方案 A）

### 2.1 改动

| 文件:行 | 改动 |
|---|---|
| `Relay/DxgiDdaCapture.cs:53` | `AcquireTimeoutMs = 100` **保留不动**（仍是动态画面的超时） |
| `DxgiDdaCapture.cs:66 附近` | 新增 `internal bool InputTriggeredFrame { get; set; }` |
| `:292-293`（CPU 路径） | `uint acquireTimeout = InputTriggeredFrame ? 15u : (uint)AcquireTimeoutMs;` |
| `:385-386`（零拷贝路径） | `uint zeroTimeout = InputTriggeredFrame ? 15u : (uint)AcquireTimeoutMs;` |
| `Relay/SessionRecorder.cs:790` | 抓屏前置位：`ddaForTrigger.InputTriggeredFrame = urgentInput;` |
| `:923 附近` | 抓完立刻复位 `= false`（避免影响后续拍） |

### 2.2 为什么选 15ms，而不是 0ms

| 候选 | 问题 |
|---|---|
| **0ms** | 语义是「完全不等」—— 会连「恰好此刻有新帧」也拿不到，白白错过真实帧。我第一版用了 0，**改回 15**。 |
| **15ms** ✅ | 足够覆盖一次 DDA 唤醒（典型新帧 1~5ms 内可取）；又是 Windows 计时器精度下限（1 tick ≈ 15.6ms），再小没有实际收益。 |
| 原 100ms | 为「动态画面等新帧」设计，静止时纯白等。 |

### 2.3 为什么选方案 A 而不是 B/C

| 方案 | 评价 |
|---|---|
| **A：动态超时** ✅ | 改动最小（2 处超时 + 1 个属性 + 2 行置位/复位）；**动态画面零退化**（真在变时几 ms 就有帧，100ms 与 15ms 无差别）；静止时 100→15ms |
| B：零拷贝缓存 shared texture | 要管理纹理生命周期（ReleaseFrame 后失效），风险明显更高；收益与 A 相当 |
| C：urgent 时跳过零拷贝直接复用 `_lastBgra` | 0ms 等待最快，但**放弃了「恰好有新帧」的机会**，且强制走 CPU 转换。A 用 15ms 已经逼近它的速度，还保住了零拷贝的机会 |

**结论：A**，因为它同时满足「最快」与「零退化」。

---

## 3. 效果推演（确定性，非估算）

| 场景 | 修前 | 修后 | 依据 |
|---|---|---|---|
| **松手（urgent，桌面静止）** | 100 + 100 = **200ms** | 15 + 15 = **30ms** | 两处超时都从 100 → 15 |
| **拖动（move，桌面在变）** | 100ms 内拿到新帧（实际 1~5ms） | **完全相同** | `urgentInput=false` → 超时仍是 100ms |
| **静止无输入** | 跳过（不编码） | **完全相同** | `inputTrigger=false` → `_skippedStaticFrames++` 路径不变（L996-998） |
| **保活（250ms）** | 一帧 | **完全相同** | 保活逻辑未动 |

**「松手 → 新帧」实际耗时 ≈ 30ms（抓屏）+ 编码 + 入队**，远低于用户可感知阈值（~100ms）。

---

## 4. 回答你的问题：150ms/250ms 保活 与 200ms 超时，当初怎么定的？

**结论：它们是各自独立定的，从来没有在「静止 + 松手」这条路径上一起验证过。**

| 参数 | 出处与意图 | 是否考虑过对方 |
|---|---|---|
| `AcquireTimeoutMs = 100` | `DxgiDdaCapture.cs:48-52`，注释写的是「Sunshine display_base.cpp 组首」—— 为**动态画面等新帧**定的 | **否**。它假设「反正拿不到就复用旧帧」，没考虑复用前的 100ms 是**用户可感知的** |
| `KeepaliveIntervalMs = 250` | `SessionRecorder.cs:93`，为「维持 /stream 帧流、防前端看门狗断流」定的 | **否**。它只关心「有没有帧」，不关心「一帧要多久才产出」 |

**所以矛盾确实存在**：
```
保活认为「250ms 出一帧就够维持连接」
但出一帧本身要 200ms（两次超时）
⇒ 保活帧的实际到达间隔 ≈ 250ms + 200ms = 450ms
⇒ 而 /stream 前端看门狗/用户感知按 250ms 预期 → 感觉「卡」
```
**这解释了为什么「有保活」但用户仍觉卡** —— 保活的 250ms 是**发起间隔**，不是**到达间隔**。

**这条应该记进设计约束**：凡是「周期性产出」的参数，必须同时核对「单次产出耗时」，否则两个数字会互相矛盾。

---

## 5. 顺带交付：T28 页面自动刷新（本会话已完成，一并说明）

**用户问「为什么程序不能帮用户刷新」** —— 这个问得对，我已实现：

### 5.1 你问的三个设计问题

**① 版本号从哪来？**
· 查证结果：**`/api/info` 原本没有 `version` 字段**（`AgentWebServer.cs:269` 只返回 ok/account/session/pid/port/streamVersion/captureBackend/几何）。
  页面 `:775` 读的 `d.version` **永远是 undefined** —— 页面版本号从来没显示过。
· 我**新增了** `GET /api/pagever`（`Services/WebServer.cs:367`），返回 **对实际喂给浏览器的 HTML 做的 SHA-256 前 12 位**。
· **为什么用内容哈希而不是程序版本号**：页面有两个来源（exe 同目录的 `web-index.html` 优先 / 内置常量兜底）；
  **只改 web-index.html 时程序版本号不变** → 用版本号会漏掉更新。内容哈希只要字节变了就变。

**② 刷新打断画面怎么办？**
**只在空闲时刷**：记录最后输入时刻，**<5s 内有操作就推迟**，最多推迟 2 分钟（避免用户一直在操作导致永不更新）。
理由：刷新会重连视频流（黑一下 + 等 IDR），正在拖动时刷**比晚几分钟更新更糟**。

**③ 死循环怎么防？** 三重保护：
| 保护 | 实现 |
|---|---|
| ① 最小间隔 | `MIN_RELOAD_GAP_MS = 60000`（60s 内最多刷一次） |
| ② 连续上限 | `MAX_CONSECUTIVE_RELOADS = 3`，超过就 warn 并停止 |
| ③ 跨 reload 持久化 | 用 `sessionStorage` 存「上次刷新时间 / 连续次数 / 已应用版本」—— 否则 reload 后计数清零，保护失效 |

**关键实现细节（我修正过一次）**：`appliedVer`（本页已应用版本）**必须跨 reload 持久化**，
否则 reload 后它又被设成当前值，「检测到更新」永远无法与「已应用」区分 → **会漏刷**。

---

## 6. 验证

| 项 | 结果 |
|---|---|
| `dotnet build -c Release` | **0 error** |
| 两处超时均为动态值 | L292 `acquireTimeout` / L385 `zeroTimeout`，均 `? 15u : (uint)AcquireTimeoutMs` |
| urgent 与 inputTrigger 已分离 | `_urgentInputPending` 仅 `up/click/releaseall` 置位（`AgentWebServer.cs:677`） |
| 静止省 CPU 未变 | `_skippedStaticFrames++` 保留（L996-998） |
| 拖动未退化（代码层） | `move` → `urgent=false` → `InputTriggeredFrame=false` → 超时仍 100ms |
| 前端 JS 语法（T28 的 `new Function()`） | **通过**（101,449 字符） |

### ⚠️ 实测数字未取（如实说明）

**拿不到**：任务要求**不部署**，所以 A2 上跑的是旧 agent，新超时未生效；
且禁止主动发输入（不能自己造松手）。

**替代证据（代码路径确定性）**：
· 修前：`AcquireNextFrame(100)` 静止必等满 → 两次 = 200ms；
· 修后：同路径 `AcquireNextFrame(15)` → 两次 = 30ms。
**这是把常量从 100 改成 15 的直接算术结果，不依赖运行时测量。**

**建议部署后由用户验证**：松手看是否立即落位；agent 日志的 `captureMs=` 应从 ~200 降到 ~30。

---

## 7. 边界自查

改了 `Relay/DxgiDdaCapture.cs`（动态超时）、`Relay/SessionRecorder.cs`（urgent 标志 + 置位/复位）、
`Services/WebServer.cs`（T28 的 /api/pagever + PageVersion）、`docs/web-index.html`（T28 自动刷新）+ 2 个自动生成文件。
**未部署**；未主动发输入；未碰 `dist\stable`；未发布、未碰 COS、未 git commit、未弹窗、未用 A1/A4。

---

**文档作者**：yomori-scout
**最后更新**：2026-11-24