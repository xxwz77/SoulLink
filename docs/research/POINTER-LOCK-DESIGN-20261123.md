# Pointer Lock 操作方案设计（POINTER-LOCK-DESIGN）

> 日期：2026-11-23
> 作者：viewer-auditor（dsh-team task `task_mwte6oi21s0rgj`）
> 范围：**只读研究 + 设计，未改任何产品代码/脚本/配置**。唯一写入为本设计文档。
> 证据标记：【规范】= MDN/W3C Pointer Lock 规范原文；【外部源码】= moonlight-chrome/sunshine/selkies 一手源码（file:line 可回查）；【代码事实】= 本仓库 `docs/web-index.html` 现状；【未核实】= 闭源产品（云原神/向日葵/原神 PC）无法逆向取证，明确标注不臆造。

---

## 0. 结论（三句话）

1. **Pointer Lock 是「相对鼠标转视角」在桌面浏览器上的正解**：锁定后 `mousemove.movementX/Y` 提供无屏幕边界的相对位移，光标隐藏，与桌面版「按住右键拖动转视角」语义同构，但**不需要按住右键**——把「鼠标持续移动」直接映射到 `moverel` 相对位移，正是云游戏桌面网页端的标准做法【规范 + 月光外部源码】。
2. **我们网页端无 iframe 结构**（grep 全文件 0 处 iframe/frame），锁定请求发到画面容器 `#scr` 即可，**不涉及云原神那种跨源 iframe 的 sandbox/权限坑**【代码事实】。
3. **复用现有 lookDelta/lookAcc 8ms 合并路径，不重造轮子**：锁定态的 `movementX/Y` 直接累加进现有 `lookAcc`，走已实现的 `flushLook()→sendInput('moverel')→8ms 节流` 链路【代码事实】。

---

## 1. Pointer Lock 完整状态机

### 1.1 四个 API 原语（【规范】）

| 原语 | 类型 | 说明 |
|---|---|---|
| `element.requestPointerLock()` | 方法 | 异步请求锁定到该元素；**需要 transient activation（用户手势）**，目标文档必须处于 active 状态 |
| `document.pointerLockElement` | 属性 | 当前锁定元素；`null` = 未锁定 |
| `document.exitPointerLock()` | 方法 | 请求退出锁定（异步，经 pointerlockchange 生效） |
| `pointerlockchange` / `pointerlockerror` | 事件 | 在 **document** 上监听；锁定/解锁成功触发 change，失败触发 error |

### 1.2 状态转换表

| 当前态 | 事件/动作 | 触发条件 | 目标态 | 异常处理 |
|---|---|---|---|---|
| **IDLE**（未锁定） | `scr.requestPointerLock()` | 用户手势（点击画面/按钮）内调用 | **LOCKED** | 若无非用户手势调用 → 浏览器拒绝，触发 `pointerlockerror`，停留在 IDLE |
| IDLE | `scr.requestPointerLock()` | 非手势/文档非 active/iframe 缺 sandbox token | IDLE | `pointerlockerror` 触发，不进入 LOCKED |
| LOCKED | `pointerlockchange` + `document.pointerLockElement === scr` | 锁定成功 | LOCKED | 更新 UI（隐藏提示、进入相对模式） |
| LOCKED | 用户按 **Esc** | 浏览器 default unlock gesture | IDLE | **浏览器层强制退出**，JS 无法阻止；`pointerlockchange` 触发，`pointerLockElement === null` |
| LOCKED | `document.exitPointerLock()` | 我们的 Alt 逃生门 / 关闭画面 / 切账号 | IDLE | 异步；`pointerlockchange` 触发 |
| LOCKED | 锁定元素从 DOM 移除 / 页面隐藏 | 浏览器自动解锁 | IDLE | `pointerlockchange` 触发 |
| LOCKED | 其它 iframe 尝试 `requestPointerLock` | 已有元素锁定时 | 不变 | 新请求触发 `pointerlockerror`（一次只能锁一个 iframe） |
| IDLE | Esc 解锁后**立即**再 `requestPointerLock` | default unlock gesture 后 | 拒绝 | 即使有 transient activation 也会失败（【规范】明确：Esc 退出后立即重锁会被拒） |

### 1.3 三个关键坑（【规范】原文依据）

