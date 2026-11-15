# SoulLink 交接文档

> 给接手的新会话看。**只写本会话实际验证过的结论**；未验证的会标「待验证」，不要当成已完成。
> 最后更新：2026-11-15（热键修复 + 自由视角鼠标落点 + 通用设置持久化 + 侧栏对齐 + 边框强调色）
> 完整历史踩坑记录见 [HANDOFF.archive-2026-11-15.md](HANDOFF.archive-2026-11-15.md)（旧文档，2026-11-14 至 15 日，含 12.1~12.94 全部细节）。

---

## 一、项目定位

把「自动查找 ID 启动其他用户程序.bat」升级为 **多用户挂机中控台**：

1. 同时管理多个 Windows 用户会话（本机：lummy + a1~a4）
2. 跨会话启动/关闭程序，**不抢前台键鼠**
3. 内嵌 RDP 画面、标签页切换
4. 注入鼠标实现**转游戏视角**（RDP 原生做不到）
5. 免安装、解压即用

技术栈：WPF / .NET 8（`net8.0-windows`），单文件发布到 `dist\SoulLink.exe`。

---

## 二、测试红线（必须遵守）

| 用户 | 允许操作 |
| --- | --- |
| **A1、A4** | ❌ 绝对不允许任何操作：不投送、不注入、不 tsdiscon、不 logoff、不查询 |
| **A2、A3** | ✅ 可随意登录/注销/连接画面；**不要删除用户文件** |

其他约束：

