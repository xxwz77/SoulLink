# 桌面查看画面：游戏模式与输入/显示延迟诊断（2026-11-27）

## 路径判定

- Windows 原生控制台是 `Views/ConsolePage.xaml` → `RdpConsole`（WindowsFormsHost/RDP ActiveX），自由视角使用 `RawMouseInput` 的 WM_INPUT 相对位移 + `LookInputQueue` → RelayAgent 目标会话相对输入。它不等同于网页 Pointer Lock。
- PC 浏览器账号卡片“查看画面”走 `docs/web-index.html` 中 `#/remote/:id`，`/stream` H.264 → WebCodecs VideoDecoder → WebGL2/Canvas，鼠标经 `/ws/input`、退化至 `/api/input`。

## 确认的网页缺陷和修正

`plLockWanted` 原始初始化为 false；全仓源码/历史注释中只提到不存在的 `plEnterByButton`，没有任何实际入口置 true。因此网页 `pointerdown` 里的 `requestPointerLock()` 条件不可达，不能进入无限相对转视角。为账号卡片查看画面增加显式“游戏模式”开关：用户开启后下一次画布左键请求标准 Pointer Lock；成功后 `movementX/Y` 走现有 `moverel` 聚合发送，系统光标锁在画布、不会撞桌面边缘；Esc 按浏览器标准退出并补发一次远端 Esc，退出后自动解除模式，下一次画布点击恢复普通桌面左键；自定义逃生键纯解锁；失焦、隐藏、切账号、关画面/停流会主动解锁并 `releaseall`。只有安全上下文和 Pointer Lock 支持时才武装，未使用注入/钩子绕过游戏保护。

## WPF 独立路径

WPF 既有 FreeLook API 已有 `SetInputMode(FreeLook)`、物理相对 `WM_INPUT`（只读注册，不设 NOLEGACY、不拦截）、8ms 合并/有界按键队列。工具栏原先仅显示“自由视角中”，实际切换走用户配置的热键，不是网页实现。新增显式“游戏模式”按钮直接调用本账号 `RdpConsole.SetInputMode`，与现有热键并存；无会话时不执行。连续旋转不依赖光标坐标差分，Raw Input 用相对位移；应用窗口失焦时 RdpConsole `OnOwnerDeactivated` 调用 `ReleaseCapture`，它解除捕获/`CursorClip`、停 Raw Input 并异步 `ReleaseAllAsync`。全屏时普通工具栏折叠，但同一模式按钮也置入全屏悬浮顶部菜单；其 label/提示由同一个 `ApplyModeUi` 同步，故可从全屏顶部显式退出，已配置热键继续可用。

## 观测与延迟证据

网页新增只读内存样本 `window.__slInputSamples`（最多512项：moverel dx/dy、采样时刻、WS队列深度）；`window.__slVideoMetrics` 记录解码输出帧数、`decodeQueueSize`、输出时间、Canvas 绘制调用耗时、输出后下一 `requestAnimationFrame` 等待。注意这些是客户端解码/绘制/下一动画帧代理量，不是 GPU scanout/屏幕光子时间，也不包含服务端捕获时间。WPF `LookInputQueue` 统计已有累计发送 buttons/moves 与合并后排队数，8ms 窗口、位移累加不排队，按键队列至多64；无法从源码静态得到真实网络 RTT。

当前无授权的游戏实机互动、同网络/分辨率/帧率 A/B、松手残余运动或真实流会话，故没有可报告的真实 P50/P95，也没有把模拟值冒充测量。对照采样方案：同账号/同网络/固定分辨率和帧率，分别记录不少于数百次 move 与 up 的浏览器 `performance.now`/服务端响应、解码队列与绘制代理时间、release 后 100ms 位移；分别报告各路径 P50/P95、样本数、丢帧及残余位移分布。网页 `/api/input` fire-and-forget move 不取回响应，因此要量端到端 RTT需在用户授权的测试账号加服务端关联时间戳/只读日志，不能用浏览器发送时间伪造。

## 验证边界

- 网页主脚本由绑定 Node.js `node --check` 通过。
- `dotnet build SoulLink.csproj --no-restore` 两目标报告0个主工程编译错误，但构建目标中的 `tools/watchdog` publish 失败（已有多目标 `obj` 被不同 `BaseIntermediateOutputPath` 混合导致 CS0579 重复程序集属性；独立复现）。因此不能称全构建成功。
- 未运行/启动应用或服务，未连生产账号，未部署。工作区有其他并行未提交修改；网页文件亦有并行差异，本次只对读取后的明确字符串做小块修改，未重写文件。
- 模拟浏览器 Pointer Lock 事件和真实游戏行为未验收；浏览器 GUI 服务未监听本轮指定端口（未尝试重启）。
