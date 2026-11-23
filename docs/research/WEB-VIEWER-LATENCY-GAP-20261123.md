# 网页端播放链路逐毫秒审计与成熟项目对照（2026-11-23）

> 作者：viewer-auditor（dsh-team `team_mwt4bbjy11d7lsz` / task `task_mwt4bbsj11il5an`）
> 范围：**只读审计**。未改任何产品代码/脚本/配置；唯一写入为本研究文档。
> 证据标记：**【代码事实】**＝工作区源码可证（file:line）；**【实测】**＝本仓库前序文档/日志实测或本次 Node 语义验证；**【外部源码】**＝GitHub API 抓取的 moonlight-qt / moonlight-chrome / moonlight-common-c 一手源码（file:line 可回查）；**【推断】**＝有依据未实测；**【待验证】**＝明确假设需实验。
> 定标：**127.0.0.1 回环，网络 ~0**；验收标准（Lead 2026-11-23 晚）：交互期 0 静止误判、全链路 ≤80ms（冲刺 50ms）、拖动跟手无粘键、无黑闪；网页体验须 ≥ 向日葵/UU 外网水准。

---

## 0. 结论（按用户 2026-11-23 晚定调置顶）

### 0.1 三句话

1. **「连不上/反复断」有一条确定性 bug 链**：前端看门狗计时器 `stallTimer` 声明在 `try` 块内、却在 `finally` 里引用 —— 块级作用域下这是 **ReferenceError**，任何一次断流的收尾都会抛未捕获异常并跳过清理（h264Reader/decoder 泄漏、旧 timer 空转）。叠加「NVENC 无限 GOP + reset 兜底拿不到 IDR → 只能等 10s 看门狗 → 重连」的服务端结构，构成 P0.5「断开→重连→断开」不收敛的完整机制【代码事实 + Node 语义验证】。
2. **回环下 453ms 全是「管线税」**：约 2/3 来自服务端串行节拍（静止去重把 30fps 拖成低占空比 + 10ms 轮询读队列 + 逐 NAL Flush 拼包），约 1/3 来自浏览器管线（fetch chunk 合并 + 解码队列 + drawImage 无呈现对齐）。网络段按 ~0 计（127.0.0.1 定标）。
3. **「换形态」不是根治的第一步**：向日葵类网页端走 WebRTC（SRTP 视频轨道，天然帧边界、内建重连/带宽自适应），稳定性确实更好；但 SoulLink 回环场景的首要病灶是服务端节拍与前端 bug，**先修 W1-W6 止血（<半天）再评估 WS 分帧**——fetch 形态本身在回环下的延迟税约几十 ms，不是 453ms 的主因。WebTransport/WebRTC 重型改造按用户定位留给安卓端与远期。

### 0.2 用户三问的直接回答

**Q1（fetch 拉流 vs 向日葵类 WebRTC，稳定性差距在哪）**：
- WebRTC 形态的稳定性优势是**机制性**的：SRTP 视频轨道自带帧边界/分片重排/PLI 关键帧请求（= 客户端能向服务端要 IDR）；ICE/DTLS 有内建重连与拥塞反馈（TWCC/REMB）。向日葵网页端量产可用，靠的就是这套【推断（外部公开知识），非本次源码核对】。
- SoulLink fetch 形态的**全部重同步手段只剩「断开重连」**：服务端 NVENC 无限 GOP（`Services/NvencSdkEncoder.cs:549`）没有周期性 IDR，按需 IDR 只在「新 viewer 订阅」时触发（`Relay/SessionRecorder.cs:536`）。前端 `decoder.reset()` 是本地行为、服务端不知道 → 无限 GOP 下 reset 后永远等不到 SPS/IDR → 画面冻结 → 10s 看门狗 → fail → 重连（新订阅才有 IDR）。**「断流」是唯一的重同步通道，这就是循环不收敛的结构性根源**【代码事实，链路完整可证】。
- 结论：**稳定性差距主要不在 TCP vs UDP，而在「客户端无法请求关键帧 + 无重连协商」**。fetch+WS 都可以补齐（WS 加一条控制消息通道即可），不必换 WebRTC 才能稳定。