1. **Esc 后立即重锁会失败**：`requestPointerLock()` 在「用户经 default unlock gesture 退出后」立即调用会失败，即使有 transient activation。**设计对策**：Esc 退出后进入「冷却期」（≥500ms）或等用户下一次显式点击再重锁，不能在同一 mousemove/keydown 里重试。
2. **requestFullscreen 会消耗 transient activation**：若同时要全屏 + 锁定，必须**先 `requestPointerLock()` 再 `requestFullscreen()`**，顺序反了锁定会因失去激活态而失败【规范】。
3. **Promise vs 非 Promise 双版本**：当前规范 `requestPointerLock()` 返回 Promise，但多数浏览器仍实现非 Promise 版本（返回 undefined）。**不能依赖 `.then()`**，必须以 `pointerlockchange/pointerlockerror` 事件为准【规范】。

---

## 2. 锁定态 vs 非锁定态的输入语义差异

| 维度 | 非锁定态 | 锁定态 |
|---|---|---|
| 位置来源 | `clientX/clientY`（绝对，受屏幕边界限制） | `movementX/movementY`（相对，**无边界**，可无限旋转） |
| 光标 | 显示 | 隐藏（浏览器自动） |
| 事件目标 | 命中的元素（`#scr` 及其子元素） | **锁定元素（`#scr`）**，鼠标事件持续定向到它 |
| `movementX/Y` 有效事件 | 仅 mousemove/pointermove/pointerrawupdate 非零；**单位在浏览器间不统一**（物理/逻辑/CSS 像素） | 同左，但不会撞到屏幕边缘 |
| 现有绝对 move | 可发 `move`（绝对定位） | 应停发 `move`，只发 `moverel` |

**关键语义（【规范】movementX）**：`movementX = currentEvent.screenX - previousEvent.screenX`，在 `mousemove` 之外的任何事件恒为 0。**浏览器间单位不统一**（Chrome 物理像素 / 其它逻辑像素 / CSS 像素），需要做一个可调的**灵敏度系数**（sensitivity），不能假设 1 个 movement 像素 = 1 个远端像素。

**设计结论**：锁定态监听 `mousemove`（不是 pointermove，锁定时鼠标无 pointer 语义），把 `movementX/Y × sensitivity` 累加进现有 `lookAcc`，复用 `lookDelta()` 的 8ms 合并与 `flushLook()` 的 moverel 发送。**点击语义不变**：锁定态下 `mousedown/up` 仍正常触发，用于发 `down/up/click`。

---

## 3. 成熟产品的「Alt 逃生门」精确方案

### 3.1 月光浏览器客户端（moonlight-chrome，【外部源码】input.cpp）

一手源码 `input.cpp`（sha 43ef38309aa2061b8b5b28cfbb8fecf9d637c4fb）确认：

- **锁定时点击画面才进入锁定**：`MOUSEDOWN` 分支里「若未锁定 → `LockMouseOrJustCaptureInput()` 并 `return true`」，**第一次点击被锁定动作吃掉，不发给远端**（避免「点击进锁定」变成「远端也点了一下」）。
- **相对位移批量上报**：`m_MouseDeltaX += posDelta.x()` 累积，`ReportMouseMovement()` 在下一个输入轮询窗口批量 `LiSendMouseMoveEvent(deltaX, deltaY)`——与我们 lookAcc 8ms 合并**完全同构**。
- **逃生门 = 修饰键组合，不是单纯 Alt**：`KEYDOWN` 里，当 `modifiers == MODIFIER_ALT|MODIFIER_CTRL|MODIFIER_SHIFT`：
  - 若键码为 **Q**（`0x51`）→ `StopConnection()` 直接断连；
  - 其它 → `m_WaitingForAllModifiersUp = true`，**等 Alt/Ctrl/Shift 全部抬起后** `UnlockMouseOrJustReleaseInput()`。
- 即月光用「**Ctrl+Alt+Shift 三键按住 → 全部抬起 = 解锁鼠标**」，Q 加在其中 = 断连。**单纯按 Alt 不解锁**（因为 Alt 是游戏里常见键，会误触）。

### 3.2 云原神 / 原神 PC 的 Alt 语义（【未核实】）

