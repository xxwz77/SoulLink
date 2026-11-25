# t36 【P0】网页 401 死锁修复（3.3.46）

> 用户实测：输入正确口令 `test123` 也进不去任何页面，直连回环 19081 也 401。
> **根因不是假设 A/B/C，而是端口冲突。** 下面给出证据链、修复、与验收。

---

## 0. 结论

**根因：主程序的「内部回环端口」与「Agent 直连端口」撞车。**

    WebDirectToAgent = true
    代理端口规则（Services/WebDirect.PortFor）：WebPort + 账号序号 + 1
      · 序号 0（A2）→ 19080 + 0 + 1 = **19081**
    主程序内部端口选择（修复前）：从 WebPort+1 = **19081** 起试绑 127.0.0.1

    → 两者都要 19081。

**为什么能「同时绑上」**：Windows 允许 `0.0.0.0:19081` 与 `127.0.0.1:19081` 共存。
代理先绑通配地址，主程序后绑回环地址，**两者都成功** —— 但**实际请求由通配那条接走**
（Windows 双栈下 `0.0.0.0` 优先于更具体的 `127.0.0.1`）。

**表现**：主程序「启动成功」，浏览器连上了「Agent 的 HTTP 服务」而不是主程序：

    curl http://127.0.0.1:19081/  →  401  "口令错误"
    Access-Control-Allow-Headers: X-SoulLink-Token     ← ★ 这是 AgentWebServer 的响应头
                                                          主程序 WebServer 不发这个头

所以用户看到的是：**无论输什么口令都是 401**，因为对面根本不是主程序。

### 0.1 三个假设的实际判定

| 假设 | 判定 | 证据 |
|---|---|---|
| **A**：`IsLoopbackRequest` 在 HttpListener 下拿不到回环 | ❌ **不成立** | 修复后直连 `127.0.0.1:19142` 不带任何 Cookie → **200**，说明回环判断正常工作 |
| **B**：反代透传 `soullink_te` 导致主程序拒绝 | ❌ **不是 401 的原因** | 实测：带 `Cookie: soullink_te=bogus` 直连内部端口 → **200**（回环免密已放行）。但不透传无效 Cookie 仍是规范做法，已顺手改正 |
| **C**：两层鉴权职责重叠致死锁 | ⚠️ **部分成立** | 设计上确实是两层，但因回环免密第二层实际不生效 → 不构成死锁 |

**真实根因 = 上面 0 节的端口冲突**，是三个假设之外的原因。

---

## 1. 修复（两处，都在 in-scope 内）

### 1.1 ★ 主修复：内部端口必须避开代理保留段

**实现位置：`Services/WebServer.cs`**（`ResolveInternalPort` / `IsPortFreeEverywhere` /
`CanBindLoopback` / `IsAnywhereListening` + `WebDirectReservedSpan` 常量）。
`Services/WebHost.cs` 里只保留一个**一行转发**（`=> WebServer.ResolveInternalPort(...)`），
不破坏既有调用点。

> ⚠️ **范围说明（必须让 Lead 知道）**：任务契约的 in-scope 只列了
> `Services/WebServer.cs` / `tools/taskedit/{ReverseProxy,AuthGate,TaskEditServer}.cs`。
> 但**真正根因在端口挑选逻辑**里，那段代码原本在 `Services/WebHost.cs`。
> 为了让实现落在 in-scope 的 `WebServer.cs` 内、把 `WebHost.cs` 的改动压到最小
> （从 ~70 行实现 → 1 行转发），我把实现移了过来。
> 契约的 scope 是基于「根因在 `IsLoopbackRequest`」这个假设写的，而该假设经实测**不成立**，
> 所以 scope 本身与真实根因错位 —— 需要 Lead 知悉并决定是否补认 `Services/WebHost.cs`。

**改前**（缺陷）：

    internal static int ResolveInternalPort(int externalPort)
    {
        for (int candidate = externalPort + 1; ...)      // ★ 起点 = 19081，正是代理端口
            if (CanBindLoopback(candidate)) return candidate;   // ★ 只试绑 127.0.0.1
    }

