# 对标研究 Yomori 之三：运维健壮性 / 更新 / 本地账户 / 测试 CI

> 日期：2026-11-24　作者：input-chain-analyst（dsh-team `team_mwu35l8pae8oj5` / task `task_mwubm3jjo9h192`）
> 对象：https://github.com/AITNR/Yomori （GPL-3.0，main 分支，2026-11-24 抓取；只读研究，未复制任何 GPL 代码进本仓库）。
> 证据口径：所有结论附对方仓库具体文件路径（raw.githubusercontent.com/AITNR/Yomori/main/<path> 可回查）；【可借鉴】【我们有而它没有】【不该抄】三类明确标注。

## 0. 结论摘要

1. Yomori 的运维工程化程度显著高于 SoulLink：**4 条 GitHub Actions**（ci/build/release/cleanup-builds）、**8 个独立测试项目 + 5 个 C 探针 + 3 个 Python 解码探针**、**独立进程看门狗**、**Inno Setup 安装器 + 安装时自动部署 agent**、**更新包"先记账下次启动再删"清理机制**。SoulLink 目前 0 条 CI、0 个测试项目、崩溃仅写文本日志。
2. 最值得抄的三件（只借鉴机制，不复制 GPL 代码）：①外置进程看门狗 + 假死抓 dump；②更新残留 pending-cleanup 清单机制；③CI 每次构建归档预发布 tag + 手动清理工作流。
3. 不该抄的：GitHub Releases 更新通道（我们走自有 COS+CDN 且有发布顺序红线）；安装器钩子部署形态（我们 broker 服务化架构更优，可借鉴其"活跃任务不覆盖"守卫）。

## 1. 运维健壮性

### 1.1 外置进程看门狗（【可借鉴】★ 收益最大）

- 对方证据：`winui3/src/Yomori.Watchdog/Program.cs`（常驻/--once/--pid 三模式；EnumWindows 找主窗；dbghelp MiniDumpWriteDump MiniDumpWithFullMemory 抓全量 dump）；`winui3/src/Yomori.Watchdog/HangProbe.cs` 头注释记录 2026-10-16 三次漏报结论：**进程内看门狗在 68 线程同时卡死时自己也得不到调度，必须独立进程**；判据固化：SendMessageTimeout rc==0 且 IsWindow(hwnd)=假死，明确"不要看 GetLastError / IsHungAppWindow"。假死后：写 watchdog.log → 抓全线程快照（NtQuerySystemInformation）→ 超阈值抓 dump。设计文档 `winui3/docs/ui-hang-watchdog.md`（20KB，含现场 PID/时间戳证据与验收清单）。
- 我们现状：无 UI 假死看门狗；RdpConsole 只有业务级锁定看门狗（Controls/RdpConsole.cs:473-508）；崩溃仅文本 crash.log（App.xaml.cs:80-159）。主程序假死时用户只能杀进程，无栈可查。
- 落地动作：新建 `tools/watchdog/`（框架依赖 exe，目标 <100KB）：主程序 TickHub 每秒 touch 心跳文件 + 看门狗进程监控心跳 + SendMessageTimeout 探测主窗 + MiniDump。约 2 人日；收益：假死可诊断可留证。
- 【我们有而它没有】TickHub 统一周期任务规范（AGENTS.md）；InputTrace 时间戳取证机制。

### 1.2 更新包残留清理（【可借鉴】★ 成本极低）

- 对方证据：`winui3/src/Yomori.App/Services/UpdatePackageCleanup.cs`：安装前把待删路径写入 `updates/pending-cleanup.json`，**新版本首次启动再删**（"安装器正拿着那个 exe 在跑，当场删不掉也不该删"）；兜底扫描超 7 天的 .part/旧安装包；全程吞异常（"清理失败最坏多占几十 MB，绝不能让启动挂住"）。
- 我们现状：tools/updater/Program.cs（"等主程序退出→解压覆盖→重启"，zip-slip 防护+重命名回滚）无残留清单机制；用户中途取消/杀进程后残留包无清理路径【推断，待实测确认无兜底】。
- 落地动作：App 启动时约 +50 行：updates 目录扫描 + 时间戳阈值删除（放 Services 层，复用 TickHub 或启动钩子）。0.5 人日。

### 1.3 假死诊断方法论（【可借鉴】）

- `winui3/docs/ui-hang-watchdog.md` 的两条结构性原则：**能发现卡死的机制不能跑在被卡死的线程上**；**UI 线程不做阻塞式原生/GPU/进程调用**。后者正是我们缺陷账 D15/D16/D18 的同族问题（UI 线程同步 qwinsta/GetProcessesByName/File.Exists）。
- 【不该抄】其文档文本（GPL 仓库一部分）；原则可借鉴。

## 2. 更新

### 2.1 更新通道（【不该抄】架构 + 【可借鉴】细节）

