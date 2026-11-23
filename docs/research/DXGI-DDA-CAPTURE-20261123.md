# DXGI Desktop Duplication 抓屏后端接入降级链（根治游戏撕裂）

> 日期：2026-11-23
> 作者：gpu-zero-copy（dsh-team `team_mwsomf2tj73xd7` / member `m_mwsosfpfjamcdf` / task `task_mwsze01evb4gax`）
> 性质：**实现**。新增 `Relay/DxgiDdaCapture.cs` + 接入 SessionRecorder 降级链，未提交 git。
> 依据：`tools/dxgi-dda-probe/`（上一任务实测骨架，A2 RDP 出帧 18ms）+ `DXGI-DDA-RDP-20261123.md` + Sunshine `display_base.cpp`/`misc.cpp`。

---

## 0. 一句话结论

**DXGI Desktop Duplication 抓屏后端已落地并接入降级链，两 TFM 0 error。** 新增 `Relay/DxgiDdaCapture.cs`（339 行，实现 `IScreenCapture`），抓屏降级链改为 **DXGI DDA → WGC → BitBlt**（DDA 优先，因独占全屏 + 无撕裂 + 0.13ms/帧）。处理了脏矩形（静止复用上一帧）、独占全屏切换重建（ACCESS_LOST）、DPI 物理尺寸（模式列表）、显存失败降级四个边界。

---

## 1. 改动清单

| 文件 | 改动 |
|---|---|
| `Relay/DxgiDdaCapture.cs` | **新增**。DDA 抓屏后端（syncThreadDesktop + Hardware D3D11 + DuplicateOutput + AcquireNextFrame + staging 读回 BGRA），339 行 |
| `Relay/SessionRecorder.cs` | `CreateCapture()` 改 DDA 优先；`CaptureBgraWithProbe()` 加 DDA 探测降级（DDA → WGC → BitBlt） |

---

## 2. DxgiDdaCapture 实现要点

### 2.1 三个前提（对齐 Sunshine）

| 前提 | 实现 | 位置 |
|---|---|---|
| ① syncThreadDesktop | `OpenInputDesktop(DF_ALLOWOTHERACCOUNTHOOK, FALSE, GENERIC_ALL)` + `SetThreadDesktop` | `SyncThreadDesktop()` |
| ② Hardware D3D11 | `D3D11CreateDevice(DriverType.Hardware, BgraSupport, Level_11_1~10_0)` | `Start()` |
| ③ Optimus hook | **跳过**（单 N 卡不需要，仅 Intel+NVIDIA 混合本才需要） | — |

### 2.2 四个边界处理

| 边界 | 处理 |
|---|---|
| 脏矩形（静止不产帧） | `AcquireNextFrame(16ms)` 返回 `DXGI_ERROR_WAIT_TIMEOUT` 时**复用 `_lastBgra`**（上一帧），不返回 null |
| 独占全屏切换重建 | `AcquireNextFrame` 返回/抛 `DXGI_ERROR_ACCESS_LOST` 时 `Rebuild()`（Dispose duplication → 重新 Start） |
| DPI 缩放物理尺寸 | 用 `Description.ModeDescription.Width/Height`（模式列表），不回退到 `GetSystemMetrics` 逻辑尺寸；仅当模式列表为 0 时回退 `DesktopCoordinates` |
| 显存失败降级 | `DuplicateOutput` 失败/设备创建失败 → 抛 `InvalidOperationException`，由 `CaptureBgraWithProbe` 接住降级 WGC |

### 2.3 CaptureBgra 语义

- `CaptureBgra` 返回 **top-down BGRA**（与 WgcCapture/BitBltCapture 一致），紧凑 stride = width*4。
- staging 读回逻辑与 `WgcCapture.ReadBackTexture` 同款（staging + CopyResource + Map(READ) + 逐行拷贝处理 rowPitch）。
- `CaptureJpeg` 复用 BGRA → GDI+ JPEG（与 BitBlt 同款），仅供截图协议用，不进入实时链路。

---

## 3. 降级链接入

`SessionRecorder.CreateCapture()`：

| SOULLINK_CAPTURE_MODE | 后端 |
|---|---|
| 0（默认/auto） | **DDA 优先**（探测失败 → WGC → BitBlt） |
| 1 | 显式 WGC（跳过 DDA） |
| 3 | 显式 BitBlt |

`CaptureBgraWithProbe()` 探测链：

```
Capture is DxgiDdaCapture?
  → CaptureBgra（DDA 构造即尝试 DuplicateOutput，失败抛异常）
    → 出帧 + IsActive → 采用 DDA
    → 300ms 无首帧/异常 → Dispose DDA → new WgcCapture → 递归探测
Capture is WgcCapture?
  → 300ms 无首帧 → Dispose → new BitBltCapture → 降级
else → Capture.CaptureBgra（BitBlt 直接抓）
```

> ★ 降级链顺序满足验收：DDA 优先（独占全屏 + 无撕裂 + 0.13ms），失败静默降级，绝不单点。

---

## 4. 验收对照

| 验收项 | 结果 |
|---|---|
| 两 TFM 0 error | ✅ |
| ① DxgiDdaCapture 实现 IScreenCapture | ✅（syncThreadDesktop + Hardware D3D11 + DuplicateOutput + AcquireNextFrame） |
| ② 降级链 DDA → WGC → BitBlt | ✅（CreateCapture + CaptureBgraWithProbe） |
| ③ 脏矩形/独占全屏重建/DPI/显存降级 | ✅（§2.2 四条） |
| ④ 日志记录后端 + 降级原因 | ✅（`[capture] 采用 DXGI-DDA` / `DXGI-DDA 降级：` + 原因） |
| ⑤ 不引框架不碰运行进程 | ✅（只用已有 Vortice 包） |

---

## 5. 已知边界与后续

1. **真机端到端未验**：DxgiDdaCapture 只在探针层验证（A2 RDP 出帧），主程序集成后需重启 A2 agent + 观察 `[capture] 采用 DXGI-DDA` 日志 + /stream 出帧。
2. **多账号显存叠加**：DDA 每个代理进程建一个 duplication + D3D11 设备，显存是共享的（`WGC-VERIFY §2.3`），多账号并发需监控。
3. **静止画面复用上一帧**：DDA 静止不产帧，`CaptureBgra` 复用 `_lastBgra`。这配合 `SessionRecorder.Loop` 里的 `previousBgra` 变化检测，静止时不会重复编码（已有去重逻辑）。
4. **cursor 不抓**：DDA 的 `GetFramePointerShape` 可抓光标形状，但 `CaptureBgra` 目前只读桌面纹理不含光标（与 WGC 的 `IsCursorCaptureEnabled=false` 一致，光标由前端绘制或后续叠加）。

---

## 6. 产物清单

| 路径 | 说明 |
|---|---|
| `Relay/DxgiDdaCapture.cs` | DDA 抓屏后端（339 行） |
| `Relay/SessionRecorder.cs` | 降级链接入 |
| 本报告 | `docs/research/DXGI-DDA-CAPTURE-20261123.md` |