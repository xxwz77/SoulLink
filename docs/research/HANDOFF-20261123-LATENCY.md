# 交接文档 · 视频流延迟攻坚（2026-11-23 上午）

> 状态：**裸 HTTP 推流能出画面、全程 H.264、内存稳定，但端到端延迟 4 秒不可接受**
> 上一份：`HANDOFF-STREAM-STATUS-20261123.md`（凌晨，记录 /stream 初次跑通）
> 本份覆盖：3.3.20 全部修复 + 延迟根因定位 + 下一步技术路线

---

## 0. 三句话现状

1. **画面能出、有颜色、多观众可同时看、内存不再泄漏** —— 凌晨修的那批全部生效。
2. **但延迟 4 秒**（用户用「北京时间对照法」实测：远程桌面 10:17:28.9 vs 本机 10:17:24.9）。
3. **根因已定位到三层**：① 软编优先导致 NVENC 闲置 ② 每秒 1 个 95KB 的 IDR ③ 浏览器解码积压不丢帧。

---

## 1. 用户实测的关键数字（最硬的证据）

### 端到端延迟 4.0~4.3 秒

本机浏览器打开 https://time.syiban.com，远程桌面里也打开同一个页面，同框截图：

```
本机网页时钟    10:17:24.9
远程桌面时钟    10:17:28.9
延迟            4.0 秒
```

### 输入链路完全正常（已排除）

- 服务端 `/api/input` 往返：**min 1ms / avg 3.6ms**（20 次采样）
- 300 个并发 move 后再发右键：**43ms**（无排队）
- 用户自己开 DevTools Network 面板确认：每条 input 请求 200、耗时 1~3ms

**结论：命令 43ms 就执行了，用户要等 4 秒才「看见」。延迟在画面侧。**

### IDR 大小异常（关键发现）

抓 /stream 的 NAL 到达时间戳（6 秒）：

```
67ms   /  3801B     普通帧
67ms   / 95618B    <- IDR 关键帧，95 KB
225ms  /  4089B
425ms  /  4090B
628ms  /  1665B
1233ms /  4089B
1233ms / 95310B    <- 又一个 95 KB IDR
```

- **每秒 1 个 IDR，每个 95 KB**
- 普通帧只有 1.6~4 KB
- 95KB 在 8Mbps 下需 95ms 传完，这期间画面停滞

---

## 2. 根因（三层，按影响排序）

### 根因 A：软件编码器优先，NVENC 一直闲置

`Services/MfFrameEncoder.cs` 当前枚举顺序（第 96 行起）：

```csharp
foreach (var act in swColl) { _transform = act.ActivateObject<IMFTransform>(); _hardware = false; break; }
if (_transform == null)
    foreach (var act in hwColl) { ... _hardware = true; break; }
```

**软件优先，硬件只在软编不存在时才用。**

而 `docs/research/MEDIA-CAPABILITY-20261122.md:23` 早就测出：

> 硬件 H.264 编码器 | **存在**：NVIDIA H.264 Encoder MFT（NVENC）

本机 RTX 4060 的 NVENC **从头到尾没被用过**。

且整个初始化只设了 `MF_MT_AVG_BITRATE`，没有 GOP / 码控模式 / 关键帧质量 / 低延迟设置。
软件 MFT 默认 VBR 追质量 -> I 帧 95KB、P 帧 2KB，码率控制完全失衡。

### 根因 B：NVENC 硬编代码有 3 个时序 bug（deploy-fix 对照 MSDN 确认）

详见 `docs/research/NVENC-IMPLEMENTATION-20261123.md`。三个确切位置：

| # | 位置 | 问题 | 正确做法 |
|---|---|---|---|
| 1 | `MfFrameEncoder.cs:264` | `GetEvent(0)` 是同步阻塞，timeout=0 立即返回 null，永远收不到 HaveOutput | 必须用 `BeginGetEvent`/`EndGetEvent` **异步接收** |
| 2 | `:278` | `if (!hr.Success) break;` 把 `MF_E_TRANSFORM_NEED_MORE_INPUT`、`E_UNEXPECTED` 当失败 | 这两个是**正常控制流** |
| 3 | `:195-206` | 驱动模型颠倒：现在是「ProcessInput -> 立即轮询 ProcessOutput」 | 异步 MFT 是「收到 `METransformNeedInput` 才 ProcessInput、收到 `METransformHaveOutput` 才 ProcessOutput」 |

