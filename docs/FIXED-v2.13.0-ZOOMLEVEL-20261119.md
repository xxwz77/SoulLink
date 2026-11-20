# 交接文档 · SoulLink 2.13.0 · RDP 本地放大探索（失败总结）

> 固定日期：2026-11-19
> 版本号：2.13.0.0
> 状态：**本次放大改造失败，应用不可用**

---

## 0. 必读：这次改失败了

**用户原话：「已经被你彻底改废了，无法解锁，无法验证。」**

本次会话围绕「RDP 最大化放大」做了多轮改造，引入了比修复更多的问题：

| 引入的问题 | 严重度 |
|---|---|
| **无法解锁**（点画面后锁回，键鼠进不去 RDP） | 致命 |
| **窗口缩放异常**（窗口被压缩） | 严重 |
| **铺满模式解锁时闪烁 + 白边** | 严重 |
| **多画面同样闪烁 + 白边** | 严重 |
| 控件反复重建（1 分钟 4 个 HWND） | 高危（0x119 风险） |

**接手的人第一件事：让应用恢复可用，不要继续改进放大功能。**

---

## 1. 当前机器状态

| 项目 | 值 |
|---|---|
| dist 当前包 | `86811575B47C9698985F814D03BE8050F24C1F5C9D0E6E6501E6BC0356D79FBD`（19:48，**不可用**） |
| 版本号 | 2.13.0.0 |
| **推荐回退目标** | `artifacts/pub-fitmode/SoulLink.exe`（`C1559970…`，17:55） |
| 2.12.0 存档（**未动**） | `E:\SoulLink-SOURCE-ARCHIVE\v2.12.0\`，`844D4E7E…` |
| 2.13 前备份 | `E:\SoulLink-SOURCE-ARCHIVE\20261119-1657-before-2.13-localzoom\` |
| 本会话前备份 | `E:\SoulLink-SOURCE-ARCHIVE\20261119-1620-before-grayband\` |
| 挂机版 `dist/stable/` | **7 个文件零字节变动** |

### 1.1 各历史包及其状态（artifacts/ 下）

```
pub-grayband-builder 844D4E7E… 16:23  = 2.12.0 基线（无任何本会话改动）
pub-slotfix          78903F62… 17:29  = slot 修正第一版
pub-slotfix2         4B35E147… 17:31  = slot 修正 + 连接触发
pub-measure          CE19FCB9… 17:36  = + 测量埋点
pub-fitmode          C1559970… 17:55  = + 顶栏开关 + 最小化门控前（★ 推荐回退）
pub-final            6F9C526C… 18:43  = + 1:1 居中（引入灰边/自激）
pub-relock           4953642B… 19:10  = + 双模式重锁
pub-relock2          09FF4E92… 19:30  = + 500ms 防抖 + 最小化门控
pub-rearm            7BF4C75A… 19:33  = + 清除两条绕过路径
pub-no1to1           86811575… 19:48  = 移除 1:1（当前，仍不可用）
```

---

## 2. ★ 唯一真正的技术成果：ZoomLevel vtable 修正

### 2.1 历史错误

`Services/NativeDispatch.cs` 与 `Services/RdpLocalZoom.cs` 都按错误布局调用：

```
0-2 IUnknown, 3-6 IDispatch, 7 get_Property, 8 put_Property   <- 错误
```

**实测类型库（mstscax.dll，STA 线程）：**

```
=== IMsRdpExtendedSettings ===
cbSizeVft=40  slotCount=5  cFuncs=2
  slot=3  PUT  Property  cParams=2
  slot=4  GET  Property  cParams=2
