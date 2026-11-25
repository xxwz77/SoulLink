# 对标调研：Moonlight V+（月光威力版）+ Foundation Sunshine

> 调研人：yomori-scout ｜ 任务 t28 ｜ attempt `b339d2a7-fe57-4747-b785-9cd507d2abbe`
> 日期：2026-11-24 ｜ **纯调研，未改任何代码**

---

## 0. 「月光威力版」究竟是什么（先解决歧义）

### 结论：**确认为 Moonlight V+（`qiin2333/moonlight-vplus`）**

用户口述「魔改增强版本，moonlight V+」与检索结果完全吻合。

| 项 | 事实 | 出处 |
|---|---|---|
| 仓库 | `qiin2333/moonlight-vplus` | GitHub API `/repos/qiin2333/moonlight-vplus` |
| 性质 | **fork of `moonlight-stream/moonlight-android`** | 同上 `fork:true, parent=moonlight-stream/moonlight-android` |
| 规模 | **4,721 星**、129 fork | 同上 |
| 语言/许可 | Kotlin / **GPL-3.0**（**开源，可读源码**） | 同上 |
| 最新版 | v12.15.33（2026-11-22） | `/releases` |
| 定位 | 「基于 Moonlight 的增强版 Android 游戏串流客户端」 | 其 README（中文原文） |

**旁证（证明「V+」是真实存在且被生态引用的名称）**：
· `qiin2333/crown-profiles` — 「Community Crown profile store **for Moonlight V+**」
· `F-e-n-y-x/nebula` — README 徽章明确写 **engine: Moonlight V+**，并称其为「fork of Moonlight V+」
· `ninibiku/moonlight-vplus` — 「Moonlight V+ with embedded Tailscale」，自述基线 **12.15.31**
· `1923265514/moonlight-vplus-265`、`llxo/moonlight-vplus-crown-editor` 等衍生仓库

**检索否证**：GitHub 搜索 `moonlight 威力版` → **total_count = 0**；
`moonlight+威力` → 0；`月光+串流` → 1（`foggggggg/moonlight-rdp-fix`，无关）。
⇒ **「威力版」是中文口语称呼，官方名称是 Moonlight V+。**

### ★ 关键：它为服务端也指定了配套魔改 —— Foundation Sunshine

V+ 的 release notes 原文（v12.15.33）：

