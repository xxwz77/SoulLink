# SoulLink 版本管理与回退

> **2026-11-20 16:55 新版本 2.17.26（待定版）：快捷动作的四个按钮也能区分了。**
> 交接文档：`docs/HANDOFF-2.17.26-20261120.md`
> 当前 `dist/SoulLink.exe` = **2.17.26.0** · 67,478,037 字节
> · SHA256 `9172A638F664D1CC1964E5AD7A9F99878050FD8F4B7C8E8145D46BB32B74FD09`
> 原来四个按钮全是同一个灰色 `IconButton`，而且「定时恢复」和「立即执行」**都用 ▶**。
> 现在：定时开关=胶囊带文字（定时生效/定时已停，青绿/灰）；
> 立即执行=胶囊+⚡+「执行」；编辑中性、删除偏红。XAML 启动实测无异常。

> **2026-11-20 16:50 版本 2.17.25（待定版）：新建即自动跑 + 自动开关/手动触发分离。**
> 交接文档：`docs/HANDOFF-2.17.25-20261120.md`
> 当前 `dist/SoulLink.exe` = **2.17.25.0** · 67,477,881 字节
> · SHA256 `15B839212185958F6003FCF2D562DC7577A9DD3F4FCE63C9B3CC6A17AB8C9924`
> 新建流程显式 `Enabled=true/Paused=false`，并默认种一个「定时·间隔 5 秒」触点
> （不用「会话在线」：它依赖会话快照，账号没登录时恒 false，新建后仍不动）。
> 两个按钮改成形状/颜色/文字都不同的胶囊：**自动运行开关**（绿=开/灰=停，持续状态）
> vs **运行一次**（闪电图标，一次性）。新增 `PillButton` 样式，
> 因为 IconButton 的 hover 会覆盖状态色。真机实测重启后 RunCount 继续增长。

> **2026-11-20 16:45 版本 2.17.24（待定版）：状态灯四色语义 + 修「失败一次后永久卡死」。**
> 交接文档：`docs/HANDOFF-2.17.24-20261120.md`
> 当前 `dist/SoulLink.exe` = **2.17.24.0** · 67,477,418 字节
> · SHA256 `535D00ADF206E8D6BA0B3221900DF52C57FD7220116A28F7B7B1C91C9601ABE0`
> 用户指定的灯色语义：**蓝=等待 / 黄=暂停 / 绿=执行成功 / 红=执行失败**。
> 新增持久化字段 `LastRunOk/LastRunError/LastRunAction/LastActionResultAt`，每次动作执行后落盘。
> ★ 顺带修掉真 bug：失败结果写进 `ActionResults` 后，
> `(manual || !previous || !ContainsKey)` 三条件全假 → 动作**再也不会执行**；
> 而清理失败结果只在触点断开时触发，间隔触点几乎一直为真 → **永久卡死**。
> 修法：失败不阻挡重试，成功仍走上沿。真机实测：间隔流程恢复执行，灯转为绿色。

> **2026-11-20 16:40 版本 2.17.22（待定版）：修「状态灯不亮 / 新建后要手点」的显示与提示。**
> 交接文档：`docs/HANDOFF-2.17.22-20261120.md`
> 当前 `dist/SoulLink.exe` = **2.17.22.0** · 67,476,872 字节
> · SHA256 `6A24699E77933D66EA185DE3F1324A0758C845993219A604691F72C05C130EDE`
> 结论：**执行引擎没坏**。灯原来在「仅管理挂机账号」开启时无条件变灰（本机 True，
> 流程账号是主用户 lummy → excluded），且采样的触点窗口只有 2 秒 / 界面 3 秒刷一次；
> 「新建要手点」是因为新建流程默认连的是**「手动」触点**（设计如此，但界面没说）。
> 修法：灯改为反映"是否会自动执行"（青绿=会自动跑 / 青色=必须手点 / 琥珀=今日已完成），
> 摘要给出手动触发提示。**引擎代码（PlcScanEngine/FlowRunner/ScheduleRunner）未改。**
> **重启保留已实测**：两个间隔流程 RunCount 415→418，Enabled/Paused 原样。

