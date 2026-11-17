# 切页时隐藏 RDP 原生窗口：修复记录

> 2026-11-17 · 修复「切到其它导航页时 RDP 浮在最上层盖住界面」
---

## 问题

`RdpConsole` 内部是 `WindowsFormsHost`，它是一个**独立的 Win32 窗口**。
WPF 的 `Visibility.Collapsed` 只影响 WPF 合成层，**不会**隐藏这个原生窗口。

事故链：

1. `MainWindow.SetActivePage()` 切页只做 `page.Visibility = Collapsed`；
2. 原代码靠 `ConsolePage.IsVisibleChanged` 里的 `ViewportHost.Children.Clear()` 把控件摘出树规避；
3. 多画面改造为了「绝不重挂 ActiveX」删掉了这句；
4. 结果：切到启动台/设置时，RDP 继续浮在最上层，盖住左侧导航与页面内容。

## 修复

**不摘控件、不 Dispose、不重建 ActiveX**，只显式控制原生窗口的显示状态。

### 1. `RdpConsole.SetNativeVisible(bool)`

```csharp
public void SetNativeVisible(bool visible)
{
    if (_disposed) return;
    // 要藏的是 WindowsFormsHost 自己的窗口：真正浮在 WPF 之上的是它，
    // AxHost 只是它的子窗口。只藏子窗口，宿主那片黑底仍留在最上层。
    if (_host != null && _host.Handle != IntPtr.Zero)
        ShowWindow(_host.Handle, visible ? SW_SHOWNOACTIVATE : SW_HIDE);
    var child = NativeViewportHandle;
    if (child != IntPtr.Zero) ShowWindow(child, visible ? SW_SHOWNOACTIVATE : SW_HIDE);
}
```

要点：

- 用 `SW_SHOWNOACTIVATE`（4）而不是 `SW_SHOW`，避免恢复画面时抢焦点。
- 宿主 HWND 与 AxHost 子窗口一起处理，避免留下未重绘的黑底块。
- 句柄每次现取（`NativeViewportHandle`），重连后句柄变化也不会指向旧窗口。

### 2. `ConsolePage.SetHiddenForPageSwitch(bool)`

遍历所有 Tile：

- 隐藏时：`Chrome.Visibility = Collapsed` + `SetNativeVisible(false)`；
- 恢复时：先 `ApplyLayout()` 算出谁该可见，再对可见的调 `SetNativeVisible(true)`。

### 3. `SyncNativeVisibility()`

`ApplyLayoutCore()` 的**每一个出口**都会调用它：

- 单画面出口、无可见画面出口、网格/双列出口；
- 保证「布局说可见」与「原生窗口真的可见」始终一致。

自身有两道闸：`_pageHidden`（整页被切走）与 `!IsVisible`（页面整体不可见）。

### 4. 最小化

主窗口最小化同样不会隐藏原生子窗口，已在 `StateChanged` 里统一 `SW_HIDE` / 恢复。

## 为什么不用「摘控件」

| 方案 | 结果 |
|---|---|
| 摘控件再挂回 | ActiveX 在容器间反复进出，**正是 0x119 的高风险形态** |
| 显式 SW_HIDE（本次采用） | 控件树结构完全不变，只改原生窗口显示状态 |

## 验证状态

- Release 构建：**0 错误**，改动文件无新增警告。
- 布局纯函数检查：全部通过。
- **未做真实 RDP 验证**：需要启动主程序 + 连 A2/A3，尚未执行。

需要实测确认的三点：

1. 切到「启动台 / 设置」时，RDP 是否真的不再盖住导航；
2. 切回「中控台」时，画面是否正常恢复且不抢焦点；
3. 主窗口最小化 / 还原后，画面是否正常。
