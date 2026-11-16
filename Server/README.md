# SoulLink 联机大厅服务端

部署在腾讯云轻量应用服务器（Ubuntu）上，为客户端提供联机大厅功能。

## 技术栈

- ASP.NET Core 8 Minimal API
- SQLite（单文件数据库，零配置）
- 自包含发布（服务器不需要装 .NET）

## 快速部署

### 1. 本地打包（Windows 上执行）

```powershell
cd Server
dotnet publish -c Release -r linux-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:DebugType=none -o ../dist-server
```

产物在 `dist-server/`：
- `SoulLink.Server`（约 88 MB，可执行文件）
- `libe_sqlite3.so`（SQLite 原生库）

### 2. 上传到服务器

用 WinSCP / scp 把 `dist-server/` 整个目录 + `deploy.sh` 传到服务器，例如 `/root/soullink/`。

```bash
scp -r dist-server/* deploy.sh root@你的服务器IP:/root/soullink/
```

### 3. 一键部署

```bash
cd /root/soullink
sudo bash deploy.sh
```

脚本会自动：
- 安装到 `/opt/soullink-server`
- 创建 systemd 服务并开机自启
- 放行防火墙 5080 端口

### 4. 腾讯云安全组

⚠ **必须手动在腾讯云控制台放行 5080 端口**，否则外网连不上。

路径：轻量应用服务器 → 防火墙 → 添加规则 → TCP 5080

### 5. 验证

```bash
# 服务器上
curl http://127.0.0.1:5080/api/ping

# 本地电脑
curl http://你的服务器IP:5080/api/ping
```

返回：
```json
{"ok":true,"time":"2026-11-17T...","version":"1.11.1.0"}
```

## 常用命令

```bash
systemctl status  soullink-server    # 查看状态
systemctl restart soullink-server    # 重启
systemctl stop    soullink-server    # 停止
journalctl -u soullink-server -f     # 实时日志
```

## 数据

- 数据库：`/opt/soullink-server/soul-link.db`
- 备份：直接复制这个文件即可
- 重置：删掉它，重启服务会自动重建

## 接口

所有接口都要求请求头 `X-Device-Code`（设备码，客户端生成）。

### 设备

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | /api/device/register?name=xxx | 注册/更新设备 |

### 房间

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | /api/rooms | 列出所有房间（房间码只对创建者可见） |
| GET | /api/rooms/{id} | 房间详情 |
| POST | /api/rooms/create | 建私人·四人房 |
| POST | /api/rooms/create-pair | 建私人·大小号房（成对） |
| POST | /api/rooms/join | 用房间码加入 |
| POST | /api/rooms/{id}/leave | 离开 |
| POST | /api/rooms/depart | 发车（返回所有成员的发车动作） |
| POST | /api/rooms/change-code | 改房间码 |
| DELETE | /api/rooms/{id} | 关闭房间 |

### 发车动作

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | /api/depart-config | 读本设备的发车动作 |
| POST | /api/depart-config | 保存本设备的发车动作 |

## 业务规则

### 房间类型

- **私人·四人**：一个房间，满 4 人发车
- **私人·大小号**：成对的两间（大号房 + 小号房），各满 2 人才能发车

### 可见性

- 所有房间对所有人可见（能看到名称、类型、在线人数）
- **房间码只对创建者可见**（码就是通行证）

### 限制

- 每个设备最多 1 个私人·四人房 + 1 对私人·大小号房
- 大小号房的成员不能跨房间上线（两两绑定）
- 发车后自动全员下线

### 自动清理

房间超过 **3 天**没有活动会自动删除。活动包括：
上线、下线、改房间码、发车。

## 发车流程

```
房主点「发车」
  → POST /api/rooms/depart
  → 服务端校验人数
  → 返回所有成员的 DeviceCode + 各自的发车动作
  → 各客户端匹配自己的 DeviceCode，执行自己的动作
  → 服务端清空座位（全员下线）
```

## 修改端口

```bash
# 编辑服务配置
sudo nano /etc/systemd/system/soullink-server.service

# 改这一行
Environment=SOULLINK_URLS=http://0.0.0.0:5080

# 重载
sudo systemctl daemon-reload && sudo systemctl restart soullink-server
```

## 排查

**服务起不来**
```bash
journalctl -u soullink-server -n 50 --no-pager
```

**外部连不上**
1. 服务器内部 `curl http://127.0.0.1:5080/api/ping` 通不通
2. 腾讯云安全组是否放行 5080
3. 服务器 ufw 是否放行 `sudo ufw status`

**数据库损坏**
```bash
sudo systemctl stop soullink-server
sudo mv /opt/soullink-server/soul-link.db /opt/soullink-server/soul-link.db.bak
sudo systemctl start soullink-server
```
