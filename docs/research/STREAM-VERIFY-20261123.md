# /stream 端到端验收报告（2026-11-23）

> 日期：2026-11-23
> 作者：verifier（dsh-team `team_mws24h8h198lvov` / task `task_mws24wkx19jephv`）
> 范围：**只验证、只写文档**。未改任何业务代码（`Relay/`、`Services/`、`Controls/`、`docs/web-index.html` 一行未动）。
> 代码基线：工作区 HEAD `ae270a9` + 未提交改动，构建产物 `SoulLink.dll` 时间戳 **01:47:41**。
> 证据标记：**【实测】**=本次原始输出可复跑；**【代码事实】**=源码可证；**【未验证】**=未测，不猜。

---

## 0. 结论摘要

| # | 验收项 | 结论 |
|---|---|---|
| 1 | 两个 TFM 构建 0 error | ✅ **通过**（均 EXIT=0） |
| 2 | `/stream` ffprobe + 解码 | ✅ **链路通过**（h264 / Main@5.0 / 2560x1368 / 25fps，ffmpeg 与 WebCodecs 都能解）<br>❌ **但 WGC 后端画面全黑**（BitBlt 后端正常，见 §4；根因已定位为 WGC 抓窗口却按整屏尺寸建帧池） |
| 3 | 缺 DLL 场景诊断 | ✅ **通过**（HTTP 500 + 明确中文 + 日志完整堆栈；此前的「连接重置」已修好） |
| 4 | 网页 WebCodecs 真的先跑 | ✅ **通过**（无头 Edge 实跑：解出 13 帧、已上 canvas、SPS 动态解析出 `avc1.4D4032`） |
| 5 | 文档 + README 登记 | ✅ 本文 |
| 6 | 边界（不碰 dist/stable、不用 A1/A4、不重启主程序） | ✅ 全程遵守，见 §7 |

**一句话**：**链路是通的、诊断是好的、网页是真在跑 WebCodecs 的；唯一挡在"能看到画面"前面的，是一个抓屏侧的 DPI 缺陷（§4），不在链路本身。**

---

## 1. 验收项 1：两个 TFM 构建 0 error【实测】

```
########## TFM 1: net11.0-windows ##########
dotnet build SoulLink.csproj -c Release -f net11.0-windows --nologo -v q
    2 个警告
    0 个错误
已用时间 00:00:02.47
EXIT=0

########## TFM 2: net11.0-windows10.0.26100.0 ##########
dotnet build SoulLink.csproj -c Release -f net11.0-windows10.0.26100.0 --nologo -v q
    152 个警告
    0 个错误
已用时间 00:00:04.48
EXIT=0
```

> 说明：交接文档记录的「HEAD 上 4 处 CS0103（Log 未定义）」**已修复**，本次两个 TFM 均 0 error。

---

## 2. 验收项 2：独立 agent 上的 `/stream` 实测【实测】

### 2.1 环境

| 项 | 值 |
|---|---|
| 会话 | **session 1**（console，我自己的会话，非 A1/A4） |
| 端口 | **19095**（独立实例，不碰 19080/19081 与用户主程序） |
| token | `test123` |
| 抓屏后端 | WGC（`recordstatus` 报 `capture=WGC`） |
| 系统内存 | 采样期间约 60~71% 占用 |

### 2.2 完整输出

```
recordstatus：{"Ok":true,"Message":"recording|fps=15|quality=75|frames=6|bytes=334002|total=6|size=2560x1368|session=1|capture=WGC",...}
ok    环形缓冲有帧（画面真的抓到了）
/api/info → 200 {"ok":true,"account":"verify","session":1,"pid":36300,"port":19095}
/live → 200 image/jpeg 55667B
/stream → HTTP 200 · 64132 字节
ok    输出以 Annex-B 起始码开头（00 00 00 01），符合 WebCodecs 输入约定

== ffprobe 输出 ==
[STREAM]
codec_name=h264
profile=Main
level=50
width=2560
height=1368
avg_frame_rate=25/1
[/STREAM]
[FORMAT]
format_name=h264
size=64132
bit_rate=N/A          ← 见 §2.4 说明
[/FORMAT]
ok    ffprobe 认出这是 H.264 流
```

