# 编码器频繁重建与重建后产帧中断根因排查

> 日期：2026-11-23
> 性质：只读源码 + 既有日志对照；未改业务代码、未 build、未操作运行进程。
> 证据：`Relay/SessionRecorder.cs`、`Relay/DxgiDdaCapture.cs`、`Services/NvencSdkEncoder.cs`、`Relay/AgentWebServer.cs`、`C:\Users\A2\AppData\Local\Temp\soullink-relay-50560.log`。

## 0. 结论先行

1. **DDA ACCESS_LOST 不是编码器频繁重建的直接主因。** 它只调用 `DxgiDdaCapture.Rebuild`，不会调用 `CreateEncoder`、不会 Dispose 编码器。
2. **编码器频繁重建有两条直接触发源：**
   - 参数版本变化（滑杆/网页热更新）——明确重建，且每收到不同参数一次就重建；
   - 新 viewer 订阅 `_forceKeyFrame=true`，MF 分支通过“销毁旧编码器→新首帧 IDR”实现重同步；前端反复重连会放大它。NVENC 分支只设 FORCEIDR，不重建。
   另有显式抓屏热切和实际分辨率变化，属于必要重建。
3. **total 8~65 秒无日志的直接原因不是 NVENC 初始化，而是静止帧去重。** `DxgiDdaCapture` 无新帧时复用上一张 BGRA，`SequenceEqual` 命中后直接 continue，不编码；total 只在第 30 个成功 NAL 时打印，静止时没有新 NAL，就没有 total。
4. **重建后仍可能长期不恢复，核心是“新 viewer 需要 IDR”和“静止帧去重”冲突。** 新 viewer 在 Ring 为空时没有 `initial`，等待最多 25 秒；若画面一直静止，编码循环跳过帧，IDR 请求也一直不执行，最终 503/客户端超时。viewer 断开后 Unsubscribe，前端重连又触发同样路径。
5. 旧 bug “Loop finally Dispose 静态 Capture”在当前代码已修；本日志覆盖的 DDA duplication 是有效重建。不能把本报告与已修静态 disposed 问题混为一谈。

## 1. 所有编码器重建触发源与主因排序

| 触发源 | 当前代码路径 | 日志证据 / 主因判断 |
|---|---|---|
| 参数热更新 | `SessionRecorder.Loop:377-388`：`_videoParameterVersion != appliedVersion` → `encoder.Dispose` → `CreateEncoder`。relay `videoParams` 来自网页和主程序滑杆 | **明确的主动重建源。** 17:32:29 25→16Mbps、17:32:32 16→12Mbps、17:32:34 60→45fps、17:34:05 VBV 70→0%，每次均记录 `参数热更新` 与新的 `编码器=NVENC-SDK`。初始化约 26~54ms，不足以解释 8~65s 无 total。 |
| 新 viewer 请求 IDR | `TrySubscribe`（`:495-514`）设 `_forceKeyFrame=true`；`ConsumeKeyFrameRequest`（`:405-419`）NVENC 只 FORCEIDR，MF Dispose 重建 | **MF 频繁重连时是直接重建源。** 17:36:50、17:37:12、17:37:21 等仅“尝试/编码器=”没有参数热更新，且紧邻客户端断开，符合订阅重建。当前日志的 NVENC 本身不因此重建。 |
| 抓屏后端热切 | `captureParams` 后 worker `:363-375` Dispose Capture + CreateCapture，并 Dispose encoder 重置 frameIndex | **必要重建，不是意外主因。** 17:35:21 DDA→WGC、17:35:31 WGC→DDA，尺寸变化时也需重建。 |
| 分辨率改变 | `:428-437`：ew/eh 变化 → CreateEncoder | 合理；WGC 热切后 1920x1032 与 DDA 1920x1080 不同。 |
| DDA ACCESS_LOST | `DxgiDdaCapture.cs:199-219` → `Rebuild()`（`:233-249`）只重建 duplication | **不是编码器重建源。** 17:33:02.992 ACCESS_LOST → 17:33:03.012 DDA 建立成功，周围没有编码器 Dispose/CreateEncoder。 |
| 首次启动/尺寸初始化 | `encoder == null` → CreateEncoder | 正常。 |
| 无观众生命周期停止后重订阅 | Loop finally 释放 encoder，但保留 Capture；新订阅新建 encoder | 合理生命周期；与 `ACCESS_LOST` 不同。 |

