# SoulLink RDP 多窗口研究与失败总结（2026-11-17）

> 本文供新会话快速接手。功能基线见 HANDOVER.md；事故原始证据见
> RDP-SPLIT-INCIDENT-20261117.md；各项目详细源码研究见对应专题文档。

## 一、最终状态

- 当前正式开发基线：v1.13.2，附加提交 0d12968（连接/注销动画不遮导航）。
- dist/ 为开发发布目录，不含分屏实验；当前 exe 含 0d12968 后续小修复，与 v1.13.2 标签归档 exe 哈希不同。
- dist/stable/ 为生产挂机目录，始终未修改，重启后正在运行 SoulLink-Stable.exe。
- 分屏、Canvas 内部窗口、独立顶级窗口、1:1 分屏实验源码均已撤回。
- tools/rdp-layout-validation、tools/rdp-process-windowing 等仅保留作证据，禁止运行。
- 撤回后的主工程 Release 构建通过，0 错误；未因此启动任何应用或 RDP。

## 二、用户真正想要的交互

1. 单窗口：多个账号标签，同一时刻显示一个 RDP；点击标签立即切换，连接保活。
2. 分屏：多个账号在主应用内同时显示，可缩放、可自由移动，不受独立顶级窗口拖出应用的影响。
3. 主应用最小化时，其内部 RDP 一起隐藏；恢复后回到原布局。
4. 每个账号缩放模式必须保持：跟随窗口 / 拉伸（固定远端分辨率）/ 原大小（固定远端分辨率）。
5. 1:1 按远端实际像素显示，优先扩大内部窗口，到工作区边界后才滚动。
6. 每个账号位置、尺寸、Z 序按账号保存，下次无需重排。
7. 保护层不能失效：只有选中账号接收输入，首次点击不透传，移出/失焦/切换时重锁。

独立顶级窗口方案因可拖出 SoulLink、主控与子窗口难以形成可靠统一生命周期，用户明确不接受，已停止。

## 三、分屏实验为何失败

### 3.1 UI/生命周期问题（已确认）

- 同进程多 ActiveX 在 Grid/Canvas 之间反复重挂，出现“元素已是另一个元素的逻辑子元素”。
- 单窗口/分屏切换不断清空并添加容器，保护层的 WPF IsVisible 与 Win32 IsWindowVisible 脱节。
- 普通 Popup 与自有 HWND 导航互相抢 Z 序，连接/注销动画会覆盖导航。
- 保护层多次出现“请求显示但原生不可见”、闪烁、切页后才恢复。
- 调用栈证实 ConsolePage.Deactivated 曾把刚显示的保护层 Hide。
- 注销后重建 ActiveX 与普通重连路径不同，复用保护层 HWND 不可靠。
- 自建原生标题栏与 ActiveX 边框拖动涉及 DPI、命中测试和 Z 序，复杂度高。

### 3.2 严重系统事故（已确认证据，根因尚未完成内核分析）

- 2026-11-17 15:09:29 意外关机，15:10:35 重启。
- 15:09:17 NVIDIA nvlddmkm Event 153，GPUID 100。
- Bugcheck 0x119 VIDEO_SCHEDULER_INTERNAL_ERROR。
- 转储 C:\Windows\Minidump\111726-18203-01.dmp。
- 机器同时有 NVIDIA、GameViewer Virtual Display、OrayIddDriver。

用户态代码不能直接制造内核 Bugcheck。当前只能确认时间相关和高风险触发组合，不能声称已证明某一行代码是直接根因。
多 ActiveX 重挂、每视口原生覆盖 HWND、UpdateSessionDisplaySettings 重协商都可能触发显示驱动缺陷。

## 四、保护层迭代的教训

1. 不要靠不断增加“连接完成/动画结束/标签创建/句柄变化”触发器修补可见性。
2. 先记录 requested、NativeVisible、Owner、anchor、IsActive 和 Show/Hide 调用栈。
3. WPF Window.Show/Hide、Win32 ShowWindow、SetWindowPos 不能无约束混用。
4. 真实 Owner + 非 Topmost 与主动 Hide 是不同职责：层级与输入锁定要分开处理。
5. 新建、重连、注销后新建是三个不同生命周期，不能假设走同一回调。
6. 自动测试通过不等于真实 ActiveX + 多显示驱动环境安全。

当前用户实测：基础浮层和后台重连保护曾确认可用；注销后重开最后修复未得到完整验收。
因此新会话不得把保护层写成“全部场景已解决”。

## 五、1Remote 研究结论