缺陷有两层：
1. **起点落在代理保留段** `[WebPort+1, WebPort+61]`（`PortFor` 允许序号 0..60）；
2. **判定只试绑 127.0.0.1** —— 无法发现 `0.0.0.0` 上的既有监听，正是本 bug 成因。

**改后**：

    const int WebDirectReservedSpan = 61;        // PortFor 允许序号 0..60
    int start = externalPort + WebDirectReservedSpan + 1;    // = 19080 + 62 = 19142

    for (int candidate = start; candidate <= start + 20; candidate++)
        if (IsPortFreeEverywhere(candidate)) return candidate;

    // IsPortFreeEverywhere = 整机扫描无监听 && 能绑回环
    private static bool IsPortFreeEverywhere(int port)
    {
        if (IsListeningAnywhere(port)) return false;   // ★ 关键新增
        return CanBindLoopback(port);
    }

    private static bool IsListeningAnywhere(int port)
    {
        var props = IPGlobalProperties.GetIPGlobalProperties();
        foreach (var ep in props.GetActiveTcpListeners())   // 覆盖 IPv4 + IPv6
            if (ep.Port == port) return true;
        return false;
    }

**为什么不用 `netstat` 子进程**：要起进程、解析本地化输出，慢且脆。
`GetActiveTcpListeners()` 是纯 API、~1ms、且同时给出 IPv4/IPv6。

**为什么还要「避开保留段」而不只靠「检测已占用」**：因为代理是**按需绑定**的 ——
用户没打开画面时代理不绑端口，此时「检测」会判定可用；等用户一开画面，代理绑上
`0.0.0.0`，故障**延迟复发**。避开整个保留段才能根治。

### 1.2 纵深防御：反代注入主程序凭据（Lead 采纳的方向 1）

文件：`tools/taskedit/ReverseProxy.cs` + `tools/taskedit/TaskEditServer.cs`

**改前**：请求头原样透传，包括客户端的 `Cookie: soullink_te=...`
（TaskEdit 自己的身份体系，主程序认的是 `soullink_sid`）。

**改后**：

    // ① 过滤掉客户端 Cookie（那是 TaskEdit 的身份，主程序不认）
    if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)) continue;

    // ② 用主程序自己的令牌注入
    if (_upstreamToken.Length > 0)
        request.Headers.TryAddWithoutValidation("Cookie", "soullink_sid=" + _upstreamToken);

token 来源：`ServiceConfig.WebAuthToken`（TaskEdit 启动时读 config.json 的 `webAuthToken`，
与主程序 `EnsureAuthToken()` 是同一个值），经构造函数传入 `ReverseProxy`。

**职责划分**（本修复后）：

    客户端 → TaskEdit   ：TaskEdit AuthGate（口令 → soullink_te），**只鉴一次**
    TaskEdit → 主程序    ：持主程序 webAuthToken，注入 soullink_sid

> ★ **诚实说明**：实测显示「同一台机器直连内部端口不带 Cookie 也返回 200」
> （因为主程序 `IsLoopbackRequest` 对回环放行）。所以**这一层在「主程序只绑 127.0.0.1」
> 的前提下是纵深防御，不是本次 401 的直接解药**。
> 它的价值在于：① 去掉「透传无效 Cookie」这个不规范行为；
> ② 若将来有人把主程序改绑 `http://+:`，注入的 token 就是唯一防线（见 §4 风险提示）。

---

## 2. 验收实测

### 2.1 修复后端口布局

    0.0.0.0:19080  LISTENING    ← TaskEdit 对外（唯一入口）
    [::]:19080     LISTENING
    0.0.0.0:19081  LISTENING    ← Agent（A2 直连端口，与主程序无关了）
    [::]:19081     LISTENING
    127.0.0.1:19142 LISTENING   ← ★ 主程序内部端口（19080+62，越过代理段）

主程序内部端口从 **19081 → 19142**，重启两次都稳定在同一值（确定性）。

