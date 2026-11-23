# GPU 截图 → GPU 编码零拷贝路径调研（官方文档 + 开源实现）

> 日期：2026-11-23
> 作者：gpu-zero-copy（dsh-team `team_mwsomf2tj73xd7` / member `m_mwsosfpfjamcdf` / task `task_mwsosxakjcdl4o`）
> 性质：**纯调研**。未改业务代码、未改 csproj、未碰 `Relay/SessionRecorder.cs` 与 `Services/MfFrameEncoder.cs`、未提交 git。
> 证据等级：**【代码事实】**＝当前工作区源码逐行可证；**【官方文档】**＝MSDN/learn.microsoft.com 直取原文；**【开源源码】**＝GitHub API 抓取的 Sunshine / OBS master 源码片段（存 `artifacts/zero-copy-src/`，file:line 可回查，GPL-3.0 只读参考不可抄）；**【已有实测】**＝本仓库 `docs/research/` 内前序任务的实测结论，附出处。
> 前置必读：`NVENC-DESIGN-20261122.md`、`NVENC-IMPLEMENTATION-20261123.md`、`MEDIA-CAPABILITY-20261122.md`、`WGC-VERIFY-20261122.md`、`WGC-SNAPSHOT-20261122.md`、`OSS-REMOTE-RESEARCH-20261122.md`。

---

## 0. 一句话结论（先回答验收问题）

1. **当前是不是 CPU 截图 → 内存 → GPU？** —— **是**。无论 BitBlt（本来就在 CPU）还是 WGC（GPU 纹理），帧都被**读回 CPU 内存**，在 CPU 上做 BGRA→NV12，再 `Marshal.Copy` 进 Media Foundation 系统内存缓冲交给编码器。编码器（本机 NVIDIA H.264 MFT 与微软软 MFT）**都不是 D3D11-aware**，所以 MF 这条链路的"上 GPU"是靠编码器内部再上传，**像素数据必然落过 CPU**。

2. **能否 GPU 截图直接 GPU 编码不落 CPU？** —— **能，但只有一条路**：**NVENC SDK 的 D3D11 直通**（`NV_ENC_REGISTER_RESOURCE` → `NvEncMapInputResource` → `NvEncEncodePicture`），Sunshine 与 OBS 都是这么做的（见 §4 file:line）。**走 Media Foundation 的 GPU 零拷贝在本机是死路**，因为本机枚举到的 NVIDIA H.264 MFT 标记为「非 D3D11」（`MF_SA_D3D11_AWARE=FALSE`），MF 无法接收 D3D11 纹理（§3）。

3. **关键澄清：NVENC 不接受 BGRA，只收 YUV。** 所以"零拷贝"的正确含义是 **GPU 抓帧 → GPU 上做 BGRA→NV12（shader / VideoProcessor）→ NV12 纹理注册给 NVENC → GPU 编码**，像素全程不离开显存；不是"把 WGC 的 BGRA 纹理直接塞给编码器"。

4. **落地判断**：真正零拷贝 = 引入 NVENC SDK P/Invoke（约 1~2 天 + 高风险），收益是砍掉 CPU 读回（staging+Map ≈ 3~6ms）+ CPU BGRA→NV12（≈ 3~6ms）+ 系统内存上传（NV12 3.1MB 拷贝）三段。**但前提是抓屏已经换成 WGC**（BitBlt 是 CPU 抓屏，本身就是"CPU 截图"，无 GPU 纹理可直通）。所以路线顺序是 **WGC（已就绪）→ GPU 色彩转换 → NVENC SDK**，而**不是**在 MF 上想办法。

---

## 1. 当前 SoulLink 的实际数据流（逐行定位）

### 1.1 抓屏：WGC 已经把帧留在 GPU 上，但立刻被读回 CPU

`Relay/WgcCapture.cs`：

- `CaptureBgra` 拿到 `_stashedTexture`（GPU 纹理的 COM 指针，FrameArrived 时已 AddRef 持有）后，直接调 `ReadBackTexture` —— **WgcCapture.cs:242-251**：

