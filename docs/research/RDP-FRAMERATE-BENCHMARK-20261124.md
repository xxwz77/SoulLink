# RDP 会话真实帧率天花板：DDA、合成与 SoulLink 管线的分离实测

> 日期：2026-11-24；作者：`deep-sunshine`；团队：`team_mwtjn0d77v8gtr`；任务：`task_mwtm6fokiajgrn`。
> 范围：只读产品源码/日志 + 临时测量负载，**没有修改产品代码**。临时程序均在 `%TEMP%\soullink-rdp-framerate` / `C:\Temp\soullink-rdp-framerate`，用交互计划任务投进授权的 **A2 / session 4**。没有操作 `dist/stable/SoulLink-Stable.exe`，没有使用 A1/A4。
> 证据等级：【实测】＝本报告给出的原始计数；【日志实测】＝现有 A2 agent 日志；【推断】＝由对照得出但未做代码级因果剖分的结论。

## 0. 结论先行

1. **A2 RDP session 4 当前桌面真实合成率约为 66 fps。** 最后一组元数据为 `output=1920x1080 refresh=66/1`、`adapter=Intel(R) UHD Graphics`。60 fps 视频、120 fps 视频、硬件/软件 WPF 在无 agent DDA 争用时都收敛到约 66 fps；这不是 14 fps 或 32 fps 的 RDP 硬上限。
2. **同一 SoulLink agent 与独立 DDA 探针并存时，曾经把同一份 WPF/视频压到约 32 fps。** 无争用时同一 WPF 达 65.98 fps。32 fps 是**当前组合路径的可观察瓶颈**，不是 RDP 不可突破的上限。
3. **当前 60 fps 配置的 CPU agent 管线长样本实际为 48.59 fps**（840 帧 / 17.289 秒；逐 30 帧区间平均 48.71 fps，范围 40.49–52.36）。所以当前 60 fps 下每帧平均间隔 20.59ms，比 16.67ms 多约 3.92ms，**仍有 agent 采集/转换/编码/节拍损失**，不能全推给 RDP 合成。
4. **当前本机真实新内容上限就是约 66 fps；配置 120 fps 不会产生 120 fps 的新桌面。** 120 fps 素材实测只有 65.42 个累积桌面帧/秒。60 fps 是合理配置；120 fps 只可能增加重复编码/资源争用。
5. **瓶颈结论**：桌面内容供给上界≈66 fps；当前 60 fps 产品管线≈48.6 fps，主要瓶颈在 SoulLink agent 自身处理/调度。旧的 GPU 零拷贝/PS 路径在配置 120 fps 的日志中仍只有约 32 fps，不能只靠着色器消除会话/多客户端争用。
6. **停止争用后不能自动声称所有场景都可达 66 fps**：该结论是这台机器、这个 1920×1080 RDP session、这些负载的实测上界，不是 RDP 协议的普遍常数；高负载独占全屏游戏仍需另测。

---

## 1. 为什么不能直接拿 `total` 当桌面 fps

本次用两个不同量：

- `AcquireNextFrame` 成功次数：每次成功都可能只是光标变化；
- `AccumulatedFrames` 累计值：桌面合成/提交的帧数，最接近 SoulLink `DxgiDdaCapture.FrameSequence` 的增长。

当前 SoulLink `Relay/DxgiDdaCapture.cs` 在成功取帧后用 `max(1, AccumulatedFrames)` 累加 `FrameSequence`。因此动态桌面下「原始 `AccumulatedFrames` 总和/秒」与「SoulLink `FrameSequence` 增量/秒」同构；但**仅鼠标更新且没有桌面 present 时，SoulLink 的 max(1,…) 也会加一，不能把它叫物理桌面 fps**。

`SessionRecorder` 的 `total` 又是另一层：它是送进编码器并产出的 NAL 数，可能受 fps 限速、CPU 读回、转换、编码耗时和静止保活影响，不是 compositor 帧数。

独立探针不调用 staging、不 `Map`、不读回像素；只循环：

