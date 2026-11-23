# NVENC 硬编正确实现：异步 MFT 时序 + 官方依据 + MF 软编压 IDR 可行性

> 日期：2026-11-23
> 作者：deploy-fix（agent-team / task `task_mwsnr4zsihjn6o`）
> 性质：**纯调研**，不改业务代码、不改 csproj、不碰 Relay/Services。
> 背景：stream-core 试 NVENC 硬编「能激活但事件泵无首帧输出」，回退软编后用户批评「为什么不去研究开源软件与英伟达官方文档」。本文补上正确实现路径，回答「NVENC 值不值得做」与「不换编码器能不能压 IDR」。
> 证据来源：MSDN（Asynchronous MFTs / IMFTransform::ProcessOutput / MF_MT_MAX_KEYFRAME_SPACING / CODECAPI_AVLowLatencyMode）、本仓库 `MfFrameEncoder.cs` / `MfH264Encoder.cs` / `NVENC-DESIGN-20261122.md` / `OSS-REMOTE-RESEARCH-20261122.md`、Vortice.MediaFoundation 3.8.3 程序集元数据（本机反射）。

---

## 0. 一句话结论

1. **NVENC 值得做，但必须先修好一个明确的时序 bug**：当前 `MfFrameEncoder.cs` 的硬件异步路径**没有真正实现 MSDN 规定的异步 MFT 时序**，事件泵「无首帧」不是 NVENC 不行，是调用方式错了。
2. **最小可行改动清单**（§4）集中在 `MfFrameEncoder.cs`：解锁顺序、事件驱动的 `BeginGetEvent/EndGetEvent`（或按 NeedInput/HaveOutput 驱动）、`ProcessOutput` 返回码分支、输入样本复用。**不需要**引入 NVENC SDK。
3. **MF 软编压 IDR 可行**（§6）：`MF_MT_MAX_KEYFRAME_SPACING` 已在当前代码设置（`MfFrameEncoder.cs:122`），但**用错了方向**（追求大 GOP）且**未设低延迟码控**；要压 IDR 应反向设小 GOP + `CODECAPI_AVLowLatencyMode` + `CODECAPI_AVEncCommonRateControlMode`，但这些 `ICodecAPI` 属性 **Vortice 3.8.3 未绑定**，需手写 P/Invoke（~40 行）。

---

## 1. 当前实现的确切缺陷（file:line 定位）

`Services/MfFrameEncoder.cs`（stream-core 已改过的当前版本）：

### 1.1 硬件分支枚举与解锁（:92-111）

```csharp
using var hwColl = MediaFactory.MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, 4, inInfo, outInfo);  // :92
...
_transform = act.ActivateObject<IMFTransform>();           // :103
_transform.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, true);  // :104
```

**结论**：`MFTEnumEx` 的 flag `4` = `MFT_ENUM_FLAG_HARDWARE`（`MEDIA-CAPABILITY §6.4` 已确认「硬件 MFT 必须用 HARDWARE 单独枚举」）。解锁 `TransformAsyncUnlock=true` 这一步**是对的**（MSDN Asynchronous MFTs §Unlocking 明确：解锁必须设 `MF_TRANSFORM_ASYNC_UNLOCK=TRUE`，否则所有 IMFTransform 方法返回 `MF_E_TRANSFORM_ASYNC_LOCKED`）。

### 1.2 核心缺陷：事件泵只调 `GetEvent`，且不驱动输入

`EncodeHardwareOutput()`（:254-298）：

```csharp
using var events = _transform.QueryInterfaceOrNull<IMFMediaEventGenerator>();  // :257
...
while (guard++ < 64)
{
    try { ev = events.GetEvent(0); } catch { break; }      // :264
    if (ev == null) break;
    if (ev.EventType != MediaEventTypes.TransformHaveOutput) continue;  // :269
    ...
    var hr = _transform.ProcessOutput(...);                 // :277
    if (!hr.Success) break;                                 // :278
```

**三个 bug**（对照 MSDN `Asynchronous MFTs` 与 `IMFTransform::ProcessOutput`）：

