# 成熟项目硬事实核查：延迟口径 · 码率上限 · 输入通道 · 浏览器极限

> 日期：2026-11-23
> 作者：mature-researcher（dsh-team team_mwt4bbjy11d7lsz / member m_mwt4bbnt11g753x / task task_mwt4bc8611m8xr3）
> 性质：纯只读调研。未改任何业务代码/脚本/配置；唯一写入文件即本文档。未碰 dist/stable、未用 A1/A4、未重启进程、未发布、未碰 COS、未改 latest.json。
> 方法：web_fetch 直读官方页面 + curl.exe -x http://127.0.0.1:7897 抓取 raw.githubusercontent 源码逐行核对（GPL-3.0 只读参考）。
> 证据分级：【官方文档】=官网/官方手册原文；【官方源码】=GitHub raw 源码逐行（file:line 可回查）；【营销口径】=官网宣传文案（非技术承诺）；【未核实】=查不到，明确标注，不编造。

---

## 0. 一句话结论

用户抱怨里对照的「成熟项目个位数毫秒端到端延迟」「成熟项目 800Mbps 码率」两个数字都不是任何成熟项目的官方技术口径：

1. 「个位数毫秒」不存在于端到端。Moonlight 官方明确把延迟拆成「网络 + 解码 + 队列 + 渲染」四段、且声明「这些数字不能跨客户端比较、还有合成器/显示器/输入设备等测不到的系统延迟」；Parsec 官方给出的工程阈值是编码/解码各 <15ms（60fps）或 <32ms（30fps）、网络 ping <30ms 为好——没有任何一家官方宣称「端到端个位数毫秒」。
2. 「800Mbps」不存在。Moonlight 客户端码率滑块硬上限 150 Mbps（官方 FAQ 解释原因）；Parsec 官方默认 encoder_bitrate=10、并明确「>50 会显著增加延迟」；Sunshine 服务端 max_bitrate 默认 0（跟随客户端请求，被 Moonlight 的 150Mbps 顶死）。行业「800Mbps 级」无出处，判定为误传。
3. 输入通道「触控流畅」的成熟做法是四条：事件合并批处理、独立于视频的控制通道、断线强制释放所有按住状态（up 对账）、以及绝对/相对双模式。SoulLink 已实现其中三条半，唯一真差距是「断线释放」需确认是否覆盖 Web 前端断连场景（服务端已有 releaseall 命令与 ProcessExit 钩子）。

---

## 1. 问题一：延迟口径（「个位数毫秒」的条件与真相）

### 1.1 Moonlight 官方：没有端到端数字，只有四段分解 + 免责声明 【官方文档】

来源：Moonlight FAQ「What do the numbers mean in the on-screen connection statistics?」
URL：https://github.com/moonlight-stream/moonlight-docs/wiki/Frequently-Asked-Questions

原文要点（逐字引用）：
- 延迟被拆成四段：Network latency（=「你的 ping，一包 client->host->client 的时间」）、Decode latency（「一帧解码并准备好渲染的平均时间」）、Frame queue latency（「解码后等渲染的等待时间，通常应 < 一帧间隔，60fps 下即 16ms」）、Render latency（「解码帧渲染到显示器的平均时间」）。
- 关键免责声明原文：
  - "These numbers can't be used to compare to other non-Moonlight clients, since each value may not be measuring the same thing."
  - "There is additional system latency that can't be measured by apps (compositor latency, display latency, input device latency, etc). For the most accurate results, you should always measure using external testing hardware."

结论：Moonlight 官方从未宣称「个位数毫秒端到端」。它只给出「单段指标」，且帧队列段自认「应 <16ms（60fps）」、渲染段含 V-Sync 周期（60Hz 即 16ms）——光这两段就 >30ms，加网络 RTT + 解码 + 合成器，局域网真实端到端是几十毫秒量级，不是个位数。

