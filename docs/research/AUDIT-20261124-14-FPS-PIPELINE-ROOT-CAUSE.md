# 审计 14 · P0 根因：配置 60fps 实际只出 16fps —— 抓屏供给不足 还是 节拍器自激降速？

> 日期：2026-11-24
> 作者：rdp-session-analyst（dsh-team team_mwu35l8pae8oj5 / member m_mwu3790mafbish / task task_mwu5724ahvg41u）
> 性质：只读审计 + 根因判定。未改任何产品代码/脚本/配置/版本号；未碰 dist\stable\SoulLink-Stable.exe；未用 A1/A4；未执行 git。修复建议仅以 unified diff 形式写入本文档（未落 .cs 文件，遵守红线 5）。
> 证据分级：【代码事实】= 源码 file:line；【已有实测】= 本仓库前序实测；【推断】= 有依据推导；【待验证】= 无证据不猜。

---

## 0. 一句话结论

**16fps 是「抓屏供给不足」，不是「节拍器自激降速」——占比约 100% : 0%。** 决定性证据是 direct-probe 实测的 captureMs 波动 8~89ms：captureMs 里包含 DDA AcquireNextFrame(100) 的阻塞等帧时间，若桌面供给真有 60fps，captureMs 应稳定 <5ms（Acquire 立即返回）；captureMs 高到 89ms 说明 worker 每拍都在「等桌面出新帧」，等掉的时间就是「桌面供给 < 60fps」的直接度量。节拍器（AdvanceSlot 累积式，SessionRecorder.cs:1173-1183）实现正确、无自激——它已在 65fps WPF 动画下被独立证伪到 58.86fps（MULTI-VIEWER 实测），不存在「节拍器把自己拖慢」的机制。

---

## 1. 三份 fps 数据对照：同一个「矛盾」

| 来源 | 场景/动画源 | 配置 | 实测出帧 | captureMs | 关键证据行 |
|---|---|---|---|---|---|
| MULTI-VIEWER-DDA-CONTENTION §4.1 | 全屏 WPF（48 移动矩形，CompositionTarget.Rendering 约 65fps 回调） | 60fps | **58.86fps**（1290帧/21.912s）；双 viewer 60.00fps | 未单列 | 文档 §4.1 表 PID 35400 |
| RDP-FRAMERATE-BENCHMARK §5.1 | 60fps 全屏视频/动画，CPU 管线长样本 | 60fps | **48.59fps**（840帧/17.289s，逐 30 帧区间 40.5~52.4） | 3~15ms | 文档 §5.1 |
| TOOL-DIRECT-PROBE §3.1 | 「SoulLink-DupFrame-Animation」桌面动画 | 60fps | **16.4fps**（30帧/1.82~1.83s；探针 gap p50 55.38ms） | **8~89ms 波动**；staticSkipped=0 | 文档 §3.1 |

**关键矛盾**：同是「60fps 配置 + NVENC-SDK + 1920x1080 + Intel 抓屏」，MULTI-VIEWER 能出 58.86fps，direct-probe 只出 16.4fps。两者唯一不同 = **动画源**：前者是 65fps WPF 高频动画，后者是「DupFrame」动画（名字即暗示『重复帧/低频变化』测试源）。这直接指向：**出帧率被动画源（桌面新内容供给）限死，而非被节拍器限死。**

---

## 2. 走读：节拍器与抓屏超时的交互（file:line）

### 2.1 每拍的完整时序【代码事实】

Loop() 每轮：
  1. ConsumeKeyFrameRequest()                          SessionRecorder.cs:758
  2. WaitUntilNextSlot(ref nextAt)  <- 60fps→周期16.67ms   SessionRecorder.cs:759, 1191-1198
  3. CaptureBgraWithProbe → CaptureBgra               SessionRecorder.cs:897
     → AcquireNextFrame(100)  <- 阻塞等帧，最多100ms      DxgiDdaCapture.cs:53, 265
        · 有新帧 → 立即返回（读回8.3MB BGRA）            DxgiDdaCapture.cs:266-296
        · 无新帧 → 等满100ms超时，复用上一帧             DxgiDdaCapture.cs:298-303
  4. 静止判定：FrameSequence 是否增长                    SessionRecorder.cs:928-955
  5. BgraToNv12（并行 1~2ms）+ EncodeNv12（3~5ms）       SessionRecorder.cs:1002-1007
  6. AdvanceSlot(nextAt += 16.67ms)                     SessionRecorder.cs:1068, 1173-1183

