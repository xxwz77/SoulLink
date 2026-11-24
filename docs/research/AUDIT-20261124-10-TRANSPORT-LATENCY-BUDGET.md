# 传输链路（HTTP chunked / TCP）对 233ms 的贡献审计

> 日期：2026-11-24
> 作者：transport-network-analyst（dsh-team `team_mwu35l8pae8oj5` / member `m_mwu37cxvahbb82` / task `task_mwu37oamanfvkz`）
> 性质：**只读审计**。未改任何产品代码/脚本/配置/版本号；未碰 `dist\stable\SoulLink-Stable.exe`（PID 13392）；未用 A1/A4（session 2/3）账号；未停/启任何进程；未改注册表；未发布、未碰 COS/latest.json。唯一写入 = 本文档 + `docs/research/README.md` 登记一行。
>
> 证据分级：【代码事实】= 当前源码 file:line 逐行可证；【实测】= 本会话真机测量（数值+方法可复现）；【已有实测】= 本仓库前序文档实测；【推断】= 有依据推导未实测；【待验证】= 无证据不猜。

---

## 0. 一句话结论

**用户当前是局域网/回环场景（实测 TCP RTT p50≈1.1~1.7ms），传输层在 233ms 差分里最多占 2~20ms（<10%），换 UDP/WebTransport 收益 ≈ 0 甚至为负。233ms 的主要矛盾不在传输层，而在管线（真机观看实际只有 ~18fps + 静止跳帧）与浏览器段（解码/上屏）。~140ms 账外延迟里，传输层只解释个位数毫秒。**

---

## 1. 测量口径澄清（Lead 更正后）

用户测量方法：RDP 会话内打开秒表，浏览器画面（SoulLink 观看页）与 RDP 画面同时呈现同一桌面，多次截图取平均。

**233ms = SoulLink（浏览器）端到端延迟 − RDP 端到端延迟**（差分口径）。

推论（本报告全部以此为口径）：
1. SoulLink **绝对**端到端 = 233ms + RDP 自身延迟（RDP 自身延迟未实测，行业典型 LAN RDP 30~80ms【推断】），即绝对值 ≥ 233ms。
2. 传输层在差分里的体现 = SoulLink 传输延迟 − RDP 传输延迟（RDP 走 TCP + bitmap delta，传输本身也是毫秒级）。
3. 服务端日志自报 ~90ms 与差分 233ms 严重不符 → 存在 ~140ms 账外延迟。本报告负责划定「传输层占多少」，排除/坐实传输层嫌疑。

---

## 2. 代码事实：服务端视频写出路径全链

### 2.1 写出路径（file:line）

| 环节 | 实现 | 证据 |
|---|---|---|
| HTTP 服务 | `HttpListener`（HTTP/1.1，http.sys 内核承载） | `Relay/AgentWebServer.cs:83-86` |
| chunked 响应 | `SendChunked=true` + `KeepAlive=true`，`ContentType=video/h264` | `AgentWebServer.cs:472-476` |
| **每帧一次 Flush** | `WriteFrameAsync`：先 `WriteAsync`（framed 模式先写 4B 大端长度前缀）再 **`FlushAsync` 每帧强制发出** | `AgentWebServer.cs:481-486`（`:485` 即 Flush）；framed 前缀 `:516-525` |
| 等帧方式 | 事件驱动：入队即 `FrameReady.Set()` 唤醒（**无 10ms 轮询税**，旧轮询已删） | 入队 `SessionRecorder.cs:819`；等待 `AgentWebServer.cs:500-503` → `SessionRecorder.cs:1240-1245` |
| 每 viewer 队列 | `Viewer.Pending`（`Queue<(byte[] Nal, DateTime At)>`），**深度上限 2**（`SOULLINK_VIEWER_QUEUE` 可调 1~8），丢旧保新，**队头是 IDR 时绝不丢** | `SessionRecorder.cs:26-33, 95-103, 815-819, 1036-1041` |
| 出队观测 | `queueAge` = 出队帧龄（服务端队列等待的直接测量） | `SessionRecorder.cs:1211-1231` |
| 浏览器收流 | `fetch` + `getReader()` 流式读，`v=2` 帧边界模式按 4B 长度切帧，读完一帧立即组装 AU 送 decode | `Services/WebPages.cs:1374-1381, 1498-1546` |