- **原神 PC 是原生客户端**（Unity/米哈游自研），不涉及浏览器 Pointer Lock，「Alt 解锁」无对应概念——其鼠标锁定由游戏引擎（SetCursor/CaptureCursor）实现。**标注：未核实，且与本网页端场景不可直接类比**。
- **云原神（浏览器网页版）** 基于 cg-sdk（米哈游云游戏 SDK），**闭源未逆向**，其 Pointer Lock 与 Alt 逃生门的具体键位**未核实**。公开可观察事实：云原神网页端确实使用 Pointer Lock 实现转视角，但这属于「可观察现象」，键位细节不臆造。

### 3.3 本方案的 Alt 逃生门设计（对齐任务要求）

任务要求「按 Alt 退出锁定」，与月光的「组合键」不同——**因为我们的网页端必须给用户一条无需记忆组合键的显式退路**。设计：

- **keydown 监听 Alt**（`event.key === 'Alt'` 或 `event.altKey`），在锁定态时调 `document.exitPointerLock()`。
- **退出后恢复**：`pointerlockchange` 里检测 `pointerLockElement === null` → ①重新显示光标（浏览器自动，无需 JS）②把 `mouseMode` 切回 `'absolute'`（现有绝对模式）③清空 `lookAcc` 残留位移④UI 提示「已退出鼠标锁定，回到绝对模式」。
- **防误触**：Alt 是游戏内常用键（如 ALT 切换、原神部分功能）。**设计建议给 Lead 二选一**：
  - (a) 单纯 Alt：最直观，但游戏内按 Alt 会意外解锁（需用户再点画面重进）；
  - (b) 月光式「**按住 Alt 1 秒**」或「**Alt+点击**」：降低误触，但多一层记忆。
  - 本设计文档默认推荐 **(a) 单纯 Alt 立即解锁 + 画面内醒目提示**，因为「锁死后无退路」的风险远大于「误触后重进一次」的成本；具体键位由 Lead 拍板。

### 3.4 与 Esc 的关系

- **Esc 是浏览器层的 default unlock gesture**，无需 JS 监听，锁定态按 Esc 必退出（浏览器保证，**这是「锁死后唯一无条件的逃生口」**）。
- Alt 逃生门是**额外的、用户可预期的、可在 UI 上明示的**退出方式。两者共存：Esc 兜底，Alt 显式。

---

## 4. iframe 场景的坑

### 4.1 云原神在跨源 iframe 里的 Pointer Lock 限制（【规范】）

- **sandbox 必需 token**：`<iframe sandbox="allow-pointer-lock">`，否则 sandbox iframe 内 `requestPointerLock()` 直接被拒【规范 MDN IFrame limitations】。
- **一次只能锁一个 iframe**：锁定 A iframe 后再锁 B iframe 会 `pointerlockerror`；须先 unlock A 再 lock B【规范】。
- **锁定目标文档必须 active**：跨源 iframe 的文档须处于 active 状态（用户已交互）。

### 4.2 我们网页端的实际情况（【代码事实】）

- **grep `docs/web-index.html` 全文：0 处 iframe / frame / contentWindow / sandbox**——我们网页端是**单文档**，画面直接画在 `#scr > #vframe` canvas 里，没有嵌入任何远程文档。
- 因此**不存在跨源 iframe 的 requestPointerLock 权限/目标文档问题**，锁定请求直接发到 `#scr` 元素即可，无需 sandbox token，无需处理「锁哪个 iframe」。

### 4.3 设计结论

锁定目标 = **`#scr`**（画面容器，`touch-action:none; cursor:default`，已有 pointerdown/up/move/wheel 监听）。理由：
- `#vframe` canvas 是 `pointer-events:none`，事件都落在 `#scr` 上【代码事实 CSS L306-307】；
- 锁定 `#scr` 后 mousemove/mousedown 持续定向到它，与现有事件监听位置一致，最小改动。

---

## 5. 与现有代码的接缝（不重造轮子）

### 5.1 现有可复用资产（【代码事实】）

| 现有资产 | 位置 | 用途 |
|---|---|---|
| `lookAcc = { dx, dy, lastSent, timer }` | web-index.html:1464 | 相对位移累积器 |
| `LOOK_HZ_MS = 8` 合并窗口 | :1465 | 8ms 节流 |
| `lookDelta(dx, dy)` | :1472-1486 | 累加 + 8ms 节流 + timer 冲刷 |
| `flushLook()` → `sendInput('moverel', ...)` | :1467-1471 | 相对位移发送 |
| `mouseMode = 'absolute'/'relative'` | :1419 | 双模式 |
| 右键 look 模式（`mouseRight: true` → lookDelta） | :1522-1528, :1610-1616 | 已有「相对转视角」入口 |
| `sendInputNoWait / sendMoveThrottled` | :1347-1352, :1551-1571 | move 的 latest-wins |

