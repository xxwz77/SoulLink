# SoulLink 2.8.8 变更

日期：2026-11-18
主线：修复测试版 Broker 管理员代理投送

## 用户可见变化

- 测试版 Broker 通道现在能真正把管理员代理投送进目标会话（A2/A3 实测通过）。
- 界面通道状态能正确显示"通道就绪"，不再投送成功后仍显示"通道未投送"。
- 多个账号同时投送时不再只成功一个。
- 目标会话未在线（Disc）时，投送会明确提示"需要先用 RDP 连上"，不再含糊失败。

## 修复的缺陷

### 投送链路

1. **SYSTEM 里管理员判定必然失败（AUTH_FAIL 根因）**
   `WindowsIdentity(IntPtr)` / `WindowsPrincipal.IsInRole` 在 LocalSystem 令牌上
   需要 SeTcbPrivilege，100% 抛"拒绝访问"。
   改为直接读 TokenUser / TokenGroups 扫描 BUILTIN\Administrators，不依赖特权。
   **管理员强制要求未放宽**：非管理员组令牌仍被拒。

2. **代理没有收到启动参数**
   `LaunchAdminAgentFromBroker` 组装 Job 时漏了 `Arguments`，
   代理以宿主模式启动、永不连管道。
   现拼 `--relay <管道> --require-elevated --deploy-nonce <nonce>`。

3. **UAC 关闭的机器上一律拒绝投送**
   原要求必须拿到 linked token；`EnableLUA=0` 时会话令牌本身即完整管理员令牌
   （ElevationTypeDefault），拿不到 linked token。
   改为"有 linked token 就用，没有就校验登录令牌本身"，校验不通过仍拒绝。

4. **代理桌面自检永远失败**
   `CurrentDesktop()` 用 `GetProcessWindowStation` 取到 `WinSta0` 却和 `\default` 比。
   因普通通道从不传 `--require-elevated`，此 bug 长期未暴露。
   改用 `GetThreadDesktop(GetCurrentThreadId())`，取不到名字时以 `OpenInputDesktop` 为准。

5. **代理校验竞态**
   `AcceptAgentAsync` 在 `AgentPid` 赋值前就比 PID，代理连得快会被判失败。
   校验移到 PID 确定后的 `VerifyAgentPipe`。

6. **Dispose 卡死调用线程**
   对失效命名管道 `WriteLine` 会阻塞。收尾指令与 `stop` 命令改为后台线程。

### 并发与会话监控

7. **并发投送只成功一个（本轮最后定位）**
   会话监控每 2 秒扫一轮，A2/A3 同轮并行投送都要生成同一个
   `SoulLink.proxy.exe`，两个线程同时 Delete+Copy 互相抢占，
   后来者抛 "being used by another process"，整条通道判失败进重建队列。
   修复：`ResolveGate` 锁 + 双重检查串行化生成；`CopyAgentWithRetry`
   对被占用做 8 次递增重试；`RemoveIfStale` 复用同一把锁。

8. **投送成功后被客户端自己拆掉**
   `RdpConsole` 重建路径判 `InputAllowed`（要求前台且可见），
   后台账号恒为 false，刚建好的通道立刻被 `StopInputAsync`。
   同文件投送入口的注释早已写明"不能判 InputAllowed"，此处漏改。

9. **界面认不出 Broker 代理**
   `DiscoverLiveAgentSessions` 只认 `SoulLink` / `SoulLink.proxy`，
   Broker 通道的代理是 `SoulLink.Agent`。改为从 `BrokerInstall.AgentProcessName` 取。

### 安装器

10. **同版本不覆盖导致装完仍是旧二进制**
    安装器按 FileVersion 判断跳过复制，同版本重编译的 exe 版本号完全一样。
    改为 SHA256 内容比对；代理清单每次校验并按需重打。

## 新增能力

- **在线才投送**：`SystemLauncher.DescribeSessionOnline` 以 WTS 会话状态为准
  （Active/Connected 放行），Broker 服务端与客户端各校验一次。
  查询失败时退回 `query user`，不因查询问题一刀切拒绝。
- **AUTH 分步日志**：`AUTH_BEGIN → AUTH_CLIENT → AUTH_OK/AUTH_FAIL`，
  每步带 PID/用户/SID/会话/admin/elevated。

## 新增验证工具（tools/layout-check）

- `BrokerAuthCheck`：管理员/SYSTEM/非管理员的令牌判定与在线门槛。
- `BrokerDeployCheck`：端到端投送 + 自检 + 回收。
- `ProxyRaceCheck`：并发生成代理与并发投送（可复现本轮第 7 项缺陷）。
- `ChannelDetectCheck`：通道被界面识别的断言。

## 未验证

UAC 开启的 linked token 分支、冷启动时间窗口、锁屏与会话切换、
真实游戏内输入响应、杀软拦截异常路径。