### 2.3 解码证明【实测】

| 方式 | 命令 | 结果 |
|---|---|---|
| ffmpeg 重封装 | `ffmpeg -i stream.h264 -c copy out.mp4` | exit=0，输出 64.3 KB |
| **ffmpeg 真解码** | `ffmpeg -i stream.h264 -vf select=eq(n\,5) -vframes 1 frame-005.png` | **exit=0，解出 2560x1368 PNG（13.4 KB）** |
| **浏览器 WebCodecs** | 见 §4 | **解出 13 帧，首帧 2560x1368** |

**结论：码流是合规 H.264，两个独立解码器（ffmpeg 软件解码、浏览器硬解/软解）都能成功解码。** 这排除了「码流本身有问题」。

### 2.4 关于 bit_rate=N/A

ffprobe 对**裸 Annex-B 流**（无容器）不报 `bit_rate`，属正常——裸流没有容器级码率元数据。
需要码率时按 `文件字节数 / 时长` 自行计算：
- 本次 64132 B / 约 10 秒 ≈ **51 kbps**
- 该值**不能代表游戏动态画面**：本次测试的是静态桌面，画面几乎不变，编码器输出极少数据。
- 动态画面的真实码率**【未验证】**，需 A2 游戏会话才能测（我不使用 A2）。

---

## 3. 验收项 3：缺 DLL 场景诊断【实测】✅ 通过

这是之前明确记录的失败项（当时表现为「连接重置、日志无记录」）。现在**已修好**。

### 3.1 测试方法

复制一份 agent 到临时目录，**抽掉 `Vortice.MediaFoundation.dll`**，再启服务打 `/stream`。

### 3.2 实际响应（裸 TCP 抓原始字节）

```
HTTP/1.1 500 Internal Server Error
Content-Length: 132
Content-Type: text/plain; charset=utf-8
Server: Microsoft-HTTPAPI/2.0
Date: Sun, 22 Nov 2026 17:50:28 GMT
Connection: close

视频服务启动失败：Agent 目录缺少视频运行库（Vortice.MediaFoundation.dll 等），请补齐旁挂 DLL 后重试。
```

**三个关键点全部满足**：
1. ✅ **HTTP 500**（不是连接重置、不是无响应）
2. ✅ **明确中文诊断**，用户能看懂该补什么
3. ✅ 代理进程**仍存活**，`/api/info` 重置后仍返回 200（服务没崩）

### 3.3 日志完整【实测】

```
[web] 请求失败 path=/stream：System.IO.FileNotFoundException:
  Could not load file or assembly 'Vortice.MediaFoundation, Culture=neutral, PublicKeyToken=null'.
  File name: 'Vortice.MediaFoundation, Culture=neutral, PublicKeyToken=null'
  Requested by: SoulLink, Version=3.3.20.0
   at SoulLink.Relay.AgentWebServer.EnsureVideoDependencies()
   at SoulLink.Relay.AgentWebServer.HandleSafe(HttpListenerContext ctx)
Failed to open file '...\agent-nolib\Vortice.MediaFoundation.dll'. 系统找不到指定的文件。 (0x80070002)
[cached failure] ...
```

**新增的 `AgentWebServer.EnsureVideoDependencies()` 是这次修复的关键**——它把依赖加载提前到可捕获的位置，所以异常能进 `HandleSafe` 的 catch，从而写出 500 而不是让连接静默断掉。

---

## 4. ⚠ 验收中发现的问题：画面全黑（**仅 WGC 后端**，非进程级 DPI 问题）

### 4.1 现象【实测】

