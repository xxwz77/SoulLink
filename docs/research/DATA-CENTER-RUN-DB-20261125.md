# 数据中心运行记录数据库：SQLite schema 与日志锚点契约

> 日期：2026-11-25
> 状态：技术契约（本文档是后续实现任务的唯一依据）
> 决策基线：用户本轮明确要求「数据库化」；推翻 docs/research/DATA-CENTER-SINGLE-SOURCE-20261123.md 里「暂缓 SQLite」的结论。
> 关联代码（只读核实，本任务不改任何 .cs）：
>   - Services/RouteLedger.cs（现有 JSONL 台账 + 归因状态机）
>   - Services/BgiLogCenter.cs（日志唯一读取者）
>   - Services/ScriptGroupTracker.cs、Services/DailyStatsCache.cs
>   - Services/AppIdentity.cs（DataDir）、Models/Models.cs（AppConfig/AppSettings）、Services/ConfigStore.cs
>   - Services/TickHub.cs、Services/WebServer.cs（/api/bgi/ledger）、Services/BgiLogWatcher.cs
>   - Server/Db.cs + Server/SoulLink.Server.csproj（现成 SQLite 用法参照）

---

## 0. 一句话结论

把「配置组运行台账」从 JSONL 升级为**单文件 SQLite**（`<AppIdentity.DataDir>\run-db\run.db`），
沿用 RouteLedger 的「Feed 只做内存归因 → 后台线程写盘」模型，新增三张表
（run 运行记录 / route 路线记录 / err 报错记录），跨天补全用「**持久化 open 行**」模型
（start 事件即 INSERT、end 事件即 UPDATE，运行中途的行 end_at_ms 为 NULL，天然跨文件/跨天/跨重启存活）。
/api/bgi/ledger 的返回结构不变，WebServer.cs 无需改签名。

---

## 1. 现状锚点核实（每个锚点都来自实际读过的代码 / 日志）

### 1.1 日志文件命名与滚动

- BGI 用 Serilog 写 `<程序目录>\log\better-genshin-impactYYYYMMDD.log`，逐条 flush，按天滚动
  （Services/BgiLogWatcher.cs:16-17 注释 + Services/BgiLogWatcher.cs:666-682 FindTodayLog）。
- 真实样本（只读）：`D:\AAA挂机专区\A4\BetterGI\log\better-genshin-impact20261124.log`（8115 行）
  与 `20261125.log`（1978 行）。实测 20261123.log 尾部 18:56「游戏已退出」正常收尾、次日 04:00 全新启动 ——
  说明正常跨天是「干净停止 + 次日重开」；跨天**续跑**（23:00 起、次日 1:00 止）是本文档必须覆盖的边界。

### 1.2 配置组（run）锚点（现有代码同源，两处）

| 锚点 | 正则（已存在于代码） | 来源 |
|---|---|---|
| 开始执行 | `配置组\s*"(?<g>[^"]+)"\s*加载完成[，,]\s*共\s*(?<n>\d+)\s*个脚本[，,]\s*开始执行` | RouteLedger.cs:102-104、ScriptGroupTracker.cs:70-71 |
| 执行结束 | `配置组\s*"(?<g>[^"]+)"\s*执行结束` | RouteLedger.cs:105-106、ScriptGroupTracker.cs:73-74 |
| 未成功完成 | `配置组\s*"(?<g>[^"]+)"\s*未成功完成[，,]?\s*结果\s*[:：]\s*(?<r>\S*)` | RouteLedger.cs:107-109、ScriptGroupTracker.cs:76-77 |
| 取消 | `取消执行配置组\s*[:：]\s*"(?<g>[^"]+)"` | RouteLedger.cs:110-111、ScriptGroupTracker.cs:79-80 |

真实日志样本核对（20261124.log）：

- L172 `配置组 "罐子激活" 加载完成，共3个脚本，开始执行` → Running，脚本数=3。
- L1943 `配置组 "罐子激活" 未成功完成，结果: Failed` → Failed（结果 token = Failed）。
- L2104 `配置组 "联机锄地-罐子" 加载完成，共1个脚本，开始执行`；L8083 `配置组 "联机锄地-罐子" 执行结束` → Finished。
- 伪组名过滤：`The operation was canceled.` / 含 `Exception` / `OperationCanceledException`
  （RouteLedger.cs:394-400、ScriptGroupTracker.cs:183-189 同口径）。

### 1.3 路线 / 脚本锚点（本文档新增，实测 20261124.log）

