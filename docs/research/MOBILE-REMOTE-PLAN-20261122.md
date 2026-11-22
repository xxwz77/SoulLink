# 手机端远程控制方案（MOBILE-REMOTE-PLAN）

> 日期：2026-11-22
> 作者：mobile-lead（agent-team）
> 前置文档：`HANDOFF-3.3.19-20261122.md`（网页直连代理）、`WEB-DIRECT-3.3.5-20261122.md`（端口分配）、`PERF-3.3.4-20261122.md`（性能基线）
> 用户诉求：① 手机端也要支持，**期望做成一个 APP**；② 想要 **ToDesk / 向日葵 / Parsec 那种"像远程控制"的手感**。
> 本文件是**只读分析 + 方案设计**，不改业务代码、不动 dist/、不提交、不发版。

## §0 修订记录（评审核验后修订）

| 版本 | 修订 | 依据 |
|---|---|---|
| v1.0 | 初版 | — |
| v1.1 | ① §2.7 断线重连按真实行为改写（回前台**不会**自恢复），P0 拆成「页面可见性恢复」「网络断线重连」两条；② §5.1 公网暴露面改写（防火墙规则 `WebFirewall.cs` L44-47 无 `remoteip` 限制 + `http://+` 绑定全网卡）；③ 复用率重算并撤下「后端零新增/P0 全前端」，列出必须动 `Relay/AgentWebServer.cs` 的最小改动集；④ 全文清除无 file:line 的「零改动/已有」断言 | 评审核验（kimi-reviewer + lead） |
| v1.2 | ① §4.5 补全 DTO 最小改动集：`InputDto.text`（`AgentWebServer.cs` L314–323）+ `InputDto.dx/dy` + `moverel` 路由（L280–291 switch）；② §1.1 与 §3.1 数字口径统一（复用率改定性，不再「70%/100%/95%」混用）；③ §2.9 iOS PWA 限制按真实行为改写（iOS 支持「添加到主屏」standalone，但无 `requestFullscreen`、无 `beforeinstallprompt`、SW 存储会被回收）；④ §5.1 改为「代码层暴露面 + 网络层不可达」两层表述；⑤ WebRTC 从「中期主线/手感质变关键」降级为「可选中期项」 | 评审核验（lead） |

---

## 0. 一句话结论（TL;DR）

1. **现状网页在手机上"能看，但基本不能操作"**：`pointer` 事件在移动浏览器其实会触发、画面也能拉流，但**右键、滚轮、软键盘、双指手势、双击缩放、断线重连全都不通**，等于只能围观。改造集中在前端交互层，但**不是"后端零改动"**——软键盘中文输入需代理新增 `text` 动作、安全门槛需改 `AgentWebServer`（详见 §4.5 最小改动集）。

2. **明确推荐：先 A（PWA，第 1 周验证），再 B（壳套 APP，第 2–4 周交付"APP"），C（原生）暂缓。**
   - A/B/C 三者**共享同一份 `web-index.html` 交互逻辑**，投入不浪费。
   - 真正的体验天花板不在"网页还是原生"，而在**当前 JPEG 轮询渲染**（30fps 上限 + 光标画在帧里 + 无视频编码）。WebRTC 渲染**可作为可选中期项**（§3.4），但不是 P0 的硬前提。

3. **外网穿透首选 Tailscale/ZeroTier（零运维、WireGuard 自带加密）**；要"可控/给多人分发"再自建 WireGuard 或 frp+TLS。**现有 `Server/Lobby.cs` 只是大厅状态机，不能直接当中继**，把它升级成媒体中继是中期工程，不是短期选项。⚠ 现状代理端口**已经不是"只局域网可达"**（`WebFirewall.cs` L44-47 的防火墙规则无 `remoteip` 限制 + `AgentWebServer.cs` L77 绑 `http://+` 全网卡），安全修复是**发布前置硬门槛**（§5.4），不是可选项。

---

## 1. 现状盘点（已有哪些资产，能复用多少）

读代码后的结论：**手机端 = 复用现有网页 + 补交互层 + 解决外网**。后端协议**主体可复用**，但**不是零改动**——见 §4.5 最小改动集（`text` 动作 + 安全收窄）。

| 资产 | 位置 | 对手机端的价值 |
|---|---|---|
| 实时画面拉流 | 代理 `GET /live`（`AgentWebServer.cs` L176–179 路由，`SessionRecorder.GetLatestJpeg()` 内存缓冲直取 JPEG） | ✅ 手机浏览器/WebView 直接能用，无需改 |
| 键鼠注入 | 代理 `POST /api/input`（`AgentWebServer.cs` L182–186 路由 + `HandleInputAsync` L261，`action: click/down/up/move/wheel/key`，坐标 0~1 相对值） | ✅ 协议已支持右键(`button:"right"`)与滚轮(`delta`)，手机端 JS 层把多指手势映射过去即可 |
| 身份回显 | 代理 `GET /api/info`（`AgentWebServer.cs` L163–174） | 调试用，手机端可选 |
| 会话列表 + 口令下发 | 主程序 `GET /api/sessions`（`WebServer.cs` L360 路由 → `ApiSessions` L726，含 `directPort`、`directToken`） | ✅ 手机端登录后拿端口+令牌，逻辑不变 |
| 历史录像 | 主程序 `GET /file/*.mp4`（`WebServer.cs` L364–365 路由 → `WriteFile` L439，支持 Range 206） | ✅ `<video>` 可直接播 |
| 口令鉴权 | 主程序 Cookie（`WebServer.cs` L103 `IsAuthorized`）+ 代理 `X-SoulLink-Token`/`?token=`（`AgentWebServer.cs` L208 `CheckToken`） | ⚠ 明文 HTTP + 空口令放行（L210 `if (string.IsNullOrEmpty(_token)) return true`），外网必须收窄（§5.4） |
| 端口规则 | `WebDirect.PortFor`（`WebDirect.cs` L45–53）= `WebPort + accountIndex + 1`；防火墙 `WebFirewall.cs` L44–47 放行范围 | ⚠ 防火墙规则**无 `remoteip` 限制**（见 §5.1），不只是局域网 |
| 联机大厅 | `Server/Lobby.cs`（ASP.NET Core + SQLite，房间/座位状态机，`Program.cs` L52–203 路由） | ⚠ 只有状态，**不转发媒体字节**（见 §5.3） |

