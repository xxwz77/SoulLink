# SimpleRemote 单窗口多标签源码研究（2026-11-17）

> 项目：beckzhu/SimpleRemote，C# / WPF。只读研究，未启动 RDP、未修改功能代码。

## 核心结论

SimpleRemote 是一个主窗口中的 Dragablz TabablzControl 管理多个连接标签。
每个标签一个 RemoteTabControl，每个 RemoteTabControl 创建一个 RemoteControl_rdp，
RemoteControl_rdp 内包含一个 WindowsFormsHost 和一个 AxMsRdpClient7/9 ActiveX。

普通标签切换：

- 标签对象和 RDP ActiveX 继续存在。
- 不断开、不重新 Connect、不重新创建 ActiveX。
- 由 TabablzControl/WPF 切换选中内容，非选中标签不再作为当前可见内容显示。
- 不做多视口同屏平铺，不做标签之间的分辨率重协商。

因此它是“单窗口多标签、同一时刻一个可见 RDP”的参考，不是同屏自由分屏的参考。

## 官方源码

- [MainWindow.xaml](https://github.com/beckzhu/SimpleRemote/blob/master/SimoleRemote/MainWindow.xaml)
  - 主窗口内只有一个 TabablzControl。
- [MainWindow.xaml.cs](https://github.com/beckzhu/SimpleRemote/blob/master/SimoleRemote/MainWindow.xaml.cs)
  - AddTabItem 把 UserControl 放进标签，保留标签对象。
  - SelectedTabItem 只修改 SelectedItem。
  - RemoveTabItem 才移除标签并触发 Closed。
- [RemoteTabControl.xaml.cs](https://github.com/beckzhu/SimpleRemote/blob/master/SimoleRemote/Container/RemoteTabControl.xaml.cs)
  - Open 创建 RemoteControl_rdp 并 Grid.Children.Add。
  - 标签关闭时 _remoteControl.Release()，全屏窗口同时关闭。
  - 全屏才 Grid.Children.Remove -> FullScreenWindow.Content；退出全屏再移回。
- [RemoteControl_rdp.xaml](https://github.com/beckzhu/SimpleRemote/blob/master/SimoleRemote/Container/RemoteControl_rdp.xaml)
  - 一个 WindowsFormsHost。
- [RemoteControl_rdp.xaml.cs](https://github.com/beckzhu/SimpleRemote/blob/master/SimoleRemote/Container/RemoteControl_rdp.xaml.cs)
  - 一个标签一个 AxMsRdpClient7NotSafeForScripting 或 AxMsRdpClient9NotSafeForScripting。
  - 构造时设置 FormsHost.Child，连接后一直保留。
  - Release 只 FormsHost.Dispose。
  - 连接前设置 DesktopWidth/DesktopHeight。

## 独立窗口模式

SimpleRemote 也支持同进程 RemoteWinControl（MetroWindow）作为单独远程窗口。
它不是独立进程，也不是 SoulLink 已拒绝的可拖出主应用的统一分屏模型。
主窗口关闭时遍历并关闭其他窗口。

## 缩放实现

- 自适应分辨率：连接前用 Window.Width - 4、Window.Height - 34 设置远端宽高。
- 固定分辨率：从账号预设 DesktopSize 设置远端宽高。
- 没有 1Remote 那样的 500ms resize-end + 全局重入锁 + UpdateSessionDisplaySettings 机制。
- RDP 窗口尺寸变化时主要让 ActiveX 宿主变化，并用连接动画短时遮盖闪烁。
- 这套实现较简单，不应被理解为更完整或更安全的动态缩放方案。

## 对 SoulLink 最有价值的参考

1. 单窗口多标签应优先保证“选中一个、其他保活但不显示”，不要恢复多视口同屏平铺。
2. 标签切换不重连、不重建 ActiveX。
3. 尽量不在标签切换时重挂 ActiveX；由稳定的标签容器持有内容。
4. 连接/全屏/关闭三个生命周期分开，避免普通标签切换触发 Dispose 或分辨率重协商。
5. 若要借鉴 Dragablz，只借鉴标签内容保活和选中切换，不同时引入独立顶级窗口拆出功能。
6. 每视口透明保护层、原生标题栏、分屏缩放器都是 SoulLink 额外风险，SimpleRemote 并未验证这些组合。

## 不能据此得出的结论

- 不能证明 SimpleRemote 的多标签在 SoulLink 当前 NVIDIA + 双虚拟显示驱动环境绝对安全。
- 不能证明它支持同屏多窗口自由拖动。
- 不能证明它的简化缩放优于 1Remote 的防抖/互斥策略。
- 0x119 事故仍需转储分析，不能把任何第三方项目能运行当作安全证明。