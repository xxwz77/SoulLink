# 蓝球（#plCursor 虚拟光标）现状根因 + 成熟做法 + 改造方案

> 日期：2026-11-24
> 作者：cursor-ux（dsh-team `team_mwtjn0d77v8gtr` / task `task_mwtjocog8c3rop`）
> 范围：**只读审计 + 方案文档，未改任何产品代码**。唯一写入为本文档。
> 证据标记：【代码事实】= `Services/WebPages.cs` 逐行可证（由 `docs/web-index.html` 生成，改样式请改那个文件再跑 `scripts\sync-webpages.ps1`，见 WebPages.cs:6-8；下文所有行号以 **WebPages.cs** 为准，`docs/web-index.html` 对应位置约 -16 行）；【外部】= 成熟产品源码/文档（file:line 或 URL 可回查）；【推断】= 有依据未实测。

---

## 0. 结论摘要（先看这里）

1. **蓝球的三个用户可感现象全部是「设计边界」不是 bug，但边界组合起来让它必然像废功能**：
   - 只在本机鼠标显示时才显示 → 由显示条件 `!pointerLocked && e.pointerType !== 'touch'`（L1794）决定：**只有真实鼠标（非触屏）悬停移动时才更新显示**，锁定态/触屏/解锁后全部隐藏；
   - 点一下就隐藏 → 鼠标左键点击触发 Pointer Lock 请求（L1725-1731），锁定成功后 `pointerlockchange` 调 `hideVisibleCursor()`（L1591）——**蓝球被自己的「点击=进锁定」设计亲手关掉**；
   - 对操控没帮助 → 蓝球只回显**本机鼠标**的归一化落点（norm 的逆变换），**不反映远端光标的真实位置**。远端画面里根本没有光标（WGC 两处 `IsCursorCaptureEnabled=false`，WgcCapture.cs:174/578），444ms 画面延迟下「远端画面光标」更是无从谈起。用户看到的蓝球 = 本机鼠标的一个延迟为零的影子，而自己本机明明有系统光标 —— **信息量为零**。
2. **成熟产品的正解是「远端光标状态回传 + 客户端合成绘制」，不是本机鼠标影子**：Moonlight/Sunshine 把远端光标画进视频帧（Sunshine `IsCursorCaptureEnabled` 可配、WGC 光标合成）；Moonlight-qt 桌面端绝对模式直接画系统光标 + 远端帧内光标重合；浏览器端（selkies/云原神形态）在绝对模式下用 CSS `cursor` 或自绘 sprite 回显**服务端上报的光标位置与形状**；Guacamole 相对模式（触控板仿真）会「屏幕自动 pan 保持光标可见」——**没有任何一个成熟产品做「本机鼠标影子圆球」**。
3. **建议：删掉蓝球，替换为「远端光标位置回显」机制（分两档实现）**。最小改动档（P1）：服务端在输入注入后把实际光标位置（`GetCursorPos`）随帧侧信道或输入响应回传，前端在 canvas 上画一个与远端光标形状一致的自绘光标；零改动档（P0，立即可做）：绝对模式下隐藏本机系统光标（`cursor:none`）+ 让服务端 WGC 打开 `IsCursorCaptureEnabled(true)`，把光标画进画面帧 —— 这正是 Sunshine 的默认行为。**两档都不需要蓝球这个元素**。

---

## 1. 现状代码事实（#plCursor 全链路）

### 1.1 DOM 与 CSS（【代码事实】）

- DOM：`<div id="plCursor"></div>` 是 `#scr` 的子元素（WebPages.cs:628），叠在 `<canvas id="vframe">`（:625）之上。
- CSS（:335-340）：

```css
#plCursor{position:absolute;width:18px;height:18px;margin:-9px 0 0 -9px;
          border:1.5px solid var(--accent);border-radius:50%;
          background:rgba(255,255,255,.16);box-shadow:0 0 4px rgba(0,0,0,.45);
          pointer-events:none;display:none;z-index:12}
```

要点：18px 蓝色描边圆（var(--accent)）、`pointer-events:none` 不挡点击、默认 `display:none`、`margin:-9px` 使圆心对准落点。注释（:335-336）自述设计意图：「绝对模式跟随鼠标 move 的小圆点，锁定态隐藏」。

