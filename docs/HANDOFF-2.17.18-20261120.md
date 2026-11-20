# SoulLink 交接文档 · 2.17.18（未定版）

> 固定时间：2026-11-20 13:35
> 版本号：**2.17.18.0**（**尚未定版**）
> 性质：本轮**没有**新增功能，只针对「窗口内侧灰/白粗框」做了两处改动，
> **白框在受控条件下已消失，但根因未彻底坐实**，因此不定版。
> 建议：新会话先读第 3 节和第 6 节，再决定从哪里开始。

---

## 0. 一句话现状

2.17.17 遗留的「窗口内侧灰/白粗框」在本轮被两处改动压住了（实测三种焦点态全部干净），
但**两处改动都属于「盖住」而非「根除」**，且用户尚未确认肉眼观感。
**代码可回退，回退点见第 7 节。**

---

## 1. 产物与状态

| 项目 | 值 |
|---|---|
| 标准版 | `dist/SoulLink.exe` |
| 版本号 | 2.17.18.0（**csproj 里仍是 2.17.17，尚未改**） |
| 大小 | 67,476,014 字节 |
| SHA256 | `C640D9E825F66498DB12B9668848246A1FC56D90F3628A63B6B1A2918BDF668B` |
| 构建时间 | 2026-11-20 13:34:06 |
| **状态** | **未定版**（白框根因未坐实） |
| 挂机版 | `dist/stable` **全程未动**，仍 2.17.4.0 `C83BDD34...` |
| 上一固定版 | 2.17.16.0，归档 `E:\SoulLink-SOURCE-ARCHIVE\v2.17.16-reliable\` |
| 备份 | `tools/cfg-backup-20261120-124531.json`（用户配置，实验前备份） |

---

## 2. 本轮改了什么（只有两个文件）

### 2.1 `Controls/Backdrop.cs` —— 中/低画质也做「低频整窗重绘」

```csharp
// SyncAnimation()：订阅条件不再只看"是否激活"，而是"可见且非最小化"
bool canRender = IsLoaded && IsVisible && ActualWidth > 2 && ActualHeight > 2
                 && (_window == null || (_window.IsVisible && _window.WindowState != WindowState.Minimized));