### 5.2 接入方案（设计，不写代码）

1. **新增第三模式 `mouseMode = 'locked'`**（或独立布尔 `pointerLocked`），持久化到 `localStorage`（同现有 `MOUSE_MODE_KEY` 机制）。
2. **进入锁定**：在 `#scr` 的 `pointerdown`（桌面鼠标、左键）时调 `scr.requestPointerLock()`；用 `pointerlockchange` 判定成功后才切 `pointerLocked = true`。
3. **锁定态 mousemove**：新增 `#scr` 的 `mousemove` 监听（仅 `pointerLocked` 时生效），`lookDelta(e.movementX * SENSITIVITY, e.movementY * SENSITIVITY)`——**直接复用 lookDelta/lookAcc/flushLook 全套 8ms 合并链路，零重造**。
4. **锁定态点击**：`mousedown/up` 复用现有 `sendInputOrdered('down'/'up'/'click')`；**首次进锁定的那次点击吃掉**（对齐月光，避免「点进锁定 = 远端也点一下」）。
5. **退出锁定**：Esc（浏览器自动）或 Alt 逃生门（`keydown` + `exitPointerLock()`）→ `pointerlockchange` 里 `pointerLocked = false` + 清空 `lookAcc` + 切回 `absolute`。
6. **与现有右键 look 的差异**：右键 look 需要「按住右键」；Pointer Lock 是「进入后无需按任何键，纯移动转视角」。两者可并存：锁定态优先，未锁定保留右键 look 兜底。

### 5.3 sensitivity 系数（【规范】movementX 单位不统一）

- 因浏览器/OS 对 `movementX` 的单位不一致（物理/逻辑/CSS 像素），必须加**灵敏度系数**，默认 `1.0`，并**持久化**（如 `localStorage`），首次实现在真机标定后回填默认值。

---

## 6. 边界与回退

| 场景 | 判定 | 行为 |
|---|---|---|
| 移动端触屏 | `'ontouchstart' in window` 或 pointerType === 'touch' | **不启用 Pointer Lock**（触屏无光标，requestPointerLock 无效）；保留现有触屏 `moverel`（单指滑动转视角）与绝对 move 状态机 |
| 桌面鼠标 | pointerType === 'mouse' | 启用锁定 |
| 不支持 Pointer Lock 的浏览器 | `!('pointerLockElement' in document)` | 降级到现有绝对模式 + 右键 look 相对模式，不发锁定请求 |
| sandbox iframe / 无 transient activation | `pointerlockerror` 触发 | 降级到绝对模式，UI 提示「当前环境不支持鼠标锁定」 |
| Esc 强制退出 | 浏览器 default unlock gesture | 无条件退出，UI 恢复到绝对模式（浏览器已重新显示光标） |
| 锁定元素被移除/页面隐藏 | `pointerlockchange` 且 pointerLockElement === null | 清理状态，停发 moverel |

---

## 7. 验收设计（真机验证锁定/退出/逃生门不锁死）

### 7.1 核心原则：锁死不可接受

「锁死后无光标无退路」是不可接受的失败模式。**三重兜底保证用户永远能退出**：
1. **Esc** = 浏览器层 default unlock gesture，**即使我们的 JS 崩溃也会生效**（浏览器自身强制解锁并恢复光标）。
2. **Alt 逃生门** = 我们的显式退出（若 JS 正常）。
3. **画面外点击** = 锁定态光标隐藏，但用户仍可移动鼠标到屏幕边缘外的系统区域（锁定时鼠标仍可移动，只是不显示位置）——若 JS 挂了，浏览器 Esc 是最终兜底。

### 7.2 真机验收清单

