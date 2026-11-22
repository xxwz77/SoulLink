# 录制/直播双速率解耦设计（2fps 录像 + 60fps 直播）

> 日期：2026-11-22
> 作者：architect（dsh-team `soullink-stream-opt` / task `task_mwrq0ooi691i1d`）
> 范围：**只读分析 + 本产出文档**。未改任何业务代码、未动 dist/、未提交 git、未发版。
> 上游依据：`STREAM-PERF-AUDIT-20261122.md` §4.3 / §4.8-1（本设计是它的落地细化）。
> 代码基线：`SoulLink.csproj` `<Version>3.3.19</Version>`，`SessionRecorder.cs` 共 629 行。

---

## 0. 目标与一句话

**把"录制（回放用）"和"直播（实时观看用）"从同一条 30fps 采集循环里拆成两条独立节奏**：录制走低频（默认 2fps，够"触发前后 N 秒"取证回放），直播走高频（目标 60fps，让交互跟手），**两者共享同一次抓屏/一次 BitBlt**，只在"编码频率 + 缓冲归属"上分叉，避免双份抓屏白烧 CPU。

> 为什么是 2fps 录像 / 60fps 直播：这是任务给定的目标档位，本设计**只保证架构支持这两个速率可独立配置**，数值本身是产品决策、可调，不在此论证孰优。

---

## 1. 现状 → 目标 数据流对比

### 1.1 现状（单速率，录制绑架直播）

```
                    ┌──────────────── SessionRecorder.Loop()（唯一线程）────────────────┐
                    │   while(!_stopRequested)   (SessionRecorder.cs:226)                │
                    │     interval = 1000 / _fps   ← 单一 _fps=RecordFps (15/30)        │
                    │     GrabJpeg(_quality)       ← BitBlt + JPEG 编码（每次全屏）       │
                    │     Ring.Enqueue(Frame)      ← 只有一条环形缓冲 (Ring)             │
                    └──────────────────────────┬───────────────────────────────────────┘
                                               │  同一批帧
                     ┌──────────────────────────┼───────────────────────────────┐
                     ▼                          ▼                               ▼
              保存回放 MP4                直播 /live GetLatestJpeg()        单张截图
          (SaveRecording 落盘)           (AgentWebServer.cs:232)          (CaptureOnce)
                     │                          │
                     └── 都吃 _fps / _quality ──┘   ← 耦合点：改直播帧率 = 改录制帧率
```

**关键耦合事实**（有据可查）：

1. 采集循环只有一个 `_fps`（`SessionRecorder.cs:142`），`Loop()` 用它定节奏（`:231` `interval = 1000/fps`）。
2. `/live` 直播取的是**同一批帧**：`AgentWebServer.WriteLiveAsync` → `SessionRecorder.GetLatestJpeg()`（`AgentWebServer.cs:232`），而 `GetLatestJpeg` 返回 `Ring` 尾帧（`SessionRecorder.cs:368-377`）。
3. 回放保存也是**同一批帧**：`SaveRecording` → `WriteSlice` 遍历 `Ring` 落盘（`:515-530`），且 MP4 用 `_fps` 做时间基准（`:453` `TryEncodeMp4(dir, _fps, ...)`）。
4. 前端"直播帧率"被录制帧率**封顶**：`computeLiveMs()` 里 `if (hz > recordHz) hz = recordHz`（`web-index.html:553`，注释"不可能比录制更快拿到新画面"）。
5. 主程序下发录制参数只有 `RecordFps` 一项，没有独立的"直播帧率"通道（`RelayClient.RecordStartAsync` → `Cmd="record", Count=fps`，`RelayClient.cs:595-596`）。

**现状结论**：60fps 直播在现有结构里**根本做不到**——`_fps` 上限 30（`SessionRecorder.cs:176` `Math.Clamp(fps,1,30)`），且直播帧率被录制的 `RecordFps` 一票否决。

### 1.2 目标（双速率，共享单次抓屏）

