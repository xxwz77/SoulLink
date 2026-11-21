# SoulLink 交接文档 · 2.19.0（迁移 .NET 11 + 改为框架依赖发布）

> 时间：2026-11-21 10:45
> 版本：客户端 **2.19.0.0** · 服务端 **1.14.0.0**
> 分支：`migrate/net11`（从 `main` 856cdd0 切出，先后经历 net10 降级与回升）
> 性质：**运行时迁移 + 发布形态变更**，无接口变更、无界面行为变更

---

## 0. 一句话现状

目标框架从 `net8` 迁到 **`net11`**，发布形态从「自包含单文件」改为
**「框架依赖 + 单文件」**：exe 从 **67.7 MB 降到 3.27 MB**，启动**反而快 1.5 秒**。
代价是用户必须先装 **.NET 11 桌面运行时**，随包附带 `check-runtime.ps1` 引导。

---

## 1. 为什么最终是 .NET 11

`.NET 8` 支持期已于 **2026-11-10 结束**。.NET 11 虽为 STS（支持到约 2028-05），
但它是**唯一能让 exe 缩到 3 MB 的版本**——见 §3。

---

## 2. 改了什么

### 2.1 目标框架（86 个 csproj）

| 原 | 现 | 数量 |
|---|---|---|
| `net8.0-windows` | `net11.0-windows` | 63 |
| `net8.0` | `net11.0` | 23 |

全部单行替换。迁移前已 grep 确认全仓无 `#if NET*` 条件编译、无运行时版本分支。

### 2.2 NuGet

```
Microsoft.Extensions.Hosting.WindowsServices   8.0.1   → 11.0.0
Microsoft.Data.Sqlite                          8.0.11  → 11.0.0
```

### 2.3 版本号

```
客户端  2.17.50 → 2.19.0
服务端  1.12.0  → 1.14.0
```

> 2.18.0 曾是「迁移到 net10」的中间版本，未发布，版本号已跳过。

### 2.4 global.json

```json
{ "sdk": { "version": "11.0.100", "rollForward": "latestPatch", "allowPrerelease": false } }
```

### 2.5 SoulLink.csproj 新增

```xml
<RollForward>LatestMajor</RollForward>
```

写进 `runtimeconfig.json`，含义：**用户装了任何 ≥ 11 的桌面运行时都能跑**，
不要求版本号精确匹配。避免"用户装了 .NET 12 反而起不来"这类支持问题。

### 2.6 新增 `tools/check-runtime.ps1`

运行时检测 + 安装引导脚本，随包发布。检测逻辑见 §3.4。

---

## 3. ★ 核心：为什么是「框架依赖」而不是「自包含」

这是本次最重要的一组实测结论，**下次动发布形态前请先看完这一节**。

### 3.1 自包含单文件在 net11 上会崩

```
Unhandled exception. System.TypeInitializationException:
  The type initializer for 'System.Windows.Application' threw an exception.
 ---> System.BadImageFormatException: 'DirectWriteForwarder.dll'
   at System.Windows.Application.ApplicationInit()
```

**最小复现**（空 WPF 工程，相同发布参数）：

| 目标框架 | 自包含单文件 | 结果 |
|---|---|---|
| net8.0-windows | 68.30 MB | ✅ 正常 |
| net10.0-windows | 61.85 MB | ✅ 正常 |
| **net11.0-windows** | 64.35 MB | ❌ **崩溃** |

根因：`DirectWriteForwarder.dll` 是 WPF 混合模式程序集，.NET 11 重写了它：

| 运行时 | 文件大小 | CLR 目录 RVA |
|---|---|---|
| 8.0.33 | 526,160 | 0x1600 |
| 10.0.14 | 509,736 | 0x1600 |
| **11.0.0** | **203,048** | **0x16448** |

它是 **.NET 11 的通用回归**，与项目代码无关。

### 3.2 框架依赖绕开了这个回归

框架依赖时 `DirectWriteForwarder.dll` **不进 bundle**，直接从用户安装的运行时加载：

```
程序目录里有 DirectWriteForwarder.dll 吗？ → False
主窗口显示： True   error.log：无
```

### 3.3 三种发布形态实测对比

| 模式 | exe 体积 | 目录总大小 | 启动（3 次中位） |
|---|---|---|---|
| net10 自包含单文件 | 67.69 MB | 约 326 MB | 4101 ms |
| net10 框架依赖单文件 | 3.70 MB | — | 2857 ms |
| **net11 框架依赖单文件** | **3.27 MB** | **约 39 MB** | **2617 ms** |

