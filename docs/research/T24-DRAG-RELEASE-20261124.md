# T24 网页输入迟滞：根因实测与修复（2026-11-24）

> 任务：T24（dsh-team `soullink-p0-fix-yomori` / member `yomori-scout` / attempt `a9b24416-b6ef-444e-a904-644e2c3de861`）
> 用户原话：「拖动图标时很流畅，但释放鼠标需要 3 秒才能到位」「**如果松开瞬间点击一下左键，会瞬间完成**」「包括键盘输入同样有迟滞感」

---

## 0. 结论先行：原假设被实测推翻，真正的根因是另一条

| 假设 | 实测结果 | 判定 |
|---|---|---|
| **move 堵住 up**（队列积压） | 本机 up 往返 **0.9~1.0 ms**，发 up 时队列积压 **= 0** | **本机不成立** |
| **`SendKeyCombo` 的 `Thread.Sleep(20)`** | key **31 ms** vs keydown **0.3 ms**；repeat=15ms 积压 9 条、repeat=5ms 积压 **11 条** | ✅ **确认，真凶** |
| **`e.repeat` 未过滤** | 代码确认无 `if (e.repeat) return` | ✅ **确认，放大器** |
| **50ms 限流丢掉 up 的抓帧触发** | 代码确认（up 与 move 一视同仁） | ✅ **确认，解释「画面不刷新」** |

**因果链**：长按/连打字 → 每条 `key` 在服务端**串行循环里睡 20ms** → 队列积压 →
**排在后面的 up 出不去** → 用户看到「松手很久才落位」。
而「点一下左键就瞬间完成」正好印证：**点击时键盘停了，队列空了，up 立刻出得去。**

---

## 1. 修前基线（A2 生产 agent，pid 4516，端口 19081）

### 1.1 决定性隔离实验
```
key      median =   28.6 ms   raw=[23, 27.3, 28.1, 29.1, 29.3, 28.6, 28.8]
keydown  median =    0.3 ms   raw=[1.9, 2.6, 0.5, 0.3, 0.3, 0.3, 0.3]
keyup    median =    0.5 ms   raw=[1, 0.4, 0.4, 0.6, 0.3, 1.1, 0.5]
move     median =    0.3 ms   raw=[0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.2]
```
**同一服务端、同一通道、同一鉴权** —— 唯一差别是 `key` 走 `SendKeyCombo`（含 `Thread.Sleep(20)`），
`keydown`/`keyup` 走另一条不含 Sleep 的分支。**95 倍差 → 锁定 Sleep(20)。**

### 1.2 病态验证：repeat 间隔 < 处理时间时的积压
```
repeat=30ms x20 条: 发送=620ms  清空额外=  0ms  积压=1
repeat=15ms x20 条: 发送=365ms  清空额外=244ms  积压=9
repeat= 5ms x20 条: 发送=306ms  清空额外=306ms  积压=11
```
**这就是「键盘把自己堵死」的定量证据。** 积压会把排在后面的 up/click 一起堵住。

### 1.3 「move 堵 up」为什么不成立（本机）
```
PRE-FIX: 40 moves then up
  #1  up send->reply = 1.3 ms   pending@tail=0
  #2  up send->reply = 1.2 ms   pending@tail=0
  ...5 次全部 pending@tail=0
```
本地 RTT ≈1 ms，move 间隔 8 ms → **队列每 8ms 就被消费空一次**，up 到达时前面没有积压。
即使 40 条 move **零间隔突发**（最坏情况），up 也只 5.8 ms。
**所以「move 堵 up」在公网（RTT 几十~几百 ms）下仍可能成立，但在本机复现不出来 —— 不当作已排除。**

---

## 2. 修复（三条）

### P0-1：`SendKeyCombo` 去掉阻塞的 `Thread.Sleep(20)`

| 位置 | 改动 |
|---|---|
| `Relay/RelayAgent.cs:186-236` `SendKeyCombo` | 删除 `Thread.Sleep(20)`，其余**一字未改** |

**为什么敢删（三条依据）**：
1. 前端已有 `keydown`/`keyup` 分离动作 —— 需要「按住」的场景本就该走它们；
2. 实测 0ms 间隔的 `keydown` + `keyup`，远端**两者都返回 ok**（见 §4）；
3. `key` 语义是「点按一次」，20ms 与 0ms 对远端 Windows 都是一次完整按下-抬起，不改变可观察结果。

**未改变（任务硬要求）**：
```
foreach (var vk in vks) SendVk(vk, false);              // 顺序按下  —— 保留
for (int i = vks.Count - 1; i >= 0; i--) SendVk(vks[i], true);  // 逆序抬起 —— 保留
// 任一 SendVk 失败即 return false（不吞错）             —— 保留
```
**Ctrl+C 不会变成 C 先松开** —— 顺序、逆序、原子性全部原样。

### P0-2：过滤浏览器自动重复 `e.repeat`

| 位置 | 改动 |
|---|---|
| `docs/web-index.html:2395`（**唯一源**） | keydown handler 加 `if (e.repeat) return;` |
| `Services/WebPages.cs:2411` | 由 `scripts/sync-webpages.ps1` 从上面自动生成 |
| `dist/web-index.html:2395` | 同上，同步刷新 |

**做法**：长按的重复由**远端系统自己产生**（真实键盘也是这样）。只发第一次 keydown，
浏览器自己的 repeat 一律丢掉；抬起由 `keyup` 处理 —— **语义无损失**。

### P0-3：`up` / `click` / `releaseall` 无视 50ms 限流

