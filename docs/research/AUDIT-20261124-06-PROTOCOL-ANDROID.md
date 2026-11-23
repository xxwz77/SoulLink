# 协议层与 Android 端链路审计（AUDIT-20261124-06）

> 成员：pixelflow-android（协议层与 Android 端分析）
> 日期：2026-11-24
> 范围：SoulLink 自研串流协议（Relay/ 与 Android/ 两侧）、Android MediaCodec/SurfaceView 管线延迟、对照 Moonlight/Sunshine 等成熟设计的差距。
> 方法：只读源码 + 公开协议知识；行号以当前工作区为准。

---

## 一、结论摘要

1. **SoulLink 没有统一的串流协议，而是三条互不相通的信令/数据通道拼起来的**：①宿主↔代理的命名管道 JSON 行协议（控制）；②网页/安卓↔代理的 HTTP+chunked H.264（视频，无应用层帧头）；③网页/安卓↔主程序的 Cookie 登录+端口映射（信令）。视频与输入走**同一个 HTTP 服务器**，输入是**无序、无连接语义的独立 POST**。这与 Moonlight「视频/控制/信令三通道分离、视频带帧边界元数据、控制带序号+重传」的成熟模型差距最大。
2. **协议最大硬伤：视频流无任何帧边界元数据、无流内时间戳、无包序号**。所有 NAL 边界靠客户端逐字节扫描起始码猜出来，AU 时间戳是客户端自造的固定 33_333µs 步进（`H264StreamReader.kt:82`），不是服务端捕获时间。结果：客户端无法做 jitter buffer、无法测端到端延迟、无法做网络自适应（码率/帧率联动），丢帧只能靠「队列深度≥2 就丢」这种粗糙启发式（`H264Decoder.kt:73`）。
3. **输入通道是协议层卡顿的主根因**：每个输入动作 = 一条独立 HTTP POST（`InputSender.kt:64-69`），`down/up/click` 阻塞等回包保序（`InputSender.kt:44-51`），在 WAN 下每动作至少一个完整 RTT；`move/moverel` fire-and-forget 但**无序号、无会话概念**，服务端也无输入队列合并。代理端 `click` 注入自带 `Sleep(18)` + 双击间隔 `Sleep(90)`（`RelayAgent.cs:856-862`），加上服务端 `HttpListener` 每请求一个 Task 的调度，输入回环叠了 3 层人工延迟。成熟产品（Moonlight）输入走 UDP 不可靠通道 + 事件序号 + 服务端聚合，单事件 <1ms 处理。
4. **鉴权是「长期共享口令」模型，没有会话票据**：网页/安卓用 `X-SoulLink-Token`（或 `?token=` 查询串）永不过期的共享密钥（`AgentWebServer.cs:320-334`、`WebDirect.cs:56`），主程序登录也是 GET 查询串传密码（`SoulLinkApi.kt:54`）。token 出现在 URL 查询串里会进浏览器历史与代理日志（AgentWebServer.cs 注释自己也承认，L316-318）。Moonlight/Sunshine 用每次会话的 pair 阶段证书+challenge，RustDesk 用一次性连接密钥。
5. **Android 端 MediaCodec 管线有三个可修的延迟点**：①无 `KEY_MAX_INPUT_SIZE`/复用输入缓冲导致大 IDR 帧可能进不了 capacity 不足的 input buffer（`H264Decoder.kt:115` 直接丢弃该 AU）；②解码循环 `DEQUEUE_TIMEOUT_US=10ms` 的同步轮询 + 空转 `queueInputBuffer(0,0)` 空缓冲会人为引入最高 ~10ms 抖动（`H264Decoder.kt:109,124`）；③时间戳无意义（固定步进）导致 `releaseOutputBuffer(idx, true)` 走默认渲染时钟，编码端无 pacing 信息时解码端无从对齐。

---

## 二、证据（文件:行号）

### 2.1 协议拓扑：三条通道 + 对应关系

