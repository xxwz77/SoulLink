# 交互标准研究：单击 / 呼出 / 手势（源码级）

> 调研人：yomori-scout ｜ 2026-11-25 ｜ **未改代码 ｜ 未连手机**

> **结论先行**：船长犯的错是**行业里几乎没有反例的错** —— **单击画面必须 100% 是远端鼠标左键**。
> 我查的两个开源实现（Moonlight、RustDesk）**无一例外**。

---

## 一、单击映射的行业共识（含反例搜索）

### 1.1 Moonlight（源码确认）

单击 = 远端左键，走 `TouchContext.touchUpEvent()`（`Game.java:2049-2075`）：
```java
switch (event.getActionMasked()) {
case MotionEvent.ACTION_DOWN:
    context.touchDownEvent(eventX, eventY, event.getEventTime(), true);
    break;
case MotionEvent.ACTION_UP:
    // ... 三指判定（见 §二）...
    context.touchUpEvent(eventX, eventY, event.getEventTime());   // ← 单击走这里
}
```

`RelativeTouchContext.touchUpEvent` 的 tap 实现（`RelativeTouchContext.java:200-220`）：
```java
if (confirmedDrag) {
    conn.sendMouseButtonUp(buttonIndex);      // 拖动结束：抬键
}
else if (isTap(eventTime)) {
    conn.sendMouseButtonDown(buttonIndex);    // ← 单击 = 按下
    // Release the mouse button in 100ms to allow for apps that use polling
    handler.postDelayed(buttonUpRunnable, 100);   // ← 100ms 后抬起
}
```

**★ 注意这个 100ms**：源码注释说「allow for apps that use polling to detect mouse button presses」——
**某些应用靠轮询检测按键，按下立刻抬起会被漏掉**。这是「单击」实现的关键细节（不是简单 down+up 连发）。

### 1.2 RustDesk（源码确认）

`remote_page.dart` **1599 行里没有任何「单击画面呼出 UI」的绑定**；
所有 `onTap:` 都在**底部工具栏的按钮**上（`L1306`、`L1457`、`L1469`、`L1481`）；
画面区域的单击交给手势识别器 → 输入模型（`input_model.dart:28, 157-158` 的 `MouseButtons.left`）。

### 1.3 反例搜索：**没有找到**

**我查的两个实现，没有一个把「单击画面」绑成 UI 呼出。**

**[推测]** 原因显而易见：远控/串流的核心用例就是**点东西**；
单击呼出 UI ⇒ 每次想点东西都先弹菜单 ⇒ 完全不可用。**这正是我们现在的灾难。**

### 1.4 结论

**单击 = 远端左键，无例外。** 这条不需要再讨论。

---

## 二、UI 呼出方式对比

### 2.1 Moonlight：三指轻点呼出键盘（源码确认）

这是 Moonlight 唯一的「手势呼出 UI」（`Game.java:2020-2032` + `:2058-2068`）：
```java
// 三指按下：记录时间，并取消前两指的触摸（防误触）
if (event.getActionMasked() == MotionEvent.ACTION_POINTER_DOWN &&
        event.getPointerCount() == 3) {
    threeFingerDownTime = event.getEventTime();
    // Cancel the first and second touches to avoid erroneous events
    for (TouchContext aTouchContext : touchContextMap) {
        aTouchContext.cancelTouch();
    }
    return true;
}

// 抬起时：若 三指按下→抬起 < 300ms → 呼出键盘
if (event.getEventTime() - threeFingerDownTime < THREE_FINGER_TAP_THRESHOLD) {
    toggleKeyboard();
    return true;
}
```
常量：`private static final int THREE_FINGER_TAP_THRESHOLD = 300;`（`Game.java:110`）

**关键设计**：三指按下时**主动 cancel 前两指**（`L2026-2030`），
**否则三指点击会先触发一次误触**（前两指已经按下了）。**这是必须学的细节。**

### 2.2 Moonlight：输入抓取/光标/退出 用物理键组合

`Game.java:1259-1276`（源码确认）：`Ctrl+Alt+Shift` + 某键：

