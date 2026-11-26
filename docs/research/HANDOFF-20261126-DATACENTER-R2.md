# SoulLink 数据中心 — 交接文档（2026-11-26 第二轮）

> 日期：2026-11-26 14:00
> 版本：v3.3.49（`dist\SoulLink.exe`，ProductVersion `3.3.49+faeb8ef`）
> 状态：**可交接 / 数据已修正 / 全部质量指标归零**
> 上一份交接：[HANDOFF-20261126-DATACENTER.md](HANDOFF-20261126-DATACENTER.md)（上午版，**其中「零串台」结论有误，见 §1**）

---

## 0. 一句话现状

上午那份交接宣称「数据已修正、零串台」，但**实际库里还有大量错乱残留**，且**最终修复从未真正运行过**。
本轮补了 **11 个提交**，把「错乱残留 → 根因 → 修复 → 清理 → 验证」全部闭环，并新增**轮次记录**能力。

当前实测（14:00）：

| 指标 | 结果 |
|---|---|
| 负时长（run / route） | **0 / 0** |
| 跨文件错乱（pick/route/err/raw） | **0 / 0 / 0 / 0** |
| ghost 假记录 | **0** |
| 重复行（pick / raw） | **0 / 0** |
| 7 天 run 分布 | 37 / 28 / 42 / 29 / 33 / 49 / 20（11-20 ~ 11-26） |

---

## 1. ⚠️ 上午交接文档的三处错误（必读）

### 1.1 「零串台」是错的

上午文档 §0 称「零串台」，§13 checklist 打勾「数据质量已验证」。
**实测不符**：库里残留跨文件错乱 **pick 102 / route 43 / err 18 / raw 9609** 条。

排查脚本（按「`src_file` 文件名日期 vs `ts_ms` 日期」逐表比对）见 §9.1。上午的 SQL 只查了「route 跨越多天」，**覆盖不到这类横向错乱**。

### 1.2 最终修复根本没在跑

`dist\SoulLink.exe` 的 ProductVersion 是 `+7f1ab57`，而串台的最终修复是 **`2c0ab71`**（比 exe 构建时间晚 3 分钟）。
即：**git 里有修复，但二进制跑的仍是旧代码**，t76/t77 从未生效。

### 1.3 t76 漏了 5 处

`2c0ab71` 只加固了 `AccumPick` / `StartRoute`。实测还有 5 处写库/改内存路径绕过跨文件隔离：

| 位置 | 症状 |
|---|---|
| `StartRouteIdx` 跳过推断 | 回填时生成 `#skipped-N` 坏行（实测 30 条） |
| `EmitError` | err 表串台（实测 18 条） |
| `SetPlannedRoutes` | 历史文件的计划数改写今天 run |
| `AccumReward` ×2 调用点 | **参数没传 `srcFile`，隔离是死代码** |

---

## 2. 本轮修的 11 个问题

| # | Commit | 问题 | 修法 |
|---|---|---|---|
| 1 | `8cf1abc` | t76 漏 5 处跨文件隔离 | 统一用 `IsSameDayAsOpenRun` 拦住 |
| 2 | `cdf30b0` | 回填断点续传失效（重启从头重扫） | `ResumeFrom` 记录入队时 offset |
| 3 | `cdf30b0` | pick_event 重复（11426 组） | 唯一索引去掉 `chunk_offset` |
| 4 | `cdf30b0` | 背压让出后重读导致重复 | 背压检查**前置到 Feed 之前** |
| 5 | `cdf30b0` | 尾窗快照 800 行 | → **200 行**（用户要求） |
| 6 | `ad2ae09` | raw_event 稳定键（用户选方案1） | 保持原键，靠 1+3+4 根治源头 |
| 7 | `4478825` | Tracker 时间解析错 → 界面重复卡片 | `EventTime` 回溯上一行时间戳 |
| 8 | `30328fc` | 展开历史重复两遍 | 删掉重复的 `BuildGroupHistory` |
| 9 | `30328fc` | 今日 N 次却列 N+1 行 | 去重键改**按秒对齐** |
| 10 | `7bcb372` | **ghost 假记录**（今天只跑1次却显示2次） | ghost 的 start 必须落在源文件当天窗口 |
| 11 | `d73ad85` | **回填把今天正在跑的组误收尾** | `CloseOpenForBackfill` 只对历史文件调用 |

### 另：新增轮次能力（`48948e8`）

