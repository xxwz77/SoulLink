# 交接文档 · 3.1.10

> 时间：2026-11-21 21:35
> 版本：客户端 **3.1.10.0**（主程序与挂机版同步）
> 分支：`main` @ `90b49eb`（已推送 origin）
> 线上：**3.1.10 已发布**（增量包 2.83 MB）

---

## 0. 本轮做了什么（3.1.6 → 3.1.10）

五轮发版，主题依次是：

| 版本 | 主题 | 关键改动 |
|---|---|---|
| 3.1.6 | 云更新上线 + 卡顿根治 | 包名带版本、TickHub 统一节拍、体积 123 MB → 2.7 MB |
| 3.1.7 | 会话瘦身 | 清理外围进程、按账号启动项优化 |
| 3.1.8 | 性能 | WTS 原生会话查询（消除句柄泄漏）、日志事件驱动 |
| 3.1.9 | 更新器修复 | 替换失败、弹窗黑边、版本号点不开 |
| 3.1.10 | 动效修复 | 高档不动画、档位被重置、挂机版标识 |

---

## 1. ★ 命名管道泄漏（本轮最重要的发现）

### 1.1 现象

系统命名管道总数达到 **3.8 万个**，任何遍历 `\\.\pipe\` 的代码要 **6.5 秒**，
界面每次刷新都卡死。

### 1.2 根因

`Process.Start` 在 .NET 里会留下约 **0.6 个** `dotnet_<guid>` 命名管道句柄：

```
重定向后读流   60 次 → +36 个
完全不重定向   60 次 → +38 个
GC.Collect() 后依然残留
```

**只要还起子进程就会漏，跟读写方式无关。**

而 `AccountStateCenter` 原本每秒起 `query.exe` + `qwinsta` 两个子进程 ——
一天约 10 万个句柄。

### 1.3 修复

`SessionService` 改用 **`WTSEnumerateSessions`**（原生 API）：

| 项 | 改造前 | 改造后 |
|---|---|---|
| 单次耗时 | 56.8 ms（qwinsta 子进程） | **7.6 ms** |
| 句柄泄漏 | 每子进程 0.6 个 | **零** |
| 用户名大小写 | 需第二个子进程校正 | 直接给出真实大小写 |

**保留子进程路径作为兜底**（WTS 不可用时自动退回）。

**实测：改造前 60 秒句柄 +252，改造后 -65。**

### 1.4 ★ 教训：诊断工具自己制造问题

上一轮（3.1.6）我用 `Process.GetCurrentProcess().HandleCount` 做探针，
每次约 3~5 ms，挂在 1Hz 循环里**自己把 CPU 抬到 21%，还制造了 0.64/秒的句柄增长**。

**取句柄数必须用 `GetProcessHandleCount`（约 1 微秒）+ 缓存**，不要用 `Process.HandleCount`。

---

## 2. ★ 管道枚举（第二处）

`DiscoverLiveAgentSessions` 原本遍历系统全部管道来找自己的投送通道。

用户明确：**「只投送自己的会话，不关心别的版本」**。

改为直接查 `RelayClient` 的内存注册表（投送成功时 `Register`、断开时 `Unregister`）：

```csharp
public static HashSet<int> GetLiveSessions()   // O(会话数)，约 0 毫秒
```

`DiscoverChannelSessions` 已无调用方，**删除**。

**唯一保留的管道枚举**：`ManualAgent.FindHostPipe` —— 代理是独立进程，
拿不到主进程的注册表，只能枚举。但它**只在代理启动时执行一次**。

---

## 3. ★ BGI 日志改事件驱动

### 3.1 原状

状态判断依赖 `AccountStateCenter` 的轮询，延迟数十秒。
而 `LaunchPage` 另有一条 `LogFollower`（FileSystemWatcher）只用于日志面板显示。

**两条独立路径读同一份日志，各自维护字节游标 —— 违背"单一数据源"原则。**

### 3.2 修复

给 `BgiLogCenter` 自建 `FileSystemWatcher`，但**只置标记不直接解析**：

```
watcher 事件 → Interlocked.Exchange(ref e.Dirty, 1) → 立刻返回
       ↓
