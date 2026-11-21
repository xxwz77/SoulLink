# SoulLink 交接文档 · 2.18.0（.NET 8 → .NET 10 迁移）

> 时间：2026-11-21 04:35
> 版本：客户端 **2.18.0.0** · 服务端 **1.13.0.0**
> 分支：`migrate/net10`（从 `main` 856cdd0 切出）
> 性质：**运行时迁移 + 发版脚本修复**，无接口变更、无界面行为变更
> 结论：**目标框架用 net10 而不是 net11**，原因是 net11 单文件发布有回归，见 §3

---

## 0. 一句话现状

85 个工程的目标框架从 `net8.0(-windows)` 迁到 `net10.0(-windows)`，
编译 0 错误、警告数不增加，客户端与服务端单文件产物均已实测可启动。
**原计划迁到 .NET 11，实测发现 .NET 11 的 WPF 单文件发布有通用回归，故降级到 .NET 10 LTS。**

---

## 1. 为什么要动

`.NET 8` 的支持期在 **2026-11-10 已经结束**，继续用等于跑无安全补丁的运行时。
`net10.0` 是 LTS，支持到 **2028-11**。

---

## 2. 改了什么

### 2.1 目标框架（86 个 csproj）

| 原 | 现 | 数量 |
|---|---|---|
| `net8.0-windows` | `net10.0-windows` | 63 |
| `net8.0` | `net10.0` | 23 |

全部是**单行替换**，没有改任何 `DefineConstants`、条件编译或 `Condition` 表达式
（迁移前已 grep 确认：全仓没有 `#if NET*`、没有 `RuntimeInformation` 的版本分支）。

### 2.2 NuGet（全项目只有两个包）

```
Microsoft.Extensions.Hosting.WindowsServices   8.0.1   → 10.0.15
Microsoft.Data.Sqlite                          8.0.11  → 10.0.15
```

### 2.3 版本号

```
客户端  2.17.50 → 2.18.0
服务端  1.12.0  → 1.13.0   （便于用 /api/ping 确认线上是否已更新）
```

### 2.4 `global.json`（新增）

```json
{ "sdk": { "version": "10.0.403", "rollForward": "latestFeature", "allowPrerelease": false } }
```

本机装了 8/9/10/11 四套 SDK，不锁会悄悄漂移。
`latestFeature` 允许 10.0.4xx 内小幅前进，不会跳到 11。

> 注意：这个文件只影响**编译这个仓库的机器**，不会被打进 exe，
> 对最终用户零影响（发布是 self-contained，用户机器连 .NET 都不用装）。

### 2.5 `build.bat`（三处修正）

| # | 原问题 | 现在 |
|---|---|---|
| 1 | `rmdir /s /q dist` 清空 dist，会删掉 19 个历史留档 exe | 发布到 `artifacts\dist-<ver>` 再覆盖 `dist\SoulLink.exe`；覆盖失败时明确提示"文件正在运行" |
| 2 | 版本号手写，和 csproj 容易对不上 | `dotnet msbuild -getProperty:Version` 读取 |
| 3 | 提示文案写的是".NET 8 SDK" | 改为 .NET 10 |

> 试过用 `findstr` 抓 `<Version>`，但 csproj 里还有 FileVersion/AssemblyVersion，
> 取出来是 `Version>2.17.50` 这种脏值，不可靠，已换成 MSBuild 属性。

---

## 3. ★ 核心结论：为什么不是 .NET 11

这是本次迁移最重要的发现，**下次想升 11 请先看这里**。

### 3.1 现象

`net11.0-windows` 编译、运行、框架依赖发布**全部正常**，
唯独 **单文件自包含发布** 时，进程启动即崩溃：

```
Unhandled exception. System.TypeInitializationException:
  The type initializer for 'System.Windows.Application' threw an exception.
 ---> System.BadImageFormatException:
File name: 'DirectWriteForwarder.dll'
   at System.Windows.Application.ApplicationInit()
   at SoulLink.App..ctor()
```

退出码 `-532462766`（0xE0434352）。

### 3.2 这是 .NET 11 的通用回归，不是本项目的问题

**最小复现**：新建一个空 WPF 工程（只有一个 `Application` + 一个 `Window`），
用完全相同的发布参数：

| 目标框架 | 单文件产物 | 结果 |
|---|---|---|
| net8.0-windows | 68.30 MB | ✅ RUNNING |
| net10.0-windows | 61.85 MB | ✅ RUNNING |
| **net11.0-windows** | 64.35 MB | ❌ **CRASH-BadImageFormat** |

同一个现象在本项目上也复现，且 **net10 单文件 67.69 MB 启动正常**。

### 3.3 根因

`DirectWriteForwarder.dll` 是 WPF 的**混合模式程序集**（含原生代码），
.NET 11 重写了它：

| 运行时 | 文件大小 | CLR 目录 RVA |
|---|---|---|
| 8.0.33 | 526,160 | 0x1600 |
| 10.0.14 | 509,736 | 0x1600 |
| **11.0.0** | **203,048** | **0x16448** |

