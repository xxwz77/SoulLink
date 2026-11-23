# Parsec / Moonlight / Guacamole / RustDesk 延迟工程深挖：参数清单、结构对照与浏览器端天花板

> 日期：2026-11-24
> 作者：deep-parsec（dsh-team `team_mwtjn0d77v8gtr` / member `m_mwtkvdn1e6jqik` / task `task_mwtkvoybefcxr6`）
> 性质：**纯调研，未改任何产品代码、未碰 dist/、未提交 git。**
>
> **证据分级（全文通用）**：
> - **【开源源码】**＝本次直接读取的一手源码，file:line 可回查。分两类：
>   - 【本地快照】＝前序任务抓取、存于本仓库 `artifacts/latency-playbook-src/`、`artifacts/zero-copy-src/` 的 master 快照（Sunshine / Moonlight-common-c / OBS，GPL 只读参考）；
>   - 【本次抓取】＝本次用 PowerShell 从 `raw.githubusercontent.com` 直接拉取（HTTP 200），存 `%TEMP%\parsec-deep-src\`（moonlight-common-c `InputStream.c`/`ControlStream.c`，guacamole-server `rdp.c`/`guac_protocol.h`/`client.h`/`rdp.h`，rustdesk `video_service.rs`/`connection.rs`/`video_qos.rs`）。
> - **【官方文档】**＝官方手册/文档原文（本次直接抓取或引用前序任务已核对文档）。
> - **【实测】**＝本仓库 `docs/research/` 前序任务的实测数字，附出处。
> - **【推断】**＝有依据的推导，未实测；推导过程写明。
> - **【查不到】**＝闭源或一手来源无法访问，明确标注，不编造。

---

## 0. 一句话结论

1. 四个产品**没有一家用「HTTP 逐帧拉流」当主链路**：Moonlight/Sunshine 是「ENet 可靠 UDP 控制流 + RTP/UDP 视频流 + Reed-Solomon FEC」，RustDesk 是「WebRTC 式 P2P/SVC + 每 viewer ACK + TestDelay 自适应 QoS」，Guacamole 是「文本指令流 over WebSocket + sync/ack 背压」，Parsec 闭源（BUD/UDT 类 UDP，【查不到】内部实现）。
2. **低延迟靠的不是单个魔法参数，而是一整套组合拳**：编码器侧「`max_b_frames=0` + 无限 GOP + 按需 IDR + CBR + `delay=0`/`zerolatency=1`」；帧节拍侧「绝对时间步进 + 掉帧不追赶」；队列侧「有界小队列 + 丢旧保新 + 队列满即 flush 并请求 IDR」；网络侧「UDP + FEC + 服务器端按 1ms 粒度 pacing」；输入侧「小包 + 1ms 微批 + 16 位溢出拆包 + latest-wins」。
3. **浏览器端的天花板**（1080p60 H.264 硬解 + WebCodecs + 回环网络）：下限约 **16.7~33.4ms（1~2 个 vsync）+ 解码 2~5ms ≈ 20~38ms**，即「跟手」的物理极限是 **~40ms 端到端视频单程**；输入前向可以压到 **~10ms**。SoulLink 当前 444ms 的差距**不在浏览器形态本身**，而在服务端管线税（静止误判、10ms 轮询、fetch 无帧边界、无按需 IDR 回传）。

---

## 1. Moonlight（moonlight-common-c，客户端侧黄金参考）

### 1.1 传输与网络层

| 项 | 值 | 出处 |
|---|---|---|
| 视频通道 | RTP over UDP（`rtpSocket`，`bindUdpSocket` 带 `SOCK_QOS_TYPE_VIDEO` QoS 标记） | 【本地快照】`mlcc_VideoStream.c:333-335` |
| 接收缓冲 | `RTP_RECV_PACKETS_BUFFERED = 2048` 个包（约 2~3 帧的量，「平滑瞬时突发，不占过多内核内存」） | 【本地快照】`mlcc_VideoStream.c:28-35` |
| 乱序等待 | `RTP_QUEUE_DELAY = 10ms`：RTP 队列最多等 10ms 收齐乱序/丢包，超过就触发丢帧检测 | 【本地快照】`mlcc_VideoStream.c:23-26` |
| 保活/首帧 | 视频 UDP 500ms ping（`VideoPingThreadProc`，:55-82）；首帧超时 10s 断链（:3-4）；Gen3 用 TCP 47996 端口触发开流 | 【本地快照】`mlcc_VideoStream.c` |
| 加密 | AES-GCM，**帧号小于当前帧的旧包在解密前就丢弃**（省 FEC 恢复的 CPU） | 【本地快照】`mlcc_VideoStream.c:189-212` |

**结构要点**：视频是纯 UDP（不可靠）+ 应用层 FEC；控制/输入走 ENet（可靠 UDP）——**音视频与控制分通道，输入不与视频抢同一个队列**。

### 1.2 FEC（Reed-Solomon，恢复 丢包）

客户端队列侧（`mlcc_RtpVideoQueue.c`）：

- RS(255) 8-bit 限定域：data+parity ≤ 255 片；`parity_shards = (data_shards * fecPercentage + 99) / 100`，不足 `minparityshards` 时自动上调百分比——【本地快照】`sunshine_stream.cpp:873-890`（服务端编码同公式）。
- **投机 RFI（speculative RFI）**：从未见过乱序包时，客户端按「缺片数 > 可恢复片数」提前上报 `notifyFrameLost`，让服务端立刻发 RFI 帧，而不是等超时；见过乱序后冷却 5 分钟（`SPECULATIVE_RFI_COOLDOWN_PERIOD_US = 300000000`）——【本地快照】`mlcc_RtpVideoQueue.c:13-15, 210-241`。
- 服务端 FEC 上限：**每帧最多 4 个 FEC 块**（协议 2 bit），超过则该帧关闭 FEC；**帧超过 4096 包（10-bit 索引）直接报错**——【本地快照】`sunshine_stream.cpp:1694-1724`。

### 1.3 解码队列与丢帧策略（对 SoulLink 网页端最有直接参照价值）

【本地快照】`mlcc_VideoDepacketizer.c`：

| 项 | 值 | 行号 |
|---|---|---|
| 解码单元队列上限 | **15 帧**（`LbqInitializeLinkedBlockingQueue(&decodeUnitQueue, 15)`） | :94 |
| 连续丢帧上限 | `CONSECUTIVE_DROP_LIMIT = 120`，达到即强制 `LiRequestIdrFrame()` 重同步 | :56, 151-165 |
| 队列满（`LbqOfferQueueItem` 失败） | **flush 整条队列 + 请求 IDR**（丢旧保新，不是阻塞等待） | :550-566 |
| RFI 优先于 IDR | 丢帧后先判断 `waitingForRefInvalFrame`（参考帧失效帧比重同步 IDR 便宜得多），只有「非 RFI 模式 / 从未收到 IDR / 丢了半个 IDR」才升级为完整 IDR | :132-168 |
| **拥塞期不请求 IDR**（反常识关键点） | *"Requesting an IDR frame while the network is unstable will just contribute to congestion collapse"*——要等收到一个完整帧（网络恢复证据）才发 IDR 请求 | :1182-1195 |
| 单帧尺寸上限 | `MAX_VIDEO_REASSEMBLED_FRAME_SIZE = 32MB`，分片 ≤ 65536（防对抗性小包） | :6-11 |

### 1.4 输入通道（客户端 → 主机）

【本次抓取】`mlcc_InputStream.c`（%TEMP%\parsec-deep-src\）：

| 项 | 值 | 出处 |
|---|---|---|
| 通道 | ENet（可靠 UDP），**专用输入流**，与视频/控制分通道 | 【本地快照】`mlcc_VideoStream.c` 结构 + 【本次抓取】`InputStream.c` 全文 |
| 鼠标微批 | **`MOUSE_BATCHING_INTERVAL_MS = 1`**：超过 1 event/ms 时等 1ms 尝试合并。注释原文：*"This batching wait paradoxically _decreases_ effective input latency by avoiding packet queuing in ENet"*——批 1ms 比立刻发更省延迟（避免 ENet 内部排队） | :40-45, 397-406 |
| 16 位溢出拆包 | 相对位移累加后若超出 int16，**拆成多个包发送**（`while deltaX != 0` 循环） | :419-454 |
| 键盘/按键 | **从不批**（`Keyboard packets are never batched`，:2590 注释）——按键语义不可合并 | :2590 |
| 控制器批量 | 按钮状态变化即终止当前批（`If the new packet has different button flags, end the batch`），同状态摇杆可合并 | :1124-1142 |
| 触摸/笔 | 只批 hover/move；down/up/cancel 绝不批 | :47-48 |
| 滚轮 | 客户端累计到 `WHEEL_DELTA(120)` 才发一格（模拟 HID 报告语义） | :1288-1333 |
| 队列上限 | `MAX_QUEUED_INPUT_PACKETS = 150` | :32 |
| 服务端配合 | Sunshine `input.cpp` 收包后也做**二次批**：同类包合并（rel/abs/scroll/controller/touch/pen 各有 `batch()` 规则，16 位溢出即终止），锁队列期间合并、解锁后才注入 OS | 【本地快照】`sunshine_input.cpp:1883-2234` |

### 1.5 控制通道反馈（丢帧上报 / IDR 请求 / 连接质量）

【本次抓取】`mlcc_ControlStream.c`：

| 项 | 值 | 出处 |
|---|---|---|
| 丢帧统计上报周期 | `LOSS_REPORT_INTERVAL_MS = 50ms`（每 50ms 上报一次帧丢失统计，服务端据此调 FEC） | :316, 1680-1701 |
| 连接质量判定 | 丢帧率 ≥30%（单次）或 ≥15%（连续两区间）→ 差；≤5% → 恢复正常 | :140-142, 522-536 |
| IDR 请求实现 | 不支持 IDR 请求的旧服务端，用 **RFI（invalidate reference frames）** 代替：请求作废 `lastSeenFrame-0x20 ~ lastSeenFrame` 的参考帧 | :1708-1751 |
| RFI 请求 | `firstFrameIndex/lastFrameIndex` 区间作废，走 `CTRL_CHANNEL_URGENT` + 可靠包 | :1756-1778 |
| 通道 | ENet 多 channel，URGENT 通道插队 | packetTypes 表 + CTRL_CHANNEL_URGENT 常量 |

### 1.6 Sunshine 服务端编码参数（Moonlight 的对端）

【本地快照】`sunshine-video.cpp`（artifacts/zero-copy-src/）+ 【本地快照】`sunshine_stream.cpp`：

| 参数 | 值 | 出处 |
|---|---|---|
| `max_b_frames` | **0**（注释：*"B-frames delay decoder output, so never use them"*） | video.cpp:2335-2336 |
| GOP | 无限（`gop_size = INT_MAX`，按需 IDR）；仅 Media Foundation 等不支持按需 IDR 的编码器退化为固定 `gop_size=120`（60fps 下约 2s）；legacy 格式 600 | video.cpp:2338-2352 |
| NVENC 私参 | `delay=0`、`forced-idr=1`、`zerolatency=1`、`surfaces=1`、`tune=ULTRA_LOW_LATENCY`、`rc=CBR`、`cbr_padding=false` | video.cpp:892-903（av1/hevc/h264 三个编码器同配） |
| QSV 私参 | `async_depth=1`、`low_delay_brc=1`、`low_power=1`、`forced_idr=1`（H264 另加 `vcm=1`、`max_dec_frame_buffering=1`） | video.cpp:990-1067 |
| 通用 flag | `AV_CODEC_FLAG_CLOSED_GOP | AV_CODEC_FLAG_LOW_DELAY`、`AV_CODEC_FLAG2_FAST` | video.cpp:2363-2367 |
| 帧节拍 | **frame pacing group**：以真实捕获时间戳为锚，绝对时间步进；睡过头则作废当前组（不追赶不漂移）；`snapshot(0ms)` 继续 vs `snapshot(200ms)` 开新组；连续 timeout 时 **无锁睡 10ms** 防 D3D11 非公平锁饿死编码线程 | display_base.cpp:225-345 |
| 帧率自适应 | 请求 fps 与显示器刷新率非整数倍时自动对齐（**只允许降不允许升**，注释：升会积压帧增延迟） | display_base.cpp:226-248 |
| 发送 pacing | **每 1ms 一个 ratecontrol 组**（80% of 1Gbps 换算成包数）；**批量 ≤64KB 且 ≤64 包**（注释：Windows 上 >64KB 的批会绕过 SO_SNDBUF 进 "Other I/O" 等中断，性能不一致；Linux GSO 上限 64） | stream.cpp:1738-1750 |
| 帧时间戳 | RTP 90KHz 时钟，用**捕获时刻**（非发送时刻）；重复帧用 ratecontrol 时刻兜底 | stream.cpp:1802-1810 |

---

## 2. Parsec（闭源标杆，行为可观测）

| 项 | 结论 | 证据等级 |
|---|---|---|
| 传输协议 | 自研 BUD（UDP 类低延迟可靠传输），非 WebRTC | 【推断】（行业共识，本仓库 `STREAM-SURVEY-20261122.md` §1 #12 已核对「BUD 是 UDP 而非 WebRTC」） |
| 编码 | H.264/HEVC/AV1 GPU 硬编；桌面端「Hosting mode」宣称 <16ms 量级（具体数字官方未给可复核口径） | 【推断·未独立复核】 |
| 抓屏 | GPU 抓屏（DDA/WGC 类）+ IddCx 虚拟显示器（parsec-vdd 开源组件是其虚拟显示器驱动的公开版） | 【推断】（parsec-vdd GitHub 仓库存在，`STREAM-SURVEY` 已核对） |
| 输入 | 驱动级注入（装 Parsec 虚拟输入驱动）；宣称输入延迟极低 | 【查不到】内部实现；行为（虚拟驱动存在）【推断】 |
| 内部参数 | 编码参数/队列策略/FEC 具体值 | **【查不到】**——闭源，无一手源码，本文不编造 |

**可借鉴的行为事实**（公开可验证）：
- 官方文档明确推荐「关闭 Windows 游戏栏/Game DVR」「全屏独占」——侧面说明其延迟预算里每一毫秒都要抠（【推断】）。
- Parsec Labs/社区对延迟的解释口径是「网络 <16ms + 编码解码各几 ms + 显示 1 帧」，与本文 §5 天花板模型一致（【推断·未独立复核】）。

**结论：Parsec 的「不可抄」部分是协议与驱动；「可抄」部分是架构形态（UDP+硬编+驱动级输入+虚拟显示器），而这些全部能在 Moonlight/Sunshine 开源源码里找到等价实现。Parsec 的价值是证明这条路的天花板够高。**

---

## 3. Apache Guacamole（浏览器端 RDP/VNC 网关，SoulLink 网页形态的直接同类）

### 3.1 架构

guacd（C 守护进程，跑 FreeRDP/VNClib 客户端）→ Guacamole 协议（**文本指令流**，长连接指令 `size/rect/img/copy/cursor/sync/ack/key/mouse`）→ WebSocket → 浏览器 JS 客户端（guacamole-common-js）canvas 绘制。

### 3.2 关键延迟相关事实（【本次抓取】%TEMP%\parsec-deep-src\）

| 项 | 值 | 出处 |
|---|---|---|
| RDP 事件轮询间隔 | `GUAC_RDP_MESSAGE_CHECK_INTERVAL = 1000ms`（`WaitForMultipleObjects` 超时上限；**事件就绪会立刻返回**，1s 只是空闲上限，不是每帧延迟） | `client.h` define + `rdp.c:919-957`（do-while 0ms 空转直到取空） |
| 帧结束信号 | `sync` 指令带 timestamp + 帧合并数（`guac_protocol_send_sync(socket, timestamp, frames)`）——**帧边界语义内建在协议里** | `guac_protocol.h:381-402` |
| 背压 | `ack` 指令逐流确认；渲染独立线程 `guac_display_render_thread`（脏区合并、指令→图像编码都在渲染线程，与 RDP 消息线程解耦） | `rdp.c:916, 959-964`（`gdi_modified` → `notify_modified`） |
| 输入 | 浏览器 JS → 指令（key/mouse）→ guacd 输入 FIFO（容量 `GUAC_RDP_INPUT_EVENT_QUEUE_SIZE = 4096` 事件）→ 事件信号量唤醒 RDP 线程（`input_event_queued` handle 挂进 `WaitForMultipleObjects`，**输入立即打断等待循环**，不等下个轮询周期） | `rdp.c:725-737` + `rdp.h` |
| keep-alive | 独立线程，仅在有写入间隔超时时补 sync（防代理/防火墙断空闲连接） | `guac_socket.c:46-63` |
| 编码 | 图像矩形 PNG/WebP/JPEG（**矩形图块**，非视频流）；RDP GFX AVC 透传是可选路径 | 【官方文档】（前序 `STREAM-SURVEY §1 #5` 已核对）+ guac_protocol.h 结构 |

