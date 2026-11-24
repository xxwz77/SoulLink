# 浏览器 UDP 可行性权威研究：WebTransport / WebRTC vs 当前 HTTP chunked

> 日期：2026-11-24
> 作者：proto-webudp（dsh-team `team_mwtjn0d77v8gtr` / member `m_mwu0xpde4m88k0` / task `task_mwu0xyhw4n93fb`）
> 性质：**纯调研**。未改任何产品代码/脚本/配置，未碰 `dist/stable/SoulLink-Stable.exe`，未用 A1/A4，未改注册表。唯一写入为本研究文档。
>
> **证据分级（全文通用）**：
> - **【官方文档】**＝MDN / W3C 规范 / RFC / Can I Use 原文，附 URL，可回查。
> - **【实测】**＝本仓库 `docs/research/` 前序任务的实测数字，附出处。
> - **【代码事实】**＝当前源码可证（file:line）。
> - **【推断】**＝有依据的推导，未实测；推导过程写明。
> - **【查不到】**＝闭源或一手来源无法访问，明确标注，不编造。

---

## 0. 一句话结论

**浏览器当然能用 UDP，但三条 UDP 路线（WebRTC DataChannel / WebTransport datagram / WebTransport 可靠流）的「净收益」取决于丢包环境；在当前实测传输只占 0.8~5ms、且服务端是裸 HTTP（无 HTTPS+有效证书）的前提下，换 UDP 属于「给一个还没发生的病吃药」，收益接近 0，且代价是一次协议层重构 + HTTPS/证书基建。建议：不换。先把「帧边界语义 + 客户端按需 IDR 回传 + WebSocket 二进制分帧」这三件在 TCP 上就能做的事做了；只有进入公网有损部署时，才把 WebTransport 作为首选升级路径。**

---

## 1. 浏览器到底能不能用 UDP？逐条列出可用 API 与协议栈

**直接回答：能，但浏览器不允许裸 `UDP Socket`（除少数试验性 API），只能通过三大「合规封装」间接用 UDP。**

| API | 底层协议栈 | 是否走 UDP | 可靠性模式 | 可用的安全上下文要求 | 证据 |
|---|---|---|---|---|---|
| **fetch / ReadableStream**（SoulLink 现状） | HTTP/1.1 over **TCP**（`HttpListener` + `SendChunked`） | ❌ TCP | 可靠（内核重传） | 无（HTTP 即可） | 【代码事实】`Relay/AgentWebServer.cs:85`（`http://+`）、`:449`（`SendChunked`） |
| **WebSocket** | HTTP/1.1 Upgrade over **TCP** | ❌ TCP | 可靠 + 消息边界 | 无（HTTP 即可，`ws://`） | 【官方文档】MDN WebSockets API |
| **WebTransport** | HTTP/3 over **QUIC** over **UDP** | ✅ UDP | 双模式：可靠 **streams** + 不可靠 **datagrams** | **必须 HTTPS + 有效 TLS 证书** | 【官方文档】MDN WebTransport API（下文 §2） |
| **WebRTC DataChannel** | **SCTP** over **DTLS** over **UDP**（或 ICE-TCP 回退） | ✅ UDP（优先） | 双模式：`ordered` 可靠 / `ordered:false` + `maxRetransmits`/`maxPacketLifeTime` 半可靠 | 需要信令 + ICE，**页面须 HTTPS**（`getUserMedia`/部分 API 要求；仅 DataChannel 在 `http://localhost` 下可用） | 【官方文档】MDN RTCDataChannel / createDataChannel（下文 §3） |
| **裸 UDP Socket** | 直接 UDP | ✅ UDP | 无 | — | **浏览器不可用**（`UDPSocket` 已废弃，Web Serial/`quic-transport` 非标准）【官方文档+查不到】 |

**关键结论**：SoulLink 的现状是 `fetch + chunked HTTP/1.1`（纯 TCP，`http://` 明文），**没有任何一个 UDP 路线能在 `http://` 明文上直接点亮**——WebTransport 强制 HTTPS+有效证书，WebRTC 需要信令交换 + ICE（本质是要么装证书要么绕道）。

