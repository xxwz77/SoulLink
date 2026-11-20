# RDP 保护层黑屏修复 · 2026-11-19

> 状态：**已在 dist 真机验证通过**（A2/A3 双画面同时显示，画面不再是黑色遮盖）。
> 对应交接文档：FIXED-v2.11.0-DREAMSCAPE-20261119.md 第 4 节「未修复」事项。

## 1. 结论先说

**根因不是“透明没调对”，而是“根本不该往 RDP 上盖窗口”。**

只要有一个 `AllowsTransparency=True` 的 WPF 浮层压在 RDP 画面上，RDP 区域就必然是黑的。
所以修复方向不是继续调透明，而是**彻底取消这个浮层**。

## 2. 根因证据

| 证据 | 内容 |
|---|---|
| `mstscax.dll` 字符串 | `Software mode is not implemented`、`Not using hardware. Software overlay layer is not implemented` |
| `mstscax.dll` 依赖 | 导入 `dcomp.dll` / `dxgi.dll`，使用 `CreateSwapChainForComposition`、`HWndSurfacePresenterDComp` |
| 推论 | RDP 控件**只有硬件合成路径，没有软件回退** |
| WPF 侧 | `AllowsTransparency=True` 等价于 `WS_EX_LAYERED` + 逐像素 alpha（`UpdateLayeredWindow`），没有重定向表面交给 DWM |
| 结果 | 分层窗口压到 flip-model / DirectComposition 表面上，该区域被迫退出硬件/MPO 合成，**整块黑** |
| 本仓既有记录 | `HANDOFF.md` 4.2：`AllowsTransparency=True` 的主窗口里 RDP 画面永远全黑，同一个机制 |

补充（Windows 官方可查的旁证）：

- `WS_EX_LAYERED` 是 GDI 时代的合成机制，设计上不支持子窗口，也不是给 DirectX 表面准备的。
- MPO（多平面覆盖）遇到分层窗口时退回合成，是已知的驱动级缺陷类别（NVIDIA 为此提供过 MPO 关闭开关）。
- 是否与“被完全遮挡后 RDP 停止出帧”（DXGI_STATUS_OCCLUDED）有关**未证实**：
  mstscax 走 flip model，而 flip model 恰恰不会返回 OCCLUDED，所以不能把原因归到“停止重绘”。

## 3. 修复方案

### 3.1 不再创建任何覆盖窗口

删除 `Views/ConsolePage.xaml.cs` 里的 `EnsureShieldOverlayWindow()` 及整个
`_shieldOverlayWindow` / `_shieldOverlayContent` 字段与调用点。

RDP 画面**全程可见**，保护层的“拦截首次点击”改由已有的
`WH_MOUSE_LL` 全局鼠标钩子完成（自由视角本来就用它吞鼠标）。

### 3.2 闸门语义（与旧保护层完全等价）

| 时刻 | 行为 |
|---|---|
| 锁定态 + 鼠标在画面内 + 左键 Down | 吞掉，**不进入远端** |
| 锁定态 + 鼠标在画面内 + 左键 Up | 吞掉，并**此刻解锁** |
| 锁定态 + 鼠标移动 / 右键 / 中键 / 滚轮 | **放行**（光标不会卡住） |
| 已接管 | 全部放行，直达 RDP |
| 鼠标在画面外 | 全部放行（不影响别的界面） |

“首次点击被完整消费”的语义保留：用于解锁的那一次 Down+Up 都不会漏进远端游戏。

### 3.3 代码落点

| 文件 | 变化 |
|---|---|
| `Controls/RdpConsole.cs` | 新增 `ArmShield` / `ReleaseShield` / `ShieldGate` / `RefreshCanvasRectCache`；`OnGlobalMouse` 最前面加闸门 |
| `Controls/ShieldGateLogic.cs` | **新增**：闸门的纯判定逻辑（无 UI / 无 IO / 无 P/Invoke），可脱机回归 |
| `Views/ConsolePage.xaml.cs` | 删除覆盖窗口，改为驱动 `ArmShield` / `ReleaseShield`；指示灯增加“点击画面接管” |
| `Views/ConsolePage.Tiles.cs` | 去掉浮层几何同步（`SyncShieldSize` / `SyncToAnchor`） |
| `tools/shield-gate-check/` | **新增**：闸门回归程序，`RESULT=PASS` |

