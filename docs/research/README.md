# 研究文档索引

> 归档时间：2026-11-21
> 原分散在 `releases/`、项目根目录、`research/` 三处，现统一收在此目录。

## RDP / 远程桌面架构研究

- [1REMOTE-WINDOWING-RESEARCH-20261117.md](1REMOTE-WINDOWING-RESEARCH-20261117.md)
- [FULLSCREEN-RDP-RESEARCH-20261121.md](FULLSCREEN-RDP-RESEARCH-20261121.md)
- [MOBAXTERM-RDP-ARCHITECTURE-RESEARCH-20261117.md](MOBAXTERM-RDP-ARCHITECTURE-RESEARCH-20261117.md)
- [MOBAXTERM-RDP-EMBEDDING-DEEP-DIVE-20261117.md](MOBAXTERM-RDP-EMBEDDING-DEEP-DIVE-20261117.md)
- [MULTITAB-MULTIVIEW-COEXISTENCE-DESIGN-20261117.md](MULTITAB-MULTIVIEW-COEXISTENCE-DESIGN-20261117.md)
- [MULTITAB-MULTIVIEW-INTEGRATION-20261117.md](MULTITAB-MULTIVIEW-INTEGRATION-20261117.md)
- [RDP-NATIVE-VISIBILITY-FIX-20261117.md](RDP-NATIVE-VISIBILITY-FIX-20261117.md)
- [RDP-SPLIT-INCIDENT-20261117.md](RDP-SPLIT-INCIDENT-20261117.md)
- [RDP-WINDOWING-RESEARCH-SUMMARY-20261117.md](RDP-WINDOWING-RESEARCH-SUMMARY-20261117.md)
- [SIMPLEREMOTE-TABBED-RDP-RESEARCH-20261117.md](SIMPLEREMOTE-TABBED-RDP-RESEARCH-20261117.md)
- [RDP-CONFIG-FIX-20261122.md](RDP-CONFIG-FIX-20261122.md) —— **RDP 配置页两处缺陷修复**（删除用户「回来」根因 = 远程管理页删 Windows 账户与中控台连接配置不联动，非重建非缓存；默认页改为选中「通用设置」；附用户验证步骤与「文案区分」待决策项）

## 会话瘦身 / 会话隔离

- [SESSION-SLIM-DEEP-20261122.md](SESSION-SLIM-DEEP-20261122.md) —— 会话瘦身两层拆分（分账号杀进程 / 机器级 HKLM 策略）
- [SESSION-SLIM-DEEP-V3-20261122.md](SESSION-SLIM-DEEP-V3-20261122.md) —— **SessionSlimmer 深度优化补充**（三层设计 / 会话43实测 54进程2168MB / SearchHost块392MB+Widgets块99MB精确归因 / 注册表禁用+恢复命令 / 是否影响操作逐项判断 / 会话隔离架构结论 CreateDesktop不可替代RDP / INJECTED对原神=功能边界非封号风险）
- [SESSION-POLICY-FIX-20261122.md](SESSION-POLICY-FIX-20261122.md) —— **注册表策略修复**（小组件/搜索是 class=Machine，旧代码写用户分支从未生效；新增 `ApplyMachinePolicy` 写 HKLM + 代理启动写 HKCU Edge 策略（路径A绕开已登录无法挂载），附准确键名/恢复方法/路径B建议）
- [SESSION-SLIM-SWITCH-WIRING-20261122.md](SESSION-SLIM-SWITCH-WIRING-20261122.md) —— **会话瘦身三开关接线 + 两页面统一**（`SessionSlimKillEnabled` 接入 `Slim()` 生效；说明页改只读+跳转，操作页三开关齐全；接线对照表）

## 自动化 / 流程引擎

- [AUTOMATION-BUG-20261122.md](AUTOMATION-BUG-20261122.md) —— **自动化「无 END + 日志触点只触发一次」根因审计**（数据层时间窗语义，非引擎锁存卡死；含最小复现测试 `tools/layout-check/LogRetriggerCheck`）
- [LOG-LATCH-DESIGN-20261122.md](LOG-LATCH-DESIGN-20261122.md) —— **日志触点「事件锁存 + 消费」实现**（TrigLogMatch 从「最近 N 秒电平」改为「出现即锁存、动作消费后清空」，替代时间窗方案；含 `tools/layout-check/LogLatchCheck`）

## 视频流 / 远程画面（2026-11-22 主线）

