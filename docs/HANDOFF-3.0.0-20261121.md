# SoulLink 交接文档 · 3.0.0（运行时迁移 + 数据目录重构 + 备份体系重建）

> 时间：2026-11-21 11:06
> 版本：客户端 **3.0.0.0** · 服务端 **2.0.0.0**
> 分支：`migrate/net11`
> 性质：**重大变更** —— 运行时、发布形态、数据目录、备份机制全部换过
> 兼容性：**用户需安装 .NET 11 桌面运行时**；老数据需手动迁移（程序内有一键迁移）

---

## 0. 一页速览

| 维度 | 2.17.50（旧） | 3.0.0（新） | 变化 |
|---|---|---|---|
| 目标框架 | net8.0-windows | **net11.0-windows** | .NET 8 已 EOL |
| 发布形态 | 自包含单文件 | **框架依赖单文件** | |
| exe 体积 | 67.69 MB | **3.28 MB** | **-95%** |
| 启动耗时 | 4101 ms | **2617 ms** | **-36%** |
| 数据目录 | %LOCALAPPDATA%\SoulLink | **exe 同级 data\** | 可搬离 C 盘 |
| BGI 备份 | 目录复制，无清理 | **zip，可配保留** | 872 MB → 可控 |
| 路线备份 | 目录，无清理 | **目录，可配保留** | 新增清理 |
| 包缓存 | 目录，无清理 | **目录，按天过期** | 新增清理 |
| 用户侧前置 | 无 | **装 .NET 11 桌面运行时** | ⚠ 需通知 |

---

## 1. 运行时迁移：net8 → net11

### 1.1 为什么必须动

`.NET 8` 支持期已于 **2026-11-10 结束**，继续用等于跑无安全补丁的运行时。

### 1.2 改动范围

| 项目 | 数量 |
|---|---|
| csproj 目标框架 | 86 个（63 个 `-windows` + 23 个无后缀） |
| NuGet 包 | 2 个 |
| C# 源码改动 | 5 个文件（全是分析器告警） |

NuGet：
```
Microsoft.Extensions.Hosting.WindowsServices   8.0.1  → 11.0.0
Microsoft.Data.Sqlite                          8.0.11 → 11.0.0
```

### 1.3 ★★ 核心发现：net11 自包含单文件有 WPF 回归 ★★

**这是我们绕了一大圈才搞清楚的事，下次动发布形态前必须看完。**

#### 现象

`net11.0-windows` 编译正常、框架依赖发布正常，**唯独自包含单文件启动即崩**：

```
Unhandled exception. System.TypeInitializationException:
  The type initializer for 'System.Windows.Application' threw an exception.
 ---> System.BadImageFormatException: 'DirectWriteForwarder.dll'
   at System.Windows.Application.ApplicationInit()
```

退出码 `-532462766`（0xE0434352）。

#### 这是 .NET 11 的通用回归，不是本项目问题

**最小复现**：空 WPF 工程，相同发布参数：

| 目标框架 | 自包含单文件 | 结果 |
|---|---|---|
| net8.0-windows | 68.30 MB | ✅ |
| net10.0-windows | 61.85 MB | ✅ |
| **net11.0-windows** | 64.35 MB | ❌ **崩溃** |

根因：`DirectWriteForwarder.dll` 是 WPF 混合模式程序集，.NET 11 重写了它：

| 运行时 | 大小 | CLR 目录 RVA |
|---|---|---|
| 8.0.33 | 526,160 | 0x1600 |
| 10.0.14 | 509,736 | 0x1600 |
| **11.0.0** | **203,048** | **0x16448** |

#### 试过的绕法（都不采纳）

`-p:IncludeAllContentForSelfExtract=true` 确实能让它跑，但构建报 `NETSDK1244`：

> "会启用一种旧版兼容模式……从项目中移除该属性，改用默认的内存中单文件行为。"

官方文档原话：**"not recommended: it's a .NET Core 3.1 compatibility mode and might be removed in a future release."**

每次启动还要把全部托管程序集解压到 `%TEMP%\.net`。不能作为正式方案。

#### 最终方案：框架依赖

`DirectWriteForwarder.dll` **不进 bundle**，直接从用户安装的运行时加载，回归被彻底绕开。

三种形态实测（3 次中位数）：

| 模式 | exe | 启动 |
|---|---|---|
| net10 自包含单文件 | 67.69 MB | 4101 ms |
| net10 框架依赖单文件 | 3.70 MB | 2857 ms |
| **net11 框架依赖单文件** | **3.28 MB** | **2617 ms** |

体积降到 1/20，启动快 1.5 秒。

---

## 2. 用户侧变化：必须装 .NET 11 桌面运行时

### 2.1 系统自带的提示（apphost 内置，无需写代码）

用户没装时会弹系统对话框：

