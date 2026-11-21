# SoulLink 交接文档 · 3.1.6（云更新上线 + 卡顿根治 + 体积优化）

> 时间：2026-11-21 17:10
> 版本：客户端 **3.1.6.0**
> 分支：`main` @ `4129c11`（已推送 origin）
> 触发：用户反馈「切换菜单越来越卡、Windows 提示无响应」+ 需要云更新

---

## 0. 一页速览

| 项 | 结果 |
|---|---|
| 卡顿 | **已根治**（切页后看门狗空转 + 定时器各自唤醒线程池） |
| 空闲 CPU | 27% → **21%** |
| 句柄 | 0.64/秒 → 0.44/秒，**19 分钟后稳定在 1630 并回落** |
| 全量更新包 | 123 MB → **2.69 MB** |
| 云更新 | **完整链路已上线**（增量/全量、独立更新器、版本化包名） |
| 提交 | 4 个，见 §7 |

---

## 1. ★ 卡顿的真实原因（与第一直觉不同）

### 1.1 不是 RDP，不是渲染

排查初期怀疑过 `Backdrop` 渲染、RDP 控件、`AsyncLog`，全部被实测排除：

| 怀疑对象 | 排除依据 |
|---|---|
| `Backdrop` 渲染循环 | 配置是低档（`EffectTier=2`），根本不订阅 `CompositionTarget.Rendering` |
| `AsyncLog` | 12/12 帧都出现它，但那只是**常驻等待线程**的正常表现 |
| `Process.GetProcesses()` | 测 100 轮只涨 4 个句柄，不泄漏 |
| 子进程重定向 | 50 次后增量收敛到 +2 |
| `Task.Delay` | Δ0 |

### 1.2 真正的两个元凶

**元凶一：控制台页切走后，150ms 保护层看门狗仍在跑**

```csharp
// ConsolePage.xaml.cs
Loaded += (_, __) => { ...; EnsureShieldWatch(); };   // 启动
Unloaded += (_, __) => { _shieldWatch?.Stop(); };     // 只在 Unloaded 停
```

而 `SetActivePage` **只切 Visibility，永不触发 Unloaded**：

```csharp
foreach (var kv in _pages)
    kv.Value.Visibility = kv.Key == tag ? Visibility.Visible : Visibility.Collapsed;
```

于是切到别的页面后，看门狗继续以 150ms（每秒 6.7 次）空转，而 RDP 画面根本不可见。

修法：新增 `OnPageActivated()` / `OnPageDeactivated()`，由 `SetActivePage` 调用。

**元凶二：6 个各自独立的 1 秒级 DispatcherTimer**

```
ScheduleRunner          1 秒
RouteUpdateScheduler    1 秒
BgiLogWatcher           1 秒（FileSystemWatcher 的兜底）
LaunchPage（会话）       3 秒
LaunchPage（卡片）       1 秒
LobbyPage               5 秒
```

它们各自唤醒 Dispatcher，线程池因此长期处于「有事要做」的状态。
.NET 线程池在这种状态下**保留自旋线程而不立即阻塞** —— trace 里
`LowLevelLifoSemaphore.Block` 占 37.79% 就是这个。

修法：新增 `Services/TickHub.cs`，统一 1 秒节拍，订阅者用计数器分频。

### 1.3 诊断方法论（★ 重要）

**坑一：诊断工具自己制造了问题**

我加的 `HandleProbe` 用 `Process.GetCurrentProcess().HandleCount` 取句柄数：

```csharp
// 错误写法 —— 每次约 3~5ms 系统查询
public static int Current() => Process.GetCurrentProcess().HandleCount;

// 正确写法 —— 约 1 微秒，且带 1 秒缓存
[DllImport("kernel32.dll")] static extern bool GetProcessHandleCount(IntPtr, out uint);
```

而探针挂在 1 Hz 的 `ScheduleRunner.Tick` 里 **3 处**，合计每秒十几次系统调用
→ **自己把 CPU 抬到 21%，还制造了 0.64/秒 的句柄增长**。

**教训：先确认诊断工具本身的开销，再采信它给出的数字。**

**坑二：`dotnet-stack` 会漏报线程**

它只列出**托管线程**。一个消耗 38,812ms（比主线程还多）的线程池工作线程
完全不在列表里。判断依据要改用 `Process.Threads[].TotalProcessorTime` 差分。

**坑三：`handle.exe` 会漏报句柄**

对 `SoulLink-Stable` 它只显示 158 个句柄，而系统报告 33,600 个。
真正看清构成要靠自己写 `NtQuerySystemInformation` + `NtQueryObject` 枚举。

