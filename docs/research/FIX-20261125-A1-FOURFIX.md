# t44 交付：采集性能 + 时间偏 8h + 假跨日 + raw_text 四修

> 日期：2026-11-25（t44，attempt adad303d）。A1 临时接入（只读）后实测暴露的四连修。

## 1. CPU 200% 满载（O(n²) 锚点盖章）

根因：`RunRecordCollector` 行循环内 `foreach (ev in events) ev.Anchor = AnchorOf(line);` —
每处理一行遍历全部历史事件（O(行×事件)）。实测：4MB 真实 A1 chunk = 61.05s；删除对照 = 1.28s（48×）。
修复：行循环内一次算 `lineAnchorByStart`，事件在循环后按自身 Pos 反查（新 helper `AnchorOfLine`）。
效果：**A1 全文件 519,555 行 Feed=7.4s**（34MB 首扫 ≈ 13s 以内，远低于 2 分钟验收线）；
单 tick（4MB 块）≈1.4s 后台线程，CPU 从 2 核满载降到 <25% 单核。
（MaxBytesPerTick 不动——瓶颈是 O(n²) 不是 32MB/tick；噪音行过滤留 t8 契约。）

## 2. 时间偏 8 小时（墙钟假 UTC）

根因：`ToMs` 用 `(local - UnixEpoch)` 把本地墙钟当 UTC 存——存储值本身没错，但任何按真 UTC
解释的消费方（网页 JS `new Date(ms)`）都 +8h。
修复：`ToMs` 补上 `TimeZoneInfo.Local.GetUtcOffset(local)`，存**真 UTC ms**；`DayOf` 配套反转。
实测（真实 A1 全文件重扫）：**等待至4-00 = 03:53:55 → 04:00:11**（原 11:53:55，+8h 消失）。

## 3. 假跨日 22.5h（回退就 +1 天）

根因：`ResolveTs` 的「回退 1s~12h → +1 天」分支方向反了——真正午夜跨越（23:59→00:00）是回退
~24h（落在 >12h 的重扫分支）；而 1s~12h 回退在增量尾随里几乎总是**乱序行**（BGI 多线程写序），
+1 天造成 end 落次日 → 22.5h 假时长。
修复（新判据）：回退 22~26h = 真跨日 +1 天；回退 <12h = 乱序行按原值（不推进基线）；>26h = 重读
重置基线。另加 `CloseRunLocked` 兜底：end < start 夹齐并注明。
实测：**疑似假跨日(>20h Finished)=0**；最长运行 4.5h（联机锄地-混合精英，合理）。

## 4. raw_text 恒空

根因：`ArchiveRawLine` 硬编码 `RawText = ""`。
修复：存 `line`（按 `RawLineCharLimit` 16K 截断，`RawTruncated` 如实）。实测 A1 抽样 30 条 20 条非空
（其余为真实空行）。

## 5. 验证汇总

- A1 真实全文件（519,555 行）：Feed 7.4s；等待至4-00 03:53:55→04:00:11；假跨日 0；
  最长 4.5h 合理；raw 非空 ✓；
- 4MB 基准：61.05s → 1.41s（44×）；
- 全套回归（T44+T35+T10+T6+t3 含 A4/A1 真实日志回放）ALL-PASS（测试 Ms/Iso 已对齐真 UTC 语义）；
- 双 TFM `-f net11.0-windows` / `-f net11.0-windows10.0.26100.0` 均 0 error。

## 6. 部署注意

需重启主程序生效（captain 已授权停机）。重启后 A1 首扫 34MB ≈13s、A4 ≈2s；网页 JS `new Date`
直接显示本地时间。**历史 24 条 run 是旧「墙钟假 UTC」语义——重扫会以新语义插新行（run_id 含
start_at_ms 自然不同），旧行等待留存清理过期；若需要修正历史行，用 SQL 一次性 UPDATE 减 8h，
建议部署后按需决定。**
