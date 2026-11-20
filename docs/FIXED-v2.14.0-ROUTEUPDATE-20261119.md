# 交接文档 · SoulLink 2.14.0 · 路线更新器（新增功能）

> 固定日期：2026-11-19
> 版本号：**2.14.0.0**
> 主题：**放弃 2.13 失败代码，源码整体回到 2.12.0 基线；在基线上新增「路线更新」功能**

---

## 0. 一句话结论

2.13 的 RDP 放大/镜像实验**全部作废**，源码已回到用户真机验证过的 2.12.0，
并在其之上新增独立的**路线更新器**：用户指定路线根目录 → 选路线包 →
核对包内第一层文件夹 → **只删除目标里同名的文件夹** → 把包内文件夹放进去。

---

## 1. 成品与存档

| 项目 | 值 |
|---|---|
| 标准版 | `dist/SoulLink.exe` |
| 版本号 | 2.14.0.0 |
| 大小 | 67,206,401 字节 |
| SHA256 | `C6476E6FA9F24A9274721C5D1C2510CD873FF543B408D33FDDC73D42E6D8D9F8` |
| 发布暂存 | `artifacts/pub-routeupdate-2.14.0/`（与 dist 同哈希） |
| 源码存档 | `E:\SoulLink-SOURCE-ARCHIVE\v2.12.0\source\`（2.12 基线，未动） |
| 2.13 现场备份 | `E:\SoulLink-SOURCE-ARCHIVE\20261119-225525-before-2.14-routeupdate\` |
| 替换前 dist 备份 | `E:\SoulLink-SOURCE-ARCHIVE\20261119-230241-before-2.14-dist\SoulLink-2.13.0-failed.exe` |
| 挂机版 `dist/stable/` | **未改**，`06A742A0…` |

> ⚠️ 用户仍在运行的 **PID 59136**（`C:\Program Files\SoulLinkDev\SoulLink.exe`，2.11.0）
> 全程未干预，替换 `dist/SoulLink.exe` 时该文件未被占用（实测可写）。

---

## 2. 基线恢复（做了什么）

用 `E:\SoulLink-SOURCE-ARCHIVE\v2.12.0\source\` 覆盖，**逐一核对 SHA256 = IDENTICAL**：

| 文件 | 状态 |
|---|---|
| `Controls/RdpConsole.cs` | 已还原（141,977 B） |
| `Views/ConsolePage.xaml.cs` | 已还原（126,781 B） |
| `Views/ConsolePage.Tiles.cs` | 已还原（37,037 B） |
| `Views/ConsolePage.xaml` | 已还原（34,936 B） |
| `Models/Models.cs` | 已还原 + 新增 1 个设置项 |
| `Services/NativeDispatch.cs` | 已还原（22,384 B） |
| `Controls/RdpGeneralSettings.xaml(.cs)` | 已还原 |
| `SoulLink.csproj` | 已还原 + 版本号 2.14.0 |

**2.13 独有文件已移出源码树**（留在上表备份目录，不在仓库内）：

- `Controls/RdpConsole.Zoom.cs`
- `Controls/RdpMirrorOverlay.cs`
- `Services/LocalZoomGeometry.cs`
- `Services/RdpLocalZoom.cs`

> ★ 为什么必须移出而不是只还原调用方：csproj 用通配符编译，这些文件留在树里
> 仍会被编译，`RdpConsole.Zoom.cs` 是 `partial RdpConsole`，会继续往主类型里塞
> 镜像/放大成员，等于 2.13 没走。移出后 `RdpConsole.cs` 里对
> `_mirror` / `ApplyLocalZoom` / `ScheduleMirrorRefresh` 的引用一并消失
> （2.12 基线本就没有这些调用，已核对 2.12 存档无一处引用）。

**恢复后基线编译：0 错误 / 110 警告**（警告数与 2.12 交接记录一致，均为既有）。

---

## 3. 新功能：路线更新

### 3.1 用户可见行为

左侧导航 → **路线更新**（在「更新 BGI」下面）。

1. **路线根目录**（用户指定，会记住）：例如
   `D:\AAA挂机专区\A2\BetterGI\User\AutoPathing`
2. **路线包**：zip / 7z / rar / tar / gz
3. 选包后自动解压到临时目录**核对一次**，列出包内第一层文件夹，
   并标注「替换同名」还是「新增」
4. 点「开始更新」：

| 步骤 | 行为 |
|---|---|
| ② 核对 | 只看**第一层文件夹**；第一层散落文件不处理，日志里明确列出 |
| ③ 删除 | 目标根目录里**只有同名文件夹**被移入备份目录；其它文件夹和根目录文件一律不动 |
| ④ 放入 | 把包内第一层文件夹原样复制进去 |

### 3.2 实测结果（用真实编译产物跑，非静态推断）

构造「包内 = CD采集路线 + 全新路线，包内另有 1 个散落文件；
目标 = 旧的 CD采集路线 + 无关路线 + 根目录散落文件」：

```
将要替换（同名）：CD采集路线
将要新增：全新路线
其余文件夹与文件不会改动