```cpp
// 等价于临时 C# 探针使用的 Vortice 调用
AcquireNextFrame(remainingTimeout, out frameInfo, out resource);
success++;
accumulated += max(0, frameInfo.AccumulatedFrames);
present += frameInfo.LastPresentTime != 0;
mouse   += frameInfo.LastMouseUpdateTime != 0;
shape   += frameInfo.PointerShapeBufferSize > 0;
resource.Dispose();
ReleaseFrame();
```

每帧同时记录 QPC 到达间隔与 `LastPresentTime` 差值。探针构建为 `DdaRateProbe.dll`，0 warning / 0 error；`OpenInputDesktop + SetThreadDesktop` 成功，`D3D11CreateDevice` 返回 `0x00000000`，`DuplicateOutput` 成功。

原始计数及 agent 日志摘录另存 `%TEMP%\soullink-rdp-framerate\evidence.txt`，未写入工作区构建目录。

---

## 2. 分场景 DDA 实测数字

### 2.1 汇总表

| 场景 | 测量时长 | DDA 成功次数 / 秒 | 累积桌面帧 / 秒 | present / mouse / shape | 是否可当作真实桌面速率 |
|---|---:|---:|---:|---|---|
| 基本静止，早期样本 | 15.012s | 2 / 0.133 | 1 / **0.067** | 1 / 1 / 1 | 是；只有首帧，后续主要超时 |
| 120Hz 受控鼠标，早期有 agent | 15.006s | 500 / 33.320 | 480 / **31.987** | 480 / 20 / 12 | 是，含桌面变化；输入频率不是输出 fps |
| 全屏 WPF 默认，早期有 agent | 20.001s | 640 / 31.998 | 640 / **31.998** | 639 / 1 / 1 | 是；agent 同跑场景 |
| 全屏 WPF 强制软件，早期有 agent | 20.005s | 640 / 31.992 | 640 / **31.992** | 639 / 1 / 1 | 是；同上 |
| 全屏 H.264 60fps，早期有 agent | 20.007s | 626 / 31.289 | 637 / **31.839** | 625 / 1 / 1 | 是；同上 |
| 全屏 WPF 默认，无 agent DDA 争用 | 20.007s | 1320 / 65.978 | 1318 / **65.878** | 1317 / 3 / 3 | 是 |
| 全屏 WPF 强制软件，无 agent DDA 争用 | 20.001s | 1320 / 65.997 | 1318 / **65.897** | 1317 / 3 / 3 | 是 |
| 全屏 H.264 60fps，无 agent DDA 争用 | 20.002s | 1320 / 65.993 | 1320 / **65.993** | 1319 / 1 / 1 | 是 |
| 全屏 H.264 120fps，无 agent DDA 争用 | 20.009s | 1311 / 65.519 | 1309 / **65.419** | 1308 / 3 / 3 | 是；>60fps 仍被 66Hz 截断 |
| 120Hz 鼠标，后续后台已有约 66fps 活动 | 15.001s | 1048 / 69.864 | 991 / **66.064** | 990 / 58 / 31 | 桌面≈66；多出的 58 次含鼠标/形状，不归为 present |
| 后台持续更新，无自制负载 | 15.001s | 990 / 65.994 | 990 / **65.994** | 989 / 1 / 1 | 是，但**不是静止**；说明系统后台仍在出帧 |

鼠标负载实际 `SetCursorPos` 频率为 **119.888 / 119.890 Hz**。后一轮后台已有 66fps 的桌面变化，所以不能用 `success-present` 精确归因于某个鼠标输入；只用于证明 DDA 的 Acquire 事件与桌面 present 可分离。

### 2.2 被排除的样本

- 早期 `static-agent-off` 测得 16.654fps，但它紧接视频播放尾部，不是真正静止，故不纳入静止结论。
- 后来静态名 `static-agent-on` 测得 5.148fps，但 `/stream` 请求被现有鉴权返回 401，未建立新观众；尾部播放/重建状态混杂，故不作为 agent 或静止证据。
- 这些数字保留在原始证据文件，**不删除、不挑选性忽略，只明确标为无效基线**。

---

## 3. 显卡与 CPU 渲染后端：本负载没有影响出帧上限

WPF 负载为 1920×1080 最大化窗口、48 个持续移动的矩形，通过 `CompositionTarget.Rendering` 更新位置：

