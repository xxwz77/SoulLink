# day_span 区间互斥与历史补算修复（t39）

> 日期：2026-11-25。范围：仅 Services/RunRecordStore.cs。未改 WebServer/taskedit/采集器，A1 只读。

## 根因

1) 原实现只处理 now 的今天/昨天，now=26 时永远漏掉 24 日。
2) run 的 running 覆盖与 state_span 的 idle/not_running 直接 UNION ALL 累加，重叠时间重复计入，可能使 unknown<0。

## 修复

- `UpdateDaySpansForAccountAsync(..., recentDays=7)`：从实际状态段生成日期集合，覆盖最近 7 天有数据的日期，不再只看两天；已 final 日跳过。
- 优先级 `running > not_running > idle > unknown`：每类先合并同类区间，再扣除更高优先级已占区间；同一毫秒只计一次。
- 跨天先与自然日 [00:00,次日00:00) 求交，再做互斥积分。
- unknown = 86400000 - running - idle - not_running，未覆盖才 unknown；没有事实的日期仍无记录。
- 写前断言四项非负且和为 86400000；异常只记一次节流警告，不写坏桶。
- 新表 CHECK 非负；新旧库均有 INSERT/UPDATE 触发器拒绝负值或非日长数据，不删旧库。

## 验证

同 captain 播种 idle 24日23:00→25日01:00 + not_running 25日01:00→13:00，now=26日00:30：

- 24日 idle=3,600,000，unknown=82,800,000，final=1（此前整行缺失，现已补）。
- 25日 idle=3,600,000，not_running=43,200,000，unknown=39,600,000。
- 两天 running+idle+not_running+unknown == 86,400,000，unknown 非负。
- final 日连续重算两次 Idle/NotRunning/ComputedAtMs 完全一致。
- 无数据日仍返回无记录。
- 重叠 run 23:00→01:00 与 idle 23:00→13:00：24日 Running=1h/Idle=0；25日 Running=1h/Idle=12h，无重复。
- 手工写 unknown=-1 被数据库触发器拒绝。
- t35/t10/t6/t3 全回归 ALL-PASS；双 TFM Release 0 error。