```csharp
public byte[]? CaptureBgra(out int width, out int height) {
    ...
    return ReadBackTexture(_stashedTexture, width, height);   // :251
}
```

- `ReadBackTexture` 是"GPU 纹理 → CPU BGRA"：建 staging 纹理 → `CopyResource` → `Map(READ)` → `Marshal.Copy` 到托管 `byte[]` —— **WgcCapture.cs:398-440**：

```csharp
var staging = _device.CreateTexture2D(desc);                 // :418  CPU 可读 staging
_context.CopyResource(staging, srcTex);                       // :421  GPU→staging
var mapped = _context.Map(staging, 0, MapMode.Read, ...);     // :422  阻塞等待拷贝完成
Marshal.Copy(mapped.DataPointer, tight, 0, tight.Length);     // :429  显存→系统内存
```

  注释自证（:31）："GPU 纹理不能 IDXGISurface::Map，必须 staging 纹理 + CopyResource + Map(READ)"。

- BitBlt 兜底后端 `Relay/BitBltCapture.cs:117-155` 则**从头就是 CPU**：`GetDC` → `BitBlt` 到兼容位图 → `Image.FromHbitmap` → `LockBits` 读像素。这条路径连"GPU 截图"都不是。

### 1.2 编码：CPU BGRA → CPU NV12 → 系统内存 MF 缓冲

`Relay/SessionRecorder.cs` 视频线程 `Loop()`：

- `Capture.CaptureBgra(...)`（:193，产出 CPU `byte[]` BGRA）
- `BgraToNv12(bgra, ew, eh, width*4)`（:215，**纯 C# 逐像素** BT.601 转换，实现在 :387-421）
- `encoder.EncodeNv12(nv12, ...)`（:218）

`Services/MfFrameEncoder.cs` 编码器：

- `EncodeNv12` 每帧 `MFCreateMemoryBuffer` + `Marshal.Copy(nv12, 0, pData, nv12.Length)` —— **MfFrameEncoder.cs:180-186**。这是**系统内存缓冲**（不是 D3D 纹理）。
- MFT 选择：`swColl`（软件）优先、`hwColl`（硬件）兜底 —— **MfFrameEncoder.cs:96 / :99**。两个 MFT 本机都报「非 D3D11」（`MEDIA-CAPABILITY §2.6`）。

### 1.3 结论：这是一条 "GPU(或CPU)截图 → CPU 内存 → (MF 内部再上传) GPU/CPU 编码" 的链路

数据流示意（WGC 分支）：

```
WGC 帧池(GPU BGRA 纹理)
  → staging 纹理 + CopyResource + Map(READ)      ← GPU→CPU 拷贝（第 1 次落内存）
  → Marshal.Copy → byte[] BGRA（托管堆）
  → BgraToNv12（CPU 逐像素）→ byte[] NV12          ← CPU 色彩转换
  → MFCreateMemoryBuffer + Marshal.Copy            ← 第 2 次内存拷贝（进 MF 系统缓冲）
  → MFT（非 D3D11）→ 若为硬编，MFT 内部再上传 GPU → NVENC
```

这条链路里有 **2 次全帧内存拷贝 + 1 次 CPU 色彩转换**，像素数据在显存与系统内存之间至少往返 1 次。

---

## 2. 零拷贝需要具备的三个条件（缺一不可）

| # | 条件 | 本机状态 | 依据 |
|---|---|---|---|
| ① | 抓帧直接产出 **D3D11 纹理**（不读回 CPU） | ✅ **已具备**：WGC `Direct3D11CaptureFramePool` 天然给 GPU 纹理，`WgcCapture.OnFrameArrived` 已拿到 `ID3D11Texture2D` 指针并持有 | 【代码事实】`WgcCapture.cs:171-200`；【已有实测】`WGC-SNAPSHOT`：RDP 会话内 CreateForWindow 抓到 1920×1080 非黑 100%，且 D3D11 硬件设备成功 |
| ② | 色彩转换 BGRA→NV12 在 **GPU** 上做（不是 CPU 的 `BgraToNv12`） | ⚠️ **未实现**：NVENC 只收 YUV（NV12/P010/AYUV/…），不接受 BGRA，必须有一次转换。可上 GPU（shader 或 `ID3D11VideoProcessor`） | 【开源源码】Sunshine `display_vram.cpp:494-563` 用像素着色器做转换（§4.2） |
| ③ | 编码器能**直接消费 D3D11 纹理** | ⚠️ **本机 MF 死路，需 NVENC SDK**：NVIDIA H.264 MFT 非 D3D11（§3）；只有 NVENC SDK 的 `NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX` 能直接吃纹理 | 【已有实测】`MEDIA-CAPABILITY §2.6`；【开源源码】Sunshine/OBS（§4） |