1 秒合并窗口（TickHub）→ 有标记才读增量
```

**为什么不直接在事件里解析**：实测 BGI 满速写日志时每秒触发几十次事件，
每次都解析会让 CPU 飙到 25%（日志每秒只写 1KB，却烧掉 25% 单核）。

**不丢数据**：`Pump()` 用字节游标，从 `Cursor` 一路读到 `EOF`，中间不跳过任何字节。

### 3.3 附带优化

日志路径改为**从配置的 exe 推导**（0.017 ms），不再枚举全部进程（23.7 ms）：

```csharp
// 旧：先枚举 550 个进程拿 inst.LogFile，拿不到才走推导路径
// 新：直接 BgiLogWatcher.FindTodayLog(exe)
```

BGI 的 PID 缓存进 `Entry`，判活用 `GetProcessById`（**0.012 ms，比枚举便宜 2000 倍**）。

---

## 4. ★ 会话瘦身（3.1.7 新增功能）

### 4.1 背景

Windows 每个 RDP 会话首次登录会自动拉起一批外围组件。实测单会话
63-70 个进程 / 2.4-10 GB，其中游戏与必需进程只占小部分。

### 4.2 白名单（全部实测验证）

**可清理（杀掉不重启）**：

```
msedge / PhoneExperienceHost / CrossDeviceService / CrossDeviceResume
GameBar / GameBarFTServer / GameBarPresenceWriter
TabTip / UserOOBEBroker / FileCoAuth
```

**需注册表优化（杀不掉，会自动重启）**：

```
SearchHost / TextInputHost / ShellHost / Widgets / WidgetService
```

**明确排除（有风险）**：

| 进程 | 为什么不清 |
|---|---|
| `nvcontainer` / `NVDisplay.Container` | 杀它 NVIDIA 框架重启、**游戏黑屏** |
| `RtkAudUService64` | 音频初始化失败，部分游戏卡加载 |
| `AppActions` | 随系统 UI 自动回来，无净收益 |

### 4.3 启动项优化

`reg load` 临时挂载目标用户的 `NTUSER.DAT` → 写 `Policies` → `reg unload`。

**只影响勾选的账号**，主用户完全不出现在列表里。

用户正在登录时 `NTUSER.DAT` 被独占 → **跳过并提示「请先注销」**，不做强制操作。

---

## 5. ★ 云更新的坑（3.1.6 起）

### 5.1 包名必须带版本号

固定名 `update.zip` 会被 CDN 缓存成「清单是新的、包是旧的」。

改为 `update-<version>.zip`，内容与文件名一一对应，永不覆盖。

### 5.2 发布顺序不可颠倒

```
打包 → 上传 zip → HEAD 校验 → 通过后才写 latest.json → 刷新 CDN
```

**清单一旦指向坏包，所有用户更新失败且无法自愈。**

### 5.3 ★ 更新器无法替换主程序（3.1.9 修复）

**现象**：点「确认更新」，程序退出又重启，但版本号没变。

**证据**：`SoulLink.Updater.exe` 已更新到新版，`SoulLink.exe` 仍是旧版。

**根因**：

```csharp
WaitForExit(pid, 30 秒);   // 返回 void，超时不报错
// 直接继续 → 覆盖 SoulLink.exe → 文件被锁 → IOException
// → 改名 .old 也失败 → 回滚 → 重启旧版
```

WPF 的 `Application.Shutdown()` 有时会被窗口关闭流程拖住，主程序迟迟不退。

**修复**：`WaitForExit` 改为返回 `bool`，超时后**强制结束**主程序：

```csharp
if (!WaitForExit(pid, 20 秒)) {
    victim.Kill(entireProcessTree: true);
    Thread.Sleep(1500);   // 等文件句柄真正释放
}
```

### 5.4 ★ 遗留隐患

**3.1.8 用户的更新器仍是坏的。** 他们从 3.1.8 更新到 3.1.10 时跑的仍是旧更新器，
可能再次失败。

**缓解方案（未实施）**：让更新器先把自身复制到临时位置再执行，
避免旧更新器的缺陷传下去。

---

## 6. ★ 动效 bug（3.1.10 修复）

### 6.1 选「高（完整动效）」却只有静态图

```csharp
public static bool Backdrop(int tier) => BackdropFrameMs(tier) > 0;