### 3.3 对 SoulLink 的启示

1. **Guacamole 用 1000ms 事件轮询照样能做「即时输入」**——因为输入用 event handle 插队唤醒（`rdp.c:728` 把 `input_event_queued` 挂进 `WaitForMultipleObjects`）。轮询间隔只影响空闲 CPU，不影响输入延迟。**SoulLink 的 10ms 输出轮询同理可以放宽，只要输入/控制是事件驱动插队的。**
2. **帧边界靠协议指令（sync）显式表达**，不靠字节流猜——这正是 `WEB-VIEWER-LATENCY-GAP §3` 指出的 SoulLink fetch 流「块边界≠帧边界」问题的标准解法（WebSocket + 帧定界消息）。
3. Guacamole 的延迟量级（RDP 原生 + 矩形编码 + WebSocket）整体高于 Moonlight——**它是「浏览器可达性优先」形态，不是「延迟优先」形态**。SoulLink 想要两者兼得：视频走 Moonlight 式管线（H.264+WebCodecs），控制/会话管理走 Guacamole 式指令流（WS）。

---

## 4. RustDesk（开源 WebRTC 系远程桌面）

### 4.1 帧节拍与帧队列（【本次抓取】%TEMP%\parsec-deep-src\rustdesk_video_service.rs）

