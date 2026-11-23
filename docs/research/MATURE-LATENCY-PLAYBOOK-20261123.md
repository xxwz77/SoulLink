# 成熟串流项目延迟处理 Playbook：SoulLink 可借鉴清单

> 日期：2026-11-23
> 作者：gpu-zero-copy（dsh-team `team_mwsomf2tj73xd7` / member `m_mwsosfpfjamcdf` / task `task_mwsq6uetjx2lcd`）
> 性质：**纯调研**。未改业务代码、未改 csproj、未碰 `Relay/SessionRecorder.cs` 与 `Services/MfFrameEncoder.cs`、未提交 git。
> 证据等级：**【代码事实】**＝当前工作区源码逐行可证；**【开源源码】**＝GitHub API 抓取的 Sunshine / Moonlight-common-c / OBS master 源码片段（存 `artifacts/latency-playbook-src/`，file:line 可回查，GPL-3.0 只读参考不可抄）；**【已有实测】**＝本仓库 `docs/research/` 前序任务实测，附出处。
> 范围：把 Sunshine / Moonlight / OBS / Parsec / 云游戏在【采集节拍 / 编码参数 / 客户端解码队列丢帧 / 输入与画面解耦 / 网络传输】五方面的成熟做法逐项拆解，每条给证据，并映射成 SoulLink 具体改动项（文件+段、收益/成本/风险/优先级）。

---

## 0. 一句话结论

五方面里，SoulLink 已有两条半：**输入与画面解耦（已做对）**、**客户端解码队列丢帧（已做对）**、**采集节拍（做了但睡法是错的）**。真正缺的是 **编码参数（zerolatency / 关 B 帧 / 无限 GOP + 按需 IDR / CBR 一项都没设）** 和 **网络传输（裸 chunked TCP，无 FEC、无丢帧语义）**。按「先参数、再节拍、最后网络」的顺序做，前两项低风险高收益，网络项当前局域网场景不咬人、应缓做。

---

## 1. 采集节拍（帧循环怎么睡）

### 1.1 成熟做法：绝对时间节拍，不是「抓完再睡」

**OBS** `libobs/obs-video.c`：

- 主循环：`tick_sources → output_frames → video_sleep`（:1111-1142）。
- `video_sleep` 用**绝对时间** `os_sleepto_ns(t)` 睡到下一个帧时刻 `t = cur_time + interval_ns`，而不是睡「interval - 上一帧耗时」—— **obs-video.c:808-825**：

```c
static inline void video_sleep(struct obs_core_video *video, uint64_t *p_time, uint64_t interval_ns) {
    uint64_t cur_time = *p_time;
    uint64_t t = cur_time + interval_ns;   // :812 绝对目标时刻
    if (os_sleepto_ns(t)) { *p_time = t; count = 1; }
    else {                                  // 睡晚了：按实际落后量追赶，不累积漂移
        ...
        count = (int)(clamped_diff / interval_ns);
        *p_time = cur_time + interval_ns * count;   // :824 补偿
    }
    video->lagged_frames += count - 1;      // :828 显式统计落后帧
}
```

### 1.2 SoulLink 现状（相对睡法，周期漂移）

`Relay/SessionRecorder.cs:248-249`：

```csharp
int wait = Math.Max(1, 1000 / fps) - (int)frameStart.ElapsedMilliseconds;  // 相对睡
if (wait > 0) Thread.Sleep(wait);
```

**问题**：真实周期 = 抓帧耗时 T + max(0, interval - T)。当 T 超过 interval（抓帧+编码吃满预算）时不睡，周期 = T > interval，**帧率往下掉且无法恢复**。这已被前序实测印证：配置 15fps 实测只有 12.14fps（`LATENCY-BENCHMARK-20261123.md §1.1`，周期 82ms = 抓帧超预算）。

### 1.3 SoulLink 改动项

| 项 | 内容 |
|---|---|
| 改哪里 | `Relay/SessionRecorder.cs:248-249`（`Loop()` 尾部睡法） |
| 改成 | 绝对时间节拍：循环外维护 `long nextTickMs`，每帧 `nextTickMs += interval`，睡到 `nextTickMs - now`；落后超 1 个 interval 时按 OBS 方式 `nextTickMs = now + interval`（不累积漂移），并计入 `laggedFrames` 供 `StatusText` 回显 |
| 收益 | 帧率从「随耗时漂移」回到稳定跟随 fps；落后可见（laggedFrames 进 `StatusText`） |
| 成本 | 0.5 人日（<10 行） |
| 风险 | 低（纯节拍逻辑，不碰抓帧/编码） |
| 优先级 | **P1（高）** |

