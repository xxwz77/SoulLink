# 对标研究 Yomori 之一：RDP 内嵌与画面链路（FreeRDP vs ActiveX / AVC444 / 脏区增量）

> 日期：2026-11-24
> 作者：webclient-instrumentation（dsh-team `team_mwu35l8pae8oj5` / member `m_mwu3791qage7i6` / task `task_mwubm3jeo7buxx`）
> 对象仓库：**https://github.com/AITNR/Yomori**（public，**GPL-3.0**，`main` 分支；本文所有对方证据均为 **GitHub Contents API 只读抓取**，见 §8 取证方法）
> 性质：**只读对标研究**。未改本仓库任何产品代码/脚本/配置/版本号；未碰 `dist\stable\SoulLink-Stable.exe`（PID 13392）；未使用 A1/A4；未停启任何进程；未新建团队。唯一写入 = 本文档 + `docs/research/README.md` 一行登记。
> **④ 合规声明（验收硬指标）**：本文**未复制任何 Yomori 源代码**进本仓库。全文出现的代码片段只有两类：① 我方 `docs/web-index.html` 的**我方**代码；② 对方**头文件中的常量/宏定义**（`YCN_*`、`PTR_FLAGS_*`、`KBD_SYNC_*`）与**文件名/函数名**，用于说明机制。这些属于**接口事实**（不受版权保护的思想/事实层），且本文档本身是研究文档、不参与编译（`DefaultItemExcludes` 不含 docs，但 .md 不编译）。**任何一行 GPL 实现代码都没有进入本仓库。** 详见 §7「GPL 红线与复用边界」。
> 证据分级：**【对方代码】/【对方文档】**＝Yomori 仓库中具体文件（路径逐条给出）；**【我方代码】**＝SoulLink 工作区 file:line；**【我方实测】**＝本仓库前序实测（注明出处）；**【推断】**＝有依据未实测；**【待验证】**＝无证据，不猜。

---

## 0. 一页结论

### 0.1 三个最重要的判断

1. **Yomori 的 RDP 路线和我们根本不同，而且是「更正确」的那一条——但代价我们目前付不起。**
   他们**放弃了 ActiveX，改用 FreeRDP 自建客户端 + 自解自绘**（`winui3/docs/rdp-embed-freerdp-plan.md` §1.2 明写「不要复活 ActiveX 路线」）。我们（SoulLink）走的正是他们明确放弃的那条 ActiveX + `WindowsFormsHost` 路（`Controls/RdpConsole.cs:19,37`）。
   → 这不是「我们落后」，而是**两条路的成本结构不同**：他们付的是「协议栈自管 + 原生产物 + GPL 传染面」的长期成本；我们付的是「黑盒窗口 + SetParent/焦点/Z 序 hack」的**已知存量**成本。**换路的收益是真的，但工作量是 9~13 个 AI 会话（他们自己的估算），而且与我们的 TermWrap 多会话架构正面冲突**（§3.3）。

2. **他们最有价值、我们能真的拿走的，是「一组用命换来的反面清单 + 一套失败语义设计」，不是任何实现**：① 键盘 toggle 必须同步（我们**有确定性 bug**）；② 「等关键帧」必须有**兜底放行帧数**（我们有闸门、只缺兜底）；③ 脏区的三条安全不变量；④ 一串「错误表现会误导你」的踩坑记录（Winsock 未 `WSAStartup` 伪装成 DNS 故障、rdpsnd 硬依赖 rdpdr、AutoDetect PDU 破坏 RC4 流、`PTR_FLAGS_MOVE` 写错就断流）。**全是思想，没有一行代码值得抄。**
   其中**脏矩形增量对我们几乎没有收益**——【我方实测】脏矩形平均占比 50.7%（`MEDIA-CAPABILITY-20261122.md:122`），而同文档实测编码只占 **4.9ms**，故脏区绝对收益上限约 **2.5ms**，且我们整帧 NVENC 喂不进矩形补丁（详见 §4.3）。真正值钱的是「不变量 + 失败降级 + 幂等 ack」这套**设计思想**，可用在静止判定与诊断上，不是「抄个脏区就能提速」。

3. **必须承认：我们有一条他们完全没有的能力——「一份会话画面，多端分发」。**
   他们是**单机单客户端内嵌**（画面只在自己窗口里）；我们是**Agent 抓屏 → H.264 → 网页/多 viewer**。我们的 `viewer.Pending` 有界队列、丢旧保新、按需 IDR、帧边界 v2 协议、浏览器 WebGL2 零回读呈现，**在这一维度上比 Yomori 更成熟**。不要因为「他们用了 FreeRDP」就自轻（§3.4）。

### 0.2 三分表（验收要求 ②）

| 分类 | 条目 | 一句话理由 |
|---|---|---|
| **可借鉴（拿走思想，不拿代码）** | ① **键盘 toggle 状态同步**（我们有确定性 bug）② 恢复解码后「等关键帧」的**兜底放行帧数**（我们有闸门、缺兜底）③ 切页/遮挡时**按会话**暂停解码 ④ 脏区三不变量（不确定就整帧 / 合并只在不会变贵时 / 先验后写）——**只用在静止判定与诊断，不指望提速** ⑤ 能力探测位 + 旧产物安全降级 ⑥ 诊断按 reason 分桶 | 全是**接口契约与失败语义**，可以**独立重新实现**，不构成衍生作品 |
| **我们有而它没有** | ① 一份画面多端分发（网页/移动/多 viewer）② 浏览器端 WebGL2 零回读呈现 + 帧边界 v2 + 按需 IDR ③ 网页端浏览器段实测 1~5ms（`AUDIT-20261124-09`）④ 跨会话投送/自动化/中控台业务面 ⑤ TermWrap 多用户并发会话 | 我们做的是「远程访问产品」，他们做的是「单机多开挂机工具」 |
| **不该抄** | ① **任何 FreeRDP 源码/patch/.c/.h 实现**（GPL-3.0 传染 + 我们闭源分发）② `ycn_rdp.c` / `ycn_dirty_*.c` 全族 ③ FreeRDP 的 vcpkg 构建链（他们自己都写「沙箱不可跑 GUI / 构建链会碎」）④ ActiveX→FreeRDP 的整体换路（与 TermWrap 会话所有权冲突、9~13 会话成本、无对应收益）⑤ 他们的 WinUI 3 `SwapChainPanel` 呈现栈（WPF 用不了） | 见 §7 |

---

## 1. 对方 RDP 内嵌的架构事实（逐文件取证）

### 1.1 分层：C# WinUI 3 ← C ABI → C++ 原生 DLL → FreeRDP 3.32.0

【对方文档】`winui3/docs/rdp-embed-freerdp-plan.md` §3.1 给出的分层：

