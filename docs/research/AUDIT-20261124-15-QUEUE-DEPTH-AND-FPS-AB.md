# 审计 15 · viewer 队列深度 2→1 A/B：省多少 ms + 静止/动态出帧率对照

> 日期：2026-11-24
> 作者：rdp-session-analyst（dsh-team team_mwu35l8pae8oj5 / member m_mwu3790mafbish / task task_mwu74r73l2jzos）
> 性质：只读审计 + 机制推导。未改任何产品代码/环境变量/配置/版本号；未重启任何 agent；未碰 dist\stable\SoulLink-Stable.exe；未用 A1/A4。
> 证据分级：【代码事实】= 源码 file:line；【已有实测】= 本仓库前序实测；【本次日志实测】= 读 live agent 日志；【推断】= 机制推导；【未取到】= 明确标注原因。

---

## 0. 一句话结论

**2→1 实测省多少：快读端 ≈ 0ms，慢读端 ≈ 1 个帧间隔（16.7~33ms）。不建议改默认值为 1。** 队列深度只在「读者比帧慢、队列顶满」时才绑定：当前 live agent 稳态 `queueAge=0`（快读端队列从不积压），2→1 收益为零；只有拥塞场景才有收益，而深度 1 会让「丢旧保新」退化成单帧槽、丢帧率翻倍、关键帧保护退化。真正的 4→2 那一刀（慢读端 82→41ms，省 ~40ms）已在 `E2E-LATENCY-BASELINE §6` 做完；2→1 是边际收益递减 + 风险上升的过度优化。

---

## 1. 队列深度生效路径（代码事实）

| 事实 | 位置 | 含义 |
|---|---|---|
| 默认深度 2 | `Relay/SessionRecorder.cs:97` `private const int ViewerQueueDepth = 2` | 编译期默认 2 |
| 环境变量覆盖 | `SessionRecorder.cs:100-104` `ReadQueueDepth()` 读 `SOULLINK_VIEWER_QUEUE`，clamp 1~8 | 覆盖值 |
| **静态只读一次** | `SessionRecorder.cs:98` `private static readonly int ViewerQueueDepthValue = ReadQueueDepth()` | ★ 进程启动时读一次，**改环境变量必须重启 agent 才能生效**（不能热切） |
| 丢旧保新 | `SessionRecorder.cs:1038` `while (viewer.Pending.Count >= ViewerQueueDepthValue && !IsKeyFrame(队头)) Dequeue()` | 队列满且队头非 IDR → 丢最旧帧 |
| 关键帧保护 | `SessionRecorder.cs:1038` `!IsKeyFrame(...)` 条件 | 队头是 IDR 时不丢（宁可超深不花屏） |
| queueAge 语义 | `SessionRecorder.cs:1221` `ageMs = DateTime.Now - at`（出队帧龄） | 服务端队列等待的直接测量 |
| queueAgeMax | `SessionRecorder.cs:1227-1228` 全局最大值 | 历史最坏队列等待 |

**关键机制**：服务端队列最坏等待 ≈ 深度 × 帧间隔（仅当读者消费慢于帧到达时顶满）。这是「2→1 省多少」的完整答案：
- 快读端（读者消费快，队列不顶满）：**深度上限不生效，省 0ms**；
- 慢读端（读者消费慢，队列顶满）：深度 N 的最坏等待 ≈ N × 帧间隔，2→1 省 **1 × 帧间隔**。

---

## 2. 2→1 量化结论（机制推导 + 已有实测）

| 场景 | 深度 2 最坏等待 | 深度 1 最坏等待 | 2→1 节省 | 丢帧风险 | 证据等级 |
|---|---:|---:|---:|---:|---|
| 快读端（正常局域网 viewer） | ~0（队列不积压） | ~0 | **≈ 0ms** | 不变（都不丢） | 本次日志实测 queueAge=0 |
| 慢读端 @50fps（帧间隔 20ms） | 40ms | 20ms | **≈ 20ms** | 翻倍 | 机制推断（对齐 E2E §6 4→2） |
| 慢读端 @30fps（帧间隔 33ms） | 66ms | 33ms | **≈ 33ms** | 翻倍 | 机制推断 |
| 慢读端 @60fps（帧间隔 16.7ms） | 33ms | 16.7ms | **≈ 16.7ms** | 翻倍 | 机制推断 |

