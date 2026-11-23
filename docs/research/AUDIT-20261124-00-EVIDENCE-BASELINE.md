# 取证与测量报告：444ms 拆段实测 + GPU 零拷贝为何未生效

> 日期：2026-11-24
> 作者：builder-repro（dsh-team `team_mwtjn0d77v8gtr` / member `m_mwtjndnz7w43gl` / task `task_mwtjoc1186ktkw`）
> 性质：**只读取证**。未改任何产品代码/脚本/配置；未停进程、未重启程序、未碰 dist\stable\、未用 A1/A4 账号、未发布、未改 latest.json。
> 证据等级：【实测】= 真机日志/前序任务复跑输出；【代码事实】= 当前工作区源码逐行可证；【git 事实】= git 历史可证；【估算】= 基于证据的推断，未单独实测，明确不猜。
> 核心取证对象：真机日志 `C:\Users\A2\AppData\Local\Temp\soullink-relay-78240.log`（PID 78240 = 3.3.34 测试 agent，session 4，DDA 后端，2026-11-24 01:17~01:25）。

---

## 0. 结论摘要（先回答三个验收问题）

1. **444ms 里，agent 侧「抓屏+转换+编码」只占 ~36ms**（captureMs≈25 + convertMs≈8 + encodeMs≈3，真机日志逐行实测）。剩余 ~408ms 不在 agent 的 CPU 路径里，而在「有效帧率只有 ~14fps（RDP 慢合成导致 DDA 频繁 WAIT_TIMEOUT）+ 浏览器解码上屏 + 前端反复断流重连」这三段。**因此「GPU 零拷贝」即使 100% 生效，也只省掉 ~36ms 里的 CPU 读回+转换部分（约 30ms），它根本不是 444ms 的根因。**

2. **GPU 零拷贝代码路径已存在但从未被走到（从未产出一帧 NAL）**。判定是**硬证据**而非猜测：agent 日志 78240 里编码器确为 `NVENC-SDK`，但全文 **0 条**「零拷贝 / PSNR / GPU 转换器 / 降级 CPU」诊断行，且 CPU 路径指标（captureMs/convertMs/encodeMs）持续打印。代码里零拷贝分支入口是 `SessionRecorder.cs:434` 的 `if (gpuConverter != null && encoder is NvencSdkEncoder)`，其成功/异常都有日志、唯独**静默 fall-through 无日志**——证据指向 DDA 后端在 `DxgiDdaCapture.TryCaptureBgraTexture`（跨设备共享句柄）这一段静默返回 false。

3. **「GPU 零拷贝部署后回到 444」的因果被误读了**：零拷贝从未生效，等于「什么都没变」。340→444 的回归另有原因（A2 节流改动 + 前端重连循环 + RDP 慢合成下 60fps 配置名存实亡），**不是零拷贝变慢**。见 §4。

---

## 1. 延迟拆段表（每段标注来源 + 实测/估算）

### 1.1 当前真机（3.3.34，DDA 后端，fps=60，1920×1080）agent 内部逐段【实测】

来源：`soullink-relay-78240.log` 的 `[video] 共享管线 total=...` 行（该行由 CPU 路径 `SessionRecorder.cs:612-617` 打印，字段 `captureMs/convertMs/encodeMs` 即 `SessionRecorder.cs:610` 写入的三段耗时）。

| 段 | 实测值 | 证据（文件:行号） | 等级 |
|---|---|---|---|
| 抓屏读回 captureMs | **8~34ms，均值约 25ms**（DDA `AcquireNextFrame(16)` + staging+Map+Marshal.Copy 读回 1920×1080 BGRA 8MB） | 日志行 19~71；`DxgiDdaCapture.cs:176,185,337-380` | 实测 |
| CPU 色彩转换 convertMs | **6~10ms**（纯 C# 逐像素 `BgraToNv12`） | 日志行 19~71；`SessionRecorder.cs:849-878` | 实测 |
| NVENC 编码 encodeMs | **2~5ms**（`NvencSdkEncoder.EncodeNv12` 同步，含每帧 `Marshal.AllocHGlobal` + `UpdateSubresource` 3MB 上传） | 日志行 19~71；`NvencSdkEncoder.cs:649-654` | 实测 |
| **agent CPU 路径小计** | **≈36ms**（25+8+3） | 上三行求和 | 实测 |