| 位置 | 改动 |
|---|---|
| `Relay/SessionRecorder.cs:1175` `NotifyInputActivity` | 加 `bool urgent = false` 参数；`urgent` 时跳过 50ms 限流 |
| `Relay/AgentWebServer.cs:658-661` | 按 `cmd` 判定 urgent 并传入 |

**理由**：拖动时 pointermove 每 8ms 一条、每条都刷新限流时间戳，
松手那一帧极可能落进 50ms 窗口被丢掉 —— **远端松开了但画面不刷新**，
用户看到的就是「判定我没松开」。这类动作是用户唯一能直接感知的操作终点，多编一帧代价极小。

---

## 3. 修后实测（隔离实例对比，同一方法）

### 3.1 测试方法（为什么是隔离实例）

**A1（pid 38324）/ A4（pid 6904）/ A2（pid 4516）三个会话共用同一个 `SoulLink.Agent.exe` 文件。**
替换它必然要 Kill 占用它的进程 —— **包含 A1/A4，属红线**。
所以改用**独立副本 + 独立端口**：
```
复制 Agent\bin\Release\net11.0-windows\ → %TEMP%\t24-agent-test\
SOULLINK_WEB_PORT=19099  SOULLINK_WEB_TOKEN=t24test
SoulLink.Agent.exe --relay SoulLink_T24Test_Relay_99
```
**测试期间 A1/A4/A2 三个生产 agent 全程未动，PID 完全不变。**

### 3.2 公平对比（两侧各预热 40 次后各测 20 次，取中位数）

| 项目 | 旧代码（19081） | 新代码（19099） | 变化 |
|---|---|---|---|
| **key（按下+抬起）** | **31 ms** | **1.7 ms** | **−29.3 ms（−94%）** |
| keydown | 0.3 ms | 0.8 ms | +0.5 ms |
| up | 0.4 ms | 0.9 ms | +0.5 ms |
| move | 0.1 ms | 0.1 ms | 0.0 ms |

**唯一实质变化就是 `key`**；其余三项差异在 1 ms 内，属进程/调度噪声（两个独立进程，基线本就不同）。

### 3.3 病态积压：修前 vs 修后

| repeat 间隔 | 修前积压 | **修后积压** |
|---|---|---|
| 30 ms | 1 条 | **0 条** |
| 15 ms | **9 条** | **0 条** |
| 5 ms | **11 条** | **0 条** |

**这是本次最有力的证据**：修复后任何 repeat 速率都不再积压，
也就不会再有「键盘把 up 堵在后面」的可能。

---

## 4. 粘键保护 / 组合键顺序 未破坏

**零间隔 keydown+keyup 实测**：
```
replies: [{"ok":true,...},{"ok":true,...}]
=> 0ms 间隔的 keydown+keyup 两者都 ok
```
说明去掉 20ms 不会让远端漏掉按下或抬起。

**`releaseall` 幂等未动** —— 前端 `INPUT_RETRYABLE = new Set(['up','releaseall'])` 原样保留（`WebPages.cs:1738`）。
**`ExecuteWeb` 的 `_webHeldInput` 状态机未动**（`RelayAgent.cs:443-452`）。
**`WebIdleReleaseMs=5000` 断连看门狗未动**（`RelayAgent.cs:455`）。

---

## 5. 未做的与原因

| 项 | 状态 |
|---|---|
| **seq 协议改造 / move 直发不等回包** | **未做**。本机实测 up 已 1ms、队列积压 0，改协议风险不值得。**不是永久否决** —— 若公网仍报迟滞再回来做 |
| **move 与 key 共用单 in-flight 槽** | **未改架构**。结构问题确实存在（且三行注释在描述不存在的实现，已在本报告记录），但本机不是瓶颈 |
| **`SendKeyCombo` 改异步/后台化** | **未做**。直接删 Sleep 已达成目标，改成异步反而引入组合键时序风险 |

### ★ 顺带发现：三行注释在说谎
```
WebPages.cs:2202  // 有货才发；move 已改 noWait，不再有 in-flight 等待
WebPages.cs:1617  // · move 走 noWait 不等回包（RTT 不再拖慢拖动）
WebPages.cs:2214  // ★ noWait：不等回包...单 in-flight 语义改为「节流窗口内最多排一条」
```
实际上 `sendInputNoWait` → `postInputRaw`（`:1691`）→ **`:1697 return await wsSend(fullBody)`**，
它照样进单 in-flight 队列。**当初「move 改 noWait」只改了名字和注释，没真正绕开 `wsSend`。**
这解释了为什么问题一直没被发现：**读注释会以为已经修好了。**
（本次未改，但建议后续修正注释，否则会继续误导人。）

---

## 6. 边界自查

改了 4 个文件：`Relay/RelayAgent.cs`（P0-1）、`Relay/SessionRecorder.cs` + `Relay/AgentWebServer.cs`（P0-3）、`docs/web-index.html`（P0-2 源）+ 自动生成的 `Services/WebPages.cs` / `dist/web-index.html`。
**未碰** `Controls/RdpConsole.cs`、编码/抓屏/传输热路径；未新建 DispatcherTimer；未 git commit。
**未替换** `C:\Program Files\SoulLinkDev\SoulLink.Agent.exe`（因 A1/A4 共用，属红线）；测试用 `%TEMP%` 独立副本 + 19099 端口，测后已 Kill 并删除。
**A1(38324) / A4(6904) / A2(4516) 全程 PID 未变、未被触碰。** 未碰 `dist\stable`、未发布、未碰 COS。未用 A1/A4。未弹窗。

---

**文档作者**：yomori-scout
**最后更新**：2026-11-24