框架依赖不仅体积降到 **1/20**，启动还**快了 1.5 秒**——不用解压、
直接映射共享运行时，且运行时是 ReadyToRun 预编译的。

### 3.4 用户没装运行时会怎样（实测）

双击 exe 会弹出**系统级对话框**（`apphost` 内置，无需写代码）：

```
You must install or update .NET to run this application.

Required: 'Microsoft.NETCore.App', version '11.0.0' (x64)

Learn more:  https://aka.ms/dotnet/app-launch-failed
Download link: https://aka.ms/dotnet-core-applaunch?framework=...&gui=true

[ Download it now ]  [ 查看详细信息(D) ]  [ 关闭(C) ]
```

**点 "Download it now" 会跳转到微软官方页面**，链接里已带好框架名、版本、架构。

**但它有两个问题**：
1. 中英文混杂，体验割裂
2. 提示写的是 `Microsoft.NETCore.App`（基础运行时），而 WPF 应用需要的是
   **`Microsoft.WindowsDesktop.App`（桌面运行时）**，用户容易下错

所以额外提供了 `check-runtime.ps1`，它会：
- 列出已装的所有桌面运行时，标出哪几个够用
- 缺了就询问是否打开**正确的**下载页（`dotnet.microsoft.com/download/dotnet/11.0/runtime`）
- 明确提示「选 Desktop Runtime，不要选 Runtime」

---

## 4. 顺带修掉的告警

net11 分析器新增 `WFO2000` / `WFO0003`。已全部处理，**均为等价替换**：

| 位置 | 原实现 | 现在 |
|---|---|---|
| `RdpConsole.CandidateClsids` | 每次访问 new 数组 | 静态只读缓存 |
| `Models.Account.SessionTagBrush` | getter 里每次 new 画刷 | 冻结静态实例 |
| `LogBus.LogEntry.TextColor` | 同上（5 色） | 5 个冻结静态实例 |
| `LaunchPage.ActionVm` 的 12 个画刷 | 同上 | 冻结静态实例 |
| `RdpAxHost.Ocx` | `object` 但失败分支赋 null | `object?` |

**冻结前提已验证**：全仓 grep 确认没有任何地方对这些画刷做 `.Color =` 赋值或动画。
唯一做 `.Color =` 的是 `EngineDot`/`GlowDot`，那是 `Color` 依赖属性，不受影响。

告警数：net8 基线 132 → 现在 130。唯一差异是 `WFAC010` 改名 `WFO0003`。

> ⚠ `WFO0003` 建议删除 manifest 里的 DPI 设置——**不要照做**。
> 那是 WinForms 的 API，本应用是 WPF 启动，DPI 只能靠清单声明。

---

## 5. build.bat

三处关键变更（前两次都踩过坑，注释写在脚本里）：

| # | 问题 | 现在 |
|---|---|---|
| 1 | `rmdir /s /q dist` 会删掉 19 个历史留档 exe | 发布到 `artifacts\dist-<ver>` 再覆盖；失败时明确提示 |
| 2 | 版本号手写易错 | `dotnet msbuild -getProperty:Version` |
| 3 | 发布形态 | `--self-contained false` + `PublishSingleFile`；**不能**加 `EnableCompressionInSingleFile`（框架依赖不支持压缩，报 NETSDK1176） |

---

## 6. 验证

### 6.1 构建

```
客户端 Release：0 错误 / 130 警告（net8 基线 132）
服务端 Release：0 错误 / 0 警告
```

### 6.2 客户端产物

```
artifacts\dist-2.19.0\
  SoulLink.exe          3.27 MB
  7za.exe               1.23 MB
  check-runtime.ps1       4 KB
  broker-install.ps1
  broker-uninstall.ps1
独立身份启动：RUNNING，主窗口 Normal，无 error.log
```

### 6.3 服务端（自包含单文件，Linux 部署用）

```
PING: {"ok":true,"version":"1.14.0.0"}
30 路并发（建房/列表/心跳）→ OK=30 FAIL=0
WAL: soul-link.db / -wal / -shm 三个文件都在
stderr: 空
```

### 6.4 check-runtime.ps1