- 必须以**管理员权限**运行（跨会话投送与 RDP 注入依赖）。
- 涉及 a2/a3 会话状态的操作，测完要恢复原状态（如该用户原本挂机的程序不能因测试被注销）。
- 测试临时文件放 `tools\`（已被 .gitignore 排除），结束前清理。
- 验证后**主动关闭测试应用**，不要留给用户关。

---

## 三、环境事实

| 项目 | 值 |
| --- | --- |
| 系统 | Windows 11 专业工作站版 Insider Preview，Build 29698 |
| 显卡 | RTX 4060 Laptop，真实显存 8188 MiB（WMI 报 4GB 是 32 位溢出） |
| 多用户 RDP | 依赖 **RDPWrap 补丁**，非原生；系统更新会顶掉 |
| 本机用户 | lummy（当前会话）、a1、a2、a3、a4 |
| 环回地址 | A1=127.0.0.2，A2=127.0.0.3，A3=127.0.0.4，A4=127.0.0.5 |

⚠️ **环回地址不能按会话号推算**：所有环回 RDP 客户端在服务端看来都是 127.0.0.1，
必须以配置/探测结果为准（旧文档 10.6）。

---

## 四、★ 关键技术结论（已实测，不要重新试错）

### 4.1 RDP 密码设置（本会话最终修复）

1Remote 的做法是 `IMsTscNonScriptable.ClearTextPassword`，但**接口必须是 IUnknown，不能是 Dual**：

```csharp
[ComImport, Guid("C1E6743A-41C1-4A74-832A-0DD06C1C7A0E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMsTscNonScriptable
{
    [DispId(1)] string ClearTextPassword { set; }
}
((IMsTscNonScriptable)ocx).ClearTextPassword = password;
```

- `InterfaceIsDual` 在 .NET 8 抛 `NotImplementedException`。
- 旧 IDispatch 路径报「无该名字(0x80004001)」，不可用。
- 该属性**只写、不能回读**验证。
- **A3 已实测**：设置后直接进入桌面，不再弹 Windows 凭据输入框。
- 不要改用 Windows 凭据管理器（`CredWrite`）作为替代方案，方向已否定并移除。
- 接口定义文件：`Services/RdpNonScriptable.cs`；调用点：`Controls/RdpConsole.cs` Configure 阶段。

### 4.2 主窗口必须是非分层窗口

- `AllowsTransparency=True` 的分层窗口里，RDP ActiveX 能连上、会话是 Active，但**画面永远全黑**。
- 主窗口因此用 `AllowsTransparency=False` + 非客户区自绘。
- 圆角只能靠 DWM：`WindowCorners.ApplyRounded`（`DWMWA_WINDOW_CORNER_PREFERENCE`）。
- 顶部多余横幅用 `WM_NCCALCSIZE` 返回 0 去掉（`MainWindow.WndProc`）。**不要**用 `DWMWA_BORDER_COLOR` 把系统主题色边框改成深色（用户明确要求保留系统主题色）。

### 4.2.1 ★ 应用边框用系统强调色（本会话修复，两条都是实测结论）

**坑一：`DWMWA_BORDER_COLOR` 要的是 COLORREF，不是 ARGB。**

- 取值格式是 **`0x00BBGGRR`**，最高字节 alpha **必须为 0x00**。
- 传 `0xFFRRGGBB`（ARGB）→ DWM 返回 **`E_INVALIDARG (0x80070057)`**，边框**静默保持原样**。
  旧代码一直传 ARGB，所以「设置边框颜色」从未生效过，看到的永远是系统默认边框。
- 两个特殊值：`0xFFFFFFFF` = `DWMWA_COLOR_DEFAULT`（恢复原生），`0xFFFFFFFE` = `DWMWA_COLOR_NONE`（不画边框）。
- 已落地：`WindowCorners.SetBorderColor` 接收 ARGB，内部 `ArgbToColorRef` 换算；
  `ResetBorderColor` 用于未激活态。**传值前后务必看返回值，别假设成功。**

**坑二：强调色不能取 `DwmGetColorizationColor`。**

| 来源 | 本机实测值 | 能否用作边框色 |
| --- | --- | --- |
| 注册表 `HKCU\Software\Microsoft\Windows\DWM` → `ColorizationColor` | `#F091C1` | ✅ **正确**，与原生窗口逐像素一致 |
| `DwmGetColorizationColor` | `#E48AB7`（`blend=true`，开启透明效果后是**混合后**的着色） | ❌ 偏暗偏灰 |
| 注册表 `AccentColor` / `AccentColorMenu` | `#F091C1`（COLORREF `0xFFC191F0`，需转换） | ✅ 兜底 |

取值顺序已固定为：**注册表 ColorizationColor → DwmGetColorizationColor → 注册表 AccentColor → 硬编码蓝色**。

**行为（已实测）**：

- 激活态四边 = 强调色 `#F091C1`；与资源管理器激活态顶边 `#F091C1` **逐像素相同**（`docs/fix-accent-border-active.png`）。
- 未激活态交回 `DWMWA_COLOR_DEFAULT`，实测 `#B2B2B3` 灰边，与原生窗口失焦表现一致（`docs/fix-accent-border-inactive.png`）。
- 改系统强调色后会**实时刷新**（`SystemEvents.UserPreferenceChanged` 钩子）：实测改蓝立即变 `#3A8BD5`，改回立即可复原。
- 应用内深浅主题切换**不影响**边框色（本来就该跟系统强调色走），三种模式下实测均为 `#F091C1`。
- 最大化时四边无色属**正常**：原生窗口最大化同样不出边框，不是缺陷。

### 4.2.2 ★ 侧边栏两态几何（本会话修复，实测结论）

侧栏是「收起 = 独立圆角卡片 / 展开 = 图标栏 + Popup 面板拼成一张卡」，两块必须严丝合缝。

**布局基线（2026-11-15 二次修正，以此为准）**

- 内容区 `Grid Margin="20,10,20,16"`；本机 **150% 缩放**，1 DIP = 1.5 px，1770×1170 物理 = 1180×780 DIP。
- **`NavRail` 的 `Margin="0"` + `Width="56"`**，间距全由列宽承担：
  `NavColumn.Width = 76`（56 图标栏 + 20 间距）。
- 外边距**不能重复加**：外层内容区已有 `Margin(20,...)`，
  rail 若再写左边距，实际会变成 40 DIP，与右侧卡片的 20 DIP 不对称（实测踩过）。
- rail 也不再设 `VerticalAlignment`/上下 Margin —— 主体 Grid 就是内容区，
  上下边距由外层 `Margin` 提供；rail 天然与右侧卡片**等高**。
  之前写 `Margin="10,4,0,16"` 导致顶差 4 DIP、底差 16 DIP，即用户说的"高度不齐"。

**✅ 对齐验收（实测像素，150% 缩放）**

| 项 | rail | 右侧卡片 | 结论 |
| --- | --- | --- | --- |
| 上边框 | y=111 | y=111 | 对齐 |
| 下边框 | y=1144 | y=1144 | 对齐 |
| 左边缘距内容区 | 30px = 20 DIP | 20 DIP（右侧） | 对称 |

- 列宽写死 56 的旧结论作废：现在 rail 显式 `Width="56"`，不在 Margin 上做减法。

**坑一：Popup 的 `ElementName` 绑定不跨视觉树**

- Popup 内容在独立视觉树里，`Height="{Binding ActualHeight, ElementName=NavRail}"` **绑不上**，
  高度掉回默认值 → 面板塌成一个 202×56 的不可见小窗，**侧栏根本弹不出来**（且日志无任何报错）。
- 必须在代码里显式同步：`NavPanel.Height = NavRail.ActualHeight`。

**坑二：`Placement="Right"` 会按 Margin 外框算，差 2px**

- `NavRail` 带 Margin 时 Popup 定位偏 2px，露出窗口底色形成竖缝。
- 改用 `Placement="Relative"` + `PlacementTarget` 指向外层 Grid，再自己算偏移：

```csharp
var offset = NavRail.TranslatePoint(new Point(0, 0), SideNavHost);
NavPopup.HorizontalOffset = offset.X + NavRail.ActualWidth - 2;  // -2 盖住 rail 的右边框
NavPopup.VerticalOffset   = offset.Y;                            // ★ 必须加，漏了顶部高 4 DIP
NavPanel.Height           = NavRail.ActualHeight;
```

**坑三：两条 1px 边框并排 = 一条看得见的接缝**

- rail 有右边框、panel 有左边框，并排就显出一条 `#505469` 亮线。
- 解法：`NavPanel.BorderThickness = "0,1,1,1"`（不画左边），再把 Popup 左移 2px 盖住 rail 边框。
- 只移 1px 仍会露 1px 亮线（抗锯齿），**取 2px**。

**圆角**

- 收起：`NavRail.CornerRadius = 14`（四角全圆）。
- 展开：`NavRail.CornerRadius = (14,0,0,14)` + `NavPanel.CornerRadius = (0,14,14,0)`，
  拼接处两边都直角，外侧两角圆 —— 合起来是一张完整卡片。
- 两态切换统一走 `MainWindow.UpdateNavSurfaceShape(bool collapsed)`，
  `ThemeManager.Apply()` 也调它（否则主题刷新会把圆角写回旧值）。

**展开态底部区**

- 「分隔线 → 鼠标悬停展开 → 当前会话」必须放 **`Grid.Row="2"`（Auto，贴底）**。
  历史上这三块塞在导航的 `StackPanel` 里，`Grid.Row="2"` 写了也无效，
  它们会紧跟导航项浮在面板中部（用户看图像是"悬空"）。
- 底部区用 **`Grid` 而非 `StackPanel`**：StackPanel 里 `GlassPanel` 只按内容宽度收缩，
  「当前会话」卡片会缩成一小块；Grid 才会水平撑满面板。

**实机验证**（`docs/fix-sidebar-align-collapsed.png`、`fix-sidebar-align-expanded.png`）

- 收起态：左边距 20 DIP，与右侧卡片对称；上下边框与右侧卡片齐平（y=111 / y=1144）。
- 展开态：rail 右缘 → panel 左缘**无亮线**；底部区贴底、撑满宽度。

### 4.2.3 ★ WPF 控件构造期事件会把默认值写进配置（本会话修复）

**症状**：「RDP 通用设置」改完保存了，下次启动又变回去（灵敏度总被冲回 1/4）。

**根因**：`InitializeComponent()` 建立模板时，`Slider` 会**先按 XAML 里的 `Minimum` 发一次 `ValueChanged`**
（`old=0.00 new=1.00`）。那一刻 `_loading` 还是 `false`（它原本在 `LoadSettings()` 里才置 true，
而 `LoadSettings` 挂在 `Loaded` 上，还没执行），于是这条"默认值"被当成用户操作写进了配置：

```
Sensitivity_Changed(new=1.0) → Settings.LookSensitivity = 1 → ConfigStore.Save()
```

用户保存的 9.5 就这样在每次启动时被覆盖成 1。

**修复**：`_loading` **字段初始值直接写成 `true`**，让构造期事件一律被挡掉；
`LoadSettings()` 里读完真实配置后再置回 `false`。

```csharp
private bool _loading = true;   // ★ 必须初始 true
public RdpGeneralSettings() { InitializeComponent(); Loaded += (_, __) => LoadSettings(); }
```

**同类隐患（一并修了）**：`ConsolePage` 工具栏浮层里还有一个 `SensSlider`（XAML 写死 `Value="4"`），
和通用设置共用同一项设置。构造函数里给它赋值同样会触发 `ValueChanged` 写回配置。
已加 `_suppressSensSave` 守卫包住初始化赋值。

**排查手法**：这类"值被悄悄覆盖"光看代码很难定位。加一行临时日志即可一击命中——
把 `old/new/loading` 都打出来，日志里 `loading=False` 就是元凶（诊断代码已移除）。

**实机验证**：写入 `LookSensitivity=8.5 / RdpCaptureOnClick=false` → 启动 → 两项均原样保持；
滑块位置与数值文字同步（`docs/fix-general-settings-slider.png`）。

### 4.2.4 ★ 滑动条样式（`GlassSlider`）

默认 WPF Slider 是白色粗条 + 方头，和本应用玻璃风完全不搭。已在 `Theme/Styles.xaml` 重做模板：

- 轨道 4px 圆角；已填充段用 **青→紫罗兰渐变**（和标题栏光晕呼应），未填充段是低透明度白。
- 滑块 12px 圆点，白色填充 + 青色描边；悬停/拖动时放大并亮起光晕。
- `IsMoveToPointEnabled=True`：点轨道任意位置直接跳过去，不用拖。
- 去掉默认刻度线，但保留 `TickFrequency` 以支持离散取值。

**顺带修的 UI 缺陷**：通用设置页里「自由视角灵敏度」标签和数值曾经**重叠在一起**，
原因是两个 `TextBlock` 都没写 `Grid.Column`，全落在第 0 列。凡是用 `Grid.ColumnDefinitions`
布局的都要显式写 `Grid.Column`。

### 4.2.5 ★ 热键三连坑（本会话修复）

**坑一：`ToString()` 与 `Parse()` 往返不一致**

- `HotkeySpec.ToString()` 输出中文名「鼠标侧键4」，但 `Parse()` 原来只认英文 `mouse4`/`x1`。
- 结果：`ConfigStore` 存下去的中文名读回来 `Parse` 得到 **null** → 热键配置整个丢失。
- 修复：`Parse()` 增加中文分支（鼠标左键/右键/中键/侧键4/侧键5）。
- **教训：凡是 `ToString` 会被持久化的类型，`Parse` 必须能读回自己的输出。**

**坑二：`Loaded` 里只同步了一部分全局设置**

- `RdpConsole.HookInput` 的 `Loaded` 原来只写了 `CaptureOnClick = ...`，
  **没有调用 `ApplyGlobalSettings()`**，于是 `ToggleHotkey` 永远停在字段初值 `Ctrl+Alt+Q`。
- 用户改的热键在配置里是对的，但注册的永远是默认值。
- 日志证据：`EnsureToggleHotkey: spec='Ctrl+Alt+Q'` 而配置文件里是 `Ctrl+Alt+K`。
- 修复：`Loaded` 直接调 `ApplyGlobalSettings()`（它一并同步灵敏度、热键、点击捕获）。

**坑三：辅助窗口被 `Hide()` 后收不到 `WM_HOTKEY`**

- `RegisterHotKey` 把 `WM_HOTKEY` 投递到**该窗口的消息队列**。
- 原实现 `helper.Hide()` 之后，`WndProc` 完全收不到消息（`RegisterHotKey` 却返回 true，日志还显示"已注册"）。
- 修复：**不要 Hide**，改为保持显示 + 挪到屏幕外 + 全透明：

```csharp
helper.Show();
helper.Width = 1; helper.Height = 1;
helper.Left = -32000; helper.Top = -32000;
helper.Opacity = 0;
```

**验证限制（重要）**：`SendInput`/`keybd_event` 模拟的按键**无法触发 `RegisterHotKey` 的系统热键**，
所以"按热键"这一步没法用脚本自动化验证，只能由人手按。
本会话通过日志确认了注册正确（`锁定热键已注册：Ctrl+Alt+K`、`parsed=鼠标侧键4`），
但**实际按键触发需要人工确认**。

### 4.2.6 ★ 自由视角不能影响鼠标落点

**症状**：开启自由视角后，无法正常拖动地图与点击传送。

**根因**：自由视角下本地光标被 `SetCursorPos` 钉在画面中心，而按键事件当时是
`return false`（放行给 RDP 原生通道）。RDP 发出去的点击坐标因此**永远等于中心点** ——
用户点哪里都落到屏幕正中，拖拽也拖不动。

**修复**：按键改走注入通道，远端光标停在它自己的位置，和视角转动解耦：

```csharp
// 滚轮：放行（不影响落点）
if (ev.IsWheel) return false;

// 鼠标按键：走注入，down/up 分离（拖拽需要按住不放）
if (ev.IsLeftDown || ev.IsRightDown || ev.IsMiddleDown || ev.IsXButton1Down || ev.IsXButton2Down) {
    _ = InjectButtonAsync(ButtonNameOf(ev), true);
    return true;
}
if (ev.IsLeftUp || ev.IsRightUp || ev.IsMiddleUp || ev.Message == 0x020C /*WM_XBUTTONUP*/) {
    _ = InjectButtonAsync(ButtonNameOf(ev), false);
    return true;
}
```

- **必须用 `down`/`up` 而不是 `click`**：拖拽地图要的是"按住不放 + 移动 + 松开"。
- `WM_XBUTTONUP = 0x020C` 原来在 `MouseHookEvent` 里没有判定，已补 `IsXButtonUp`。

### 4.3 RDP 空域（Airspace）：WPF 浮层盖不住画面

- RDP 是 `WindowsFormsHost` 里的 ActiveX，永远绘制在 WPF 之上，`Panel.ZIndex` 无效。
- 需要浮层时只能用 **`Popup`** 或**独立窗口**。
- 已落地：侧栏浮层用 `Popup`；注销过场用独立 `Window`（`Views/LogoffTransitionWindow`）；
  连接遮罩用 `BusyPopup`。

### 4.4 注销过场（独立窗口）的坑

- 过场必须只属于**当前用户视口**：通过 `Func<bool>` 归属判断 + 80ms 定时器检查。
- 切页/失焦/最小化时隐藏；完成态禁止再次 `Show()`（否则会残留在其他用户画面上）。
- **已实测**：A2 注销过程中切到 A3，A3 画面正常显示不被覆盖。

### 4.5 跨会话注入必须真 SYSTEM

| 方案 | 结果 |
| --- | --- |
| 管理员令牌 + `WTSQueryUserToken` | ❌ 1314 |
| 窃取 SYSTEM 令牌后冒充 | ❌ 1314 |
| `CreateProcessWithTokenW` | ⚠️ 假成功（进程落在调用者会话） |
| 真 SYSTEM（服务 / `schtasks /ru SYSTEM`） | ✅ 可用，免安装方案 ~459ms |

注入代理直接复用 `SoulLink.exe` 本体（`--relay` 模式），通过命名管道下发指令。

### 4.6 其他已验证结论

- 鼠标注入：`mouse_event` 相对位移可转视角；`SetCursorPos` 绝对定位误差 0；点击/右键/滚轮均命中。
- **Disc（断开）会话没有可交互桌面**，必须先让 RDP 接管画面才能注入。
- 对**已断开**的会话执行 `tsdiscon` 会直接注销它（旧文档 10.7，务必避免）。
- 同一会话重复连接会把前一个客户端踢掉。
- 转视角必须临时关闭系统指针加速，且**幂等恢复**（引用计数），退出时兜底还原。
- 坐标换算必须用物理像素（`PointToScreen`），不能混用 DIP：本机 150% 缩放。
- DesktopScaleFactor 等 DPI 属性在本机不被 RDP 控件接受（4 种写法均失败），靠宿主侧 `CoordinateMapper` 消化。

---

## 五、当前代码结构

```
SoulLink/
├─ App.xaml.cs              启动、单实例、IPC、托盘、异常日志
├─ Models/Models.cs         Account / ActionItem / AppSettings / AutomationProfile
├─ Controls/
│   ├─ RdpConsole.cs        内嵌 RDP 控件 + 输入接管 + 密码/性能/重定向配置
│   ├─ RdpGeneralSettings.* RDP 通用设置（作用于全部用户）
│   ├─ Backdrop.cs          梦幻极光背景（随主题深浅变色）
│   ├─ GlassControls.cs     玻璃风格自定义控件
│   └─ HotkeyRecorder.cs    热键录制
├─ Services/
│   ├─ ThemeManager.cs      三态主题 + 动态资源刷新
│   ├─ WindowCorners.cs     DWM 圆角 / 边框颜色 / 系统强调色
│   ├─ RdpNonScriptable.cs  IMsTscNonScriptable（IUnknown）
│   ├─ NativeDispatch.cs    原生 IDispatch 桥（其他属性）
│   ├─ SessionService.cs    query user / 会话解析
│   ├─ SessionPrepare.cs    tsdiscon / logoff
│   ├─ SystemLauncher.cs    SYSTEM 计划任务通道
│   ├─ SessionInput.cs      注入代理控制
│   ├─ LowLevelMouseHook.cs 全局鼠标钩子
│   ├─ PointerAcceleration.cs 指针加速幂等恢复
│   ├─ AutomationRunner.cs  多用户顺序执行 + 每日定时
│   └─ ConfigStore.cs       JSON 配置 + DPAPI 密码
├─ Views/
│   ├─ MainWindow.*         主窗口（侧栏 Popup、主题、导航）
│   ├─ ConsolePage.*        中控台（用户列表 / 配置 / 画面标签）
│   ├─ AutomationPage.*     自动化管理（配置组 / 定时）
│   ├─ SettingsPage.*       应用设置（不含 RDP 凭据）
│   ├─ AppDialog.*          统一自绘弹窗
│   ├─ LogoffConfirmWindow.* 注销二次确认
│   └─ LogoffTransitionWindow.* 注销过场（独立 HWND）
└─ Theme/Styles.xaml        全部样式与主题画刷
```

### 配置字段要点（`Models/Models.cs`）

- `Account.SortOrder`：用户列表顺序（拖动排序持久化）。
- `Account.RdpUserName`：RDP 登录名；`UserName`：跨会话注入/会话解析用的 Windows 账户名。**两者不能混用**。
- `Account.RdpAudioMode / RdpPerformanceMode / RdpKeyboardHookMode / RdpRedirect*`：已实测保存并回读。
- `AppSettings.ThemeMode`：0=跟随系统、1=浅色、2=深色。
- `AppSettings.RdpCaptureOnClick`：点击画面才捕获键鼠。
- `AppSettings.LookSensitivity / LockHotkey`：自由视角灵敏度与热键。
- `AutomationProfile`：配置组（用户范围、动作流程、每日定时、启用状态）。

---

## 六、已完成并实测

| 功能 | 状态 | 证据 |
| --- | --- | --- |
| 用户多选 + 批量打开画面 | ✅ | `docs/verify-multi-select.png` |
| 新增用户即进入配置表单 | ✅ | `docs/verify-new-user.png` |
| 用户拖动排序（右侧手柄，`SoulLinkAccount` 拖放格式） | ✅ 顺序写入 `SortOrder` | `docs/verify-sort.png` |
| 顶部固定「保存配置/移除用户」 | ✅ | `docs/fix-inline-password.png` |
| 账号密码内联编辑（端口 100px 固定列） | ✅ | `docs/fix-inline-password.png` |
| A3 保存密码后自动登录 | ✅ 进入桌面 | `docs/fix-a3-login.png` |
| 自由视角按钮进入/退出 | ✅ | 按钮文案切换「自由视角 / 自由视角中」 |
| RDP 通用设置内嵌（全用户共享） | ✅ | `docs/fix-general-settings.png` |
| 通用设置可返回用户配置 | ✅ | 实测点击用户卡片 |
| 断开关闭标签页（不注销用户） | ✅ | 实测 |
| 无有效会话时注销只关画面 | ✅ | 代码路径 `CloseViewPage` |
| 有效会话注销：二次确认 + 过场 + 恢复配置页 | ✅ | `docs/verify-logoff-confirm.png` 等 |
| 过场不覆盖其他用户画面 | ✅ | `docs/transition-other-user.png`、`transition-complete.png` |
| 浅色侧栏随主题变浅、无中间缝 | ✅ | `docs/fix-light-sidebar.png` |
| 系统主题色边框 + 无顶部粉条 | ✅ | `docs/window-top-caption.png`、`window-top-crop.png` |
| 边框真正用上系统强调色（COLORREF 修复） | ✅ 激活 `#F091C1` / 失活灰边，与原生窗口逐像素一致 | `docs/fix-accent-border-active.png`、`fix-accent-border-inactive.png` |
| 侧栏收起态圆角 + 四周边距 | ✅ 左 10 / 上 4 / 下 16 DIP，四角圆 | `docs/fix-sidebar-collapsed.png` |
| 侧栏展开态无缝拼接 | ✅ 无接缝、上下边界与图标栏对齐 | `docs/fix-sidebar-expanded.png` |
| RDP 通用设置真正持久化 | ✅ 重启后灵敏度/点击捕获/热键均保持 | `docs/fix-general-settings-slider.png` |
| 滑动条改为玻璃风 | ✅ 青紫渐变轨道 + 发光圆点 | 同上 |
| 灵敏度标签与数值重叠 | ✅ 补 `Grid.Column` | 同上 |
| 热键 ToString/Parse 往返一致 | ✅ `parsed=鼠标侧键4` 不再为 null | 日志证据 |
| 热键真正用上配置里的值 | ✅ `锁定热键已注册：Ctrl+Alt+K` | 日志证据 |
| 自由视角不影响鼠标落点 | ⚠️ 代码已改（按键走注入 down/up），**待人工按键确认** | 见 4.2.6 |
| 统一自绘弹窗替代 MessageBox | ✅ | `Views/AppDialog` |
| 自动化配置组 + 顺序执行 + 每日定时 | ✅ 保存与界面 | `docs/verify-automation.png` |

---

## 七、待验证 / 待完成

以下**不要当成已完成**：

1. **点击捕获（`RdpCaptureOnClick`）的完整手感**：首次点击已改为送达 RDP，但“移出即释放、重新点击才接管”的多场景（切页、失焦、最小化）尚未逐项实测。
   注：该项的**保存与回显**已修好（4.2.3），重启后能正确保持。
2. **浅色模式跨页面文字对比度**：侧栏已修，启动台/在线会话/会话详情页是否全部达标未逐页确认。
3. **自动化实际执行**：只验证了配置保存与界面，未实际跑过一条龙流程与定时触发。
4. **应用设置页（`SettingsPage`）**：已移除 RDP 凭据区，但页面内仍有「修改密码/地址/移除」按钮绑定的旧事件处理，需要确认没有残留入口或空操作。
5. **窗口最大化下的边框/圆角**：已确认最大化时无边框属正常（原生窗口同样如此），
   圆角与拖拽还原在最大化/还原切换后正常；但**最大化下的圆角裁切**仍未逐像素核对过。
6. **多用户同时注销**：只验证了 A2 注销期间切 A3，未测试两个用户同时注销。

---

## 八、开发与发布流程

```powershell
# 编译
dotnet build -c Release

# 发布（生成 dist\SoulLink.exe）
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist

# 或直接运行 build.bat
```

- 发布后**必须实机启动验证并截图**，不能只凭编译通过就报完成。
- 验证完关闭自己启动的 SoulLink 进程：
  ```powershell
  Get-Process SoulLink -ErrorAction SilentlyContinue | Stop-Process -Force
  ```
- 日志位置：`%LOCALAPPDATA%\SoulLink\`：`boot.log`、`error.log`、`rdp-debug.log`。
- 配置位置：`%LOCALAPPDATA%\SoulLink\config.json`（密码为 DPAPI 密文）。

---

## 九、版本管理

项目已初始化本地 Git，详见 [VERSIONING.md](VERSIONING.md)。

```powershell
git log --oneline -10                      # 查看提交
git restore --source=HEAD --staged --worktree -- .   # 丢弃未提交修改
git switch --detach <hash>                 # 查看旧版本
git revert <hash>                          # 撤销已提交的错误
```

提交约定：

- 只提交源码、XAML、文档；`bin/obj/dist/tools` 与本地凭据已忽略。
- **每次实机验证通过后再提交**，提交信息写清修复内容。
- `dist\SoulLink.exe` 不在 Git 中，回退源码后需重新发布。
- 回退源码**不会**回退 `%LOCALAPPDATA%` 下的用户配置，改配置前单独备份。

近期提交：

| 提交 | 说明 |
| --- | --- |
| 本次 | 热键往返/生效修复 + 自由视角不再影响鼠标落点 |
| `a703c2a` | 通用设置持久化失效 + 滑动条换玻璃风 + 标签重叠 |
| `55c52a7` | 侧栏高度对齐右侧卡片、左右边距对称、底部区贴底 |
| `435f801` | 修复侧栏收起态无圆角/无边距、展开态接缝 |
| `f38d912` | 修复边框未使用系统强调色（COLORREF 格式 + 强调色取值来源） |
| `6ad4676` | 修复窗口顶部额外粉条，边框随激活状态用系统主题色 |
| `4f897f8` | 添加版本管理与安全回退说明 |
| `bac46e8` | 稳定基线 |

---

## 十、已知踩坑速查

| 现象 | 原因 / 处理 |
| --- | --- |
| RDP 画面全黑 | 主窗口被改成分层窗口（`AllowsTransparency=True`） |
| 保存的密码不生效、弹凭据框 | 接口写成 `InterfaceIsDual`；必须 `InterfaceIsIUnknown`（见 4.1） |
| 点击偏移 | DIP 与物理像素混用；用 `PointToScreen` |
| WPF 浮层被 RDP 盖住 | 空域限制，改 `Popup` 或独立窗口 |
| 注销过场挡住别的用户 | 过场缺归属判断，或完成态仍允许 `Show()` |
| 对 Disc 会话 `tsdiscon` 直接注销 | 先判断 `IsDisconnected` |
| 侧栏浅色下仍是深色 | 局部固定背景覆盖主题，需用 `NavBackground` 动态资源 |
| 输入框与下拉框不等高 | 统一 `GlassTextBox` / ComboBox 高度与内边距 |
| 窗口四周粉色粗边 | 不是 `DWMWA_BORDER_COLOR`，是顶部非客户区横条；用 `WM_NCCALCSIZE` 去掉 |
| 边框怎么都不变色 | `DWMWA_BORDER_COLOR` 传了 ARGB；必须 COLORREF（`0x00BBGGRR`，alpha=0），否则 `E_INVALIDARG` 静默失效（4.2.1） |
| 边框色偏暗偏灰 | 用了 `DwmGetColorizationColor`（混合着色）；改读注册表 `DWM\ColorizationColor`（4.2.1） |
| 侧栏点了没反应、弹不出来 | Popup 里对 NavRail 的 `ElementName` 绑定不跨视觉树，高度塌掉；改用代码赋值（4.2.2） |
| 侧栏展开中间有条竖缝 | rail 右边框与 panel 左边框并排；panel 不画左边 + Popup 左移 2px 盖住（4.2.2） |
| 侧栏展开比图标栏高/矮一截 | 漏了 `VerticalOffset = offset.Y`，或高度没跟 `NavRail.ActualHeight` 同步（4.2.2） |
| 侧栏与右侧卡片高度不齐 | rail 上写了 `Margin="...,4,...,16"`；外边距应由外层内容区提供，rail 不再加（4.2.2） |
| 侧栏左右边距不对称 | 外层已有 `Margin(20,...)`，rail 又加了一次左边距 → 40 DIP（4.2.2） |
| 侧栏底部开关/会话卡片悬在中间 | 三类元素塞在 `StackPanel` 里，`Grid.Row` 无效；要放进 `Grid.Row="2"`（4.2.2） |
| 设置保存了下次打开又变回去 | 控件构造期事件把默认值写进配置：`_loading` 必须**初始 true**（4.2.3） |
| 标签和数值叠在一起 | `Grid.ColumnDefinitions` 里忘了写 `Grid.Column`，全挤在第 0 列（4.2.4） |
| 热键改了不生效 | `Loaded` 没调 `ApplyGlobalSettings()`，注册的永远是字段初值（4.2.5） |
| 热键"注册成功"但按了没反应 | 辅助窗口被 `Hide()`，收不到 `WM_HOTKEY`；保持显示挪到屏幕外（4.2.5） |
| 热键配置读回来变成 null | `ToString` 输出中文名但 `Parse` 只认英文，往返不一致（4.2.5） |
| 自由视角下点不中、拖不动 | 按键放行给 RDP，但本地光标钉在中心 → 点击坐标恒为中心（4.2.6） |

---

## 十一、新会话开工建议

1. 先读本文，需要细节再查 `HANDOFF.archive-2026-11-15.md`。
2. 确认以管理员运行，`dotnet build` 通过。
3. 动手改 UI 前先截图当前状态，便于对比。
4. 只碰 A2/A3，测完恢复会话状态并关闭应用。
5. 每项改动实机验证 + 截图后再 `git commit`。
