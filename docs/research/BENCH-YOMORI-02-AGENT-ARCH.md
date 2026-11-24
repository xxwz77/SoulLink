# 对标研究 Yomori 之二：Agent 架构与自动部署（独立 exe、版本校验、看门狗）

> 日期：2026-11-24
> 作者：transport-network-analyst（dsh-team `team_mwu35l8pae8oj5` / member `m_mwu37cxvahbb82` / task `task_mwubm3jho8fuo5`）
> 对象：[AITNR/Yomori](https://github.com/AITNR/Yomori)（public，GPL-3.0，19 stars，~52MB，main 分支）
> 方法：GitHub API / blob API 只读抓取源码（raw.githubusercontent.com 在本环境被网络层阻断，改 api.github.com + curl.exe 转存 %TEMP% 读取大文件）。**未 clone、未把任何 GPL 代码复制进本仓库**——所有引用均为「机制描述 + 路径指认」，结论用自己的话写。
> 性质：只读研究。未改任何产品代码；唯一写入 = 本文档。

---

## 0. 一句话总评

**Yomori 的 Agent 架构比我们成熟一代：它把「会话代理是每账户常驻的独立交付物」这件事做成了正式工程——独立精简 exe（73MB vs 228MB 全量）、独立版本号（与主程序解耦）、双槽热替换（agent/agent_b）、独立进程看门狗（三次进程内漏报后的教训）。SoulLink 现状是「主 exe 复制改名 + 主程序版本当判据 + 单目录 + 无独立看门狗」——五条差距里三条值得抄（版本解耦/双槽/独立看门狗），两条不必抄（精简工程拆分成本高、安装期自动部署我们场景不同）。**

---

## 1. Yomori Agent 架构全景（每条带对方仓库路径）

### 1.1 独立精简 exe：228MB → 73MB

- **历史**：代理原本是主控端同一个 exe 靠 `--rdp-agent` 参数分流，部署时整目录复制到 `%ProgramData%\Yomori\agent`，每目标账户白占 228MB（WinUI 界面栈 36MB + WindowsAppSDK 48MB + ONNX/DirectML 39MB + 安装器残留 4.4MB ≈128MB 代理一次都用不到）。证据：`winui3/src/Yomori.Agent/Program.cs` 头部注释。
- **现状**：`Yomori.Agent` 独立工程（`winui3/src/Yomori.Agent/Yomori.Agent.csproj`）：`net8.0-windows` **不带平台版本**（避免自动引入 23.7MB 的 Microsoft.Windows.SDK.NET 投影——代理不碰任何 WinRT 类型）；WinExe 不闪控制台；不产 pdb（`DebugType=none`，常驻分发进程不需要符号）。
- **源码共享不复制**：代理工程以「链接 + 排除」复用 Yomori.App 的 Models/Services/Helpers——**排除清单本身是安全设计**：界面文件（NotificationService/RdpD3DRenderer/DialogHelper 等）与「改注册表/换 ServiceDll/下载运行 exe」的高危服务（RdpUnlockService/RdpRegistryService/RdpToolDownloader 等 11 个）全部排除在代理编译范围外；将来谁误引用，代理工程直接 CS0246 编译失败——「编译期红线」代替「运行期自律」。证据：csproj 的 `<Compile Include ... Exclude=...>` 与注释。
- **manifest 天生 asInvoker**：独立工程不再需要部署期用 mt.exe 改写副本清单（全量复制时代的脆弱步骤）。证据：Program.cs 注释「不再需要部署时用 mt.exe 去改写副本清单」。
- **体积**：注释明确「73MB 代理副本」（AgentAutoDeploy.cs / csproj 注释多处）。

### 1.2 独立版本号（与主程序解耦）—— AgentVersionInfo

- **单一来源**：`Yomori.Agent.csproj` 的 `<AgentVersion>2.1.5</AgentVersion>`，构建时写进**程序集元数据** `AssemblyMetadata("AgentVersion")` + 文件版本资源两处；代码里**没有**常量（避免「元数据 2.0.0、代码 2.0.1」无法对账）。证据：`winui3/src/Yomori.Agent/Yomori.Agent.csproj` 与 `winui3/src/Yomori.App/Models/AgentVersionInfo.cs`。
- **为什么与主程序版本解耦**（csproj 注释原文大意）：主程序每次发版号都变，而代理载荷大部分时候一个字节没动——用主程序版本判断「要不要替换代理」会把每次安装变成 73MB 的无谓搬运+把正在跑的代理顶掉。
- **版本读不出的语义**：`AgentVersionInfo.Same()` 任一侧读不出就判「不同」（宁可多替换一次，不静默保留来路不明的旧代理）；`IsAtLeast()` 读不出返回 false（保守拒绝，防旧代理把新字段误判成配置错误）。证据：AgentVersionInfo.cs。
- **附带身份判定**：主控端程序集没有 AgentVersion 元数据 → 读出空串 → 「我是不是代理进程」不需要开关/环境变量。证据：AgentVersionInfo.cs 注释。
- **忘了 +1 的后果**：csproj 注释明确警告「升级安装里代理不更新，程序侧看不出任何异常」——冒烟测试有一条断言盯着这个数字存在。

### 1.3 双槽热替换（agent / agent_b）—— 解决「常驻进程锁文件」

- **问题**（RdpSessionService.cs:860-872 注释）：代理是常驻进程，持有副本目录里的 DLL 句柄 → `File.Copy(overwrite)` 抛「文件被占用」；**实测连整个目录改名都做不到**（目录内有文件被持有句柄时 MoveFile 返回 Access denied，四种共享模式全试过）——「先挪开旧目录再重建」是死路。
- **两步方案**（`EnsureAgentPayload`，RdpSessionService.cs:878-1017）：
  1. 先把旧代理送走（仅当它空闲，`StopAgentProcess` 跨会话按状态文件判活）→ 原地覆盖（路径不变最干净）；
  2. 送不走（跨会话杀不掉/正在跑任务）→ **改用备用目录 `agent_b`**：旧代理继续用它那份，新副本落新路径，快捷方式改指新路径。用户提示「首选副本目录被运行中的代理占用，已改用备用目录」（:1073）。
- **形态迁移**：旧全量副本 → 新精简载荷时**必须整目录重建**（只覆盖降不下占用：150MB 界面栈不在新清单里永远躺着）——`NeedsShapeRebuild`（:959-984）。
- **占用替换期间不打断任务**：`AgentAutoDeploy.Run` 安装期先扫所有桥的 status.json，「有活跃任务就整轮跳过」（宁可这次不部署也不打断任务）——退出码 `ExitAgentBusy=2` 专为此设计。证据：`winui3/src/Yomori.App/Services/AgentAutoDeploy.cs`。

### 1.4 版本一致性双判据 —— AgentPayloadIdentity（内容哈希）+ AgentVersion（版本号）

- **内容判据**（`AgentPayloadIdentity.cs`）：`<主名>.dll + .deps.json + .runtimeconfig.json`（+ 源里有 .pri 就也比）**三/四件套逐字节 SHA256 对比**（`CryptographicOperations.FixedTimeEquals`）。为什么不用时间戳：全量副本时代 manifest 被 mt.exe 就地改写，时间戳只说明「何时改过」不说明「哪个版本」。为什么比业务 dll 而非 exe：两种载荷（精简 Yomori.Agent.exe / 全量 Yomori.exe）主名不同，写死名字会让一种永远判不等。
- **版本号判据**：部署「要不要替换」的唯一判据是 AgentVersion（RdpSessionService.cs:1636-1646 注释：以前比内容哈希，判据语义不清；现以版本号为准——「代理没更新就不替换」）。**读不出版本号按「需要替换」处理**（老载荷/文件被独占到读不出属性，:1689）。
- **判据分层**：`AgentStartGate`（闸门：副本在不在 + 自启快捷方式在不在，拆成两个独立判据、缺哪样做哪样）→ `AgentPayloadIdentity`（逐字节一致才复用槽位）→ `AgentVersion`（要不要替换的唯一判据）。三层各管一事，注释明确「两套口径迟早矛盾，那种最难查」。

### 1.5 独立进程看门狗 —— Yomori.Watchdog

- **为什么必须进程外**（`winui3/src/Yomori.Watchdog/HangProbe.cs` 头部注释）：进程内看门狗（Services/UiHangWatchdog.cs 后台线程 + SendMessageTimeout）逻辑正确，但真实冻结是 **68 个线程一起卡死、连低优先级后台线程都得不到调度**——看门狗自己也跑不起来 → 什么都没记下。**实测三次漏报**（10-12/10-15/10-10-16）后的结论「进程内线程监视进程内卡死不可靠，必须独立进程」。
- **探测判据**（固化踩坑经验）：`SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG)`：rc≠0=活着；rc=0 且窗口在=假死；rc=0 且窗口不在=正在关。**不要看 GetLastError**（卡死时 1460/0/1400 毫无规律）、**不要看 IsHungAppWindow**（卡在 WrLpcReply 时返回 false）。证据：HangProbe.cs。
- **自包含单文件**（Watchdog.csproj）：`SelfContained=true + PublishSingleFile + EnableCompression`——「丢到任何机器上都能跑，不依赖 .NET 运行时，也不会因为主程序目录里那一大堆 WinUI dll 产生歧义」；独立 asInvoker manifest（主程序 requireAdministrator 是编译期静态的，共用 exe 每次启动都弹 UAC）。
- **假死后三件事**：写日志（与 crash.log 分开）→ 抓全部线程状态快照（NtQuerySystemInformation 一次调用）→ 假死超 90s 触发一次 MiniDumpWriteDump 全内存转储（cooldown 10min 防写满磁盘）。证据：`winui3/src/Yomori.Watchdog/Program.cs`。
- **日志写 ProgramData 不写 exe 同级**（Program.cs 注释）：安装目录 C:\Program Files 只给 Administrators 写权限，看门狗是 asInvoker 标准用户——写 logs\ 会**静默失败一行都留不下**（2026-10-16 实测踩过）。

---

## 2. 对照 SoulLink 现状（每条带本仓库 file:line）

| 维度 | Yomori | SoulLink 现状 | 差距 |
|---|---|---|---|
| agent 形态 | 独立精简工程 73MB（无界面栈/无 WinRT 投影/无 pdb） | **主 exe 整体复制**改名 SoulLink.Agent.exe + manifest patch（`Services/ManualAgent.cs:148-188`：File.Copy + `ManifestPatcher.MakeAsInvoker`）；单文件发布后主 exe 约 3.3MB（框架依赖）——**体积我们反而小**（单文件架构红利），但复制的是全功能 exe（含 WPF UI 栈，代理模式不用） | 小（形态不同但代价可控） |
| 版本判据 | 独立 `AgentVersion` 元数据，与主程序解耦；读不出判「需替换」 | `Services/BrokerInstall.cs:21-31` `ValidAgent`：**只比 FileVersion（=主程序版本）**——主程序每次发版 agent 都「不一致」→ 触发重铺 | **大，值得抄** |
| 占用替换 | 双槽 agent/agent_b + 先送走后覆盖 + 正在跑任务整轮跳过 | 无双槽。agent 更新 = Broker 停服务换文件（`Services/BrokerService.cs`），被占用的处理依赖「停 agent→换→重启」的时序 | **大，值得抄** |
| 部署期保护 | 安装器调 `--deploy-agent`：扫所有桥 status.json，活跃任务 → ExitAgentBusy 跳过 | Broker 部署在服务安装/主程序重启时发生，无「正在执行任务就跳过」语义（挂机中的 agent 被直接顶掉重启） | 中 |
| 看门狗 | 独立进程 Watchdog（自包含单文件）：SendMessageTimeout 假死探测 + 线程快照 + MiniDump；进程内版三次漏报后弃用 | 主程序**无独立看门狗**；Web 前端有断流看门狗（`Services/WebPages.cs:1386-1400` 画面 10s 无新帧重连）；agent 侧有 `StartIdleReleaseWatchdog`（输入释放）；另有手动救援 sl-rescue.exe | **大，值得抄** |
| 身份判定 | AssemblyMetadata 读出空串 = 非代理，无需环境变量 | 按文件名 `SoulLink.Agent.exe` 判定（`Services/AppIdentity.cs:175-189`）——简单有效，单文件发布下无需元数据 | 小（各有取舍） |
| 启动闸门 | AgentStartGate：副本+自启拆两个判据，缺哪样补哪样，纯函数可测 | 无对应（部署即全做，无「只缺自启就只补自启」的轻量路径） | 中 |
| 独立看门狗 vs 进程内 | 「68 线程全卡时进程内看门狗自己也被调度饿死」——三次漏报实证 | 我们没有 UI 假死探测（WPF 主程序卡死只能靠用户跑 sl-rescue） | 抄 |

---

## 3. 分类结论：可借鉴 / 我们有而它没有 / 不该抄

### 3.1 可借鉴（3 条，按 ROI 排序）

**① Agent 版本号与主程序解耦（最高 ROI，改动最小）**
- 学它的：csproj 加 `<AgentVersion>` 写进 AssemblyMetadata；部署判据从「FileVersion == 主程序版本」改为「AgentVersion 一致就不替换」。
- 我们的落点：`SoulLink.csproj`（加 AgentVersion 元数据，只在 agent 相关代码——Relay/ 与 agent 实际用到的 Services——改动时 +1）+ `Services/BrokerInstall.cs` ValidAgent 改读元数据 + `Services/ManualAgent.cs`。
- 工作量：~0.5 天（csproj 一行 + 判据改一处 + 冒烟断言）。
- 预期收益：主程序发版不再触发 agent 无谓重铺（当前 dist 全量 3.3MB 单文件代价小，但**每次重铺都顶掉正在挂机的 agent**——挂机中断才是真实代价）；版本对账单一来源（csproj），杜绝「版本没变但行为变了」。
- 风险：忘 +1 → agent 不更新（Yomori 的对策是冒烟断言盯着，我们照抄）。

**② 双槽替换（agent_b 备用目录）**
- 学它的：常驻 agent 锁 DLL 时（File.Copy 抛占用、**目录改名也失败**——对方实测结论，四种共享模式都试过），先跨会话送走空闲 agent 原地覆盖；送不走则新副本落 `agent_b`、快捷方式/拉起路径改指新槽。
- 我们的落点：`Services/BrokerService.cs`（部署逻辑加槽位轮询）+ `Services/AutoStart.cs`（自启指向当前生效槽）。
- 工作量：~2 天（槽选择 + 状态判定 + 路径切换 + 测试）。
- 预期收益：更新不停机——挂机会话无中断升级（当前每次 agent 更新都断流重连，断一次=一次 IDR 等待+首帧延迟）。
- 前提：先做 ①（有版本判据才知道「要不要换槽」）。

**③ 独立进程看门狗（对主程序 WPF 假死）**
- 学它的：自包含单文件 exe（无 .NET 运行时依赖）、SendMessageTimeout 三态判定（不看 GetLastError/IsHungAppWindow——对方踩坑固化）、假死超阈值抓 MiniDump、日志写 ProgramData 不写安装目录。
- 我们的落点：新 `tools/watchdog/`（或 Services+csproj 独立工程）——探测目标是主程序窗口（挂机主控台卡死目前只能人工 sl-rescue）。
- 工作量：~1 天（核心 HangProbe 逻辑 ~200 行，探测/日志/dump 三件套）。
- 预期收益：主程序假死可观测（线程快照+dump 留证据）+ 可自动拉起救援（当前 sl-rescue 是纯手动）。
- 注意：探测目标是我们自己的主程序（不是用户机器）——风险低。

### 3.2 我们有而它没有（不必羡慕）

| 项 | 我们的实现 | Yomori 对应 |
|---|---|---|
| 单文件框架依赖发布（3.3MB） | `SoulLink.csproj` PublishSingleFile（build.bat 注释：3.3MB vs 自包含 67.7MB） | 代理 73MB 多文件 + Watchdog 自包含单文件（更重）——**我们体积形态更优**，双槽的「73MB 搬运浪费」痛点在我们这弱得多 |
| agent 由 Broker 服务统一管理 | `Services/BrokerService.cs`（srvany 式服务，agent 生命周期托管） | Yomori 靠启动目录快捷方式 + 登录拉起（无服务层） |
| 云更新链（latest.json/全量包） | `Services/CloudUpdater.cs` + publish-release.ps1 | 无（靠安装器） |
| 实时视频流 | 全套 /stream 管线 | 无（文件桥轮询 status.json） |

### 3.3 不该抄（2 条）

| 项 | 原因 |
|---|---|
| 精简独立工程拆分（链接源码+排除清单） | ① 我们是**单文件发布**——主 exe 本来就 3.3MB，「拆精简工程省 128MB」的动机不存在；② Yomori 的「链接+排除」是为共享 228MB 代码库的多工程形态设计，我们是单 csproj，强行拆会引入双工程同步成本；③ 排除清单的「编译期红线」价值可以低成本获得（见 §4 行动 0）。 |
| 安装期 --deploy-agent 自动部署 | 我们没有安装器场景（绿色软件+云更新），Broker 服务已覆盖部署时机；学它的是「部署前扫活跃任务就跳过」的语义（并入 ② 的设计）。 |

**GPL 合规说明**：Yomori 为 GPL-3.0（LICENSE 在仓库根）。本报告只做机制级对比与路径指认，**未复制任何代码**；若未来实现 §4 动作，必须是「机制思路的独立实现」（版本元数据判据/双槽轮换/SendMessageTimeout 探测均为公开通用技术，非 GPL 独创表达），且实现时不得参考对方源码逐行翻译。

---

## 4. 可落地动作清单（给 Lead 排期）

| # | 动作 | 改哪个文件 | 工作量 | 预期收益 |
|---|---|---|---|---|
| 0 | agent 编译红线（低成本先做）：在 csproj 加一条「Relay/ 与 agent 用 Services 的编译警告清单」注释 + 构建断言（若 agent 链引入 WPF-only 类型则警告） | `SoulLink.csproj` | 0.5 天 | 防「高危服务进常驻 agent」（Yomori 排除清单的弱化版） |
| 1 | AgentVersion 解耦（§3.1①） | `SoulLink.csproj` + `BrokerInstall.cs:21` + `ManualAgent.cs` | 0.5 天 | 发版不重铺 agent（挂机不中断） |
| 2 | 双槽替换（§3.1②） | `BrokerService.cs` + `AutoStart.cs` | 2 天 | 更新不停机 |
| 3 | 独立看门狗（§3.1③） | 新 `tools/watchdog/` | 1 天 | 主程序假死可观测可救援 |

推荐顺序：0 → 1 → 3 → 2（2 依赖 1 的版本判据，3 独立无依赖）。

---

## 5. 证据索引

| 结论 | Yomori 证据（仓库路径） |
|---|---|
| 独立 exe 动机与 228→73MB | `winui3/src/Yomori.Agent/Program.cs` 头部注释 |
| 精简工程（TFM/无投影/无 pdb） | `winui3/src/Yomori.Agent/Yomori.Agent.csproj` |
| 链接+排除（编译红线） | 同上 `<Compile ... Exclude>` |
| AgentVersion 解耦/冒烟断言 | 同上 `<AgentVersion>2.1.5</AgentVersion>` 与注释 |
| 版本元数据读取/空串身份判定 | `winui3/src/Yomori.App/Models/AgentVersionInfo.cs` |
| 内容哈希判据 | `winui3/src/Yomori.App/Services/AgentPayloadIdentity.cs` |
| 安装期部署/ExitAgentBusy | `winui3/src/Yomori.App/Services/AgentAutoDeploy.cs` |
| 双槽/占用替换/目录改名死路 | `winui3/src/Yomori.App/Services/RdpSessionService.cs` :829/:853/:860-1017/:1073 |
| 启动闸门拆判据 | `winui3/src/Yomori.App/Services/AgentStartGate.cs` |
| 看门狗进程外结论/三态判据 | `winui3/src/Yomori.Watchdog/HangProbe.cs` 头部 |
| 看门狗自包含/dump/日志路径 | `winui3/src/Yomori.Watchdog/Yomori.Watchdog.csproj` + `Program.cs` |
| agent 执行模型 | `winui3/src/Yomori.App/Services/RdpAgentRunner.cs` |

| 结论 | SoulLink 证据 |
|---|---|
| agent=主 exe 复制+manifest patch | `Services/ManualAgent.cs:148-188` |
| 版本判据=主程序 FileVersion | `Services/BrokerInstall.cs:21-31` |
| 按文件名判 agent 身份 | `Services/AppIdentity.cs:175-189` |
| 单文件 3.3MB 发布 | `SoulLink.csproj`（PublishSingleFile）+ `build.bat` 注释 |
| Web 断流看门狗（非 UI 假死） | `Services/WebPages.cs:1386-1400` |
| agent 计划任务启动 | `Services/AutoStart.cs` |
| Broker 服务管理 agent | `Services/BrokerService.cs` |

---

## 6. 边界遵守自查

只读研究：GitHub API/blob 抓取源码（大文件经 curl 转存 %TEMP% 读取，测后清理）；未 clone、未改任何产品代码、未碰运行进程、未 git 操作。GPL-3.0 代码零复制（仅机制描述+路径指认）。唯一写入 = 本文档。