| 锚点 | 正则（新） | 实测 |
|---|---|---|
| 脚本开始 | `→\s*开始执行JS脚本\s*[:：]\s*"(?<name>[^"]+)"` | L190 `→ 开始执行JS脚本: "圣遗物调查点纯激活"` |
| 脚本结束（带耗时） | `→\s*脚本执行结束\s*[:：]\s*"(?<name>[^"]+)"\s*[,，]?\s*耗时\s*[:：]\s*(?<dur>[^，,]+)` | L8065 `→ 脚本执行结束: "锄地一条龙（联机）", 耗时: 18分20.948秒` |
| 路径组头 | `当前组\s+(?<path>\S+)\s+共\s+(?<n>\d+)\s*条路线` | L2425 `当前组 路径组1 共 5 条路线，精英0，小怪0，预计用时 0时5分0秒` |
| 开始遍历 | `\[DEBUG\]\s*开始遍历路线[，,]?\s*共\s*(?<n>\d+)\s*条` | L2431 `[DEBUG] 开始遍历路线，共 5 条，startIndex=0` |
| 路线开始 | `进入路线循环\s*[,，]?\s*count\s*=\s*(?<c>\d+)\s*[,，]?\s*route\s*=\s*"(?<name>[^"]+)"` | L2437 `[DEBUG] 进入路线循环，count=1，route="07【额外】至冬-旧基捷城西盗罐者6.json"` |
| 路线结束（无耗时） | `→\s*脚本执行结束\s*[:：]\s*"(?<name>[^"]+)"` 且该 name 等于某条已打开路线 | L2932 `→ 脚本执行结束: "07【额外】至冬-旧基捷城西盗罐者6.json"`（无「耗时」） |

关键区分：**脚本结束带「耗时: …」，路线结束不带**。路线名以 `.json` 结尾，且由「进入路线循环 route=…」打开、由无耗时的「脚本执行结束」关闭。
「联机锄地-罐子」1 个脚本内嵌套 5 条路线（07/08/09/10/99），路由表用 `kind` 区分 script/route，`parent_route_id` 记录嵌套。

### 1.4 拾取锚点（同源）

- `交互或拾取\s*[:：]\s*"(?<item>[^"]+)"`（RouteLedger.cs:120-121、DailyStatsCache.cs:92），排除 `item=="无有效结果"` 与空串。
- 实测 L2884 `交互或拾取："游医"`、L2887 `"幸运儿"`。注意任务描述里的「拾取：交互或拾取: …」**没有**前导「拾取：」，真实文本就是 `交互或拾取："物品"`。

### 1.5 报错锚点（本文档新增，实测 20261124.log L1759-1798）

- 错误头：一行以 `[04:15:19.683] [ERR] [Primary:...] <logger/category>` 开头，level=ERR（复用 BgiLogCenter.cs:193-195 RxTs 已捕获 level）。
- 错误体：头部行之后直到下一个 `^\[` 时间戳头之间的所有行，是**多行消息 + ClearScript 栈**（无时间戳的续行沿用上一时间戳）。
- 异常类型从累积文本里提取：`(?<type>[A-Za-z0-9_.]+Exception)`（实测 `Microsoft.ClearScript.ScriptEngineException`，L1770）或 `Error:\s*(?<msg>[^\r\n]+)`。
- 来源（source）= 头部行里的 logger/category，如 `BetterGenshinImpact.Core.Script.Dependence.Log`（L1759）或 `BetterGenshinImpact.Service.ScriptService`（L1794）。
- 消息（message）= 头部行之后第一条内容行（或 `执行脚本时发生异常: "…"` 行），截断 ≤500 字；**完整多行栈存 detail**。

实测错误块样例（L1759-1798）：

    [04:15:19.683] [ERR] ... BetterGenshinImpact.Core.Script.Dependence.Log
    脚本停止：线路 7至冬额外激活CD.json 终点校验失败，距离：1511.12
    [04:15:19.750] [DBG] ... ScriptService
    执行脚本时发生异常
    Microsoft.ClearScript.ScriptEngineException: Error: 激活脚本执行失败，详见上方日志与 records 记录
     ---> Microsoft.ClearScript.ScriptEngineException: ...
       at Microsoft.ClearScript.V8.…（多行栈）
    --- End of inner exception stack trace ---
    ...
    [04:15:19.773] [ERR] ... ScriptService
    执行脚本时发生异常: "Error: 激活脚本执行失败…"

### 1.6 时间戳与日期归属（关键）

- 日志行时间戳形如 `[04:00:22.869]`（**无日期**），日期来自文件名 `better-genshin-impactYYYYMMDD.log`。
- 现有代码用 `DateTime.Now` 当日期锚、超出 now+2s 则 -1 天（RouteLedger.cs:402-416 StampOf、BgiLogCenter.cs:820-830 TryStamp）——
  这在重启重扫时**不具确定性**（同一行在不同时刻扫可能落在不同日期）。
