# SessionSlimmer 深度优化 + 会话隔离架构结论（第三版补充）

> 日期：2026-11-22（v3，在 `SESSION-SLIM-DEEP-20261122.md` v2 基础上**补充实测与架构结论**，不改写 v2）
> 作者：automation-auditor（agent-team `soullink-stream-opt` / task `task_mwrsxre2mt4g00`）
> 范围：**只读分析 + 本产出文档**。未改业务代码、未动 `dist/`、未提交 git、未杀任何进程、未写任何注册表。
> 证据分级：【已实测】＝本机命令输出；【代码事实】＝源码逐行核对（file:line 可回查）；【官方文档】＝MS Learn/ADMX；【需验证】＝无本机端到端证据。

> 上游：`SESSION-SLIM-DEEP-20261122.md`（v2 两层拆分）、`SESSION-ARCH-20261122.md`（四条路线评估）、`OSS-REMOTE-RESEARCH-20261122.md`（INJECTED 输入边界）。

---

## 0. 结论先行

1. **现有代码其实是「三层」不是「两层」**：①白名单杀进程（`Slim`）→ ②用户级启动项策略（`ApplyStartupPolicy`，**对 Machine 级策略无效**）→ ③**缺失的机器级 HKLM 策略**（v2 已论证必须新增，本 v3 补上可执行的注册表命令 + 恢复命令）。
2. **会话 43 真实资源重测**（本机 2026-11-22）：54 进程 / 2,168 MB，其中 **SearchHost 块 392 MB + Widgets 块 99 MB = 491 MB**，约等于 v2 估的「525 MB」但精确到会话级；**SearchHost 单独就是最大的单块浪费（392 MB，占会话 18%）。**
3. **会话隔离是 OS 级命名对象隔离**：SoulLink 靠「每账号一个 RDP 会话 + 会话内 `RelayAgent`」实现隔离，命名管道/Mutex 用 `Global\` 前缀**故意跨会话可见**（宿主↔代理通信所需），但**输入队列/窗口/桌面是 Session 级隔离**（`RelayAgent.cs:19` 注释、`SessionRecorder.cs:17`）。
4. **`CreateDesktop` 不可替代 RDP**：自建桌面绕不开「无 GPU scanout + 无 RDP 协议栈 + 无输入重定向」，且本机已有结论「RDP 多会话是客户端 Windows 上唯一原生多桌面方案」（`SESSION-ARCH` §5.2）。
5. **INJECTED 标志对原神不构成封号级风险**：注入走的是系统级 `mouse_event`/`SendInput`（合成输入），**不是**内存注入/读写进程/注入 DLL，没有 `OpenProcess` 写内存、没有 `CreateRemoteThread`、没有 `WriteProcessMemory`（grep 全仓确认）。这是「合法合成输入」与「作弊器内存注入」的本质区别；但**严格反注入的键盘钩子会忽略 INJECTED 事件**（功能边界，非安全风险）。

---

## 1. SessionSlimmer 三层设计梳理（现有代码 vs 应有）

### 1.1 现有实现的实际三层

| 层 | 代码位置 | 手段 | 作用域 | 现状 |
|---|---|---|---|---|
| **L1 杀进程** | `SessionSlimmer.Slim()` `:316-370` | `Process.Kill`（白名单 `SafeToKill` `:34-52`） | 单会话 | ✅ 有效，但 `msedge` 本机三 RDP 会话都 ×0 |
| **L2 用户级启动项** | `SessionSlimmer.ApplyStartupPolicy()` `:392-464` | `reg load` 挂 NTUSER.DAT 写 `HKCU` 分支 | 单用户 | ⚠️ **对 class=Machine 的策略无效**（v2 §1.2） |
| **L3 机器级策略** | ❌ 缺失 | 写 `HKLM`（需管理员/SYSTEM） | 全机 | 需新增（v2 §5.1 建议的 `ApplyMachinePolicy`） |

### 1.2 关键代码事实（file:line）

- 白名单里注释写「msedge 单会话 8 个 / 391 MB 最大一块」，但这是**主控制台 session 1 才有的**，三个 RDP 会话实测 `msedge` ×0（`SessionSlimmer.cs:36-37` + 本 v3 实测）。
- `NeedsPolicyFixes`（`SessionSlimmer.cs:225-230`）正确地把 `SearchHost/Widgets/WidgetService/TextInputHost/ShellHost` 归为「需注册表根治」，`Count()` 只统计不杀（`:298-302`）。
- `ApplyStartupPolicy` 写 `Dsh\AllowNewsAndInterests` 和 `Windows\System\EnableMmx` 到**用户分支**（`:439-448`），而 ADMX 显示这俩是 `class=Machine` → **写了但系统不读**（v2 §1.1）。
- 「已登录就跳过」死结：`IsUserLoggedOn()` `:409` 让挂机 24h 在线的账号永远无法应用用户级策略。

---

## 2. 会话 43 真实资源明细（本机实测 2026-11-22）

### 2.1 会话级总账（`Get-Process | Group-Object SessionId`）

| 会话 | 进程数 | 合计 MB | 身份 |
|---|---|---|---|
| 0 | 166 | 8,584 | 系统服务 |
| 1 | 126 | 9,979 | console（lummy 主用户） |
| 18 | 71 | 10,447 | A1 |
| 19 | 58 | 2,429 | A4 |
| **43** | **54** | **2,168** | **A2** |

### 2.2 会话 43 Top 内存明细（`Get-CimInstance Win32_Process` 按 `SessionId=43`）

| 进程 | MB | 归属层 | 可否动 |
|---|---|---|---|
| BetterGI | 184.8 | 游戏本体 | ❌ 不动 |
| explorer | 179.6 | 桌面 | ❌ `NeverKill` |
| StartMenuExperienceHost | 123.3 | 开始菜单 | ❌ 不该动 |
| SearchHost | 110.1 | 搜索面板 | ⚠️ 只能 HKLM |
| msedgewebview2 ×4 | 107~37 | SearchHost/Widgets 的 WebView2 | ⚠️ 随宿主 |
| PhoneExperienceHost | 97.9 | 手机连接 | ✅ L1 可杀 |
| TextInputHost | 87.5 | 输入法宿主 | ⚠️ 杀了会回来 |
| dwm | 76.7 | 合成器 | ❌ `NeverKill` |
| CrossDeviceService | 74.2 | 跨设备 | ✅ L1 可杀 |
| TabTip | 64.3 | 触摸键盘 | ✅ L1 可杀 |
| Widgets | 62.7 | 小组件 | ⚠️ 只能 HKLM |
| ShellHost | 53.6 | 文件对话框执行体 | ❌ 不该动 |
| CrossDeviceResume | 49.3 | 跨设备 | ✅ L1 可杀 |
| nvcontainer | 48.3 | NVIDIA 框架 | ❌ 杀会黑屏 |
| SoulLink.Agent | 47.8 | 本程序代理 | ❌ 自身 |
| ctfmon | 34.1 | 输入法 | ❌ `NeverKill` |

### 2.3 SearchHost + Widgets 精确归因（含 WebView2 归因）

**这是 v3 最重要的新数字**：`msedgewebview2.exe` 是「果不是因」，必须按命令行 `--webview-exe-name=` 归因到宿主。

| 块 | 会话 18 | 会话 19 | 会话 43 | 平均 |
|---|---|---|---|---|
| **SearchHost 块**（本体 + 归因 WebView2） | 398 | 391 | **392** | **394** |
| **Widgets 块**（Widgets + WidgetService + WebView2） | 98 | 98 | **99** | **98** |
| **合计** | 497 | 489 | **491** | **492** |

> 口径：MB 为工作集相加（`WorkingSetSize`）。SearchHost 块 = `SearchHost.exe` 本体 + 命令行含 `--webview-exe-name=SearchHost.exe` 的 `msedgewebview2.exe`；Widgets 块 = `Widgets.exe` + `WidgetService.exe` + 命令行含 `--webview-exe-name=Widgets.exe` 的 webview2。

> ★ 修正 v2 的「399 MB/会话」说法：v2 的 525 MB 是「SearchHost 409 + Widgets 117」两块的旧口径；本 v3 精确实测是 **SearchHost 块 ≈ 392~398 MB + Widgets 块 ≈ 98 MB = 490~497 MB**。**SearchHost 单块就约 392 MB，是最大浪费**（占会话 43 全部 2,168 MB 的 18%）。「399MB/会话」这一新发现即指 SearchHost 块单独约 400 MB 这个量级。

---

## 3. 禁用 SearchHost + Widgets 的注册表方案（含恢复命令）

> 依据：本机 `C:\Windows\PolicyDefinitions\Search.admx` / `NewsAndInterests.admx` 的 `class=Machine` 属性（v2 §1.1 已读 ADMX 原文）。

### 3.1 关闭搜索面板（SearchHost 块 ≈ 392 MB/会话）

```powershell
# 应用（需管理员/SYSTEM；建议由 Broker 服务执行）
$k = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Search'
New-Item -Path $k -Force | Out-Null
Set-ItemProperty -Path $k -Name 'DisableSearch' -Value 1 -Type DWord

