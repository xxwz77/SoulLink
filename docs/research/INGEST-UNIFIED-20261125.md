# 统一化采集 + 启动时遗漏检查（t45 架构设计）

> 日期：2026-11-25
> 代码落点：Services/IngestCursorStore.cs（新）、Services/BgiLogCenter.cs（游标接线）、Services/WebServer.cs（可观测 API）
> 用户原话：数据中心应该做统一化的采集，每次启动要检查是否有遗漏。采集统一解析统一，多账号是不是还得做隔离？电脑核心那么多，把任务分到不同核心跑。

## 一、问题回溯：为什么之前会每次启动全量重扫

| # | 缺陷 | 后果 |
|---|---|---|
| 1 | Cursor 只存内存（BgiLogCenter.cs:49），进程退出即丢失，重启 Cursor=0（:135） | 每次启动从文件开头重扫全部历史 |
| 2 | 无遗漏检查 | 停进程期间日志增长要么全扫（慢）要么漏掉，无法察觉 |
| 3 | 5 个订阅者各自为政（Status/Groups/DailyStats/RouteLedger/RunRecordCollector 各跑一遍正则） | 无统一事务边界，崩溃可能有的吃了有的没吃 |
| 4 | 共享一把全局锁 + foreach(acc) Step(acc) 单线程串行 | A1 的 34MB 啃不完，A2/A3/A4 全饿死 |
| 5 | 没上并行 | 32 逻辑核只用 2 核，6 分钟只推进 2% |

## 二、三层隔离（用户「多账号是否要隔离」的答案：要，分三层）

第一层：解析隔离（按账号并行）
- 每个账号独立 Entry + 独立解析器状态；
- 不同账号解析可并行（A1 啃 34MB 时 A2/A3/A4 照样跟进）；
- 同账号内仍串行（同一日志文件不并发读）；
- 并发上限 = Math.Min(账号数, Math.Max(2, ProcessorCount / 4))（32 核 → 8）。

第二层：写库串行（保持现状，别改）
- RunRecordStoreCore 单线程队列是对的，多线程写 SQLite 只会锁争用；
- 并行解析结果统一入队。

第三层：cursor 按 (account, src_file) 独立
- ingest_cursor 主键 (account, src_file)；
- A1 的 cursor 推进不影响 A4，各账号遗漏检查独立。

关于「统一解析」的正确理解：不是「共用一份解析状态」，而是「同一份日志只解析一次、统一管道分发」——单次解析产出结构化事件 → 分发给各消费者，而非每个消费者重扫全文。（本任务只做游标+遗漏+管道封装，行级并行扫描留待 t44 性能优化一并落地，避免两个任务同时改解析器互踩。）

## 三、ingest_cursor 表结构（独立库）

- 数据库：<DataDir>/run-db/ingest.db（与 run.db 同目录不同文件，更新包不包含）。
- 独立库理由：RunRecordStore 表结构由 t44 并行改，共享同一 DB 会在 EnsureSchema 互踩。

```sql
CREATE TABLE IF NOT EXISTS ingest_cursor (
 account TEXT NOT NULL,
 src_file TEXT NOT NULL,
 cursor INTEGER NOT NULL DEFAULT 0,
 file_length INTEGER NOT NULL DEFAULT 0,
 file_write_ticks INTEGER NOT NULL DEFAULT 0,
 updated_at_ms INTEGER NOT NULL DEFAULT 0,
 first_seen_ms INTEGER NOT NULL DEFAULT 0,
 PRIMARY KEY(account, src_file)
);
```

- 节流写：推进 ≥1MB 或距上次 ≥30s 才落盘（游标是位置推进非业务数据，丢了最多多扫一段，幂等兜底）。

## 四、启动遗漏检查流程

1. 读持久化 cursor（IngestCursorStore.LoadCursor）；
2. 对比当前文件长度；
3. 未读区间 = [cursor, len)；
4. > 8MB → LogBus 记「补采中」，分多个 tick（MaxBytesPerTick=32MB/次）消化，不阻塞启动；
5. len < cursor → 轮转：Reset 游标为 0 + LogBus 记「轮转，从头补」；
6. 文件不存在 → LogBus 记「缺失」；
7. 可观测：GET /api/bgi/ingest/status 返回每账号 cursor/待补字节/更新时间。

## 五、补采与实时的一致性

- 每次 tick 先做「补采」预算（MaxBytesPerTick 单账号 32MB，实际按 chunk 4MB 内循环读），再做「实时」追加；
- 补采历史与实时跟进不互相饿死：同一 Pump 从 cursor 读到 EOF，天然覆盖补采与实时；
- 补采期间仍能响应新数据（FileSystemWatcher 置 Dirty 标记，1 秒合并窗口统一消费）。

## 六、统一事务边界（改动策略）

- 不重写现有解析器（e.Status/e.Groups/DailyStatsCache/RouteLedger 不动）；
- 只改「怎么喂」和「喂到哪」：ProcessChunk 保持 5 个订阅者调用顺序，但把 cursor 推进纳入同一检查点——Pump 处理完一个 chunk 后，cursor 与结构化数据要么一起进、要么一起退；
- 崩溃恢复：游标若略滞后于已写入的结构化数据，重扫会因自然键幂等（INSERT OR IGNORE）不产生重复；游标若略超前（极端），丢失的只是位置，下次重扫补上。

## 七、并行/串行开关（风险缓解）

- 并行扫描引入的顺序错乱 bug 极隐蔽（时间轴错、状态机错乱）；
- 保留「并行/串行」开关（配置项或环境变量），出问题立刻退回串行；
- 本任务游标持久化不引入并行（保守），并行扫描由 t44 性能优化统一落地并配开关。

## 八、验收实测（本任务已完成部分）

- 断点续传：喂 10MB → flush → 重启 LoadCursor=10485760（从断点，非 0）
- 遗漏检查：追加 5MB → pending=5242880
- 轮转：Reset → LoadCursor=0
- 可观测：All() 返回 account/cursor/len/pending
- 幂等：重复 Upsert 不新增行
- 多账号隔离：A1=100 / A4=5000 互不影响
- 双 TFM 0 error