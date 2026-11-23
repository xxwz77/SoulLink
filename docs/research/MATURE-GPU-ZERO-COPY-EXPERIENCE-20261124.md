# 成熟方案 GPU 零拷贝 + 低延迟工程经验汇总（服务 GPU 零拷贝实现）

> 日期：2026-11-24
> 作者：wgc-auditor（dsh-team team_mwt4bbjy11d7lsz / member m_mwt4iptj11xgzz9 / task task_mwthpaxb3zclax）
> 性质：**纯只读经验汇总**。未改任何产品代码/脚本/配置；唯一写入即本文档。
> 证据分级：【开源源码】= GitHub 抓取的 Sunshine / OBS / Moonlight-common-c master 源码片段（存 artifacts/zero-copy-src/ 与 artifacts/latency-playbook-src/，file:line 可回查，GPL-3.0 只读参考不可抄）；【代码事实】= 当前工作区源码逐行可证；【已有实测】= 本仓库 docs/research/ 前序任务实测；【未核实】= 查不到，明确标注不编造。
> 前置：本任务是 GPU-ZERO-COPY-IMPL-PLAN-20261124.md（阶段A 实现方案）的经验侧补全；方案决策/改造点/风险已在彼，本文聚焦「成熟项目怎么做 + 我们差距」，两者互不重复。

---

## 0. 一句话结论

成熟项目把「GPU 抓屏 → GPU 编码」直通 + 把延迟做到极致，靠的是**四件事**，而我们的 NVENC SDK 直通已经完成了最难的第 1 件、其余三件一件没做：

1. **NVENC D3D11 直通**（RegisterResource→Map→Encode→LockBitstream）——**我们已做完**（NvencSdkEncoder.cs），Sunshine/OBS 同款。
2. **GPU 上做 BGRA→NV12**（像素着色器，非 VideoProcessor）——**我们还没做**，还在 CPU BgraToNv12。
3. **帧到达即编码 + 绝对时间节拍**（pacing group / video_sleep）——**我们还没做**，还在定时轮询 SleepUntil。
4. **抓屏慢/GPU 忙时的显式降级与让锁**——**我们部分做了**（DDA 降级链），但缺「GPU 忙时让锁 + 显存驱逐保护 + 编码能力前置校验」。

---

## 1. Sunshine 抓屏→NVENC 完整数据流（逐环节对照 SessionRecorder.Loop）

### 1.1 Sunshine 链路（【开源源码】）

| 环节 | Sunshine 做什么 | 证据 file:line |
|---|---|---|
| ① 抓帧 | AcquireNextFrame(timeout, &frame_info, res) 阻塞取帧，frame_info 全字段使用（ProtectedContentMaskedOut 判 DRM） | display_base.cpp:150-180 |
| ② 帧节奏 | **frame pacing group**：组内用 0ms 超时非阻塞取帧（到达即编码），组首用 200ms 超时；落后则弃组 | display_base.cpp:250-338 |
| ③ GPU 转 NV12 | **像素着色器**（非 VideoProcessor）：BGRA 纹理作 SRV，Draw 全屏三角形，Y/UV 两 pass 分别渲染到 NV12 双平面 RTV | display_vram.cpp:494-563（convert()，Y pass Draw(3)，UV pass Draw(3)） |
| ④ 输入纹理 | 输入纹理 **BindFlags = RENDER_TARGET**（着色器要渲染进去） | nvenc_d3d11_native.cpp:38-50（:46 明写 D3D11_BIND_RENDER_TARGET） |
| ⑤ 注册资源 | RegisterResource（resourceType=DIRECTX，resourceToRegister=d3d_input_texture） | nvenc_d3d11_native.cpp:53-68 |
| ⑥ 编码 | MapInputResource → EncodePicture（FORCEIDR 按需）→ LockBitstream | nvenc_base.cpp:715-789 |
| ⑦ 低延迟参数 | gopLength=INFINITE + frameIntervalP=1（关B帧）+ zeroReorderDelay=1 + enableLookahead=0 + lowDelayKeyFrameScale=1 + CBR + vbv=bitrate/fps | nvenc_base.cpp:258-281 |
| ⑧ 降延迟 | SetGPUThreadPriority(7) + SetMaximumFrameLatency(1) | display_base.cpp:718、733 |