// OnFrame()：高档 33ms 推进动画；中/低档只按慢速节奏 Invalidate，不推进 _time
if (animated && active) { ...33ms 动画... return; }
if (now - _last < RepaintInterval(tier)) return;   // 中 500ms / 低 1000ms
InvalidateVisual();
```

**保留**了 `Activated`/`Deactivated` 订阅（见 3.3，这点很关键）。

### 2.2 `Views/MainWindow.xaml.cs` —— 撤销 `WM_NCCALCSIZE` 的 11px 收缩

2.17.17 曾在 `WndProc` 里把 `rgrc0` 四边各收缩 `SM_CXSIZEFRAME + SM_CXPADDEDBORDER`（=11px），
理由写的是"窗口内侧露出残余边"。**那个归因是错的**，且它本身制造了新问题：

```csharp
// 现在：客户区撑满整个窗口
if (msg == WM_NCCALCSIZE && wParam != IntPtr.Zero) { handled = true; return IntPtr.Zero; }
```

---

## 3. 实测证据（重要，别重复做）

所有数据来自 `tools/verify2.ps1`（启动独立实例 → 聚焦截图 → 失焦截图 → 再聚焦截图）
与 `tools/seq/*.py`（逐像素扫描四边前 14px）。**沿窗口外边沿向内数第 0 个像素起。**

### 3.1 白带的真实像素结构

失焦态（2.17.17 原始版本）：

```
TOP    : BDBDBD BDBDBD | F4F7FC x8 | 201C39 ...（内容）
LEFT   : B2B2B3 B2B2B3 | F4F7FC x8 | 1A1935 ...
BOTTOM : A5A5A5 A5A5A5 | F4F7FC x8 | 1B2841 ...
RIGHT  : B5B5B5 B5B5B5 | F4F7FC x8 | 28243E ...
```

- 最外 2px = 系统边框（聚焦 `#F091C1` 强调色 / 失焦 `#B2B2B3` 灰）
- 紧跟 8px = `#F4F7FC` 白带（失焦）或 `#B4B4B4` 灰带（聚焦）
- **厚度恒定 8px，不随窗口尺寸变化**（900x600 与 1613x1076 测得一致）

### 3.2 决定性证据：它只在「窗口自己的渲染目标」里

| 捕获方式 | 结果 |
|---|---|
| `PrintWindow` + `PW_CLIENTONLY`(1) | **有**白带（顶/左有，底/右无 —— 复现了"缺口"） |
| `PrintWindow` + `PW_RENDERFULLCONTENT`(2) | **没有**白带 |
| 屏幕抓取 | 有（看到的就是屏幕内容） |

→ 结论：**它不是 DWM 合成层画的，也不是应用主题色，而是窗口客户区里一块"没被内容覆盖"的区域**，
露出的是渲染目标的残留色。

### 3.3 ⚠ 焦点事件触发的那次重绘才是真正有效的覆盖

做过一组对照实验（2026-11-20 13:29）：

| 版本 | 聚焦 | 失焦 | 再聚焦 |
|---|---|---|---|
| 重绘订阅**含** `IsActive` 门槛 | 干净 | 干净 | 干净 |
| 重绘订阅**去掉**焦点联动 | **白带出现** | **白带出现** | 干净 |

**结论：把 `Activated`/`Deactivated` 订阅删掉，白带会回来。**
"焦点变化"本身触发的重绘是有效覆盖手段，低频定时器替代不了它。
**新会话千万不要再删这两个订阅。**

### 3.4 `WM_NCCALCSIZE` 收缩会把白带"藏"起来

收缩 11px 后测得：

```
TOP : F091C1 x10 | 内容     <- 白带没了，但边框变成 10px 粗
```

**不是修好了，是被粗边框盖住了。** 用户当时指出"顶部边框变宽了"，完全正确。
撑满客户区后边框回到标准 2px。

### 3.5 本轮最终（已恢复版本）三态实测

```
聚焦  : F091C1 F091C1 | 1C1835 ...（直接进内容）        四边均无白带
失焦  : 1C1835 ...（直接进内容，无边框、无白带）        四边均无白带
再聚焦: F091C1 F091C1 | 1C1835 ...                      四边均无白带
```

失焦态连 2px 外框都没有 —— 这是 DWM 在失焦时的正常表现（`ResetBorderColor` 用的是 `ColorNone`）。

---

## 4. ⚠️ 根因未完全坐实（未定版的原因）

**已知**：
- 那块区域在客户区内、厚度恒定 8px、颜色是"最后一次绘制的残留"
- 焦点变化触发的重绘能覆盖它；低频定时重绘覆盖不了（或覆盖不持久）
- 中/低画质才明显，高画质（`CompositionTarget.Rendering` 每 33ms 全量重绘）看不到

**未证实**：
- 那 8px 到底是谁的？内容为什么覆盖不到？
  - 猜测：`WindowStyle=None` + `CanResize` 时 WPF 的渲染目标比客户端区域小一圈
  - 但**没有**做最终判定（比如读取 `HwndSource.RootVisual` 的实际布局尺寸）
- 为什么"焦点事件的那次重绘"有效而"定时 Invalidate"无效

**因此**：当前方案是"把这块区域刷干净"，不是"让它不存在"。
如果以后又冒出边缘异常，**回来查这一节**。

---

## 5. 已排除的假设（都做过实验，别再试）

| 假设 | 做法 | 结果 |
|---|---|---|
| `RootLayer.Margin` 内缩 | 启动路径设 14 / 改回 0 | 像素**完全不变** |
| `WM_NCCALCSIZE` 撑满客户区 | 返回 0 | **不变**（但见 3.4，它会让粗边框出现） |
| DWM 边框色 | `ResetBorderColor` 用 `ColorDefault` vs `ColorNone` | **不变** |
| 窗口位置/尺寸抖动 | 连续采样 8 次 | rect 恒定，排除 |
| `WindowsFormsHost` 是独立 HWND 盖住圆角 | 2.17.17 延迟挂载 | **完全不变，用户判定"白改了"** |
| 颜色是强调色混合产物 | 查注册表/主题表 | **不是**，主题里根本没有 `#F4F7FC`（深 `#090919` / 浅 `#FBF7FE`） |
| 去掉焦点联动改纯定时重绘 | 删 `Activated`/`Deactivated` | **变差**，白带回来（见 3.3） |

---

## 6. 本轮踩过的坑（血泪，务必避免）

### 6.1 ⚠ 用 `MainWindowHandle` 找主窗口会找错

`Get-Process SoulLink | Select MainWindowHandle` **返回的不是主窗口**，
而是代码里创建的那个"屏幕外热键辅助窗"（237x39 @ -32000，见 HANDOFF 4.2.5 坑三）。

**我因此得出过两个错误结论**：
- 报给用户"WindowRect == ClientRect、NC 厚度全 0" → 那是辅助窗的数据
- 一度以为 `WM_NCCALCSIZE` 没生效 → 其实一直生效（主窗口 NC=11）

**正确做法**：枚举该进程所有 `HwndWrapper` 窗口，取**可见且面积最大**的那个：

```csharp
// 排除 r.L > -10000（屏幕外的辅助窗），再按 w*h 取最大
if (r.L > -10000 && w > 800 && h > 600) { ... }
```

### 6.2 单实例互斥体会静默杀掉第二个实例

想"另开一个实例做对照"时，`Start-Process` 的新进程会**立刻退出**，
日志只有 `config loaded`，没有任何错误。
必须先关掉正在运行的那个，或者用不同程序名。

### 6.3 主窗口会被收进托盘 → 窗口"消失"

跑截图脚本时若窗口被最小化/隐藏，`EnumWindows` 找不到它，
表现为 "NO MAIN WINDOW"。不是崩溃。脚本里要先 `ShowWindow(h, 9)`。

### 6.4 截图前必须确认焦点归属

`SetForegroundWindow` 可能失败（前台锁），截出来的是别的窗口（本轮截到过自己的对话框）。
脚本里要打 `fg=True/False`，**别假设置前成功**。

### 6.5 看到"问题消失"先确认不是被盖住了

3.4 就是典型案例：白带消失是因为被 10px 粗边框压住，不是修好。
**任何"变好了"都要用像素数据 + 肉眼截图双向确认。**

### 6.6 PowerShell 里 `$Pid` 是只读自动变量

脚本参数不能叫 `-Pid`，会报"无法覆盖变量 Pid"。改用 `-ProcId` / `-TargetPid`。

### 6.7 PowerShell 5.1 的 `Add-Type` 引用 `System.Drawing` 会编译失败

`System.Drawing.Common` 转发问题。**改用 `[System.Drawing.Bitmap]::new()` 直接调**，
不要在 C# 里 `new Bitmap()`。

### 6.8 会话中途擅自加实验会浪费用户时间

用户明确说过："你按我的步骤来，听我指挥，你的实验麻烦而且无效。"
**先问、再做；用户给了方向就严格按方向做，不要自己另开实验。**

---

## 7. 回退方法

用户明确要求"先不回退，继续尝试修复"。若之后要回退：

| 目标 | 操作 |
|---|---|
| **回到 2.17.17（本轮改动全部撤销）** | `git checkout -- Controls/Backdrop.cs Views/MainWindow.xaml.cs` 再 `dotnet publish` |
| 回到 2.17.16 定版 | 源码用 `E:\SoulLink-SOURCE-ARCHIVE\v2.17.16-reliable\`（**会同时丢掉 2.17.17 的 6 个修复和档位功能**） |
| 只撤销 2.2 那处 | 把 `WndProc` 的 `WM_NCCALCSIZE` 分支改回收缩 11px 的写法（**不建议**，会出粗边框） |

---

## 8. 未完成 / 待确认

1. **用户肉眼确认**：三种焦点态、三档画质是否都干净（我只做了像素验证）
2. **高/中/低三档各跑一遍**确认没有新异常
3. 版本号未升：`SoulLink.csproj` 里仍是 `<Version>2.17.17</Version>`
4. 2.17.17 遗留的实验残留**仍在**（上一份交接第 4 节那份清单）：
   - `Views/MainWindow.xaml.cs`：`MAIN_ACT`/`MAIN_STATE`/`MAIN_DEACT` 埋点
   - `Services/WindowCorners.cs`：`ResetBorderColor` 用 `ColorNone`
   - `Services/AccountStateCenter.cs`：`REFRESH_TIME` 计时日志
   - `Services/InputTrace.cs`：45 处埋点（已默认关闭）
5. **用户提出过"取消画质档位"**，但随后改为"先不回退，继续尝试修复"。
   若之后要做，注意：**档位是 2.17.17 的 CPU 修复带来的，不是本轮新增**，
   摘掉档位等于回退 2.17.17 的 CPU 优化，单核占用会回到 ~100%。

---

## 9. 工具（本轮新增，下次可复用）

| 文件 | 用途 |
|---|---|
| `tools/verify2.ps1` | 启动独立实例 → 聚焦/失焦/再聚焦三次截图，自动关闭自己启动的进程 |
| `tools/sizecheck.ps1` | 在大/小两种窗口尺寸下截图，验证白带厚度是否随尺寸变化 |
| `tools/seq/*.py` | 逐像素扫描四边、对比多次截图、测量白带厚度 |
| `tools/ncprobe.ps1` | 读窗口/客户区几何、NC 厚度、DWM 属性（**注意 6.1 的坑**） |

**这些脚本都只在 `tools/` 下（已 gitignore），不进版本库。**

---

## 10. 同日后续补充：任务栏 / 应用交替点击仍闪框（2026-11-20 14:18）

**尚未消除，不能定版。** 旧截图 `tools/seq/f0000-A.png` 是启动首帧，不是焦点切换帧，不能用来证明切换时全白。

### 实际验证

- 使用主窗口的真实 HWND，连续 8 轮真实鼠标点击任务栏空白处 → 应用，共 64 帧。
- 原版本首帧仍能见到内侧约 8px 的 `#B4B4B4` / `#F4F7FC` 带，不是单纯的 2px 粉色系统边。
- 主窗口 `WindowRect` 与 `ClientRect` 都是 `1613x1076`，四边 NC 厚度为 0；配置哈希全程未变。
- 本机 .NET 8 WPF 反编译确认：`HwndWrapper` 窗口类使用 `NULL_BRUSH`，`HwndTarget.BackgroundColor` 默认是系统窗口色；这支持“边带是未覆盖区域”的判断，但不能单凭颜色坐实绘制者。

### 本次最终仅保留

`MainWindow.UpdateBorderColor()` 固定设置系统强调色；`OnActivated` / `OnDeactivated` 不再修改 DWM 边框属性。创建窗口、系统强调色变化时仍会设置。`Backdrop` 的 `Activated` / `Deactivated` 订阅保留，未删除。

当前 `dist/SoulLink.exe` SHA256 = `2E332B4F588DDD8B77BC436EBDBB69F92DD98D5B5756F0062F07848878A7EC27`；版本字段仍为 2.17.17，未升级、未定版。`dist/stable/` 未改动。

### 无效 / 未交付的尝试

- 焦点事件显式 `InvalidateVisual()`：能把边带推到下一帧消失，但不能消除首帧闪带，已撤回。
- `WM_NCCALCSIZE` 返回 `WVR_VALIDRECTS`：无效，已撤回。
- 把 `rgrc0` 重写为 `rgrc1`：无效，已撤回。
- 对运行中窗口临时去掉 `WS_THICKFRAME`：64 帧未见内侧灰/白带，说明缩放窗口路径高度相关；但拖拽探针未得到可信的缩放通过结果，**未写入源码，也未作为修复交付**。后续不要直接据此删缩放功能。

### 下一步的最小排查点

读取 `HwndSource.RootVisual` 的实际 `RenderSize`、`HwndTarget` 的客户矩形与 `WS_THICKFRAME` 下的有效区域，区分“根视觉尺寸不足”和“原生残留边带”。不要用更多定时/焦点重绘作为根除方案。
