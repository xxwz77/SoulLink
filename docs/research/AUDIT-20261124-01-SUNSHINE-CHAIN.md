# 对照参考：Sunshine/Moonlight/Parsec 完整串流链路逐跳拆解

> 日期：2026-11-24
> 作者：sunshine-flow（dsh-team `team_mwtjn0d77v8gtr` / member `m_mwtjndol7xi7sy` / task `task_mwtjoc9c88k8z0`）
> 性质：**纯只读调研**。未改任何产品代码/脚本/配置；唯一写入即本文档。
> 任务：把 Sunshine 的 Windows 采集→编码→传输→Moonlight 解码→上屏整条链路的每一跳、每一种低延迟机制写清楚，并逐条对照 SoulLink 现状，指出缺了什么、错在哪一层。
> 证据分级：
> - 【开源源码·本地快照】＝ `artifacts/zero-copy-src/` 与 `artifacts/latency-playbook-src/` 中抓取的 Sunshine/Moonlight-common-c/OBS master 源码（file:line 可回查；GitHub 路径已用 GitHub API 复核存在，见 §6 URL）
> - 【代码事实】＝ SoulLink 当前工作区源码逐行可证（本文全部行号为 2026-11-24 工作区实际行号）
> - 【官方口径】＝厂商官方文档（Parsec 支持页等）
> - 【已有实测】＝本仓库 `docs/research/` 前序任务实测，附出处
> - 【估计】＝量级推算，明确标注，不编造
> - 【待验证】＝无法离线证实，明确标注

---

## 0. 结论摘要

1. **Sunshine 低延迟不是单一魔法，是「数据永远不上 CPU + 帧到达即编码 + 按需 IDR/RFI 控制回传 + 客户端有界队列丢旧」四条链路的组合**。SoulLink 已对齐两条半（编码参数全套 ✅、输入解耦 ✅、客户端丢旧半个 ✅），**结构性缺失两条**：GPU 直通整条链（当前真机未生效，444ms 元凶）与「帧到达即编码」节拍（captureMs=42ms 的元凶）。
2. **444ms 归因按层**：服务端节拍税（16ms DDA 轮询超时 + 33ms 节拍等待 + CPU 读回 42ms/帧）占大头；传输是 TCP chunked 但局域网只 0.8ms 不咬人；浏览器端 WebCodecs + canvas 无法做 vsync 对齐的 pacing 是「最后一英里」，Parsec 官方明确 web 客户端拿不到硬件解码/低层优化。**先修服务端两层，再谈浏览器**。
3. **对 P0（零拷贝未生效）的关键判断**：SoulLink 零拷贝卡点在 `GpuColorConverter` 依赖 `ID3D11VideoDevice`（RDP 会话内可用性存疑，handoff 已列为头号嫌疑）。**Sunshine 根本不用 VideoProcessor**——它用像素着色器两 pass 做 BGRA→NV12（display_vram.cpp:494-563），且 DDA duplication 与 NVENC 建在**同一个 ID3D11Device** 上，完全没有跨设备共享句柄这一步。这印证前序 DDA-TEXTURE-SHARED-HANDLE-20261124 的「路线A 同 device」判断：**SoulLink 现方案（跨 device 共享句柄 + VideoProcessor）比 Sunshine 多两个高风险环节，路线A 才是对齐 Sunshine 的正解**。
4. **「虚拟鼠标蓝球」问题的业界正解**：Sunshine 把光标**画进视频帧**（GPU shader 合成，display_vram.cpp:1663-1685），且「光标动了但桌面没更新也推一帧」（:1488-1506）——光标即画面的一部分，永远可见、零额外延迟。SoulLink 在网页端画球是「画面外光标」路线，用户已认定废功能；正确方向是服务端把光标后合成进帧（DDA 的 `frame_info.PointerShapeInfo` 已随帧返回，DxgiDdaCapture.cs:176 已拿到 frameInfo 只是没用）。
5. **不可照抄但必须知道的硬边界**：UDP+FEC+RTP 换不成（浏览器不能裸 UDP，只能 WebTransport/WebRTC）；RFI 换不成（WebCodecs 无参考帧失效语义，只能完整 IDR）；Moonlight 的硬解 swapchain 直渲换不成（浏览器只有 canvas+rAF）。但**这些在局域网场景都不是 444ms 的构成项**，先不做。

---

## 1. Sunshine→Moonlight 端到端跳数表（每跳：做什么 / API / 量级 / 如何被优化掉）

> 采集与编码为 **GPU 路径**（Sunshine 官方推荐 dxgi 捕获 + nvenc 编码的组合，全程数据不出显存）。
> 量级栏：未注明处为该跳本身耗时；「被优化掉」指该跳的延迟被哪种机制吸收或消除。

