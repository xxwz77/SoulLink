# 延迟账目核对：233ms 差分里的"账外缺口"在哪里

> 日期：2026-11-24　作者：input-chain-analyst（dsh-team `team_mwu35l8pae8oj5` / task `task_mwu3bodobc4lyt`）
> 只读审计，未改任何产品代码。证据标记：【代码事实】【已有实测】【实测-本次】(读 A2 会话 %TEMP%\soullink-relay-27656.log)【推断】【待实测】。

## 0. 结论（200 字内）

| 归属项 | 估算 ms | 等级 |
|---|---|---|
| 服务端每帧记账（capture 17 + convert 2 + encode 4 ≈ 23ms） | ~23 | 【实测-本次】 |
| 抓屏节拍税（变化→被采到，半个出帧周期，实测周期 26.5ms） | ~13 | 【推断】(计算见 §2) |
| 静止→活动切换的 DDA 100ms 阻塞尾（captureMs p95=100） | 0~100（间歇性） | 【代码事实+实测-本次】 |
| 服务端 viewer 队列 | ~0（实测 queueAge=0，max 18） | 【实测-本次】 |
| IDR/GOP 拖影 | 稳态 0（无限 GOP 只影响重同步后短暂期） | 【代码事实】 |
| 浏览器 fetch 攒块 + 解码输出队列 + vsync 上屏 | 待实测（代码无人为延迟，量级 0~40） | 【代码事实】（无 pacing）+【待实测】 |
| **账外缺口（~140ms）** | **代码里找不到对应项 → 主要嫌疑是测量法误差 + Chrome 渲染管线，见 §6** | 【推断】 |

最大可控项（现有证据内）：**DDA AcquireTimeoutMs=100 的间歇阻塞**（DxgiDdaCapture.cs:53,265）与**抓屏节拍**。**没有任何代码级平滑/jitter buffer/人为延迟**（§5 已逐条排查）。~140ms 缺口最可能落在测量法与浏览器端，均标注了 5 分钟验证实验。

## 1. 先澄清测量口径（Lead 更正后）

233ms = SoulLink(浏览器) 端到端 − RDP 端到端（同一桌面、同一截图多次取平均）。
⇒ SoulLink 绝对端到端 = 233ms + RDP 自身延迟（>233ms，不是 <233ms）。
⇒ **输入/鼠标链路被排除**：秒表法是纯视频差分，不含任何鼠标事件。旧任务（输入固定税）按用户澄清作废，5 行总结：桌面点击 2×RTT+12ms（SessionInput.cs:53-65）、WH_MOUSE_LL 每事件 Parse（RdpConsole.cs:1571,1750）、单 FIFO 队头阻塞（RelayClient.cs:531-580）等输入链固定税**真实存在但与本 233ms 无关**——秒表画面里没有鼠标事件，它们影响的是操作手感不是画面跟随，不在本账目内。

## 2. 抓屏节拍税：计算依据

- 【代码事实】worker 按 fps 节拍抓屏：`WaitUntilNextSlot` + `AdvanceSlot` 累积式节拍器（SessionRecorder.cs:759,1173-1198）；每拍调 `CaptureBgraWithProbe`（:897）→ DDA `AcquireNextFrame(100)`（DxgiDdaCapture.cs:53,265）。
- 【代码事实】DDA 只在桌面变化时出新帧；变化帧落在两次"到拍抓屏"之间时，**平均要等半个节拍周期才被采到**。
- 【实测-本次】最新 A2 日志（soullink-relay-27656.log，每 30 帧打一行）：30 帧间隔中位数 796ms → **实测每帧周期 26.5ms ≈ 37.7fps**（编码器配置 fps=60，SessionRecorder.cs:120 默认 30，WebUI 可推 60——本机跑在两者之间说明部分拍空转）。
- ⇒ 节拍税 = 半个周期 ≈ **13ms**【推断】。若 streamFps=30 则 ≈16.7~33ms。
- 附【代码事实】：`AcquireNextFrame(100)` 在静止桌面会让 worker 阻塞至多 100ms 等变化帧（日志 captureMs p95=100、max=100 多次出现）。这既有利（变化帧被同一调用逮住，实际降低节拍税）也有害（静止→活动的**第一帧**最多吃满 100ms 阻塞尾）。

## 3. 服务端队列与推流

- 【代码事实】每 viewer Pending 队列深度 = `ViewerQueueDepthValue` 默认 **2**（SessionRecorder.cs:97-103），丢旧保新、IDR 绝不丢（:1038）。
- 【实测-本次】queueDepthMax=1、queueAge=0（max 18ms 一次性）→ **队列积压税 ≈ 0**。
- 【代码事实】推流已改事件驱动 `WaitForFrame` 内核信号（SessionRecorder.cs:1240-1245；AgentWebServer.cs:500-503），旧 Task.Delay(10) 轮询已删。
- 【代码事实】每帧写完即 `FlushAsync`（AgentWebServer.cs:481-486），无 200ms 批量推流（web-index.html:1397 注释里的旧根因已修）。

## 4. IDR / GOP

