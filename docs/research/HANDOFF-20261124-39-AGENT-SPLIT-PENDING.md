# 交接文档 · 3.3.39 交接（2026-11-24 下午）

> **状态：3.3.39 已构建，但有 P0 级投送故障未修复，当前不可交付用户。**
> 上一份：[HANDOFF-20261124-LATENCY-OPTIMIZED.md](HANDOFF-20261124-LATENCY-OPTIMIZED.md)（444ms→90ms 那轮）
> Git HEAD：`588c007`（**26 个文件未提交**，见 §7）
> 版本：`SoulLink.csproj` Version=**3.3.39**，AgentVersion=**1.0.0**

---

## 0. 三句话现状

1. **延迟问题已经查清**：233ms 不是浏览器/链路问题，是**静止桌面保活节拍（500ms）的采样等待**；动态场景实测只有 **15~63ms**。
2. **3.3.39 引入了 agent 版本解耦，但踩中单文件发布的坑 → 所有投送报 `AGENT_INVALID`，三个会话全废**（P0，正在修）。
3. **界面状态显示是假的**：XAML 硬编码「引擎就绪」，只有设置页会刷新，投送失败不反馈 → 用户看到全绿但用不了。

---

## 1. P0：当前必须优先修复的问题

### 1.1 投送全线失败（根因已定位）

**症状**：日志每 10 秒刷 `Broker 拒绝了投送：会话 4 · AGENT_INVALID: 固定目录中的管理员代理缺失或版本不符`，A1/A2/A4 全部无法投送。

**根因（Lead 已实证）**：

```
SoulLink.exe       -> PEReader.HasMetadata = False
SoulLink.Agent.exe -> PEReader.HasMetadata = False
```

我们是**单文件发布（PublishSingleFile=true）**，外层是原生宿主，托管元数据被打包在 bundle 内部。

于是 `Services/BrokerInstall.cs` 的 `ReadAgentVersion()`（用 `PEReader` 读 `AssemblyMetadata("AgentVersion")`）对两个文件**都返回 null**：

```csharp
string? sv = ReadAgentVersion(source);  // null
string? av = ReadAgentVersion(agent);   // null
if (string.IsNullOrEmpty(av)) return false;   // ★ 永远命中 → 永远拒绝
```

**⇒ 跟版本号无关，是元数据根本读不到。手工同步版本到 3.3.39 也没用。**

**修复方向**（已派 `task_mwucra47u67l0r`）：
- 改用**随包分发的版本文件**（如 `SoulLink.Agent.version` 文本），单文件/非单文件都可用；
- 或解析 bundle 内嵌程序集（复杂，不推荐）；
- **不可退回 FileVersion 强绑定**（那就等于放弃解耦目标）。

**保留的正确方向性**：
- agent 侧标识缺失 → 判需替换（可重铺）；
- 服务侧标识缺失（老服务）→ 不校验、放行；
- 双方都有 → 语义版本比较，agent 不低于服务要求。

### 1.2 界面状态造假（用户原话：「界面没有提示，全绿，不是废了是什么」）

**四处根因**：

| # | 位置 | 问题 |
|---|---|---|
| 1 | `Views/MainWindow.xaml:126` | `<TextBlock x:Name="EngineText" Text="引擎就绪"/>` —— **设计时硬编码默认值**，程序没改它之前一直显示 |
| 2 | `Views/SettingsPage.xaml.cs:554,570` | 只有设置页触发 `StatusChanged`；`LaunchView` **根本不触发**（但 MainWindow 订阅了它） |
| 3 | `Views/MainWindow.xaml.cs:493` | `EnsureEngineAsync()` **只在启动跑一次**，之后不再检查 |
| 4 | `Views/SettingsPage.xaml.cs:525-560` | 「Broker 服务可用」**只探测 health 命令能否通信**，完全不含 agent 判定；`versionNote` 比的是 Broker **协议版本**不是 agent 版本 |

**已派任务** `task_mwucppattz06rl`，要求：
- XAML 默认值改中性态；
- 启动/投送失败/手动重试都要重新 Inspect 并渲染；
- `Ready` 必须同时满足「服务在跑 + agent 存在 + 版本通过」，任一不满足禁止显示绿；
- **设置页与顶栏 Tooltip 必须显示三方版本**：「当前程序 X · 服务 Y · 注入代理 Z」；
- 状态文案要区分：`引擎就绪 / 引擎待安装 / 引擎未运行 / 注入代理待更新 / 引擎异常`。

