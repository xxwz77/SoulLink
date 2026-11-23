# 输入延迟量化与修复方案（含落点误差复核）

> 日期：2026-11-24
> 作者：mlow-mouse（dsh-team `team_mwtjn0d77v8gtr` / task `task_mwtjocvs8eabyl`）
> 范围：**只读定位 + 写研究文档，未改任何产品代码**。
> 证据标记：【代码事实】= 源码逐行可证；【实测】= 仓库历史文档实测值；【推断】= 有依据未实测。
> 关键历史口径：端到端 **444ms**（GPU 零拷贝部署后）→ 曾 340ms（A1/A2 后），见 `HANDOFF-VIDEO-LATENCY-GPU-20261124.md`。

---

## 0. 结论摘要（先看这三句）

1. **「鼠标很卡」的主因不在输入前向，而在画面回传（feedback）**。输入前向（浏览器事件 → 注入完成）在局域网只有约 **5~15ms**，且网页侧已做过 8ms 合并 + noWait 去 RTT 优化；真正让用户感觉「跟不上手」的是**注入完成后光标移动要等一整条视频管线（采集 42ms + 帧间隔 + 队列 + 浏览器解码）才回传上屏**，这是 444ms 的大头。
2. **落点误差只修了一半**：网页 `norm()` 已修（用 canvas 实际显示矩形 + letterbox 黑边），但 agent 侧 `AgentWebServer.HandleInputAsync` 仍用 `GetSystemMetrics(0/1)`（主屏尺寸）反算，且 `Math.Clamp` 到主屏——当抓屏后端尺寸/原点 ≠ 主屏时（DDA 抓第一个枚举输出、WGC 优先抓前台窗口、多屏主屏原点非 0,0），落点必偏、且**点不到副屏**。
3. **最大单项收益是让 GPU 零拷贝真正生效**：DDA 后端当前仍走 CPU 读回（`captureMs=42ms` 实测），零拷贝分支静默降级 CPU；消掉这段 + CPU BGRA→NV12 转换，是「340ms→继续往下压」的第一优先项。

---

## 1. 输入链路逐段耗时表（输入前向 + 画面回传）

链路 = 浏览器事件 → 归一化/合并 → 传输 → Agent 解析 → 注入 → 系统响应 → 画面回传。

