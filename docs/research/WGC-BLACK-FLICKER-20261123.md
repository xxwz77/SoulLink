# WGC 抓屏黑色闪烁根因审计 + BGI V2 对齐修复设计（2026-11-23）

> 日期：2026-11-23
> 作者：wgc-auditor（dsh-team `team_mwt4bbjy11d7lsz` / task `task_mwt4iptp11y2mkn`）
> 性质：**只读根因审计 + 修复设计（只设计，不改代码）**。全文未改任何产品代码/脚本/配置；唯一写入物即本文件。
> 输入：`Relay/WgcCapture.cs`（537 行，当前版本）、`artifacts/GraphicsCaptureV2.cs`（BGI V2，1312 行）、
> `docs/research/WGC-V2-RESEARCH-20261123.md`、`HANDOFF-VIDEO-LATENCY-DONE-20261123.md`（§3 降级链）、
> `BLACKSCREEN-ROOTCAUSE-20261123.md`、`WGC-VERIFY/A1-DYNAMIC/SNAPSHOT/TFM-VERIFY-20261122.md`、
> `Relay/SessionRecorder.cs`、`Relay/DxgiDdaCapture.cs`、`Services/WebPages.cs`（前端看门狗）。
> 证据等级标注：**[事实]** = 当前源码/文档可直接证明；**[推断]** = 由机制/设计同构推断（标注依据）；**[待验证]** = 需探针实测。

---

## 0. 一句话结论

**黑闪的第一根因是 stash 语义错误：我们在 `OnFrameArrived` 里只 stash 纹理裸指针（AddRef），却在同一个 `using` 里把 `Direct3D11CaptureFrame` Dispose 掉——帧池的 buffer 检出（check-out）语义挂在帧对象上，帧一 Dispose buffer 立即归还给 DWM 复写，AddRef 只保内存不保内容。于是延迟读回（`CaptureBgra`）读的是一块「DWM 可随时复写的内存」，窗口过渡/遮挡/RDP 抖动时 DWM 会往里写黑帧或写一半的合成内容 → 读回黑帧/撕裂帧。第二根因（被低估、确定性成立）：`ContentSize` 与帧池尺寸不一致时（窗口 resize/最小化/RDP 切换），`CopyResource` 因 staging 与源纹理尺寸不同必然 E_INVALIDARG → 全部读回失败 → 画面冻结，直到尺寸复原。黑帧过滤（L254-264）只是把这两个根因的症状「丢弃」掉，同时误杀合法全黑画面并制造帧空洞——治标且掩盖。**

修复设计对齐 BGI V2：**stash 帧对象而非纹理指针**（检出期=持有期，消费侧拷贝到私有纹理后立即 Dispose 检入）+ 帧池代数/Recreate + 单飞闸门/在途计数 + 无新帧跳过读回，四件套逐项给出行号、收益与风险。

---

## 1. 症状与现状

- 用户反馈：WGC 模式下**画面黑色闪烁**（闪烁=间歇性黑帧/暗帧/冻结交替，非持续黑屏）。持续黑屏的旧根因（帧池尺寸错配）已由 `BLACKSCREEN-ROOTCAUSE-20261123.md` 定位并以 `item.Size` 建池修复（WgcCapture.cs:119-126，当前代码已是 `int w = item.Size.Width, h = item.Size.Height`）。
- 现有兜底：`WgcCapture.CaptureBgra` L254-264 —— 读回后 `IsMostlyBlack`（L269-284，降采样纯黑像素 >50%）判黑即丢（返回 null），5 秒节流记一次日志（L261）。**过滤在生产环境命中**（用户简报确认该日志出现），说明 item.Size 修复后黑帧仍然产生——本报告解释「剩余黑帧从哪来」。
- 消费侧：`SessionRecorder.Loop` L409-414，`CaptureBgra` 返回 null → status=waiting，本 tick 不编码 → 前端 canvas 冻结在最后一帧；连续 ≥10s 无新解码帧触发前端看门狗重连（WebPages.cs:1116-1129，STALL_MS=10000）。

---

## 2. 七个嫌疑方向逐一验证

### 2.1 方向①：stash 生命周期竞态（头号嫌疑）——**成立，主根因**

**机制**：WGC 帧池的 buffer 检出/检入语义挂在 `Direct3D11CaptureFrame` 对象上：`TryGetNextFrame` 把一个 buffer 检出给应用，**帧对象 Dispose 才把 buffer 检回池里供 DWM 写下一帧合成**。AddRef 裸纹理指针只延长 COM 对象寿命（内存不被释放），**不延长 buffer 的检出状态**——帧池的槽位记账看不到我们的私有 AddRef。

**我们实现（WgcCapture.cs）**：
- L151-191 `OnFrameArrived`：排干帧池，L158 `using (frame)`，L179-186 锁内把 `GetInterface` 得到的纹理指针存进 `_stashedTexture`（L182，注释自认「GetInterface 已 AddRef，持有它」），**L187 using 结束帧对象即 Dispose** → buffer 归还 DWM，而我们 33ms 后才读它。
- L286-320 `DrainFramePool`（视频线程路径）：同样的模式（L308-315 换指针，L316 using 结束 Dispose）。
- L252 `CaptureBgra`：`ReadBackTexture(_stashedTexture, ...)` 直接对池 buffer 做延迟读回——此时该 buffer 已处于「DWM 可复写」状态。
- 帧池容量 2（L126）：排干后**两个槽全部空闲**，DWM 以合成节拍（~16.7ms）轮换使用它们——stash 指向的 buffer 在读回时刻被 DWM 拿去写下一次合成的概率接近 1。

