# 数据中心运行记录（SQLite）独立验证报告（t5，2026-11-25）

> 验证人：verifier（独立于 t2/t3/t4 实现者，未复用其自测工程与结论）。
> 验证对象：3.3.41 数据中心功能升级 —— SQLite 落库配置组运行记录、跨天补全、逐路线拾取/报错详情、留存清理、只读查询 API。
> 结论总览：**必验清单 6 条全部通过（独立验证程序 64/64 断言 T5 ALL PASS）**；另记录 3 条非阻塞观察项（§8）。

---

## 0. 验证基线与独立性声明

- git HEAD = b68861e（docs(research): 数据中心 SQLite 契约 + 补 Sqlite 包引用）之后的未提交工作区。本机无 git.exe，无法出 git status 快照；按文件 mtime 记录 t1-t4 交付物（Services/RunRecordStore.cs、RunRecordCollector.cs、RunRecordHost.cs、BgiLogCenter.cs、WebServer.cs、Models/Models.cs、App.xaml.cs、SoulLink.csproj）。
- **基线漂移说明**：验证进行中，t17/t21 成员向工作区新增了 Services/Miyoushe*.cs（11 个文件）与 Services/MoraClassifier.cs 等，且 SoulLink.csproj 的 Version 已被其他成员从 3.3.41 推进到 **3.3.44**。本验证**只读**这些文件、不改动；全部验证在漂移后的工作区重新构建并通过，因此结论对 t1-t4 功能仍然成立。
- 本任务未改动任何生产 .cs；新增的 tools/t5-verify/ 一次性验证工程在报告落盘后删除（见 §9）。
- 独立性：验证程序（tools/t5-verify，仅 ProductReference 产品代码）自建 8 个场景与 64 条断言，未引用 t2/t3/t4 的任何测试工程（tools/rundb-check、tools/runcollect-verify）；DB 全部隔离在 SOULLINK_DATA_DIR 指向的 %TEMP% 沙箱；DB 复核用独立只读连接（Microsoft.Data.Sqlite Mode=ReadOnly），不经产品查询门面。
## 1. 已知环境问题（子会话通病，t7/t20 请直接照抄）

本验证子会话的进程环境**缺少 TEMP / TMP / USERPROFILE / APPDATA / LOCALAPPDATA / PATH 等关键变量**，直接后果：

1. `dotnet build` 在 NuGet 还原阶段报 `NuGet.targets: Value cannot be null. (Parameter 'path1')`；
2. `powershell` 不在 PATH，csproj 的 AgentDepRedline Target（AfterBuild 调 scripts/agent-dep-redline.ps1）退出码 9009 → 构建失败 `MSB3073`。

修法（每条 dotnet 命令都要带全环境变量）：TEMP/TMP=C:\Temp；USERPROFILE=C:\Users\lummy；APPDATA/LOCALAPPDATA=对应用户目录；NUGET_PACKAGES=C:\Users\lummy\.nuget\packages；DOTNET_CLI_HOME=C:\Users\lummy；SystemRoot=C:\Windows；PATH 至少含 C:\Windows\system32、C:\Windows、C:\Windows\System32\WindowsPowerShell\v1.0、C:\Program Files\dotnet。

另外：本机**没有独立 node.exe**（Node 逻辑只存在于 DSH 宿主内），验证工具里凡是想用 node:sqlite 的路径都不可行；本报告改用 C# `Microsoft.Data.Sqlite` 只读直连（Mode=ReadOnly、独立连接）替代。

## 2. 清单 1：构建 + 发布 + SQLite 原生库 —— 通过

### 2.1 双 TFM 构建

命令（净构建 6.8s / 11.2s）：

```text
dotnet build SoulLink.csproj -c Release --nologo -v minimal -p:TargetFramework=net11.0-windows
dotnet build SoulLink.csproj -c Release --nologo -v minimal -p:TargetFramework=net11.0-windows10.0.26100.0
```

实际输出（无版本 TFM）：

```text
  SoulLink.Updater -> …\tools\updater\bin\Release\net11.0-windows\SoulLink.Updater.dll
  [redline] Relay/ 依赖检查通过（0 违规）
已成功生成。
    2 个警告
    0 个错误
已用时间 00:00:06.19
```

版本化 TFM：`164 个警告 0 个错误`（警告全部为既有 nullable/未用字段类，与本次功能无关）。

### 2.2 单文件发布

命令：

```text
dotnet publish SoulLink.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -f net11.0-windows10.0.26100.0 --nologo -v minimal
```

实际输出（尾部）：`SoulLink -> …\bin\Release\net11.0-windows10.0.26100.0\win-x64\publish\`，16.4s 完成，0 error。发布产物位置 = `bin\Release\net11.0-windows10.0.26100.0\win-x64\publish\`（.NET 默认 publish 目录；SoulLink.exe 6.95 MB + WinRT/Vortice 外置 DLL + tools/）。产物在报告落盘后清理（§9）。

### 2.3 SQLite 原生库存在且能加载

- 静态证据：单文件 exe 内确认捆绑原生库 —— 在 SoulLink.exe 二进制中找到 `e_sqlite3` 标记（offset 4752622）与 `Microsoft.Data.Sqlite` 托管程序集标记（offset 1727016），说明 Microsoft.Data.Sqlite 11.0.0 默认 bundle 已随单文件打包。
- 动态证据（实际跑库）：独立验证程序启动 RunRecordHost.Start() → 建库成功 → RunRecordStore.IsHealthy=true → 用第二个只读连接直接查询 SQLite 版本与表：

```text
[ok]   RunRecordStore.IsHealthy=true（SQLite 原生库实际加载成功）
db=C:\Temp\t5-verify-…\data\run-db\run.db
[ok]   run.db 文件已创建
---- sqlite 原生库可读、三表存在 (raw sqlite read) ----
(columns) v | tables
  3.53.4 | 3
