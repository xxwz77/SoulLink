# 注册表策略修复：代理写 HKCU（路径A）+ 补全 Widgets/Search 机器级策略

> 日期：2026-11-22
> 作者：rdpfix（AgentTeams `soullink-stream-opt` / task `task_mwrt1h3xnbfeiv`）
> 上游：`SESSION-SLIM-DEEP-20261122.md`（v2 论证 class=Machine 写 HKCU 无效）、`SESSION-SLIM-DEEP-V3-20261122.md`（v3 补实测）
> 实测环境：本机 Win11 10.0.29698，管理员权限（app.manifest `requireAdministrator`）

---

## 0. 结论先行

1. **根因**：`SessionSlimmer.ApplyStartupPolicy`（原 `:392-464`）写的是**用户分支**（挂载 NTUSER.DAT → `HKU\SoulLink_<user>\SOFTWARE\Policies\...`），但其中「小组件（`AllowNewsAndInterests`）」和「手机连接（`EnableMmx`）」的 ADMX 定义是 `class=Machine` —— **写用户分支系统不读、等于没生效**。用户最关心的「小组件」从没生效过。
2. **修复分两条**：
   - **机器级（全机）**：新增 `ApplyMachinePolicy(bool)`，写 HKLM 的 `DisableWidgetsBoard`（小组件面板）、`AllowNewsAndInterests`（新闻流）、`DisableSearch`（任务栏搜索 / SearchHost）。三键均 class=Machine。
   - **用户级（按账号）**：Edge 预启动策略保留为 class=User，但**改由注入代理在目标会话内写自己的 HKCU**（路径A），绕开「宿主挂载已登录用户 NTUSER.DAT 会失败」的死结。
3. **回滚只删值不删键**：`Dsh` 键删完值后保留空键，`reg delete /f` 报 `Access is denied`（管理员也删不掉），所以回滚一律删值、删失败静默忽略。

---

## 1. ADMX 查证（准确键名 + class）

从本机 `C:\Windows\PolicyDefinitions\*.admx` 原文读出：

| 策略 | class | key | valueName |
|---|---|---|---|
| `DisableWidgetsBoard` | **Machine** | `SOFTWARE\Policies\Microsoft\Dsh` | `DisableWidgetsBoard` |
| `AllowNewsAndInterests` | **Machine** | `SOFTWARE\Policies\Microsoft\Dsh` | `AllowNewsAndInterests` |
| `DisableWidgetsOnLockScreen` | Machine | `SOFTWARE\Policies\Microsoft\Dsh` | `DisableWidgetsOnLockScreen` |
| `DisableSearch` | **Machine** | `SOFTWARE\Policies\Microsoft\Windows\Windows Search` | `DisableSearch` |
| `AllowCortana` | Machine | `SOFTWARE\Policies\Microsoft\Windows\Windows Search` | `AllowCortana` |
| `EnableMMX` | Machine | `Software\Policies\Microsoft\Windows\System` | `EnableMmx` |
| `Edge\StartupBoostEnabled` | **User** | `SOFTWARE\Policies\Microsoft\Edge` | `StartupBoostEnabled` |
| `Edge\BackgroundModeEnabled` | **User** | `SOFTWARE\Policies\Microsoft\Edge` | `BackgroundModeEnabled` |

> 前 6 个 `class=Machine` 只能写 HKLM；后 2 个 Edge 是 `class=User`，写 HKCU 有效。

---

## 2. 修复内容

### 2.1 机器级策略（新增）

`SessionSlimmer.ApplyMachinePolicy(bool enable)`：

```
HKLM\SOFTWARE\Policies\Microsoft\Dsh\DisableWidgetsBoard = 1   （关小组件面板）
HKLM\SOFTWARE\Policies\Microsoft\Dsh\AllowNewsAndInterests = 0 （关新闻流）
HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search\DisableSearch = 1  （关任务栏搜索 / SearchHost）
```

- **作用域**：全机所有用户（含主控制台）。
- **生效**：写 HKLM 后**杀掉对应进程即不再自动重启**（perfmeasure 实测，已回滚），无需注销/重启。
- **影响**：任务栏搜索框消失、Win+S 失效、触摸板手势失效、开始菜单打字搜索失效；开始菜单本体正常、资源管理器内搜索正常。
- 另加 `IsMachinePolicyEnabled()` 只读探测 `DisableWidgetsBoard`，供 UI 对齐状态。

### 2.2 用户级策略（路径A：代理写 HKCU）

原 `ApplyStartupPolicy` 因「已登录用户 NTUSER.DAT 被独占」永远无法对 24h 挂机账号生效。现改：

