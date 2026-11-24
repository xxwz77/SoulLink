# 浏览器段自检与埋点：收包 → 切帧 → 解码 → 上屏到底占多少 ms（AUDIT-20261124-09）

> 日期：2026-11-24
> 作者：webclient-instrumentation（dsh-team `team_mwu35l8pae8oj5` / member `m_mwu3791qage7i6` / task `task_mwu37oaiam7rye`）
> 性质：**只读审计**。未改任何产品代码/脚本/配置/版本号；未碰 `dist\stable\SoulLink-Stable.exe`（PID 13392）；未使用 A1/A4 会话；未停/启任何现有进程；未弹窗；未改注册表；未发布、未碰 COS/latest.json；未做任何 git 写操作。唯一写入 = 本文档 + `docs/research/README.md` 一行登记。
> 证据分级：**【代码事实】**＝当前工作区 `docs/web-index.html` 逐行可证（file:line）；**【本次实测】**＝本会话在**本机**用 headless Edge 159（D3D11/ANGLE，RTX 4060 Laptop）+ CDP 驱动的真实测数（方法见 §9，可复现）；**【已有实测】**＝本仓库前序文档；**【推断】**＝有依据未实测；**【待实测】**＝无证据，不猜。
> 行号基线：`docs/web-index.html` 共 2963 行，与 `Services/WebPages.cs` 内嵌常量**逐行完全一致**（本会话做过去空白归一化后逐行比对 = equal，2963/2963 行），且与 `dist/web-index.html`、`dist/stable/web-index.html` **SHA-256 相同**（`b0a0339460900545…`，151238 字节）——即线上页面 == 本文引用源。

---

## 0. 直接回答用户的问题（先给结论，再给证据）

用户问：「**是浏览器问题需要自检一个 web 应用？还是代码有问题？**」

**答案：三段论。**

1. **不是「浏览器有问题」，也不是「代码有 bug」——是代码已经把浏览器段做到了下限，剩下的缺口不在这段。**
   本会话第一次把浏览器段**实测**出来（此前所有文档都是「结构证据 + 未实测」）：
   - 收包（HttpListener 真实 chunked + 每帧 Flush → 浏览器 `reader.read()` 返回）：**p50 ≈ 0ms，p95 0.4ms，max 1.9ms**
   - 切帧（`splitAnnexB` + AU 重打包 + 缓冲合并）：**< 0.3ms/帧**
   - 解码（硬解 1440p，`decode()` 提交 → `output` 回调）：**p50 0.5~1.0ms，p95 ≤1.6ms**，稳态 `decodeQueueSize` p50 = **0**
   - 绘制（WebGL2 `texImage2D(VideoFrame)`）：**p50 0.1~0.2ms**
   - 合成上屏：代码不做 rAF/rVFC 排队（全文件 0 命中），上屏时刻完全交给 Chrome 合成器，本地不可测（见 §6）
   - **浏览器段合计：典型 1~5ms，最坏 1 个 vsync（≤16.7ms），极端 ≤25ms。**
   这个量级**解释不了 233ms 差分**，更解释不了「服务端记账 ~90ms vs 真机 233ms」之间 **~140ms 的账外缺口**。
   **独立交叉印证**：同队 `TOOL-20261124-DIRECT-PROBE.md` 用一个**完全不含浏览器**的原生客户端（HTTP 收包 → MF 软解 → GDI 上屏）测出「解码 0.91ms + 上屏 7.79ms ≈ 9ms」，且**帧到达间隔 gap p50 = 55ms（≈16~18fps）**——两条完全独立的证据同时把浏览器段压到 ~10ms 以内、并把矛盾指向**服务端的帧供给节奏**。

2. **但浏览器段有一个「在别人机器上会被放大」的数字，而且它已经不是默认路径了**：
   **2D canvas `drawImage(VideoFrame)` 路径 = 22~25ms/帧（1440p，强制同步实测）**，而 WebGL2 路径 = **0.1ms**。
   现状代码（`:1132-1215` 建 WebGL2 渲染器，`:1247` 优先用它，`:1321` 才回退 2D）**默认走 WebGL2**，所以这个 22ms 默认不发生。
   它只在 `webgl2` 上下文拿不到 / 丢失时发生。**用户的机器上是否落到了 2D 回退，就是「浏览器问题」这一支唯一还活着的可能。** §5 给了一条 3 行代码的自证方法。

3. **分屏/窗口化/失焦导致节流——用户已澄清、本次也用代码与实测双重排除**：
   本页上屏**不经过 requestAnimationFrame、也不经过 requestVideoFrameCallback**（两者全文件 0 命中，`performance.now` 也 0 命中）。
   上屏发生在 `VideoDecoder.output` 回调里直绘（`:1311-1330`）。Chrome 对「不可见标签页」的 rAF/合成降频**在本实现里被短路**：页面 `hidden` 就 `abortStream()` 断流、回前台重连（`:2615-2629`）。
   所以「窗口不可见 → 帧率崩」这条路径**要么不触发（可见但不聚焦），要么直接断流（不可见）**，不会变成「慢 140ms」。
   ⇒ **该假设已由用户澄清 + 代码事实双重排除，不再作为账外 140ms 的主因。**

**结论一句话：浏览器段不是 140ms 的主因（实测 1~8ms，且比同队原生 GDI 探针的 9ms 还快）；要么用户的机器落到了 2D 回退（22ms/帧，§5 一条命令自证），要么缺口在服务端帧供给节奏（独立实测 55ms/帧 ≈16~18fps）与测量法本身。**

---

## 1. 播放链路逐段核实行号（2026-11-24 当前源码，非照抄旧文档）

> ★ 旧文档（`AUDIT-20261124-05-WEB-CLIENT-GAP.md`）的行号**已整体失效**（源码在此期间重写：WebGL2 呈现层、framed v2 协议、按需 IDR、AbortController 全部新增，文件从 ~2600 行长到 2963 行）。下表是**本次逐行 grep + read 核对后的现行行号**。

