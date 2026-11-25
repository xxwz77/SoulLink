# SoulLink.TaskEdit 独立服务骨架（t24 / 3.3.45）

> 依据 [TASK-EDITOR-DESIGN-20261125.md](TASK-EDITOR-DESIGN-20261125.md) §11（进程与部署架构）、
> §17（远程访问与性能）、§18（请求路由表与反向代理风险）。
> **本任务只做 P0：骨架与通道，不做编辑界面、不读写任何配置内容。**

---

## 0. 结论摘要

| 项 | 结果 |
|---|---|
| 独立服务工程 | `tools/taskedit/SoulLink.TaskEdit.csproj`，**单文件 133.6 KB**（框架依赖） |
| TFM | `net11.0-windows`（不带版本号）—— 理由见 §2 |
| 主程序双 TFM Release | **0 error**（4 警告，均为既有） |
| 端口唯一 | `netstat` 实测：只有 WebPort 对 `0.0.0.0`/`[::]`，内部端口只对 `127.0.0.1` |
| 反代一致 | 直连内部端口与经反代的 `/api/status` **字节完全相同**（4086 字节；逐字节一致=True） |
| Range 流式 | `206` + `Content-Range: bytes 0-1023/200000` 正确透传 |
| 鉴权 | 本机免密 200；局域网未带令牌 401；带正确口令 200；5 次失败后锁定（正确口令也被拒） |
| gzip | JSON **4086 → 146 字节（96.4% 压缩）**；视频路径 `206` **无 Content-Encoding** |
| 降级 | 停掉上游 → **200** + 「主程序未运行」页（不是 500 白屏），含处置建议 |
| 单实例 | 第二个实例明确提示并以 exit=5 退出 |

---

## 1. 工程位置与文件清单

```
tools/taskedit/
  SoulLink.TaskEdit.csproj     发布配置（框架依赖 + 单文件，仿 Agent 模式）
  Program.cs            174 行  入口：参数、单实例、优雅退出
  ServiceConfig.cs      140 行  读 config.json（零 WPF 依赖）
  AuthGate.cs           201 行  强制鉴权 + 防暴力 + 会话 Cookie
  Compression.cs        113 行  gzip 判定与实现
  Pages.cs               75 行  登录页 / 占位页 / 降级页（HTML 与逻辑分离）
  ReverseProxy.cs       282 行  流式反代（零缓冲）
  TaskEditServer.cs     227 行  监听、路由、健康检查、登录入口
  TaskEditLogin.cs       85 行  登录处理（partial class）
```

每个源文件均 **< 300 行**（队内硬约束）。

### 1.1 ★ 完整新增文件清单（9 个，合并时逐个核对）

```
tools/taskedit/SoulLink.TaskEdit.csproj
tools/taskedit/Program.cs
tools/taskedit/ServiceConfig.cs
tools/taskedit/AuthGate.cs
tools/taskedit/Compression.cs
tools/taskedit/Pages.cs
tools/taskedit/ReverseProxy.cs
tools/taskedit/TaskEditServer.cs
tools/taskedit/TaskEditLogin.cs
```

**主程序侧新增 2 个文件**：

```
Services/WebHost.cs                              （网页入口宿主：端口分配 + 起停子进程 + 实例复用）
docs/research/IMPL-20261125-TASKEDIT-P0.md       （本文）
```

**主程序侧修改 7 个文件**：

| 文件 | 改动 |
|---|---|
| `Services/WebServer.cs` | 绑回环 + 去掉放行短路 + 不管防火墙 |
| `Services/WebFirewall.cs` | `EnsureAllowed` 改单端口；旧范围逻辑改名 `EnsureRangeAllowed` |
| `Models/Models.cs` | 新增 `AppSettings.WebInternalPort` |
| `App.xaml.cs` | 启动/退出改走 `WebHost` |
| `Views/SettingsPage.xaml.cs` | 改端口/口令走 `WebHost`；状态文案更新 |
| `SoulLink.csproj` | `<Version>` 3.3.44 → **3.3.45** |
| `.gitignore` | 追加 `!tools/taskedit` 白名单（9 条） |

