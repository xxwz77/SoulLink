# 交互体验学习：虚拟摇杆 / 虚拟按键 / 虚拟鼠标 的正确形态

> 调研人：core-fixer ｜ 任务 T-C（Lead 07:00 指令）+ 向日葵录屏 211 帧实测定案
> 日期：2026-11-25 ｜ **纯调研，未改任何代码，未装/未连用户手机**
> 用户三条批评（逐字）：「虚拟按键也挤在一堆，让用户挨个摆位置吗？」「行走键做成按键，手机最常用的是轮盘操作啊」「没看见虚拟鼠标」

---

## 0. 结论摘要（TL;DR）

1. **向日葵的左侧 WASD 是「虚拟摇杆（轮盘）」，不是 4 个独立按键** —— 录屏逐帧证实（见 §2，帧 v0142/v0150/v0152）。一个半透明大圆底座 + W/A/S/D 四个字母标在四正方向 + **摇杆头跟随手指移动** + 推斜角时**两个方向键同时亮**。这正是用户说的「轮盘操作」。
2. **RustDesk 的 floating_mouse 不是影子光标，是一个「可拖动的鼠标形手柄」** —— 展开态 = 一个 80×120 逻辑单位的迷你鼠标（左键/右键/中键滚轮/拖拽区四块，源码确认 :869-1075），收起态 = 一个可拖拽小手柄（7 秒自动收起，:216）。**和「影子光标」（CursorPaint :1189）是两个不同的东西**，我们此前混淆了。
3. **我们的虚拟鼠标没显示，根因不是 SurfaceView 层级**（层级判定见 §4），而是 **visible 门控**：前端只在 `cursorVisible==true` 才画（PlayerPlaceholderScreen.kt:256），而服务端 visible = inside && CURSOR_SHOWING（AgentWebServer.cs:765）—— **3D 游戏全屏时系统隐藏光标 → visible 恒 false → 永远不画**。向日葵的做法是给一个**显式「虚拟鼠标」开关**（v0200 帧实录：红钮 ON），开了就常驻画，与远端是否隐藏光标解耦。
4. **我们的 fps 预设把行走键做成了 4 个散落按键**（ControlsLayout.kt:51-61 k_w/k_a/k_s/k_d），这正是用户骂的第一条。改成**一个摇杆**（模型 VControl.Joystick 已存在但没有任何预设用它，:27/:47 注释自认「供后续预设选用」）。

---

## 一、虚拟摇杆（轮盘）标准实现 —— 参数 + 视觉 + 位置

### 1.1 Moonlight/V+ 源码参数（沿用已有调研，此处只汇总）

出处：MOONLIGHT-VPLUS-ONSCREEN-CONTROLS-20261124.md §1.4/§1.7（源码 AnalogStick.kt 7,173B / ConfigurationLoader 12,440B）+ VIRTUAL-CONTROLS-RESEARCH-20261122.md §4.3。**已核读，无新增修正，以下为汇总**：

| 参数 | 值 | 出处 |
|---|---|---|
| 底座半径 | 控件宽/2（外圈 `radius_complete = w/2 - 描边`） | AnalogStick.java（官方） |
| 摇杆头半径 | **控件宽/2 × 20%** | 同上 |
| 死区半径 | **控件宽/2 × 30%**（= 60% 底座半径） | AnalogStick.kt:45（V+ 同官方） |
| 死区粘滞 | **150ms**（进入 ACTIVE 后回到死区不立即回中） | AnalogStick.kt:158 |
| 双击 | **350ms** 内二次按下 = L3/R3 | AnalogStick.kt |
| 钳制 | knob 拉出不超过 `radius_complete - radius_analog_stick`（不画出圈） | 官方 AnalogStick.java |
| 输出 | 手柄轴 16 位（我们改 8 方向 → WASD keydown/keyup，已有定案） | LeftAnalogStick |
| 透明度 | **全局一个值 0~100，V+ 默认 90**（不是每键独立） | seekbar_osc_opacity |
| 竖屏半高 | `halfHeightOscPortrait`：竖屏控件只占下半屏 | ConfigurationLoader |
| 底部对齐 | `profileOffsetY = layoutHeight - height`（贴下缘） | 同上 |

### 1.2 视觉（Moonlight 源码 + 向日葵录屏互相印证）