- 本文档改为：**日期 = 源文件名的 YYYYMMDD**（权威、确定、可重扫复现），行内显式日期覆盖文件名日期。
  epoch = `DateTimeOffset(local(yyyy,MM,dd,HH,mm,ss.fff)).ToUnixTimeMilliseconds()`。

---

## 2. 表结构 DDL

### 2.1 建库 + PRAGMA（照抄）

SQLite 用法参照现有 Server/Db.cs:20-62 的成熟模式（WAL + busy_timeout + synchronous=NORMAL），
但**主程序是单进程多线程，连接只在自己的写线程用，不搞连接池**：

```sql
-- 每次打开连接时执行（连接级，不随库持久化）
PRAGMA busy_timeout=5000;
-- 库级，建库时执行一次
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA foreign_keys=ON;
```

### 2.2 三张表

```sql
CREATE TABLE IF NOT EXISTS run (
  run_id         TEXT PRIMARY KEY,          -- 自然键：account 单位分隔符 group_name 单位分隔符 start_at_ms
  account        TEXT NOT NULL,             -- Windows 用户名（UserName，非 AccountId）
  group_name     TEXT NOT NULL,             -- 配置组名
  start_at_ms    INTEGER NOT NULL,          -- epoch ms（本地时区换算）
  end_at_ms      INTEGER,                   -- NULL = 运行中（open row，跨天补全的锚点）
  dur_ms         INTEGER NOT NULL DEFAULT 0,
  state          TEXT NOT NULL,             -- Running/Finished/Failed/Cancelled/Interrupted
  script_count   INTEGER NOT NULL DEFAULT 0,
  exp            INTEGER NOT NULL DEFAULT 0,-- 狗粮经验（兼容 /api/bgi/ledger）
  mora           INTEGER NOT NULL DEFAULT 0,-- 狗粮摩拉（兼容 /api/bgi/ledger）
  picks          INTEGER NOT NULL DEFAULT 0,-- 本次拾取总次数
  src_file_start TEXT NOT NULL DEFAULT '',  -- 开始事件所在文件 basename
  src_file_end   TEXT NOT NULL DEFAULT '',  -- 结束事件所在文件 basename
  start_day      TEXT NOT NULL DEFAULT '',  -- yyyy-MM-dd（start_at 本地日）
  end_day        TEXT NOT NULL DEFAULT '',  -- yyyy-MM-dd（end_at 本地日）
  is_cross_day   INTEGER NOT NULL DEFAULT 0,-- start_day != end_day
  is_backfilled  INTEGER NOT NULL DEFAULT 0,-- start 锚点继承自上一文件/持久化 open 行（非本文件内「加载完成」）
  detail         TEXT NOT NULL DEFAULT ''   -- 失败原因 token（Failed/Cancelled 原文）
);

CREATE INDEX IF NOT EXISTS ix_run_account_end ON run(account, end_at_ms DESC);
CREATE INDEX IF NOT EXISTS ix_run_group       ON run(group_name);
CREATE INDEX IF NOT EXISTS ix_run_end_day     ON run(end_day);
CREATE INDEX IF NOT EXISTS ix_run_open        ON run(account) WHERE end_at_ms IS NULL;

CREATE TABLE IF NOT EXISTS route (
  route_id       TEXT PRIMARY KEY,          -- 自然键：run_id 单位分隔符 kind 单位分隔符 name 单位分隔符 start_at_ms
  run_id         TEXT NOT NULL REFERENCES run(run_id) ON DELETE CASCADE,
  kind           TEXT NOT NULL,             -- 'script'（JS脚本） | 'route'（脚本内的路线文件）
  name           TEXT NOT NULL,
  parent_route_id TEXT,                     -- 路线所属脚本的 route_id（script 层为 NULL）
  start_at_ms    INTEGER NOT NULL,
  end_at_ms      INTEGER,
  dur_ms         INTEGER NOT NULL DEFAULT 0,
  state          TEXT NOT NULL,             -- Running/Finished/Failed/Interrupted
  pick_count     INTEGER NOT NULL DEFAULT 0,-- 该路线内「交互或拾取」次数
  pick_detail    TEXT NOT NULL DEFAULT '{}',-- JSON：{"物品名":次数}
  src_file       TEXT NOT NULL DEFAULT ''
);

CREATE INDEX IF NOT EXISTS ix_route_run ON route(run_id);

CREATE TABLE IF NOT EXISTS err (
  err_id         INTEGER PRIMARY KEY AUTOINCREMENT,
  run_id         TEXT NOT NULL REFERENCES run(run_id) ON DELETE CASCADE,
  route_id       TEXT,                      -- 可空：无打开路线时
  ts_ms          INTEGER NOT NULL,
  level          TEXT NOT NULL DEFAULT 'ERR',-- ERR / WRN
  exception_type TEXT NOT NULL DEFAULT '',  -- 如 ClearScript.ScriptEngineException
  message        TEXT NOT NULL DEFAULT '',  -- 首行消息，≤500 字
  detail         TEXT NOT NULL DEFAULT '',  -- 多行完整栈（原始，不去重）
  source         TEXT NOT NULL DEFAULT '',  -- logger/category
  content_hash   TEXT NOT NULL,             -- SHA-256(ts|level|detail) 前 16 hex
  src_file       TEXT NOT NULL DEFAULT ''
);

CREATE INDEX IF NOT EXISTS ix_err_run   ON err(run_id);
CREATE INDEX IF NOT EXISTS ix_err_route ON err(route_id);
CREATE INDEX IF NOT EXISTS ix_err_ts    ON err(ts_ms);
CREATE UNIQUE INDEX IF NOT EXISTS ux_err_dedup ON err(run_id, ts_ms, level, content_hash);
```

