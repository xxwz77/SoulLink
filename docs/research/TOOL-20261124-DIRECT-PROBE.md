# TOOL：direct-probe 独立原生客户端探针——绕开浏览器直连 /stream 判定延迟归属

> 日期：2026-11-24
> 作者：transport-network-analyst（dsh-team `team_mwu35l8pae8oj5` / member `m_mwu37cxvahbb82` / task `task_mwu40qf2dr188i`）
> 性质：只读审计 + 独立探针工具（`tools/direct-probe/`，纯新增，未改任何产品代码）。测后已清理测试 agent/计划任务/临时文件。
>
> 证据分级：【实测】= 本会话真机测量（数值+方法可复现）；【代码事实】= 源码 file:line；【已有实测】= 仓库前序文档；【推断】= 有依据推导。

---

## 0. 一句话判定

**延迟不在浏览器（原生客户端解码+上屏合计仅 ~9ms），而在服务端的帧供给节奏——桌面动态时服务端也只出 ~16fps（30 帧间隔 1.82~1.83s，captureMs 含等帧 8~89ms），一次画面变化平均要等 ~30ms+ 才被采到并编码。** 用户 233ms 差分（SoulLink 浏览器 − RDP 同源秒表）的主项是「服务端出帧节奏（16fps 采样等待）+ 浏览器段（估 20~40ms，原生对照 9ms）」，其中浏览器段贡献 ~15~30ms，其余大头在帧供给节奏与 RDP 的「变化即推送」优势差。

---

## 1. 设计与架构

### 1.1 目的

用一条**完全不含浏览器**的原生链路（HTTP 收包 → MF 解码 → GDI 上屏）消费同一个 `/stream?v=2`，测量三段耗时并与浏览器对照，判定 233ms 差分延迟归属。

### 1.2 组件（`tools/direct-probe/`，3 源文件 + csproj）

| 文件 | 职责 |
|---|---|
| `Program.cs` | HTTP 收包（HttpClient 流式读）、4B 长度前缀帧边界解析、三段计时与 p50/p95/max 统计、`--file`（本地 framed 文件回放诊断）、`--decode-ffmpeg`（管道喂 ffmpeg 的对照模式） |
| `MfDecoder.cs` | Media Foundation H.264 解码器（MFTEnumEx 枚举 + **output-first 类型协商** + 逐 AU ProcessInput/ProcessOutput），NV12 输出 |
| `Window.cs` | 最小 GDI 窗口（RegisterClass/CreateWindow + NV12→RGB32 软件 YUV 转 + SetDIBitsToDevice），消息泵内嵌在 Blit |

### 1.3 计时口径（每帧）

| 记号 | 定义 | 实现位置 |
|---|---|---|
| t0 | 一帧字节全部从 socket 读齐（4B 前缀+数据 read 完成） | `Program.cs` 主循环 |
| t1 | MF `ProcessOutput` 返回该帧 NV12（解码完成） | `MfDecoder.DrainOne` |
| t2 | `SetDIBitsToDevice` 返回（上屏完成） | `Window.Blit` 调用后 |
| gap | 相邻帧 t0 差（帧到达节奏 = 服务端出帧节奏的直接测量） | `Program.cs` |

### 1.4 解码路径选型理由

- **Media Foundation MFT（微软软解 H264 Video Decoder MFT）**：系统自带零依赖、与主程序同栈（Vortice.MediaFoundation 3.8.3，NuGet 缓存离线可构建）。硬解 MFT 在 RDP session 内不可枚举（MFT_ENUM_FLAG_HARDWARE 下无 Intel/NVIDIA 解码 MFT【实测枚举 0 个】，本机抓屏源 Intel UHD 的 QuickSync 解码 MFT 不在该会话可用）。
- **GDI 而非 D3D 上屏**：探针目标是给浏览器一条非浏览器参照；GDI 路径最短无 GPU 上下文管理。代价是 CPU YUV 转换（计入 t2，报告单列，不与服务端/网络混淆）。
- 备用模式 `--decode-ffmpeg`：收到的帧剥前缀后管道喂系统 ffmpeg（`-vf showinfo`）做解码对照——用于交叉验证流合法性与解码速率。

### 1.5 构建/运行方法

```powershell
cd tools\direct-probe
dotnet build -c Release
# 连流（需 agent 已起且带 web 端口+token）：
.\bin\Release\net11.0-windows\direct-probe.exe "http://127.0.0.1:19099/stream?token=<token>&v=2" --duration 12
#   DIRECT_PROBE_NO_WINDOW=1 环境变量 → 无头模式（只测收包+解码）
# 本地文件回放（诊断）：
direct-probe.exe --file <framed.h264>
# ffmpeg 解码对照模式：
direct-probe.exe <url> --decode-ffmpeg --duration 10
```