- **半透明圆环（底座）+ 内部摇杆头圆点**；全局透明度控制 alpha；
- **按下/推动时摇杆头与方向键高亮**（向日葵录屏：推左下 → A+S 同时变红）；
- V+ `isForceDarkAllowed = false` 防系统深色模式反转控件颜色（VirtualControllerElement 注释原文）。

### 1.3 位置（向日葵录屏实测，v0142 帧测量）

以视频显示区（letterbox 后）为坐标系归一化：

| 项 | 实测值 | 说明 |
|---|---|---|
| 摇杆底座圆心 | **(0.13, 0.73)**（视频区归一化） | 左下角，与我们设计稿 (0.13, 0.78) 几乎一致 |
| 底座直径 | **≈ 0.21 × 视频宽** | 一个大圆包住 W/A/S/D 四个字母 |
| 距左缘 | ≈ 0.03 × 视频宽 | 不贴死边缘，留出误触缓冲 |
| W/A/S/D 字母位置 | 底座内四正方向（上/左/下/右），偏内 | 字母标在**按键热区**上，底座是连续滚动区 |

**对照我们**：MOBILE-CONTROLS-DESIGN-20261122.md §3.3 预设 `joy-move center(0.13,0.78) radius 0.14` —— **位置/半径与向日葵几乎一致，说明设计稿没问题，问题只在没实现成摇杆**。

---

## 二、虚拟按键的合理默认布局（开箱即用，用户最在意）

### 2.1 ★ 向日葵 v0142 帧的布局实测（决定性参照）

以视频显示区归一化（坐标来自逐帧测量，±0.02 误差）：

**左侧（移动）**：
- **一个摇杆**（非 4 键）：底座圆心 (0.13, 0.73)、直径 ≈0.21×宽；
- W/A/S/D 字母标在底座内四正方向；
- 另有一个 Shift 类修饰键圆钮在摇杆左下外侧（≈0.04, 0.87），独立小圆。

**右侧（功能键）—— 排布规律 = 「沿右缘纵向散落 + 角部大键」，不是网格也不是弧形**：

| 键 | 位置（视频区归一化） | 大小（直径/视频宽） |
|---|---|---|
| Esc | (0.97, 0.06) 右上角贴边 | ≈0.05 |
| L | (0.99, 0.16) 右缘 | ≈0.045 |
| Enter | (0.99, 0.28) 右缘 | ≈0.045 |
| 大圆（锁定/交互，带锁图标） | (0.93, 0.42) | ≈0.09 |
| 小功能键 ×4（F/R/C/Shift 类） | (0.85,0.22)/(0.90,0.33)/(0.84,0.44)/(0.79,0.37) 散落 | ≈0.05 |
| Space 大键 | (0.80, 0.78) | ≈0.10 |
| 顶部一排小键 ×6 | y≈0.05，x 从 0.55 到 0.90 均布 | ≈0.04 |
| 左缘纵列（Esc/I/Enter/菜单） | x≈0.02，y 0.06/0.22/0.35/0.45 | ≈0.04 |

**排布规律总结**（回答 Lead 的追加问题）：
- **不是网格、不是弧形，是「边缘锚定 + 功能分组散落」**：高频键（Esc/Enter/锁定）贴屏幕右缘纵列（拇指外侧够得到），技能键散落在右侧拇指活动区内且**互不重叠、大小随重要性分级**（Space 最大 0.10，普通键 0.05）；
- 全部半透明（录屏目测 alpha ≈ 0.3~0.4 常态 / 按下升高）；
- **顶部一排**是「全局快捷键带」（v0180 帧对应展开后的横向条：O / Win+D / M / Alt+F4 / Esc —— 组合键放横条，不占画面键位）。

### 2.2 ★ v0142 之后逐帧：WASD 是摇杆还是按键？（Lead 追加问题的答案）

**判定：摇杆（轮盘），证据三条**：

| 帧 | 观察到的状态 | 说明 |
|---|---|---|
| v0142~v0148 | 静止：一个大圆底座，W/A/S/D 四字母各在一个小热区上，**无高亮** | 底座是一个整体圆，字母只是方向标记 |
| **v0150** | **A 与 S 同时红色高亮**（其余 W/D 暗） | **推斜角（左下）→ 两个方向键同时亮**。4 个独立按键要两个手指才解释得了，而录屏只有一个触点在动；摇杆推左下输出双键是标准实现 |
| **v0152** | **W 高亮 + 一个小圆（摇杆头）移到 W 位置（左上）**，A/S 恢复暗 | **摇杆头跟随手指移动**的直接证据；单键 W 亮 = 推上 |
| v0154~v0158 | 摇杆头回到中央附近，高亮消退 | 松手回中 |