**Q2（延迟差距）**：回环下 WebRTC 与 fetch+WS 的传输层延迟差 <5ms 级【推断】。453ms 的构成见 §2 逐毫秒表：主导项是服务端节拍与解码队列，任何传输形态都救不了这两项。**换形态≠根治延迟**。

**Q3（是否换形态才根治）**：**不是**。根治顺序 = ①修 W1 bug 链（10 分钟）→ ②重同步机制（W2/W7 控制通道）→ ③服务端节拍（10ms 轮询与静止误判，stream-core 侧）→ ④WS 分帧（若埋点证明 fetch 合并税 >20ms）→ ⑤WebRTC/WebTransport 留给安卓端。向日葵能成不等于我们必须用 WebRTC 才能成——**它成在「客户端能要 IDR + 帧边界 + 自适应」，这三件事在 HTTP/WS 形态里也能做**。

---

## 1. P0.5 重连循环不收敛：根因链（第一交付物）

### 1.1 确定性 bug：stallTimer 作用域（【代码事实】+ Node 语义验证）

- `docs/web-index.html:1107`：`const stallTimer = setInterval(...)` 声明在 **try 块内**（try 从 :1067 开始）。
- `docs/web-index.html:1269`：`finally { clearInterval(stallTimer); ... }` 在块外引用。
- JS 的 `const` 是块级作用域：finally 属于函数体块，try 块内的 `const` 对它**不可见**。我用 Node 对同构片段实测：`try { const t = 1 } finally { clearInterval(t) }` → **`ReferenceError: t is not defined`**。
- 后果链：任何一次流结束/异常/看门狗触发（done、HTTP 断开、decoder error、STALL 超时）→ finally 抛未捕获异常 → :1270-1274 的收尾（`h264Active=false`、`h264Reader=null`、`decoder.close()`）**部分不执行** →
  - `h264Reader` 残留旧 reader（下一轮 `abortStream` 才能收，若异常发生在换代后则泄漏）；
  - `decoder` 不 close（VideoDecoder 实例泄漏，多次循环后 decodeQueueSize 累积假象）；
  - 旧 `stallTimer` 未清除 → 每 1s 空转一次（有 `myGen !== h264Gen` 守卫不误触发，但计时器泄漏）。
- **这条 bug 与用户观察的「断开→重连→断开」时间线对齐**：重连本身由 fail() 排上，但每轮收尾都异常，状态逐轮变脏【推断（机制成立）】。

### 1.2 结构性根源：无限 GOP 下 reset 是死路，重连是唯一出口

- 服务端：`gopLength = NVENC_INFINITE_GOPLENGTH`（`Services/NvencSdkEncoder.cs:549`）→ 无周期 IDR；按需 IDR 仅两处：首帧（:446/:623/:668-670）与 `RequestKeyFrame()`（:707），而后者只被 `SessionRecorder` 在「新观众 TrySubscribe / 强制关键帧请求」时调用（`Relay/SessionRecorder.cs:536`、:415-421）。
- 前端：连续跳 60 AU 后 `decoder.reset(); configured=false`（`web-index.html:1153-1159`）→ 等新 SPS/IDR，**服务端不会发**（它不知道客户端 reset 了）→ 画面永久冻结 → 10s 看门狗（:1100-1113）→ fail → 重连 → TrySubscribe → _forceKeyFrame → 才恢复。**每轮白付 10 秒黑屏**。
- 退避叠加：指数退避上限 8s（:905）+ 看门狗 10s → 单轮最坏 ~18s「连接中」。用户看到的「一直连接中」由此而来。

### 1.3 新增场景：调节码率/preset/帧率（热更新）后「连不上」——关联链与验证方法

