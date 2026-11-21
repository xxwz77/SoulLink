# SoulLink 2.10.1 变更

日期：2026-11-19
主线：修复手动代理接入后侧键不生效

## 现象

开启手动代理模式后，代理显示接入成功，但按鼠标侧键没有反应，进不了自由视角。

## 根因

手动代理走的是 `ManualAgentMode.ListenAsync` 这条新路径。
它只做了「建管道 + 等代理连回」两件事，**漏掉了挂接**：

1. 没有把连上的 `RelayClient` 挂到 `RdpConsole`（`Input` 从未被设置）
2. 没有调用 `SyncRelayHotkey`，热键配置从未推送

代理侧日志因此停在：

```
00:26:39.925  热键接收线程启动 thread=64396 session=6
（之后什么都没有）
```

正常情况下这里应该有：

```
目标会话鼠标热键安装=True thread=... error=0 spec=鼠标侧键4
```

也就是说钩子根本没装上，侧键自然没反应。

对比：自动投送路径里这两步都在 `BeginInputCore` 内部完成，所以自动通道一直正常。

## 修复

### 1. 抽出 `AttachRelay`

把「装配 SessionInput、订阅 HotkeyPressed、设置远端尺寸」
从 `BeginInputCore` 提炼成 `RdpConsole.AttachRelay(RelayClient)`，
自动投送与手动代理共用同一段逻辑。

### 2. `RdpConsole.AttachManualRelay`

手动通道挂到控制台，并主动 `SyncRelayHotkey("手动代理接入")`。

### 3. `ManualAgentMode.Attached` 事件

代理接入成功后触发；`ConsolePage` 订阅并把通道挂到对应控制台。

## 改动文件

| 文件 | 改动 |
|------|------|
| `Controls/RdpConsole.cs` | 抽出 `AttachRelay`；新增 `AttachManualRelay` |
| `Services/ManualAgentMode.cs` | 新增 `Attached` 事件 |
| `Views/ConsolePage.xaml.cs` | 订阅事件并挂接到对应控制台 |

## 验证

`ManualHotkeyCheck`（新）：

```
ok   建立等待管道
ok   代理接入
ok   代理能响应热键配置指令
ok   热键配置被代理接受（安装成功）
ok   代理热键线程已启动
ok   代理已安装鼠标热键（侧键可用）
```

代理日志确认出现：

```
00:31:16.087  目标会话鼠标热键安装=True thread=60544 error=0 spec=鼠标侧键4
```

## 未解决

- **A3 会话侧键未命中**仍未复验。本版修的是「手动通道漏挂接」，
  与 A3 之前那次的成因可能不同，需要实测确认。
- UAC 开启的 linked token 分支、冷启动时间窗口、锁屏与会话切换未回归。