---

## 2. WebTransport 现状（最相关的候选）

### 2.1 是什么 / 协议栈

- WebTransport 是 WebSockets 的现代升级，基于 **HTTP/3 + QUIC（QUIC 本身基于 UDP）**。【官方文档】MDN WebTransport API。
- 支持：多路流、单向流、乱序投递；**可靠传输走 streams（流），不可靠传输走 datagrams（UDP 式数据报）**。【官方文档】MDN WebTransport/datagrams。
- datagrams 语义明确：*"unreliable ... not guaranteed, nor is arrival in a specific order ... provides very fast delivery"*——这正是 Moonlight 视频 RTP 想要的「丢了就丢、不重传」。【官方文档】MDN WebTransport/datagrams。

### 2.2 浏览器支持率

- **Can I Use：全局 92.37%**（截至 2026-10 StatCounter）。
- Chrome 97+、Edge 98+、Firefox 114+、Safari 26.4+、Opera 83+ 支持；**Chrome for Android 157+、Safari on iOS 26.4+** 支持。【官方文档】caniuse.com/webtransport。
- MDN 标注 **"Baseline 2026 — Newly available"**（2026-03 才跨浏览器全线可用）——即**移动端/旧浏览器仍有相当比例不支持**。【官方文档】MDN WebTransport API（"Since March 2026 ... might not work in older devices or browsers"）。
- **对我们最关键的坑**：用户若要连 SoulLink 的浏览器不一定是 Chrome/Edge 最新版，WebTransport 的「Baseline 2026」意味着**旧设备、旧 Safari、部分国内套壳浏览器（QQ/UC）不支持**（caniuse：QQ Browser ❌，Opera Mini ❌）。

### 2.3 是否基于 QUIC/UDP

是。HTTP/3 标准 2022 定稿，基于 QUIC（2021 定稿，RFC 9000），QUIC 实现于 UDP 之上。【官方文档】MDN Glossary/QUIC + RFC 9000。

### 2.4 延迟特性

- **免 TCP 队头阻塞（HOL）**：QUIC 按流独立检测丢包/重传，**某条流的丢包只阻塞那条流**，不像 HTTP/2（单 TCP 上多路复用，一个丢包拖住所有）。【官方文档】MDN Glossary/QUIC（*"packet loss only blocks the particular stream whose packets were lost"*）。
- **建连更少往返**：QUIC 把 TLS 握手合并进初次握手，比 TCP+TLS 少一次往返。【官方文档】MDN WebTransport API。
- **可选拥塞控制调优**：`congestionControl: "low-latency"`（throughput / low-latency / default 三选一），给浏览器一个「低延迟优先」的 hint。【官方文档】MDN WebTransport() constructor。
- **网络切换不掉线**：QUIC 用 connection ID 而非 IP 标识，Wi-Fi↔蜂窝切换不断链。【官方文档】MDN WebTransport API。

### 2.5 是否必须 HTTPS + 有效 TLS 证书

**必须。这是当前 SoulLink 的硬门槛。**

- `new WebTransport(url)` 的 `url` **scheme 必须是 HTTPS，且端口必须显式写出**。【官方文档】MDN WebTransport() constructor。
- 特性本身 **"Secure context: available only in secure contexts (HTTPS)"**。【官方文档】MDN WebTransport API。
- 服务端必须实现 HTTP/3 服务器 + 提供**浏览器信任的 TLS 证书和私钥**（MDN 示例直接要求 `certificate.pem` + `private-key.pem`）。【官方文档】MDN WebTransport API（server implementation）。
- 可选的 `serverCertificateHashes` 允许用证书指纹而非 Web PKI 校验——但仍需 HTTPS，只是绕开「买/配公共信任证书」的 PKI 校验步骤。【官方文档】MDN WebTransport() constructor。

