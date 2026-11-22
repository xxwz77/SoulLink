# 转视角链路优化 + 手机端贯通（LOOK-OPTIMIZE）

> 日期：2026-11-22
> 作者：lookopt（agent-team，member `m_mwrs1uclet5tls`）
> 范围：桌面端转视角链路的三项优化评估与落地；网页/手机端转视角从 0 到 1。
> 底线遵守：未改 `<Version>`、未 commit/push、未动 `dist/` 或 `dist/stable/` 的 exe（只同步了 `dist/web-index.html`，见 HANDOFF 第 5 节允许的操作）。

---

## 0. 结论摘要

1. **桌面端最大单一延迟源是 `RelayAgent.cs` moverel 里的 `Thread.Sleep(6)`**：大位移被拆成最多 20 段，每段睡 6ms，累计可达 ~120ms，且拆段理由（"系统会截断"）**经微软文档查证不成立**。已去掉，改单段直发。
2. **moverel 已改单向发送（fire-and-forget）**：协议加 `NoReply` 标志，代理不回写、宿主不等待。位移不需要确认，省掉每条 45~64ms 的 RTT 等待。
3. **LookInputQueue 节流从 20ms（50Hz）提到 8ms（≈125Hz）**：在 NoReply 单向优化完成后安全，否则提频反而更糟（见 §3 先后依赖）。
4. **手机端转视角选「直接滑动屏幕」**（用户明确否决右摇杆）：触屏滑动 = moverel 相对位移；点一下 = 左键单击；长按不动后滑动 = 左键按住拖动。已落地 web-index.html。
5. **Pointer Lock 在移动端不可用**（iOS Safari / Chrome for Android 均 ❌），桌面端仍可用，但本方案手机走触摸 delta，不依赖 pointerlock。
6. `dotnet build SoulLink.csproj` **0 error**（142 个既有 warning，与本次改动无关）。

---

## 1. 现有链路延迟分解（逐跳）

链路（桌面端，已实测可用）逐跳成本：

| # | 环节 | 文件:行 | 单次成本 | 实测/估算 |
|---|---|---|---|---|
| 1 | Raw Input 采集硬件相对位移 | `Services/RawMouseInput.cs:133-157` | <0.1ms | 估算（WM_INPUT 钩子，只读 lLastX/lLastY） |
| 2 | 入队合并 | `Controls/RdpConsole.cs:526-531` → `Services/LookInputQueue.cs:35-44` | <0.1ms | 估算（lock + 累加） |
| 3 | 后台线程节流唤醒 | `LookInputQueue.cs:78` `WaitOne(8)` | 0~8ms | 实测口径（原 20ms） |
| 4 | 灵敏度换算 + 调 LookRaw | `LookInputQueue.cs:107-110` → `SessionInput.cs:86-92` | <0.1ms | 估算 |
| 5 | **命名管道写 + 等回复（串行一问一答）** | `RelayClient.cs:494-533` | **45~64ms/条** | **实测**（RelayClient.cs:553-555 自述） |
| 6 | 代理读一行 → 分发 → 写一行 | `Relay/RelayAgent.cs:585-636` | <1ms（不含业务） | 估算 |
| 7 | **moverel 拆段 + Thread.Sleep(6)** | `Relay/RelayAgent.cs:694-724`（旧） | **0~120ms**（steps≤20 时每段 6ms） | 代码推导：20 段 = 19 次 sleep ≈ 114ms |
| 8 | `mouse_event(MOVE)` 注入 | `Relay/RelayAgent.cs:703` | <1ms | 估算 |

**最大瓶颈排序**：① Thread.Sleep(6) 拆段（大位移可达 ~114ms）＞ ② 串行一问一答的 RTT（45~64ms/条，且把 8ms 合并窗口的实际频率压到 ~15Hz）＞ ③ 20ms 节流窗口。

> 注：⑤ 的 45~64ms 是"一条指令一个完整往返"的口径；在高频位移流里它不会每条都独立叠加，而是被 20ms 窗口串行化——窗口期内的下一条要等上一条回复回来才能发，所以实际有效注入频率 ≈ 1000/(45~64) ≈ 15~22Hz，而非 50Hz。这正是"提频先做单向"的依赖关系所在。

---

## 2. 桌面端三项优化评估结论

### A1. 去掉/调整 `Thread.Sleep(6)` —— **已做，去掉拆段**

