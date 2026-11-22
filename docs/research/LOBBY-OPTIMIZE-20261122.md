# 联机大厅按需访问改造 + 发车人数 2-4 可配

> 日期：2026-11-22
> 作者：server-engineer（team_mwrr48fjaninl6 / task_mwrr56jiaqa0hu）
> 状态：代码已实施，主程序 + 服务端均编译 0 error。**未部署到服务器**（部署由另一成员负责）。

---

## 0. 结论速览

| 项目 | 改造前 | 改造后 |
|---|---|---|
| 挂机一天（不看联机页、不在房间）每客户端请求数 | ~30,240 | **0** |
| 挂机一天每客户端请求数（在房间、保持心跳） | ~30,240（还多全量房间列表） | **8,640**（仅轻量心跳） |
| 请求类型 | Ping + Heartbeat + 全量房间列表 | 仅 Heartbeat（轻量） |
| 挂机一天每客户端上传/下载字节（估算） | ~15 MB+ | **~0.5 MB**（在房间）/ 0（静默） |
| 默认服务器地址 | 写死 `http://121.4.78.52:45080` | **空**（不配置不发请求） |

---

## 1. 问题根因（改前）

### 1.1 常驻流量来源

| 位置 | 行为 | 频率 | 说明 |
|---|---|---|---|
| `Services/LobbyConnection.cs` | DispatcherTimer | 每 10s | Ping + Heartbeat，**启动即跑，永不停止** |
| `Views/LobbyPage.xaml.cs` | TickHub 5s | 每 5s | 全量 `GET /api/rooms`（联机页打开时） |

### 1.2 单个挂机客户端的请求量（改前）

以用户观察到的「约每分钟 6 次 ping + 12 次心跳 + 12 次全量房间列表」为基准：

- Ping：`GET /api/ping`，约 150 B 请求头 + 响应 ~200 B
- Heartbeat：`POST /api/heartbeat`，约 160 B + 响应 ~30 B
- 房间列表：`GET /api/rooms`，响应随房间数增长（含全部房间 + 全部座位，单房间约 500 B，10 房间约 4 KB）

**一天（86400s）估算（1 个挂机客户端）：**

| 请求 | 次数/天 | 单次请求+响应字节 | 日字节 |
|---|---|---|---|
| ping | 8,640 | ~350 B | ~3.0 MB |
| heartbeat | 8,640 | ~190 B | ~1.6 MB |
| rooms（联机页挂后台） | 17,280 | ~2 KB（10 房间） | ~35 MB |

> 合计约 **40 MB/天/客户端**（房间列表是绝对大头）。账号多、设备多、挂着不关 → 欠费直接来源。

### 1.3 服务端放大效应

服务端 `/api/rooms` 是「房间 + 座位两条查询」，但**每个客户端每 5 秒都拉一次**。N 个客户端 × 房间列表 → O(N) 次全量查询。旧服务器在客户端数量上去后被打爆。

---

## 2. 改造方案（改后）

### 2.1 活动定义（"什么算有活动"）

采用**活动引用计数**模型，只有以下任一为真才发请求：

1. **正在看联机页**（LobbyPage 可见）→ 保持 5s 房间列表轮询 + 10s 心跳
2. **本机在某个房间里**（座位 Online/PoolOnline）→ 仅保持 10s 轻量心跳（保住座位不掉线）
3. **手动操作**（建/加/离开/发车/改码/关闭/改服务器地址）→ 即时单次请求

其余情况（含"有自动发车任务但不在房间"）：**完全静默，0 请求**。

> 说明：当前客户端**没有**"自动发车任务"这类独立后台概念——发车是房主在联机页点按钮触发的。若未来加自动发车，其调度本身在客户端本机（ScheduleRunner/TickHub），无需向服务器常驻轮询；发车那一刻才发一次 `POST /api/rooms/depart`。因此"自动发车任务"不构成常驻流量来源。

### 2.2 心跳与轮询分离

| 状态 | 房间列表轮询（5s） | 心跳（10s） | 请求/分 |
|---|---|---|---|
| 不看联机页、不在房间 | ✗ | ✗ | 0 |
| 看联机页、不在房间 | ✓ | ✓ | 12 + 6 = 18 |
| 看联机页、在房间 | ✓ | ✓ | 18 |
| 不看联机页、在房间 | ✗ | ✓ | 6 |
| 未配置服务器 | ✗ | ✗ | 0 |

