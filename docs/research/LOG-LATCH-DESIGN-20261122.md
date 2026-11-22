# 日志触点「事件锁存 + 消费」设计

> 设计/实现日期：2026-11-22
> 实现人：loglatch（agent-team 成员）
> 用户原话：「只要流程在运行，每出现一次，条件达到就记住，用完就清空。有end当天不再判断」
> 关联：`docs/research/AUTOMATION-BUG-20261122.md`（根因审计）

---

## 一、为什么时间窗方案不够（新旧语义对比）

### 旧语义：最近 N 秒电平

`TrigLogMatch` / `CondLogContains` 每轮扫描从尾窗 `RecentLog` 取全文，调
`PlcLogMatcher.Contains`，按**每行时间戳**过滤 `[now - withinSeconds, now + 2s]`：

```csharp
// PlcLogMatcher.cs（旧）
if (at > now.AddSeconds(2) || at < now.AddSeconds(-Math.Max(1, seconds))) continue;
```

于是「一条日志是否命中」取决于**现在这一刻它还在不在时间窗里**：

- 日志一写进来 → 窗口内命中 → 电平真；
- 窗口一过 → 同一行不再命中 → 电平假。

**为什么不够**：自动化流程 1 秒一轮，但流程可能正「在等待别的步骤」（AND 门控条件
未凑齐 / 通道未就绪 / 并行支路未走完）。一条日志出现时流程那轮没检查它（或检查了
但别的条件没满足，动作没执行），等其它条件凑齐时，那条日志**早就滚出时间窗**，条件
永远凑不齐 → 用户看到的「抓日志只能触发一次 / 再也不触发」。

把默认窗口从 10 秒提到 120 秒**只是治标**：它只是把「漏掉的概率」压小，没有改变
「时间窗一过就永远丢」的本质；窗口越大，误触发（拿旧日志冒充新信号）越严重。

### 新语义：事件锁存 + 消费

```
出现一次（新日志行命中）  →  记下一个「待处理事件」（不依赖时间窗，不会丢）
动作执行成功一次         →  消费掉这个事件（用完就清空，不重复触发）
有 END 且当天已完成      →  不再判断、不再锁存新事件（DailyLatchKey 保留）
```

| 维度 | 旧（电平 + 时间窗） | 新（事件锁存 + 消费） |
|---|---|---|
| 命中条件 | 窗口内存在匹配行 | 游标之后**新增**匹配行 |
| 窗口过了 | 条件永久丢失 | 事件已锁存，不会丢 |
| 动作执行后 | 电平持续 → 靠上升沿防重复 | 消费事件 → 无重复 |
| 等待其它条件 | 事件在窗口内过期 | 事件一直记得，直到被消费 |
| 同一条日志两次 | 第二条可能被去重/漏判 | 每次出现各记一个事件 → 执行两次 |
| 有 END 当天完成 | DailyLatchKey 锁存 | **完全不变** |

---

## 二、事件化范围：哪些触点改、哪些不改

### ✅ 事件化：`TrigLogMatch`（日志匹配）

**理由**：它是「触发型」触点（Trigger），用户心智就是「日志出现一次 = 触发一次」。
这正是用户报 Bug 的对象。改成事件锁存 + 消费，语义与用户原话完全一致。

### ❌ 保持电平：`CondLogContains`（日志包含）

**理由**：它是「条件型」触点（Condition），用在 AND/OR 组合里表达「**当前**日志状态
是否满足」。电平语义 = 「现在这一刻窗口内是否有匹配行」，是组合条件的正确读法；
一旦事件化，它会变成「曾经出现过就一直锁存」，破坏「条件持续满足才导通」的 PLC 语义。

> **TrigLogMatch vs CondLogContains 差异**：一个 Trigger、一个 Condition。
> 触发型 = 离散事件（出现即锁存、消费后清空）；条件型 = 连续电平（持续满足/不满足）。
> 用户的三句话针对的是「触发一次」的痛点，落在 Trigger 上，故只改 TrigLogMatch。

