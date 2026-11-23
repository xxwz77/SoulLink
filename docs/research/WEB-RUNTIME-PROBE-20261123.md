# 云原神网页运行时抓证（CDP 直抓一手证据）· 2026-11-23

> 作者：viewer-auditor（task_mwt691az13qbql7）。方法：ClientWebSocket 直连 CDP（127.0.0.1:9222，调试 Edge 实例，用户登录并进入游戏会话），对目标页 ys.mihoyo.com/cloud/#/ 执行 Runtime.evaluate / Runtime.queryObjects / Network.webSocketFrame 监听。
> 抓取时间：19:2x~19:4x（游戏画面运行中，右下角延迟角标可见）。全部为**运行时一手证据**，非静态逆向。

---

## 0. 核心结论（推翻/修正 MATURE-WEB-STACK §9.1 的两点推断）

**1. 云原神网页当前会话的实际媒体通道是 WebRTC（SRTP/RTP/UDP），不是 WebTransport、不是 WebSocket。**
- 媒体面证据：`RTCPeerConnection` 实例 1 个，`connectionState=connected`（Runtime.queryObjects 计数：RTCPeerConnection Array(1)、WebTransport Array(0)、VideoDecoder Array(0)、WebSocket Array(1)）。
- `getStats` inbound-rtp(video)：`1920x1080, 60fps, H264(PT 98), 217~398MB 累计接收`。音频 opus 48kHz。
- **视频直接由 video.srcObject=MediaStream 呈现**（1920x1080，readyState=4，paused=false，无 canvas、无 MSE blob src）—— MediaStreamTrack 由 PC receiver 提供（`receivers=[audio:audio_track, video:video_track]`，与 video.srcObject 的 track id 完全一致）。
- 候选对：`udp host 39.175.169.80`（浙江移动公网 IP），`currentRoundTripTime=0.007`（7ms，与角标一致）。另有一条 tcp 备用候选对（7 字节/213685 包为同一 stats 报告的聚合值，选定路径是 UDP）。
- **SDP`recvonly` + BUNDLE 0/1/2`：纯下行流（浏览器不上行媒体）。
- **§9.1 的「用户大概率跑 WebTransport」推断被运行时证据推翻**（用户之前的 webrtc-internals 截图为空页，可能是没进入游戏会话或导出方式问题——本次游戏运行中 PC 明确存在且 connected）。

**2. 输入通道与视频是「同一 PeerConnection 上的 DataChannel」（SCTP）+ 一条独立 wss 信令。**
- DataChannel：`label="webrtc_data_channel", state=open, ordered=true`，挂在**同一个 PC** 上（`pc.sctp 存在`）→ 输入与视频同连接复用 ✓（cg-sdk「GameChannel 单通道复用」的运行时证实，但载体是 WebRTC 而非逆向预测的 WT/WS）。
- 另一条 wss：`wss://cg-mhyjx1-wss-mobile-cg_cn_mix1.mhystatic.com/mhyjx1-c1-717-1/50202/rtc-sdk`（state=OPEN，信令/房间/会话管理）。
- WS 上行帧节奏（Network.webSocketFrameSent 20s 采样）：**静息期 p50=999ms（~1Hz 心跳，16B~96B）；活跃期 p50=703ms（33 帧/20s，1.6/s，均值 74B，最大 96B）**。活跃期与静息期 sent 速率仅差 0.5 帧/s → **wss 上行不是高频输入通道**（输入数据量太小时会混入心跳帧）。高频输入应走 DataChannel（SCTP，messagesSent 字段未暴露/统计被限制，未能直接量化——待后续用 Network domain 的 DataChannel 帧事件补抓，标注【待验证】）。