### 1.2 Parsec 官方：工程阈值是「编码/解码 <15ms + 网络 <30ms」 【官方文档】

来源：Parsec 支持库「Troubleshooting Lag, Latency and Quality Issues」
URL：https://support.parsec.app/hc/en-us/articles/32381352822804-Troubleshooting-Lag-Latency-and-Quality-Issues

原文要点（逐字）：
- "Decode and Encode is the time the device takes to handle a single frame. ... When streaming 60fps, you don't want to go above 15ms. At 30fps the threshold is 32ms."
- "Network is the ping between the guest and host. ... Below 30ms is good, and below 60ms is still fine for most people."

Parsec 官网首页写「near-zero latency」「virtually no latency」「silky smooth」——这是营销文案（【营销口径】），无毫秒数字。官方技术文档给出的可操作阈值是：编码 + 解码各 <15ms（60fps），网络 ping <30ms。三者相加的局域网端到端预算约 45~60ms，绝无「个位数毫秒」。

### 1.3 结论与 SoulLink 对照

| 指标 | 成熟项目官方口径 | SoulLink 现状 | 判定 |
|---|---|---|---|
| 端到端「个位数 ms」 | 不存在（Moonlight/Parsec 均无此宣称） | 用户对照的 453ms 是整链路含抓屏+编码+排队+网络+解码+渲染 | 对比口径错误 |
| 编码段 | Parsec 官方：<15ms（60fps）/ <32ms（30fps） | 前序已做 NVENC SDK 直通 | 硬编后可达标 |
| 网络段 | Parsec：<30ms 为好 | 局域网 RTT≈3ms | 已达标 |
| 解码段 | Parsec：<15ms | 浏览器 WebCodecs 硬解，optimizeForLatency:true 已设 | 基本达标 |

可移植结论：SoulLink 的 453ms 大头的根因不在「网络/解码」，而在抓屏（BitBlt 22~31ms）+ 编码（软编积压）+ 排队——这与已有 MATURE-LATENCY-PLAYBOOK-20261123.md §6 的 P1 判断完全一致。「对标个位数 ms」是伪命题，应把对标目标改为「编码<15ms + 网络<30ms + 队列<1帧」，即端到端 <60ms 局域网。

---

## 2. 问题二：码率（「800Mbps 级」的条件与真相）

### 2.1 Moonlight：客户端滑块硬上限 150 Mbps 【官方文档】

来源：Moonlight FAQ「Why doesn't the bitrate slider go beyond 150 Mbps?」
URL：https://github.com/moonlight-stream/moonlight-docs/wiki/Frequently-Asked-Questions

原文要点（逐字）：
- "There are a few hardware and software limitations at play. The hardware video decoder on the client must have the capability to actually handle the video bitrate you specify. Since almost no content is produced at a bitrate above 100 Mbps, it's unlikely that the hardware decoder and driver could handle a 1 Gbps video stream even if you have a 1 Gbps network connection."
- "Encoders also have limitations ... NVIDIA's GameStream tuning for NVENC is optimized for very fast encode times to ensure low latency. While this ensures frames are delivered ... as fast as possible, it also limits the encoding hardware in terms of how much encoding it can do in the time window."

结论：Moonlight 滑块上限 150 Mbps，且官方解释是刻意为之——更高码率让硬件解码器与 NVENC 实时编码都扛不住，会牺牲低延迟。800 Mbps 纯属不存在。

### 2.2 Sunshine 服务端：max_bitrate 跟随客户端，无更高上限 【官方文档 + 官方源码】

来源（文档）：Sunshine 配置文档「max_bitrate」
URL：https://raw.githubusercontent.com/LizardByte/Sunshine/master/docs/configuration.md （L1502-1523）

原文：
- "The maximum bitrate (in Kbps) that Sunshine will encode the stream at. If set to 0, it will always use the bitrate requested by Moonlight."（默认 0，示例 max_bitrate = 5000 即 5 Mbps）

