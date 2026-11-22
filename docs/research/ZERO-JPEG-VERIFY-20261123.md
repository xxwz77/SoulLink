# H.264 原生链路验收：零 JPEG + 性能对比（2026-11-23）

> 日期：2026-11-23
> 作者：verifier（dsh-team `team_mws24h8h198lvov` / task `task_mws2jq8e1awkueb`）
> 范围：**只验证、只写文档**。未改任何业务代码，只修了自己 tools/ 下探针的两个 bug。
> 代码基线：工作区 HEAD `ae270a9` + 未提交改动，`SoulLink.dll` 时间戳 **02:00:41**。
> 证据标记：**【实测】**=本次原始输出可复跑；**【代码事实】**=源码可证；**【未实测】**=没测，不猜。

---

## 0. 结论摘要（先看这个）

| 验收项 | 结论 |
|---|---|
| 1. 代码层：实时链路零 JPEG | ❌ **未达成**：服务端 5 项仍为 JPEG（详见 §1）<br>✅ 网页侧 4 项已达成 |
| 2. 运行层：`/stream` ffprobe + CPU 对比 | ✅ `/stream` 通过；⚠ **CPU 基线只有"改前"，"改后"无法产生**（因为没改） |
| 3. 录像：无 `frame-*.jpg`、能出 mp4 | ✅ **通过**（`replay.mp4` 42 KB，ffprobe 读出 h264/50 帧） |
| 4. 静态截图仍可用 | ✅ **通过**（`shot.jpg` 54 KB） |
| 5. 文档 + README | ✅ 本文 |
| 6. 边界 | ✅ 全程遵守，见 §7 |

**一句话**：**这次验收的核心目标（服务端实时通路零 JPEG）在验收时刻还没达成——P1 任务 `task_mws2jkdm1av20hw` 于 02:03 开始动工，但 `SessionRecorder` 尚未改造，实时通路仍走 JPEG 中转。**
网页侧已经改好了，录像/截图功能健康。我把"改前基线"测准了，等 P1 落地后可直接对比。

> ⚠ 我**不能**把这份报告写成"通过"。按验收标准，第 1 项明确要求"实时链路里不存在 JPEG 编解码调用"，
> 而现在 `SessionRecorder` 存的还是 JPEG、`/stream` 还在 `DecodeJpegToNv12`。

---

## 1. 验收项 1：代码层零 JPEG【代码事实】

检查工具：`tools/stream-verify/check-no-jpeg.ps1`（可复跑）

```
== A1 抓屏不再产出 JPEG（接口 + 两个实现）
  FAIL  IScreenCapture 接口不再声明 CaptureJpeg  → 1 处
          IScreenCapture.cs:28  byte[]? CaptureJpeg(int quality, out int width, out int height);
  FAIL  SessionRecorder 实时抓帧不再调用 CaptureJpeg  → 2 处
          SessionRecorder.cs:222  var jpeg = Capture.CaptureJpeg(quality, out int cw, out int ch);
          SessionRecorder.cs:479  var jpeg = Capture.CaptureJpeg(Math.Clamp(quality, 30, 95), out int cw, out int ch);

== A2 环形缓冲不再只存 JPEG
  FAIL  Ring 的 Frame 不再以 byte[] Jpeg 存帧  → 1 处
          SessionRecorder.cs:115  public byte[] Jpeg = Array.Empty<byte>();

== A3 推流不再解码 JPEG
  FAIL  /stream 不再有 DecodeJpegToNv12  → 2 处
          AgentWebServer.cs:456  byte[] nv12 = DecodeJpegToNv12(cur);
          AgentWebServer.cs:481  private static byte[] DecodeJpegToNv12(byte[] jpeg)
  FAIL  /stream 推流路径不再取 GetLatestJpeg  → 3 处
          AgentWebServer.cs:369  try { jpeg = SessionRecorder.GetLatestJpeg(); } catch { }
          AgentWebServer.cs:414  jpeg = SessionRecorder.GetLatestJpeg();
          AgentWebServer.cs:453  byte[]? cur = SessionRecorder.GetLatestJpeg();

== A4 网页不再做 JPEG 重编码 / 不再有 JPEG 降级链
  PASS  Services/WebPages.cs 不再 toDataURL（只计真实调用，注释不算）
  PASS  Services/WebPages.cs 不再有「退回 JPEG」降级（只计真实调用）
  PASS  docs/web-index.html 不再 toDataURL（只计真实调用，注释不算）
  PASS  docs/web-index.html 不再有「退回 JPEG」降级（只计真实调用）

== A5 两份网页必须一致（WebPages.cs 由 html 生成）
  PASS  WebPages.cs 已与 web-index.html 同步

== B1 静态截图必须仍然可用（不能为了零 JPEG 删掉）
  PASS  shot 指令仍然存在
  PASS  CaptureOnce（单张截图）仍然存在

== B2 录像回放必须仍然可用
  PASS  save 指令仍然存在
  PASS  SaveRecording 仍然存在

RESULT=FAIL 5 项不满足
```

