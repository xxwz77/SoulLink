# 自动化「无 END + 日志触点只触发一次」根因审计

> 审计日期：2026-11-22
> 审计人：automation-auditor（agent-team 成员）
> 用户原话：「昨天有人跟我反馈，自动化抓日志好像只能抓到一次，启动一次。（没加 end 的情况下）」
> 结论要求：file:line 可查证，不靠猜。本文所有行号均以工作区源码为准。

---

## 一、结论先行（根因一句话）

**不是锁存/调度/上升沿把流程"卡死"了，而是日志触点本身只在"最近 N 秒时间窗"里命中。**
一条日志行一旦写入，它只会在**写入时刻之后的 `withinSeconds` 秒窗口内**被判定为"命中"；
窗口一过，同一行日志就不再命中。用户看到的"只触发一次"，本质是：

1. 用户观察的那条日志（例如一次"任务结束"）**只出现了一次**，之后没有新的同类日志；
2. 流程（无 END，循环）在命中并执行一次动作后，下一轮扫描再读尾窗时，那条日志已经**超出 `withinSeconds` 时间窗**，于是触点返回 `false`，动作不再执行。

我的最小复现测试（直接编译生产源码 `PlcScanEngine` + `PlcLogMatcher`，`tools/layout-check/LogRetriggerCheck.cs`）**全部通过**，证明引擎侧**每轮收尾确实会清空本轮状态并重新获取条件**，无 END 循环**可以在窗口内被再次触发**，不存在"锁存卡死"。

**所以这是一个"电平/时间窗语义"与用户预期不符的问题，不是程序卡死的 bug**——但存在两个必须修的体验缺陷（见第五节）：
- `TrigLogMatch` 默认窗口只有 **10 秒**（`FlowNodeCatalog.cs:81`、`FlowRunner.cs:324`），太短，用户"看一眼的工夫"就过期；
- 界面/文档把"日志匹配"描述得像"看到这条日志就触发一次"，没有提示它其实是"10 秒内出现才触发"。

---

## 二、逐条疑点判定（含 file:line 证据）

### 疑点 1：无 END 时锁存是否会阻止后续扫描？——**不会**

- 有 END 才锁存：`PlcScanEngine.cs:53-72`。`hasEnd = PlcLogic.HasEnd(flow)`；只有 `hasEnd == true` 才检查 `IsDailyComplete` 并提前返回。无 END 流程**完全不走**这段锁存。
- 无 END 的 `flow.RunComplete` 会被"进入扫描时"清掉：`PlcScanEngine.cs:73-77`：

  ```csharp
  else if (flow.RunComplete) { flow.RunComplete = false; }
  ```

- `CompleteRun`（收尾）里 `flow.RunComplete = hasEnd`：`FlowRunLifecycle.cs:40`。无 END 时 `hasEnd=false`，所以 `RunComplete` 恒为 false，且 `daily=false` 不写 `DailyLatchKey`（`FlowRunLifecycle.cs:45` + `PlcScanEngine.cs:228` 传 `hasEnd && !todayReopened`）。
- `DailyLatchKey` 只由 `CompleteToday`/`CompleteRun(daily=true)` 写，二者都要求 `hasEnd`：`PlcLogic.cs:78-79`、`FlowRunLifecycle.cs:45`。无 END 永远不写。
- `PlcPower` 在无 END 收尾时被 `CompleteRun` 清空：`FlowRunLifecycle.cs:44`。

**判定**：无 END 流程不存在"被写成已完成从而不再扫描"的路径。`PlcScanEngine.cs:73-77` 的 `else if` 只在 `hasEnd==false` 时生效，且扫描本身每 1 秒由 `ScheduleRunner.TickAutomations` 无条件调度（见疑点 5），不存在"扫描根本没被调度"的情况。

### 疑点 2：动作是否靠上升沿才执行？——**是，但无 END 收尾会主动复位，不构成"永久卡死"**

- 上升沿判定在 `PlcScanEngine.cs:103`（`bool previous = flow.PlcPower.TryGetValue(n.Id, out var p) && p;`）和 `PlcScanEngine.cs:163`：

  ```csharp
  if (input && selected && (manual || lastFailed || !previous || !flow.ActionResults.ContainsKey(n.Id)))
  ```

  即"只有上一轮没导通、或从未执行过、或上次失败、或手动"才会再次执行。`ActionResults` 保留成功结果（`PlcScanEngine.cs:190-191`），成功动作在电平持续时不会重复执行——这是**正确**的防重复语义。

