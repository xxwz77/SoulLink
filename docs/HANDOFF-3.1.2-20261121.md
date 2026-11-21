# SoulLink 交接文档 · 3.1.2（设置页样式修复）

> 时间：2026-11-21 12:40
> 版本：客户端 **3.1.2.0**
> 分支：`main` @ `f5b04a0`（已推送 origin）
> 性质：**纯 UI 样式修复**，无逻辑变更、无数据格式变更
> 触发：用户反馈两处界面与主题风格不符

---

## 0. 一句话

设置页的「旧数据迁移面板」和「两个数字输入框」原来不符合深色玻璃主题，
分别用了硬编码颜色和 WPF 默认控件模板，现已全部改用主题资源。

---

## 1. 问题一：迁移配置面板

### 1.1 原实现

```xml
<Border x:Name="LegacyDataPanel"
        Background="#22FFC46B" CornerRadius="8" Padding="12,10">
    <TextBlock Text="检测到旧版本的数据" Foreground="#FFFFC46B" />
```

### 1.2 三个问题

| 问题 | 说明 |
|---|---|
| **亮色主题下几乎看不见** | 浅黄底 + 浅黄字 |
| **不跟随主题切换** | 颜色硬编码，`ThemeManager` 换配色时它不跟着变 |
| **与周围玻璃卡片脱节** | 没有边框、圆角偏小、没有图标，像临时贴上去的 |

### 1.3 修法

```xml
<Border Background="{DynamicResource FieldBackground}"
        BorderBrush="{DynamicResource AmberBrush}"
        BorderThickness="1" CornerRadius="12">
    <TextBlock Text="&#xE7BA;" FontFamily="Segoe MDL2 Assets"
               Foreground="{DynamicResource AmberBrush}" />
```

`AmberBrush` 由 `ThemeManager.Apply()` 通过 `SetBrush(styles, "AmberBrush", ...)` 动态更新，
所以真正跟随主题。补充了 MDL2 警告图标（`&#xE7BA;`）和 12px 标题字号。

---

## 2. 问题二：两个数字输入框

### 2.1 原实现

```xml
<TextBox x:Name="BackupKeepBox" Width="64" VerticalContentAlignment="Center"
         TextAlignment="Center" LostFocus="BackupKeepBox_LostFocus" />
```

**完全没写 `Style`** → 用 WPF 默认 `TextBox` 模板：**白底 + 灰黑边框 + 黑字**。
在深色玻璃主题里像贴上去的补丁。

### 2.2 修法

套用项目已有的 `CompactTextBox`（本来就为这种小数字输入设计）：

```xml
<TextBox x:Name="BackupKeepBox" Width="58" Height="26"
         Style="{StaticResource CompactTextBox}" FontSize="12.5" />
```

`CompactTextBox` 提供的效果：

| 特性 | 实现 |
|---|---|
| 透明背景 + 圆角填充 | `FieldBackground` |
| 聚焦时青色光环 | `FocusRing` + `IsKeyboardFocused` 触发器 |
| 悬停边框提亮 | `IsMouseOver` 触发器 |
| 点右边空白也能聚焦 | `ScrollViewer` 设为 `HorizontalAlignment=Stretch` |

> `CompactTextBox` 的注释里记着一个历史坑：
> 早期 `ScrollViewer` 没撑满，可点击区域只有文字那么宽，
> 用户反馈「不能点右边」。现在的模板已经修好了。

尺寸调整为 `58×26 / FontSize=12.5`，与行内 `Caption` 文字视觉对齐。
两个框都补了 `ToolTip` 说明「留空 = 不限制」。

---

## 3. 验证

### 3.1 编译

```
客户端 3.1.2：0 错误 / 130 警告（与基线持平）
```

### 3.2 视觉验证

启动到设置页截图（`artifacts/ui-settings.png`）确认：

- 迁移面板：深色玻璃底 + 琥珀色边框 + MDL2 警告图标，与上方卡片风格统一
- 两个数字框（3 / 30）：与深色背景融合，不再是白底黑框
- 版本号显示 `v3.1.2`
- 三处占用统计正常显示

---

## 4. 产物

| 项 | 值 |
|---|---|
| 客户端版本 | **3.1.2.0** |
| dist 成品 | `dist/SoulLink.exe` · 3.28 MB |
| SHA256 | `897AE125C79C4AAE3D1A338558D787B4E5C18E54EE384E1FD12704D484795090` |
| 暂存 | `artifacts/dist-3.1.2/` |

### 4.1 发版时的一个插曲

第一次尝试发布时 `dist/SoulLink.exe` 被占用 —— 那是我自己 12:35 为验证配置恢复
而启动的实例（PID 173436）。

处理流程：
1. 确认该实例空闲（`boot.log` 最后写入 12:35:54，`last-command.log` 停在 11-20，无进行中任务）
2. `CloseMainWindow()` 无效（托盘程序常见）→ `Stop-Process -Force`
3. **文件锁有延迟释放** —— 进程退出后立即检查仍报占用，等 2 秒后才可写

> ⚠ 这个延迟值得记住：`build.bat` 覆盖失败时先别急着重跑，等两秒再试。

---

## 5. 本轮改动清单

| 文件 | 行数 | 内容 |
|---|---|---|
| `Views/SettingsPage.xaml` | +27/-14 | 迁移面板改主题资源；两个 TextBox 套 CompactTextBox |
| `SoulLink.csproj` | +1/-1 | 版本号 3.1.2 |

---

## 6. 教训

### 6.1 新增 UI 时先找现成样式

这次两个输入框的 bug 是**写 XAML 时忘了加 `Style`** 造成的。
项目里 `Theme/Styles.xaml` 已经有 `GlassTextBox` / `InlineTextBox` /
`CompactTextBox` 三种，选对就行 —— 不写 Style 等于用 WPF 默认模板，
在深色主题里必然穿帮。

### 6.2 颜色一律用 DynamicResource，不要硬编码

硬编码的 `#22FFC46B` 有两个后果：亮色主题下看不见、切主题时不跟着变。
项目已经有一套完整的主题色资源（`AmberBrush` / `CyanBrush` / `TextPrimary` 等），
且都被 `ThemeManager` 动态维护 —— 用它们就自动支持明暗切换。

### 6.3 UI 问题必须截图验证

这次改完后实际启动到设置页截图确认，肉眼一看就知道对不对。
只靠编译通过不能说明样式正确。

---

## 7. 边界约定（继续有效）

> **未授权不得触碰挂机测试版（`dist/stable/SoulLink-Stable.exe`）**
> **未授权不得使用 A1 / A4 账号**

本次操作全程未触碰挂机版（仍为 3.0.0，进程运行中）。