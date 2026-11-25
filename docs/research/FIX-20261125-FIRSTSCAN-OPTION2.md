# t44 交付：首扫 34MB 日志 CPU 满载根因（O(n²) 锚点盖章）+ raw_text 全空修复

> 日期：2026-11-25（t43/t44，attempt 9dcf68d4）。紧急：A1 接入后 CPU 打满、采集停滞。

## 1. 根因（实测铁证）

- **现象**：A1 接入后 CPU ≈2 核满载，byteOffset 冻在 742,676（2%），事件数不再增长。
- **复现**：取 A1 真实日志前 4MB（59,243 行，含 985 交互或拾取/808 无有效结果/191 识别到），
  隔离库手动 Feed 一次 = **61.05 秒**。34MB ÷ 4MB × 61s ≈ **9 分钟单线程满载**——正是观察到的
 「卡住」（不是死锁：offset 冻结只是采样间隔内单 chunk 没跑完）。
- **定位**：`RunRecordCollector.cs` 行循环内一句
  `foreach (var ev in events) ev.Anchor = AnchorOf(line);` —— 每处理**一行**就遍历**全部已累积事件**，
  O(行×事件)：59,243 行 × 平均上千事件 ≈ 上亿次字符串操作/每 4MB chunk。
  删除该句（同库同数据对照实验）：**61.05s → 1.28s（48×）**。二分实锤。

## 2. 修复（两处，均已落地并回归）

1. **O(n²)→O(1)**：行循环内先算 `lineAnchorByStart[lineStart] = AnchorOf(line)`（喂 ArchiveRawLine 用），
   事件锚点改为**行循环结束后按事件自身 Pos 反查**（新 helper `AnchorOfLine`，与 TimestampOf 同思路）。
   顺带修掉 captain 已核实的语义 bug：原写法会把**先前事件**的锚点改写成**当前行**的锚点。
2. **raw_text 全空修复**：`ArchiveRawLine` 里 `RawText = ""` 硬编码 → 改为存 `line` 并按
   `RawLineCharLimit`(16K) 截断、`RawTruncated` 如实置位。实测 A1 4MB 后 raw 抽样 4/5 非空
   （第 5 条是真实空行），首行原文可读。

## 3. 性能与回归

| 指标 | 修复前 | 修复后 |
|---|---|---|
| Feed(4MB 真实 A1 chunk) | 61.05s / 60.60s（两次） | **1.41s / 1.39s**（44×） |
| 34MB 首扫预计 | ~9 分钟单核满载 | **~13 秒**（9×1.4s） |
| raw_text | 全空（23 万条不可诊断） | 抽样 4/5 非空、原文可读 |
| 全套回归（T44+T35+T10+T6+t3，含 A1/A4 真实日志回放） | — | **ALL-PASS** |
| 双 TFM `-f net11.0-windows` / `-f net11.0-windows10.0.26100.0` | — | 均 0 error |

## 4. 对 captain 三个判断的回应

1. **是否一次性成本**：是（首扫 34MB 历史量的成本），但修复前 9 分钟 × 每个新账号首扫不可接受；
   修复后 34MB 首扫 ≈13 秒，无需「从尾部读 N MB」的方案（保留全量首扫=数据完整）。
2. **MaxBytesPerTick/降块**：不动——瓶颈不是 32MB/tick 而是 O(n²)；修完后 32MB/tick 只需 8 个 4MB
   块 ≈11 秒，ServiceTick 1Hz 下分 8 拍完成，每拍 ~1.4s 可接受（后台线程）。
3. **噪音行过滤**：暂不做——修完 O(n²) 后噪音行的成本主要是 raw 归档（SHA/byte 计数，每行 µs 级），
   4MB=59k 行全归档只要 ~1.4s。若后续 raw_event 体积成问题（23 万行/34MB），再按 captain 提议
   「无锚点行降采样」单独处理（涉及 t8 的契约，今天不动）。

## 5. 遗留与建议

- WAL 639KB：t43 已证=正常（A2 写入，21:21 已 checkpoint）。
- 测量资产：`tools/runcollect-verify/T44Bench.cs`（T44_ONLY=1 复跑性能回归）保留；%TEMP% 探针/副本已精确删除。
- **上线动作**：需重启主程序使修复生效（等 captain 决定时机；重启后 A1 34MB 首扫 ~13s、A4 ~2s）。