```
Yomori.App (WinUI 3, C#)     ← 全部 UI / 状态机 / 输入映射 / D3D11 呈现
      │ C ABI (DllImport)
Yomori.RdpNative (C++ DLL)   ← freerdp 生命周期 / 帧回调 / 输入注入 / rdpsnd
      │ 动态链接
freerdp3.dll + winpr3.dll    ← 上游 3.32.0
```

**关键决策【对方文档 §3.2】：刻意不用 C++/WinRT Runtime Component，改用 C ABI + P/Invoke。**理由是「XAML 自定义控件留 C# 侧 + 帧缓冲传递零封送」。这条**对我们有参考价值**（我们已有 `NativeDispatch.cs` 的 IDispatch 手写互操作先例，见 `Controls/RdpConsole.cs:20` 注释）。

**真实实现规模【对方代码】`winui3/src/Yomori.RdpNative/ycn_rdp.h`（14870 字节）**：C ABI 已从计划书的「约 10 个导出」长到**约 40 个导出**（含 audio 静音、键盘 toggle 同步、dirty ABI、诊断、自检）。这说明一个反直觉的事实：**「薄桥接」在实践中会长成厚桥接**——凡是原生侧持有的状态（会话/帧/音频/锁状态），C# 侧都需要一个查询口。

### 1.2 FreeRDP 版本与构建

【对方代码】`winui3/native-deps/vcpkg.json` + `ports/freerdp/portfile.cmake` + `patches/*.patch`：
- 版本钉死 **FreeRDP 3.32.0**，通过 vcpkg overlay port 构建；
- 打了 **5 个自有 patch**：`freerdp-3.32.0-msvc-hw-label`、`-ycn-h264-diagnostics`、`-ycnz-avc444-low-chroma`、`-ycnz-decode-pause`、`-ycnz-keyframe-gate`。
- 【对方文档】`rdp-embed-freerdp-plan.md` 附录 C 承认：官方 Release / pub.freerdp.com / Jenkins / Actions **都没有现成 Windows 产物**，只能自建（vcpkg 装 `freerdp[client]:x64-windows` 约 40 分钟）。

**⇒ 判据：这条路的第一道门槛不是写代码，是「造出一个能跑的 FreeRDP Windows 二进制并长期维护它」。**

### 1.3 渲染：SwapChainPanel + D3D11 + dirty 增量

【对方文档】`rdp-embed-freerdp-plan.md` §5.2：M2 用 `SoftwareBitmap` 验证解码正确性，M4 换 `SwapChainPanel` + D3D11（Vortice.Windows）+ `ISwapChainPanelNative::SetSwapChain`。

【对方代码】`winui3/src/Yomori.App/Services/RdpD3DRenderer.cs`（30068 字节）开头的「防退化，改前必读」注释是**全文最有价值的工程经验**（原文摘要，非代码复制）：
- swap chain 尺寸 **必须等于**远端帧分辨率（同尺寸 `CopyResource`），否则是未定义行为，表现为**竖直条纹**；
- **缩放/黑边交给 XAML**（`SwapChainPanel` 包在 `Viewbox(Stretch=Uniform)`），**不要**手动 `SetCompositionScale`（会被布局重置为 1，画面变 1:1 裁切）；
- **`Present(0,0)` 不等 vsync**——等 vsync 会**阻塞 FreeRDP 事件循环线程**（网络/输入同线程）；
- **整条呈现管线在 UI 线程上跑**（16ms `DispatcherTimer` 驱动）。他们明确纠正了自己早期「零 UI 线程参与」的错误注释，并记录了一个真实故障：从原生帧线程直接调 `ResizeRemote` 与 UI 线程的 `CopyResource/Present` 相撞 → **整窗假死**（进程活着、`Responding=False`、UI 线程卡在 `Wait=LpcReply`、CPU 零增长），**偶发**（典型竞态）。修法是把尺寸与 D3D 写点全部收口到 UI 线程 + 类内 `_gate` 串行化。

### 1.4 脏矩形增量：本报告最值得抄的「设计」

**这是我们完全没有的一层，而且是他们做得最工程化的一层。**

| 层 | 文件 | 作用 |
|---|---|---|
| 采集 | `winui3/src/Yomori.RdpNative/ycn_dirty_snapshot.h/.c` | 把一次 `EndPaint` 批次的脏矩形**有界、零分配**地收集到调用方缓冲；`MAX_RECTS=2048`、`MAX_GDI_CAPACITY=65536` |
| 累积 | `ycn_dirty_pending.h/.c` | 将多个 `EndPaint` 批次**累积**为 sealed 批次；按 **64px tile 位图**（32×32 = 1024 tile，16 个 `uint64` word）记录；`MAX_BOXES=2048` |
| 计数 | `ycn_dirty_stats.h/.c` + `Yomori.App/Services/RdpDirtyPathStats.cs` | 分层计数：`attempts/ok/fail/empty/boxes/full/boxesTotal/boxesMax/bytesTotal` + **按 reason 分桶**（`notAttached/staleToken/staleSeq/notConnected/invalidArg/noMemory/sessionNotFound/other`） |
| ABI | `ycn_rdp.h` §「阶段 2B：dirty pending 增量帧 C ABI」 | `dirty_api_version()` / `dirty_attach(session,token)` / `dirty_copy_pending(...)` / `dirty_ack(session,token,seq)` / `dirty_detach`；三态 `EMPTY/BOXES/FULL` |
| 消费 | `RdpD3DRenderer.cs`（`_dirtyRects` 惰性分配、`dirtyBoxCalls/Bytes/FullUploads/TextureResets` 计数） | 只在 BOXES 时按矩形做局部 `UpdateSubresource` |

**它为什么值钱——三个安全不变量（全部在 `ycn_dirty_snapshot.c` 里用代码落实）**：

1. **「不确定就整帧」**：任何异常输入（尺寸非法、rect 越界、rect 数超限、面积溢出、`count==0`）都置 `full_required=1`，直接降级整帧，**绝不猜**；
2. **「合并只在不会变贵时才做」**：`finish()` 只在「包围盒面积 ≤ 各矩形面积之和」时把多矩形合并成一个包围盒（`ycn_dirty_snapshot.c` 的 `bbox_area <= sum_area` 判断），**否则保持多矩形**——这是「优化不能反而更慢」的显式守卫；
3. **「先校验整个计划，再动 dst」**：`copy_rects()` 在写任何像素前，先把所有 rect、stride、buffer 边界全部验完，**失败绝不留下半更新的帧**（注释原文：「a failed copy must never leave a partially updated frame」）；
4. **「未 ack 前重复取回同一批」**：`ycn_rdp.h` 明写「未 ack 前再次 copy_pending 会返回同一批（相同 seq/rects），不会丢帧」——**幂等取帧**，消费者崩了不会漏数据。