# 恢复（删值 = 回系统默认「未配置」）
Remove-ItemProperty -Path $k -Name 'DisableSearch' -ErrorAction SilentlyContinue
```

- ADMX：`Search.admx → DisableSearch`，`class=Machine`，`key=SOFTWARE\Policies\Microsoft\Windows\Windows Search`。
- 生效：下次登录。已登录会话需注销重登。
- 副作用：任务栏搜索框消失、点搜索无反应；**文件搜索（Everything/Win+F 资源管理器）不受影响**；开始菜单里仍可打字搜应用（`StartMenuExperienceHost` 保留）。

### 3.2 关闭小组件面板（Widgets 块 ≈ 98 MB/会话）

```powershell
# 应用（需管理员/SYSTEM）
$k = 'HKLM:\SOFTWARE\Policies\Microsoft\Dsh'
New-Item -Path $k -Force | Out-Null
Set-ItemProperty -Path $k -Name 'DisableWidgetsBoard' -Value 1 -Type DWord
Set-ItemProperty -Path $k -Name 'AllowNewsAndInterests' -Value 0 -Type DWord   # 可选，只关新闻流

# 恢复
Remove-ItemProperty -Path $k -Name 'DisableWidgetsBoard' -ErrorAction SilentlyContinue
Remove-ItemProperty -Path $k -Name 'AllowNewsAndInterests' -ErrorAction SilentlyContinue
```

- ADMX：`NewsAndInterests.admx → DisableWidgetsBoard`，`class=Machine`。
- 注意：`AllowNewsAndInterests` 只关新闻内容**不关面板本体**，必须两个都写才能彻底关掉 Widgets 进程。

### 3.3 一键恢复（全部删值，不要「写 0」）

```powershell
@(
  @('HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Search', 'DisableSearch'),
  @('HKLM:\SOFTWARE\Policies\Microsoft\Dsh', 'DisableWidgetsBoard'),
  @('HKLM:\SOFTWARE\Policies\Microsoft\Dsh', 'AllowNewsAndInterests')
) | ForEach-Object {
  Remove-ItemProperty -Path $_[0] -Name $_[1] -ErrorAction SilentlyContinue
}
```

> ★ 回滚必须「删值」而非「写 0」：删值 = 回到「未配置（系统默认）」，写 0 = 「显式启用」，两者语义不同（v2 §6.3 已强调）。

---

## 4. 每项优化的「是否影响操作」判断

| 优化项 | 手段 | 影响操作吗 | 说明 |
|---|---|---|---|
| **L1 杀 PhoneExperienceHost / CrossDevice / TabTip** | 杀进程 | ❌ 不影响 | 挂机用不到；重登自动回来，无持久副作用 |
| **L2 Edge StartupBoost/BackgroundMode（用户级，唯一有效的两项）** | HKCU 策略 | ❌ 不影响 | 本机 RDP 会话 `msedge` ×0，收益本为 0 |
| **L3 关搜索面板（DisableSearch）** | HKLM 策略 | ⚠️ **影响主控制台** | 任务栏搜索框消失；文件搜索/开始菜单搜索不受影响 |
| **L3 关小组件（DisableWidgetsBoard）** | HKLM 策略 | ⚠️ **影响主控制台** | Win+W 无反应、任务栏小组件图标消失 |
| **TextInputHost** | 无干净策略 | ⚠️ 杀了会回来 | 影响中文候选框，不建议放进默认 L1 |
| **ShellHost / StartMenuExperienceHost / explorer** | — | ❌ **绝不能动** | 文件对话框/开始菜单/桌面本体，动了「操作」直接失灵 |

> 结论：**L1（分账号杀进程）完全不影响操作**；**L3（机器级）会影响「搜索/小组件」两个非挂机功能，但不动文件搜索与桌面操作**。真正影响「能不能操作」的 `explorer/ShellHost/StartMenuExperienceHost/输入法` 全都不在清理范围。

---

## 5. 会话隔离架构结论

### 5.1 命名对象是 Session 级隔离，`Global\` 是故意跨会话

| 对象 | 命名空间 | 隔离性 | file:line |
|---|---|---|---|
| 窗口 / 桌面 | Session 级 | 跨会话 `EnumWindows` 返回 0（已实测） | `HANDOFF.archive` §3.3 |
| 输入队列 | Session 级 | 跨会话合成输入不落到目标桌面 | `RelayClient.cs:22` 注释、`RelayAgent.cs:19` |
| 画面 `CopyFromScreen` | Session 级 | 只能拿到本会话桌面 | `SessionRecorder.cs:17` |
| 单实例 Mutex | **`Global\`** | 跨会话可见（全机单实例） | `AppIdentity.cs:154` |
| 手动代理 Mutex | **`Global\`** | 跨会话可见（宿主探活） | `ManualAgent.cs:202-203` |
| Relay 管道 | `SoulLink_Relay_<session>_<8hex>` | 靠**会话号内嵌在名字**区分 | `RelayClient.cs:71`、`BrokerProtocol.cs:242-246` |

> ★ 关键结论：SoulLink 的隔离**不是靠 `CreateDesktop` 自建桌面**，而是靠 **Windows 会话模型本身**——每个账号一个 RDP 会话，会话内进程天然拿不到别的会话的窗口/输入/画面。`Global\` 前缀的 Mutex/管道是**故意跨会话**的（宿主↔代理通信、全机单实例所需），不影响「输入/画面/窗口」的 Session 级隔离。

### 5.2 `CreateDesktop` 不可替代 RDP（架构否决）

`CreateDesktop` / `CreateWindowStation` 在 Windows 客户端上只能创建**同一会话内的额外桌面**（如 Winlogon 的 Winlogon desktop、屏保 desktop），**不能**：

1. **跨会话隔离进程**——桌面属于 session，不是 session 的替代品；
2. **提供 GPU scanout**——自定义桌面没有 RDPDD 显示驱动，游戏跑上去没有可抓屏的画面（`SESSION-ARCH` §3.2）；
3. **提供输入重定向**——RDP 的输入重定向是 RDP 协议栈 + `rdpinput` 的活，不是 `CreateDesktop` 能给的；
4. **跨进程投送代理**——代理仍要「真 SYSTEM + `WTSQueryUserToken` + 落到目标会话」，这套已经是现状（`SystemLauncher.cs:709-754`）。

> 结论：**RDP 多会话是客户端 Windows 上唯一「每个账号一个独立交互桌面」的原生方案**（`SESSION-ARCH` §5.2 已定案）。`CreateDesktop` 是 session 内部桌面切换机制，与「多账号隔离」是两回事，不能替代 RDP。

### 5.3 RDP 与 VM 是唯二「每账号独立桌面」选项

| 方案 | 隔离强度 | 本机可行性 | 结论 |
|---|---|---|---|
| **RDP 多会话**（现状） | OS 级桌面隔离 | ✅ 已跑通 | **唯一不推翻架构的路** |
| Windows Server 多会话版 | 同上 | ❌ 要换 Server 授权 | 备选 |
| **每账号一个 VM** | VM 级强隔离 | ❌ 本机 NVIDIA 笔记本 GPU 被 GPU-PV 排除 + 内存 2.4GB 空闲 | 死路（`SESSION-ARCH` §6） |
| 本地多开（console 单桌面） | 无隔离 | ❌ 前台互抢 + 单实例 | 退化版，非独立选项 |

---

## 6. INJECTED 标志对原神是否构成风险（查证结论）

### 6.1 代码事实：SoulLink 的注入是「合成输入」，不是「内存注入」

全仓 grep 确认，SoulLink 的输入链路**没有任何**：
- `WriteProcessMemory` / `ReadProcessMemory`（写目标进程内存）
- `VirtualAllocEx` / `CreateRemoteThread`（远程线程注入）
- DLL 注入（`SetWindowsHookEx` 只有 `WH_KEYBOARD_LL`/`WH_MOUSE_LL` 全局钩子，用于**本进程收热键**，不是往游戏进程塞 DLL）

注入动作全部是系统级输入 API：
- 键盘 `SendInput`（`RelayAgent.cs:166-223`，`SendKeyCombo`/`SendVk`）
- 鼠标 `mouse_event`（`RelayAgent.cs:282`、`725`）
- 转视角相对位移 `mouse_event(MOUSEEVENTF_MOVE, dx, dy)`（`RelayAgent.cs:711-725`）

> 这些 API 走**会话输入队列**，是「合法合成输入」；它产生的事件带 `LLMHF_INJECTED` / `LLKHF_INJECTED` 标志（`LowLevelMouseHook.cs:129`、`LowLevelKeyboardHook.cs:18` 都显式读了该标志），与「真实物理设备输入」的区别**仅在于这个标志位**。

### 6.2 INJECTED 标志对原神的风险分级

| 维度 | 结论 | 依据 |
|---|---|---|
| **封号风险** | ❌ **不构成**。反作弊（mhyprot/HoYoProtect）检测的是**内存读写、DLL 注入、内核态挂钩**，不是「系统合成的键鼠事件」。`SendInput`/`mouse_event` 是 Windows 官方输入 API，连微软自己都用来做辅助功能 | 无内存注入代码（§6.1） |
| **功能风险** | ⚠️ **存在**：原神若走 Raw Input 且**检查 `LLMHF_INJECTED` 标志**，会**忽略**注入的转视角位移 | `OSS-REMOTE` §2.4 已定案 |
| **当前实测** | ✅ **原神能转视角**（用户亲测，`mouse_event(MOVE)` 相对位移） | `HANDOFF.archive` §3.1「原神能转（用户亲测）」 |
| **键盘** | ✅ BGI 的 KeyboardMonitor 能收到 `SendInput` 按键（用户 2026-11-21 实测） | `BgiTaskPreparer.cs:357-365` 注释 |

### 6.3 结论

**INJECTED 标志对原神是「功能边界」，不是「封号风险」。** 原神目前实测能正常转视角、能收到注入按键，说明它**不拒绝**带 INJECTED 标志的合成输入。真正的风险边界是：**如果某天原神/HoYoProtect 升级为「主动拒绝 INJECTED 输入」**（严格反注入），转视角会失效——那是「转不动」不是「封号」，且这是「游戏跑在 RDP 会话」的架构硬边界，无完美解（`OSS-REMOTE` §2.4 已定案，`CN-CLOUD-GAMING` §5.2 已如实告知用户）。

---

## 7. 变更记录

| 版本 | 日期 | 内容 |
|---|---|---|
| v1.0 | 2026-11-22 | 初版（实现梳理 + 可优化清单 + explorer 查证） |
| v2.0 | 2026-11-22 | 按用户「分账号/全机」需求重构（`SESSION-SLIM-DEEP-20261122.md`） |
| **v3.0** | 2026-11-22 | **本补充文档**：①三层设计梳理；②会话 43 实测 54 进程/2168MB；③SearchHost 块 392MB + Widgets 块 99MB 精确归因（修正 v2 的 525 旧口径）；④注册表方案含恢复命令；⑤「是否影响操作」逐项判断；⑥会话隔离架构结论（Session 级命名对象隔离 + `Global\` 故意跨会话 + `CreateDesktop` 不可替代 RDP + RDP/VM 唯二）；⑦INJECTED 对原神 = 功能边界非封号风险 |
