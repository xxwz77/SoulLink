# 项目规则

## Git 仓库分流

- 桌面端改动在 SoulLink 仓库提交，推送目标为 `https://github.com/xxwz77/SoulLink.git`。暂存桌面改动时排除 `Android/`。
- Android 改动在独立的 [ATM-GI-Android 仓库](https://github.com/xxwz77/ATM-GI-Android) 提交和推送。当前独立工作树为 `E:/Documents/deepseek-harness/default-workspace/ATM-GI`，应用项目位于其中的 `ATM-GI-Android/`。SoulLink 工作树中已有的 `Android/` 改动应移植到独立项目对应实现，保留原工作区改动，禁止把 Android 改动提交或推送到桌面仓库。
- 两端联动时各自暂存和提交，推送前核对两边的远端 URL 与暂存范围。

## 云端发布（必读 docs/RELEASE-RULES.md）

1. **所有发布只走 `scripts/publish-release.ps1`**，不手工上传 COS。
2. **一律用全量包**：`publish-release.ps1 -Full -Push`。
   ★ 2026-11-22 用户明确要求：「以后全用全量包，因为可能有人更新断档。
   全量包体积也不大。」——增量包跨多版更新会缺文件且无报错，不再使用。
   全量包实际约 2.85 MB（`agent\` 已排除、Broker 已改框架依赖）。
4. **绝对禁止把 `data\` 目录打进更新包** —— 那是用户配置/账号/日志。
   打包规则已在脚本中硬编码，改动前必须理解后果。
5. **发布顺序不可颠倒**：先上传 exe → HEAD 校验通过 → 才写 latest.json。
   清单一旦指向坏包，所有用户更新失败且无法自愈。
6. **版本号唯一来源是 `SoulLink.csproj` 的 `<Version>`**，改动必须升版本。
7. **发布域名不得出现在任何用户可见的位置**（界面、日志、异常提示）。
   只允许在 `Services/CloudUpdater.cs` 的两个常量里。

## 边界约定（2026-11-24 重划，按「账号 / 目录」判归属，不按 PID）

> 旧规则按 PID 记红线已作废 —— PID 每次重启都变，记录方式本身就是错的。
> 详细判据与判定脚本见 `docs/research/TEAM-REDLINES.md` 第二节。

### 归属（实测：`query user` + `Win32_Process` 的 GetOwner）

| 会话 | 账号 | 性质 |
|---|---|---|
| session 1 | `lummy` | 本机控制台（用户本人） |
| session 2 | **A1** | **生产，须用户授权** |
| session 3 | **A4** | **生产，须用户授权** |
| session 4 | **A2** | 测试，可随意启停 |
| — | **A3** | 测试，未登录，可随意启停 |

### 生产操作须用户明确授权（2026-11-26）

挂机版更新、A1 操作、A4 操作分别授权。用户点明本次任务的对象和操作即可执行，范围不清时先确认。更新开发版或笼统要求“更新 dist”仅适用于开发版。

| 对象 | 判据 |
|---|---|
| **A1 / A4 两个账号**（session 2 / 3） | 进程 owner 为 `A1` 或 `A4`。会话内的进程、窗口、桌面、抓屏和输入等操作，限于用户明确授权的账号和操作 |
| `dist/stable/SoulLink-Stable.exe` | 文件名或路径含 `stable` 的产物默认受保护。用户明确要求更新挂机版时，可替换更新所需文件，并按授权范围停启、验证挂机版 |

账号归属以**进程 owner** 为准，挂机版另按文件名或路径识别。owner 读不出来时**按禁动处理**。

### 可以自由操作（测试环境）

| 对象 | 说明 |
|---|---|
| `dist/` 下的非 stable 测试产物 | 主程序、`SoulLink.Agent.exe`、`SoulLink.Broker.exe`、version 文件、工具 exe |
| `dist/stable/` 以外的 `dist/` 子目录 | 可读写、可替换、可删除 |
| `C:\Program Files\SoulLinkDev\SoulLink.Agent.exe` | **服务侧 agent，测试用途，可停、可换、可重铺** |
| `C:\Program Files\SoulLinkDev\SoulLink.Broker.exe` | **服务本体（拆分后），可停、可换、可更新** |
| `SoulLink.DevBroker` 服务 | **可停、可启、可改配置、可重装、可就地改路径** |
| session 4 (A2) / A3 | 测试账户，可随意启停 |
| 注册表 | 仅限已授权的 `DWMFRAMEINTERVAL`，其他项不动 |

> ⚠️ `C:\Program Files\SoulLinkDev\SoulLink.Agent.exe` **不是 A1/A4 专用** ——
> 它被 A1、A4、A2 三个会话的 agent 进程**共用同一个文件**。
> 所以：停「A2 会话里的 agent」可以；停 A1 或 A4 的 agent 需要用户授权对应账号的停进程操作。
> 替换该文件属于安装/修复动作，允许；但会 Kill 占用它的进程，那一步须逐个核对 owner，
> 凡被 A1/A4 持有的，先报 Lead，确认已有该账号的用户授权后按精确 PID 操作。

### 停进程规则

- 只能用**精确 PID**（`Stop-Process -Id <pid>`）
- 禁止按名字批量杀（`taskkill /IM`）
- 禁止 `Get-Process SoulLink* | Stop-Process` 这类模糊匹配
- **停之前必须先核 owner**；owner 读不出来按禁动处理
- 停服务用 `Stop-Service`，不要 `Stop-Process` 硬杀服务进程

## 性能约束（必读 docs/RELEASE-RULES.md §10-11）

1. **新增周期任务走 `TickHub`**，不要新建 `DispatcherTimer`。
   6 个独立 1 秒定时器曾让空闲 CPU 达到 27%。
2. **页面可见性**：`SetActivePage` 只切 Visibility，**不触发 Loaded/Unloaded**。
   只在该页可见时需要的东西，必须实现 `OnPageActivated/OnPageDeactivated`。
3. **诊断代码**：取句柄数用 `GetProcessHandleCount`，不要用 `Process.HandleCount`
   （后者每次 3~5ms，挂在 1Hz 循环里自己就吃掉 20% CPU）。
4. **句柄增长要观察 >15 分钟才能定性**，启动爬坡是正常的。
5. **产物超过 5 MB 先问为什么**：`sl-rescue` 和 `BrokerSetup` 曾因自包含
   各自膨胀到 33 MB，改为框架依赖后分别是 72 KB / 1.16 MB。

## 工作区整洁（必读 docs/WORKSPACE-RULES.md）

1. **构建产物不入库也不囤积**：`bin/` `obj/` `dist/` 不进 git，
   但**也不该一直留在磁盘上**。每次大改动后清一次。
2. **一次性实验目录当场删**：只放 `.cs` 与 `.csproj`，跑完删掉整个目录。
   曾累积 12 个 `focus-flash-*` 目录、每个 63 MB。
3. **截图进 `%TEMP%`**，不要放工作区。曾经散落 100+ 张验证截图。
4. **`backups/` 只留最近一份**源码 zip，新备份做好后删旧的。
5. **研究/交接文档统一放 `docs/research/`** 并在其 `README.md` 登记，
   不要散落在项目根或 `releases/`。
6. **`tools/` 只保留能构建的源码**（`tools/7za` `broker-setup` `rescue` `updater`
   是构建依赖），其余测试工具的 `bin*`/`obj*` 都要清掉。

### 健康标准

| 项目 | 目标 |
|---|---|
| 工作区总计（不含 `.git`） | **< 3 GB**（2026-11-24 放宽，见下） |
| `tools/` | < 50 MB |
| `backups/` | 只有 1 个 zip |
| 根目录 `.md` | < 5 个（只留 AGENTS / README） |
| 任何 `bin/` `obj/` | 0 个 |

> 2026-11-21 清理：**17,781 MB → 583 MB**。
> **2026-11-24 用户明确放宽**：「不要将 1G 作为硬性标准，可以放宽到 5G 甚至 10G，
> 明确没用的可以删，有用的别删。」
> 原因：项目规模变了（多目标框架、Vortice/SDK 依赖、多个工具子项目），1 GB 已不现实。
>
> **删之前先问「它是不是明确没用」，不要拿总大小倒推该删什么。**
> 三条判断：
> 1. **名字/内容就是一次性的**（hotfix、test、probe、experiment）→ 可删；
> 2. **历史版本产物**（`artifacts/dist-x.y.z/`）→ **保留**，是版本参照，不按「版本号低」批量删；
>    2026-11-24 曾因此误删 350 MB（dist-3.1.x~3.3.33），源码已前进无法精确重建。
> 3. **可重建但重建要花时间的**（`Android/app/build`、`tools/*/bin`）→ 空间不缺就保留。