### 2.2 单帧从编码完成到字节进 TCP 的排队上限【代码事实+实测】

- 队列上限 = 深度 2 × 帧间隔（30fps 时机制上界 ≈ 66ms，拥塞才顶满）；
- 事件唤醒延迟 ≈ 0（`AutoResetEvent` 内核信号，入队即 Set）；
- **真机实测（A2 agent 日志 `soullink-relay-27656.log`，2026-11-24 10:33:41~45，viewers=1）**：
  `queueAge=0 queueAgeMax=18 queueDrops=0 queueDepthMax=1`
  → 服务端队列稳态等待 **≈0ms，全程最坏 18ms**，队列从未积压（深度峰值 1）。
- 写出即 Flush：无应用层攒包缓冲（每帧一次 `FlushAsync` 强制清空）。
- http.sys 层缓冲：`FlushAsync` 语义上触发 http.sys 发送当前数据【推断，MS 文档未明说 chunked+flush 的内核行为，标注待实测——但既有仓库实测 `/live` 0.8ms p95 4.6ms（`LATENCY-BENCHMARK-20261123.md:33`）旁证 http.sys 不会囤积响应】。

### 2.3 Nagle / 延迟 ACK 风险【代码事实 + 推断量化】

**(a) NoDelay 是否设置：没有，且无法在应用层设置。**

- 全仓 grep `NoDelay|UseNagleAlgorithm|ServicePointManager`：**0 命中**（唯一 TcpClient 出现在 `Services/RdpHostProbe.cs:85`，是探测代码非视频路径）。
- 服务端是 `HttpListener`，TCP socket 由 http.sys 内核管理，托管代码**拿不到 socket 对象**：MS 官方 API 页（learn.microsoft.com/dotnet/api/system.net.httplistener）全文无 Nagle/NoDelay/Socket 任何暴露【官方文档，本会话抓取核对】。

**(b) 风险量化：本场景 ≈ 0~2ms，不构成 40ms 级罚项。**

- Nagle 只在「有未确认的小段在途」时才延迟。当前写出模式是**每帧一次大块 WriteAsync（8Mbps/~18-43fps → 帧均 23~57KB，≈16~40 个满 MSS 段）+ 每帧 Flush**，不是「逐 NAL 小包」。只有帧尾最后一个不满段可能被 Nagle 扣住等 ACK；
- LAN RTT ≈1~2ms（本会话实测 §3）→ ACK 几乎立刻回来 → 尾段延迟 ≈1ms；
- 接收端延迟 ACK（Windows 默认最长 200ms）只在「纯静止无流量」时才到顶；本链路是持续帧流 + 静止保活 2fps 帧入队（`SessionRecorder` 静止保活），ACK 会随流量捎带，不会出现 200ms 死等【推断，机制推导】；
- 唯一理论风险点：framed 模式的 4B 长度前缀是独立一次 `WriteAsync`（`AgentWebServer.cs:523-524`），紧跟大块数据写（`:524`）——若 http.sys 把 4B 单独成段且被 Nagle 扣住，理论上加 1 RTT（LAN≈1ms，公网≈10ms）。**量级远到不了 40ms**【推断】。
- 逐 NAL 写出风险：**不存在**。写入单位是整帧 NAL（`WriteFrameAsync(nal)`，一帧一调用），见 `AgentWebServer.cs:494-498`。

**(c) 浏览器侧/代理攒包：**

