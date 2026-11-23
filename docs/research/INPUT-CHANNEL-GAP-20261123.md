# 输入链路专项审计 · up 丢失根因 + 触控/鼠标流畅度（2026-11-23）

> 审计员：input-auditor（input 链路）｜团队：latency-gap-audit
> 只读审计，未改任何产品代码/脚本/配置。唯一产物是本文件。
> 证据标注：**【事实】**=工作区源码逐行可证；**【推断】**=由代码逻辑推导；**【待验证】**=需真机/实测确认；**【已有实测】**=本仓库前序文档实测口径。
> 上游交接：`docs/research/HANDOFF-VIDEO-LATENCY-DONE-20261123.md` P0.5（用户 18:10 反馈「鼠标放开了远端没放开」+「缩小浏览器/拖动内容很卡」）。

---

## 0. 一句话结论

网页输入链路（`docs/web-index.html` → `Relay/AgentWebServer.cs` /api/input → `RelayAgent.Execute`）有**三条真实缺口**共同导致「up 丢失/粘键 + 拖动卡」：

1. **down/up 是两条并发 HTTP POST，无顺序保证、无重试** —— 拖动路径里 `down`（web-index.html:1549）与 `up`（:1601）是两次独立 fetch，代理端每个请求 `Task.Run` 并发处理（AgentWebServer.cs:129），**up 可能先于 down 执行**，远端按键停在按下态。这是「按钮松开远端没松开」的**首要根因链**。
2. **网页断开/切后台不释放按键** —— `visibilitychange`（web-index.html:1882）只断流不 `releaseall`，全文无 `beforeunload/pagehide/blur` 释放；而代理端 `ReleaseAllMouseButtons/ReleaseAllKeys` 只在**命名管道断开/进程退出**时触发（RelayAgent.cs:694-695、588、624-625），**网页 HTTP 断开不会触发**。刷新/切 tab 时按住的键会永久粘住。
3. **move 单 in-flight + 等 HTTP 回包** —— 拖动是 RTT 绑定的（pumpMove 里 `moveInFlight` 要等 fetch 返回 + r.json 完成，web-index.html:1466-1483）。局域网 1~3ms 没问题，但手机/WAN（RTT 50~200ms）下实际移动刷新率退化为 1/RTT ≈ 5~20Hz，表现为「拖动很卡」。

这三条里，#1/#2 直接对应「up 丢失」，#3 对应「拖动/缩小很卡」。

---

## 1. 输入链路全景（当前实现）

~~~
浏览器(web-index.html)
  ├─ 触屏/鼠标 pointerdown/pointermove/pointerup/wheel/keydown
  ├─ sendInput() → fetch POST {action,x,y,button,dx,dy,delta,key}   [web-index.html:1279-1294]
  │     直连代理端口 directBase(cur)+'/api/input'  [web-index.html:1284-1291]
  ▼
代理进程内 HttpListener(AgentWebServer)
  ├─ LoopAsync 每条请求 Task.Run 并发处理（无串行锁）          [AgentWebServer.cs:122-130]
  ├─ HandleAsync → /api/input → HandleInputAsync                  [AgentWebServer.cs:294-298, 471-532]
  ├─ 坐标 0~1 → 实际像素 px/py = round(x*w)                     [AgentWebServer.cs:483-486]
  └─ RelayAgent.Execute(req) 同步执行（同进程、不走命名管道）     [AgentWebServer.cs:525]
  ▼
RelayAgent.Execute
  ├─ move  → SetCursorPos                                    [RelayAgent.cs:741-752]
  ├─ moverel → mouse_event(MOUSEEVENTF_MOVE,dx,dy)           [RelayAgent.cs:755-783]
  ├─ click → (Move?SetCursorPos) + down + Sleep(18) + up      [RelayAgent.cs:785-800]
  ├─ down/up → MouseButton → mouse_event(LEFT/RIGHT/… DOWN/UP) [RelayAgent.cs:803-809, 269-286]
  └─ keydown/keyup → SendInput(KEYEVENTF_KEYUP)               [RelayAgent.cs:843-860]