### 2.3 主键 / 去重自然键（重启重扫不重复）

- **run_id = account ␟ group_name ␟ start_at_ms**（确定性字符串主键，␟ 为 U+001F 单位分隔符）。同一账号同一组在同一起跑时刻只有一行。
- **route_id = run_id ␟ kind ␟ name ␟ start_at_ms**（确定性）。
- **err** 用自增 + 唯一索引 `(run_id, ts_ms, level, content_hash)` 去重，`content_hash` 由完整 detail 计算，保证同一条多行栈不重复入库。
- 用确定性主键（而非自增）的原因：状态机在内存里就能算出 id，**无需任何 DB 往返**即可把整行丢进写队列，
  写线程 `INSERT OR IGNORE` / `UPDATE ... WHERE end_at_ms IS NULL` 天然幂等，重启重扫（BgiLogCenter cursor=0）不重复。
- 现有 RouteLedger 的自然键是 `Group␟State␟Day␟Start␟End`（RouteLedger.cs:47-48，代码里实际用 U+0001 连接），
  它把「日 + 时刻」分开存导致跨午夜 Start 有歧义；本文档用**完整 epoch ms** 消除歧义。

---

## 3. 归因状态机（Feed 只做内存归因，写 IO 交给后台线程）

### 3.1 状态（内存，复用 RouteLedger.OpenRuns 的扩展，RouteLedger.cs:127-135）

    OpenRun : { group, start_at_ms, run_id, script_count, src_file,
                exp, mora, picks, open_route }        // open_route = 当前路线窗口
    OpenRuns : Dictionary<account, OpenRun>           // 同一账号同刻只跑一组

### 3.2 事件处理伪代码（替换 RouteLedger.Feed 的归因逻辑，RouteLedger.cs:175-241）

```
feed(userName, chunk, srcFile, day):        // chunk=增量文本；srcFile=文件名；day=文件名日期
  events = 收集本 chunk 内全部锚点匹配，按 position 排序   // 同 RouteLedger.cs:182-239
  lock(StateLock):
    for ev in events:
      case 开始执行(G, N):
        if OpenRuns[user] 存在且 group != G:
            close(OpenRuns[user], "Interrupted", "后续配置组已开始，未记录正常结束")
        else if OpenRuns[user] 存在且 group == G:
            // 同组重跑未收尾：旧窗口作废，不落 Interrupted（同 RouteLedger.cs:270-274）
            丢弃旧内存窗口（但保留其已持久化的 open 行——见 §3.4）
        run_id = user + U+001F + G + U+001F + start_ms
        OpenRuns[user] = OpenRun{ group=G, start_at_ms=start_ms, run_id, script_count=N, src_file }
        enqueue( INSERT OR IGNORE run(run_id, account, group_name, start_at_ms,
                                      state='Running', script_count=N, src_file_start, start_day=day) )
      case 执行结束(G) / 未成功完成(G, R) / 取消(G):
        if OpenRuns[user] 匹配 group==G:
            state = Finished / (R=='Cancelled' ? Cancelled : Failed) / Cancelled
            close(OpenRuns[user], state, R)
        else:
            // 没有内存窗口却有结束事件（重启后半途接手 / 首捕）→ 走跨天补全探针（见 §4）
            backfill_and_close(user, G, state, R, end_ms, srcFile, day)
      case 脚本开始(name):   打开 route 窗口(kind='script')，parent=NULL
      case 路线开始(name):   打开 route 窗口(kind='route')，parent=当前 script
      case 脚本结束(name,dur):  关闭 script 窗口（若仍开着 route，先 Interrupted 关闭）
      case 路线结束(name):      关闭 route 窗口
      case 拾取(item):
        if 打开 route 窗口:  route.pick_count++, route.pick_detail[item]++
        OpenRun.picks++
      case 收益(exp/mora):   OpenRun.exp += v / OpenRun.mora += v
      case 错误头([ERR] level):
        累积 detail（头部行后所有续行直到下一个时间戳头）
        route_id = 当前打开的 route 或 script 的 route_id（无则 NULL）
        enqueue( INSERT OR IGNORE err(...) )
        if 打开的 route 窗口: 标记 route.state='Failed'（见 3.3）

close(run, state, detail):
  enqueue( UPDATE run SET end_at_ms, dur_ms, state, detail,
           src_file_end, end_day, is_cross_day
           WHERE run_id=? AND end_at_ms IS NULL )
  // 同时 Interrupted 关闭所有仍开着的 route 窗口
  移除 OpenRuns[user]
```

