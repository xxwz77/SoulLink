# 缺陷总账：全仓库延迟 / 卡顿相关缺陷清单（AUDIT-20261124-07）

> 日期：2026-11-24
> 作者：auditor-ds（dsh-team `team_mwtjn0d77v8gtr` / task `task_mwtjodhu8k153q`）
> 性质：**纯只读审计**。未改任何业务代码、脚本、配置、版本号；唯一写入是本文档与 `docs/research/README.md` 的一行登记。
> 范围：C#（`Relay/` `Services/` `Controls/` `Views/` `Server/`）+ 网页观看端（`docs/web-index.html` / `Services/WebPages.cs`）。**不含** Android 端（`pixelflow-android` 在并行审）。
> 证据分级：**【代码事实】**＝当前工作区逐行可证；**【已有实测】**＝本仓库前序文档实测值（注明出处）；**【推断】**＝按代码结构推算的量级，未实测，需 dotnet-counters / 真机计时复核。
> 行号基线：工作区 HEAD `4cc3389`（2026-11-24 凌晨，git status 仅本文档 + README 登记两处变化）。
> 与其它成员的分工：本账只做「找缺陷 + 给修法建议」，不设计方案、不改代码。D01/D03/D04/D05/D07 建议 `sunshine-flow` 与 `builder-repro` 优先读；D08/D09/D10 建议 `moonflow-input` 读；D27/D32/D33 建议 `web-client` 读（已在 §7 点名）。

---

## 0. 结论摘要（先看这 6 条）

1. **最优先不是继续调编码参数，而是先修三个「把 GPU 路径废掉 / 让 CPU 路径更贵」的结构缺陷**：
   - **D01**：auto 抓屏探测把「当前没有新帧」当成「后端不可用」。**静止桌面起播时 DDA→WGC→BitBlt 会一路降级，并因 `_captureProbeComplete = true` 永久锁死 BitBlt**，之后整个 agent 生命周期都是 GDI 全帧抓屏、`TryCaptureBgraTexture` 恒 false、GPU 零拷贝永远不生效。这条能同时解释「captureMs 高」「零拷贝不生效」「GPU 直出没跑起来」。（【代码事实】`Relay/SessionRecorder.cs:143-213`，关键 `:166` `:198-206`）
   - **D03**：WGC 零拷贝分支拿的是私有 `_gpuTexture` 的共享句柄，但该纹理**只在 CPU `ConsumeBgra` 里更新**；零拷贝一旦成功就没人再调用 `ConsumeBgra` → 编码器一直编码同一张旧帧，画面会冻结/重复。**即使把 D01 修好、WGC 零拷贝「跑起来」，也会立刻变成新故障**。（【代码事实】`Relay/WgcCapture.cs:326-344`、`:365-414`；`Relay/SessionRecorder.cs:434-514`）
   - **D02**：每帧都有 5.25~14 MB 的 LOH 级分配（BGRA 读回、NV12 转换、NVENC 上传）。`SessionRecorder` 每秒 30 帧就制造约 200~600 MB/s 的大对象分配，Gen2/LOH GC 的停顿直接表现为「周期性一顿」。（【代码事实】`Relay/DxgiDdaCapture.cs:363`；`Relay/WgcCapture.cs:636`；`Relay/SessionRecorder.cs:852`；`Services/NvencSdkEncoder.cs:657-660`）
2. **444 ms 是链路多段叠加，代码能解释的部分集中在「采集/转换节拍 + 每帧分配 + 静止去重空转」**，不是单点。前序 `HANDOFF-VIDEO-LATENCY-GPU-20261124.md` 实测 captureMs 42 ms、延迟 444 ms；本账给出的 D01/D02/D05/D06 能直接攻击 captureMs 与帧时延抖动。**本账不声称能单独把 444 降到 60**，但 D01/D02 修复前，任何零拷贝/参数优化的收益都会被抵消。
3. **输入侧有三个「每事件/每命令」的固定税**：桌面控制台点击 = 2 次 RTT + 强制 12 ms（D08）；WH_MOUSE_LL 回调里每个鼠标事件都 `HotkeySpec.Parse`（字符串 Split + 字典遍历，D09）；控制通道单 FIFO 无优先级，`recordstatus`/`shot` 可把后面的 moverel/down/up 堵住（D10）。这三条比「再调灵敏度」更接近用户说的「鼠标很卡、落点不准」。
4. **UI 线程仍有残留的周期任务与同步 IO**：`ConsolePage` 150 ms 保护层定时器每拍 `new SolidColorBrush`（D17）；`ProcessPage`/`ActionEditorWindow` 在 UI 线程逐个账号起 `qwinsta`（D15/D16）；错误日志/托盘气泡同步落盘（D25）；`ConsolePage` 2 s 一次的调试用 `File.Exists` 永不停止（D18）。
5. **「智能降帧」这条反向优化仍在：静止跳过分支不推进 `nextAt`**，导致画面静止时视频线程以 ~5 ms（WGC/BitBlt）或 ~32 ms（DDA 零拷贝失败后 CPU 兜底）节奏反复抓屏/读回/比对，既烧 CPU，又让操作恢复时第一帧被积压拖慢（D05）。这与前序 `AGENT-PIPELINE-LATENCY-GAP-20261123.md §H1` 的「静止误判」是同一族问题的节拍侧。
6. **先修 D01/D03，再谈零拷贝路线**：D01 不修，WGC/DDA 的 GPU 直通在任何「起播时桌面静止」的场景都不可用；D03 不修，WGC 零拷贝一旦启用就冻结。之后按 §5 顺序做 D02（缓冲池）→ D04（DDA 重建兜底）→ D08/D09/D10（输入）→ D06/D07（CPU 段）。