**这套设计的思路（不变量 + 三态 + 幂等 + 分桶诊断）可以直接用，代码一行都不能拿（GPL）。**

### 1.5 AVC444 / 硬解：他们踩过的坑比成果更多

【对方文档】`winui3/docs/rdp-hardware-decode-implementation-2026-10-31.md`：
- 默认 `decode_backend=auto`，优先 FFmpeg/DXVA2 解 AVC420；`software` 强制禁用；
- **关键诚实结论**：「硬解后的 GPU→CPU 回读、YUV 转换、GDI 合成和多次复制**仍存在**，不能将本次结果解释为整体 CPU 已下降某个百分比」；
- 故障恢复：软件恢复**不能盲目重放 P 帧** → 请求刷新 + 等后续关键帧 + 超时（5s 未恢复则明确报错断开）；
- 验证局限：「测试使用自行生成的单帧 SPS/PPS/IDR AVC420 数据…**未登录真实目标 RDP 会话**」。

【对方文档】`winui3/docs/rdp-cpu-followup-2026-10-31.md` 记录了一次**真实的失败**：
> 20:17 真实会话：`pipeline/thin/h264 均为 1`，但服务器确认 **8.1 flags=0x03（清除了 AVC420 0x10）**，实际 `codec=3`、`hw_frames=0`。服务端 `RdpCoreTS Event162` 同样记录 `AvcEnabled0`。**「不能把客户端设置为 h264=1 当作已经进入硬解」**。

【对方文档】`winui3/docs/rdp-avc444-aux-drop-plan-2026-11-14.md`（18593 字节）是整个仓库**最硬的技术文档**，结论摘要：
- AVC444 的 `op=0/1/2` 语义**不是**「主流/aux/色度」这么简单：**`op=2` 是「主流码流携带色度-only 更新」，它仍是主流 H.264 参考序列的一部分**，不能当 aux 丢弃，否则破坏后续 P 帧参考；
- **「历史『只跳 aux』开关已因未测得可辨收益而撤回」**——他们的第一次尝试**失败了**；
- 最终做法：`op=0/1` 主流解码后**直接 YUV420→BGRA**（跳过 YUV444 平面扩展与 aux 合成），`op=2` 解码但不做色度重建/不提交 dirty；
- 实测收益：**codec 路径 −5.6%**（据此把开关默认打开）；
- 明确边界：「跳过颜色转换而不改变显示链接口，会得到无法正确显示的数据。可行的长期路线是让 YUV420 surface 一直保留到 GPU shader 转换，但**这属于独立的渲染架构项目**」。

**⇒ 对我们的直接意义：他们花了至少 3 份计划书 + 1 次撤回，才把 AVC444 的 CPU 收益做到 5.6%。我们如果对标的目的是「降 233ms 延迟」，这条路**完全不对口**（CPU ≠ 延迟，见 §5）。**

### 1.6 遮挡/切页：他们付过学费的地方

【对方文档】`winui3/docs/embed-occlusion-fix-plan-2026-11-08.md`（13297 字节）记录的故障链，**和我们 `AGENTS.md` 里那条「SetActivePage 只切 Visibility、不触发 Loaded/Unloaded」是同族问题**：
- 现象：跑 10 分钟后画面定格 → 切页刷新变全黑 → 收托盘再双击恢复正常；
- 根因：全局闸门 `s_windowOccluded`（`static volatile bool`）被 `SuspendAllForHiddenWindow()` 置位后**没有任何对称复位路径**（只有 `AppWindow.Changed` 的 Minimized 分支 + `HideToTray/RestoreFromTray`）；
- 修复原则（**这条最值得我们抄**）：「**单一判定源** `SyncOcclusionGate()`——按窗口**当前实际状态**决定闸门，不依赖事件参数；所有『把窗口叫回来』的入口统一调它，方法幂等」；
- 还记录了一个**顺序陷阱**：`HideToTray` 里若先 `AppWindow.Hide()` 再置 `_hiddenToTray=true`，回调会读到旧状态把刚设的暂停**反向清掉**——必须先置位再 Hide 再同步闸门；
- 以及「跨重建残留状态」：`_renderSuspended` 在 `DetachClient/Disconnect/AttachClient` 三处都没复位，导致「切页回来全黑」。

---

## 2. 我方的 RDP 内嵌现状（对照基线，逐条给 file:line）

| 维度 | SoulLink 现状 | 证据 |
|---|---|---|
| 内嵌方式 | **ActiveX（MsRdpClient v9/v10/v11）+ 手写 `AxHost`（本机无 aximp）+ `WindowsFormsHost`** | `Controls/RdpConsole.cs:19-20,26-31,37-38` |
| 为什么手写 AxHost | .NET Core 下无 aximp，用 CLSID 直接建 `RdpAxHost` | `RdpConsole.cs:19`；`Services/NativeDispatch.cs:392` |
| 密码注入 | 原生 IDispatch 按 `DISPID=1` 写 | `RdpConsole.cs:20`（注释）、`NativeDispatch.cs` |
| 事件 | **无事件接口，加固轮询** | `RdpConsole.cs:2729-2733`（「手写 AxHost 无事件接口，使用加固轮询」） |
| 会话前置 | 连接前先 `tsdiscon` 目标会话（否则新建空会话） | `RdpConsole.cs:21` |
| 分辨率 | `DesktopWidth/Height` 在 Connect 前设，范围 200~8192 | `RdpConsole.cs:2631-2637` |
| 缩放 | `SmartSizing` + `EnableZoom`（只缩不放 → 允许放大） | `RdpConsole.cs:2639-2666` |
| 视觉特效 | `AdvancedSettings9.PerformanceFlags`（0x6F/0x18F/0x180/0） | `RdpConsole.cs:2616-2623` |
| 画面位置 | `WindowsFormsHost` = **独立 HWND**，不参与 WPF/DWM 圆角裁剪 | `RdpConsole.cs:434-436` |
| 焦点 | 只能靠 `AxHost.ContainsFocus` + 轮询加固判断 | `RdpConsole.cs:306,327` |
| 脏区增量 | **生产链路无**（`Relay/` 下 0 处使用；DDA 只取 `AccumulatedFrames` 做「变没变」判断）。**但测量工具里已有**：`tools/media-probe/Program.cs:367-390` 用过 `GetFrameDirtyRects`，【我方实测】脏矩形平均占比 **50.7%（n=64 帧）** | `Relay/DxgiDdaCapture.cs:285,392`；`tools/media-probe/Program.cs:373`；`docs/research/MEDIA-CAPABILITY-20261122.md:122` |
| 逐帧 CPU 回读 | **有**（DDA `ReadBackTexture` 全帧 BGRA 读回） | `Relay/DxgiDdaCapture.cs:274` |
| 编码 | 整帧 NVENC（无限 GOP + 按需 IDR + 关 B 帧 + zeroReorderDelay） | `Services/NvencSdkEncoder.cs:561-566` |
| 一份画面多端 | **有**（`viewer.Pending` 深度 2、丢旧保新、IDR 不丢、事件唤醒） | `Relay/SessionRecorder.cs:106-107,1051-1054` |
| 网页呈现 | **WebGL2 `texImage2D(VideoFrame)` 零回读（默认），2D 仅回退** | `docs/web-index.html:1132-1215,1247,1204` |
| 切页/遮挡 | `OnPageActivated/OnPageDeactivated` 只停看门狗与全屏菜单定时器，**不暂停画面呈现** | `Views/ConsolePage.xaml.cs:530-555` |
| 多会话 | **TermWrap 修补 termsrv**，A1/A4/A2 三会话并行 | `Services/RdpWrapService.cs`（`docs/research/ARCH-20261124-RDP-STREAM-TAPPING.md` §1.B 有实测） |

