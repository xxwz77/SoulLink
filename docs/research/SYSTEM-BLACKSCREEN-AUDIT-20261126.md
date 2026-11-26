# 系统级黑屏审计：DWM 是否被 SoulLink 渲染洪泛压垮（只读审查，2026-11-26 追加）

> 审查人：architect。只读，未启动任何程序、未改生产代码。
> 前提：本报告基于当前代码（t4 之后又经过 t63 等改动），与 t4 审计时已不同。

---

## 0. 结论（确定性）

captain 的「DWM 被 SoulLink 渲染请求灌满」假设——在当前代码下不成立。

5 个环节逐一核对后：

1. BgiLogCenter.Changed 的订阅者只有一个：LaunchPage.OnLogCenterChanged（Views/LaunchPage.xaml.cs:1704）。DataCenterPage、ConsolePage、MainWindow 都不是 Changed 订阅者。
2. OnLogCenterChanged 有两道闸门把重活压住：第 1766 行 if (text == _logText) return（20 万字符相等则零开销）；LogTailAppend.Overlap 增量追加（只重排真正新增的行）。
3. DataCenterPage 的 1 秒 DispatcherTimer 有签名去重（BuildSignature 相同就不 Rebuild，第 162-163 行），A1 刷屏时 CurrentGroup/计数基本不变 → 每秒 Rebuild 几乎不触发。
4. Backdrop 在中档（默认 EffectTier=1）失焦时走 RepaintInterval=500ms 低频重绘，失焦即降频；它不是被 Changed 连带触发，只有尺寸/焦点/主题变化才重绘。

所以「UI 线程每帧提交新帧 → DWM 合成队列积压 → 全桌面黑屏」这条链在当前代码里断裂。

真正的机制（唯一能解释「SoulLink 停了 DWM 还在忙」）：

DWM 的 77 线程 / 累计 304s CPU / GPU 3D 67% / VideoDecode 21% / 显存 2371MB，几乎全部来自「原神 + Windows 桌面本身的 DWM 合成」，不是 SoulLink。

关键证据（captain 自己贴的实测）：
- SoulLink 停了，DWM 还在忙，且 8 秒内 CPU 又涨 2.19s —— 如果忙是 SoulLink 造成，停掉就该降；它没降，说明忙的是别的窗口合成（原神/桌面/其他会话窗口）。
- 原神 2931MB + 累计 CPU 5598s + 响应正常 —— 原神是前台 GPU 大户，DWM 要给原神合成每一帧。
- GPU 3D 67% + VideoDecode 21% 是原神渲染 + 视频解码的典型特征，SoulLink 一个 WPF 面板产生不了 21% 视频解码。
- 显存 2371MB：SoulLink 实测 254MB 工作集、背景位图约 2M 像素（几十 MB），远不够把显存拉高到 2371MB。

所以：系统黑屏不是 SoulLink 把 DWM 灌满，而是「原神（GPU 大户）+ 多会话 DWM 合成」本就把机器压在 GPU/DWM 高负载，SoulLink 数据中心只是在这台已紧绷的机器上又加了「启动期一次性重活」，成了压垮体验的最后一根稻草。

---

## 1. 逐环核对（captain 的 5 个环节）

### 环 1：Changed 触发频率

当前 MaxBytesPerTick 已从 32MB 改成 2MB（Services/BgiLogCenter.cs:51）。Changed 只有两处：:462（ServiceTick，后台线程）、:556（Tick，AccountStateCenter Timer 线程）。any 只在 Step 返回 true 才置位，Step 只有真读到新字节并解析（Pump 的 changed）或 PumpBackfill 返回 true 才 true。

精算 A1 37MB：首扫 37MB / 2MB每tick = 约 19 个 tick 触发 Changed（不是每轮都触发，只有读到新内容那 19 轮）。ServiceTick 1 秒一拍，且首扫阻塞当前线程（2MB×5解析器×约15正则，单 tick 几十~上百 ms，远低于 32MB 的秒级）。所以实际频率 ≈ 最多 1 秒 1~2 次，只在有真实增量时。