```
[服务端] Relay/SessionRecorder.cs 唯一视频线程 → viewer.Pending（深度 2，丢旧保新）
   → AgentWebServer.cs:481-486 每帧 WriteFramedAsync + FlushAsync（v=2 时 4B 大端长度前缀）
                                    │
[浏览器 docs/web-index.html]  ─────┴──────────────────────────────────────────────
  resolveStreamVersion()                  :1004-1018   （/api/info 取 streamVersion，>=2 走 framed）
  requestIdr()                            :1022-1035   （POST /api/idr，客户端 500ms 限流）
  fetch(url, {cache:'no-store', signal})  :1358        （AbortController 取消僵尸连接）
    ↑ 首字节超时 FIRST_BYTE_MS=8000        :1349-1361
  resp.body.getReader()                   :1364
  ── framed 模式（v2）主循环 ──
  while(myGen===h264Gen){ await reader.read() }  :1487-1490
  frameBuf 合并（new Uint8Array + set）    :1491-1494   ← 每 chunk 一次全量拷贝
  4B 长度前缀切帧                          :1496-1505
  splitAnnexB(one, true) 切 NAL            :1507        → :1092-1130（逐字节状态扫描）
  逐 NAL 分类（SPS/PPS/IDR/VCL）           :1510-1522
  flushAu() 立即解码                       :1523        → :1419-1480
  ── flushAu 内部 ──
  decoder.decodeQueueSize > SKIP_QUEUE 丢 delta  :1423
  AU → annexb 重打包（new Uint8Array+set） :1445-1453
  decoder.decode(new EncodedVideoChunk)    :1455-1467  （有 .catch + 同步 try/catch + 计数）
  ts += round(1e6/streamFps)               :1476        ← 已跟随 streamFps，不再硬编码 66666
  ── 解码器配置 ──
  tryConfigure(): optimizeForLatency:true  :1268
      + hardwareAcceleration:'prefer-hardware'  :1270   （失败降级不带 hint :1274）
  IDR 恢复 reconfigure 同 hint             :1300-1303
  ── 呈现 ──
  getGlRenderer(canvas)                    :1139-1215
      webgl2 上下文 attrs（含 desynchronized:true，:1146）
      gl.texImage2D(..., frame)            :1204        ← VideoFrame 直接作纹理源
  const glR = getGlRenderer(canvas)        :1247
  const ctx = glR ? null : getContext('2d')  :1248      ← 仅回退路径
  output 回调                              :1311-1330
      canvas.width/height = frame.displayW/H   :1315-1319
      glR.draw(frame, w, h)  /  ctx.drawImage   :1320 / :1321
      frame.close()（finally）             :1329
```

**用户简报里点名要核实的四个点，逐条给状态：**

| 点名项 | 当前实现 | 判定 |
|---|---|---|
| `SKIP_QUEUE` | `Math.max(4, Math.round(streamFps/10))`（30→4 / 60→6 / 120→12），`:1412`；`SKIP_RESET_LIMIT = Math.max(30, streamFps)`，`:1413`；判定 `:1423` | **已是跟随帧率的动态阈值**，不再是最初的常数 2 |
| `hardwareAcceleration` | `:1270` `'prefer-hardware'`，失败降级不带 hint（`:1274`）；IDR 恢复路径 `:1300` 同样优先硬解 | **已设**（旧文档说「未设」已过时） |
| `optimizeForLatency` | `:1268` 与 `:1300` 两处均 `true` | **已设** |
| `ts += 66666` | **已不存在**。现为 `:1476` `ts += Math.round(1000000/(streamFps>0?streamFps:30))` | **已修**（全文件 grep `66666` 0 命中） |

---

## 2. `AUDIT-20261124-05` 的 12 条缺陷：现在还剩哪些 open（逐条用当前行号核实）

| # | 缺陷 | 现状 | 当前证据 | 本次实测的量化结论 |
|---|---|---|---|---|
| ① | 2D `drawImage(VideoFrame)` GPU 回读 | **已修（默认不走）** | WebGL2 呈现层 `:1132-1215`、`:1247`、`:1204`；2D 仅 `:1321` 回退 | 2D 路径 **22~25ms/帧**（1440p 强制同步 p50 21.9~24.5 / p95 26.6~38.5）；WebGL2 **0.1~0.2ms**。见 §5 |
| ② | `ts += 66666` 假 15fps | **已修** | `:1476` 跟随 `streamFps` | 另测：60fps 投喂下 `ts` 用 real-16.667ms / 假-66.666ms / 纯序号，`decode→output` 三项 p50 全为 **0.5~0.6ms**、输出间隔全 **16.7ms** → **假时间戳不产生延迟**（推翻旧文档 §3-② 的「待验证」为「无影响」） |
| ③ | `decode()` 不 catch | **已修** | `:1455-1475`（`.catch` + 同步 try/catch + `decodeErrors` 计数 + 限频日志） | — |
| ④ | `fetch` 无 AbortController | **已修** | `:1343-1362`（`FIRST_BYTE_MS=8000`，`abortStream` 里 `ac.abort()` `:919-926`） | — |
| ⑤ | 无 `hardwareAcceleration` | **已修** | `:1270`/`:1300` | 本机 1440p 硬解吞吐 **2.6~3.0ms/帧（329~384fps）** |
| ⑥ | `output` 回调 DOM 操作过多 | **仍在，但风险≈0** | `:1315-1328`（尺寸判断 + classList + setPh + Date.now） | 实测这些操作 **0.4µs/帧**（2000 次 `classList.add`+`style.display` 摊还）→ **可降为噪声级，不必改** |
| ⑦ | `pending` 全量拷贝合并 | **framed 路径仍在** | `:1491-1494`（每 chunk 一次 `new Uint8Array` + 两次 `set`） | 33KB 一帧的合并+切分 p50 **0.1ms**（legacy 路径）→ 可忽略，优化收益 <0.2ms |
| ⑧ | `splitAnnexB` 逐字节扫描 | **仍在（by design）** | `:1092-1130`；framed 下每帧一次 `:1507` | 33KB p50 **0.0ms** / p95 0.1ms；200KB(4K IDR) p50 **0.3ms** → 可忽略 |
| ⑨ | 无 `desynchronized` | **已修（主路径）** | WebGL2 attrs `:1146` `desynchronized:true`；2D 回退 `:1248` 未带 | 主路径 OK |
| ⑩ | `SKIP_QUEUE=2` 过激 | **已修** | `:1412-1413` 跟随 fps | 实测稳态 `decodeQueueSize` p50=0（解码 330fps ≫ 到达 30~37fps），**阈值根本没被触发** |
| ⑪ | 首帧/重连无超时 | **已修（部分）** | 首字节 8s `:1349`；IDR 恢复 `:1285-1308`（≤2 次后断流） | 残留：**「已连上但编码器不出帧」** 仍只能靠服务端 25s（`AgentWebServer.cs:448`）+ 前端 10s 看门狗（`:1371`/`:1378-1385`）兜底 |
| ⑫ | 录像回放无 error 提示 | **仍 open** | `:2713-2721` `playVideo` 无 `v.onerror`；`:2722-2725` 关闭也无异常处理 | 与实时画面无关，优先级最低 |

