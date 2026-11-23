# 浏览器端远程桌面/云游戏技术栈核查：向日葵·ToDesk·UU·开源参照·商业云游戏 Web

> 日期：2026-11-23
> 作者：mature-researcher（dsh-team team_mwt4bbjy11d7lsz / member m_mwt4bbnt11g753x / task task_mwt5afyr132cctj）
> 性质：纯只读调研。未改业务代码/脚本/配置；唯一写入文件即本文档。未碰 dist/stable、未用 A1/A4、未重启进程、未发布、未碰 COS、未改 latest.json。
> 方法：web_fetch 官方页面 + curl.exe -x 代理 抓 raw.githubusercontent 源码 + codeload tarball 解包逐行核对 + MDN/W3C 规范原文。
> 证据分级：【官方文档】=官网/官方手册/规范原文；【官方源码】=GitHub 源码逐行（file:line 可回查）；【营销口径】=官网宣传文案（非技术承诺）；【业界共识】=多来源一致但未逐字核对；【一手证据】=用户 DevTools 截图/抓包（2026-11-23）；【未核实】=查不到，明确标注；【待补】=等后续一手 dump。

---

## 0. 一句话结论

游戏级浏览器串流的最小技术集 = WebRTC（媒体）+ H.264 硬编（NVENC/VA-API）+ 无限 GOP/按需 IDR + CBR 码控 + transport-wide-cc 拥塞降档 + RTCDataChannel（输入/信令）+ 有界 jitter buffer。开源权威参照是 selkies-gstreamer（云游戏向，README 自述 "Moonlight/Stadia/GFN in noVNC form factor"）；商业云游戏 web（GFN/xCloud/云原神）走同一条 WebRTC 路但细节闭源。国内三大家（向日葵/ToDesk/UU）网页端媒体/信令全部闭源查不到，只能靠用户抓包佐证。

关键判断：SoulLink 网页端换 WebRTC 形态是彻底通的，但最小改造不是换掉现有 chunked-H.264，而是三步：① 输入通道从 HTTP 挪到 WebSocket/DataChannel；② 加传输层拥塞降档；③ 修断连 releaseall 触发。现有架构（WebCodecs 硬解 + H.264 + 按需 IDR + 8ms 输入合并）与 selkies 的 WebSocket 路线同构度极高，不必一步跳 WebRTC。

---

## 1. 向日葵 / ToDesk / UU 网页端技术栈 —— 全部闭源，查不到

| 产品 | 网页端入口 | 官方公开的媒体/信令技术 | 证据 | 判定 |
|---|---|---|---|---|
| 向日葵（贝锐 Oray） | sunlogin.oray.com | 官网只写「自研技术，低延迟」「7ms 零感延迟」「RDP 技术加持」「自主 P2P 数据传输协议（RSA+AES，支持国密 SM2/3/4）」 | sunlogin.oray.com 首页（Nuxt SSR，前端 dist JS 混淆） | 【营销口径】媒体通道/信令细节【未核实】 |
| ToDesk | www.todesk.com | 官网写「8K 无损原画，360帧，3ms 极低延迟」「RTC+VIP 专属节点」「ZeroSync 传输引擎」 | todesk.com 首页 | 【营销口径】技术细节【未核实】 |
| 网易 UU 远程 | uu.163.com | 官网写「4K 144 帧、最高 500 Mbps、毫秒级」 | uu.163.com 首页（前序 CN-CLOUD-GAMING §1 已录） | 【营销口径】技术细节【未核实】 |

三点硬结论：
1. 三家网页端媒体/信令均无任何公开技术文档（无 WebRTC/MSE/自研的官方说明）。向日葵首页有「自主 P2P 数据传输协议 + RSA2048 密钥交换 + AES + SM 国密」的加密层描述，但加密层≠传输层，未说明媒体是 RTP/QUIC/私有 UDP。
2. 「7ms / 3ms 极低延迟」是网络 RTT 或单段口径，不是端到端延迟（同云原神 UI「7ms」角标=网络 RTT 佐证，见 §5.3）。与本任务上一份 MATURE-ARCH-FACTS §1 结论一致。
3. 向日葵首页视频演示文案「您的浏览器不支持 video 标签」——暗示网页端用 video 元素承载画面（MSE 或 srcObject=MediaStream 两可），但无法从页面静态 HTML 定死是 WebRTC 还是 MSE。

---

## 2. 开源浏览器远程桌面实现逐个核查（权威参照）

### 2.1 selkies-gstreamer —— 云游戏向 WebRTC，最值得抄 【官方源码+官方文档】