> **为什么必须动 .gitignore**：`.gitignore:68` 有 `tools/*` 全局忽略。
> 不追加白名单，上面 9 个文件**不会进版本库**，交付物等于丢失。
> 沿用的是 `tools/updater` / `tools/rescue` / `tools/view-demo` 同款范式
> （先 `!tools/<dir>` 再逐文件例外），位置也保持一致 —— 独立进程放 `tools/` 是既有约定。

---

## 2. TFM 选择理由

选 **`net11.0-windows`（不带版本号）**，不是主程序的 `net11.0-windows10.0.26100.0`：

1. 本服务只用 `HttpListener` + `System.Text.Json` + `GZipStream`，**不需要任何 WinRT 投影**；
2. 带版本号的 TFM 会拉进 `Microsoft.Windows.SDK.NET.dll`（约 26 MB）与 `WinRT.Runtime.dll`，
   单文件包直接翻倍 —— 与「体积小」的发布目标冲突；
3. 与 `Agent/SoulLink.Agent.csproj` 保持同一形态，维护成本最低。

主程序是多目标（两个 TFM 都编），本服务**只需一个能跑的 TFM**，两者解耦、互不影响。

---

## 3. 配置读取方式（为什么不起引用主程序）

### 3.1 结论

**自己读 `config.json`，用 `JsonDocument` 只取需要的几个标量**，不引用主程序程序集。

### 3.2 为什么不 ProjectReference 主程序

主程序是 WPF 工程（`UseWPF=true` + `UseWindowsForms=true`）。一旦引用它：

- 被迫拖进 `PresentationFramework` / `PresentationCore` / `WindowsBase` / `System.Windows.Forms` 全套 UI 依赖；
- 单文件包从 ~0.13 MB 膨胀到 5 MB 以上；
- 直接违背「本服务不引用 WPF」的硬约束。

### 3.3 为什么不新建共享库

把 `AppSettings` 从 WPF 工程整个搬出来，改动面覆盖全部既有调用点，
风险远大于「只读 4 个标量字段」的收益。**P0 不值得为它动主程序的核心模型。**

### 3.4 实际读哪些字段

| 字段 | 用途 | 缺失时 |
|---|---|---|
| `settings.webPort` | 对外监听端口（0 = 不启动） | 打印提示并 exit=3 |
| `settings.webPassword` | 远程访问口令 | 走 §4.3 自动生成令牌 |
| `settings.webAuthToken` | 持久会话令牌 | 首次自动生成并尝试写回 |
| `settings.webInternalPort` | 内部端口的**兜底**来源 | 由命令行参数提供（主要通道） |

**不读也不写**：任何账号、BGI 路径、`User/ScriptGroup` 等配置内容 —— 那是 P1 数据面的事。

### 3.5 数据目录判定

直接复用 `Services/AppIdentity.cs`（纯路径逻辑、零 WPF 依赖，以 `<Compile Include>` 引用而非复制），
保证与主程序**同一套**路径规则：`SOULLINK_DATA_DIR` → `exe 目录\data` → `%LOCALAPPDATA%\<身份名>`。

---

## 4. 内部端口传递方式

### 4.1 选型：**命令行参数为主 + config.json 兜底**

    SoulLink.TaskEdit.exe --port <对外端口> --internal-port <内部端口> [--no-firewall] [--check]

**理由**：

| 方案 | 问题 |
|---|---|
| ✅ 命令行参数 | 一次性、随进程生命周期自然失效，**不落盘、不留残留**；时序上启动即有 |
| ❌ 临时文件 | 残留的旧文件会误导（崩溃后下次读到过期端口） |
| ❌ 注册表 | 要写权限，且清理不干净就是永久垃圾 |
| ❌ 动态探测端口 | 同机可能有多个 SoulLink 实例，猜错会把请求转给别人的服务 |

**为什么不用 IPC 传**：IPC 是给「命令调用」用的（有长度上限与并发语义），
用它传一个进程启动参数属于杀鸡用牛刀；而且独立服务必须先知道端口才能开始监听。

### 4.2 内部端口怎么选（主程序侧）