**BGI V2 做法（GraphicsCaptureV2.cs）**：
- L26-28（注释，帧池容量 2 的理由）：「1 个 buffer 被 **stash 中的最新帧检出占用**，1 个始终留给 DWM 写下一帧合成」——检出占用的是**帧**，不是纹理。
- L60 `_stashedFrame` 字段类型是 `Direct3D11CaptureFrame?`（帧对象，非纹理指针）。
- L599-643 `OnFrameArrived`：L627-636 锁内只做帧对象交换（stash 新帧、记代数），**只有被替换的旧帧才 Dispose**（L635 `replaced?.Dispose()`）。
- L856-887 消费侧 `Capture()` 阶段0：取走 stash 帧 → L872 `TryRenderNewFrameLocked` 立即把帧内容拷贝/绘制到**私有 GPU 纹理**（_gpuTexture）→ L886 `stashedFrame?.Dispose()`「检入归还池（渲染已完成，buffer 交还 DWM）」→ 之后的所有读回（L989/L1017 Map）都发生在**私有纹理/staging buffer** 上，永不触碰池 buffer。
- L130-132（注释）：「消费时刻直接从跨进程表面回读会固定等待一个 DWM 周期（~20ms/帧），到达时写入则无此等待」——BGI 甚至发现在检出期外回读表面有额外代价。

**差异**：BGI 的不变式是**「buffer 内容只在其检出期（帧对象存活期）内被访问」**；我们的不变式是「AddRef 了内存指针，随时可读」——后者不成立。检出期被我们提前终止（using 立即 Dispose），而读取推迟到消费 tick。DWM 在这 0~33ms 窗口里可以也确实会复写该 buffer：正常时序下读到「比预期更新的一帧」（内容连贯，侥幸无害）；窗口过渡/遮挡切换/RDP 合成抖动时，DWM 会把 buffer 清成全黑再合成 → 我们读到黑帧或写一半的过渡内容 → **黑闪/暗帧/撕裂帧**。

**旁证链**：
1. 三方同构设计都遵守检出期访问：BGI V2 stash 帧对象（L60）；微软官方 BasicCapture sample 在 FrameArrived 内立即绘制后 Dispose，从不把 Surface 寿命延长到帧对象之外；FFmpeg gfxcapture（BGI L130 注释）到达时立即 draw 写入。我们是唯一「Dispose 后延迟读」的实现。[事实（三方代码/注释同构）+ 推断（由此定性复写机制）]
2. 我们自己的调研文档已写明正确语义但实现走样：`WGC-V2-RESEARCH-20261123.md` L26-29「只保留最新一帧挂到 _stashedFrame，被替换的旧帧立即 Dispose 归还」、L57「渲染完立即检入归还池」——**stash 的对象应是帧**；该文档 L88 却把我们的实现误标为「✅ 对齐」（「只 stash 纹理引用不读回 ✅ L179-187（AddRef 持有 texPtr）」）——**这个对齐判定是错的**，本报告予以更正。[事实]
3. 生产侧黑帧过滤日志仍在命中（用户反馈），而尺寸根因已修——剩余黑帧与本机制吻合。[事实（日志存在）+ 推断（归因）]

**是否黑闪嫌疑：是，主嫌疑（R1）。** 最终定级：机制为强推断（check-out 语义有 BGI 注释+三方同构佐证），**建议用 §6.1 的 A/B 探针一锤定音**。

> 附注（顺带发现）：`CaptureBgra` 持 `_stashLock`（L248）期间做整个 ReadBack（L252），会阻塞 `OnFrameArrived` 回调线程最多 2~8ms/帧——BGI 刻意避免「回调线程持锁冻结」（L56-58、L611-612 的锁序设计）。这是抖动隐患而非黑闪根因，随修复设计 A 一并消除。[事实（锁范围）]

### 2.2 方向②：帧对象 Dispose 与纹理生命周期 —— **COM 账目正确，但语义即 2.1 的病灶**

逐项核对 COM 引用计数：[事实]
- L158-187 `OnFrameArrived`：`GetInterface` AddRef 一次；换指针时对旧指针 `Marshal.Release`（L181）；`using` 保证帧对象释放。✔ 无泄漏。
- L286-320 `DrainFramePool`：同上对称。✔
- L193-208 `Stop`：L199-202 锁内 Release stash 指针。✔
- L393-417 `CaptureOnce`：L416 `finally { Marshal.Release(texPtr); }`。✔
- 读回期间旧纹理不会被释放：`CaptureBgra`/`CaptureJpeg` 都持 `_stashLock` 做读回，`Stop` 也要先拿 `_stashLock`（L199）才能 Release stash → **不存在「读回中纹理被释放/UAF」**。✔

**差异**：BGI 消费完才 Dispose 帧（L886）；我们 stash 时就 Dispose。问题不是泄漏而是 2.1 的「检回过早」。
**是否黑闪嫌疑：是，但与 2.1 是同一根因的两个侧面（Dispose 时机 vs 持有对象），不重复计数。**

### 2.3 方向③：Recreate/尺寸变化过渡期 —— **成立，第二根因，确定性比 2.1 更高**

**我们实现**：全文件**零次 Recreate、零次 IsIconic**（grep 证据：WgcCapture.cs 中 Recreate/IsIconic 零命中；ContentSize 只在 L176/L312-313/L395-396 被读值，无任何池重建/最小化保护）。[事实]

链条：
1. 帧池按 `item.Size` 建一次（L119-126），buffer 表面尺寸从此固定。
2. 帧 Surface = 池尺寸（固定）；`frame.ContentSize` 随窗口实际内容变化（resize、最小化缩成图标尺寸、RDP 分辨率切换）。BGI 注释 L665-666 明证：「最小化窗口的 ContentSize 会缩成图标尺寸」。
3. 我们把**ContentSize 当作纹理尺寸** stash（L183-184/L312-313），`ReadBackTexture` 用它建 staging（L436-449）再 `CopyResource(staging, srcTex)`（L452）。**D3D11 `CopyResource` 要求源/目标维度完全一致**：一旦 ContentSize ≠ 池尺寸 → E_INVALIDARG → catch（L473）→ 返回 null → **该状态下每一帧读回都失败**，画面完全冻结，直到 ContentSize 复原。[事实（代码路径确定性成立）+ 待验证（实际触发频率取决于挂机环境 resize/最小化频率）]
4. 由此产生的用户可见症状：resize/最小化/还原/RDP 切换瞬间起画面冻结（被感知为「黑了一下/闪一下」），复原后自动恢复——**间歇性闪烁的完美来源**。
5. 过渡期旧池帧/黑帧被当正常帧下发：无代数机制，尺寸漂移期间 stash 尺寸字段跟着 ContentSize 逐帧变（L183-184），下游编码器看到 ew/eh 抖动会反复重建（SessionRecorder.cs:445-455），GOP 反复打断。

