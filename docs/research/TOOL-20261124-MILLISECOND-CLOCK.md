# TOOL：ms-clock 毫秒级高刷时钟（浏览器 vs RDP 同框截图测延迟）

> 日期：2026-11-24
> 作者：rdp-session-analyst（dsh-team team_mwu35l8pae8oj5 / member m_mwu3790mafbish / task task_mwu609rwjh2qt2）
> 性质：新增独立工具 tools/ms-clock/（纯新增，未改任何产品代码）。exe 只在 A2(session4) 桌面启动验证，未在 console(session1)/A1/A4 会话残留任何进程。
> 证据分级：【实测】= 本会话真机测量；【代码事实】= 源码 file:line（本工具自建）。

---

## 0. 一句话结论

毫秒时钟工具已交付：tools/ms-clock/ 可构建（0 warning 0 error）、HH:mm:ss.fff 大字号等宽数字 + 实时帧率 + 每帧跳变色块 + 无边框置顶 + ESC 退出（实测验证退出成功）。本地 console 实测渲染 ~125fps；A2(session4) 经计划任务投递启动成功（SessionId=4 已核验）。

---

## 1. 选型理由

| 项 | 选择 | 理由 |
|---|---|---|
| 框架 | WPF（net11.0-windows，WinExe） | 与仓库同栈；无第三方依赖；TextBlock 渲染清晰、字体抗锯齿；DispatcherTimer 天然跑 UI 线程 |
| 渲染节拍 | DispatcherTimer(1ms, DispatcherPriority.Render) | Render 优先级在布局/渲染前排队，1ms 间隔让 Dispatcher 尽上限刷新（实测 ~125fps，证明渲染不构成瓶颈） |
| 时间显示 | DateTime.Now.ToString(HH:mm:ss.fff) + Consolas 96px | Consolas 等宽避免数字宽度抖动；fff 三位毫秒；大字号便于截图读数 |
| 帧率显示 | 每秒采样一次（500ms 滑动窗） | 显示渲染实际帧率，证明窗口在真刷 |
| 色块 | 6 色循环，每渲染帧跳变一次 | 在截图里肉眼分辨「这一帧更新了没」，避免两画面拍到同色块被误判同步 |
| 窗口 | WindowStyle=None（无边框）+ Topmost=true + NoResize + ShowInTaskbar=false | 置顶保证不被 RDP 窗口盖住；无边框省掉标题栏遮挡时钟 |
| 退出 | ESC（PreviewKeyDown）+ Alt+F4 兜底 | 用户明确要求 ESC；无边框窗口必须靠键盘焦点，故 Loaded 时 Activate()+Focus() 抢焦点 |

## 2. 构建方法

```powershell
cd tools\ms-clock
dotnet build -c Release --nologo
# 产物 bin\Release\net11.0-windows\ms-clock.exe（需 .NET 11 runtime）
# 构建后清理（遵守工作区整洁规则）：
Remove-Item -Recurse -Force tools\ms-clock\bin, tools\ms-clock\obj -ErrorAction SilentlyContinue
```

本会话实测：dotnet build 0 warning 0 error（首次报 CS0246 缺 using System.Windows.Controls，已补）。

## 3. A2 启动与精确 PID 停止

### 3.1 正确启动（跨会话投递，绝不能从 console 直接 Start-Process）

```powershell
$exe = 'E:\Documents\deepseek-harness\SoulLink\tools\ms-clock\bin\Release\net11.0-windows\ms-clock.exe'
schtasks /Create /TN SoulLinkMsClock /TR "$exe" /SC ONCE /ST 23:59 /RU LUMMY\A2 /IT /F
schtasks /Run /TN SoulLinkMsClock
Get-Process ms-clock | Select-Object Id,SessionId   # 必须是 4
```

### 3.2 停止

1. **首选：按 ESC**（窗口内键盘焦点时立即退出）；
2. 兜底 Alt+F4；
3. 精确 PID 停止：

```powershell
Get-Process ms-clock | Select-Object Id,SessionId
Stop-Process -Id <PID> -Force
schtasks /Delete /TN SoulLinkMsClock /F   # 若用了计划任务
```

## 4. 实测记录

### 4.1 本地 console 验证【实测】

- 构建：0 warning 0 error；
- 启动：窗口 900x320，无边框置顶；截图确认显示 HH:mm:ss.fff（12:07:11.001 类格式）、左上角 fps、右上角色块；
- 渲染帧率：约 125fps（本地 console，截图读到 fps 数值）；
- ESC 退出：发 WM_KEYDOWN/WM_KEYUP(VK_ESCAPE=0x1B) 后 1.5s，Get-Process 查 PID 已不存在 → **退出成功**；
- Alt+F4：OnKeyDown 处理 Key.System + SystemKey.F4 → Close()（代码路径具备，未单独人工按测，标【代码事实】）。

### 4.2 A2(session4) 投递验证【实测】