README（raw.githubusercontent，已抓）：https://raw.githubusercontent.com/selkies-project/selkies-gstreamer/master/README.md
- 自述定位：「Moonlight, Google Stadia, or GeForce NOW in noVNC form factor for Linux X11 and Wayland, in any HTML5 web interface ... at least 60 fps on Full HD」。
- 传输：「streams over plain WebSockets by default, with WebRTC available as an opt-in transport」——即默认 WebSocket、WebRTC 可选，与 SoulLink 现有 chunked 路线同构。
- 许可：MPL 2.0（可参考实现，不强制闭源开源）。

Settings Reference（已抓 raw，L 行号可回查）：https://raw.githubusercontent.com/selkies-project/selkies-gstreamer/master/docs/settings.md

媒体通道关键参数：
| 项 | 默认值 | 证据（settings.md 行号） |
|---|---|---|
| 编码器 | h264enc（枚举 h264/h265/vp8/vp9/av1/jpeg） | L34 |
| 码控 | cbr（枚举 cbr/crf），--video-bitrate 默认 8000 kbps（8 Mbps） | L38, L40 |
| 帧率 | 60（范围 8-240） | L37 |
| GOP | --keyframe-interval 默认 0 = 无限 GOP，仅按需 keyframe（join/reset/请求） | L43 |
| 拥塞降档 | --congestion-control 默认 true：WebRTC 用 transport-wide-cc 反馈（loss + one-way delay），WebSocket 用队列 RTT 反馈 | L42 |
| 发送节流 | --webrtc-pacer 默认 true：audio/RTCP > data-channel > video 严格优先级 + IDR-aware 视频队列预算 + GOP-reset 恢复 | L56 |
| 码率自适应 | 4:4:4/10bit 客户端能力协商、按 codec 步进降级（硬编优先→软编→jpeg） | L34-36 |
| 输入 | 键盘/鼠标/手柄（uinput/gamepad），--raw-pointer-motion 默认 true（pointer lock 下取未加速原始位移） | L92, L160 |

这是游戏级浏览器串流最小技术集的完整开源答案：WebRTC（可选）+ H.264 硬编 + 无限 GOP/按需 IDR + CBR + transport-wide-cc 降档 + DataChannel 输入 + 有界队列。与 SoulLink 现有做法几乎逐条对应。

### 2.2 MeshCentral —— web 远程桌面走 WebRTC 【官方文档+官方源码】

README：https://raw.githubusercontent.com/Ylianst/MeshCentral/master/readme.md
- 「full web-based remote desktop」；Design Architecture Guide 明确含「browser to agent relay and WebRTC」章节。
- 许可 Apache 2.0。
- 媒体/信令：agent↔server↔browser 三端，浏览器远程桌面默认 WebRTC（降级走 WebSocket 的 MJPEG/批量更新）。
- 定位：IT 运维远管，非游戏向，无低延迟编码参数公开。

### 2.3 Remotely —— .NET+Blazor+SignalR，静态图像编码（对照：非 WebRTC 主流） 【官方源码】

codeload tarball 解包逐行核对（immense/Remotely master）：
- 媒体通道：非 WebRTC 视频流。用 SkiaSharp 静态图像编码（Desktop.Core/Utilities/ImageUtils.cs EncodeBitmap），帧数据 EncodedImageBytes 按 50,000 字节分片（Desktop.Core/Services/Viewer.cs:223 Chunk(50_000)）通过 SignalR HubConnection 传（CasterSocket.cs 继承 SignalR）。
- WebRTC 已废弃：migrations 里 20220827150639_Remove deprecated WebRTC.cs（PostgreSql/Sqlite/SqlServer 三库都有）——证明早期走过 WebRTC 后移除，退回「静态图像分片 + SignalR」。
- 输入：IKeyboardMouseInput + CasterSocket SignalR 双向。
- 定位：运维远程控制，非低延迟游戏，分片静态图像方案延迟高。

### 2.4 RustDesk web 客户端 —— Flutter Web（WASM），自研协议非 WebRTC 【官方源码】

README：https://raw.githubusercontent.com/rustdesk/rustdesk/master/README.md
- 依赖：libvpx/libyuv/opus/aom（vcpkg）——自研编解码栈；GUI 用 Flutter（flutter/ 目录）。
- 媒体通道：自研协议，README 文件结构 src/server（audio/clipboard/input/video services）、libs/scrap（screen capture + video codecs）、rendezvous_mediator（TCP hole punching / relay）——TCP/UDP 打洞 + 中继，非 WebRTC。
- web 客户端 = Flutter Web 编译到 WASM，媒体仍走自研协议（不是浏览器原生 WebRTC）。
- 许可 AGPL-3.0（不可抄代码进闭源）。

### 2.5 KasmVNC —— 浏览器 VNC，WebCodecs 视频 + WebRTC UDP 【官方文档+官方源码】