**关键结论**：手机端接入路径与桌面浏览器完全一致——
`GET /api/sessions`（拿 `directPort`+`directToken`）→ `http://主机:directPort/live?token=…` 拉帧 + `POST /api/input` 注入。**P0 的主体工作在前端**，但有一个必须动代理的例外（`text` 动作，§4.5）+ 一组安全收窄改动（§5.4）。

### 1.1 复用率估算（重算，附依据）

> 原稿「后端零新增 / P0 全前端」过于乐观，撤下。以下按**代码单元**统计，而非"功能点"拍脑袋。

**前端 `web-index.html`（约 1218 行）**：
| 部分 | 是否复用 | 依据 |
|---|---|---|
| 拉帧循环（`pullFrame`/`scheduleNext`/静止降频/失败退避 L703–802） | ✅ 复用 | 手机端同一份 JS |
| 坐标换算 `norm()`（L822–831，已兼容 `touches`） | ✅ 复用（双指要改读中心点） | L828 已有 `e.touches ? e.touches[0].clientX …` |
| 输入投递 `sendInput()`（L805–820，已走直连 + 令牌头） | ✅ 复用 | 手势只改"事件→动作"映射层，不改投递 |
| 手势/软键盘/虚拟修饰键/断线重连/防缩放/横屏 | ❌ **新增/改写** | 这是手机端的实际工作量 |

**代理 `AgentWebServer.cs` + `RelayAgent.cs`**：
| 部分 | 是否复用 | 依据 |
|---|---|---|
| `/live`、`/api/info`、`/api/input` 的 click/down/up/move/wheel/key | ✅ 复用 | 协议字段无需改 |
| `text` 动作 | ❌ 新增（P0 必需） | `AgentWebServer` L282 switch + `RelayAgent` L623 switch |
| CORS 收窄、令牌强制非空 | ❌ 改（P0 安全必需） | L138–140、L210 |

**主程序 `WebServer.cs`**：会话列表/历史录像/口令下发全部复用；二次认证需新增（§5.4-4）。

**复用率结论（定性，不列百分比口径）**：前端**大部分复用**（拉帧循环、`norm()` 坐标换算、`sendInput()` 投递），**新增集中在**触摸语义 + 软键盘 + 虚拟修饰键 + 断线重连 + 防缩放/横屏；代理**协议 7 个动作复用 6 个**（click/down/up/move/wheel/key），**新增 1 个 `text` + 补 `moverel` 的 dx/dy 路由**（§4.5）+ 2 处安全改动；主程序仅新增二次认证。**不是"零后端改动"，而是"后端改动集中且小"**。注：§3.1 的「复用 web-index.html」行此前标 100%/95% 是**路线层面**的粗口径，与本节"代码单元级"口径不同，已统一为定性表述（见 §3.1）。

---

## 2. 问题 1：现状网页端在手机上能不能用？

**结论：能打开、能看实时画面，但操作能力几乎为零。** 逐个阻碍如下：

| # | 阻碍 | 现状代码事实 | 手机上的表现 | 严重度 |
|---|---|---|---|---|
| 1 | **指针事件 vs 触摸** | `#scr` 用 `pointerdown/move/up`（`web-index.html` L841–919） | 移动浏览器（iOS Safari 13+/Chrome）**会**把单指触摸派发成 pointer 事件，所以单指点击/拖动**能通**，但语义是"绝对定位"，没有拖拽灵敏度概念 | 低（能通，需优化） |
| 2 | **右键不可达** | 右键靠 `e.button===2`（L846），`contextmenu` 被 `preventDefault`（L924） | 触屏没有 button=2；长按默认触发系统 contextmenu 又被禁掉 → **手机上无法右键** | **高** |
| 3 | **滚轮不可达** | 滚轮靠 `wheel` 事件（L920） | 触屏没有 wheel；双指捏合/双指滑动目前无映射 → **无法滚动页面/游戏视角** | **高** |
| 4 | **软键盘无法触发** | 键盘靠全局 `keydown`（L938）+ `kbMode` 开关，没有 `<input>` 承载 | 移动端软键盘只在 `<input>/<textarea>` focus 时弹出；当前无输入框 → **手机上完全打不了字**（无法进游戏聊天/输入账号） | **高** |
| 5 | **双击缩放** | `viewport` 是 `width=device-width, initial-scale=1, viewport-fit=cover`（L5，**没有** `user-scalable=no` / `maximum-scale=1`） | iOS Safari 双击/双指放大缩小画面，干扰远程点击 | 中 |
| 6 | **横竖屏** | 布局是桌面版 flex，无 `orientation` 处理，画面 `object-fit:contain` | 竖屏画面居中留大片黑边；旋转后坐标映射（`norm()`）需重新计算，当前依赖 `getBoundingClientRect` 实时取值**理论上能自愈**，但无沉浸式全屏/隐藏浏览器 UI | 中 |
| 7 | **断线重连（页面可见性 + 网络两层）** | ① `pullFrame` L757 `if (document.hidden || paused) { liveHandle = null; return; }` 后台**只清句柄、不排下一帧**，且全文**无 `visibilitychange`/`pageshow`/`focus` 监听**（grep 无匹配）；② `pullFrame` L792–800 失败退避只覆盖"请求失败"，**无 `online`/`offline` 监听**，也不重拉 `/api/sessions` | **回前台画面是死的**：`liveHandle` 已置 null，`startLive()` 无人再调，只能手动刷新；**切 4G↔WiFi / 走出 WiFi** 后 `directPort` 指向的 IP 失效，同样无自愈 | **高** |
| 8 | **令牌携带** | `?token=` 查询串（`img/video` 用）+ `X-SoulLink-Token`（fetch 用） | 机制本身手机端可用，但**令牌明文走 HTTP**，外网裸奔时口令泄露（见 §5） | **高（外网场景）** |
| 9 | **iOS PWA 限制** | 无 `manifest.json`、无 `Service Worker`（现状连基础 PWA 都不是） | iOS Safari **支持**「添加到主屏」并可用 `display:standalone` 全屏打开（非"不能全屏"）；但 **无 `requestFullscreen` API、无 `beforeinstallprompt` 安装引导、无推送通知**，且 iOS 会**定期回收 SW 存储**（几周不用即清），离线壳不可靠 | 中（影响"像 APP"的体感，但比原「不能全屏」轻） |
| 10 | 画面延迟/帧率 | JPEG 轮询 + 光标画在帧里（`HANDOFF` §4.2） | 30fps 下光标一顿一顿；移动网络带宽受限时更明显 | 中（手感天花板，见 §3.4） |

