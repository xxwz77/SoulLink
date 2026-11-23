# Web 观看端（浏览器侧）管线审计 + 系统性缺陷排查

> 日期：2026-11-24
> 作者：web-client（dsh-team `team_mwtjn0d77v8gtr` / task `task_mwtjod3b8gfa22`）
> 范围：**只读审计**。未改任何产品代码/脚本/配置；唯一写入为本研究文档。
> 代码基线：工作区 HEAD 当前状态（3.3.34 之后，含 GPU 零拷贝、可见光标、落点偏移修复均已提交）。
> 证据标记：**【代码事实】**=当前源码可证（file:line）；**【实测】**=本仓库前序文档/日志实测；**【推断】**=有依据未实测；**【待验证】**=明确假设需实验。
> 行号说明：网页端**唯一事实源是 `docs/web-index.html`**（由 `scripts/sync-webpages.ps1` 同步到 `dist/web-index.html` 供主程序读取；`Services/WebPages.cs` 只是同一内容的兜底嵌入常量，行号 = web-index.html + 16）。本文所有前端行号均指向 `docs/web-index.html`，服务端指向对应 `.cs`。

---

## 0. 结论摘要（置顶）

1. **任务简报假设的「WebSocket / MediaSource」在浏览器侧根本不存在**，这是一条必须先纠正的前置事实。实际链路是：`fetch() + ReadableStream`（HTTP chunked 长拉）→ `splitAnnexB` 手工切 NAL → `WebCodecs VideoDecoder`（Annex-B 纯流，无 description）→ `output` 回调里 `ctx.drawImage` 直绘 `<canvas id="vframe">`。`MediaSource/SourceBuffer`、`<video>` 直播、`playbackRate`、jitter buffer **全部不存在**；`<video id="vid">` 只用于录像回放的 mp4 播放（`web-index.html:2285-2293`）。

2. **浏览器侧不是 444ms 的主因**。结构证据：浏览器侧没有任何能容纳 444ms 的缓冲——`optimizeForLatency:true` 已设（`:1250`）、唯一队列 `decodeQueueSize` 被 `SKIP_QUEUE=2` 主动丢帧封顶（`:1162/:1172`）、绘制在 `output` 回调立即 `drawImage`（`:1101`）无 rVFC 攒帧。浏览器稳态贡献约 **10~30ms**，最坏约 **50~90ms**（解码队列 ≤2 帧 + 合成 1 vsync）。444ms 主因在**服务端 GPU 零拷贝未生效、DDA 仍走 CPU 读回**（见 §5 与 `HANDOFF-VIDEO-LATENCY-GPU-20261124.md`）。

3. **前序审计（`WEB-VIEWER-LATENCY-GAP-20261123.md`）的 W1-W3 已落地**：`stallTimer` 作用域 bug 已修（现在声明在 try 块外 `:1070`）、`SKIP_QUEUE 5→2 / SKIP_RESET_LIMIT 60→10` 已改（`:1162-1163`）、reset 兜底已改为「断流重连式重同步」（`:1177-1185`）。**W6（时间戳节拍）仍未做**：`ts += 66666` 硬编码 15fps 仍在（`:1210`）。

4. 系统性排查新发现 **12 条缺陷 + 5 类用户没提到的隐患**（§3/§4），其中最高价值的是：①2D canvas `drawImage(VideoFrame)` 强制 GPU→CPU→GPU 回读（`:1101`）；②`fetch` 无 `AbortController` 超时，卡在首字节时无法取消（僵尸连接）；③大量 `catch {}` 静默吞错导致「卡但无日志」难以排障。

---

## 1. 完整链路图：从收包到呈现（标注每一处可能引入缓冲/延迟的位置）