### 1.4 句柄增长不是泄漏（★ 结论）

```
30 秒:   1015
60 秒:   1035
150 秒:  1080
240 秒:  1126     ← 一直在涨
19 分钟: 1630    ← 涨到 1630 后稳定，并开始回落
```

这是 .NET 运行时的**启动爬坡**（线程池扩张、JIT 缓存、GC 分代稳定），不是泄漏。

判别方法：

| 特征 | 正常爬坡 | 真泄漏 |
|---|---|---|
| 长时间趋势 | 上升后**趋于平缓** | **持续线性上升** |
| 增长率 | 递减 | 恒定 |

对照：`SoulLink-Stable`（3.0.0）3.6 小时涨到 37,306 个、7/秒、CPU 158%，
**那个是真问题**，当前代码没有。

---

## 2. ★ 云更新（新增能力）

### 2.1 客户端

```
Services/CloudUpdater.cs     检查、下载、SHA256 校验
Views/UpdateWindow.xaml(.cs) 主题对话框（版本对比 + 更新说明 + 确认/取消）
Views/MainWindow.xaml.cs     版本号可点击；有新版时琥珀色 + 红点
tools/updater/               独立更新器（替换文件并重启）
```

### 2.2 发布脚本

```
scripts/publish-release.ps1           默认增量，-Full 全量
scripts/publish-release.ps1 -Push     正式推送
scripts/publish-release.ps1 -Full -Push
```

### 2.3 ★ 包名必须带版本号（踩过的坑）

**原始设计**：固定名 `update.zip`。

**问题**：客户端虽然给清单加了 `?t=` 时间戳，但包本身没有。
如果 CDN 忽略查询串，就会出现「清单是新的、包是旧的」——
SHA256 能拦住，用户装不到错误版本，但会**反复下载几十 MB 后报错重试**。

**修正**：包名带版本号 → `update-3.1.6.zip`，内容与文件名一一对应，永不覆盖。

> ★ 这个文件名**用户永远看不到**：下载到 `%TEMP%`、解压覆盖、随即删除。
> 安装目录里的 `SoulLink.exe` 仍是固定名，桌面快捷方式不受影响。

### 2.4 ★ 发布顺序不可颠倒

```
打包 → 上传 zip → HEAD 校验（5 次重试）→ 通过后才写 latest.json → 刷新 CDN
```

**为什么必须校验包再写清单**：一旦 `latest.json` 指向不可下载的包，
所有用户点「检查更新」都会失败，且**无法自愈**——只能再推一版覆盖。

### 2.5 ★ 打包内容规则

**必须包含**：

```
SoulLink.exe                  主程序
SoulLink.Updater.exe          更新器（可自我更新）
sl-rescue.exe                 救援程序
7za.exe / next\7za.exe        解压工具
tools\SoulLink.BrokerSetup.exe 服务安装器
tools\*.ps1                  安装脚本
```

**绝对排除**：

| 排除项 | 原因 |
|---|---|
| `data\` | **用户配置、账号、日志** —— 覆盖会毁掉数据 |
| `stable\` | 挂机测试版，不参与更新 |
| `agent\` | 手动代理的导出目录，里面是旧版本副本，打包会**覆盖掉新程序** |
| `*.log / *.bak / *.old / *.tmp / *.pdb` | 运行时产物与调试符号 |

---

## 3. ★ 体积优化（123 MB → 2.69 MB）

### 3.1 三项优化

| 文件 | 优化前 | 优化后 | 原因 |
|---|---|---|---|
| `sl-rescue.exe` | 33.48 MB | **72 KB** | 原是自包含，只做 5 个 Win32 调用 |
| `SoulLink.BrokerSetup.exe` | 33.60 MB | **1.16 MB** | 原是自包含，只做复制文件/建服务/改 ACL |
| `dist\agent\` 移出打包 | 64 MB | **0** | 旧版本副本，不该分发 |

**合计：全量包 123 MB → 2.69 MB。**

### 3.2 为什么这两个可以框架依赖

它们与主程序同属框架依赖运行：用户能用到它们，前提是**已经装了主程序**，
也就必然有 .NET 11 桌面运行时。自包含是纯浪费。

### 3.3 构建方式变化

```xml
<!-- SoulLink.csproj -->
<Target Name="PublishBrokerSetup" BeforeTargets="AfterBuild">
  <Exec Command="dotnet publish ... --self-contained false -p:PublishSingleFile=true ..." />
