# 公网 / 内网穿透 / 手机访问下的传输层决策：UDP 是否必须

> 日期：2026-11-24
> 作者：proto-webudp（dsh-team `team_mwtjn0d77v8gtr` / member `m_mwu0xpde4m88k0` / task `task_mwu1696b5l72dl`）
> 性质：**纯调研**。未改任何产品代码/脚本/配置，未碰 `dist/stable/SoulLink-Stable.exe`，未用 A1/A4，未改注册表。唯一写入为本研究文档。
> 承接：`BROWSER-UDP-FEASIBILITY-20261124.md`（LAN 下结论=不换 UDP）；本文件回答**追加关键场景**——公网直连 / 内网穿透 / 手机 4G-5G 下，UDP 是否必须。
>
> **证据分级（全文通用）**：
> - **【官方文档】**＝frp/ngrok/Cloudflare/Tailscale 官方文档、MDN、RFC，附 URL，可回查。
> - **【实测】**＝本仓库 `docs/research/` 前序任务实测数字，附出处。
> - **【代码事实】**＝当前源码可证（file:line）。
> - **【推断】**＝有依据的推导，未实测；推导过程写明。
> - **【查不到】**＝闭源或一手来源无法访问，明确标注，不编造。

---

## 0. 一句话结论

**场景变了，结论也变：在内网穿透 + 手机 4G/5G（RTT 20~100ms、丢包 0.1~3%）下，TCP 队头阻塞从「≈0」变成「主延迟来源」，UDP 从「可选项」变成「强烈建议项」。但有一个前置硬约束可能直接否决 WebTransport：**主流免费内网穿透工具里，只有 frp 和 Tailscale/ZeroTier 能透传 UDP，ngrok 免费版 / Cloudflare Tunnel 只转 HTTP/TCP，花生壳主要转 TCP——如果用户落在「只转 TCP」的穿透方案上，WebTransport（QUIC/UDP）根本建不起连接，反而只能退回「TCP 上的可靠流」或换穿透方案。**决策结论：先确认穿透方案是否支持 UDP；支持 → 值得换 WebTransport（首选项）；不支持 → 不换协议，改用「WebSocket 二进制分帧 + 帧边界语义 + 客户端按需 IDR」在 TCP 上把队头阻塞的破坏降到可接受，或换到 Tailscale/ZeroTier。**

---

## 1. 三种访问场景的链路特征对比

| 维度 | ① 公网直连（有公网 IP + 端口映射） | ② 内网穿透（frp/ngrok/CF Tunnel/Tailscale） | ③ 手机 4G/5G |
|---|---|---|---|
| 链路 | 客户端 → 公网 IP:端口 → agent | 客户端 → 隧道公网端点 → 隧道 → agent | 客户端 → 基站 → 骨干网 →（同上 ①或②） |
| 典型 RTT | 国内跨城 10~40ms；跨运营商 30~60ms | 取决于隧道服务器位置，通常 10~50ms（同城/同省）；跨国可达 100ms+ | **4G 20~60ms；5G 10~30ms**（空口 + 骨干） |
| 典型丢包率 | 0.01~0.5%（有线骨干） | 0.1~2%（隧道服务器负载/公网抖动） | **4G 0.5~3%；5G 0.1~1%**（无线空口固有） |
| 抖动 | 低（亚 ms~数 ms） | 中（隧道中继 + 公网） | **高（4G 数十 ms 抖动，5G 低些）** |
| 安全 | 需自建 TLS，暴露面最大 | 隧道自带加密（frp TLS/Tailscale WireGuard），不暴露公网端口 | 继承 ①或② |
| SoulLink 现状兼容性 | `http://` 明文 + 端口映射 = 高危（本仓库 `REMOTE-ACCESS-OPTIONS §6`） | **首选**（Tailscale/ZeroTier 零运维） | 依赖 ①或② |