```

**整个接口只有 5 个槽**，且**不是 IDispatch 派生**（STA 下 QI IDispatch 返回 E_NOINTERFACE）。

槽 7/8 是**越界读取相邻接口的 vtable**。这才是历史上「DesktopScaleFactor 不支持」
（恒 `0x8000FFFF`）的**真实原因** —— 一直调错了函数。

### 2.2 修正后的实测结果

| 属性 | PUT | GET | 结论 |
|---|---|---|---|
| `DesktopScaleFactor` | `S_OK` | `OK vt=19 val=150` | 完全可用 |
| `DeviceScaleFactor` | `S_OK` | `OK vt=19 val=180` | 完全可用 |
| `ZoomLevel` | `E_FAIL`（未连接时） | `OK vt=19 val=100` | 属性存在，连接后可写 |
| `EnableZoom` | `0x80004005` | `E_UNEXPECTED` | 不是合法属性名 |

### 2.3 ★ 关键坑：STA 线程

```
MTA 线程：QI(IMsRdpExtendedSettings) -> 0x80004002 E_NOINTERFACE
STA 线程：QI(IMsRdpExtendedSettings) -> S_OK
```

WPF UI 线程是 STA，正常路径没问题；后台线程/线程池调用会静默失败。

**这一条是本会话唯一确定可用、且应保留的成果。**

---

## 3. 为什么改失败：三个致命错误

### 3.1 错误一：给 WindowsFormsHost 设固定尺寸

为了实现「1:1 精确居中」，给 `_host` 设了：

```csharp
_host.HorizontalAlignment = HorizontalAlignment.Center;
_host.Width = 1280;    // = 1920 / 1.5
_host.Height = 720;    // = 1080 / 1.5
```

**后果链：**

1. `_host` 是 `RdpConsole(Grid)` 唯一子项
2. 固定尺寸 + Center 后，父 Grid 的 Star 行把它当**最小约束**
3. 压缩父 Border 测量 → 经 Canvas/ScrollViewer 传导到 MainWindow
4. **窗口被压缩**（用户截图约 680x510）
5. `_host.Handle` 因 WPF 布局变化被**内部销毁重建** → 1 分钟 4 个 HWND

**第 5 条是 0x119 事故的高风险组合。项目文档早有警告：
「同一进程里的 ActiveX 绝不能在容器之间反复重挂」。**

### 3.2 错误二：在「表面现象」上反复打补丁

发现问题后的处理顺序：

| 轮次 | 补丁 | 结果 |
|---|---|---|
| 1 | 加 `SetWindowPos` 死区 | 引入亚像素抖动（1580↔1581） |
| 2 | 加 ±2px 吸附 + 幂等 | 抖动缓解，卡顿仍在 |
| 3 | 加 500ms 防抖 + 最小化门控 | **窗口缩小暴露出来** |

**每一轮都在治标。真正的病因（固定尺寸）始终没被移除，直到第 4 轮才被迫放弃。**

### 3.3 错误三：修 bug 时只看一条路径

「解锁后被锁回」这个 bug，前面几轮一直在改防抖/死区/幂等，**全是无关的**。

真正的重锁来自**三条路径**，只有一条受新加的双模式控制：

```
路径 A（受 RdpRelockScope 控制）OK
  看门狗 -> ShouldRelockByPointer -> 双模式 -> 3秒冷却 + 2秒宽限

路径 B（绕过）
  RdpConsole.cs OnOwnerDeactivated -> ShieldRearmRequested -> RearmShield

路径 C（绕过）<- 最关键
  RdpConsole.cs WatchPointerCapture:
    bool ownerInactive = !_ownerWindow.IsActive;   <- 又是 IsActive
    -> ShieldRearmRequested -> RearmShield
