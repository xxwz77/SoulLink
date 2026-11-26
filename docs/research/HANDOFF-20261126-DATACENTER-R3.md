# SoulLink 数据中心 — 交接文档（2026-11-26 第三轮）

> 日期：2026-11-26 15:00
> 版本：v3.3.49（`dist\SoulLink.exe`，ProductVersion `3.3.49+602cbcd`）
> 状态：**可交接 / 数据全绿 / 轮次与狗粮收益已上线**
> 前序文档：
> · [HANDOFF-20261126-DATACENTER.md](HANDOFF-20261126-DATACENTER.md)（上午版，**「零串台」结论有误**）
> · [HANDOFF-20261126-DATACENTER-R2.md](HANDOFF-20261126-DATACENTER-R2.md)（第二轮，修完 11 个错乱问题）

---

## 0. 一句话现状

在 R2 把「数据错乱」全部清零之后，本轮做了两件**新功能**：
**① 配置组轮次记录**（有轮次记轮次，无轮次记启动次数）；
**② 狗粮收益分解**（各组经验/摩拉分别显示 + 汇总）。

当前实测（15:00）：

| 指标 | 结果 |
|---|---|
| 负时长（run / route） | **0 / 0** |
| ghost 假记录 | **0** |
| 跨文件错乱 | **0**（R2 已清） |
| run / route / pick / raw / err | 238 / 7709 / 114962 / 138249 / 1212 |
| 主进程 | PID 52380 / 241 MB / handles 867 / owner lummy |

---

## 1. 本轮新增功能一：配置组轮次

### 1.1 用户要求（原话）

> 「有轮次要记录轮次。没轮次只记录配置组启动次数。」
> 「算 1 次运行。」（一次启动内跑多轮仍算 1 次运行）
> 「好感锄地最大次数不固定，如实记录。」

### 1.2 日志锚点（两种写法，实测每轮各出现一次，幂等）

```
[第 1/4 轮 海带屁骨汤] "当前进度：开始第 1/78 条线路: ..."
[多世界] 第 1/4 轮，房主: "海带屁骨汤"，我是"成员"
```

### 1.3 实现

`run` 表新增 4 列（`EnsureColumn` 兼容老库）：

| 列 | 含义 |
|---|---|
| `round_index` | 第几轮（N）；0 = 该组无轮次概念 |
| `round_total` | 共几轮（M） |
| `round_host` | 本轮房主名 |
| `round_count` | 本 run 内实际完成的轮次数 |

### 1.4 界面

- **有轮次** → `今日 N 次启动 · M 轮`，展开每行带轮次 + 房主
- **无轮次**（好感/CD采集/钓鱼等）→ `今日 N 次`（= 配置组启动次数）

### 1.5 今日实测

| 时间 | 配置组 | 状态 | 轮次 | 房主 |
|---|---|---|---|---|
| 08:00:56~08:32:05 | 联机锄地-超级狗粮 | Finished | **4 轮**（4/4） | 澪号大猫猫 |
| 08:32:20~12:07:53 | 联机锄地-混合精英 | Failed | **3 轮**（3/4） | 火山老师 |
| 12:07:58~13:06:51 | 联机锄地-混合精英 | Finished | **2 轮**（2/2） | 澪号大猫猫 |
| 13:07:21~ | 联机锄地-小怪-队员 | Running | **1 轮**（1/4） | 海带屁骨汤 |

> 混合精英合计 **5 轮**（跨了 12:07 那次 Fail 重启，重启后 BGI 重新计数为 2/2）。
> 用户原判断「至少 4 轮」正确。

### 1.6 「好感」为何按启动次数记

实测昨天日志，好感**没有** `[第 N/M 轮]` 标记，只有进度式输出：

```
好感任务开始，敌人类型: HilichurlBrigade
今日（凌晨4点起）HilichurlBrigade已完成 0/10 次
HilichurlBrigade好感运行总时长：7 分 24 秒
```

一次配置组启动内部循环刷 10 次怪，**不是轮次**。按用户要求「如实记录启动次数」。

---

## 2. 本轮新增功能二：狗粮收益分解

### 2.1 用户要求（原话）

