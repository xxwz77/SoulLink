# 传输层自适应架构设计：自动探测 UDP 可用性并自动切换 WebTransport / TCP

> 日期：2026-11-24
> 作者：proto-webudp（dsh-team `team_mwtjn0d77v8gtr` / member `m_mwu0xpde4m88k0` / task `task_mwu1gnly738t6n`）
> 性质：**纯架构设计，未改任何产品代码**。未碰 `dist/stable/SoulLink-Stable.exe`，未用 A1/A4，未改注册表。
> 用户原话：*「我的自建工具可以 udp，别的用户可能只有 tcp，要能自动识别自动切换」*——本设计**否定手动选协议**，实现软件自判、用户零配置。
> 承接：`BROWSER-UDP-FEASIBILITY-20261124.md`（API 全景）、`WAN-MOBILE-TRANSPORT-DECISION-20261124.md`（场景决策 + 穿透 UDP 支持差异）。
>
> **证据分级**：【官方文档】＝frp/ngrok/CF/Tailscale/.NET/MDN 官方文档；【代码事实】＝当前源码 file:line；【实测】＝本仓库前序实测；【推断】＝有依据未实测；【查不到】＝无一手来源。

---

## 0. 一句话结论

**做成「能力协商 + 短超时探测 + 静默回退」三层自适应，用户零配置。但有一个必须先说清的可行性事实：.NET 11 有稳定的 `System.Net.Quic`（QUIC 底层）和 Kestrel HTTP/3，却没有第一方的 WebTransport 服务端协议实现（MDN 官方的服务端库列表是 Go/Python/Rust/Node/Deno，没有 .NET）。** 因此「WebTransport」这条最优 UDP 路径在 .NET 侧要么自己实现 WebTransport-over-HTTP/3 协议（重），要么退而求其次走「QUIC 自定义流」（快但非标准 WebTransport、浏览器 JS 不能直接用 WebTransport API）。**结论：P1 的自适应框架先落地（能力协商 + 探测 + 回退 + 传输抽象层），UDP 具体承载分两档——P1 用「WebSocket 分帧（TCP）+ 自建 UDP 探测面」先把「自动识别自动切换」的骨架和体验做出来；P2 再根据「是否需要真 WebTransport」决定是引第三方 QUIC/WebTransport 服务端还是自实现。** 这样既满足「自动识别自动切换」的产品诉求，又不被「.NET 无 WebTransport 服务端」卡死。

---

## 1. 可行性事实核查（决定 P1 能不能做的关键）

### 1.1 现状（代码事实）

| 项 | 现状 | 证据 |
|---|---|---|
| 服务端 | `HttpListener`，`http://+:<port>/`，HTTP/1.1 明文，无 TLS 栈 | `Relay/AgentWebServer.cs:83-86` |
| 视频面 | `/stream` chunked HTTP（`SendChunked` + 逐 NAL `WriteAsync+Flush`） | `AgentWebServer.cs:449-464` |
| 输入面 | `/api/input` 每事件 POST + 已新增 `/ws/input`（WebSocket，`AcceptWebSocketAsync` 原生可用） | `AgentWebServer.cs:327-329`；`WebDirect.cs:70` |
| 控制面 | `/api/idr`（按需关键帧）+ `/api/info`（含 `streamVersion=2`） | `AgentWebServer.cs:264-285, 321` |
| 框架 | `net11.0-windows`（主程序 + agent） | `SoulLink.csproj:4` |

### 1.2 .NET 11 下可用的方案（官方文档）

| 方案 | 是否可用 | 说明 | 证据 |
|---|---|---|---|
| **Kestrel + HTTP/3** | ✅ | HTTP/3 在 .NET 7+ 全支持；需 `HttpProtocols.Http1AndHttp2AndHttp3` + `UseHttps`；依赖 MsQuic（Windows 11/Server 2022+，msquic.dll 随 .NET 运行时分发） | 【官方文档】learn.microsoft.com aspnet/core kestrel/http3 |
| **`System.Net.Quic`（裸 QUIC）** | ✅ | .NET 9+ 稳定 API；`QuicListener`/`QuicConnection`/`QuicStream` 三件套；**只有 stream（可靠/不可靠流），无 datagram API**；`QuicListener.IsSupported` 可探测 | 【官方文档】learn.microsoft.com dotnet networking/quic |
| **WebTransport 服务端协议** | ❌ **无第一方实现** | MDN 官方列的服务端库：Go `webtransport-go`、Python `aioquic`、Rust `wtransport`、Node `@fails-components/webtransport`、Deno（unstable）——**没有 .NET**。Kestrel 的 HTTP/3 只做 HTTP 语义，不实现 WebTransport 的 extended CONNECT + 会话/流/datagram 协议层 | 【官方文档】MDN WebTransport API（server implementation） |
| **第三方 .NET WebTransport 库** | 🟡 有但需评估 | 社区存在基于 MsQuic 的 WebTransport 服务端库（如 `WebTransport` 相关 NuGet），成熟度/维护度需 P2 单独评估，本设计不假设其可用 | 【查不到·未在本机拉包验证】 |