- **代理侧**：`RelayAgent.Run()` 启动时读环境变量 `SOULLINK_USER_POLICY`（1=写禁用，0=删值，缺失=不动作），调 `SessionSlimmer.ApplyUserPolicy(enable)` 写**代理自己进程的 HKCU** —— 天然就是目标账号。
- **宿主侧**：`RelayClient.CaptureEnv()` 根据 `SessionSlimUserPolicyEnabled` 下发 `SOULLINK_USER_POLICY`。
- `ApplyUserPolicy` 写：
  ```
  HKCU\SOFTWARE\Policies\Microsoft\Edge\StartupBoostEnabled = 0
  HKCU\SOFTWARE\Policies\Microsoft\Edge\BackgroundModeEnabled = 0
  ```
- **幂等 + 写失败静默降级**：`ApplyUserPolicy` 绝不抛异常，失败只落一行日志，不阻塞代理注入循环。

### 2.3 修正旧实现（去掉无效写入）

`ApplyStartupPolicy` 原来写 4 个值，现在只保留 class=User 的 2 个 Edge 值，删掉无效的：
- ❌ `Dsh\AllowNewsAndInterests`（Machine）→ 移入 `ApplyMachinePolicy`
- ❌ `Windows\System\EnableMmx`（Machine，且与小组件/搜索无关）→ 删除

`RevertStartupPolicy` 同步只删这 2 个 Edge 值。

### 2.4 配置开关

`AppSettings` 新增两个字段：
- `SessionSlimMachinePolicyEnabled`（bool，默认 false）—— 全机策略开关
- `SessionSlimUserPolicyEnabled`（bool，默认 false）—— Edge 用户级策略开关

### 2.5 UI（ProcessPage 会话瘦身页）

在「启动项优化」账号胶囊下方新增一个面板，两个区块：
- **全机设置**：`MachinePolicyCheck` 勾选 → 二次确认 → `ApplyMachinePolicy(true)`；取消勾选或「恢复默认」→ `ApplyMachinePolicy(false)`。
- **Edge 预启动/后台**：`UserPolicyCheck` 勾选 → 存 `SessionSlimUserPolicyEnabled`，下次代理投送时生效。

---

## 3. 改动清单（file:line）

| 文件 | 位置 | 改动 |
|---|---|---|
| `Models/Models.cs` | `:344` 后 | 新增 `SessionSlimMachinePolicyEnabled` / `SessionSlimUserPolicyEnabled` 两个配置字段 |
| `Services/SessionSlimmer.cs` | `ApplyStartupPolicy` | 去掉 `Dsh\AllowNewsAndInterests`、`Windows\System\EnableMmx` 两个无效写入；文案改为「关闭 Edge 预启动/后台」 |
| `Services/SessionSlimmer.cs` | `RevertStartupPolicy` | 同步只删 2 个 Edge 值 |
| `Services/SessionSlimmer.cs` | `:525` 后 | 新增 `ApplyMachinePolicy(bool)`、`IsMachinePolicyEnabled()`、`ApplyUserPolicy(bool)` |
| `Services/RelayClient.cs` | `CaptureEnv` | 下发 `SOULLINK_USER_POLICY` 环境变量 |
| `Relay/RelayAgent.cs` | `Run` | 启动时读环境变量调 `ApplyStartupUserPolicy()` |
| `Relay/RelayAgent.cs` | `Log` 后 | 新增 `LogQuiet()`（供 Services 层静默记日志）与 `ApplyStartupUserPolicy()` |
| `Views/ProcessPage.xaml` | 策略胶囊后 | 新增「全机设置 + Edge 预启动」面板 |
| `Views/ProcessPage.xaml.cs` | `Activate` | 加 `RefreshPolicySwitches()` |
| `Views/ProcessPage.xaml.cs` | `Policy_Click` 后 | 新增 `MachinePolicyCheck_Changed`、`RevertMachine_Click`、`UserPolicyCheck_Changed`、`RefreshPolicySwitches` |

> 未改 Version、未 commit/push、未动 `dist/`、未动 `tools/`。

---

## 4. 编译结果

`dotnet build SoulLink.csproj -t:Rebuild`：

```
已成功生成。
    142 个警告
    0 个错误
```

（142 条警告全部存量；本次新增代码未引入新警告。首次 `dotnet build` 出现过一个 `FlowModels.cs:143 LogTriggerState` 的 CS0246 错误，那是 `Models/FlowModels.cs` 被**另一成员（automation-auditor）**改动的增量编译缓存残留，`-t:Rebuild` 全量重编后 0 错误，与本任务无关。）

---

## 5. 路径B 触发点建议（机器级策略的执行位置）

路径A（代理写 HKCU）只能覆盖 **class=User** 的 Edge 策略。class=Machine 的策略必须写 HKLM，需要管理员/SYSTEM。建议的执行位置（按优先级）：