> **2026-11-20 16:25 版本 2.17.21（待定版）：修「模拟按键根本没发出去」。**
> 交接文档：`docs/HANDOFF-2.17.21-20261120.md`
> 当前 `dist/SoulLink.exe` = **2.17.21.0** · 67,476,732 字节
> · SHA256 `672A95264DEA67DC161FEE99E238366205E271FB635EC7FA04162CE668788BAB`
> 根因：`RelayAgent` 的 `INPUT` 联合体只有 `KEYBDINPUT`（24），算出 cbSize=32，
> 而 Windows 的 `sizeof(INPUT)`=40（联合体按最大成员 `MOUSEINPUT` 算）→
> `SendInput` 返回 0 / ERROR_INVALID_PARAMETER(87)，**一个键都没注入**；
> 且调用方丢弃返回值，代理照样回「按键完成」。
> 修法：补齐联合体成员 + 检查 SendInput 返回值 + 失败原因冒到弹窗。
> **★ 注意：注入代理是独立安装的 `C:\Program Files\SoulLinkDev\SoulLink.Agent.exe`，
> 只换 `dist\SoulLink.exe` 不会更新它，必须同步更新并重启 Broker 服务。**
> 上一版 2.17.20 已内置手动锁存修复，见 `docs/HANDOFF-2.17.20-20261120.md`。

> **2026-11-20 16:10 版本 2.17.20（待定版）：修「手动运行只能跑一次」。**
> 交接文档：`docs/HANDOFF-2.17.20-20261120.md`
> 当前 `dist/SoulLink.exe` = **2.17.20.0** · 67,476,450 字节
> · SHA256 `B8FF54A1D1BD8E3AACF38D2A99974026502F5799005A81FA64ACB9B0DF0230A0`
> 根因：`TrigManual` 被当成跨扫描瞬时信号写进 `SatisfiedContacts`，而手动触点永远不会"断开"
> → 动作节点恒为"上次结果仍在"，再点 ▶ 只重复报上一次的失败（实测 02:28 的旧失败被反复显示）。
> 修法：手动触发不参与锁存；手动运行强制重新执行动作。
> 上一版 2.17.19 备份于 `dist/SoulLink-2.17.19.exe`；**可信归档仍是 `v2.17.19-reliable`**。

