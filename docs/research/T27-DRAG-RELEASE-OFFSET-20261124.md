# T27 拖动松手落点偏移：待发 move 拽回光标（2026-11-24）

> 用户原话：「这一轮修复引入新问题了，**点击选择文件拖动，操作太快鼠标落点会偏移**，松手的问题也没解决」

---

## 0. 先说一件必须澄清的事：**这不是本轮引入的**

| 核查 | 结果 |
|---|---|
| 我的改动是否碰过 `pumpMove` / `movePending`？ | **没有**（`git diff` 里这两个词 0 命中） |
| 这段代码在 HEAD 里本来就有？ | **是**（`git show HEAD:docs/web-index.html` 可查到同样的 `movePending` / `pumpMove` / `latest-wins`） |

**这个缺陷是既有的**，与 T24/T25/T26 的改动无关。我说这个不是为了推责，是因为**定位错了会继续引入新 bug** ——
如果误以为是「本轮引入」，就可能去回滚正确的修复，反而把死锁问题放回来。

---

## 1. 根因（确认成立）

`sendMoveThrottled` 是 **latest-wins + 8ms 节流**：`pointermove` 只把坐标记进 `movePending`，
节流窗口内**不立即发**，靠 `pumpMove._t` 定时器补发。

```javascript
// :2267
function sendMoveThrottled(nx, ny) {
  movePending = { nx, ny };   // 只记最新，旧的自然被覆盖
  pumpMove();                 // 8ms 窗口内 → 只排 timer，不发
}
```

### 时序表（修前，问题成立）

| 时刻 | 事件 | `movePending` | 定时器 | 实际发出 |
|---|---|---|---|---|
| t0 | `pointermove(P1)` | `P1` | 排 8ms | — |
| t1 | `pointerup(P2)` | `P1`（**没被清**） | 仍在 | `up(P2)` ← 光标到 P2 并松开 |
| t2 | timer 到期 | → null | 触发 | **`move(P1)`** ← ★ **光标被拽回 P1** |

**⇒ 终点变成 P1 而不是 P2 = 落点偏移。操作越快，节流窗口内越容易有存货，偏移越明显。** 与用户描述完全吻合。

---

## 2. 修复

### 2.1 新增统一辅助函数（`docs/web-index.html:2285`）

```javascript
// ★ T27：丢弃待发的旧坐标 —— 释放类动作前必须调用
function dropPendingMove() {
  if (pumpMove._t) { clearTimeout(pumpMove._t); pumpMove._t = null; }
  movePending = null;
}
```

**为什么抽成函数**：释放路径有 **4 条**，逐个内联重复既难维护、也**必然漏掉某一条**
（这正是「顺手检查 pointercancel」容易变成「只改了主路径」的原因）。

### 2.2 四处调用点（全部覆盖）

| # | 位置 | 路径 | 说明 |
|---|---|---|---|
| 1 | `:2440` | 主 pointerup（drag 且 moved） | 用户报的「拖动松手」主路径 |
| 2 | `:2410` | pointerup 的 `look.mode==='drag'` | 转视角拖动结束，同样是释放 |
| 3 | `:2453` | pointercancel 的 look-drag 分支 | 取消后不能再来一条迟到 move |
| 4 | `:2467` | pointercancel 的 drag.moved 分支 | 同上 |

> **顺序已核验**：主路径的 `dropPendingMove();` 位于 `sendInputOrdered('up', ...)` 的**前一行**（L2440 vs L2441）。
> 顺序必须是「先清 pending + 取消 timer，再发 up」—— 反了就无效。

### 2.3 时序表（修后）

| 时刻 | 事件 | `movePending` | 定时器 | 实际发出 |
|---|---|---|---|---|
| t0 | `pointermove(P1)` | `P1` | 排 8ms | — |
| t1 | `pointerup(P2)` | **`null`**（已被 dropPendingMove 清空） | **已 clearTimeout** | `up(P2)` |
| t2 | （timer 已取消，不触发） | — | — | **无迟到 move** ✅ |

**⇒ up 之后不再有任何 move 能改落点。**

### 2.4 未改 tap/click 路径（说明理由）

`pointerup` 的 tap 分支（`sendInputOrdered('click', ...)`）也发 click，但**不调用** `dropPendingMove` ——
因为 tap 的语义是「没动就抬起」（`!moved`），此时本来就没有拖动中的 movePending。
强行加进去无害，但会让代码产生「这里为什么需要」的疑问，所以保持语义清晰：**只修真正有 pending 的路径**。

---

## 3. 第二个问题：「松手的问题也没解决」—— 必须分开说

**我把两件事分开，不混在一起下结论**：

| 问题 | 状态 | 证据 |
|---|---|---|
| **落点偏移** | ✅ **本次已修，根因确定** | 代码路径确定性（见 §2.3 时序表）；前端语法验证通过 |
| **松手不落位** | ⚠️ **T26 修复可能未生效，或另有原因** | **我没有运行时证据** |

### 为什么说「可能未生效」—— 三个可验证的怀疑点

| # | 怀疑 | 怎么验证 |
|---|---|---|
| 1 | **浏览器还在跑旧页面** | 需要 **Ctrl+F5** 硬刷新。`dist/web-index.html` 已更新，但浏览器缓存可能仍是旧的 |
| 2 | **新 agent 没部署** | 按你要求我**没有部署**。`Program Files\SoulLinkDev\SoulLink.Agent.exe` 仍是 19:40，**t26 的后端第 4 处（urgent→IDR）不在里面** |
| 3 | **t26 修的不是这条** | 如果 `firstKey` 本来就是 true（画面能出），那松手不落位就**不是 firstKey 死锁**，而是帧供给/编码路径问题 |

### 我需要的证据（不猜）

请让用户在 **Ctrl+F5 之后**看一眼 console，把这几类行贴回来：

| 若看到 | 说明 |
|---|---|
| `[H264] 首关键帧未到…主动请求 IDR 第 N 次` | t26 前端修复生效了，但 IDR 一直没到 → 问题在服务端/传输 |
| `decode() 返回 undefined` 仍在刷 | 解码器状态问题，与 firstKey 无关 |
| 完全没有 H264 日志、画面正常 | 那「松手不落位」就是**帧供给**问题（t25 的 inputTrigger 路径） |
| `画面 10000ms 无新帧，自动重连` | 保活/编码停转 |

**在拿到这些日志之前，我不会对「松手不落位」下结论。**

---

## 4. 验证

| 项 | 结果 |
|---|---|
| 前端 JS 语法（**96,610 字符**整块 `new Function()` 解析） | **通过** |
| `dropPendingMove` 定义 + 4 处调用 | 全部确认（L2285 定义；L2410 / L2440 / L2453 / L2467） |
| 调用顺序（drop 在 up **之前**） | 已核验：L2440 在 L2441 之前 |
| 三处同步 | `docs` / `WebPages.cs` / `dist` 均 `dropPendingMove=5` |
| `dotnet build -c Release` | **0 error** |

---

## 5. 边界自查

只改 `docs/web-index.html` + 自动生成的 `Services/WebPages.cs` / `dist/web-index.html`。
**未部署**（按要求）；未主动发输入；未碰 `dist\stable`；未发布、未碰 COS、未 git commit、未弹窗、未用 A1/A4。
未改后端（本轮问题纯前端）；T26 的后端改动保持原样未动。

---

**文档作者**：yomori-scout
**最后更新**：2026-11-24