# T32 落点偏移回归：WS 永久降级 HTTP 导致输入失序

---

## 0. 三条 P0 全部完成

| # | 修复 | 状态 |
|---|---|---|
| P0-1 | WS 失败不再永久降级，指数退避重连 | ✅ |
| P0-2 | HTTP 降级下 move 进串行队列保序 | ✅ |
| P0-3 | `dropPendingMove` 清队列里待发的 move（不碰 in-flight） | ✅ |
| 附 | `/api/pagever` 端口错误（T28 从未生效） | ✅ **确实是个 bug，已修** |

---

## 1. 根因（确认成立）

```
某次 WS 握手失败（agent 重启 / 网络抖动）
  → wsInputFailed = true，getWsInput() 直接 return null
  → **本次会话内永不恢复**
  → 全部输入走 HTTP：
      · move → sendInputNoWait → 独立 fetch（并发，不等回包）
      · up   → sendInputOrdered → pumpInput → 单 in-flight 串行 fetch
  ⇒ 两条独立 HTTP 请求，**浏览器/网络不保证谁先到**
  ⇒ up(P3) 先到、move(P1) 后到
  ⇒ 服务端先松手，再把光标拽回 P1 = **落点偏移**
```

**用户看到 `wsInputFailed = true` 正好印证这条链。**

---

## 2. P0-1：失败不再永久降级（指数退避）

### 改动（`docs/web-index.html`）
```javascript
let wsInputFailed = false;   // 当前是否处于失败退避状态
let wsRetryAt = 0;           // 下次允许尝试重建的时刻
let wsRetryDelay = 500;      // 退避间隔：500→1s→2s→…→30s 封顶

function markWsFailed() {
  wsInputFailed = true;
  wsRetryAt = Date.now() + wsRetryDelay;
  wsRetryDelay = Math.min(wsRetryDelay * 2, 30000);
}
function markWsHealthy() {   // onopen 时调用 → 退出降级，清零退避
  wsInputFailed = false; wsRetryAt = 0; wsRetryDelay = 500;
}
```

`getWsInput()` 的闸门改为：
```javascript
if (wsInputFailed) {
  if (Date.now() < wsRetryAt) return null;   // 退避期内：继续用 HTTP
  wsInputFailed = false;                     // 到期：放行一次重建尝试
}
ws.onopen  = () => { markWsHealthy(); pumpWs(); };
ws.onerror = () => { markWsFailed(); ... };
ws.onclose = () => { ... if (wasCurrent && !wsInputFailed) markWsFailed(); };
```

**`onclose` 的处理要小心**：`closeWsInput()`（主动关闭，如切账号）会先把 `wsInput` 置 null，
所以用 `wasCurrent` 区分「异常断开」与「主动关闭」，不会误判。

**效果**：最坏情况下 30 秒后自动重连成功，回到 WS 通道；**不再需要用户刷新页面**。

---

## 3. P0-2：HTTP 降级下 move 保序

```javascript
const ws = getWsInput();
if (ws && ws.readyState === WebSocket.OPEN) {
  postInputRaw(body).then(...);   // WS：保持原样，性能不变
  return;
}
// HTTP 降级：进串行队列保序（单 in-flight），但不等回包
inputQueue.push({ body, resolve: function(){}, fireAndForget: true, httpOnly: true });
pumpInput();
```

### ★ 我自己发现并修掉的一个竞态（不在你的清单里）

`postInputRaw()` **内部也会判断 WS**。于是有这条竞态链：
```
① sendInputNoWait 判 WS 未就绪 → 决定「入队走 HTTP」
② 入队后、pumpInput 真正发送前，WS 恰好连上（P0-1 的退避重连会带来这种情况）
③ postInputRaw 内部再判一次 → 发现 WS 可用 → **改走 wsSend**
⇒ 「入队保序」的意图被绕过，行为不可预测；若同一条 body 被别的路径也发，还会重复注入
```
**修法**：入队时带 `httpOnly: true`，`postInputRaw(body, httpOnly)` 据此**跳过 WS 判断**，
`pumpInput` 两处调用（首发 + 重试）都透传 `item.httpOnly`。这样传输方式在入队时就被冻结，可预测。

### 另外两处配套
· **move 不重试**：`if (resp == null && !item.fireAndForget && INPUT_RETRYABLE.has(...))` ——
  重发过时位置只会让光标再跳一次；且 noWait 语义本就是「发出去不管」。
· **move 失败不刷屏**：HTTP 降级下每 8ms 一条 move，失败 warn 会把 console 冲爆、掩盖真问题。

**代价（明确说明）**：HTTP 模式下 move 变慢（串行）。但这是该付的代价 ——
用户明确表示「落点偏移比 move 慢严重得多」；且 WS 一恢复就自动回到快速直发。

---