~~~

> **【事实】重要分界**：网页 `/api/input` 由代理**进程内同步执行**（AgentWebServer.cs:525 `RelayAgent.Execute(req)`），**不经过宿主命名管道**。因此「45~64ms/条的串行 RTT」（前序文档 `LOOK-OPTIMIZE` 说的桌面管道往返）**不适用于网页路径**。网页路径的 RTT 只有 HTTP 往返（局域网 ~1~3ms）。这一区别直接影响 §4 延迟成本估算的定性。

---

## 2. 逐环节对照表（环节｜当前实现｜成熟做法｜差距与影响）

| # | 环节 | 当前实现（证据） | 成熟做法（Moonlight/Sunshine/Parsec） | 差距与影响 |
|---|---|---|---|---|
| 1 | 输入与画面解耦 | 独立 `POST /api/input` 直连代理，与 /stream 分离【事实，AgentWebServer.cs:294-298】 | 控制通道与视频通道分离（Sunshine `NV_REL_MOUSE_MOVE_PACKET`，见 MATURE-LATENCY-PLAYBOOK §4.1） | ✅ 方向正确，无差距 |
| 2 | down/up 配对与顺序 | 拖动路径 `down` 与 `up` 是两次**独立 fetch**，无队列、无序号、无等待【事实，web-index.html:1549/1601】；代理每请求并发 Task.Run【事实，AgentWebServer.cs:129】 | Sunshine/Moonlight 输入包走**单条有序控制通道**，天然保序；按钮状态在服务端有状态机 | ❌ **up 可能先于 down 到，远端粘键**（根因链见 §3） |
| 3 | 失败重试/ack | `sendInput` 用 try/catch 吞掉所有异常直接 return null【事实，web-index.html:1293】，**无重试、无确认** | 成熟项目控制包带序号，丢包/失序可检测重发 | ❌ 网络抖动时 up 静默丢失，无自愈 |
| 4 | 断线释放（防粘键） | 代理只在**管道断开/进程退出**释放【事实，RelayAgent.cs:694-695/588/624-625】；网页 `visibilitychange` 只 `abortStream` 不释放【事实，web-index.html:1882-1894】，无 beforeunload/pagehide/blur | 客户端断开即发「全键释放」，服务端带按键超时看门狗 | ❌ 网页断线/刷新/切后台时按住键永久粘住 |
| 5 | move 节流/合帧 | `MOVE_MIN_INTERVAL_MS=8` + 单 in-flight + latest-wins【事实，web-index.html:1456-1483】 | Moonlight 合并同一帧内多位移一次发（xrel+=）；上报率与刷新率解耦 | ⚠️ 思想正确，但**单 in-flight 要等回包** → RTT 绑定（见 §6） |
| 6 | moverel（转视角） | `lookDelta` 8ms 合并累积 → `moverel`【事实，web-index.html:1371-1394】 | Moonlight 相对位移合并累加直发（OSS-REMOTE-RESEARCH §2.2） | ✅ 已对齐，无差距 |
| 7 | 触屏坐标换算 | `norm()` 用 canvas 分辨率 + letterbox 偏移，0~1 归一化【事实，web-index.html:1296-1310】；代理 `round(x*GetSystemMetrics)`【事实，AgentWebServer.cs:483-486】 | 归一化 0~1 或逻辑分辨率锚点（VIRTUAL-CONTROLS-RESEARCH §5） | ✅ 正确；仅有 DPI/多显示器非主屏的**待验证**边界（§8） |
| 8 | click 原子性 | 单条 `click` 命令内 down+Sleep(18)+up【事实，RelayAgent.cs:791-796】 | — | ✅ 单击路径无 reorder 风险（对比 #2 拖动路径） |
| 9 | 桌面端（本地 RDP）对照 | 按键走 `LookInputQueue` **有界 FIFO**，down/up 保序且逐条 await 回复；位移走 `SendAsyncNoReply` 单向【事实，LookInputQueue.cs:60-114、SessionInput.cs:99-105】 | — | ✅ 桌面端比网页端**更安全**：down/up 有队列保序，网页端没有（关键差异） |