> 「能增加联机狗粮分解出来的摩拉与经验吗？两个数据分别显示，然后汇总。
> 一共显示三行。也是事件驱动，不要轮询。
> 别人配置组名字可能跟我不一样，按日志格式来。主界面汇总显示总数。」

### 2.2 ★ 关键：同一天日志里有**两套格式**（必读）

这是本轮踩到的最大坑——只认一套会导致另一组恒为 0。

| 配置组 | 经验锚点 | 摩拉锚点 |
|---|---|---|
| **狗粮批发**（AAA-Artifacts-Bulk） | `狗粮路线获取狗粮经验: 61740` | `狗粮路线获取摩拉: 21786` |
| **联机狗粮** | `分解可获得经验: 86940` | `思收益：经验 未识别，摩拉 19400` |

> 狗粮批发还会额外输出一次 `分解可获得经验: 61740`（数值相同，冗余，无害）。

最终正则（同时认两套）：

```csharp
// 经验
(?:分解可获得经验|狗粮路线获取狗粮经验)\s*[:：]\s*(\d+)
// 摩拉
(?:狗粮路线获取摩拉|收益[：:]\s*经验\s*\S+\s*[，,]\s*摩拉)\s*[:：]?\s*(\d+)
```

### 2.3 ★ 取「最后一次」而不是累加（必读）

用户明确：**只记第二次的收益**，第一次是清空背包，不是本轮收益。

```
04:00:31  分解可获得经验: 356160   ← 清空背包（背包容量，不算收益）
04:37:33  分解可获得经验: 61740    ← 本轮真实收益 ✅
```

实现方式：`AccumDogfoodGain` 用**后写覆盖前写**，天然取最后一次。
**7 天实测数据全部符合此规律**，且与 AAA 脚本写进 `records/*.txt` 的值一致。

### 2.4 实现

`run` 表新增 2 列：`dogfood_exp` / `dogfood_mora`（`EnsureColumn` 兼容老库）。

采集层两个新正则 + `AccumDogfoodGain`，挂在既有日志链路上（`ScanAnchors`）：

```
FileSystemWatcher → 日志写入 → Pump/Feed → ScanAnchors → AccumDogfoodGain
```

**零新增定时器**，天然事件驱动。

### 2.5 界面分工

| 界面 | 显示 |
|---|---|
| **主界面**（运行状态卡） | `收尾A·联机 · 汇总 经验 +148,680 · 摩拉 +41,186` |
| **数据中心页狗粮分区** | 逐组三行表 + 合计 |

数据中心页表格：

```
组            经验        摩拉
狗粮批发      61,740      21,786
联机狗粮      86,940      19,400
合计         148,680      41,186
```

### 2.6 汇总去重（主界面那个「其他组」的坑）

第一版主界面显示：`经验 +61,740 · 其他组 经验 +148,680` —— **自相矛盾**，
因为 148,680 已经包含那个 61,740。

修法：主界面**只显示一个汇总**；`DogfoodOtherExp/Mora` 汇总时**排除 records 来源组**
（`DogfoodRecordReader.RecordsSourceGroup`，默认「狗粮批发」）。

> ⚠️ `RecordsSourceGroup` **仅用于汇总去重**。
> 采集层解析锚点**完全不依赖组名**（符合「别人组名可能不一样，按日志格式来」）。

---

## 3. 本轮新增功能三：狗粮 records 文件监听

### 3.1 问题

用户疑问「一轮是多久」后发现：界面显示 `读取：14:08:52` 在跳，
但数据停在 04:37（源文件 9.6 小时没动）—— **让人误以为在刷新却不更新**。

根因：`DogfoodReadAt`（读取时刻，每 30 秒更新）与数据真实年龄是两回事。

### 3.2 修法 A：界面区分两个时刻

新增 `AccountState.DogfoodFileWriteAt`（records 文件真实写入时刻），界面显示：

```
数据更新于 04:37:39（9.6 小时前）（源文件未更新）· 读取：14:20:37
```

一眼区分「程序卡住」与「源文件没变」。

### 3.3 修法 B：FileSystemWatcher

新增 `Services/DogfoodWatcher.cs`。**性能铁律沿用 `BgiLogCenter.EnsureWatcher` 的教训**：