**净结论：12 条里 9 条已修、1 条降级为噪声（⑥）、2 条仍 open（⑪ 的部分、⑫）。旧的 12 条缺陷清单不再是 233ms 的任何一支。**

---

## 3. `WEB-VIEWER-LATENCY-GAP` / `WEB-RUNTIME-PROBE` 的现状核对

| 旧结论 | 出处 | 现在是否成立 | 依据 |
|---|---|---|---|
| W1 `stallTimer` 作用域 ReferenceError | `WEB-VIEWER-LATENCY-GAP §1.1` | **已修** | `stallTimer` 现声明在 `startStream` 函数体顶层 `:1235`（try 之外），`finally` `:1586` 安全 clear |
| W2 reset 改重连式重同步 | 同上 | **已改为按需 IDR + 重连兜底** | `:1285-1308` `recoverViaIdr()`；连续 2 次失败才 `fail()`；服务端 `/api/idr` 已上线（`AgentWebServer.cs:321-325, 534-549`） |
| W3 `SKIP_QUEUE 5→2` | 同上 | **被再次修正为「跟随 fps」** | `:1412-1413`（注释自认原 2/10 过激） |
| W5 rVFC 埋点 | 同上 | **仍未做**（且 canvas 不支持 rVFC，见 §6） | 全文件 `requestVideoFrameCallback` 0 命中；本会话实测 `'requestVideoFrameCallback' in HTMLCanvasElement.prototype === false` |
| W6 `ts` 跟随 fps | 同上 | **已做** | `:1476` |
| W7 客户端→服务端控制通道 | 同上 | **已做（HTTP 版）** | `/api/idr`：`web-index.html:1022-1035` ↔ `AgentWebServer.cs:321-325`；限流 500ms 客户端 + 300ms 服务端 |
| 「fetch 形态固有拼包税，几~几十 ms」 | `§2-⑥/§3-1` | **回环下实测≈0** | §4：HttpListener 逐帧 Flush → `reader.read()` 返回 p50 0ms / p95 0.4ms / max 1.9ms |
| 「浏览器侧 10~30ms，最坏 50~90ms」 | `AUDIT-05 §5` | **偏保守但方向对**；实测更小 | 本次：典型 1~5ms，最坏 ≤25ms（§4/§6） |
| 云原神「rVFC 分析器」对照 | `WEB-RUNTIME-PROBE §1` | 不适用于我们 | 云原神是 `video.srcObject=MediaStream`（`WEB-RUNTIME-PROBE` 实测），有 rVFC；我们是 canvas + WebCodecs，**canvas 无 rVFC** |

---

## 4. 浏览器段逐段 ms 分解（【本次实测】，本机 headless Edge 159 + CDP，方法见 §9）

> 测法口径：本机 127.0.0.1 回环；服务端用 **真实 `System.Net.HttpListener`**（与 `AgentWebServer` 同一套 http.sys 栈）+ `SendChunked=true` + 每帧 `Write+Flush`，帧体 33KB、27~33ms 一帧、帧内嵌**服务端墙钟毫秒**；浏览器侧用一个和 `web-index.html` 同构的 `fetch+getReader+4B 长度切帧` 循环消费。码流另一路用本机 `VideoEncoder` 生成**真 H.264 Annex-B**（1440p/1080p/720p 多档）喂真 `VideoDecoder`。

| 段 | 测点（代码位置） | 实测 p50 | 实测 p95 | 最坏 | 等级 |
|---|---|---|---|---|---|
| ① 服务端 write 完成 → 浏览器 `reader.read()` 返回 | `AgentWebServer.cs:481-486`（Flush） ↔ `web-index.html:1488` | **≈0ms**（-0.07~+1.0） | **0.36~1.0ms** | 1.9~5ms | 【本次实测】 |
| ①a 每次 read 覆盖多少帧 | `reads/frames` | **1.37~1.51 帧/read** | — | — | 【本次实测】 |
| ② 切帧 + 缓冲合并 + AU 重打包 | `:1491-1494` / `:1507` / `:1445-1453` | **<0.1ms**（33KB） | 0.1~0.2ms | 0.8ms | 【本次实测】 |
| ②b 4K 大 IDR（200KB）切分 | `:1092-1130` | 0.3ms | 0.5ms | 2.2ms | 【本次实测】 |
| ③ 解码器排队 + 解码执行（`decode()` → `output`） | `:1455`→`:1312` | **0.5~1.0ms** | **≤1.6ms** | 13~50ms（首帧/IDR） | 【本次实测】 |
| ③a 1440p 解码吞吐（硬解） | — | **2.6~3.0ms/帧 ≈ 329~384fps** | — | — | 【本次实测】 |
| ③b 稳态 `decodeQueueSize` | `:1423` 读它做丢帧判断 | **0** | 0~5 | 14（突发） | 【本次实测】 |
| ④ 绘制：WebGL2 `texImage2D(VideoFrame)` 调用 | `:1204` | **0.1~0.2ms** | 0.2~0.4ms | 0.6ms | 【本次实测】 |
| ④' 绘制：2D `drawImage(VideoFrame)`（回退路径） | `:1321` | **21.9~24.5ms** | **26.6~38.5ms** | — | 【本次实测】 |
| ⑤ 合成上屏 | 无代码（交给 Chrome） | **本机不可测** | — | — | 【待实测】 |
| ⑤a 合成节拍参考（headless） | — | rAF 4.2ms（238Hz，headless 不限帧） | 4.3ms | 639ms（一次性长任务） | 【本次实测】 |
| **浏览器段合计（WebGL2 主路径）** | — | **≈1~5ms** | **≤8ms** | **1 vsync + 偶发首帧 ≈ 25ms** | 【推断】（④+③+②+①，⑤待实测补 0~16.7ms） |

**四个必须说清的判别性结论：**