| 采集方式 | 尺寸 | 内容 |
|---|---|---|
| agent `/live`（JPEG） | **2560x1368** | **全黑**（2240 个采样点，非黑 **0 个**，中心像素 RGB = 0,0,0） |
| agent `/stream`（H.264 解码后） | 2560x1368 | **全黑**（`YAVG≈0.00007`，`SATAVG=0`） |
| 我的 PowerShell `Graphics.CopyFromScreen` | **1707x960** | **有内容**（能看到桌面 UI） |

> 注：`/live` 与 `/stream` 都黑，说明问题在**抓屏**这一跳，与 H.264 编码无关
> —— 两者在编码之前就已经是黑的。

### 4.2 根因【实测 + 代码事实】

> **★★ 2026-11-23 02:15 重要更正：本文档初版的根因是错的**
>
> 初版写的是「`SetProcessDPIAware()` 把进程降级为 system-aware，导致全局抓屏尺寸错」。
> **这个结论已被对照实验推翻。** 错误原因：我只测了 WGC 一个后端就下了进程级结论，
> 没做 BitBlt 对照。详见独立文档 `BLACKSCREEN-ROOTCAUSE-20261123.md`。

#### 4.2.1 三组对照实验（决定性）【实测】

同一会话（session 1）、同一时刻、同样桌面内容，三种抓法并排：

| 抓法 | 输出尺寸 | Y 均值 | 非黑占比 | 判定 |
|---|---|---|---|---|
| 本进程 `Graphics.CopyFromScreen`（绕过 agent） | 1707x960 | **247.4** | **100.0 %** | 有内容 |
| agent **BitBlt** 后端（`CAPTURE_MODE=0`） | 2560x1440 | **243.4** | **100.0 %** | 有内容 |
| agent **WGC** 后端（`CAPTURE_MODE=1`） | 2560x1368 | **0.0** | **0.0 %** | **全黑** |

**结论：全黑只发生在 WGC 后端，BitBlt 完全正常（画面、颜色都对）。**

这就否定了「进程级 DPI 上下文」假说——若是进程级问题，两个后端都会黑。
`RelayAgent.cs:523` 的 `SetProcessDPIAware()` 确实存在，但它**不是**本次全黑的原因。

#### 4.2.2 真正根因：WGC 抓窗口，却按整屏尺寸建帧池

`Relay/WgcCapture.cs` 两处尺寸来源不一致：

| 位置 | 代码 | 问题 |
|---|---|---|
| `WgcCapture.cs:207-211` | `TryCreateFromWindow()` 用 **`GetForegroundWindow()`** → 抓**单个前台窗口** | 抓的是窗口，不是整屏 |
| `WgcCapture.cs:115` / `:287` | 用 **`GetSystemMetrics(0/1)`** 取尺寸 | 返回**整屏物理像素** 2560x1368，不是窗口尺寸 |
| `WgcCapture.cs:123` / `:306` | `CreateFreeThreaded(..., w, h)` | 把整屏尺寸当帧池尺寸 |

**实测参数**：
- 当前前台窗口：`DeepSeek Harness`，矩形 `-7,-7 → 1714,920` ＝ **1721x927**
- 实际建出的帧池：**2560x1368**

**帧池（2560x1368）远大于实际抓取目标（1721x927）**，超出窗口的区域没有任何内容 → 全 0 → 整帧黑。

**代码从未比对 `item.Size`**：全文件 grep `item.Size` / `.Size.Width` **零命中**。
WGC 自己报的真实目标尺寸被完全忽略。

#### 4.2.3 为什么 stream-core 在 A1 实测是彩色的（两个结论都对）

关键变量是**前台窗口有多大**：

- 前台窗口**接近全屏**（如游戏最大化，或窗口≈整屏物理像素）
  → 帧池尺寸与窗口尺寸接近 → 画面基本正常 → **A1 抓原神是彩色**
- 前台窗口**明显小于整屏**（如我现在的前台是 1721x927 的窗口，整屏物理 2560x1440）
  → 帧池远大于窗口 → 大面积全 0 → **全黑**

所以这是一个**随前台窗口大小漂移的偶发缺陷**。这正好解释了为什么它会时好时坏、
让不同人得出相反结论。**不是谁测错了，是条件不同。**