**链路（【代码事实】）**：
1. 网页滑杆 → POST `/api/settings`（`Services/WebServer.cs:930-958` 写 `s.StreamFps/VideoBitrateKbps/NvencPreset/VbvBufferIncreasePercent`）→ 主程序下发环境变量给代理。
2. 代理 worker 检测 `parameterVersion != appliedVersion` → **Dispose 编码器、encoder=null**（`Relay/SessionRecorder.cs:389-397`，注释明言「保留观众 HTTP 连接……viewer 不重建、不断开」）。
3. 下一帧循环重建编码器（:445-455）→ NVENC 新实例首帧 FORCEIDR+SPS/PPS（`NvencSdkEncoder.cs:668-670`）→ 理论上无缝。
4. **但重建窗口内有三个坑**：
   a. **参数热切与静止去重互斥**：静止判定（:432-437）在重建判定（:445）**之前**执行，且 encoder==null 不影响跳过 —— 画面静止时每帧都被 `SleepUntil` 跳过，**重建被无限推迟，新 IDR 永不来**【代码事实：两处判定顺序 + encoder null 无守护】。
   b. **首帧超时路径**：`TrySubscribe` 等 initial 最多 25s（`AgentWebServer.cs:414-418`）——重建慢时新观众等不到帧；已有观众因 (a) 无新 NAL 触发前端 10s 看门狗 → 断开重连 → 每次 TrySubscribe 又强制 IDR。
   c. **重连风暴自激**：每次重连都强制「静止也编码」的 IDR（`SessionRecorder.cs:441`）→ 30fps 节拍插大 IDR（NVENC P3 下 IDR 33KB+，`NVENC-SDK-RCPARAMS` 实测）→ 前端解码队列瞬时积压 → 又可能触发跳帧→reset→冻结→看门狗……闭环【推断，机制环闭合】。
- **18:06-18:10 五连断与调节操作的相关性**：时间吻合（用户 18:10 反馈「一直连接中」，`HANDOFF §P0.5` 记录 18:06:56→18:10:00 五次断开/重连）。**验证方法**（不动 A2，用独立代理实例）：
  1. session-1 独立 agent（端口 19095+，token test123），开 /stream；
  2. POST `/api/settings` 改 `videoBitrate`（8000→12000），前端保持打开；
  3. 观察三个时间点：(i) 日志「[video] 参数热更新」后多少 ms 出现下一个 NAL（`lastNalAt`）；(ii) 前端是否 ~10s 后打出「画面 10000ms 无新帧」；(iii) 是否同时出现「新观众按需 IDR」日志（= 断开重连已发生）。
  4. 判定：若 (i)→(ii) 间隔 ≈10s 且 (iii) 与 (ii) 同帧出现 → 坐实「调节→重建延迟→前端看门狗→重连→强制 IDR」链；静止画面下 NAL 永不来 → 坐实坑 (a)。
- **关联修复**：对应 W2/W7（重连改控制消息式）+ W8（热切后主动向在线 viewer 发 IDR，不能只靠「新实例首帧必 IDR」——坑 (a) 会让首帧不来）。

### 1.4 staticSkipped 暴涨与前端的关系

- 计数在服务端（`SessionRecorder.cs:432-437`）：DDA 静止复用 `_lastBgra`（`DxgiDdaCapture.cs:193-198`）→ 全帧 `SequenceEqual` 相等 → skip。
- **前端放大器**：页面隐藏即断流、回前台即重连（`web-index.html:1882-1894`）；重连风暴 → 每次 TrySubscribe 强制 IDR → 节拍里插大帧 → 解码积压 → 看门狗 → 再重连。**交互期暴涨（1956→3458）是「DDA 出帧滞后 + 重连风暴」的合并计数**【推断】。
- 「交互期间 0 静止误判」需服务端把指针移动/微脏区计为变化（DDA dirty rects 目前丢弃，`DxgiDdaCapture.cs:167` 只传 `out _`）——前端无法单独达标，归属 stream-core 侧。

---

## 2. 逐毫秒延迟预算表（127.0.0.1 回环，网络 ~0）

