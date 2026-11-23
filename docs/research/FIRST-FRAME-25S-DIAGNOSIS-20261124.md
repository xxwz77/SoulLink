# 首帧 25 秒超时根因诊断（静止起播 IDR 死锁未根治）

> 日期：2026-11-24
> 性质：只读定位 + 诊断文档；未改任何产品代码。
> 任务：task_mwtheurx3f3ktk（A2 后续：定位「点击观看 → 首帧返回」25 秒超时根因）
> 证据：`Relay/SessionRecorder.cs`、`Relay/AgentWebServer.cs`、`Relay/DxgiDdaCapture.cs`、
>   `Services/MfFrameEncoder.cs`、`Services/NvencSdkEncoder.cs`、git 历史 `d6058f7`(3.3.32)/`521f27a`(A2)。

---

## 0. 结论先行

**首帧 25 秒不是「首帧要等 25 秒才到」，而是「首帧永远到不了、撞上 25 秒超时上限后 503」。**

根因是一条**确定性死锁**，机制如下：

1. 静止画面下，抓屏后端（DDA / WGC）都不产出新帧 → 探测链 300ms+300ms 后必然降级到 BitBlt（约 600-700ms，非主因）。
2. BitBlt 抓到首帧后进入编码；但 **NVENC 不可用时会降级到 MF 软编/硬编 MFT**，而 MF MFT 的**首个 IDR 需要连续多帧输入才产出**（`AgentWebServer.cs:413` 注释自证「软件 MFT 可能先缓冲若干输入帧才产生 IDR」）。
3. `_forceKeyFrame` 只在**首帧**绕过静止去重（`SessionRecorder.cs:461` 的 `!forceKeyFrame && !desktopChanged`）。首帧喂给 MF 后 `_forceKeyFrame` 已被 `ConsumeKeyFrameRequest()` 消费归零（`:414`），且 `previousBgra` 已提交（`:469`）。
4. 从第二帧起：画面静止 → `desktopChanged=false` → 命中 `!forceKeyFrame && !desktopChanged` → `_skippedStaticFrames++` 直接 `continue`（`:461-464`）→ **不再给 MF 喂帧**。
5. MF 凑不齐产出 IDR 所需的输入帧 → `EncodeNv12` 永远返回 `null` → `Ring` 永远无帧 → `ReadViewerFrame` 永远 `null` → `WriteStreamAsync` 轮询到 25 秒上限 → 503（`AgentWebServer.cs:410-425`）。

**一句话：3.3.32 修「IDR 死锁」只保证 `forceKeyFrame` 的「首帧」能编码，但 MF 编码器需要「连续多帧」才吐 IDR，静止去重在首帧之后立刻切断后续输入，于是死锁复发——这正是任务标题「静止起播 IDR 死锁未根治」的精确含义。**

---

## 1. 首帧链路追踪（点击观看 → 首帧返回）

### 1.1 订阅入口：TrySubscribe（SessionRecorder.cs:552-578）

    // :566  新订阅置按需 IDR
    _forceKeyFrame = true;
    RecomputeLifecycle();                 // :567 → Set() 唤醒 worker
    if (Ring.Count > 0) {                 // :568 若 Ring 有历史 IDR，直接返回，不超时
        ... initial = Ring[start].Nal;
    }
    // 否则 initial 保持 null
    ...
    if (!StartCore(...)) {...}            // :575 worker 没跑则冷启动 Loop

关键：**Ring 为空时 `initial=null`**，进入 25 秒等待。Ring 为空发生在「冷启动」或「上一次 Ring 已被 keepSec 时间窗裁剪清空」。

### 1.2 HTTP 首帧等待：WriteStreamAsync（AgentWebServer.cs:404-455）

    // :406
    if (!SessionRecorder.TrySubscribe(out int viewerId, out byte[]? initial, out string error))
        throw ...;
    // :410-419  首帧等待循环，硬编码 25 秒
    if (initial == null) {
        var wait = Stopwatch.StartNew();
        while (initial == null && wait.ElapsedMilliseconds < 25000) {
            initial = SessionRecorder.ReadViewerFrame(viewerId);   // 从 Pending 队列取
            if (initial == null) await Task.Delay(20);
        }
    }
    // :420-426  超时 → 503
    if (initial == null) {
        string detail = SessionRecorder.StreamReadiness;
        LogFailure("[stream] 首帧等待超时：" + detail);
        await WriteText(ctx, 503, "视频尚未就绪：" + detail);
        return;
    }

