# 远程访问「更优方案」横向对比（REMOTE-ACCESS-OPTIONS）

> 日期：2026-11-23
> 作者：deploy-fix（agent-team / task `task_mws3zcsf2rbar3`）
> 性质：**纯调研 + 结论**，不新增任何 C# 业务代码、不改 csproj、不碰 Relay/Services/docs/web-index.html、不碰 dist/stable/、不跑重负载。
> 前情：原「安卓可行性普查」已中止；本文件承接其已实测的**现状基线**（工具链 / 端口 / 鉴权 / 协议就绪度），回答一个新问题——**「要不要做 APP」，以及有没有比自研 APP 更优的路**。
> 关键外部来源：caniuse（VideoDecoder 支持表，2026-10 数据）、MDN WebCodecs 文档、Sunshine 官方文档（LizardByte）、RustDesk 官方文档。均 2026-11-23 抓取。

---

## 0. 一句话结论（先看这里）

**「做不做 APP」是个伪问题 —— 当前真正的瓶颈既不是 APP 还是网页，而是 `/stream` 这条传输通道本身（裸 chunked HTTP + Annex-B，无拥塞控制、无重传/FEC、15fps 硬编码），以及颜色链路的一个未定位 bug。** 无论 PWA、WebView 壳、还是原生 Kotlin，都架在同一条 `/stream` HTTP 端点之上；不先解决传输与颜色，换哪个客户端形态都只是换皮。

**推荐排序**（理由见 §5）：

| 排名 | 路线 | 结论 |
|---|---|---|
| **1** | **PWA / 纯 Web 增强（a）** | ✅ **立即做**。零安装零分发，复用已就绪的 WebCodecs H.264 播放器；移动端支持度已达标（iOS 16.4+，已实测查证） |
| **2** | **WebRTC（c）** | ✅ **中期做**。它是「延迟/丢包/NAT」的**本质解法**，但落在**服务端（C# 代理）**，与「APP 还是 PWA」正交；浏览器原生支持 WebRTC，所以仍可先跑在 PWA 里 |
| **3** | **原生壳复用网页（d）** | 🟡 **暂缓**。Android 上相对 PWA 增益有限，等网页跑稳再包壳；iOS 壳无意义（PWA 已够） |
| **4** | **现成开源串流复用（b）** | ❌ **不推荐直接套**。Moonlight 吃不了我们的 Annex-B HTTP 流（需 GameStream 协议）；Sunshine 整套替换 = 放弃自研栈 + GPL-3.0 许可风险 + 不解决多账号 RDP |
| **5** | **第三方产品兜底（e）** | ❌ **不构成替代**。用户要的是「多账号挂机 + 每账号独立注入」，ToDesk/向日葵/RustDesk 都是单桌面远程，无挂机/注入语义；仅可作「纯看桌面」的临时兜底 |

---

## 1. 现状基线（承接安卓普查，代码已核对到 2026-11-23）

### 1.1 端口分配 —— 设计文档一致 ✅

`Services/WebDirect.cs:45-53` `PortFor = basePort + accountIndex + 1`（**有 +1**）。历史交接曾写错为「无 +1」，实际代码与 `tools/layout-check/WebDirectCheck.cs` 的断言一致：第 0 个账号 = base+1。主程序 web 用 `WebPort`（配置值），代理直连用 `WebPort + 序号 + 1`。

### 1.2 鉴权 —— 两套，别混 ✅

| 端口 | 鉴权 | 证据 |
|---|---|---|
| 主程序 `WebPort` | **Cookie**（`soullink_sid`） | `Services/WebServer.cs:103-114` `IsAuthorized` + `:116-129` `IssueSession` |
| 代理 `directPort` | **token**（`X-SoulLink-Token` 头 或 `?token=` 查询串） | `Relay/AgentWebServer.cs:310-323` `CheckToken` |

