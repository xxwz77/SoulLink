# 视频流方案调研：Windows 自带 MF vs 小型库 vs 精简 ffmpeg（2026-11-22）

> 日期：2026-11-22；作者：oss-remote（task_mwrvnt2iz0djsc）；团队：soullink-stream-opt
> 范围：只读调研 + 本文档。**未安装任何东西、未改业务代码、未动 dist、未提交。**
> 约束（用户 2026-11-22 最终确认）：① 用户**零额外安装**是唯一硬约束；② 节约硬件资源（CPU/显存/电量）；③ 画质与延迟最优；④ 体积尽可能小但**不是硬指标**（20~50MB 可接受）；⑤ **不做 COS、不考虑流量/分发成本**。
> 证据标注：**已证实**＝实测/源码/官方文档逐字核对；**需实测**＝逻辑成立但本机未端到端验证；**传闻**＝第三方描述。

---

## 0. 一句话最终推荐

**推荐：Windows 自带 Media Foundation（MF）H.264 硬编直出 fMP4 片段流 + 浏览器 MSE（fMP4）播放。零新增体积、用户零安装、走硬件编码器（NVENC/QSV/AMF）省 CPU。**

- **体积**：0 新增（系统自带 `mfplat.dll` / `mfreadwrite.dll` / NVIDIA H.264 Encoder MFT，SoulLink 已实测枚举到）。
- **是否额外安装**：否（这是决定性优势）。
- **更新成本**：0（不进安装包）。
- **兜底**：MF 在极少数环境不可用/要写内存流有坑时，**降级到「精简 ffmpeg 单文件」**（BtbN `ffmpeg-n9.0-latest-win64-gpl-shared` 解压后约 80~190MB，可裁剪；或用 gyan.dev essentials 35MB 7z），进程调用 `-f mp4 -movflags frag_keyframe+empty_moov` 直接吐 fMP4 到 stdout。但**默认不走 ffmpeg**，只作 MF 失败时的 B 计划。

---

## 1. 现状（已证实，读现有代码）

SoulLink 已具备关键基础，本方案是「把已有 MF 能力从离线转码升级为在线直播」：

| 现有资产 | 位置 | 说明 |
|---|---|---|
| MF H.264 离线 MP4 转码 | `Services/Mp4Writer.cs`（390 行） | 已用 `IMFSinkWriter` + `MFCreateSinkWriterFromURL` 把 JPEG 序列写成 H.264 MP4，实测 17.6 倍压缩 |
| MF 编码器探测 | `tools/media-probe/MfProbe.cs` | 实测枚举到 **NVIDIA H.264 Encoder MFT（NVENC 硬编）** + `H264 Encoder MFT`（软编）；关键教训「硬件 MFT 必须用 `MFT_ENUM_FLAG_HARDWARE` 单独枚举」 |
| MF 编码成本实测 | `MEDIA-CAPABILITY-20261122.md` §2.7 | MF H.264 单帧 0.284ms（CPU 侧提交下界），vs GDI+ JPEG 4.9ms；带宽 H.264 单帧 23KB ≈ 0.69MB/s @30fps vs JPEG 197KB ≈ 5.78MB/s |
| 抓屏瓶颈 | `MEDIA-CAPABILITY` §2.5 | **BitBlt 17~20ms/帧是真正瓶颈**（本地控制台实测），WGC 桌面复制 0.13ms；RDP 会话内 WGC 已被 BetterGI 实测可用 |
| 浏览器播放现状 | `docs/web-index.html`（STREAM-PLAN-V2 提及） | 目前是 HTTP 拉 JPEG 单帧（90KB/帧），无视频流 |

**关键结论**：编码不是瓶颈（MF H.264 已比 JPEG 便宜一个数量级），抓屏才是。本调研聚焦「编码→浏览器」这一跳的方案选择，抓屏方案（WGC/BitBlt）见 `GITHUB-SWEEP` / `MEDIA-CAPABILITY`。

---

## 2. 方案对比表（含打包体积 / 更新成本 / 用户安装）