> 心跳从「Ping + Heartbeat 两个请求」合并为「仅 Heartbeat 一个请求」：
> 旧代码 `ProbeAsync` 里先 Ping 再 Heartbeat，一次 tick 打 2 个请求。
> 新代码活动期只发 Heartbeat（`POST /api/heartbeat`），成功即视为在线。

### 2.3 轮询 vs 长连接（SignalR / SSE / WebSocket）

**挂机一整天（1 客户端）对比：**

| 方案 | 请求数/天 | 保持连接数 | 静默时流量 | 服务端开销 | 改动成本 |
|---|---|---|---|---|---|
| 旧：10s Ping+Heartbeat + 5s 全量列表 | ~30,240 | 0（短连接） | 无（常驻） | 高（N×列表） | — |
| **新：按需心跳 + 按需轮询** | **0~8,640** | 0（短连接） | 0 | 低 | 小（本次已做） |
| 长连接 WebSocket/SSE 推送 | 1 次握手/天 | 1 长连接 | 心跳帧（~30B/30s） | 中（每连接常驻） | 大（协议+连接管理+断线重连+反向代理超时） |
| SignalR | 1 次握手/天 | 1 长连接 | keepalive 帧 | 中高（库依赖） | 大（引入包 + hub） |

**结论：**

- 对「挂机一整天」的流量，按需短连接方案已把**静默场景压到 0**，在房间场景压到 6 请求/分（仅心跳）。
- 长连接的优势在**低延迟推送**（别人建房/满员即时可见），但本场景是私人房间、低频协作，5 秒轮询的延迟完全可接受；且用户的核心痛点是**欠费**，长连接要保持一条常驻 TCP + 周期 keepalive，反而在"挂着不关"时依然有持续心跳流量。
- 长连接还引入断线重连、NAT/反向代理超时（云服务器 60s idle timeout 很常见）、服务端每连接内存/句柄占用等问题。

**推荐**：当前阶段用按需短连接（已实施）；若未来联机房间需要"秒级实时同步"，再单点引入 SSE 仅对**正在看联机页**的客户端推送房间变更，其余静默——但那是增量优化，不在本次欠费治理范围。

---

## 3. 发车人数 2-4 可配

### 3.1 数据结构（SQLite schema 迁移）

`Rooms` 表新增一列：

```sql
DepartSize INTEGER NOT NULL DEFAULT 0
```

- `0` = 未设置（旧数据/未填）→ 按房间类型取默认：单人房 4、大小号每边 2。
- `2/3/4` = 显式发车人数。

**迁移方式（向后兼容）**：`Db.Init()` 建表语句已带新列；对已有库，新增 `EnsureColumn()` 幂等迁移——用 `PRAGMA table_info` 探测，缺列才 `ALTER TABLE ... ADD COLUMN`。旧库的 `DepartSize` 全部为 0 → 旧房间继续按原规则（四人满 4、大小号满 2）发车，**零破坏**。

### 3.2 服务端校验

`Lobby.Depart()`：

- 单人房：`online >= EffectiveDepartSize(room)`
- 大小号房：本房 `>= EffectiveDepartSize(room)` 且配对房 `>= EffectiveDepartSize(partner)`
- `EffectiveDepartSize`：`DepartSize` 在 [2,4] 用显式值，否则按 Kind 取默认。

座位数：建单人房按 `effective` 建空座位；建大小号房按 `max(2, max(两边 effective))` 建座位（`InsertRoomPair` 的 `seatsEach`）。

### 3.3 客户端 UI

- 新建单人房流程：名称 → **发车人数（2/3/4 下拉）** → 房间码。
- 新建大小号房流程：名称 → 大号发车人数 → 小号发车人数 → 大号码 → 小号码。
- 房间详情：显示「满 N 人发车」（单人房）/「大号满 N · 小号满 M」（大小号房）。
- 新增 `Views/LobbyDepartSizeWindow.xaml(.cs)` 下拉选择窗口。

### 3.4 大小号房（成对）与可变人数共存方案

**方案：每间房独立 `DepartSize`。**