- [ENCODER-REBUILD-HANG-20261123.md](ENCODER-REBUILD-HANG-20261123.md) —— **编码器频繁重建与产帧中断根因**（DDA ACCESS_LOST 仅重建 duplication；静止复用+去重导致 total 中断，新 viewer IDR 与去重冲突致起播失败；自恢复与参数去抖方案）
- [WGC-V2-RESEARCH-20261123.md](WGC-V2-RESEARCH-20261123.md) —— **WGC V2 帧完整性与撕裂规避调研（对齐 BGI）**（拆解 BGI GraphicsCaptureV2.cs 到达即取+单槽stash+帧池容量2+帧池代数+无新帧跳过读回+GPU打包BGR24；对照微软官方 WGC 与我们的 WgcCapture 538行；结论：核心模型已对齐，建议补帧池代数+无新帧跳过读回两条，作为 DDA 失败时 WGC 兜底增强；MIT 许可只读不抄）

- [DXGI-DDA-CAPTURE-20261123.md](DXGI-DDA-CAPTURE-20261123.md) —— **DXGI Desktop Duplication 抓屏后端接入降级链**（新增 Relay/DxgiDdaCapture.cs 339行实现 IScreenCapture，syncThreadDesktop+Hardware D3D11+DuplicateOutput+AcquireNextFrame；降级链 DDA→WGC→BitBlt，DDA优先因独占全屏+无撕裂+0.13ms；处理脏矩形静止复用/ACCESS_LOST重建/DPI物理尺寸/显存降级四边界；两TFM 0 error）

- [DXGI-DDA-RDP-20261123.md](DXGI-DDA-RDP-20261123.md) —— **DXGI Desktop Duplication RDP 可行性实测（推翻旧死路结论）**（A2 RDP SessionId=4 内 DuplicateOutput 成功 + AcquireNextFrame 出帧 18ms；根因是旧实测显存耗尽 E_OUTOFMEMORY 非 RDP 架构限制；syncThreadDesktop 必需、D3D11 Hardware 即可、单N卡无需 Optimus hook；DDA 可作独占全屏+无撕裂抓屏后端，接入方案 WGC→DXGI DDA→BitBlt）

- [MF-LOWLATENCY-20261123.md](MF-LOWLATENCY-20261123.md) —— **非N卡低延迟 ICodecAPI 码流实测**（手写 ICodecAPI 接入 MfFrameEncoder，SetOutputType 前设 AVLowLatencyMode/关B帧/CBR；码流级实锤微软软 MFT「SetValue S_OK 但完全忽略」，三模式 MD5 相同 B 帧仍 20；软编唯一有效手段是 MaxKeyframeSpacing=60；QSV/AMF 同构留待硬件；附 ffmpeg mfenc.c/qsvenc.c/amfenc 权威 file:line）

- [NVENC-SDK-QUALITY-PRESETS-20261123.md](NVENC-SDK-QUALITY-PRESETS-20261123.md) —— **画质自由档位**（NVENC preset P1~P7 下拉默认P3 + 码率/帧率滑动条(帧率上限120/码率1~50Mbps) + 关键帧缓冲上调0~200%对齐Sunshine vbv_percentage_increase；配置落AppSettings持久化+RelayClient下发环境变量；实测vbv 0%→100% IDR 33KB→58KB/QP33→31）

- [NVENC-SDK-RCPARAMS-20261123.md](NVENC-SDK-RCPARAMS-20261123.md) —— **NvencSdkEncoder v2 精确码控**（CBR+无限GOP+关B帧+zeroReorderDelay，GetEncodePresetConfigEx 取预设再覆盖 rcParams，codec union 不能碰否则 INVALID_PARAM；按需 IDR 走 FORCEIDR|OUTPUT_SPSPPS；A2 RDP 实测 IDR 251KB→33KB 降 7.4×；惰性/后台/降级不变）

- [NVENC-SDK-INTEGRATION-20261123.md](NVENC-SDK-INTEGRATION-20261123.md) —— **NvencSdkEncoder 接入主程序**（新增 Services/NvencSdkEncoder.cs 658 行 + IFrameH264Encoder 接口 + SessionRecorder 降级链 NVENC-SDK→MF硬编→软编；惰性加载 nvEncodeAPI64.dll 不进启动路径、动态加载不增体积、同步模式无事件泵；两 TFM 0 error；A2 RDP 会话 SessionId=4 直通复测出帧；v1 边界=encodeConfig=NULL 预设默认码率，精确 CBR/GOP 是下一步）

- [NVENC-SDK-PROBE-20261123.md](NVENC-SDK-PROBE-20261123.md) —— **NVENC SDK D3D11 直通探针实测**（独立探针 tools/nvenc-sdk-probe，0 warning 0 error；RTX 4060 驱动 621.01 走 REGISTER(DIRECTX,NV12)→Map→Encode 完整闭环出 3 帧真 H.264，条纹图案 1:1 保真；同步模式即可用无需事件泵；SDK 比 MF 干净无 COM 陷阱；已知瑕疵 GUID 枚举回读为 0 不影响直通）