> **2026-11-20 16:00 定版（历史）：2.17.19 为可信版本 / 恢复节点。**
> 交接文档：`docs/HANDOFF-2.17.19-20261120.md`
> **固定归档：`E:\SoulLink-SOURCE-ARCHIVE\v2.17.19-reliable\`**（source + docs + 成品 + VERSION.txt + SHA256.txt + restore.ps1）
> 当前 `dist/SoulLink.exe` = **2.17.19.0**
> · 大小 67,476,425 字节
> · SHA256 `6BBEB9E1BB2FBCAFFFC61D8A10DB19CABA36515864999746E78AD4EB929DB723`
> 本轮修的：2.17.17 引入的「无法创建 RDP 控件」回归（延迟挂载 + Collapsed 时
> `Control.CreateControl()` 是空操作 → OCX 恒为 null）。修法：**先挂视觉树并置 Visible，再创建控件**；
> 系统 ActiveX 组件本身从未缺失（v9/v10/v11 三个 CLSID 一直已注册）。
> 回归版保留于 `dist/SoulLink-2.17.17-before-ocxfix.exe`（148,640,904 字节，发布时漏了压缩参数）。
> **本版已实测**：反射探针 + 真实启动，RDP 画面恢复、注入通道投送成功（证据见交接第 5 节）。
> ⚠ 2.17.18 的「窗口内侧白带」**仍未定版**（根因未坐实，本版未改），见 `docs/HANDOFF-2.17.18-20261120.md` 第 4 节。
> 回退：把归档里的 `SoulLink-2.17.19.exe` 复制回 `dist/SoulLink.exe`，或直接跑归档里的 `restore.ps1`。

> **2026-11-20 更新（历史）：2.17.17 已修 6 个问题，但尚未定版。**
> 交接文档：`docs/HANDOFF-2.17.17-20261120.md`（**必读第 3 节**）
> 当前 `dist/SoulLink.exe` = 2.17.17.0 · `A49B2C5AFAA6468F77C60B70A996A5656846BD5AEF739EE64CD653A448787017`
> **★ 仍未解决 1 个问题**：窗口内侧灰/白色粗框（本节汇报的修法被用户判定为错误，需重做）。
>
> 上一固定版仍为 2.17.16.0，归档 `E:\SoulLink-SOURCE-ARCHIVE\v2.17.16-reliable\`
> 成品 SHA256：`7C16F7B3FBBD44CC0A5D7A7EA8FC2F8AA519C9CFD8AE962F0E35FF398D094DAC`
> 挂机版（`dist/stable/`）全程**未改动**，仍为 2.17.4.0 `C83BDD34...`。

---

> 2026-11-19 补充（历史）：当时标准版固定为“2.11.0 · 梦幻星海主题基线”。
> 详见 [固定版本交接](docs/FIXED-v2.11.0-DREAMSCAPE-20261119.md)。
> 本轮未修改挂机版，未覆盖旧可靠归档，未创建新 Git 提交。
> **RDP 保护层遮挡尚未修复，两次透明实验失败且已撤销。**
> 下方旧版本记录保留，用于追溯历史，不应覆盖本次交接的最新状态。

## 当前固定版本

- **版本：2.11.0**，固定归档：`releases/v2.11.0-reliable/`（2026-11-19 01:00）。
- 内容：**新应用图标**（粉紫渐变圆角方块 + 显示器 + 云朵气泡，源图 1024³）。
  导出 10 个尺寸（16/20/24/32/40/48/64/96/128/256）以 PNG 内嵌。
  exe 图标、任务栏、窗口标题栏、托盘全部统一到同一张图——
  托盘此前是代码手绘的紫青圆环，和 exe 图标不一致，现改为从
  `Assets/soullink.ico` 加载，读不到才回退手绘。
- 本版只改图标与版本号，**未改动任何业务逻辑**；2.10.2 的功能结论继续有效。
- `dist/SoulLink.exe` 与此固定版本一致；`%ProgramFiles%\SoulLinkDev\SoulLink.exe` 同哈希。
- **`dist/stable/` 同步更新为 2.11.0**（用户授权）：`SoulLink-Stable.exe` 与测试版同哈希，
  文件名决定身份 `SoulLink-Stable`，配置目录/互斥体/管道/任务名完全隔离（已校验）。
  旧版保留 `SoulLink-Stable-2.10.2.exe`；备份 `releases/stable-backup-20261119-005008/`。
- 挂机版更新包：`releases/SoulLink-v2.11.0-Stable-Update.zip`（115.8 MB，含更新说明与 SHA256）。
  包内主程序名为 `SoulLink.exe`，部署时须改名为 `SoulLink-Stable.exe`（文件名即身份）。
- exe 大小 67,180,553 字节；SHA256 `78D8389990131C76…`（完整值见归档 SHA256.txt）。
  完整记录见归档内 `VERSION.txt`。后续改动必须另升版本，不覆盖此归档。
- **使用注意**：热键要求 RDP 画面控件有焦点，**按侧键前先点一下画面**。
- 图标生成工具：`tools/layout-check/IconBuilder.cs`（源 PNG → 多尺寸 ICO）。
- 上一固定版本 2.10.2 / 2.10.1 / 2.10.0 / 2.9.x / 2.8.x / 2.6.2 归档均保持不变。
- 跑回归测试前需先关闭正在运行的客户端，否则手动模式占用同名管道会互相干扰。
- 已知未解决：**A3（会话 7）侧键尚未复验**。
## 当前稳定基线

- 分支：`main`
- 基线提交：`bac46e8 SoulLink 稳定基线：多用户 RDP 中控台与自动化管理`
- 位置：`E:\Documents\deepseek-harness\SoulLink` 的本地 Git 仓库。

提交包含源码、XAML、文档和验证截图；不包含构建产物、测试工具、日志或本地凭据配置。

## 日常提交

每次修改并实机验证后再提交：

```powershell
git add --all
git commit -m "说明本次修改"
```

不要只凭编译通过提交稳定版本。HANDOFF.md 中应同时记录实测结论与未验证限制。

## 改出问题后回退

### 1. 查看最近提交

```powershell
git log --oneline --decorate -10
```

### 2. 丢弃尚未提交的源码修改，回到最近提交

```powershell
git restore --source=HEAD --staged --worktree -- .
```

这会覆盖已跟踪源码，请先把需要的改动另存为提交或补丁。`dist` 和 `tools` 被忽略，不会被此命令删除。

### 3. 回到稳定基线

```powershell
git switch --detach bac46e8
```

这会让仓库进入 detached HEAD，只读查看旧版本。若要继续开发，建议创建修复分支：

```powershell
git switch -c repair-from-baseline
```

### 4. 只回退某个文件

```powershell
git restore --source=bac46e8 -- Views/ConsolePage.xaml.cs
```

### 5. 撤销已经提交的错误提交，保留修改历史

```powershell
git revert <错误的提交哈希>
```

`revert` 创建反向提交，适合已经分享的提交；不要对 shared/public 提交使用 reset。

### 6. 仅查看旧版本，不修改当前工作区

```powershell
git show bac46e8:Views/ConsolePage.xaml.cs
```

## 发布与安全

- `dist\SoulLink.exe` 不纳入 Git，回退源码后需重新 `dotnet publish`。
- 本地 `%LOCALAPPDATA%\SoulLink\config.json` 不受 Git 管理，**不会因为回退源码而自动回退配置**。
- 配置和 DPAPI 凭据的备份必须在实际修改前单独进行，不能依赖 Git。
- 禁止使用 `git clean -fd` 作为常规回退命令，避免误删测试/本地文件。
