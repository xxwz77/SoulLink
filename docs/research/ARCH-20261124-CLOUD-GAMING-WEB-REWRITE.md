# 架构决策：自建「云原神式」Web 应用能否降低延迟（三层拆解 + 方案对比）

> 日期：2026-11-24
> 作者：rdp-session-analyst（dsh-team team_mwu35l8pae8oj5 / member m_mwu3790mafbish / task task_mwu3v7i2da1pcv）
> 性质：只读审计 + 架构决策。未改任何产品代码/脚本/配置/版本号；未碰 dist\stable\SoulLink-Stable.exe；未用 A1/A4；未执行 git。
> 证据分级：【代码事实】= 源码 file:line；【已有实测】= 本仓库前序实测；【官方文档】= 规范原文；【营销口径】= 官网宣传非承诺；【推断】= 有依据推导；【待验证】= 无证据不猜。
>
> 前置事实（承接本队 08/10/11 报告）：233ms 是差分 = SoulLink(浏览器)端到端 − RDP端到端；SoulLink 绝对端到端 ≈ 233 + RDP自身延迟 ≈ 253~293ms；其中服务端日志自报 ~90ms，存在 ~120~160ms 账外缺口（AUDIT-20261124-11-LATENCY-ACCOUNTING-GAP.md §0）。本报告回答：把 web 端重写成「云原神式」能否解释并消掉这个缺口。

---

## 0. 一句话结论

不能。「云原神式 WebRTC 重写」对当前局域网/回环场景的增量延迟收益 ≈ 0~2ms（甚至为负——云原神那套 RTP jitter buffer 实测 ≈74ms/帧，是 SoulLink 现在没有背的税）。它既不能解释、也不能消除 120~160ms 账外缺口：那个缺口从未被测量（前端无 rVFC、服务端不下发帧时间戳，全仓 requestVideoFrameCallback 与 capturedAt 均 0 命中），重写架构不会自动补上测量。云原神式的真正价值在公网/手机（NAT 打洞、UDP、拥塞降档），不在关闭当前的 233ms 差分。

---

## 1. 云原神式 Web 云游戏「各技术项」三层拆解（解决什么问题 + 对照本仓库）

> 云原神运行时实测口径见 WEB-RUNTIME-PROBE-20261123.md（CDP 一手证据）：实际媒体通道 WebRTC SRTP/UDP、video.srcObject=MediaStream 原生呈现、输入=同一 PeerConnection 的 DataChannel、RTT 7ms 角标、jitterBuffer(video)≈73.8ms/帧、NACK/PLI 全启用。注意：§9.1 逆向曾推断「WebTransport 为主」，已被运行时实测推翻（§9.2），此处一律以运行时实测为准。

### L1 采集/编码层（服务端把画面变成 H.264 帧）

| 技术项 | 解决什么问题 | SoulLink 已具备 | 未具备/不适用 | 证据 |
|---|---|---|---|---|
| 抓 GPU 帧（非 GDI/BitBlt） | 拿真 GPU 桌面/离屏表面，避免 CPU 读回 | ✅ DXGI DDA（RDP 会话内已实测可用） | 云原神抓的是云端 GPU 离屏/游戏 present 表面（真实 GPU 桌面），SoulLink 抓的是 RDP 会话 DWM 合成结果——两者抓帧对象不同，不是换 API 就能对齐；RDP 会话内 DWM 合成率封顶 ~60fps（p50=15.2ms） | DxgiDdaCapture.cs:132-234；RDP-DDA-CEILING-EXPERIMENT-20261124.md §2 |
| H.264 硬编 + 低延迟参数 | 编码 2~10ms 而非软编 100ms+ | ✅ NVENC-SDK：ULTRA_LOW_LATENCY + 无限GOP + 关B帧(frameIntervalP=1) + zeroReorderDelay + 按需IDR(FORCEIDR/OUTPUT_SPSPPS) | 云原神主选 HEVC/多档 codec，SoulLink 仅 H.264——只影响带宽/画质，不影响延迟 | NvencSdkEncoder.cs:553,561-566,680-682；AUDIT-20261124-01-SUNSHINE-CHAIN.md §0「编码参数全对齐」 |
| 跨 GPU 零拷贝 | 抓屏与编码同 GPU 时免 CPU 转换 | ✅ 同 GPU 时 PS 着色器零拷贝（PSNR 52.6dB）；跨 GPU（Intel 抓屏+NVIDIA 编码）物理禁用 | 云原神云端是渲染与编码同卡天然零拷贝；SoulLink A2 会话跨 GPU 是物理限制，只能 CPU 转换 1~2ms（并行化后）——不是重写能改的 | SessionRecorder.cs:974-991；E2E-LATENCY-BASELINE-20261124.md §4-1 |

