# 手机串流 UX 深度研究（8 个专题）

> 调研人：yomori-scout ｜ 任务 t36 ｜ attempt `4afff0c1-ff7a-4020-b4d6-c784e6bfb798`
> 日期：2026-11-25 ｜ **未改 SoulLink 代码 ｜ 未连手机 ｜ 只读 ATM-GI**

> 本文是「8 个专题」的深度结论。**只学思路不照搬**（用户明确指示）。
> 已有报告 `MOBILE-UX-BENCH-20261125.md`（481 行）与 `MOONLIGHT-VPLUS-ONSCREEN-CONTROLS-20261124.md`（621 行）已覆盖的部分**不重复**，只做增量。

---

## 专题 ①：全屏（5 层）

### 已完成的部分（引自 `MOBILE-UX-BENCH-20261125.md`，不重复）

5 层定义 + Moonlight 源码出处已在 T31 报告给出（`Game.java:190-206/225-236/764-782/935-960`、`StreamView.java:38-60`、`DEFAULT_STRETCH=false`）。

### ★ 本轮增量：**分辨率协商是最彻底的一层，但要先确认我们服务端能不能改**

| 方案 | 效果 | 前提 | 我们的可行性 |
|---|---|---|---|
| 拉伸铺满（`stretchVideo`） | 无黑边，但**画面变形** | 无 | ✅ 可做（客户端单方） |
| letterbox（保持比例） | 不变形，但**大黑边** | 无 | ✅ 可做（客户端单方） |
| **分辨率协商** | **无黑边 + 不变形**，最彻底 | **服务端要能按客户端比例重新编码** | ⚠️ **需确认** |

**我们当前状况（源码确认）**：服务端编码器分辨率由抓屏矩形决定（`SessionRecorder` 的 `encodedWidth/encodedHeight`），
**没有「按客户端请求改分辨率」的接口**。
**⇒ 分辨率协商对我们是「要改服务端」的 B 档工作，不是配置级改动。**

**⇒ 我的建议（按顺序）**：
1. **先做 letterbox + 拉伸开关**（A 档，客户端单方，立刻解决「变形/黑边」）；
2. **再做分辨率协商**（B 档，需服务端配合）—— 但**手机竖屏 vs 桌面横屏比例完全相反**，
   协商成竖屏分辨率会让桌面 UI 重排（Windows 会对 1080×2340 这种分辨率做奇怪布局），
   **可能反而更糟** —— [推测] 这条需要实测才能定。

---

## 专题 ②：虚拟鼠标（带可见光标）

### 已确认的现状（T31 报告，不重复）

服务端 `CursorEcho()` 已有、网页端 `#rcCursor` 已画（十字+圆环）、**安卓端零光标概念**。

### ★ 本轮增量一：**光标形态的选择**（用户要「带光标的可移动鼠标形态」）

| 形态 | 优点 | 缺点 | 适用 |
|---|---|---|---|
| **箭头**（系统默认） | 用户最熟悉，指向精确（尖端点=热点） | 小屏上偏小，深色背景可能看不清 | **桌面操控首选** |
| **十字准星**（网页端现用） | 中心点明确，适合精细定位 | 不直观（不像鼠标） | 绘图/精确选取 |
| **圆点+外环** | 醒目、不易丢失 | 热点在圆心，**指向不如箭头精确** | 触摸板模式辅助 |
| **箭头 + 点击涟漪** | 有反馈 | 稍复杂 | **推荐组合** |

**⇒ 建议**：**默认箭头**（符合「鼠标形态」的直觉），**点击时加涟漪**（补足「点到了」的反馈）。
箭头资源：Android 有现成的 `PointerIcon.getSystemIcon(TYPE_DEFAULT)` 可参考尺寸（[推测] 约 24dp 逻辑尺寸）。

### ★ 本轮增量二：**本地预测 vs 远端回传** 的取舍

