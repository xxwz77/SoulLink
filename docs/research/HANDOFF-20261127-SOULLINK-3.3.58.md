# SoulLink 开发交接 2026-11-27

本次接手完成了网页自动拾取设置和 A1 米游社摩拉同步修复。本地开发版已更新并启动至 **3.3.58**。Git HEAD 仍在 `2d7bb3b`（v3.3.51），后续版本和工作区改动待整理提交。部署范围为本地 `dist/` 开发版，云端和挂机版未更新。

## 网页自动拾取

入口为「任务编辑 → 拾取名单」。使用 A2 测试账号验收。网页从所选账号绑定的 BGI 安装读取设置，保存时只提交变更。模板可同时按名称、颜色和物品种类筛选，筛选本身不会修改名单。同名不同颜色的模板按原生物品名共享勾选状态。分类优先使用目录元数据，并补充已知物品类别，无法判断的归入「未知」。

| 设置 | BGI 存储 |
| --- | --- |
| 自动拾取开关、OCR 模式和参数 | `User/config.json` 中的 `autoPickConfig` |
| OCR 精确名单、模糊名单及模式例外 | `User/pick_*.txt` |
| 全局模板黑名单 | `User/mojang_black_lists.txt` |
| 配置组模板白名单、黑名单 | `User/ScriptGroup/<组名>.json` 中的 `config.autoPickConfig.whiteList` 和 `blackList` |

对照 A2 安装版确认了全局和配置组的作用范围。进入「模板拾取」后，选择配置组即可分别编辑模板白名单和黑名单。网页使用 SoulLink 样式组织 BGI 功能分区，原生窗口的视觉一致性待实机比对。3.3.54 曾把模板勾选写入 OCR 名单，旧条目保留原样，清理时需逐项确认来源。

- 网页与 API 入口见 [PickJs.cs](../../tools/taskedit/Static/PickJs.cs)、[TaskEditApi.cs](../../tools/taskedit/TaskEditApi.cs) 和 [BgiTemplatePick.cs](../../tools/taskedit/BgiTemplatePick.cs)。
- 桌面和窄屏浏览器测试已覆盖组合筛选、搜索焦点、两份模板名单隔离及保存后重读。临时 BGI 目录测试已覆盖全局名单、配置组回写和损坏配置拒写。复跑入口见 [NativeViews.ps1](../../tools/taskedit/tests/NativeViews.ps1) 和 [TaskEditPickRoundTrip.csproj](../../tools/taskedit-check/TaskEditPickRoundTrip.csproj)。
- Android 拾取页已把模板勾选接到全局模板黑名单。Gradle 守护进程连接失败，编译和真机验收待补。配置组模板名单的两个编辑页签目前在网页端提供。

## 米游社摩拉

开发版原先把空的国服 DS 配置当成失败，新版使用项目默认值，显式配置优先。A1 重扫后可以查询角色，但札记仍返回 `-100`。进一步验证确认扫码链路漏发 `account_mid_v2`。新版保留扫码返回的该字段，对旧的国服扫码档案使用已有 MID 补齐。手动札记拉取可重新验证此前被标记为「需重扫」的凭据。

3.3.58 用 A1 刚扫码的凭据完成一次真实拉取。接口返回成功，本地事实库确认 2026 年 11 月累计获得 **19,849,006 摩拉**，新增 **10 条摩拉流水**和 **10 条原石流水**。这是旅行者札记的月度获得量。本次结果覆盖 A1 已选的默认角色和服务端此次返回的分页。

- 请求、凭据与状态处理见 [MiyousheClient.cs](../../Services/MiyousheClient.cs)、[MiyousheCredentialStore.cs](../../Services/MiyousheCredentialStore.cs)、[MiyousheService.cs](../../Services/MiyousheService.cs) 和 [MiyousheStore.cs](../../Services/MiyousheStore.cs)。
- 复验可在开发版对 A1 执行一次札记拉取，检查 `dist/data/miyoushe/miyoushe.db` 的 `my_pull_log`、`my_ledger_month` 和 `my_ledger_entry`。同一批流水再次拉取时会去重，新增为零可与已有记录并存。凭据继续以 DPAPI 密文存储。

## 工作区与后续处理

- 本文和索引写入前，Git 有 **52 个已跟踪文件修改、20 个未跟踪文件、0 个已暂存文件**。本文随后新增一份未跟踪文档，索引此前已处于修改状态。开发版已部署，源码仍待提交。
- 工作区包含接手前遗留的 Android、串流、运行日志、数据中心和任务编辑器等改动。这些改动未逐项审计，拾取与米游社的验证结果仅覆盖上面列出的链路。拆分提交时先查看逐文件 diff，尤其是同时包含历史改动和本次修复的 `SoulLink.csproj`、`Services/WebServer.cs` 与任务编辑器文件。
- 主程序和网页服务本地构建通过。主程序构建中的 Watchdog 附带发布任务报过重复程序集特性警告，本轮沿用开发版既有 Watchdog。全量发布前应复核附带工具产物。
- 生产边界按 [AGENTS.md](../../AGENTS.md) 执行。A1、A4 会话操作和挂机版更新分别授权。云端发布按 [发布规则](../RELEASE-RULES.md) 使用 `scripts/publish-release.ps1 -Full -Push`，先校验包再更新清单，`data/` 排除在更新包外。
