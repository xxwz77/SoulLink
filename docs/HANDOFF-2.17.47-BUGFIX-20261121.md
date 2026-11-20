# SoulLink 交接文档 · 2.17.47 补充（四个界面 bug 修复）

> 时间：2026-11-21 02:45
> 版本：**2.17.47.0**（`dist` 已更新，**挂机版仍是 2.17.45**）
> 性质：**修 bug**（不是性能优化），全部为用户实测反馈
> git：`9fb5702`（本文档之前的渲染优化见 `HANDOFF-2.17.47-20261121.md`）

---

## 0. 一句话现状

用户报的四个问题全部定位到根因并修复：

| # | 问题 | 根因 | 修复 |
|---|---|---|---|
| 1 | 密码框输入不可见、无光标 | 模板把 `Padding` 叠了两层 | 去掉 `Margin={TemplateBinding Padding}` |
| 2 | 弹窗按 Enter/Esc 没反应 | 分层窗口拿不到焦点 | 14 个对话框改非分层 |
| 3 | 更新报「找不到 7za.exe」 | 单文件发布的 `BaseDirectory` 是临时目录 | 改用 `Environment.ProcessPath` |
| 4 | 更新时未停止脚本就杀进程 | 读不到快捷键就降级杀 | 改从数据中心读 + 读不到就报错 |

---

## 1. 密码框输入不可见（问题 1）

### 用户描述
> 「输入不可见」「鼠标点击也没竖线，但是焦点框色已经出现」

### 根因

`Theme/Styles.xaml` 的 `GlassPasswordBox` 模板：

```xml
<ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}"
              VerticalAlignment="Center" />
```

样式里有 `Padding="12,9"`（上下共 18px），模板又用 `Margin` 叠了一次。
而 `Views/RemoteAdminPage.xaml:81` 写的是：

```xml
<PasswordBox x:Name="NewPwdBox" ... Height="27" ... />
```

**27 − 18（双层内边距）− 2（边框）= 7px** 可用高度。7px 装不下 13px 的字，
`ScrollViewer` 把内容整个裁掉 —— 于是"能聚焦、能打字，但一个字都看不见"。

### 修复
```xml
<ScrollViewer x:Name="PART_ContentHost" VerticalAlignment="Center" />
```
`Padding` 交给 `PasswordBox` 自己处理。

### ⚠ 排查教训
我写的最小复现程序里三种写法**都正常**，因为没复现 `Height="27"` 这个条件。
**高度充足时双层内边距不会把内容挤没** —— 复现必须带上真实约束。

---

## 2. 弹窗 Enter/Esc 无反应（问题 2）

### 用户描述
> 「更新提示没有快捷键」

### 根因（有硬证据）

`AppDialog.xaml` 的 `IsDefault="True"` / `IsCancel="True"` 写得是对的。
问题在 `AllowsTransparency="True"`。写了个最小程序实测：

| | `AllowsTransparency=False` | `AllowsTransparency=True` |
|---|---|---|
| 窗口 `IsActive` | **True** | **False** |
| 焦点元素 | `Window` | **`null`** |

**分层窗口拿不到激活状态，焦点是 `null`** → 键盘事件根本进不来 →
`IsDefault`/`IsCancel` 全失效。

### 修复
14 个对话框改 `AllowsTransparency="False"`。
其中三个靠 `Margin="16"` + `DropShadowEffect` 做浮起效果的
（`PasswordWindow` / `ReadmeWindow` / `ProcessPickerWindow`）
同步去掉边距与阴影，改由 DWM 画圆角。

> 主窗口在 2.17.47 早前那批已改过（详见 `HANDOFF-2.17.47-20261121.md` §4.3）。
> 那次修的是一处**代码与配置矛盾**：`MainWindow.xaml.cs:48` 注释明写
> 「必须非分层，否则 RDP 全黑」，XAML 却一直开着。

---

## 3. 更新报「找不到 7za.exe」（问题 3）

### 用户截图
```
× 失败：找不到 7za.exe（应随程序一起分发）
— 结束：成功 0，失败 2 —
```

而 `dist\7za.exe` 明明存在（1.2 MB）。

