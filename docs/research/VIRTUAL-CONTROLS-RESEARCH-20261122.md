# 虚拟按键 / 摇杆 / 键位映射研究（VIRTUAL-CONTROLS-RESEARCH）

> 日期：2026-11-22
> 作者：virtual-controls（agent-team）
> 前置：`MOBILE-REMOTE-PLAN-20261122.md`（路线/手势/最小改动集）、`MOBILE-CONTROLS-DESIGN-20261122.md`（摇杆数学 + 布局 JSON 初稿）
> 范围：**只读研究 + 写本文档**，不改业务代码、不动 dist/、不提交、不发版、不做逆向。
> 任务来源：用户点名「GIT 上成熟远程项目、能转视角的那种远程模式；UU 这块不错、虚拟按键也很好，看看有没有资料」。

---

## 0. 结论摘要（TL;DR）

> **v1.1 修订（2026-11-22）**：修正「转视角」交互结论。原 v1.0 称「转视角 = 右摇杆 + 相对位移」，经用户否决——**错误**。正确结论：**SoulLink 面向 PC 键鼠游戏，手机端转视角 = 直接在画面上滑动手指（映射 `moverel` 相对位移）**，与手机端原神/永劫无间等手游一致。左摇杆仅用于移动。详见 §6（已重写）与 §10（手势判定参数）。

1. **最值得借鉴的开源项目是 Moonlight Android 的 On-Screen Controls（OSC）**，它是唯一一个「开源 + 已被数千万用户验证 + 直接覆盖『虚拟摇杆 + 按键 + 可编辑布局 + 手柄位图』」的完整参考。但它是 **GPL-3.0**——只能借鉴**设计思想与数据结构**，不能抄代码进闭源/自有许可的 SoulLink。
2. **「转视角」的交互 = 直接在画面上滑动手指（相对位移 `moverel`）**，**不是右摇杆**。Moonlight 面向「手柄游戏串流」，右摇杆映射手柄轴（`rightStickX/Y`）是合理的；但 SoulLink 面向「PC 键鼠游戏」，手游玩家转视角的习惯是**在右侧屏幕上滑动**（原神/永劫无间手游均如此），滑一下抬手、不会持续挡画面。滑动映射到 `moverel`（`RelayAgent.Execute` L694 已实现、只差 HTTP 接线，见 `MOBILE-REMOTE-PLAN §4.5 改动 3`）。**左摇杆仍用于移动**（方向键），摇杆数学（死区粘滞、八方向扇区）继续保留。详见 §6 与 §10。
3. **UU 远程（uuyc.163.com）确认是「远程串流 + 按键映射」产品**，公开资料明确写出「超 300 款按键方案、增减按键、调整大小/透明度/命名、宏按键、一键多键、3D 视角控制（按住左/右/中键滑动转视角）、官方方案分享码导入」。**它不开源**，无逆向，但公开功能清单已足够我们对照设计。
4. **数据模型已成熟**：我们的 `MOBILE-CONTROLS-DESIGN §3` 布局 JSON 方向正确，但缺三样 Moonlight/UU 都有的东西——**① 图层（layer）、② 长按/连发/宏、③ 分享码（序列化 + base64/短码）**。本文 §5 给出补齐后的完整 Schema。
5. **许可风险明确**：Moonlight = GPL-3.0（**不可抄代码**）；KeyMapper = GPL-3.0（同样只借鉴思想）；nipplejs / bobboteck JoyStick = MIT（可安全引用，nipplejs 是纯前端摇杆的现成候选）；Shizuku 系（android-open-mapper 等）= 部分无明确许可（NOASSERTION），**默认不可用**。

---

## 1. 为什么需要研究虚拟按键（背景回顾）

SoulLink 是 Windows 多账号 RDP 挂机工具，正在做远程控制。核心痛点是三件套之一：

| 痛点 | 现状 | 关联文档 |
|---|---|---|
| 3D 游戏转视角 | 只有绝对坐标 `move`（`SetCursorPos`），游戏锁鼠标后转不了视角；相对位移 `moverel` 已在 `RelayAgent.Execute` L694 实现但 HTTP 层未接线 | `MOBILE-REMOTE-PLAN §4.5` |
| 虚拟按键/摇杆 | 手机上要像手游一样操作（已有设计 `MOBILE-CONTROLS-DESIGN`，但要参考成熟产品） | `MOBILE-CONTROLS-DESIGN` |
| 画面传输 | RDP 会话 DXGI 不可用，BitBlt 抓屏 22~27ms，60fps 做不到 | `STREAM-PERF-AUDIT-20261122` |

本文聚焦**第二点**：虚拟按键/摇杆/键位映射怎么设计，向谁学。

---

## 2. UU 远程及其他产品的公开资料梳理

### 2.1 UU 远程（网易，uuyc.163.com）——用户点名的产品

**结论：确认存在且「按键映射」是核心卖点之一。不开源，只能看公开产品页 + 功能清单。**

公开资料（来自官网 https://uuyc.163.com/ 及其 `/features/keyboard/` 子页，2026-11-22 抓取）梳理如下：

**产品定位**：网易出的远程控制软件，主打「远程办公 / 远程游戏 / 远程协助」，官网明确写「基础远控免费、不限次不限时、4K 144 帧、端到端加密」。

**按键映射（/features/keyboard/）公开功能清单**：