**NVENC 不出帧不是 NVENC 的问题，是我们没按异步模型写。**

### 根因 C：浏览器解码积压不丢帧

`docs/web-index.html` 收到每一帧都 `decoder.decode()`，从不丢弃。
解码速度 < 生产速度时，队列越积越长，积压 65 帧 = 4.3 秒。

**已修**（web-player 交付）：`decodeQueueSize > 5` 时丢 delta AU，连续跳 60 AU 触发 `decoder.reset()` + 等下一个 IDR。

**但这是止血，不是治本** —— 服务端仍在制造积压。

---

## 3. 本轮已完成（已提交，3.3.20）

```
1fa46cf  test: P1 架构改动功能验收全部通过
24d0ce0  feat: 视频流链路全面修复 + UI 线程减负 + JPEG 序列下线 (3.3.20)
```

### 视频链路

| 修复 | 文件 | 效果 |
|---|---|---|
| NV12 色度全填 128 -> 2x2 降采样 BT.601 | `AgentWebServer.cs` | 黑白 -> 彩色（A1 实拍截图确认） |
| `IMFSample` 从不 Dispose | `MfFrameEncoder.cs` | 私有内存 9.9GB -> 1.2GB 稳定 |
| WGC 帧池用整屏尺寸而非 `item.Size` | `WgcCapture.cs` | 窗口小于全屏时全黑 -> 正常 |
| 逐 NAL 喂 decode -> AU 攒批 | `web-index.html` | 一帧解不出 -> 28/30 帧 |
| JPEG 序列下线，Ring 存 NAL | `SessionRecorder.cs` | 多观众共享一次编码 |
| NAL -> MP4（不重编码） | `Mp4Writer.cs` | 录像不再写 JPEG 再转码 |
| 缺 DLL 时返回中文 500 | `AgentWebServer.cs` | 连接重置 -> 可诊断 |

### 安全

| 修复 | 效果 |
|---|---|
| `CheckToken` 空 token 默认拒绝 | 默认无鉴权 -> 401 |
| CORS 从 `*` 收口为同主机/回环 | 任意网页可注入键鼠 -> 受限 |

### 性能 / UI

| 修复 | 效果 |
|---|---|
| `TickHub.RegisterBackground` | 4 个 IO 订阅者迁出 UI 线程 |
| RdpConsole 5 个 DispatcherTimer 收编 | 60ms 轮询改事件驱动 |
| `ConfigStore.Save` 后台串行队列 | UI 线程不阻塞 |
| `convertMs` 单线程索引优化 | 13~15ms -> 3~6ms |

### 部署

| 修复 | 效果 |
|---|---|
| `BrokerSetup` 安装时同步旁挂 DLL / web-index.html / tools | 代理目录缺运行库 -> 齐备 |
| `broker-install.ps1` 重写（原脚本是死代码） | 参数已不存在 -> 转发给真安装器 |

### 验收数据（`docs/research/P1-VERIFY-20261123.md`）

```
句柄    45398 -> 807      (-98%)
内存    1710MB -> 890MB
单核CPU 41~45% -> 33.4%
双观众  各 334 NAL / 11 关键帧，字节数完全相同（共享编码验证通过）
```

---

## 4. 未提交的改动（工作区，编译 0 error）

**stream-core 正在收尾，以下是未完成的半成品：**

```
Controls/RdpGeneralSettings.xaml     +22   直播帧率下拉（未完成）
Controls/RdpGeneralSettings.xaml.cs  +12
Models/Models.cs                      +5   StreamFps 配置项
Relay/SessionRecorder.cs             +89   RecomputeLifecycle（改到一半）
Relay/WgcCapture.cs                   +1
Services/MfFrameEncoder.cs           +95   硬编 event pump（有问题，见根因 B）
Services/Mp4Writer.cs                +11
Services/RelayClient.cs               +3
Services/WebServer.cs                 +5
docs/web-index.html                 +169   鼠标跟手 + 右键转视角 + 跳帧（已验证）
Services/WebPages.cs                +149   同上（生成产物）
```