③ 已移入备份：CD采集路线
④ ✓ CD采集路线（2 个文件）
   ✓ 全新路线（1 个文件）
更新完成：2 个文件夹，共 3 个文件
```

更新后实测目录：

| 路径 | 内容 | 结论 |
|---|---|---|
| `CD采集路线\12小时狗粮\a.json` | `NEW-12` | ✅ 旧内容被替换 |
| `CD采集路线\旧子目录\old.json` | — | ✅ 已随同名文件夹删除 |
| `全新路线\子目录\c.json` | `ADDED` | ✅ 新增正确 |
| `无关路线\keep.json` | `KEEP` | ✅ 未被动 |
| `根目录散落.json` | `ROOTFILE` | ✅ 未被删 |

**失败路径也实测过**：

| 用例 | 结果 |
|---|---|
| 包内只有散落文件、没有第一层文件夹 | 失败并给出说明；**目标目录零改动**（实测 `muststay.json` 仍在） |
| 目标目录不存在 | 直接报「目录不存在」，不执行任何删除 |
| 目标填成 BGI 安装目录本身 | 拒绝，提示路线通常在其中的 `User\AutoPathing` |

### 3.3 代码落点

| 文件 | 职责 |
|---|---|
| `Services/RouteUpdater.cs`（新） | 解压、核对第一层文件夹、删除同名、复制、结果汇总 |
| `Views/RouteUpdatePage.xaml`（新） | 页面布局 |
| `Views/RouteUpdatePage.xaml.cs`（新） | 选目录 / 选包 / 预检 / 开始更新 / 日志进度 |
| `Views/MainWindow.xaml` | 导航行 `Tag="routeupdate"` + `PageRouteUpdate` 宿主 |
| `Views/MainWindow.xaml.cs` | `_pages` 注册、侧栏文字显隐、`SetActivePage` 调 `Reload()` |
| `Models/Models.cs` | `AppSettings.RouteUpdatePath`（用户指定的根目录，持久化） |
| `Services/BgiUpdater.cs` | `Find7za()` 由 `private` 改 `internal`，供路线更新器复用 |

### 3.4 关键设计决定（改代码前必读）

| # | 决定 | 原因 |
|---|---|---|
| 1 | **不剥包内包裹层**，包内第一层文件夹就是更新单位 | 早期版本试过「只有一个文件夹就往下钻一层」，实测把 `CD采集路线\12小时狗粮` 误判成包裹层，结果在目标里建出 `12小时狗粮`，而不是替换 `CD采集路线`。用户要的是「包内文件夹同名替换」，第一层必须原样使用。此坑已用真实压缩包复现并修掉 |
| 2 | 只删**同名**第一层文件夹，绝不整目录清空 | 用户原话要求；无关路线、根目录散落文件实测未被触碰 |
| 3 | 删除前先 `Directory.Move` 到 `%LOCALAPPDATA%\SoulLink\route-backups\<时间戳>\` | 移不动（跨卷/被占用）才退化成真删除，日志会写明；用户可回滚 |
| 4 | 复用 `BgiUpdater.Find7za()` | 7za 已随程序分发，重复实现会分叉 |
| 5 | 不复用 `BgiUpdater.UpdateBatchAsync` | 那是「整包铺到 BGI 根目录」的语义（连 exe/dll 一起），与「只替换同名文件夹」完全不同 |
| 6 | 预检也解压一次到临时目录 | 必须先看到真实目录结构才能判断同名/新增；解压完即删 |
| 7 | 不碰 BGI 进程 | 路线是纯数据文件夹，BGI 空闲时替换即可；停/启 BGI 仍归「更新 BGI」页 |

---

## 4. 已验证 / 未验证

### 已验证

- 2.12 基线恢复后编译 **0 错误 / 110 警告**
- 新增页面编译 **0 错误**，整体 `dotnet publish` 成功
- 路线更新核心逻辑实测（替换 / 新增 / 保留无关文件夹 / 保留根目录散落文件）
- 失败路径实测（包内无文件夹不删任何东西；目标不存在直接拒绝）
- 页面布局离屏渲染实测（导航项、输入框、核对列表、替换/新增标签、按钮位置）

### 未验证（必须真机确认）

- **用户真机点「浏览」选真实目录**、用真实路线包跑一次完整更新
- 真实大包（GB 级、上千文件）的解压耗时与进度显示
- 目标文件夹被 BGI/资源管理器占用时的失败提示是否够清楚
- 长路径（>260 字符）的路线文件
- 2.13 期间用户反馈的 RDP 四个问题（解锁后焦点、标签闪烁、自动上锁、画面越界）
  ——**本次一行 RDP 代码都没改**，这些问题回到 2.12.0 的状态，
  即「保护层本身可用，但灰边等已知遗留仍在」。

---

## 5. 与 2.13 的关系（不要回退错方向）

- 2.13 的 **ZoomLevel vtable slot 3/4 修正**（`IMsRdpExtendedSettings` 只有 5 槽、
  非 IDispatch 派生、STA 才可用）是**真结论**，但实现文件
  `Services/RdpLocalZoom.cs` 已随 2.13 一起移出源码树 —— 也就是说
  **2.14.0 不再使用 ZoomLevel**，走的是 2.12.0 的 `SmartSizing` 路径。
  将来若要重做放大，结论可复用，文件不能直接搬回来（它依赖 2.13 的
  `RdpConsole.Zoom.cs` / `LocalZoomGeometry.cs` 一整套）。
- 2.13 的 `RdpMirrorOverlay`（DWM 缩略图镜像）同样已移出，**不是**运行时禁用，
  是**代码不在树里**。要恢复需评估 HWND 重建与 0x119 风险。
- 2.12 的结论继续有效：**RDP 画面上不能盖任何 WPF 浮层**、
  **不能给 `WindowsFormsHost` 设固定 Width/Height/Alignment**。

---

## 6. 构建与部署

```powershell
dotnet publish SoulLink.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=none ^
  -o artifacts/pub-routeupdate-2.14.0 --nologo