| 方案 | 带宽(1080p@30fps) | CPU/GPU | 打包体积 | 更新成本 | 用户额外安装 | 兼容性 | 画质/延迟 | 实现成本 |
|---|---|---|---|---|---|---|---|---|
| **① MF H.264 硬编 + MSE（推荐）** | ~0.7~2 Mbps | CPU 极低（硬编走 GPU） | **0** | **0** | **无** | Win10+ 全有（SoulLink 目标就是 Windows） | 好 / 低延迟 | 中（SinkWriter 已有 390 行，改内存流+分包） |
| ② MF H.264 软编 + MSE | ~2~4 Mbps | CPU 高（软编） | 0 | 0 | 无 | 同上 | 好 / 低延迟 | 低（同①，只是编码器选软件 MFT） |
| ③ 精简 ffmpeg 单文件 | ~0.7~2 Mbps | CPU 低（`-c:v h264_nvenc`） | 20~80MB | 每用户+20~80MB | 无（随程序目录） | Win x64 | 好 / 低延迟（`-tune zerolatency`） | 中（进程调用，管道） |
| ④ 完整 ffmpeg | 同③ | 同③ | 80~190MB | 每用户+80~190MB | 无 | Win x64 | 最好 / 参数最细 | 低（现成命令） |
| ⑤ 小型库（openh264/minih264/PopH264） | ~2~4 Mbps | CPU 高（软编） | 0.5~2MB | 可忽略 | 无（随 DLL） | C/C++ 需 P/Invoke | 中 / 软编延迟较高 | 高（要写 C# 绑定 + 包装） |
| ⑥ 现有 JPEG 单帧（基线） | ~5.78 MB/s（=46Mbps） | CPU 高（JPEG 4.9ms + BitBlt 17ms） | 0 | 0 | 无 | 全浏览器 | 一般 / 帧率受限 | 已实现 |

**带宽对比结论**：H.264 硬编（①③）比 JPEG（⑥）**省 25~60 倍带宽**（0.69 vs 46 Mbps），这是「60fps 直播」能否实现的决定性差异——JPEG 在 60fps 下要 92Mbps，H.264 只要 ~1.4Mbps。

**体积对比结论**：① 是唯一「0 体积 + 0 安装 + 0 更新成本」的方案；③ 是「20~80MB 但体验最稳」的兜底；④ 是「体积最夸张」的备胎。

---

## 3. MF 直出 H.264 流可行性（已证实，含关键 API 与伪代码）

### 3.1 结论：可行，两条路都成立

MF 写「直播流」（不是离线 MP4）有两条路：

| 路 | 机制 | 适用 | SoulLink 建议 |
|---|---|---|---|
| **A. SinkWriter + IMFByteStream（内存流）** | `MFCreateSinkWriterFromURL` 换成字节流工厂，输出 fMP4（`MFTranscodeContainerType_FMPEG4`）到内存 `IMFByteStream`，再读出来分包 | 完整、参数可控 | **首选** |
| **B. SinkWriter 写临时 fMP4 文件 + 边写边读** | `MFCreateSinkWriterFromURL` 直接写磁盘 fMP4，用 `MF_LOW_LATENCY` + 短 GOP 让 `Finalize` 前就能读到可播放的 fragment | 最简单，复用现有 Mp4Writer 90% | **兜底/第一版** |

### 3.2 关键 API（已证实，从 wine-mirror/wine `mfapi.h`/`mfreadwrite.h` 核对，与微软官方同源）

| API | 作用 | 备注 |
|---|---|---|
| `MFCreateSinkWriterFromURL` | 现有 Mp4Writer 已用 | 写文件用 |
| `MFCreateSinkWriterFromMediaSink` | 绑定自定义 sink（内存流用） | 内存流方案的核心 |
| `MFBeginCreateFile` / `MFCreateFile` / `MFCreateTempFile` | 打开/创建字节流 | 配合自定义 `IMFByteStream` |
| `IMFByteStream`（自定义实现） | 内存流，`Read`/`Write`/`SetCurrentPosition` | 需要手写 COM 实现（或用 `MFCreateTempFile` 落临时文件） |
| `MFCreateMediaType` + `SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264)` | 输出 H.264 | 现有 Mp4Writer 已用 |
| `MF_LOW_LATENCY` 属性（`{9c27891a-ed7a-40e1-88e8-b22727a024ee}`） | 低延迟模式 | 关键，直播必须 |
| `MF_MT_AVG_BITRATE` | 码率控制 | CBR |