- [MATURE-LATENCY-PLAYBOOK-20261123.md](MATURE-LATENCY-PLAYBOOK-20261123.md) —— **成熟串流项目延迟处理 Playbook**（Sunshine/Moonlight/OBS/Parsec/云游戏 在 采集节拍/编码参数/客户端解码丢帧/输入解耦/网络传输 五方面逐项拆解 + file:line 证据，映射成 SoulLink 具体改动项：P1=ICodecAPI低延迟+绝对时间节拍，P2=按需IDR，P3=帧边界元数据/网络升级缓做）

- [GPU-ZERO-COPY-20261123.md](GPU-ZERO-COPY-20261123.md) —— **GPU 截图→GPU 编码零拷贝路径调研**（当前是「CPU 读回 + CPU BGRA→NV12 + 系统内存 MF」，确认无疑；GPU 零拷贝唯一路线是 NVENC SDK D3D11 直通，本机 NVIDIA MF MFT 非 D3D11 使 MF 直通死路；附 Sunshine/OBS file:line 证据 + 三阶段改造路线收益/成本/风险）

## 视频流差距审计（2026-11-23 下午 · 对照成熟项目五线并行审计）

> 背景：用户反馈「效果差到无法使用」。五名成员（GLM-5.3 max / DS-V4-Pro xhigh）对照 Moonlight/Sunshine/Parsec/selkies/向日葵/云原神，对 453ms 延迟、低码率、触控不流畅、黑色闪烁、重连循环五线并行只读审计，全部行号级证据。团队记忆：decision/target-corrections（回环口径+安卓为重头戏）、decision/web-client-bar（回环须≥向日葵外网水准）、decision/p0-stability（稳定高于延迟）。

- [AGENT-PIPELINE-LATENCY-GAP-20261123.md](AGENT-PIPELINE-LATENCY-GAP-20261123.md) —— **Agent 管线 453ms 构成拆解**（三重架构税：H1 静止误判跳帧 ~150-300ms 最大嫌疑 + H2 定时轮询 16-33ms/帧 + H3 单线程串行 CPU 四趟拷贝 10-15ms/帧；码率低=5 处 clamp 钉 50Mbps + 帧供给不足实际 ≈2.5Mbps，编码器未触顶；关键发现 DxgiDdaCapture.cs:167 丢弃 FRAME_INFO（LastPresentTime）零成本权威变化信号可替代全帧 SequenceEqual；行动项 A1~A8 按 ROI 排序）
- [WEB-VIEWER-LATENCY-GAP-20261123.md](WEB-VIEWER-LATENCY-GAP-20261123.md) —— **网页播放链路审计**（P0.5 重连循环=确定性 bug 链：stallTimer ReferenceError(W1 修 10min) + 无限 GOP+decoder.reset 服务端不知情致 IDR 永不至→10s 看门狗断流成唯一重同步通道；热更断流根因=静止判定在编码器重建之前执行(W8)；453ms 回环归因=服务端节拍税 2/3 + 浏览器管线税 1/3；向日葵对照：PLI/帧边界/自适应三件事 HTTP/WS 同样能做，换 WebRTC 非第一步）
- [INPUT-CHANNEL-GAP-20261123.md](INPUT-CHANNEL-GAP-20261123.md) —— **输入链路审计**（「up 丢失」三根因：①down/up 独立 fetch 无序并发 ②sendInput 静默吞异常无重试 ③HTTP 断开不触发 releaseall；拖动卡=单 in-flight 等回包 WAN 下退化为 1/RTT Hz；双指触控单例覆盖 bug；行动项：FIFO 串行+pagehide 释放+代理看门狗+move noWait+Pointer Lock 相对位移）
- [WGC-BLACK-FLICKER-20261123.md](WGC-BLACK-FLICKER-20261123.md) —— **WGC 黑色闪烁根因**（7 嫌疑 5 立 2 排除：R1 主根因 stash 语义错误——AddRef 纹理指针≠帧对象持有，帧 Dispose=buffer 检回 DWM 复写，延迟读回读到黑帧/半黑帧；R2 无 Recreate/代数/IsIconic 致 resize 冻结；R3 >50% 黑帧过滤是闪烁放大器且误杀合法黑场景；修复 A~G 按 A(stash帧对象)→B(Recreate 状态机)→E(过滤降级，必须 A/B 后)排序；顺带更正 WGC-V2-RESEARCH L88 「stash 已对齐」误判）
- [MATURE-ARCH-FACTS-20261123.md](MATURE-ARCH-FACTS-20261123.md) —— **成熟项目延迟/码率/输入硬事实核查**（Moonlight FAQ 分段口径+「不可跨客户端比较」、Parsec 工程阈值编码/解码各<15ms@60fps；Moonlight 码率滑块上限 150Mbps、「800Mbps」查无出处；Sunshine video.cpp:893 zerolatency+CBR+无限GOP+按需IDR、input.cpp:2344 reset_input_state 断线释放；WebCodecs 低延迟参数全是 hint；对标口径：局域网端到端 <60ms）
- [MATURE-WEB-STACK-20261123.md](MATURE-WEB-STACK-20261123.md) —— **向日葵/ToDesk/UU/云游戏网页端技术栈核查 + 开源参照**（国内三大家闭源查无公开文档；selkies-gstreamer=游戏级浏览器串流完整开源答案：WebSocket+WebCodecs 同构我们现有路线、CBR8Mbps+无限GOP+按需keyframe+拥塞降档+优先级队列 pacer；结论 **不必跳 WebRTC**：现有 WebCodecs+按需IDR+8ms 合并已是 selkies 同构，真差距=输入/视频共 HttpListener 单循环+无拥塞降档+断连 releaseall 缺失；输入正解=Pointer Lock 相对位移接现有 lookAcc 8ms 合并 <0.5 人日；云原神一手证据 PWA+PointerLock+跨源 iframe；§9.1 dump 分析留待位）