| # | 项 | 操作 | 通过判据 |
|---|---|---|---|
| V1 | 进入锁定 | 桌面鼠标点画面 | 光标隐藏，`document.pointerLockElement === #scr`，移动鼠标画面持续转视角 |
| V2 | 相对位移正确 | 锁定态缓慢匀速移动鼠标 | 远端视角旋转与鼠标移动方向/幅度一致，无反向、无漂移 |
| V3 | 点击不粘连 | 锁定态点击 | 远端点击正确，无「进锁定那次被误点击」 |
| V4 | Esc 退出 | 锁定态按 Esc | 光标立即恢复，`pointerLockElement === null`，回到绝对模式，移动鼠标不再转视角 |
| V5 | Alt 逃生门 | 锁定态按 Alt | 同上退出；UI 提示已退出 |
| V6 | Esc 后重锁冷却 | Esc 退出后立即点画面重锁 | 要么进入冷却（≥500ms 内不响应），要么等下一次点击才成功，**不出现 error 抖动** |
| V7 | 触屏回退 | 手机/触屏打开 | 无锁定 UI，触屏单指滑动仍转视角（现有 moverel），不触发锁定 |
| V8 | 不支持浏览器降级 | 无 Pointer Lock 的浏览器 | 走绝对模式 + 右键 look，无报错 |
| V9 | 锁死兜底 | 锁定态断网/JS 抛异常 | 按 Esc 仍能退出（浏览器层），光标恢复 |
| V10 | 灵敏度 | 同一台机器锁定态转 90° 视角 | 记录所需鼠标位移，标定 sensitivity，二次进入一致 |

### 7.3 锁死探测（自动化护栏）

- 在 `pointerlockchange` 里加断言：若 `pointerLocked === true` 但 `document.pointerLockElement === null`（异常解锁），立即清理状态并恢复到绝对模式，避免「JS 认为锁定但实际未锁定」的状态不一致。

---

## 8. 证据索引

- **【规范】MDN**：Pointer_Lock_API（requestPointerLock/pointerlockchange/pointerlockerror/pointerLockElement/exitPointerLock、Esc default unlock、iframe 限制、unadjustedMovement）；Element.requestPointerLock（transient activation、requestFullscreen 消耗激活态、Promise 双版本）；MouseEvent.movementX（screenX 差值、单位不统一、仅在 mousemove/pointermove/pointerrawupdate 非零）。
- **【外部源码】moonlight-chrome** `input.cpp`（sha 43ef38309aa2061b8b5b28cfbb8fecf9d637c4fb）：LockMouseOrJustCaptureInput/MOUSE_DOWN 首次点击吃掉/m_MouseDeltaX 累加+批量 LiSendMouseMoveEvent/Ctrl+Alt+Shift(+Q) 组合键逃生门/WaitingForAllModifiersUp 抬起解锁。
- **【外部源码】sunshine** `src/input.cpp`（本仓库 artifacts/latency-playbook-src/sunshine_input.cpp）：NV_REL_MOUSE_MOVE_PACKET 相对 + batch 累加(16位溢出终止)/NV_ABS 绝对 latest-wins（L1883-1917, L2594-2614）。
- **【外部源码】selkies-gstreamer**：前端 `web/selkies-web-core/lib/input.ts`（298KB，超 web_fetch 截断未逐行核对）、`touch-controls.ts`/`gamepad.ts` 文件清单已核实存在；其 Pointer Lock/Alt 逃生门**具体实现未核实**（大文件截断），不臆造。
- **【未核实】云原神 cg-sdk / 向日葵 / 原神 PC**：闭源未逆向，Pointer Lock 与 Alt 键位细节不臆造。
- **【代码事实】docs/web-index.html**：无 iframe 结构（grep 0 处）；#scr 容器 L585/#vframe canvas pointer-events:none L306-307；mouseMode 双模式 L1419；lookAcc/lookDelta/flushLook/8ms 节流 L1463-1486；右键 look 模式 L1522-1528；sendInputOrdered/sendMoveThrottled L1315-1352。



---

## 10. ESC 冲突专题（游戏用 ESC 开菜单 vs Pointer Lock 锁鼠标）

> 本专题核实：锁定态按 ESC 时，浏览器是「解锁」还是「把 ESC 当普通按键派发」？游戏（远端注入）还能否收到这个 ESC 开菜单？

### 10.1 规范层面：ESC 的 keydown 是否派发、能否 preventDefault？——规范空白点

**结论：W3C Pointer Lock 规范【未定义】ESC 键的 keydown 事件派发与 preventDefault 行为。**

证据（【规范】https://w3c.github.io/pointerlock/ 原文逐字核对）：