| 项 | 值 | 出处 |
|---|---|---|
| 帧循环 | `c.frame(spf)` 阻塞拿帧（spf=1/fps）；**循环尾部绝对时间补偿**：`if elapsed < spf { sleep(spf - elapsed) }`——实际节拍 = max(捕获耗时, spf)，**与 OBS/Sunshine 同一绝对时间思想**（不追赶，丢帧就丢） | :818, 1024-1029 |
| **多 viewer 帧确认** | `VideoFrameController`：每帧记录 `send_conn_ids`，**等所有 viewer 回「帧已取」通知**（最多 3s，每 300ms 检查一次）才进下一帧——慢 viewer 天然形成背压，**快 viewer 不会被慢 viewer 拖死**（`try_wait_next` 只等未确认的） | :176-250, 1009-1021 |
| 每 viewer ACK | 客户端在 LoginRequest 协商 `video_ack_required`；服务端对需要 ACK 的连接等 `notify_video_frame_fetched` | connection.rs:418, 1099-1103 |
| 慢帧丢弃 | rx 队列里延迟 >1000ms 的 AudioFrame 直接 continue（丢音频不丢视频）；**音频让位于视频** | connection.rs:1110-1122 |
| 编码失败切换 | 同一帧编码失败达上限自动换编码器（`switch due to encoding fails`）；关键帧间隔：**录制模式 240，串流模式 None（按需）** | video_service.rs:1330-1335, 1114-1115 |