**推论**：条件 ① 白送、② 是新工作、③ 决定技术栈。③ 的唯一可行解是 NVENC SDK，这推翻了"复用现有 MF MFT 就够"的直觉——**MF 的 D3D11 感知是编码器自己的属性，本机两个 MFT 都不开**。

---

## 3. 为什么 Media Foundation 的 GPU 零拷贝在本机走不通（官方文档依据）

MF 有一条官方规定的"D3D11 直通"协议，但它是**双向协商**，编码器不支持就没法用：

| 步骤 | 官方机制 | 本机结果 |
|---|---|---|
| 探测 MFT 是否 D3D11-aware | 读 `IMFTransform::GetAttributes` 的 `MF_SA_D3D11_AWARE`（BOOL 存 UINT32，只读） | **FALSE**（NVIDIA H.264 Encoder MFT 与微软 H264 Encoder MFT 都报「非 D3D11」） |
| 若为 TRUE，握手 | 客户端发 `MFT_MESSAGE_SET_D3D_MANAGER`，ulParam 传 `IMFDXGIDeviceManager` | 因上一步 FALSE，**不得发送**（官方明说"unless the MFT returns TRUE ... should not send"） |
| 传 D3D11 纹理 | `MFCreateDXGISurfaceBuffer` 把 `ID3D11Texture2D` 包成 `IMFMediaBuffer`（riid 只支持 `IID_ID3D11Texture2D` / `IID_ID3D12Resource`） | 编码器非 D3D11-aware，不接受纹理样本 |

**官方文档原文（已直取 learn.microsoft.com）**：

- `MF_SA_D3D11_AWARE`：*"If this attribute is zero (FALSE), the MFT does not support Direct3D 11, and the client should not send the MFT_MESSAGE_SET_D3D_MANAGER message."* —— <https://learn.microsoft.com/en-us/windows/win32/medfound/mf-sa-d3d11-aware>
- `MFT_MESSAGE_SET_D3D_MANAGER`：*"The client should not send this message unless the MFT returns TRUE for the MF_SA_D3D_AWARE attribute (MF_SA_D3D11_AWARE for Direct3D 11)."* —— <https://learn.microsoft.com/en-us/windows/win32/medfound/mft-message-set-d3d-manager>
- `Direct3D-Aware MFTs`：*"An video MFT is considered Direct3D-aware if it can process samples that contain Direct3D surfaces."* —— <https://learn.microsoft.com/en-us/windows/win32/medfound/direct3d-aware-mfts>
- `MFCreateDXGISurfaceBuffer`：*"Creates a media buffer to manage a DXGI surface. riid must be IID_ID3D11Texture2D or IID_ID3D12Resource."* —— <https://learn.microsoft.com/en-us/windows/win32/api/mfapi/nf-mfapi-mfcreatedxgisurfacebuffer>

**结论**：MF 的 D3D11 直通需要"编码器自己声明支持"，本机 NVIDIA MFT 明确不支持，因此 **在 MF 框架内做 GPU 零拷贝是无解的**。要真零拷贝，必须绕过 MF，直接走 NVENC SDK（NVENC 本质是一个 GPU 上的独立编码引擎，MF 只是它的一个"系统内存包装"）。