- 关键：无 END 每轮完成会 `CompleteRun` → 清空 `PlcPower` 和 `ActionResults`（`FlowRunLifecycle.cs:43-44`）。所以只要触点**先断开再重新满足**，下一轮就是"新的上升沿"，动作会再次执行。我复现测试里的"窗口内再次命中→第二次执行"（`ran==2`）就验证了这点。

- 注释里 2.17.24 修复的"失败一次后永久卡死"（`PlcScanEngine.cs:145-161`）是**历史 bug**，现已通过 `lastFailed` 重试解决，与本报告无关。

**判定**：动作确实是上升沿触发，但无 END 的收尾会清 `PlcPower`/`ActionResults`，所以只要触点再次变真就会重新触发。疑点 2 的"PlcPower 一直 true 就再也不触发"对**无 END 流程不成立**（对**有 END 流程**正是它想要的"当日只跑一次"）。

### 疑点 3：日志尾窗是消费型还是快照型？——**快照型（不消费、不推进游标）**

- 尾窗是"环形队列，只保留最近 N 行/字符"：`BgiLogCenter.cs:98-99`（`Queue<BgiLogLine> Tail`）、`PushTail` 在 `BgiLogCenter.cs:789-801`（`TailMaxLines = 800`、`TailBudgetChars = 200_000`，见 `BgiLogCenter.cs:34-37`）。
- `RecentLog` 是快照的**只读拼串**，每次刷新都从 `BgiLogCenter.Snapshot()` 的尾窗重新拼，不消费、不移动游标：`AccountStateCenter.cs:751-779`（`st.RecentLog = BuildTailText(snap)`）。
- 真正"只读一次"的是**文件字节游标** `Entry.Cursor`（`BgiLogCenter.cs:49`、`Pump` 在 `BgiLogCenter.cs:563-665`，`Cursor = end` 在 `:642`），但那是"文件读到哪"，与"触点每次读什么"无关。触点读的是尾窗快照，尾窗是滚动窗口，不是一次性消耗品。

**判定**：尾窗是快照型，**不会"读一次就没了"**。所以"读第二次是空"这个假设**不成立**。真正的过滤发生在**时间窗**（疑点 4），不是数据被消费掉。

### 疑点 4：Trigger 是电平还是边沿？——**电平 + 时间窗，不是边沿**

- `TrigLogMatch`/`CondLogContains`/`CondLogMissing` 统一走 `FlowRunner.cs:317-328`：每次扫描都从 `state.RecentLog` 取**当前尾窗全文**，调 `PlcLogMatcher.Contains(text, ...)` 逐行匹配，**没有任何"已消费/已上报"标记**。命中与否只取决于"尾窗里现在有没有满足条件的行"。
- `PlcLogMatcher.Scan`（`PlcLogMatcher.cs:37-50`）对每行做**时间戳过滤**：

  ```csharp
  if (at > now.AddSeconds(2) || at < now.AddSeconds(-Math.Max(1, seconds))) continue;
  ```

  即只认 `[now - seconds, now + 2s]` 窗口内的行。`TrigLogMatch` 默认 `seconds=10`（`FlowRunner.cs:324`、`FlowNodeCatalog.cs:81`）。

- 因此触点语义是：**"现在这一刻往前 `withinSeconds` 秒内，尾窗里存在匹配行" → 电平真；不存在 → 电平假**。它不是"出现过一次就永久边沿锁存"，也不是"只满足一次"。

- 但这与 `PlcScanEngine.cs:336-337` 的 `IsMomentary` 有微妙之处：日志触点被归为"瞬时信号"，`SatisfiedContacts` 会在命中时 `Add`（`PlcScanEngine.cs:274`），并在**本轮运行内**保持（`PlcScanEngine.cs:275`）。这把"电平"在**单轮内**加强成"本轮已见"——但无 END 每轮 `CompleteRun` 都会 `SatisfiedContacts.Clear()`（`FlowRunLifecycle.cs:43`），所以跨轮不会残留。

**判定**：日志触点是**电平 + 时间窗**语义。它是"持续满足"的（只要窗口内有匹配行，每一轮都满足），不是"只满足一次"。**bug 的真相**是：那条日志的时间戳一旦超出 `withinSeconds` 窗口，电平就掉回 false，动作才停止触发。用户误以为"只触发一次"，其实是"那条日志只出现了一次，且 10 秒后过期"。

### 疑点 5：调度是否被停？——**不会停**