| 功能 | 原文表述 | 对我们的启示 |
|---|---|---|
| 开箱即用方案 | 「超 300 款按键方案，用手机也能畅玩 3A 大作」「官方为各新老游戏提供专属键位方案，点击即用」 | 预设方案库 + 官方维护，是产品化的关键 |
| 触屏方式操作键鼠手柄 | 「在手机、平板屏幕上显示电脑按键，以触屏方式完成键盘、鼠标、手柄操作」 | 按键映射本质 = 触屏 → 键/鼠/手柄事件 |
| 编辑能力 | 「增减按键，调整大小、透明度、命名等设置」 | **用户已经要求的：半透明、可拖动、可自定义备注** ✅ 与我们的需求完全一致 |
| 方案分享 | 「复制分享码 / 输入分享码导入」「编辑官方方案并分享」 | 分享码（短码导入）是成熟产品的标配 |
| 3D 视角控制 | 「支持左键、右键、中键按住滑动视角」「鼠标移动灵敏度、按键按下移动鼠标灵敏度、滚轮灵敏度可调」 | **转视角 = 按住某键（如右键）+ 相对位移**，与 SoulLink `moverel` 语义一致 |
| 智能细节 | 「显示鼠标时自动隐藏虚拟按键」「锁定鼠标时禁用鼠标点击」「可关闭智能游戏鼠标防漂移」 | 我们也要做「游戏锁鼠标时的禁用/隐藏」策略 |
| 一键多键 / 宏 | 「多个按键组合，一键顶多键」「编辑操作脚本，一键释放连招」「宏按键在功能键中选取添加」 | 数据模型要支持：组合键 + 宏（键序列/脚本） |
| 键位覆盖 | 「支持映射全键鼠按键，包括小键盘、鼠标侧键」 | 键名表要覆盖小键盘 NumPad、鼠标侧键 XButton |
| 手柄 | 「支持 XBOX、PS、小鸡、北通…蓝牙手柄 + 虚拟手柄 + 手柄震动」 | 手柄直连是另一条输入线（可选中期项） |

**找不到的（诚实声明）**：
- 不开源、无源码；官网无 API 文档、无技术博客。
- 无布局文件格式、无数据结构、无摇杆数学等实现细节。
- 本机网络限制下，Bing/百度中文搜索对「UU 远程 虚拟按键」的精准评测文章检索结果被泛化（命中"网易"门户），**未能拿到第三方图文评测的按键布局截图**。产品页已提供足够的功能面信息。

### 2.2 Steam Link / Steam Remote Play（Valve，不开源，但有公开文档）

- Steam Link 的触屏控制（touch controls）**同样不开源**（客户端闭源），但社区文档与商店描述公开。
- 公开已知：Steam Link 支持「触控布局（touch layout）」，把屏幕划分成虚拟按键/摇杆/触控板，可自定义、可在 Steam 社区分享/订阅布局。这是「官方预设 + 社区分享」模式的另一例证。
- **对我们没有可抄的代码**，只有产品形态参考：**布局可分享、可订阅、按游戏自动匹配**。

### 2.3 其他同类商业产品（ToDesk / 向日葵 / Parsec / 网易 MuMu 等）

- 用户原话「ToDesk / 向日葵 / Parsec 那种像远程控制的手感」（见 `MOBILE-REMOTE-PLAN`）。
- 这些产品**均闭源**；公开资料主要描述「延迟低、帧率高、有虚拟键盘」，但**虚拟摇杆/游戏键位映射能力普遍弱于 UU / Moonlight**（ToDesk 偏办公、向日葵偏运维，游戏键位映射不是其强项）。MuMu 模拟器（网易）有键位映射但那是「PC 玩 Android」，方向相反，不适用。

**小结**：产品形态上，UU 远程（串流 + 游戏键位映射）是**最接近 SoulLink 远程形态**的参照物；技术上可借鉴的开源实现是 **Moonlight**（见 §3）。

---

## 3. 开源项目对比表

| 项目 | 星数 | 语言 | 许可 | 虚拟按键能力 | 借鉴价值 |
|---|---|---|---|---|---|
| **moonlight-stream/moonlight-android** | ~7.6k | C/Java | **GPL-3.0** | ★★★★★ 完整 OSC：数字按键/十字键/左右摇杆/扳机，可拖动可缩放，布局 JSON 持久化 | **最高**（设计+数据结构参考，不可抄代码） |
| keymapperorg/KeyMapper | ~2.9k | Kotlin | **GPL-3.0** | ★★★★ Android 无障碍服务按键重映射（按键→触摸/动作），有自定义动作与配置文件 | 中高（键位表达模型参考，GPL 限制） |
| yoannmoinet/nipplejs | ~2.0k | TypeScript | **MIT** | ★★★ 纯前端虚拟摇杆（dynamic/static/semi 三种模式、死区 threshold、多指） | **高且可安全引用**（Web 前端摇杆现成库） |
| bobboteck/JoyStick | ~416 | JavaScript | **MIT** | ★★ 简单 HTML5 Canvas 摇杆（鼠标+触屏） | 中（轻量参考，可安全引用） |
| Shrey113/Android-Dex | ~4.0k | HTML/Flutter | NOASSERTION | ★★★★ DeX 替代，含 FPS 游戏键鼠映射 | 低（方向相反 + 许可不明） |
| kinou-p/android-open-mapper | ~39 | Kotlin | NOASSERTION | ★★★ Shizuku 免 root 手柄映射（Mantis/Panda 开源替代） | 低（许可不明，默认不可用） |
| Shyri/TouchMapper | ~91 | Java | **GPL-3.0** | ★★ 手柄→触摸映射（Android TV） | 低（方向相反 + GPL） |
| akbartaimurr/WmapperKeymapper | ~43 | — | MIT | ★★ WSA 键鼠→触摸 | 低（方向相反） |
| 114514hhha233/shizuku-keypad | ~6 | Kotlin | MIT | ★★★ 可自由摆放的 Android 虚拟键鼠悬浮层（Shizuku 注入） | 低-中（思路可参考，但与 SoulLink 的 C# 注入栈不同） |
| dansunmi/GlassPad | ~5 | C# | MIT | ★★ Windows 透明触屏游戏手柄覆盖层 | 中（同为 C#/Windows，可参考触屏→虚拟手柄的映射表达） |
| Gtxplosiom/TBVGPE | ~17 | C# | MIT | ★★ Windows 虚拟游戏手柄覆盖层 | 中（同上） |
| Squarific/Touchscreen-Joystick 等 | <50 | JS | 各异 | ★ 简单 HTML5 摇杆 | 低 |

> 说明：星数/语言/许可来自 GitHub API 实时查询（2026-11-22）。「借鉴价值」综合了方向契合度、许可可用性、实现完整度。

---

## 4. Moonlight On-Screen Controls 详细拆解（最有价值的参考）

> 源码位置（master 分支）：`app/src/main/java/com/limelight/binding/input/virtual_controller/`，共 11 个文件。
> 许可：**GPL-3.0**。以下只拆解**设计与数据结构**，不引用任何可直接粘贴的代码。

### 4.1 架构分层