**小结**：阻碍 2、3、4、7 是"能不能操作/能不能稳定用"的硬伤；5、6、9 是"像不像 APP"的体验项；8 是外网安全项；10 是长期手感项。

---

## 3. 问题 2：三条路线对比与推荐

### 3.1 总对比表

| 维度 | A. PWA / 移动网页 | B. 壳套 WebView APP | C. 原生/半原生 + WebRTC |
|---|---|---|---|
| **开发成本** | **2–4 人日**（改 `web-index.html` + 加 manifest） | **1–2 周**（Capacitor 壳 + 复用网页 + 少量原生插件） | **2–3 月**（渲染管线 + 原生 UI + 双端调试） |
| **体验上限** | 中：JPEG 轮询上限，浏览器后台受限，无系统级通知/剪贴板 | 中高：同 A 的渲染上限，但**能装成 APP、上架、后台续命、调系统键盘/剪贴板/文件** | **高**：WebRTC H.264 低延迟 + 独立光标通道，真正 ToDesk 手感 |
| **维护成本** | 极低（同一份网页） | 低（网页逻辑 99% 复用，只多壳层） | 高（原生代码 + 媒体管线双端各自维护） |
| **离线/后台限制** | iOS Safari 切后台即冻、无离线壳；Android 加 SW 可部分离线 | 可申请后台/前台服务、离线壳，iOS 仍需用户授权；比纯网页好一档 | 原生可控性最强（前台服务/后台权限自主） |
| **上架与分发** | 无需上架，扫码/网址即用；无应用商店入口 | Google Play ✅ / 国内渠道 ✅ / **iOS App Store 需审核+开发者账号(99$/年)**；Android 可侧载 APK | 同上，且审核更严（远程控制类目需隐私合规） |
| **复用现有 web-index.html** | 基本全复用（改触摸/软键盘/断线层） | 基本全复用（同一 HTML 直接加载 + 壳层桥接） | 仅协议复用，UI 重写 |
| **与"期望做个 APP"的契合度** | 差（本质还是网页） | **好**（就是 APP，包壳） | 好（真原生） |

### 3.2 各路线展开

**A. PWA / 移动网页（复用 `web-index.html` 加触摸适配）**
- 做什么：补 §2 的 2/3/4/5/6/9——手势映射、软键盘 `<input>` 承载、`user-scalable=no`、`manifest.json` + 图标、可选 Service Worker。
- 成本最低（2–4 人日），**是 B/C 的共同底座**，投入零浪费。
- 体验天花板：**受 JPEG 轮询限制**（见 3.4），且 iOS 无"添加到主屏"沉浸式、后台会冻、无系统剪贴板。适合"先让手机能远程操作"的验证期。

**B. 壳套 WebView APP（.NET MAUI BlazorWebView / Capacitor / Tauri Mobile）**
- 做什么：用壳加载**同一份 `web-index.html`**，通过桥接拿到原生能力：软键盘调度、剪贴板、后台续命、沉浸式横屏、文件分享、推送。
- 三选一：**Capacitor**（团队 JS 生态最顺、iOS/Android 双端一致、社区插件全）> MAUI BlazorWebView（同 .NET 技术栈、但 UI 生态弱、真机调试成本高）> Tauri Mobile（v2 仍新，移动端插件少）。
- 维护成本低：交互逻辑仍在一份 HTML 里改，壳层几乎不动。
- 这是"用户要一个 APP"的**最经济落地形态**。

**C. 原生/半原生 APP + WebRTC 渲染**
- 做什么：代理侧新增 WebRTC 媒体通道（H.264/VP8 编码 + ICE/TURN），APP 侧原生渲染 + 独立光标通道。
- 体验最高，但**必须同时改代理（C#）与 APP（原生）两端**，且引入 STUN/TURN 基础设施（与 §5 穿透耦合）。
- 唯一值得选它的前提：JPEG 轮询的延迟/带宽/帧率成为不可接受的瓶颈。**在渲染升级完成前，C 的性价比低。**

### 3.3 明确推荐

> **推荐路线：A → B 渐进式，C 原生暂缓。WebRTC 渲染为可选中期项（非 P0 硬前提）。**

1. **第 1 周**：做 **A（PWA）**，补齐手势 + 软键盘 + 防缩放 + manifest。目的：**验证"手机上能看能操作"**，同时为 B 攒下全套触摸交互代码。
2. **第 2–4 周**：把 A 的成果**原样包进 Capacitor 壳 → 得到"APP"**（可装 APK / 上架 / 侧载），补原生能力（后台续命、剪贴板、沉浸式横屏、软键盘调度）。
3. **之后（可选）**：JPEG 轮询的延迟/带宽成为**实测瓶颈**后，再评估 **WebRTC 渲染升级**（惠及 A/B/C）；若届时已有稳定用户与上架需求，再评估是否迁移 C 原生。

