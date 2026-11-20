# SoulLink 交接文档 · 2.17.50（热路径性能修复 + 服务端上线）

> 时间：2026-11-21 04:01
> 版本：客户端 **2.17.50.0** · 服务端 **1.12.0.0**
> 性质：**性能/稳定性修复**，无接口变更、无界面行为变更
> 来源分支：`perf/hotpath-fixes`（已 no-ff 合并，工作树与分支均已清理）
> 线上：服务端已更新并验证通过

---

## 0. 一句话现状

按用户给的四条性能风险截图逐条核对：**RDP 解码那条不成立**（本就跑在 ocx 自己的线程里），
另外三条全部命中并已修。核对过程中另外发现服务端**缺事务**（并发 join 互相覆盖座位）
和**缺 WAL**（并发写直接 `database is locked`），一并修掉。

客户端与服务端均已出包，服务端已部署上线并用 `/api/ping` 确认版本。

---

## 1. 四条风险逐条核对

| # | 截图说法 | 实际代码 | 判定 | 处置 |
|---|---|---|---|---|
| 1 | `RdpConsole.cs:1165` UI 线程同步等管道 | `Input.CheckUsableAsync().GetAwaiter().GetResult()`；内部串行 4 个 relay 请求，每条故障超时 `timeoutMs + 250` = 4.25s ⇒ **最坏阻塞 ≈12.6s**，且调用方正是热键解锁路径 | ✅ 命中，且比描述更严重 | 拆成"同步直达 + 后台探测" |
| 2 | `MainWindow.xaml.cs:738` 16ms 轮询 | `_navAnimationSync`（`DispatcherTimer` 16ms）展开分支**无退出条件**，导航展开即永久 62.5Hz | ✅ 命中 | 尺寸稳定后自停，重排时按需重启 |
| 3 | RDP 解码在 ocx 自己的线程 | 手写 `RdpAxHost`（WinForms `AxHost`）塞进 `WindowsFormsHost`；全仓无 `BitBlt`/`CopyFromScreen`/DXGI 生产代码 | ❌ **不成立** | 不处理 |
| 4 | Server 端 N+1 + 每请求开新连接 | 每方法 `new SqliteConnection`、无任何 `PRAGMA`；`ListRooms` 对每房间再查一次座位；`Heartbeat` 遍历全部房间 | ✅ 命中，且**还缺事务** | WAL + 连接池 + 批量查询 + 单条 UPDATE + 事务 |

---

## 2. 服务端改动（`Server/Db.cs`、`Server/Lobby.cs`）

### 2.1 连接与并发

```csharp
// 原来：Data Source=path；每个方法 new 连接；无 busy_timeout（并发写直接抛 locked）
Pooling = true, DefaultTimeout = 10
// busy_timeout 是连接级设置，不随库持久化 → 每个连接都要设
PRAGMA busy_timeout=5000
// journal_mode 会写进库文件 → Init 里设一次即可
PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL
```

### 2.2 去 N+1

新增 `Db.ListRoomsWithSeats()`：房间、座位**各一条查询**，内存里按 RoomId 归组。
`Lobby.ListRooms` 改用它，**往返次数与房间数解耦**（原来 1+N）。

这个接口被每个客户端**每 5 秒**拉一次（`LobbyPage` 的定时器），放大效应最明显。

### 2.3 心跳

新增 `Db.TouchSeatsForDevice(device)`：一条
`UPDATE Seats SET LastSeenAt=$t WHERE DeviceCode=$d AND Online=1`，
并补 `IX_Seats_Device` 索引。

原实现是"列全部房间 → 每个房间查座位 → 命中才写"，成本随房间数线性增长，
而心跳是每客户端每 10 秒的固定开销。

### 2.4 事务（原代码完全缺失）

| 方法 | 原问题 | 现在 |
|---|---|---|
| `CreatePair` | big/small 两条独立 INSERT + 各建座位；中途失败留下"只有大号房"的半成品，而存在性检查按 `PairId` 判定 ⇒ 用户既建不了新的、也用不了旧的 | `Db.InsertRoomPair` 单事务 |
| `Join` | "查座位 → 内存挑位 → 写回"；两人同时 join 抢到同一 `SeatIndex`，后写的覆盖先来的（表现为"我明明进来了却被挤掉"） | `Db.OccupySeat` 单事务：查重 → 条件 UPDATE 原子占位 → 满了才 `MAX+1` 追加 |

---

## 3. 客户端改动

### 3.1 `RdpConsole.SetInputMode` 拆成同步 + 异步

- 签名 `(bool ok, string message)` → `void`；调用点：`ToggleFromHotkey`、`ConsolePage` 两处注销路径
- 新增 `TrySetInputModeNow`：退出（Pointer）、已就绪、通道未连接等情况**当场给出结论**
- 只有"FreeLook 且 `_inputUsable == false`"才走 `SetInputModeAsync` 后台探测
- **退出分支永远不被阻塞** —— 热键"锁死时能挣脱"的底线语义原样保留
- 异步回填带三重作废判定：`_disposed` / 通道已被替换 / 用户已切回指针模式

