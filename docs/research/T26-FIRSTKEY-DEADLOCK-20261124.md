# T26 永久黑屏：`firstKey` 死锁（客户端只等 IDR，服务端只发 delta）

> 用户实测证据：控制台反复 `[H264] decode() 返回 undefined（解码器状态: configured），丢弃本 AU`
> 用户关键判断：「**输出操作会触发关键帧，之前有做这个修复，但是没有松开这个触发**」

---

## 0. 一句话：**服务端在发 delta，客户端只收 IDR，双方永远对不上**

```
客户端 web-index.html:1536
  if (!firstKey && !isKey && !isSps && !isPps) continue;   // 首 key 前丢光所有 delta
  if (isKey) firstKey = true;

服务端
  NotifyInputActivity() -> 抓一帧 -> 编出 **P 帧 (delta)**
  RequestKeyFrame()     -> _forceKeyFrame -> NVENC -> 编出 **IDR (key)**
```

输入路径（`AgentWebServer.cs` 原 :673）**只调用了前者**。于是：

| 时序 | 服务端 | 客户端 | 结果 |
|---|---|---|---|
| 用户松手 | `NotifyInputActivity(true)` → 抓帧 → **delta** | `firstKey=false` → **丢弃** | 画面不动 |
| 用户点击 | 同上 → **delta** | 仍 `firstKey=false` → **丢弃** | 画面不动 |
| 等 HTTP 首帧循环的 500ms nudge 凑效 | 终于发 IDR | `firstKey=true` | **画面才动** |

**⇒ 在 `firstKey=false` 的窗口里，所有输入触发都是无效的** —— 这正是用户说的「松开这个触发没做」。

---

## 1. 为什么以前能用、现在不行（时间线分析，任务要求）

这个问题**不是一直存在**，它有明确的前置条件。三条路径叠在一起才暴露：

| # | 时间点 | 发生了什么 | 为什么当时没坏 |
|---|---|---|---|
| ① | **首次连接**（页面刚打开、编码器刚创建） | NVENC **启动第一帧必是 IDR** | `firstKey` 立刻变 true → 后续 delta 全部正常接收 |
| ② | **agent 重启后页面重连** | 编码器**已在跑**，下一帧大概率不是 IDR（GOP 中途） | ← **这里是分水岭**：起点不再是 IDR |
| ③ | **页面刷新/切后台回来**（流重订阅） | 同上，且客户端 `firstKey` 被重置为 false | 死锁成立 |

**两个放大因素**：

1. **t25 之后**：`decode()` 返回 undefined 的分支现在会打日志（原本静默），所以这个问题**变得可见**了 ——
   但它在 t25 之前**同样存在**，只是没人看见日志。
2. **无限 GOP**：NVENC 只按需发 IDR（`RequestKeyFrame`），没有周期性 GOP 边界。
   所以一旦错过起点，**没有任何一次自然的 IDR 会把客户端救回来** —— 必须主动请求。

**合起来**：
```
首次连接 = 撞上 IDR = firstKey true = 一直好用
重启/重连 = 撞上 P 帧 = firstKey 永远 false = 永久黑屏
且：没有任何代码路径会在「从未拿到首帧」时去请求 IDR
```

---

## 2. 修复：四处后端 + 三处前端

### 2.1 后端（`Relay/`）

| # | 位置 | 改动 | 解决什么 |
|---|---|---|---|
| 1 | `SessionRecorder.cs:804` | `if (forceKeyFrame || zChanged || inputTrigger)` | 零拷贝分支不再吞掉输入触发 |
| 2 | `:985` | `&& !inputTrigger` | CPU 分支静止跳过不再吞掉输入触发 |
| 3 | `:934` | 抓屏空转时 `_inputActivityPending = true` 重新武装 | 输入触发不会因抓屏失败而丢失 |
| 4 | `AgentWebServer.cs:699` | `if (urgent) SessionRecorder.RequestKeyFrame();` | **松手/点击/释放同时升级为 IDR** ← 用户指出的缺口 |

### 2.2 第 4 处的节流设计（任务要求「你自己定阈值并说明理由」）

**新增** `RequestKeyFrame()` 进程内节流 **200ms**，另开 `RequestKeyFrameUnthrottled()` 给已有自己限流的调用方。

**为什么必须加**：
· `RequestKeyFrame()` 原本只置位，**零节流**；
· HTTP `POST /api/idr` 有 300ms 限流，但**输入路径直接调用会绕过它**；
· 拖动结束/连点时 urgent 可达 10Hz+，每帧强制 IDR → IDR 体积是 P 帧数倍、NVENC 每帧重建 GOP；
· 代码里原话（`IdrMinIntervalMs` 注释）：「Moonlight 教训：狂请求会拥塞崩溃」。

