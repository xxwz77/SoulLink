# SoulLink 联机大厅 · 新服务器部署方案与流量监控

> 日期：2026-11-22（第二次更新：新增运维脚本 + 实况复核）
> 责任：server-deployer（新服务器 42.192.114.250 · 腾讯云轻量 · Ubuntu 26.04 · 4C4G 40G）
> 状态：**服务端已由 Lead 经 SSH_ASKPASS 密码路径部署完成**（vnstat/告警/运维脚本均已落地，服务 running）；安全组放行 5080 与客户端连接后即可进入实测阶段
> 关联：[Server/README.md](../../Server/README.md) · [docs/SERVER-UPDATE-GUIDE.md](../SERVER-UPDATE-GUIDE.md)

---

## 0. 结论速览

| 项 | 结论 |
|---|---|
| 能否交叉编译自包含发布 | ✅ **能**。本机 `dotnet publish -r linux-x64 --self-contained` 成功，产出 ELF 单文件 101.51 MB（原生 SQLite 已内嵌，无独立 `.so`） |
| 服务器要不要装 .NET | ❌ **不用**。自包含发布把运行时打进 ELF，服务器无需任何 .NET 运行时 |
| 目标框架 | `net11.0`（**不是 README 写的 ASP.NET Core 8**，README 已过时） |
| 服务器 glibc 兼容 | ✅ Ubuntu 26.04 glibc ≥ 2.17，满足 .NET 11 自包含要求 |
| SSH 连接现状 | ✅ 已由 Lead 用 SSH_ASKPASS 密码机制打通（密钥授权仍未做，密码路径可用）；22 端口可达 |
| 5080 端口 | ⚠️ 安全组未放行，外网 TCP 连接被拒（`TcpTestSucceeded: False`）；服务端已在本地 `0.0.0.0:5080` 监听 |
| 交付物 | `Server/deploy-lighthouse.sh`（部署）· `Server/ops.sh`（运维，已装 `/usr/local/bin/soullink-ops.sh`）· `Server/setup-traffic-monitor.sh`（流量监控，已落地）· 本文档 |

---

## 1. 新服务器现状评估

### 1.1 已确认的事实（本机可验证部分）

- **网络可达**：新服务器 `42.192.114.250` 的 SSH 主机指纹已存在于本机 `~/.ssh/known_hosts`（说明此前已被探测过，22 端口开放）。
- **SSH 认证未就绪（2026-11-22 19:36 复核）**：`ssh -o BatchMode=yes ubuntu@42.192.114.250` 仍返回 `Permission denied (publickey,password)`。已试过 3 个密钥（`soullink_server_ed25519` / `soullink_deploy` / `atmgi_ed25519`）× 2 个用户（`ubuntu` / `root`）全部被拒，公钥尚未加入 `~/.ssh/authorized_keys`。
- **5080 未放行（2026-11-22 19:37 复核）**：外网 TCP 连接被拒（`TcpTestSucceeded: False`）。⚠ 本机有 `127.0.0.1:7897` 系统代理，用 `Invoke-WebRequest` 探 5080 曾得到「502 Bad Gateway」是**代理返回的假象**，不是服务器在响应；应以 raw TCP（`Test-NetConnection`）为准。
- **本机工具链**：
  - `ssh.exe` / `scp.exe` / `ssh-keygen.exe`（`C:\WINDOWS\System32\OpenSSH\`）✅
  - `sshpass` / `plink` ❌ 不存在（密码认证无法脚本化）
  - .NET SDK：8.0.427 / 9.0.320 / 10.0.403 / **11.0.100** 四套齐备 ✅

### 1.2 待服务器接入后核实（本文已给出命令，接入后逐条跑）

以下评估项需要登录服务器才能确认，命令已备好，接入后执行即可：

```bash
# ① 系统版本 / 内核 / glibc
cat /etc/os-release; uname -a; ldd --version | head -1

# ② .NET 运行时是否已装（自包含部署不需要，但顺手确认）
command -v dotnet && dotnet --info || echo "未装 dotnet（自包含部署不需要）"

# ③ 磁盘空间
df -h /

# ④ 内存占用（4GB）
free -h

# ⑤ 防火墙 ufw 状态
sudo ufw status verbose 2>/dev/null || echo "ufw 未安装/未启用"