**RTT/丢包数据出处**：
- 4G 典型 RTT 20~60ms、5G 10~30ms、移动网络丢包 0.1~3%：**【官方文档/行业标准】**（3GPP 定义的 5G 空口单向延迟目标 1ms、eMBB 目标 4ms，但端到端含骨干/拥塞通常 10~30ms；移动宽带丢包率随信号质量在 0~3% 波动）。精确到「某运营商某时刻」的数值无稳定官方单源，故本节给**区间**并标注为行业典型值，非本仓库实测。
- 本仓库可参考的实测：回环 `/live` 0.8ms p95 4.6ms（【实测】`LATENCY-BENCHMARK-20261123.md:33`）；云原神 RTT 7ms（【实测】`WEB-RUNTIME-PROBE-20261123.md:44`）。

---

## 2. TCP 队头阻塞在 WAN 下的量化分析

### 2.1 机制

HTTP/1.1 chunked 是**单条 TCP 流**，视频所有帧的字节按序在同一流上传输。**一个 TCP 段丢包，后续所有字节（含后续所有帧）必须等这个段重传成功才能交给应用层**——这就是队头阻塞（HOL）。丢包重传的等待时间 ≥ 一个 RTT（更准确：RTO 起步通常 ≈ 200ms，TCP 快速重传需要 3 个重复 ACK 才能触发，实际等待 ≈ 1 个 RTT 到 RTO 之间）。【官方文档】RFC 6298（TCP RTO）、MDN Glossary/Head_of_line_blocking。

### 2.2 量化模型（【推断】，推导写明）

设 RTT = R，丢包率 = p，每帧平均 N 个 TCP 段。单帧「至少发生一次丢包」的概率 ≈ 1−(1−p)^N。发生丢包后，该帧及之后所有排队字节的到达延迟增加 ≈ R（快速重传）到 RTO（超时重传，~200ms 起步）。

以 **1080p60 H.264 @ 8Mbps、帧均 ~16.7KB、MTU 1500（每帧约 11 个 1460B 段）** 为例：

| 场景 | RTT | 丢包率 p | 单帧至少一丢包概率（N≈11） | 丢包后延迟尖峰 | 稳态帧到达延迟影响 |
|---|---|---|---|---|---|
| 回环/LAN | 0.8ms | ≈0 | ≈0 | — | ≈0.8ms（【实测】） |
| 公网直连（有线） | 30ms | 0.1% | 1−(0.999)^11 ≈ 1.1% | 30~200ms | 偶发卡顿（每 ~90 帧一次） |
| 内网穿透 | 40ms | 1% | 1−(0.99)^11 ≈ 10.5% | 40~200ms | **每 ~10 帧卡一次，观感明显卡** |
| 手机 4G | 50ms | 2% | 1−(0.98)^11 ≈ 19.9% | 50~200ms | **每 ~5 帧卡一次，不可用级** |
| 手机 5G 弱信号 | 30ms | 0.5% | ≈ 5.4% | 30~200ms | 每 ~18 帧卡一次，可感 |

**关键结论**：
1. **p ≤ 0.1% 且 RTT ≤ 30ms（公网有线直连）**：HOL 影响「偶发可容忍」，UDP 收益中等。
2. **p ≥ 0.5% 且 RTT ≥ 30ms（内网穿透 + 手机 4G/5G）**：HOL 变成**主延迟来源**——一个关键帧丢包 = 画面花屏或冻结 50~200ms，且后续帧全被堵住。此时 UDP（丢旧保新 + 不重传）的收益**从「≈0」跃升到「决定可用性」**。

### 2.3 与上一份文档结论的关系（不自相矛盾）

- 上一份 `BROWSER-UDP-FEASIBILITY-20261124.md` 的「不换」结论**严格限定在 LAN/回环（丢包≈0）**。
- 本任务场景（穿透 + 手机）的 p 和 RTT 跨过了 §2.2 的阈值，**结论按原决策树（`RTT>20ms 且丢包>0.5%`）自然翻转为「值得换」**。两者一致，是同一决策树的两端。