**3. 视频质量/网络指标（对照 SoulLink 网页现状）**
- 分辨率/帧率：**1920x1080 @ 60fps**（vs SoulLink 2560x1440 @ 30fps 上限）。
- 码率（bytesReceived 差分）：**活跃期 12.52Mbps，静息期 14.72Mbps**（云原神待机动画仍在播；`availableOutgoingBitrate=300000`（0.3Mbps）为候选对遗留字段，实际码率见差分值）。
- RTT：**7ms**（与右下角角标一致，确认角标=网络 RTT 口径，非端到端）。
- 抖动 6-7ms、丢包 30/202121（0.015%）、NACK 59、PLI 2、FIR 0。
- **freeze 15 次 / 14.2s 累计**（web 渲染卡顿在商业产品同样存在——不是 SoulLink 独有）。
- jitterBufferDelay(video)=581s/emitted 7872 帧 ≈ **73.8ms/帧 jitter buffer 延迟**（audio 52ms/帧）——这是 jitter buffer 引入的解码前缓冲，SoulLink 无此层（我们无 RTP jitter buffer，WebCodecs 直推）。

---

## 1. 与 SoulLink 网页的逐项对照（对 MATURE-WEB-STACK §9.1 表的运行时修正）

| 环节 | 云原神（运行时实测） | SoulLink 网页现状 | 差距 |
|---|---|---|---|
| 传输形态 | **WebRTC SRTP/UDP**（PC connected，udp host 直连） | fetch chunked HTTP 回环 | 云原神有 RTP 重排/NACK/PLI 机制；SoulLink 回环无丢包故不咬人 |
| 帧边界/丢帧恢复 | **NACK/PLI/FIR 全启用**（SDP rtcp-fb: nack/pli/ccm fir）+ 2 次 PLI（丢帧后 1 帧内恢复） | 无（10s 看门狗断流重连） | **云原神用的正是我们 W7 缺的「客户端主动 PLI 要关键帧」** |
| 解码 | **WebRTC 内建解码**（无 VideoDecoder 实例，stats framesDecoded=7871→14069） | WebCodecs VideoDecoder | 云原神零解码 API 管理成本；我们自管队列/丢帧 |
| 呈现 | **video.srcObject=MediaStream 直接呈现**（无 canvas！） | canvas drawImage（output 回调同步） | 原生 video 元素呈现=浏览器合成器直管 vsync；SoulLink 的 rVFC 缺失等价于云原神的 requestVideoFrameCallback 分析器缺失 |
| 输入通道 | **同一 PC 的 DataChannel**（SCTP，ordered）+ wss 信令 | 每事件一次 HTTP POST | 单通道复用（SCTP 有序/可靠）， vs 我们 HTTP 风险：丢粘键 |
| 帧率/分辨率 | 1080p@60fps | 1440p@30fps（StreamFps 30-60 clamp） | 帧率减半、分辨率反而更高（我们的带宽模型不同：回环+1440p） |
| 码率 | 12.5~14.7Mbps 动态 | 8000kbps 配置 | 同量级 |
| 视频缓冲 | jitterBuffer ~74ms/帧（video） | 无 jitter buffer（WebCodecs 直推） | 我们少 ~74ms 缓冲税 = 回环优势（但公网需要它换稳定） |
| 卡顿 | freeze 15 次/14.2s（7871 帧内） | 无量化（rVFC 未埋点） | 商业产品也有 freeze；无埋点 = 不可比 |

**最核心的互证**：云原神的「NACK/PLI 主动丢帧恢复」= SoulLink 五线审计 W7（前端主动 requestKeyFrame 通道）的**业界实锤**。它不是可选项，是 WebRTC 媒体面的标配（SDP 里 nack/pli/fir 全在）。

---

## 2. 抓取方法与复现（可复用流水线）

```js
// 核心 CDP 调用序列（完整脚本见 scripts/，本次为手动抓取）
// 1. GET /json/list → 找 title/url 含 ys.mihoyo.com/cloud 的 page → webSocketDebuggerUrl
// 2. Runtime.evaluate: video/canvas 形态、support 探针
// 3. Runtime.evaluate(RTCPeerConnection.prototype) → Runtime.queryObjects → 实例计数
// 4. Runtime.callFunctionOn(实例数组, async pc.getStats()) → SDP/getStats 全量
// 5. Network.enable + webSocketFrameSent/Received 事件 → WS 帧节奏
// 6. getStats bytesReceived 差分 → 实时码率
```