#### 4.2.4 修复方向（**属 stream-core 的文件，我未修改**）

| 建议 | 说明 | 风险 |
|---|---|---|
| **首选**：帧池尺寸改用 `item.Size.Width/Height` | WGC 自己报的目标真实尺寸，天然正确，同时兼容"抓窗口"与"抓屏"两种意图 | 低 |
| 若意图是**抓整个屏幕**，改用 `CreateForMonitor` + 显示器真实尺寸 | 不要用 `GetForegroundWindow`——抓窗口意味着"用户一切换窗口就抓错" | 低 |
| 加自检：连续 N 帧全 0 记明确日志 | 现在是**静默产出黑帧**，用户只看到黑屏、日志里什么都没有 | 低 |

> ⚠ 我**没有**动这些文件。按团队约定只报告不修改。
>
> ⚠ 顺带收回另一句错话：初版写「用户高分屏 150% 缩放下必现」也不准确。
> 准确说法是「**前台窗口明显小于整屏物理尺寸时**必现」，与缩放比例无直接因果
> （缩放只是让窗口/物理像素比值变大，更容易触发）。
---

## 5. 验收项 4：网页 WebCodecs 真的先跑【实测】✅ 通过

### 5.1 自动化验证方式

用**无头 Edge 159**（`--headless=new`）真实加载一个验证页，页面自己按**产品页相同的解码逻辑**消费 `/stream`，再把结构化结果 POST 回来判定。

> 为什么不能只截个图：WebCodecs 是否真的跑，靠肉眼看截图无法区分
> 「解码成功画上去了」和「解码器报错但页面还是黑底」。

### 5.2 实测结果

```
== 页面内实测结果（浏览器真实执行）==
  WebCodecs 支持    : True
  HTTP 状态         : 200
  收到字节          : 10939
  NAL 数            : 31
  关键帧数          : 1
  从 SPS 解析 codec : avc1.4D4032
  解码器已配置      : True
  解出帧数          : 13
  首帧尺寸          : 2560x1368
  已画到 canvas     : True
  canvas 非全黑     : False
```

| 判定项 | 结果 |
|---|---|
| 浏览器支持 WebCodecs | ✅ PASS |
| `/stream` 返回 200 | ✅ PASS |
| 收到实质数据 | ✅ PASS |
| 解码器按流内 SPS 成功配置 | ✅ PASS |
| **真的解出了帧**（13 帧） | ✅ PASS |
| **帧被画进 canvas** | ✅ PASS |
| canvas 非全黑 | ❌ FAIL ← **源画面本身就是黑的（§4），不是网页问题** |

**结论：网页侧 WebCodecs 通路完全正常。** 最后一列 FAIL 是上游黑屏导致的，与网页逻辑无关。

### 5.3 SPS 动态解析已验证（解决此前的硬编码隐患）【实测】

之前交接文档记录的缺陷是「网页硬编码 `avc1.4D4028`（Main@4.0），实际流可能是别的 profile/level → configure 失败」。

现在**已改成从 SPS 动态解析**。我从实际码流里逐字节验证：

```
SPS 起始位置 = 6
NAL 头 = 0x67 (type=7)
byte1 profile_idc   = 77 (0x4D)   ← Main
byte2 constraint    = 64 (0x40)
byte3 level_idc     = 50 (0x32)   ← 5.0
网页将算出的 codec = avc1.4D4032  ← 与实际流一致
profile 在已知列表内 = True
```

而 ffprobe 独立报的是 `profile=Main / level=50` —— **两者一致**。
实测浏览器也确实用 `avc1.4D4032` 配置成功并解出 13 帧。

> **注意**：本次流是 **level=50（Main@5.0）**，不是 4.0。
> 如果网页还是硬编码 `avc1.4D4028`，这里必然失败。所以动态解析这一步是**必需的**，不是可选优化。

### 5.4 「失败时能退回」的现状【代码事实】

