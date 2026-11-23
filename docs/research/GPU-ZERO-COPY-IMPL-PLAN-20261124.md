# GPU 零拷贝实现方案：抓屏纹理直通 NVENC（阶段A 方案）

> 日期：2026-11-24
> 作者：mature-researcher（dsh-team team_mwt4bbjy11d7lsz / member m_mwt4bbnt11g753x / task task_mwthnbmy3th0k9）
> 性质：阶段A 纯方案，未改任何业务代码。等 Lead 审查通过后进入阶段B 实现。
> 依据：【代码事实】= 当前工作区源码逐行；【官方文档】= MSDN；【开源源码】= Sunshine/OBS（GPL-3.0 只读参考，存 artifacts/zero-copy-src/）；【已有实测】= 前序任务结论。

---

## 0. 一句话结论

零拷贝的「难啃部分」已经在前序任务做完：NVENC SDK D3D11 直通全链（RegisterResource(DIRECTX)→Map→Encode→LockBitstream）已集成进 NvencSdkEncoder.cs 并真机出帧。**当前只剩两段缺口**：

1. **WGC 抓屏纹理不该读回 CPU**：WGC 已把帧放在显存里的 `_gpuTexture`（B8G8R8A8），但 SessionRecorder 调 `CaptureBgra` 立刻 `ReadBackTexture` 读回 CPU。
2. **BGRA→NV12 该在 GPU 上做**：NVENC 只收 NV12，当前用 CPU 的 `BgraToNv12`。

**方案 = ① 新增 GPU 色彩转换器（B8G8R8A8 纹理 → NV12 纹理）② 给 NvencSdkEncoder 加纹理入口 EncodeD3D11Texture ③ SessionRecorder.Loop 加 WGC 零拷贝分支 + BitBlt 保留 CPU 兜底。**

---

## 1. 现状盘点（逐行定位，已读透）

### 1.1 已就绪的部分（无需重做）

| 环节 | 现状 | 证据 |
|---|---|---|
| NVENC SDK 直通全链 | 已集成 NvencSdkEncoder.cs，RegisterResource(DIRECTX)→Map→Encode→LockBitstream | NvencSdkEncoder.cs:495-624（初始化）、638-704（每帧） |
| DIRECTX 会话 + D3D11 设备 | 已建（硬件设备 + OpenEncodeSessionEx(DIRECTX)） | NvencSdkEncoder.cs:507-520 |
| NV12 纹理注册 | 已注册 NV12 纹理，NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX | NvencSdkEncoder.cs:594-607 |
| 精确码控（CBR/关B帧/无限GOP/vbv） | 已实现（rcParams 覆盖） | NvencSdkEncoder.cs:547-559 |
| 按需 IDR | 已实现（首帧/RequestKeyFrame → FORCEIDR|OUTPUT_SPSPPS） | NvencSdkEncoder.cs:668-670, 707 |
| 验证骨架 | tools/nvenc-sdk-probe 已 PASS DIRECTX 直通出帧 | nvenc-sdk-probe/Program.cs:680 |
| WGC 抓帧已在 GPU | `_gpuTexture`（B8G8R8A8 私有纹理，CopyResource 从帧池拷来） | WgcCapture.cs:63, 396-453 |

### 1.2 缺口（本方案要补的）

| # | 缺口 | 现状 | 证据 |
|---|---|---|---|
| ① | 抓屏纹理被读回 CPU | `CaptureBgra` → `ConsumeBgra` → `ReadBackTexture`（staging+Map+Marshal.Copy） | WgcCapture.cs:318-319, 340-390, 371 |
| ② | BGRA→NV12 在 CPU 做 | `SessionRecorder.BgraToNv12`（逐像素 BT.601） | SessionRecorder.cs:670-699 |
| ③ | NV12 又上传回 GPU | NvencSdkEncoder.EncodeNv12 里 UpdateSubresource | NvencSdkEncoder.cs:650-654 |

**三段都是同一次「像素数据显存↔内存往返」的三个面**：读回（GPU→CPU）+ CPU 转换 + 上传（CPU→GPU）。砍掉它们 = 零拷贝。

---

## 2. 关键决策①：BGRA→NV12 用 GPU 着色器还是 VideoProcessor

### 2.1 两条路线对比

