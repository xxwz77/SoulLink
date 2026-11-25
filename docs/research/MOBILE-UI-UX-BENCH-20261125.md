# 手机串流/远控「播放页」界面与交互逻辑 对标调研

> 调研人：core-fixer ｜ 任务 t34（T42） ｜ attempt `8d6beb9c-cd65-4745-8dd1-c615b4edb84a`
> 日期：2026-11-25 ｜ **纯调研，未改任何代码，未连接用户手机**
> 用户原话（逐字）：「去查一下别的软件的界面，交互逻辑吧，**你做的像个网页**」
> 数据来源：Moonlight Android / RustDesk 均**开源可读**（通过 GitHub Contents API 取到源码原文）；Parsec / Steam Link / ToDesk / 向日葵 / Xbox 为闭源，只能凭官方特性页 + 通用印象，标 [推测]。

---

## 0. 先回答：用户说「像个网页」是什么意思

**理解正确**。用户说的不是技术，是**产品形态**。我们的播放页 = 常驻顶栏（返回/账号/状态/模式/方向）+ padding(8.dp)，是「网页式」常驻 UI；专业软件播放页 = **默认零 UI、画面铺满、手势驱动、控件轻触浮出 + 数秒自动隐藏**。

本报告只谈**界面与交互**，不重复已写过的技术项（全屏五层见 MOBILE-UX-BENCH-20261125.md，Android 技术自审见 MOBILE-SELF-AUDIT-20261125.md，二者与本报告同属 t31/t33 系列）。

---

## 一、播放页 UI 形态（逐产品）

### ① Moonlight Android（源码确认）

**常驻元素 = 几乎为 0**。串流播放页 activity_game.xml 整棵布局只有 5 个节点：

    <merge>
      <View id=backgroundTouchView  match_parent/>   <!-- 透明触摸接收层，吃空白区触摸转鼠标 -->
      <StreamView id=surfaceView  match_parent/>      <!-- 视频画面 -->
      <TextView id=performanceOverlay visibility="gone"/>  <!-- 左上角，性能/延迟数字，默认隐藏 -->
      <TextView id=notificationOverlay visibility="gone"/> <!-- 右上角，通知/断开提示，默认隐藏 -->
    </merge>

出处：app/src/main/res/layout/activity_game.xml（GitHub Contents API，size=1905B，全文）。

**结论（源码确认）**：
- 播放中屏幕上的**常驻控件 = 0**。两个 TextView 默认 visibility="gone"，只有需要时才显示；
- performanceOverlay 在**左上角**、margin=10dp、半透明黑底 #80000000、visibility="gone" —— **诊断信息是「按需点出来的小字」，不是常驻**；
- notificationOverlay 在**右上角**（断开/重连提示）；
- 顶栏/底栏**没有**。全屏沉浸式（Game.java:190-206、:993-1000，见 MOBILE-UX-BENCH-20261125.md §1.1）；
- 虚拟按键/摇杆（On-Screen Controls）是**按用户显式开启**才叠加的透明控件，不是默认就有（见 MOONLIGHT-VPLUS-ONSCREEN-CONTROLS-20261124.md）。

**关于 toggleOverlay / hideSystemUi 调用时机**：官方 Game.java（119,760B）超过本环境 100KB 抓取上限，全文未取到；但布局 XML + 已有 MOBILE-UX-BENCH-20261125.md 的 Game.java:190-236 / 764-782 / 935-960 / 993-1000 已覆盖沉浸式与 letterbox 调用时机。**虚拟按键的 toggle 触发方式（三指点按屏幕中央唤起/隐藏）为 Moonlight 官方文档描述的交互，本轮未在源码定位到具体 toggleOverlay 方法体，标 [推测]。**

### ② Parsec / Steam Link / Xbox Game Pass（[推测]，闭源）

