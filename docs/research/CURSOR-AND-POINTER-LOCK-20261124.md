# 鼠标落点不准 + 无光标问题诊断（CURSOR-AND-POINTER-LOCK）

> 日期：2026-11-24
> 作者：viewer-auditor（dsh-team task `task_mwthev353h6bbb`）
> 范围：**只读定位 + 设计诊断，未改任何产品代码**。唯一写入为本诊断文档。
> 证据标记：【代码事实】= `docs/web-index.html` 逐行可证；【推断】= 有依据未实测；【待验证】= 需真机确认。

---

## 0. 结论（三句话）

1. **Pointer Lock 未生效的根因**：锁定请求 `requestPointerLock()` 被放在了 `#scr` 的 `pointerdown` 里，但**首次点击被无条件 `return` 吃掉**（L1658-1662），一旦 `requestPointerLock` 因**非 secure context（http 明文）、非用户手势激活窗口、或 headless/自动化环境**而失败（触发 `pointerlockerror`），`pointerLocked` 保持 false，**后续每次点击都走同一 `return` 分支被吞**，造成「点画面没反应、进不了锁定态、连绝对模式的点击也失效」【代码事实 + 推断】。
2. **落点不准的根因**：`norm()` 用 **`#scr` 的 `getBoundingClientRect()`** 计算缩放（L1407），但 canvas 实际显示尺寸被 `max-height:70vh`（非 viewonly，L306）/ `max-height:calc(100vh - 46px)`（viewonly，L286）+ `object-fit:contain` 夹住。当画面实际渲染高度 < `#scr` 高度时，`norm` 算出的 `ox/oy` 居中偏移与真实画面位置**不一致** → 落点系统性偏移【代码事实】。
3. **无光标方案**：绝对模式下在 canvas 上叠一个跟随 `move` 的可见光标指示器（复用 `norm` 逆变换），解决「不知道落点在哪」；锁定态光标隐藏是预期（转视角不需要光标），非锁定态才显示可见光标。

---

## 1. Pointer Lock 为什么没实际生效

### 1.1 锁定进入代码路径（【代码事实】）

- `#scr` 的 `pointerdown` 监听（L1628）：桌面鼠标左键且 `PL_SUPPORTED && !pointerLocked && Date.now() >= plLockCooldownUntil` 时 → `try { $('scr').requestPointerLock(); } catch (err) {} return;`（L1658-1662）。
- `PL_SUPPORTED = ('pointerLockElement' in document) && ('requestPointerLock' in document.body)`（L1458）。
- `pointerlockchange` 监听（L1526）：`document.pointerLockElement === $('scr')` 时 `pointerLocked = true`（L1528-1533）。
- `pointerlockerror` 监听（L1557）：`if (!document.pointerLockElement) pointerLocked = false;`（L1559）。

### 1.2 根因一：首次点击被无条件吞掉（【代码事实】）

```
if (e.pointerType === 'mouse' && btn === 'left' && PL_SUPPORTED
    && !pointerLocked && Date.now() >= plLockCooldownUntil) {
  try { $('scr').requestPointerLock(); } catch (err) {}
  return;    // ★ 无论锁定是否成功，都 return，点击被吃掉
}
```

- 设计意图（对齐月光 input.cpp MOUSE_DOWN）：「首次点击用于进入锁定，不发远端」。
- **但缺陷**：`requestPointerLock()` 是**异步**的，成功与否要等 `pointerlockchange/error`。这里的 `try/catch` 只捕获同步异常（如非支持环境抛错），**捕获不到异步失败**（非 secure context / 无 transient activation / headless 拒绝）。
- **后果链**：`requestPointerLock` 异步失败 → `pointerlockerror` 触发 → `pointerLocked` 已是 false（本来就 false）→ 状态不变 → **下次点击再次走同一分支，再次被吞** → 用户永远进不了锁定态，且**绝对模式的左键点击也失效**（因为每次左键点击都命中这个 return）。

### 1.3 根因二：非 secure context 限制（【推断，待验证】）

