# 对标调研：Moonlight V+ 虚拟按键/屏幕控件（可照实现）

> 调研人：yomori-scout ｜ 任务 t30 ｜ attempt `61c2a389-15a8-49af-88f4-9ba8b39004d5`
> 日期：2026-11-24 ｜ **纯调研，未改任何代码**

---

## 0. 先说三件必须先知道的事

### ① 上一轮「太浅」的那三行，根因是**没拿到源码**

本轮找到了可用的源码通道：**`ghproxy.net` 代理 raw.githubusercontent**（`api.github.com` 直连已 403 限流、
`raw.githubusercontent.com` 与 `cdn.jsdelivr.net` 均不可达、GitHub 网页是 SPA 外壳）。
**⇒ 下面的所有 V+ 结论都来自真实源码，不是读 README 猜的。**

### ② 我们**本地已有一份 552 行的同类调研**，本次只做增量

| 已有文档 | 覆盖内容 | 本文态度 |
|---|---|---|
| `docs/research/VIRTUAL-CONTROLS-RESEARCH-20261122.md`（552 行） | **官方 Moonlight OSC 完整拆解**（§4：架构分层、JSON、摇杆数学、许可约束）、UU 远程功能清单、**JSON Schema**（§5） | **采纳，不重复** |
| `docs/research/MOBILE-CONTROLS-DESIGN-20261122.md`（24 命中） | 摇杆 8 方向映射数学、布局 JSON 初稿 | **采纳** |

**⇒ 本文的唯一增量是「V+ 相对官方新增了什么」+「我们怎么落地」。**
因为官方 OSC 的设计，上面那份 552 行文档已经拆到源码级了，**重复它没有价值**。

### ③ ★ 发现一个**必须先补的硬阻塞**

我们 Android 端 `InputAction.kt` **只有**：`Click/Down/Up/Move/MoveRel/Wheel/Noop/ReleaseAll`；
**缺少** `key` / `keydown` / `keyup` / `text` —— 而**后端全部已支持**：

| 动作 | 后端 | 安卓 InputAction |
|---|---|---|
| `key` | ✅ `RelayAgent.cs:1021` | ❌ **没有** |
| `keydown` | ✅ `RelayAgent.cs:1039` | ❌ **没有** |
| `keyup` | ✅ `RelayAgent.cs:1040` | ❌ **没有** |
| `text` | ✅ `RelayAgent.cs:1120` | ❌ **没有** |

**⇒ 虚拟按键的本质就是「发 keydown/keyup」，所以这是实现虚拟按键前的 P0 前置条件。**
（`InputAction.kt` 的注释自己写着「动作已全部在后端接线：...keydown/keyup/text...」——**注释与实现不符**，实际只接线了后端侧。）

---

## 1. 一、V+ 控件到底怎么实现的（源码级）

### 1.1 源码位置（与官方**同一路径**，但已 Kotlin 化）

```
app/src/main/java/com/limelight/binding/input/virtual_controller/
  VirtualController.kt                     18,483B   ← 官方 Java 版 7,188B 的 2.57 倍
  VirtualControllerElement.kt               6,855B
  VirtualControllerConfigurationLoader.kt  12,440B
  VirtualControllerLayout.kt                  395B   ← ★ V+ 新增文件
  DigitalButton.kt                          9,318B
  AnalogStick.kt                            7,173B
```

**官方对照**（同路径）：`VirtualController.java` = **7,188B**。
**⇒ 主容器代码量 2.57 倍增长，这是 V+ 增强幅度的直接证据。**

### 1.2 ★ V+ 最核心的新增：**多套布局预设（VirtualControllerLayout）**

全文（395B，**这就是官方没有的东西**）：

```kotlin
enum class VirtualControllerLayout(val preferenceValue: String) {
    XBOX("xbox"), DS("ds"), NS("ns"), CLASSIC("classic");
    companion object {
        fun fromPreference(value: String?) =
            entries.firstOrNull { it.preferenceValue == value } ?: XBOX
    }
}
```

| 预设 | preferenceValue | 强调色（源码 `VirtualControllerConfigurationLoader.kt`） |
|---|---|---|
| **XBOX** | `xbox` | `0xFF8ED269`（绿） |
| **DS**（DualSense） | `ds` | `0xFF83B7FF`（蓝） |
| **NS**（Switch） | `ns` | `0xFF56CEE5`（青）｜**右侧按键用 `0xFFFF7C83`（红）** |
| **CLASSIC** | `classic` | `0xF00000FF`（沿用官方原始键位） |

**切换机制**（`VirtualController.kt` `switchLayout()`）：
```kotlin
internal fun switchLayout(preset: VirtualControllerLayout) {
    if (controllerMode != ControllerMode.Active) finishEditing()
    releaseInputs()                       // ← 切布局前先松开所有按键（防粘键）
    defaultPreferences.edit()
        .putString("list_osc_layout", preset.preferenceValue).apply()
    refreshLayout()
}
```