| 做法 | 成本 |
|---|---|
| ❌ 事件里直接读文件+解析 | 每次 8ms，写入频繁时 CPU 25%（实测过） |
| ✅ **事件只置脏标记** | **纳秒级，可忽略** |
| ✅ 由 TickHub 消费（1 秒合并） | 最多每秒解析 1 次 |

**关键细节（踩过的坑）**：

1. TickHub 5 秒兜底拍用 `PeekDirty`（只读不清），
   清除留给 `RefreshOnce` 的 `ConsumeDirty` —— 否则标记被吃掉，那次刷新的重读条件失效。
2. 句柄生命周期完整收口：路径变化重建、账号移除 `StopFor`、`Stop()` 时 `StopAll`
   （本仓库踩过 `Entries.Remove` 未 Dispose 导致句柄泄漏）。

**实测验证**：

```
14:24:33  LISTEN A1 -> ...records\思.txt        ← watcher 挂上
14:25:00  改文件
14:25:00  DIRTY A1                              ← 同一秒触发
```

延迟从最多 **30 秒** → **<1 秒**。空闲时零开销（watcher 不触发就不做事）。

---

## 4. 日志链路本来就有 watcher（用户提问）

> 用户问：「日志有挂 watcher 吗」

**有，两层，且都有轮询兜底**：

| 位置 | 作用 | 触发后行为 |
|---|---|---|
| `BgiLogCenter.cs:320` | 数据中心采集跟今天日志 | 只置 `Dirty=1`，`ServiceTick` 每秒消费 |
| `BgiLogWatcher.cs:867` | 界面日志面板跟随 | 直接 Pump 读增量（`_busy` 重入保护 + 256KB 上限）|

兜底：`BgiLogCenter` 走 `TickHub` 每秒；`LogFollower` 有 `TickHub.RegisterBackground(..., 1, Pump)`。

---

## 5. 本轮踩的坑（交接必读）

### 5.1 清库重扫验证 = 看到残缺数据

改完解析器后，我清掉今日 run 让程序「重扫」，想验证新字段。
**结果数据全乱**：`联机狗粮` 变 ghost、狗粮收益记到 `CD采集-全采集` 头上。

**根因**：重扫走的是 R2 刚修好的**断点续传**（`ResumeFrom` 从持久化游标续读），
**跳过了 04:00~04:37 的历史内容** → 状态机看到的是残缺序列。

**教训**：
> 改解析器后**不能靠「清库重扫」验证** —— 断点续传会跳过已读部分。
> 正确做法：用独立脚本从**原始日志完整重算**，再与库对账。

已从 `backup-pre-dogfood-20261126-143831` 完整恢复。

### 5.2 多组共用解析器时，必须逐组验证

第一版只认了联机狗粮的格式 `收益：…摩拉 N`，
看到狗粮批发 `摩拉=0` 时**以为是那组本来没数据**，没深究 —— 用户追问才发现是双格式问题。

**教训**：
> 多组共用一套解析逻辑时，**必须逐组核对**，
> 不能因为一组的数对上了就认为逻辑正确。

### 5.3 汇总不能把明细的源头重复计入

主界面第一版：`经验 +61,740 · 其他组 经验 +148,680`（148,680 含 61,740）→ 自相矛盾。

**教训**：任何「明细 + 其他/合计」的组合，都要先确认两者**互斥**。

---

## 6. 版本与提交

本轮共 **11 个提交**（14:04 ~ 14:50）：

| Commit | 内容 |
|---|---|
| `277f908` | docs: 第二轮交接文档 R2 |
| `98b7f0c` / `fa37a1f` | 狗粮 records 挂 FileSystemWatcher + 界面区分读取/数据时刻 |
| `9fe0d76` | 狗粮收益分解（三行显示 + 汇总）|
| `d2b66df` | 狗粮摩拉**双格式**支持（修狗粮批发摩拉恒为 0）|
| `166878b` | 主界面只显示收尾A/B + 汇总（去掉自相矛盾的「其他组」）|
| 其余 | chore: 移除误提交的临时文件 + gitignore |

> ⚠️ 本轮多次把 `_msg*.txt` / `_tmp_*.py` 误提交进 git，已全部移除并加进 `.gitignore`。
> **下次提交前先 `git status --short` 确认**。