**关键：这些改动编译通过（0 error），但功能未完整验证。下次会话第一件事应该是确认它们的状态。**

### 收尾时的确切状态（stream-core 中断前的最后确认）

- **`MfFrameEncoder` 默认软编优先**：`swColl` 在第 96 行，`hwColl` 在第 99 行作为回退。硬编路径保留但不再默认启用。
- **`RecomputeLifecycle()` 已实现**（`SessionRecorder.cs:162-166`）：

      private static void RecomputeLifecycle()
      {
          // 管线只要还有录像或观众就继续；只有两者都没有才停止。
          _stopRequested = !_recordRequested && Viewers.Count == 0;
      }

  逻辑正确，但「关录像但仍有观众」这个场景**没有实测验证**（stream-core 自己明确说了不能把代码逻辑当实测通过）。
- **Ring 在停止时清空**已实现。
- 直播帧率配置（`StreamFps`）、WPF 下拉、网页下拉：**部分完成**，未验证。

---

## 5. 用户明确的架构要求（下次会话必须实现）

### 要求 1：不录像也能直播，无人观看时 0 消耗

用户原话：

> 「现在的痛点是为了直播必须打开录像，应该是不录像也能直播，没人的时候 0 消耗，这样才合理。」

目标状态：

```
有人在看（不管录不录像）-> 管线运行，帧率 = StreamFps (30/45/60)
没人看 + 不录像        -> 管线停止，0 消耗（CPU/内存/显存/句柄全释放）
没人看 + 在录像        -> 管线运行，帧率 = RecordFps (5/10/15)
有人在看 + 在录像      -> 管线运行，帧率 = StreamFps
```

当前问题：`Start()` 强制设 `_recordRequested = true`，直播依赖录像标记。

### 要求 2：帧率配置

| 配置 | 档位 | 默认 | 生效场景 |
|---|---|---|---|
| `StreamFps`（直播帧率） | 30 / 45 / 60 | 30 | 有观众 |
| `RecordFps`（录像帧率） | 5 / 10 / 15 | **5** | 无观众 |

**注意：5fps 是录像帧率本身，不是「直播降频档」。** 用户纠正过这个概念。

### 要求 3：编码器选项要注明，自动选择要准确

用户原话：

> 「软编肯定也得优化，万一用户不是 n 卡呢？选项应该注明，自动选择要准确。」

必须支持：

| 选项 | 说明 |
|---|---|
| 自动（推荐） | **探测式验证：真的出帧才算通过** |
| NVIDIA NVENC | 明确指定 |
| Intel Quick Sync | 明确指定（笔记本主流） |
| AMD AMF | 明确指定 |
| 软件编码 | 兜底 |

**「自动」的判据必须是「喂一帧测试数据、300ms 内产出 NAL」，不能是「枚举到了」。** 这次就是踩了这个坑。

### 要求 4：录像开关不能影响直播

`Stop()` 现在（半完成版）改成：

```csharp
public static void Stop() {
    lock (Gate) { _recordRequested = false; RecomputeLifecycle(); }
}
```

需要验证 `RecomputeLifecycle()` 是否正确实现「有观众就不停」。

---

## 6. 下一步技术路线（已与用户讨论过优先级）

| 顺序 | 任务 | 理由 |
|---|---|---|
| 1 | **完成生命周期分离**（要求 1 / 4） | 真 bug，改动已在工作区 |
| 2 | **手写 ICodecAPI（约 40 行）** | 按需 IDR + 低延迟，硬编软编都要 |
| 3 | **修 NVENC 3 个时序 bug** | 调研已给出确切位置 |
| 4 | **GOP 拉长 + 按需 IDR** | IDR 总开销降 4~20 倍 |
| 5 | **编码器选项 + 探测验证** | 要求 3 |
| 6 | 软编优化（GOP、CBR）兜底 | N 卡以外用户 |