L1 结论：采集/编码层 SoulLink 已对齐云游戏内核（NVENC 低延迟四件套 + DDA + 并行转换），增量收益 ≈ 0ms。L1 的剩余瓶颈（RDP 会话 DWM 合成率 60fps、跨 GPU 只能 CPU 转换）是物理/会话层边界，云原神式重写碰不到（云原神根本没有 RDP 会话这一层）。

### L2 传输层（字节从服务端到浏览器）

| 技术项 | 解决什么问题 | SoulLink 已具备 | 未具备/不适用 | 证据 |
|---|---|---|---|---|
| WebRTC SRTP/UDP | NAT 打洞 + 丢包恢复(NACK/FEC) + 拥塞控制 + jitter buffer | ❌（当前 fetch chunked HTTP/TCP 直连） | 局域网/回环不适用：实测回环 TCP RTT p50≈1.1~1.7ms、丢包≈0%，HOL 不咬人；换 UDP 收益 0~2ms 甚至为负（HTTPS+证书+QUIC 栈前置成本） | AUDIT-20261124-10-TRANSPORT-LATENCY-BUDGET.md §3/§6 |
| 拥塞降档（transport-wide-cc） | 弱网自动降码率防卡死 | ❌（静态 8000kbps 无降档） | 局域网不适用；公网/手机才有量级收益 | Mature-WEB-STACK §2.1 selkies congestion-control L42 |
| 输入通道与视频分离 | 避免输入被推流阻塞 | ✅ 输入已挪独立 WebSocket /ws/input（HTTP POST 降级保留） | 云原神是同一 PeerConnection 的 DataChannel；SoulLink 是独立 WS——功能等价 | WebPages.cs:1620-1655；AgentWebServer.cs:692-744 |
| 前端主动要关键帧（PLI/requestKeyFrame） | 解码错误/丢帧后 1 帧内恢复，非断流重连 | ✅ /api/idr POST + 前端 requestIdr()（限流 300ms） | 云原神靠 RTP PLI；SoulLink 用 HTTP 控制消息——语义等价（W7 已落地） | WebPages.cs:1031,1301-1304；AgentWebServer.cs:321-325,534-553 |

L2 结论：传输层是云原神式重写与现状差异最大的地方，但收益发生在公网/手机（RTT>20ms 且丢包>0.5%），不是当前局域网场景。当前场景下重写传输层增量 ≈ 0~2ms（甚至为负）。

### L3 浏览器解码/呈现层（H.264 帧 → 上屏）