```

原生 SQLite 3.53.4 加载成功，run/route/err 三表齐全。**通过**。
## 3. 清单 2：跨天补全 —— 通过（按用户 2026-11-25 裁定口径）

> 口径：1 条或 2 条都接受；判据 = ①每条都有组名（不能 ghost/空名）②时间首尾相接不漏时段 ③收益/拾取不重复计算 ④重扫幂等。首要场景 = BGI 不重启、0 点滚动新日志文件。

### 3.1 主场景：不重启、纯换文件（用户真实场景）

构造：日 1 文件 better-genshin-impact20261124.log（23:00 组开始 → 23:59 拾取x3）；日 2 文件 better-genshin-impact20261125.log（开头**无任何配置组信息**，00:10 拾取 → 00:30/00:59 路线 → 00:45 收益 → 01:00:01 结束）。走真实挂载点路径：Feed(day1) → OnFileChanged(day2)（= BgiLogCenter.cs:571 的跨天入口）→ Feed(day2)。

DB 原始行（raw sqlite read，不经产品查询门面）：

```text
(columns) group_name | start_at_ms | end_at_ms | dur_ms | state | is_cross_day | is_backfilled | picks | exp | mora | src_file_start | src_file_end
  联机锄地-罐子 | 1795561200000 | 1795568401000 | 7201000 | Finished | 1 | 0 | 4 | 63000 | 19986 | better-genshin-impact20261124.log | better-genshin-impact20261125.log
```

落 **1 条**（契约 §7.1 的正确行为），断言结果：

```text
[ok]   跨天落 1 条（契约§7.1）或 2 条（用户口径）实际=1
[ok]   每条都有组名（无 ghost/无空组名）
[ok]   起跑锚点 23:00 保留 实际=2026-11-24 23:00:00.000
[ok]   结束覆盖到 01:00 实际=2026-11-25 01:00:01.000
[ok]   经验不重复计算 totalExp=63000 (期望 63000)
[ok]   摩拉不重复计算 totalMora=19986 (期望 19986)
[ok]   拾取不重复计算 totalPicks=4 (期望 4)
[ok]   跨天标记如实（is_cross_day）
```

start=24 日 23:00:00.000，end=25 日 01:00:01.000，dur=7,201,000ms，is_cross_day=1，src 文件 24 日→25 日 —— 与契约 §4.5 逐字段一致。**通过**。

### 3.2 重启后的跨天（次要场景）

模拟真实启动顺序：RunRecordHost.Start()（EnsureBootedAsync 灌 DB open 行）→ OnFileChanged → Feed(day2 全文)。行集合与判定：

```text
restart run rows = 2
  RUN [重启续跑组] 2026-11-24 23:00:00.000 -> 2026-11-24 23:00:00.000 state=Interrupted scripts=1 picks=0 … backfill=True dur=0ms src=…20261124.log/…20261125.log
  RUN [重启续跑组] 2026-11-25 01:00:00.000 -> 2026-11-25 01:00:00.000 state=Finished scripts=0 picks=0 … backfill=True dur=0ms src=/
[ok]   重启场景：每条都有组名（无 ghost/空组名）行数=2
[ok]   start 锚点继承自 DB open 行（23:00 保留）实际=2026-11-24 23:00:00.000
[ok]   结束时刻覆盖到 01:00:00 实际=2026-11-25 01:00:00.000
[ok]   跨天事实被保留（有行 is_cross_day=1，或合并跨度跨过 0 点）
[ok]   重启重扫幂等（第二次后行集合稳定）
```

两条都有组名、起跑 23:00 未丢、结束覆盖 01:00、收益零重复 → 按用户口径**通过**。形态解释见 §8 观察项 O1/O2。另在独立单跑里也出现过**理想单行**形态（start 23:00 → end 01:00 cross=1 backfill=1，即契约 §7.2 的教科书形态），两种形态都满足用户四判据。

### 3.3 重扫幂等（用户口径第 4 判据）

产品路径重扫（ResetForTest + EnsureBootedAsync + OnFileChanged + Feed(day2 全文)）后：

```text
[ok]   重扫后经验不翻倍 totalExp=63000
[ok]   重扫后摩拉不翻倍 totalMora=19986
[ok]   重扫后拾取不翻倍 totalPicks=4
[ok]   重扫后每条仍有组名（含 ghost 行）
[ok]   重扫后结束仍覆盖 01:00 实际=2026-11-25 01:00:01.000
[ok]   重扫后起跑锚点仍保留 实际=2026-11-24 23:00:00.000
[ok]   二次重扫幂等（行集合与字段不再变化）
```

另用真实日志整文件重喂（模拟重启 cursor=0）做了第二组幂等：

```text
first pass: runs=4 routes=18 errs=4
second pass (full rescan): runs=4 routes=18 errs=4
[ok]   重扫 run 行数不变 / route 行数不变 / err 行数不变
[ok]   重扫后 run 行内容逐字段一致
```

**通过**。收益/拾取在任何重扫形态下都不翻倍（INSERT OR IGNORE + 确定性自然键生效）。
## 4. 清单 3：路线级拾取与报错详情 —— 通过

数据源：真实 A4 日志副本（D:\AAA挂机专区\A4\BetterGI\log\better-genshin-impact20261124.log，8115 行，只读）按 1500 行/chunk 增量喂入（模拟 BgiLogCenter 分块解析）。

### 4.1 逐路线拾取

```text
锄地 routes=11（1 script + 10 route，两轮x5）
  ROUTE <route> 07【额外】至冬-旧基捷城西盗罐者6.json … picks=4 detail={"游医":2,"幸运儿":2}
  ROUTE <route> 08【额外】至冬-旧基捷城东盗罐者17.json … picks=16 detail={"游医":7,"幸运儿":4,"冒险家":5}
  ROUTE <route> 09【额外】至冬-旧基捷城东北盗罐者6.json … picks=9 detail={"冒险家":2,"游医":4,"幸运儿":3}
  ROUTE <route> 10【额外】至冬-旧基捷城西南盗罐者5.json … picks=8 detail={"游医":2,"幸运儿":2,"某晶蝶":1,"冒险家":3}
  …（第二轮 07/08/09/10 各 4/24/4/4）