| 维度 | A. 像素着色器（PS） | B. ID3D11VideoProcessor（VideoProcessor） |
|---|---|---|
| 原理 | 手写 HLSL，BGRA 纹理作 SRV，Draw 全屏三角形到 NV12 RTV，PS 里算 RGB→YUV | D3D11 视频设备 CreateVideoProcessor，VideoProcessorBlt 做色彩空间转换 |
| NV12 输出 | NV12 是双平面（Y 平面 + UV 交错平面），**不能作为 RTV 直接渲染**，需「两 pass：先渲染 Y 到 R8 纹理，再渲染 UV 到 R8G8 纹理，最后 CopyResource 合成 NV12」或「渲染到 R8 单通道 NV12 兼容纹理」 | VideoProcessorBlt 原生支持 NV12 输出，一步到位 |
| 复杂度 | 中：手写 HLSL + 常量缓冲 + 双平面处理 + UV 下采样（2x2 平均） | 低：官方 API，Blt 调用一次 |
| 色彩精度 | 完全可控（BT.601/BT.709 全范围/部分范围可自定义） | 由驱动实现，标准 BT.601/BT.709 |
| 与 CPU BgraToNv12 一致性 | 需自己对齐 BT.601 系数，否则与旧 CPU 路径有色差 | VideoProcessor 输出标准，但可能与现有 CPU 实现有细微色差 |
| 依赖 | 只需 D3D11 + 手写 shader（Vortice 已引） | 需 ID3D11VideoDevice（D3D11 视频设备，硬件支持依赖） |
| 行业参照 | Sunshine 用 PS（display_vram.cpp:494-563，GPL 只读） | OBS 捕获侧已是 NV12 无此步；Microsoft WGC 官方样例多用 VideoProcessor |
| RDP 会话内可用性 | ✅ 普通 D3D11 渲染管线，WGC 已实测硬件设备可用 | ⚠️ 视频设备（ID3D11VideoDevice）在 RDP 会话内是否可用未实测 |

### 2.2 决策：选 B（ID3D11VideoProcessor），理由

1. **NV12 双平面渲染是着色器方案的硬坑**：NV12 不是单一 RTV 能直接写的格式（Y 与 UV 分平面、UV 半分辨率交错），着色器方案要么两 pass + 合成（复杂 3 倍），要么用 R8 单通道 trick（Vortice 对 NV12 RTV 的支持需逐项验证）。VideoProcessor 的 `VideoProcessorBlt` 原生支持 BGRA→NV12 一步完成，是官方为该场景设计的标准 API。
2. **Sunshine 用着色器是因为它要支持 NV12/P010/YUV444 多格式**（display_vram.cpp:494-563 是通用转换器），SoulLink 只需 BGRA→NV12 一种，不需要着色器的灵活性，VideoProcessor 的「一个 Blt」完全够用。
3. **与 CPU 路径的一致性风险**：两条路线都可能有 BT.601 系数差异，但 VideoProcessor 是硬件标准实现，色差可控且稳定；着色器需要手动对齐 BT.601 全范围/部分范围，反而更容易出错。
4. **成本更低**：VideoProcessor 是 Vortice.Direct3D11 已绑定的 API（ID3D11VideoDevice/VideoProcessorOutputView），无需手写 HLSL + 编译着色器。

**风险点（需在阶段B验证）**：RDP 会话内 `ID3D11VideoDevice` 是否可创建。若不可用（WARP 软件设备无视频加速），**降级回 CPU BgraToNv12**（现有路径），即 VideoProcessor 作为「最优路径」而非「唯一路径」。这也是为什么阶段B要保留 CPU 兜底（§5.3）。

---

## 3. 关键决策②：NV12 纹理注册给 NVENC 的完整 P/Invoke 链路

**这一段已经全部实现并验证，方案只确认「复用现有代码，不重写」**：

```
NV_ENC_REGISTER_RESOURCE { version, resourceType=DIRECTX, width, height, pitch=0,
  subResourceIndex=0, resourceToRegister=NV12纹理.NativePointer, bufferFormat=NV12,
  bufferUsage=NV_ENC_INPUT_IMAGE }
  → NvEncRegisterResource
每帧:
  NV_ENC_MAP_INPUT_RESOURCE { version, registeredResource } → NvEncMapInputResource
  NV_ENC_PIC_PARAMS { inputBuffer=mappedResource, bufferFmt=mappedBufferFmt,
    encodePicFlags(FORCEIDR|OUTPUT_SPSPPS 首帧), outputBitstream }
  → NvEncEncodePicture
  NV_ENC_LOCK_BITSTREAM { doNotWait=0 } → NvEncLockBitstream → 拷出 NAL → Unlock
  → NvEncUnmapInputResource
```

