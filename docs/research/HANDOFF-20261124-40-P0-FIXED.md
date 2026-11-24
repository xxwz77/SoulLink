# 交接：P0 三修复收官（投送 / 界面状态 / 无脑重试）— 2026-11-24

> 日期：2026-11-24
> 作者：core-fixer（dsh-team `soullink-p0-fix-yomori` / member `core-fixer` / task `t6`）
> 状态：**交接文档（只读产出，未改产品代码）**
> 输入：t1/t2/t3 实现改动、`VERIFY-20261124-P0-FIXED.md`（t5 独立验证）、`BENCH-YOMORI-04-RESEARCH-20261124.md`（t4 对标研究）

---

## 1. 三句话现状

1. **P0 三个问题（投送全线失败 / 界面状态造假 / 不可恢复错误无脑重试）已全部修复并通过独立验证**：构建 0 error、判据自检两条路径成立、部署后真实投送成功、正确部署后 AGENT_INVALID 新增 0 次。
2. **根因是「两套互斥的版本判据」+「agent 仍是主程序字节副本」**：`ReadAgentVersion` 读托管元数据（单文件下恒 null）→ 投送恒拒；`AgentSync.Inspect` 读 FileVersion（两边都 3.3.39.0）→ 界面恒就绪。两个症状一个根。
3. **下一步两件事**：① 把已拆出的独立 agent 工程（t7a）接进 Broker 拆分与部署（t16/T7b）；② 把 Yomori 研究结论中「90 个 layout-check 断言串成 runner + 挂进构建」落地（冻结项 #7 已解冻换形态，0.5~2 人日）。

---

## 2. P0-1 / P0-2 / P0-3 各自根因 + 修法 + 验证结论

### 2.1 P0-1 投送全线失败（AGENT_INVALID）

**根因**：单文件发布（`PublishSingleFile=true`）下，`dist\SoulLink.exe` 与 `dist\SoulLink.Agent.exe` 的 `PEReader.HasMetadata` 均为 `False`（托管元数据在 bundle 内部），于是 `Services/BrokerInstall.cs` 的 `ReadAgentVersion()` 对两个文件都返回 `null` → `av==null → return false` → `ValidAgent` 永远拒绝 → `BrokerService.cs:196` / `SystemLauncher.cs:459` 投送全线失败。与版本号无关。

**修法（t1）**：
- `Services/BrokerInstall.cs`：`ReadAgentVersion` 改三级回退——① exe 同目录 `SoulLink.Agent.version` → ② 托管元数据 `AssemblyMetadata("AgentVersion")` → ③ `FileVersion`（单文件下实测可读，四段归一化成三段）。新增 `NormalizeVersion`（四段裁三段，读与比共用）。
- `SoulLink.csproj`：加 Target 把 `$(Version)` 写成发布输出的 `SoulLink.Agent.version`；`<Version>` 升 3.3.40。
- `build.bat` / `tools/broker-setup/Program.cs` / `Services/ManualAgent.cs`：version 文件随包复制到 dist、安装目录、手动代理导出目录。

**验证结论（t5）**：判据自检两条路径成立——「双方有标识 → ValidAgent=True」「agent 侧缺标识 → ValidAgent=False」。部署 3.3.40 后真实投送（session 1 Active）返回 `AGENT_STARTED`，正确部署后 broker.log 新增 AGENT_INVALID = **0 次**。

### 2.2 P0-2 界面状态造假

**根因（四处）**：① XAML 硬编码「引擎就绪」假默认值；② 只有设置页触发检查；③ 只在启动查一次；④「Broker 可用」探测不含 agent 判定。核心是**两套版本判据打架**：界面 `AgentSync.Inspect` 读 FileVersion 判等（恒 Ready），投送 `ValidAgent` 读元数据判拒（恒 AGENT_INVALID）——同一次运行里顶栏绿「就绪」但每次投送都失败。