**判定依据（对照 Lead 给的判据）**：摇杆 =「一个底座 + 一个跟随的摇杆头」✅（v0152 可见头在 W 位）；按键 =「4 个独立的圆」❌（四字母共享同一底座圆，无独立按压态）。**斜角双键同亮（v0150）是摇杆 8 方向映射的决定性指纹。**

### 2.3 V+ 的 4 套预设（源码，沿用已有报告 §1.2，只补要点）

XBOX（绿 0xFF8ED269）/ DS（蓝 0xFF83B7FF）/ NS（青 0xFF56CEE5，右侧键红 0xFFFF7C83）/ CLASSIC。
切换时 `releaseInputs()` 先松全部键防粘键（VirtualController.switchLayout）。**布局预设与元素位置分开存**（list_osc_layout / OSC_PREFERENCE）。

### 2.4 我们的 fps 预设怎么改（具体数值）

现状（ControlsLayout.kt:51-61）：k_w/k_a/k_s/k_d 四个散落按钮 —— **用户骂的就是这个**。
改成（沿用 §2.1 实测 + 已有 Joystick 模型，全部归一化）：

```
预设 fps v2（GAME 模式默认）：
  joystick move   anchor(0.13, 0.73)  size(0.21, 0.21)  deadzone 0.30  stickyMs 150
                  keys [W, D, S, A]（上/右/下/左；斜角双键，VirtualControlsLayer 已支持）
  button space    anchor(0.80, 0.78)  size(0.10)         label "空"
  button shift    anchor(0.04, 0.87)  size(0.07)         label "Shift"
  button esc      anchor(0.97, 0.06)  size(0.05)         label "Esc"
  button e        anchor(0.90, 0.33)  size(0.05)         label "E"
  button r        anchor(0.95, 0.24)  size(0.05)         label "R"
  button f        anchor(0.85, 0.44)  size(0.05)         label "F"
  button c        anchor(0.79, 0.37)  size(0.05)         label "C"
  opacity 45（现状）→ 建议 55（向日葵目测更亮一档，且按下会升高）
```

原则（从向日葵提炼）：**高频修饰键贴缘、技能键散落不重叠、大小按重要性分级、行走必须是摇杆**。

---

## 三、虚拟鼠标为什么没显示（根因诊断）

### 3.1 层级判定：**SurfaceView 层级不是根因**（源码确认）

- 我们用 Compose `Box { AndroidView(AspectSurfaceView); TouchGestureLayer; VirtualControlsLayer; Canvas(悬浮球); if(cursorVisible) Canvas(光标) }`（PlayerPlaceholderScreen.kt:144-281）。
- Android 规则：**SurfaceView 默认把它的 surface 合成在 window 后面**（在 view 树上打一个「洞」），**普通 View/Compose 画在 window surface 上 → 永远盖在 SurfaceView 内容之上**。只有调用 `setZOrderOnTop(true)` / `setZOrderMediaOverlay(true)` 才会反转。
- 我们的 AspectSurfaceView.kt **全文没有调用这两个方法**（12 行类，只有 onMeasure）⇒ 层级正确，Compose Canvas 悬浮球/光标**能**画在视频之上（悬浮球本身用户能看到，就是证明）。
- Moonlight 的对照做法（把控件加进 streamView.parent FrameLayout，Game.kt:630-637）是**原生 View 体系**下的层级技巧；Compose 下兄弟顺序即 z 序，我们现状已正确。

### 3.2 真根因：**visible 门控把光标锁死在 false**（源码确认）

链路（三处，逐处核对）：

1. 服务端 `CursorEcho`（AgentWebServer.cs:748-767）：
   `visible = inside && showing`，其中 `showing = GetCursorInfo && (flags & CURSOR_SHOWING) != 0`（:760-761）。
   **3D 游戏全屏时 Windows 系统隐藏光标 → CURSOR_SHOWING=0 → visible=false**（代码注释自己也写了这一点 :754-756）。