| # | 段 | 代码位置（文件:行号） | 耗时来源 | 量级 | 可消除量级 |
|---|---|---|---|---|---|
| ① | 浏览器事件 → 归一化 `norm()` | `docs/web-index.html:1410-1428` | 纯浮点/取 rect，可忽略 | <0.1ms | — |
| ② | 去抖/合并（move 8ms 节流） | `docs/web-index.html:1747`(`MOVE_MIN_INTERVAL_MS=8`) → `pumpMove` `:1756-1771` | **8ms 合并窗口**（latest-wins，窗口内最多排一条） | 0~8ms | 可降到 ~2ms（见 Fix-3） |
| ③ | 传输（**每 move 一个 HTTP POST**） | `docs/web-index.html:1310-1320`(`postInputRaw` fetch)；服务端 `Relay/AgentWebServer.cs:115-131`(`LoopAsync` 每请求 `Task.Run`) | 无 WebSocket/WebRTC，每事件一个无状态 HTTP POST：连接/头/token/CORS + LAN RTT | LAN 1~5ms（【实测】`/live` 0.8ms p95 4.6ms）；WAN 10~50ms | 换 WebSocket 持久连接可消 HTTP 开销，RTT 仍在（见 Fix-4） |
| ④ | Agent 解析 | `Relay/AgentWebServer.cs:483-498`(读 body→JSON 反序列化→`GetSystemMetrics`) | JSON 反序列化 + 每请求 `GetSystemMetrics` 两次 + `Task.Run` 调度 | ~1ms | 可忽略；顺带省掉 key/wheel 场景的无效坐标换算 |
| ⑤ | 注入 | `Relay/RelayAgent.cs:806-817`(`move`→`SetCursorPos`)；`moverel` `:820-848`(`mouse_event`) | 系统调用 <1ms | <1ms | — |
| ⑥ | 系统响应 | 注入后系统光标即时移动 | 应用反应视应用而定 | <1ms | — |
| ⑦ | 画面回传·采集 | `Relay/SessionRecorder.cs:516-519`(`CaptureBgraWithProbe`) → `DxgiDdaCapture.cs:176`(`AcquireNextFrame(16)`) + `:337-380`(`ReadBackTexture` GPU→CPU staging+Map) | **DDA CPU 读回 stall**（staging CopyResource+Map 强制 GPU flush）+ 抓屏前 fps 节流 `WaitUntilNextSlot` `:739-746` | `captureMs=42ms`（【实测】`HANDOFF-VIDEO-LATENCY-GPU-20261124.md`） | GPU 零拷贝生效可消去读回 stall（见 Fix-1） |
| ⑧ | 画面回传·转换 | `Relay/SessionRecorder.cs:596`(`BgraToNv12`) + `:849-878`(逐像素 CPU 循环) | 纯 C# 单线程 BGRA→NV12 | 1080p ~5~15ms（【推断】） | GPU 转换器可消去（随 Fix-1） |
| ⑨ | 画面回传·编码 | `Relay/SessionRecorder.cs:599`(`EncodeNv12`) → `NvencSdkEncoder.cs:555-579` | NVENC 已配低延迟（`zeroReorderDelay`+`frameIntervalP=1`+`ULTRA_LOW_LATENCY`+无限 GOP），同步阻塞 | 2~5ms | — |
| ⑩ | 画面回传·推流 | `Relay/AgentWebServer.cs:451-461`(`WriteStreamAsync` 空转 `Task.Delay(10)`)；`SessionRecorder.cs:627`(viewer.Pending ≤4 帧才丢) | 环形缓冲排队 + 每 viewer 队列深度 + 空转轮询 | 队列深度 × 帧间隔 | 降深度/丢旧保新（见 Fix-5） |
| ⑪ | 画面回传·浏览器解码+上屏 | `docs/web-index.html:1058-1130`(`startStream` fetch→`getReader`→WebCodecs `VideoDecoder`) | 解码 + vsync 合成上屏 | 1~2 帧（【推断】） | 减解码缓冲 |

**关键判断**：①~⑥（输入前向）合计 LAN 下 **约 5~15ms**，已经做得不错；⑦~⑪（画面回传）才是 444ms 的主体。因此「输入卡」的量化结论是——**输入链路本身不慢，慢的是「光标动完到看见」的反馈环**，优化重心必须在视频管线，而不是继续压输入段。

---

## 2. 落点误差复核（两处已知根因是否真修完）

### 2.1 网页侧 `norm()` —— **已修完** ✅

- 现状【代码事实】`docs/web-index.html:1410-1428`：`norm()` 改用 canvas(`#vframe`) 自身的 `getBoundingClientRect()`，并按 `iw/ih` 算 `object-fit:contain` 黑边 `ox/oy`，注释明确「★ 2026-11-24 修落点偏移」。
- 佐证：`CURSOR-AND-POINTER-LOCK-20261124.md` §2.2 的旧根因（用 `#scr` 矩形、被 `max-height:70vh` 夹住导致纵向偏移）已被本版代码消除。
- 归一化结果：`nx/ny ∈ [0,1]`，基准是**抓屏帧内容区**（= canvas.width/height = 解码帧物理分辨率 = 抓屏后端实际输出尺寸）。

### 2.2 agent 侧 `GetSystemMetrics` 换算 —— **未修** ❌（剩余唯一根因）