| 通道 | 路径 | 协议 | 鉴权 | 证据 |
|---|---|---|---|---|
| 控制（宿主→代理） | 命名管道 `SoulLink.Relay[<nonce>]` | JSON 行（Request/Response 一问一答，NoReply 例外） | 进程身份校验（PID+会话+提权+Admin），无加密 | `Services/RelayClient.cs:335-365`（服务端建管道）、`Relay/RelayAgent.cs:702-758`（ServePipe 逐行）、`RelayClient.cs:371-386`（VerifyAgentPipe） |
| 事件（代理→宿主） | 命名管道 `..._Events` | JSON 行，单向 | 同上 | `RelayClient.cs:392-429`、`RelayAgent.cs:623-630` |
| 视频+输入（网页/安卓→代理） | HTTP `:directPort` | `/stream`=chunked 裸 H.264 Annex-B；`/api/input`=独立 POST | 静态共享 token（头或查询串） | `Relay/AgentWebServer.cs:273-298`、`AgentWebServer.cs:320-334`、`Services/WebDirect.cs:56-72` |
| 信令（网页/安卓→主程序） | HTTP `:mainPort` | `/api/login?pw=`、`/api/sessions` | Cookie `soullink_sid` | `Android/.../net/SoulLinkApi.kt:30-83`、`Services/WebServer.cs:909-917`（directToken 下发） |

代理本身的指令分发（`/api/input` 复用 `RelayAgent.ExecuteWeb` → `Execute`）保证网页/安卓输入语义与桌面控制台一致：`AgentWebServer.cs:470-539`、`RelayAgent.cs:764-1108`。

### 2.2 控制协议（管道）现状

- Request 结构：`RelayAgent.cs:509-547`。字段 `Cmd/X/Y/Dx/Dy/Delta/Button/Count/Hotkey/Trace/...` + `Move`（先定位再点的合并位）+ `NoReply`（单向指令）。
- Response 结构：`RelayAgent.cs:555-571`。同型 JSON 行。
- 宿主串行请求-响应，单条实测 45~64ms（注释自证：`RelayAgent.cs:533`、`RelayClient.cs:603-606`）——这是 `Move` 合并位与 `NoReply` 出现的原因，也是「协议本来就把 RTT 当稀缺资源」的证据。
- `NoReply` 语义安全依赖**双方严格交替**（代理不回写、宿主不读）：`RelayAgent.cs:539-546`、`RelayClient.cs:496-529`。这是手搓可靠流的典型代价——没有序号时只能靠「读写配对纪律」防错位。
- 心跳：仅 `noop` 刷新 Web 看门狗（`RelayAgent.cs:776-778`）+ Web 前端按 `_lastWebActivityUtc` 释放按键（`RelayAgent.cs:111-115`）。**没有流级别的 RTT 探测**。

### 2.3 视频 / 输入直连协议现状

- `/stream`：服务端订阅共享编码器的 Annex-B NAL，新客户端从最近 IDR 起播，每个 viewer 一个有界队列，`ReadViewerFrame` 轮询 + `Task.Delay(10)`（`AgentWebServer.cs:404-467`）。**每帧之间无任何元数据**：没有长度前缀、没有序号、没有服务端时间戳，全靠客户端 AnnexB 切分。
- 起播等待逻辑：25s 上限 + 每 500ms 主动请求 IDR（`AgentWebServer.cs:410-438`）。
- `/api/input`：请求体 DTO `{action,x,y,dx,dy,button,key,text,delta,count,sid}`（`AgentWebServer.cs:548-560` + Android 侧 `InputSender.kt:28-42`）。坐标是 0~1 相对值（`AgentWebServer.cs:495-498`）。**sid 字段服务端根本没有读**——`InputDto` 里没有 sid（`AgentWebServer.cs:548-560`），安卓发的 `sid` 被静默忽略，意味着代理无法区分多客户端会话，只能靠看门狗整体释放。
- 代理注入实现：`click` = SetCursorPos + down + `Sleep(18)` + up + 双击 `Sleep(90)`（`RelayAgent.cs:850-864`）；`moverel` 单段直发 `mouse_event`（`RelayAgent.cs:820-847`）。