### 4.2 QoS（video_qos.rs，全文件核心是自适应 fps/bitrate）

| 项 | 值 | 出处 |
|---|---|---|
| 基准帧率 | `FPS=30`，`MIN_FPS=1`，`MAX_FPS=120`，**初始 `INIT_FPS=15`**（起步保守，按网络爬升） | :51-55 |
| 延迟阈值 | `DELAY_THRESHOLD_150MS = 150ms`（好/坏网络分界）；RTT 基线由滑动窗口最小值动态估计 | :66, 843-857 |
| 自适应规则 | TestDelay 回显延迟（减 RTT 基线）：<50ms 涨 fps（快通道连 3 次翻倍）；50~100ms 缓涨；100~150ms 保持；>150ms **先降码率再降 fps**（注释：bitrate-targeted 编码器降帧率不省字节，必须先降码率）；**探测超时 2s 无回应 → fps 减半** | :463-598（整段 user_network_delay） |
| 多 viewer | 取所有 viewer 的**最保守**决定（min fps / max 触发降级） | :736-747, 818-838 |
| 恢复 | 降级前记 `fps_before_congestion`，网络好 3 次后逐步回升 | :130-174 |

**结构启示**：RustDesk 的 QoS 是**闭环的**（TestDelay 探测 → 自适应 → 探测），SoulLink 目前是开环（固定 fps/码率，无 RTT 探测、无自适应）。RustDesk 的「慢 viewer ACK 背压 + 音频让位视频」直接适用于 SoulLink 的多 viewer Pending 队列设计。