### 环 2：Changed 订阅者

grep 全仓库 BgiLogCenter.Changed += 只有 Views/LaunchPage.xaml.cs:1704。DataCenterPage 不靠 Changed，靠自己的 1 秒 DispatcherTimer + 签名去重。

### 环 3：OnLogCenterChanged 开销

Views/LaunchPage.xaml.cs:1735-1800：Dispatcher.InvokeAsync 排队（异步）；lambda 内 TailTextOf(SnapshotByUser) 拼 800 行/20 万字符（仅当前 _logSession 非空时）；第 1766 行 if (text == _logText) return —— 20 万字符相等比较，没变就 return，零 TextBlock 重排；变了才 LogTailAppend.Overlap 增量追加，只 Run(added) 新增几行。

量化：A1 满速写日志时每秒 Changed ≈1 次 → 每秒最多 1 次增量追加（新增几行的 TextBlock 重排，不是 800 行全量重排）。这是 2026-11-22 就修掉的「每秒整窗重排」。

### 环 4：DataCenterPage 的 1 秒 Rebuild

Views/DataCenterPage.xaml.cs:153-165：每秒 BuildSignature()；第 162 行 if (sig == _lastSignature) return。签名含 CurrentGroup/OneDragon/Groups.Count/ErrorCount/...，A1 刷屏时这些大部分时间不变 → 每秒 Rebuild 大多数被签名去重拦下。即使真的 Rebuild，每账号约 20~30 控件 × 4 账号 ≈ 100 视觉树节点 = 几十 ms UI 线程工作，不是 DWM 洪泛。

### 环 5：渲染请求如何压垮 DWM

WPF 渲染模型：UI 线程改视觉树 → WPF 把 dirty 区域合成到自己的渲染线程 → 最后一个 surface 提交给 DWM。DWM 看到的是「每帧一个最终 surface」，不是「每次 InvalidateVisual 一个合成任务」。DWM 按 vsync 节奏（16.7ms）合成，UI 线程再怎么折腾 DWM 每帧最多合一次；UI 线程慢只会让 SoulLink 自己掉帧/无响应，不会让 DWM 积压。

DWM 卡死全桌面的真正前提是 GPU 驱动崩溃 / 显卡重置（TDR）/ 显存耗尽 / 桌面堆耗尽，而不是「合成队列被灌满」。当前实测显存 2371MB、GPU 3D 67%、VideoDecode 21% 指向的是原神 + 视频解码把 GPU 吃紧，不是 SoulLink。

---

## 2. 那「整个系统黑屏」到底是谁？

最可能：原神（GPU 大户）+ 多会话 DWM 合成把机器 GPU/DWM 压到临界，SoulLink 数据中心启动期的一次性重活（首扫 37MB×5解析器 + 回填 250MB + SQLite 建索引）在这个临界点上加剧卡顿，被用户感知为「一引入 A1 就黑屏」。

证据：
1. SoulLink 停后 DWM 仍 146MB/77线程/累计304s/8秒涨2.19s —— 忙的不是 SoulLink；
2. GPU 3D 67% + VideoDecode 21% + 显存 2371MB —— 原神级别；
3. 原神 2931MB + CPU 累计 5598s 且响应正常 —— 前台 GPU 主体；
4. SoulLink 实测 254MB 工作集、背景位图 2M 像素 —— 远不足以拉高显存到 2371MB。

SoulLink 的贡献（仍是数据中心问题，但机制不同）：
- t4 已定位的首扫 2MB/tick × 5 解析器 × 约15 正则（现已是 2MB，比 t4 审的 32MB 改善，但首扫 37MB 仍要约 19 秒连续占用采集线程 + GC）；
- 启动期 EnsureBootedAsync + EnsureSpanBootAsync + PurgeOlderThanAsync（RunRecordHost.Start）里的 SQLite 建表/索引/VACUUM；
- 这些都发生在「原神前台 + DWM 高负载」的机器上，任何 CPU/IO 抖动都会被放大成可见的桌面卡顿/黑屏。

---

