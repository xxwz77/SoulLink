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

## 服务器地址与发布

- [FIX-20261124-AGENT-AUTO-SYNC.md](FIX-20261124-AGENT-AUTO-SYNC.md) —— **注入代理版本自检 + 自动替换 + 引擎 5 态显示**（3.3.39：新增 `Services/AgentSync.cs`，启动自检安装目录 agent 版本、不一致时按精确 PID 停进程后自动复制修复、版本一致零写入；MainWindow 5 态如实显示，禁止 agent 版本不匹配时显示「引擎就绪」）
- [VERIFY-20261124-T8-AGENT-SPLIT.md](VERIFY-20261124-T8-AGENT-SPLIT.md) —— **t8 回归复验：拆出独立 Agent 工程后 P0 再验（2026-11-24，verifier）**：build.bat 0 error，Agent.exe 拆成独立无 UI 单文件 2.70MB（主程序 4.47MB 不变）、deps.json 零 PresentationFramework/PresentationCore/WindowsBase/System.Windows.Forms；VersionFileFor 链不断、判据基线=required.version(1.0.0)非服务版本/非 FileVersion 回退（三条路径实测）；真实投送 AGENT_STARTED 且拉起的代理确为 2.7MB/1.0.0.0、AGENT_INVALID 0 次；Broker 拆分未落地归 t16/T7b（服务仍 SoulLink.exe --broker）
- [VERIFY-20261124-P0-FIXED.md](VERIFY-20261124-P0-FIXED.md) —— **P0 三修复独立验证（2026-11-24，verifier）**：dotnet build 0 error（双 TFM + 红线 0 违规）、build.bat 产物落位（dist 与 artifacts\dist-3.3.40 均有 3.3.40 三件套）、判据自检两条路径实测（双方有标识→true / agent 缺标识→false，探针引编译产物）、静态证据齐全（XAML 中性默认值 / TickHub 周期复检 / 分类器 + 双恢复入口 / _rebuildAttempt 4 归零点）、部署实测 C:\Program Files\SoulLinkDev 3.3.40 ValidAgent=true；未停 A1/A4、未碰 stable、未改注册表
- [HANDOFF-20261124-40-P0-FIXED.md](HANDOFF-20261124-40-P0-FIXED.md) ← **P0 三修复收官交接（2026-11-24）**：投送全线失败 / 界面状态造假 / 不可恢复错误无脑重试三根因 + 修法（file:line）+ 验证结论（构建 0 error / 判据自检 / 真实投送 AGENT_STARTED / 正确部署后 AGENT_INVALID 0 次）；版本 3.3.40 / AgentVersion 1.0.0 / git HEAD 588c007 / 未提交 77 文件；可复跑部署+自检命令；仍待做（Broker 拆分 t16、断言 runner、copy 失败断言）+ 冻结项；十条铁律。


- [REFACTOR-20261124-SERVER-URL-EXTERNALIZED.md](REFACTOR-20261124-SERVER-URL-EXTERNALIZED.md) —— **服务器地址外部化**（3.3.36：联机大厅/路线更新/客户端更新三处归一为单一 `ServerBaseUrl` 设置，新增 `Services/ServerEndpoints.cs`，源码 0 域名命中，老配置自动迁移）

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
- [FIRST-FRAME-25S-DIAGNOSIS-20261124.md](FIRST-FRAME-25S-DIAGNOSIS-20261124.md) —— **首帧 25 秒超时根因**（静止起播 IDR 死锁未根治：`_forceKeyFrame` 只保首帧一帧，MF 编码器需连续多帧才吐 IDR，静止去重第二帧起切断输入 → Ring 永不产帧 → 25s 上限 503；最小修复=编码失败且 forceKeyFrame 时重新置位直到出 IDR；附完整链路 file:line 证据）
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
- [DDA-TEXTURE-SHARED-HANDLE-20261124.md](DDA-TEXTURE-SHARED-HANDLE-20261124.md) —— **DDA 纹理跨设备共享句柄可行性**（DDA surface 非 SHARED 创建不可直接 GetSharedHandle 跨设备；跨设备须 CopyResource 到 SHARED_NTHANDLE|SHARED_KEYEDMUTEX 纹理再 CreateSharedHandle+OpenSharedResource1+KeyedMutex；正确时序=先 CopyResource 再 ReleaseFrame。**其「Sunshine DDA 同 device」结论已被下条源码深读纠正**）
- [DEEP-SUNSHINE-SOURCE-20261124.md](DEEP-SUNSHINE-SOURCE-20261124.md) —— **Sunshine GPU 直通与帧节拍源码深读**（纠正「DDA/NVENC 同 device」：实际双 device + NT handle + keyed mutex；PS 两 pass 写 NV12、无 VideoProcessor；像素无 CPU 回读但 bitstream 会复制到 CPU；DDA 光标与 cursor-only 帧、WGC 生产者/消费者、SetMaximumFrameLatency 所在设备、pacing group；末尾给 SoulLink 函数级改动清单与收益边界）
- [RDP-FRAMERATE-BENCHMARK-20261124.md](RDP-FRAMERATE-BENCHMARK-20261124.md) —— **A2 RDP 真实帧率与 DDA 争用实测**（session 4 输出 Intel 1920×1080 66Hz，无 agent 争用时 60/120fps 视频与默认/软件 WPF 均≈66fps；有 agent 时历史场景≈32fps；当前 60fps CPU 管线长样本 48.59fps。区分 Acquire/AccumulatedFrames/编码 total，明确 RDP 非 14/32fps 硬上限，当前主要瓶颈在 agent 数据路径/调度；含无效样本披露与复现边界）
- [MULTI-VIEWER-DDA-CONTENTION-20261124.md](MULTI-VIEWER-DDA-CONTENTION-20261124.md) —— **多观看者 DDA 争用诊断（主动停止前的完整阶段报告）**（SoulLink 已共享单 Capture/单编码器；独立 DDA 1/2/3 客户端均约66fps，无客户端数导致腰斩；CPU 转换使 Acquire 降至62.91fps；共享1→2 viewer 仍约60fps且 queueDrops=0；三 viewer 真实光测延迟受并发端口争用未完成，明确标注不得用 queueAge 冒充总延迟）

