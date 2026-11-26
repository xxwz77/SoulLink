# SoulLink 卡顿与串流审查 · 2026-11-26

桌面基线为 v3.3.50。下列代码路径按源码核对，耗时需在测试账号 A2/A3 上采样。诊断时记录单调时间、线程、账号匿名标识和阶段耗时，避免记录输入内容。

## 自动化与采集

| 优先级 | 可定位的行为及影响 | 改法与验收 |
|---|---|---|
| 高 | 数据中心换文件在全局 Sync 内调用 ResetForNewFile，后者同步读 SQLite 游标。UI、自动化读 Snapshot 也取这把锁。一次游标读取变慢可能拖住其他账号和前台。见 [换文件](../../Services/BgiLogCenter.cs#L1070)、[载入游标](../../Services/BgiLogCenter.cs#L160)、[同步查询](../../Services/IngestCursorStore.cs#L57)、[读快照](../../Services/BgiLogCenter.cs#L257)。 | 按账号串行处理变更，在锁外读取游标，提交时检查文件代次。UI 只读上一次已发布快照。验收时用临时数据库让读取等待，同时反复取另一个账号的快照，记录 UI 心跳与快照延迟。 |
| 高 | 1 秒 ServiceTick 和账号刷新 Tick 都会调用 Step。Pump 维护共享 Pending、尾窗和游标。ProcessChunk 在填充数据前已经 Version++，读方持 Sync 建缓存，写方修改尾窗不持同一把锁。读方可能缓存半批结果，两个采集入口也可能重复消费同一段。参见 [快拍入口](../../Services/BgiLogCenter.cs#L464)、[账号刷新入口](../../Services/BgiLogCenter.cs#L537)、[分块](../../Services/BgiLogCenter.cs#L1183)、[版本](../../Services/BgiLogCenter.cs#L1237)、[发布](../../Services/BgiLogCenter.cs#L1412)。 | 每账号保留一个写者，按批生成完整只读快照，最后一次性发布引用。另一个入口仅标脏。用合成日志并发触发两入口，校验序号唯一、游标单调、同版本尾窗与组状态一致。 |
| 高 | SeedGroupsFromDbAsync 返回后只检查账号与 Entry 引用，同一 Entry 可以换文件，查询期间还可能已收到新组事件。Seed 后也没有增加 Version，已经缓存的空 Groups 列表可能继续返回。见 [重建](../../Services/BgiLogCenter.cs#L1462)、[提交条件](../../Services/BgiLogCenter.cs#L1491)。 | 使用文件代次和基线游标确认结果仍适用，不覆盖更新的组事件。成功合并后再发布新版本。测试让数据库查询延迟返回，在等待中注入一次开始/结束及一次换文件。 |
| 中高 | 编辑器 DispatcherTimer 调用 RefreshConditionPreview。EvaluateAsync 在第一次真正挂起前会复制节点、遍历触点并做日志匹配。常见条件同步完成，ConfigureAwait(false) 不会把这部分移离 UI。见 [预览入口](../../Views/FlowEditorWindow.xaml.cs#L2299)、[遍历](../../Services/FlowPreview.cs#L15)、[匹配](../../Services/FlowRunner.cs#L329)。 | UI 捕获小型不可变编辑快照，后台单飞求值，UI 仅应用最新版本。关闭编辑器应取消迟到的结果。验收用长尾窗、多日志节点反复编辑与关闭，记录 Dispatcher 延迟和过期结果次数。 |
| 中 | FlowRunner 每次扫描前后各序列化完整流程来判断变化。动作检查点还保存全配置。ConfigStore.Save 在调用线程序列化，后台调用者同步等待单写者确认。并行流程多时会增加分配、GC 与线程池等待，UI 发起保存也需完成序列化。见 [扫描](../../Services/FlowRunner.cs#L74)、[检查点](../../Services/FlowRunner.cs#L115)、[保存](../../Services/ConfigStore.cs#L306)。 | 由状态变更显式置脏，分离 UI 配置数据和运行进度。保留外部动作前后的持久化确认，后台异步等待确认。验收覆盖无变化扫描、慢盘和动作中途重启，统计每秒序列化次数、分配与重复执行。 |

## 数据库可靠性与页面查询

1. **写入失败与读取游标脱节，优先级最高。** [RunRecordStore 队列](../../Services/RunRecordStore.cs#L1476) 满后 TryAdd 会失败，写入调用方忽略结果，而 [BgiLogCenter](../../Services/BgiLogCenter.cs#L1197) 仍可持久化游标。故障后可能跳过尚未入库的记录。让写线程在事务提交后确认对应文件偏移，再推进持久化游标，设计幂等重放。用临时库、缩小队列、注入慢写与中途退出，重启后按记录 ID 比较缺失和重复。
2. **退出排空可能返回假成功。** [FlushAsync](../../Services/RunRecordStore.cs#L1187) 与普通查询共用队列，拒收时可直接完成。[退出回调](../../Services/RunRecordHost.cs#L162) 同步等两秒，忽略超时。应先关闭生产入口，异步等待最后一次已接受事务完成，超时留下可恢复游标与明确状态。普通 UI 查询遇到过载时也应保留上次成功值并标记延迟，当前默认空列表可能把界面覆盖为零。
3. **角色刷新能在前台执行整套归档查询。** [UI 入口](../../Views/DataCenterPage.xaml.cs#L706) 经角色服务进入 [MiyousheStore](../../Services/MiyousheStore.cs#L49)。信号量空闲时等待立即完成，后续 SQLite 同步开库和读取沿调用线程执行。应提供专门角色查询，交给拥有连接的后台执行器，最后只回 UI 更新数据。异常关闭连接也应在释放互斥权前完成。验收使用被锁定的临时库，刷新角色时窗口仍可拖动，第二个请求可恢复。

## RDP 全屏

顶栏可见时退出会在同一 UI 事件里先隐藏菜单并强制布局，再恢复窗口并强制布局。[第一次](../../Views/ConsolePage.xaml.cs#L3662)、[第二次](../../Views/ConsolePage.xaml.cs#L3552)。进入全屏有同步窗口定位、焦点和布局，[入口](../../Views/ConsolePage.xaml.cs#L3425)。尺寸事件会再次请求布局，多画面筛选还会重复查询 ActiveX 连接属性，[布局筛选](../../Views/ConsolePage.Tiles.cs#L417)。建议窗口转换期间合并菜单和尺寸变更，使用最终尺寸布局一次，COM 和 WPF 控件留在 UI 所属线程。

用同一个快捷键测菜单可见与隐藏、单画面与四画面，各预热 5 次，再测 50 次。分别记录处理回调耗时、首次 WPF 重绘、第一帧远端图像与切换后 1 秒内最长 UI 空档。16.7ms、50ms 分别作为一帧预算和明显长帧的观测线。实际主因以采样排序为准。

## 网页串流和键鼠

| 场景 | 证据 | 修复与离线验收 |
|---|---|---|
| 切后台后按键仍按住 | [页面发送 releaseall](../../docs/web-index.html#L2279)，[Agent 动作表](../../Relay/AgentWebServer.cs#L629) 缺此项，下层已有实现 | 先接通释放，验证失焦、断连及迟到的旧 down 都不会遗留按下状态。再给长按加独立 keydown/keyup，目前 [网页](../../docs/web-index.html#L3056) 将普通键发为点按，并过滤单独的修饰键。 |
| 相对模式不能连续转向 | [锁定标志](../../docs/web-index.html#L2411) 初始 false，用户入口未置 true，[请求锁定](../../docs/web-index.html#L2628) 要求该值为真。右拖到屏幕边缘会停止。前端还能发 dx=0.5，而 [Agent 协议](../../Relay/AgentWebServer.cs#L836) 只接受整数。小数包反序列化失败已离线验证。 | 用户手势开启 Pointer Lock，处理拒绝与退出，按整数发送并保留小数余量。验证同向连续转动和正负各十次半单位的总位移。 |
| 慢网络下松手越来越迟 | [WS 队列](../../docs/web-index.html#L2106) 等上条响应才发下一条。移动约每 8ms 可入队一次。若 RTT 为 80ms，理论吞吐约 12.5 条/秒，峰值入队约 125 条/秒。 | 合并相邻移动，给队列设上限和超时，保持按下、移动、抬起顺序。用模拟 WS 分别延迟和丢失响应，测最大队长与松键延迟。 |
| 冷启动反复重连 | [网页响应头超时](../../docs/web-index.html#L1670) 为 8 秒，[服务端等待首帧](../../Relay/AgentWebServer.cs#L450) 可达 25 秒，首帧就绪后才返回成功响应。 | 统筹响应头和首帧的超时，用第 10 秒才出首帧的模拟流做回归。记录收到字节、首个可解码帧和首次呈现三个时刻。 |

Android 客户端的解码器改动和真机取证见[团队手册](ANDROID-BLACKSCREEN-20261126.md)。

## 建议采样

分别记录 Dispatcher 排队到执行、后台运行时长、等待数据库/快照锁的时长。分开测空闲、历史回填、打开流程编辑器以及同时切换全屏四种情况，输出 p50/p95/最大值。窗口画面冻结时同时保留 UI 和工作线程的栈，才能区分 UI 自身在工作、等锁和远端迟迟没有新帧。
