# raw_event 存储策略 A（t49）：只存锚点行 + 错误/警告行

> 日期：2026-11-25
> 用户裁定：方案 A —— raw_event 只存有锚点的行 + 错误/警告行（ERR/WRN/FTL）。

## 一、问题与收益

- A1 日志 35.05 MB → run.db 701.8 MB（20 倍膨胀），raw_event 1,626,923 行；
- 有锚点 30,616 行（1.9%），无锚点噪音 1,596,307 行（98.1%）；
- 用户要留存 7 天 → 4 账号 × 700MB × 7 天 ≈ 18 GB，不可接受；
- 方案 A 后预估 20~40 MB。

## 二、改动

### 改动 1：ArchiveRawLine 加入库门槛（RunRecordCollector.cs）

```csharp
bool keep = !string.IsNullOrEmpty(anchor) || level is "ERR" or "WRN" or "FTL";
if (keep) { output.Add(new RawEvent { ... }); }
```

★ 关键坑（已处理）：RawByteOffsets 的推进在 output.Add 之前（lock(StateLock) 块内），
  即使本行不入库也必须继续执行——只把 output.Add 包进 if，绝不 return。
  否则被过滤行的字节数不累加 → 后续所有入库行的 byte_offset 全部偏移，
  「按偏移回溯原文」静默失效且不报错。

### 改动 2：清理现有数据（RunRecordStore.cs）

新增 PurgeNoiseRawEventsAsync(ct)：
- DELETE FROM raw_event WHERE anchor='' AND level NOT IN ('ERR','WRN','FTL')；
- 再 VACUUM（SQLite 删行不自动缩文件，必须 VACUUM）；
- VACUUM 在事务外执行（用 EnqueueWrite 单线程队列串行跑）；
- 返回删除行数。

### 改动 3：不破坏 t44 修复

- AnchorOfLine(ev.Pos, lineAnchorByStart) 不回退成行循环内盖锚点；
- RawText 截断（RawLineCharLimit=16KB）不回退成空串。

## 三、验收实测

- 结构化零损失：清理 SQL 只 WHERE anchor='' AND level NOT IN(...)，绝不触碰 run/route/err/pick_event；
  隔离测试证明 run 表清理前后行数不变；
- byte_offset 正确性：过滤 2 行后，第 3 行（ERR）byte_offset=84 = 前两行字节之和（证明跳过行仍推进偏移）✅；
- 清理删除噪音 2 行、raw_event 剩 3 行（锚点+ERR+WRN）✅；
- 双 TFM 0 error ✅。

## 四、重采验证（生产环境步骤，需真实 A1 副本）

1. 清空库后重新采集 A1 的 35MB 日志（%TEMP% 副本，A1 只读）；
2. 断言新库 < 60MB 且 run/route/err/pick_event 齐全；
3. 抽 3 条 anchor<>'' 的 raw_event，raw_text 与日志原文逐字符一致。

★ 注：本任务因 A1 红线（只读）与内存约束，采用隔离测试（%TEMP% 副本）验证逻辑；
   真实 701.8MB 库的 PurgeNoiseRawEventsAsync 由集成/验证任务在安全窗口执行并贴前后大小。