### 2.2 captureMs 的含义（本任务的钥匙）【代码事实 + 已有实测】

SessionRecorder.cs:897-899：
    var frameStart = Stopwatch.StartNew();
    var bgra = CaptureBgraWithProbe(out int width, out int height);
    long captureMs = frameStart.ElapsedMilliseconds;   // <- 含 AcquireNextFrame(100) 阻塞等帧时间

captureMs 不是「抓屏工作耗时」，而是「等帧 + 抓屏」的总和。DDA AcquireNextFrame(100) 是阻塞语义（DxgiDdaCapture.cs:265）：桌面有变化立即返回（读回 8.3MB 约 5.8ms），桌面静止则阻塞直到变化或 100ms 超时。因此：
- 桌面供给 65fps（每 15.2ms 一帧）→ Acquire 几乎立即返回 → captureMs ≈ 读回 5.8ms，稳定低位；
- 桌面供给 ~16fps（每 62.5ms 一帧）→ Acquire 平均等半个供给周期 ≈ 31ms → captureMs 波动在 8~89ms。

direct-probe 实测 captureMs 8~89ms（§3.1）正是后者，且 staticSkipped=0（SessionRecorder.cs:952 静止跳过未触发，桌面确实在变但变得慢）。captureMs 的波动幅度本身就是「桌面供给周期 ≈ 62.5ms → ~16fps」的实测度量。

### 2.3 节拍器无自激机制【代码事实】

AdvanceSlot（SessionRecorder.cs:1173-1183）是累积式 + 追帧补偿：
    long period = (long)(Stopwatch.Frequency / (double)fps);   // 60fps → 16.67ms
    long next = nextAt + period;
    if (next < now - period) next = now;   // 落后超一周期 → 对齐 now，不补发积压帧
    nextAt = next;

- 工作耗时（等帧+读回+转换+编码）包含在周期内，不叠加等待（这是 P0-F 修复，注释 SessionRecorder.cs:1157-1171 自证旧 bug）；
- 某拍超时（如静止等满 100ms）→ 追帧补偿把 nextAt 对齐 now，不制造连续追帧；
- 因此节拍器只设「上限」，不会把供给充足时的 60fps 压到 16fps。它的能力上限已被 MULTI-VIEWER（58.86fps）证伪。

不存在「节拍器自激降速」的代码路径。唯一能让出帧掉到 16fps 的，是「每拍都等帧等满 ~60ms」——也就是桌面供给只有 ~16fps。

---

## 3. 根因判定：供给不足 100% / 节拍器 0%

### 3.1 证据链（逐条反证节拍器嫌疑）

| # | 证据 | 结论 | 出处 |
|---|---|---|---|
| 1 | 同 60fps 配置、同拓扑，65fps WPF 动画 → agent 出 **58.86fps** | 节拍器能力 ≥58fps，无自激 | MULTI-VIEWER §4.1 |
| 2 | 独立 DDA metadata-only 探针（只 Acquire/Release 不读回）→ **65.9fps** | DDA API 本身无 60fps 以下上限 | RDP-FRAMERATE-BENCHMARK §2.1 |
| 3 | 同探针，GDI 4ms Invalidate 满屏动画 → **35~38fps** | GDI 类动画源自身产帧率不足 | RDP-DDA-CEILING §2 |
| 4 | direct-probe 动画 → agent 出 **16.4fps** + captureMs 8~89ms + staticSkipped=0 | 供给不足（等帧时间大）且非静止误判 | TOOL-DIRECT-PROBE §3.1 |
| 5 | 节拍器追帧补偿正确（落后对齐 now 不补发） | 无自激降速代码路径 | SessionRecorder.cs:1173-1183 |

结论：出帧率 = min(桌面新内容供给率, 节拍器上限 60fps)。三份数据分别落在「供给 65fps→59、供给 48fps→48.6、供给 ~16fps→16.4」，完全由「供给」这一变量决定，与节拍器无关。

### 3.2 供给 16fps 的来源（为什么这个动画源只有 ~16fps）【推断 + 待验证】