### 1.1 哪些调用还没被移除（逐条对照）

| # | 位置 | 现状 | P1 需要改成 |
|---|---|---|---|
| 1 | `Relay/IScreenCapture.cs:28` | 接口方法就是 `CaptureJpeg(...)`，返回 JPEG 字节 | 增加/替换为产出原始帧或 NV12 的方法 |
| 2 | `Relay/BitBltCapture.cs:116-142` | BitBlt → `bmp.Save(ms, JpegCodec(), ep)` 出 JPEG | 保留截图用的 JPEG 编码，实时路径改产原始帧 |
| 3 | `Relay/WgcCapture.cs:242,391-414` | 同上，WGC 也转 JPEG | 同上 |
| 4 | `Relay/SessionRecorder.cs:115` | `Frame { byte[] Jpeg }` —— Ring **只存 JPEG** | 改存 NAL 或原始帧 |
| 5 | `Relay/SessionRecorder.cs:222` | 实时抓帧循环调 `CaptureJpeg` | 改调产原始帧/NAL |
| 6 | `Relay/AgentWebServer.cs:369/414/453` | 推流前 `GetLatestJpeg()` 取帧 | 改取 NAL/原始帧 |
| 7 | `Relay/AgentWebServer.cs:456/481` | **`DecodeJpegToNv12(cur)`** 每帧解码 JPEG | 整段删除（这是最浪费的一跳） |

### 1.2 已经达成的部分（网页侧）

- `toDataURL` 与「退回 JPEG」降级链 **确已移除**，且**只剩注释**。
  这点需要特别说明：我第一次跑检查时被**自己的正则误报**了——
  `Services/WebPages.cs:919` 和 `docs/web-index.html:903` 里各有一行
  `// 旧版每帧 canvas.toDataURL('image/jpeg') 等于把省下的带宽又花回去`，
  这是**说明性注释**，不是调用。我已修正脚本为「只计非注释行」。
- `WebPages.cs` 与 `docs/web-index.html` **已同步**（`sync-webpages.ps1` 跑过了）。
  这条以前是缺陷（改了 html 但内置兜底常量是旧的），现在已修好。

> **这个误报值得记下来**：如果是人肉 grep，很可能就得出"网页还有 toDataURL，没改完"的错误结论。

---

## 2. 验收项 2：运行层 `/stream` + CPU 对比

### 2.1 `/stream` ffprobe【实测】✅

```
ok    代理已连上宿主管道，网页服务在 :19095 就绪
ok    环形缓冲有帧（画面真的抓到了）
ok    /api/info 可用
ok    /live 返回 JPEG 55667 字节 → live.jpg          ← ★ 注意这行，证明 /live 仍是 JPEG
/stream → HTTP 200 · 42922 字节
ok    输出以 Annex-B 起始码开头（00 00 00 01），符合 WebCodecs 输入约定

codec_name=h264
profile=Main
level=50
width=2560
height=1368
avg_frame_rate=25/1
ok    ffprobe 认出这是 H.264 流
```

> `/live` 仍返回 `image/jpeg` 55667 字节 —— 这是**第二个证据**说明实时通路没改（§1 的代码证据 + 这里的运行证据一致）。

### 2.2 采集侧 CPU【实测】

采样方法：`Process.TotalProcessorTime` 差值 ÷ 墙钟，单核百分比。
（遵守 AGENTS.md 性能约束第 3 条：**不**用 `Process.HandleCount` 做 1Hz 循环采样。）

**3 轮 × 10 秒，同一 agent 实例（PID 102496）：**

| 轮次 | CPU 时间 | 墙钟 | 单核占用 |
|---|---|---|---|
| 1 | 5859 ms | 10000 ms | **58.6 %** |
| 2 | 4531 ms | 10000 ms | **45.3 %** |
| 3 | 4125 ms | 10000 ms | **41.2 %** |

**单轮内（record 探针，另一次运行）：** 4140 ms / 10000 ms = **41.3 %**

**取稳态值（第 2、3 轮）≈ 41~45 % 单核**（第一轮偏高是启动爬坡）。

**同次采样的内存与句柄**（顺带记录，与内存泄漏任务互证）：
- WorkingSet **1710 MB**、句柄 **45398** —— 与 `MEMORY-LEAK-VERIFY-20261123.md` 记录的
  「`using var` 后仍泄漏、句柄暴涨」一致，**泄漏确实还在**。