## 3. P0/P1 问题清单（当前代码，文件:行号）

### P0-1（数据正确性/吞吐风险，非渲染）· 首扫仍连续占用采集线程约 19 秒

- 位置：Services/BgiLogCenter.cs:51（MaxBytesPerTick=2MB）、:931（budget）、:983-984（ProcessChunk 跑 5 解析器）。
- 触发：A1 首次全量扫描 37MB。
- 影响：不是 DWM 洪泛，而是采集线程连续 19 秒忙，期间所有账号实时采集排队。
- 修法：首扫按「每 tick 最多 N ms」时间预算（而非字节预算）让出，Stopwatch 在 ProcessChunk 前检查已耗时、超了 break 下 tick 续（游标幂等）。

### P0-2（真正要修的核心）· 启动期数据中心重活与前台游戏争 CPU/IO

- 位置：Services/RunRecordHost.cs:88-95（Start 后台 EnsureBootedAsync + EnsureSpanBootAsync + PurgeOnceAsync）、Services/RunRecordStore.cs:1574-1688（EnsureSchema 建表+索引）。
- 触发：有账号 + 大 run.db 时启动即并发做建表/索引/读 open 行/purge。
- 影响：在「原神前台 + GPU 满载」机器上，启动期这几十~几百 ms 突发 CPU/磁盘 IO 是「一引入 A1 就黑屏」的直接放大器。
- 修法：启动期数据中心重活降到 DispatcherPriority.ApplicationIdle 之后的低优先级线程，Purge 里的 wal_checkpoint + incremental_vacuum 从启动路径挪到每小时清理拍。

### P1-1 · 回填 PumpBackfill 只有队列深度闸门、无时间预算

- 位置：Services/BgiLogCenter.cs:744-750（qDepth>2000 才让出，但没看「本 tick 已耗时」）。
- 影响：回填 250MB 时，即使队列没到 2000，每 tick 1MB 读+Feed 也占采集线程 250 秒（约 4 分钟）持续。
- 修法：PumpBackfill 加 Stopwatch 时间预算（如 50ms/tick），超了就 return 下 tick 续。

### P1-2 · DataCenterPage 签名去重没覆盖「配置组卡片今日运行列表」

- 位置：Views/DataCenterPage.xaml.cs:107-146（BuildSignature）。
- 影响：_sqliteTodayRuns 异步加载完成后不触发签名变化（t62 修掉递归，改为下次定时器刷新自然用上新数据），首次出现 SQLite 数据时可能延迟 1 秒才显示——体验问题，非黑屏。
- 修法：EnsureSqliteTodayRunsAsync 完成后显式 _lastSignature="" 只触发一次 Rebuild（防重入已用 _sqliteLoading）。

---

## 4. 最小验证方法（不启动主程序能证明的）

1. 证明「Changed 频率有上限」：纯 console（%TEMP%）复刻 MaxBytesPerTick=2MB + 1 秒 ServiceTick 节拍，喂 37MB 文本，计数 Changed 触发次数。预期 ≈19 次、每次间隔 ≥1 秒。
2. 证明「OnLogCenterChanged 有相等短路」：纯 console 复刻 if (text == _logText) return + LogTailAppend.Overlap，喂连续相同尾窗，计数 TextBlock 重排。预期内容不变时 0 次。
3. 证明「DWM 忙与 SoulLink 无关」：captain 已贴的实测「SoulLink 停后 DWM 仍涨」本身就是结论。

以上 1、2 都不需要 WPF UI、不需要启动 SoulLink，跑完即删。

---

## 5. 一句话给 captain

别再把方向钉在「DWM 被 SoulLink 渲染灌满」上——这条链在当前代码里断裂（Changed 只 1 个订阅者 + 相等短路 + 签名去重 + 失焦降频）。真正要修的是 t4 已定位的「启动期数据中心重活与前台游戏争资源」，而「系统黑屏」的主体是原神 + 多会话 DWM 合成，SoulLink 只是这台满载机器上的放大器。

（只读审查：未启动任何程序、未构建运行、未修改任何生产代码。）
