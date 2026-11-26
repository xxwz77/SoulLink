# 团队绝对红线（TEAM REDLINES）

> 本文档是团队协作的最高约束，**优先级高于任何任务简报**。
> 每次启动新成员、派发新任务前必须确认。

---

## 一、成本红线（2026-11-24 用户明确要求）

### 只允许使用腾讯代理（provider = `buddy`）下的模型

| 允许的模型 | 倍率 | 适用场景 |
|---|---|---|
| `buddy / glm-5.3-flash` | x0.08 | **最便宜**，机械任务/简单查询 |
| `buddy / deepseek-v4.1-flash` | x0.11 | 快速任务 |
| `buddy / deepseek-v4-pro` | x0.51 | 日常编码/实现 |
| `buddy / glm-5.3` | x0.69 | 通用 |
| `buddy / kimi-k3.1-pro` | x0.97 | 复杂研究 |

### 绝对禁止

- ❌ `deepseek-account`（DeepSeek 官方直连，付费）
- ❌ `jet-hub-auto` 以外的任何未列出的 provider
- ❌ 任何其他付费 provider

### 例外流程

如果 buddy 下模型能力不足，**必须先向 Lead 报告**：

1. 需要什么能力
2. 为什么 buddy 下模型不够
3. 预估成本

由 Lead 请示用户批准后才能使用其他模型。**不许自行切换。**

### 硬性执行要求

- 所有 `dsh_team_spawn` 必须显式写 `agentProvider: "buddy"`
- 不使用 provider 默认继承（可能继承到付费模型）

---

## 二、生产环境红线

> ★ 2026-11-24 用户明确重划边界，以本节为准。
> 旧的「按 PID 记红线」已作废 —— PID 每次重启都变，按 PID 记本身就是错的记录方式。
> **一律按「账号 / 目录」判归属，不按 PID。**

### 归属（实测：`query user` + `Win32_Process` 的 GetOwner）

| 会话 | 账号 | 性质 |
|---|---|---|
| session 1 | `lummy` | 本机控制台（用户本人），`dist\` 与 `dist\stable\` 都从这里跑 |
| session 2 | **A1** | **生产，须用户授权** |
| session 3 | **A4** | **生产，须用户授权** |
| session 4 | **A2** | 测试，可随意启停 |
| — | **A3** | 测试，未登录，可随意启停 |

### 生产操作须用户明确授权（2026-11-26）

挂机版更新、A1 操作、A4 操作分别授权。用户点明本次任务的对象和操作即可执行，范围不清时先确认。更新开发版或笼统要求“更新 dist”仅适用于开发版。

| 对象 | 判据 |
|---|---|
| **A1 / A4 两个账号** | 进程 owner 为 `A1` 或 `A4`（session 2 / 3）。会话内的进程、窗口、桌面、抓屏和输入等操作，限于用户明确授权的账号和操作 |
| `dist\stable\SoulLink-Stable.exe` | 文件名或路径含 `stable` 的产物默认受保护。用户明确要求更新挂机版时，可替换更新所需文件，并按授权范围停启、验证挂机版 |

账号归属按进程 owner 判定，挂机版另按文件名或路径识别。

```powershell
Get-CimInstance Win32_Process -Filter "Name LIKE 'SoulLink%'" | ForEach-Object {
  $o = Invoke-CimMethod -InputObject $_ -MethodName GetOwner
  # $o.User 为 A1 / A4 -> 核对该账号及操作的用户授权
}
```

若读不到 owner，**按禁动处理**（判不清就不动）。

### 可以自由操作（测试环境）

| 对象 | 说明 |
|---|---|
| `dist\SoulLink.exe` | 主程序（测试） |
| `dist\SoulLink.Agent.exe` | 注入代理（测试） |
| `dist\` 下的非 stable 测试产物 | 构建输出、version 文件、`sl-rescue.exe`、`sl-watchdog.exe` 等 |
| `C:\Program Files\SoulLinkDev\SoulLink.Agent.exe` | **服务侧 agent，测试用途，可停、可换、可重铺** |
| `SoulLink.DevBroker` 服务（session 0 的 `SoulLink.exe --broker`） | **可停、可启、可改配置、可重装** |
| session 4 (A2) / A3 | 测试账户，可随意启停 |
| 注册表 | **仅限已授权的 DWMFRAMEINTERVAL**，其他项不动 |

> ⚠️ 纠正一个历史误记：`C:\Program Files\SoulLinkDev\SoulLink.Agent.exe` **不是** A1/A4 专用。
> 它被 A1、A4、A2 三个会话的 agent 进程**共用同一个可执行文件**。
> 所以判据始终是**进程 owner**，不是文件所在目录：
> 停「A2 会话里的 agent」可以；停 A1 或 A4 的 agent 需要用户授权对应账号的停进程操作。
> 替换 Program Files 里的 agent **文件**属于安装/修复动作，是允许的 ——
> 但会 Kill 掉占用它的进程，那一步必须逐个核对 owner，凡被 A1/A4 持有，**先报 Lead，确认已有该账号的用户授权后按精确 PID 操作**。

### 停进程规则（不变，更严）

- 只能用**精确 PID**（`Stop-Process -Id <pid>`）
- 禁止按名字批量杀（`taskkill /IM`）
- 禁止 `Get-Process SoulLink* | Stop-Process` 这种模糊匹配
- **停之前必须先核 owner**；owner 读不出来按禁动处理

---
## 三、用户体验红线

### 绝对不要弹窗

用户已明确投诉过 cmd 窗口反复弹出。所有后台启动必须：

```powershell
# 方式一：wscript + vbs（Run 第二参数 0 = 完全隐藏）
Set ws = CreateObject("Wscript.Shell")
ws.Run "powershell.exe -WindowStyle Hidden -File xxx.ps1", 0, False