| 组合 | 动作 | 出处 |
|---|---|---|
| `Ctrl+Alt+Shift+Z` | 切换输入抓取（延迟 250ms） | `:1224-1229` |
| `Ctrl+Alt+Shift+Q` | 退出 | `:1232-1233` |
| `Ctrl+Alt+Shift+C` | 切换光标可见性 | `:1237-1246` |

**⇒ Moonlight 用「键盘组合」而不是「屏幕悬浮按钮」** —— [推测] 因为游戏串流屏幕上任何常驻按钮都会遮挡画面。

### 2.3 RustDesk：工具栏默认显示，用箭头按钮折叠（源码确认）

```dart
// remote_page.dart:92
bool _showBar = !isWebDesktop;      // ← 移动端默认 true（显示）
```

切换入口**只有一个 `IconButton`**（`remote_page.dart:690-698`）：
```dart
Obx(() => IconButton(
      color: Colors.white,
      icon: Icon(Icons.expand_more),      // ← 一个「向下箭头」按钮
      onPressed: gFFI.ffiModel.waitForFirstImage.isTrue
          ? null                          // ← 首帧未到时禁用
          : () { setState(() => _showBar = !_showBar); },
    )),
```

**⇒ RustDesk 的工具栏呼出 = 点一个具体按钮，不是滑手势、不是点画面。**

### 2.4 呼出方式对比表

| 产品 | UI 呼出方式 | 单击画面 | 出处 |
|---|---|---|---|
| **Moonlight** | **三指轻点 <300ms**（只呼键盘）；抓取用 `Ctrl+Alt+Shift+Z` | **远端左键** | `Game.java:110, 2020-2032, 2058-2068, 1222-1246` |
| **RustDesk** | **点 `Icons.expand_more` 按钮**切换 `_showBar`；默认**显示** | **远端左键** | `remote_page.dart:92, 690-698`；`input_model.dart:28-32` |
| **scrcpy** | `Ctrl+o` 等键盘快捷键（桌面端） | 远端左键 | ⚠️ **本轮未取到源码，未验证** |

**⇒ 两种方案按场景分：**
· **游戏场景（Moonlight）**：**手势（三指）**——屏幕不能有常驻 UI；
· **桌面场景（RustDesk）**：**具体按钮**——可以有常驻工具栏。

---

## 三、工具栏：常驻 vs 可隐藏

| 产品 | 默认 | 遮挡怎么解决 |
|---|---|---|
| **Moonlight** | **无常驻工具栏**（只有可选性能 overlay） | 屏幕全留给游戏；操作靠手势/键组合 |
| **RustDesk** | **常驻**（`_showBar = !isWebDesktop`） | **可折叠**（箭头按钮）；另有 `tryShowAndroidActionsOverlay()`（`remote_page.dart:539`） |

### 我的判断（针对我们）

**我们是「桌面操控」为主，不是纯游戏** ⇒ **应该常驻**（学 RustDesk），但必须满足三条：
1. **可一键折叠**（明确的折叠按钮）；
2. **折叠后完全不吃触摸**（不能残留透明层吞事件）；
3. **半透明 + 靠边**（不压画面中心）。

**但用户抱怨「点击屏幕出来个菜单」说明入口设计错了** ——
**入口不该是「单击画面」，应该是「点那个折叠按钮」。**

---

## 四、手势全集表

### 4.1 Moonlight（源码确认）

| 手势 | 映射 | 出处 |
|---|---|---|
| **单击** | **远端左键**（down + 100ms 后 up） | `RelativeTouchContext.java:200-220` |
| 单击（第 2 指） | 远端**右键** | `RelativeTouchContext.java:172-178` |
| 长按 650ms | 进入**拖动**（按下左键） | `RelativeTouchContext.java:88, 46-47` |
| 拖动（已确认） | `sendMouseMove(dx,dy)` 或绝对定位 | `:230-247` |
| **双指** | **滚动**（`SCROLL_SPEED_FACTOR=5`） | `:83, :212-215` |
| **三指轻点 <300ms** | **呼出键盘** | `Game.java:110, 2063-2066` |
| 三指按下瞬间 | **取消前两指**（防误触） | `Game.java:2026-2030` |
| `Ctrl+Alt+Shift+Z` | 切换输入抓取 | `Game.java:1224-1229` |
| `Ctrl+Alt+Shift+C` | 切换光标可见 | `Game.java:1237-1246` |
| `Ctrl+Alt+Shift+Q` | 退出 | `Game.java:1232-1233` |