### 根因
`PublishSingleFile` 会把运行时解压到 `%TEMP%\.net\SoulLink\<随机>\`，
`AppContext.BaseDirectory` 指向**那个临时目录**，不是 exe 所在目录。
7za.exe 是 `AfterPublish` 复制到 `dist\` 的，所以在临时目录里找不到。

### 修复
`BgiUpdater.Find7za()` 改成优先用 `Environment.ProcessPath` 的目录：

```csharp
string exePath = Environment.ProcessPath ?? "";
if (exePath.Length > 0) dirs.Add(Path.GetDirectoryName(exePath));
dirs.Add(AppContext.BaseDirectory);   // 兜底
```

### ⚠ 注意
`SystemLauncher.ResolveSelfExe` 和 `BrokerInstall.FindScript` **早就这么处理了**
（注释还写着"单文件发布的 BaseDirectory 可能是临时目录"），只有 `Find7za` 漏了。
**新增读取 exe 旁文件的代码时，一律用 `Environment.ProcessPath`。**

---

## 4. 更新前必须停止当前脚本（问题 4）

### 用户要求（原文）
> 「更新BGI之前先按下停止当前脚本，然后才可以关进程，经行更新。
>   这是执行逻辑，必须要去执行的步骤。」

### 原来的行为
`BgiStartGuard.BeforeStartAsync` 读不到快捷键时**直接杀进程**：

```csharp
if (hotkey.Length == 0)
{
    result.StopMethod = "kill";
    await p.CloseProcesses(...);      // ← 跳过"停止脚本"，硬中断
    result.Message = "BGI 未设置停止快捷键，已直接关闭并确认退出";
}
```

现场日志正是这条。后果：BGI 里正在跑的脚本被硬中断，可能留下半截状态
（正在写的日志、正在做的动作、未收尾的流程）。

### 修复 A：直接从数据中心读（用户明确指出）

原来 `ReadCancelHotkey(exePath)` 内部绕三圈：

```
exePath → 遍历 Accounts 反查账号 → 从 exePath 推 User\config.json → 正则抓 F12
```

而**数据中心 `BgiDataCenter` 本来就持有每个账号的 `CancelTaskHotkey`**，
界面上显示 F12 用的就是它。现在改成：

```csharp
// BgiTaskPreparer.ReadCancelHotkeyFor(sessionId, bgiExePath)
会话ID → AccountStateCenter.All() 找 UserName
      → BgiDataCenter.Refresh(账号).CancelTaskHotkey
```

只有查不到账号时才退回按 `exePath` 读文件。**一份真相来源。**

### 修复 B：读不到就停，不降级
```csharp
result.StopMethod = "none";
result.Ok = false;
result.Message = "读不到 BGI 的「停止当前脚本」快捷键，已阻止关闭和启动。";
result.Message += "\n请在 BGI 设置里为「停止当前脚本」设置一个快捷键（如 F12）后重试。";
return result;   // ← 不执行任何关闭动作
```

### 最终执行顺序（不可跳过任何一步）
```
1. 读快捷键（数据中心）
2. 读不到 → 报错停止，什么都不关
3. 发快捷键「停止当前脚本」
4. 等确认脚本真的停了（10 秒超时）→ 超时则中止
5. 关闭 BGI 进程
6. 确认全部退出（15 秒超时）→ 超时则中止
7. 才允许启动 / 更新
```

### 修复 C：顺带修的隐患
`ReadCancelHotkey` 的账号匹配循环里，未绑定账号的 `GetPath()` 返回 `""`，
若传入的 `exePath` 归一化后也是空，`"" == ""` 就命中该账号并返回它空的快捷键。
已加 `if (accountPath.Length == 0) continue;`。

---

## 5. 产物

| 项目 | 值 |
|---|---|
| 版本 | **2.17.47.0** |
| `dist/SoulLink.exe` | 67,500,304 字节 |
| SHA256 | `C7FD89AD53DB2A185883E88324165F1FED122EF72081281EC197049BFFF2DF2D` |
| `dist/7za.exe` | 1,292,288 字节（**必须随包分发**） |
| git | `9fb5702` |

**构建命令（必须照抄）：**
```bat
dotnet publish -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:DebugType=none -o dist --nologo
```

---

## 6. 待验证（需要用户手动操作，我无法自测）

1. **密码框**：远程管理页 → 新建用户 → 密码框能否看到圆点和光标
2. **弹窗快捷键**：任意弹窗 → Enter / Esc 能否关闭
3. **更新 BGI**：日志是否变成
   `[A1] 已停止当前脚本并关闭 BGI，进程已全部退出`
   而不是之前的「未设置停止快捷键，已直接关闭」
4. 若仍报「读不到快捷键」，`boot.log` 里会有 `ReadCancelHotkey:` 开头的
   诊断行，能直接看出是哪一步失败（会话没找到 / 数据中心读到空 / 配置文件不存在）

---

## 7. 教训

### 7.1 复现必须带上真实约束
密码框排查走了弯路：最小复现里三种写法都正常，
因为**没带 `Height="27"`**。20 分钟后才想到补上高度约束。
**规则：复现时把用户环境里的尺寸、数量、状态一并复制。**

### 7.2 有更直接的真相来源就别绕
快捷键问题我一开始去改 `ReadCancelHotkey` 的匹配逻辑和正则，
用户一句话点醒：「直接从数据中心读取不简单吗」。
**同一份数据只该有一个读取入口。**

### 7.3 分层窗口是这批 bug 的共同根源
`AllowsTransparency="True"` 同时引起：焦点丢失（快捷键失效）、
密码框渲染异常、以及更早发现的 RDP 黑屏。
**这个属性在 WPF 里代价很高，非必要不要开。**

### 7.4 单文件发布路径是高频坑
`AppContext.BaseDirectory` 在 `PublishSingleFile` 下指向临时解压目录。
本次是第 N 次踩（代码里已有两处正确写法可参考）。
**约定：读 exe 同级的文件，一律 `Environment.ProcessPath`。**

---

## 8. 与本次无关但仍在的已知问题

1. **深色主题下仍有约 24 个单核百分点未定位**（WPF 光栅化）
   —— 详见 `HANDOFF-2.17.47-20261121.md` §7
2. **测量环境被污染**：用户机器上原神长期占 198% 单核，绝对值不可信
3. `--identity=新名字` 首次启动 exit=1（2.17.44 交接文档已记录，既有 bug）
4. RDP Wrapper 状态见远程管理页（当前显示「已安装并生效」）
