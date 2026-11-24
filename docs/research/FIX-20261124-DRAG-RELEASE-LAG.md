# 修复：拖动松手落位错位 + shot 阻塞注入通道

> 日期：2026-11-24　作者：input-chain-analyst（dsh-team `team_mwu35l8pae8oj5` / task `task_mwu970c2nbgu7n`）
> 诊断依据：AUDIT-20261124-17-DRAG-RELEASE-LAG.md；版本 `3.3.38`（SoulLink.csproj，唯一版本来源）。
> 构建验证：`dotnet build -c Release` **0 error**（4 个 warning 全部是 broker-setup 的 NU1510 既有包警告，与本次改动无关）；构建后 bin/ obj/ 已清理（工作区整洁规则）。

## 1. 改动清单（file:line 为改后位置）

### 缺陷 1：up/down 不定位 → 松手落旧位

| 文件 | 位置 | 改动 |
|---|---|---|
| Relay/AgentWebServer.cs | :639-642（`ExecuteInput` 的 `Move` 判定） | `(cmd == "click" && hasX) ? 1 : 0` → `((cmd == "click" || cmd == "down" || cmd == "up") && hasX) ? 1 : 0` |
| Relay/RelayAgent.cs | :868-881（`down`/`up` 分支） | `req.Move != 0` 时先 `SetCursorPos(req.X, req.Y)` 再 `MouseButton(...)` |

效果：网页 `pointerup` 携带的最终坐标（WebPages.cs:2347）不再被丢弃，up 与最后一次节流 move 之间的位置差由宿主端定位兜底。语义兼容：不带坐标（x=-1）的 up 行为不变。

### 缺陷 2：shot 阻塞 ServePipe（第一步）

| 文件 | 位置 | 改动 |
|---|---|---|
| Relay/RelayAgent.cs | `case "shot"`（Execute 内） | `CaptureOnce`（JPEG 抓屏+落盘，100ms~秒级）改为 `Task.Run` 后台执行 + `_shotGate` 串行锁；ServePipe 立即返回 `Ok=true, Message="已受理（后台保存中）"` |
| Relay/RelayAgent.cs | `Fail` 方法旁 | 新增 `private static readonly object _shotGate`（防并发写同一时间戳目录） |

语义影响：调用方 FlowRunner（Services/FlowRunner.cs:565-566）只判断 `Ok`/`Message`，兼容；真实成败记入 agent 日志 `[shot] 后台截图 完成/失败`。

## 2. 双通道为什么没做（本期未做 + 后续方案）

**理由**：`RelayClient` 的管道协议是**严格单写者 request-response**（write→readLine 一问一答，RelayClient.cs:531-580；agent 端 RelayAgent.cs:748-751 依赖该顺序回写）。拆两条 Channel 后有两个消费者抢同一个 `_writer`，必须引入跨通道写序（先排空输入再写控制），否则回复错位污染。这是一次协议级改动，需要设计评审 + 全链路回归，不适合夹在本修复里。且本次两个缺陷修完后，up 的最坏等待从 0.1~4s 收敛到 1 RTT（45~64ms 口径），体感问题先行解决。

**后续方案（供排期）**：
1. `SendAsync` 加 priority 参数，输入命令（move/down/up/click/wheel）与状态命令（recordstatus/shot/save）分两条 Channel、两条消费者；写侧加锁序轮询（输入优先）。
2. 或者更小步：把 `save`（60s 超时）也后台化（同 shot 模式），并给 `recordstatus` 降频/缓存。
3. 长期：agent 端 ServePipe 改异步读（读与执行解耦），彻底消除队头阻塞。

## 3. 待用户验证步骤

1. 发布 3.3.38 并连接一个测试会话（**不要用 A1/A4**）。
2. **网页拖动**：浏览器打开控制页，按住左键拖动一个桌面图标到新位置松手——图标应落在**松手时指针所在位置**（修复前会落在最后一次节流 move 的旧位置，视觉上"迟一步/掉错格"）。
3. **桌面控制台拖动**：捕获画面后拖动图标，同样验证落点。
4. **截图并发**：流程编排里连续触发两次截图动作，同时快速拖动图标——拖动不应出现明显卡顿（修复前会被截图卡 100ms~秒级）；日志出现 `[shot] 后台截图 完成：...`。
5. 回归确认：右键转视角（down 不带坐标）行为不变；单击（click）落点不变。

## 4. 边界自查

- 仅改 3 个文件：Relay/AgentWebServer.cs、Relay/RelayAgent.cs、SoulLink.csproj（版本号）。
- 未执行 git commit/push/checkout/reset；未发布、未碰 COS/latest.json；未碰 dist\stable\SoulLink-Stable.exe；未使用 A1/A4。
- 构建产生的 bin/ obj/ 已全部清理（工作区 0 bin/obj）。
- 新增文档：本文档 + docs/research/README.md 登记行。