**绝对模式额外手势**（`AbsoluteTouchContext.java:58-67`）：

| 手势 | 映射 |
|---|---|
| 长按 650ms（位移 <30px） | 右键 |
| 双击 250ms 内（位移 <60px） | 左键双击 |
| 按下后 100ms 内移动 >20px | **不发点击**（死区，防误点） |
| 双指 | 滚动（`SCROLL_SPEED_FACTOR=3`） |

### 4.2 RustDesk（源码确认，部分）

| 手势 | 映射 | 出处 |
|---|---|---|
| 单击 | 远端左键 | `input_model.dart:28, 157-158` |
| 单指滑动 | `oneFingerPan`（移动光标） | `gestures.dart:48-49, 64-68` |
| 双指捏合/平移 | `twoFingerScale`（缩放画面） | `gestures.dart:50-52, 69-70` |
| **三指垂直拖** | `threeFingerVerticalDrag` | `gestures.dart:53-61` |
| 工具栏折叠 | **点箭头按钮** | `remote_page.dart:690-698` |

**⚠️ 未确认**：RustDesk 三指垂直拖**具体绑了什么动作** —— `gestures.dart` 只定义状态机，映射在别处，本轮未定位。**如实标注。**

### 4.3 两个产品的共识

| 手势 | Moonlight | RustDesk | **共识** |
|---|---|---|---|
| **单击** | 左键 | 左键 | ✅ **绝对共识** |
| 双指 | 滚动 | 缩放画面 | ❌ 不同（场景不同） |
| 三指 | **轻点呼键盘** | **垂直拖**（动作未确认） | ❌ 不同 |
| 长按 | 拖动（650ms） | 未确认 | — |

**⇒ 单击是唯一有「全行业共识」的手势。其余都因场景而异。**

---

## 五、我们该怎么定（明确结论）

### 5.1 铁律（不可协商）

| # | 规则 | 依据 |
|---|---|---|
| **1** | **单击画面 = 远端左键**，`down` 后**延迟 100ms** 再 `up` | `RelativeTouchContext.java:200-220` 注释「allow for apps that use polling」 |
| **2** | **任何 UI 都不能由「单击画面」触发** | 两个开源实现均无反例 |
| **3** | 单击与拖动**必须在同一套判定里** | Moonlight 用 `confirmedMove`/`confirmedDrag`/`confirmedTap` 三态互斥 |

### 5.2 工具栏呼出：我的明确结论

**用「常驻折叠按钮」+「三指轻点」双入口。**

| 入口 | 行为 | 为什么 |
|---|---|---|
| **常驻折叠按钮**（画面边缘小箭头） | 点它展开/收起工具栏 | 学 RustDesk；**可发现**，用户一眼能看到 |
| **三指轻点 <300ms** | 呼出键盘 | 学 Moonlight；**不遮挡画面** |

**为什么两个都要**：常驻按钮**可发现性好**（新手能看见）；三指轻点是**熟手快捷方式**（不占屏幕）；**两个入口互为替代**（不会卡死）。

**明确不做**：
· ❌ 单击画面呼出任何 UI；
· ❌ 双击画面呼出 UI（与「双击=远端双击」冲突）；
· ❌ 边缘滑呼出（与系统返回手势冲突，见 §5.4）。

### 5.3 三指判定的必学细节