| # | 缺陷 | MSDN 依据 | 后果 |
|---|---|---|---|
| 1 | **用轮询 `GetEvent(0)` 而非事件驱动** | MSDN Asynchronous MFTs §Events：客户端应通过 `IMFMediaEventGenerator::BeginGetEvent/EndGetEvent` **异步接收**带外事件；`GetEvent` 是**同步阻塞**接口，配 timeout=0 立即返回 null，根本收不到「编码器稍后才产生的 HaveOutput」 | 事件泵空转，永远拿不到首帧 |
| 2 | **没有正确处理 `ProcessOutput` 返回码** | MSDN `IMFTransform::ProcessOutput` 返回码表：`MF_E_TRANSFORM_NEED_MORE_INPUT`、`E_UNEXPECTED`（异步 MFT 在非预期时调用返回）都是**正常控制流**，不是失败；`if (!hr.Success) break;` 把 `NEED_MORE_INPUT` 当失败直接退出 | 漏掉「还需要喂输入」的信号 |
| 3 | **异步 MFT 的驱动模型完全颠倒** | MSDN Asynchronous MFTs §ProcessInput/§ProcessOutput：异步 MFT **自己发 `METransformNeedInput` / `METransformHaveOutput` 事件**，客户端**收到 NeedInput 才 ProcessInput、收到 HaveOutput 才 ProcessOutput**；`ProcessInput` 若不对应 NeedInput 事件必须返回 `MF_E_NOTACCEPTING`；`ProcessOutput` 若在非预期时调用返回 `E_UNEXPECTED` | 当前代码「同步式」地 `ProcessInput → 立即轮询 ProcessOutput`，对异步 MFT 是完全错误的调用模式 |

### 1.3 输出类型属性设置（:113-123）

```csharp
outMediaType.Set(MF_MT_AVG_BITRATE, ...);                    // :116
outMediaType.Set(MF_MT_INTERLACE_MODE, 2u);                  // :117
outMediaType.Set(MF_MT_FRAME_SIZE, ...);                     // :118
outMediaType.Set(MF_MT_FRAME_RATE, ...);                     // :119
outMediaType.Set(MF_MT_PIXEL_ASPECT_RATIO, ...);             // :120
outMediaType.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing, Math.Max(300, _fps * 20));  // :122
```

**缺陷**：`MaxKeyframeSpacing = max(300, fps*20)` 是在**拉长 GOP**（`OSS-REMOTE §4.2` 说 Sunshine 走「无限 GOP + 按需 IDR」），但对「截图式单帧编码/直播」场景，**应该把 GOP 压到 1~30**（每帧 IDR，见 §6）。且**没设** `MF_MT_MPEG2_PROFILE`（baseline/main/high）与低延迟码控。

### 1.4 输入缓冲每帧新建（:179-190）

```csharp
using var sample = MediaFactory.MFCreateSample();          // :179 每帧新建
using var buf = MediaFactory.MFCreateMemoryBuffer(nv12.Length);  // :180 每帧新建 + Marshal.Copy
```

**缺陷**：每帧 `MFCreateMemoryBuffer` + `Marshal.Copy`（1080p NV12 = 3.1MB）会触发额外拷贝。NVENC 硬件 MFT 通常接受**预分配 + 复用**缓冲（`MF_SA_BUFFERS_PER_SAMPLE`、`MF_SA_D3D_AWARE` 属性，见 §5.3）。当前没有设置这些属性，也没有复用缓冲。

---

## 2. MFT 异步模式完整时序（MSDN 权威，逐条回答）

来源：MSDN `Asynchronous MFTs`（`learn.microsoft.com/en-us/windows/win32/medfound/asynchronous-mfts`）。

### 2.1 TransformAsyncUnlock 之后还要做什么