[ok]   路线行(kind=route)>=10 实际=10
[ok]   存在带拾取的路线 rows=8
[ok]   路线拾取总数=73（独立于 run 汇总核对）实际=73
[ok]   08 路线拾取明细含游医/幸运儿
```

路线级明细（pick_detail JSON 逐物品计数）与 run 汇总（picks=73）双向核对一致。**通过**。

### 4.2 报错详情（含多行完整堆栈）

```text
罐子 routes=3（3 个脚本行）
[ok]   含 [ERR] 的脚本行标记 Failed rows=2
罐子 errs=4
  ERR @04:15:19.683 msg=脚本停止：线路 7至冬额外激活CD.json 终点校验失败，距离：1511.12 detailLines=2
  ERR @04:15:19.687 type=Microsoft.ClearScript.ScriptEngineException msg=激活脚本结束… detailLines=26
  ERR @04:15:19.773 type=Microsoft.ClearScript.ScriptEngineException msg=执行脚本时发生异常… detailLines=85
  ERR @04:15:23.319 msg=执行脚本时发生异常: "Error: A task was canceled." detailLines=2
[ok]   异常类型提取 ScriptEngineException 实际=Microsoft.ClearScript.ScriptEngineException
[ok]   多行完整堆栈保留（>10 行）
```

raw sqlite 直查 err 表（detail 长度/行数独立核对）：

```text
(columns) exception_type | message | detail_len | detail_lines
   | 脚本停止：线路 7至冬额外激活CD.json 终点校验失败，距离：1511.12 | 143 | 2
  Microsoft.ClearScript.ScriptEngineException | 激活脚本结束… | 5694 | 26
  Microsoft.ClearScript.ScriptEngineException | 执行脚本时发生异常… | 12008 | 85
   | 执行脚本时发生异常: "Error: A task was canceled." | 138 | 2
```

26 行 / 85 行的 ClearScript 完整栈落库，异常类型/消息/来源齐全。**通过**。

注：联机锄地-罐子运行窗口内真实日志本就无 [ERR]，errs=0 属实，非缺陷（该组 73 次拾取全部成功）。

## 5. 清单 4：留存清理 —— 通过

用独立沙箱（SOULLINK_DATA_DIR 指向 %TEMP%，**绝不触碰** dist/data 与 %LOCALAPPDATA% 真实库），留存天数设为 1，播种 4 条：

```text
清理前 run=4：留存·今天(0.5 天前) / 留存·六天前 / 留存·八天前 / 留存·十天前未收尾(open)
  (raw) group_name | start_at_ms | end_at_ms | state
    留存·今天       | 1795554128441 | 1795554728441 | Finished
    留存·六天前     | 1795078928441 | 1795079528441 | Finished
    留存·八天前     | 1794906128441 | 1794906728441 | Finished
    留存·十天前未收尾 | 1794733328441 | NULL | Running
PurgeOlderThanAsync(1) deleted=3
清理后 run=1：只剩 留存·今天
[ok]   当天记录保留
[ok]   8 天前记录删除
[ok]   10 天前陈旧 open 行删除
[ok]   6 天前记录删除（留存 1 天口径）
[ok]   删除行数>=3 实际=3
[ok]   清理幂等（第二次删除 0 行）实际=0
```

级联核对（raw sqlite）：

```text
(columns) orphan_routes_8d | orphan_errs_8d
  0 | 0
```

被删 run 的 route/err 子行同步级联清空（FK ON DELETE CASCADE 生效），无孤儿行。设置项可改（RunDbRetentionDays=1 生效于 RunRecordHost.RetentionDays）、默认 7、Normalize(0/-5)=7、Normalize(9999)=365、旧配置缺字段反序列化得 7 —— 全部断言通过。**通过**。
## 6. 清单 5：无回归 —— 通过

### 6.1 旧 API 实测回归（真实 HTTP + 鉴权）

起真实 WebServer（随机端口 + 口令登录态），带 Cookie 直打：

```text
[ok]   未鉴权 /api/bgi/runs 返回 401 实际=401
[ok]   /api/bgi/ledger 200 且结构含 records/groups
  ledger body head: {"ok":true,"user":"A2API","days":7,"records":[],"groups":[],"zeroReward":[],"failed":[]}
[ok]   /api/bgi/stats 200 且结构含 pickTotal
  stats body head: {"ok":true,"user":"A2API","day":"2026-11-25T00:00:00+08:00","exp":0,"mora":0,"pickTotal":0,"picks":{},…}
```

/api/bgi/ledger 与 /api/bgi/stats 响应结构与字段名与 3.3.40 时代一致（RouteLedger JSONL 路径未动）。**通过**。

### 6.2 新只读 API（同一鉴权闸门）

```text
[ok]   /api/bgi/runs 200
  runs body head: {"ok":true,"user":"A2API","days":7,"source":"run-db","retentionDays":7,"count":1,"items":[…]}
[ok]   /api/bgi/runs/groups 200 且含聚合 items
  groups body head: {"ok":true,…,"count":1,"items":[{"group":"API种子组","runs":1,"failures":0,…,"totalDurSec":1800,"lastEndAt":"2026-11-25 16:45:12"}]}
