# 全屏 RDP 方案研究（2026-11-21）

> **最终状态：已实现并实测通过（v3.2.0）。**
> 实现走的是下文路线 A（主窗口全屏化），因为实现阶段发现
> `ConsolePage.Tiles.cs` 顶部有一条 0x119 事故遗留的硬约束：
> 「同一进程里的 ActiveX 绝不能在容器之间反复重挂」——路线 B（控件搬家）
> 直接违反该约束，被否决。且发现 ConsolePage 里已有全屏雏形
> （EnterFullScreen/ExitFullScreen + 顶部热区菜单条 + 切换账号菜单），
> 本次是**补完**而不是新写。
> 全部 10 项自动化检查通过（FlaUI harness + rust 合成帧测试服务器），
> 性能无新增异常。
> **全屏铺满**（4K 屏 + 1080p 远端拉伸铺满）：第一版用的 ZoomProperty 是死路
> （默认 IDispatch 上没有该成员，dynamic 直调失败）；查官方文档找到真 mstsc
> 「智能调整大小」的机制——**IMsRdpExtendedSettings.EnableZoom**，连接前设置后
> SmartSizing 即可放大铺满（详见「四、6」与「七」）。

## 一、现状（代码摸底）

- `Controls/RdpConsole.cs`：手写 AxHost（CLSID v9，无 aximp）+ `WindowsFormsHost`，单一 ActiveX。
- `Controls/RdpConsole.cs:2546`：`ContainerHandledFullScreen = 0`，注释明确原因 ——
  控件自己处理全屏会把 ESC 吃掉，游戏里 ESC 关不掉地图。**这个设置不能动。**
- `Controls/RdpConsole.cs:2827` / `3363`：已有热更新分辨率路径
  （`UpdateSessionDisplaySettings` + `ResizeGate` 信号量 + 防抖 + 等鼠标释放），
  `RequestResize` 用 `ViewportScreenRect()` 物理像素协商。
- 缩放模式：0=跟随窗口（协商分辨率）、1/3=拉伸（SmartSizing，只本地缩放）、2=原大小。
- `Views/ConsolePage.xaml`：单窗口多标签（同一时刻显示一个 RDP，其余保活），
  `ViewportHost` 为画面宿主，另有网格布局模式（`ConsoleLayoutMode`）。
- 保护层/护盾：`RdpConsole.LockShield` 直接禁用后台 tile 的 RDP 宿主窗口；
  浮层走 `Services/OwnedOverlayWindow.cs`（owner 锚定）。

## 二、三种技术路线对比

### 路线 C：控件自带全屏（IMsRdpClient.FullScreen = true）—— 排除

机制（官方文档确认）：

- `ContainerHandledFullScreen = 0`（当前值）时，设置 `FullScreen = TRUE` 或用户按
  Ctrl+Alt+Break，mstscax 会**自己创建顶级全屏窗口**并把控件窗口挂进去。
- `ContainerHandledFullScreen ≠ 0` 时是「容器处理全屏」模式：控件不弹自己的窗口，
  用户请求进出全屏时只发事件（OnRequestContainerMinimize 等），由宿主程序自己缩放窗口。

排除理由：

1. 控件弹出的顶级窗口脱离 SoulLink：保护层、热键、OwnedOverlayWindow、
   标签生命周期全部失效（Devolutions 论坛同款坑：嵌入控件自己蹦成全屏窗口）。
2. 与游戏 ESC 需求直接冲突（现有注释已实测踩过）。
3. 即使改用容器处理模式（=1），等于自己重新实现进出全屏事件，收益不如路线 B。

### 路线 A：主窗口自身全屏化 —— 备选（最低风险，UI 手术多）

MainWindow 设 `WindowStyle=None` + 覆盖显示器矩形 + 隐藏导航/TopBar，只留 ConsolePage 画面区。

- 优点：**完全不搬控件**，对 0x119 事故历史最保守；无重挂风险。
- 缺点：动 MainWindow 结构（切页、最小化、DreamScene、导航浮层、OwnedOverlayWindow 锚点
  都挂在主窗口上）；「全屏」语义上是全屏整个应用而不是单账号画面。