- **Parsec**：手机端播放页**默认全屏零 UI**；轻点屏幕浮出半透明顶栏（返回 + 连接名 + 菜单），几秒无操作淡出。有**连接质量浮层**：左下/左上角小字显示延迟/帧率/码率，可从菜单开关（Parsec 以「低延迟 + 稳定」为核心卖点，其官网特性页 parsec.app/features 强调 sub-frame latency，但具体 UI 布局未公开，[推测]）。
- **Steam Link**：手机端默认全屏画面 + 底部一行**半透明触控条**（可收起）；顶栏有返回/设置/手柄映射入口。连接质量角标在左上（bitrate 波动时有图标提示），[推测]。
- **Xbox Game Pass 云串流**：全屏 + 右上角悬浮「⋯」菜单；断线时中央大提示。触控游戏有专为触屏设计的屏幕控件叠加层（这是微软明示的功能），[推测]。

### ③ ToDesk / 向日葵 / RustDesk（RustDesk 源码确认，ToDesk/向日葵 [推测]）

**RustDesk（源码确认）** 是三者里与我们场景最接近的（远控桌面），其播放页结构（flutter/lib/mobile/pages/remote_page.dart，57KB 全文）：

- **默认 _showBar = !isWebDesktop（:92）** —— 连接建立后**底栏是收起的**（网页桌面端才常驻）；
- 画面铺满，body = Overlay + 视频 ImagePaint；**右下角悬浮一个 FAB**（:507-531，expand_less/expand_more 箭头），**点它才展开底栏**（_showBar = !_showBar）；
- **底栏展开后**（getBottomAppBar，:605-702）是一排图标：断开(×)、显示选项(tv)、键盘(keyboard)、触控/鼠标模式切换(touch_app/mouse，:654-658)、语音/聊天、更多(⋮)；最右 expand_more 收起；
- **「更多」展开的菜单**（common/widgets/toolbar.dart，55KB）含：**刷新 / 图像质量(最佳/均衡/低) / 编解码器(Auto/VP8/VP9/AV1/H264/H265) / 显示切换 / 质量监视器开关 / 隐私模式 / 锁定 / 重启远端 / 请求提权**（:103-150 常量表）；
- **质量监视器 QualityMonitor 固定在画面右上角**（remote_page.dart:715-719 Positioned(top:10, right:10)），显示延迟/fps/码率 —— **角落小字，可开关**，不是常驻大段文字；
- **悬浮球**：RustDesk 没有传统「球」，但有**「虚拟鼠标」**（见 §二③）和一个常驻 FAB。ToDesk/向日葵则有**真悬浮球**（可拖动、点开是快捷菜单：键盘/鼠标模式/断开等），[推测]。

**ToDesk / 向日葵**（[推测]，闭源，未能取到布局代码/截图）：
- 手机端播放页顶部一条**半透明工具栏**（返回/设备名/更多），底部一条**快捷工具条**（键盘/鼠标模式/常用快捷键/断开），通常**默认收起为一个小悬浮球或侧边条**；
- 悬浮球可拖动，点开二级菜单；
- 手势普遍支持：单击=左键、长按=右键、双指捏合=缩放/滚动、双指拖=平移、三指=滚轮/拖动。

### ④ 视频播放器惯例（YouTube / B站 / Netflix，[推测] 通用惯例）

- **UI 自动隐藏 3~5 秒**：全屏播放时轻点浮出控件，约 3~5 秒无操作淡出；
- **全屏时保留**：播放/暂停、进度条、返回、更多；**隐藏**：标题、评论、推荐列表等非当前播放必需信息；
- 隐藏时**画面不响应控件触摸**（控件已消失），仅保留手势（双击暂停等）与边缘返回。

---

## 二、交互逻辑（重点）

### ① 手势清单矩阵

**RustDesk（源码确认，flutter/lib/mobile/widgets/gesture_help.dart:284-345）** —— 它把手势帮助页做成**触控模式 / 鼠标模式两栏对照**：