| # | 跳 | 做什么 | API/实现 | 耗时量级 | 如何被优化掉 | SoulLink 现状（file:line） |
|---|---|---|---|---|---|---|
| 1 | 线程分工 | 采集线程与编码线程**分离**：采集线程阻塞等新帧，编码线程独立消费 | `captureThread`/`captureThreadSync`（sunshine-video.cpp:1778, 3139, 4084 `std::jthread`）+ `encode_run`（:2707） | 并行化收益，非单跳耗时 | 采集慢不阻塞编码；编码慢不丢新帧（帧池缓冲） | **单线程串行**：`Thread Loop`（SessionRecorder.cs:332）内抓屏→转换→编码→写 viewer 队列全部串行，抓屏 42ms 直接吃掉节拍预算 |
| 2 | 线程桌面绑定 | 把采集线程绑到输入桌面（窗口站隔离下 DuplicateOutput 的前提） | `OpenInputDesktop`+`SetThreadDesktop`（display_base.cpp:86,114,423,1078） | 一次性，µs 级 | 无 | ✅ 同款：`SyncThreadDesktop`（DxgiDdaCapture.cs:148-158） |
| 3 | 采集：拿桌面合成帧 | DDA 阻塞取帧；静止时 WAIT_TIMEOUT；模式切换 ACCESS_LOST→reinit | `IDXGIOutputDuplication::AcquireNextFrame(timeout)`（display_base.cpp:156） | 帧到达即返回（新帧时 ≈0.13ms GPU 复制，【已有实测】DXGI-DDA-RDP-20261123.md） | **阻塞等帧＝到达即编码**：组首用 **200ms 大超时**（:307-308），帧来了立刻返回，没有「等节拍」税 | ⚠️ 同 API 但用法不同：`AcquireNextFrame(16, …)`（DxgiDdaCapture.cs:176）**16ms 短超时轮询** + `WaitUntilNextSlot`（SessionRecorder.cs:739-746）33ms 节拍门——静止时每 16ms 空转一次，新帧平均要多等 ~8ms+节拍残差；**缺失 200ms 阻塞等帧语义** |
| 4 | 采集节拍：frame pacing group | 组首拿带时间戳的帧 → 组内按目标 fps 精确睡到下一拍 → **0ms 超时非阻塞取帧**（到达即编码）；错过节拍**弃组重来**不累积漂移 | `display_base_t::capture`（display_base.cpp:225-338，组逻辑 :265-311） | 0（睡眠只发生在桌面无新帧时） | 落后弃组 + 绝对时间 `sleep_target`（:280-291） | ⚠️ 已改绝对时刻门（A2，SessionRecorder.cs:420-428 注释+739-746），但**无「组内 0ms 非阻塞取帧」**：到拍才抓一次，拍内第 2 帧直接丢（DDA 16ms 轮询内新帧可被覆盖） |
| 5 | 采集线程 GPU 优先级/帧延迟 | 提高采集线程 GPU 调度优先级；限制 GPU 队列为 1 帧 | `SetGPUThreadPriority(7)`（display_base.cpp:718）、`SetMaximumFrameLatency(1)`（:733） | 减少排队抖动（µs~ms 级） | 帧在 GPU 队列最多排 1 帧 | **缺失**（全仓无这两个调用） |
| 6 | GPU 拷贝：DDA 临时纹理→私有纹理 | DDA 的帧纹理是临时的（ReleaseFrame 后失效），先拷到私有纹理再 Release | `CopyResource`（display_vram.cpp snapshot 内，`copy_last_surface_and_blend_cursor` 路径 :1464-1506） | 显存内拷贝 1080p BGRA ≈0.04ms【估计】（~8MB @ 200GB/s） | 与编码同 device，零拷贝语义 | ⚠️ 零拷贝分支同款：`CopyResource`（DxgiDdaCapture.cs:292）；但 CPU 主路径是 `ReadBackTexture` staging+Map 读回（:336-380）≈42ms 级（【已有实测】handoff captureMs=42） |
| 7 | 光标合成 | 把系统光标**画进视频帧**：DDA 帧自带 `PointerShapeInfo`，转成 ARGB 纹理后用 cursor VS/PS 着色器 blend 到画面；**光标动但桌面没变也推一帧** | `make_cursor_alpha/xor_image`（display_vram.cpp:280-426）→ `set_cursor_texture`（:1322-1362）→ `blend_cursor`（:1663-1685）；cursor-only 帧路径注释（:1488-1506）；WGC 路径 `set_cursor_visible`（:1860-1863） | GPU 上 µs 级 | 光标=画面一部分，永不消失、零额外链路 | **缺失**。DDA 的 `frameInfo` 已拿到（DxgiDdaCapture.cs:176）但 `PointerShapeInfo` 未用；网页端画「虚拟鼠标球」是画面外方案且随本地指针显隐（用户认定废功能） |
| 8 | 色彩转换 BGRA→NV12 | GPU **像素着色器**两 pass（Y pass + UV pass）渲染到 NV12 双平面 RTV | `convert()`（display_vram.cpp:494-563；NV12 shader 选择 :643-655）；输入纹理 `D3D11_BIND_RENDER_TARGET`（nvenc_d3d11_native.cpp:38-50） | <1ms【估计】（两个全屏三角形） | 着色器在 D3D11 硬件 device 上，RDP 会话内可用（无 VideoProcessor 依赖） | ⚠️ 双重问题：① CPU 主路径 `BgraToNv12` 逐像素（SessionRecorder.cs:849-878，实测 convertMs 3~6ms 级）；② 零拷贝分支用 **VideoProcessorBlt**（GpuColorConverter.cs:128-171）而非着色器——`ID3D11VideoDevice` 在 RDP 会话内不可用是 P0 头号嫌疑 |
| 9 | 编码输入注册 | NV12 纹理直通 NVENC（不读回、不上传 CPU） | `NvEncRegisterResource(DIRECTX)`（nvenc_d3d11_native.cpp:53-68） | 一次性 | 编码器直接读显存 | ✅ 同款：`NvEncRegisterResource`（NvencSdkEncoder.cs:601-613）——**问题不在这一跳，在输入纹理怎么来**（第 8 跳） |
| 10 | 编码参数（低延迟四件套） | zerolatency + 关 B 帧 + 无限 GOP 按需 IDR + CBR | nvenc options：`delay=0, forced-idr=1, zerolatency=1, surfaces=1, cbr_padding=false, tune=ULTRA_LOW_LATENCY, rc=CBR`（sunshine-video.cpp:944-954）；`gopLength=INFINITE, frameIntervalP=1, zeroReorderDelay=1, lookahead=0, lowDelayKeyFrameScale=1, CBR, vbv=bitrate/fps`（nvenc_base.cpp:258-281）；libavcodec 通用 `max_b_frames=0`（sunshine-video.cpp:2336）、`gop_size=INT_MAX` 注释「I-frames are generated on demand」（:2347-2350） | NVENC 1080p P3 ≈1~5ms【估计，上限锚点=Parsec 官方 encode<15ms@60fps】 | 输入 1 帧→输出 1 帧，编码器内部零缓冲零重排 | ✅ **已对齐**（本链路少有的全对齐项）：无限 GOP :555、关 B 帧 :556、CBR :557-559、zeroReorderDelay :560、lowDelayKeyFrameScale :561、单 pass :562、vbv=bitrate/fps :563、ULTRA_LOW_LATENCY :579、FORCEIDR|OUTPUT_SPSPPS 按需 IDR :674-676 |
| 11 | 编码执行 | Map→EncodePicture→LockBitstream（同步或异步） | nvenc_base.cpp:715-789 | 即第 10 跳量级 | — | ✅ 同款同步模式：Map :662-666 / Encode :685 / Lock(doNotWait=0) :688-693 |
| 12 | 按需 IDR / RFI 控制回传 | 客户端可随时要求服务端「下一帧强制 IDR」或「失效参考帧」（RFI，比 IDR 轻） | 控制通道：`IDX_REQUEST_IDR_FRAME`（sunshine-stream.cpp:46, 1221-1225 → raise `idr_events`）、`IDX_INVALIDATE_REF_FRAMES`（:40, 1227-1237）、`IDX_LOSS_STATS`（:41, 1205） | 1 帧内恢复 | 花屏/丢帧恢复从「等自然 IDR」缩短到 1 帧；RFI 进一步省带宽 | ⚠️ 半套：服务端有 `RequestKeyFrame`（SessionRecorder.cs:714-718）和 NVENC 精确按需 IDR（NvencSdkEncoder.cs:778），但**触发源只有服务端**（新观众 :686 / 500ms nudge AgentWebServer.cs:423-427）；**网页端无 IDR 请求回传通道**（reset 后干等，SKIP_RESET_LIMIT 到点断流重连）；**RFI 缺失**（NVENC API 入口其实已在 function list，NvencSdkEncoder.cs:401，未调用） |
| 13 | 传输：UDP+RTP+Reed-Solomon FEC | 视频走独立 UDP socket，每帧切块后算校验块同发，容忍丢包不重传 | `udp::socket video_sock`（sunshine-stream.cpp:491）；`fec::encode` Reed-Solomon（:873-945）；广播线程每帧 FEC 分片发送（:1685-1906） | 局域网 <1ms【已有实测：SoulLink TCP 0.8ms，LATENCY-BENCHMARK §1.1⑥】 | 丢包靠 FEC 恢复，**绝不触发 TCP 重传队头阻塞** | ⚠️ **裸 HTTP chunked TCP**：`SendChunked` + 逐帧 `WriteAsync/FlushAsync`（AgentWebServer.cs:442-457），无帧边界元数据、无 FEC、无丢帧语义；局域网不咬人（0.8ms），**公网是硬伤，但换 WebTransport 才能解**（见 §3 抄不了的 #1） |
| 14 | 客户端收包/乱序重排 | 等 10ms 让乱序/迟到包到齐，超时即判定丢包 | `RTP_QUEUE_DELAY=10`（mlcc_VideoStream.c:26）；`RTP_RECV_PACKETS_BUFFERED=2048`（:35） | 0~10ms | 等待窗口有界，不无限等 | **无对应层**（TCP 保序无乱序问题；代价是没有丢包语义） |
| 15 | 客户端 FEC 恢复 / 投机 RFI | 丢包先用校验块恢复；恢复不了且预测会丢帧 → **投机 RFI** 提前让服务端失效参考帧 | `reed_solomon_decode`（mlcc_RtpVideoQueue.c:261-332）；`notifyFrameLost(speculative=true)`（:215-221, :1245-1256 见 VideoDepacketizer） | 0~10ms（仅丢包时） | 避免「解码出花屏后才发现」的滞后恢复 | **缺失**（TCP + WebCodecs 无此语义） |
| 16 | 客户端解包/有界队列 | 解码单元队列上限 **15 帧**；满则整队 flush + 请求 IDR；连丢 120 帧强制 IDR；**RFI 优先于 IDR**；拥塞期**不**请求 IDR（防拥塞崩溃） | `LbqInitializeLinkedBlockingQueue(&decodeUnitQueue, 15)`（mlcc_VideoDepacketizer.c:94）；`CONSECUTIVE_DROP_LIMIT 120`（:56, :155-164）；RFI 优先（:139-148）；flush+IDR（:537-564）；拥塞注释（:1184-1194） | 0（常态）/ 一次 IDR 恢复 | 队列有界 → 解码延迟有界；丢旧保新 | ⚠️ 同构但参数极端：网页 `SKIP_QUEUE=2`（web-index.html:1162）比 Moonlight 的 15 激进 7.5 倍、`SKIP_RESET_LIMIT=10`（:1163）比 120 激进 12 倍；服务端每 viewer `Pending` 上限 4（SessionRecorder.cs:471, 627）；重同步只有「断流重连」没有 RFI 档 |
| 17 | 客户端解码 | FFmpeg 硬件解码（D3D11VA/DXVA2/VAAPI/VideoToolbox），独立 decoder 线程 | moonlight-qt `ffmpeg.cpp`（§6 URL）；`decoderThread`（mlcc_VideoStream.c:17） | 2~6ms【估计，上限锚点=Parsec 官方 decode<15ms@60fps】 | 硬解 + 独立线程，不占渲染线程 | ⚠️ 浏览器 WebCodecs（`optimizeForLatency:true`，web-index.html:1250）：硬件与否取决于浏览器实现，**不可控**；Parsec 官方明言 web 端拿不到硬件解码（§4） |
| 18 | 上屏/pacing | vsync 对齐上屏 + 帧节奏器；可配「最低延迟」策略 | moonlight-qt swapchain 渲染 + pacing（PR #1139「enforce a minimum latency to improve frame pacing」，§6 URL） | 0~16.7ms（60Hz vsync） | 与刷新率对齐：不早到（撕裂/抖动）不迟到 | ⚠️ 浏览器 `ctx.drawImage(frame,0,0)`（web-index.html:1101）走 rAF 上屏；**无 vsync 对齐、无迟到丢帧控制**，浏览器帧管线税是 453ms 回环中客户端 1/3 的来源（【已有实测】WEB-VIEWER-LATENCY-GAP-20261123.md） |
| 19 | 输入：相对鼠标 | 客户端把一段时间的移动**合并累加**成相对位移包，独立控制通道直发服务端 | `NV_REL_MOUSE_MOVE_PACKET`（sunshine-input.cpp:483-488, 698）；服务端 batch 合并（:1878-1883, 2131）；`send_relative_mouse_packet`（:2594-2595） | 网络 RTT 主导 | 输入绝不走视频链路；合并=高频移动不丢、不打爆队列 | ✅ **已对齐**：独立 `POST /api/input`（AgentWebServer.cs:294-298）+ `moverel`（:512）+ 网页 `lookDelta` 8ms 合并（web-index.html:1651-1660）+ Pointer Lock movementX/Y（:1492-1498, 1614） |
| 20 | 输入：断线释放 | 会话断开时重置所有按键/鼠标状态（防「卡键」） | `reset_input_state`（sunshine-input.cpp:2399-2413） | — | 断线即释放，防幽灵按键 | **缺失**（前序 INPUT-CHANNEL-GAP-20261123.md 结论：HTTP 断开不触发 releaseall）；另：agent 坐标换算用 `GetSystemMetrics`（AgentWebServer.cs:495-498）而非实际抓屏尺寸，P2 已知 |