**已有 4→2 实测锚点**（`E2E-LATENCY-BASELINE-20261124.md` §6，唯一有慢读端的权威数据）：
- 慢读端（30ms/帧）+ 动态：深度 4 → queueAgeMax 82ms；深度 2 → ≈41ms（= 2×帧间隔），**省 ~40ms**；
- 快读端（10ms 轮询）+ 动态：深度 2 → queueAge 11~18ms，队列不顶满。

**2→1 是同一机制的下一档**：41ms → 20ms（@50fps），省 ~20ms，但代价是「丢旧保新」从「留 2 帧」退化成「留 1 帧」，慢读 + 动态下丢帧率翻倍；关键帧保护退化（IDR 大帧在队里未发完时，后续 delta 全部被丢）。

---

## 3. 静止 vs 动态出帧率对照（本次 live 日志实测）

日志：`C:\Users\A2\AppData\Local\Temp\soullink-relay-27656.log`（live agent PID 27656，session4，viewers=1~2）

| 段 | 区间 | total 增量 | 耗时 | 出帧率 | captureMs | staticSkipped | 判读 |
|---|---|---|---:|---:|---:|---:|---|
| **动态**（用户测延迟时段） | 12:20:27.870 → 12:20:35.525 | 25140→25590 = 450 | 7.655s | **58.8 fps** | 4~21ms（多数 4~12） | 2522（不变） | 桌面持续变化，DDA 立即返回，节拍器 60fps 上限附近 |
| **静止**（无操作） | 12:31:08.449 → 12:31:21.227 | 25650→25680 = 30 | 12.778s | **2.35 fps** | **100ms（阻塞超时）** | 2650→2746（增长） | 变化驱动合成：桌面几乎不产新帧，2fps 保活节拍（500ms） |
| 静止（更长样本） | 12:30:52.963 → 12:31:08.449 | 25620→25650 = 30 | 15.486s | **1.94 fps** | 100ms | 2534→2650 | 同上 |

**对照结论**：
- 动态：出帧率 ≈ 60fps（节拍器上限），captureMs 低（DDA 立即返回）；
- 静止：出帧率 ≈ 2fps（保活节拍 `KeepaliveIntervalMs=500`，`SessionRecorder.cs:89`），captureMs=100（`AcquireNextFrame(100)` 阻塞超时，`DxgiDdaCapture.cs:53,265`），staticSkipped 持续增长。
- 这说明**队列深度 A/B 的「慢读端」前提是「动态桌面」**——静止桌面帧供给只有 2fps（帧间隔 500ms），任何读者的消费都远快于供给，队列永远不顶满，深度差异测不出来。

---

## 4. 是否建议改默认值

**不建议把默认 2 改成 1。** 理由：
1. **稳态收益为 0**：当前 live agent 实测 `queueAge=0`、`queueDepthMax=2`（未顶满），说明正常局域网 viewer 队列从不积压，深度上限根本不生效（本次日志实测）；
2. **拥塞收益有限**：慢读端 2→1 只省 1 个帧间隔（16.7~33ms），而 4→2 已砍掉大头（40ms）；
3. **丢帧风险翻倍**：深度 1 = 单帧槽，「丢旧保新」退化为「有 1 帧在队就丢新来的」，慢读 + 动态下丢帧率显著上升（`SessionRecorder.cs:1038` 的 Dequeue 会更频繁触发）；
4. **关键帧保护退化**：IDR 大帧（8Mbps 下 33KB+）在队里未发完时，后续 delta 全部被丢（深度 1 无第二缓冲位），画面跳帧/重同步风险上升（`SessionRecorder.cs:1038` 的 `!IsKeyFrame` 保护在深度 1 下形同虚设）；
5. **正确解法不在队列深度**：慢读端的根因是「读者（浏览器解码/渲染）比服务端出帧慢」，应修读者而非把队列压到 1。

