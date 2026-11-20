# SoulLink 2.13 RDP 修复失败交接文档

> 结论先行：两轮修复均未解决用户问题。用户 22:26 后新版 dist（PID 193256，哈希 2773D649...）实测 + 视频 E:\Desktop\QQ20261119-222703.mp4 + 后续截图证明：解锁后焦点切换、标签全窗闪烁、自动上锁、RDP 原生画面越界覆盖主 UI 四个问题仍在。本文档只做失败交接，不进行任何修复。
>
> 生成时间：2026-11-19 22:39（Asia/Shanghai）
> 作者：recovery-owner（源码恢复负责人）
> 性质：仅记录事实与未决问题，绝不启动/停止/构建/发布程序，不改 dist、dist/stable、不改 git、不操作账号、不做额外备份。

---

## 1. 当前失败状态（用户实测证据，最高优先级）

- 用户结论：上传视频并明确表示「没有修复任何问题点」。A3 黑色桌面背景是正常远端桌面壁纸，不是黑屏故障。
- 用户仍在运行的 dist：PID 193256（第二轮源码，dist\SoulLink.exe，哈希 2773D649...，生成时间 2026-11-19 22:25:47）。保持运行供对照，禁止干预。
- 详细日志：C:\Users\91244\AppData\Local\SoulLink\rdp-debug-20261119.log、shield-trace.log。
- 视频：E:\Desktop\QQ20261119-222703.mp4（Lead 侧解码取证）。
- 用户命令：明确停止修复。

### 日志关键事实（只读 grep 自 rdp-debug-20261119.log，置信：实测原文）

| 时间 | 事件 |
|---|---|
| 22:27:09.552 | RDP 宿主窗口移回 (622,342) 后同一毫秒 保护层解锁(UpdateShield) 与 保护层锁定(UpdateShield) 先解锁后锁回 |
| 22:27:19.530 | 鼠标离开重锁范围（连续 20 次）重新上锁 caller=RearmShield（不等于 Deactivated 正确） |
| 22:28:16.520 | RDP 宿主窗口移回 (0,0) —— 宿主恢复到主窗口左上角，越出 Tile/视口客户矩形 |
| 22:32:22.343 | 键盘焦点 -> RDP 控件 ax.ContainsFocus=True hwnd=13631550（真实原生焦点未随逻辑焦点切换） |
| 22:27:10 / 22:28:19 / 22:29:48 | 多次 铺满填充镜像 + 拉伸重铺 + 紧随 镜像层已撤 反复往返 |

---

## 2. 初始根因线索与真实技术成果（保留，不回退）

### 2.1 IMsRdpExtendedSettings 仅 5 槽（真·技术成果，务必保留 slot 3/4）
- 实测（Services/RdpLocalZoom.cs 头部注释 + 源码）：IMsRdpExtendedSettings vtable 仅 5 槽：0-2 = IUnknown，3 = put_Property，4 = get_Property；非 IDispatch 派生（STA 下 QI IDispatch = E_NOINTERFACE）。
- 历史版本曾按 slot 7/8 写死，越界读到相邻接口 vtable，put 恒 0x8000FFFF。换回 slot 3/4 后 DesktopScaleFactor 立即 S_OK。
- 禁止回退 slot 7/8。当前 RdpLocalZoom.cs 使用 slot 3/4（put/get），未动。

### 2.2 历史 2.13 固定 Host 尺寸 / 1:1 失败与当前状态区分
- 1:1 模式曾通过给 _host 设固定 Width/Height + Center 居中，导致窗口缩小、WindowsFormsHost 空域窗口反复重建（hwnd 1 分钟换 4 个），是 0x119 高风险组合，已移除（ApplyHostSizing 恒 return false）。
- 当前 _host 应永远 Stretch 填满父容器，禁止重新引入固定 Host Width/Height/Alignment。

---

## 3. 第一轮 6 处精确 edit（已作废，不能称为修好）