**⇒ 对我们的直接价值**：**「多套预设 + 切换」是 V+ 最显眼的增强**，
而官方只能「移动/缩放一套固定手柄位图」。**这是我们要抄的第一件事。**

### 1.3 缩放算法：`72` 逻辑高度 + 16:9 锚定（V+ 相对官方有改动）

源码 `VirtualControllerConfigurationLoader.createDefaultLayout()`：
```kotlin
val classic = style == VirtualControllerLayout.CLASSIC
val half = config.halfHeightOscPortrait && orientation == ORIENTATION_PORTRAIT
val available = if (half) layoutHeight / 2 else layoutHeight
val height = if (classic) available else minOf(available, layoutWidth * 72 / 88)
val scale = height / 72f
controller.profileOffsetY = if (classic) { if (half) height else 0 } else layoutHeight - height
```

| 设计点 | 说明 |
|---|---|
| 逻辑高度 **72** 单位 | 沿用官方常量体系（官方 `screenScale(units,height)=height/72*units`） |
| **V+ 新增 `layoutWidth * 72 / 88`** | 用 **88（而非 72）** 做宽高比约束 —— 因为现代手机比 16:9 更宽，直接按 72 会溢出 |
| **`profileOffsetY = layoutHeight - height`** | 非 classic 布局**底部对齐**（贴住屏幕下缘），而非顶端对齐 |
| **竖屏半高** `halfHeightOscPortrait` | 竖屏时控件只占下半屏，上半留给游戏画面 |

**★ 关键实现细节**（值得直接抄）：相邻控件边界**只取整一次**，避免像素重叠：
```kotlin
if (classic) (w * scale).roundToInt()
else ((x + w) * scale).roundToInt() - (x * scale).roundToInt()   // ← 算边界差，不是宽度乘 scale
```
源码注释原文：`// Round shared boundaries once so adjacent hit targets never overlap by a pixel.`

### 1.4 控件类型清单（V+ 与官方一致的部分 + 增强）

**元素 id 枚举**（`VirtualControllerElement.kt`，V+ 与官方同）：
```
EID_DPAD=1, EID_LT=2, EID_RT=3, EID_LB=4, EID_RB=5,
EID_A=6, EID_B=7, EID_X=8, EID_Y=9, EID_BACK=10, EID_START=11,
EID_LS=12, EID_RS=13, EID_LSB=14, EID_RSB=15, EID_GDB=16
```

| 控件 | V+ 是否增强 |
|---|---|
| `DigitalButton`（椭圆/药丸按钮） | ✅ **药丸形状**（`pill = !classic && width > height * 1.2f`）、**多来源按下合并**（见 §1.5）、长按 3s（`postDelayed(longClick, 3000)`） |
| `AnalogStick`（摇杆） | 死区 **0.15×宽**（=30% 半径，与官方 `30%` 一致）、**150ms 死区粘滞**（与官方一致）、双击 350ms 触发 L3/R3 |
| `DigitalPad`（十字键） | 8 方向位标志（官方同） |
| `LeftTrigger/RightTrigger` | 继承 `DigitalButton` |
| **轮盘按键**（README 称 `12.3.7`） | ⚠️ **未在 `virtual_controller/` 目录找到对应文件**，可能实现在 Compose 侧或其它目录。**如实说明：本轮未定位到轮盘源码，不编造其实现。** |

### 1.5 ★ V+ 新增：**多来源按下合并（owners 集合）**

**官方问题**：物理手柄与虚拟按键同时按同一个键时，后到的会覆盖 `inputMap`。
**V+ 解法**（`VirtualController.kt` + `DigitalButton.kt`）：

```kotlin
// VirtualController.kt —— 位图由「所有来源」折叠而成
private val buttonSources = mutableMapOf<Any, Int>()
internal fun setButtonState(source: Any, flags: Int, pressed: Boolean) {
    if (pressed) buttonSources[source] = flags else buttonSources.remove(source)
    controllerInputContext.inputMap =
        buttonSources.values.fold(0) { bits, value -> bits or value }.toShort()
    sendControllerInputContext()
}

// DigitalButton.kt —— 每个按钮记录「谁按着它」
private val owners = mutableSetOf<DigitalButton>()
private fun own(source: DigitalButton, down: Boolean) {
    val previous = isPressed
    if (down) owners.add(source) else owners.remove(source)
    isPressed = owners.isNotEmpty()      // ← 只要还有人按着，就不算松开
    if (previous == isPressed) return    // ← 状态没变就不重复触发回调
    ...
}
```

**⇒ 这是「多指 + 手柄 + 虚拟键」共存的正确做法**，我们如果要做虚拟按键，**必然要抄这个**。