1. **收包完全没有攒批。** 服务端每帧 `Flush` 之后，浏览器 `read()` **立刻返回**（p50 0ms、p95 0.4ms、max 1.9ms）。旧文档担心的「HTTP.sys/浏览器合并小写出 → 最后一帧在缓冲里等下一批」在**回环 + 逐帧 Flush 的现状下不成立**（实测 `reads` 甚至多于 `frames`，即前缀与帧体偶尔分两次到，而不是多帧被凑成一次）。
2. **解码链路不是瓶颈。** 硬解 1440p 每秒能解 330~384 帧，实际到达 30~37fps，`decodeQueueSize` 稳态为 **0**——`:1412` 的 SKIP_QUEUE（30fps 下 =4）**根本没被触发过**。旧文档 §2-⑧「解码队列 0~5 帧排队」的说法在本机不成立。
3. **假时间戳无代价。** 我用同一批真码流做 A/B：`ts` 用正确 16.667ms / 用假 66.666ms / 用纯序号，三组 `decode→output` p50 分别为 0.5/0.6/0.5ms，输出间隔全 16.7ms。**`ts` 的语义不影响实现帧率**（虽然 `:1476` 已经修对了）。
4. **2D 回退是唯一有量级的浏览器段风险项**，但**不是无条件爆炸**：
   - 30~37fps 到达（帧间隔 27~33ms） vs 2D 路径 22ms/帧处理 → **仍然不积压**（我实测把 output 人为拖慢 22ms，队列 p50 仍为 0）；
   - 60fps 到达（帧间隔 16.7ms） vs 22ms → **开始积压**，稳态顶到 SKIP_QUEUE（60fps→6 帧 ≈ 100ms）并持续丢帧。
   ⇒ 若用户机器落到 2D 且 `streamFps=60`，浏览器段会从 ~5ms 变成 **~100ms+**，这就足以吃掉账外缺口的一大块。**这是必须让用户在实机上确认的一件事。**

---

## 5. 高风险点判定：`drawImage(VideoFrame)` 是否真的 GPU→CPU→GPU 回读？改 WebGL2 省了多少？

### 5.1 现状：WebGL2 已经是默认路径（【代码事实】）
- `:1139-1215` `getGlRenderer`：建 `webgl2`（`alpha:false, antialias:false, depth:false, stencil:false, premultipliedAlpha:false, desynchronized:true, powerPreference:'high-performance'`，`:1144-1147`），全屏三角形 + `texImage2D(gl.TEXTURE_2D,0,gl.RGBA,gl.RGBA,gl.UNSIGNED_BYTE,frame)`（`:1204`）。
- `:1247-1248`：`glR` 拿到就用它，否则才 `getContext('2d')`。
- `:1210-1213`：初始化异常 → `console.warn('[H264] WebGL2 呈现初始化失败，回退 2D drawImage:', e)` 并置 `null`。**这条 warn 是用户在实机上判断「我是不是落到 2D」的唯一现成开关。**

### 5.2 「回读」这个机制，本次实验只证明了一半（诚实标注）
| 实验 | p50 | 说明 |
|---|---|---|
| 纯 `getImageData(0,0,1,1)`（不画） | **0ms** | 强制同步本身不贵 |
| `drawImage(VideoFrame)` + 强制 `getImageData` | **21.9~24.5ms** | 2D 路径「画+强制看见」的真实代价 |
| `drawImage(Canvas 源)` + 强制 `getImageData` | **25.1ms** | **和 VideoFrame 源同量级！** |
| `drawImage(VideoFrame)` 不强制（60 次摊还） | **0.87ms/次** | 延迟提交时很便宜 |
| WebGL2 `texImage2D(VideoFrame)` + `gl.finish()` | **0.1ms** | 同一张 1440p 帧 |

**解读（【本次实测】+【推断】）**：
- 「Canvas 源也要 25ms」说明这 22~25ms **不是 VideoFrame 独有的 GPU→CPU→GPU 回读**，而是 **2D canvas 在「必须真正光栅化并让结果可见」时的整条软件/拷贝路径成本**。
- 所以旧文档「2D drawImage 强制 GPU→CPU→GPU 回读」这个**具体机制在本实验里未被直接证明**；被证明的是「**2D 路径每帧同步成本 ~22ms，WebGL2 路径 ~0.1ms**」。结论（该换 WebGL2）不变，但**理由要改为「2D 光栅+同步的成本量级」，而不是断言回读机制**。
- 顺带纠正一处：`desynchronized:true` 只出现在 **WebGL2** 的 attrs（`:1146`），2D 回退的 `getContext('2d',{alpha:false})`（`:1248`）**没有** `desynchronized`——回退路径等于把低延迟 hint 也一起丢了。

### 5.3 收益量化（【本次实测】）
- 主路径已是 WebGL2 ⇒ **本次改动收益 = 0**（已经拿到了）。
- 若用户机器落到 2D：改回 WebGL2 的收益 = **每帧省 ~22ms 同步成本**；在 60fps 下这等于 **从「稳态积压 100ms 并持续丢帧」回到「0 积压」**；在 30~37fps 下等于 **省 ~22ms 的每帧延迟尾（p95 从 38ms 降到 0.4ms）**。
- 若 WebGL2 因为某些机器（远程桌面内无 GPU / 驱动黑名单）真不可用：**不要停在 2D**，退一步用 `createImageBitmap(frame)` + `ctx.drawImage(bitmap)`，或直接把 canvas backing store 降到显示尺寸（**本次实测：backing 900×506 时 2D 强制同步 p50 从 21.9ms 降到 3.7ms**，因为要搬的像素从 14.1MB 降到 1.7MB）。**这是不依赖 WebGL 的兜底，成本极低。**

### 5.4 canvas 尺寸 / 显示尺寸 / DPR（回应「分屏下缩小窗口」的新变量）
- **【代码事实】** canvas 的 backing store 只有一处赋值：`:1315-1319`，取 `frame.displayWidth/displayHeight`（即视频原始分辨率，1440p 时 2560×1440）。**不跟随窗口、不看 `devicePixelRatio`**（全文件 `devicePixelRatio` 0 命中，`resize` 0 命中）。
- **【代码事实】** 显示尺寸由 CSS 决定：`#scr canvas{width:100%;height:auto;max-height:70vh;object-fit:contain}`（`:314-315`），viewonly 模式下 `:285-287` 用 `max-height:calc(100vh - 46px)`。**缩放是合成器（GPU）做的 bilinear，不是 JS/CPU 做的。**
- **【本次实测】** backing 2560×1440（CSS 900×506）与 backing 900×506，**WebGL2 上传成本都是 0.1ms**；2D 路径前者 21.9ms、后者 3.7ms。
- **【本次实测-环境】用户这台机器的 DPI 缩放 = 150%**：`HKCU\Control Panel\Desktop\WindowMetrics\AppliedDPI = 144`（96=100%，144=150%）⇒ 浏览器里 `devicePixelRatio = 1.5`；同机另一个 Edge 实例的命令行也带着 `--device-scale-factor=1.5`。这意味着 **CSS 像素 → 物理像素还要 ×1.5**，画布从 2560×1440 backing 缩到「CSS 尺寸 × 1.5」的物理像素再去显示——**仍然是合成器 GPU bilinear，不进 JS**。
- ⇒ **分屏/窗口化/DPI 缩放本身不会因为「canvas 与显示尺寸不匹配」产生额外延迟**（主路径 GPU 缩放免费；2D 回退才按 backing 面积付费，而 2D 回退实测的 22ms 在 backing=2560×1440 时是常数，与 CSS 尺寸无关）。**这条从嫌疑名单里排除。**