来源（源码）：Sunshine src/video.cpp 码率与编码参数（已抓取 raw 逐行核对）：
- L2501-2504：bitrate = min(config.bitrate, max_bitrate) * 1000 → rc_max_rate = bitrate; bit_rate = bitrate;（单位换算成 bps，CBR 拉满 max==min，见 L2506-2510）。
- L895/L900：NVENC {"zerolatency", 1} + {"rc", NV_ENC_PARAMS_RC_CBR}。
- L899：{"tune", NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY}。
- L894/L916/L945：{"forced-idr", 1}。
- L893：{"delay", 0}；L896：{"surfaces", 1}。
- L2336：ctx->max_b_frames = 0;（注释「B-frames delay decoder output, so never use them」）。
- L2344/L2347-2350：GOP——不支持按需 IDR 的 Media Foundation 用固定 GOP=120（~2s@60fps）；现代编码器用无限 GOP + 按需 IDR。

编码参数结论（映射到 800Mbps 问题）：成熟项目的「高码率」是局域网内 4K60/4K144 的 CBR 硬编（NVENC zerolatency + CBR + 无 B 帧 + 无限 GOP/按需 IDR + forced-idr），码率量级是 10~150 Mbps，不是 800 Mbps。4K144 4:4:4 全损画质理论上可以冲到几百 Mbps，但没有成熟产品默认或宣称 800Mbps。

### 2.3 Parsec：默认 10，>50 显著伤延迟 【官方文档】

来源：Parsec 支持库「Setting The Maximum Bitrate Of The Stream」
URL：https://support.parsec.app/hc/en-us/articles/32381776828820-Setting-The-Maximum-Bitrate-Of-The-Stream

原文（逐字）：
- "Bitrate determines the maximum amount of bandwidth Parsec will use when streaming video to your client. The default value is 10. Higher bitrates increase latency. Going higher than 50 will definitely have a material impact on your latency. encoder_bitrate=10"

Parsec 官网「up to 4K 4:4:4」「up to 240 FPS (up to 60 FPS at 4K)」是能力上限营销文案，无码率数字。

### 2.4 「800 Mbps」可能的来源与判定

- 国内产品口径里最高的是网易 UU 远程「4K 144 帧、自定义码率最高 500 Mbps」（官网营销口径，已记于 CN-CLOUD-GAMING-20261122.md §1）。800Mbps 无任何官方出处。
- 判定：用户抱怨里的「成熟 800Mbps」是误传/混淆。真实成熟项目码率上限是 150 Mbps（Moonlight 客户端硬顶）/ 50 Mbps（Parsec 实用上限），场景是「局域网 + 硬编 + 高分辨率高帧率」，编码参数是 CBR + 无 B 帧 + 低延迟 tune + 无限 GOP/按需 IDR。

### 2.5 SoulLink 对照

| 项 | 成熟做法 | SoulLink 现状 | 可移植结论 |
|---|---|---|---|
| 码率上限 | 50~150 Mbps（刻意压，换低延迟） | 现有外网码率远低于此 | 不追 800Mbps；追「CBR + 无 B 帧 + 低延迟」参数组合 |
| 编码参数 | zerolatency + CBR + 无 B 帧 + 无限 GOP/按需 IDR | 前序已做 NVENC SDK、MF 侧 ICodecAPI 已手写 | 与 MATURE-LATENCY-PLAYBOOK §2 P1 项一致 |

---

## 3. 问题三：输入通道「触控流畅」怎么做（成熟做法四条）

### 3.1 事件合并批处理（省 CPU、防丢位移、防打爆队列） 【官方源码】