> 桌面端（`RdpConsole`）与网页端（`web-index.html`）是**两套独立实现**：桌面端按键经过 `LookInputQueue` 的有界 FIFO（`_buttons` 队列按序 dequeue + await，LookInputQueue.cs:90-114）保证 down/up 顺序；网页端则是「每个 pointer 事件一次裸 fetch」，没有任何队列。**同一产品的两条输入路径存在实现漂移**——这是本次「up 丢失」只发生在网页端、桌面端相对稳定的结构性解释。

---

## 3. up 丢失根因链（证据行号）

### 根因链 A：down/up 并发乱序（首要，【推断】由并发模型推导）

~~~
1. 拖动超过 8px：pointermove 里发 'down'（独立 fetch，不 await）   web-index.html:1543-1549
2. 期间 0~n 条 'move'（单 in-flight，各自独立 fetch）              web-index.html:1461-1483
3. 抬手：pointerup 里发 'up'（独立 fetch，不 await）               web-index.html:1601
4. 代理 LoopAsync 对每条请求 Task.Run 并发、无顺序锁               AgentWebServer.cs:122-130
5. HandleInputAsync → RelayAgent.Execute 同步改按钮状态            AgentWebServer.cs:525 → RelayAgent.cs:803-809
   ⇒ down 与 up 的到达顺序不受网络/线程调度保证
~~~

- **【事实】** 浏览器对同一 origin 的并发 fetch 可开多条连接（每域上限 6），down/up 可能并行在两条连接上；代理侧 `_ = Task.Run(() => HandleSafe(ctx))` 无任何串行化。
- **【推断】** 若 `up` 请求先被线程池调度执行（`MouseButton(up)` 后 `PressedButtons.TryRemove`），随后 `down` 才执行（`PressedButtons[button]=true`），则最终远端物理按键 = 按下态，且 `PressedButtons` 记录为按下 → **粘键直到 releaseall**。
- **触发场景**：快速甩动/快速点按、网络抖动、代理线程池繁忙（此时恰好 `staticSkipped` 异常高、代理在跑视频编码，见 HANDOFF P0.5）时概率上升。
- **【待验证】** 复现口径：代理日志 `soullink-relay-*.log` 中 down/up 到达顺序 + 前端 `sendInput` 打印请求时间戳，即可坐实乱序。

### 根因链 B：up 请求失败静默丢失（【事实】）

- `sendInput` 的 catch 直接 `return null`【web-index.html:1293】，调用方（pointerup/pointercancel）也**不检查返回值、不重发**【web-index.html:1601-1602, 1612, 1625】。
- `pointercancel` 虽补发 up【web-index.html:1604-1627】，但同样不校验成功与否；且 `pointercancel` 不能覆盖「页面隐藏/断网/浏览器被杀」等场景。

### 根因链 C：网页断线不触发释放（【事实】）

- 代理释放按键的唯一入口是：命名管道断开【RelayAgent.cs:694-695】、进程退出【:588, :624-625】、显式 `releaseall` 命令【:811-814】。
- 网页与代理之间是 **HTTP 直连**，网页断开**不触碰命名管道**，故代理不释放。
- 网页侧 `visibilitychange` 只 `abortStream()` 断流【web-index.html:1882-1887】，无 `releaseall`；全文无 `beforeunload`/`pagehide`/`blur` 释放逻辑（grep 确认）。

> **综合根因**：up 丢失 = 乱序（A）+ 静默失败（B）+ 断线不释放（C）三者叠加；即便 A 概率不高，B/C 也会在断线/刷新/弱网下必现。

