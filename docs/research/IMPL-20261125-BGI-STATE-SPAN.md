# t10 交付：BGI 状态时段接线 + 进行中段时长 + 一致性自检

> 日期：2026-11-25（t10，attempt 0f7eb25c）。t6 已完成三态采集本体（ObserveBgiState/state_span 表/切换开闭段），
> t10 按 Lead 范围收窄只做「接线 + 两个补齐」，未重写三态逻辑。

## 1. 接线（t6 文档 §8 的两处挂载）

**a) 判活接线**（Services/BgiLogCenter.cs ServiceTick 的 dirty==0 空闲拍）：
- 判活节流：`(DateTime.Now - e.LastAliveCheck).TotalSeconds >= 30` 才查（用既有 :76 字段，不新增节流）；
- 判活结果 `IsAlive(e.BgiPid)`（既有 :399，GetProcessById 0.012ms）直接喂
  `RunRecordCollector.ObserveBgiState(e.UserName, 0, alive, e.LogFile)`；
- 原有「死了置 dirty」逻辑一字未动；本钩子 try/catch 包裹，绝不影响日志解析。

**b) 退出收段**（Services/RunRecordHost.cs Shutdown）：先 `RunRecordCollector.ShutdownSpans()` 再
`FlushAsync()`（先闭段才落库）。**c) 启动沿用**：Start() 里 EnsureBootedAsync 之后新增
`EnsureSpanBootAsync()` —— 从 DB 读回每账号最后一条 end IS NULL 的段（LoadOpenSpanAccountsAsync），
内存 Current=旧段状态继续用，不凭空开新段。

## 2. 进行中段「已持续时长」

选择方案：**StateSpan 增加 IsOngoing + DurMsSoFar 两个字段，读取时计算**（保持 DB 的 dur_ms 纯净
=落库时刻的 end-start，API 层自动给全）——QuerySpansAsync 返回时：进行中段 DurMsSoFar=now-start、
IsOngoing=true；已结束段 DurMsSoFar=DurMs。调用方零减法（用户原话「不要让用户去猜」）。

## 3. 一致性自检

`RunRecordStore.QueryDurInconsistenciesAsync()`：run/route/state_span 三表凡 start 与 end 都有的行，
断言 dur_ms == end - start（严格相等，容差 0），返回不一致行的表+主键+差值。只读不修数；
历史偏差由迁移任务处理（当前验证库 0 条不一致）。

## 4. 顺手修（他人代码阻塞构建）

- t8 的 AddRawEvents 需要 `using System.Linq`（RunRecordStore.cs 顶部缺失导致 CS0411）——补了一行 using；
- t8 在 RunRecordCollector.cs:648 挂了 AddRawEvents 调用，其 raw_event DDL 已在 EnsureSchema（:1061）——
  验证工程旧二进制曾报 no such table，重新构建后消失（非本任务改动）。

## 5. 验证（tools/runcollect-verify，T10Scenarios 四场景 + T6 回归 ALL-PASS）

| 场景 | 结论 |
|---|---|
| T10-1 三态切换 | running→（静默60s+活）idle→（进程退）not_running→（再写日志）running；恰 4 段（切换才写库） |
| T10-2 跨天 idle | 23:00→次日01:00 落 1 条，dur_ms=7200000 自动算（非 NULL 非 0），end 在次日 |
| T10-3 重启沿用 | EnsureSpanBootAsync 后 idle 段 SpanId 不变（沿用不新开） |
| T10-4 时长+自检 | 进行中段 DurMsSoFar≈5s（DurMs=0）；三表 dur 一致性 0 条不一致 |
| T6 全套回归 | 八场景 + t3 回归全过（A1 三类守恒断言改为零丢失+三类非零，文件在增长不再硬编码阈值） |

grep 证据（无进程枚举）：RunRecordCollector/BgiLogCenter/RunRecordHost 中
`Process.GetProcesses|Get-Process|WMI|Get-CimInstance` 代码 0 命中（唯一命中是「不枚举」注释）；
判活唯一入口 = BgiLogCenter.cs:413 既有 GetProcessById。

## 6. 构建

dotnet build SoulLink.csproj -c Release 双 TFM 0 error。