**「接近 25 秒」= `:414` 的 `25000` 毫秒上限**，不是首帧真实耗时。

### 1.3 worker 主循环：Loop（SessionRecorder.cs:358-529）

按执行顺序（A2 落地后的当前代码）：

    // :414  A2：ConsumeKeyFrameRequest 移到抓屏前
    bool forceKeyFrame = ConsumeKeyFrameRequest();          // 消费 _forceKeyFrame（一次性）
    // :415  forceKeyFrame 绕过节流
    if (!forceKeyFrame && !WaitUntilNextSlot(ref nextAt)) { continue; }
    // :420  抓屏（含探测降级链）
    var bgra = CaptureBgraWithProbe(out int width, out int height);
    // :423  抓屏 null（探测未完成）→ IdleWait 5ms → 下轮
    if (bgra == null || width <= 0 || height <= 0) { ... IdleWait(); continue; }
    // :440-453  A1 桌面变化判定
    bool desktopChanged;
    if (Capture is DxgiDdaCapture ddaBackend) {
        currentFrameSeq = ddaBackend.FrameSequence;
        desktopChanged = previousFrameSeq < 0 || currentFrameSeq != previousFrameSeq;   // :453
    } else {
        desktopChanged = previousBgra == null || !previousBgra.AsSpan().SequenceEqual(bgra); // :458
    }
    // :461  ★ 静止去重闸门（死锁点）
    if (!forceKeyFrame && !desktopChanged) {
        _skippedStaticFrames++;
        IdleWait();
        continue;                                            // ← 从第二帧起静止画面永远走这里
    }
    // :468-469  提交 previous（latest-wins）
    if (Capture is DxgiDdaCapture) previousFrameSeq = currentFrameSeq;
    previousBgra = bgra;
    // :471-477  惰性创建编码器
    if (encoder == null || encodedWidth != ew || encodedHeight != eh) {
        encoder = CreateEncoder(ew, eh, fps, parameters);    // NVENC SDK → MF 硬编 → MF 软编
    }
    // :489  编码
    byte[]? nal = encoder.EncodeNv12(nv12, frameIndex * 1_000_000L / fps);
    // :495-525  nal 非空才进 Ring；否则 _noNalFrames++
    if (nal != null && nal.Length > 0) { ... Ring.Add(frame); ... }

### 1.4 抓屏探测降级链（SessionRecorder.cs:143-211）

    private static byte[]? CaptureBgraWithProbe(out int width, out int height) {
        if (_captureProbeComplete) return Capture.CaptureBgra(...);   // 已锁定则直抓
        // DDA 探测（mode 0）
        if (Capture is DxgiDdaCapture dda && _captureMode == 0) {
            _probeClock ??= Stopwatch.StartNew();
            frame = dda.CaptureBgra(...);                    // 静止桌面 → 见 1.5
            if (frame != null && frame.Length > 0 && dda.IsActive) { _captureProbeComplete = true; return frame; }
            if (_probeClock.ElapsedMilliseconds >= 300) {    // :163  300ms 降级 WGC
                dda.Dispose(); Capture = new WgcCapture(); _probeClock = null;
                return CaptureBgraWithProbe(...);            // 递归
            }
            Thread.Sleep(10); return null;                   // 未到 300ms，返回 null 等下轮
        }
        // WGC 探测
        if (Capture is WgcCapture wgc && _captureMode != 2) {
            _probeClock ??= Stopwatch.StartNew();
            frame = wgc.CaptureBgra(...);                    // 静止桌面 → FrameSequence==0，返回 null
            if (frame != null && ... && wgc.FrameSequence > 0) { _captureProbeComplete = true; return frame; }
            if (_probeClock.ElapsedMilliseconds >= 300) {    // :195  300ms 降级 BitBlt
                wgc.Dispose(); Capture = new BitBltCapture(); _probeClock = null; _captureProbeComplete = true;
                fallback = Capture.CaptureBgra(...);         // BitBlt 立即出帧
                return fallback;
            }
            Thread.Sleep(10); return null;
        }
        return Capture.CaptureBgra(...);
    }

### 1.5 DDA 静止桌面永远不出帧（DxgiDdaCapture.cs:173-211）

    // :173  阻塞式 AcquireNextFrame，16ms 超时
    Result hr = _duplication.AcquireNextFrame(16, out frameInfo, out resource);
    if (hr.Success && resource != null) { ... 出帧 ... }
    else if (hr.Code == DXGI_ERROR_WAIT_TIMEOUT) {   // :206  静止桌面
        width = _lastWidth; height = _lastHeight;
        return _lastBgra;                            // 首次 _lastBgra == null
    }