**为什么不直接上 C**：C 的主要收益来自"WebRTC 渲染"，而 WebRTC 渲染可**先加在 A/B 的 WebView 里**（浏览器原生支持 WebRTC），不必立刻重写整个 APP。**但 WebRTC 需要改代理（C# 媒体编码）+ 前端两端，且引入 STUN/TURN 基础设施，是独立的中期工程，不是 P0 必需**——先用 JPEG 轮询把 P0 跑通，再按实测决定是否投入。

### 3.4 体验天花板说明（当前与 ToDesk 的差距，以及何时值得升级）

| 现状 | 影响 | 出路（可选中期） |
|---|---|---|
| `/live` 是 **JPEG 逐帧轮询**（`setTimeout` 串行） | 30fps 上限、~4.8MB/s（30fps/q75）、每帧完整重传 | WebRTC：H.264 帧差编码，带宽 -50~80%，延迟从"帧间隔级"降到"编码级" |
| **光标画在帧里**（`SessionRecorder` 叠加） | 光标流畅度 = RecordFps，5fps 下一顿一顿 | 独立光标通道：move 事件 → CSS/原生光标，不再受帧率拖累（`HANDOFF` §4.2 已点名） |
| 输入走 **HTTP POST 串行往返** | 拖动视角时每个 move 一次请求，RTT 敏感 | 升级 WebSocket/DataChannel 双向低延迟通道 |
| 无音频 | 远程只能看不能听 | 中期可加（WebRTC 顺带支持） |

**结论**：A/B 能做到"可用、流畅看、能点能拖"；「像远程控制的手感」要更好，WebRTC 是**可选中期的提升项**，但**是否投入要看 JPEG 轮询在真实移动网络下的实测表现**（带宽、延迟、电池），不作为 P0 阻塞。

---

## 4. 问题 3：手机端交互设计要点

### 4.0 手机端相对现有网页端，新增的到底是什么（先厘清，避免重复计收益）

> ★ 评审提醒：**"减少事件往返"不是新收益**——现有桌面网页已经把 `Move + Click` 合并成一次 `click` 事件（`web-index.html` L913 直接 `sendInput('click', …)`，代理侧 `RelayAgent` 的 `Move` 标志在同一条命令里先 `SetCursorPos` 再点），手机端沿用即可，**不得再当卖点**。

手机端方案真正**新增**的东西，只有下面这些（其余全部复用现有协议与逻辑）：

| 新增项 | 现状（网页端没有） | 手机端要做的 |
|---|---|---|
| **触摸语义** | 只认 `pointer` 的单点 + 鼠标 `wheel`/右键 | 多指触控：单指点/拖、双指滚轮、长按右键、双指捏合，映射到既有 `click/down/up/move/wheel` 动作 |
| **软键盘** | 靠物理键盘 `keydown`，无 `<input>` 载体 | 隐藏 `<input>` 唤起宿主输入法 → 文本/组合键回传（需代理新增 `text` 动作，§4.5） |
| **虚拟修饰键** | 无（物理键盘自带 Ctrl/Alt/Shift） | 屏幕上常驻 Ctrl/Alt/Shift/Win 虚拟键，配合点按组合 |
| **触控板模式** | 无（鼠标天然有相对移动） | 触控板区做相对位移（需代理接线 `moverel` 的 `dx/dy`，§4.5），避免手指挡画面 |
| **断线重连** | 靠手动刷新浏览器（`web-index.html` L757 后台只清句柄、无 `visibilitychange`/`online` 监听） | 前台恢复时 `startLive()` 续帧 + 网络恢复时重拿 `/api/sessions` |
| **横竖屏/沉浸式** | 桌面固定横屏 | `orientationchange` + 全屏 + 方向锁定 + `safe-area` |
| **防双击缩放** | `viewport` 未禁缩放（`web-index.html` L5 无 `user-scalable=no`） | 收紧 `touch-action` + `user-scalable=no` |
| **PWA/壳层** | 纯网页（无 `manifest.json`/Service Worker） | manifest / Service Worker / Capacitor 原生桥接 |

> 一句话：**前端是主体，但后端不是零改动**——软键盘中文需代理新增 `text` 动作、触控板需接线 `moverel`（§4.5），安全门槛需改 `AgentWebServer` 的 CORS/令牌（§5.4）。见 §4.5「必须动 Relay/AgentWebServer.cs 的最小改动集」。

### 4.1 手势映射表（核心）

> 原则：单指是"移动+点按"，双指承担"滚轮/缩放/右键"，长按补右键兜底。映射只发生在 JS 层，最终都落到现有 `/api/input` 的 `click/down/up/move/wheel/key`。**事件合并沿用现有实现，不重做。**

| 手势 | 语义 | 映射到 `/api/input` |
|---|---|---|
| 单指轻点（无位移） | 左键单击 | `{action:"click", x, y, button:"left"}` |
| 单指按住并拖动 | 左键按住拖动（选区/拖拽视角） | `down(left)` → `move`(节流) → `up(left)` |
| 单指悬停滑动（未按下） | 光标跟随移动 | `move`（节流，复用现有 `sendMoveThrottled`） |
| **双指上下滑动** | 滚轮（页面滚动/游戏视角缩放） | `wheel`（`delta` 按滑动位移映射 ±120 的倍数） |
| **双指捏合** | 缩放（可选：发滚轮或本地 zoom 画面） | 建议先做滚轮；本地画面 zoom 属于客户端行为，不发后端 |
| **长按（≥500ms 未位移）** | 右键（弹上下文菜单） | 先 `move` 到位 → `{action:"click", button:"right"}` 或 `down/up(right)` |
| 三指 | 可选预留：中键 / 快捷键 | `button:"middle"` 或 `key` |