`/api/sessions`（`WebServer.cs:726-875`）在 `directPort`（`:824`）与 `directToken`（`:866-874`）下发。**任何客户端都必须维护两套 base URL + 两套鉴权**——这是设计文档早就写对的，代码现状一致。

### 1.3 三类能力就绪度（这是「APP 能不能做」的地基）

| 能力 | 服务端现状 | 证据 | 判定 |
|---|---|---|---|
| `/stream`（H.264 推流） | chunked HTTP，`Content-Type: video/h264`，Annex-B NAL 直接写出，**15fps 硬编码**，单客户端，30 秒上限 | `Relay/AgentWebServer.cs:346-442`（`SendChunked` :406、15fps `Task.Delay(66)` :429） | ⚠️ **有缺陷**：无拥塞控制/重传/FEC，丢包画面花；帧率写死 |
| `/live`（JPEG 逐帧） | 单帧 `image/jpeg`，从内存 Ring 直取 | `Relay/AgentWebServer.cs:331-348` | ❌ **用户已拒绝**（「我不要图片live，太卡了」） |
| `/api/input`（键鼠注入） | click/down/up/move/wheel/key + **keydown/keyup/moverel/text 已全部实现** | `AgentWebServer.cs:534-567`（switch）+ `Relay/RelayAgent.cs:749-871`（执行）+ `Services/InputActions.cs:73-113`（Unicode 校验/拆分） | ✅ **已就绪**（比设计文档超前：文档里的「必须新增」四项已落地） |

> ★ 重要更正：`MOBILE-CONTROLS-DESIGN §7` / `MOBILE-REMOTE-PLAN §4.5` 里写的「`text`/`keydown`/`keyup`/`moverel` 是后端缺口、必须新增」**已经过时**——stream-core 的当前代码已把这四个动作全部实现并接线（含 `PressedKeys` 断线释放 `RelayAgent.cs:108/317-326/619`、`KEYEVENTF_UNICODE` `RelayAgent.cs:170/232-238`、代理对成对发送 `InputActions.cs:95-112`）。**安卓/手机端的「操作」后端前提已满足。**

### 1.4 网页播放器现状（web-player 已改）

`docs/web-index.html` 已改为 **WebCodecs 解码 H.264 /stream**：`VideoDecoder` + `EncodedVideoChunk`（:709-710、:921）、**从 SPS 动态解析 codec 字符串**（:802-838 的 `avcCodecFromSps`，修掉了旧版硬编码 `avc1.4D4028` 的 profile/level 不匹配）、annexb 模式切 NAL（:1003-1009）。**即「PWA 看 H.264」的前端主体已经存在。**

---

## 2. 痛点清单（决策的锚点）

| # | 痛点 | 现状定位 |
|---|---|---|
| ① | 画面不是黑白 | ✅ **已定位 + 已修复（待端到端验证）**。根因是旧 `BgraToNv12` 末行把整个 UV 平面填 128（`U=V=128` = 色度归零 = 黑白）；现代码已改为正确的 2×2 降采样 BT.601（`Relay/AgentWebServer.cs:476-497`，`for y+=2` / `for x+=2` 取四像素平均算 U/V 交错写入）。由 Lead 定位、stream-core 修复。 |
| ② | 不要 JPEG 逐帧 | 🟡 **传输层是 H.264，但采集侧仍在每帧编 JPEG、推流侧再解开**。实时链路仍为「SessionRecorder 环形缓冲存 JPEG（`SessionRecorder.cs:115/229/258`）→ /stream 取 JPEG（`AgentWebServer.cs:374-413`）→ 解码 BGRA → 转 NV12 → 编码」。已有 P1 任务（task_mws2jkdm1av20hw）承诺下线，含环形缓冲改存 NAL。 |
| ③ | 延迟要跟手 | ❌ 未解决：HTTP 串行 + 15fps 硬编码 + 无拥塞控制 |
| ④ | 服务端 CPU 要低 | 🟡 部分：WGC+硬编已比 BitBlt+JPEG 省 24 倍（交接数据）；但每帧 JPEG→NV12 软转仍浪费（`AgentWebServer.cs:445-499`） |
| ⑤ | 手机/外网能连 | 🟡 局域网可（`http://+` 全网卡绑定）；外网 NAT/CGNAT 未解决，且安全门槛未收口（见 §6 专节：空令牌 `CheckToken:312` 放行、CORS `*` `:214`、防火墙无 remoteip `WebFirewall.cs:44-47`） |

