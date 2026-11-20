# SoulLink 交接文档 · 2.17.17（未定版）

> 固定时间：2026-11-20 12:40
> 版本号：**2.17.17.0**（**尚未定版，仍有 1 个未解决问题**）
> 性质：本轮从 2.17.16 出发，修 6 个问题，**最后一个改动被用户判定为修法错误**
> 建议：新会话先读第 5 节，再决定从哪里开始。

---

## 0. 一句话现状

修好了注入代理被断开、服务更新后热键失效、自由视角进出、CPU 单核满载、自动化快照过期五个问题。
**但「窗口内侧出现灰/白色粗框」这个 UI 问题没有修对**——本轮最后的改法被用户明确指出有错误，需要重做。

---

## 1. 产物与状态

| 项目 | 值 |
|---|---|
| 标准版 | `dist/SoulLink.exe` |
| 版本号 | 2.17.17.0 |
| 大小 | 67,475,253 字节 |
| SHA256 | `A49B2C5AFAA6468F77C60B70A996A5656846BD5AEF739EE64CD653A448787017` |
| **状态** | **未定版**，含一个错误的修复 |
| 固定目录 | `C:\Program Files\SoulLinkDev\SoulLink.exe` 2.17.17.0 · `0DC5ECC0...`（比 dist 旧一版） |
| 挂机版 | `dist/stable` **全程未动**，仍 2.17.4.0 `C83BDD34...` |
| Broker 服务 | `SoulLink.DevBroker` 当前 **Stopped** |
| 上一版归档 | `E:\SoulLink-SOURCE-ARCHIVE\v2.17.16-reliable\` |
| 本轮存档 | `E:\SoulLink-SOURCE-ARCHIVE\20261120-0410-checkpoint-2.17.16-diag\` |
| 备份 | `dist-backup-20261120-030303\`（原始 2.17.16）、`dist-backup-20261120-114918\` |

---

## 2. 本轮已修复（均已实测验证）

### 2.1 ★ 注入代理建连约 2 秒后被宿主主动断开（交接 4.1 结案）

**根因**：`Controls/RdpConsole.cs` `BeginInputCore` 在投送成功 + 探测完成后，有一句：

```csharp
if (!InputAllowed) { _ = StopInputAsync(); return; }
```

而会话监控投送前会显式 `SetForegroundActive(false)`（`ConsolePage.xaml.cs:1079`），
后台账号的 `_activeState` 恒为 1、`IsVisible` 恒为 false → `InputAllowed` 永远 false
→ **投送成功后必定自杀**。

**证据（三条独立来源互相印证）**：
- 客户端 input-trace：`DEPLOY_RESULT ok=True` → 1.6~2.3 秒后 `KILL_INPUTALLOWED` → `RL_SEND_EXIT`
- 代理侧日志：`已连上宿主管道` → 1.7 秒后 `宿主断开`
- Broker 服务端 `broker.log`：`AGENT_STARTED` 后紧跟 `RESPONSE_SENT STOPPED`

**修复**：删掉那三行，保留 `_inputUsable = usable; SyncRelayHotkey("代理就绪")`。
`InputAllowed` 在键鼠消息处理路径（`#870`/`#1388`）里仍然生效。

### 2.2 重装服务后热键失效

**根因**：`OnBrokerServiceUpdated` 对**所有** Console 调 `SetForegroundActive(false)`，
而该方法双职责：既停键鼠注入，又会清掉 `HotkeyArmed`、释放捕获、卸载钩子。
且执行后**没有任何恢复路径**（恢复只发生在 `ShowViewPageAsync`）。

**修复**：新增 `RdpConsole.InvalidateAgentConnection()`——只处置通道
（`StopInputAsync` 递增 generation、注销注册表、Dispose 旧 client），完全不碰前台状态。

### 2.3 自由视角进得去出不来

**根因**：进入自由视角 → 撤保护层 → 画面控件 `IsVisible=false`
→ `IsVisibleChanged` → `SetForegroundActive(false)` → 其中的 `_captured = false`
**把刚设上的捕获清掉**。这是"进入动作触发退出逻辑"的自毁回路。

**证据**：rdp-debug 连续 5 次都是「进入自由视角 captured=True」，没有一次「退出」。

**修复**：`SetForegroundActive(false)` 里，`_captured` 为真时只撤鼠标钩子直接 return。

### 2.4 切页/关弹窗后热键失效（死锁）

**根因**：`IsVisibleChanged` 的恢复分支要求 `_activeState == 0`，
但隐藏分支已把它设成 1 → **一旦隐藏过就永远恢复不了**。

**修复**：
- `RdpConsole` 恢复分支解除自锁，变可见时无条件装钩子/注册热键
- `ConsolePage.IsVisibleChanged` 页面可见时显式 `_viewing?.Console?.SetForegroundActive(true)`

### 2.5 CPU 约 1 核（交接 4.2 结案）

**实测归因（严格单变量，基线 = 全关 6.8%）**：