```
[服务端]                                        [浏览器 docs/web-index.html]
DDA/WGC 抓屏 ──(CPU 读回 captureMs≈42ms)──> NVENC 编码 ──> 每 viewer 有界队列
   ▲ 这是 444ms 主因所在（GPU 零拷贝未生效，见 HANDOFF-20261124）      │
                                                          │ HTTP chunked 逐 NAL Write+Flush
                                                          ▼
                                              fetch('/stream', {cache:'no-store'})   :1114
                                              resp.body.getReader()                    :1117
                                                          │
                                            ┌─ while(myGen===h264Gen) { await reader.read() }  :1216
                                            │   pending = new Uint8Array(pending+value) 拷贝合并  :1221-1224  ← 反复全量拷贝
                                            │   splitAnnexB(pending) 逐字节扫描起始码        :1024     ← 每字节循环
                                            │   ├─ 首个 SPS → avcCodecFromSps 拼 codec → configure({optimizeForLatency:true}) :1248-1253
                                            │   ├─ AU 攒批（SPS/PPS/SEI/VCL 归组）          :1168-1214
                                            │   └─ flushAu():
                                            │        if (decodeQueueSize > SKIP_QUEUE=2 && !key) 丢 delta  :1172  ← 唯一背压
                                            │        decoder.decode(EncodedVideoChunk{ts+=66666})          :1203-1210  ← 假 15fps 时间戳
                                            └─ VideoDecoder.output 回调:                    :1093
                                                  canvas.width/height = displayW/H          :1096-1100  ← 分辨率变化时清空 canvas
                                                  ctx.drawImage(frame, 0, 0)                :1101       ← 2D canvas 直绘（GPU 回读风险）
                                                  frame.close()                              :1109
                                                          │
                                                 合成器上屏（0~1 vsync）      ← 无 rVFC/rAF 对齐
```

**可能引入缓冲/延迟的代码位置汇总**（详见 §2/§3）：

| 环节 | 位置 | 是否引入延迟/缓冲 | 量级 |
|---|---|---|---|
| `pending` 全量拷贝合并 | `:1221-1224` | CPU 抖动（大分片反复拷贝） | 亚 ms~数 ms |
| `splitAnnexB` 逐字节扫描 | `:1024` | CPU 随码率线性 | 亚 ms |
| AU 攒批（等下一个 VCL 才 flush） | `:1168-1214` | 攒批本身不延迟（帧边界即 flush），但网络块边界≠帧边界 | 见 §3 |
| `decodeQueueSize` 背压丢帧 | `:1172` | 是**减延迟**手段（丢旧保新） | 封顶 ~2 帧 |
| `ts += 66666` 假 15fps 时间戳 | `:1210` | 无 B 帧时不产生真实延迟，但节拍错位【待验证】 | 见 §3-② |
| `drawImage` 2D canvas 直绘 | `:1101` | GPU 回读 + 阻塞 output 回调 | 1~5ms/帧 |
| 无 rVFC/rAF 呈现对齐 | 全文件无 | 解码完成时刻与 vsync 无关，最多 1 vsync | 0~16.7ms |

---

## 2. 用户点名的延迟来源逐项审计

> 任务简报列了 8 个「可能被忽略的延迟来源」，逐一给结论（很多项在本实现里**根本不存在**，这正是「不闭门造车」的核对结果）。

### 2.1 缓冲目标 / jitter buffer —— **不存在，这是对的**
无 `SourceBuffer`、无 `<video>` 直播、无自定义 jitter buffer。直播靠 WebCodecs「即解即出」+ `optimizeForLatency:true`（`:1250`）。**结论：没有缓冲目标可调，也没有网络抖动的平滑缓冲——低延迟场景这是正确取舍**，代价是网络抖动时直接掉帧而非平滑（可接受，回环/局域网场景）。

### 2.2 backpressure —— **只有 `decodeQueueSize` 一处，且是丢帧式背压**
浏览器不控制服务端推流速率（`reader.read()` 能读多快读多快）。唯一背压是 `flushAu` 里 `decodeQueueSize > 2` 就丢弃 delta AU（`:1172`）。**结论：背压方向是「解码跟不上就丢旧帧保新帧」，语义正确**；但它只作用于「已收到的数据」，网络层无流控——服务端 `WriteStreamAsync` 逐 NAL `WriteAsync+Flush`（`AgentWebServer.cs:448-457`）会因浏览器读得慢而阻塞，此时服务端 per-viewer 队列增长（服务端侧问题，归 stream-core 成员）。

