# 交接文档 · 3.3.3（屏幕录制 + 网页远程 + 性能修复）

> 日期：2026-11-22
> 版本：**3.3.3**（`SoulLink.csproj`）
> 上一版：3.2.7（`5ad4372`）
> 本次跨度：3.2.7 → 3.3.0 → 3.3.1 → 3.3.2 → 3.3.3

---

## 0. 一句话总结

新增**屏幕录制回放**（内存环形缓冲 + MP4 保存）与**网页远程查看/控制**；
修掉一个藏了 8 个版本的 WTS 编码 bug（导致运行状态总览不显示）；
把日志读取统一收敛到数据中心；消除每秒无条件抓帧的性能浪费。

---

## 1. 已完成的功能

### 1.1 屏幕录制（目标会话内）

| 项 | 实现 |
|---|---|
| 位置 | 代理进程内（`Relay/SessionRecorder.cs`） |
| 缓冲 | **内存环形队列**，只存 JPEG 压缩数据 |
| 占用 | 1920×1080 @10fps 质量75 ≈ **46 MB/账号/30秒** |
| 磁盘 | 平时**零写入**，只有「保存」才落盘 |
| 尺寸 | 取**代理所在会话**的分辨率（不是主控台桌面） |
| 生命周期 | 依附代理，代理断开即停（用户明确要求） |

**为什么必须在代理里录**：画面按会话隔离，别的会话 `CopyFromScreen` 只能拿到自己的桌面；
SYSTEM 也不行——它在会话 0，没有交互式桌面，截出来全黑（实测）。

### 1.2 保存为 MP4

`Services/Mp4Writer.cs` —— Windows 自带 Media Foundation H.264，**零依赖零体积**。

实测（1920×1080，254 帧真实桌面）：

| 格式 | 体积 |
|---|---|
| JPEG 序列 | 25 MB |
| **MP4** | **1.28 MB** |
| 压缩比 | **约 20 倍** |

**⚠️ 这里踩了三个大坑，注释已写进代码，改之前务必读：**

1. `IMFAttributes` 真实方法顺序里，`GetAllocatedString`(10) 和 `GetAllocatedBlob`(13)
   极易漏掉。漏一个 → 之后全体错位 → `SetUINT32` 实际调到 `DeleteAllItems`
   （同样返回 S_OK）→ 属性静默丢失 → `AddStream` 报 `0xC00D36E6`。
2. `IMFSample` 在 mfobjects.idl 里标了 **`[local]`**，不能跨 COM 编组，
   用 `ComImport` 会直接 AccessViolation。必须走 **vtable 直调**。
3. `IMFMediaBuffer` 的 IID 是 `045fa593-8799-42b8-bc8d-8968c6453507`，
   容易误写成 `066a-4794...`。

**权威来源**：从 `wine-mirror/wine` 的 `mfobjects.idl` 提取（与微软官方同源），不要靠记忆写。

### 1.3 自动化执行器

| 执行器 | 行为 |
|---|---|
| **截图** | 触发即截图，存 `录制回放\<用户名-昵称>\<时间-触发条件>\` |
| **保存录像** | 触发时刻前 N 秒 + 后 N 秒（N ≤ 保留时长/2），编码成 MP4 |

**目录规范**（用户指定）：

```
<程序目录>\capture\录制回放\
  A2-山客\
    2026-11-22-112845-手动\
      replay.mp4
      meta.json
