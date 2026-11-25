# t6 交付：路线全量覆盖 + 三类拾取 + 换文件跨天拆段 + BGI 状态时段

> 日期：2026-11-25（t6，attempt 8950c04c）
> 改动：Services/RunRecordCollector.cs（952→约 1560 行）+ Services/RunRecordStore.cs（加列/加表/新 API）。
> BgiLogCenter 扫描/游标一字未改；无新增 DispatcherTimer；无 UI；分段写文件（单次 ≤300 行）。
> 验证：tools/runcollect-verify（T6Scenarios 八场景 + 原 t3 回归）ALL-PASS；双 TFM Release 0 error。

## 1. 新增锚点（全部实测自真实日志，行号见代码注释）

| 层 | 锚点 | 正则（要点） | 用途 |
|---|---|---|---|
| script | → 开始执行地图追踪任务: "X" | `→\s*开始执行地图追踪任务…` | 地图追踪任务整类补齐（A4 L2449 等 10 处）；与同名 route 开窗成对 → 只 Touch 不另开 |
| cd_line | 线路 X.json 开始状态已保存 / 无历史记录，将首次执行 | `线路\s*(…\.json)\s*(?:开始状态已保存\|无历史记录…)` | 激活CD 线路层（8 条全落库） |
| cd_line | 线路 X.json 成功，结束时间 | `线路\s*(…\.json)\s*成功[，,]\s*结束时间` | CD 正常收尾 Finished |
| route | 开始第 x/y 条线路: "X" | `开始第\s*(\d+)/(\d+)\s*条线路[:：]\s*"X"` | 与「进入路线循环」同义 + 携带序号；同名去重防自我挤掉 |
| route | 完成第 x/y 条线路: "X" | `完成第\s*(\d+)/(\d+)…` | 路线正常收尾 |
| run | 共加载 N 条路线 / 上报路线列表: N 条 | `共加载\s*(\d+)\s*条路线\|上报路线列表[:：]\s*(\d+)\s*条` | C) 计划对账 → run.planned_routes |
| pick | 无有效结果，不交互 | `无有效结果\s*[,，]\s*不交互` | no_target（原 100% 丢弃） |
| pick | 识别到"X"，不交互 | `识别到\s*"X"\s*[,，]\s*不交互` | skipped（物品名原话照抄） |

## 2. 状态推断（B：无完成锚点不默认 Finished）

- **结构性边界挤掉**：下一路线/脚本/CD线路/组开始而本条未完成 → 挤掉收尾；
  紧邻错误（HasError）→ **Failed**，否则 **Interrupted**；end=上一事件时刻（推断即边界）。
- **G) 永不因 ERR/拾取/杂项行关窗**——只有结构性开始锚点会走到挤掉路径。
- **Skipped**：锄地「开始第 x/y 条」序号跳跃（idx 跳过中间号）→ 中间路线补 `(跳过 第n/y条)` 行（dur=0）。
- CD 失败：ERR 块归并（HasError）→ 收尾 Failed；原因（终点校验失败）在 err 表 route_id 归属。
- 地图追踪与路线循环同名成对（相隔 ~12 行）→ 同名 Touch，不产生 0ms 假 script 窗。

## 3. 新表与新增列

| 表/列 | DDL 要点 |
|---|---|
| pick_event | (run_id,route_id,kind,item,ts_ms,line_no,line_offset,src_file)；**唯一索引 (run_id,ts_ms,line_no,line_offset,kind)** → M) 幂等含行序号+偏移 |
| state_span | (account,state,start_at_ms,end_at_ms,dur_ms,src_file)；ix(account,start_at_ms) |
| route +3 列 | picked/skipped/no_target（picked=pick_count 语义不变） |
| run +3 列 | planned_routes（C 计划对账）/ day_split（跨天分段标记）/ pick_unattributed（守恒差异） |

## 4. 跨天拆分算法（硬要求三主路径 + 重启次路径）