```
You must install or update .NET to run this application.
Required: 'Microsoft.NETCore.App', version '11.0.0' (x64)

Learn more:  https://aka.ms/dotnet/app-launch-failed
[ Download it now ]  [ 查看详细信息(D) ]  [ 关闭(C) ]
```

点 "Download it now" 会跳微软官方页面。

### 2.2 但这个提示有两个坑

1. **中英文混杂**，体验割裂
2. 提示写的是 `Microsoft.NETCore.App`（**基础**运行时），而 WPF 应用需要的是
   `Microsoft.WindowsDesktop.App`（**桌面**运行时）。**用户照提示装完还是跑不起来。**

### 2.3 所以补了 `scripts/check-runtime.ps1`

随包发布，用户双击 exe 前先跑它：

```
已安装的 .NET 桌面运行时：
  [OK] Microsoft.WindowsDesktop.App 11.0.0
  [旧] Microsoft.WindowsDesktop.App 10.0.14
  ...
√ 可以运行 SoulLink（将使用 Microsoft.WindowsDesktop.App 11.0.0）
```

缺运行时会询问并打开**正确的**下载页，明确提示「选 Desktop Runtime，不要选 Runtime」。

### 2.4 rollForward=LatestMajor

`SoulLink.csproj` 里配了：

```xml
<RollForward>LatestMajor</RollForward>
```

写进 `runtimeconfig.json`，含义：**装了任何 ≥ 11 的桌面运行时都能跑**，
不要求版本号精确匹配。避免「用户装了 .NET 12 反而起不来」。

---

## 3. 数据目录重构

### 3.1 问题

实测用户机器上 `%LOCALAPPDATA%\SoulLink` 占 **887.81 MB / 145,191 个文件**，
其中 `bgi-backups` 一项 872.7 MB。C 盘小的机器负担很重。

### 3.2 新策略（`AppIdentity.DataDir`）

按优先级：

```
① 环境变量 SOULLINK_DATA_DIR 显式指定     ← 可指到 D 盘
② exe 同目录下的 data\                     ← 默认
③ %LOCALAPPDATA%\<身份>\                   ← exe 目录不可写时自动回退
```

**为什么要有 ③**：exe 若装在 `C:\Program Files`，普通用户没有写权限，
没有回退程序直接起不来。

**关键实现**：判断「可写」不能只调 `Directory.CreateDirectory`——
装在 Program Files 时管理员能建目录，但普通用户写文件仍会失败。
**必须真写一个探针文件再删掉**才知道能不能用（`TryCreateWritable`）。

### 3.3 所有数据现在都在 data\ 下

| 数据 | 路径 |
|---|---|
| 配置 | `data\config.json` |
| 状态 | `data\state.json` |
| 设备码 | `data\device.json` |
| BGI 备份 | `data\bgi-backups\<账号>\*.zip` |
| 路线备份 | `data\route-backups\*` |
| 包缓存 | `data\route-cache\*` |
| 日志 | `data\*.log` |

还有一处仍用 `%TEMP%`：**临时工作目录**（一次性中间产物，放 TEMP 是对的）。

---

## 4. 备份体系重建

### 4.1 BGI 备份：目录 → zip

**原实现**：复制整个 `User` 目录。一次 80~130 MB / 数万文件，
占空间且零碎，复制和迁移都慢。

**现实现**：打包成 `User_yyyyMMdd_HHmmss.zip`，单文件。

> ⚠ **中途走过弯路，已纠正**：
> 中间版本曾想过「跳过 PNG/ICO/字体等可再生资源」来省空间（实测能省 62 MB）。
> 但用户指出：**「备份就应该备份好了，用户恢复就应该能用」**——这是对的。
> 少资源的备份恢复出来是「能看不能跑」的残缺目录。
> **已完全删除跳过逻辑，现在备份包含源目录全部文件。**

打包时写一份 `_备份说明.txt` 进包，说明备份时间和还原方式。

### 4.2 保留策略（可配）

| 设置 | 默认 | 空值语义 |
|---|---|---|
| `BgiBackupKeep`（每账号保留次数） | 3 | **0 = 不限制** |
| `RouteCacheKeepDays`（缓存保留天数） | 30 | **0 = 不限制** |

设置页两个输入框，**留空 = 不限制**，失焦即保存。

按名字/时间排序即可裁剪：
- 备份目录名是 `yyyyMMdd_HHmmss`，字典序 = 时间序
- 缓存用 `LastWriteTimeUtc`（下载时间），**不用 LastAccessTime**——
  Windows 默认可能关闭最后访问时间更新，且不该让「看了一眼」延长寿命

### 4.3 三类数据的生命周期

| 数据 | 清理策略 | 删了会怎样 |
|---|---|---|
| BGI 备份 | 每账号保留 N 次 | 丢失配置历史 |
| 路线备份 | 保留 N 个 | 无法回滚路线更新 |
| 包缓存 | 保留 N 天 | 重新下载即可 |

