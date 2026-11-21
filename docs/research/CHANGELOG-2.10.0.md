# SoulLink 2.10.0 变更

日期：2026-11-19
主线：手动代理模式（开关驱动）

## 为什么简化

2.9.1 用手动解析进程命令行来反推「有没有手动代理」，实现复杂且脆弱：
手动代理和 Broker 自动代理文件名相同（SoulLink.Agent.exe），
只能靠 --relay 区分，而跨进程读命令行要解析 PEB，各版本布局不同。

本版改成**用户显式声明的开关**：

```
开关打开 → 不自动投送，只准备管道等用户启动代理
开关关闭 → 恢复自动投送
```

程序不需要猜任何事。

## 新增

### 1. 设置项 `ManualAgentMode`

全局开关，落在 config.json。程序启动时读取：
开着就直接进入等待状态，不投送任何代理。

### 2. `ManualAgentMode` 控制器

- `SetEnabledAsync(bool)`：切换模式并返回给用户看的说明
- `StartWaitingAsync(sessionId, user)`：为该会话准备等待管道
- `StopAllAsync()`：撤销全部等待
- `CleanAutoAgents()`：清理 SoulLink.proxy（只清这个文件名，
  它只有计划任务通道会用，不会误杀用户手动副本）

### 3. `RelayClient.ListenAsync`

把「建管道 + 等代理连回」从 `DeployAsync` 里拆出来，
让宿主可以在不投送任何进程的前提下等待用户手动启动代理。

### 4. 设置页开关

「使用手动代理模式（不自动投送）」勾选框 + 说明文字。
切换即时生效并写日志。

## 改动点

| 文件 | 改动 |
|------|------|
| `Models/Models.cs` | 新增 `ManualAgentMode` 设置项 |
| `Services/ManualAgentMode.cs` | 新增，模式控制器 |
| `Services/RelayClient.cs` | 拆出 `ListenAsync` |
| `Views/ConsolePage.xaml.cs` | 手动模式下跳过自动投送，改为准备等待管道 |
| `App.xaml.cs` | 启动时检查开关 |
| `Views/SettingsPage.xaml(.cs)` | 开关 UI 与事件 |

## 验证

`ManualModeCheck`（新）：

```
ok   关闭时 Enabled=false，没有等待中的会话
ok   打开时 Enabled=true
ok   能为会话建立等待管道
ok   重复建立被拒绝（不会重复占管道）
ok   用户启动代理后自动接入（无需任何参数）
ok   接入后握手正常
ok   关闭后等待列表已清空、通道已注销
```

其余回归全部通过。

## 未解决

- **A3 会话侧键未命中**仍未修复。本模式可用于在该会话复验，
  以区分「投送路径问题」还是「注入本身问题」。
- 手动模式下不主动杀 Broker 代理（由服务自行管理）。
- UAC 开启的 linked token 分支、冷启动时间窗口、锁屏与会话切换未回归。