**DDA 桌面复制语义**：`AcquireNextFrame` 只返回「自上次以来新合成的帧」。静止桌面无新帧 → 16ms 超时 → 返回 `_lastBgra`（首次为 `null`）。故 DDA 探测要求 `frame != null && dda.IsActive` 在静止桌面下**永远不满足**，300ms 后必然降级 WGC。

### 1.6 WGC 静止桌面同样不出帧（WgcCapture.cs）

WGC 是事件驱动（`FrameArrived` → stash 帧 → `FrameSequence++`）。静止桌面 DWM 不合成新帧 → 无 `FrameArrived` → `_stashedFrame=null` → `ConsumeBgra` 返回 `null`，`FrameSequence==0`。探测要求 `wgc.FrameSequence > 0` 不满足 → 300ms 后降级 BitBlt。

**结论**：静止起播时探测链固定消耗 **DDA 300ms + WGC 300ms ≈ 600ms + 各次 Thread.Sleep(10) 累积 ≈ 700ms**，最终 BitBlt 出帧。这不是 25 秒的主因，但会叠加在死锁之上。

---

## 2. 编码器首帧行为：NVENC 单帧即出，MF 需多帧

### 2.1 NVENC SDK：首帧即 IDR（无死锁）

`NvencSdkEncoder.EncodeNv12`（:638-704）：

    // :669  首帧 / 显式 RequestKeyFrame 强制 IDR
    bool forceIdr = _firstFrame || _forceIdr;
    pic.encodePicFlags = forceIdr ? (FORCEIDR | OUTPUT_SPSPPS) : 0u;
    // :679  同步 NvEncEncodePicture → 阻塞到完成
    // :682-687  LockBitstream → 拷贝 NAL
    // :692-693  _forceIdr=false; _firstFrame=false;
    return nal.Length > 0 ? nal : null;   // 首帧即返回 IDR

NVENC 同步模式**单帧进单帧出**，首帧即产出 IDR。只要 NVENC 可用，静止起播**不会** 25 秒超时。

### 2.2 MF 软编/硬编：首帧需多帧输入（死锁）

`MfFrameEncoder.EncodeNv12` → `EncodeSoftware`（:314-365）或 `EncodeHardware`（:373-394）：

    // EncodeSoftware
    _transform.ProcessInput(0, sample, 0);      // :333  喂一帧
    // :346  ProcessOutput
    var r = _transform.ProcessOutput(...);
    if (!r.Success) break;                      // 缓冲不足 → NEED_MORE_INPUT → 无输出
    // 返回 null（首帧缓冲期）

`AgentWebServer.cs:413` 注释自证：「软件 MFT 可能先缓冲若干输入帧才产生 IDR」。MF-LOWLATENCY-20261123.md 已实测「微软软 MFT SetValue S_OK 但完全忽略」，低延迟参数无效，软编首帧必须凑够输入帧。

**MF 硬编（异步 MFT）同理**：`EncodeHardware` 里 `WaitForHardwareEvent(...8ms)` 等 HaveOutput，首帧通常无输出返回 `null`，`_hardwareNoOutput++`，要连续喂帧（30 帧或 3 秒）才降级软编。

---

## 3. 死锁机制（精确时序）

**场景**：冷启动（或 Ring 空）+ 画面静止 + NVENC 不可用（降级 MF）。

| 迭代 | forceKeyFrame | 抓屏 | desktopChanged | 去重闸门(:461) | 结果 |
|---|---|---|---|---|---|
| 探测期 ×N 轮（约 700ms） | 第 1 轮已消费归零 | null（探测未完成） | — | — | IdleWait 5ms |
| BitBlt 出帧第 1 帧 | **false**（早被消费） | 有帧 | true（previousBgra==null） | `!false && !true`=false 不跳过 | 喂 1 帧给 MF，MF 返回 null |
| 第 2 帧起 | false | 有帧（静止复用） | **false** | `!false && !false`=**true** 跳过 | **不再喂帧** |

关键点：**`_forceKeyFrame` 在探测期第 1 轮就被 `ConsumeKeyFrameRequest`（:414）消费归零**。等 BitBlt 真正出第一帧时，`forceKeyFrame` 已是 false；这一帧靠 `previousBgra==null → desktopChanged=true` 勉强进入编码，但只喂了 MF **一帧**。MF 需要多帧 → 返回 null；下一帧起静止去重切断输入 → 死锁。