### ❌ 保持电平：`CondLogMissing`（日志缺失）

**理由**：它的语义是「窗口内**没**出现就满足」，与事件化**方向相反**。
「没出现」没有可锁存的离散事件（哪一轮该「记下缺失」？），只能靠时间窗电平查询。
强行事件化会造出「一旦缺失就永久锁存」的假阳性。**必须保持电平，本次未改动**。

### ❌ 保持电平：`CondLogHasError`（日志有报错）

**理由**：它判断的是「窗口内是否**存在**报错行」——是**状态**不是**事件**。
用户要的是「现在这段日志里有没有 ERR/失败/异常」（用于条件门控），
不是「每出现一条 ERR 就触发一次」。事件化会把它变成「报错计数触发器」，语义跑偏。
**保持电平**。

### 总结

| 触点 | 类型 | 语义 | 本次处理 |
|---|---|---|---|
| TrigLogMatch | Trigger | 离散事件 | ✅ 事件锁存 + 消费 |
| CondLogContains | Condition | 连续电平 | ❌ 保持 |
| CondLogMissing | Condition | 反向电平 | ❌ 保持 |
| CondLogHasError | Condition | 状态电平 | ❌ 保持 |

---

## 三、游标与事件表的数据结构

### 行序号（在 BgiLogCenter 里分配）

`BgiLogCenter` 是唯一读 BGI 日志的地方，每个账号的 `Entry` 增加一个**单调递增行序号**：

```csharp
// BgiLogCenter.Entry
public long NextSeq = 1;            // 下一个行序号
// PushTail 时：
e.Tail.Enqueue(new BgiLogLine(at, level, text, e.NextSeq++));
```

- **跨天滚动 / 文件重建不归零**（`ResetForNewFile` 保留 `NextSeq`），
  新文件首行接着上一行的序号 → 触点游标不会被「换文件」误判成回退。
- 快照 `BgiLogSnapshot` 新增 `LastSeq`（= `NextSeq - 1`）与每条 `BgiLogLine.Seq`。
- `AccountState` 新增 `LogTail`（`IReadOnlyList<BgiLogLine>`）、`LogTailSeq`、`LogTailFile`，
  由 `RefreshAccount` 从快照填充，供 `FlowRunner` 事件化触点使用。

> 为什么不能直接用 `RecentLog` 文本：它是拼接文本，环形队列把旧行挤掉后就无法判断
> 「哪行是新增」。序号 + 整行列表才允许「从游标扫到末尾」推进。

### 触点状态（在 AutomationFlow 上，内存态）

```csharp
// Models/FlowModels.cs
[JsonIgnore]
public Dictionary<string, LogTriggerState> LogTriggers = new(...);  // key = 触点节点 Id

public sealed class LogTriggerState
{
    public long Cursor;                 // 已扫描到的最大行序号（游标）
    public List<LogLatchEvent> Events;  // 待消费事件（可为空）
}
public sealed class LogLatchEvent
{
    public string Line;                 // 命中行内容（截断）
    public long Seq;                    // 该行序号
}
```

### 消费引擎（Services/LogLatch.cs）

```csharp
// 每轮扫描（FlowRunner 对 TrigLogMatch 调用）：
bool Scan(flow, contact, tail, tailSeq, now, mutable)
{
    // 1. 有 END 且当日已完成 → 直接 false（不再判断，满足用户第三条）
    // 2. 首次见到该触点 → 建状态，Cursor = tailSeq（不补历史）
    // 3. Events 非空 → true（事件已锁存，等待动作消费）
    // 4. 从 Cursor 之后扫新行，命中 → 记事件、Cursor=该行 Seq、返回 true
    //    无命中 → Cursor=tailSeq、返回 false
}

// 动作执行成功时（PlcScanEngine）：
void ConsumeUpstreamOf(flow, action)
{
    // 回溯该动作上游所有触点（递归穿越中间门控条件），
    // 只清掉其中 TrigLogMatch 的 Events（用完就清空）。
    // 失败不消费 → 事件保留，下一轮按 lastFailed 重试（与既有失败重试语义一致）。
}
```