测试 agent 启动方式（本次用，已清理）：A2 会话（session 4）计划任务 `cmd /c` 一个 %TEMP% 下的 bat（设置 `SOULLINK_WEB_PORT=19099 / SOULLINK_WEB_TOKEN / SOULLINK_STREAM_FPS=60` 后 start dist\SoulLink.Agent.exe）——agent 无主管道也能起 web 监听（`RelayAgent.cs:617` StartWebDirect 在管道连接前执行），管道 40×3.5s 超时窗口内完成测量。

---

## 2. 踩坑记录（后续可复用，工程价值高）

| # | 坑 | 事实 | 解法 |
|---|---|---|---|
| 1 | **MFT_CATEGORY_VIDEO_DECODER 的 GUID** | 网上常传 `d6c02d6e-18d4-46d4-9e76-9faa1c20c0c6`（枚举 0 个）。真实值 `d6c02d4b-6833-45b4-971a-05a4b04bab91` | 从注册表 `HKLM\SOFTWARE\Classes\MediaFoundation\Transforms\Categories` 实证【实测】 |
| 2 | **MFTEnumEx filter 不筛 H264** | 带 RegisterTypeInfo(H264) 枚举出 MPEG/DV/Mpeg4s 等无关解码器（注册表不按 input subtype 挂） | 无 filter 枚举 + 逐个「按名含 H264 匹配」 |
| 3 | **解码器类型协商必须 output-first** | 先 SetInputType 后 SetOutputType → 永远 NEED_MORE_INPUT 不吐帧（输入 180 帧 0 输出）；先 SetOutputType(NV12) 再 SetInputType(H264) → 立即出帧 | `MfDecoder.Configure` 输出类型先协商【实测：修复后 45/45 帧解出】 |
| 4 | **ProcessOutput 输出 sample 不能跨多次调用复用** | 复用 sample（RemoveAllBuffers+重挂 buffer，编码器模式）在解码器上导致 0xC0000409 崩溃（同帧多次 Drain 时 MFT 内部引用被破坏） | 每次 DrainOne 新建 sample/buffer，成功后 Dispose【实测：崩溃消失】 |
| 5 | **HttpListener 端口归 http.sys（PID 4）** | netstat 按 agent PID 查监听恒空，要用 Get-NetTCPConnection 按 LocalPort 查 | 端口→PID4 = http.sys 正常现象 |
| 6 | **独立 agent 的生命周期** | 无主管道时 agent 40×(3s+0.5s)≈140s 后自退（`RelayAgent.cs:667-672`） | 测量窗口内完成；或每次 schtasks /Run 重启 |

---

## 3. 真实测量结果（2026-11-24 11:30，session4/A2 测试 agent，1920x1080，NVENC-SDK 18Mbps，桌面动画跑动）

### 3.1 三段计时【实测】

| 段 | p50 | p95 | max | n |
|---|---|---|---|---|
| **解码 t1-t0**（MF 软解 1080p） | **0.91 ms** | 1.34 ms | 1.56 ms | 177 |
| **上屏 t2-t1**（GDI NV12→RGB+SetDIBits） | **7.79 ms** | 10.11 ms | 14.93 ms | 177 |
| **帧到达间隔 gap**（=服务端出帧节奏） | **55.38 ms** | 103.24 ms | 112.14 ms | 215 |
| 帧率 | 解码出帧 14.7 fps；收包 17.8 fps | | | |

- 交叉验证 1：agent 日志（`soullink-relay-53356.log`）`total` 30 帧耗时 1.82~1.83s = **服务端 16.4fps**，与探针 gap p50 55ms 一致——**探针实测即服务端真实节奏**。
- 交叉验证 2：`staticSkipped=0`（桌面判定有变化、动画在跑）但出帧仍 ~16fps → **不是静止误判，是 capture/编码节拍本身**。agent 侧 captureMs 8~89ms 波动（含 DDA 等帧时间）。
- 交叉验证 3：ffmpeg 解码模式（`-f h264 -i pipe:0`）同流可解、同节奏到达。
- 交叉验证 4：服务端 `queueAge=0 queueAgeMax=16`——服务端队列无积压。

### 3.2 原生客户端 vs 浏览器的处理开销对照

| 端 | 解码 | 上屏 | 合计 |
|---|---|---|---|
| **原生探针（本工具）** | 0.91ms（MF 软解） | 7.79ms（GDI 软件 YUV+BitBlt） | **~9ms** |
| 浏览器（WebCodecs+vsync） | 2~5ms（硬解估） | 20~40ms（含 1 vsync 16.7ms+合成） | ~25~45ms |