```
        ┌──────────── 共享采集层（每 1/60s 触发一次，仅一次 BitBlt + 一次光标叠加）────────────┐
        │   GrabFrame():  GetDC → BitBlt(SRCCOPY) → DrawIconEx  → 产出【未压缩位图 hBmp】    │
        │   频率 = max(recFps, liveFps) = 60Hz（由直播驱动；纯录制时降回 recFps）              │
        └──────────────────────────────┬────────────────────────────────────────────────────┘
                                       │  同一张 hBmp
                    ┌──────────────────┴───────────────────┐
                    ▼                                      ▼
        ┌── 录制分支（低频，默认 2fps）──┐      ┌── 直播分支（高频，默认 60fps）──┐
        │  每 1/2s 才对 hBmp 编一次 JPEG  │      │  每 1/60s 对 hBmp 编一次 JPEG    │
        │  → RecRing.Enqueue(Frame)      │      │  → 只存【最新一帧】LiveFrame      │
        │  → SaveRecording 读 RecRing    │      │  → /live 读 LiveFrame            │
        └───────────────────────────────┘      └───────────────────────────────────┘
```

**目标收益（相对现状，估算）**：

| 项 | 现状（单 30fps） | 目标（2fps 录 + 60fps 播） |
|---|---|---|
| 抓屏次数/秒 | 30 | 60（直播驱动）或 2（纯录制） |
| JPEG 编码次数/秒 | 30 | 62（2 录 + 60 播） |
| 回放 MP4 体积 | 30fps 高质量 | 2fps，约 -93% |
| 直播端到端延迟 | 受录制帧率封顶 | 仅受直播帧率，可到 1/60s 级 |
| 空闲（无人看、仅录制） | 30fps 空转 | 2fps，编码/抓屏 -93% |

> ★ 注意：**编码次数并未减少**（直播 60fps 是 30fps 的 2 倍）。本设计的核心收益是**解耦**——录制体积骤降、直播延迟骤降、空闲纯录制时骤降；直播高帧率下编码 CPU 会比现状更高（这是"要 60fps 流畅"的必然代价，§6 有缓解：直播用更低 quality 或后续换 H.264 硬编）。**共享单次抓屏**只是避免"2fps 录 + 60fps 播各抓各屏"造成的 62 次/秒 BitBlt（7.9MB 拷贝）——那才是真正浪费。

---

## 2. Ring 管理机制核实（现有代码，逐点）

> 验收要求"Ring 管理机制核实"，下面是对 `SessionRecorder.cs` 现有实现的逐点确认，本设计的所有改动都建立在这些事实上。

| # | 事实 | 代码位置 | 对双速率设计的影响 |
|---|---|---|---|
| R1 | 只有**一条** `Queue<Frame> Ring` | `SessionRecorder.cs:139` | 需要新增第二条 `Queue<Frame> RecRing`（或拆成"录制帧序列"+"直播单帧"），并保留旧 `Ring` 名字以兼容 `GetFrameAt`/`WriteSlice` |
| R2 | `Frame` 只存 `byte[] Jpeg` + `DateTime At`，**不存位图** | `SessionRecorder.cs:132-136` | 直播高频若复用 `Frame` 结构 OK；但直播只需"最新一帧"，可不用 Queue 而用 `volatile byte[] _liveJpeg` + 锁 |
| R3 | 入队后按 `At` 时间戳清理超期帧 | `SessionRecorder.cs:243-244` | 录制分支保留该清理；直播单帧无需清理 |
| R4 | `GetLatestJpeg()` 用 `foreach` 遍历整条 `Ring` 取尾帧，O(n) | `SessionRecorder.cs:374` | 直播改读 `_liveJpeg` 后变为 O(1)，顺带修掉这个历史低效点 |
| R5 | `SaveRecording` 的"前 N 秒"读 `Ring`（`WriteSlice`）、"后 N 秒"读 `Ring` 追加（`WriteSliceFrom`） | `SessionRecorder.cs:515-546` | 保存必须读**录制缓冲**（RecRing），不能读直播单帧；这是"2fps 录像仍能取证"的关键 |
| R6 | MP4 编码用 `_fps` 作时间基准 | `SessionRecorder.cs:453` | 双速率后必须改用**录制分支的 fps**（`_recFps`），否则 2fps 录像会按 60fps 播放被 30 倍加速 |
| R7 | `_fps`/`_quality`/`_keepSeconds` 是单份静态字段 | `SessionRecorder.cs:142-144` | 拆成 `_recFps/_recQuality/_recKeep` 与 `_liveFps/_liveQuality` 两套 |
| R8 | `Start()` 里 `fps=Clamp(fps,1,30)` 硬上限 30 | `SessionRecorder.cs:176` | 直播要 60fps，必须放宽（至少直播分支放宽到 60），否则 60fps 目标是空话 |
| R9 | `Shutdown()` 只 `Join` 一个 `_worker` | `SessionRecorder.cs:209-218` | 双线程后要 `Join` 两个 worker，或改为单线程双节奏（见 §4 决策） |

