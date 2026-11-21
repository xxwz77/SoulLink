# 项目规则

## 云端发布（必读 docs/RELEASE-RULES.md）

1. **所有发布只走 `scripts/publish-release.ps1`**，不手工上传 COS。
2. **默认增量**：`publish-release.ps1 -Push` —— 只含与上版相比有变化的文件。
3. **全量**：`publish-release.ps1 -Full -Push` —— 首次发布、新增目录结构、
   需要删除废弃文件、或要强制修复用户端文件时使用。
4. **绝对禁止把 `data\` 目录打进更新包** —— 那是用户配置/账号/日志。
   打包规则已在脚本中硬编码，改动前必须理解后果。
5. **发布顺序不可颠倒**：先上传 exe → HEAD 校验通过 → 才写 latest.json。
   清单一旦指向坏包，所有用户更新失败且无法自愈。
6. **版本号唯一来源是 `SoulLink.csproj` 的 `<Version>`**，改动必须升版本。
7. **发布域名不得出现在任何用户可见的位置**（界面、日志、异常提示）。
   只允许在 `Services/CloudUpdater.cs` 的两个常量里。

## 边界约定

- 未授权不得触碰 `dist/stable/SoulLink-Stable.exe`（挂机测试版）。
- 未授权不得使用 A1 / A4 账号。

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
| 工作区总计（不含 `.git`） | **< 1 GB** |
| `tools/` | < 50 MB |
| `backups/` | 只有 1 个 zip |
| 根目录 `.md` | < 5 个（只留 AGENTS / README） |
| 任何 `bin/` `obj/` | 0 个 |

> 2026-11-21 清理：**17,781 MB → 583 MB**。