**实现要点**：
- 用 `touchstart/move/end` 自行判**触点个数**（`e.touches.length`）来分派单指/双指，不要依赖浏览器默认手势（默认已被 `touch-action:none` 抑制，见 `web-index.html` L234）。
- 单指拖动加**阈值判定**（现有 `drag.moved` 用 8px，L896）区分"点击"与"拖动"，触屏建议放宽到 10–12px。
- 双指位移 → `wheel` 要**量化为 ±120 的整数倍**（代理侧 `mouse_event` 的 WHEEL 以 120 为一格），并在两指间做节流，避免事件风暴。
- **坐标换算复用 `norm()`**（L822–831，含 `object-fit:contain` 的黑边 offset），触屏时改读 `touches[0].clientX/Y`——现有代码**已经这么写了**，但双指滚轮要改读两指中心点。

### 4.1.1 虚拟修饰键（Ctrl / Alt / Shift / Win）

- 手机没有物理修饰键，远程操作（复制粘贴 `Ctrl+C/V`、`Alt+Tab`、`Ctrl+Shift+Esc`、游戏组合键）需要**屏幕上常驻一排虚拟修饰键**。
- 实现：四个可切换按下的软键（Toggle 状态存内存，按下高亮）。用户先点亮修饰键，再点一下目标键/画面，前端把 `parts = [已点亮的修饰键..., 目标键]` 拼成 `Ctrl+Shift+A` 格式交给 `key` 动作（代理 `ParseVk` 已认 Ctrl/Alt/Shift/Win）。
- 关键点：`keydown` 监听（`web-index.html` L938）已排除纯修饰键本身，虚拟键在**前端**维护"按住状态"，不依赖远端 OS 的修饰键状态；发送完一个组合后按策略复位（可配置"粘滞"或"一次性"）。

### 4.2 触控板模式 vs 直触模式

| 模式 | 行为 | 适用 |
|---|---|---|
| **直触模式（默认）** | 点哪打哪（绝对坐标），手指挡画面 | 日常点击、菜单操作、打字 |
| **触控板模式** | 屏幕下方一块"触控板"控制远端光标（相对位移），画面区不消费触摸 | 精确点击、游戏里转视角、需要看清全屏时 |

- 建议两者**可切换**（顶栏一个开关，状态存 `localStorage`）。
- 触控板模式天然适合"远程控制手感"：单指在触控板上滑动 = 相对移动（可映射到代理已有的 `moverel` 相对位移语义，或前端做相对→绝对换算后走 `move`）；点按 = 左键；双指滑动 = 滚轮。

### 4.3 软键盘如何触发宿主输入法

- 现状硬伤：全局 `keydown` 监听没有 `<input>` 载体，移动端软键盘不弹出。
- 修法：在页面上放一个**隐藏的 `<input>`（或 `contenteditable`）**，用户点画面（`kbMode=true`）后 `input.focus()` 唤起系统键盘，监听该 input 的 `input`/`keydown` 事件，把**输入字符/组合键**通过 `key` 动作发往远程，输入完收起（blur）。
- 注意：移动端 `keydown` 的 `e.key` 对中文输入法（IME）返回的是**候选/合成**值，`compositionstart/compositionend` 期间不要逐字发送，等 `compositionend` 后把**最终字符串**整段发送（代理 `key` 目前是"按键名"语义，**长文本/中文需要扩展一个 `text` 动作**或逐字符发 `key`——这是唯一需要动后端协议的潜在点，见 §7 未决问题）。

### 4.4 横屏沉浸式

- CSS 加 `@media (orientation: landscape)` 与"全屏"按钮（`requestFullscreen()` + `screen.orientation.lock('landscape')`）。
- 配合 `manifest.json` 的 `display: fullscreen` + `orientation: landscape`（PWA）或 Capacitor 的原生全屏/方向锁定（APP）。
- 隐藏浏览器地址栏/状态栏：PWA 用 `viewport-fit=cover` + `safe-area-inset`；APP 用原生沉浸式。
- 横竖屏切换时强制重算 `norm()`（监听 `resize` + `orientationchange`，重新取 `getBoundingClientRect`）。

### 4.5 必须动 `Relay/AgentWebServer.cs` 的最小改动集（回应评审：撤下「后端零改动」）

> 原稿称「后端零新增 / P0 全前端」是错的。按改动性质分两类：**功能必需** + **安全必需**。除这两类外，手机端不改代理协议。下面给出**可直接照做的 DTO 级改动**（含具体字段与 file:line）。

**改动 0 — 现状 `InputDto`（这是要改的基座，先看清楚）**：

`AgentWebServer.cs` L314–323 当前 `InputDto` 字段：`action / x / y / button / key / delta / count`。**没有 `text`，也没有 `dx/dy`**（`RelayAgent.Request` L404–422 内部有 `Dx/Dy`，但 HTTP 层 `InputDto` 没暴露，所以触控板模式想要的"相对位移"目前从网页发不进来）。

| # | 改动 | 落点（现 file:line） | 类别 | 是否 P0 |
|---|---|---|---|---|
| 1 | **`InputDto` 加 `text` 字段**（手机中文/长文本整段输入） | `AgentWebServer.cs` L314–323 的 `InputDto` 加 `public string? text { get; set; }` | 功能必需（软键盘中文打字） | **是**（否则 §2 阻碍 4 只能打 ASCII 组合键） |
| 2 | **switch 加 `case "text"` 路由**（把 `text` 转成代理能执行的命令） | `AgentWebServer.HandleInputAsync` L280–291 的 `switch` 加 `case "text": cmd = "text"; break;`；`RelayAgent.Execute` L623 `switch` 加 `case "text"`，用 `SendInput` + `KEYEVENTF_UNICODE`（`RelayAgent.cs` L155–156 已有 `SendInput` P/Invoke，需补 `KEYEVENTF_UNICODE=0x4` + `KEYEVENTF_KEYUP` 的 Unicode 分支；`Request` 用 `Button` 字段承载文本即可，或复用 `Hotkey` 字段） | 功能必需（配合 #1） | **是** |
| 3 | **`InputDto` 加 `dx/dy` 字段 + `moverel` 路由**（触控板模式相对位移） | `InputDto` L314–323 加 `public int dx { get; set; }`、`public int dy { get; set; }`；`HandleInputAsync` L280–291 switch 加 `case "moverel": cmd = "moverel"; break;`，并把 `dx/dy` 写进 `RelayAgent.Request.Dx/Dy`（`Request` L409–410 已有 `Dx/Dy`，`Execute` L675 `case "moverel"` 已实现相对位移，只是 HTTP 层没接线） | 功能必需（§4.2 触控板模式的相对移动） | 是（触控板模式依赖） |
| 4 | **CORS 收窄** | `AgentWebServer.HandleAsync` L138–140：`Access-Control-Allow-Origin` 从 `*` 改为回显白名单 Origin | 安全必需（§5.4-3） | **是**（对外发布前） |
| 5 | **令牌强制非空** | `AgentWebServer.CheckToken` L210：`if (string.IsNullOrEmpty(_token)) return true` 改为外网暴露时拒绝/强制生成 | 安全必需（§5.4-2） | **是**（对外发布前） |
| 6 | （可选）`?token=` 改一次性票据 | `AgentWebServer.CheckToken` L216–218 | 安全增强（§7-5） | 否（可后置） |