```

替换 `dist/SoulLink.exe` 前：

1. 先确认 `dist/SoulLink.exe` 没被正在运行的进程占用
2. 备份到 `E:\SoulLink-SOURCE-ARCHIVE\<时间戳>-before-2.14-dist\`
3. 核对 `SHA256` 与 `VersionInfo.FileVersion`

> ⚠️ `app.manifest` 是 `requireAdministrator`。用 `Start-Process` 非提权起进程会
> 立即退出（exit 1）—— **这是既有行为，2.12.0 存档 exe 表现完全相同**，
> 不是本次改坏，不要照此判定「新版本启动不了」。

---

## 7. 禁止事项（沿用 + 新增）

1. 修改/回退 `dist/stable/`，除非用户明确授权
2. 操作 A1/A4；测试只用 A2/A3
3. 不按 PID + SessionId + 完整路径核对就操作任何进程
4. 往 RDP 画面上盖任何 WPF 浮层
5. 手动叠加 `WS_EX_LAYERED` / `WS_EX_TRANSPARENT`
6. 给 `WindowsFormsHost` 设固定尺寸
7. **新增**：不要把 2.13 的 `RdpConsole.Zoom.cs` / `RdpMirrorOverlay.cs` /
   `LocalZoomGeometry.cs` / `RdpLocalZoom.cs` 直接搬回源码树
8. **新增**：路线更新器**不要**改成「清空目标目录再放入」，也不要加回「剥包内包裹层」

---

## 8. 文件清单

**新增**

- `Services/RouteUpdater.cs`
- `Views/RouteUpdatePage.xaml`
- `Views/RouteUpdatePage.xaml.cs`
- `docs/FIXED-v2.14.0-ROUTEUPDATE-20261119.md`（本文）

**修改**

- `Models/Models.cs`（+ `RouteUpdatePath`，其余回到 2.12）
- `Views/MainWindow.xaml`（导航行 + 页面宿主）
- `Views/MainWindow.xaml.cs`（页面注册 / 文字显隐 / 切页调用）
- `Services/BgiUpdater.cs`（`Find7za` → `internal`）
- `SoulLink.csproj`（`<Version>2.14.0`）

**删除（已移出源码树）**

- `Controls/RdpConsole.Zoom.cs`
- `Controls/RdpMirrorOverlay.cs`
- `Services/LocalZoomGeometry.cs`
- `Services/RdpLocalZoom.cs`

**未提交 git**：本轮所有改动仍在工作区，未 `git commit`。