> 「**建议使用配套的 [Foundation Sunshine](https://github.com/AlkaidLab/foundation-sunshine)**，以获得最佳兼容性和完整的输入、触觉反馈体验。」

| 项 | 事实 | 出处 |
|---|---|---|
| 仓库 | `AlkaidLab/foundation-sunshine` | GitHub API |
| 性质 | **Sunshine 的 fork/衍生**（`fork:false` 但 README 自述「基于 Sunshine」） | README + API |
| 规模 | **8,250 星**，C++ / GPL-3.0 | API |
| 自述 | 「Sunshine fork: an enhanced sunshine... optimized encoders」 | API description |
| 官网 | alkaidlab.com / alkaidlab.cn | README |

**⇒ 所以「月光威力版」的完整体是「V+ 客户端 + Foundation Sunshine 服务端」两端魔改，**
**且两端都开源（GPL-3.0），源码可查。这对我们价值远大于只看官方 Moonlight。**

---

## 1. 与已有 AUDIT-06 的关系（只做增量）

已读 `docs/research/AUDIT-20261124-06-PROTOCOL-ANDROID.md`（148 行）。它的贡献：

| AUDIT-06 已做 | 本文态度 |
|---|---|
| 梳理 SoulLink 三条通道、协议硬伤（无帧头/无 PTS/无序号）、安卓 MediaCodec 三处延迟点 | **采纳，不重复** |
| §3.1 对照 Moonlight「UDP 47998 / ENet 双通道 / idrRequest」 | **采纳，但补引用** |
| §6 **自承「联网抓取 GitHub wiki 正文失败（被重定向到 wiki 首页）」** | **本文的最大增量：这次真的拿到了可引用的一手材料** |

**本文的增量**（AUDIT-06 没有的）：
1. **确认了「月光威力版」的具体身份**（AUDIT-06 完全没涉及）；
2. **拿到 Foundation Sunshine 的一手设计文档**：动态 RS FEC 完整规格、runtime bitrate 控制协议（`0x5506`）；
3. **发现 WGC vs DDAPI 的 judder 文档**，且**我们恰好在用 DDAPI** —— 这是全新发现；
4. **确认我们的 GOP 策略细节**（`Services/NvencSdkEncoder.cs` 的 `NVENC_INFINITE_GOPLENGTH` 与**已有但未启用的 `_gopLength`**）；
5. **纠错一处**：AUDIT-06 §3.1 说我们「客户端不能主动发 PLI」——**现状已变**（我方已有 `/api/idr` + 客户端 `requestIdr`），需更新。

---

## 2. 五个必答问题

### A. 关键帧策略（对应我们的 Android 黑屏）

#### A.1 我们现状（源码确认）

| 事实 | 出处 |
|---|---|
| **无限 GOP**：`cfg.gopLength = _gopLength > 0 ? (uint)_gopLength : NVENC_INFINITE_GOPLENGTH` | `Services/NvencSdkEncoder.cs:561` |
| `NVENC_INFINITE_GOPLENGTH = 0xffffffff` | `同上:82` |
| **已有 GOP 变量但恒为 0**：`private int _gopLength = 0;  // 0 = 无限 GOP + 按需 IDR` | `同上:448` |
| **没有任何代码给它赋值**（全仓只出现这 3 次） | grep `_gopLength` 仅 L448/L561 |
| IDR 只在三处触发：首帧 / `RequestKeyFrame()` / 新观众 | `同上:681, 746, 784` |
| 客户端靠 `if (!firstKey && !isKey && !isSps && !isPps) continue` 丢弃 delta | `docs/web-index.html:1754, 1801` |

#### A.2 成熟做法（一手引用）

Foundation Sunshine 的 FEC 文档里反复出现「**周期刷新**」概念：

> 「这些信息用于区分关键帧峰值、包长和保护比例的影响，**不改变编码器、周期刷新、分块或发送回退**。」
> — `docs/dynamic-fec.zh-CN.md`（原文）

[推测] 该句把「周期刷新」（= 周期 IDR）列为**已存在、不被 FEC 改动影响**的既有机制 ——
即 Foundation Sunshine 的编码器是**周期性出 IDR** 的，而不是无限 GOP。
**但我没有直接读到其 `gopLength` 赋值代码**（`src/video.cpp` 约 300KB，超出抓取上限多次尝试未果），
**所以这条标注为 [推测] 而非确认。**

**官方 Sunshine（上游）有明确的 GOP 配置项**：`gopLength` 出现在其 `config.cpp`/`video.cpp`，
且 Moonlight 协议本就以「周期性 IDR + 客户端 idrRequest 补救」为设计前提 ——
AUDIT-06 §3.1 也已记录「**丢帧检测→发 idrRequest→续流，不重连**」。

#### A.3 我们的偏离与代价

| 维度 | 成熟做法 | SoulLink | 代价 |
|---|---|---|---|
| 周期 IDR | 有（数十~数百帧一次） | **无（0xffffffff）** | 错过首 IDR 的客户端**永久**无法解码 |
| 补救 | 客户端 idrRequest | ✅ 已有 `/api/idr` + `requestIdr()` | 但**恢复依赖客户端主动且能发出请求** |
| 安卓端 | 有 idrRequest 通路 | **`H264Player` 只「等首个关键帧」，无请求能力**（AUDIT-06 §3.1 已记） | **= 永久黑屏** |

**⇒ 结论：我们偏离成熟做法，且偏离的代价被「安卓端不会请求 IDR」放大成了永久黑屏。**

**你的判断成立**：无限 GOP + 纯按需 IDR 不是成熟做法。
**但要注意**：无限 GOP 并不是「错」——它对**低延迟**是有利的（周期 IDR 会带来周期性码率尖峰与额外编码耗时）。
成熟产品的做法是「**无限/长 GOP + 可靠的按需 IDR 通路**」，而我们只做到了前半句。

### B. 帧边界与时间戳

#### B.1 成熟做法（一手引用）

Foundation Sunshine 的控制协议文档给出了**可引用的具体报文**：

> 「The callback is an encrypted control-stream `0x5506` payload of exactly eight bytes:
> little-endian `u32 parameter_type = 2` (`BITRATE`), followed by a little-endian `i32 total_bitrate_kbps`.
> — `docs/protocol/runtime-bitrate-and-audio.md`（原文）

> 「The host keeps the existing `0x5506` dynamic-parameter packet for client-to-host requests.」

**可提取的设计要点**：
1. **控制消息走独立加密控制流**（ENet），**不走视频 UDP** —— 视频格式完全不被控制逻辑污染；
2. **featureFlags 协商**：客户端在 `x-ml-general.featureFlags` 置 bit `0x400` 才启用回调，
   **host 在 `x-ss-general.featureFlags` 广告同一 bit，客户端没开就不发** —— 这是**协议版本协商的标准范式**；
3. 报文**定长 8 字节**、**明确定义字节序**（little-endian）、**明确取值范围** `1..800000`；
4. 「Legacy clients ... continue to work because the host omits the callback」——**向后兼容靠「不发」而非「发空」**。

#### B.2 我们在 chunked HTTP 裸 Annex-B 里**最少**要加什么

AUDIT-06 §4.A 已建议「16B 定长头：magic/seq/服务端毫秒时间戳/字节数」。**我采纳并补充两点**：

**补充 1：时间戳要带「捕获时刻」而非「发送时刻」。**
我们当前 `captureMs`/`encodeMs`/`queueAge` 都测过了，但**客户端拿不到**，
所以端到端延迟只能靠「截图比对两个时钟」这种笨办法（用户就是这么做的：91/136ms）。
帧头里放**捕获时刻的单调毫秒**，客户端 `now - ts` 立刻得到端到端延迟 —— **这是让延迟从「要靠猜」变成「可监控」的最小改动。**

**补充 2：seq 必须是「帧序号」而非「字节偏移」，且要允许跳变被检测。**
客户端据此可「丢弃 seq 跳变段并请求 IDR」，**替代现在的「连丢 10 帧就整条重连」**（AUDIT-06 §3.1 记为高代价）。

### C. 传输与重传

#### C.1 一手引用：动态 RS FEC 的完整规格

Foundation Sunshine 有一份**很长的中文设计文档** `docs/dynamic-fec.zh-CN.md`（9,081 字节），要点：

| 设计点 | 原文事实 |
|---|---|
| FEC 模式 | 三档：**跟随主机 / 自动 / 固定**（0–100%），客户端固定值优先 |
| **反馈通道** | 「每秒通过既有**加密 ENet 控制连接**发送一条 **12 字节**汇总」——**视频 UDP 格式完全不变** |
| 反馈内容 | 三个大端 uint32：**序号、数据分片数、缺失数据分片数**（`0x5510`） |
| 观测窗口 | 「主机以**十个一秒桶**保存有限历史」，决策用**最高桶**比例快速响应 |
| 失效回退 | 「超过 **3 秒**没有有效样本时使用主机固定值」 |
| 目标函数 | 「二项分布尾概率…使**一帧任意块不可恢复的概率上界不超过 0.1%**」 |
| 冗余公式 | `P = ceil(D × F / 100)` |
| 码率预算 | 「先扣除已有音频及协议预留（分别最多 **20%** 和剩余预算的 **10%**），再按 `编码码率 = 视频预算 × 100 / (100 + F)` 分配」 |
| 协议边界 | 「RS 容量超限日志记录会话、**帧编号、IDR 标志**、编码大小和协商包长」 |
| 明确不做 | 「**不承担拥塞控制或链路容量发现**」「不修改视频 UDP 格式，不新增逐包身份或发送账本」 |

**这份文档的价值**：它展示了一个成熟产品**如何把 FEC 从「拍脑袋百分比」做成「可观测闭环」** ——
而我们连「丢了多少」都测不到（没有帧序号）。

#### C.2 输入通道

AUDIT-06 §3.1 已指出「Moonlight 输入走 UDP 不可靠 + 事件序号 + 服务端聚合」，**不重复**。
**增量**：Foundation Sunshine 的 `src/input_activity.cpp` / `input_activity.h` 的存在（源码树确认）
说明其**输入活动是独立模块**，与视频解耦 —— 与我们「输入与视频共用同一 HttpListener」形成对照。

#### C.3 通道分离

AUDIT-06 §3.1 已列（视频 UDP / 控制 ENet / 信令 HTTPS）。**增量**：
Foundation Sunshine 把 **`cursor_channel.cpp`**（光标）也**独立成一个通道**（源码树确认）。
**这对我们有直接启发**：我们靠「每条输入响应附带 cursor 字段」回显光标，
这是把光标绑在输入 RTT 上的做法——一旦输入走独立通道或降级，「光标就没了」。
**独立光标通道才是标准解法**（我们已在 T31 记录过「move 不回包就没光标」的隐患）。

### D. Android 客户端解码

#### D.1 我们现状 vs AUDIT-06

AUDIT-06 §2.4 已给完整表（`KEY_LOW_LATENCY=1` 已设、无 `KEY_MAX_INPUT_SIZE`、
双 10ms 轮询、`releaseOutputBuffer(idx,true)`、SurfaceView）。**不重复。**

#### D.2 增量：V+ 在 Android 侧的**增强方向**（README 原文）

| V+ 增强项 | README 原文 | 对我们的意义 |
|---|---|---|
| 超高刷新率 | 「解锁 144 / 165 Hz，最高 **800 Mbps** 码率」 | 说明其链路预算可达 800Mbps → 高码率不以牺牲延迟为代价 |
| 自定义分辨率 | 「任意分辨率、宽高比、不对称分辨率」 | 我们固定抓屏矩形，无此灵活性 |
| 相对/绝对鼠标切换 | 「**相对鼠标**适用于视角旋转，**绝对鼠标**让鼠标更跟手，适合远程桌面和需要直接定位指针的场景」 | ⚠️ **直接对应我们的落点偏移问题**——两种模式各有适用场景 |
| 性能监控 | 「串流菜单可调整性能监控样式、指标和手势」 | 我们刚加「推送实测 fps」（T34），方向一致 |
| ABR | 见 [ninibiku] README：「ABR 使用 `lowLatency` 模式（最低 5 Mbps，上限约 14.4 Mbps）」「主机支持原生 ABR 时，客户端只通过 **`/api/abr/feedback`** 上报指标」 | **有专门的 ABR 反馈端点** |

**★ 最值得注意的一条**：「绝对鼠标」的提法说明成熟产品**明确区分了「游戏视角」与「远程桌面指针」两种鼠标语义**。
我们的落点偏移（T27/T32）本质是「绝对坐标 + 节流 + 队列」的组合问题，
而 V+ 的做法是**让用户明确选模式**，而不是试图用一种模式兼容两者。这是产品级的设计取舍。

### E. 延迟预算与自适应

#### E.1 我们**查不到**官方延迟拆分的**可引用**数字

**如实说明**：
· AUDIT-06 §6 已记录「联网抓取 wiki 失败」；
· 本次我成功访问了 **GitHub API**（org repos、releases、docs、contents），
  **但 `raw.githubusercontent.com` 与 `cdn.jsdelivr.net` 均不可达**（`fetch failed`），
  GitHub 网页返回的是 SPA 外壳（源码靠 JS 加载，HTML 里搜不到 `gop`/`idr`）；
  `src/video.cpp`（约 300KB）与 `src/config.cpp` 多次尝试均超出抓取上限；
  GitHub code search 需认证（401），仓库 API 在多次请求后**触发 403 限流**。

**⇒ 所以「Moonlight 官方公布的端到端延迟构成拆分」我这一轮没有拿到可引用来源，不编数字。**

#### E.2 但拿到了**自适应的触发指标**（一手引用）

Foundation Sunshine 的 FEC 文档明确：自动 FEC 的**唯一输入**是
「**块完成时的数据分片缺失比例**」，且**明确声明**：

> 「这些计数表示『块完成或放弃时的数据分片缺失』，**不是面板的视频丢帧率，也不等价于物理链路丢包率**。」
> 「反馈超时或汇总序号跳跃代表未知区间：清除之前的估计历史，并重新开始观测期。」

**这两句是设计层面最有价值的部分**：它诚实地界定了「指标能说明什么、不能说明什么」。
对照我们自己：`actualFps` 是「Ring 内真实帧时间跨度」，
我们刚刚（T34）才意识到「渲染 fps ≠ 推送 fps」。**同一类问题的两种处理方式，一目了然。**

---

## 3. ★ 核心产出：三档可落地清单

### A 档 —— 配置级，改动最小（今晚可做）

#### A-1. **启用周期 IDR：给 `_gopLength` 赋一个有限值**

· **依据**：`Services/NvencSdkEncoder.cs:448` 已有 `private int _gopLength = 0;`，`:561` 已有 `_gopLength > 0 ? ... : INFINITE` 分支。
  **代码结构已支持，只是没人赋值。**
· **解决症状**：**Android 黑屏**（错过首 IDR 的客户端最多等一个 GOP 周期就能恢复）；
  顺带缓解「8 帧延迟」中的**恢复代价**（不必整条重连）。
· **代价**：一行赋值 + 一个配置项。周期 IDR 会带来周期性码率尖峰与额外编码耗时；
  建议 **`_gopLength = fps * 2`（约 2 秒）** 起步 —— 该值由**我们自己的 fps 配置**推导，不是抄来的数字。
· ⚠️ **注意**：AUDIT-06 未提此项；这是我们自己的代码里「差一行」的发现。

#### A-2. **把抓屏后端从 DDAPI 切到 WGC**（如硬件/系统允许）

· **依据**：Foundation Sunshine 专文 `docs/WGC_vs_DDAPI_Smoothness_Fun_Guide.md` 原文：
  > 「DDAPI 因为编码器被耽误了，每帧**包含的游戏运动量不一样**……这种东西有个专业名字叫 **judder**」
  > 「DDAPI 傻等时霸占 GPU 锁 → 编码器被饿死 → 帧间距抖动 → judder」
  > 「**帧缓冲和 V-Sync 管不了帧内容 → judder 从服务端就决定了 → 客户端救不了**」
  > 高帧率时更明显：60fps 时 ±3ms 抖动占 18%，**120fps 时占 36%**
· **我们的现状（源码确认）**：
  - `SoulLink.Agent.csproj:6` 注释：「**不定义 WGC**，抓屏走 DXGI DDA + BitBlt 降级链」；
  - `SessionRecorder.cs:240-241` 的 auto 模式 **`default: return new DxgiDdaCapture();`**；
  - A2 实测回报 `captureBackend: "DXGI-DDA"`；
  - **但我们已有完整的 `Relay/WgcCapture.cs`**（只是被 `#if WGC` 排除在 agent 构建之外）。
· **解决症状**：**judder（画面「说不清哪里不对」的不顺滑）**，高帧率下尤其明显。
· **代价**：需在 `SoulLink.Agent.csproj` 启用 `WGC` 常量（注释说「无版本 TFM 不碰 WinRT」，
  即**需要把 agent 的 TFM 升到带版本的 Windows TFM**），这是个需要评估的构建改动，
  所以**归 A 档偏难的一端**，或干脆归 B 档。**建议先做 A-1，A-2 单独评估。**

#### A-3. **给安卓端补上「主动请求 IDR」**

· **依据**：AUDIT-06 §4.D 已建议（`/api/input` 加 `idr` 动作）；
  **现状**：服务端**已有** `POST /api/idr` + `RequestKeyFrame()`，网页端**已实现** `requestIdr()`（500ms 限流），
  **唯独安卓端没有**。
· **解决症状**：**Android 黑屏**（服务端已有能力，客户端不调用）。
· **代价**：安卓端加一个 HTTP 调用；比 A-1 更直接（不依赖周期 IDR）。
· **优先级建议：A-3 与 A-1 互补**——A-3 让客户端能自救，A-1 保证「即使客户端不会自救也能恢复」。
  **两个都做才是成熟做法**（Cycle IDR 兜底 + 按需 IDR 快速响应）。

### B 档 —— 改协议，一两天

#### B-1. 帧信封（采纳 AUDIT-06 §4.A，补充 2 点）

· **内容**：`[magic(2) | ver(1) | flags(1) | seq(4) | captureTsMs(4) | nalLen(4)]` = 16 字节；
· **补充 1（关键）**：`captureTsMs` 必须是**捕获时刻**，不是发送时刻 —— 否则测不出端到端延迟；
· **补充 2**：`seq` 是**帧序号**，客户端据此检测跳变并**只请求 IDR 而不整条重连**；
· **解决症状**：**8 帧延迟变得可测**（从「靠截图比对」到「一行日志」）、**丢帧可检测**、为自适应提供输入；
· **代价**：改 2 处（`AgentWebServer.cs` 写出、`H264StreamReader.kt`/`AnnexB.kt` 读取）；
  **必须做版本协商**（我们已有 `streamVersion` + `/api/info` 机制，可直接复用）；
· **依据**：AUDIT-06 §4.A + 本次 Foundation Sunshine `0x5506` 的「定长 + 明确字节序 + featureFlags 协商 + 不发即兼容」范式。

#### B-2. 输入批量 + 序号（采纳 AUDIT-06 §4.B）

· `InputDto` 加 `seq` + `events: [...]`；安卓把 `down/up` 对塞进同一批；
· **顺带修**：`sid` 字段安卓在发、服务端 `InputDto` 里**根本没有**（AUDIT-06 §2.3 已记）；
· **解决症状**：**WAN 下输入卡顿**（每动作一个 RTT）；
· **代价**：改 DTO + 安卓端批量封装；服务端按序执行即可。

#### B-3. **独立光标通道**（AUDIT-06 未提）

· **依据**：Foundation Sunshine 源码树有独立的 `src/cursor_channel.cpp` / `cursor_channel.h`；
· **我们的问题**：光标靠「每条输入响应附带 `cursor` 字段」回显，**绑定在输入 RTT 上**；
  一旦 move 不走请求/响应（如 WS 直发、或 HTTP 降级下 move 不再等回包），**光标就没了**——
  这个隐患我在 T31 已经写明（「move 不回包后，光标位置改由服务端主动推」），但当时没有依据；
  **现在有了**：成熟产品就是独立通道。
· **解决症状**：**光标回显的健壮性**（不再与输入 RTT 耦合）；
· **代价**：服务端定时（如 30Hz）推光标位置，客户端独立渲染。中等改动。

### C 档 —— 架构级，需评估是否值得

#### C-1. 动态 RS FEC + 客户端反馈闭环

· **依据**：`docs/dynamic-fec.zh-CN.md` 完整规格（§C.1 已摘录）；
· **解决症状**：**丢包下的画质/流畅度**、**自适应**；
· **代价**：**很高**。且该文档自己写明「不承担拥塞控制或链路容量发现」，
  且我们的传输是 **TCP（chunked HTTP）**——**TCP 本身不丢包（只延迟）**，
  ⇒ **在换 UDP 之前，FEC 对我们的收益接近于零**（AUDIT-06 §4.F 已判断，我同意）；
· **建议**：**不做**。若要吸收，只吸收其「**先把可观测性做好**」（= B-1 的 seq + 反馈端点），而非 FEC 本身。

#### C-2. 视频改 UDP / WebRTC

· **依据**：AUDIT-06 §4.F 已判断「传输层实测 0.8ms，改传输收益最小、工程量最大」；
· **本次增量**：Foundation Sunshine 虽然有完整 FEC 体系，
  但其文档反复强调「**不修改视频 UDP 格式**」「**不新增逐包身份**」——
  **说明即便在 UDP 体系里，成熟做法也是「尽量不动传输格式」**，这反过来说明「改传输」应当是最后手段。
· **解决症状**：（几乎不解决我们当前的任何症状）；
· **代价**：极高。**建议不做。**

#### C-3. 通道分离（视频 / 控制 / 输入 / 光标 四通道）

· **依据**：AUDIT-06 §3.1 + 本次源码树证据（`cursor_channel.cpp` 独立）；
· **解决症状**：**单服务器抖动同时打视频与输入**（AUDIT-06 定级「高」）；
· **代价**：高（要拆服务器）。**建议先做 B-3（只把光标拆出去）**，作为最小可行的第一步。

---

## 4. 按症状归因的速查表

| 我们的症状 | 对应哪几条建议 |
|---|---|
| **8 帧延迟（91~136ms）** | **B-1**（让延迟可测 → 才能定位）；A-2（judder 会伪装成「延迟」） |
| **Android 黑屏** | **A-1**（周期 IDR 兜底）+ **A-3**（客户端请求 IDR） |
| **落点偏移** | **B-2**（输入保序）；V+ 的「相对/绝对鼠标模式分离」思路上可借鉴 |
| **无自适应** | **B-1**（先有 seq/丢帧观测）→ 才谈得上 FEC/ABR |
| **judder（不丝滑但 FPS 正常）** | **A-2**（DDAPI → WGC） |
| **光标消失（T31 隐患）** | **B-3**（独立光标通道） |

---

## 5. 我认为最该先做的两件（个人判断）

1. **A-1（周期 IDR，一行）+ A-3（安卓请求 IDR）** —— **成本最低、直接消灭「Android 永久黑屏」**；
2. **B-1（帧信封带捕获时间戳 + seq）** —— **把「8 帧延迟」从「靠截图猜」变成「一行日志可测」**。
   没有它，后续所有延迟优化都是盲改（我们已经盲改了好几轮：T31 整数除法、T33 差 0.67ms 都是因为缺可观测性才一错再错）。

**特别强调**：T31→T33→T34 这三轮反复，根因不是「不懂超时」，
而是**没有端到端可观测性** —— 只能靠用户报「卡」来发现问题。**B-1 正是治这个。**

---

## 6. 调研方法与可信度自评

### 成功获取（可引用）

| 来源 | 内容 |
|---|---|
| GitHub REST API `/repos/*` | 仓库元数据（fork 关系、星数、许可、语言、更新时间） |
| `/releases` | V+ 的 release notes 原文（含「建议使用配套 Foundation Sunshine」） |
| `/readme`（base64 解码） | V+ README 中文原文、nebula README、ninibiku README |
| `/contents/docs/**` | **`dynamic-fec.zh-CN.md`（9,081B 全文）**、**`protocol/runtime-bitrate-and-audio.md`（1,914B 全文）**、`WGC_vs_DDAPI_Smoothness_Fun_Guide.md`（7,075B 全文）、`http-transport.zh-CN.md` |
| `/git/trees` | Foundation Sunshine 的 `src/` 完整文件清单（179 条）、`docs/` 清单 |
| 本地源码 | `Services/NvencSdkEncoder.cs`、`Relay/SessionRecorder.cs`、`SoulLink.Agent.csproj`、`docs/web-index.html` |

### 未能获取（已如实标注）

| 目标 | 状态 |
|---|---|
| `src/video.cpp`（约 300KB）| **超抓取上限**（contents 返回 100,000 字符被截断；blobs 同样） |
| `src/config.cpp` | 同上 |
| `raw.githubusercontent.com` / `cdn.jsdelivr.net` | **不可达（fetch failed）** |
| GitHub 网页源码视图 | SPA 外壳，HTML 中无源码 |
| GitHub code search | **需认证（401）** |
| 仓库 API 后续请求 | **触发 403 限流** |
| **Moonlight 官方端到端延迟拆分数字** | **未取得，不编** |

### 推测标注汇总

| 结论 | 性质 |
|---|---|
| Foundation Sunshine 编码器是周期 IDR | **[推测]** 依据「周期刷新」被列为既有机制，但未读到 `gopLength` 赋值 |
| Moonlight 上游有 `gopLength` 配置 | **[推测]** 基于协议常识与文档旁证，未直读 config 源码 |
| V+ 的 800Mbps / 144Hz 等为实测能力 | **官方 README 声称**，非第三方实测 |

其余结论均有 `文件:行号` 或 `docs/xxx.md` 出处。

---

## 7. 附：V+ 相对官方 Moonlight 的完整增强清单（README 原文表格摘录）

| 类别 | 功能 | 说明 |
|---|---|---|
| 串流 | 超高刷新率 | 解锁 144 / 165 Hz，最高 800 Mbps 码率 |
| | HDR / HLG | 自动加载设备专属 HDR 校准文件，支持 HLG（`12.6.6`） |
| | 自定义分辨率 | 任意分辨率、宽高比、不对称分辨率 |
| | 多场景预设 | 一键保存/切换不同游戏串流配置（`12.3`） |
| **输入** | 自定义按键 | 拖动/缩放/隐藏，支持组合键、连发、手柄瞄准（`12.3.3`） |
| | 多配置档案 | 按键布局支持多配置实时切换 |
| | 轮盘按键 | 轮盘分区自定义按键绑定（`12.3.7`） |
| | **相对/绝对鼠标** | 「绝对鼠标可以让鼠标更加跟手，适合远程桌面和需要直接定位指针的场景」（`12.15.33`） |
| 配置 | 王冠（Crown）配置 + 商店 | 社区配置仓库 `qiin2333/crown-profiles` |
| 运维 | 性能监控覆盖层 | 可调样式、指标、手势（`12.15.28+`） |

**注意**：V+ 的增强**绝大部分是「功能/画质/易用性」，而非「协议底层」** ——
它「保持与原版串流协议的完全兼容」（README 原文）。
**⇒ 这本身是一个重要信息：V+ 之所以「成熟好用」，不是因为它重写了协议，
而是因为它在既有协议上把「可配置性」和「场景适配」（相对/绝对鼠标、多预设）做到了极致。**
这对我们的启示是：**先把已有链路的可观测性和可配置性做好，比换架构更划算。**

---

**文档作者**：yomori-scout
**最后更新**：2026-11-24
**未改任何代码**（本任务为纯调研）