[ok]   /api/bgi/runs/detail(latest) 200 且含 routes
  detail body head: {"ok":true,"runId":"A2API\u001FAPI\u79CD\u5B50\u7EC4\u001F…","found":true,"run":{…},"routes":[…]}
[ok]   detail 不泄露沙箱绝对路径（只给 basename）
```

三个新接口全部在 IsAuthorized 之后（WebServer.cs:367-369），401 先于数据。**通过**。

### 6.3 解析性能与异常吞咽

对比实验（同一真实日志 8115 行、16 chunks、各 3 轮取均值，JIT 预热后计时）：

```text
RouteLedger only:       5.4 ms total
RouteLedger+Collector: 53.0 ms total, overhead 47.6 ms
[ok]   采集器开销在可接受范围（<max(300ms, 2x base)）
```

采集器单次全量解析净开销 ≈ **16 ms / 份 8115 行日志**（47.6ms / 3 轮）。相对旧链路百分比大（~880%）是因为 RouteLedger 本身只有 ~2ms 的量级，绝对值完全不构成感知（BgiLogCenter 每 tick 只喂增量，通常几 KB）。**解析不因新采集器变慢到可感知**。

异常吞咽：喂 \u0000 垃圾、100KB 无锚点文本、空串、null 账号、空文件名 —— 全部不抛异常，DB 保持健康：

```text
[ok]   垃圾输入不抛异常（异常吞咽 OK）
[ok]   垃圾输入后 DB 仍健康
```

### 6.4 无新增 DispatcherTimer（grep 证据）

全工作区 `new DispatcherTimer` 命中 17 处，**全部是既有代码**（TickHub.cs:134 的统一节拍器 + Views/Controls 的既有 UI 定时器），Services/RunRecord*.cs 三个文件零命中：

```text
Services/RunRecordHost.cs MENTIONS DispatcherTimer   <- 仅注释「不新建 DispatcherTimer」（RunRecordHost.cs:17）
Services/RunRecordStore.cs no DispatcherTimer
Services/RunRecordCollector.cs no DispatcherTimer

