# SoulLink 交接文档 · 2.17.16

> 固定时间：2026-11-20 02:40
> 版本号：**2.17.16.0**
> 性质：本会话收尾固定版。**有 2 个已知问题未解决**，见第 4 节。
> 建议：新会话先读第 4 节，再决定从哪里开始。

---

## 0. 一句话现状

本会话从 2.17.4 做到 2.17.16，修了 21 个问题（RDP 显示、RDP Wrapper、Broker 通道、注入通道、自动化范围、状态显示、性能）。
**但注入代理仍会在建连约 2 秒后被宿主主动断开**，这是当前最影响功能的问题。
CPU 长期占用约 95% 单核，经对照实验确认是既有现象，非本会话引入。

---

## 1. 成品与存档

| 项目 | 值 |
|---|---|
| 标准版 | `dist/SoulLink.exe` |
| 版本号 | 2.17.16.0 |
| 大小 | 67,466,678 字节 |
| SHA256 | `7C16F7B3FBBD44CC0A5D7A7EA8FC2F8AA519C9CFD8AE962F0E35FF398D094DAC` |
| **固定归档** | `E:\SoulLink-SOURCE-ARCHIVE\v2.17.16-reliable\` |
| 前一版 | 2.17.15，备份 `20261120-023952-before-2.17.16-dist\` |
| 更早固定版 | `v2.17.7-reliable\` |
| 挂机版 | `dist/stable` **未改动**，仍 2.17.4.0 `C83BDD34...` |

归档内容（102.5 MB）：`SoulLink-2.17.16.exe` + `tools/SoulLink.BrokerSetup.exe` +
`7za.exe` + broker 脚本 + `source/`（169 文件）+ `docs/` + `VERSION.txt` + `SHA256.txt`（192 条）。

---

## 2. 本会话修复清单（2.17.4 → 2.17.16）

### 2.1 RDP 显示与交互

| # | 问题 | 根因 | 落点 |
|---|---|---|---|
| 1 | 双列点画面切不了焦点（紫框不动） | `ShieldClicked` 订阅里用「row 是否 _viewing」过滤，把"切到另一个画面"挡掉了 | `ConsolePage.xaml.cs` 订阅处 |
| 2 | 双列点画面时画面放大溢出 | `ApplyStretchRefit` 里 `SmartSizing=false->true` 先关再开，中间一帧按 1920×1080 原尺寸绘制 | `RdpConsole.cs` |
| 3 | 切页/切画面抖动 | 多处叠加：移屏外再移回、滚动条测量顺序、强制 UpdateLayout、视口反馈 | `Tiles.cs` / `RdpConsole.cs` / `MainWindow` |

### 2.2 RDP Wrapper

| # | 问题 | 根因 / 做法 |
|---|---|---|
| 4 | 装完仍提示需要重启电脑 | **UmRdpService 依赖 TermService 且运行中**，Windows 不允许停止有运行中依赖者的服务，TermService 从未真正停止，TermWrap.dll 从未重载 |
| 5 | 一键安装 | 4 个运行时 DLL 内嵌进 exe，自动创建目录释放，按实际路径写注册表，双写 CurrentControlSet + 实际 ControlSet00x |
| 6 | 1056 误报 | `sc stop` 后轮询等 STOPPED，`sc start` 遇 1056 再查状态 |

**第 4 项的修复从未真机验证**（验证需停 TermService，会断开所有 RDP 会话几秒）。

### 2.3 Broker 通道

| # | 问题 | 根因 / 做法 |
|---|---|---|
| 7 | 找不到独立安装器 | `SoulLink.BrokerSetup.exe` 从未进发布包。用 MSBuild Target 复制到 `tools\` |
| 8 | 便携版无法用 Broker | `DeployAsync` 开头强制解析本地代理副本；Broker 通道由服务从固定目录启动代理，客户端不需要 |
| 9 | 装完不自动恢复投送 | 清版本闸门缓存 + 通知中控台重置投送记录并立即重投 |
| 10 | 最低可用版本门槛 | `MinimumServiceVersion = 2.17.13.0`，两端共用，只要求"不低于" |

**第 7 项的坑**：`None`/`Content` 配 `CopyToPublishDirectory` 会被单文件发布合并进 exe
（实测 64MB → 97.9MB），必须用 MSBuild Target 复制。

### 2.4 注入通道

| # | 问题 | 根因 / 做法 |
|---|---|---|
| 11 | **注入代理从未真正投送** | 上一版加"等待结果"时**误删了 `BeginInput(s.Id, acc)`**，变成只等不投 |
| 12 | 投送静默失败不可观测 | `BeginInput` 三种提前返回原来合并成一个无日志 return，现各自留痕 + 45 秒硬超时 |
| 13 | 代理死亡不重投 | `_deployHandled` 只记"发起过"，死了不清标记，监控永久跳过 |

### 2.5 自动化与账号范围

| # | 问题 | 根因 / 做法 |
|---|---|---|
| 14 | 主用户（主控）的自动化无法运行 | 「仅管理挂机账号」被实现成执行拦截，3 处会废掉主控：`FlowRunner`（定时自动化静默失败）、`ActionService`、`FlowValidator`。已移除 |
| 15 | 主用户卡片显示运行数据 | 现在只显示结构（N 个条件 · M 个动作），隐藏状态灯 / 满足数 / 今日运行 |

**保留的 2 处拦截**：`SystemLauncher`、`RequestHandler` —— 它们拦的是"把主用户桌面当挂机目标投送进程"，语义正确。
用户原话：「不能停啊，它是主控」。

### 2.6 投送状态显示

| # | 问题 | 做法 |
|---|---|---|
| 16 | 主界面「未投送」与实际可用不一致 | 投送成功/断开时主动通知界面刷新 |
| 17 | 通道状态「显示成功又灭了」 | 6 秒去抖；修掉"管道全消失直接 return、跳过去抖"的漏洞 |
| 18 | 三种模式可同时勾选 | 改为下拉单选（计划任务 / Broker / 手动），互斥 |
| 19 | 手动 ↔ 自动切换后不恢复投送 | 切换时清空 `_deployHandled` + `_sessionSeen` |
| 20 | 手动代理 CTS 异常刷屏 | 先 Cancel 唤醒任务，再 Dispose 通道 |

### 2.7 性能

| # | 问题 | 做法 |
|---|---|---|
| 21 | 死代理检测把 CPU 占满 | 循环内对每个账号各调 `SessionService.QueryAsync`（每次起 query.exe），每 2 秒一轮。改为只查一次 + 检测节流 10 秒 |

---

## 3. 验证状态

### 已验证（真机）

- Broker 投送链路跑通：`DEPLOY_BEGIN / DEPLOY_RESULT ok=True / DEPLOY_IDENTITY admin=True elevated=True`
- 客户端日志出现「已确认连接（会话 4）」
- 「仅管理挂机账号」不再阻止主用户自动化
- 双列点画面切焦点（2.17.6 用户确认「可以了」）
- 投送方式下拉三选一
- 主界面通道状态不再"显示成功又灭了"

### 未验证

- **RDP Wrapper 依赖服务重启顺序**（第 4 项）—— 从未真机跑过
- `tools/SoulLink.BrokerSetup.exe` 是 2.8.8 时代版本，与 2.17.16 兼容性未验证
- 长时间挂机稳定性

---

## 4. ★ 未解决的问题（新会话优先处理）

### 4.1 注入代理被宿主主动断开（最影响功能）

**现象**

```
Broker 服务端：
  DEPLOY_RESULT ok=True pid=112732 已投送到会话 5
  DEPLOY_IDENTITY user=Lummy\A3 session=5 admin=True elevated=True