### 1.3 不可恢复错误仍在无脑重试（用户原话：「版本不对的情况下还在疯狂尝试注入，有意义吗？」）

`Controls/RdpConsole.cs:796-821`：指数退避最多 5 次（2s/4s/8s/16s/30s），**不区分可恢复与不可恢复**。

**要做的**：
- `AGENT_INVALID` / agent 缺失 / 服务未安装 等 → **停止自动重试循环**，给可执行指引；
- **但必须保留全部恢复入口**（这是重点，不要理解成「关掉就再也不试」）：
  - 点「重新连接」/「修复注入代理」/「重新检测」→ 重新检测并重试；
  - **周期性（30~60s）重新 Inspect**，发现 agent 已修好 → **自动恢复**，不需要重启程序；
  - 恢复时把 `_rebuildAttempt` 归零（`RdpConsole.cs:844` 有现成逻辑，别破坏）；
- 不可恢复错误日志**只记一次**，不要每 10 秒刷屏。

---

## 2. 延迟问题的最终结论（这条已经闭环，不用再查）

### 2.1 233ms 的真正来源

**静止桌面下保活节拍（`KeepaliveIntervalMs = 500`）的采样等待。**

- 实测：A2 静止桌面 `/stream` 只有 **2.10 fps**，帧间隔 p50 = **531.6ms**（`Relay/SessionRecorder.cs:89`）；
- 随机时刻采样一个 531ms 才更新一次的时钟，数学期望等待 = `E[g²]/2E[g] = 261.5ms`（周期模型 233.4ms）→ **与用户实测 233ms 数值直接重合**；
- 用户测量方法已标定：注入 0/50/120/300ms → 读出 0/52/122/300ms（误差 ≤2ms，线性吻合），**233ms 不是噪声**。

### 2.2 动态场景实测（用户用 ms-clock 同框截图，5 组）

| 场景 | 延迟 |
|---|---|
| 动态桌面 | **15~63ms**（中位数约 46ms） |
| 静止桌面 | **233ms** |

**233ms 里各段的实际占比**：

| 项 | 贡献 |
|---|---|
| 静止保活节拍（帧龄） | **~200~233ms** ← 主项 |
| 服务端管线（capture+convert+encode） | ~13ms（p50） |
| 传输层 | 2~20ms（典型 ~5ms） |
| 浏览器段 | **1~5ms**（最坏 25ms）← 曾被冤枉 |
| 输入/鼠标链路 | 0ms（已排除） |

**结论：浏览器无责，换 web 框架/重写前端没有意义。**

### 2.3 已被数据否掉的方向（不要再提）

| 方向 | 否掉理由 |
|---|---|
| 换 UDP / WebTransport | 局域网 RTT 1.14~1.7ms，净收益 ≈0 甚至为负 |
| 脏区增量编码 | 实测脏矩形占比 50.7%、编码仅 4.9ms → **上限 ~2.5ms** |
| AVC444 低色度 | Yomori 三份计划书 + 一次撤回，只换 −5.6% CPU，与延迟无关 |
| 换 FreeRDP 自建 RDP 客户端 | 与 TermWrap 多会话冲突 + 对方自估 9~13 AI 会话 + **不解决帧供给痛点** |
| 迁移 WinUI 3 | 瓶颈不在 UI，且会毁掉单文件 4.4MB 的优势 |
| viewer 队列 2→1 | 快读端省 0ms，慢读端仅省 1 帧间隔，且削弱关键帧保护 |

---

## 3. 本轮的 Yomori 对标结论

仓库：<https://github.com/AITNR/Yomori>（GPL-3.0，C#/WinUI 3，52MB）

**我们比它强的地方**：一份画面多端分发、浏览器段实测 1~5ms、单文件 3.3MB（它 73MB 多文件）、跨会话投送与自动化业务面。

**它比我们强的地方**：
- agent 版本与主程序解耦（`AgentVersion` 独立元数据，发版不重铺）；
- 双槽热替换（更新不停机）；
- 独立进程看门狗（进程内看门狗在 68 线程全卡时调度饿死，**三次漏报实证**）；
- 4 条 GitHub Actions + 8 个独立测试项目（我们 0 CI / 0 测试）。

**⚠️ 许可证红线：GPL-3.0，只能借鉴机制，一行代码都不能抄。**

详见 `BENCH-YOMORI-01/02/03`。

---

## 4. 架构现状与待办：agent / 服务还没拆

### 4.1 事实：**agent 目前不是独立 exe，只是主程序的副本**

