# SoulLink 2.9.0 变更

日期：2026-11-19
主线：新增手动启动代理，与现有自动投送并存

## 背景

自动投送要跨会话创建进程，链路上任何一环保问题都会表现为"按侧键没反应"：

- SYSTEM 计划任务通道：需要偷 SYSTEM 令牌、建任务、写 job 文件
- Broker 通道：需要装服务、固定目录、管理员客户端
- 两者都依赖 CreateProcessAsUser、令牌校验、命名管道 ACL

排查时还要跨会话看多处日志，代价很高。

手动代理是另一条路：**用户自己在目标会话里启动它**，天然就在正确的会话和桌面上，
不依赖任何提权或跨会话机制。

## 新增

### 1. 手动代理入口 `--agent`

```
SoulLink.exe --agent                       # 自动发现本会话的宿主管道
SoulLink.exe --agent --pipe <管道名>        # 显式指定
SoulLink.exe --agent --wait 120            # 等待宿主管道最多 120 秒（默认 60）
```

`SoulLink.Agent.exe`（无参数双击）等价于 `--agent`，用户不需要记参数。

### 2. 管道自动发现

宿主管道命名规则：`<身份>_Relay_<会话号>_<8位hex>`。

代理读 `Process.SessionId`，只枚举 `\\.\pipe\` 里满足以下条件的管道：

- 前缀匹配本版本身份（`SoulLink_Relay_`）
- 会话号完全匹配（避免 `Relay_6` 命中 `Relay_60`）
- 排除 `_Events` 事件管道
- 后缀必须是 8 位十六进制

所以**不需要用户输入，也不会连到别的账号**。

### 3. 导出功能

设置页新增「导出手动代理」按钮，把主程序复制一份改名为 `SoulLink.Agent.exe`
并改写清单为 `asInvoker`，这样普通用户令牌也能启动。

## 修复的连带问题

**`AppIdentity` 把 `SoulLink.Agent.exe` 当成独立版本**

它按文件名判断身份，于是代理副本得到身份 `SoulLink.Agent`，
管道前缀变成 `SoulLink.Agent_Relay_...`，**永远匹配不到宿主**。

修复：代理文件名归属主程序身份。新增 `IsAgentExecutable`，在 `Resolve()` 里优先返回 `SoulLink`。

## 验证

`ManualAgentCheck`（17 项）：

- 不存在的会话 / 会话号 0 / 负数 → 返回 null 并给出可读原因
- 真实建管道 → 能发现
- 其他会话的管道 → 不会误连
- 仅有 `_Events` 管道 → 不会被当主通道
- 非法后缀（`_zzzz`）→ 拒绝
- 导出产物 → 存在且清单为 `asInvoker`

`ManualAgentLiveCheck`（真实端到端）：

```
宿主管道 = SoulLink_Relay_1_6d6be805
代理来源 = dist\SoulLink.exe
启动代理（无参数，自动发现管道）…
ok   代理自动发现管道并连回宿主（无需任何参数）
ping 响应：{"Ok":true,"Message":"pong",...,"SessionId":1,"Elevated":true}
ok   代理能正常响应 ping
```

其余回归：`LogFilterCheck`、`BrokerCheck`、`ProxyRaceCheck` 全部通过。

## 未解决

- **A3 会话侧键未命中的问题没有修复**。本版只是多提供一条通道，
  让用户可以在该会话里用手动代理复验，从而区分是"投送路径问题"还是"注入本身问题"。
- UAC 开启的 linked token 分支、冷启动时间窗口、锁屏与会话切换、
  真实游戏内输入响应仍未回归。