### 1.3 关键结论

1. **能立刻做的**：`System.Net.Quic` 裸 QUIC 流（`QuicStream` 支持**不可靠流**，通过 `WriteAsync` 的 abort/RESET 语义实现「丢了就丢」）+ WebSocket 分帧（TCP）。这**不需要任何第三方库**。
2. **不能立刻做的**：标准 WebTransport（浏览器 JS 用 `new WebTransport()` 直连）。因为服务端无第一方协议实现。
3. **务实选择**：P1 的自适应骨架不绑定「必须 WebTransport」，而是抽象成「传输能力协商 + 探测 + 回退」，UDP 承载先用 `System.Net.Quic` 自定义流（浏览器侧用 `WebSocket` 无法直连 QUIC——**注意：浏览器 JS 没有裸 QUIC API**）。因此**真正的「浏览器端 UDP」在 .NET 侧仍是硬约束**：浏览器能用的 UDP 只有 WebTransport（需要服务端实现）和 WebRTC（需要媒体栈）。这个矛盾在 §6 的 P2 决策里解决。

---

## 2. 自适应架构总览

```
                    ┌─────────────────────────────────────────────┐
  浏览器(网页)        │  传输协商层 TransportNegotiator（新增）          │
  ┌────────────┐    │  1. GET /api/capabilities → 拿能力清单        │
  │ 上层：      │    │  2. 若 HTTPS 且清单含 webtransport:            │
  │ 解码/渲染/  │    │        new WebTransport(url)  → 800ms 超时   │
  │ 输入(不感知 │    │        成功 → 走 UDP 数据面                   │
  │ 传输协议)   │    │        失败/超时 → 静默回退                    │
  └─────┬──────┘    │  3. 回退 → WebSocket 二进制分帧(TCP)           │
        │           │  4. 结果上报 transport=udp|tcp 到 debug 面板  │
        ▼           └─────────────────────────────────────────────┘
  TransportAdapter 抽象接口（新增，浏览器侧 JS + 服务端 C# 各一份）
    ├─ sendFrame(nalBytes) / onFrame(cb) / sendControl(msg) / close()
    ├─ 实现 A：WebTransportAdapter（UDP）
    ├─ 实现 B：WebSocketAdapter（TCP 分帧）
    └─ 实现 C：HttpChunkedAdapter（最终兜底，现状 /stream）
```

**核心原则**：
1. **上层（解码/渲染/输入）只认 `TransportAdapter` 接口**，不感知具体协议。
2. **探测是旁路**，与主画面启动并行，探测结果只决定「首帧走哪条路」，不阻塞首帧。
3. **任何探测失败都静默**，落到 HTTP chunked / WebSocket 兜底，**绝不弹错误、绝不卡界面**。

---

## 3. 探测协议设计（具体到报文格式与超时值）

### 3.1 服务端能力清单 `GET /api/capabilities`（新增路由，加到 `WebDirect.IsAllowedPath`）

响应（JSON，`WriteJson` 已有）：

```json
{
  "ok": true,
  "transports": [
    { "id": "webtransport", "url": "https://<host>:<port>/wt", "available": true },
    { "id": "websocket",    "url": "ws://<host>:<port>/ws/stream", "available": true },
    { "id": "http-chunked", "url": "http://<host>:<port>/stream",  "available": true }
  ],
  "secure": false,
  "token": "<一次性探测票据，30s 有效>",
  "streamVersion": 2
}
```

