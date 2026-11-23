# Agent 管线延迟审计：桌面像素 → 编码比特 → 网络写出（453ms 拆解 + Sunshine 逐项对照）

> 日期：2026-11-23
> 作者：agent-auditor（latency-gap-audit 团队 / member m_mwt4bbm311f48gj / task_mwt4bc0z11kaybm）
> 性质：**只读审计**。未改任何产品代码/脚本/配置；本文档是唯一写入物。未碰 dist\stable\、A1/A4 账号、任何运行进程、COS、latest.json。
> 审计对象：agent 端 `Relay\SessionRecorder.cs`（共享视频管线）、`Relay\DxgiDdaCapture.cs` / `Relay\WgcCapture.cs` / `Relay\BitBltCapture.cs`（抓屏）、`Services\NvencSdkEncoder.cs` / `Services\MfFrameEncoder.cs`（编码）、`Relay\AgentWebServer.cs`（发送）、`tools\nvenc-sdk-probe\Program.cs`（P/Invoke 形态对照）。
> 对照基线：Sunshine 源码快照（`artifacts/zero-copy-src/display_base.cpp`、`nvenc_base.cpp`，GPL-3.0 只读参考）。
> 证据标记：**【事实】** = 当前工作区源码/文档逐行可证；**【推断】** = 基于证据的推理，未单独实测；**【待验证】** = 需要新的测量才能确认。

---

## 0. 一句话结论

**453ms 的主体不是硬件能力问题，而是「单线程串行 + 定时轮询 + CPU 往返」三重架构税**：抓屏/转换/编码全部在同一个 `SoulLinkVideo` 线程串行执行（【事实】`SessionRecorder.cs:327,405-461`），帧按固定 fps 定时采集而非帧到达即编码（【事实】`SessionRecorder.cs:571-577` + `DxgiDdaCapture.cs:167` AcquireNextFrame(16)），数据路径 GPU→CPU→GPU→CPU→网络四趟拷贝（【事实】`DxgiDdaCapture.cs:253-296` + `SessionRecorder.cs:670-699` + `NvencSdkEncoder.cs:649-654`）。Sunshine 用「帧事件驱动 + 常驻 GPU 纹理 + 编码器 1 帧在飞」把同一段压到个位数毫秒。**码率上不去同样不是 NVENC 能力问题**：编码参数已与 Sunshine 基本对齐（【事实】`NvencSdkEncoder.cs:549-559` vs `nvenc_base.cpp:258-281`），瓶颈在**配置链路的 50Mbps 硬上限**（【事实】`SessionRecorder.cs:64` ReadInt("SOULLINK_VIDEO_BITRATE", 500, 50000, 8000)）与**静止去重把大部分帧根本送不进编码器**（【事实】`SessionRecorder.cs:431-436`）。

---

## 1. 线程模型：抓屏/编码/发送是否串行（任务重点 ①）

### 1.1 现状【事实】

| 环节 | 线程 | 证据 |
|---|---|---|
| 抓屏 + BGRA→NV12 + 编码 + 入 Ring + 分发 viewer | **同一个线程** `SoulLinkVideo`（后台线程，单实例） | `SessionRecorder.cs:327` `new Thread(Loop){Name="SoulLinkVideo"}`；`SessionRecorder.cs:405-461` 顺序执行 CaptureBgraWithProbe → BgraToNv12 → encoder.EncodeNv12 → 入 Ring → 遍历 viewers 入队 |
| 网络写出 | 每个 viewer 一个 `Task.Run`（线程池），与视频线程解耦 | `AgentWebServer.cs:129` `Task.Run(() => HandleSafe(ctx))` → `AgentWebServer.cs:439-449` 推流循环 |
| HTTP 请求接受 | `LoopAsync` 单循环 accept + 每请求 `Task.Run` | `AgentWebServer.cs:115-131` |
| 输入注入 | 经 `RelayAgent.ServePipe`（管道线程）→ `HandleInputAsync` 独立 Task | `RelayAgent.cs:641-697`、`AgentWebServer.cs:471-532` |