### 4.3 网络层

- WebRTC（含 ICE 打洞、STUN/TURN、SRTP）——【源码依赖】（前序 `STREAM-SURVEY §1 #3` 已核对 webrtc 依赖）。
- 私有中继（hbbs/hbbr）走 TCP 转发 + 自有加密——【官方文档】。

---

## 5. 浏览器端能做到什么：天花板估算（验收必查项）

### 5.1 模型（逐项可查证）

端到端（输入 → 看见光标动）= 输入前向 + 画面回传。画面回传又分：

```
T_capture(变化→捕获) + T_encode(编码) + T_net(网络) + T_decode(解码) + T_render(上屏)
```

### 5.2 各段物理下限（1080p60 H.264 硬解，Chrome/Edge，回环或局域网）

| 段 | 下限 | 依据 | 等级 |
|---|---|---|---|
| 捕获→编码入队 | DDA AcquireNextFrame 有变化即返回，≈0~1 帧显示刷新（16.7ms@60Hz） | 【开源源码】Sunshine display_base（frame pacing 锚定捕获时间戳）；WGC/DDA 行为 | 【推断·基于源码语义】 |
| NVENC 编码 | 1~3ms | 【实测】本仓库 `NVENC-IMPLEMENTATION-20261123.md`（encodeMs=1~3） | 【实测】 |
| 网络（LAN） | 0.1~1ms；WAN = RTT/2 | 【实测】本仓库 `/live` 0.8ms p95 4.6ms（回环） | 【实测】 |
| WebCodecs 解码 | 2~5ms（`optimizeForLatency:true`，硬解，无 B 帧） | 【实测】本仓库（:⑨ 1~4ms@1440p 估）+ Moonlight 桌面对照 | 【实测+推断】 |
| 解码→上屏 | **1~2 个 vsync（16.7~33.4ms@60Hz）**：output 回调到合成器呈现；用 `requestVideoFrameCallback` 对齐可到 1 个 vsync；同步 drawImage 无对齐则平均再加 0~1 帧 | Chrome 合成器行为 + `WEB-VIEWER-LATENCY-GAP §2-⑩⑪` | 【推断】 |
| 输入前向 | 1~5ms（事件→HTTP/WS→注入） | 【实测】`AUDIT-20261124-04 §1` ①~⑥ ≈5~15ms（含 8ms 合并窗） | 【实测】 |