```
SoulLink.exe        4,453,727  SHA=5D69C86D...
SoulLink.Agent.exe  4,453,727  SHA=5D69C86D...   ← 完全相同
```

`build.bat:94` 就是一句 `copy /y "!STAGE!\SoulLink.exe" "!STAGE!\SoulLink.Agent.exe"`。

**⇒ 三个会话的 agent + 一个 Broker 服务，每个都背着完整的 WPF UI 运行。**

### 4.2 实测内存（这就是拆分的动力）

| 进程 | 工作集 |
|---|---|
| Broker 服务（session 0） | **61 MB** |
| agent × 2（session 2/3） | **47 MB × 2** |

拆成独立 exe 后预估：服务 ~15MB、agent ~20MB，**合计可省 80~125MB**。

### 4.3 拆分方案（已勘察过两轮，依赖清单是准的）

**agent 依赖闭包**（`REFACTOR-20261124-AGENT-BROKER-SPLIT.md`）：
- `Relay/` 下 10 个文件（约 294KB）
- **12 个** Services 类：`AsyncLog / SessionSlimmer / RelayHotkeyReceiver / HotkeySpec / InputTrace / NvencSdkEncoder + IFrameH264Encoder / MfFrameEncoder / CodecApi / GpuColorConverter / Mp4Writer / AppIdentity`
  - ⚠️ 注意：不是 5 个，**编码器那批（NVENC/MF/CodecApi）必须带上**
- 入口：`App.xaml.cs:145-170` 的 `--relay` 分支 → `RelayAgent.Run()` → `Shutdown(0)`

**Broker 依赖**：`BrokerService.cs / BrokerProtocol.cs / BrokerInstall.cs / BrokerArguments.cs` + `SystemLauncher` 的跨会话投送段。
- 入口：`App.xaml.cs:93-96` 的 `--broker` 分支

**关键实现要求**：
- 用 `<Compile Include="..\Relay\*.cs" Link="..."/>` **引用源文件，不要复制文件**（避免双份维护）；
- 去掉 `UseWPF/UseWindowsForms`；
- 保留 `--relay <pipe> --require-elevated --deploy-nonce <nonce>` 参数契约（Broker 投送依赖）；
- 产物名必须是 `SoulLink.Agent.exe`（`AppIdentity.IsAgentExecutable` 按文件名切身份）；
- csproj 里用 **Target** 复制产物，不能用 None/Content 项（否则会被合并进单文件）。

### 4.4 服务拆分的额外风险

服务注册路径会变（`SoulLink.exe --broker` → `SoulLink.Broker.exe`），老用户升级会失效。

**但兼容不难**——现有安装器有个 bug 可以顺手修：

`tools/broker-setup/Program.cs:240-254`：
```csharp
IntPtr service = OpenService(scm, ServiceName, ServiceChange | ServiceAll);
if (service == IntPtr.Zero) service = CreateService(...);
if (service == IntPtr.Zero) throw ...;
CloseServiceHandle(service);   // ← ServiceChange 权限申请了却从没用，服务已存在就什么都不改
```

补一个 `ChangeServiceConfig(service, ..., command, ...)` 即可让已存在的服务改正路径。

**建议顺序**：先拆 agent（无服务注册风险，省 80MB+），服务后拆。

---

## 5. 本轮已完成并验证的改动

| 版本 | 改动 | 状态 |
|---|---|---|
| 3.3.36 | 服务器地址外部化，源码零域名（`Services/ServerEndpoints.cs`） | ✅ 已推送 |
| 3.3.37 | 保活节拍 500→250ms 可调 + 输入触发出帧 | ✅ 已构建 |
| 3.3.38 | 拖动 up 定位 + `shot` 后台化 | ✅ 已构建 |
| 3.3.39 | AgentVersion 解耦 + AgentSync 自检 + 引擎 5 态 + 键盘 toggle 修复 | ⚠️ **构建通过但投送故障** |

**键盘 toggle 修复（本轮质量最高的一项）**：
- 真因不是 toggle 没同步，而是**注入没带扫描码与扩展位**（`wScan=0`、无 `KEYEVENTF_EXTENDEDKEY`），导致 `VK_LEFT` 与 `VK_NUMPAD4` 在消息层 lParam **完全相同**；
- 修法：`SendVk` 按 `MAPVK_VK_TO_VSC_EX` 取真实扫描码 + 叠加自维护扩展表（**实测方向键 VSC_EX hi-byte 不是 0xE0，纯 MAPVK 会漏判**）；
- 新增 `keystate`/`keysync` 动作（先读远端三锁再 Diff，只在目标≠当前时才按，按完回读校验）；
- ⚠️ **踩坑提醒**：Web 动作白名单（`AgentWebServer.cs:629`）与 `RelayAgent.Execute` 是**两份独立 switch**，加任何输入动作必须同时改两处。

