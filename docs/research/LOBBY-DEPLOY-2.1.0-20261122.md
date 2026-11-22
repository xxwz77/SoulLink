# SoulLink 联机大厅服务端 · 部署记录（2.1.0）

> 日期：2026-11-22
> 执行：server-engineer（team_mwrr48fjaninl6 / task_mwrsgd68i255nc）
> 目标：新服务器 42.192.114.250（腾讯云轻量 · Ubuntu 26.04 · 4C4G 40G）
> 关联：[LOBBY-OPTIMIZE-20261122.md](LOBBY-OPTIMIZE-20261122.md)（客户端/服务端改造） · [LOBBY-DEPLOY-20261122.md](LOBBY-DEPLOY-20261122.md)（部署方案与流量监控）

---

## 0. 结论

| 项 | 结果 |
|---|---|
| 服务端版本 | **2.0.0 → 2.1.0**（`/api/ping` 已返回 `2.1.0.0`） |
| 服务状态 | `soullink-server` **active (running)** |
| schema 迁移 | `Rooms.DepartSize` 列已自动迁移（`EnsureColumn` 生效） |
| 发车人数可配 | 建 3 人房、大小号房（大2小4）均验证通过，`departSize` 正确落库/读回 |
| 发车校验 | 3 人房 1 人发车 → `人数不够（需要 3 人，当前 1）` ✅ |
| 测试数据 | 已全部清理（rooms/seats/devices = 0） |
| 旧二进制/数据库 | 已备份 `/opt/soullink-server/backup/`（时间戳 20261122-200739） |
| 未动 | 旧服务器 121.4.78.52、云防火墙/安全组 |

---

## 1. 部署步骤（实际执行）

### 1.1 本地出包

```powershell
dotnet publish Server\SoulLink.Server.csproj -c Release -r linux-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
  -o artifacts\dist-server-2.1.0
```

产物：`artifacts\dist-server-2.1.0\SoulLink.Server`（ELF 101.51 MB）
本地 SHA256：`FF6E91BBAA04D18841C10FB07A2B8F4774450C4BB427FAC7A7C7E392A2E79BEC`

### 1.2 版本号

`Server/SoulLink.Server.csproj` 的 `<Version>` 从 `2.0.0` → `2.1.0`（本次联机大厅改造升版本，见 SERVER-UPDATE-GUIDE.md §更新后必须做的验证）。

### 1.3 服务器备份（部署前）

```bash
TS=20261122-200739
cp /opt/soullink-server/SoulLink.Server            → backup/SoulLink.Server.$TS
cp /opt/soullink-server/soul-link.db               → backup/soul-link.db.$TS
cp /opt/soullink-server/soul-link.db-wal           → backup/soul-link.db-wal.$TS
cp /opt/soullink-server/soul-link.db-shm           → backup/soul-link.db-shm.$TS
```

### 1.4 上传 + 部署

- scp 上传到 `/home/ubuntu/SoulLink.Server.new`，服务器侧 `sha256sum` 与本地一致后覆盖。
- 覆盖 `/opt/soullink-server/SoulLink.Server` → `chmod +x` → `daemon-reload` → `reset-failed` → `restart`。
- 启动后 `/api/ping` 返回 `2.1.0.0`。

---

## 2. 验证结果

### 2.1 schema 迁移

```python
# PRAGMA table_info(Rooms) 结果
['Id','Code','Title','Kind','OwnerDevice','PairId','PairRole','Departed','CreatedAt','LastActiveAt','DepartSize']
```

`DepartSize` 列已在服务首次启动时由 `EnsureColumn()` 自动 `ALTER TABLE ADD COLUMN` 补上（旧库本无此列）。

### 2.2 发车人数 2-4 可配

| 操作 | 请求 | 结果 |
|---|---|---|
| 建 3 人房 | `POST /api/rooms/create` `{departSize:3}` | `departSize:3`，建 3 个空座位 |
| 建 2 人房（同设备） | `POST /api/rooms/create` `{departSize:2}` | 拒绝「你已经有一个私人房间了」（每设备每类型限一房，符合预期） |
| 建大小号房 | `POST /api/rooms/create-pair` `{bigSize:2,smallSize:4}` | 大号 `departSize:2`、小号 `departSize:4`，各建 4 座位（取 max） |
| 读房间列表 | `GET /api/rooms` | 三个房间均带正确 `departSize` 字段 |

### 2.3 发车校验

- 3 人房只有房主 1 人 → `POST /api/rooms/depart` 返回 `{ok:false, message:"人数不够（需要 3 人，当前 1）"}` ✅

### 2.4 测试数据清理

- 删除 3 人房、大小号房（删大号房连配对一起删）。
- 最终 `rooms/seats/devices = 0`。

---

## 3. 回滚方法

```bash
TS=20261122-200739   # 部署前的备份时间戳

sudo systemctl stop soullink-server
sudo cp /opt/soullink-server/backup/SoulLink.Server.$TS /opt/soullink-server/SoulLink.Server
sudo cp /opt/soullink-server/backup/soul-link.db.$TS /opt/soullink-server/soul-link.db
sudo rm -f /opt/soullink-server/soul-link.db-wal /opt/soullink-server/soul-link.db-shm
sudo chmod +x /opt/soullink-server/SoulLink.Server
sudo systemctl reset-failed soullink-server
sudo systemctl start soullink-server
curl http://127.0.0.1:5080/api/ping   # 应回到 2.0.0.0
```

> 说明：本次部署前旧库是空的（无房间数据），schema 迁移仅加了列、未改任何数据行，因此即使只回滚二进制也安全；按惯例仍建议二进制+数据库一起回。

---

## 4. 底线核对

| 约束 | 落实情况 |
|---|---|
| 不动旧服务器 121.4.78.52 | ✅ 全部命令只针对 42.192.114.250 |
| 不动云防火墙/安全组 | ✅ 未触碰腾讯云控制台安全组；服务端 ufw 未启用、未做全局策略变更 |
| 不 commit / push / 发版 | ✅ |
| 代码不写死新服务器 IP | ✅ 服务器 IP 仅作为本次运维 SSH 目标，未写入任何客户端代码 |
| 不用 A1/A4 账号 | ✅ 测试用 `DEPLOY-TEST-DEV1` 设备码 |
| 测试房间清理 | ✅ rooms/seats/devices 归零 |
| 备份与回滚说明 | ✅ §1.3 / §3 |