---

## 3. 内网穿透对 TCP / UDP 的支持差异（★ 本节最关键）

| 穿透方案 | TCP | UDP | 是否支持 WebTransport(QUIC/UDP) | HTTPS/证书 | 证据 |
|---|---|---|---|---|---|
| **frp** | ✅（type=tcp） | ✅（**type=udp 需显式配置**） | ✅ 可（但要自己配证书） | 需自配 TLS 证书 | 【官方文档】gofrp.org Proxy Config：`type` 选项含 `tcp, udp, http, https, stcp, sudp, xtcp`，`UDPProxyConfig` 单独存在 |
| **ngrok** | ✅（免费版含 TCP） | ❌ **免费版无 UDP**（无 UDP endpoint） | ❌ | 有（ngrok 自带 TLS 域名） | 【官方文档】ngrok Gateway 只有 TCP/HTTP/TLS endpoints；`/gateway/endpoints/udp` 返回 404（无此端点） |
| **Cloudflare Tunnel** | ✅（HTTP/HTTPS/TCP via Spectrum） | ❌ **Tunnel 本体只转 HTTP/HTTPS；UDP 需另购 Cloudflare Spectrum（付费，且 UDP 仅 Enterprise 档）** | ❌（免费档） | 有（CF 自带 TLS 域名，最省心） | 【官方文档】CF Tunnel use-cases 仅列 SSH/RDP/SMB/gRPC（皆 TCP）；Spectrum "available on all paid plans ... Enterprise supports all TCP and UDP" |
| **Tailscale / ZeroTier** | ✅（上层任意） | ✅（**WireGuard 隧道，隧道本身是 UDP，上层 TCP/UDP 都能跑**） | ✅ 可（但需在 agent 侧配证书；Tailscale 提供 HTTPS 证书） | Tailscale 提供 `tailscale cert` 自动 HTTPS 证书 | 【官方文档】Tailscale "WireGuard® for Enterprises" + "Set up HTTPS certificates" |
| **花生壳 / 其他传统内网穿透** | ✅ | ❌ 主要转 TCP（部分付费版有 UDP，但非标准） | ❌ | 视版本 | 【查不到·行业常识】无统一官方文档，按「主要 TCP」标注 |

### 3.1 结论（穿透对 UDP 的支持是硬约束）

1. **能透传 UDP 的免费方案**：`frp`（自建 VPS）、`Tailscale` / `ZeroTier`（WireGuard，零运维）。
2. **不能透传 UDP 的免费方案**：`ngrok`（免费）、`Cloudflare Tunnel`（免费档）、花生壳（主要）。
3. **推论**：WebTransport 基于 QUIC/UDP。**若用户用的是 ngrok 免费版 / Cloudflare Tunnel 免费版 / 花生壳，WebTransport 直接无法建立连接**，UDP 方案在这些穿透下不成立；此时唯一出路是「换到 frp 或 Tailscale/ZeroTier」，否则只能留在 TCP 上做优化（§5 方案 A）。

---

## 4. 手机浏览器 WebTransport 支持率（补充前置）

| 浏览器 | WebTransport | 说明 |
|---|---|---|
| Chrome for Android / Samsung Internet | ✅ 157+ | 主流安卓默认浏览器可用 |
| Safari on iOS | ✅ 26.4+（iOS 18.4+） | 需较新 iOS |
| QQ Browser | ❌ | 国产套壳不支持 |
| UC Browser | ✅ 15.5+ | 部分支持 |

**结论**：iOS 需 18.4+（较新）、安卓 Chrome 可用、国产套壳（QQ）不行。【官方文档】caniuse.com/webtransport。

---

## 5. 决策矩阵：场景 × 传输方案