- 对方证据：`winui3/src/Yomori.App/Services/UpdateService.cs`（42KB）：GitHub Releases 通道；`UpdateCheckStatus` 6 态枚举 UpToDate/UpdateAvailable/**LocalBuild/LocalNewer/NoRelease/Failed**——LocalNewer 覆盖"远端被回退或本机自编译包"，NoRelease 覆盖"正式版未发过"。
- 我们现状：自有 COS+CDN latest.json（Services/CloudUpdater.cs:22-23），scripts/publish-release.ps1 全量包 + 先传包 HEAD 校验后才写 latest.json 的顺序红线（RELEASE-RULES.md）。**不该抄 GitHub Releases 通道**。
- 落地动作：把 6 态语义移植进 CloudUpdater.CheckAsync（约 +30 行），重点 LocalNewer 防回退误升级。0.5 人日。

### 2.2 安装器与 agent 自动部署（【不该抄】形态 + 【可借鉴】守卫）

- 对方证据：`winui3/tools/installer.iss`（Inno Setup 6；装完调 `Yomori.exe --deploy-agent` 铺 agent 到 %ProgramData%\Yomori\agent 并登记所有用户登录自启，失败不阻断安装；CI 还改编译器模板让引导器强制管理员，细节见文件头注释）；`winui3/src/Yomori.App/Services/AgentAutoDeploy.cs`：部署前**扫所有桥的状态文件，有活跃任务就整轮跳过**（"宁可这一次不部署……也不把人家的活打断"）。
- 我们现状：Broker 走 Windows 服务安装/启动/投送（tools/broker-setup/Program.cs，ServiceController），agent 由服务投送——形态不同，**不该抄安装器钩子**。
- 落地动作：把"活跃任务不覆盖"守卫移植进 broker 投送逻辑（投送前检查目标会话是否有活动录像/挂机任务）。0.5~1 人日；收益：升级不再打断挂机。

## 3. 测试与 CI

### 3.1 测试（【可借鉴】★）

- 对方证据：`winui3/tests/` 8 个项目：DecodeChecks、HomeBannerPickerChecks、NotificationChecks、ProcessHelperChecks、RunnerMemoryChecks、Yomori.LogChecks 等 + C 探针（ycn_dirty_pending_tests.c 等 5 个）+ Python 解码探针（h264_decode_probe.py 等 3 个）；`winui3/docs/known-limits-and-roadmap.md` 自述"770+ 项断言的源码级冒烟"。
- 我们现状：0 个测试项目；验证靠 tools/layout-check 类一次性探针与真机实测。
- 落地动作：不照搬 8 个；先建 1 个 `tests/SoulLink.Tests`（xUnit）抽测纯逻辑：版本分段比较（CloudUpdater.cs:83-106）、坐标 clamp（AgentWebServer ExecuteInput）、路线清单路径解析（RouteUpdater.ResolveManifestTarget）。1~2 人日。

### 3.2 CI（【可借鉴】★）

- 对方证据：`.github/workflows/ci.yml`（15KB）：push/PR 编译 + **每次构建归档独立预发布 tag**（build-<run_number> / pr-<PR>-<run>，可回溯任何一版；main 另有滚动 test-build 标签）；`cleanup-builds.yml`：手动触发，**默认演习只出清单**、双保留规则（最近 N 份 OR N 天内满足其一）、只动 prerelease 不碰正式版；build.yml 可复用 job 与 release.yml（手动正式发版）分离。
- 我们现状：0 条 workflow，发布全手工。
- 落地动作：建 1 条最小 CI（push/PR → dotnet build -c Release 0 error 校验，可选 zip 归档）；**正式发布仍走 publish-release.ps1**（COS 红线不可绕过）。0.5~1 人日。

## 4. 本地账户

- 对方证据：部署 nonce / 桥状态文件 / agent 登录自启（AgentAutoDeploy.cs、installer.iss [Code]）；家庭版检测移除改为探测 3389 监听（known-limits-and-roadmap.md，2026-10-25 修复："端口在听即判就绪——比读 SKU 可靠"）。
- 我们现状：跨会话投送 + --deploy-nonce 契约自建（见并行重构任务 task_mwubb27ho4kgjy 阶段 1 验收）；会话发现走 qwinsta（D15 已记其开销）。
- 【可借鉴】3389 监听探测思路用于会话就绪判定【推断，需先核对我们当前判据】。【不该抄】%ProgramData% 公共目录形态（broker 服务投送已覆盖同一职责）。

## 5. 汇总表

| 项 | Yomori | SoulLink | 判定 | 我们的动作 | 工作量 |
|---|---|---|---|---|---|
| 外置看门狗+dump | ✔ Watchdog.exe | ✘ 仅文本 crash.log | 可借鉴 | tools/watchdog 新项目 | ~2 人日 |
| 更新残留清理 | ✔ pending-cleanup.json | ✘ | 可借鉴 | 启动扫描+阈值删除 | 0.5 人日 |
| 更新 6 态枚举 | ✔ UpdateService.cs | 部分 | 可借鉴 | CheckAsync 补 LocalNewer 等 | 0.5 人日 |
| CI 每构建归档 | ✔ ci.yml | ✘ 0 workflow | 可借鉴 | 最小 build 校验 workflow | 0.5~1 人日 |
| 清理工作流（演习+双保留） | ✔ cleanup-builds.yml | — | 可借鉴 | 随 CI | 0.5 人日 |
| 测试项目 | ✔ 8 个+C 探针 | ✘ | 可借鉴 | 1 个 xUnit 起步 | 1~2 人日 |
| 安装器自动部署 | ✔ installer.iss | broker 服务化（等价更优） | 我们有而它没有等价物 | 移植"活跃任务不覆盖"守卫 | 0.5~1 人日 |
| GitHub Releases 通道 | ✔ | COS+CDN 红线 | 不该抄 | — | — |
| TickHub 规范 | ✘ | ✔ | 我们有而它没有 | 保持 | — |
| InputTrace 取证 | ✘ | ✔ | 我们有而它没有 | 保持 | — |
| GPL 代码复制 | — | — | 不该抄 | 仅借鉴机制，全部手写 | — |

## 6. 边界自查

只读研究：未克隆/未下载对方代码到工作区（仅经 web_fetch 阅读源码与文档）；未复制任何 GPL-3.0 代码/文本进本仓库；未改产品代码；未执行 git 命令。唯一写入：本文档 + docs/research/README.md 登记行。