### 1.2 JS：显示 / 更新 / 隐藏（【代码事实】）

- `plCursorEl = $('plCursor')`（:1449）。
- `plCursorGeometry()`（:1450-1459）：与 `norm()`（:1426-1444）完全同源的几何 —— canvas 实际显示矩形（`getBoundingClientRect`）+ `object-fit:contain` letterbox 黑边扣除。**这是 2026-11-24 落点偏移修复的配套**（norm 注释 :1433-1435）。
- `updateVisibleCursor(nx, ny)`（:1460-1470）：把归一化落点逆变换回 `#scr` 局部坐标，`display:block`。**只被一个地方调用**（见 1.3）。
- `hideVisibleCursor()`（:1471-1473）：`display:none`。被三处调用：锁定成功（:1591）、解锁（:1612）、以及（经调用链）触屏/锁定态不显示。

### 1.3 显隐的完整真值表（【代码事实】—— 三个现象的根源）

`#scr` 的 `pointermove` 监听（:1789-1797）：

```js
if (pointerLocked && e.pointerType === 'mouse') return;          // L1792 锁定态：跳过
if (!pointerLocked && e.pointerType !== 'touch') {                // L1794 ★唯一显示入口
  const pc = norm(e);
  updateVisibleCursor(pc.nx, pc.ny);
}
```

`#scr` 的 `pointerdown`（:1725-1731）—— 桌面左键 + 未锁定 + 冷却期外：

```js
if (e.pointerType === 'mouse' && btn === 'left' && PL_SUPPORTED && window.isSecureContext
    && !pointerLocked && Date.now() >= plLockCooldownUntil) {
  const p0 = norm(e);
  plLockPending = { nx: p0.nx, ny: p0.ny };
  try { $('scr').requestPointerLock(); } catch (err) {}           // L1729
  return;                                                          // 首击挂起（锁定成功即吃掉）
}
```

`pointerlockchange`（:1583-1615）：锁定成功 → `hideVisibleCursor()`（:1591）；解锁（含 ESC）→ `hideVisibleCursor()`（:1612）。

由此得到**蓝球可见性真值表**：

| 场景 | pointerLocked | pointerType | 蓝球 | 代码依据 |
|---|---|---|---|---|
| 鼠标悬停移动（未锁定） | false | mouse | **显示**（唯一场景） | :1794-1797 |
| 鼠标点了一下左键（锁定成功） | false→true | mouse | **隐藏**（锁定即关） | :1729→:1591 |
| ESC / 逃生门解锁后 | true→false | mouse | **隐藏**（解锁也关，等下次 move） | :1612 |
| 锁定态转视角 | true | mouse | 隐藏（L1792 直接 return） | :1792 |
| 触屏（手机/平板） | false | touch | **永远不显示** | :1794 条件排除 |
| 鼠标不在 #scr 上、或静止不动 | false | mouse | 保持上次状态（不动就不刷新，但也不隐藏） | — |

### 1.4 三个用户现象的精确归因

**现象 ①「只在本地鼠标显示时才显示」**

蓝球的唯一显示入口是本机鼠标的 `pointermove`（:1794）。本机鼠标离开 `#scr`、或事件被上层的指针锁定吞掉、或触屏操作，蓝球都不更新。它**与远端光标没有任何数据通道**——没有服务端位置回传、没有帧内光标解析，纯粹是 `norm(e)` 的本地逆变换。所以用户观察完全正确：「它跟着我的本机鼠标走」。

**现象 ②「点一下就隐藏了」**

点一下左键 → L1725-1731 发起 `requestPointerLock()` → 锁定成功 → :1591 `hideVisibleCursor()`。在桌面 Chrome/Edge（secure context，`http://localhost` 或 https）上这**必然发生**：**任何一次左键点击都会关掉蓝球**。若锁定失败（如非 localhost 的明文 `http://192.168.x.x:port`，`window.isSecureContext` 为 false 时 L1725 直接不进锁定分支——这是 2026-11-24 修复吞点击死循环时加的守卫），蓝球倒是不隐藏，但那次点击也只发一个 `click`（:1909-1914 兜底补发），蓝球停留在落点圆心不动。两种结局用户都感知为「点一下就没了/废了」。