- 浏览器直连 agent（`directBase()` 用 `location.hostname + directPort`，`WebPages.cs:1011-1015`）→ **浏览器 ↔ agent 之间无任何中间代理**（不经主程序转发视频流、无内网穿透组件——本机 netstat 快照无 frp/ngrok/CF/tailscale 监听【实测】）。
- 响应无 Content-Length（chunked），fetch 流式读没有固定缓冲语义；framed 模式读完一帧立即送 decode（`WebPages.cs:1498-1546`）。
- 主程序 10800 的路由表（`WebServer.cs:350-363`）**没有 /stream**：主程序转发模式下网页视频实际也要求 directPort 直连（`WebPages.cs:1242-1245` base 为空仍请求 `/stream` 会 404）→ 被测链路就是浏览器↔agent 直连。
- 若未来经内网穿透（frp/ngrok），chunked 无 Content-Length + 穿透服务转发会引入攒包风险 → 那是另一个场景（§6 公网判断）。

---

## 3. 本会话实测数据（全部只读操作）

> 方法说明：agent 端口 19099 当前无监听（E2E 基线的测试 agent 已按其清理规则退出，属预期状态，三个在跑 agent 均为管道模式无 web 监听【实测 netstat】），改用同机同等服务做回环计时 + 局域网第二节点 + 公网参照点。连接类测量全部为「TCP 三次握手完成即断开」的纯计时，不发送任何业务数据、不改状态。

| 目标 | 方法 | 结果 |
|---|---|---|
| 回环 TCP 握手 RTT（127.0.0.1:56686，http.sys 承载的 HTTP 服务） | TcpClient BeginConnect×30 | **min 0.68 / p50 1.14 / p95 2.06 / max 19.65 ms** |
| 回环 TCP 握手 RTT（127.0.0.1:135，系统 RPC） | ×10 | min 0.68 / **p50 1.09** / p95 1.32 ms |
| 回环 HTTP 首字节（curl.exe connect/ttfb/total） | ×3 | connect 0.35~0.93ms / **ttfb 0.9~2.8ms** / total 1.0~3.0ms |
| 回环 ICMP | ping×10 | 0ms |
| 局域网第二节点 TCP（网关 192.168.0.1:80） | ×15 | min 1.32 / **p50 1.69** / p95 6.19 / max 19.31 ms |
| 局域网 ICMP（网关） | ×10 | 0~2ms |
| 公网参照 ICMP（42.192.114.250 腾讯云，同城） | ×20 | min 9 / **avg 10** / max 13 ms |
| 公网参照 TCP（223.5.5.5:53 阿里 DNS） | ×15 | min 8.37 / **p50 9.19** / p95 10.21 ms |
| （弃用）119.29.29.29:53 | ×5 | p50≈1018ms，路径异常，不采信 |

**结论（实测）**：本机回环 TCP RTT ≈ 1ms；局域网（同网段第二节点）≈ 1.7ms；公网同城 ≈ 10ms。与仓库既有实测一致（`/live` 0.8ms p95 4.6ms，`LATENCY-BENCHMARK-20261123.md:33`；网络传输段 0.8~5ms，`E2E-LATENCY-BASELINE-20261124.md:36`）。

---

## 4. 场景判定：局域网/回环，不是公网

| 判据 | 证据 | 等级 |
|---|---|---|
| 浏览器与 agent 同机或同网段（192.168.0.x） | 本机 IP 192.168.0.101；网页用 `location.hostname + directPort` 直连（`WebPages.cs:1011-1015`）；netstat 无 frp/ngrok/tailscale 等穿透进程监听 | 实测 |
| 观看路径无中间代理 | `directBase` 非空时 fetch 直打 agent 端口；`IsAllowedPath` 白名单（`WebDirect.cs:62-74`）即 agent 侧路由 | 代码事实 |
| 回环/局域网 RTT | §3 实测 p50 1.1~1.7ms | 实测 |

**判定：用户当前是「局域网/回环直连」场景（SoulLink 的设计主场景）。公网/穿透场景当前不存在于这条被测链路。**

---

## 5. 233ms 差分的传输层预算

### 5.1 传输层各段（差分 = SoulLink 传输 − RDP 传输）

