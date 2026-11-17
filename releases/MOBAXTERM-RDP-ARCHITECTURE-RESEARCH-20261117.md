# MobaXterm 多标签 / 同屏 / RDP 缩放架构研究（2026-11-17）

> 只读研究。未启动 MobaXterm、未启动 RDP、未反编译代码。证据来自官方文档、
> 本机安装文件版本信息、PE 导入表与可见字符串；闭源实现细节仍有边界。

## 最重要结论

MobaXterm 不是 WPF，也不是 SimpleRemote 那种同进程 WindowsFormsHost 标签。
它是 32 位原生 Windows 程序，主程序 MobaXterm.exe 与 MobaRTE.exe 分离。

- MobaXterm.exe：主界面、标签、TSplitter、2/4 终端分屏、MultiExec、布局与缩放。
- MobaRTE.exe：原生 Delphi/VCL 运行时，含 RDP COM/ActiveX 类型、TFormMobaRDP、TFrameRDP、TPanel。
- RDP 使用 Microsoft MSTSCLib / IMsTscAx / IMsRdpClient，不是 FreeRDP。
- MobaRTE 有 -mobahwnd、IsChildWindow、ParentWindow 等嵌入宿主线索。

因此最符合证据的模型是：

主控原生进程 + 每会话独立运行时进程 + RDP 子窗口嵌入主控分配的面板 HWND。

它不是“每账号独立顶级窗口，可拖出主程序”，也不是“同进程 Canvas 内多个 WPF ActiveX”。

## 官方文档可确认的功能

- Tab interface：多个独立会话标签。
- Split modes：同窗口 2 个水平/垂直终端或 4 个终端，类似 screen。
- Detach tab：标签可拆出、重新附着或全屏。
- Multi-execution：同时显示所有终端并向全部终端发送命令。
- RDP Enhanced graphics：RDP 会话有单独图形设置。
- Flicker-free main window：针对终端缩放和按钮悬停闪烁的可选显示设置，官方提示会降低界面速度。

官方文档：
https://mobaxterm.mobatek.net/documentation.html#1_3
https://mobaxterm.mobatek.net/documentation.html#2_1_2
https://mobaxterm.mobatek.net/documentation.html#4_4

## 本机文件与 PE 证据

安装：D:\AAA挂机专区\远程本地多用户\MobaXterm

- MobaXterm.exe 26.5.0.5542，32 位原生程序。
- MobaRTE.exe，32 位原生程序。
- MobaXterm.exe 静态导入 Win32/COM/GDI/GDI+/Common Controls/IMM/Winsock。
- 没有静态导入 mstscax.dll 或 wfreerdp.dll；RDP 通过 MobaRTE/COM 路径。

MobaXterm.exe 可见字符串：

- TSplitter / TsSplitter / HSPLIT / VSPLIT
- BtnSplit4 / BtnSplitVert / BtnSplitHoriz / SplitModeResize
- IsOngletATerminal / ResizeTerminal
- MobaRDPTerminalServiceClient2

MobaRTE.exe 可见字符串：

- MSTSCLib_TLB / IMsTscAx / IMsRdpClient / TMsRdpClientNotSafeForScripting
- TFormMobaRDP1/2/3 / TFrameRDP / TPanel / TForm / VCL
- -mobahwnd / IsChildWindow / ParentWindow
- RDPConnect / RDPDisconnect / DesktopResize / SmartSizing / DesktopWidth / DesktopHeight
- RaiseRootWindow / WMMouseActivate / Mouse is over RDP frame

这些证据支持子进程内 RDP 子窗口嵌入，但未反编译，不能给出具体函数实现或消息协议。

## 与之前方案的本质差异

| 方案 | 进程 | 窗口/容器 | 是否受主应用约束 |
|---|---|---|---|
| SimpleRemote | 同进程 | 主窗口标签中的 WindowsFormsHost | 是 |
| 1Remote | 同进程 | 顶级标签窗口/全屏窗口 | 独立窗口可拆出 |
| 已拒绝的独立顶级窗口原型 | 多进程 | 每连接顶级窗口 | 否，用户不接受 |
| 已撤回 SoulLink 多视口实验 | 同进程 | Canvas 内多 ActiveX + 多覆盖 HWND | 是，但发生 0x119 事故 |
| MobaXterm（证据模型） | 主控 + MobaRTE 子进程 | 子窗口嵌入主控面板/标签/分隔器 | 是 |

## 对 SoulLink 的启示

如果未来继续，这是一个新的独立研究方向：

1. 每账号独立 RDP 子进程，ActiveX 永不离职子进程。
2. 子进程创建的是可嵌入的子窗口，不是可任意拖走的顶级窗口。
3. 主控给子进程一个稳定的宿主 panel HWND；标签/分屏只改变矩形和可见性。
4. 主控负责标签、Splitter、保护层和最小化，子进程只负责一个 RDP。
5. RDP 窗口消息与主控层级通过明确协议协调，类似 MobaRTE 的 raise 信号。
6. 缩放仍遵守 AutoResize/Stretch/Fixed；AutoResize 全局串行、防抖、去重。

## 尚未确认的部分

- 官方 Split 文案主要写 terminals，不能仅凭字符串证明任意 4 个 RDP 都可同屏交互。
- 不知道 MobaXterm 与 MobaRTE 的具体私有协议和重连时序。
- 不知道其 Enhanced graphics 开关对应哪些 RDP 内部选项。
- 尚未分析 SoulLink 0x119 转储，不能把 MobaXterm 架构直接认定为当前驱动环境安全。
- 未验证 MobaRTE 子窗口嵌入能否直接照搬到 .NET/WPF；WPF 空域和原生 VCL 容器不同。

本轮没有修改功能代码，没有启动任何验证程序。