### 2.4 Android 端管线现状（延迟逐点）

| 环节 | 现状 | 证据 |
|---|---|---|
| 拉流 | OkHttp 阻塞读 64KB 缓冲 → 逐字节 AnnexB 切分 → AU 攒批 | `H264StreamReader.kt:46-62`、`AnnexB.kt:28-90` |
| AU 时间戳 | **客户端自造**：每 AU +33_333µs，与真实捕获时间无关 | `H264StreamReader.kt:59,82` |
| 解码器配置 | csd-0/csd-1 + `KEY_LOW_LATENCY=1`（API≥R）；**没设 `KEY_MAX_INPUT_SIZE`** | `H264Decoder.kt:42-68` |
| 解码循环 | 同步 `dequeueInputBuffer(10_000)` / `dequeueOutputBuffer(10_000)` 双轮询，队列空时 `queueInputBuffer(size=0)` 空转 | `H264Decoder.kt:106-142` |
| 输入缓冲检查 | `avcc.size <= buf.capacity()` 不满足则**整 AU 丢弃**（大 IDR 最易触发） | `H264Decoder.kt:113-122` |
| 丢帧策略 | queueDepth≥2 且非 key → 丢；连丢 10 → 重同步（重连拿 IDR） | `H264Decoder.kt:71-85`、`PlayerConstants.kt:12-15` |
| 上屏 | `releaseOutputBuffer(idx, true)`，无自绘时钟控制 | `H264Decoder.kt:138` |
| SurfaceView | 默认（未设 `setZOrderOnTop`/punch-hole），Compose `AndroidView` 包裹 | `PlayerPlaceholderScreen.kt:90-119` |
| 输入 | `send`（down/up/click 阻塞等回包保序）与 `sendNoWait`（move/moverel/wheel）分两条 OkHttp 链路，无全局 FIFO | `InputSender.kt:44-59`、`PlayerPlaceholderScreen.kt:124-129` |
| 输入节流 | 8ms 合并（look/绝对 move 各自独立） | `InputThrottler.kt:12-91`、`PlayerConstants.kt:21` |
| 手势 | 单指滑=moverel、点按=click（tap 阈值 10px），双指=wheel | `PlayerPlaceholderScreen.kt:146-211` |

### 2.5 鉴权现状

- 代理直连：静态 token，空 token 默认拒绝（可显式豁免），**支持查询串传 token**（img/video 标签兼容），注释自认会泄进历史/日志（`AgentWebServer.cs:316-334`）。
- 主程序登录：`GET /api/login?pw=`（`SoulLinkApi.kt:53-60`，注释引用 WebServer.cs L330）——密码进 URL。
- Cookie 会话：内存 CookieJar 仅存 `soullink_sid`（`SoulLinkApi.kt:33-41`）。
- 管道控制：靠进程身份（PID+SessionId+Elevated+Admin）+ 部署 nonce（`RelayAgent.cs:590-595`、`RelayClient.cs:371-386`）——这一侧比 HTTP 侧严谨。
- CORS：只放行同 Host / 回环 Origin（`AgentWebServer.cs:341-360`），收口做得对。

---

## 三、差距（对照成熟设计）

### 3.1 Moonlight 模型（moonlight-common-c 公开设计，社区文档一致口径）