**结论**：发送与编码解耦（正确）；但**抓屏→转换→编码在单线程里串行**，三段耗时相加构成帧周期下限。1080p 量级：
- DDA 抓屏读回 0.13ms~33ms（【事实】`DXGI-DDA-RDP-20261123.md` 单帧 0.13ms；`DEPLOY-3.3.29-VERIFY-20261123.md` captureMs 2~33ms 波动，含 staging 纹理每帧新建 `DxgiDdaCapture.cs:271`）；
- `BgraToNv12` 纯 C# 逐像素约 8~12ms（【事实】`SessionRecorder.cs:670-699` 单线程标量循环；【推断】量级沿用 LATENCY-BENCHMARK §1.1③ 的 10~20ms 估计，未实测）；
- NVENC 同步编码单帧 1~3ms 量级（【推断】；探针实测出帧但无 per-frame 计时）。

**单线程串行 vs Sunshine**：Sunshine 采集线程与编码线程**分离**，纹理经共享表面池传递，编码器在飞 surface 数 1（MATURE-LATENCY-PLAYBOOK §2.1 引 sunshine video.cpp:892-900 surfaces=1）。SoulLink 没有任何阶段流水线化：**每帧的抓屏等待直接推迟编码，编码阻塞直接推迟下一帧抓屏**。

### 1.2 锁与队列深度【事实】

| 锁/队列 | 位置 | 影响 |
|---|---|---|
| `Gate`（全局锁） | `SessionRecorder.cs:32` | Loop 每帧至少进 3 次（`:388` 读参数、`:399-404` 读 fps、`:468-493` 入 Ring+分发）；入 Ring 段持锁做 foreach Viewers + 每 viewer `lock(viewer.Sync)`；`StatusText()`（`:282-304`）持同一锁遍历整个 Ring 统计字节数——**诊断查询会与帧生产争锁**（【推断】recordstatus 高频轮询会拖帧循环） |
| `viewer.Sync` + `Queue<byte[]>` | `SessionRecorder.cs:24-29,482-492` | 每个 viewer 独立队列，**深度上限 4**（`:489` 满时丢旧非关键帧、绝不丢 IDR）→ 最坏 4 帧排队 ≈133ms@30fps，但丢旧保新使稳态深度通常 ≤1 |
| `DxgiDdaCapture._gate` | `DxgiDdaCapture.cs:163` | 抓屏自锁；与视频线程同线程时无争用 |
| `LifecycleChanged`（AutoResetEvent） | `SessionRecorder.cs:49` | `SleepUntil` 用 `WaitOne(wait)` 代替 Thread.Sleep（`:575`），可被订阅/停止事件提前唤醒（好设计【事实】）；但 wait 用 Math.Ceiling 取整到毫秒，Windows 默认定时器精度 15.6ms，全仓 grep `timeBeginPeriod` **零命中**【事实】 |

**关键事实**：编码与发送之间唯一的队列是每 viewer 的 `Pending`（深 4）；抓屏与编码之间**没有队列**（直接串行）；编码器同步模式一次一帧、无在飞缓冲。这既限制并行度，也限制积压上限——**延迟主要不是积压造成的，而是每帧的串行路径耗时**。

---

## 2. 帧 pacing：30fps 定时 vs 帧到达即编码（任务重点 ②）

### 2.1 当前节拍【事实】

`SessionRecorder.cs:571-577` SleepUntil：

```csharp
private static void SleepUntil(ref long nextAt, int fps)
{
    nextAt += (long)(Stopwatch.Frequency / (double)fps);
    int wait = (int)Math.Ceiling((nextAt - Stopwatch.GetTimestamp()) * 1000d / Stopwatch.Frequency);
    if (wait > 0) LifecycleChanged.WaitOne(wait);
    else if (wait < -1000 / fps) nextAt = Stopwatch.GetTimestamp();
}
```

- **绝对时间节拍**（`nextAt += interval`）——旧版「相对睡」漂移已修（对比 LATENCY-BENCHMARK §1.3 旧码 :248-249；MATURE-LATENCY-PLAYBOOK §1.3 P1 建议已落地）【事实】。
- 但整条链是**定时采集**而非事件驱动：即使 DDA 5ms 后就有新帧，也要等满 33ms 周期（30fps）；33ms 内 DDA 出的第 2 帧会被下一次 AcquireNextFrame 覆盖丢失（DDA 只保留最新，`DxgiDdaCapture.cs:167` Acquire + `:190` ReleaseFrame）。
- **DDA 超时 16ms + 静止复用**：WAIT_TIMEOUT 时返回上一帧缓存 `_lastBgra`（`DxgiDdaCapture.cs:193-197`）。动态画面下每周期内 AcquireNextFrame 命中新帧的等待 ≤16ms；静止画面每次吃满 16ms 超时 → 复用旧帧 → SequenceEqual 命中 → 跳过编码（`SessionRecorder.cs:431-436`）。

