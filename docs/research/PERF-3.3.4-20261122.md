# SoulLink 性能实测报告（3.3.4 基线）

> 日期：2026-11-22
> 被测：`dist\SoulLink.exe` 3.3.3.0（PID 39056，运行 30 分钟，空闲挂机态）
> 配置：`EffectTier = 2`（低档 / 背景已冻结）
> 环境：32 逻辑核，本机 i9-13900HX
> 方法：`Get-Process.TotalProcessorTime` 差分 + `dotnet-stack` + `dotnet-trace`（SampleProfiler，15 秒）

---

## 0. 结论摘要

空闲挂机态稳定占 **约 16% 单核**（三次独立采样 14.1% / 17.5% / 15.9% 均值）。
构成分解：**约 12% 是 UI 线程 + WPF 渲染线程**，其余是后台枚举。

「偶尔卡卡的」有三个互相独立的来源，全部已定位到具体代码行：

| # | 问题 | 位置 | 症状 |
|---|---|---|---|
| 1 | 同步刷新在 UI 线程执行 | `RdpConsole` ×3、`DataCenterPage` ×1 | 连接/投送瞬间界面冻结 |
| 2 | 每秒一次全窗口文本重排 | `LaunchPage.OnLogCenterChanged` | 持续掉帧、自动子树更新 |
| 3 | 进程枚举反复执行 | `BgiLogWatcher.DiscoverCore` | 后台稳定吃 5~7% |

---

## 1. ⚠ 测量方法上的坑（先读这一条）

**第一次采样得到「279.65% 单核」是假数据。**

原因：`Get-Process` 返回的 `TotalProcessorTime` 在 `$p.Refresh()` 缺失时可能重复取值，
两个采样点落在同一时刻 → 分母极小、分子恒定 → 得出荒谬的百分比。

**正确写法**：

```powershell
function Sample([int]$procId, $ms) {
  $p = Get-Process -Id $procId
  $p.Refresh(); $a = $p.TotalProcessorTime.TotalMilliseconds
  Start-Sleep -Milliseconds $ms
  $p.Refresh(); $b = $p.TotalProcessorTime.TotalMilliseconds
  return ($b - $a) / $ms * 100
}
```

**交叉验证不可省**：拿「进程总 CPU / 进程存活时长」当平均基线对照。
本次平均 15.9%，与瞬时采样 14~17% 吻合，才可采信。

同类教训见 `docs/RELEASE-RULES.md` §11.2「采信数据前先确认工具开销」。

---

## 2. 线程级分解（`dotnet-stack` + `Get-Process.Threads`）

| 线程 | 累计 CPU | 折算平均 | 身份 |
|---|---|---|---|
| 33776 | 152.4 s | ~8.2% | **WPF 渲染线程**（无托管帧） |
| 39060 | 67.3 s | ~3.6% | **UI 线程**（`Dispatcher.GetMessageW`） |
| 162360 / 140820 | 12.0 / 10.5 s | ~1.2% | 线程池工作线程 |

渲染线程比 UI 线程还忙 —— 说明瓶颈在**光栅化/排版**，不在业务逻辑。

---

## 3. Trace 热点（15 秒，1370 采样点）

### 3.1 按 SoulLink 自有代码的**包含时间**排序

| 占比 | 函数 | 说明 |
|---|---|---|
| **7.15%** | `BgiLogWatcher.Discover()` | 被调用 5 次以上/轮，虽有 1 秒 TTL 仍在重复枚举 |
| **5.99%** | `BgiLogWatcher.DiscoverCore()` | `Process.GetProcessesByName` + 跨进程读 `MainModule` |
| **4.09%** | `BgiLogCenter.DiscoverCached()` | 2 秒 TTL，每月仍要全量枚举 |
| **3.80%** | `LowLevelMouseHook.Callback` | 见 3.3 |
| 2.34% | `LaunchPage.RefreshOverview` | 每 3 秒，`Task.Run` + `Dispatcher.InvokeAsync` |
| 2.34% | `LaunchPage.UpdateOverviewTexts` | 同上 |
| 2.26% | `LaunchPage.RefreshSessions` | 同上 |
| 2.04% | `LaunchPage.RefreshSelectedLog` | 同上 |

### 3.2 WPF 内部热点（自身时间）

| 占比 | 帧 | 判断 |
|---|---|---|
| 16.20% | `UNMANAGED_CODE_TIME` | 全在原生侧（光栅化 / D3D / 内核） |
| 6.79% | `CPU_TIME` | 采样器占位 |
| 3.65% | `ExceptionWrapper.TryCatchWhen` | 每帧走定时器回调都套一次异常包装 |
| **2.34%** | `Visual.PrecomputeRecursive` | 视觉树递归预计算 |
| **1.75%** | `AutomationPeer.UpdateSubtree()` | **UI 自动化子树重建**（WPF 经典性能杀手） |
| ~1% | `TextBlock.OnRender` → `SimpleTextLine.Create` | 文本行重新格式化 |

调用链证据（来自 trace 的父子边）：

```
MediaContext.RenderMessageHandlerCore
  → FireInvokeOnRenderCallbacks
    → ContextLayoutManager.UpdateLayoutCallback
      → UpdateLayout
        → fireAutomationEvents
          → AutomationPeer.UpdateSubtree      ← 子树反复重建
        → UIElement.Arrange
          → TextBlock.OnRender
            → SimpleTextLine.Create           ← 文本重新断行
```

**这就是「每秒整窗文本重排」的指纹。**

### 3.3 `LowLevelMouseHook.Callback` = 3.80%

Trace 显示它的父帧是 `Dispatcher.GetMessageW` —— **跑在 UI 线程的消息泵里**。