> 表内 SoulLink 行号全部为 2026-11-24 工作区实际行号（前序文档的行号已因 GPU 零拷贝/首帧修复提交而漂移，勿直接沿用旧文档行号）。

---

## 2. 关键机制逐条展开（Sunshine 证据 vs SoulLink 证据）

### 2.1 采集节拍：为什么 Sunshine 的「到达即编码」没有 33ms 税

- Sunshine 组首 `snapshot(…, 200ms, …)` **大超时阻塞等新帧**（display_base.cpp:307-308）：桌面一更新，AcquireNextFrame 立刻返回，**采集等待时间 = 桌面帧间隔，而不是固定节拍**。组内用 0ms 超时取帧（:295），错过节拍直接弃组（:285-289），绝不累积漂移。
- SoulLink `WaitUntilNextSlot`（SessionRecorder.cs:739-746）是**绝对时刻门**：到拍才抓。DDA 内部 `AcquireNextFrame(16)` 又是 16ms 轮询。二者叠加：桌面更新发生在节拍刚过时，要等满一个 33ms 周期；静止桌面则每 16ms 空转。这正是 captureMs=42ms 与「H2 定时轮询 16-33ms/帧」的构成（【已有实测】AGENT-PIPELINE-LATENCY-GAP-20261123.md H2）。
- **改法（最小）**：DDA 后端把「节拍门 + 16ms 轮询」换成「`AcquireNextFrame(剩余节拍时间)` 单次阻塞」：帧来了立即编码（到达即编码），超时说明桌面静止（可复用旧帧或跳过）。这等价于 Sunshine 组首语义，不用引入 pacing group 全量重写。注意 DDA 静止时持 D3D11 不公平锁，Sunshine 在超时后 `sleep_for(10ms)` 让锁（display_base.cpp:320-337），SoulLink 单线程下无编码线程竞争，但若后续分线程必须补。

