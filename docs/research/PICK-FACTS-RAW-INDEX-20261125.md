# 拾取事实与原始日志索引（t9）交付

> 日期：2026-11-25。范围：RunRecordStore、RunRecordCollector 的 raw 索引、既有 ProcessChunk 文件指纹透传。
> 原则：**结构化事实完整落库；raw_event 只存可回读索引，原文按需从日志读取。**

## 1. 两类数据，不能混为一谈

以下结构化事实**全部保留，可离线、供网页和其他调用方直接查询，不读原日志**：
run/route/err（异常类型、消息、完整多行栈）、pick_event（picked/no_target/skipped、物品名）、state_span、
米游社 my_role/my_sign_log/my_ledger_entry/my_ledger_day/my_ledger_month/my_sign_batch/my_sign_task/my_pull_log，
以及摩拉归类 my_mora_class/my_mora_class_run/my_mora_summary。

只有 raw_event 的原文改为索引：账号、日志 basename、source_day、ts_ms、level、logger、Feed/行定位、
byte_offset、byte_length、anchor、content_hash、collected_at_ms、文件 size/mtime 指纹。
旧 raw_text 列保留，旧数据仍可读；新写入 raw_text=''，不删除旧库。

## 2. pick_event：否定事实完整可查

t6 已有表/幂等自然键/采集守恒，本任务不重做采集器。
- picked/no_target/skipped 三类事件原话物品名，含“无有效结果”。
- 幂等键保持 run_id + ts_ms + kind + item_name + line_no + line_offset，重扫不重复。
- route 的 picked/skipped/no_target 三列自动迁移；PickCount 仍只等于 picked，不破坏既有统计。
- 索引：ix_pick_run、ix_pick_route、ix_pick_kind_ts。
- 超期 pick_event 随 run 外键级联清理；保留的 run/route/err 不受影响；WAL checkpoint 沿用原实现。

新增事实 API（Store 方法，供鉴权网页/API 调用方接入）：
- QueryPickFactsAsync(runId?, routeId?, kind?, account?, days=7, offset=0, limit=1000, ct)
  按 run/route/kind/账号日期筛选，ORDER BY ts_ms,pick_id，LIMIT/OFFSET 分页（limit最大10000）。
  kind 是调用方明确指定的筛选，不是数据中心替用户判断。
- QueryPickCountsAsync(runId?, routeId?, account?, days=7, ct)
  返回 picked/no_target/skipped 三类计数，供与 route 三列对账。
- 既有 QueryPickEventsAsync 保持兼容。

## 3. raw_event 索引与网页回读 API

`ReadRawLogExcerptsAsync(account, srcFile?, runId?, routeId?, limit=500, ct)`
返回 `RawLogExcerptResult{Items,ElapsedMs,FileOpens}`；每项为 `RawLogExcerpt{Event,Text,Status,Detail}`。
按账号已绑定 BGI exe 定位 log 目录，再叠加允许的日志 basename；多账号同名文件不混用。
run 查询覆盖跨天 src_file_start/end；route 查询按 route.src_file。

- 每文件一次打开，offset 排序，连续行合并 seek/read；不逐行开文件。
- 正常：status=ok，Text 为对应 UTF-8 字节原文。
- 文件删除：status=missing，明确“原始日志已被清理”，结构化事实仍可用。
- 文件仍增长/越界：status=growing，读到当前末尾片段并明确不是目标行。
- 文件 size/mtime 不符：status=changed，提示原文可能不对应。
- 无索引的旧库：status=legacy，返回旧 raw_text；IO/权限失败：unavailable，不抛到网页线程。

UTF-8 字节偏移按原 chunk 每行 raw 字节 + 实际换行符计算（CRLF=2、LF=1，文件末尾=0），不是 .NET 字符数。
文件指纹从既有 Pump 的 FileInfo 透传 ProcessChunk/Feed，不新增每 chunk 文件 IO。

## 4. 真实验证

### raw 索引（A4 20261124，527,823 B）

- raw_event 8,114 行（含空行），**全部 8,114/8,114 行回读逐字节一致**；随机 20 条亦一致。
- 8,114 行批量回读一次开文件，约 24 ms（后续一次 40 ms，均 <100 ms）。
- 新写入原文总字节 **0**；引用原始字节 527,823。
- DB 主文件 2,248,704 B=2.145 MiB，含 WAL 6.082 MiB。
  与 t8 主文件 2.477 MiB 比减少约 **13.4%**；含 WAL 6.414 MiB 比减少约 **5.2%**。
  降幅不是理论一个数量级，因复合长 raw_id/索引和 WAL 固定页开销，如实给出。
- 7×20 账号主文件线性估算约 **300.3 MiB**（t8 ~347.8 MiB）；含 WAL 峰值约 **851.5 MiB**（t8 ~898 MiB）。
  这些是当前 schema 的实测外推，不承诺不同日志/历史数据的固定体积。
- 缺失文件、超出当前长度、替换文件三条降级均实测通过。

### pick_event（A1 20261125，文件在增长，以当次快照为准）

- t6/t3/t10 全回归 ALL-PASS：DB+孤儿三类事件与日志行数完全一致，重扫幂等，run/route/err不变。
- 最新隔离快照：4,868 条 pick_event，平均结构载荷约 164.2 B/条（长 run_id/route_id 计入）。
- 隔离测量库（父 run + pick_event + 全部索引）主文件 2,609,152 B，WAL 4,157,112 B，共 6.44 MiB。
  平均约 **1,389.9 B/事件**，7天×20账号线性估算 **903.4 MiB**，这是含长自然键与 WAL 的保守上界，不是理论最小行大小。
- 若未来要优化，优先**设计更短的整数事件 ID/可复用父 ID 字典、定期 checkpoint/vacuum**，而不是删除否定事实。
  可选分级：picked 全留明细、no_target/skipped 也全留明细；如用户仍嫌大，可只缩短 raw_event 索引保留期或精简其索引，不能自行砍三类拾取事实。

### pick 核心测试

三类各若干 + 重复写入 → 4 条；中文物品名不乱码；kind/run/route/账号日期分页查询正确；三类计数对账；
旧库无 pick_event/三列自动升级且旧 run/route 仍在；过期 pick_event 级联删除，保留 run/route/err 不受影响。

## 5. 构建与边界

- `dotnet build SoulLink.csproj -c Release -f net11.0-windows`：主 TFM **0 error**。
- `dotnet run --project tools/runcollect-verify -c Release`：t6/t3/t10 ALL-PASS。
- `dotnet run --project RawTierProbe.csproj -c Release`：raw index 全量回读/降级/pick体量通过。
- `dotnet run --project PickFactsProbe.csproj -c Release`：pick 核心 ALL-PASS。
- 双 TFM 最后一次构建被**非本任务** `undefined/temp/taskedit-test/Program.cs` 多入口点/缺 TaskEdit 阻塞，
  已通知 captain 协调；本任务不擅删队友文件，具体命令错误在任务 commandsRun 如实记录。
- 未改 UI/RouteLedger JSONL、米游社结构、摩拉归类规则；不新增 DispatcherTimer。