| 方案 | 跟手性 | 位置准确性 | 复杂度 |
|---|---|---|---|
| **纯远端回传** | ❌ 差（等一个 RTT + 服务端抓帧周期） | ✅ 准 | 低（网页端已是这样） |
| **纯本地预测** | ✅ 好（0 延迟） | ❌ 会漂移（与服务端不一致） | 中 |
| **本地预测 + 远端校正** ✅ | ✅ 好 | ✅ 准（静止时对齐） | 中高 |

**⇒ 建议：本地预测 + 远端校正**：
```
手指动 → 立即本地移动光标（零延迟，跟手）
        → 同时发 moverel 给服务端
服务端回 cursor{nx,ny} → 若与本地差异 > 阈值（如 3% 屏宽）则平滑纠正
```
**为什么需要校正**：本地预测会累积误差（尤其服务端有加速/边界限制时），
**不校正就会出现「本地光标和远端实际位置差很远」** —— 那时点击会点错地方。

**但注意 T31 发现的坑**：只在 input 响应附光标 ⇒ 相对模式下不动手指就没有新回传。
**⇒ 必须补独立 `/api/cursor` 低频轮询（10~30Hz）**，否则「手指停住时光标不更新」，校正也无从谈起。

---

## 专题 ③：触摸手感参数

### 已确认（T31 报告，源码级，不重复）

`TAP_MOVEMENT=20px`、`TAP_DISTANCE=25px`（累计路径）、`TAP_TIME=250ms`、`LONG_PRESS=650ms/30px`、
`DOUBLE_TAP=250ms/60px`、**`TOUCH_DOWN_DEAD_ZONE=100ms/20px`**、零增量保护、tap 后 100ms 抬键、`DEFAULT_ABSOLUTE_MOUSE_MODE=false`。

### ★ 本轮增量：**我们该选哪套默认值**

| 参数 | Moonlight 值 | 我们当前值 | 建议 |
|---|---|---|---|
| 默认模式 | **相对** | **绝对** ← 头号问题 | **改相对** |
| 按下死区 | 100ms / 20px | **无** | **加** |
| tap 位移 | 20px / 25px 累计 | `TAP_PX=10f` | **放宽到 20px** |
| 长按 | 650ms / 30px | 600ms / 12dp | 接近，**容差放宽到 20~30px** |
| 双击 | 250ms / 60px | 无 | 可选 |

**★ 一个必须注意的差异**：Moonlight 的 px 是**屏幕物理像素**，我们的 dp 是**密度无关**。
在 3x 屏上 20px ≈ 6.7dp；**`TAP_PX=10f` 换算过去反而比 Moonlight 更宽**。
**⇒ 不能直接照抄数字，要按「占屏幕宽度的百分比」换算才对等**（[推测] Moonlight 未做此换算，因为它按固定分辨率设计）。

---

## 专题 ④：虚拟按键层

### 已覆盖（`MOONLIGHT-VPLUS-ONSCREEN-CONTROLS-20261124.md` 621 行，不重复）

控件类型、4 套预设、`owners` 多来源合并、触感、数据模型 JSON、Compose 渲染方案、三档工作量。

### ★ 本轮增量：ATM-GI 的 `Host` 接口思路（架构层面）

ATM-GI 的 `PanelViewFactory`（42.5KB）用 **`interface Host`（约 30 个能力方法）做依赖倒置**：
· **工厂只负责「把配置变成 View」**，业务能力（开页面/读配置/拖拽）全在 Host；
· 源码注释（`PanelViewFactory.kt:24-29`）：「只创建 View 并绑定点击回调，**布局数据来自 PanelController**」。

**⇒ 我们的虚拟按键层照这个骨架拆**：
```
VirtualControlConfig  (纯数据：type/anchor/size/mapping/turbo/layer)
        ↓
ControlRenderer        (只吃 config，输出 Composable)
        ↓
ControlHost (接口)     (onKeyDown/onKeyUp/onMoveRel/onSave)
```
**好处**：渲染层可 `@Preview` 单测（不需要真设备/真连接），**这是 ATM-GI 教给我们的最实用一条**。

---

## 专题 ⑤：键盘呼出

### ★ 本轮新拿到的一手证据