**BGI V2 做法**：
- L662-664 ContentSize 变化检测 → L667-671 **IsIconic 保护**（最小化不 Recreate，保留旧资源供图）→ L681-686 在专属泵线程 `framePool.Recreate(...)` + L685 `Interlocked.Increment(ref _poolGeneration)` 帧池代数自增。
- L618-633 到达回调在取帧**前**读代数（L620），横跨 Recreate 的帧盖上旧代数被消费侧丢弃（L654-657 代数守卫）——防「旧尺寸帧反向把新池建回旧尺寸」。
- L704-723 Recreate 后按新尺寸惰性重建全部资源并作废缓存。
- L726-728 Recreate 当帧（旧尺寸 surface）直接跳过。
- 依据 BGI 注释 L181-182/L378：Win10 上 WGC WinRT 对象跨线程 Recreate 抛 `RPC_E_WRONG_THREAD`，必须路由回建池线程（泵线程，L186-265）——我们 TFM 的 TargetPlatformMinVersion=10.0.17763（WGC-TFM-VERIFY-20261122.md §2.4），Win10 在支持范围内，**该约束对我们同样适用**。

**差异**：BGI 有完整的 Recreate 状态机 + 代数 + 最小化保护；我们完全没有 → 任何尺寸事件后 WGC 输出全灭直到尺寸复原。
**是否黑闪嫌疑：是（R2）。** 触发即全冻（比 R1 更严重），但仅限尺寸变化场景。

### 2.4 方向④：临时帧池路径 vs 常驻泵线程路径 —— **视频流黑闪不来自临时路径；但临时路径有一个独立的「设备字段劫持」隐患**

**触发条件**（WgcCapture.cs:327-344）：`CaptureJpeg` 仅当 `_stashedTexture == 0`（无常驻缓存：视频管线未启动，或会话已停）才走 `CaptureOnce`；视频路径 `CaptureBgra` 第一次调用即 `AddRef()`（L246）→ `EnsureRunning` → `Start` 建常驻会话（L100-145，订阅 FrameArrived L136）。`CaptureOnce` 的临时池（L375-377）**不订阅 FrameArrived**（L375-381 无 += 语句），自旋 `TryGetNextFrame` 最多 5 秒（L383-390）。[事实]

**临时路径本身的帧语义反而正确**：L393 `using (frame)` 包住整个读回（L413），读回发生在检出期内 → 无 DWM 复写竞态。[事实]

**但有三个真实问题**：
1. **[事实] 无黑帧过滤**：`CaptureJpeg` 读回缓存路径（L332-341）和临时路径都不做 `IsMostlyBlack`——WGC 会话刚建立时 DWM 常先推若干全黑过渡帧，临时路径取到的**第一帧可能是纯黑**并直接 JPEG 下发（快照发黑）。
2. **[事实] 设备字段劫持**：L361-362 `_device = device; _context = context;`（注释自认「让实例字段指向临时设备」）+ L423 finally 复原 `_device = null; _context = null;`——**共享可变实例字段、无锁**。若快照（消息线程，AgentWebServer.cs:371 → RelayAgent.cs:1011）与视频线程并发：自旋最长 5 秒期间，视频线程的 `ReadBackTexture` 可能拿到临时设备/null 设备 → 读回失败或跨设备 CopyResource 失败 → 视频帧全 null 数秒 → 冻结。且 `Stop()`（L205-206）可能在期间 Dispose 正被劫持的字段。
3. **[推断] 快照触发概率低但后果大**：需要「视频未运行时来快照请求」+「期间视频恰好启动」双条件；后果是秒级画面中断，感知为闪断。

**BGI V2 对照**：BGI 无临时单帧路径（会话常驻、Start/Stop 都在泵线程，L287-437）；设备/上下文不设实例字段轮换，全部资源在 `Stop` 里与在途计数对齐后销毁（L1200-1203 等待 `_activeCaptures` 归零后才 Dispose，L1205-1241）。
**差异/是否黑闪嫌疑**：视频黑闪不来自临时路径本身；**字段劫持是独立隐患（R4，偶发长冻结）**；快照黑帧是可见 bug 但只影响截图。

### 2.5 方向⑤：读回 GPU 同步（缺 fence/flush？）—— **排除（我方管线自身无同步缺失）；但「读一半」症状归因于方向①**

**我们实现**：`ReadBackTexture` L452 `CopyResource` → L453 `Map(MapMode.Read)`。D3D11 语义：对 staging 的阻塞式 MAP_READ 会等到**同一设备上下文里先序提交的拷贝**完成，等的是自己刚提交的命令，不需要显式 fence。BGI 完全同款且注释明说：L985「普通阻塞 MAP_READ（ffmpeg hwcontext 同款）：等的是自己刚提交的按序拷贝」、L1014 同文；BGI 也**没有任何 fence**（grep：全部 Map 都带 MapFlags.None）。[事实]

**差异**：无差异（两者都依赖阻塞 Map 的隐式同步）。
**是否黑闪嫌疑：否——就「我方命令流」而言同步是完备的。** 但要害在于：**DWM 是另一个进程的另一条命令流，它对池 buffer 的写入不会排进我们的上下文队列，任何 fence 都管不到它**。所以「读到写一半的数据」这个症状真实存在，但病根是方向①（buffer 已检回给 DWM），不是缺 fence——加 fence 也治不了。R1 修复后该症状自动消失。

### 2.6 方向⑥：黑帧过滤的副作用 —— **成立（R3）：误杀合法全黑画面 + 制造帧空洞 + 掩盖根因 + 干扰探测**