1. 解锁（`MF_TRANSFORM_ASYNC_UNLOCK=TRUE`）——当前代码已做（:104）。
2. **必须改用 `BeginGetEvent/EndGetEvent` 异步接收事件**（不是轮询 `GetEvent`）。
3. **必须按事件驱动输入/输出**：收到 `METransformNeedInput` → `ProcessInput`；收到 `METransformHaveOutput` → `ProcessOutput`。
4. 流开始时发 `MFT_MESSAGE_NOTIFY_START_OF_STREAM`（当前代码 :140-141 已发 `MessageNotifyBeginStreaming` + `MessageNotifyStartOfStream`，**顺序对**）。
5. 结束时发 `MFT_MESSAGE_NOTIFY_END_OF_STREAM`（:348 已发）。

### 2.2 必须调哪些 ProcessMessage

| 消息 | 何时 | MSDN 依据 |
|---|---|---|
| `MFT_MESSAGE_NOTIFY_START_OF_STREAM` | 流开始（解锁后） | Asynchronous MFTs §ProcessInput 第 1 条 |
| `MFT_MESSAGE_NOTIFY_END_OF_STREAM` | 流结束 | 同上第 4 条 |
| `MFT_MESSAGE_COMMAND_FLUSH` | **丢弃**内部积压的输入/输出（切换画面/重同步时） | §Flushing |
| `MFT_MESSAGE_COMMAND_DRAIN` | 想要「把已喂入的都吐出来」 | §Draining |
| `MFT_MESSAGE_COMMAND_MARKER` | 标记流中一点（不中断流） | §Markers |

**当前代码只发了 START/END，缺 FLUSH/DRAIN**。对直播「换源/首帧重建」场景，**需要 FLUSH** 来丢弃旧的 NVENC 内部缓冲。

### 2.3 事件句柄从哪来 / GetEvent 怎么用

- **异步 MFT 必须实现 `IMFMediaEventGenerator`**（MSDN §General Requirements）。句柄不直接「拿」——客户端通过 `QueryInterface` 到 `IMFMediaEventGenerator`（当前代码 :257 已做 `QueryInterfaceOrNull<IMFMediaEventGenerator>`）。
- **正确用法是 `BeginGetEvent/EndGetEvent`**（异步回调/完成），或把事件队列绑到 MF 标准事件队列后 `Wait`。`GetEvent` 是**同步阻塞**，配 `timeout=0` 只会返回空——**这就是「无首帧」的直接原因**。

### 2.4 METransformNeedInput / HaveOutput 完整时序

```
客户端                                      MFT
  │ QI(IMFMediaEventGenerator) ──────────►
  │ 发 NOTIFY_START_OF_STREAM ───────────►
  │                                           │ 发 METransformNeedInput(streamId)
  │ ◄── BeginGetEvent/EndGetEvent 收到 NeedInput ──┘
  │ ProcessInput(sample) ────────────────►
  │                                           │ (异步编码…)
  │                                           │ 发 METransformHaveOutput
  │ ◄── 收到 HaveOutput ─────────────────────┘
  │ ProcessOutput(...) ───────────────────►   │ 返回 NAL 样本
  │ (循环：每个 NeedInput 喂一帧，每个 HaveOutput 取一帧)
```

**关键点**（MSDN §ProcessInput/§ProcessOutput Implementation notes）：
- `ProcessInput` 若**不对应** NeedInput 事件 → 必须返回 `MF_E_NOTACCEPTING`。
- `ProcessOutput` 若在**非预期时**调用（没有 HaveOutput）→ 返回 `E_UNEXPECTED`。
- **异步 MFT 的 ProcessOutput 不应返回 `MF_E_TRANSFORM_NEED_MORE_INPUT`**（那是同步 MFT 的语义；异步 MFT 缺输入时发 NeedInput 事件）。

---

## 3. ProcessOutput 返回码正确处理（逐条）

来源：MSDN `IMFTransform::ProcessOutput` 返回码表 + `Asynchronous MFTs` §ProcessOutput。

