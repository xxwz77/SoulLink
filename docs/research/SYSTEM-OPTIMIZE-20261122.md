# 系统优化：可关服务白名单 + Defender 目录排除（SYSTEM-OPTIMIZE）

> 日期：2026-11-22
> 作者：virtual-controls（agent-team）
> 关联实现：`Services/SystemOptimizer.cs`、`Views/SettingsPage.xaml(.cs)`（设置页「系统优化」区块）
> 验收状态：`dotnet build SoulLink.csproj -t:Rebuild` **0 error**（两个 TFM 均通过）
> 合规声明：**未实际停止任何服务、未实际改动任何 Defender 排除项**；本文所有命令只描述实现，未在本机执行。

---

## 0. 结论摘要（TL;DR）

1. 实现了一个**服务白名单**（仅 6 项明确安全的服务），提供「检测状态 + 停止/恢复」；**只做本次停止**（`sc stop`/`sc start`），**绝不改启动类型**，重启后自动恢复默认，零永久改动。
2. 实现 **Defender 目录排除**：查看现有排除项、把 SoulLink 数据目录加入排除、逐个移除；**只增删目录路径，绝不关闭实时保护**。
3. 两处操作**都需要管理员权限**，UI 层有权限判断 + 二次确认 + 明确影响范围提示。
4. **安全边界**：白名单制（其余服务一律不碰）、不引用 `System.Management`/`System.ServiceProcess`（单文件发布省体积，服务走 `sc.exe`，Defender 走 `powershell.exe`）。

---

## 1. 服务白名单（6 项）

| 服务名 | 显示名 | 为什么可安全停 | 按需自启 |
|---|---|---|---|
| `SysMain` | 内存预取（Superfetch） | 挂机时持续预读磁盘，纯浪费 I/O；停掉不影响已运行程序。**如果经常启动新程序，可能感觉略慢** | 否 |
| `DiagTrack` | 遥测（Connected User Experiences） | 只上传遥测，停掉不影响系统功能 | 否 |
| `WSearch` | Windows 搜索索引 | 后台持续索引磁盘（44 MB）；**停掉后文件管理器搜索会明显变慢（退化为逐文件扫描），开始菜单搜程序不受影响** | 否 |
| `FontCache` | 字体缓存（Font Cache） | 字体缓存；**停掉后字体枚举变慢（冷启动首次加载时逐文件扫描）。无自动重启，重启系统后恢复** | 否 |
| `BITS` | 后台智能传输（BITS） | Windows 更新下载用；停掉只是推迟更新下载（**按需启动，更新需要时会自动拉起**） | **是** |
| `Spooler` | 打印后台（Print Spooler） | 挂机用不到打印；**停掉更安全（规避打印漏洞）。极少数安装程序会检查此服务，如遇异常请恢复** | 否 |

> **v1.1 修订（2026-11-22）**：核对了本机实际服务配置，修正两处「按需自启」误判。
> - `FontCache` 实测 `START_TYPE: AUTO_START` 且**无 start/stop triggers**——**不会自动重启**（原误标「按需自动重启」）。停掉后的真实影响是「字体枚举变慢」，不是「没影响」。
> - `WSearch` 实测 `AUTO_START (DELAYED)` 且**无 triggers**——同样**不会自动重启**（原误标「是」）。
> - `BITS` 实测 `START_TYPE: DEMAND_START`——**真正按需**，Windows 更新需要时会拉起它，保留「按需自启 = 是」。
> - `SysMain`/`DiagTrack`/`Spooler` 实测均 `AUTO_START` 无 triggers，维持「否」。

**选入标准**：
- ① 不依赖 `TermService` / RDP / 登录链路（停掉不会踢掉挂机会话）；
- ② 停掉不破坏桌面、游戏、挂机；
- ③ 重启即恢复（无永久改动）；
- ④ 与「挂机游戏机的内存/磁盘/后台」开销直接相关。

**明确排除的服务**（**绝不碰**）：`TermService`（远程桌面，停掉=踢掉所有挂机会话）、`UmRdpService`（RDP 端口重定向依赖）、`DcomLaunch`/`RpcSs`/`RpcEptMapper`（RPC 链）、`EventLog`、`Schedule`（本工具自身依赖计划任务）、`Power`、`Winmgmt`（WMI）、`AudioSrv`（部分游戏需要音频）、`StateRepository` 等系统关键服务。

---

## 2. 实现细节（Services/SystemOptimizer.cs）

### 2.1 服务停止 / 恢复

```
停止：sc.exe stop <服务名>   → 轮询 STATE 到 STOPPED（20s 超时）
恢复：sc.exe start <服务名>  → 轮询 STATE 到 RUNNING（20s 超时）
```

**关键坑（已在实现里处理）**：
- `sc stop` 对「已停止」返回 **1062**（`ERROR_SERVICE_NOT_ACTIVE`），不是失败——代码里显式放行。
- `sc start` 对「已在运行」返回 **1056**（`ERROR_SERVICE_ALREADY_RUNNING`），同样不是失败——放行。
- `sc query` 输出语言随系统变，**不能按文字匹配**，只解析 `STATE` 行冒号后的状态词（与 `RdpWrapService.QueryServiceState` 同法）。
- 控制台工具输出编码：**必须用系统 ANSI/OEM 代码页解码**（`SystemLauncher.ConsoleEncoding()`），中文系统 `sc.exe` 输出是 GBK(936)，用 `Encoding.Default`（恒为 UTF-8）会乱码。