> 补充：这是不是说明 NVENC 硬件本身"非 D3D11"？不是。NVENC 芯片一定吃显存纹理。是 NVIDIA 提供给 Windows 的那个 **MF 包装 MFT** 选择只暴露"系统内存 NV12 → 内部上传"这一种输入，没暴露 D3D11 直通。Sunshine/OBS 直接用 NVENC SDK 绕开了这个包装。

---

## 4. 开源实现的零拷贝证据（file:line）

### 4.1 业界标准三连：Register → Map → Encode

NVENC SDK 的 D3D11 直通只有三步（Sunshine 与 OBS 完全一致）：

1. 把 D3D11 纹理注册为 NVENC 资源：`NV_ENC_REGISTER_RESOURCE{ resourceType = NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX, resourceToRegister = ID3D11Texture2D* }` → `NvEncRegisterResource`
2. 编码前映射：`NvEncMapInputResource`
3. 把映射结果填进 `NV_ENC_PIC_PARAMS.inputBuffer` → `NvEncEncodePicture`

### 4.2 Sunshine（master，2026-11-23 抓取，存 `artifacts/zero-copy-src/`）

**（a）D3D11 NV12 纹理的创建与注册** —— `src/nvenc/nvenc_d3d11_native.cpp:37-64`：

```cpp
D3D11_TEXTURE2D_DESC desc = {};           // :38
desc.Width  = encoder_params.width;       // :39
desc.Height = encoder_params.height;      // :40
desc.Format = dxgi_format_from_nvenc_format(encoder_params.buffer_format);  // :44  NV12/P010/…
desc.Usage  = D3D11_USAGE_DEFAULT;        // :47
desc.BindFlags = D3D11_BIND_RENDER_TARGET; // :48  ← 该纹理既是"色彩转换的渲染目标"又是"NVENC 输入"
d3d_device->CreateTexture2D(&desc, nullptr, &d3d_input_texture);  // :47

NV_ENC_REGISTER_RESOURCE register_resource = { .version = NV_ENC_REGISTER_RESOURCE_VER };  // :54
register_resource.resourceType = NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX;   // :55  ★ 关键
register_resource.width  = encoder_params.width;                        // :56
register_resource.height = encoder_params.height;                       // :57
register_resource.resourceToRegister = d3d_input_texture.GetInterfacePtr();  // :58  ★ 纹理指针直通
register_resource.bufferFormat = encoder_params.buffer_format;          // :59
register_resource.bufferUsage  = NV_ENC_INPUT_IMAGE;                    // :60
nvenc->nvEncRegisterResource(encoder, &register_resource);              // :62
```

同时构造函数把 `device = d3d_device`、`device_type = NV_ENC_DEVICE_TYPE_DIRECTX` —— `nvenc_d3d11_native.cpp:15-17`，编码会话用 `nvEncOpenEncodeSessionEx` 带原生 `ID3D11Device` —— `nvenc_base.cpp:590-598`。

**（b）每帧 Map → Encode** —— `src/nvenc/nvenc_base.cpp:728-766`：

```cpp
NV_ENC_MAP_INPUT_RESOURCE mapped_input_buffer = { .version = NV_ENC_MAP_INPUT_RESOURCE_VER };  // :728
mapped_input_buffer.registeredResource = registered_input_buffer;   // :729
nvenc->nvEncMapInputResource(encoder, &mapped_input_buffer);        // :731  ★ 映射，不拷贝

NV_ENC_PIC_PARAMS pic_params = { .version = NV_ENC_PIC_PARAMS_VER };  // :741
pic_params.inputBuffer  = mapped_input_buffer.mappedResource;         // :747  ★ 直接给映射句柄
pic_params.bufferFmt    = mapped_input_buffer.mappedBufferFmt;        // :748
pic_params.outputBitstream = output_bitstream;                        // :749
nvenc->nvEncEncodePicture(encoder, &pic_params);                      // :752
```

