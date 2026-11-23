# 成熟产品鼠标输入全链路对照：报文 / 注入 / 坐标 / 光标 / SoulLink 差距

> 日期：2026-11-24
> 作者：moonflow-input（dsh-team `team_mwtjn0d77v8gtr` / task `task_mwtjocgj8a04nt`）
> 性质：只读代码审计 + 官方资料研究，未改产品代码、未操作任何进程或账号。外部证据均为 2026-11-24 抓取的官方仓库 `master` / `main` 当前文件；链接中的 `#Lx-Ly` 可直接回查。
> 证据标记：【代码事实】= 已逐行读源文件；【官方文档】= 官方协议/API 文档；【推断】= 尚须运行测量，不能当成已证实根因。

## 0. 结论摘要

1. **SoulLink 的输入没有统一全链路 FIFO，当前“按钮 FIFO”是假安全边界。** `move/moverel/wheel/noop` 绕开 `sendInputOrdered`；`AgentWebServer` 又把所有请求各开一个 `Task.Run`，注入端也无单一消费者。按钮可越过仍在途中的移动，落点与按下状态会错位（`Services/WebPages.cs:1345-1390,1671-1689,1748-1787`；`Relay/AgentWebServer.cs:115-130,483-539`）。Moonlight 用同一队列并在按钮前终止移动批次，noVNC 在按钮前显式冲刷移动，Guacamole/RDP 用事件队列串行处理；SoulLink 缺的是这条**跨类型保序不变式**。
2. **`releaseall` 在网页直连路径是确定性断线：** 前端 `pagehide/beforeunload/hidden` 都发送它（`Services/WebPages.cs:1413-1424,2203-2222`），`RelayAgent` 也有实现（`Relay/RelayAgent.cs:876-879`），但 `AgentWebServer` 白名单没有它（`Relay/AgentWebServer.cs:502-517`），直接回“未知动作”。不是“看门狗偶尔失效”，而是**立即释放请求必然被拒绝，只剩 5 秒兜底**。
3. **蓝色圆球不是远端光标。** WGC 明确关闭光标采集（`Relay/WgcCapture.cs:170-175,578,687-689`）；DDA 忽略 `LastMouseUpdateTime / PointerPosition / PointerShapeBufferSize`（`Relay/DxgiDdaCapture.cs:176-196,264-301`）；蓝球只把本地 `norm(e)` 再画回来（`Services/WebPages.cs:1460-1470,1789-1797`）。它给不出“宿主实际位置 / hotspot / 当前形状 / 注入是否被接受”。
4. **注入侧不要二选一，要按模型分路。** 绝对位置用 `SendInput(INPUT_MOUSE, MOVE|ABSOLUTE)` 或（保持当前会话兼容时）`SetCursorPos`，相对转视角用真正的相对 `SendInput(MOVE, dx,dy)`。当前虽然定义了 `MOUSEEVENTF_ABSOLUTE` 却从未用于移动（`Relay/RelayAgent.cs:81-93,805-833`）；鼠标按钮和滚轮仍走已被微软标为 superseded 的无返回值 `mouse_event`，失败时可能仍报成功。
5. **正确顺序不是先重写 WebRTC，而是先修顺序、坐标空间和光标真相源。** P0 补 `releaseall` 并把绝对 move + down/up/click 收进同一有界注入队列；P1 换 `SendInput`、发送完整按钮状态、保留原始 wheel delta、增加输入序列号/时间戳；P2 增加独立 cursor side-channel（形状仅在变化时发，位置即时本地预测+宿主确认）。444ms 画面延迟不应由自绘光标再等一遍。

## 1. 本文的统一链路口径

“输入卡”必须拆成四跳，不能拿 444ms 视频延迟代替输入延迟：

```
本地 DOM/SDL 事件
  → 坐标归一化 / 位移合并 / 按钮状态快照
  → 控制通道（有序、状态事件不丢）
  → 宿主注入（目标会话、目标桌面、正确坐标空间）
  → 光标呈现（本地即时光标；远端实际位置/形状的独立反馈）
```

**两条延迟不同：** `本地事件→宿主注入` 是输入通道 RTT；`本地事件→画面里看到变化` 还叠加抓屏/编码/传输/解码。成熟产品通常不让“光标上屏”必须等待后一条完整视频回路。

---

## 2. 产品 A：Moonlight-qt + moonlight-common-c → Sunshine

### 2.1 逐跳表

