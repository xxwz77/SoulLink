# 状态时段与全天事实汇总（t11 / t12）交付

> 日期：2026-11-25。范围：state_span 留存/范围查询/day_span，t6 状态段与 dur_ms 自动计算未重做。

## 1. 留存覆盖

- run/route/err 保持既有清理和外键级联。
- pick_event 随 run ON DELETE CASCADE 清理，孤儿数不被伪造为 run 事实。
- state_span 按 COALESCE(end_at_ms,start_at_ms) < cutoff 删除；进行中段超时后也按留存策略清理。
- day_span 按 day < cutoffDay 删除。
- raw_event 继续按 source_day/collected_at_ms 清理。
- 超期 state_span/pick_event/day_span 均实测为 0；过期 run 删除，二次清理 0 行；保留 run/route/err 与既有 WAL checkpoint 不变。

## 2. 只读 API（全部既有鉴权之后）

- GET /api/bgi/spans?user=A1&start=epochMs&end=epochMs
  按时间范围裁剪状态段，返回 start/end/state/DurMs/DurMsSoFar/IsOngoing/src_file；进行中段 EndAtMs=null，DurMsSoFar自动算。
- GET /api/bgi/spans/totals?user=A1&start=epochMs&end=epochMs
  返回 IdleMs/NotRunningMs；XML/响应标明 derived_facts_not_business_judgement：派生视图非事实源，业务判定归消费方。
- GET /api/bgi/day_span?user=A1&day=yyyy-MM-dd
  有事实返回 running/idle/not_running/unknown/computed_at/is_final；无事实返回 hasRecord=false、message=无记录，不返回全天0。

Store 对应：QuerySpansRangeAsync、QuerySpanTotalsAsync、QueryDaySpanAsync、
UpdateDaySpansForAccountAsync、QuerySpanAccountsAsync、QueryRunAccountsForDaySpanAsync。

## 3. day_span 计算与 running 依据

表：account/day/running_ms/idle_ms/not_running_ms/unknown_ms/computed_at_ms/is_final，PK(account,day)。
CHECK 四类之和 = 86,400,000 ms，避免部分覆盖时丢失全天长度。

- 每段先与自然日 [00:00,次日00:00) 求交；23:00→次日01:00 的 idle 拆成两天各 3,600,000ms。
- running：仅来自 state_span.state=running 和 run.start_at_ms/end_at_ms 的真实覆盖时段。
  run 表示日志里有配置组运行窗口，**不把其它时间猜成进程 running**；未覆盖一律 unknown。
- idle/not_running：直接取 state_span 的对应事实；无明确状态的时间为 unknown。
- 有事实才建桶；从未有记录日期不创建全天 unknown 假记录。
- RunRecordHost 既有 3600s TickHub 每次只重算今天/昨天；昨天结束即 is_final=1，final 日后不再写历史。
- 没有新增 DispatcherTimer；参数化 SQL，后台写队列。

## 4. 实测输出

- 双 TFM Release 0 error。
- day1 idle=3,600,000、is_final=1、四类之和 86,400,000；day2 idle=3,600,000、和 86,400,000。
- 封盘重复计算 IdleMs/ComputedAtMs 完全一致；进行中段 DurMsSoFar=13h，EndAtMs=null。
- run-only 30 分钟运行窗口 → RunningMs=1,800,000，UnknownMs=84,600,000；无事实次日仍无记录。
- 范围查询片段：idle start=23:30 end=次日00:30，DurMsSoFar=3,600,000，IsOngoing=false。
- totals：IdleMs=3,600,000，NotRunningMs=0。
- 旧库无 state_span/pick_event/day_span，启动自动补表，旧 run/route 保留，新 state/pick 可写。
- 超期三类记录删除，二次清理 0 行。
- tools/runcollect-verify t6/t3/t10 ALL-PASS（状态切换/跨天/路线/拾取/重扫不变）。

详见隔离探针输出：RESULT T11 CORE ALL-PASS / RESULT T11 ONGOING ALL-PASS。