**现象 ③「对操控毫无帮助」**

操控链路是：网页 `sendInput('move', nx, ny)` → `WebServer.HandleInput`（WebServer.cs:682-750）按会话分辨率乘回像素（:704-707）→ `RelayClient.MoveAsync` → 代理 `SetCursorPos`（RelayAgent.cs:808）。远端真实落点由 `SetCursorPos` 决定，蓝球显示的是**本机侧 `norm(e)` 的预测值**。两者理论一致（norm 几何已修），但：
- 本机系统光标**本来就在那个位置**（非锁定态浏览器显示系统光标），蓝球与系统光标重叠 → 蓝球提供的增量信息 = 0；
- 444ms 画面延迟下，远端画面反馈要 444ms 后才到，蓝球零延迟地跟着本机鼠标 → 用户把「远端没反应」归咎于蓝球没用（它确实没用，但没用的原因是**没有远端回显通道**，不是画得不好）；
- 远端画面里根本没有光标像素：WGC 主通路 `TrySetCursorCaptureEnabled(_session, false)`（WgcCapture.cs:174）、探针路径同样 false（:578）——**用户在画面里永远看不到远端光标**，这才是「不知道点没点中」的真正病灶。蓝球既不补这个洞（它不在远端），又不消失（占视觉），所以被判废。

### 1.5 一句话定性

> 蓝球 = 「本机鼠标的零延迟影子」，画在「444ms 延迟的远端画面」上，而远端画面里没有光标。它回答不了用户唯一关心的问题：「**远端的光标现在在哪**」。

---

## 2. 成熟产品怎么处理光标（对照表）

> 来源：本仓库已核实的源码级调研（`OSS-REMOTE-RESEARCH-20261122.md` §2/§3.2、`POINTER-LOCK-DESIGN-20261123.md` §3/§8/§10、`MATURE-WEB-STACK-20261123.md` §7.2/§9.1、`MATURE-ARCH-FACTS-20261123.md`）+ 本任务新增的 Apache Guacamole 官方手册核对（https://guacamole.apache.org/doc/gug/using-guacamole.html）。

| 产品 | 远端光标可见性 | 绝对模式（点哪指哪） | 相对/锁定模式（转视角） | 关键机制（证据） |
|---|---|---|---|---|
| **Sunshine**（服务端） | **光标画进视频帧**（WGC `IsCursorCaptureEnabled` 可配，display_wgc.cpp:244-253 游标合成） | 服务端 `move_absolute`（virtualhid_input.cpp:915-934，走虚拟 HID 不产生跳变） | 相对位移包 + batch 累加（input.cpp:1883-1917/2594-2614） | 客户端**不需要**自绘光标：帧里就有 |
| **Moonlight-qt**（桌面客户端） | **客户端系统光标直接盖在视频上**（绝对模式）；相对模式锁定后光标隐藏 | `LiSendMousePositionEvent` + 视频区裁剪/边界钳制（mouse.cpp:122-175） | `SDL_SetRelativeMouseMode` 持续拿 xrel/yrel 直发（mouse.cpp:176-178）；相对模式 `updatePointerRegionLock()` early-return 不碰光标（:285-336） | 绝对模式本机光标=远端光标（1:1 无延迟回显）；**没有第三种「影子指示器」** |
| **moonlight-chrome**（浏览器客户端） | Pepper MouseLock：锁定后光标隐藏 | 首次点击吃掉（进锁定），其后锁定态点击即远端点击 | movement 累加 + 批量 `LiSendMouseMoveEvent`；Ctrl+Alt+Shift 组合键逃生门（input.cpp L274-315） | 浏览器端同样**不做本机鼠标影子** |
| **Guacamole**（Web RDP/VNC） | 远端光标由协议回传（RDP/VNC 的 cursor shape/pos 通道），客户端按形状合成绘制 | 触屏绝对模式：tap=左键落点即光标位置 | **相对（触控板）模式：屏幕自动 pan 保持远端光标可见**（官方手册 "the screen will automatically pan to keep the mouse pointer in view"） | **光标可见性是相对模式的一等公民**，靠 pan 而不是靠影子 |
| **selkies-gstreamer**（浏览器云游戏开源标杆） | 帧内光标（GStreamer 采集含光标）或 cursor 事件通道 | Pointer Lock 相对位移为主（--raw-pointer-motion） | 同左 | "Moonlight/Stadia/GFN in noVNC form factor"（README） |
| **云原神**（商业 Web 云游戏） | 画面内光标（服务端渲染含光标） | Pointer Lock 相对位移（CDP 实测 UNLOCKED 态 + DataChannel 输入，`WEB-RUNTIME-PROBE-20261123.md` §6） | 同左；ESC=解锁、游戏内 ESC 走另一条通路 | 商业产品同样**零「影子圆球」** |
| **Parsec** | 驱动级输入 + 光标由服务端侧合成进帧 | 绝对（驱动级） | 相对（BUD 通道） | 闭源，行为与上同 |
| **SoulLink 现状（蓝球）** | **远端画面无光标**（WgcCapture.cs:174/578 关死）+ 本机鼠标影子圆球 | 蓝球=本机 norm 逆变换 | 锁定即隐藏（:1591） | **与所有成熟产品的交集为空** |

