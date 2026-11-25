# t35 附录：pick_event 跨 chunk 去重修复 · 验证者注意事项

> 日期：2026-11-25（t35，attempt 1298b565）。主交付见任务 update 记录；本文档收录后续验证者
> 复跑 T35 场景时必须知道的三个语义/坑（t7 复验或 t13 后重跑会用到）。

## 1. 验证工具与入口

- 场景文件：`tools/runcollect-verify/T35Scenarios.cs`（四场景：碰撞 / A4 分段 / 重扫幂等 / 旧库迁移）；
- 快速复跑：`$env:T35_ONLY='1'; dotnet run --project tools\runcollect-verify -c Release`
  （不加过滤则跑全套含 T6/T10/t3 回归，A1 重放耗时长）；
- 工程直编 Services 源码（RUN_DB_TEST + UseIsolatedForTest 隔离 %TEMP% DB），不碰生产库。

## 2. 坑①：碰撞用例的组行 ts 必须与拾取行一致

T35-1 构造「两个完全相同的 chunk」时，**组开始行的 ts 要与拾取行同为 23:30**。
若组行用 23:00、拾取行 23:30：第二次 Feed 同文本时，首行 23:00 会比 DayClock 的 LastRaw（23:30）
回退 30 分钟 → 触发 ResolveTs 的**天轴回绕**（+1 天）→ 组/拾取全部落到「次日」→ 产生第二个 run 行，
碰撞被这个副作用掩盖（表象：两条各 1 pick 而不是同一 run 内碰撞/去重）。
回绕本身是 t3/t6 的既有正确语义（23:59→00:00 必跨日），只是与本用例目标正交。

## 3. 坑②：重扫幂等必须与首扫「同尺寸分块」

T35-3 模拟重启重扫时，必须按**与首扫相同的 2000 行/chunk** 重新分块喂入。
原因：chunk_offset 是 per user+file 的递增序号（与 raw_event 同语义）——同尺寸分块时
重扫的 chunk 序列与首扫完全一致 → 每行的自然键 (run,ts,chunk,line,off,kind) 相同 →
INSERT OR IGNORE 幂等。若把整份文件当成**单个大 chunk** 重喂：首扫分布在 chunk 0..4 的行，
重扫全部落在 chunk 0 → chunk 维不同 → 自然键不同 → 行数翻倍。
**这是设计语义不是缺陷**（生产重扫走 BgiLogCenter 的同游标增量路径，天然同分块）；
但验证者构造重扫用例时必须复刻同分块，否则会误报回归。

## 4. 语义③：同组重跑下的 run.Picks 口径

T35-1 第二个 chunk 的组开始行触发 StartGroup「同组重跑未收尾」路径：旧内存窗口作废、
新窗口从同 ts 开始 → 同 run_id（INSERT OR IGNORE 保旧行）→ **run 行的 picks 归属最新内存窗口**，
跨 chunk 累计不会体现在 run.Picks（除非收到结束事件才落最终值）。
因此「逐物品计数不变」的断言口径应取 **pick_event 明细**（durable 层），不要用 run.Picks——
测试里已按此断言（游医 pick_event=2）。

## 5. 关联事项（ captain 已定性）

- t9 的 `foreach (var ev in events) ev.Anchor = AnchorOf(line)`（RunRecordCollector.cs:697）：
  确认笔误（改写全部历史事件锚点 + O(n²)），但 `Ev.Anchor` 全仓无读取方 → 死字段，
  定性 P2 记入 tech-debt，待 t7 复验完、t13 落地后一并处理。本修复未动它。
