# SoulLink 新会话开场白（直接复制到新会话）

---

接手 SoulLink 项目。请按顺序读这几份，再动手：

1. `docs/HANDOFF-2.17.38-20261120.md` ← **最新交接（鼠标选点：标记/回填/大图，必读）**
   然后 `docs/HANDOFF-2.17.37-20261120.md`（鼠标选图点选+自动换算）
   然后 `docs/HANDOFF-2.17.36-20261120.md`（修饰键单录 + Esc 可录）
   然后 `docs/HANDOFF-2.17.35-20261120.md`（按键捕获修焦点）
   然后 `docs/HANDOFF-2.17.34-20261120.md`（按键捕获录入）
   然后 `docs/HANDOFF-2.17.33-20261120.md`（通道门控：先建通道再判条件）
   然后 `docs/HANDOFF-2.17.32-20261120.md`（失败也封盘的 bug + 今日完成按钮）
   然后 `docs/HANDOFF-2.17.31-20261120.md`（今日成功/失败次数）
   然后 `docs/HANDOFF-2.17.30-20261120.md`（等待注入通道：黄灯+保持提示）
   然后 `docs/HANDOFF-2.17.29-20261120.md`（END 今日完成橙色 + 可清除）
   然后 `docs/HANDOFF-2.17.28-20261120.md`（状态灯交替）
   然后 `docs/HANDOFF-2.17.26-20261120.md`（快捷动作按钮区分）
   然后 `docs/HANDOFF-2.17.25-20261120.md`（新建即自动跑 + 按钮分离）
   然后 `docs/HANDOFF-2.17.24-20261120.md`（状态灯四色 + 失败卡死修复）
   然后 `docs/HANDOFF-2.17.22-20261120.md`（状态灯/手动触发显示）
   然后 `docs/HANDOFF-2.17.21-20261120.md`（模拟按键 SendInput 修复）
   然后 `docs/HANDOFF-2.17.20-20261120.md`（手动运行锁存修复）、
   `docs/HANDOFF-2.17.19-20261120.md`（RDP 控件修复 / 定版轮）
   ★ 重点看第 2 节（根因）、第 3 节（A/B 证据）、第 7 节（恢复节点）
2. `docs/HANDOFF-2.17.18-20261120.md` ← 上一轮，**第 4 节是仍未解决的白带问题**
3. `VERSIONING.md` ← 版本固定与回退规则（当前定版 2.17.19）
4. `HANDOVER.md` ← 项目结构、测试红线、历史事故
5. `HANDOFF.md` ← RDP / 代理技术结论

---

## 当前基线（2026-11-20 16:00 定版）