**修法（t2）**：
- `Views/MainWindow.xaml` / `Views/SettingsPage.xaml`：默认值改中性「检测中…」+ 灰点（`#FF8B9CC8`），绿灯只有 `EngineState.Ready` 一条路。
- `Services/AgentSync.cs`：`Inspect` 改调用 `BrokerInstall.ValidAgent`（与投送同源判据，禁止自算一套）；Tooltip 三方版本「当前程序 X · 服务 Y · 注入代理 Z · 判据 通过/不通过（依据…）」；公开 `AgentVerdict` + `LastVerdict` 供界面/t3 复用；Ready 判据 = 服务 Running + agent 存在 + 版本通过（任一不满足不得 Ready）。
- `Views/MainWindow.xaml.cs`：新增 `StartEnginePeriodicRecheck()`（`TickHub.RegisterBackground` 30s，零新建 DispatcherTimer）；订阅 `AgentSync.RecheckRequested`（投送失败触发）与 LaunchView/SettingsView 状态事件；health 探测降为 detail 附注，只「health 失败但服务 Running」才降 Error。

**验证结论（t2/t5）**：服务 Stopped 时顶栏显示琥珀「引擎未运行」而非绿；Tooltip 含三方版本+判据依据+原因；17 条断言 ALL PASS（5 态文案、绿色仅 Ready、判定顺序、服务 Stopped 绝不 Ready）。

### 2.3 P0-3 不可恢复错误无脑重试

**根因**：投送失败只按「瞬时可恢复」统一走指数退避（`_rebuildAttempt` ≤ 5，退避 2/4/8/16/30s），**不区分 AGENT_INVALID（不可恢复）还是网络抖动（可恢复）**——服务没装/没跑/agent 版本不符时，每 10 秒刷一轮失败日志。

**修法（t3，只改 `Controls/RdpConsole.cs`，+234 行纯加法）**：
- `ClassifyDeployFailure`（:843）：权威判据 = `AgentSync.Inspect` 5 态——NotInstalled / AgentNeedsUpdate / NotRunning / Error → **不可恢复**（停止自动重试）；Ready → 可恢复（维持退避）。外加 message 文案兜底（AGENT_INVALID / INSTALL_INVALID / NOT_INSTALLED / SERVICE_OUTDATED）。
- `HaltDeploy`（:874）：置 `_deployHalted`；日志每原因只记一次（`_haltLoggedKey`）；挂 30s `DeployAutoResume` 订阅。
- 恢复入口（全部保留）：手动 `ResumeIfEngineReadyAsync`（:973）、自动 `AutoResumeDeploy`（:944）——都先 `Inspect==Ready` 再清标志 + `ResetRebuildCounter()` + 重投。非 Ready 静默跳过（不刷日志）。
- `_rebuildAttempt` 归零点共 4 处（既有 :1050/:3401 未动，新增 :953/:989）。

**验证结论（t3/t5）**：分类器驱动断言 ALL PASS；现场服务 Stopped 实测「服务未运行→不可恢复」。

---

## 3. 版本与工作区状态

| 项 | 值 |
|---|---|
| `<Version>`（主程序） | **3.3.40**（`SoulLink.csproj`） |
| `<AgentVersion>`（主程序要求） | **1.0.0**（`SoulLink.csproj`，语义 = 主程序要求的 agent 最低版本） |
| Agent 工程 `<Version>` | **1.0.0**（`Agent/SoulLink.Agent.csproj`，语义 = agent 自身版本） |
| git HEAD | **588c007**（`docs: 清理注释里误留的占位词`） |
| 未提交文件数 | **77**（`git status --porcelain` 行数；其中 ` M` 35 个、`??` 28 个） |

> ★ 解耦验证点：主程序 3.3.40 升版**不触发 agent 重铺**——agent 独立版本仍是 1.0.0，两者通过 `SoulLink.Agent.version`（agent 自身）与 `SoulLink.Agent.required.version`（主程序要求）两个文件语义分离。

---

## 4. 部署与自检怎么做（可复跑命令）

### 4.1 构建（0 error 为门槛）

    dotnet build -c Release --nologo

期望末尾：`0 个错误`（现有 NU1510 / CS8632 等警告是既有项，与 P0 无关）。

### 4.2 全量打包（dist 四件套齐全）

    cmd /c "echo. | build.bat"

期望 `dist` 与 `artifacts\dist-3.3.40` 同时有：
- `SoulLink.exe`（FileVersion 3.3.40.0）
- `SoulLink.Agent.exe`（独立 agent 工程产物，FileVersion 1.0.0.0，远小于主程序）
- `SoulLink.Agent.version`（内容 `1.0.0`）
- `SoulLink.Agent.required.version`（内容 `1.0.0`）