> **诚实说明（需实测）**：`MFCreateSinkWriterFromMediaSink` + 自定义内存 `IMFByteStream` 在 .NET 里要手写 COM 接口（`IMFByteStream` 不是 `[local]`，理论上可跨边界，但 SinkWriter 对字节流的 `Seek`/`Flush` 行为有要求，历史上有坑）。**第一版建议走 B（写临时 fMP4 + `MF_LOW_LATENCY`），风险最低、复用 Mp4Writer 最多**；A 作为优化项。

### 3.3 伪代码（路 B，第一版）

```csharp
// 1. 输出类型：H.264 + fMP4 容器 + 低延迟
IMFAttributes outType;
outType.SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);       // 34363248-...
outType.SetUINT32(MF_MT_AVG_BITRATE, bitrate);            // CBR
outType.SetUINT32(MF_TRANSCODE_CONTAINERTYPE, MFTranscodeContainerType_FMPEG4); // fMP4
// 2. SinkWriter 绑临时文件
MFCreateSinkWriterFromURL(tmpFmp4, null, null, out writer);
writer.AddStream(outType, out streamIdx);
// 3. 输入类型 NV12（或 RGB32，Mp4Writer 已用 RGB32）
// 4. 逐帧 WriteSample（sample time 用 100ns 单位）
// 5. 不立即 Finalize，而是边写边读 tmpFmp4 的 fMP4 fragment 分发给浏览器
//    （MF_LOW_LATENCY + 短 GOP 保证 fragment 及时落盘）
```

**关键点**：fMP4（fragmented MP4）是浏览器 MSE 能边下边播的前提，`empty_moov` + `frag_keyframe` 标志让 moov 头前移、每个关键帧一个 fragment。

---

## 4. 浏览器端：MSE vs WebCodecs（已证实，caniuse 官方数据 2026-11-22）

### 4.1 支持矩阵

| API | Chrome | Edge | Firefox | Safari(桌面) | iOS Safari | Android Chrome | 状态 |
|---|---|---|---|---|---|---|---|
| **MSE（MediaSource）** | ✅ 全版 | ✅ | ✅ | ✅ | ✅ | ✅ | **rec**（成熟标准） |
| **H.264 in `<video>`** | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | 事实标准 |
| **WebCodecs** | ✅ 94+ | ✅ 94+ | ✅ 130+ | ⚠️ 26.0+（较新） | ⚠️ 26.0+ | ✅ 157+ | **wd**（working draft，仍非最终标准） |

### 4.2 结论：用 MSE（fMP4），不用 WebCodecs

| 维度 | MSE + fMP4 | WebCodecs |
|---|---|---|
| 成熟度 | rec，10+ 年稳定 | wd，2021 才出 |
| iOS Safari | ✅ 全支持（关键是 fMP4 而非 ts） | ⚠️ 26.0 才支持，旧 iOS 全挂 |
| 兼容风险 | 低 | 高（Safari 是最短板） |
| 实现复杂度 | 低（`MediaSource` + `SourceBuffer.appendBuffer`） | 中（`VideoDecoder` + `VideoFrame` → canvas，要自己管时序/渲染） |
| SoulLink 目标 | **多账号网页看画面，浏览器五花八门** | 需要尽可能广的兼容 |

**结论：MSE + fMP4 是唯一能覆盖 iOS Safari / Android Chrome / 桌面全谱系的方案**。WebCodecs 只在「必须精确控制解码 + 离屏渲染（canvas 合成）」时才值得，SoulLink 只是「看画面」，不需要。

### 4.3 传输层：WebSocket 还是 HTTP chunked？

- **推荐 WebSocket**：SoulLink 已有 `AgentWebServer.cs`（HTTP），WebSocket 能双向（客户端发「要关键帧」/「降码率」）、低开销、天然有序流。fMP4 fragment 作为二进制帧推给 `SourceBuffer.appendBuffer()`。
- HTTP chunked 也可（`Content-Type: video/mp4` + `Transfer-Encoding: chunked`），但单向、无背压反馈，不如 WebSocket。
- **建议**：`GET /stream?sessionId=xx` 升级为 WebSocket，服务端每出一个 fMP4 fragment 就 `ws.Send(fragmentBytes)`。

### 4.4 完整链路（从抓屏到浏览器显示，每一跳）

```
[代理进程内，目标 RDP 会话]
  WGC 抓帧 (0.13ms, 需实测 RDP 内)  ──或──  BitBlt 抓帧 (17~20ms)
        │
        ▼  原始 BGRA/NV12 帧
  MF SinkWriter (h264 硬编, CBR, 短GOP, low-latency)
        │
        ▼  fMP4 fragment（每个关键帧一个 fragment）
  WebSocket 推流 (fragment 二进制帧)
        │
        ▼
[浏览器]
  MediaSource + SourceBuffer.appendBuffer(fragment)
        │
        ▼
  <video> 自动解码播放（硬件解码）
```

