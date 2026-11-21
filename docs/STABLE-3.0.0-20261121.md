# SoulLink 挂机测试版更新记录 · 3.0.0

> 时间：2026-11-21 11:15
> 版本：挂机测试版 **3.0.0.0**（原 2.17.45.0）
> 位置：`dist/stable/SoulLink-Stable.exe`
> 状态：已部署并验证，当前以 PID 7596 常驻运行

---

## 0. 一句话

挂机测试版从 **2.17.45（64.37 MB 自包含）** 升级到 **3.0.0（3.28 MB 框架依赖）**，
原有 3 个账号配置（A1 / A4 / lummy）已迁移到新数据目录，启动验证通过。

---

## 1. 身份隔离机制

挂机测试版不是单独编译的产物，而是**同一个 exe 换名字**：

```
AppIdentity.Resolve() → 取当前进程文件名作为身份
  SoulLink.exe        → 身份 SoulLink         → 数据目录 data/
  SoulLink-Stable.exe → 身份 SoulLink-Stable  → 数据目录 data/
```

派生出来的一切都跟着身份走：单实例互斥体、IPC 管道、任务名前缀、数据目录。

**所以更新方式就是：把 3.0.0 的 exe 复制一份改名成 `SoulLink-Stable.exe`。**

> ⚠ 两个实例的数据目录都在各自 exe 所在的 `data/` 下。
> 当前两者同处 `dist/` 和 `dist/stable/`，**各自独立**，互不干扰。

---

## 2. 更新前置检查

### 2.1 确认实例空闲

更新前必须确认挂机版没有在跑任务：

| 检查项 | 结果 |
|---|---|
| `boot.log` 最后写入 | 11:10:21（刚刷新过，说明程序活着） |
| `config.json` 动作数 | **0** |
| `last-command.log` | **不存在** |

**动作数为 0 + 无执行记录 → 没有挂机任务在跑，可以安全关闭。**

> 判断依据不能只看进程活没活。托盘程序即使空转，`boot.log` 也会周期刷新。
> **关键看有没有配置动作、有没有执行记录。**

### 2.2 关闭实例

```
CloseMainWindow() → 未生效（托盘程序常见）
Stop-Process -Force → 成功退出
确认文件锁释放后才继续
```

---

## 3. 部署步骤

```powershell
# 1. 备份旧版（回退用）
Move-Item dist/stable/SoulLink-Stable.exe `
          dist/stable/SoulLink-Stable-2.17.45-before-3.0.0.exe

# 2. 部署 3.0.0 并改名
Copy-Item dist/SoulLink.exe dist/stable/SoulLink-Stable.exe

# 3. 同批的 SoulLink.exe 也更新
Move-Item dist/stable/SoulLink.exe dist/stable/SoulLink-2.17.4-before-3.0.0.exe
Copy-Item dist/SoulLink.exe dist/stable/SoulLink.exe

# 4. 运行时检查脚本一起放过去
Copy-Item scripts/check-runtime.ps1 dist/stable/check-runtime.ps1
```

---

## 4. ★ 配置没有自动带过来（重要）

### 4.1 现象

首次启动 3.0.0 后，`boot.log` 显示：

```
config loaded: E:\...\dist\stable\data\config.json
```

读的是**新位置**，而老配置在 `%LOCALAPPDATA%\SoulLink-Stable\config.json`。
新位置是程序自动生成的**空配置**（0 个账号）。

### 4.2 为什么

3.0.0 的数据目录改为 `exe 同级 data/`，而旧版用 `%LOCALAPPDATA%/<身份>`。
程序**不会自动迁移**（这是 3.0.0 的刻意设计：872 MB 级数据自动搬有风险）。

正常情况下应该走设置页的「迁移旧数据」橙色面板。但这里是命令行部署，
**需要手动迁移**。

### 4.3 本次处理

```powershell
# 复制旧配置过来
Copy-Item $env:LOCALAPPDATA\SoulLink-Stable\config.json dist\stable\data\config.json -Force
```

> ⚠ **第一遍自动复制时踩了坑**：批量复制脚本「跳过已存在文件」，
> 而新的空 `config.json` 已经生成了，导致配置没覆盖过去，账号数仍为 0。
> 改用 `-Force` 覆盖后恢复。

**迁移后确认：**

```
config.json 账号数: 3
  - A1
  - A4
  - lummy
