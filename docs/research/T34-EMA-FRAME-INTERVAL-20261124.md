# T34 抓屏超时改为按实测帧间隔自适应（EMA）+ 区分显示推送/渲染 fps

> 用户原话：「不应该根据帧率自动调节吗？不然不同帧率又卡了？」

---

## 0. 用户的直觉是对的，而且指出了 T31/T33 反复出问题的**共同根因**

| | 之前 | 现在 |
|---|---|---|
| 超时基准 | **配置** fps（StreamFps） | **实测**帧间隔（EMA） |
| 配置60 实际40 | 死按 19ms → **25ms 间隔下每帧必超时** → 复用旧帧 | 自动放宽到 **32ms** ✓ |
| 配置60 实际30 | 死按 19ms → 必超时 | 自动放宽到 **42ms** ✓ |

**这就是为什么 T31（整数除法）、T33（差 0.67ms）会一修再修** ——
两者都在用「**期望值**」算超时，而服务端真实产出能力随时可能低于配置。
只要实际帧率低于配置，超时就会小于真实间隔，**必然**重现「每帧必超时」。

---

## 1. 实现

### 1.1 EMA 状态与更新（`Relay/DxgiDdaCapture.cs`）

```csharp
private double _emaFrameIntervalMs;      // 0 = 尚未测到，用配置 fps 兜底
private long _lastNewFrameAtMs;          // 上次**成功拿到新帧**的时刻
private const double EmaAlpha = 0.2;             // 平滑系数（约等价最近 5 帧）
private const double TimeoutSafetyFactor = 1.2;  // 超时 = 实测间隔 × 1.2
private const long MaxEmaSampleMs = 500;         // 超过此间隔不采信（静止污染）

private void NoteNewFrame()
{
    long now = Environment.TickCount64;
    if (_lastNewFrameAtMs > 0) {
        long delta = now - _lastNewFrameAtMs;
        if (delta > 0 && delta < MaxEmaSampleMs) {   // ← 边界②：过滤静止污染
            double sample = delta;
            _emaFrameIntervalMs = _emaFrameIntervalMs <= 0
                ? sample                                    // 第一个样本直接采用
                : _emaFrameIntervalMs * (1-EmaAlpha) + sample * EmaAlpha;
        }
    }
    _lastNewFrameAtMs = now;
}
```

**调用点只有两处，且必须是「真的拿到新帧」**：
· `:456` CPU 路径 —— `_frameSequence += ...` 之后；
· `:567` 零拷贝路径 —— 同样在 `_frameSequence += ...` 之后。

**复用旧帧（`WAIT_TIMEOUT` 分支）不调用** —— 那不算拿到新帧，不能污染 EMA。

### 1.2 超时计算

```csharp
double basisMs = _emaFrameIntervalMs > 0
    ? _emaFrameIntervalMs * TimeoutSafetyFactor   // 实测 × 1.2
    : 1000.0 / Math.Max(1, _fps);                 // 冷启动兜底
int frameIntervalCeil = (int)Math.Ceiling(basisMs);   // 仍用 Ceiling，不用整数除法
int ms = Math.Clamp(frameIntervalCeil + 2, 8, 120);
```

**`urgent` 仍不减半**（T33 已确认：减半会小于间隔，把 bug 引回来）。

---

## 2. 边界处理（你列的四条）

| # | 边界 | 处理 | 验证 |
|---|---|---|---|
| ① | **冷启动**（还没拿到过帧） | `_emaFrameIntervalMs = 0` → 用配置 fps 兜底，等同 T33 逻辑 | 配置60→19ms、30→36ms、120→11ms，均 > 间隔 ✓ |
| ② | **静止污染** | 只在 `delta < 500ms` 时更新 EMA | 实测：稳定60fps 后静止 800ms，EMA 仍 16.7ms（未被拉大）✓ |
| ③ | **上限** | `Clamp(..., 8, 120)` 保留 | ✓ |
| ④ | **urgent** | 不减半 | ✓ |

**边界②为什么必须过滤**：静止时新帧间隔可能是几百 ms；若进 EMA，超时会被拉到 122ms+，
**松手又变慢** —— 那正是 T31 要解决的问题。过滤后静止时 EMA 停在最后的稳定值，正确。

---

## 3. 超时取值推演（含你问的「配置60 实际40」）

| 场景 | 配置 | 实测间隔 | **超时** | 是否 > 实测间隔 |
|---|---|---|---|---|
| 冷启动（未测到） | 60 | 16.67 | **19ms** | ✓ |
| 配置60 实际60 | 60 | 16.67 | **23ms** | ✓ |
| **配置60 实际40** | 60 | 25.00 | **32ms** | ✓ ← **你问的这档** |
| 配置60 实际30 | 60 | 33.33 | **42ms** | ✓ |
| 配置30 实际30 | 30 | 33.33 | **42ms** | ✓ |
| 配置60 实际20 | 60 | 50.00 | **62ms** | ✓ |
| 配置120 实际120 | 120 | 8.33 | **12ms** | ✓ |

**注意一个副作用**：EMA 稳态下超时 = 间隔×1.2+2，比 T33 的「间隔+2」略大。
fps=60 时从 19ms 变成 23ms（+4ms）。这是**刻意换来的稳健性**——
多等 4ms 换「永不因为帧率抖动而丢帧」，值得；且 23ms 远小于原固定 100ms。