| 返回码 | 含义 | 正确处理 | 当前代码 |
|---|---|---|---|
| `S_OK` | 有输出（或事件） | 读 `pOutputSamples`，继续 | ✅ 读样本 |
| `E_UNEXPECTED` | 异步 MFT 在非预期时被调 | **正常**，忽略，等下一个 HaveOutput 事件 | ❌ 当失败 `break` |
| `MF_E_TRANSFORM_NEED_MORE_INPUT` | （同步 MFT）需更多输入 | **正常**，继续喂输入 | ❌ 当失败 `break` |
| `MF_E_TRANSFORM_STREAM_CHANGE` | 输出格式变化 | 重新 `SetOutputType` / 读新类型 | ❌ 未处理 |
| `MF_E_TRANSFORM_TYPE_NOT_SET` | 未设媒体类型 | 初始化错误，应报错 | ❌ 未区分 |

**正确模式**：`ProcessOutput` 的返回码里只有 `MF_E_TRANSFORM_TYPE_NOT_SET` / 未知 HRESULT 才算真失败；`E_UNEXPECTED` 与 `MF_E_TRANSFORM_NEED_MORE_INPUT` 都是「继续等事件」的正常信号。

---

## 4. NVENC 值不值得做 + 最小可行改动清单

### 4.1 值不值得做

**值得，但优先级仍是「先修抓屏」（`NVENC-DESIGN §9` 结论不变）**。理由：
- NVENC 在 RDP 内**真实可用**（`NVENC-DESIGN §1`：`nvidia-smi encodersessions` 显示 TermService 正用 NVENC，media-probe 枚举到 `NVIDIA H.264 Encoder MFT` 并产出真 H.264）。
- 硬件并发上限 **12 路**（RTX 4060 Laptop，Ada，8th Gen NVENC；`NVENC-DESIGN §4`），4~8 账号够用。
- **但**当前代码的硬件分支是**坏的**（§1.2 时序 bug），不是 NVENC 不行。修好时序就能出帧。

### 4.2 最小可行改动清单（代码级，只动 `MfFrameEncoder.cs`）

| # | 改动 | 落点 | 工作量 |
|---|---|---|---|
| 1 | 硬件分支：`IMFMediaEventGenerator` 用 **`BeginGetEvent/EndGetEvent`**（异步）或绑标准事件队列 + Wait，替代轮询 `GetEvent(0)` | `EncodeHardwareOutput` :257-298 | 中（需确认 Vortice 是否绑定 BeginGetEvent，见 §5.2） |
| 2 | 输入/输出**按事件驱动**：收到 `NeedInput` 才 `ProcessInput`，收到 `HaveOutput` 才 `ProcessOutput` | `EncodeNv12` + `EncodeHardwareOutput` | 中 |
| 3 | `ProcessOutput` 返回码分支：`E_UNEXPECTED`/`NEED_MORE_INPUT` 视为正常，仅 `TYPE_NOT_SET`/未知 HRESULT 报错 | :278 `if (!hr.Success) break;` | 小 |
| 4 | 输入缓冲**预分配 + 复用**（不每帧 `MFCreateMemoryBuffer`），并设 `MF_SA_BUFFERS_PER_SAMPLE`/`MF_SA_D3D_AWARE`（若 MFT 支持） | :179-190 | 小~中 |
| 5 | 输出类型补 `MF_MT_MPEG2_PROFILE`（baseline/main）+ 低延迟码控属性（`CODECAPI_AVLowLatencyMode`，需手写 ICodecAPI，见 §6） | :113-123 | 小 |
| 6 | 流切换/首帧重建时发 `MFT_MESSAGE_COMMAND_FLUSH` | 初始化/重连处 | 小 |

> **一句话给 stream-core**：不是回退软编，而是把 `MfFrameEncoder` 的硬件分支改成「事件驱动 + 正确返回码」，就能让 NVENC 出帧。

---

## 5. 输入缓冲 / D3D 属性 / Vortice 绑定现状

### 5.1 输入缓冲是否需预分配