---

## 3. 双路共享单次抓屏：同步方案 + 伪代码

### 3.1 核心决策：单线程双节奏，还是双线程？

**决策：单线程 + 双节奏（推荐）**，理由：

- `GrabJpeg`（抓屏+编码）天然是**串行重活**（BitBlt 7.9MB + JPEG 软编），开两个线程各跑各的抓屏不会更快，只会**双份 7.9MB BitBlt + 锁竞争**。
- 单线程用一个**主循环按 60Hz 触发抓屏一次**，抓到后**同一张位图**在"该不该给录制"和"该不该给直播"两个判据上分叉，逻辑最简单、最省 CPU、无共享位图所有权问题。
- 现有 `Loop()` 已是单线程，改动面最小。

**主循环伪代码**（替换 `SessionRecorder.Loop()`）：

```
loop():
  liveInterval  = 1000 / liveFps      // 默认 60 → 16.67ms
  recInterval   = 1000 / recFps       // 默认 2 → 500ms
  nextLive      = now
  nextRec       = now
  while !stop:
    # —— 等待最近的触发点（合并为单个 sleep，避免忙等）——
    wait = min(nextLive, nextRec) - now
    if wait > 0: sleep(wait)

    dueLive = now >= nextLive
    dueRec  = now >= nextRec

    # —— ① 共享抓屏：只要任一分支到期就抓一次（抓屏不重复）——
    if dueLive or dueRec:
        hBmp = GrabBitmap()            # GetDC + BitBlt + DrawIconEx，一次
        if hBmp != null:
            # —— ② 直播分支：需要就给这张位图编一次（直播质量）——
            if dueLive:
                _liveJpeg = EncodeJpeg(hBmp, liveQuality)   # 锁内写最新单帧
                nextLive += liveInterval
            # —— ③ 录制分支：需要才编一次（录制质量），并入 RecRing ——
            if dueRec:
                j = EncodeJpeg(hBmp, recQuality)
                RecRing.Enqueue(Frame{j, now});  trim(RecRing, recKeep)
                nextRec += recInterval
            Dispose(hBmp)

    # 漂移保护：若某次编码超时导致 nextLive 落后，避免一次性追赶
    if nextLive < now - liveInterval*2: nextLive = now
    if nextRec  < now - recInterval*2:  nextRec  = now
```

> ★ 关键点：**BitBlt 只执行一次**（`GrabBitmap()`），`EncodeJpeg` 执行 0/1/2 次取决于 `dueLive`/`dueRec`。当 2fps 录 + 60fps 播时，平均每秒 62 次编码、60 次抓屏；当纯录制（无直播）时，**抓屏和编码都降到 2 次/秒**（`dueLive` 恒 false）。

### 3.2 位图生命周期的两个选择（需标注为"待验证"）

- **方案 A（推荐先实现）**：`GrabBitmap()` 每次 `CreateCompatibleBitmap` 新建、编码后 `DeleteObject`（沿用现状 `GrabJpeg` 的 `finally` 清理，`SessionRecorder.cs:347-353`）。**正确性有保证**，未做位图复用优化。
- **方案 B（性能优化，后置）**：复用同一块 `hBmp`（`CreateCompatibleBitmap` 提到循环外）。省每帧 7.9MB 分配，但 BitBlt 与两个 `EncodeJpeg` 之间需保证**同一块位图不被并发读改写**——单线程下天然安全，但要注意 `Image.FromHbitmap` 会 lock 位图。**标为待验证**（`STREAM-PERF-AUDIT` §4.8-2 已列为独立优化，不与双速率强耦合）。

> 结论：双速率方案**不依赖**位图复用也能落地；位图复用是正交的额外优化，单独做。

### 3.3 直播"最新单帧"的数据结构

直播分支不建 Queue，用一个受锁保护的"最新帧"：

```csharp
// 新增字段
private static readonly object _liveGate = new();       // 或复用 Gate
private static byte[]? _liveJpeg;
private static long _liveSeq;                            // 帧序号，供前端判重/判静止

// GetLatestJpeg() 改为（保持方法名不变，AgentWebServer 无感知）：
public static byte[]? GetLatestJpeg()
{
    lock (_liveGate) return _liveJpeg;
}
```

