# SoulLink 固定版本交接 · RDP 保护层黑屏修复

> 固定日期：2026-11-19  
> 基线名称：**2.11.0 · RDP 保护层修复基线**  
> 状态：标准版固定，**真机验收通过**（保护层、多画面切换、重新上锁均正常）；挂机版未修改。

## 1. 成品身份

| 项目 | 当前状态 |
|---|---|
| 标准版 | `dist/SoulLink.exe` |
| 版本号 | 2.11.0.0（未改版本号） |
| 大小 | 67,194,501 字节 |
| SHA256 | `6B2356B7CBD850AF447C07068FDAEE77A8BAB0B985CFDD7653E835F052C61365` |
| 固定副本 | `artifacts/fixed-rdp-shield-20261119/SoulLink.exe` |
| 挂机版 | `dist/stable/SoulLink-Stable.exe`，**本轮未修改、未重新构建** |

旧主题基线归档仍在 `artifacts/fixed-v2.11.0-dreamscape-20261119/`，
`releases/v2.11.0-reliable/` 保持不变，两者都**不是**本次成品。

## 2. 本次修复的核心结论

**RDP 画面上不能盖任何分层窗口。**

| 证据 | 内容 |
|---|---|
| `mstscax.dll` 字符串 | `Software mode is not implemented`、`Not using hardware. Software overlay layer is not implemented` |
| `mstscax.dll` 依赖 | 导入 `dcomp.dll` / `dxgi.dll`，使用 `CreateSwapChainForComposition` |
| 推论 | RDP 控件**只有硬件合成路径，没有软件回退** |
| WPF 侧 | `AllowsTransparency=True` 等价于 `WS_EX_LAYERED` + 逐像素 alpha |
| 结果 | 分层窗口压到 DirectComposition 表面上，该区域退出硬件合成 → **整块黑** |

## 3. 保护层最终实现

### 3.1 机制：禁用 RDP 原生宿主窗口

```csharp
EnableWindow(_host.Handle, false);   // 锁定
EnableWindow(_host.Handle, true);    // 解锁
```

- 不新建/不销毁窗口，不动合成表面 —— **不会黑屏，也不具备 0x119 的触发条件**
- 禁用后 `WindowFromPoint` 从 RDP 子窗口变成 WPF 主窗口，
  鼠标移动/悬停/点击/滚轮在系统层面全部送不进 RDP
- 画面照常渲染（`WS_DISABLED` 只影响输入路由）

### 3.2 交互语义

| 场景 | 行为 |
|---|---|
| 锁定态 | 边框为自定义色（默认琥珀 `#CCF0C041`），框住**每一个**锁定画面 |
| 点击画面 | 一次点击完成：切焦点 + 解锁全部画面 |
| 解锁态 | 点击 RDP 画面，靠 `WM_MOUSEACTIVATE` 当拍切焦点 |
| 鼠标移出画布 / 窗口失活 | 重新上锁 |
| 自由视角 | 解锁；退出后重新上锁 |

### 3.3 焦点识别

解锁态下 RDP 会吞掉鼠标消息，WPF 收不到点击，
因此用 `RdpConsole.HasKeyboardFocus` 三重判定（AxHost / 前台窗口 / `GetGUIThreadInfo`）反推用户点了哪一格。

**关键坑**：`EnableWindow(FALSE)` **不会转移键盘焦点**，被禁用的窗口会残留焦点。
所以 `HasKeyboardFocus` 必须先排除 `_hostDisabled`，否则锁定态切焦点会被弹回旧画面。

### 3.4 代码落点