| 段 | 数值 | 证据 | 等级 |
|---|---|---|---|
| 服务端每 viewer 队列等待 | **0ms 稳态 / 18ms 全程最坏** | A2 agent 真机日志 `queueAge=0 queueAgeMax=18`（2026-11-24 10:33，viewers=1） | 实测 |
| 等帧唤醒（轮询税） | ≈0ms（事件驱动已落地） | `SessionRecorder.cs:819, 1240-1245`；`AgentWebServer.cs:500-503` | 代码事实 |
| Nagle 尾段延迟 | 0~2ms | §2.3 推导 | 推断 |
| TCP 线路传输（回环/LAN） | 0.8~5ms | §3 实测 + `E2E-LATENCY-BASELINE-20261124.md:36` | 实测 |
| 浏览器 fetch 收流缓冲 | 未知（无证据显示存在固定攒包；framed 模式读完整帧立即送 decode） | `WebPages.cs:1498-1546` | 待验证（需 rVFC 埋点） |
| RDP 侧传输（要从差分里减掉的） | ≈0~2ms（RDP 同样走本机/局域网 TCP） | RDP 3389 本机监听（netstat 实测）；RDP bitmap delta 机制 | 推断 |

**传输层差分贡献 ≈ (0~18 队列) + (0~2 Nagle) + (0.8~5 线路) + (0~? fetch 缓冲) − (0~2 RDP) ≈ 2~20ms。**

即：**传输层最多解释 233ms 里的 ~20ms（<10%），典型 ~5ms（~2%）。传输层不是本次的主要矛盾。**

### 5.2 ~140ms 账外延迟的去向（跨域提示，供管线/浏览器审计接手）

本会话拿到一个关键旁证：真机观看时段（10:33:41→45）agent 日志 `total` 2040→2070→2100，30 帧耗时 1.587s / 1.712s → **实际出帧率仅 ~18fps**，且 `staticSkipped=624 keepalive=155`（静止跳帧累计 624、保活帧 155）。对照 `E2E-LATENCY-BASELINE-20261124.md` 鼠标场景的 43.1fps——**用户测 233ms 的「秒表桌面」接近静止场景，帧供给节奏远低于动态场景**。一次画面变化被采到的平均等待 ≈ 1/2 × 帧间隔（18fps 时 ≈28ms）+ 采样错过窗口，叠加浏览器解码/上屏 20~40ms 与 RDP 侧的快节奏（RDP 对秒表这种小变化走 bitmap delta 即刻推送），差分被拉大到 233ms 是**管线（帧供给）+ 浏览器段**的事，不是传输的事【推断，证据=日志实测帧率 + staticSkipped 计数】。

验证建议（不在本任务范围）：同一秒表画面下抓 agent 日志的实际 fps 与 staticSkipped 增速；rVFC 埋点（`AUDIT-20261124-05` 的 P2 方案）测浏览器段真实帧龄。

### 5.3 浏览器侧嫌疑复核（2026-11-24 用户澄清后追加）

**用户澄清：Windows 自带分屏/窗口化，浏览器为前台焦点窗口，RDP 窗口与浏览器画面同时可见。** 据此「浏览器被遮挡/失焦 → Chrome 节流」假设**已由用户澄清排除**，从嫌疑名单划掉。同时源码侧没有「非最大化/部分可见敏感」的结构证据：