- 现状【代码事实】`Relay/AgentWebServer.cs:495-498`：
  ```csharp
  int w = Math.Max(1, GetSystemMetrics(0));   // SM_CXSCREEN = 主屏宽
  int h = Math.Max(1, GetSystemMetrics(1));   // SM_CYSCREEN = 主屏高
  int px = dto.x >= 0 ? (int)Math.Round(dto.x * w) : -1;
  ```
  `:526-527` 再 `Math.Clamp(px, 0, w-1)`、`Math.Clamp(py, 0, h-1)`；最终 `:538` 走 `RelayAgent.ExecuteWeb` → `move`/`click` 的 `SetCursorPos`（`Relay/RelayAgent.cs:808/853`）。
- **两端不一致**：网页按「抓屏帧尺寸」归一化，agent 按「主屏尺寸」反算。二者仅在「抓屏后端恰好等于主屏且原点(0,0)」时一致。

### 2.3 仍存在的偏差来源（逐条）

| 偏差来源 | 证据（文件:行号） | 判定 |
|---|---|---|
| **GetSystemMetrics vs 实际抓屏尺寸 `_width/_height`** | `SessionRecorder.cs:44`(私有 `_width/_height`) 由抓屏后端输出决定 `:592`；但 `AgentWebServer` 从不读它，只读 `GetSystemMetrics` | **核心根因**。三后端的输出尺寸源不同：BitBlt=主屏（`BitBltCapture.cs:124`）；DDA=第一个枚举输出的 ModeDescription（`DxgiDdaCapture.cs:104-120`）；WGC=前台窗口优先（`WgcCapture.cs:152-153`），失败才 `TryCreateFromMonitor(DisplayId(0))`（`WgcCapture.cs:256-260`） |
| **多屏 / 抓窗口** | DDA `EnumOutputs` 取**第一个**成功 DuplicateOutput 的输出（`DxgiDdaCapture.cs:104-116`），不保证是主屏；WGC 抓前台窗口时 `item.Size` 是**窗口**尺寸（`WgcCapture.cs:160`） | 分辨率 ≠ 主屏时，`nx*主屏宽` 尺度错 + 目标错 |
| **主屏原点非 0,0 / 副屏不可点** | `SetCursorPos` 用虚拟桌面坐标；`Math.Clamp(px,0,w-1)` 把可点范围锁死在主屏 | 抓屏若覆盖虚拟桌面/副屏，**副屏永远点不到**；主屏原点非 0,0 时整体平移错 |
| **DPI 缩放** | agent 已 `SetProcessDPIAware()`（`Relay/RelayAgent.cs:589`），故 `GetSystemMetrics` 返回物理像素；DDA `ModeDescription` 也是物理像素（`DxgiDdaCapture.cs:119-120`） | 主屏 100% 下一致；RDP 会话缩放/混合 DPI 时仍有偏差【推断·待真机】 |

### 2.4 复核结论

- 网页 `norm` 与 agent 换算**目前两端不一致**：前者基准「抓屏帧」，后者基准「主屏」。
- **只修了网页一半**；agent 侧必须改成以「抓屏后端实际输出几何（尺寸 + 虚拟桌面原点）」为基准反算，才能闭环（见 Fix-2）。

---

## 3. 修复优先级（按 收益/成本 排序，精确到 函数:行号）

### Fix-1【P0｜收益最大｜成本中】让 GPU 零拷贝真正生效，消去 DDA CPU 读回 42ms