**我们实现**（WgcCapture.cs:254-264 + 269-284）：
1. **[事实] 误杀合法全黑**：阈值「降采样后纯黑像素 >50%」（L283 `black * 2 > samples`，且要求三通道严格 0x00，L281）。游戏加载黑屏、暗场转场、无阴影边框的窗口整帧接近纯黑时都会命中 → 返回 null。之后下一帧来自下一次 `CaptureBgra`（33ms 后）重新读 stash——**如果画面持续黑，每一帧都被丢，形成持续空洞**，无「降级到上一帧」逻辑（对比 DDA：DxgiDdaCapture.cs 在所有失败路径都返回 `_lastBgra`，L160/L184/L196/L205/L211/L220/L226）。
2. **[事实] 空洞 → 冻结 → 看门狗**：SessionRecorder L409-414 null → waiting；连续 ≥10s 无解码帧触发前端重连（WebPages.cs:1116），重连重置解码器等 IDR → 合法黑场景（如加载 >10s）被放大成「冻结+重连循环」。
3. **[事实] 过滤与自动探测耦合**：`SessionRecorder.CaptureBgraWithProbe` 的 WGC 探测分支（L179-205）以「300ms 内拿到非空帧且 FrameSequence>0」为采用条件（L186）——**WGC 会话 warmup 期 DWM 先推黑帧 + 我们过滤丢弃 → 探测 300ms 超时 → 误降级 BitBlt**（撕裂后端）。即过滤不仅治标，还可能把本可用的 WGC 误判为不可用。
4. **[推断] 半黑帧穿透**：R1 的 DWM 复写产生的「部分黑/写一半」帧，黑占比在 30%~49% 时通过过滤直接编码 → 与被丢弃的黑帧交替出现 → **用户看到的就是「闪烁」**。过滤的 50% 阈值恰好处在「把最明显的黑帧丢掉、放行次明显的暗帧」的位置，制造了黑/冻结交替的节律。
5. **[事实] 诊断盲**：丢帧只 5 秒一条日志（L257-261），无计数、无连续性、无原因猜测，生产排障只能看到只言片语。

**BGI V2 对照**：BGI**没有**像素级黑帧过滤（WGC-V2-RESEARCH §3.3 已记录）；其防线是 IsIconic 保护（L667）+ 代数丢弃旧池帧（L654）+ Recreate 失败时显式作废缓存并返回 null 让上层感知（L691-698）——即**在「帧的产生端」保证帧有效，而不是在「帧的消费端」事后丢弃**。
**是否黑闪嫌疑：过滤本身不制造黑帧（不编码黑内容），但它是闪烁节律的放大器（R3）+ 根因的遮羞布。**

### 2.7 方向⑦：DDA→WGC 降级切换瞬间的黑帧 —— **轻微成立（R5），且热切路径行为可接受**

**我们实现**：
- 自动降级（SessionRecorder.cs:143-208）：DDA 300ms 未出帧 → `dda.Dispose(); Capture = new WgcCapture()`（L166-171）→ WGC 探测 300ms（FrameSequence>0 且非黑帧才采用，L186）→ 再降 BitBlt（L197-201）。切换后 `previousBgra`**不重置**（自动路径不动它）→ 新后端首帧与旧帧比对去重（L432）。
- 手动热切（L372-384）：`Capture.Dispose()` → 新后端 → `encoder?.Dispose(); frameIndex = 0; previousBgra = null` → 新编码器首帧必 IDR。
- 窗口期表现：新后端建会话到首帧 stash 之间 `CaptureBgra` 返回 null → waiting（前端 canvas**保留上一帧像素**：WebPages.cs:1075 `fail()` 里 abortStream 只收流「画面留着（冻结在最后一帧）」，setPh 是半透明提示层盖上去，L928-937——**服务端/前端都不会主动把画面清黑**）。唯一清黑点：canvas 尺寸变化时重设 `canvas.width/height`（L1088-1091）会把画布清成透明黑——后端间输出尺寸不同（DDA 整屏 2560x1440 vs WGC 窗口 1721x927）时首帧会造成一次清屏闪黑。[事实]
- **[推断]** 因此「降级瞬间黑闪」的实际表现是：短暂冻结（数十~300ms）+ 尺寸变化一次清屏 + 撕裂后端（误降 BitBlt 时）。幅度小、一次性的，不是持续性闪烁来源。

**BGI V2 对照**：BGI 无多后端降级链（单一 WGC V2 后端），不可对照。
**是否黑闪嫌疑：低（R5）。** 值得做的只是切换窗口期的桥接帧（见修复 G）。

---

## 3. 根因链排序（按对「黑色闪烁」的贡献度）

| 排名 | 根因 | 方向 | 机制 | 置信度 | 触发频率 |
|---|---|---|---|---|---|
| **R1** | **stash 语义错误：帧 Dispose 过早，AddRef 不保 check-out，DWM 复写被读缓冲** | ①+②+⑤ | 读回发生在 buffer 检回之后：过渡期 DWM 写黑/写一半 → 黑帧与半黑帧混入 | 机制强推断（BGI 注释+三方同构设计佐证），终验待 A/B 探针 | 常态存在（每次读回都有竞态窗口），过渡期爆发 |
| **R2** | **无 Recreate/无 IsIconic：ContentSize≠池尺寸 → CopyResource 全灭** | ③ | staging(ContentSize) vs 源纹理(池尺寸) 维度不一致 → E_INVALIDARG → 全部 null → 冻结 | 代码路径事实（一旦尺寸漂移必然全灭）；触发频率待验证 | 每次窗口 resize/最小化/RDP 切换 |
| **R3** | **黑帧过滤的节律放大 + 误杀 + 掩盖** | ⑥ | 半黑帧(<50%)穿透编码、全黑帧(>50%)丢弃成空洞，黑/冻结交替=闪烁；合法黑场景被冻结+重连；探测被误降 | 代码事实 + 症状推断 | 每个过渡/暗场景 |
| **R4** | **CaptureOnce 设备字段劫持（5 秒窗口）** | ④ | 快照线程轮换 `_device/_context` 实例字段，视频线程读回撞上 null/临时设备 → 秒级全冻 | 代码事实；并发触发概率低 | 偶发（快照与视频起停重叠时） |
| **R5** | **后端切换/热切窗口期** | ⑦ | 旧后端停、新后端未出首帧的数十~300ms 空洞 + 尺寸变化清屏一次 | 代码事实 | 每次切换一次 |
| R6 | GPU 同步缺失 | ⑤ | 不存在：阻塞 Map 的隐式同步已覆盖我方命令流；跨进程 DWM 写入不是 fence 能管的 | 事实（排除） | — |