| 嫌疑项 | 复核结果 | 证据 | 等级 |
|---|---|---|---|
| 失焦/遮挡节流 | **已排除**（用户澄清：分屏下浏览器前台持有焦点） | 用户原话；无反证 | 用户澄清 |
| 隐藏时断流逻辑误触发 | 不触发：`document.hidden` 才断流，分屏可见 ≠ hidden | `WebPages.cs:2631-2637`（visibilitychange 仅 hidden 分支断流） | 代码事实 |
| 2D drawImage GPU 回读 | **当前源码已用 WebGL2 呈现层**（`texImage2D` 直接以 VideoFrame 为纹理源，注释明确「不走 CPU 读回」）+ `desynchronized:true`；仅 WebGL2 不可用才回退 2D | `WebPages.cs:1148-1163, 1219-1222`；回退分支 `:1337` | 代码事实（是否兑现需 rVFC 实测，标注待验证） |
| ts += 66666 假 15fps | **已修复**：当前实现 `ts += Math.round(1000000 / (streamFps > 0 ? streamFps : 30))`——跟随服务端 streamFps（默认 30fps → 33333µs 步进），非硬编码 66666 | `WebPages.cs:1492` | 代码事实 |
| 分屏窗口变小 → 分辨率/缩放开销 | canvas 内部分辨率固定 = 解码帧 `displayWidth/Height`（仅帧尺寸变化才重设）；CSS 显示尺寸由 `max-width:100% + object-fit:contain` 缩放，**由 GPU 合成器处理，非 CPU**；服务端编码尺寸来自抓屏几何（`ew = width & ~1`）**与浏览器窗口无关**，无按客户端窗口转码/缩放逻辑 | `WebPages.cs:1331-1335, 301-304`；`SessionRecorder.cs:940, 962-969` | 代码事实 |
| devicePixelRatio 变化 | 源码渲染路径无 dpr 相关代码（grep `devicePixelRatio` 0 命中）；无 resize 事件重建链路（canvas 尺寸只随帧尺寸变，不随窗口变） | grep 结果 | 代码事实 |

**剩余未排除的嫌疑（重新排序）**：
1. **浏览器链路本身真实耗时未实测**（fetch 收包 → 切帧 → VideoDecoder → output → 上屏）：旧文档 20~40ms 是估算，须 rVFC/performance.now 埋点实测——下一优先级。
2. **服务端帧供给节奏（18fps vs 37.7/43fps）与观看时段桌面活动状态的相关性**：10:33 快照的 ~18fps 与 staticSkipped=624 是「秒表桌面接近静止」的读数；E2E 基线 43fps 是鼠标移动场景。两次测量场景不同——**233ms 测量时段需对齐复测**（同秒表画面抓 agent 日志实际 fps 与 staticSkipped 增速）。
3. 分屏下 `desynchronized:true` 是否真兑现低延迟呈现（无额外呈现缓冲）：需 CDP 或 rVFC 对照验证。

---

## 6. 换 UDP / WebTransport 能省多少？

### 6.1 用户当前场景（局域网/回环）：**省不了，直说**

| 项 | 数值 | 依据 |
|---|---|---|
| TCP 现状端到端传输段 | 0.8~5ms，丢包 ≈ 0 | §3 实测 + `WEB-VIEWER-LATENCY-GAP-20261123.md:104`（回环丢包≈0，HOL 不咬人） |
| UDP 理论可省 | **≈ 0~2ms**（只剩建连/头开销差异） | `BROWSER-UDP-FEASIBILITY-20261124.md §5.1`（实测传输 0.8~5ms 里 TCP 重传/HOL 部分≈0） |
| UDP 实付成本 | HTTPS + 有效证书（WebTransport 硬门槛）+ QUIC/HTTP3 服务端栈（`HttpListener` 不支持）+ .NET 无第一方 WebTransport 服务端 | `BROWSER-UDP-FEASIBILITY §2.5/§2.6`、`TRANSPORT-ADAPTIVE-DESIGN §1.2`【官方文档】 |
| 净收益 | **≈ 0，甚至为负**（握手/证书前置成本 > 省下的 <2ms） | 同上 |

**明确结论：用户当前局域网场景换 UDP/WebTransport 省不了 233ms 里的任何可观部分（<2ms）。预算不要花在 UDP 上。**

### 6.2 未来公网/穿透场景（决策树已就绪，届时再启用）

- 公网实测参考：同城腾讯云 RTT 10ms（§3）。按 `WAN-MOBILE-TRANSPORT-DECISION-20261124.md §2.2` 模型：RTT>20ms 且丢包>0.5% 时 TCP HOL 才成为主延迟来源，WebTransport datagram 才有量级收益；
- 穿透工具对 UDP 的支持差异（ngrok 免费/CF Tunnel 免费/花生壳不透 UDP → WebTransport 直接建不起来）是硬约束；
- 落地路径按 `TRANSPORT-ADAPTIVE-DESIGN-20261124.md`（能力协商 + 800ms 探测 + 三级静默回退），但**触发条件是产品进入公网有损部署，不是当前**。