### 1.6 ★ V+ 新增：触感反馈 + 设置按钮可拖拽

| 功能 | 源码位置 | 说明 |
|---|---|---|
| **点击触感** | `VirtualController.performClickHaptic()` | 用 `VibrationEffect.EFFECT_CLICK`（API29+）/10ms 单次（API26+）/**10ms 兜底**三级降级 |
| **震动前置检查** | 同上 | 尊重系统 `Settings.System.HAPTIC_FEEDBACK_ENABLED`（用户关了就不震） |
| **编辑态不震** | 同上 | `if (controllerMode != Active ...) return` |
| **设置按钮可拖拽** | `OscSettingsButtonPositionController` + `OscSettingsButtonStore` | 齿轮按钮位置/大小可用户自定并持久化 |

**⇒ 触感是「手感」的关键，成本极低（几行），强烈建议抄。**

### 1.7 配置档案怎么存

比官方多一层：**布局预设** 与 **元素位置** 分开存。

| 数据 | 存储位置 | 键名 |
|---|---|---|
| 布局预设选择 | `PreferenceManager.getDefaultSharedPreferences` | `list_osc_layout`（`xbox/ds/ns/classic`） |
| 控件位置/尺寸 | Android `SharedPreferences`，键名 `OSC_PREFERENCE` | 每个元素 id 一个 JSON |
| 透明度 | 同上 | `seekbar_osc_opacity`（默认 90） |
| 只显示 L3/R3 | 同上 | `checkbox_only_show_L3R3` |
| 竖屏半高 | 同上 | `checkbox_half_height_osc_portrait` |
| 设置按钮位置/大小 | `OscSettingsButtonStore` | 独立 |

**元素级 JSON 仍然只有 4 个字段**（与官方一致）：
```json
{ "LEFT": 1234, "TOP": 900, "WIDTH": 120, "HEIGHT": 120 }
```
**⇒ V+ 没有把「键位映射」放进 JSON**（它仍是手柄硬编码语义），
**这一点我们不该照抄** —— 我们要做「键鼠映射」，见 §4。

---

## 2. 二、怎么和串流共存（关键技术难点）

### 2.1 ★ 视图层级：控件不是叠在 SurfaceView 上，而是**加进它的父 FrameLayout**

源码 `Game.kt:630-637`：
```kotlin
if (prefConfig.onscreenController) {
    virtualController = VirtualController(
        controllerHandler,
        streamView.parent as FrameLayout,   // ← 父容器，不是 streamView 本身
        this
    )
    refreshVirtualControllerLayout()
}
```

**为什么这样**：`SurfaceView` 是独立 surface（Z 序特殊），把普通 View 直接叠在它上面会有层叠问题；
加到**共同父容器**里，两个 sibling 按 FrameLayout 规则叠放，`SurfaceView` 在下、控件在上。
源码注释（`VirtualControllerElement.kt`）也印证：
> 「Game overlays supply their own contrast; automatic darkening can invert their highlights.」
（`isForceDarkAllowed = false` 防系统深色模式反转控件颜色）

### 2.2 ★ 触摸分发：**背景层吃鼠标，控件层吃自己**

关键两行（`Game.kt:476` 与 `:728`）：
```kotlin
backgroundTouchView.setOnTouchListener(this)
findViewById<View>(R.id.backgroundTouchView)?.setOnTouchListener(this)
```

**分发模型**（Android 标准事件流，源码确认）：
```
FrameLayout（父容器）
  ├─ backgroundTouchView   ← 铺满的全透明 View，吃「空白区」触摸 → 转成鼠标事件
  ├─ SurfaceView           ← 画面（不处理触摸）
  └─ VirtualController 的 elements[]  ← 各控件 View，只在**自己 bounds 内** onTouchEvent 返回 true
       └─ 未消费的触摸会「穿透」给下层（因为 View 返回 false）
```

**核心机制**：每个控件 `VirtualControllerElement.onTouchEvent()` 内部先做
**指针归属判定**（`findPointerIndex(pointerId)`），只处理「属于自己那个指针」的事件：

```kotlin
override fun onTouchEvent(event: MotionEvent): Boolean {
    if (actionMasked == ACTION_DOWN) pointerId = event.getPointerId(0)
    val index = event.findPointerIndex(pointerId)
    if (index < 0) return true                 // 不是我的指针 → 消费掉但不处理（防误触）
    if (actionMasked == ACTION_POINTER_DOWN) return true
    if (actionMasked == ACTION_POINTER_UP && actionIndex != index) return true
    ...   // 只把「我这个指针」的事件抽成单点 MotionEvent 再处理
}
```

**⇒ 这就是「多指同时操作摇杆 + 转视角」不打架的原因：每个控件绑定自己的 pointerId。**

### 2.3 ★ 延迟影响：控件层**几乎不影响串流延迟**

**源码级依据**（不是猜的）：
· 控件是**普通 View** + `onDraw` 画 Canvas（`DigitalButton.onElementDraw` / `AnalogStick`），
  **不参与视频解码/渲染路径**，`SurfaceView` 的 surface 由 MediaCodec 直接投递；
· 解码回调跑在独立线程，控件绘制跑在主线程 —— **两者只竞争主线程 UI 帧**，不竞争解码；
· **唯一的真实风险**：控件多、每帧 `invalidate()` 频繁 → 主线程掉帧。
  但源码里 `invalidate()` 只在**状态变化时**调用（`if (previous == isPressed) return` 提前返回，**不重复触发**）；
· `frameLayout.isMotionEventSplittingEnabled = true`（`VirtualController.init`）—— 开启多点触控拆分。

**[推测]** 在低端机上，若控件数量很多 + 半透明重叠面积大，`onDraw` 的 Canvas 混合会有额外 GPU 开销；
但 V+ 只有 16 个元素 id，且都是简单形状（圆/椭圆/药丸），实测量级应可忽略。**此条未实测，标推测。**

### 2.4 横竖屏/分辨率变化

源码 `createDefaultLayout(controller, context, layoutWidth, layoutHeight)` **每次 refreshLayout 都重新算 scale**；
`Game.kt:1491-1492`：配置变化时重新 `refreshVirtualControllerLayout()`；
`:1621` `virtualController?.refreshLayout()` 也用于窗口尺寸变化。

**⇒ 结论：不做「保存像素坐标」，而是「保存逻辑单位 + 每次按当前尺寸重算 scale」** ——
这正是「竖屏半高 / 横向 88 约束」能生效的原因。

### 2.5 沉浸式全屏

**如实说明**：本轮未在 `Game.kt` 截取范围内定位到 `WindowInsetsController` / `setSystemUiVisibility` 相关代码
（`Game.kt` 超过 100KB 抓取上限，只取到前 2231 行）。**不编造其实现方式。**

---

## 3. 三、输入语义（与我们的落点偏移相关）

### 3.1 ★ 相对/绝对鼠标在 V+ 里怎么区分

**源码确认**：V+ 的 `virtual_controller/` 目录**只处理手柄语义**（`ControllerInputContext` 是 `inputMap/leftTrigger/leftStickX` 等）；
**相对/绝对鼠标不在这个目录**，而在 `ControllerHandler.kt`（100,000B，超抓取上限）与偏好设置里。

**从 release notes 可确认其存在与定位**（V+ v12.15.33 原文）：
> 「新增实体键盘输入支持、虚拟屏幕键盘与本机键盘互斥，以及**相对鼠标和绝对鼠标切换**；
> 其中**相对鼠标适用于游戏画面中的视角旋转，绝对鼠标可以让鼠标更加跟手**，
> **适合远程桌面和需要直接定位指针的场景**。」

且 v12.15.32 有：「添加**绝对相对鼠标切换快捷键 Ctrl+Shift+Alt+M**」、「修复外接鼠标关闭远程桌面模式后仍发送绝对坐标的问题」。

**⇒ 对我们的直接启示（重要）**：

| V+ 的做法 | 我们的现状 |
|---|---|
| **让用户显式选「相对 / 绝对」模式**，并说明各自适用场景 | 我们**用一种模式兼容两者**（绝对 `move` + 相对 `moverel` 混用），结果 T27/T32 的落点偏移就是这种混用的后果 |
| 相对模式 = 视角旋转（游戏） | 我们用 `moverel` 做转视角（方向对） |
| 绝对模式 = 远程桌面指针（跟手） | 我们的 `move` 节流 + 队列导致落点漂移 |
| **模式切换有快捷键** | 我们没有模式概念 |

**★ 这是 V+ 给我们的最重要产品级启示：不要试图让一种模式兼容两种场景，而是让用户选。**

### 3.2 虚拟摇杆输出什么格式

**V+（源码确认）**：`LeftAnalogStick` → `leftStickX = (short)(x * 0x7FFE)` → **手柄轴**（16 位有符号）。
**它不发 WASD**，因为 Moonlight 面向手柄游戏。

**我们该怎么做**（沿用 `MOBILE-CONTROLS-DESIGN-20261122.md` §1 的结论，**本文不重复其数学**）：
左摇杆 → **8 方向扇区 → WASD 的 `keydown`/`keyup`**。
**⇒ 所以 §0③ 那个 `InputAction` 缺 `keydown/keyup` 的缺口，是摇杆能否工作的前提。**

### 3.3 连发（turbo）怎么实现

**未在 V+ 源码中找到 turbo 实现**（`DigitalButton.kt` 只有 `onClick/onLongClick/onRelease` 三个回调 + 3000ms 长按）。
**如实说明**：V+ 的 README 声称有「连发」，但**本轮未定位到其实现**；
可能是通过「长按 + 外部配置」或 Crown 配置脚本实现。[推测]

**我们的实现建议（不依赖 V+）**：连发 = 在 `onClick` 后启动一个固定间隔（如 50~100ms）的协程循环，
反复发 `keydown`→`keyup` 直到 `onRelease`。**这是最容易自己实现的特性。**

### 3.4 触控板模式（整块屏幕当鼠标垫）

**源码确认其存在**：V+ 有「远程桌面模式」（release notes：「修复外接鼠标关闭**远程桌面模式**后仍发送绝对坐标」），
该模式下整屏即触控板。**但具体实现类未定位到**（应在 `ControllerHandler.kt` 的超限部分）。

**我们的实现建议**（参照 `PlayerPlaceholderScreen.kt:157` 现有 `pointerInput` 手势层）：
```
触控板模式 = 整个画面区域的手势 → 每次 MOVE 发 moverel(dx, dy)（增量），
                click → down/up 短按，双指 → wheel
```
**这其实就是我们现有手势层的「相对模式」**，只差一个模式开关。

---

## 4. 四、官方 Moonlight 有没有（对比 V+ 增强）

**有。** 详见已有 `VIRTUAL-CONTROLS-RESEARCH-20261122.md §4`（552 行文档已拆到源码级，**不重复**）。

### 官方 vs V+ 关键差异表（本文增量）

| 维度 | 官方 Moonlight | **V+** | 源码依据 |
|---|---|---|---|
| 主容器代码量 | `VirtualController.java` **7,188B** | `VirtualController.kt` **18,483B（2.57×）** | 两仓库同路径对照 |
| **布局预设** | **无**（一套固定手柄位图） | **4 套：XBOX/DS/NS/CLASSIC** + 强调色 | `VirtualControllerLayout.kt` |
| **多来源按下合并** | 无（后到覆盖） | **`owners` 集合 + `buttonSources` 位图折叠** | `DigitalButton.kt:39-67` |
| **触感反馈** | 无 | **`performClickHaptic()` 三级降级** | `VirtualController.kt` |
| **设置按钮可拖拽** | 固定位置 | **可拖拽 + 可调大小 + 持久化** | `OscSettingsButtonPositionController` |
| 缩放锚定 | `height/72` | **`+ minOf(available, layoutWidth*72/88)`**（更宽屏适配） | `ConfigurationLoader` |
| 竖屏半高 | 无 | **`halfHeightOscPortrait`** | 同上 |
| 按钮边界取整 | 各自乘 scale | **算边界差，防像素重叠** | 同上 |
| 只显示 L3/R3 | 无 | **`onlyL3R3`** 精简模式 | 同上 |
| 摇杆死区/粘滞 | 30% / 150ms | **一致**（未改） | `AnalogStick.kt:45,158` |
| 键位映射可改 | **不可**（硬编码手柄） | **也不可**（同样硬编码） | 两者 JSON 都只存 LEFT/TOP/WIDTH/HEIGHT |
| 许可 | GPL-3.0 | **GPL-3.0** | API |

**⇒ 对「能不能抄代码」的结论（重申已有文档的判断）**：
**GPL-3.0 只能借鉴设计思想，不能抄源码进 SoulLink。** 本文所有 Kotlin 片段仅用于**说明设计**，实现须自己写。

---

## 5. ★ 五、我们能直接抄什么（可落地设计）

### 5.1 数据模型（JSON）

**设计原则**（结合已有 `VIRTUAL-CONTROLS-RESEARCH §5` 的 Schema + V+ 的预设思想）：
1. **坐标用归一化 0~1**（比 V+ 的像素更好持久化，已有文档已论证）；
2. **加 `layoutPreset`**（抄 V+ 的多预设思想）；
3. **必加 `mapping`**（V+ 没有，但我们需要 —— 这是差异化机会，也是我们做键鼠映射的必然）；
4. **加 `owners` 语义**（抄 V+ 的多来源合并）；
5. 支持 `turbo`、`layer`（已有文档 §5 已提，本次并入）。

```json
{
  "version": 1,
  "id": "genshin-mobile",
  "name": "原神（手机习惯）",
  "layoutPreset": "custom",        // xbox | ds | ns | classic | custom
  "opacity": 90,                    // 全局透明度（V+ 同款，非每按键）
  "halfHeightPortrait": true,       // 竖屏只占下半（抄 V+）
  "controls": [
    {
      "id": "stick_left",
      "type": "joystick",           // joystick | button | dpad | trackpad | wheel
      "anchor": [0.10, 0.72],       // 归一化中心点
      "size": [0.20, 0.20],         // 归一化宽高（相对 min(屏宽,屏高)）
      "layer": 1,                   // 同 layer 才允许滑动切换（抄 V+）
      "deadzone": 0.30,             // 占半径比例（V+ 用 0.15×宽=30% 半径）
      "stickyMs": 150,              // 死区粘滞（V+ 同款）
      "mapping": { "kind": "keys8way", "keys": ["W","D","S","A"] }
    },
    {
      "id": "btn_a",
      "type": "button",
      "anchor": [0.86, 0.78],
      "size": [0.11, 0.11],
      "shape": "circle",           // circle | pill（V+: width > height*1.2 → pill）
      "label": "跳",
      "layer": 10,
      "turbo": { "enabled": false, "intervalMs": 80 },
      "mapping": { "kind": "key", "key": "Space" }
    },
    {
      "id": "btn_lt",
      "type": "trigger",            // 长条药丸（V+ pill）
      "anchor": [0.06, 0.06],
      "size": [0.16, 0.06],
      "layer": 20,
      "mapping": { "kind": "mouseButton", "button": "left" }
    },
    {
      "id": "pad_right",
      "type": "trackpad",           // 整块区域 = 相对位移（转视角）
      "anchor": [0.70, 0.35],
      "size": [0.28, 0.55],
      "mapping": { "kind": "moverel", "sensitivity": 1.0 }
    }
  ]
}
```

**与 V+ JSON 的关键差异（我们的增强）**：

| 字段 | V+ | 我们 | 为什么 |
|---|---|---|---|
| `anchor`/`size` | 像素 LEFT/TOP/WIDTH/HEIGHT | **归一化 0~1** | 跨设备天然适配，无需每次重算 scale |
| `mapping` | **无**（硬编码手柄） | **有**（key / keys8way / mouseButton / moverel） | 我们面向**键鼠游戏**，这是本质差异 |
| `layer` | 有 | 有 | 抄：同 layer 才允许滑动切换 |
| `turbo` | README 声称有，**源码未找到** | 有 | 自己实现（定时重发） |
| `layoutPreset` | 有（4 套） | 有 + `custom` | 抄思想，并允许自定义 |

### 5.2 渲染方式：**Compose `Box` + `Canvas`（推荐），不用原生 View**

| 方案 | 优点 | 缺点 | 结论 |
|---|---|---|---|
| **原生 View 叠加**（V+ 做法） | 事件分发成熟、SurfaceView 共存已验证 | 我们要在 Compose 里混用，**需要 AndroidView 包裹 + 手动管理生命周期**，且与现有 `PlayerPlaceholderScreen.kt` 全 Compose 风格冲突 | ❌ |
| **Compose Overlay** ✅ | 与现有代码风格一致（`PlayerPlaceholderScreen.kt` 已是 Compose + `pointerInput`）；**手势分发用 Compose 的 `pointerInput` 天然支持多指**；`Canvas` 画控件简单；`Modifier.offset/alpha` 直接对应 opacity | 需要自己实现 pointerId 归属（但 Compose 提供 `awaitPointerEventScope` 与 `PointerId`） | ✅ **推荐** |

**关键理由（源码依据）**：
我们现有代码 `PlayerPlaceholderScreen.kt:156-170` 已经是：
```kotlin
Box(modifier.pointerInput(Unit) {
    val event = awaitPointerEvent()
    ...
})
```
**⇒ 同一套机制可以扩展成控件层**，不必引入 View 体系。

**但有一个必须注意的坑**（源码确认）：
`PlayerPlaceholderScreen.kt:124` 注释写着：「手势层：覆盖 SurfaceView（**SurfaceView 是传统 View，不吃 Compose 手势**）」
⇒ 我们**已经**用 Compose Box 盖在 SurfaceView 上并收到了手势，**说明这条路是通的**。
控件层就是在这层 Box 内部再加子 Box，各自 `pointerInput` 即可。

### 5.3 触摸分发的关键代码思路

**核心：每个控件只处理「落在自己范围内且未被更上层消费」的指针。**

```kotlin
// 契约：返回 true = 我消费了这个指针（后续 move/up 都归我）
fun Modifier.controlGesture(
    bounds: Rect,
    onDown: (PointerId, Offset) -> Unit,
    onMove: (PointerId, Offset) -> Unit,
    onUp: (PointerId) -> Unit,
) = pointerInput(bounds) {
    awaitEachGesture {
        val down = awaitFirstDown(requireUnconsumed = true)   // ← 关键：只接未被消费的
        if (!bounds.contains(down.position)) return@awaitEachGesture
        down.consume()                            // ← 标记已消费，下层收不到
        val pid = down.id
        onDown(pid, down.position)
        var last = down.position
        while (true) {
            val ev = awaitPointerEvent()
            val ch = ev.changes.firstOrNull { it.id == pid } ?: break
            if (!ch.pressed) { onUp(pid); break }
            ch.consume();
            onMove(pid, ch.position)
            last = ch.position
        }
    }
}
```

**与 V+ 的对应关系**：

| V+ 的机制 | Compose 等价物 |
|---|---|
| `pointerId` 归属（`findPointerIndex`） | `awaitFirstDown().id` + `changes.firstOrNull { it.id == pid }` |
| `onTouchEvent` 返回 true 消费 | `change.consume()` |
| 未消费穿透下层 | `requireUnconsumed = true` / 不 consume |
| `owners` 多来源合并 | 自己维护 `MutableSet<PointerId>` |

### 5.4 默认布局预设（3 套）

| 预设 | 控件 | 适用 |
|---|---|---|
| **桌面操作** | 左下「触控板」（整块左半 = moverel）、右下 左/右键、无摇杆 | 远程办公/挂机点按 |
| **FPS** | 左摇杆（WASD）+ 右下视角触控板 + 开火键 + 蹲/跳/换弹 | 射击类 |
| **3D 游戏（原神类）** | 左摇杆（WASD）+ 整屏滑动转视角（**不用右摇杆**，已有文档 §6 已论证）+ 跳跃/交互键 | 键鼠 3D 游戏 |

**注意**：第 3 套**不用右摇杆**这一点，是已有 `VIRTUAL-CONTROLS-RESEARCH §6` 经用户明确定的，
**不要因为抄 V+ 就退回右摇杆**（V+ 是手柄语义，我们是键鼠语义）。

### 5.5 工作量估计（分阶段）

| 阶段 | 内容 | 预估 |
|---|---|---|
| **P0 前置** | 补 `InputAction` 的 `Key/KeyDown/KeyUp/Text` 四个类 + `InputSender` 分支 | **0.5 天**（纯样板，风险低） |
| **A 最小可用** | 固定位置 + 固定数量的按钮（如 4 个：左/右键、跳、ESC）；无拖动、无配置、无摇杆 | **1~1.5 天** |
| **B 完整** | 可拖动/缩放编辑态 + JSON 持久化 + 摇杆（8 方向）+ 透明度 + 多预设切换 + owners 合并 | **3~4 天** |
| **C 锦上添花** | 触感反馈 + turbo 连发 + 分享码 + 按游戏自动切换 + 轮盘 | **3~5 天** |

**（工期为 [推测] 量级估计，基于我们现有代码风格与 Compose 熟悉度；实际以排期为准。）**

---

## 6. 三档清单（可直接排期）

### A 档 —— 最小可用（1.5~2 天）

| # | 内容 | 解决什么 | 代价 |
|---|---|---|---|
| **A0** | **补 `InputAction` 的 `Key/KeyDown/KeyUp/Text`** + `InputSender` 分支 | **虚拟按键的硬前提**（后端已支持，只差客户端） | 0.5 天，纯样板 |
| **A1** | 固定布局的 4~6 个按钮（Compose `Box` + `pointerInput`） | 「能点虚拟键」 | 1 天 |
| **A2** | 空白区触摸**透传**给现有手势层（`requireUnconsumed`） | 不破坏现有鼠标操作 | 半天（与 A1 一起） |

**A 档做完即可交付「有虚拟按键」**，且不动现有串流链路。

### B 档 —— 完整（3~4 天）

| # | 内容 | 解决什么 | 依据 |
|---|---|---|---|
| **B1** | 编辑态（移动/缩放）+ JSON 持久化 | 用户自定义布局（用户明确要求「可以改位置、可以保存位置、可以删除添加」） | 抄 V+ `ControllerMode` 三态 + `edit()` |
| **B2** | 虚拟摇杆（8 方向 → WASD keydown/keyup） | 移动操作 | 已有文档 §1 数学 + V+ 死区粘滞 |
| **B3** | 多预设切换（XBOX/DS/NS/custom 或按场景） | 全面对标 V+ 最显眼的增强 | 抄 V+ `VirtualControllerLayout` |
| **B4** | `owners` 多来源按下合并 | 多指 + 虚拟键不打架 | 抄 V+ `DigitalButton.owners` |
| **B5** | 全局透明度滑杆 | 用户明确要求「半透明」 | V+ `seekbar_osc_opacity` |

### C 档 —— 锦上添花（3~5 天）

| # | 内容 | 依据 |
|---|---|---|
| **C1** | 触感反馈（`performClickHaptic` 三级降级） | V+ 同款，成本极低，手感提升明显 |
| **C2** | turbo 连发 | V+ README 声称有但**源码未找到**，需自研（定时 keydown/keyup） |
| **C3** | 相对/绝对鼠标**模式切换**（+快捷入口） | ★ V+ 的产品级启示，**直接对应我们的落点偏移** |
| **C4** | 布局分享码 | 已有文档 §5.3 已设计 |
| **C5** | 按游戏自动切换预设 | 已有文档 §5 已提 |
| **C6** | 轮盘按键 | ⚠️ **V+ 源码本轮未定位到轮盘实现**，需自研 |

---

## 7. 我的判断：最该先做的三件

1. **A0（补 InputAction 的 key 系列）** —— **这是全部虚拟按键的地基，且后端早就支持了，只差客户端 5 行**；
2. **A1+A2（固定按钮 + 透传）** —— 最快看到「有虚拟按键」的效果，不动串流链路；
3. **C3（相对/绝对鼠标模式切换）** —— 虽然归在 C 档，但**它直接对应我们的落点偏移问题**，
   而且 V+ 证明了「让用户选模式」比「用一种模式兼容两者」更正确。**建议提前做。**

**关于「轮盘按键」「连发」**：V+ README 声称有，但**本轮未在源码中定位到实现**。
我不编造它们长什么样 —— **如实标注为「未找到」，建议自研而非对标。**

---

## 8. 调研方法与可信度自评

### 成功获取（源码级，可引用）

| 来源 | 内容 |
|---|---|
| `ghproxy.net` 代理 raw.githubusercontent | **V+ 6 个 virtual_controller 源文件全文**（共约 54KB）+ 官方 `VirtualController.java` |
| V+ `VirtualController.kt`（18,483B） | 多预设切换、owners 位图、触感、设置按钮拖拽、编辑态 |
| V+ `VirtualControllerLayout.kt`（395B） | **4 套布局预设枚举全文** |
| V+ `VirtualControllerConfigurationLoader.kt`（12,440B） | 缩放算法、预设配色、边界取整、半高、onlyL3R3 |
| V+ `VirtualControllerElement.kt`（6,855B） | 16 个元素 id、三态编辑、pointerId 归属、拖动/缩放 clamp |
| V+ `DigitalButton.kt`（9,318B） | owners 合并、checkMovement 同 layer 判定、药丸、长按 3s |
| V+ `AnalogStick.kt`（7,173B） | 死区 0.15×宽、150ms 粘滞、双击 350ms、cap 半径 |
| V+ `Game.kt`（前 2231 行） | `VirtualController(... streamView.parent as FrameLayout ...)`、`backgroundTouchView.setOnTouchListener` |
| V+ `app/build.gradle` | 启用 `kotlin.compose` 插件（说明 V+ 已 Compose 化） |
| V+ release notes | 相对/绝对鼠标的官方表述、Ctrl+Shift+Alt+M 快捷键 |
| 本地源码 | `InputAction.kt`、`PlayerPlaceholderScreen.kt:124/156-170`、`InputSender.kt`、`RelayAgent.cs:1021/1039/1040/1120` |
| 本地已有调研 | `VIRTUAL-CONTROLS-RESEARCH-20261122.md`（552 行）、`MOBILE-CONTROLS-DESIGN-20261122.md` |

### 未能获取（已在正文标注）

| 目标 | 状态 |
|---|---|
| **轮盘按键实现** | **未定位到源码文件**；README 声称有。**不编造** |
| **连发（turbo）实现** | **未在 `DigitalButton` 找到**；README 声称有。**不编造** |
| `ControllerHandler.kt` | **超 100KB 抓取上限**（相对/绝对鼠标应在其中） |
| `Game.kt` 沉浸式全屏部分 | 仅取到前 2231 行，**未定位** `WindowInsetsController` |
| `PlayStore` 截图/控件外观 | 未获取（无法看图，**不描述控件长什么样**） |
| `api.github.com` 直连 | **403 限流**（60/60 用尽，需等 38 分钟） |
| `raw.githubusercontent.com` / `cdn.jsdelivr.net` | **fetch failed** |
| GitHub 网页源码视图 | SPA 外壳，HTML 无源码 |

### 推测标注汇总

| 结论 | 性质 |
|---|---|
| 控件层对延迟影响可忽略（低端机大量半透明控件可能有 GPU 开销） | **[推测]**，未实测 |
| 工期估计（0.5 / 1~1.5 / 3~4 / 3~5 天） | **[推测]**，量级估计 |
| V+ 轮盘/连发可能实现在 Compose 侧或 Crown 配置 | **[推测]**，未证实 |

**其余全部结论均有 `文件:行号` 或源码全文出处。**

### 许可合规声明
· Moonlight V+ 与官方 Moonlight 均为 **GPL-3.0**；
· 本文引用的 Kotlin 片段**仅用于说明设计思想**；
· **实现 SoulLink 侧必须自己写，不得复制 GPL 源码。**
（该约束已有 `VIRTUAL-CONTROLS-RESEARCH-20261122.md §7` 明确记录，**本文重申**。）

---

**文档作者**：yomori-scout
**最后更新**：2026-11-24
**未改任何代码**（本任务为纯调研）