**闪烁的完整画面**：R1 提供「黑帧/半黑帧」的原料（常态竞态，过渡期爆发）→ R3 把原料剪辑成「黑一下、冻一下」的交替节律 → R2 在尺寸事件时插入「整段冻结」→ R4/R5 偶发拉长空洞。用户看到的「黑色闪烁」是 R1+R3 的合成产物，R2 决定何时爆发成持续冻结。

---

## 4. 我们 vs BGI V2 逐条对照表

| # | 机制 | 我们实现（WgcCapture.cs） | BGI V2（GraphicsCaptureV2.cs） | 差异 | 是否黑闪嫌疑 |
|---|---|---|---|---|---|
| 1 | **stash 持有对象** | 纹理裸指针 `_stashedTexture`（L52，AddRef），帧在 using 里立即 Dispose（L158/187） | 帧对象 `_stashedFrame`（L60），仅被替换的旧帧 Dispose（L635），消费完才检入（L886） | **check-out 语义被我们提前终止** | ★★★ R1 主根因 |
| 2 | **读回对象** | 直接读池 buffer（L252/L338） | 只读私有 GPU 纹理/staging（到达时 draw/copy 写入，L731-810；Map 于 L989/L1017） | 我们读 DWM 可复写内存 | ★★★ R1 |
| 3 | **帧池代数** | 无（全文件 0 处） | `_poolGeneration` L61-66，取帧前读 L620，消费侧守卫 L654，Recreate 自增 L685 | 我们无法区分新旧池帧 | ★★ R2 |
| 4 | **ContentSize 变化处理** | 只记录尺寸字段（L183-184），无 Recreate，无 IsIconic | 检测→IsIconic 保护→泵线程 Recreate→代数自增→资源重建→跳过过渡帧（L662-728） | 我们尺寸漂移后读回全灭 | ★★★ R2 |
| 5 | **读回尺寸来源** | ContentSize（L183-184→L436-449 staging）vs 源=池尺寸 → 维度不匹配即 E_INVALIDARG | staging 用池尺寸（surface desc），客户区裁剪折进 UV/region（L749-753/L802-809），尺寸恒一致 | 我们把内容尺寸当纹理尺寸 | ★★★ R2 |
| 6 | **单飞闸门** | 无（消费线程隐式单线程，但快照线程可并发） | `_captureGate` L83，Capture 串行化 L831 | 我们缺并发防护 | ★ R4 |
| 7 | **在途计数** | 无（Stop 直接 Dispose 设备，靠 `_stashLock` 间接遮护） | `_activeCaptures` L86，消费前 ++ L958，Stop 等 Monitor.Wait 归零 L1200-1203 | 我们 Stop/在途读回有窄竞态 | ★ R4 |
| 8 | **无新帧跳过读回** | 无：每 tick 全量读回+黑检（L247-266） | `_cachedFrameTs` 比对 L905，未变则克隆缓存 L975-980（零 GPU/PCIe） | 我们静止画面空转 | 非黑闪，性能 |
| 9 | **黑帧策略** | >50% 纯黑即丢 + 5s 节流日志（L254-264） | 无像素过滤；产线保证（代数+IsIconic+Recreate 失败显式作废 L691-698） | 我们事后丢弃 vs BGI 事前保证 | ★★ R3 |
| 10 | **最小化保护** | 无 | IsIconic 不 Recreate、保留旧帧供图（L667-671，L303-307 启动提示） | 我们最小化期间行为不可控 | ★★ R2 |
| 11 | **WinRT 线程亲和** | 无泵线程；Start 于视频线程、Recreate 无从谈起 | 专属泵线程路由建池/Recreate/销毁（L186-265），跨线程抛 RPC_E_WRONG_THREAD 注释 L181-182/L378 | 我们做 Recreate 时必须补 | ★ R2 前置 |
| 12 | **回调线程纪律** | 回调等 `_stashLock`（消费读回持锁 2-8ms，L248-266） | 回调只做帧检出/检入，锁序单向（L54-58、L611-612） | 我们回调可能被读回阻塞 | 抖动非黑闪 |
| 13 | **临时单帧路径** | CaptureOnce 轮换 `_device/_context` 字段 + 5s 自旋（L346-427） | 无此路径（会话常驻） | 字段劫持隐患 | ★ R4 |
| 14 | **客户区裁剪** | 无（整窗含边框/阴影区） | region/UV 裁剪到客户区（L439-467/L749-753） | 我们画面带黑边 | 边缘黑非闪烁 |
| 15 | **GPU 打包 BGR24** | 读回 BGRA 8.3MB（L457 紧凑拷贝） | compute shader 打包 BGR24，回读 -25%（L93-128/L925-955） | 优化项，非正确性 | 否 |
| 16 | **失败兜底** | 返回 null（视频冻结） | Recreate 失败作废缓存显式返回 null + 响亮日志（L691-698）；Stop 泵失败仍继续本地清理（L1167-1189） | 我们缺「响亮失败」哲学 | 诊断性 |

---

## 5. 修复设计（只设计；对齐 BGI V2 四增强 + stash 正确姿势）

> 落点假设：全部改动收敛在 `Relay/WgcCapture.cs`（+必要时 SessionRecorder 探测分支微调）。不新增后端、不动 DDA/BitBlt。以下每项给：设计 / 预期收益 / 风险 / 验证。

### 5.1 修复 A（P0）：stash 帧对象 + 消费侧立即拷贝到私有纹理（BGI 检出模型）

