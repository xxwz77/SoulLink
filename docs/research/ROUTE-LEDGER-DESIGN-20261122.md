# 配置组 / 路线级收益与报错台账（Route Ledger）

> 日期：2026-11-22
> 状态：最小可用版已实现（归因 + JSONL 落盘 + 今日 0 收益/失败查询 + 按组聚合）
> 关联代码：`Services/RouteLedger.cs`（新增）、`Services/BgiLogCenter.cs`（一处挂钩）

---

## 1. 背景与目标

用户原话：

> 「数据分析比如说 0 收益的路线啊，报错的路线啊，它不能快速分析出来，要在一个个配置组里面翻找。
> 我希望数据中心读日志的时候顺手把这事做了，然后存个文件啥的，让别的地方读取，我们也可以查看多天的记录。」

现有两个结构都满足不了：

| 组件 | 现状 | 缺什么 |
|---|---|---|
| `Services/DailyStatsCache.cs` | 账号级今日累计（Exp/Mora/Picks），纯内存，跨天清空 | 不按组归因、无历史 |
| `Services/ScriptGroupTracker.cs` | 配置组状态机（Running/Finished/Failed…），纯内存，跨天清空 | 无收益、不落盘 |

两者都没有把「这一组跑完赚了多少 / 为什么失败」关联成一条可跨天查询的记录。

---

## 2. 归因规则（已核实，非猜测）

### 2.1 日志样本核实

用 A1 账号 21 天真实日志（`D:\AAA挂机专区\A1\BetterGI\log\better-genshin-impact20261102~22.log`）
验证了两个事实：

**事实一：收益行和配置组行交错出现，收益行严格落在「开始执行」和「执行结束/未成功完成」之间。**

样本（20261114.log）：

```
[04:37:50.423] 狗粮路线获取摩拉: 19986          ← 归「狗粮批发」
[04:37:50.423] 狗粮路线获取狗粮经验: 63000
[04:37:50.424] JS 通知关闭，消息被拦截: 日期…   ← 必须排除的重复通知
...
配置组 "狗粮批发" 执行结束                       ← 收益在结束**之前**
配置组 "联机狗粮" 加载完成，共8个脚本，开始执行
```

**事实二：全量 21 天扫描，收益行 0 条落在组窗口之外。**

| 指标 | 数值 |
|---|---|
| 扫描天数 | 21 天（11102~11122）|
| 收益行总数（去重后）| 各天 0~4 条 |
| 落在「无打开组」窗口的收益 | **0** |

结论：**「最近一个 Started 且未 Ended 的组」就是可靠的归属锚点**，不存在需要猜测的
「收益落在两组之间」的歧义——真实日志里没有这种样本。若未来出现，按下面 2.3 的边界规则兜底。

### 2.2 归因伪代码

```
状态：openRun = null  （当前账号唯一「正在运行」的窗口，同一账号同刻只跑一组）

feed(chunk):                       // chunk = 增量日志文本（数据中心字节游标保证只喂新内容）
  对 chunk 内所有事件按出现位置排序（同一 chunk 内保证先后正确）：
    event 配置组 "G" 加载完成，共N个脚本，开始执行：
        if openRun != null 且 openRun.Group != G:
            emit(openRun, "Interrupted", "后续配置组已开始，未记录正常结束")
        openRun = { Group=G, StartedAt=本行时间戳, ScriptCount=N, Exp=0, Mora=0, Picks=0 }

    event 收益行（狗粮经验/摩拉/其他角色经验/摩拉）:
        if openRun != null: openRun.Exp += v 或 openRun.Mora += v

    event 拾取行:
        if openRun != null: openRun.Picks++

    event 配置组 "G" 执行结束:
        emit(openRun, "Finished", "", 本行时间戳); openRun = null

    event 配置组 "G" 未成功完成，结果: R:
        state = (R == "Cancelled") ? "Cancelled" : "Failed"
        emit(openRun, state, R, 本行时间戳); openRun = null

    event 取消执行配置组: "G":
        emit(openRun, "Cancelled", "Cancelled", 本行时间戳); openRun = null

emit(run, state, detail, endedAt):
    record = {
        Account, Day = endedAt 的 yyyy-MM-dd,
        Group, State, Start=StartedAt 的 HH:mm:ss.fff, End=endedAt 的 HH:mm:ss.fff,
        DurSec = End-Start 的秒, ScriptCount, Exp, Mora, Picks, Detail
    }
    enqueue(record)     // 只入队，文件写由后台线程做
```

### 2.3 边界情况处理