### 3.3 路线状态语义

- 路线结束且窗口内出现过 `[ERR]`（或 `执行失败`/终点校验失败/激活脚本执行失败）→ Failed；否则 Finished。
- 被后续路线/脚本顶掉而无结束 → Interrupted。
- 父配置组结束/中断时仍开着的路线 → Interrupted。

### 3.4 同组重跑与 open 行

RouteLedger.cs:270-274 对「同组连续重跑未收尾」只作废内存窗口、不额外落 Interrupted。DB 下对应规则：
- start 事件 `INSERT OR IGNORE` 不会覆盖已存在的旧 open 行（run_id 含 start_at_ms，不同起跑时刻 → 不同 run_id）。
- 旧 open 行（上一轮未收尾）由后续 `close` 时机兜底：若下一事件是「执行结束」且 end_at 匹配的是**最新**窗口，
  旧 open 行仍停在 end_at IS NULL —— 交给 §6 的 stale-open 清理（跨天/小时级）统一标 Interrupted。
  这与现状一致（现状旧窗口直接丢弃，同样不落盘），但 DB 至少不丢 start 事实。

---

## 4. 跨天补全规则（具体阈值，不含糊）

### 4.1 核心模型：持久化 open 行 = 跨天锚点

run 表的 open 行（`end_at_ms IS NULL`）就是「还没收尾的配置组」的**持久化锚点**。
它由 start 事件 INSERT、end 事件 UPDATE，**天然跨文件 / 跨天 / 跨重启存活**（SQLite 落盘，不随 BgiLogCenter.ResetForNewFile 清空——
BgiLogCenter.cs:131-152 的 ResetForNewFile 只清 Entry 内字段，本状态机是独立静态表，不受影响）。

### 4.2 启动重建 open 窗口

进程启动（首次使用前）执行一次：

    SELECT run_id, group_name, start_at_ms, script_count, src_file_start
      FROM run WHERE end_at_ms IS NULL
    → 灌入内存 OpenRuns（同时恢复 route/err 的归因目标）

这等价于 RouteLedger 现有 `WrittenKeys` 的「读回已有」思路（RouteLedger.cs:335-369），但读的是 DB open 行。

### 4.3 判定条件（三个具体阈值）

| 项 | 阈值 | 依据 / 说明 |
|---|---|---|
| 时间连续性阈值 | **5 分钟（300 秒）** | 新文件首条带时间戳行 − open 行的「最近归因时刻」≤ 300s → 视为同一运行续跑；> 300s → 判陈旧中断 |
| 粘性组名 | **open 行的 group_name 是权威**，不因新文件无「加载完成」而丢名 | 与 BgiLogCenter.Entry.CurrentGroup 的粘性语义一致（BgiLogCenter.cs:78-90、131-152 注释：跨天滚动不清空 CurrentGroup）；但「停止」会清空（BgiLogCenter.cs:184-191 RxStopped + L733-741） |
| Interrupted 兜底 | 新文件首条时间 − open 行最近归因时刻 > 300s，且该 open 行无收尾事件 → close(state='Interrupted', end=open 行最近归因时刻) | 覆盖「程序/机器跨天期间停机，BGI 也被杀，运行实际已中断」的场景 |

### 4.4 判定伪代码（新文件首扫 / 检测到文件滚动时）

```
on_new_file(account, newFile, first_ts):
  open = OpenRuns[account]
  if open != null:
    gap = first_ts - open.last_event_ms
    if gap <= 300_000:
        keep(open)                    # 续跑：新文件无「加载完成」也不改名/改时
    else:
        close(open, "Interrupted", "跨天时间断档，未正常结束")   # 陈旧 open 兜底
        OpenRuns.Remove(account)

# 新文件里有结束事件但内存无窗口（重启/首捕场景）：
backfill_and_close(account, G, state, detail, end_ms, file, day):
  row = SELECT ... FROM run WHERE account=? AND group_name=? AND end_at_ms IS NULL
  if row != null:
      # 直接 UPDATE 这个 open 行（继承 start_at_ms），is_backfilled=1
      UPDATE run SET end_at_ms=end_ms, dur_ms, state, detail, src_file_end=file,
             end_day=day, is_cross_day=(start_day!=day), is_backfilled=1
             WHERE run_id=row.run_id AND end_at_ms IS NULL
  else:
      # 无任何 open 行（DB 被清/历史首捕）：退化为 ghost（start=end, dur=0, is_backfilled=1, detail 标注「缺开始」）
      INSERT run(run_id=..., start_at_ms=end_ms, end_at_ms=end_ms, state, is_backfilled=1, ...)
```