文件缩到 40%、CLR 目录位置也变了，单文件的内存加载路径处理不了它。

### 3.4 试过的绕过方案（都不推荐）

`-p:IncludeAllContentForSelfExtract=true` **确实能让它跑起来**（实测主窗口正常显示），
但构建时会报：

```
warning NETSDK1244: "IncludeAllContentForSelfExtract" 会启用一种旧版兼容模式……
                    从项目中移除该属性，改用默认的内存中单文件行为。
```

官方文档原话：**"This mode is not recommended: it's a .NET Core 3.1 compatibility mode
and might be removed in a future release."**

即：这是条会被移除的退路，且每次启动都要把全部托管程序集解压到磁盘（`%TEMP%\.net`），
启动变慢、还多一个可被篡改的解压目录。**不适合作为正式发布方案。**

### 3.5 结论

- **目标框架定 net10.0**（LTS，单文件发布已验证可用）
- 想升 .NET 11 的前置条件：**等它修好这个回归**，或**改用框架依赖发布**
  （`--self-contained false`，但用户就得自己装 .NET 11 运行时，违背免安装的初衷）
- 升级时先跑一遍 §3.2 的最小复现，一分钟就能判定

---

## 4. 顺带修掉的告警

net11 的 WinForms/WPF 分析器新增了两类告警（`WFO2000`、`WFO0003`）。
虽然是 net11 才引入的，但既然发现了就一并修干净——**全部是等价替换，不改行为**。

| 位置 | 原实现 | 现在 |
|---|---|---|
| `RdpConsole.CandidateClsids` | 每次访问都 `new` 一个数组 | 静态只读缓存 |
| `Models.Account.SessionTagBrush` | getter 里每次 new 画刷 | 冻结的静态实例 |
| `LogBus.LogEntry.TextColor` | 同上（5 种颜色） | 5 个冻结静态实例 |
| `LaunchPage.ActionVm` 的 `NextRunBrush`/`ScheduleToggle*`/`Badge*` | 同上（12 处） | 冻结静态实例，按主题/状态分档 |
| `RdpAxHost.Ocx` | `object` 但失败分支赋 null | `object?`（把既有事实写进类型） |

**为什么画刷要 `Freeze()`**：WPF 绑定会对同一属性反复求值，列表越长重复分配越多。
冻结点之后是只读对象，可跨线程复用、渲染开销更低。颜色值逐字节核对过，**像素结果完全一致**。

> ⚠ 冻结的前提是**绝不原地改这个画刷的颜色**。
> 迁移前已全仓 grep 确认：没有任何地方对这几个属性返回的画刷做 `.Color =` 赋值或动画。
> 唯一做 `.Color =` 的是 `EngineDot`/`GlowDot`，那是 `Color` 依赖属性，不受影响。

### 告警数对比

```
net8 基线：132 个警告 / 66 个不同点位
迁移之后：130 个警告 / 65 个不同点位
唯一差异：WFAC010 → WFO0003（同名同义，只是分析器编号变了）
```

`WFO0003` 的提示是"从 app.manifest 删除高 DPI 设置，改用 `Application.SetHighDpiMode`"。
**不要照做**：那是 WinForms 的 API，本应用是 WPF 启动（`Application` + `app.manifest`），
DPI 只能靠清单声明。删掉会真的破坏高 DPI 行为。

---

## 5. 验证

### 5.1 构建

```
客户端 Release：0 错误 / 130 警告（net8 基线 132）
服务端 Release：0 错误 / 0 警告
```

### 5.2 全工程构建（86 个 csproj）

```
77 通过 · 9 失败
```

**9 个失败全部与迁移无关**——用独立 worktree 在 **net8 基线（856cdd0）上重编，报错一模一样**：

```
AgentCmdProbe / CmdLineProbe / WhyHostProbe   CS0103: ProcessCommandLine 不存在
BgiStartGuardCheck                            CS0117: BgiTaskPreparer.ReadCancelHotkeyFor 不存在
BusinessDayCheck                              CS2001: Services/BusinessDayData.cs 文件不存在
ChannelDiscoveryCheck                         CS0103: BrokerInstall 不存在
DupAgentProbe                                 CS0117: ManualAgent.FindManualAgents 不存在
LampProbe                                     CS1061: FlowCanvas._debugLastStatus 不存在
ValidatorCheck                                CS0103: BgiDataDependency 不存在
```

这些都是**探针引用了早已重构掉的成员**，属于历史遗留，本次不动（改动它们会扩大风险面）。

### 5.3 客户端单文件启动

用"复制发布目录 + 把 exe 改名"的方式拿独立身份（避免撞上你正在跑的生产实例的互斥体）：

```
RUNNING: True
error.log: False
boot.log: main window shown: True state=Normal
```

### 5.4 服务端单文件（本机，独立库 + 独立目录）

```
PING: {"ok":true,"version":"1.13.0.0"}
30 路并发（建房 / 列表 / 心跳）→ OK=30 FAIL=0
房间数: 30
WAL: soul-link.db / -wal / -shm 三个文件都在
stderr: 空
```