---

## 4. 「一次点击的完整往返成本」估算

区分两个量：**端到端 453ms 是视频链路**（抓屏+编码+传输+解码，本审计不覆盖）；**输入链路**成本单独如下。

### 4.1 网页单击（LAN，直连代理）

| 阶段 | 成本 | 证据/口径 |
|---|---|---|
| pointerdown→pointerup（用户动作） | 不计入系统延迟 | — |
| fetch 发 'click'（Move=1 合并定位） | 网络 RTT 局域网 ~1~3ms | 【已有实测】MATURE-LATENCY-PLAYBOOK §5.2 传输 0.8ms |
| 代理反序列化 + Execute('click') | <1ms（SetCursorPos + mouse_event ×2） | 【推断】内存操作 + 同步 Win32 |
| **click 内固定 Sleep(18ms)**（down→up 间隔） | **18ms** | 【事实】RelayAgent.cs:794 |
| 回包 + fetch json | 并入 RTT | 【推断】 |

> **单击系统侧总成本 ≈ RTT + 18ms ≈ 局域网 ~20ms**；【待验证】手机/WAN 场景 RTT 50~200ms 时 ≈ 70~220ms，RTT 主导。
> 注：18ms 是 `click` 命令内部的**人工按住时长**（保证目标窗口识别为单击而非双击/无效），不是排队延迟。

### 4.2 网页拖动（LAN）与「卡」的量化

- 移动刷新率受**单 in-flight + 等回包**约束：`pumpMove` 里 `moveInFlight` 要等 `sendInput(...).finally(...)`，而 `sendInput` 内部 `await fetch + await r.json()`【事实，web-index.html:1288-1292, 1466-1483】。
- 因此**实际移动频率 = min(125Hz, 1/RTT)**：
  - 局域网 RTT 3ms → ~125Hz（节流 8ms 主导），顺滑。
  - 手机/WAN RTT 100ms → ~10Hz，**明显卡顿**。
- 【推断】「缩小浏览器/拖动内容很卡」在局域网若仍卡，则不是本链路，需查视频帧率/staticSkipped（HANDOFF P0.5 方向 3，属 viewer-auditor/agent-auditor 范围）；在 WAN/手机下才是本链路的 RTT 绑定问题。

### 4.3 与桌面端注入方式差异（关键对照）

| 维度 | 桌面端 RdpConsole（本地 RDP） | 网页端 web-index.html |
|---|---|---|
| 按键顺序 | `LookInputQueue._buttons` 有界 FIFO 保序，逐条 await 回复【事实，LookInputQueue.cs:60-114】 | 裸 fetch，并发无序【事实，web-index.html:1549/1601】 |
| 位移 | `SendAsyncNoReply` 单向，不等回复【事实，SessionInput.cs:99-105】 | 单 in-flight **等回包**【事实，web-index.html:1479-1482】 |
| 注入方式 | Raw Input 硬件级 → 相对位移 → 管道 → 代理 mouse_event【事实，RdpConsole.cs:1654-1690】 | 绝对 move(SetCursorPos) / moverel(mouse_event)【事实，AgentWebServer.cs:485-519】 |
| 断线释放 | 管道断开 → 代理释放【事实，RelayAgent.cs:694-695】 | HTTP 断开不触发释放（缺口 C） |

**结论**：桌面端「位移单向 + 按键有界保序」是对的；网页端把这两点都丢了——位移退化为等回包、按键退化为无序裸 fetch。改造方向就是把桌面端 `LookInputQueue` 的成熟模式**平移到网页端**（见 §9）。

---

## 5. down/up 配对性与失败重试缺失（细节）