### 5.3 天花板数字（推导依据全部列明）

**视频单程（看到画面变化）**：
- 最好情况（一切对齐）：16.7（捕获 1 帧）+ 1（编码）+ 0.5（网）+ 2（解码）+ 16.7（1 vsync 上屏）≈ **37ms**
- 典型情况（2 vsync + 保守捕获）：16.7×2 + 3 + 1 + 4 + 16.7×1 ≈ **58ms**
- **结论：浏览器端 1080p60 的视频单程物理天花板 ≈ 37~60ms**。这是 Chrome WebCodecs + vsync 合成的结构下限，换传输协议（WS/WebTransport/WebRTC）**不会突破**这个下限，只能消掉「协议税」（帧边界、合并缓冲、无按需 IDR 的重同步等待）。

**输入→看见（闭环跟手）**：
- 下限 = 输入前向 5ms + 视频单程 37ms ≈ **42ms**
- Moonlight 桌面版社区实测 LAN 端到端 ~30~50ms（【推断·未独立复核】），与此模型吻合。
- SoulLink 当前 444ms（【实测】`HANDOFF-VIDEO-LATENCY-GPU-20261124`）→ 距天花板 **约 8~10 倍**，但差距分解已由 `WEB-VIEWER-LATENCY-GAP §2` 完成：网络 ≈0，管线税 ≈2/3 服务端 + 1/3 浏览器。