- MSDN `IMFTransform::ProcessOutput` §Output Buffers：MFT 的分配模型由 `GetOutputStreamInfo` 的 `MFT_OUTPUT_STREAM_PROVIDES_SAMPLES` / `CAN_PROVIDE_SAMPLES` 决定。**输出侧**要按此判定是否复用缓冲。
- **输入侧**：NVENC MFT 通常接受调用方分配的样本，但**每帧新建 `MFCreateMemoryBuffer` + `Marshal.Copy`（3.1MB）** 是明显的浪费（`NVENC-DESIGN §6` 估算手工 NV12 拷贝 2~4ms/帧）。**应预分配一个输入缓冲池复用**。

### 5.2 `MF_SA_D3D_AWARE` / `MF_SA_BUFFERS_PER_SAMPLE` 要不要设

- MSDN `Asynchronous MFTs` §Attributes 只强制 `MF_TRANSFORM_ASYNC` / `MF_TRANSFORM_ASYNC_UNLOCK` / `MFT_SUPPORT_DYNAMIC_FORMAT_CHANGE`。
- `MF_SA_D3D_AWARE`（D3D11 感知）与 `MF_SA_BUFFERS_PER_SAMPLE` 是**可选优化**，不是必须。**本机 NVENC MFT 枚举显示「非 D3D11」（`MEDIA-CAPABILITY §2.6`）**，所以 `MF_SA_D3D_AWARE` 对当前「系统内存 NV12 → 非D3D11 MFT」路径**无效**；`BUFFERS_PER_SAMPLE` 可设但需实测 MFT 是否响应。

### 5.3 Vortice 3.8.3 绑定缺口（本机反射确认）

| 能力 | Vortice 3.8.3 是否绑定 | 依据 |
|---|---|---|
| `IMFTransform` / `IMFSample` / `IMFMediaType` / `ProcessInput` / `ProcessOutput` | ✅ 有（当前代码在用） | `MfFrameEncoder.cs` |
| `TransformAttributeKeys.TransformAsyncUnlock` / `D3DAware` / `BuffersPerSample` | ✅ 有 | 反射枚举 `TransformAttributeKeys` 静态字段 |
| `MediaTypeAttributeKeys.MaxKeyframeSpacing` / `Mpeg2Profile` / `Mpeg2Level` / `AvgBitrate` | ✅ 有 | 反射枚举 `MediaTypeAttributeKeys` 静态字段 |
| `MediaEventTypes.TransformNeedInput` / `TransformHaveOutput` / `TransformDrainComplete` / `TransformMarker` | ✅ 有 | 反射枚举 `MediaEventTypes` |
| **`ICodecAPI`**（`CODECAPI_AVLowLatencyMode` / `AVEncCommonRateControlMode` 等码控属性） | ❌ **未绑定** | 反射 `GetType("Vortice.MediaFoundation.ICodecAPI")` 返回 null |
| **`IMFMediaEventGenerator` 的 `BeginGetEvent/EndGetEvent`** | ⚠️ 需进一步确认（当前代码只用了 `GetEvent` 同步版） | `MfFrameEncoder.cs:257` 只调 `QueryInterfaceOrNull<IMFMediaEventGenerator>` + `GetEvent` |

> **关键结论**：压 IDR 需要的 `CODECAPI_*` 码控属性在 Vortice 3.8.3 **没有绑定**，需要**手写 ~40 行 `ICodecAPI` P/Invoke**（`NVENC-DESIGN §3` 已提示「Vortice 无 ICodecAPI，需手写 ~50 行」）。这与本仓库 `Mp4Writer` 踩过的「手写 COM vtable 翻车 3 次」是同一风险区，务必谨慎。

---

## 6. MF 软编压 IDR：可行性 + 方案

用户问「不换编码器，能不能用软编把 IDR 从 95KB 压到 30KB、GOP 从 15 拉到 300」。

### 6.1 关键澄清：GOP 方向说反了

