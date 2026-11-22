# 手机端控制层设计：虚拟摇杆 + 可编辑键盘映射 + 中文输入

> 日期：2026-11-22
> 作者：mobile-lead（agent-team）
> 前置：`MOBILE-REMOTE-PLAN-20261122.md`（路线/手势/最小改动集）、`HANDOFF-3.3.19-20261122.md`（网页直连代理）、`Relay/AgentWebServer.cs` + `Relay/RelayAgent.cs`（键鼠注入协议）
> 范围：**只读分析 + 设计文档**，不改业务代码、不动 dist/、不提交、不发版。
> 本文件解决「手机端怎么像游戏手柄一样操作」：虚拟摇杆（移动/视角）、可编辑键位、中文输入上屏。

---

## 0. 结论摘要（TL;DR）

1. **摇杆是"方向 → 按键序列"的映射器**，不是新输入设备。它把触屏的连续向量离散成 8 方向，再转成「松开旧方向键 + 按下新方向键」的事件序列，最终仍走现有 `/api/input` 的 `key`/新增的 `keydown`/`keyup`。
2. **当前协议有个硬缺口**：`RelayAgent.Execute` 的 `key` 动作（`RelayAgent.cs` L746–758）是**点按式**（`SendKeyCombo` L162–186 全按下 → `Thread.Sleep(20)` → 全抬起），**没有独立的 keydown/keyup**，而摇杆「按住方向不放」恰恰需要按住语义。**必须新增 `keydown`/`keyup`（或 `holdkey`）动作**，这是 P0 功能必需，不能绕过。
3. **中文输入**：`SendVk`（`RelayAgent.cs` L189–212）只走 `wVk`（虚拟键码），**没有 `KEYEVENTF_UNICODE`**；手机 IME 产出的是字符串，必须新增 `text` 动作走 Unicode 注入。这与 MOBILE-REMOTE-PLAN §4.5 的结论一致，此处给出**上屏时序与伪代码**。
4. **断线/锁屏释放是安全项**：摇杆按住期间断线/锁屏，远端若不释放会「卡键」（角色一直走/技能一直按）。必须设计**前端兜底（blur/visibilitychange 时强制发 keyup）+ 后端兜底（会话断开时释放所有按键）**。

---

## 1. 摇杆数学

### 1.1 坐标与参数

摇杆是一个圆形区域，视觉上有「底座（base）」和「摇杆头（knob）」：

| 符号 | 含义 | 默认值 |
|---|---|---|
| `C = (cx, cy)` | 底座圆心（屏幕相对坐标，布局里配） | 由布局 JSON 决定 |
| `T = (tx, ty)` | 当前触点坐标 | 运行时 |
| `R` | 摇杆最大半径（knob 可拉出的最大距离，px） | 60 px（按屏宽缩放） |
| `d` | 死区半径（|v| < d 视为回中） | 12 px（0.2·R） |
| `θ` | 方向角 = `atan2(ty-cy, tx-cx)` | 运行时 |
| `m` | 位移模长 = `|T-C|` | 运行时 |

### 1.2 归一化与死区（伪代码）

```
function joystickVector(T):
    vx = T.x - cx
    vy = T.y - cy
    m  = sqrt(vx*vx + vy*vy)

    if m <= d:                          # 死区内 → 回中，不产生任何方向
        return { dir: CENTER, mag: 0, nx: 0, ny: 0 }

    nx = vx / m
    ny = vy / m                          # 单位方向

    # 位移钳制到 R：knob 视觉停在 R 处，但方向仍按原始 nx/ny 算
    clamp = min(m, R)
    knobX = cx + nx * clamp
    knobY = cy + ny * clamp

    mag = (m - d) / (R - d)              # 0~1 归一化幅度（d 之后开始计）
    mag = clamp(mag, 0, 1)

    return { dir: sector(nx, ny), mag, nx, ny, knobX, knobY }
```

### 1.3 八方向扇区

```
function sector(nx, ny):
    ang = atan2(ny, nx)                  # -π ~ π，0 = 正右(东)
    if mag == 0: return CENTER
    # 每个方向占 45°（π/4）。东=0°，逆时针增。
    oct = floor((ang + π/8) / (π/4))     # 归到 0..7
    return DIRS[oct]                     # 0=E 1=NE 2=N 3=NW 4=W 5=SW 6=S 7=SE
```