```
VirtualController（容器/协调者）
  ├─ VirtualControllerElement（抽象基类：View + 拖动/缩放 + JSON 配置）
  │    ├─ DigitalButton   （椭圆按钮，单击/长按/释放）
  │    ├─ DigitalPad      （十字键 dpad，8 方向位标志）
  │    ├─ LeftTrigger / RightTrigger（扳机，继承 DigitalButton）
  │    └─ AnalogStick     （模拟摇杆基类）
  │         ├─ LeftAnalogStick   → leftStickX/Y
  │         └─ RightAnalogStick  → rightStickX/Y
  └─ VirtualControllerConfigurationLoader（默认布局 + 保存/加载 JSON）
```

**关键设计**：每个元素都是一个 Android `View`，统一拥有「Normal / Move / Resize」三种编辑模式，通过 `getConfiguration()`/`loadConfiguration()` 做 JSON 序列化。**编辑态与运行态用同一个对象**，只是 `ControllerMode` 切换。

### 4.2 布局数据结构（JSON）

Moonlight **不用 XML 布局文件**，而是**每个元素一个 JSON 对象**，存在 Android `SharedPreferences`（键名 = 元素 id，如 `"6"` 代表 EID_A 按钮）。

元素级配置（`VirtualControllerElement.getConfiguration()`）**只有 4 个字段**：

```json
{ "LEFT": 1234, "TOP": 900, "WIDTH": 120, "HEIGHT": 120 }
```

- 坐标是**像素绝对值**（`leftMargin/topMargin`），不是归一化 0~1。
- 尺寸也是像素。
- **没有**记录键位映射——因为 Moonlight 的键位是**硬编码**的（每个元素 id 对应固定的 Xbox 手柄按钮/轴），用户只能**移动/缩放位置**，**不能改「这个键映射成什么」**。

**默认布局**（`createDefaultLayout`）用一套 **128×72 逻辑网格（16:9）** 坐标常量硬编码：

```java
DPAD_BASE_X=4, DPAD_BASE_Y=41, DPAD_SIZE=30
ANALOG_L_BASE_X=6, ANALOG_L_BASE_Y=4, ANALOG_R_BASE_X=98, ANALOG_R_BASE_Y=42, ANALOG_SIZE=26
BUTTON_BASE_X=106, BUTTON_BASE_Y=1, BUTTON_SIZE=10   // A/B/X/Y 面键围绕 Y 按钮排列
TRIGGER_* / START_X / BACK_X / GUIDE_X ...
```

并通过 `screenScale(units, height)` = `height/72 * units` 做**跨分辨率等比缩放**（按屏幕高度比例，非宽度），右侧控件再加 `rightDisplacement = width - height*16/9` 适配不同长宽比。

**对我们最重要的启示**：
1. **默认布局用「逻辑网格 + 屏幕比例缩放」而非写死像素** —— 比我们 `MOBILE-CONTROLS-DESIGN` 的「归一化 0~1」多一种思路：归一化 0~1 更简单、更好持久化；Moonlight 用像素 + 高度比例是因为它要适配不同长宽比的 Android 设备。**建议我们保持归一化 0~1**（更简单、跨设备天然适配），但**补充「逻辑分辨率 + 高宽比锚点」**（见 §5）。
2. **元素 id 固定枚举（EID_A=6 等）**，键位与 id 绑定，布局 JSON 只存「位置+尺寸」。
3. **布局编辑能力只有「移动 + 缩放」两件事**，没有「改键位」——这正说明「可自定义备注/可改键」是 UU 比 Moonlight 更进一步的卖点，也是**我们的差异化机会**。

### 4.3 摇杆实现（AnalogStick）——手势 → 轴值

Moonlight 摇杆的数学（拆解 `AnalogStick.java`）：

1. **三个半径**（随控件尺寸按比例计算）：
   - 外圈 `radius_complete = 控件宽/2`（减去描边）
   - 死区 `radius_dead_zone = 控件宽/2 × 30%`
   - 摇杆头 `radius_analog_stick = 控件宽/2 × 20%`
2. **触点 → 极坐标**：`relative = touch - center`，`movement_radius = sqrt(x²+y²)`，`movement_angle = atan2 分象限求`。
3. **钳制**：`radius > (radius_complete - radius_analog_stick)` 时钳到外圈内边缘（knob 不画出圈）。
4. **归一化输出**：`x = -correlated_x/complete`，`y = correlated_y/complete`，范围 **-1.0 ~ +1.0**。
5. **死区粘滞**：`stick_state` 三态（NO_MOVEMENT / MOVED_IN_DEAD_ZONE / MOVED_ACTIVE）。**一旦进入 ACTIVE，即使手指回到死区也不立即回中**（`stick_state == MOVED_ACTIVE || eventTime-lastClick > 150ms || radius > deadzone`）——这是为了「精细操作时不会因为轻微回中而瞬间松键」。150ms 是「按住一段时间后解除死区」的阈值。
6. **双击**：350ms 内第二次 down 触发 `onDoubleClick`（左摇杆双击 = L3 按下，右摇杆双击 = R3 按下）。

**输出映射**（`LeftAnalogStick`/`RightAnalogStick`）：
- 左摇杆：`leftStickX = (short)(x * 0x7FFE)`，`leftStickY = (short)(y * 0x7FFE)` → 手柄轴（16 位有符号模拟量）。
- 右摇杆：`rightStickX/Y` 同法。
- 十字键 `DigitalPad`：8 方向位标志（`LEFT=1, UP=2, RIGHT=4, DOWN=8`，对角线 = 或运算），触点按「左/右 1/3、上/下 1/3」分 3×3 网格判定方向。

**对 SoulLink 的映射（关键差异，v1.1 修正）**：
- Moonlight 目标是「把触屏变成 Xbox 手柄」，最终发手柄轴/按键包（`ControllerPacket` 位图 + 轴值）。
- SoulLink 目标是「把触屏变成 **PC 键鼠**」，所以：
  - **左摇杆 → 8 方向键（WASD）的 keydown/keyup**（`MOBILE-CONTROLS-DESIGN §1` 已给出 8 方向扇区数学，可直接复用）——**左摇杆保留**，只用于移动。
  - **转视角 ≠ 右摇杆，而是「画面滑动 → `moverel` 相对位移」**（`RelayAgent.Execute` L694 已有，接 HTTP 即可）。Moonlight 的 `RightAnalogStick`（手柄轴）不适用于键鼠游戏；滑动映射到相对位移才符合手游习惯（详见 §6、§10）。
  - 死区粘滞（150ms 解除死区）这个细节**值得抄给左摇杆**：能显著改善「轻推摇杆移动」的手感，避免方向键抖动。