### 2.2 GPU 直通：Sunshine 的「同 device」与 SoulLink 的「跨 device + VideoProcessor」

数据流对照（均为 GPU 路径）：

| 环节 | Sunshine | SoulLink（当前代码） |
|---|---|---|
| DDA 建立在哪个 device | **编码器的 device**（`DuplicateOutput(device)`，display_base.cpp:113-115，device 即后续 NVENC 会话的 device） | 独立新建的 device（DxgiDdaCapture.cs:90-97） |
| NVENC 会话在哪个 device | 同一个 device（nvenc_d3d11_native.cpp:53-68 RegisterResource 直用） | 另一个独立 device（NvencSdkEncoder.cs:514-526） |
| 纹理跨设备 | **不需要**（同 device 天然共享） | 跨 device：DDA 拷到 `Shared` 纹理拿 `SharedHandle`（DxgiDdaCapture.cs:275-297）→ `OpenSharedResource` + `CopyResource`（GpuColorConverter.cs:191-206） |
| BGRA→NV12 | **像素着色器两 pass**（display_vram.cpp:494-563） | **VideoProcessorBlt**（GpuColorConverter.cs:128-171） |

结论：
1. SoulLink 现方案比 Sunshine 多 **2 个高风险环节**（跨 device 共享句柄、VideoProcessor 初始化），而这两个正是 handoff 里 P0 的三个嫌疑卡点之二（「VideoProcessor 不可用」「Shared 句柄拿不到」）。
2. Sunshine 的着色器路线在 RDP 会话内可用的概率远高于 VideoProcessor（着色器只要 D3D11 硬件 device + RenderTarget 纹理即可；VideoProcessor 依赖 ID3D11VideoDevice，RDP 会话内不可用是 GpuColorConverter.cs:21 注释自己承认的已知风险）。
3. **路线A（前序 DDA-TEXTURE-SHARED-HANDLE-20261124.md 已论证）= 编码器先建 device → DDA 用该 device 建 DuplicateOutput → 着色器转 NV12 → 直接 EncodeD3D11Texture**。这是唯一与 Sunshine 同构的路线，应作为零拷贝下一轮首选；「修 VideoProcessor」是次选。
4. 提示：NVENC 输入纹理 `BindFlags` 现为 RenderTarget（NvencSdkEncoder.cs:792），着色器路线可直接复用（把 VideoProcessor 的写入换成两 pass PS 即可，不改注册/编码逻辑）。

