# 重构（分两阶段）：拆出 SoulLink.Agent.exe / SoulLink.Broker.exe —— S1 依赖边界勘察（只读，未施工）

> 日期：2026-11-24
> 作者：rdp-session-analyst（dsh-team team_mwu35l8pae8oj5 / member m_mwu3790mafbish / task task_mwubb27ho4kgjy）
> 状态：**仅完成 S1 只读勘察，未做任何写入/构建/服务安装。**
> 原因：本任务要求改动产品代码（新建 csproj）、改 build.bat/publish-release.ps1、升版本号 3.3.39、安装 Windows 服务——与本成员红线「只读审计，不得改任何产品代码/脚本/配置/版本号」及 TEAM-REDLINES §四/§五 冲突。
> 证据分级：【代码事实】= 源码 file:line；【推断】= 推导。

---

## 0. 结论（给 Lead）

这是一个**实现类任务，不是只读审计**。本成员（rdp-session-analyst）按红线不能改产品代码/脚本/版本号，应转给有写权限的实现成员执行。本报告留下 S1 的只读依赖勘察结果（已完成），供实现成员直接使用。

---

## 1. S1 依赖边界勘察（只读，已完成）

### 1.1 当前「agent 只是主程序副本」的事实

- 主程序入口 `App.xaml.cs:146-166`：命令行首参 `--relay` → `Relay.RelayAgent.Run(e.Args)`；agent 契约参数 `--relay <管道名>` + `--require-elevated`（`RelayAgent.cs:580-590`）+ `--deploy-nonce`（主程序投送用）。
- 身份判定 `Services/AppIdentity.cs:175-189` `IsAgentExecutable`：按**文件名**（`SoulLink.Agent`）判身份，身份仍归 `SoulLink`（`:207-218`，保证 relay 管道前缀 `Name_Relay_` 对齐宿主）。
- `build.bat:94-95`：目前 `SoulLink.Agent.exe` 是 `copy SoulLink.exe` 出来的副本（4.4MB，含 WPF），注释 `:92-93` 明说「后续会拆成真正的独立 exe」。
- 主 csproj 版本当前 = **3.3.38**（`SoulLink.csproj:1` `<Version>3.3.38</Version>`）。

### 1.2 SoulLink.Agent.exe 的最小依赖闭包（Relay + Services）

入口：`Relay/RelayAgent.Run`（`RelayAgent.cs:581`）。需要 `Relay/*.cs`（10 个文件）+ 以下 Services 类（**闭包 ≥12 个文件，不是简报写的 5 个**）：

| 类 | 文件 | 被谁引用 | 证据 |
|---|---|---|---|
| AppIdentity | Services/AppIdentity.cs | RelayAgent（using 短名） | 管道前缀/身份派生 |
| AsyncLog | Services/AsyncLog.cs | RelayAgent | RelayAgent.cs:451 SoulLink.Services.AsyncLog.Append |
| SessionSlimmer | Services/SessionSlimmer.cs | RelayAgent | RelayAgent.cs:473 SoulLink.Services.SessionSlimmer.ApplyUserPolicy |
| RelayHotkeyReceiver | Services/RelayHotkeyReceiver.cs | RelayAgent | RelayAgent.cs:394,620 |
| HotkeySpec | Services/HotkeySpec.cs | RelayHotkeyReceiver | RelayHotkeyReceiver.cs:20,101 |
| InputTrace | Services/InputTrace.cs | RelayAgent | RelayAgent.cs:660 等 InputTrace.Write |
| NvencSdkEncoder (+IFrameH264Encoder) | Services/NvencSdkEncoder.cs | SessionRecorder | SessionRecorder.cs:565,783,926,990 |
| MfFrameEncoder | Services/MfFrameEncoder.cs | SessionRecorder | SessionRecorder.cs:575 |
| CodecApi | Services/CodecApi.cs | MfFrameEncoder | MfFrameEncoder.cs:177-183 |
| GpuColorConverter | Services/GpuColorConverter.cs | SessionRecorder | SessionRecorder.cs:855 |
| Mp4Writer | Services/Mp4Writer.cs | SessionRecorder | SessionRecorder.cs:1338 |
| （SessionRecorder 依赖的抓屏后端已在 Relay/ 内） | DxgiDdaCapture/WgcCapture/BitBltCapture/GpuShaderConverter/GpuAdapter | SessionRecorder | 同命名空间 |