### 4.4 用户能不能自定义？怎么存、怎么编辑

- **能，但只有「移动位置 + 缩放大小」**（不能改键位、不能改备注）。
- 编辑模式：三态 `Active → MoveButtons → ResizeButtons`（循环切换，由顶栏按钮触发）。
- 每个元素 `onTouchEvent` 在编辑态里把 touch 解释成 `moveElement`/`resizeElement`（改 `LayoutParams`）。
- **保存**：`saveProfile()` 把每个元素 `getConfiguration().toString()`（JSON）写进 `SharedPreferences`；`loadFromPreferences()` 反向读回，损坏项自动删除。
- **透明度**：全局一个 `oscOpacity`（0~100），`setOpacity()` 把透明度写进元素颜色 alpha 通道（`hexOpacity = opacity*255/100`）。**是全局透明度，不是每按键独立**（UU 也是「透明度应用于所有键位」——两者一致，可参考）。
- **层（layer）**：`DigitalButton` 有 `layer` 字段（面键=1，BACK/START=2/3），用于**滑动时跨按钮判定**（手指从 A 滑到 B，只有同 layer 才切换按下状态）。这是「手指滑动扫过多个按键」时的正确性处理，**值得借鉴**。

### 4.5 能借鉴多少？（许可约束下的结论）

| 部分 | 可否借鉴 | 方式 |
|---|---|---|
| 元素分层（按钮/十字键/摇杆/扳机）+ 统一「移动/缩放」编辑模式 | ✅ | 设计思想，用自己的 C#/Web 实现 |
| 摇杆三半径 + 死区粘滞 + 双击 | ✅ | 数学与交互逻辑，`MOBILE-CONTROLS-DESIGN §1` 已覆盖大半，补充死区粘滞 |
| 布局 JSON（LEFT/TOP/WIDTH/HEIGHT） | ✅ | 数据结构思想；我们已有更完整的 Schema，替换为归一化坐标 |
| 默认布局「逻辑网格 + 高度比例缩放」 | ✅ | 思想；我们可提供「逻辑分辨率 + 归一化」方案 |
| 十字键 8 方向位标志 | ✅ | 思想；我们已有 8 方向扇区数学 |
| **任何 Java 源码** | ❌ | **GPL-3.0，禁止抄入 SoulLink** |

---

## 5. 键位映射数据模型建议（JSON Schema）

> 综合 Moonlight（元素分层 + 图层 + 死区粘滞）、UU（可改键/命名/透明度/宏/一键多键/分享码）、`MOBILE-CONTROLS-DESIGN §3`（已有方向键/摇杆/按钮/文本 + 归一化坐标），给出**补齐后**的 Schema。

### 5.1 一个「键」能表达什么

一个按键/控件 = 下面能力之一或组合：

| 能力 | 说明 | 对应输出动作 |
|---|---|---|
| 单键 | `W`、`Space`、`NumPad1`、`XButton1`(鼠标侧键) | `keydown` / `keyup` / `key`(点按) |
| 组合键 | `Ctrl+Shift+A`、`W+D`（斜向移动） | 按下时顺序按下各成员，抬起时逆序抬起 |
| 长按 | 按住不放（跳跃蓄力/冲刺） | `keydown`(按下) → ... → `keyup`(抬起) |
| 连发 | 快速点按（普攻） | `key` + `count` + `interval`（复用 `RelayAgent` `click count`） |
| 宏/脚本 | 一段键序列或延时脚本（一键连招） | 宏引擎（中期项，先存数据结构） |
| 摇杆轴 | 8 方向键（移动） | dpad8 → keydown/keyup |
| 画面滑动（转视角） | 在画面上滑动 → 相对位移 | `moverel`（dx/dy），可选 `holdButton`（按住右键滑动）——v1.1 定案 |
| 鼠标键 | 左/右/中键按下/抬起/点按 | `down`/`up`/`click` |
| 文本 | 整段文本（中文聊天） | `text`（需代理新增，见 `MOBILE-REMOTE-PLAN §4.5`） |

### 5.2 Schema（v2 建议，补齐 layer/长按/连发/宏/分享）