---

## 1. 严重度定义

| 级别 | 判定 |
|---|---|
| **P0** | 直接导致主诉（444 ms 级延迟、画面冻结/永久降级、零拷贝不可用）或让后续优化全部失效 |
| **P1** | 明显增加单帧时延/输入时延/UI 冻结，稳态可感知；修复成本通常 < 1 人日 |
| **P2** | 稳态浪费、锁竞争、偶发尖峰；不修也能跑，但会持续消耗 CPU/GC/诊断能力 |
| **P3** | 轻微浪费或违反项目规则（AGENTS 性能约束），顺手修 |

★ = **用户没有直接提到、但确实造成卡顿/延迟**（§3 单独列出）。

---

## 2. 缺陷总账（38 条）

> 列说明：**代码位置**均为当前工作区 workspace-relative 路径 + 行号；**预估收益**里未标【推断】的是代码结构可直接消除的固定开销，标【推断】的是需要实测复核的量级。

| 编号 | 严重度 | 一句话现象 | 代码位置（文件:行号） | 根因 | 建议修法 | 预估收益 |
|---|---|---|---|---|---|---|
| **D01** ★ | **P0** | 静止桌面起播后抓屏永久降级 BitBlt，GPU 路径全废 | `Relay/SessionRecorder.cs:143-213`（`:145` 早退、`:166` DDA 误判、`:198-206` WGC 误判、`:203` 置 `_captureProbeComplete=true`） | 探测链把「本拍无新帧」当成「后端不可用」；WGC 降级分支还把探测完成标志置真，之后不再重试 | ① 探测完成只表示「选定」，不禁止重试；② BitBlt 仅作临时兜底，每 5~10 s 或收到输入后重试 GPU 后端；③ 增加 fallback 原因进 `StatusText`；④ 静止起播时用「等首帧事件/主动触发一次重绘」而不是 300 ms 超时判死 | 恢复 DDA 0.13 ms 级抓屏（【已有实测】`DXGI-DDA-RDP-20261123.md`）；captureMs 从 17~42 ms 回落个位数【推断】；零拷贝才可能生效 |
| **D02** ★ | **P0** | 每帧 5.25~14 MB LOH 分配，GC 周期性卡顿 | `Relay/DxgiDdaCapture.cs:363`；`Relay/WgcCapture.cs:636`；`Relay/SessionRecorder.cs:852`；`Services/NvencSdkEncoder.cs:657-660`；`Relay/BitBltCapture.cs:139` | 读回/转换/上传统统 `new byte[]` + `AllocHGlobal`，无帧缓冲池；>85 KB 全部进 LOH | ① 按尺寸缓存 BGRA/NV12/staging 缓冲（帧池）；② `BgraToNv12` 写入复用缓冲；③ NVENC 用固定 unmanaged/pinned 缓冲 + 复用；④ readback staging texture 也缓存（见 D07） | 消除 150~500 MB/s 的 LOH 分配；减少 Gen2/LOH GC 停顿（【推断】每次 10~30 ms，需 `dotnet-counters` 复核） |
| **D03** ★ | **P0** | WGC 零拷贝拿旧纹理，画面冻结/重复 | `Relay/WgcCapture.cs:326-344`（只返回现有 `_gpuTexture`）、`:365-414`（唯一调用 `ProcessStashedFrame` 的地方）、`:421-479`；`Relay/SessionRecorder.cs:434-514` | stash 帧 → 私有纹理的搬运绑在 `ConsumeBgra` 里；零拷贝分支不消费 stash，`_gpuTexture` 永不更新 | 把「检出 stash → CopyResource 到 Shared 私有纹理」抽成独立方法，零拷贝分支每帧调用（不读回 CPU）；或 `TryCaptureBgraTexture` 内部先搬运再返回句柄 | WGC 零拷贝启用后不再冻结；保住「WGC 兜底 + GPU 直通」这条路线 |
| **D04** ★ | **P0** | DDA 运行期失败后不重新进降级链；每帧重试 Start，失败时恒返回旧帧 | `Relay/DxgiDdaCapture.cs:166-170`（`EnsureRunning` 每帧重试 `Start`）、`:215-244`、`:317-334`；`Relay/SessionRecorder.cs:143-145`、`:545-556` | 降级判定只在探测期（`_captureProbeComplete=false`）执行；失败后 `CaptureBgra` 每次先重试 `Start`（可能反复建 D3D11 设备/ duplication），仍失败就返回 `_lastBgra`；`FrameSequence` 不增 → 被当静止跳过 | 运行期连续 N 帧 `!IsActive` 或 `LastError` 非空 → 重置 `_captureProbeComplete` 并降级 WGC/BitBlt；`Start` 重试加指数退避，避免每帧重建设备 | 避免全屏切换/ACCESS_LOST 后长时间冻结 + 每帧设备创建开销（稳定性） |
| **D05** ★ | **P1** | 静止时视频线程空转，重复读回/比对 | `Relay/SessionRecorder.cs:425-428`、`:558-563`、`:753-756`；`Relay/WgcCapture.cs:365-414`；`Relay/DxgiDdaCapture.cs:176`、`:264-265` | 静止 `continue` 不推进 `nextAt`；WGC 无新帧仍全帧读回；DDA 零拷贝失败后 CPU 路径再等一次 16 ms | ① 静止分支推进 `nextAt` 或进入「等帧事件/等输入」；② WGC `ConsumeBgra` 在「无新 stash 且内容未变」时直接返回 null；③ DDA 零拷贝失败当拍不再走 CPU 抓屏 | 静止桌面唤醒从 ~200 Hz 降到 ~fps；去掉无意义 14 MB 读回；操作恢复首帧更跟手【推断】 |
| **D06** ★ | **P1** | BGRA→NV12 单线程逐像素，每帧 3.5 MP×2 pass | `Relay/SessionRecorder.cs:849-877`（调用 `:596`） | C# 标量循环，无 SIMD/并行；每帧新数组 | 短期 `System.Numerics.Vector`/SSE 或按行 2~4 线程；中期 GPU 转 NV12（VideoProcessor/着色器，见 `MATURE-GPU-ZERO-COPY-EXPERIENCE-20261124.md`） | 减少 5~10 ms/帧【推断】（3.5 MP 估算，未实测） |
| **D07** ★ | **P1** | 每次读回都新建 staging 纹理 | `Relay/DxgiDdaCapture.cs:355`；`Relay/WgcCapture.cs:628` | 读回目标不缓存，驱动每帧分配/销毁 GPU 资源 | 按尺寸缓存 staging texture + Map/Unmap 复用；尺寸变化才重建 | 每帧少一次驱动资源创建（0.1~1 ms 及驱动 CPU）【推断】 |
| **D08** | **P1** | 桌面控制台点击 = 2 次 RTT + 12 ms 固定等待（诊断路径再多 1 次 RTT） | `Services/SessionInput.cs:53-65`；`Relay/RelayAgent.cs:850-865`（click 内 `Thread.Sleep(18)`，多击再 `Sleep(90)`）；`Controls/RdpConsole.cs:1763-1778`（点完再 `CursorAsync` 回读） | 绝对定位与点击拆成两条命令；未用现成 `Move=1`；目标端又插固定 Sleep；点击后还发一条诊断查询 | 用 `click + Move=1` 单条命令（`RelayAgent.cs:853` 已支持）；删掉 12 ms Delay 或多击间隔交给目标端；把 `CursorAsync` 回读改为仅 `--input-trace` 时执行 | 点击少 1 个 RTT + 12 ms（诊断路径少 2 个 RTT）；局域网 15~20 ms、WAN 50~80 ms【推断】；落点误差同步下降 |
| **D09** ★ | **P1** | 每个鼠标事件都解析热键字符串（WH_MOUSE_LL 回调、UI 线程） | `Controls/RdpConsole.cs:1571`、`:1622`、`:1748-1761`；`Services/HotkeySpec.cs:102-136`；`Services/LowLevelMouseHook.cs:101-153` | 配置字符串在每事件路径 `Split + ToLower + 遍历 VkNames`；未缓存解析结果 | 配置变更时解析一次存字段（`SettingsChanged` 刷新）；回调只做字段/标志位比较；热键→掩码 O(1) | 每个鼠标事件省一次 Split+分配；改善瞄准/拖拽跟手【推断】 |
| **D10** | **P1** | 控制通道单 FIFO 无优先级，输入被状态请求队头阻塞 | `Services/RelayClient.cs:531-580`（单 sender 顺序 write/read）、`:474-493`、`:631`；`Controls/RdpGeneralSettings.xaml.cs:55`（5 s 一次 recordstatus） | 所有请求共用一条 Channel + 一个写者；慢请求（shot 15 s、recordstatus 4 s）排前面时输入全等 | 拆「输入通道 / 控制通道」两条 FIFO（或两条 writer）；状态查询降频/缓存；输入短超时 + 过期丢弃 | 消除 100 ms~秒级输入卡顿尖峰【推断】 |
| **D11** | **P1** | 每个网页输入请求一次 `Task.Run` + 反射 JSON | `Relay/AgentWebServer.cs:129`、`:483-545`（`:491-492` 每请求 new options） | HttpListener 每请求独立任务；无源生成/池化；options 每请求新建 | 静态 `JsonSerializerOptions` + Source Generator；输入走有界单线程队列（保序 + 合并 move）；限制并发 | 125 Hz move 时减少线程池任务与 GC【推断】 |
| **D12** ★ | **P1** | MF 软编每帧建两份 sample、拷贝两次 5 MB | `Services/MfFrameEncoder.cs:269-276`（先建再拷）→ `:314-331`（`EncodeSoftware` 再建再拷） | 软编/硬编降级复用同一入口，构建逻辑重复 | 软编分支直接复用 `EncodeNv12` 已建 sample；或把 sample 构建下沉到分支 | 软编回退时省一次 5 MB 拷贝 + 两个 COM 对象/帧（1~3 ms）【推断】 |
| **D13** | **P1** | NVENC CPU 路径每帧 `AllocHGlobal(5.25 MB)` + 两次拷贝 | `Services/NvencSdkEncoder.cs:657-660`（`:659` UpdateSubresource） | 每次上传都分配/释放非托管内存，管理→非托管→GPU 三段 | 预分配固定缓冲复用；用 Vortice 的 `UpdateSubresource(byte[])` 重载省中间拷贝 | 每帧省一次 5.25 MB 分配 + 一次拷贝（1~3 ms）【推断】 |
| **D14** ★ | **P1** | 零拷贝每帧仍 `OpenSharedResource` + 新建 BGRA 纹理/输入视图 | `Services/GpuColorConverter.cs:191`、`:195-204`、`:137-142`、`:159` | 跨设备共享资源与视图每帧重建，未缓存 | 按尺寸缓存 `localBgra`/input view；句柄在会话内复用（注意 keyed mutex）；只重建尺寸变化 | 零拷贝真正启用后每帧少 2~3 个 GPU 资源创建（1~5 ms）【推断】 |
| **D15** ★ | **P1** | ProcessPage 每个账号一次 `qwinsta`，UI 线程同步等 | `Views/ProcessPage.xaml.cs:27-31`（Activate→RebuildPolicyRows）、`:316-341`（循环调用）、`:183-197`（`GetAwaiter().GetResult()`） | 每个账号单独查会话；UI 线程同步起子进程（每次 ~28~50 ms） | 进页时后台查一次会话列表并缓存；`IsMainUserAccount` 用当前 `SessionId` 与快照比较，不再逐账号起进程 | N 个账号省 (N-1)×30~50 ms UI 冻结【推断】 |
| **D16** ★ | **P1** | 「BGI 自动填入」按钮 UI 冻结数百 ms~秒级 | `Views/ActionEditorWindow.xaml.cs:850`、`:894-911`（QueryAsync + `GetResult`，再遍历 `GetProcessesByName` 读 `MainModule`） | UI 事件里同步起 qwinsta + 逐进程读 MainModule | 改 `async`/后台执行；MainModule 读取加 try/timeout，或一次 WMI 查询 | 点击按钮不再冻结【推断】 |
| **D17** | **P2** | 控制台页 150 ms 定时器每拍新建 4~6 个画刷并写属性 | `Views/ConsolePage.xaml.cs:557-644`、`:639`、`:691-704` | 状态灯每拍无条件重建 `SolidColorBrush`（未 Freeze）并赋 DP，触发渲染失效 | 仅在状态变化时更新；画刷缓存 + Freeze；150 ms 降到 500 ms 或事件驱动 | 省每秒 6.7 次无效渲染/分配（1~3% 单核）【推断】 |
| **D18** ★ | **P2** | 2 s 一次的调试钩子 `File.Exists` 永不停止（UI 线程） | `Views/ConsolePage.xaml.cs:87`（构造启动）、`:518-527`、`:458-467` | 调试用 save-request 钩子无开关、无停止 | 仅 `SOULLINK_DEBUG`/环境变量开启时启动；或 FileSystemWatcher；正式版删除 | 每 2 s 一次同步磁盘探测从 UI 线程消失 |
| **D19** | **P2** | 全局 `Gate` 持锁期间写日志、清 Ring、给观众入队 | `Relay/SessionRecorder.cs:606-631`（`:612-617` LogQuiet 在锁内）、`:457-475`、`:739-746` | 一把大锁覆盖状态更新 + 日志字符串 + 队列操作 | 字符串拼接与日志移出锁；Ring 清理与 viewer enqueue 用细粒度锁/并发队列 | 降低状态查询/新观众与视频线程的锁等待（毫秒级）【推断】 |
| **D20** ★ | **P2** | Ring 上限过大且每帧 `RemoveAt(0)` | `Relay/SessionRecorder.cs:464`、`:619`（`Math.Max(300, fps*keepSec*2)`） | 30 fps×30 s=1800 帧、120 fps=7200 帧；NAL 常驻数十~200 MB；`RemoveAt(0)` O(n) | 按录制需求缩小（`fps*keepSec`+少量关键帧余量）；环形数组/队列；非录制只留最近 GOP | 内存与 GC 压力下降，RemoveAt 抖动消失【推断】 |
| **D21** ★ | **P2** | 每帧对每个观众重扫 NAL 判关键帧 | `Relay/SessionRecorder.cs:471`、`:627`（`IsKeyFrame(viewer.Pending.Peek())` → `:828-833` 全字节扫） | 入队时已知道 `KeyFrame`（`VideoFrame` 有），队列却只存 `byte[]` | 队列存 `(byte[] nal, bool key)` 或封装对象，入队时带标志 | 多观众时省 O(观众数×NAL 大小)【推断】 |
| **D22** | **P2** | 推流每帧 `Write/Flush`，无帧时 10 ms 轮询 | `Relay/AgentWebServer.cs:451-461`（`:460` Task.Delay(10)）、`:444-450` | 无事件驱动信号，只靠轮询；每帧强制 Flush | 用 `SemaphoreSlim`/Channel 事件驱动；按帧边界批量写；确认是否真的每帧需要 Flush | 每帧最多 10 ms 额外排队【推断】；系统调用减少 |
| **D23** | **P2** | `/live` 仍会全帧抓屏 + JPEG，和视频线程抢抓屏锁 | `Relay/AgentWebServer.cs:267-271`、`:368-385`；`Relay/DxgiDdaCapture.cs:383-400`；`Relay/WgcCapture.cs:349-359` + `:371`（`_captureGate`） | JPEG 回退端点未删除；抓屏锁被 HTTP 线程持有做 2560×1368 JPEG 编码（几十 ms） | 删除 `/live` 或只返回最近 NAL/缓存图；若保留则限频 + 独立轻量后端 | 避免旧页面/工具回退 JPEG 时把视频线程阻塞几十 ms【推断】 |
| **D24** | **P2** | `StatusText`/`/api/info` 无 fallback 原因，线上无法定位降级 | `Relay/SessionRecorder.cs:287-309`、`:85`；`Relay/AgentWebServer.cs:252-265` | 只有当前后端名，没有「何时/为何降级、最后抓屏错误」 | 加 `captureReason/fallbackAt/lastCaptureError`；`/api/info` 暴露 | 诊断从小时级到分钟级（直接影响本轮 444 ms 排查） |
| **D25** | **P2** | 错误日志同步写盘；UI 线程每个 Error 弹托盘气泡 | `App.xaml.cs:464-475`、`:505-509`；`Relay/AgentWebServer.cs:196-206` | 异常/重连风暴时同步 IO + Shell 通知 | 统一走 `AsyncLog`；错误去重/限频；托盘气泡按分钟节流 | 异常/重连风暴时 UI 不被磁盘 IO 卡住【推断】 |
| **D26** ★ | **P2** | 每 3 s 重复枚举 BGI 进程/日志目录（后台线程） | `Views/LaunchPage.xaml.cs:1022-1031`、`:1246-1252`；`Services/BgiLogWatcher.cs:41`（TTL 2 s）、`:320-331`、`:343-360` | 3 s 定时器每次都 `Discover()`，缓存 TTL 只有 2 s，等于每拍全量枚举 | 复用 `AccountStateCenter` 的 BGI 快照（已有 `BgiLogFile`）；只在快照过期/切会话时发现 | 省每 3 s 一次 `GetProcessesByName + MainModule + 目录枚举`（20~50 ms）【推断】 |
| **D27** | **P2** | 前端跳帧阈值过激：队列 >2 就丢 delta，连丢 10 AU 就断流重连 | `docs/web-index.html:1162-1166`、`:1172-1187`、`:1131-1137`（10 s 看门狗） | 阈值按最坏情况设得太小，轻微抖动即进入跳帧→重连→重新等 IDR | 按真机数据调回（如 4~8 / 30~60）；重连前先请求 IDR；记录 skippedAUs 直方图 | 减少「重连→等 IDR」的秒级卡顿（需真机验证）【推断】 |
| **D28** | **P2** | 硬编事件等待固定 8 ms×2，慢 MFT 每帧固定多 16 ms | `Services/MfFrameEncoder.cs:373`、`:376`（`WaitForHardwareEvent(..., 8)`）、`:446-464` | 用固定超时轮询，而不是「有输出即返回」 | 事件回调 + 条件变量；把等待上限降到 1~3 ms 并统计超时；超时直接降级 | 硬编回退/慢 MFT 时每帧最多省 16 ms【推断】 |
| **D29** | **P2** | DDA 超时后立即返回复用帧，不让锁（与 Sunshine 相反） | `Relay/DxgiDdaCapture.cs:209-214`；对照 `MATURE-GPU-ZERO-COPY-EXPERIENCE-20261124.md §5`（Sunshine 超时 sleep 10 ms 让锁） | 当前单线程串行不咬人，但没有为后续抓屏/编码分线程留余地 | 分线程前先在超时分支加 1~10 ms 让锁；单线程阶段只记录计数 | 预防未来分线程后编码线程饿死（当前风险低） |
| **D30** | **P3** | 残留 `DispatcherTimer` 未收编 TickHub；一次性提示每次 new 一个 6 s 定时器不释放 | `Views/DataCenterPage.xaml.cs:23`、`:70-84`；`Views/SessionsPage.xaml.cs:33-39`；`Services/AutomationRunner.cs:34-36`；`Controls/FlowCanvas.cs:162-168`；`Views/LaunchPage.xaml.cs:1881-1888`；`Controls/GlassControls.cs:173` | 违反 AGENTS「周期任务走 TickHub」；一次性延时用 DispatcherTimer 且不 Dispose | 周期任务统一 TickHub；一次性延时改 `CancellationTokenSource + Task.Delay`；提示定时器复用 | 减少空闲唤醒与分配；符合项目规则（低） |
| **D31** ★ | **P3** | 流程编辑器每 750 ms 全量 new 画刷并写属性 | `Controls/FlowCanvas.cs:162-168`、`:785-805` | `RefreshLive` 对每个节点/边无条件 `new SolidColorBrush` 并赋值，状态没变也触发渲染 | 状态不变不写 DP；画刷缓存 + Freeze；定时器跟窗口激活联动 | 流程编辑器打开时去掉每 750 ms 的无效渲染/分配【推断】 |
| **D32** | **P3** | 前端时间戳硬编码 15 fps，服务端默认 30 fps | `docs/web-index.html:1210`（`ts += 66666`）；`Relay/SessionRecorder.cs:63-68`（默认 Stream=30） | 客户端假设 15 fps；服务端默认 30/可调到 120 | 用服务端 PTS/帧率，或只用单调序号；至少从 SPS VUI 读帧率 | 避免时间戳与真实帧率不符的调度抖动【推断，低】 |
| **D33** | **P3** | Web 每个网络 chunk 复制两次、每个 AU 再复制一次 | `docs/web-index.html:1221-1224`（pending+value 合并 new）、`:1194-1202`（AU 再 new+set） | 逐 chunk 拼接新数组；AU 重新打包 | 环形/链式 buffer；`subarray` 零拷贝切 NAL；AU 一次预分配 | 高频流下减少 renderer GC【推断，低】 |
| **D34** | **P3** | 录屏保存用 `Thread.Sleep(afterSeconds)`（最多 60 s）阻塞调用线程 | `Relay/SessionRecorder.cs:792` | 保存接口在 HTTP 请求线程里同步等待 | 后台保存任务 + 立即返回进度 | 避免偶发 60 s 挂起请求（低） |
| **D35** ★ | **P3** | 命名管道全量枚举持锁，病态环境可拖 7 s+ | `Services/BgiLogWatcher.cs:98-136`（锁内 `Directory.GetFiles(@"\\.\pipe\\")`，TTL 30 s；注释实测 7.3 s） | 枚举在锁内，超长枚举阻塞所有调用者 | 后台枚举 + 双缓冲替换；超时降级跳过本轮 | 避免后台刷新/线程池被长枚举拖死【推断，中低】 |
| **D36** | **P2** | 启动台 3 s 节拍在 UI 线程重建自动化卡片/概览 | `Views/LaunchPage.xaml.cs:100-124`、`:132-142` | TickHub UI 回调里做 `RefreshAutomationList/RefreshOverview/UpdateOverviewTexts`；虽有签名去重，仍每次检查+可能重建 | 把「取数/算签名」放后台，UI 只 Apply diff；把两个订阅合并成一个 3 s 节拍 | 降低每 3 s 的 UI 峰值【推断】 |
| **D37** ★ | **P3** | 每条日志 `ObservableCollection.Insert(0)` + `RemoveAt(末尾)`，绑定列表全量响应 | `Services/LogBus.cs:77-91` | 高频日志时 UI 线程 O(600) 搬移 + CollectionChanged | 用批量/节流刷新（例如 100 ms 合并一次）；上限裁剪成环形 | 自动化/重连日志风暴时 UI 更稳【推断】 |
| **D38** ★ | **P3** | BGI 日志每秒最多解析 4 MB，`Split('\n')` + 每行 Regex 在后台抢 CPU；首次全量后触发 LOH compacting GC | `Services/BgiLogCenter.cs:599`（4 MB 预算）、`:698-708`（Split+Regex）、`:661-670`（`GC.Collect(2, compacting:true)`） | 后台 1 s 节拍解析；多账号同时活跃时 CPU 峰值与视频线程争核 | 解析预算按账号分摊/降频；按行流式解析避免大字符串 Split；GC.Collect 移到低频维护窗口 | 降低后台 CPU 峰值对采集线程的抢占【推断，中低】 |