**结论**：保持默认 2。若个别慢客户端场景确需更低延迟，用 `SOULLINK_VIEWER_QUEUE=1` 环境变量按会话定向覆盖即可（`SessionRecorder.cs:102-103`），不改全局默认。

---

## 5. 未取到项 + 原因（诚实标注）

| 验收项 | 状态 | 原因 |
|---|---|---|
| 深度 2 vs 1 各 ≥10 张截图取中位数 | **未取到** | 截图差分是「浏览器画面 vs RDP 画面同框」动作，需用户在 A2 的 RDP 客户端里同时截两画面，本子会话无法独立完成；且改队列深度必须**重启 live agent（PID 27656）**（`SessionRecorder.cs:98` 静态只读一次），会断掉当前 2 个正在连的 viewer（127.0.0.1→19081，用户在测延迟） |
| 慢读端队列顶满的 2→1 实测 | **未取到** | 制造慢读端需「60fps 动态桌面源 + 故意慢读探针」，前序工具（WPF 动画/慢读探针）已按红线清理；且当前桌面静止（出帧 2fps），队列永不顶满，测不出深度差异 |

**替代证据已给**：机制推导（§2）+ 已有 4→2 慢读端实测（E2E §6）+ 静止/动态 live 日志实测（§3）已足以回答「2→1 省多少 + 是否改默认」。

---

## 6. 可复现 A/B 方案（供后续/用户配合时执行）

**前提**：用户在 A2 RDP 客户端里同框截图（毫秒时钟已在 A2 桌面：`C:\Users\A2\Desktop\毫秒时钟-测延迟.exe`）。

**服务端侧（本会话可做的部分）**：
1. 用独立 agent（不动 live 27656）：schtasks 起带 `SOULLINK_VIEWER_QUEUE=2` 的 agent（端口 19099，SOULLINK_STREAM_FPS=60，token）；
2. 起 60fps 动态桌面源（WPF 48 矩形动画，MULTI-VIEWER 同款）；
3. 起慢读端探针（每帧 sleep 30ms），记录服务端 `queueAge/queueDepthMax/queueDrops`；
4. 停 agent，改 `SOULLINK_VIEWER_QUEUE=1` 重启，重复；
5. 对比两组 queueAge 中位数。

**截图差分侧（需用户）**：毫秒时钟同框，浏览器画面 vs RDP 画面各截 ≥10 张，取中位数，A 组（深度 2）与 B 组（深度 1）差分相减 = 2→1 的端到端收益。

---

## 7. 现场恢复确认

- live agent PID 27656 **未重启、未动**（cmdline 无 SOULLINK_VIEWER_QUEUE，走代码默认 2）；
- 未改任何环境变量/注册表/配置/版本号；
- 未新建/删除任何计划任务（前序残留的 SoulLink-DupFrame-Agent / SoulLink-DupFrame-Animation 是历史遗留，非本次创建，未触碰）；
- 未写入任何临时文件（本报告为唯一写入）；
- 无残留进程。

---

## 8. 证据索引

- 队列深度：`Relay/SessionRecorder.cs:97-104`（默认2/环境变量/静态只读一次）、`:1038`（丢旧保新+关键帧保护）、`:1221-1228`（queueAge 语义）。
- 保活节拍：`SessionRecorder.cs:89`（KeepaliveIntervalMs=500）；抓屏超时：`DxgiDdaCapture.cs:53,265`（AcquireNextFrame(100)）。
- 已有 4→2 实测：`E2E-LATENCY-BASELINE-20261124.md` §6（慢读端 82→41ms，快读端 11~18ms）。
- 本次 live 日志：`C:\Users\A2\AppData\Local\Temp\soullink-relay-27656.log`（12:20 动态 58.8fps / 12:31 静止 2fps）。