**结论**：P0 的代理侧改动 = **2 个功能（`text` 动作 + `moverel` 接线，含 `InputDto` 字段）+ 2 个安全（CORS/令牌非空）**；其余 `/live`、`/api/input` 的 click/down/up/move/wheel/key、`/api/info`、端口分配全部不动。主程序侧另需：二次认证（§5.4-4，落点在 `WebServer.cs` 鉴权层）。

---

## 5. 问题 4：网络与穿透（手机通常不在同一局域网）

### 5.1 现状：两层表述——「代码层暴露面」与「网络层不可达」

手机外网问题要拆成**两个独立层面**看，二者并不矛盾：**代码层已经把端口暴露到全网卡，网络层却因 NAT/CGNAT 挡住了实际访问**。

**第 1 层 · 代码层（暴露面已存在）**：

1. **绑定全网卡**：`AgentWebServer.Start` L77 `l.Prefixes.Add("http://+:" + port + "/")`。`+` 前缀 = **绑定本机所有网卡**（含公网 IP、VPN 虚拟网卡、局域网网卡），不是 `localhost`。所以一旦有公网 IP / 路由器做了端口映射，代理端口**直接对公网可达**。
2. **防火墙规则无来源限制**：`WebFirewall.EnsureAllowed` L44–47 只写 `dir=in action=allow protocol=TCP localport=<from>-<to>`，**没有 `remoteip=` 白名单、没有 `profile=` 限制**。等于"任何源 IP 都能连进这段端口"（只要防火墙开了档）。注释 L12 写「加 = 别的机器能用」，本意是让**局域网手机平板**能连，但规则本身**没有限定到局域网网段**。
3. **明文 + 空口令 + CORS=`*` 叠加**：`AgentWebServer.HandleAsync` L138 `Access-Control-Allow-Origin: *` + `CheckToken` L210 空令牌直接放行 + HTTP 明文。三条叠加后，**任何能连到该端口的公网页面都能无凭据 `fetch` 注入键鼠到提权桌面**。

**第 2 层 · 网络层（日常"连不上"的原因）**：

- 家里路由器是 NAT，外网无法直达 `:port`；
- 运营商常给家用宽带 **CGNAT**（无公网 IPv4），即使端口映射也没用。

**结论**：**代码层的"暴露面"已经存在**（只要哪天有了公网 IP 或误开端口映射，就是裸奔），而**网络层的 NAT/CGNAT 只是"暂时挡住了"这种暴露**。二者是"隐患"与"遮挡"的关系，不是"有暴露就没遮挡"或"有遮挡就没暴露"。所以 §5.4 安全门槛必须在任何对外形态之前落地，而非"外网才需要考虑"。

### 5.2 方案对比与推荐

| 方案 | 成本 | 延迟 | 安全 | 运维 | 推荐度 |
|---|---|---|---|---|---|
| **Tailscale / ZeroTier（用户自建虚拟局域网）** | 零（个人版免费） | 低（WireGuard，P2P 直连） | **高**（隧道自带加密） | 极低（装两端 App/客户端即可） | ⭐ **首选** |
| 自建 WireGuard | 一台 VPS + 配置 | 低 | 高 | 中 | 次选（要可控时） |
| **frp / 内网穿透** | 一台公网 VPS | 中（经 VPS 转发） | 中（需自己套 TLS） | 中 | 自控场景可选 |
| Cloudflare Tunnel | 免费 | 中高 | 中 | 低 | ⚠ 不建议（TOS 限视频/大流量，免费档约 100MB/次） |
| **自建中继（扩展 `Server/Lobby.cs`）** | 高（新增媒体转发+信令+加密） | 中 | 可控 | 高 | 中期自控方案，非短期 |
| **WebRTC 打洞（STUN/TURN）** | 高（代理侧加 WebRTC + TURN 服务器） | **最低（P2P）** | 中（需 TURN 兜底 + 加密） | 高 | **可选中期项**（与渲染升级绑定，非 P0） |

**推荐组合（分层）**：
- **个人使用（1~几人）**：**Tailscale 或 ZeroTier**。手机装 APP、PC 装客户端，两边进同一虚拟网，`web-index.html` 里 `location.hostname` 换成虚拟网 IP 即可。**注意：这不等于"后端零改动"——隧道解决传输加密，但 CORS 收窄与令牌非空仍要改代理（§5.4-2/3）。**
- **要给"非技术用户"分发（要做成产品）**：**自建 WireGuard 或 frp + 强制 HTTPS**，提供一键配置；把代理端口只暴露在隧道内，不对公网裸开。
- **可选中期（真·ToDesk 形态，按实测决定）**：**WebRTC + TURN**，与 §3.4 渲染升级合并评估；TURN 就是你的"自建中继"，此时 `Server` 需要新增媒体转发能力。**非 P0，是否投入取决于 JPEG 轮询在真实移动网络下的实测瓶颈。**

### 5.3 `Server/Lobby.cs` 能不能直接当中继？——**不能**