| 文件 | 职责 |
|---|---|
| `Controls/RdpConsole.cs` | `LockShield` / `UnlockShield` / `HasKeyboardFocus` / `ForceKeyboardFocus` |
| `Services/NativeDispatch.cs` | `RdpAxHost.MouseActivated`（`WM_MOUSEACTIVATE` 当拍通知） |
| `Views/ConsolePage.xaml.cs` | 锁存、看门狗、焦点跟随、重新上锁判定 |
| `Views/ConsolePage.Tiles.cs` | `EnterTakeover`（一次点击完成接管）、边框绘制 |
| `Controls/RdpGeneralSettings.xaml(.cs)` | 「保护层边框」颜色/粗细设置 |
| `Services/ColorText.cs` | 颜色字符串解析（`#RGB` / `#RRGGBB` / `#AARRGGBB`） |
| `Models/Models.cs` | `ShieldBorderColor` / `ShieldBorderThickness`（默认 2） |

## 4. 已验证 / 未验证

### 已验证（实测）

- 双画面正常渲染，无黑色遮盖
- RDP 之上可见 `WS_EX_LAYERED` 窗口数为 0
- 锁定态两个宿主窗口 `enabled=False`（逐窗口枚举确认）
- 解锁后可操作；鼠标移出画布重新上锁；切应用回来第一下解锁
- 解锁态点画面可切换焦点（`WM_MOUSEACTIVATE` 路径）
- 保护层边框颜色/粗细可自定义且持久化

### 焦点回落问题（已修复并验证）

**症状**：锁定态从 A 点 B，焦点框会回落到 A。

**根因链**（三层叠加，逐层修掉）：

1. `EnableWindow(FALSE)` **不会转移键盘焦点**，被禁用的宿主窗口仍残留焦点；
2. `GiveKeyboardFocus` 有一道 `CaptureOnClick && !_pointerCaptured` 的前置返回，
   解锁流程里直接 return，**根本没设置焦点**；
3. 看门狗 150ms 跟随"谁有焦点"，于是把焦点拉回残留的旧画面。

**修复**：

| 层 | 修复 |
|---|---|
| 焦点识别 | `HasKeyboardFocus` 先排除 `_hostDisabled` 与 `!IsWindowEnabled` |
| 焦点转移 | 新增 `ForceKeyboardFocus()`，绕过前置判断无条件 `SetFocus` |
| 接管时序 | `EnterTakeover` 第 ④ 步显式把系统焦点交给被点的那一格 |
| 抑制抖动 | 接管后 600ms 内不做焦点跟随 |
| 状态隔离 | 锁定态完全不跟随焦点（`IsShieldLockedNow()` 为真时跳过） |

**验收**：用户真机确认「目前一切正常」——锁定态 A↔B 切换、解锁态切换、
鼠标移出画布重新上锁、切应用回来解锁，均正常。

### 未验证（需要长期观察）

- 真实 RDP 下的长时间稳定性（数小时挂机）
- 注销、重连、最小化、多开账号等完整生命周期
- `RdpFocusBorder` 相关实验字段已全部回滚，配置无残留

## 5. 回滚

| 目标 | 路径 | SHA256 |
|---|---|---|
| 修复前（2.11.0 主题基线） | `artifacts/dist-backup-before-shield-fix/SoulLink.exe` | `FCD67B470ADC1DB723DF96891E5D6851844461D3725D6313CE92669218324F5D` |
| 本次固定 | `artifacts/fixed-rdp-shield-20261119/SoulLink.exe` | `6B2356B7CBD850AF447C07068FDAEE77A8BAB0B985CFDD7653E835F052C61365` |

回滚按 PID + 完整路径确认后替换，**不要** `git restore` 整个仓库。

## 6. 禁止事项

- **不要**再往 RDP 画面上盖任何 WPF 浮层，哪怕是 `Background=Transparent`。
- **不要**手动叠加 `WS_EX_LAYERED` / `WS_EX_TRANSPARENT` 去修透明（两次实验都失败）。
- **不要**用隔离截图（不接真实 RDP）证明黑屏问题已解决。
- **不要**改 `RdpCaptureOnClick` 的语义（锁定 = 禁用宿主窗口，不是拦消息）。
