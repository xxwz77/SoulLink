# ATM-GI 结构思路学习（**只学思路，不照搬**）

> 调研人：yomori-scout ｜ 任务 t36 ｜ attempt `4afff0c1-ff7a-4020-b4d6-c784e6bfb798`
> 日期：2026-11-25 ｜ **只读 ATM-GI，未改其任何文件 ｜ 未改 SoulLink 代码 ｜ 未连手机**

> **用户明确指示**：「**别照搬，那不是一个成熟的项目。只能学习一下思路。**」
> 本文严格按此执行：**只写「它为什么这么做 → 我们该怎么做」，不贴大段代码，不建议抄视觉/组件。**

---

## 0. 我实际做了什么

· **看了 4 张截图**：`v1.png`、`t1.png`、`v5.png`、`preview-theme.png`（1208×2624 / 1836×1320）；
· **读了源码**：`BallView.kt` 全文、`FanMenuView.kt` 前 120 行、`OverlayActivity.kt` 关键行、`PanelViewFactory.kt` 前 80 行、`PanelDragController.kt` 前 70 行、`docs-对比报告.md` 全文；
· 项目规模确认：`ui/` 下 **51 个 .kt 文件**，最大 `ScreenShareActivity.kt` 79.8KB、`PanelViewFactory.kt` 42.5KB。

### ⚠️ 先纠正一处：截图上看到的与你描述不同

你说「**大号圆角按钮**」，但我在 `v1.png`/`t1.png` 上实际看到的是：
**「打开 AV-GI」「数据分析」「屏幕共享」这些按钮是接近直角的矩形（圆角极小或无圆角），不是大圆角。**
真正圆角明显的是**卡片**（账号卡、配置组卡、路线卡）和**底部弹层**（`v5.png` 顶角圆）。
**⇒ 我按实际看到的写，不按描述写。** 这也印证了「不能照搬」——它自己的视觉并不统一。

---

## 1. 从 ATM-GI 提取的 7 条结构思路

### 思路 1：悬浮球是「常驻入口」，但**它不在系统层，而在 Activity 内**

**它怎么做的**：
· `BallView` 是**普通自定义 View**（继承 `View`），用 `Canvas.drawCircle` 自绘（`BallView.kt:74-101`）；
· **承载在 `MainActivity` 里**（`MainActivity.kt:68, :1123` 的 `ball = BallView(this)`），**不是 `WindowManager` 悬浮窗**；
· **⇒ 不需要 `SYSTEM_ALERT_WINDOW` 权限**（这点很关键，见下）。

**为什么这么做（推测其动机）**：
· 系统级悬浮窗要申请「显示在其他应用上层」权限，**用户授权门槛高、且很多国产 ROM 会杀**；
· Activity 内悬浮球**零权限**，只要在页面里就一直可见；
· 代价是**离开这个 App 就消失** —— 但 ATM-GI 是**单 App 工具**，够用。

**⇒ 我们该怎么做**：
| 我们的场景 | 结论 |
|---|---|
| 播放页内的悬浮工具栏 | **用 Activity 内 View 叠加**（我们已经是 Compose，直接在 `Box` 顶层加一层）——**零权限、最简单** |
| 需要「切到别的 App 也浮着」 | **才需要** `SYSTEM_ALERT_WINDOW` —— 我们的播放页不需要 |

**这条思路对我们价值是「反向确认」：我们不需要系统悬浮窗，别把问题复杂化。**

---

### 思路 2：悬浮球的**手势语义**设计（一个球承载 4 种操作）

**它怎么做的**（`BallView.kt:120-210` 的 `onTouchEvent`）：

| 手势 | 行为 | 实现要点 |
|---|---|---|
| **轻点** | 切到下一页 | `!dragging && !longPressed → performClick()` |
| **长按 480ms** | 打开菜单 | `postDelayed(longPressRunnable, 480)`，触发前若移动超 8f 则取消 |
| **横向拖动** | 底栏内左右挪 | **带阻尼** `translationX + dx * 0.55f`，松手 `OvershootInterpolator(1.9f)` 回弹 420ms |
| **向上滑 >34dp** | 展开扇形菜单 | 判定 `(downRawY - rawY) > 34dp && 垂直分量 > 水平*1.1`（**防斜滑误触**） |

**值得学的三个细节（源码确认）**：
1. **按下即反馈**：`performHapticFeedback(VIRTUAL_KEY)` + `scaleX/Y(0.9f)` 90ms 缩放（`L136-139`）——**不等点击完成**；
2. **拖动带阻尼**：`0.55f` 系数让「拖得越远越拉不动」，**手感像皮筋**（`L175-179`）；
3. **上滑展开时把水平位移复位**：`translationX = 0f`，源码注释写「**否则气球会越滑越偏**」（`L168`）—— 这是踩过坑的痕迹。
4. **回弹过冲**：`OvershootInterpolator(1.9f)` 让球弹回时**略微过冲再回位**，比线性自然。

