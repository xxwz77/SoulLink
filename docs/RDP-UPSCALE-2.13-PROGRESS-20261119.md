# RDP 放大 / 灰边 —— 本次修改方向、发现与问题总结

> 2026-11-19 · 版本 2.13.0 · 状态：**可用但不合格**（画面正确，拖动错位 + 卡顿）

---

## 一、原始需求

1. 最大化后画面要跟着放大，**不能有灰边**
2. **不能改变远端实际分辨率**
3. 不能破坏 2.12.0 存档、不能动 `dist/stable/`

---

## 二、根因（全部本机实测，不是推断）

### 2.1 SmartSizing 只会缩小，永远不会放大 ★核心结论

| 控制区（物理像素） | 远端 | RDP 画面实际像素 | 结果 |
|---|---|---|---|
| 2370×1136（最大化） | 1920×1080 | **1920×1080 居中** | 左右各 224px 灰边 |
| 2318×654（超宽） | 1920×1080 | **仍是 1920×1080** | 图标 1:1，左右各 144px 灰边 |
| 1370×818（比远端小） | 1920×1080 | **缩小到 0.71 倍铺满** | 图标/文字同步变小 |

→ 微软文档只说 "scaled to fit the client area"，没说方向；**实测是单向的（只缩不放）**。

### 2.2 灰边的颜色是控件自己的灰底

逐像素采样：**RGB(171,171,171) = #ABABAB**。
不是 WPF 的 `#B2B2B3`（那是 DWM 非活动边框色），也不是我们的布局背景。

### 2.3 控件里没有别的本地缩放开关

类型库 118 个 typeinfo 全部枚举过，除 `SmartSizing` 外唯一的候选是
`IMsRdpExtendedSettings::Property("ZoomLevel")`（mstsc 系统菜单里的"缩放"）。

### 2.4 `UpdateSessionDisplaySettings` 的 physical 参数是"毫米"

MS-RDPEDISP 2.2.2.2.1：`PhysicalWidth/PhysicalHeight … in millimeters`（10–10000 mm）。
传 2370×1136 只是向服务器**谎报显示器物理尺寸**，不会放大本地画面。
→ 想靠它放大是走不通的，而且会动远端，违反需求 2。

### 2.5 其它路都被实测否决

| 方案 | 结论 |
|---|---|
| `PrintWindow` 抓 RDP 表面 | 返回**纯白图**，0.2ms"假成功"，抓不到合成表面 |
| `WS_EX_LAYERED` 浮层压上面 | **整块黑屏**（2.12.0 的事故形态），禁止 |
| 反复 `SmartSizing=false→true` | 画面不动，纯刷日志 |
| 强制 `SetWindowPos` 内部容器 | 控件自己把 `OPWindowClass` 拉回 1920×1080 |

---

## 三、本次实际做的两个通道

### 通道 A：`ZoomLevel`（理论上最干净，**实测失败**）

- 新增 `Services/RdpLocalZoom.cs`：QI `IMsRdpExtendedSettings`（IID `302D8188-…`），
  按 vtable slot 7/8 调 `get/put_Property`
- 新增 `Controls/RdpConsole.Zoom.cs`：算所需比例 → 吸附档位 → 先关 SmartSizing → 隔拍再设 ZoomLevel

**结果：本机恒返回 `0x8000FFFF E_UNEXPECTED`**
- v9 / v10 / v11 三个控件版本都试过 → 一样
- 连接前、连接后都试过 → 一样
- 用已知属性（`DesktopScaleFactor` 等）交叉验证 → 同样 `E_UNEXPECTED`
- 与 `HANDOFF 12.4` 记录的历史结论一致：**接口是通的，是这个控件不接受这类属性**

已加保护：**连续失败 3 次就放弃**，不刷日志、不反复重排控件。

### 通道 B：DWM 缩略图镜像（**画面成功**）

- 新增 `Controls/RdpMirrorOverlay.cs`
- 原理：`DwmRegisterThumbnail` 把 SoulLink 主窗口挂成缩略图来源，
  用 `rcSource` 限定到 RDP 画面那 1920×1080，`rcDestination` 是整块控制区 →
  **DWM 直接把它非等比拉伸铺满**
- 目标窗口是普通 `WS_POPUP | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`，**不是分层窗口** → 不黑屏
- 输入：`WM_NCHITTEST` 返回 `HTTRANSPARENT` 做穿透（锁定态），接管后改成转发鼠标进 RDP

**效果实测**：灰边**完全消失**，1920×1080 被拉伸成 2376×1142，远端分辨率一个像素没动。

---

## 四、改动的文件清单

### 新增（5 个）

| 文件 | 作用 |
|---|---|
| `Services/RdpLocalZoom.cs` | ZoomLevel 的 vtable 直调封装 |
| `Services/LocalZoomGeometry.cs` | 纯数学：算画面尺寸/偏移/是否需要放大 |
| `Controls/RdpConsole.Zoom.cs` | partial 类：ApplyLocalZoom / SyncMirrorOverlay / ScheduleMirrorRefresh / TearMirror |
| `Controls/RdpMirrorOverlay.cs` | DWM 缩略图窗口 + 鼠标转发 + 穿透 |
| `tools/rdp-mirror-probe/mirror-probe.ps1` | 独立验证脚本（不参与编译） |