| 边界 | 规则 | 说明 |
|---|---|---|
| 收益行（异常地）落在两组之间 | 归到「最近已开始未结束」组；若无打开组则**丢弃** | 21 天实测 0 次；丢弃不误记比错记好 |
| 新组开始但旧组没收尾 | 旧组记为 **Interrupted** 落盘，收益照旧组累计值 | 与 `ScriptGroupTracker` 语义一致 |
| 同组连续重跑（旧窗口未收尾又 Start）| 旧窗口作废，**不**额外落 Interrupted（避免噪音），开新窗口 | 实测挖矿组会连续 Failed→重试 |
| 结束事件但没有对应 Start（重启后半途接手）| 记一条 0 收益 ghost 记录，不丢 | 重启重扫当天日志时会遇到 |
| 「未成功完成，结果: Cancelled」| 状态记为 Cancelled（而不是 Failed）| 与 tracker 的 RxFail 同源 |
| 伪组名（`The operation was canceled.` / 含 Exception）| 忽略 | 复用 `ScriptGroupTracker.ValidGroupName` 口径 |
| 重复通知「JS 通知关闭，消息被拦截: …」| 负向后顾排除，不重复计 | 与 `DailyStatsCache` 同源 |
| 跨午夜（如「等待至4-00」跨 00:00）| Day 取**结束事件**的日历日；Start 只存时间 | 一天一条记录只落一次，不拆两行 |

### 2.4 为什么用「开始/结束事件」而不是「时间窗差值」

收益行紧贴组内，但**真正的锚点是日志里明确的两行**：
- `配置组 "X" 加载完成…开始执行`（Start）
- `配置组 "X" 执行结束 / 未成功完成`（End）

这两行由 BGI 脚本引擎打印，边界清晰、零歧义；收益行夹在中间，天然按顺序归因。
「用最近 Started 未 Ended 的组」等价于「按这两行切窗口」，无需额外的时钟对齐。

---

## 3. 落盘格式与位置

### 3.1 为什么 JSONL（追加写）而不是整体 JSON

| 方案 | 问题 |
|---|---|
| 整体 JSON | 每次写都要读全量→反序列化→追加→重新序列化→整文件写回；文件越大越慢，崩溃可能整文件损坏 |
| **JSONL 追加写（采用）** | 一行一条；用 `FileMode.Append` 追加，写完 `Flush`；崩溃最多丢**最后一行**（已 Flush 的不丢）|

### 3.2 位置

```
<AppIdentity.DataDir>\ledger\<账号>\<yyyy-MM-dd>.jsonl
```