- **兼容性**：`AgentWebServer.WriteLiveAsync` 仍调 `GetLatestJpeg()`（`AgentWebServer.cs:232`），无需改动。
- **录制缓冲**改名 `Ring → RecRing`（或新增 `RecRing` 并存 `Ring` 供旧调用方），`WriteSlice`/`WriteSliceFrom`/`GetFrameAt`/`FrameCount` 全部改读 `RecRing`。

---

## 4. file:line 级改动清单

> 分三块：代理（采集/编码/缓冲）、协议（下发直播参数）、前端（去掉录制帧率封顶）。

### 4.1 代理侧 `Relay/SessionRecorder.cs`（核心，最大）

| # | 改动 | 位置 | 说明 |
|---|---|---|---|
| 1 | 字段拆分：`_fps/_quality/_keepSeconds` → 录制组 `_recFps/_recQuality/_recKeep` + 直播组 `_liveFps/_liveQuality` | `:142-144` | 保留旧字段名做别名可减小改动，但**语义必须分离** |
| 2 | `Start()` 签名扩展：增加直播参数；`fps=Clamp(fps,1,30)` 的上限对**直播**放宽到 60 | `:174-201` | 录制仍 clamp 1..30，直播 clamp 1..60（新增 `_liveFps = Math.Clamp(liveFps,1,60)`） |
| 3 | `Loop()` 改双节奏主循环（§3.1 伪代码） | `:222-257` | 单线程双节奏，共享 `GrabBitmap()` 一次 |
| 4 | 拆 `GrabJpeg` 为 `GrabBitmap()`（抓屏+光标，返回 hBmp/托管位图）+ `EncodeJpeg(bmp,quality)`（编码） | `:311-354` | 抓屏与编码解耦，双分支复用同一抓屏结果 |
| 5 | 新增 `Queue<Frame> RecRing`（录制缓冲），保留/改名 `Ring` 供兼容 | `:139` | 直播用 `_liveJpeg` 单帧（§3.3），录制用 `RecRing` |
| 6 | `GetLatestJpeg()` 改为读 `_liveJpeg`，O(1) | `:368-377` | 顺带消除 `foreach` 尾帧遍历（历史低效） |
| 7 | `GetFrameAt()` / `FrameCount` 改读 `RecRing` | `:380-394` | 回放翻看只应看录制帧 |
| 8 | `SaveRecording` → `WriteSlice`/`WriteSliceFrom` 改读 `RecRing` | `:515-546` | 保存取证读录制缓冲，不读直播单帧 |
| 9 | MP4 时间基准 `_fps` → `_recFps` | `:453` | ★ 否则 2fps 录像按 60fps 播放 30 倍加速 |
| 10 | `WriteMeta` 的 `"fps":_fps` 拆成录/播两字段 | `:556` | meta.json 增加 `liveFps` |
| 11 | `StatusText()` 输出录/播两套 fps | `:151-166` | recordstatus 命令返回双速率，便于诊断 |
| 12 | `Shutdown()` Join 逻辑随单线程不变（仍一个 worker） | `:209-218` | 单线程方案下无需改，但若选双线程则须改 |
| 13 | `CaptureOnce()` 不受影响（单张截图独立调 `GrabJpeg`） | `:589-627` | 截图仍走全质量单帧，不改 |

### 4.2 代理侧 `Relay/RelayAgent.cs` + `Relay/AgentWebServer.cs`

| # | 改动 | 位置 | 说明 |
|---|---|---|---|
| 14 | `case "record"` 解析新增直播参数（字段复用，见 §5） | `RelayAgent.cs:813-829` | 从 `Request` 扩展字段或复用 `Count`/`Delta` 语义，避免破坏既有管道协议 |
| 15 | `/live` 无需改动（仍调 `GetLatestJpeg()`） | `AgentWebServer.cs:229-246` | 直播单帧由 `GetLatestJpeg` 内部切换，路由零改动 |
| 16 | （可选）`/live` 响应头增加 `X-Live-Fps`/`X-Frame-Seq` 便于前端观测 | `AgentWebServer.cs:240-243` | 供前端静止判据使用（顺带修 `STREAM-PERF-AUDIT` §3.3 的字节数判据） |

### 4.3 宿主侧 `Services/RelayClient.cs` + `Services/WebServer.cs` + `Views/ConsolePage.xaml.cs`

