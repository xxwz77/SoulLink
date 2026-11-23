# NvencSdkEncoder 接入主程序（NVENC SDK D3D11 直通）

> 日期：2026-11-23
> 作者：gpu-zero-copy（dsh-team `team_mwsomf2tj73xd7` / member `m_mwsosfpfjamcdf` / task `task_mwsss5xap27m11`）
> 性质：**实现 + 实测**。新增 `Services/NvencSdkEncoder.cs` + 降级链接入，已改业务代码（`Services/MfFrameEncoder.cs` 接口挂接、`Relay/SessionRecorder.cs` 工厂降级）。未提交 git。
> 依据：`tools/nvenc-sdk-probe/`（上一任务实测骨架，console + A2 双会话直通均出帧）、`NVENC-SDK-PROBE-20261123.md`、`GPU-ZERO-COPY-20261123.md §6`。

---

## 0. 一句话结论

**NvencSdkEncoder 已落地接入主程序，两个 TFM `dotnet build SoulLink.csproj -c Release` 0 error**。以探针为骨架实现 `Services/NvencSdkEncoder.cs`（658 行），惰性加载 `nvEncodeAPI64.dll`（LoadLibraryEx SYSTEM32，不随程序分发、不增体积），同步模式无事件泵；接入降级链 **NvencSdkEncoder(D3D11直通) → MfFrameEncoder(硬编，内部再落软编)**，NVENC 缺失/失败静默降级。**RDP 会话内（A2, SessionId=4）直通复测出帧通过**。

---

## 1. 改动清单

| 文件 | 改动 | 行数/性质 |
|---|---|---|
| `Services/NvencSdkEncoder.cs` | **新增**。完整 NVENC SDK P/Invoke + D3D11 NV12 纹理直通，实现 `IFrameH264Encoder` | 658 行 |
| `Services/MfFrameEncoder.cs` | 类声明挂接 `IFrameH264Encoder`（`public sealed class MfFrameEncoder : IDisposable, IFrameH264Encoder`） | +1 处 |
| `Relay/SessionRecorder.cs` | ① Loop 局部变量 `MfFrameEncoder?` → `IFrameH264Encoder?`；② 编码器创建处改为 `CreateEncoder(ew, eh, fps)`；③ 新增 `IFrameH264Encoder` 消费逻辑 + `TryInitializeCandidate` + `CreateEncoder` 工厂降级 | +约 40 行 |

**新增公共接口 `IFrameH264Encoder`**（定义在 `NvencSdkEncoder.cs` 顶部）：

```csharp
internal interface IFrameH264Encoder : IDisposable
{
    string Name { get; }
    string LastError { get; }
    int ConfiguredBitrateKbps { get; }
    int NegotiatedBitrateKbps { get; }
    bool TryInitialize(int width, int height, int fps, int bitrateKbps);
    byte[]? EncodeNv12(byte[] nv12, long timestampUs);
}
```

> 为什么不复用现有 `IVideoEncoder`：那是 **SinkWriter→MP4 文件** 的录像接口（`MfH264Encoder` 实现），输入 BGRA、输出容器字节；而直播热路径的 `MfFrameEncoder` 是 **NV12 进、Annex-B NAL 出**。两者形状不同，强行复用会污染语义。故新建与 `MfFrameEncoder` 同形的窄接口，仅覆盖直播编码器。

## 2. 惰性加载（用户优先级：响应/启动第一）

严格按 Lead 转达的用户优先级实现：

1. **DLL 只在 TryInitialize 里 LoadLibraryEx**：`NvencSdkEncoder.TryInitialize` 第一行才 `LoadLibraryEx("nvEncodeAPI64.dll", ..., LOAD_LIBRARY_SEARCH_SYSTEM32)`，构造 `new NvencSdkEncoder()` 不加载任何东西。
2. **D3D11 设备 + OpenEncodeSessionEx 也都在 TryInitialize 内**：`SessionRecorder.Loop` 跑在后台 `SoulLinkVideo` 线程，`CreateEncoder` → `TryInitializeCandidate` → `TryInitialize` 全程不碰 UI 线程。
3. **动态加载不随程序分发**：`nvEncodeAPI64.dll` 是系统自带驱动 DLL（`C:\Windows\System32`），不打包，exe 不增体积（"能小就小"的顺手收益，非硬红线）。

## 3. 降级链（静默降级，绝不单点）

`Relay/SessionRecorder.cs` 的 `CreateEncoder`：

```
SOULLINK_ENCODER_MODE:
  0/缺省 = auto：NVENC SDK → MF 硬编（MfFrameEncoder 内部再落软编）
  1       = 强制 NVENC SDK（失败即抛，诊断用）
  2       = 强制 MF（跳过 NVENC SDK）

auto 流程：
  TryInitializeCandidate(new NvencSdkEncoder(), ...)
    ├─ TryInitialize 成功 → 用之
    └─ 失败/异常 → LogQuiet + Dispose → 落 MfFrameEncoder
  TryInitializeCandidate(new MfFrameEncoder(), ...)
    └─ 失败 → throw（进入 error 态）
```