### 1.2 端到端 444ms / 历史 340ms【实测（用户口径）】

| 值 | 出处 | 口径 |
|---|---|---|
| 444ms | `HANDOFF-VIDEO-LATENCY-GPU-20261124.md:43`（2026-11-24 01:20，DDA 后端，3.3.34） | 北京时间对照法（本机 vs 远程桌面同框两时钟差值） |
| 340ms | 同文档 `:11/:43`（A1/A2 优化后） | 同上 |

### 1.3 其余 ~408ms 去哪了（逐段拆，标注实测/估算）

| 段 | 估计值 | 证据 | 等级 |
|---|---|---|---|
| 有效帧率不足（DDA 在 RDP 内实际只 ~14fps 而非 60fps） | 每帧间隔从 16.7ms 拉长到 ~70ms，等效额外 ~50ms/帧 | 日志：`total=30@44.624 → total=60@46.601` 30 帧耗时 1.98s = **15.2fps**；`total=90@48.718` 30 帧耗时 2.12s = **14.2fps** | 实测（日志帧间隔反推） |
| 静止误判跳帧（`staticSkipped` 持续增长） | 变化（RDP 慢合成下动画面被判静止而跳帧） | 日志 `staticSkipped` 3 分钟内 0→199（行 19→104）；机制 `SessionRecorder.cs:545-563` + `DxgiDdaCapture.cs:209-214` | 证据实测，延迟量级【估算】 |
| 浏览器 WebCodecs 解码 + 合成上屏 | 50~100ms | `AGENT-PIPELINE-LATENCY-GAP-20261123.md` H3（§6 表） | 估算（未实测） |
| 前端反复断流重连（每次重连=等按需 IDR + 解码重启） | 变化（数百 ms 级） | 日志 13、72、83、108… 共 12+ 处 `[web] [stream] 客户端断开 frames=N`；机制 `web-index.html` 看门狗 + `SessionRecorder.cs:714-718 RequestKeyFrame` | 证据实测，延迟量级【估算】 |
| 节拍等待（`WaitUntilNextSlot` @60fps） | 0~16.6ms/帧 | `SessionRecorder.cs:739-746` | 代码事实，未单独实测 |

### 1.4 关键判定：agent 侧不是 444ms 的瓶颈

- agent 内三段合计 ~36ms，即便 GPU 零拷贝完全生效，也只能砍掉其中「读回 25ms + 转换 8ms」≈33ms，端到端仍会 >400ms。
- 444ms 的大头是 **RDP 会话内 DDA 实际出帧率只有 ~14fps（60fps 配置名存实亡）+ 浏览器侧 + 前端断流重连**。
- 这条结论直接推翻了「GPU 零拷贝是延迟根因 / 零拷贝部署导致回到 444」的叙事，应作为后续 P0 排序的依据。

---

## 2. GPU 零拷贝未生效的判定（函数名+行号+提前 return 条件，非猜测）

### 2.1 硬证据：日志证明零拷贝从未产出一帧 NAL

- 编码器确为 `NVENC-SDK`：【实测】`soullink-relay-78240.log:11` `[video] 编码器=NVENC-SDK 配置码率=18000kbps ... fps=60 尺寸=1920x1080`。
- 但全程 **0 条**以下任一诊断行（对 `C:\Users\A2\...\Temp\soullink-relay-*.log` 与 `C:\Users\91244\...\Temp\soullink-relay-*.log` 全量 grep `零拷贝|PSNR|GPU 转换器|降级 CPU|ConvertFromSharedHandle|EncodeD3D11Texture`，命中 0）：
  - `[video] 零拷贝色差 PSNR(Y) = ...` —— 零拷贝成功的唯一日志（`SessionRecorder.cs:489`）
  - `[video] GPU 转换器不可用，零拷贝降级 CPU：...` —— 转换器初始化失败（`SessionRecorder.cs:586`）
  - `[video] 零拷贝失败，降级 CPU：...` —— 运行时异常（`SessionRecorder.cs:505`）