- `secure`：是否 HTTPS（**false 时浏览器直接跳过 WebTransport，省 800ms**——这是「HTTP 就直接跳过 WT」的落地）。
- `token`：探测用一次性票据，避免把长效口令放进 URL（安全收口，见 `WAN-MOBILE-TRANSPORT-DECISION §6` 的「外网改一次性票据」）。
- `webtransport.url` 只在服务端**真的起了 WebTransport/QUIC 监听**时才非空；否则 `transports` 里根本没有 `webtransport` 项。

### 3.2 客户端探测时序（超时值硬编码）

```
页面加载
  ├─ (并行) GET /api/capabilities   ── 1500ms 超时，失败 → 直接用 http-chunked
  │
  ├─ (并行) 起 HTTP chunked /stream 兜底连接（先保证有画面，不等探测）
  │
  └─ capabilities 返回后：
       if (!secure || 无 webtransport 项)  → 停在 chunked/ws，结束
       else:
         try { new WebTransport(url); await Promise.race([wt.ready, timeout(800ms)]) }
         catch/超时 → 停在 ws/chunked（静默，记 transport=tcp）
         成功 → 切换：断 chunked → WebTransport datagram 收帧
```

**关键超时值**（全部硬编码，不给用户配置，保证「无感」）：

| 项 | 值 | 理由 |
|---|---|---|
| `/api/capabilities` 超时 | 1500ms | 覆盖慢网络，失败即走兜底 |
| WebTransport 探测超时 | **800ms**（用户明确要求） | QUIC 握手 1-RTT 通常 <200ms，800ms 足够且不拖首帧 |
| 探测票据有效期 | 30s | 防重放，一次性 |
| 运行中 WT 断开重试 | 3 次后退回 TCP，冷却 30s | 防「半坏 UDP」反复横跳 |
| 切换后重新探测间隔 | 30s | 网络恢复后自动升级回 UDP |

### 3.3 报文格式（统一帧信封，三态共用）

**不管走 WebTransport datagram / WebSocket 二进制帧 / HTTP chunked，都用同一个帧信封**（这是「上层不感知协议」的关键）：

```
帧信封（小端）：
  [0]      version = 2
  [1]      type: 0x01=视频帧  0x02=控制(请求IDR/丢帧上报)  0x03=心跳
  [2..5]   uint32 payloadLength
  [6]      flags: bit0=关键帧(IDR) bit1=帧尾 bit2=丢帧上报
  [7]      reserved
  [8..]    payload（NAL 字节，或控制 JSON）
```

- **帧边界 = 消息边界**：WebTransport datagram / WebSocket 每消息一条，天然分帧，消除现状「chunk 边界≠帧边界」的拼包税（`AUDIT-20261124-05 §2.7`）。
- 视频帧用 `type=0x01` + `flags.bit1=帧尾` 标记一帧结束；控制帧走 `type=0x02` 承载「客户端请求 IDR」——对应 `LOW-LATENCY-DEEP-DIVE §7 #1/#2` 的两件 P0 事，正好一起落地。
- **丢帧语义**：UDP（datagram）下丢帧不重传，服务端配「应用层 FEC 或按需 IDR 兜底」；TCP（WebSocket/chunked）下可靠但受 HOL。

---

## 4. 四种玩家场景的完整决策树

| 场景 | 服务端能力 | 客户端探测结果 | 最终路径 | 用户感知 |
|---|---|---|---|---|
| ① 自建工具 + 支持 UDP（用户自建中转） | `webtransport` 项存在 + secure | `new WebTransport` ready <800ms | **WebTransport datagram（UDP）** | 最优，零配置 |
| ② frp/Tailscale（支持 UDP） | 同上（穿透透传 UDP） | ready <800ms | **WebTransport datagram（UDP）** | 最优 |
| ③ ngrok免费/CF免费/花生壳（只 TCP） | 无 `webtransport` 项（服务端起不了 QUIC 监听，因为穿透不透 UDP） | 直接跳过 WT | **WebSocket 分帧（TCP）**，若无 ws 则 HTTP chunked | 自动回退，无感 |
| ④ 纯 LAN | `secure=false`（http:// 明文） | 因非 HTTPS **直接跳过 WT**（省 800ms） | **HTTP chunked 或 WebSocket（TCP）** | LAN 下 TCP 已够 |

**决策树伪码（客户端）**：

```
transport = pick(capabilities):
  if capabilities 请求失败:            return http-chunked          # 最稳兜底
  if not secure:                       return websocket ?? chunked  # 非HTTPS直接跳过WT
  if 'webtransport' not in transports: return websocket ?? chunked  # 穿透不透UDP
  try WT ready (800ms):  success → return webtransport
  except/timeout:        return websocket ?? chunked               # 静默回退
```

