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

### 绝对不可触碰

| 对象 | 说明 |
|---|---|
| `dist\stable\SoulLink-Stable.exe` | **挂机测试版（PID 13392）**，运行 A1/A4 挂机业务 |
| **A1 账号**（session 2） | 挂机测试账号，只读抓屏用 |
| **A4 账号**（session 3） | 挂机测试账号，只读抓屏用 |

### 可以操作（测试环境）

| 对象 | 说明 |
|---|---|
| `dist\SoulLink.exe` | 主程序（测试用，跑 A2/A3） |
| `SoulLink.Agent.exe` | 测试 agent |
| session 4 (A2) | 测试会话，可随意重启 |
| 注册表 | **仅限已授权的 DWMFRAMEINTERVAL**，其他项不动 |

### 停进程规则

- 只能用**精确 PID**（`Stop-Process -Id <pid>`）
- 禁止按名字批量杀（`taskkill /IM`）
- 禁止 `Get-Process SoulLink* | Stop-Process` 这种模糊匹配

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