> **合计 38 条**（P0 4 条、P1 12 条、P2 14 条、P3 8 条）。其中 ★ = 20 条用户未直接提到的隐性缺陷。

---

## 3. 用户未提及、但确实造成卡顿/延迟的隐性缺陷（★，20 条）

> 说明：用户直接抱怨的是「延迟 444 / 鼠标卡 / 虚拟鼠标废」，没有点名下面这些机制；但每一条都能在代码里定位，且会对体感产生可测影响。按「不看不知道、看了能解释体感」排序。

1. **D01** 静止桌面的探测链把抓屏永久降级 BitBlt（用户只看到「GPU 没用上」，看不到降级逻辑）。
2. **D02** 每帧 LOH 分配（GC 周期性停顿，用户只会说「偶尔一顿」）。
3. **D03** WGC 零拷贝陈旧纹理（一旦修好 D01 会立刻暴露成冻结，属于「先修反而更明显」的坑）。
4. **D04** DDA 重建失败后永久返回旧帧（用户描述成「全屏切出去再回来就卡住了」）。
5. **D05** 静止跳过不推进节拍（静止桌面 CPU 空转 + 操作恢复首帧被积压）。
6. **D06** BGRA→NV12 单线程逐像素（每帧 3.5 MP×2 pass，软编/CPU 回退时的固定成本）。
7. **D07** 每次读回新建 staging 纹理（驱动每帧分配/销毁 GPU 资源，隐性 CPU 税）。
8. **D09** 每个鼠标事件解析热键（自由视角/瞄准时 UI 线程被字符串解析占住）。
9. **D12** MF 软编双份 5 MB 拷贝（非 N 卡/硬编不可用时的隐藏成本）。
10. **D14** 零拷贝每帧重建 GPU 资源（零拷贝一旦启用，这部分会把收益吃掉一半）。
11. **D15** ProcessPage 每账号一次 qwinsta（进「进程管理」页必卡一下）。
12. **D16** 「BGI 自动填入」UI 同步起进程 + MainModule（按钮点下去先白一下）。
13. **D18** 2 s 一次 `File.Exists` 调试钩子（挂机时永久存在）。
14. **D20** Ring 上限 1800~7200 帧（内存/GC 常驻压力）。
15. **D21** 每帧重扫 NAL 判关键帧（多观众时 CPU 陡增）。
16. **D26** 3 s 一次全量 BGI 进程发现（后台 CPU 持续 20~50 ms/次）。
17. **D31** 流程编辑器每 750 ms 全量画刷重建（打开编辑器就持续烧）。
18. **D35** 管道枚举持锁 7 s 的极端 case（第三方软件泄漏管道时整个后台刷新被拖死）。
19. **D37** 日志集合每条 Insert(0)（自动化日志风暴时 UI 卡）。
20. **D38** BGI 日志 4 MB/秒解析 + 行 Regex（多账号时与视频线程抢核）。