```jsonc
{
  "$schema": "https://soullink.local/schema/controls-layout-v2.json",
  "version": 2,
  "name": "原神-预设",                 // 方案名（UU 的「命名」）
  "screen": { "logicalWidth": 1920, "logicalHeight": 1080 },  // 逻辑分辨率，坐标归一化

  // 全局设置（对标 Moonlight/UU 的「全局透明度」）
  "global": {
    "opacity": 0.55,                    // 0~1，应用于所有元素（也可被元素覆盖）
    "hideWhenMouseVisible": true,       // UU：「显示鼠标时自动隐藏虚拟按键」
    "disableClickWhenMouseLocked": true, // UU：「锁定鼠标时禁用鼠标点击」

    // ★ v1.1：转视角 = 画面滑动（非摇杆），相关手势参数全局化
    "look": {                            // 转视角（滑动 → moverel 相对位移）
      "sensitivity": 1.0,                // 灵敏度（1.0 = 屏幕位移 1px → moverel 1px）
      "maxStep": 40,                     // 单帧最大相对位移（防止过大被系统截断）
      "holdButton": null                 // 可选：滑动转视角时按住某键（如 "right"，部分游戏需要）
    },
    "gesture": {                         // 三态手势参数（详见 §10）
      "slop": 12,                        // 位移阈值 px（touch；mouse=8）
      "longPress": 250,                  // 长按阈值 ms
      "dragConfirm": 16                  // 长按后进入拖动的确认位移 px
    }
  },

  "elements": [
    {
      "id": "joy-move",
      "type": "joystick",
      "mode": "dpad8",                  // ★ v1.1：仅移动（8 方向键）；视角不再用摇杆
      "layer": 1,                       // ★ 新增：滑动跨键判定层（Moonlight）
      "center": { "x": 0.12, "y": 0.80 },
      "radius": 0.14,                   // 相对屏宽
      "deadzone": 0.20,                 // 占 radius 比例
      "deadzoneSticky": 150,            // ★ 新增：按住 ms 后解除死区（Moonlight 死区粘滞）
      "map": {
        "N": "W", "NE": "W+D", "E": "D", "SE": "S+D",
        "S": "S", "SW": "S+A", "W": "A", "NW": "W+A"
      },
      "run": { "key": "Shift", "threshold": 0.70 }   // 可选：幅度阈值切跑步键
    },

    {
      "id": "btn-attack",
      "type": "button",
      "mode": "tap",                    // tap | hold | taprepeat | macro
      "layer": 1,
      "center": { "x": 0.78, "y": 0.62 },
      "size": { "w": 0.07, "h": 0.12 },
      "opacity": 0.5,                   // ★ 可选：单键覆盖全局透明度（UU 命名+透明度）
      "map": { "action": "click", "button": "left" },
      "label": "攻击",                  // ★ 命名（UU「命名」）
      "icon": "⚔"
    },

    {
      "id": "btn-dash",
      "type": "button",
      "mode": "hold",                   // 长按：按下抬起分开
      "layer": 1,
      "center": { "x": 0.72, "y": 0.66 },
      "size": { "w": 0.06, "h": 0.11 },
      "map": { "action": "keydown", "key": "Shift", "release": "keyup" },
      "label": "冲刺", "icon": "💨"
    },

    {
      "id": "btn-burst",
      "type": "button",
      "mode": "taprepeat",              // 连发
      "layer": 1,
      "center": { "x": 0.93, "y": 0.44 },
      "size": { "w": 0.06, "h": 0.11 },
      "map": { "action": "key", "key": "Q", "count": 3, "interval": 90 },
      "label": "爆发", "icon": "💥"
    },

    {
      "id": "btn-combo",
      "type": "button",
      "mode": "macro",                  // ★ 宏/一键连招（UU「一键顶多键/连招」）
      "layer": 1,
      "center": { "x": 0.60, "y": 0.50 },
      "size": { "w": 0.07, "h": 0.12 },
      "map": {
        "action": "macro",
        "steps": [                       // 键序列：action + 延时(ms)
          { "type": "keydown", "key": "E", "delay": 0 },
          { "type": "keyup",   "key": "E", "delay": 50 },
          { "type": "click",   "button": "left", "delay": 30 }
        ]
      },
      "label": "连招", "icon": "🌀"
    },

    {
      "id": "btn-side",
      "type": "button",
      "mode": "tap",
      "layer": 1,
      "center": { "x": 0.90, "y": 0.30 },
      "size": { "w": 0.05, "h": 0.09 },
      "map": { "action": "click", "button": "XButton1" },  // ★ 鼠标侧键（UU「映射全键鼠」）
      "label": "侧键", "icon": "🖱"
    },

    {
      "id": "text-chat",
      "type": "text",
      "layer": 1,
      "center": { "x": 0.50, "y": 0.93 },
      "size": { "w": 0.40, "h": 0.05 },
      "map": { "action": "text" },
      "label": "聊天输入"
    }
  ]
}
```

### 5.3 分享码（对标 UU「复制分享码 / 输入分享码导入」）

布局 JSON 需要可序列化 + 可分享。建议：

```
分享码 = base64url( 压缩后的布局 JSON )
        + 前缀版本号  e.g. "SLCP2." + base64url(gzip(json))
```

- **用 gzip 压缩**：一个含十几个元素的布局 JSON 约 2~4 KB，gzip 后 <1 KB，base64 后仍可读（`SLCP2.H4sIA...`）。
- **短码方案**（可选）：服务端/本机维护「分享码 → JSON」的映射（像 UU 官方方案），短码只作索引；自建分享码则走「全量 JSON 内嵌」，无需服务器。
- **P0 建议**：先做「导出/导入 JSON 文件」+「复制/粘贴分享码（gzip+base64url）」，不依赖服务器；官方方案库是**中期项**（需后端存储）。

### 5.4 数据模型要点总结

1. **归一化坐标（0~1）+ 逻辑分辨率锚点**，跨设备天然适配（优于 Moonlight 的像素 + 高度比例，也优于 UU 的未知实现）。
2. **元素类型**：`joystick`（dpad8 移动）、`button`（tap/hold/taprepeat/macro）、`text`；**转视角走 `global.look`（画面滑动 → `moverel`），不是元素**（v1.1 修正）。
3. **`map` 表达「这个控件 → 什么动作」**：键（单键/组合键）、鼠标（left/right/middle/XButton1/XButton2）、宏（steps 序列）；相对位移（dx/dy/maxStep）由 `global.look` 承载。
4. **`layer` 字段**：解决「手指滑动扫过多个按键」的跨键判定（Moonlight 经验）。
5. **`deadzoneSticky`**：死区粘滞改善摇杆手感（Moonlight 经验，左摇杆移动用）。
6. **`global.opacity` + 元素级 `opacity` 覆盖**：全局半透明（对标 UU/Moonlight），可选单键覆盖。
7. **`label`/`icon`**：命名与图标（UU「命名」）。
8. **`global.gesture`**：三态手势参数（slop/longPress/dragConfirm），见 §10。
9. **分享码 = 版本前缀 + gzip + base64url**，P0 无服务器。

---

## 6. 手机端「转视角」交互方案对比

### 6.1 为什么不用摇杆转视角（手柄串流 vs 键鼠游戏的动作差异）

> ★ v1.1 修正：本节推翻了 v1.0 的「右摇杆转视角」结论。

Moonlight OSC 的右摇杆方案是**手柄游戏串流**的思路——它的目标是「把手机变成 Xbox 手柄」，最终发出的是手柄右摇杆轴（`rightStickX/Y`），因为 Steam 串流游戏本来就是用手柄玩的。**SoulLink 不是这个场景**：

| 维度 | 手柄串流（Moonlight 思路） | PC 键鼠游戏（SoulLink 场景） |
|---|---|---|
| 最终注入 | 手柄轴 + 手柄按键位图（Xbox 协议） | 键盘 + 鼠标（`SendInput`/`mouse_event`） |
| 玩家的既有肌肉记忆 | 主机/手柄玩家 | PC 键鼠玩家，但**在手机上**玩端游时，期望的其实是「手游的操控方式」 |
| 手游转视角习惯 | — | **右侧屏幕上滑动手指转视角**（原神、永劫无间、和平精英手游全部如此，没有右摇杆） |
| 手指遮挡 | — | 玩家滑一下就抬手，**不会持续挡住画面**（用户已明确此顾虑在实际产品里不成立） |

