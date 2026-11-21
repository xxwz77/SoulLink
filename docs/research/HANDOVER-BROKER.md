# SoulLink 测试版 Broker 交接记录

更新时间：2026-11-18 23:15

## 结论（已实测）

**管理员代理投送已打通**：A2（会话 4）、A3（会话 5）在 Active 状态下各投送一次，
代理均为提权管理员令牌、落在目标会话、自检通过（elevated/desktopOk/nonce 全部 true），
验证后立即回收代理，未改动 A1/A4/挂机版，未注销或重启任何用户会话。

投送成功日志（broker.log）：

```
AUTH_BEGIN deploy client verification
AUTH_CLIENT pid=<客户端> user=Lummy\lummy ... admin=True elevated=True
AUTH_OK client verified
DEPLOY_SESSION session=4 user=A2 => Active
DEPLOY_RESULT ok=True pid=<代理> 已投送到会话 4，PID <代理>
DEPLOY_IDENTITY pid=<代理> user=Lummy\A2 session=4 admin=True elevated=True
RESPONSE_SENT AGENT_STARTED
```

## 本轮修复的 7 个真实缺陷

1. **管理员判定在 SYSTEM 里必然失败（AUTH_FAIL 根因）**
   `BrokerSecurity` 用 `new WindowsIdentity(token)` / `WindowsPrincipal.IsInRole` 判断管理员。
   这两个 API 会走 LSA / 复制令牌（需要 SeTcbPrivilege），在 LocalSystem 服务进程里
   100% 抛 `SecurityException: 拒绝访问`（.NET 8.0.33 已复现）。
   → 改为纯 P/Invoke 读 `TokenUser` / `TokenGroups`（直接扫 BUILTIN\Administrators），
   不依赖任何特权。**管理员强制要求没有放宽**：非管理员组令牌仍然被拒。

2. **代理根本没有收到 --relay 参数**
   `SystemLauncher.LaunchAdminAgentFromBroker` 组装 Job 时没写 `Arguments`，
   代理会以宿主模式启动、永远不连管道。→ 现在拼 `--relay <管道> --require-elevated --deploy-nonce <nonce>`。

3. **UAC 关闭的机器上必然拒绝投送**
   `LaunchAsUser` 要求必须拿到 linked token，但 `EnableLUA=0` 时会话令牌本身就是完整管理员令牌
   （ElevationTypeDefault），拿不到 linked token。→ 改为"有 linked token 就用，没有就校验登录令牌本身"，
   校验不通过仍拒绝。

4. **代理桌面自检永远是失败**
   `AgentSecurity.CurrentDesktop()` 用 `GetProcessWindowStation` 取到 `WinSta0`，
   却拿去和 `\default` 比较。因为普通通道从不传 `--require-elevated`，这个 bug 一直没暴露；
   Broker 通道第一次带上该参数就自杀。→ 改用 `GetThreadDesktop(GetCurrentThreadId())`，
   取不到名字时以 `OpenInputDesktop` 为准，不误判。

5. **代理校验存在竞态**
   `AcceptAgentAsync` 在 `AgentPid`/BrokerBacked 赋值之前就比 PID，
   代理比 Broker 回包更快连回时会被判失败。→ 校验移到 PID 确定后的 `VerifyAgentPipe`。

6. **Dispose 会卡死调用线程**
   对已断开/失效的命名管道 `WriteLine` 会阻塞，`RelayClient.Dispose` 在界面线程上同步执行时直接冻结。
   → 收尾指令与 `stop` 命令都改为后台线程尽力而为，Dispose 立即返回。

7. **安装器用版本号判断文件是否相同**
   同版本重新编译的 exe 版本号完全一样，安装器会跳过复制，导致"装了新包但跑的还是旧二进制"
   （本轮排查因此多绕了 3 轮）。→ 改为 SHA256 内容比对；代理清单每次校验并按需重打。

## 新增的保护