**对照**：tools/nvenc-sdk-probe/Program.cs:609-676 已完整验证此链（console + A2 RDP 双会话直通出帧 PASS）。NvencSdkEncoder.cs:594-704 已把它产品化。**阶段B唯一要做的是「把 EncodeNv12 的 CPU 上传段（L650-654）替换成「直接吃外部 NV12 纹理」，其余 Map/Encode/Lock 全部不动。**

---

## 4. 数据流改造点（SessionRecorder.Loop 三处）

### 4.1 现状数据流（WGC 分支，3 次 CPU 往返）

```
Loop():
  bgra = CaptureBgra(out w, out h)          // ← 改点①：读回 CPU（WgcCapture.ReadBackTexture）
  nv12 = BgraToNv12(bgra, ...)              // ← 改点②：CPU 转换
  nal = encoder.EncodeNv12(nv12, ts)        // ← 改点③：NV12 上传回 GPU（UpdateSubresource）
```

### 4.2 目标数据流（WGC 零拷贝分支）

```
Loop():
  IFrameH264Encoder encoder = ...            // 不变
  if (Capture is WgcCapture wgc && encoder is NvencSdkEncoder nvenc) {
      // 新增零拷贝分支
      nv12Tex = wgc.CaptureNv12Texture(gpuConverter)   // ← 改点①：WGC 纹理不读回，直接 GPU 转 NV12
      nal = nvenc.EncodeD3D11Texture(nv12Tex, ts)       // ← 改点②：直接吃 NV12 纹理，跳过上传
  } else {
      // 现状 CPU 路径（BitBlt / MF / 降级）
      bgra = Capture.CaptureBgra(out w, out h);
      nv12 = BgraToNv12(bgra, ...);
      nal = encoder.EncodeNv12(nv12, ts);
  }
```

### 4.3 三处改造点明细

| 改点 | 现状（文件:行） | 改法 | 涉及文件 |
|---|---|---|---|
| ① 抓屏不读回 | WgcCapture.CaptureBgra → ConsumeBgra → ReadBackTexture（WgcCapture.cs:318-319, 371） | 新增 `CaptureNv12Texture(ID3D11VideoDevice?)` 或 `CaptureBgraTexture(out ID3D11Texture2D)`：取 `_gpuTexture` 直接返回（不 ReadBack），供转换器用 | WgcCapture.cs 新增方法 |
| ② GPU 转换 | SessionRecorder.BgraToNv12（SessionRecorder.cs:670-699） | 新增 `GpuColorConverter`（独立类）：ID3D11VideoDevice → VideoProcessor，Blt(BGRA纹理→NV12纹理) | 新增 Services/GpuColorConverter.cs |
| ③ 纹理直通编码 | NvencSdkEncoder.EncodeNv12 里 UpdateSubresource（NvencSdkEncoder.cs:650-654） | 新增 `EncodeD3D11Texture(ID3D11Texture2D nv12Tex, long ts)`：跳过 UpdateSubresource，直接 Map 外部纹理 → Encode → Lock | NvencSdkEncoder.cs 新增方法 |

### 4.4 关键设计约束

1. **转换器与编码器必须共享同一个 D3D11 设备**：WGC 帧池有自己的设备（WgcCapture.cs:146 创建），NvencSdkEncoder 也建了自己的设备（NvencSdkEncoder.cs:508）。**零拷贝要求三者在同一设备/上下文**（BGRA 纹理 → VideoProcessor → NV12 纹理 → NVENC 注册），否则要跨设备 CopyResource（又引入一次 GPU 拷贝，但仍是显存内，可接受）。**阶段B需决策：WGC 帧池设备 = 编码器设备 = 转换器设备，三者复用同一 ID3D11Device**（Sunshine 也是这么做的，display_vram 与 nvenc 共用 device）。
2. **NV12 纹理尺寸必须偶数对齐**：NV12 要求宽高为偶数（SessionRecorder 已有 `ew = width & ~1`，NvencSdkEncoder.TryInitialize 也 `width--` if odd）。转换器创建 NV12 纹理时同样对齐。
3. **纹理所有权与生命周期**：转换器产出的 NV12 纹理是「每帧复用的一块」还是「帧池」。推荐每帧复用一块（与 NvencSdkEncoder 现有 _nv12Tex 一致），转换器内部持有这块 NV12 纹理，NvencSdkEncoder 只注册一次、每帧 Map 同一块。
4. **静止去重逻辑位置**：现在静止去重用 `previousBgra`（CPU byte[] SequenceEqual）。零拷贝分支没有 CPU bgra，**静止去重需移到纹理级**（比较帧时间戳 `_gpuFrameTs` 是否变化，而非像素内容）。这是改点②的连带改动。