**手机端原神/永劫无间等手游的实际做法**：**左摇杆控制角色移动（虚拟摇杆），右侧屏幕直接滑动控制视角（无摇杆）**。玩家右手在画面空白处滑动 → 视角旋转；滑完抬手，画面恢复可见。这是「手游玩家操控 PC 端游」时的**天然预期**，SoulLink 应照此设计。

**摇杆方案的问题**（v1.0 未意识到）：
1. **占屏幕空间**：右摇杆要占一块固定的屏幕区域，挤压本就不大的手机屏。
2. **需要精确按住**：摇杆要求手指始终压在摇杆圆圈内并保持方向，玩家要分心「手指是否还在摇杆上」，而滑动转视角只需「滑一下抬手」。
3. **不符合手机习惯**：手游玩家已经习惯「左摇杆 + 右屏滑动」，强行给个右摇杆反而别扭。

### 6.2 方案对比（v1.1 修正）

| 方案 | 手势 | 手感 | 实现难度 | 参考 | 结论 |
|---|---|---|---|---|---|
| **直接滑动屏幕（定案）** | 在画面上按住并滑动手指 | ★★★★★ 手游玩家本能、不挡画面（滑完抬手）、方向+幅度连续 | 中（映射 `moverel` 相对位移，后端 L694 已实现） | 原神/永劫无间手游；UU「3D 视角：按住左/右/中键滑动」 | **首选（用户定案）** |
| 虚拟右摇杆 | 右手拇指在固定/浮动圆内拖动 | ★★★ 占屏、需精确按住、非手游习惯 | 中 | Moonlight `RightAnalogStick` | **否决**（v1.1 已撤下，仅保留摇杆数学给左摇杆用） |
| 触摸屏幕边缘 | 按住屏幕边缘方向滑动 | ★★★ 会误触、占用边缘 | 中 | 部分手游 | 备选，非必需 |
| 触控板模式 | 屏幕下方一块触控板，单指滑动=相对移动 | ★★★★ 不挡画面、可精确瞄准 | 中（`MOBILE-REMOTE-PLAN §4.2` 已列，接线 `moverel`） | Steam Link 触控板 | **次选**：与「滑动转视角」共用 `moverel` 后端，适合「要看清全屏」的精确场景 |
| 陀螺仪（手机） | 摇手机 | ★★★ 精度/漂移问题 | 高 | shuakami/LowLatencyInput（Star 4，弱） | 不做（P0 外） |

**关键结论**：
1. **转视角 = 画面滑动 + `moverel` 相对位移**（用户定案），后端最小改动（`RelayAgent.Execute` L694 已实现相对位移，只需 HTTP 接线 `dx/dy`，见 `MOBILE-REMOTE-PLAN §4.5 改动 3`）。**左摇杆仍用于移动**（方向键），其数学（死区粘滞、八方向扇区，`MOBILE-CONTROLS-DESIGN §1`）继续保留。
2. **部分游戏转视角需要「按住右键 + 相对移动」**（如相机拖拽类、某些 3D 游戏），UU 明确写了「左/右/中键按住滑动视角」——这正是「滑动 = 相对位移」的产品实证，方向与我们的定案一致。所以滑动转视角要支持**可选 `hold.button`**（按下后滑 = 按住某键 + 相对移动）。
3. **高频视角流 + HTTP 串行 RTT** 是体验瓶颈（`MOBILE-CONTROLS-DESIGN §2.2` 已点名 45~64ms/次），最终要靠 WebSocket/DataChannel 低延迟通道（可选中期项）。P0 用前端节流（16ms）+ `moverel` 顶住。

### 6.3 「滑动转视角」与「按住左键拖动」的冲突与区分

用户已经给出方向：「点一下 = 左键单击；按下后立即滑 = 转视角；按住不动一会儿再滑 = 左键按住拖动（拖物品/选范围）；原神没有拖动东西，拖地图时是没有视角的」。

这是一个经典的三态手势判定问题（点 / 滑 / 长按拖），成熟手势库（@use-gesture、Hammer.js）和移动系统（Android/iOS）都有既定参数。**判定逻辑与具体数值见 §10**（新增），核心是：

```
pointerdown 记起点 + 时间戳
  ├─ 位移 < slop 且 时长 < longPressTimeout → 抬起 = click（左键单击）
  ├─ 位移 ≥ slop 且 时长 < longPressTimeout → 滑 = moverel（转视角）
  └─ 时长 ≥ longPressTimeout（未位移）→ 进入「拖动态」，之后移动 = down + move + up（拖物品）
```

**「原神没有拖东西、拖地图时没有视角」的启示**：原神这类游戏里，「拖拽物品/地图」与「转视角」天然不同屏（拖地图时镜头锁定），所以不会冲突；但 SoulLink 作为**通用远程控制**（不绑定某一款游戏），必须覆盖「拖物品/选范围」的场景，因此需要**长按进入拖动**这条支路（见 §10.3 的误判处理）。

---

## 7. 许可风险提示

| 项目 | 许可 | 能否用于 SoulLink | 说明 |
|---|---|---|---|
| Moonlight Android | **GPL-3.0** | ❌ **不可抄代码** | 借鉴设计思想/数据结构/交互数学可以，但任何源码复制都会让 SoulLink 被迫 GPL。SoulLink 是自有许可，**必须只用「思想」，不搬运代码** |
| KeyMapper | **GPL-3.0** | ❌ 不可抄代码 | 同上 |
| Shyri/TouchMapper | **GPL-3.0** | ❌ 不可抄代码 | 方向也相反 |
| nipplejs | **MIT** | ✅ 可安全引用 | Web 前端虚拟摇杆现成库，若前端用 TS/JS 可直接依赖；但 SoulLink 前端是自研 HTML，评估是否值得引入（体积/维护） |
| bobboteck/JoyStick | **MIT** | ✅ 可安全引用 | 轻量，但功能弱于 nipplejs |
| GlassPad / TBVGPE | MIT | ✅ 可安全引用 | C#/Windows 触屏手柄，可参考映射表达，方向与我们（PC 键鼠注入）略有差异 |
| android-open-mapper / Android-Dex / 多数 Shizuku 系 | **NOASSERTION**（无明确许可） | ❌ 默认不可用 | 无 LICENSE = 默认保留所有权利，不要用 |
| UU / Steam Link / ToDesk 等 | 闭源 | ❌ 仅参考产品形态 | 不逆向、不反编译 |