- `Server` 现在只是**大厅/房间状态机**：SQLite 存 `Devices/Rooms/Seats/Departs`，HTTP 接口全是状态读写（建房/入座/发车/心跳），**没有任何字节转发、没有 WebSocket、没有媒体通道**。
- 要当中继，需要新增：① 长连接通道（WebSocket/QUIC）；② 双向字节转发（视频 + 输入）；③ 转发层鉴权（房间码→会话票据）；④ 带宽/QoS 与审计。这是**一个独立的中型工程**，且代理（C# 提权进程）也要加"连中继"的客户端模式。
- **结论**：`Server` 是未来中继的**天然宿主**（已有设备身份 `X-Device-Code`、房间码体系），但短期别指望它。短期用 Tailscale 把"中继"外包出去，最快也最安全。

### 5.4 安全边界 —— ⚠ 硬前提，不是可选项（回应评审质疑 2）

> ★ 评审点名：**明文 HTTP + 空口令 + CORS=`*` 是手机端的前置风险**。现状代理端口 `http://+:<port>/`（`AgentWebServer.cs` L77）明文；`SOULLINK_WEB_TOKEN` 为空时 `CheckToken` 直接放行（L210）；CORS `Access-Control-Allow-Origin: *`（L138）。这三者在**局域网**尚可接受，一旦手机走外网，就变成**公网任意网站可写键鼠到提权桌面**（浏览器任意页面发一个 `fetch` 就能注入，因为 CORS 全放开 + 无令牌）。
>
> **结论：不先修安全，做 APP 不是"放大风险"而已，而是"把枪递到公网"。所以安全修复必须排在 A/B 任一形态的对外发布之前，作为 P0 门槛。**

**四项硬前提（缺一不可对外发布）**：

| # | 前提 | 具体做法 | 落点 |
|---|---|---|---|
| 1 | **传输加密** | 优先走 **WireGuard 隧道（Tailscale/ZeroTier）**，整条链路加密，代理端口只在虚拟网内可达，**不直接暴露公网**；若要裸走公网则必须给主程序/代理套 **TLS 终结**（反代或前置 `https` 监听） | 部署层；不改代理协议 |
| 2 | **令牌强制非空** | 现有 `CheckToken` 在 `_token` 为空时 `return true`。改为：外网/直连模式下**不允许空口令启动**——主程序侧在开启网页直连且检测到外网暴露时，若 `WebPassword` 为空则**拒绝下发端口或强制生成随机口令** | `AgentWebServer.CheckToken` + `RelayClient.CaptureEnv` |
| 3 | **CORS 收窄** | 把 `Access-Control-Allow-Origin: *` 改成**回显请求 Origin 白名单**（只允许本机/隧道域名），并把 `Access-Control-Allow-Headers/Methods` 同步收窄到仅 `X-SoulLink-Token, Content-Type` 与 `GET,POST,OPTIONS`。**杜绝任意公网页面跨源注入** | `AgentWebServer.HandleAsync` L138–140 |
| 4 | **二次认证** | 在"手机 ↔ 主机"控制链路外再加一道：**设备绑定（白名单 DeviceId）/ 一次性授权码 / TOTP**。被偷的手机或泄露的口令不能单独构成控制能力 | 主程序鉴权层 + 手机端登录态 |

**为什么"不先修 = 放大风险面"**：
- 现有网页端的前提是"同一局域网里的可信浏览器"，风险面已经被物理网络边界框住。
- APP/PWA + 外网穿透把这个边界**撕掉**，把提权桌面的控制面从"家里"扩展到"任何 4G/5G 手机"。此时空口令 + CORS`*` + 明文三者叠加，等于**任何拿到 URL 的人（甚至任何你访问过的恶意网页）都能控制你的电脑**。
- 所以本方案的**发布门槛 = 上述 4 项全部满足**。第 1 周的 PWA 允许在**纯局域网**内先行验证交互（不对外），第 2–4 周的 APP 若要"外出可用"，必须先过 5.4 安全门槛。

---

## 6. 问题 5：P0 交付物清单与里程碑

### 第 1 周（A：PWA，让手机"能看能操作"）

| 交付物 | 说明 | 位置 |
|---|---|---|
| 触摸手势映射 | 单指点/拖、双指滚轮、长按右键（§4.1） | `docs/web-index.html`（改后 `scripts/sync-webpages.ps1`） |
| 软键盘输入承载 | 隐藏 `<input>` 唤起系统键盘 + 组合键/文本发送 | 同上 |
| 防双击缩放 | `viewport` 加 `user-scalable=no, maximum-scale=1` + `touch-action` 全量收紧 | 同上 |
| PWA 壳 | `manifest.json` + 图标 + `display:fullscreen/standalone` | 同目录 + `WebServer` 静态路由补 manifest/icon |
| 横屏沉浸式 | `orientationchange` 处理 + 全屏按钮 + `safe-area` | 同上 |
| **断线重连（页面可见性恢复）** | 补 `visibilitychange`/`pageshow`/`focus` 监听：回前台若 `liveHandle===null && liveRunning` 则重调 `startLive()`。现状 `web-index.html` L757 只清句柄、无监听 | `docs/web-index.html` |
| **断线重连（网络断线）** | 补 `online`/`offline` 监听：`offline` 停帧 + 提示，`online` 后重拉 `/api/sessions`（重拿 `directPort`+`directToken`）再 `startLive()` | `docs/web-index.html` |
| **代理侧最小改动集** | ① `text` 动作（`InputDto` L314–323 加 `text` + `AgentWebServer` L280–291 switch + `RelayAgent` L623 switch）；② `moverel` 接线（`InputDto` 加 `dx/dy` + switch 加 `moverel`，接 `RelayAgent.Execute` L675 已有的相对位移）；③ CORS 收窄（L138–140）；④ 令牌强制非空（L210） | `Relay/AgentWebServer.cs` + `Relay/RelayAgent.cs` |
| 验证清单 | 真机（iOS + Android）过一遍 §2 的阻碍 1–7 | — |
| **安全门槛（对外发布前）** | §5.4 四项：隧道加密 / 令牌强制非空 / CORS 收窄 / 二次认证 | 代理 + 主程序 |