## 视频流差距审计（2026-11-23 下午 · 对照成熟项目五线并行审计）

- [AUDIT-20261124-06-PROTOCOL-ANDROID.md](AUDIT-20261124-06-PROTOCOL-ANDROID.md) —— **协议层与 Android 端链路审计**（SoulLink=三条互不相通通道拼装：管道 JSON 行控制+HTTP chunked 裸 H.264+主程序信令；视频无帧边界元数据/无流内时间戳/AU 时间戳是客户端自造 33_333µs 步进；输入=每事件独立 HTTP POST 无序号（全行业无此设计），代理 click 自带 Sleep(18/90) 固定地板；/api/input 的 sid 字段被 InputDto 静默丢弃；Android 解码三缺陷=无 KEY_MAX_INPUT_SIZE 大 IDR 整帧丢弃+双 10ms 轮询空转+无帧 pacing；建议 P0=最小帧信封+输入批 POST+解码三微修，P1=客户端 PLI，换 UDP/WebRTC 现阶段不做）

> 背景：用户反馈「效果差到无法使用」。五名成员（GLM-5.3 max / DS-V4-Pro xhigh）对照 Moonlight/Sunshine/Parsec/selkies/向日葵/云原神，对 453ms 延迟、低码率、触控不流畅、黑色闪烁、重连循环五线并行只读审计，全部行号级证据。团队记忆：decision/target-corrections（回环口径+安卓为重头戏）、decision/web-client-bar（回环须≥向日葵外网水准）、decision/p0-stability（稳定高于延迟）。