### 5.4 浏览器形态的能力边界（哪些做不到）

1. **做不到零拷贝呈现**：WebCodecs `VideoFrame` 出来必须经 canvas（2D/WebGL）drawImage → 合成器 → vsync。绕不开 1~2 vsync。（`WEB-VIEWER-LATENCY-GAP §4` 已核对无 rVFC 对齐是现状缺陷，可修。）
2. **做不到应用层 FEC**：浏览器 TCP（fetch/WS）由内核重传，丢包退化为队头阻塞；WebRTC-datachannel/WebTransport（不可靠）理论上可用但 SoulLink 当前形态不用。**回环/优质 LAN 下这不是瓶颈**（【实测】丢包≈0）。
3. **做不到驱动级输入**：浏览器指针事件粒度受 OS 合成限制，Pointer Lock + movementX 是浏览器能拿到的最高粒度（SoulLink 已用）。
4. **做不到「客户端主动要 IDR」的自动通道**：必须自建控制消息（WS 一条 control 帧即可）——这是 SoulLink 与 Moonlight 的**关键结构差距**（`WEB-VIEWER-LATENCY-GAP §0.1 Q1` 已论证）。

---

## 6. 四产品结构对照表（一屏总览）

| 维度 | Moonlight/Sunshine | Parsec | Guacamole | RustDesk | **SoulLink 现状** |
|---|---|---|---|---|---|
| 视频 | RTP/UDP + RS-FEC + 10ms 乱序窗 | BUD/UDP【查不到】 | 指令流+矩形图块 over WS | WebRTC SRTP/私有中继 | chunked HTTP 逐 NAL（无帧边界） |
| 编码 | 硬编 + `b=0` + 无限GOP + 按需IDR + CBR + `delay=0` | 同路线【推断】 | PNG/WebP 矩形（非视频） | VP8/9/H.264/AV1 硬编 + 按需关键帧 | NVENC 低延迟已配（无限GOP），**缺客户端 IDR 回传** |
| 帧节拍 | 绝对时间 pacing group，掉帧不追 | 【查不到】 | RDP 事件驱动 + 渲染线程 | `sleep(spf - elapsed)` 绝对补偿 | 相对睡已改（A2），**静止误判仍在** |
| 队列/丢帧 | 15 帧上限 + 满即 flush+IDR + RFI 优先 | 【查不到】 | ack 背压 + sync 帧界 | 每 viewer ACK + 3s 超时 + 音频让位 | SKIP_QUEUE=5 + reset（无 IDR 可拿） |
| 输入 | ENet 专用流 + 1ms 微批 + 16位拆包 + 按键不批 | 驱动级【查不到】 | WS 指令 + event 插队唤醒 | WS/RTC 消息 + TestDelay | 每事件 HTTP POST + 8ms 合并 |
| 自适应 | 丢帧统计 50ms 上报 → 调 FEC | 【查不到】 | 无（矩形本质自适应） | TestDelay 闭环 + 先码率后帧率 | 无（开环） |

---

## 7. 可直接落地的参数清单（SoulLink 移植表）

按收益/成本排序，每项给来源（全部是上文核对过的一手源码）：