- `ScheduleRunner` 每 1 秒 `Tick`（`ScheduleRunner.cs:76`、`TickHub.Register("ScheduleRunner", TickSeconds, Tick)` 在 `ScheduleRunner.cs:83`），`Tick` → `TickAutomations`（`ScheduleRunner.cs:195`）。
- `TickAutomations` 对每个 `flow.Enabled && !flow.Paused` 的流程**无条件** `Task.Run(FlowRunner.RunAsync(...))`（`ScheduleRunner.cs:129-157`）。无 END 流程完成一轮后没有任何状态会让它退出这个循环——`Enabled`/`Paused` 不变就永远每轮调度。
- `FlowRunner.RunAsync` 只有 `!manual` 时才校验结构（`FlowRunner.cs:54-58`），校验失败只影响"非法流程"，不影响正常流程。

**判定**：调度不会停。无 END 流程完成一轮后，下一轮照常进入 `ScanAsync`，再走 `CompleteRun` 清本轮状态、重新读条件。`PlcScanEngine.cs:236` 的文案"本轮已完成，等待条件重新满足后再次执行"就是这种循环语义的体现。

---

## 三、复现步骤（含最小复现测试）

### 3.1 最小复现测试（已跑通）

- 文件：`tools/layout-check/LogRetriggerCheck.cs` + `tools/layout-check/LogRetriggerCheck.csproj`
- 做法：构造 `TrigLogMatch(pattern="任务结束", withinSeconds=10) → ActRunAction` 的**无 END** 流程，`read` 委托真实走 `PlcLogMatcher.Contains`（与 `FlowRunner.ReadContactAsync` 日志分支同一逻辑），直接编译生产源码 `PlcScanEngine.cs`、`PlcLogMatcher.cs`、`FlowRunLifecycle.cs`。

- 运行结果（`dotnet run --project tools/layout-check/LogRetriggerCheck.csproj -c Release`）：

  ```
  ok   第1段日志命中，动作执行1次，无END不写日界锁存
  ok   同一行日志窗口内仍命中 → 无END循环可再次触发（排除引擎锁存卡死）
  ok   窗口过期后同一行日志不再命中（不触发）——这正是『只触发一次』的数据层原因
  ok   新日志行出现 → 再次触发（证明不是永久卡死，只是旧行过期）
  ok   无END完成一轮后 PlcPower/ActionResults/SatisfiedContacts/RunComplete 均已清空
  ALL PASSED
  ```

  该测试用五个断言把"引擎锁存卡死"和"数据层时间窗"两种可能**分离**：第 2 条证明引擎侧窗口内能重复触发（排除锁存），第 3 条证明时间窗过期后同一行不再命中（坐实数据层原因）。

### 3.2 在真实应用里人工复现

1. 新建一个**无 END** 流程：触点选「日志匹配」，pattern 填一个当天会出现的日志关键字（如"任务结束"），`最近多少秒` 用默认 10，连一个动作（如"模拟按键"）。
2. 等目标账号 BGI 写出匹配行 → 观察到动作执行一次，`LastRunAt` 更新。
3. **停止让 BGI 再写任何新日志**，静置 10 秒以上。
4. 观察：动作**不再**执行——即使界面数据中心页仍能看到那条日志原文（尾窗保留 800 行，见 `BgiLogCenter.cs:35`）。
5. 让 BGI **再写一条**新的匹配行 → 动作**立刻**再次执行。

预期：第 3-4 步"不执行"，第 5 步"恢复执行"。这正是"只能触发一次"的用户观感来源：**不是流程卡死，是旧日志过期、新日志没来**。

---

## 四、用户观感与"设计如此"的定性

**结论：用户看到的现象不是引擎 bug，是"日志匹配 = 最近 N 秒电平"这一设计的必然结果，但交互与文档严重误导。**

为什么用户会觉得是 bug：

1. **默认窗口只有 10 秒**（`FlowNodeCatalog.cs:81` 的 `P("withinSeconds","最近多少秒","10")`，`FlowRunner.cs:324` 的 fallback 也是 10）。用户配置后"过一会儿再看"，那条日志早已超过 10 秒窗口。
2. **`IsMomentary` 归类 + 注释措辞**（`PlcScanEngine.cs:336-337`、`FlowNodeCatalog.cs:80` "日志匹配：目标账号日志在时间窗内匹配关键字或正则"）没把"时间窗会过期"讲透。用户直觉是"日志出现过，就该触发"，而非"日志出现在最近 10 秒内才触发"。
3. **`CondLogMissing`（日志缺失）反而是"窗口内没出现就满足"**（`FlowRunner.cs:327` 的 `!hit`），这进一步暗示用户"日志匹配"是一个"出现即触发"的事件，而非持续电平。
4. 界面数据中心页仍展示尾窗原文（`DataCenterPage.xaml.cs:465` 用 `RecentLog` 匹配），用户能看到"日志明明还在"，却不知道为什么流程不再触发。