**对 SoulLink 的直接影响**：SoulLink agent 当前是 `HttpListener` + `http://+:<port>/`（【代码事实】`Relay/AgentWebServer.cs:85`），**是明文 HTTP，没有任何 TLS 栈**。要上 WebTransport，必须先给 agent 加 HTTPS（证书 + HTTP/3/QUIC 服务器实现），这本身就是一次安全/部署基建工程，与「局域网快速直连、免配证书」的现有体验直接冲突（证书在 LAN IP 上没法被浏览器默认信任）。

### 2.6 部署复杂度

- 需要服务端 HTTP/3/QUIC 实现。.NET 侧 `HttpListener` **不支持 HTTP/3/WebTransport**（它是 HTTP/1.1 级别的老 API），需要引入第三方 QUIC 栈（如 .NET `MsQuic`、或独立 HTTP/3 服务），或把视频面挪到独立进程/服务。【代码事实】`HttpListener` 仅绑定 `http://` 前缀。
- MDN 列出的服务端库均为非 .NET 生态为主：Go `webtransport-go`、Python `aioquic`、Rust `wtransport`、Node `@fails-components/webtransport`、Deno 内建（unstable）。【官方文档】MDN WebTransport API。
- 结论：**部署复杂度高**——不是「改个前缀」的事，而是要选型 QUIC 库、配证书、改 agent 架构。

---

## 3. WebRTC DataChannel 现状

| 项 | 值 | 证据 |
|---|---|---|
| 支持率 | **Baseline Widely available**，2020-01 起全线可用，成熟度远高于 WebTransport | 【官方文档】MDN RTCDataChannel |
| 底层 | **SCTP over DTLS over UDP**（默认），可 ICE-TCP 回退（回退即失去 UDP 优势） | 【官方文档】MDN RTCSctpTransport |
| 可靠模式 | `ordered:true`（默认，可靠有序）；`ordered:false` + `maxRetransmits` / `maxPacketLifeTime` 开启**半可靠/不可靠** | 【官方文档】MDN createDataChannel |
| 反压 | `bufferedAmount` / `bufferedAmountLowThreshold` 可做发送侧背压 | 【官方文档】MDN RTCDataChannel |
| 部署成本 | **最高**：需要 ICE 信令交换（offer/answer + STUN/TURN）、SDP 协商、NAT 打洞；原生 DataChannel 是 P2P，SoulLink 是「浏览器→agent 直连」，仍需一整套信令 | 【官方文档】MDN WebRTC API |

**要点**：
1. WebRTC 成熟、UDP 真可用，但它是**为 P2P 设计的**（ICE/STUN/TURN/SDP），对 SoulLink「浏览器 → 局域网内固定 agent」这种**服务端直连**场景是「杀鸡用牛刀」：要自己维护信令通道 + ICE candidate 交换，还要处理 NAT（本场景可省，但代码量仍在）。
2. 云游戏标杆（云原神）用 WebRTC 媒体面，靠 RTP 的 NACK/PLI/jitterBuffer 换稳定，代价是 **~74ms/帧 jitter buffer**（【实测】本仓库 `WEB-RUNTIME-PROBE-20261123.md:44`）——这与 SoulLink「WebCodecs 直推、零 jitter buffer」的低延迟取舍相反。
3. WebRTC 的 DataChannel 承载 H.264 NAL 是「非主流用法」：标准做法是走 `RTCPeerConnection` 的**媒体轨道**（`addTrack`/`RTCRtpSender`），那是把整条视频管线交给浏览器 RTP 栈，工程量和当前 `WebCodecs + fetch` 形态是两条完全不同的实现路径。

---

## 4. 量化对比：当前 HTTP chunked vs WebTransport vs WebRTC

> 比较口径：1080p60 H.264，单 viewer，逐帧到达。传输层「帧到达延迟/抖动/HOL」三项。