> D06/D07 属于「CPU 回退路径的固定成本」，只有当 D01 修复后仍走 CPU 路径（非 N 卡/VideoProcessor 不可用）时才成为主瓶颈；D20/D21 是「多观众 + 长时间挂机」才放大，单观众短时不会成为主因。修复排序见 §5。

---

## 4. 与成熟实现的差距（只列与本账相关的，不重复前序调研）

| 主题 | 成熟实现（证据出处） | SoulLink 现状（file:line） | 差距判定 |
|---|---|---|---|
| 采集后端降级 | Sunshine 的 DDA 超时 **让锁 10 ms**，不把「本拍无帧」当成后端死亡（`display_base.cpp:320-338`，见 `MATURE-GPU-ZERO-COPY-EXPERIENCE-20261124.md §5`） | D01 把「无新帧」当后端不可用并永久置 `_captureProbeComplete`（`SessionRecorder.cs:198-206`） | **方向相反**，必须改 |
| 帧缓冲复用 | OBS/Sunshine 的输入纹理/缓冲按尺寸复用；OBS 还 `SetEvictionPriority(MAXIMUM)`（`obs_nvenc-d3d11.c:98-115`） | 每帧 `new byte[]` / `AllocHGlobal` / 新建 staging（D02/D07/D13） | 缺帧池，GC 抖动 |
| 零拷贝纹理更新 | Sunshine 的 `display_vram.cpp` 每帧把抓到的纹理转 NV12 再编码（`convert()`），不存在「只读旧纹理」 | WGC 零拷贝不消费 stash，`_gpuTexture` 不更新（D03） | **正确性缺陷**，先于性能 |
| 输入合并/点击 | Sunshine 相对位移独立包；Moonlight 同帧位移合并（见 `MATURE-LATENCY-PLAYBOOK-20261123.md §4`）；Parsec 编/解各 <15 ms | 桌面点击 2 RTT + 12 ms（D08）；热键每事件 Parse（D09）；控制通道单 FIFO（D10） | 输入侧固定税 |
| 静止节拍 | OBS `video_sleep` 绝对时刻 + 落后帧统计（`obs_video.c:808-828`）；Sunshine pacing group 组内 0 ms 取帧 | 静止跳过不推进 `nextAt`（D05）；`WaitUntilNextSlot` 只在编码后推进（`SessionRecorder.cs:739-746`） | 节拍语义缺一条 |
| 客户端解码队列 | Moonlight 有界队列 15 + 溢出 flush + RFI 优先（`mlcc_VideoDepacketizer.c`）；SoulLink 网页 `SKIP_QUEUE=2/10` 更激进（D27） | 网页 `web-index.html:1162-1187` | 阈值需按实测回调 |
| 诊断可观测性 | 成熟实现把「实际后端/降级原因/落后帧」纳入状态（Sunshine 日志） | `StatusText` 无 fallbackReason（D24） | 先补埋点，否则无法验证零拷贝是否生效 |