- 网页由 `http://主机:端口` 提供（局域网直连明文 HTTP，见 ANDROID-APP-DESIGN §2.1）。
- **Pointer Lock 在非 secure context 下 Chrome 的行为**：localhost/127.0.0.1 被特殊豁免（视为 potentially trustworthy），但**非 localhost 的 http 明文**（如 `http://192.168.x.x:port`）可能被拒绝，触发 `pointerlockerror` 或直接不锁定【推断，需真机验证】。
- 若用户访问的是局域网 IP（手机/平板连主机），而非 localhost，Pointer Lock 可能因非 secure context 失败 → 命中根因一的「点击被吞」恶性循环。

### 1.4 根因三：requestPointerLock 需要 transient activation（【代码事实 + 规范】）

- 规范要求「transient activation is required」——必须用户手势内调用（pointerdown 满足）。
- 但若首次点击的 `requestPointerLock` 因冷却期（Esc 后 500ms）被跳过（L1659），或者目标文档非 active，也会失败。

### 1.5 修复方向（诊断结论，供实现任务参考）

1. **不吞掉首次点击**：改为「先发 click（绝对模式正常点击），锁定成功后由 pointerlockchange 切换状态，锁定态期间鼠标移动走 movementX/Y」。首次点击是否发远端由 Lead 决策——若对齐月光「首次点击吃掉」，则必须保证锁定一定成功（secure context + 用户手势），否则回退发 click。
2. **锁定失败回退**：`pointerlockerror` 里把 `pointerLocked` 置 false，并**让下一次点击走绝对模式正常 click**（不进入锁定分支），避免死循环。
3. **secure context 兜底**：若检测到非 secure context（`!window.isSecureContext`），禁用 Pointer Lock 进入（降级绝对模式），避免反复失败。

---

## 2. 绝对坐标映射偏差（落点不准）

### 2.1 norm() 的坐标映射（【代码事实】）

```
function norm(e) {
  const el = $('scr'), rect = el.getBoundingClientRect();   // ★ 用 #scr 的矩形
  const cv = $('vframe');
  const iw = (cv && cv.width > 0) ? cv.width : 16;            // canvas 内部分辨率
  const ih = (cv && cv.height > 0) ? cv.height : 9;
  const sc = Math.min(rect.width / iw, rect.height / ih);     // 用 #scr 尺寸算缩放
  const dw = iw * sc, dh = ih * sc;
  const ox = (rect.width - dw) / 2, oy = (rect.height - dh) / 2;  // 居中偏移
  const cx = (e.clientX) - rect.left - ox;
  const cy = (e.clientY) - rect.top - oy;
  return { nx: clamp(cx / dw), ny: clamp(cy / dh) };
}
```

### 2.2 根因：canvas 显示尺寸 ≠ #scr 尺寸（【代码事实】）

- canvas 的 CSS（非 viewonly，L306-307）：`width:100%; height:auto; max-height:70vh; object-fit:contain`。
- canvas 的 CSS（viewonly，L285-288）：`width:auto; height:auto; max-width:100%; max-height:calc(100vh - 46px); object-fit:contain`。
- **`norm` 用 `#scr` 的 rect 计算，但 canvas 实际渲染尺寸可能被 `max-height` 夹住而 < `#scr` 高度**。此时 canvas 在 `#scr` 内（`display:flex` 布局下）居中，`norm` 算出的 `oy` 与真实画面顶部不一致 → **纵向落点偏移**（横向因 `width:100%` 通常贴合，偏移较小）。
- 此外，`object-fit:contain` 下若 canvas 元素尺寸与视频比例不一致（letterbox），画面内容本身还有黑边，`norm` 未考虑黑边 → 进一步偏移。

### 2.3 DPR/缩放的影响（【代码事实】）

- canvas 内部分辨率 `canvas.width/height = frame.displayWidth/displayHeight`（L1089-1092），即**解码帧的物理分辨率**。
- `norm` 用 CSS 像素（`clientX/getBoundingClientRect` 均为 CSS px）除以 `sc`（CSS px / 物理 px），**单位换算本身是对的**（物理 px 内部坐标）。
- **但 DPR 无关**：`getBoundingClientRect` 和 `clientX` 都是 CSS px，`canvas.width` 是物理 px，两者比值就是缩放，DPR 已被 `width:100%` 的 CSS 布局吸收。**真正的偏差来自「canvas 显示尺寸被 max-height 夹住 + letterbox 黑边」，不是 DPR**【代码事实】。

### 2.4 修复方向