---

## 五、修复建议（按优先级，明确影响面）

> 本节是建议，不包含对业务代码的任何修改（本次审计只写文档 + 只读测试）。

### 建议 A（体验修正，低风险，强烈建议）：把「日志匹配」语义与默认值对齐用户预期

- **目标**：让"日志出现一次"能可靠触发，而不是被 10 秒窗口漏掉。
- **改法（择一，勿叠加）**：
  - **A1 调默认窗口**：`FlowNodeCatalog.cs:81` 把 `withinSeconds` 默认从 `10` 提到 `120`（与 `CondLogContains` 的默认 `120` 一致，见 `FlowNodeCatalog.cs:92`），同时 `FlowRunner.cs:324` 的 `TrigLogMatch` fallback 从 10 提到 120。**影响**：只影响新建节点的默认值，存量流程已落盘的 `withinSeconds` 不变；代价是匹配窗口变宽、误触发概率略升，但对"日志驱动"场景 120 秒远更实用。
  - **A2 把日志触点改成真正的"边沿锁存"**：命中后写一个持久化"已见"标记，直到该行滚出尾窗或手动复位。**影响**：语义变化大，需要新增状态字段 + 迁移 + 生命周期清空逻辑（`FlowRunLifecycle`），且会与"无 END 循环重复触发"的现有设计冲突（循环流程要重复触发，边沿锁存会阻止第二次），**不推荐**。
- **不能破坏**：有 END 流程"当日只跑一次"靠的是 `DailyLatchKey` 锁存（`PlcLogic.cs:76-79`、`PlcScanEngine.cs:66-71`），与 `withinSeconds` 无关。调窗口默认值**不影响**有 END 语义。

### 建议 B（文档/提示，零风险）：把"时间窗"语义写清楚

- 在 `FlowNodeCatalog.cs:80` 的 `TrigLogMatch` 描述、以及编辑器参数 hint 里，明确写"**仅在最近 N 秒内出现的日志会命中；超过 N 秒的旧日志不会再次触发**"。
- 对 `CondLogMissing`（`FlowNodeCatalog.cs:94`）同样补一句"窗口内未出现 = 满足"。
- 这样用户就不会把"日志出现过"误解为"永久触发条件"。

### 建议 C（可选，谨慎）：提供"日志出现一次即触发（边沿）"的独立触点类型

- 若确有"只要出现一次就触发、之后不重复"的真实需求，应**新增**一个触点类型（如 `TrigLogOnce`），而不是改动现有 `TrigLogMatch` 的电平语义。**影响**：需要新枚举 + 编辑器菜单 + `ReadContactAsync` 分支 + 状态持久化，工作量中等；但能同时保留"电平"（循环重复）与"边沿"（出现一次）两种清晰语义，是长期最干净的解法。

---

## 六、关键证据索引（file:line 速查）

| 疑点 | 结论 | 关键 file:line |
|---|---|---|
| 无 END 不锁存 | 确认 | `PlcScanEngine.cs:53-77`、`FlowRunLifecycle.cs:40-45`、`PlcLogic.cs:72-79` |
| 动作上升沿 | 确认（无 END 收尾复位） | `PlcScanEngine.cs:103,163`、`FlowRunLifecycle.cs:43-44` |
| 尾窗快照型 | 快照，不消费 | `BgiLogCenter.cs:98-99,789-801`、`AccountStateCenter.cs:751-779` |
| 日志触发电平+时间窗 | 电平，10 秒默认 | `FlowRunner.cs:317-328`、`PlcLogMatcher.cs:37-50`、`FlowNodeCatalog.cs:81` |
| 调度不停 | 每 1 秒无条件 | `ScheduleRunner.cs:76,83,129-157` |
| 尾窗保留 800 行 | 界面仍可见但触点不命中 | `BgiLogCenter.cs:35` |
| 有 END 当日只跑一次 | 靠 DailyLatchKey，与窗口无关 | `PlcScanEngine.cs:66-71`、`PlcLogic.cs:76-79` |
