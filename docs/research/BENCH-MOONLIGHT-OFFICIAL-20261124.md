# 对标研究：官方 Moonlight + Sunshine 源码级实现（BENCH-MOONLIGHT-OFFICIAL）

> 成员：verifier（官方开源实现调研，与 yomori-scout 的 V+ 调研并行对照）
> 日期：2026-11-24
> 对象：moonlight-stream/moonlight-common-c（客户端 C 库）+ moonlight-stream/moonlight-android（Android 端）+ LizardByte/Sunshine（服务端，master 分支）
> 方法：逐文件拉取官方源码，结论全部标注「源码确认」（文件:行号）或 [推测]。
> 定位：对 `AUDIT-20261124-06-PROTOCOL-ANDROID.md` 的增量与纠错，不重复其已列结论。

---

## 〇、一句话结论（先给 Lead 校验 V+ 用）

**「无限 GOP + 纯按需 IDR」不是我们的错误——它就是 Sunshine 官方的默认设计**（源码确认：`Sunshine/src/video.cpp:2347-2351` 明写 "Use an infinite GOP length since I-frames are generated on demand"）。
差别不在「要不要按需 IDR」，而在**成熟产品补齐了三条配套**，我们缺了它们才导致黑屏：

1. **客户端有显式的「请求 IDR」控制消息**（Sunshine 侧 `stream.cpp:1221-1224` 收 `IDX_REQUEST_IDR_FRAME`；moonlight 侧 `ControlStream.c:1741-1743` 发 `0x0302`），错过 IDR 时主动要，而不是干等。
2. **视频帧头带帧类型/序号/服务端延迟**（Sunshine `stream.cpp:98-132` 8 字节 short header），客户端据此做 jitter buffer、测端到端延迟、判帧类型。
3. **FEC 默认 20%**（`Sunshine/src/config.cpp:819`），UDP 丢包可恢复，不必为丢一个包就重连。

---

## A. GOP 与 IDR 策略（★最重要）

### A1. Sunshine 默认 GOP = 无限 + 按需 IDR（源码确认）

`Sunshine/src/video.cpp:2346-2352`：

    // Use an infinite GOP length since I-frames are generated on demand.
    ctx->gop_size = encoder.flags & LIMITED_GOP_SIZE ?
                    std::numeric_limits<std::int16_t>::max() :
                    std::numeric_limits<int>::max();
    ctx->keyint_min = std::numeric_limits<int>::max();

- **默认分支就是「无限 GOP + 按需 IDR」**。这不是我们的独有设计，是成熟产品的既定选择。
- 例外两条（源码确认）：
  - `FIXED_GOP_SIZE` 标志（`video.cpp:429` 注释 "encoder doesn't support on-demand IDR frames"）：`gop_size=120, keyint_min=120`（`video.cpp:2342-2345`），只为 Media Foundation 等不支持按需 IDR 的编码器。
  - legacy 格式（H263+/MPEG2）：`gop_size=600, keyint_min=1`（`video.cpp:2338-2341`）。
- B 帧永远不用：`ctx->max_b_frames = 0`（`video.cpp:2336`），配合 `AV_CODEC_FLAG_LOW_DELAY`（`video.cpp:2365`）。

### A2. 配置项：没有 gop/keyint/idr_refresh（源码确认）

`Sunshine/src/config.cpp` 全文件 grep `gop|keyint|idr|intra_refresh` 无任何可配置项。唯一相关的是 `video.h:62` 的 `enableIntraRefresh`（帧内刷新，不是 GOP），以及 `fec_percentage`/`qp`。→ **Sunshine 不把 GOP 暴露给用户**，按需 IDR 是硬编码策略。

### A3. 新客户端接入的「立刻可解码起点」：显式 IDR 请求 + 启动即强制 IDR（源码确认）