---

## 5. P0 / P1 建议开工顺序（附依赖关系）

> 原则：先修「让 GPU 路径不可用」的，再修「让 CPU 路径更贵」的，最后修输入/UI 固定税。**D01 不修，任何零拷贝验收都不可信**。

**第 1 批（P0，必须先做，1~2 天）**
1. **D01** 探测链重试语义（`SessionRecorder.cs:143-213`）：改为「探测选定 ≠ 永久不重试」，BitBlt 仅临时兜底，静止起播不再判死后端。验收：静止桌面起播后 `/api/info` 的 `capture` 仍为 DDA/WGC，且 `captureMs` 不出现 17~42 ms。
2. **D03** WGC 零拷贝纹理更新（`WgcCapture.cs`）：零拷贝分支每帧消费 stash → CopyResource 到 Shared 私有纹理。验收：WGC 零拷贝下画面持续更新、PSNR 埋点仍达标。
3. **D04** DDA 运行期失败重新进降级链（`DxgiDdaCapture.cs:317-334` + `SessionRecorder` 探测标志），与 D01 同一处改。验收：全屏切换/ACCESS_LOST 后 5 s 内恢复出帧或降级到 WGC/BitBlt。
4. **D02** 帧缓冲池（分三步：readback 数组 → staging 纹理 → NV12/NVENC 上传）。验收：`dotnet-counters` 的 `gen-2 gc count`、`LOH size` 不随推流时间线性增长；帧时延 p99 下降。