```

### 1.4 网页远程（应用设置 → 网页访问）

`Services/WebServer.cs` + `Services/WebPages.cs`

| 能力 | 说明 |
|---|---|
| 实时画面 | `/api/live?sid=N`，按需抓帧，实测 17ms |
| 鼠标 | 点击 / 拖拽 / 右键 / 滚轮（复用投送通道） |
| 键盘 | 组合键，支持 Ctrl+Shift+A 等 |
| 录像回放 | MP4 + HTTP Range（可拖进度条） |
| 鉴权 | 口令 + Cookie 持久化（1 年） |
| 主题 | 亮/暗切换，默认跟随应用 |

**页面分离**（重要）：
- `docs/web-index.html` —— 可直接编辑预览的设计稿
- `Services/WebPages.cs` —— 由设计稿生成的 C# 常量（raw string literal）
- 改样式只动 `web-index.html`，重新生成 `WebPages.cs`

### 1.5 数据中心架构（用户硬性要求）

> 「访问日志的权限是数据中心，应用内其余地方只能从数据中心拿数据！」

**之前违反的地方**（已修）：
- `LaunchPage.RefreshSelectedLog` —— `new LogFollower(日志文件)`
- `LaunchPage.StartStatusFollowFor` —— `new LogFollower(..., deepBackfill:true)`，
  对几十 MB 日志做深度回填，和数据中心重复解析

**现在**：两处都改为读 `BgiLogCenter` 快照 + 订阅 `Changed` 事件。
「未绑定不读日志」由数据中心统一保证，不需要 UI 每个入口各写一遍。

---

## 2. 本次修掉的 Bug

### 2.1 WTS 站名编码错误（藏了 8 个版本）

**症状**：启动台选中主会话时，左栏本应显示「所有挂机账号运行总览」，
实际显示「lummy · 该账号的 BGI 运行情况」，总览完全消失。

**根因**（一行）：

```csharp
- Marshal.PtrToStringUni(info.pWinStationName)   // → "潃獮汯e䑒ⵐ捔..." 乱码
+ Marshal.PtrToStringAnsi(info.pWinStationName)  // → "Console"
```

`WTS_SESSION_INFO.pWinStationName` 指向的是 **ANSI 窄字符串**，用 Unicode 读会把字节序读反
→ `IsConsoleSession` 永远 false → `ApplyLeftPanelMode` 永远走非主会话分支 → `OverviewPanel` 不显示。

**为什么一直没发现**：`query user` 兜底路径是文本解析、站名正常，于是「有时好有时坏」。
这个 bug 从 `2.17.45`（`e474457`）把会话查询改成 WTS API 时就引入了。

### 2.2 未绑定账号仍被读取日志

**根因**：`IsPinned` 只看「BgiPaths 有非空路径」，而 A1/A4 虽已从账号列表删除，`BgiPaths` 里残留着历史路径。

**修复**：`IsPinned` 第一条判据改为「该用户必须还在 `App.Config.Accounts` 里」。

### 2.3 会话列表显示不该有的账号

**要求**：「RDP 配置里没有的用户直接隐藏」。

**修复**：`FilterUsers` 之后再过滤一次，只保留 `App.Config.Accounts` 里的用户（主会话始终保留，它是总览入口）。

### 2.4 主用户显示「通道未投送」

主会话是操作者自己的桌面，从不投送代理，显示「通道未投送」既无意义又误导。现在显示「当前会话」。

### 2.5 性能：每秒无条件抓帧

**根因**：`WebServer.EnsureLiveRefresh` 注册了 TickHub 每秒任务，**无条件**向所有录制账号发 `frame` 命令。
每帧在代理侧要 BitBlt + JPEG 编码（13~16ms），没人看网页也照抓不误。

**修复**：不注册任何定时器，只在 `/api/live` 收到请求时才抓一帧。
前端也加了 `document.hidden` 检查，页面切后台就停。

**验证**：30 秒不访问网页 → `capture\s4\live\` 目录**为空**，零抓帧。

### 2.6 性能：`BgiLogWatcher.Discover()` 无缓存

**根因**：每次调用都枚举全部进程 + 读 `MainModule`（跨进程，慢）+ 扫日志目录。
而调用方极多（LaunchPage 每 3 秒 4 次、BgiPathResolver 每账号 1 次、AccountStateCenter 每秒 1 次）。
3 个 BGI 在跑时单次约 20~30ms。

**修复**：加 1 秒 TTL 缓存 + `ClearDiscoverCache()` 供需要实时的场景用。

**效果**：CPU **92~168% → 平均 10%**（启动卡顿消失）。

---

## 3. 当前状态

### 版本与文件

| 项 | 值 |
|---|---|
| 版本号 | **3.3.3** |
| `dist\SoulLink.exe` | v3.3.3 |
| `dist\stable\` | **已更新到 v3.3.3** |
| 备份 | `backups\stable-3.2.3-20261122-142455\` |

### Git

```
9e2f45d feat: 网页改为运行状态总览 + 账号列表 (3.3.3)
bea248d fix: 严格按 RDP 配置过滤会话 + 日志统一走数据中心 (3.3.2)
4d0f669 fix: 修 WTS 站名编码错误 (3.3.1)
a4d4e2b feat: 屏幕录制回放与网页远程查看 (3.3.0)
```

**未提交**：`BgiLogWatcher.cs`、`WebPages.cs`、`WebServer.cs`、`docs/web-index.html`（性能优化 + 背景美化）

### 实测数据

| 场景 | 数值 |
|---|---|
| 无人访问网页时 CPU | 平均 10% |
| `/api/live` 按需抓帧 | 17 ms |
| 网页点击往返 | 39~85 ms |
| 录制内存占用 | 46 MB/账号（1080p 10fps 30秒） |
| MP4 压缩比 | 约 20 倍 |

---

## 4. 已知问题与未完成

### 4.1 CPU 35%（WPF 渲染）

`e474457` 就记录了这条，**不是数据层问题**：

```
BgiLogCenter.Tick        0.31%
最小化窗口              11.7%
恢复窗口                53%
```

全部来自 WPF 渲染（`RenderRecursive` 19.6% + `PrecomputeRecursive`）。
本次的 Discover 缓存能降低一部分，但**根因未解决**。

可疑方向：
- 大窗口上大量 `Border` + `DropShadowEffect` 的合成
- `MainWindow.xaml` 的 `DropShadowEffect BlurRadius=18`
- 某个元素在持续 `InvalidateVisual`

### 4.2 网页星点背景

用户要求加星空背景，我试了 `radial-gradient` 平铺（网格感，被否）
和 `box-shadow` 随机布点（**始终没渲染出来，原因未查明**）。

**当前状态**：只保留渐变背景（紫/青星云），星点层已移除。用户已确认「先不做星点」。

如果以后要做，建议换 **多重 `radial-gradient` 逐点定位**（每颗星一个独立 position，不平铺），比 box-shadow 可靠。

### 4.3 稳定版服务需手动更新

> ⚠️ **只替换客户端 exe 不会更新 Broker 服务。**

`dist\stable\` 更新后，必须启动 `SoulLink-Stable.exe` → 「远程管理」→ 点「安装 / 修复服务」。

因为服务装的是 `C:\Program Files\SoulLinkDev\SoulLink.exe`，那是独立的一份。
不更新会导致挂机版**代理投送失败**。

### 4.4 内存问题（本机环境）

```
物理内存 29.7 GB，可用只剩 1.5 GB
Memory Compression 占 4806 MB（系统在硬压缩换页）
```

不是 SoulLink 造成的（它只占 375 MB）。主要是原神 5885 MB + BetterGI 999 MB。

SoulLink 侧可释放：
- `SoulLink-Stable` 388 MB（旧挂机版，需授权）
- `VBCSCompiler` 518 MB（编译服务器）

---

## 5. 代码改动清单

### 新增文件

| 文件 | 行数 | 作用 |
|---|---|---|
| `Relay/SessionRecorder.cs` | 537 | 内存环形缓冲录制 |
| `Services/Mp4Writer.cs` | 390 | MF H.264 编码 |
| `Services/WebServer.cs` | 852 | 网页服务逻辑 |
| `Services/WebPages.cs` | 692 | 网页界面（生成物） |
| `docs/web-index.html` | 675 | 网页设计稿 |

### 主要修改

| 文件 | 改动 |
|---|---|
| `Services/WtsSessionQuery.cs` | 一行：`PtrToStringUni` → `PtrToStringAnsi` |
| `Services/BgiPathResolver.cs` | `IsPinned` 加账号存在性检查 |
| `Services/BgiLogWatcher.cs` | `Discover` 加 1 秒缓存 |
| `Services/AccountStateCenter.cs` | 注入环境变量支持 |
| `Services/SystemLauncher.cs` | `Job.Env` + `MergeEnvironment` |
| `Services/BrokerProtocol.cs` | `Request.Env` |
| `Services/RelayClient.cs` | 录制/保存/截图方法 + `ClickAtAsync` |
| `Models/Models.cs` | 录制配置 + 主题字段 |
| `Models/FlowModels.cs` | `ActScreenshot` / `ActSaveRecording` |
| `Models/FlowNodeCatalog.cs` | 两个执行器注册 |
| `Services/FlowRunner.cs` | 执行器实现 + `UserTagOf` |
| `Views/LaunchPage.xaml.cs` | 会话过滤 + 日志走数据中心 |
| `Views/SettingsPage.xaml(.cs)` | 网页访问设置 |
| `Controls/RdpGeneralSettings.xaml(.cs)` | 录制设置 |

---

## 6. 给继任者的提醒

### 6.1 绝对不要做的事

1. **不要用 `write` 覆盖大文件** —— 本次会话我因此截断了 `LaunchPage.xaml.cs`（3014 行）
   和 `WebServer.cs` 各一次，靠 git 才救回来。**大文件一律用 `edit` 增量修改。**
2. **不要动 `dist/stable/`**，除非用户明确授权。
3. **不要用 A1 / A4 账号**，除非用户明确授权。
4. **不要在 UI 层直接读日志文件** —— 必须走 `BgiLogCenter` 快照。

### 6.2 改代码前先看历史

我本次最大的教训：花了很多轮改 `RefreshOverview` 的过滤逻辑，**那些代码本来就是对的**。
真正的问题是 WTS 编码 bug 让它从未被调用到。

`git log --oneline -- <file>` 和 `git show <commit>` 的提交说明里，
经常已经写明了根因和已知问题（比如 `e474457` 就记录了 CPU 偏高是 WPF 渲染）。

### 6.3 前端最典型的坑

本次网页空白过一次：`$('themeBtn').onclick` 拿到 `null` 抛异常，
**后面的所有代码（含 `loadUsers`）全都不执行**，页面只剩静态 HTML。

规则：
- 所有 DOM 查询用 `if (el)` 守卫
- 关键初始化包 `try`
- 替换 HTML 前先确认原文真的存在

---

## 7. 下一步建议

**优先级从高到低**：

1. **提交未提交的改动**（性能优化 + 背景）
2. **发版到 COS**（`publish-release.ps1 -Full -Push`）
3. **更新 Broker 服务**（挂机版必须）
4. **查 CPU 35% 的 WPF 渲染问题**（需性能分析工具）
5. 网页星点背景（可选，建议换多重 radial-gradient）
6. 拖拽手势前端（API 已支持）