| # | 改动 | 位置 | 说明 |
|---|---|---|---|
| 17 | `RecordStartAsync` 增加直播 fps/quality 参数（透传到 `record` 命令） | `RelayClient.cs:595-596` | 管道协议字段复用（见 §5） |
| 18 | `WebServer.ApiSessions` 下发新增 `liveFps` 字段 | `WebServer.cs:853`（recordFps 附近） | 前端拿到直播帧率 |
| 19 | `HandleSettings` 支持网页改直播帧率（白名单只加 `liveFps`） | `WebServer.cs:887-951` | 注意 §2.3 的 POST 白名单坑（`WebServer.cs:307-321`） |
| 20 | `ConsolePage.TryStartRecordingAsync` 透传直播参数 | `ConsolePage.xaml.cs:391-394` | 随录制命令一起下发 |

### 4.4 前端 `docs/web-index.html`

| # | 改动 | 位置 | 说明 |
|---|---|---|---|
| 21 | `computeLiveMs()` 去掉 `if (hz > recordHz) hz = recordHz` 的**录制帧率封顶**，改为用 `liveFps` 封顶 | `web-index.html:550-554` | ★ 关键：否则直播仍被 2fps 录像卡死到 2fps |
| 22 | `applySettings` 新增 `liveFps` 读取 | `web-index.html:967-980` | 与 `recordFps` 平行 |
| 23 | 顶栏下拉新增"直播帧率"（默认 60） | `web-index.html:380-421` | 与录制组区分（同现有两组 UI 风格） |

---

## 5. 协议字段复用方案（不破坏既有管道）

现有 `record` 命令字段复用：`Count=fps, X=quality, Delta=keepSeconds`（`RelayClient.cs:596` 注释）。直播参数**不新增管道命令**，复用 `Request` 已有多余字段：

| 直播参数 | 复用字段 | 说明 |
|---|---|---|
| `liveFps` | `Request.Hotkey`（record 命令里当前未用）或 `Request.Dx` | 选**未被 record 语义占用**的字段，避免与 `Count/X/Delta` 冲突 |
| `liveQuality` | `Request.Dy` | 同上 |

> ★ 风险点：`Request` 是 `RelayAgent.cs:404-423` 定义的内部类，字段复用会造成语义魔法数。**更稳做法**：给 `Request` 加两个显式字段 `LiveFps`/`LiveQuality`（JSON 反序列化自动兼容，`PropertyNameCaseInsensitive` 已开，`RelayAgent.cs:451`）。**推荐后者**——加字段不破坏旧客户端（旧请求缺字段=0=走默认）。

---

## 6. 性能与风险再校准（诚实标注）

| 项 | 结论 | 标注 |
|---|---|---|
| 抓屏次数 | 2fps 录 + 60fps 播 = **60 次/秒 BitBlt**（直播驱动），共享一次 | 确定 |
| 编码次数 | 62 次/秒（2 录 + 60 播）| 确定 |
| 编码 CPU | 60fps 软编 JPEG 会比现状 30fps **更高**（约 2 倍），是"要 60fps"的代价 | 待实测（`STREAM-PERF-AUDIT` §2.3 打点） |
| 缓解手段 | ① 直播用更低 `liveQuality`（如 40~50，画面够看即可）；② 后续 P2 换 MF H.264 硬编（`STREAM-PERF-AUDIT` §4.5）；③ 直播静止时降频（复用已有 `stillStreak` 机制） | 待验证 |
| 录制体积 | 2fps vs 30fps ≈ -93% 落盘体积/内存 | 估算 |
| 直播延迟 | 从"受录制帧率封顶"变为"仅受直播帧率 + 编码时长"，60fps 下理论 < 20ms + 编码 | 估算，待实测 |
| 空闲纯录制 | 无人看时直播分支停，抓屏+编码都降到 2fps，接近"没人看不烧" | 确定（需配合"无人观看停直播"信号，见 `STREAM-PERF-AUDIT` §4.3 代理自统计方案） |

---

## 7. 可独立验证的分步施工顺序

> 每步可独立编译 + 独立回归，不依赖后续步骤。回归命令沿用 `tools/layout-check` 现有测试（`WebDirectCheck` 等）。

### Step 1（纯重构，行为不变）——拆 `GrabJpeg` 为 `GrabBitmap` + `EncodeJpeg`
- 改动：`SessionRecorder.cs:311-354`。
- 验证：录制/截图/直播帧内容与改前逐字节一致（`GetLatestJpeg` 返回值不变）。
- 回归：`dotnet run --project tools\layout-check\WebDirectCheck.csproj -c Release`。