---

## 3. 关键事实查证（能实测的实测，其余标来源）

### 3.1 WebCodecs 移动浏览器支持度 —— 已查证 ✅

来源：caniuse `VideoDecoder API`（2026-10 StatCounter 数据）+ MDN。

| 平台 | 支持 | 版本 |
|---|---|---|
| iOS Safari / Safari on iOS | ✅ | **16.4+**（2023-03） |
| Chrome / Edge | ✅ | 94+ |
| Chrome for Android / Samsung Internet | ✅ | 已支持 |
| Firefox | ✅ | 130+ |
| **Firefox for Android** | ❌ | 至今不支持（caniuse 标注） |

**结论**：iOS 的 WebCodecs 支持**早已达标**（不是传言中的「iOS 不支持」），PWA 的 H.264 硬解路径在 iOS 16.4+ 与 Android Chrome 上都是通的。**唯一明显缺口是 Firefox for Android**（用户群很小，可忽略或降级到提示页）。

### 3.2 我们的 `/stream` 输出形态 —— 已核对 ✅

`AgentWebServer.cs:399` `Content-Type: video/h264` + `:406 SendChunked=true` + `:419` 注释「当前 MFT 已输出 Annex-B，直接写出」。**即：裸 Annex-B NAL 流（起始码分界），无容器封装、无长度前缀、无 SDP/协商。**

**谁能吃 Annex-B H.264 裸流**（据 MDN WebCodecs 与 Moonlight 架构）：
- ✅ **WebCodecs**：`VideoDecoder` 的 **annexb 模式**专门吃 Annex-B（`format: 'annexb'`，`description` 缺省 → 起始码解析）——这正是 web-index.html 已实现的方式。
- ✅ **Android MediaCodec**：`MediaCodec` 配 `csd-0/csd-1`（SPS/PPS）后可吃 Annex-B 裸流（Moonlight Android 同款架构，见 `VIDEO-STREAM-RESEARCH §7.2`）。
- ❌ **Moonlight**：走的是 **GameStream 兼容协议**（RTSP 握手 + RTP/UDP 视频，见 `STREAM-SURVEY §1` 表），**不认裸 Annex-B HTTP 流**。不能直接把 `/stream` 塞给 Moonlight。
- ❌ **ExoPlayer/Media3**：需要完整容器（MP4/HLS/DASH），不认裸 Annex-B（`ANDROID-APP-DESIGN §3.2` 已写）。

### 3.3 Moonlight 能不能吃非 NVENC 的 H.264 源 —— 已查证 ✅

来源：Sunshine 官方文档（LizardByte，2026-11-23 抓取）。

**Moonlight 本身是纯客户端**，只解码 H.264/HEVC/AV1，不管服务端用什么编码器。它连的是 **Sunshine**（或已停服的 NVIDIA GameStream）。Sunshine 的编码器表明确支持 **Media Foundation**（Windows）与 **Software**（软件编）——**所以 Moonlight 吃的 H.264 不要求 NVENC**。但前提是**服务端实现 GameStream 协议**。我们当前 `/stream` 是 HTTP Annex-B，**协议不匹配，Moonlight 无法直接接入**。

> 结论：要复用 Moonlight 客户端，只有两条路：① 在 C# 侧**自实现 GameStream 协议栈**（RTSP+RTP/UDP+FEC，工程量大）；② **整套换用 Sunshine** 当服务端（放弃 WGC+MF 自研栈，且 Sunshine 面向 **console 会话**，与 SoulLink「每账号一个 RDP 会话」的多开架构冲突——见 §4）。