- 现状【代码事实】：`SessionRecorder.cs:434` 零拷贝分支触发条件是 `gpuConverter != null && encoder is NvencSdkEncoder`；而 `gpuConverter` 只在编码器建立时创建（`:580-589`），创建失败则 `:586` 记「GPU 转换器不可用」静默降级。真机 DDA 仍走 CPU 读回（`captureMs=42ms`），说明**零拷贝分支没跑起来**（`HANDOFF-VIDEO-LATENCY-GPU-20261124.md` §2）。
- 改前逻辑：DDA `AcquireNextFrame` → `ReadBackTexture`（staging CopyResource+Map，`DxgiDdaCapture.cs:337-380`）→ CPU `BgraToNv12`（`SessionRecorder.cs:849-878`）→ 上传回 GPU 给 NVENC。
- 改后逻辑：DDA `TryCaptureBgraTexture`（`DxgiDdaCapture.cs:253-314`，已在显存内 CopyResource 到 Shared 纹理）→ `GpuColorConverter.ConvertFromSharedHandle` → `EncodeD3D11Texture`（`SessionRecorder.cs:450-452`），全程不落 CPU。
- 落地步骤：① 真机点观看，看 agent 日志三选一——「零拷贝色差 PSNR」（已生效）/「零拷贝失败，降级 CPU：xxx」/「GPU 转换器不可用」（卡点）；② 若 VideoProcessor 在 RDP 内不可用，改用「路线A 同 device」：把 DDA duplication 直接建在 NVENC 的 D3D11 device 上，免跨设备共享（见 `DDA-TEXTURE-SHARED-HANDLE-20261124.md`）。

### Fix-2【P0｜收益中｜成本低】agent 坐标换算改为「实际抓屏几何」

- 现状【代码事实】：`AgentWebServer.cs:495-498` 用 `GetSystemMetrics(0/1)`；`:526-527` clamp 到主屏。
- 改前：`px = round(nx * 主屏宽)`，`py = round(ny * 主屏高)`，clamp 主屏。
- 改后：
  1. 给 `SessionRecorder` 增加公开只读访问器，暴露实际抓屏几何：`CaptureWidth/_height`（已有私有字段 `:44`，编码器建立时写 `:592`）+ `CaptureOriginX/Y`（DDA 需保留 `output.Description.DesktopCoordinates.Left/Top`，现在 `DxgiDdaCapture.cs:124-126` 只当回退用、未保留；WGC 抓窗口时用窗口 rect，抓 monitor 时用 monitor 虚拟坐标；BitBlt 原点=主屏虚拟原点）。
  2. `HandleInputAsync` 改为 `px = CaptureOriginX + round(nx * CaptureWidth)`，`py = CaptureOriginY + round(ny * CaptureHeight)`；`clamp` 范围改为「抓屏矩形」而非主屏。
- 收益：多屏/抓窗口/主屏原点非 0,0 全部落点正确，副屏可点。

### Fix-3【P1｜收益小｜成本低】点击 `down→up` 的 `Thread.Sleep(18)` 缩短

- 现状【代码事实】：`Relay/RelayAgent.cs:850-865` `click` 分支：`SetCursorPos` → `MouseButton(down)` → `Thread.Sleep(18)` → `MouseButton(up)`；多击 `:861` 还有 `Sleep(90)`。
- 改前：down/up 之间固定 18ms（人为「按下时长」，对单击响应感知有贡献）；`Sleep` 还阻塞一个 ThreadPool 线程（每请求）。
- 改后：降为 8~10ms（仍保证 down/up 有序，不丢 up）；`Sleep(90)` 双击间隔按需降到 ~40ms。成本极低，纯改魔法数。

### Fix-4【P1｜收益中｜成本中】输入从「每事件 HTTP POST」改 WebSocket 持久连接

- 现状【代码事实】：`docs/web-index.html:1310-1320` 每个 move/click 一个 `fetch` POST；服务端 `AgentWebServer.cs:129` 每请求 `Task.Run(() => HandleSafe(ctx))`，且 `:224-243` 每请求做 CORS+token+白名单+反序列化。
- 改前：每 move 一个无状态 HTTP 往返，浏览器同域 6 连接并发上限会排队（旧版「右键 5-7 秒才见菜单」的帮凶，见 `web-index.html:1732-1746` 注释）；WAN 下更差。
- 改后：`/api/input` 升级为持久 WebSocket（或 SSE+POST 混合），`move` 走二进制/紧凑 JSON 帧，去掉每请求 HTTP 头/token/`Task.Run`/反序列化摊销。收益是消「每事件开销」与「连接排队」，绝对 ms 在 LAN 不大、但在 WAN 与高并发时显著，且降低抖动。

