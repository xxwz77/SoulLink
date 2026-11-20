# 交接文档 · SoulLink 2.12.0 · RDP 保护层重构

> 固定日期：2026-11-19  
> 版本号：**2.12.0.0**（从 2.11.0 升级）  
> 主题：RDP 保护层从「半透明浮层」彻底改为「禁用原生宿主窗口」

## 1. 成品与存档

| 项目 | 值 |
|---|---|
| 标准版 | `dist/SoulLink.exe` |
| 版本号 | 2.12.0.0 |
| 大小 | 67,196,808 字节 |
| SHA256 | `844D4E7E88F2325172FB38A5117BF5BEC9C416432EE8FE877C5F2938E786AC96` |
| 源码存档 | `E:\SoulLink-SOURCE-ARCHIVE\v2.12.0\source\` |
| 成品存档 | `E:\SoulLink-SOURCE-ARCHIVE\v2.12.0\SoulLink-2.12.0.exe` |
| 升级前备份 | `E:\SoulLink-SOURCE-ARCHIVE\20261119-1517-before-2.12\` |

## 2. 核心结论：为什么不能用浮层

**RDP 画面上不能盖任何分层窗口。**

| 证据 | 内容 |
|---|---|
| `mstscax.dll` 字符串 | `Software mode is not implemented`、`Not using hardware. Software overlay layer is not implemented` |
| 依赖 | 导入 `dcomp.dll` / `dxgi.dll`，走 `CreateSwapChainForComposition` |
| 推论 | RDP 控件**只有硬件合成路径，没有软件回退** |
| WPF 侧 | `AllowsTransparency=True` 等于 `WS_EX_LAYERED` 加逐像素 alpha |
| 结果 | 分层窗口压到 DirectComposition 表面上，该区域退出硬件合成，**整块黑** |

这与 `HANDOFF.md` 4.2 记录的老结论（分层窗口里 RDP 永远全黑）是同一机制。

## 3. 最终实现

### 3.1 机制

```csharp
EnableWindow(_host.Handle, false);   // 锁定
EnableWindow(_host.Handle, true);    // 解锁
```

- 不新建/不销毁窗口、不动合成表面，**不会黑屏，也不具备 0x119 触发条件**
- 禁用后 `WindowFromPoint` 从 RDP 子窗口变成 WPF 主窗口，
  鼠标移动/悬停/点击/滚轮在系统层面全部送不进 RDP
- 画面照常渲染（`WS_DISABLED` 只影响输入路由）

### 3.2 交互语义

| 场景 | 行为 |
|---|---|
| 锁定态 | 每个已连接画面都是琥珀色边框（`#CCF0C041`，可在设置里改） |
| 指示灯 | 三态：已接管（青）/ 点击画面接管（琥珀）/ 未接管（灰） |
| 点击画面 | 一次完成：切焦点 + 解锁全部画面 |
| 解锁后 A/B 切换 | 靠 `WM_MOUSEACTIVATE` 当拍切焦点，**不重新上锁** |
| 鼠标移出画布 / 窗口失活 | 重新上锁 |
| 重连 / ActiveX 句柄变化 | 重新上锁（安全优先） |

### 3.3 代码落点

| 文件 | 职责 |
|---|---|
| `Controls/RdpConsole.cs` | `LockShield` / `UnlockShield` / `HasKeyboardFocus` / `ForceKeyboardFocus` / `EnsureShieldEnforced` |
| `Services/NativeDispatch.cs` | `RdpAxHost.MouseActivated`（`WM_MOUSEACTIVATE` 当拍通知） |
| `Views/ConsolePage.xaml.cs` | 锁存、看门狗、`ApplyShieldToAllTiles`、`FollowRdpFocus`、`ShouldRelockByPointer` |
| `Views/ConsolePage.Tiles.cs` | `EnterTakeover`（一次点击完成接管）、`UpdateTileChrome`（边框绘制） |
| `Controls/RdpGeneralSettings.xaml(.cs)` | 「保护层边框」颜色/粗细设置 |
| `Services/ColorText.cs` | 颜色解析（`#RGB` / `#RRGGBB` / `#AARRGGBB`） |
| `Models/Models.cs` | `ShieldBorderColor` / `ShieldBorderThickness`（默认 2） |
| `Views/MainWindow.xaml.cs` | `WM_GETMINMAXINFO`（最大化保留任务栏） |

## 4. 踩过的坑（改代码前必读）