- Moonlight 客户端：同一帧内的多个鼠标移动事件合并累加后一次发送（前序 OSS-REMOTE-RESEARCH-20261122.md §2.2 已核 moonlight-qt mouse.cpp，xrel += event->xrel）。
- Sunshine 服务端：src/input.cpp 有显式批处理机制——input_queue（L330，待处理输入包队列）、terminate_batch（L1874/L1890/L1893，批量终止标记）、scroll 累加器 accumulated_vscroll_delta/accumulated_hscroll_delta（L338-339，滚轮 delta 合并）。
- SoulLink 现状：已实现——docs/web-index.html:1371-1394 lookAcc 8ms 合并 + flushLook（LOOK_HZ_MS=8），:1456-1483 MOVE_MIN_INTERVAL_MS=8 + 单 in-flight latest-wins。这条已达标。

### 3.2 独立于视频流的控制通道（输入不被画面帧率拖累） 【官方源码】

- Sunshine：鼠标/键盘/触摸走独立协议包（NV_REL_MOUSE_MOVE_PACKET L483、NV_ABS_MOUSE_MOVE_PACKET L496、NV_MOUSE_BUTTON_PACKET L958、PNV_SCROLL_PACKET L707），在控制通道处理，不经视频编码（前序 OSS-REMOTE §2.2 已核）。
- SoulLink 现状：Relay/AgentWebServer.cs:294 POST /api/input 独立端点，:273 /stream 独立。但二者共用同一个 HttpListener 单事件循环（AgentWebServer.cs:44/115），理论上输入可能被推流写阻塞——这已是 MATURE-LATENCY-PLAYBOOK §4.3 列出的「P3 仅验证」项，尚未验证。
- 关键新证据（浏览器端）：Parsec 官方明确「If the guest is joining via the browser, you may notice Decode says N/A. The web client does not have access to low-level hardware optimizations or hardware decoding」——浏览器输入/解码都拿不到原生低层优化。

### 3.3 断线强制释放所有按住状态（up 对账 = 用户抱怨「鼠标放开了远端没放开」的根因线索） 【官方源码】

来源：Sunshine src/input.cpp（已抓取 raw 逐行核对）
- L2341-2351 reset_mouse_buttons()：遍历 mouse_press[]，凡按住者逐个 platf::button_mouse(..., true) 释放。
- L2353-2368 reset_keyboard_keys()：遍历 key_press，凡按下者 emit_keyboard_update(..., true, ...) 释放，并注释「otherwise a remapped modifier stays latched after the client disconnects」。
- L2370-2392 reset_gamepads()：neutralize 手柄、LI_TOUCH_EVENT_CANCEL_ALL 取消全部触摸。
- L2394-2403 reset_input_state()：「Reset all pressed input state for a disconnected stream」，三条全调。
- L2405-2414 reset()：task_pool.push(reset_input_state, input) —— 断流时把「释放全部按住状态」作为一项任务派发。
- L963-971：鼠标 button down/up 有状态去重（mouse_press[button] != release 时 return，避免重复注入）。

这是「up 事件对账」的成熟标准答案：服务端维护一份「当前按住状态表」，断线/流结束时不依赖客户端逐个发 up，而是主动把表里所有按住项强制释放。

SoulLink 现状（Relay/RelayAgent.cs 已读）：
- 已实现「按住状态表 + 主动释放」：PressedButtons（L98）、PressedKeys（L108）、ReleaseAllMouseButtons（L296）、ReleaseAllKeys（L317）、releaseall 命令（L811-813）、ProcessExit 钩子（L588）、关停时释放（L624-625、L694-695）。
- 差距/待查：① Web 前端断连时是否触发服务端 releaseall 尚无证据（用户 18:10 反馈「鼠标放开了远端没放开」指向此）；② 前端 pointercancel 已补 up（web-index.html:1604-1627），但网络断线/页面关闭场景 pointercancel 不一定触发。

### 3.4 绝对/相对双模式（触屏「点哪指哪」 vs 指针「相对转视角」） 【官方源码 + 已有结论】