---

## 7. 证据来源清单

| 来源 | 位置 | 用途 |
|---|---|---|
| `Relay/AgentWebServer.cs:437-525` | 源码 | WriteStreamAsync 全路径、每帧 Flush、framed 前缀 |
| `Relay/AgentWebServer.cs:472-476` | 源码 | SendChunked/KeepAlive |
| `Relay/SessionRecorder.cs:26-33, 95-103, 815-819, 1211-1231, 1240-1245` | 源码 | viewer 队列/深度/丢旧保新/queueAge/事件唤醒 |
| `Services/WebPages.cs:1011-1015, 1374-1381, 1498-1546` | 源码 | 浏览器直连 base、fetch 流式读、framed 切帧 |
| `Services/WebDirect.cs:62-74` | 源码 | agent 路径白名单 |
| `Services/WebServer.cs:350-363` | 源码 | 主程序路由表（无 /stream，证直连） |
| A2 agent 日志 `C:\Users\A2\AppData\Local\Temp\soullink-relay-27656.log`（2026-11-24 10:33） | 真机日志 | queueAge=0/Max=18、实际 ~18fps、staticSkipped=624 |
| 本会话 TcpClient/curl/ping 实测 | §3 表格 | 回环/LAN/公网 RTT |
| MS 官方 HttpListener API 页 | learn.microsoft.com/dotnet/api/system.net.httplistener | 无 Nagle/NoDelay/Socket 暴露（无应用层开关） |
| `LATENCY-BENCHMARK-20261123.md:33` | 仓库 | /live 0.8ms p95 4.6ms |
| `E2E-LATENCY-BASELINE-20261124.md:36, §6` | 仓库 | 传输段 0.8~5ms、queueAge A/B 实测 |
| `BROWSER-UDP-FEASIBILITY-20261124.md` | 仓库 | UDP 收益≈0 决策树、WebTransport HTTPS 硬门槛 |
| `WAN-MOBILE-TRANSPORT-DECISION-20261124.md` | 仓库 | 公网/穿透场景决策 |
| `TRANSPORT-ADAPTIVE-DESIGN-20261124.md` | 仓库 | 自适应架构（P0 骨架） |
| `HANDOFF-20261124-LATENCY-OPTIMIZED.md §5` | 仓库 | 231ms 真机体感记录（差分口径来源之一） |

---

## 8. 待验证清单（不猜）

| 项 | 状态 |
|---|---|
| http.sys 对 chunked Flush 的内核发送时机（是否有隐藏缓冲） | 待实测（Wireshark 抓回环 /stream 每帧到达间隔 vs 服务端 lastNalAt） |
| 浏览器 fetch 收流是否引入固定缓冲 | 待实测（rVFC 埋点，`AUDIT-20261124-05` P2） |
| 233ms 测量时段的真实帧供给节奏（18fps 是 10:33 快照，需与用户测量时段对齐复测） | 待实测 |
| framed 4B 前缀独立 Write 是否被 http.sys 单独成段（Nagle 尾段理论罚） | 待实测（抓包可证，量级预判 ≤1 RTT） |
| RDP 侧自身延迟绝对值 | 未测（差分口径下不需要，绝对口径下需要） |

---

## 9. 边界遵守自查

未改任何产品代码/脚本/配置/版本号；未碰 `dist\stable\SoulLink-Stable.exe`（PID 13392）；未使用/控制 A1（session 2）/A4（session 3）账号；未按名字杀任何进程（全程零进程操作）；未弹窗；未改注册表；未发布、未碰 COS/latest.json；未执行任何 git 操作。网络测量全部为只读连接（TCP 握手即断、ICMP ping、curl GET 静态页），未对 19099/agent 做任何 POST。临时输出仅控制台，无文件残留。唯一写入 = 本文档 + README 登记一行。