| 手势 | RustDesk 鼠标模式 | RustDesk 触控模式 | Moonlight（官方，见 MOBILE-UX-BENCH §触摸参数） | Parsec / ToDesk / 向日葵 [推测] |
|---|---|---|---|---|
| 单指点按 | **左键** | **左键** | 绝对/相对模式点击=左键 | 左键 |
| 单指拖动 | **鼠标拖动**（One-Finger Move） | 拖动 | 相对模式=移动视角 | 拖动/画框 |
| 单指长按 | **右键**（One-Long Tap） | **右键** | LONG_PRESS=650ms=右键/长按 | 右键 |
| 双指点按 | （未单列） | （未单列） | 未单列 | 右键（常见） |
| 双指拖动 | **画布平移**（Two-Finger Move → Canvas Move） | 画布平移 | 缩放/平移 | 滚动 |
| 双指捏合 | **画布缩放**（Pinch to Zoom） | 画布缩放 | 缩放 | 缩放 |
| 三指 | **滚轮**（Three-Finger vertically → Mouse Wheel） | 滚轮 | 未单列 | 滚轮/拖动 |
| 边缘内滑 | （未单列） | （未单列） | 系统返回手势 | 呼出工具栏 |
| 双击 | 触控模式下 Double Tap & Move = 拖动（:328-330） | 同左 | DOUBLE_TAP=250ms（双击） | 双击=左键双击 |

**关键区分（RustDesk 源码确认，gesture_help.dart:98-119）**：它有 **「鼠标模式」与「触控模式」两套手势语义**，用户可在工具栏切换。鼠标模式 One-Finger Move=拖动；触控模式 Double Tap & Move=拖动（单指直接拖是另一种语义）。**这正是我们落点偏移问题的成熟解法：两种模式分开，不硬塞一套。**

**三指手势**：RustDesk common/widgets/gestures.dart 有完整的 threeFingerVerticalDrag 状态机（:10-116），映射到**滚轮**（三指上下滑=鼠标滚轮）。

### ② 工具栏出现/隐藏规则

**RustDesk（源码确认）**：
- **触发**：右下角悬浮 FAB 点按（remote_page.dart:518-528）或底栏末端的 expand_more（:696）切换 _showBar；**不是**点屏幕任意处、也不是三指下滑；
- **隐藏**：再点同一按钮收起；**无自动隐藏定时器**（_showBar 是持久状态，收起靠手动）——但**默认就是收起**，所以「常驻」不会发生；
- **隐藏时**：底栏 Offstage()（:489）完全不渲染；但 FAB **仍显示**（:495 showActionButton = !_showBar || keyboardIsVisible || _showGestureHelp），所以收起时仍有一个触控点。

**Moonlight（[推测] + 部分源码）**：无工具栏；虚拟按键靠**三指点按屏幕中央**唤起/隐藏（官方文档描述，源码方法体未定位，标 [推测]）；沉浸式全屏里「轻触边缘」会带出系统导航栏（系统行为）。

**视频播放器惯例（[推测]）**：点屏幕中央浮出、3~5 秒自动隐藏、隐藏时控件不响应触摸。

### ③ 「虚拟鼠标」的视觉呈现

**RustDesk（源码确认，flutter/lib/mobile/widgets/floating_mouse.dart，41KB）** 是三者里最明确的：

- **有影子光标**（FloatingMouse），默认灰 Colors.grey.withOpacity(0.7)，按下高亮蓝 Colors.blue.withOpacity(0.7)（:14-16）；
- **与远端光标关系**：它是**本地影子**，代表「手指当前映射到的屏幕位置」；远端的真光标是另一回事。showCursorPaint（remote_page.dart:704-707）在「非安卓远端 + 光标未内嵌 + 非相对鼠标模式」时才画本地光标 —— 即**远端光标内嵌在画面里时，本地不重复画影子**；
- **自动折叠**：悬浮鼠标展开后有 **7 秒定时器**（floating_mouse.dart:216 Timer(Duration(seconds: 7))）自动收成一个可拖拽的小手柄（:654-668），点它再展开 —— **「悬浮球/悬浮鼠标 + 自动收起」就是远控软件的影子光标惯例**；
- 有「显示虚拟鼠标 / 虚拟鼠标大小(小/大) / 显示虚拟摇杆 / 相对鼠标模式」等开关（gesture_help.dart:130-270）。

**ToDesk/向日葵（[推测]）**：手机触屏模式下**有本地光标**（影子），拖到哪里远端鼠标点哪里；鼠标模式下手机变成触控板，光标移动用「单指拖动=移动光标、轻点=点击」。

### ④ 键盘怎么出