> 注意：`NvencSdkEncoder.cs` 同时定义 `internal interface IFrameH264Encoder`（NvencSdkEncoder.cs:14），是编码抽象所在文件。简报说「5 个 Services 类」低估了实际闭包。

### 1.3 SoulLink.Broker.exe 的入口与闭包

- 服务入口 `Services/BrokerService.cs`（`public static class BrokerService`，BrokerService.cs:16），当前由 `App.xaml.cs:95` `BrokerService.RunAsync` 在 UI 进程内启动。
- 安装器 `Services/BrokerInstall.cs`（BrokerInstall.cs:9）；投送链 `Services/SystemLauncher.cs` + `WtsSessionQuery.cs`（WTSQueryUserToken/CreateProcessAsUser）。
- 闭包含 BrokerService/BrokerInstall/SystemLauncher/WtsSessionQuery/BrokerProtocol/BrokerArguments/Ipc/DeviceIdentity 等，**与主程序 UI 强耦合**（ConsolePage/SettingsPage 通过 `BrokerProtocol.ServiceUpdated` 订阅），拆分成本远高于 Agent。

### 1.4 拆分的关键风险点（供实现成员参考）【推断】

1. `AppIdentity.IsAgentExecutable` 按文件名判身份：独立 SoulLink.Agent.exe 若去掉 WPF，必须保留 `AppIdentity` + 身份归 `SoulLink` 的逻辑，否则 relay 管道前缀对不上宿主（`AppIdentity.cs:207-218`）。
2. WGC 需要版本化 TFM `net11.0-windows10.0.26100.0`（主 csproj 双 TFM；build.bat:66 显式 -f 发布）。Agent 若含 WgcCapture，TFM 不能只留 net11.0-windows。
3. `Mp4Writer`/`GpuColorConverter`/`CodecApi` 是 SessionRecorder 的硬依赖，Agent 去 WPF 后仍需带入，去 WPF 省的体积来自「不再编译 Views/Controls/XAML」，不是「去掉这些 Services」。
4. Broker 拆成独立 exe 且装成 Windows 服务，涉及服务安装/路径迁移/回滚，风险最高，必须与用户确认服务路径与开机自启策略后再动手。

---

## 2. 为什么本成员停在此处（未施工）

- 任务要求：新建 SoulLink.Agent.csproj / SoulLink.Broker.csproj、改 build.bat 与 publish-release.ps1、升版本 3.3.39、安装并验证服务投送 —— 全部是**产品代码/脚本/版本号写入 + 系统级服务操作**。
- 本成员红线：只读审计，不得改任何产品代码/脚本/配置/版本号；TEAM-REDLINES §五「版本号由 Lead 统一升」、§四「各成员只改自己分配的文件，跨文件改动必须先 dsh_team_send 协调」。
- 因此本成员**不能执行** S2~S6，只能留下 S1 勘察结果。

---

## 3. 转交建议

1. 由 Lead 将本任务转给**有写权限的实现成员**（或 Lead 授权本成员临时扩大为 workspace-write + 明确豁免版本号红线，但需用户/Lead 明确批准，本成员不擅自扩权）。
2. 实现顺序建议：先 Agent（低风险、build.bat:92-93 已有待办锚点），后 Broker（高风险，需先定服务路径与回滚方案）。
3. 本报告 S1 闭包表可直接作为 Agent csproj 的 Compile Include 清单。