```
方向扇区示意（角度从正右逆时针）：
            N (上, 270°/ -90°)
               ▲
      NW(315°) │ NE(225°)
         ╲     │     ╱
          ╲    │    ╱
  W(180°) ──── + ──── E(0°)
          ╱    │    ╲
         ╱     │     ╲
      SW(135°) │ SE(45°)
               ▼
            S (下, 90°)
```

### 1.4 幅度 → 步行/跑步（原神场景）

原神移动：摇杆幅度决定**走/跑**。8 方向键映射时用幅度阈值切「跑步修饰键」：

```
WALK_THRESHOLD  = 0.35   # mag < 0.35 → 只发方向键（走）
RUN_THRESHOLD   = 0.7    # mag >= 0.7 → 方向键 + 跑步键（原神默认 Shift=冲刺，R 或无）

若布局配了 runKey:
    if mag >= RUN_THRESHOLD  and runHeld == false:  send keydown(runKey); runHeld = true
    if mag <  RUN_THRESHOLD  and runHeld == true:   send keyup(runKey);   runHeld = false
```

---

## 2. 摇杆事件序列（伪代码）

> 核心原则：**只发「变化」**——方向变了才松旧按新；幅度跨阈值才切跑步；其余 touchmove 只更新 knob 视觉，不打后端请求。这避免高频 move 打爆串行 HTTP（与 `MOBILE-REMOTE-PLAN` §3.4 的 RTT 敏感一致）。

```
state = { active: false, dir: CENTER, runHeld: false, pointerId: null }

onPointerDown(joystickBase, e):
    if state.active: return
    state.active = true
    state.pointerId = e.pointerId
    setPointerCapture(e.pointerId)
    C = baseCenter(joystickBase)
    updateJoystick(e)                    # 进入时立即算一次（点哪边即朝哪走）

onPointerMove(e):
    if not state.active or e.pointerId != state.pointerId: return
    updateJoystick(e)

function updateJoystick(e):
    T = { x: e.clientX, y: e.clientY }
    v = joystickVector(T)                # §1.2
    renderKnob(v.knobX, v.knobY)

    if v.dir == state.dir:
        # 同方向：只处理跑步阈值切换（若有 runKey）
        updateRun(v.mag)
        return

    # 方向改变：松旧、按新
    if state.dir != CENTER: sendKeyUp(KEYMAP[state.dir])     # 松开旧方向键（组合键全松）
    if v.dir   != CENTER: sendKeyDown(KEYMAP[v.dir])         # 按下新方向键（组合键全按）
    state.dir = v.dir
    updateRun(v.mag)

onPointerUp / onPointerCancel(e):
    if not state.active: return
    releaseJoystick()

function releaseJoystick():
    if state.dir != CENTER: sendKeyUp(KEYMAP[state.dir])     # 松方向键
    if state.runHeld:        sendKeyUp(runKey); state.runHeld = false
    state.dir = CENTER
    state.active = false
    renderKnob(C)                                            # knob 回中
```

### 2.1 按键序列的原子性（重要）

- **按/松必须成对**：`sendKeyDown(dir)` 与 `sendKeyUp(dir)` 必须严格配对，否则「卡键」。
- **组合键**：一个方向可以映射为组合（如原神里方向 + 冲刺）。`sendKeyDown(combo)` = 顺序 `keydown` 每个成员；`sendKeyUp(combo)` = **逆序** `keyup`（与 `RelayAgent.SendKeyCombo` L178–182 的「正序按下、逆序抬起」一致）。
- **后端无 keydown/keyup 时的退化**：若暂不改协议，可用「`key` 点按 + 前端 40ms 重发」近似，但**按住体验差且占满串行通道，不推荐**（见 §7 待验证）。

### 2.2 右摇杆 = 视角（moverel）

右摇杆不复用 8 方向键，而是映射到**相对位移**（转视角），数学同上，但输出是 `moverel`（`RelayAgent.Execute` L675 已实现，`Request.Dx/Dy` L409–410 已有，但 HTTP 层未接线）：