> 关联项：现有 `StatusText`（`SessionRecorder.cs:110-121`）已输出 `captureMs/convertMs/encodeMs/actualFps`，加 `laggedFrames` 即可让「掉帧」变成可回归数字（呼应 `LATENCY-BENCHMARK §4 建议5`「测量先行」）。

---

## 2. 编码参数（zerolatency · 关 B 帧 · 无限 GOP + 按需 IDR · CBR）

### 2.1 成熟做法：四个参数是低延迟编码的「标配组合」

**Sunshine** `src/video.cpp`（编码器通用参数，master）：

```cpp
// B-frames delay decoder output, so never use them
ctx->max_b_frames = 0;                    // :2336
// 无限 GOP + 按需 IDR；只有不支持按需 IDR 的 Media Foundation 才退化固定 GOP
ctx->gop_size = 120;  // ~2 seconds at 60 FPS  →  Media Foundation 分支  // :2344
ctx->gop_size = INT_MAX;  // I-frames are generated on demand  // :2347-2350（现代编码器）
```

NVENC 私有参数（`src/video.cpp:892-900`）：`zerolatency=1`、`tune=NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY`、`rc=NV_ENC_PARAMS_RC_CBR`、`surfaces=1`、`forced-idr=1`。

**OBS** `plugins/obs-ffmpeg/obs-ffmpeg-nvenc.c`：

```c
} else if (astrcmpi(rc, "vbr") != 0) { /* CBR by default */   // :155
    av_opt_set_int(enc->ffve.context->priv_data, "cbr", true, 0);  // :156
    enc->ffve.context->rc_max_rate = rate;   // :158  CBR = max==min
    enc->ffve.context->rc_min_rate = rate;   // :159
}
...
enc->ffve.context->max_b_frames = bf;        // :174  可关 B 帧
```

### 2.2 SoulLink 现状（一项都没设）

`Services/MfFrameEncoder.cs` 初始化只设 `MF_MT_AVG_BITRATE`（:116），输出类型处 `MaxKeyframeSpacing = Math.Max(300, fps*20)`（:122）**方向反了**——在拉大 GOP 而不是「按需 IDR + 低延迟」。`ICodecAPI`（`CODECAPI_AVLowLatencyMode` / `CODECAPI_AVEncCommonRateControlMode` / `CODECAPI_AVEncMPVDefaultBPictureCount`）在 Vortice 3.8.3 **未绑定**（`NVENC-IMPLEMENTATION-20261123.md §5.3` 已反射确认）。

### 2.3 SoulLink 改动项

| 项 | 内容 |
|---|---|
| 改哪里 | `Services/MfFrameEncoder.cs` ① `:122` `MaxKeyframeSpacing` 方向；② 新增手写 `ICodecAPI` P/Invoke（~40 行，IID `901db4c7-31ce-41a2-85dc-8fa0bf41b8da`，仅 SetValue/GetValue 两槽位） |
| 改成 | ① `MaxKeyframeSpacing` 改小（30~60）或配合按需 IDR 拉大；② 设 `CODECAPI_AVLowLatencyMode=TRUE` + `CODECAPI_AVEncCommonRateControlMode=CBR` + `CODECAPI_AVEncMPVDefaultBPictureCount=0`；③ 若走 NVENC SDK（见 `GPU-ZERO-COPY-20261123.md`）则直接 `zerolatency=1 + tune=ULTRA_LOW_LATENCY + rc=CBR + forced-idr` |
| 收益 | 消除编码器内部帧缓冲（实测软编「30 帧入只出 13 帧」的根因）；消除周期性 95KB IDR 尖峰（`HANDOFF-20261123-LATENCY §1`）；输入 1 帧→输出 1 帧 |
| 成本 | 1 人日（ICodecAPI 手写 COM，本项目翻车过 3 次，见 `Mp4Writer` 教训，须逐项校验返回值） |
| 风险 | 中高。`SetValue` 失败是静默的，须逐项判 HRESULT 并降级；**一次只加一项、验证有效再加下一项** |
| 优先级 | **P1（高，且是硬编能真正出帧的前提）** |