**（c）BGRA→NV12 的 GPU 色彩转换** —— `src/platform/windows/display_vram.cpp:494-563` `convert()`：把抓到的捕获纹理作为着色器输入（`PSSetShaderResources` :521），渲染到一个 NV12/P010 渲染目标（`OMSetRenderTargets` :524/:535），用像素着色器做 RGB→YUV（`Draw` :530/:539）。**这是"GPU 上把 BGRA 转成 NV12"，替代了 SoulLink 的 CPU `BgraToNv12`**。捕获纹理从 DXGI 接口取 —— `display_vram.cpp:1424` `res->QueryInterface(IID_ID3D11Texture2D, &src)`；抓帧与编码纹理之间的 GPU 拷贝是 `CopyResource`（:1596/:1643 等，纯显存内拷贝）。

**（d）Sunshine 的软/硬帧切换旁证** —— `src/video.cpp`：硬件帧路径 `AV_PIX_FMT_D3D11` + `dxgi_init_avcodec_hardware_input_buffer`（:4023-4046，用 `AVD3D11VADeviceContext.device = 同一个 ID3D11Device`）；软件帧路径才有 `sws_scale_frame`（:268）与 `av_hwframe_transfer_data`（:294，注释原话 *"transfer from main memory to vram memory"*）。**这说明 Sunshine 只有在软件编码时才把数据搬回主存，硬件路径不搬。**

### 4.3 OBS（master，2026-11-23 抓取，存 `artifacts/zero-copy-src/`）

`plugins/obs-nvenc/nvenc-d3d11.c`：

- NV12/P010 纹理 + 注册 —— `nvenc-d3d11.c:98-127`：

```c
desc.Format = p010 ? DXGI_FORMAT_P010 : DXGI_FORMAT_NV12;   // :103
device->lpVtbl->CreateTexture2D(device, &desc, NULL, &tex); // :109
NV_ENC_REGISTER_RESOURCE res = { NV_ENC_REGISTER_RESOURCE_VER };  // :117
res.resourceType = NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX;       // :118
res.resourceToRegister = tex;                                 // :119
res.bufferFormat = p010 ? NV_ENC_BUFFER_FORMAT_YUV420_10BIT : NV_ENC_BUFFER_FORMAT_NV12;  // :120
```

- 编码时从捕获共享句柄打开纹理 → GPU `CopyResource` 到注册纹理 → Map → 编码 —— `nvenc-d3d11.c:188 / :243-245 / :254 / :263`：

```c
hr = device->lpVtbl->OpenSharedResource(device, handle, &IID_ID3D11Texture2D, &input_tex);  // :188
km->lpVtbl->AcquireSync(km, lock_key, INFINITE);        // :243
context->lpVtbl->CopyResource(context, output_tex, input_tex);  // :245  ★ GPU 内拷贝
map.registeredResource = nvtex->res;                    // :254
nv.nvEncMapInputResource(enc->session, &map);           // :254
return nvenc_encode_base(enc, bs, nvtex->mapped_res, pts, ...);  // :263
```

> 与 Sunshine 唯一差别：OBS 的捕获链路（`win-capture` 的 D3D11 duplication / 渲染捕获）已经把帧放进 NV12 纹理，所以它只有 `CopyResource` 拷贝、没有额外的着色器转换。Sunshine 因为 WGC 给的是 BGRA，所以多一层 GPU 着色器做 RGB→YUV。

### 4.4 两个实现的一致性结论

| 环节 | Sunshine | OBS | 是否落 CPU |
|---|---|---|---|
| 抓帧 | WGC → `ID3D11Texture2D` | D3D11 duplication → 共享纹理 | ❌ 不落 |
| 色彩转换 | GPU 像素着色器 BGRA→NV12/P010 | 捕获侧已是 NV12 | ❌ 不落（GPU） |
| 纹理拷贝 | `CopyResource`（显存内） | `CopyResource`（显存内） | ❌ 不落 |
| 交给编码器 | `NvEncRegisterResource(DIRECTX)` + Map | 同左 | ❌ 不落 |
| 编码 | `NvEncEncodePicture`（GPU） | 同左 | ❌ 不落 |

**结论**：Sunshine/OBS 的"零拷贝"= 像素数据**全程在显存**，CPU 只做"注册/映射/提交"的轻量控制，不做任何全帧像素搬运。这正是 SoulLink 当前缺失的部分。

---