---

## 3. 逐维度对比

### 3.1 内嵌形态：ActiveX vs FreeRDP

| | Yomori（FreeRDP）【对方文档 `rdp-embed-freerdp-plan.md` §1.2/§1.3】 | SoulLink（ActiveX）【我方代码】 | 判定 |
|---|---|---|---|
| 画面归属 | 自己解码、自己 D3D11 画 | 系统控件画的，我们只能 `SetParent`/看 | 他们**根本性更强** |
| 凭据 | 连接参数直喂（`RdpCredentialStore.CredRead`） | 原生 IDispatch DISPID=1 写明文属性，受策略限制 | 他们更稳 |
| 状态感知 | 回调直通（`on_connected/on_disconnected(reason)/on_error`） | **无事件 + 加固轮询** | 他们更强 |
| 分辨率动态调 | DisplayControl 通道（M6） | `UpdateSessionDisplaySettings`（未用）/ 重连 | 持平 |
| 断开语义 | 程序自己决定 | 用户在 mstsc 窗口里选 | 他们更强 |
| Toggle 键同步 | **有**（`ycn_rdp_send_keyboard_sync`，解决小键盘与方向键共用扫描码） | **无**（全仓 `KBD_SYNC` 0 命中） | **他们更强，且我们可能有隐性 bug** |
| 二进制依赖 | freerdp3/winpr3 + OpenSSL（随包 30MB+） | **0**（系统自带） | 我们更轻 |
| 分发合规 | GPL-3.0（他们的选择） | ActiveX 是系统组件，无额外许可 | 我们更省心 |
| 维护面 | 协议栈自管（随上游升级） | 系统管（但 hack 自管） | 各有代价 |

**【我方实测旁证】**`docs/research/ARCH-20261124-RDP-STREAM-TAPPING.md` §1.B 已实测：本机 `ServiceDll = C:\Program Files\RDP Wrap\TermWrap.dll`，svchost 同时加载 termwrap+termsrv。**这条决定了我们不可能简单换成 FreeRDP 客户端而不动会话所有权**（§3.3）。

### 3.2 画面链路：他们「拉」vs 我们「推」

| | Yomori | SoulLink |
|---|---|---|
| 数据源 | RDP 协议栈自己解出来的 BGRA | DDA/WGC 抓屏 → BGRA（CPU 回读） |
| 帧边界 | 天然（每 `EndPaint` 是一个完整更新） | 手工切（`splitAnnexB` / v2 长度前缀） |
| 增量 | **脏矩形 BOXES**（局部 `UpdateSubresource`） | **整帧**（NVENC 全帧编码） |
| 呈现 | D3D11 `SwapChainPanel` | 网页 WebGL2 / 2D；桌面 ActiveX 窗口 |
| 一份画面多端 | **不支持** | **支持**（多 viewer 队列） |
| 端到端延迟口径 | 无公开实测数字 | 【我方实测】浏览器段 1~5ms（`AUDIT-20261124-09`）；服务端帧 gap p50 55ms（`TOOL-20261124-DIRECT-PROBE`） |

**⇒ 关键判断**：他们脏区增量收益大，是因为**他们的呈现就是逐矩形 `UpdateSubresource`**——脏区直接等于「少上传的像素」，中间**没有编码器这一层**。而我们的链路是「抓屏 → 整帧 NVENC → 多 viewer 分发」，**NVENC 不收矩形补丁**，所以**照搬脏区到编码链路不成立**。
再加一条硬数字：我方 `tools/media-probe` 早已实测脏矩形平均占比 **50.7%**（`MEDIA-CAPABILITY-20261122.md:122`），而同一份文档实测**编码只占 4.9ms** ⇒ 脏区理论的绝对收益上限约 **2.5ms/帧**，且**不改善延迟**（我们的延迟痛点不在这）。脏区对我们的价值只剩两处：① 桌面 ActiveX 画面自绘替代（远景）；② 「这一帧到底变没变」的判据精度（见 §4.3）。

### 3.3 为什么我们现在不能换 FreeRDP（结构性冲突，非能力不足）

1. **会话所有权冲突**：TermWrap 修补的是本机 termsrv，我们的多用户并发（A1/A4/A2）建立在它之上。【我方实测】`ARCH-20261124-RDP-STREAM-TAPPING.md` §1.B 结论原文：「**第三方 RDP 服务端都无法共享 TermWrap 维护的会话/图形栈**」。换 FreeRDP 客户端不解决服务端问题，只换客户端——**收益是「画面可控」，而我们真正的痛点是「延迟」与「多端分发」，两者都不因换客户端而改善**。
2. **成本**：对方自估 M0–M5 = **9~13 个 AI 会话**，且 M0 闸门（NLA/CredSSP 在 Win11 24H2+ 能否连通）失败就得整体冻结（`rdp-embed-freerdp-plan.md` §7 R2 / §6 M0 失败出口）。
3. **GPL 传染**：FreeRDP 本身 Apache-2.0，但他们**打了 5 个 patch 的原生侧代码**是 GPL-3.0 仓库的一部分，我们不能复用；自建意味着**从零重写这 5 个 patch 的功能**。
4. **收益错位**：我们当前最痛的是「**服务端帧供给节奏 ~16fps**」（`TOOL-20261124-DIRECT-PROBE`：帧 gap p50 55ms）与「**浏览器段已压到 1~5ms**」（`AUDIT-20261124-09`）。换客户端既不改善帧供给，也不改善浏览器段。

**⇒ 结论：换路是「正确的长期方向」，但不是「本季度的动作」。** 如果将来要做，前置条件是「我们的产品定位从『多端远程访问』收缩为『单机多开挂机』」——那就不是技术决策了。

### 3.4 我们明显更强的地方（不要自轻）