### 2.1 提炼：成熟做法的三个不变式

1. **远端光标必须有且只有一个真相源**：要么在帧里（Sunshine/selkies/云原神），要么由输入协议回传位置（Guacamole/VNC cursor 通道、Moonlight 绝对模式 1:1）。**绝不在客户端凭本机事件自造一个假光标**。
2. **绝对模式下本机系统光标就是回显**（Moonlight-qt 桌面形态）：1:1 映射 + 零延迟，任何自绘影子都是冗余。要做的是**保证落点映射准确**（norm 已修）。
3. **相对/锁定模式下光标隐藏，但要给「远端光标不可见」留补偿机制**：Guacamole 用自动 pan；云游戏用帧内光标。**补偿的对象是远端光标，不是本机鼠标**。

---

## 3. 差距（SoulLink 与成熟做法）

| # | 差距 | 证据 | 影响 |
|---|---|---|---|
| G1 | **远端画面无光标**：WGC `IsCursorCaptureEnabled=false` 两处写死（WgcCapture.cs:174、:578），无配置开关 | 【代码事实】 | 用户在 444ms 延迟的画面里完全看不到远端光标 → 「不知道点没点中」→ 落点误差被放大感知 |
| G2 | **无远端光标位置回传通道**：`HandleInput`（WebServer.cs:747）只回 `{ok:true, sid}`，`GetCursorPos` 结果丢弃；帧侧信道无 cursor 消息 | 【代码事实】 | 客户端无从得知远端光标真实位置，只能自造影子（蓝球）|
| G3 | **蓝球信息量为零**：唯一显示场景=本机鼠标悬停移动（:1794），与系统光标重叠；点击即隐藏（:1591） | 【代码事实】 | 用户判废，正确 |
| G4 | **绝对模式本机光标与画面不同步的感知**：本机光标零延迟移动，远端 444ms 后才动 → 用户感知「卡」。蓝球零延迟跟随加剧了这种不同步感（假象：本地很流畅、远端不动） | 【推断】 | 与「鼠标输入输出很卡」的用户主诉直接相关 |
| G5 | 触屏端无任何落点回显（蓝球排除触屏 :1794；只有 `#rip` 涟漪一次性的点击反馈 :1475-1480） | 【代码事实】 | 手机端「点哪了」全靠涟漪 + 444ms 后的画面 |

---

## 4. 可执行改造方案

### 4.0 决策结论

**删掉蓝球（#plCursor 现形态），换成「帧内光标 + 远端位置回显」两级方案。**

理由：
- 蓝球的存在前提（「本机鼠标影子能帮用户定位」）不成立——本机系统光标已经做了同样的事（现象①③），且它被点击进锁定的设计亲手杀死（现象②）；
- 成熟产品无一采用此形态（§2）；
- 用户真正缺的是**远端光标可见性**（G1/G2），蓝球回答不了这个问题；
- 删除成本几乎为零（CSS+DOM+4 个函数，全部集中于 ~40 行内），且不破坏 Pointer Lock / 双鼠标模式 / 触屏手势状态机——蓝球是纯旁路层（`pointer-events:none`、无输入副作用）。

### 4.1 P0（最小改动，~1 人时）：打开帧内光标 + 隐藏本机光标