**Moonlight 的呼出方式（源码确认，`Game.java:1500-1505`）**：
```java
@Override
public void toggleKeyboard() {
    LimeLog.info("Toggling keyboard overlay");
    InputMethodManager inputManager = (InputMethodManager) getSystemService(Context.INPUT_METHOD_SERVICE);
    inputManager.toggleSoftInput(0, 0);       // ← toggle，不是 show
}
```

**三个关键事实**：
1. **用 `toggleSoftInput(0,0)` 而非 `showSoftInput()`** —— 同一个按钮**开/关切换**，不需要额外「收起」按钮；
2. **`0, 0` 参数**：第一个 0 = 无额外 flags，第二个 0 = `SHOW_IMPLICIT`（[推测] 依赖窗口焦点，不强制）；
3. **它是一个「被 UI 调用的方法」**（`toggleKeyboard()` 是 `InputCallbacks` 接口的一部分），
   **说明入口在 UI 层**（虚拟按键层里的一个键盘按钮），不是「点画面自动弹」。

### ★ 软键盘挤压画面：Moonlight 怎么避免

**AndroidManifest 源码确认**（9794B 全文已取）：
· **`Game` Activity 没有显式设 `windowSoftInputMode`**（L89-107 那段），即**用默认 `adjustResize` 语义**；
· 另一个 Activity 显式设了 `android:windowSoftInputMode="stateVisible"`（L139）；
· `Game` 的 `configChanges` 里**包含 `screenSize|smallestScreenSize`**（L94），
  ⇒ **软键盘弹出导致的尺寸变化不会重建 Activity**，而是走 `onConfigurationChanged`。
· `resizeableActivity="true"`（L92）。

**[推测] 它避免挤压的真正方式是**：
`configChanges` 含 `screenSize` ⇒ Activity 不重建，`StreamView.onMeasure` 会拿到新的可用高度，
**按 `desiredAspectRatio` 等比缩小**（`StreamView.java:38-60`）—— 即**画面等比缩小、不拉伸变形**。
**代价**：画面变小（上面留出键盘空间），但**不变形、不裁切**。

**⇒ 我们的选择**：
| 方案 | 效果 | 建议 |
|---|---|---|
| `adjustResize` + 等比缩小 | 画面变小不变形 | ✅ **推荐**（Moonlight 同款） |
| `adjustNothing` | 画面不变，**键盘遮住下半屏** | 桌面操控慎用（遮住就看不见下半屏） |
| `adjustPan` | 整体上移 | ❌ 画面会「跳」 |

**文本输入走 `Text` 还是逐键**：
Moonlight 两者都有 —— `conn.sendUtf8Text(event.getCharacters())`（`Game.java:1486`）用于 IME 组合文本；
`conn.sendKeyboardInput(translated, KEY_DOWN, modifier)`（`L1397`）用于物理键/单键。
**⇒ 中文输入必须走 `Text`**（逐键发 VK 码无法输入汉字）。

---

## 专题 ⑥：工具栏/悬浮层交互

### ★ ATM-GI 的一球多手势（源码确认，`BallView.kt:120-210`）

| 手势 | 行为 | 关键实现 |
|---|---|---|
| 轻点 | 切下一页 | `!dragging && !longPressed → performClick()` |
| 长按 480ms | 打开菜单 | `postDelayed(480)`，移动超 **8f** 取消 |
| 横向拖动 | 移位（带阻尼 0.55f） | 松手 `OvershootInterpolator(1.9f)` 420ms 回弹 |
| **上滑 >34dp** | 展开扇形菜单 | 判定 `垂直 > 水平*1.1` **防斜滑误触** |

**⇒ 我们的工具栏交互建议**：
· **触发**：点屏幕边缘/悬浮球（**不要用三指等隐蔽手势** —— 用户发现不了）；
· **自动隐藏**：专业应用 3~5 秒（[推测] 无权威数字，取 4s 折中）；
· **隐藏时必须完全不响应触摸**（`pointerInput` 整层不启用）—— 否则单击会先被工具栏吃掉；
· **阻尼 + 过冲回弹**这类手感细节可直接借（比 Material 默认自然）。