**查证"大位移一次性给会被系统截断"**：
- `MOUSEINPUT` 结构（`https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-mouseinput`）明确 `dx/dy` 类型是 **`LONG`（32 位带符号）**，文档**没有任何"超过 N 像素被截断"的说明**。
- 文档明确的副作用只有一个方向相反的行为：相对位移会受系统**指针速度**影响，最多被放大 **×4**（"It is thus possible for the system to multiply specified relative mouse movement ... by up to four times"），不是截断。
- 结论：**原注释说法不成立**。拆段 + `if(steps>4) Sleep(6)` 是给 20 段注入引入约 114ms 人工延迟的最大元凶。

**处理**：改为单段直发 `mouse_event(MOUSEEVENTF_MOVE, totalDx, totalDy, 0, IntPtr.Zero)`。指针加速与真实鼠标一致，游戏读 Raw Input 得到完整增量。

- 收益估计：大位移场景消除最高 ~114ms 人工延迟；小幅位移（steps≤4，原无 sleep）无变化。
- 实现成本：极低（删一段循环）。
- 风险：低。若担心极快甩鼠标时单帧位移过大导致的"跳变"，可在**前端**（LookInputQueue 的 8ms 窗口）自然限幅——8ms 内手指/鼠标不可能产生上千像素的物理位移；游戏侧 Raw Input 自己处理。

### A2. 减少往返：moverel 改单向 —— **已做，NoReply 标志**

**三个候选方案评估**：

| 方案 | 安全性 | 结论 |
|---|---|---|
| a) moverel 单向（fire-and-forget） | 需协议级"不回复"标志，否则代理写的回复会错位污染下一条 | **选中** |
| b) 客户端每 16ms 发累积位移 | 只是节流，不解决 RTT 串行化，仍需等回复 | 作为节流的一部分，不单独选 |
| c) 协议加"不回复"标志 | 与 a) 本质同一件事 | 即 a) 的实现方式 |

**为什么选 a)+c)**：位移是连续流，等 45~64ms 的回复既无意义又压低了实际注入频率。加 `NoReply` 标志是最干净的做法——它**不改动其它命令的一问一答语义**（move/click/down/up/wheel/key 等继续走 `SendAsync` 等回复）。

**死锁/错位的安全性（用户特别提示的点）**：
- 宿主侧 `RelayClient` 是单写者队列（`SingleReader=true`），写后是否读回复由 `RelayRequest.NoReply` 决定。NoReply 请求写完就 `continue`，不读。
- 代理侧 `ServePipe` 读到 `NoReply=true` 后 **`continue` 跳过 WriteLine**——双方都不收发额外字节，下一条指令的回复仍然是它自己的回复，通道不错位。
- 若代理不回写而宿主却在等（或反过来），才会死锁/错位；两边已对称实现，无此风险。
- 队列满（256）时 NoReply 请求也走 `WriteAsync` 背压超时（150ms）→ 丢弃并告警，不会无限堆积（`RelayClient.cs:481-488` 逻辑沿用）。

**改动落点**：
- `Services/RelayClient.cs`：新增 `SendAsyncNoReply`（反射把任意匿名对象转 `Dictionary` 并注入 `NoReply=true`）、`RelayRequest` 加 `NoReply` 字段、`ProcessRequestsAsync` 跳读、`MoveRelNoReplyAsync`。
- `Services/SessionInput.cs`：新增 `LookRawNoReplyAsync`。
- `Relay/RelayAgent.cs`：`Request` 加 `NoReply` 字段；`ServePipe` 读到 NoReply 后 `continue`。
- `Services/LookInputQueue.cs`：后台线程改调 `LookRawNoReplyAsync`。

### A3. LookInputQueue 节流 20ms → 8ms —— **已做**

**先后依赖关系**：20ms 窗口叠加 45~64ms 的串行 RTT，实际有效频率已被 RTT 压到 ~15~22Hz，此时**单纯提频毫无收益，只会让队列背压更糟**。因此必须**先做 A2 单向优化**，RTT 从每条 45~64ms 降到只写一次（<1ms），8ms 窗口才能真正达到 ~125Hz 上限。

**20ms（50Hz）够不够**：不够。125Hz 与主流游戏鼠标上报速率相当，8ms 合并窗口能在不丢位移的前提下把手感上限从 50Hz 提到 ~125Hz。