1. **用 canvas 的实际显示矩形（`getBoundingClientRect()`）而非 #scr 的矩形** 计算 `sc/ox/oy`，这样 max-height 夹住、居中、letterbox 都自动纳入。
2. 或用 `ctx.getTransform()`/已知的 drawImage 目标尺寸反推真实渲染区域。

---

## 3. 可见光标指示方案（解决「无光标不知道落点」）

### 3.1 现状（【代码事实】）

- 现有 `#rip` 是「点击涟漪」元素（L316-319），`ripple(e)` 在 `click` 时短暂显示（L1419-1424），**不是跟随光标**，点击瞬间才出现。
- `#scr` 的 `cursor:default`（L298），锁定态浏览器隐藏光标，非锁定态是系统默认箭头。
- **无「跟随鼠标 move 的实时位置指示」**——绝对模式下用户移动鼠标时看不到「远端光标在哪」，只能凭点击后的涟漪确认。

### 3.2 方案（设计，对齐云原神可见光标）

**目标**：绝对模式下，在 canvas 上叠一个**跟随鼠标 `move` 位置的可见光标**（小圆点/箭头），实时反映「远端光标落点」；锁定态隐藏（转视角不需要光标）。

```
// 伪代码（设计，不实现）
const cursor = $('plCursor');  // 新增 <div id="plCursor">，CSS 绝对定位 + pointer-events:none
function updateVisibleCursor(nx, ny) {
  const rect = $('scr').getBoundingClientRect();
  // 复用 norm 的逆变换：nx,ny(0..1) → 画面内 CSS px
  // ★ 必须与 norm 用同一套 sc/ox/oy（修 §2 的偏差后），保证指示位置=实际落点
  const x = ox + nx * dw, y = oy + ny * dh;
  cursor.style.left = x + 'px';
  cursor.style.top = y + 'px';
}
// 非锁定态 pointermove 时更新（锁定态隐藏光标，转视角不显示）
```

- **关键**：`updateVisibleCursor` 与 `norm` 必须共用同一套坐标变换，否则指示位置与实际落点仍会偏差（这正是 §2 要修的）。
- **显示时机**：绝对模式（非锁定态）+ 鼠标 pointermove；锁定态隐藏（转视角时用户不需要落点）；触屏不显示（触屏无 hover 落点）。
- **样式**：小圆点/十字，半透明，`pointer-events:none`（不挡点击），与远端光标颜色区分。

### 3.3 与 Pointer Lock 的耦合

- 绝对模式（非锁定）：显示可见光标，落点=鼠标位置。
- 锁定态：隐藏光标，落点概念不存在（转视角），鼠标移动=相对位移。
- **两者的切换**：`pointerLocked` 变化时切换可见光标显隐。

---

## 4. 修复优先级（供 Lead 决策）

| 优先级 | 问题 | 修复 | 成本 |
|---|---|---|---|
| **P0** | 锁定失败导致点击被吞（死循环） | §1.5：锁定失败回退绝对模式，不吞首次点击 | 小 |
| **P0** | 落点不准 | §2.4：norm 用 canvas 实际显示矩形 | 小 |
| **P1** | 无光标指示 | §3.2：canvas 叠可见光标 | 中 |
| P1 | 非 secure context 锁定失败 | §1.5：检测 isSecureContext 降级 | 小 |

---

## 5. 证据索引

- **【代码事实】docs/web-index.html**：
  - Pointer Lock 状态机 L1450-1568（PL_SUPPORTED L1458、锁定进入 L1658-1662、pointerlockchange L1526、pointerlockerror L1557）；
  - norm 坐标映射 L1403-1416（用 #scr rect）；
  - canvas CSS max-height L306-307 / viewonly L285-288；
  - canvas 尺寸设置 L1089-1092（displayWidth/Height）；
  - #rip 涟漪 L316-319 / ripple() L1419-1424；
  - 绝对坐标输入点 L1640/1663/1769/1777/1797/1815/1824/1838。
- **【规范】** MDN Pointer Lock API：requestPointerLock 需要 transient activation + 目标文档 active；非 secure context 可能拒绝（待真机验证）。
- **【待验证】** 非 localhost http 明文下 Pointer Lock 是否被拒；锁定失败时点击被吞的复现。

---

## 6. 边界遵守自查

未改任何产品代码/脚本/配置；未碰 dist/、Relay/、Android/；未动 docs/web-index.html；唯一写入为本诊断文档。README.md 登记留给 Lead。