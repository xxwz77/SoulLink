# 修复：注入代理版本自检 + 自动替换 + 引擎 5 态如实显示

> 日期：2026-11-24　·　版本：3.3.39　·　作者：server-url-implementer（team_mwu35l8pae8oj5 / task_mwub7acco3atcg）

## 一、问题（用户痛点）

主程序升级后，安装目录 `C:\Program Files\SoulLinkDev\SoulLink.Agent.exe` 仍是旧版（解压覆盖时 agent 进程占用文件、覆盖失败）。Broker 投送前 `BrokerInstall.ValidAgent` 校验版本不符 → 投送全部失败，但界面仍显示「引擎就绪」。普通用户无法理解「服务可用却投送用不了」。

## 二、改动清单

| 文件 | 改动 |
|---|---|
| `Services/AgentSync.cs` | **新增**。`EngineState` 5 态枚举 + `AgentSync.Inspect`（纯读取自检）+ `RepairAsync`（自动替换）+ `StopAgentsLocking`（按精确 PID 停占用进程） |
| `Views/MainWindow.xaml.cs` | `OnEngineStatus` 改为 5 态渲染（仅 Broker 通道做 agent 自检）；新增 `RenderEngine` / `EnsureEngineAsync`（启动后台自检 + 管理员时自动修复） |
| `Views/SettingsPage.xaml` | 投送引擎区新增「修复注入代理」按钮（兜底入口） |
| `Views/SettingsPage.xaml.cs` | 新增 `RepairAgent_Click` 处理 |
| `SoulLink.csproj` | 版本 3.3.38 → 3.3.39 |

## 三、自检状态机（5 态 + 动作）

| 状态 | 判定 | 界面文案 | 颜色 | 自动动作 |
|---|---|---|---|---|
| Ready | 服务 Running 且 agent FileVersion == 主程序 | 引擎就绪 | 绿 | 无 |
| NotInstalled | 无 `SoulLink.DevBroker` 服务 | 引擎待安装 | 黄 | 提示首次安装（不自动装） |
| NotRunning | 服务存在但非 Running | 引擎未运行 | 黄 | 提示启动 |
| AgentNeedsUpdate | 服务 Running 但 agent 缺失/版本不一致 | 注入代理待更新 | 橙 | 管理员时**自动替换**；普通权限引导提权 |
| Error | 修复失败 / 通道不可用 | 引擎异常 | 红 | Tooltip 给原因 |

## 四、自动修复调用链（file:line）

1. `MainWindow.MainWindow_Loaded`（:491）后台启动 `EnsureEngineAsync`
2. `EnsureEngineAsync`（:745）→ `AgentSync.Inspect`（:750）判定 5 态
3. 若 `AgentNeedsUpdate` 且 `SystemLauncher.IsElevated` → `AgentSync.RepairAsync`（:757）
4. `RepairCore`（AgentSync.cs）：
   - 版本一致 → 直接返回（零写入，见下）
   - 源（主程序同目录 `SoulLink.Agent.exe`）不存在 → 明确报错
   - 非管理员 → 明确报错（引导提权）
   - `File.Copy` 重试，失败则 `StopAgentsLocking` 按精确 PID 停占用进程（只停路径完全一致的进程，不误伤稳定版）
   - 复制后复校验 `VersionOf(target) == SelfVersion`，不等即失败

## 五、『版本一致时零动作』证明

`AgentSync.RepairCore` 首行：`if (IsAgentCurrent) return (true, "…无需修复。");`
`IsAgentCurrent => File.Exists(TargetAgent) && VersionEquals(SelfVersion, VersionOf(TargetAgent))` —— 版本一致时**不执行任何 File.Copy / 不创建目录 / 不枚举进程 / 不弹提权**，直接返回。

## 六、agent 被占用的处理策略

1. 先直接 `File.Copy`（agent 未运行时一次成功，不先停服务）
2. 失败（IOException/UnauthorizedAccessException）→ `StopAgentsLocking` 枚举 `SoulLink.Agent` 进程，`MainModule.FileName` 与 target 全路径一致才 `Kill` + `WaitForExit(3000)`，每次失败后 `Sleep(600)` 重试，最多 5 次
3. 仍失败 → 明确报错建议关闭占用进程
4. **不停 Broker 服务**（用户要求：服务只是启动 agent 的，agent 退出后文件即解锁）

## 七、普通权限 / 拒绝 UAC 文案

- 非管理员时 `RepairCore` 返回：`需要管理员权限才能更新注入代理。请以管理员身份运行本程序后再试。`
- 界面显示「注入代理待更新」（橙），Tooltip 附：`主程序 3.3.39.0 / 注入代理 3.3.38.0` + 修复失败原因
- 设置页「修复注入代理」按钮兜底，可手动触发

## 八、用户升级流程

1. **首次使用**：设置 → 投送方式 → 选 Broker → 「安装 / 管理服务」（安装器复制主程序 + agent 到 C 盘）
2. **后续升级**：解压新包覆盖到任意目录 → 双击 SoulLink.exe → 启动后台自检 → 发现 agent 版本不一致 → 管理员权限下**自动复制新 agent 到 C 盘** → 「引擎就绪」→ 可用。**全程不重装服务**
3. **覆盖时 agent 被占用**：自检发现不一致 → 自动按精确 PID 停旧 agent → 复制新 agent → 复校验 → 就绪
4. **agent 版本不匹配时用户看到**：橙点「注入代理待更新」，Tooltip 显示双方版本号；修复成功转绿「引擎就绪」，失败转红「引擎异常」+ 原因

## 九、构建自证

`dotnet build -c Release --nologo` → **0 错误**（历史警告与本改动无关）。

## 十、依赖说明（与拆分任务的关系）

本任务替换来源是「主程序同目录的 `SoulLink.Agent.exe`」（`AgentSync.SourceAgent`），即**独立 agent exe 拆分产物**。若拆分任务（task_mwu9txm3nv7l5p）尚未落地、发布目录没有独立 agent，`SourceAgent` 找不到文件时会明确报错「发布目录缺少注入代理」，不会静默降级为「用主程序副本兜底」。