- 收益估计：转视角跟手度提升，尤其快速甩动。
- 成本：极低（一个常量 + 调用点）。
- 风险：低。前提是 A2 已做；已满足。

---

## 3. 手机/网页端贯通（从 0 到 1）

### 3.1 Pointer Lock 移动端支持查证 —— **不可用，弃用**

| 平台 | Pointer Lock 支持 |
|---|---|
| Chrome 桌面 / Edge / Firefox | ✅ 支持 |
| Safari 桌面 | ✅ 支持 |
| **iOS Safari** | ❌ **不支持**（caniuse 明确标注全版本 ❌） |
| **Chrome for Android** | ❌ **不支持** |
| Samsung Internet / Android Browser | ❌ 不支持 |
| Firefox for Android | ✅ 支持（少数） |

来源：`https://caniuse.com/pointerlock`、`https://developer.mozilla.org/en-US/docs/Web/API/Pointer_Lock_API`（MDN 标注 "Limited availability / not Baseline"）。

**结论**：手机转视角**不能**依赖 Pointer Lock（iOS 全家不支持）。手机走触摸 delta 方案（手指每帧位移差 = movementX/Y），不碰 pointerlock。桌面端若未来要更精细的手感可以再加 pointerlock，但不在本次范围。

### 3.2 手机端手势设计（最终版，用户拍板）

用户明确要求：
- **「是直接滑动屏幕」**（否决右摇杆）。
- **「任意非按钮位置」**都可滑动转视角（否决固定半屏分区）。
- 标准手游方案，不自己发明轮子。

最终手势映射：

| 手指动作 | 鼠标行为 | 判定依据 |
|---|---|---|
| 点一下屏幕（按下即抬起，几乎没动） | 左键单击 | 位移 < 滑动阈值 |
| 按下后**立即**滑动（长按窗口内越过位移阈值） | 转视角（moverel 相对位移） | 长按窗口内越过 10px |
| 按住不动超过长按阈值后再滑动 | 左键按住拖动（down + 绝对 move + up） | 250ms 内未越过阈值 → 进入 drag |
| 双指 | 滚轮/缩放（保留现有 wheel 逻辑，未新增捏合缩放） | — |

**为什么不需要右摇杆**：我们串流的是 PC 键鼠游戏（原神/永劫无间手游式操作），不是手柄游戏。右摇杆是 Moonlight 面向手柄的映射；用户要的是"手指直接划屏转视角"，与手游原生一致。

**"游戏内打开地图时滑动不转视角"**：游戏 UI 层与 3D 场景分开，打开地图时游戏自己处理滑动，我们不额外判断（用户原话"托地图的时候是没有视角的"）。

### 3.3 四个设计问题的答案

#### Q1. 长按阈值多少？
- 成熟手游常见 **200~300ms**。太长：想拖动时等得久；太短：正常转视角起始会误判成拖动。
- **选 250ms**（`LOOK_TAP_MS`），可调范围 200~300ms。
- 依据：iOS 长按手势（`UILongPressGestureRecognizer`）默认 0.5s 偏长；游戏串流（Steam Link / Moonlight）常用 200~300ms 区分 tap/long-press；手游长按普遍落在该区间。

#### Q2. 位移阈值与长按阈值怎么配合？
- 原 web-index.html 用 8px 判定 drag（鼠标口径）。手指抖动比鼠标大，**8px 太灵敏**。
- **选 10px**（`LOOK_SLIDE_PX`），作为"抖动/误触容差"。
- 配合逻辑：按下后，`pointermove` 里先算到起点的欧氏距离，<10px 视为抖动忽略；一旦 ≥10px 且仍在 250ms 窗口内 → 转视角；若 250ms 到了还没越过 10px → 进入 drag（按住左键），此后移动发绝对 move。

#### Q3. 长按进入拖动后的反馈？
- 网页可用 **Vibration API**（`navigator.vibrate(ms)`）：Android Chrome ✅、iOS Safari ❌（iOS 无震动 API）。
- **建议**：进入 drag 时尝试 `navigator.vibrate(15)`，失败静默（iOS 无反馈也可接受）。**本次未实现**（见 §5 待办）——拖动反馈不是转视角的必需项，先保证功能正确，反馈作为体验项留待用户确认是否要加视觉/震动提示。
- 判断依据：iOS 不支持震动 → 视觉反馈（如短暂高亮）是唯一跨平台方案，但会与"不造假光标"的原则边缘摩擦；故先不做，列为待确认。

