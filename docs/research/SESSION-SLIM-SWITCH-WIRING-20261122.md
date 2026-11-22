# 会话瘦身三个开关接线 + 两个页面统一

> 日期：2026-11-22
> 作者：rdpfix（AgentTeams `soullink-stream-opt` / task `task_mwrux6aevj4sgd`）
> 上游：`SESSION-POLICY-FIX-20261122.md`（机器级/用户级策略实现）

---

## 0. 结论

1. **`SessionSlimKillEnabled`（清理总开关）此前只有字段定义、只在说明弹窗里可切，但 `SessionSlimmer.Slim()` 根本没读它** —— 用户关了开关照样杀进程。现已接进 `Slim()` 与操作页按钮。
2. **两个页面语义统一**：`SessionsSlimPage`（说明页）改成**只读展示状态 + 跳转**，真正的开关操作统一收到 `ProcessPage`（操作页）。
3. **ProcessPage 三个开关齐全**：清理外围进程 / 全机设置（HKLM）/ Edge 预启动（HKCU），且 `Slim()`、按钮、代理下发三处都读对对应字段。

---

## 1. 三个开关 ↔ 代码接线表（完整性核对）

| 开关 | 配置字段 | 默认 | 生效点 |
|---|---|---|---|
| 清理会话外围进程 | `SessionSlimKillEnabled` | **true** | ① `SessionSlimmer.Slim()` 开头拦截；② `ProcessPage.Slim_Click` 拒绝并提示；③ `RefreshSlimSummary` 控制 `BtnSlim.IsEnabled` |
| 全机设置（小组件/搜索，HKLM） | `SessionSlimMachinePolicyEnabled` | false | ① `ProcessPage.MachinePolicyCheck_Changed` → `ApplyMachinePolicy(true/false)` 写 HKLM；② `RefreshPolicySwitches` 对齐 `IsMachinePolicyEnabled()` 只读探测 |
| Edge 预启动/后台（HKCU，按账号） | `SessionSlimUserPolicyEnabled` | false | ① `ProcessPage.UserPolicyCheck_Changed` 存配置；② `RelayClient.CaptureEnv` 下发 `SOULLINK_USER_POLICY`；③ `RelayAgent.Run` 启动时调 `ApplyUserPolicy` 写自己 HKCU |

> 默认值与 `docs/research/SESSION-SLIM-PAGE-DESIGN-20261122.md` §5.4 一致：kill 默认 true，machine/user 默认 false。

---

## 2. 改动清单（file:line）

| 文件 | 位置 | 改动 |
|---|---|---|
| `Services/SessionSlimmer.cs` | `Slim()` 开头 | 读 `SessionSlimKillEnabled`，false 时直接返回「总开关已关闭」不杀进程 |
| `Views/ProcessPage.xaml` | 策略面板顶部 | 新增「清理会话外围进程」`KillEnabledCheck` 开关（三开关齐全） |
| `Views/ProcessPage.xaml.cs` | `RefreshPolicySwitches` | 同步对齐 `KillEnabledCheck` |
| `Views/ProcessPage.xaml.cs` | 新增 `KillEnabledCheck_Changed` | 写 `SessionSlimKillEnabled` + 刷新按钮可用性 |
| `Views/ProcessPage.xaml.cs` | `RefreshSlimSummary` | `BtnSlim.IsEnabled = killEnabled && st.Killable > 0` |
| `Views/ProcessPage.xaml.cs` | `Slim_Click` | 总开关关闭时弹提示拒绝 |
| `Views/SessionsSlimPage.xaml` | 三个 `ToggleButton` | 全部 `IsEnabled="False"`（只读），去掉三个 `_Click` |
| `Views/SessionsSlimPage.xaml` | 「怎么恢复」区块 | 「恢复全部默认」改为「去设置这三个开关」`JumpToSettings_Click` |
| `Views/SessionsSlimPage.xaml.cs` | 删除 `Save`/三个 `_Click`/`RestoreDefaults_Click` | 改为只读 `LoadSettings` + `JumpToSettings_Click`（`NavigateTo("procs")`） |

> 未改 Version、未 commit/push、未动 `dist/`、未重命名任何功能、未重构无关代码。

---

## 3. 编译结果

`dotnet build SoulLink.csproj -t:Rebuild`：

```
已成功生成。
    142 个警告
    0 个错误
```

> 期间第一次 rebuild 曾出现 2 个 `CS1061/CS1501` 错误（`Services/LogLatch.cs`、`Services/FlowRunner.cs`），那是**另一位成员（automation-auditor）正在同步编辑日志锁存相关文件**的中间态，与本任务无关；再次 rebuild 时对方文件已收敛，0 错误。本任务改动的 9 个文件不在任何报错列表里。

---

## 4. 用户怎么验证

1. **清理总开关**：会话瘦身页把「清理会话外围进程」关掉 → 选一个有可清理进程的会话 → 「清理外围进程」按钮变灰；若强行触发也提示「已关闭」。重新打开 → 按钮恢复，能正常清理。
2. **说明页只读**：点右上「说明」打开说明弹窗 → 三个开关只显示状态、不可点击；点「去设置这三个开关」→ 跳回会话瘦身操作页。
3. **三开关齐全**：操作页能看到「清理会话外围进程 / 全机设置 / Edge 预启动」三个开关，各自独立保存。

### 无法自动化验证的部分
- WPF 交互（开关点击、弹窗跳转）无法在本会话运行点选验证，需用户按上述步骤人工验证。
- 全机/Edge 策略的端到端效果沿用 `SESSION-POLICY-FIX-20261122.md` §7 的验证方法。