---

## 专题 ⑦：手势全集

### 已确认（T31 报告）

Moonlight 的 tap/drag/scroll/多指语义（`RelativeTouchContext` / `AbsoluteTouchContext`）。

### ★ 本轮增量：**系统返回手势该不该拦**

| 手势 | 建议 | 理由 |
|---|---|---|
| **系统返回（边缘内滑）** | **不要拦** | Android 10+ 的预测性返回是系统级约定，拦了用户会「退不出去」 |
| 双指下滑 | 可拦 | 不冲突 |
| 三指 | 可拦 | 但要**可发现**（首次使用给提示） |
| 长按画面 | **不要拦**（用于拖动/右键） | 与系统「长按菜单」可能冲突，需测试 |

**⇒ 我们的原则**：**只拦「系统没占用」的手势**，且每个手势都要有替代路径（避免用户卡死）。

---

## 专题 ⑧：视觉语言（原生感）

### ★ 我们为什么「像网页」

**源码层面（我们自己的代码）**：`PlayerPlaceholderScreen.kt` 直接用了 `OutlinedButton`/`Text`/`TextButton`（Material 默认组件），
**这些组件的默认样式就是「表单控件」** —— 描边、默认圆角、默认字号，这正是网页表单的视觉语言。

### ★ 从 ATM-GI 截图实测到的视觉规律（我看的是像素，不是描述）

| 维度 | 实测观察 |
|---|---|
| **卡片** | **确实是圆角**（约 16~24dp），但**按钮是接近直角的矩形** |
| 背景 | 深色渐变（紫/蓝）+ 星点粒子 |
| 主按钮 | **粉→紫渐变填充**，无描边 |
| 层次 | **靠卡片分区**、靠留白，**很少用标题文字** |
| 悬浮球 | 底部橙色实心圆，**压在内容上（不占布局）** |

**⚠️ 但用户说它「不成熟」，所以不能照抄配色/粒子/发光。**

### ★ 我建议的「原生感」设计 token（不抄 ATM-GI）

| Token | 建议值 | 依据 |
|---|---|---|
| 圆角 | 卡片 16dp / 按钮 **全圆（pill，height/2）** / 弹层顶部 28dp | Material 3 规范 + 原生应用惯例 |
| 间距 | 4dp 基准（4/8/12/16/24） | Material 8dp 网格的细分 |
| 字号 | 标题 20sp / 正文 14sp / 辅助 12sp | Material Type Scale |
| 动画时长 | 快速 150ms / 常规 250ms / 弹层 300ms | Material Motion 规范 |
| 缓动 | `FastOutSlowIn`（常规）/ `OvershootInterpolator`（回弹） | 前者系统标准，后者手感 |
| 触摸反馈 | **涟漪 + 轻微缩放 0.96 + 触感** | ATM-GI 的 0.9 缩放 + VIRTUAL_KEY 触感（`BallView.kt:136-139`）可借 |
| 透明度 | 工具栏 85~92%（不能太透，看不清） | [推测] 经验值 |

**★ 最关键的一条**：
**「原生感」不等于「好看」，而等于「符合 Android 交互惯例」**：
· 涟漪从**按下点**扩散（不是从中心）；
· 返回键**有过渡动画**；
· 长按**先触感后弹窗**；
· 列表**有 overscroll 边缘光**。
**这些是 Material 组件自动给的 —— 所以问题不在「用 Material」，而在「用错了组件」（OutlinedButton 是表单控件，不是主操作按钮）。**

**⇒ 具体改法**：把 `OutlinedButton` 换成 `FilledTonalButton` 或自定义 `Surface + clickable`，
主操作用 `Button`（filled），**不要用描边样式做主要操作**。

---

## 汇总：按优先级的落地清单

### A 档（立刻做，客户端单方）