README：https://raw.githubusercontent.com/kasmtech/KasmVNC/master/README.md
- 脱离 RFB 规范，仅浏览器访问。媒体三形态并存：
  - WebCodecs video streaming（H.264/H.265/AV1）—— 与 SoulLink 现有 WebCodecs 解码同构。
  - WebRTC UDP Transit（可选）。
  - JPEG/WebP 静态图像（CPU 可用性自动混合，QOI 无损 for 局域网）。
- 配置 YAML（README 内嵌）：video_streaming_mode: codec:auto / quality:17 / gop:24；udp: public_ip/port/stun_server:auto（WebRTC UDP 用）。
- 输入：键盘/鼠标（raw_keyboard、relative cursor support、cursor lock、IME）。
- 许可：GPL-2.0（KasmVNC 本身，前端 kasmweb 另有协议）——可借鉴思想不可抄。

### 2.6 noVNC —— WebSocket + canvas 传统路线（对照） 【官方文档】

README：https://raw.githubusercontent.com/novnc/noVNC/master/README.md
- 「HTML VNC client」：WebSocket（websockify 代理）承载 RFB 协议，画面用 canvas 绘制（JPEG/tight/raw/ZRLE 等 VNC 编码），支持 H.264（VNC 扩展）。
- 定位：传统 VNC 的浏览器皮，非低延迟游戏，延迟受 VNC 服务端 + canvas 绘制限制。
- 许可 MPL 2.0。
- 对照意义：这是「不换服务端、纯 canvas 画静态图像」的老路线，延迟最高，SoulLink 现有 WebCodecs 硬解已经超越它。

### 2.7 六个开源项目对比总表

| 项目 | 媒体通道 | 输入通道 | 参数热更新 | 延迟水平 | 许可 | 对 SoulLink 价值 |
|---|---|---|---|---|---|---|
| selkies-gstreamer | WebSocket（默认）/ WebRTC（可选）+ H264/H265/VP8/VP9/AV1 | DataChannel/WS + uinput/gamepad | dashboard 实时改 bitrate/codec，congestion-control 自动降档 | 云游戏级（60fps FHD） | MPL-2.0 | 最高（逐条对应） |
| MeshCentral | WebRTC（降级 WS MJPEG） | WebRTC 信道 | — | 运维级 | Apache-2.0 | 中（架构参考） |
| Remotely | 静态图像 50KB 分片 + SignalR（WebRTC 已废弃） | SignalR | — | 低（运维） | 待核 | 低（反例：分片图像延迟高） |
| RustDesk web | 自研协议（TCP/UDP 打洞+中继），WASM | 自研 | — | 中低 | AGPL-3.0 | 低（不可抄，自研协议） |
| KasmVNC | WebCodecs H264/H265/AV1 + WebRTC UDP + JPEG/WebP | WS 键鼠 | 客户端可改编码设置 | 中低 | GPL-2.0 | 中（WebCodecs 同构） |
| noVNC | WebSocket + canvas（RFB） | WS | — | 低（传统） | MPL-2.0 | 低（对照基线） |

---

## 3. WebRTC 浏览器串流的延迟构成与硬件解码现状 【官方文档（W3C/MDN）】

### 3.1 接收侧低延迟：playoutDelayHint（已废弃）与 jitterBufferTarget（新）

- playoutDelayHint（RTCRtpReceiver 属性，webrtc-extensions 曾提议）：hint to the User Agent to control the amount of audio/video that is buffered for playout，2024 年起从规范移除（MDN 404，webrtc-extensions 现文档已无此节）——已废弃，不可依赖。
- jitterBufferTarget（RTCRtpReceiver 属性，W3C WebRTC + MDN）：preferred duration, in ms, for which the jitter buffer should hold media before playing it out，范围 0-4000ms，默认 0（不设=UA 自定）。仅 "influences" 不直接设定：UA 会按网络/内存动态调整。MDN 标注「Baseline 2026-09 新可用」。来源：https://developer.mozilla.org/en-US/docs/Web/API/RTCRtpReceiver/jitterBufferTarget
- targetLatency（RTCConfiguration，webrtc-extensions）：lowest（默认）/none——User Agent SHOULD use techniques like network slicing ... to optimize the connection latency。来源：https://w3c.github.io/webrtc-extensions/

结论：浏览器 WebRTC 接收侧的低延迟只有三个可调旋钮，全是 hint：jitterBufferTarget（调小）、targetLatency=lowest、以及 stats 观测。没有硬保证——与上一份 MATURE-ARCH-FACTS §4.1 WebCodecs 结论一致。

### 3.2 硬件解码现状