### Step 2（加第二条缓冲，仍单速率）——`RecRing` + `_liveJpeg`，但 `_recFps==_liveFps==原 _fps`
- 改动：`SessionRecorder.cs:139`、`:368-377`、`:515-546`、`:453`。
- 验证：两缓冲内容一致、保存回放 MP4 帧率正确（meta.json 里 fps 正确）。
- 独立验收：跑一次真实录制 + 保存，对比 MP4 时长/帧数。

### Step 3（双节奏主循环，速率仍相等）——`Loop()` 改 §3.1 伪代码，`_recFps/_liveFps` 拆字段但都设为原值
- 改动：`SessionRecorder.cs:142-144`、`:174-201`、`:222-257`。
- 验证：行为仍等价（录/播都 30fps），CPU 无明显变化。
- 独立验收：`StatusText()` 输出双速率，前端画面仍流畅。

### Step 4（协议下发直播参数）——`RelayClient`/`WebServer`/`ConsolePage`/`RelayAgent` 加 `liveFps/liveQuality` 透传
- 改动：§4.2/§4.3 全部。
- 验证：`recordstatus` 命令能看到 `liveFps=60`，管道协议向后兼容（旧请求不传=默认）。
- 独立验收：`tools/layout-check` 加/跑一个 `record` 命令解析用例。

### Step 5（前端解封顶 + 直播帧率 UI）——`computeLiveMs` 用 `liveFps` 封顶 + 顶栏直播组
- 改动：§4.4 全部。
- 验证：把录制设 2fps、直播设 60fps，前端画面实际刷新约 60fps，`Network` 面板 `/live` 请求频率随直播帧率。
- 独立验收：`sync-webpages.ps1` 后 headless 渲染（`HANDOFF-3.3.19` §5 流程）。

### Step 6（最终联调 + 空闲停直播）——2fps 录 + 60fps 播端到端 + 代理自统计"无人看停直播分支"
- 改动：§4.3 的代理自统计空闲信号（`STREAM-PERF-AUDIT` §4.3 方案 A）。
- 验证：观看时直播 60fps、录制 2fps；关页面后直播分支停、录制继续 2fps；保存回放 MP4 为 2fps、时长正确。
- 独立验收：对比开关页面时代理进程 CPU（PowerShell `Sample()` 差分法，`PERF-3.3.4` §1）。

---

## 8. 验收标准（任务验收用）

| # | 验收项 | 判定 |
|---|---|---|
| A | `DUAL-RATE-DESIGN-20261122.md` 存在，含现状→目标数据流对比 | 本节 §1 |
| B | Ring 管理机制核实（逐点 + file:line） | §2 R1~R9 |
| C | 双路共享单次抓屏的同步方案 + 伪代码 | §3.1 伪代码 + §3.2 生命周期 |
| D | file:line 级改动清单 | §4 四表 23 项 |
| E | 可独立验证的分步施工顺序 | §7 六步，每步独立编译+独立验收 |
| F | 确定 / 待验证标注 | 全文"确定/估算/待验证/待实测"标注 |
| G | 未动业务代码/dist/git | 本次仅新增本文档 |

---

## 9. 确定 / 待验证 汇总

**确定（代码可证）**：
- 现状单 `_fps`、单 `Ring`、直播被录制封顶（§1.1 五条，全部有 file:line）。
- 直播 60fps 在现状 `Clamp(fps,1,30)` 下不可达（`SessionRecorder.cs:176`）。
- 保存回放与直播读同一 `Ring`（`WriteSlice` vs `GetLatestJpeg`），拆缓冲必须同步改 MP4 时间基准 `_fps`（`:453`）。

**待验证（需实测/决策）**：
- 60fps 软编 JPEG 的实际 CPU 占用（是否需 `liveQuality` 下调或换硬编）。
- 位图复用（方案 B）与 `Image.FromHbitmap` lock 的交互，是否安全。
- "2fps 录像"是否满足用户"触发前后 N 秒"的取证精度（2fps 意味着事件后可能丢失 0.5s 内的画面细节）。
- 直播/录制**质量**是否需要独立：2fps 录像可能想要高质量（`recQuality=75`），60fps 直播可能想低质量省 CPU（`liveQuality=40`）——这是产品取舍，需用户拍板。

---

## 10. 附：与上游审计的衔接

本设计是 `STREAM-PERF-AUDIT-20261122.md` §4.3（无人观看停直播）+ §4.8-1（采集/观看解耦）+ §4.1(a)（脏矩形，后置）的**第一步落地**。脏矩形/光标远端渲染/WebSocket 等后续优化可在此双速率骨架上叠加，互不冲突。