关键点：**每个候选都实际 TryInitialize 一次**（不是"枚举到就算可用"），失败即换下一级。这是 `HANDOFF-20261123-LATENCY §5 要求3`「自动 = 探测式验证：真的出帧才算通过」的落地。

## 4. 实现要点（相对探针的取舍）

| 项 | 探针 | NvencSdkEncoder | 理由 |
|---|---|---|---|
| 结构体 | 完整 | 完整（reserved 数组全保留，union 用 byte[] 占位） | SDK 按指针长度校验，漏 reserved 会 INVALID_PARAM/崩溃 |
| 同步模式 | enableEncodeAsync=0 | 同 | 低帧率单帧提交够用，无事件泵 |
| 码控 | 无（P1 预设默认） | 无（v1 同） | `NV_ENC_CONFIG.rcParams` 精确 CBR/码率/GOP 是下一步 |
| GUID 枚举 | 读回为 0（瑕疵） | 移除（不枚举，直接 H264_GUID） | 不影响直通，探针已验证 InitializeEncoder 直接用 GUID 成功 |
| 输入 | UpdateSubresource | 同 | 探针条纹测试证明 1:1 保真 |

**v1 明确边界**：`encodeConfig=NULL`，码率由 P1 预设决定，`NegotiatedBitrateKbps` 暂回填 `ConfiguredBitrateKbps`。精确 CBR/zerolatency/无限 GOP/按需 IDR 需下一轮填 `NV_ENC_CONFIG.rcParams`（对应 `MATURE-LATENCY-PLAYBOOK §2`）。

## 5. 构建验证

```
dotnet build SoulLink.csproj -c Release
→ 已成功生成。 0 个错误（298 个警告，全部为存量 CS8xxx/WFO0003/NU1510，与本次改动无关）
```

两个 TFM（`net11.0-windows` + `net11.0-windows10.0.26100.0`）均编译通过。`NvencSdkEncoder.cs` 只依赖 `Vortice.Direct3D11`/`Vortice.DXGI`（csproj 已有 3.6.2），无新增 NuGet、无 csproj 改动。

## 6. RDP 会话内复测（A2）

用计划任务（`schtasks /RU A2 /IT`，A2 = session 4 RDP 会话）投送上一任务的探针到 A2 会话，实测日志：

```
进程 SessionId=4            ← A2 RDP 会话
[OK  ] nvEncodeAPI64.dll 载入
[OK  ] D3D11 硬件设备 featureLevel=Level_11_1
[OK  ] NvEncOpenEncodeSessionEx(DIRECTX) 成功
[OK  ] NvEncRegisterResource(DIRECTX,NV12) 成功
[OK  ] 帧0 出 251131B  picType=IDR QP=17
[OK  ] 帧1 出 16402B  picType=P QP=13
[PASS] DIRECTX 直通出帧验证通过
```

产物 `C:\Temp\nvenc-sdk-probe-rdp.txt` ffprobe 确认 `h264/High/1920×1080/yuv420p`（267533B，2 帧）。**NVENC SDK 直通在 RDP 会话内可用，与 console 会话结论一致**。复测用的计划任务已删除（`schtasks /Delete`）。

## 7. 下一步（给 Lead）

1. **精确码控**：填 `NV_ENC_CONFIG.rcParams`（CBR + 码率 + zerolatency + idrPeriod=INFINITE + 按需 IDR），消除 v1 的"预设默认码率"边界，对应 `MATURE-LATENCY-PLAYBOOK §2` 的编码参数项。
2. **完整 GPU 零拷贝**：当前 `EncodeNv12` 仍是 CPU NV12 → UpdateSubresource 上传（`GPU-ZERO-COPY §6 阶段1 的 GPU 色彩转换 + 阶段2 的纹理直通尚未闭合），需 `GpuColorConverter` 把 WGC 的 BGRA 纹理 GPU 转 NV12 后直接喂 `Encode(IntPtr d3dTexture)`。
3. **真机验收**：A2 复测只验证了 SDK 直通本身，**未验证主程序集成后的端到端**（需重启 A2 agent + 设置 `SOULLINK_CAPTURE_MODE=1` + 观察 `[video] 编码器=NVENC-SDK` 日志与 /stream 出帧）。这条留给实施/验证阶段。
4. **降级路径全链路回归**：`SOULLINK_ENCODER_MODE=0/1/2` 三态 + NVENC 缺失（无 N 卡）时的 MF 降级，需在真实 agent 各跑一遍。

## 8. 产物清单

| 路径 | 说明 |
|---|---|
| `Services/NvencSdkEncoder.cs` | 新增，658 行，NVENC SDK 直通编码器 |
| `Services/MfFrameEncoder.cs` | +1 行，挂接 `IFrameH264Encoder` |
| `Relay/SessionRecorder.cs` | 工厂降级链 + 变量类型 + 创建点 |
| 本报告 | `docs/research/NVENC-SDK-INTEGRATION-20261123.md` |