> 第一轮任务 task_mwnikj5b173byk。编译 0 错误 / 120 warning（均既有）。当时报 done，并有自动验收通过报告，dist 哈希（第一轮）记录为 1C561455...。后被用户真实操作推翻。

| # | 文件 | 函数/位置 | 改动 | 被推翻原因 |
|---|---|---|---|---|
| 1 | ConsolePage.xaml.cs | 新增 IsExternalDeactivation() + DllImport | PID 单拍门 fgPid != rootPid 判外部失活 | 单拍判定不可靠 |
| 2 | ConsolePage.xaml.cs | Window.Deactivated | 加 if (!IsExternalDeactivation(window)) return; | 单拍不可靠 + 与 RdpConsole 独立重锁双通道矛盾 |
| 3 | ConsolePage.xaml.cs | FollowRdpFocus() | 改恰好一个画面 HasKeyboardFocus 才跟随 | 已接管标签不转移真实焦点，唯一性进一步抑制跟随 |
| 4 | RdpConsole.cs | 新增 _nativeVisible 字段 | SetNativeVisible 幂等短路 | 挡不住真实目标状态变化，不能当完整解 |
| 5 | RdpConsole.cs | SetNativeVisible(visible) | _nativeVisible 相等则 return | 同上 |
| 6 | RdpConsole.Zoom.cs | ApplyLocalZoom() | 移除无条件 TearMirror() | 镜像撤/重建往返仍在（日志多次） |

---

## 4. 第二轮 8 处精确 edit（已作废，不能称为修好）

> 第二轮任务 task_mwnlntxa3ffi2z。编译 0 错误 / 124 warning。22:26 发布 dist（哈希 2773D6491E9068E9C4946FD3C9EF971543935A93B0214AF49B7F1FD593B81142），PID 193256。用户视频证明未修复。

| # | 文件 | 函数/位置 | 改动 | 被推翻原因 |
|---|---|---|---|---|
| 1 | ConsolePage.xaml.cs | IsExternalDeactivation() | 三处 return 改 fail-closed（root=0/fg=0/异常 = true） | 未覆盖 Single tab-click 实际路径 |
| 2 | ConsolePage.xaml.cs | Window.Deactivated | async + Task.Delay(120) 沉降 | 未完整验证，与离开整窗门混同 |
| 3 | ConsolePage.xaml.cs | InitializeConsole | 订阅 c.ShieldClicked 按 console 反查 Tile | 实际路径 Single tab-click 走 ShowViewPageAsync，不经过此处 |
| 4 | ConsolePage.xaml.cs | SetFocusFromShield() | 已接管时 ForceKeyboardFocus() | 实际路径不经过 |
| 5 | ConsolePage.Tiles.cs | 新增 FindTileByConsole() | 辅助反查 | 上游订阅路径不覆盖 |
| 6 | ConsolePage.Tiles.cs | SetFocus() | 已接管时 ForceKeyboardFocus() | Single tab-click 走 ShowViewPageAsync(xaml.cs 1273-1286)，其 ApplyLayout/OnShown 无条件 SetShieldReleased(false)+UpdateShield，不经过 SetFocus |
| 7 | RdpConsole.cs | SetNativeVisible(else) | TearMirror() 改 _mirror?.Hide() 软隐藏 | 软隐藏只隐镜像但露出隐藏宿主；镜像仍每 0.5s 重铺/撤/显 |
| 8 | RdpConsole.cs | RestoreHostToLayout() | x/y < -20000 时用 HostScreenRectProvider 兜底 | 仅覆盖 < -20000 失真，(0,0) 无钳制（22:28:16 日志） |

---

## 5. 逐项失败分析（事实 / 推论 / 未验证 分开标注）