1. **一份画面多端分发**：`viewer.Pending` 有界队列 + 丢旧保新 + IDR 永不丢 + 事件唤醒（`Relay/SessionRecorder.cs:106-107,1051-1054`）。Yomori 完全没有这一层（它的画面只给本机窗口）。
2. **网页端呈现已做到零回读 + 实测延迟**：`docs/web-index.html:1204` `texImage2D(VideoFrame)`，实测 0.1~0.2ms/帧；2D 回退 22~25ms（`AUDIT-20261124-09` §4/§5）。他们**没有浏览器端**。
3. **传输层已做帧边界 v2 + 按需 IDR 控制通道**：`AgentWebServer.cs:478-525`（4B 长度前缀）、`/api/idr`（`AgentWebServer.cs:321-325,534-549`）。他们走 RDP 协议栈自带这些，但在**HTTP 形态**上我们有等价物。
4. **诊断可观测性**：我们有 `queueDrops/queueDepthMax/queueAge/queueAgeMax`（`SessionRecorder.cs:628-630`）、`captureMs/convertMs/encodeMs/zeroMs`；他们的 `RdpDirtyPathStats` 分桶更细，但覆盖面窄（只有 dirty 路径）。

---

## 4. 可落地动作（验收要求 ③：改哪个文件 / 工作量 / 预期收益）

> 原则：**只落地「思想」，不落地「代码」**。每条都给出「我们自己怎么写」的一句话方向。
>
> **排序口径（先说清）**：本次对标**没有任何一条能降低 233ms 延迟**（§5）。所以这里的 P0/P1 是按「用户可感知的正确性 / 省资源 / 可观测性」排的，**不是按延迟收益排**。
> · **P0** = 确定性功能 bug（键盘小键盘打错）——不修就是错的；
> · **P1** = 省资源 + 修体感故障（切页不停转、恢复后花屏）；
> · **P2** = 省 CPU（脏区，收益 ≤2.5ms，**不建议立项**）+ 可观测性；
> · **P3** = 预防性收口。

### 4.1 P0 —— 键盘 toggle 状态同步（我们有隐性 bug，他们已解决）

| 项 | 内容 |
|---|---|
| **问题** | Set1 扫描码里小键盘 0-9 与 Insert/Home/PgUp/End/PgDn/方向键**共用扫描码**（`Numpad1` 与 `End` 同为 0x4F），只靠 extended 位 + 服务器侧 NumLock 区分。若不同步本机三个锁的状态，远端按小键盘会打出方向键——**这是确定性可复现的功能 bug，不是性能问题**。 |
| **他们的做法【对方代码】** | `ycn_rdp.h`：`YCN_KBD_SYNC_SCROLL_LOCK/NUM_LOCK/CAPS_LOCK/KANA_LOCK` 四位 + `ycn_rdp_send_keyboard_sync(session, flags)`，注释明写「每位只在对应锁**开启**时置 1，这是状态快照不是『按一下』」。 |
| **我们改哪** | ① `Controls/RdpConsole.cs`：连接成功 / 画面获得焦点时读 `GetKeyState(VK_NUMLOCK/VK_CAPITAL/VK_SCROLL)`；② 把三态转成「需要按哪些 toggle 键」；③ 走**现有**输入通道发送——agent 侧已支持 `action: key/keydown/keyup`（`Relay/AgentWebServer.cs:564,620-622,642`），**无需新增协议**，只要按需要发 `Numpad* / CapsLock / ScrollLock` 的 down+up；④ `Services/SessionInput.cs`（184 行，注入封装）加一个 `SyncToggleKeys()` 入口。 |
| **工作量** | **0.5 人日**（无需改 agent、无需改网页；纯 C#，复用现有 `/api/input` 的 key 动作） |
| **预期收益** | 修掉「小键盘打不出数字/方向键错乱」的确定性 bug。**不改善延迟**，但这是用户能直接感知的正确性问题。 |
| **风险** | 低。纯新增一个动作，不改现有注入路径。 |

### 4.2 P1 —— 「按会话暂停解码」+「等关键帧的兜底放行」（一处要新做，一处只差兜底）

| 项 | 内容 |
|---|---|
| **问题 A** | 我们的 `OnPageDeactivated` 只停看门狗与全屏菜单定时器（`Views/ConsolePage.xaml.cs:546-555`），**画面呈现不停**——切走页面后 DDA 抓屏、NVENC 编码、网页推送全在跑。 |
| **问题 B** | 恢复时若解码器 DPB 缺参考帧，先到的 P 帧会解出成片错块，并**一直烂到下一个 IDR**（我们已有闸门避免「烂画面被显示」，但**没有兜底放行**，见下行）。【对方文档】`rdp-embed-freerdp-plan.md` 同级经验 + 他们的 patch `freerdp-3.32.0-ycnz-keyframe-gate.patch` 明写：「恢复后先只『喂』不『出』，拿到主码流第一个 I 帧之前不把结果写进表面；兜底 90 帧后放行（宁可短暂有块，不能永久定格）」。 |
| **他们的做法【对方代码】** | `ycn_rdp_set_session_decode_paused(session, paused)`（**按会话**，不是进程级）；`+ ycn_rdp_set_decode_paused`（进程级，注释警告「只应在整个窗口都不可见时用」）；恢复时**请求一次全帧刷新**并等关键帧。 |
| **我们改哪** | ① **A 需要新增一条 agent 命令**（实测：`Relay/AgentWebServer.cs:615-625` 的 action 白名单里**没有**任何「暂停/恢复编码」动作，`Relay/` 下也 grep 不到 `SetActivePage`/`PauseStream`）→ 要加一个 `{"action":"pause"}` 语义 + `SessionRecorder` 的暂停闸门，属于**跨仓库改动**，工作量比表面大；② **B 的实情要更正**：我们**已经有等关键帧的闸门**——① 起播 `web-index.html:1517` `if (!firstKey && !isKey && !isSps && !isPps) continue`；② 解码错误后 `recoverViaIdr()` 显式 `firstKey = false` 并 `decoder.reset()`（`:1295-1297`）等新 IDR。**真正缺的只有两件**：(a) **兜底帧数**——他们设了「90 帧还没等到 I 帧就强制放行，宁可短暂有块也不永久定格」（`freerdp-3.32.0-ycnz-keyframe-gate.patch` 的 `YCN_WAIT_KEYFRAME_MAX_FRAMES 90`）；我们只有 10s 看门狗（`:1378-1384`）兜底，即**最坏白等 10 秒**；(b) **恢复期不清屏**——他们让「表面停在暂停前最后一张好图上」，我们 `recoverViaIdr` 后旧画面还在但一直冻着，直到新 IDR 到达。 建议：(a) 加计数器，≈15 行；(b) 维持现状（我们的旧画面本来就还在）。 |
| **工作量** | **A：1~1.5 人日**（agent 新命令 + 服务端闸门 + 真机验证跨 2 个仓库）；**B：0.25 人日**（前端 ~15 行加兜底帧数，其余已具备） |
| **预期收益** | A：切走页面不再烧 CPU/带宽（挂机场景常年开着，收益是持续的）；B：把「等 IDR 最坏 10s 黑屏/停帧」压到「约 90 帧 ≈ 1.5~3s 后强制放行」。**都不直接降 233ms**。 |
| **风险** | A：跨仓库 + 改编码节拍，历史上有「改节拍就画面卡住」的先例，需真机回归；B：纯前端，零风险，建议先做 B。 |