### 3.4 RustDesk 在 RDP 里怎么抓 —— 承接既有研究（标注来源）

`STREAM-SURVEY §1`（本仓库，已核源码）：RustDesk 抓屏 = **DXGI Desktop Duplication（主）+ GDI BitBlt 回退**，**无 WGC 后端**。它能在 RDP 里跑，靠的是 **GDI 回退分支**——与我们 BitBlt 同级，慢。`VDD-RESEARCH §5.2`：RustDesk 的 IddCx 虚拟显示器挂 **console 会话**，不能在 RDP 会话内救 GPU 抓屏。**许可证：AGPL-3.0**（比 GPL 更严，网络服务也要开源）。

---

## 4. 五条路线逐条评估

### a. PWA / 纯 Web 增强 —— ✅ 推荐（第 1）

- **原理**：现有 `docs/web-index.html` 已用 WebCodecs 硬解 `/stream`；补 manifest + service worker（可安装/离线壳）+ 触屏手势（`MOBILE-CONTROLS-DESIGN` 已定案）+ 断线重连。
- **已有基础**：WebCodecs H.264 播放器 + SPS 动态 codec（`web-index.html:709-1009`）；端口/鉴权/输入协议全部就绪（§1）。
- **需新增**：manifest.json + SW、`user-scalable=no` 防缩放、触屏三态手势（点/滑/长按拖，`VIRTUAL-CONTROLS-RESEARCH §10` 已给参数）、`?token=` 外网时改一次性票据。
- **工作量**：**小**（前端为主，1~2 周）。**零安装零分发**，改完即全端生效。
- **风险/许可**：无许可风险；`Firefox for Android` 不支持 WebCodecs（可降级提示）；iOS PWA 无 `requestFullscreen`/`beforeinstallprompt`、SW 存储会被回收（`MOBILE-REMOTE-PLAN §2`）。

### b. 现成开源串流/远程复用 —— ❌ 不推荐直接套

- **Sunshine + Moonlight**：成熟低延迟组合，但 ① Moonlight 吃 GameStream 协议、吃不了我们的 Annex-B HTTP（§3.2/3.3）；② Sunshine 面向 **console 会话**，与「多账号 RDP 挂机」架构冲突（`STREAM-SURVEY §2`）；③ **GPL-3.0**，抄代码会让 SoulLink 被迫 GPL（`OSS-REMOTE-RESEARCH §7`）。**唯一可取的**是它「无 B 帧 + 无限 GOP/按需 IDR + 丢旧帧 + FEC」的低延迟参数思想——**已被本仓库研究文档吸收**，无需再引它的代码。
- **RustDesk**：AGPL-3.0 + GDI 回退慢（§3.4），自托管还要额外跑 hbbs/hbbr 服务。
- **webrtc-streamer / OBS+NDI**：单向广播，无输入通道，不是远程控制。
- **结论**：这条「白得一个成熟安卓端」的期望**落空**——接入成本 ≈ 自实现协议或整套换栈，都不划算。

### c. WebRTC（裸 / LiveKit / Pion / mediasoup）—— ✅ 中期做（第 2）

- **原理**：RTP over UDP + NACK/PLI 重传 + 拥塞控制（GCC/BBR）+ ICE/STUN/TURN 打洞。**本质改善**延迟、丢包、NAT 穿透三件事。
- **改善幅度**：现在的 `/stream` 是 TCP chunked——TCP 有可靠传输但**队头阻塞**，丢一个包后面全卡，且无拥塞自适应；WebRTC 用 UDP + 选择性重传 + 主动丢帧，弱网/外网下延迟与花屏显著优于裸 HTTP。**定性改善是确定的**（来源：WebRTC 官方 FAQ + `STREAM-PERF-AUDIT §4.7c`），但**具体毫秒数未实测**。
- **关键判断**：**WebRTC 是服务端（C# 代理）的传输升级，与「APP 还是 PWA」正交**。浏览器原生 `RTCPeerConnection`，所以升级后**仍可先跑在 PWA 里**，不必立刻做原生 APP。`STREAM-PERF-AUDIT §4.7c` 已点出最大成本：**.NET 侧没有现成 WebRTC 媒体栈**（SIPSorcery 有信令但媒体要自己接 MF）。
- **工作量**：**大**（服务端媒体栈 + 信令 + 可选 TURN），独立工程。