### 2.3 光标：业界唯一正解是「画进帧」，SoulLink 错在层

- Sunshine：DDA 每帧自带 `DXGI_OUTDUPL_FRAME_INFO.PointerShapeInfo/PointerPosition`，光标图在 **GPU 上用 cursor shader blend 进画面**（display_vram.cpp:1322-1362, 1663-1685）；光标单独移动时（桌面帧没更新）**也推一帧带光标的画面**（:1488-1506 注释：*"in case the mouse cursor appears without a new frame from DesktopDuplication"*）。WGC 路径直接 `set_cursor_visible`（:1860-1863）。
- SoulLink：视频帧里**没有光标**；网页端画「蓝色虚拟鼠标球」且随本地指针显隐（用户认定废功能）。`DxgiDdaCapture.CaptureBgra` 已拿到 `frameInfo`（DxgiDdaCapture.cs:176）但只用了 `AccumulatedFrames`（:196），`PointerShapeInfo` 完全没用——**素材已在手上，缺的是合成这一步**。
- 【待验证】RDP 会话（session 4）内 DDA 的 PointerShapeInfo 是否返回有效光标形状（RDP 会话桌面合成器可能不产光标元数据）。若有效：后合成进帧（GPU shader 或 CPU 路径的字节级合成均可，CPU 路径下成本 <1ms）；若无效：保持网页端画但改为「由输入状态驱动显示、与本地指针显隐解耦」。此验证应与 P0 日志排查并行做。