| # | 环节 | 耗时 | 证据 | 等级 |
|---|---|---|---|---|
| ① | 画面变化→DDA 出帧 | 0.1~18ms | `DxgiDdaCapture.cs:167`（16ms 超时；有变化立即） | 代码事实 |
| ② | **静止误判跳帧** | 交互期把 30fps 拖成 ~50% 占空比（+33ms/跳过帧） | `SessionRecorder.cs:432-437` | 代码事实 |
| ③ | BGRA→NV12 | 8~13ms@2560×1440 | `SessionRecorder.cs:457-459`；%TEMP%\soullink-relay-79832.log `convertMs=8` | 实测 |
| ④ | NVENC 编码 | 1~3ms | 同日志 `encodeMs=1~3` | 实测 |
| ⑤ | 观众队列→HTTP 写出 | 0~10ms 轮询延迟 | `AgentWebServer.cs:441-448`（null 时 `Task.Delay(10)`） | 代码事实 |
| ⑥ | fetch chunked 到达浏览器 | 0~数十 ms（HTTP.sys/浏览器合并小写出） | `AgentWebServer.cs:436-447` 逐 NAL Write+Flush；`web-index.html:1257-1258` 注释自认「network chunk 边界≠AU 边界」 | 代码事实+推断 |
| ⑦ | splitAnnexB+AU 攒批 | <1ms | `web-index.html:1002-1033` | 推断 |
| ⑧ | decode 队列排队 | 0~5 帧（SKIP_QUEUE=5，+165ms 才反应） | `web-index.html:1138,1148` | 代码事实 |
| ⑨ | 解码执行 | 1~4ms@1440p 硬解 | 未测 | 待验证 |
| ⑩ | output→drawImage | 1~5ms（同步绘制、无 rVFC 对齐、无测量） | `web-index.html:1069-1086` | 代码事实 |
| ⑪ | 合成上屏 | 0~1 vsync（0~16.7ms@60Hz） | Chrome 合成器行为 | 推断 |
| ⑫ | 时间戳假节拍 | Chrome 可能做内部平滑 | `web-index.html:1185`（ts+=66666，注释「与服务端一致」已过时：服务端默认 30fps，`SessionRecorder.cs:67`/`WebServer.cs:900` clamp 30~60） | 待验证 |

**结论**：②+⑤+⑥+⑧ 主导（最坏 200ms+），任何传输形态都改变不了这四项 —— **453ms 的账在管线，不在网络**。

---

## 3. 拉流循环架构审计（fetch ReadableStream）

**做对的**：代际作废机制（:852-873）；AU 攒批符合 W3C AVC Registration（:1116-1124；`STREAM-VERIFY §5` 实测 13 帧）；从 IDR 起播（:1246-1249）；看门狗以解码帧为准（:1100-1105）。

**问题**：
1. **块边界≠帧边界**（§2-⑥）：fetch 流没有「这条消息就是一帧」的语义，服务端逐 NAL 小写出被 HTTP.sys 合并，最后一帧在缓冲里等下一批字节【代码事实（注释自证）+推断（粒度）】。
2. **解码队列阈值太松**：SKIP_QUEUE=5（30fps 下 +165ms）、SKIP_RESET_LIMIT=60（2s）。对照 Moonlight 桌面 15 帧上限但配合「队列满→flush 整条+立即要 IDR」（`mlcc_VideoDepacketizer.c:558-561` 本地快照）；我们 reset 后**无 IDR 可拿**（§1.2），兜底形同虚设【代码事实】。
3. **时间戳假节拍**（§2-⑫）【待验证】。
4. **HOL 不咬人**：回环丢包≈0（与 `LATENCY-BENCHMARK §3.2` 一致）。

---

## 4. VideoDecoder 配置与渲染路径

- `configure({ codec: avc1.PPCCLL, optimizeForLatency: true })` 纯 Annex-B 无 description，合规且实测可解（`STREAM-VERIFY §5.3`）【实测】。
- **渲染时机无对齐**：output 回调里同步 `ctx.drawImage`（:1069-1086），无 `requestVideoFrameCallback`/`requestAnimationFrame` —— 解码完成时刻 vs vsync 完全无关联，且**这一环节从未被测量**（`LATENCY-BENCHMARK §1.1⑦⑧`「未实测」）【代码事实】。
- 成熟做法：output 只存帧（latest-wins），rVFC 呈现回调里 drawImage 最新帧 —— 对齐呈现且天然丢旧。

---