**设计**（对齐 GraphicsCaptureV2.cs L60/L599-643/L856-887）：
1. 字段替换：`IntPtr _stashedTexture` + 尺寸字段（L52-54）→ `Direct3D11CaptureFrame? _stashedFrame` + `long _stashedGeneration`（配 5.2）+ `SizeInt32 _poolSize`（帧池真实尺寸，读回用）。
2. `OnFrameArrived`（L151-191）/ `DrainFramePool`（L286-320）改写：不再取 Surface→QI→GetInterface 裸指针；改为「取帧前读代数 → `TryGetNextFrame()` → 锁内 `replaced = _stashedFrame; _stashedFrame = next; _stashedGeneration = gen;` → 锁外 `replaced?.Dispose()`」。回调里**不做任何 D3D11 设备上下文操作**。
3. 新增私有广播纹理 `_gpuTexture`（DEFAULT usage，尺寸=池尺寸）+ `_gpuFrameTs`：消费侧（`CaptureBgra`/`CaptureJpeg`）取走 stash 帧后：代数守卫 → `frame.ContentSize` 变化检测（交给 5.2）→ **检出期内** `context.CopyResource(_gpuTexture, frameTexture)`（0.1ms 级）→ `frame.Dispose()` 检入 → 之后所有 staging 读回只面向 `_gpuTexture`（BGI L886 检入后再 Map 的同款顺序）。
4. `ReadBackTexture` 的尺寸参数改用 `_poolSize`（池尺寸）而非 ContentSize（消除 5.3 的维度不匹配前置条件）；ContentSize 若 ≠ 池尺寸，走 5.2 的 Recreate，而不是读一个不存在的区域。
5. 读回移出 `_stashLock`：锁只保护帧对象交换（对齐 BGI `_frameLock` 只保护 stash，L54-59）；回调不再被消费读回阻塞。

**预期收益**：R1 直接消灭（DWM 在检出期外永远拿不到我们正在读的 buffer）；顺带消除回调阻塞抖动；读回语义从「读 DWM 内存」变为「读自己纹理」。
**风险**：① 多一张池尺寸 GPU 纹理（1920x1080 BGRA ≈ 7.9MB 显存，可控）；② `frame.Surface` → ID3D11Texture2D 的 QI 代码保留（现有 L160-174 可复用，只是挪到消费侧、检出期内）；③ `CopyResource` 要求两纹理同设备——池由 `_winrtDevice` 创建、设备即 `_device`（L108-112 同源），成立；④ CopyResource 源必须与 _gpuTexture 尺寸一致——用 _poolSize 建私有纹理即恒成立。
**验证**：§6.1 A/B 探针（黑帧率/撕裂行统计）；R1 若成立，A 版黑帧率应归零或仅剩过渡期真实黑。

### 5.2 修复 B（P0）：Recreate 状态机 + 帧池代数 + IsIconic 保护 + 泵线程

**设计**（对齐 GraphicsCaptureV2.cs L61-66/L186-265/L654-728）：
1. 新增 `long _poolGeneration`（每次 Recreate 自增）。
2. 新增 WGC 专属泵线程（`BlockingCollection<Action>` + 后台 Thread，BGI L186-216 同款；**泵线程是会话级资源，永不内联退化为调用线程执行**，超时 2s 抛出）。`Start` 里的建池/建会话、`Stop` 里的销毁、以及 Recreate 全部 `RunOnWgcThread` 路由（Win10 RPC_E_WRONG_THREAD 防护，BGI L181-182 依据）。
3. 消费侧（取走 stash 帧后）：`gen != _poolGeneration` → 丢弃该帧（不拷贝、不 Recreate，等新代帧）；`frame.ContentSize != _poolSize` → `IsIconic(hwnd)` 为真则跳过（保留旧画面，BGI L667-671）；否则泵线程 `_pool.Recreate(_winrtDevice, format, 2, contentSize)` + 代数自增 + 重置 `_poolSize/_gpuTexture/_cached`（尺寸变化后私有纹理惰性重建）+ 本帧跳过（旧尺寸 surface 不再拷贝，BGI L726-728）。
4. Recreate 失败：计数 + 作废 `_gpuFrameTs/_cached` + 保留旧 `_poolSize` 等待下帧重试 + 响亮日志（BGI L691-698 哲学）。
5. 捕获目标句柄：`Start` 时记录 `item` 对应 hwnd（TryCreateFromWindow 的 hwnd，L212），供 IsIconic 使用。

**预期收益**：R2 直接消灭（resize/最小化/RDP 切换后要么 Recreate 续供、要么显式保留旧帧，不再全灭冻结）；旧池遗留帧不再污染新池（代数）。
**风险**：① 泵线程引入复杂度（约 80 行，BGI L186-265 可作设计模板，注意 MIT 只借鉴模式不抄实现）；② Recreate 在 RDP/挂机环境中的失败率未知（BGI 已遇连续失败场景并给了重试哲学）；③ IsIconic 需要窗口句柄——若走 TryCreateFromMonitor 降级则无 hwnd，需回退为「不做 IsIconic 保护，只做 Recreate」；④ 代数读写必须与 5.1 的锁序一致（回调只取 `_frameLock`，消费方先 `_captureGate` 再 `_lock`——对齐 BGI L58 无反向路径）。
**验证**：§6.2 resize/最小化剧本（A 现状应全灭，B 修复版应持续出帧或显式保留旧帧）。

### 5.3 修复 C（P1）：单飞闸门 + 在途计数 + CaptureOnce 去字段化

**设计**（对齐 GraphicsCaptureV2.cs L83-86/L831/L958/L1200-1203）：
1. `object _captureGate` 包住 `CaptureBgra`/`CaptureJpeg` 全程（串行化并发消费方）。
2. `int _activeCaptures`（锁内 ++/--）：离开锁做 Map/读回前置 ++，finally 归零并 `Monitor.PulseAll`；`Stop` 在 Dispose 设备/纹理前 `while (_activeCaptures > 0) Monitor.Wait`。
3. `CaptureOnce` 去字段化：`ReadBackTexture` 增加 (device, context) 参数重载，临时路径全程用局部变量，**删除 L360-362/L423 的实例字段轮换**（这是零成本纯减法）。
4. `Stop` 与 `AddRef/Release` 补齐 `_lifeGate` 互斥（当前 Stop 不持 _lifeGate，L194-208，与 AddRef 的 Start 有窄竞态）。