| 维度 | HTTP chunked（现状，TCP） | WebTransport datagram（UDP 不可靠） | WebTransport stream（UDP 可靠） | WebRTC DataChannel（SCTP/UDP） |
|---|---|---|---|---|
| 帧到达延迟（LAN，丢包 0） | 0.8ms p95 4.6ms【实测】`LATENCY-BENCHMARK-20261123.md:33` | ≈同量级（无重传税） | ≈同量级（可靠流仍需按流重传） | ≈同量级 + ICE 建连一次性开销 |
| 抖动（LAN，丢包 0） | 亚 ms~数 ms（fetch 拼包税） | 最低（数据报不保序不重传） | 低（按流重传隔离） | 低 |
| **队头阻塞** | **有**（单 TCP 流，丢包拖整条流） | **无**（数据报无重传） | **流间隔离**（丢包只堵本流） | **无**（SCTP 多流 + 可选乱序） |
| 丢包>0 时帧到达延迟 | 丢包触发 TCP 重传 + 慢启动，**延迟尖峰 = RTT 级别**，且阻塞后续所有帧 | 丢的帧直接不要（需应用层 FEC/IDR 重同步），延迟不升 | 按流重传该流，其余流不受影响 | 乱序模式丢帧不重传，延迟不升 |
| 建连成本 | fetch 复用 keep-alive，一次连接多帧 | 一次 QUIC 握手（0-RTT 可达） | 同左 | ICE 信令往返（最重） |
| 帧边界语义 | **无**（字节流，chunk 边界≠帧边界） | 数据报天然边界（但需自己切帧） | 流无消息边界（需长度前缀） | 消息边界（`send` 一条 = 一条消息） |

### 4.1 队头阻塞在 60fps + LAN 下实际造成多少延迟？

**当前：≈0。** 回环/优质 LAN 丢包率 ≈0（【实测】`WEB-VIEWER-LATENCY-GAP-20261123.md:104` "HOL 不咬人：回环丢包≈0"）。TCP 的 HOL 只有在「丢包 + 需要重传」时才发生；丢包为 0 时 TCP 和 UDP 的帧到达延迟差在亚 ms 级。

### 4.2 丢包率 0 时 UDP 优势还剩多少？

**只剩「帧边界语义」和「不重传」两个结构优势，延迟数字优势≈0。** 而帧边界语义**在 TCP 上也能实现**（WebSocket 二进制消息、或 HTTP chunked 加长度前缀），不需要换 UDP。

### 4.3 什么条件下 UDP 才有明显收益？

【推断】量化模型（基于 TCP 重传机制，非本仓库实测，推导过程写明）：

- **丢包率 p > 0.1~0.5% 且 RTT > 10~20ms** 时，TCP 的 HOL + 重传开始产生「帧到达延迟尖峰」：一次丢包需要 RTT 级等待（LAN 0.1ms 时尖峰 <1ms；公网 30~80ms RTT 时尖峰 = 30~80ms 起步，且阻塞后续帧）。
- **典型分界**：
  - **LAN / 回环（RTT<5ms，丢包≈0）**：UDP 收益 ≈ 0，不值得。
  - **公网 / 有损（RTT 30~80ms，丢包 0.5~2%）**：TCP 逐帧流会周期性「卡一下」；此时 WebTransport datagram（丢旧保新 + 应用层 FEC/IDR）才有**量级可感的收益**（省掉每次丢包 RTT 级别的重传尖峰）。
- **结论（决策树）**：`RTT > 20ms 且丢包 > 0.5%` → 换 UDP 有明确收益；否则收益趋近 0。

---

## 5. 关键：实测传输只占 0.8~5ms，换 UDP 到底能带来多少收益？值不值得？

### 5.1 收益量化

端到端 85~130ms（【实测】`E2E-LATENCY-BASELINE-20261124.md:39`）的构成里：

| 段 | 值 | 出处 |
|---|---|---|
| 网络传输（LAN/回环） | **0.8~5ms** | 【实测】`E2E-LATENCY-BASELINE-20261124.md:36` |
| 服务端 capture + convert + encode | 8.4 + 7.8 + 4.6 ≈ 20.8ms | 同文档 §2 |
| 浏览器解码队列 + 解码 + 上屏 | 20~40ms | 同文档 §2 |

**换 UDP 能省的，只有 0.8~5ms 里「TCP 重传/HOL」那一小部分——而丢包≈0 时这一部分就是 0。** 换句话说：