动作数: 0
```

### 4.4 顺带清理

复制过来的 `error.log` 里是 **2026-11-18** 的历史记录（LobbyPage 的
FontFamily 异常，2.17.x 时代的问题）：

```
[2026-11-18 22:14:12] [Task] System.AggregateException:
  '' is not a valid value for property 'FontFamily'.
  at SoulLink.Views.LobbyPage.PairSubRow(...)
```

确认是旧日志（与旧目录文件哈希一致）后删除，避免误判为 3.0.0 的新问题。

---

## 5. 验证

### 5.1 干净启动

清掉 `boot.log` 和 `error.log` 后重启：

```
RUNNING: True
11:14:44.370  config loaded: dist\stable\data\config.json
11:14:45.267  single instance acquired
11:14:45.325  tray created
11:14:46.174  main window shown: True state=Normal
error.log: 无（good）
```

### 5.2 数据目录

```
dist/stable/data/
  bgi-backups / route-backups / route-cache   （空，等待使用）
  boot.log / config.json / device.json / state.json / ipc.log
  rdp-debug-*.log                              （历史日志已随迁移带过来）
```

`%LOCALAPPDATA%\SoulLink-Stable` **不再被写入**。

### 5.3 常驻状态

```
PID 7596  SoulLink-Stable.exe   dist\stable\SoulLink-Stable.exe
```

---

## 6. 最终目录状态

```
dist/stable/
  SoulLink-Stable.exe                       3.28 MB  v3.0.0.0     ← 挂机测试版
  SoulLink.exe                              3.28 MB  v3.0.0.0     ← 同批分发
  SoulLink-Stable-2.17.45-before-3.0.0.exe 64.37 MB  v2.17.45.0   ← 回退点
  SoulLink-2.17.4-before-3.0.0.exe         64.33 MB  v2.17.4.0    ← 回退点
  sl-rescue.exe                            33.48 MB
  check-runtime.ps1
  data/                                    （新数据目录）
```

`dist` 总体积：**403.66 MB**（33 个文件）

---

## 7. 回退方案

如果 3.0.0 挂机版出问题：

```powershell
# 先退出挂机版
Get-Process SoulLink-Stable | Stop-Process -Force

# 恢复旧版（2.17.45，自包含，不需要 .NET 运行时）
Move-Item dist\stable\SoulLink-Stable.exe dist\stable\SoulLink-Stable-3.0.0-broken.exe
Copy-Item dist\stable\SoulLink-Stable-2.17.45-before-3.0.0.exe dist\stable\SoulLink-Stable.exe
```

**注意**：回退后数据位置不同——

| 版本 | 数据目录 |
|---|---|
| 2.17.45 | `%LOCALAPPDATA%\SoulLink-Stable\`（旧配置还在，未删） |
| 3.0.0 | `dist\stable\data\` |

旧目录 `%LOCALAPPDATA%\SoulLink-Stable`（0.18 MB / 12 个文件）
**本次保留未删**，就是为了回退时配置还在。

---

## 8. 遗留

| 项 | 说明 |
|---|---|
| `%LOCALAPPDATA%\SoulLink-Stable` 未删（0.18 MB） | 回退保底，确认 3.0.0 稳定后可删 |
| `dist\stable\data\` 里的 rdp-debug 历史日志 | 随迁移带过来，程序会按 7 天自动清理 |
| 挂机版动作数 0 | 用户原本就没配动作，非本次变更导致 |