- 网页有 SPS 解析失败兜底：`codec || 'avc1.4D4028'` 并 `console.warn`
- `decoder.configure` 抛异常时有 `fail()` 上报
- ⚠ **但 JPEG 轮询降级链已按用户要求拆掉**（用户明确「不要图片 live，太卡了」）。
  所以现在**没有「退回 JPEG」的自动降级**——这是**有意为之**，不是缺陷。
  若 `/stream` 不通，页面会报错而不是悄悄退化成 JPEG。

---

## 6. 手动验收步骤（给用户看的 3 步）

自动化已覆盖到"解码出帧 + 画进 canvas"，但**「画面内容对不对」必须人眼确认**。用户按这三步即可：

### 最短手动验收 3 步

1. **打开页面**：浏览器访问 `http://<主机>:19080`，登录后进入目标账号的实时画面。
2. **按 F12 → Network，找 `/stream` 请求**：
   - 应该是 **一条持续的长连接**（`type=stream`，`Status 200`，大小持续增长），
     **不是**每帧一条 `/live` 请求。
   - 这就能一眼看出「走的是 H.264 流还是 JPEG 轮询」。
3. **按 F12 → Console，确认无红色报错**：
   - 应无 `[H264] decoder.configure 失败` / `解码器错误` / `流断开`。
   - 画面应该是**实时动态**的，不是一张静止图。

**判定标准**：
- ✅ Network 里 `/stream` 长连接持续增长 + Console 干净 + 画面在动 = **全链路正常**
- ❌ 画面全黑但 Console 干净 = **抓屏侧问题**（就是 §4 那个 DPI 缺陷），不是网页问题
- ❌ Console 有 `configure 失败` = profile/level 不匹配（§5.3 的兜底会打 warn）

### 无法自动化的部分（如实说明）

| 项 | 为什么自动化不了 |
|---|---|
| 画面**内容**是否正确（颜色/内容/不是黑屏） | 无头浏览器能验证"解出帧并画上 canvas"，但**判断不了内容是否是该账号的真实桌面**。本次 `nonBlank=False` 就是靠这个判出来的，但它只能报"黑/非黑"，不能报"对不对" |
| 真实 RDP 会话（A2）下的表现 | 我不使用 A2（团队约定），本次全在 session 1 |
| 游戏动态画面下的码率/流畅度 | 需 A2 游戏会话，**【未验证】** |
| 手机端实际观感 | 需真机 |

---

## 7. 边界遵守（逐条自查）

| # | 约束 | 证据 |
|---|---|---|
| 1 | 不碰 `dist\stable\SoulLink-Stable.exe` | 时间戳仍是 `2026/11/22 14:24:48`，全程未触碰 |
| 2 | 不重启 session 1 主程序 | 全程用**独立 agent 实例**（`%TEMP%\sv-verify-final`，端口 19095/19096） |
| 3 | 不用 A1/A4 | 全程 session 1（console，我自己的会话） |
| 4 | 不碰运行中的 agent 33372/41672 | 只按自己 `Process.Start` 的 PID 精确收尾 |
| 5 | 停进程不按路径通配 | 全部用记录下来的 PID；未使用任何 `Where Path -like` |
| 6 | 不改业务代码 | `git status` 里 `Relay/`、`Services/`、`docs/web-index.html` 的改动**均非本次验收所致**（是其他成员的在途修改），我一行未动 |
| 7 | 只在工作目录落地 | 新增 `tools/stream-verify/*`（探针）与本文档；临时产物在 `%TEMP%` |
| 8 | 不 commit/push | 未执行任何 git 写操作 |
| 9 | 不发布 | 未碰 `scripts/publish-release.ps1`、未碰 COS/latest.json |

---

## 8. 遗留问题清单