用户要求：「**有轮次要记录轮次，没轮次只记录配置组启动次数**」

- `run` 表新增 `round_index` / `round_total` / `round_host` / `round_count`（`EnsureColumn` 兼容老库）
- 解析两种锚点（实测每轮各出现一次，幂等）：

```
[第 1/4 轮 海带屁骨汤] "当前进度：开始第 1/78 条线路: ..."
[多世界] 第 1/4 轮，房主: "海带屁骨汤"，我是"成员"
```

- **一次配置组启动内跑多轮仍算「1 次运行」**（用户明确），只额外带轮次信息
- 界面：有轮次 → `今日 N 次启动 · M 轮`，展开每行带轮次+房主；无轮次 → `今日 N 次`
- 「好感」类如实记启动次数（日志里无 `[第 N/M 轮]`，只有 `已完成 N/10 次` 进度）

---

## 3. 三个「假数据」根因（本节最重要）

用户最不能接受的是**界面上出现根本不存在的记录**。本轮定位了三个源头：

### 3.1 未来时刻的 ghost（「11 点看到 14:00 的记录」）

```
11-26 14:28:34  state=Finished  backfill=1  src=''   detail='…缺开始事件，ghost'
```

**根因**：回填历史文件时天轴（`FileDayMs`）没按文件名校准，历史日志里的 `14:28:34` 被算成「今天 14:28:34」。
**铁证**：`14:28:34` 在今天日志里不存在，只存在于 11-22/23/24/25。

### 3.2 今天的组却记了历史的时刻（「今天只跑 1 次却显示 2 次」）

```
11-26 07:03:49  '联机锄地上线'  Finished  picks=0  backfill=1  src=''
```

**根因**：`BackfillCloseAsync` 查不到 open 行就造 ghost（`start=end=endMs`），**没校验 endMs 是否属于源文件那一天**。
07:03:49 是你每天固定的运行时刻，来自 11-20…11-25，被串成了「今天」。

### 3.3 今天正在跑的组被回填强行收尾

```
13:07:21  '联机锄地-小怪-队员'  Finished 13:48:09
detail='历史回填收尾（日志文件结束，未见结束锚点）'
```

而此刻它**仍在跑**（日志持续输出，进度 43/47 条线路）。
**根因**：回填把「读到当前 EOF」当成「文件结束」，但今天的日志还在实时增长。

**三者共同点：回填路径把「读到哪」当成「事实是什么」。修复原则统一为「宁缺勿假」。**

---

## 4. 清理记录（每次清理前都已备份）

| 时间 | 备份目录 | 清理内容 |
|---|---|---|
| 11:23 | `backup-20261126-112352` | 初次全量备份 |
| 12:47 | `backup-pre-clean2-20261126-124732` | 跨文件错乱 102+43+18+9609 / 重复 11426 / 未来 ghost 3 / 重算 picks 32 + route 2889 |
| 13:26 | `backup-pre-ghostclean-20261126-132605` | 全部 102 条 ghost（今日 14 条） |
| 13:47 | `backup-pre-roundrescan-20261126-134748` | 清今日 run 以便按新解析器重扫（轮次回填） |

