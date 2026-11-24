# 回归复验：拆出独立 Agent 工程后 P0 再验 — 2026-11-24（t8）

> 验证人：verifier（独立于 t7a 实现者）
> 对象：t7a（只拆 SoulLink.Agent.exe）+ 回归 P0（t1/t2/t3 修复在拆工程后是否仍成立）
> 关键前提：**Broker 拆分 + 服务路径迁移归 t16/T7b（Lead 已另建），本任务不覆盖**。t8 验收清单里「SoulLink.Broker.exe」「服务 ImagePath 迁到 Broker.exe」两项在 t7a 完成后**尚不存在**，如实标注为「未适用（依赖 t16/T7b）」。

---

## 1. build.bat 重跑 + 产物落位 — 通过（Agent 部分）

命令：`cmd /c "echo. | build.bat"`（exit 0，双 TFM 0 error）。

产物核对（dist 与 artifacts\dist-3.3.40）：

| 文件 | dist 字节 | FileVersion / 内容 | 说明 |
|---|---|---|---|
| SoulLink.exe | 4,470,111 | 3.3.40.0 | 主程序（不变） |
| SoulLink.Agent.exe | **2,702,170** | **1.0.0.0** | ★ 拆出的独立无 UI exe（不再是主程序字节副本） |
| SoulLink.Agent.version | 10 B | 1.0.0 | 与 Agent.exe 同目录 |
| SoulLink.Agent.required.version | 10 B | 1.0.0 | 主程序要求的 agent 最低版本 |
| SoulLink.Updater.exe | 74,240 | 3.2.7.0 | 更新器（不变） |

**体积对比（三个数字）**：
- 主程序 SoulLink.exe = **4.47 MB**（不变）
- 注入代理 SoulLink.Agent.exe = **2.70 MB**（拆前是主程序字节副本 4.47 MB，现在显著变小；t7a 预估 ~1.4MB，实际 2.7MB，因单文件打包内嵌了 Vortice 系列 + SharpGen + System.Drawing.Common，仍比主程序少 1.77 MB）
- SoulLink.Updater.exe = 74 KB（参照）

> **SoulLink.Broker.exe：不存在**（全仓 0 命中，无 SoulLink.Broker.csproj）。Broker 拆分归 t16/T7b。当前服务仍以 `SoulLink.exe --broker` 运行（见 §5）。

---

## 2. 解耦硬指标 — 通过（Agent 实测）

**agent 的 deps.json 引用程序集清单**（`Agent\bin\Release\net11.0-windows\win-x64\SoulLink.Agent.deps.json`，因 dist 只放单文件 exe 不带 deps.json，故取 publish 输出的 deps.json）：

```
Microsoft.Win32.SystemEvents/8.0.0
SharpGen.Runtime.COM/2.4.2-beta
SharpGen.Runtime/2.4.2-beta
SoulLink.Agent/1.0.0
System.Drawing.Common/8.0.0
Vortice.Direct3D11/3.6.2
Vortice.DirectX/3.8.3
Vortice.DXGI/3.6.2
Vortice.Mathematics/2.1.0
Vortice.MediaFoundation/3.8.3
```

WPF/WinForms 依赖检查（逐个匹配）：

| 依赖 | 命中 |
|---|---|
| PresentationFramework | **0** |
| PresentationCore | **0** |
| WindowsBase | **0** |
| System.Windows.Forms | **0** |

→ agent 无任何 WPF/WinForms 依赖。**broker 未拆（不存在），无 broker 可验**，该项标注「未适用（依赖 t16/T7b）」。

---

## 3. 版本标记链 — 通过

- `BrokerInstall.VersionFileFor` 按 exe 同目录找标记：探针实测 `VersionFileFor(dist\SoulLink.Agent.exe)` = `dist\SoulLink.Agent.version`，**存在、内容=1.0.0**。
- `ReadAgentVersion(dist agent)` = **1.0.0**（source=version-file）。
- `ReadRequiredAgentVersion(dist main)` = **1.0.0**（读 required.version，**不是 FileVersion 回退**——主程序 FileVersion=3.3.40.0 但 required 读回 1.0.0，证明基线已与主程序版本解耦）。