| 项 | 单核占比 |
|---|---|
| **`AuroraBackdrop` 梦幻星海动画** | **约 61 点** |
| **`GlowDot` 呼吸灯** | **约 27 点** |
| `DropShadowEffect` 阴影 | **0**（无关，最初判断错误） |
| `tasklist /V` 轮询 | 13 点（另一处问题，已单独修） |

**与 RDP 完全无关**：无 RDP 画面的探针也要 86%。

**修复**：
- `GlowDot`：画刷缓存冻结 + 频率 60ms→120ms
- `AuroraBackdrop`：**窗口失焦自动暂停**
- 新增**界面动效三档**（设置 → 行为）：高 92.4% / 中 22.7% / 低 9.1%

**教训**：不要用 `RepeatBehavior.Forever` 的 Opacity 动画做呼吸效果——
WPF 里无限动画会强制该元素每帧参与渲染，比 60ms 定时器更贵（实测反而升到 100%）。

### 2.6 自动化读到过期快照

**根因**：`ProcessOwnerSnapshot.Refresh()` 用 `tasklist /V /FO CSV`，
单轮 **1583ms**，而 `AccountStateCenter` 周期是 **1000ms**
→ 重入保护把大部分轮次跳过 → 自动化条件读到几十秒前的数据，且跑起来时吃满一核。

**实测对比**：
| 方案 | 单次耗时 |
|---|---|
| `tasklist /V` | 1583 ms |
| `tasklist` | 806 ms |
| **`Process.GetProcesses()` + `SessionId`** | **14~25 ms**（逐进程读 SessionId 为 0ms） |

**修复**：改为内存枚举，用户名由「会话号 → 用户名」映射补（`SessionUsers`，由 `AccountStateCenter` 每轮喂入）。
`Exists()` 支持只给用户名时反查会话号 —— **用户重登导致会话号变化，1 秒内自动跟上，无需改配置**。

改造后实测：每轮 15~19ms，**每秒都跑完，从不跳过**。

### 2.7 服务版本提示太烦

**用户要求原话**：「服务大于等于最低可用版本，就不应该让用户去更新，太麻烦了」。

**修复**：`DescribeAppVersionMismatch` 只判最低门槛（`2.17.13.0`），达到即视为正常、不再提示。
（原来只要服务版本 < 客户端版本就弹琥珀提示，即便远高于门槛。）

---

## 3. ★ 未解决：窗口内侧灰/白色粗框（本轮修错）

### 3.1 用户的完整描述（务必照此复现）

> - **只要不是高画质启动就会有**
> - 启动后**切到高画质会消失，切回中低没变化**
> - **重启又会有**
> - 启动的**一瞬间是没有的，过一两秒才出现**（我等 5 秒是为了稳定观察）
> - 框的**大小会自己动**，有时候一整个四边，**大部分时候残缺**
> - **焦点变化的时候这个框就会变，缺口时大时小，最终会稳定下来**
> - **缺口永远在底边与右边**
> - **聚焦灰色，失焦白色**
> - **很粗，不是 2px**（对比：系统强调色边框是 2px，它是 10px 以上）
> - **框在系统粉色边框的内部**
> - **应用应该是圆角的，在这个框的覆盖下变成了内部直角**
> - **关掉 RDP 自启，中低画质也没了**；但**应用切后台回来，会形成一整圈**
> - 截图：[用户 QQ 截图，框在粉色系统边框内侧]

### 3.2 已排除的假设（都做了实验，均无效）

| 假设 | 做法 | 结果 |
|---|---|---|
| `RootLayer.Margin` 造成内缩 | 启动路径设 14，后改回 0 | 白色残余数据**完全不变** |
| `WM_NCCALCSIZE` 返回 0 把客户区撑满 | 改为收缩保留边框厚度 | **完全不变** |
| DWM 边框色（失焦恢复系统默认=白） | `ResetBorderColor` 改 `ColorNone`（不画边框） | **完全不变** |
| 窗口位置/尺寸抖动 | 连续采样 8 次，rect 恒定 1613x1076 | 排除 |

**像素扫描数据**（1613x1076 窗口，低画质）：
```
顶部白色残余 x=4..1608（宽 1605，完整）
左侧白色残余 y=4..1071（高 1068，完整）
底部白色残余 x=5..91  （仅宽 87，残缺）
右侧无
白色 #f4f7fc，厚约 8px
```

### 3.3 ★ 本轮最后的改法（用户判定为错误）

我把根因归为「`WindowsFormsHost` 是独立 HWND，不参与 DWM 圆角裁剪」，
改了 `Controls/RdpConsole.cs`：

```csharp
// 构造时（原为 Children.Add(_host)）
_host.Visibility = Visibility.Collapsed;

// EnsureHost() 成功创建 OCX 后才挂
if (!Children.Contains(_host)) Children.Add(_host);
_host.Visibility = Visibility.Visible;
```

**用户回复：「你的修法有错误」——未说明具体错在哪。**

### 3.4 需要新会话做的