- WebRTC 视频解码自动硬件加速（Chromium/Firefox/Safari 均对 H.264/VP8/VP9 硬解），无 API 强制/查询硬解。
- 无硬解信号 = Parsec 官方「web client Decode N/A」（上一份已引：https://support.parsec.app/hc/en-us/articles/32381352822804 ）。
- 浏览器硬解 codec 覆盖（业界共识）：H.264 全平台；VP8/VP9 Chromium/Firefox；H.265 Chromium（需硬件支持，Safari 部分）；AV1 Chromium 较新。H.264 是浏览器串流最稳的低延迟 codec 首选（selkies 默认 h264enc 印证）。

---

## 4. 输入与控制信令：DataChannel 【官方文档】

- WebRTC 标准 RTCDataChannel（SCTP over DTLS），ordered/maxRetransmits 可配可靠/不可靠。远程桌面输入（键鼠/触控）业内标准做法 = 不可靠低延迟 DataChannel（ordered=false, maxRetransmits=0 或 small）。
- selkies：README 自述「resilient keyboard, mouse, and gamepad input」；--raw-pointer-motion 默认 true（pointer lock 取未加速位移，对应 SoulLink 的 moverel 相对位移）。
- SoulLink 现状：输入走独立 POST /api/input HTTP（Relay/AgentWebServer.cs:294），前端 8ms 合并（web-index.html:1371-1394）。换 DataChannel 的收益 = 免 HTTP 头部开销 + 天然无序低延迟 + 断连释放语义，但非刚需（现有 HTTP 局域网 RTT≈3ms 已够，见 MATURE-ARCH-FACTS §1）。

---

## 5. 商业云游戏 web（浏览器串流极限形态）

### 5.1 GeForce NOW web —— 【未核实技术栈，运营中】

- play.geforcenow.com 首页 HTML（已抓 70KB）：无 WebRTC/RTCPeerConnection/codec 明文（JS bundle 混淆），无法静态定死媒体通道。
- 官方公开：Chrome/Edge 支持浏览器游玩，4K/120fps 需会员。媒体通道官方未公开（业界共识指向 WebRTC/自研 RTP-UDP）。
- 【待补】需一手 dump（webrtc-internals / video 元素 srcObject）。

### 5.2 Xbox Cloud Gaming (xCloud) web —— 【业界共识 WebRTC，官方未逐字公开】

- xbox.com/play：微软官方页面有 enablemiconmacsafari、enablemediaplayonweb、usetizenh264mainsdphack（"h264 main sdp hack"——直接暴露 H.264 + SDP 协商）等 feature flag（play.xbox.com HTML 已抓，明文可见）。
- 「h264 main sdp」flag = 强佐证 xCloud web 用 WebRTC（SDP 协商）+ H.264。
- 官方博客（微软多次讲 xCloud 用 direct streaming 低延迟架构）未逐字核到传输协议原文，标【业界共识+feature flag 佐证】。

### 5.3 云原神 web —— 【一手证据已到 4 项，传输层证据待补】

前序 CN-CLOUD-GAMING-20261122.md 已录：米哈游云·原神「网页版（WebRTC 播放）」「最低码率 ≥15 Mbps」。

用户 DevTools 一手证据（2026-11-23 截图，已确定 4 件事）：
1. 【PWA】云原神 web 是 PWA：控制台报 beforeinstallprompt / apple-mobile-web-app-capable 相关错误，说明走 PWA 安装形态。
2. 【输入=Pointer Lock】输入用 Pointer Lock API：requestPointerLock 系列报错，且报错「wrong document for pointer lock」——证明输入是「指针锁定 + 相对位移」模式，不是绝对坐标。
3. 【跨源 iframe 渲染】游戏渲染跑在跨源 iframe（document 为 clouid/#/…，主文档 top 查不到游戏元素）——渲染隔离在 iframe 内，主文档只做外壳/控制面板。
4. 【全屏】尝试编程式全屏（enter fullscreen without user gesture 报错）——游戏进入时请求全屏。

传输层证据（video srcObject 判定 / webrtc-internals dump / Network WS+RTC 过滤）用户正在抓，到位后由 Lead 转发补 §9.1。

★ 云原神 UI 自带「7ms」角标 = 网络 RTT 显示，不是端到端延迟——这是「个位数毫秒=网络段口径」的一手佐证（与 MATURE-ARCH-FACTS §1 结论互证）。

### 5.4 腾讯 START / 网易云游戏 web —— 【未核实】

无公开技术文档，闭源。标【未核实】。

### 5.5 商业云游戏 web vs 开源云游戏 web 对照