1. **配对是「时间上相邻的两次独立请求」，不是协议层配对**【事实】：`down`/`up` 各自一个 JSON action，代理 `PressedButtons` 只是「是否按过」的记录【RelayAgent.cs:98-99, 284-285】，**不校验 down 与 up 的请求来源/序号/配对**。上游少发一个 up（B 类失败）代理无从得知。
2. **无序号/无 generation**：乱序或重复都不可检测。成熟做法（Sunshine 控制包带序列、客户端 ack）缺位。
3. **无重试**：`sendInput` 静默失败【web-index.html:1293】；即使想重试也没有「重发这个 up」的幂等语义兜底（但 up 天然幂等，重发无害，见 §9）。
4. **click 的原子性掩盖了问题**：单击走单条 `click`（down+up 在代理内同一线程先后执行，RelayAgent.cs:791-796）**不暴露乱序**；只有拖动（down/up 分离）才暴露——这解释了为什么「点一下正常、按住拖动后松手粘键」。

---

## 6. 移动事件节流/合帧现状

- **moverel（转视角）**：`lookDelta` 累计 dx/dy，`LOOK_HZ_MS=8` 合并窗口，`flushLook` 一次发【事实，web-index.html:1371-1394】。**与桌面 `LookInputQueue.WakeIntervalMs=8` 对齐**【事实，LookInputQueue.cs:36】。✅ 已达 Moonlight「合并累加直发」语义。
- **move（光标跟随/拖动）**：`MOVE_MIN_INTERVAL_MS=8` + latest-wins + 单 in-flight【事实，web-index.html:1456-1483】。latest-wins 丢中间位置是**正确**的（最新优先）；唯一缺陷是单 in-flight 等回包（§4.2）。
- **【已有实测】** 前序注释：60Hz 滑动 3s，RTT 3ms，旧版 15Hz→新版 125Hz【web-index.html:1454】。说明局域网下 move 已修到位。
- **【待验证】** 手机端 `pointermove` 事件频率（触摸 60~120Hz）+ 8ms 节流 + 单 in-flight 的实际手感，需真机。

---

## 7. 断线时按键状态释放（防粘键）现状与缺口

**已有（正确）**：
- 代理侧 `PressedButtons`/`PressedKeys` 只记录真正按过的键，释放时只抬按过的键（避免孤立 UP 弹右键菜单）【事实，RelayAgent.cs:97-109, 288-329】。
- `pointercancel` 补发 up【事实，web-index.html:1604-1627】。
- 宿主管道断开/进程退出 → `ReleaseAllMouseButtons + ReleaseAllKeys`【事实，RelayAgent.cs:694-695, 588, 624-625】。

**缺口（本次根因）**：
1. **网页 HTTP 断开不触发任何释放**（§3 根因 C）。
2. **网页切后台/刷新/关页无 `releaseall`**：`visibilitychange` 只断流【web-index.html:1882-1894】。
3. **无按键超时看门狗**：即使一切正常，若某条 up 丢失且无后续事件，粘键会持续到下次 releaseall/重投。
4. **keydown 无配对 keyup**：键盘只有 `keydown`→`key`（组合键瞬发，web-index.html:1646-1656），长按方向键靠 `keydown/keyup` 动作但网页端**只发了 keydown 没有对应 keyup 接线**——网页侧当前根本没用 `keydown`/`keyup` 动作（grep 仅命中协议定义，无前端调用）。若未来网页端接摇杆长按，需补 keyup + 断线释放。

---

## 8. 触控坐标换算与丢帧

- **坐标换算正确**【事实】：`norm()` 用解码帧真实分辨率 + letterbox 偏移 + 0~1 归一化（web-index.html:1296-1310）；代理按 `GetSystemMetrics` 实际分辨率换算（AgentWebServer.cs:483-486）。多显示器/非主屏、DPI 缩放下的映射正确性属**【待验证】**（当前按主屏 `SM_CXSCREEN` 换算，若远端会话主屏与画面不一致会偏移）。
- **丢帧**：`move` 的 latest-wins 是**有意的中间位置丢弃**（正确）；`moverel` 是累积合并（不丢位移）。真正可能「丢」的是：
  1. **up 丢失**（§3，非坐标问题）；
  2. 单 in-flight 期间的中间 move 被 latest-wins 覆盖（符合预期，非缺陷）。
