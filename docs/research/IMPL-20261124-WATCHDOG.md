# 实现：外置进程看门狗 sl-watchdog + 假死 MiniDump

> 日期：2026-11-24
> 作者：transport-network-analyst（dsh-team `team_mwu35l8pae8oj5` / member `m_mwu37cxvahbb82` / task `task_mwuc9h0qrtk83z`）
> 来源：`BENCH-YOMORI-02-AGENT-ARCH.md` §3.1③（对标 `Yomori.Watchdog`）
> 性质：实现任务。改动 = `tools/watchdog/`（新增工程）+ `SoulLink.csproj`（部署 Target ×3）。

---

## 0. 一句话结论

**sl-watchdog 已落地：独立进程常驻监视 SoulLink 主程序，SendMessageTimeout 三态探测（Yomori 踩坑固化判据）——假死判定（连续 2 次 × 3s 超时）→ ProgramData 日志 + NtQuerySystemInformation 线程快照 → 持续 90s 抓 MiniDumpWithFullMemory（cooldown 10min）。只观测不动进程（救援仍归 sl-rescue 人工决策）。构建 0 error，发布产物 72KB 框架依赖单文件。**

---

## 1. 设计决策（每条对标来源 + 我们差异）

| 决策 | Yomori 原版 | 本实现 | 理由 |
|---|---|---|---|
| 独立进程 | ✓（三次进程内漏报实证：68 线程全卡时看门狗自己也调度饿死） | ✓ | 同 |
| 探测判据 | SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG) 三态；**不看 GetLastError**（卡死时 1460/0/1400 毫无规律）、**不看 IsHungAppWindow**（卡在 WrLpcReply 时返回 false） | ✓ 完整照搬判据 | 对方三次踩坑固化的结论，无需重试 |
| 防抖 | 连续 2 次 / 3s 探测 / 3s 超时 | ✓ 同参数 | |
| dump | 假死 90s 后 MiniDumpWithFullMemory，cooldown 10min | ✓ 同 | 抓 dump 会暂停目标进程，不能一发现就抓 |
| 线程快照 | NtQuerySystemInformation 一次调用拿全部线程状态 | ✓ 同（x64 偏移 256/80） | 文字证据 + dump 双保险 |
| 日志位置 | ProgramData（安装目录标准用户无写权限——实测踩坑：写 logs\ 静默失败一行不留） | ✓ `%ProgramData%\SoulLink\watchdog\watchdog.log` + 5MB 截断 | 同坑避免 |
| **对假死的动作** | 只记录+dump，**不杀不重启** | ✓ 同 | 自动杀主程序=挂机会话中断，风险大于收益；救援交给 sl-rescue 人工决策 |
| 自包含 vs 框架依赖 | 自包含单文件（不依赖 .NET 运行时） | **框架依赖**（72KB） | 部署机必装 .NET 11（主程序运行时同要求）；产物小 100 倍，与 sl-rescue 同形态 |
| 参数 | 常驻 / --once / --pid | ✓ 同 | 自检与调试通道 |
| 数据目录 | 与 agent 平级 | `%ProgramData%\SoulLink\watchdog\`（crashdumps 子目录） | |
| 目标发现 | EnumWindows 找主程序窗口 | ✓ + 排除自身/Agent/Updater/Broker（按进程名 SoulLink 精确匹配 + 可见主窗口） | Broker 服务进程同名无窗口——窗口枚举天然过滤 |

## 2. 实现（`tools/watchdog/`，2 文件 ~430 行）

- `Program.cs`：探测/防抖/快照/dump/日志五件套 + 目标跟随（主程序重启自动换 pid）+ 假死恢复记录（持续时长）。
- `SoulLink.Watchdog.csproj`：`net11.0-windows` 框架依赖，AssemblyName=sl-watchdog，无 pdb。
- `SoulLink.csproj` 集成：`PublishWatchdog`（publish 到 artifacts）+ `CopyWatchdogToOutput/Publish`（复制到 dist/输出目录，与 rescue/updater 同模式）。

## 3. 验证记录（2026-11-24 15:0x）

| 场景 | 结果 |
|---|---|
| `dotnet build -c Release`（主工程） | **0 error**；watchdog 自动发布（artifacts\watchdog-build\ 四件套）+ 复制到主输出目录 ✓ |
| watchdog 工程独立构建 | 0 警告 0 错误 |
| `--once` 自检（无主程序运行） | `目标进程未找到` exit 0（不误报）✓ |
| `--once --pid <explorer>` | `=> Alive`（SendMessageTimeout 链路真实验证）✓ |
| 常驻模式 12s | 日志写入 ProgramData ✓、启动行含全参数 ✓、目标缺失空转不误报 ✓、FreeConsole 无黑窗 ✓ |
| 测试产物清理 | watchdog.log + ProgramData\SoulLink\watchdog\ 已删 ✓ |

**未实测项**（如实标注）：真·假死注入（挂起主程序线程验证 Hung→dump 全链路）——需制造真实挂起窗口，本轮用「探测 Alive 链路 + 三态代码审阅 + Yomori 同判据」替代；建议下轮用 NtSuspendProcess 注入验证 dump 链路。**常驻自启注册**（计划任务/启动项）未做——属于部署决策，留给 Lead（工具已可手动/计划任务运行）。

## 4. 使用方法

```powershell
# 自检（单次探测打印）
sl-watchdog.exe --once
# 常驻监视（计划任务/手动，无窗口）
sl-watchdog.exe
# 只看某个进程（调试）
sl-watchdog.exe --pid 1234
# 日志与转储
%ProgramData%SoulLinkwatchdogwatchdog.log
%ProgramData%SoulLinkwatchdogcrashdumpsSoulLink-<pid>-<时间>.dmp
```

## 5. 与红线任务（task_mwuc9h0irr6sy4）的关系

同源于 BENCH-YOMORI-02 的落地动作清单：红线（§4 行动 0）管「agent 链别引入 UI 依赖」，看门狗（§4 行动 3）管「主程序假死可观测」。两者独立无依赖。

## 6. 证据索引

| 结论 | 证据 |
|---|---|
| 来源与判据 | `BENCH-YOMORI-02-AGENT-ARCH.md` §1.5（HangProbe.cs 三态/两不看） |
| 进程内看门狗不可靠 | Yomori HangProbe.cs 头部（68 线程全卡、三次漏报）——本报告引用不复述代码 |
| 现有救援（不动进程的对偶） | `tools/rescue/Program.cs`（sl-rescue：手动杀+释放光标） |
| 实现 | `tools/watchdog/Program.cs` + `SoulLink.Watchdog.csproj` + `SoulLink.csproj` 三个 Target |
| 构建/验证 | §3 表（0 error + Alive 链路 + 常驻日志） |
| Broker 同名进程过滤 | `FindTarget`：进程名精确 `SoulLink` + EnumWindows 可见窗口（无窗口的 Broker 天然排除） |

## 7. 边界遵守自查

改动范围：`tools/watchdog/`（新增）+ `SoulLink.csproj`（追加 3 个 Target，不触碰既有内容）；未改产品代码/Relay/；未碰运行进程（测试只对 explorer 探测只读 SendMessageTimeout）；测试产物（ProgramData 日志）已清理；未 git 操作；GPL 零复制（判据与机制来自来源报告的描述，实现独立编写）。产物 = watchdog 工程 + csproj 集成 + 本报告。