### 2.2 与 Sunshine 的对照【事实：Sunshine 行为；推断：对 SoulLink 的等效影响】

Sunshine `display_base.cpp` 捕获循环（`artifacts/zero-copy-src/display_base.cpp:265-338`）：

1. **帧事件驱动 + frame pacing group**：起播用非零超时 200ms 等首帧（`:308`）；有帧后进入 pacing group——按 client 帧率的**绝对合成器时刻**睡（`:280-283` sleep_target，高精度 timer），醒来用 **0ms 超时** snapshot（`:295`）取「此刻刚合成的新帧」；错过则整组作废重等 200ms（`:285-289`）。**语义 = 取帧本身零等待，节拍由合成器时钟对齐**。
2. **超时后主动让锁**：AcquireNextFrame 持 D3D11 不公平锁，Sunshine 在超时分支显式 `sleep_for(10ms)` 不持锁（`:319-338` 注释原文），避免饿死编码线程——前提是**采集与编码分线程**。
3. **零拷贝**：捕获纹理直接送编码（`display.h:752` 延迟销毁的旧表面池），无 CPU 读回。

**差距**：SoulLink 是「定时轮询 + CPU 读回 + 单线程串行」；Sunshine 是「合成器时钟 + 事件驱动 + GPU 常驻」。前者每帧引入 0~33ms 节拍对齐等待 + 0~16ms DDA 超时等待（两者部分重叠），后者接近 0。

**Windows 定时器精度**【事实+推断】：`WaitOne(wait)` 在默认 15.6ms 定时器精度下实际睡眠可能是量化值——30fps 每帧 33ms 预算里**纯睡精度误差就可能吃掉 15ms**（【推断】未实测；现代 Windows 对短 WaitOne 有亚毫秒补偿但受进程精度设置影响；本项目无 timeBeginPeriod【事实】）。

---

## 3. 静止去重与按需 IDR 的交互（任务重点 ③）

### 3.1 三层静止处理【事实】

| 层 | 行为 | 证据 |
|---|---|---|
| ① DDA 超时复用 | AcquireNextFrame WAIT_TIMEOUT → 返回 `_lastBgra`（非 null） | `DxgiDdaCapture.cs:193-197` |
| ② SequenceEqual 全帧比较 | `previousBgra.AsSpan().SequenceEqual(bgra)` 相等 → `_skippedStaticFrames++`，SleepUntil 后 continue，**不编码不出 NAL** | `SessionRecorder.cs:431-436`（staticSkipped 计数在 `:300` StatusText、`:479` 每 30 帧日志、`:500` 每 150 循环活性日志输出） |
| ③ 按需 IDR 信号 | `ConsumeKeyFrameRequest()` 在去重判断**之前**消费 `_forceKeyFrame` | `SessionRecorder.cs:414-430` |

### 3.2 交互缺陷（延迟与可用性双重影响）

1. **IDR 请求被去重吞掉的问题已部分修复**【事实】：`:414-442` 顺序为——先 ConsumeKeyFrameRequest；forceKeyFrame 且 NVENC → `RequestKeyFrame()` 设 FORCEIDR；再做 SequenceEqual 判断，但 `:432` 条件带 `!forceKeyFrame`、`:438-443` forceKeyFrame 时强制编码并记日志「新观众按需 IDR：即使画面静止也强制编码」。即 3.3.32 已修 ENCODER-REBUILD-HANG §6-P0-1 的死锁。
   - 但 MF 分支：forceKeyFrame 时 MF 编码器 **Dispose 重建**实现 IDR（`:424-429`）——重建实测 26~54ms（ENCODER-REBUILD-HANG §1 表），期间无帧产出【事实】。