| WPF `RenderOptions.ProcessRenderMode` | WPF 自身回调 | DDA 累积帧 | 争用条件 |
|---|---:|---:|---|
| `Default` | 643 / 20.016s = 32.125fps | 640 / 20.001s = 31.998fps | 有 agent DDA |
| `SoftwareOnly` | 643 / 20.012s = 32.131fps | 640 / 20.005s = 31.992fps | 有 agent DDA |
| `Default`，无争用 | 本组只保存 DDA 原始汇总，WPF 文件被后续 software 场景覆盖 | 1318 / 20.007s = 65.878fps | 无 agent DDA |
| `SoftwareOnly`，无争用 | 同上，未把旧回调数字冒充本组数字 | 1318 / 20.001s = 65.897fps | 无 agent DDA |

**结论【实测】**：在这个 WPF 动画上，软件渲染与默认渲染的 DDA 出帧率差 <0.03fps，可视为相同；瓶颈不是「必须 GPU 绘制才能快」。WPF 的 tier=0 只是当前 RDP 的能力报告，不能代替上述直接测量。

**边界**：不能外推到 DirectX 游戏、独占全屏或更大显存压力。那些内容的生产方式不同，需要按目标游戏复测。

---

## 4. 为什么旧的 14fps / 32fps 结论需要修正

### 4.1 14fps 是历史编码输出，不是合成器上限

旧 `AUDIT-20261124-00-EVIDENCE-BASELINE.md` 用 agent `total` 在约 2 秒内增长 30 帧反推出 14–15fps。它只能证明**当时那条管线的输出速率**，不能直接证明 compositor 只能出 14fps。

### 4.2 32fps 的双客户端对照

相同全屏 WPF：

- 有 agent DDA：640 帧 / 20.001s = **31.998fps**；
- 无 agent DDA：1320 帧 / 20.007s，累计桌面 1318 帧 = **65.878fps**。

相同 60fps 全屏视频：

- 有 agent DDA：累计 637 帧 / 20.007s = **31.839fps**；
- 无 agent DDA：1320 帧 / 20.002s = **65.993fps**。

因此【推断，强对照】约 32fps 与 agent 同时消费 DDA/执行当前采集编码高度相关。可能性包括两个 DDA 客户端的争用、采集 API 使用时序、agent 每帧 CPU/GPU 工作、当前节拍等待；**本任务未做 ETW/驱动级跟踪，不能把它缩成“已证明就是某个 DDA 锁”**。

安全结论不是「第二个 DDA 客户端必然减半」，而是：**在当前 agent 与其测试配置下，出现了约一半的桌面吞吐；实施者应优先修当前 agent 数据路径与并发/锁结构，而不是接受 32fps 为 RDP 限制。**

---

## 5. agent 管线与桌面合成率对照

### 5.1 当前 60fps CPU 管线（最新长样本）

`C:\Users\A2\AppData\Local\Temp\soullink-relay-38088.log`：

- `fps=60`、`1920x1080`、`NVENC-SDK`；
- 抓屏源 `Intel(R) UHD Graphics`，编码器 NVIDIA，`同GPU=false`，零拷贝禁用；
- 03:20:59.087 `total=30` → 03:21:16.376 `total=870`；
- **增量 840 帧 / 17.289s = 48.586fps**；
- 28 个逐 30 帧区间的平均 **48.709fps**，范围 **40.486–52.356fps**；
- 多数日志 `captureMs≈3–15 + convertMs≈7–9 + encodeMs≈3–7`，合计约 13–31ms；这是单段日志的瞬时值，不是整个窗口均值。

与约 66fps 的桌面供给相比，当前 agent 每 60fps 目标帧少约 11.4 个输出/秒；**60fps 配置的实测有效输出只有约 48.6fps**。

### 5.2 早期 30fps CPU 管线

`C:\Users\A2\AppData\Local\Temp\soullink-relay-76220.log`，配置 `fps=30`，零拷贝连续失败 3 次后回退 CPU：

| 日志区间 | total 增量 | 时间 | 输出速率 |
|---|---:|---:|---:|
| 02:44:37.984 → :38.996 | 30 | 1.012s | 29.644fps |
| 02:44:38.996 → :39.982 | 30 | 0.986s | 30.426fps |
| 02:44:39.982 → :40.983 | 30 | 1.001s | 29.970fps |
| 02:44:40.983 → :41.994 | 30 | 1.011s | 29.673fps |