### d. 原生安卓壳复用网页（WebView + 桥接）—— 🟡 暂缓（第 3）

- **原理**：Capacitor/WebView 加载同一份 `web-index.html`，桥接原生能力（软键盘调度、剪贴板、后台、沉浸式横屏）。
- **vs 纯 PWA**：Android 上多「可侧载 APK、后台续命、系统级能力」；**vs 完全原生 Kotlin**：省掉全部 UI/播放器重写（播放器仍用 WebCodecs）。
- **工作量**：**中**（壳 + 少量桥接，1~2 周）。**Android 可侧载**；iOS 上架需开发者账号 + 审核（远程控制类目敏感）。
- **结论**：是「要一个 APP」的最经济形态，但**应排在 PWA 跑稳之后**——因为它的画面播放器就是 PWA 那套，PWA 没跑通，包壳只是把 bug 包进去。

### e. 第三方远程桌面兜底（ToDesk/向日葵/RustDesk 直连 RDP）—— ❌ 不构成替代

- **原理**：第三方工具直接远程连目标会话。
- **核心矛盾**：用户的核心资产是**「多账号挂机 + 每账号独立 BetterGI/注入」**，第三方工具是**单桌面远程**，没有挂机语义、没有「账号→会话→注入」的概念，也无法读 SoulLink 的会话/账号状态。
- **ToDesk/向日葵**偏办公/运维，游戏键位映射弱（`VIRTUAL-CONTROLS-RESEARCH §2.3`）。
- **结论**：只能当「临时纯看某个桌面」的兜底，**不能替代自研链路**。若用户其实只是「想偶尔远程看一眼」，那第三方确实够——但那就意味着 SoulLink 的远程功能可以整体砍掉，这需要 Lead/用户明确，本调研不替其决定。

---

## 5. 推荐排序（给结论，不含糊）

1. **PWA 增强（a）**—— 现在做。前端主体已在（WebCodecs H.264 + SPS codec），移动端支持已达标（iOS 16.4+ 实测查证），零分发。
2. **WebRTC 传输升级（c）**—— 紧随其后、且**独立于 APP 决策**。这是解决痛点③（跟手）、⑤（外网）的正解；先跑在 PWA 里。
3. **原生壳（d）**—— PWA 跑稳后，若要「可侧载 APK / 后台」，再包 Capacitor 壳，边际成本低。
4. **不推荐（b）Sunshine/Moonlight 直接套**：协议不匹配 + 架构冲突（console vs RDP 多开）+ GPL。
5. **不推荐（e）第三方替代自研**：无挂机/注入语义，仅兜底。

**一句话给用户**：**不用现在决定做不做 APP。先把 `/stream` 的传输（WebRTC）与采集侧「JPEG→NV12→H.264」的浪费（P1 已立项）解决，PWA 先跑通「手机上流畅看 + 能点能拖」；要「像 APP」时再花一两周包个壳，路径是顺的，不会白做。**（颜色黑白 bug 已定位已修复，待端到端验证。）

---

## 6. 外网访问 + 安全收口（最大缺口，逐条风险与修复）

> 前情：`MOBILE-REMOTE-PLAN §5` 已把「代码层暴露面」与「网络层不可达」拆成两层。本节只做**具体风险定级 + 修复落点 + 外网三思路可行性**，不改代码。

### 6.1 风险一：空 token 直接放行 —— 高风险，且**默认配置就会踩中**

**代码事实**：`Relay/AgentWebServer.cs:310-323` `CheckToken`：