2. **「画面在动却被判静止」→ 触控不流畅的直接嫌疑**【推断，证据强】：HANDOFF-VIDEO-LATENCY-DONE §P0.5 记录 A2 真机日志 staticSkipped 在**用户操作画面时仍持续增长**（1956→2986→3458）。机理：RDP 会话桌面合成节奏慢于本地时，16ms 内新帧未合成 → AcquireNextFrame 超时 → 复用旧 `_lastBgra` → SequenceEqual 命中 → 跳过。**整个判定的输入本身已经是陈旧帧**（【事实】`:432` 比较基准是上次抓到的帧），于是动画面被判静止，视觉延迟直接叠加连续跳过的周期数（33ms@30fps × N）。**这同时解释了码率低（没编几帧）与触控不流畅（更新丢失）**。RDP 下 DDA 出帧节奏未实测（【待验证】）。
3. **去重判定的成本**：1080p SequenceEqual 比较 8.3MB/帧 ≈ 0.5~1ms（【推断】SIMD 内存比较量级估算），不是主要开销；真开销在「复用旧帧却仍做了完整抓屏读回 + 比较」。
4. **Ring 的 IDR 依赖时间淘汰**【事实】：`:480-481` 按 frame.At 裁剪；静止 30 秒后 Ring 只剩过旧 IDR，新观众 TrySubscribe 仍取它当 initial（`:538-543`）。若编码器已重置（参数热更新 frameIndex=0），旧 SPS/PPS 与新流不匹配 → 花屏或 25 秒等待超时（`AgentWebServer.cs:410-425`）。【推断】发生频率取决于重连次数，HANDOFF §P0.5 的前端重连循环与此吻合。

### 3.3 Sunshine 对照【事实】

Sunshine 无「全帧比较去重」：DDA 本身变化驱动（不动就不出帧），超时由客户端 RFI/IDR 机制处理重同步（MATURE-LATENCY-PLAYBOOK §3.1）。SoulLink 用「CPU 全帧逐字节比较」模拟变化驱动，**多花一次 8MB 读回 + 比较，且判定基准在 RDP 慢合成下会陈旧**。另外 `DxgiDdaCapture.cs:167` 把 AcquireNextFrame 返回的 `DXGI_OUTDUPL_FRAME_INFO`（含 LastPresentTime/AccumulatedFrames）用 `out _` **直接丢弃**——本可零成本拿到「桌面真的更新过没有」的权威信号。

---

## 4. NVENC 延迟相关参数逐项对照（任务重点 ④）

### 4.1 参数表【事实：双方源码；差异列为本审计判定】

| 参数 | SoulLink (`Services\NvencSdkEncoder.cs`) | Sunshine (`artifacts/zero-copy-src/nvenc_base.cpp`) | 差距 |
|---|---|---|---|
| tuning | `tuningInfo = NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY`（`:573`） | tune=ULTRA_LOW_LATENCY | ✅ 一致 |
| B 帧 | `frameIntervalP = 1`（`:550`） | `frameIntervalP = 1`（`nvenc_base.cpp:259`） | ✅ 一致 |
| GOP | 无限 GOP + 按需 IDR：`gopLength = NVENC_INFINITE_GOPLENGTH`（`:549`） | `gopLength = INFINITE`（`:258`）+ `idrPeriod = INFINITE`（`:362`） | ✅ 一致 |
| 码控 | CBR；`averageBitRate = maxBitRate = bitrateKbps*1000`（`:551-553`） | CBR；`averageBitRate = client.bitrate*1000`（`:260,275`） | ✅ 一致 |
| zeroReorderDelay | `bitField1 |= RC_ZERO_REORDER_DELAY_BIT`（`:554`，手写 bit9） | `zeroReorderDelay = 1`（`:261`） | ✅ 一致 |
| lookahead | **未显式清零**——依赖 preset+tuning 默认（`:541` GetEncodePresetConfigEx 取预设后只改列出的字段） | **`enableLookahead = 0`**（`:262`） | ⚠ 未显式关。ULTRA_LOW_LATENCY 预设应默认 0，但无代码证据（【待验证】读回 presetCfg 的 lookaheadDepth/lookaheadLevel 确认） |
| multiPass | `multiPass = 0`（`:556`） | DISABLED（`:271`） | ✅ 一致 |
| vbv | `vbvBufferSize = averageBitRate / fps`（1 帧 bit 量）+ 可选 % 上调（`:557-559`） | `bitrate*1000/framerate` + % 上调（`:277-281`） | ✅ 公式一致 |
| RefL0 | **未设置**（codec union 不可单独改，`:526-528` 注释：改任何 union 字段 → INVALID_PARAM，NVENC-SDK-RCPARAMS §2.1 踩坑结论） | `configure_reference_frames`：`list0_option = NV_ENC_NUM_REF_FRAMES_1`（`:298`），每帧 1 参考帧 + 大 DPB 留给 RFI | ⚠ 预设默认即 1，风险低；但未显式控制、也无 RFI 配套（见 §4.2） |
| intra-refresh | 未使用（无限 GOP + 按需 IDR 替代） | NVENC 主路径未用 | ✅ 等价 |
| async/surfaces | `enableEncodeAsync = 0` 同步（`:571`），只建 1 个 bitstream buffer（`:586-591`） | surfaces=1（1 帧在飞）+ 事件驱动 | ✅ 语义等价（1 帧在飞）；同步阻塞 vs 异步事件单帧延迟等价、流水线吞吐略差（【推断】） |
| 按需 IDR | `encodePicFlags = FORCEIDR | OUTPUT_SPSPPS`（`:668-670`） | forced-idr + repeatSPSPPS（`:361`） | ✅ 一致（SoulLink 用每帧 flags 绕开 codec union 禁改限制） |
| 输入上传 | **每帧 `Marshal.AllocHGlobal(total)` + `UpdateSubresource`**（`:649-654`），3MB/帧（1080p NV12=2.98MB） | 纹理直通（register 后 map 的是 GPU 纹理） | ❌ 每帧堆分配+释放+3MB 拷贝（【推断】0.1~0.5ms/帧；【待验证】换常驻 pinned buffer 或 GPU 纹理直通收益） |

