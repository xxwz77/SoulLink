# ms-clock —— 毫秒级高刷时钟（测延迟用）

浏览器画面 vs RDP 画面同框截图测延迟的标准件：窗口显示 HH:mm:ss.fff 大字号等宽数字、实时渲染帧率、每帧跳变的色块，无边框 + 置顶。放 A2(session 4) 桌面，让浏览器画面和 RDP 画面同时拍到同一个时钟，多次截图取平均，即可差分 SoulLink 与 RDP 的端到端延迟。

## 构建

```powershell
cd tools\ms-clock
dotnet build -c Release --nologo
# 产物：bin\Release\net11.0-windows\ms-clock.exe（WinExe，无控制台，需 .NET 11 runtime）
```

构建后请清理 bin/obj（遵守仓库工作区整洁规则）：

```powershell
Remove-Item -Recurse -Force tools\ms-clock\bin, tools\ms-clock\obj -ErrorAction SilentlyContinue
```

## 启动（只能在 A2 / session 4 的桌面，绝不能在 console 主用户桌面弹窗）

**血泪教训**：从 session 1（console）用 Start-Process 直接启动 GUI，窗口会弹到当前进程所在会话的桌面（即用户正在用的主桌面）。跨会话启动必须用计划任务投递。

### 方式一（推荐，跨会话投递到 A2）：

```powershell
$exe = 'E:\Documents\deepseek-harness\SoulLink\tools\ms-clock\bin\Release\net11.0-windows\ms-clock.exe'
schtasks /Create /TN SoulLinkMsClock /TR "$exe" /SC ONCE /ST 23:59 /RU LUMMY\A2 /IT /F
schtasks /Run /TN SoulLinkMsClock
# 验证 SessionId 必须是 4：
Get-Process ms-clock | Select-Object Id,SessionId
```

### 方式二（最安全，用户手动）：

让用户在 A2 的 RDP 桌面里直接双击 ms-clock.exe。

### 全屏模式：

命令行加 --fullscreen，ESC 同样退出。

## 关闭

1. 首选：按 ESC —— 键盘焦点在时钟窗口时按 ESC 立即退出。
2. 兜底：Alt+F4。
3. 精确 PID 停止（窗口无焦点/跨会话无法发 ESC 时用）：

```powershell
Get-Process ms-clock | Select-Object Id,SessionId
Stop-Process -Id <PID> -Force
```

（若用计划任务启动，用完记得删：schtasks /Delete /TN SoulLinkMsClock /F）

## 说明

- 渲染循环用 DispatcherTimer(1ms, DispatcherPriority.Render)：本地 console 实测约 125fps（证明渲染管线不构成瓶颈）；
- 在 A2（RDP 会话）内，DWM 合成率封顶约 60fps（DWMFRAMEINTERVAL=15 已设），故时钟数字在 RDP 会话内的实际更新粒度约 16.7ms，对毫秒级差分测量已足够；
- 色块每渲染帧跳变一次（6 色循环），用于在截图里肉眼分辨这一帧到底更新没更新；
- 帧率显示在左上角（绿色小字），时间在中央，色块在右上角。

## 修订记录：fps 口径修正（2026-11-24 第二轮）

### 旧实现错误说明
- 旧版用 `DispatcherTimer(1ms, DispatcherPriority.Render)` 的 Tick 次数当「帧率」：
  - 计的是**定时器回调次数**，不是真实上屏帧数；
  - DispatcherTimer 的 1ms 间隔受**系统定时器粒度（默认 15.6ms）**约束，实际回调约 64Hz；
  - 且 `DateTime.Now.ToString("HH:mm:ss.fff")` 的毫秒位在默认粒度下**只按 ~15.6ms 桶跳变**，对毫秒级差分测量不够精确。
- 上一轮报告里写的「本地 console 实测 ~125fps」是**假帧率**（定时器计数），不是渲染帧率，特此更正。

### 新实现
- 改用 `CompositionTarget.Rendering` 计真实渲染帧率：每次**真实渲染帧**（≈ 显示器刷新率）触发一次，色块与渲染序号（窗口右上角 `#N`）与真实上屏帧同步；
- 启动即 `timeBeginPeriod(1)`，把系统定时器粒度从 15.6ms 提到 ~1.5ms；
- 界面显示改为 `fps: xx.x (render)`，明确标注口径；
- 新增 `--bench <path>` 自测：12 秒后自动落盘（renderFps / renderIntervalMs p50/p95/max / sleep1msGranularityMs 中位数）并退出。

### console 与 A2 两地实测对比

| 环境 | renderFps | renderIntervalMs p50 | sleep1ms 中位数 | 结论 |
|---|---:|---:|---:|---|
| console（session 1，本地桌面） | **224.1** | 4.18ms | 1.51ms | 本地高刷屏 + timeBeginPeriod 生效，毫秒粒度 ~1.5ms |
| A2（session 4，RDP 桌面） | **62.6** | 15.15ms | 1.50ms | RDP 会话 DWM 合成率封顶 ~60fps（与 RDP-DDA-CEILING 实测 p50=15.2ms 吻合），毫秒粒度仍 ~1.5ms |

### 毫秒更新粒度验证数据
- 旧默认（未 timeBeginPeriod）：`Thread.Sleep(1)` 实际 ~15.6ms；
- 新（timeBeginPeriod(1)）：`Thread.Sleep(1)` 实测中位数 **1.50~1.51ms**（console 与 A2 一致）→ `.fff` 毫秒位从「15.6ms 桶跳变」变为「~1.5ms 级更新」，对差分测量足够。

### 单文件发布说明
- 自包含单文件（`SelfContained=true`，64MB）在 A2 崩溃（Last Result 0xE0434352，WPF 依赖裁剪问题）；
- 改为**框架依赖单文件**（`PublishSingleFile=true + SelfContained=false`，约 86KB），A2 已装 .NET 11 WindowsDesktop 11.0.0，运行正常；
- 覆盖产物：`C:\Users\A2\Desktop\毫秒时钟-测延迟.exe`（86KB，框架依赖单文件）。