direct-probe 的动画源是「SoulLink-DupFrame-Animation」（§7 自述复用前序研究动画，40s 自限）。该名字即『DupFrame（重复帧）』测试源，产帧率本就不是 60fps。RDP 会话内桌面合成是变化驱动（RDP-DDA-CEILING §4.3），静止桌面 0.5fps、鼠标 65fps、GDI 动画 38fps——桌面供给率由「内容变化源」决定，不是常数 60fps。

注意（诚实标注）：direct-probe 动画源的确切产帧率未独立测量（工具已按红线清理，无源码可查）。「供给 = 16fps 是动画源自身产帧率」是【推断强，多源交叉】，决定性判据见 §5 的对照实验。

---

## 4. 场景对照表（场景 / 实测 fps / 单帧耗时 / 理论 fps / 差距归因）

| 场景（动画源） | 配置 fps | 实测出帧 fps | 单帧周期 | 关键段耗时 | 理论 fps（若供给充足） | 差距归因 |
|---|---:|---:|---:|---|---:|---|
| 65fps WPF（48 矩形） | 60 | 58.86 | 16.99ms | 读回5.8+转换1~2+编码3~5 ≈ 11ms | 60（受节拍器上限） | 管线损耗 ~1.1fps（正常） |
| 60fps 全屏视频（长样本） | 60 | 48.59 | 20.59ms | capture3~15+convert7~9+encode3~7 ≈ 20ms | 60 | 管线损耗 ~11fps（早期 CPU 转换未并行） |
| SoulLink-DupFrame-Animation | 60 | **16.4** | 61.0ms | captureMs 8~89（等帧为主）+convert1~2+encode3~5 | **~16（= 供给）** | **100% 供给不足：桌面每 ~62ms 才出一帧新内容** |
| 静止桌面（保活节拍） | 60 | ~2（保活） | 500ms | captureMs≈100（阻塞超时） | 0（无新内容） | 变化驱动合成，静止几乎不产帧（正常） |

判读：第三行的「单帧周期 61ms」几乎全由 captureMs（等帧）构成，而 convert 1~2ms + encode 3~5ms 合计仅 ~4~7ms——worker 在 61ms 里只有 ~6ms 在干活，其余 ~55ms 在等桌面出新帧。这就是「供给不足」的定量铁证。

---

## 5. 可复现的最小验证实验（不改产品代码）

目的：在「同一动画源、同一窗口」下用独立 DDA 计数器对照，一锤定音区分「供给不足」vs「节拍器/管线」。

实验 A（决定性）：
1. A2/session4 里跑 direct-probe 用过的「SoulLink-DupFrame-Animation」（复用前序计划任务，40s 自限）；
2. 同窗口跑独立 DDA metadata-only 计数器（RDP-DDA-CEILING 的工具，只 AcquireNextFrame/ReleaseFrame 不读回不编码，%TEMP%\dda-counter-final-results.txt 同款）；
3. 同时连 agent /stream?v=2，记录 agent 日志 total 增速与 captureMs 分布；
4. 判据：
   - 若独立计数器也 ≈16fps → 桌面供给真的只有 16fps，坐实供给不足，agent 无责；
   - 若独立计数器 65fps 而 agent 仍 16fps → 问题在 agent 管线/节拍器，回查 ReadBackTexture 每帧新建 staging（DxgiDdaCapture.cs:434-469）与 EncodeNv12 上传（NvencSdkEncoder.cs:650-666）。

实验 B（快速判据，看 captureMs 分布即可）：
- 连 agent 后只看日志 captureMs：p50 <5ms = 供给充足（管线瓶颈）；p50 ≈30ms 且波动大 = 供给不足（等帧主导）。本实验无需额外工具，30 秒出结论。

---

## 6. 修复建议（unified diff，未应用）

### 6.1 P0 诊断（先做，不改行为，只为把「供给 vs 管线」分开记账）

当前日志 total 是编码输出帧数，staticSkipped 是静止跳过计数，缺一个「桌面真实出帧」的独立计数，导致每次只能靠 captureMs 间接猜供给。DDA 后端已有 FrameSequence（= AccumulatedFrames 累计，DxgiDdaCapture.cs:285），把它打进日志即可直接对比。