### 修改（2 个）

| 文件 | 改动 |
|---|---|
| `Controls/RdpConsole.cs` | ① 类声明 `sealed class` → `sealed partial class`（3 处接入点） |
| `SoulLink.csproj` | `<Version>2.12.0</Version>` → `2.13.0` |

`RdpConsole.cs` 里的 4 个接入点：
1. `ApplyStretchRefit()` 末尾：`ApplyLocalZoom()` + `SyncMirrorOverlay()`
2. `SetNativeVisible(true/false)`：移回时 `ScheduleMirrorRefresh()`，藏起来时 `TearMirror()`
3. `OnOwnerActivated()`：`ScheduleMirrorRefresh()`
4. `LockShield()/UnlockShield()`：切 `_mirror.ClickThrough`

---

## 五、现在的两个问题（你反馈的）

### 问题 1：拖动窗口，RDP 画面停在原地 ★确认根因

**根因**：`ScheduleMirrorRefresh()` 的触发点只有三处 ——
`ApplyStretchRefit`（尺寸变化）、`OnOwnerActivated`、`SetNativeVisible`。
**唯独没有监听主窗口 `LocationChanged`。**

而 `ConsolePage.xaml.cs:86` 里窗口拖动只挂了 `UpdateShield()`：
```csharp
window.LocationChanged += (_, __) => UpdateShield();   // 只更新保护层，没管镜像
```
→ 镜像窗口是**绝对屏幕坐标**的顶层窗口，主窗口移动它不会跟，于是"画面留在原地"。

**修法**（1 处，低风险）：给 `RdpConsole` 挂 `_ownerWindow.LocationChanged` →
`ScheduleMirrorRefresh()`；或在 `ConsolePage` 的 `LocationChanged` 里补一次镜像刷新。

### 问题 2：缩放变得很卡 ★三个叠加原因

| # | 原因 | 代价 |
|---|---|---|
| ① | `FindRenderSurface()` **每次**都做整个 ActiveX 子树的 `EnumChildWindows` 全扫描（数百个窗口） | 每次刷新几十毫秒 |
| ② | `DwmUpdateThumbnailProperties` **无条件调用**，即使参数完全没变 | 每次都触发一次 DWM 重新合成 |
| ③ | 两个定时器叠加：`_stretchRefit` 200ms + `_mirrorTimer` 120ms，互相触发 | 拖动/缩放时每 120ms 重合成整窗 |

**修法**（都不用动保护层）：
- `FindRenderSurface` 结果缓存，只在 `SizeChanged`/句柄变化时重查
- `Update()` 里做参数 diff，**值没变就直接 return**
- 拖动期间改用 `SizeChanged` 的 `e.NewSize` 直接算，**不走枚举、不等定时器**

### 问题 3：早前日志里"点了 0.25 秒又被锁回去"（**你说能操作，所以这条待确认**）

```
17:12:05.947  保护层解锁 → 恢复 RDP 原生窗口
17:12:06.200  鼠标离开画布或窗口失活 → 重新上锁
```
当时是我用脚本 `SetCursorPos` 点，窗口没真正激活，触发了
`ShouldRelockByPointer`（`ConsolePage.xaml.cs:615`）。
你实测能操作 ⇒ 真人点击时没这个问题，但**这条路径在镜像层存在时没有重新验证过**，
一旦复现就是"点了没反应"，需要盯。

---

## 六、当前机器状态

| 项目 | 状态 |
|---|---|
| `dist/SoulLink.exe` | **已替换为 2.13.0**（67200263 B，SHA256 `9FAFFBDF…`） |
| 2.12.0 原件 | 完整保存在 `E:\SoulLink-SOURCE-ARCHIVE\20261119-1657-before-2.13-localzoom\SoulLink-2.12.0.exe`，SHA256 与存档一致 |
| 源码备份 | `…\20261119-1657-before-2.13-localzoom\source\`（415 文件） |
| `dist/stable/` | **一个字节没动** |
| `E:\SoulLink-SOURCE-ARCHIVE\v2.12.0\` | **未动**，SHA256 `844D4E7E…` |
| 运行中 | PID 148684（2.13.0，带 `--verbose-log`） |

---

## 七、建议的下一步（按性价比排序）

1. **修拖动错位** —— 1 处钩子，几乎零风险，先做
2. **修卡顿** —— 缓存枚举结果 + 参数 diff + 合并两个定时器，不碰保护层
3. 修完后**重新验**：单画面 / 双画面 / 最小化恢复 / 切标签 / 全屏 / 长时间挂机
4. 全部通过后，才把 `NEW_SESSION_PROMPT.md` 和 `docs/FIXED-*.md` 的"已知未做"改掉
5. 若第 2 步做完仍然卡 → 说明 DWM 缩略图这条路本身的代价不可接受，
   应当**回滚到 2.12.0**，把"放大"作为独立特性重新设计（不要混在保护层改动里）

---

## 八、一句话总结

> 根因查实了（SmartSizing 只缩不放），放大也做出了效果（DWM 缩略图非等比拉伸），
> **但工程上没做完**：拖动不同步 + 缩放卡顿，两个都是性能/事件接线问题，
> 不涉及架构，可以单独修；`ZoomLevel` 那条最干净的路在本机被控件明确拒绝，已放弃。
