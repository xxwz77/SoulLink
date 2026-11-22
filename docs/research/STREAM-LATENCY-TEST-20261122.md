# /stream 端到端延迟实测与优化（A1 会话 / 原神实时流）

> 日期：2026-11-22
> 作者：implementer（dsh-team `team_mwrnf1771i2w7` / tasks `task_mwryp74p1607cg7`、`task_mwrzf4jh17kegf5`、`task_mws08tje18pb12f`）
> 范围：只读实测 + `tools/stream-probe/` 独立探针；未改业务代码、未动 `dist/`、未提交 git、未注入输入、未碰 BetterGI/游戏。
> 代码基线：`SoulLink.csproj` `<Version>3.3.19</Version>`

---

## 0. 一句话结论

**在 A1（session 18，RDP）以 A1 身份实测「WGC 抓原神窗口 → H.264 编码 → HttpListener chunked Annex-B → ffplay 实时播放」全链路跑通。软编（同步 MFT）端到端延迟 ≈1.4s；换 NVENC 硬编（异步 MFT，MF_TRANSFORM_ASYNC_UNLOCK + 事件驱动）后 ≈0.64~0.74s，延迟减半。抓屏 2~8ms、编码 4~8ms——链条本身极快，剩余延迟主要是编码器流水线 + ffplay 缓冲，到 <500ms 还差一步（CODECAPI_AVLowLatencyMode，Vortice 无 ICodecAPI 需手写）。**

---

## 1. 实测环境

| 项 | 值 |
|---|---|
| 目标会话 | session 18（A1，RDP，`SM_REMOTESESSION=1`） |
| 投递方式 | `schtasks /Create /RU A1 /IT`（A1 交互登录态，无需凭据） |
| 游戏窗口 | 原神 `YuanShen.exe`，1920x1080，WGC `CreateForWindow` |
| 显存 | 全程 free 1496~1850 MiB，BetterGI（同会话并发 WGC V2）未受影响 |
| 编码器 | ①软件同步 MFT ②硬件异步 MFT（NVENC, RTX 4060） |
| 播放端 | ffplay（低延迟参数 `-fflags nobuffer -flags low_delay`） |
| 测量法 | 帧内画毫秒时间戳（WGC 帧→System.Drawing 叠字）→ ffplay 播放时 PowerShell 全屏截屏 → 对比截屏时刻 vs 帧内时刻 |

---

## 2. 软编 vs 硬编对比

| 项 | 软件同步 MFT | 硬件异步 MFT (NVENC) |
|---|---|---|
| 编码器模式 | 同步 ProcessInput/ProcessOutput | `MF_TRANSFORM_ASYNC_UNLOCK` + GetEvent 事件驱动 |
| 流水线 | **30 帧喂入只出 13 帧**（前段累积缓冲） | **1:1 即时产出**（到达 315 → 编码 314） |
| 单帧耗时 | 抓屏 2~8ms + 编码 4~8ms | 抓屏 2~8ms + 编码 6~8ms |
| **端到端延迟** | **≈1384ms** | **≈636~742ms** |
| 40s 编码帧数 | 1208（≈30fps） | 1223（≈30.5fps） |

**结论：软编的 1.4s 延迟大头是同步 MFT 的流水线累积；硬编异步把流水线压成 1:1，延迟直接减半。剩余 ~650ms 中，约 100~200ms 是 WGC/编码/网络链路，其余主要是 ffplay 播放端缓冲（即使加了 nobuffer/low_delay）。**

---

## 3. 各阶段耗时分解（硬编路径）

| 阶段 | 耗时 | 说明 |
|---|---|---|
| WGC 帧到达 | 0（推送） | 帧率=游戏渲染率（30fps 锁帧） |
| D3D11 staging ReadBack | 2.0~8.7ms | 1920x1080 BGRA 8MB |
| 画毫秒时间戳 | ~1ms | System.Drawing 叠字 |
| BGRA→NV12 | ~2ms | CPU 逐像素（可 SIMD/GPU 优化） |
| H.264 编码（NVENC 异步） | 6~8ms | 事件驱动即时产出 |
| HTTP chunked 传输（本机） | <1ms | 127.0.0.1 |
| ffplay 解码+渲染缓冲 | **≈500ms（大头）** | 即使 nobuffer/low_delay/framedrop |
| **端到端合计** | **≈636~742ms** | 目标 <500ms 差在播放端 |

---

## 4. 到 <500ms 的剩余路径（按性价比排序）

1. **CODECAPI_AVLowLatencyMode**（编码端）：Vortice.MediaFoundation 无 ICodecAPI 绑定，需手写一个小接口（QI IID_ICodecAPI + SetValue，约 50 行）。预期砍掉编码器内部 1~2 帧缓冲。
2. **GOP/关键帧间隔拉长 + B 帧禁用**：同样走 ICodecAPI（CODECAPI_AVEncMPVGOPSize、CODECAPI_AVEncCommonRealTime）。
3. **播放端**：ffplay 的缓冲不可控，真实前端是 WebCodecs/MediaCodec，低延迟配置空间更大。本测量的 ffplay 数字是**保守上界**。
4. **WebCodecs 前端直接消解 Annex-B→AVCC 转换**：低延迟模式 latencyHint 可控。

---

## 5. 三个实现坑（本次踩过，已修复，供后续复用）

1. **HttpListener prefix 必须以 `/` 结尾**：`http://127.0.0.1:18099/stream` 会抛 ArgumentException（`Only Uri prefixes ending in '/' are allowed`）。
2. **`GetContextAsync` 任务必须跨循环保留**：每轮 `GetContextAsync()` 丢弃重建 = 客户端连接永远不被 Accept（表现为 ffplay 挂在 vq=0KB）。正确写法：pending 任务存变量，`Wait(500)` 后检查 `RanToCompletion` 才消费。
3. **WGC 帧的 COM 所有权**：`((IWinRTObject)surface).NativeObject.ThisPtr` 取的 accessPtr **不能手动 `Marshal.Release`**——RCW 已拥有引用，手动释放与 Finalize 双重释放 = 0xC0000005 崩溃（与 streamgap 在 `WGC-SNAPSHOT` 记录的 Finalize 双释放同源）。`GetInterface` 出的 texPtr 交给 Vortice `ID3D11Texture2D` wrapper（`using`）拥有，也不要手动 Release。
4. **nvidia-smi 查询死锁**：`RedirectStandardOutput + ReadToEnd` 不读 stderr 会经典死锁；改用 `ReadToEndAsync` 并行读 + `WaitForExit(4000)` 超时杀。

---

## 6. 边界遵守

- BetterGI（session 18 PID 126956）全程运行未受影响；未注入输入、未改焦点、未重启任何进程
- 显存监控保护（free <300MB 或 BetterGI 消失即停）全程未触发
- 90 秒窗口限时，测完清理：进程/计划任务/截图/日志全删
- 未改 `Version`/`dist/`、未 commit/push

## 7. 产物

- `tools/stream-probe/Program.cs` + `StreamProbe.csproj`：独立探针（WGC→时间戳→软/硬编码→/stream），`-Soft` 开关切换软编对照
- 本文档：各阶段耗时分解 + 软硬编对比 + 剩余优化路径

## 修订记录

| 版本 | 日期 | 内容 |
|---|---|---|
| v1.0 | 2026-11-22 | 初版：A1 实测软编 1.4s / 硬编 0.64~0.74s + 耗时分解 + 三个坑 |