## 2. 日志时间线：重建不是秒级中断的充分解释

| 时间 | 现象 | 推断 |
|---|---|---|
| 17:32:29~34 | 连续 3 次参数热更新 + NVENC 重建；每次初始化几十毫秒 | 参数滑杆/网页确实会反复重建，但很快结束。 |
| 17:32:50~17:33:01 | total 30/60/90，17:33:02.992 DDA ACCESS_LOST，17:33:03.012 duplication 成功 | DDA 重建约 20ms，没有对应编码器重建；不是 long stall 的证据。 |
| 17:35:21~31 | 热切 DDA↔WGC，随后 total 120→240 | 热切与尺寸重建后能恢复产帧，不能把热切本身视作永久中断根因。 |
| 17:36:28 断开 → 17:36:50 新编码器 → 17:36:52 total 810 | 新订阅可再次产帧，但 viewer 在静止/等待 IDR 时可能拿不到 initial。 |
| 17:37:12~23、17:37:59~17:38:08 | 多轮新订阅/新编码器 + total 增长 | 新编码器确实在工作；卡顿发生在 NAL 供给与 viewer 首帧同步，不是 NVENC 永远不恢复。 |
| 17:39:00~17:41:24 | 长段无 total，后续热切后仍仅少量 NAL | 与静止复用/IDR 等待一致；本日志没有新 error 或长 NVENC 初始化证据。 |

## 3. 为什么 total 会中断

### 3.1 total 是编码计数，不是循环心跳

`SessionRecorder.Loop` 只有 `nal != null && nal.Length > 0` 才 `frameIndex++`、`_totalFrames++`，且只在 `total % 30 == 0` 打日志（`:446-458`）。所以：

- 屏幕静止 → 没有新 NAL → total 停止，但 worker 可能正常活着；
- `EncodeNv12` 返回 null → total 停止；
- 全部观众离开 → worker 停止，total 停止；
- 异常进入 error → total 停止。

因此**必须用独立 heartbeat / loopIteration / lastCaptureAt / lastEncodeAt / lastNalAt / 连续无 NAL 计数**才能区分“静止去重”与“卡死”。现有 total 不能承担活性探针。

### 3.2 DDA 静止复用 + 全帧 SequenceEqual 是日志中断的直接来源

`DxgiDdaCapture.CaptureBgra` 在 `DXGI_ERROR_WAIT_TIMEOUT` 时返回 `_lastBgra`（`:193-197`），并非 null。Loop 比较整张 BGRA（`:420-426`）相等就跳过：

- 编码器不被调用；
- total 不增长；
- 有新 viewer 需要 IDR 时，尽管 `_forceKeyFrame` 已置位，`ConsumeKeyFrameRequest` 仍在去重 continue 之前，因此可能 RequestKeyFrame，但后续 `EncodeNv12` 仍被跳过，**请求被消费了却没有出 IDR**。

这正好解释“画面没变化，日志几十秒不更新”与“新观众仍起不来”同时发生。

## 4. 为什么重建后产帧不恢复

### 4.1 首帧等待没有最近 IDR 可用

`TrySubscribe` 在 Ring 为空时返回 `initial=null`（`:501-512`），`WriteStreamAsync` 最长等 25 秒（`AgentWebServer.cs:410-425`）。新编码器若因静止去重不出 NAL，新 viewer 一直拿不到 initial，然后 503 或客户端断线；Unsubscribe 后再次重复。

**这是重建后“看起来永远不恢复”的关键闭环，不需要假设 NVENC 自身失效。**

### 4.2 Ring 最后 IDR 会被静止期间的时间淘汰

有 viewer 时循环只在出 NAL 时更新 Ring/裁剪（`:449-469`）。静止后 30 秒无新帧，新 viewer 看到的是旧 Ring；首帧策略依赖 Ring 内容而非按需真正再编码，存在取不到关键帧/过期数据的边界。

### 4.3 error 没有恢复入口

`CreateEncoder` 或 `EncodeNv12` 异常后，Loop 进入 catch，`_status="error"`、`_worker=null`（`:477-492`）。`TrySubscribe` 遇到 `_lastError` 且 `_status=="error"` 立即拒绝（`:499-500`），不会清理错误并重启 worker。日志未显示这次发生，但这是**真实的不自恢复分支**，应一并修复，而不是只盯着 ACCESS_LOST。