| 维度 | 商业（GFN/xCloud/云原神） | 开源参照（selkies） | 共同最小技术集 |
|---|---|---|---|
| 媒体通道 | WebRTC（业界共识，xCloud 有 h264-sdp flag 佐证；云原神传输层待 dump 确认） | WebSocket 默认 / WebRTC 可选 | WebRTC |
| codec | H.264（xCloud flag）/ 闭源 | H.264 默认（硬编 NVENC/VA-API） | H.264 硬编 |
| GOP | 闭源 | 无限 GOP + 按需 IDR | 按需 IDR |
| 码控 | 闭源 | CBR + congestion-control | CBR + 拥塞降档 |
| 输入 | Pointer Lock 相对位移（云原神一手证据） | DataChannel/WS + gamepad + raw-pointer-motion | DataChannel 键鼠/手柄 + 相对位移 |
| 延迟口径 | 「7ms」角标=网络 RTT（云原神） | 60fps FHD 云游戏级 | 网络 RTT + 编解码分段，无端到端个位数 |

---

## 6. 游戏级浏览器串流的最小技术集（结论）

1. 媒体通道：WebRTC（可选，selkies 证明 WebSocket 也能 60fps，但 WebRTC 给 NAT 打洞/TURN/丢包恢复/拥塞控制免费）。SoulLink 局域网场景 WebSocket 或现有 chunked-H.264 已够用，公网才需 WebRTC。
2. codec：H.264 硬编（NVENC 首选，selkies 默认；xCloud h264 flag 佐证）——与 SoulLink 已做的 NVENC SDK 直通一致。
3. GOP/pacing：无限 GOP + 按需 IDR（客户端 join/reset 时才发 keyframe）+ IDR-aware 发送队列（selkies --webrtc-pacer L56）——与 SoulLink 已做的按需 IDR 一致。
4. 码控 + 自适应：CBR + 拥塞降档（transport-wide-cc 反馈，selkies --congestion-control L42）——SoulLink 缺这一环（现在无传输层降档，全靠静态码率）。
5. 输入：DataChannel（不可靠）或独立 WebSocket + 8ms 合并 + 相对位移 + 断线释放——SoulLink 已达标。
6. 延迟旋钮：jitterBufferTarget 调小 / targetLatency=lowest（WebRTC）；optimizeForLatency:true + prefer-hardware（WebCodecs）——都是 hint，SoulLink WebCodecs 侧已设 optimizeForLatency。

---

## 7. SoulLink 网页端迁移结论 + Pointer Lock 借鉴分析

### 7.1 最小改造路径（按收益/成本排序）

| 优先级 | 改造 | 现状 | 收益 | 成本 |
|---|---|---|---|---|
| P0（先做，非 WebRTC） | 输入通道从 HTTP 挪到 WebSocket（或 DataChannel） | /api/input 走 HTTP，与 /stream 共用 HttpListener 单循环（AgentWebServer.cs:44/115/294） | 消除「输入被推流写阻塞」风险 | 0.5 人日 |
| P1（传输层降档） | 加拥塞降档：WebSocket 队列 RTT 反馈 → 动态降 bitrate（仿 selkies congestion-control L42） | 现在静态码率无降档 | 弱网不再卡死 | 1 人日 |
| P2（可选，公网才需要） | 整链换 WebRTC（媒体 + DataChannel） | 现在 chunked H.264 + WebCodecs | NAT 打洞/TURN/丢包恢复 | 3-5 人日 + 服务端 WebRTC 网关 |

关键结论：SoulLink 现有「WebCodecs 硬解 + H.264 + 按需 IDR + 8ms 输入合并」已经是 selkies WebSocket 路线的同构实现，不必为了「游戏级」一步跳 WebRTC。真差距是 ① 输入/视频共单循环 ② 无传输层降档 ③ 断连 releaseall 触发链路——三件都在现有架构内可修。

### 7.2 Pointer Lock + 全屏 + iframe 架构对 SoulLink 输入的借鉴（重点）

云原神的一手证据（Pointer Lock 相对位移 + 编程式全屏 + 跨源 iframe 渲染）直接回答了 SoulLink 网页端输入「拖动 1/RTT Hz」的绝对坐标风暴问题：

1. 问题本质：SoulLink 现有拖动是「绝对坐标 move 流」（web-index.html 里 sendMoveThrottled 发绝对 nx/ny）。绝对坐标每次 move 都要一个往返才能落点，高频拖动时要么打爆请求队列、要么被 1/RTT 限制拖成卡顿。

2. 云原神/月光同款解法：Pointer Lock（requestPointerLock）+ 相对位移（mousemove 的 movementX/movementY）。指针锁定时，浏览器把鼠标锁在画布中心、只吐相对位移增量，客户端把增量累加合并后发「相对位移」指令，服务端按增量注入（对应 SoulLink 已实现的 moverel）。这样：
   - 输入从「绝对坐标（每次定位都要 RTT 确认）」变成「相对位移（可 8ms 合并批量发，一次发多帧增量）」，彻底摆脱 1/RTT 的绝对坐标风暴。
   - 与 selkies --raw-pointer-motion 默认 true（pointer lock 取未加速原始位移）完全同构；与 Moonlight 相对模式（movementX/Y 累加）完全同构。

