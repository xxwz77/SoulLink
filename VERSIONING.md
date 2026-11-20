# SoulLink 版本管理与回退

> **2026-11-20 21:05 新版本 2.17.41（待定版）：定时·时刻支持递增延时 + 复位周期 + 精确到秒。**
> 交接文档：`docs/HANDOFF-2.17.41-20261120.md`
> 当前 `dist/SoulLink.exe` = **2.17.41.0** · 67,488,082 字节
> · SHA256 `73465DD1FFDCC8FEAF8ABC9FD71B3A89CCEF9A72AD08A8ED93480C9F19E48037`
> 新增 `stepSeconds`（下次执行延时）、`resetAfter`（复位周期），时刻改 HH:mm:ss。
> 第 N 次 = 基准 + N×步长，N 达复位周期后归零。次数**持久化**（重启不忘），跨业务日清零。
> ★ `TryParseClock` 严格解析：**不用 `TimeSpan.TryParse`** —— 它会把 "4" 当 4 天。
> 编辑器选中节点时显示"本轮第 N 次 · 本次时刻 … · 递增 x/y 次"。

> **2026-11-20 20:50 版本 2.17.40（待定版）：修双列画面不自适应窗口。**
> 交接文档：`docs/HANDOFF-2.17.40-20261120.md`
> 当前 `dist/SoulLink.exe` = **2.17.40.0** · 67,486,965 字节
> · SHA256 `93EB869B5F10AE78AF0F105CE0D8A33F2911389E0196078BB27A70610717660C`
> 根因：`ViewLayout.ComputeTwoColumn` 的高度**只由宽度推导**（cellW/16:9），
> 且 `maxCellH` 分支只缩不放 → 宽屏下格子高度只占视口 48%。
> 修法：在 `availW/availH` 两个上限内取尽量大的 16:9 格子（先按宽铺满，
> 高度不够再按高反推）。**第一次改漏了越界约束导致两列横向溢出，探针发现后已修。**
> ⚠ 两列并排时宽度是唯一瓶颈，高度占比小是几何必然（32:9 的最外层比例）；
> 想要铺满高度应切换「网格」或「单画面」模式。