**RustDesk（源码确认）**：
- **软键盘由工具栏的键盘按钮触发**（remote_page.dart:651 openKeyboard），不是自动弹；
- 有 KeyboardVisibilityController 监听软键盘显隐（:101、:286-315），弹键盘时 **FAB 上移**（floatingActionButtonLocation 随 keyboard 调整 :504-505）、并把系统栏设为 SystemUiMode.manual, overlays: []（:288）——即**键盘弹出时画面区域缩小、控件避让**，不遮挡关键按钮；
- 物理键盘与软键盘有显式 gate（:35-39、Wayland 场景 :239-267）。

**通用（[推测]）**：远控软件弹软键盘通常**由「键盘」按钮或点击远端输入框**触发；弹出时画面**压缩/避让**而非被完全遮挡。

### ⑤ 状态与诊断信息放哪

- **RustDesk（源码确认）**：延迟/fps/码率放在**画面右上角 QualityMonitor**（remote_page.dart:715-719，Positioned(top:10, right:10)），半透明、小字、可开关（toolbar 的 kShortcutActionToggleQualityMonitor）；**没有任何「状态：PLAYING」「账号：XX」常驻文字** —— 连接状态只在断开时以 overlay 提示。
- **Moonlight（源码确认）**：performanceOverlay 左上角、visibility="gone" 默认隐藏，**按需开关**；notificationOverlay 右上角，断开/重连时才显示。
- **结论（源码确认，直接回答任务问的）**：专业软件**没有**「状态：PLAYING」「账号：A2-山客」这类常驻大字。诊断信息是**角落半透明小字 + 按需开关**；账号/设备名只在**非播放页**（列表页）出现，播放页里至多一个可隐藏的顶栏小字。

---

## 三、可落地的 UI 方案（给设计，不给代码）

### ① 播放页默认状态

- **零常驻 UI**：画面铺满全屏（结合 MOBILE-UX-BENCH-20261125.md 的全屏五层：沉浸式 + 铺满刘海 + 拉伸/letterbox 选项 + aspect 布局），**不显示**顶栏/状态/模式/方向按钮；
- 唯一常驻 = **一个可拖拽的半透明悬浮球/悬浮鼠标**（右下角，RustDesk 式），作为「唤起一切」的入口；
- 连接建立后**不显示**「状态：PLAYING」；失败/断开时用**右上角半透明小字**短暂提示（Moonlight notificationOverlay 式），几秒后消失。

### ② 工具栏

- **触发**：点悬浮球（展开/收起）；**默认收起**；展开后**无操作 5~7 秒自动收起**（RustDesk 悬浮鼠标是 7s）；
- **收起时**：工具栏完全不渲染，但悬浮球仍在，避免「无入口」；
- **包含按钮**（按我们的远控场景裁剪）：断开(×)、**鼠标/触控模式切换**（关键）、键盘、显示选项（画质/码率/刷新）、更多（锁定/隐私/重启/提权）；
- **排布**：底部一排（RustDesk BottomAppBar 式），或半透明顶栏（ToDesk 式）——建议**底部一排 + 右上角质量角标**，拇指可达性好。

### ③ 手势清单（我们该支持哪些）

直接对标 RustDesk 的「鼠标/触控双模式」+ Moonlight 参数：

- 单指点按 = 左键；
- 单指长按（650ms）= 右键；
- 单指拖动 = 移动/拖动（鼠标模式）；双击后拖动 = 拖动（触控模式，避免和单指点击冲突）；
- 双指拖动 = 画布平移；双指捏合 = 缩放；
- 三指上下滑 = 滚轮；
- **关键**：绝对/相对鼠标作为**用户可切换的模式**，不硬塞一套（Moonlight DEFAULT_ABSOLUTE_MOUSE_MODE=false 默认相对；RustDesk 两模式分栏）。

### ④ 状态信息放哪

- 延迟/fps/码率 → **右上角半透明小字**（RustDesk QualityMonitor 式），默认**可关**，点开才有；
- 连接断开 → 右上角 notificationOverlay 式短暂提示；
- **删掉**「状态：PLAYING」「账号：A2-山客」常驻文字（账号在列表页已有，播放页不需要）。