### 3.2 UI 线程同步起子进程（4 处）

`JoinRoomWindow`、`MyDepartWindow`、`BgiPathResolver`（2 处）原来在 UI 线程
`QueryAsync().GetAwaiter().GetResult()`，每次起 qwinsta/query.exe 子进程（~30ms，异常路径更久）。

改为优先读 `AccountStateCenter.Sessions` 快照（状态中心每周期本就在后台查同一份数据），
**快照为空才回退**到同步查询。

`ActionEditorWindow.xaml.cs:900` 刻意保留 —— 它在 `OpenFileDialog` 的同步回调里，
本来就要阻塞等用户选文件，改异步收益为零。

### 3.3 16ms 导航同步定时器

`BeginNavOverlaySync` 展开分支增加"稳定即停"：宽高都不再变化且动画已超出 450ms，
就 `Stop()` 并清理动画 hold 值。

配套新增 `RestartNavOverlaySync()`，由 `BringNavigationToFront` 调用 ——
**定时器自停之后必须有人能重新拉起**，否则注销动画/重排时浮层尺寸会停在旧值。

---

## 4. 验证

### 4.1 构建

- 客户端 Release：0 错误（132 警告，全部是既有的未使用字段/事件类）
- 服务端 Release：0 错误 0 警告

### 4.2 服务端并发（本机，独立库 + 独立端口 + 发布出来的 exe）

```
PING: {"ok":true,...,"version":"1.12.0.0"}
30 路并发：建房 / 加入 / 心跳 / 列表  →  OK=30 FAIL=0
在线座位: 30 · 索引唯一: True
```

修前这组请求必然出现 `database is locked` 与座位索引重复。
WAL 生效后库目录能看到 `soul-link.db-wal` / `-shm`。

### 4.3 客户端冒烟

```
config loaded → single instance acquired → tray created
→ AccountStateCenter started → LaunchView activated
→ main window shown: True state=Normal
无 error.log
```

### 4.4 线上验证（腾讯云 Ubuntu）

```
$ curl http://127.0.0.1:5080/api/ping
{"ok":true,"time":"2026-11-20T19:59:31.94...","version":"1.12.0.0"}

$ ls -l /opt/soullink-server/soul-link.db*
soul-link.db  soul-link.db-wal  soul-link.db-shm    ← WAL 已生效
```

---

## 5. 产物

| 项目 | 值 |
|---|---|
| 客户端版本 | **2.17.50.0** |
| 客户端成品 | `dist\SoulLink.exe` · 67,502,312 字节 |
| 客户端 SHA256 | `158361E0696A27B455452C09E9ABF24313FFCB7D1940D86821D310A7F3535B68` |
| 客户端旧版留档 | `dist\SoulLink-2.17.49-before-perf.exe` |
| 客户端暂存 | `artifacts\dist-2.17.50\` |
| 服务端版本 | **1.12.0.0** |
| 服务端成品 | `artifacts\dist-server-2.17.50\SoulLink.Server` · 93,982,226 字节（linux-x64 单文件自包含） |
| 服务端 SHA256 | `D2561A221CC290649FF1ED815938A7D672C0C8C569541D2821A53610D126BF46` |
| 线上安装位置 | `/opt/soullink-server/`（systemd: `soullink-server`） |
| 线上数据库备份 | `/opt/soullink-server/soul-link.db.bak-2.17.50` |

> ⚠ 客户端发布**没有用** `build.bat` —— 它第 25 行是 `rmdir /s /q dist`，
> 会删掉 `dist` 下 20 多个历史留档 exe。本次改用
> `dotnet publish -o artifacts\dist-2.17.50` 再单独替换 `dist\SoulLink.exe`。**该问题仍未修**。

---

## 6. 首次更新线上服务器踩的坑

完整流程已单独整理到 [`docs/SERVER-UPDATE-GUIDE.md`](SERVER-UPDATE-GUIDE.md)，这里只记录事故过程。

### 6.1 `deploy.sh` 只上传了三个文件，脚本本身漏了

`cd /root/soullink-new` → `No such file or directory`；
`bash deploy.sh` → `No such file or directory`。

根因：文件直接传到了 `/opt/soullink-server/`（正确位置），但从没建过 `soullink-new` 目录，
`deploy.sh` 也没上传。**实际上部署脚本根本不必要** —— unit 和权限都是现成的，
覆盖二进制 + `systemctl start` 就够了。

### 6.2 `systemctl start` 静默失败：撞上 start-limit

第一次启动时文件权限是 `644`（没执行位），失败；之后几轮又失败，
累积到 systemd 的 start-limit 阈值后**直接拒绝拉起，且 `systemctl start` 不报错**。
现象是"命令成功、端口没人监听"，极具误导性。

```bash
sudo systemctl reset-failed soullink-server   # ← 关键的一步
sudo systemctl start soullink-server
```

### 6.3 前台直接跑是最快的定位手段

systemd 会吞掉启动期异常。`sudo /opt/soullink-server/SoulLink.Server` 一跑，
立刻看到 `Now listening on: http://0.0.0.0:5080`，**反证二进制完全正常**，
范围立刻收缩到 service 配置层。