### 2.3 playbackRate catch-up —— **不适用**
`playbackRate` 是 `<video>` 的属性；本实现直播不走 `<video>`（`<video id="vid">` 仅录像回放，`web-index.html:2285-2293`），故无「倍速追赶」机制，也没有它的隐患。**结论：不适用。**

### 2.4 requestAnimationFrame / requestVideoFrameCallback —— **未使用**
绘制直接发生在 `VideoDecoder.output` 回调（`:1101`），与 vsync 完全解耦。**这是低延迟的正确选择（不等下一帧 vsync 就画），但也意味着**：①可能在一个 vsync 周期内画多帧（浪费、只最后一帧可见）；②没有 vsync 对齐可能轻微撕裂；③**没有任何呈现侧的帧龄测量点**（`requestVideoFrameCallback` 是量「解码→上屏」延迟的唯一标准手段，缺失导致 §5 的浏览器段永远「未实测」）。

### 2.5 Canvas vs `<video>` —— **Canvas（WebCodecs 直绘）**
直播走 `<canvas id="vframe">` + 2D context（`:1076-1077`）。`<video>` 仅录像回放。**Canvas 直绘是低延迟正解**（`<video>`+MSE 有内部缓冲/同步，延迟更高），但 2D canvas 的 `drawImage(VideoFrame)` 是**渲染路径的高成本项**（§3-①）。

### 2.6 hidden tab throttling —— **已用 visibilitychange 断流缓解，但有两个残留问题**
页面隐藏 → `abortStream()` 断流、回前台 → 重连（`:2187-2201`）。这规避了浏览器对后台标签页的 `setInterval/setTimeout` 节流（本会让看门狗/重连计时器失真）。**残留问题**：①切后台再切回来，要重新 `fetch` → 等服务端按需 IDR → 等首帧，有「回来先卡一下」的体感；②`ensureHeldHeartbeat()`（`:1387-1393`）的 2s 定时器在页面加载即启动且**永不清理**，属常驻定时器（虽空转开销小）。

### 2.7 服务端逐 NAL Flush 与「块边界≠帧边界」
服务端 `WriteStreamAsync` 每 NAL 一次 `WriteAsync+Flush`（`AgentWebServer.cs:448-457`），浏览器端注释自认「network chunk 边界 ≠ AU 边界」（`:1282`）。HTTP.sys/浏览器会合并小写出，导致最后一帧的字节在缓冲里等下一批——这是 fetch 形态固有的「拼包税」（前序审计 §2-⑥ 已定性，回环下约几~几十 ms 级）。**结论：不是 444ms 主因，但换 WebSocket 二进制分帧可消除（属远期项）。**

### 2.8 `optimizeForLatency` 与 `hardwareAcceleration`
`optimizeForLatency:true` 已设（`:1250`），这是规范里唯一「低延迟优先」的 hint，**做对了**。但**未显式设 `hardwareAcceleration: 'prefer-hardware'`**（`:1248-1251`），依赖浏览器默认；某些环境（无 GPU 加速、远程桌面内）可能软解 H.264，增加 CPU 与解码延迟。

---

## 3. 浏览器侧缺陷清单（12 条，按严重度排序）

> 严重度定义：🔴 blocker（直接导致不可用/确定性故障）、🟠 高（明显劣化）、🟡 中（量级可感）、🟢 低（隐患/待验证）。