- Moonlight 客户端相对/绝对双模式（前序 OSS-REMOTE §2.1 已核 moonlight-qt mouse.cpp:104-178）。
- SoulLink 现状：已实现双模式（web-index.html:1323 注释「相对=月光语义」、mouseMode === 'relative'、drag 绝对 + look 相对）。

---

## 4. 问题四：浏览器端极限（WebCodecs / WebTransport）

### 4.1 W3C WebCodecs VideoDecoder 低延迟相关参数 【官方文档】

来源：W3C WebCodecs 规范
URL：https://w3c.github.io/webcodecs/

- §7.6 VideoDecoderConfig.optimizeForLatency（boolean，默认 false）：
  - "Hint that the selected decoder SHOULD be configured to minimize the number of EncodedVideoChunks that have to be decoded before a VideoFrame is output. NOTE: ... some codec bitstreams require a minimum number of inputs before any output can be produced."
- §7.9 Hardware Acceleration（枚举 no-preference / prefer-hardware / prefer-software，默认 no-preference）：
  - "prefer-hardware and prefer-software are hints. While User Agent SHOULD respect these values when possible, User Agent may ignore these values in some or all circumstances..."
  - "Hardware acceleration does not simply imply faster encoding/decoding. Hardware acceleration often has higher startup latency but more consistent throughput performance."
  - "For lower resolution content, the overhead added by hardware acceleration can yield decreased performance..."
- §7.11 Latency Mode（枚举 quality / realtime）：
  - realtime: "User Agent SHOULD optimize for low latency. ... User Agent MAY sacrifice quality to improve latency. User Agent MAY drop frames to achieve the target bitrate and/or framerate. framerate SHOULD be used as a target deadline for emitting encoded chunks."

关键结论：WebCodecs 的 optimizeForLatency / hardwareAcceleration 都是「hint（提示）」而非硬保证——浏览器「可能忽略」。这与 Parsec 官方「web client 拿不到硬件解码」的说法互相印证。浏览器端解码延迟上限由 UA 决定，作者无法强制。

### 4.2 WebTransport / QUIC 在浏览器串流的现状与限制 【官方文档】

来源：MDN WebTransport API
URL：https://developer.mozilla.org/en-US/docs/Web/API/WebTransport

- WebTransport 基于 HTTP/3（QUIC），提供「可靠（stream）+ 不可靠（datagram，UDP 语义）双通道」：
  - "connect to an HTTP/3 server, initiate reliable and unreliable transport in either or both directions"
  - "congestionControl: ... the application preference for either high throughput or low-latency when sending data."
  - "reliability: ... whether the connection supports reliable transports only, or whether it also supports unreliable transports (such as UDP)."
- 兼容性：MDN 标注「Baseline 2026 · Newly available —— Since March 2026, this feature works across the latest devices and browser versions」。即：WebTransport 直到 2026-03 才跨 Chrome/Firefox/Safari 全绿；此前多年仅 Chromium 系支持。
- 配套限制：Moonlight FAQ「Is there a Moonlight web client?」原文：
  - "No ... The GameStream protocol requires us to use raw TCP and UDP sockets which is not currently supported in web browsers. There is a proposed Direct Sockets web standard ... that could make it possible ... Optimizations like changing the display refresh rate to match the stream frame rate ... and using full-screen exclusive mode for lower latency are not currently available to web apps."

结论：浏览器端「原生级低延迟串流」目前被三件事卡住：① 裸 TCP/UDP socket 不可用（只有 WebTransport/QUIC datagram 这个替代，且刚 2026 年才全绿）；② WebCodecs 低延迟/硬解都是 hint 非保证；③ 拿不到「改刷新率匹配帧率」「独占全屏」等原生低延迟手段。Moonlight 官方因此明确不做纯 Web 客户端。

### 4.3 浏览器端对 SoulLink 的结论