---

## 6. rVFC 埋点方案：能做、怎么做、插在哪几行、输出什么字段

### 6.1 一个必须先纠正的前提（【本次实测】）
`'requestVideoFrameCallback' in HTMLCanvasElement.prototype` = **false**（Edge 159 实测）。
⇒ **canvas 没有 rVFC。** 我们上屏对象是 `<canvas id="vframe">`（`:629`），所以**无法用 rVFC 直接测「上屏时刻」**。
旧文档反复建议的「output 记 ts + rVFC 算帧龄」**在 canvas 上不可实现**，必须换方案。

### 6.2 三个可行方案（按落地成本排）

#### 方案 A（推荐先做）：**零改码 · 控制台探针**（本会话已在真页面上验证可用）
在浏览器 Console 粘贴，等 20 秒，执行 `__slProbeReport()`。原理是用 **prototype 钩子**（修改 `VideoDecoder.prototype.decode` / `VideoFrame.prototype.close` / `WebGL2RenderingContext.prototype.texImage2D`），**对已经存在的解码器实例同样生效**，所以可以在「画面已经在播」的页面上随时装，不用重开流、不用改源码。

```js
(function(){
  if (window.__slProbe) { console.log('探针已在跑；调用 __slProbeReport() 取结果'); return; }
  var P = window.__slProbe = { submit:new Map(), lat:[], tex:[], draw2d:[], q:[], raf:[],
                                frames:0, orphan:0, renderer:'unknown' };
  // ① 呈现路径判定（画面已开流时 getContext 会返回同一个上下文）
  try {
    var cv = document.getElementById('vframe');
    if (!cv) { P.renderer='noCanvas'; } else {
      var gl=null; try { gl = cv.getContext('webgl2'); } catch(e){}
      if (gl && !gl.isContextLost()) P.renderer='WebGL2';
      else { var c2=null; try { c2=cv.getContext('2d'); } catch(e){}; P.renderer = c2?'2D-fallback':'unknown'; }
      P.canvas = cv.width+'x'+cv.height+' (CSS '+getComputedStyle(cv).width+' x '+getComputedStyle(cv).height+')';
    }
  } catch(e){ P.renderer='probeErr:'+e.message; }
  // ② decode() 提交 → 出帧（含解码器排队+解码执行）
  var OVD = window.VideoDecoder;
  if (OVD && OVD.prototype && OVD.prototype.decode) {
    var od = OVD.prototype.decode;
    OVD.prototype.decode = function(chunk){
      P.submit.set(chunk.timestamp, performance.now()); P.q.push(this.decodeQueueSize);
      return od.call(this, chunk);
    };
  }
  // ③ 出帧 → 画完（页面在 output 的 finally 里调用 frame.close()，:1329）
  if (window.VideoFrame && VideoFrame.prototype.close) {
    var oc = VideoFrame.prototype.close;
    VideoFrame.prototype.close = function(){
      var t=performance.now(), ts=this.timestamp, s=P.submit.get(ts);
      if (s!==undefined){ P.lat.push(t-s); P.submit.delete(ts); } else { P.orphan++; }
      P.frames++; return oc.call(this);
    };
  }
  // ④ 逐帧绘制成本
  if (window.WebGL2RenderingContext && WebGL2RenderingContext.prototype.texImage2D) {
    var ot = WebGL2RenderingContext.prototype.texImage2D;
    WebGL2RenderingContext.prototype.texImage2D = function(){
      var a=performance.now(); var r=ot.apply(this,arguments); P.tex.push(performance.now()-a); return r;
    };
  }
  if (window.CanvasRenderingContext2D && CanvasRenderingContext2D.prototype.drawImage) {
    var odr = CanvasRenderingContext2D.prototype.drawImage;
    CanvasRenderingContext2D.prototype.drawImage = function(){
      var a=performance.now(); var r=odr.apply(this,arguments); P.draw2d.push(performance.now()-a); return r;
    };
  }
  // ⑤ 合成节拍
  var run=true, last=0;
  (function loop(t){ if(last) P.raf.push(t-last); last=t; if(run) requestAnimationFrame(loop); })(0);
  window.__slProbeStop = function(){ run=false; };
  window.__slProbeReport = function(){
    var pct=function(a,p){ if(!a.length) return null; a=a.slice().sort(function(x,y){return x-y;});
      return +a[Math.min(a.length-1,Math.floor(a.length*p))].toFixed(2); };
    var r = { renderer:P.renderer, canvas:P.canvas, dpr:window.devicePixelRatio,
      visibility:document.visibilityState, hasFocus:document.hasFocus(),
      frames:P.frames, unmatchedOutput:P.orphan,
      decodeToDrawnMs:{p50:pct(P.lat,0.5),p95:pct(P.lat,0.95),max:P.lat.length?+Math.max.apply(null,P.lat).toFixed(1):null},
      texImage2DCallMs:{p50:pct(P.tex,0.5),p95:pct(P.tex,0.95),n:P.tex.length},
      drawImageCallMs:{p50:pct(P.draw2d,0.5),p95:pct(P.draw2d,0.95),n:P.draw2d.length},
      decodeQueueSize:{p50:pct(P.q,0.5),p95:pct(P.q,0.95),max:P.q.length?Math.max.apply(null,P.q):null} };
    var rf=P.raf.slice().sort(function(a,b){return a-b;});
    if(rf.length) r.compositeHz = +(1000/rf[Math.floor(rf.length/2)]).toFixed(1);
    console.log(JSON.stringify(r,null,2)); return r;
  };
  console.log('探针已装（renderer='+P.renderer+'）。让画面继续跑 20 秒，再执行 __slProbeReport()');
})()
```

**本会话已验证**（真 `docs/web-index.html` + 12 秒模拟直播，37fps 到达）：
`{"renderer":"WebGL2","frames":484,"decodeToDrawnMs":{"p50":0.6,"p95":0.9,"max":12.4},"texImage2DCallMs":{"p50":0.1,"p95":0.2,"n":444},"drawImageCallMs":{"n":0},"decodeQueueSize":{"p50":0,"max":0},"compositeHz":238.1}`
（headless 的 `compositeHz=238` 是因为无头不限帧；实机应是 60~165。另：探针在**页面尚未开流**时 `renderer` 会落到 `?/unknown`，必须**先让画面出图再装**。）