- 同进程，一个 connectionId 一个 Host/ActiveX。
- 默认在顶级 TabWindowView 中用 Dragablz 管理标签。
- 拖出标签时创建另一个顶级 WPF TabWindowView，不是主窗口 Canvas 内部分屏。
- 移动已有 Host，不重新连接；先旧容器移除，再挂入新窗口。
- 全屏是单连接 FullScreenWindowView。
- AutoResize 使用 500ms resize-end、全局重入锁、等待鼠标释放、尺寸去重，最后才 UpdateSessionDisplaySettings。

可借鉴：连接/视图/窗口分离、单父容器、串行全屏切换、缩放防抖互斥。
不可照搬：同进程多 ActiveX + Host 重挂并未证明当前机器安全。

## 六、SimpleRemote 研究结论

- beckzhu/SimpleRemote，C# / WPF，主窗口一个 TabablzControl。
- 每标签一个 RemoteTabControl / RemoteControl_rdp / WindowsFormsHost / ActiveX。
- 普通标签切换只改 SelectedItem，不断开、不重建、不重新协商分辨率。
- 非选中标签不作为当前可见内容显示，但对象保活。
- 只有显式全屏才把控件从 Grid 移到 FullScreenWindow，关闭标签才 Dispose。
- 没有 SoulLink 所需的同屏自由分屏，也没有每标签额外保护层 HWND。

最适合参考的是“单窗口多标签、一个可见 RDP、其他保活”的稳定生命周期，不是分屏。

## 七、MobaXterm 研究结论

- 闭源 32 位原生 Windows 程序，主程序 MobaXterm.exe + MobaRTE.exe。
- MobaXterm.exe 负责标签、TSplitter、2/4 终端布局、MultiExec、分隔器尺寸。
- MobaRTE.exe 是 Delphi/VCL 原生运行时，包含 Microsoft MSTSCLib / IMsTscAx / IMsRdpClient。
- 不是 FreeRDP，也不是 WPF WindowsFormsHost。
- 可见 -mobahwnd、IsChildWindow、ParentWindow、TFormMobaRDP、TFrameRDP、TPanel 等嵌入宿主线索。
- 另有 RDPConnect、DesktopResize、SmartSizing、DesktopWidth/Height、WMMouseActivate、RaiseRootWindow。

最符合证据的模型：

主控进程 + 每会话独立运行时子进程 + RDP 子窗口嵌入主控分配的面板 HWND。

这不同于独立顶级窗口：子窗口仍受主应用约束，能够参与标签、分隔器、最小化和恢复。
这是后续唯一值得单独评估的多窗口新方向，但 WPF 与 VCL 容器不同，不能直接假设安全。

官方 Split 文档主要描述 terminals，不能据此证明任意四个 RDP 都可同屏交互。

## 八、三种架构对比

| 项目 | 进程 | RDP 宿主 | 多标签 | 同屏 | 对 SoulLink 的参考价值 |
|---|---|---|---|---|---|
| 1Remote | 同进程 | 顶级标签窗口，可拆出/全屏 | 是 | 非主窗口内多视口 | Host 生命周期、缩放互斥 |
| SimpleRemote | 同进程 | 主窗口标签内 WindowsFormsHost | 是 | 否 | 单画面标签保活、切换不重连 |
| MobaXterm | 主控 + MobaRTE 子进程 | 证据指向子窗口嵌入原生面板 | 是 | 至少终端 2/4 同屏 | 进程隔离且受主应用约束的嵌入模型 |

## 九、后续建议与禁令

优先选择：

A. 先做 0x119 转储内核栈分析，确定 faulting module 和显示驱动路径。
B. 若继续功能，优先稳定单窗口多标签保活（SimpleRemote 模型），避免分屏。
C. 若未来仍要同屏，再单独研究 MobaXterm 式“子进程内嵌子窗口”，先做纯 Win32/无 RDP 原型，不能直接恢复旧代码。

禁止：

- 禁止运行 tools/rdp-layout-validation、tools/rdp-process-windowing 的旧程序。
- 禁止在当前机器“再试一次”同进程多 ActiveX 分屏。
- 禁止把第三方软件能实现等同于当前机器和 SoulLink 组合安全。
- 禁止操作 A1/A4；测试只用 A2/A3，且必须明确授权。
- 禁止未经明确授权修改 dist/stable/。

## 十、相关文档

- HANDOVER.md：项目完整交接。
- RDP-SPLIT-INCIDENT-20261117.md：事故证据。
- 1REMOTE-WINDOWING-RESEARCH-20261117.md：1Remote 分窗研究。
- SIMPLEREMOTE-TABBED-RDP-RESEARCH-20261117.md：SimpleRemote 标签研究。
- MOBAXTERM-RDP-ARCHITECTURE-RESEARCH-20261117.md：MobaXterm 架构研究。