---

## 7. 环境与进程（实测 15:00）

| 程序 | 版本 | 说明 |
|---|---|---|
| `dist\SoulLink.exe` | **3.3.49+602cbcd** | 开发版，PID 52380 / 241MB / handles 867 |
| `dist\stable\SoulLink-Stable.exe` | v3.3.3.0 | **用户生产版，绝对不能动** |
| `C:\Program Files\SoulLinkDev\SoulLink.exe` | v3.3.40 | 服务侧（SYSTEM），不要连 |

数据库：`run.db` 206.9 MB（WAL 8.9 MB）。

---

## 8. ⚠️ 磁盘占用提醒

`dist\data\run-db\` 下有 **5 个备份，共约 1020 MB**：

| 备份目录 | 大小 |
|---|---|
| `backup-20261126-112352` | 192 MB |
| `backup-pre-clean2-20261126-124732` | 207 MB |
| `backup-pre-ghostclean-20261126-132605` | 207 MB |
| `backup-pre-roundrescan-20261126-134748` | 207 MB |
| `backup-pre-dogfood-20261126-143831` | 207 MB |

**建议只保留最新一份**（`backup-pre-dogfood-20261126-143831`），其余可删。

---

## 9. 关键代码位置

### 9.1 本轮涉及的文件

| 文件 | 负责 |
|---|---|
| `Services/RunRecordCollector.cs` | 日志锚点解析（轮次 + 狗粮收益）|
| `Services/RunRecordStore.cs` | `round_*` / `dogfood_*` 字段的 schema 与读写 |
| `Services/DogfoodWatcher.cs` | **新增**，records 文件 FileSystemWatcher |
| `Services/DogfoodRecordReader.cs` | records 解析 + `RecordsSourceGroup` 常量 |
| `Services/AccountStateCenter.cs` | 狗粮刷新（含 watcher 消费）+ `DogfoodFileWriteAt` |
| `Views/DataCenterPage.xaml.cs` | 狗粮分区三行表 + 汇总去重 |
| `Views/LaunchPage.xaml.cs` | 主界面「收尾A/B + 汇总」 |

### 9.2 新数据库列一览

| 列 | 类型 | 含义 |
|---|---|---|
| `round_index` | INTEGER | 第几轮（N）；0=无轮次 |
| `round_total` | INTEGER | 共几轮（M） |
| `round_host` | TEXT | 房主名 |
| `round_count` | INTEGER | 本 run 完成的轮次数 |
| `dogfood_exp` | INTEGER | 狗粮经验（取最后一次）|
| `dogfood_mora` | INTEGER | 狗粮摩拉（取最后一次）|

全部由 `EnsureColumn` 自动迁移，老库无需手工改。

---

## 10. 用户明确要求（累计，不可违背）

### 10.1 数据真实性

> 「数据是假的错的，我今天只在 5:55 运行过联机锄地上线」

**宁缺勿假** —— 宁可少一条记录，也绝不造不存在的记录。

### 10.2 轮次 vs 启动次数

> 「有轮次要记录轮次，没轮次只记录配置组启动次数。」
> 「算 1 次运行。」
> 「好感锄地最大次数不固定，如实记录。」

### 10.3 狗粮收益

> 「两个数据分别显示，然后汇总。一共显示三行。」
> 「也是事件驱动，不要轮询。」
> 「别人配置组名字可能跟我不一样，按日志格式来。」
> 「主界面汇总显示总数。」
> 「第一次收益就是我没订阅的文件记录的。我们只需要记录第二次的收益。」
> 「清空背包，避免满了，不是今日收益。」

### 10.4 回填语义（R2 已确认）

保留 **OR 判据**：**游标没读完 或 库里该天零记录**，且日志存在、不超 `BgiBackfillDays`（默认 7）。

### 10.5 性能与隔离

- 回填后台平缓写入，不挤占实时线程，不引发卡顿
- 实时日志准确高效；快照 200 行足够
- **每一天的日志按日志文件绝对隔离**
- 跨天运行：前一天自动补结尾、当天自动补开头，不能丢配置组

---

## 11. 验证方法

### 11.1 数据质量（期望全 0）

```python
import sqlite3
db = r"E:\Documents\deepseek-harness\SoulLink\dist\data\run-db\run.db"
conn = sqlite3.connect(db); c = conn.cursor()
print("负时长 run:", c.execute("SELECT COUNT(*) FROM run WHERE end_at_ms<start_at_ms").fetchone()[0])
print("负时长 route:", c.execute("SELECT COUNT(*) FROM route WHERE end_at_ms<start_at_ms").fetchone()[0])
print("ghost:", c.execute("SELECT COUNT(*) FROM run WHERE src_file_start='' AND is_backfilled=1").fetchone()[0])
conn.close()
```

### 11.2 轮次查看

```python
from datetime import datetime
day0 = datetime(2026,11,26,0,0,0).timestamp()*1000
for x in c.execute("""SELECT group_name, round_index, round_total, round_count, round_host
    FROM run WHERE start_at_ms>=? AND (round_total>0 OR round_count>0)""", (day0,)).fetchall():
    print(x)