### Fix-5【P1｜收益中｜成本低】viewer 帧队列降深度 + 输入活动时丢旧保新

- 现状【代码事实】：`SessionRecorder.cs:627` 队列 `>= 4` 才丢旧非关键帧；`AgentWebServer.cs:451-461` 推流空转 `Task.Delay(10)` 轮询。
- 改前：反馈环里，队列深度（最多 4 帧）+ 轮询间隔直接折算成延迟。
- 改后：把 viewer 队列上限降到 1~2（latest-wins，输入活动期间尤其要保新丢旧）；推流等待改用事件/信号（已有 `LifecycleChanged`/`AutoResetEvent` 可复用，`SessionRecorder.cs:49/739-756`）替代 `Delay(10)` 空转。

### Fix-6【P2｜收益中｜成本低】stream FPS 上限与抓屏节拍对齐/上调

- 现状【代码事实】：`SessionRecorder.cs:67` 默认 `SOULLINK_STREAM_FPS=30`；`WaitUntilNextSlot` `:739-746` 按 `1/fps` 节流抓屏；`:510/:646` 编码后推进 `nextAt`。
- 改前：30fps → 帧间隔 33ms 是反馈延迟的硬下限；低 fps 下「光标动→画面变」至少等一帧。
- 改后：把 stream fps 默认/上限提到 60（编码器已 `frameIntervalP=1` 无 B 帧、无限 GOP，能扛），交互场景用 60，静态自动降频（已有静止去重 `:558-563`）。

### Fix-7【P2｜交由 cursor-ux】「蓝球」可见光标改造

- 现状【代码事实】：`docs/web-index.html:1430-1457` 已有 `plCursor` 跟随 move 显示（绝对模式非锁定态），`pointermove` `:1778-1781` 更新、锁定态隐藏。用户认定「只在本地鼠标显示、点一下隐藏」是废功能——这是**产品形态**问题，不在本任务范围，交 `cursor-ux` 成员，本任务只点名：`updateVisibleCursor`/`hideVisibleCursor` 的显隐时机与蓝球样式是改造入口。

---

## 4. 可测量的验收方法

### 4.1 输入前向延迟（浏览器 → 注入完成）

- 方法：网页 `postInputRaw` 发送前打 `performance.now()` 时间戳（或临时加 `Date.now()` 进 body），agent 侧 `HandleInputAsync` 入口/`ExecuteWeb` 完成处用现有 `InputTrace`（`Services/InputTrace.cs`，`SOULLINK_INPUT_TRACE=1` 开启，默认关闭）打时间戳；二者相减 = 传输 + 解析 + 注入。
- 验收目标：**LAN 下 p95 < 5ms**（当前 HTTP POST 约 1~5ms，改 WebSocket 后应稳定 <2ms）。

### 4.2 落点精度（norm↔注入闭环）

- 方法：远端桌面画/显示一个已知坐标的十字靶（例如主屏正中心 `(w/2,h/2)`），网页点击画面正中心，agent `move`/`click` 后 `GetCursorPos` 回读（`RelayAgent.cs:809/863` 已在回包里带 `X/Y`），对比「点击点换算坐标 vs 实际落点」。
- 验收目标：单屏 100% DPI 下误差 ≤ 2px；多屏（副屏）、主屏原点非 0,0、WGC 抓窗口三种场景逐一验证「能点到、点得准」——这三项是 Fix-2 的直接验收。

### 4.3 端到端反馈延迟（含画面回传）

- 方法：沿用 `LATENCY-BENCHMARK-20261123.md` 的口径——远端桌面放一个高频变化源（移动方块/计时器），用外部高帧相机或 codec 内 PTS 埋点测「注入完成 → 该帧上屏」。
- 验收目标：**端到端 < 150ms**（对齐成熟产品 LAN 体验，`MATURE-LATENCY-PLAYBOOK-20261123.md` 口径）；当前 444ms，Fix-1（-42ms 读回 -10ms 转换）+ Fix-5/6（-1~2 帧队列）合计应能压到 250ms 以内，再向 150ms 收敛。