- 同时 CPU 路径的 `[video] 共享管线 total=... captureMs=XX convertMs=XX encodeMs=XX` 持续打印（日志行 19~71）——**证明 CPU 读回路径一直在跑**。

### 2.2 代码路径（已存在）与入口条件

`Relay/SessionRecorder.cs`：

```
:434   if (gpuConverter != null && encoder is NvencSdkEncoder nvencZero)   ← 零拷贝分支唯一入口
:439     if (Capture.TryCaptureBgraTexture(out sharedHandle, ...) && sharedHandle != IntPtr.Zero && ...)   ← 拿 GPU 纹理共享句柄
:450       if (gpuConverter.ConvertFromSharedHandle(sharedHandle, ...) && nv12Tex != null)                  ← GPU BGRA→NV12
:452         zNal = nvencZero.EncodeD3D11Texture(nv12Tex, ...)                                               ← 纹理直通编码
:453         if (zNal != null && zNal.Length > 0) { ... zeroHandled = true; }                                ← 成功产出 NAL
:508   if (zeroHandled) { ...continue; }                                                                      ← 只有成功才 continue
:513   // zeroHandled=false：静止/无帧/转换失败/编码无输出 → 落到下方 CPU 路径兜底（静默，无日志）
```

`gpuConverter` 只在编码器首次创建时赋值（`SessionRecorder.cs:580-589`）：

```
:580   if (encoder is NvencSdkEncoder nvencEnc && nvencEnc.Device != null && nvencEnc.Context != null)
:584     var conv = new GpuColorConverter(nvencEnc.Device, nvencEnc.Context);
:585     if (conv.TryInitialize(ew, eh)) { gpuConverter = conv; }         ← 成功才非 null（静默）
:586     else { Log("GPU 转换器不可用..."); conv.Dispose(); }             ← 失败会打日志
```

### 2.3 提前 return 的候选点（哪一条命中 = 未验证，但可穷举到行号）

**结论：日志里既没有 :586 的「转换器不可用」、也没有 :505 的「零拷贝失败」异常，说明 `gpuConverter` 大概率创建成功（`TryInitialize` 返回 true 是静默的），零拷贝是**进了分支但静默 fall-through**。静默返回点穷举如下（三者都没有日志，是唯一的无日志路径）：**

**(A) DDA 拿不到共享句柄 —— `DxgiDdaCapture.TryCaptureBgraTexture`（`DxgiDdaCapture.cs:253-314`）**

```
:264   AcquireNextFrame(16, ...)
:265   if (!hr.Success || resource == null) return false;   ← WAIT_TIMEOUT（RDP 静止/慢合成，本日志实测高频发生）或失败
:296   sharedHandle = dxgiRes.SharedHandle;                  ← IDXGIResource::GetSharedHandle（旧式非 NT 句柄）
:297   if (sharedHandle == IntPtr.Zero) return false;        ← Shared 句柄拿不到 → 回退 CPU
```

**(B) 转换器跨设备打开/转换失败 —— `GpuColorConverter.ConvertFromSharedHandle`（`GpuColorConverter.cs:178-215`）**

```
:181   if (_disposed || sharedHandle == IntPtr.Zero || ...) return false;
:182   if (!IsAvailable) return false;
:186   if (w != _width || h != _height) { if (!TryInitialize(w, h)) return false; }
:191   var sharedTex = _device.OpenSharedResource<ID3D11Texture2D>(sharedHandle);   ← 跨设备打开共享纹理
:192   if (sharedTex == null) return false;
:205   if (localBgra == null) return false;
```

**(C) 纹理直通编码返回 null —— `NvencSdkEncoder.EncodeD3D11Texture`（`NvencSdkEncoder.cs:717-722`）**