---

## 5. BitBlt 后端处理（CPU 抓屏无纹理）

**BitBlt 保留现状 CPU 路径，不做任何改动**。

理由：
1. BitBlt 本身就是 CPU 抓屏（GetDC→BitBlt→LockBits），没有 GPU 纹理可直通，零拷贝对它无意义。
2. BitBlt 是降级兜底后端（独占全屏游戏 WGC 抓不到时用），性能不是它的场景。
3. 保留 CPU 路径 = 降级链的天然一部分：WGC+NVENC SDK 零拷贝 → WGC+CPU → BitBlt+CPU → MF。

**降级链最终形态**：

```
抓屏:  DDA(auto) → WGC → BitBlt
编码:  NVENC SDK(零拷贝, WGC 纹理) → NVENC SDK(CPU NV12) → MF(硬编→软编)

组合（按优先级）:
  1. WGC 纹理 + NVENC SDK 零拷贝    ← 本方案新增
  2. WGC + NVENC SDK CPU 路径        ← 现状
  3. WGC + MF                        ← 现状
  4. BitBlt + NVENC SDK / MF         ← 现状（BitBlt 只有 CPU）
```

**触发条件**：`Capture is WgcCapture && encoder is NvencSdkEncoder && gpuConverter.IsAvailable` 三者全真才走零拷贝；任一不满足回落 CPU 路径。

---

## 6. 风险清单 + 回滚方案

| # | 风险 | 概率 | 影响 | 缓解 / 回滚 |
|---|---|---|---|---|
| 1 | RDP 会话内 ID3D11VideoDevice 创建失败 | 中 | 零拷贝不可用 | VideoProcessor 创建失败 → gpuConverter.IsAvailable=false → 回落 CPU 路径（§5）。回滚=删除零拷贝分支 |
| 2 | VideoProcessor 输出色差 vs CPU BgraToNv12 | 中 | 画面色调变化 | 阶段B先做「转换器独立验证」：转一帧 NV12 读回 CPU 与 BgraToNv12 结果逐像素比对（PSNR），确认色差在阈值内再接线 |
| 3 | 跨设备纹理不兼容（WGC 设备 ≠ 编码器设备） | 中 | Blt/注册失败 | 决策：三者复用同一 ID3D11Device（§4.4.1）。若 WGC 帧池无法共享设备，退而求其次：BGRA 纹理跨设备 CopyResource（显存内，仍省 CPU 往返） |
| 4 | NV12 纹理注册到 NVENC 后，转换器每帧写同一块纹理的竞态 | 低 | 编码读到半新半旧帧 | 同步模式（doNotWait=0）下 Map→Encode→Lock 阻塞完成，转换器在下一次 Blt 前编码已结束。天然串行，无需额外同步 |
| 5 | 静止去重从像素比较改时间戳比较，误判画面变化 | 低 | 多编几帧（画质无碍） | `_gpuFrameTs` 每帧变化（FrameArrived 更新），时间戳不变=无新帧=跳过。与现有 `_stashedFrame` 语义一致 |
| 6 | 结构体/Packing 错误（手写 COM 前科） | 低（SDK 直通已 PASS，本方案不新增 P/Invoke） | 崩溃/坏帧 | 本方案**不新增任何 P/Invoke 结构体**，全部复用已验证的 NvencSdkEncoder.cs 与 nvenc-sdk-probe |
| 7 | 多账号并发显存不足 | 中 | 零拷贝创建纹理失败 | CreateTexture2D 失败 → 回落 CPU 路径。多账号显存共享是已知约束（GPU-ZERO-COPY §5.5） |

