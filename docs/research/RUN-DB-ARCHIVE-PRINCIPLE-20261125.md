# 数据中心只做采集归档：原始事实与派生规则分层（t8）

> 日期：2026-11-25。范围：Services/RunRecordStore.cs、Services/RunRecordCollector.cs。

## 原则

日志事实先原样归档；筛选、归类、排名、聚合由消费方决定，不得因内容“看起来无效”而丢弃。

## 1. 改动清单

### raw_event：同一 chunk 的全量原始事实

在既有 ScanEvents 逐行循环直接归档，无第二条文件扫描链、无新定时器、无 UI 线程 IO。每 Feed 批量入队一次，后台写线程单事务参数化 INSERT OR IGNORE。

- raw_id：账号 + 文件 basename + Feed 序号 + chunk 内行起点，同一重扫复现。
- account/src_file、chunk_offset/line_offset/line_no：账号、源文件、Feed序号和字符偏移。
- ts_ms/level/logger：日志头解析字段，没有则空。
- raw_text/raw_chars：原文及原长度，除行尾 CR 外不改写。
- raw_truncated：单行超过 16,384 字符才截断，原长度仍保留。
- anchor：预留锚点标记，不影响 raw 入库。
- collected_at_ms/content_hash：采集时刻与原文哈希（内容校验，不代替行号）。
- occurrence：兼容保留字段（固定0），身份由偏移决定。
- source_day：源文件名日期，用于留存。

同内容重复行、异常样式组名、空白行都保留。SplitLines 不再给恰好以换行符结尾的 chunk 额外制造哨兵空行。整文件时间回退时 raw Feed 序号归零，之后连续 chunk 递增；全量重扫 raw/run/route/err 均幂等。raw_event 与 run 同期清理，按 source_day；无源日期才按 collected_at_ms。旧库自动建表/索引/补 source_day，不删旧数据。

### 移除价值判断

- ValidGroup 从“名字像 Exception/取消异常就丢弃”改为只检查 capture 非空。是否有效由消费方判定。
- “无有效结果，不交互”事件的 item 改为原话“无有效结果”，不静默改名/抹掉；no_target 只是投影类别。
- 空 capture 不生成结构化 run/route 行（缺必填字段），但原文已先落 raw_event，不算丢事实。
- QueryGroups 等 GROUP BY/统计属于**消费方便捷派生视图**，XML 注释明确不是事实源。为兼容现有 API 保留它们，不移到 WebServer；事实查询 QueryRawEventsAsync 返回原文，任何视图都不减少已存事实。

## 2. 实测：A4 20261124 日志（只读）

命令：`dotnet run --project ArchiveProbe.csproj -c Release`（%TEMP%，直接链接当前生产源，后台写线程）。

```text
INPUT physical=8115 effective=5488 blanks=2626 trailingEmpty=True chunks=5
PASS raw_event 非空行数等于日志有效行数 5488
PASS raw_event 空行也完整归档
PASS raw 截断标记与原文长度一致
ARCHIVE effective=5488 blanks=2626 raw=8114 truncated=0
ARCHIVE dbBytes=2596864 walBytes=4128272 totalMB=6.414
BASELINE runs=2 routes=32 errs=4
PASS t5/t6 原有结构化事实数量不减少
RESCAN raw=8114→8114 runs=2→2 routes=32→32 errs=4→4
PASS 旧库自动新增 raw_event
PASS 迁移旧 run 数据仍在
PASS 异常样式组名不再被价值过滤
PASS 无有效结果原文入库
PASS no_target 原话保留于 pick_event
RESULT T8 ALL-PASS
```

物理文件 527,823 B。Split('\n') 得 8,115 段，最后终止空段不是额外事件，所以实际 **8,114 行 = 非空 5,488 + 空行 2,626**。raw_event 为 8,114 行，非空行全保留，0 截断；空行也归档，不能直接把旧报告“8115 行”当 raw 行数。

| 口径 | 实测 | 说明 |
|---|---:|---|
| DB 主文件 | 2,596,864 B = 2.477 MiB | 含结构化与 raw_event |
| 含 WAL 峰值 | 6,725,136 B = 6.414 MiB | 批量事务未 checkpoint 的峰值，非每日固定增长 |
| 7天×20账号主文件估算 | ~347.8 MiB | 按主文件线性外推，未计压缩/清理 |
| 7天×20账号含WAL峰值估算 | ~898.0 MiB | 按峰值线性外推，非稳态承诺 |

因此**不启用普通行摘要降级**：虽然峰值线性外推超过 200 MB，用户原则要求先保留真实原文。16 KiB/行阈值只用于明示截断；本次 0 截断。后台留存清理和既有 incremental vacuum 回收，不编造稳态体积。

## 3. 一致性自查：采集层仍有哪些“判定”，为何是解析必需

| 保留逻辑 | 性质 | 为何不属价值筛选 |
|---|---|---|
| 空 capture 不生成 run/route 行 | 字段缺失 | 原文已归档，结构化必填字段不能满足，不是“内容没用” |
| 正则识别组/脚本/路线/拾取 | 定位与结构化 | 只决定归属，不删除原文，消费方可另判 |
| 文件日期+行内时间推断跨日 | 时钟解析 | 解决只有时分秒的问题，原文与偏移保留 |
| ERR 头与无时间戳续行归并错误块 | 结构边界解析 | 完整栈仍保留，raw 逐行也在 |
| 开窗/关窗/换文件分段 | 时段结构化 | 是运行窗口索引，raw 原文不参与筛选，t10 语义未改 |
| picked/no_target/skipped | 事件类型投影 | item 原话保存，类别不删减原始行 |
| raw_truncated 16 KiB | 明示资源上限 | 仅超长行截断，raw_chars+标记保留，不是有效性判断 |
| QueryGroups 等统计/失败/零收益视图 | 消费方派生 | 只读兼容视图，不回写、不减少事实 |

grep 证明：ValidGroup 不再含 OperationCanceledException 或 name.Contains("Exception")；“无有效结果”只在解析锚点/原话投递，不存在 item=="无有效结果" → continue。Exception 剩余命中是异常类型/正文解析，不是按组名过滤。

## 4. 构建与回归

- `dotnet build SoulLink.csproj -c Release`：双 TFM 0 error。
- `dotnet run --project tools/runcollect-verify -c Release`：t6/t3/t10 **ALL-PASS**。
- t8 真实回放、旧库迁移、异常样式组名/no_target/重扫断言全过。
- 未改 RouteLedger JSONL、WebServer、MiyousheService、开窗/锚点/t10 逻辑；临时探针与构建产物清理。
