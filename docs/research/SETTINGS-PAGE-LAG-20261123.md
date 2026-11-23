# 进入「应用设置」页面卡顿根因归因（按卡顿贡献度排序）

> 日期：2026-11-23
> 成员：web-player（dsh-team / task `task_mwsrmsormrf5u7`）
> 性质：**纯研究**，不改代码、不 build、不碰运行进程。
> 范围：`Views/SettingsPage.xaml.cs` 的 `Activate()` 调用链 + 它同步触发的 `Services/SystemOptimizer.cs`、
> `Services/BgiUpdater.cs`、`Services/RouteUpdater.cs`、`Services/DataMigration.cs`、`Services/AutoStart.cs`。

---

## 0. 一句话结论

进入设置页卡顿，**不是某一个东西慢，而是 `Activate()`（`SettingsPage.xaml.cs:212`）在 UI 线程上
一口气串行执行了：1 次 powershell.exe 进程 + 6 次 sc.exe 进程 + 2 次 schtasks.exe 进程 +
至少 3 次递归扫目录 + 1 次旧目录递归枚举**。子进程冷启动是毫秒~秒级、`Get-MpPreference` 是百毫秒~秒级，
递归扫目录随文件数线性放大。全部在页面切换的同一帧内阻塞 UI 线程，卡顿是这些开销的**叠加**。

`Activate()` 是 `MainWindow.SetActivePage()`（`MainWindow.xaml.cs:1355` `case "settings": SettingsView.Activate();`）
的同步调用，页面切换时在 UI 线程同步执行，没有后台化。

---

## 1. 调用链（证据：`SettingsPage.xaml.cs:212-249`）

```csharp
public void Activate()               // :212  UI 线程同步入口
{
    _loading = true;
    ...
    ShowDataDirInfo();               // :227  ① 递归扫目录 ×3 + 旧目录枚举
    Services.LobbyApi.LoadFromConfig(); // :232 纯内存读，忽略
    VersionText.Text = ...;          // :240 反射读版本，忽略
    _loading = false;

    RefreshAccounts();               // :244  内存构造 AccountVm，可忽略
    RebuildBgiPathList();            // :245  WPF 元素构建，小
    _ = DetectAsync();               // :246  异步（昂贵部分已 await，安全）
    _ = RefreshAutoStartAsync();     // :247  名义 async，实为全同步（见 §4）
    RefreshSystemOptimize();         // :248  ② 6×sc.exe + ③ 1×powershell.exe
}
```

> 注意：`Loaded += LoadWebSettings`（:32）只在首次构造时触发；本应用 `SetActivePage` 只切 Visibility
> （AGENTS.md 规则 2），所以**每次进入设置页的开销 = `Activate()` 本身**，与 Loaded 无关。

---

## 2. 卡顿贡献度排序

| 排名 | 开销项 | file:line 证据 | 量级估算 | 修复建议 | 优先级 |
|---|---|---|---|---|---|
| **P0** | `GetDefenderExclusions()` → `RunPowerShellSync()` 起 powershell.exe 跑 `Get-MpPreference` | `SettingsPage.xaml.cs:1399` → `SystemOptimizer.cs:134-151` → `:273-308` | **500ms ~ 2s**。powershell.exe 冷启动 200~800ms + `Get-MpPreference`（AMSI 扫描）100~500ms，串行阻塞 | 后台 `Task.Run` 后回 UI 回填；或缓存 30s；或改 WMI 等非 PowerShell 源 | **最高** |
| **P0** | `QueryServices()` 串行起 6 次 `sc.exe query` | `SettingsPage.xaml.cs:1392` → `SystemOptimizer.cs:70-88` → `:192-224` | **60 ~ 300ms**。每次 `Process.Start("sc.exe")` + `ReadToEnd()` + `WaitForExit(5000)`，6 个进程串行 | 合并为**一次** `sc query`（列多服务）或并行后台化 | **最高** |
| **P1** | `ShowDataDirInfo()` 三次递归扫目录（`Directory.EnumerateFiles(..., AllDirectories)` + 逐文件 `FileInfo.Length`） | `SettingsPage.xaml.cs:1001/1003/1006` → `BgiUpdater.cs:656-669`、`RouteUpdater.cs:142-151`、`RouteUpdater.cs:154-168` | **10ms ~ 数百 ms**，随文件数线性。`bgi-backups` 改 zip 后文件少，但 `route-cache`/`route-backups` 可能成百上千文件 | 后台 `Task.Run` 统计；或只在用户点「显示占用」时才算；用 `EnumerateFiles` 累加省去二次 stat | **高** |
| **P1** | `DataMigration.Inspect()` 递归枚举旧目录 + 逐文件 `FileInfo.Length` | `SettingsPage.xaml.cs:1022` → `DataMigration.cs:60-118`（核心 :94-98） | **仅当旧目录存在且≠目标时**才有成本，同样全量递归，随文件数线性（旧 `%LOCALAPPDATA%` 目录可能上万文件） | 后台化；或惰性：只在用户点「迁移」时才 Inspect | **中** |
| **P2** | `RefreshAutoStartAsync()` 的「同步前缀」起 1~2 次 `schtasks.exe /query` | `SettingsPage.xaml.cs:247,885,889` → `AutoStart.cs:27-35,38-60,Run:180-199` | **20 ~ 100ms**。`IsEnabled()` 1 次 + （已开启时）`GetBoundUser()` 再 1 次，`WaitForExit(8000/10000)` 同步 | 把查询包进 `Task.Run`（EnableAsync/DisableAsync 已这么做了，唯独查询没做） | **中** |
| **P3** | `AccountScope.Accounts(App.Config)` 被重复调用 7 次，每次 `Where().ToList()` 全量重建列表 | `SettingsPage.xaml.cs:264,276,284,490,840,861,869` → `AccountScope.cs:23-27` | **微秒~毫秒级**（账号数小），纯浪费而非主卡顿源 | 一次调用缓存到局部变量，7 处复用 | 低 |