- 现有代码 `MfFrameEncoder.cs:122` `MaxKeyframeSpacing = max(300, fps*20)` 已经是在**拉大 GOP**（约 300 帧 = 20 秒@15fps）。
- 但「压 IDR 大小」与「拉大 GOP」是**两个正交目标**：
  - **拉大 GOP** = 减少 IDR 出现的频率（P 帧为主）→ 平均带宽下降，但**直播丢帧后重同步慢**（要等下一个 IDR）。
  - **压 IDR 大小** = 单个 IDR 帧字节数下降（用 I 帧量化/QP 控制）→ 首帧快，但 IDR 质量下降。
- 直播场景的正确方向是 **GOP 适中（如 30~60）+ 低延迟 + 按需 IDR**（`OSS-REMOTE §4.2` Sunshine 结论「无限 GOP + 按需 IDR」），不是「拉大到 300 就不管」。

### 6.2 各属性可用性（权威 + 本机核对）

| 属性 | 作用 | 软 H.264 MFT 是否支持 | 设置方式 | 依据 |
|---|---|---|---|---|
| `MF_MT_MAX_KEYFRAME_SPACING` | 关键帧最大间隔（GOP） | ✅ 通用（当前代码已在设） | `IMFMediaType` 直接 Set | MSDN `mf-mt-max-keyframe-spacing-attribute` + 本机 `MfFrameEncoder.cs:122` |
| `CODECAPI_AVLowLatencyMode` | 低延迟（不重排帧、输入1帧出1帧） | ✅ H.264 编码器支持 | **需 ICodecAPI**（Vortice 未绑定） | MSDN `codecapi-avlowlatencymode` |
| `CODECAPI_AVEncCommonRateControlMode` | 码控模式（CBR/VBR/质量） | ✅ 通用 | **需 ICodecAPI** | MSDN（本页 404，属 `codecapi-avenccommonratecontrolmode`，**标未验证**：页面已移动） |
| `CODECAPI_AVEncMPVDefaultBPictureCount` | 默认 B 帧数（设为 0 关 B 帧） | ✅ H.264 支持 | **需 ICodecAPI** | MSDN（本页 404，标未验证） |
| I 帧量化/QP 单独控制 | 压 IDR 大小 | ⚠️ 软 H.264 MFT **无公开的 per-frame QP 控制属性**；`AVEncCommonQuality` 是全局质量 | 需 ICodecAPI | **未验证**（MSDN 无专门「I 帧量化」属性；`CODECAPI_AVEncCommonQuality` 影响全帧） |

### 6.3 软编压 IDR 的可行方案（按投入排序）

| 方案 | 具体做法 | 需要 API | 效果 | 可行性 |
|---|---|---|---|---|
| **A. 拉小 GOP** | `MaxKeyframeSpacing` 从 300 改到 30~60 | 已在用（`MediaTypeAttributeKeys` 已绑定） | 减少「大 IDR 卡顿尖峰」，但**不压单个 IDR 字节** | ✅ 立即能做，零新增依赖 |
| **B. 开低延迟** | `CODECAPI_AVLowLatencyMode=TRUE` | 手写 ICodecAPI（~40 行） | 消除 B 帧重排延迟，输入1帧→输出1帧 | 🟡 需手写绑定 |
| **C. CBR 码控** | `CODECAPI_AVEncCommonRateControlMode=CBR` + `AVEncCommonMeanBitRate` | 手写 ICodecAPI | 码率稳定，IDR 大小随码率上限 | 🟡 需手写绑定 |
| **D. 关 B 帧** | `CODECAPI_AVEncMPVDefaultBPictureCount=0` | 手写 ICodecAPI | 低延迟 + 减小编码复杂度 | 🟡 需手写绑定 |
| **E. 降低 IDR 字节** | 无专门的「I 帧量化」属性；只能靠全局质量/码率 | `AVEncCommonQuality`（若支持） | 间接 | ❌ 无官方 per-frame I 帧 QP 控制，**标未验证** |

> **结论**：软编能显著改善的是 **A（拉小 GOP）+ B/C/D（低延迟 + CBR + 关 B 帧）**，这些不换编码器就能做，但 B/C/D 需要**手写 ICodecAPI**（Vortice 未绑定）。「把单个 IDR 从 95KB 压到 30KB」没有公开的 per-frame I 帧量化属性，**只能靠降低码率/质量间接实现**，达不到精确控制。