| # | 严重度 | 缺陷 | 代码位置 | 修复建议 |
|---|---|---|---|---|
| ① | 🟡 | **2D canvas `drawImage(VideoFrame)` 强制 GPU→CPU→GPU 回读**。WebCodecs 硬解出的是 GPU 纹理，画进 2D canvas 会触发读回（Chrome 已知行为），1080p 下每帧多一次 CPU 拷贝 + 可能阻塞 output 回调，从而加剧 `decodeQueueSize` 积压、触发丢帧 | `:1101`（`ctx.drawImage(frame,0,0)`） | 换 WebGL2：`gl.texImage2D(..., videoFrame)` 直接吃 VideoFrame，或 `createImageBitmap`+`transferFromImageBitmap`；最简是把 canvas 上下文换成 `webgl2` 并写一个全屏四边形 |
| ② | 🟡 | **`ts += 66666` 硬编码 15fps 时间戳，与服务端实际 fps（默认 30、上限 120，`WebServer.cs:900`）脱节**。注释「与服务端一致」已过时。无 B 帧+立即绘制时不直接产生延迟，但：①将来若开 B 帧/重排会乱序；②时间戳节拍错位可能触发 Chrome 内部平滑【待验证】 | `:1210` | 用 `/api/sessions` 已下发的 `streamFps`（`applySettings` 已存到 `streamFps` 变量），`ts += 1000000/streamFps`；或干脆用单调递增计数器 |
| ③ | 🟡 | **`decoder.decode()` 返回值不 await、不 catch**，异步 rejection 成为未处理 Promise（浮动 Promise），错误静默 | `:1203-1209`（`try{decoder.decode(...)}catch(e){}` 只捕同步抛） | `decoder.decode(chunk).catch(e=>{计数+日志})`；依赖 `error` 回调兜底但要有日志 |
| ④ | 🟡 | **`await fetch(url)` 无 `AbortController` + 无首字节超时**。若服务端 SYN 后不响应首字节（半开连接/编码器卡死），`fetch` 永久 pending，`abortStream()` 里 `reader.cancel()` 只能取消「已拿到 body 的 reader」，卡在 fetch 阶段则无法取消 → 僵尸连接，`h264Active` 卡 true，回前台重连逻辑失效 | `:1114-1118`（fetch/getReader），`abortStream` `:890` | 加 `AbortController`：`startStream` 里 `const ac=new AbortController(); fetch(url,{signal:ac.signal, cache:'no-store'})`，`abortStream` 里 `ac.abort()`；另加首字节 5~10s 超时 |
| ⑤ | 🟢 | **无 `hardwareAcceleration: 'prefer-hardware'`**，依赖浏览器默认，弱设备/远程桌面内可能软解 | `:1248-1251` | `configure({codec, optimizeForLatency:true, hardwareAcceleration:'prefer-hardware'})`，`configure` 失败再降级不带该 hint |
| ⑥ | 🟢 | **`output` 回调里做过多同步 DOM 操作**（尺寸判断+重置 canvas+drawImage+classList+setPh+Date.now），drawImage 慢时阻塞解码输出队列 | `:1096-1108` | 最小化 output 回调：只存最新 frame + drawImage；`setPh(null)`、`failStreak` 归零等低频动作移出热路径 |
| ⑦ | 🟢 | **`pending` 每次 `reader.read()` 都 `new Uint8Array(pending.length+value.length)` 全量拷贝合并**，大分片/高码率下反复全量拷贝产生 GC 抖动 | `:1221-1224` | 用 chunk 链表或预分配可增长 buffer，只在需要时合并；或直接积累 `Uint8Array[]` 到有起始码再拼 |
| ⑧ | 🟢 | **`splitAnnexB` 逐字节 JS 扫描起始码**，CPU 开销随字节数线性；4K/高码率下成为隐性热点 | `:1024`（`for i<buf.length` 逐字节） | 用 `Uint8Array.prototype.indexOf` 定位 0x00 0x00 0x01 模式；或改走 AVCC（description + 长度前缀，服务端出 AVCC 可省掉整段切分） |
| ⑨ | 🟢 | **无 `desynchronized` / 无呈现对齐**：canvas 未设低延迟 hint，且无 rVFC 对齐 vsync | `:1077` | `getContext('2d',{alpha:false, desynchronized:true})`（需评估撕裂）；或用 rVFC 把「解码完成→上屏」对齐 |
| ⑩ | 🟢 | **跳帧阈值 `SKIP_QUEUE=2` 过激进**：30fps 下解码队列仅 2 帧就丢 delta，画面内容剧变时可能频繁掉帧（卡顿观感）；`SKIP_RESET_LIMIT=10` 连续跳帧后直接断流重连（画面中断+等 IDR） | `:1162-1185` | 阈值按「瞬时积压 vs 持续落后」区分：短暂积压丢帧即可，连续积压才重连；或把 SKIP_QUEUE 提升到 3~4 并加「跳过帧时间戳回填」 |
| ⑪ | 🟢 | **首帧/重连等 IDR 无浏览器侧超时**：`firstKey` 前 delta 全丢（`:1271-1274`），若服务端迟迟不发 IDR（无限 GOP + 静止去重），画面长时间「连接中…」，仅靠服务端 25s 上限兜底 | `:1271-1274` | 浏览器侧加「首帧 5s 无帧 → 主动重连」；配合 W7（客户端可请求 IDR 的控制通道）根治 |
| ⑫ | 🟢 | **`<video>` 录像回放无缓冲控制/无错误提示**：`playVideo` 直接 `v.src=url; v.load()`，网络/文件缺失时静默黑屏，且与主画面 canvas 并存无互斥 | `:2285-2293` | 加 `error` 监听提示；回放时暂停/断开直播流（当前已由 `hideRemoteUi` 隐藏但未断流） |