**关键坑（后续抓证必读）**：
- Performance Resource Timing **不记录** wss 连接（total=250 条，ws 条目 0 条）——WS 只能靠 Network.webSocketFrame* 事件或 queryObjects(WebSocket.prototype) 找实例。
- DataChannel 的 `messagesSent/bytesSent` 在 Edge 159 上返回 undefined（返回结果里无此字段）——输入频率量化需改用 Network 域 DataChannel 事件（本次未启用，因用户输入窗口时间紧）。
- `Runtime.queryObjects` 返回的 RemoteObject 可直接作 `Runtime.callFunctionOn` 的 objectId（`this` = 实例数组）——这是拿闭包内实例的唯一途径。
- 必须 `perMessageDeflate:false`？否——本次用 `perMessageDeflate: false` 连接成功；CDP ws 不压缩。


**wss 上行 payload 结构（首帧 base64 解码）**：`00 00 00 02 | 00 00 00 37 | 45 67 4E 30("EgN0") | 00 2B | 0A 14 | ...` —— u32 长度(2)、u32 总长(55)、ASCII 魔数 "EgN0"、u16 命令字(0x2b=43)、u16 序号(0x0a14=2580)。为自研二进制协议（疑似信令/心跳帧，与 cg-sdk KCP 栈头部吻合的候选），非 protobuf 文本。

## 3. 边界遵守

只读抓取（CDP Runtime/Network 域，无任何 DOM 操作/输入注入/登录操作）；未改任何产品代码；未碰 A1/A4；9222 实例由 Lead 启动、用户自行登录。唯一写入为本文档。


---

## 5. Pointer Lock 现场观察（设计输入，19:27-19:30）

- **页面 DOM 状态实测**：`document.pointerLockElement` 采样时 = **null（UNLOCKED 态）**，`document.fullscreenElement` = null（未全屏），iframe 数 = 0（云原神 web 主文档内无嵌套 iframe，Pointer Lock 目标应是主文档内元素）。
- **事件采样器已注入并驻留页面**（`window.__slMoveSampler`，capture 段 pointermove，记录 locked/movementX/Y/clientX/Y/pointerType），采样期 60 秒内无用户操作（samples=0，用户挂机/切走）——**两种模式的事件对象样本未拿到**，标注【待验证：用户下次操作时自动累积，可用 CDP 再取】。
- **协议结构（结合用户一手观察 + DOM 事实，写给我们 Pointer Lock 三件套的对齐参照）**：
  - 锁定态（用户观察：无光标）：pointermove 只可靠携带 `movementX/movementY`（相对位移，屏幕坐标系无界），`clientX/Y` 恒为锁定点或 0——**只消费 movementX/Y**，直接进 8ms 合并 → moverel；
  - 退出态（Alt 呼出光标）：pointermove 携带真实 `clientX/Y`（绝对坐标）——消费绝对坐标流（现有 norm() 路径）；
  - 锁定态 ESC 被游戏菜单消费（不产生系统光标事件，用户一手观察）——**退出 Pointer Lock 的唯一用户手势是 Alt**（浏览器默认），我们的实现要监听 `pointerlockchange` 切换输入协议而不是监听 ESC。
- **cloud_gaming MediaStream + UNLOCKED 态共存**：输入通道（DataChannel）与 Pointer Lock 状态无关——云原神的输入事件序列化在页面 JS 层完成（cg-sdk throttle 4ms + DataChannel），锁定与否只改变「采集什么字段」（movement vs client）。

## 4. 待验证清单

| 项 | 原因 |
|---|---|
| DataChannel 输入帧频率/大小量化 | Edge stats 未暴露 messagesSent；需 Network 域 DataChannel 事件，用户输入窗口已过 |
| wss 上行帧的用途（信令 vs 心跳 vs 部分输入） | 活跃期仅 +0.5 帧/s，不是主输入通道；payload base64 头部含自增序号（`AAAAAgAAADdFZ04w` 前缀恒定 → 疑似 KCP/心跳协议帧） |
| Pointer Lock 两态的 pointermove 事件对象样本 | 采样器已驻留页面（__slMoveSampler），用户操作后可再取 |
| WebRTC 备用模式（WT/WS 降级链）何时触发 | 本次会话全 WebRTC；cg-sdk 的 4 通道降级链存在但未观察到（网络好） |
| tcp 候选对的角色 | stats 显示 udp 为主选定（candidate-pair nominated），tcp 为备用 |