**三指按下时，必须立刻 cancel 前两指的触摸**（`Game.java:2026-2030`）：
```java
for (TouchContext aTouchContext : touchContextMap) {
    aTouchContext.cancelTouch();
}
```
**为什么**：三指落下时前两指**已经触发了 down 事件**（可能已在远端按下了左键）。
**不 cancel 就会「三指点一下，远端先被点了一下鼠标」** —— 这正是我们可能踩的坑。

### 5.4 手势全集：我们该实现的（按优先级）

| 优先级 | 手势 | 映射 | 依据 |
|---|---|---|---|
| **P0** | 单击 | **远端左键**（down + 100ms up） | 共识 |
| **P0** | 单指滑动 | 光标移动（相对模式用 `moverel`） | 共识 |
| **P0** | 长按 650ms 不动（<30px） | 进入拖动 = 按住左键 | Moonlight |
| **P0** | **三指轻点 <300ms** | **呼出键盘** | Moonlight |
| **P1** | 双指上下滑 | 滚轮 | Moonlight `SCROLL_SPEED_FACTOR` |
| **P1** | 第 2 指单击 | 右键 | Moonlight |
| **P2** | 双指捏合 | 画面缩放（本地，不上报远端） | RustDesk |
| **不做** | **边缘滑动** | — | **与 Android 10+ 系统返回手势冲突** |
| **不做** | 双击画面呼出 UI | — | 与「双击=远端双击」冲突 |

**关于系统返回手势**：我们的原则是**只拦系统没占用的手势**。边界内滑是系统返回，**绝不能拦**。
**[推测]** 若要在边缘放控件，应把控件放在**内侧**（离边缘 >24dp），避免与系统手势区重叠。

---

## 六、我们现在的错误 vs 怎么改

| 现状 | 问题 | 改法 |
|---|---|---|
| **单击画面呼出菜单** | ❌ **违反全行业共识**，导致「每次想点东西先弹菜单」 | 单击 = 远端左键；菜单改由折叠按钮/三指呼出 |
| 菜单出现方式突兀 | 用户说「出来个菜单」 | 折叠按钮**可见**、菜单**从按钮位置展开**（有来源感） |

---

## 七、诚实标注

### 源码确认

| 结论 | 出处 |
|---|---|
| 单击 = 远端左键，down 后 100ms 再 up | `RelativeTouchContext.java:200-220` |
| 三指轻点 <300ms 呼出键盘 | `Game.java:110, 2058-2068` |
| 三指按下时 cancel 前两指 | `Game.java:2026-2030` |
| `Ctrl+Alt+Shift+Z/C/Q` 三个组合 | `Game.java:1222-1246, 1259-1276` |
| RustDesk 工具栏默认显示、箭头按钮折叠 | `remote_page.dart:92, 690-698` |
| RustDesk 鼠标枚举 left/right/wheel/back/forward | `input_model.dart:28-32, 157-165` |
| RustDesk 手势状态机 1/2/3 指 | `gestures.dart:48-61` |

### 未取得 / [推测]

| 项 | 状态 |
|---|---|
| **scrcpy 源码** | **未取到，未验证**（时间给了 Moonlight + RustDesk） |
| **RustDesk 三指垂直拖的具体动作** | **未定位到** |
| 「所有产品都无单击呼出反例」 | **只证明我查的两个无反例**，不能说「全部产品」 |
| V+ 是否改手势 | **未确认**（`Game.kt` 截断 100KB，命中 0 不能证明没有） |
| 边缘控件内缩 24dp | **[推测]** |

---

## 八、一句话给船长

**你的错是行业里几乎没有反例的错：单击画面在 Moonlight 和 RustDesk 里都是远端左键，无一例外。**

**正确做法：**
1. **单击 = 远端左键**（down 后 **100ms** 再 up，兼容轮询式应用）；
2. **工具栏用「常驻折叠按钮」+「三指轻点 <300ms」双入口** —— 前者可发现、后者不遮画面；
3. **三指落下时先 cancel 前两指**，否则三指点一下远端会先被点一次；
4. **不拦系统返回手势**。

---

**文档作者**：yomori-scout
**最后更新**：2026-11-25
**未改代码 ｜ 未连手机**