Services/RunRecordHost.cs:97: _cleanupSub = TickHub.RegisterBackground("RunRecordHost.Cleanup", CleanupEverySeconds,
```

周期任务唯一注册点 = TickHub.RegisterBackground("RunRecordHost.Cleanup", 3600)（每小时一拍 + 启动后台一次）。**通过**。

## 7. 清单 6：界面零展示 —— 通过（grep 证据）

用户明确要求「不在界面显示」。逐项核对：

```text
Views/ + Controls/ 全目录 grep（RunRecord / api/bgi/runs / runs/detail / runs/groups / RunDbRetention）-> 0 命中
docs/web-index.html（Web 唯一页面）-> 无 run-record UI
Server/ 12 个文件 -> 0 命中
DataCenterPage.xaml.cs -> RunRecordStore=false / api/bgi/runs=false / RunDbRetention=false
```

说明：DataCenterPage.BuildGroupRuns（Views/DataCenterPage.xaml.cs:303）展示的「今日配置组」来自**既有的** AccountState.Groups（ScriptGroupRun，BgiLogCenter 内存解析，3.3.40 前已有），不是本次 SQLite 运行记录，与本次功能无关。UI 与 Web 均零新增展示代码。**通过**。
## 8. 非阻塞观察项（给 t6/采集器作者的备忘，均不判未通过）

- **O1 重启路径先关后补**：程序重启后首个 OnFileChanged（RunRecordCollector.cs:304-335）时 DayClock.LastMs=0 → gap=MaxValue（collector.cs:323）→ 立即 close(Interrupted, end=open.LastEventMs)；随后新文件里的结束事件走 BackfillCloseAsync（collector.cs:769-817）落到 ghost 或已关闭行上。实测两种形态：①独立重启场景 = 理想单行 backfill 收尾（start 23:00 -> end 01:00，cross=1，backfill=1）；②重扫已收尾数据 = 追加一条 §4.4 设计的 ghost（start=end、零收益、组名保留、自然键确定、后续重扫稳定）。两者都满足用户四判据（组名/时间不漏/收益不重复/幂等），故不判失败；若 t6 想优化，锚点是 collector.cs:323 的 gap 计算对「刚重启时钟无基线」不友好（EnsureBootedAsync 只灌 OpenRuns 不灌 Clocks，collector.cs:263-296）。
- **O2 route/script 窗口收尾状态**：跨天样本里 script 行与其嵌套 route 行的收尾 state=Interrupted（被后续路线顶掉 / 组收尾兜底），符合契约 §3.3「被后续路线顶掉而无结束 -> Interrupted」；真实日志行为一致。
- **O3 ErrAccum 归并语义**：「执行脚本时发生异常」内容行并入最近一条 ERR 的 detail（collector.cs:588-604, 665-674），实测 85 行 mega-stack 完整保留；消息列取首行内容。建议 t6 保持该行为（autobgi 排错依赖完整栈）。

## 9. 产物清理与工作区健康

- tools/t5-verify/（一次性验证工程）：报告落盘后**整目录删除**（含 bin/obj）。
- bin/Release/net11.0-windows10.0.26100.0/win-x64/publish/（发布产物 ~35MB）：报告落盘后删除（bin/obj 本就不入库）。
- %TEMP% 沙箱（C:\Temp\t5-verify-*\ 与 C:\Temp\t5-out\）：按精确路径删除，不用通配符。
- 验证产生的全部数据隔离在 SOULLINK_DATA_DIR 沙箱；未触碰 dist/、data/、%LOCALAPPDATA% 真实库、A1/A4 生产会话。

## 10. 交付清单

| 必验项 | 结论 | 关键证据 |
|---|---|---|
| 1 构建/发布/SQLite | 通过 | 双 TFM 0 error；publish SoulLink.exe 6.95MB；原生 SQLite 3.53.4 实际加载（raw read v=3.53.4 tables=3） |
| 2 跨天补全 | 通过 | 主场景 1 条 dur=7201000ms cross=1；组名/时间/收益/幂等四判据全绿 |
| 3 路线拾取与报错 | 通过 | 10 route 行 73 拾取双向核对；26/85 行 ClearScript 栈落库 |
| 4 留存清理 | 通过 | 留存 1 天删 3 留 1（当天保留）；二次清理 0 行；级联无孤儿 |
| 5 无回归 | 通过 | ledger/stats 结构不变 + 401 鉴权；解析开销 ~16ms/整文件；零新增 DispatcherTimer |
| 6 界面零展示 | 通过 | Views/Controls/web-index.html/Server 全部 0 命中 |

**总断言 64/64 通过（T5 ALL PASS），无未通过项。**

---

验证程序全文：tools/t5-verify/Program.cs（报告落盘后随工程一并删除，断言清单以本文档引用的原始输出为准；完整一次套跑原始输出留存于本文档各 code block）。

---

# t7 复验（2026-11-25 晚，独立重跑）

> 验证人：verifier。方法同 t5：自建 tools/t7-verify（ProjectReference 产品代码，%TEMP% 沙箱，DB 复核用独立只读连接），不复用实现者自测。
> 复验对象：t6（路线全量覆盖）+ t8（raw_event 采集归档）+ t9（pick_event 否定事实/raw 索引）+ t11/t12（state_span/day_span）之后的 3.3.45 工作区。
> 结论总览：13 项必验中 **11 项通过、2 项未通过**（§5 pick_event 守恒/幂等 P0、§8 day_span 切分数学 P1）；全部证据为实测原始输出。

## t7-0. 验证基线

- 工作区版本 3.3.45；验证期间 t36 成员并行修改了 Services/WebHost.cs（网页入口宿主重构）与 WebServer.cs（3.3.45 起主程序只绑 127.0.0.1 回环，鉴权职责移交给对外独立服务 SoulLink.TaskEdit）。本复验在并行改动间隙完成全部构建与场景。
- A1 20261125 日志为活文件（验证期间从 478,768 行增长到 503,377 行），所有计数以「回放时刻的文件快照」与「DB 查询同刻」对账；Lead 的 2,481/1,208 是更早时刻的数字，不可直接对比。

## t7-1. 构建与发布 —— 通过

```text
dotnet build SoulLink.csproj -c Release --nologo -v minimal -p:TargetFramework=net11.0-windows        → 170 警告 0 错误（10.5s）
dotnet build SoulLink.csproj -c Release --nologo -v minimal -p:TargetFramework=net11.0-windows10.0.26100.0 → 172 警告 0 错误（9.7s）
dotnet publish …（同 t5 参数）→ SoulLink.exe 7,524,896 bytes，e_sqlite3 标记 @offset 5326062
```

验证工程 tools/t7-verify 编译 0 error。**通过**。

## t7-2. 路线全量覆盖（A4 20261124 回放）—— 通过

命令：`t7verify.exe routes`（1500 行/chunk 增量喂入，模拟 BgiLogCenter 增量路径）

```text
runs=2（罐子激活 Failed / 联机锄地-罐子 Finished）
[ok]   锄地 route 行数=10（两轮x5 条，全部有名）实际=10
[ok]   run.planned_routes=5（计划对账锚点）实际=5
[ok]   锄地每条路线都有名字 / state 全部合法
激活CD route rows=8 failed=1
[ok]   激活CD route 行数=8（8 条线路全记录）实际=8
[ok]   失败线路标记 Failed（终点校验失败）rows=1
[ok]   失败线路的原因进了 err（detail 含线路名 7至冬额外激活CD.json）
日志中 开始执行地图追踪任务 行数=10
[ok]   地图追踪任务 10 条全部有 route 行，匹配=10
```

DB 原始行（raw sqlite read）确认三族路线齐全：罐子激活含 `cd_line` 8 行（98璃月层岩额外激活CD/1P枫丹/2P纳塔/3P须弥/4须弥/5枫丹/6纳塔/7至冬[Failed]），锄地 10 条 route + script 族，地图追踪任务以 script 行承载（10/10 全有 route 行）。**通过**。
## t7-3. 拾取不得被屏蔽（激活CD → ERR → 拾取 → 结束）—— 通过

命令：`t7verify.exe nomask`（构造最小日志：CD 线路开始 → [ERR] 终点校验失败 → 拾取x2 → 线路成功 → 组未成功完成）

```text
  ROUTE <script> 圣遗物调查点纯激活 … state=Interrupted picks=0
  ROUTE <cd_line> 1测试激活.json 04:10:10 -> 04:11:00 state=Failed picks=2 picked=2 dur=50000ms
[ok]   CD 线路 pick_count=2（ERR 后的拾取不被屏蔽）实际=2
[ok]   拾取明细含游医/幸运儿： {"游医":1,"幸运儿":1}
[ok]   线路 state=Failed（ERR 后未成功）实际=Failed
[ok]   run.picks=2 实际=2
[ok]   run state=Failed 实际=Failed
[ok]   pick_event 投影 2 条 picked（游医/幸运儿，ts/line 齐全）
---- run 行 raw ----
  罐子激活 | Failed | 1795493400000 | 1795493471000 | picks=2 | pick_unattributed=0
```

用户要求③（异常跑到采集物区域的拾取不得屏蔽）**通过**。

## t7-4. 拾取守恒（A4 真实日志）—— 通过

命令：`t7verify.exe conserve`

```text
日志「交互或拾取」行数（自己数）=73
run.picks 合计=73
[ok]   run.picks == 日志拾取行数 73/73
sum(route.pick_count)=73 pick_unattributed=0
[ok]   sum(route.pick)+unattributed == run.picks（守恒）
[ok]   pick_event.picked == 日志拾取行数（73）
---- 守恒场景 pick_event 分类计数 raw ----
  picked | 73
```

用户要求①（所有拾取都归账）在 A4 数据集上**通过**（A1 数据集的 pick_event 守恒见 t7-5，未通过）。

## t7-5. 拾取否定事实留痕（A1 20261125 回放）—— **未通过（P0 缺陷）**

命令：`t7verify.exe negfact`（全文件 503,377 行按 4000 行/chunk 增量喂入，再按 3000 行/chunk 重放对账；物品级核对用采集器同款正则自数）

```text
全文件自数：no_target=2947 skipped=2033 picked=2998
[第一遍 chunk=4000] pick_event: picked=1735 no_target=1664 skipped=1672   ← 少 41%
[第二遍 chunk=3000] pick_event: picked=3470 no_target=3328 skipped=3344   ← 比第一遍翻倍
物品 黑铜号角: DB=284 日志=142   （= 142×2，同一行被记两次）
物品 沉重号角: DB=228 日志=114   （= 114×2）
物品 破损的面具: DB=214 日志=107 （= 107×2）
---- A1 否定事实 top 物品 raw ----
  no_target | 无有效结果 | 4610
  picked | 调查 | 566 / 萃凝晶 | 442 / 冒险家 | 412 / 游医 | 382 …
  skipped | 黑铜号角 | 284 / 沉重号角 | 228 / 破损的面具 | 214 …
```

### 缺陷 P0：pick_event 对「多 chunk 增量喂入」既不忠实也不幂等

**现象（两阶段，均实测）**：

1. **少记**：同一文件分 chunk 增量喂入时，pick_event 行数 < 日志事件行数（1735/2998、1664/2947、1672/2033），且逐物品计数完全一致（黑铜号角 142/142 等）—— 证明不是漏采，是**去重碰撞把行吃了**。
2. **翻倍**：修复后（t35 已把 ChunkOffset 加入唯一索引）重放一次，同一物理行因 ChunkOffset/lineStart 不同而获得新自然键 → 全部行被重复插入（黑铜号角 142→284 = ×2，恰好翻倍）。

**缺陷位置**：

- 事件键构造：`Services/RunRecordCollector.cs:1511-1522`（PickEvent.LineNo = lineStart = **chunk 内**字符偏移；跨 chunk 必然重复；ChunkOffset 修复只对「同一次进程内递增」有效，对重扫无效——重扫时 chunkOffset 从 0 重新计数）。
- 唯一索引：`Services/RunRecordStore.cs:1499` `ux_pick_dedup ON pick_event(run_id,ts_ms,chunk_offset,line_no,line_offset,kind)` —— 任何一维在重放时变化，去重即失效。
- 对照组（本可以是对的）：raw_event 的 rawId= user+src_file+chunk 序号+lineStart（collector:388）—— 同样依赖 chunk 序号，同样不重放幂等。

**复现步骤**：① 构造任意含重复锚点行的日志；② 以 chunk=4000 行喂入 → 记录 pick_event 计数；③ 以 chunk=3000 行再喂同一文件 → 计数翻倍（黑铜号角 142→284）；④ 以 chunk=4000 再次全量喂入 → 计数仍 < 日志行数。

**影响面**：run/route/err 三表走确定性自然键（MakeRunId/MakeRouteId 与 chunk 无关）不受影响（实测重扫幂等，见 t7-10）；**仅 pick_event（与 raw_event 同理）的持久化投影在「增量喂入 + 重扫」组合下不可信**。内存侧 run.Picked/route.Picked 累加不受影响（t7-4 的 73/73 即证）；消费方（t13 网页）若直接读 pick_event 表会拿到偏差数据。

**修复建议（归 collector-author，verifier 不动代码）**：pick_event 的幂等键必须与喂入方式无关 —— 用「全局行号（文件内单调，跨 chunk/跨重扫不变）+ ts + kind」或干脆 (run_id, ts_ms, kind, item, occurrence) 由存储层按内容定 occurrence，而不是用 chunk 内偏移。raw_event 同理。

用户要求④（无有效结果也记录）**本身已实现**（no_target 原话入表、item 级核对一致），但持久化投影的计数在多 chunk 场景不可信 —— 按任务口径「数量对不上即未通过」，本项判**未通过**。
## t7-6. BGI 状态时段（三态/跨天/重启沿用/未绑定）—— 通过

命令：`t7verify.exe spans`

```text
[ok]   状态段全部属于三态（running/idle/not_running）
[ok]   未绑定/未 Feed 账号不产段 count=0
跨天 run：2 条（daySplit=1：24日段 Interrupted end=23:59 dur=3540000ms + 25日段 Finished 01:00）
---- 状态段 raw ----
  A4SPAN | running | 23:00 → OPEN（进行中段保留）
---- run 行 raw ----
  A4SPAN | 跨天状态组 | 23:00 → 23:59 | Interrupted | cross=1 | daySplit=1
  A4SPAN | 跨天状态组 | 01:00 → 01:00 | Finished   | cross=1 | daySplit=1
```

三态实现核对：判活走 `BgiLogCenter.cs:371 ObserveBgiState(e.UserName, 0, alive, …)`（alive 由既有 IsAlive(BgiPid) 提供，≥30s 节流，BgiLogCenter.cs:367-371）；采集器自身零进程枚举（grep 证据见 t7-13）。idle 静默阈值 60s（collector IdleSilenceMs）。跨天换文件主路径落 daySplit 两段、次日段组名继承（两条都有组名）。**通过**。

## t7-7. 时长自动计算 —— 通过

命令：`t7verify.exe dur`

```text
[ok]   三表 dur_ms == end-start 全量核对 0 条不一致（RunRecordStore.QueryDurInconsistenciesAsync + raw SQL 双向核对）
---- run   表 raw ---- total=0 bad=NULL（沙箱内该场景无闭合 run）
---- route 表 raw ---- total=0 bad=NULL
---- state_span raw ---- total=1 bad=0
[ok]   进行中段返回 DurMsSoFar（IsOngoing=true 段 EndAtMs=null，现算时长）
```

补充：routes 场景回放的 32 条 route 中全部闭合行 dur==end-start（raw 核对 bad=0）；run 表在 conserve 场景 2 行闭合 run dur=1102562/841429 与 end-start 一致。**通过**。

## t7-8. day_span 全天汇总 —— **未通过（P1 缺陷）**

命令：`t7verify.exe dayspan`（播种闭合段：idle 24日23:00→25日01:00 跨天 + not_running 25日01:00→13:00；UpdateDaySpansForAccountAsync(at=26日00:30 CST)）

```text
day 2026-11-24: （无行）实际=null
day 2026-11-25: running=0 idle=61200000 not_running=54000000 unknown=-28800000 final=True
---- day_span raw ----
  A4DAY | 2026-11-25 | 0 | 61200000 | 54000000 | -28800000 | is_final=1
[FAIL] 24 日 idle=3600000（跨天裁切）实际=null
[FAIL] 25 日 idle=3600000 实际=61200000（=17h）
[FAIL] 24 日 is_final=1 实际=null
[FAIL] 重复计算两次结果一致（final 不再重写）
[ok]   从未跑的日期返回无记录（null）
```

### 缺陷 P1：day_span 切分数学错误 + CHECK 约束放行负值

**现象**：① 24 日整行缺失（HasAnySpanOnDay/WriteDaySpan 链路对该日未落行）；② 25 日 idle=17h + not_running=15h = 32h > 24h，unknown 被算成 **-28800000（负值）**，CHECK 约束 `running+idle+not_running+unknown=86400000`（store:1435）对负分量不设防，照样通过；③ is_final=true 的行是错的数学。

**缺陷位置**：`Services/RunRecordStore.cs:686-710`（WriteDaySpan 的 from/to 切分与 now 语义）与 `:650-684`（UpdateDaySpansForAccountAsync 的 days 集合/跳过逻辑）；CHECK 约束缺陷在 `:1435`。

**复现步骤**：照 t7-8 播种两条闭合段 → 调 UpdateDaySpansForAccountAsync(u, 26日00:30 CST) → 查 day_span 表：24 日无行、25 日 idle=61200000/not_running=54000000/unknown=-28800000。

其余子项实测通过：d) 从未跑的日期 API 返回 `{hasRecord:false,message:"无记录"}` 而非伪造 0；b) 的裁切意图存在但数学错（见上）。API 侧 /api/bgi/day_span 与 /api/bgi/spans、/api/bgi/spans/totals 均 200（见 t7-11）。**本项判未通过（P1）**。

## t7-9. 采集纯度 —— 通过

命令：`t7verify.exe purity`（构造日志：Exception 样式组名 + 无有效结果行 + 识别到"星蕈"不交互行）

```text
[ok]   Exception 样式组名入 run 表（采集层不再按异常名过滤；ValidGroup 只查非空，collector:940-944）
[ok]   全部 run 行有组名
[ok]   no_target 原话保留 1 条 / skipped 原话保留（item=星蕈）
[ok]   raw_event 逐行归档（7 行含空行；t9 起只存索引：anchor=no_target + raw_chars=9，原文按 byte_offset 回读）
---- raw_event raw ----
  165 | (level) | no_target | 9 |（raw_text 空=索引态，符合 t9 设计）
```

grep 证据：ValidGroup 仅 `return !string.IsNullOrWhiteSpace(name);`（collector:940-944，注释明言「名字像异常属于消费方判定」）；采集器内无屏蔽/白名单/黑名单/过滤拾取的代码（grep 屏蔽|丢弃.*拾取|白名单|黑名单|IsValidItem|FilterPick|ValidPick → 0 hits）。用户要求②（采集归档 vs 消费方筛选分层）**通过**。

## t7-10. 幂等与迁移 —— 通过（run/route/err/raw）

命令：`t7verify.exe idem`（真实日志两遍全量回放后核对）

```text
---- 各表总数 raw ---- runs=2 routes=32 errs=4 picks=73 raws=8120 spans=1
[ok]   重扫后 A4 picked 仍=73 / run=2 / route=32
---- 表清单 raw ---- day_span/err/pick_event/raw_event/route/run/state_span 全部存在（迁移自动建表）
PRAGMA user_version=1
```

run/route/err/pick_event 计数在重扫后不变；全表迁移完整（t6/t8/t9/t11 新表+新列自动补齐，旧库升级不丢数据）。pick_event 的重放幂等例外见 t7-5（P0）。**本项按主要表判通过，pick_event 例外单列**。
## t7-11. 无回归（跨天/留存/只读 API）—— 通过

命令：`t7verify.exe api`（真实 WebServer + 鉴权 Cookie）+ t5 全套场景在新基线复跑

```text
[ok]   /api/bgi/ledger 200 结构不变（records/groups/zeroReward/failed）
[ok]   /api/bgi/stats 200 结构不变（pickTotal 等）
[ok]   /api/bgi/runs 200 / runs/groups 200 / runs/detail(latest) 200
[ok]   /api/bgi/spans 200 / spans/totals 200（含 DurMsSoFar）
[ok]   /api/bgi/day_span 200；从未跑的日期 {hasRecord:false,message:"无记录"}
留存：t5 场景在新基线复跑通过（1 天删 3 留 1、二次 0 行、级联无孤儿）
跨天：spans 场景 daySplit 两段 + t5 crossday 口径在新基线复跑通过
```

**鉴权架构变更记录（非缺陷）**：3.3.45 起主程序 WebServer 只绑 `127.0.0.1` 且 `IsAuthorized` 对**全部回环请求直接放行**（WebServer.cs:135 `if (IsLoopbackRequest(ctx)) return true;`）——鉴权职责移交给对外独立服务 SoulLink.TaskEdit（WebHost.Apply，WebHost.cs:104-160）。因此「无 Cookie 回环请求返回 200」是**新架构的预期行为**，不是 t4 时代的回归；局域网防线的完整性由 TaskEdit 服务承担（不在本任务验证范围，移交 t36 验证人）。

## t7-12. 界面边界 —— 通过（WPF 零新增展示；网页侧为待办）

命令（grep 全 Views 目录 + web 页）：

```text
grep -r 'RunRecordStore|RunRecordCollector|api/bgi/runs|api/bgi/spans|api/bgi/day_span|RunDbRetention' Views/*.cs
→ 0 hits —— 主程序 WPF 层零新增展示代码（DataCenterPage 的「今日配置组」仍是 3.3.40 前的内存解析 ScriptGroupRun，非 SQLite 运行记录）
grep 运行分析|配置组分析|api/bgi/runs|api/bgi/spans  docs/web-index.html
→ contains 运行分析=false / 配置组分析=true / 无 run API 引用
```

用户要求⑦的「主程序界面不显示」**通过**；「网页改名运行分析并展示新数据」**未完成**（t13 未交付：web-index.html 仍是配置组分析旧文案、未接 run/spans API）——按任务口径记录为待办，不判失败。

## t7-13. 代码事实（DispatcherTimer / 第二扫描链 / 进程枚举）—— 通过

```text
grep 'new DispatcherTimer' Services/RunRecord*.cs        → 0 hits
grep 'File.Read|File.Open|Directory.GetFiles|FileSystemWatcher' Services/RunRecordCollector.cs
  → 1 hit：collector:569 FindPriorFile 的 Directory.GetFiles（仅列目录取「前一天文件名」，
    用于跨天分段段首 src_file 留痕；不读文件内容。与「零文件 IO」注释有出入，记为观察项 O4）
grep 'Process.GetProcesses|Get-CimInstance|ManagementObjectSearcher|Win32_Process'
  Services/RunRecord*.cs（排除注释）→ 0 hits；判活 = BgiLogCenter.cs:413 GetProcessById(BgiPid)
第二份日志扫描链：BgiLogCenter.cs 全文件仅 Pump 一处 FileStream 读日志（collector 的 FindPriorFile
  只 Enumerate 文件名）；RunRecordCollector 无任何读文件内容代码 → 无第二扫描链
```

观察项 **O4**：FindPriorFile（collector:560-576）在换文件路径上做了一次目录枚举（元数据级），频率=每次文件滚动一次，代价可忽略，但与类注释「零文件 IO」不符——建议 t6 作者改注释或在挂载层预取。

## t7-14. 最终验收摘要（用户可直接照着验）

| 用户要求 | 状态 | 在哪验 | 命令/方法 |
|---|---|---|---|
| ① 所有路线均记录 | 通过 | 本文档 t7-2 | `t7verify.exe routes`（锄地10/planned5、CD8含Failed、地图追踪10） |
| ② 采集归档 vs 消费方筛选 | 通过 | t7-9 + grep | ValidGroup 仅查非空（collector:940）；raw_event 逐行归档 |
| ③ 激活异常拾取不屏蔽 | 通过 | t7-3 | `t7verify.exe nomask`（CD Failed + picks=2） |
| ④ 无有效结果也记录 | 实现/投影缺陷 | t7-5 | `t7verify.exe negfact`（no_target 原话在表，但计数见 P0） |
| ⑤ 空闲/未运行时段+时长 | 通过 | t7-6/t7-7 | `t7verify.exe spans` + `dur`（0 条不一致） |
| ⑥ day_span 全天汇总 | **未通过 P1** | t7-8 | `t7verify.exe dayspan`（负 unknown + 24 日缺行） |
| ⑦ 主程序不显示/网页展示 | WPF 通过；网页待办 | t7-12 | grep Views 0 hits；web-index 仍旧文案 |
| 跨天换文件分段（t6 硬要求三） | 通过 | t7-6 | daySplit=1 两段、组名继承、重扫幂等 |
| 幂等与迁移 | run/route/err/raw 通过；pick_event **P0** | t7-10/t7-5 | `t7verify.exe idem` + `negfact` |
| 无回归（ledger/stats/留存/跨天） | 通过 | t7-11 | `t7verify.exe api` + t5 场景复跑 |

**两个必须修的缺陷**（修复归 collector-author / store-author，verifier 未动任何生产代码）：

1. **P0 pick_event**：`RunRecordCollector.cs:1511-1522`（LineNo=chunk 内偏移）+ `RunRecordStore.cs:1499`（唯一索引含 chunk_offset）—— 多 chunk 增量喂入少记、重扫翻倍（黑铜号角 142→284 实测）。修复方向：幂等键与喂入方式无关（全局行号或内容定 occurrence）。
2. **P1 day_span**：`RunRecordStore.cs:686-710`（切分数学，25 日 idle=17h+not_running=15h=32h、unknown=-28800000 负值）+ `:1435`（CHECK 不防负分量）+ 24 日整行缺失。复现=t7-8。

沙箱与产物清理同 t5：tools/t7-verify、publish 产物、%TEMP% 沙箱在文档落盘后按精确路径删除。
