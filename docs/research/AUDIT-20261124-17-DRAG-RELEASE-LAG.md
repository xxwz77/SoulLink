# 拖动流畅但松手后落位迟钝：输入注入 vs 画面回传定位

> 日期：2026-11-24　作者：input-chain-analyst（dsh-team `team_mwu35l8pae8oj5` / task `task_mwu7vzilmu9p6i`）
> 只读审计，未改任何代码；文末 diff 为建议稿，**未应用**。
> 证据标记：【代码事实】【实测-本次】(A2 %TEMP%\soullink-relay-27656.log)【推断】。

## 0. 结论（一句话）

**迟钝主要发生在『输入注入』段，不是画面回传**：松手事件（up）要走一条**无优先级的单 FIFO 命名管道通道**（RelayClient.cs:31-34,531-580），前面排着位移流残留请求与低优先级状态指令（recordstatus 4s 超时、shot 15s、save 60s），而 agent 端 `ServePipe` 是**严格串行**执行（RelayAgent.cs:702-746）——任何一条慢指令都会把 up 堵在其后；画面回传段实测队列 queueAge=0（见 §4），不背锅。

## 1. 控制通道 FIFO：无优先级分流（【代码事实】）

- 单通道：`Channel.CreateBounded<RelayRequest>(256)`，`SingleReader = true`（Services/RelayClient.cs:31-34）；唯一消费者 `ProcessRequestsAsync` 逐条 write→readLine 串行（:531-580）。**没有输入/控制两条 FIFO**。
- 所有命令共用：move/moverel、down/up、recordstatus（RelayClient.cs:707-708，timeout 4000）、shot（:835-836，timeout 15000）、save（:826-832，timeout 60000）。
- agent 端串行：`ServePipe` 单线程 while 循环逐条 `Execute(req)` 后才读下一条（Relay/RelayAgent.cs:707-746）。慢指令执行期间（shot 的 JPEG 抓屏+落盘 `CaptureOnce`，SessionRecorder.cs:1484-1500；save 更长），后续 up 只能等。
- 周期性堵塞源：设置页后端状态查询 `RecordStatusAsync`（Controls/RdpGeneralSettings.xaml.cs:112，进入设置页即查询）；FlowRunner 自动化 `ScreenshotAsync`（Services/FlowRunner.cs:565）。D10（AUDIT-20261124-07-DEFECT-LEDGER.md §2）早已登记同根因。

## 2. 松手（up）的注入路径与最坏等待

两条用户路径：

**A. 桌面控制台（捕获/自由视角拖动）**：`RdpConsole` 钩子 up → `LookQueue.EnqueueButton`（Controls/RdpConsole.cs:1713-1721）→ 队列线程 `UpAsync`（Services/LookInputQueue.cs:113）→ `SendAsync` 等 RTT。拖动中的 move 走 `LookRawNoReplyAsync`（:123，不等回复），**但仍在同一管道**；up 之前排队的 move 写入很快（无回复），真正威胁是**穿插的 recordstatus/shot**。

- up 自身延迟 = FIFO 排队 + 1 RTT（实测单条 45~64ms 口径，RelayClient.cs:603-606 注释）。
- **最坏等待估算**：前方有一条正在执行的 shot（agent 串行，JPEG 抓屏 100ms 级 + 文件落盘）或 recordstatus（StatusText 拼字符串，快；但若网络/管道抖动，客户端 timeout 4s 内会一直占着"下一个回复"位置）→ up 被**阻塞 0.1~4s**【推断】；save 触发时最坏 60s（罕见）。正常无低优先级指令穿插时 up ≈ 50~70ms，用户不会感到"迟钝"。

**B. 网页拖动**：down/up/click 走前端 FIFO `sendInputOrdered`（WebPages.cs:1740-1771），单 in-flight 串行，move 是 noWait 不占 FIFO——**网页 FIFO 本身不含慢指令，up 排队 ≈ 1 RTT（LAN 3~5ms）**。但有一个**真实落位缺陷**：pointerup 把最终坐标随 up 发送（WebPages.cs:2347 `sendInputOrdered('up', p.nx, p.ny, ...)`），而 agent 端 `up` 分支**只执行 MouseButton，不读坐标**（RelayAgent.cs:872-874）；`ExecuteInput` 只给 `click` 设 `Move=1`（Relay/AgentWebServer.cs:639）。⇒ 最后一次 8ms 节流 move（WebPages.cs:2192）可能还没发/在途，up 先到 → **up 落在旧位置**，图标掉在松手点之前的位置——这正是"松手后落位迟钝/错位"的确定性机制之一【代码事实】。

## 3. 画面回传段（排除主责）

- 【实测-本次】A2 日志：queueDepthMax=1、queueAge=0（max 18ms 一次性）；推流事件驱动（SessionRecorder.cs:1284-1289）、每帧 Flush（AgentWebServer.cs:481-486）；前端 output 直画无 rAF 排队（web-index.html:1311-1330）。
- 回传延迟是**恒定**的（~90ms 账面 + 待查账外项，见 AUDIT-20261124-11），表现为"画面整体慢"而不是"松手瞬间才迟钝"。**与"拖动流畅、松手迟钝"的症状形状不符**【推断】。