- [AGENT-PIPELINE-LATENCY-GAP-20261123.md](AGENT-PIPELINE-LATENCY-GAP-20261123.md) —— **Agent 管线 453ms 构成拆解**（三重架构税：H1 静止误判跳帧 ~150-300ms 最大嫌疑 + H2 定时轮询 16-33ms/帧 + H3 单线程串行 CPU 四趟拷贝 10-15ms/帧；码率低=5 处 clamp 钉 50Mbps + 帧供给不足实际 ≈2.5Mbps，编码器未触顶；关键发现 DxgiDdaCapture.cs:167 丢弃 FRAME_INFO（LastPresentTime）零成本权威变化信号可替代全帧 SequenceEqual；行动项 A1~A8 按 ROI 排序）
- [WEB-VIEWER-LATENCY-GAP-20261123.md](WEB-VIEWER-LATENCY-GAP-20261123.md) —— **网页播放链路审计**（P0.5 重连循环=确定性 bug 链：stallTimer ReferenceError(W1 修 10min) + 无限 GOP+decoder.reset 服务端不知情致 IDR 永不至→10s 看门狗断流成唯一重同步通道；热更断流根因=静止判定在编码器重建之前执行(W8)；453ms 回环归因=服务端节拍税 2/3 + 浏览器管线税 1/3；向日葵对照：PLI/帧边界/自适应三件事 HTTP/WS 同样能做，换 WebRTC 非第一步）
- [INPUT-CHANNEL-GAP-20261123.md](INPUT-CHANNEL-GAP-20261123.md) —— **输入链路审计**（「up 丢失」三根因：①down/up 独立 fetch 无序并发 ②sendInput 静默吞异常无重试 ③HTTP 断开不触发 releaseall；拖动卡=单 in-flight 等回包 WAN 下退化为 1/RTT Hz；双指触控单例覆盖 bug；行动项：FIFO 串行+pagehide 释放+代理看门狗+move noWait+Pointer Lock 相对位移）
- [WGC-BLACK-FLICKER-20261123.md](WGC-BLACK-FLICKER-20261123.md) —— **WGC 黑色闪烁根因**（7 嫌疑 5 立 2 排除：R1 主根因 stash 语义错误——AddRef 纹理指针≠帧对象持有，帧 Dispose=buffer 检回 DWM 复写，延迟读回读到黑帧/半黑帧；R2 无 Recreate/代数/IsIconic 致 resize 冻结；R3 >50% 黑帧过滤是闪烁放大器且误杀合法黑场景；修复 A~G 按 A(stash帧对象)→B(Recreate 状态机)→E(过滤降级，必须 A/B 后)排序；顺带更正 WGC-V2-RESEARCH L88 「stash 已对齐」误判）
- [MATURE-ARCH-FACTS-20261123.md](MATURE-ARCH-FACTS-20261123.md) —— **成熟项目延迟/码率/输入硬事实核查**（Moonlight FAQ 分段口径+「不可跨客户端比较」、Parsec 工程阈值编码/解码各<15ms@60fps；Moonlight 码率滑块上限 150Mbps、「800Mbps」查无出处；Sunshine video.cpp:893 zerolatency+CBR+无限GOP+按需IDR、input.cpp:2344 reset_input_state 断线释放；WebCodecs 低延迟参数全是 hint；对标口径：局域网端到端 <60ms）
- [WEB-RUNTIME-PROBE-20261123.md](WEB-RUNTIME-PROBE-20261123.md) —— **云原神运行时实测（CDP 抓证）**（实测推翻静态推断：实际跑 WebRTC——RTCPeerConnection connected/WT 0 实例；1080p60 H264 12.5Mbps/RTT 7ms/UDP 直连；渲染 video.srcObject=MediaStream 非canvas；jitterBuffer≈73.8ms/帧+14.2s 内 15 次 freeze=商业产品也会卡；输入=同 PC DataChannel+独立 wss 信令自研二进制帧；NACK/PLI/FIR 全启用+pli 实发=W7 前端要关键帧的业界实锤；附可复用 scripts/cdp-probe.ps1 八探针组）
- [MATURE-WEB-STACK-20261123.md](MATURE-WEB-STACK-20261123.md) —— **向日葵/ToDesk/UU/云游戏网页端技术栈核查 + 开源参照**（国内三大家闭源查无公开文档；selkies-gstreamer=游戏级浏览器串流完整开源答案：WebSocket+WebCodecs 同构我们现有路线、CBR8Mbps+无限GOP+按需keyframe+拥塞降档+优先级队列 pacer；结论 **不必跳 WebRTC**：现有 WebCodecs+按需IDR+8ms 合并已是 selkies 同构，真差距=输入/视频共 HttpListener 单循环+无拥塞降档+断连 releaseall 缺失；输入正解=Pointer Lock 相对位移接现有 lookAcc 8ms 合并 <0.5 人日；云原神一手证据 PWA+PointerLock+跨源 iframe；§9.1 dump 分析留待位）
- [AUDIT-20261124-01-SUNSHINE-CHAIN.md](AUDIT-20261124-01-SUNSHINE-CHAIN.md) —— **Sunshine→Moonlight 全链路 20 跳逐跳拆解 + Parsec 对照**（每跳「做什么/API/量级/如何被优化掉」+ SoulLink 现状 file:line；444ms 按层归因=服务端节拍税+CPU读回为主、传输/浏览器为次；P0 预判：零拷贝卡点在 RDP 内无 ID3D11VideoDevice → 正解是 Sunshine 同构「路线A 同 device+PS 着色器」而非修 VideoProcessor；光标正解=画进视频帧+光标动即推帧（frameInfo.PointerShapeInfo 已到手未用）；可抄 8 条/抄不了 6 条（UDP+FEC、RFI、浏览器 vsync pacing 明确不做））
- [AUDIT-20261124-02-INPUT-CHAIN-MATURE.md](AUDIT-20261124-02-INPUT-CHAIN-MATURE.md) —— **成熟产品鼠标输入全链路对照**（Moonlight/noVNC/Guacamole/RustDesk 逐跳报文+标志位+坐标编码+宿主注入；绝对 vs 相对模型；帧内光标 vs 独立 cursor 通道；坐实 AgentWebServer 缺 releaseall、按钮 FIFO 与 move/moverel 分离无全链保序、mouse_event 无成功反馈、蓝球非宿主光标；P0~P2 修正建议）
- [AUDIT-20261124-05-WEB-CLIENT-GAP.md](AUDIT-20261124-05-WEB-CLIENT-GAP.md) ← **Web 观看端浏览器侧管线审计 + 系统性缺陷排查（2026-11-24）**（纠正前置事实：无 WebSocket/MediaSource，实际是 fetch+WebCodecs+2D canvas 直绘；判定浏览器侧非 444ms 主因=结构证据「无 444ms 级缓冲，最大 ~82ms」，主因在服务端 GPU 零拷贝未生效 DDA 走 CPU 读回；12 条缺陷含 drawImage GPU 回读/ts+=66666 假 15fps/fetch 无 AbortController 僵尸连接；5 类隐患含后台节流/常驻定时器/伪重连风暴/错误静默吞掉/无帧龄埋点；定位「蓝色圆球」=#plCursor 供 cursor-ux 接手）
- [BROWSER-UDP-FEASIBILITY-20261124.md](BROWSER-UDP-FEASIBILITY-20261124.md) ← **浏览器 UDP 可行性权威研究（2026-11-24）**（回答「浏览器不能 UDP 吗」：能，但只有 WebTransport(QUIC/UDP)/WebRTC DataChannel(SCTP/UDP)/WebSocket(TCP) 三条合规封装，裸 UDP 不可用；WebTransport 强制 HTTPS+有效证书（SoulLink 现状是 http:// 明文 = 硬门槛）、Baseline 2026 支持率 92.37% 旧浏览器/QQ/UC 不支持；量化结论=实测传输 0.8~5ms 丢包≈0 前提下换 UDP 收益≈0~2ms，远小于服务端 convertMs 7.8ms/浏览器 20~40ms 真瓶颈；决策树=RTT>20ms 且丢包>0.5% 才有收益；建议=不换，先做「帧边界语义+客户端按需 IDR+输入 WebSocket」三件 TCP 上就能做的事，公网有损部署时才选 WebTransport）
- [WAN-MOBILE-TRANSPORT-DECISION-20261124.md](WAN-MOBILE-TRANSPORT-DECISION-20261124.md) ← **公网/内网穿透/手机访问传输层决策（2026-11-24）**（承接上一份的追加关键场景：穿透+手机4G5G 下 RTT 20~100ms/丢包0.1~3%，TCP 队头阻塞从≈0变主延迟来源，UDP 从可选变强烈建议；★最关键=穿透对 UDP 支持差异：只有 frp/type=udp 与 Tailscale/ZeroTier(WireGuard) 能透 UDP，ngrok 免费版无 UDP、Cloudflare Tunnel 免费档只 HTTP/TCP、花生壳主要 TCP——穿透不支持 UDP 时 WebTransport 直接建不起连接；决策=支持 UDP 的穿透+手机场景必须换 WebTransport，只转 TCP 的穿透要么换 frp/Tailscale 要么留在 TCP 用 WebSocket 分帧+按需IDR；落地方案 P0 帧边界+IDR+输入WS→P1 WebTransport datagram→P3 WebSocket 降级）
- [TRANSPORT-ADAPTIVE-DESIGN-20261124.md](TRANSPORT-ADAPTIVE-DESIGN-20261124.md) ← **传输层自适应架构设计（2026-11-24）**（用户需求「自动识别自动切换」：能力协商 + 800ms 短超时探测 + 三级静默回退，用户零配置；★可行性关键=net11.0 有 System.Net.Quic(裸QUIC,仅stream无datagram)+Kestrel HTTP/3，但**无第一方 WebTransport 服务端**（MDN 官方库列表是 Go/Python/Rust/Node/Deno 无.NET）——故 P0 先落自适应骨架+WebSocket分帧+帧信封+按需IDR，UDP 承载留 P1/P2（WebRTC 或引第三方 WebTransport 库）；探测协议报文格式/超时值/四种场景决策树/服务端客户端改造清单/五条无感降级硬规则全给出）

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
- [AUDIT-20261124-07-DEFECT-LEDGER.md](AUDIT-20261124-07-DEFECT-LEDGER.md) —— **全仓库延迟/卡顿缺陷总账（38 条：P0 4 / P1 12 / P2 14 / P3 8，其中 20 条用户未提及）**（静止桌面探测永久降 BitBlt、WGC 零拷贝陈旧纹理、每帧 5~14 MB LOH 分配、静止去重空转、点击 2 RTT+12 ms、WH_MOUSE_LL 每事件热键 Parse、控制通道单 FIFO 队头阻塞、qwinsta UI 阻塞等；附 P0→P1 开工顺序与验证建议）
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