| 技术项 | 解决什么问题 | SoulLink 已具备 | 未具备/不适用 | 证据 |
|---|---|---|---|---|
| 硬解 + 低延迟 hint | WebCodecs optimizeForLatency:true + prefer-hardware | ✅ 已设（且 configure 失败自动降级重试） | — | WebPages.cs:1284-1286,1300 |
| 零回读呈现 | 2D drawImage(VideoFrame) 会 GPU→CPU→GPU 回读；改 WebGL2 texImage2D | ✅ WebGL2 呈现层已落地（desynchronized:true，不可用才回退 2D） | 云原神用 video.srcObject=MediaStream 原生呈现——两者都零回读；原生 video 由浏览器合成器直管 vsync，WebGL2 canvas 走 GPU 合成器，差异非延迟级 | WebPages.cs:1148-1231,1336-1337 |
| 帧时间戳 | 假 15fps 硬编码 ts+=66666 | ✅ 已修：ts += 1000000/streamFps 跟随服务端 | — | WebPages.cs:1492 |
| 解码队列背压 | decodeQueueSize > SKIP_QUEUE 丢 delta | ✅ 阈值已跟随 streamFps（30fps→4帧≈133ms） | — | WebPages.cs:1428 |
| 帧龄埋点（rVFC） | 量「解码→上屏」真实毫秒数 | ❌ 全仓 0 命中（requestVideoFrameCallback 0 命中） | 这是定位 120~160ms 账外缺口的唯一手段，但它是测量项不是降延迟项 | WebPages.cs grep 0 命中；AUDIT-20261124-05 §6-P2 |
| jitter buffer | 抗网络抖动（牺牲延迟换稳定） | ❌（WebCodecs 直推，无 jitter buffer） | 云原神实测 jitterBuffer ≈73.8ms/帧——这是它背的税，SoulLink 没有背。换成它=在局域网白白加 ~74ms | WEB-RUNTIME-PROBE-20261123.md §0/§1 |

L3 结论：浏览器呈现层 SoulLink 已把云原神式低延迟手段基本全部落地（WebGL2 零回读、硬解 hint、帧时间戳修复、队列背压）。真正缺的 rVFC 帧龄埋点是测量能力，不是延迟优化。且云原神背的 74ms jitter buffer 税 SoulLink 现在没背——重写反而可能更慢。

---

## 2. 重写相对现状的增量收益（ms）与证据等级

| 层 | 重写动作 | 当前局域网增量收益 | 证据等级 | 能否解释 120~160ms 账外缺口 |
|---|---|---|---|---|
| L1 采集/编码 | 换云 GPU 抓帧 | 0ms（采集对象是 RDP 会话 DWM 产物，重写不改 RDP 会话 DWM 60fps 上限） | 已有实测 | 否 |
| L2 传输 | 换 WebRTC/UDP | 0~2ms（甚至为负）（回环 TCP 0.8~5ms、丢包≈0；云原神 jitterBuffer 反而 +74ms） | 已有实测 + 一手证据 | 否 |
| L3 呈现 | 换 video.srcObject / 补 rVFC | 0ms（优化项已落地）；rVFC 是测量非优化 | 代码事实 | 否——但 rVFC 是定位缺口的唯一手段 |

合计：云原神式重写对当前局域网场景的增量延迟收益 ≈ 0~2ms（甚至为负）。

明确回答「能否解释 120~160ms 账外缺口」：不能。理由三条：
1. 缺口从未被测量——服务端不下发帧时间戳（AgentWebServer.cs grep capturedAt 0 命中）、前端无 rVFC（WebPages.cs grep requestVideoFrameCallback 0 命中）。在没测出来之前，任何「重写能消掉它」的断言都是猜测。
2. 现有 web 端已经落地了云原神式的低延迟技术项（WebGL2 零回读、硬解 hint、按需 IDR、输入 WS、Pointer Lock、帧时间戳修复、SKIP_QUEUE 自适应）——这些降延迟项落地后缺口仍在，反证缺口不在这套架构的「缺少」上。
3. 云原神那套 WebRTC 自带的 jitter buffer（≈74ms）在回环下是纯税，重写只会让本地更慢（与 AUDIT-20261124-10 §6.1「换 UDP 收益≈0 甚至为负」互证）。

---

## 3. 方案对比（2~3 个）

> 收益均指「当前局域网 233ms 差分场景」；「公网手机意义」单独标注。