---

## 4. 判据基线（三条路径实测）

| 场景 | required | agent | ValidAgent | 预期 |
|---|---|---|---|---|
| 双方一致 | 1.0.0 | 1.0.0 | **True** | true |
| agent 低于要求 | 2.0.0 | 1.0.0 | **False** | false |
| 无要求（放行） | null | 1.0.0 | **True** | true |

→ 基线 = 主程序要求的 agent 版本（`SoulLink.Agent.required.version` = `<AgentVersion>` 单一来源），**不是服务版本、不回退 FileVersion**。服务版本达标单独走 `BrokerProtocol.MinimumServiceVersion`，与 agent 判据分离（已读 BrokerInstall.cs:43-76 确认）。

---

## 5. 部署 + 服务路径 — 部分通过 / 未适用

- **部署实测**：Program Files\SoulLinkDev 现为 SoulLink.Agent.exe **2,702,170 字节 / FV=1.0.0.0**、SoulLink.Agent.version=1.0.0、SoulLink.Agent.required.version=1.0.0（均 18:26 mtime），服务 Running（PID 22532）。**ValidAgent==true**（探针实测）。
- **服务路径迁移**：**未适用**。当前 ImagePath = `"C:Program FilesSoulLinkDevSoulLink.exe" --broker`。t8 要求的「就地改配置到 SoulLink.Broker.exe」依赖 Broker 拆分（t16/T7b），Broker.exe 尚不存在，无从迁移。这不是缺陷，是 t7a 收窄范围后的预期现状。

---

## 6. 真实投送实测 — 通过（沿用 session=1 路）

直接走 Broker 命名管道（与 RelayClient 同链路，客户端=当前提权管理员）：

| 请求 | 返回 |
|---|---|
| health | `Ok=true Code=READY AppVersion=3.3.40.0` |
| ensure session=1 user=lummy（Active） | `Ok=true Code=AGENT_STARTED Pid=37840` |
| ensure session=4 user=A2（Disc） | `Ok=false Code=OFFLINE` |

- **被拉起的代理 PID 37840 是新的独立 agent**：path=Program Files\SoulLink.Agent.exe、**size=2,702,170、FV=1.0.0.0**，身份校验 session=1 admin=True elevated=True 通过。broker.log 原始序列 DEPLOY_BEGIN→DEPLOY_RESULT ok=True pid=37840→DEPLOY_IDENTITY→AGENT_STARTED 完整。
- **AGENT_INVALID 计数**：正确部署后（18:26 起）新出现 = **0 次**；历史累计 495 次全部在 17:37 之前。
- **A2 会话**：仍 Disc（未登录），对 session=4 的 ensure 在离线门槛先于 ValidAgent 返回 OFFLINE（非 AGENT_INVALID）。「A2 端到端 UI 投送」**未验证**（环境限制，同 t5 §8.6，补测方法不变）。
- 测试代理 PID 37840 已按精确 PID 回收；A1/A4 的 agent（PID 38324/6904）全程未动。

---

## 7. 结论

- **agent 拆分回归 P0：通过**（构建 0 error / 解耦 0 WPF 依赖 / 版本链不断 / 判据基线正确 / 真实投送成功 0 AGENT_INVALID）。
- **broker 拆分：未适用**（依赖 t16/T7b，Broker.exe 尚未产生；服务仍走 SoulLink.exe --broker）。

## 8. 红线合规

未碰 dist\stable；未使用 A1/A4（其 agent 进程 PID 38324/6904 全程未动）；未改注册表；未发布、未碰 COS/latest.json；未弹窗；临时目录全在 %TEMP%（探针已清）；停进程只按精确 PID（37840）；未 git commit。发现 bug 未自修（见下）。

## 9. 发现的问题（报 Lead）—— 已更正（2026-11-24 18:33 Lead 反馈后修订）