```
onRightJoystickMove(v):
    # v.mag 映射为移动步长；方向映射为 dx/dy
    step = v.mag * MAX_STEP          # MAX_STEP 例如 40 px/帧
    dx = round(v.nx * step)
    dy = round(v.ny * step)
    if dx != 0 or dy != 0: sendInput('moverel', -1, -1, { dx, dy })   # 需后端接线（§7）
```

> 视角移动是高频流（游戏转视角 30~60 次/秒），**HTTP POST 串行往返的 RTT 会明显拖后腿**（实测 `RelayClient` 串行 45~64ms/次，见 `MOBILE-REMOTE-PLAN` §3.4）。右摇杆体验的最终解法依赖 `MOBILE-REMOTE-PLAN` 已列的「WebSocket/DataChannel 低延迟通道」**可选中期项**；P0 先用 `moverel` + 前端节流（16ms）顶住，并标注为**待验证**。

---

## 3. 布局 JSON Schema

### 3.1 Schema（伪 schema，JSON 形式）

```jsonc
{
  "$schema": "https://soullink.local/schema/controls-layout-v1.json",
  "version": 1,
  "screen": { "logicalWidth": 1920, "logicalHeight": 1080 },  // 逻辑分辨率，坐标按此归一化
  "elements": [
    {
      "id": "joy-move",               // 唯一 id
      "type": "joystick",             // joystick | button | text
      "mode": "dpad8",                // dpad8(8方向键) | analog(模拟量) | mouse(视角相对位移)
      "center": { "x": 0.12, "y": 0.80 },   // 归一化坐标 0~1（相对 logical 尺寸）
      "radius": 0.14,                 // 摇杆半径，相对屏宽
      "deadzone": 0.20,               // 死区占 radius 比例
      "map": {                        // dpad8 时的方向→键映射
        "N":  "W", "NE": "W+D", "E":  "D", "SE": "S+D",
        "S":  "S", "SW": "S+A", "W":  "A", "NW": "W+A"
      },
      "run": { "key": "Shift", "threshold": 0.7 },   // 可选：跑步修饰键
      "touch": { "visual": "knob-follow" }           // 视觉：knob 跟随触点
    },
    {
      "id": "joy-camera",
      "type": "joystick",
      "mode": "mouse",                 // 视角：相对位移 moverel
      "center": { "x": 0.88, "y": 0.80 },
      "radius": 0.14,
      "deadzone": 0.10,
      "map": { "dx": 1.0, "dy": 1.0, "maxStep": 40 },  // 灵敏度
      "touch": { "visual": "knob-follow" }
    },
    {
      "id": "btn-attack",
      "type": "button",
      "mode": "tap",                   // tap(点按) | hold(按住) | taprepeat(连点)
      "center": { "x": 0.78, "y": 0.62 },
      "size": { "w": 0.07, "h": 0.12 },   // 椭圆按钮，相对尺寸
      "map": { "action": "click", "button": "left" },   // 普攻 = 左键点击
      "label": "攻击",
      "icon": "⚔"
    }
    // ... 更多按钮
  ]
}
```

### 3.2 元素类型与 map 语义

| type | mode | map 内容 | 输出动作 |
|---|---|---|---|
| `joystick` | `dpad8` | `N/NE/E/SE/S/SW/W/NW` → 键名或 `+` 组合 | `keydown`/`keyup`（需新增） |
| `joystick` | `mouse` | `dx/dy/maxStep` 灵敏度 | `moverel`（需 HTTP 接线） |
| `button` | `tap` | `{action, button/key}` | `click` 或 `key`（点按） |
| `button` | `hold` | `{action:"down"/"up", button/key}` | `down`/`up`（键用 `keydown`/`keyup`） |
| `button` | `taprepeat` | `{action:"click", count, interval}` | 连点（复用 `RelayAgent` `click` 的 `count`，L712） |
| `text` | — | `{action:"text"}` | `text`（需新增） |

### 3.3 完整原神预设示例