## 5. 关键陷阱与约束（调研中确认的硬边界）

1. **NVENC 不接受 BGRA，必须 NV12/P010/AYUV**（`NV_ENC_BUFFER_FORMAT`）。Sunshine `nvenc_utils.cpp` 只映射 NV12/P010/AYUV/YUV444；OBS 只用 NV12/P010。所以 WGC 的 BGRA 纹理**不能直接注册**，必须先转 NV12——转换可以做在 GPU 上（shader/VideoProcessor），**但不能省**。
2. **本机 NVIDIA MF MFT 非 D3D11**（`MEDIA-CAPABILITY §2.6`），MF 直通关闭（§3）。这是"为什么必须 SDK"的根因。
3. **NVENC SDK 不是新依赖，是系统自带的驱动 DLL**：Sunshine 用 `LoadLibraryEx("nvEncodeAPI64.dll", LOAD_LIBRARY_SEARCH_SYSTEM32)` 从 system32 加载（`nvenc_dynamic_factory.cpp:57`），**不随程序分发**。所以"产物 <5MB"（AGENTS.md 铁律）**不受影响**，前提是 P/Invoke 动态加载、不把 DLL 打进包。这是能同时守住"零拷贝 + 零体积"的关键事实。
4. **驱动版本门槛**：Sunshine 要求驱动 ≥ 456.71（`nvenc_dynamic_factory.cpp:32`），并按 `NvEncodeAPIGetMaxSupportedVersion` 选 SDK 版本（11.0~13.1 多档）。本机 621.01 远超门槛（`NVENC-DESIGN §4`），无碍。
5. **RDP 会话内 D3D11 硬件设备可用但有显存前提**：`WGC-SNAPSHOT §1/§5` 实测显存 2122MiB 可用时硬件设备成功、150MiB 时回退 WARP。零拷贝要求**必须拿到硬件 D3D11 设备**（WARP 软件光栅不产生显存纹理），多账号并发时显存是共享的（`WGC-VERIFY §2.3`）。
6. **DXGI Desktop Duplication 在 RDP 不可用**（`MEDIA-CAPABILITY`/`STREAM-PLAN-V2`），所以 GPU 抓帧来源只能是 **WGC**（已实测可用），不能退回到"桌面复制 + NVENC"的 OBS 经典组合。
7. **异步时序坑已被前序任务定位**：`NVENC-IMPLEMENTATION-20261123.md` 已给出 MF 硬件 MFT 的事件驱动时序缺陷；SDK 路径同样异步（`NvEncRegisterAsyncEvent` + completion event，Sunshine `nvenc_base.cpp:691-696, 750, 761`），实现时要用事件而非轮询。

---

## 6. SoulLink 分阶段改造路线（收益 / 成本 / 风险）

> 前提约束（不重复前序结论）：抓屏 31ms（BitBlt）是最大瓶颈，NVENC 只省编码侧；所以零拷贝的价值**只在抓屏已切 WGC 后**才兑现。以下按"先搭好 WGC 纹理直通骨架、再上 SDK"排。

### 阶段 0（已具备，无需新代码）：WGC 纹理直通骨架

`WgcCapture` 已在 `OnFrameArrived` 里拿到 `ID3D11Texture2D`（`_stashedTexture`，:171-200）并持有 COM 引用。这是零拷贝的"输入"，不需要重写抓屏。`IVideoEncoder.Encode(IntPtr d3dTexture, ...)` 接口占位已预留（`IVideoEncoder.cs:44`）。

### 阶段 1：GPU 色彩转换（BGRA→NV12，替代 CPU `BgraToNv12`）