> 与 `NVENC-IMPLEMENTATION-20261123.md §4/§6` 结论一致：这是「不换编码器也能做」的第一优先项，且是修好 NVENC 异步时序后「压 IDR + 低延迟」的必经之路。

---

## 3. 客户端解码队列丢帧策略（有界队列 + 丢旧保新 + 按需 IDR/RFI 重同步）

### 3.1 成熟做法：Moonlight 的「15 帧上限 + 溢出 flush + RFI 优先于 IDR」

**Moonlight** `src/VideoDepacketizer.c`（master）：

- 解码单元队列上限 **15 帧**：`LbqInitializeLinkedBlockingQueue(&decodeUnitQueue, 15)` —— **:94**。
- 连续丢帧上限 120，触发强制 IDR：`#define CONSECUTIVE_DROP_LIMIT 120`（:56）→ `dropFrameState()`（:151-165）里计数到 120 就 `LiRequestIdrFrame()`。
- **RFI（参考帧失效）优先于 IDR**：`dropFrameState` 先判 `waitingForRefInvalFrame = true`（:148），只有「非 RFI 模式 / 从未收到 IDR / 刚丢了一半 IDR」才要完整 IDR（:139-146）。
- 拥塞期间不请求 IDR（关键反常识点）：`VideoDepacketizer.c:1189-1195` 注释 *"Requesting an IDR frame while the network is unstable will just contribute to congestion collapse"*，要等 `waitingForNextSuccessfulFrame` 确认网络恢复才 `LiRequestIdrFrame()`。
- 有界队列满时 flush 整条 + 请求 IDR：`LbqOfferQueueItem` 失败 → `LbqFlushQueueItems`（:558-561）。

### 3.2 SoulLink 现状（已做对，但少了 RFI 与「拥塞期不请求 IDR」）

`docs/web-index.html` 已实现同构策略（前序 web-player 交付）：

- `SKIP_QUEUE = 5`（:983）：`decoder.decodeQueueSize > 5` 时丢 delta AU、只放行 key。
- `SKIP_RESET_LIMIT = 60`（:984）：连续跳 60 AU → `decoder.reset()` + 等下个 IDR（:998-1004）。
- `optimizeForLatency: true`（:1070）已设。
- 从 IDR 起播（:1091-1094）。

**差距**：① 无「按需 IDR 请求」回传服务端（`reset()` 只等自然 IDR）；② 无 RFI 概念（服务端编码器也没暴露 RFI）；③ 丢帧阈值 5 比 Moonlight 的 15 更激进（更省延迟但更频繁重同步）。

### 3.3 SoulLink 改动项

| 项 | 内容 |
|---|---|
| 改哪里 | ① 服务端 `Relay/SessionRecorder.cs` 的 `Viewer.Pending`（已是有界 4 + 丢旧，见 :242）补「按需 IDR」入口；② 网页 `docs/web-index.html` 补「重连/reset 时向服务端请求 IDR」；③（可选）编码器暴露 `RequestKeyFrame()`（`IVideoEncoder.cs:41` 已留接口，现返回 false） |
| 改成 | 服务端加 `RequestKeyFrame(viewerId)` → 编码器 `CODECAPI_AVEncVideoForceKeyFrame`（需 ICodecAPI，见 §2.3）；网页在 `decoder.reset()` 后主动请求 IDR 而非干等 |
| 收益 | 重连/花屏恢复从「等下一个自然 IDR（当前 GOP 被拉到 300+ 帧，最长 20s）」缩短到 1 帧内 |
| 成本 | 0.5~1 人日（依赖 §2 的 ICodecAPI） |
| 风险 | 中（ForceKeyFrame 是 ICodecAPI，同 §2.3 的手写 COM 风险） |
| 优先级 | **P2（中，依赖 §2 先落地）** |

---

## 4. 输入与画面解耦（相对位移独立通道 + 高频合并）

### 4.1 成熟做法：输入绝不走画面链路，相对位移合并发送

- **Sunshine**：相对鼠标是独立协议包 `NV_REL_MOUSE_MOVE_PACKET`（deltaX/deltaY），走控制通道，**不经视频编码**（`src/input.cpp` `print(PNV_REL_MOUSE_MOVE_PACKET)` :483-488；`OSS-REMOTE-RESEARCH-20261122.md §2.2` 已核 file:line）。
- **Moonlight 客户端**：同一帧内的多个鼠标移动事件**合并累加**后一次发送（`OSS-REMOTE §2.2`：`xrel += event->xrel`），省 CPU + 避免丢位移。