### 4.3 P2 —— 脏区增量：**我们能拿到的收益比想象的小得多，不要高估**（本报告最需要说清的一条）

> ⚠️ **这条必须写在前面：我们已经有脏区实测数据，而且数字不好看。**

| 项 | 内容 |
|---|---|
| **我方已有实测（关键）** | 【我方实测】`tools/media-probe/Program.cs:367-390` 早已实现 `GetFrameDirtyRects` 采样，实测**脏矩形平均占比 50.7%（n=64 帧）**（`docs/research/MEDIA-CAPABILITY-20261122.md:122`）。同一份文档 §206/§214 的结论原文是：「脏矩形占比 50.7%，收益方向成立，但基线编码只占 4.9ms，**脏矩形的绝对收益有限**」「换抓屏的收益是 17~20ms/帧，而脏矩形最多省 4.9ms 的一半」。 |
| **⇒ 直接判断** | **脏区增量在我们链路上最多省 ~2.5ms/帧（编码 4.9ms 的一半量级），而且我们的 NVENC 是整帧编码（`Services/NvencSdkEncoder.cs`），根本喂不进矩形补丁**——想用只能改成「按脏区裁剪并重编码区域」，那是重写编码链路，成本远超 2.5ms 的收益。**结论：不要为「脏区」立项。** |
| **他们的做法【对方代码】** | `ycn_dirty_snapshot.c`：`finish()` 里 `if (snapshot->copy_area >= desktop_area) snapshot->full_required = 1u`——**只有脏区面积 ≥ 全屏才整帧**；`copy_rects()` 先验全部 rect/stride/边界再写；`ycn_dirty_pending.h` 用 64px tile 位图 + 2048 box 上限累积批次；ABI 三态 `EMPTY/BOXES/FULL` + `ack` 幂等。 |
| **他们为什么需要** | 他们的呈现是**逐矩形 `UpdateSubresource`**（`RdpD3DRenderer.cs` 的 `_dirtyRects` 惰性分配），脏区直接变成「少上传的像素」，**没有编码器这一层**。**我们的链路结构不同，不能类比。** |
| **那我们该拿什么** | **只拿「不变量」这一层，用在「静止判定」上，而不是「编码」上**：① 「不确定就整帧」（我们现在判断「变没变」只有 `AccumulatedFrames` 增长，见 `Relay/DxgiDdaCapture.cs:285,392`，**只回答有没有变、不回答变了多少**）；② 「合并只在不会变贵时做」。 |
| **真要写的话** | `Relay/DxgiDdaCapture.cs`：复用 `tools/media-probe` 已验证的写法（注意那个 size 参数陷阱，见下），暴露 `DirtyAreaRatio`，**先只观测一周不改行为**。 |
| **⚠️ 已知陷阱（我方文档记录）** | `GetFrameDirtyRects` 的 `dirtyRectsBufferSize` 是**缓冲区字节数**（= `rects.Length * 16`），**不是** `TotalMetadataBufferSize`。传错会算出**恒 100%** 的脏区占比（`tools/media-probe/README.md:139-142`、`MEDIA-CAPABILITY-20261122.md:448`）。 |
| **工作量** | 观测阶段 **0.5 人日**；若真启用静止判定改造，另 **0.5 人日** + 一周真机观察 |
| **预期收益** | 【推断】**不降延迟**；充其量在「挂机静止 + 只有光标在动」场景少编一些帧，省的是 CPU 不是 ms。 |
| **风险** | **中高**。改静止判定会直接触发「画面卡住」类投诉（历史上 `staticSkipped` 暴涨就是这条路的翻车，见 `AUDIT-20261124-07` D01/D05）。**必须先只观测、不动行为。** |

### 4.4 P2 —— 诊断分桶：把「丢帧/失败」从裸计数升级为「按原因分桶」

| 项 | 内容 |
|---|---|
| **他们的做法【对方代码】** | `RdpDirtyPathStats.cs`：`ok/fail` 之外再分 `empty/boxes/full/boxesTotal/boxesMax/bytesTotal`，失败再按 **reason 枚举**分 9 桶（`notAttached/staleToken/staleSeq/notConnected/invalidArg/noMemory/sessionNotFound/other`），且**全部 `Interlocked`、5 秒一行日志、不逐帧写盘**。 |
| **我们改哪** | `Relay/SessionRecorder.cs`：现有 `queueDrops` 只有一个总数。建议拆成 `dropsByReason`（队列满丢 / 关键帧保护跳过 / 超时丢）+ `skippedAUs` 直方图（浏览器端 `web-index.html:1419-1438` 已有计数，只是没上报）。 |
| **工作量** | **0.25 人日** |
| **预期收益** | 把「卡」变成可归因。**这是所有后续优化的前提**（与我们 `AUDIT-20261124-09` §6 的埋点建议同源）。 |
| **风险** | 零。纯计数。 |

### 4.5 P3 —— 遮挡闸门的「单一判定源」原则（我们已经有类似问题）

| 项 | 内容 |
|---|---|
| **他们的做法【对方文档】** | `embed-occlusion-fix-plan-2026-11-08.md` §3.1：**`SyncOcclusionGate()` 单一判定源**，按「窗口当前实际状态」决定闸门，不依赖事件参数；所有入口幂等调用；并记下了「先 `AppWindow.Hide()` 再置 `_hiddenToTray` 会让回调反向清掉暂停」的顺序陷阱。 |
| **我们的对照（已核实，比预想的好）** | 我们**已经有**单一判定源 `SyncUiVisible()`（`Views/MainWindow.xaml.cs:1200-1208`）：`bool visible = IsVisible && WindowState != Minimized` —— **正是「按当前实际状态、不依赖事件参数」的幂等写法**，两个入口（`StateChanged` `:107`、`IsVisibleChanged` `:110`）都调它。**这条我们已经做对了。** |
| **我们缺的只有一处** | 但 `SyncUiVisible` 只喂了 `AccountStateCenter.SetUiVisible(visible)`（`:1205`），**没有喂「画面呈现/编码是否该停」**——画面那侧仍只在 `ConsolePage.OnPageDeactivated` 里停看门狗（`Views/ConsolePage.xaml.cs:546-555`）。即：**判定源对了，消费者少接了一个。** |
| **我们改哪** | 把 `SyncUiVisible` 的 visible 也接到「`ConsolePage` / `RdpConsole` 的呈现闸门」上（若 §4.2A 的暂停通道落地，这里就是它的触发点）。 |
| **工作量** | **0.25 人日**（若与 §4.2A 合并做则记入 A，不额外计） |
| **预期收益** | 「窗口隐藏 → 画面/编码真停」的语义闭环；顺带预防切页回来黑屏类故障。 |
| **风险** | 低（但改动点在主窗口 + 呈现闸门，需真机回归）。 |