### 2.3 ⚠ 关于「低于改前基线」这个要求

**这条现在无法完成，原因是逻辑性的**：
- 「改后」= P1 任务把 JPEG 链路换成原生 H.264。
- P1 于 **02:03 开始动工**（接口层 `IScreenCapture.cs:31` 已新增 `byte[]? CaptureBgra(out int width, out int height)`，`BitBltCapture`/`WgcCapture` 已实现），**但 `SessionRecorder`（0:02）尚未改造**，实时通路仍走 JPEG。见 §1。
- 所以现在测到的 41~45 % 单核 **就是"改前基线"本身**，没有"改后"可比。

我把它测准并留档，P1 落地后用**同一条命令**复跑即可对比。这正是这个基线值的用途。

> 不要拿 `STREAM-PERF-AUDIT` 里的"估算值"当改前基线——那份文档自己标了「
> 本节所有 CPU/带宽数字均为代码推导估算（未实测），不构成测量结论」。
> 本文的 41~45 % 是**实测**。

---

## 3. 验收项 3：录像验证【实测】✅ 通过

### 3.1 做了什么

起独立 agent → 下发 `record` 开始抓帧 → 下发 `shot` 静态截图 → 下发 `save` 保存录像（前 5 秒）。

### 3.2 结果

```
save：Ok=true  Message=已保存 50 帧 → replay.mp4（42 KB）  Count=50

目录：%TEMP%\recordprobe-19095\capture\录制回放\verify\2026-11-23-020146-test
文件清单（3 个）：
   meta.json      0 KB
   replay.mp4    42 KB
   shot.jpg      54 KB

ok    目录里没有 frame-*.jpg（录像中间 JPEG 序列已被清理）
ok    另有 1 个非 frame- 的 jpg（shot.jpg）—— 属静态截图，**允许保留**，不计入零 JPEG 指标
ok    产出 MP4：replay.mp4（42 KB）

== ffprobe replay.mp4
   codec_name=h264
   codec_type=video
   width=2560
   height=1368
   avg_frame_rate=15/1
   nb_frames=50
   format_name=mov,mp4,m4a,3gp,3g2,mj2
   duration=3.800000
   size=44244
ok    ffprobe 能读出该录像（含视频流）
```

**四项都符合验收标准**：
1. ✅ 目录里**没有** `frame-*.jpg`
2. ✅ 有 `replay.mp4`（42 KB）
3. ✅ ffprobe 能读出（h264 / 2560x1368 / 50 帧 / 3.8 秒）
4. ✅ 无 `.h264` 兜底、无 `transcode-error` 文件 → 说明 **MP4 转码成功**，不是"转码失败保留 jpg"

### 3.3 一个必须说清的判据细节

`shot.jpg` 和录像在**同一个目录**里（按 `日期-触发条件` 分目录，设计如此）。
我第一次跑时把这个 `shot.jpg` 误判成"JPEG 残留 → FAIL"。

**正确的判据**：
- **`frame-*.jpg`（录像中间序列）残留 = 不通过**
- **`shot.jpg`（静态截图）= 允许保留**，因为用户要的就是截图功能，且它是**一次性**的，不在实时通路上

这正是「零 JPEG」最容易被改过头的地方——把截图也一起删了就是功能回归。
所以检查脚本里 B1/B2 两组专门守护"截图和录像必须还在"。

---

## 4. 验收项 4：静态截图仍可用【实测】✅ 通过

```
shot：Ok=true  Message=已截图：...\录制回放\verify\2026-11-23-020146-test\shot.jpg
ok    截图生成 shot.jpg（54 KB）→ ...\shot.jpg
ok    截图是 JPG（这是**允许保留**的一次性功能，不是实时通路）
```

- 走的是 `RelayAgent` 的 `case "shot"` → `SessionRecorder.CaptureOnce()`。
- 产出的是 **JPG**（54 KB），符合"静态截图是例外"的设计。
- **P1 改造时必须保住这条**——如果为了零 JPEG 把 `CaptureOnce` 也改成 H.264，
  用户就没法"存一张图"了。

---

## 5. 改后复跑命令（P1 落地后用）