---

## 四、重启行为（游标重建策略）

`LogTriggers` 是 **`[JsonIgnore]` 内存态**，不落盘。

- **进程内**：游标与事件常驻内存，只要流程在运行（进程活着），事件就不丢 —— 满足用户
  「只要流程在运行，每出现一次就记住」。
- **程序重启**：`LogTriggers` 为空 → 触点第一次扫描时 `Cursor = 当前尾窗末尾`（`tailSeq`），
  **不补历史**。即重启后从「现在」重新建立游标，重启前已经滚过去的历史日志不会被
  一股脑触发一遍 —— 这是任务给定的建议（避免重启触发一堆历史事件），也符合「运行时」语义。
- **跨天滚动 / 文件重建**：行序号不归零，游标天然延续；换文件首行序号接着上一行，
  不会重复也不会回退。
- **换业务日**（END 流程到日界自动重置）：`StartRun` 清空 `LogTriggers`，下一轮按新尾窗
  末尾重建游标，与「有 end 当天不再判断、次日重新开始」的既有生命周期一致。

---

## 五、配置兼容方案（`withinSeconds` 怎么办）

用户已有的 `withinSeconds` 参数（`TrigLogMatch` 默认 10，存量流程可能填了别的值）：

- **保留字段、不迁移配置**：`dist/data/config.json` 的 Automations **完全不用改**，
  旧流程继续能加载、能运行。
- **TrigLogMatch 的 `withinSeconds` 现在被忽略**（事件锁存不依赖时间窗），
  但**保留在参数列表里不删**（编辑器仍显示，用户不会看到「参数消失」的破坏性变化）。
  → 语义从「最近多少秒」变为「历史遗留字段，当前触点不依赖时间窗」。已在
  `FlowNodeCatalog.cs` 更新触点描述说明，避免误导。
- **CondLogContains / CondLogMissing / CondLogHasError 继续使用 `withinSeconds`**：
  它们保持电平，时间窗仍是其语义的一部分，参数照常生效。

> 不做「把 withinSeconds 当事件有效期」：那会重新引入时间窗过期丢事件的毛病，
> 与用户「不依赖时间窗」的原话冲突。保留字段仅为兼容，不赋予新含义。

---

## 六、测试用例清单与结果

测试文件：`tools/layout-check/LogLatchCheck.cs` + `LogLatchCheck.csproj`
（直接编译生产源码 `PlcScanEngine` + `LogLatch` + `PlcLogMatcher` + `FlowRunLifecycle`，
与 automation-auditor 的 `LogRetriggerCheck` 同一模式，不复刻匹配逻辑）。

| # | 用例 | 结果 |
|---|---|---|
| 1 | 日志出现一次 → 动作执行一次 | ok |
| 2 | 日志出现一次、中间隔 50 轮（等 AND 门控条件）→ 仍记得，最终执行 | ok |
| 3 | 动作执行后再扫（无新日志）→ 不重复执行（消费确认） | ok |
| 4 | 同一条日志出现两次 → 执行两次 | ok |
| 5 | 有 END 当天完成后 → 新日志不再触发（日界锁存未被破坏） | ok |
| 6 | CondLogMissing 仍保持电平语义（窗口内出现=不满足 / 未出现=满足） | ok |

运行：`dotnet run --project tools/layout-check/LogLatchCheck.csproj -c Release`
→ **ALL PASSED**

### 回归