3. 全屏：进入 Pointer Lock 前通常先编程式全屏（requestFullscreen），因为 pointer lock 在非全屏下鼠标移动范围受限。云原神报「enter fullscreen without user gesture」证明它正是「用户点击 → 全屏 → 锁指针」这条链。

4. iframe 渲染隔离：游戏画面跑跨源 iframe，主文档只做控制面板/输入捕获层。好处：iframe 内全屏/锁指针不影响主站其他 UI，权限边界清晰。对 SoulLink 的启示：若将来做「游戏级全屏 + 指针锁定」，可以把 stream 页面放进独立 iframe，输入捕获（pointer lock）在 iframe 内完成，主文档不被锁。

5. 对 SoulLink 输入改造的最小动作（已在现有 moverel 基础上，无需换 WebRTC）：
   - 给网页 stream 区加 requestPointerLock + requestFullscreen 入口（用户手势触发）。
   - pointerlock 锁定时，pointermove 的 movementX/movementY 直接进现有 lookDelta/lookAcc 8ms 合并 → moverel 相对位移（web-index.html:1371-1394 已具备合并+发送，只需把事件源从 touch 的 clientX/Y 差换成 pointerlock 的 movementX/Y）。
   - 退出 pointer lock（Esc/pointerlockerror/断线）时 flush 残留位移 + 触发服务端 releaseall。
   - 这一步直接消灭「绝对坐标 move 风暴」，是「触控/拖动流畅」的正解，成本 <0.5 人日，且与云原神/月光/ selkies 三者同构。

### 7.3 给用户的一手抓包清单（向日葵/云原神配合）

要什么 + 怎么抓（用户有向日葵会员 + 云原神登录）：
1. chrome://webrtc-internals（或 edge://webrtc-internals）：开网页远程桌面运行中 → Create Dump 下载全量 JSON + 页面底部 WebRTC traces dump 文件。判定：有 RTCPeerConnection 条目=WebRTC；无=非 WebRTC（MSE 或私有）。
2. Console：document.querySelector("video") 看 srcObject 是否为 MediaStream（=WebRTC 媒体），还是 src（=MSE/直接 URL）。
3. Network 面板：筛选 WS / RTC / ice / stun / turn 连接列表截图。
4. Elements：video 元素 src/srcObject/codecs 属性截图。
5. Performance/统计：如果产品自带「7ms」类角标，截图标所在位置+旁边文字（区分「网络延迟」还是「总延迟」）。

---

## 8. 未核实清单

1. 向日葵/ToDesk/UU 网页端媒体通道（WebRTC? MSE? 私有?）——全部闭源，官方无文档，标【未核实】，等一手抓包。
2. GFN web 媒体通道——官方未公开，JS 混淆，标【未核实】。
3. xCloud 是否纯 WebRTC——官方未逐字公开，只有 h264-sdp feature flag 佐证，标【业界共识+佐证】。
4. RustDesk web 是否可跑浏览器（WASM 形态的自研协议）——README 只证明 Flutter 目录存在，web 客户端媒体细节【未核实】。
5. Remotely 许可证——README 未标注明确 LICENSE 段落，标【待核】（前序 OSS-REMOTE 记录为 GPL-3.0）。
6. 云原神传输层（WebRTC/MSE）——一手 dump 未到，标【待补 §9.1】。

---

## 9. 证据索引