```json
{
  "version": 1,
  "screen": { "logicalWidth": 1920, "logicalHeight": 1080 },
  "elements": [
    { "id": "joy-move", "type": "joystick", "mode": "dpad8",
      "center": { "x": 0.13, "y": 0.78 }, "radius": 0.14, "deadzone": 0.20,
      "map": { "N": "W", "NE": "W+D", "E": "D", "SE": "S+D",
               "S": "S", "SW": "S+A", "W": "A", "NW": "W+A" },
      "run": { "key": "Shift", "threshold": 0.70 },
      "touch": { "visual": "knob-follow" } },

    { "id": "joy-camera", "type": "joystick", "mode": "mouse",
      "center": { "x": 0.87, "y": 0.78 }, "radius": 0.13, "deadzone": 0.10,
      "map": { "dx": 1.0, "dy": 1.0, "maxStep": 40 },
      "touch": { "visual": "knob-follow" } },

    { "id": "btn-attack",  "type": "button", "mode": "tap",
      "center": { "x": 0.80, "y": 0.60 }, "size": { "w": 0.07, "h": 0.12 },
      "map": { "action": "click", "button": "left" }, "label": "攻击", "icon": "⚔" },

    { "id": "btn-jump",    "type": "button", "mode": "tap",
      "center": { "x": 0.86, "y": 0.50 }, "size": { "w": 0.06, "h": 0.11 },
      "map": { "action": "key", "key": "Space" }, "label": "跳跃", "icon": "⬆" },

    { "id": "btn-dash",    "type": "button", "mode": "hold",
      "center": { "x": 0.72, "y": 0.66 }, "size": { "w": 0.06, "h": 0.11 },
      "map": { "action": "keydown", "key": "Shift", "release": "keyup" },
      "label": "冲刺", "icon": "💨" },

    { "id": "btn-skill",   "type": "button", "mode": "tap",
      "center": { "x": 0.91, "y": 0.55 }, "size": { "w": 0.06, "h": 0.11 },
      "map": { "action": "key", "key": "E" }, "label": "战技", "icon": "✨" },

    { "id": "btn-burst",   "type": "button", "mode": "tap",
      "center": { "x": 0.93, "y": 0.44 }, "size": { "w": 0.06, "h": 0.11 },
      "map": { "action": "key", "key": "Q" }, "label": "爆发", "icon": "💥" },

    { "id": "btn-interact","type": "button", "mode": "tap",
      "center": { "x": 0.70, "y": 0.52 }, "size": { "w": 0.05, "h": 0.09 },
      "map": { "action": "key", "key": "F" }, "label": "交互", "icon": "🤝" },

    { "id": "text-chat",   "type": "text",
      "center": { "x": 0.50, "y": 0.93 }, "size": { "w": 0.40, "h": 0.05 },
      "map": { "action": "text" }, "label": "聊天输入" }
  ]
}
```

> 说明：原神 PC 默认键位 `WASD` 移动、`Shift` 冲刺、`Space` 跳、`E` 战技、`Q` 爆发、`F` 交互、左键普攻。此预设把「移动/视角」交给双摇杆、「技能/交互」交给可点击按钮、「聊天」交给 text 输入。键位以玩家实际键位为准，编辑模式（§4）可改。

---

## 4. 编辑模式交互流程与线框

### 4.1 状态机

```
[浏览模式] --点「编辑布局」--> [编辑模式]
[编辑模式] --点「完成」--> [校验+保存] --失败--> [编辑模式(标红)] 
                                     --成功--> [浏览模式]

编辑模式下：
  · 拖拽元素 → 移动位置
  · 点元素 → 选中，右侧/底部弹「元素属性面板」
  · 属性面板改：映射键 / 组合键 / 类型(mode) / 死区 / 灵敏度 / 标签 / 图标
  · 「+ 添加」→ 从模板列表加新摇杆/按钮/文本框
  · 「删除」→ 移除选中元素
```

### 4.2 线框（ASCII）