### 2.4 编码参数：已对齐，但注意 MF 软编路径仍裸奔

- NVENC SDK 路径 ✅ 全套对齐（表 #10）。**444ms 不在这层**（encodeMs 已不是大头）。
- MF 降级路径仍无低延迟参数（微软软 MFT 对 ICodecAPI 写 S_OK 但忽略，【已有实测】MF-LOWLATENCY-20261123.md）。真机若零拷贝未生效且 NVENC SDK 又降级，则参数层也失守——排查 P0 时必须同时确认日志里 `编码器=` 行的实际值（SessionRecorder.cs:590-591）。

### 2.5 传输与重同步：TCP 不换，但「IDR 请求回传」必须补

- Sunshine 的三类控制回传（IDR/RFI/LOSS）里，**IDR 请求是 SoulLink 唯一能在现有 HTTP 架构上低成本对齐的**：加一个 `POST /api/request-keyframe`（或复用现有鉴权）→ `SessionRecorder.RequestKeyFrame()`（已存在，SessionRecorder.cs:714-718）。网页端在 `decoder.reset()` 后主动调它，替代现在的「SKIP_RESET_LIMIT=10 → 断流重连」（web-index.html:1177-1179 重连成本远高于一帧 IDR）。
- RFI 不可抄（WebCodecs 无此语义），但 NVENC 侧 `nvEncInvalidateRefFrames` 入口已在 function list（NvencSdkEncoder.cs:401），**安卓 MediaCodec 路线将来可做**（MediaCodec 有 forward 语义局限，需验证）。
- FEC/UDP：见 §3 抄不了的 #1。

---

## 3. SoulLink 与 Sunshine 的结构性差异：可抄的 vs 抄不了的

### 3.1 可抄的（同构或低成本对齐，按 ROI 排序）

| # | 抄什么 | 证据 | 成本/风险 |
|---|---|---|---|
| 1 | **路线A：DDA/NVENC 同 device + PS 着色器转 NV12**（替换跨 device 共享句柄 + VideoProcessor） | display_base.cpp:113-115；display_vram.cpp:494-563 | 中（动 SessionRecorder/DxgiDdaCapture/NvencSdkEncoder 三处接线 + HLSL）；**零拷贝能否生效的关键** |
| 2 | **200ms 阻塞等帧语义**（DDA 后端用单次 `AcquireNextFrame(剩余节拍)` 替代 16ms 轮询 + 节拍门） | display_base.cpp:307-308 | 低（<20 行）；直接砍 captureMs 42 的大头 |
| 3 | **光标后合成进帧 + cursor-only 帧推送** | display_vram.cpp:1322-1362, 1488-1506 | 中（先做 §2.3 的可用性验证）；顺带解决「虚拟鼠标」痛点 |
| 4 | **IDR 请求回传通道** | sunshine-stream.cpp:46, 1221-1225 | 低（新端点 + 网页两处调用） |
| 5 | **SetMaximumFrameLatency(1) + SetGPUThreadPriority(7)** | display_base.cpp:718, 733 | 低（两行 API；RDP 会话内 GPU 优先级是否生效【待验证】，失败无害） |
| 6 | 客户端丢帧阈值从 SKIP_QUEUE=2 放宽到接近 Moonlight 语义（如 5~8）+ 触发按需 IDR 而非断流重连 | mlcc_VideoDepacketizer.c:94, 559-563 | 低（网页端参数+流程） |
| 7 | **输入断线 releaseall + 坐标换算改用实际抓屏尺寸** | sunshine-input.cpp:2399-2413；AgentWebServer.cs:495-498 | 低（已有 P2 规划） |
| 8 | 服务端 viewer Pending 上限 4 与 Moonlight 15 对齐评估（低频场景 4 已够，60fps 时 4 会误丢） | mlcc_VideoDepacketizer.c:94 | 低（参数） |

### 3.2 抄不了的（架构不兼容，明说不做，避免再走弯路）

