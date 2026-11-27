# 桌面版发布规则

## 发布到 GitHub Releases

- 主程序版本取自 `SoulLink.csproj`。代理和 Windows Broker 分别使用各自项目的版本，组件有行为或依赖变化时才升各自版本。
- GitHub Actions 手动运行只构建测试产物。推送与项目版本一致的 `v主版本` 标签时，Actions 用锁定的 .NET SDK 构建 Windows x64 版，先把四个 ZIP 上传到草稿，核对大小和 SHA-256 后再公开发布。已发布的同名版本不得覆盖。
- 发布前检查该标签所含提交，遵循 AGENTS.md 的桌面端、Android 和服务器分流规则。包从干净的发布输出按允许列表生成。用户配置、账号数据、日志、`dist/`、`Server/`、密钥和服务器部署文件禁止进包。
- 本地验证命令为 `pwsh -File scripts/publish-release.ps1 -Tag v3.3.67`。默认生成到 `artifacts/github-release/`，可用 `-OutputDirectory` 指定路径。

## 下载和应用内更新

- 手动下载使用 `SoulLink-主版本-win-x64.zip`，包含主程序、更新器、代理、Windows Broker 和本地辅助工具。要求 .NET 11 Windows Desktop Runtime x64。
- 客户端只读仓库正式 Release。主程序从 `update-主版本.zip` 下载完整客户端文件，适用于跨版本升级。代理和 Broker 使用各自版本号的独立 ZIP，只有程序目录缺失、二进制身份不符或版本低于发布版本时，才下载对应组件。
- 所有下载包由 GitHub Releases API 提供字节数和 SHA-256，客户端核验后交由独立更新器替换程序目录。应用更新不会重装 Windows Broker 服务。服务安装和修复使用随包的独立 Broker 与代理。
- 已安装的引擎组件版本过旧时，顶栏和设置会指出要更新的组件。主程序以管理员身份运行时会自动尝试安装随包组件。普通权限下在设置中点击「更新引擎组件」并完成 Windows 授权。代理文件可在线更新，运行中的会话在下次投送时启用新版。Broker 会等代理空闲后再更新。
- 仍使用旧更新源的客户端需首次手动下载完整包，后续可点击主程序窗口上的版本号检查更新。

## 发布验证

1. 确认完整包可启动，应用内更新器可独立执行。
2. 核对主程序、代理、Broker 的 PE 身份与各自版本。
3. 验证组件相同或更高版本时跳过、组件缺失或低版本时补齐。
4. 检查公开 Release 的四个 ZIP 和 SHA-256。

---

## 10. 定时器与页面生命周期规范

### 10.1 新增周期任务

```csharp
// ✅ 正确：走统一节拍
_sub = Services.TickHub.Register("模块名", 秒数, () => { ... });

// ❌ 禁止：新建独立 DispatcherTimer
new DispatcherTimer { Interval = ... };
```

6 个各自独立的 1 秒级定时器曾让线程池长期自旋，
空闲 CPU 因此高达 27%（未连任何 RDP）。

### 10.2 页面可见性

`MainWindow.SetActivePage` **只切 Visibility，不会触发 Loaded/Unloaded**。

任何「只在页面可见时需要」的定时器/订阅/轮询，必须实现：

```csharp
public void OnPageActivated()   { /* 恢复 */ }
public void OnPageDeactivated() { /* 暂停 */ }
```

并在 `SetActivePage` 里调用。

> 已知案例：控制台页的 150ms 保护层看门狗因此空转了很长时间，
> 是「切菜单越来越卡」的主因之一。

---

## 11. 诊断代码规范

### 11.1 取句柄数

```csharp
// ❌ 每次约 3~5ms 系统查询，挂在 1Hz 循环里会自己吃掉 20% CPU
Process.GetCurrentProcess().HandleCount;

// ✅ 约 1 微秒，配合 1 秒缓存
[DllImport("kernel32.dll")] static extern bool GetProcessHandleCount(IntPtr, out uint);
```

### 11.2 采信数据前先确认工具开销

曾因为探针自身消耗导致误判「`RefreshOnce` 泄漏句柄」，
排查了好几轮才发现是工具本身的问题。

### 11.3 句柄增长的判别

| 特征 | 正常爬坡 | 真泄漏 |
|---|---|---|
| 趋势 | 上升后趋于平缓 | 持续线性上升 |
| 增长率 | 递减 | 恒定 |

.NET 应用启动时句柄单调上升是正常的（线程池、JIT、GC 分代），
**必须观察足够长时间（>15 分钟）才能断定是否泄漏**。