**服务端侧判断「要不要起 QUIC/WebTransport 监听」**：由 `System.Net.Quic` 的 `QuicListener.IsSupported` + 是否配了证书决定（【官方文档】`QuicListener.IsSupported`）。起不来就在 `/api/capabilities` 里不返回 `webtransport` 项——**这是「穿透只 TCP」自动被识别的机制**（穿透不透 UDP → QUIC 监听收不到握手 → 但更简单的是：配置里没有证书/没有 QUIC 监听就默认不宣告）。

---

## 5. 服务端改造清单（HttpListener → 需要什么）

| # | 改动 | 内容 | 阶段 | 工作量 |
|---|---|---|---|---|
| 1 | **传输抽象层（C#）** | 定义 `ITransport` 接口（`SendFrame`/`OnFrame`/`SendControl`），把 `SessionRecorder` 的 viewer 队列接进去，与具体协议解耦 | P0 | 1~2 天 |
| 2 | **WebSocket 视频面 `/ws/stream`** | 用 `AcceptWebSocketAsync`（原生可用，`STREAM-PERF-AUDIT §4.4` 已证）+ 帧信封二进制分帧，替代/并行 chunked | P0 | 1~1.5 天 |
| 3 | **`/api/capabilities` 路由** | 返回能力清单 + 一次性探测票据 + `secure` 标志 | P0 | 0.5 天 |
| 4 | **QUIC 监听（UDP 承载）** | `System.Net.Quic` 起 `QuicListener`（自签证书 + `serverCertificateHashes` 或受信证书），承载自定义流（**注意：这是自定义 QUIC 流，不是标准 WebTransport，浏览器 JS 不能直连**） | P1 | 3~5 天 |
| 5 | **标准 WebTransport 服务端** | 引第三方 .NET WebTransport 库，或自实现 extended CONNECT + 会话/流/datagram 协议层（重） | P2 | 待评估（引库 1~2 周；自实现 3~6 周） |
| 6 | **可观测性** | `/api/info` 增 `transport` 字段 + 服务端日志 `[transport] path=udp|tcp|chunked` | P0 | 0.5 天 |

**关键风险**：`HttpListener` 不支持 HTTP/3/QUIC，所以 #4/#5 需要**新增一个独立的 QUIC/Kestrel 监听进程或协程**，与现有 `HttpListener` 并存（不是替换）。这保持「故障隔离」——QUIC 挂了，chunked 照常。

---

## 6. 客户端改造清单（网页端 capability 探测逻辑）

| # | 改动 | 内容 | 阶段 | 工作量 |
|---|---|---|---|---|
| 1 | **`TransportAdapter`（JS）** | 抽象接口 + 三实现：WebTransportAdapter / WebSocketAdapter / HttpChunkedAdapter | P0 | 1~2 天 |
| 2 | **探测器** | §3.2 时序：并行起 chunked 兜底 + 拉 capabilities + 800ms WT 探测 + 静默回退 | P0 | 1 天 |
| 3 | **帧信封解析** | 三态统一帧信封（§3.3），替换现状 `splitAnnexB` 的「逐字节扫起始码」 | P0 | 1 天 |
| 4 | **控制帧** | 用帧信封 `type=0x02` 承载「请求 IDR / 丢帧上报」，落地 `LOW-LATENCY-DEEP-DIVE §7 #2` | P0 | 0.5 天 |
| 5 | **debug 面板** | `#fpsinfo` 增显示 `transport: udp|tcp|chunked`，供排障 | P0 | 0.5 天 |
| 6 | **网络切换自适应** | 监听 `visibilitychange`/网络变化，重探测（QUIC 支持连接迁移，WT 可无缝；TCP 需重连） | P2 | 1 天 |

---

## 7. 降级链路与用户体验保障（★ 探测失败不能让用户看到错误）

**五条硬规则**：