## 5. 成熟项目逐环节对照（同级回环/局域网口径）

| 环节 | Moonlight-qt（桌面）【外部源码】 | Moonlight 浏览器客户端 | Sunshine 服务端【本地快照】 | SoulLink 网页现状 | 差距判定 |
|---|---|---|---|---|---|
| 传输 | ENet 可靠 UDP（`mlcc_VideoStream.c:10` rtpSocket） | moonlight-chrome 是 NaCl+Pepper 时代产物（pp::VideoDecoder + GL 直绘），**已随 PNaCl 废弃**；现代浏览器端标准做法是 WebSocket/WebTransport 分帧 | UDP + Reed-Solomon FEC（`sunshine_stream.cpp` 快照） | fetch chunked 单字节流 | **回环下 TCP 不是病灶；缺的是帧边界语义** |
| 帧边界/重同步 | RTP 序列 + 15 帧队列 + RFI/IDR 回传（`mlcc_VideoDepacketizer.c:94,151-165`） | 同左（common-c） | IDX_REQUEST_IDR_FRAME/IDX_INVALIDATE_REF_FRAMES 回传（`sunshine_stream.cpp` 快照） | **无任何客户端→服务端控制通道** | **最大机制差距** |
| 解码 | FFmpeg + D3D11VA 硬解（独立解码设备） | Pepper VideoDecoder（历史） | — | WebCodecs + optimizeForLatency | 同级 |
| 呈现 | 解码纹理→共享纹理→渲染设备→**D3D11 FLIP_DISCARD swapchain→Present(0)**；双设备 fence；ALLOW_TEARING 关 vsync（`d3d11va.cpp` blob sha 54f094be：:551-603/:569-573/:787-827） | PaintPicture：GL 直绘+SwapBuffers（`viddec.cpp:324-386`）；渲染跟不上直接丢帧（:420-425） | — | output 回调同步 drawImage 2D canvas | 缺呈现对齐与测量 |
| 帧节奏 | pacer + SyncInterval 0 | 同左 | — | 服务端固定节拍 + 前端假 ts | 待清理 |
| 输入 | 独立通道+相对位移合并 | 同左 | NV_REL_MOUSE_MOVE_PACKET 独立包 | 已做对（`MATURE-LATENCY-PLAYBOOK §4` 已核） | 达标 |

**moonlight-qt Windows 呈现一手证据要点**（可回查）：FLIP_DISCARD + BufferCount=5（:557,:577）；**故意不用 SetMaximumFrameLatency(1)**——注释解释 latency=1 会让 SyncInterval 0 的 Present 阻塞在 DWM（:569-573）；vsync 关→ALLOW_TEARING（:592-603）；Present 恒 (0, flags)（:827）；解码/渲染双 D3D11 设备+shared fence（`d3d11va.h`）。
**moonlight-chrome 的教训**：Pepper 已死，但其「渲染跟不上解码就丢帧」思想是对的；**不要对标它，对标现代 WebCodecs 实践**。
**向日葵类 WebRTC**：SRTP 轨道自带帧边界+PLI 关键帧请求+内建重连/带宽自适应——**它量产可用的根子是「客户端能要 IDR + 帧边界 + 自适应」，这三件事在 HTTP/WS 形态同样可做**（W7 控制通道 + WS 分帧）【推断（公开知识），非源码核对】。

---

## 6. 修复建议（两层）

### 6.1 网页止血项（按「画面稳定呈现+连接稳固优先，回环 ≤80ms 冲刺 50ms」倒推）