### 3.4 钩子安全（关键，容易踩）

低级鼠标钩子有**系统超时**，超时后 Windows 会**静默摘掉钩子**（不报错），
表现为“保护层突然失效、点击直接漏进游戏”。因此闸门内严格执行：

- 不调 WPF（`PointToScreen` / 布局）：画布矩形改为 **UI 线程缓存**，钩子只读字段 + `GetCursorPos`；
- 不写日志文件（`Debug()` 是同步落盘）：只在 UI 侧收尾时写；
- 解锁的状态翻转为纯字段赋值，界面通知走 `Dispatcher.BeginInvoke`。

## 4. 验证记录

### 4.1 脱机回归

```
dotnet run --project tools/shield-gate-check -c Release
→ RESULT=PASS（15 项断言，含“Down 后仍锁定”/“Up 后解锁”/“首次点击被完整消费”）
```

### 4.2 dist 真机验证（2026-11-19 11:17，pid 105524，session 1）

| 检查项 | 结果 |
|---|---|
| A2（山客）/ A3（玄月）双画面同时显示 | 通过：两个远端桌面正常渲染，**无黑色遮盖** |
| RDP 之上的可见 `WS_EX_LAYERED` 窗口数量 | 通过：**0**（逐窗口枚举确认） |
| 键鼠指示灯 | 通过：显示“点击画面接管”（琥珀色），说明闸门已上锁 |
| 连接 / 通道投送 / 热键配置日志 | 通过：无异常 |

截图：`artifacts/verify-final.png`、`artifacts/verify-after-zoom.png`

### 4.3 仍需用户确认的项

自动化只能证明“不再有覆盖窗口、画面正常渲染、闸门处于锁定态”。
以下需要真实鼠标操作，**请由用户实测**：

1. 锁定态下点一下画面：指示灯变“已接管”，且这次点击**没有**漏进远端游戏；
2. 接管后正常操作游戏；
3. 鼠标移出画面：自动重新上锁；
4. 多画面下点第二格：焦点切到第二格（键鼠投给那个账号）；
5. 切页 / 最小化 / 失焦后再回来：重新上锁；
6. 进入/退出自由视角（侧键）：退出后需重新点击接管。

## 5. 回滚

```
修复前的 dist 成品：artifacts/dist-backup-before-shield-fix/SoulLink.exe
SHA256 = FCD67B470ADC1DB723DF96891E5D6851844461D3725D6313CE92669218324F5D
```

回滚时按 PID + 完整路径确认后替换即可，**不要** `git restore` 整个仓库。

## 6. 禁止事项（承接旧交接）

- 不要再手动给任何窗口叠加 `WS_EX_LAYERED` / `WS_EX_TRANSPARENT` 去“修透明”：两次实验都失败过（黑屏 + 点不掉）。
- 不要再引入任何新的 WPF 浮层压在 RDP 上，哪怕是 `Background=Transparent`。
- 不要用隔离截图（不接真实 RDP 的测试窗口）证明该问题已解决。
## 7. 第二轮：保护层“看着在、其实死了”（2026-11-19 下午）

第一轮修复解决了黑屏，但用户复测发现：**指示灯写着“点击画面接管”，点下去却直接穿透进 RDP**，
而且点击 RDP 画面不切换焦点标签、边框也不高亮。

这两个现象是**同一个根因链**上的两件事，且都属于“静默失效”——不报错、不崩溃，功能就是不生效。

### 7.1 闸门为什么会失效（4 个独立缺陷）