**主路径 OnFileChanged（换文件=常态）**：
1. 第一段 close：end=当日最后事件时刻（兜底 23:59:59.999）、state=Interrupted、detail='跨天换文件分段(非异常)'、day_split=1、is_cross_day=1。
2. 次日段**惰性开窗**：PendingSplit 记继承组名（O2 核心）；**首个事件到达才 AddRun**，start=chunk 首行时间戳（O3：不要求首行是锚点）；确定性可重扫复现。
3. **90s 兜底**（DaySegmentFirstEventTimeoutMs=90_000）：换文件后无首事件 → 不建次日段（BGI 已停；旧段已 close 无空窗；迟到事件走陈旧 open 行兜底找回）。
4. O4：收益/拾取按段累计（旧段 close 只带当日内存值，新段归零）。
5. O6：两段 run_id 各含各自 start_at_ms 确定性自然键，INSERT OR IGNORE 幂等。

**重启次路径**：OnFileChanged 时钟无基线（LastMs=0，t5 O1 订正）→ **不 close open 行**（等首事件重建基线；组结束走 EndGroup/backfill 继承 start，绝不 ghost）。

## 5. 拾取守恒（H/L：不许静默吞掉）

- 事件层：`sum(pick_event) + OrphanPicks（无 run 窗口孤儿计数）== 日志三类行数`（零丢失，实测 A1: 4618+2907=7525）。
- run 层：run.picks 只由 picked 驱动；Feed 末尾 CheckPickConservation 校验 picks==picked，差异写 pick_unattributed。
- F) 归属不按路线类型筛选：CD 线路/地图追踪/锄地 route 打开期间拾取全计入（专项①实测 ERR 后拾取仍 pick_count=2）。

## 6. BGI 状态时段（P-T）

- 三态：running（事件驱动开段，不额外记产出段）/ idle（静默≥60s 判，**用墙钟 LastFeedWallMs** 而非日志时钟——回放不误判）/ not_running（进程不在）。
- 判活：`ObserveBgiState(user, tsMs, alive, srcFile)` 注入式（调用方复用 BgiLogCenter.Entry.BgiPid+GetProcessById 既有 0.012ms 判活；**本类零进程枚举**，同账号 30s 节流在调用方 LastAliveCheck）。
- R) 只在切换时开/闭段（open 行模型）；S) 未绑定（无 Feed）不产段 + ShutdownSpans() 退出收段；T) dur 写线程算，进行中段 EndAtMs=NULL。
- 采集器内 grep 证明：`Process.GetProcesses|Get-Process|WMI|Get-CimInstance` 0 命中。

## 7. 验证（tools/runcollect-verify，ALL-PASS）

| 场景 | 断言要点 | 结果 |
|---|---|---|
| T6-1 A4 真实回放 | 锄地两轮 10 route 全有名字；CD 8 条（7至冬 Failed+原因入 err）；地图追踪 10 script | PASS |
| T6-2 专项③换文件 | O1-O6 全过（2 条、组名继承、23:59:00→00:00:05 相接、63000 不重复、cross/split=1、src 各异、重扫幂等） | PASS |
| T6-3 90s 兜底 | 无首事件只 1 条当日段 | PASS |
| T6-4 专项③b重启 | 无基线不误关；续跑 1 条 start 继承 23:00 end 次日 01:00，无 ghost | PASS |
| T6-5 专项④时段 | running→idle（墙钟 60s）→not_running；未绑定 0 段；退出全收 | PASS |
| T6-8 专项①构造 | CD→ERR→拾取：pick_count=2、Failed、no_target=1、守恒 2=2、原因入 err | PASS |
| T6-6 专项②A1 | 现算现比（2921/2753/1851）；DB+孤儿=7525=日志三类和（零丢失）；物品名原样 | PASS |
| T6-7 幂等 | 重扫 runs 2/2 routes 32/32 picks 73/73 errs 4/4 | PASS |
| t3 回归 S1-S4 | 换文件 2 段拆分新语义、重启 backfill、真实日志、重扫幂等 | PASS |

## 8. 集成注意（给挂载方）

- 判活接线：宿主（BgiLogCenter 侧）在 Tick 里调 `RunRecordCollector.ObserveBgiState(user, 0, alive, file)`，alive 用既有 IsAlive 结果（30s 节流已有）；未接线时静默仍能切 idle（默认 Alive=false → not_running 也如实）。
- 退出收段：RunRecordHost.Shutdown 时调 `RunRecordCollector.ShutdownSpans()`（一行）。
- 本任务未改 BgiLogCenter/RunRecordHost（硬约束），挂载留给集成任务。