**预期收益**：R4 消灭；Stop/热切与在途读回的设备销毁竞态关死（BGI 用它保证 L1205-1241 的资源清点安全）。
**风险**：低——纯并发卫生；注意 `_captureGate` 不得嵌套 `_stashLock` 之外再加锁（沿用 BGI 锁序）。
**验证**：并发压测脚本（快照风暴 + 视频满速）观察句柄/显存稳定 >15 分钟（AGENTS.md §性能约束 4 的观察窗口）。

### 5.4 修复 D（P1）：无新帧跳过读回 + 缓存帧

**设计**（对齐 GraphicsCaptureV2.cs L88-92/L905-907/L975-981）：
1. `long _gpuFrameTs`（私有纹理当前内容的帧时间戳，5.1 拷贝时写 `frame.SystemRelativeTime.Ticks`）+ `byte[]? _cachedBgra`。
2. `CaptureBgra`：`_cachedFrameTs == _gpuFrameTs` → 直接返回 `_cachedBgra`（跳过 staging/Map/黑检）。
3. `IsMostlyBlack` 只在新帧读回后执行一次（缓存命中不重复判）。
4. 对齐 DDA 的 `_lastBgra` 惯用法（DxgiDdaCapture.cs L179-186）——项目内已有先例。

**预期收益**：静止画面（挂机常态）GPU/PCIe/CPU 回读归零——静态去重（SessionRecorder L432 SequenceEqual）之前就省掉读回；黑帧判定次数同样归零。
**风险**：低；注意缓存尺寸随 Recreate 重置；返回共享数组引用时调用方不得修改（当前消费方 SessionRecorder 只读比对+转 NV12，安全，需在注释声明契约）。
**验证**：静止场景 CPU 对比（对齐 DDA 修复时的同款量测法）。

### 5.5 修复 E（P1）：黑帧过滤降级为诊断 + warmup 门

**设计**：
1. **不再丢弃**稳态黑帧：A+B 落地后，过渡黑帧的产生源已被消灭，剩余黑帧是**真实内容**（加载/暗场）——过滤应放行（返回真实内容），只做计数与节流日志（保留 5s 节流但加 dropped/total 累计与连续计数、原因猜测：iconic/尺寸漂移/未知）。
2. **warmup 门**：仅在会话首 N 帧（如 30 帧 / 或 `FrameSequence < 30`）内丢弃全黑帧——专治「WGC 起始 DWM 黑过渡帧」；同时把 `SessionRecorder` WGC 探测分支（L179-205）的 300ms 超时改为「收到首个**非黑**帧或 800ms 超时」，避免 warmup 黑帧把探测误降 BitBlt（方向⑥第 3 条）。
3. 若无 D（缓存），过滤的误杀改用「连续 ≥K 帧全黑 → 返回上一帧 BGRA（DDA 同款兜底）」而非 null，消除断流空洞；有了 D 则直接回缓存。

**预期收益**：R3 消灭（合法黑场景不再冻结+重连循环；闪烁节律的「丢弃臂」被移除）；探测误降纠正；诊断信息从 1 行/5s 变成可定位的计数链。
**风险**：① 放行真实黑帧后，若 R1 未修（本项先行）会把黑帧透传给编码器——**E 必须与 A/B 同批或其后上线**；② warmup 阈值需实测定（建议探针统计起始黑帧长度分布）；③「返回上一帧兜底」会短暂显示陈旧画面（对挂机监控可接受，需注释说明）。
**验证**：加载黑屏场景 >30s 不再触发看门狗重连（WebPages STALL_MS=10000 不应触发）。

### 5.6 修复 F（P2，可选）：GPU 打包——按我们的下游适配为 BGRA→NV12 或 NVENC 直通

**设计说明**：BGI 的 GPU 打包 BGR24（L93-128 compute shader，回读 -25%）服务于 OpenCV CV_8UC3 消费者；**我们的消费者是 `BgraToNv12`（SessionRecorder L458/L670-699，1080p 单线程 CPU 转换）**，直译 BGR24 无意义。正确适配二选一：
- F1：把 BGI 的打包思路改成 **BGRA→NV12 GPU 打包**（同构 compute shader，NV12 恰为 12bit/像素，比 BGRA 回读省 62%）+ Map 紧凑 NV12；`BgraToNv12` 退役。
- F2：跳过 F1，直接对齐既有路线图 `GPU-ZERO-COPY-20261123.md`（NVENC SDK D3D11 纹理直通，读回彻底归零）——5.1 的 `_gpuTexture` 正好就是 NVENC REGISTER(DIRECTX) 需要的输入纹理形态，**A 是 F2 的前置台阶**。

**预期收益**：F1 转换 CPU 大降、回读 -62%；F2 零读回零转换（路线图已论证唯一路线）。
**风险**：F1 shader 正确性（BT.601/709 系数、半像素对齐）+ 工作量 1~2 人日；F2 依赖 NVENC 已有工程，改动面大，独立立项。
**验证**：NV12 字节级与 CPU 版逐帧 MD5 对比（项目已有码流级验证惯用法）。

### 5.7 修复 G（P2）：切换/降级窗口期桥接 + 尺寸稳定性

**设计**：
1. 后端热切（SessionRecorder L372-384）与自动降级（L163-201）时，新后端未出首帧前，编码上一份成功 BGRA（worker 里已有 `previousBgra`，热切路径显式置 null 了它——改为保留一份「桥接帧」只用于切过渡期），避免 waiting 空洞；前端本来就保留最后一帧像素（WebPages.cs:1075），桥接帧让画面「无感切换」。
2. 前端 canvas 尺寸变化清屏（L1088-1091）只发生在尺寸真变时——DDA 整屏→WGC 窗口的尺寸差不可避免，属一次性；不改。
3. `WgcCapture.Release()` 目前**无任何调用方**（grep 全仓 0 命中）——AddRef 之后永不 Release，30s 宽限停机是死代码，WGC 会话一旦启动常驻到进程退出（16MB 显存 + DWM 合成负载）。建议：SessionRecorder 在「观众+录像归零」的生命周期停机点调用 Release（配合 RecomputeLifecycle），让宽限机制真正生效；与本审计的闪烁主题无关，属顺带发现的资源卫生项，**先记录不动**。