**启动时自动清理一次**（`App.xaml.cs`，后台线程）：

- 不挡启动（清 872 MB 级目录可能要几秒）
- **只在设了限制时才动**，不限制就完全跳过（避免误删）
- 清理了写日志，可追溯

### 4.4 `route-backups` 是这次顺带发现的第二个膨胀源

之前**完全没有清理机制**，每次路线更新都往里堆一份完整旧目录。
现在和 BGI 备份共用同一个「保留次数」设置。

---

## 5. 老用户数据迁移

### 5.1 为什么不能自动迁移

1. 数据量大（实测 872 MB / 14.5 万文件），搬一半断电会留下烂摊子
2. 旧目录可能正被另一个版本（`SoulLink-Stable.exe`）使用
3. 用户可能根本不想搬（C 盘空间够）

### 5.2 设计（`Services/DataMigration.cs`）

设置页检测到旧数据时显示橙色面板，列出路径/文件数/体积，
提供「迁移到当前目录」和「打开旧目录看看」。

三条原则：

| 原则 | 原因 |
|---|---|
| **复制而非移动** | 旧数据保留，用户确认新的能用后自己删。宁可多占一份空间，也不能让用户丢失唯一一份数据 |
| **不覆盖已存在文件** | 新目录的 config.json 是程序刚建的，但也可能用户改过。跳过并计数，让用户自己判断 |
| **迁移完自动转 zip** | 老格式是 `User_xxx\` 目录，不转换的话「改 zip」这个改动对老用户等于没生效 |

迁移完成后询问是否删除旧目录（二次确认，默认不删）。

---

## 6. 验证记录

### 6.1 构建

```
客户端 3.0.0：0 错误 / 130 警告（net8 基线 132，差异仅 WFAC010→WFO0003 分析器改名）
服务端 2.0.0：0 错误 / 0 警告
```

### 6.2 客户端产物

```
artifacts\dist-3.0.0\
  SoulLink.exe          3.28 MB
  7za.exe               1.23 MB
  check-runtime.ps1
  broker-install.ps1
  broker-uninstall.ps1
```

独立身份启动：

```
RUNNING: True
data in exe dir: True
LOCALAPPDATA written: False
boot.log: main window shown: True state=Normal
error.log: 无
data 结构: bgi-backups / route-backups / route-cache / boot.log / config.json / device.json
```

### 6.3 回退逻辑实测

把 exe 放到 `C:\Program Files\SLDataDirTest` 并用 `icacls` 拒绝写入：

```
exe 旁有 data 目录: False
回退到 LOCALAPPDATA: YES
程序照常启动，主窗口 Normal
```

### 6.4 服务端 2.0.0

```
PING: {"ok":true,"version":"2.0.0.0"}
30 路并发（建房/列表/心跳）→ OK=30 FAIL=0
WAL: soul-link.db / -wal / -shm 齐全
stderr: 空
```

### 6.5 缓存按天清理

用 5 个不同日期的文件实测 30 天阈值：

```
pathing_0d.zip   2026-11-21   保留
pathing_5d.zip   2026-11-16   保留
pathing_20d.zip  2026-11-01   保留
pathing_35d.zip  2026-10-17   ← 删除
pathing_60d.zip  2026-09-22   ← 删除
```

---

## 7. 产物

| 项目 | 值 |
|---|---|
| 客户端版本 | **3.0.0.0** |
| 客户端成品 | `artifacts\dist-3.0.0\SoulLink.exe` · 3.28 MB |
| 服务端版本 | **2.0.0.0** |
| 服务端成品 | `artifacts\dist-server-3.0.0\SoulLink.Server.exe` · 51.99 MB |
| 2.17.50 留档 | `dist\SoulLink-2.17.50-before-net11.exe` |

> ⚠ **`dist\SoulLink.exe` 未被覆盖**（托盘实例 PID 187028 占用）。
> 正式发版：**退出托盘 → 跑一次 `build.bat`**。

---

## 8. 上线清单

### 8.1 客户端

1. 目标机器跑 `check-runtime.ps1`，缺运行时就装
2. 部署 `artifacts\dist-3.0.0\` 整个目录（不是单个 exe）
3. 首次启动后，设置页如出现橙色迁移面板，点「迁移到当前目录」
4. 迁完后**先重启确认配置都在**，再让它删旧目录
5. 按需在设置页调整备份保留次数 / 缓存天数

### 8.2 服务端

只改了版本号，业务逻辑未动（WAL/事务/去 N+1 是 1.12.0 已上线的）。

1. 覆盖 `/opt/soullink-server/SoulLink.Server`
2. `sudo systemctl reset-failed soullink-server` 再 `start`
3. `curl http://127.0.0.1:5080/api/ping` 确认 `version` 是 `2.0.0.0`

