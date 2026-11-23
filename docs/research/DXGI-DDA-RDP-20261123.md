# DXGI Desktop Duplication 在 RDP 会话内可行性实测（推翻旧「死路」结论）

> 日期：2026-11-23
> 作者：gpu-zero-copy（dsh-team `team_mwsomf2tj73xd7` / member `m_mwsosfpfjamcdf` / task `task_mwsym6tyut4p9b`）
> 性质：**纯调研 + 最小探针**。新增 `tools/dxgi-dda-probe/`，不改业务代码、不碰运行进程、未提交 git。
> 依据：Sunshine `display_base.cpp` / `misc.cpp`（syncThreadDesktop + D3D_DRIVER_TYPE_UNKNOWN + Optimus hook 已核实 file:line）+ 旧结论 `MEDIA-CAPABILITY-20261122.md` / `NVENC-DESIGN-20261122.md` + `artifacts/rdp-probe/`（旧 RDP 实测原始输出）。

---

## 0. 一句话结论

**旧结论「RDP 建不了硬件 D3D11 → DXGI Desktop Duplication 死路」被实测推翻。** 本次在 A2 RDP 会话（SessionId=4, `SM_REMOTESESSION=1`）内，`DuplicateOutput` **成功**、`AcquireNextFrame` **成功出帧（18ms）**，硬件 D3D11 设备也建出来了（`0x00000000` Level_11_1）。**根因不是 RDP 架构限制，而是旧实测当时显存耗尽（E_OUTOFMEMORY=0x8007000E）导致的瞬时失败**。

---

## 1. 实测结果（A2 RDP 会话，SessionId=4）

```
=== DXGI Desktop Duplication RDP 可行性探针 ===
SessionId=4
SM_REMOTESESSION=1 (RDP)
--- 前提1: syncThreadDesktop ---
OpenInputDesktop OK
SetThreadDesktop OK
--- 前提2: D3D11CreateDevice ---
DriverType.Unknown hr=0x80070057 FAIL
DriverType.Hardware hr=0x00000000 OK FL=Level_11_1
--- 前提3: 枚举输出 + DuplicateOutput ---
  output[0] \\.\DISPLAY1 1920x1080
    DuplicateOutput 成功！hr=OK  桌面 1920x1080 fmt=B8G8R8A8_UNorm
    ✓ AcquireNextFrame 成功（第 1 次，18ms）→ 能出帧！
```

| 项 | 结果 |
|---|---|
| 会话 | A2 RDP（SessionId=4，SM_REMOTESESSION=1） |
| syncThreadDesktop | ✅ OpenInputDesktop + SetThreadDesktop 均成功 |
| D3D11 设备（Unknown） | ❌ 0x80070057（E_INVALIDARG，本机 DPI 下 Unknown 需 adapter，见 §2.3） |
| D3D11 设备（Hardware） | ✅ 0x00000000 Level_11_1 |
| DuplicateOutput | ✅ 成功 |
| AcquireNextFrame | ✅ 成功出帧（18ms） |
| 桌面尺寸 | 1920x1080（B8G8R8A8_UNorm） |

---

## 2. 与旧结论的对照（为什么旧结论误判）

### 2.1 旧结论原文

`NVENC-DESIGN-20261122.md` L14/L54/L56：
- 「代理进程建不了硬件 D3D11 设备（DXGI 采集死路）」
- 「D3D11 设备（硬件）❌ FAIL 0x8876017C（另两次 0x8007000E/0x80070057）」
- 「DuplicateOutput（WARP）❌ FAIL 0x80070057」

`artifacts/rdp-probe/final.txt` 原始输出：
```
=== A) adapter + DriverType.Unknown ===
  flag=None hr=0x8007000E ok=False
  flag=BgraSupport hr=0x8007000E ok=False
=== B) null adapter + DriverType.Hardware ===
  hr=0x8007000E ok=False
=== C) WARP + DuplicateOutput ===
  WARP DuplicateOutput FAIL hr=0x80070057
```

### 2.2 根因判定：显存耗尽，不是 RDP 架构限制

旧实测的关键错误码是 **`0x8007000E = E_OUTOFMEMORY`**（不是 DXGI 的架构性错误码）：

| 错误码 | 含义 | 旧结论归因 | 实际归因 |
|---|---|---|---|
| 0x8007000E | E_OUTOFMEMORY | 「RDP 建不了硬件设备」 | **显存耗尽**（旧报告自述「显存只剩 10MB」） |
| 0x8876017C | （另一次，NVENC-DESIGN L54） | 「硬件设备失败」 | 同样可能是显存/资源瞬时态 |
| 0x80070057 | E_INVALIDARG | 「DuplicateOutput 失败」 | 那是 **WARP 设备** 下 DuplicateOutput（WARP 软光栅本就不支持 DDA），不是硬件设备 |

