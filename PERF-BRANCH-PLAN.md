# perf/hotpath-fixes · 热路径修复计划

分支：`perf/hotpath-fixes`（从 main @ bc416fc 拉出，独立工作树 `SoulLink-perf`）
合并方式：全部完成并验证后整体合回 main；期间 main 上的其它改动不受影响。

## 范围（来自截图审查结论）

| # | 位置 | 问题 | 处置 |
|---|------|------|------|
| 1 | `Controls/RdpConsole.cs:1165` | UI 线程 `GetAwaiter().GetResult()` 等管道，最坏阻塞 ≈12.6s | 改异步：探测期间不阻塞 UI，热键退出路径无条件放行 |
| 1b | `Views/JoinRoomWindow.xaml.cs:90`、`Views/MyDepartWindow.xaml.cs:58`、`Services/BgiPathResolver.cs:173,282` | 同类 UI 线程同步等 | 直接 await |
| 2 | `Views/MainWindow.xaml.cs:736-795` | 16ms 定时器在导航展开稳定后仍常驻 | 加稳定判定，停表后由 SizeChanged/LocationChanged 驱动 |
| 2b | `Controls/GlassControls.cs:172`、`Controls/FlowGuideOverlay.cs:73` | 120ms / 33ms 定时器 | 核实生命周期，确保非活动即停 |
| 3 | RDP 解码 | 已确认在 ocx 自己的线程，**不处理** | — |
| 4 | `Server/Db.cs`、`Server/Lobby.cs` | 每请求新连接、无 PRAGMA、N+1、无事务 | WAL + busy_timeout；ListRooms 改 JOIN；Heartbeat 改单条 UPDATE；建房/加入加事务 |

## 底线

- 不改 `Controls/RdpConsole.cs` 的捕获/看门狗语义（见 1350 行「退出必须无条件放行」的注释）。
- 不改 `RelayClient` 的通道协议。
- 每条改动独立可回滚，按上表顺序分提交。