### 4.6 不做清单（明确否掉，避免后续被反复提起）

| 项 | 为什么不做 |
|---|---|
| 换成 FreeRDP 客户端 | §3.3：与 TermWrap 会话所有权冲突、9~13 会话成本、GPL 传染、且**不解决我们的痛点（帧供给 + 多端）** |
| 抄他们的 AVC444 低色度模式 | 收益 **−5.6% CPU**，与延迟无关；且需要 FreeRDP patch（GPL），我们连 FreeRDP 都没有 |
| 抄 `SwapChainPanel` 呈现栈 | 我们是 WPF，WinUI 3 的 `SwapChainPanel` 用不了；我们网页端已有 WebGL2 零回读 |
| 抄 `ycn_dirty_*` 的 C 实现 | GPL-3.0 + 我们的链路是整帧 NVENC，脏区喂不进去 |
| 抄他们的 vcpkg 构建链 | 他们的计划书自己写「构建链会碎、R3 中风险、需要一键脚本」——我们没有任何 FreeRDP 依赖，抄它等于给自己装一个持续维护负担 |

### 4.7 工作量汇总（供排期）

| 优先级 | 项 | 工作量 | 改哪个文件 | 是否降延迟 |
|---|---|---|---|---|
| **P0** | 键盘 toggle 状态同步（修确定性 bug） | **0.5 人日** | `Controls/RdpConsole.cs` + `Services/SessionInput.cs`（复用现有 `/api/input` 的 key 动作，**不改 agent**） | ✗ |
| **P1** | 前端「等关键帧」兜底放行（≈15 行） | **0.25 人日** | `docs/web-index.html`（`:1517` 附近） | ✗ |
| **P1** | 按会话暂停编码（切页省资源） | **1~1.5 人日** | `Relay/AgentWebServer.cs`（新增 action）+ `Relay/SessionRecorder.cs`（暂停闸门）+ `Views/ConsolePage.xaml.cs` | ✗ |
| **P2** | 诊断按 reason 分桶 | **0.25 人日** | `Relay/SessionRecorder.cs`（`:628-630` 附近） | ✗ |
| **P2** | 脏区面积观测（**不建议立项**） | 0.5 人日观测 | `Relay/DxgiDdaCapture.cs` | ✗ |
| **P3** | 遮挡闸门：**判定源已有**，只补「接画面呈现闸门」 | **0~0.25 人日** | `Views/MainWindow.xaml.cs:1200-1208`（`SyncUiVisible` 的消费者） | ✗ |
| **合计（P0+P1+P2 诊断）** | — | **约 2~2.5 人日** | — | **0 条** |

> ★ **一句话**：这份对标能交付的是「2~2.5 人日修 1 个确定性 bug + 2 个体感故障 + 补 1 层诊断」，**不是「降低 233ms」**。想降延迟请回到 `TOOL-20261124-DIRECT-PROBE` 指向的服务端帧供给（帧 gap p50 55ms）。

---

## 5. 与「233ms 延迟」这个真问题的关系（重要，防止跑偏）

本次对标是「RDP 内嵌与画面链路」，但**必须说清它和我们当前的头号问题不是同一件事**：

- 我们的头号问题（本团队 2 天实测链）：233ms 差分里，**浏览器段只有 1~5ms**（`AUDIT-20261124-09` §4/§7.1），**传输层 ≤2ms**（`AUDIT-20261124-10`），**原生探针（不含浏览器）解码 0.91ms + 上屏 7.79ms**（`TOOL-20261124-DIRECT-PROBE`），而**帧到达 gap p50 = 55ms ≈ 16~18fps**（同文档）→ **矛盾指向服务端帧供给节奏**。
- Yomori 的 RDP 内嵌**完全不在这个坐标系里**：它没有「服务端帧供给」概念（RDP 协议栈自己出帧），也没有「多端分发」。**把它的架构搬过来，等于换一个坐标系，而不是解决现有坐标系里的问题。**
- **但它有一条间接启示值得记下**：他们走 RDP 协议栈后，**帧的来源是「变化驱动」的**（`EndPaint` 批次），天然没有「固定 fps 节拍空转」的问题。我们的 55ms gap 恰恰是「节拍器 + 变化驱动」混合模型的产物（`SessionRecorder.cs:1217-1245` 累积式节拍器 + `:962-967` 静止跳过）。**这提示：我们的节拍模型值得重新审视**（但这是服务端成员的活，不是本报告的落地项）。

---

## 6. 对方文档里「我们已经踩过 / 还没踩」的坑对照表（最有复用价值的一节）

> 这张表是**纯知识**，不含任何对方代码，可直接复用。