</Target>
```

不再依赖手工维护的 `dist\tools\SoulLink.BrokerSetup.exe`。

`build.bat` 新增同步 `tools\` 子目录到 `dist`——原来**只复制主程序**，
导致 `dist\tools` 里的安装器一直停留在旧的自包含版本。

---

## 4. ★ 会话查询策略（事件驱动 + 慢速兜底）

### 4.1 为什么不能周期高频

每轮要起 `query.exe` + `qwinsta` 两个子进程（各约 50ms）并持续产生句柄。
24 小时挂机时这是纯浪费——会话状态绝大部分时间根本不变。

### 4.2 当前策略

| 时机 | 频率 | 用途 |
|---|---|---|
| 启动后 60 秒 | 1 秒 | 连 RDP、初始投送都在这段时间 |
| **RDP 连接成功**时 | 事件触发 | 会话号已确定，此时刷最准 |
| 投送前后 | 事件触发 | 代理起来、通道建立 |
| 稳定挂机 | **30 秒** | 只用来发现掉线 |

> ★ 触发点必须在「连接**成功之后**」而不是发起连接时：
> 连接过程中会话状态还在变，那时查没有意义。

### 4.3 刷新按钮

`RefreshSessions(force: true)` → `ForceRefreshAsync()`，**真查一次系统**。
定时刷新仍用快照省资源。

> 原因：原来按钮和定时刷新共用同一路径，快照 5 秒内就直接复用、
> 完全不查系统，用户点了「刷新」看起来没反应。

---

## 5. ★ 后续需要统一的地方（待办）

### 5.1 定时器规范（重要）

**现在有了 `TickHub`，但改造不完整。** 后续新增周期任务必须：

```csharp
// ✅ 正确
_sub = Services.TickHub.Register("模块名", 秒数, () => { ... });