### 4.2 SoulLink 现状（已做对，是全项目少有的正确项）

- 独立 `POST /api/input`（`Relay/AgentWebServer.cs:292, 469`），`moverel` 相对位移（:498）。
- 网页端 `lookDelta` 8ms 合并 + `moverel`（`docs/web-index.html:1227-1255`，实测 15/s → 60/s 提升）。

### 4.3 SoulLink 改动项

| 项 | 内容 |
|---|---|
| 结论 | **无需新改动**。已达成「输入与画面解耦 + 相对位移 + 高频合并」三条，与 Moonlight/Sunshine 同构 |
| 唯一可补 | 确认 `/api/input` 与 `/stream` 是否共用端口/事件循环；若 `/api/input` 被推流写阻塞拖慢，需把输入处理挪到独立监听（验证而非假设） |
| 成本 | 0（仅验证） |
| 优先级 | P3（低） |

---

## 5. 网络传输（UDP + FEC + 丢帧语义 vs 裸 chunked TCP）

### 5.1 成熟做法：UDP + Reed-Solomon FEC + RFI/IDR 控制回传

- **Sunshine**：视频走 **UDP**（`src/stream.cpp:491-492` `udp::socket video_sock`），编码后按 `fec_percentage` 做 **Reed-Solomon FEC**（`fec::encode(...)` —— :1783），控制通道回传三类信号：`IDX_LOSS_STATS`（丢包统计 :1205）、`IDX_REQUEST_IDR_FRAME`（请求 IDR :1221-1225，raise `idr_events`）、`IDX_INVALIDATE_REF_FRAMES`（RFI :1227-1237）。
- **Moonlight**：RTP 队列 `RTP_QUEUE_DELAY=10`ms 等待乱序/丢包重组（`src/VideoStream.c:26`）；FEC 预测丢帧时发 **投机 RFI**（`RtpVideoQueue.c` `notifyFrameLost(frameNumber, speculative=true)` :218-220 / :1245-1265）。
- **Parsec / 云游戏**：BUD/自研 RTC 协议（UDP + FEC + 带宽自适应），核心闭源（`LATENCY-BENCHMARK §2` 可信度分级已注明）。

### 5.2 SoulLink 现状（裸 chunked HTTP/TCP）

`Relay/AgentWebServer.cs:428-446` `WriteStreamAsync`：`SendChunked` + `video/h264`，逐帧 `WriteAsync` + `FlushAsync`，无帧边界元数据、无 FEC、无丢帧语义、无 IDR 请求回传。

### 5.3 SoulLink 改动项（关键：当前不咬人，缓做）

| 项 | 内容 |
|---|---|
| 结论 | **这是架构问题，但当前场景（局域网、0.03~0.06Mbps、15fps）危害被高估**。TCP 自带拥塞控制；实测传输仅 0.8ms（`LATENCY-BENCHMARK §1.1⑥`）。UDP+FEC 是给「公网 + 高码率 + 60fps」准备的 |
| 现在该做的 | 只需在 chunked 流里加**帧边界 + 关键帧标记**（服务端 `WriteAsync` 前按 NAL 前缀区分 IDR，网页按 AU 边界解析——网页已做 AU 攒批 :962-966），为将来换 WebRTC/QUIC 留缝，不重写协议 |
| 何时升级 | 跑公网 / 码率 >8Mbps / 60fps 时才上 UDP+FEC（参考 Sunshine `stream.cpp` 的 FEC 与 RFI/IDR 控制回传设计，但那是大工程） |
| 成本 | 现在：0.5 人日（帧边界元数据）；将来升级：5+ 人日 |
| 风险 | 现在：低；将来：高（UDP 要自研丢包恢复） |
| 优先级 | **P3（低，缓做）** |

---

## 6. 汇总：SoulLink 落地清单（按优先级排序）