---

## 6. 已立项但冻结的任务（等 P0 修好后可放行）

来自 Yomori 对标，共 13 项，其中 10 项已 `aborted` 冻结：

| 项 | 工作量 | 价值 |
|---|---|---|
| 编译红线（弱化版）：agent 引入 WPF 类型时告警 | 0.5 天 | 防高危代码进 agent |
| 双槽 agent 热替换（依赖版本解耦） | 2 天 | 更新不停机 |
| 外置进程看门狗 + 假死 MiniDump | 1~2 人日 | **已实现**（`tools/watchdog/`） |
| 更新残留清理（pending-cleanup.json） | 0.5 人日 | 不再无限占盘 |
| 更新检查 6 态（补 LocalNewer 防回退） | 0.5 人日 | 防降级误升级 |
| 最小 CI + 构建归档 tag | 1~1.5 人日 | 可回溯 |
| 起步测试项目（xUnit） | 1~2 人日 | 防回归 |
| broker 投送「活跃任务不覆盖」守卫 | 0.5~1 人日 | 升级不打断挂机 |
| 前端「等关键帧」兜底（≈15 行） | 0.25 人日 | 修最坏白等 10s |
| 按会话暂停编码 / 遮挡闸门接呈现 | 1~1.75 人日 | 省 CPU |
| 诊断按 reason 分桶 | 0.25 人日 | 可观测性 |
| ⭐ **重新审视帧节拍模型**（变化驱动 vs 固定节拍） | 待评估 | **唯一可能真降延迟** |

未冻结且已完成的：`tools/watchdog/`（外置看门狗）、`scripts/agent-dep-redline.ps1`（编译红线），详见 `IMPL-20261124-WATCHDOG.md` / `IMPL-20261124-AGENT-DEP-REDLINE.md`。

---

## 7. 工作区状态（注意：26 个文件未提交）

```
Git HEAD: 588c007
未提交: 26 个文件（含 Relay/、Services/、Views/、build.bat、SoulLink.csproj）
版本: 3.3.39 / AgentVersion 1.0.0
```

**运行中进程**：
```
8344   session 0  C:\Program Files\SoulLinkDev\SoulLink.exe      (Broker 服务)
58080  session 1  E:\...\dist\SoulLink.exe                     (主程序)
6904   session 3  C:\Program Files\SoulLinkDev\SoulLink.Agent.exe
38324  session 2  C:\Program Files\SoulLinkDev\SoulLink.Agent.exe
```

**服务**：`SoulLink.DevBroker` = Running，路径 `C:\Program Files\SoulLinkDev\SoulLink.exe --broker`

