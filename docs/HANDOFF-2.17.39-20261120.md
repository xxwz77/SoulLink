# SoulLink 交接文档 · 2.17.39（主界面不再自适应 + 滚动条消失）

> 时间：2026-11-20 20:05
> 版本：**2.17.39.0**（待定版；可信归档仍是 2.17.38）
> 性质：修主界面布局（用户第三次提到 UI 自适应，本次定位到根因并修复）
> 用户要求：**界面操作由用户自己测，助手不做界面测试**

---

## 1. 用户反馈

> 「主界面的框没有自适应大小了，绿框部分滚动条也没了」

截图里：整页内容溢出到窗口之外，左下角「自动化」面板被截断，左侧大框（绿框标注）
没有跟随窗口高度、也没有滚动条。

## 2. 根因：外层 Grid 的行高是 `Auto`

`Views/LaunchPage.xaml` 最外层：

```xml
<Grid x:Name="LayoutRoot">
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="3*" />
        <ColumnDefinition Width="18" />
        <ColumnDefinition Width="2*" />
    </Grid.ColumnDefinitions>
    <Grid.RowDefinitions>
        <RowDefinition Height="Auto" />   <!-- ← 三行全 Auto -->
        <RowDefinition Height="Auto" />
        <RowDefinition Height="Auto" />
    </Grid.RowDefinitions>
```

`Auto` 的含义是"由内容决定高度"。于是：

1. 外层 Grid 的高度 = **内容总高**（可能 1900px+），而不是窗口高度；
2. 左侧 `Border` 里的 `ScrollViewer` 拿到的可用高度是**无限的**；
3. WPF 的 `ScrollViewer` 在无限高度下**既不会出现滚动条、也不会裁剪内容** ——
   它把内容原样铺开，于是溢出到面板和窗口之外。

这就是"框没有自适应 + 滚动条没了"的完整解释。

## 3. 修复

### 3.1 外层行高改为 `*`（唯一一行）

```xml
<Grid.RowDefinitions>
    <RowDefinition Height="*" />
</Grid.RowDefinitions>
```

现在 Border 撑满窗口高度 → ScrollViewer 拿到**确定的高度**：
内容放得下就不滚动（区块按内容自适应），放不下才出现竖向滚动条。

### 3.2 右栏配套

* 会话列表补 `ScrollViewer.VerticalScrollBarVisibility="Auto"`
  （原来在 `*` 行里没有滚动策略，内容多会被裁掉 —— 看不见的会话等于丢失）；
* 日志区补 `MinHeight="80"`，保留 `MaxHeight="220"` 上限，
  避免窗口很高时日志区无限拉长。

### 3.3 给左栏命名 `LeftPanel`

便于后续按需定位（本次未改变任何视觉样式）。

## 4. 验证（无 UI，真实 WPF 布局）

复刻 `LayoutRoot` 的两种行定义，窗口 900×500、左栏内容约 1760px：

```
修复前（3 行 Auto）  border.H=1952  scrollviewer.H=1920  content.H=1920  → 无滚动(溢出) ✗
修复后（1 行 *）     border.H=500   scrollviewer.H=468   content.H=1920  → 可滚动 ✓
```

`border.H=1952` 远超 500px 窗口 —— 这正是用户截图里"内容跑出窗口"的直接证据。
修复后被正确约束在 500px，ScrollViewer 可用高度 468px，内容 1920px → 出现滚动条。

## 5. 产物

| 项目 | 值 |
|---|---|
| 版本 | 2.17.39.0 |
| `dist/SoulLink.exe` | 67,486,930 字节 · SHA256 `C381242CA223594D102CE968DF04B2D11FF7C292A0456BF6063700E6733FC6C2` |
| 注入代理 | 未改动（安装目录仍 2.17.38.0） |
| 可信归档 | `v2.17.38-reliable`（本版未归档，等用户确认后再说） |

## 6. 待办

1. 用户确认布局是否恢复正常（尤其小窗口下的滚动行为）。
2. 注入代理投送：2.17.38 归档时没有活代理进程，本版也未验证。
3. 2.17.39 未做可信归档。