**目标**：让远端光标直接出现在画面里（Sunshine 默认形态），用户立刻能「看见点没点中」。

改动清单（全部在服务端/样式层，**不碰输入链路**）：

| # | 文件:行 | 现状 | 改为 | 说明 |
|---|---|---|---|---|
| C1 | `Relay/WgcCapture.cs:174` | `TrySetCursorCaptureEnabled(_session, false)` | `TrySetCursorCaptureEnabled(_session, true)` | WGC 主通路把光标合成进帧。**注意**：改的是生成 docs/web-index.html 之上层，主程序热加载 web-index.html 不受影响，但 WgcCapture 是 C#，需要重编译 + 会话重连（不重启 session1 主程序的前提下由 Lead 决策发布节奏） |
| C2 | `Services/WebPages.cs:314`（`#scr` CSS `cursor:default`） | `cursor:default` | `cursor:none`（仅非锁定态时，见 C3） | 绝对模式下隐藏本机系统光标：帧内已有远端光标（C1），本机光标变成干扰（两个光标：本机零延迟 + 帧内 444ms，正好把「延迟」可视化在光标上——**这是诚实的**，云游戏都这样）。注意保留一个 class 切换：锁定态浏览器本来就隐藏光标，`cursor:none` 无副作用 |
| C3 | 同上（CSS 追加） | — | `#scr.locked{cursor:none}` + body.viewonly 时同样规则 | 防御式：确保任何模式本机光标都不与帧内光标双影 |

**P0 效果**：远端光标随画面出现（含 444ms 延迟），点击后 444ms 内看到远端光标移动/点击反馈；本机光标隐藏，双影消失。**蓝球此时已无存在意义，顺手删除（C4-C7 可与 P0 同 PR）。**

### 4.2 P0 附带：删除蓝球的精确改动清单

| # | 文件:行 | 动作 | 内容 |
|---|---|---|---|
| C4 | `Services/WebPages.cs:337-340` | 删除 | `#plCursor{...}` 整条 CSS（连同 :335-336 注释） |
| C5 | `Services/WebPages.cs:628` | 删除 | `<div id="plCursor"></div>` |
| C6 | `Services/WebPages.cs:1446-1473` | 删除 | 整个「可见光标指示」块：`plCursorEl`（:1449）、`plCursorGeometry()`（:1450-1459）、`updateVisibleCursor()`（:1460-1470）、`hideVisibleCursor()`（:1471-1473） |
| C7 | 调用点清理 | 删除/简化 | ① `pointermove` 里 :1793-1797 的 `if (!pointerLocked && e.pointerType !== 'touch') { norm(e); updateVisibleCursor(...) }` 整块（norm 调用只为蓝球服务，删除后无副作用）；② `pointerlockchange` :1591 与 :1612 的两处 `hideVisibleCursor()` 调用。**改完后** `docs/web-index.html` 需同步重新生成（`scripts\sync-webpages.ps1`，WebPages.cs:8） |

**边界条件自查（C1-C7）**：
- `norm()` 本体（:1426-1444）**不动**——落点计算仍被 pointerdown/up/触屏状态机使用；
- `#rip` 涟漪（:1475-1480）**不动**——它是点击确认反馈，与光标无关，触屏端还需要它；
- Pointer Lock 状态机（:1512-1632）、`plLockPending` 兜底（:1909-1914）、ESC 语义（:1967-1969）**全部不动**；
- 触屏路径零影响（蓝球本来就排除触屏）；
- **唯一行为回退风险**：`IsCursorCaptureEnabled=true` 后，若远端应用全屏隐藏光标（游戏视频），帧内光标自动消失（WGC 语义）——这是正确行为，不是回退。

### 4.3 P1（推荐补强，~0.5 人日）：远端光标位置回传（低延迟回显，不依赖帧）

P0 的帧内光标带着 444ms 延迟（画面链路延迟是团队另一条主线在攻）。在画面延迟修好之前，给光标开一条**低延迟旁路**：