### 4.5 「23:00 起跑、次日 1:00 结束」——落几条、字段是什么（直接答案）

**落 1 条 run 记录**（不切分、不合并成两行）：

| 字段 | 值 |
|---|---|
| run_id | `A4 ␟ 联机锄地-罐子 ␟ <day1 23:00 的 epoch ms>` |
| account / group_name | A4 / 联机锄地-罐子 |
| start_at_ms / end_at_ms | day1 23:00:00.000 / day2 01:00:00.000 |
| dur_ms | 7,200,000（2 小时） |
| state | Finished（或 Failed/Cancelled，取决于收尾事件） |
| src_file_start / src_file_end | better-genshin-impact20261124.log / better-genshin-impact20261125.log |
| start_day / end_day | 2026-11-24 / 2026-11-25 |
| is_cross_day / is_backfilled | 1 / **0**（正常续跑，start 在本文件内见过） |

若**中途重启**（00:30 重启，重扫 20261125.log，该文件内无「加载完成」）：
**仍落 1 条**，唯一变化是 `is_backfilled = 1`（start 锚点来自持久化 open 行而非新文件内 start 事件），其余字段不变。

配套的 route / err 行各自带**完整 epoch ms**，按自身时间戳落在正确的日历日，不需要按天拆 run。

---

## 5. 集成点

### 5.1 写线程 / 队列（沿用 RouteLedger 后台写线程模型）

- **Feed 只做内存归因**（regex 匹配 + 状态机 + 算 run_id/route_id），把成品行丢进
  `BlockingCollection<DbOp>`；**禁止在 ProcessChunk/UI 线程做任何 SQLite IO**。
- **专用后台线程**（命名 `RunDbStore.Writer`，`IsBackground=true`，static ctor 启动）单消费者执行
  `INSERT OR IGNORE` / `UPDATE`。参照 RouteLedger.cs:141-146 的 Outbox+Writer 结构。
- 挂载点：`BgiLogCenter.ProcessChunk` 内现有 `RouteLedger.Feed(e.UserName, chunk)` 那**一行**（BgiLogCenter.cs:695）
  换成 `RunDbStore.Feed(e.UserName, chunk, e.LogFile, day)`，外层 try/catch 不变。不新增定时器、不新增 DispatcherTimer。
- 文件滚动检测：`BgiLogCenter.Step` 里 `e.ResetForNewFile(logFile)` 的分支（BgiLogCenter.cs:561-566）加一次
  `RunDbStore.OnFileChanged(userName, newFile)` 回调，用于触发 §4.4 的跨天判定。

### 5.2 写失败降级（不能拖垮日志解析）

- 写线程循环里 try/catch，失败**丢弃该条**并 `App.AppendErrorLog("RunDbStore.Writer", ex)`（照 ConfigStore.cs:117-134 的模式）。
- **绝不把异常抛回 ProcessChunk**（Feed 是纯内存 + 入队，最坏情况只是队列里多几行，不阻塞解析）。
- 可选哨兵：连续 N 次写失败后把 `DbHealthy=false`，之后 Feed 直接短路（只归因不落库），每小时 TickHub 重试一次重建连接。
  默认 N=10。避免磁盘满/DB 损坏时写线程空转刷 error.log。

### 5.3 与现有 RouteLedger JSONL 的关系（决策：替换，不长期并存）

- **决策：重写为 DB，JSONL 落盘退役。** 理由：双写 = 双份自然键簿记 + 两处修 bug 成本，且用户已明确要 DB；台账量小（24 行/账号/天），迁移零风险。
- 一次性迁移：启动时若 DB 为空且 `<DataDir>\ledger\*.jsonl` 存在，逐行反序列化（坏行跳过，同 RouteLedger.cs:372-392）转 INSERT OR IGNORE。
  迁移完**不删除** JSONL 文件，留作冷备/回滚。
- 归因状态机从 RouteLedger.Feed 提升/扩写进 RunDbStore；RouteLedger 类保留只读 `Query*` 门面（内部改走 SQL）以最小化调用方改动。

### 5.4 与 /api/bgi/ledger 兼容（WebServer.cs:361、579-593）

