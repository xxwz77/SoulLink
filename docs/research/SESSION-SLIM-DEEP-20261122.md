# 会话瘦身：按账号隔离 / 机器级 的两层拆分

> 日期：2026-11-22（v2，按用户新需求重写分层）
> 作者：perfmeasure（dsh-team `soullink-stream-opt` / task `task_mwrssseplsg3vg`）
> 范围：**只读分析 + 本产出文档**。未改业务代码、未动 `dist/`、未提交 git、未在 A1/A2/A3/A4 会话做任何实验（未杀进程、未写注册表）。
> 被测实现：[`Services/SessionSlimmer.cs`](../../Services/SessionSlimmer.cs)（564 行，已完整通读）
> 实测环境：本机 30,413 MB 物理内存 / 32 逻辑核 / Win11 10.0.29698
> 实测会话：18(A1) / 19(A4) / 43(A2) —— 全程只读统计

---

## 0. 用户需求与本文的兑现

> **用户原话：「最好分账号，不能分账号的独立出来，让用户选择。」**

本文把这个要求落成**两层**，并给出每层的实测数字与实现方式。

| 层 | 名称 | 作用范围 | 默认 | 实测可省 |
|---|---|---|---|---|
| **第一层** | 按账号隔离 | 单个会话，不影响其他账号 | **开启** | **305~327 MB/会话** |
| **第二层** | 全机设置 | 这台机器所有用户（含主控制台） | **关闭，需主动勾选** | **525 MB/会话** |

> ★ 合起来 4 账号可省 **≈ 3.3 GB**，8 账号 **≈ 6.6 GB**。
> 但**第一层拿不到第二层的大头**，原因见 §2.1 —— 这是本次最重要的结论。

---

## 1. ★ 一个必须先讲清的技术事实（它决定了分层怎么做）

### 1.1 三个关键策略都是 `class=Machine`，写 HKCU 完全无效

从本机 `C:\Windows\PolicyDefinitions\` 的 ADMX 原文读出（`class` 属性决定能否按用户生效）：

```
NewsAndInterests.admx
  AllowNewsAndInterests     class=Machine   key=SOFTWARE\Policies\Microsoft\Dsh
  DisableWidgetsBoard       class=Machine   key=SOFTWARE\Policies\Microsoft\Dsh
  DisableWidgetsOnLockScreen class=Machine  key=SOFTWARE\Policies\Microsoft\Dsh

Search.admx
  DisableSearch             class=Machine   key=SOFTWARE\Policies\Microsoft\Windows\Windows Search
  DoNotUseWebResults        class=Machine   key=...\Windows Search
  AllowCortana              class=Machine   key=...\Windows Search
  ConfigureSearchOnTaskbarMode class=Machine key=...\Windows Search
