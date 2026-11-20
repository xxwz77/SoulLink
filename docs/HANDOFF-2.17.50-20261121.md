# SoulLink 交接文档 · 2.17.50（热路径性能修复）

> 时间：2026-11-21 03:44
> 版本：**2.17.50.0**（已替换 `dist\SoulLink.exe`）
> 性质：**性能/稳定性修复**，无界面行为变更
> 来源分支：`perf/hotpath-fixes`（已 no-ff 合并进 main）

---

## 0. 一句话现状

按截图里的四条风险逐条核对后，**RDP 解码那条不成立**（本就跑在 ocx 自己的线程里），
另外三条全部命中并已修：UI 线程同步等管道、16ms 轮询定时器、服务端 N+1 与每请求新连接。
**额外发现**：服务端还缺事务（并发 join 会互相覆盖座位）、缺 WAL（并发写直接报 locked）。

---

## 1. 逐条核对与修复

| # | 截图说法 | 实际代码 | 处置 |
|---|---|---|---|
| 1 | `RdpConsole.cs:1165` UI 线程同步等管道 | `CheckUsableAsync().GetAwaiter().GetResult()`，串行 4 个 relay 请求、每条故障超时 4.25s ⇒ **最坏阻塞 ≈12.6s**，且调用方正是热键解锁路径 | 拆成"同步直达 + 后台探测" |
| 2 | `MainWindow.xaml.cs:738` 16ms 轮询 | `_navAnimationSync` 展开分支无退出条件，导航展开即永久 62.5Hz | 尺寸稳定后自停，重排时按需重启 |
| 3 | RDP 解码在 ocx 自己的线程 | 手写 `RdpAxHost`，全仓无 BitBlt/CopyFromScreen | **确认不是瓶颈，不处理** |
| 4 | Server 端 N+1 + 每请求新连接 | 每方法 `new SqliteConnection`、无 PRAGMA；`ListRooms` 对每房间再查座位；`Heartbeat` 遍历全部房间 | WAL + 连接池 + 批量查询 + 单条 UPDATE |

---

## 2. 服务端改动（`Server/Db.cs`、`Server/Lobby.cs`）

### 2.1 连接与并发

```csharp
// 原来：Data Source=path；每个方法 new 连接；无 busy_timeout（并发写直接抛 locked）
Pooling = true, DefaultTimeout = 10
// 每连接都设（busy_timeout 不随库持久化）
PRAGMA busy_timeout=5000
// Init 里设一次（journal_mode 会写进库文件）
PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL
```

### 2.2 去 N+1

新增 `Db.ListRoomsWithSeats()`：房间、座位各一条查询，在内存里按 RoomId 归组。
`Lobby.ListRooms` 改用它。**往返次数与房间数无关**（原来 1+N）。
这个接口每个客户端每 5 秒拉一次（`LobbyPage` 的 5s 定时器），放大效应最明显。

### 2.3 心跳

新增 `Db.TouchSeatsForDevice(device)`：一条 `UPDATE ... WHERE DeviceCode=$d AND Online=1`，
并补 `IX_Seats_Device` 索引。原实现是"列全部房间 → 每个房间查座位 → 命中才写"，
成本随房间数线性增长，而心跳是每客户端每 10 秒的固定开销。

### 2.4 事务（原代码完全缺失）

| 方法 | 原问题 | 现在 |
|---|---|---|
| `CreatePair` | big/small 两条独立 INSERT + 各建座位；中途失败留下"只有大号房"的半成品，而存在性检查按 PairId 判定 ⇒ 用户既建不了新的也用不了旧的 | `Db.InsertRoomPair` 单事务 |
| `Join` | "查座位 → 内存挑位 → 写回"；两人同时 join 抢到同一 SeatIndex，后写的覆盖先来的 | `Db.OccupySeat` 单事务：查重 → 条件 UPDATE 原子占位 → 满了才 Max+1 追加 |

---

## 3. 客户端改动

### 3.1 `RdpConsole.SetInputMode` 拆成同步 + 异步