- 硬约束：`RouteLedger.Query / QueryToday / QueryTodayProblems / QueryZeroReward / QueryFailed / GroupSummary`
  的**方法签名与返回类型不变**（RouteLedgerRecord / RouteLedgerGroupSummary，RouteLedger.cs:14-61）。
- `ApiBgiLedger` 返回结构不变（`records/groups/zeroReward/failed`，WebServer.cs:585-592）→ WebServer.cs 无需改动。
- SQL 映射：run 行 → RouteLedgerRecord（Day=end_day，Start=start_at 的 HH:mm:ss.fff，End=end_at 的 HH:mm:ss.fff，
  DurSec=dur_ms/1000，ScriptCount，Exp，Mora，Picks，Detail，State）。`Query(user, days)` 用
  `WHERE account=$u AND end_day >= $cutoff ORDER BY end_at_ms DESC`；GroupSummary 用 GROUP BY group_name 聚合 exp/mora/fail 计数。

### 5.5 周期任务（只走 TickHub，禁止新建 DispatcherTimer）

- 清理/陈旧-open 兜底：`TickHub.RegisterBackground("RunDbStore.Cleanup", 3600, ...)`（每小时一拍；TickHub.cs:95-107）。
- 启动时额外跑一次清理（DB init 之后）。跨天由「新文件检测 + 小时级清理」共同覆盖，不单开日期轮询。

---

## 6. 留存与清理

### 6.1 配置（放 AppConfig 哪个位置、默认值、合法范围）

- 位置：`AppSettings`（Models/Models.cs:327）新增字段 `RunDbRetentionDays`；由 ConfigStore 序列化进 config.json
  （ConfigStore.cs:139、195-304 已有加载/迁移骨架，新增一个 int 属性即可，兼容旧配置 = 缺失取默认 7）。
- 默认值：**7 天**。
- 合法范围：**[1, 365]**；读取时 clamp：缺失/≤0/NaN → 7，>365 → 365。
- **不做 UI**（用户明确不要界面展示）。改值方式 = 手改 config.json（无设置页、无绑定）。
- 与现有 `BgiBackupKeep`（0=不限，Models.cs:394）语义不同：本字段 **0 不表示「无限」**，避免无界增长。

### 6.2 清理触发时机

1. **启动一次**（DB init + open 重建之后）。
2. **小时级 TickHub 后台拍**（RegisterBackground，3600s）。
3. 跨天：由新文件检测（§5.1）触发一次按 end_day 的删除（可选优化，小时级已兜底）。

### 6.3 清理 SQL + vacuum/增量

    cutoff = now - retention_days
    DELETE FROM run WHERE (end_at_ms IS NOT NULL AND end_at_ms < cutoff)
                      OR (end_at_ms IS NULL AND start_at_ms < cutoff);   -- 陈旧 open 一并删（route/err 由 ON DELETE CASCADE 带走）
    -- 每 24h 最多一次，删除行数 > 1000 时才做：
    PRAGMA wal_checkpoint(TRUNCATE);
    -- 磁盘回收：删除占比 > 20% 时才做（低频，放清理拍内，不计较短暂停顿）
    VACUUM;

- route/err 依赖 `ON DELETE CASCADE`（§2.2 DDL 已声明）随 run 一并清理，无需额外 DELETE。
- 清理放**写线程**执行（与写入同一线程，天然串行、无锁竞争），TickHub 只置标记。

### 6.4 磁盘估算

| 项 | 量 |
|---|---|
| 记录量 | ~24 run/账号/天（ROUTE-LEDGER-DESIGN-20261122.md §6.1：16.6 结束 + 7.2 失败 + 少量中断） |
| run 行 | ~200 B |
| route 行 | 典型 1 脚本 + 5 路线 ≈ 6 行 × ~120 B ≈ 720 B/run；pick_detail JSON ~50-200 B |
| err 行 | 稀疏；单条含栈 1~8 KB，估算 ≤10 条/账号/天 |
| 单账号/天 | 典型 ~15 KB，含错误栈最坏 ~50 KB |
| **7 天 × 20 账号** | **典型 ~2 MB，最坏 ~7 MB（含 WAL/SHM 开销后个位数 MB）** |

结论：7 天留存下单文件个位数 MB，SQLite 完全无压力；无需分库、无需压缩。

---

## 7. 时序图 / 流程

### 7.1 正常续跑跨天（23:00 → 次日 1:00）

    24日文件                   25日文件
    ───────                    ───────
    23:00 配置组"X"加载完成…    00:10 交互或拾取:"游医"   (无「加载完成」)
      → INSERT run(open)       00:59 [DEBUG] 进入路线循环…
    23:59 交互或拾取…           01:00 配置组"X"执行结束
    （文件滚动，open 行留在 DB）    → UPDATE run(end=01:00, is_cross_day=1)
    结果：1 条 run + 若干 route/err，start_day=24日, end_day=25日