| 维度 | Moonlight | SoulLink 现状 | 差距定级 |
|---|---|---|---|
| 通道分离 | 视频 UDP 47998 / 控制 ENet 可靠+不可靠双通道 / 信令 HTTPS，三者独立 | 视频+输入共用一个 HttpListener；控制走宿主管道；信令走主程序 HTTP | 高（单服务器抖动同时打视频与输入） |
| 帧边界元数据 | 视频包带 frameIndex/flags/服务端时间戳，帧序明确 | 裸 NAL 流，客户端逐字节猜 | 高 |
| 输入通道 | UDP 不可靠 + 事件序号 + 重传窗口，服务端聚合；多事件/包 | 每事件一条独立 HTTP POST，无序号无队列 | 高（WAN 输入卡顿直接来源） |
| 按需 IDR | 控制通道 idrRequest 消息，客户端显式要 | 已有 `SessionRecorder.RequestKeyFrame()`（起播/重连时服务端自发），但**客户端不能主动发 PLI**——`/api/input` 动作表里没有 idr 请求动作 | 中 |
| 丢帧恢复 | 丢帧检测→发 idrRequest→续流，不重连 | 队列深→丢→连丢 10→**整条重连**（新 TCP+新订阅+起播握手），恢复代价大 | 中 |
| 网络自适应 | 客户端反馈丢包/解码延迟→服务端降码率 | 无任何反馈通路（服务端 `videoparams` 只能由主程序配置下发，`RelayClient.cs:649-660`） | 中 |
| FEC | 视频包 RS-FEC 冗余 | 无（TCP 语义上可不做，但换 UDP 时必须补） | 低（当前 TCP 阶段不阻塞） |
| 鉴权 | 一次性 pair（证书+PIN challenge），会话密钥派生 | 长期共享 token + 查询串 | 中（安全而非延迟问题） |

### 3.2 Sunshine（服务端同源参考）

- Sunshine 的输入在会话线程内聚合并 `reset_input_state` 断线释放（社区共识，`input.cpp` 模式）；SoulLink 已有等价的 `releaseall` + 看门狗（`RelayAgent.cs:876-879`、`648-678`），但**触发条件只有「无活动 5s」与「看门狗超时」**，HTTP 断开本身不触发释放——安卓端靠 `releaseAll()` 手动兜底（`InputSender.kt:61-62`），网页断电/崩溃时按住的键会滞留最多 5s。
- Sunshine 对每个客户端维护独立的输入队列与会话状态；SoulLink 的 `/api/input` 是无状态单发，`sid` 被忽略（见 2.3）。

### 3.3 RustDesk/Parsec 佐证

- RustDesk：视频/控制复用单条加密连接但**帧带类型+序号的二进制信封**，输入事件服务端带序号去重。
- Parsec：输入与视频全 UDP，输入端到端预算 <15ms（此前审计 `MATURE-ARCH-FACTS-20261123.md` 已核查）。
- 共同点：**没有一家把每个输入事件当独立 HTTP 请求发**。这是 SoulLink 输入协议与其他所有产品的本质差异。

---

## 四、可执行建议（按收益/成本排序）

### A. 视频流加「最小帧信封」（P0，成本最低收益最大）

不动大架构，只在 `/stream` 每帧 NAL 前加一个小定长头（如 16B：magic/seq/服务端毫秒时间戳/字节数），安卓与网页同步升级。收益：
- 客户端可直接丢弃 seq 跳变段（代替整条重连）；
- 端到端延迟第一次可测（`now - ts`）；
- 为后续拥塞反馈提供输入。
落点：`AgentWebServer.cs:439-461`（写出处）、`H264StreamReader.kt:46-62`、`AnnexB.kt`（切分前先剥头）。**注意兼容**：网页旧版继续裸流需版本协商（`/api/info` 加 `streamVersion`）。

### B. 输入通道改「POST 批 + 客户端序号」（P0）

- `InputDto` 增加可选 `seq`、支持 `events: [...]` 批量（一次 POST 发多个动作，服务端按序执行）；
- `sid` 要么真正接收并在响应里回显（`InputDto` 目前没有该字段，`AgentWebServer.cs:548-560`），要么客户端别发；
- 安卓端把 `down/up` 对塞进同一批，消除「down 与 up 两个独立 POST 中间隔着满 RTT」；
- 服务端对 moverel 维护 per-来源 聚合（对齐 LookThrottler 的 8ms，把合并从客户端挪一半到服务端，网络抖动时更稳）。