```

### 11.3 狗粮收益查看

```python
for x in c.execute("""SELECT group_name, dogfood_exp, dogfood_mora FROM run
    WHERE start_at_ms>=? AND (dogfood_exp>0 OR dogfood_mora>0)""", (day0,)).fetchall():
    print(x)
```

**今日期望值**：

```
('狗粮批发', 61740, 21786)
('联机狗粮', 86940, 19400)
```

### 11.4 交叉核对日志（唯一判真标准）

```powershell
$log = 'D:\AAA挂机专区\A1\BetterGI\log\better-genshin-impact20261126.log'
# 狗粮锚点（两套格式）
Select-String -Path $log -Pattern '分解可获得经验|狗粮路线获取|收益[：:]'
```

> **铁律：数据库里的每一条记录，都必须能在当天日志里找到对应锚点。找不到就是假数据。**

---

## 12. 已知遗留 / 下一步

| # | 事项 | 说明 |
|---|---|---|
| 1 | **RDP 连接触发显示器黑屏** | 上午文档 §7.1，三轮均未处理，**唯一影响日常操作的问题** |
| 2 | 挂机版仍是 v3.3.3.0 | 用户要求先不动 |
| 3 | 「好感」轮次待实测 | 今天尚未运行；已确认按启动次数记 |
| 4 | **备份占 1 GB** | 见 §8，建议只留最新一份 |
| 5 | `close-trace.log` 诊断残留 | `CloseRunLocked` 里的 StackTrace 诊断，稳定后可删 |
| 6 | 狗粮收益历史回填 | 目前只回填了今天；历史各天可从旧日志重算（规律已验证 7 天）|

---

## 13. 协作红线（不变）

### 绝对不可触碰

| 对象 | 判据 |
|---|---|
| **A1 / A4 账号**（session 2 / 3） | 进程 owner 为 `A1` 或 `A4`，其中一切不动 |
| `dist\stable\SoulLink-Stable.exe` | 路径含 `stable`，无论哪个会话 |
| `D:\AAA挂机专区\A1\BetterGI\User\ScriptGroup\` | **只读**，连备份都不许写 |

### 停进程规则

- 只能**精确 PID**（`Stop-Process -Id <pid>`）
- **禁止** `taskkill /IM` 或 `Get-Process SoulLink* | Stop-Process`
- 停之前**必须核 owner**；读不出来按禁动处理

---

## 14. 交接 checklist

- [x] 代码全部提交并推送（`git status` 干净，`HEAD` = `origin/main`）
- [x] 数据质量全绿（负时长 0 / ghost 0）
- [x] 轮次功能已实现并验证（4 组实测正确）
- [x] 狗粮收益分解已实现并验证（三行 + 汇总，与 records/日志一致）
- [x] 狗粮 records 监听已实现并实测（延迟 30s → <1s）
- [x] 测试残留已清理（records 目录只剩原始 `思.txt`）
- [x] 挂机版未被触碰
- [ ] RDP 黑屏问题待解
- [ ] 备份 1 GB 待清理

---

> 本文档由 2026-11-26 第三轮会话整理。
> **如果只读一段，请读 §2.2「两套格式」和 §5「本轮踩的坑」** —— 那是最容易复发的地方。