SoulLink 走 Web 前端（docs/web-index.html）是架构既定，可移植的边界是：
1. optimizeForLatency: true（已设，web-index.html:1070）+ hardwareAcceleration: 'prefer-hardware'（若未设应补）是能做的全部，且都是 hint。
2. WebTransport datagram（不可靠通道）可作为输入控制通道的升级候选（替代「输入与视频共用 HTTP」），但收益是「避免输入被视频写阻塞」，不是「降到个位数 ms」；且需服务端支持 HTTP/3（当前 HttpListener 不支持）。
3. 浏览器端解码/渲染延迟比原生客户端高、且不可控——这是 Web 架构的硬边界，不能对标原生 Moonlight/Parsec。

---

## 5. 汇总事实表

| 主题 | 成熟项目做法（来源 URL） | 条件/适用范围 | SoulLink 现状 | 可移植性结论 |
|---|---|---|---|---|
| 端到端「个位数 ms」 | 无任何官方此宣称。Moonlight 四段分解+免责声明（FAQ statistics）；Parsec 编/解<15ms@60fps、网络<30ms（Troubleshooting article） | 局域网+硬编+原生客户端 | 453ms 为整链路口径错误 | 伪命题；改对标「<60ms 局域网」 |
| 编码段延迟 | Parsec：encode <15ms@60fps / <32ms@30fps | 硬件编码器 | 已 NVENC SDK 直通 | 硬编后可达标（P1 已做） |
| 800 Mbps 码率 | 不存在。Moonlight 滑块上限 150Mbps（FAQ）；Parsec 默认10、>50伤延迟（bitrate article）；Sunshine max_bitrate 跟随客户端 | 局域网高分辨率高帧率 | 远低于此 | 不追 800；追 CBR+无B帧+低延迟参数 |
| 编码参数组合 | Sunshine video.cpp: zerolatency+CBR+tune ULTRA_LOW_LATENCY+forced-idr+无B帧+无限GOP/按需IDR（L893-900/L2336/L2344-2350） | NVENC/硬编 | MF/NVENC 参数已接（前序） | 与 Playbook P1 一致 |
| 事件批量合并 | Moonlight 同帧位移累加；Sunshine input_queue+scroll 累加（input.cpp L330/L338） | 高频输入 | web-index.html 8ms 合并+单in-flight | 已达标 |
| 输入与视频分离 | Sunshine 独立 NV_*_PACKET 控制通道（input.cpp L483/496/958/707） | 所有 | /api/input 独立端点，但共用 HttpListener 单循环 | 需验证是否被推流阻塞（P3） |
| 断线释放 up 对账 | Sunshine reset_input_state 遍历释放 mouse/keyboard/gamepad（input.cpp L2344-2403） | 所有断流场景 | RelayAgent 有 PressedButtons/Keys + releaseall + ProcessExit 钩子（L98/108/296/317/588/811） | 服务端机制已具备；待查「Web 断连是否触发 releaseall」 |
| 绝对/相对双模式 | Moonlight mouse.cpp 双模式 | 触屏 vs 指针 | web-index.html mouseMode relative + drag/look | 已达标 |
| WebCodecs 低延迟 | W3C §7.6/7.9/7.11：optimizeForLatency/hardwareAcceleration/LatencyMode=realtime | 均为 hint，UA 可忽略 | optimizeForLatency:true 已设 | 硬边界：浏览器解码延迟不可强制 |
| WebTransport/QUIC | MDN WebTransport：HTTP/3 可靠+不可靠双通道 | Baseline 2026-03 才全绿（此前仅 Chromium） | 未用（HttpListener 不支持 HTTP/3） | 输入控制通道升级候选，收益有限 |

---

## 6. 未核实清单（明确标注，不编造）