- **[HANDOFF-20261124-LATENCY-OPTIMIZED.md](HANDOFF-20261124-LATENCY-OPTIMIZED.md) ← ★★★ 最新交接（2026-11-24 收官）**：**端到端 444ms → ~90ms（-83%）**，16 个 commit 全部推送。核心：抓屏探测不再永久锁死 BitTlt（真正根因，非 GPU 零拷贝）／静止桌面 2fps 保活根治断流／累积式帧节拍器 15→30fps／DDA 阻塞等帧／PS 着色器零拷贝真正打通(PSNR 52.6dB)／GPU 5 拓扑智能路由／光标回传服务端补齐／左键不再触发 PointerLock／蓝球彻底删除／落点误差修正。含 6 大技术结论（RDP 60fps 官方上限、BGI 200fps 源码级解答、采样 120 无收益、Sunshine 8ms 真相、浏览器 UDP 决策矩阵、多 viewer 不腰斩）+ 5 条事故教训 + P0-P3 下一步。版本 3.3.35，客户测试包已构建。
- [HANDOFF-VIDEO-LATENCY-GPU-20261124.md](HANDOFF-VIDEO-LATENCY-GPU-20261124.md) ← 上一份交接（2026-11-24 凌晨）：GPU直通攻坚——首帧25s死锁根治/吞点击/落点偏移/可见光标/GPU零拷贝(WGC+DDA)全部提交，但延迟仍444ms；部分结论已被本轮推翻（零拷贝并非延迟根因）
- [LOW-LATENCY-DEEP-DIVE-20261124.md](LOW-LATENCY-DEEP-DIVE-20261124.md) ← **四产品延迟工程深挖（2026-11-24）**：Parsec/Moonlight/Guacamole/RustDesk 一手源码级拆解（Moonlight 输入 1ms 微批+16位拆包+50ms 丢帧上报+RFI 细节；Guacamole 1000ms 轮询+事件插队+sync/ack 帧界语义；RustDesk 每 viewer ACK 背压+TestDelay 闭环 QoS「先码率后帧率」）；浏览器端天花板推导：1080p60 视频单程 37~60ms、闭环跟手 ~42ms，SoulLink 444ms 差距在管线税不在网络；附 10 项可落地参数清单（P0=帧边界消息+客户端按需 IDR 回传）
- [RDP-FRAMERATE-AUTHORITY-20261124.md](RDP-FRAMERATE-AUTHORITY-20261124.md) ← **RDP 帧率权威查证（2026-11-24）**：微软官方双口径确认 RDP 默认 30fps 上限（KB2885213 + DWMFRAMEINTERVAL=15→60fps；第二口径=33ms 编码预算）；策略组三设置（GPU渲染/AVC硬编/444）作用与取值（本机已全开）；DDA 官方文档对 RDP 零承诺、实测 14fps 根因=DWM 变化驱动合成；SoulLink 结论：P0=先换 WGC/事件驱动把 14→30fps（不动注册表），游戏解锁 60fps 后才需 DWMFRAMEINTERVAL（需授权+重启），>60fps 无官方路径；WGC"帧率=游戏渲染率"在 RDP 内被 DWM 合成率封顶的重要修正
- [RDP-DDA-CEILING-EXPERIMENT-20261124.md](RDP-DDA-CEILING-EXPERIMENT-20261124.md) ← **RDP 会话 DDA 真实捕获率上限实验（2026-11-24）**：独立最小 DDA 计数器（只 Acquire/Release 纯数帧）在 A2/session4 实测——静止 0.5fps、鼠标 64.7~65.9fps（p50=15.2ms 精确钉在 60fps 合成节拍）、满屏 GDI 动画 35~38fps、console 对照 253fps；判定：DWMFRAMEINTERVAL=15 已生效即 60fps 到顶，SoulLink 50fps=管线损耗另有 10fps 空间；用户 BGI 200fps 反例源码级解答=BetterGI 的 DwmGetDxSharedSurface 模式直接开游戏窗口 present 表面（应用层通路，不受 DWM 合成节拍限制），WGC 模式 BetterGI 注释自证 ~60fps 封顶；若要 >60fps 唯一可行路=DwmGetDxSharedSurface 抓游戏窗口（P2 调研，未文档化 API 有风险）
- [BGRA-NV12-PARALLEL-20261124.md](BGRA-NV12-PARALLEL-20261124.md) ← **P1 色彩转换并行化（2026-11-24）**：BgraToNv12 行级 Parallel.For（Y 按行+UV 按行对分区，零写冲突），独立验证 6 尺寸+10 重复逐字节一致，性能 1080p 5.10→1.27ms(4.0x)/1440p 8.32→2.14ms(3.9x)；真机 A2 convertMs 7~8→2ms（鼠标/动画两场景 40+ 行稳定），端到端再省 ~6ms；AVX2 SIMD 降级决策记录（UnpackLow 字节交织语义等 4 个坑，草稿存 SimdBgraToNv12.cs.txt 未接入）；小图/异常自动降级标量
- [AUDIT-20261124-00-EVIDENCE-BASELINE.md](AUDIT-20261124-00-EVIDENCE-BASELINE.md) ← **取证与测量基准（2026-11-24）**：444ms 拆段实测（agent 侧 captureMs≈25+convertMs≈8+encodeMs≈3=36ms，其余在「有效帧率仅~14fps + 浏览器侧 + 前端断流重连」）；GPU 零拷贝**代码存在但从未生效**（日志 0 条 PSNR/降级行，静默 fall-through 证据穷举到行号：DxgiDdaCapture.TryCaptureBgraTexture:265/297、GpuColorConverter:191/192）；「零拷贝导致回到444」被推翻；附最小验证清单 V1~V5
- `GPU-ADAPTATION-DESIGN-20261124.md` —— P0-G/P0-H GPU 自适应路由设计（N/A/i 卡任意组合自动选抓屏/编码路径，同 GPU 才开零拷贝）
- `E2E-LATENCY-BASELINE-20261124.md` —— 端到端延迟基线复测（444ms→85~130ms 拆解：三组 15s 场景实测 + 分段延迟模型 + 剩余瓶颈）
- `IMPL-20261124-AGENT-DEP-REDLINE.md` —— **编译红线（弱化版）落地（2026-11-24）**：Release 构建后自动扫 Relay/**/*.cs，引用 using System.Windows.* / SoulLink.Controls / SoulLink.Views 即 MSB3073 构建失败（file:line 精确报告）；白名单 System.Drawing*（GDI 截图）；紧急通道 -p:SkipAgentRedline=true；干净基线 dotnet build 0 error 且红线输出通过、注入违规实测构建失败——对标 Yomori「链接+排除」CS0246 红线的弱化实现（同 csproj 无编译隔离可用）；踩坑：PS 脚本经 MSBuild Exec（Windows PowerShell 5.1）跑必须带 UTF-8 BOM（无 BOM 中文注释按 ANSI 读成 ParserError）
- `DEPLOY-VERIFY-4b5e99f.md` —— 部署验证 V2（4b5e99f，3.3.34）：版本确认为 3.3.34+4b5e99f 已部署；真实观看日志 64 行实测 captureMs p50=8/convertMs p50=1/encodeMs p50=5；跨 GPU 零拷贝=禁用为物理正确降级；三项 P0 代码齐全（帧边界 length-prefix/按需IDR/输入WS），仅服务端按需IDR拿到运行实证、其余两项因无token/agent已退出未端到端验证；6 次客户端断开待查；端到端未新测（引用 baseline 85~130ms）
- `IMPL-20261124-WATCHDOG.md` —— **外置看门狗 sl-watchdog 落地（2026-11-24）**：独立进程常驻监视主程序整窗假死（SendMessageTimeout 三态判据——不看 GetLastError/IsHungAppWindow，Yomori 三次漏报固化的结论），假死判定（连续2次×3s超时）→ ProgramData 日志 + NtQuerySystemInformation 线程快照 → 持续 90s 抓 MiniDumpWithFullMemory（cooldown 10min）；只观测不动进程（救援仍归 sl-rescue 人工）；框架依赖 72KB 单文件，主构建自动发布+复制到 dist；--once 自检实测 Alive 链路 ✓、常驻 12s 日志/空转不误报 ✓；未实测项如实标注（真·假死注入验证、常驻自启注册留 Lead 决策）
- `BENCH-YOMORI-02-AGENT-ARCH.md` —— **Yomori 对标之二：Agent 架构与自动部署（2026-11-24）**：独立精简 exe（228→73MB）/独立 AgentVersion 元数据与主程序解耦（忘 +1 冒烟断言兜底）/双槽 agent_b 热替换（目录改名实测死路→先送走后覆盖→备用槽）/内容哈希+版本号双判据分层/独立进程看门狗（68 线程全卡时进程内看门狗调度饿死三次漏报实证；SendMessageTimeout 三态判据不看 GetLastError/IsHungAppWindow）；对照结论：我们「主 exe 复制改名+主程序版本当判据+无独立看门狗」落后一代但单文件 3.3MB 形态更优；可抄 3 条（版本解耦 0.5 天/双槽 2 天/看门狗 1 天）+编译红线弱化版；GPL-3.0 零代码复制，仅机制指认
- `AUDIT-20261124-10-TRANSPORT-LATENCY-BUDGET.md` —— **传输层延迟预算审计（2026-11-24）**：233ms 差分（浏览器−RDP 同源秒表口径）里传输层最多占 2~20ms（<10%）：回环/LAN TCP RTT 实测 p50 1.1~1.7ms、viewer 队列真机 queueAge=0/Max18、每帧一次 FlushAsync 无攒包、Nagle 罚项 0~2ms（HttpListener 无法设 NoDelay，但整帧大块写+每帧 Flush 使其不构成 40ms 级风险）；场景判定=局域网直连非公网；换 UDP/WebTransport 净收益 ≈0 甚至为负（HTTPS+QUIC 成本 > 省下 <2ms）；~140ms 账外延迟指向管线帧供给（真机观看实际 ~18fps + staticSkipped 624）与浏览器段
- [AUDIT-20261124-08-RDP-BASELINE-VS-BROWSER.md](AUDIT-20261124-08-RDP-BASELINE-VS-BROWSER.md) ← **RDP 基准侧延迟归因（2026-11-24）**：233ms 是差分（=SoulLink浏览器端到端 − RDP端到端），SoulLink 绝对端到端 = 233 + RDP自身延迟 ≈ 253~293ms；RDP 自身延迟 ~20~60ms（LAN同机物理下限~20ms，DWM重定向面直通GPU管线+无CPU回读+bitmap delta+客户端直绘）；三栏分段表=共有段抵消/SoulLink独有(进233)/RDP独有(加回绝对)；账外~120~160ms 延迟是服务端日志~90ms 与真机差分 233ms 对不上的核心矛盾（不在 RDP 侧、不在 capture/convert/encode/queueAge 字段，归浏览器段埋点定位）
- `AUDIT-20261124-10-TRANSPORT-LATENCY-BUDGET.md` 之后的补充审计见上一行；`TOOL-20261124-DIRECT-PROBE.md` —— **独立原生客户端探针（2026-11-24）**：绕开浏览器直连 /stream?v=2（MF 软解 + GDI 上屏 + 三段计时 t0收包/t1解码/t2上屏）判定延迟归属——原生链路解码 0.91ms + 上屏 7.79ms（合计 ~9ms）vs 浏览器估 25~45ms；帧到达 gap p50 55ms = 服务端出帧节奏 ~16fps（agent 日志交叉验证 16.4fps、staticSkipped=0 排除静止误判）→ **233ms 差分主项在服务端帧供给节奏（capture 含等帧 8~89ms 波动），浏览器段次之（多 ~15~35ms）**；含 MFT 解码器 6 个踩坑记录（category GUID 注册表实证、output-first 协商、输出 sample 不可复用等）
- `ARCH-20261124-RDP-STREAM-TAPPING.md` —— **RDP 画面直取架构评估（2026-11-24）**：5 条路径可行性+延迟对比（ActiveX二次中继/agent当RDP服务端❌机制级不可行/mstsc shadow变体/HTML5 RDP viewer/WDS API）——正面回答用户质疑：RDP 在架构里只是「看画面的客户端」（RdpConsole ActiveX），会话载体是登录+TermWrap 修补的 termsrv，agent 经计划任务启动与 RDP 无关；「拿 RDP 画面→转推网页」的二次中继必劣化（60~98ms vs 现状 46~63ms），唯一真省延迟的是 viewer 直接说 RDP（路径4 IronRDP-Web，总延迟≈RDP体感 40~100ms，降幅3~4倍但需换底座1~3月）；三项硬约束查证：console 会话无法作为 RDP 服务端被连接（接管语义）、同账号自连 3389 必踢人（shadow 是唯一 attach 例外）、第三方 RDP 端点与 termsrv 端口不冲突但拿不到其图形栈（FreeRDP shadow 实源=DDA）；本机实证 TermWrap 已生效（ServiceDll 指向 TermWrap.dll）
- AUDIT-20261124-11-LATENCY-ACCOUNTING-GAP.md — 延迟账目核对：233ms 差分 vs 服务端 ~90ms 账面，账外缺口归属与判别实验（input-chain-analyst）
- [ARCH-20261124-CLOUD-GAMING-WEB-REWRITE.md](ARCH-20261124-CLOUD-GAMING-WEB-REWRITE.md) ← **架构决策：自建「云原神式」Web 应用能否降延迟（2026-11-24）**：三层拆解（L1采集/编码已对齐NVENC+DDA、L2传输WebRTC/UDP、L3呈现WebGL2零回读/硬解/按需IDR/输入WS已落地）；结论=云原神式重写对局域网场景增量 0~2ms 甚至为负（云原神 jitterBuffer≈74ms 是税），不能解释/消除 120~160ms 账外缺口（缺口未测量，全仓 rVFC/capturedAt 0 命中）；方案 A 补埋点(0.5人日)→B 轻量加固(降档)→C 完整 WebRTC(公网手机终态)；一句话=先埋点定位缺口，公网再谈 WebRTC
- [BENCH-YOMORI-01-RDP-EMBED.md](BENCH-YOMORI-01-RDP-EMBED.md) ← **对标研究 Yomori 之一：RDP 内嵌与画面链路（2026-11-24）**：Yomori（GitHub AITNR/Yomori，**GPL-3.0**）**放弃 ActiveX 改走 FreeRDP 3.32.0 自建客户端 + C ABI 原生 DLL + D3D11 SwapChainPanel 自绘**（`winui3/docs/rdp-embed-freerdp-plan.md` §1.2「不要复活 ActiveX 路线」），我们走的正是他们放弃的 ActiveX+`WindowsFormsHost` 路（`Controls/RdpConsole.cs:19,37`）。**三分表**：可借鉴仅 6 条思想（键盘 toggle 同步=我方确定性 bug / 恢复后等关键帧闸门 / 按会话暂停解码 / 脏区三不变量 / 能力位降级 / 诊断分桶）；我方更强 5 条（一份画面多端分发、WebGL2 零回读、帧边界 v2+按需 IDR、TermWrap 多会话、业务面）；不该抄 5 条（任何 GPL 代码/patch、vcpkg 构建链、整体换路）。**关键量化**：脏区增量在我方**上限仅 ~2.5ms/帧**（我方 `media-probe` 实测脏矩形占比 50.7%、编码只占 4.9ms，`MEDIA-CAPABILITY-20261122.md:122`）且 NVENC 喂不进矩形补丁 ⇒ **不建议立项**；AVC444 低色度他们折腾 3 份计划书+1 次撤回只换来 **−5.6% CPU**，与延迟无关。**本次对标没有任何一条能降 233ms**（我方浏览器段已实测 1~5ms）。**④ 未复制任何 GPL 代码**（§7 有红线决策树）。
- [HANDOFF-20261124-39-AGENT-SPLIT-PENDING.md](HANDOFF-20261124-39-AGENT-SPLIT-PENDING.md) ← **3.3.39 交接（2026-11-24 下午）**：①**P0 投送全线失败已定位根因**——单文件发布导致 `PEReader.HasMetadata=False`，`ReadAgentVersion` 两个文件都返回 null → `ValidAgent` 永远 false → `AGENT_INVALID`，与版本号无关；②**界面状态造假四处根因**（XAML 硬编码「引擎就绪」+ 只有设置页触发 + 只在启动查一次 + 只探测 health 不含 agent 判定）；③不可恢复错误仍在指数退避重试（需分类+保留恢复入口）；④**agent 尚未拆分**（与主程序 SHA 完全相同，`build.bat:94` 是 copy），实测 Broker 61MB / agent 47MB×2，拆分方案与 12 个类的真实依赖清单见 §4；⑤延迟问题已闭环（静止 233ms=保活节拍采样等待，动态 15~63ms）；⑥Yomori 对标结论与 13 项立项（10 项冻结）；⑦26 个文件未提交，工作区状态与十条铁律
- [BENCH-YOMORI-04-RESEARCH-20261124.md](BENCH-YOMORI-04-RESEARCH-20261124.md) ← **对标研究 Yomori 之四：二次审计与解冻裁决（2026-11-24）**：①**推翻团队共识**——P0-1（投送全失败）与 P0-2（界面造假）是**同一根因**：两条互斥版本判据在 3.3.39 上同时成立（`BrokerInstall.ReadAgentVersion` 读托管元数据 → `PEReader.HasMetadata=False` → 恒 null → 恒 AGENT_INVALID；`AgentSync.Inspect` 读 `FileVersion` → 两边都 3.3.39.0 → 恒 Ready），**只改 XAML 默认值修不好 P0-2**；②**Yomori 上游已冻结 6 天**（最后提交 2026-11-18 `089d06c7`，且本身是 fork，上游 2026-06-01），「找新增」为空，本次价值全在「上次漏读的文件」：`RdpErrorText.cs`（30+ 原因码→人话+动作，对标 P0-3）、`RdpClientStateMachine.cs`（纯函数状态机+非法迁移保持原状态）、`AgentStartGate.cs`（双正交判据+缺哪样补哪样）、`build.yml`（**34 处构建断言**，我们 0 处）；③**修正 BENCH-03 事实错误**：我们有 `tools/layout-check/` 下 **90 个断言工程**（非「0 测试」），缺的是 runner+闸门，故冻结项 #7「建 xUnit」改判为**解冻换形态，1~2 人日 → 0.5 人日**；④**13 项冻结裁决**：解冻 #7/#8/#11/#13，继续冻结 #2/#5/#6/#10/#12，#5「更新 6 态」建议**正式作废**（前提不成立）；⑤前 5 项清单合计 ≈3 人日，其中 3 条直服本轮 P0。GPL-3.0 零代码复制（仅机制与接口事实，全文用文件:行指认）。
- [AUDIT-20261124-HOTPATH-IO.md](AUDIT-20261124-HOTPATH-IO.md) ← **热路径同步 I/O 审计（2026-11-24，只读）**：对标 Yomori 588MB/整窗卡死（诊断写盘在锁内 + 每帧触发）做全仓排查。**结论：高风险项 0 项** —— UI 线程同步写盘共 4 处但频率均 < 10Hz；持锁写盘 0 处（`AsyncLog` 无锁、`RouteLedger` 锁与写盘已分离）；6 个 ≥10Hz UI 定时器（16/33/60/120/150ms）磁盘调用**实测全为 0**；逐帧抓屏路径 `Relay/DxgiDdaCapture.cs` 磁盘调用 0 处。三个真实问题：A) `ConsolePage.xaml.cs:1858` 同步直写与 `RdpConsole.cs:2116` 的 `AsyncLog` 队列**写同一份日志**、两套实现；B) `TickHub.cs:190` 订阅者持续异常时 → `App.xaml.cs:469` **1Hz UI 线程同步写盘且无大小上限**（AsyncLog 有 5MB 轮转、AppendErrorLog 没有）；C) `AsyncLog.cs:48-62` 调用线程 stat 实测 13.7µs/次（不构成卡顿）+ `Rotated` 无锁 HashSet（**20万次并发实测未复现**，列为观察项）。设计做对的：AsyncLog 有界队列、RouteLedger 专用后台写线程、TickHub.RegisterBackground 已把 4 个同步 IO 订阅者迁出 UI 线程。修复建议合计 0.5~0.6 人日，均非 P0