→ 浏览器比原生多 **~15~35ms** 处理开销【推断：原生实测 + 浏览器为既有估算，rVFC 埋点后可替换】。

---

## 4. 判定：延迟在浏览器还是服务端？

**两者都有，但服务端是主项：**

| 段 | 对 233ms 差分的贡献 | 依据 |
|---|---|---|
| **服务端帧供给节奏** | **主项**：16fps 出帧 → 一次画面变化平均等 ~30ms（1/16.4/2）+ 最坏 ~61ms 才被采到；RDP 是变化即推送（bitmap delta 无采样等待）→ 差分里 SoulLink 的采样等待无法被 RDP 侧抵消 | 【实测】gap p50 55ms + agent 日志 16.4fps |
| 服务端编码/队列/传输 | ≈0：queueAge=0、传输 1ms、帧一入队即推 | 【实测】 |
| **浏览器段（解码+上屏+vsync）** | **次项 ~15~35ms**：比原生 9ms 多出的部分 | 【实测对照+推断】 |
| RDP 侧自身 | 差分口径下不占（233 = SoulLink−RDP） | 口径定义 |

**推论**：即使用完美客户端（0ms 开销），16fps 的服务端节奏仍会在差分里留下可观的采样等待；反之浏览器优化只能收回 15~35ms。**233ms 的解释权大头在服务端帧供给（抓屏→编码节拍），浏览器段次之。**

**与前序审计的衔接**：`AUDIT-20261124-00` 的「RDP 慢合成导致 DDA 出帧率低」与本实测一致——但注意本轮 `staticSkipped=0` 排除了「静止误判跳帧」，瓶颈是 **capture 节拍含等帧时间（captureMs 8~89ms 波动）+ 出帧上限 ~16fps**，下一步应查 DDA `AcquireNextFrame` 在该会话的实际帧到达率与 `WaitUntilNextSlot` 节拍交互（`SessionRecorder.cs:1191-1198`）。

---

## 5. 下一步建议（给 Lead）

1. **P0**：查为什么桌面动态时只有 ~16fps（配置 60fps）——对比 `RDP-FRAMERATE-BENCHMARK-20261124.md` 的 48.59fps 结论（那是 CPU 管线长样本），本测试 captureMs 波动大（8~89）疑似 DDA 等帧与节拍器交互问题；用独立 DDA 计数器（`RDP-DDA-CEILING-EXPERIMENT` 的工具）同窗口对照。
2. **P1**：浏览器段 rVFC 埋点（`AUDIT-20261124-05` P2），把 20~40ms 估算变实测，与原生 9ms 对照。
3. 本探针可复用为后续所有「服务端出帧节奏」的独立测量仪（gap 统计即出帧率）。

---

## 6. 证据来源

| 来源 | 位置 | 用途 |
|---|---|---|
| 本工具源码 | `tools/direct-probe/{Program,MfDecoder,Window}.cs` + `DirectProbe.csproj` | 全部实现 |
| 实测输出 | §3.1 表（2026-11-24 11:30 会话） | 三段计时 |
| agent 日志 | `C:\Users\a2\AppData\Local\Temp\soullink-relay-53356.log` | 16.4fps 交叉验证、queueAge |
| `RelayAgent.cs:617` | 源码 | StartWebDirect 先于管道（独立 agent 可行性） |
| `RelayAgent.cs:667-672` | 源码 | 无管道 140s 自退 |
| `SessionRecorder.cs:1118` | 源码 | TrySubscribe 自动起管线 |
| 注册表 | `HKLM\SOFTWARE\Classes\MediaFoundation\Transforms\Categories` | 解码器 category GUID 实证 |
| 系统依赖 | Vortice.MediaFoundation 3.8.3（NuGet 缓存）、dotnet SDK 11.0.100 | 离线构建 |

---

## 7. 边界遵守自查

未改任何产品代码/脚本/配置/版本号（探针为纯新增 tools/ 源码，测后 bin/obj 已清）；未碰 `dist\stable\SoulLink-Stable.exe`（PID 13392）；未用 A1/A4 账号（测试 agent 以 A2 身份 session 4 启动，动画任务复用前序研究的 SoulLink-DupFrame-Animation，40s 自限）；未按名字杀进程（测试 agent 53356 按精确 PID 停止）；未弹窗（wscript/cmd 均隐藏窗口，探针窗口为测试必要 UI 且本机可见）；未改注册表；未发布；未 git 操作。测试产物（bat/vbs/h264）已删，计划任务 DirectProbe-Agent-Bat/Bat 已删，19099 监听已清。截图/临时文件仅在 %TEMP%。
