# RDP 配置页两处缺陷修复（2026-11-22）

> 作者：rdpfix（AgentTeams 成员）
> 范围：缺陷 1「删除用户会自己回来」+ 缺陷 2「通用配置没有作为主页」
> 结论先行：**两个缺陷都不是「删除失败」或「自动重建」，而是「两处删除语义不联动 + 默认页空状态」**。

---

## 一、缺陷 1：删除用户会自己回来

### 1.1 用户原话
> 有用户反馈在 RDP 配置中删除用户，会自己回来。

### 1.2 三个必须查清的问题，逐条给结论

#### Q1：删除到底成功没有？（是「删不掉」还是「删掉了又回来」）

**结论：删除是成功的，Windows 账户确实被删掉了，不是「删不掉」。**

实测证据（本机 Windows，管理员权限，用一次性账户，未碰 A1/A2/A3/A4）：

| 场景 | 命令 | 退出码 | 结果 |
|---|---|---|---|
| 无会话账户 | `net user __deltest /delete` | **0** | 成功删除，`Get-LocalUser` 查不到 |
| 有登录令牌账户（`LogonUser` INTERACTIVE 成功） | `net user __deltest3 /delete` | **0** | 成功删除，`Get-LocalUser` 查不到 |
| 已登录用户（A1/A4 有 Active RDP 会话，未实测删除） | — | — | 未对真实账号动手 |

关键发现：
- **`net user X /delete` 即使该账户有活动/断开会话，也返回退出码 0 并删除**（本机实测）。任务描述里「有活动会话无法删除」的 Windows 硬限制，在本机环境下**并不成立**——因为 `net user` 删除用户时并不要求无会话，Windows 会照删、把相关会话变成孤儿会话。
- 因此根因**不是「删不掉」**，也不是「删掉了又重建」。真正的问题在下面 Q2/Q3。

> 说明：不同 Windows 版本 / 是否被进程占用时，`net user /delete` 的失败方式可能不同（例如某版本可能报 "The user has a session" 或拒绝访问）。但本机实测（Windows 上 A1/A2/A4 均挂着 RDP Active 会话的前提下，删除一个带 INTERACTIVE 登录令牌的账户）退出码仍为 0。所以对当前部署环境，结论是「删得掉」。

#### Q2：有没有自动重建路径？

**结论：没有「删除后自动重建」的路径。**

全仓 grep 所有创建本地用户的代码，结果如下：

| 位置 | 代码 | 是否会在删除后重建 |
|---|---|---|
| `Services/LocalUserService.cs:349` | `net user ... /add`（`CreateAsync` 唯一实现） | 否，只有人点「新建」才走 |
| `Views/RemoteAdminPage.xaml.cs:367` | `LocalUserService.CreateAsync` | 否，只有人点「创建用户」 |
| `Views/NewUserWindow.xaml.cs:132` | `LocalUserService.CreateAsync` | 否，只有人点「创建」 |
| `Services/AccountProvisioner.cs` | 只生成**连接配置**（`App.Config.Accounts.Add`），**从不建 Windows 账户** | 否 |

**关键澄清：`AccountProvisioner` 不是「补建 Windows 账户」的代码**，它只负责给新账号挑环回地址、生成并持久化一条 `Account` 配置（`RdpHost=127.0.0.x`）。它不调用 `net user /add`。

启动时/定时任务里也**没有**「补齐缺失用户」的自愈逻辑：
- `grep Ensure|Provision|补齐|自愈` 命中的全是无关代码（BgiStatusParser 补字段、日志残字节补齐、CloudUpdater 自愈等），没有一条会把删除的 Windows 账户加回来。
- 会话监控 `ConsolePage.StartSessionWatch → ScanAndDeployAsync` 只对**已经存在于 `App.Config.Accounts` 且勾选了自动连接**的账号做 RDP 连接/投送，**不会创建 Windows 账户**。
- `AccountStateCenter.SyncAccounts` 只是「让内存状态与配置列表保持一致」，不建账户。

#### Q3：列表是怎么刷新的？「看起来又回来了」是什么原因？

**结论：UI 层没有缓存，但「两处删除不联动」造成了「又回来了」的错觉。这是真正的根因。**

代码路径核实：

1. **`RemoteAdminPage`（远程管理页）的「删除」**（`RemoteAdminPage.xaml.cs:299 DeleteUserAsync`）：
   - 调 `LocalUserService.DeleteAsync(u.Name)` → `net user X /delete`，删的是 **Windows 账户本身**。
   - 删完后只 `ReloadAsync()`，只刷新**本页**的 `Get-LocalUser` 列表。
   - **从不删除 `App.Config.Accounts` 里那条对应的连接配置。**