### 4.3 判据自检（复现两条路径）

用独立控制台探针引用编译产物 `SoulLink.dll`，只调 `BrokerInstall`，不加载 WPF：

    # 双方有标识 → ValidAgent=true
    # agent 缺标识（非 PE + 删 version 文件）→ ValidAgent=false
    # 详见 VERIFY-20261124-P0-FIXED.md §3

### 4.4 真实投送自检（A2 登录后补测）

1. A2 用 RDP 登录到 Active；
2. 用主程序或直接 Broker 管道对 session=4 user=A2 发 ensure；
3. 期望返回 `AGENT_STARTED`（非 AGENT_INVALID）；
4. 查 broker.log 该次请求后无 AGENT_INVALID。

---

## 5. 仍未做 / 已冻结的项（含 Yomori 新一轮研究结论指向）

### 5.1 仍待做（按优先级）

| 项 | 责任 | 说明 |
|---|---|---|
| **Broker 拆分 + 服务路径迁移** | t16/T7b | 从 `SoulLink.exe --broker` 迁到 `SoulLink.Broker.exe`；`tools/broker-setup/Program.cs:244-258` 补 `ChangeServiceConfig`（服务已存在就地改路径，不删不重装） |
| **判据分层 + 部署前守卫** | 后续 | Yomori 硬纪律「两套口径迟早一个说更新一个说不用」——我们已在 t1/t2 归一判据，但「部署时活跃任务不覆盖」守卫仍需落地 |
| **90 个 layout-check 断言串成 runner + 挂进构建** | 冻结 #7 已解冻 | 有现成 `scripts/run-assertions.ps1`（T19，快闸门 37 个 32s / 全量 90 个 215s）+ `artifacts/assertions-baseline.json`；8 个 BUILD-FAIL 是「产品 API 变了断言没跟上」，可修 4 个；第二步挂 build.bat 需先全绿 |
| **build.bat copy 失败断言** | t10（Lead 已并入） | 本轮踩过：dist\SoulLink.exe 被占用、copy 静默失败停在 3.3.39 |

### 5.2 已冻结（本轮不动）

- **#2/#5/#6/#10/#12** 继续冻结（`BENCH-YOMORI-04` §4 裁决）；**#5「更新 6 态」正式作废**（前提不成立）。
- **Yomori 上游已冻结 6 天**（最后提交 2026-11-18 `089d06c7`，本身是 fork）——「找新增」为空，价值全在「上次漏读的文件」：`RdpErrorText.cs`（30+ 原因码→人话+动作）、`RdpClientStateMachine.cs`（纯函数状态机）、`AgentStartGate.cs`（双正交判据）、`build.yml`（34 处构建断言）。
- **GPL-3.0 红线**：全文只做机制描述 + 路径指认，**未复制任何 Yomori 代码**。

---

## 6. 铁律沿用

> 详见项目根 `AGENTS.md` 与 `docs/research/TEAM-REDLINES.md`（2026-11-24 重划，按「账号/目录」判归属，不按 PID）。

1. **版本号唯一来源 = `SoulLink.csproj` 的 `<Version>`**，改动必须升版本。
2. **发布只走 `scripts/publish-release.ps1`**，一律全量包；发布顺序先 exe → HEAD 校验 → 才写 latest.json。
3. **判据唯一**：`AgentSync.Inspect` 与 `BrokerInstall.ValidAgent` 必须同源，禁止自算一套版本口径。
4. **绝对禁动**：A1 / A4 两个账号（判据 = 进程 owner），以及 `dist\stable\SoulLink-Stable.exe`。
5. **停进程只按精确 PID**，停前先核 owner；停服务用 `Stop-Service` 不用硬杀。
6. **静默失败必须留痕**（LogBus.Warn），本轮 P0 的同类病（copy 静默失败 / marker 复制吞异常）。
7. **临时目录只放 %TEMP%**，绝不建在项目根（本轮踩过 CS0017）。
8. **新增周期任务走 TickHub**，不新建 DispatcherTimer（性能约束）。
9. **不改注册表**（服务配置用 ChangeServiceConfig API）；不弹窗；不 git commit（Lead 统一提交）。
10. **产物超过 5 MB 先问为什么**；构建产物不入库也不囤积。

---

*索引登记见 `docs/research/README.md` 追加行。*