```csharp
private static bool CheckToken(HttpListenerContext ctx)
{
    if (string.IsNullOrEmpty(_token)) return true;   // ← :312 空令牌直接放行
    ...
}
```

**空 token 的触发场景**：`Models.cs:599` `WebPassword` 默认 `""`，`Models.cs:596` `WebDirectToAgent` 默认 `true`。即**新装/未设口令的默认状态**下：`CaptureEnv`（`RelayClient.cs:706`）把 `s.WebPassword ?? ""` 空串下发给代理 → 代理 `_token=""` → **所有 `/live`、`/stream`、`/api/input` 无鉴权开放**。

**风险多大**：局域网内 = 任何同网段设备/网页可无凭据 `fetch` 注入键鼠到**提权桌面**；一旦外网可达（见 6.3），= 公网任意主机可控。**这是最高优先级**，不是「可选增强」。

**修复建议**（给 stream-core 参考）：`WebDirectToAgent == true` 且 `WebPassword` 为空时，**拒绝启动直连代理**或**强制生成随机口令**（主程序侧 `CaptureEnv` 校验；代理侧 `CheckToken` 在 `_token` 空时改为 401 而非放行）。至少做到：`CheckToken` 空 token 不再 `return true`。

### 6.2 风险二：CORS `*` —— 中高风险，是风险一的「放大器」

**代码事实**：`Relay/AgentWebServer.cs:214` `Access-Control-Allow-Origin = "*"`。

**攻击面**：CORS `*` 意味着**任何来源的网页**（含用户无意访问的恶意站、被注入脚本的普通站）都能从浏览器发起跨源请求。若叠加 6.1 的空 token，则恶意网页无需知晓口令即可调 `/api/input`。但**前提**是：① 浏览器能路由到目标主机的局域网 IP；② token 为空（或 token 已泄露）。若 token 非空，浏览器侧脚本仍需带对 token 才能过 `CheckToken`（:315 比对 `X-SoulLink-Token`），此时 CORS `*` 的主要风险是「把已登录网页的 token 暴露给任意源读响应」——但 `directToken` 只由主程序 `/api/sessions`（Cookie 鉴权后）下发，代理侧 CORS `*` 本身不直接泄露 token。

**修复建议**：`Access-Control-Allow-Origin` 从 `*` 改为**回显请求 `Origin` 白名单**（仅本机/隧道域名）；`Access-Control-Allow-Headers/Methods` 收窄到 `X-SoulLink-Token, Content-Type` 与 `GET,POST,OPTIONS`（现 `:215-216` 已是，仅 Origin 需改）。

### 6.3 风险三：外网可达 + 防火墙无来源限制 —— 组合后才致命

**代码事实**：`AgentWebServer.Start`（`Relay/AgentWebServer.cs:75-78`）`l.Prefixes.Add("http://+:" + port + "/")` 绑**全网卡**；`Services/WebFirewall.cs:44-47` 放行规则 `dir=in action=allow protocol=TCP localport=from-to` **无 `remoteip=` 白名单**。

**攻击面**：一旦主机有公网 IP（或路由器做了端口映射 / 误开 DMZ），代理端口即全网可达；叠加 6.1 空 token = 裸奔。

**外网三条思路可行性对比**：

| 思路 | 原理 | 可行性 | 成本 | 风险 | 结论 |
|---|---|---|---|---|---|
| **① 端口映射 + HTTPS** | 路由器端口转发 + 前置 TLS 终结（反代/自签证书） | 需公网 IP（家用 CGNAT 常无）；TLS 需反代或自建证书 | 低（有公网 IP 时） | 暴露面最大，须先过 6.1/6.2 | 仅在有公网 IP + 已收口安全时用 |
| **② 反向隧道（frp / cloudflared / Tailscale）** | 出站连公网隧道，无需入站端口 | **高**，尤其 **Tailscale/ZeroTier（WireGuard）** 零运维自带加密 | frp 需一台 VPS；Tailscale 个人免费 | 低（隧道加密 + 不暴露公网端口） | ✅ **首选**，个人自用场景最省心 |
| **③ 走现有联机大厅服务中继** | 扩展 `Server/Lobby.cs` 做媒体转发 | **低（短期）**，`Lobby.cs` 只是房间状态机，无字节转发/WebSocket/媒体通道 | **高**（独立中型工程：信令+转发+鉴权+QoS） | 中（自控但工程量大） | 🟡 中期自控方案，非短期 |