### 4.4 一键自检（低成本回归护栏）

- 在 `/api/info`（`AgentWebServer.cs:252-265`）回显里追加 `captureWidth/captureHeight/captureOriginX/captureOriginY`，前端 `syncCapture`/`refreshCaptureBackend`（`web-index.html:2018-2032`）读取后与 `norm()` 用的 `canvas.width/height` 比对，不一致即提示「抓屏几何与坐标基准不一致」。这是 Fix-2 落地后的常驻自检。

---

## 5. 证据索引

- `docs/web-index.html`：`norm()` L1410-1428；`postInputRaw` L1310-1320；`sendInputOrdered/pumpInput` L1337-1367；`sendInputNoWait` L1370-1374；`sendMoveThrottled/pumpMove` L1751-1771；`MOVE_MIN_INTERVAL_MS=8` L1747；`lookDelta/flushLook` L1655-1674、`LOOK_HZ_MS=8` L1653；可见光标 L1430-1457；`startStream` L1058-1130；wheel L1930-1933；keydown L1948-1961。
- `Relay/AgentWebServer.cs`：`HandleInputAsync` L483-545（`GetSystemMetrics` L495-498、clamp L526-527、`ExecuteWeb` L538）；`LoopAsync` 每请求 `Task.Run` L129；`WriteStreamAsync` 推流 `Task.Delay(10)` L451-461；CORS/token/白名单 L224-250。
- `Relay/RelayAgent.cs`：`Execute` `move` L806-817（`SetCursorPos`）、`click` L850-865（`Thread.Sleep(18)` L859 / `Sleep(90)` L861）、`moverel` L820-848、`wheel` L881-886；`SetProcessDPIAware` L589；`SM_CXSCREEN=0` L391。
- `Relay/SessionRecorder.cs`：`Loop` L363-669；`WaitUntilNextSlot` L739-746；零拷贝分支 L434-514；CPU 路径 L516-632；`BgraToNv12` L849-878；`_width/_height` L44、写于 L592；`CreateEncoder` L239-257（NVENC 优先）；viewer 队列 ≤4 L627。
- `Relay/DxgiDdaCapture.cs`：`EnumOutputs` 取第一个输出 L104-116；`ModeDescription` 尺寸 L119-120；`AcquireNextFrame(16)` L176；`ReadBackTexture` L337-380；`TryCaptureBgraTexture` L253-314。
- `Relay/WgcCapture.cs`：`CreateForWindow` 优先 L152-154；`TryCreateFromMonitor(DisplayId(0))` L256-260；`item.Size` L160。
- `Relay/BitBltCapture.cs`：`GetSystemMetrics(SM_CXSCREEN/CYSCREEN)` L124。
- `Services/NvencSdkEncoder.cs`：低延迟配置 `zeroReorderDelay` L560、`frameIntervalP=1` L556、`ULTRA_LOW_LATENCY` L547/579、无限 GOP L555。
- `Services/MfFrameEncoder.cs`：`AVLowLatencyMode=TRUE` L176-177（MF 回退也已低延迟）。
- 历史文档：`HANDOFF-VIDEO-LATENCY-GPU-20261124.md`（444ms/340ms、captureMs=42ms、agent 坐标 P2）；`CURSOR-AND-POINTER-LOCK-20261124.md`（网页 norm 已修）；`LATENCY-BENCHMARK-20261123.md`（传输 0.8ms、软编/节拍历史）；`INPUT-CHANNEL-GAP-20261123.md`（输入通道 FIFO/noWait 已修）。

---

## 6. 边界遵守自查

未改任何产品代码/脚本/配置；未碰 `dist/`、`Relay/`、`Android/`、`docs/web-index.html`；未杀进程、未重启、未发布、未改版本号；唯一写入为本研究文档。README.md 登记交由 Lead/后续任务。