```
:719   if (_disposed || _enc == IntPtr.Zero || _nv12Tex == null || _context == null) return null;
:720   if (_mapRes == null || _encodePic == null || _lockBs == null) return null;
:721   if (!ReferenceEquals(nv12Tex, _nv12Tex)) throw ...   ← 此异常会走 :505 打日志（未见），故排除
```

**(D) 转换器初始化失败（若发生，会打日志，但日志未见，故基本排除）—— `GpuColorConverter.TryInitialize`（`GpuColorConverter.cs:50-121`）**

```
:52   if (_disposed) return false;
:58   if (w <= 0 || h <= 0) return false;
:67   if (_videoDevice == null) { LastError = "QueryInterface ID3D11VideoDevice 失败"; return false; }   ← RDP 内最可疑，但会经 :586 打日志（未见）
:69   if (_videoContext == null) { ... return false; }
:85   if (_enumerator == null)   { ... return false; }
:89   if (_processor == null)    { ... return false; }
:102  if (_nv12Texture == null) { ... return false; }
:110  if (_outputView == null)  { ... return false; }
```

### 2.4 为什么最可能是 (A) 或 (B)，而不是转换器没建起来

- `NvencSdkEncoder.Device/Context` 是 `TryInitialize` 成功后 `:516` 赋值的非空字段（`NvencSdkEncoder.cs:470-471, 516`），而日志 :11 证明 NVENC-SDK 初始化成功 → `SessionRecorder.cs:580` 条件为真 → `GpuColorConverter.TryInitialize` 被调用。
- `TryInitialize` 失败必打 `:586` 日志（未见）→ 转换器大概率建成（`gpuConverter != null`）。
- 于是零拷贝分支 `:434` 每轮都被进入，但 `:439` 的 `TryCaptureBgraTexture` 或 `:450` 的 `ConvertFromSharedHandle` 静默返回 false → `zeroHandled` 恒 false → 每轮 fall through 到 CPU。
- **佐证**：`DDA-TEXTURE-SHARED-HANDLE-20261124.md`（§0/§4.3）已用 MSDN 原文指出：DDA 跨设备共享用 `GetSharedHandle`（非 NT 句柄）+ 无 `Flush`/无 `IDXGIKeyedMutex` 是脆弱路径，Sunshine 正确做法是「DDA duplication 建在编码设备上（同 device，免共享）」——与本代码（`DxgiDdaCapture` 自建设备 + `NvencSdkEncoder` 自建设备 + `GetSharedHandle` 跨设备）结构性冲突。

### 2.5 一句话判定（可复述给任何人）

> 零拷贝代码在 `SessionRecorder.cs:434` 存在且被进入，但 DDA 后端在 `DxgiDdaCapture.TryCaptureBgraTexture`（`:265` 静止超时 / `:297` 共享句柄为零）或 `GpuColorConverter.ConvertFromSharedHandle`（`:191/:192` 跨设备 OpenSharedResource 失败）静默 return false，导致 `zeroHandled` 恒为 false、每帧 fall through 到 CPU 读回路径；全程无任何日志，因为这三条路径都不打日志。**「具体卡在哪一行」需在真机加一条日志或用探针实测确认（见 §5），当前标注为未验证。**

---

## 3. 证据索引（文件:行号 全表）

