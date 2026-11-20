# SoulLink 交接文档 · 2.17.48（远程管理输入不可见 + 配置组下拉读不到）

> 时间：2026-11-21 03:20
> 版本：**2.17.48.0**（已构建到 `dist`，**未**替换挂机版）
> 性质：**修 bug**，全部来自用户截图反馈
> git：本文档提交之前的工作树

---

## 0. 一句话现状

用户报的三件事全部定位到根因并修复，且**每一条都有复现 + 像素级验证**：

| # | 用户描述 | 根因 | 修复 |
|---|---|---|---|
| 1 | 「昵称、用户名不可见」 | `GlassTextBox` 模板把 `Padding` 叠了两层，27px 高的框可用高度算成 0 | 去掉模板里的 `Margin={TemplateBinding Padding}` |
| 2 | 「将右侧内容调大至整个框」 | 右栏写死 330，且整块不可滚 | 右栏 330→360 + 输入框 34 高 + 面板内部可滚 |
| 3 | 「未能从数据中心获取配置组名称」 | 目标用户下拉自动选中后**没写回节点参数**，配置组拿空账号读目录 | 选中即写回 + 读不到时分类说明原因 |

---

## 1. 昵称 / Windows 用户名不可见（问题 1 + 2）

### 用户描述
> 「昵称用户名不可见，将右侧内容调大至整个框。恢复输入可见性。」

### 根因（`Theme/Styles.xaml` 的 `GlassTextBox`）

```xml
<ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}"
              VerticalAlignment="Center" />
```

样式本身已有 `Padding="12,9"`，而 TextBox 内部还会把 `Padding` 应用一次到内容宿主，
模板里再叠一层 `Margin` 就是**第三层**内边距。`Views/RemoteAdminPage.xaml` 写的是：

```xml
<TextBox x:Name="NewNickBox" ... Height="27" FontSize="10.5" />
```

**27 − 18（模板叠的）− 18（控件自身的）− 2（边框）＝ 负数** → 内容宿主高度 0 →
文字和光标全被裁掉，只剩一个空的玻璃胶囊（焦点框却正常亮，能打字但看不见）。

### 实测证据（探针 `GlassTextBoxTemplateProbe`）

| 写法 | 框位置 | 文字像素范围 |
|---|---|---|
| 现状（模板带 Margin，27px） | y=8..35 | **无** |
| 去掉 Margin（27px） | y=51..78 | y=62..68 ✔ |
| 现状（34px + Padding 10,0） | y=194..228 | y=206..215 ✔ |
| 去掉 Margin（34px + Padding 10,0） | y=244..278 | y=256..265 ✔ |

> ⚠ 这是本项目**第 2 次**踩同一个坑：2.17.47 修的是 `GlassPasswordBox` 的同款写法。
> **规则：自定义模板里凡是 `PART_ContentHost`，一律不要写 `Margin="{TemplateBinding Padding}"`。**

### 顺带修的第二层：点不到输入框

原模板的 `ScrollViewer` 没写 `HorizontalAlignment`，默认不拉伸 —— 可点击区域只有文字那几像素宽，
点右边空白不进焦点。已补 `HorizontalAlignment="Stretch"`。

### 排版调整（`Views/RemoteAdminPage.xaml`）

- 右栏列宽 `330 → 360`，卡片占满整列（用户要的"调大至整个框"）；
- 四个输入框统一 `Height=34`、`Padding="10,0,10,0"`、`FontSize=12`，
  上下不再靠 `12,9` 硬塞；
- 右侧整块套 `ScrollViewer`：内容超高时可滚，不再把「创建用户」按钮顶出可视区；
- RDP Wrapper 目录框 `24 → 26` 并补 `Padding="8,0,8,0"`（同一原因，24px 也压不出字）；
- `ChangePasswordWindow` 的两个密码框同步改成 34 高（同款风险）。

### 验证

`tools/layout-check` 里的截图程序按真实内容区尺寸（1124×726）渲染：
昵称 `山客二号`、用户名 `shanke2`、密码圆点全部可见；
**深色与浅色两套主题各渲染一次，均正常**（浅色那张确认没有「深色文字压深色底」的问题）。

---

## 2. 配置组名读不到（问题 3）

### 用户描述
> 「未能从数据中心获取配置组名称」（图3：配置组名下拉只有一项「（未选择）」）

### 根因（`Views/FlowEditorWindow.xaml.cs` 的 `UserPicker`）

```csharp
string cur = node.Get(p.Key);
foreach (...) if ((it.Tag as string) == cur) { it.IsSelected = true; break; }
if (combo.SelectedIndex < 0 && combo.Items.Count > 0) combo.SelectedIndex = 0;   // ← 只改了界面
combo.SelectionChanged += ...                                                    // ← 事件在这之后才挂
```

新建节点时 `user` 是空串，界面默认选中「A2 山客」，但**参数里仍是空**。
紧随其后的「配置组名」用 `node.Get("user")`（空）去调
`BgiDataCenter.ListConfigs(user, "ScriptGroup")` → `Read("")` 直接返回
「未指定账号」的空数据 → 下拉永远只有「（未选择）」。