**结论**：NVENC 编码参数本身已基本对齐 Sunshine（唯 lookahead 未显式清零待验证）。**参数不是 453ms 的主要贡献者**——与 NVENC-SDK-RCPARAMS 实测一致（IDR 33KB/QP33 说明码控在工作）。

### 4.2 缺失的两个成熟配套【事实】

1. **RFI（参考帧失效）**：Sunshine/Moonlight 丢帧时 RFI 优先于 IDR、拥塞期不请求 IDR（MATURE-LATENCY-PLAYBOOK §3.1）；SoulLink 服务端无 RFI 概念，客户端 `decoder.reset()` 后只等自然 IDR（`web-index.html:1153-1154`）。花屏恢复依赖下一次订阅触发按需 IDR。
2. **客户端队列**：SoulLink 网页 SKIP_QUEUE=5 / SKIP_RESET_LIMIT=60（`web-index.html:1138-1139`）已同构 Moonlight（阈值更激进），optimizeForLatency 已设（`:1225`）——这块达标。

---

## 5. 码率上限为什么上不去（任务重点 ⑤）

### 5.1 配置链路硬上限 50Mbps【事实】

`SessionRecorder.cs:64`：`ReadInt("SOULLINK_VIDEO_BITRATE", 500, 50000, 8000)`——**clamp 上限 50,000kbps = 50Mbps**。全链一致：
- `RelayClient.cs:655` Math.Clamp(s.VideoBitrateKbps, 500, 50000)
- `RelayClient.cs:755` env 下发同 clamp
- `WebServer.cs:901/952` 网页侧同 clamp
- `SessionRecorder.cs:104` UpdateVideoParameters 再 clamp
- `RdpGeneralSettings.xaml.cs:226` UI 滑杆上限 50Mbps

这是「800Mbps 量级差」的第一层：**配置空间被钉死在 50Mbps**。Sunshine 的 800Mbps 是客户端请求值直传 NVENC（`nvenc_base.cpp:275` `client_config.bitrate * 1000`），无中间 clamp。

### 5.2 帧供给不足：静止去重 + 串行节拍压制实际码率【事实+推断】

即便配 50Mbps：CBR 按「实际供给的帧」分配 bit。30fps × 串行路径（33ms 周期内 16ms DDA 超时 + 去重跳过）下：
- A2 真机日志 staticSkipped 操作中仍增长（HANDOFF §P0.5）→ 实际编码帧数远低于 30fps；
- DEPLOY-3.3.29-VERIFY 实测 P 帧均值 10.3KB：30fps × 10.3KB ≈ **2.5Mbps 实际码率**（【事实】由该文档 §4 帧型数据推算）。**不是编码器不肯给 bit，是没有帧来消耗 bit。**
- LATENCY-BENCHMARK §1.2 的 0.03~0.06Mbps 是静态桌面极端（几乎无帧编码）。