| 跳 | 成熟实现 | 报文格式 / 标志位 / 坐标编码 |
|---|---|---|
| 本地事件 | SDL 鼠标移动先 `SDL_PeepEvents` 抽干待处理移动：绝对位置覆盖为最新，相对 `xrel/yrel` **累加**（[mouse.cpp:93-117](https://github.com/moonlight-stream/moonlight-qt/blob/master/app/streaming/input/mouse.cpp#L93-L117)）。按钮单独发送 PRESS/RELEASE（同文件 [:80-90](https://github.com/moonlight-stream/moonlight-qt/blob/master/app/streaming/input/mouse.cpp#L80-L90)）。 | 绝对模式先从窗口坐标裁剪到实际视频 region，再发 `(x,y,dst.w,dst.h)`；相对模式发 `xrel/yrel`（[:122-178](https://github.com/moonlight-stream/moonlight-qt/blob/master/app/streaming/input/mouse.cpp#L122-L178)）。 |
| 合并与保序 | common-c 有 150 个 packet holder 的有界队列，移动合并窗口为 **1ms**（[InputStream.c:32,44,98](https://github.com/moonlight-stream/moonlight-common-c/blob/master/src/InputStream.c#L32-L98)）。绝对包 latest-wins（[:824-875](https://github.com/moonlight-stream/moonlight-common-c/blob/master/src/InputStream.c#L824-L875)），相对包累加（[:745-800](https://github.com/moonlight-stream/moonlight-common-c/blob/master/src/InputStream.c#L745-L800)）。 | **按钮到达时清空当前相对/绝对 batch 指针**，保证按钮前移动先发、按钮后移动不混进旧批（[:917-949](https://github.com/moonlight-stream/moonlight-common-c/blob/master/src/InputStream.c#L917-L949)）。 |
| 控制通道 | 独立 mouse channel；当前实现给这些输入包 `ENET_PACKET_FLAG_RELIABLE`（[:773-785](https://github.com/moonlight-stream/moonlight-common-c/blob/master/src/InputStream.c#L773-L785)、[:848-854](https://github.com/moonlight-stream/moonlight-common-c/blob/master/src/InputStream.c#L848-L854)、[:931-939](https://github.com/moonlight-stream/moonlight-common-c/blob/master/src/InputStream.c#L931-L939)）。 | `NV_INPUT_HEADER` = big-endian `size` + little-endian `magic`（[Input.h:11-14](https://github.com/moonlight-stream/moonlight-common-c/blob/master/src/Input.h#L11-L14)）。 |
| 相对报文 | `MOUSE_MOVE_REL_MAGIC=0x06` / Gen5 `0x07`；payload `int16 deltaX, deltaY`，big-endian 上线（[Input.h:39-45](https://github.com/moonlight-stream/moonlight-common-c/blob/master/src/Input.h#L39-L45)）。超 int16 的总累加量拆多个包，不丢尾数（[InputStream.c:396-461](https://github.com/moonlight-stream/moonlight-common-c/blob/master/src/InputStream.c#L396-L461)）。 | 无 `ABSOLUTE` 位，因为**包类型本身**已经区分相对/绝对。 |
| 绝对报文 | `MOUSE_MOVE_ABS_MAGIC=0x05`；`int16 x,y,unused,width,height`（[Input.h:47-60](https://github.com/moonlight-stream/moonlight-common-c/blob/master/src/Input.h#L47-L60)）。 | 坐标不是 0~1 float，而是**参考平面的整数像素 + 参考宽高**；客户端以 `referenceWidth-1/referenceHeight-1` 修边界舍入（InputStream.c:867-873）。 |
| 按钮报文 | Gen5 DOWN `0x08`、UP `0x09`；payload `uint8 button`（[Input.h:62-67](https://github.com/moonlight-stream/moonlight-common-c/blob/master/src/Input.h#L62-L67)）。 | 按钮是状态变化，不是“每次移动都重复按位”。 |
| 滚轮 | 垂直 `SCROLL_MAGIC(_GEN5)`，两个 big-endian int16 高分辨率 delta；横向 Sunshine 扩展 `SS_HSCROLL_MAGIC=0x55000001` + int16（[Input.h:111-124](https://github.com/moonlight-stream/moonlight-common-c/blob/master/src/Input.h#L111-L124)）。qt 保留 `preciseX/Y × 120`（[mouse.cpp:201-229](https://github.com/moonlight-stream/moonlight-qt/blob/master/app/streaming/input/mouse.cpp#L201-L229)）。 | 不把触控板高精度滚轮压成 ±120 一格。 |
| 宿主换算与注入 | Sunshine 读 x/y 和参考 width/height，经 `client_to_touchport`、旋转/offset 后 `platf::abs_mouse`（[src/input.cpp:918-950](https://github.com/LizardByte/Sunshine/blob/master/src/input.cpp#L918-L950)）。当前 Windows 后端走 virtual HID：相对 `move_relative(dx,dy)`，绝对 `move_absolute(x-offset,y-offset,width,height)`（[virtualhid_input.cpp:900-944](https://github.com/LizardByte/Sunshine/blob/master/src/platform/windows/virtualhid_input.cpp#L900-L944)）。 | Sunshine 对按钮状态去重（[src/input.cpp:958-972](https://github.com/LizardByte/Sunshine/blob/master/src/input.cpp#L958-L972)）。注意：当前 master 已偏虚拟 HID，**不能沿用旧文档“当前 Sunshine 仍一律 SendInput”的说法**。 |
| 光标呈现 | Moonlight-qt 绝对模式直接使用本地系统光标，视频 region 内可按捕获状态隐藏/恢复（mouse.cpp:164-174）；相对捕获模式发送相对位移，没有“蓝色影子球”。 | Moonlight 协议链路没有独立的 cursor-shape 上行/下行包；其低延迟关键是**本地光标即时呈现**，不是等视频帧回显。 |

### 2.2 可移植的关键点

- **相对移动只能累加，绝对移动才可 latest-wins。** SoulLink 两种策略方向是对的，但相对包 `moverel` 的独立 fetch 破坏了与按钮的顺序。
- Moonlight 文档明确指出：绝对移动对很多游戏无效，鼠标默认应相对；触屏若不支持原生 touch，可用绝对模式，游戏更适合作 trackpad 相对模式（[Limelight.h:584-603](https://github.com/moonlight-stream/moonlight-common-c/blob/master/src/Limelight.h#L584-L603)）。
- 若客户端只能拿到已加速的鼠标移动，Moonlight 另提供“维护虚拟客户端绝对位置，再发绝对位置”的接口，避免客户端+宿主双重加速；代价是仍有游戏兼容问题（[Limelight.h:605-622](https://github.com/moonlight-stream/moonlight-common-c/blob/master/src/Limelight.h#L605-L622)）。**这是一条兼容性退路，不应替代 Raw Input 相对模式。**

---

## 3. 产品 B：noVNC / RFB

### 3.1 逐跳表

| 跳 | 成熟实现 | 报文格式 / 标志位 / 坐标编码 |
|---|---|---|
| 本地事件 | canvas 监听 `mousedown/mouseup/mousemove`；`clientToElement` 转为 canvas 元素坐标，DOM `buttons` 转 RFB button mask（[rfb.js:1090-1177](https://github.com/novnc/noVNC/blob/master/core/rfb.js#L1090-L1177)）。 | 绝对整数像素；普通左/中/右分别为 bit 0/1/2。 |
| 去抖与保序 | move 以 **17ms** 节流，timer 只保留最新 `_mousePos`（[rfb.js:47,1187-1211](https://github.com/novnc/noVNC/blob/master/core/rfb.js#L1187-L1211)）。按钮处理先 `_flushMouseMoveTimer(x,y)` 再发按钮（[:1179-1185](https://github.com/novnc/noVNC/blob/master/core/rfb.js#L1179-L1185)）。 | 每次 `_sendMouse` 携带**当前完整 mask**，而不是孤立 button id。 |
| RFB 报文 | 普通 `PointerEvent` 固定 **6 字节**：`u8 type=5, u8 button-mask, u16 x, u16 y`，big-endian（[rfb.js:3127-3139](https://github.com/novnc/noVNC/blob/master/core/rfb.js#L3127-L3139)）。 | wheel up/down 借 mask bit 3/4 的瞬时 press+release；扩展按钮用 Extended Pointer Event，最高 marker bit=1，追加 higher bits（[:3142-3160](https://github.com/novnc/noVNC/blob/master/core/rfb.js#L3142-L3160)）。 |
| 宿主转发/注入 | noVNC 是浏览器客户端，不直接调 Windows API。Guacamole 的 VNC 后端接同一个 RFB 语义，直接 `SendPointerEvent(rfb_client,x,y,mask)`（[guacamole-server input.c:29-45](https://github.com/apache/guacamole-server/blob/master/src/protocols/vnc/input.c#L29-L45)），再由具体 VNC server 在宿主会话注入。 | **不能把“noVNC 的注入 API”写成 SendInput**；这一层由服务端实现，协议只规定绝对坐标和 mask。 |
| 光标呈现 | 客户端声明支持 Cursor 与 VMware Cursor pseudo-encoding（[rfb.js:2275-2276](https://github.com/novnc/noVNC/blob/master/core/rfb.js#L2275-L2276)）。RFB Cursor pseudo-encoding 的 FBU x/y 是 **hotspot**，负载为 `w*h*4 BGRX + ceil(w/8)*h AND mask`（[:2835-2867](https://github.com/novnc/noVNC/blob/master/core/rfb.js#L2835-L2867)）。 | 浏览器支持时变成 CSS `cursor:url(...) hotx hoty`；否则用不接收命中的浮动画布（[cursor.js:80-105](https://github.com/novnc/noVNC/blob/master/core/util/cursor.js#L80-L105)）。形状 0×0 会隐藏本地光标（[:80-115](https://github.com/novnc/noVNC/blob/master/core/util/cursor.js#L80-L115)）。 |

### 3.2 可移植的关键点

- 视频桌面更新与 cursor pseudo-encoding 分离；**形状/hotspot 是协议数据，不需要从视频图像猜**。
- noVNC 的 `showDotCursor` 只是“服务端给了全透明 cursor image 时”的可选可访问性兜底，不是默认本机影子（[rfb.js:3048-3076](https://github.com/novnc/noVNC/blob/master/core/rfb.js#L3048-L3076)）。这与 SoulLink 永远蓝球不同。
- 代价：RFB 普通 PointerEvent 每包带全 mask 和 x/y，比专用相对包大；wheel 用瞬时按钮模拟也不是高精度滚轮协议。

---

## 4. 产品 C：Apache Guacamole（Web → guacd → RDP/VNC）

### 4.1 逐跳表

| 跳 | 成熟实现 | 报文格式 / 标志位 / 坐标编码 |
|---|---|---|
| 本地事件与状态 | `Guacamole.Mouse.State` 同时保存 x/y、left/middle/right/up/down/back/forward（[Mouse.js:350-475](https://github.com/apache/guacamole-client/blob/master/guacamole-common-js/src/main/webapp/modules/Mouse.js#L350-L475)）。 | 绝对像素坐标；不是归一化 float。 |
| 本地即时呈现 | `sendMouseState` 先按 display scale 除回远端像素，**立即 `display.moveCursor`**，然后发报文（[Client.js:386-419](https://github.com/apache/guacamole-client/blob/master/guacamole-common-js/src/main/webapp/modules/Client.js#L386-L419)）。 | `moveCursor` 不等 atomic video frame，注释明确“for responsiveness ... immediately”（[Display.js:732-751](https://github.com/apache/guacamole-client/blob/master/guacamole-common-js/src/main/webapp/modules/Display.js#L732-L751)）。 |
| Guacamole 协议 | `mouse,x,y,mask` 指令，x/y 整数，mask 是当前所有按钮状态；官方[协议参考](https://guacamole.apache.org/doc/gug/protocol-reference.html#client-mouse-instruction)。 | mask: 左 0x01、中 0x02、右 0x04、滚轮上 0x08、下 0x10、back 0x20、forward 0x40（Client.js:407-418）。 |
| RDP 注入 | RDP handler 将事件入 `guac_rdp_input_event_enqueue`（[input.c:38-54](https://github.com/apache/guacamole-server/blob/master/src/protocols/rdp/input.c#L38-L54)），单队列处理再调 FreeRDP `input->MouseEvent` / `ExtendedMouseEvent`。 | mask 不变 → `PTR_FLAGS_MOVE,x,y`；变化则计算 released/pressed，按 `PTR_FLAGS_DOWN|BUTTONn` 等发送（[input-queue.c:50-169](https://github.com/apache/guacamole-server/blob/master/src/protocols/rdp/input-queue.c#L50-L169)）。**宿主侧由 RDP server 接收，不在浏览器进程调 SetCursorPos。** |
| VNC 注入 | guacd VNC 后端直接 `SendPointerEvent(x,y,mask)`（[vnc/input.c:29-45](https://github.com/apache/guacamole-server/blob/master/src/protocols/vnc/input.c#L29-L45)）。 | 与 noVNC 的 RFB PointerEvent 对齐。 |
| 光标呈现 | RDP pointer update 将 XOR/AND mask 转 alpha cursor，保存 layer，设置 hotspot（[pointer.c:34-107](https://github.com/apache/guacamole-server/blob/master/src/protocols/rdp/pointer.c#L34-L107)）；Guacamole `cursor` 指令传 hotspot + 源矩形，客户端可 CSS cursor 或独立 software layer。 | 光标 layer 位于所有显示 layer 之上，本地位置由 `moveCursor(x-hotspot,y-hotspot)` 立即移动（Display.js:60-113,686-702,743-751）。 |

### 4.2 可移植的关键点

- Guacamole 同时给出 SoulLink 最缺的两点：**本地光标即时移动**和**远端光标形状/hotspot 更新**；不是二选一。
- 一条 mouse 指令携带整个状态，天然可做 last-state 对账；而 SoulLink 每个 move/down/up 都是不同 JSON 动作且服务端并发执行。

---

## 5. 产品 D：RustDesk

| 跳 | 实现与证据 | 编码/坐标 |
|---|---|---|
| 本地事件→报文 | `send_mouse(mask,x,y,modifiers)` 构造 `MouseEvent`，封装进 protobuf `Message.mouse_event` 并发送（[client.rs:4316-4354](https://github.com/rustdesk/rustdesk/blob/master/src/client.rs#L4316-L4354)）。 | `mask` 低 3 位事件类型、`mask>>3` 按钮；x/y int32。绝对移动时 x/y=位置，相对移动时 x/y=dx/dy（[input_service.rs:1292-1299,1319-1368](https://github.com/rustdesk/rustdesk/blob/master/src/server/input_service.rs#L1292-L1368)）。 |
| 宿主注入 | Windows 进入 `handle_mouse_simulation_`，经 Enigo：绝对 `mouse_move_to(mx,my)`，相对 `mouse_move_relative(dx,dy)`，按钮分别 down/up（同文件 [:1281-1413](https://github.com/rustdesk/rustdesk/blob/master/src/server/input_service.rs#L1281-L1413)）。 | 相对 delta 限幅 ±10000，避免恶意/异常大值；相对模式不拿陈旧绝对坐标绘制光标（:1269-1276）。 |
| 光标呈现 | 有独立 `CursorPosition` 与 `CursorData` 服务；位置改变才发，形状按 id 缓存（[input_service.rs:349-395,395-470](https://github.com/rustdesk/rustdesk/blob/master/src/server/input_service.rs#L349-L470)）。 | 最近由某控制连接注入的位置在 300ms 窗口内不回发给该连接，避免自我回声（:369-378）；其他观看者仍收到。 |

**取舍：** RustDesk 证明通用远控也应把 cursor position/shape 当独立状态通道，而不是每帧编码进桌面。它还有观看者/控制者区分；SoulLink 将来多客户端时不能简单把每次本地输入都广播成“远端已移动”。

---

## 6. Parsec：能确认什么，不能编造什么

Parsec 客户端/宿主协议与 cursor 报文均闭源，本次没有公开源码可给出 `file:line`。因此：

- **可确认的成熟设计：** 官方 `client_immersive` 可将鼠标锁定到 Parsec 窗口；`host_virtual_mouse=1` 会创建虚拟鼠标，让无物理鼠标的宿主仍显示 cursor；官方注明该 dummy device 本身不产生输入（[All Advanced Configuration Options](https://support.parsec.app/hc/en-us/articles/32381443626516-All-Advanced-Configuration-Options) 的 Immersive Mode / Virtual Mouse）。
- **不可当作事实的写法：** “Parsec BUD 报文某字节是绝对坐标”“Parsec 一定用某私有 cursor channel”“Parsec 服务端一定把光标合成进视频”。均没有本次一手证据。
- **对 SoulLink 有用的结论只到产品层：** 捕获鼠标用沉浸/锁定模式，宿主无物理鼠标时用虚拟 HID 保持光标存在；不要把网页蓝球称为“Parsec 式虚拟鼠标”，二者不是同一东西。

---

## 7. 绝对 vs 相对：不是设置项，而是两种输入语义

| 模型 | 数据含义 | 优势 | 代价/边界 | 产品与场景 |
|---|---|---|---|---|
| 绝对位置 | 指针在参考平面 `(x,y)`；必须携带或隐含 reference width/height | 点哪指哪；丢中间点不影响最终位置；适合 latest-wins | 没有物理位移语义，Raw Input 游戏可能不转视角；窗口 letterbox、DPI、多显示器、远端旋转都会错位 | Moonlight 绝对模式；noVNC/RFB；Guacamole RDP/VNC；RustDesk `MOUSE_TYPE_MOVE`；触屏 tap/桌面 UI 拖拽 |
| 相对位移 | 自上次事件的 `(dx,dy)`；与当前绝对位置解耦 | 真正设备运动；适合 Pointer Lock、FPS/3D、无限转视角 | **任何丢包都永久累计误差**；需要可靠顺序；宿主 acceleration 可能二次放大；不能对旧 delta 做 latest-wins 覆盖 | Moonlight 默认鼠标模式；RustDesk `MOVE_RELATIVE`；SoulLink Pointer Lock/右键转视角 |
| 绝对仿真相对 | 客户端维护虚拟绝对坐标，把 delta 累加到该坐标再发绝对包 | 规避客户端/宿主双加速，适合 OS 只能给加速绝对捕获的移动设备 | 仍不具备 Raw Input 游戏兼容性；位置需要同步，丢包/多控制端有边界 | Moonlight `LiSendMouseMoveAsMousePositionEvent`，是退路不是默认 |

**SoulLink 应采用：** 桌面未锁定/触屏点按/拖动=绝对；Pointer Lock/游戏转视角=相对；不要拿 `SetCursorPos` 模拟相对，也不要在相对模式把圆球画在“猜测的绝对点”。

### 7.1 坐标空间的正确公式

前端已把 canvas 的 CSS 矩形、实际视频比例与 contain 黑边分开计算（`Services/WebPages.cs:1426-1444`），方向正确。但 `nx=1` 在 `AgentWebServer` 被换算为 `round(1*w)` 后再 clamp 到 `w-1`（`Relay/AgentWebServer.cs:495-498,526-527`）。统一契约会更好：

```
客户端：content-local CSS px → frame pixel → nx = x/(frameWidth-1)
宿主：x = round(nx*(targetWidth-1))
```

或使用 Moonlight 式整数 `(x,y,referenceWidth,referenceHeight)`，由宿主只负责 display→desktop 变换。必须明确：参考尺寸是**当前视频帧尺寸**还是**桌面尺寸**，视频帧 resize 与输入并发时不能各用各的旧尺寸。当前 `GetSystemMetrics(SM_CXSCREEN/SM_CYSCREEN)` 只代表主屏，不是任意 capture target 的通用坐标空间；多显示器/窗口 WGC 下需要 target rect + offset，而不是仅乘主屏宽高。

---

## 8. 宿主光标是否编码进视频：关键分叉

| 方案 | 成熟实现 | 优点 | 缺点/陷阱 | 对 SoulLink 的判定 |
|---|---|---|---|---|
| **A. 光标烘焙进视频帧** | Sunshine DDA 将 cursor texture 合成进输出；cursor-only 更新也产帧（团队并行报告 `AUDIT-20261124-01-SUNSHINE-CHAIN.md` 已逐行核查）；WGC 可直接 `IsCursorCaptureEnabled` | 所见即宿主；包含形状与 hotspot；无需浏览器新协议 | 受视频 RTT/编码延迟；静止画面也要因光标更新而编码；网页同时保留本地 cursor 会双影 | 可作兼容/观看模式的兜底，不应是唯一控制反馈 |
| **B. 本地即时光标 + 独立宿主 cursor 通道** | Guacamole 本地立即 move + cursor 指令；noVNC Cursor pseudo-encoding；RustDesk CursorPosition/CursorData；Moonlight 绝对模式本地系统光标 | 操控反馈不等视频；形状仅在变化时传输；位置可 latest-wins；支持观看者/控制者 | 需要 visible/shape/hotspot/位置协议；本地预测不等于宿主确认；相对模式必须避免伪造绝对位置 | **推荐主路径**；删除蓝球语义，改为真正的 cursor model |
| **C. 仅本地默认箭头，无远端形状** | 极简串流客户端常见 | 实现最少 | 不知道 I-beam/手型/隐藏状态，多控制端不work | 只够临时过渡，不是完成态 |

**DDA 已经免费提供分叉所需数据：** 微软 `DXGI_OUTDUPL_FRAME_INFO` 的 `LastMouseUpdateTime` 非零即位置或形状更新，`PointerPosition` 给的是 shape 左上角而非 hotspot，`PointerShapeBufferSize>0` 才有新形状；只有鼠标动时 `AccumulatedFrames/LastPresentTime` 可为 0（[微软官方定义](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/ns-dxgi1_2-dxgi_outdupl_frame_info)）。SoulLink 当前只用 `AccumulatedFrames`，还把它 `Math.Max(1,...)` 强制当成桌面帧（`Relay/DxgiDdaCapture.cs:190-196,299-301`），这会把 cursor-only 更新误记为桌面出帧。修复光标时必须同时更正这一语义。

**推荐协议最小字段：** `seq, x, y, visible, shapeId, width, height, hotspotX, hotspotY, rgba/压缩形状（仅 shapeId 变化时）`。位置跟随宿主确认，本地可做即时预测但样式应可区分；`relative` 模式不显示伪造的远端绝对位置。形状缓存与 RustDesk 相同思路即可，不要每 8ms 重发 RGBA。

---

## 9. 逐条对照 SoulLink 当前实现

### 9.1 本地事件与报文

| # | 现状证据 | 缺失/错误 | 成熟对照 |
|---|---|---|---|
| L1 | 按钮 FIFO 在 `Services/WebPages.cs:1345-1383`；move 独立 fire-and-forget 在 :1385-1390；moverel 在 :1671-1689 走 `sendInput` | **不是一个全输入队列。** down/up 可越过 pending move，moverel 与 click/up 也无统一 happens-before | Moonlight 按钮切断移动 batch；noVNC 按钮前 flush；Guac RDP 队列 |
| L2 | `postInputRaw` 每次 JSON+HTTP POST，返回再 `r.json()`（:1326-1336） | 无持久有序流、无序号/时间戳、无请求超时；失败仅 console.warn；move 即使不用结果也读响应 | Moonlight/Guac/RustDesk 持久控制通道 |
| L3 | 8ms latest-wins（:1763-1787）与 8ms 相对合并（:1667-1690） | 策略本身合理；**不能因此宣称并发恒为1**。`sendInputNoWait` 不等回包，125Hz 下 WAN 可积累多个 fetch | Moonlight 1ms 有界队列；noVNC 17ms；关键是有界背压 |
| L4 | 单击用 `click`，拖动才 down/up（:1861,1919-1920） | 单击按下与抬起合并，丢失真实按下时长；无法表达多键同时按下的完整状态 | noVNC/Guac 每包完整 mask |
| L5 | wheel 用 `e.deltaY>0 ? -120 : 120`（:1946-1949） | 丢掉 deltaMode、幅度、触控板高精度与 `deltaX`；只支持垂直滚轮 | Moonlight preciseX/Y×120 + HSCROLL |
| L6 | 鼠标按钮映射仅 left/middle/right（:1719），pointer lock 首击策略在 :1720-1731 | X1/X2 被归到 left；首次点击被吃掉是模式切换语义，不应由蓝球补偿 | Moonlight 支持 X1/X2；捕获首击不进游戏 |
| L7 | `releaseAllInputs` 清掉 FIFO 后 fire-and-forget releaseall（:1413-1424），钩子 :2203-2222 | 直连代理白名单无 releaseall，必然拒绝；且清队列会丢掉尚未发送的 up，全部寄望 5s watchdog | Sunshine/RustDesk 对按住状态做断线对账 |

### 9.2 `AgentWebServer` 与宿主注入

| # | 文件:行 | 当前事实 | 差距与可执行修正 |
|---|---|---|---|
| A1 | `Relay/AgentWebServer.cs:115-130` | 每个连接请求独立 `Task.Run` | 对 move/down/up 没有服务端顺序保证。按 viewer/session 建**单消费者有界队列**；按钮前冲刷最新绝对 move，相对增量合并且不覆盖；up/releaseall 不丢 |
| A2 | :478-481 | 文本协议 `{action,x,y,button,delta,dx,dy,count}` | 无按钮全状态/序列号/坐标参考尺寸；短期可兼容加字段，长期用一条 WS 控制流，而非每事件新 HTTP |
| A3 | :495-498 | 用 `GetSystemMetrics(0/1)` 把 0~1 转主屏像素 | 对窗口捕获/非主屏/旋转/虚拟 display 不充分。由 capture target 元数据确定宽高、origin、rotation；用 `w-1/h-1` 明确边缘映射 |
| A4 | :502-517 | action 白名单没有 `releaseall` | **P0 确定 bug：补白名单并透传，前端读取失败要记日志；保留 watchdog 兜底** |
| A5 | :526-528 | 仅 click 设置 `Move=1`；down 不先定位 | down 可发生在宿主旧位置。协议允许在 down 携带 x/y 时，以同一原子输入批次完成 move+down |
| A6 | :538-539 | `RelayAgent.ExecuteWeb` 同步执行后回 `{ok,error}` | 鼠标 `mouse_event` 无返回码，resp.Ok 不等于系统接受。应改 `SendInput` 并检查实际插入数，不把 HTTP 200 当注入成功 |
| A7 | `Relay/RelayAgent.cs:81-95` | 已定义 MOVE/DOWN/UP/WHEEL/HWHEEL/ABSOLUTE/X 标志 | `ABSOLUTE/HWHEEL` 当前只是常量；真正 move 走 `SetCursorPos`，滚轮只 `WHEEL`（:805-884） |
| A8 | :275-292,868-884 | 按钮与 wheel 用 `mouse_event`；`MouseButton` 返回 void | 换 `SendInput(INPUT_MOUSE)`，保留 DOWN/UP/XDOWN/XUP/WHEEL/HWHEEL；重复状态去重；失败回错误 |
| A9 | :805-817 | 绝对 `SetCursorPos` 后每次 `GetCursorPos` + 两次 `GetSystemMetrics` | 高频热路径不需要这些读回；读回也不能证明目标应用消费了输入。改为低频诊断/序列号确认，不纳入每次 move 的注入尾延迟 |
| A10 | :820-847 | 相对 `mouse_event(MOVE,totalDx,totalDy)` 后读回位置；仍回 `Ok=true` | 保留相对语义，改 `SendInput` 检查返回值；不要为视觉回显把相对位移换算成伪绝对位置 |
| A11 | :850-865 | click 先 `SetCursorPos`，down 后固定 `Thread.Sleep(18)`，多击间再 Sleep 90 | 每个请求线程被占用且固定延时；如需真实 press duration，应由 down/up 状态表达，不以固定 sleep 模拟所有点击 |
| A12 | :98-115,342-388 | 有 PressedButtons/PressedKeys、5 秒 watchdog | 机制存在但 `_webHeldInput` 是进程级 bool，任意一个 up 就清 false，多键/多按钮仍按住时可能失去兜底。改为由状态集合是否为空决定，并按控制连接分账 |
| A13 | `Relay/SessionRecorder.cs` / `AgentWebServer.cs:273-296` | 视频与输入端点不同 | **没有证据表明 HttpListener 是“单线程视频必然堵输入”**：accept 后确实 `Task.Run`。不要凭“共用 listener”下根因结论；应测输入排队、请求开始、注入完成各时间戳 |

### 9.3 光标与蓝球

- `#plCursor` 只是 18px CSS 圆，`pointer-events:none`（`Services/WebPages.cs:335-340`），没有远端数据。
- 本地 `pointermove` 更新并显示（:1789-1797），Pointer Lock 成功/解锁都隐藏（:1583-1613）；点击隐藏是状态机的副作用。
- WGC 两处主动 `IsCursorCaptureEnabled=false`（`Relay/WgcCapture.cs:174,578`）；DDA 没有读 pointer metadata；BitBlt 另有 `GetCursorInfo/DrawIconEx` 烘焙路径（`Relay/BitBltCapture.cs:85-115,132,184`），所以**不同抓屏后端光标行为不一致**。
- 建议删除“本地位置蓝球”，但保留触屏点击 ripple；新增的真正远端 cursor layer 必须支持 hotspot、visible 和形状，不是把蓝球换成箭头却仍旧只跟随本地事件。

---

## 10. 可执行建议（按依赖排序）

### P0：先修确定性错误与保序（不改视频传输协议）

1. `AgentWebServer` 补 `releaseall`，并让前端释放动作有失败日志；5 秒 watchdog 继续保留。
2. 建立 viewer/session 级单一输入消费者：绝对 move latest-wins、相对 move 累加、按钮/滚轮状态事件不可丢。按钮事件前先提交其发生时的最新绝对位置；up/releaseall 进入同一顺序流。
3. down 带 x/y 时，在宿主用同一批次完成定位+按下；避免在旧位置按下。
4. 给所有输入加 `seq`（诊断可先用日志）：记录本地产生、开始发送、Agent 开始处理、SendInput 返回四时间点；先测出卡在何处。

### P1：换正确注入与坐标模型

1. 用 `SendInput` 替代鼠标 `mouse_event`；绝对 `MOVE|ABSOLUTE` 用 0..65535 或显式 virtual-screen 映射，相对仅 `MOVE`。检查返回插入数，不把失败写成成功。
2. wheel 保留原始 `deltaY/deltaX/deltaMode`，转换到 Windows 高分辨率 delta；接 `HWHEEL`。
3. 坐标契约从“主屏归一化”升级为“capture target 元数据 + reference size + origin/rotation”，并发 resize 时带 frame generation。
4. `_webHeldInput` 不再用单 bool；依据 PressedButtons/PressedKeys 实际集合释放，避免多键误清状态。

### P2：真正的光标通道与 UX

1. 删除蓝球本地影子语义。默认桌面绝对模式可先用浏览器本地 cursor 作即时反馈，但远端 cursor layer 要根据宿主确认更新。
2. DDA 读取 `LastMouseUpdateTime/PointerPosition/PointerShapeBufferSize/GetFramePointerShape`；WGC 需要另外获取宿主 cursor 状态，不能仅靠关闭/开启视频内光标就完成独立通道。
3. 形状缓存 + hotspot + visible；cursor-only 更新不必强制视频编码，也不能被 `AccumulatedFrames` 当成新桌面帧。
4. 需要“视频里也有光标”的观看/录制场景，再开启帧内合成；控制页默认避免本地光标 + 烘焙光标双影。

### 非建议

- 不把“上 WebRTC/WebTransport”当作修落点和按钮乱序的前置条件。
- 不用更大的 8ms/17ms 去抖窗口掩盖乱序。
- 不全局关闭宿主鼠标加速来“修”相对输入（会改变系统状态，也不是协议修复）。
- 不宣称 Parsec 的闭源报文格式或 cursor 内部通道已核实。

---

## 11. 证据索引与复核边界

- SoulLink 当前源：`Services/WebPages.cs`、`Relay/AgentWebServer.cs`、`Relay/RelayAgent.cs`、`Relay/WgcCapture.cs`、`Relay/DxgiDdaCapture.cs`、`Relay/BitBltCapture.cs`，行号均已在对应段落列出。
- 外部一手源码：上述 Moonlight、Sunshine、noVNC、Guacamole、RustDesk 的 GitHub 行锚链接；外部代码只读借鉴设计，注意各项目许可，不复制 GPL 实现进闭源产品。
- Windows 注入依据：微软 [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput) 明确数组事件串行插入且返回实际插入数；[mouse_event](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-mouse_event) 明确已被 SendInput 取代，并说明 ABSOLUTE=0x8000、WHEEL=0x0800、HWHEEL=0x01000 与相对 acceleration；[SetCursorPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setcursorpos) 的坐标还会受 ClipCursor 和输入桌面约束。
- 本次没有做生产机输入实测，因此“SendInput 比 SetCursorPos 在每个事件中快多少”“HTTP 队列实际积多少”均不下数值结论；这些需要后续在非挂机账号上用时间戳验证。