| # | 问题 | 严重度 | 归属 | 状态 |
|---|---|---|---|---|
| 1 | **WGC 后端画面全黑**（BitBlt 正常）：`WgcCapture` 用 `GetForegroundWindow()` 抓单个窗口，却用 `GetSystemMetrics()` 的**整屏**尺寸建帧池，导致帧池远大于实际目标、超出区域全 0。**随前台窗口大小漂移，故偶发** | **P0（挡住"能看到画面"）** | stream-core（`Relay/WgcCapture.cs:115/123/207-211/287/306`） | **已定位根因，未修**（我不改业务代码） |
| 2 | 内存泄漏 | P0 | stream-core | **未修复**。详见 `MEMORY-LEAK-VERIFY-20261123.md`：用含 `using var` 的新构建实测仍 46.3 MB/s（R²=0.998），且**句柄漏 1454 个/秒**（新发现） |
| 3 | 抓屏全黑时**静默产出黑帧**，无任何日志 | P1 | stream-core | 建议加「连续 N 帧全黑」告警 |
| 4 | `SOULLINK_CAPTURE_MODE=0` 不生效（实测仍报 `capture=WGC`） | P2 | stream-core | 可能因 `SessionRecorder.Capture` 是静态字段，初始化早于环境变量读取。未深入验证 |
| 5 | 动态游戏画面下的真实码率 | — | — | **【未验证】**（不用 A2） |
| 6 | 网页 JPEG 轮询降级链 | — | — | 已按用户要求**有意拆除**，非缺陷 |

---

## 9. 复跑方法

工具均在 `tools/stream-verify/`（可构建，`dotnet build -c Release` 0 error）。

```powershell
$root = 'E:\Documents\deepseek-harness\SoulLink'
Set-Location $root
$ffprobe = 'C:\Users\91244\AppData\Local\Temp\ff-dl\x\ffmpeg-master-latest-win64-gpl\bin\ffprobe.exe'
$ffmpeg  = 'C:\Users\91244\AppData\Local\Temp\ff-dl\x\ffmpeg-master-latest-win64-gpl\bin\ffmpeg.exe'

# ① 暂存独立 agent（★ 产物在「不带 win-x64」那级；runtimeconfig/deps 必须原名）
$src = "$root\bin\Release\net11.0-windows10.0.26100.0"
$dst = "$env:TEMP\sv-verify-final"
if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
New-Item -ItemType Directory -Path $dst -Force | Out-Null
Copy-Item "$src\SoulLink.exe" (Join-Path $dst 'SoulLink.Agent.exe') -Force
Get-ChildItem $src -Filter '*.dll' | ForEach-Object { Copy-Item $_.FullName $dst -Force }
Copy-Item "$src\SoulLink.runtimeconfig.json" $dst -Force
Copy-Item "$src\SoulLink.deps.json"          $dst -Force

# ② 端到端（含 ffprobe）
& "$root\tools\stream-verify\bin\Release\net11.0-windows\stream-verify.exe" run `
    --dir $dst --port 19095 --token test123 --seconds 10 --ffprobe $ffprobe

# ③ 缺 DLL 诊断
& "$root\tools\stream-verify\bin\Release\net11.0-windows\stream-verify.exe" nolib `
    --dir $dst --port 19096 --token test123

# ④ 网页 WebCodecs 无头验证
powershell -ExecutionPolicy Bypass -File "$root\tools\stream-verify\verify-webcodecs.ps1" `
    -AgentDir $dst -Port 19095 -Token test123 -Ms 8000

# ⑤ 把码流解成图片人眼确认
& $ffmpeg -hide_banner -v error -y -f h264 -i "$env:TEMP\stream-verify-19095\stream.h264" `
    -vf "select=eq(n\,5)" -vframes 1 "$env:TEMP\frame-005.png"
```

---

## 10. 相关文档

- `MEMORY-LEAK-VERIFY-20261123.md` —— 内存泄漏验证基座（量具 + 判据 + 基线 + 回归清单）
- `LATENCY-BENCHMARK-20261123.md` —— 延迟分段归因（采集 12.14fps / 推流 13.56fps）
- `HANDOFF-STREAM-STATUS-20261123.md` —— `/stream` 状态与端口/鉴权坑
- `STREAM-LATENCY-TEST-20261122.md` —— 软编/硬编端到端延迟