### 5.3 vbv=1 帧 bit 量对 IDR 的压制【事实】

`vbvBufferSize = averageBitRate / fps`（`:557`）＝单帧预算。8Mbps@30fps = 266Kbit ≈ 33KB/帧上限——IDR 33KB/QP33 画质偏低正是此约束（NVENC-SDK-RCPARAMS §1 注）。要 800Mbps 级画质 vbv 须同步放大（Sunshine 公式自动跟随 bitrate；SoulLink 同公式但因 bitrate 被 clamp 在 50M，vbv 一并被钉死）。

**结论**：码率差距 = **配置链 clamp（50Mbps）× 帧供给率（去重/节拍）**。编码器本身（RTX 4060 NVENC，probe 实测 1080p 可编）远未触顶。

---

## 6. 453ms 构成假说排序（哪一环贡献最大）

> 453ms 出处【事实】：`HANDOFF-VIDEO-LATENCY-DONE-20261123.md:10,20`（3.3.20~3.3.25 成果表，口径为浏览器端到端）。当前代码（3.3.33）相对 3.3.25 又加了 DDA 与去重修复，构成可能已变；下列为**按当前源码的假说排序**。

| # | 环节 | 估计贡献 | 机理 | 证据 |
|---|---|---|---|---|
| **H1** | **静止误判跳帧（去重吞帧）** | **~150-300ms（连续 N 帧被跳）** | RDP 慢合成下 `_lastBgra` 陈旧，SequenceEqual 把动画面判静止；每跳一帧延迟 +33ms@30fps；staticSkipped 操作中增长证明确实在跳 | 机制【事实】`SessionRecorder.cs:431-436` + `DxgiDdaCapture.cs:193-197`；A2 日志【事实】；贡献量【推断】（取决于跳帧连续数，未实测分布） |
| **H2** | **节拍对齐等待（定时 vs 事件）** | ~16-33ms/帧 | 每帧平均等半个周期 16.5ms + DDA 超时 16ms 可叠加；再加 15.6ms 定时器量子的可能 | 【事实】`SessionRecorder.cs:571-577`、`DxgiDdaCapture.cs:167`；平均量【推断】 |
| **H3** | **客户端解码/渲染链（浏览器）** | ~50-100ms | WebCodecs 解码 + 合成上屏 1~2 个 vsync（60Hz=16.7~33.3ms）+ JS 调度 | 【推断】（MATURE-LATENCY-PLAYBOOK §5.1 未实测项；`web-index.html:1225` optimizeForLatency 已设） |
| **H4** | **CPU 转换与上传（BGRA→NV12 + HGlobal）** | ~10-15ms/帧 | 逐像素转换 8~12ms + 3MB 上传 + 每帧堆分配 | 代码【事实】`SessionRecorder.cs:670-699`、`NvencSdkEncoder.cs:649-654`；毫秒量【推断】 |
| **H5** | **viewer 队列与网络** | ≤10ms + 队列深×33ms | 本机/局域网 TCP 0.8ms（LATENCY-BENCHMARK §1.1⑥）；队列深 4 但丢旧保新 | 【事实】`AgentWebServer.cs:439-449`、`SessionRecorder.cs:489`；传输 0.8ms【事实】 |
| **H6** | **编码器参数（B帧/lookahead/GOP）** | ~0（已对齐） | frameIntervalP=1、CBR、无限 GOP、按需 IDR 全部已设 | 【事实】§4.1 表 |
| **H7** | **TCP/协议（无 FEC/帧语义）** | ~0（局域网） | 0.03~2.5Mbps 负载下 HOL/重传几乎不触发 | 【事实】MATURE-LATENCY-PLAYBOOK §5.3 判定 |

**最大嫌疑 = H1（静止误判）+ H2（节拍）+ H3（浏览器端）叠加**；H1 与 H2 属 agent 端、可直接从源码消除；H3 需前端埋点（requestVideoFrameCallback）。**编码器（H6）已排除为因**——这与上一轮把主攻方向放在 NVENC 参数上的历史不同：参数已做完，**剩余差距在管线形态（线程模型/节拍/去重判定基准）与配置 clamp**。