- **做什么**：新增一个 `GpuColorConverter`（独立工具类，不动业务热路径），用 `ID3D11VideoProcessor`（`ID3D11VideoDevice::CreateVideoProcessor`，官方接口已核实存在 <https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nn-d3d11-id3d11videoprocessor>）或手写像素着色器，把 WGC 的 B8G8R8A8 纹理渲染成 NV12 纹理。Sunshine 的 `display_vram.cpp:494-563` 是可直接参照的着色器方案。
- **收益**：砍掉 CPU `BgraToNv12`（实测/估算 3~6ms/帧，`NVENC-DESIGN §6`）与 staging 读回（3~6ms），CPU 占用显著下降。
- **成本**：1~2 人日（Vortice.Direct3D11 已引，着色器/VideoProcessor 均为 D3D11 标准能力）。
- **风险**：中。VideoProcessor 的输出格式协商（B8G8R8A8→NV12）与尺寸对齐（NV12 要求偶数宽高）是常见坑；着色器方案要自己管常量缓冲与 UV 下采样。**可独立于编码器先行验证**（先转出来、再 `CopyResource` 回读一帧比对 CPU 结果）。

### 阶段 2：NVENC SDK 直通（真正的零拷贝编码）

- **做什么**：新增 `NvencSdkEncoder`（实现 `IVideoEncoder`），P/Invoke `nvEncodeAPI64.dll`：`NvEncodeAPIGetMaxSupportedVersion` → `NvEncodeAPICreateInstance` → `NvEncOpenEncodeSessionEx(DIRECTX)` → `NvEncRegisterResource(DIRECTX)` → 每帧 `NvEncMapInputResource` + `NvEncEncodePicture` + `NvEncLockBitstream`。结构体与流程照抄 Sunshine/OBS（§4，GPL 只能参考接口签名与流程，**不能照抄实现**，但 NVENC SDK 头文件本身是 NVIDIA 的，流程属公共知识）。
- **收益**：像素数据**全程不落 CPU**，编码侧 CPU 降到接近 0；同时消除 MFT 异步时序 bug（`NVENC-IMPLEMENTATION`）带来的复杂度。对 4~8 账号多开，CPU 节省叠加明显（`MEDIA-CAPABILITY §5.2` 估算硬编路径 CPU 仅 ~1.3% 单核/账号下界）。
- **成本**：1~2 人日（P/Invoke 面较大：约 10+ 个函数、多个结构体需按 SDK 版本精确对齐；异步完成事件）。**这是本项目"手写 COM vtable 翻车 3 次"的同类风险区**（`Mp4Writer` 教训），必须逐结构体验证。
- **风险**：**高**。
  - SDK 版本兼容（多档 ABI，Sunshine 分了 11.0~13.1 五档）；
  - 结构体对齐/Packing 错误 → 静默坏帧或崩溃；
  - RDP 内显存不足时 `CreateTexture2D`/设备创建失败（`WGC-SNAPSHOT`），需保留 WARP/软编/MF 降级链；
  - GeForce 并发上限 12 路（`NVENC-DESIGN §4`），多账号仍要兜底。

### 阶段 3：接线 + 降级链

- **做什么**：`SessionRecorder.Loop` 里加分支：`SOULLINK_CAPTURE_MODE=WGC` 且 NVENC SDK 可用 → 走"WGC 纹理 → GPU 转 NV12 → NVENC SDK"；否则维持现有 MF 软/硬编回退。降级链：**WGC+NVENC SDK → WGC+MF（现状）→ BitBlt+MF（现状）**。
- **收益**：把零拷贝作为"最优路径"而非"唯一路径"，守住可用性。
- **成本**：0.5 人日（在既有 `IVideoEncoder`/`IScreenCapture` 抽象上接，接口已留）。
- **风险**：低（纯接线，但需真机逐形态验收：独占全屏/无边框/最小化，见 `WGC-VERIFY §4`）。

### 收益总账（相对现状，WGC 分支）

| 段 | 现状 | 零拷贝后 | 依据 |
|---|---|---|---|
| staging 读回 | 3~6ms CPU | 0（不读回） | `WgcCapture.cs:421-429`；量级见 `NVENC-DESIGN §6` |
| BGRA→NV12 | 3~6ms CPU | GPU 着色器（CPU≈0） | `BgraToNv12` :387-421；Sunshine 着色器 |
| 系统内存上传 | NV12 3.1MB 拷贝 | 0（纹理直通） | `MfFrameEncoder.cs:186` |
| 编码提交 | MF 异步时序复杂 | SDK 事件驱动 | §4 |

