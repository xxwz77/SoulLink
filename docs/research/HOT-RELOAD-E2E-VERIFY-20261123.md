# 视频参数热更新 + 生命周期 · 真机端到端验收

> 日期：2026-11-23
> 作者：verifier（latency-fix 团队 / task_mwsvyh2lssdo5l）
> 相关：HANDOFF-20261123-LATENCY.md（铁律）、LATENCY-FIX-VERIFY-20261123.md（上轮验收）、
>   NVENC-SDK-RCPARAMS-20261123.md、NVENC-SDK-QUALITY-PRESETS-20261123.md（热更新实现）
> 结论：**热更新码率/帧率实时生效 ✅，生命周期 ✅；preset/vbv 热更新 ❌ 不生效（代码缺陷）。**

---

## 0. 验收结论

| 验收项 | 判据 | 结果 |
|---|---|---|
| ① 换新代码 | 精确 PID + 熔断，不碰 A1/A4/主程序/Stable/Broker | ⚠️ 改为隔离验证（见 §1 决策） |
| ② 热更新实时生效 | 码率/preset/vbv/帧率变化 + viewer 不断 | ⚠️ 码率/帧率 ✅；preset/vbv ❌ |
| ③ 生命周期 | 无人降帧率 / 无直播无录像暂停释放 | ✅ 通过 |
| ④ 约束遵守 | 不碰 A1/A4/主程序/Stable/Broker | ✅ 全程遵守 |

**一句话：热更新的「码率 / 帧率」两个参数隔离实测实时生效、viewer 不重连；但「preset / vbv」两个参数是空转——UpdateVideoParameters 记录了值、日志也打印了新值，但编码器 NvencSdkEncoder 只从环境变量读 preset/vbv，进程启动后环境变量不变，热更新值被丢弃。生命周期（无直播无录像→暂停+释放）实测通过。**

---

## 1. 关于「重启 A2 换新代码」的决策（重要，如实记录）

任务要求「重启 A2(PID 91664) 换新代码」。静态排查后发现**部署矛盾**，未擅自执行，改为隔离验证：

- 新代码版本已升到 **3.3.24**（SoulLink.csproj），而运行中的 Broker(68156)/A1/A4/A2 全部是 **3.3.20**。
- BrokerInstall.ValidAgent 校验：Program Files 里 SoulLink.exe（Broker 服务本体）与
  SoulLink.Agent.exe 的 **FileVersion 必须一致**。换 A2 的 agent 到 3.3.24，就必须同时把
  SoulLink.exe（Broker）也换成 3.3.24 → 必然要重启 Broker 服务(68156)。
- 这与验收④「不碰 Broker 68156」**直接冲突**。

**因此**：真正的「A2 换新代码」需要 Lead 决策（是否允许连带升级+重启 Broker，还是版本先回退/另寻路径）。
本轮采用**隔离验证**：新 build(3.3.24) 以独立 --relay 管道 + 自建宿主管道桩在 session1 起，
只读抓屏、精确 PID、挂熔断，**零生产进程接触**，等价验证了热更新与生命周期的运行时行为
（这些行为与会话无关，隔离即可定性）。

---

## 2. 热更新验证（验收②）

### 2.1 方法

- 自建宿主管道桩（临时 C# 工具，已删）创建命名管道，spawn 新 build agent，发 videoparams 命令。
- 通过 recordstatus 读取 SessionRecorder.StatusText() 的实时字段（bitrateKbps/liveFps/recordFps/actualFps 等）。
- agent 日志 soullink-relay-<pid>.log 记录「参数热更新」与编码器重建。

### 2.2 实测数据（隔离，2560×1440，NVENC SDK 编码器）

| 时间点 | 动作 | bitrateKbps | liveFps | recordFps | currentFps | pipelineRunning |
|---|---|---|---|---|---|---|
| STATUS_0 | 初始 record 15fps | 8000 | 30 | 5 | 5 | 1 |
| videoparams | bitrate→15000, stream→60, record→10, preset→7, vbv→100 | — | — | — | — | — |
| STATUS_1 | 热更新后 | **15000** | **60** | **10** | **10** | 1（未重连） |
| STATUS_2 | 停录像后 | 15000 | 60 | 10 | 10 | 0（暂停） |

日志确认（soullink-relay-14808.log）：

```
14:48:57.506 [video] 参数热更新：bitrate=8000kbps preset=P3 vbv=+0% stream=30fps record=5fps
                    → bitrate=15000kbps preset=P7 vbv=+100% stream=60fps record=10fps
14:48:57.582 [video] 尝试 NVENC SDK 直通编码器
14:48:57.734 [video] 编码器=NVENC-SDK 配置码率=15000kbps MFT读回=15000kbps fps=10
```

### 2.3 结论

