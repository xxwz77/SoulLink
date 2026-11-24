# 端到端延迟基线复测（2026-11-24）

> 背景：最初基线 444ms。经 P0-A~P0-H 系列修复（探测锁死解除、静止保活、30fps 节拍、
> PS 着色器零拷贝、GPU 智能路由）后，本报告给出当前端到端延迟的实测拆解与对比。
> 测试环境：session4/A2（RDP 登录），1920x1080，SOULLINK_STREAM_FPS=60，
> agent 端口 19099，NVENC-SDK 8000kbps，HEAD=0854e5a。
>
> ★ 关键拓扑事实：session4 的 RDP 桌面合成在 **Intel UHD** 上（NVIDIA 无显示输出），
> 路由日志：`抓屏源=Intel(0x8086); 编码器=NVENC-SDK; 同GPU=false; 零拷贝=禁用`——
> 跨 GPU 共享是物理限制（Sunshine 同构），该场景走 CPU 转换路径是**正确降级而非缺陷**。

## 1. 三组 15s 实测（服务端管线指标，来自 agent relay 日志每 30 帧行）

| 场景 | 实际 fps | captureMs | convertMs | encodeMs | staticSkipped/keepalive | 15s 流量 |
|---|---|---|---|---|---|---|
| A 静止桌面 | 2.0（保活节拍） | ~100（阻塞等帧语义） | 7 | 8 | 静止跳过增长，keepalive 2fps 持续 | 80 KB |
| B 鼠标移动（/api/input 真实输入链路） | **43.1** | 均值 8.4（4~19） | **7.8** | **4.6** | 不再误判静止（0 增长） | 741 KB |
| C 全屏动画（FullscreenAnimation 工具） | **46.9** | 均值 6.7（4~14） | **8.0** | **4.7** | 0 增长 | 9.96 MB |

说明：
- fps 上限 = RDP 会话 DWM 合成率（约 47~50fps，DWMFRAMEINTERVAL=15 已生效），
  **不是管线瓶颈**：单帧工作 6.7+8.0+4.7 ≈ 19.4ms → 理论 ~51fps，与实测 47 吻合。
- 静止时 captureMs≈100 是 DDA 阻塞等帧超时（P0-E 设计语义，非工作耗时）；
  动态拍 captureMs 4~19 已含等帧分量，纯 GPU 拷贝/读回成本 <3ms。
- 断流 0 次（P0-D 保活生效）；staticSkipped 在动态场景不再增长（P0-A 修复生效）。

## 2. 端到端延迟拆解（鼠标移动场景 = 用户主诉「操作跟手」路径）

| 段 | 数值 | 依据 |
|---|---|---|
| 网页输入 → agent 注入 | ~15 ms | AUDIT-20261124-04 实测（HTTP + 注入） |
| 抓屏（DDA 阻塞等帧，动态拍） | 4~19 ms（均值 8.4） | 本测 captureMs |
| BGRA→NV12（CPU，跨 GPU 无零拷贝） | 7.8 ms | 本测 convertMs |
| H.264 编码（NVENC-SDK） | 4.6 ms | 本测 encodeMs |
| 服务端 viewer 队列（≤4 上限，实际 1~2 帧） | ~23~46 ms | 帧间隔 23ms@43fps × 队列深度 |
| 网络传输（局域网/本机回环） | 0.8~5 ms | AUDIT-20261124-00 实测 0.8ms |
| 浏览器解码队列+解码+合成上屏 | 20~40 ms | SKIP_QUEUE=2 + 硬解 2~5ms + 1 vsync 16.7ms |

**端到端延迟 ≈ 85~130 ms（典型 ~90ms，上界 ~140ms）**，对比基线 444ms：
**降幅 3~5 倍，达成「100ms 量级」目标。**

## 3. 与历史基线对比

| 版本 | 端到端 | 服务端单帧 | 主要瓶颈 |
|---|---|---|---|
| 最初基线 | 444 ms | captureMs 42 | 探测永久锁死 BitBlt + 16ms 短轮询 + 零拷贝噬帧 + 静止误判 + 25s 断流 |
| 本轮（P0-A~H 后） | **85~130 ms** | capture 4~19 + convert 7.8 + encode 4.6 | 跨 GPU 只能 CPU 转换（物理限制） |

## 4. 剩余瓶颈与下一步

1. **convertMs 7.8ms**：session4 跨 GPU（Intel 抓屏 + NVENC）禁用零拷贝是物理约束；
   同 GPU 机器上零拷贝已实测（session1：zeroMs 4~7ms、convMs<1ms、60fps 稳定），
   E2E 可进一步压到 ~60~80ms。备选：SIMD 优化 BgraToNv12（4-6→1-2ms，零风险）。
2. **RDP 合成率 47fps**：60fps 配置下被 DWM 合成封顶（KB2885213 的 30fps 默认已由
   DWMFRAMEINTERVAL=15 提到 60 上限），服务端管线不是瓶颈。
3. **浏览器段 20~40ms**：SKIP_QUEUE=2 已激进；进一步需要 rVFC/时间戳埋点实测
   （AUDIT-20261124-05 的 P2 方案），不在本轮范围。

## 5. 验证方法（可复现）

1. 隐藏启动测试 agent 于 session4（InteractiveToken 计划任务 + wscript 包装，防弹窗）：
   `start-agent.bat`（端口 19099 + token + fps=60）。
2. 三组场景：静止=仅连 `/stream?token=testtoken123`；鼠标=`/api/input` POST move；
   动画=`schtasks /Run /TN SoulLink-DDA-Animation-Research`。
3. 指标：grep agent relay 日志 `共享管线|零拷贝管线` 行，窗口内 total 增量÷秒数=fps，
   captureMs/convertMs/encodeMs 取均值。
4. 测完清理：删除计划任务、vbs、测试 agent（精确 PID）。

## 6. 补充：观众队列深度优化实测（2026-11-24 晚，队列 A/B）

viewer.Pending 队列深度 4 → 2（可配置 SOULLINK_VIEWER_QUEUE，默认 2，关键帧头保护保留）。
新增可观测指标：`queueDrops`（丢旧保新累计）、`queueDepthMax`（入队峰值深度）、
`queueAge/queueAgeMax`（出队帧龄 = 服务端队列等待的直接测量）。

| 场景 | 队列深度 | queueDepthMax | queueAgeMax | 丢帧 |
|---|---|---|---|---|
| 慢读端（30ms/帧）+ 桌面动态 | 4（基线） | **4（顶满上限）** | **82 ms** | 537 |
| 慢读端（30ms/帧）+ 桌面动态 | 2（优化） | ≤2 | ≈41 ms（=2×帧间隔，机制上界） | 少量 |
| 快读端（10ms 轮询）+ 动态 | 2 | 1~2 | 11~18 ms | 0 |

结论：
1. **队列上限只在「读者比帧到达慢」时绑定**——快读端稳态队列年龄仅 5~10ms（此前文档
   §2 里 23~46ms 是保守上界估计，实测修正为 5~10ms 典型 / 82ms 拥塞最坏）。
2. 拥塞场景下 cap 4→2 把服务端队列最坏等待从 82ms 减半到 ~41ms（**节省 ~40ms**）；
   稳态节省接近 0（队列本就不积压）。
3. 关键帧头永不被丢（宁超深不花屏），丢帧计数证明丢的只有非关键旧帧；无撕裂/花屏报告。
4. 剩余服务端可控延迟：WriteStreamAsync 的 10ms 轮询（AgentWebServer.cs，非抓屏文件）。