### ⑤ 「从左到右、从上到下」界面结构

    ┌─────────────────────────────┐
    │ (全屏画面，铺满到刘海下)          │
    │                               │
    │  [左上：无]  [右上：质量角标小字]   │   ← 半透明、可关、无操作不显示
    │                               │
    │                               │
    │              [悬浮球 · 右下角]    │   ← 唯一常驻，可拖动，点开工具栏
    │                               │
    │  (工具栏：默认收起)              │
    │  ┌─────────────────────────┐  │
    │  │ 断开 × │ 模式🖱/👆 │ 键盘 │ ⋮ │  │   ← 底部一排，展开后 5~7s 自动收起
    │  └─────────────────────────┘  │
    └─────────────────────────────┘

断开/失败时：右上角短暂「已断开 · 点此重连」；点悬浮球 → 展开工具栏 → 「更多」里是画质/码率/锁定/隐私/重启。

---

## 四、诚实标注（哪些是源码、哪些是推测、哪些没取到）

| 结论 | 级别 | 出处 |
|---|---|---|
| Moonlight 播放页常驻控件=0，仅两个 visibility="gone" 的 overlay | **源码确认** | activity_game.xml（GitHub Contents API 全文） |
| Moonlight 性能数字在左上角、通知在右上角、默认隐藏 | **源码确认** | 同上 performanceOverlay/notificationOverlay 属性 |
| RustDesk 默认底栏收起、右下 FAB 唤起、底栏按钮清单、质量监视器右上角 | **源码确认** | remote_page.dart:92/485-702/715-719 |
| RustDesk 手势矩阵（单击=左键/长按=右键/双指=平移/捏合=缩放/三指=滚轮/双击拖动） | **源码确认** | gesture_help.dart:284-345 |
| RustDesk 三指=滚轮状态机 | **源码确认** | gestures.dart:10-116 |
| RustDesk 影子光标（默认灰、按下蓝、7s 自动收起） | **源码确认** | floating_mouse.dart:14-16/216/654-668 |
| RustDesk 软键盘由工具栏按钮触发、弹出时画面避让 | **源码确认** | remote_page.dart:651/286-315/504-505 |
| RustDesk「更多」菜单项（画质/编解码/刷新/隐私/锁定/重启/提权） | **源码确认** | toolbar.dart:103-150 常量表 |
| Moonlight toggleOverlay/hideSystemUi 精确调用时机 | **未取到**（Game.java 119KB 超抓取上限） | 布局 XML + 已有 MOBILE-UX-BENCH 的 Game.java:190-236/993-1000 部分覆盖 |
| Parsec / Steam Link / Xbox / ToDesk / 向日葵 具体 UI | **[推测]** | 闭源，仅官方特性页 + 通用印象 |
| 视频播放器 3~5 秒自动隐藏 | **[推测]** | 通用惯例 |
| RustDesk「虚拟鼠标 vs 远端光标」精确重合关系 | **部分源码** | showCursorPaint（远端光标内嵌则不画本地影子）逻辑有源码，具体像素重合关系未实测 |

---

## 五、与现有文档的关系

- **本报告 = 界面/交互层**（默认零 UI / 工具栏收起 / 手势矩阵 / 影子光标 / 键盘 / 诊断位置）；
- MOBILE-UX-BENCH-20261125.md = **全屏技术层**（沉浸式五层 / 刘海 / stretch / letterbox）+ Moonlight 触摸参数全表（同系列，数据同源）；
- MOBILE-SELF-AUDIT-20261125.md = **Android 技术自审**（15 项缺陷 + 一行修复清单）；
- MOONLIGHT-VPLUS-ONSCREEN-CONTROLS-20261124.md = **虚拟按键/屏幕控件**（V+ 源码级）；
- 四篇合起来才完整回答「像个网页」：**技术（全屏/输入）+ 界面（零 UI/收起）+ 交互（手势/影子光标）三件事都要改，缺一不可。**

---

*未改任何代码；未连接用户手机；未部署；GitHub 源码通过 Contents API 读取（raw.githubusercontent.com 本环境不可达）。临时文件均未落盘。*