| # | 改动 | SoulLink 落点 | 参照 | 优先级 |
|---|---|---|---|---|
| 1 | **帧边界消息**：WS/HTTP 流改「每帧一个 length-prefix 消息」（或 SSE event） | `docs/web-index.html` 拉流循环 + `AgentWebServer.WriteStreamAsync` | Guacamole sync 指令（`guac_protocol.h:401`）；Moonlight EOF flag（`sunshine_stream.cpp:1776-1778` FLAG_SOF/EOF） | **P0** |
| 2 | **客户端→服务端按需 IDR**：decoder reset / 花屏检测时发控制消息，服务端 `ForceKeyFrame` | `web-index.html` + `SessionRecorder` 已留 `RequestKeyFrame` 入口（`IVideoEncoder.cs:41`） | Moonlight `LiRequestIdrFrame`（VideoDepacketizer.c:56,164）+ **拥塞期不请求**（:1189-1195） | **P0** |
| 3 | 输入微批改 1ms + 按键不批 | `web-index.html` `MOVE_MIN_INTERVAL_MS=8` → 1~2ms；click down/up 保持独立 | `mlcc_InputStream.c:44`（1ms 批悖论注释） | P1 |
| 4 | 16 位溢出拆包（相对位移通道） | 若做 moverrel 聚合，delta 超 int16 拆多包 | `mlcc_InputStream.c:419-454` | P1 |
| 5 | 队列满 → flush 整条 + 请求 IDR（不是只丢 delta） | `SKIP_RESET_LIMIT` 逻辑重做 | `mlcc_VideoDepacketizer.c:558-566` | P1 |
| 6 | 服务端发送 pacing：批量 ≤64KB/≤64 包，1ms 粒度 | 若改 UDP/WS 批量写 | `sunshine_stream.cpp:1740-1750` | P2（当前 TCP 形态不咬） |
| 7 | TestDelay 闭环 QoS：100ms 探测 + 150ms 阈值 + 先码率后帧率 | 后续自适应阶段 | `rustdesk_video_qos.rs:51-66, 463-598` | P2 |
| 8 | 慢 viewer ACK 背压（视频帧等确认再产下一帧，超时跳过） | `SessionRecorder` viewer Pending | `rustdesk_video_service.rs:176-250` | P2（多 viewer 时） |
| 9 | 音频让位视频（延迟>1s 丢音频帧） | 未来音频通道 | `rustdesk_connection.rs:1110-1122` | P3 |
| 10 | RS-FEC + 10ms 乱序窗 | 仅当迁 UDP 时 | `mlcc_RtpVideoQueue.c` 全套 + `sunshine_stream.cpp:873-890` | P3（LAN 不咬） |

---

## 8. 附：本次源码清单（可回查）

| 文件 | 来源 | 位置 |
|---|---|---|
| moonlight-common-c `InputStream.c`/`ControlStream.c` | raw.githubusercontent master，HTTP 200 | `%TEMP%\parsec-deep-src\mlcc_*.c` |
| guacamole-server `rdp.c`/`guac_protocol.h`/`client.h`/`rdp.h`/`guac_socket.c` | 同上 | `%TEMP%\parsec-deep-src\guac_*` |
| rustdesk `video_service.rs`/`connection.rs`/`video_qos.rs` | 同上 | `%TEMP%\parsec-deep-src\rustdesk_*` |
| Sunshine `video.cpp`/`stream.cpp`/`input.cpp`/`display_base.cpp` | 前序任务快照 | `artifacts/zero-copy-src/`、`artifacts/latency-playbook-src/` |
| Moonlight `VideoStream.c`/`VideoDepacketizer.c`/`RtpVideoQueue.c` | 前序任务快照 | `artifacts/latency-playbook-src/` |

（%TEMP% 副本按工作区整洁规则放临时目录，不入库；`artifacts/` 快照是前序任务留存。）

**与既有文档的关系**：本文不重复 `MATURE-LATENCY-PLAYBOOK-20261123`（Sunshine/OBS 五方面 vs SoulLink 差距）与 `WEB-VIEWER-LATENCY-GAP-20261123`（网页逐毫秒审计）已覆盖的内容；本文新增的是：①Moonlight 输入/控制通道一手细节（1ms 批、16 位拆包、50ms 丢帧上报、RFI 实现细节）；②Guacamole 结构一手数据（1000ms 轮询 + 事件插队、sync/ack 语义）；③RustDesk 帧节拍/ACK 背压/TestDelay QoS 一手数据；④四产品对照表与浏览器端天花板数字推导。