**服务端**（`Relay/RelayAgent.cs` 输入命令处理处，`move`/`click` 分支 :806-819 附近）：
- 在执行 `SetCursorPos`/`mouse_event` 后读 `GetCursorPos()`，把 `{x, y, visible}`（归一化到会话分辨率）塞进**输入响应**或**帧流侧信道**。最小实现：`HandleInput`（WebServer.cs:747）的 `{ok:true}` 响应加 `cursor:{nx,ny}` 字段——`move` 本就走 8ms 节流 noWait（WebPages.cs:1763-1786），可以改成「响应带位置、前端下一次 pump 时消费」；更干净的做法是 WebSocket 帧通道里插 `{"t":"cursor","nx":..,"ny":..}` 级消息（改 `AgentWebServer.cs` 帧输出协议，需与视频链路成员对齐消息类型枚举——**建议放 P1 任务，由 Lead 排期**）。

**前端**（替代蓝球的新 `#rcCursor`，~15 行）：
- 收到 cursor 消息 → 在 canvas 上画一个**小十字/箭头 sprite**（不是圆球；对齐远端光标 hotspot 方向），位置 = `plCursorGeometry()` 同款逆变换（**注意：plCursorGeometry 将被 C6 删除，需保留为公共函数 `screenGeometry()`**——或者直接复用 `norm` 的几何提取成 `function frameRect()` 供 norm 与 rcCursor 共用，避免复制粘贴两份几何）；
- 显示条件：`pointerLocked === false`（绝对模式/触屏绝对模式）**或** 相对模式（Guacamole 式：转视角时远端光标也在动，回显它比隐藏它信息量大）；
- 隐藏条件：收到 `visible:false`（远端 `GetCursorInfo().flags` 无 `CURSOR_SHOWING` 时上报隐藏——光标在输入框变成 I-beam、游戏隐藏光标时同步隐藏）；
- 节流：跟随 moverel 8ms 合并的既有节奏即可，无需新定时器（遵守 TickHub/无新定时器的性能约束，AGENTS.md §性能约束 1）。

**P1 效果**：光标回显延迟 ≈ 输入 RTT（回环 3ms 级）+ 处理，而不是 444ms——「鼠标输入输出很卡、落点误差」的感知会被这条旁路大幅缓解（用户看到的远端光标几乎实时跟随，即使画面本体 444ms）。

### 4.4 不建议做的（明确否决项）

| 否决项 | 理由 |
|---|---|
| 保留蓝球 + 改成「跟随远端回传位置」 | 名字/形态已与用户结怨（「废功能」）；且 P1 的 rcCursor 是新语义新元素，保留旧 id 徒增混淆 |
| 蓝球改在触屏显示（补触屏回显） | 触屏手指即落点（tap 处就是落点，有 #rip 涟漪确认），触屏更需要 P1 的远端位置回显而不是本地影子 |
| 本机光标保留 + 帧内光标共存（不隐藏本机光标） | 双影：本机零延迟 vs 帧内 444ms，两影分离 444ms 视觉上极糟（比蓝球更糟） |
| 在 `#scr` 上用 CSS `cursor:url(...)` 画远端光标 | CSS 光标跟随本机事件（同蓝球问题），且动态换 shape 需要换 URL 重渲染，闪烁风险；自绘 DOM sprite（P1）更稳 |

---

## 5. 验收清单（供 Lead 派实现任务时直接用）

| # | 项 | 操作 | 通过判据 |
|---|---|---|---|
| V1 | 蓝球消失 | 任意模式打开画面 | DOM 无 #plCursor，grep WebPages.cs 无 plCursor 残留 |
| V2 | 帧内光标 | P0 后点击画面 | 444ms 内看到远端光标在点击处出现（WGC 光标合成生效） |
| V3 | 无双影 | 绝对模式移动鼠标 | 只看到帧内远端光标，本机系统光标隐藏（cursor:none 生效） |
| V4 | 锁定态 | 点画面进锁定转视角 | 帧内光标随远端移动，浏览器不显示本机光标（锁定语义不变） |
| V5 | 触屏 | 手机 tap | 涟漪照常，落点正确（norm 未受影响） |
| V6 | P1 光标旁路 | move 时看 rcCursor | 回显延迟 <50ms（不随画面 444ms） |
| V7 | 回退安全 | 关闭 P1 配置 | 退回 P0 形态（帧内光标），无报错 |

---

## 6. 证据索引