> **2026-11-20 20:35 定版：2.17.39 为可信版本 / 恢复节点（源码 + 成品）。**
> **固定归档：`E:\SoulLink-SOURCE-ARCHIVE\v2.17.39-reliable\`**
> · source\（170 文件，客户端可编译子集，与仓库逐字节一致）
> · docs\（41 文档快照，含 HANDOFF-2.17.19 ~ 2.17.39）
> · 成品 SoulLink-2.17.39.exe（67,486,930 字节）
> · 配套 7za / sl-rescue / broker 脚本 / tools\SoulLink.BrokerSetup.exe
> · restore.ps1 / TAG.txt / VERSION.txt / SHA256.txt（222 条全通过）
> · git 标签 `v2.17.39-reliable` → 提交 `055520d`
> 当前 `dist/SoulLink.exe` = **2.17.39.0**
> · SHA256 `C381242CA223594D102CE968DF04B2D11FF7C292A0456BF6063700E6733FC6C2`
> ★ 还原源码：`git checkout v2.17.39-reliable`，或复制归档 source\ 重新编译。

> **2026-11-20 20:20 2.17.39 已出更新包（用户认可"信任此版本"）。**
> 更新包：`releases\SoulLink-v2.17.39-Update.zip`（117 MB，含 7za / BrokerSetup / broker 脚本 / sl-rescue）
> · zip SHA256 `6898CC7CD636571937769D04B0D92F5E30872A96381806B36642595A93CF78DC`
> · 包内 SoulLink.exe = 2.17.39.0 · `C381242CA223594D102CE968DF04B2D11FF7C292A0456BF6063700E6733FC6C2`
> RdpWrap 4 个 DLL 与图标已内嵌在 exe 内，包内无需 Assets 目录。
> 已实测：解压到临时目录可直接启动出主窗口（不依赖仓库目录）。
> 说明文档：`docs/HANDOFF-2.17.39-PACKAGE-20261120.md`
> 源码归档仍是 `v2.17.38-reliable`（2.17.39 未归档）。

> **2026-11-20 20:05 版本 2.17.39（已发布）：修主界面不自适应 + 滚动条消失。**
> 交接文档：`docs/HANDOFF-2.17.39-20261120.md`
> 当前 `dist/SoulLink.exe` = **2.17.39.0** · 67,486,930 字节
> · SHA256 `C381242CA223594D102CE968DF04B2D11FF7C292A0456BF6063700E6733FC6C2`
> 根因：`LayoutRoot` 三行全是 `Height="Auto"` → 外层高度由内容决定（实测 1952px 撑爆 500px 窗口）
> → 左栏 ScrollViewer 拿到无限高度 → WPF **不显示滚动条也不裁剪** → 内容溢出。
> 修法：改为单行 `Height="*"`；右栏会话列表补竖向滚动、日志区补 MinHeight。
> 可信归档仍是 `v2.17.38-reliable`。

> **2026-11-20 19:35 定版：2.17.38 为可信版本 / 恢复节点。**
> **固定归档：`E:\SoulLink-SOURCE-ARCHIVE\v2.17.38-reliable\`**
> （source + docs + 成品 + VERSION.txt + SHA256.txt + restore.ps1；216 条哈希全通过）
> 当前 `dist/SoulLink.exe` = **2.17.38.0** · 67,487,060 字节
> · SHA256 `7ED66631080A4028FE71D962B4036F507879ECA7ED68051A7B0E20198F4120D4`
> 注入代理 `C:\Program Files\SoulLinkDev\` 的 SoulLink.exe / SoulLink.Agent.exe
> **均已对齐到 2.17.38.0**（上一版 2.17.21.0 保留为 `*.old`）。
> 本版覆盖 2.17.20~2.17.38 全部修复：通道门控、五色状态灯、失败不封盘、
> 今日成功/失败计数、今日完成按钮、按键捕获（含 Modifier/Esc）、
> 鼠标选图点选（标记跟随+坐标回填+大图选点）。
> ⚠ **仍未解决**：UI 自适应（小窗口卡片挤压、按钮点不到）—— 用户多次提到，本版未处理。
> ⚠ 归档时注入代理**无活进程**（会话 4/5 投送未成功），需下次启动观察。
> 上一可信版 2.17.19 归档保持不变。

> **2026-11-20 19:20 版本 2.17.38（已定版）：鼠标选点修标记/回填 + 大图选点。**
> 交接文档：`docs/HANDOFF-2.17.38-20261120.md`
> 当前 `dist/SoulLink.exe` = **2.17.38.0** · 67,487,060 字节
> · SHA256 `7ED66631080A4028FE71D962B4036F507879ECA7ED68051A7B0E20198F4120D4`
> 修 2.17.37 三个实测问题：
> ① 蓝点不跟随 —— 原来**从没算过 Margin**，且 Uniform 下图片绘制区 ≠ 元素区；
>    现按 `Math.Min(boxW/srcW, boxH/srcH)` 反推绘制矩形 + 居中偏移定位。
> ② 坐标不回填 —— 点完只写了节点，没刷 X/Y 输入框；新增 `SyncXYBoxes()`，
>    并给默认文本框补 `Tag = p.Key`。
> ③ 预览太小 —— 缩略图点开**弹大窗**（屏幕 85% 内），实时十字+坐标，
>    单击确认即自动关闭，黑边点击判无效。

> **2026-11-20 19:00 版本 2.17.37（待定版）：鼠标坐标改为选图点选 + 自动换算。**
> 交接文档：`docs/HANDOFF-2.17.37-20261120.md`
> 当前 `dist/SoulLink.exe` = **2.17.37.0** · 67,484,823 字节
> · SHA256 `2DFABAFDAF64B4104F5AEFBB4349977A47B419AE46A873B16B508A6E26A1AE32`
> 新增 `FlowParamKind.CursorPick`：选一张截图 → 图上点位置 → 存**归一化比例**
> （不是像素），运行期 × 目标会话实际分辨率换算；换分辨率/换机器都能点对。
> 没配图时回退旧 x/y，完全兼容。`GetDouble/SetDouble` 强制 InvariantCulture
> （否则中文区域把 0.5 写成 0,5 会解析成 0）。

> **2026-11-20 18:45 版本 2.17.36（待定版）：修饰键可单独录入 + Esc 可录入。**
> 交接文档：`docs/HANDOFF-2.17.36-20261120.md`
> 当前 `dist/SoulLink.exe` = **2.17.36.0** · 67,483,134 字节
> · SHA256 `BD4F63A6473EEF717BDFE5BA6C6F1FE119F758783B402884BD487D1E70BBB7EF`
> ① 原来"按下主键才结束"，只按 CTRL/SHIFT/ALT 永远停在预览 → 无法单独录入。
>    加 `PreviewKeyUp`：没按过主键时松开修饰键即提交（仅认单个修饰键）。
> ② 原来 Esc 被"取消监听"占用 → 永远录不进 Esc。取消改由「取消录入」按钮负责，
>    Esc 变成可正常录入的按键；另加「清空」按钮。

> **2026-11-20 18:30 版本 2.17.35（待定版）：修按键捕获"点了没反应"。**
> 交接文档：`docs/HANDOFF-2.17.35-20261120.md`
> 当前 `dist/SoulLink.exe` = **2.17.35.0** · 67,482,714 字节
> · SHA256 `87D956BA4C0793E012C317E72A780915FC5D860CC7E158C01F3DEC67201F345C`
> 根因：2.17.34 用 `Border`（Decorator，非 Control）做捕获框，`Focus()` 拿不到键盘焦点
> → `PreviewKeyDown` 根本不触发。改为 `TextBox` + 可编辑但拦截所有输入 +
> `PreviewMouseLeftButtonDown` + 显式 `Keyboard.Focus`。
> 用真实 WPF 探针验证：focused=True、KEYDOWN 正常、CAPTURED=CTRL+A。

> **2026-11-20 18:15 版本 2.17.34（待定版）：模拟按键改为按键捕获录入。**
> 交接文档：`docs/HANDOFF-2.17.34-20261120.md`
> 当前 `dist/SoulLink.exe` = **2.17.34.0** · 67,482,250 字节
> · SHA256 `4A5406DEB86DFB70F3EA0A59A73280EAB6F022F953B3D9D64F92FEB04625718B`
> 新增 `FlowParamKind.KeyCapture`：点一下框、直接按真实按键（可组合键），
> Esc 取消 / Backspace 清空；不再要求用户手打 "CTRL+A" 字面量。
> 同时补齐 `RelayAgent.ParseVk` 的 11 个符号键（原来录进去也发不出去）。
> ⚠ 代理（SoulLink.Agent.exe）未升级，符号键补丁尚未在实际代理里生效。

> **2026-11-20 18:00 版本 2.17.33（待定版）：通道门控——先建通道，再判定条件。**
> 交接文档：`docs/HANDOFF-2.17.33-20261120.md`
> 当前 `dist/SoulLink.exe` = **2.17.33.0** · 67,481,036 字节
> · SHA256 `0ACBE853FB4C5EB242AE77E9651F8F6C69C24294DAAA0D928DC7576808342993`
> ★ 之前是「先判定条件 → 通道没好才等」→ 条件用的是通道建立前的采样值，
> 通道一就绪就补发动作；对「定时·间隔」这类脉冲触点会凭空补执行一次（用户实测指出）。
> 现在：`UsesInjection` 的流程先 `EnsureChannelBeforeScanAsync`，
> **未就绪直接返回、连 ScanAsync 都不进**（不判定、不补发），
> 就绪后用 `time = DateTime.Now` **重新取时间**再判定条件。
> 另堵死：`sid <= 0`（无会话）**不再放行**（第一版就是这么漏的）。
> 真机 40 秒：三个在用流程失败均为 0。

> **2026-11-20 17:50 版本 2.17.32（待定版）：修「今日完成」不出现 + 失败封盘。**
> 交接文档：`docs/HANDOFF-2.17.32-20261120.md`
> 当前 `dist/SoulLink.exe` = **2.17.32.0** · 67,479,949 字节
> · SHA256 `E6687697C8F0278B529E7D526BA8455C9B8F319EB532A38046A8987DA04D399D`
> ★ 真 bug：`complete && !failed` 两个条件同时失守（AllActivePathsComplete 把失败结果
> 也算"走完"；failed 依赖 WaitingActions 而那时动作已不在其中）→ **动作失败也写今日标记**
> → 当天不再重试。修法：`anyActionFailed = ActionResults.Any(!Ok)`，失败绝不封盘。
> ★ 「今日完成」原来只是点 8px 橙点（用户看不出可点）→ 改成显式胶囊按钮。
> 已备份并清理该流程的历史错误锁存，重启实测成功 1 次后按钮正常出现。

> **2026-11-20 17:35 版本 2.17.31（待定版）：流程名后显示今日成功/失败次数。**
> 交接文档：`docs/HANDOFF-2.17.31-20261120.md`
> 当前 `dist/SoulLink.exe` = **2.17.31.0** · 67,479,754 字节
> · SHA256 `B0246A2E09F2F2830CC515E914AD4527D32E4606A29B972E5B17FA74CF8CEBF8`
> 新增 `StatDayKey / TodayOkCount / TodayFailCount` + `RecordRunResult()`，
> 按业务日自动清零；**不能复用 RunCount**（它是累计值，本机已 1850 次，且只在整轮收尾 +1）。
> 计数点在"每次动作真正执行完"，与 LastRunOk 同处，保证成功+失败=今日执行次数。
> 日界跟随流程：无 END=00:00，有 END=该 END 的 dayBoundaryHour（通常 04:00）。

> **2026-11-20 17:25 版本 2.17.30（待定版）：执行前等待注入通道，黄灯 + 保持提示。**
> 交接文档：`docs/HANDOFF-2.17.30-20261120.md`
> 当前 `dist/SoulLink.exe` = **2.17.30.0** · 67,479,564 字节
> · SHA256 `A2DEF6F2642D73557291C8A02A7C86436C64A6579F6D627960AA733F12C46F95`
> 原来通道没就绪就**立刻判失败**（灯变红），但 RDP 刚连上时代理还在投送中。
> 现在模拟按键/鼠标类动作会先等通道（最多 20 秒，250ms 轮询），
> 等待期间灯=黄、摘要保持"等待注入通道建立（会话 N）…"，
> 超时才失败并给出可读原因；通道就绪则立即继续执行。
> 灯色顺序：暂停黄 → 等通道黄 → 今日完成橙 → 刚失败红 → 刚成功绿 → 等待蓝。

> **2026-11-20 17:10 版本 2.17.29（待定版）：END 流程"今日完成"显示橙色，可点击清除。**
> 交接文档：`docs/HANDOFF-2.17.29-20261120.md`
> 当前 `dist/SoulLink.exe` = **2.17.29.0** · 67,478,562 字节
> · SHA256 `89B166C99FF1B68E408B630DB69417500193211F241E725B6B601B32FEAC1299`
> 状态灯增至五色：黄=暂停 / **橙=今日完成（END 已锁存）** / 红=刚失败 / 绿=刚成功 / 蓝=等待。
> 橙色优先级仅次于暂停，且**灯本身可点击** → 清除今日标记（同时复位本轮运行状态）。
> 完整链路实测：蓝 → 橙 → 点橙清标记 → 蓝 → 再跑 → 又橙。

> **2026-11-20 17:00 版本 2.17.28（待定版）：状态灯随执行交替，不再焊死。**
> 交接文档：`docs/HANDOFF-2.17.28-20261120.md`
> 当前 `dist/SoulLink.exe` = **2.17.28.0** · 67,478,255 字节
> · SHA256 `1D6F506A7D1E35A6740DB5AD1E7397C071EAD8EFCF5DC0653C3D7C54946FBAE1`
> 根因：2.17.23 把 `LastRunOk` 当持久状态 → 成功一次就永远绿。
> 修法：结果加"保鲜期"，超过回蓝；保鲜期必须 **700ms < 界面 1 秒重建周期 < 执行间隔**。
> 踩过两次：6 秒（比执行间隔长）和 1.5 秒（比刷新周期长）都会让灯永远焊死。
> 另把自动化卡片刷新从 3 秒降到独立 1 秒轻量定时器。真机采样确认交替。

> **2026-11-20 16:55 版本 2.17.26（待定版）：快捷动作的四个按钮也能区分了。**
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