- `PlcLogMatcherCheck`（27 断言）→ **ALL PASSED**（电平查询逻辑未动）。
- `LogRetriggerCheck` → **ALL PASSED**（旧实现根因复现测试仍通过，证明旧行为被准确记录）。
- `PlcScanCheck` → **4 个 FAILURE，均为既有的、与本改动无关的失败**：
  1. `可手动设为今日不运行，且不再执行`、`条件直达 END 无普通动作也完成并锁存`
     —— 测试用 `IsDailyComplete`（对 `DateTime.Now`=2026-11-22）断言 2026-11-18 硬编码的
     `DailyLatchKey`，跨日敏感，非本改动引入；
  2. `失败不盲重试且不提前 END`、`失败输入断开后允许重试`
     —— 测试断言旧的「失败不重试」语义，而生产代码在 2.17.24 已改为「失败每轮重试」
     （`lastFailed`），测试预期落后于生产，非本改动引入。
  > 这 4 个失败全部不涉及 `TrigLogMatch` / `LogLatch` / `ConsumeUpstreamOf`；
  > 我的改动对非日志触点是无操作（`ConsumeUpstreamOf` 找不到日志触点即空转），
  > 因此结论可证：**非本改动导致**。未在本次任务中修这些遗留测试（超出任务范围）。

---

## 七、改动文件清单

- `Services/BgiLogSnapshot.cs`：`BgiLogLine` 加 `Seq`；`BgiLogSnapshot` 加 `LastSeq`。
- `Services/BgiLogCenter.cs`：`Entry.NextSeq`；`PushTail` 分配序号；`ResetForNewFile` 不归零；`Build` 填 `LastSeq`。
- `Services/AccountStateCenter.cs`：`AccountState` 加 `LogTail/LogTailSeq/LogTailFile`；`RefreshAccount` 填充、BGI 停止时清空。
- `Models/FlowModels.cs`：`AutomationFlow.LogTriggers`（`[JsonIgnore]`）；新增 `LogTriggerState`/`LogLatchEvent`/`LogLineRef`；`EnsurePlcModel` 初始化。
- `Services/PlcLogMatcher.cs`：新增 `MatchText`（单行命中，不查时间戳）。
- `Services/LogLatch.cs`：**新增**，事件锁存 + 消费引擎。
- `Services/FlowRunner.cs`：`ReadContactAsync` 加 `mutable`；`TrigLogMatch` 走 `LogLatch.Scan`（含当日完成守卫）；其余日志触点保持电平。
- `Services/PlcScanEngine.cs`：动作成功后 `LogLatch.ConsumeUpstreamOf`。
- `Services/FlowRunLifecycle.cs`：`StartRun` 清 `LogTriggers`。
- `Models/FlowNodeCatalog.cs`：`TrigLogMatch` 描述更新（说明新语义，保留 `withinSeconds` 字段）。
- `tools/layout-check/LogLatchCheck.cs` + `LogLatchCheck.csproj`：**新增测试**。
- `tools/layout-check/PlcScanCheck.csproj`、`LogRetriggerCheck.csproj`：补编译 `LogLatch.cs`。

---

## 八、未验证项（如实说明）

- **未在真实 BGI 挂机环境端到端验证**：测试用合成日志流驱动 `LogLatch` + `PlcScanEngine`，
  未连真实 `BgiLogCenter` 的 watcher/文件流跑一遍（无 A1/A4 授权，且不碰生产 config）。
- **未验证 `FlowPreview` 只读预览路径在真实 UI 上的表现**：`mutable=false` 时
  `TrigLogMatch` 只按当前尾窗报实时电平（不锁存），逻辑已实现但未在编辑器里点验。
- **PlcScanCheck 的 4 个遗留失败**：已确认与本改动无关，但未修复（超范围）。

## 九、结论

- `TrigLogMatch` 已从「最近 N 秒电平」改为「事件锁存 + 消费」，满足用户三句话；
- `CondLogContains` / `CondLogMissing` / `CondLogHasError` 保持电平，理由见第二节；
- 有 END 当日锁存（`DailyLatchKey`）完全未破坏，测试 5 验证通过；
- `dotnet build SoulLink.csproj` **0 error**；
- `LogLatchCheck` **ALL PASSED**。