1. **首选：Broker 服务（SYSTEM）**。`Services/BrokerService.cs` 已是 SYSTEM 常驻，天然能写 HKLM。给它加一条 `ApplyMachinePolicy` 命令，宿主侧通过 `BrokerProtocol.SendAsync` 下发即可（`RelayClient.DeployAsync` 已有现成的 Broker 请求通道 `:224-236`）。**优点**：不需要 UI 进程持有管理员令牌，跨会话、随时可写。
2. **次选：主程序自身**。主程序 app.manifest 已是 `requireAdministrator`，`App.OnStartup`（`App.xaml.cs:168` 之后）或切到「会话瘦身」页时直接调 `SessionSlimmer.ApplyMachinePolicy(bool)`。**限制**：主程序必须以管理员运行（当前已是），且只在主程序存活时能触发。
3. **兜底：UI 手动按钮**。本次已在 ProcessPage 加了开关，主程序管理员运行时点开关即写 HKLM。

> 本次实现：路径A（代理写 HKCU）+ 路径B 的「主程序自身 + UI 手动按钮」两条都已落地；Broker 服务下发通道建议作为后续增强（需要改动 Broker 协议，超出本任务最小范围，未实施）。

---

## 6. 恢复方法（用户/运维）

### 6.1 全机策略恢复（删值 = 回到系统默认）

```powershell
Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Dsh' -Name 'DisableWidgetsBoard' -ErrorAction SilentlyContinue
Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Dsh' -Name 'AllowNewsAndInterests' -ErrorAction SilentlyContinue
Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Search' -Name 'DisableSearch' -ErrorAction SilentlyContinue
```

> ★ 只删值，不删键。`Dsh` 键删完值后保留空键，`reg delete /f` 会报 `Access is denied`，这是正常现象，不要因此判定「回滚失败」。

### 6.2 用户级 Edge 策略恢复

```powershell
Remove-ItemProperty -Path 'HKCU:\SOFTWARE\Policies\Microsoft\Edge' -Name 'StartupBoostEnabled' -ErrorAction SilentlyContinue
Remove-ItemProperty -Path 'HKCU:\SOFTWARE\Policies\Microsoft\Edge' -Name 'BackgroundModeEnabled' -ErrorAction SilentlyContinue
```

### 6.3 UI 恢复

- 「会话瘦身」页 → 「全机设置」取消勾选 或 点「恢复默认」。
- 「Edge 预启动/后台」取消勾选 → 下次代理投送时自动删值。

---

## 7. 用户怎么验证

### 全机策略（小组件 / 搜索）
1. 会话瘦身页勾选「全机设置 → 启用」，确认二次确认弹窗。
2. 在某个已登录会话（如 A1）里：`Get-Process SearchHost, Widgets | Stop-Process`（或直接在 SoulLink 里看到）。
3. 观察：SearchHost / Widgets **不再自动重启**（perfmeasure 实测写 HKLM 后杀进程即不再回来）。
4. 验证副作用：任务栏搜索框消失、开始菜单打字搜索失效；但开始菜单能开、资源管理器搜索正常。
5. 取消勾选或点「恢复默认」→ 删值 → 进程恢复默认行为。

### 用户级 Edge 策略
1. 会话瘦身页勾选「Edge 预启动/后台 → 启用」。
2. 让对应账号重新连接/重新投送代理（或重启代理）。
3. 检查 `HKCU:\SOFTWARE\Policies\Microsoft\Edge` 下出现 `StartupBoostEnabled=0`、`BackgroundModeEnabled=0`。

### 无法自动化验证的部分（如实说明）
- **未在真实账号上写 HKLM**：perfmeasure 已做受控实验并回滚，本任务只实现代码 + 用一次性账户/ADMX 验证键名与 class，未重复对 A1/A2/A4 写注册表。
- **GUI 交互**：新增的复选框/按钮无法在本会话运行 WPF 点选验证，需用户按上述步骤人工验证。
- **Broker 下发通道**：路径B 的 Broker 增强未实施，见 §5。

---

## 8. 策略清单总表

| 目标 | 键 | 位置 | class | 关闭值 | 恢复方式 |
|---|---|---|---|---|---|
| 小组件面板 | `DisableWidgetsBoard` | HKLM `...\Dsh` | Machine | 1 | 删值 |
| 小组件新闻流 | `AllowNewsAndInterests` | HKLM `...\Dsh` | Machine | 0 | 删值 |
| 任务栏搜索/SearchHost | `DisableSearch` | HKLM `...\Windows Search` | Machine | 1 | 删值 |
| Edge 预启动 | `StartupBoostEnabled` | HKCU `...\Edge` | User | 0 | 删值 |
| Edge 后台 | `BackgroundModeEnabled` | HKCU `...\Edge` | User | 0 | 删值 |
