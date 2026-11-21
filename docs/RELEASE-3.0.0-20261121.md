# SoulLink 发布记录 · 3.0.0 上线（dist 清理 + 正式发版）

> 时间：2026-11-21 11:12
> 版本：客户端 **3.0.0.0** · 服务端 **2.0.0.0**
> 分支：`main` @ `a495ec2`（已推送 origin）
> 性质：**正式发版**，dist 目录清理 + SoulLink.exe 更新为 3.0.0

---

## 0. 一句话

3.0.0 已发布到 `dist\SoulLink.exe`（3.28 MB）。
`dist` 从 **2071 MB 清理到 397 MB**，释放 1674 MB。
详细技术交接见 [HANDOFF-3.0.0-20261121.md](HANDOFF-3.0.0-20261121.md)。

---

## 1. dist 清理

### 1.1 清理前

```
42 个文件 / 2071.24 MB
其中历史版本 exe 19 个，最老的到 2.8.7（2026-11-18）
```

### 1.2 删除清单（18 个历史 exe + 1 个旧 next 包）

| 文件 | 大小 | 说明 |
|---|---|---|
| SoulLink-2.8.7.exe | 63.91 MB | |
| SoulLink-2.8.8-before-logfix.exe | 63.92 MB | |
| SoulLink-2.8.8-logfix.exe | 63.92 MB | |
| SoulLink-2.8.8-prev.exe | 63.92 MB | |
| SoulLink-2.8.9-nomanual.exe | 63.92 MB | |
| SoulLink-2.9.0-before-mutex.exe | 63.92 MB | |
| SoulLink-2.9.1-before-mode.exe | 63.92 MB | |
| SoulLink-2.10.0-before-attach.exe | 63.92 MB | |
| SoulLink-2.10.1-before-focus.exe | 63.92 MB | |
| SoulLink-2.10.2-before-icon.exe | 63.92 MB | |
| SoulLink-2.17.17-before-ocxfix.exe | 141.76 MB | 最大单个 |
| SoulLink-2.17.19.exe | 64.35 MB | |
| SoulLink-2.17.44-before-menu-a.exe | 64.36 MB | |
| SoulLink-2.17.47-before-inputfix.exe | 64.37 MB | |
| SoulLink-2.17.48-before-boundfix.exe | 64.37 MB | |
| SoulLink-2.17.48.exe | 64.37 MB | |
| SoulLink-2.17.49-before-perf.exe | 64.37 MB | |
| SoulLink-Verify.exe | 64.38 MB | 临时验证副本 |
| next\SoulLink.exe | 64.08 MB | 2.17.19 时代的分发暂存 |
| **stable 里的 5 个旧版** | | |
| SoulLink-Stable-1.5.3.exe | 63.04 MB | |
| SoulLink-Stable-1.11.0.exe | 63.12 MB | |
| SoulLink-Stable-2.2.0.exe | 63.20 MB | |
| SoulLink-Stable-2.10.2.exe | 63.92 MB | |
| SoulLink-Stable-2.17.4-before-menu-a.exe | 64.33 MB | |

**合计删除 24 个文件，释放约 1674 MB。**

### 1.3 保留清单（及保留理由）

| 文件 | 大小 | 为什么保留 |
|---|---|---|
| `SoulLink.exe` | 3.28 MB | **3.0.0 正式版** |
| `SoulLink-2.17.50-before-net11.exe` | 64.38 MB | **唯一的回退点**（最后一个 net8 版本） |
| `agent\SoulLink.Agent.exe` | 63.92 MB | 手动代理，Broker 部署时复制到 Program Files |
| `stable\SoulLink-Stable.exe` | 64.37 MB | **当时正在运行**（PID 66024），挂机验证版 |
| `stable\SoulLink.exe` | 64.33 MB | 与 Stable 同批分发 |
| `stable\SoulLink.BrokerSetup.exe` | 33.60 MB | Broker 安装器 |
| `tools\SoulLink.BrokerSetup.exe` | 33.60 MB | 同上（dist 根下的 tools 是分发位置） |
| `sl-rescue.exe` ×2 | 33.48 MB ×2 | 自救工具 |
| `7za.exe` ×2 | 1.23 MB ×2 | BGI 更新解压依赖 |
| `broker-*.ps1` | 极小 | Broker 安装/卸载脚本 |

### 1.4 清理后

```
18 个文件 / 396.92 MB   （从 2071.24 MB 降下来）
```

---

## 2. 发版操作

### 2.1 前置：必须先退出占用的实例

`dist\SoulLink.exe` 之前一直被托盘实例锁着（PID 187028，2.17.50）。
`build.bat` 的设计是**覆盖失败就明确报错、保留暂存产物**，不会静默失败。