| 项目 | 值 |
|---|---|
| 标准版 | **2.17.39.0（待定版）**，`dist/SoulLink.exe`（修主界面布局） |
| 可信版本 | **2.17.38.0** · 归档 `E:\SoulLink-SOURCE-ARCHIVE\v2.17.38-reliable\` |
| 可信版本 | **2.17.19.0** · `dist/SoulLink-2.17.19.exe` · 归档 `v2.17.19-reliable` |
| 大小 | 67,487,060 字节 |
| SHA256 | `7ED66631080A4028FE71D962B4036F507879ECA7ED68051A7B0E20198F4120D4` |
| **恢复节点** | `E:\SoulLink-SOURCE-ARCHIVE\v2.17.38-reliable\`（216 条哈希全通过） |
| 上一可信版 | `E:\SoulLink-SOURCE-ARCHIVE\v2.17.19-reliable\` |
| 回归版备份 | `dist/SoulLink-2.17.17-before-ocxfix.exe`（**坏版本，别用**） |
| 挂机版 | `dist/stable/` 仍 **2.17.4.0**，**全程未改动** |
| 项目路径 | `E:\Documents\deepseek-harness\SoulLink` |

⚠ 只改 `dist/SoulLink.exe`；`dist/stable/` 未经用户授权不要动。

---

## 本版（2.17.19）修的什么 —— 一条根因，别读错

**现象**：控制台页 RDP 区域全黑 + 红字「无法创建 RDP 控件。系统可能缺少远程桌面
ActiveX 组件。控件创建后 OCX 为空（{A0C63C30…}）」，每 2 秒重试一次。

**根因**：2.17.17 的「延迟挂载」让 `WindowsFormsHost` 在创建控件时仍是
`Visibility=Collapsed` 且没有父窗口，而 WinForms 的 `Control.CreateControl()`
对**不可见**控件是空操作 → AxHost 窗口不创建 → `AttachInterfaces()` 不被调用
→ `GetOcx()` 恒为 null。**全程不抛异常**，所以日志只有那句「OCX 为空」。

**修法**：`Controls/RdpConsole.cs` 的 `EnsureHost()` ——
先 `MountHostForCreation()`（挂视觉树 + Visible + UpdateLayout），
再 `_host.Child = ax`，最后 `EnsureCreated()`。

**系统侧从来没问题**：v9/v10/v11 三个 CLSID 一直已注册，`mstscax.dll` 存在；
独立进程直接 `CreateInstance` 三个 CLSID 全部成功。**不要再往"缺组件/重装 RDP"方向排查。**

---

## 必读教训（本会话新增）

1. **WinForms `CreateControl()` 对不可见控件是空操作**，不抛异常、不留日志。
   见到「创建后 XX 为空」先查「控件当时是否可见、是否有父窗口」。
2. **多候选回退逻辑必须把每条失败都带出来**（CLSID + HRESULT）；
   只留最后一条会掩盖真实原因（v9/v10 的错误被 v11 的"OCX 为空"覆盖）。
3. **先做时间线对账**：本次靠「12:36 改代码 / 12:48 正常 / 14:54 起报错」
   直接锁定回归来源，比通读代码快得多。
4. **用真实类做对照探针**（反射调用私有方法、控制变量 A/B）比读文档猜快得多。

---

## 仍未解决的问题（别当成已修好）

1. **窗口内侧 8px 灰/白粗框**（2.17.18 遗留，本版未碰）
   已压住但根因未坐实。`PrintWindow(PW_CLIENTONLY)` 能复现、
   `PW_RENDERFULLCONTENT` 复现不出来 → 是客户区内未覆盖的残留色。
   下一步最小排查点：读 `HwndSource.RootVisual` 的实际 `RenderSize`、
   `HwndTarget` 客户矩形与 `WS_THICKFRAME` 有效区域的区别。
   **不要再用更多定时/焦点重绘当根除方案。**
2. `tools/SoulLink.BrokerSetup.exe` 仍是 2.8.8 时代版本（FileVersion 1.0.0.0），
   与 2.17.19 的兼容性未验证。
3. RDP Wrapper「停 UmRdpService → 停 TermService → 启动」的重启顺序仍无真机端到端验证。

---

## 必须知道的操作陷阱

1. **`MainWindowHandle` 返回的是屏幕外热键辅助窗**（237x39 @ -32000），不是主窗口。
   正确做法：枚举该进程所有 `HwndWrapper` 窗口，取可见且面积最大的
   （排除 `r.L > -10000`）。
2. **单实例互斥体**（`Global\<身份>.SingleInstance.v1`）会静默杀掉第二个实例。
   要做对照实例：用不同**文件名**（身份由文件名派生）或 `--identity=名字` 完全隔离。
3. **主窗口会收进托盘**：截图脚本找不到窗口报 "NO MAIN WINDOW"，先 `ShowWindow(h, 9)`。
4. **`SetForegroundWindow` 可能失败**（前台锁），脚本要打 `fg=True/False` 核实。
5. **"问题消失"要先确认不是被盖住了**（上一轮有过白带被 10px 粗边框压住的假象）。
6. **PowerShell 的 `$Pid` 是只读自动变量**，参数别叫 `-Pid`。
7. **PS 5.1 的 `Add-Type` 引 `System.Drawing` 会编译失败**，用 `[Bitmap]::new()`。
8. **`build.bat` 里有 `rmdir /s /q dist`**，会把 `dist` 下所有历史 exe 一起删掉
   （含 `SoulLink-2.17.17-before-ocxfix.exe` 这类备份）。
   要发布请按 `VERSIONING.md` 里的命令 publish 到临时目录，再替换单文件。
9. **不要自己另开实验** —— 用户明确说过：
   > "你按我的步骤来，听我指挥，你的实验麻烦而且无效。"
   > 先问、再做；用户给了方向就严格照做。

---

## 纪律

1. 不修改/回退 `dist/stable/`，除非用户明确授权
2. 不操作 A1/A4；测试用 A2/A3
3. 按 PID + SessionId + 完整路径核对后才操作任何进程
4. 不往 RDP 画面上盖任何 WPF 浮层（会让画面整块黑）
5. 不给 `WindowsFormsHost` 设固定尺寸
6. 大文件用 `edit` 精确改，不要整文件 `write` 覆盖
7. **改完必须跑起来看日志/截图验证**，不能只看编译通过
8. **实验前备份配置，实验后恢复**
9. 动 `Controls/RdpConsole.cs` 的 `EnsureHost()` 时，**"先可见再创建"这条顺序不能改**

---

## 建议的起手动作

1. 先确认运行的是 2.17.19：标题栏 `v2.17.19`，或
   `(Get-Item dist\SoulLink.exe).VersionInfo.ProductVersion`。
2. 问用户这轮要做什么。若继续白带问题 → 按上面"仍未解决"第 1 条的最小排查点走。
3. 若只是想回退到已验证的 2.17.19 →
   运行 `E:\SoulLink-SOURCE-ARCHIVE\v2.17.19-reliable\restore.ps1`。

另外用户提过想「取消画质档位」，但当时改成了"先不回退"。
**注意：档位是 2.17.17 的 CPU 修复带来的，摘掉会让单核占用回到 ~100%。**
要动之前必须先跟用户确认这个代价。