- 大号房、小号房各自存一个 `DepartSize`（各 2-4），互不影响。
- 发车条件：大号房在线 ≥ 大号 `DepartSize`，且小号房在线 ≥ 小号 `DepartSize`。
- 座位：成对建房的 `seatsEach` 取两房需要的最大值，保证每房都有足够空位。
- 与旧行为完全兼容：不传人数时默认 2，等价于原来的"各满 2 发车"。

---

## 4. 改动清单（file:line）

### 服务端（Server/，独立 csproj）

| 文件 | 改动 |
|---|---|
| `Server/Dto.cs` | `RoomDto.DepartSize`；`CreateRoomRequest.DepartSize`；`CreatePairRequest.BigSize/SmallSize` |
| `Server/Db.cs` | `Rooms` 建表加 `DepartSize` 列；`EnsureColumn()` 迁移；`InsertRoomCore`/`ReadRoom`/`UpdateRoom`/`RoomRow` 增 `DepartSize` |
| `Server/Lobby.cs` | `NormalizeDepartSize`/`EffectiveDepartSize`；`CreateFour`/`CreatePair` 接收人数并建对应座位；`Depart` 用 `EffectiveDepartSize` 校验 |
| `Server/Program.cs` | `/api/rooms/create` 与 `/api/rooms/create-pair` 透传人数 |

### 客户端（主程序）

| 文件 | 改动 |
|---|---|
| `Services/LobbyApi.cs` | `DefaultBaseUrl = ""`（移除旧 IP）；`HasServer`；所有接口加"未配置即短路不发请求"守卫；`HeartbeatAsync` 返回 `bool`；`CreateFourAsync`/`CreatePairAsync` 加人数参数；`RoomDto.DepartSize` |
| `Services/LobbyConnection.cs` | 重写为活动引用计数 + TickHub 心跳（10s，仅 Heartbeat 一个请求）；去掉常驻 DispatcherTimer；`Acquire/Release/SetInRoom` |
| `Views/LobbyPage.xaml.cs` | `OnPageActivated/OnPageDeactivated`；`StartPolling/StopPolling`（按需）；`UpdateInRoomStatus`；未配置服务器时静默提示；建房流程加人数选择；详情显示发车人数 |
| `Views/LobbyPage.xaml` | 页签文案「私人·四人」→「私人房间」 |
| `Views/LobbyRoomKindWindow.xaml` | 文案更新为 2-4 人 |
| `Views/LobbyDepartSizeWindow.xaml(.cs)` | 新增：发车人数下拉窗口 |
| `Views/MainWindow.xaml.cs` | `SetActivePage` 切页时调 `LobbyView.OnPageActivated/Deactivated` |
| `Models/LobbyModels.cs` | `LobbyRoom.DepartSize`；`RequiredCount` 用人数；`CanDepart` 用 `OnlineCount >= RequiredCount`；`Capacity` 用 `max(RequiredCount, seats)`；文案 |

### 未改动（遵守约束）

- `dist/`、`dist/stable/`、`backups/`：未动。
- A1/A4 账号：未用。
- 用户生产 `config.json`：未改。
- 未 commit / push / 发版。

---

## 5. 向后兼容与过渡方案

- **旧客户端 → 新服务端**：旧客户端请求体不含 `departSize`/`bigSize`/`smallSize`，服务端 `CreateRoomRequest.DepartSize` 反序列化为 0 → 走默认（四人满 4、大小号满 2），行为与旧版一致。**兼容。**
- **新客户端 → 旧服务端**：旧服务端忽略多余 JSON 字段，返回的 `RoomDto` 不含 `DepartSize` → 客户端读到默认 0 → `RequiredCount` 回退到类型默认。**兼容。**
- **旧库 → 新服务端**：`EnsureColumn` 自动补 `DepartSize=0`，旧房间保持原发车规则。**兼容。**

---

## 6. 部署注意（给部署成员）

- 服务端程序目录旧 `soul-link.db` 会在首次启动时自动迁移（补列），**无需手工改库**。
- 客户端默认不再内置服务器地址：用户必须在「设置 → 服务器」填新服务器地址后才联机。
- 新服务器地址**不得写入代码**（遵守发布域名规则），由用户/部署方在客户端配置填写。