### 5.1 解锁 / 焦点切换
- 事实：ShieldClicked 此前全项目无订阅；第二轮补订阅，但真实点击路径 Single tab-click 走 ShowViewPageAsync，不经过订阅/SetFocus。
- 事实：tab-click/SetFocus 只改逻辑 _focusTile/_viewing + SetForegroundActive，未显式转真实原生焦点（第二轮 ForceFocus 未覆盖 Single 路径）。
- 推论：逻辑焦点 != 原生焦点；解锁后系统键盘焦点残留旧画面，用户点 B 但输入还在 A。
- 未验证：订阅路径对多画面（非 Single）点击是否有效未真机验证。

### 5.2 自动上锁
- 事实：RdpRelockScope=0（用户配置实测）= 宽松离开整窗；重锁门 = 2s 宽限 + 连续 20 次 x 150ms 范围外。
- 事实：22:27:19、22:32:35 日志均为连续 20 次范围外触发 RearmShield，caller 不是 Deactivated。
- 推论：Deactivated 120ms/PID 归属判定未经完整验证；外部失活与位置兜底是两条不同链路，不可混同。
- 未验证：标签时间戳/ownerActive 对重锁门影响未实测。

### 5.3 标签全窗闪烁
- 事实：Single tab-click 走 ShowViewPageAsync（完整挂载，RenderTabs 全量重建 + ApplyLayout 双跑 + 原生可见性翻转/RestoreHostToLayout）。
- 事实：日志每次标签点击后出现宿主移出/移回、保护状态翻转、约 0.5s 后双画面拉伸重铺 + 镜像撤/显。
- 事实：视频 22:26:42/46、22:27:04/07/09 每次标签均短暂整幅画面消失。
- 重要区分：A3 黑色桌面背景是正常远端壁纸，不是黑屏故障。
- 推论：闪烁由 ShowViewPageAsync 完整挂载 + 宿主移出移回 + 镜像 Hide/TearMirror/ScheduleMirrorRefresh/500ms 重铺整链副作用共同造成。
- 未验证：全黑那一拍是源 RDP 原生窗口不可见还是镜像层撤除导致，未真机逐帧证明。

### 5.4 RDP 越界覆盖主 UI / 白色矩形框
- 事实：22:28:16 日志 RDP 宿主窗口移回 (0,0)；RestoreHostToLayout 仅在 x/y < -20000 时用 Tile 兜底，(0,0) 无钳制。
- 事实：宿主/镜像未与目标 Tile 与 ViewportHost 可见矩形交集做机械钳制。
- 推论：越界覆盖右侧「点击画面接管」与边框，来自宿主恢复到错误坐标 (0,0)，非远端内容。
- 未验证（严禁编造）：用户最新交互出现的白色矩形框（图右侧/下方大白块）根因尚未完成证明。候选排查：① 原生 WindowsFormsHost/ActiveX 未覆盖的宿主背景白底；② SoulLinkRdpMirror（DWM 缩略图）顶层窗口在交互/Focus/SetForegroundActive/SetNativeVisible/尺寸重铺时露出白底。两者责任不同（WS_CHILD 裁剪 vs DWM mirror），需真机比对白框位置与目标 Tile/宿主 Rect/主窗口客户区，当前不能凭截图静态断言。

---

## 6. 已否决路线（严禁执行）

- 任何 WPF / 透明覆盖面、WS_EX_LAYERED、WS_EX_TRANSPARENT（压 RDP DirectComposition 表面会整块黑屏 / 遮挡主 UI）。
- 固定 WindowsFormsHost Width/Height/Alignment。
- MobaXterm 子进程 SetParent 大改（只借鉴窗口/输入职责分离观察，不引入）。
- 恢复同进程多 ActiveX 原型（0x119 高风险形态）。
- RIDEV_NOLEGACY。
- 透明接管面建议曾提出，已撤回，绝不可实现。
- Go / 换语言方案未采用。
- 1Remote 借鉴仅限：精确去重、500ms resizeEndTimer、最小化停止 resize、连接状态限制、登录后延迟 resize、Focus/WndProc，均未证明落地解决本问题。