**底线**：SoulLink 的虚拟按键实现应**全部自研**（C# 后端 + 自研 Web 前端），只「借鉴」公开的设计思想、交互数学（摇杆极坐标、死区粘滞、8 方向扇区、图层）与产品功能清单（UU 的命名/透明度/宏/分享码）。**不得**引入任何 GPL-3.0 源码，也不得依赖无明确许可的仓库。

---

## 8. 给后续任务的结论（可直接落地）

1. **交互定案**：**左摇杆移动（dpad8 → WASD）+ 画面滑动转视角（`moverel` 相对位移）**，`global.look.holdButton`（按住右键滑动视角，可选）。三态手势「点=单击 / 滑=转视角 / 长按后滑=拖动」参数见 §10（slop=12px、longPress=250ms、dragConfirm=16px）。触控板模式作为第二方案，与滑动共用 `moverel` 后端。
2. **后端最小改动**（已在 `MOBILE-REMOTE-PLAN §4.5` 定案，本文不新增）：`InputDto.dx/dy` + `moverel` 路由 + `keydown/keyup` + `text` + 键盘释放追踪。
3. **数据模型**：采用 §5.2 的 v2 Schema（归一化坐标 + layer + deadzoneSticky + taprepeat/macro + 分享码 + `global.look`/`global.gesture`）。
4. **交互细节照搬（合法）**：Moonlight 的**死区粘滞（150ms 解除死区）**、**图层跨键判定**、**双击 = 摇杆按压**；UU 的**全局透明度 + 显示鼠标自动隐藏 + 锁定鼠标禁用点击**。
5. **分享码**：P0 用 `版本前缀 + gzip + base64url` 自包含，不依赖服务器；官方方案库是中期项。

---

## 9. 相关文档

- `MOBILE-REMOTE-PLAN-20261122.md` —— 路线 / 手势 / 最小改动集 / 外网安全
- `MOBILE-CONTROLS-DESIGN-20261122.md` —— 摇杆数学与事件序列 / 布局 JSON 初稿 / 中文输入 / 断线释放
- `STREAM-PERF-AUDIT-20261122.md` —— 画面链路性能与远程控制内核
- `Relay/RelayAgent.cs`（`Execute` L694 `moverel` 相对位移实现）、`Relay/AgentWebServer.cs`（HTTP 层需接线 `dx/dy`）
- 参考源码（GPL，仅作设计参考，勿抄）：moonlight-stream/moonlight-android `virtual_controller/` 包

---

## 10. 触摸手势判定：点 / 滑 / 长按拖的成熟参数

> ★ v1.1 新增。用户定案的三态手势：**点 = 左键单击；按下后立即滑 = 转视角（相对位移）；按住不动一会儿再滑 = 左键按住拖动**。本节查清三态的判定参数、来源、误判处理与实现取舍。
> 数据来源已逐一用 curl 抓取并核实（2026-11-22），数值均来自对应库/系统源码或官方文档，未编造。

### 10.1 长按阈值（long-press threshold）

| 来源 | 默认值 | 出处（已核实） |
|---|---|---|
| Hammer.js `PressRecognizer` | **251 ms** | `hammer.js/src/recognizers/press.js` `defaults.time = 251`（源码实测） |
| @use-gesture drag `delay`（传 `true` 时） | **180 ms** | `use-gesture/packages/core/src/config/dragConfigResolver.ts` `DEFAULT_DRAG_DELAY = 180` |
| @use-gesture `preventScroll` 默认时长 | **250 ms** | 同上 `DEFAULT_PREVENT_SCROLL_DELAY = 250` |
| Android `ViewConfiguration` `DEFAULT_LONG_PRESS_TIMEOUT` | **400 ms** | AOSP `ViewConfiguration.java` 源码实测 `DEFAULT_LONG_PRESS_TIMEOUT = 400` |
| iOS `UILongPressGestureRecognizer.minimumPressDuration` | **0.5 s（500 ms）** | Apple 官方文档（JS 页面未能抓全文，但该默认值 0.5s 是公开文档的稳定值；标注为「未逐字抓取，经验值待真机复核」） |
| 手游实际体验（原神/和平精英） | 无公开精确数字；玩家侧主观感受 < 300ms 即「按住」 | 未找到官方文档（见下方「查不到」声明） |

**推荐取值**：**250 ms**。
- 理由：Harmmer.js（251ms）与 @use-gesture（180~250ms）两大手势库都落在 200~250ms 区间；Android 系统 400ms 偏长（为「长按弹出菜单」设计，游戏里偏钝），iOS 500ms 更钝。游戏/串流场景要求跟手，取 250ms 折中——既不误伤「快速滑动转视角」，又给「想拖物品」留出足够识别窗口。
- **必须可配置**：放全局配置（默认 250ms），允许玩家调到 180~400ms。

### 10.2 位移阈值（多少 px 算「滑动」而非「抖动」）

| 来源 | 默认值 | 出处（已核实） |
|---|---|---|
| Hammer.js `PressRecognizer` `threshold` | **9 px** | `press.js` `defaults.threshold = 9`（源码实测） |
| @use-gesture drag `threshold` | **0 px**（不设 `filterTaps` 时）；设 `filterTaps` 时用 `tapsThreshold=3` | `dragConfigResolver.ts` `V.toVector(value, filterTaps ? tapsThreshold : 0)` |
| Android `ViewConfiguration` `TOUCH_SLOP` | **8 dp**（源码 `TOUCH_SLOP = 8`，运行时按 density 缩放为 `getScaledTouchSlop()`） | AOSP `ViewConfiguration.java` 源码实测 |
| SoulLink 现有 `docs/web-index.html` | **8 px**（`if (Math.hypot(dx,dy) < 8)`，L896） | 本地代码（桌面鼠标语义） |

**推荐取值**：**触屏 10~12 px（CSS px），桌面保持 8 px**。
- 理由：手指抖动比鼠标大，触屏 slop 应比桌面 8px 略宽；Android 8dp ≈ 在 2.5~3x density 设备上约 20~24 物理 px ≈ 8 CSS px，但考虑到「手指 + 移动网络」，取 10~12 CSS px 更稳。**用 CSS px + `pointer` 事件统一触屏/鼠标，按 `pointerType` 区分**（`touch` 用 12，`mouse` 用 8）。
- 注意：现有 `web-index.html` L896 写死 8px 且不分指针类型，手机化时需改为「按 pointerType 区分」。

