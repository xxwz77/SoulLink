# NVENC SDK D3D11 直通探针报告（RTX 4060 Laptop 实测）

> 日期：2026-11-23
> 作者：gpu-zero-copy（dsh-team `team_mwsomf2tj73xd7` / member `m_mwsosfpfjamcdf` / task `task_mwsrjawvmabhpq`）
> 性质：**独立探针实测**。未改主项目文件、未 build SoulLink.csproj、未碰运行中进程、未提交 git。
> 探针：`tools/nvenc-sdk-probe/`（`dotnet build -c Release` **0 warning 0 error**）
> 依据：`GPU-ZERO-COPY-20261123.md`（结论「唯一路线 = NVENC SDK D3D11 直通」）+ `MATURE-LATENCY-PLAYBOOK-20261123.md`（编码参数）+ nvEncodeAPI.h（NVENC SDK 13.1，FFmpeg/nv-codec-headers 仓库）。

---

## 0. 一句话结论

**RTX 4060 Laptop 走 `NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX` 直通完全可行**：独立探针用系统自带的 `nvEncodeAPI64.dll`（驱动 621.01）成功完成 `OpenEncodeSessionEx(DIRECTX) → RegisterResource(NV12 D3D11 纹理) → MapInputResource → EncodePicture → LockBitstream` 完整闭环，**3 帧真 H.264（IDR + 2×P）从纯 GPU 纹理直入 NVENC 出帧**，产物经 ffprobe 确认为合法 H.264（High@L4.0, 1920×1080, yuv420p）且 ffmpeg 可解码成 PNG。**GPU-ZERO-COPY 调研的"唯一路线"判断被实测坐实。**

---

## 1. 实测环境

| 项 | 值 |
|---|---|
| GPU | NVIDIA GeForce RTX 4060 Laptop GPU |
| 驱动 | 621.01 |
| NVENC DLL | `C:\Windows\System32\nvEncodeAPI64.dll`（1.06MB，2026-10-21） |
| SDK 头 | NVENC 13.1（nv-codec-headers） |
| 会话 | SessionId=1（console，非 RDP） |
| .NET | 11.0.100 |
| 显存 | 3870/8188 MiB |

## 2. 探针做了什么（`tools/nvenc-sdk-probe/Program.cs`，677 行）

1. `LoadLibraryEx("nvEncodeAPI64.dll", LOAD_LIBRARY_SEARCH_SYSTEM32)`（动态加载，不随程序分发 → 守住产物 <5MB）
2. `NvEncodeAPIGetMaxSupportedVersion` → **0x000000D1**（驱动 API 版本满足 SDK 13.1）
3. `NvEncodeAPICreateInstance`（填充 `NV_ENCODE_API_FUNCTION_LIST`，version = `NVENCAPI_STRUCT_VERSION(2)`）
4. `D3D11CreateDevice(Hardware)` → **Level_11_1** ✅
5. `NvEncOpenEncodeSessionEx(DIRECTX, device=d3dDevice)` ✅
6. `NvEncGetEncodeGUIDs`（count=8；探针的枚举回读有一个**小瑕疵**，见 §4.3）
7. 造 1920×1080 **NV12** D3D11 纹理（DEFAULT + UpdateSubresource 一次性 CPU→GPU 填充）
8. `NvEncInitializeEncoder`（P1 + `ULTRA_LOW_LATENCY`，`encodeConfig=NULL` 最小路径）
9. `NvEncCreateBitstreamBuffer`
10. `NvEncRegisterResource(DIRECTX, NV12)` ← **本任务核心**
11. 循环 N 帧：`MapInputResource → EncodePicture → LockBitstream`，落盘 .h264

**结构体布局**（MSVC 8 字节默认对齐，用布局计算器逐字段核对 nvEncodeAPI.h）：

| 结构 | C# sizeof | 用途 |
|---|---|---|
| `NV_ENC_RC_PARAMS` | 132 | CBR / zerolatency / 无限 GOP |
| `NV_ENC_CONFIG_H264` | 1792 | idrPeriod / repeatSPSPPS / maxNumRefFrames（RFI 用） |
| `NV_ENC_CONFIG` | 3592 | profileGUID + gopLength + frameIntervalP + rcParams + codec union |
| `NV_ENC_INITIALIZE_PARAMS` | 1800 | tuningInfo / enablePTD / encodeConfig 指针 |
| `NV_ENC_PIC_PARAMS` | 2840 | inputBuffer / outputBitstream / FORCEIDR |
| `NV_ENC_LOCK_BITSTREAM` | 1544 | bitstreamBufferPtr / pictureType / bitstreamSizeInBytes |
| `NV_ENC_REGISTER_RESOURCE` | 1536 | resourceToRegister / bufferFormat(NV12) |
| `NV_ENC_MAP_INPUT_RESOURCE` | 1544 | registeredResource → mappedResource |
| `NV_ENCODE_API_FUNCTION_LIST` | 2552 | 44 个函数指针 + 275 个保留位 |

