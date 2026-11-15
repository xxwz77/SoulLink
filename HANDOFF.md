# SoulLink 交接文档

> 给接手的新会话看。**只写本会话实际验证过的结论**；未验证的会标「待验证」，不要当成已完成。
> 最后更新：2026-11-15（边框强调色修复）
> 完整历史踩坑记录见 [HANDOFF.archive-2026-11-15.md](HANDOFF.archive-2026-11-15.md)（旧文档，2026-11-14 至 15 日，含 12.1~12.94 全部细节）。

---

## 一、项目定位

把「自动查找 ID 启动其他用户程序.bat」升级为 **多用户挂机中控台**：

1. 同时管理多个 Windows 用户会话（本机：lummy + a1~a4）
2. 跨会话启动/关闭程序，**不抢前台键鼠**
3. 内嵌 RDP 画面、标签页切换
4. 注入鼠标实现**转游戏视角**（RDP 原生做不到）
5. 免安装、解压即用

技术栈：WPF / .NET 8（`net8.0-windows`），单文件发布到 `dist\SoulLink.exe`。

---

## 二、测试红线（必须遵守）

| 用户 | 允许操作 |
| --- | --- |
| **A1、A4** | ❌ 绝对不允许任何操作：不投送、不注入、不 tsdiscon、不 logoff、不查询 |
| **A2、A3** | ✅ 可随意登录/注销/连接画面；**不要删除用户文件** |

其他约束：