- **触屏状态机单例风险**【推断】：绝对模式下 `look`/`drag` 是单例全局变量（web-index.html:1319, 1367）。双指同时落下时第二个 pointerdown 会覆盖第一个的 `look`，第一个手指抬起时因 `pointerId` 不匹配而走不进 up 分支 → **第一个手指若已进 drag 模式，其 down 永不 up**。默认绝对模式下多指操作可能粘键。相对模式（mouseMode==='relative'）用 `relPoints` Map 处理多指，无此问题【事实，web-index.html:1345-1354】。

---

## 9. 对照成熟项目输入通道设计 + 改造方案

### 9.1 成熟做法要点（引用既有调研，标注可信度）

| 项目 | 输入通道设计 | 可信度 |
|---|---|---|
| Sunshine | 输入走**独立控制通道**（`NV_REL_MOUSE_MOVE_PACKET` 等），不经视频编码；服务端按键状态机 | 【开源源码】MATURE-LATENCY-PLAYBOOK §4.1（file:line 已核） |
| Moonlight 客户端 | 同一帧内多鼠标事件**合并累加**一次发（`xrel += event->xrel`）；相对模式 early-return 不碰光标 | 【开源源码】OSS-REMOTE-RESEARCH §2.2 |
| Moonlight 上报频率 | 社区口径「5~10ms/上报」级 | 【行业共识·待验证】未独立复核具体 ms，前序文档以 125Hz 对齐 |
| Parsec | 驱动级输入 + 自研 BUD UDP 控制通道，输入与视频彻底分离 | 【引用·闭源】LATENCY-BENCHMARK §2 |

### 9.2 改造方案（按「先止血、后根治」排序，均未实施，仅建议）

> 方案只给方向和落点，**不动代码**（本审计硬边界）。

| 优先级 | 改造 | 落点 | 解决的问题 |
|---|---|---|---|
| **R1（止血，0.5 人日）** | 网页端把 down/up 改为**经同一发送队列串行**：复用现有 move 的「单 in-flight + 队列」思路，down/up/click 都进一个 FIFO 队列按序 await 发送 | web-index.html:1279-1294（新增 inputQueue） | 根因 A（乱序） |
| **R1（止血）** | `sendInput` 失败时对 **up 类动作重试 2~3 次**（up/release 幂等，重发无害）；down 失败也重试（down 幂等） | web-index.html:1293 | 根因 B（静默失败） |
| **R1（止血）** | 补 `beforeunload`/`pagehide`/`visibilitychange(hidden)` 里发 `releaseall`（fire-and-forget，用 `navigator.sendBeacon` 或同步 fetch） | web-index.html:1882 | 根因 C（断线不释放） |
| **R2（根治，1~2 人日）** | 代理端给 /api/input 加**按键超时看门狗**：`PressedButtons/PressedKeys` 超过 N ms（如 2s）未收到任何输入 → 自动 `ReleaseAll` | RelayAgent.cs:98-109（新起 TickHub/定时任务，遵循 AGENTS.md §性能约束） | 兜底粘键（即使前端全挂） |
| **R2（根治）** | move 改为**不等待回包**：`sendInput` 加 `noWait` 选项（fetch 不 await 响应体，或改用 `sendBeacon`/未来 WebSocket），只保证发送顺序 | web-index.html:1466-1483 | 拖动 RTT 绑定（根因 #3） |
| **R3（中期，2~4 人日）** | 输入通道升级 WebSocket（`HttpListenerContext.AcceptWebSocketAsync` 原生可用，STREAM-PERF-AUDIT §4.4d 已论证）或 DataChannel，双向 + 有序 + 免每事件 HTTP 头开销 | AgentWebServer.cs（新增 ws 路由） | 一劳永逸解决顺序/RTT/头开销 |
| **R3（中期）** | 网页端引入桌面端同款「按键有界 FIFO + 位移单向」模型（把 `LookInputQueue` 语义平移到 JS），消除两套实现漂移 | web-index.html 输入区 | 结构对齐 |