# 方式二：Start-Process 隐藏窗口
Start-Process -FilePath "xxx.exe" -WindowStyle Hidden
```

**绝对不要**：
- ❌ `schtasks` 直接跑 `.bat`
- ❌ `cmd /c xxx.bat`
- ❌ 任何会闪黑窗的方式

### 测完立即清理

- 临时计划任务删除
- 临时脚本删除
- 临时测试 agent 停掉
- 临时项目目录删除

---

## 四、代码纪律

### 构建必须通过

```
dotnet build -c Release --nologo
→ 必须 0 error
```

### 不要打红共享构建

- 长时间编辑中的代码，先放 `.cs.txt` 或独立目录
- 绝对不要把编译不过的文件留在主项目里
- 已发生过：SIMD 草稿 236 个错误阻塞整个团队 20 分钟

### 文件归属

- 各成员只改自己被分配的文件
- 跨文件改动必须先 `dsh_team_send` 协调
- 并发改同一文件会互相覆盖

### 不要擅自提交

- 由 Lead 统一 `git commit`
- 成员改完就停，不要 `git add` / `git commit`

---

## 五、发布红线

- ❌ 不发布（`scripts/publish-release.ps1` 只能用户或 Lead 在授权下执行）
- ❌ 不碰 COS
- ❌ 不改 `latest.json`
- ❌ 不改 `SoulLink.csproj` 的 `<Version>`（由 Lead 统一升）

---

## 六、数据真实性红线

- ❌ 禁止编造数据
- ❌ 禁止伪造「已验证」「通过」
- ✅ 查不到就写「未查到」
- ✅ 没测就写「未实测」
- ✅ 失败就如实汇报，不要粉饰

**用户明确要求过：宁可少写，不要编造。**

---

## 附：违规处理

任何违反上述红线的行为：

1. 立即停止
2. 向 Lead 报告
3. 由 Lead 决定是否回滚

**成本红线违规尤其严重**，因为它直接造成用户经济损失。