## 视频流 / 远程画面（2026-11-22 主线）

- [P1-VERIFY-20261123.md](P1-VERIFY-20261123.md) ← **P1 架构改动功能验收（五条全过）**：A 单观众 /stream（h264/Main@5.0/2560x1368/**2.4MB**，此前仅 42KB）；B **双观众同时订阅**（各 **334 NAL / 3,164,743 B，比值 1.00**，ffprobe 各自独立识别为 h264）；C 录像 **NAL 直封 MP4 无 rame-*.jpg**（eplay.mp4 1444KB / 64 帧）；D 静态截图 275KB 彩色正常；E **WGC 帧池修复回归**（同场景 Y 均值 **0.0 → 247.1**、非黑 **0% → 99.9%**，证实根因判断成立）。附 P1 前后对比：句柄 **45398 → 807**、内存 1710→890MB、CPU 41~45%→33.4%
- [VERIFY-PITFALLS-20261123.md](VERIFY-PITFALLS-20261123.md) ← **验证陷阱清单（做验收前先读）**：作者自己踩过的 9 个坑——注释里的关键词不算代码 / 一个后端失败≠进程级问题（必须做对照）/ 自己的工具没传参数 / JSON 中文是 `\uXXXX` 转义导致 `Directory.Exists` 恒 false / `shot.jpg` 不等于残留 / 短采样窗口被 GC 锯齿骗 / 播放器缓冲污染端到端数字 / 构建产物取错 `win-x64` 目录 / 固定时长上限伪装成故障
- [BLACKSCREEN-ROOTCAUSE-20261123.md](BLACKSCREEN-ROOTCAUSE-20261123.md) ← **画面全黑根因判定**（含对作者此前错误结论的更正）：三组对照实验证明**全黑只发生在 WGC 后端**、BitBlt 完全正常；根因是 `WgcCapture` 用 `GetForegroundWindow()` 抓**单个窗口**却用 `GetSystemMetrics()` 的**整屏**尺寸建帧池（实测 帧池 2560x1368 vs 窗口 1721x927），且代码从未读 `item.Size`；**随前台窗口大小漂移故偶发**——这解释了「我实测全黑 / stream-core 在 A1 实测彩色」两者都对
- [ZERO-JPEG-VERIFY-20261123.md](ZERO-JPEG-VERIFY-20261123.md) ← **H.264 原生链路（零 JPEG）验收**：代码层检查工具 `check-no-jpeg.ps1` 实测——**网页侧 4 项已达成**（toDataURL/退回 JPEG 已移除且只剩注释、WebPages.cs 已同步），**服务端 5 项未达成**（Ring 仍存 `byte[] Jpeg`、`/stream` 仍每帧 `DecodeJpegToNv12`、`/live` 仍返 JPEG）→ P1 尚未开工；录像 ✅ 无 `frame-*.jpg` 只剩 `replay.mp4`（ffprobe: h264/50帧）；静态截图 ✅ `shot.jpg` 54KB 仍可用；采集侧 CPU **实测 41~45% 单核**（作为改前基线）
- [STREAM-VERIFY-20261123.md](STREAM-VERIFY-20261123.md) ← **`/stream` 端到端验收报告**：两个 TFM 均 0 error；ffprobe 实测 h264/Main@5.0/2560x1368/25fps 且 ffmpeg 与 WebCodecs 都解码成功；缺 DLL 已给出中文 500 + 日志堆栈（此前的连接重置已修好）；无头 Edge 实测 WebCodecs **解出 13 帧并上屏**、SPS 动态解析出 `avc1.4D4032`；**发现 P0 缺陷：画面全黑，根因为 `RelayAgent.cs:523` 的 `SetProcessDPIAware()` 使 `GetSystemMetrics` 返回缩放尺寸 2560x1368 而真实合成尺寸是 1707x960**（含对照实验证据链与 4 条修复建议）；附最短手动验收 3 步
- [MEMORY-LEAK-VERIFY-20261123.md](MEMORY-LEAK-VERIFY-20261123.md) ← **内存泄漏验证基座 + 修复后回归清单**（`tools/mem-probe` 量具 / 含 `--selftest` 6/6 证判据可区分真假泄漏 / 实测「`using var` 修复后仍泄漏 46.3MB/s R²=0.998」/ 新发现句柄漏 1454 个/秒 / 24 项回归清单含 401、颜色、错误可诊断）
- [LATENCY-BENCHMARK-20261123.md](LATENCY-BENCHMARK-20261123.md) ← **延迟归因与业界对标**：把「卡」拆成抓屏/编码/传输/解码/上屏逐段（实测采集 12.14fps、推流 13.56fps，串行双节拍 ≈157ms）；7 个业界参照物及可信度分级；指出瓶颈是「15fps 硬编码 + 软编优先 + 无帧间去重」而非传输（实测 0.8ms）；5 条落地建议含收益/成本/风险；专项评估 `optimizeForLatency` / `AVLowLatencyMode` / 「不攒帧直接推」
- [HANDOFF-20261123-LATENCY.md](HANDOFF-20261123-LATENCY.md)
- [HANDOFF-VIDEO-LATENCY-DONE-20261123.md](HANDOFF-VIDEO-LATENCY-DONE-20261123.md) ← **视频流延迟/撕裂攻坚收官交接**（4s→453ms、DXGI DDA 根治撕裂、NVENC 直通；代码已推送 3.3.33 但未部署 A2，下一步第一件事=部署） <- **最新交接（上午）**：3.3.20 全部修复已落地，但端到端延迟 4 秒；根因三层（软编优先/NVENC 闲置、每秒 95KB IDR、解码积压不丢帧）；含 NVENC 3 个时序 bug、四个架构要求、ICodecAPI 手写要点、验证方法
- [HANDOFF-STREAM-STATUS-20261123.md](HANDOFF-STREAM-STATUS-20261123.md) ← **最新交接（修正上一份）**：服务端链路跑通但 `/stream` 在真实 Agent 里不可用；含端口分配实测（`PortFor` 有 +1）、两套鉴权、5 个环境坑、下一步 P0~P3
- [HANDOFF-VIDEO-20261122.md](HANDOFF-VIDEO-20261122.md) —— 昨晚交接（**`/stream` 可用的结论已被修正**，其余数据仍有效）
- [STREAM-LATENCY-TEST-20261122.md](STREAM-LATENCY-TEST-20261122.md) —— **延迟实测**（软编 1384ms vs 硬编 636~742ms，各阶段耗时分解）
- [WGC-SNAPSHOT-20261122.md](WGC-SNAPSHOT-20261122.md) —— **WGC 非黑帧实证**（A1 抓原神 1920x1080 非黑 100%，含截图）
- [WGC-TFM-VERIFY-20261122.md](WGC-TFM-VERIFY-20261122.md) —— **TFM 多目标方案**（55 个 tools 零改动，publish 加 -f）
- [WGC-A1-DYNAMIC-20261122.md](WGC-A1-DYNAMIC-20261122.md) —— WGC 帧率实测（31.7fps = 原神锁帧，抓窗口比抓屏快 2.6 倍）
- [WGC-VERIFY-20261122.md](WGC-VERIFY-20261122.md) —— WGC 在 RDP 可用（身份是关键）
- [VIDEO-STREAM-RESEARCH-20261122.md](VIDEO-STREAM-RESEARCH-20261122.md) —— 视频流方案对比（MF 软编 mfh264enc 系统自带 Win7+）
- [NVENC-DESIGN-20261122.md](NVENC-DESIGN-20261122.md) —— 硬件编码设计（多厂商抽象层）
- [STREAM-GAP-20261122.md](STREAM-GAP-20261122.md) —— **为什么串流软件能 60fps**（BitBlt 是 2006 年的 GDI）
- [ANDROID-APP-DESIGN-20261122.md](ANDROID-APP-DESIGN-20261122.md) —— **安卓 APP 设计**（Kotlin + SurfaceView + MediaCodec）

## 性能与架构

- [SETTINGS-PAGE-LAG-20261123.md](SETTINGS-PAGE-LAG-20261123.md) —— **进入「应用设置」页卡顿根因归因（按贡献度排序）**（`Activate()` UI 线程串行跑 1×powershell `Get-MpPreference` + 6×sc.exe + 2×schtasks + 3×递归扫目录 + 旧目录枚举；P0=Defender 排除查询/服务状态查询，P1=目录占用统计/迁移 Inspect，P2=自启状态假 async；含 file:line 证据 + 量级估算 + 后台化修复建议）
- [DATA-CENTER-SINGLE-SOURCE-20261123.md](DATA-CENTER-SINGLE-SOURCE-20261123.md) ← **数据中心唯一入口决策 + 数据库化评估**（唯一入口立即生效 / SQLite 暂缓）
- [DATA-CENTER-AUDIT-20261123.md](DATA-CENTER-AUDIT-20261123.md) —— **数据中心旁路全仓审计**（日志直读 / 进程枚举 / 递归目录 / FileSystemWatcher 全调用点；旁路与独立功能分级，设置页与配置编辑器 UI IO 优先整改）
- [UI-PERF-IO-TRIAGE-20261123.md](UI-PERF-IO-TRIAGE-20261123.md) —— **UI 线程减负**（TickHub 后台订阅 RegisterBackground + 4 订阅者迁后台；RdpConsole 5 个 DispatcherTimer 全收编/事件驱动；ConfigStore.Save 改后台串行队列保序原子写；94 处同步 IO 分类清单）
- [PERF-3.3.4-20261122.md](PERF-3.3.4-20261122.md) —— **性能基线**（16% 单核的构成分解 + 三个卡顿根因）
- [LOOK-OPTIMIZE-20261122.md](LOOK-OPTIMIZE-20261122.md) —— **转视角链路优化 + 手机端贯通**（去 Thread.Sleep(6) + moverel 单向 NoReply + 8ms 节流 + 触屏滑动手势状态机）
- [WEB-DIRECT-3.3.5-20261122.md](WEB-DIRECT-3.3.5-20261122.md) —— **网页直连代理**（端口按账号分配 / 端口独占实测 / 路径穿越防护）
- [REVIEW-20261122.md](REVIEW-20261122.md) —— **三方案对抗性评审**（唯一 P0/P1/P2 顺序、量化复算、风险与遗漏项）
- [WEB-EXTENSION-PLAN-20261122.md](WEB-EXTENSION-PLAN-20261122.md) —— 网页 BGI 数据与自动化流程扩展（按需 API / 双端乐观锁 / 安全前置）
- [STREAM-PERF-AUDIT-20261122.md](STREAM-PERF-AUDIT-20261122.md) —— **画面链路性能根因审计 + 最小成本远程控制内核**（逐跳数据流 / CPU 五分解 / 7 方案量化 / 推荐架构 + 分阶段路线）
- [DASHBOARD-DESIGN-20261122.md](DASHBOARD-DESIGN-20261122.md) —— **主页/控制面板信息架构 + myorg 模块取舍**（模块取舍表 / 账号卡片字段排序 / 快捷按钮 / CPU内存开销结论 / 网页与APP分工 / 应砍清单）
- [SESSION-SLIM-PAGE-DESIGN-20261122.md](SESSION-SLIM-PAGE-DESIGN-20261122.md) —— **会话瘦身说明页设计**（两区块拆分 / 每个优化项的大白话四行说明 / 三个独立开关 / 全机设置警告色默认关）
- [DUAL-RATE-DESIGN-20261122.md](DUAL-RATE-DESIGN-20261122.md) —— **录制/直播双速率解耦设计**（2fps 录像 + 60fps 直播，共享单次抓屏，file:line 改动清单 + 分步施工）
- [STREAM-PLAN-V2-20261122.md](STREAM-PLAN-V2-20261122.md) —— **RDP 实测后的远程画面方案**（帧率上界 / 交互解耦 / 外网码率 / RDP 服务端能力边界）
- [NVENC-DESIGN-20261122.md](NVENC-DESIGN-20261122.md) —— **NVENC 硬编替换 JPEG**（RDP 内可用性实测：NVENC 可用但抓屏 31ms 才是瓶颈；多厂商 IVideoEncoder 抽象层 + 选型 MF MFT 不选 SDK + 4060 Laptop 12 路并发查证 + 回退链 + AMD/Intel 备用记录）
- [NVENC-IMPLEMENTATION-20261123.md](NVENC-IMPLEMENTATION-20261123.md) —— **NVENC 硬编正确实现**（异步 MFT 完整时序 / ProcessOutput 返回码 / 事件驱动 vs 轮询 / MF 软编压 IDR 可行性 / Vortice 绑定缺口 ICodecAPI / 最小改动清单）
- [ICODECAPI-PROBE-20261123.md](ICODECAPI-PROBE-20261123.md) —— **手写 ICodecAPI 独立探针实测**（SetValue/GetValue 槽位与 VT_BOOL/VT_UI4；微软软编写入 S_OK 但读回 E_NOTIMPL，不能宣称生效）
- [MOBILE-REMOTE-PLAN-20261122.md](MOBILE-REMOTE-PLAN-20261122.md) —— **手机端远程控制方案**（PWA→壳套 APP 路线 / 手势映射 / 外网穿透与安全边界 / 里程碑）
- [MOBILE-CONTROLS-DESIGN-20261122.md](MOBILE-CONTROLS-DESIGN-20261122.md) —— **手机端控制层设计**（虚拟摇杆数学与事件序列 / 可编辑键位布局 JSON / 中文输入上屏时序 / 断线释放协议）
- [ANDROID-APP-DESIGN-20261122.md](ANDROID-APP-DESIGN-20261122.md) —— **安卓 APP 协议设计与工程脚手架**（Kotlin + 传统 View/SurfaceView + Compose 混合 / ExoPlayer vs MediaCodec 取舍 / 协议时序 / 目录树 / 类骨架 / 分阶段）
- [VIRTUAL-CONTROLS-RESEARCH-20261122.md](VIRTUAL-CONTROLS-RESEARCH-20261122.md) —— **虚拟按键/摇杆/键位映射调研**（UU 远程公开资料 / Moonlight OSC 详细拆解 / 开源项目对比表 / 手机端转视角方案对比 / 键位映射 JSON Schema v2 / 许可风险）
- [REMOTE-ACCESS-OPTIONS-20261123.md](REMOTE-ACCESS-OPTIONS-20261123.md) —— **远程访问「更优方案」横向对比**（PWA / 开源串流复用 / WebRTC / 原生壳 / 第三方兜底 五路线；WebCodecs 移动端支持度实测 + Moonlight 协议不匹配结论；推荐排序 + 逐条痛点打分）
- [MIYOUSHE-RESEARCH-20261122.md](MIYOUSHE-RESEARCH-20261122.md) —— **米游社扫码/短信登录与收益数据获取调研**（开源项目清单 / 扫码+短信登录完整流程含极验 / 收益接口清单 / C# 集成方案含 DS 签名 / 风险合规）
- [SYSTEM-OPTIMIZE-20261122.md](SYSTEM-OPTIMIZE-20261122.md) —— **系统优化：可关服务白名单 + Defender 目录排除**（6 项服务白名单 / 仅本次停止不改启动类型 / Defender 排除增删 / 风险边界）
- [OSS-REMOTE-RESEARCH-20261122.md](OSS-REMOTE-RESEARCH-20261122.md) —— **开源远程串流项目调研**（Sunshine/Moonlight/Parsec 替代/候选项目对比 / 转视角实现方案对比 / WGC vs DXGI vs BitBlt 在 RDP 下的可用性 / 可借鉴代码 file:line / 许可风险 / 给 SoulLink 的 3 条建议）
- [GITHUB-SWEEP-20261122.md](GITHUB-SWEEP-20261122.md) —— **GitHub 地毯式搜索 60 项目清单**（远程/串流/抓屏/虚拟显示器/输入注入 / WGC 与虚拟显示器在 RDP 会话的 issue 级证据 / 最值得看的 5 个项目 / 已证实·传闻·未验证标注）
- [VDD-RESEARCH-20261122.md](VDD-RESEARCH-20261122.md) —— **虚拟显示器（IddCx）方案深度研究**（parsec-vdd 原理/源码/许可/安装 / 同类方案对比表 / RDP 会话用 RDPDD 而非 IddCx 的关键修正 / 副作用 / 换本地会话方向评估 / 给 SoulLink 的 3 条建议）
- [VIDEO-STREAM-RESEARCH-20261122.md](VIDEO-STREAM-RESEARCH-20261122.md) —— **视频流方案调研**（MF vs 小型库 vs 精简 ffmpeg 对比 / MF 可用性矩阵与检测逻辑 / 原生安卓 APP 的 MediaCodec 拉流方案 / MSE vs WebCodecs 支持矩阵 / 最终推荐与分阶段路线）
- [CN-CLOUD-GAMING-20261122.md](CN-CLOUD-GAMING-20261122.md) —— **国内云游戏/云电脑调研**（UU/顺网/达龙/START/云原神/ToDesk·向日葵·RayLink/华为·阿里 对比表 / 多开路线 A(云厂商GPU虚拟化) vs B(远程串流) 架构分析 / 与 SoulLink RDP 差异 / 可借鉴做法 / UU 手机虚拟按键产品级设计参考）
- [CITATION-AUDIT-20261122.md](CITATION-AUDIT-20261122.md) —— 上述三份文档的 file:line 引用全量核对（107 条，0 WRONG / 0 MISSING）
- [PERF-MEASURE-20261122.md](PERF-MEASURE-20261122.md) —— **性能实测方法**（`scripts/measure-soullink-perf.ps1` 用法 + 三个关键数字的验证步骤）
- [ROUTE-LEDGER-DESIGN-20261122.md](ROUTE-LEDGER-DESIGN-20261122.md) —— **配置组/路线级收益与报错台账**（归因规则 / JSONL 落盘 / 读写契约 / 查询能力 / 磁盘估算，最小可用版已实现）
- [SESSION-ARCH-20261122.md](SESSION-ARCH-20261122.md) —— **会话架构路线评估**（RDP / 虚拟显示器 / 本地多开 / 虚拟机 四条路线对比 + 本机实测环境 + 本地多开否决 + VM 真实开销「NVIDIA 笔记本 GPU 被 GPU-PV 排除」+ 推荐路径 A + 给用户的直白解释）
- [LOBBY-OPTIMIZE-20261122.md](LOBBY-OPTIMIZE-20261122.md) —— **联机大厅按需访问 + 发车人数 2-4 可配**（改前/改后流量量化对比 / 轮询 vs 长连接取舍 / SQLite schema 迁移 / 改动清单）

## 交接与状态文档

- [HANDOFF-3.3.19-20261122.md](HANDOFF-3.3.19-20261122.md) ← **最新**（网页直连代理 + 按需取数 + 性能修复）
- [HANDOFF-3.3.3-20261122.md](HANDOFF-3.3.3-20261122.md)（屏幕录制 + 网页远程 + 性能修复）
- [HANDOFF-2.17.45-20261121.md](HANDOFF-2.17.45-20261121.md)
- [HANDOFF-FAILURE-2.13-20261119.md](HANDOFF-FAILURE-2.13-20261119.md)
- [HANDOFF.archive-2026-11-15.md](HANDOFF.archive-2026-11-15.md)
- [HANDOFF.md](HANDOFF.md)
- [HANDOVER-AUTOMATION.md](HANDOVER-AUTOMATION.md)
- [HANDOVER-BROKER.md](HANDOVER-BROKER.md)
- [HANDOVER-GUIDE.md](HANDOVER-GUIDE.md)
- [HANDOVER.md](HANDOVER.md)
- [NEW_SESSION_PROMPT.md](NEW_SESSION_PROMPT.md)

## 变更记录与版本规范

- [CHANGELOG-2.10.0.md](CHANGELOG-2.10.0.md)
- [CHANGELOG-2.10.1.md](CHANGELOG-2.10.1.md)
- [CHANGELOG-2.10.2.md](CHANGELOG-2.10.2.md)
- [CHANGELOG-2.11.0.md](CHANGELOG-2.11.0.md)
- [CHANGELOG-2.17.45.md](CHANGELOG-2.17.45.md)
- [CHANGELOG-2.3.0.md](CHANGELOG-2.3.0.md)
- [CHANGELOG-2.6.2.md](CHANGELOG-2.6.2.md)
- [CHANGELOG-2.8.8.md](CHANGELOG-2.8.8.md)
- [CHANGELOG-2.8.9.md](CHANGELOG-2.8.9.md)
- [CHANGELOG-2.9.0.md](CHANGELOG-2.9.0.md)
- [CHANGELOG-2.9.1.md](CHANGELOG-2.9.1.md)
- [VERSIONING.md](VERSIONING.md)

## 服务端部署与运维

- [LOBBY-DEPLOY-20261122.md](LOBBY-DEPLOY-20261122.md) —— **新服务器（42.192.114.250）部署方案与流量监控**（自包含交叉编译可行性 / 部署步骤 / 腾讯云安全组 / 流量监控命令 / 回滚 / HTTPS 三选项）
- [LOBBY-DEPLOY-2.1.0-20261122.md](LOBBY-DEPLOY-2.1.0-20261122.md) —— **服务端 2.1.0 部署记录**（实际部署步骤 / schema 迁移验证 / 发车人数验证 / 回滚方法）

## 其它

- [BROKER-TESTING.md](BROKER-TESTING.md)
- [PERF-BRANCH-PLAN.md](PERF-BRANCH-PLAN.md)

---

## 说明

这些是开发过程中的研究记录、故障复盘与交接文档，**不是当前代码的说明**。
当前有效的规则见项目根的 `AGENTS.md` 与 `docs/RELEASE-RULES.md`。
- `HOT-RELOAD-E2E-VERIFY-20261123.md` 视频参数热更新 + 生命周期端到端验收（码率/帧率热更新生效，preset/vbv 缺陷）
- `DEPLOY-3.3.25-VERIFY-20261123.md` 3.3.25 本地部署 + 真机端到端验收（Broker 重启 + A2 换新代码 + NVENC SDK 出帧 + preset/vbv 热更新修复）
- `DEPLOY-3.3.27-VERIFY-20261123.md` 3.3.27 本地部署 + WGC 不撕裂验收（探测式抓屏 WGC 优先 + BitBlt 兜底）
- `DEPLOY-3.3.29-VERIFY-20261123.md` 3.3.29 部署 + 主程序重启 + DDA 生效验收（监控修复生效，DDA 首次出帧无撕裂但二次启动失败缺陷）
- `DEPLOY-3.3.30-VERIFY-20261123.md` 3.3.30 部署 + 二次订阅修复闭环验收（DDA 二次启动失败已修复，三次订阅均出帧无撕裂）
- `DEPLOY-3.3.31-VERIFY-20261123.md` 3.3.31 部署 + 前端三修复验收（热切重连/拉流自愈/顶栏收敛已部署，A2 出帧卡顿待查）
- `DEPLOY-3.3.33-VERIFY-20261123.md` 3.3.33 部署 + 一直连接中修复验收（IDR死锁/探测锁定/活性探针/前端看门狗闭环通过）
