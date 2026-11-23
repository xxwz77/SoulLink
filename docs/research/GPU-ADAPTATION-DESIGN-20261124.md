# GPU 自适应路由设计（P0-G/P0-H，2026-11-24）

> 背景：用户 BIOS 启用核显后，系统默认适配器从 NVIDIA 变为 Intel UHD，
> `D3D11CreateDevice(IntPtr.Zero, Hardware)` 拿到核显设备 → NVENC 直通
> `NvEncOpenEncodeSessionEx(DIRECTX)` 返回 `NV_ENC_ERR_DEVICE_NOT_SUPPORTED`，
> 且编码器被按需 IDR 反复重建，每秒重试 NVENC 2-3 次（日志风暴）。
> 用户产品化要求：**不能要求用户关闭核显**；任意 GPU 组合（N/A/i 卡、纯核显、多卡）都要自动选对路径。

## 1. 拓扑枚举（`Relay\GpuAdapter.cs`）

- 进程内枚举一次全部 DXGI adapter，记录 `VendorId`（0x10DE=NVIDIA / 0x8086=Intel / 0x1002=AMD）、
  名称、`EnumOutputs(0)` 是否成功（=「有显示输出」）、是否软件适配器。
- 日志（只打一次）：`[gpu] 枚举到 N 块适配器: adapter0=... 有输出, ...`

## 2. 抓屏源选择（`DxgiDdaCapture.Start`）

候选顺序（`PickCaptureCandidates`）：
1. **有输出的 NVIDIA**（与 NVENC 同 GPU → 零拷贝可用）；
2. 其余**有输出的硬件适配器**（Intel/AMD/虚拟显示，按枚举序）；
3. 逐个尝试 `DuplicateOutput`，成功即记录 `CaptureAdapterVendorId/Name`（暴露给编码路由与零拷贝门控）。

## 3. 编码器路由（`SessionRecorder.CreateEncoder`）

| 拓扑 | 抓屏源 | 编码器 | 零拷贝 |
|---|---|---|---|
| 单 N 卡 / N 卡带屏 | NVIDIA | NVENC-SDK（NVIDIA 设备） | ✅ 同 GPU |
| N+i 双卡（当前机器） | NVIDIA 有输出→NVIDIA；否则有输出的那块 | NVENC-SDK | 仅同 GPU 时 ✅ |
| 单 A 卡 / 单 i 卡 / 纯核显 | 该卡 | MF（vendor 中立 MFT 枚举：AMF/QSV/软编自动落） | ❌（无 NVIDIA） |
| A+i 双卡 | 有输出的那块 | MF | ❌ |
| 跨 GPU（Intel 抓屏 + NVENC 编码） | Intel | NVENC-SDK（允许，EncodeNv12 上传走 CPU） | ❌ 禁用（跨 GPU 共享物理必败，不浪费帧试） |

- **NVENC 只在机器存在 NVIDIA 时尝试**（`GpuAdapter.HasNvidiaAdapter()`），
  失败一次置 `_nvencUnavailable`，本进程内不再重试（修 P0-G 重建风暴），显式热切抓屏后端才重置。
- 路由日志（每编码器建立一次）：
  `[gpu] 抓屏源=...; 编码器=...; 同GPU=true/false; 零拷贝=可用/禁用`

## 4. 零拷贝物理约束（`GpuShaderConverter` + SessionRecorder 门控）

- PS 两 pass BGRA→NV12（渲染进编码器注册的 NV12 纹理，R8/R8G8 双平面 RTV），
  绕开 RDP 内不可用的 VideoProcessor（P0-F 已实测 PSNR 52.6dB）。
- **只在「抓屏与编码同 GPU」时创建转换器**；跨 GPU 时 gpuConverter=null，
  CPU 路径 BgraToNv12 + EncodeNv12 上传兜底（不丢帧、不重试）。

## 5. 真机验证（2026-11-24，N+i 双卡机器，2560x1440，60fps）

```
[gpu] 枚举到 5 块适配器: adapter0=NVIDIA(0x10DE) 有输出, adapter1=Intel(0x8086) 无输出, ...
[capture] DXGI-DDA 抓屏源=NVIDIA GeForce RTX 4060 Laptop GPU(0x10DE) 与 NVENC 同 GPU
[video] 编码器=NVENC-SDK 配置码率=8000kbps ... fps=60
[gpu] 抓屏源=NVIDIA ...; 编码器=NVENC-SDK; 同GPU=true; 零拷贝=可用
[video] 零拷贝管线 total=... zeroMs=4~7 convMs=0（<1ms）→ 60fps 稳定
```

## 6. 已知边界

- RDP 会话内「有输出」的可能是虚拟显示适配器（GameViewer/Oray 等）；抓屏源按
  「NVIDIA 有输出优先，否则第一个有输出者」的规则落点，符合预期。
- 会话 4（RDP）下若 NVIDIA 无输出、虚拟适配器当选抓屏源 → 自动走 MF + 无零拷贝，
  这是物理正确的降级（跨 GPU 共享必败），非 bug。