#### Q4. 转视角 vs 拖动的鼠标事件序列差异 + 拖动用绝对还是相对？
- **转视角**：`moverel(dx, dy)`，不碰按键。依据：游戏转视角只认相对位移，绝对坐标不转（HANDOFF 4.2.6 实测）。
- **拖动**：`down('left')` → 连续 `move(绝对坐标)` → `up('left')`。依据：拖物品/拖地图是"光标跟随手指"，用绝对坐标；只有 3D 场景里的镜头拖动才需要相对位移，而那种场景游戏多半已用鼠标右键拖拽（走转视角同款相对语义）。所以**拖动统一用绝对坐标**，转视角统一用相对位移，两者泾渭分明。

### 3.4 单帧位移量限制（与去 Sleep 联动）

手指快速滑动一帧可能产生 100~200px+ 位移。**已经与 A1 去 Sleep 一起解决**：
- 前端 `lookDelta` 以 8ms 合并窗口累积，一次发 `moverel(dx, dy)`。
- 代理端 moverel 已改单段直发（无拆段、无 Sleep），`MOUSEINPUT` dx 是 32 位 LONG，200px 远不构成问题。
- 指针加速会让位移放大最多 ×4，仍属"真实鼠标"行为，游戏读 Raw Input 一致。

### 3.5 限流确认（手机高频 moverel 会不会打爆队列）

- 前端 `lookAcc` 以 8ms 窗口合并，**最多 ~125 次/秒**的 `moverel` fetch。
- 代理侧 `AgentWebServer` 每条请求独立 `Task.Run` 处理，`RelayAgent.Execute` 直接执行、同步返回，无队列堆积。
- 宿主编 `RelayClient` 的 256 有界队列只服务**桌面端** `LookInputQueue`（也改成了 8ms + 单向），同样 ≤125Hz。
- 结论：**前端 8ms 合并足够**，不会打爆。原 `RelayClient.cs:486` 的"满则丢弃+告警"仍是兜底。

### 3.6 与现有 web-index.html 手势逻辑的衔接（pointerType 区分）

- 用 **`e.pointerType === 'touch'`** 区分触屏与鼠标/触控笔。
- **触屏**：走新状态机（pending → look / drag → click），**不再发悬停绝对 move**（手机没有"悬停"概念）。
- **鼠标/触控笔**：完全保留原逻辑（悬停 move 跟随 + 8px drag 阈值 + 右键/中键 click）。
- 虚拟按钮（左摇杆等，未来）落在独立图层，`pointer-events` 正确设置、阻止事件穿透；按钮间隙仍算"非按钮区域"可转视角。

---

## 4. 改动清单（file:line）

### 后端 C#

| 文件 | 行 | 改动 |
|---|---|---|
| `Relay/RelayAgent.cs` | ~448-467 | `Request` 新增 `bool NoReply` |
| `Relay/RelayAgent.cs` | ~618-636 | `ServePipe`：读到 NoReply 后 `continue` 跳过 WriteLine |
| `Relay/RelayAgent.cs` | ~694-724 | `moverel`：删拆段 + Sleep，改单段直发 |
| `Services/RelayClient.cs` | ~37-46 | `RelayRequest` 加 `NoReply` 字段 |
| `Services/RelayClient.cs` | ~473-540 | 新增 `SendAsyncNoReply`（反射注入 NoReply + 不读回复） |
| `Services/RelayClient.cs` | ~494-533 | `ProcessRequestsAsync`：NoReply 写后 `continue` |
| `Services/RelayClient.cs` | ~548-556 | 新增 `MoveRelNoReplyAsync` |
| `Services/SessionInput.cs` | ~94-108 | 新增 `LookRawNoReplyAsync` |
| `Services/LookInputQueue.cs` | ~11-30 | 新增 `WakeIntervalMs=8` 常量 + 注释 |
| `Services/LookInputQueue.cs` | ~78 | `WaitOne(20)` → `WaitOne(WakeIntervalMs)` |
| `Services/LookInputQueue.cs` | ~107-112 | 后台线程改调 `LookRawNoReplyAsync` |

### 网页 `docs/web-index.html`