### 1.2 我们现状对照（【代码事实】）

| 环节 | 我们现状 | 证据 |
|---|---|---|
| ① 抓帧 | DDA/WGC 已实现，DDA 已用 FRAME_INFO（前序 A1 修复） | DxgiDdaCapture.cs:173 |
| ② 帧节奏 | **定时轮询**：SleepUntil(ref nextAt, fps) 固定周期，非到达驱动 | SessionRecorder.cs:575-581 |
| ③ GPU 转 NV12 | **未做**，CPU BgraToNv12（逐像素 BT.601） | SessionRecorder.cs:674-703 |
| ④ 输入纹理 | NV12 纹理 BindFlags = None（**无 RENDER_TARGET**，因当前是 CPU UpdateSubresource 上传，不需要渲染） | NvencSdkEncoder.cs:713-724 |
| ⑤ 注册资源 | 已做，DIRECTX + NV12 | NvencSdkEncoder.cs:594-607 |
| ⑥ 编码 | 已做，同步模式 Map→Encode→Lock | NvencSdkEncoder.cs:648-704 |
| ⑦ 低延迟参数 | 已做（CBR + 关B帧 + 无限GOP + zeroReorderDelay + ULTRA_LOW_LATENCY + vbv=bitrate/fps） | NvencSdkEncoder.cs:547-559 |
| ⑧ 降延迟 | **未做** SetGPUThreadPriority / SetMaximumFrameLatency | 无 |

**关键结论**：差距只剩 ②③⑧。其中 ③（GPU 转 NV12）是零拷贝的最后一块拼图，需要把 NV12 输入纹理的 BindFlags 从 None 改为 RENDER_TARGET（Sunshine/OBS 都这么建），否则着色器无处渲染。

---

## 2. OBS 的 NVENC 直通 + 帧队列/缓存策略

### 2.1 OBS 直通（【开源源码】）

| 项 | OBS 做法 | 证据 |
|---|---|---|
| 输入纹理 | BindFlags = RENDER_TARGET + **SetEvictionPriority(DXGI_RESOURCE_PRIORITY_MAXIMUM)**（防显存驱逐） | obs_nvenc-d3d11.c:98-115 |
| 跨进程共享 | 从 OBS 主程序纹理句柄 OpenSharedResource + IDXGIKeyedMutex 跨进程同步 | obs_nvenc-d3d11.c:173-208 |
| 同步时序 | **AcquireSync(lock_key) → CopyResource → ReleaseSync(next_key)**，锁内拷贝防写竞态 | obs_nvenc-d3d11.c:240-248 |
| 编码 | CopyResource 后 MapInputResource → 基类编码 | obs_nvenc-d3d11.c:250-263 |
| 多缓冲 | buf_count 个纹理 + bitstream 轮换（next_bitstream），编码在飞时下一帧用另一槽 | obs_nvenc-d3d11.c:135-149, 227-228 |

### 2.2 lookahead / bframes / surfaces 对延迟的影响（【开源源码】）

- OBS 走 FFmpeg h264_nvenc，max_b_frames = bf（默认**可配 0**，bf=0 即关 B 帧）；on_first_packet 里发现 b-frame 时做 DTS 修正（b-frame 会引入重排延迟） | obs_ffmpeg_nvenc.c:174, 271-277
- Sunshine 直接走 SDK，**frameIntervalP=1 + enableLookahead=0** 双保险关掉重排与前瞻——lookahead 会为前瞻缓存数帧、bframes 会重排，两者都直接加端到端延迟 | nvenc_base.cpp:259-263
- **surfaces（numStateBuffers）**：Sunshine/OBS 都不显式设置（交给驱动默认），低延迟场景的关键不是 surface 数而是 zeroReorderDelay=1 + 关 B 帧；我们同样不设（NvencSdkEncoder 未传 numStateBuffers），**这一点已对齐**。