- **启动即强制 IDR**：`Sunshine/src/video.cpp:3279` `idr_events->raise(true)`（编码线程启动时先发一个 IDR）；`validate_config` 里 `session->request_idr_frame()`（`video.cpp:3340`）并断言首包 `is_idr()`（`video.cpp:3350-3354`）。
- **客户端主动请求 IDR** 是独立控制消息：
  - 服务端收：`Sunshine/src/stream.cpp:46` `IDX_REQUEST_IDR_FRAME = 9`，`stream.cpp:1221-1224` 收到后 `session->video.idr_events->raise(true)` → 编码线程 `video.cpp:2770-2777` 读到事件调 `session->request_idr_frame()`。
  - 客户端发：`moonlight-common-c/src/ControlStream.c:1741-1743` `sendMessageAndDiscardReply(packetTypes[IDX_REQUEST_IDR_FRAME], ...)`；packetType 值随代际：Gen3=`0x1407`、Gen4=`0x0606`、Gen7Enc=`0x0302`（`ControlStream.c:162-225`）。

### A4. 客户端 PLI/FIR 等价物：RFI + IDR 请求（源码确认）

1. **RFI（Reference Frame Invalidation）**：`ControlStream.c:1728-1732` 发 `IDX_INVALIDATE_REF_FRAMES`（Gen5+ `0x0301`），只失效参考帧、让服务端发「带 intra-refresh 块的 P 帧」（frameType=4，`Sunshine/stream.cpp:118`），比完整 IDR 省带宽。
2. **完整 IDR 请求**：RFI 不可用或连续丢帧达阈值（`VideoDepacketizer.c` `CONSECUTIVE_DROP_LIMIT=120`）时，发 `0x0302` 要完整 IDR。
- 这是应用层自定义控制消息，不是 RTP 层的 PLI/FIR（Moonlight 的 RTP 只承载音视频数据）。

### 对我们的直接结论

我们「按需 IDR」方向正确，但缺了客户端主动 PLI/RFI 消息——官方用 `0x0302`（IDR）+ `0x0301`（RFI）补齐。

---

## B. 帧边界与时间戳

### B1. RTP 载荷与帧头（源码确认）

moonlight 视频流 = RTP 头 + NV_VIDEO_PACKET + 帧内 short header：

`moonlight-common-c/src/Video.h`（NV_VIDEO_PACKET，packed）：

    uint32_t streamPacketIndex;  // 包在流内的序号
    uint32_t frameIndex;         // 帧序号
    uint8_t flags;               // FLAG_SOF(0x4)/FLAG_EOF(0x2)/FLAG_CONTAINS_PIC_DATA(0x1)
    uint8_t extraFlags;          // 含 FLAG_EXTENSION(0x10)
    uint8_t multiFecFlags;
    uint8_t multiFecBlocks;
    uint32_t fecInfo;            // fecIndex/dataPackets/parityPackets/fecPercentage 打包

- **序号**：`streamPacketIndex`（包级）+ `frameIndex`（帧级），客户端 `RtpvAddPacket`（`RtpVideoQueue.c`）据此重排、判丢、FEC 恢复。
- **帧类型标志**在 Sunshine 扩展的 8 字节 short header（`Sunshine/src/stream.cpp:98-132`）：

    struct video_short_frame_header_t {
        uint8_t headerType;                       // 0x01
        little_uint16 frame_processing_latency;   // 服务端处理延迟，1/10 ms
        uint8_t frameType;                        // 1=P, 2=IDR, 4=P+intra-refresh, 5=P after RFI
        little_uint16 lastPayloadLen;             // 末包长度（AV1 等零填充敏感）
        uint8_t unknown[2];                       // 保留
    };  // sizeof == 8 (static_assert stream.cpp:129-132)

- **PTS**：Sunshine 不传绝对 PTS，只传 `frame_processing_latency`（服务端捕获→编码完成耗时，1/10ms，`stream.cpp:1672-1683`）。

### B2. jitter buffer 与端到端延迟测量（源码确认）