| 行区间 | 改动 |
|---|---|
| ~840-880 | 新增触屏手势状态机常量 + `lookAcc` 合并/节流 |
| ~882-905 | `pointerdown` 分支：touch 走 look 状态机，鼠标走原 drag |
| ~942-981 | `pointermove` 分支：touch 转视角/拖动，鼠标原逻辑 |
| ~982-1018 | `pointerup`/`pointercancel` 分支：touch 点击/拖动结束，鼠标原逻辑 |

同步：已跑 `scripts/sync-webpages.ps1`（生成 `Services/WebPages.cs` + 复制到 `dist/web-index.html`）。刷新浏览器即生效。

---

## 5. 验证状态（如实说明）

### 已验证
- ✅ `dotnet build SoulLink.csproj` **0 error**（142 个既有 warning，与改动无关）。
- ✅ `scripts/sync-webpages.ps1` 成功生成 WebPages.cs + 复制 dist/web-index.html。
- ✅ 逐文件核实 Lead 梳理的链路（RawMouseInput → RdpConsole.OnRawDelta → LookInputQueue → SessionInput.LookRawAsync → RelayClient.MoveRelAsync → SendAsync/ProcessRequestsAsync → RelayAgent.ServePipe → moverel），与描述一致。
- ✅ 微软文档查证 `MOUSEINPUT` dx/dy 为 LONG、无截断、有指针加速（×4）。
- ✅ caniuse 查证 Pointer Lock 移动端不支持（iOS Safari / Chrome Android ❌）。

### 无法验证（需人工/实机）
- ❌ **实机手感**：A2 会话在线、用户在用，**未在 A2 会话里跑 moverel 实测**（遵守"不在用户会话乱点"的红线）。桌面端"去 Sleep 后视角是否更跟手、单向发送是否稳定"需用户实机转视角确认。
- ❌ **手机实机**：无法在本环境模拟 iOS/Android 触屏 + 真实网页。手势状态机（点击/转视角/长按拖动）逻辑已完成并自检，但**需用户在手机浏览器上实际划屏验证手感与阈值**。
- ❌ **NoReply 通道错位**：代码对称性已论证（代理不回写、宿主不读），但"多条 NoReply + 有回复命令交替"的字节级正确性**未在真实代理上跑过**。建议用户实机转视角 + 顺带点一下/滚轮，确认后续命令回复不错位。
- ❌ 拖动反馈（震动/视觉）**未实现**，列为待确认。

### 给用户的可执行手动验证步骤

**桌面端**：
1. 用管理员重建运行 `SoulLink.exe`（改动涉及 `RelayAgent`/`RelayClient`，必须重启让新 exe 生效；重启会断开 RDP，请自行取舍时机）。
2. 进 A2 自由视角，快速甩鼠标转视角：对比旧版，应明显更跟手、无"一顿一顿"的 114ms 级迟滞。
3. 转视角过程中点一下鼠标（走注入 down/up）、滚一下滚轮：确认这些**有回复**的命令回复没错位（日志无异常、操作正常）。

**手机端**（改完网页后，手机连同一局域网打开 `http://主机:10800` 或账号直连端口）：
1. 点一下画面空白处 → 左键单击。
2. 按住画面**立即**滑动 → 转视角（无光标跟随，只转视角）。
3. 按住不动约 0.25 秒**再**滑动 → 按住左键拖动。
4. 观察快速滑动是否顺滑（8ms 合并应无顿挫）。

---

## 6. 待用户确认项

1. **长按阈值 250ms 是否合适**：偏"慢手"可降到 200ms，偏"怕误触"可升到 300ms（改 `LOOK_TAP_MS` 一个常量）。
2. **位移阈值 10px 是否合适**：手指抖动大、误触多可升到 12~14px（改 `LOOK_SLIDE_PX`）。
3. **拖动反馈要不要加**：Vibration API（仅 Android）或极淡视觉提示，iOS 无震动。
4. **滑动灵敏度倍率**：前端 `lookDelta` 目前 1:1 转发位移（`moverel` 的 dx/dy = 手指位移像素）。若嫌太快/太慢，在 `lookDelta` 里乘一个倍率系数即可，建议范围 0.5~2.0。
5. **双指缩放**：当前双指仍走现有 wheel 逻辑（无捏合缩放），是否要做捏合 → 滚轮映射，待确认。