### 2.3 我们的差距

| 项 | 现状 | 判定 |
|---|---|---|
| 输入纹理 BindFlags | None（因 CPU 上传） | **做 GPU 转 NV12 时必须改 RENDER_TARGET** |
| SetEvictionPriority | 未做 | 挂机显存紧张时纹理可能被驱逐 → 编码失败，建议补 |
| 跨进程共享/KeyedMutex | 不需要（我们是单进程 WGC→NVENC） | 无差距 |
| 多缓冲轮换 | 单纹理同步编码 | 同步模式下够用（5~30fps 单帧提交），无需改 |

---

## 3. 月光客户端的解码渲染经验（对应我们浏览器端堆积）

### 3.1 月光做法（【开源源码】）

| 机制 | 做法 | 证据 |
|---|---|---|
| 有界解码队列 | 解码单元队列有 sizeBound，满则 LBQ_BOUND_EXCEEDED | mlcc_LinkedBlockingQueue.c:39-55, 87-125 |
| 溢出即 flush | 队列溢出 → **清空整个 decode unit 队列** + 请求 IDR | mlcc_VideoDepacketizer.c:537-564 |
| 连续丢帧上限 | CONSECUTIVE_DROP_LIMIT：连丢到上限强制 IDR 重同步 | mlcc_VideoDepacketizer.c:151-165 |
| RFI 优先于 IDR | 丢帧后**优先用 RFI（reference frame invalidation）重同步**，比完整 IDR 省带宽/省延迟；仅「非 RFI 模式 / 从未收过 IDR / 正丢 IDR」才回退完整 IDR | mlcc_VideoDepacketizer.c:132-150 |
| FEC 恢复 | Reed-Solomon FEC 队列可恢复丢失帧，避免动辄 IDR | mlcc_RtpVideoQueue.c:194-269 |

### 3.2 对应我们浏览器端

- 我们前端 WebCodecs 已有「队列满丢旧非关键帧 + 10s 看门狗重连」的堆积处理（前序结论，WebPages.cs:1075、1116）。**差距是：重同步只有「重连=完整 IDR」，没有 RFI 这一档**。
- **可借鉴结论**：RFI 是比「丢帧→等下一个 IDR」更轻的重同步手段——NVENC 支持 RFI（NV_ENC_CAPS_SUPPORT_REF_PIC_INVALIDATION，Sunshine nvenc_base.cpp:223 探测），Sunshine 侧支持 after_ref_frame_invalidation 标记（sunshine-video.cpp:2166）。但 RFI 需要服务端编码器 + 客户端解码器 + 控制回传三端配合，**当前局域网浏览器场景重连成本低（<100ms），RFI 收益有限，建议缓做**（与前序 MATURE-LATENCY-PLAYBOOK §3.3 判断一致）。

---

## 4. 帧节奏 / pacing：成熟项目怎么做「帧到达即编码」（印证 A2 方向）

### 4.1 Sunshine：frame pacing group（【开源源码】）

display_base.cpp:250-338 的 capture() 循环：

- **组首**：snapshot(pull_free_image_cb, img_out, 200ms) —— 大超时阻塞等新帧；
- **组内**：timer->sleep_for(sleep_period) 睡到下一帧节拍后，snapshot(..., 0ms) —— **零超时非阻塞取帧**，有新帧立即拿（到达即编码），无新帧立刻返回；
- **落后弃组**：错过节拍则 frame_pacing_group_start = nullopt 弃组重来，不累积漂移；
- **让锁**：AcquireNextFrame 持 D3D11 不公平锁，静止时独占会让编码线程饿死——超时分支 sleep_for(10ms) 主动让锁（:320-338 注释明说）。