---

## 6. 产物

| 项目 | 值 |
|---|---|
| 客户端版本 | **2.18.0.0** |
| 客户端成品 | `artifacts\dist-2.18.0\SoulLink.exe` · 67.69 MB |
| 客户端 SHA256 | `76CBC07964923D4C79C50AA6C9A43DDB3CBD5DBC5DFD4D28786507E706447349` |
| 服务端版本 | **1.13.0.0** |
| 服务端成品 | `artifacts\dist-server-2.18.0\SoulLink.Server.exe` · 49.15 MB |
| 服务端 SHA256 | `B0444E74514A117E3FD279A0B419CED9BC6BFE1EBCB04CD73158CF193E0ABC6B` |
| 2.17.50 留档 | `dist\SoulLink-2.17.50-before-net11.exe`（SHA256 与上一份交接文档一致） |

> ⚠ **`dist\SoulLink.exe` 本次没有覆盖。**
> 它当时正被托盘实例占用（PID 187028），`build.bat` 已按设计给出明确提示并保留了暂存产物。
> **要正式发版：先退出托盘里的 SoulLink，再跑一次 `build.bat` 即可。**
> 2.17.50 的原件已备份为 `dist\SoulLink-2.17.50-before-net11.exe`。

---

## 7. 上线注意

服务端这次只改了目标框架和版本号，**没有动 `Db.cs` / `Lobby.cs` 的任何逻辑**，
WAL、事务、去 N+1 那套是 1.12.0 已经上线并验证过的，本次原样保留。

升级步骤与 `docs/SERVER-UPDATE-GUIDE.md` 一致，注意：
1. 覆盖二进制后 `sudo systemctl reset-failed soullink-server` 再 `start`
2. 用 `curl http://127.0.0.1:5080/api/ping` 确认 `version` 是 `1.13.0.0`

---

## 8. 已知遗留

| 项 | 说明 | 建议 |
|---|---|---|
| **909 个构建产物被 git 跟踪** | `tools/layout-check/obj*/**`。根因是该目录的 `Directory.Build.props` 把输出目录改成了 `obj<项目名>`，而 `.gitignore` 只忽略了 `obj/`，`tools/**/obj*/` 没匹配上 | 加一条 `tools/layout-check/obj*/` 忽略规则，再 `git rm -r --cached` 清理。**本次未动**，避免把 909 个文件塞进这次提交 |
| .NET 11 单文件回归 | 见 §3 | 等上游修复；升级前先跑 §3.2 最小复现 |
| 9 个损坏的历史探针 | 见 §5.2 | 要么删掉，要么补上缺失成员。都是 `tools/` 下的一次性验证程序 |
| `build.bat` 覆盖失败时的退出 | 现在是提示后 `goto :fail` | 可以改成"目标被占用时自动改用 `dist\SoulLink-<ver>.exe` 并存"，但那样容易让用户以为更新成功，**建议保持现在这种"明确失败"** |

---

## 9. 本轮提交记录

```
38ca483 2.18.0 迁移到 .NET 10（原计划 .NET 11，因单文件发布回归降级）
```

---

## 10. 教训

### 10.1 "能编译"和"能发版"是两件事

net11 从头到尾编译干净、框架依赖发布也能跑，
**只有走到 `PublishSingleFile` 这一步才炸**。
如果只在 IDE 里跑一遍就宣布迁移成功，用户拿到的会是双击即闪退的 exe。
**验收必须用最终发布形态（单文件自包含）跑一遍。**

### 10.2 最小复现比读文档快得多

发现 `BadImageFormatException` 后，第一反应容易是去翻自己的代码/清单/P-Invoke。
实际做法：**建一个空 WPF 工程，三个 TFM 各发一次**，十分钟就锁定了
"这是 .NET 11 的回归、与项目无关"，避免了在错误方向上浪费时间。

### 10.3 分析器的"修复建议"不能照抄

`WFO0003` 让你把 DPI 设置从 manifest 删掉、改用 `Application.SetHighDpiMode`。
这个建议对 WinForms 应用成立，对 **WPF 应用会直接破坏高 DPI**。
**看到分析器建议，先确认它预设的宿主模型和你的项目一致。**

### 10.4 等价替换也要验证等价性

把 12 个 `=> new SolidColorBrush(...)` 改成静态冻结实例，
前提是"这些画刷从不被原地修改"。这个前提**不能假设，要 grep 验证**——
一旦某处对它做了 `.Color =` 或动画，冻结后会直接抛 `InvalidOperationException`。
本次已验证：唯一做 `.Color =` 的是 `Color` 依赖属性，与这些 `Brush` 属性无关。

### 10.5 `git status` 里有不该有的东西时，先查清楚再提交

本次改动本来只有 93 个文件，但 `git status` 显示了 275 个。
多出来的是 909 个被误跟踪的构建产物。**直接 `git commit -a` 会把它们一起卷进来**。
做法：`git reset` 后按扩展名精确 `git add`，提交前确认 `git diff --cached --name-only | grep obj` 为空。