### 路线 B：专用全屏窗口 + 控件搬家（推荐）

SimpleRemote 已验证的模型（见 SIMPLEREMOTE-TABBED-RDP-RESEARCH-20261117.md 第 30-31 行）：
普通标签切换不动控件；只有显式全屏才把宿主控件从 Grid 移到全屏窗口，退出移回。
1Remote 同模型，且给了安全规则（见 RDP-WINDOWING-RESEARCH-SUMMARY-20261117.md 第五节）。

优点：

- 主窗口结构零改动；后台 tile 的 LockShield、保护层逻辑不受影响。
- 全屏窗口可置顶覆盖任务栏，是真 mstsc 式全屏；可放任意显示器。
- 分辨率协商复用现成热路径，全屏时远端 = 显示器物理分辨率，天然 1:1。
- 与 0x119 事故方案（同进程多 ActiveX 反复重挂）不同：只有单控件、用户显式触发、
  一次性搬移，不属于事故禁止的组合（禁令针对的是"多 ActiveX 分屏反复重挂"）。

风险与对策：

| 风险 | 对策 |
|---|---|
| WPF 控件重挂"已是逻辑子元素" | 先从旧父 Remove 再挂新窗口；每次进出全屏各一次，串行互斥 |
| 重连竞态下全屏窗口悬空 | 1Remote 规则：重连/注销路径不复用旧全屏窗口，连接断开即关全屏窗并搬回 |
| 分辨率重协商抖动（0x119 高风险组合之一） | 复用 ResizeGate + 现有防抖，进出全屏各协商一次，不连续抖动 |
| DPI 不同的显示器 | 全屏窗口尺寸用显示器物理像素设置（Win32 SetWindowPos 或 DIP 换算） |
| ESC 冲突 | 退出全屏热键用自有 `NativeHotkey`/`LowLevelKeyboardHook`，可配置；**绝不用 ESC**；可加悬浮退出角标兜底 |

## 三、推荐实现要点（路线 B）

1. `FullScreenHostWindow`：WPF Window，`WindowStyle=None`、`ResizeMode=NoResize`、
   `Background=Black`、`ShowInTaskbar=false`，边界 = 目标显示器整屏矩形；
   置顶策略：进入时 Topmost=true 覆盖任务栏，退出恢复。
2. 选显示器：`MonitorFromWindow`（当前 tile 的 `NativeViewportHandle`）→ 目标显示器矩形。
3. 进入：互斥锁 → tile 画面容器（或 RdpConsole）从 `ViewportHost` 摘除 → 挂入全屏窗 →
   Show → 等首次 SizeChanged 后按 mode 协商：
   - mode 0（跟随窗口）：协商为显示器物理分辨率（现有 `RequestResize` 路径）；
   - mode 1/3（拉伸）：只 `ApplyStretchRefit`，不动远端分辨率；
   - mode 2（原大小）：不协商。
4. 退出：热键/角标 → 搬回原 tile → `NoteViewportSizeChanged` 重新协商回 tile 尺寸 →
   恢复焦点（复用现有 CaptureFocus 机制）。
5. 生命周期分离（1Remote 规则）：连接、标签切换、全屏切换三者互不触发
   Dispose/重连；注销/断开时若在全屏态，先静默退出全屏再走原清理。
6. 多标签共存：全屏的是"当前查看"账号；标签栏仍可点击切换 —— 切换时先退出全屏再切
   （v1 不做"全屏中切标签"，保持串行简单）。

## 四、实现内容（v3.2.0）

改动文件：`Views/MainWindow.xaml(.cs)`、`Views/ConsolePage.xaml(.cs)`、`SoulLink.csproj`。

1. **主窗口 chrome 收纳**（`MainWindow.SetChromeFullscreen(bool)`）：
   内边距 `ChromeGrid.Margin`→0、标题行 `TitleRow`→0、`NavColumn`→0、`NavRail` Collapse；
   进入前若侧栏展开在浮层里先按「收起」处理（浮层是独立 HWND，Visibility 管不住）。