**SoulLink 代码（【代码事实】，均为本任务逐行核对）**
- `Services/WebPages.cs`（源 = docs/web-index.html + sync-webpages.ps1，:6-12）：#plCursor CSS :335-340；DOM :628；plCursorEl/plCursorGeometry/updateVisibleCursor/hideVisibleCursor :1449-1473；norm :1426-1444；Pointer Lock 状态机 :1512-1632（锁定成功 hide :1591、解锁 hide :1612、error 兜底 :1617-1624、冷却 :1611）；pointerdown 锁定入口 :1725-1731；pointermove 蓝球唯一显示入口 :1789-1797；move 8ms latest-wins :1763-1786；首击兜底 :1909-1914；#rip 涟漪 :1475-1480；#scr cursor:default :314。
- `Relay/WgcCapture.cs`：主通路 `TrySetCursorCaptureEnabled(_session, false)` :174；探针路径 :578；`TrySetCursorCaptureEnabled` 实现 :687-689。
- `Services/WebServer.cs`：`HandleInput` :682-750（归一化→像素 :704-707；响应只有 {ok,sid} :747）。
- `Relay/RelayAgent.cs`：`SetCursorPos` :808（move 注入）；SendInput 结构 :41/172。
- `Relay/DxgiDdaCapture.cs`：grep "Cursor" 0 命中（DDA 路径无光标概念，帧内光标只能靠 WGC 的 IsCursorCaptureEnabled）。

**成熟产品（【外部】，file:line 或 URL）**
- Sunshine：`display_wgc.cpp:244-253`（IsCursorCaptureEnabled 游标合成）、`virtualhid_input.cpp:915-934`（move_absolute）、`input.cpp:1883-1917/2594-2614`（相对位移 batch）——本仓库 `OSS-REMOTE-RESEARCH-20261122.md` §2.2/§3.2 转引。
- Moonlight-qt：`mouse.cpp:104-178`（同帧合并累加/绝对/相对三模式）、`:285-336`（相对模式不碰光标）——同上转引。
- moonlight-chrome：`input.cpp` L274-315（Ctrl+Alt+Shift 逃生门、首击吃掉、批量 LiSendMouseMoveEvent）——`POINTER-LOCK-DESIGN-20261123.md` §8 转引。
- selkies-gstreamer：Pointer Lock + raw-pointer-motion，README 自述 "Moonlight/Stadia/GFN in noVNC form factor"——`MATURE-WEB-STACK-20261123.md` §7.2 转引。
- 云原神：Pointer Lock + DataChannel 输入（CDP 实测）——`WEB-RUNTIME-PROBE-20261123.md` §6；cg-sdk 逆向 §9.1。
- Apache Guacamole 官方手册（本任务一手核对，2026-11-24 抓取）：https://guacamole.apache.org/doc/gug/using-guacamole.html —— Mouse emulation 节："Guacamole uses 'absolute' mouse emulation … the mouse pointer is positioned at the location of each tap"；Relative mode (touchpad) 节："the screen will automatically pan to keep the mouse pointer in view"。
- Parsec：驱动级输入 + BUD，闭源——`STREAM-SURVEY-20261122.md` §1 转引（行为级）。

**同团队关联文档**（跨成员结论交换）
- `CURSOR-AND-POINTER-LOCK-20261124.md`（viewer-auditor）：蓝球设计源头（§3.2 「可见光标指示方案，对齐云原神」）——注意其「对齐云原神」的说法不准确：云原神没有影子圆球（见本文 §2）；该文档的 §1/§2（Pointer Lock 吞点击、norm 落点偏移）已被 2026-11-24 的实现修复（WebPages.cs:1433-1435、:1720-1731 注释为证）。
- `HANDOFF-VIDEO-LATENCY-GPU-20261124.md`：444ms 延迟现状（G4 的感知放大器）。

---

## 7. 边界遵守自查

- 未改任何产品代码/脚本/配置；未碰 dist/、Relay/、Android/（只读）；未碰 dist\stable\SoulLink-Stable.exe；未杀任何进程；未做任何系统状态变更；
- 唯一写入：本文档 `docs/research/AUDIT-20261124-03-CURSOR-UX.md`；
- README.md 登记与 sync-webpages 重生成留给 Lead（涉及发布边界）。
