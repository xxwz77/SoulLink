# RDP 最大化不放大 / 灰边 —— 根因与方案（2026-11-19 实测）

> 目标：最大化时画面跟着放大，**远端分辨率不变**，且**不要灰边**。
> 本文只写本次在本机实测到的结论；未验证的明确标注。

## 1. 结论先行

1. **mstscax 的 `SmartSizing` 只会「缩小」，永远不会「放大」。**
   控制区比远端分辨率大时，画面停在 1920×1080 居中，四周露出的是 **mstscax 自己的灰底（RGB 171,171,171）**，
   不是 WPF 的底色。所以「拉大窗口」这条路在控件内部走不通。
2. **控件的类型库被完整枚举过（118 个 typeinfo）**，除 `SmartSizing` 外没有任何本地缩放/放大属性；
   `MsRdpClient10/11` 的方法表里也没有相关方法。
3. **能真正放大的只有两条路**：
   - **A. `IMsRdpExtendedSettings` 的 `ZoomLevel`**（mstsc 系统菜单里的「缩放」）——
     文档明确它可在连接后修改、且与 `SmartSizing` **互斥**。**但本机在未连接状态下探测返回 `0x8000FFFF E_UNEXPECTED`，需要连着会话才能确认。**
   - **B. DWM 缩略图把 RDP 画面放大后显示** —— 本机已用最小原型**实测成功**（含非等比拉伸），见第 4 节。
4. 远端分辨率这条要求与 A/B 都不冲突：两者都只动本地显示。

## 2. 实测数据（本机 2560×1440，150% DPI）

| 场景 | 控制区（物理像素） | 远端 | RDP 画面实际像素 | 证据 |
|---|---|---|---|---|
| 现在（最大化 2560×1368） | 2370×1136 | 1920×1080 | **1920×1080 居中**，左右各 224px 灰边 (171,171,171) | `docs/rdp-grayband-before.png` |
| 窗口 2400×700（超宽） | 2318×654 | 1920×1080 | **仍是 1920×1080**，左右各 144px 灰边，桌面图标 1:1 | `docs/rdp-1to1-wide.png` |
| 窗口 1100×780（比远端小） | 1370×818 | 1920×1080 | **按 0.71 倍缩小**铺满，图标/文字同步变小 | 本次截图 fit-small |
| 窗口 1200×700 | 1370×818 | 1920×1080 | 缩小铺满 | 同上 |

× = 画面宽度；灰边宽度 = (2370−1920)/2 = 225，实测 224。**灰底颜色 171,171,171 = mstscax 的客户区背景。**

## 3. 为什么不能靠 SmartSizing 放大

- 微软文档只说「scaled to fit the client area」，没有说方向；实测是**只缩小**。
- 本机逐窗口测过：把宿主缩到比远端小 → 画面缩小；放大回 1920×1080 以上 → 画面回到 1:1 居中，不再变大。
- 反复重设 `SmartSizing`（false→true）、强制 `SetWindowPos` 内部容器（`UIContainerClass`/`IHWindowClass`/`OPContainerClass`/`OPWindowClass`）
  **都不会让它放大**；`OPWindowClass` 会被控件自己拉回 1920×1080（=远端分辨率）。
- `IMsRdpClient10::UpdateSessionDisplaySettings(w,h,physicalW,physicalH,orient,desktopScale,deviceScale)` 的 physical 两个参数是
  **毫米（显示器物理尺寸）**，不是"渲染目标像素"（见 MS-RDPEDISP 2.2.2.2.1：`PhysicalWidth/Height … in millimeters`）。
  传 2370×1136 只会把「显示器有多大」谎报给服务器，**不会放大本地画面**。

## 4. 原型验证：DWM 缩略图可以对 RDP 画面做任意缩放（本机实测）

最小原型（独立进程、`WS_EX_TOOLWINDOW|WS_EX_TOPMOST|WS_EX_NOACTIVATE` 的普通窗口 + `DwmRegisterThumbnail`）：

- 把 SoulLink 主窗口作为来源、目标区域 2370×1136、源矩形取 1920×1080 的画面区 →
  **画面被非等比拉伸铺满整个 2370×1136**，灰边消失。证据：`docs/rdp-dwmstretch-proof.png`
  （图标与任务栏可见的横向/纵向拉伸，证明是拉伸而不是裁剪）