- 必须以**管理员权限**运行（跨会话投送与 RDP 注入依赖）。
- 涉及 a2/a3 会话状态的操作，测完要恢复原状态（如该用户原本挂机的程序不能因测试被注销）。
- 测试临时文件放 `tools\`（已被 .gitignore 排除），结束前清理。
- 验证后**主动关闭测试应用**，不要留给用户关。

---

## 三、环境事实

| 项目 | 值 |
| --- | --- |
| 系统 | Windows 11 专业工作站版 Insider Preview，Build 29698 |
| 显卡 | RTX 4060 Laptop，真实显存 8188 MiB（WMI 报 4GB 是 32 位溢出） |
| 多用户 RDP | 依赖 **RDPWrap 补丁**，非原生；系统更新会顶掉 |
| 本机用户 | lummy（当前会话）、a1、a2、a3、a4 |
| 环回地址 | A1=127.0.0.2，A2=127.0.0.3，A3=127.0.0.4，A4=127.0.0.5 |

⚠️ **环回地址不能按会话号推算**：所有环回 RDP 客户端在服务端看来都是 127.0.0.1，
必须以配置/探测结果为准（旧文档 10.6）。

---

## 四、★ 关键技术结论（已实测，不要重新试错）

### 4.1 RDP 密码设置（本会话最终修复）

1Remote 的做法是 `IMsTscNonScriptable.ClearTextPassword`，但**接口必须是 IUnknown，不能是 Dual**：

```csharp
[ComImport, Guid("C1E6743A-41C1-4A74-832A-0DD06C1C7A0E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMsTscNonScriptable
{
    [DispId(1)] string ClearTextPassword { set; }
}
((IMsTscNonScriptable)ocx).ClearTextPassword = password;
```

- `InterfaceIsDual` 在 .NET 8 抛 `NotImplementedException`。
- 旧 IDispatch 路径报「无该名字(0x80004001)」，不可用。
- 该属性**只写、不能回读**验证。
- **A3 已实测**：设置后直接进入桌面，不再弹 Windows 凭据输入框。
- 不要改用 Windows 凭据管理器（`CredWrite`）作为替代方案，方向已否定并移除。
- 接口定义文件：`Services/RdpNonScriptable.cs`；调用点：`Controls/RdpConsole.cs` Configure 阶段。

### 4.2 主窗口必须是非分层窗口

- `AllowsTransparency=True` 的分层窗口里，RDP ActiveX 能连上、会话是 Active，但**画面永远全黑**。
- 主窗口因此用 `AllowsTransparency=False` + 非客户区自绘。
- 圆角只能靠 DWM：`WindowCorners.ApplyRounded`（`DWMWA_WINDOW_CORNER_PREFERENCE`）。
- 顶部多余横幅用 `WM_NCCALCSIZE` 返回 0 去掉（`MainWindow.WndProc`）。**不要**用 `DWMWA_BORDER_COLOR` 把系统主题色边框改成深色（用户明确要求保留系统主题色）。

### 4.2.1 ★ 应用边框用系统强调色（本会话修复，两条都是实测结论）

**坑一：`DWMWA_BORDER_COLOR` 要的是 COLORREF，不是 ARGB。**

- 取值格式是 **`0x00BBGGRR`**，最高字节 alpha **必须为 0x00**。
- 传 `0xFFRRGGBB`（ARGB）→ DWM 返回 **`E_INVALIDARG (0x80070057)`**，边框**静默保持原样**。
  旧代码一直传 ARGB，所以「设置边框颜色」从未生效过，看到的永远是系统默认边框。
- 两个特殊值：`0xFFFFFFFF` = `DWMWA_COLOR_DEFAULT`（恢复原生），`0xFFFFFFFE` = `DWMWA_COLOR_NONE`（不画边框）。
- 已落地：`WindowCorners.SetBorderColor` 接收 ARGB，内部 `ArgbToColorRef` 换算；
  `ResetBorderColor` 用于未激活态。**传值前后务必看返回值，别假设成功。**

**坑二：强调色不能取 `DwmGetColorizationColor`。**

| 来源 | 本机实测值 | 能否用作边框色 |
| --- | --- | --- |
| 注册表 `HKCU\Software\Microsoft\Windows\DWM` → `ColorizationColor` | `#F091C1` | ✅ **正确**，与原生窗口逐像素一致 |
| `DwmGetColorizationColor` | `#E48AB7`（`blend=true`，开启透明效果后是**混合后**的着色） | ❌ 偏暗偏灰 |
| 注册表 `AccentColor` / `AccentColorMenu` | `#F091C1`（COLORREF `0xFFC191F0`，需转换） | ✅ 兜底 |

取值顺序已固定为：**注册表 ColorizationColor → DwmGetColorizationColor → 注册表 AccentColor → 硬编码蓝色**。

**行为（已实测）**：

- 激活态四边 = 强调色 `#F091C1`；与资源管理器激活态顶边 `#F091C1` **逐像素相同**（`docs/fix-accent-border-active.png`）。
- 未激活态交回 `DWMWA_COLOR_DEFAULT`，实测 `#B2B2B3` 灰边，与原生窗口失焦表现一致（`docs/fix-accent-border-inactive.png`）。
- 改系统强调色后会**实时刷新**（`SystemEvents.UserPreferenceChanged` 钩子）：实测改蓝立即变 `#3A8BD5`，改回立即可复原。
- 应用内深浅主题切换**不影响**边框色（本来就该跟系统强调色走），三种模式下实测均为 `#F091C1`。
- 最大化时四边无色属**正常**：原生窗口最大化同样不出边框，不是缺陷。

### 4.3 RDP 空域（Airspace）：WPF 浮层盖不住画面

- RDP 是 `WindowsFormsHost` 里的 ActiveX，永远绘制在 WPF 之上，`Panel.ZIndex` 无效。
- 需要浮层时只能用 **`Popup`** 或**独立窗口**。
- 已落地：侧栏浮层用 `Popup`；注销过场用独立 `Window`（`Views/LogoffTransitionWindow`）；
  连接遮罩用 `BusyPopup`。

### 4.4 注销过场（独立窗口）的坑

- 过场必须只属于**当前用户视口**：通过 `Func<bool>` 归属判断 + 80ms 定时器检查。
- 切页/失焦/最小化时隐藏；完成态禁止再次 `Show()`（否则会残留在其他用户画面上）。
- **已实测**：A2 注销过程中切到 A3，A3 画面正常显示不被覆盖。

### 4.5 跨会话注入必须真 SYSTEM

| 方案 | 结果 |
| --- | --- |
| 管理员令牌 + `WTSQueryUserToken` | ❌ 1314 |
| 窃取 SYSTEM 令牌后冒充 | ❌ 1314 |
| `CreateProcessWithTokenW` | ⚠️ 假成功（进程落在调用者会话） |
| 真 SYSTEM（服务 / `schtasks /ru SYSTEM`） | ✅ 可用，免安装方案 ~459ms |

注入代理直接复用 `SoulLink.exe` 本体（`--relay` 模式），通过命名管道下发指令。

### 4.6 其他已验证结论

- 鼠标注入：`mouse_event` 相对位移可转视角；`SetCursorPos` 绝对定位误差 0；点击/右键/滚轮均命中。
- **Disc（断开）会话没有可交互桌面**，必须先让 RDP 接管画面才能注入。
- 对**已断开**的会话执行 `tsdiscon` 会直接注销它（旧文档 10.7，务必避免）。
- 同一会话重复连接会把前一个客户端踢掉。
- 转视角必须临时关闭系统指针加速，且**幂等恢复**（引用计数），退出时兜底还原。
- 坐标换算必须用物理像素（`PointToScreen`），不能混用 DIP：本机 150% 缩放。
- DesktopScaleFactor 等 DPI 属性在本机不被 RDP 控件接受（4 种写法均失败），靠宿主侧 `CoordinateMapper` 消化。

---

## 五、当前代码结构

```
SoulLink/
├─ App.xaml.cs              启动、单实例、IPC、托盘、异常日志
├─ Models/Models.cs         Account / ActionItem / AppSettings / AutomationProfile
├─ Controls/
│   ├─ RdpConsole.cs        内嵌 RDP 控件 + 输入接管 + 密码/性能/重定向配置
│   ├─ RdpGeneralSettings.* RDP 通用设置（作用于全部用户）
│   ├─ Backdrop.cs          梦幻极光背景（随主题深浅变色）
│   ├─ GlassControls.cs     玻璃风格自定义控件
│   └─ HotkeyRecorder.cs    热键录制
├─ Services/
│   ├─ ThemeManager.cs      三态主题 + 动态资源刷新
│   ├─ WindowCorners.cs     DWM 圆角 / 边框颜色 / 系统强调色
│   ├─ RdpNonScriptable.cs  IMsTscNonScriptable（IUnknown）
│   ├─ NativeDispatch.cs    原生 IDispatch 桥（其他属性）
│   ├─ SessionService.cs    query user / 会话解析
│   ├─ SessionPrepare.cs    tsdiscon / logoff
│   ├─ SystemLauncher.cs    SYSTEM 计划任务通道
│   ├─ SessionInput.cs      注入代理控制
│   ├─ LowLevelMouseHook.cs 全局鼠标钩子
│   ├─ PointerAcceleration.cs 指针加速幂等恢复
│   ├─ AutomationRunner.cs  多用户顺序执行 + 每日定时
│   └─ ConfigStore.cs       JSON 配置 + DPAPI 密码
├─ Views/
│   ├─ MainWindow.*         主窗口（侧栏 Popup、主题、导航）
│   ├─ ConsolePage.*        中控台（用户列表 / 配置 / 画面标签）
│   ├─ AutomationPage.*     自动化管理（配置组 / 定时）
│   ├─ SettingsPage.*       应用设置（不含 RDP 凭据）
│   ├─ AppDialog.*          统一自绘弹窗
│   ├─ LogoffConfirmWindow.* 注销二次确认
│   └─ LogoffTransitionWindow.* 注销过场（独立 HWND）
└─ Theme/Styles.xaml        全部样式与主题画刷
```

### 配置字段要点（`Models/Models.cs`）

- `Account.SortOrder`：用户列表顺序（拖动排序持久化）。
- `Account.RdpUserName`：RDP 登录名；`UserName`：跨会话注入/会话解析用的 Windows 账户名。**两者不能混用**。
- `Account.RdpAudioMode / RdpPerformanceMode / RdpKeyboardHookMode / RdpRedirect*`：已实测保存并回读。
- `AppSettings.ThemeMode`：0=跟随系统、1=浅色、2=深色。
- `AppSettings.RdpCaptureOnClick`：点击画面才捕获键鼠。
- `AppSettings.LookSensitivity / LockHotkey`：自由视角灵敏度与热键。
- `AutomationProfile`：配置组（用户范围、动作流程、每日定时、启用状态）。

---

## 六、已完成并实测

| 功能 | 状态 | 证据 |
| --- | --- | --- |
| 用户多选 + 批量打开画面 | ✅ | `docs/verify-multi-select.png` |
| 新增用户即进入配置表单 | ✅ | `docs/verify-new-user.png` |
| 用户拖动排序（右侧手柄，`SoulLinkAccount` 拖放格式） | ✅ 顺序写入 `SortOrder` | `docs/verify-sort.png` |
| 顶部固定「保存配置/移除用户」 | ✅ | `docs/fix-inline-password.png` |
| 账号密码内联编辑（端口 100px 固定列） | ✅ | `docs/fix-inline-password.png` |
| A3 保存密码后自动登录 | ✅ 进入桌面 | `docs/fix-a3-login.png` |
| 自由视角按钮进入/退出 | ✅ | 按钮文案切换「自由视角 / 自由视角中」 |
| RDP 通用设置内嵌（全用户共享） | ✅ | `docs/fix-general-settings.png` |
| 通用设置可返回用户配置 | ✅ | 实测点击用户卡片 |
| 断开关闭标签页（不注销用户） | ✅ | 实测 |
| 无有效会话时注销只关画面 | ✅ | 代码路径 `CloseViewPage` |
| 有效会话注销：二次确认 + 过场 + 恢复配置页 | ✅ | `docs/verify-logoff-confirm.png` 等 |
| 过场不覆盖其他用户画面 | ✅ | `docs/transition-other-user.png`、`transition-complete.png` |
| 浅色侧栏随主题变浅、无中间缝 | ✅ | `docs/fix-light-sidebar.png` |
| 系统主题色边框 + 无顶部粉条 | ✅ | `docs/window-top-caption.png`、`window-top-crop.png` |
| 边框真正用上系统强调色（COLORREF 修复） | ✅ 激活 `#F091C1` / 失活灰边，与原生窗口逐像素一致 | `docs/fix-accent-border-active.png`、`fix-accent-border-inactive.png` |
| 统一自绘弹窗替代 MessageBox | ✅ | `Views/AppDialog` |
| 自动化配置组 + 顺序执行 + 每日定时 | ✅ 保存与界面 | `docs/verify-automation.png` |

---

## 七、待验证 / 待完成

以下**不要当成已完成**：

1. **点击捕获（`RdpCaptureOnClick`）的完整手感**：首次点击已改为送达 RDP，但“移出即释放、重新点击才接管”的多场景（切页、失焦、最小化）尚未逐项实测。
2. **浅色模式跨页面文字对比度**：侧栏已修，启动台/在线会话/会话详情页是否全部达标未逐页确认。
3. **自动化实际执行**：只验证了配置保存与界面，未实际跑过一条龙流程与定时触发。
4. **应用设置页（`SettingsPage`）**：已移除 RDP 凭据区，但页面内仍有「修改密码/地址/移除」按钮绑定的旧事件处理，需要确认没有残留入口或空操作。
5. **窗口最大化下的边框/圆角**：已确认最大化时无边框属正常（原生窗口同样如此），
   圆角与拖拽还原在最大化/还原切换后正常；但**最大化下的圆角裁切**仍未逐像素核对过。
6. **多用户同时注销**：只验证了 A2 注销期间切 A3，未测试两个用户同时注销。

---

## 八、开发与发布流程

```powershell
# 编译
dotnet build -c Release

# 发布（生成 dist\SoulLink.exe）
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist

# 或直接运行 build.bat
```

- 发布后**必须实机启动验证并截图**，不能只凭编译通过就报完成。
- 验证完关闭自己启动的 SoulLink 进程：
  ```powershell
  Get-Process SoulLink -ErrorAction SilentlyContinue | Stop-Process -Force
  ```
- 日志位置：`%LOCALAPPDATA%\SoulLink\`：`boot.log`、`error.log`、`rdp-debug.log`。
- 配置位置：`%LOCALAPPDATA%\SoulLink\config.json`（密码为 DPAPI 密文）。

---

## 九、版本管理

项目已初始化本地 Git，详见 [VERSIONING.md](VERSIONING.md)。

```powershell
git log --oneline -10                      # 查看提交
git restore --source=HEAD --staged --worktree -- .   # 丢弃未提交修改
git switch --detach <hash>                 # 查看旧版本
git revert <hash>                          # 撤销已提交的错误
```

提交约定：

- 只提交源码、XAML、文档；`bin/obj/dist/tools` 与本地凭据已忽略。
- **每次实机验证通过后再提交**，提交信息写清修复内容。
- `dist\SoulLink.exe` 不在 Git 中，回退源码后需重新发布。
- 回退源码**不会**回退 `%LOCALAPPDATA%` 下的用户配置，改配置前单独备份。

近期提交：

| 提交 | 说明 |
| --- | --- |
| 本次 | 修复边框未使用系统强调色（COLORREF 格式 + 强调色取值来源） |
| `6ad4676` | 修复窗口顶部额外粉条，边框随激活状态用系统主题色 |
| `4f897f8` | 添加版本管理与安全回退说明 |
| `bac46e8` | 稳定基线 |

---

## 十、已知踩坑速查

| 现象 | 原因 / 处理 |
| --- | --- |
| RDP 画面全黑 | 主窗口被改成分层窗口（`AllowsTransparency=True`） |
| 保存的密码不生效、弹凭据框 | 接口写成 `InterfaceIsDual`；必须 `InterfaceIsIUnknown`（见 4.1） |
| 点击偏移 | DIP 与物理像素混用；用 `PointToScreen` |
| WPF 浮层被 RDP 盖住 | 空域限制，改 `Popup` 或独立窗口 |
| 注销过场挡住别的用户 | 过场缺归属判断，或完成态仍允许 `Show()` |
| 对 Disc 会话 `tsdiscon` 直接注销 | 先判断 `IsDisconnected` |
| 侧栏浅色下仍是深色 | 局部固定背景覆盖主题，需用 `NavBackground` 动态资源 |
| 输入框与下拉框不等高 | 统一 `GlassTextBox` / ComboBox 高度与内边距 |
| 窗口四周粉色粗边 | 不是 `DWMWA_BORDER_COLOR`，是顶部非客户区横条；用 `WM_NCCALCSIZE` 去掉 |
| 边框怎么都不变色 | `DWMWA_BORDER_COLOR` 传了 ARGB；必须 COLORREF（`0x00BBGGRR`，alpha=0），否则 `E_INVALIDARG` 静默失效（4.2.1） |
| 边框色偏暗偏灰 | 用了 `DwmGetColorizationColor`（混合着色）；改读注册表 `DWM\ColorizationColor`（4.2.1） |

---

## 十一、新会话开工建议

1. 先读本文，需要细节再查 `HANDOFF.archive-2026-11-15.md`。
2. 确认以管理员运行，`dotnet build` 通过。
3. 动手改 UI 前先截图当前状态，便于对比。
4. 只碰 A2/A3，测完恢复会话状态并关闭应用。
5. 每项改动实机验证 + 截图后再 `git commit`。