| # | 改动 | 位置 | 作用 | 成本 |
|---|---|---|---|---|
| **W1** | **修 stallTimer 作用域**（声明提 try 外 / finally 判空） | `web-index.html:1107/:1269` | 消除每次断流的未捕获异常+收尾中断+泄漏（§1.1） | **10 分钟** |
| **W2** | **reset 改重连式重同步**：`decoder.reset()` 后不干等 IDR，直接 `abortStream()+scheduleReconnect()` | `web-index.html:1153-1159` | 无限 GOP 下 reset 是死路（§1.2） | 0.5h |
| **W3** | **SKIP_QUEUE 5→2、SKIP_RESET_LIMIT 60→10** | `web-index.html:1138-1139` | 30fps 下 +165ms 才反应，与 ≤80ms 冲突 | 10 分钟 |
| **W4** | **断流/隐藏时补发 up**（abortStream 里发 /api/input up，try/catch） | `web-index.html:868-873` | 粘键（断线释放） | 0.5h |
| **W5** | **rVFC 埋点**：decode 记 ts，rVFC 算帧龄 P50/P95 显示到 #fpsinfo | output 回调+新增 | 无数字则无法证伪管线税；服务验收量化 | 0.5h |
| **W6** | **时间戳节拍跟随服务端 streamFps**（/api/sessions 已下发） | `web-index.html:1185` | 消除 Chrome 内部平滑隐患 | 10 分钟 |
| **W7** | **加客户端→服务端控制通道**（最简：POST /api/idr；或 WS 控制帧）+ 服务端 `RequestKeyFrame(viewerId)` 接线 | `AgentWebServer.cs` + `SessionRecorder.cs:536` 同款 _forceKeyFrame | **根治重同步**：reset/花屏 1 帧内拿 IDR，替代 10s 看门狗；同时修复热更新「连不上」（§1.3 坑 b） | 0.5~1 天 |
| **W8** | **参数热切后主动向在线 viewer 发 IDR**（不只靠新实例首帧） | `SessionRecorder.cs:389-397` | §1.3 坑 a/c：静止+重建互斥会让首帧永不来 | 0.5h（stream-core 侧） |

**顺序**：W1→W2→W3 立即（合计 <1.5h，直接命中「连接稳固」）；W4/W5/W6 半天内；W7/W8 是根治项（1~1.5 天，跨前后端）。

**若 W1-W8 后回环仍 >80ms**（不默认浏览器无解）：结构性剩余风险是 ⑥（fetch 合并）与 ⑧（解码队列）。**最小改造方案**：
- **WebSocket 二进制分帧直推**：服务端为 /stream 增加 WS 端点（HttpListener 不支持 WS：用 `System.Net.WebSockets`+自管 TCP，或单独 ASP.NET Core Kestrel 只服务 /stream），**每帧一条二进制消息**（帧边界=消息边界，顺带承载 IDR 请求控制帧）——一步消除 ⑥ 合并税 + 落地 W7 通道。约 1~1.5 天。**WebTransport 不做**（与用户定位一致，列安卓端/远期）。
- **decode 换 rVFC 呈现**（output 只存帧 latest-wins，rVFC 里 drawImage）——对齐 vsync 且天然丢旧【推断，W5 数字验证收益】。
- 服务端配套（stream-core 侧，直接决定网页达标）：消灭 `Task.Delay(10)` 轮询（事件唤醒写线程）；交互期 0 静止误判（DDA dirty rects/指针区域计变化，或去重加「输入活跃窗口」豁免）。

### 6.2 留给安卓端的根本项

| # | 项 | 依据 |
|---|---|---|
| A1 | UDP+FEC+IDR/RFI 控制回传（对标 Moonlight ENet/Sunshine UDP+RS-FEC） | 公网/高码率场景才必要（`LATENCY-BENCHMARK §3.2`） |
| A2 | WebTransport（浏览器形态远期） | 同上 |
| A3 | MediaCodec 直通 + SurfaceControl 直呈 | 产品定位：安卓端是重头戏 |
| A4 | GPU 零拷贝完善（纹理直通不落 CPU） | `HANDOFF §P3` |

### 6.3 验收对照（127.0.0.1 回环）

| 验收标准 | 止血项覆盖 | 备注 |
|---|---|---|
| 交互期间 0 静止误判跳帧 | W8 + stream-core 侧 dirty rects/输入豁免 | 前端无法单独达标（§1.4） |
| 全链路 ≤80ms（冲刺 50ms） | W1-W6 + WS 分帧（若埋点证明 fetch 税>20ms） | 需 W5 数字复验 |
| 拖动跟手无粘键 | W4 + LOOK_TAP_MS 调优（`web-index.html:1368`，250ms 长按判定让快速拖动前 250ms 不发 down）【推断】 | 输入三件套之外补「断线释放」 |
| 无黑闪 | W2/W7（重同步 1 帧内）+ W1（收尾不再异常） | 10s 看门狗黑屏提示即「黑闪」体感来源 |