| 结论 | 证据 |
|---|---|
| selkies 定位云游戏向 | selkies README（"Moonlight/Stadia/GFN in noVNC form factor"） |
| selkies 默认 WebSocket + WebRTC 可选 | selkies README（"streams over plain WebSockets by default, with WebRTC available as an opt-in transport"） |
| selkies 编码器 h264 默认 + CBR 8Mbps | settings.md L34, L38, L40 |
| selkies 无限 GOP/按需 keyframe | settings.md L43 |
| selkies congestion-control（twcc 降档） | settings.md L42 |
| selkies webrtc-pacer（优先级+IDR-aware） | settings.md L56 |
| selkies raw-pointer-motion | settings.md L92 |
| MeshCentral WebRTC | MeshCentral readme + Design Architecture Guide |
| Remotely 静态图像 50KB 分片 + SignalR | Remotely Viewer.cs:223, CasterSocket.cs, ImageUtils.cs（tarball 解包） |
| Remotely WebRTC 已废弃 | migrations 20220827150639_Remove deprecated WebRTC.cs |
| RustDesk 自研协议非 WebRTC | rustdesk README 依赖 libvpx/libyuv + src/server + rendezvous_mediator |
| KasmVNC WebCodecs H264/H265/AV1 + WebRTC UDP | KasmVNC README（New Features + 配置 YAML） |
| noVNC WebSocket + canvas | noVNC README |
| playoutDelayHint 已废弃 | MDN 404 + webrtc-extensions 无此节 |
| jitterBufferTarget 0-4000ms + Baseline 2026-09 | MDN jitterBufferTarget |
| targetLatency lowest/none | w3c webrtc-extensions |
| setParameters maxBitrate 动态降档（无 renegotiation） | MDN RTCRtpSender.setParameters |
| xCloud h264 main sdp flag | play.xbox.com HTML feature flag "usetizenh264mainsdphack" |
| 云原神 PWA + Pointer Lock + 跨源 iframe + 全屏 | 用户 DevTools 截图（2026-11-23 一手证据） |
| 云原神 7ms 角标=网络 RTT | 用户一手截图（2026-11-23）+ CN-CLOUD-GAMING §1 |
| 向日葵 7ms/ToDesk 3ms 营销口径 | sunlogin.oray.com / todesk.com 首页 |
| SoulLink 输入 HTTP 独立端点 + 8ms 合并 | AgentWebServer.cs:294 / web-index.html:1371-1394 |

---

## 9.1 云原神 cg-sdk 逆向分析（Lead 一手分析，2026-11-23 19:10）

> 方法：绕过需要用户操作的抓包，直接拉取云原神 web 加载器公开 JS（ys.mihoyo.com/cloud/ → webstatic.mihoyo.com/common/clgm-web-app/ys/js/）逐段逆向。核心文件 **cg-sdk.891daeae.js（1.23MB，云游戏 SDK 本体）**，已存 `%TEMP%cg-sdk.js`。

### 9.1.1 决定性发现：多引擎传输层 + 运行时降级链

cg-sdk 内置**四条通道**，全部实现于同一 SDK（出现次数：RTCPeerConnection 206 / WebSocket 73 / VideoDecoder 113 / WebTransport 24）：

| 通道 | 证据（cg-sdk 逆向） | 状态 |
|---|---|---|
| **WebTransport（首选）** | `GameChannel.connect(): webTransportChannel.connect() 失败 → logger.info("[gcchan] web transport failed, fallback to ws") → webSocketChannel.connect()`；WebTransport 以 `congestionControl:"low-latency"` 创建 | ★ 用户 webrtc-internals 为空 + Edge 137 已支持 WT：**当前大概率跑 WebTransport 通道** |
| WebSocket（备胎） | 同上降级链 `wtu(不可靠)/wtr(可靠)/ws` 三态 channelType() | 兜底 |
| WebRTC | getCompatibility() 探测 hasWebRtc/hasWebSocket/hasMediaStreamTrack/hasH264Decoder，CompatibilityChecker.check 通过才启用 | 兼容模式路径 |
| KCP | **自研 KcpBuffer/IKCP 协议栈完整实现**（IKCP_OVERHEAD/KcpConnectAck/KcpPing 2s 心跳/pong 5s 超时断连） | 跑在 WT/WS 之上的可靠传输层（ARQ 滑窗），云原神输入/信令的底层 |

**用户 webrtc-internals 空页 = 一手运行时证据**：云原神 web 当前会话没用 WebRTC 媒体面——**商业级云游戏网页端走的正是「WebTransport/WebSocket + WebCodecs」而非 WebRTC**，与 selkies（WebSocket 路线）同型，与 SoulLink 现有技术栈同构。

### 9.1.2 视频管线（与我们逐项对照）

| 环节 | 云原神 cg-sdk 做法 | SoulLink 现状 |
|---|---|---|
| 解码 | VideoDecoder `optimizeForLatency:!0, hardwareAcceleration:"prefer-hardware"`；默认 **HEVC 主选（hev1.1.6.L93.B0）**，DefaultConnectionConfigurations 有 enableHevc/enableWrappedHevc/compatibleMode 三档降级 | 同为 WebCodecs+optimizeForLatency ✅（H264 主选，无 HEVC 档） |
| 渲染 | **canvas drawImage**（"[Renderer] coded resolution" + ctx.drawImage）+ **rVFC 渲染节律分析器**（VideoAnalyzer 用 requestVideoFrameCallback 收 expectedDisplayTime/renderTimestampBuffer） | canvas drawImage ✅；无 rVFC 节律埋点（viewer 报告 W 项已列） |
| 帧注入 | EncodedVideoChunk(timestamp 重基+元数据)；**key 帧时前插 SPS/PPS/VPS 参数集再入队**（"key"===e.type 分支拼 z/q/fe 三个 ArrayBuffer） | 按需 IDR 带 SPS/PPS ✅（NVENC OUTPUT_SPSPPS） |
| 丢帧恢复 | 解码溢出/错误 → `stop()→start()→flushAndRequestKeyFrame()` **主动向服务端要关键帧**（signalMessageSender.requestKeyFrame()，"Please implement requestKeyFrame()" 抽象接口） | ✗ **无前端→服务端关键帧请求通道**（viewer 报告 W7 正是此项；我们靠 10s 看门狗断流重连，云原神靠 1 个控制消息） |
| 码率自适应 | GraphicsMode 三档（KHIGHQUALITY/KTRAFFICSAVING/KEXTREMESAVING）+ KCP/WTT 拥塞感知 | ✗ 无（mature 报告"传输层拥塞降档"缺口） |