1. **§Requirements 只规定「必须有 default unlock gesture」，不规定它的事件语义**：
   > "A **default unlock gesture** must always be available that will exit pointer lock with the user agent's pointer-lock target."
   > note："The **ESC key** is the recommended default unlock gesture for user agents with keyboard input."

2. **§Exit Pointer Lock 算法只有三步，全程无键盘事件**：
   > (1) 系统光标重新显示并定位到 cursor position；(2) 在 user interaction task source 上 queue task，向锁定元素的 node document fire `pointerlockchange`；(3) 将 pointer-lock target/options/cursor position 置 null 退出锁定态。

3. **规范全文检索结论**：`keydown`、`KeyboardEvent`、`preventDefault`、`cancelable` 四个词在规范正文中 **0 次出现**。即：规范没有说 ESC 的 keydown 会派发到页面、也没说会被消费、更没说能否 preventDefault。这是一个**规范空白点**，行为由各 UA 自行实现。

**推论**：
- ESC 触发解锁是**浏览器层的强制安全机制**（保证用户永远能退出锁定，见规范 §Security「user agents will prevent [malicious lock] by always providing a mechanism to exit pointer lock」）。
- 因此 **`preventDefault()` 不可靠也不能依赖**：即使某些 UA 会派发 ESC 的 keydown，浏览器也不承诺允许脚本取消解锁手势——解锁是 UA 的默认行为，规范未提供可取消的键盘事件语义。

### 10.2 ESC 触发解锁后，焦点与后续键盘事件的流转

依据规范（【规范】§Exit Pointer Lock + §Requirements）：

- **解锁不改变键盘焦点**：规范明确「Moving focus between elements of the navigable/active document, including between browsing contexts, does **not** exit pointer lock」——反之亦然，解锁动作本身不移动焦点。焦点仍留在锁定前的活动文档。
- **这个 ESC 本身大概率被浏览器消费**（作为 unlock gesture），**不派发到页面 JS**，因此远端游戏收不到这次 ESC（我们的键盘注入通路 `document keydown -> sendInput('key', {key:'Esc'})` 不会被触发）。
- **后续 ESC**（解锁之后用户再按）**正常派发**：此时已非锁定态，浏览器不再拦截，ESC 走现有 `document keydown`（web-index.html L1734）-> `keyName` 把 `'Escape'` 映射成 `'Esc'`（L1726）-> `sendInput('key', {key:'Esc'})` 注入远端开菜单（L1743）。

> 注：上述「ESC 被浏览器消费、不派发 keydown」是主流 UA（Chromium/Firefox）的已实现行为，但规范未强制规定，属 UA 内部实现。**标注：需真机验证**（headless 环境无法进入 Pointer Lock，无法在本会话实测）。

### 10.3 成熟产品的处理

| 产品 | 场景 | 处理 | 证据等级 |
|---|---|---|---|
| **moonlight-chrome** | 浏览器客户端（Pepper MouseLock，非 Web Pointer Lock） | ①逃生门用 Ctrl+Alt+Shift 组合键（KEYDOWN 里 `modifiers == ALT|CTRL|SHIFT` 时，非 Q 键 -> `m_WaitingForAllModifiersUp = true`，等全部修饰键抬起后 `UnlockMouseOrJustReleaseInput()`；+Q -> 断连）；②KEYDOWN 分支不处理 ESC（0x1B）——ESC 解锁交给 Pepper 层 LockMouse 内部，月光自己的键注入不碰 ESC | 【外部源码】input.cpp L274-315（sha 43ef38…） |
| **moonlight-qt（桌面）** | 原生客户端 | 相对鼠标走 raw input，无浏览器 Pointer Lock；无 ESC 冲突（ESC 是游戏键，客户端不拦截） | 【外部源码】d3d11va/input（本仓库 latency-playbook-src 同源） |
| **云原神（cg-sdk）** | 网页版，Pointer Lock + 游戏内 ESC 开菜单 | 闭源未逆向。可观察现象：其转视角用 Pointer Lock，游戏内 ESC 开菜单。它必然处理了「ESC 同时是解锁键+菜单键」的矛盾——最可能的做法是「第一次 ESC 解锁鼠标、第二次 ESC 开菜单」，或「非 ESC 逃生门」；具体实现未核实，不臆造 | 【未核实】 |
| **原神 PC / 向日葵** | 原生客户端 / 闭源网页 | 原生客户端无浏览器 Pointer Lock，无此冲突；向日葵网页端键位细节未核实 | 【未核实】 |