**453ms 对账**【推断】：若 453 = DDA 等 16 + 节拍等 16 + 转换 12 + 编码 2 + 队列 33 + 浏览器 66 ≈ 145ms 基线，剩下 ~300ms 最可能来自 H1 的连续跳帧（≈9 帧被跳）与首帧等待/重连循环（`AgentWebServer.cs:414-417` 的 20ms 轮询 + 25s 超时）。**下一步先测「输入变化 → 下一帧 NAL 产出」的 agent 内时延**（StatusText 已有 lastCapture/lastEncode/lastNal 时间戳 `:298-299`，只差对齐输入时刻），即可拆开 H1 vs H3。

---

## 7. 逐环节对照总表（环节｜当前实现（文件:行号）｜成熟做法｜差距与影响）

| 环节 | 当前实现（证据） | Sunshine/成熟做法（证据） | 差距与影响 |
|---|---|---|---|
| 采集触发 | 定时轮询：33ms 周期 + AcquireNextFrame(16ms) 超时（`SessionRecorder.cs:571-577`、`DxgiDdaCapture.cs:167`） | 合成器时钟 pacing group：0ms 超时取帧 + 高精度 sleep + 200ms 等首帧（`display_base.cpp:276-308`） | 每帧多 0~33ms 节拍等待 + 0~16ms DDA 超时；帧率上限被周期钉死 |
| 采集→编码数据路径 | GPU 纹理 → staging → CPU BGRA(8MB) → C# NV12(3MB) → HGlobal → UpdateSubresource 回 GPU（`DxgiDdaCapture.cs:253-296`、`SessionRecorder.cs:670-699`、`NvencSdkEncoder.cs:649-654`） | GPU 纹理直通编码（`display.h:752` 表面池 + nvenc register_resource） | 4 趟拷贝 + 2 次 CPU 转换 ≈10-15ms/帧 + 内存带宽 + GC 压力（每帧 new 3 个大数组） |
| 线程模型 | 抓屏+转换+编码单线程串行（`SessionRecorder.cs:327,405-461`） | 采集/编码分线程 + 表面池传递 | 无流水线；慢段阻塞快段；编码期间不抓屏 |
| staging 纹理 | **每帧新建+销毁**（`DxgiDdaCapture.cs:271,293`） | 常驻 staging/表面池 | 每帧 D3D11 资源创建开销（【推断】0.5~2ms + 驱动验证） |
| 帧去重 | 全帧 CPU 逐字节比较，判定基准可能陈旧（`SessionRecorder.cs:431-436`） | DDA 天然变化驱动，无需比较；frame_info 有权威更新信号 | 误判跳帧 = H1 最大嫌疑；额外 8MB 比较；`:167` 把 frame_info `out _` 丢弃 |
| 按需 IDR | NVENC 分支 FORCEIDR ✅；MF 分支 Dispose 重建（26~54ms）（`SessionRecorder.cs:414-430`） | forced-idr + repeatSPSPPS（`nvenc_base.cpp:361`） | NVENC ✅；MF 分支重构成延迟尖峰 |
| NVENC 参数 | ULTRA_LOW_LATENCY + CBR + 无限 GOP + vbv=1帧 + zeroReorder + multiPass=0（`NvencSdkEncoder.cs:549-559`） | 同左 + **显式 enableLookahead=0** + RefL0=1 + RFI 配套（`nvenc_base.cpp:258-298`） | lookahead 未显式清零【待验证】；无 RFI |
| 码率上限 | clamp 50Mbps 全链 5 处（`SessionRecorder.cs:64` 等） | 客户端请求直传（`nvenc_base.cpp:275`） | 800Mbps 不可达的第一层原因 |
| 发送 | 每 viewer 独立 Task + 队列深 4 丢旧保新 + 逐帧 FlushAsync（`AgentWebServer.cs:439-449`、`SessionRecorder.cs:489`） | UDP+FEC+帧语义（公网场景，`stream.cpp`） | 局域网下差距 ~0（0.8ms 实测）；帧边界元数据缺失是将来升级缝 |
| 客户端恢复 | reset 后等 IDR（`web-index.html:1153-1154`） | RFI 优先 + 拥塞期不请求 IDR（Moonlight VideoDepacketizer.c） | 花屏恢复慢 |
| 活性观测 | lastCapture/lastEncode/lastNal + staticSkipped + loop（`SessionRecorder.cs:297-300`） | sleep_overshoot_logger 等系统级打点 | 已够定位 H1/H2；缺「输入→出帧」端到端打点 |

---

## 8. 行动项（按 ROI 排序）