**结论**：个人自用 → **Tailscale/ZeroTier**（零运维、WireGuard 自带加密、不暴露公网）；要分发多人 → 自建 WireGuard 或 frp+强制 HTTPS；中继扩 `Lobby` 属独立工程，不急。

### 6.4 有没有必要加 IP 白名单 / 限速 / 审计 —— 分级建议

| 措施 | 必要性 | 理由与落点 |
|---|---|---|
| **IP 白名单（remoteip）** | 🟡 建议（外网时） | `WebFirewall.cs:44-47` 补 `remoteip=<局域网段>` 即可把暴露面锁回局域网；外网走隧道则天然隔离 |
| **令牌强制非空** | ✅ 必做 | 见 6.1，优先级高于白名单（白名单防不了「同网段恶意设备」） |
| **限速** | 🟡 可选（P0 后） | 防单个客户端打爆注入通道/暴力猜口令；`/api/login` 已有 300ms 慢速（`WebServer.cs:341`），注入侧暂无 |
| **审计日志** | ✅ 建议 | 已有 `broker.log`（`BrokerService.Trace`）与 `InputTrace`；注入侧建议补「来源 IP + 时间 + 动作」审计，出事故可追溯 |

---

## 7. 未验证项（诚实声明）

| 项 | 状态 |
|---|---|
| WebRTC 相对 HTTP chunked 的**具体延迟/带宽毫秒数** | ❌ 未实测（定性改善有依据，定量无） |
| 颜色「黑白」bug 修复的**端到端实测** | 🟡 已定位已修复（`AgentWebServer.cs:476-497` 正确 BT.601 2×2 降采样），**端到端画面颜色待真机验证** |
| Moonlight 能否「非 GameStream 协议」接入 | ✅ 已查证：不能（协议不匹配，有官方文档 + 本仓库源码调研双重依据） |
| `.NET 侧 WebRTC 媒体栈成熟度（SIPSorcery 等） | 🟡 仅二手（`STREAM-PERF-AUDIT §4.7c` 记录），未在本机拉包验证 |
| 外网真实带宽/丢包下的 /stream 表现 | ❌ 未实测（无公网环境 + 不跑重负载） |
| 空 token/CORS 的实际攻击面 | 🟡 已静态定位（file:line），未做真实攻击复现（不跑重负载/不碰生产） |

---

## 8. 相关文档

- `STREAM-SURVEY-20261122.md` —— 开源串流全表（Sunshine/Moonlight/RustDesk/Parsec 协议与许可）
- `OSS-REMOTE-RESEARCH-20261122.md` —— Sunshine/Moonlight 源码级拆解（低延迟参数 + 许可风险）
- `VDD-RESEARCH-20261122.md` —— 虚拟显示器（IddCx）只能救 console 会话
- `VIDEO-STREAM-RESEARCH-20261122.md` —— MediaCodec 拉流 + Moonlight Android 架构
- `STREAM-PERF-AUDIT-20261122.md` —— 逐跳数据流 + WebRTC 候选（§4.7c）
- `MOBILE-REMOTE-PLAN-20261122.md` / `MOBILE-CONTROLS-DESIGN-20261122.md` / `VIRTUAL-CONTROLS-RESEARCH-20261122.md` —— 手机端手势/键位/转视角（协议缺口已由 stream-core 补齐）
- `HANDOFF-STREAM-STATUS-20261123.md` —— /stream 现状与问题
- 外部：caniuse VideoDecoder、MDN WebCodecs、Sunshine 官方文档（LizardByte）、RustDesk 官方文档