| # | 抄什么 | 为什么不兼容 | 后果与替代 |
|---|---|---|---|
| 1 | **UDP + RTP + Reed-Solomon FEC** | 浏览器 JS **不能开裸 UDP socket**；WebCodecs 收的是 chunked 字节流不是 RTP | 局域网 TCP 0.8ms 不咬人；**公网要升级只能 WebTransport(QUIC)/WebRTC**，那是一次协议层重构，不是抄 Sunshine 的 FEC 代码 |
| 2 | **RFI 参考帧失效** | WebCodecs 解码器无「invalidate reference frames」语义，只能完整 IDR | 用按需 IDR 替代；安卓 MediaCodec 路线可再评估 NVENC `nvEncInvalidateRefFrames`（NvencSdkEncoder.cs:401 已有入口） |
| 3 | **Moonlight 硬解 swapchain 直渲 + vsync 对齐 pacing** | 浏览器只有 canvas + rAF，无 vsync 对齐 API、无迟到丢帧控制 | 这是「网页端永远到不了原生延迟」的硬边界（Parsec 官方同款结论）；**安卓 APK MediaCodec+SurfaceView 是对标 Moonlight 5-12ms 的唯一路径**（已在 P3 规划） |
| 4 | **Parsec BUD 协议 / 虚拟显示驱动** | 闭源；VDD 要装驱动（违反本团队铁律「不改系统状态」） | 只做架构事实参考（§4） |
| 5 | **采集/编码双线程** | SoulLink 单线程 Loop 是现状架构 | **不是不可抄，是要动 Loop 结构**（Ring 缓冲已存在，天然适合生产-消费解耦）。若路线A 后 captureMs 仍 >10ms 再考虑 |
| 6 | **Sunshine 的「同 device」** | SoulLink 当前 DDA 与 NVENC 各自建 device 是代码结构事实 | 可抄（§3.1 #1），但属于架构改造非参数调整 |

---

## 4. Parsec 对照（闭源，只有官方口径与公开事实）