| # | 行动 | 预期收益 | 成本 | 风险 | 证据 |
|---|---|---|---|---|---|
| **A1** | **修「静止误判」**：去重判定改用 AcquireNextFrame 的 `DXGI_OUTDUPL_FRAME_INFO`（LastPresentTime / AccumulatedFrames / Metadata 指针）而非复用帧字节比较；超时返回 null 并把「有变化」交给脏计数 | 消除 H1（~150-300ms 级）；触控跟手直接改善 | 0.5 天 | 低（frame_info 本来就拿到了但被 `out _` 丢弃：`DxgiDdaCapture.cs:167`） | 【事实】`:167` 丢弃 frame_info；staticSkipped 异常增长【事实】 |
| **A2** | **帧到达即编码**：有观众时 AcquireNextFrame 超时拉长到整周期（33ms@30fps）取到帧立即编码，或改 Sunshine 式 pacing group（0ms 取帧 + 高精度睡）；加 `timeBeginPeriod(1)`（仅视频线程存活期间，注意 AGENTS.md TickHub CPU 约束） | 消除 H2（~16-33ms/帧）+ 帧率上限跟随合成器 | 0.5-1 天 | 低-中（节拍重写需回归 actualFps） | 【事实】`:571-577`、`:167` |
| **A3** | **放开盘率空间**：5 处 clamp 50000 → 200000（或 500000），UI 滑杆同步；vbv 公式自动跟随 | 码率可达 200Mbps+（网络链路上限另测） | 0.5 天 | 低 | 【事实】§5.1 五处 |
| **A4** | **staging 纹理常驻 + NV12 上传固定 pinned buffer**（去每帧 CreateTexture2D/AllocHGlobal） | ~2-5ms/帧 + GC 压力降 | 0.5 天 | 低 | 【事实】`DxgiDdaCapture.cs:271`、`NvencSdkEncoder.cs:651` |
| **A5** | **采集/编码分线程**（NV12 byte[] 队列深 2 起步，终态纹理池） | 帧周期 = max(抓,转,编) 而非 sum | 1-2 天 | 中（锁模型需重审，Gate 与 viewer 分发要拆） | 【事实】`:327,405-461` 串行 |
| **A6** | GPU 零拷贝收尾（WGC/DDA 纹理 → GPU 转 NV12 → 直通编码） | 消 H4 全部（10-15ms） | 3-5 天 | 高 | NVENC-SDK-INTEGRATION §7-2 已列为后续阶段 |
| **A7** | lookahead 显式清零（读回 presetCfg 验证后置 0）+ 显式 numRefL0=1 | 消除残余不确定性 | 0.25 天 | 低 | 【待验证】§4.1 |
| **A8** | 测量闭环：agent 内「输入→下一 NAL」时间戳 + 前端 rVFC 帧龄 | 后续所有优化的可证伪性 | 0.5 天 | 无 | MATURE-LATENCY-PLAYBOOK §5 建议5 |

**执行顺序建议**：A1+A2（同属节拍/判定层，一起改一起验）→ A3+A4 → A8（测量先行贯穿全程）→ A5/A6（架构层）。

---

## 9. 证据边界与未验证清单

| 项 | 状态 |
|---|---|
| 453ms 的精确测量口径（版本/播放端/样本量） | 【待验证】HANDOFF 只给结论值；建议按 LATENCY-BENCHMARK §7 方式在浏览器端复测 |
| H1 的跳帧分布（连续跳帧次数直方图） | 【待验证】需 A2 会话日志统计 staticSkipped 增量 vs 输入事件时刻 |
| lookahead 默认值（ULTRA_LOW_LATENCY+P3 下） | 【待验证】读回 presetCfg.rcParams.lookaheadDepth |
| Windows 定时器精度对 WaitOne 的实际影响 | 【待验证】需 clockres/ETW 实测 |
| HGlobal 每帧分配实际耗时 | 【待验证】microbenchmark |
| 200Mbps+ 码率下本机/局域网链路实际吞吐 | 【待验证】A3 之后实测 |
| RDP 会话 DDA 出帧节奏 vs 桌面合成更新时刻 | 【待验证】记录 AcquireNextFrame 返回间隔序列 |

**未做的事**：未重启/停止任何进程；未构建项目（只读审计）；未碰 dist\stable\；未用 A1/A4 账号；未发布；未改 README.md（Lead 统一登记）。