#### 方案 B（生产埋点，改 5 处、约 50 行）：把数字直接写进顶栏
| 插入点 | 行号 | 改什么 | 输出字段 |
|---|---|---|---|
| 1 | `:1312` `output:(frame)=>` 内第一行 | `const tOut = performance.now();` 并从 `submitMap` 取该 `frame.timestamp` 的提交时刻 | `decMs = tOut - tSubmit` |
| 2 | `:1329` `finally { frame.close(); }` 之前 | 记 `tDrawn`，`drawMs = tDrawn - tOut` | `drawMs` |
| 3 | `:1455` `decoder.decode(...)` 之前 | `submitMap.set(ts, performance.now())`（`submitMap` 容量上限 64，防泄漏） | 供 1 用 |
| 4 | `:1423-1426`（丢 delta 分支） | `skipped++`、`skippedTotal++` 已存在，把 `skippedTotal` 暴露出去 | `skipRate` |
| 5 | `:2798`（`#fpsinfo` 的写点）或 `:510` 那个 `<span id="fpsinfo">` | 每秒刷一次 `dec p50/p95 · draw p50/p95 · q · skip/s` | 顶栏小字 |
> 上屏时刻：canvas 无 rVFC（§6.1），**方案 B 也只能测到「画完」（`frame.close()` 之前），测不到「真正可见」**。要真测可见，只能上 `canvas.captureStream(0)` + `<video>` + `video.requestVideoFrameCallback`，但那会**引入额外一级流水**（本会话试跑该路径挂起/超时），**不推荐当埋点手段**。

#### 方案 C（要端到端）：服务端随帧下发 `capturedAt`
- 现状：`/api/info` 只回 `streamVersion / captureBackend / captureWidth / captureHeight / captureOrigin`（`AgentWebServer.cs:264-285`），**没有帧级时间戳**。
- `SessionRecorder.VideoFrame` 内部**已经有** `At = DateTime.Now`（`SessionRecorder.cs:1012`/`:795`），只是没写进流。
- 最小改法：v=2 的 4B 长度前缀已经是自定义帧头，可扩成 `8B 长度 + 8B capturedAtUnixMs`，前端在 `:1496-1505` 解出后算 `now - capturedAt` = **端到端单程**。**这是唯一能把 233ms 拆成实数的办法**，也是最该做的一件事（0.5 人日）。

### 6.3 埋点字段汇总（要什么、为什么）
```
renderer          WebGL2 / 2D-fallback     ← 决定是不是掉进了 22ms 路径
canvas            2560x1440 (CSS …)       ← backing vs 显示尺寸
dpr / visibility / hasFocus               ← 排除节流/分屏变量
frames            收帧数                   ← 供计算丢帧率
unmatchedOutput   提交了但没出帧的条数       ← 解码器真丢帧
decodeToDrawnMs   p50/p95/max             ← 浏览器段核心数字（解码排队+解码+绘制）
texImage2DCallMs  或 drawImageCallMs       ← 逐帧绘制成本（区分两条路径）
decodeQueueSize   p50/p95/max             ← 是否触发 SKIP_QUEUE
compositeHz       rAF 节拍                 ← 60/120/被节流
```

---

## 7. 关键判断：与服务端 ~90ms 记账之间的矛盾（本报告最重要的一节）

### 7.1 已用本次实测排除的
| 候选 | 实测上界 | 等级 |
|---|---|---|
| 传输/收包（回环） | **≤2ms**（p95 0.4ms，max 1.9ms） | 【本次实测】（与 transport-network-analyst 的结论一致） |
| 切帧/合并/NAL 重打包 | **<0.3ms** | 【本次实测】 |
| 解码排队 + 解码 | **≤1.6ms**（p95），吞吐 330~384fps | 【本次实测】 |
| 绘制（WebGL2） | **0.1~0.2ms** | 【本次实测】 |
| canvas 与显示尺寸不匹配的缩放开销 | **0**（GPU bilinear） | 【本次实测】 |
| 假 `ts`（66666）造成的调度延迟 | **0**（A/B 三组 p50 全 0.5~0.6ms） | 【本次实测】 |
| 窗口失焦/不可见 → rAF/合成降频 | **不适用**（不用 rAF；不可见即断流） | 【代码事实】+ 用户澄清 |

⇒ **浏览器段合计典型 1~5ms、最坏 ≤25ms。它解释不了 233ms，也解释不了 ~140ms 账外缺口。**

**独立交叉印证（同队 `TOOL-20261124-DIRECT-PROBE.md`）**：同为 127.0.0.1 回环、同一个 `/stream?v=2`、**完全不含浏览器**的原生链路（HttpClient 收包 → MF 软解 → GDI `SetDIBitsToDevice` 上屏）实测：**解码 0.91ms + 上屏 7.79ms ≈ 9ms**，且**帧到达间隔 gap p50 = 55ms**（≈16~18fps，与其 agent 日志 16.4fps 交叉验证、`staticSkipped=0` 排除静止误判）。
两条独立证据的一致结论：
- 浏览器 vs 原生（软解+GDI）的**解码+呈现差**只有 **~0~3ms**（我们硬解 0.5~1.0ms + WebGL2 0.1~0.2ms，反而更快）；
- **真正在 55ms 一帧的，是服务端「出帧节奏」**，不是任何客户端侧的处理耗时。

### 7.2 仍未归属的部分（按嫌疑排，含新增候选）
1. **【新增候选，代码事实】起播/重连时，浏览器可能从一个「陈旧 IDR」起播**
   `TrySubscribe` 取 `initial = Ring[start].Nal`，`start` = Ring 里**从后往前找到的第一个关键帧**（`SessionRecorder.cs:1111-1116`）。
   而 NVENC 是**无限 GOP**（`NvencSdkEncoder.cs:561` `NVENC_INFINITE_GOPLENGTH`，只有 `RequestKeyFrame`/首帧才出 IDR），Ring 保留 **30 秒**（`SessionRecorder.cs:49` `_keepSeconds=30`）。
   **后果**：某些时刻 Ring 里"最近的关键帧"可能就是**几十秒前那一帧 IDR**。浏览器 `:1517` 在第一个 key 之后就把 `firstKey = true`，随后到达的新 delta 帧以该陈旧 IDR 为参考起点——要么解不出来（→ `:1285-1308` 触发按需 IDR，多花 1~2 个 RTT + 等 IDR），要么解出**过期画面**直到下一次新帧覆盖。
   **这条能解释「偶发的、几百 ms 到秒级的起点延迟」，但解释不了「稳定的 233ms」**。→ 【待实测】：连续重连 20 次，记录「第 1 个 decoded frame 的 timestamp 与当时服务端最新 frameIndex 的差」。