### 4.4 WGC 黑帧过滤也可能让 initial 永远没有

当前 `WgcCapture.IsMostlyBlack` 返回 null（约 `:252-267`），17:41:21 日志出现黑帧丢弃。如果后续持续黑帧，自动探测强制 WGC 可能一直无 initial；这是另一条抓帧供给不足路径，不是编码器重建错误。

## 5. DDA ACCESS_LOST 与编码器的真实联动

`ACCESS_LOST` 是 DXGI 桌面复制会话失效，典型于显示模式/独占全屏变化。当前只应重建 DDA 资源：

```
AcquireNextFrame → ACCESS_LOST → DxgiDdaCapture.Rebuild
                                  → Dispose duplication → Start
```

**它不会也不应直接导致编码器重建。** 只有以下间接情况需要重建编码器：
1. 用户随后显式切换 CaptureMode（worker 热切）；
2. 自动模式探测确认 DDA 持续不可用并切 WGC；
3. 重建后实际分辨率 ew/eh 改变；
4. 新 viewer/新参数独立的编码器重建条件。

当前 17:33 ACCESS_LOST 后 20ms 成功，后续无 total 不能归因于它。

## 6. 修复方案（待 Lead 确认后再改）

### P0：重建后一定可自恢复

1. **按需 IDR 优先于静止去重**：若 `_forceKeyFrame` 或有待起播 viewer，必须对这一帧执行 Encode，不得 `SequenceEqual` continue；NVENC 直接 FORCEIDR，MF 才在确有必要时重建。
2. **新 viewer 立即给最近 IDR**：不要 Ring 为空就等 25s。若 Ring 有历史 IDR，直接 initial 返回最近 IDR；若确实无 Ring，触发按需 IDR 并在 worker 产出后唤醒，不依赖画面变化。
3. **异常自动重启/降级**：编码器初始化/编码失败进入可重试状态，下一次订阅或有需求时清错误并重建；NVENC→MF 软编的既有降级不能被 error 锁死。
4. **worker 级 watchdog**：记录 lastCapture/lastEncode/lastNal 与连续无 NAL；若抓帧正常但编码器连续 N 次无 NAL/异常，则重建编码器，不能让新 viewer 永远等。

### P1：减少不必要重建

1. 滑杆值未变化不递增版本；当前已有 record 相等判断，保留。
2. **滑杆 drag 期间合并参数更新**：150~300ms debounce 或拖动结束后一次提交，避免 25→16→12→... 每次都完整创建 NVENC session；最终应用仍 worker 执行。
3. **参数分类**：fps 变化可以更新节拍，不必在所有情况下重建编码器；码率/preset/VBV 变化仍需 NVENC 重建。新 viewer 的 IDR 不应驱动 MF 反复重建，可考虑 MF 的 flush/GOP 或复用最近 IDR。
4. **ACCESS_LOST 独立处理**：只重建 DDA，不要新增编码器重建调用；重建成功继续用当前编码器，尺寸未变不动。
5. **探测只执行一次**：用后端 generation/ready 标志锁定成功结果，热切/降级才重置；每帧不再建 Stopwatch、不再刷日志，避免其掩盖真实编码器重建频率。

### P1：诊断与验收

- 加 `loopSeq/captureSeq/nalSeq`、lastCaptureAt/lastEncodeAt、encoderGeneration 与触发原因；
- 分开记录“参数重建 / 新 viewer IDR / 热切 / 尺寸 / 错误重试”；
- 静止画面 60s 后新订阅立即得到 IDR；
- 连续参数拖动最终只重建一次且 viewer HTTP 不断；
- DDA ACCESS_LOST 人工触发后尺寸不变时 encoderGeneration 不变；
- 抓帧正常但编码异常时可自动恢复，不出现永久 error。

## 7. 证据边界

- 本报告未直接观测编码器对象内部状态，仅基于当前源码和指定日志的对应关系；未新增探针、未停止或启动进程。
- total 中断可解释，但不能据此排除所有网络/客户端因素；HTTP 断开 64/1229 是结果，不是编码器重建的直接证据。
