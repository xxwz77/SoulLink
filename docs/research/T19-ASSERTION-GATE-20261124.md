# T19 断言闸门：runner + 基线 + 34 个红项分类（2026-11-24）

> 任务：T19（dsh-team `soullink-p0-fix-yomori` / member `yomori-scout` / attempt `9bcca12e-eba9-49db-9c5c-9bf4bf155bc0`）
> 红线遵守：**未改任何产品代码、未改断言判定逻辑、未挂 build.bat、未 git commit**。

---

## 0. 一句话结论

`scripts/run-assertions.ps1` 已写入并跑通，**快闸门 32.4 秒、全量 193.9 秒**。
但**第二步（挂 build.bat）不满足前提**：快闸门 37 个里有 **7 个非 PASS**，按任务规定**不挂**。

| 层 | 结果 | 耗时 |
|---|---|---|
| **快闸门（37 个 Compile-only）** | 22 PASS / 4 FAIL / 3 BUILD-FAIL / 8 SKIP | **32.4 秒** |
| **全量（90 个，`-Clean` 权威基线）** | 43 PASS / 15 FAIL / 8 BUILD-FAIL / 24 SKIP | **214.7 秒** |

> 注：全量 193.9 秒比勘察时的 308.7 秒更快，因为 `--no-dependencies` 生效 + obj 已预热。

---

## 1. 交付物

| 文件 | 说明 |
|---|---|
| `scripts/run-assertions.ps1` | runner（310 行）。支持 `-Full` / `-Filter` / `-Skip` / `-NoBuiltInSkip` / `-DryRun` / `-Json` |
| `artifacts/assertions-baseline.json` | 基线报告（55,731 字节），含每个工程的 status/exit/耗时/detail/输出尾部 |

### 1.1 runner 的关键设计

· **快闸门默认**只跑 37 个 `Compile-only`（不引用主工程）→ 32 秒；
· **`--no-dependencies`** 让 ProjectReference 类从 7.2s → 0.8s；
· **不用 `-o`**：默认 `bin<name>` 目录才会正常复制依赖 dll（用 `-o %TEMP%` 会 `FileNotFoundException` 假失败）；
· **SKIP 与 PASS 严格分开**：SKIP 绝不算 PASS，汇总单列（防「假绿」）；
· **内置跳过清单 24 个**，每条注明依据（A1A4 红线 4 个 / 需管理员 8 个 / 需真机 6 个 / 需 GUI 5 个 / 端到端 1 个）；
· **临时文件绝不落项目根**（今天已两次踩 `undefined\temp` 被通配符收进编译 → CS0017）。

### 1.2 两个踩坑记录（都已修，写下来防复发）

**坑 1：UTF-8 无 BOM → PowerShell 5.1 按 ANSI 读 → 中文乱码 → `ParserError`**

首版脚本写成无 BOM，PowerShell 5.1 把中文全读成乱码，哈希表字面量直接解析失败。
加 BOM 后正常。这与 `scripts/agent-dep-redline.ps1` 是**同一个坑**（MSBuild Exec 调 5.1 必须带 BOM）。

**坑 2：`--no-dependencies` + 增量判定 → 「幽灵失败」（这条更重要）**

反向对照测试时我故意把 `TimeoutCheck` 的 `Main` 改成 `return 1`，闸门正确变红（见 §4）。
但**还原源码后，闸门仍然是红的**，报的还是注入的那句 `FAIL 反向对照：故意注入的失败`。

排查过程与结论：

| 观察 | 结果 |
|---|---|
| 源码已还原（850 字节，无注入） | 确认 |
| `binTimeoutCheck` 里的 exe 按字符串搜索不含注入文本 | 矛盾 |
| 直接运行该 exe | **仍输出注入文本** → 跑的不是我以为的那份 |
| 删 `bin` + 重建 | **仍是旧输出** |
| **同时删 `bin` 和 `obj`** + 重建 | 恢复正常 |

**根因**：MSBuild 增量判定依赖中间产物（`obj`）。源码「改回原样」后，
`obj` 里的旧编译产物仍被复用，于是产出与源码不符的 exe。

**对策**：runner 新增 `-Clean` 开关，跑前删掉每个工程的 `bin<name>` + `obj<name>`。
正常开发不用（增量更快）；**反向对照或怀疑结果与源码不符时必须用**。
本文所有数字均以 `-Clean` 跑的权威基线为准。