### 6.4 教训：版本号是唯一的线上确认手段

服务端原来 `<Version>1.11.1</Version>`，本次升到 `1.12.0`。
没有这一步，更新完就无法区分线上跑的是新是旧 —— `/api/ping` 的 `version` 字段是唯一凭证。

---

## 7. 已知遗留（未做，供后续接手）

| 项 | 说明 | 建议 |
|---|---|---|
| `build.bat` 会删历史留档 | 第 25 行 `rmdir /s /q dist` | 改成"新增 + 替换"，不要清空重建 |
| `dist-server/` 不进 git | `.gitignore` 第 87 行 `dist-*/` 把它排除，README/deploy.sh 的改动无法提交 | 加 `!dist-server/README.md`、`!dist-server/deploy.sh` 例外 |
| 剩余 7 处 `GetAwaiter().GetResult()` | 见下节，均非 UI 线程或已刻意保留 | 视情况处理 |
| 房间变更仍靠轮询 | `LobbyPage` 5s、`LobbyConnection` 10s | `HANDOVER.md` 早已列为待办；改推送需 WebSocket |
| `AccountStateCenter` 1Hz 起子进程 | 每次 `QueryAsync` 派生 qwinsta/query.exe | 可改 WTS 会话通知事件驱动 |

### 7.1 未改的同步等待点（逐条已核）

**已刻意保留（改了没收益）**

- `Views/ActionEditorWindow.xaml.cs:900` —— `OpenFileDialog` 同步回调，本就要阻塞等用户
- `Services/AccountStateCenter.cs:379` —— 跑在 `System.Threading.Timer` 后台线程
- `Services/RelayClient.cs:586` —— 已包在 `Task.Run` 里

**已改为兜底分支（正常路径不走）**

- `Views/JoinRoomWindow.xaml.cs:94`、`Views/MyDepartWindow.xaml.cs:61`
- `Services/BgiPathResolver.cs:180`、`:297`

**稀有路径，暂未动**

- `Services/SystemLauncher.cs:506` —— `DescribeSessionOnline` 里 WTS 查询失败时的兜底。
  可能被 `RelayClient.DeployAsync` 在 UI 线程调到，但仅当前面 WTS 调用失败才触发。

---

## 8. 教训

### 8.1 "同步等一个会超时的 IPC"是 UI 线程的头号杀手

`CheckUsableAsync` 单看每步都很快，串起来最坏 12.6 秒。
**规则：UI 线程上的任何同步等待，都要按"最坏超时 × 串联次数"核算。**
而这个最坏值恰好出现在"代理已挂、TCP 未断"——最容易触发用户操作的状态。

### 8.2 SQLite 的默认配置是单机玩具级

rollback journal + `busy_timeout=0` + 每请求新连接，在并发下必然出错。
这三项在任何"Web 服务 + SQLite"组合里都该是**起手就配的，不是优化项**。

### 8.3 出包脚本不能顺手删现场

`rmdir /s /q dist` 在别的项目也许没问题，但本仓库把 `dist` 当版本留档目录用
（`SoulLink-2.17.48-before-boundfix.exe` 这类文件名本身就是事故复盘的一手材料）。
**发版要"新增 + 替换"，不要"清空重建"。**

### 8.4 分开的工作树让并行改动变得安全

本次改动全程在 `SoulLink-perf` 独立工作树（`git worktree`）里做，与 main 上正在进行的
2.17.49 bugfix 互不干扰；期间 main 前进了两个提交，rebase 后无冲突。完成并验证后
`--no-ff` 合并，随后 `git worktree remove` + `git branch -d` 清理干净。
**"独立分支稍后合并"这个诉求，`git worktree` 比 stash 或切分支更适合。**

---

## 9. 本轮提交记录

```
a111cf6 补服务端更新运维文档：更新流程、哈希校验、start-limit 与 noexec 坑
9bf609f 服务端 1.12.0：WAL/事务/去 N+1 后升版本号，便于用 /api/ping 确认线上是否已更新
7ad57d1 2.17.50 补交接文档：四条风险核对结论与验证数据
fbc314e 合并 perf/hotpath-fixes：UI 热路径与联机大厅服务端性能修复
e67f2ac 2.17.50 版本号
cc88781 客户端热路径：去掉 UI 线程同步等管道，16ms 定时器稳定即停
3ee2519 服务端热路径：WAL + busy_timeout + 去 N+1 + 事务
```