- **延迟阈值口径**：60fps 下 encode/decode 各自 **>15ms 即告警**，30fps 阈值 32ms；网络 <30ms 好、<60ms 可接受（[Parsec 官方支持页](https://support.parsec.app/hc/en-us/articles/32381352822804-Troubleshooting-Lag-Latency-and-Quality-Issues)）。可作为 SoulLink 的分段验收标尺。
- **软件编码红线**：主机用不上硬件编码器时显式红字警告（同上页）。SoulLink 的 NVENC→MF→软编降级链应把「实际编码器」做成同样显眼的 UI 状态（StatusText 已有 `encoder.Name`，SessionRecorder.cs:590，但用户界面是否可见需确认）。
- **Web 客户端硬边界（对 SoulLink 最重要的一条）**：Parsec 官方明确——浏览器客户端 **decode 显示 N/A，web 客户端拿不到底层硬件优化和硬件解码**，官方建议下载原生应用（同上页）。这给「SoulLink 网页端 + 安卓 APK」的组合路线提供了厂商级背书：**网页端是通用入口，低延迟体验必须靠原生端**。
- **丢包可视**：控制台 `N:0/0/0` 丢包计数（同上页）——SoulLink 无丢包统计通道（TCP 下也无此概念，换 WebTransport 时补）。
- **4:4:4 高负载**：4:4:4 色度会显著加重负载，官方建议关（同上页）——SoulLink 用 NV12 4:2:0，无此问题。
- **P2P 直连**：流量不经服务器（同上页）。
- **parsec-vdd**：开源虚拟显示器驱动（https://github.com/parsec-cloud/parsec-vdd），本仓库已有 VDD-RESEARCH-20261122.md 深研，此处不重复。
- 【未核实，明确标注】Parsec BUD 协议细节（UDP/FEC 具体实现）无公开源码与文档，本文不引用任何「BUD 如何做」的二手描述。

---

## 5. 给后续任务的执行建议（按优先级）

1. **P0 排查（与 handoff 一致，本审计给出加速方向）**：真机触发观看 → 查日志三选一（`零拷贝色差 PSNR` / `零拷贝失败，降级 CPU` / `GPU 转换器不可用`，SessionRecorder.cs:489/505/586 三条埋点）。**本审计预判：卡点极大概率是 `GPU 转换器不可用`（RDP 内无 ID3D11VideoDevice）→ 结论不是「修 VideoProcessor」而是「直接上路线A 着色器」**（§3.1 #1），这是 Sunshine 同构做法，跳过共享句柄与 VideoProcessor 两个环节。
2. **P0.5（低风险高收益，可与 P0 并行）**：DDA 后端改 200ms 阻塞等帧语义（§3.1 #2）；即使零拷贝没生效，这一步应能把 captureMs 42 显著压下来。
3. **P1**：IDR 请求回传（§3.1 #4）+ 网页端 SKIP 参数与重同步策略对齐（§3.1 #6）。
4. **P1.5**：光标合成进帧的可行性验证（§2.3，DDA frameInfo.PointerShapeInfo 在 session 4 是否有值）——决定「虚拟鼠标」是「移除」还是「重做进帧」。
5. **P2**：输入断线 releaseall + 坐标换算（§3.1 #7）。
6. **明确不做**：UDP/FEC 重写、RFI、浏览器端 pacing 三件事在现有架构下无解（§3.2），不要再排期。

---

## 6. 证据与引用 URL 索引

### SoulLink（本仓库，行号为 2026-11-24 工作区实际行号）
- `Relay/SessionRecorder.cs`（Loop 单线程 :332、A2 节流 :420-428、零拷贝分支 :429-514、CPU 路径 :516-646、viewer Pending :471/:625-628、WaitUntilNextSlot :739-746、IdleWait :753-756、BgraToNv12 :849-878、RequestKeyFrame :714-718）
- `Relay/DxgiDdaCapture.cs`（device :90-97、DuplicateOutput :104-131、syncThreadDesktop :148-158、AcquireNextFrame(16) :176、frameInfo :176/:196、TryCaptureBgraTexture :253-314、Shared 纹理/句柄 :275-297、ReadBackTexture :336-380）
- `Relay/WgcCapture.cs`（FrameSequence :45、TryCaptureBgraTexture :326）
- `Services/NvencSdkEncoder.cs`（D3D11 device :514-516、OpenEncodeSessionEx :518-526、预设+rcParams :533-565、ULTRA_LOW_LATENCY :579、RegisterResource :601-613、EncodeNv12 CPU 上传 :644-710、EncodeD3D11Texture :717-775、RequestKeyFrame :778、RFI 入口 :401）
- `Services/GpuColorConverter.cs`（VideoDevice QI :66-69、Convert/VideoProcessorBlt :128-171、ConvertFromSharedHandle :178-215）
- `Relay/AgentWebServer.cs`（/stream :273-292、WriteStreamAsync :404-467、500ms IDR nudge :423-427、/api/input :294-298、HandleInputAsync :483-545、GetSystemMetrics 换算 :495-498）
- `docs/web-index.html`（drawImage :1101、stallTimer :1131、SKIP_QUEUE=2 :1162、SKIP_RESET_LIMIT=10 :1163、reset+断流重连 :1177-1179、optimizeForLatency :1250、Pointer Lock :1492-1498、lookDelta/moverel :1651-1660）
  > 注：`Services/WebPages.cs` 内嵌同款网页代码作兜底（WebPages.cs:6 注明由 web-index.html 生成，主程序优先读 exe 同目录文件），行号偏移（如 SKIP_QUEUE 在其 :1178），**改代码只改 web-index.html，引用行号以本文为准**。

### Sunshine（master，GitHub API 复核存在；本地快照 `artifacts/zero-copy-src/`、`artifacts/latency-playbook-src/`，GPL-3.0 只读）
- display_base.cpp（pacing group / AcquireNextFrame / syncThreadDesktop / SetGPUThreadPriority / SetMaximumFrameLatency）：https://github.com/SunshineStream/Sunshine/blob/master/src/platform/windows/display_base.cpp
- display_vram.cpp（PS 转 NV12 / GPU 光标合成 / cursor-only 帧）：https://github.com/SunshineStream/Sunshine/blob/master/src/platform/windows/display_vram.cpp
- video.cpp（编码参数 / gop / 线程）：https://github.com/SunshineStream/Sunshine/blob/master/src/video.cpp
- stream.cpp（UDP / FEC / 控制回传）：https://github.com/SunshineStream/Sunshine/blob/master/src/stream.cpp
- input.cpp（相对鼠标 / batch / reset_input_state）：https://github.com/SunshineStream/Sunshine/blob/master/src/input.cpp
- nvenc_base.cpp / nvenc_d3d11_native.cpp（低延迟参数 / RegisterResource）：https://github.com/SunshineStream/Sunshine/tree/master/src/video/nvenc

### Moonlight
- moonlight-common-c VideoStream.c / VideoDepacketizer.c / RtpVideoQueue.c / LinkedBlockingQueue.c：https://github.com/moonlight-stream/moonlight-common-c/tree/master/src
- moonlight-qt ffmpeg.cpp（硬解渲染）：https://github.com/moonlight-stream/moonlight-qt/blob/master/app/streaming/video/ffmpeg.cpp
- moonlight-qt frame pacing（PR #1139）：https://github.com/moonlight-stream/moonlight-qt/pull/1139

### Parsec（官方口径）
- Troubleshooting Lag, Latency and Quality Issues（encode/decode <15ms@60fps、web 客户端无硬件解码、N:0/0/0、4:4:4、P2P）：https://support.parsec.app/hc/en-us/articles/32381352822804-Troubleshooting-Lag-Latency-and-Quality-Issues
- parsec-vdd（开源虚拟显示器驱动）：https://github.com/parsec-cloud/parsec-vdd

### 本仓库前序文档（结论继承，行号以本文为准）
- `MATURE-LATENCY-PLAYBOOK-20261123.md`（五方面 playbook）、`MATURE-GPU-ZERO-COPY-EXPERIENCE-20261124.md`（主机 GPU 路径经验）、`DDA-TEXTURE-SHARED-HANDLE-20261124.md`（路线A 论证）、`AGENT-PIPELINE-LATENCY-GAP-20261123.md`（453ms 构成）、`WEB-VIEWER-LATENCY-GAP-20261123.md`（浏览器侧 1/3）、`INPUT-CHANNEL-GAP-20261123.md`（输入三根因）、`HANDOFF-VIDEO-LATENCY-GPU-20261124.md`（444ms 现状与 P0 排查）

## 修订记录

| 版本 | 日期 | 内容 |
|---|---|---|
| v1.0 | 2026-11-24 | 初版：20 跳端到端表 + 5 条机制展开 + 可抄/抄不了清单 + Parsec 对照 + 执行建议 |
