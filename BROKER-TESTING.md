# SoulLink 测试版 Broker · 验证记录

更新时间：2026-11-18 23:15

## 已在本机测试版验证

### 安装与系统集成

- 独立安装器首次安装、重复安装和卸载。
- 旧服务停止、固定目录文件替换、文件锁释放。
- **同版本重新编译后按内容哈希替换**（2.8.8 修复：以前按版本号跳过，会留旧二进制）。
- 代理清单每次校验并按需重打。
- 目录 ACL：仅 SYSTEM / Administrators 完全控制，Users 只读执行。
- LocalSystem SID 与会话 0 校验。
- SCM 服务创建、启动与运行（`SoulLink.DevBroker`）。

### 认证与身份判定

- `AUTH_BEGIN → AUTH_CLIENT → AUTH_OK` 分步日志，含 PID/用户/SID/会话/admin/elevated。
- LocalSystem 服务内**不再**使用 `WindowsIdentity(IntPtr)` / `WindowsPrincipal.IsInRole`
  （这两个在 SYSTEM 令牌上必然抛"拒绝访问"）；改为直接读 TokenUser / TokenGroups。
- 强制要求未放宽：非管理员组令牌（如 DWM）仍被判定为非管理员。

### 管理员代理投送（2026-11-18 实测通过）

| 账号 | 会话 | 会话状态 | 结果 | 代理身份 |
|------|------|----------|------|----------|
| A2 | 4 | Active | 成功 | Lummy\A2 · session=4 · admin=True · elevated=True |
| A3 | 5 | Active | 成功 | Lummy\A3 · session=5 · admin=True · elevated=True |

代理自检（ping）结果：`elevated=true desktopOk=true nonce匹配=true`，屏幕 1920x1080。
两次投送后都立即回收代理，未留下残留进程。

- 健康握手：CLIENT_ACCEPTED → READ_BEGIN → READ_DONE → AUTH_BEGIN → AUTH_CLIENT → AUTH_OK
  → DEPLOY_SESSION → DEPLOY_BEGIN → DEPLOY_RESULT → DEPLOY_IDENTITY → RESPONSE_SENT AGENT_STARTED。
- 离线门槛：会话不在 Active/Connected 时直接返回 `OFFLINE`，不会去 WTSQueryUserToken 撞墙。

### 回归

- `BrokerCheck`：管道名、ACL、nonce 解析、健康响应全部通过。
- `BrokerAuthCheck`：自助令牌识别、SYSTEM 识别、非管理员拒识、在线判定全部通过。
- 本机 `EnableLUA=0`（UAC 关闭），走的是"登录令牌本身即管理员令牌"分支。

## 尚未验收

- UAC 开启环境（linked token 分支）的管理员代理投送。
- 冷启动 / 刚登录会话的时间窗口，长时间浸泡。
- 锁屏、断开和会话切换时的输入行为。
- 真实账号的游戏内输入与目标程序响应。
- 杀软拦截命名管道 / 拦截 CreateProcessAsUser 的异常路径。

本服务健康检查通过不等于真实输入已验证。

## 发布状态

- `dist\SoulLink.exe` = **2.8.8**（SHA256 `3E987D6938EB821094992FC168F312957A0342D3C8DEE1D701CFDEEF2224175A`）。
- `dist\tools\SoulLink.BrokerSetup.exe` = 最新安装器（内容哈希比对版）。
- `dist\SoulLink-2.8.7.exe` = 上一版，回退用：改回名字即可。
- `dist\stable\` 未修改。
- 更新时没有结束正在运行的测试版客户端；正在运行的进程仍在用旧文件，
  新版本在下次启动时生效。