| # | 他们的坑【对方文档】 | 我们现状 | 判定 |
|---|---|---|---|
| 1 | **Winsock 必须显式 `WSAStartup`**，否则 FreeRDP 内部 `getaddrinfo` 全部 `WSANOTINITIALISED`，日志表现成「乱码 + DNS_NAME_NOT_FOUND」，极易误判成网络不通（他们为此浪费半小时） | 我们用 `HttpListener`/托管 socket，已自动初始化 | **用不上**（但记下这个「错误表现会误导」的教训） |
| 2 | **`NetworkAutoDetect`/`Multitransport`/`HeartbeatPdu` 必须关**：服务器处于标准 RDP 安全模式（`SecurityLayer=0`）时，AutoDetect PDU 会把 RC4 解密流错位 → `invalid packet signature` 连接失败 | 我们不用 FreeRDP，不受影响 | **用不上** |
| 3 | **rdpsnd 硬依赖 rdpdr**（load_addins 检测到 rdpsnd 会强制把 DeviceRedirection 拉回 TRUE） | 我们不重定向音频 | **用不上** |
| 4 | **`PTR_FLAGS_MOVE = 0x0800`**，误写 0x0008（保留位）会让服务器判「协议流错误」直接断流（`RdpCoreTS #97`） | 我们不用 FreeRDP 的输入包格式 | **用不上** |
| 5 | **小键盘与方向键共用扫描码**，必须同步 NumLock 状态 | **我们没做**（全仓 `KBD_SYNC` 0 命中） | ✅ **必须借鉴（§4.1）** |
| 6 | **恢复解码后不能直接吃 P 帧**，必须等关键帧（否则一直烂到下一个 IDR），但要设兜底帧数上限（他们=90 帧） | 网页端 `:1517` 已有「首 key 前丢 delta」闸门、`:1295-1297` 恢复时会重置 `firstKey`，**但只靠 10s 看门狗（`:1378-1384`）兜底，缺帧数兜底** | ✅ **补兜底（§4.2B）** |
| 7 | **脏区「不确定就整帧」「合并只在不会变贵时做」「先校验再写」三条不变量** | 生产链路无脏区层（`tools/media-probe` 有测量实现，实测脏区占比 50.7%） | ✅ **只借不变量，用于静止判定（§4.3）；不指望提速** |
| 8 | **遮挡闸门必须单一判定源 + 幂等**，且有「先 Hide 再置标志」的顺序陷阱 | **我们已有同款幂等判定源** `SyncUiVisible()`（`MainWindow.xaml.cs:1200-1208`，判 `IsVisible && !=Minimized`，两入口都调）；**只是没接到画面呈现闸门** | ✅ **判定源已对，补消费者（§4.5）** |
| 9 | **UI 线程与 D3D 线程混用 → 整窗假死**（`Wait=LpcReply`、CPU 零增长、偶发竞态） | 我们的 `WindowsFormsHost` 是独立 HWND，跨线程风险点不同；但我们有 `Controls/RdpConsole.cs:434-436` 的 Z 序/裁剪问题族 | ⚠️ **同类不同因**，记下「偶发假死要查 LpcReply」 |
| 10 | **「客户端设置 = 实际生效」是幻觉**：设了 `h264=1` 但服务器 `CAPS_CONFIRM` 清了 AVC 位，实际走 ClearCodec，`hw_frames=0`。**必须以服务端确认值与实际 codec 计数为准** | 我们有过同族教训：`Settings.CaptureMode` 设了 WGC 但实际降级 BitBlt（`AUDIT-20261124-07` D01/D24） | ✅ **同族，已验证我们会踩** |
| 11 | **「硬解成功 ≠ CPU 降了」**：他们明确写「硬解后 GPU→CPU 回读、YUV 转换、GDI 合成、多次复制仍存在」 | 我们已在 `AUDIT-20261124-09` 实测过同族结论（2D 回退 22ms 与「回读机制」无关，是光栅+同步成本） | ✅ **互相印证** |

---

## 7. GPL-3.0 红线与复用边界（验收要求 ④）

### 7.1 事实
- Yomori 仓库根 `LICENSE` = **GNU GPL v3**（本会话抓取确认，35149 字节标准文本）。
- 被 GPL 覆盖的包括：`winui3/src/Yomori.RdpNative/*.c/.h`（含 `ycn_rdp.c`、`ycn_dirty_*.c`）、`.cs` 全部、5 个 FreeRDP patch、`tests/*.c`。
- FreeRDP / winpr / OpenSSL / zlib 本身是 Apache-2.0/BSD/MIT（他们 `third_party/licenses/` 有清单）——**但这些上游许可不解除 Yomori 自有代码的 GPL**。

### 7.2 我们做了什么 / 没做什么
| 行为 | 是否发生 |
|---|---|
| 把 Yomori 的 `.c/.h/.cs` 复制进本仓库 | **没有** |
| 把 `YCN_*` 宏、`PTR_FLAGS_*`、`KBD_SYNC_*` 常量抄进本仓库源码 | **没有**（本文档只**提及**它们用于说明机制；本仓库源码 0 改动） |
| 把他们的算法「逐行翻译」成 C# | **没有** |
| 阅读文档、提炼**思想/不变量/失败语义**，用我们自己的话重述 | **有**（§4 全部是「我们自己怎么写」的一句话方向，不含实现） |
| 本文档本身进编译 | **不会**（`.md` 不参与 `dotnet build`） |

### 7.3 如果将来真要复用（决策树）
- **只要思想不要代码** → 允许，且无需任何许可动作（思想/接口不受版权保护）；**但必须在落地 PR 里注明「思想来源：Yomori 文档 §X」**，避免日后被指「洗稿」。
- **要抄任何一行 C 或 C#** → **禁止**，除非：① 产品整体转为 GPL-3.0 兼容分发；或 ② 走「clean-room」（一方只给需求、另一方不看源码）——成本远超收益，**不建议**。
- **要 vendored FreeRDP 二进制** → FreeRDP 自身 Apache-2.0 允许闭源分发（带 NOTICE），**但我们没有 FreeRDP 依赖，不需要**。

---

## 8. 取证方法与可复现

- 目录树：`GET https://api.github.com/repos/AITNR/Yomori/git/trees/main?recursive=1` → **343 条**，`truncated:false`（本会话实测）。
- 单文件：`GET https://api.github.com/repos/AITNR/Yomori/contents/<path>` → base64 解码（**注意**：`raw.githubusercontent.com` 在本会话 **fetch failed**，只能用 Contents API）。
- 许可证：`LICENSE` 抓取确认为 GPL v3。
- 本文引用的对方文件清单（全部实际读取，非推测）：
  `LICENSE`、`winui3/docs/rdp-embed-freerdp-plan.md`(23460 字符)、`winui3/docs/rdp-avc444-aux-drop-plan-2026-11-14.md`(18593)、`winui3/docs/rdp-hardware-decode-implementation-2026-10-31.md`(1964)、`winui3/docs/rdp-cpu-followup-2026-10-31.md`(3547)、`winui3/docs/known-limits-and-roadmap.md`(2000)、`winui3/docs/embed-occlusion-fix-plan-2026-11-08.md`(13297)、`winui3/src/Yomori.RdpNative/ycn_rdp.h`(14870)、`ycn_dirty_snapshot.h`、`ycn_dirty_snapshot.c`(6284)、`ycn_dirty_pending.h`(2105)、`ycn_dirty_stats.h`、`ycn_dirty_stats.c`(2964)、`winui3/src/Yomori.App/Services/RdpDirtyPathStats.cs`(6502)、`RdpD3DRenderer.cs`(30068)、`winui3/native-deps/patches/freerdp-3.32.0-ycnz-keyframe-gate.patch`(3898)。

---

## 9. 边界遵守自查

未改本仓库任何产品代码/脚本/配置/版本号；未碰 `dist\stable\SoulLink-Stable.exe`（PID 13392）；未使用 A1（session 2）/A4（session 3）；未停/启任何进程；未弹窗；未改注册表；未发布、未碰 COS/latest.json；未执行任何 git 写操作（只 `git status`/`git log` 只读）；`bin/`/`obj/`/`dist/` 未改动；未在 C 盘新建任何东西；未新建团队、未调用 `dsh_team_create`；**未把任何 GPL 代码复制进本仓库**（§7.2）。外网仅 GitHub 公共 API 只读。唯一写入 = 本文档 + `docs/research/README.md` 一行登记。