| # | 坑 | 症状 | 正确做法 |
|---|---|---|---|
| 1 | 用半透明浮层做保护层 | RDP 区域整块黑 | 用 `EnableWindow` 禁用宿主窗口 |
| 2 | 只在低级鼠标钩子里吞左键 Down/Up | 鼠标**移动**照样进 RDP，等于没保护 | 同上，必须禁窗口 |
| 3 | `EnableWindow(FALSE)` 不会转移键盘焦点 | 被禁用窗口残留焦点，看门狗把焦点拉回旧画面 | `HasKeyboardFocus` 先排除 `_hostDisabled` |
| 4 | `GiveKeyboardFocus` 有 `CaptureOnClick && !_pointerCaptured` 前置返回 | 解锁时直接 return，**根本没设焦点** | 用 `ForceKeyboardFocus()` 绕过 |
| 5 | `SetFocusFromShield` 改 `_viewing`，看门狗拿 `_viewing` 判身份 | A/B 切换被当成新画面，反复上锁 | 区分重连与单纯切焦点 |
| 6 | `ApplyShieldToAllTiles` 改完状态没重画边框 | 锁着但看不到框 | 结尾调 `UpdateTileChrome()` |
| 7 | 指示灯自己按 `_shieldReleased` 反推 | 锁定态永远显示未接管 | 读真实 `ShieldLocked` |
| 8 | `SetFocus` 末尾 `Dispatcher.BeginInvoke(UpdateShield)` 延时执行 | 解锁后又被锁回 | 已接管时只刷指示灯，不调 `UpdateShield` |

## 5. 已验证 / 未验证

### 已验证（用户真机确认）

- 双画面正常渲染，无黑色遮盖
- RDP 之上可见 `WS_EX_LAYERED` 窗口数为 0
- 锁定态两个宿主 `enabled=False`（逐窗口枚举确认）
- 锁定态边框出现、指示灯显示「点击画面接管」
- 点击画面一次解锁（切焦点 + 解锁全部）
- 解锁后 A/B 切换焦点（紫框来回）
- 保护层边框颜色/粗细可自定义并持久化
- 最大化保留任务栏

### 未验证

- 长时间挂机稳定性（数小时）
- 注销、重连、最小化等完整生命周期
- 3 个以上账号同时显示

## 6. 已知未做

**最大化后画面不跟着放大（灰边）**

- 现象：`RdpScaleMode=1`（拉伸）下，窗口放大但远端内容仍是 1920×1080，四周露底色
- 原因：`SmartSizing` 是连接时的设备属性，窗口尺寸变化不会自动重算
- 用户要求：`RdpScaleMode=1` 固定，**不改远端分辨率**，只做本地显示缩放
- 尚未解决

## 7. 禁止事项

- **不要**再往 RDP 画面上盖任何 WPF 浮层，哪怕是 `Background=Transparent`
- **不要**手动叠加 `WS_EX_LAYERED` / `WS_EX_TRANSPARENT` 去修透明（两次实验都失败）
- **不要**用隔离截图（不接真实 RDP）证明黑屏问题已解决
- **不要**整文件 `write` 覆盖 `RdpConsole.cs` / `ConsolePage.xaml.cs`（本会话坏过两次，只能用 `edit` 精确改）

## 8. 回滚

| 目标 | 路径 | SHA256 |
|---|---|---|
| 2.11.0 基线 | `E:\SoulLink-SOURCE-ARCHIVE\20261119-1517-before-2.12\` | `C7F5F1C1…` |
| 2.12.0 | `E:\SoulLink-SOURCE-ARCHIVE\v2.12.0\` | `844D4E7E…` |

回滚按 PID + 完整路径确认后替换，**不要** `git restore` 整个仓库。

## 9. 版本演进记录

| 版本 | 变更 | 状态 |
|---|---|---|
| 2.11.0 | 梦幻星海主题基线（月莓星轨 / 云端棱镜） | 视觉固定，保护层未修复 |
| **2.12.0** | **保护层改为禁用原生宿主窗口；新增边框颜色设置；最大化保留任务栏** | **保护层已验证** |

版本号唯一来源是 `SoulLink.csproj` 的 `<Version>`，
`FileVersion` / `AssemblyVersion` 自动派生。改这一处即可。

## 10. 构建与部署

```powershell
# 发布到独立暂存目录，确认成功后再替换 dist
dotnet publish SoulLink.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none `
  -o artifacts/pub-2.12.0 --nologo
```

替换 `dist/SoulLink.exe` 前：

1. 按 **PID + SessionId + 完整路径** 确认目标进程，不要按名字批量结束
2. 先备份当前 dist 到 `E:\SoulLink-SOURCE-ARCHIVE\`
3. 替换后核对 `SHA256` 与 `VersionInfo.FileVersion`

## 11. 编译前务必知道的缓存陷阱

**改完 XAML 后如果报「控件名不存在」，先清 `obj` / `bin`。**

本会话踩过：从存档恢复 `ConsolePage.xaml` + `ConsolePage.xaml.cs` 后，
编译仍报 `FullScreenMenuBar` 不存在 —— 实际是 `obj` 里的增量缓存还引用着
上一版裁剪过的 XAML。清掉 `obj`/`bin` 后 0 错误。

## 12. 源码存档位置

```
E:\SoulLink-SOURCE-ARCHIVE\
├── v2.12.0\                      ← 当前版本（source\ + SoulLink-2.12.0.exe）
├── 20261119-1517-before-2.12\    ← 升级前备份（2.11.0）
├── 20261119-1426-full\           ← 保护层改造完成时的全量快照
├── dist-backup-before-indicator-fix\
├── dist-backup-before-identity-fix\
└── 20261119-broken-backup\       ← 本会话曾改坏的版本（备查，勿用）
```

**存档目录不要放在仓库内**：`artifacts/` 不在 csproj 排除列表里，
放在里面的 `.cs` 会被通配符编译进去，导致构建失败。