- **LAN/回环场景**：换 UDP 的净收益 **≈ 0~2ms**（省的可能只是 HTTP 头/建连的一点点，且 WebTransport/WebRTC 还要先付 HTTPS 证书 + QUIC/ICE 建连成本，可能反而变慢）。
- **对比真正的瓶颈**：服务端 convertMs 7.8ms（跨 GPU CPU 转换）、浏览器 20~40ms（解码队列 + vsync）——**任何一个都比「传输层换 UDP」大一个数量级**。

### 5.2 值不值得？

**当前不值得。** 理由：
1. **收益≈0**（§5.1），且要付 HTTPS 证书 + QUIC/HTTP3 服务器 + 客户端改写的全套成本。
2. **要解决的真正问题不是传输层**：SoulLink 与 Moonlight 的结构差距在「缺帧边界语义 + 缺客户端按需 IDR 回传 + fetch 无消息分帧」（【代码事实+推断】`LOW-LATENCY-DEEP-DIVE-20261124.md §5.4 / §7`），**这些在 TCP 上就能做**。
3. **WebTransport 支持率风险**：Baseline 2026，旧 Safari/旧 Android/QQ/UC 不支持，会砍掉一部分现有可访问性。

---

## 6. 如果要换，迁移路径与工作量评估

> 仅作评估，不构成「现在就换」的建议。

### 6.1 推荐路径（若进入公网有损部署）：**WebTransport > WebRTC**

- 选 WebTransport 而非 WebRTC 的理由：它是「客户端↔服务器」直连形态（契合 SoulLink 的 agent 直连架构），无需 ICE/STUN/TURN/SDP 信令；WebRTC 的 P2P 信令是多余负担。【推断】
- datagram 模式承载视频帧（丢旧保新 + 应用层 FEC/按需 IDR），可靠 stream 承载控制/输入通道。

### 6.2 工作量评估（粗粒度，【推断】）

| 步骤 | 内容 | 量级 |
|---|---|---|
| 1 | agent 加 HTTPS + 有效 TLS 证书（LAN 自签证书需用户信任，体验冲突需产品决策） | 中 |
| 2 | 引入 QUIC/HTTP3 服务器栈（`HttpListener` 不支持，需 .NET MsQuic 或独立服务） | 大 |
| 3 | 服务端视频面改 datagram 发送 + 帧分片 + 应用层 FEC（或先不做 FEC，用按需 IDR 兜底） | 大 |
| 4 | 浏览器端 `fetch/getReader` 改 `WebTransport` datagram 收包 + 重装帧 + 喂 WebCodecs | 中 |
| 5 | 输入/控制通道改可靠 stream（替代每事件 HTTP POST） | 中 |
| 6 | 兼容降级：不支持 WebTransport 的浏览器回退 HTTP chunked | 中 |

**总评**：这是一次**协议层重构**（视频面 + 控制面 + 安全面），不是增量优化。相比之下，§7 的「TCP 上三件事」是增量优化，收益/成本比高得多。

---

## 7. 明确建议

### 结论：**不换 UDP。** 分场景：

| 场景 | 建议 | 理由 |
|---|---|---|
| **当前（LAN/回环直连，HTTP 明文）** | **不换** | 传输只 0.8~5ms，丢包≈0，UDP 收益≈0；WebTransport/WebRTC 反而要先付 HTTPS 证书 + QUIC/ICE 成本 |
| **公网有损部署（RTT>20ms 且丢包>0.5%）** | **条件换，首选 WebTransport datagram** | TCP 重传 HOL 产生 RTT 级延迟尖峰，WebTransport datagram 才有量级可感收益 |
| **想「在 TCP 上先吃帧边界红利」** | **先做（推荐，与 UDP 无关）** | 见下方三件事 |

### 7.1 换 UDP 之前，先在 TCP 上做这三件（收益远高于换协议，成本低）