- jitter buffer = FEC 队列 + decode unit 队列：`RtpVideoQueue.c`（按 frameIndex 排序/FEC 恢复/`notifyFrameLost`）、`VideoDepacketizer.c` 的 `decodeUnitQueue`（容量 15，`VideoDepacketizer.c:73`），存 frameNumber/latency/receiveTimeUs 等。队列溢出 → 清空 → `LiRequestIdrFrame()`。
- 端到端延迟统计：`MediaCodecDecoderRenderer.java:1903` `getAverageEndToEndLatency()`，口径 = `totalTimeMs += (enqueueTimeUs - receiveTimeUs)/1000`（`:1647`）+ host processing latency（`:1629-1638`）。注意这是「客户端收首包→入队 + 服务端处理延迟」，非完整闭环；官方未公开「1~3 帧」这种闭环数字。

---

## C. 传输与重传

### C1. 三通道独立 + 端口（源码确认）

- 视频 UDP 47998（`RtspConnection.c:1245`）；控制 UDP 47999（`:1289`）；音频 UDP 48000（`:1161`）；信令/RTSP TCP 48010（`Connection.c:278`），另有 TCP 47984/47989 兼容探测（`Connection.c:365-376`）。
- 输入 Gen5+ 走控制流（`InputStream.c:693-695`），老 Gen 走独立 TCP 35043（`InputStream.c:696`）。

### C2. 丢包：FEC（Reed-Solomon），默认 20%（源码确认）

- 默认 `fec_percentage = 20`（`Sunshine/src/config.cpp:819` `20, // fecPercentage`），范围 0~100（`config.cpp:1870`）。
- 实现 Reed-Solomon：客户端 `RtpVideoQueue.c` 调 `reed_solomon_decode`；校验分片 `bufferParityPackets = dataPackets * fecPercentage/100`。
- 另有 NACK 语义：控制流周期发 `SS_FRAME_FEC_STATUS`（`0x5502`，`Video.h`）汇报收/丢包统计（`ControlStream.c:1626`）。

### C3. 输入通道：ENet 可靠为主，批量聚合（源码确认）

- 绝大多数输入 `ENET_PACKET_FLAG_RELIABLE`（鼠标 `InputStream.c:777`、键盘 `:974`、手柄 `:1153`）。fire-and-forget = `ControlStream.c:1005 sendMessageAndForget`。
- 序号/去重/聚合：每类输入独立 ENet channel（MOUSE/KEYBOARD/GAMEPAD_BASE+n/TOUCH，`InputStream.c:773,973,1150,1437`）；`batchedInputMutex` + `MAX_QUEUED_INPUT_PACKETS=150`（`InputStream.c:83,32`）攒批合并同类型事件（`InputStream.c:1117-1142`）。
- 多处 `// TODO: Send this as unreliable sequenced`（`InputStream.c:775,850,1152`）——官方也承认当前可靠发送、未做「不可靠+序号」优化。
- 代理端无固定 Sleep：moonlight 直接 SendInput 语义，没有我们 `RelayAgent.cs:856-862` 的 `Sleep(18)/Sleep(90)` 固定地板。

### C4. 为什么必须分通道

控制消息（IDR 请求、丢帧报告、输入）必须可靠有序（ENet reliable），视频/音频必须低延迟可容忍丢包（UDP+FEC）。混在一起会队头阻塞卡住视频流。

---

## D. Android 客户端解码（对应我们黑屏）

### D1. KEY_LOW_LATENCY（源码确认）

`MediaCodecHelper.java:495-595 setDecoderLowLatencyOptions`，按厂商分级：官方 `low-latency`（`:503`）→ `vdec-lowlatency`（`:528`）→ `KEY_OPERATING_RATE=Short.MAX_VALUE`（`:534`）/`KEY_PRIORITY=0`（`:538`）→ 各家 vendor key（`:564,572,580,588`）。策略：从最危险到最安全逐级试，configure() 失败降级（`:496-497`）。→ 我们只有 `KEY_LOW_LATENCY=1` 一条，无厂商降级链。