- `DwmUpdateThumbnailProperties` 返回 `S_OK`；目标窗口**不是** `WS_EX_LAYERED`，
  因此**不触发** 2.12.0 遇到的「分层窗口压上去 → 整块黑」。
- 缩略图窗口不接收输入（需要自行转发），这是这条路的主要工作量。

⚠ 未验证：长时间稳定性、多画面同时镜像、与保护层（`EnableWindow` 禁用宿主）叠加时的表现、GPU 路径下的刷新率。
⚠ 未验证：把来源换成 **`OPWindowClass`（RDP 画面本身）** 时 `DwmRegisterThumbnail` 返回 `E_INVALIDARG`
（16:38 实测 `hr=0x80070057`），只能退而用**主窗口 + 源矩形**的方式。

## 5. ZoomLevel（route A）—— 需要连着会话才能确认

- 接口：`IMsRdpExtendedSettings`，IID `302d8188-0052-4807-806a-362b628f9ac5`，
  命名属性 `Property(BSTR, VARIANT*)`，**vtable slot 7 = get、slot 8 = put**（本次从类型库反推并验证）。
- 文档：`ZoomLevel` = `VT_UI4`，可**连接后**修改，与 `SmartSizing` **互斥**，RemoteApp/全屏模式无效。
- 本次在**未连接**的控件实例上 `put_Property("ZoomLevel", 150)` → **`0x8000FFFF`**，
  与历史上 `DesktopScaleFactor` 的表现一致（连接前必失败）。
- ⇒ **必须在已连接的控件上验证**。这需要真的连一次 RDP（A2/A3），按项目红线需要用户授权。

## 6. 两条路的取舍（给决策用）

| | A. ZoomLevel | B. DWM 缩略图镜像 |
|---|---|---|
| 远端分辨率 | 不变 | 不变 |
| 放大能力 | 文档支持，**未为本机证实**；档位可能是整数百分比 | **已实测可任意缩放（含非等比）** |
| 灰边 | 若放大成功则消失 | 消失（画面被拉伸铺满） |
| 与 SmartSizing | 互斥，每次尺寸变化都要切换 | 无冲突（可让控件保持 1:1） |
| 输入 | 原生，无额外工作 | **必须自己转发鼠标/键盘/滚轮/拖拽**（工作量大、风险高） |
| 风险 | 低（只调一个属性） | 中高（新窗口 + 输入转发 + 与保护层叠加） |

**建议顺序**：先做 A 的连通性验证（一次真实连接即可判定成败）；A 不成立再走 B。
无论哪条，**只在「控制区大于远端分辨率」时启用**，小于时继续用原生 SmartSizing 缩小（那条路已验证可用）。

## 7. 本次未改动的东西

- `dist/SoulLink.exe`、`dist/stable/`、`E:\SoulLink-SOURCE-ARCHIVE\v2.12.0\` **全部未动**，
  2.12.0 归档 SHA256 仍为 `844D4E7E88F2325172FB38A5117BF5BEC9C416432EE8FE877C5F2938E786AC96`。
- 未构建、未发布、未重启任何 SoulLink 进程。
- 所有原型窗口已销毁；源码未做任何修改（本次只新增了本文与 3 张证据图）。

## 8. 复现要点（给实现的人）

```csharp
// 判定"要不要放大"
int remoteW = (int)_ocx.DesktopWidth;       // 1920
int remoteH = (int)_ocx.DesktopHeight;      // 1080
var rect = ViewportScreenRect();            // 物理像素
bool needMagnify = rect.width > remoteW || rect.height > remoteH;
```

- 需要放大时：关 `SmartSizing`（让控件 1:1 输出 1920×1080），再由外层把这块画面拉伸到控制区大小。
- 目标区域与实际画面矩形的换算必须用**物理像素**（本机 150% DPI，`ActualWidth` 是 DIP，直接用会差 1/3）。
- 鼠标坐标换算继续走 `Services/CoordinateMapper.cs`，把 `ViewWidth/ViewHeight` 换成**放大后的显示区域**即可，
  `KeepAspect=false` 正好对应「非等比拉伸」。