---

## 4. 用户没提到的隐患（系统性排查，专项）

### 4.1 标签页后台节流（hidden tab throttling）
- **已做**：`visibilitychange` 隐藏即断流、回前台重连（`:2187-2201`），规避后台 `setInterval` 被节流到 1 次/分钟导致看门狗失效。
- **未覆盖的残留**：①`heldHeartbeat` 的 2s `setInterval`（`:1387-1393`）页面加载即启动、永不清理——虽只在 `hasHeldInput()` 为真时才发 noop，但定时器本体常驻；②后台标签页里 `fetch` body 的读取也可能被浏览器暂停，但既然已断流，此路径不触发；③**切回前台重连链路里没有任何「快速首帧」优化**——每次回前台都要走「fetch → 等按需 IDR → 首帧」，体感「回来卡一下」。

### 4.2 长时间运行内存/句柄增长
- **代际作废机制基本正确**：`h264Gen` 递增作废旧流回调（`:1059/:1094/:1231`），`finally` 里 `clearInterval(stallTimer)` + `decoder.close()`（`:1294-1299`），切账号/重连不泄漏。
- **残留风险点**：①`pending`/`auNals` 的 `Uint8Array` 高频新建（`:1221`），GC 压力随运行时长累积；②`splitAnnexB` 的 1MB（无起始码）/4MB（单 NAL）清空阈值（`:1024` 段内）在 4K 高码率 IDR 下可能误判丢帧；③`decoder.decode()` 未 await，若解码器内部排队过多（不触发 error 回调也不抛同步异常），`decodeQueueSize` 检查只在 `flushAu` 时生效，极端情况下队列可能被 key 帧持续填充。
- **无 WebSocket**，故无 WS 连接泄漏类问题；fetch 流靠 `reader.cancel()`（`abortStream` `:890-895`）回收。

### 4.3 重连风暴
- **退避有界**：指数退避 1s→2s→4s→8s 封顶（`:925-933`），`failStreak` 仅在出帧时清零（`:1107`），单 tab 最多 ~1 次/8s，**不是真风暴**。
- **真隐患**：①手动重连按钮（`:946-953`）点击时 `failStreak=0` 立即 `startStream()`，**无最小间隔**——用户狂点会无冷却并发重连；②「半坏服务端」（能连上但只吐 SPS/PPS 无 VCL）下，浏览器陷入「连上→等服务端 25s→503→fail→backoff→重连」的伪风暴，且 `visibilitychange` 可见分支的 `!reconnectTimer` 判断（`:2196`）与 `h264Active` 存在竞态窗口，极端下可能并发两个 `startStream`；③**多 tab/多 viewer 看同一账号**时，每个 tab 独立重连 + 每次 `TrySubscribe` 都强制 IDR（`SessionRecorder` 侧），叠加放大服务端压力。