**第 2 批（P1 输入，0.5~1 天，用户体感最快）**
5. **D09** 热键解析缓存（改动最小，直接改善鼠标跟手）。
6. **D08** 点击合并成单命令（`click + Move=1`），删除 12 ms Delay。
7. **D10** 控制/输入通道拆分或优先级（与 D08 一起验收：按住移动/点击时不再被 recordstatus 卡住）。

**第 3 批（P1 视频 CPU 段，1~2 天）**
8. **D05** 静止分支推进节拍 / 等帧事件（与 D01 一起改）。
9. **D07** staging 纹理缓存（D02 的子项）。
10. **D06** BGRA→NV12 SIMD/并行（或直接切 GPU 转 NV12，视 D01 是否恢复 GPU 路径而定）。
11. **D13/D12/D14** 编码器/转换器缓冲复用（D02 的子项，按是否走 NVENC CPU/MF/零拷贝分支分别做）。
12. **D22/D11** 输入/推流请求模型（事件驱动 + 有界队列），需与 `web-client`/`moonflow-input` 对齐协议。

**第 4 批（P1 UI 固定税，0.5 天）**
13. **D15/D16** qwinsta/进程枚举移后台 + 一次快照复用。
14. **D25** 错误日志异步化/节流（顺手修 D18 的调试钩子）。