1. **SoulLink.Broker.exe 拆分未落地**（属 t16/T7b，非本任务范围，仅记录）。
2. ~~BrokerSetup.SyncRuntimeFiles 尚未适配独立 agent~~ —— **已由 Lead 修复（18:25），本条作废**。

   **时间序说明**：我初版报告基于 18:19 读到的 `tools/broker-setup/Program.cs`（当时第 61 行仍是「主程序复制改 asInvoker 当 agent」旧逻辑）。Lead 于 **18:25:53** 改了该文件，我读文件早于改动，故误报为「尚未适配」。

   **当前代码（tools/broker-setup/Program.cs:62-78，18:25:53 mtime，已复核）**：
   ```csharp
   string sourceDir = Path.GetDirectoryName(source) ?? "";
   string standaloneAgent = Path.Combine(sourceDir, "SoulLink.Agent.exe");
   if (File.Exists(standaloneAgent))
   {
       CopyFile(standaloneAgent, agent);          // ← 优先用独立 agent（2.7MB 无 WPF）
       Log("AGENT_STANDALONE " + Path.GetFileName(standaloneAgent));
       if (!PatchManifest(agent, null)) Log("AGENT_MANIFEST_UNVERIFIED " + agent);
   }
   else
   {
       // 兼容老发布包：回退到「复制主程序 + 改清单」
       if (!File.Exists(agent) || !PatchManifest(agent, source)) { CopyFile(source, agent); PatchManifest(agent, null); }
       Log("AGENT_FALLBACK_FROM_MAIN");
   }
   ```

   **运行时证据（独立核实）**：
   - 安装日志 `%TEMP%\SoulLink-Broker-install-current.log`（18:26:50）记录 `AGENT_STANDALONE SoulLink.Agent.exe` —— 编译进的确实是新逻辑，走的是「优先独立 agent」分支。
   - `dist\tools\SoulLink.BrokerSetup.exe`（18:26:02 编译）UTF-16LE 字符串含 `AGENT_STANDALONE` / `AGENT_FALLBACK_FROM_MAIN` / `AGENT_MANIFEST_UNVERIFIED` 三标记（ASCII 搜索误报 False 是因 C# 字符串字面量在托管元数据里是 UTF-16 编码，非缺陷）。
   - 部署结果：`C:\Program Files\SoulLinkDev\SoulLink.Agent.exe` = 2,702,170 字节 / 1.0.0.0（18:26:23），确为独立 agent。

---

## 10. A2 真实投送成功（Lead 18:32 补充，独立核实）

Lead 18:32 让用户启动 dist 后，A2 会话恢复 Active，broker.log 记录完整投送序列（我已独立核实）：

```
18:32:31.612  DEPLOY_SESSION session=4 user=A2 => Active
18:32:31.613  DEPLOY_BEGIN   session=4 user=A2 pipe=SoulLink_Relay_4_6b2d626e
18:32:31.647  DEPLOY_RESULT  ok=True pid=4516 已投送到会话 4，PID 4516
18:32:31.649  DEPLOY_IDENTITY pid=4516 user=Lummy\A2 sid=...-1004 session=4 admin=True elevated=True
18:32:31.649  RESPONSE_SENT AGENT_STARTED
```

- 被拉起的 agent PID 4516（session 4 / A2）实测 path=`C:\Program Files\SoulLinkDev\SoulLink.Agent.exe`、**size=2,702,170、FV=1.0.0.0** —— 确认为新的独立 agent。
- **t5 §8.6 那个「A2 在线时是否还会被 AGENT_INVALID 拒」的未覆盖场景，现已真实覆盖**：A2（跨会话 + 特定账号，正是用户实际使用路径）投送成功，AGENT_INVALID 0 次。这比 session=1 的证据更强。
- 注意：A2 的投送是 Lead 在 18:32 通过主程序发起的（非我）；我 18:29 复测时 A2 仍是 Disc 故返回 OFFLINE。时间序：我 18:29 验证（Disc）→ Lead 18:32 投送成功（Active）。