1. **首帧永不阻塞探测**：页面加载即并行起 HTTP chunked `/stream` 兜底，探测是旁路。用户永远先看到画面（哪怕走最慢的 chunked），再有机会升级到 UDP。
2. **探测失败 = 静默**：任何 `catch` / 超时都只 `console.debug`（不是 `warn`），UI 状态栏只显示最终 `transport: xxx`，不弹任何对话框、不闪提示。
3. **三级降级链**：`WebTransport(UDP)` → `WebSocket(TCP 分帧)` → `HTTP chunked(现状)`，逐级兜底，绝不「探测失败就没有画面」。
4. **运行中降级**：WT 连接运行中断开（穿透抖动/UDP 被墙）→ 3 次重试失败 → 自动切到 WebSocket/chunked，**画面冻结一帧 + 静默重连**（复用现有 `scheduleReconnect` 退避），不弹「连接断开」。
5. **运行中升级**：冷却 30s 后重探测，网络恢复自动升回 UDP（用户无感）。

**「HTTP 而非 HTTPS 直接跳过 WT」**：`/api/capabilities` 返回 `secure:false`，客户端 `if(!secure) skip WT`——省掉 800ms 空等，且符合 `BROWSER-UDP-FEASIBILITY §2.5`（WT 强制 HTTPS）的结论。

---

## 8. 抽象传输层接口（上层不感知协议）

### 8.1 C# 侧（服务端）

```csharp
public interface IStreamTransport : IAsyncDisposable
{
    // 视频帧（NAL 或帧信封）；返回 false = 该传输已不可用（触发上层降级）
    ValueTask<bool> SendFrameAsync(ReadOnlyMemory<byte> frame, bool isKeyFrame, CancellationToken ct);
    // 控制帧（请求 IDR / 丢帧上报等）
    ValueTask SendControlAsync(string json, CancellationToken ct);
    // 客户端断开/可观测
    event Action? Closed;
    string Kind { get; }   // "webtransport" | "websocket" | "chunked"
}
```

`SessionRecorder` 的 viewer 队列从「直接 `WriteAsync+Flush` 到 `ctx.Response.OutputStream`」改为「写进 `IStreamTransport`」。这是 P0 #1 的核心改动，其余协议（QUIC/WS/chunked）只是 `IStreamTransport` 的三个实现。

### 8.2 JS 侧（客户端）

```js
class TransportAdapter {
  async start() {}          // 建立连接
  async sendFrame(bytes) {} // 发帧（视频/控制）
  onFrame(cb) {}            // 收帧回调（上层解码）
  close() {}
  get kind() {}             // 'webtransport'|'websocket'|'chunked'
}
```

`startStream()` 现有逻辑（`WebPages.cs:1185` 起）改为「先起 chunked 兜底 → 探测 → 成功则 `adapter = new WebTransportAdapter()` 切换」，解码/渲染部分不动。

---

## 9. 可观测性（debug 面板 + 日志）

| 层 | 手段 | 内容 |
|---|---|---|
| 网页 debug 面板 | `#fpsinfo` 追加一行 | `transport: udp|tcp|chunked` + `probe: pass|skip|fail` + `switch: N 次` |
| 服务端日志 | `RelayAgent.LogQuiet("[transport] ...")` | `[transport] listen quic=false(no-cert)`、`[transport] session udp viewer=1`、`[transport] downgrade udp→tcp reason=probe-timeout` |
| `/api/info` | 增字段 | `transport`（当前路径）、`transportSwitchCount` |

目的：用户报「卡」时，一眼看出走的是 UDP 还是 TCP，快速排除「穿透没透 UDP 导致退回 TCP」这类误判。

---

## 10. 分阶段落地计划与工作量

| 阶段 | 内容 | 关键决策/理由 | 工作量 |
|---|---|---|---|
| **P0（立即做，骨架 + 体验）** | ① 传输抽象层（C# + JS）② `/ws/stream` WebSocket 分帧 ③ `/api/capabilities` 能力协商 ④ 帧信封 ⑤ 三级降级链 + 静默回退 ⑥ 可观测 | **先把「自动识别自动切换」的骨架和兜底体验做出来**，UDP 承载先留空（`webtransport` 项暂时不宣告）。收益：帧边界语义 + 按需 IDR 回传 + 输入 WebSocket 三件 P0 事一起落地 | **1~1.5 周** |
| **P1（UDP 承载，先不绑定 WebTransport）** | ① `System.Net.Quic` 起 QUIC 监听 + 自定义流承载视频 ② 浏览器侧……**见下方 P1 决策** | **这是本设计的最大难点**：浏览器 JS 没有裸 QUIC API，只有 WebTransport/WebRTC 两个 UDP 封装。若用 `System.Net.Quic` 自定义流，浏览器端接不上。故 P1 必须二选一 | 见下 |
| **P2（标准 WebTransport，可选）** | ① 引第三方 .NET WebTransport 库或自实现协议层 ② 浏览器 `new WebTransport()` 直连 ③ 网络切换自适应 | 只有当「真 UDP 到浏览器」成为硬需求时才做 | 引库 1~2 周 / 自实现 3~6 周 |

