# 1Remote 多窗口 / 分窗源码研究（2026-11-17）

> 只读研究。未启动任何 RDP、未修改功能代码。事故背景见 RDP-SPLIT-INCIDENT-20261117.md。

## 核心结论

1Remote 的“分窗口”不是在主窗口 Canvas 内同时摆放多个 RDP ActiveX，也不是同页四宫格。
它用 Dragablz TabablzControl 管理会话标签；拖出标签时创建新的顶级 WPF TabWindowView。
每个顶级窗口有自己的视觉树、标签栏和选中的协议 Host。移动的是已有 Host / ActiveX，不重新连接。
同一顶级窗口只显示选中的一个标签，不会把全部标签同时渲染为多个 RDP。
全屏是一个连接一个 FullScreenWindowView。

因此“1Remote 能分窗”不能直接证明“同一主窗口同时平铺多个实时 ActiveX + 多个原生覆盖层”安全。

## 官方源码

- [SessionControlService_OpenConnection.cs](https://github.com/1Remote/1Remote/blob/main/Ui/Service/SessionControlService_OpenConnection.cs)
  - Connect 创建/查找 AxMsRdpClient09Host，默认加入最后一个 TabWindowView。
  - FullScreenWindowView.Create(tabToken, host, tab) 创建单连接全屏窗口。
- [SessionControlService_WindowControl.cs](https://github.com/1Remote/1Remote/blob/main/Ui/Service/SessionControlService_WindowControl.cs)
  - MoveSessionToFullScreen / MoveSessionToTabWindow。
  - 先 TryRemoveItem(connectionId) 从旧视觉父容器移除，再移动到目标窗口。
  - 重连竞态下关闭过的缓存全屏窗口不得复用。
- [TabWindowViewModel.cs](https://github.com/1Remote/1Remote/blob/main/Ui/View/Host/TabWindowViewModel.cs)
  - InterTabClient.NewWindowHost() 创建新 TabWindowView，返回 Dragablz NewTabHost<Window>。
  - 拆窗/合窗由 Dragablz 移动现有标签内容。
- [TabWindowView.xaml](https://github.com/1Remote/1Remote/blob/main/Ui/View/Host/TabWindowView.xaml)
  - 真实顶级窗口和 Tabablz 布局，不是 Canvas 内嵌小窗口。
- [AxMsRdpClient09Host.xaml.cs](https://github.com/1Remote/1Remote/blob/main/Ui/View/Host/ProtocolHosts/AxMsRdpClient09Host.xaml.cs)
  - RDP Host、缩放、全屏和释放逻辑。

## 生命周期架构

SessionControlService 分别维护 connectionId -> HostBase、tabToken -> TabWindowView、connectionId -> FullScreenWindowView。

移动核心约束：

1. 旧窗口必须 Loaded；未 Loaded 时拒绝移动，避免 Loaded 回调覆盖新状态。
2. 先从旧 TabablzControl 删除 Host。
3. 再设置到目标窗口。
4. 设置新的 ParentWindow / HWND。
5. 从全屏返回标签时强制 FullScreen=false。
6. 清理空窗口和排队释放的 Host，避免重连竞态复用已关闭对象。

这些不是 UI 装饰，而是 ActiveX 重挂的安全前置条件。

## 缩放与远端分辨率

- AutoResize：允许按宿主尺寸更新远端桌面。
- Stretch：固定远端宽高，SmartSizing=true，本地拉伸。
- Fixed：固定远端宽高，不自动更新远端分辨率。

AutoResize 的整体机制：

1. 仅 AutoResize 响应窗口尺寸变化。
2. 尺寸真正变化才重置 500ms 单次 resize-end timer。
3. 窗口最小化/移动期间可禁用自动缩放。
4. 调用前有静态全局重入保护。
5. 等本机鼠标左键释放后再继续。
6. 再比较远端 DesktopWidth/Height 与目标尺寸，没变化不调用。
7. 最后才在 UI 线程执行 UpdateSessionDisplaySettings。

这套节流、互斥、最新尺寸比较必须作为整体理解；只复制最后的 API 调用是不安全的。

## 与撤回实验的本质差异

| 项目 | 1Remote | 已撤回的 SoulLink 实验 |
|---|---|---|
| 窗口单位 | 顶级 WPF Window | 主窗口 Canvas 内窗口框架 |
| 同时可见 RDP | 每个独立顶级窗口可选中一个 | 同一主窗口内多个实时 ActiveX |
| 标题栏 | OS / 顶级窗口窗口铬 | 额外原生标题栏 HWND 跟随 ActiveX |
| 分窗方式 | Dragablz 拆出/合并标签 | 自建多视口与多覆盖层 |
| 默认启动 | 一个标签窗口逐个加入 | 多选后立即全部平铺 |
| 分辨率更新 | resize-end + 全局互斥 + 等待鼠标释放 | 多个事件源可能并行触发 |
| 保护层 | 无 SoulLink 这种每视口原生透明保护层 | 每视口额外独立 HWND |
| 全屏 | 单连接专用 FullScreenWindow | 工作区窗口最大化模拟 |

## 对当前机器的安全结论

0x119 VIDEO_SCHEDULER_INTERNAL_ERROR 已证明当前组合不可继续试错。
1Remote 的架构不消除这些驱动风险；它仍有同进程多个 ActiveX 和 Host 重挂。
其最新源码中的多次竞态修复反而说明 Host 重挂需要高度谨慎。

在完成转储分析前：

- 不直接照搬 1Remote 的多 ActiveX 分窗。
- 不在本机再次启动撤回的多视口实验。
- 不把“1Remote 这样做”当作当前驱动组合安全的依据。

## 可借鉴的非高风险设计原则

1. 连接、视图、窗口三层分离；移动视图不等于重连。
2. 每个 Host 明确只有一个视觉父容器。
3. 拆窗先卸载旧父容器，再挂新容器，禁止同时双挂载。
4. 全屏和窗口状态必须由同一个会话控制器串行切换。
5. 分辨率更新采用全局串行队列、resize-end 防抖和尺寸去重。
6. 保存窗口布局按稳定 connection/account ID，不按显示顺序。
7. 更安全的研究方向是每连接独立进程/独立顶级窗口或单 ActiveX 预览模型，而不是恢复同进程多视口实时平铺。

## 尚未完成的取证

本机没有 WinDbg/CDB/kd/dumpchk。事件日志只确认 0x119 与 NVIDIA Event 153 的时间关联，
尚未完成内核栈和最终 faulting module 的分析，不能声称已证明某一段 SoulLink 代码是直接根因。