```powershell
$root = 'E:\Documents\deepseek-harness\SoulLink'
Set-Location $root
$ffprobe = 'C:\Users\91244\AppData\Local\Temp\ff-dl\x\ffmpeg-master-latest-win64-gpl\bin\ffprobe.exe'

# 0) 构建 + 暂存（★ 产物在「不带 win-x64」那级；runtimeconfig/deps 必须原名）
dotnet build SoulLink.csproj -c Release -f net11.0-windows10.0.26100.0 --nologo -v q
$src = "$root\bin\Release\net11.0-windows10.0.26100.0"
$dst = "$env:TEMP\sv-zerojpeg"
if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
New-Item -ItemType Directory -Path $dst -Force | Out-Null
Copy-Item "$src\SoulLink.exe" (Join-Path $dst 'SoulLink.Agent.exe') -Force
Get-ChildItem $src -Filter '*.dll' | ForEach-Object { Copy-Item $_.FullName $dst -Force }
Copy-Item "$src\SoulLink.runtimeconfig.json" $dst -Force
Copy-Item "$src\SoulLink.deps.json"          $dst -Force

# 1) 代码层：零 JPEG 检查（期望 RESULT=PASS，且 B1/B2 仍 PASS）
powershell -ExecutionPolicy Bypass -File "$root\tools\stream-verify\check-no-jpeg.ps1" -Verbose

# 2) 运行层 + 录像 + 截图 + CPU（一条命令全测）
& "$root\tools\stream-verify\bin\Release\net11.0-windows\stream-verify.exe" record `
    --dir $dst --port 19095 --token test123 --seconds 8 --ffprobe $ffprobe

# 3) 只需 /stream 时
& "$root\tools\stream-verify\bin\Release\net11.0-windows\stream-verify.exe" run `
    --dir $dst --port 19095 --token test123 --seconds 8 --ffprobe $ffprobe
```

**对比口径**：CPU 用同一台机器、同一会话、同一 content（静态桌面）、同样 10 秒窗口。
改前实测 **41~45 % 单核**（§2.2）。

---

## 6. 遗留问题

| # | 问题 | 严重度 | 归属 |
|---|---|---|---|
| 1 | **服务端实时通路仍是 JPEG**（Ring 存 JPEG、`/stream` 每帧 `DecodeJpegToNv12`） | **P0（本任务目标未达成）** | stream-core（P1 `task_mws2jkdm1av20hw`） |
| 2 | `/live` 端点仍返回 JPEG（55667 B） | P1 | 同 P1；需明确是删除还是保留但不在自动播放链 |
| 3 | 内存泄漏仍在（同次采样 WorkingSet 1710 MB / 句柄 45398） | P0 | stream-core（见 `MEMORY-LEAK-VERIFY`） |
| 4 | 画面全黑（DPI 帧池尺寸错） | P0 | stream-core（见 `STREAM-VERIFY-20261123.md` §4） |
| 5 | 采集侧 CPU 41~45 % 单核 | 待改后对比 | P1 落地后复跑 |
| 6 | 动态游戏画面的真实码率 | — | **【未实测】**（不用 A2） |

> 顺带说明：本次测试的 `replay.mp4` 只有 42 KB / 50 帧，是因为**静态桌面**内容几乎不变。
> 真实游戏画面的录像体积会大得多，**不能拿这个数字推断用户的实际录像大小**。

---

## 7. 边界遵守

| # | 约束 | 证据 |
|---|---|---|
| 1 | 不碰 `dist\stable\` | 时间戳仍是 `2026/11/22 14:24:48` |
| 2 | 不用 A1/A4 | 全程 session 1（console） |
| 3 | 不重启 session 1 主程序 | 全程独立 agent 实例（`%TEMP%\sv-zerojpeg`，端口 19095） |
| 4 | 不按路径通配停进程 | 只用记录的 PID |
| 5 | 不改业务代码 | `Relay/`、`Services/`、`docs/web-index.html` 的 `git status` 改动**均非本次验收所致** |
| 6 | 临时产物在 `%TEMP%` | 录像/截图写在 `%TEMP%\recordprobe-19095\` |
| 7 | 不 commit/push/发布 | 未执行任何 git 写操作，未碰 publish 脚本 |

### 本次顺手修的探针 bug（都出在我自己的工具里）

| Bug | 现象 | 修正 |
|---|---|---|
| 检查脚本把注释当代码 | `toDataURL` 匹配到注释行 → 误报"网页还有 JPEG" | 加注释行过滤，只计非注释行 |
| 录像探针未解 JSON Unicode 转义 | 路径含 `\u5F55\u5236\u56DE\u653E`（"录制回放"）→ `Directory.Exists` 为 false，误报"录像目录不存在" | 加 `UnescapeUnicode()` |
| 录像探针判据过宽 | 把 `shot.jpg` 误判成 JPEG 残留 | 只把 `frame-*.jpg` 算残留，`shot.jpg` 明确放行 |

---

## 8. 相关文档

- `STREAM-VERIFY-20261123.md` —— `/stream` 端到端验收（含画面全黑的 DPI 根因）
- `MEMORY-LEAK-VERIFY-20261123.md` —— 内存泄漏验证基座与回归清单
- `LATENCY-BENCHMARK-20261123.md` —— 延迟分段归因