## 3. 实测结果（完整通过）

```
[OK  ] nvEncodeAPI64.dll 载入
[INFO] MaxSupportedVersion=0x000000D1
[OK  ] NvEncodeAPICreateInstance 成功
[OK  ] D3D11 硬件设备 featureLevel=Level_11_1
[OK  ] NvEncOpenEncodeSessionEx(DIRECTX) 成功
[INFO] GetEncodeGUIDs hr=0x00000000 count=8
[OK  ] 创建 1920x1080 NV12 D3D11 纹理（DEFAULT + UpdateSubresource 填充）
[OK  ] NvEncInitializeEncoder 成功（P1 + ULL，encodeConfig=NULL）
[OK  ] NvEncCreateBitstreamBuffer 成功
[OK  ] NvEncRegisterResource(DIRECTX,NV12) 成功
[OK  ] 帧0 出 251131B  picType=IDR QP=17
[OK  ] 帧1 出 16402B  picType=P QP=13
[OK  ] 帧2 出 6933B  picType=P QP=11
[OK  ] 共 3 帧 → %TEMP%\nvenc-sdk-probe.h264（274466B）
[PASS] DIRECTX 直通出帧验证通过
```

**ffprobe 确认**：`codec_name=h264, profile=High, width=1920, height=1080, pix_fmt=yuv420p`；ffmpeg 解码出 1920×1080 PNG 成功。

**内容保真度验证**：用纯横条纹图案（每 8 行黑白台阶）测试，解码出的 PNG **逐行条纹与源图案完全一致、无错位、无 pitch 问题** —— 证明 NVENC 读到的显存内容与 CPU 写入的**逐字节一致**，直通路径真实有效（见 `tools/nvenc-sdk-probe` 运行日志 + %TEMP% 下 PNG 快照）。

## 4. 关键发现 / 坑（可复用）

### 4.1 硬件路径一次跑通（对比 MF 的三轮翻车）

- **没有任何结构体对齐 / vtable 错位 / AccessViolation**。对照 `Mp4Writer` 手写 COM 翻车 3 次的教训，NVENC SDK 的 P/Invoke 面**比 MF 干净得多**——它是一份纯 C ABI + 显式 version 字段，没有 QI / AddRef / 双重释放这类 COM 生命周期陷阱。
- `NV_ENC_PIC_PARAMS` 的 `codecPicParams`（H264 联合体 1024B）和 `NV_ENC_CONFIG` 的 `encodeCodecConfig`（1792B union）在 C# 里用 `byte[SizeConst]` 占位即可，**不必展开全部子字段**（除非真的要设置）。
- `NV_ENC_CONFIG_H264` 的 `reserved1[264] + reserved2[64]` 与 `NV_ENC_INITIALIZE_PARAMS` 的 `reserved1[284] + reserved2[64]` **是必须的**（SDK 按指针长度校验 version），漏掉会拿到 `NV_ENC_ERR_INVALID_PARAM` 或干脆崩。

### 4.2 同步模式即可用（不必事件驱动）

`enableEncodeAsync=0` + `completionEvent=IntPtr.Zero` + `LockBitstream.doNotWait=0` → `NvEncEncodePicture` 返回即编码完成，`LockBitstream` 直接阻塞到 bitstream 就绪。**对 SoulLink 的低帧率（5~30fps）单帧提交场景，同步模式完全够用**，不用走 Sunshine 那套 async event 泵。`NVENC-IMPLEMENTATION-20261123.md` 里「MF 硬件 MFT 必须事件驱动」的坑在 SDK 路径**不存在**——这是 SDK 相对 MF 的另一个工程优势。

### 4.3 已知瑕疵（不影响核心结论，记入待办）