手动改目标用户也没用：`SelectionChanged` 里的 `Dispatcher.InvokeAsync(() => ShowPropPanel(node))`
而 `node` 是**方法参数**，闭包捕获的始终是同一个节点，面板根本没被重建。
（探针实测：手动把下拉切到 A2 后，属性面板里的配置组选项数仍是 1。）

### 修复（三处）

1. **默认选中即写回参数**，并立即重建属性面板，让配置组按新账号读目录；
2. **已保存账号不在候选里时原样保留**（新增一条同名项并选中）——
   绝不能悄悄改成列表里的第一个账号，那等于把用户的流程静默改到别人会话上执行；
3. **读不到必须说清为什么**：原来静默只剩「（未选择）」。现在按三种情况分别提示：

```
未能从数据中心获取配置组名称：账号「lummy」还没有绑定 BetterGI 路径，读不到配置组。
请用下面的「选择 BGI 程序…」指定。
```
```
未能从数据中心获取配置组名称：已读取 D:\AAA挂机专区\A2\BetterGI\User\ScriptGroup，但里面没有 *.json 配置组。
```

### 为什么是「A2 山客」而不是流程自己的账号

该节点类型在 `BgiDataDependency.NeedsBoundBgi` 里属于「依赖日志数据」，
下拉**只列已绑定 BGI 路径的账号**（2.17.45 定下的规则）。
所以流程绑在 `lummy`（未绑定）时，列表里只剩 A2 / A3 —— 这也解释了用户截图里
「目标用户是 A2，但配置组读不出来」：A2 是当前显示的默认值，参数里却是空的。

### 验证

新增 `tools/layout-check/GroupHintCheck.cs`（可重复执行）：

```
ok   未绑定 BGI 路径时给出明确原因
ok   原因里指出是路径未绑定
ok   已绑定账号时不显示告警
ok   已绑定账号时列出配置组（10 项）
ok   默认选中的目标用户写回节点参数（user=A2）
ALL PASSED
```

同时回归了既有 3 个探针，全部 `ALL PASSED`：
`FlowEditorCheck`（END 日界 / 今日运行）、`PathPickerCheck`（程序路径）、
`BgiDataCenterCheck`（每账号独立读目录）、`FlowEditorVisualCheck`（状态灯像素）。

---

## 3. 产物

| 项目 | 值 |
|---|---|
| 版本 | **2.17.48.0** |
| 构建 | `dist\SoulLink-2.17.48.exe` · 67,500,340 字节 |
| SHA256 | `6F4BBE5D181A007AD895CE6D76731006C73A5E7DCABB5A4030872DA5621765AA` |
| 暂存目录 | `artifacts\dist-2.17.48\`（含 7za.exe / tools / broker 脚本） |
| `dist\SoulLink.exe` | **仍是 2.17.47**（被正在运行的实例占用，未能覆盖） |

**构建命令（照抄）：**
```bat
dotnet publish -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:DebugType=none -o artifacts\dist-2.17.48 --nologo
```

> 本次 `dotnet publish -o dist` 直接失败（MSB4018 GenerateBundle）——
> 因为 `dist\SoulLink.exe` 正被 PID 135920 打开。文件锁不会报"被占用"，
> 而是打包阶段重试后抛 `GenerateBundle` 异常。
> **下次发布前先关掉正在跑的实例，或直接输出到暂存目录再拷。**

---

## 4. 待验证（需要用户手动操作）

1. 远程管理页 → 新增用户：昵称 / 用户名 / 两个密码框都能看到字与光标；
2. 点一下输入框右侧的空白，也能进焦点（原来只有文字那几像素能点）；
3. 窗口缩到最小高度时，右侧面板可上下滚动，「创建用户」按钮仍点得到；
4. 流程编辑器 → 拖「配置组状态」：目标用户默认就是流程自己的账号（若已绑定），
   配置组下拉直接列出该账号的所有配置组；
5. 目标用户是未绑定账号时，下拉下方应出现橙色说明，写明缺什么。

---

## 5. 教训

### 5.1 同一个模板缺陷会以不同控件出现
`TextBox` 的 `PART_ContentHost` 和 `PasswordBox` 的写法**不是同一段代码**，
2.17.47 只修了密码框，文本框继续带着同一个 bug 发布了两个版本。
**规则：修模板类 bug 时，把所有 `PART_ContentHost` 一起看一遍。**

### 5.2 "界面显示"和"实际生效值"必须同源
下拉默认选中却没写回参数，是典型的"看起来选好了、其实没选"。
**规则：任何自动选中/自动填充，都要在同一个代码块里把值写进数据模型，并断言一遍。**

### 5.3 静默降级比报错更贵
配置组读不到时只留一个「（未选择）」，用户只能来问"为什么读不到"。
现在把三种原因（没选账号 / 没绑路径 / 目录是空的）分别写出来，
**这一条本身就省掉一整轮答疑**。