- `AppIdentity.DataDir` = 运行时数据目录（便携模式 `exe\data\`，否则 `%LOCALAPPDATA%\<身份>\`）。
- **绝对不在源码 `dist\` 下**（`dist\` 是发布产物，会被全量包覆盖）。
- 账号名做文件名净化（`Path.GetInvalidFileNameChars()` → `_`），防注入路径。

### 3.3 跨天切文件 / 重启续写 / 坏行容错

| 场景 | 处理 |
|---|---|
| 跨天切文件 | 记录写进 `Day = 结束事件日历日` 的文件；跨午夜那一组落到「结束那天的文件」，不拆行 |
| 重启续写 | 追加模式天然续写；但重启后数据中心会把当天日志**从头重扫一遍**（cursor=0），会再次产生同样的记录 → 写线程在首次写某文件前读回该文件已有的「自然键」集合，只追加没见过的新键，跨重启不重复 |
| 文件损坏容错 | 查询逐行 `JsonSerializer.Deserialize`，单行失败**跳过继续**，不崩；写入失败 catch 丢弃该条继续（不崩主程序）|

自然键 = `Group \u0001 State \u0001 Day \u0001 Start \u0001 End`（同一条运行记录唯一）。

---

## 4. 读写契约

| 角色 | 是谁 | 说明 |
|---|---|---|
| **唯一写入者** | `RouteLedger.Feed`（挂在 `BgiLogCenter.ProcessChunk`）| 数据中心读日志的增量路径，**不新增定时器** |
| 文件写 IO | `RouteLedger` 专用后台线程 `RouteLedger.Writer` | `Feed` 只入队；**UI 线程绝不做文件 IO** |
| **只读者** | 网页 / APP / 统计页 | 通过 `Query*` 读取 |

### 4.1 挂在哪里、为什么、开销

- 挂载点：`BgiLogCenter.ProcessChunk`，紧跟既有的
  `e.Status.Update(chunk)` / `e.Groups.Update(chunk)` / `DailyStatsCache.Accumulate(...)` 之后，一行
  `RouteLedger.Feed(e.UserName, chunk)`，外层 `try/catch` 包裹。
- 为什么这里：`ProcessChunk` 是数据中心**唯一**的增量解析出口，字节游标保证只喂新内容，
  天然满足「读日志的时候顺手做了」，无需新增轮询。
- 开销：`Feed` 对 chunk 跑 8 个已编译正则 + 内存归因，无任何磁盘 IO（写走队列）。
  数据量约 24 条记录/账号/天（16.6 结束 + 7.2 失败），正则匹配远小于既有 Status/Groups 的规模。
  实测 A1 满速写日志单遍扫描 130ms，新增 8 个正则的增量开销可忽略。
- 线程安全：`Feed` 内状态机用 `Lock` 保护；文件写单线程；读侧用 `FileShare.Read` 与写共存。

---

## 5. 查询能力（最小可用版已实现）

| 查询 | API | 说明 |
|---|---|---|
| 今日全部记录 | `RouteLedger.QueryToday(user)` | 新→旧 |
| **今日 0 收益 + 失败（重点排查）** | `RouteLedger.QueryTodayProblems(user)` | `ZeroReward \|\| IsFailure` 并集 |
| 近 N 天 0 收益组 | `RouteLedger.QueryZeroReward(user, N)` | |
| 近 N 天失败组 | `RouteLedger.QueryFailed(user, N)` | Failed/Cancelled/Interrupted |
| 近 N 天按组聚合 | `RouteLedger.GroupSummary(user, N)` | 平均 Exp/Mora、失败次数、总次数 |

**一次查询出结果**：`Query` 只扫目标账号目录下最近 N 个 `.jsonl` 文件
（每天约 24 条，7 天 ≈ 170 行），**不是**翻遍原始 BGI 日志。原始日志单日 40~66 MB，
台账单日仅 ~15 KB——查询从「扫几十 MB」降到「扫几 KB」。

> 网页只读入口 `GET /api/ledger?user=X&days=7` 未接（属「后续」），
> 但读侧 `RouteLedger.Query*` 已就绪，接网页/统计页只是加一个路由的事。

---

## 6. 磁盘增长估算与清理策略

### 6.1 实测事件量（A1，21 天）

| 指标 | 总量 | 日均 |
|---|---|---|
| 开始执行 | 1178 | 56.1（含挖矿重试循环）|
| 执行结束 | 349 | 16.6 |
| 未成功完成 | 152 | 7.2 |

**每天落盘记录数 ≈ 16.6 + 7.2 + 少量 Cancelled/Interrupted ≈ 24 条**。

### 6.2 体积估算

单条 JSONL 记录约 **400~600 字节**（含中文组名、Detail）。

| 范围 | 记录数 | 体积 |
|---|---|---|
| 单账号 / 天 | ~24 条 | ~12 KB |
| 单账号 / 年 | ~8800 条 | ~4.4 MB |
| 20 账号 / 年 | ~176k 条 | ~88 MB |

### 6.3 策略

- 默认**保留 90 天**（单账号 ~1.1 MB，20 账号 ~22 MB），由 `Query` 的上限 `days ≤ 365` 约束读范围。
- 清理：**后续**加一个 `RouteLedger.Prune(keepDays)`，在现有数据中心跨天清理点顺带调用
  （或由 `TickHub` 每日一拍调用一次，删 `*.jsonl` 中早于截止日期的文件）。**未在最小版实现**，
  因为一天几 KB 的体量，攒一年才几 MB，短期无压力。

---

## 7. 现状标注

### 【已实现】（本版）

- [x] 数据结构 `RouteLedgerRecord` + `RouteLedgerGroupSummary`
- [x] 归因规则（开始/结束窗口，21 天样本核实 0 脱窗收益）
- [x] JSONL 追加落盘（`AppIdentity.DataDir\ledger\`，写完 Flush）
- [x] 跨天切文件 / 重启续写去重 / 坏行跳过容错
- [x] 挂载现有增量解析路径 `ProcessChunk`，未新增定时器，未碰 UI 线程文件 IO
- [x] 今日 0 收益/失败查询 + 按组聚合查询入口
- [x] 磁盘估算与保留策略说明
- [x] `dotnet build` 0 error

### 【后续】（未实现，故意留白）

- [ ] 网页只读路由 `GET /api/ledger`（读侧 API 已就绪，加路由即可）
- [ ] 自动清理 `RouteLedger.Prune(keepDays=90)` 挂到每日节拍
- [ ] 多日预聚合索引（当单账号记录数增长到单次 `GroupSummary` 明显变慢时再上；
      当前 170 行/7 天的量级不需要）
- [ ] 耗时异常长的组 / 连续失败的组 这两个查询（当前 `Query*` 已能支撑，
      只是没有专门封装成单一方法；需要时在 `RouteLedger` 加两个谓词方法即可）

---

## 8. 改动清单

| 文件 | 改动 |
|---|---|
| `Services/RouteLedger.cs` | **新增**，台账全部逻辑 |
| `Services/BgiLogCenter.cs` | 在 `ProcessChunk` 增加一行 `RouteLedger.Feed(e.UserName, chunk)` |
| `docs/research/ROUTE-LEDGER-DESIGN-20261122.md` | 本文档 |
| `docs/research/README.md` | 登记本文档 |

未改：Version、未 commit/push、未发版、未动 `dist/`、未新建 `DispatcherTimer`、
未重构 `BgiLogCenter`、未改 `DailyStatsCache` / `ScriptGroupTracker`。

---

## 9. 未验证项

- **真实日志样本下的归因准确率**：归因规则已在 21 天真实样本上做「收益是否落在组窗口」
  的离线验证（0 脱窗），但**端到端跑起来后产出的 JSONL 行内容**尚未在真实挂机环境复核
  （需要在 A1 挂机一晚，比对台账行与实际收益是否一致）。
- 跨午夜那一组（「等待至4-00」）落盘到结束日的文件、以及重启去重，只有逻辑推演 + 单测级验证，
  未在真实环境跑过整晚。