```
┌────────────────────────────────────────────────┐
│  ◀ 返回      [ 编辑布局 ]      [ 完成 ✓ ]      │  ← 顶栏
├────────────────────────────────────────────────┤
│                                                │
│   ╭──────────╮                    ╭──────────╮ │
│   │  ⊙       │   （左摇杆）        │  ⊙       │ │ ← 右摇杆
│   ╰──────────╯                    ╰──────────╯ │
│                                                │
│        ⚔        💨                          ┌─────┐ │
│      (攻击)    (冲刺)                        │ 编辑 │ │ ← 属性面板(选中元素时弹出)
│   🤝    ⬆    ✨                            │ 键位 │ │
│ (交互)(跳)  (战技)   💥                     │ W+A │ │
│                     (爆发)                   │ 死区 │ │
│   ┌────────────────────────────┐            │ 20% │ │
│   │        [聊天输入]           │            └─────┘ │
│   └────────────────────────────┘                     │
└────────────────────────────────────────────────┘
```

### 4.3 编辑交互细节

| 动作 | 行为 | 技术要点 |
|---|---|---|
| 拖拽元素 | 移动 `center`（归一化坐标） | 编辑态禁用手势→动作映射，触点只改布局 |
| 选中 | 高亮 + 显示属性面板 | 面板字段与 §3.2 `map` 一一对应 |
| 改方向键 | 八方向各一个输入框 | 支持组合 `W+D`；空 = 该方向无映射 |
| 改跑步键 | 开关 + 键名 + 阈值滑杆 | 阈值 0~1 |
| 添加元素 | 模板（左摇杆/右摇杆/圆钮/文本框） | 复制预设默认值 |
| 保存 | JSON 校验 + `localStorage` 持久化 | 失败标红 + 提示字段 |

---

## 5. 中文输入上屏时序（compositionend）

### 5.1 问题

移动端 IME（中文输入法）在 `keydown`/`input` 期间产出的是**合成中的候选串**（`compositionstart` → 多次 `input` → `compositionend`）。若在 `input` 里逐次发送，会把拼音/候选也打上屏。

### 5.2 时序（伪代码）

```
let composing = false, composeBuf = ''

inputEl.addEventListener('compositionstart', () => {
    composing = true
    composeBuf = ''
})

inputEl.addEventListener('input', (e) => {
    if (composing) {
        composeBuf = e.data ?? ''          // 合成中的中间态，只缓存，不发
        return
    }
    // 非合成输入（如英文直打）：可直接上屏
    sendText(e.data ?? '')
})

inputEl.addEventListener('compositionend', (e) => {
    composing = false
    const final = e.data ?? composeBuf      // 合成结束：拿到最终中文串
    if (final.length > 0) sendText(final)
    composeBuf = ''
})

inputEl.addEventListener('keydown', (e) => {
    // 组合键（Enter 发送、Esc 取消、Ctrl+V 粘贴）走 key 动作，不走 text
    if (e.key === 'Enter') { sendKey('Enter'); e.preventDefault(); }
    else if (e.key === 'Escape') { sendKey('Esc'); e.preventDefault(); }
    // 其余交给 composition/input 流程
})
```

### 5.3 `sendText` 与后端 `text` 动作

```
async function sendText(str):
    if (!str) return
    await sendInput('text', -1, -1, { text: str })   # 新增动作（§7）
```

后端（需新增，落点见 §7）：

```
// AgentWebServer.HandleInputAsync L280–291 switch 加：
case "text": cmd = "text"; break;

// InputDto L314–323 加： public string? text { get; set; }

// RelayAgent.Execute L623 switch 加：
case "text":
    // 走 SendInput + KEYEVENTF_UNICODE（每字符 KEYEVENTF_UNICODE down + up）
    // 注意：KEYEVENTF_UNICODE(0x4) 必须与 KEYEVENTF_KEYUP(0x2) 组合使用
    // 当前 SendVk 只支持 wVk（L189–212），需新增 Unicode 注入路径
```

---

## 6. 断线 / 锁屏释放协议

### 6.1 为什么是安全项

摇杆/`hold` 按钮按住期间，若前端断线、切后台、锁屏，远端键一直处于「按下」状态 → 角色持续移动 / 技能卡住，甚至触发危险操作。必须有**两层兜底**。

### 6.2 前端兜底（第一时间）