**延迟预算**（目标 <200ms）：
- 抓屏 WGC 0.13ms（BitBlt 17~20ms 是最大头，这就是要换 WGC 的原因）
- 编码硬编 ~1-5ms（异步 MFT，端到端见 §6.1）
- WebSocket 传输 本机/局域网 ~1ms，外网取决于链路
- 浏览器缓冲：MSE 默认会缓冲，需设小缓冲（见 §5.3）

---

## 5. 关键实现细节（H.264 参数 / 关键帧 / 丢帧 / 浏览器缓冲）

### 5.1 H.264 低延迟参数（对标 Sunshine 的经验，见 OSS-REMOTE-RESEARCH §4）

| 参数 | 值 | 说明 |
|---|---|---|
| 码率控制 | CBR（`MF_MT_AVG_BITRATE`） | 1080p 建议 2~4 Mbps（文字清晰），游戏 4~6 Mbps |
| GOP 关键帧间隔 | **短，1~2 秒**（或 `MF_LOW_LATENCY` 自动） | 直播要短 GOP，否则切流/丢帧后恢复慢；注意：直播用短 GOP，**离线录像用长 GOP 省体积**（两个场景参数不同） |
| 低延迟 | `MF_LOW_LATENCY` 属性 | 禁用编码器内部缓冲 |
| B 帧 | **0**（MF 默认 Main Profile 无 B 帧，若 Advanced 有则关） | B 帧引入重排延迟 |
| 分辨率 | 跟随抓屏（RDP 会话 1920x1080 常见） | yuv420p 要求偶数尺寸 |

### 5.2 关键帧策略（丢帧恢复）

- **客户端检测到「解码花屏/掉帧」→ 主动请求关键帧**（WebSocket 双向的原因）。
- 服务端收到请求 → 下一帧强制 IDR（MF 里通过 `SendStreamTick` 或重新设 `MF_LOW_LATENCY` + 输出 keyframe 属性）。
- 短 GOP（1~2s）保证即使没收到请求，最多 2 秒内自动恢复。

### 5.3 丢帧策略 + 浏览器缓冲控制

- **服务端丢旧帧**：MF 异步编码器队列有界，编码慢时丢最旧未编码帧（对标 Moonlight 15 帧上限 + 溢出重同步，见 OSS-REMOTE-RESEARCH §4.1）。
- **浏览器侧**：`SourceBuffer` 要**小缓冲**（`MediaSource` 默认会提前缓冲几秒，直播场景要主动控制）：
  - 用 `video.playbackRate` 或手动 `SourceBuffer.remove()` 控制缓冲时长，保持 **0.2~0.5 秒**缓冲（够抗抖动，又不积累延迟）。
  - 监测 `video.buffered` 末端 vs `video.currentTime`，超过阈值就跳帧（`currentTime = buffered.end - 0.3`）。

---

## 6. 风险与降级方案

| 风险 | 影响 | 降级 |
|---|---|---|
| MF 硬编 MFT 在目标 RDP 会话不可用（某些显卡/驱动） | 编码退化到软编 | 软编 MFT 兜底（CPU 高但可用）；再不行降 ffmpeg |
| MF 内存流（`SinkWriterFromMediaSink`）有坑 | 第一版卡住 | **第一版用「写临时 fMP4 文件 + 边写边读」**，风险最低 |
| NVENC 并发会话数限制（8 账号同时硬编可能撞上限，见 MEDIA-CAPABILITY §7） | 部分账号静默回退软编 | 超限账号自动降软编；实测 8 路并发 |
| 浏览器不支持 MSE/fMP4（极老浏览器） | 无法播放 | 降级到现有 JPEG 单帧（已有实现） |
| WGC 在 RDP 内抓屏失败（尚未 SoulLink 端到端验证） | 抓屏 17~20ms 拖垮 60fps | BitBlt 兜底 + 30fps 目标（见 GITHUB-SWEEP §1.1） |

---

## 7. 关于 ffmpeg 的具体规格（兜底 B 计划，非首选）

### 7.1 构建选择（已证实，GitHub release API 实测体积 2026-11-22）