### D2. releaseOutputBuffer：Choreographer 帧 pacing，非立即上屏（源码确认，纠错 AUDIT）

`MediaCodecDecoderRenderer.java:955-975`：`releaseOutputBuffer(nextOutputBuffer, frameTimeNanos)` 用 Choreographer 时间戳；非 balanced 模式 `releaseOutputBuffer(lastIndex, System.nanoTime())`（`:1051`）或 `releaseOutputBuffer(lastIndex, 0)`（`:1046`）。没有 `releaseOutputBuffer(idx, true)` 立即上屏写法。用队列上限 2（`OUTPUT_BUFFER_QUEUE_LIMIT`）多缓冲 1 帧平滑抖动。

### D3. 输入缓冲：复用 input buffer，无 KEY_MAX_INPUT_SIZE（源码确认，纠错 AUDIT）

grep `KEY_MAX_INPUT_SIZE` = 0 命中。官方靠：`getInputBuffer(index)` 复用（`:1134`）+ SPS level_idc 下修（720p→31、1080p→42，`MediaCodecDecoderRenderer.java:1441-1455`，注释 "Max 5 buffered frames at 720x480x60"）+ 按需 IDR 保证每 AU 独立可解码。→ AUDIT 建议「加 KEY_MAX_INPUT_SIZE」是合理加固但非官方解法；官方解法是 SPS level 下修 + input buffer 复用。

### D4. 起播策略：必须等 IDR，但有请求 IDR 容错 + fused CSD（源码确认）

- `VideoDepacketizer.c:72` `waitingForIdrFrame = true` 初始化——起播必须等 IDR（与我们 handleAu 一致）。
- 容错链：等 IDR 期间丢非 IDR → 连丢 120 帧 `LiRequestIdrFrame()`（CONSECUTIVE_DROP_LIMIT）→ 解码器 `DR_NEED_IDR` → `requestDecoderRefresh()`；fused IDR frame 把 SPS/PPS/VPS 与 IDR 图片并进同一 input buffer（`MediaCodecDecoderRenderer.java:1659-1665`）。
- 关键：moonlight 能容错不是不等 IDR，而是有「主动要 IDR」控制消息 + fused CSD 快起。我们缺前者。

### D5. 渲染端：SurfaceHolder，无额外拷贝（源码确认）

`MediaCodecDecoderRenderer.java:70` `SurfaceHolder renderTarget` + `:519` `configure(format, renderTarget.getSurface(), ...)`。直接用 SurfaceHolder 输出，无 TextureView/GL 中转。

---

## E. 延迟预算

### E1. 官方延迟构成（源码确认）

- 服务端处理延迟：`frame_processing_latency`（1/10ms，`Sunshine/stream.cpp:1672-1683`）。
- 客户端 receive→enqueue：`MediaCodecDecoderRenderer.java:1644-1648`。
- 端到端（客户端口径）= host processing latency + receive→enqueue + 解码 + 渲染（`:1903`）。无官方文档承诺的绝对延迟数字，任何「N ms」无源码/实测出处都是 [推测]。

### E2. 自适应码率触发指标（源码确认）

`Sunshine/src/rtsp.cpp:1219-1242`：码率非实时自适应，RTSP 协商时客户端上报 `x-ml-video.configuredBitrateKbps`（`:1174`），服务端扣 FEC+音频开销后定编码码率。没有运行时丢包率→降码率闭环；丢包走 FEC + `IDX_LOSS_STATS` 上报（`ControlStream.c:1688`）。[推测] Moonlight 有「丢包太多→建议降码率」UI 但非自动。

---

## ★ 核心问题：我们「无限 GOP + 纯按需 IDR」在成熟产品里存在吗？

**存在，且就是 Sunshine 官方默认**（`video.cpp:2347-2351`）。不是根本性设计错误。