### 4.2 OBS：绝对时间节拍 + 落后统计（【开源源码】）

obs_video.c:808-828 的 video_sleep：睡到**绝对目标时刻** t = cur_time + interval_ns，睡晚了按实际落后量追赶（count = clamped_diff / interval_ns），并 lagged_frames += count - 1 显式统计落后帧。

### 4.3 印证 A2 方向（【代码事实】）

我们的 SessionRecorder.Loop 是**定时轮询**：SleepUntil(ref nextAt, fps) 固定周期睡，抓帧用 AcquireNextFrame(16ms) 短超时——即使 DDA 5ms 后就有新帧，也要等满 33ms 周期，且周期内第 2 帧被覆盖丢失（前序 AGENT-PIPELINE-LATENCY-GAP §H2 已定位）。

**A2 方向被两份成熟实现双重印证**：要么 Sunshine 式 pacing group（0ms 取帧 + 高精度 sleep），要么 OBS 式绝对时间节拍。当前我们 SleepUntil 的 nextAt += (Stopwatch.Frequency/fps) 已经是「绝对时间」的雏形（不是「抓完再睡 interval-耗时」），**缺的只是「组内 0ms 非阻塞取帧 + 到达即编码」**——这正是 A2 的核心。

---

## 5. 抓屏慢 / GPU 忙时的降级与缓冲策略（对应我们 42ms 抓屏）

| 成熟做法 | 证据 | 对应我们 |
|---|---|---|
| **GPU 忙时让锁**：静止时 AcquireNextFrame 独占不公平锁会饿死编码线程，超时后 sleep 10ms 让锁 | display_base.cpp:320-338 | 我们 DDA 超时返回 _lastBgra 但**不 sleep 让锁**，编码与抓屏同线程（单线程串行）反而无锁竞争问题——但若未来抓屏/编码分线程，此坑必踩 |
| **GPU 优先级**：SetGPUThreadPriority(7) 提高抓屏线程 GPU 优先级（需管理员） | display_base.cpp:718 | 未做 |
| **编码能力前置校验**：初始化时 validate_encoder_capabilities 查 max 分辨率/10bit/YUV444/async 支持，不满足直接降级 | nvenc_base.cpp:203-225 | 我们 TryInitialize 只试硬件设备，**未查 NV_ENC_CAPS**——若分辨率超 GPU 上限会 encode 失败后才知 |
| **显存驱逐保护**：输入纹理 SetEvictionPriority(MAXIMUM) | obs_nvenc-d3d11.c:115 | 未做 |
| **NVENC deadlock 兜底**：NVIDIA 驱动在 HAGS（硬件加速 GPU 调度）开启时 NVENC 可能死锁编码线程，Sunshine 用 10s 看门狗强制终止 + 服务重启 | sunshine_stream.cpp:2295-2307 | 我们无此兜底，但同步模式 + 无 HAGS 环境风险低 |

**对应我们 42ms 抓屏**：42ms 大头是「定时轮询 33ms + DDA 超时 16ms 叠加」的节拍税（前序结论），**不是 GPU 忙**。上述 5 条里，真正对我们 42ms 有直接帮助的是「pacing group 0ms 取帧」（§4）与「编码能力前置校验」（避免 encode 失败后才降级的二次延迟）；GPU 优先级/显存驱逐/死锁兜底是稳定性项，随零拷贝一起补。

---

## 6. 坑清单：NVENC D3D11 直通的已知坑（每条标出处）