**⇒ 我们该怎么做**：
· **这套「一球多手势」的语义可以直接借**：轻点=开工具栏、长按=更多、拖动=移位、上滑=扇形菜单；
· **阻尼 + 过冲回弹**是「手感」的关键，**比 Material 默认动画自然**；
· **上滑判定要加「垂直分量 > 水平*1.1」**，否则斜着滑会误触 —— 这个细节值得抄。

---

### 思路 3：扇形菜单解决「球附近放不下更多入口」

**它怎么做的**（`FanMenuView.kt`）：
· 条目在球**上方呈扇形**排布，`arcRadius = 200dp`、`itemRadius = 34dp`（`L43-44`）；
· **入场动画**：`ValueAnimator 0→1`，400ms，`OvershootInterpolator(1.4f)`（`L106-117`）——**从球心飞出 + 缩放 + 淡入**；
· **拖选交互**：`updatePointer(x,y)` 实时高亮命中的项（`L92-98`），松手 `onFanCommit()` 生效；
· **半透明遮罩** `#66000000`（`L50`）压暗背景，突出菜单。

**为什么扇形而不是列表**：
· 扇形**以球为圆心**，手指从球滑出去**天然沿着弧线**，**不需要「先抬手再点」** → 一步完成；
· 列表需要「点球 → 弹出列表 → 再点一项」，**多一次点击**。

**⇒ 我们该怎么做**：
· **适用场景**：入口数量 3~6 个、且**操作要快**（如「键盘/鼠标模式/虚拟按键/设置」）；
· **不适用**：入口 >8 个（扇形排布会挤）或需要文字说明（扇形空间小）。
· 我们**可以先不做扇形**，用「底部弹层」更简单；**但「拖选即生效」这个交互值得借鉴**。

---

### 思路 4：`OverlayActivity` 是**透明 Activity**，不是悬浮窗

**它怎么做的**（`OverlayActivity.kt` 748 行）：
· 通过 `EXTRA_TRANSPARENT` 控制背景：`if (transparent) Color.TRANSPARENT else Color.parseColor("#191034")`（`L56`）；
· 根容器 `FrameLayout` + `MATCH_PARENT` + 透明背景（`L61-64`）；
· 用 `Intent.FLAG_ACTIVITY_NO_ANIMATION` 启动（`L721, L736`）让覆盖层「瞬间出现」；
· 非 Activity 上下文时加 `FLAG_ACTIVITY_NEW_TASK`；
· **有 `startOnTop()` 重载**（`L743`）—— 叠在另一个 Overlay 之上。

**★ 重要澄清（源码确认）**：
**我没有在这个文件里找到 `FLAG_NOT_TOUCHABLE`、也没有 `TYPE_APPLICATION_OVERLAY`。**
**⇒ 它不是「浮在上面但不拦触摸」的悬浮窗，而是一个「透明 Activity」** ——
**透明 Activity 仍然会拦截触摸**（它占据整个屏幕）。

**⇒ 我们该怎么做（这条要反过来学）**：
| 需求 | 正确做法 |
|---|---|
| 覆盖层**只在自己控件区域响应触摸**，其余穿透 | **不要用透明 Activity**；在**同一个页面内**用 Compose `Box` 叠一层，**只有控件本身 `pointerInput` 消费事件**，空白处不消费 → 自动穿透到下层 |
| 覆盖层需要**独立返回栈** | 才用透明 Activity |

**这条是本轮最有价值的「反面教材」**：ATM-GI 的透明 Activity 方案对我们不适用，**我们的 Compose 同层叠加更优**。

---

### 思路 5：`PanelViewFactory` 用 **Host 接口做依赖倒置**

**它怎么做的**（`PanelViewFactory.kt:30-75`）：
```kotlin
class PanelViewFactory(private val host: Host) {
    interface Host {
        val activity: Activity
        fun dp(v: Int): Int
        fun panelBg(): String
        fun tint(hex: String): String
        fun openScreenShare(longPress: Boolean)
        fun openOneDragon()
        fun isEditMode(): Boolean
        fun attachDrag(v: View, m: PModV)
        // ... 约 30 个能力
    }
}
```

源码注释原话（`L24-29`）：
> 「只创建 View 并绑定点击回调，**布局数据来自 PanelController**；拖动 / 缩放手柄仍由 MainActivity 处理。」

**为什么这么做**：
· **UI 构建**（42.5KB 的面板拼装）与**业务能力**（打开页面/读写配置/拖拽）**完全解耦**；
· 工厂只依赖 `Host` 接口，**不依赖 MainActivity 的具体实现** → 可以单测、可以复用到别的宿主；
· **数据驱动**：`PanelEntry`（配置项）决定渲染什么，工厂只负责「把 entry 变成 View」。