### C. Android 解码器三处微修（P0/P1）

1. `MediaFormat` 设 `KEY_MAX_INPUT_SIZE`（按 SPS level 推 2~4MB，或直接用 IDR 实测最大值），避免大 IDR 触发 `H264Decoder.kt:115` 的整 AU 丢弃 → 表现为「点击瞬间画面卡住」的伪延迟。
2. 解码循环去掉双 10ms 轮询：输入侧改为「队列有数据才 dequeueInput」，输出侧用 `MediaCodec.BUFFER_FLAG_...`/回调式 `MediaCodec.Callback`（异步模式），或至少把 `DEQUEUE_TIMEOUT_US` 降到 2ms。当前空转 + 10ms 超时最多贡献 ~10-20ms 每帧抖动。
3. `configure()` 后 `codec.setVideoScalingMode`/`KEY_...` 无关紧要，但 `releaseOutputBuffer` 的渲染路径在 SurfaceView 上默认走 VSync；真正缺的是**帧 pacing**：等 A（帧信封）落地后用服务端时间戳算 `releaseOutputBuffer(idx, renderAtNs)`（API 26+ 版本），替代立即渲染，消除网络抖动被直接放大为显示抖动。

### D. 客户端主动 IDR（PLI）动作（P1）

`/api/input` 动作表加 `idr`（或独立 `/api/pli`），代理转调 `SessionRecorder.RequestKeyFrame()`（已存在，`AgentWebServer.cs:425`）。安卓/网页在「丢帧/画质降级/用户切回前台」时主动请求，替代当前「连丢 10 帧→整条重连」的高代价路径（`H264Decoder.kt:76-79`、`H264Player.kt:116-121`）。

### E. 鉴权：token 换一次性会话票据（P2，非延迟项）

登录后由主程序签发短时效 ticket，代理收到 `/api/info` 级别的校验请求时向主程序确认（或共享 HMAC）。至少先去掉查询串传 token 的兼容路径（`AgentWebServer.cs:329-331`）中 img/video 的用法——改为 cookie 或一次性 302。

### F. 不建议现在做的

- 换 UDP/WebRTC：当前 444ms 主要在服务端节拍与客户端管线（见 AGENT-PIPELINE-LATENCY-GAP-20261123 / WEB-VIEWER-LATENCY-GAP-20261123 的归因），传输层实测 0.8ms（LATENCY-BENCHMARK-20261123），改传输收益最小、工程量最大。
- FEC/RS：TCP 阶段无意义，等真换 UDP 再做。

---

## 五、跨成员交接

- **给 stream-core / 服务端成员**：`/api/input` 的 `InputDto`（`AgentWebServer.cs:548-560`）没有 `sid` 字段，安卓侧 `InputSender.kt:40` 发的 `sid` 被丢弃——若计划做多 viewer 输入归属，先补协议再补实现。
- **给输入链路成员**：代理 `click` 的 `Sleep(18)/Sleep(90)`（`RelayAgent.cs:856-862`）是服务端输入延迟的固定地板，任何客户端节流优化都绕不过它，优先评估降到 10ms 以下或按 button 事件直发（不模拟双击间隔）。
- **给 web/安卓 viewer 成员**：A 建议（帧信封）需要两端协议同步，改动前先在本文档评审，避免各改各的。

## 六、附：Moonlight 协议事实来源说明

联网抓取 GitHub wiki 正文失败（被重定向到 wiki 首页）。本文 §3.1 的 Moonlight 描述基于其公开且多年稳定的协议设计（moonlight-common-c 源码结构：RtpVideoStream/FecPacketPool/ControlStream 的 ENet 双通道、UDP 47998/47999/48000 端口族、idrRequest 控制消息），与已有团队文档 `MATURE-ARCH-FACTS-20261123.md`、`OSS-REMOTE-RESEARCH-20261122.md`、`STREAM-SURVEY-20261122.md` 的核查结论一致，未引入新的未核实数字。