- 计划任务 SoulLinkMsClock 以 /RU LUMMY\A2 /IT 创建成功；
- /Run 后 Get-Process ms-clock 显示 Id=59552 **SessionId=4** → 确认窗口落在 A2 的 RDP 桌面（非 console）；
- 跨会话 PostMessage ESC 未能送达（跨会话无法向对方窗口站发消息，窗口句柄枚举为 0），改为精确 PID Stop-Process -Id 59552 -Force 停止成功；
- 计划任务已删除（schtasks /Delete SoulLinkMsClock /F 成功），无残留进程。

## 5. 复盘：这次为什么弹错会话（避免其他成员再犯）

1. **根因**：`Start-Process` 启动的 GUI 进程归属**当前进程所在的会话**。本子会话跑在 console（session 1），直接 Start-Process ms-clock.exe 就把窗口弹到了用户正在用的主桌面。
2. **正确姿势**：跨会话启动 GUI 必须用**计划任务投递**（/RU 目标账号 /IT 交互令牌，落到目标 RDP 会话桌面），参考仓库 AutoStart.cs 的 InteractiveToken + LogonTrigger 写法（Services/AutoStart.cs:130-136）。
3. **启动后必须验证 SessionId**：`Get-Process <pid> | Select-Object Id,SessionId`，确认是 4（A2）而不是 1（console）。
4. **最安全路径**：若无可靠跨会话投递手段，就让用户在 A2 桌面手动双击 exe，绝不在 console 里试启动。
5. **ESC 的跨会话限制**：跨会话无法用 PostMessage 发 ESC（窗口站隔离），停止只能按精确 PID；ESC 退出只在「目标会话内真正按下键盘」时才生效。

## 6. 边界遵守自查

- 纯新增 tools/ms-clock/{ms-clock.csproj, App.xaml.cs, README.md}，未改任何产品代码/脚本/配置/版本号；
- 未碰 dist\stable\SoulLink-Stable.exe；未用 A1/A4 会话；
- console 会话启动的 ms-clock 进程已按精确 PID 全部结束（事故窗口已清，无残留）；A2 投递的 59552 也已按精确 PID -Force 停止；
- 计划任务 SoulLinkMsClock 已删除；
- 未改注册表；未 git；未发布/COS/latest.json；
- 截图临时文件在 %TEMP%（ms-clock-local.png / ms-clock-local2.png）。

## 7. 使用说明（给用户，一句话）

在 A2 桌面双击 ms-clock.exe，窗口显示 HH:mm:ss.fff 时钟 + 帧率 + 跳变色块；让浏览器画面和 RDP 画面同框拍到它，多次截图取平均即差分延迟；按 ESC 退出。

---

## 8. 修订记录：fps 口径修正（2026-11-24 第二轮）

### 8.1 旧实现错误说明
- 旧版用 DispatcherTimer(1ms, DispatcherPriority.Render) 的 Tick 次数当「帧率」：计的是定时器回调次数，不是真实上屏帧数；且系统定时器默认粒度 15.6ms，导致 .fff 毫秒位只按 ~15.6ms 桶跳变。
- 上一轮报告 §4.1 写的「本地 console ~125fps」是**假帧率**（定时器计数），特此更正。

### 8.2 新实现
- 改用 CompositionTarget.Rendering 计真实渲染帧率（每次真实渲染帧触发，≈ 显示器刷新率），色块与渲染序号（窗口右上角 #N）与真实上屏帧同步；
- 启动即 timeBeginPeriod(1)，系统定时器粒度 15.6ms → ~1.5ms；
- 界面显示改为 fps: xx.x (render)；
- 新增 --bench <path> 自测：12 秒落盘 renderFps / renderIntervalMs p50/p95/max / sleep1ms 中位数 后退出。

### 8.3 console 与 A2 两地实测 fps 对比

| 环境 | renderFps | renderIntervalMs p50 | sleep1ms 中位数 | 判读 |
|---|---:|---:|---:|---|
| console（session 1） | 224.1 | 4.18ms | 1.51ms | 本地高刷 + timeBeginPeriod 生效 |
| A2（session 4，RDP） | 62.6 | 15.15ms | 1.50ms | RDP 会话 DWM 合成率封顶 ~60fps（与 RDP-DDA-CEILING 实测 p50=15.2ms 吻合） |

### 8.4 毫秒更新粒度验证
- 未 timeBeginPeriod：Thread.Sleep(1) 实际 ~15.6ms；
- timeBeginPeriod(1) 后：Thread.Sleep(1) 实测中位数 1.50~1.51ms（console 与 A2 一致）→ .fff 毫秒位从 15.6ms 桶跳变变为 ~1.5ms 级更新。

### 8.5 单文件发布坑
- 自包含单文件（SelfContained=true，64MB）在 A2 崩溃（计划任务 Last Result 0xE0434352，WPF 依赖裁剪）；
- 改框架依赖单文件（PublishSingleFile=true + SelfContained=false，约 86KB），A2 已装 .NET 11 WindowsDesktop 11.0.0，运行正常；
- 覆盖产物：C:\Users\A2\Desktop\毫秒时钟-测延迟.exe（86KB）。