> 备份都在 `dist\data\run-db\` 下。run.db 约 207 MB，**空间不足时可只留最新一份**。

---

## 5. 环境与进程（实测 14:00）

| 程序 | 版本 | 端口 | 说明 |
|---|---|---|---|
| `dist\SoulLink.exe` | **3.3.49+faeb8ef** | 19080 | 开发版，PID 20444 / 237MB / 采集中 |
| `dist\stable\SoulLink-Stable.exe` | v3.3.3.0 | 10800 | **用户生产版，绝对不能动** |
| `C:\Program Files\SoulLinkDev\SoulLink.exe` | v3.3.40 | — | 服务侧（SYSTEM），不要连 |

数据库：`run.db` 206.9 MB（WAL 9.8 MB）。表行数：

```
run 238 | route 7663 | pick_event 114824 | raw_event 137678 | err 1212
state_span 11 | day_span 7
```

---

## 6. 当前轮次实况（今日，实测）

| 时间 | 配置组 | 状态 | 轮次 | 房主 |
|---|---|---|---|---|
| 08:00:56~08:32:05 | 联机锄地-超级狗粮 | Finished | **4 轮**（4/4） | 澪号大猫猫 |
| 08:32:20~12:07:53 | 联机锄地-混合精英 | Failed | **3 轮**（3/4） | 火山老师 |
| 12:07:58~13:06:51 | 联机锄地-混合精英 | Finished | **2 轮**（2/2） | 澪号大猫猫 |
| 13:07:21~ | 联机锄地-小怪-队员 | Running | **1 轮**（1/4） | 海带屁骨汤 |

> 混合精英合计 **5 轮**（跨了 12:07 那次 Fail 重启，重启后 BGI 重新计数为 2/2）。
> 用户原判断「至少 4 轮」正确。

---

## 7. 关键代码位置

### 7.1 本轮改动文件

| 文件 | 改动 |
|---|---|
| `Services/RunRecordCollector.cs` | 5 处跨文件隔离补漏、轮次解析、重复 route 合并、ghost 窗口校验 |
| `Services/BgiLogCenter.cs` | 尾窗 200、断点续传 `ResumeFrom`、背压前置、回填不碰今天 |
| `Services/RunRecordStore.cs` | `round_*` 四字段、pick_event 唯一索引去 `chunk_offset` |
| `Services/ScriptGroupTracker.cs` | `EventTime` 回溯上一行时间戳 |
| `Views/DataCenterPage.xaml.cs` | 去重按秒对齐、删重复历史块、轮次展示 |

### 7.2 关键常量

```csharp
// BgiLogCenter.cs
private const int MaxBytesPerTick = 2 * 1024 * 1024;   // 实时跟随
private const int BackfillBytesPerTick = 1024 * 1024;  // 回填 1MB/tick
public const int TailMaxLines = 200;                   // ★ 本轮 800 → 200
public const long SessionBudgetMs = 5 * 60 * 1000;     // 回填预算 5 分钟
public const int MaxFailStreak = 3;                    // 回填失败熔断

// DataCenterPage.xaml.cs
private const int RebuildMinIntervalMs = 3000;         // 整页重建冷却
private const int SqliteQueryMinIntervalMs = 3000;     // 查库节流
```

---

## 8. 用户明确要求（不可违背）

### 8.1 回填语义（本次已确认）

> 「历史数据不存在，且存在日志的时候，进行数据回写。」

保留 **OR 判据**：**游标没读完 或 库里该天零记录**，且日志存在、不超 `BgiBackfillDays`（默认 7）。
两个判据各防不同的坑，**不能改成只看「库里没有」**（会导致中断那天的尾部数据永久丢失）。

### 8.2 轮次 vs 启动次数（本次新增，已确认）

> 「有轮次要记录轮次，没轮次只记录配置组启动次数。」
> 「算 1 次运行。」（一次启动内跑多轮仍算 1 次运行）
> 「好感锄地最大次数不固定，如实记录。」

### 8.3 数据真实性（本次反复强调）

> 「数据是假的错的，我今天只在 5:55 运行过联机锄地上线」

**宁缺勿假** —— 宁可少一条记录，也绝不造不存在的记录。

### 8.4 性能与隔离要求

- 回填后台平缓写入，不挤占实时线程，不引发卡顿
- 实时日志准确高效；快照 200 行足够
- **每一天的日志按日志文件绝对隔离**
- 跨天运行：前一天自动补结尾、当天自动补开头，不能丢配置组

---

## 9. 验证方法（可直接复跑）

### 9.1 数据质量总检

```python
import sqlite3, re
from datetime import datetime
db = r"E:\Documents\deepseek-harness\SoulLink\dist\data\run-db\run.db"
conn = sqlite3.connect(db); c = conn.cursor()

def fd(s):
    m = re.search(r'(\d{8})', s) if s else None
    return m.group(1) if m else None
def td(ms):
    return datetime.fromtimestamp(ms/1000).strftime('%Y%m%d') if ms else None

print("负时长 run:", c.execute("SELECT COUNT(*) FROM run WHERE end_at_ms<start_at_ms").fetchone()[0])
print("负时长 route:", c.execute("SELECT COUNT(*) FROM route WHERE end_at_ms<start_at_ms").fetchone()[0])
print("ghost:", c.execute("SELECT COUNT(*) FROM run WHERE src_file_start='' AND is_backfilled=1").fetchone()[0])

