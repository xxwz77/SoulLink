# SoulLink 服务端 · 更新与运维

> 适用：**已经在 Ubuntu 上跑起来的** SoulLink 联机大厅服务端。
> 首次部署见 `dist-server/README.md`（该目录被 `.gitignore` 的 `dist-*/` 排除，所以运维文档单独放这里）。

## 线上地址约定

| 项 | 值 |
|---|---|
| 安装目录 | `/opt/soullink-server` |
| 服务名 | `soullink-server`（systemd） |
| 监听端口 | 5080 |
| 数据库 | `/opt/soullink-server/soul-link.db` |

## 更新后必须做的验证

```bash
curl -s http://127.0.0.1:5080/api/ping
ls -l /opt/soullink-server/soul-link.db*
```

版本号必须变；改了数据库层时还要看到 `soul-link.db-wal` / `-shm`。
**版本号不变就无法确认线上跑的是新版**，所以每次改服务端代码都要同步升
`Server/SoulLink.Server.csproj` 里的 `<Version>`。

---

## 更新已有的服务器（不是首次部署）

升级二进制**不需要重跑 `deploy.sh`** —— unit 文件、权限、防火墙都是现成的，覆盖文件再重启即可。

### 1. 本地出包

```powershell
cd E:\Documents\deepseek-harness\SoulLink
dotnet publish Server\SoulLink.Server.csproj -c Release -r linux-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
  -o artifacts\dist-server-<版本>
```

⚠ **必须带 `-p:PublishSingleFile=true`**。漏了会产出几百个 dll，而 `deploy.sh` 只拷
`SoulLink.Server` 和 `*.so`，按那个方式部署上去是个跑不起来的残包。

### 2. 备份数据库（改了数据库层时必须做）

```bash
sudo systemctl stop soullink-server
sudo cp /opt/soullink-server/soul-link.db /opt/soullink-server/soul-link.db.bak-<版本>
```

### 3. 覆盖二进制并记录哈希

```bash
# 上传后核对，避免传了一半
sha256sum /opt/soullink-server/SoulLink.Server
```

与本地 `(Get-FileHash artifacts\dist-server-<版本>\SoulLink.Server).Hash` 对比。

### 4. 启动

```bash
sudo chmod +x /opt/soullink-server/SoulLink.Server
sudo systemctl start soullink-server
sleep 2
curl http://127.0.0.1:5080/api/ping
```

### 5. 确认新版真的生效

```bash
# 版本号必须变
curl -s http://127.0.0.1:5080/api/ping

# 2026-11 之后的服务端还应看到 WAL 文件（说明新的 PRAGMA 生效）
ls -l /opt/soullink-server/soul-link.db*
# 期望多出 soul-link.db-wal 和 soul-link.db-shm
```

### 6. 回滚

```bash
sudo systemctl stop soullink-server
sudo cp /opt/soullink-server/soul-link.db.bak-<版本> /opt/soullink-server/soul-link.db
sudo cp /root/soullink-old/SoulLink.Server /opt/soullink-server/   # 部署前记得留一份旧的
sudo systemctl start soullink-server
```

⚠ **二进制和数据库必须一起回滚**。新版按 WAL 写过库，只回退二进制可能读不了。

## 更新踩过的坑

### 坑 1：`systemctl start` 静默失败 —— 撞上 start-limit

连续几次启动失败后，systemd 会拒绝再拉起这个服务，且 `systemctl start` **不一定报错**，
表现为"命令成功但端口没人监听"。

```bash
sudo systemctl reset-failed soullink-server   # 清失败计数
sudo systemctl start soullink-server
```

### 坑 2：curl 返回 `Couldn't connect to server` 但进程像是起来了

先分辨是哪一种：

```bash
sudo systemctl status soullink-server --no-pager -l
sudo ss -ltnp | grep 5080
```

- `activating (auto-restart)` → 在崩溃重启循环里，看 journal
- `active (running)` 但 5080 无监听 → 绑定了别的地址，查 `SOULLINK_URLS`
- 直接 `failed` → 看 `journalctl -u soullink-server -n 40 --no-pager`

### 坑 3：`/tmp` 是 noexec 时单文件程序起不来

单文件自包含程序启动时要把原生库解压到 `/tmp`。若 `/tmp` 挂载了 `noexec`，加载失败直接崩：

```bash
mount | grep -w /tmp        # 输出里有 noexec 就是它
```

解法——换个解压目录：

```bash
sudo systemctl edit soullink-server
# 加入：
# [Service]
# Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=/opt/soullink-server/.extract
```

### 排查入口：前台直接跑

systemd 会吞掉启动期的异常。想看真实报错就前台跑（记得先 `systemctl stop` 释放 5080）：

```bash
sudo /opt/soullink-server/SoulLink.Server
```

正常会打印：

```
SoulLink 服务端启动：http://0.0.0.0:5080
数据库：/opt/soullink-server/soul-link.db
Now listening on: http://0.0.0.0:5080
```