---

## 9. 已知遗留

| 项 | 说明 | 建议 |
|---|---|---|
| **909 个构建产物被 git 跟踪** | `tools/layout-check/obj*/**`，因为该目录的 `Directory.Build.props` 把输出目录改成了 `obj<项目名>`，`.gitignore` 的 `tools/**/obj*/` 没匹配上 | 加 `tools/layout-check/obj*/` 忽略规则再 `git rm -r --cached` |
| 9 个损坏的历史探针 | 引用了早已重构掉的成员（`ProcessCommandLine` 等），**在 net8 基线上同样失败**，与本次迁移无关 | 要么删，要么补 |
| .NET 11 自包含单文件回归 | 见 §1.3 | 等上游修复后可切回自包含 |
| `build.bat` 覆盖失败时 | 给出明确提示并保留暂存产物 | 建议保持「明确失败」，不要自动改用别的文件名（那样用户会以为更新成功了） |

---

## 10. 教训

### 10.1 「能编译」和「能发版」是两件事

net11 编译、运行、框架依赖发布全正常，**只有自包含单文件会炸**。
如果只在 IDE 里跑就宣布成功，用户拿到的是双击闪退的 exe。
**验收必须用最终发布形态跑。**

### 10.2 换个发布形态能绕过整类问题

这次没有去修 `BadImageFormatException`（那是上游的），而是换了发布形态。
**遇到「某个打包方式坏了」，先问「能不能换一种打包方式」，比死磕快得多。**

### 10.3 框架依赖不是「退化」

直觉上「自包含 = 免安装 = 更好」，但实测：3.28 MB vs 67.7 MB，启动快 1.5 秒。
**前提是用户能装运行时**——这是产品决策，不是技术决策。

### 10.4 系统自带的报错框不够好，必须自己补一层

`apphost` 的缺失运行时对话框指向的是**基础运行时**，而 WPF 要的是**桌面运行时**。
用户照着提示装完还是跑不起来 —— 这种坑必须用外部脚本堵。

### 10.5 备份的语义是「恢复后必须能直接用」

为了省 60 MB 而跳过图片/字体/DLL，看起来是聪明的优化，
实际造出的是**恢复不完整的备份**。用户一句话点破了：备份就该备份完整。
**优化不能突破功能的语义边界。**

### 10.6 分析器的「修复建议」不能照抄

`WFO0003` 让你把 DPI 设置从 manifest 删掉、改用 `Application.SetHighDpiMode`。
这对 WinForms 成立，对 **WPF 应用会直接破坏高 DPI**。
**看分析器建议，先确认它预设的宿主模型和你的项目一致。**

### 10.7 数据目录「改一处」不等于「改完」

第一版只改了 `AppIdentity.ConfigDir`，以为万事大吉。
实际还有 4 处硬编码 `%LOCALAPPDATA%`（`route-cache`、`route-backups`×2、
`device.json`、`flow-menu.log`）。
**改路径类需求，必须全仓 grep 所有 `GetFolderPath` 调用点。**

### 10.8 复用已有结论能省大量重复劳动

2.17.47 已为单文件修过 `Find7za()`（用 `Environment.ProcessPath` 而非
`AppContext.BaseDirectory`）。这次换发布形态，那套路径逻辑**一行没改就兼容**。
**踩过的坑要写成代码注释，下次才有人能接住。**

### 10.9 PowerShell 脚本写中文必须存成 UTF-8 BOM

无 BOM 时 PowerShell 5.1 按 ANSI（GBK）解码，中文全乱、连引号配对都会失败，
报一堆莫名其妙的语法错误。
**验证方法**：`[Parser]::ParseFile()` 做语法检查，别等运行时才发现。

---

## 11. 本轮提交记录

```
8a08aef 2.21.2 路线包缓存按天过期 + 启动自动清理
b5f38d4 2.21.1 修正备份完整性 + 补齐剩余 C 盘路径
26b95d6 2.21.0 BGI 备份改 zip + 保留次数可配 + 旧数据一键迁移
2522158 2.20.0 数据目录迁到 exe 所在文件夹 + BGI 备份自动清理
dea94fc 2.19.0 迁移 .NET 11 + 改为框架依赖发布（exe 67.7MB → 3.27MB）
98b3a3f 2.18.0 交接文档：net11 单文件回归的完整证据链、降级 net10 的原因
38ca483 2.18.0 迁移到 .NET 10（原计划 .NET 11，因单文件发布回归降级）
4678fc2 锁 .NET SDK 11.0.100
```

> 2.18.0 是迁移过程中的中间版本（曾一度降级到 net10），版本号已跳过；
> 最终 3.0.0 直接使用 net11 + 框架依赖。