## 4. P0-3：清队列里的待发 move

```javascript
function dropPendingMove() {
  if (pumpMove._t) { clearTimeout(pumpMove._t); pumpMove._t = null; }
  movePending = null;
  inputQueue = inputQueue.filter(function (it) {
    return !(it && it.body && it.body.action === 'move');
  });
}
```

### 为什么**不会**碰到 in-flight 那一条（你特别提醒的点）
```
pumpInput():  const item = inputQueue.shift();   ← 先出队
              inputSending = true;               ← 再标记在途
⇒ 在途项**已经不在 inputQueue 里**
⇒ filter 操作天然不可能碰到它
```
所以 **resolve 链不会被破坏** —— up 的重试判定、光标回显都正常。

`inputQueue` 声明为 `let`（L1995），可重新赋值，filter 写法成立。

---

## 5. 附：`/api/pagever` 端口错误 —— 你说得对，这是个真 bug

**你的判断完全正确**：
· `/api/pagever` 定义在 **主程序** `Services/WebServer.cs:367`（19080）；
· agent（19081）**没有**这个端点 → 返回 404；
· 而 T28 的轮询调的是 `directBase(cur)` = **19081**
  ⇒ **T28 的页面自动刷新从未生效过**。

**修法：改用页面同源（相对路径）**
```javascript
// 原来：agent 直连口（错误）
const base = directBase(cur) || '';
await fetch(base + '/api/pagever', { headers });

// 现在：页面自身来源（正确）
await fetch('/api/pagever', { cache: 'no-store', credentials: 'same-origin' });
```

**为什么页面来源才对**：版本轮询要回答的是「**这个页面**该不该刷新」，
而页面本身由主程序 19080 发出。页面里其它管理类接口（`/api/sessions`、`/api/settings`、`/api/list`）
**全都是相对路径**（= 主程序），只有画面流/输入类才用 `directBase`（agent）。`pagever` 属于前者。

**口令**：主程序 WebServer 用 **cookie 鉴权**（`IsAuthorized`），同源请求自动带 cookie；
不需要 `X-SoulLink-Token`（那是 **agent** 校验的）。所以原来的 `headers` 也一并去掉，改 `credentials: 'same-origin'`。

---

## 6. 验收：顺序保证的模拟验证

用等价状态机模拟（同样的并发/串行语义、随机网络延迟），每组跑 8 次：

### 修前（move 并发 fetch + up 串行）
```
试 1: [move P2, move P3, move P4, up, move P5, move P1]  ← up 之后还有 move ✗
试 2: [move P1, move P2, move P3, move P5, move P4, up]  ✓
试 3: [move P4, move P1, move P2, move P3, move P5, up]  ✓
试 4: [move P2, move P5, move P3, move P1, up, move P4]  ← up 之后还有 move ✗
⇒ 8 次里有 3 次出现「up 之后还有 move」= 落点偏移
```

### 修后（move 与 up 同一条串行队列）
```
试 1~8 全部：[move P1, move P2, move P3, move P4, move P5, up]
⇒ 8 次里 0 次违规，up 恒为最后一条
```

**这与用户「操作太快落点飘逸」的体感一致**：越快 → move 越密集 → 并发乱序概率越高。

---

## 7. 其余验证

| 项 | 结果 |
|---|---|
| 前端 JS 语法（**111,985 字符**整块 `new Function()`） | **通过** |
| 三处同步（docs / WebPages.cs / dist） | 均 `markWsFailed=4 httpOnly=1 filter=1 pagever=1` |
| `dotnet build -c Release` | **0 error** |
| WS 恢复后自动切回 | 逻辑上由 `onopen → markWsHealthy()` 保证；退避封顶 30s，最迟 30s 内恢复 |
| in-flight 不被误清 | 由 `shift()` 先出队的结构保证（见 §4） |

### ⚠️ 未做的实测（如实说明）

**HTTP 降级下的真实到达顺序、WS 恢复的真实耗时，均未实测** ——
原因：① 要求**不部署**，A2 跑的是旧页面；② 我不主动发输入（会操控用户键鼠）。
顺序保证已用等价状态机验证（§6），这是逻辑层证据；真实网络下的表现需部署后由用户确认。

**部署后建议这样验**：浏览器 console 里应看到
· `[WS] 输入通道已恢复走 WebSocket（退出 HTTP 降级）` —— 说明 P0-1 生效；
· 不再出现 `wsInputFailed = true` 的长期挂起。

---

## 8. 边界自查

只改 `docs/web-index.html`（+ 自动生成的 `Services/WebPages.cs` / `dist/web-index.html`）。
**未部署**；**未发任何输入**；未碰 `dist\stable`；未发布、未碰 COS、未 git commit、未弹窗；未动 A1/A4。

---

**文档作者**：yomori-scout
**最后更新**：2026-11-24