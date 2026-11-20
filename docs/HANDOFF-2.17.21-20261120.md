# SoulLink 交接文档 · 2.17.21（模拟按键根本没发出去 —— SendInput 结构体尺寸错误）

> 时间：2026-11-20 16:25
> 版本：**2.17.21.0**（待定版；可信归档仍是 2.17.19）
> 性质：修 **「模拟按键」从来就没有真正注入过**（不是偶发，是一直）
> 结论：根因坐实（cbSize=32≠40，SendInput 直接返回 0），已修，已在 A2 真实会话内端到端验证按键落地

---

## 1. 现象与探索过程（记下来，避免重复踩）

用户在启动页点自动化卡片 ▶，弹窗显示「执行「模拟按键」：按键完成」，
但**记事本收不到任何输入**。

排查顺序（前两轮都白走了，记下来）：

| 轮次 | 判断 | 结论 |
|---|---|---|
| 1 | 以为 ▶ 点不到（界面自适应问题） | 用 UIAutomation 拿到按钮真实坐标并 Invoke，**点得到**，弹窗确实弹出 |
| 2 | 以为只是流程状态锁死 | 2.17.20 修掉锁存后，弹窗变「按键完成」，但**记事本仍没有输入** → 说明是发送端问题 |
| 3 | 怀疑 SendInput | **坐实**：结构体尺寸不对，SendInput 一个事件都没注入 |

## 2. 根因

`Relay/RelayAgent.cs` 里的 `INPUT` 复刻少了一个联合体成员：

```csharp
[StructLayout(LayoutKind.Explicit)]
private struct INPUTUNION
{
    [FieldOffset(0)] public KEYBDINPUT ki;   // ← 只有它，24 字节
}
```

Windows 的 `sizeof(INPUT)` 按**联合体里最大的成员**（`MOUSEINPUT`，32 字节）算，
x64 下 `sizeof(INPUT) = 40`。原代码 `Marshal.SizeOf<KEYINPUT>()` 只有 **32**。

实测（同机、复刻同一份声明）：

```
RelayAgent 实际传的 cbSize = 32
Windows 期望 sizeof(INPUT) = 40
A) SendInput(cbSize=32) → 返回 0，GetLastError=87 (ERROR_INVALID_PARAMETER)
```

而调用方 `SendVk` **丢弃了返回值**，`SendKeyCombo` 于是无条件 `return true`
→ 代理回「按键完成」→ 界面显示成功 → **目标窗口一个键都收不到**。

## 3. 修复（`Relay/RelayAgent.cs`）

1. `INPUTUNION` 补齐 `MOUSEINPUT` / `HARDWAREINPUT`（联合体按最大成员算尺寸 → 40）；
2. `SendVk` 检查 `SendInput` 返回值，失败时记日志（vk / up / cbSize / last-error）并返回 false；
3. `SendKeyCombo(spec, out string error)` 不再无条件 true，失败原因一路冒到界面：
   `执行「模拟按键」：按下 1 失败（SendInput 被系统拒绝）`。

## 4. 验证（真机，A2 会话内）

### 4.1 复刻探针（先证伪）

`SendInput(cbSize=32)` → 返回 **0**，`GetLastError=87`。

### 4.2 真实 `RelayAgent.SendKeyCombo`，在 A2 会话里自测

把自测程序用计划任务（`/ru A2 /it`）投进会话 4，程序自建一个前台窗口 + 文本框，
调用**真实的**（反射拿到的）`SendKeyCombo("1")`：

```
assembly=SoulLink, Version=2.17.20.0
过程会话=4
SendKeyCombo("1") = True  error=''
前景窗口='SendKeyCombo 自测(A2)'
文本框内容='1'
RESULT: PASS — 按键真的落进目标会话的窗口
```

⚠ 第一次自测误判成 FAIL，原因是我在 UI 线程里 `Thread.Sleep(300)`，
把刚刚投进队列的按键消息堵住了。**必须用 `Application.DoEvents()` 循环等待**。
这条也适用于以后任何"在 UI 线程里验证输入"的场景。

## 5. 顺带修好的一件事：注入代理是**独立安装的旧副本**

`C:\Program Files\SoulLinkDev\SoulLink.Agent.exe` 一直是 **2.17.19**，
而客户端已经是 2.17.21 —— **光替换 `dist\SoulLink.exe` 不会更新代理**，
代理代码在旧二进制里跑，修复根本不生效。

更新步骤（本次实际执行）：

1. `Stop-Service SoulLink.DevBroker`；
2. 结束 `--broker` 服务进程与两个 `SoulLink.Agent.exe`（否则文件被占用，安装脚本报 exit 2）；
3. 用本仓库的 `ManifestPatcher.MakeAsInvoker`（反射调用，等价于安装脚本的 `--secure-directory` + 清单改写）
   把 2.17.21 复制成 `SoulLink.exe` 与 `SoulLink.Agent.exe`；
4. `Start-Service SoulLink.DevBroker`；
5. 重启客户端，监控自动重新投送 → 会话 4/5 的代理都变成 **2.17.21.0**。

⚠ 已知问题（未修）：
* `tools\broker-install.ps1` 文件**无 BOM + 含中文**，Windows PowerShell 5.1 会解析失败，
  必须用 `pwsh`（PowerShell 7）执行；
* 该脚本依赖的 `tools\SoulLink.BrokerSetup.exe` 是 2.8.8 时代产物（FileVersion 1.0.0.0），
  **不认 `--secure-directory` / `--install-service`（一律 exit 2）**，所以脚本必然在第一步失败。
  本次是绕过它手工完成的。**下次要修：把 BrokerSetup 更新到当前版本，或让安装逻辑内置到主程序。**

## 6. 产物

| 项目 | 值 |
|---|---|
| 版本 | 2.17.21.0（`SoulLink.csproj`） |
| `dist/SoulLink.exe` | 67,476,732 字节 · SHA256 `672A95264DEA67DC161FEE99E238366205E271FB635EC7FA04162CE668788BAB` |
| 已安装副本 | `C:\Program Files\SoulLinkDev\SoulLink.exe` 与 `SoulLink.Agent.exe` 均为 2.17.21.0 |
| 旧副本 | 同目录 `*.old`（2.17.19.0 / 2.17.16.0） |
| 可信归档 | 仍是 `v2.17.19-reliable`（2.17.20/2.17.21 未归档） |

## 7. 给用户的验证步骤（界面操作由用户执行）

1. 确认 A2 会话里开着记事本（或任何可输入的窗口）并置于前台；
2. 启动页点「lummy 自动化」卡片 ▶；
3. 预期：弹窗出现「执行「模拟按键」：按键完成」，且**记事本里出现 `1`**；
4. 若仍失败，弹窗会给出具体原因（如 `按下 1 失败（SendInput 被系统拒绝）`），
   不再是无信息量的「按键完成」。

## 8. UI 自适应（用户反馈的另一问题，本次未修）

用户指出：主界面在小窗口下无法自适应、按钮点不到。本次只做了输入链路修复，
**没有改布局**。建议下一轮单独处理（可先量 `LaunchPage` 的
`MinWidth/MinHeight`、ScrollViewer 缺失点、以及固定像素宽度的卡片）。