---

## 7. NVIDIA 官方文档（能查到的 + 未验证）

| 项 | 结果 | 说明 |
|---|---|---|
| NVENC Video Codec SDK Programming Guide / Application Note | ❌ **未验证** | NVIDIA docs 站点 URL（`docs.nvidia.com/video-technologies/video-codec-sdk/...`）返回 404（页面已迁移，多次尝试不同路径均 404） |
| low-latency 章节 | ❌ 未验证 | 同上 |
| 并发会话限制 | ✅ **已有本仓库一手结论**：RTX 4060 Laptop = 12 路（`NVENC-DESIGN §4`，来源 NVIDIA 官方矩阵 + 本机 `nvidia-smi` 实测） | 无需再查，直接引用 |
| ffmpeg `libavcodec/nvenc.c` / `nvenc_h264.c` 源码 | ❌ 未验证 | `raw.githubusercontent.com` 与 `github.com` 均连接失败（`TypeError: fetch failed`），GitHub 受限 |
| OBS `plugins/obs-ffmpeg` NVENC 封装 | ❌ 未验证 | GitHub 受限，同上 |

> **诚实声明**：本机网络对 GitHub 与 NVIDIA 新版 docs 域名受限。NVENC 的**正确时序**已由 MSDN（微软官方、可达）权威覆盖（§2/§3）；NVENC **专用**参数（zerolatency/tune/并发细节）依赖 `OSS-REMOTE-RESEARCH §4.2`（已核 Sunshine 源码）与 `NVENC-DESIGN §4/§5`（已核 NVIDIA 矩阵），这两份是本仓库一手结论，可采信。ffmpeg/OBS 的 C 源码本次未抓到，标「未验证」。

---

## 8. 明确结论（给 Lead / stream-core）

1. **NVENC 值得做**，先修 `MfFrameEncoder.cs` 硬件分支的异步时序 bug（§1.2/§4.2 清单），不是「回退软编」。
2. **最小改动在 `MfFrameEncoder.cs` 一个文件内**，不引入 NVENC SDK（守住「产物 <5MB」），复用 Vortice 已绑定的 `IMFTransform`/`MediaEventTypes`。
3. **需要额外投入的只有**：① 手写 `ICodecAPI`（~40 行，为软编低延迟 + 压 IDR 服务，Vortice 未绑定）；② 若 Vortice 无 `BeginGetEvent/EndGetEvent`，补该事件异步绑定。
4. **软编压 IDR 的现实边界**：能压的是「GOP 大小 + 低延迟 + CBR + 关 B 帧」；**不能**精确控制单个 IDR 字节数（无 per-frame I 帧 QP 公开属性），只能靠码率/质量间接降。

---

## 9. 相关文档 / 来源

- MSDN `Asynchronous MFTs`：`https://learn.microsoft.com/en-us/windows/win32/medfound/asynchronous-mfts`
- MSDN `IMFTransform::ProcessOutput`：`https://learn.microsoft.com/en-us/windows/win32/api/mftransform/nf-mftransform-imftransform-processoutput`
- MSDN `MF_MT_MAX_KEYFRAME_SPACING`：`https://learn.microsoft.com/en-us/windows/win32/medfound/mf-mt-max-keyframe-spacing-attribute`
- MSDN `CODECAPI_AVLowLatencyMode`：`https://learn.microsoft.com/en-us/windows/win32/medfound/codecapi-avlowlatencymode`
- 本仓库 `Services/MfFrameEncoder.cs`、`Services/MfH264Encoder.cs`、`docs/research/NVENC-DESIGN-20261122.md`、`MEDIA-CAPABILITY-20261122.md`、`OSS-REMOTE-RESEARCH-20261122.md`（Sunshine `src/video.cpp` 低延迟参数）
- Vortice.MediaFoundation 3.8.3（本机 NuGet 程序集反射）