---

## 10. 按 ROI 排序的行动项

| 排序 | 行动项 | 收益 | 成本 | 风险 | 类型 |
|---|---|---|---|---|---|
| 1 | 网页 down/up 串行队列 + up 重试 | 直接消除「松手粘键」主因 | 0.5 人日 | 低 | 止血 |
| 2 | beforeunload/pagehide/visibilitychange → releaseall | 消除「刷新/切后台粘键」 | 0.5 人日 | 低 | 止血 |
| 3 | 代理按键超时看门狗 | 兜底所有粘键（含前端故障） | 0.5 人日 | 低（注意不新增 DispatcherTimer，走 TickHub） | 根治兜底 |
| 4 | move 不等回包（noWait） | 拖动顺滑，WAN 场景收益大 | 0.5 人日 | 中（需保序） | 根治 |
| 5 | 输入通道 WebSocket/DataChannel | 顺序+RTT+头开销一劳永逸 | 2~4 人日 | 中 | 中期 |
| 6 | 触屏多指状态机（绝对模式）改 Map | 修多指粘键边界 | 0.5 人日 | 低 | 边界 |

---

## 11. 事实 / 推断 / 待验证 汇总

**事实（源码逐行）**：
- down/up 是两次独立 fetch，无队列/序号/等待：web-index.html:1549、1601
- 代理每请求 Task.Run 并发、无串行锁：AgentWebServer.cs:122-130
- sendInput 异常静默 return null：web-index.html:1293
- 代理释放仅限管道断开/进程退出：RelayAgent.cs:694-695、588、624-625
- visibilitychange 只 abortStream 不 releaseall：web-index.html:1882-1894
- click 命令内 Sleep(18)：RelayAgent.cs:794
- move 单 in-flight 等 fetch+r.json：web-index.html:1466-1492
- 桌面端 LookInputQueue 按键 FIFO 保序：LookInputQueue.cs:60-114
- 桌面端位移 SendAsyncNoReply 单向：SessionInput.cs:99-105、RelayClient.cs:598-599

**推断（逻辑推导）**：
- up 先于 down 执行导致粘键（并发模型必然允许，但触发概率未实测）
- move 单 in-flight 在 WAN 下退化为 1/RTT Hz
- 绝对模式双指覆盖单例 look 导致第一指 up 丢失

**待验证（需真机/日志）**：
- down/up 实际乱序复现（代理日志时间戳 + 前端请求时间戳）
- 手机/WAN 下拖动手感与 move 频率
- 多显示器/DPI 下坐标映射偏移
- Moonlight「5~10ms 上报」具体数值（引用前序文档，未独立复核）

---

## 12. 与既有文档的关系

- `MATURE-LATENCY-PLAYBOOK-20261123.md §4` 曾断言「输入与画面解耦 + 高频合并」是「全项目少有的正确项」，**本审计修正该结论**：合帧/解耦确实正确，但**遗漏了 down/up 顺序、重试、断线释放三件事**——这三件正是 P0.5「up 丢失」的根因。§4 当时只看 move/moverel，没审 down/up/断线。
- 与 `MOBILE-CONTROLS-DESIGN`/`MOBILE-REMOTE-PLAN` 的「HTTP POST 串行 RTT」担忧一致，本审计进一步定位到**单 in-flight 等回包**是网页拖动卡的精确机制。
- `HANDOFF-VIDEO-LATENCY-DONE` P0.5 的「up 丢失」方向 1，本审计给出完整根因链与止血方案。