- 【代码事实】NVENC 无限 GOP + 按需 IDR（NvencSdkEncoder.cs:82,448,561）、frameIntervalP=1 无 B 帧、zeroReorderDelay（:562,566）、ULTRA_LOW_LATENCY（:553,585）。
- 【代码事实】新观众从 Ring 内最近 IDR 起播 + `_forceKeyFrame=true`（SessionRecorder.cs:1109-1116）；前端解码错误先按需 IDR 恢复（web-index.html:1285-1308），服务端限流 300ms（AgentWebServer.cs:73）。
- ⇒ 稳态延迟贡献 ≈ 0；只有切换观众/解码错误后等 IDR 的**过渡期**（数百 ms 级黑屏/旧帧）会被用户并进"延迟"印象【推断】。

## 5. 平滑/补帧/jitter buffer 排查（逐条）

- 全仓 grep pacing/smooth/jitter/targetLatency/delay：视频链路**零命中**。服务端唯二 Sleep/Delay：首帧等待 Task.Delay(20)（AgentWebServer.cs:461，仅起播）与录像保存 Sleep（SessionRecorder.cs:1275，非实时路径）。
- 前端 `output` 回调**直接画**（web-index.html:1311-1330：WebGL2 texImage2D 或 2D drawImage），**不用 requestAnimationFrame**（全文件 0 命中）→ 无人为 vsync 排队，但也意味着**上屏时机完全交给 Chrome 合成器**。
- 跳帧逻辑是丢帧不是延迟（SKIP_QUEUE 丢旧 AU，web-index.html:1423-1438）。
- AU 攒批：framed 模式帧边界即 AU 边界、立即 flush（:1523）→ 无凑批等待。
- 【代码事实】canvas 有 `desynchronized:true`（:1146）——已申请低延迟呈现，但 Chrome 是否兑现取决于窗口状态【待实测】。

## 6. 账外 ~140ms 的候选归属（按嫌疑排序）

1. **Chrome 渲染管线 + 窗口可见性节流**【推断，最大嫌疑】：视频段 fetch 流在 Chrome 内部有网络栈/解码器输入缓冲；若浏览器窗口非前台/被遮挡，合成器降帧会放大延迟。代码已排除人为延迟，所以剩下的只能在这层。→ **5 分钟验证实验（见 §7）**。
2. **测量法误差**【推断】：秒表法粒度受"截图时机 + 秒表帧刷新"限制；秒表自身在 RDP 内渲染，读数误差 ±1~2 帧（30fps 下 ±33~66ms）。两窗口若不在同一张截图同拍，差分被污染。
3. **DDA 100ms 阻塞尾**（间歇性，静止→活动首帧）【代码事实+实测-本次】。
4. RDP 自身延迟已按定义加回，不算缺口。

## 7. 两个 5 分钟判别实验

**实验 A（窗口可见性 → 实测帧率）**：浏览器开 DevTools Console，运行
`setInterval(()=>{ console.log(performance.now(), document.visibilityState, document.hasFocus()); },1000)`
同时让远端桌面放一个毫秒计数器；先前台、再被遮挡各录 30s。判据：若遮挡时 visibilityState='hidden'/rAF 停摆/画面明显更滞后 → Chrome 节流坐实，测量时必须保持浏览器前台。
**实验 B（拆服务端 vs 浏览器）**：远端桌面放系统毫秒时钟，手机拍一段 60fps 慢动作视频（同时拍到浏览器画面和 RDP 画面 + 秒表），逐帧数"时钟数值差"。_browser−RDP_ 的逐帧差分稳定性能直接区分"恒定偏移（链路延迟）"vs"抖动（队列/节流）"。恒定 233ms 且不抖 → 链路里有固定账外项；抖动大 → 节流/积压。

## 8. 证据索引

- 服务端账面与节拍：SessionRecorder.cs:97-103（队列深 2）、:759/1173-1198（节拍器）、:947-955（静止跳过+保活 500ms）、:1038-1045（入队）、:1210-1231（出队帧龄）、:1240-1245（事件驱动等帧）。
- DDA：DxgiDdaCapture.cs:53（AcquireTimeoutMs=100）、:265、:285（FrameSequence）、:202-207（几何）。
- NVENC：NvencSdkEncoder.cs:448/561（无限 GOP）、:562/566（无 B 帧/零重排）。
- 推流：AgentWebServer.cs:437-510（WriteStreamAsync 全路径）、:481-486（每帧 Flush）。
- 前端：docs/web-index.html:1311-1330（output 直画）、:1146（desynchronized）、:1412-1413/:1423-1438（跳帧阈值）、:1482-1525（framed 逐帧 flush）。全文件无 requestAnimationFrame。
- 【实测-本次】%TEMP%\soullink-relay-27656.log（A2，2026-11-24 10:15-10:33）：编码器=NVENC-SDK fps=60 1920x1080、抓屏源 Intel 零拷贝禁用、captureMs avg 17 p95 100、convertMs ~2、encodeMs ~4、queueDepthMax=1、queueAge=0/max18、实测周期 26.5ms(37.7fps)。

## 9. 边界自查

未改任何产品代码/配置/版本号；未碰 dist\stable\SoulLink-Stable.exe；未使用 A1/A4；未杀进程；未 git 操作。唯一写入：本文档 + docs/research/README.md 一行登记。