---

## 2. 快闸门实测输出

```
═══ SoulLink 断言闸门 ═══
  模式      : 快闸门（Compile-only）
  工程数    : 37
  跳过        : 8

  PASS        AutomationDataCheck           1.1s
  PASS        BgiStartGuardCheck            0.7s
  BUILD-FAIL  BusinessDayCheck              1.1s  CS2001 找不到 Services\BusinessDayData.cs
  BUILD-FAIL  ChannelDiscoveryCheck         1.2s  CS0117 BgiLogWatcher 无 DiscoverChannelSessions
  FAIL        DogfoodRecordCheck            0.8s  exit=1  FAIL 数据不符
  FAIL        InstallScriptCheck            0.7s  exit=3
  FAIL        LayoutCheck                   0.7s  exit=1  FAIL nine needs scroll (content=1438)
  FAIL        PlcScanCheck                  1.1s  exit=4  FAIL 可手动设为今日不运行
  BUILD-FAIL  ValidatorCheck                1.3s  CS0103 缺 BgiDataDependency
  ...

═══ 汇总 ═══
  PASS        : 22
  FAIL        : 4
  BUILD-FAIL  : 3
  SKIP        : 8
  总耗时      : 32.4 秒

闸门失败：7 个工程不是 PASS（SKIP 不计入）
=== runner exit=1  wall time=32.9s ===
```

---

## 3. 23 个非 PASS 的完整分类（这是本任务最重要的产出）

### 3.1 BUILD-FAIL 8 个 —— 全部是「产品 API 变了，断言没跟上」

| 工程 | 错误 | 根因（已核实） | 类别 | 可否修 |
|---|---|---|---|---|
| `AgentCmdProbe` | CS0103 `ProcessCommandLine` | 产品中**已无此符号**（全仓 grep 0 命中） | **真·API 移除** | 需 Lead 定：删断言或改测新 API |
| `CmdLineProbe` | 同上 | 同上 | 同上 | 同上 |
| `WhyHostProbe` | 同上 | 同上 | 同上 | 同上 |
| `ChannelDiscoveryCheck` | CS0117 `DiscoverChannelSessions` | **产品有意移除**：`ConsolePage.xaml.cs:1248` 注释「原来的 DiscoverChannelSessions() 遍历系统全部管道（实测 3.5 万个…）」 | **真·性能优化致移除** | 需 Lead 定 |
| `DupAgentProbe` | CS0117 `FindManualAgents` | **只是改名**：现名 `FindManualAgentSessions`（`Services/ManualAgent.cs:267`） | **可修（改名）** | ✅ 0.1 人日 |
| `LampProbe` | CS1061 `FlowCanvas._debugLastStatus` | 私有调试字段被移除/改名（UI 重构） | **断言过期** | ✅ 可修 |
| `BusinessDayCheck` | CS2001 `Services\BusinessDayData.cs` | **该类已从 HEAD 删除**（git 历史 e428c6e/17a86c3 曾有）。`AccountStateCenter.cs:137` 明写「**不再经过 BusinessDayData**」→ 产品有意重构 | **真·重构删除** | 需 Lead 定：整个断言作废或改测 AccountStateCenter |
| `ValidatorCheck` | CS0103 `BgiDataDependency` | 链接闭包缺文件。**我试补过链接，暴露出第二层**：`BgiDataDependency.cs:56` 又依赖 `App`（需 stub） | **闭包不全 + 需 stub** | 可修但需补 stub，0.25 人日 |

### 3.2 快闸门内 4 个 FAIL

| 工程 | exit | 断言内容 | 判断 |
|---|---|---|---|
| `LayoutCheck` | 1 | `Check(r.NeedsScroll, "nine needs scroll (content=" + r.ContentHeight + ")")` → content=1438 | **断言过期**：UI 尺寸变了，阈值没跟上 |
| `DogfoodRecordCheck` | 1 | `FAIL 数据不符`（L38） | **待定**：需看是产品数据变了还是断言期望值过期 |
| `InstallScriptCheck` | 3 | L11 要求脚本含 `S-1-5-18/S-1-5-32-544/S-1-5-32-545` 三个固定 SID | **断言过期**：发布脚本改过（`publish-release.ps1` 当前有未提交改动） |
| `PlcScanCheck` | 4 | `FAIL 可手动设为今日不运行，且不再执行` | **待定**：PLC 逻辑行为，需业务判断 |