### 4.4 错误静默吞掉（「卡但无日志」的排障黑洞）
以下 catch 全部静默，用户报告「卡/没反应」时 console 无任何线索：
| 位置 | 吞掉什么 | 后果 |
|---|---|---|
| `flushAu` `:1209` | `decoder.decode()` 失败 | 丢帧无日志，用户只看到卡 |
| `sendInput` `:1325-1326` | 输入 POST 失败 | 点击没反应，无提示 |
| `loadSaved`（`web-index.html` 末段 `catch(x){}`） | 录像列表加载失败 | 静默空白 |
| `pushSettings`（`web-index.html` 末段 `catch(e){} return false`） | 设置写回失败 | 仅回滚下拉，无用户提示 |
| `refreshCaptureBackend` `catch(e){}` | `/api/info` 失败 | 后端显示「—」 |
- **建议**：至少对「解码丢帧累计数」「输入发送失败数」做 console.warn 计数并暴露到 `#fpsinfo`，否则任何「卡」都无法归因。

### 4.5 其他隐患（顺带扫出）
- **无端到端帧龄测量**：服务端不随帧下发 `capturedAt`，浏览器只用本地 `Date.now()`（`:1104`）做看门狗，`ts` 是合成值——**端到端延迟无法从浏览器侧量化**，`LATENCY-BENCHMARK §建议5` 早已提出，至今未做。
- **`#plCursor` 即用户口中的「蓝色圆球」**：CSS `:321`（18px 圆、`border:var(--accent)` 青色）、DOM `:612`、JS `:1433-1456`。当前行为：`pointermove` 显示、Pointer Lock 锁定/解锁即 `display:none`（`:1592/:1612` 对应 WebPages）——正好命中「点一下就隐藏」的体感。**这是 cursor-ux 成员的改造对象**，此处只定位代码，供其接手。
- **`contextmenu/dblclick/selectstart` 在 `#scr` 上全 `preventDefault`**（`web-index.html` 中段），右键被用于「转视角」，与用户「右键出菜单」的预期冲突——设计取舍，需 cursor-ux 确认。
- **键盘全局注入**：点画面开 `kbMode`、点画面外关（`:2175-2181`），但若用户忘记点画面外，F5/Ctrl+W 会被当按键发远端（已用 `setKbMode(false)` 兜底，仍属易误触）。

---

## 5. 浏览器侧是否 444ms 主因：判定与证明

**结论：不是。** 浏览器侧稳态约 10~30ms、最坏约 50~90ms，无法解释 444ms；主因在服务端 GPU 零拷贝未生效导致的 CPU 读回与采集/编码管线。

### 5.1 结构证明（代码事实，浏览器侧无 444ms 级缓冲）
1. **无大缓冲**：无 MediaSource/SourceBuffer、无 `<video>` 直播缓冲、无 jitter buffer（§2.1）。
2. **解码不攒帧**：`optimizeForLatency:true`（`:1250`）已设。
3. **唯一队列有界且丢旧**：`decodeQueueSize > 2` 即丢 delta AU（`:1172`），最多积压 ~2 帧（30fps 下 ~66ms）就被丢弃追赶。
4. **绘制即时**：`output` 回调立即 `drawImage`（`:1101`），无 rVFC 攒帧、无 playbackRate、无缓冲目标。
5. **因此浏览器侧最大缓冲 ≈ decodeQueue(≤2 帧) + 合成上屏(1 vsync) ≈ 66+16 ≈ 82ms 上限（30fps/60Hz），典型 10~30ms。** 这个数字在任何情况下都到不了 444ms。

### 5.2 主因证据链（指向服务端）
- 最新交接 `HANDOFF-VIDEO-LATENCY-GPU-20261124.md` §2.2 明言：**「GPU 零拷贝没生效：DDA 后端仍走 CPU 读回路径（captureMs=42ms 的元凶）」**，且「GPU 零拷贝后反而回到 444」。
- 前序审计 `WEB-VIEWER-LATENCY-GAP-20261123.md` 已实测/拆解：回环下 453ms ≈ 服务端节拍税 2/3 + 浏览器管线 1/3，且浏览器那段是**旧 SKIP_QUEUE=5 时代的估值**，现已收紧到 2，浏览器段占比只会更小。
- `LATENCY-BENCHMARK-20261123.md` 实测：采集节拍 82ms + 推流节拍 74ms ≈ 157ms 起步（软编时代），叠加编码/转换后量级与 444ms 同区间。