**关键区分**：月光（chrome 版）之所以「不冲突」，是因为它主动用组合键做逃生门、ESC 留给 Pepper 层解锁，游戏要开菜单的 ESC 走的是另一条非锁定的键盘通路。但即便如此，ESC 在锁定态仍会被 Pepper 层拦截——月光桌面版才是「ESC 全程透传」的形态（原生客户端无 Pointer Lock）。

### 10.4 我们的可行设计（四方案对比，推荐 A）

**前提事实**：ESC 解锁是浏览器强制机制，JS 无法禁用（§10.1）。因此「锁定态让 ESC 透传进游戏」在 Web Pointer Lock 下不可能——ESC 永远会先被浏览器消费为解锁手势。

| 方案 | 行为 | 优点 | 缺点 | 推荐度 |
|---|---|---|---|---|
| **A. ESC 只解锁，二次 ESC 开菜单** | 锁定态 ESC -> 浏览器解锁（远端收不到）；用户看到光标恢复后，再按一次 ESC -> 走现有 document keydown 注入远端开菜单 | 最简单、行为可预测、不吞不抢、与主流云游戏网页端一致 | 开菜单需按两次 ESC（首次解锁） | **★ 推荐** |
| **B. 组合键逃生门（月光式）+ ESC 透传** | 用 Ctrl+Alt+Shift/Alt 做逃生门，ESC 全程透传注入远端 | ESC 一次直达开菜单 | **不可行**：浏览器仍会用 ESC 解锁（default unlock gesture 无法禁用），ESC 在锁定态还是会被拦截，透传失效 | ✗ |
| **C. ESC 解锁 + 补发一次 ESC 开菜单** | `pointerlockchange`（解锁）里补发一次 `sendInput('key',{key:'Esc'})` | 一次 ESC = 解锁 + 开菜单 | 用户「只想解锁不想开菜单」时误开菜单；且远端游戏此时可能还在全屏，行为需 Lead 拍板 | 可选 |
| **D. 锁定态主动 preventDefault ESC** | document keydown 里锁定时 `e.preventDefault()` 尝试吞掉 ESC | — | 不可靠：§10.1 规范未定义、ESC 解锁常不派发 keydown、preventDefault 无效 | ✗ |

**推荐 A（ESC 只解锁，二次 ESC 开菜单）**，理由：
1. 完全顺应浏览器安全机制（ESC 解锁不可阻止），零对抗；
2. 与云原神等主流云游戏网页端「先解锁、再开菜单」的可观察行为一致；
3. 复用现有键盘注入通路（web-index.html L1724-1744 已把 ESC 映射为 'Esc' 注入），锁定态与解锁态行为边界清晰。

**配套 UX（写入 §10 供实现任务引用）**：
- 解锁时（`pointerlockchange` 且 `pointerLockElement === null`）显示提示条「已解锁鼠标 · 再按 ESC 打开游戏菜单 · 点击画面重新锁定」，避免用户困惑「为什么 ESC 没反应」。
- 若要「一次 ESC = 解锁 + 开菜单」（方案 C），需 Lead 明确拍板，因为这会改变「解锁」与「开菜单」两个动作的耦合。

### 10.5 需 Lead 决策

1. **ESC 语义**：方案 A（二次 ESC 开菜单，推荐）还是方案 C（一次 ESC = 解锁+开菜单）？
2. **逃生门键位**：本专题 §10.3 确认月光用组合键（Ctrl+Alt+Shift）防误触；结合前文 §3.3，逃生门仍是「Alt 单独」或「组合键」二选一待拍板。ESC 本身不可禁用，只能作为「浏览器兜底解锁」，不能作为「游戏内菜单键」。

---

## 9. 边界遵守自查

（更新）本任务 + 上一任务（task_mwte6oi21s0rgj）均为只研究+设计，未改任何产品代码/脚本/配置；未碰 dist/、Relay/、Android/；未动 docs/web-index.html；唯一写入为本设计文档。外网仅读 MDN + W3C 规范 + GitHub 公开 API（moonlight-chrome 源码）。README.md 登记留给 Lead。