2. **【推断】秒表差分法的系统误差**：两个窗口各自的呈现延迟、截图时机、秒表自身的刷新量化，都会并入差分。一次 30fps 的量化误差就是 ±33ms，多次平均不能消除系统性偏置。
3. **【已有实测 + 本次交叉印证】服务端节拍税被「真机 16~18fps」放大**（`TOOL-20261124-DIRECT-PROBE.md`：原生探针独立测得 `gap p50 = 55ms`）：
   `E2E-LATENCY-BASELINE` 的 13ms 节拍税是按「半个出帧周期」算的；但 transport 侧真机实测**实际只有 ~18fps**（帧间隔 55ms）→ **半周期 = 27.8ms**，不是 13ms。再加上 DDA 的 `AcquireTimeoutMs=100`（`DxgiDdaCapture.cs:53`）在「静止→活动」瞬间会阻塞满 100ms（`:265`）。
   ⇒ **「实际只有 18fps」这一条本身就是延迟**：帧龄（frame age）的期望值 ≈ 半个帧间隔 = 27.8ms，而用户看到的画面在**任何时刻**都已经比真实桌面旧了这么多。**这不在服务端日志的 captureMs/convertMs/encodeMs 里，是账外项。**
4. **【推断】服务端 `EncodeNv12` 的同步上传+同步取流**：`enableEncodeAsync=0`、`LockBitstream.doNotWait=0`（`NvencSdkEncoder.cs:583,697`），每帧 `UpdateSubresource(byte[])` 前还要 `AllocHGlobal + Marshal.Copy` 一次 5.25MB（`:663-666`）。CPU 路径下这段是**串行阻塞**，且不在 `encodeMs` 的 `Stopwatch` 里（`SessionRecorder.cs:1005-1007` 只包 `EncodeNv12` 调用，实际包含了；但 `UpdateSubresource` 的 GPU 等待是否计入取决于驱动）。→ **归服务端成员，此处只标疑点。**

### 7.3 本报告对「浏览器段是 20~40ms 还是更大」的明确回答
- **不是 20~40ms，也不是 140ms。实测是 1~8ms（WebGL2 主路径）。**
- **例外（唯一会让它变大的条件）：落到 2D 回退 + `streamFps=60`** → 稳态 ~100ms 并持续丢帧。**这是唯一需要用户在实机上确认的分支。**

---

## 8. 用户可自跑的最短自检（2 分钟，按顺序做，每步都能单独定论）

### 步骤 1（30 秒）：确认「我有没有落到 2D 回退」——一条命令
打开观看页（画面已经在动）→ F12 → Console → 粘贴：
```js
const c=document.getElementById('vframe'), gl=c.getContext('webgl2');
console.log('路径:', (gl && !gl.isContextLost()) ? 'WebGL2(正常)' : '2D回退(慢22ms/帧)',
            '| canvas', c.width+'x'+c.height, '| DPR', devicePixelRatio,
            '| visibility', document.visibilityState, '| hasFocus', document.hasFocus());
```
- 输出 `WebGL2(正常)` → **浏览器段不是问题，不要再往这条线上花时间**（对应 §7.1 的实测数字）。
- 输出 `2D回退(慢22ms/帧)` → **这就是答案**：你这台机器上浏览器每帧多 22ms，且 `streamFps=60` 时会稳态积压 ~100ms。修法 §5.3。

> 也可以在 Console 里搜一条现成的 warn：`[H264] WebGL2 呈现初始化失败，回退 2D drawImage`（`:1211`）。有这条 = 落到 2D。

### 步骤 2（1 分钟）：测出你这台机器上的浏览器段实数
同 Console，粘贴 §6.2 方案 A 的探针 → 等 20 秒 → 执行 `__slProbeReport()`。
判读：
| 输出 | 含义 |
|---|---|
| `renderer: "WebGL2"` 且 `decodeToDrawnMs.p50 < 3`、`decodeQueueSize.max ≤ 2` | 浏览器段健康（1~5ms），账外缺口**不在浏览器** |
| `renderer: "2D-fallback"` 或 `drawImageCallMs.p50 > 15` | **浏览器段在吃 20~25ms/帧**，改 WebGL2 / 降 backing store |
| `decodeQueueSize.max` 经常 ≥ 4（30fps）或 ≥6（60fps） | 解码跟不上 → 检查是否软解（步骤 3） |
| `compositeHz` 明显低于显示器刷新率（如 60Hz 屏却只有 20） | 合成被节流（分屏/省电模式/远程桌面） |
| `unmatchedOutput` 持续增长 | 解码器真丢帧（走 IDR 恢复） |

### 步骤 3（30 秒）：确认硬解没退化成软解
同 Console：
```js
navigator.mediaCapabilities.decodingInfo({type:'file',video:{contentType:'video/mp4; codecs="avc1.640033"',width:2560,height:1440,bitrate:8000000,framerate:60}})
  .then(r=>console.log('硬解能力:', r.supported, 'smooth:', r.smooth, '省电:', r.powerEfficient));
```
- `smooth:true, 省电:true` → 硬解可用（本机实测为 `supported:true, smooth:true, powerEfficient:true`）。
- `supported:false` 或 `smooth:false` → 你处在软解/无 GPU 环境，浏览器段会被放大（本机软解 1440p 实测 2.03ms/帧只是噪声码流的乐观值，真实复杂画面会显著更高）。

### 步骤 4（10 秒，可选）：排除窗口节流
同 Console：`setInterval(()=>console.log(performance.now()|0, document.visibilityState, document.hasFocus()), 1000)`，把浏览器窗口**置顶并点一下**、再**切到 RDP 窗口**各看 10 行。
- 本页在 `hidden` 时会**直接断流**（`:2615-2629`），所以「切走再切回」会看到重连——这是设计行为，不是节流。
- 若在 `visible + hasFocus` 下浏览器段的数字（步骤 2）已经很小，**分屏/窗口化就与 233ms 无关**。

---

## 9. 本次实测的方法（可复现，未改任何产品代码）