**为什么取 200ms**：
| 考虑 | 结论 |
|---|---|
| 要 > 客户端 500ms？ | 不必 —— 那是**客户端**发的，与本进程内路径独立计数 |
| 要 > 服务端 HTTP 300ms？ | 也不必 —— 两条路径独立，取小一点更稳 |
| **取 200ms 的理由** | urgent 动作（松手/点击）是人手级别（≤5Hz，即 ≥200ms 间隔），200ms 恰好覆盖正常操作不丢，同时压掉脚本连点/拖拽尾部抖动 |
| 与既有节拍的关系 | 与保活 250ms 同量级，不引入比现有更密的 IDR |

**稳态**：最多 5 次 IDR/秒，且**只在真有 urgent 输入时**；静止时一次都不发。

**同时修正了两处旧调用点**（否则会被新节流静默吞掉）：
| 调用点 | 改为 | 理由 |
|---|---|---|
| `/api/idr`（`:551`） | `RequestKeyFrameUnthrottled()` | 它自己已有 300ms 限流；若叠加 200ms 会出现「HTTP 返回 ok=true 但实际没置位」的谎言 |
| 首帧等待 500ms nudge（`:460`） | `RequestKeyFrameUnthrottled()` | 它自己已有 500ms 闸门，同样不能叠加 |

### 2.3 前端（`docs/web-index.html`，已同步到 3 处）

| # | 改动 | 解决什么 |
|---|---|---|
| 5 | `firstKey===false` 时每 2s 主动 `requestIdr()`，最多 5 次 | 首 key 未到时**主动要**，不再干等 |
| 6 | `decode()` 返回 undefined 时（首次）触发 `requestIdr()` | 这条路径**既不抛异常也不触发 decoder.error**，原先是死路 |
| 7 | 看门狗加「从未出过画面」分支：5s 补发 IDR、10s 断流重连 | 原看门狗有 `gotFrame &&` 前置 → 首帧不来时**根本不启用** |

**保留未动**：`requestIdr` 的 500ms 客户端限流、服务端 300ms 限流、现有 10s 看门狗（已出画面后）、静止省 CPU、保活 250ms。

---

## 3. 验证

| 项 | 结果 |
|---|---|
| `dotnet build -c Release` | **0 error** |
| 前端 JS 语法（95,551 字符整块 `new Function()` 解析） | **通过** |
| 后端 4 处修复存在性 | 逐条 grep 确认（L804 / L985 / L934 / L699） |
| 前端 3 处修复存在性 | `firstKeyWaitTimer` / `decodeErrors === 1) requestIdr()` / `if (!gotFrame)` 全部命中 |
| 三处同步（docs / WebPages.cs / dist） | 全部 `firstKeyTimer=5 decP-idr=1 noFrameWdog=2` |

### ⚠️ 未能取得运行时数据（如实说明）

| 验收项 | 状态 | 原因 |
|---|---|---|
| 修前 `up` 后 `firstKey` 仍 false | **未取** | 任务禁止主动发输入（move/down/up/click/key）；且要求**不要部署**，新代码尚未在 A2 生效 |
| 修后 ≤1 帧内 `firstKey` 变 true | **未取** | 同上 |
| `decode()` 返回 undefined 次数前后对比 | **未取** | 客户端 console 日志在**用户的浏览器**里，我无法读取 |

**结论层证据（确定性，非概率）**：
· 修前：客户端在 `firstKey=false` 时 `:1536` **必然** `continue` 丢弃 delta；
  而唯一会请求 IDR 的 `recoverViaIdr()` 只由 `decoder.error`/连续失败触发，
  `decode()` 返回 undefined **不触发它** → **没有任何路径能救**。这是死锁，不是偶发。
· 修后：`up` → `RequestKeyFrame()` → 服务端发 IDR → 客户端 `isKey` → `firstKey=true`。

**建议由用户验证（我不发输入）**：
1. 部署后 Ctrl+F5 强制刷新；
2. 打开 console，看是否还刷 `decode() 返回 undefined`；
3. 拖动图标松手，看是否立即落位；
4. 若仍卡，把 console 前 30 行贴给我 —— 新增的 `[H264] 首关键帧未到…主动请求 IDR 第 N 次` 会直接告诉我们卡在哪一环。

---

## 4. 边界自查

改了 **2 个源文件**：`Relay/SessionRecorder.cs`（第①②③处 + 节流）、`Relay/AgentWebServer.cs`（第④处 + 两处旧调用点改 Unthrottled）、
+`docs/web-index.html`（第⑤⑥⑦处）+ 自动生成的 `Services/WebPages.cs` / `dist/web-index.html`。

**未部署**（按要求，由 Lead 统一部署）；**未主动发任何输入**；未碰 `dist\stable`、未发布、未碰 COS、未 git commit、未弹窗、未用 A1/A4。

---

**文档作者**：yomori-scout
**最后更新**：2026-11-24