| # | 坑 | 出处 | 我们的状态 |
|---|---|---|---|
| 1 | **结构体对齐 Pack=8**：NVENC API 结构体必须 Pack=8，否则字段错位导致 NV_ENC_ERR_INVALID_PARAM | nvEncodeAPI.h（我们 NvencSdkEncoder.cs:88-417 已全部 [StructLayout(..., Pack=8)]） | ✅ 已对齐 |
| 2 | **输入纹理必须 BindFlags=RENDER_TARGET**：GPU 转 NV12 时着色器要渲染进输入纹理；BindFlags=None 只能 CPU 上传 | nvenc_d3d11_native.cpp:46、obs_nvenc-d3d11.c:105 | ⚠️ 当前 None，做 GPU 转 NV12 时必改 |
| 3 | **10-bit 4:4:4 与 D3D11 surface 不兼容**：NV_ENC_BUFFER_FORMAT_YUV444_10BIT 无对应 DXGI 格式，必须走 CUDA interop | nvenc_d3d11_native.cpp:32-35 | 我们只用 NV12 8bit，无此坑 |
| 4 | **异步状态机**：enableEncodeAsync 需先 NV_ENC_CAPS_ASYNC_ENCODE_SUPPORT 探测 + RegisterAsyncEvent + LockBitstream.doNotWait + 事件等待；驱动不支持时静默回退同步 | nvenc_base.cpp:219-222, 492-496, 644, 759-764 | 我们同步模式，无此坑（且刻意避开，见 NvencSdkEncoder.cs:41-43 注释） |
| 5 | **resource 生命周期**：UnregisterResource 必须在 DestroyEncoder 之前、UnmapInputResource 必须在每帧 finally 里、bitstream 缓冲销毁顺序固定（destroy_bitstream → destroy_encoder） | nvenc_base.cpp:684-713 | ✅ 我们 Cleanup 顺序正确（NvencSdkEncoder.cs:729-740） |
| 6 | **帧索引 mismatch**：编码返回的 frame_index 与提交的 frame_nr 不一致要报错（同步模式少见，异步模式重排时发生） | sunshine-video.cpp:2160-2162 | 我们同步模式 + 单帧提交，无重排 |
| 7 | **多 GPU**：OpenEncodeSessionEx 的 device 必须与抓屏 D3D11 设备**同 GPU**，否则 MapInputResource 失败 | obs_nvenc-d3d11.c:62（显式 EnumAdapters(0) 选设备） | ⚠️ 我们 D3D11CreateDevice 未枚举 adapter，若多 GPU 机器 NVENC 设备与抓屏设备不同会失败（挂机机单卡场景暂不咬人，多卡需补） |
| 8 | **显存耗尽 E_OUTOFMEMORY**：DuplicateOutput/设备创建在显存紧张时抛 0x8007000E，非 RDP 架构限制 | 前序 DXGI-DDA-RDP-20261123.md（已实测） | 已记录，降级链兜住 |

---

## 7. 我们可直接借鉴的 N 条（按 ROI 排序）

| # | 借鉴项 | 收益 | 成本/风险 | 优先级 |
|---|---|---|---|---|
| 1 | **GPU 转 NV12 用像素着色器（非 VideoProcessor）**：Sunshine 用 PS 两 pass（display_vram.cpp:494-563），NV12 双平面天然两 pass，着色器系数据自控（BT.601/BT.709） | 砍 CPU BgraToNv12（3~6ms）+ 让零拷贝闭环 | 中（HLSL + 双平面 + 常量缓冲），与阶段A 方案一致 | P0 |
| 2 | **输入纹理改 BindFlags=RENDER_TARGET**（随 #1） | 着色器渲染前提 | 低 | P0 |
| 3 | **A2 pacing group**：组内 0ms 非阻塞取帧 + 组首大超时，到达即编码 | 消除 33ms 周期税（A2 核心收益） | 低-中（节拍重写需回归 actualFps） | P0 |
| 4 | **编码能力前置校验**（NV_ENC_CAPS：max 分辨率/async） | encode 失败前就降级，避免运行时二次延迟 | 低 | P1 |
| 5 | **SetEvictionPriority(MAXIMUM)**（输入纹理 + 位流缓冲） | 挂机显存紧张防驱逐 | 低 | P1 |
| 6 | **SetGPUThreadPriority(7) + SetMaximumFrameLatency(1)** | 抓屏线程 GPU 优先级 + 降帧延迟 | 低（需管理员），收益温和 | P2 |
| 7 | **RFI 重同步**（服务端 RFI 标记 + 客户端 RFI 优先于 IDR） | 比完整 IDR 更轻的重同步 | 高（三端配合），局域网重连成本低 | 缓做 |
| 8 | **NVENC deadlock 10s 看门狗**（HAGS 场景） | 防编码线程死锁挂死 | 低，但仅 HAGS 场景 | 缓做 |