| # | 缺陷 | 后果 | 修复 |
|---|---|---|---|
| 1 | `ArmShield` 只在 `changed` 时才 `InstallMouseHook()` | “已经上锁”的后续调用不再补装钩子；钩子被别处卸掉后闸门永久失效 | 改成**每次调用都补装**（幂等），并顺带刷新画布矩形 |
| 2 | `SetForegroundActive(true)` 不装钩子，而 `false` 那侧会卸载 | 多画面切焦点必然走 false→true，切回来闸门就是死的 | 在 `true` 分支补装，恢复**对称性** |
| 3 | 闸门用 `_activeState == 0` 当前置条件 | 会话监控投送通道时会对**每个**账号（含正在看的那个）调 `SetForegroundActive(false)`，标志被置 1，闸门直接放行 | 闸门不再依赖 `_activeState`；范围限定只靠“只对 `_viewing.Console` 上锁 + 画布矩形” |
| 4 | `ShieldGateLogic` 把 `IsInjected` 当放行条件 | 触摸板驱动 / 鼠标厂商软件 / KVM / 自动化工具发出的事件都带 injected 标志，等于给保护层开后门 | **失败关闭**：本机无任何组件注入鼠标（RelayAgent 在目标会话内注入），不再排除 |

其中 3 和 4 是最难查的：它们让闸门“看起来配置正确、日志正常”，只是 `decision=Pass`。

### 7.2 指示灯为什么在撒谎

`RefreshInputIndicator()` 原来**自己重新推一遍**该不该锁：

```csharp
bool armed = App.Config.Settings.RdpCaptureOnClick && connected && !_shieldReleased && pageVisible;
```

这是纯配置推断，**完全没读闸门的真实状态**。于是闸门早就失效了，界面照样显示“点击画面接管”，
把“保护层死了”伪装成“保护层正常未点击”。

修复：唯一真源改为 `console.ShieldArmed`，并新增 `console.InputGateReady`（钩子已挂 + 画布矩形已缓存）。
两者缺一，界面直接显示“未接管”，不再骗人。

### 7.3 点画面不切焦点 / 边框不高亮

`chrome.MouseLeftButtonUp`（格子点击 → 切焦点）**永远收不到事件**：
RDP 的 ActiveX 原生窗口把鼠标消息吞掉了（airspace 的老问题，与保护层无关）。
以前靠保护层浮层替它接这一下，浮层删掉后这个入口就断了。

修复：闸门在允许点击穿透的那一刻（首次左键 Up）记录 `GetCursorPos`，
连同 `ShieldClicked` 一起交给外层，由 `HitTestTileAt(x, y)` 决定把焦点切到哪个格子。
在钩子里取坐标是必须的——`Dispatcher` 排队到执行时鼠标可能已经移开。

### 7.4 自愈

`ConsolePage` 的 150ms 看门狗每轮调用 `EnsureInputGate()`：钩子没了就补装，
锁定中就把画布矩形刷新一遍。闸门是“拦截”能力，**必须能自愈**，不能指望某次回调刚好把它装上。

### 7.5 验证（自动化，非人工手感）

用合成点击驱动完整循环，日志取证：

```
A 画面内点击   → decision=SwallowDown → 解锁 → 键盘焦点交给 RDP
B 移到画布外   → 保护层上锁（UpdateShield）           ← 自动重新上锁
C 再回画面内点 → decision=SwallowDown → 解锁 → 焦点切到「玄月」
```

指示灯：琥珀「点击画面接管」→ 点击后青色「已接管」（截图 `artifacts/ind-final-*.png`）。

回归程序 `tools/shield-gate-check` 增补 2 条断言（含“与 `_activeState` 无关”），`RESULT=PASS`。

### 7.6 这段代码的坑（给以后改的人）

- **闸门运行在 `WH_MOUSE_LL` 钩子里**：不能调 WPF、不能写日志文件、不能做任何可能超时的事。
  超时后 Windows 会**静默摘钩**（不报错），表现为“保护层突然失效”。
  所以画布矩形是 UI 线程缓存，探测结果只写字段、由 UI 线程落盘。
- **不要给闸门加 `_activeState` / `IsConnected` / 焦点之类的条件**：
  这些标志会被别处的清理逻辑随手改掉，而且它们表达的不是“要不要拦这一次点击”。
- **不要排除 injected 事件**：那是个后门。
- **诊断开关**：`--verbose-log` 或 `SOULLINK_VERBOSE_LOG=1` 会输出
  `保护层探测 ... decision=...`，包含 `armed/activeState/inside/rect/hooked` 全套现场。
  保护层失效是静默故障，没有这份现场就只能靠猜。