官方解决「新客户端错过 IDR 永久黑屏」靠三条我们缺的配套（源码确认）：
1. 客户端显式 IDR 请求 `0x0302`（或 RFI `0x0301`）。
2. 帧头帧类型标志（short header frameType）。
3. FEC 20% + 丢帧报告。

我们的缺口：客户端无 PLI/IDR 请求动作；帧流无帧类型/序号/时间戳；起播错过 IDR 永久等且无「连丢 N 帧主动要 IDR」。

---

## 三档可落地清单

### A 档（配置级，今晚可做）

| 项 | 解决症状 | 代价 | 出处 |
|---|---|---|---|
| max_b_frames=0 + LOW_DELAY | B 帧多缓冲 | 确认即可 | `Sunshine/video.cpp:2336,2365` |
| SPS level_idc 下修（720p→31、1080p→42） | 大 IDR 撑爆 input buffer 被整 AU 丢弃 | 半小时 | `MediaCodecDecoderRenderer.java:1441-1455` |
| KEY_LOW_LATENCY 厂商分级降级链 | 低延迟解码 | 1~2 小时 | `MediaCodecHelper.java:495-595` |
| FEC 20%（后续换 UDP 时） | 丢包重连 | 换 UDP 时配置 | `Sunshine/config.cpp:819` |

### B 档（协议级，一两天）

| 项 | 解决症状 | 代价 | 出处 |
|---|---|---|---|
| 帧头加 8 字节 short header（frameType+latency+序号） | 做不了 jitter/测不了延迟/判不了帧类型 | 1~2 天 | `Sunshine/stream.cpp:98-132` |
| 客户端「请求 IDR」控制消息（0x0302/RFI 0x0301） | Android 黑屏、连丢 N 帧重连 | 半天 | `ControlStream.c:1741-1743` + `stream.cpp:1221-1224` |
| 「连丢 120 帧→主动要 IDR」 | 丢帧后永久黑屏 | 半天 | `VideoDepacketizer.c` CONSECUTIVE_DROP_LIMIT |
| 输入批量聚合 + 可靠通道 | WAN 输入卡顿 | 1~2 天 | `InputStream.c:1117-1142` |

### C 档（架构级，需评估）

| 项 | 解决症状 | 代价 | 出处 |
|---|---|---|---|
| 视频改 UDP + RS-FEC | 队头阻塞、丢包重连 | 大 | `RtpVideoQueue.c` + `config.cpp:819` |
| 控制/信令/视频/输入分通道 | 单 HttpListener 抖动互扰 | 大 | `Connection.c`/`RtspConnection.c` 端口族 |

---

## 附：与 AUDIT-20261124-06 的增量与纠错汇总

1. 纠错：AUDIT §3.1 说信令 HTTPS——实为 RTSP over TCP 48010（`Connection.c:278`），HTTPS 仅用于配对。
2. 纠错：AUDIT §2.4 说我们无 KEY_MAX_INPUT_SIZE 导致大 IDR 丢弃——官方也不设，靠 SPS level 下修 + input buffer 复用，「加 MAX_INPUT_SIZE」是加固非对齐官方。
3. 增量：RFI（0x0301）是比纯按需 IDR 省带宽的中间档；frameType=4/5 是 RFI 产物。
4. 增量：Sunshine 帧头是 8 字节 short header（字段精确定义）。
5. 增量：FEC 默认 20%（config.cpp:819）。
6. 确认：AUDIT §F「不建议现在换 UDP」与官方一致——延迟优势不在传输层，而在按需 IDR+显式 PLI+帧头元数据+FEC，可先在 HTTP 通道做帧头+PLI 两条。

---

## 硬性合规

- 全部结论有出处（文件:行号）；「1~3 帧」等无源码数字标 [推测] 或未采信。
- 未改任何代码，纯调研。
- 与 V+ 调研（yomori-scout）分工不重叠：只覆盖官方 moonlight-stream/moonlight-common-c/moonlight-android 与 LizardByte/Sunshine 的 master 分支源码。