- **在线才投送**：`SystemLauncher.DescribeSessionOnline` 以 `WTSQuerySessionInformation`
  的会话状态为准（Active/Connected 才放行），Broker 服务端和客户端各校验一次；
  离线返回 `OFFLINE` 并给出中文原因，不会去 WTSQueryUserToken 撞墙。
  查询失败时退回 `query user`，不因为查询问题一刀切拒绝。
- **AUTH 分步日志**：`AUTH_BEGIN → AUTH_CLIENT → AUTH_OK/AUTH_FAIL`，
  每步带客户端 PID/用户/SID/会话/admin/elevated，失败原因可直接读日志定位。

## 当前版本与位置

- 测试版：**2.8.8**，发布产物 `artifacts\publish-broker-2.8.8\SoulLink.exe`
  - size 67,021,874 · SHA256 `3E987D6938EB821094992FC168F312957A0342D3C8DEE1D701CFDEEF2224175A`
  - 服务目录内 `SoulLink.exe` 哈希与之相同；`SoulLink.Agent.exe`（asInvoker）为
    `5281D76245EE39F68C3294C3AE20C48CA33707A3D059CC34B89D91075E52E51C`。
- 服务目录 `%ProgramFiles%\SoulLinkDev`：`SoulLink.exe`（服务本体，requireAdministrator）
  与 `SoulLink.Agent.exe`（代理，asInvoker），均 2.8.8。
- 服务名 `SoulLink.DevBroker`，LocalSystem，正在运行。
- **`dist\SoulLink.exe` 已更新为 2.8.8**
  （SHA256 `3E987D69…`，与服务目录一致）；`dist\tools\SoulLink.BrokerSetup.exe` 同步为最新安装器，
  `dist\BROKER-TESTING.md` 已同步。

### dist 更新的方式与影响

- 首次更新用"改名让路 + 复制新文件"完成，**没有结束当时正在运行的测试版客户端**。
- 用户随后自行退出了测试版客户端；确认 `dist\SoulLink.exe` 锁定解除后复核：
  文件完整、可执行、哈希与 `artifacts\publish-broker-2.8.8` 及服务目录 `%ProgramFiles%\SoulLinkDev\SoulLink.exe`
  三处完全一致（`3E987D69…`）。
- 旧的 2.8.7 保留为 `dist\SoulLink-2.8.7.exe`，需要回退直接改回名字即可。
- `dist\stable\` 未做任何修改。
- **当前没有测试版客户端在运行**；下次从 `dist\SoulLink.exe` 启动即为 2.8.8，
  客户端侧的在线门槛随之生效。

### 离线门槛已实测

客户端退出后 A2（会话 4）、A3（会话 5）自动变为 `Disc`，此时投送被正确拒绝：

```
=== A2 会话 4 状态 Disc ===
在线判定：会话 4 已断开（Disc），需要先用 RDP 连上才能投送
SKIP A2：会话不在线，按设计要求不投送
```

没有创建任何代理进程，服务端也没有去调 WTSQueryUserToken。这验证了"必须在线才投送"确实生效。

## 验证命令（可重复执行）

```powershell
cd E:\Documents\deepseek-harness\SoulLink
dotnet build tools\layout-check\BrokerDeployCheck.csproj -c Release
.\tools\layout-check\binBrokerDeployCheck\Release\net8.0-windows\BrokerDeployCheck.exe A2 A3 --identity=SoulLink
```

该工具只做：健康检查 → 在线判定 → 投送 → ping 自检 → 回收代理。
**不连接 RDP、不注销、不重启任何客户端。**

## 未验证 / 仍需注意

- 冷启动（服务刚起来、目标会话刚登录）时的时间窗口未做长时间浸泡测试。
- 锁屏、会话切换、代理被杀软拦截等异常路径未覆盖。
- 真机游戏内输入响应（鼠标注入到游戏）未在本轮验证范围。
- 若以后把 UAC 打开，走的是 linked token 分支，需要重新回归一次。

## 严重操作问题（仍然有效）

本轮同样**没有**关闭 dist 测试版客户端、没有注销 A2/A3、没有动 A1/A4/挂机版。
后续如必须重启测试版客户端，先按用户授权处理 A2/A3 注销并等待重新登录。