| 场景 | 推荐方案 | 延迟/成本/可行性打分 | 是否必须 UDP |
|---|---|---|---|
| LAN / 回环 | HTTP chunked（现状） | 延迟✅ 成本✅ 可行✅ | **否**（收益≈0） |
| 公网直连（有公网 IP + 有线） | TCP 上做「帧边界+按需IDR」先，WebTransport 可选 | 延迟🟡 成本🟡 可行✅ | **否**（p≤0.1% 可容忍，先做 TCP 三件事） |
| 内网穿透（frp/Tailscale，**支持 UDP**） | **WebTransport datagram（首选）** 或 WebRTC | 延迟✅ 成本🟡 可行✅ | **强烈建议**（p 0.5~2% 时 HOL 是主延迟） |
| 内网穿透（ngrok免费/CF免费/花生壳，**只转 TCP**） | **WebSocket 二进制分帧 + 帧边界 + 按需IDR**（留在 TCP） | 延迟🟡 成本✅ 可行✅ | **不能换**（穿透不支持 UDP）；或换 frp/Tailscale |
| 手机 4G/5G + 支持 UDP 的穿透 | **WebTransport datagram（首选）** | 延迟✅ 成本🟡 可行✅ | **强烈建议**（p 0.5~3%，HOL 决定可用性） |
| 手机 4G/5G + 只转 TCP 的穿透 | WebSocket 分帧 + 丢帧策略（留在 TCP，接受偶发卡） | 延迟🟡 成本✅ 可行🟡 | **不能换**（穿透限制）；或换 Tailscale |

### 5.1 明确决策（不含糊）

1. **必须换 UDP 的场景**：**手机 4G/5G + 支持 UDP 的穿透（frp / Tailscale / ZeroTier）**——此时 p≥0.5%、RTT≥30ms，TCP HOL 使画面「每几帧卡一次」，UDP 是「决定可用性」而非「锦上添花」。
2. **可以不换的场景**：LAN/回环（收益≈0）；公网有线直连（p≤0.1%，可容忍，先做 TCP 三件事即可）。
3. **不能换（客观限制）的场景**：穿透方案只转 TCP（ngrok 免费 / CF Tunnel 免费 / 花生壳）——WebTransport 建不起连接。此时要么换穿透工具（frp/Tailscale），要么留在 TCP 上用 WebSocket 分帧把破坏降到可接受。

---

## 6. 若必须换：优先级排序的落地方案与工作量

> 前提：已确认穿透方案支持 UDP（frp / Tailscale / ZeroTier），或公网直连。按「收益/成本」排序。

| 优先级 | 方案 | 内容 | 工作量 | 前置依赖 |
|---|---|---|---|---|
| **P0（先做，与 UDP 无关）** | 帧边界语义 + 客户端按需 IDR + 输入 WebSocket | 见 `BROWSER-UDP-FEASIBILITY-20261124.md §7.1`。**无论换不换 UDP 都要先做**，否则换 UDP 也因无帧语义/FEC 而花屏 | 中（1~2 周） | 无 |
| **P1（换 UDP 的首选）** | **WebTransport datagram** | 视频面 datagram（丢旧保新 + 应用层 FEC/按需 IDR 兜底）+ 控制/输入可靠 stream；agent 加 HTTPS+有效证书 + QUIC/HTTP3 栈（`HttpListener` 不支持，需 MsQuic 或独立服务） | **大**（协议层重构，3~5 周） | P0 + 穿透支持 UDP + 证书 |
| **P2（备选，仅当不想引入 QUIC 栈）** | WebRTC DataChannel（或媒体轨道） | 浏览器原生 RTP 栈 + NACK/PLI，但需 ICE/STUN/TURN 信令，且 `.NET 侧无现成 WebRTC 媒体栈`（本仓库 `REMOTE-ACCESS-OPTIONS §4c` 已证） | **更大**（服务端媒体栈 + 信令） | P0 |
| **P3（不换 UDP 的降级）** | WebSocket 二进制分帧 + 帧边界 + 丢帧策略 | 穿透只转 TCP 时的替代：TCP 上做消息分帧消除「块边界≠帧边界」，配合按需 IDR + 丢旧保新把 HOL 破坏降到「偶发卡顿可接受」 | 小（前端 + 服务端分帧，1 周） | 无 |