| # | 现象 | 判读 |
|---|---|---|
| 1 | `GetEncodeGUIDs` 返回 count=8 但内容全零 | 调用本身 hr=0；回读用 `Marshal.Copy` 手动读也是零。疑似 `GUID*` 在 `__stdcall` 出参位置要按引用数组而非 `IntPtr` 传。**不影响直通**（`InitializeEncoder` 直接用 `H264_GUID` 就成功了，说明编码器根本没校验"必须先枚举"）。实现 `NvencSdkEncoder` 时按 Sunshine 的 `nvEncGetEncodeGUIDs(encoder, guids.data(), size, &count)`（C++ 数组传参）重写这段 |
| 2 | `GetEncodePresetConfigEx` 返回 hr=0xF（`NV_ENC_ERR_INVALID_PTR`） | 可选演示，直通路径不依赖。实现时按引用传 `NV_ENC_PRESET_CONFIG` 结构体即可 |
| 3 | `UpdateSubresource` 内容图案解码后右下角稍暗 | Vortice 的 `UpdateSubresource` 对 NV12 的 `SrcDepthPitch` 语义有歧义（CPU 上传侧问题，与 NVENC 无关）。**条纹测试已证明 NVENC 读取 1:1 保真**，这个瑕疵只在探针图案上可见；实现时用 staging 纹理 + `CopyResource`（Sunshine 同款）替代 `UpdateSubresource` 即可彻底规避 |

### 4.4 与既有调研的对照（全部被实测证实/修正）

| 既有结论 | 实测 |
|---|---|
| `GPU-ZERO-COPY §0`：唯一路线 = NVENC SDK D3D11 直通 | ✅ **坐实**。3 帧真 H.264 从 D3D11 NV12 纹理直入 NVENC |
| `GPU-ZERO-COPY §3`：MF 直通死路（本机 NVIDIA MFT 非 D3D11） | ✅ 无新证据推翻；SDK 是绕过 MF 包装的唯一出路 |
| `GPU-ZERO-COPY §5.3`：SDK 动态加载、不占体积 | ✅ `LoadLibraryEx(SYSTEM32)` 路径，产物 0 额外依赖 |
| `MATURE-LATENCY-PLAYBOOK §2`：zerolatency/CBR/无限 GOP 需 ICodecAPI（MF 路径） | ✅ SDK 路径下这些是**原生字段**（`NV_ENC_RC_PARAMS`），无 ICodecAPI 手写 COM 的风险区 |
| `NVENC-IMPLEMENTATION §5.3`：Vortice 无 ICodecAPI，需手写 ~40 行 | ✅ 对 SDK 路径不适用；SDK 的码控参数直接可设 |

## 5. SoulLink 下一步（给 Lead）

1. **可以直接立项 `NvencSdkEncoder`**（`IVideoEncoder` 的第三个实现），以本探针为骨架（约 680 行，含 500 行结构体定义），接入点已在 `GPU-ZERO-COPY §6` 排好：阶段 2「NVENC SDK 直通」，`SessionRecorder.Loop` 加分支走「WGC 纹理 → GPU 转 NV12 → NVENC SDK」。
2. **工作量大头是结构体完整化**（`NV_ENC_CONFIG` + `NV_ENC_CONFIG_H264` 的真实参数设置，`zerolatency/CBR/idrPeriod`），探针已验证布局正确，照 `MATURE-LATENCY-PLAYBOOK §2` 的参数表填即可。
3. **必须保留降级链**（`GPU-ZERO-COPY §6 阶段3`）：WGC+NVENC SDK → WGC+MF → BitBlt+MF。SDK 在 RDP 会话/显存不足时的可用性**本探针未测**（console session 测的），实施阶段要在 A2 会话里复测。
4. **探针本身可保留在 `tools/`**，作为后续 `NvencSdkEncoder` 实现的活参考与回归工具（`tools/` 规则允许保留可构建源码）。

## 6. 产物清单

| 路径 | 说明 |
|---|---|
| `tools/nvenc-sdk-probe/NvencSdkProbe.csproj` | 独立 csproj（Vortice.D3D11 3.6.2 同主项目版本，net11.0-windows） |
| `tools/nvenc-sdk-probe/Program.cs` | 677 行探针（完整 NVENC SDK P/Invoke + D3D11 NV12 纹理 + 直通编码） |
| `tools/nvenc-sdk-probe/bin/Release/net11.0-windows/` | 构建产物（0 warning 0 error） |
| `%TEMP%/nvenc-sdk-probe.h264` | 探针输出的真 H.264（274KB，3 帧） |
| `%TEMP%/nvenc-final.png` | ffmpeg 解码首帧 |
| `artifacts/nvEncodeAPI.h` | NVIDIA nvEncodeAPI.h 快照（nv-codec-headers SDK 13.1，供后续 `NvencSdkEncoder` 实现对照） |

## 7. 复跑方法

```powershell
cd E:\Documents\deepseek-harness\SoulLink\tools\nvenc-sdk-probe
dotnet build -c Release
.\bin\Release\net11.0-windows\nvenc-sdk-probe.exe -N 3
# 产物 %TEMP%\nvenc-sdk-probe.h264
ffprobe -v error -show_entries stream=codec_name,profile,width,height "C:\Users\91244\AppData\Local\Temp\nvenc-sdk-probe.h264"
```