2. 前端 `parseCursor`（InputSender.kt:54-64）：把 visible 原样上抛，✅ 无 bug（被调用链 :102/:121 都在）。
3. 绘制门控（PlayerPlaceholderScreen.kt:256）：`if (cursorVisible) { 画箭头 }`，且初值 false（:90）。

**⇒ 游戏内光标恒隐藏 ⇒ visible 恒 false ⇒ 我们的箭头永远不画。用户「没看见虚拟鼠标」完全成立。**
另外两个加重因素：
- 回显只挂在 `/api/input` 响应上（AgentWebServer.cs:727），**不发输入就没有新回显**，静止时永远停在旧状态；
- Lead 改的 CURSOR_SHOWING 检查（T41e）让「游戏内」更快变 false —— 这对「跟谁真光标」是对的，但对「虚拟鼠标要始终可见」是反的。

### 3.3 向日葵的答案（v0200 帧实录）

向日葵设置面板有一个**显式「虚拟鼠标」开关（默认 ON，红钮）** + 「指针模式 / 触控模式」二选一 + 「锁定显示」。**开了虚拟鼠标就常驻画影子光标，与远端系统是否隐藏光标完全解耦** —— 远端游戏里光标隐藏时，它照样画（因为这是「你的手指在哪」指示器，不是「远端光标在哪」复制品）。
v0142~v0158 各帧均可见半透明白色箭头影子光标跟随操作移动，按下变蓝（v0160 帧按下态蓝圈）—— 与我们 T41g 的箭头设计一致，**我们只差把门控改对**。

---

## 四、★ floating_mouse 的真实形态（此前确实理解错了）

**源码定案（rustdesk flutter/lib/mobile/widgets/floating_mouse.dart，1218 行全文）**：
floating_mouse **不是影子光标**，是一个「**可拖动、可展开/收起的迷你鼠标手柄**」：

| 形态 | 内容 | 源码 |
|---|---|---|
| **展开态** | 一个 80×120 逻辑单位的**迷你鼠标**：上半是左键+右键两块（按下变蓝 :897-985），中间一条 22×67 的**滚轮条**（上/中/下三段 :1005-1013），下半是**拖拽区**（按住它移动 = 移动整个手柄，:1024-1068 drag_indicator 图标） | MouseBody.build :869-1075 |
| **收起态** | 7 秒无操作自动收起（Timer :216），缩成一个小手柄（:654-668），点它再展开 | _collapseTimer |
| **拖动** | 手柄可拖到任意位置（_onDragHandleUpdate :503） | 同上 |
| **影子光标** | **另一个独立组件 CursorPaint（:1189）**，画在画布上跟随真实指针 | 分开的类 |

**三个产品、三种东西，一次分清**：

| 东西 | 是什么 | 向日葵 | RustDesk | 我们现状 |
|---|---|---|---|---|
| **影子光标** | 画面上跟随远端指针位置的箭头（指示器） | ✅ 常驻（开关控制） | ✅ CursorPaint | ✅ 已画（箭头），但被 visible 门控锁死 |
| **虚拟鼠标手柄** | 可拖动的迷你鼠标（左/右/滚轮按钮，按了发输入） | ❌ 无（指针模式直接触屏） | ✅ floating_mouse | ❌ 没有 |
| **虚拟摇杆** | 行走轮盘 | ✅ 左下 WASD 轮盘 | ❌ 无 | 模型有、预设没用 |

我们 T41g 把「8 秒收起」当成影子光标的规则删掉了 —— **删对了**（收起的是手柄不是光标），但**把该学的手柄形态整个漏掉了**。

---

## 五、我们该怎么改（具体到数值，不改代码，给排期依据）

### A 档（修骂点，半天~1 天）
| # | 改动 | 数值 | 依据 |
|---|---|---|---|
| A1 | **fps/game3d 预设：删 4 个 WASD 按钮，换成 1 个摇杆** | anchor(0.13,0.73) size(0.21) deadzone 0.30 sticky 150ms keys[W,D,S,A] | §2.1 向日葵实测 + VControl.Joystick 模型现成 |
| A2 | **虚拟鼠标常驻**：删 `if (cursorVisible)` 门控（或改 `cursorVisible || 触控模式开关`），visible=false 时画**半透明白箭头**（alpha 0.5），visible=true 时画实箭头 | 常态 0xB3E0E0E0 / 按下 0xB32196F3（现有配色保留） | §3.2/§3.3：与远端光标可见性解耦（向日葵开关模式） |
| A3 | 静止回显：无输入响应时按最后已知 nx/ny 持续画（已有状态，无需新请求） | — | §3.2 加重因素 |