它能接近 30fps 配置，但不证明桌面只有 30fps，也不证明 60fps 时能跟上。

### 5.3 120fps + PS 零拷贝历史样本

`C:\Users\A2\AppData\Local\Temp\soullink-relay-107924.log`：

- 配置 `fps=120`；
- `PS着色器两pass（BGRA→NV12，无 VideoProcessor）`；
- 首帧 PSNR(Y)=52.6dB；
- `total=30` 02:58:01.995 → `total=240` 02:58:08.555；
- 增量 210 帧 / 6.560s = **32.012fps**。

这与当时的独立 DDA 探针约 32fps 一致，说明当时 PS 零拷贝虽消除了 CPU 色彩转换，却没有突破约 32fps 的组合瓶颈。**不要把首帧 PSNR 和 Register/Map 成功等同于高帧率已完成。**

---

## 6. 对 SoulLink 的直接结论与建议

### 6.1 帧率上限与配置

- **本机 A2 RDP 的当前真实内容上限约 66fps**，直接对应 DDA 输出 `refresh=66/1`。
- 60fps 配置不会超过该供给上界，可以保留；120fps 不会增加桌面真实新内容。
- 当前 60fps 的 agent 实际约 48.6fps，应先修处理与节拍，而不是改配置数字。
- 静止桌面可能接近 0 个新 present；这正常。若为了 WebCodecs 保活而重复编码，必须把重复帧单独计数，不能混入真实 FrameSequence/actualFps。

### 6.2 瓶颈判定

| 层 | 本次证据 | 判定 |
|---|---|---|
| RDP 合成/显示供给 | 无 agent 争用时 60/120fps 视频、默认/软件 WPF 均≈66fps | **上界≈66fps** |
| DDA API 本身 | 单独探针无像素读回就能≈66fps | **不是天然只有14/32fps** |
| 当前 agent 采集/转换/编码/节拍组合 | 60fps 长样本≈48.6fps；早期 PS+120fps≈32fps；同跑探针降到≈32fps | **当前主要瓶颈** |
| CPU vs GPU 内容渲染 | 同一 WPF 默认/软件结果相同 | **本负载无影响** |
| 网络/浏览器 | 本任务未测 | **不在本报告归因范围** |

### 6.3 下一步只需验证的三件事

1. 在 agent 内记录原始 `AccumulatedFrames`、仅鼠标帧、真实 present、重复保活帧和编码 NAL，避免继续混用指标。
2. 用一个共享 DDA 生产端向采集/诊断/编码分发，避免同一桌面多个 duplication 客户端互相影响；检查 `AcquireNextFrame` 是否被两个不同 device/context 同时调用。
3. 在 60fps 配置下同步测「FrameSequence 增量、total 增量、capture/convert/encode、队列帧龄」。目标不是宣称 66fps 必达，而是让当前 48.6fps 与桌面 66fps 的缺口可定位。

---

## 7. 复现与证据边界

- DDA 探针 `%TEMP%\soullink-rdp-framerate\DdaRateProbe.cs`，只取 metadata，不读回像素。
- 全屏动画 `%TEMP%\soullink-rdp-framerate\FullscreenAnimation.cs`；强制软件使用 `RenderOptions.ProcessRenderMode=SoftwareOnly`。
- 鼠标 `%TEMP%\soullink-rdp-framerate\MouseMotion.cs`，以约 120Hz 移动系统光标，结束后恢复原位置。
- 视频由本机 ffmpeg 生成：1920×1080 `testsrc2`、H.264/yuv420p，分别 60fps 和 120fps；ffplay 全屏播放。
- 任务名以 `SoulLink-DDA-*-Research` 为前缀，仅用于 A2 / session 4 的临时交互执行；文档收尾时清理。
- 工作区只新增本报告并更新 `docs/research/README.md`；未改产品源文件、未构建产品。
- 本报告未测 A3、未测其他分辨率、未测真实游戏、未测端到端浏览器上屏；这些都不能从 66fps 外推。