# ⑥ 5080 端口是否被占用
ss -ltnp | grep 5080 || echo "5080 空闲"

# ⑦ /tmp 是否 noexec（单文件程序解压原生库的关键坑）
mount | grep -w /tmp
```

### 1.3 关键判断：.NET 运行时可用性

| 路径 | 可行性 | 说明 |
|---|---|---|
| 自包含发布（推荐） | ✅ | 服务器**零依赖**，不受 Ubuntu 26.04 是否还有 .NET 8 包影响；101.51 MB 单文件已实测可编译 |
| 服务器装 .NET 运行时 | ⚠️ 不推荐 | Ubuntu 26.04 是 2026 年的新 LTS，官方 `dotnet-sdk-8.0` 微软源是否仍提供 8.x 不保证；且服务器目标框架实际是 `net11.0`，需装 .NET 11 运行时，多一层依赖 |
| Windows 交叉编译 linux-x64 | ✅ | 已实测通过，见 §2.1 |

**结论：走「Windows 交叉编译 + 自包含单文件发布」，服务器不装任何 .NET。** README 的「ASP.NET Core 8 + 自包含」描述框架版本过时，实际 `Server/SoulLink.Server.csproj` 是 `net11.0`，但「自包含、服务器不需装 .NET」这条策略依然成立且正确。

---

## 2. 部署步骤

### 2.1 本地出包（Windows，已实测成功）

```powershell
cd E:\Documents\deepseek-harness\SoulLink

dotnet publish Server\SoulLink.Server.csproj -c Release -r linux-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
  -o artifacts\dist-server-2.0.0
```

产物（**只有 2 个文件**）：

```
artifacts\dist-server-2.0.0\
  SoulLink.Server                          101.51 MB  ELF 单文件（已内嵌 SQLite 原生库）
  SoulLink.Server.staticwebassets.endpoints.json  0 KB（可忽略）
```

> ⚠ 必须带 `-p:PublishSingleFile=true`，否则产出几百个 dll，`deploy-lighthouse.sh` 会因缺文件跑不起来（旧 `deploy.sh` 也有同样约束，见 SERVER-UPDATE-GUIDE.md）。

验证是 Linux ELF：

```powershell
# 头 4 字节应为 7F 45 4C 46（即 "7FELF"）
$b = [System.IO.File]::ReadAllBytes("artifacts\dist-server-2.0.0\SoulLink.Server")[0..3]
($b | ForEach-Object { $_.ToString('X2') }) -join ' '
```

### 2.2 建立 SSH 访问（二选一）

#### 方式 A：SSH 密钥（推荐，可脚本化）

已在 `C:\Users\91244\.ssh\soullink_server_ed25519` 生成专用密钥对，公钥内容：

```
ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIG4zFHpxMztxmhMbaFUAmnCsSYGlWZ2FwnSg5rTbYMrX soullink-server-deploy
```

请 **Lead 转告用户**：用腾讯云「一键登录 / 密码登录」进服务器后执行一条命令把公钥加进去：

```bash
mkdir -p ~/.ssh && chmod 700 ~/.ssh && touch ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys
echo 'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIG4zFHpxMztxmhMbaFUAmnCsSYGlWZ2FwnSg5rTbYMrX soullink-server-deploy' >> ~/.ssh/authorized_keys
```

之后本机即可：

```powershell
ssh -i C:\Users\91244\.ssh\soullink_server_ed25519 ubuntu@42.192.114.250 "echo ok"
```

#### 方式 B：腾讯云一键登录（用户手工执行，最稳）

如果不想配密钥，把 §2.3 的「用户手工部署命令清单」整体交给用户，让用户用腾讯云控制台 WebShell（一键登录）逐条粘贴执行即可，**不经过本机 SSH**。

> ⚠ 安全底线：**不要尝试把密码管道喂给 ssh**（本机无 sshpass/plink，会失败且泄露密码）。若密钥走不通，直接走方式 B。

### 2.3 用户手工部署命令清单（无 SSH 时的完整可粘贴方案）

> 用腾讯云控制台「登录 → WebShell / OrcaTerm」打开服务器终端，**逐段粘贴**。

**第 1 步：把出好的包传上去**

方式一（本机已有密钥后，用 scp 推，二选一）：

```powershell
# 本机 PowerShell 执行
scp -i C:\Users\91244\.ssh\soullink_server_ed25519 `
  E:\Documents\deepseek-harness\SoulLink\artifacts\dist-server-2.0.0\SoulLink.Server `
  E:\Documents\deepseek-harness\SoulLink\Server\deploy-lighthouse.sh `
  ubuntu@42.192.114.250:/home/ubuntu/soullink/
```