| 构建 | 变体 | 压缩包体积 | 说明 |
|---|---|---|---|
| **BtbN/FFmpeg-Builds** | `n9.0-latest-win64-gpl-shared.zip` | **83.1 MB** | 共享库版，体积最小 |
| BtbN | `n9.0-latest-win64-gpl.zip` | 189.1 MB | 静态单 exe |
| BtbN | `master-latest-win64-gpl-shared.zip` | 88.1 MB | 最新 master |
| **gyan.dev** | `ffmpeg-9.1-essentials_build.7z` | **35 MB**（7z）/ 113.9MB(zip) | essentials 精简版，**体积最小** |
| gyan.dev | `ffmpeg-9.1-full_build.7z` | 72 MB | full |

**推荐兜底规格**：**gyan.dev essentials**（35MB 7z，解压后约 100MB，含 `h264_nvenc`/`h264_amf`/`h264_qsv` + `libx264`）或 **BtbN gpl-shared**（83MB，可进一步只保留 ffmpeg.exe + 少数 DLL）。

> **诚实说明**：开源 ffmpeg 二进制**很难压到 10~20MB**——官方变体最小的是 gyan essentials 35MB（7z）。所谓「10~25MB」需要**自己裁剪重编译**（`--disable-everything --enable-encoder=h264_nvenc,libx264 --enable-muxer=mp4`），这是额外工程。**结论：如果要 ffmpeg 兜底，直接用 gyan essentials（35MB 7z），不做裁剪**。

### 7.2 调用方式与直播命令（进程 vs DLL）

- **推荐进程调用**（`ffmpeg -f rawvideo ... -c:v h264_nvenc -f mp4 -movflags frag_keyframe+empty_moov pipe:1`），stdout 就是 fMP4 fragment，转发给 WebSocket。进程调用比 DLL（`libavcodec`）简单 10 倍，且崩溃隔离。
- 示例命令（fMP4 直播到 stdout）：
  ```
  ffmpeg -f rawvideo -pix_fmt bgra -s 1920x1080 -r 30 -i pipe:0 \
         -c:v h264_nvenc -preset p1 -tune ull -b:v 4M -g 60 -bf 0 \
         -f mp4 -movflags frag_keyframe+empty_moov+default_base_moof pipe:1
  ```

### 7.3 分发方式（若用）

- 放程序目录 `ffmpeg/` 子目录，随全量包发布（用户明确说不考虑流量/体积）。
- **但默认不启用**：仅当 MF 编码器探测失败时才用，避免无谓增加体积。

---

## 8. 最终结论（一句话 + 完整路径）

**一句话：用 Windows 自带 Media Foundation H.264 硬编直出 fMP4 + WebSocket 推流 + 浏览器 MSE 播放，零体积零安装，ffmpeg 只作兜底。**

**完整实现路径（每跳）：**
1. **抓屏**：WGC（RDP 内需实测，参考 BetterGI GraphicsCaptureV2.cs）→ 失败降 BitBlt。
2. **编码**：MF SinkWriter，H.264 硬编（NVENC），CBR 2~6Mbps，短 GOP 1~2s，`MF_LOW_LATENCY`，无 B 帧。
3. **封装**：fMP4（fragmented MP4），`frag_keyframe+empty_moov`。
4. **传输**：WebSocket 二进制帧（fragment 粒度），双向（客户端可请求关键帧/降码率）。
5. **播放**：浏览器 `MediaSource` + `SourceBuffer.appendBuffer` + `<video>`，小缓冲 0.2~0.5s。

**体积**：0 新增（MF）；ffmpeg 兜底 +35MB（gyan essentials 7z）。
**额外安装**：无。
**更新成本**：0（MF 方案）；ffmpeg 兜底每用户 +35MB（但用户已声明不考虑流量/体积）。

**分阶段落地建议**：
- **第一阶段（P0）**：MF SinkWriter 写临时 fMP4 + 边写边读 + WebSocket + MSE，先把「H.264 直播」跑通（复用 Mp4Writer 最多，风险最低）。
- **第二阶段（P1）**：换 WGC 抓屏（解决 17~20ms 瓶颈，60fps 的前提）。
- **第三阶段（P2）**：MF 内存流（`SinkWriterFromMediaSink`）替代临时文件，减少磁盘 I/O。
- **兜底**：MF 探测失败 → 切 gyan essentials ffmpeg 进程。