**第 5 批（P2/P3，按需）**
15. D17/D30/D31/D36/D37（UI 定时器/画刷/日志集合）→ D20/D21（Ring/关键帧标志）→ D19（锁粒度）→ D23/D24（/live 与可观测性）→ D26/D35/D38（后台 CPU）→ D27/D32/D33（网页端阈值/时间戳/拷贝）。

> **不建议**在 D01/D02 之前投入「GPU 零拷贝路线A/着色器转 NV12」：当前 444 ms 里 capture 与 GC 抖动占大头，先把数据流拉回 GPU、把 CPU 分配降下来，再谈同 device/着色器，否则会重复「代码正确但真机不生效」的弯路。

---

## 6. 验证建议（只读/低风险，不改系统）

1. **GC/LOH**：`dotnet-counters monitor -p <agent pid> System.Runtime`，观察 `gen-2 gc count`、`loh size`、`alloc-rate`；静止/操作两种场景各 2 min。预期 D02 修复后 LOH 增长斜率显著下降。
2. **后端是否真的在用**：读 `SessionRecorder.StatusText()`（`recordstatus` 命令）与 `/api/info` 的 `capture`；新增 D24 的 `captureReason` 后，能直接看到「何时降级、为什么」。
3. **静止空转**：静止桌面 30 s，记录 agent 进程 CPU 与 `staticSkipped`/`loop` 增量（`StatusText` 已有字段）。D01/D05 修复前后对比，CPU 应明显下降、`loop` 不应在静止时高速增长。
4. **点击延迟**：在 `SessionInput.ClickUiAsync` 两端打 `Stopwatch`（只加临时诊断，不进提交），对比「两次 RTT」与「单命令 Move=1」；同时看 `RelayClient.InputTrace`（`--input-trace` 才启用，默认零开销）。
5. **鼠标事件成本**：`dotnet-trace collect --providers Microsoft-Windows-DotNETRuntime` 采 15 s，看 `HotkeySpec.Parse` 在热路径的占比（前序同类实测：`LowLevelMouseHook.Callback` 曾占 3.80%，见 `Services/LowLevelMouseHook.cs:109-120` 注释）。
6. **UI 冻结**：ProcessPage/ActionEditor 点击时用 UI 线程响应性探针（`Dispatcher.InvokeAsync` 往返计时）或 WPF `Render` 帧间隔；预期 D15/D16 修复后 <16 ms。