> 补充：即使非冷启动（worker 已在跑、Ring 空），同样成立——`_forceKeyFrame` 只保一帧，MF 多帧需求不被满足。

---

## 4. 首帧延迟精确构成

| 环节 | 耗时量级 | 是否主因 |
|---|---|---|
| DDA 探测降级（静止桌面必然失败） | ~300ms + Sleep(10)×N | 否（但可省） |
| WGC 探测降级（静止桌面必然失败） | ~300ms + Sleep(10)×N | 否（但可省） |
| BitBlt 首帧 | ~几 ms | 否 |
| NVENC SDK 初始化（可用时） | ~30-50ms | 否 |
| NVENC 不可用 → MF 降级 + 首帧缓冲 | **死锁** | **是** |
| WriteStreamAsync 超时上限 | 25000ms（:414） | 表现 |

**预期首帧（修复后）**：NVENC 可用 ≈ 700ms（探测）+ 50ms（init）≈ **<1s**；NVENC 不可用、MF 软编 ≈ 700ms + 多帧喂入（fps 节流下约 3-5 帧 ≈ 100-200ms）≈ **<1.5s**，均 <2s 达标。

---

## 5. 最小修复方案（待 Lead 确认后另派实现）

### P0（核心）：forceKeyFrame 持续到首帧 IDR 真正产出，而非只保一帧

**问题本质**：`_forceKeyFrame` 是「一次性标志」，但「按需 IDR」的语义应是「直到产出 IDR 前，去重不得切断输入」。

方案（三选一，推荐 A）：

- **A（最小改动，推荐）**：在 Loop 编码失败分支，若本帧是 forceKeyFrame 触发的，重新置 `_forceKeyFrame=true`，直到 `nal` 真正产出关键帧才归零。即：
    // :523 附近
    if (nal == null || nal.Length == 0) {
        _noNalFrames++;
        if (forceKeyFrame) { lock (Gate) _forceKeyFrame = true; }  // 下帧继续强制，直到出 IDR
    }
  配合 `:461` 的 `!forceKeyFrame && !desktopChanged`，使静止画面下连续强制喂帧直到 MF 吐出首个 IDR。受 `nextAt`（fps 节流）约束不会超速。

- **B（warmup 门）**：编码器重建后，直到产出第一个 KeyFrame，不执行静止去重（类似 WgcCapture 的 warmup 门，但作用在编码器代数上）。

- **C（去重语义修正）**：把去重条件从「帧字节/序号不变」改为「本帧已编码过且无待服务 viewer」——即「有 viewer 等待首帧时永不跳过」。

### P1（首帧加速）：缩短静止起播的探测链

- DDA 静止桌面永远不出帧，但「DDA 建立成功」（`dda.IsActive`）本身就是有效信号，不应再等 300ms 才降级。可改为：**DDA 一旦 `IsActive` 且 `CaptureBgra` 返回 `_lastBgra==null`（未抓到首帧），立即判定静止、跳过 300ms 探测窗口直接降级**。
- 同理 WGC：静止桌面 `FrameSequence==0` 即可立即降级，不必等 300ms。

### P1（根因加固）：首帧等待不要依赖画面变化

- `WriteStreamAsync` 首帧等待期间，若 worker 处于「编码器已建但无 NAL」状态，应主动再触发一次 `_forceKeyFrame`（而非被动等画面变化），与 P0 形成双保险。

---

## 6. 证据边界

- 本诊断基于当前源码（HEAD `0d62882`）只读推导 + git 历史对照（`d6058f7` 3.3.32 的 IDR 死锁修复、`521f27a` A2 改动），未新增探针、未运行/停止进程、未改代码。
- 「MF 软编需多帧」由 `AgentWebServer.cs:413` 注释 + `MF-LOWLATENCY-20261123.md`（软 MFT 低延迟参数无效）交叉佐证；未在本机实时复现 25 秒超时（无法在只读约束下操作运行中进程）。
- 「DDA/WGC 静止桌面不出帧」由 `AcquireNextFrame` 阻塞语义（`DXGI_ERROR_WAIT_TIMEOUT` 分支）与 WGC 事件驱动（`FrameSequence` 仅 FrameArrived 时递增）源码推导，与 `ENCODER-REBUILD-HANG-20261123.md` 第 3.2 节结论一致。
- NVENC 可用时无此死锁（首帧即 IDR）；死锁仅在「NVENC 不可用 → MF 降级」路径发生。是否复现取决于部署机是否走 MF。