---

## 7. 工具 / 团队失败教训

- hy4-preview-x 不可用。
- 全混元选型错误，后改 DeepSeek xhigh / Kimi max / GLM max / MiniMax max。
- 旧恢复 DAG 全部作废。
- 任务调度状态曾不同步（成员 currentTaskId=null 而任务 ready，dsh_team_dispatch 报 no_idle_agent）。
- 自动验收只看 enabled 状态 / 脚本点 tile 中心 / 截图，无法覆盖真实焦点与原生窗口越界——这是两轮误判修好的根本教训。

---

## 8. 当前实际修改文件与 dist/stable 哈希（实测）

### 8.1 git 改动文件（git status --short 实测）
- M Controls/RdpConsole.cs（第一轮 + 第二轮）
- ?? Controls/RdpConsole.Zoom.cs（未跟踪新文件）
- M Views/ConsolePage.xaml.cs（第一轮 + 第二轮）
- M Views/ConsolePage.Tiles.cs（第二轮）
- M Views/ConsolePage.xaml（非本轮所致，属其他会话/历史改动，本交接不宣称归属）

### 8.2 dist / stable 哈希（Get-FileHash SHA256 实测）
| 文件 | SHA256 | 状态 |
|---|---|---|
| dist\SoulLink.exe | 2773D6491E9068E9C4946FD3C9EF971543935A93B0214AF49B7F1FD593B81142 | 第二轮源码发布，PID 193256 |
| dist\stable\SoulLink-Stable.exe | 78D8389990131C76564DBA13B7802B59D8FC225E3D7974D6A7964975777BBC6F | 未改 |
| dist\stable\SoulLink.exe | 06A742A0647CABA7F5EEBDE4F4643F20DA2C7915805773546EB9653747A49002 | 未改 |

### 8.3 约束遵守
- 本轮从未修改 dist、dist/stable，从未启动/停止/点击/RDP 操作，未 commit git，未做额外备份。
- dist\SoulLink.exe 第二轮覆盖是 Lead/发布流程所为（22:25:47），非本成员直接操作。

---

## 9. 后续建议（只写调查与验收原则，不执行，不自动继续修复）

1. 优先决定镜像路线去留：RdpMirrorOverlay（DWM 缩略图）与源 RDP 原生窗口职责、Z 序、裁剪边界必须二选一或严格分离，禁止两者叠加导致白框/越界。
2. 布局时序：Arrange 完成后统一做宿主 rect 在视口可见 rect 内的机械钳制（坐标 + 尺寸），对 (0,0)、负坐标、越界一律钳制，不只处理 < -20000。
3. 统一 Single 标签轻量路径：已可见标签复用轻量 SetFocus 语义，不再走 ShowViewPageAsync 完整挂载。
4. 焦点与锁存单一状态机：真实原生焦点、逻辑 _focusTile/_viewing、ShieldLocked/_shieldReleased 三态必须单一真源，禁止 Deactivated 与 RdpConsole 独立重锁双通道。
5. 真实人工验收：必须人工复现四项（解锁后点 B 切焦点输入落 B、标签点击无整幅闪黑、RDP 画面不越界覆盖右侧控件、离开窗口自动上锁），任何编译/截图/脚本点 tile 都不作通过依据。

---

## 附：证据来源与置信边界

- 哈希：Get-FileHash SHA256 实测（高置信）。
- git 改动文件：git status --short 实测（高置信）。
- 日志时间戳/事件：grep rdp-debug-20261119.log 原文（高置信，仅覆盖 22:26 后关键事件，未穷举全日志）。
- 视频/截图具体画面：由 Lead 解码取证，本成员未直接读视频帧，相关描述以 Lead 结论为准（中置信）。
- 源码函数行为：读取当前源码静态判断（中置信，未经真机）。
- 白色矩形框根因：未证明，仅列候选排查方向（低置信，严禁当作结论）。