**关键提醒（不夸大）**：这些收益是**编码侧**的。端到端延迟的最大头是抓屏（BitBlt 31ms）与推流节拍（66ms，见 `LATENCY-BENCHMARK-20261123.md`）。**零拷贝是"锦上添花"的第 2~3 步，不能替代"换 WGC 抓屏 + 修节拍 + 事件驱动硬编"这前三步**。

---

## 7. 明确结论（给 Lead）

1. **当前是"GPU 截图 → CPU 内存 → GPU 编码"，确认无疑**，且有 2 次全帧拷贝 + 1 次 CPU 色彩转换（§1）。
2. **GPU 零拷贝可行，但唯一路线是 NVENC SDK 直通**，不是 MF（本机 NVIDIA MF MFT 非 D3D11，MF 直通官方协议要求编码器自声明支持，已排除）（§3）。
3. **NVENC SDK 不是体积负担**：驱动自带 `nvEncodeAPI64.dll`，动态加载即可，守住"<5MB"（§5.3）。
4. **技术栈结论**：在 `IVideoEncoder.Encode(IntPtr d3dTexture)` 占位上实现 `NvencSdkEncoder`，输入是"已经 GPU 转好的 NV12 纹理"；配套一个 GPU 色彩转换器（阶段 1）。
5. **顺序**：先做阶段 1（GPU 色彩转换，可独立验证、风险中），再做阶段 2（SDK 直通，风险高、需逐结构体验证），最后阶段 3 接线 + 降级。**不要跳步直接写 SDK**（手写 COM 翻车 3 次的前科）。
6. **不要指望零拷贝解决"卡"**：端到端瓶颈是抓屏与节拍，零拷贝只在抓屏切 WGC 后省编码侧 CPU/延迟，是收益明确但非首位的优化。

---

## 8. 证据索引

| 结论 | 证据 |
|---|---|
| 现状是 CPU 读回 + CPU 转 NV12 + 系统内存 MF | `WgcCapture.cs:242-251,398-440`；`SessionRecorder.cs:193,215,218,387-421`；`MfFrameEncoder.cs:180-186` |
| 本机 NVIDIA/微软 MF MFT 均非 D3D11 | `MEDIA-CAPABILITY-20261122.md §2.6` |
| MF D3D11 直通协议（aware → SET_D3D_MANAGER → DXGISurfaceBuffer） | MSDN `MF_SA_D3D11_AWARE` / `MFT_MESSAGE_SET_D3D_MANAGER` / `MFCreateDXGISurfaceBuffer` / `Direct3D-Aware MFTs` |
| WGC 在 RDP 产 GPU 纹理、硬件 D3D11 设备可用 | `WGC-SNAPSHOT-20261122.md`；`WGC-VERIFY-20261122.md` |
| Sunshine NVENC D3D11 直通 file:line | `nvenc_d3d11_native.cpp:37-64`、`nvenc_base.cpp:590-598,728-766`、`display_vram.cpp:494-563,1424`（存 `artifacts/zero-copy-src/`） |
| OBS NVENC D3D11 直通 file:line | `plugins/obs-nvenc/nvenc-d3d11.c:98-127,188,243-245,254,263`（存 `artifacts/zero-copy-src/`） |
| NVENC SDK 动态加载、不随程序分发 | Sunshine `nvenc_dynamic_factory.cpp:57`；`nvenc_shared_dll.h` |
| NVENC 并发 12 路、驱动 621.01 | `NVENC-DESIGN-20261122.md §4` |

## 9. 参考资料

- 开源源码快照（本次抓取，GPL-3.0 只读）：`artifacts/zero-copy-src/README.md`
- 前序调研：`NVENC-DESIGN-20261122.md`、`NVENC-IMPLEMENTATION-20261123.md`、`MEDIA-CAPABILITY-20261122.md`、`WGC-VERIFY-20261122.md`、`WGC-SNAPSHOT-20261122.md`、`OSS-REMOTE-RESEARCH-20261122.md`、`LATENCY-BENCHMARK-20261123.md`