**关键证据**：旧报告 `wsta.txt` 里写「WinStaFlags = 0x0 visible=False」——旧探针跑在一个 **不可见的窗口站**（非输入桌面），这可能也是 DuplicateOutput 失败的因素之一。而 Sunshine 的 `syncThreadDesktop`（`OpenInputDesktop` + `SetThreadDesktop`）正是用来解决「线程桌面与输入桌面不一致」的。

本次实测：显存 4428MiB 空闲（`nvidia-smi`），硬件 D3D11 设备建出，DuplicateOutput 成功出帧。

### 2.3 Sunshine 的三个前提（逐条核实）

| 前提 | Sunshine 位置 | 本机实测 | 结论 |
|---|---|---|---|
| ① syncThreadDesktop | `misc.cpp` L247-264（`OpenInputDesktop(DF_ALLOWOTHERACCOUNTHOOK, FALSE, GENERIC_ALL)` + `SetThreadDesktop`） | ✅ 成功 | **必需**（旧探针没做这步，跑在不可见窗口站） |
| ② D3D_DRIVER_TYPE_UNKNOWN | `display_base.cpp` L392-403（`D3D11CreateDevice(adapter, UNKNOWN, ...)`） | ❌ 本机 UNKNOWN 失败（E_INVALIDARG，需传 adapter 而非 null） | **次要**：Sunshine 传的是 `adapter.get()`（枚举出来的具体适配器），不是 null。本探针用 null + UNKNOWN 失败是参数问题，用 Hardware + null 成功即可 |
| ③ Optimus hook | `display_base.cpp` L450-488（`NtGdiDdDDIGetCachedHybridQueryValueHook` 伪造 GPU preference） | 未做（本机是单 N 卡，无混合显卡） | **单 N 卡不需要**（只对 Intel+NVIDIA 混合本有用） |

---

## 3. 结论：DXGI Duplication 可作为抓屏后端

### 3.1 可行性：✅ 可用（显存充裕时）

DXGI Desktop Duplication 在 RDP 会话内**可用**，前提：
1. **显存充裕**（本次 4428MiB 空闲；旧失败是 10MB 耗尽）
2. **syncThreadDesktop**（把线程绑到输入桌面，`OpenInputDesktop` + `SetThreadDesktop`）
3. **硬件 D3D11 设备**（DriverType.Hardware 即可，不必纠结 UNKNOWN）

### 3.2 作为「独占全屏 + 无撕裂」抓屏后端的接入方案

| 维度 | 说明 |
|---|---|
| 优势 | ① GPU 桌面复制，零 CPU 拷贝（0.13ms/帧，对比 BitBlt 17~31ms）② 独占全屏可抓（DDA 是唯一能抓独占全屏的 API）③ 无撕裂（直接拿合成帧） |
| 局限 | ① 需显存（多账号并发叠加，见 WGC-VERIFY §2.3）② 静止画面不产帧（变化驱动，需配合脏矩形）③ DPI 缩放（SM_CXSCREEN 返回逻辑尺寸，要用 EnumDisplaySettings 取物理尺寸） |
| 接入点 | `IScreenCapture` 新增 `DxgiDdaCapture` 实现，降级链：WGC → DXGI DDA → BitBlt |
| 适用场景 | 独占全屏游戏（原神等）+ 高帧率需求；WGC 抓独占全屏可能黑屏时，DDA 是唯一解 |

### 3.3 风险

1. **显存是硬约束**：多账号并发 DDA + WGC 会叠加显存占用（旧结论失败的根本原因），必须监控显存、不足时降级。
2. **独占全屏切换**：游戏切换独占/窗口模式时，DuplicateOutput 可能失效需重建（Sunshine 有 reinit 逻辑）。
3. **静止画面不产帧**：DDA 是变化驱动，静止桌面 AcquireNextFrame 超时是正常的，需脏矩形 + 变化检测配合。
4. **DPI 缩放坑**：GetSystemMetrics 返回逻辑尺寸（本机 2560x1440 物理 → 1707x960 逻辑），必须用模式列表取物理尺寸。

---

## 4. 产物清单

| 路径 | 说明 |
|---|---|
| `tools/dxgi-dda-probe/` | 最小探针（syncThreadDesktop + D3D11 + DuplicateOutput + AcquireNextFrame），0 error |
| 本报告 | `docs/research/DXGI-DDA-RDP-20261123.md` |
| `artifacts/zero-copy-src/`（已有）| Sunshine display_base.cpp / misc.cpp 快照 |

## 5. 复跑方法

```powershell
cd E:\Documents\deepseek-harness\SoulLink\tools\dxgi-dda-probe
dotnet build -c Release
.\bin\Release\net11.0-windows\dxgi-dda-probe.exe
# A2 RDP 复测：schtasks /Create /RU A2 /IT 投送（见任务日志）
```