```
// 页面隐藏 / 失去焦点 / 网络断开 → 立即释放所有"当前按住"的键
function releaseAllHeld():
    for each joystick j in activeJoysticks: j.releaseJoystick()   # §2
    for each holdButton b in heldButtons:  sendKeyUp(b.key); b.held=false

document.addEventListener('visibilitychange', () => {
    if (document.hidden) releaseAllHeld()
})
window.addEventListener('blur', releaseAllHeld)
window.addEventListener('pagehide', releaseAllHeld)
window.addEventListener('offline', releaseAllHeld)
```

> 注：`visibilitychange`/`blur` 触发时**页面还可能再执行一小段同步代码**，`sendKeyUp` 用 `navigator.sendBeacon` 或**同步 fetch（keepalive）** 兜底发最后一次释放（普通异步 fetch 可能被浏览器掐断）。锁屏/切后台的释放请求优先级最高。

### 6.3 后端兜底（最终保障）

前端可能来不及发（崩溃、进程被杀）。所以**代理侧必须在会话断开时强制释放**：

```
// RelayAgent 已有「宿主断开释放」路径（ServePipe 末尾 L615 ReleaseAllMouseButtons()）
// 但那只释放鼠标键，不释放键盘。需新增：
//   1. 记录"当前按下的虚拟键"集合（PressKeys，仿 PressedButtons L98 的结构）
//   2. ServePipe 循环退出 / Run() 结束（L550-560）时，对 PressKeys 逐个发 keyup
```

> 现状 `RelayAgent` 只有 `PressedButtons`（鼠标，L98）会在退出时 `ReleaseAllMouseButtons()`（L515/L550），**没有键盘按住状态追踪**。新增 `keydown`/`keyup` 后必须配套「按住键追踪 + 断开释放」，否则断线即卡键。

### 6.4 心跳看门狗（可选加固）

若担心网络半开（TCP 未断但前端已死），可加**看门狗**：`keydown` 后若 3 秒内无「心跳/移动/新指令」，代理自动 keyup。P0 可不做，标注**待验证**（HTTP 短连接下难以判断"前端死"与"用户停着不动"）。

---

## 7. 协议依赖（确定 / 待验证）

| # | 项 | 现状 | 结论 | 标注 |
|---|---|---|---|---|
| 1 | `keydown`/`keyup` 动作（摇杆按住、hold 按钮） | `RelayAgent.Execute` `key`（L746–758）只点按，`down`/`up`（L725/729）是**鼠标**专用 | **必须新增**键盘按住/抬起动作 | **确定**（P0 功能必需） |
| 2 | `text` 动作（中文输入） | `SendVk`（L189–212）只走 `wVk`，无 `KEYEVENTF_UNICODE` | **必须新增** `text` + Unicode 注入 | **确定**（P0 功能必需） |
| 3 | `moverel` HTTP 接线（右摇杆视角） | `Execute` L675 已实现，但 `AgentWebServer` L280–291 无 `case "moverel"`、`InputDto` L314–323 无 `dx/dy` | **必须接线** `InputDto.dx/dy` + switch | **确定**（P0 视角必需） |
| 4 | 后端键盘释放（断线卡键） | 只有鼠标 `ReleaseAllMouseButtons`，无键盘状态追踪 | **必须配套**按住键追踪 + 断开释放 | **确定**（安全必需） |
| 5 | 右摇杆视角的低延迟体验 | HTTP POST 串行 RTT 45~64ms/次 | P0 用 `moverel`+节流顶住；最终靠 WebSocket/DataChannel（可选中期项） | **待验证**（体验需真机实测） |
| 6 | 摇杆幅度→跑步阈值 | 原神跑步键位玩家可改 | 阈值 0.7 为经验值，需真机调 | **待验证** |
| 7 | 断线看门狗（半开连接） | 无 | P0 不做，靠 6.2/6.3 两层兜底 | **待验证** |

---

## 8. 相关文档

- `MOBILE-REMOTE-PLAN-20261122.md` —— 路线 / 手势 / 最小改动集（`text`/`moverel`/CORS/令牌）
- `HANDOFF-3.3.19-20261122.md` —— 网页直连代理
- `Relay/AgentWebServer.cs`、`Relay/RelayAgent.cs` —— 键鼠注入协议实现
- `AGENTS.md` —— 性能约束（摇杆事件节流遵循「按需触发，不持续轮询」）