---

## 7. 给其他成员的「去读」清单（按你的范围）

- **`sunshine-flow` / `builder-repro`**：优先读 **D01 / D03 / D04 / D05 / D07**——这五条决定「GPU 抓屏 + 零拷贝」到底有没有机会跑起来；D01 的探测链与 D03 的 stale 纹理是本轮 444 ms 归因里最硬的两块代码证据。
- **`moonflow-input`**：读 **D08 / D09 / D10 / D11**——输入侧的三个固定税（2 RTT+12 ms、每事件热键 Parse、单 FIFO 队头阻塞）与网页输入请求模型；本账只给代码位置和建议，方案由你定。
- **`web-client`**：读 **D27 / D32 / D33**——前端 `SKIP_QUEUE=2`/10 AU 重连、15 fps 硬编码时间戳、chunk 双拷贝；这些和网页「卡一下/重连」直接相关，建议与真机 `WEB-RUNTIME-PROBE` 数据对齐后再调阈值。
- **`cursor-ux`**：D01 的 `capture`/fallback 状态与「可见光标是否显示」无直接冲突；但若你要在 `/api/info` 增加字段，请与 D24 一起提，避免两次改协议。
- **Lead**：本账可直接作为汇总输入；P0 的 4 条有依赖顺序（D01→D03→D04→D02），不要并行改同一文件（`SessionRecorder.cs` 同时被 D01/D04/D05 触碰，`WgcCapture.cs` 只被 D03 触碰）。

---

## 8. 审计方法与范围（可复核）

- 只读扫描：`Relay/`（7 个文件，SessionRecorder 921 行、AgentWebServer 585 行、WgcCapture 717 行、DxgiDdaCapture 423 行、BitBltCapture 203 行、RelayAgent 1111 行）、`Services/`（编码器、GpuColorConverter、RelayClient、TickHub、AsyncLog、LogBus、BgiLogWatcher、BgiLogCenter、SessionInput、LookInputQueue、HotkeySpec）、`Controls/`（RdpConsole、LowLevelMouseHook、FlowCanvas、DreamScene、Backdrop、GlassControls、FlowGuideOverlay）、`Views/`（ConsolePage、ProcessPage、ActionEditorWindow、LaunchPage、SessionsPage、DataCenterPage、MyDepartWindow、JoinRoomWindow）、`App.xaml.cs`、`docs/web-index.html`、`Services/WebPages.cs`。
- 关键词：`DispatcherTimer`、`new Timer`、`Thread.Sleep`、`Task.Delay`、`lock (`、`SemaphoreSlim`、`new byte[`、`AllocHGlobal`、`new Bitmap`、`File.AppendAllText`、`GetAwaiter().GetResult()`、`Process.GetProcesses`、`GetProcessesByName`、`Directory.GetFiles`、`GC.Collect`、`CompositionTarget.Rendering`、`InvalidateVisual`、`setInterval`、`requestAnimationFrame`、`decodeQueueSize`。
- 扫描结果：`DispatcherTimer` 实例化仍存 16 处（其中 6 处属于周期任务、未收编 TickHub，见 D30）；`GetAwaiter().GetResult()` 同步等待 12 处（其中约 5 处在 UI 路径）；`Process.GetProcessesByName` 多处落在 UI 事件/页面路径（D15/D16/D26）；每帧 `new byte[]`/`AllocHGlobal` 5 处。
- 本账**未覆盖**：Android `pixelflow-android` 范围（ExoPlayer/MediaCodec 缓冲、触控采样）；`Server/` 服务端与大厅（与画面/输入时延无直接关系）；`tools/` 测试工具（不影响生产）。

## 9. 修订记录

| 版本 | 日期 | 内容 |
|---|---|---|
| v1.0 | 2026-11-24 | 初版：38 条缺陷总账（P0 4 / P1 12 / P2 14 / P3 8），20 条用户未提及；附 P0/P1 开工顺序、验证建议、成员分工 |