1. **帧边界语义**：`/stream` 每帧一个 length-prefix 二进制消息（WebSocket 分帧或 SSE event），消除「network chunk 边界≠帧边界」的拼包税。【参照】Guacamole sync 指令、Moonlight FLAG_SOF/EOF（`LOW-LATENCY-DEEP-DIVE-20261124.md §7 #1`）。
2. **客户端按需 IDR 回传**：decoder reset / 花屏时发控制消息让服务端 `ForceKeyFrame`。【代码事实】`IVideoEncoder.cs:41` 已留 `RequestKeyFrame` 入口。
3. **输入通道改 WebSocket 持久连接**（替代每事件 HTTP POST），把输入前向从 ~15ms 压到 <5ms。【参照】`AUDIT-20261124-04-INPUT-LATENCY-FIX.md`。

这三件是 SoulLink 与 Moonlight 的**真正结构差距**（`LOW-LATENCY-DEEP-DIVE-20261124.md §5.4`），与「换不换 UDP」正交。

### 7.2 何时才值得启动 UDP

【推断】触发条件（可写进未来路线图）：
- 产品明确进入**公网有损**部署（跨互联网串流），且
- 实测丢包 >0.5%、RTT >20ms，TCP 帧流出现周期性卡顿，且
- 已落地 §7.1 的帧边界 + 按需 IDR（否则换 UDP 也会因无帧语义/FEC 而花屏）。

---

## 8. 证据来源清单

| 来源 | URL | 用途 |
|---|---|---|
| MDN WebTransport API | https://developer.mozilla.org/en-US/docs/Web/API/WebTransport_API | §2（协议栈/HTTPS/证书/部署） |
| MDN WebTransport() constructor | https://developer.mozilla.org/en-US/docs/Web/API/WebTransport/WebTransport | §2（HTTPS scheme、congestionControl、serverCertificateHashes） |
| MDN WebTransport/datagrams | https://developer.mozilla.org/en-US/docs/Web/API/WebTransport/datagrams | §2（不可靠数据报语义） |
| Can I Use — WebTransport | https://caniuse.com/webtransport | §2.2（支持率） |
| MDN RTCDataChannel | https://developer.mozilla.org/en-US/docs/Web/API/RTCDataChannel | §3（协议栈/支持率） |
| MDN RTCPeerConnection/createDataChannel | https://developer.mozilla.org/en-US/docs/Web/API/RTCPeerConnection/createDataChannel | §3（ordered/maxRetransmits/maxPacketLifeTime） |
| MDN WebRTC API | https://developer.mozilla.org/en-US/docs/Web/API/WebRTC_API | §3（ICE/SCTP/DTLS） |
| MDN Glossary/QUIC | https://developer.mozilla.org/en-US/docs/Glossary/QUIC | §2（HOL/建连往返） |
| 本仓库 `E2E-LATENCY-BASELINE-20261124.md` | docs/research/ | 实测传输 0.8~5ms、端到端 85~130ms |
| 本仓库 `LATENCY-BENCHMARK-20261123.md` | docs/research/ | 实测 `/live` 0.8ms p95 4.6ms |
| 本仓库 `LOW-LATENCY-DEEP-DIVE-20261124.md` | docs/research/ | Moonlight UDP/FEC、浏览器天花板、TCP 三件事 |
| 本仓库 `AUDIT-20261124-05-WEB-CLIENT-GAP.md` | docs/research/ | fetch 形态、拼包税、缺帧边界 |
| 本仓库 `WEB-VIEWER-LATENCY-GAP-20261123.md` | docs/research/ | 回环 HOL≈0、换形态≠根治 |
| 本仓库 `WEB-RUNTIME-PROBE-20261123.md` | docs/research/ | 云原神 WebRTC jitterBuffer ~74ms |

---

## 9. 边界遵守自查

未改任何产品代码/脚本/配置；未碰 `dist/stable/SoulLink-Stable.exe`（PID 13392）；未使用/控制 A1/A4；未改注册表；未重启任何进程；未发布、未碰 COS/latest.json/版本号。唯一写入为本研究文档（`docs/research/BROWSER-UDP-FEASIBILITY-20261124.md`）。所有代码事实均来自只读 `read`/`grep`，行号已核对。