代理日志（C:\Users\A3\AppData\Local\Temp\soullink-relay-112732.log）：
  02:34:39.429  relay 启动 pid=112732 pipe=SoulLink_Relay_5_9056a6db session=5
  02:34:39.448  热键事件管道已连接
  02:34:39.448  已连上宿主管道
  02:34:39.467  热键接收线程启动 thread=114632 session=5
  02:34:41.084  宿主断开，释放鼠标并退出        ← 2 秒后宿主断开
```

A2 侧（`soullink-relay-113688.log`）只有启动记录，没有断开记录，但进程同样消失。

**已排除**

- Broker 侧正常：`DEPLOY_BEGIN / RESULT / IDENTITY` 全部成功，身份正确
- 代理自身启动正常：连上宿主管道、热键线程启动

**怀疑方向**

`BeginInputCore` 开头有 `await StopInputAsync()`，本意是停自己 Console 的旧通道。
但两个 Console 在 2 秒内都被断开，需要确认：

1. 两个 Console 是否共享了 `Input` 对象
2. `EnsureInputRestored` / `ScheduleInputRebuild` 是否在互相踩
3. `StopInputAsync` → `RelayClient.Dispose()` → Broker `stop` 命令的 Pid/SessionId 是否可能指向别的会话

**相关代码位置**

- `Controls/RdpConsole.cs` — `BeginInputCore`（约 2654 行，开头 `StopInputAsync`）
- `Controls/RdpConsole.cs` — `StopInputAsync`（约 2782 行）
- `Services/RelayClient.cs` — `Dispose`（约 545 行，向 Broker 发 `stop`）
- `Services/BrokerService.cs` — `stop` 分支（约 122 行，按 SessionId + Pid 校验）

**建议做法**

加时序日志：记录每一次 `StopInputAsync` 的调用来源（谁调的）、当前 `Input` 对象引用、
SessionId、以及 Dispose 发出的 stop 请求内容。跑一次拿完整证据，不要再猜。

### 4.2 CPU 约 95-100% 单核（长期存在）

**实测**

```
2.17.16：CPU 增量 28.5 秒 / 30 秒 = 95% 单核
2.17.7（对照）：19.9 秒 / 20 秒 = 99% 单核     ← 老版本同样高
最忙线程累计 98~129 秒
```

日志仍在正常输出（每 12 秒一条 tick），说明 UI 线程没卡死。

**怀疑**

1. 两个 1920×1080 画面经 SmartSizing 拉伸到 ~955×568 的渲染开销
2. 150ms 保护层看门狗（`EnsureShieldEnforced` + `PointToScreen`）

**未做**：单画面 / 双画面对照实验。

### 4.3 其他

- RDP Wrapper 依赖服务重启顺序（2.2 第 4 项）从未真机验证
- `SoulLink.BrokerSetup.exe` 版本兼容性未验证

---

## 5. 本会话的教训（避免重蹈）

1. **不要用 `Encoding.Unicode.GetString` 搜单文件发布 exe 里的字符串。**
   启用了 `EnableCompressionInSingleFile`，程序集被压缩，永远搜不到。
   验证代码是否生效**只能看运行时日志**。我曾据此误判"诊断代码没编译进去"，
   实际是搜索方法错了。

2. **改"等待结果"这类逻辑时，确认原有"发起动作"的调用还在。**
   本会话误删过 `BeginInput(s.id, acc)`，导致只等不投，
   日志显示"已发起未确认"，把排查方向带偏了好几轮。

3. **任何提前 return 都必须留痕。**
   `BeginInput` 的三种提前返回原来完全无日志，"彻底没反应"时无从排查。

4. **清空正在被服务写入的日志会触发服务重启。**
   需要清日志时先备份并接受一次服务重启影响。

5. **改之前先做对照实验**（用旧版本跑同样场景），
   避免把既有问题归因为本次改动。CPU 问题就是靠 2.17.7 对照才确认是既有的。

6. **单文件发布的产物不能用静态方式验证功能**，必须跑起来看日志。

---

## 6. 关键代码位置速查

| 功能 | 文件 | 说明 |
|---|---|---|
| 投送调度 | `Views/ConsolePage.xaml.cs` `ScanAndDeployAsync` | 每 2 秒扫描；死代理检测节流 10 秒 |
| 发起投送 | `Controls/RdpConsole.cs` `StartInputBySessionAsync` / `BeginInput` / `BeginInputCore` | **BeginInput 绝不能少** |
| Broker 协议 | `Services/BrokerProtocol.cs` | `MinimumServiceVersion` / `SendAsync` / 版本闸门 |
| Broker 服务端 | `Services/BrokerService.cs` | `ensure` / `stop` / `health` |
| 投送实现 | `Services/RelayClient.cs` `DeployAsync` | Broker 分支 / 计划任务分支 |
| RDP Wrapper | `Services/RdpWrapService.cs` | `InstallAsync` / `RestartTermServiceAsync`（依赖顺序） |
| 账号范围 | `Services/AccountScope.cs` | 只用于界面过滤与投送目标，**不用于拦执行** |
| 通道状态探测 | `Services/BgiLogWatcher.cs` `DiscoverLiveAgentSessions` | 进程 + 管道同时在才算活着，带 6 秒去抖 |

---

## 7. 纪律（沿用）

1. 不要修改/回退 `dist/stable/`，除非用户明确授权
2. 不操作 A1/A4；测试用 A2/A3
3. 不按 PID + SessionId + 完整路径核对就操作任何进程
4. 不要往 RDP 画面上盖任何 WPF 浮层
5. 不要给 `WindowsFormsHost` 设固定尺寸
6. 大文件用 `edit` 精确改，不要整文件 `write` 覆盖
7. **改完必须跑起来看日志验证**，不能只看编译通过