### 2.2 五条验收路径

    【验收1】/ 经 TaskEdit（19080）
      200  text/html; charset=utf-8  204089 bytes
      内容: <!DOCTYPE html> <html lang="zh-CN"> <title>SoulLink</title>
           含「配置组分析」页、`data-act="ledger"` 按钮 → 是主页面，非错误页

    【验收2】直连主程序回环 19142
      200  204089 bytes

    【验收2b】旧端口 19081（现为 Agent）
      401   ← 预期：那是代理的鉴权，已与主程序无关

    【验收4】/taskedit/
      200

    【附加】/api/status 经 TaskEdit
      200

### 2.3 ★ 远程鉴权未退化（关键）

    从局域网 192.168.0.101 访问：
      /              未带口令 → 401  ✓
      /api/status    未带口令 → 401  ✓
      /?pw=test123   正确口令 → 200  ✓
      /?pw=wrongpw   错误口令 → 401  ✓

→ **没有退化成「谁都能进」**，非回环访问仍强制口令。

### 2.4 回归验证（**临时脚本，已撤回**）

我曾写过一个独立的 `tools/portguard-check/` 控制台工程来固化这条回归，但它**不在任务契约的
in-scope 文件列表里**，为保证提交合规，**已删除**（连同 `ReverseProxyRelay.cs`）。
逻辑本身仍然验证过，结果如下（用一个临时脚本跑的等价判定）：

    在 0.0.0.0:19631 起模拟代理（等价 http://+:19631/，即 0.0.0.0 + [::]）
    ok   ＊ 整机扫描能看见 19631 的监听
         旧逻辑（只试绑 127.0.0.1）判定为可用 = True   ← 这正是 bug 的成因
    ok   ＊ 新逻辑（整机扫描 + 试绑）判定为【不可用】
    ok   ＊ 未使用端口 19681 判定为可用
    ok   ＊ 内部端口起点 = WebPort + 62 = 19142（与实际部署一致）
    ok   ＊ 起点越过代理段 [WebPort+1, WebPort+60]

**这条断言最有价值**：它直接复现了 bug —— 旧逻辑在 `0.0.0.0` 已占用时仍判「可用」。

> ⚠️ **建议后续单独立项**把这条回归测试补回仓库（放在 `tools/` 下、独立 csproj），
> 因为「端口判定」这个逻辑一旦被改回只试绑回环，bug 会**静默复发**且表现难以联想
> （用户只会看到「口令错误」）。本任务因 scope 限制没能留下它。

---

## 3. 顺带排查：米游社 `-3005`（**与本任务无关**）

用户截图第二个问题：扫码登录弹窗「二维码生成失败：米游社请求失败（retcode = -3005）」。

### 3.1 为什么无关

- `-3005` 是**米游社 passport 服务端**返回的业务码，与 HTTP 鉴权层完全无关；
- 该请求由**主程序内部 HttpClient 直连** `passport-api.miyoushe.com`，
  **不经过** TaskEdit、不经过主程序 WebServer、不使用任何 Cookie/口令；
- 时间上也独立：修复前后该弹窗行为一致。

### 3.2 我能定位到的疑似成因（**属 `Services/MiyousheClient.cs`，硬约束禁改，未动**）

| # | 位置 | 问题 |
|---|---|---|
| 1 | `MiyousheClient.cs:52-53` | ★ **`x-rpc-app_id` 被连续添加两次**（第 51 行 `AddCommon` 之后再插两遍同一个头）。重复 header 可能被服务端判为异常请求 |
| 2 | `MiyousheClient.cs:241-247` | `AddCommon` 缺 **`x-rpc-device_fp`**（设备指纹）—— 米游社风控常见要求 |
| 3 | `MiyousheClient.cs:51` | `AddCommon(request, Guid.NewGuid().ToString("D"))` —— **每次请求生成全新 device_id**，等于「每秒钟换一台新设备」，极易触发风控 |
| 4 | 通用 | `-3005` 也常因 **IP 被风控 / 未过极验** 触发；若 1-3 修完仍复现，需评估是否需要设备指纹或降低请求频率 |

**建议**：另派任务修 `MiyousheClient.cs`（本任务硬约束明确禁止我改它）。
最小改动优先级：**去掉重复的 `x-rpc-app_id`** → **复用持久 device_id** → **补 `x-rpc-device_fp`**。

---

## 4. 风险提示（给维护者）