### 9.1.3 输入管线

- 输入走 **proto.common.DataChannelMessage**（protobuf 二进制，DataType 枚举：KEYBOARD_DOWN/UP、MOUSE_MOVE、LBUTTON/RBUTTON/MBUTTON down/up、ZDELTA…）经 KCP 可靠通道；
- **mouseMove 节流 4ms**（throttle(...,4)，比我们 8ms 更激进）+ _accumulatedPosition 累积合并；
- 触控为 RtcTouchEventList 批量事件列表（一次消息携带多事件，非逐条 HTTP）；
- **所有输入单通道复用**（GameChannel 一条 WT/WS 连接承载视频+输入+信令，方向/优先级靠 KCP 分包），对照我们「每事件一次 HTTP POST」。

### 9.1.4 结论（升级 §5.3 云原神条目）

1. 云原神 web 传输形态 = **WebTransport（KCP 封装）为主、WS 兜底、WebRTC 仅兼容模式** —— 商业云游戏在浏览器端的现实选择与 SoulLink 现有 WS/WebCodecs 路线**同型**，坚定了 §10 结论 1「不必跳 WebRTC」。
2. 它比我们多出的四件事全是**控制面**：前端主动 requestKeyFrame、渲染节律埋点（rVFC）、GraphicsMode 三档自适应、输入 4ms 节流+protobuf 批量单通道 —— 与五线审计的 W7/W8、拥塞降档、输入 FIFO/Pointer Lock 行动项**完全互证**。
3. 7ms 角标维持「网络 RTT 口径」判定（§5.3），cg-sdk 逆向未见端到端延迟显示逻辑。

### 9.1.5 证据索引（本节新增）

| 结论 | 证据 |
|---|---|
| WT 失败降级 WS | cg-sdk `GameChannel.connect()` 源码（"[gcchan] web transport failed, fallback to ws"） |
| WebTransport low-latency | cg-sdk `new WebTransport(o,{congestionControl:"low-latency"})` |
| KCP 自研栈 | cg-sdk IKCP_* 常量 + proto.msg.KcpConnectAck/Sync/Ping/Pong + 2s ping/5s pong 超时 |
| HEVC 主选+三档降级 | cg-sdk `config:{codec:"hev1.1.6.L93.B0"...}` + DefaultConnectionConfigurations(){enableHevc/compatibleMode/enableWrappedHevc} |
| 前端主动要关键帧 | cg-sdk `requestKeyFrame()` 抽象接口 + 溢出/错误恢复调用链 |
| 输入 4ms 节流 | cg-sdk mouseMove throttle 尾参 `,4)` + MOUSE_MOVE DataType |
| 输入 protobuf 批量 | proto.common.DataChannelMessage / RtcTouchEventList |
| webrtc-internals 空 | 用户 Edge 一手截图（游戏运行中无 PeerConnection） |
| 渲染 canvas+rVFC 分析器 | cg-sdk "[Renderer] coded resolution"/ctx.drawImage + VideoAnalyzer.requestVideoFrameCallback |

> 后续若用户再提供向日葵 dump，另起 §9.2。

---

## 10. 给后续任务的 3 条建议

1. 不要为了「游戏级」一步跳 WebRTC：SoulLink 现有 WebCodecs + H.264 + 按需 IDR 已是 selkies WebSocket 路线同构实现。优先做三件架构内可修的事：输入挪独立 WebSocket、加传输层拥塞降档、修断连 releaseall 触发。
2. 输入流畅的正解是 Pointer Lock 相对位移（云原神/月光/ selkies 三者同构），不是优化绝对坐标 move：给 stream 区加 requestPointerLock+requestFullscreen，把 pointermove 的 movementX/Y 接进现有 8ms 合并 → moverel。成本 <0.5 人日，直接消灭「拖动 1/RTT 绝对坐标风暴」。
3. 等一手 dump 补 §9.1：用户抓的云原神 webrtc-internals dump 是「商业云游戏 web 到底用不用 WebRTC」的最终证据，补上后 §5 从未核实升级为已核实。