---

## 7. 待验证清单（不猜）

| 项 | 方法 |
|---|---|
| HTTP.sys/浏览器合并粒度 | W5 埋点量「服务端编码时刻→浏览器 read 时刻」；或直接对照 WS 版本 |
| 调节参数→断流链路（§1.3） | 独立代理实例 + /api/settings 改码率 + 观察日志时间线（方法已给 §1.3） |
| Chrome 在 optimizeForLatency+错位 ts 下是否内部平滑 | W6 前后 A/B |
| drawImage 1440p 实际耗时 | rVFC 埋点 |
| 453ms 精确分段 | 需真机（本任务不用 A2）；建议下任务带 W5 实测 |
| 向日葵 WebRTC 细节口径 | 公开知识推断，未源码核对（如需精确对照另立任务） |

---

## 8. 证据索引

**前端（docs/web-index.html）**：852-873 代际/abortStream · 903-910 指数退避(1s→8s) · 1002-1033 splitAnnexB · 1067 try 开始 · 1069-1086 output 同步 drawImage · **1107 stallTimer（try 块内）** · 1100-1113 看门狗 10s · 1138-1139 SKIP_QUEUE=5/RESET=60 · 1144-1189 flushAu（1153-1159 reset 兜底） · **1185 ts+=66666** · 1192-1259 拉流循环 · **1266-1275 finally 引用 stallTimer（ReferenceError）** · 1882-1894 visibilitychange 断流/重连 · 1368 LOOK_TAP_MS=250

**服务端**：`Relay/AgentWebServer.cs:404-455`（WriteStreamAsync 逐 NAL+Flush+Task.Delay(10)）；`Relay/SessionRecorder.cs:389-397`（参数热切丢编码器保 viewer）· :415-441（forceKeyFrame/新观众 IDR）· :432-437（静止去重）· :482-492（viewer 队列 4）· :522-550（TrySubscribe→_forceKeyFrame）· :571-577（SleepUntil 绝对节拍）；`Relay/DxgiDdaCapture.cs:167`（16ms 超时、脏矩形丢弃）· :193-198（静止复用）；`Services/NvencSdkEncoder.cs:549`（无限 GOP）· :668-670（FORCEIDR）· :707（RequestKeyFrame）；`Services/WebServer.cs:900`（StreamFps clamp 30-60）· :930-958（settings 写回）；`Services/WebDirect.cs:62-72`（白名单无 WS）

**外部源码（GitHub API 抓取，可回查）**：moonlight-qt `d3d11va.cpp`（sha 54f094be458f399b46362328070f55a951f32f19）：:551-603/:569-573/:787-827；`d3d11va.h` 双设备 fence；moonlight-chrome `viddec.cpp` :324-386/:412-438；moonlight-common-c `VideoStream.c:26`（RTP_QUEUE_DELAY=10ms）· `VideoDepacketizer.c:94/:151-165/:558-561`；Sunshine/OBS 本地快照 `artifacts/latency-playbook-src/`

**实测**：%TEMP%\soullink-relay-79832.log（30fps/1440p/convertMs 8/encodeMs 1~3）；soullink-relay-76796.log（15fps 软编对照）；Node 作用域语义验证（本审计复现 ReferenceError）。

## 9. 边界遵守自查

未改任何产品代码/脚本/配置；未碰 dist/stable；未使用/控制 A1/A4；未重启任何进程；未发布；未碰 COS/latest.json；未新建团队；唯一写入为本文档。README.md 登记留给 Lead。外网仅 GitHub 公共 API 只读。

> 附注：本报告按 Lead 三条定调组织——「连接稳固优先」（§1 为第一交付物）、热更新断流场景（§1.3）、向日葵/WebRTC 形态对照（§0.2、§5）。浏览器管线逐毫秒拆解见 §2-§4。