2. **真全屏几何**（ConsolePage.EnterFullScreen）：
   当前显示器整屏矩形（`Screen.FromHandle` 物理像素 → DIP）替代旧的全虚拟桌面矩形；
   `Topmost=true` 盖住任务栏（退出还原）；`WindowCorners.RemoveRounded` 去圆角；
   `SetWindowPos` 物理像素强制覆盖（WPF DIP + WindowChrome 隐形 WS_THICKFRAME
   在 4K/150% 下会差 26px，只改 DIP 永远盖不满）；`ResizeBorderThickness` 全屏归零、退出还原 6。
3. **退出恢复修复**：显式记住 `Left/Top/Width/Height`。旧实现退出时若之前是
   Normal 状态只调 `WindowState=Normal`，窗口停留在全屏尺寸（实测 bug，已修）。
4. **F11 热键**：`LowLevelKeyboardHook`（本机 LL 键盘钩子），CanTrigger 绑定本页可见性，
   其它应用里 F11 原样透传；RDP 子 HWND 持有焦点时 WPF KeyDown 收不到键，必须走 LL 钩子。
   **不实现 Esc 退出**（游戏内 ESC 冲突，见 RdpConsole.cs:2546 注释），tooltip 已修正。
5. **切页守卫**：`SetActivePage` 离开控制台页时先 `ExitFullScreenIfActive()`
   （全屏下导航已收纳，自动化/热键仍可能触发切页）。
6. **全屏铺满（EnableZoom）**：`RdpConsole.Configure()` 连接前经
   `IMsRdpExtendedSettings`（IID 302D8188-0052-4807-806A-362B628F9AC5，Win8+，
   IUnknown 直系；vtable 槽位用 typelib 探针核实：put_Property 在前）设
   **EnableZoom = true**。SmartSizing 官方语义是**只缩不放**，EnableZoom 是
   真 mstsc「智能调整大小」能全屏拉满的放行开关（官方文档原文："allows the
   user to scale the remote session window above its native resolution while
   smart sizing is active"）。远端分辨率不动，纯本地缩放。
   - 中途试过 IMsRdpClient8/9 的 `ZoomProperty`（dynamic 直调）——失败：
     默认 IDispatch 不含该成员（`System.__ComObject does not contain a
     definition`），已整体拆除。
   - Win11 24H2 之前的系统没有 EnableZoom 属性 → 设置失败自动降级为旧行为
     （只缩），日志有记录。
   - 副作用（有意为之）：拉伸模式下**窗口**大于远端时同样放大铺满，
     与真 mstsc 智能调整的语义一致（此前窗口内是 1:1 居中留黑边）。

## 五、测试方法（可复用）

- **被测应用非提权启动**：`dotnet exec bin\Release\net11.0-windows\SoulLink.dll --page console`
  + `SOULLINK_CONFIG` / `SOULLINK_DATA_DIR` / `SOULLINK_NOANIM` 环境变量——
  绕过 exe 清单的 requireAdministrator，UIA 驱动也就不需要提权，全程无 UAC。
- **测试服务器**：rust 版 `rust\target\release\soullink-test-server.exe --port 33890
  --width 1280 --height 800 --fps 15`（合成帧 RDP，登录 soullink/soullink）。
- **UI 自动化**：FlaUI（UIA3）harness（%TEMP%\sl-fs-harness，Console 程序）：
  - 连接判定用 `IPGlobalProperties.GetActiveTcpConnections()` 等 33890 ESTABLISHED
    （busy 浮层是独立 HWND，UIA 主窗口树里看不到，文本匹配不可靠）；
  - 打开画面用**双击用户行**（WPF ClickCount==2 → ShowViewPageAsync 直达）。
    单击选中 + BtnOpenView 的路径在 UIA 下不稳定（元素游离、重挂时序）；
  - 全屏切换用 UIA InvokePattern（`BtnFullScreen` / `BtnExitFullScreen`），
    菜单条隐藏后把光标移到顶部 2px 热区重新召唤；
  - 几何/Topmost 用 GetWindowRect + GWL_EXSTYLE 断言，每阶段截图存证。