1. **服务端**：用 PowerShell `Add-Type` 起一个真实 `System.Net.HttpListener`（与 `AgentWebServer` 同一套 http.sys 栈），`SendChunked=true`，每帧 `[4B 大端长度][33000B 帧体]`，帧体前 8 字节写**服务端墙钟毫秒（double, little-endian）**，按 27~33ms/帧 `Write+Flush`。
2. **浏览器**：`msedge.exe --headless=new --remote-debugging-port=<私有端口>`（**独立 `--user-data-dir` 在 %TEMP%，用完按 PID 精确停掉并删除**），用 Node（`C:\Users\91244\.dsh\dsh-runtimes\…\node.exe`）经 CDP `Runtime.evaluate` 驱动；页面用 `file:///E:/Documents/deepseek-harness/SoulLink/docs/web-index.html` 载入真实页面做探针验证。
3. **码流**：本机 `VideoEncoder` 生成真 H.264 Annex-B（`avc1.42001f/avc1.640028/avc1.640033`），喂真 `VideoDecoder`；1440p 用 `avc1.640033`（Level 5.1，`avc1.640028`=L4.0 会因 coded area 超限被拒，这是一个值得记住的坑）。
4. **统计**：每项 ≥25 次采样取 p50/p95/max；时延全部用**同机墙钟**或 `performance.now()` 同源相对量，避免跨时钟。
5. **清理**：所有 headless Edge 按 **精确 PID**（`37540/40056/49976/55716` 及其子进程）停止；所有临时脚本（`%TEMP%\sl_*.js|ps1`、`cdp*.js`、`_sl_*_prof` 目录）**全部删除并验证为 0**；临时端口 19303/19311-19316/19321/19331/19341/19351 **全部关闭并验证为 0 监听**。**全程未触碰 `dist\stable\SoulLink-Stable.exe`（PID 13392）、未使用 A1/A4 会话、未停启任何现有进程。**

---

## 10. 建议（按性价比，全部为「下一步」而非本任务已做）

| # | 改动 | 位置 | 收益 | 成本 |
|---|---|---|---|---|
| P0 | **服务端随帧下发 `capturedAt`**（v=2 帧头 4B→12B） | `AgentWebServer.cs:516-525` + `web-index.html:1496-1505` | 把 233ms 从「差分猜」变成「单程实测」，是终结账外缺口的唯一手段 | 0.5 人日 |
| P1 | **首帧必须有「真·第一帧」超时**：8s 只覆盖首字节，不覆盖「连上但不出帧」 | `web-index.html:1349` 旁加「`firstKey` 未置真且 >5s → requestIdr + 重连」 | 消除起播/回前台最长 25s+10s 的死等 | 0.5h |
| P2 | **2D 回退兜底**：WebGL2 不可用时，把 canvas backing 降到 `min(显示尺寸, 视频尺寸)` | `web-index.html:1248` 后 + `:1315-1319` | 实测 2D 强制同步从 **21.9ms → 3.7ms**（backing 900×506） | 1h |
| P3 | **只读自检页/控制台探针**（把 §6.2 方案 A 做成页面上的按钮） | 新增 | 用户随时自证浏览器段，不必等我们 | 1h |
| P4 | **重连时校验首帧新鲜度**：`initial` 若来自 Ring 且帧龄 > 1s，直接弃用、等服务端新 IDR | `SessionRecorder.cs:1111-1116` | 消除「陈旧 IDR 起播 → 花屏/错帧 → 再走 IDR 恢复」的偶发秒级延迟 | 0.5h |
| P5 | 生产埋点（§6.2 方案 B，5 处插桩 + 顶栏小字） | 见 §6.2 表 | 把上面的数字固化进产品，长期可观测 | 0.5 人日 |

> **不要做的**：把浏览器段再当主攻方向。本次实测（1~8ms）已经证明它不是 233ms 的账；继续在 SKIP_QUEUE/时间戳/拷贝优化上花时间，收益都在 0.3ms 以下。

---

## 11. 与同队成员的接口

- **transport-network-analyst（AUDIT-10 + TOOL-DIRECT-PROBE）**：本报告独立复现了「回环 chunked 每帧 Flush → 浏览器 read 立即返回（p50 0ms/p95 0.4ms/max 1.9ms）」，**支持其「传输层 ≤2~20ms、换 UDP 收益≈0」的结论**。补充：`reads/frames = 1.37~1.51`，说明 4B 前缀与帧体偶尔分两次到达，**但总延迟仍 ≈0**。另外，其 `direct-probe` 原生探针给出的「解码 0.91ms + 上屏 7.79ms」与「gap p50 = 55ms」与本报告**完全互洽**：浏览器段 ~1~5ms（甚至比原生 GDI 上屏更快），55ms 的帧间隔只能来自服务端出帧节奏。**两条独立证据合并后，「浏览器段」作为账外缺口候选正式关闭。**
- **input-chain-analyst（AUDIT-11）**：其 §6 把「Chrome 渲染管线 + 可见性节流」列为最大嫌疑。**本报告用代码事实（不用 rAF/rVFC）+ 用户澄清，把这条划掉了**；请把账外缺口重心移到「服务端实际只有 18fps 的帧龄（半周期 ≈27.8ms）+ DDA 100ms 阻塞尾 + 秒表法系统误差 + 起播陈旧 IDR」。
- **rdp-session-analyst（AUDIT-08）**：其 §0.3 把「浏览器段」列为账外缺口最可能藏身处。**本报告实测否决了这一指向**（1~8ms），请改引本报告 §7.1。
- **lead**：`233ms` 里浏览器段的贡献上界已被收窄到 **≤25ms（且典型 1~8ms）**；剩余缺口请按 §7.2 的 4 条候选推进，其中 **P0（帧级 capturedAt）是唯一能一劳永逸的办法**。

---

## 12. 边界遵守自查

未改任何产品代码/脚本/配置/版本号；未碰 `dist\stable\SoulLink-Stable.exe`（PID 13392）；未使用 A1（rdp session 2）/A4（rdp session 3），未在那两个会话里做任何操作；未停/启任何**现有**进程（只停了我自己起的 headless Edge，按精确 PID）；未按名字批量杀；全程 `Start-Process -WindowStyle Hidden`，未弹窗；未改注册表；未发布、未碰 COS/latest.json；未执行任何 git 写操作（只 `git status` 只读）；`bin/`/`obj/`/`dist/` 未改动；未在 C 盘新建任何东西（临时文件全部在 `%TEMP%` 且已删除）；未新建团队、未调用 `dsh_team_create`。唯一写入 = 本文档 + `docs/research/README.md` 一行登记。