## 4. 五条假设逐条判定

| # | 假设 | 判定 | 依据 |
|---|---|---|---|
| H1 | up 被单 FIFO 前面的慢指令堵住 | **成立（主因，桌面控制台路径）** | RelayClient.cs:31-34,531-580 无优先级；agent 串行 RelayAgent.cs:707-746；recordstatus/shot/save 同通道 |
| H2 | up 携带的最终坐标被丢弃，up 落旧位 | **成立（网页路径确定性 bug）** | WebPages.cs:2347 发坐标；AgentWebServer.cs:639 只 click 设 Move=1；RelayAgent.cs:872-874 up 不定位 |
| H3 | 画面回传队列积压导致迟钝 | **不成立** | 实测 queueAge=0、深度 1；事件驱动推流 |
| H4 | 8ms move 节流把"最后一格位移"吞掉 | **部分成立**：节流窗口内最后一次 move 可能被 up 抢先（move 走 noWait 旁路，无与 up 的 happens-before） | WebPages.cs:2192-2216（move 旁路）vs 1740-1771（FIFO 串行）|
| H5 | RDP 原生通道（非捕获态拖动）本身慢 | 不成立 | 非捕获态事件直接走 RDP 控件原生通道，不经 FIFO（RdpConsole.cs:1637 起 CaptureOnClick 分支）；若用户在非捕获态也迟钝，则该假设复活 |

**RDP 对照实验（5 分钟）**：同一会话先在**非捕获态**（点击画面进入捕获前）拖动图标——此时输入走 RDP 原生通道；再捕获后拖动。若非捕获态不迟钝、捕获态迟钝 → 坐实注入 FIFO；若两者都迟钝 → 转向画面回传/远端系统（可再用 A1 不装 SoulLink 的原版 RDP（mstsc）拖动对照）。辅助：抓取 `SOULLINK_INPUT_TRACE=1` 时间戳（Services/InputTrace.cs），对比 up 的 AGENT_RECV_CMD 时刻 vs 本机 pointerup 时刻。

## 5. 修复建议（unified diff，未应用）

```diff
--- a/Relay/AgentWebServer.cs
+++ b/Relay/AgentWebServer.cs
@@ // ExecuteInput: up/down 也支持坐标定位（与 click 一致）
-            Move = (cmd == "click" && hasX) ? 1 : 0,
+            Move = ((cmd == "click" || cmd == "up" || cmd == "down") && hasX) ? 1 : 0,

--- a/Relay/RelayAgent.cs
+++ b/Relay/RelayAgent.cs
@@ // case "up"/"down": 先定位再按键（req.Move==1 时）
                 case "down":
+                    if (req.Move != 0) SetCursorPos(req.X, req.Y);
                     MouseButton(req.Button, true);
                     return new Response { ... };

                 case "up":
+                    if (req.Move != 0) SetCursorPos(req.X, req.Y);
                     MouseButton(req.Button, false);
                     return new Response { ... };

--- a/Services/RelayClient.cs
+++ b/Services/RelayClient.cs
@@ // 拆双通道：输入(Bounded 256) / 控制(状态查询) 各自单写者；Down/Up/Move 走输入通道
-        private readonly Channel<RelayRequest> _requests = Channel.CreateBounded<RelayRequest>(
-            new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
+        private readonly Channel<RelayRequest> _inputRequests = Channel.CreateBounded<RelayRequest>(
+            new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
+        private readonly Channel<RelayRequest> _ctrlRequests = Channel.CreateBounded<RelayRequest>(
+            new BoundedChannelOptions(16) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
+        // SendAsync 增加 priority 参数：输入命令写 _inputRequests，recordstatus/shot/save 写 _ctrlRequests；
+        // 两个消费者各占一条 Task，管道写侧仍需按序（输入优先轮询）。
```
注：管道写侧是单 `_writer`，双通道消费者必须加锁序轮询（先排空输入通道再写控制），否则 write/readLine 仍会错位——这是 diff 中最需要设计评审的一点，建议先落"up/down 定位 + shot 异步化（agent 端把 CaptureOnce 移出 ServePipe 线程）"两个小步。

## 6. 证据索引

RelayClient.cs:31-34（单 FIFO）、:474-493（SendAsync）、:506-508（NoReply 同通道说明）、:603-606（单条 45~64ms 实测口径）、:707-708/:826-836（recordstatus/shot/save 超时）；RelayAgent.cs:702-746（串行 ServePipe）、:872-874（up 不定位）、:1024-1038（shot 同步执行）、:1484-1500(SessionRecorder.CaptureOnce)；AgentWebServer.cs:639（仅 click Move=1）；LookInputQueue.cs:36/109-125（按钮等 RTT、move NoReply）；RdpConsole.cs:1699-1727（捕获态按钮注入）；WebPages.cs:2192-2216（move 节流旁路）、:2347（up 带坐标）；RdpGeneralSettings.xaml.cs:112（状态查询）；FlowRunner.cs:565（自动化截图）。实测：%TEMP%\soullink-relay-27656.log（queueAge=0）。

## 7. 边界自查

未改任何产品代码；未执行 git 命令；未碰 dist\stable；未杀进程；唯一写入本文档与 README 登记行。
