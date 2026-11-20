# SoulLink 交接文档 · 2.17.35（按键捕获"点了没反应"的真正原因）

> 时间：2026-11-20 18:30
> 版本：**2.17.35.0**（待定版；可信归档仍是 2.17.19）
> 性质：修 2.17.34 的按键捕获控件无法录入
> 用户要求：**界面操作由用户自己测，助手不做界面测试**

---

## 1. 用户反馈

> 「光标放上去会变手指，点击无反应，无法录入」

## 2. 根因：用 Border 当输入控件是错的

2.17.34 用 `Border` 做捕获框，只设了 `Focusable = true`：

```csharp
var keyBox = new Border { Cursor = Cursors.Hand, Focusable = true, ... };
keyBox.MouseLeftButtonDown += (_, __) => { listening = true; keyBox.Focus(); ... };
```

问题：

1. **`Border` 是 `Decorator`，不是 `Control`**。即使 `Focusable=true`，
   `Focus()` 也经常拿不到**键盘焦点**（`IsKeyboardFocused` 仍为 false）；
2. `PreviewKeyDown` 只在元素持有键盘焦点时才会收到事件 →
   **按键全部落到别的元素上**，捕获逻辑一次都没执行；
3. 光标、点击都正常（所以用户看到"变手指、点了没反应"这种诡异组合）。

## 3. 修复

### 3.1 换成 `TextBox`（`Views/FlowEditorWindow.xaml.cs`）

`TextBox` 天生可聚焦、能吃键盘输入，是做"录入框"最稳的控件。

```csharp
var keyBox = new TextBox
{
    Style = (Style)FindResource("InlineTextBox"),
    IsReadOnly = false,      // ★ 见 3.2
    Cursor = Cursors.IBeam,
    ...
};
```

### 3.2 不用 `IsReadOnly`

第一版改成了 `IsReadOnly = true`，但只读 TextBox 在部分场景下**不参与点击聚焦**
（又要复现"点了没反应"）。改为**可编辑 + 全面拦截输入**：

```csharp
keyBox.PreviewTextInput += (_, ev) => { ev.Handled = true; };      // 打字
keyBox.AddHandler(CommandManager.PreviewExecutedEvent, ...);       // 粘贴/剪切
```

内容只能由捕获逻辑写入，用户改不了，但焦点和按键事件都能正常到达。

### 3.3 点击走 `PreviewMouseLeftButtonDown`

`MouseLeftButtonDown` 可能被 TextBox 内部标记为已处理（用于定位插入符），
冒到外层就没了。改在 Preview 阶段拦，并 `ev.Handled = true` 防止进入编辑态。

同时补 `GotKeyboardFocus`：即使没走点击（Tab 进入）也能开始监听。

```csharp
keyBox.Focus();
Keyboard.Focus(keyBox);   // ★ 必须显式拿键盘焦点，否则 PreviewKeyDown 不触发
```

## 4. 验证（真实 WPF 窗口，不是推测）

写了一个最小 WPF 程序，**逐字复刻**编辑器的 KeyCapture 实现，
然后程序化点击 + 注入 `CTRL`、`A` 两个按键事件：

```
START_LISTEN focused=True isFocused=True     ← 键盘焦点确实拿到了
AFTER_CLICK  focused=True                    ← 点击后仍保持焦点
KEYDOWN LeftCtrl
KEYDOWN A
CAPTURED=CTRL+A                              ← 组合键正确拼出
FINAL_CAPTURED='CTRL+A'
```

对比 2.17.34 的 Border 方案：`focused` 一直是 False，`KEYDOWN` 一条都不会出现。

## 5. 产物

| 项目 | 值 |
|---|---|
| 版本 | 2.17.35.0 |
| `dist/SoulLink.exe` | 67,482,714 字节 · SHA256 `87D956BA4C0793E012C317E72A780915FC5D860CC7E158C01F3DEC67201F345C` |
| 注入代理 | 未改动（仍是 2.17.21） |
| 可信归档 | 仍是 `v2.17.19-reliable` |

## 6. 待办

1. **UI 自适应**（用户多次提到）：小窗口卡片挤压、按钮点不到。仍未动。
2. 符号键补丁在代理里尚未生效（见 2.17.34 交接第 6 节）。
3. 2.17.20 ~ 2.17.35 均未做可信归档。