### 为什么第 2 步是 ICodecAPI

- **按需 IDR**：`CODECAPI_AVEncVideoForceKeyFrame` —— 新观众接入时立刻发关键帧，这样 GOP 才能拉长到 300+
- **低延迟模式**：`CODECAPI_AVLowLatencyMode` —— 关掉编码器内部帧缓冲
- **CBR 码控**：`CODECAPI_AVEncCommonRateControlMode`

Vortice 3.8.3 **没有** `ICodecAPI` 绑定，必须手写。

风险：本项目手写 COM vtable 翻车过 3 次（`Mp4Writer`）。
但不做这个，就只能靠「拉长 GOP」单腿走路，30/45/60 帧率也难做好。

### ICodecAPI 手写要点

```
IID: 901db4c7-31ce-41a2-85dc-8fa0bf41b8da
只需要两个方法：SetValue(GUID*, VARIANT*), GetValue(GUID*, VARIANT*)
注意：这是 IUnknown 之后的第 1、2 个槽位（不是 0 开始）
VARIANT 要注意 VT_BOOL / VT_UI4 的区别
```

---

## 7. 环境与约束

### 机器

```
Win11 29698 / i9-13900HX 32核 / 29.7GB / RTX 4060 Laptop 8GB
有 NVENC（NVIDIA H.264 Encoder MFT）
```

### 会话（2026-11-23 10:00 状态）

```
console  lummy  1  <- 主桌面
A1              2  <- 可只读抓屏测试，不能注入键鼠
A4              3
A2              4  <- 用户测试用，agent PID 91664
```

### 运行中的进程

```
SoulLink         5080   session 1   dist/SoulLink.exe          <- 用户测试用（3.3.20）
SoulLink        68156   session 0   SoulLinkDev/SoulLink.exe   <- Broker 服务端
SoulLink-Stable 13116   session 1   dist/stable/...            <- 挂机测试版【禁碰】
SoulLink.Agent  33372   session 2   (旧代码，01:05 启动)
SoulLink.Agent  41672   session 3   (旧代码)
SoulLink.Agent  91664   session 4   (旧代码，09:26 启动) <- 用户正在看
```

### 铁律（逐条都有事故背景）

1. **绝不碰 `dist/stable/SoulLink-Stable.exe`**（用户挂机测试版）
2. **不用 A1/A4 账号做输入控制**（可以只读抓屏）
3. **不要重启 session 1 的主程序**（会断用户 RDP 会话）
4. **停进程禁止路径通配**，只用精确 PID 或完整路径
5. **不要反复重建 A2 的 agent**（用户原话：重建次数太多要注销 A2 再重连）
6. **系统会因过载自动关机**（凌晨发生过一次，9.9GB 泄漏导致）—— 测试必须挂熔断
7. **不发布、不碰 COS、不改 latest.json**

### 部署状态

`C:/Program Files/SoulLinkDev/` **已补齐**：

```
SoulLink.exe         4068703   09:21:53   (3.3.20)
SoulLink.Agent.exe   4068703   09:21:53
web-index.html         74799   01:41:19   <- 需更新（当前 docs 是 79934）
Vortice.*.dll ×5 / WinRT.Runtime / SharpGen ×2 / SDK.NET  全部就位
```

**注意：运行中的 3 个 agent 都是旧代码**（09:26 前启动），要让新代码生效必须重启 agent。

---

## 8. 验证方法（用户已经用过，很有效）

### 延迟对照法（最直观）

1. 本机浏览器打开 https://time.syiban.com
2. 远程桌面里也打开同一页面
3. 同框对比两个时钟 -> 差值就是端到端延迟

**目标：< 500ms**

### IDR 分析（命令行）

```powershell
$ffprobe = "C:/Users/91244/AppData/Local/Temp/ff-dl/x/ffmpeg-master-latest-win64-gpl/bin/ffprobe.exe"
& $ffprobe -v error -select_streams v:0 -show_entries frame=pict_type,pkt_size,pts_time `
  -read_intervals '%+10' -of csv "http://127.0.0.1:19081/stream?token=test123"