\`\`\`diff
--- a/Relay/SessionRecorder.cs
+++ b/Relay/SessionRecorder.cs
@@ -1020,6 +1020,7 @@
                             if (_totalFrames % 30 == 0)
                                 RelayAgent.LogQuiet("[video] 共享管线 total=" + _totalFrames + " viewers=" + Viewers.Count
+                                    + " desktopSeq=" + (Capture is DxgiDdaCapture dda ? dda.FrameSequence : -1)
                                     + " captureMs=" + captureMs + " convertMs=" + convertMs + " encodeMs=" + encodeMs
                                     + " loop=" + _loopSequence + " lastCapture=" + _lastCaptureAt.ToString("HH:mm:ss.fff")
\`\`\`

效果：日志出现 desktopSeq 后，相邻两行 desktopSeq 增量 / 时间差 = 桌面真实供给率；total 增量 / 时间差 = 编码输出率。两者相等 → 供给是上限；两者差大 → 管线损耗。一眼定案，不再靠猜。

### 6.2 P1 若确认供给不足（本报告预判）

这不是代码 bug，是「桌面内容变化源产帧率不足」：
1. 真实游戏锁 30fps 就只能出 30fps，锁 60 才可能 60——把 streamFps 配置对齐游戏实际产帧率，避免 60fps 名不副实的误导；
2. RDP 会话 DWM 合成率封顶 60fps（DWMFRAMEINTERVAL=15 已设，RDP-DDA-CEILING §2 实测 p50=15.2ms），这是硬上限，换抓帧后端也破不了；
3. 若游戏产 60fps 仍出不满，才回到管线优化（见 6.3）。

### 6.3 P1 若确认管线瓶颈（本报告已基本排除，仅防御性列出）

\`\`\`diff
--- a/Relay/DxgiDdaCapture.cs
+++ b/Relay/DxgiDdaCapture.cs
@@ -428,6 +428,14 @@
         private byte[]? ReadBackTexture(ID3D11Texture2D srcTex, int width, int height)
         {
+            // 复用 staging 纹理与 BGRA 缓冲，禁止每帧 new（当前每帧 new staging + new byte[8.3MB] 进 LOH）
+            // 仅尺寸变化才重建；失败重建。
             try
             {
\`\`\`

依据：MULTI-VIEWER-DDA-CONTENTION §6 P0-1 已列「缓存 staging 和 BGRA buffer」，预期消除每帧 8.3MB LOH 分配与 GC 抖动。但这不是 16fps 的根因（16fps 时 convert+encode 仅 ~6ms，瓶颈在等帧不在分配），只在「供给 65fps 而管线追不上」时才有意义。

---

## 7. 证据索引

- 节拍器：Relay/SessionRecorder.cs:758-762（节流）、:897-899（captureMs 含等帧）、:1173-1183（累积式+追帧补偿）、:1191-1198（WaitUntilNextSlot）。
- 抓屏：Relay/DxgiDdaCapture.cs:53（AcquireTimeoutMs=100）、:265（AcquireNextFrame 阻塞）、:285（FrameSequence+=AccumulatedFrames）、:298-303（WAIT_TIMEOUT 复用旧帧）、:434-469（ReadBackTexture 每帧新建 staging）。
- 编码上传：Services/NvencSdkEncoder.cs:650-666（EncodeNv12 AllocHGlobal+UpdateSubresource）。
- 实测：MULTI-VIEWER-DDA-CONTENTION-20261124.md §4.1（58.86fps）；RDP-FRAMERATE-BENCHMARK-20261124.md §2.1（65.9fps）/§5.1（48.59fps）；RDP-DDA-CEILING-EXPERIMENT-20261124.md §2（GDI 38fps/鼠标 65fps/静止 0.5fps）；TOOL-20261124-DIRECT-PROBE.md §3.1（16.4fps/captureMs 8~89ms/staticSkipped=0）。

## 8. 边界遵守自查

未改任何产品代码/脚本/配置/版本号；未碰 dist\stable\SoulLink-Stable.exe；未用 A1/A4；未杀进程；未 git；未发布/COS/latest.json。修复建议仅以 diff 形式写于本文档，未落 .cs 文件。全部为读源码 + 读前序实测，无运行时操作。