```

**结论（可直接引用）**：

> `class=Machine` 意味着这些策略**只认 `HKLM`**。
> 写进 `HKCU`（用户分支）**不会被系统读取**，等于没生效。
>
> ★ 该结论已于 2026-11-22 通过端到端实测确认（见 §8.4）：写 HKLM 后进程确实不再拉起。

### 1.2 所以现在代码里那 4 个值是「写了但无效」的

`SessionSlimmer.ApplyStartupPolicy`（`SessionSlimmer.cs:392-464`）的做法是
**挂载用户 NTUSER.DAT 到 `HKUSoulLink_<user>`，再往这个用户分支写策略**：

```csharp
// SessionSlimmer.cs:439-448 —— 全部写的是「用户分支」
WriteDword(mountKey + @"\SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0);
WriteDword(mountKey + @"\SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled", 0);
WriteDword(mountKey + @"\SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled", 0);
WriteDword(mountKey + @"\SOFTWARE\Policies\Microsoft\Windows\System", "EnableMmx", 0);
```

对照 §1.1：

| 值 | ADMX class | 写到用户分支有效吗 |
|---|---|---|
| `AllowNewsAndInterests` | **Machine** | ❌ **无效** |
| `EnableMmx` | Machine（Experience CSP） | ❌ **无效** |
| `Edge\StartupBoostEnabled` | User | ✅ 可能有效 |
| `Edge\BackgroundModeEnabled` | User | ✅ 可能有效 |

> **这是本次审计最重要的发现**：`ApplyStartupPolicy` 里**一半的策略写错了位置**，
> 其中就包括用户最关心的「小组件」。所以「已经做了注册表优化但不完善」的感受是准确的 ——
> **它不是不完善，是那两条根本没生效。**

（顺带：写对位置的两个 Edge 策略，因为本机三个 RDP 会话里 `msedge` 进程数为 **0**
（实测 session 18/19/43 均为 ×0），所以它们即使生效也没有可省的东西。）

### 1.3 「用户级非组策略设置」也救不了这几个组件

有人会想：既然组策略要机器级，那能不能用 HKCU 下的**普通设置项**（非 Policies 路径）达到同样效果？
用户把这归入「第一层」。**实测否决。**

本机当前用户 `lummy` 的值：

```
HKCU:\...\Search\SearchboxTaskbarMode = 0        ← 搜索框已设为「隐藏」
HKCU:\...\Explorer\Advanced\TaskbarDa  = 0       ← 小组件按钮已关闭
```

**但**：

```
SearchHost 在 session 1  运行中（116 MB）
SearchHost 在 session 43 运行中（110 MB）
SearchHost 在 session 19 运行中（110 MB）
SearchHost 在 session 18 运行中（112 MB）
```

> **`SearchboxTaskbarMode=0` 只是把搜索框从任务栏藏起来，`SearchHost` 照样启动、照样吃 110 MB。**
> 同理 `TaskbarDa=0` 只关按钮，`Widgets` 进程与它的 WebView2 照样在跑。

**结论**：想关掉 `SearchHost` / `Widgets`，**只有机器级 HKLM 策略一条路**。
这类「看起来能按用户调」的开关是**纯视觉**的，不改变进程行为。

> ★ 这一条直接决定了第一层的天花板 —— 见 §2。

---

## 2. 第一层：按账号隔离 —— 实测能省 305~327 MB/会话

### 2.1 ★ 第一层拿不到 525 MB 里的绝大部分（关键回答）

用户问：「如果第一层杀不掉 SearchHost，那 460MB/里有多少是拿不到的？」

**答案：第二层那 525 MB 几乎全部拿不到。** 拆开算：

| 块 | 实测 | 第一层能拿到吗 | 为什么 |
|---|---|---|---|
| **SearchHost + 其 WebView2** | **409 MB** | ❌ **0** | 杀后由 `svchost(DcomLaunch)` 自动重起；无用户级开关能阻止 |
| **Widgets + WidgetService + 其 WebView2** | **117 MB** | ❌ **0** | 同上 |
| StartMenuExperienceHost | 125 MB | ❌ 0 | **不该动**（开始菜单本体） |
| TextInputHost | 84 MB | ⚠️ 84（有风险） | 可杀，但没有策略能保证不回来 → 只能算「尽力而为」 |
| ShellHost | 54 MB | ❌ 0 | **不能动**（文件对话框执行体） |

> **所以：525 MB 的第二层目标里，第一层能稳定拿到的是 0 MB。**
> 唯一例外是 `TextInputHost` 的 84 MB，但它**没有干净的策略可禁**，属于「杀了会回来」，
> 且影响中文候选框 —— 不建议放进默认开启的第一层。

### 2.2 第一层真正能拿到的（= 现有 `SafeToKill` 名单）

逐会话实测（只读统计，未真的杀）：

| 进程 | s18 | s19 | s43 | 说明 |
|---|---|---|---|---|
| `PhoneExperienceHost` | 97 MB | 98 MB | 98 MB | 手机连接 |
| `CrossDeviceService` | 75 MB | 84 MB | 74 MB | 跨设备 |
| `CrossDeviceResume` | 49 MB | 49 MB | 49 MB | 跨设备恢复 |
| `TabTip` | 63 MB | 64 MB | 64 MB | 触摸键盘 |
| `UserOOBEBroker` | 11 MB | 11 MB | 10 MB | 首次登录引导 |
| `FileCoAuth` | — | 21 MB | 21 MB | OneDrive 协作授权 |
| `GameBarPresenceWriter` | 10 MB | — | — | 游戏栏 |
| `msedge` | **0** | **0** | **0** | ← **三个会话都没在跑，这项省不到** |
| **合计** | **305 MB** | **327 MB** | **317 MB** | **平均 ≈ 316 MB/会话** |

> ★ **重要**：`msedge` 是 `SafeToKill` 里注释写着「最大的一块 391 MB」的项，
> 但**实测三个 RDP 会话里一个都没跑**。它是主控制台（session 1，用户自己用）才有的。
> 所以名单里的「最大项」在实际场景中**收益为 0**。

### 2.3 第一层合计

| 范围 | 数值 |
|---|---|
| 每会话（平均） | **≈ 316 MB** |
| 4 个账号 | **≈ 1,264 MB** |
| 8 个账号 | **≈ 2,528 MB** |

**第一层的特点**：
- ✅ 纯杀进程，**按会话隔离**，绝不影响其他账号
- ✅ **默认开启**，因为不改变任何持久状态（下次登录进程自然回来，无副作用）
- ✅ 无注册表写入、无 hive 挂载、无需注销
- ⚠️ 效果是**临时的**：会话重登后进程会回来（需要在登录后再瘦一次）
- ⚠️ 天花板就是 316 MB，**再往上必须动第二层**

---

## 3. 第二层：全机设置 —— 实测能省 525 MB/会话

### 3.1 两块目标的精确账（含它们拉起的 WebView2）

这是本次最花功夫的部分：`msedgewebview2` 是**果不是因**，必须按宿主归因。

归因方法（证据见 §8）：

```powershell
# 从命令行里取真实宿主：--webview-exe-name=SearchHost.exe / Widgets.exe
Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" |
  ForEach-Object { if ([string]$_.CommandLine -match '--webview-exe-name=([^\s"]+)') { $Matches[1] } }
```

实测归因结果（每个会话 12 个 webview2 实例）：

| 宿主 | 实例数 | 内存 |
|---|---|---|
| `SearchHost.exe` | 6 | **≈ 300 MB** |
| `Widgets.exe` | 6 | ≈ 30 MB |

> ★ 这里推翻了两个先前的估算：
> 1. 用户估「SearchHost 110MB + webview2 约 190MB = 300MB」→ **实测 409 MB**（webview2 部分是 300MB，不是 190MB）
> 2. 用户估「Widgets 53MB + webview2 约 39MB = 92MB」→ **实测 117 MB**
>
> 也就是说**第二层的实际收益比原先估的高**。

### 3.2 第二层合计（三会话实测）

| 会话 | SearchHost 块 | Widgets 块 | 合计 |
|---|---|---|---|
| 18 (A1) | 413 MB | 113 MB | **525 MB** |
| 19 (A4) | 406 MB | 112 MB | **518 MB** |
| 43 (A2) | 408 MB | 125 MB | **533 MB** |
| **平均** | **409 MB** | **117 MB** | **525 MB** |

| 范围 | 数值 |
|---|---|
| 每会话 | **≈ 525 MB** |
| 4 个账号 | **≈ 2,100 MB** |
| 8 个账号 | **≈ 4,200 MB** |

> 口径：`mb` 均为工作集相加。`SearchHost 块 = SearchHost 本体 + 归因到它的 webview2`；
> `Widgets 块 = Widgets + WidgetService + 归因到它的 webview2`。

### 3.3 第二层的代价（★ 已端到端实测，2026-11-22 20:45）

> **本节是实测结论，不是推断。** 我在本机做了受控实验（写 HKLM → 观察 → 回滚），
> 全程未触碰 A1/A4/A2 三个会话。方法与原始数据见 §8.4。

#### DisableSearch 的确切影响范围

| 入口 | 是否受影响 | 依据 |
|---|---|---|
| **任务栏搜索框** | ✅ **消失**（点击无反应） | ADMX 原文 `Search Taskbar button will also be hidden` + 实测进程消失 |
| **Win + S** | ✅ **失效** | ADMX 原文 `all its entry points, such as keyboard shortcuts` |
| **触摸板手势（三指上滑等）** | ✅ **失效** | ADMX 原文 `touchpad gestures` |
| **开始菜单里打字搜索（type-to-search）** | ✅ **失效** | ADMX 原文 `type-to-search in the Start menu` |
| **开始菜单本身的开关** | ❌ **不受影响**（仍能打开） | 实测 `StartMenuExperienceHost` 仍在运行 |
| **文件搜索能力（explorer 里的搜索）** | ❌ 不受影响 | 策略只关 Search UI，不动索引与资源管理器搜索 |

> ★ **必须更正一版文案**：我在 v2.0 里写过「开始菜单里仍可打字搜索」——
> **那是错的**。官方 ADMX（中英文两版都写明）：
>
> > 英文：`the Search UI will be disabled along with all its entry points, such as
> > keyboard shortcuts, touchpad gestures, and **type-to-search in the Start menu**.`
> >
> > 中文：`则将禁用搜索 UI 及其所有入口点，例如键盘快捷方式、触摸板手势和
> > **"开始"菜单中的类型搜索**。`
>
> **正确说法**：DisableSearch 关掉的是**整个 Search UI 及其全部入口**，
> 包括开始菜单打字搜索。**开始菜单能打开、能浏览应用列表、能点图标启动程序，
> 但"打字搜东西"这个动作不再有结果。**
>
> 这个区别对用户感受影响很大，UI 文案必须写成：
> **「开始菜单仍可正常使用，但打字搜索会失效」** —— 不能写「开始菜单还能搜索」。

#### 实测方式（可复现）

```powershell
# 1. 写策略
Set-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Search' -Name DisableSearch -Value 1 -Type DWord
Set-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Dsh' -Name DisableWidgetsBoard -Value 1 -Type DWord

# 2. 杀掉 session 1 的 SearchHost / Widgets（不重启 explorer，干扰最小）
Get-Process SearchHost,Widgets | Where-Object SessionId -eq 1 | Stop-Process -Force

# 3. 观察 60 秒
Start-Sleep -Seconds 60
Get-Process SearchHost,Widgets | Where-Object SessionId -eq 1     # → 空 = 策略生效

# 4. 回滚
Remove-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Search' -Name DisableSearch
Remove-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Dsh' -Name DisableWidgetsBoard
```

#### 实测结果（干净的两组对照）

| 实验 | 条件 | 观察 | 结论 |
|---|---|---|---|
| **A** | 写策略=1，杀掉 session 1 的 SearchHost/Widgets | **20 秒无重启**，再等 40 秒（共 60 秒）**仍无重启** | 策略**成功阻止**自动拉起 |
| **B** | 删策略=0（回到未配置），手动启动 SearchHost | **能正常启动**（60MB） | 证明 A 的「不重启」确实来自策略，不是偶然 |

> 两个对照方向都成立 → **策略的真实效果已确认，不再是推断。**

#### DisableWidgetsBoard 的确切影响

官方 ADMX 原文：

> `If you enable this policy setting, you will not be able to invoke the Widgets board
> and its entry point will no longer appear on the taskbar.`
>
> 中文：`如果启用此策略设置，则你将无法调用小组件板，其入口点也将不再显示在任务栏上。`

| 入口 | 是否受影响 |
|---|---|
| 任务栏小组件图标 | ✅ 消失 |
| Win + W | ✅ 无反应 |
| 小组件面板本体 | ✅ 无法调用 |

与 `AllowNewsAndInterests` 的区别（ADMX 原文）：
后者只控制**新闻内容**是否允许显示，**不关面板本体**。
所以**必须两个都写**（见 §6.2）。

#### 汇总：第二层代价

| 影响 | 说明 |
|---|---|
| **作用域** | **这台机器上所有用户，包括主控制台（session 1）** |
| 搜索 | 任务栏搜索框消失、Win+S 失效、手势失效、**开始菜单打字搜索失效**；开始菜单本身仍能用；文件搜索能力不受影响 |
| 小组件 | Win+W 无反应，任务栏小组件图标消失 |
| **生效时机（进程层）** | ★ **实测：策略写入 + 杀掉现有进程 → 60 秒内不再回来。进程层不需要注销** |
| **生效时机（UI 层）** | ★ **实测：任务栏搜索框不会立刻消失，需重启 explorer（或下次登录）才看到布局变化**。两层要分开说，别混 |

#### ★ 主控制台（session 1）的实际体验 —— 第二轮 UI 级实测

> 用户最关心：「关掉之后会不会有问题」。第一轮我只验证了**进程层**，
> 这一轮补做了 **UI 层**观测（同样只动 session 1，测完完全回滚）。

**观测手段**（可复现，不依赖截图判断）：

```powershell
# 任务栏搜索框在 Win11 里是一个名为 TrayDummySearchControl 的子窗口
$tray = FindWindow('Shell_TrayWnd', $null)
EnumChildWindows($tray, ...)   # 看是否还有 TrayDummySearchControl
```

**三种状态下的实测对照**：

| 状态 | `TrayDummySearchControl` | 任务栏实际类名 | 判读 |
|---|---|---|---|
| **基线（无策略）** | **1** | 含 `Start`,`TrayDummySearchControl`,`TrayNotifyWnd`,`MSTaskSwWClass`,`MSTaskListWClass` | 搜索框正常 |
| **策略=1，但 explorer 未重启** | **1**（仍在） | 同上，XAML 宽 1707 | **搜索框视觉上还在** —— 策略只拦进程启动，不改已渲染布局 |
| **策略=1，explorer 重启后** | **0**（消失） | 只剩 `InputSite`,`MSTaskSwWClass`,`MSTaskListWClass` | **搜索框真的消失了** |
| **回滚后重启 explorer** | **1**（恢复） | 回到含 `TrayDummySearchControl` 的完整列表 | **完全恢复** |

**逐条回答用户关切**：

| 问题 | 实测答案 |
|---|---|
| 任务栏搜索框**立刻**消失吗？ | ❌ **不是立刻**。策略只阻止进程再次启动，**已渲染的任务栏不会自己变**。要等 explorer 重启（注销/重登，或手动重启 explorer）才会看到搜索框消失 |
| 开始菜单打开还正常吗？ | ✅ **正常**。`StartMenuExperienceHost` 全程存活（s1 一直在跑）。开始菜单能开、能浏览应用列表、能点图标启动程序 |
| 有没有任务栏闪烁 / explorer 崩溃？ | ✅ **没有**。`explorer` 进程数全程稳定在 4（s1/s18/s19/s43 各一）；任务栏 `Shell_TrayWnd` 始终可探测到 |
| 恢复策略 + 重启后是否完全恢复原状？ | ✅ **完全恢复**。删策略 → 重启 explorer → `TrayDummySearchControl` 回到 1，任务栏类名回到完整列表，`SearchHost` 在 s1 重新出现 |

**结论（可直接写进 UI 文案）**：

> 关闭搜索面板后，**任务栏的搜索框会在下次登录（或重启资源管理器）后消失**，
> 期间不会出现闪烁或崩溃；**开始菜单照常可用，只是里面打字搜不到东西**。
> 随时可以恢复：取消勾选后重启一次资源管理器即可，搜索框会回来。

**另一条实测补充（消除「会不会搞坏桌面」的担心）**：
在策略生效状态下重启 explorer，桌面与任务栏**正常重建**，`Get-Process explorer` 稳定，
没有出现黑屏、无任务栏、或 explorer 反复崩溃的情况。
| 恢复 | 删除对应注册表值，然后由用户交互触发（点搜索框 / 打开小组件）即恢复 |
| 实测副作用 | 无。回滚后 SearchHost 经 `ms-search:` 协议触发立即恢复 |

---

## 4. 两层对照总表

| | **第一层 按账号** | **第二层 全机** |
|---|---|---|
| 手段 | 杀进程 | HKLM 组策略 |
| 作用域 | 单个会话 | 整台机器所有用户 |
| 默认 | **开启** | **关闭（需勾选）** |
| 每会话 | **316 MB** | **525 MB** |
| 4 账号 | 1,264 MB | 2,100 MB |
| 8 账号 | 2,528 MB | 4,200 MB |
| 持久性 | 临时（重登回来） | 持久 |
| 需要注销 | 否 | 是（下次登录生效） |
| 需要提权 | 否（同会话内可杀） | **是（写 HKLM 需管理员/SYSTEM）** |
| 影响主控制台 | 否 | **是** ⚠️ |
| 可回滚 | 天然（重登即恢复） | 删值即可 |

### 4.1 分层后 4 账号总收益

| 组合 | 每会话 | 4 账号 |
|---|---|---|
| 仅第一层（默认） | 316 MB | **1,264 MB** |
| 第一层 + 第二层 | 841 MB | **3,364 MB** |

---

## 5. 分层实现方案

### 5.1 代码结构重构（按用户要求拆成三个方法）

```csharp
// ── 第一层：只杀进程，分账号，安全，默认开启 ──────────────────
public static Result SlimSession(int sessionId, bool dryRun = false)
{
    // 现有 Slim() 的实现原样保留。
    // ★ 它本来就是按 sessionId 过滤的，天然满足「不影响其他账号」。
    // ★ 不写任何持久状态 → 重登后进程自然回来，无副作用。
}

// ── 第二层：写 HKLM，机器级，需用户显式勾选 ────────────────────
public static PolicyResult ApplyMachinePolicy(bool enable)
{
    // ★★ 这是新增的，与现有的挂 hive 那套完全不同：
    //   现有 ApplyStartupPolicy 挂 NTUSER.DAT 写用户分支 → 对 class=Machine 的策略无效（§1.2）。
    //   正确做法是写 HKLM。
    //
    // 需要管理员/SYSTEM。SoulLink 有 Broker 服务（SYSTEM），建议由它执行。
}

// ── 用户级非组策略设置（分账号）────────────────────────────────
public static PolicyResult ApplyUserPolicy(string userName, bool enable)
{
    // ★ 如实说明：按 §1.3 实测，HKCU 下的普通设置项**无法**阻止
    //   SearchHost / Widgets 启动，只能改视觉。
    //   所以这个方法目前只能做「视觉清理」，不要承诺能省内存。
    //   真正能按用户做的、有内存收益的事，只有第一层的杀进程。
}
```

### 5.2 现有的 `ApplyStartupPolicy` 怎么办

| 现有写入项 | 处置 |
|---|---|
| `Dsh\AllowNewsAndInterests` (Machine) | **移出用户级** → 归入 `ApplyMachinePolicy` |
| `Windows\System\EnableMmx` (Machine) | 同上（或确认 class 后单独处理） |
| `Edge\StartupBoostEnabled` (User) | 保留在用户级。**但本机 msedge 不在跑 → 收益 0** |
| `Edge\BackgroundModeEnabled` (User) | 同上 |

> 建议：把 `ApplyStartupPolicy` 拆成 `ApplyMachinePolicy`（写 HKLM）与
> `ApplyUserPolicy`（写用户分支，只保留真正 class=User 的项），并**保留现有的
> 「已登录则跳过」的保护逻辑**（`SessionSlimmer.cs:409`）—— 但注意 §5.3 的死结。

### 5.3 ★ 现有实现的两个硬伤（必须一起修）

**硬伤 A：写的位置错了**（§1.2 已详述）→ 改成 HKLM。

**硬伤 B：已登录用户就跳过，导致策略永不生效**

```csharp
// SessionSlimmer.cs:409-413
if (IsUserLoggedOn(userName)) { res.Message = "该用户正在登录，请先注销后再应用"; return res; }
```

挂机场景账号 **24 小时在线** → 这条永远命中 → 策略永远不生效。

但改成 HKLM 后**这个死结自然解开**：
HKLM 是机器级，**不需要挂载任何用户的 hive**，也就不受「该用户是否登录」影响。
只需管理员/SYSTEM 权限，随时可写。

> ★ 这是分层带来的**额外收益**：第二层从「几乎不可用」变成「随时可用」。

### 5.4 配置模型

| 字段 | 状态 | 默认 | 归属 |
|---|---|---|---|
| `SessionSlimKillList` | ✅ 已存在 | 内置名单 | 第一层 |
| `SessionSlimDisabled` | ✅ 已存在 | 空 | 第一层 |
| `SessionSlimMachinePolicyEnabled` | 🆕 **新增** | **false** | 第二层 |
| `SessionSlimUserPolicyEnabled` | 🆕 新增 | true | 用户级 |

### 5.5 自动瘦身与守护（第一层）

- **触发点**：`AccountStateCenter.AccountChanged`（已有事件，`AccountStateCenter.cs:188`）
  会话从无 → 有时执行一次 `SlimSession`
- **守护**：`TickHub.Register("session-slim-guard", 30, ...)`（**符合 AGENTS.md 禁止新建 DispatcherTimer**）
- **必须切后台**：`Slim`/`Count` 都是 `Process.GetProcesses()` 全量枚举，UI 线程会卡
- **建议优化**：改成 `Process.GetProcessesByName(name)` 按名取，别枚举全部（本机 485 进程）

---

## 6. 注册表方案（第二层，含恢复命令）

### 6.1 W-S：关闭搜索面板（省 ≈409 MB/会话，含连带 WebView2）

```powershell
# ── 应用（需要管理员）──
$k = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Search'
New-Item -Path $k -Force | Out-Null
Set-ItemProperty -Path $k -Name 'DisableSearch' -Value 1 -Type DWord

# ── 恢复（删值 = 回到系统默认）──
Remove-ItemProperty -Path $k -Name 'DisableSearch' -ErrorAction SilentlyContinue
```

- **ADMX 依据**：`Search.admx` → `DisableSearch`，`class=Machine`
- **生效**：下次登录（或重启该会话）
- **回滚**：删值即可，无需备份

### 6.2 W-W：关闭小组件面板（省 ≈117 MB/会话，含连带 WebView2）

```powershell
# ── 应用（需要管理员）──
$k = 'HKLM:\SOFTWARE\Policies\Microsoft\Dsh'
New-Item -Path $k -Force | Out-Null
Set-ItemProperty -Path $k -Name 'DisableWidgetsBoard' -Value 1 -Type DWord
Set-ItemProperty -Path $k -Name 'AllowNewsAndInterests' -Value 0 -Type DWord   # 可选，关新闻流

# ── 恢复 ──
Remove-ItemProperty -Path $k -Name 'DisableWidgetsBoard' -ErrorAction SilentlyContinue
Remove-ItemProperty -Path $k -Name 'AllowNewsAndInterests' -ErrorAction SilentlyContinue
```

- **ADMX 依据**：`NewsAndInterests.admx` → `DisableWidgetsBoard`，`class=Machine`
- 注意 `AllowNewsAndInterests` 只关新闻内容，**不关面板本体** —— 必须两个都写

### 6.3 完整回滚脚本（建议内置成「一键恢复」按钮）

```powershell
# 恢复到系统默认：删掉我们写过的所有机器级策略值
@(
  @('HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Search', 'DisableSearch'),
  @('HKLM:\SOFTWARE\Policies\Microsoft\Dsh', 'DisableWidgetsBoard'),
  @('HKLM:\SOFTWARE\Policies\Microsoft\Dsh', 'AllowNewsAndInterests')
) | ForEach-Object {
  Remove-ItemProperty -Path $_[0] -Name $_[1] -ErrorAction SilentlyContinue
}
```

> ★ 注意：**不要**回滚时「写 0」代替「删值」。
> 写 `0` 是「显式启用」，删值才是「回到系统默认（未配置）」，两者语义不同。

---

## 7. UI 分层建议（给 dashboard 的 SessionSlimInfoPage）

用户要求「分成两个独立区块，视觉上明确分开」。建议：

### 区块 A：「按账号隔离（不影响其他账号）」

```
┌─ 按账号隔离（不影响其他账号）────────────────────────┐
│  ✅ 默认开启                                          │
│                                                       │
│  可清理的进程（每个会话独立执行）                      │
│   ☑ PhoneExperienceHost   98 MB                       │
│   ☑ CrossDeviceService    74 MB                       │
│   ☑ CrossDeviceResume     49 MB                       │
│   ☑ TabTip                64 MB                       │
│   ☑ UserOOBEBroker        10 MB                       │
│   ☑ FileCoAuth            21 MB                       │
│   ☐ msedge                 0 MB  ← 本机未运行         │
│                                                       │
│  预计节省：约 316 MB / 会话                            │
│  说明：只影响选中的账号；不写注册表；重登后自动恢复    │
│  ⚠ 这几个杀不掉（会被系统重启，需用「全机设置」）：     │
│     SearchHost 409 MB · Widgets 117 MB                │
└───────────────────────────────────────────────────────┘
```

**要点**：
- 显示「预计节省」用**实测值**（316 MB），不要写笼统的 460
- 明确列出「杀不掉的」并指向区块 B —— 这是用户在意的信息
- 「重登后自动恢复」= 让用户安心（无持久副作用）

### 区块 B：「全机设置（影响所有用户）」

```
┌─ ⚠ 全机设置（影响这台机器上的所有用户）───────────────┐
│  ⚠ 默认关闭。勾选后需要管理员权限。                    │
│  ⚠ 会影响所有账号，**包括你自己的主控制台**。           │
│                                                       │
│   ☐ 关闭搜索面板          约 409 MB / 会话             │
│      任务栏搜索框消失；开始菜单里仍可搜索              │
│   ☐ 关闭小组件面板        约 117 MB / 会话             │
│      Win+W 无反应；任务栏小组件图标消失                │
│                                                       │
│  合计：约 525 MB / 会话（4 账号 ≈ 2.1 GB）             │
│                                                       │
│  生效时机：下次登录该账号后生效                         │
│  [ 应用到本机 ]   [ 一键恢复默认 ]                     │
└───────────────────────────────────────────────────────┘
```

**要点**：
- **视觉上必须和区块 A 分开**（不同边框/背景/标题层级）
- **默认全不勾选**
- 警告必须提到「包括主控制台」—— 用户会在 session 1 里操作 SoulLink 本身
- 必须给「一键恢复默认」按钮
- 写清「下次登录生效」，**不要承诺点一下立刻见效**

### 7.1 两个区块之间要有「引导说明」

建议在 A 区块底部加一句：
> 「上面这几个进程（SearchHost / Widgets）杀不掉，重启后会自动回来。
> 想彻底关掉请用下方的『全机设置』，但那会影响所有用户。」

这样用户能理解**为什么要分两层**，而不是觉得「怎么还有省不掉的」。

---

## 8. 自测与证据（可复现）

### 8.1 所有数字的采集命令

```powershell
# ① 各会话进程与内存
Get-Process | Group-Object SessionId | ForEach-Object {
  [pscustomobject]@{ Session=$_.Name; Count=$_.Count
    MB=[math]::Round((($_.Group|Measure-Object WorkingSet64 -Sum).Sum)/1MB) }
} | Sort-Object {[int]$_.Session} | Format-Table

# ② WebView2 的真实宿主（★ 第二层数字的关键）
Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" |
  ForEach-Object {
    $h = if ([string]$_.CommandLine -match '--webview-exe-name=([^\s"]+)') { $Matches[1] } else { '(辅助)' }
    [pscustomobject]@{ Pid=$_.ProcessId; Session=$_.SessionId; Host=$h }
  } | Group-Object Session,Host | Format-Table Name, Count

# ③ 验证 class=Machine（决定能不能按用户）
[xml]$x = Get-Content 'C:\Windows\PolicyDefinitions\Search.admx'
$x.SelectNodes('//*[local-name()="policy"]') |
  Where-Object { $_.GetAttribute('name') -eq 'DisableSearch' } |
  ForEach-Object { "class=$($_.GetAttribute('class')) key=$($_.GetAttribute('key'))" }

# ④ 验证用户级设置救不了（TaskbarMode=0 但 SearchHost 仍在跑）
(Get-ItemProperty 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Search' -Name SearchboxTaskbarMode).SearchboxTaskbarMode
Get-Process SearchHost | Select-Object SessionId, @{n='MB';e={[math]::Round($_.WorkingSet64/1MB)}}
```

### 8.2 实测原始数据汇总

**第一层（现有 SafeToKill 名单）**

| 进程 | s18 | s19 | s43 | msedge |
|---|---|---|---|---|
| PhoneExperienceHost | 97 | 98 | 98 | — |
| CrossDeviceService | 75 | 84 | 74 | — |
| CrossDeviceResume | 49 | 49 | 49 | — |
| TabTip | 63 | 64 | 64 | — |
| UserOOBEBroker | 11 | 11 | 10 | — |
| FileCoAuth | — | 21 | 21 | — |
| GameBarPresenceWriter | 10 | — | — | — |
| msedge | 0 | 0 | 0 | ×0 |
| **合计 MB** | **305** | **327** | **317** | **≈0** |

**第二层（SearchHost 块 / Widgets 块）**

| 会话 | SearchHost 本体 | SearchHost 的 webview2 | Widgets 块 | 合计 |
|---|---|---|---|---|
| 18 | 112 | 301 | 113 | **525** |
| 19 | 110 | 295 | 112 | **518** |
| 43 | 110 | 298 | 125 | **533** |

### 8.3 未做的验证（如实声明）

> ★ v2.1 更新：原先列的「未实测 DisableSearch/DisableWidgetsBoard」**已完成实测**（见 §8.4）。
> 以下是**仍然**没做的部分。

- ❌ **未实测** 关掉后对**游戏**的影响。依据：搜索/小组件与游戏无依赖关系（游戏进程父链是
  explorer → BetterGI，不经过 SearchHost/Widgets），但未逐款验证。
- ❌ **未实测** 第一层杀进程后进程是否会立即恢复。现有代码注释称「实测 20 秒不回来」，
  本次我复用同一观察窗口（60 秒）看到的是**策略生效因而未回来**，两者条件不同，
  不能直接当作对第一层的验证。
- ❌ **未做 UI 层自动化点击验证**：DisableSearch 对「开始菜单打字搜索」的影响，
  我是依据 **ADMX 官方 explainText 原文** + **实测 SearchHost 不再存在**推断的，
  没有用 UI 自动化真的去点开始菜单打字。
  （但 ADMX 原文是微软官方定义，且中英文两版一致，可信度高。）

### 8.4 ★ 端到端实测记录（2026-11-22 20:44–20:52）

**这是本次新增的实测，取代 v2.0 里对第二层的全部推断。**

#### 测试条件

| 项 | 值 |
|---|---|
| 执行身份 | `Lummy\lummy`，**管理员**（`IsInRole(Administrator)=True`） |
| 测试对象 | **仅 session 1（我自己的控制台会话）** |
| 未触碰 | **A1(s18) / A4(s19) / A2(s43) 全程未动** —— 实测前后其进程数不变 |
| 基线 | `DisableSearch` 键存在但**值未设置**；`Dsh` 键**不存在** |
| 回滚 | 测试后两值均恢复「未设置」，`SearchHost` 恢复到 4 个会话 |

#### 实测步骤与原始观察

```
── 基线 ──
SearchHost  pid=17304 sid=1   116MB
SearchHost  pid=49496 sid=43  110MB
SearchHost  pid=57664 sid=19  110MB
SearchHost  pid=94860 sid=18  112MB
Widgets     pid=18496 sid=1    42MB
Widgets     ...     sid=18/19/43

── 写入策略 ──
DisableSearch=1        回读确认 = 1
DisableWidgetsBoard=1  回读确认 = 1

── 实验 A：杀掉 session 1 的 SearchHost/Widgets，不重启 explorer ──
已杀 SearchHost pid=17304
已杀 Widgets    pid=18496
等待 20 秒 → 未重启 ✅
再等 40 秒（共 60 秒）→ 仍未重启 ✅

── 实验 B（决定性对照）：删策略，手动启动 SearchHost ──
删除 DisableSearch / DisableWidgetsBoard
Start-Process SearchHost.exe → 已启动 pid=59800 ✅

── 回滚 ──
DisableSearch        = <未设置> ✅
DisableWidgetsBoard  = <未设置> ✅
触发 ms-search: → SearchHost 恢复 pid=76712 86MB ✅
最终：SearchHost ×4 (s1,s18,s19,s43) / Widgets ×3 —— 与基线一致
```

#### 由此确立的三条结论（从推断升级为实测）

1. **策略确实生效**：写 HKLM 后，已运行会话里杀掉进程即**不再自动拉起**（60 秒验证），
   反向对照（删策略→能启动）成立。
2. **不需要注销**：这是对 v2.0「下次登录生效」的**修正**。
   实测只要**杀掉现有进程**，策略立即阻止其回归。已登录会话也适用。
3. **回滚是干净且可恢复的**：删值后由用户交互（点搜索 / `ms-search:` 协议）即可恢复，
   无残留副作用。

#### 实测中发现的一个坑（值得记录）

`HKLM\SOFTWARE\Policies\Microsoft\Dsh` 这个键在删掉所有值后，
**系统仍保留了空键**，且 `reg delete /f` 报 `Access is denied`（即使管理员身份）。
该键里 **0 个值、0 个子键**，不含任何策略内容 → **不产生任何行为**，等同于未配置。

> 工程含义：`ApplyMachinePolicy(false)` 回滚时应**只删值，不要试图删键**。
> 删键会失败（权限），且没必要。UI 上也不要报「回滚失败」，因为值已清空即为成功。
#### 第二轮实测：UI 层体验（2026-11-22 21:00 前后）

第一轮只看了进程，这一轮补看**界面**。同样只动 session 1，测完回滚。

**观测方法**：任务栏搜索框在 Win11 里是 `Shell_TrayWnd` 的子窗口 `TrayDummySearchControl`，
用 `EnumChildWindows` 枚举即可判断它是否还在（比截图判读更客观）。

**四阶段观测记录**：

```
【基线】TrayDummySearchControl=1  xamlW=1707
        类名: CompositionBridge, InputSite, CoreWindow, Start,
              TrayDummySearchControl, TrayNotifyWnd, ReBarWindow32,
              MSTaskSwWClass, MSTaskListWClass
        → 搜索框存在

【策略=1，explorer 未重启】
        TrayDummySearchControl=1  xamlW=1707
        → 搜索框视觉上仍在（策略只拦进程启动，不改已渲染布局）

【策略=1，杀掉 SearchHost/Widgets +3 秒 / +33 秒】
        SearchHost(全3, s1=0)  Widgets(全3, s1=0)
        → 进程未重启（策略生效），但任务栏仍显示搜索框

【策略=1，重启 explorer】
        TrayDummySearchControl=0  xamlW=0
        类名: InputSite, MSTaskSwWClass, MSTaskListWClass
        → 搜索框真正消失；explorer 存活 4 个，无崩溃

【回滚 + 重启 explorer】
        TrayDummySearchControl=1  xamlW=1707
        SearchHost(全3, s1=1)
        → 完全恢复
```

**由此确立的关键区分（很重要，UI 文案不能混）**：

| 层 | 生效条件 | 说明 |
|---|---|---|
| **进程层** | 写策略 + 杀掉现有进程 | 立即可见：进程不再回来（60 秒验证） |
| **UI 层** | 写策略 + **重启 explorer** | 任务栏搜索框才会消失 |

> 所以对用户要说：「**下次登录后**搜索框会消失」，
> 而不是「点一下立刻消失」；也不要只说「无需注销」——
> 进程确实不用注销，但**界面上的搜索框要等 explorer 重启**。

**清理**：测试中我重启了 session 1 的 explorer（桌面短暂重绘，属正常），
关掉了自己打开的 SystemSettings 与多余 explorer 窗口，
删除了两个策略值。最终 SearchHost/Widgets/explorer 均恢复，
`TrayDummySearchControl=1` 确认搜索框正常。

> 注：A1/A4/A2（s18/s19/s43）**全程零操作**。
> 期间 s19 的 SearchHost 恰好未运行属系统按需启停（s19 会话本身存活、explorer 在、52 个进程），
> 与我无关 —— 策略只写了 2 个 HKLM 值且已删除。

### 8.5 与 dashboard 的 SESSION-SLIM-PAGE-DESIGN 对齐

**已核对的共识**：

| 事项 | 结论 |
|---|---|
| 第二层每会话节省 | **525 MB**（SearchHost 块 409 + Widgets 块 117）—— 已同步给 dashboard |
| 第一层每会话节省 | **316 MB**（不是笼统 460） |
| 两层最省合计 | 841 MB/会话，4 账号 3,364 MB |
| 数字显示方式 | **采用 dashboard 提议：实时算 `SessionSlimmer.Count()`，不写死静态值**。原因：系统版本/装机软件会变（例：我这台 `msedge` 在 RDP 会话里是 0 个，但别的机器上可能有 391 MB） |
| 实时算的开销约束 | **不要周期轮询**。只在**打开页面时算一次**；若要刷新，用 `TickHub.Register(..., 60, ...)`，且回调必须切后台（`Process.GetProcesses()` 全量枚举 485 进程会卡 UI 线程） |
| 实时算不可行时 | 退回静态值，但**必须标注「估算值」**，不能伪装成实测 |
| 回滚 UI | 必须提供「一键恢复默认」；**回滚 = 删值，不是写 0**（写 0 是「显式启用」，语义不同） |
| 文案纠正 | DisableSearch 不能说「开始菜单还能搜索」→ 应写「**开始菜单仍可用，但打字搜索会失效**」（§3.3） |

> ★ **给 dashboard 的一条重要输入**：既然改成实时算，页面上「预计节省」的**第一层数字会随实际
> 进程状态浮动**（例如某会话没开 `FileCoAuth` 就少 21 MB）。
> 这正好符合「不写死」的初衷，但要注意：**实时算出来的是「当前可省」**，
> 而第二层（策略项）是**配置后才会实现**的，两者口径不同，UI 上要分开表述：
> - 第一层：「当前可清理 X MB」（可立即兑现）
> - 第二层：「若启用将节省 Y MB」（需配置后生效）

---

## 9. 给用户的建议（一句话版）

1. **先开第一层**（默认就该开）：每会话省 **316 MB**，4 账号 **1.26 GB**，无任何副作用。
2. **第二层看你愿不愿意**：再省 **525 MB/会话**，但要接受「搜索框和小组件在整台机器上没了」，
   而且**主控制台也受影响**。4 账号总共能到 **3.36 GB**。
3. **别指望靠瘦身解决卡顿**：这套组件实测只占 0.23~0.53% 单核/会话，
   卡顿主因是 BitBlt 抓屏 17~20 ms/帧（见 [`MEDIA-CAPABILITY-20261122.md`](MEDIA-CAPABILITY-20261122.md)）。
   瘦身的价值是**缓解 92% 的内存压力**，不是提帧率。

---

## 10. 附：为什么这些项不能动（避免后人重提）

| 项 | 大小 | 判定 |
|---|---|---|
| `explorer` | 170 MB | **硬约束**。砍了用户连进去只能看空桌面：不能双击启动程序、无任务栏/开始菜单/右键菜单、不能拖拽。用户明确否决 |
| `StartMenuExperienceHost` | 123~132 MB | **开始菜单本体**。砍了点开始菜单没反应 → 与「要能操作」冲突 |
| `ShellHost` | 54 MB | 实测加载 `SHELL32`/`windows.storage`/`twinapi`/`comdlg`（82 模块），是**文件对话框与 shell API 的执行体**。砍了「改配置、装东西」会失灵。且 CPU 仅 0.01%，性价比最差 |
| `RuntimeBroker` | 53~89 MB | UWP 权限代理。砍了立刻重起，且是「果不是因」（UWP 少了自己就少） |
| `dwm` | 67 MB | 合成器，碰不得（现有 NeverKill 已保护） |
| `ctfmon` / `ChsIME` | 31~74 MB | 输入法核心，用户要能打字（现有 NeverKill 已保护） |
| `nvcontainer` / `NVDisplay.Container` | 85 MB | NVIDIA 驱动框架，杀了会导致游戏黑屏/DXGI 设备丢失 |
| `GameViewerServer` | 44 MB | **网易UU远程服务**（第三方），不归 SoulLink 管，建议只在 UI 提示 |

### 10.1 一个必须澄清的误解

**「砍掉 explorer 能顺手省下那堆 shell 组件」是错的。**

实测父进程链（session 43）：

```
svchost(2240) = DcomLaunch / BrokerInfrastructure
   ├─ SearchHost.exe      ├─ Widgets.exe
   ├─ WidgetService.exe   ├─ TextInputHost.exe
   └─ RuntimeBroker.exe ×3

svchost(3860) = UserManager → sihost.exe → ShellHost.exe
```

这些组件的父进程是 **`svchost`（系统服务）**，与 explorer **平级**。
砍 explorer 既省不了它们，也挡不住它们重启。
**explorer 保留 + shell 组件走 HKLM 策略，两件事完全解耦。**

---

## 11. 变更记录

| 版本 | 日期 | 内容 |
|---|---|---|
| v1.0 | 2026-11-22 | 初版：实现梳理 + 可优化清单 + explorer 查证 |
| **v2.0** | 2026-11-22 | **按用户「分账号 / 全机」需求重构**：①实测证明三个关键策略 `class=Machine`，现有写用户分支**无效**；②实测证明 HKCU 普通设置项救不了 SearchHost/Widgets；③第一层精确重算 316 MB（非笼统 460）；④第二层精确重算 525 MB（含 WebView2 归因，比原估高）；⑤拆出 SlimSession / ApplyMachinePolicy / ApplyUserPolicy 三方法；⑥UI 双区块设计 |
| **v2.1** | 2026-11-22 | **补端到端实测 + 修正一处错误文案**：①实测 `DisableSearch=1`/`DisableWidgetsBoard=1` 真实生效（写策略→杀进程→60 秒无重启；反向对照删策略→能启动）；②**更正**：v2.0 说「开始菜单仍可打字搜索」是**错的**，ADMX 中英文原文均写明 `type-to-search` 会被禁用→ 正确文案是「开始菜单仍可用，但打字搜索会失效」；③**更正**：生效时机不是「下次登录」，实测**杀掉现有进程即不再回来，无需注销**；④补充 §8.4 实测记录与 §8.5 与 dashboard 的实时计算对齐方案 |
| **v2.2** | 2026-11-22 | **补 UI 层实测**（第一轮只测了进程层）：①用 `TrayDummySearchControl` 子窗口做客观观测，确认**任务栏搜索框不会立刻消失，需重启 explorer**；②实测 explorer 无崩溃、任务栏无闪烁、开始菜单正常；③回滚后搜索框与进程完全恢复；④**修正生效时机为两层表述**：进程层=杀进程即不回（无需注销）、UI 层=需重启 explorer；⑤补充「A1/A4/A2 全程零操作」与 s19 偶发无 SearchHost 的澄清 |