- **码率 ✅**：8000→15000 kbps，编码器重建后 配置码率=15000、mftBitrateKbps=15000，实时生效。
- **帧率 ✅**：streamFps 30→60、recordFps 5→10，STATUS_1 的 liveFps/recordFps/currentFps 全部更新，实时生效。
- **viewer 不断 ✅**：热更新期间 pipelineRunning 保持 1，total 帧计数从 12 连续递增到 42，
  未重建 agent、未重连 viewer（SessionRecorder 只替换编码器，保留 Viewer 字典与 Pending 队列）。
- **preset ❌ / vbv ❌**：见 §4 缺陷。

---

## 3. 生命周期验证（验收③）

### 3.1 无直播无录像 → 暂停 + 释放

实测 STATUS_2（发 record stop 且无 viewer 后）：

```
stopped|fps=10|frames=0|bytes=0|total=44|pipelineRunning=0|recording=0|actualFps=0.0
```

- pipelineRunning=0：视频线程已停止。
- frames=0 / bytes=0：环形缓冲已清空释放（Loop() 的 finally 里 Ring.Clear()）。
- recording=0：录像标记清除。

**结论 ✅**：无直播且无录像时管线暂停、编码器与 Ring 释放，达成「无人 0 消耗」。

### 3.2 无人观看降录像帧率

- 录像状态下 currentFps=5（= RecordFps），而非 liveFps=30，符合「无观众 → RecordFps」。
- 热更新 recordFps 5→10 后，currentFps=10 实时跟随。

**结论 ✅**：无人观看时按 RecordFps 运行，热更新后跟随新值。

---

## 4. 缺陷：preset / vbv 热更新不生效（需修复）

### 4.1 现象

日志 参数热更新 … preset=P7 vbv=+100% 打印了新值，但编码器实际仍用 **P3 / vbv 0%**。

### 4.2 根因（代码级）

Relay/SessionRecorder.cs：

- UpdateVideoParameters 把 5 个参数存进 _videoParameters（bitrate/preset/vbv/stream/record）。
- 但 CreateEncoder(ew, eh, fps) → TryInitializeCandidate → candidate.TryInitialize(ew, eh, fps, StreamBitrate)
  **只传 bitrate 和 fps**，preset/vbv **没有传给编码器**。

Services/NvencSdkEncoder.cs：

- TryInitialize 里的 _preset / _vbvIncreasePercent 从**环境变量**
  SOULLINK_NVENC_PRESET / SOULLINK_VBV_INCREASE_PERCENT 惰性读取（:488-489）。
- 这些环境变量由 RelayClient.CaptureEnv 在**投送时**下发，进程启动后**不再改变**。
- 所以热更新 videoparams 传的 preset/vbv 存进了 _videoParameters 但**从未到达编码器**。

### 4.3 修复建议（给 stream-core / gpu-zero-copy）

1. IFrameH264Encoder.TryInitialize 增加 preset/vbv 参数（或增加 UpdateParameters(bitrate,preset,vbv,fps) 方法）；
2. NvencSdkEncoder 支持运行时改 preset/vbv（preset 改变需重建编码会话；vbv 改变可用
   NvEncReconfigureEncoder 或重建）；
3. CreateEncoder / TryInitializeCandidate 把 _videoParameters.Preset/Vbv 传下去。

> 注：preset 改变必须重建 NVENC 会话（GetEncodePresetConfigEx 用不同 GUID），
>   vbv 改变可走重配置或重建。当前代码两者都只读环境变量，热更新语义未接通。

---

## 5. 约束遵守（验收④ + 全程）

- A1(33372) / A4(41672) / A2(91664) / 主程序(5080) / SoulLink-Stable(13116) / Broker(68156) **全程未碰**，
  实测后确认全部存活。
- 隔离测试 agent 由探针 spawn 后 exit 命令正常退出 + 兜底 Kill，无遗留进程。
- 全程精确 PID（探针记录 AGENT_PID=14808，退出后确认无残留）。
- 内存熔断：隔离 agent 运行 <10 秒、私有内存远低于 2GB，未触发。
- 未提交 git。临时探针工具目录已删除（工作区整洁规则）。

---

## 6. 待 Lead 决策 / 后续

1. **A2 真机换新代码的部署路径**：3.3.24 需要连带升级+重启 Broker（与「不碰 Broker」冲突），
   请 Lead 决策：允许连带升级 Broker，或先回退版本/另寻部署路径。
2. **preset/vbv 热更新缺陷**：需 stream-core/gpu-zero-copy 修复（§4.3），修复后复测。
3. 真机 A2 上「用户拖滑杆 → 网页下拉 → 主程序 → 代理」的完整 UI 链路，需在部署路径确定后
   用真实 A2 会话复测（隔离验证已证明底层热更新行为，但 UI→配置→代理 的贯通未测）。

---

## 7. 可复用结论

- 热更新机制本身正确：videoparams → UpdateVideoParameters → worker 线程内替换编码器、
  保留 Viewer/Pending 不重连，码率/帧率两个参数已贯通。
- 隔离验证法（自建管道桩 + spawn agent + recordstatus 读状态）可完全离线复现热更新/生命周期，
  无需动生产进程，是后续复测的标准姿势。