### 2.2 Defender 目录排除

```
查看：Get-MpPreference -ErrorAction Stop | .ExclusionPath
添加：Add-MpPreference -ExclusionPath '<路径>'
移除：Remove-MpPreference -ExclusionPath '<路径>'
```

**关键坑**：
- `Get-MpPreference` 只在 **Defender 是活动杀软**时可用；装了火绒/360 等第三方杀软时 cmdlet 会报错，**属正常情况**，UI 文案已说明。
- 路径含单引号时先 `Replace("'", "''")` 转义，再包进 PowerShell 单引号字符串。
- 添加前先 `Directory.Exists` 校验（不存在的目录加入排除无意义且可能报错）。

### 2.3 权限与编码

- 服务与 Defender 操作**都强制校验 `SystemLauncher.IsElevated`**（非管理员直接返回带提示的失败，UI 再弹错误框）。
- PowerShell 走 `-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command`，输出强制 UTF-8（`$OutputEncoding=[Console]::OutputEncoding=[Text.Encoding]::UTF8`），对齐 `LocalUserService.RunPowerShellAsync` 的既有写法。

---

## 3. UI 集成（设置页「系统优化」区块）

位置：`Views/SettingsPage.xaml` 设置页，「网页访问」区块之后、「配置文件」区块之前。

**布局**：
- **可停止的后台服务**：6 项逐行列出（中文名 + 运行状态 + 停止/恢复按钮 + 需管理员提示）。
  - 停止 = 二次确认弹窗（写明「仅本次停止、不改启动类型、重启恢复、需管理员」）。
- **Defender 目录排除**：现有排除项逐行列出（路径 + 移除按钮）。
  - 「把数据目录加入排除」= 二次确认（写明「只排除该目录、不关闭实时保护、需管理员」）。
  - 「刷新」按钮重查服务状态与排除项。
  - 状态栏显示当前排除项数量或读取失败原因。

**交互规范**（对齐现有代码风格）：
- 全部弹窗走 `ThemedMessage.Confirm/Error`（主题消息框，非系统 MessageBox）。
- 操作写 `LogBus`（运行日志）。
- 进入设置页 `Activate()` 时自动刷新该区块（对齐现有 `RefreshAutoStartAsync` 等惰性刷新模式）。
- 动态行用代码构建（对齐 `RebuildBgiPathList` 的 `Children.Clear()` + `BuildRow` 模式），资源走 `FindResource("TextPrimary"/"GhostButton"/...)`。

---

## 4. 风险说明（务必阅读）

1. **服务停止的风险**：`SysMain`/`WSearch`/`Spooler`/`FontCache` 停掉后，如果用户忘了这是「挂机游戏机」而在上面办公/打印/搜索/加载字体，会感到功能缺失或变慢（尤其 `WSearch` 文件搜索退化为逐文件扫描）。**但因为是「仅本次停止」，重启即恢复**，且 UI 明确提示「重启后自动恢复」，风险可控。
2. **Defender 排除的风险**：被排除的目录，Defender **不再实时扫描**其下文件。SoulLink 数据目录放的是日志/缓存/备份，本身风险低；但若用户把别的敏感目录（比如下载目录）加进去，会**降低那部分的安全防护**。UI 已提示「只增删目录、不关闭实时保护」，且移除按钮随时可撤销。
3. **不做的（边界）**：
   - ❌ 不改服务启动类型（`sc config` 一律不调用）——避免「重启后服务永久禁用」这种难自愈的状态。
   - ❌ 不关闭 Defender 实时保护 / 云保护 / 篡改防护（`Set-MpPreference -DisableRealtimeMonitoring` 一律不调用）。
   - ❌ 不在白名单外的任何服务上操作。
4. **第三方杀软**：装了火绒/360 等时 `Get-MpPreference` 可能失败，UI 会显示「未启用 Defender 或装了第三方杀软」，此时 Defender 排除功能天然不可用（第三方杀软有自己的排除机制，不在本功能范围）。

---

## 5. 后续可选（本次未做，供决策）

- **服务状态常驻看板**：目前是「进入设置页时刷新一次」；可考虑加入 `TickHub` 周期刷新（遵循 AGENTS.md「新增周期任务走 TickHub」）。
- **一键「挂机优化」预设**：把「停 6 项服务 + 加 Defender 排除 + 会话瘦身」组合成一个按钮，一键执行 + 一键还原。
- **按账号差异化**：当前是机器级操作；若只想对挂机账号生效（而非主控制台），需评估服务是机器级的（无法按账号停），Defender 排除也是机器级的，故保持机器级 + 明确提示。

---

## 6. 相关文档

- `docs/RELEASE-RULES.md` §10-11 —— 性能约束（本功能不新增周期任务）
- `AGENTS.md` —— 工作区整洁 / 边界约定（本功能只改了 `Services/SystemOptimizer.cs` + `Views/SettingsPage.xaml(.cs)`）
- `Services/SessionSlimmer.cs` —— 会话瘦身（本功能的姊妹功能，同为「挂机机器级优化」）
- `Services/RdpWrapService.cs` —— `sc query` 状态解析的既有范式（本功能复用其思路）
