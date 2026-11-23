# ICodecAPI 独立探针：微软软件 H.264 MFT 实测

> 日期：2026-11-23  
> 探针：`tools/icodecapi-probe/`（独立项目，不属于 SoulLink 主项目）  
> 范围：仅验证 `ICodecAPI::SetValue/GetValue`；不改主项目，不操作现有运行进程。

## 结论

本机微软软件 H.264 MFT（`H264 Encoder MFT`）上，两个 `SetValue` 均返回 `S_OK`，但对应的 `GetValue` 在设置前、设置后及重新 QueryInterface 后**全部返回 `E_NOTIMPL`**。因此，不能据此宣称低延迟模式或关 B 帧已生效。

| 属性 | SetValue | GetValue（前 / 后 / 新 QI） | 生效判定 |
|---|---|---|---|
| `CODECAPI_AVLowLatencyMode = VT_BOOL TRUE` | `0x00000000` | 均为 `0x80004001` (`E_NOTIMPL`) | 未确认 |
| `CODECAPI_AVEncMPVDefaultBPictureCount = VT_UI4 0` | `0x00000000` | 均为 `0x80004001` (`E_NOTIMPL`) | 未确认 |

**结论边界：** 本探针只证明调用路径可执行及该 MFT 的读回行为，不证明码流一定不含 B 帧，也不测端到端延迟。若业务要求“静默降级”，`S_OK` 不能被解释为支持读回或参数实际生效。

## 接口实现

- IID：`901db4c7-31ce-41a2-85dc-8fa0bf41b8da`
- `IUnknown` 的 `QueryInterface/AddRef/Release` 占 vtable 槽位 0–2。
- `ICodecAPI::SetValue` / `GetValue` 分别是接口第 1 / 2 个方法，即完整 vtable 的**槽位 3 / 4**。
- 低延迟使用 `VT_BOOL=11`，`VARIANT_TRUE=-1`；B 帧数使用 `VT_UI4=19`，值为 0。
- x64 探针显式初始化 16 字节原生 VARIANT：8 字节头部 + 8 字节标量载荷；不使用 `Variant.Value=bool`（SharpGen.Runtime.COM 2.4.2-beta 会抛 `Type [Boolean] is not handled`）。
- `GetValue` 前把 VARIANT 初始化为 `VT_EMPTY`，并打印 HRESULT、VT 和标量值；另经 fresh QI 读回，避免把包装对象缓存当作 MFT 状态。

## 复现

```powershell
dotnet build tools/icodecapi-probe/ICodecApiProbe.csproj -c Release
dotnet tools/icodecapi-probe/bin/Release/net10.0-windows/ICodecApiProbe.dll
```

- 构建：**0 warning / 0 error**。
- 探针进程返回 **1**：不是编译失败，而是两个属性均无法读回确认。
- 枚举 flags 为 `MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_ASYNCMFT = 3`，选择系统显示名为 `H264 Encoder MFT` 的微软软编。
- 环境：Windows x64、.NET 10、Vortice.MediaFoundation 3.8.3；其 SharpGen.Runtime.COM 依赖版本为 2.4.2-beta（不存在 2.4.2 稳定包）。

## 原始输出

```text
MFT: H264 Encoder MFT
AVLowLatencyMode GetValue before: HRESULT=0x80004001
AVLowLatencyMode SetValue(VT_BOOL=True) HRESULT=0x00000000
AVLowLatencyMode GetValue after: HRESULT=0x80004001
AVLowLatencyMode GetValue fresh-QI: HRESULT=0x80004001
AVEncMPVDefaultBPictureCount GetValue before: HRESULT=0x80004001
AVEncMPVDefaultBPictureCount SetValue(VT_UI4=0) HRESULT=0x00000000
AVEncMPVDefaultBPictureCount GetValue after: HRESULT=0x80004001
AVEncMPVDefaultBPictureCount GetValue fresh-QI: HRESULT=0x80004001
FAIL: at least one property did not read back as requested.
```

## 依据与后续

属性名与 GUID 取自本机 Windows `codecapi.h`；设计背景见 `NVENC-IMPLEMENTATION-20261123.md` §6.2 与 `HANDOFF-20261123-LATENCY.md` §6。当前结果仅针对本机微软软编，不外推到 NVIDIA、Intel 或 AMD；后续专项应做码流层 B 帧与端到端延迟实测。