# ★ 关键：跨文件错乱（上午文档漏了这一项）
for t, tsc, idc in (("pick_event","ts_ms","pick_id"),("route","start_at_ms","route_id"),
                    ("err","ts_ms","err_id"),("raw_event","ts_ms","raw_id")):
    n = sum(1 for r in c.execute(f"SELECT {idc},{tsc},src_file FROM {t}").fetchall()
            if r[1] and td(r[1]) and fd(r[2]) and td(r[1])!=fd(r[2]))
    print(f"跨文件错乱 {t}:", n)

# 重复行
dup = c.execute("""SELECT COUNT(*) FROM (
  SELECT run_id,ts_ms,line_no,line_offset,kind FROM pick_event
  GROUP BY run_id,ts_ms,line_no,line_offset,kind HAVING COUNT(*)>1)""").fetchone()[0]
print("pick 重复组:", dup)
conn.close()
```

**期望全 0。**

### 9.2 轮次查看

```python
today = datetime(2026,11,26,0,0,0).timestamp()*1000
for r in c.execute("""SELECT group_name, start_at_ms, end_at_ms, state,
                             round_index, round_total, round_count, round_host
                      FROM run WHERE start_at_ms>=? ORDER BY start_at_ms""", (today,)).fetchall():
    print(r)
```

### 9.3 交叉核对日志（判断数据真假的唯一标准）

```powershell
# 某组的真实锚点（含回溯上一行时间戳）
$log = 'D:\AAA挂机专区\A1\BetterGI\log\better-genshin-impact20261126.log'
$lines = Get-Content $log
for ($i=0; $i -lt $lines.Count; $i++) {
  if ($lines[$i] -match '配置组 "联机锄地上线"') {
    $ts = ''
    for ($j=$i; $j -ge 0 -and $j -gt $i-4; $j--) { if ($lines[$j] -match '^\[(\d{2}:\d{2}:\d{2})') { $ts=$Matches[1]; break } }
    Write-Host ('L'+($i+1)+' '+$ts+' '+$lines[$i].Trim())
  }
}
```

> **铁律：数据库里的每一条 run，都必须能在当天日志里找到对应锚点。找不到就是假数据。**

---

## 10. 已知遗留 / 下一步

| # | 事项 | 说明 |
|---|---|---|
| 1 | **RDP 连接触发显示器黑屏** | 上午文档 §7.1 记录，本轮未处理 |
| 2 | 挂机版仍是 v3.3.3.0 | 用户要求先不动 |
| 3 | 「好感」轮次待实测 | 今天尚未运行；已确认它按「启动次数」记，等它跑起来验证 |
| 4 | 历史 ghost 已全清 | 若后续再出现，说明还有未覆盖的 ghost 路径 |
| 5 | `close-trace.log` 诊断残留 | `CloseRunLocked` 里的 StackTrace 诊断，稳定后可删 |

---

## 11. 协作红线（不变）

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

## 12. 经验教训（本轮新增）

### 12.1 「修复提交了」≠「修复生效了」

`2c0ab71` 提交后，二进制仍是旧版。**每次改完必须核对 exe 的 ProductVersion 与 git HEAD 一致**：

```powershell
(Get-Item 'dist\SoulLink.exe').VersionInfo.ProductVersion   # 应含最新 commit hash
git log --oneline -1
```

### 12.2 数据质量检查必须多维度

上午只查了「route 跨越多天」，漏掉了「src_file 与 ts 日期不一致」这类横向错乱。**复查要按表逐字段比对**。

### 12.3 「宁缺勿假」是数据中心的底线

用户对假数据的容忍度是零。任何「推断补全」都必须有日志依据，否则宁可不记。

### 12.4 回填与实时共用一个状态机，必须处处校验文件日期

同一条路径上，「关闭」「写入」「时钟」「错误归因」「轮次」五处都要 `IsSameDayAsOpenRun`。
**只修一处必然复发**（上午修了 3 处，本轮又发现 5 处）。

---

## 13. 交接 checklist

- [x] 代码全部提交（`git status` 干净）
- [x] 推送到云端 git
- [x] 数据质量全绿（负时长 0 / 跨文件错乱 0 / ghost 0 / 重复 0）
- [x] 轮次功能已实现并验证（4 组实测正确）
- [x] 三个「假数据」根因已修
- [x] 每次清理前均已备份
- [x] 挂机版未被触碰
- [ ] RDP 黑屏问题待解
- [ ] 挂机版待升级

---

> 本文档由 2026-11-26 第二轮会话整理。
> **如果只读一段，请读 §3「三个假数据根因」和 §12「经验教训」** —— 那是最容易复发的地方。