```

看：IDR 间隔（应 >=5 秒）、IDR 大小（应 <30KB）

### 内存熔断（防关机）

```powershell
$deadline = (Get-Date).AddMinutes(5)
while ((Get-Date) -lt $deadline) {
  $p = Get-Process -Id <PID> -ErrorAction SilentlyContinue
  if (-not $p) { break }
  $mb = [math]::Round($p.PrivateMemorySize64/1MB,0)
  if ($mb -gt 2048) { Stop-Process -Id $p.Id -Force; 'CIRCUIT BREAKER'; break }
  Start-Sleep -Seconds 3
}
```

---

## 9. 本轮新增的研究文档

| 文档 | 内容 |
|---|---|
| `BLACKSCREEN-ROOTCAUSE-20261123.md` | WGC 帧池尺寸 bug 的三组对照实验（含推翻自己错误结论） |
| `MEMORY-LEAK-VERIFY-20261123.md` | 内存量具 + 判据 + 24 项回归清单 |
| `STREAM-VERIFY-20261123.md` | 全链路端到端验证 |
| `ZERO-JPEG-VERIFY-20261123.md` | 零 JPEG 验收 |
| `LATENCY-BENCHMARK-20261123.md` | 延迟分段归因 + 业界对标 |
| `REMOTE-ACCESS-OPTIONS-20261123.md` | 手机/远程访问 5 条路线对比（结论：做不做 APP 是伪命题） |
| `UI-PERF-IO-TRIAGE-20261123.md` | 94 处 UI 线程同步 IO 分类清单 |
| `VERIFY-PITFALLS-20261123.md` | **9 条验证陷阱**（含「注释里的 toDataURL 不算调用」） |
| `NVENC-IMPLEMENTATION-20261123.md` | **NVENC 3 个时序 bug + 软编能压/不能压的边界** |
| `P1-VERIFY-20261123.md` | P1 架构改动五条验收 |

---

## 10. 教训

### Lead 犯的错

| 错误 | 后果 | 修正 |
|---|---|---|
| 把「视频流卡」理解成画质问题 | 查了半天码率/GOP，方向错 | 用户指出是「鼠标操作反馈」才转向 |
| 定「3 分钟内存 <500MB」当验收 | 1.2GB 被误判为失败 | 私有内存 1.2GB 对 1080p 编码是正常的 |
| **NVENC 失败后直接决定「回退」** | 用户批评「不要擅自做决定」 | 应该先查 OBS/Sunshine 怎么做的 |
| 把 5fps 理解成「直播降频档」 | 概念错误 | 5fps 就是录像帧率本身 |
| 建议「网页本地渲染假光标」 | 用户指出「要看见真实状态」 | 该做的是加光标开关 |

### 团队发现的（都值得记）

1. **web-player**：**逐 NAL 喂 decode = 一帧都解不出**。WebCodecs 要求完整 access unit。这个 bug 藏在最深处，服务端全修好了前端也会黑屏。
2. **verifier**：WGC 帧池用整屏尺寸但抓单窗口 -> 超出区域全 0 -> 全黑。而且它**先推翻了自己的错误结论**（DPI 假设）才发现。
3. **deploy-fix**：空 token 默认放行 = 新装软件默认无鉴权，`/api/input` 可被任意网页注入键鼠。
4. **verifier**：`/stream` 内存泄漏 128MB/s（每帧 4 个 COM 对象不释放）—— **这很可能就是凌晨系统自动关机的原因**。

---

## 11. 下次会话第一步

1. 读本文件
2. `git status` 看未提交改动，确认 stream-core 的收尾状态
3. `dotnet build SoulLink.csproj -c Release` 确认 0 error
4. 读 `NVENC-IMPLEMENTATION-20261123.md`（决定硬编怎么做）
5. 按 §5 的四个架构要求排任务

**用户最痛的是延迟 4 秒。先解决它，再谈其他。**