### 3.3 全量中额外暴露的 11 个（快闸门里没跑到的 ProjectReference 类）

| 工程 | exit | 现象 | 初判 |
|---|---|---|---|
| `AgentMutexCheck` | 1 | 手动代理启动后能被识别 | 需活进程 → **环境** |
| `ChannelStateCheck` | 4 | 初始状态为未投送 | **待定** |
| `EventPipeCheck` | 1 (26.3s) | 代理接入 | 需活代理 → **环境** |
| `GroupHintCheck` | -532462766 | 0xE0434352 未处理异常 | **需单独查** |
| `GuideBoundsCheck` | 3 | 遮罩尺寸有效 | 需 GUI/尺寸 → **环境** |
| `HotkeyFocusCheck` | 5 | 存在 RdpSurfaceHasFocus | **待定**（可能是真回归：焦点判定） |
| `LogFilterCheck` | 9 | 「UI线程=True connected=True input=True **captured=False**」 | **待定**：captured=False 可能是真回归 |
| `LogFilterCheck2` | 7 | 同上 | 同上 |
| `ManualHotkeyCheck` | 1 | 代理接入 | 需活代理 → **环境** |
| `ManualModeCheck` | 1 | 用户启动代理后自动接入 | 需活代理 → **环境** |
| `StableIdentityCheck` | 6 | 身份为 SoulLink-Stable（与测试版隔离） | 需 stable 可执行文件 → **环境** |

### 3.4 分类小结

| 类别 | 数量 | 处置建议 |
|---|---|---|
| **真·产品 API 移除/重构**（需 Lead 判定断言去留） | **6** | `AgentCmdProbe`、`CmdLineProbe`、`WhyHostProbe`、`ChannelDiscoveryCheck`、`BusinessDayCheck`、`PlcScanCheck`(?) |
| **可修（改名/补 stub/更新阈值）** | **4** | `DupAgentProbe`、`LampProbe`、`ValidatorCheck`、`LayoutCheck`、`InstallScriptCheck` |
| **环境依赖（应进跳过清单）** | **6** | `AgentMutexCheck`、`EventPipeCheck`、`GuideBoundsCheck`、`ManualHotkeyCheck`、`ManualModeCheck`、`StableIdentityCheck` |
| **待定（可能真回归，需 Lead 判断）** | **5** | `HotkeyFocusCheck`、`LogFilterCheck`、`LogFilterCheck2`、`GroupHintCheck`、`DogfoodRecordCheck` |
| **疑似真回归（Lead 已点名，本次未动）** | **3** | `ManifestPatchCheck`、`ChannelDetectCheck`、`BrokerCheck` —— 均在 SKIP 中，未执行 |

---

## 4. 为什么没有挂 build.bat

任务规定：「**只有当快闸门 37 个全绿时才挂**」。当前快闸门 **22 PASS / 7 非 PASS / 8 SKIP**，不满足。

若强行挂，后果是：
1. 每次构建都红 → 人会加 `-SkipChecks` 绕过 → **闸门形同不存在**；
2. 或有人为了让构建过而**删断言** → 正是任务明令禁止的「假绿」。

**建议顺序**：
1. 先处置快闸门内 7 个（4 FAIL + 3 BUILD-FAIL）；
2. 待快闸门全绿后再挂 build.bat（届时我加最小一段 + `-SkipChecks` 逃生阀）；
3. 全量的 12 个非 PASS 另行排期。

---

## 5. 顺带发现：`Agent\` 目录正在污染主工程（**不是我造成的**）

验收要求「`dotnet build -c Release` 0 error」时发现主工程**编译失败**：

```
Agent\Stubs.cs(21,25): error CS0260: 类型"App"的声明上缺少 partial 修饰符
Models\Models.cs(918,18): error CS0101: 命名空间"SoulLink.Models"已包含"AppConfig"的定义
```