**回滚总方案**：零拷贝分支是「新增代码路径」，不修改现有 CPU 路径。回滚 = 删除新增分支 + 新增类（GpuColorConverter）+ 新增方法（WgcCapture.CaptureNv12Texture / NvencSdkEncoder.EncodeD3D11Texture），SessionRecorder.Loop 恢复为单一 CPU 路径。改动集中、可原子回滚。

---

## 7. 阶段B实现清单（审查通过后执行）

| 步骤 | 内容 | 文件 | 验证 |
|---|---|---|---|
| B1 | 新增 GpuColorConverter（VideoProcessor BGRA→NV12），独立可测 | 新增 Services/GpuColorConverter.cs | 独立探针：转一帧读回 CPU 与 BgraToNv12 比对 PSNR |
| B2 | WgcCapture 新增 CaptureNv12Texture（不读回，返回 _gpuTexture） | WgcCapture.cs | 返回非空纹理 + 尺寸正确 |
| B3 | NvencSdkEncoder 新增 EncodeD3D11Texture（跳过 UpdateSubresource） | NvencSdkEncoder.cs | 复用 nvenc-sdk-probe 验证（纹理直通出帧） |
| B4 | SessionRecorder.Loop 加零拷贝分支 + 静止去重改时间戳 | SessionRecorder.cs | dotnet build 0 error |
| B5 | 真机验收：DDA→WGC→BitBlt 三形态 + 独占全屏/无边框/最小化 + 多账号 | A2 真机 | 画面正常 + 降级链触发正确 |

---

## 8. 收益账（相对现状 WGC 分支）

| 段 | 现状 | 零拷贝后 | 依据 |
|---|---|---|---|
| staging 读回（GPU→CPU） | 3~6ms | 0 | WgcCapture.cs:418-429；NVENC-DESIGN §6 |
| BGRA→NV12（CPU） | 3~6ms | GPU VideoProcessor（CPU≈0） | SessionRecorder.cs:670-699 |
| NV12 上传（CPU→GPU） | NV12 3.1MB 拷贝 | 0（纹理直通） | NvencSdkEncoder.cs:650-654 |
| **合计** | **约 6~12ms CPU/帧** | **≈0 CPU，仅 GPU 控制开销** | 每帧省一次显存↔内存往返 |

> 不夸大：这是编码侧收益。端到端延迟大头是抓屏节拍与推流节拍（见 LATENCY-BENCHMARK），零拷贝是第 2~3 步优化，不替代换 WGC + 修节拍。

---

## 9. 未决问题（需 Lead 拍板）

1. **三者设备是否强制复用**：WGC 帧池 / VideoProcessor / NVENC 是否要求同一 ID3D11Device？我的建议是「能复用就复用」（Sunshine 做法），但 WGC 帧池设备由 WgcCapture 内部创建，复用需改造 WgcCapture 暴露设备。**备选**：允许跨设备 CopyResource（显存内拷贝，仍省 CPU 往返，只是多一次 GPU 拷贝）。请 Lead 定。
2. **阶段B的验证门槛**：色差 PSNR 阈值定多少（我的建议 ≥40dB 视为可接受）？
3. **是否本阶段就做「WGC 帧池设备共享」改造**，还是先做「跨设备 CopyResource」的保守版（改动小、风险低，但多一次 GPU 拷贝）？

---

## 10. 证据索引

| 结论 | 证据 |
|---|---|
| NVENC SDK 直通已集成 | NvencSdkEncoder.cs:495-624, 638-704 |
| DIRECTX 直通已 PASS | nvenc-sdk-probe/Program.cs:680 |
| WGC 帧已在 GPU（_gpuTexture） | WgcCapture.cs:63, 396-453 |
| 现状读回 CPU | WgcCapture.cs:318-319, 340-390, 371 |
| 现状 CPU 转 NV12 | SessionRecorder.cs:670-699 |
| 现状 NV12 再上传 | NvencSdkEncoder.cs:650-654 |
| Sunshine 着色器转换（对照） | display_vram.cpp:494-563（artifacts/zero-copy-src/） |
| OBS NV12 纹理直通（对照） | obs-nvenc/nvenc-d3d11.c:98-127, 243-263（artifacts/zero-copy-src/） |
| VideoProcessor 官方接口 | learn.microsoft.com ID3D11VideoDevice / VideoProcessorBlt |
| 本机 MF MFT 非 D3D11（必须 SDK） | MEDIA-CAPABILITY §2.6 |