### 第 2–4 周（B：壳套 APP，交付"APP"）

| 交付物 | 说明 |
|---|---|
| Capacitor 工程 | 加载同一份 `web-index.html`，iOS/Android 双端 |
| 原生桥接 | 后台续命（前台服务/后台模式）、系统剪贴板、方向锁定、软键盘调度 |
| 分发 | Android：侧载 APK + 国内渠道签名；iOS：开发者账号 + App Store 提交（远程控制类目需隐私说明） |
| 外网方案落地 | Tailscale/WireGuard 接入文档 + APP 内"连接助手" |
| 二次认证 | 设备绑定 / 授权码（后端令牌体系升级） |
| **安全门槛收口** | 发布前复验 §5.4 四项；`?token=` 改一次性票据 | 代理 + 主程序 |

### 之后（可选中期项，非 P0，按实测决定）

| 立项 | 收益 | 前置 / 触发条件 |
|---|---|---|
| 渲染升级 WebRTC（H.264 + 独立光标通道） | 延迟/带宽/光标流畅度提升（§3.4） | 先实测 JPEG 轮询在移动网络下的瓶颈；需评估代理 C# 与 WebView 双向改造量 |
| 中继服务器（扩 `Server`） | 摆脱第三方隧道，自控分发 | 与 WebRTC/TURN 合并规划 |
| 音频 | 远程可听 | WebRTC 顺带 |

---

## 7. 风险与未决问题（需 Lead 决策）

1. **中文/长文本输入**：代理 `key` 动作是"按键名"语义（`ParseVk` 只认 ASCII/组合键），手机中文输入法产出的是字符串。要么前端逐字符发 `key`（慢且中文会失败），要么**给 `/api/input` 加一个 `text` 动作**（`InputDto` L314–323 加 `text` 字段 + `SendInput` 走 `KEYEVENTF_UNICODE`）。**已列入 §4.5 P0 必需改动**，仍需 Lead 拍板确认是否现在就做（不做则 P0 只能打 ASCII/组合键）。
2. **iOS 后台限制**：即便 Capacitor 壳，iOS 后台长时间跑远程画面仍受系统约束，可能需"前台服务/后台模式"授权或引导用户保持前台——这是 App Store 审核风险点，需产品层面明确预期。
3. **上架合规**：远程控制类 APP 在 Google Play / 国内安卓市场 / App Store 均有**隐私与用途审核**（可能要求"仅限自有设备"声明、防滥用条款）。若只是自用/小范围分发，**侧载 APK / TestFlight** 更现实。
4. **WebRTC 渲染（可选中期）**：需要动代理（C# 媒体编码）+ 前端，是 P0 之外的独立工程。**是否投入取决于 JPEG 轮询在真实移动网络下的实测表现**，建议先跑通 P0 再单独立项评估（编码库选型、双端兼容、TURN 部署）。
5. **令牌一次性化**：当前 `?token=` 会进 URL/日志（`AgentWebServer` 注释已声明是已知取舍），外网化后建议改为短时效一次性票据，需后端配合。

---

## 8. 架构图（ASCII）

### 8.1 现状（局域网 · 网页直连代理，桌面浏览器已验证）

```
 桌面浏览器
     │  ① GET /api/sessions ──────────────► 主程序 (WebServer :WebPort)
     │        （拿 directPort + directToken）       │
     │                                             │ 只出映射，不出流量
     │  ② GET /live?token=… ──────────────► 代理 :directPort ──┐
     │  ③ POST /api/input (X-SoulLink-Token) ► 代理 :directPort  │ 内存环形缓冲
     │                                             ▲              │ GetLatestJpeg
     └──────────── 明文 HTTP · CORS=* · 令牌可空 ───┘              ▼
                                                            SendInput/SetCursorPos
                                                            （目标 RDP 会话内）
```

### 8.2 推荐目标形态（P0：PWA → Capacitor 壳，外网走 WireGuard 隧道）

```
 ┌────────────────────────── 手机 ──────────────────────────┐
 │  Capacitor 壳  ──加载──►  web-index.html（同一份，复用）    │
 │    │ 触摸手势/软键盘/虚拟修饰键/触控板（纯前端，§4）          │
 │    │ 原生桥接：后台续命·剪贴板·方向锁定·沉浸式               │
 └────┼────────────────────────────────────────────────────┘
     │  ② /live + /api/input   （复用现有 HTTP 协议；代理侧新增 text + moverel 接线 + 安全收窄）
      │     ┌────────── 加密隧道（Tailscale/WireGuard，硬前提）─────────┐
      │     │  · 传输加密  · 令牌强制非空  · CORS 收窄  · 二次认证        │
      │     ▼                                                            │
 ┌────┴───────────────────────────── 主机（局域网） ────────────────────┐│
 │  主程序 :WebPort  ① /api/sessions（directPort+directToken）          ││
 │  代理   :directPort  /live（内存 JPEG 帧）  /api/input（键鼠注入）     ││
 │        （提权进程 · 目标 RDP 会话 · SendInput/SetCursorPos）           ││
 └──────────────────────────────────────────────────────────────────┘│
                                                         (WireGuard)───┘
 
  可选中期项（非 P0，按实测）：/live 从 JPEG 轮询 → WebRTC(H.264) + 独立光标通道
                            Server/Lobby.cs 扩展为媒体中继（替代第三方隧道）
```

---

## 9. 相关文档

- `HANDOFF-3.3.19-20261122.md` —— 网页直连代理 + 按需取数 + 三卡顿根因
- `WEB-DIRECT-3.3.5-20261122.md` —— 端口分配 / 端口独占 / 路径白名单
- `PERF-3.3.4-20261122.md` —— 性能基线
- `AGENTS.md`、`docs/RELEASE-RULES.md` —— 性能约束 / 发布规则（本方案不触发发布）