| 优先级 | 改动项 | 文件+段 | 收益 | 成本 | 风险 |
|---|---|---|---|---|---|
| **P1** | 编码参数：ICodecAPI 低延迟 + CBR + 关 B 帧 + GOP 方向修正 | `Services/MfFrameEncoder.cs:116,122` + 新增 ICodecAPI | 消除软编「30入13出」帧缓冲 + IDR 尖峰，硬编出帧前提 | 1 人日 | 中高（手写 COM） |
| **P1** | 采集节拍改绝对时间 | `Relay/SessionRecorder.cs:248-249` | 帧率不再随耗时漂移，12.14→15fps | 0.5 人日 | 低 |
| **P2** | 按需 IDR 请求（重连/reset 秒级恢复） | `SessionRecorder` + `web-index.html` + `IVideoEncoder.RequestKeyFrame` | 花屏恢复从 ≤20s→1 帧 | 0.5~1 人日 | 中（依赖 P1 ICodecAPI） |
| **P3** | 帧边界 + 关键帧标记元数据 | `AgentWebServer.cs:428-446` | 为将来传输升级留缝 | 0.5 人日 | 低 |
| **P3** | 输入通道独立监听验证 | `AgentWebServer.cs` /api/input | 防止被推流阻塞（仅验证） | 0 | 低 |
| **已达标** | 客户端解码丢帧 + 输入解耦 | `web-index.html:983-1004` / `AgentWebServer.cs:292,498` | — | — | — |

**一句话执行顺序**：先做 P1 的两件（ICodecAPI + 绝对节拍），再做 P2 按需 IDR，P3 网络缓做——因为「卡」的根因是软编优先 + 节拍漂移 + 无低延迟参数，不是网络。

---

## 7. 证据索引

| 结论 | 证据 |
|---|---|
| OBS 绝对时间节拍 + 落后帧统计 | `libobs/obs-video.c:808-828, 1111-1142`（存 `artifacts/latency-playbook-src/`） |
| SoulLink 相对睡法导致漂移 | `Relay/SessionRecorder.cs:248-249`；`LATENCY-BENCHMARK-20261123.md §1.1`（15fps 实测 12.14fps） |
| Sunshine 关 B 帧 + 无限 GOP + 按需 IDR | `src/video.cpp:2336, 2344, 2347-2350`（存 `artifacts/latency-playbook-src/`） |
| Sunshine NVENC zerolatency/tune/rc | `src/video.cpp:892-900` |
| OBS NVENC CBR + 关 B 帧 | `plugins/obs-ffmpeg/obs-ffmpeg-nvenc.c:155-159, 174` |
| SoulLink 未设低延迟参数 | `MfFrameEncoder.cs:116,122`；`NVENC-IMPLEMENTATION-20261123.md §5.3`（Vortice 无 ICodecAPI） |
| Moonlight 解码队列 15 帧上限 | `src/VideoDepacketizer.c:94` |
| Moonlight 连续丢帧 120 → 强制 IDR | `VideoDepacketizer.c:56, 151-165` |
| Moonlight RFI 优先于 IDR | `VideoDepacketizer.c:139-148` |
| Moonlight 拥塞期不请求 IDR | `VideoDepacketizer.c:1189-1195` |
| SoulLink 网页丢帧策略（已做） | `docs/web-index.html:983-984, 998-1004, 1070` |
| Sunshine UDP + FEC | `src/stream.cpp:491-492, 1783` |
| Sunshine IDR/RFI/LOSS 控制回传 | `src/stream.cpp:1205, 1221-1237` |
| Moonlight RTP 乱序等待 + 投机 RFI | `src/VideoStream.c:26`；`RtpVideoQueue.c:218-220, 1245-1265` |
| SoulLink 裸 chunked TCP | `Relay/AgentWebServer.cs:428-446` |
| SoulLink 输入解耦（已做） | `AgentWebServer.cs:292, 498`；`docs/web-index.html:1227-1255` |

## 8. 参考资料

- 开源源码快照（本次抓取，GPL-3.0 只读）：`artifacts/latency-playbook-src/README.md`
- 前序调研：`OSS-REMOTE-RESEARCH-20261122.md`（Sunshine/Moonlight file:line 首核）、`LATENCY-BENCHMARK-20261123.md`（分段归因 + 对标）、`HANDOFF-20261123-LATENCY.md`（软编优先/IDR 尖峰/解码积压三层根因）、`NVENC-IMPLEMENTATION-20261123.md`（ICodecAPI 手写要点）、`GPU-ZERO-COPY-20261123.md`（本任务组上一篇，NVENC SDK 直通）