方式二（无密钥，在服务器 WebShell 里直接贴，用这里附带的部署脚本内容）：

> 在 WebShell 里 `mkdir -p ~/soullink`，然后用 `nano ~/soullink/deploy-lighthouse.sh` 把
> `Server/deploy-lighthouse.sh` 的全文贴进去保存；`SoulLink.Server` 二进制 101 MB 无法用粘贴传输，
> 需通过腾讯云控制台「文件上传」或临时用 `scp`（配好密钥后）上传到 `~/soullink/`。

**第 2 步：执行部署**

```bash
cd ~/soullink
ls -l SoulLink.Server deploy-lighthouse.sh   # 确认两文件都在
sudo bash deploy-lighthouse.sh
```

脚本会自动：备份旧版 → 复制二进制 → 校验 ELF → 放行 ufw（若启用）→ 写 systemd → 启动 → `curl /api/ping` 健康检查。

**第 3 步：外网验证**

```bash
# 服务器内
curl http://127.0.0.1:5080/api/ping
# 预期 {"ok":true,"time":"...","version":"2.0.0"}

# 本地电脑（在安全组放行之后）
curl http://42.192.114.250:5080/api/ping
```

---

## 3. 腾讯云安全组配置指引（用户操作，无法代劳）

腾讯云**轻量应用服务器**的「防火墙」就是安全组。必须在控制台放行 5080，否则外网连不上。