- F11 键本身无法机测：钩子 matcher 忽略 injected 事件（防回放设计），
  需人工按一次验证。

## 六、性能结论

分阶段采样（SoulLink 进程，单核基准；4K/150% 单显示器，rust 测试服务器 15fps）：

| 阶段 | CPU(单核) | 句柄 | 线程 | 私有内存 |
|---|---|---|---|---|
| UI 空闲（未连接） | ~90% | 721→759 | 41→45 | 245→266 MB |
| RDP 已连接（窗口内） | ~91% | 1561→1562 | 63 | 307→308 MB |
| 全屏中 | ~94% | 1563→1564 | 63→64 | 452→460 MB |
| 退出全屏后 | ~82% | 1564→1559 | 64→63 | 458→433 MB |

- **全屏代价：+3% 单核 CPU（4K 上采样）+ ~145MB（上采样缓冲）**，退出后句柄/线程
  无增长（本窗口内），无泄漏迹象。
- **空闲 ~0.8–0.9 核为存量行为**：stash 回 v3.1.10（NOANIM）实测 0.80 核/秒，
  与本次改动版 0.78 核/秒持平——与全屏功能无关，属于控制台页自身的既有开销，
  待单独排查（与句柄 15 分钟观察规则一样，不归本次改动）。
- AGENTS「新增周期任务走 TickHub」：本次没有新增定时器；全屏菜单热区轮询是
  存量 120ms DispatcherTimer（TickHub 秒级粒度接不了），仅全屏期间运行，量级 ~8Hz 空转。

## 七、实测结果（10/10 通过）与已知限制

| 检查 | 结果 |
|---|---|
| 打开画面（双击用户行） | ✅ |
| RDP 连接（rust 测试服务器） | ✅ |
| 全屏几何 = 显示器矩形（两轮） | ✅ 0,0 3840x2160 |
| Topmost 生效（盖任务栏）/ 退出清除 | ✅ |
| 顶部热区召唤菜单条 | ✅ |
| 退出还原几何（精确到原位置原尺寸） | ✅ 1071,495 1770x1170 |
| 第二轮往返（可重复性） | ✅ |
| **全屏铺满（1280x800 → 4K）** | ✅ 两轮截图四角/边缘/中心 9 点采样全非黑，棋盘格铺满整屏 |

**全屏铺满（2026-11-21 二次修复）**：拉伸模式全屏现在**本地放大铺满**，
远端分辨率不动。机制 = 连接前 `IMsRdpExtendedSettings.EnableZoom = true`
放行 SmartSizing 放大（见「四、6」）。实测证据：rdp-debug 日志
`EnableZoom = true`（连接前）+ 两轮全屏截图 9 点像素采样全非黑 +
目视棋盘格铺满；窗口内拉伸同样放大铺满（与 mstsc 语义一致）。
注意：Win11 24H2 之前的系统无此属性，自动降级为「只缩」旧行为。

## 八、参考

- SimpleRemote（beckzhu）：全屏=控件搬进 FullScreenWindow，关闭标签才 Dispose。
- 1Remote：MoveSessionToFullScreen/MoveSessionToTabWindow；重连竞态不复用旧全屏窗；
  从全屏返回强制重协商；AutoResize 500ms 防抖 + 全局重入锁 + 等鼠标释放（SoulLink 已有同款）。
- 官方语义：IMsRdpClient.FullScreen 与 IMsTscAdvancedSettings.ContainerHandledFullScreen
  （Microsoft Learn，见下）。

来源：

- https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpclient-fullscreen
- https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpextendedsettings-property
  （EnableZoom / ZoomLevel 命名属性表，EnableZoom 即智能调整放大的放行开关）
- https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpclientadvancedsettings-smartsizing
- https://github.com/MicrosoftDocs/win32/blob/docs/desktop-src/TermServ/msrdpclient2notsafeforscripting.md
- https://forum.devolutions.net/topics/28846/embedded-rdp-session-opens-in-full-screen-window-not-tab