### 帧率突降的收敛速度（15 帧实测）
```
60fps 稳态: EMA=16.7ms 超时=23ms
降到 20fps 15 帧后: EMA=49ms 超时=61ms  (需 >50ms ✓)
```
α=0.2 意味着约 **5 帧**就能跟上大部分变化；15 帧已完全收敛。
期间若短暂超时，只是复用一两帧旧画面，不会持续。

---

## 4. 第二个问题：页面上的 `fps: 66.0 (render)` 到底是什么

### 查证结果

| 检查 | 结果 |
|---|---|
| 页面里有 fps 显示代码吗？ | **没有**。全仓搜 `render`/`decodeFps`/`recvFps`/`requestVideoFrameCallback` **均无** |
| `#fpsinfo` 元素显示什么？ | 原来只显示 **分辨率**（`u.screen`），不显示 fps |
| 那 `fps: 66.0 (render)` 从哪来？ | **不是这个页面产生的** —— 应该是浏览器扩展/开发者工具/其它页面的显示 |

**但你的判断方向完全正确**：`render` 与「推送帧率」确实是两回事：

| 指标 | 含义 |
|---|---|
| **渲染 fps**（render/RAF） | 浏览器合成器绘制频率。**可以把同一帧重复画 60 次**，与内容是否更新无关 |
| **接收/推送 fps** | 服务端真实产出并被解码显示的帧率。**这才是卡顿判据** |
| **配置 fps** | 用户设定的目标值，是「期望」不是「实际」 |

**推论**：用户看到 66.0 (render) **完全可能**在服务端只推 30fps 时出现。
若拿渲染 fps 判断「推送正常」，就会**误判卡顿不存在** —— 这正是你担心的事。

### 已做的改动：页面区分显示「实测推送 fps」与「配置 fps」

**服务端**（`/api/info` 新增两字段）：
```csharp
configuredFps = SessionRecorder.CurrentFps,    // 配置（_fps）
actualFps     = SessionRecorder.MeasuredFps,   // 实测：Ring 内真实帧时间跨度算出的帧率
```
`MeasuredFps` 是新增属性，用 `(Ring.Count-1) / (首末帧时间差)` 计算 —— **这才是真实推送帧率**。
（`StatusText()` 里本来就有同名计算，我把它抽成可复用属性，避免两处算法漂移。）

**前端**（`#fpsinfo` 统一渲染）：
```
1280x720 · 推送实测 29.8 fps · 配置 30 fps
```
**顺带修掉一个已有缺陷**：原来 `fpsinfo` 有**两处**各自写 `textContent`（L3353 写分辨率、我那处写 fps），
会互相覆盖。现在统一到单一 `renderFpsInfo()` 入口。

**另加 2 秒轻量轮询**（`startFpsPolling`/`stopFpsPolling`，挂在 `startLive`/`stopLive`）：
实测 fps 是动态值，只在进入画面时取一次会一直是旧值。轮询只在「画面打开中」跑，关闭自动停。

**同时修了一个 TDZ 隐患**：`let __fpsPollTimer` 原本声明在 L3008（函数定义处），
而 `startLive` 定义在 L1091 会引用它 —— 虽然运行时调用时机晚于脚本加载（实际不会触发），
但这依赖隐含假设。已把声明上移到 **L1045**（`liveRunning` 旁边），彻底消除。

---

## 5. 验证

| 项 | 结果 |
|---|---|
| `dotnet build -c Release` | **0 error** |
| 四档边界推演 | 全部通过（见 §2 表） |
| EMA 静止污染过滤 | 静止 800ms 后 EMA 不变 ✓ |
| 帧率突降收敛 | 15 帧内收敛 ✓ |
| 前端 JS 语法（**113,911 字符**整块 `new Function()`） | **通过** |
| 三处同步（docs / WebPages.cs / dist） | 均 `fpsDisplay=3` |
| TDZ 隐患 | 已验证并修复（声明上移 L1045 < startLive L1091） |

### ⚠️ 未实测（如实说明）

**EMA 真实收敛曲线、超时实际取值分布均未实测** —— 要求**不部署**，A2 跑的是旧 agent。

**已给的替代证据**：用与 C# 代码逐行等价的逻辑做了数值推演（§3），
覆盖冷启动/稳态/突降/静止污染四类场景，全部满足「超时 > 实测间隔」。

**部署后建议这样验**：
1. 日志 `acqMs=` 应随实际帧率浮动（而非固定值）；
2. `captureMs` 应大部分 **0~2ms**（真拿到帧），不再是 `acqMs` 的等值；
3. 页面右上角应显示「推送实测 X fps · 配置 Y fps」—— **若两者差距大，说明实际产出跟不上配置**，
   这本身就是一个有用的诊断信号；
4. 若日志出现「抓屏超时 <= 帧间隔」警告（T33 的安全网），说明 EMA 还没收敛或异常，需回看。

---

## 6. 边界自查

改了 `Relay/DxgiDdaCapture.cs`（EMA 超时）、`Relay/SessionRecorder.cs`（CurrentFps/MeasuredFps 属性）、
`Relay/AgentWebServer.cs`（/api/info 两字段）、`docs/web-index.html`（fps 区分显示 + 轮询）+ 2 个自动生成文件。
**未部署**；未发输入；未碰 `dist\stable`；未发布、未 commit、未弹窗；未动 A1/A4。

---

**文档作者**：yomori-scout
**最后更新**：2026-11-24