全仓库同时挂着 **4 个 `WH_MOUSE_LL` 钩子**：

| 文件 | 变量 |
|---|---|
| `Controls\RdpConsole.cs:1116` | `_mouseHook` |
| `Services\NativeHotkey.cs:10` | `_mouseHook` |
| `Views\ConsolePage.xaml.cs:3122` | `_fsMouseToggleHook` |
| `Views\ConsolePage.xaml.cs`（另一处） | `_fsMouseExitHook` |

每个系统鼠标事件 → 跨进程回调 4 次 → 每次都 `Marshal.PtrToStructure` + 分配一个
`MouseHookEvent` 类实例（GC 压力）。

---

## 4. 三个「偶尔卡」的确切来源

### 4.1 同步刷新跑在 UI 线程（冻结）★ 最影响手感

`AccountStateCenter.Kick()` 内部**同步**执行 `RefreshNow()` → `RefreshOnce()`，
后者依次做：`SessionService.QueryAsync`（WTS）、`BgiLogWatcher.Discover()`、
`ProcessOwnerSnapshot.Refresh()`（遍历 550 进程）、`BgiLogCenter.Tick()`（**文件 IO**）。

四个调用点，全部在 **UI 线程**：

| 位置 | 场景 | 线程依据 |
|---|---|---|
| `RdpConsole.cs:2465` | RDP 连接成功 | `ConnectAsync` 用 `ConfigureAwait(true)` |
| `RdpConsole.cs:2996` | 投送开始 | `BeginInputCore` 是 async，`ConfigureAwait(true)` |
| `RdpConsole.cs:2998` | 投送成功/失败 | 同上 |
| `DataCenterPage.xaml.cs:97` | 数据页「刷新」按钮 | WPF 事件处理器 |

**表现**：连上 RDP、投送代理的那几秒界面冻结 —— 正是用户操作最密集的时刻。

### 4.2 每秒一次全窗口文本重排（掉帧）

`LaunchPage.OnLogCenterChanged`（`BgiLogCenter.Changed` 的订阅者）：

```csharp
string text = TailTextOf(BgiLogCenter.SnapshotByUser(...));  // 最多 ~400 行拼串
if (text.Length > 0 && text != _logText) {
    _logText = text;
    LogText.Text = text;      // ← 整块 TextBlock 文本替换
    TrimLogText();            // ← 可能再赋值一次
    LogScroll.ScrollToEnd();  // ← 滚动 → 重排
}
```

`LogText` 是 `TextWrapping="Wrap"` 的 TextBlock，上限 60,000 字符。
每次赋值 → 全部断行 → 自动 `AutomationPeer` 子树更新 → 整窗 `PrecomputeRecursive`。

BGI 满速写日志时 `Changed` 每秒触发，于是**每秒一次全量重排**。

### 4.3 进程枚举（后台稳定开销）

`BgiLogWatcher.DiscoverCore()`：`Process.GetProcessesByName("BetterGI")`
+ 每进程 `MainModule.FileName`（跨进程读内存）+ 扫日志目录。
虽有 1 秒 TTL，但调用方多、且 `BgiLogCenter.DiscoverCached` 还有自己的 2 秒 TTL，
两条缓存互不相通，实际每秒仍有 1~2 次全量枚举。

---

## 5. 修复方向（按收益排序）

1. **`Kick()` 改异步 / 不阻塞调用方** —— 收益最大，直接消除冻结。
   注意 `_refreshing` 重入保护已存在，改成后台排队即可。
2. **`LogText` 增量追加**（保留滚动位置，只 append 新增行），或换成虚拟化控件。
   消除每秒整窗重排。
3. **鼠标钩子合并为一个**，回调里不做堆分配（改 struct 或复用实例）。
4. **`Discover` 合并缓存**，并移出 UI 线程。

---

## 5.1 本次已落地的修复（2026-11-22）

| # | 改动 | 文件 | 预期收益 |
|---|---|---|---|
| 1 | `Kick()` 改后台执行，不再阻塞 UI 线程 | `Services/AccountStateCenter.cs` | 消除连接/投送瞬间冻结 |
| 2 | 日志改增量追加，内容没变则零开销 | `Views/LaunchPage.xaml.cs`、`Services/LogTailAppend.cs` | 消除每秒整窗重排 |
| 3 | `Discover` TTL 1s→3s + 枚举串行化（双检锁） | `Services/BgiLogWatcher.cs` | 进程枚举 -2/3 |
| 4 | 鼠标钩子复用事件对象，不再每次 `new` | `Services/LowLevelMouseHook.cs` | 消除高频 GC 压力 |
| 5 | `Kick` 不再往 error.log 塞假异常 | `App.xaml.cs`、`AccountStateCenter.cs` | error.log 恢复可读 |

**回归测试**：`tools/layout-check/LogTailAppendCheck`（12 个用例，`ALL PASSED`）。
增量追加的算法有两个真实踩过的坑，都写进了用例：

1. 锚点落在换行符上却继续往后吃一行 → 最新一行永远不上屏；
2. 用 `IndexOf` 而非 `LastIndexOf` → 重复行被当成新增，界面成片重复。

---

## 6. 测量命令备查

```powershell
# 线程栈
dotnet-stack report -p <pid>

# CPU 采样（15 秒）
dotnet-trace collect -p <pid> --providers Microsoft-DotNETCore-SampleProfiler \
  --duration 00:00:15 -o sl.nettrace
dotnet-trace convert sl.nettrace --format Speedscope
```

> ★ `--profile cpu-sampling` 在本版本工具上会报
> 「The specified profile 'cpu-sampling' does not apply」，改用 `--providers` 显式指定。