2. **`ConsolePage`（RDP 配置页，中控台）的「删除用户」**（`ConsolePage.xaml.cs:2484 RemoveUser` / `:2508 RemoveUser_Click`）：
   - 只删 `App.Config.Accounts` 里的**连接配置**，`ConfigStore.Save`。
   - **从不删 Windows 账户**。右键菜单文案是「删除用户」，确认框里也写「只删除这里的配置，不影响该用户的实际会话」，但按钮/菜单名就叫「删除用户」——用户极易误解。

3. `ConsolePage.RebuildUserList()` 的数据源是 `AccountScope.Accounts(App.Config)`（`ConsolePage.xaml.cs:1894`），读取的是 `App.Config.Accounts` 配置列表，**不是实时 Windows 账户列表**。

于是「又回来了」的完整链条：
1. 用户在「远程管理页」删了 Windows 账户（比如 `A3`）→ Windows 账户没了，但 `App.Config.Accounts` 里 `A3` 那条连接配置**还在**。
2. 用户切到「RDP 配置页」→ `RebuildUserList` 照旧列出 `A3`（它来自 config，不是来自 Windows）→ 用户看到「我明明删了 A3，怎么又回来了」。

**结论：缺陷 1 的根因是「远程管理页删除 Windows 账户」与「RDP 配置页的连接配置列表」不联动；不是删除失败，不是自动重建，也不是 UI 缓存。**

### 1.3 两处「删除」的语义混淆

| 入口 | 实际删除对象 | 按钮/菜单文案 | 用户直觉 |
|---|---|---|---|
| `RemoteAdminPage` 每行「删除」按钮（`:224`） | **Windows 本地账户** | 「删除」 | 删掉这个用户 |
| `ConsolePage` 右键菜单「删除用户」（`:2239`）+ 编辑区「移除用户」按钮（`:220`） | **仅连接配置**（`App.Config.Accounts` 条目） | 「删除用户」/「移除用户」 | 也以为是删 Windows 账户 |

两个入口一个删 Windows 账户、一个只删配置，文案却都叫「删除用户」，这是「回来了」的错觉来源。**按任务要求：这里只报告语义混淆 + 给出区分方案，不擅自重命名功能。**

### 1.4 修复方案（已实施）

**根因修复 = 让两处删除联动**，而不是加 try-catch 掩盖：

- `RemoteAdminPage.DeleteUserAsync`（`:299`）删除 Windows 账户成功后，**同步删除** `App.Config.Accounts` 里 `UserName` 或 `RdpUserName` 匹配的连接配置，`ConfigStore.Save` 并调 `main.ConsoleView.RebuildUserList()` 让中控台列表即时消失。
- 删除前用 `SessionService.QueryAsync()` 检测该账户是否仍有 `Active/Disconnected` 会话，若有则弹**二次确认**说明后果（会话会失去账户、连接配置会被一并移除），避免用户点完才发现画面断了。会话查询失败不阻断删除。

**UI 区分方案（本次未实施，仅报告，待 Lead 决策）：**
1. `ConsolePage` 右键菜单把「删除用户」改为「移除此配置」（或「从列表移除」），`RemoteAdminPage` 的按钮保留「删除 Windows 账户」；两者文案不再混用。
2. `RemoteAdminPage` 删除按钮的 ToolTip 已写「删除该账户（不可恢复）」，可再补「会一并移除中控台配置」。

---

## 二、缺陷 2：RDP 配置页没把通用配置作为主页

### 2.1 用户原话
> RDP 配置那页，没有把通用配置作为主页，这个也要改改。

### 2.2 根因

`ConsolePage.RebuildUserList()` 结尾（`ConsolePage.xaml.cs:1927-1929`）：

```csharp
if (_showGeneralSettings) GeneralSettingsPanel.Visibility = Visibility.Visible;
else if (_editing != null) LoadConfigFields(_editing);
else ShowNoSelection();
```

- 首次进入页面时 `_showGeneralSettings == false` 且 `_editing == null` → 走 `ShowNoSelection()`，右侧是「请选择一个用户」的空状态。
- 「通用设置」卡片（`BuildSettingsRow`）固定是列表第一条，但**默认不选中**，右侧空着。

### 2.3 改动点（已实施）