> 命名坑（值得记）：`RefreshAutoStartAsync` 是 `async Task` 且被 `_ =` 丢弃，但函数体里**直到
> `:896 await Task.CompletedTask` 之前没有任何真正的 await**——所以名字里的「Async」是假的，
> 整段（含 2 次 schtasks 子进程）都在调用线程（UI 线程）同步执行。同理 `DetectAsync`（:500）
> 的昂贵部分在 `:508 await BrokerProtocol.SendAsync` 之后，**它的同步前缀只有 `IsElevated`（P/Invoke，微秒级）**，
> 是安全的——两者对比正好说明「async 方法 ≠ 不阻塞 UI」要看第一个真实 await 在哪。

---

## 3. 为什么「sc.exe / powershell.exe / schtasks.exe」会卡 UI

`SystemOptimizer.QueryServiceState`（`SystemOptimizer.cs:192-224`）与 `RunPowerShellSync`（:273-308）、
`AutoStart.Run`（`AutoStart.cs:180-199`）都是 `Process.Start` + `StandardOutput.ReadToEnd()` +
`WaitForExit(ms)` 的**同步等待**模式，且在 `Activate()` 的同步调用链上被**逐个串行**调用：

- 子进程冷启动本身（CreateProcess + 加载）在 Windows 上 10~50ms；
- `powershell.exe -NoProfile -NonInteractive -Command Get-MpPreference` 因要加载 CLR + PowerShell 引擎 + AMSI，
  冷启动轻松 300ms 起，`Get-MpPreference` 自身还要枚举 Defender 配置；
- 这些耗时全部叠加在「页面切换」这一帧里，用户感知就是点击「应用设置」后界面冻结半秒~数秒。

---

## 4. 与既有规则的关系（`docs/research/UI-PERF-IO-TRIAGE-20261123.md` 的盲区）

上一轮 `ui-perf` 已经把**周期性/高频**同步 IO（每秒读日志、每秒写配置、RdpConsole 5 个定时器）迁走了，
并把剩余 93 处同步 IO 里的「用户点击触发」类判定为「保留」。但那次分类**漏掉了一类**：

> **「页面激活触发」的同步 IO**——不是周期任务、也不是用户点击，而是 `Activate()` 里每次进页都跑的
> 一次性但**串行多进程 + 多目录扫描**。它不属于「每秒发生」，但发生时机恰好在页面切换的关键路径上，
> 单次成本又远高于普通 `File.Exists` 校验。这正是本次设置页卡顿的来源。

对应 AGENTS.md 性能约束：

- 约束 2「页面可见性：`SetActivePage` 只切 Visibility，不触发 Loaded/Unloaded」——意味着 `Activate()`
  成为每次进页的**唯一**同步入口，任何同步开销都会在进页瞬间集中爆发，更凸显 `Activate()` 必须轻。
- 约束 1「新增周期任务走 TickHub」——本报告不新增周期任务，是建议把一次性重开销移到 `Task.Run` + 回填。

---

## 5. 修复建议汇总（按推荐顺序）

1. **`RefreshSystemOptimize` 整体后台化**（最高）：把 `QueryServices()` + `GetDefenderExclusions()`
   放进 `Task.Run`，结果 `Dispatcher.InvokeAsync` 回 UI 填列表；期间 UI 显示「检测中…」占位。
   这是消除 P0 两项目最直接的办法。
2. **合并/并行 sc.exe**：`QueryServiceState` 逐服务起进程改为一次性 `sc query` 解析多服务，
   或 `Task.WhenAll` 并行 6 个（稳妥起见合并为一次）。
3. **`ShowDataDirInfo` 的目录统计后台化**：占用统计对用户是低频信息，不必在进页瞬间算；
   后台算出后异步回填，或只保留「缓存 30s」。
4. **`RefreshAutoStartAsync` 真正异步化**：把 `AutoStart.IsEnabled()`/`GetBoundUser()` 包 `Task.Run`，
   并让函数体在第一个 await 之前不碰任何子进程（对齐 `EnableAsync`/`DisableAsync` 的既有写法）。
5. **`DataMigration.Inspect()` 惰性化**：进页只判断「旧目录是否存在」，全量递归统计留到用户点「迁移」时。
6. **`AccountScope.Accounts` 去重**：7 次调用合一，顺手省掉重复 `ToList()`。

---

## 6. 未验证项（诚实声明）

- 量级为**静态估算**（进程冷启动 + AMSI + 递归目录扫描的行业经验值），**未做运行时采样**：
  任务约束「不碰运行进程」，且按 `HANDOFF-20261123-LATENCY.md` 铁律不能碰 session 1 的 dist 主程序、
  不能起第二实例抢端口。要拿到精确毫秒数，可在干净环境对自构建产物加 `Stopwatch` 打点复测。
- `UpdateWebStatus()`（`LoadWebSettings` 触发，非 Activate 路径）未深查，不进入本报告范围。