```
已安装的 .NET 桌面运行时：
  [OK] Microsoft.WindowsDesktop.App 11.0.0
  [旧] Microsoft.WindowsDesktop.App 10.0.14
  ...
√ 可以运行 SoulLink（将使用 Microsoft.WindowsDesktop.App 11.0.0）
```

缺运行时分支也已测试（把阈值临时改成 99 模拟），正确输出下载引导，退出码 1。

---

## 7. 产物

| 项目 | 值 |
|---|---|
| 客户端版本 | **2.19.0.0** |
| 客户端成品 | `artifacts\dist-2.19.0\SoulLink.exe` · 3.27 MB |
| 服务端版本 | **1.14.0.0** |
| 服务端成品 | `artifacts\dist-server-2.19.0\SoulLink.Server.exe` · 51.99 MB |
| 服务端 SHA256 | `70EDF96C00AD2A0A9BC6817EDDB3A9252D8D3DFDA3AA3268B2032B1A2F489228` |
| 2.17.50 客户端留档 | `dist\SoulLink-2.17.50-before-net11.exe` |

> ⚠ **`dist\SoulLink.exe` 本次仍未被覆盖**（托盘实例占用，PID 187028）。
> 正式发版：退出托盘 → 跑 `build.bat`。

---

## 8. 上线的重大变化

**这是本次唯一需要用户配合的变更**：
用户机器必须安装 **.NET 11 桌面运行时**（约 55 MB，微软官方组件）。

部署步骤：
1. 目标机器运行 `check-runtime.ps1`
2. 缺运行时就按提示装
3. 装完双击 `SoulLink.exe`

如果用户机器不方便装东西，**回退方案**：把 `build.bat` 的
`--self-contained false` 改回 `true`，并在 `SoulLink.csproj` 里把
`TargetFramework` 改回 `net10.0-windows`（net11 的自包含单文件是坏的）。
改回后 exe 约 67.7 MB，但双击即用。

---

## 9. 已知遗留

| 项 | 说明 | 建议 |
|---|---|---|
| **909 个构建产物被 git 跟踪** | `tools/layout-check/obj*/**`，因为该目录的 `Directory.Build.props` 把输出目录改成了 `obj<项目名>`，`.gitignore` 的 `tools/**/obj*/` 没匹配上 | 加一条 `tools/layout-check/obj*/` 忽略规则再 `git rm -r --cached` |
| 9 个损坏的历史探针 | 引用了早已重构掉的成员（`ProcessCommandLine` 等），在 net8 基线上同样失败 | 要么删，要么补 |
| .NET 11 自包含单文件回归 | 见 §3.1，本次靠框架依赖绕开 | 等上游修复后可切回自包含 |

---

## 10. 教训

### 10.1 "能编译"和"能发版"是两件事
net11 编译、运行、框架依赖发布全正常，**只有自包含单文件会炸**。
如果只在 IDE 里跑就宣布成功，用户拿到的会是双击闪退的 exe。
**验收必须用最终发布形态跑。**

### 10.2 换个发布形态能绕过整类问题
这次没有去修 `BadImageFormatException`（那是上游的），而是换了发布形态。
**遇到"某个打包方式坏了"，先问"能不能换一种打包方式"，比死磕快得多。**

### 10.3 框架依赖不是"退化"，是更优解
直觉上"自包含 = 免安装 = 更好"，但实测：3.27 MB vs 67.7 MB，
启动快 1.5 秒。**前提是用户能装运行时**——这是产品决策，不是技术决策。

### 10.4 系统自带的报错框不够好，需要自己补一层
`apphost` 的缺失运行时对话框是英文的，且指向 `Microsoft.NETCore.App`（基础运行时），
而 WPF 要的是 `Microsoft.WindowsDesktop.App`（桌面运行时）。
**用户照着提示装完还是跑不起来，这种坑必须用外部脚本堵。**

### 10.5 PowerShell 脚本里写中文必须存成 UTF-8 BOM
无 BOM 时 PowerShell 5.1 按 ANSI（GBK）解码，中文全乱，
连引号配对都会失败，报一堆莫名其妙的语法错误。
**验证方法**：`[Parser]::ParseFile()` 做语法检查，别等运行时才发现。

### 10.6 复用已有结论能省掉大量重复劳动
2.17.47 已经为单文件修复过 `Find7za()`（用 `Environment.ProcessPath` 而非
`AppContext.BaseDirectory`）。这次换发布形态，那套路径逻辑**一行没改就兼容**。
**踩过的坑要写成代码里的注释，下次才有人能接住。**