**预期收益**：切换瞬间无空洞；空闲不再常驻占显存。
**风险**：桥接帧会短暂显示陈旧画面（一次、<300ms，可接受）；Release 接线要小心 AddRef/Release 配对（建议由实施者写生命周期对照表）。

### 5.8 实施顺序建议

```
第一批（黑闪根治）：A（stash 帧持有+私有纹理） → B（Recreate+代数+泵线程） → E（过滤降级+warmup 门+探测超时调整）
第二批（并发卫生）：C（闸门+在途计数+CaptureOnce 去字段化） → D（无新帧跳过）
第三批（性能/路线）：F2（NVENC 直通，复用 A 的 _gpuTexture） → G（桥接+Release 接线）
```
理由：A/B/E 直接消灭 R1/R2/R3（黑闪三主因）；C 关死 R4；D 是挂机场景性能红利；E 若先于 A 上线会透传黑帧，故锁死顺序。

---

## 6. 待验证清单（探针设计，未实施）

> 边界遵守：以下均为**建议实验**，本审计未写任何探针代码。工具落点建议 `tools/wgc-probe/`（现成基座，WGC-SNAPSHOT-20261122.md §5 提过其 COM 清理瑕疵，复用时按本报告 5.1 的帧持有姿势修）。

### 6.1 R1 终验：stash 纹理（现状）vs stash 帧（修复 A）A/B 黑帧率对比
- 同一窗口、同一负载，各跑 60s；每帧记录：IsMostlyBlack 结果、行级撕裂检测（对每行做 CRC，行间哈希突变数）、帧时间戳间隔。
- 人工剧本：遮挡切换/前窗切换/窗口拖动/RDP 带宽抖动（±10 个事件）。
- 判定：现状版黑帧/撕裂行显著 > 修复版（预期现状数十倍）→ R1 实锤；反之回炉。

### 6.2 R2 终验：resize/最小化剧本
- 运行中把目标窗口 resize（±10%~50%）与最小化/还原各 10 次。
- 现状预期：每次触发后 `CaptureBgra` 连续 null 直至尺寸复原（观察日志 waiting + FrameSequence 是否停止增长）；修复版预期：Recreate 日志 + 持续出帧（或 IsIconic 期间显式保留）。

### 6.3 事实核对补充（低优先）
- [待验证] WGC 起始 warmup 黑帧的典型时长分布（决定 E 的 warmup 门 N 与探测超时）。
- [待验证] Win11/Win10 各一遍 Recreate 跨线程行为（验证 RPC_E_WRONG_THREAD 是否复现，决定泵线程必要性档位）。
- [待验证] `GetForegroundWindow` 目标窗口含阴影/边框区在帧里的黑边占比（对照 BGI _region 裁剪，决定是否需要客户区裁剪——L14 差异项）。

---

## 7. 结论回顾（事实/推断/待验证汇总）

| 结论 | 等级 |
|---|---|
| 我们 stash 纹理指针、帧立即 Dispose（L158-187）｜BGI stash 帧对象、消费后 Dispose（L60/L635/L886） | 事实 |
| 帧池 buffer 检出语义挂在帧对象上；AddRef 不延长检出 | 推断（BGI L26-28 注释 + 微软 sample/FFmpeg/Sunshine 三方同构设计佐证） |
| 由此 DWM 复写被读缓冲 → 黑帧/半黑帧 → 黑闪主因（R1） | 推断（机制）+ 事实（过滤日志命中）——待 6.1 A/B 终验 |
| ContentSize≠池尺寸 → CopyResource E_INVALIDARG → 全部读回失败（R2） | 事实（代码路径；D3D11 CopyResource 维度一致性要求）——触发频率待验证 |
| 无 Recreate/无 IsIconic/无代数（我们）vs BGI 完整状态机 | 事实 |
| 过滤误杀合法黑画面 + 空洞→看门狗重连 + 半黑帧穿透成闪烁节律（R3） | 事实（代码行为）+ 推断（症状合成） |
| CaptureOnce 设备字段劫持 5s 窗口（R4） | 事实（代码）——并发触发概率推断为低 |
| 降级/热切窗口期空洞轻微（R5）；前端不清屏，仅尺寸变化清一次 | 事实 |
| GPU 同步无缺失（R6 排除）：阻塞 Map 隐式同步已覆盖我方命令流，BGI 同款无 fence | 事实（D3D11 语义 + BGI L985/L1014 注释） |
| `WgcCapture.Release()` 全仓零调用，30s 宽限是死代码 | 事实（grep） |
| WGC-V2-RESEARCH-20261123.md L88 的「stash 已对齐 ✅」判定有误 | 事实（本报告更正） |

---

## 8. 范围与合规声明

- 本次为**只读审计**：未改任何产品代码/脚本/配置，未 build，未重启进程，未碰 dist/stable/、A1/A4、COS、latest.json；唯一写入为本文件。
- 未在 `docs/research/README.md` 登记本文件——任务硬边界限定「唯一允许写入 WGC-BLACK-FLICKER-20261123.md」，与 AGENTS.md 的登记要求冲突，**留给 Lead/后续任务补登记**（建议条目：WGC-BLACK-FLICKER-20261123.md —— WGC 黑色闪烁根因审计（stash 帧持有语义/Recreate 缺失/过滤副作用三主因 + BGI V2 对照 16 项 + 修复设计 A-G））。
- BGI 为 MIT 许可，本报告只做机制对照与设计模式借鉴（到达即取/检出持有/代数是 Sunshine/官方 sample/FFmpeg 通用模式），不构成代码复制。

## 修订记录

| 版本 | 日期 | 内容 |
|---|---|---|
| v1.0 | 2026-11-23 | 初版（task `task_mwt4iptp11y2mkn`）：7 嫌疑方向逐一验证（5 立 2 排除/降级）、根因链 R1-R6 排序、我们 vs BGI V2 16 项对照、修复设计 A-G 含收益风险、待验证探针 3 项 |