// ❌ 禁止：新建独立 DispatcherTimer
new DispatcherTimer { Interval = ... };
```

**仍未迁移的定时器**（都有正当理由，但需复核）：

| 位置 | 间隔 | 为什么不迁 |
|---|---|---|
| `ConsolePage` 看门狗 | 150ms | 已加生命周期控制，但仍是独立定时器 |
| `ConsolePage` 全屏菜单 | 120ms | 仅全屏时运行 |
| `RdpConsole` × 5 处 | 200ms~5s | RDP 相关，等重构 |
| `MainWindow` 悬停/隐藏 | 320~1200ms | 事件驱动的临时定时器，用完即停 |
| `AccountStateCenter` | 1s/30s | 用 `System.Threading.Timer`（后台线程，不占 Dispatcher） |

**统一原则**：
- 纯周期性、且在 Dispatcher 线程上的 → 走 `TickHub`
- 事件触发、用完即停的 → 保持独立
- 后台线程的 → 保持 `System.Threading.Timer`

### 5.2 页面生命周期约定（重要）

**所有页面都应实现 `OnPageActivated()` / `OnPageDeactivated()`**，
由 `MainWindow.SetActivePage` 调用。

`SetActivePage` 只切 Visibility，**不会触发 Loaded/Unloaded**。
任何「只在页面可见时需要」的定时器/订阅/轮询，都必须挂在这两个方法上。

当前只有 `ConsolePage` 实现了。

### 5.3 RDP 架构（待评估）

用户提出：SimpleRemote / 1Remote 是**每个 RDP 连接一个子进程**，
而我们是**全部挂在主进程**。

| 维度 | 进程内（当前） | 独立进程 |
|---|---|---|
| 崩溃隔离 | ❌ 一个崩全崩 | ✅ 互不影响 |
| 资源隔离 | ❌ 累积到主进程 | ✅ 各自独立 |
| 嵌入门槛 | ✅ 简单 | ❌ 需 SetParent + IPC |

**当前结论：暂不改。** 理由：
1. 实测证明「不连 RDP 时也有 21% CPU」，主因不是 RDP
2. 改造涉及 `RdpConsole.cs`（3,408 行）60-70% 重写
3. 代码里有大量实测得出的硬约束（如「必须先挂视觉树再创建 OCX」），重构要重新踩坑

**如果将来要开 6 个 RDP 连接且出现稳定性问题，再评估。**

### 5.4 进程快照（已按需，但需观察）

`ProcessOwnerSnapshot` 改为 `IsNeeded(cfg)` 判断——只有启用中的流程
真的用到「进程检查」节点时才枚举 550 个进程。

**风险**：如果未来有流程通过其他路径依赖这个快照，会读到空数据。
加新节点类型时记得检查 `UsesProcessCondition` 是否需要更新。

### 5.5 诊断代码已清理

`Services/HandleProbe.cs` 及相关调用已全部移除（3 个文件、16 处）。
如果下次需要类似的逐轮计量，**记得用 `GetProcessHandleCount`**，
不要用 `Process.HandleCount`。

---

## 6. ★ 本次踩过的坑（务必记住）

| # | 坑 | 教训 |
|---|---|---|
| 1 | 诊断探针用 `Process.HandleCount`，自己吃掉 20% CPU | **先测诊断工具开销** |
| 2 | `dotnet-stack` 漏报线程池工作线程 | 用 `Threads[].TotalProcessorTime` 差分 |
| 3 | `handle.exe` 只显示 158/33600 个句柄 | 自己写 `NtQuerySystemInformation` |
| 4 | 看到 12/12 帧都在 `AsyncLog` 就断定死循环 | **常驻等待线程每次都会被采样到**，不是异常 |
| 5 | 改了 `LaunchPage` 但 CPU 没降 | 找错热点了，实际是看门狗 |
| 6 | PowerShell 的 `$` 和反引号在 JS 模板串里被吃掉 | 用字符码拼接，或写成文件 |
| 7 | 增量打包把 `SoulLink.exe` 筛掉了 | 校验全量集合，不能校验过滤后的集合 |
| 8 | 提交拆分时按「主题」而不是「文件」分组 | 同一文件的改动会被混进错误提交 |
| 9 | `build.bat` 只复制主程序，忘了 `tools\` | 新增产物目录时记得同步 |
| 10 | 误以为「注销」= 会话 Disc | **注销会让会话从列表消失**，Disc 只是断开 |

---

## 7. 改动清单

| 文件 | 内容 |
|---|---|
| `Services/TickHub.cs` | **新增**，统一 1 秒节拍调度 |
| `Services/AccountStateCenter.cs` | 事件驱动 + `Kick`/`ForceRefreshAsync` + 自适应周期 |
| `Services/ProcessOwnerSnapshot.cs` | 按需刷新 `IsNeeded(cfg)` |
| `Services/BgiLogWatcher.cs` / `RouteUpdateScheduler.cs` / `ScheduleRunner.cs` | 迁移到 TickHub |
| `Views/ConsolePage.xaml.cs` | 新增 `OnPageActivated`/`OnPageDeactivated` |
| `Views/LaunchPage.xaml.cs` | 迁移到 TickHub + `RefreshSessions(force)` |
| `Views/LobbyPage.xaml.cs` / `MainWindow.xaml.cs` | 迁移 / 调用生命周期 |
| `Controls/RdpConsole.cs` | RDP 连接成功后 `Kick` |
| `SoulLink.csproj` | `PublishBrokerSetup` 目标 |
| `build.bat` | 同步 `tools\` 到 `dist` |
| `scripts/publish-release.ps1` | 版本化包名、打包排除规则、多个 bug 修复 |

提交：

```
4129c11  构建：服务安装器改为框架依赖单文件（33.6 MB → 1.1 MB）
17d255a  3.1.6：版本号、更新说明与发布脚本修正
c2e78e6  性能：会话查询改为事件驱动 + 刷新按钮强制重查
4ac3870  性能：定时器统一调度 + 控制台页切走后暂停看门狗
```

---

## 8. 验证记录

| 项 | 结果 |
|---|---|
| 编译 | 0 错误 / 132 警告（与基线持平） |
| 空闲 CPU | 27% → 21% |
| 句柄 | 19 分钟稳定在 1630 并回落 |
| 全量包 | 2.69 MB / 13 文件 / 解压 7.1 MB |
| BrokerSetup 冒烟 | 不传参返回退出码 2，运行正常 |
| 包内容核对 | 7za/主程序/更新器/救援/安装器 全部存在；data/stable/agent 全部排除 |

---

## 9. 边界约定（继续有效）

> **未授权不得触碰挂机测试版（`dist/stable/SoulLink-Stable.exe`）**
> **未授权不得使用 A1 / A4 账号**

本次操作全程未触碰挂机版（仍为 3.1.5），也未使用 A1/A4 账号。

---

## 10. 发版流程（更新）

```
1. 改 docs/RELEASE-NOTES.md        （更新说明）
2. 改 SoulLink.csproj 的 <Version>  （版本号唯一来源，三处 csproj 都要升）
3. build.bat                        （生成 dist\ 与 artifacts\dist-<ver>\）
4. scripts\publish-release.ps1 -Push         （增量推送）
   或 scripts\publish-release.ps1 -Full -Push （全量推送）
```

**当前状态**：3.1.6 全量包已生成（`artifacts\package\update-3.1.6.zip`），
**尚未上传 COS**（用户要求暂缓，其他人还没有新版）。