1. Parsec 具体毫秒数：官网只有「near-zero / virtually no latency」营销文案，未公开任何端到端 ms 数字（闭源 BUD 协议）。
2. 「800 Mbps」的出处：未核实到任何官方来源。已查 Moonlight/Sunshine/Parsec/国内云游戏（UU 远程最高 500Mbps 营销口径），均无 800。判定为误传。
3. SoulLink Web 前端断连是否触发服务端 releaseall：releaseall 命令存在（RelayAgent.cs:811），但「浏览器页面关闭/网络断线 → 服务端强制释放」的触发链路未在本任务核实，需查 AgentWebServer.cs 断连处理与前端 pagehide/beforeunload/close 事件（待验证）。
4. WebTransport 服务端（.NET）支持现状：未核实 .NET HttpListener/ASP.NET 的 HTTP/3 支持矩阵（超出本任务范围）。

---

## 7. 证据索引

| 结论 | 证据 |
|---|---|
| Moonlight 无端到端 ms、四段分解、免责声明 | Moonlight FAQ statistics 段（wiki，无行号） |
| Moonlight 码率滑块 150Mbps 上限及原因 | Moonlight FAQ bitrate 段 |
| Moonlight 无纯 Web 客户端（socket 限制） | Moonlight FAQ web client 段 |
| Sunshine NVENC zerolatency/CBR/tune/forced-idr/surfaces=1/delay=0 | src/video.cpp:893-900, 916-922, 945-951 |
| Sunshine 无 B 帧 | src/video.cpp:2335-2336 |
| Sunshine 无限 GOP / MF 固定 120 | src/video.cpp:2342-2351 |
| Sunshine CBR max==min | src/video.cpp:2501-2510 |
| Sunshine max_bitrate 跟随客户端、默认0 | docs/configuration.md:1502-1520 |
| Sunshine 断线释放全部输入 | src/input.cpp:2344-2403 |
| Sunshine 鼠标 button 状态去重 | src/input.cpp:963-971 |
| Sunshine 输入独立协议包 | src/input.cpp:483, 496, 958, 707 |
| Parsec 编码/解码阈值 15ms/32ms | Parsec Troubleshooting article |
| Parsec 网络 <30ms 为好 | 同上 |
| Parsec 码率默认10、>50伤延迟 | Parsec max bitrate article |
| Parsec 浏览器客户端无硬解（Decode N/A） | Parsec Troubleshooting article |
| Parsec 4K 4:4:4 / 240FPS / 60FPS@4K | parsec.app 首页（营销） |
| WebCodecs optimizeForLatency/hardwareAcceleration/LatencyMode | W3C WebCodecs §7.6 / §7.9 / §7.11 |
| WebTransport HTTP/3 双通道 + Baseline 2026 | MDN WebTransport |
| SoulLink 输入合并 8ms | docs/web-index.html:1371-1394, 1456-1483 |
| SoulLink 断连释放机制 | Relay/RelayAgent.cs:98, 108, 296, 317, 588, 811-813 |
| SoulLink 输入/视频端点 | Relay/AgentWebServer.cs:273, 294 |
| SoulLink 前端 pointercancel 补 up | docs/web-index.html:1604-1627 |

---

## 8. 给后续任务的 3 条最具体建议

1. 立刻纠正对标口径：不要再拿「个位数 ms / 800Mbps」当目标（都是不存在的官方口径）。正确目标：编码+解码各 <15ms、网络 <30ms、队列 <1 帧 → 局域网端到端 <60ms。这个目标 SoulLink 硬编后已接近，继续做 MATURE-LATENCY-PLAYBOOK §6 的 P1 即可。
2. 优先排查「断连不释放」（用户 18:10 「鼠标放开了远端没放开」）：服务端 releaseall 已具备，需验证 Web 前端断线/关页时是否触发（AgentWebServer.cs 断连处理 + 前端 pagehide/close 钩子），这是「触控不流畅」最可能的输入侧根因。
3. 输入通道阻塞验证（P3 转 P2 候选）：/api/input 与 /stream 共用 HttpListener 单循环，若被推流 WriteAsync/FlushAsync 阻塞会拖慢输入响应。要么验证不阻塞，要么把输入挪独立监听——这是「触控流畅」的传输侧根因候选。