`Services/WebHost.ResolveInternalPort()`：
从 `WebPort + 1` 起往上找第一个**能真正绑上 127.0.0.1** 的端口（最多试 20 个）。
- 不写死：用户改 WebPort 时写死值必然不一致；
- 不用随机：容易被安全软件当异常，排查时也不好认；
- **必须实际绑一次**才算数 —— 只看「是否被监听」会漏掉 TIME_WAIT 占用。

用户也可在 `AppSettings.WebInternalPort` 写死（默认 0 = 自动）。

### 4.3 令牌/口令传递

- 口令通过**环境变量** `SOULLINK_WEB_PASSWORD` 传给子进程，**不出现在命令行**
  （命令行对同机其他用户可见）；
- 独立服务也会自己读 `config.json` 的 `webPassword`，两条通道任一可用即可。

---

## 5. 路由表实现（§18.1）

    /taskedit/health          → 本服务（健康检查，**不需要鉴权**，给主程序探活/穿透保活）
    /taskedit/login           → 本服务（登录页 GET / 提交 POST，唯一不需已登录的入口）
    /taskedit/ 、/taskedit/*  → 本服务（P0 占位页；其余 404「将在 P1 提供」）
    其余全部                   → 鉴权通过后**流式反代**到 http://127.0.0.1:<internal>/
                                （/、/api/bgi/*、/api/miyoushe/*、/api/sessions、/api/status、
                                  /api/live、/file/*、静态资源 ……）

**路由判断只有一次前缀比较**（`/taskedit`），不做路径枚举 ——
新增页面/接口时**不需要改本服务**，天然符合「不改现有页面与 API 行为」。

---

## 6. 反向代理实现（§18.2 风险应对）

### 6.1 流式转发，零缓冲

    HttpCompletionOption.ResponseHeadersRead   // 拿到响应头就返回
      → 边读边写（64 KB 单块驻留）

**绝不整段缓冲**：现有页面有录像流（`WriteStreamWithRange`）与分块视频，
整段缓冲会直接把内存打爆，而且会让视频进度条失效。

内容长度已知时设 `ContentLength64`，未知时 `SendChunked = true` —— 两种都原样透传。

### 6.2 透传清单

| 类别 | 透传内容 |
|---|---|
| 请求 | 方法、路径、查询串、请求体（`StreamContent` 流式）、`Range`、`Cookie`、`Accept-Encoding`、`Content-Type` 及其余全部头 |
| 响应 | 状态码、`Content-Range`、`Content-Type`、`Set-Cookie`（多个用逗号合并）、其余全部头 |
| 附加 | `X-Forwarded-For` / `X-Forwarded-Proto` / `X-Forwarded-Host`（仅诊断，**不参与鉴权**） |
| 剥离 | hop-by-hop 头（`Connection` / `Keep-Alive` / `Transfer-Encoding` / `Upgrade` / `TE` / `Trailer`） |

`UseCookies=false` 且逐请求透传 Cookie —— 不能让 handler 自己维护容器，否则多浏览器会话会串味。
`AllowAutoRedirect=false` —— 302 必须原样交给浏览器，不能在代理里跟掉。

### 6.3 关于「内部令牌」

**没有加内部令牌**。理由：主程序监听的是 `127.0.0.1` 回环，**物理上只有本机可达**，
而本服务本身就跑在同一台机器上。加一个内部令牌只会多出一处需要保管的秘密，
收益为零。这也让主程序侧**零改动、零新凭证**。

> ⚠️ **前提必须写死（Lead 2026-11-25 要求）**
>
> **回环端口的安全性依赖「只绑 `127.0.0.1`」这一事实。**
> 若将来有人把主程序的监听改回 `http://+:`（所有网卡）或把内部端口对外开放，
> **必须同步加鉴权**（最小方案：恢复内部令牌头并让反代带上）。
> 当前「无内部令牌」不等于「不需要鉴权」—— 它只是「回环已经提供了物理边界」这一前提的推论。
> 改动这里的人必须先确认这个前提是否还成立。

### 6.4 主程序未运行时的降级

返回 **200** + 明确的中文说明页，不是 500：

- 这是**用户可理解的服务状态**，不是服务器内部错误；
- 浏览器与穿透工具看到 500 会当故障重试/报警，看到 200 + 说明才符合预期；
- 页面上直接说「主程序未运行」并列出三条处置建议（确认 exe 已启动 / 稍等刷新 / 查 error.log）。

---

## 7. 鉴权实现（§17.2 定稿）

### 7.1 规则

| 来源 | 处理 |
|---|---|
| **回环**（127.0.0.1 / ::1 / IPv4-mapped） | **免密**（本机用户点开就用） |
| **非回环** | **强制令牌**，无例外 |
| 令牌与口令都为空 | **拒绝**并提示去设置口令（★ 不复制主程序的「没设密码就放行」） |

### 7.2 令牌来源（优先级）

1. `settings.webPassword`（用户设的口令，口令即凭据）
2. 已生成的持久 `settings.webAuthToken`
3. **自动生成**一个新令牌 → 打印到控制台 + 尝试写回 config.json

自动生成时的控制台输出是一个醒目的方框，直接给出令牌值（用户在本机看得到）。

### 7.3 会话

登录成功下发 `soullink_te` Cookie，**1 年有效期**（`Expires` + `Max-Age` 双保险，
与主程序 `IssueSession` 同思路）。同时也接受主程序原有的 `soullink_sid`，避免老用户重登。

### 7.4 防暴力

- 同一来源 IP 连续失败 **≥5 次 → 锁定 60 秒**；
- 锁定期间**即使口令正确也拒绝**（否则锁定形同虚设）；
- 按 IP 隔离而非全局（一个攻击者不该把合法用户一起锁死）；
- 桶数超 512 时清理已解锁项，防内存膨胀；
- 口令比较用 `CryptographicOperations.FixedTimeEquals`（定长比较，避免计时侧信道）。

### 7.5 主程序侧同步收紧

**去掉了 `IsAuthorized` 里的 `if (!PasswordEnabled()) return true;` 短路**：
- 非回环 + 无口令 **一律拒绝**（原来是放行 —— 暴露到局域网就等于裸奔）；
- 回环来源直接放行（主程序只绑 127.0.0.1，外部连接只可能来自已经强制过口令的独立服务）。

---

## 8. gzip 实现（§17.3）

### 8.1 两条硬规则

1. **只压文本**：JSON / HTML / CSS / JS / XML / SVG / CSV。
   判断依据**先看 `Content-Type`**（不看扩展名 —— 同一路径可能返回 JSON 也可能返回视频）。
2. **绝不压流式与大二进制**：
   - 存在 `Content-Range` 的 **206 分片** → 原样透传（压缩会破坏 Range 字节语义）；
   - `Content-Length` **未知**（chunked 流）→ 不压；
   - 小于 **1024 字节** → 不压（gzip 头尾各 10 字节，小响应反而变大）；
   - `video/*`、图片、字体等已压缩类型 → 不压。

### 8.2 判定函数

    Compression.ShouldCompress(acceptEncoding, contentType, contentRange, contentLength, isHead)

`Accept-Encoding` 解析支持 `gzip` / `x-gzip` / `*`，并正确忽略 `q=0`（明确拒绝）。

### 8.3 阈值与 SSE 边界（Lead 2026-11-25 追问，已确认并实测）

| 边界 | 处理 | 实测 |
|---|---|---|
| 小于 **512 字节** | **不压** | `/api/tiny`（30 字节）→ 无 `Content-Encoding`，`Content-Length: 30` |
| `text/event-stream`（SSE） | **不压、不缓冲** | `/events` → `Transfer-Encoding: chunked`，**无** `Content-Encoding`，3 条 `data: tick` 全部到达 |
| `Content-Length` 未知（chunked 流） | 不压（流式判定） | 同上 |
| 206 分片 / `Content-Range` | 不压 | 视频 206 无 `Content-Encoding` |

阈值从初版的 1024 下调为 **512**（Lead 要求）：gzip 固定开销约 18 字节，但小响应压缩率极差，
300 字节的极简 JSON 压完常只小几十字节甚至变大。512 能确保 `/api/status` 之类极简响应
**不被无意义地压**，同时 4 KB 级 JSON 仍保持 90%+ 压缩率（实测 4086→86，**97.9%**）。

SSE 的实现要点：`IsCompressibleType` 对 `text/` 前缀一律放行，**但显式排除 `text/event-stream`**；
反代侧对长度未知的流走 `SendChunked = true` 且**逐块 Flush**，不做任何缓冲 —— 否则 SSE 会「攒着不发」。

---

## 9. 主程序侧改动（最小侵入）

| 改动 | 文件 | 说明 |
|---|---|---|
| 只绑回环 | `Services/WebServer.cs` | `http://+:port` → `http://127.0.0.1:port`；不再「降级试 localhost」 |
| 去掉放行短路 | `Services/WebServer.cs` | `IsAuthorized` 收紧（§7.5），新增 `IsLoopbackRequest` |
| 不再管防火墙 | `Services/WebServer.cs` | 主程序回环端口不需要放行；`Stop()` 也不再 `WebFirewall.Remove`（否则会删掉独立服务的规则） |
| 新增设置 | `Models/Models.cs` | `AppSettings.WebInternalPort`（默认 0 = 自动） |
| 新增宿主 | `Services/WebHost.cs` | 选内部端口、起/停独立服务进程、监控启动失败的明确报错 |
| 接线 | `App.xaml.cs` | 启动/退出改走 `WebHost` |
| 设置页 | `Views/SettingsPage.xaml.cs` | 改端口/口令即时生效（走 `WebHost.Apply`）；状态文案改为「远程需令牌」 |
| 防火墙单端口 | `Services/WebFirewall.cs` | `EnsureAllowed(port)` 改为**单端口**；旧范围逻辑保留为 `EnsureRangeAllowed` |

**页面与 API 的内容一行未改** —— 只搬了入口。

---

## 10. 实例复用：主程序重启后不重复起进程（Lead 2026-11-25 要求）

### 10.1 要解决的问题

主程序重启（崩溃 / 用户重开）时，独立服务**还活着并占着对外端口**。
如果主程序直接 `Process.Start` 再起一个：

- 新实例撞上单实例 Mutex → `exit=5` 立即退出；
- 对外端口仍然指向**旧的内部端口**（旧主程序已释放）→ 全站返回「主程序未运行」；
- 用户看到的现象是「重启主程序后网页就废了，必须手动杀进程」。

### 10.2 处理方式：**探测已运行实例并复用**

主程序 `WebHost.Apply` 在启动子进程**之前**先走 `TryReuseRunningInstance(externalPort, internalPort)`：

    ① GET http://127.0.0.1:<WebPort>/taskedit/health      （1200ms 超时）
    ② 响应里必须含 "SoulLink.TaskEdit"                      ← 关键判据
    ③ 是我们的服务 → POST /taskedit/rebind {"internalPort": N}
    ④ 切换成功 → 复用，_proc 留空（不归本次主程序管，退出时也不去 Kill 别人的进程）
    ⑤ 任何一步失败 → 回退到「正常启动一个新实例」

**为什么判据是 health 里的 `service` 字段而不是「端口是否被监听」**：
端口可能被**别人的程序**占着（用户把网页端口填成了别的服务在用的端口）。
只看「有人听」就去 rebind，等于把请求转发到一个陌生服务上。

### 10.3 独立服务侧的 `/taskedit/rebind`

| 项 | 规则 |
|---|---|
| 方法 | 仅 `POST`（GET 返回 405） |
| 来源 | **仅回环**（非回环 403）—— 远程绝不能改我们的上游指向 |
| 参数 | `?internalPort=N` 或 body `{"internalPort":N}` |
| 校验 | `1024 ≤ N ≤ 65535`，否则 400 |
| 效果 | `ReverseProxy.Rebind(N)` 热切换 `_baseUrl`，**不重启监听、不断现有连接** |
| 日志 | `[TaskEdit] 上游已切换到 127.0.0.1:N（主程序重启后复用本实例）` |

### 10.4 实测（模拟主程序重启两次）

    初始：服务 19300 → 上游 A 19301
    杀掉 A，起上游 B 19401
      /whoami → 「主程序未运行」页         ← 旧端口已死，符合预期
      rebind 到 19401 → {"ok":true,"internalPort":19401}
      /whoami → {"port":19401}             ← 已切到新上游
      health → internalPort:19401, pid:8356  ← 仍是同一个进程

    再起上游 C 19402（模拟第二次重启）
      health 含 "SoulLink.TaskEdit" = True ← 探测判据成立
      rebind 到 19402 → {"ok":true,"internalPort":19402}
      /whoami → {"port":19402}
      服务进程数 = 1，PID 仍是 8356        ← **没有多起实例**

    局域网 IP 调用 /taskedit/rebind → 403  ← 只有本机能改上游

### 10.5 已知限制

- `rebind` 目前只换上游端口，**不改对外端口与口令**（那两者变了应当重启进程，语义上更清晰）。
- 若旧实例是**旧版本**（没有 `/taskedit/rebind`），探测会失败 → 回退到「启动新实例」→ 撞 Mutex `exit=5`。
  这种情况需要用户手动结束旧进程，或由主程序做一次版本检查（**留待 P1**）。

---

## 11. 生命周期

- **启动**：`App.ShowMainWindow` → `WebHost.Apply(WebPort, WebInternalPort, WebPassword)`
  1. 分配内部回环端口；
  2. `WebServer.Apply(内部端口)`（主程序自己的页面/接口）；
  3. `Process.Start` 拉起 `SoulLink.TaskEdit.exe`（带 `--port` / `--internal-port`）；
  4. 1.2 秒后检查是否立刻退出，退出则写一条明确的 error.log（不自动无限重启，避免崩溃循环）。
- **退出**：`App` 的 `Exit` → `WebHost.Stop()`：`Kill` 子进程（`WaitForExit(3000)`）+ 停回环 WebServer。
  **不留孤儿进程占着对外端口。**
- **独立服务自身**：单实例 Mutex（`Local\\<身份>.TaskEdit.v1`）→
  监听 → `Console.CancelKeyPress` 走优雅停止（`e.Cancel = true` 阻止强杀）→
  `AppDomain.ProcessExit` 兜底 `Stop()`。

---

## 12. 验收实测（命令 + 原始输出）

验证环境：假上游（`FakeUpstream.exe`，只用 BCL，在 19201 提供测试页面/JSON/二进制/Range）
+ 真实 `SoulLink.TaskEdit.exe`（对外 19200，内部 19201），独立沙箱数据目录，**未碰 dist\data / dist\stable**。

### 12.1 构建

    === 主程序双 TFM Release ===
      SoulLink -> bin\Release\net11.0-windows\SoulLink.dll
      SoulLink -> bin\Release\net11.0-windows10.0.26100.0\SoulLink.dll
      0 个错误

    === 独立服务 Release + 单文件 publish ===
      SoulLink.TaskEdit -> tools\taskedit\bin\Release\net11.0-windows\SoulLink.TaskEdit.dll
      0 个错误
      SoulLink.TaskEdit.exe      133.6 KB
      SoulLink.TaskEdit.version

### 12.2 端口唯一（验收 2）

    TCP    0.0.0.0:19200          0.0.0.0:0              LISTENING
    TCP    127.0.0.1:19201        0.0.0.0:0              LISTENING
    TCP    [::]:19200             [::]:0                 LISTENING

→ **对外端口 19200 在 `0.0.0.0` + `[::]`（所有网卡，可远程）；内部端口 19201 只在 `127.0.0.1`。**

### 12.3 反代一致 + Range 流式（验收 3）

    === 反代 /api/status（回环免密）===
      直连长度=4086  反代长度=4086  一致=True
      前120字符：{"ok":true,"kind":"upstream-status","accounts":[{"id":"a1","user":"A1"}],"padding":"xxxx…

    === Range 请求 ===
      status=206  Content-Range=bytes 0-1023/200000  Len=1024  Type=video/mp4

### 12.4 鉴权 + 防暴力（验收 4）

    本机局域网 IP = 192.168.0.101
    === 局域网 IP 未带令牌 → 401 ===
      status=401  长度=1258（登录页）
    === 带正确口令 → 200 ===
      status=200  长度=4086  含upstream标记=True
    === 连续 5 次错误口令 → 锁定 ===
      #1=401  #2=401  #3=401  #4=401  #5=401  #6=401
      锁定后用正确口令也拒绝 → status=401  提示含锁定=True

### 12.5 gzip（验收 5，curl 原始头，不经客户端自动解压）

    === Accept-Encoding: gzip ===
      HTTP/1.1 200 OK
      Content-Length: 146
      Content-Type: application/json; charset=utf-8
      Content-Encoding: gzip
      Vary: Accept-Encoding

    === 不带 Accept-Encoding ===
      HTTP/1.1 200 OK
      Content-Length: 4086

    实际字节：压缩传输=146  明文=4086   → **压缩率 96.4%**

    === 视频路径（Range + gzip）===
      HTTP/1.1 206 Partial Content
      Content-Length: 2048
      Content-Range: bytes 0-2047/200000
      （无 Content-Encoding → 二进制不压）

### 12.6 降级（验收 6）

    停掉上游假主程序后访问 /api/status：
      status=200  （不是 500）
      含「主程序未运行」=True
      含建议列表（SoulLink.exe）=True

### 12.7 单实例（验收 7）

    再启动一个实例：
      [TaskEdit] 已经有一个 SoulLink.TaskEdit 实例在运行，本进程退出。
                 如需重启，请先关闭现有实例（或退出主程序后再试）。
      exit=5

### 12.8 请求体 / 查询串 / Cookie 透传

    POST /api/echo?x=1&y=2  body=hello-body-12345  Cookie=upstream_cookie=A
      → {"method":"POST","path":"/api/echo?x=1&y=2","bodyLen":16,
         "cookie":"upstream_cookie=A","bodyHead":"hello-body-12345"}

方法、查询串、请求体、Cookie 全部原样到达上游。

---


### 12.9 补充验收（Lead 2026-11-25 追问的三项）

**(a) gzip 阈值 —— 小于 512 字节不压**

    --- /api/tiny（30 字节，应不压）---
      HTTP/1.1 200 OK
      Content-Length: 30          ← 无 Content-Encoding，未被无意义压缩
    --- /api/status（约 4KB，应压）---
      HTTP/1.1 200 OK
      Content-Length: 86
      Content-Encoding: gzip      ← 4086 → 86（97.9%）

**(b) SSE(`text/event-stream`) 不压、不缓冲**

    curl -s -N -D - -H "Accept-Encoding: gzip" http://127.0.0.1:19300/events
      HTTP/1.1 200 OK
      Transfer-Encoding: chunked
      Content-Type: text/event-stream
      （无 Content-Encoding）
      data: tick 0
      data: tick 1
      data: tick 2                ← 3 条事件全部即时到达，未被攒住

**(c) 实例复用（模拟主程序重启两次）**

    初始：服务 19300 → 上游 A 19301
    杀掉 A、起上游 B 19401
      /whoami → 「主程序未运行」页            ← 旧端口已死（预期）
      POST /taskedit/rebind {"internalPort":19401} → {"ok":true,"internalPort":19401}
      /whoami → {"port":19401}                ← 已切到新上游
      health → internalPort:19401, pid:8356   ← 同一进程
    再起上游 C 19402（第二次模拟重启）
      health 含 "SoulLink.TaskEdit" = True    ← 探测判据成立
      rebind → {"ok":true,"internalPort":19402}
      /whoami → {"port":19402}
      服务进程数 = 1，PID 仍是 8356           ← **没有多起实例**
    局域网 IP 调用 rebind → HTTP 403          ← 只有本机能改上游

**(d) `/taskedit/rebind` 的 POST 必须带 Content-Length**

    curl -X POST -d '...' 直接打会先被 HttpListener 拦成 411（Length Required），
    请求根本到不了 handler。这是 **HttpListener 的协议要求**，不是本服务的缺陷 ——
    主程序侧用 `HttpClient`（自动带 Content-Length），实测正常。
    `GET /taskedit/rebind` → 405 也是同理（只接受 POST）。

---

## 13. 硬约束对照

| 约束 | 落实 |
|---|---|
| 不改现有页面与 API 的行为 | 页面/接口内容一行未改；路由只有一次 `/taskedit` 前缀比较 |
| 不在 UI 线程做 IO | 独立服务无 UI；主程序侧`WebHost` 只在启动路径做一次端口探测（毫秒级） |
| 不新增 DispatcherTimer | 全程零计时器（反代/鉴权都是事件驱动） |
| 独立服务绝不读写配置内容 | P0 只读 4 个标量字段；**不读也不写**任何账号/BGI 路径/ScriptGroup |
| 分段写文件 ≤300 行 | 8 个源文件：174/140/201/113/75/282/227/85，全部 < 300 |
| 不碰 dist\stable / 不停 A1/A4 | 全程未触碰；验证用独立沙箱 + 假上游 |

---

## 14. 已知边界（如实记录）

1. **未与真主程序端到端联调**：验证用「假上游」而非真实 `SoulLink.exe`。
   理由：真主程序是 WPF 进程，启动它会拉起整套 UI/会话管理，在验证机上副作用大且不可控。
   假上游覆盖了反代需要处理的**全部响应形态**（JSON / HTML / 二进制 / Range / 206 / 查询串 / POST 体 / Cookie），
   协议层面等价。**真机联调建议**：启动主程序 → 确认 `error.log` 无「独立服务已退出」→
   浏览器访问 `http://<本机IP>:<WebPort>/` 与 `/api/status`。
2. **WebSocket 未支持**（§18.2 已预判）：当前无 WS 页面；未来需要则按设计文档改 Kestrel。
3. **验收时主工程曾被队友的并行改动短暂打断（与本任务无关）**：
   验收期间另一个任务正在改 `Services/BgiLogCenter.cs` / `Services/RunRecordCollector.cs`
   （`RunRecordCollector.Feed(...)` 新增了 `len, write.Ticks` 两个参数，而调用点尚未同步），
   主工程一度报 8 个 `CS0103`，**全部在这两个文件里，我改的文件一个错误都没有**。
   处理方式：把源码树复制到 `%TEMP%` 的隔离副本、只回退那两处未完成的签名，构建 **0 错误**；
   随后清理副本。**没有去改队友的文件。**

4. **`tools/updater` 的既存构建设施问题**（与本次改动无关，如实记录）：
   `tools/updater` 只定义 `net11.0-windows`，但主程序的 WPF 临时工程（`*_wpftmp.csproj`）
   会复用它的 `obj/` 并写入**另一个 TFM** 的 `project.assets.json`，导致偶发
   `NETSDK1005: 资产文件…没有"net11.0-windows10.0.26100.0"的目标`。
   实测：第 1 次构建 1 个错误、第 2~4 次 0 个错误；`dotnet restore tools/updater` 后即恢复。
   **建议后续给 `tools/updater` 加独立 `BaseIntermediateOutputPath`**（不在本任务范围）。
5. **注册表/防火墙规则**：验证时用 `--no-firewall` 跳过 netsh（沙箱环境无意义且需要管理员）。
   真实部署时独立服务启动会调 `WebFirewall.EnsureAllowed(WebPort)` 放行**单个端口**。
6. **令牌写回 config.json 是「插入式」的**：只在缺 `webAuthToken` 键时插入一行，
   不改动其它任何内容。若格式异常则放弃写回并提示用户手动记录（不破坏配置文件）。

---

## 15. 给后续任务（P1）的接口

- **数据面**：`/taskedit/*` 由独立服务自处理；P1 在这里实现读写 `User/ScriptGroup`、`User/OneDragon`、JS settings。
  已经在 `TaskEditServer.HandleTaskEditAsync` 里预留给 P1 的 404 分支（「将在 P1 提供」）。
- **控制面**：按 §11.5 走 `AppIdentity.IpcPipeName` 到主程序（本任务未实现，属 P1/P2）。
- **反代不需要跟着改**：新增页面/API 自动被反代覆盖（前缀判断）。
- **多账号/多实例**：`AppIdentity.Name` 决定数据目录与 Mutex 名，因此
  `SoulLink.exe` 与 `SoulLink-Stable.exe` 可各跑一个独立服务互不冲突。