| 维度 | 方案 A：维持现状 + 补埋点 | 方案 B：现有链路上轻量加固（非 WebRTC） | 方案 C：完整云原神式 WebRTC 重写 |
|---|---|---|---|
| 做什么 | 服务端随帧下发 capturedAt（复用 DDA 的 LastPresentTime）+ 前端 rVFC 埋点，把 120~160ms 缺口测出来 | 在现有 WebCodecs+framed 链路上加：① rVFC 帧龄埋点 ② 传输层拥塞降档（仿 selkies）③ HEVC 档（可选） | 媒体换 WebRTC(SRTP/UDP) + DataChannel 输入 + RTP jitter buffer/NACK/PLI + video.srcObject |
| 当前局域网收益 | 0ms（纯测量，但是后续一切优化的前提） | 0~2ms（降档在局域网不触发） | 0~2ms，甚至为负（jitter buffer +74ms、HTTPS/QUIC 前置成本） |
| 能否解释账外缺口 | ✅ 唯一能定位缺口的手段 | 部分（埋点定位，降档不相关） | ❌ 不能 |
| 公网/手机意义 | 中性（埋点同样适用） | 中（拥塞降档是公网刚需） | 高（NAT/UDP/丢包恢复/拥塞控制，云原神同款） |
| 工作量 | 0.5 人日 | 1~2 人日 | 3~5 人日 + 服务端 WebRTC 网关（.NET 无第一方 WebTransport 服务端，TRANSPORT-ADAPTIVE-DESIGN §1.2） |
| 风险 | 低 | 低 | 高（HTTPS+证书硬门槛；穿透工具 UDP 支持差异；jitter buffer 调参引入新变量） |
| 证据 | AUDIT-20261124-05 §6-P2；AUDIT-20261124-11 §7 | Mature-WEB-STACK §7.1 | WEB-RUNTIME-PROBE §0；BROWSER-UDP-FEASIBILITY §2.5/§2.6；WAN-MOBILE-TRANSPORT-DECISION §2.2 |

推荐顺序：
- 先做 A（0.5 人日）：不测出来，谈任何重写/优化都是对着空气开枪。这是当前唯一能把 120~160ms 缺口从「账外猜测」变成「可定位」的动作。
- 局域网场景不需要 C：B 的降档项在公网才有收益，当前可缓。
- C 是产品进入公网/手机时的正确终态（对齐云原神/Moonlight 现实选择），但触发条件是公网有损部署，不是关闭当前 233ms 差分。

---

## 4. 给用户的一句话（≤80 字）

> 云原神式重写救不了当前的 233ms：局域网下它增量为 0 甚至更慢，缺口（约 140ms）还没被测量。先花半天埋点把缺口定位出来，公网/手机阶段再谈 WebRTC。

---

## 5. 证据索引

- 云原神运行时一手证据：WEB-RUNTIME-PROBE-20261123.md §0/§1（WebRTC SRTP/UDP、video.srcObject、DataChannel、RTT 7ms、jitterBuffer≈73.8ms/帧、NACK/PLI 全启用）。
- 云原神 cg-sdk 逆向 + 运行时修正：Mature-WEB-STACK-20261123.md §9.1/§9.2。
- 国内云游戏/远控路线：CN-CLOUD-GAMING-20261122.md §0/§3.3/§7。
- 传输层预算（回环 RTT 0.8~5ms、UDP 收益≈0）：AUDIT-20261124-10-TRANSPORT-LATENCY-BUDGET.md §3/§5/§6。
- 账外缺口定位：AUDIT-20261124-11-LATENCY-ACCOUNTING-GAP.md §0/§6/§7。
- 浏览器段结构审计：AUDIT-20261124-05-WEB-CLIENT-GAP.md §1/§5。
- RDP 会话 DWM 60fps 上限：RDP-DDA-CEILING-EXPERIMENT-20261124.md §2。
- 前端实现 file:line：Services/WebPages.cs:1148-1231(WebGL2 呈现)、1266-1296(硬解 hint)、1301-1324(按需 IDR)、1358-1381(fetch+AbortController)、1402-1496(AU 攒批+SKIP_QUEUE+ts)、1620-1655(输入 WS)、1905+(Pointer Lock)。
- 服务端：NvencSdkEncoder.cs:553,561-566,680-682；AgentWebServer.cs:321-325,437-525,534-553,692-744；SessionRecorder.cs:97-103,974-991。

## 6. 边界遵守自查

未改任何产品代码/脚本/配置/版本号；未碰 dist\stable\SoulLink-Stable.exe；未用 A1/A4；未杀进程；未 git；未发布/COS/latest.json。唯一写入 = 本文档 + docs/research/README.md 登记一行。全部为读文档 + 读源码（file:line 可回查），无运行时操作。