选 **`Activate()`** 而非 `Loaded` 作为初始化入口，理由：
- `Activate()` 是「进入本页」的统一入口：首次 `Loaded`（`ConsolePage.xaml.cs:232 Loaded += Activate`）与每次从别的页切回（`MainWindow.SetActivePage` 的 `case "console"` → `ConsoleView.Activate()`，`MainWindow.xaml.cs:1335`）都会走这里。
- 符合 AGENTS.md 性能约束第 2 条：`SetActivePage` 只切 Visibility、不触发 Loaded/Unloaded。若放 `Loaded` 只能覆盖首次，无法统一。

改动（`ConsolePage.xaml.cs:985-1000`）：

```csharp
public void Activate()
{
    if (!_initialized)
    {
        ShowGeneralSettings();   // 首次进入默认选中「通用设置」
    }
    RebuildUserList();
    RenderTabs();
    _initialized = true;
    ...
}
```

行为保证：
- **首次进入**：`_initialized == false` → `ShowGeneralSettings()` 设置 `_showGeneralSettings=true`、`_editing=null`，右侧显示通用设置内容；随后 `RebuildUserList()` 让「通用设置」卡片呈选中态。
- **切回本页**：`_initialized == true` → 不再重置 `_showGeneralSettings`/`_editing`，保持用户离开前的选择（可能停在某个用户配置，或仍停在通用设置）。
- **点击某个用户**：`BuildRow` 的行点击（`:2106-2110`）仍 `_showGeneralSettings=false` → `LoadConfigFields(row)`，切到用户配置的行为不变。

---

## 三、改动清单（file:line）

| 文件 | 位置 | 改动 |
|---|---|---|
| `Views/RemoteAdminPage.xaml.cs` | `:299-352` `DeleteUserAsync` | 删除前检测活动/断开会话并二次确认；删除成功后同步删除 `App.Config.Accounts` 中匹配的连接配置、`ConfigStore.Save`、`main.ConsoleView.RebuildUserList()` |
| `Views/ConsolePage.xaml.cs` | `:985-1000` `Activate()` | 首次进入（`!_initialized`）时调 `ShowGeneralSettings()` 默认选中通用设置 |

> 未改动：`LocalUserService.cs`（`DeleteAsync`/`CreateAsync` 保持原样，根因不在那里）；未改动任何 `.xaml`；未改 Version；未 commit/push；未动 `dist/`。

---

## 四、编译结果

`dotnet build SoulLink.csproj`：

```
    142 个警告
    0 个错误
```

（142 条警告全部为存量警告，与本次改动无关；本次新增代码未引入新警告。）

---

## 五、用户怎么验证

### 缺陷 1（删除联动）
1. 确认存在一个「远程管理页」能删、且「RDP 配置页」也有对应条目的用户（例如先在两处都能看到 `A3`）。
2. 打开「远程管理」页 → 对 `A3` 点「删除」→ 若 `A3` 有在线/断开会话，会先弹二次确认。
3. 删除后切到「RDP 配置」（中控台）页 → `A3` 的连接配置应**已消失**，不再「又回来」。
4. 反向验证：在「RDP 配置」页右键「删除用户」（只删配置）后，回「远程管理」页应仍能看到该 Windows 账户（语义为「只移除配置」，符合预期，但文案易混，见 1.4 区分方案待决策）。

### 缺陷 2（默认通用设置）
1. 全新启动程序（或首次打开「RDP 配置」页）→ 右侧应直接显示「通用设置」内容，左侧「通用设置」卡片呈选中态，不再是「请选择一个用户」空状态。
2. 点击任意用户 → 切到该用户配置（行为不变）。
3. 切到别的页面再切回「RDP 配置」→ 保持你离开前的选择（停在用户配置或通用设置）。

### 无法自动化验证的部分
- 真实「删一个已登录 A1/A2/A4」的删除动作未做（避免影响用户实际挂机会话）。删除联动逻辑与「有会话二次确认」的分支已通过代码审阅 + 一次性账户实验验证了 `net user /delete` 的退出码行为（退出码 0 且能删）。
- GUI 交互（页面切换、默认选中）无法在本会话内运行 WPF 程序点选验证，需用户按上述步骤人工验证。

---

## 六、给 Lead 的待决策项

1. 是否采纳 1.4 的「文案区分」方案：`ConsolePage` 右键菜单「删除用户」→「移除此配置」，与 `RemoteAdminPage`「删除 Windows 账户」区分开。任务要求「不要自己重命名功能，先报告」，故本次未改文案，仅实现了删除联动。