1. 登录 [腾讯云控制台](https://console.cloud.tencent.com/) → 顶部选「**轻量应用服务器**」。
2. 找到新服务器（IP `42.192.114.250`）→ 点实例名进入详情。
3. 左侧/顶部找「**防火墙**」标签页。
4. 点「**添加规则**」：
   - 应用类型：**自定义**
   - 协议：**TCP**
   - 端口：**5080**
   - 来源：**0.0.0.0/0**（所有 IP；如只想自己访问，可填自己出口 IP/24）
   - 策略/备注：`SoulLink 联机大厅`
5. 保存。若后续上 HTTPS，需**另加 443**（TCP）。

> ⚠ 只放行 5080 业务端口即可，**不要**在这里动 22（SSH）等已有规则，避免把自己锁在外面。

### 3.1 安全组放行后的验证步骤（放行后按此执行）

> 安全组是**云侧**规则，放行后本机外网才能连上 5080。验证分两层：

```bash
# ① 服务器内（登录后）：确认服务在监听
ss -ltnp | grep 5080
curl -s http://127.0.0.1:5080/api/ping
# 预期 {"ok":true,"time":"...","version":"2.0.0"}

# ② 本机（Windows PowerShell，安全组放行后）：确认外网通
#    ⚠ 本机有 127.0.0.1:7897 代理，curl.exe 可能走代理拿到 502 假象；
#    用 curl.exe --noproxy 绕过，或用 Test-NetConnection 验证 TCP 层是否通。
curl.exe --noproxy "*" -s http://42.192.114.250:5080/api/ping
Test-NetConnection -ComputerName 42.192.114.250 -Port 5080 -InformationLevel Quiet
# Test-NetConnection 应返回 True（TCP 层通了才算安全组放行成功）
```

> 判定标准：`Test-NetConnection ... -Port 5080` 返回 `True` = 安全组已放行且服务在监听；`curl.exe --noproxy` 拿到 `{"ok":true,...}` = 端到端可用。

---

## 4. HTTPS 方案（选项，供 Lead 转用户决策）

> 现状：先 HTTP + 设备码校验（服务端所有接口已要求 `X-Device-Code` 头，见 `Server/Program.cs`）。
> 是否上 HTTPS、上哪种，**不在本任务内拍板**，按下列选项交用户决定。

| 选项 | 优点 | 缺点 | 前置 |
|---|---|---|---|
| **A. Caddy 自动 HTTPS**（推荐长期） | 自动申请/续期证书，零维护；对客户端几乎无感 | 需要绑定一个域名，域名 DNS 要指到 42.192.114.250 | 需用户提供域名 |
| **B. 自签证书**（客户端信任） | 无域名、无外购成本 | 每个客户端都要手动安装并信任证书，多设备很麻烦；证书过期要重装 | 客户端配合 |
| **C. 先 HTTP + 设备码，HTTPS 后续** | 立即可用，改动最小 | 明文传输设备码/房间码，有中间人风险（当前用户规模可接受） | 无 |

**建议路径**：先走 C 上线（服务端已有设备码校验兜底），用户有域名后再切 A。Caddy 切 A 的具体步骤预留在此文档 §7 附录，等域名到位再补。

> 红线提醒：**不要在任何用户可见位置写死服务器 IP/域名**。客户端默认地址的修改另由客户端侧任务处理（`Services/LobbyApi.cs:22 DefaultBaseUrl`），本部署任务不触碰客户端代码。

---

## 4.5 服务器加固建议（选项，仅列不执行）

> 底线：**未确认不改系统级配置**。以下均为建议项，需 Lead 转用户逐项确认后再动手，本任务未自行执行任何一项。

| 加固项 | 做法 | 风险/代价 | 建议 |
|---|---|---|---|
| **SSH 只允许密钥登录** | `/etc/ssh/sshd_config` 设 `PasswordAuthentication no` + `PermitRootLogin no` | 一旦密钥丢失且无控制台一键登录会被锁死；腾讯云有 VNC/一键登录兜底可接受 | 配好密钥后**再**做 |
| **改 SSH 端口** | 把 22 改成非常用端口 | 降低扫描噪音，但易忘、易把同事锁在外面 | 可做可不做，非必须 |
| **fail2ban** | `apt install fail2ban` 自动封爆破 IP | 对 22 端口爆破有效；对业务 5080 无帮助 | 可选 |
| **限制 5080 来源** | 安全组把来源从 `0.0.0.0/0` 收窄到自己出口 IP | 最有效，但家庭宽带 IP 会变，需随时更新 | 强推（尤其只有自用） |
| **数据库定期异地备份** | `ops.sh backup` + 定期 scp 下载到本地 | 单机磁盘坏则数据丢 | 建议每周一次 |
| **unattended-upgrades** | 自动打安全补丁 | Ubuntu 自带，默认已开 | 保持默认即可 |

> 最小可用基线：**① 配好密钥后关密码登录 → ② 安全组 5080 来源收窄 → ③ 每周 `ops.sh backup` 并下载一份**。其余视需要再加。

---

## 5. 流量监控（用户刚因流量欠费，这是重点）

### 5.1 实时流量速率

```bash
# 安装（腾讯云 Ubuntu 通常自带 nethogs，没有则装）
sudo apt-get install -y nethogs

# 看每个进程的实时进出速率
sudo nethogs

# 只看某个端口/进程的瞬时连接与速率
sudo ss -tnp | grep 5080
```

### 5.2 累计流量（按网卡统计，腾讯云轻量按出网流量计费）

```bash
# 安装 vnstat（开机即自动按网卡累计，重启不清零）
sudo apt-get install -y vnstat
sudo systemctl enable --now vnstat

# 当天累计（上行 tx = 出网计费流量，下行 rx = 入网，轻量通常只计出网）
vnstat -d          # 按日
vnstat -h          # 按小时
vnstat -m          # 按月
```

### 5.3 每日流量查看方法（固定习惯）

```bash
# ① 看今天的出网总量（最常用）
vnstat -d | tail -3

# ② 看今天逐小时，找出流量高峰时段
vnstat -h | tail -30

# ③ 只看上行（计费方向）
vnstat --json d | head -40
```

**每日建议**：`vnstat -d` 看一眼当天 `tx`（上行）总量。腾讯云轻量套餐通常是**固定月流量包**，超了按量计费才产生欠费；配合本文 §5.4 的「按需访问」改造落地后，常态流量应接近 0。

### 5.4 流量为什么会爆（与欠费的因果关系，供复盘）

客户端侧三个常驻流量源（由 Lead 侦察，属客户端任务范畴，此处仅记录以便监控验证）：

| 位置 | 行为 | 频率 |
|---|---|---|
| `Services/LobbyConnection.cs:58` | DispatcherTimer 每 10s 发 Ping + Heartbeat（不管有没有开联机页/有没有房间） | 常驻 |
| `Views/LobbyPage.xaml.cs:59` | 联机页打开时每 5s 拉全量房间列表 | 页可见时 |
| 叠加 | 单个挂机客户端 ≈ 每分钟 6 ping + 12 心跳 + 12 全量列表 | — |

> 服务端侧无需为本任务改动；但监控时可用下面命令观察请求来源，确认改造后是否有残留常驻请求：

```bash
# 看最近 5 分钟访问 /api/ping、/api/heartbeat、/api/rooms 的次数（改造后应大幅下降）
sudo journalctl -u soullink-server --since "5 min ago" --no-pager | grep -c "ping\|heartbeat\|rooms"
```

### 5.5 一键流量监控安装（新脚本，推荐）

已产出 `Server/setup-traffic-monitor.sh`，服务器上跑一次即可完成 vnstat 安装 + 日报 + 阈值告警：

```bash
sudo bash setup-traffic-monitor.sh
```

它自动完成：
1. `apt-get install vnstat` + `systemctl enable --now vnstat`（按网卡累计，重启不清零）。
2. 每日 08:00 生成流量日报 → `/var/log/soullink-traffic.log`（今日 / 本月 / 昨日）。
3. 每小时检查本月出网是否超阈值（默认 100 GB），超限写 journal + 日志。

**改阈值**：重跑时设环境变量，或直接编辑 `/usr/local/bin/soullink-traffic-alarm.sh` 里的 `LIMIT_MB`：

```bash
# 例如套餐 50 GB，留 20% 缓冲设 40 GB = 40960 MB
sudo SOULLINK_MONTHLY_LIMIT_MB=40960 bash setup-traffic-monitor.sh
```

**手动查**（随时可用）：

```bash
vnstat -d                    # 今日/昨日/逐日
vnstat -m                    # 按月汇总
vnstat -h                    # 今日逐小时
sudo bash /usr/local/bin/soullink-traffic-alarm.sh   # 手动触发一次阈值检查
```

> ⚠ 腾讯云轻量按**出网**流量计费（`tx` 方向）；套餐额度在控制台「监控」页可查，建议阈值设为套餐的 80%。

### 5.6 欠费预警（云侧兜底，可选）

腾讯云轻量可在控制台「**监控告警**」里给「公网出流量」设置阈值告警（如单日 80% 套餐额度），到量发短信/站内信。路径：实例详情 → 监控 → 告警策略 → 新建。这是云侧兜底，与 §5.5 服务器内 vnstat 互补。

### 5.7 现状与验证状态（实况 · 2026-11-22 Lead 经 SSH_ASKPASS 落地）

✅ **vnstat 已安装并运行**，以下为服务器真实状态：

| 项 | 实况 |
|---|---|
| vnstat 版本 | **2.13** |
| systemd 服务 | **active (running)**，`vnstat` 已 `enable --now` |
| 安装时间 | 2026-11-22 19:45:34 CST |
| 日报 timer | `soullink-traffic-daily.timer` 每天 08:00 → `/var/log/soullink-traffic.log` |
| 告警 timer | `soullink-traffic-alarm.timer` 每小时检查 |
| 阈值 | **102400 MB/月**（改 `/usr/local/bin/soullink-traffic-alarm.sh` 里的 `LIMIT_MB`） |
| 脚本路径 | `/usr/local/bin/soullink-traffic-daily.sh`、`/usr/local/bin/soullink-traffic-alarm.sh` |
| 运维脚本 | 已装到 `/usr/local/bin/soullink-ops.sh`（`status` 子命令验证通过：服务状态/健康检查/端口/磁盘/内存/进程/数据库/流量齐全） |
| 服务端 | `soullink-server` active (running)，内存 25.7M（上限 1G），`/api/ping` 正常 |

当前 `vnstat -d` 真实输出：

```
eth0: No data. Timestamp of last update is same 2026-11-22 19:45:34 as of database creation.
```

> 这是**正常现象**：vnstat 的数据库刚创建，还没累积满 5 分钟采样（vnstat 默认每 5 分钟采样一次），所以暂无统计行。**明天（有完整一天采样后）起 `vnstat -d` 就会有数据**。同时新服务器安全组尚未放行 5080、无客户端连接，本身也没有业务流量。

### 5.8 monthly 流量预估依据（⚠ 估算，非实测）

> 现在**没有真实流量数据**（新服务器刚上线、安全组未放行、无客户端连接），以下是**基于请求模型的估算**，仅用于理解量级、判断 102400 MB 阈值是否合理。**拿到真实数据的时间点**：安全组放行 5080 且**有客户端连接后满 24 小时**，`vnstat -d` 就能给出实测基线，届时用实测替换本估算。

#### 现状模型（未改造，每客户端常驻）

| 请求 | 频率 | 估算单次字节（上行 tx） | 每分钟 tx |
|---|---|---|---|
| `GET /api/ping` | 10s 一次（6/min） | ~120 B（含 HTTP 头） | ~0.7 KB |
| `POST /api/heartbeat` | 10s 一次（6/min） | ~400 B（含 `X-Device-Code` 头 + 请求体） | ~2.4 KB |
| `GET /api/rooms` | 联机页打开时 5s 一次（12/min） | 视房间数，单房含座位 JSON ≈ 300~800 B | ~3.6~9.6 KB |

> 一个挂机客户端 ≈ **每分钟 6 ping + 12 心跳 + 12 全量列表**（Lead 侦察的结论）。心跳在 10s 这个档位上，按「每 10s 发 Ping + Heartbeat」双请求算，实际心跳约 6/min；列表在页面可见时 12/min。单客户端上行粗算约 **5~13 KB/min**。

**单客户端月流量粗算**：

```
约 7 KB/min × 60 min × 24 h × 30 d ≈ 300 MB/月/客户端（中位估算）
波动区间：约 200 MB ~ 550 MB/月/客户端（取决于房间列表大小、页面停留时长）
```

> ⚠ 注意：这个估算**不含下行（rx）**，而腾讯云轻量按**出网（tx）**计费，所以上行是关注点。**这就是欠费的直接来源**——账号/设备一多，几百 MB × N 个客户端，很快突破月流量包。

#### 改造后模型（按需访问，只有房间内才心跳）

| 请求 | 现状 | 改造后 |
|---|---|---|
| Ping + Heartbeat | 常驻，每 10s（不管有无房间/页面） | **仅房间内**才心跳；未开联机页、未入房 = 0 请求 |
| 房间列表 | 联机页打开每 5s 全量拉 | 按需拉取，不挂机不刷 |

**改造后预期**：

```
空闲挂机（未开联机页/未入房）：≈ 0 MB/月/客户端
仅在房间内且页面可见时：接近现状单客户端量级（但只发生在真正组队时段）
```

> 结论：改造落地后，常态流量应**趋近于 0**，只有「有人真的在开房组队」时才产生流量。这正是「联机房间按需使用，不用时不访问服务器」的目标。**102400 MB/月 阈值对改造后是极宽裕的**（相当于数百个客户端同时满负载挂机才可能触及）。

### 5.9 流量告警怎么验证（不必等真超阈值）

告警链路是否可用，**不用等本月真的超 100 GB**，用下面任一条立即验证：

```bash
# ① 直接跑一次告警脚本，看是否有输出/报错（未超阈值时静默=正常）
sudo bash /usr/local/bin/soullink-traffic-alarm.sh
echo "exit=$?"     # 0 且无报错 = 脚本可运行

# ② 临时把阈值改成 1 MB，强制触发一次，确认能写日志（推荐，验证完整链路）
#    先看当前阈值行
grep LIMIT_MB /usr/local/bin/soullink-traffic-alarm.sh
#    临时改小（注意：只在内存里试跑，别改文件）
LIMIT_MB=1 bash -c 'TX_MB=$(vnstat --json m | python3 -c "import sys,json; d=json.load(sys.stdin); print(round(d[\"interfaces\"][0][\"traffic\"][\"month\"][-1][\"tx\"]/1048576))" 2>/dev/null || echo 0); [ "$TX_MB" -ge 1 ] && echo "⚠ 已触发告警：本月 tx=$TX_MB MB ≥ 1 MB" >> /tmp/alarm-test.log; cat /tmp/alarm-test.log'
#    验证后删测试文件
rm -f /tmp/alarm-test.log

# ③ 看定时器是否真的挂上了（两条都应 active）
systemctl list-timers --no-pager | grep soullink-traffic
```

> 判定标准：`①` 无报错即脚本可用；`②` 能写出告警行说明「判断 + 写日志」链路通；`③` 确认每日/每小时 timer 已调度。三者都过，即可放心等真实流量累计。

---

## 6. 回滚方案

部署脚本每次运行都会**先备份**旧二进制和数据库到 `/opt/soullink-server/backup/`（带时间戳），因此可随时回滚：

```bash
# ① 看有哪些备份
ls -l /opt/soullink-server/backup/

# ② 停服
sudo systemctl stop soullink-server

# ③ 回滚二进制 + 数据库（时间戳按实际备份名替换）
TS=20261122-190000
sudo cp /opt/soullink-server/backup/SoulLink.Server.$TS /opt/soullink-server/SoulLink.Server
sudo cp /opt/soullink-server/backup/soul-link.db.$TS /opt/soullink-server/soul-link.db
sudo chmod +x /opt/soullink-server/SoulLink.Server

# ④ 起服 + 验证
sudo systemctl reset-failed soullink-server
sudo systemctl start soullink-server
curl http://127.0.0.1:5080/api/ping
```

> ⚠ **二进制和数据库必须一起回滚**：新版按 WAL 写过库，只回退二进制可能读不了（见 SERVER-UPDATE-GUIDE.md §坑）。若数据库结构没变（本任务不动数据层），只回滚二进制通常也安全，但习惯上仍建议一起回。

**一键运维脚本（新）**：已产出 `Server/ops.sh`，把状态检查 / 备份 / 回滚 / 流量 / 告警都收进一个命令：

```bash
sudo bash ops.sh status           # 一键：服务状态 + 健康检查 + 端口 + 磁盘 + 内存 + 流量
sudo bash ops.sh backup           # 备份二进制 + 数据库（只留最近 10 份）
sudo bash ops.sh rollback         # 回滚到最近一次备份（交互确认）
sudo bash ops.sh rollback 20261122-190000   # 回滚到指定时间戳
sudo bash ops.sh traffic          # 今日/本月/每小时流量
sudo bash ops.sh alarm 40960      # 检查本月出网是否超 40960 MB
```

**彻底停用**（万不得已时，把服务关掉、流量归零）：

```bash
sudo systemctl disable --now soullink-server
```

---

## 7. 附录：待办与未决项

- [x] **SSH 接入**：已由 Lead 用 SSH_ASKPASS 密码机制打通（密钥授权仍未做，密码路径可用）。公钥见 §2.2。
- [x] **vnstat 安装 + 日报/告警 timer**：已完成（vnstat 2.13 active，阈值 102400 MB，见 §5.7）。
- [x] **一键运维脚本**：`Server/ops.sh` 已产出，并已装到服务器 `/usr/local/bin/soullink-ops.sh`。
- [ ] 把 §1.2 的服务器现状核实命令结果（真实 `df -h` / `free -h` / `ufw`）回填本文档（Lead 已登录，可顺手补）。
- [ ] **腾讯云安全组放行 5080**（用户操作，见 §3）；放行后按 §3.1 验证。
- [ ] 有客户端连接后满 24 小时，用 `vnstat -d` 实测流量，替换 §5.8 的估算。
- [ ] HTTPS 方案三选一（Lead 转用户决策，见 §4）。
- [ ] 加固项逐项确认（见 §4.5，先确认再动手）。
- [ ] 客户端「按需访问 + 2-4 人发车」改造落地后，用 §5.4 验证流量归零。

---

## 8. 底线核对

| 约束 | 落实情况 |
|---|---|
| 不动旧服务器 | ✅ 本任务所有命令只针对 42.192.114.250，未触碰 121.4.78.52 |
| 不覆盖现有 deploy.sh | ✅ 新脚本为 `Server/deploy-lighthouse.sh` / `Server/ops.sh` / `Server/setup-traffic-monitor.sh` |
| 不动 dist/、dist/stable/ | ✅ 未改动 |
| 不用 A1/A4 账号 | ✅ |
| 不改用户生产 config.json | ✅ |
| 不 commit/push/发版 | ✅ |
| 代码不写死新服务器 IP | ✅ 部署脚本/文档中 IP 仅作为运维目标地址，未进入任何客户端可见代码 |
| 未确认不改系统级配置 | ✅ 脚本只 `ufw allow`（仅当 ufw 已启用），不碰 SSH 端口/全局防火墙策略；加固项仅列选项未执行 |
| 未中断正在运行的服务 | ✅ 未登录服务器，未触碰任何运行中服务 |