**产物**：`dist\` 与 `artifacts\dist-3.3.39\` 均为 3.3.39（主程序与 agent 版本一致，但因 §1.1 的 bug 实际不可用）

---

## 8. 铁律（沿用）

1. **绝不碰 `dist\stable\SoulLink-Stable.exe`（PID 13392，挂机版）**
2. **绝不使用 A1（session 2）、A4（session 3）账号**
3. **只用腾讯代理 `buddy` 下的模型，禁止 `deepseek-account` 等付费 provider**
4. 停进程只按**精确 PID**，禁止按名字批量杀
5. **绝不弹窗**（用 `-WindowStyle Hidden`）
6. 不改注册表（除已授权项）
7. 不发布、不碰 COS、不改 `latest.json`
8. 版本号唯一来源是 `SoulLink.csproj` 的 `<Version>`，改动必须升版本
9. **构建产物 `bin/obj` 用完即删**；临时文件只放 `%TEMP%`
10. **临时实验目录绝不能建在项目根下**（本轮踩过：`undefined\temp\agentver-probe\probe.cs` 被通配符编译进去，报 CS0017 多个入口点）

---

## 9. 给接手者的建议

**第一优先**：修 §1.1 的投送故障。**在此之前 3.3.39 不可交付任何用户。**

**第二优先**：修 §1.2 的界面状态造假。用户已经明确表达不满：「界面没有提示，全绿，不是废了是什么」。

**第三优先**：按 §4 拆 agent（省 80MB+ 内存，且不涉及服务注册风险）。

**不要做的事**：
- 不要再纠缠 233ms（已闭环，静止=234ms/动态=15~63ms，真凶是保活节拍）；
- 不要换 UI 框架、不要换 RDP 客户端、不要上 UDP、不要做脏区增量；
- 不要再让多个成员并行改同一批文件（本轮因并发编辑导致多次构建互相打断）。

**流程教训**：
- 派实现类任务时，**必须明确写「本任务允许修改产品代码」**，否则成员会按只读红线上报 blocked；
- **成员报告 `dotnet build 0 error` 不可信**，必须要求贴构建输出末尾 5 行，Lead 自己复跑一次；
- 多人并行改代码时，**构建是共享资源**，串行化更重要。

---

**文档作者**：Lead（团队 soullink-latency-233ms）
**最后更新**：2026-11-24 15:15
**Git HEAD**：`588c007` + 26 个未提交改动
**版本**：3.3.39

---

## ⚠️ 附加：本交接文档写完后发生的变更（以本节为准）

### A. 所有后台任务已全部停止

用户明确要求停止一切后台工作并换人接手。以下任务已 `abort`：

| 任务 | 内容 | 状态 |
|---|---|---|
| `task_mwucra47u67l0r` | 修根因：单文件下读不到 AgentVersion | **aborted（未完成）** |
| `task_mwucppattz06rl` | 修界面状态造假 + 版本显示 | **aborted（未完成）** |
| `task_mwucnpi0tt6ixz` | 统一 AgentSync 与 ValidAgent 判据 | aborted（与上者重复，已作废） |

**⇒ §1.1（投送全线失败）与 §1.2（界面状态造假）两个 P0 问题仍未修复。**

### B. 停止时工作区处于"半成品"状态，已由 Lead 修复到可编译

两个成员在任务被中止时**留下了未完成编辑**，导致 `dotnet build` 报 `CS0103: 不存在名称 OnEngineRecheck`（订阅写了、方法没写）。

**Lead 已补的最小修复**（`Views/MainWindow.xaml.cs`）：
- 新增 `OnEngineRecheck(string reason)` 方法：回到 UI 线程 `AgentSync.Inspect` + `RenderEngine`，异常记 `LogBus.Warn`；
- **注意**：这只是让编译通过的最小补丁，**不等于完成了原任务**（原任务还要求 XAML 默认值改中性态、投送失败反馈、三方版本显示等）。

**当前状态：`dotnet build -c Release` = 0 error**（已验证）。

### C. 版本与工作区

```
Version       = 3.3.39
AgentVersion  = 1.0.0
Git HEAD      = 588c007
未提交文件     = 30 个（含 Controls/、Relay/、Services/、Views/、build.bat、SoulLink.csproj）
构建状态       = dotnet build 0 error（build.bat 未复跑）
```

### D. 交付可用性判断

| 项 | 结论 |
|---|---|
| `dist\SoulLink.exe` / `dist\SoulLink.Agent.exe` | 3.3.39，**但投送仍会全线失败**（§1.1 未修） |
| 能否给用户用 | **不能**。必须先修 §1.1 |
| 编译是否通过 | ✅ 通过 |
| 服务现状 | `SoulLink.DevBroker` Running，Program Files 下两个 exe 均为 3.3.39 |

### E. 接手第一件事（建议）

1. 读 §1.1 的根因（单文件 `PEReader.HasMetadata=False`）；
2. 用**随包 version 文件**方案替换 `ReadAgentVersion`，或按 §4.3 拆出真正的独立 agent；
3. 修完**必须实测 A2 投送成功**再交付；
4. 再做 §1.2 界面状态。

### F. 给接手者的团队协作提醒

本次 Lead 在流程上犯的错误，供规避：

| 错误 | 后果 |
|---|---|
| 派实现类任务时未明确解除"只读"红线 | 成员两次 `blocked`，浪费两轮 |
| 同一问题重复派发（版本判据派了两次） | 两个任务改同一文件，被迫作废一个 |
| 一次性派超大任务（拆项目 + 改构建 + 改服务） | 成员 30 分钟零落盘，卡死 |
| 轻信成员"dotnet build 0 error" | 实际有 6 个编译错误 |
| 并发派多个成员改同一批文件 | 构建互相打断，反复失败 |
| 任务中止前未检查工作区状态 | 留下半成品代码，构建断裂 |

**建议**：实现类任务一次只派一个，串行推进；每个任务的验收必须**贴构建输出** + **Lead 亲自复跑**。