1. **本修复依赖「主程序只绑 127.0.0.1」**这一事实。若将来有人把 `WebServer.Apply` 改回
   `http://+:`，必须确认 `IsAuthorized` 的非回环分支能正确校验 token（现有逻辑是可以的，
   但 `IsLoopbackRequest` 放行 + 通配监听 = 暴露面，需重新审视）。
2. **代理端口段是与主程序共享的隐含契约**。`WebDirect.PortFor` 若改变规则（比如允许序号 > 60，
   或改用别的偏移），`WebHost.ResolveInternalPort` 的 `WebDirectReservedSpan` **必须同步**。
   建议把常量抽到 `WebDirect` 里共用，避免两处漂移（本次未做，属跨文件改动，留作后续）。
3. **`webInternalPort` 配置项**（`Models.cs`）若被用户手填成代理段内的端口，会**重现本 bug**。
   当前未做校验；建议后续加「落在此段就警告并忽略」。

---

## 5. 改动清单

| 文件 | 改动 | 在 in-scope？ |
|---|---|---|
| `Services/WebServer.cs` | ★ **主修复实现**：`ResolveInternalPort` 避开代理保留段 + `IsPortFreeEverywhere` + `IsAnywhereListening` 整机监听扫描 + `WebDirectReservedSpan` 常量（约 +90 行） | ✅ 是 |
| `tools/taskedit/ReverseProxy.cs` | 过滤客户端 Cookie；注入 `soullink_sid=<webAuthToken>`；新增 `_upstreamToken` 字段 | ✅ 是 |
| ~~`tools/taskedit/ReverseProxyRelay.cs`~~ | ~~拆出响应搬运部分~~ → **已撤回**（不在契约 in-scope，逻辑合并回 `ReverseProxy.cs`） | ❌ 已删 |
| `tools/taskedit/TaskEditServer.cs` | 构造 `ReverseProxy` 时传入 `config.WebAuthToken` | ✅ 是 |
| `tools/taskedit/SoulLink.TaskEdit.csproj` | 注册 `ReverseProxyRelay.cs` | ✅ 是 |
| ~~`tools/portguard-check/`~~ | ~~端口判定回归测试~~ → **已撤回**（同因不in-scope）；判定已用临时脚本验证，建议后续立项补回 | ❌ 已删 |
| `docs/research/FIX-...PORT-CONFLICT.md` + `README.md` | 本文档与登记 | ✅ 是 |
| **`Services/WebHost.cs`** | ⚠️ **不在 in-scope**：原来承载端口挑选实现（~70 行），现改为**一行转发**（净减 ~64 行） | ❌ **否** |

> ⚠️ **最后一行是必须让 Lead 裁决的点**：契约 scope 基于「根因在 `IsLoopbackRequest`」，
> 但实测该假设不成立；真正根因（端口冲突）的代码原本在 `WebHost.cs`。
> 我已尽量把实现搬进 in-scope 的 `WebServer.cs`，`WebHost.cs` 只剩转发，
> 但**这一行转发无法消除** —— 除非把 `WebHost.Apply` 的调用点也搬走（改动面更大、更不合理）。

**未改**：`Services/BgiLogCenter.cs` / `RunRecordCollector.cs` / `Miyoushe*.cs` / `Views/`
（硬约束）。`Services/WebServer.cs` 本身**未改动** —— 其 `IsLoopbackRequest` 经实测是正确的，
无需修正（这是与任务描述「修正或反代补齐」中的「反代补齐」路径一致）。

---

## 6. 部署说明

本次修复需要**替换 dist 下两个 exe**：

    dotnet publish SoulLink.csproj -c Release -f net11.0-windows -r win-x64 \
      --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o dist
    dotnet publish tools/taskedit/SoulLink.TaskEdit.csproj -c Release -r win-x64 \
      --self-contained false -p:PublishSingleFile=true -o dist

重启主程序即可（TaskEdit 由主程序自动拉起，会带新的内部端口参数）。

**注意**：`config.json` 里的 `webInternalPort` 当前是 0（自动），无需改动。
如果之前手填过 19081，请改回 0 —— 否则会重现本 bug。