**⇒ 我们该怎么做**：
· 我们的虚拟按键层也应该**拆成「数据（配置） + 渲染（Composable）」两层**：
  - **数据层**：`VirtualControlConfig`（JSON，含 `type`/`anchor`/`mapping`/`turbo`）；
  - **渲染层**：`@Composable fun RenderControl(config, onEvent)` —— **只吃配置，不碰业务**；
  - **宿主**：传入 `onKeyDown`/`onKeyUp`/`onMoveRel` 回调 → **可单测、可预览**。
· **这条是最值得学的架构思路**，比任何视觉都重要。

---

### 思路 6：拖拽编辑用**系统常量**而不是拍脑袋的数字

**它怎么做的**（`PanelDragController.kt:46-70`）：
```kotlin
val touchSlop = ViewConfiguration.get(ctx).scaledTouchSlop      // ← 系统 touch slop
val longPressTimeout = ViewConfiguration.getLongPressTimeout()  // ← 系统长按时长
```
配合注释（`L45`）：「**编辑态拖动：先长按进入拖动，再跟手吸附；轻点只选中。**」

**为什么这么做**：
· `scaledTouchSlop` 是**按当前屏幕密度/系统设置算好的**，比写死 `12dp` 更贴合用户设备；
· `getLongPressTimeout()` 尊重**用户是否在系统设置里调过长按时长**（无障碍场景）。

**⇒ 我们该怎么做**：
**我们现在的 `PlayerConstants.kt` 用的是写死常量**（`DRAG_THRESHOLD_DP = 12`、`LONG_PRESS_MS = 600`）。
**建议改成「系统常量 + 可覆盖」**：
· 编辑态拖拽：用 `ViewConfiguration.getLongPressTimeout()`；
· 触摸容差：用 `scaledTouchSlop`；
· 但**游戏操作用的阈值仍应写死**（`TOUCH_DOWN_DEAD_ZONE=100ms/20px` 那套），因为要跨设备一致。

**⇒ 区分「系统 UI 交互用系统常量」vs「游戏手感用手调常量」** —— 这是它没写出来但实际做到的。

---

### 思路 7：`docs-对比报告.md` 的思路 —— **用表格管理「有/无/部分」**

**它怎么做的**：整份文档是**「网页功能 vs App 功能」逐项对照表**，每项标 ✅/❌/⚠️，最后给「最值得补的三个缺口」。

**为什么这么做**：
· 不写「我们做了什么」，而是**写「差距是什么」**；
· **明确标「不建议补的」**（如 Maa/日志分析「功能体量远超其他页面，且手机上不实用」）—— **有取舍**。

**⇒ 我们该怎么做**：
· 我们的对标报告也应加一节「**不建议做的**」，避免「什么都想抄」；
· 尤其我们已有 `T31` 那轮的经验：**C 档（FEC/UDP）明确不建议**，这就是好实践。

---

## 2. 汇总：值得学 vs 不该学

| # | ATM-GI 的做法 | 我们学不学 | 理由 |
|---|---|---|---|
| 1 | 悬浮球在 Activity 内（零权限） | ✅ **学** | 我们播放页也不需要系统悬浮窗 |
| 2 | 一球多手势（点/长按/拖/上滑） | ✅ **学** | 省入口，且手势判定有防误触细节 |
| 3 | 阻尼拖动 + Overshoot 回弹 | ✅ **学** | 手感关键，比 Material 默认自然 |
| 4 | 扇形菜单拖选即生效 | ⚠️ **可选** | 入口 ≤6 个时好用；我们先用底部弹层更简单 |
| 5 | **透明 Activity 做覆盖层** | ❌ **不学** | 它仍拦触摸；我们用 Compose 同层叠加更优 |
| 6 | **Host 接口依赖倒置 + 数据驱动** | ✅✅ **最该学** | 架构层面，让虚拟按键层可测可复用 |
| 7 | 拖拽用系统 `touchSlop`/`getLongPressTimeout()` | ✅ **学** | 尊重用户设备与无障碍设置 |
| 8 | 对比报告标「不建议做的」 | ✅ **学** | 避免无脑对标 |
| 9 | **视觉：渐变背景/发光/星星粒子** | ❌ **不学** | 用户明确说不成熟；且与「原生串流应用」方向不符（那是游戏化装饰） |
| 10 | 按钮直角/卡片圆角混用 | ❌ **不学** | 视觉不统一（我对截图的实测结论） |

---

## 3. 一句话总结

**ATM-GI 对我们的价值**不在视觉，而在三件事：
1. **确认「悬浮球不需要系统权限」**（Activity 内即可）——**别把问题复杂化**；
2. **手势语义与手感**（阻尼/过冲/防误触判定）——**可直接借**；
3. **Host 接口 + 数据驱动的架构**——**最该学，决定虚拟按键层能不能维护**。

**而它的视觉与透明 Activity 方案，我们明确不学。**

---

**文档作者**：yomori-scout
**最后更新**：2026-11-25
**只读 ATM-GI，未改其任何文件 ｜ 未改 SoulLink 代码 ｜ 未连手机**