### 7.2 重启续跑跨天（00:30 重启）

    启动：SELECT open 行 → 重建 OpenRuns（"X", start=23:00, run_id=…）
    重扫 25日文件：无「加载完成」→ 续行归因到该 open 窗口
    01:00 执行结束 → UPDATE run(end=01:00, is_backfilled=1)
    结果：仍 1 条 run，start 时间未被丢失（区别于旧 JSONL 的 ghost 行为）

### 7.3 陈旧 open 兜底（程序/机器跨天停机）

    启动：open 行("X", start=23:00) 在 DB
    检测 25日文件首条时间 = 08:00
    gap = 08:00 - open.last_event(23:59) = 481 分钟 > 5 分钟
    → close(open, "Interrupted", "跨天时间断档，未正常结束", end=23:59)
    结果：1 条 Interrupted 的 run（如实反映「起跑但没跑到收尾」），不虚构成跨天完成

---

## 8. 明确的非目标

1. **不做界面展示**（用户明确不要 UI）：不加设置页、不加列表页、不加任何 XAML/绑定。配置只落 config.json。
2. **不改变现有日志解析的 CPU/内存预算**：ProcessChunk 仍是单遍流式、字节游标、只喂增量（BgiLogCenter.cs:576-678 不变）；
   Feed 只多跑若干已编译正则 + 内存状态机 + 入队，写盘全在后台线程（与现状 RouteLedger.Feed 同量级）。
3. 不新建 DispatcherTimer、不新建 FileSystemWatcher、不枚举进程（沿用既有 watcher/游标）。
4. 不改 `dist\`、不发版、不动 `data\` 目录结构之外的任何既有功能。
5. 不动 Server/Db.cs（那是联机大厅服务端 DB，与数据中心台账无关，仅作 SQLite 用法参照）。

---

## 9. 风险与回滚

| 风险 | 影响 | 缓解 / 回滚 |
|---|---|---|
| DB 文件损坏（磁盘满/异常断电） | 台账查询失败 | WAL + synchronous=NORMAL 降低损坏概率；写失败降级丢弃 + error.log；坏库可删除后由 JSONL 冷备重建（迁移逻辑保留） |
| 跨天补全误判（阈值 5 分钟不匹配真实停顿） | 把两次独立运行判成一次（或反之） | 阈值集中成常量；Interrupted 兜底保证最坏也只是 state 标错、不丢 start 事实；后续可用日志样本回测调整 |
| 迁移失败 | 历史台账缺失 | 迁移是 best-effort（坏行跳过）；JSONL 不删，随时可重迁 |
| 写线程与读取并发 | SQLITE_BUSY | 单写线程 + WAL（读不阻塞写）+ 每连接 busy_timeout=5000 |
| 自增 err_id 与确定性 run/route_id 混用 | 无 | err 用唯一索引去重，与自增主键正交 |

**回滚**：若 DB 方案出问题，把 BgiLogCenter.ProcessChunk 的挂钩恢复为 `RouteLedger.Feed`，JSONL 落盘路径仍在（文件未删），
即可回到 2026-11-22 的 JSONL 台账形态；数据不丢失（JSONL 是原始冷备）。

---

## 10. 交付给实现者的改动清单（本任务不执行，仅契约）

| 文件 | 动作 |
|---|---|
| Services/RunDbStore.cs | 新增：建库 DDL、Feed 归因状态机、写线程、Query* SQL 门面、跨天补全、清理 |
| Services/BgiLogCenter.cs | ProcessChunk 挂钩一行（:695）+ Step 文件滚动回调（:561-566）；其余不动 |
| Services/RouteLedger.cs | 归因逻辑移交 RunDbStore；保留 RouteLedgerRecord/GroupSummary DTO 与 Query* 签名（内部改 SQL） |
| Models/Models.cs | AppSettings 增 `RunDbRetentionDays`（默认 7） |
| Services/WebServer.cs | 无需改（ApiBgiLedger 返回结构不变） |
| SoulLink.csproj | 增 `Microsoft.Data.Sqlite` 包引用（版本对齐 Server/SoulLink.Server.csproj:21 的 11.0.0） |

---

## 11. 验收对照（本文档是否满足任务验收）

- 每个锚点来自实际读过的代码或日志，标注 file:line 或日志行号 → §1 全表。
- 架构契约可直接照抄 → §2 DDL + §3 伪代码 + §4 判定阈值 + §6 清理 SQL。
- 跨天补全给出具体阈值（5 分钟连续性 / 粘性组名 / Interrupted 兜底）→ §4.3。
- 「23:00 起、次日 1:00 止落几条」直接回答 → §4.5。