| 结论 | 证据 |
|---|---|
| 零拷贝分支存在且入口是 gpuConverter!=null | `SessionRecorder.cs:434` |
| gpuConverter 唯一赋值点 | `SessionRecorder.cs:580-589`（`:585` 成功赋值，`:586` 失败日志） |
| NVENC 设备/上下文暴露 | `NvencSdkEncoder.cs:470-471`，赋值 `:516` |
| DDA 零拷贝拿句柄 | `DxgiDdaCapture.cs:253-314`（`:265` 超时 return false，`:286` Shared 标志，`:292` CopyResource，`:296-297` SharedHandle） |
| 转换器跨设备打开 | `GpuColorConverter.cs:178-215`（`:191` OpenSharedResource，`:192/:205` return false） |
| 转换器初始化（VideoProcessor） | `GpuColorConverter.cs:50-121`（`:67/:69/:85/:89/:102/:110` return false） |
| 纹理直通编码 | `NvencSdkEncoder.cs:717-722`（`:719-720` return null） |
| 零拷贝成功/异常日志（本应出现但未出现） | `SessionRecorder.cs:489`（PSNR）、`:505`（异常降级）、`:586`（转换器不可用） |
| CPU 路径三段计时 | `SessionRecorder.cs:516-519, 596-600, 610` |
| 有效帧率 15fps 证据 | `soullink-relay-78240.log:19-21`（30 帧间隔 1.98s / 2.12s） |
| 前端断流重连证据 | `soullink-relay-78240.log:13,72,83,108,122,129,135,146,152,157,171,176,180,185,197,201,206,210` |
| A1/A2/零拷贝提交 | git `15299af`(A1)、`0ad5f2a`+`521f27a`(A2)、`468f2cf`+`c323239`(零拷贝) |

---

## 4. 「340→444」回归的重新归因（推翻旧叙事）

1. **340ms（A1/A2 后）与 444ms（零拷贝部署后）走的都是同一条 CPU 路径**：零拷贝从未生效，两个数字的 agent 侧结构相同（capture+convert+encode ≈36ms）。
2. **因此「零拷贝部署导致回到 444」不成立**——零拷贝等于没部署（未生效）。回归应归因于 A1/A2→零拷贝 之间叠加的其它改动与测量条件：60fps 配置在 RDP 慢合成下实际只 ~14fps、前端断流重连循环、以及两次测量可能的口径/会话差异。
3. **正确的 P0 排序**（按证据强度）：
   - P0-1：**修「有效帧率只有 14fps」**——DDA 在 RDP 内 AcquireNextFrame 频繁 WAIT_TIMEOUT，60fps 形同虚设；要么接受 RDP 真实出帧率、要么改 WGC/事件驱动取帧（对应 A1/A2 方向，非零拷贝）。
   - P0-2：**修前端断流重连循环**——日志 12+ 处 `客户端断开`，每次重连 = 等 IDR + 解码重启，是体感卡顿与端到端延迟的主要放大器。
   - P1：**零拷贝未生效**——但预期收益只有 ~33ms，且需先解决「同 device」（Sunshine 路线A）而非跨设备共享句柄（`DDA-TEXTURE-SHARED-HANDLE-20261124.md` 已给出结论）。

---

## 5. 下一步最小验证清单（每条 = 可执行命令/真机操作 + 预期日志字符串）

> 目标：把 §2.5 里「未验证」的那一行确定下来。全程只读/只操作自己启动的进程，精确 PID，不碰 dist\stable\、不重启 session1、不用 A1/A4 做输入。

### V1. 确认「零拷贝是否进分支、卡在哪一步」——给静默路径临时补 3 行日志（改动最小，回滚=删 3 行）

在 `SessionRecorder.cs` 零拷贝分支内、`:439` 的 `TryCaptureBgraTexture` 返回后与 `:450`/`:452` 返回后各补一行 `RelayAgent.LogQuiet`，重新部署 agent（精确 PID）后真机「点击观看」。

| 操作 | 预期日志字符串（判定） |
|---|---|
| 补日志后点击观看 | `[video] 零拷贝 TryCaptureBgraTexture=false（未拿纹理）` → 卡在 DDA 拿句柄（§2.3-A） |
| 同上 | `[video] 零拷贝 ConvertFromSharedHandle=false：<LastError>` → 卡在跨设备 Open/转换（§2.3-B） |
| 同上 | `[video] 零拷贝 EncodeD3D11Texture=null` → 卡在纹理直通编码（§2.3-C） |
| 同上 | `[video] 零拷贝色差 PSNR(Y) = XX dB` → 零拷贝已生效（回归到浏览器侧排查） |

### V2. 独立探针验证 RDP 会话内 `ID3D11VideoDevice` 与跨设备共享句柄（不改产品代码）