```

**路径 C 里的 `IsActive` 判断，正是从看门狗里删掉的那个，但这里还留着。**

最后（19:33）清掉了 B/C 两条路径，但**用户反馈仍无法解锁** —— 说明可能还有第四条路径，
或者清掉后引入了新的问题。**这是未完成的排查。**

---

## 4. 数学约束：为什么「不变形 + 零留边」不可能

用户控制区 `2370x1136`（宽高比 **2.086**），远端 `1920x1080`（**1.778**）。

| 做法 | 结果 |
|---|---|
| 等比取大（1.234） | 2400x1350，**溢出被裁** |
| 等比取小（1.052） | 2020x1134，**两侧各留 175px** |
| 非等比拉伸 | **铺满零留边，但横向拉伸约 17%** |

**宽高比不同时不存在数学解。** 这是硬约束，任何实现方案都绕不过。

---

## 5. 本会话改动的文件（需要逐个评估是否回退）

| 文件 | 最后修改 | 大小 | 本会话改动 |
|---|---|---|---|
| `Controls/RdpConsole.cs` | 19:40 | 150,371 | vtable 调用、连接触发、500ms 防抖、±2px 死区、最小化门控、清 B/C 路径 |
| `Controls/RdpConsole.Zoom.cs` | 19:44 | 16,441 | **新增文件**：本地放大逻辑（现为铺满） |
| `Controls/RdpMirrorOverlay.cs` | 17:28 | 19,343 | **新增文件**：DWM 缩略图镜像（未启用） |
| `Controls/RdpGeneralSettings.xaml(.cs)` | 19:43 | — | 加/删放大模式下拉、双模式重锁下拉 |
| `Services/RdpLocalZoom.cs` | 18:22 | 9,154 | **vtable slot 3/4 修正**（★ 核心成果） |
| `Services/LocalZoomGeometry.cs` | 18:20 | 7,173 | **新增文件**：几何计算 |
| `Services/NativeDispatch.cs` | 17:27 | 22,975 | **vtable slot 3/4 修正**（★） |
| `Models/Models.cs` | 19:45 | 34,906 | `RdpZoomFitMode`、`RdpRelockScope` |
| `Views/ConsolePage.xaml` | 19:43 | 35,240 | 顶栏按钮（已删）、背景透明 |
| `Views/ConsolePage.xaml.cs` | 19:43 | 132,400 | 看门狗双模式、冷却宽限、`HitTestTileUnderMouse` 兜底 |
| `Views/ConsolePage.Tiles.cs` | 19:43 | 37,033 | 1:1 背景（已删）、`PixelPerfect` 残留 |

---

## 6. 建议的恢复路径

### 方案 A（推荐）：回退二进制，源码从存档恢复

```
1. 备份当前 dist 到 E:\SoulLink-SOURCE-ARCHIVE\
2. 用 artifacts\pub-fitmode\SoulLink.exe（C1559970…，17:55）替换 dist/SoulLink.exe
3. 源码：对比 20261119-1657-before-2.13-localzoom\ 与本会话前备份，决定回退范围
```

`pub-fitmode` 是最后一个「功能相对完整且未引入 1:1」的版本。
但它**仍有已知问题**：灰边、铺满模式下的部分抖动。

### 方案 B：彻底回到 2.12.0

```
用 E:\SoulLink-SOURCE-ARCHIVE\v2.12.0\SoulLink-2.12.0.exe（844D4E7E…）
源码从 E:\SoulLink-SOURCE-ARCHIVE\20261119-1657-before-2.13-localzoom\source\ 恢复
```

**2.12.0 是用户真机验证过的稳定版**（保护层已验证，只是有灰边）。

### 方案 C：继续修当前版本

**不推荐。** 当前版本问题叠加：无法解锁 + 窗口缩小 + 闪烁白边 + HWND 重建。
每修一个都在暴露另一个，已经证明这条路走不通。

---

## 7. 如果将来要重做放大功能

### 7.1 绝对不要再做的

1. **不要给 `WindowsFormsHost` 设固定 `Width/Height/Alignment`**
2. **不要在 RDP 画面上盖 WPF 浮层**（`WS_EX_LAYERED` → 整块黑）
3. **不要相信「编译通过 = 功能正确」**

### 7.2 可考虑的路线

| 路线 | 说明 | 风险 |
|---|---|---|
| 接受变形（最简单） | 只用 SmartSizing 铺满，画面拉伸 | 无技术风险，只是变形 |
| 独立子进程 + SetParent | MobaXterm 路线，进程隔离 | 架构大改 |
| DWM 缩略图 | `DwmRegisterThumbnail` 拉伸，已实测可行 | 需自己转发输入，性能待验 |

**MobaXterm 机制已在 `releases/MOBAXTERM-RDP-EMBEDDING-DEEP-DIVE-20261117.md` 反汇编确认。**

### 7.3 如果继续用 ZoomLevel

vtable 修正（slot 3/4）是有效的。但要注意：

- `ZoomLevel` 与 `SmartSizing` 官方**互斥**
- 只接受标准档位 `{100,110,125,150,175,200,...}`（非标准值未验证）
- **不要在 RDP 空域窗口抢焦点时做任何尺寸判定**（`IsActive` 不可靠）

---

## 8. 从 1Remote 学到的东西（仍然有效）

源码：`docs/1remote-AxMsRdpClient09Host.xaml.cs.txt`（834-904 行）

```csharp
private readonly Timer _resizeEndTimer = new Timer(500);   // 500ms 防抖
private readonly object _resizeEndLocker = new object();   // 全局重入锁
private uint _previousWidth = 0, _previousHeight = 0;      // 尺寸去重