### 5.3 诚实声明
浏览器「解码→上屏」这段我**未埋点实测**（前序审计 §7 也标未实测）。上述是**结构证据（代码事实）**，不是实测数字。要最终证伪/坐实，唯一办法是落地 `requestVideoFrameCallback` + 服务端随帧下发 `capturedAt` 的埋点（§6-P2）。**若真机日志出现「[video] 零拷贝色差 PSNR」说明零拷贝已生效、延迟仍未降，则需重新评估浏览器段；但在那之前，排查重心应放在服务端零拷贝为何没生效。**

---

## 6. 可执行建议（按性价比排序，均为浏览器侧）

| # | 改动 | 位置 | 收益 | 成本 |
|---|---|---|---|---|
| P1 | 加 `AbortController` + fetch 首字节超时，`abortStream` 里 `ac.abort()` | `:1114-1118`/`:890` | 消除僵尸连接（§3-④），回前台重连可靠 | 0.5h |
| P2 | 落地帧龄埋点：`output` 记 `decode` 时刻，`requestVideoFrameCallback` 算 P50/P95 显示到 `#fpsinfo` | `:1093-1108` + 新增 | **让浏览器段延迟可证伪**，是所有后续优化的前提 | 0.5h |
| P3 | `ts` 节拍跟随 `streamFps` | `:1210` | 消除假 15fps 时间戳隐患（§3-②） | 10 分钟 |
| P4 | `configure` 加 `hardwareAcceleration:'prefer-hardware'` + 失败降级 | `:1248-1251` | 确保硬解，弱设备不软解 | 10 分钟 |
| P5 | `drawImage` 换 WebGL2 `texImage2D(VideoFrame)`（或 createImageBitmap 路线） | `:1077/:1101` | 消除 GPU 回读，降 CPU + 减 output 阻塞 | 0.5~1 天 |
| P6 | 给「解码丢帧/输入失败」加 console.warn 计数并暴露 | `:1209`/`:1325` | 终结「卡但无日志」 | 0.5h |
| P7 | 手动重连加最小间隔 + 首帧 5s 无帧主动重连 | `:946-953`/`:1271-1274` | 防重连风暴/防首帧死等 | 0.5h |

> 注意：以上**任何一条都不能把 444ms 降到目标值**——444ms 的根子在服务端零拷贝（P0，见 HANDOFF-20261124）。浏览器侧这些是「把浏览器段压到 10ms 级 + 让延迟可测」的配套项。

---

## 7. 与其他成员的交叉引用

- **builder-repro / auditor-ds（服务端侧）**：444ms 主因 = GPU 零拷贝未生效、DDA 走 CPU 读回（captureMs≈42ms）。请优先查 agent 日志 `[video] 零拷贝…` / `[video] 降级 CPU…`（HANDOFF-20261124 §5 P0 已列排查入口）。浏览器侧结构上不是主因（本文 §5）。
- **cursor-ux（虚拟鼠标/蓝色圆球）**：`#plCursor` 即用户说的「蓝色圆球」，代码位置 CSS `:321` / DOM `:612` / JS `:1433-1456`，「点一下就隐藏」= Pointer Lock 锁定即 `display:none`。请据此改造或移除。
- **moonflow-input / mlow-mouse（输入链路）**：浏览器侧输入已做 FIFO 串行 + move noWait 8ms 节流（`pumpInput :1346`、`sendMoveThrottled :1751`），「鼠标卡/落点误差」的浏览器侧半（norm 落点）已在 91fc288 修过；**agent 侧坐标换算仍用 `GetSystemMetrics` 主屏尺寸**（HANDOFF-20261124 §5 P2），那是剩余落点误差的 agent 侧半，归服务端成员。

---

## 8. 边界遵守自查

未改任何产品代码/脚本/配置；未碰 `dist/stable/SoulLink-Stable.exe`；未重启任何进程；未发布、未碰 COS/latest.json/版本号；未使用/控制 A1/A4；未新建团队、未调用 dsh_team_create；唯一写入为本研究文档。所有前端行号指向 `docs/web-index.html`（服务端指向对应 `.cs`），已逐一 grep 核对。