- 签名 `(bool ok, string message)` → `void`（调用点：`ToggleFromHotkey`、`ConsolePage` 注销路径）
- 新增 `TrySetInputModeNow`：退出（Pointer）、已就绪、通道未连接等情况**当场给出结论**
- 只有"FreeLook 且 `_inputUsable == false`"才走 `SetInputModeAsync` 后台探测
- **退出分支永远不被阻塞** —— 这是热键"锁死时能挣脱"的底线，原语义保留
- 异步回填带三重作废判定：`_disposed` / 通道被替换 / 用户已切回指针模式

### 3.2 三处 UI 线程同步起子进程

`JoinRoomWindow`、`MyDepartWindow`、`BgiPathResolver`（2 处）原来在 UI 线程
`QueryAsync().GetAwaiter().GetResult()`，每次起 qwinsta/query.exe 子进程（~30ms，异常路径更久）。
改为优先读 `AccountStateCenter.Sessions` 快照（状态中心本来每周期就在后台查同一份数据），
**快照为空才回退**到同步查询。

`ActionEditorWindow.xaml.cs:900` 那处保留 —— 它在 OpenFileDialog 的同步回调里，
本来就要阻塞等用户选文件，改异步的收益为零、复杂度上来。

### 3.3 16ms 导航同步定时器

`BeginNavOverlaySync` 的展开分支增加"稳定即停"：宽度与高度都不再变化且动画已超出 450ms，
就 `Stop()` 并清理动画 hold 值。`BringNavigationToFront` 里新增 `RestartNavOverlaySync()`
保证重排/注销动画时仍能重新拉起，不会停在旧尺寸。

---

## 4. 验证

### 4.1 构建

- 客户端 Release：0 错误（132 警告，全部为既有的未使用字段类）
- 服务端 Release：0 错误 0 警告

### 4.2 服务端并发（本机实测，独立库 + 独立端口）

```
PING: {"ok":true,"version":"1.11.1.0"}
30 路并发：建房 / 加入 / 心跳 / 列表  →  OK=30 FAIL=0
在线座位: 30 · 索引唯一: True
```

修前这组请求必然出现 `database is locked` 与座位索引重复。WAL 落地后
库目录能看到 `soul-link.db-wal` / `-shm`。

### 4.3 客户端冒烟

```
config loaded → single instance acquired → tray created
→ LaunchView activated → main window shown: True state=Normal
无 error.log
```

---

## 5. 产物

| 项目 | 值 |
|---|---|
| 版本 | **2.17.50.0** |
| 成品 | `dist\SoulLink.exe` · 67,502,312 字节 |
| SHA256 | `158361E0696A27B455452C09E9ABF24313FFCB7D1940D86821D310A7F3535B68` |
| 旧版留档 | `dist\SoulLink-2.17.49-before-perf.exe` |
| 暂存 | `artifacts\dist-2.17.50\` |
| 服务端 | `artifacts\dist-server-2.17.50\`（linux-x64 自包含，SHA256 `53294C829384E9D63E4494419B66102C7D8D19CEA9902A256B1D53778FC529A3`） |

> ⚠ 本仓库 `build.bat` 里有一句 `rmdir /s /q dist`，会**删掉 dist 下所有历史留档 exe**。
> 本次发布刻意绕过它，改用 `dotnet publish -o artifacts\dist-2.17.50` 再单独替换 `dist\SoulLink.exe`。
> 若要继续用 build.bat，建议先把那行改掉。

---

## 6. 教训

### 6.1 "同步等一个会超时的 IPC"是 UI 线程的头号杀手
`CheckUsableAsync` 单看每步都很快，串起来最坏 12.6 秒。
**规则：UI 线程上任何 await 外层的同步等待都要按"最坏超时 × 串联次数"核算。**
而这个最坏值恰好出现在"代理已挂、TCP 未断"这种最容易触发用户操作的状态下。

### 6.2 SQLite 的默认配置是单机玩具级
rollback journal + busy_timeout=0 + 每请求新连接，在并发下必然出错。
这三项在任何"Web 服务 + SQLite"的组合里都应该是起手就配的，不是优化项。

### 6.3 出包脚本不能顺手删现场
`rmdir /s /q dist` 在别的项目里可能没问题，但这个仓库把 `dist` 当成版本留档目录用
（`SoulLink-2.17.48-before-boundfix.exe` 这类文件名本身就是事故复盘的一手材料）。
**发版要"新增 + 替换"，不要"清空重建"。**