### 10.1 P1 的 UDP 承载决策（关键，给出明确建议）

浏览器端能用的 UDP 封装只有两个：**WebTransport**（需服务端实现）和 **WebRTC DataChannel/媒体轨道**（需信令 + 媒体栈）。.NET 侧两者都没有第一方实现。因此 P1 有两条路：

- **路 A：P1 直接上 WebRTC DataChannel（推荐）**——因为浏览器原生 `RTCPeerConnection` + `createDataChannel`（Baseline Widely available，`BROWSER-UDP-FEASIBILITY §3`），服务端用 `.NET 无现成 WebRTC 栈`是已知硬成本（`REMOTE-ACCESS-OPTIONS §4c`），但这是浏览器 UDP 的「标准答案」。
- **路 B：P1 上 WebTransport（引第三方 .NET 库）**——若社区库成熟（需 P2 评估），比 WebRTC 少了 ICE/STUN/TURN 信令，更契合「服务端直连」架构。

**本设计建议**：**P1 先不硬上 UDP**。因为「自动识别自动切换」的产品价值在 P0 已经 80% 落地（能力协商 + 探测 + 三级降级 + 无感）。UDP 承载（WebTransport 或 WebRTC）是 P1/P2 的「增量提速」，应等 P0 骨架验证 + 第三方 .NET 库评估后再定，**避免在没有第一方 WebTransport 服务端的情况下重造轮子**。

---

## 11. 与既有文档的衔接（不重复、不矛盾）

- `BROWSER-UDP-FEASIBILITY-20261124.md`：本设计 §1 直接引用其「WebTransport 强制 HTTPS / 无 .NET 服务端库」的结论，落地为「`secure=false` 跳过 WT」+「无 `webtransport` 项即不宣告」。
- `WAN-MOBILE-TRANSPORT-DECISION-20261124.md`：本设计 §4 的四种场景决策树 = 该文档决策矩阵的「自动版」；§3.1 的「一次性探测票据」落地其「外网改一次性票据」安全建议。
- `LOW-LATENCY-DEEP-DIVE-20261124.md` §7 #1/#2（帧边界 + 按需 IDR）：本设计 §3.3 帧信封 + §6 #4 控制帧 = 这两件 P0 事的具体协议载体。

---

## 12. 证据来源清单

| 来源 | URL | 用途 |
|---|---|---|
| ASP.NET Core Kestrel HTTP/3（官方） | https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/http3 | §1（HTTP/3 支持 + MsQuic 依赖） |
| .NET QUIC protocol（官方） | https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-overview | §1（System.Net.Quic 只有 stream 无 datagram、IsSupported） |
| MDN WebTransport API | https://developer.mozilla.org/en-US/docs/Web/API/WebTransport_API | §1（无 .NET 服务端库、强制 HTTPS） |
| MDN RTCDataChannel / createDataChannel | https://developer.mozilla.org/en-US/docs/Web/API/RTCDataChannel | §10（WebRTC 是浏览器 UDP 标准答案） |
| frp / ngrok / CF / Tailscale 官方 | 见 `WAN-MOBILE-TRANSPORT-DECISION-20261124.md §7` | §4（穿透 UDP 支持差异） |
| 本仓库 `AgentWebServer.cs` / `WebDirect.cs` / `SoulLink.csproj` | Relay/ Services/ | §1（现状 file:line） |
| 本仓库 `AUDIT-20261124-05-WEB-CLIENT-GAP.md` / `STREAM-PERF-AUDIT-20261122.md` | docs/research/ | §5（WebSocket 原生可用、拼包税） |

---

## 13. 边界遵守自查

本任务为**纯架构设计**，未改任何产品代码/脚本/配置；未碰 `dist/stable/SoulLink-Stable.exe`（PID 13392）；未使用/控制 A1/A4；未改注册表；未重启任何进程。唯一写入为本研究文档（`docs/research/TRANSPORT-ADAPTIVE-DESIGN-20261124.md`）。所有代码事实来自只读 `read`/`grep`，行号已核对。