| # | 改动 | 解决症状 | 依据 |
|---|---|---|---|
| **A1** | 默认鼠标模式 `absolute → relative` | ★★★ 差到没法用 | `DEFAULT_ABSOLUTE_MOUSE_MODE=false` |
| **A2** | 加按下死区 100ms/20px | ★★ 滑动前误点 | `TOUCH_DOWN_DEAD_ZONE=100ms/20px` |
| **A3** | **安卓端画可见光标**（箭头+点击涟漪） | ★★★ 看不见指针 | 服务端 `CursorEcho` 已有 |
| **A4** | 加「铺满/保持比例」选项 | ★★ 变形或黑边 | `DEFAULT_STRETCH=false` + letterbox |
| A5 | `tap` 阈值 10f → 20px 等效 | 误判拖动 | `TAP_MOVEMENT=20` |
| A6 | 把 `OutlinedButton` 换成 filled/pill | 像网页 | 专题⑧ |

### B 档（要改架构）

| # | 改动 | 解决症状 |
|---|---|---|
| B1 | **独立 `/api/cursor` 低频轮询（10~30Hz）** | 手指停住时光标不更新 |
| B2 | 本地预测 + 远端校正 | 光标跟手性 |
| B3 | 虚拟按键层（Host 接口 + 数据驱动渲染） | 游戏操控 |
| B4 | 键盘呼出按钮 + `adjustResize` + 等比缩小 | 输入文字 |
| B5 | 分辨率协商（需服务端配合） | 彻底消除黑边（**但竖屏/横屏比例相反，需实测**） |

### C 档（暂不做）

| # | 内容 | 理由 |
|---|---|---|
| C1 | 扇形菜单 | 底部弹层更简单；入口 ≤6 时才值得 |
| C2 | 触摸平滑/插值 | Moonlight 官方也没做，会增延迟 |
| C3 | 系统级悬浮窗（`SYSTEM_ALERT_WINDOW`） | 我们只需页面内悬浮，**ATM-GI 也没用** |

---

## 诚实标注

### 源码确认（本轮新增证据）
| 结论 | 出处 |
|---|---|
| Moonlight 用 `toggleSoftInput(0,0)` 呼出键盘 | `Game.java:1500-1505` |
| `Game` Activity 无显式 `windowSoftInputMode`，但有 `screenSize` in configChanges | `AndroidManifest.xml:89-107` |
| 另一个 Activity 设了 `stateVisible` | `AndroidManifest.xml:139` |
| ATM-GI 悬浮球在 Activity 内、Canvas 自绘、零权限 | `BallView.kt:22, 74-101`；`MainActivity.kt:68, :1123` |
| ATM-GI 一球四手势 + 阻尼 0.55 + Overshoot 1.9 | `BallView.kt:120-210` |
| ATM-GI 扇形菜单 200dp 弧 + Overshoot 1.4 飞入 | `FanMenuView.kt:43-44, 106-117` |
| ATM-GI `OverlayActivity` 是透明 Activity，**无 FLAG_NOT_TOUCHABLE** | `OverlayActivity.kt:35-64, 708-744` |
| ATM-GI `PanelViewFactory` 用 Host 接口依赖倒置 | `PanelViewFactory.kt:30-75` |
| ATM-GI 拖拽用系统 `scaledTouchSlop`/`getLongPressTimeout` | `PanelDragController.kt:51-52` |

### 未取得 / 标 [推测]
| 项 | 状态 |
|---|---|
| 端到端延迟数字（各产品） | **未取得，未编** |
| `toggleSoftInput(0,0)` 的 `0` 具体语义 | **[推测]**（未查 AOSP 源码） |
| Moonlight 避免挤压的真实机制 | **[推测]**（基于 configChanges + onMeasure 推断，未实测） |
| 工具栏自动隐藏 3~5 秒 | **[推测]**（无权威数字） |
| 光标箭头逻辑尺寸 24dp | **[推测]** |
| 分辨率协商对 Windows 桌面 UI 的影响 | **[推测]**（需实测） |

---

**文档作者**：yomori-scout
**最后更新**：2026-11-25
**未改 SoulLink 代码 ｜ 只读 ATM-GI ｜ 未连手机**