本次处理：

1. 确认该实例**没有进行中的任务**（last-command.log 最后活动 2026-11-20 16:17，
   boot.log 10:06，距当时已过 1 小时）
2. `CloseMainWindow()` 优雅关闭未生效（托盘程序常见），改用 `Stop-Process -Force`
3. 确认文件锁已释放后才继续

> ⚠ 如果当时有正在跑的挂机任务，绝不能强杀。判断依据就是
> `last-command.log` 的尾部时间和 `boot.log` 的活跃度。

### 2.2 执行

```bat
build.bat
```

输出：

```
版本：3.0.0
SoulLink -> artifacts\dist-3.0.0\
完成！成品：dist\SoulLink.exe
暂存：artifacts\dist-3.0.0
大小：3440525 字节
★ 这是「框架依赖」版本，用户需要先安装 .NET 11 桌面运行时。
```

### 2.3 结果

```
dist\SoulLink.exe
  版本    3.0.0.0
  大小    3.28 MB（3,440,525 字节）
  时间    2026-11-21 11:11:39
  SHA256  D6857D9CBDE6A6008735691554193F345824C07EF1299BE9CFC66A2C14B79228
```

### 2.4 启动验证

用「复制发布目录 + 改名为 Final300.exe」拿独立身份（避免撞上正在跑的
`SoulLink-Stable` 实例的互斥体）：

```
RUNNING: True
data in exe dir: True
LOCALAPPDATA written: False
boot.log: main window shown: True state=Normal
error.log: 无
```

---

## 3. Git 状态

```
main          a495ec2  [origin/main]          已同步
migrate/net11 4993f1b  [origin/migrate/net11] 保留
```

`dist/` 被 `.gitignore` 排除（第 4 行 `dist/`），所以清理和发版**不产生 git 变更**。
这次只是本地文件操作 + 已有的 3.0.0 提交（`4993f1b`）已合并推送。

---

## 4. 部署清单

### 4.1 用户机器

1. 跑 `check-runtime.ps1` 确认有 .NET 11 桌面运行时（没有会引导安装）
2. 部署 `dist\` 整个目录的内容：
   ```
   SoulLink.exe            3.28 MB   ← 主程序
   7za.exe                 1.23 MB
   check-runtime.ps1
   broker-install.ps1 / broker-uninstall.ps1
   agent\SoulLink.Agent.exe  63.92 MB  ← 手动代理（如用 Broker 通道）
   tools\SoulLink.BrokerSetup.exe 33.60 MB
   ```
3. 首次启动后，设置页如出现橙色迁移面板 → 点「迁移到当前目录」
4. 迁完**先重启确认配置都在**，再让它删旧目录

### 4.2 服务端

服务端 2.0.0 已构建但**未重新发布到 `dist-server`**。
如需上线：

```bash
# 本地
dotnet publish Server/SoulLink.Server.csproj -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist-server

# 服务器
sudo systemctl stop soullink-server
# 覆盖二进制
sudo systemctl reset-failed soullink-server   # ← 关键，别漏
sudo systemctl start soullink-server
curl http://127.0.0.1:5080/api/ping            # 应返回 2.0.0.0
```

---

## 5. 回退方案

如果 3.0.0 在目标机器上出问题：

```bat
:: dist 里保留着最后一个 net8 版本
copy dist\SoulLink-2.17.50-before-net11.exe dist\SoulLink.exe
```

2.17.50 是自包含单文件（64.38 MB），**不需要装任何运行时**，双击即用。

---

## 6. 本次操作记录

| 操作 | 结果 |
|---|---|
| 关闭占用实例 PID 187028 | 已确认无任务后强制结束 |
| 删除 18 个历史 exe | 释放 1231.53 MB |
| 删除 5 个 stable 旧版 | 释放 317.61 MB |
| 删除 next\SoulLink.exe | 释放 64.08 MB |
| 运行 build.bat | dist\SoulLink.exe → 3.0.0（3.28 MB）|
| 启动验证 | 通过 |
| **合计释放** | **约 1674 MB** |

---

## 7. 遗留

| 项 | 说明 |
|---|---|
| `stable\` 里还留着 `SoulLink-Stable.exe` + `SoulLink.exe`（128 MB） | 因 Stable 实例**正在运行**未删。等它退出后可清理，或保留作为挂机验证版 |
| `agent\SoulLink.Agent.exe`（63.92 MB） | 手动代理，保留 |
| 909 个构建产物仍被 git 跟踪 | `tools/layout-check/obj*/**`，见 3.0.0 交接文档 §9 |
| 服务端 2.0.0 未发布到 dist-server | 客户端改动不影响服务端，可择期 |