---

## 8. 对照我们现状的差距表（一页速查）

| 维度 | 成熟项目 | 我们现状 | 差距 |
|---|---|---|---|
| NVENC D3D11 直通 | Sunshine/OBS 同款 Register→Map→Encode→Lock | **已做完** | 无 |
| 低延迟编码参数 | zerolatency（关B帧+无限GOP+zeroReorderDelay+ULTRA_LOW_LATENCY） | **已做完**（NvencSdkEncoder.cs:547-559） | 无 |
| GPU 转 NV12 | 像素着色器（PS 两 pass） | CPU BgraToNv12 | **未做（零拷贝最后一块）** |
| 输入纹理 BindFlags | RENDER_TARGET | None（CPU 上传） | 做 GPU 转 NV12 时必改 |
| 帧节奏 | pacing group 0ms 取帧 / 绝对时间节拍 | 定时轮询 SleepUntil | **未做（A2）** |
| 编码能力前置校验 | validate_encoder_capabilities | 只试硬件设备 | 未做 |
| 显存驱逐保护 | SetEvictionPriority(MAXIMUM) | 无 | 未做 |
| GPU 忙让锁 | 超时后 sleep 10ms | 无（单线程串行暂不咬人） | 分线程后必补 |
| 重同步 | RFI 优先于 IDR | 重连=完整 IDR | 缓做 |

---

## 9. 证据索引（缓存源码路径）

- Sunshine：artifacts/zero-copy-src/display_base.cpp（抓帧+pacing）、display_vram.cpp（GPU 转 NV12）、nvenc_base.cpp（编码+参数）、nvenc_d3d11_native.cpp（注册资源）、nvenc_d3d11.cpp/h（异步事件）、sunshine-video.cpp（编码调用链+RFI 标记）、nvenc_utils.cpp（格式映射）
- OBS：artifacts/zero-copy-src/obs_nvenc-d3d11.c（直通）、artifacts/latency-playbook-src/obs_ffmpeg_nvenc.c（bframes/参数）、obs_video.c（帧节奏+GPU 队列）、obs_encoder.c
- Moonlight：artifacts/latency-playbook-src/mlcc_VideoDepacketizer.c（丢帧/RFI）、mlcc_RtpVideoQueue.c（FEC）、mlcc_LinkedBlockingQueue.c（有界队列）、mlcc_VideoStream.c
- 我们：Services/NvencSdkEncoder.cs、Relay/SessionRecorder.cs、Relay/DxgiDdaCapture.cs、Relay/WgcCapture.cs

---

## 10. 未核实清单（明确标注，不编造）

- 【未核实】Sunshine 的 SetGPUThreadPriority(7) 在 RDP 会话内是否生效（需管理员 + 物理桌面，RDP 下未实测）。
- 【未核实】ID3D11VideoProcessor（阶段A 方案选的 B 路线）在 RDP 会话内是否可用——阶段A 方案已标注此风险，本文档只负责「Sunshine 用 PS 而非 VideoProcessor」这一事实对照，两者取舍由阶段A 方案决定。
- 【未核实】warmup 黑帧的典型时长分布（决定 E 的 warmup 门 N，前序 WGC-BLACK-FLICKER 已列为待验证）。

## 修订记录

| 版本 | 日期 | 内容 |
|---|---|---|
| v1.0 | 2026-11-24 | 初版：6 大主题逐条带 file:line 证据 + 8 条可借鉴 + 差距表 + 8 坑清单 |