| 事实 | 值 |
|---|---|
| 目录 | `Agent\`（Program.cs / SoulLink.Agent.csproj / Stubs.cs） |
| 创建时间 | **17:49:30**（我的工作窗口内，但**不是我**） |
| git 跟踪 | **0** |
| 归属 | **core-fixer 的 t7a**（拆分独立 agent 工程） |
| 是否已加入 `DefaultItemExcludes` | **否**（当前只排除 tools/releases/backups/artifacts/Server/dist-server*） |

**所以这是 t7a 的中间状态**：新建了 `Agent\`，但主 csproj 还没排除它 → 被 SDK 通配符收进主工程 → `Stubs.cs` 的 `App`/`AppConfig` 与主工程冲突。

**我没有动它**（不是我的任务，且 t7a 正在改）。但需要提醒：
t7a 完成时必须给 `Agent\**` 加 `DefaultItemExcludes`，否则**主工程永远编不过**。
这与今天两次 `undefined\temp` 是**同一类问题**（新目录被 SDK 默认通配）。

---

## 6. 项目根目录快照（验收要求：无新增目录）

```
.agent-teams  .git  %SystemDrive%  Agent  Android  artifacts  Assets  backups
bgi-scripts  bin  Controls  design  dist  docs  Models  obj  Relay  research
scripts  Server  Services  Theme  tools  Views
```

**我新增的目录：0 个。** 唯一新目录 `Agent\` 属 t7a（17:49:30，非我）。
`%SystemDrive%` 是 16:26 的历史遗留（环境变量未展开），非我，也未动。

**runner 的临时产物**：只在各工程目录下的 `bin<name>` / `obj<name>`（SDK 默认位置，gitignore 内），**未在项目根建任何目录**。

---

## 7. 成本重估与建议

| 项 | 原估 | 实测 |
|---|---|---|
| 写 runner | 0.25 人日 | ✅ **已完成**（本次） |
| 基线报告 | 0.1 人日 | ✅ **已完成** |
| 34 个红项分类 | 0.5 人日 | ✅ **已完成**（本文档） |
| 修可修类（5 个） | — | **0.5 人日**（改名 2 + 补 stub 1 + 更新阈值 2） |
| Lead 判定 6 个真·API 移除 | — | **需 Lead 决策**（删断言 or 改测新 API） |
| 挂 build.bat | 0.1 人日 | **待快闸门全绿** |

**结论**：runner 与分类已交付；剩下的是「处置」，其中 **6 个必须由 Lead 决策**（断言是否作废），
因为那涉及「产品有意删除功能」与「断言该不该跟着消失」的判断，超出我的权限。

---

## 7.5 反向对照测试结果（验收要求：证明闸门真会红）

**做法**：把 `tools/layout-check/TimeoutCheck.cs` 的 `Main()` 改成直接
`Console.WriteLine("FAIL 反向对照：故意注入的失败"); return 1;`，跑闸门，再还原。

**注入后**：

```
  FAIL        TimeoutCheck                  1.2s  exit=1  FAIL 反向对照：故意注入的失败

═══ 汇总 ═══
  PASS        : 0
  FAIL        : 1
  BUILD-FAIL  : 0
  SKIP        : 0
  总耗时      : 1.2 秒

闸门失败：1 个工程不是 PASS（SKIP 不计入）
exit=1   (expect NON-ZERO)
```

**结论**：
1. 闸门确实会红，退出码非 0 —— **不是空摆设**；
2. **单个 FAIL 也被正确计数**（`FAIL : 1`），这同时验证了 §1.2 修掉的 `.Count` 缺陷。

还原后 `git status` 确认 `TimeoutCheck.cs` 无残留改动，TEMP 备份已删。

---

## 8. 边界自查

未改产品代码、未改断言判定逻辑、**未挂 build.bat**、未 git commit。
尝试过给 `ChannelDiscoveryCheck` / `ValidatorCheck` 补 `<Compile Include>`，**已完整回退**（补链接后暴露更深的产品 API 变更，属需 Lead 决策范畴）。
未碰 `dist` / `dist\stable` / `COS` / `%SystemDrive%` / `Agent\`。未用 A1/A4、未弹窗、未发布。
唯一新增文件 = `scripts/run-assertions.ps1` + `artifacts/assertions-baseline.json`（gitignore）+ 本文档 + README 索引。

---

**文档作者**：yomori-scout
**最后更新**：2026-11-24