### 10.3 长按后进入拖动的过渡（关键：避免误判）

判定状态机（推荐）：

```
pointerdown: 记录 startTime、startPos，置 state=PRESSED，启动 longPressTimer(250ms)
pointermove (在 longPressTimer 触发前):
    if 位移 ≥ slop(12px):   → 取消 longPressTimer，state=SWIPE，开始发 moverel（转视角）
pointermove (在 longPressTimer 触发后，且 state 仍为 PRESSED):
    → state=DRAG，立即发 down(left)（此时才把「按下」落成真实鼠标按下）
    后续 move → move(left)，up → up(left)（拖物品/选范围）
pointerup:
    if state==PRESSED: 发 click(left)（点）
    if state==SWIPE:   停发 moverel（抬手结束转视角）
    if state==DRAG:    发 up(left)
```

**三个关键决策点**：

1. **「长按时间到就发 down」还是「长按时间到 + 之后移动了才发 down」？**
   - 推荐**后者**（长按时间到 + 之后移动才发 down）。若「时间到就发 down」，用户在长按阈值刚过、还没来得及动时，远端已经收到「按下」——若用户其实只是想转视角（手慢了点），就误触发了拖动。**只有长按 + 后续实际位移 ≥ slop，才真正落 down**。
   - 但要注意：这会引入「长按后第一次移动」的延迟（要等 slop 位移才发 down），对「拖物品」影响可忽略（拖本来就有位移）。

2. **长按时间到了但用户其实想转视角（手慢），会误触发拖动吗？**
   - 会。缓解手段（成熟产品/系统做法）：
     - **进入拖动前，移动仍先发 `moverel` 预览**：即在「长按已到但未确认拖动」的过渡态，**继续按滑动处理（发 moverel）**，直到位移累计超过一个更大的「拖动确认位移」（如 16~20px）才切到 down+move。这样手慢的玩家滑得不够狠就仍是转视角。
     - **UI 反馈兜底**：长按成功时给震动/视觉提示（§10.4），让玩家明确「现在是拖动态」。
   - 最终策略建议：**长按 250ms 后仍处于「未移动」→ 进入「待定」；一旦移动，先按位移大小裁决——位移 < 拖动确认阈值(16px) 判为滑动(转视角)，≥ 阈值才判为拖动**。极端情况（玩家想精确小步拖）可通过「长按成功后先震动提示」让玩家感知已进入拖动态，从而配合更果断的移动。

3. **成熟产品怎么处理？**
   - 未找到 UU/原神的公开技术文档描述这一过渡（见下方「查不到」声明）。但 Android 系统的 `TouchSlop` + `LongPressTimeout` 组合（按下不动 400ms = long press，动了 8dp = 滚动/拖动）就是同一套「时间+位移」双阈值，是业界通用范式。

### 10.4 反馈方式（长按成功）

| 方式 | 支持情况 | 结论 |
|---|---|---|
| `navigator.vibrate(10)`（震动） | Chrome/Edge/Android 支持；**iOS Safari 与 Safari on iOS 均不支持**（caniuse.com/vibration 已核实：Safari 全版本 ❌，Safari on iOS ❌，Chrome for Android ✅） | iOS 不能用震动 |
| 视觉反馈（按键高亮/圆环提示） | 全平台可用 | **替代方案**：长按成功时给元素高亮/波纹（现有 `ripple()` 已有，`web-index.html` L914） |
| 音频 | 需预加载音频，可能突兀 | 不推荐 |

**结论**：长按成功的反馈用**视觉高亮/波纹**（跨平台一致），震动作为 Android 上的增强（`if (navigator.vibrate) navigator.vibrate(10)`，iOS 静默跳过）。

### 10.5 自己实现 vs 引库

**约束**：SoulLink 的 `docs/web-index.html` 是**单文件、不引框架**（`HANDOFF` §5 约束），只能引无依赖小库或自己写几十行。

| 方案 | 成本 | 依赖 | 结论 |
|---|---|---|---|
| **自己写（推荐）** | 约 40~60 行：pointerdown/move/up + 一个 250ms 定时器 + 两个阈值（slop 12px / 拖动确认 16px）+ 三态状态机 | 零依赖 | **首选**：逻辑清晰、完全可控、无 bundle 负担，且 SoulLink 前端本来就有 `drag`/`sendMoveThrottled` 的基础设施（`web-index.html` L840-919），只需在现有 `drag` 状态机上扩展「长按定时器 + 滑动 vs 拖动分叉」 |
| 引 nipplejs | ~8KB | 无依赖但库体积 ~8KB | 不必要：nipplejs 是摇杆库，本场景是「滑动 + 长按」，不匹配 |
| 引 Hammer.js / @use-gesture | Hammer ~8KB；@use-gesture 需打包器 | 有依赖/需构建 | 不必要：我们只需 press+pan 两个识别器，自写更轻 |

**结论**：**自己写**。在现有 `web-index.html` 的 `pointerdown/move/up`（L841-919）基础上扩展：加 `longPressTimer`（250ms）+ 三态 `PRESSED/SWIPE/DRAG`，复用现有 `sendInput('moverel', ...)`（需接线）与 `down/up` 动作。

### 10.6 「查不到」的诚实声明

- **原神/永劫无间手游的官方长按/滑动阈值**：未找到官方文档公开精确 ms/px 数值，只有玩家社区的主观描述。上述 250ms/12px 取自通用手势库与系统默认，非原神实测。
- **UU 远程「3D 视角」的精确触发机制**（是纯滑动、还是按住某键+滑动、阈值多少）：官网 `/features/keyboard/` 只写「支持左键、右键、中键按住滑动视角」和「灵敏度可调」，**未公开判定阈值**。结论「滑动 = 相对位移」方向与 UU 一致，但具体数值需自定。
- **iOS `minimumPressDuration` 默认 0.5s**：Apple 文档页是 JS 渲染，curl 拿不到正文，该值来自公开文档的稳定记忆，标注「待真机/文档复核」，但**不影响我们自定 250ms 的结论**（我们自定义，不依赖系统默认）。