### B 档（对标手柄形态，2~3 天）
| # | 改动 | 数值 | 依据 |
|---|---|---|---|
| B1 | 虚拟鼠标**手柄**（RustDesk 形态）：展开 80×120dp（左/右键+滚轮条+拖拽区），7s 收起成小手柄，可拖动 | 收起手柄 ≈48dp 圆 | §四 源码 |
| B2 | 预设切换入口（fps/game3d/desktop）+ 布局 JSON 导入导出 | V+ 4 预设思想 | ControlsLayout.fromJson 已有 |
| B3 | 顶部快捷键横条（Win+D/Alt+F4/Esc 类组合键），默认收起 | v0180 帧 | §2.1 |

### C 档（手感，可选）
触感反馈（V+ performClickHaptic 三级降级）、双击摇杆=L3/R3 等价物（双击=定位键）、分享码。

---

## 六、证据清单（每条的出处）

| 结论 | 级别 | 出处 |
|---|---|---|
| 向日葵左侧 WASD = 摇杆（斜角双键同亮 + 摇杆头跟随） | **录屏逐帧实测** | v0142/v0150/v0152 帧（%TEMP%\sl-video） |
| 向日葵右侧键 = 边缘锚定散落、大小分级 | **录屏实测** | v0142 帧坐标测量 |
| 向日葵有「虚拟鼠标」显式开关（ON） | **录屏实测** | v0200 帧 |
| 向日葵游戏键盘入口 4 标签页 | **录屏实测** | v0134 帧 |
| 向日葵底部快捷键横条（O/Win+D/M/Alt+F4/Esc） | **录屏实测** | v0180 帧 |
| RustDesk floating_mouse = 可拖动迷你鼠标手柄（左/右/滚轮/拖拽区），7s 收起 | **源码确认** | floating_mouse.dart:869-1075/:216/:654-668 |
| CursorPaint 与 floating_mouse 是两个组件 | **源码确认** | floating_mouse.dart:1189 |
| 摇杆三半径/死区 30%/粘滞 150ms/双击 350ms | **源码确认**（已有报告汇总） | AnalogStick.kt:45/158（V+） |
| SurfaceView 默认在 window 后、Compose 覆盖其上；我们未设 ZOrder | **源码确认** | AspectSurfaceView.kt 全文（无 setZOrder*） |
| 我们的根因 = visible 门控 + CURSOR_SHOWING | **源码确认** | AgentWebServer.cs:748-767、PlayerPlaceholderScreen.kt:256/:90、InputSender.kt:54-64 |
| 我们的 fps 预设行走键 = 4 按钮（被骂点） | **源码确认** | ControlsLayout.kt:51-61 |
| VControl.Joystick 模型存在但无预设使用 | **源码确认** | ControlsLayout.kt:27/:47/:51-88 |
| Moonlight toggleOverlay 方法体 | 未取到（Game.java 超 100KB 上限，沿用 MOBILE-UX-BENCH 标注） | — |
| Parsec/ToDesk 悬浮球细节 | [推测]（闭源） | — |

---

## 七、与已有文档的关系
- MOONLIGHT-VPLUS-ONSCREEN-CONTROLS-20261124.md：V+ 控件源码（本文 §1.1 只汇总其摇杆参数）；
- VIRTUAL-CONTROLS-RESEARCH-20261122.md：官方 OSC 拆解 + 摇杆数学（§4.3 三半径/死区粘滞）；
- MOBILE-CONTROLS-DESIGN-20261122.md：摇杆 8 方向数学 + JSON Schema（§3.3 预设位置与向日葵实测吻合）；
- MOBILE-UI-UX-BENCH-20261125.md：播放页零 UI / 工具栏 / 手势矩阵（本篇是其控件层的深化）。
- **本文唯一增量 = 向日葵 211 帧实测 + floating_mouse 形态定案 + 虚拟鼠标没显示的根因链。**

*未改任何代码；未装/未连用户手机；读图均为 %TEMP%\sl-video 下 captain 提供的帧。*