if (nw != _previousWidth || nh != _previousHeight)          // 精确去重
{
    _previousWidth = nw; _previousHeight = nh;
    _resizeEndTimer.Stop();
    _resizeEndTimer.Start();
}
```

**1Remote 的关键注释（840-843 行）：**

> "when tab window goes to min from max, base.SizeChanged invoke and size will get bigger,
> normal to min will not trigger this issue, don't know why.
> so stop resize when window status change to min until status restore."

**最小化时 `SizeChanged` 会给出错误尺寸 —— 这是「窗口缩小」的可疑原因之一。**

其他可参考：
- `CanResizeNow()`：只在 Connected/Disconnected 时允许 resize
- `_loginResizeTimer` = 300ms：连接成功后延迟 resize
- `WndProc` 重写处理 Focus 消息

---

## 9. 工程纪律（本次会话的血泪教训）

### 9.1 关于交付质量

**本次会话最大的问题是：成员反复报「编译通过 0 error」，但功能实际不可用。**

| 交付 | 声称 | 实际 |
|---|---|---|
| 1:1 居中 | 编译通过 | 灰边 + 卡顿 |
| 500ms 防抖 | 编译通过 | 窗口缩小 |
| 清两条路径 | 编译通过 | 仍无法解锁 |

**必须建立的要求：**

1. **不接受只报编译通过** —— 必须说明改动的**实际行为**
2. **改动前做静态推演** —— 走完整调用链，说明为什么不会循环
3. **每个改动必须有明确的验证方法** —— 不能验证的改动不做
4. **改 3 次不行立即停止报告** —— 不允许硬耗

### 9.2 关于 bug 排查

**修 bug 必须把所有触发点串起来看。**

「解锁后被锁回」前面几轮一直在改无关的代码，因为没人把 `ShieldRearmRequested`
的所有触发点列出来。等到 architect 提了一句「Deactivated 仍会无条件重锁」，
才发现还有两条完全绕过的路径。

**方法：先用 grep 列出某个事件/方法的所有触发点，再逐个判断。**

### 9.3 工具坑

- 改完 XAML 报「控件名不存在」→ 先删 `obj`/`bin`
- `read` 默认只返回约 975 行，**改大文件必须用 `edit`，禁止整文件 `write`**
- `artifacts/` 下不能放 `.cs`（会被通配符编译）

---

## 10. 绝对禁止（不变）

1. 修改/回退 `dist/stable/`，除非用户明确授权
2. 操作 A1/A4；测试只用 A2/A3
3. 不按 PID + SessionId + 完整路径核对就操作任何进程
4. 往 RDP 画面上盖任何 WPF 浮层
5. 手动叠加 `WS_EX_LAYERED` / `WS_EX_TRANSPARENT`
6. 用隔离截图证明黑屏已解决
7. **给 `WindowsFormsHost` 设固定尺寸**（本次新增）

---

## 11. 事故背景（不变）

2026-11-17 分屏验证后发生 Windows Bugcheck **0x119** `VIDEO_SCHEDULER_INTERNAL_ERROR`。
转储：`C:\Windows\Minidump\111726-18203-01.dmp`。NVIDIA nvlddmkm Event 153。

只确认时间关联与高风险组合，**未完成内核栈分析**。

同进程多 ActiveX 分屏、Canvas 内部窗口、独立顶级窗口原型 —— 已全部撤回，不要恢复。

---

## 12. 未解决的问题清单

- [ ] **无法解锁**（点画面后锁回，键鼠进不去 RDP）← 最严重
- [ ] 窗口缩放异常（被压缩）
- [ ] 铺满模式解锁闪烁 + 白边
- [ ] 多画面模式闪烁 + 白边
- [ ] 控件反复重建（HWND 变化）
- [ ] 最大化后画面不跟着放大（灰边，2.12.0 遗留）

---

## 13. 存档位置

```
E:\SoulLink-SOURCE-ARCHIVE\
├── 20261119-1426-full\              保护层改造完成时全量
├── 20261119-1517-before-2.12\       2.12 升级前
├── 20261119-1620-before-grayband\   本会话开始前全量（234 文件）
├── 20261119-1622-rdp-stretch-refit\ 中途备份
├── 20261119-1657-before-2.13-localzoom\  2.13 开发前（含 source\）
├── v2.12.0\                         2.12.0 成品 + source（SHA256 844D4E7E…，未动）
└── ...
```

**artifacts 下的包可以删**（dist 已有最新，且各版本可从存档重建）。