// BackdropFrameMs(High=0) = 0  →  Backdrop(0) = (0 > 0) = false
```

**高档被判成"不播动画"。**

根因：`0` 这个值承担了两种相反含义：

| 档位 | FrameMs | 本意 |
|---|---|---|
| High | `0` | 间隔为零，**每帧都推** |
| Low | `0` | **永不推进** |

从间隔值反推就分不清。改为看档位本身：

```csharp
public static bool Backdrop(int tier) => tier == High || tier == Medium;
public static bool EveryFrame(int tier) => tier == High;
```

### 6.2 打开设置页会把档位重置成「高」

```csharp
EffectTierCombo.SelectedIndex = tier;   // 触发 SelectionChanged
    ↓
EffectTier_Changed 无 _loading 保护 → 把默认索引 0（高）写盘
```

**用户配置从 2 变成 0，用户没动过设置。** 加 `if (_loading) return;`。

---

## 7. ★ 工作区失控与整理

### 7.1 问题

长期开发积累到 **17,781 MB**，而真正的源码只有几 MB：

```
releases        9,100 MB   历史发布包（164 个 exe）
artifacts       4,021 MB   70+ 个测试构建目录
dist-cpuexp     1,984 MB
tools           1,881 MB   layout-check 有 107 个 bin 目录共 472 MB
backups           944 MB   6 份重复备份
```

### 7.2 整理结果

**17,781 MB → 267 MB。**

规范见 `docs/WORKSPACE-RULES.md`，要点：

1. 构建产物不入库**也不囤积** —— 不进 git ≠ 可以留在磁盘
2. 一次性实验目录**当场删** —— 曾累积 12 个 `focus-flash-*` 各 63 MB
3. 截图进 `%TEMP%` —— 曾散落 100+ 张验证截图
4. `backups/` **只留最近一份**源码 zip
5. 研究文档统一放 `docs/research/` 并登记索引
6. `tools/` 只保留能构建的源码

**健康标准**：工作区 < 1 GB、tools < 50 MB、backups 1 个 zip、根目录 `.md` < 5 个、bin/obj 0 个。

### 7.3 源码备份

`backups/source-20261121-211352.zip` —— **1.2 MB**（纯源码，343 个文件）

> 上一版备份误把构建产物包进去了，122 MB。备份脚本要按扩展名白名单收集。

---

## 8. ★★ 我犯的错误（务必让后来者避免）

### 8.1 实验后只回滚源码，没重新构建 ★★★

**最严重的一次。**

做 CPU 实验时把 `AuroraBackdrop` 换成静态 `Border`，实验完回滚了源码，
但 **dist 里的 exe 一直是那次实验的坏版本**。

后来排查「背景没了」时，一直用这个坏 exe 测试，却怀疑代码逻辑，
加了 4 处诊断代码、试了 6 轮都定位不到。

**用户一句话点破：「3.1.9 你在做测试的时候关掉的」。**

**教训**：实验完要么**立刻重建**，要么**连构建产物一起清掉**。
`git diff` 干净不代表运行的二进制是干净的。

### 8.2 用错 CPU 测量算法 ★★

```powershell
(TotalProcessorTime 增量) / (墙钟秒数) × 100   # 假设单核
```

多核机器 + 同时跑着原神 240%、BetterGI 136% 的环境下，这个数字毫无意义。

**我据此得出「运行状态页占 20-26% CPU」的结论，还查了很久。**

任务管理器实测是 **4.1%**。**该用任务管理器对照一次再下结论。**

### 8.3 文件被占用时 `Copy-Item` 静默失败 ★★

更新挂机版时，`SoulLink-Stable.exe` 正在运行，文件被锁，
`Copy-Item` **静默失败**，没有报错。

结果版本号显示 3.1.9 但实际跑的是旧的坏 exe。

**教训**：覆盖运行中的 exe 前必须**先停进程**，并用**哈希对比**验证。

### 8.4 反复对 600 行文件做字符串替换

改 `ProcessPage.xaml.cs` 时：
- 误删了 `ApplyFilter()` 调用 → 进程列表空白
- 插入位置算错 → 6 个方法重复定义
- 清理重复时误删了 `SessionItem` 类

**大文件不适合反复整体替换**，应先备份或分小步验证。

### 8.5 声称「已加入 TabTip」但只改了注释

给白名单加 `TabTip`/`TextInputHost` 时，只改了 `NeverKill` 的注释，
**没改 `SafeToKill` 数组**。然后告诉用户"已加入"。

### 8.6 未验证的推测当成结论

说「OneDrive 会连锁重启」，实测 45 秒内根本没重启。

**推测要标注为推测，验证过才能当结论。**

---

## 9. 当前状态

| 项 | 值 |
|---|---|
| git | `90b49eb`（已同步 origin） |
| dist | 3.1.10.0 |
| 挂机版 | 3.1.10.0（哈希与 dist 一致） |
| 线上 | 3.1.10（增量包 2.83 MB） |
| 工作区 | **267.5 MB** |
| 源码备份 | 1.2 MB |

### 挂机版组件（已全部对齐）

```
SoulLink-Stable.exe  3.1.10.0
SoulLink.Updater.exe 3.1.10.0
sl-rescue.exe        3.1.10.0
tools/BrokerSetup.exe 1.1 MB（原 34 MB）
7za.exe              已补（原本缺失 → BgiUpdater 解压会抛异常）
```

---

## 10. ★ 待办

### 10.1 挂机版左上角标识（用户明确要求，未完成）

> 用户原话：「把挂机版左上角标题也加个备注，现在不好区分」
> 补充：「就是主界面的左上角啊！」

当前**窗口标题**（任务栏）已有「SoulLink · 挂机验证版」，
左上角界面**已加了 `IdentityBadge`**（琥珀色「挂机版」徽章，
`MainWindow.xaml` 第 70 行附近，代码里非默认身份时设为 Visible）。

**但用户反馈没看到。** 需要确认：
- 挂机版是否已重启（旧进程不会加载新 exe）
- 徽章是否被布局挤压或颜色不显眼

### 10.2 更新器自替换（防御性改进）

让更新器先把自己复制到临时位置执行，避免旧更新器的缺陷传递。

### 10.3 进程检查的按需刷新需要观察

`ProcessOwnerSnapshot.IsNeeded(cfg)` 只在流程用到「进程检查」节点时才枚举全进程。
**加新节点类型时记得检查 `UsesProcessCondition` 是否需要更新**，否则会读到空数据。

### 10.4 RDP 架构（暂不改）

用户提出 SimpleRemote / 1Remote 是每个连接一个子进程，而我们是全部挂在主进程。

**当前结论：暂不改。** 理由：
1. 实测证明不连 RDP 时也有开销，主因不是 RDP
2. 改造涉及 `RdpConsole.cs`（3,408 行）60-70% 重写
3. 代码里有大量实测得出的硬约束（如「必须先挂视觉树再创建 OCX」）

---

## 11. 边界约定（继续有效）

> **未授权不得触碰 `dist/stable/SoulLink-Stable.exe`**
> **未授权不得使用 A1 / A4 账号**

本轮修改挂机版**已获得用户明确授权**（"挂机版已经结束挂机，可以更新"）。
A1/A4 账号全程未使用。

---

## 12. 发版流程

```
1. 改 docs/RELEASE-NOTES.md        （更新说明）
2. 改 SoulLink.csproj 的 <Version>  （版本号唯一来源，三个 csproj 都要升）
3. build.bat                        （生成 dist）
4. scriptspublish-release.ps1 -Push         （增量）
   或 scriptspublish-release.ps1 -Full -Push （全量）
```

**每次发版后**：同步 `diststable\` 的组件（停进程后复制，用哈希验证）。