### 6.1 关键工程风险提示

1. **`HttpListener` 不支持 HTTP/3/QUIC/WebTransport**（【代码事实】`Relay/AgentWebServer.cs:85` 仅 `http://+`）。换 WebTransport 必须先引入第三方 QUIC 栈（.NET `MsQuic` / `System.Net.Quic` 尚不支持 WebTransport 服务端，需评估成熟度），或把视频面挪到独立进程。这是**最大的成本项**，不能低估。
2. **证书**：frp/Tailscale 下可自签证书 + `serverCertificateHashes` 绕过 PKI（【官方文档】MDN WebTransport constructor）；Cloudflare Tunnel 自带域名证书最省心（但免费档不转 UDP，矛盾）。
3. **回退策略**：无论选 WebTransport 还是 WebRTC，都要保留 HTTP chunked / WebSocket 的降级路径，否则「只转 TCP 的穿透」用户直接不可用。

---

## 7. 证据来源清单

| 来源 | URL | 用途 |
|---|---|---|
| frp Proxy Configuration（官方） | https://gofrp.org/en/docs/reference/proxy/ | §3（frp 支持 tcp/udp/sudp/xtcp） |
| ngrok Gateway（官方） | https://ngrok.com/docs/gateway/endpoints/tcp | §3（ngrok 只有 TCP endpoint） |
| ngrok UDP endpoint（404） | https://ngrok.com/docs/gateway/endpoints/udp | §3（ngrok 无 UDP） |
| Cloudflare Tunnel use-cases（官方） | https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/use-cases/ | §3（CF Tunnel 只 HTTP/HTTPS/TCP） |
| Cloudflare Spectrum get-started（官方） | https://developers.cloudflare.com/spectrum/get-started/ | §3（UDP 仅 Enterprise 档） |
| Tailscale HTTPS certificates（官方） | https://tailscale.com/docs/how-to/set-up-https-certificates | §3（Tailscale 提供 HTTPS 证书） |
| Tailscale 官方 | https://tailscale.com/（WireGuard） | §3（WireGuard 隧道，上层任意协议） |
| caniuse WebTransport | https://caniuse.com/webtransport | §4（手机浏览器支持率） |
| MDN WebTransport() constructor | https://developer.mozilla.org/en-US/docs/Web/API/WebTransport/WebTransport | §6（serverCertificateHashes、congestionControl） |
| MDN Glossary/Head_of_line_blocking | https://developer.mozilla.org/en-US/docs/Glossary/Head_of_line_blocking | §2（HOL 机制） |
| RFC 6298（TCP RTO） | https://datatracker.ietf.org/doc/html/rfc6298 | §2（RTO 起步 ~200ms） |
| 本仓库 `REMOTE-ACCESS-OPTIONS-20261123.md` | docs/research/ | §1/§6（穿透首选 Tailscale、.NET 无 WebRTC 栈、安全收口） |
| 本仓库 `BROWSER-UDP-FEASIBILITY-20261124.md` | docs/research/ | 承接上一份结论 + TCP 三件事 |
| 本仓库 `LATENCY-BENCHMARK-20261123.md` | docs/research/ | 实测回环 0.8ms |
| 本仓库 `WEB-RUNTIME-PROBE-20261123.md` | docs/research/ | 云原神 RTT 7ms、jitterBuffer ~74ms |

---

## 8. 边界遵守自查

未改任何产品代码/脚本/配置；未碰 `dist/stable/SoulLink-Stable.exe`（PID 13392）；未使用/控制 A1/A4；未改注册表；未重启任何进程；未发布、未碰 COS/latest.json/版本号。唯一写入为本研究文档（`docs/research/WAN-MOBILE-TRANSPORT-DECISION-20261124.md`）。所有代码事实来自只读 `read`/`grep`，行号已核对。
