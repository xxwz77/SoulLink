# SoulLink 交接文档 · 2.17.19（RDP 控件创建失败修复）

> 固定时间：2026-11-20 15:55（**16:00 用户确认定版，见第 9 节**）
> 版本号：**2.17.19.0**（csproj 已升级；`dist/SoulLink.exe` 已替换并实测）
> 性质：修复 **2.17.17 引入的回归 —— RDP 画面完全打不开**
> 结论：根因单一且已坐实，修复后**本机实测 RDP 画面恢复**（见第 5 节）
> 状态：**已定版 = 当前可信版本**，固定归档 `E:\SoulLink-SOURCE-ARCHIVE\v2.17.19-reliable\`

---

## 0. 一句话现状

用户在控制台页看到的红字「无法创建 RDP 控件。系统可能缺少远程桌面 ActiveX 组件。
控件创建后 OCX 为空（{A0C63C30…}）」不是系统缺组件，
而是 **2.17.17 的"延迟挂载 WindowsFormsHost"让 CreateControl() 变成了空操作**。
现已把"挂进视觉树 + 可见"提前到创建之前，RDP 恢复正常；系统侧一切正常（v9/v10/v11 三个 CLSID 均已注册）。

---

## 1. 现象

- 控制台页 RDP 区域全黑 + 红字：`无法创建 RDP 控件。系统可能缺少远程桌面 ActiveX 组件。`
  第二行固定是最后一个候选 `{A0C63C30-F08D-4AB4-907C-34905D770C7D}`（v11）。
- `rdp-debug-*.log` 每 2 秒一条 `自动连接失败：<账号> → 无法创建 RDP 控件…`
  （2026-11-20 14:54:32 起至 15:43，共 2253 条；error.log 累计 9109 条 `RdpConsole.EnsureHost`）。
- 系统侧无问题：`HKCR\CLSID\{A3BC03A0…}`（v9）、`{8B918B82…}`（v10）、
  `{A0C63C30…}`（v11）**全部已注册**，InprocServer32 → `C:\WINDOWS\system32\mstscax.dll`，
  mstscax.dll 10.0.29698.1000 存在；用独立进程直接 `Activator.CreateInstance` 和
  AxHost 创建三个 CLSID **全部成功**。→ 排除"缺组件"。

---

## 2. 根因

`Controls/RdpConsole.cs` 2.17.17 的改动（2026-11-20 12:36）：

1. 构造函数里 `_host.Visibility = Visibility.Collapsed`；
2. `EnsureHost()` 先 `_host.Child = ax; ax.EnsureCreated();`，
   只有**创建成功之后**才 `Children.Add(_host); _host.Visibility = Visible;`。

问题在 `Control.CreateControl()`（`RdpAxHost.EnsureCreated()` 内部调用）：
**控件不可见时它是空操作**。此时 `_host` 既不在视觉树里、又是 Collapsed，
于是 `AxHost` 的窗口根本不会创建 → `AttachInterfaces()` 不被调用 → `GetOcx()` 恒为 null
→ 三个候选全部"控件创建后 OCX 为空"，**而且一个异常都不抛**（所以日志里只有那句 OCX 为空）。

时间线也对得上：`RdpConsole.cs` 12:36 改；12:48 之前运行的是改动前的 2.17.18 版（正常）；
14:54 首次出现该错误；15:27 的构建（`dist/SoulLink.exe`，148,640,904 字节）就是带这个回归的版本。

---

## 3. 决定性证据（同一台机器、同一 .NET 8、同一 CLSID 的 A/B）

| # | 条件 | 结果 |
|---|---|---|
| A | `WindowsFormsHost` 未挂进视觉树 + `Visibility=Visible` | **OCX 正常**，AttachInterfaces 被调用 |
| B | `WindowsFormsHost` 未挂进视觉树 + `Visibility=Collapsed` | **OCX 为空，且不抛异常**（= 线上症状） |
| C | `WindowsFormsHost` 已挂窗口 + `Collapsed` | OCX 正常（但要求外层已在窗口里） |
| D | `WindowsFormsHost` 已挂窗口 + `Visible` | OCX 正常 |

用**真实 `SoulLink.dll` 的 `RdpConsole`** 做对照（反射调用私有 `EnsureHost()`，
控制台对象**完全不挂进任何窗口**——比线上更苛刻）：
修复前走的就是 B（OCX 为空）；修复后 **PASS**，`_ax=RdpAxHost`、`_ocx=System.__ComObject`。

---

## 4. 修复（只改 `Controls/RdpConsole.cs` 的 `EnsureHost` 及其三个小助手）

```csharp
MountHostForCreation();   // ① 先挂进视觉树 + 置 Visible + UpdateLayout
_host.Child = ax;         // ② 再挂控件
ax.EnsureCreated();       // ③ 这时 CreateControl() 才会真正创建 ActiveX
```

另外三处顺带修正（都与本 bug 直接相关）：

1. **失败候选要清理**：原来 `continue` 前既不摘 `_host.Child` 也不 Dispose，
   每次重试泄漏 3 个 AxHost（监控每 2 秒重试一次）。新增 `DetachControl(ax)`。
2. **失败后收回宿主**：新增 `UnmountHostAfterFailure()`，恢复"未挂载 + Collapsed"，
   保住 2.17.17 想要的"未连接的 Console 不在窗口上露方形 HWND"。
3. **三个候选的错误全带上**：原来只保留最后一条，v9/v10 的真实 HRESULT 被 v11 的
   "OCX 为空"覆盖，排查时看不出是"没注册类"还是"不可见"。现在异常里逐条列出 CLSID + HRESULT。

**为什么保留"延迟挂载"而不是回退成构造时挂载**：
2.17.18 交接第 5 节已用像素实测证明"延迟挂载对内侧白带完全无效"，
但它**也没有害处**（修好后是"连接时才挂载"）；回退成构造时挂载则需要重新验证白带。
若以后要删，删掉时**同样必须保证"先可见再 CreateControl"**。

---

## 5. 验证（2.17.19 实测）

1. 编译 + 单文件发布 → `dist/SoulLink.exe`（见第 6 节哈希）。
2. 反射探针（真实 RdpConsole，未挂窗口 + Collapsed）：`RESULT: PASS — OCX 创建成功`，`ocx.Connected = 0`。
3. 真实启动 2.17.19（旧实例已关闭），`rdp-debug` 新增日志：

```
15:47:44.084  [Console] 会话监控：「山客」会话不可用（Disc），主动发起 RDP 连接
15:47:46.304  连接后真实会话号=4
15:47:46.433  [Console] 自动连接成功：山客（会话 4）
15:47:47.939  连接后真实会话号=5
15:47:48.127  [Console] 自动连接成功：玄月（会话 5）
```

   此后 `error.log` **不再新增任何 `RdpConsole.EnsureHost` 记录**（最后一条是 15:43:05 的旧版）。
4. 界面截图（标题栏 v2.17.19，双列布局，画面内是真实远端桌面）：
   `artifacts/rdp-fix-verify.png`。

---

## 6. 产物

| 项目 | 值 |
|---|---|
| 版本 | **2.17.19.0**（`SoulLink.csproj` 已从 2.17.17 升上来） |
| `dist/SoulLink.exe` | 67,476,425 字节 |
| SHA256 | `6BBEB9E1BB2FBCAFFFC61D8A10DB19CABA36515864999746E78AD4EB929DB723` |
| 构建方式 | `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none`（与 `build.bat` 一致） |
| 被替换的回归版 | `dist/SoulLink-2.17.17-before-ocxfix.exe`（148,640,904 字节，2.17.17.0，**发布时漏了压缩参数**，所以体积是两倍） |
| 挂机版 `dist/stable` | **未改动** |
| 固定归档（恢复节点） | `E:\SoulLink-SOURCE-ARCHIVE\v2.17.19-reliable\`（200 个文件 / 107,643,070 字节，见第 9 节） |

---

## 7. 未涉及（与本次无关，别当成没修好）

1. **注入代理投送失败**：`Broker 服务已安装但未运行（状态：Stopped）`
   —— 2.17.18 交接已记录的既有状态，与 RDP 画面无关；启动/修复 Broker 服务后即可投送。
2. **窗口内侧灰/白粗框**：2.17.18 未定版问题，本次**没有碰**（`Backdrop.cs`/`MainWindow.xaml.cs` 均未改）。

---

## 8. 回退方法

| 目标 | 操作 |
|---|---|
| 撤销本次修复 | `git checkout -- Controls/RdpConsole.cs`，版本号改回 2.17.17，重新 publish |
| 用旧 exe 顶一下 | 把 `dist/SoulLink-2.17.17-before-ocxfix.exe` 改名为 `SoulLink.exe`（**别这么做**，它打不开画面） |

**注意**：`build.bat` 里有 `rmdir /s /q dist`，会把 `dist` 下所有历史 exe 一起删掉。
本次是按 `build.bat` 的参数直接 publish 到临时目录再替换单文件，没有清 `dist`。
---

## 9. 定版与恢复节点（2026-11-20 16:00）

用户确认「将本版本视为可信版本，创建恢复节点」，本版**已定版**。
沿用仓库既有的固定版归档约定（与 `v2.17.16-reliable\` 同结构）：

```
E:\SoulLink-SOURCE-ARCHIVE\v2.17.19-reliable\
  source\      客户端可编译子集（Assets/Controls/Models/Relay/Services/Theme/Views +
               app.manifest / App.xaml / App.xaml.cs / SoulLink.csproj）170 个文件
  docs\        docs\*.md + docs\*.txt + 根目录 5 个说明文档（含本文件）22 个文件
  tools\       SoulLink.BrokerSetup.exe
  SoulLink-2.17.19.exe   67,476,425 字节 · SHA256 6BBEB9E1…DB723
  7za.exe / broker-install.ps1 / broker-uninstall.ps1
  restore.ps1  一键回填 dist\SoulLink.exe（先校验归档 exe 哈希，再关实例、复制、复核）
  VERSION.txt  本版说明（根因、验证证据、未解决项、构建与回退命令、教训）
  SHA256.txt   除自身外全部文件的 SHA256（大写 + 两空格 + 相对路径，共 199 行）
```

完整性校验（归档创建后立即执行）：

| 校验项 | 结果 |
|---|---|
| `SHA256.txt` 逐条复核 | **199/199 通过，0 不一致** |
| `source\` 与仓库逐文件比对 | 170/170 哈希一致 |
| `docs\` 与仓库逐文件比对 | 22/22 哈希一致 |
| 归档 exe 与 `dist/SoulLink.exe` | 哈希一致 `6BBEB9E1…DB723` |

回退到本版：双击/运行 `E:\SoulLink-SOURCE-ARCHIVE\v2.17.19-reliable\restore.ps1`，
或手工把 `SoulLink-2.17.19.exe` 复制成 `<仓库>\dist\SoulLink.exe` 并核对哈希。

**同时更新过的说明文档**（都已进入归档快照）：
`VERSIONING.md`（定版声明 + 归档路径）、`NEW_SESSION_PROMPT.md`（基线改为 2.17.19 +
新增 `CreateControl()` 不可见即空操作的教训 + `build.bat` 删 dist 的警告）。

**未做的**：没有创建 git 提交/标签（沿用本轮之前"不提交"的做法，
工作区仍有大量未提交改动，HEAD 停在 `fddb788`/2.6.1）。
`dist/stable/` 未改动。