复用 `tools/nvenc-sdk-probe` 形态，新增只读探针（`tools/` 允许保留可构建源码）在 A2 会话运行：

| 命令/操作 | 预期输出 |
|---|---|
| `QueryInterface<ID3D11VideoDevice>` + `CreateVideoProcessorEnumerator(B8G8R8A8→NV12)` | 若输出 `ID3D11VideoDevice 不可用/枚举失败` → 命中 §2.3-D（RDP 无视频设备），零拷贝必须换「同 device + 着色器」路线 |
| DDA 自建设备抓一帧 → `GetSharedHandle` 打印句柄值 | 若 `sharedHandle == 0` → 命中 §2.3-A-`:297` |
| NVENC 设备 `OpenSharedResource(该句柄)` | 若抛异常/返回 null → 命中 §2.3-B-`:191/:192`（跨设备共享不可行，需同 device） |

### V3. 验证「有效帧率 14fps」根因（不动零拷贝）

| 命令 | 预期 |
|---|---|
| 真机点击观看，抓 `soullink-relay-<PID>.log`，数 `共享管线 total=` 两相邻行时间差 | 若 30 帧间隔 ≈2s（15fps）而非 0.5s（60fps）→ 坐实 RDP 慢合成下 60fps 名存实亡，P0 应转向「事件驱动取帧 / 接受真实帧率」 |
| 同日志观察 `staticSkipped` 在用户操作画面时是否持续增长 | 若增长 → 坐实「动画面被误判静止跳帧」（对应 A1 的 FRAME_INFO 判据是否被绕过） |

### V4. 复核 340→444 回归（对比测量）

| 命令 | 预期 |
|---|---|
| 用北京时间对照法（time.syiban.com 双开同框）在**同一会话/同一后端/同一码率**下，分别用「当前 3.3.34」与「A1/A2 之后的提交」各测 3 次取中位数 | 若两者端到端接近（都 ~400ms 级）→ 坐实「回归与零拷贝无关」；若 340 明显更低 → 需 bisect 定位中间引入的回归提交 |

### V5. 浏览器侧埋点（拆开 444ms 的浏览器半段）

| 命令/操作 | 预期 |
|---|---|
| 在 `docs/web-index.html` 帧接收处记录 `capturedAt`（服务端帧时间戳）与 `requestVideoFrameCallback` 时刻，页面小字显示 P50/P95 帧龄 | 得到「服务端出帧 → 浏览器上屏」的真实毫秒数，把 §1.3 的 50~100ms 估算变成实测 |

---

## 6. 未验证清单（明确不猜）

| 项 | 状态 |
|---|---|
| 零拷贝具体卡在 §2.3 的 (A)/(B)/(C) 哪一行 | 未验证——需 V1/V2 补日志或探针 |
| RDP 会话内 `ID3D11VideoDevice` 是否可用 | 未验证——需 V2 探针 |
| 浏览器解码+上屏的真实毫秒数 | 未验证——需 V5 埋点 |
| 340→444 回归的确切提交 | 未验证——需 V4 bisect |
| 零拷贝若生效后的真实收益（预计 ~33ms） | 未验证——零拷贝从未生效，无法实测 |

---

## 7. 相关文档

- `HANDOFF-VIDEO-LATENCY-GPU-20261124.md` —— 本轮交接（444ms / 零拷贝未生效的原始定位线索）
- `GPU-ZERO-COPY-IMPL-PLAN-20261124.md` —— 零拷贝实现方案（阶段A）
- `DDA-TEXTURE-SHARED-HANDLE-20261124.md` —— DDA 跨设备共享句柄研究（路线A同 device 结论）
- `AGENT-PIPELINE-LATENCY-GAP-20261123.md` —— 453ms 拆解 + H1 静止误判假说
- `LATENCY-BENCHMARK-20261123.md` —— 15fps 时代的 157ms 节拍实测
- `GPU-ZERO-COPY-20261123.md` —— 零拷贝调研（NVENC SDK 直通唯一路线）