- [CLEANUP-20261124-WORKSPACE.md](CLEANUP-20261124-WORKSPACE.md) ← **工作区清理 + 一次误删的复盘（2026-11-24）**：1833.1 MB → **914.4 MB**（释放 918.7 MB，artifacts 51→18 目录）。判据不是「压到某个数字」，而是「**这个目录是不是某次性动作的副产物**」。删对的一次性产物（433 MB）：`server-build`/`dist-server-2.1.0`(203 MB，Server\ 12 文件全 git 跟踪、可 `dotnet publish` 重建)、`stream-probe-bin`(41.5)、`dist-hotfix[1-3]`/`dist-p0f`/`dist-test`/`dist-build-new`/`dist-backup-*`(137)、`latencyfix-agent`/`wgc-publish-test6`/`stable-build`(75)。**误删复盘（约 410 MB，指令变更时序所致，Lead 已确认不补救）**：历史版本快照 `dist-3.1.x~3.3.33` 10 个（349.9 MB）+ `Android\app\build`（60.2 MB）。**三条教训**：① **历史版本目录应保留，不应按「版本号低」批量删**（版本号低只说明旧，不说明没用；且源码已前进、**不可精确重建**）；② 删前须区分「文档引用」与「叙事提及」——只要有任何文档按名引用过就默认保留；③ 空间充裕时「可重建但要花时间」的不该删。**守住的关键依赖**：`artifacts/package`（含 `filelist.json` 增量发布基准，删了会让下一版把所有文件当新增重推）、`watchdog-build`（csproj Target 依赖）；`tools\updater\bin` 因 17:18 仍在写入 + 3 个 MSBuild 活跃而跳过。保留 `docs/` 下 123 MB 图片（`git ls-files` 实测 75/75 全被跟踪，是源码非残留）。**新标准**：已推动 `AGENTS.md` 从 < 1 GB 放宽到 **< 3 GB**（本任务未改规则文件，由用户拍板）。附「怎么重建被删产物」+ 下次清理规则。
- [PREP-20261124-T10-COST.md](PREP-20261124-T10-COST.md) ← **t10 开发前勘察：90 个断言工程的真实成本（2026-11-24）**：**全量实测 5.1 分钟**（默认 bin，依赖完整）/ 1.7 分钟（`-o %TEMP%`，但会缺依赖假失败）。**决定性优化**：`--no-dependencies` 把 ProjectReference 类单工程从 **7.2s → 0.8s**（-89%）——53 个工程都指向同一个 `SoulLink.csproj`，而主工程**空转构建就要 7.6s**，增量也不省。**分类**：53 个 ProjectReference（拉整个 WPF 主工程）+ 37 个 Compile-only（0.6s）。**通过率只有 62%**：PASS 56 / RUN-FAIL 24 / **BUILD-FAIL 10**；10 个编译失败里 4 个是「链接源码闭包不全」（CS0103 缺 `ServerEndpoints`/`BgiDataDependency`）、1 个路径失效、5 个待查；24 个运行失败里 12 个 exit=1（抽样验证既有断言过期 `LayoutCheck` 也有疑似真回归 `ManifestPatchCheck`/`ChannelDetectCheck`）、8 个前置条件不满足、1 个 `0xC0000005`（`BrokerCheck`）。**约 22 个（24%）需管理员/真机/GUI 或触及 A1A4，闸门必须有 `-Skip`**。**闸门策略建议分层**：快闸门只跑 37 个 Compile-only（~30s）挂 build.bat；全量 5.1 分钟发布前手动。**成本重估 1.5~2 人日**（原估 0.5，主因是 34 个红的要先处置）。附「同一个 `undefined\temp\` 坑今天第二次踩」（HANDOFF §8 第 10 条早有记录）。
- [T19-ASSERTION-GATE-20261124.md](T19-ASSERTION-GATE-20261124.md) ← **T19 断言闸门：runner + 基线 + 红项分类（2026-11-24）**：交付 `scripts/run-assertions.ps1`（快闸门 37 个 **32.4 秒** / `-Full` 90 个 **214.7 秒**）+ `artifacts/assertions-baseline.json`。**权威基线：PASS 43 / FAIL 15 / BUILD-FAIL 8 / SKIP 24**（快闸门内 22 PASS / 4 FAIL / 3 BUILD-FAIL / 8 SKIP）。**8 个 BUILD-FAIL 全是「产品 API 变了断言没跟上」**：`ProcessCommandLine`×3 已被移除、`DiscoverChannelSessions` 因性能优化有意删除（`ConsolePage.xaml.cs:1248` 注释可证）、`BusinessDayData` 类已从 HEAD 删除（`AccountStateCenter.cs:137` 明写「不再经过」）；可修的仅 4 个（`DupAgentProbe` 改名 `FindManualAgentSessions`、`LampProbe` 私有字段、`ValidatorCheck` 补 stub、`LayoutCheck`/`InstallScriptCheck` 阈值）。**第二步不挂 build.bat**：快闸门 7 个非 PASS，按任务规定「全绿才挂」。**两个踩坑**：① PS 脚本必须带 UTF-8 BOM（否则 5.1 读乱码）；② `--no-dependencies` + 增量判定会产生**幽灵失败**——源码还原后 exe 仍是旧行为，必须**同时清 bin 和 obj**，故 runner 新增 `-Clean`。**反向对照已验证闸门会红**（注入 FAIL → exit=1，且单条 FAIL 也正确计数）。**发现 t7a 的 `Agent\` 目录正在卡住主工程编译**（`Stubs.cs` 的 `App`/`AppConfig` 与主工程冲突，尚未加入 `DefaultItemExcludes`），已上报未擅动。
- [T22-ASSERTION-CLEANUP-20261124.md](T22-ASSERTION-CLEANUP-20261124.md) ← **T22 断言整改：作废 7 + 修 3 + 防弹窗跳过 + 进程清理（2026-11-24）**：工程 90 → **83**；**BUILD-FAIL 8 → 0**；快闸门 **26 PASS / 1 FAIL / 8 SKIP（29.2s）**；全量 **43 PASS / 6 FAIL / 28 SKIP（103s）**。**作废 7 个，每个附独立证据**：`ProcessCommandLine` 全仓 0 命中（×3）、`DiscoverChannelSessions` **仅存于注释且已被 `DiscoverLiveAgentSessions` 取代**（`ConsolePage.xaml.cs:1246-1255` 性能优化有意删除）、`BusinessDayData` 类已从 HEAD 删除（`AccountStateCenter.cs:137`「不再经过」）、`FindManualAgents` 只剩断言自引用（产品改名且返回类型已变）、`_debugLastStatus` 概念消失。**修 3 个**：`LayoutCheck`（手算证明 `contentHeight` 恒为 `vh-2`，原「9 个必然滚动」断言**在任何视口下都不可能成立** → 改验证填满视口语义）、`InstallScriptCheck`（SID 逻辑搬到 `broker-setup/Program.cs:210-213`）、`ValidatorCheck`（补 3 层桩：`BgiDataDependency` + `App` + `AccountScope/BgiPathResolver`）。**⚠️ 防弹窗（用户第二次被骚扰）**：`FlowCanvasInputCheck` 会开 **Topmost 置顶窗口「SoulLink isolated mouse check」+ 模拟真实鼠标**，此前不在跳过清单 —— 已加 SKIP。全量排查 7 个候选，**只加真会弹窗的 2 个**（另 `FlowEditorVisualCheck`），`FlowEditorCheck`/`FlowEditorLiveCheck` 核实后**不跳过**（不 Show）。**关键区分**：**SKIP 是防止打扰（跑前），Cleanup 是兜底收尾（跑后）—— 窗口在跑的过程中已骚扰，清理不能替代跳过**。**进程清理已实现并实测**：PID 差集 + 白名单判据（`dist\stable`、`Program Files\SoulLinkDev` 标 PROTECTED 绝不杀）；跑前跑后 SoulLink 系进程 4→4 个、PID 完全相同、**零残留零误杀**；`-NoCleanup` 可保留现场。**未挂 build.bat**：快闸门仍 1 个红（`PlcScanCheck`）。其根因已验证为**测试时间注入缺陷**——`FlowModels.cs:166` 属性用 `DateTime.Now`，测试注入 `2026-11-18` 而今天是 `2026-11-24`，产品逻辑正确。修它需改判定逻辑，**待 Lead 批准**。另发现并清理 4 个**过期跳过项**（t21 删的工程仍留在清单里）。**主工程已恢复 0 error**。
- [T15-LOG-IO-20261124.md](T15-LOG-IO-20261124.md) ← **T15 日志写盘整改：问题 A + B（2026-11-24）**：**问题 A（UI 线程同步写盘）**：`Views/ConsolePage.xaml.cs` **两处**（`Debug` L1853 + `ShieldTrace` L318，审计只点了前者）由 `Directory.CreateDirectory` + `File.AppendAllText` 改为 `AsyncLog.Append` 队列化。行为等价（同文件、同格式、建目录由 AsyncLog 代劳）；**唯一差异是写入时机同步→异步，这正是改动目的**，且改后与 `RdpConsole.Debug`（同一文件的另一写入者）真正同源。**问题 B（error.log 无上限）**：`App.AppendErrorLog` 加三条纪律 —— ① **5MB 上限 + `.old` 轮转**（抽出 `AsyncLog.RotateIfOversized` 共用）② **相同异常 60s 内只写首条**，抑制条数在下次落盘时以「另有 N 条被抑制」补写（信息不丢）③ 绝不抛。**关键设计**：指纹 = `source + 异常类型 + Message`，**刻意不含 StackTrace**（含了会永远判不重）；判定抽成 `internal` 纯函数 `ErrorFingerprint`/`ShouldWriteError` 以脱离 WPF 可测。**验证 17 条断言全绿**：轮转部分**直接调用产品代码**（造 6MB 文件 → 确认移走 + `.old` 尺寸一致，9 条）；去重部分 8 条。构建 **0 error**（326 警告）。**未碰** `Controls/RdpConsole.cs`/`MainWindow.xaml.cs`/`SettingsPage.xaml.cs`；未新建 DispatcherTimer；未 commit。