1. **先问用户"错在哪"**，不要重新猜。
2. 我的猜测仅供参考：截图上框仍在、或 RDP 画面显示受影响、或根本没解决。
3. 如果仍要沿"独立 HWND 盖住圆角"这条线，
   注意 `WindowsFormsHost` 的 `Visibility=Collapsed` 语义在 WPF/WinForms 互操作里**不等同于不创建 HWND**，
   可能需要在 `Dispose`/`ConnectAsync` 里显式 `Children.Remove(_host)`。

### 3.5 相关代码位置

| 关注点 | 文件 |
|---|---|
| 窗口根布局 | `Views/MainWindow.xaml` `RootLayer` / `Shell`（Margin 0，CornerRadius 0，BorderThickness 0） |
| 不透明模式 | `Views/MainWindow.xaml.cs` `ApplyOpaqueMode()` |
| WM_NCCALCSIZE | `Views/MainWindow.xaml.cs` `WndProc`（本轮加过收缩逻辑，**待评估是否保留**） |
| DWM 圆角/边框色 | `Services/WindowCorners.cs` |
| RDP 宿主 | `Controls/RdpConsole.cs` `_host`、`EnsureHost()` |
| 动画背景 | `Controls/Backdrop.cs` + `Controls/DreamScene.cs` |

---

## 4. 本轮代码里仍留着的"实验残留"（交付前需清理）

- `Views/MainWindow.xaml.cs`：`NCCALCSIZE_PARAMS` + `FrameResizeThickness()`（无效改动）
- `Services/WindowCorners.cs`：`ResetBorderColor` 用的是 `ColorNone`（无效改动，且改变原意）
- `Services/InputTrace.cs`：45 处埋点，已改成**默认关闭**（`--input-trace` 或 `SOULLINK_INPUT_TRACE=1` 才启用），零开销
- `Services/AccountStateCenter.cs`：`REFRESH_TIME` 计时日志
- `Views/MainWindow.xaml.cs`：`MAIN_ACT`/`MAIN_STATE`/`MAIN_DEACT` 埋点（当时为查窗口消失问题加的）

---

## 5. 本会话踩过的坑（血泪）

1. **`dotnet-stack` 栈采样不能用来推断 CPU 占比。**
   它每秒都会撞到 `RefreshOnce`（占 13%），让我误判它是热点。正确工具是
   `dotnet-trace collect --profile dotnet-sampled-thread-time`。
   （Windows 上没有 `cpu-sampling`，那是 Linux 专用。）

2. **在埋点里读 UI 属性会抛跨线程异常，把成功判成失败。**
   `IsVisible`/`Window.IsActive` 只在 UI 线程可读。我加的一段"兜底恢复前台资格"代码
   在后台线程读它们 → `The calling thread cannot access this object` →
   被 `catch` 吞掉后整条投送判失败，误导了好几轮。**埋点绝不能改变控制流。**

3. **`Select-String` 默认不区分大小写。** 我搜 `ARMED` 匹配到 `HotkeyArmed`，
   误判"日志里没有 ARMED 记录"。要加 `-CaseSensitive`。

4. **实验前先备份配置并事后恢复。** 我清空 `AutoConnectAccountIds` 做 CPU 实验后忘了恢复。

5. **不要用 `dotnet publish` 的输出直接被占用**：进程没退干净时 `Rename-Item` 会静默失败，
   导致测的还是旧二进制（我因此得到过一组错误数据）。

6. **截图验证时要先把目标窗口置顶**，否则抓到自己这个对话框。

---

## 6. 关键实测数据（供后续对照）

### 6.1 进程枚举方案

| 方案 | 单次耗时 |
|---|---|
| `tasklist /V /FO CSV /NH` | 1583 ms |
| `tasklist /FO CSV` | 806 ms |
| `Process.GetProcesses()` | 14~25 ms |
| 逐个读 `Process.SessionId`（551 个进程） | **0 ms，0 失败** |
| `query user` | 48~67 ms |
| `qwinsta` | 31~61 ms |

### 6.2 `RefreshOnce` 各阶段（改造后，稳定）

```
本轮总耗时 = 130~168ms
  ├─ query（query user + qwinsta）= 85~104ms
  └─ 进程快照（内存枚举）        = 14~19ms
刷新计数每秒 +1，从不跳过
```

### 6.3 CPU（窗口前台，无 RDP 画面）

| 档位 | 单核 |
|---|---|
| 高（完整动效） | 92.4% |
| **中（默认，推荐）** | **22.7%** |
| 低（全静止） | 9.1% |
| 窗口最小化 | 20.6% |
| 修复前（2.17.16） | ~100% |

---

## 7. 纪律（沿用）

1. 不要修改/回退 `dist/stable/`（挂机版），除非用户明确授权
2. 不操作 A1/A4；测试用 A2/A3
3. 按 PID + SessionId + 完整路径核对后才操作进程
4. 不往 RDP 画面上盖 WPF 浮层
5. 不给 `WindowsFormsHost` 设固定尺寸
6. 改完必须跑起来看日志/截图验证，不能只看编译通过
7. **改之前先做对照实验**，不要靠猜
8. **任何提前 return 都要留痕**
9. **不要用 `Encoding.Unicode.GetString` 搜单文件发布 exe 里的字符串**（启用了压缩）
