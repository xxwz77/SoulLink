# 远程画面方案 V2：RDP 实测上限与流畅感解耦

> 日期：2026-11-22；作者：kimi-reviewer；任务：`task_mwrrh09wbplqr7`。  
> 范围：只读分析 + 本文档。未改业务代码、未动 dist、未提交、未发版。  
> 证据等级：**实测**＝`artifacts/rdp-probe/` 原始输出；**代码事实**＝当前源码可证；**推算**＝按公式换算；**需验证**＝尚无真实 BGI 动态画面或端到端证据。  
> 单位：帧体积 `90KiB` 是 Lead 补充的当前网页 HTTP JPEG 单帧值，约等于用户口述 90KB；本文显式按 KiB 计算，不再混淆 MB/MiB、Mbps。

## 0. 最终结论

**放弃“2fps 录制 + 60fps JPEG 直播”。当前 RDP 抓屏小样本均值 27.168ms、30 次均值 31.35ms，串行转换/编码后现实目标应默认 5~10fps、短时按需 15fps，60fps 只能用于本地视觉反馈，不是远端采集目标。先把帧序号、交互时抓帧、预测光标和预览分辨率做好；只有测量证明这些不够，才值得投入新媒体管线。**

- 当前 `SessionRecorder.GrabJpeg()` 在同一轮串行执行 BitBlt、光标叠加、`Image.FromHbitmap` 和 JPEG 编码（`Relay/SessionRecorder.cs:311-354`），因此不能只用 BitBlt 估算线上 fps。
- `DUAL-RATE-DESIGN` 的共享单次抓屏思路可保留；60fps 直播、直播/录制双重 JPEG、以及“空闲因此近零”的数字不可保留。
- **本机 RDP 代理里的 DXGI Desktop Duplication 实测不可用**；NVENC 枚举存在不等于当前会话能建立硬件 D3D 设备，也不等于能取得硬件编码后的帧。
- 对外网，90KiB×10fps＝**7.37Mbps**，×30fps＝**22.12Mbps**。高帧率 JPEG 直接超出常见低码率外网链路；限制实际新帧率比换 WebSocket 更先解决问题。

## 1. 原始实测：证实与推翻

### 1.1 RDP 实际结果

| 项 | 原始数据 | 能说明什么 | 不能说明什么 |
|---|---|---|---|
| RDP 会话 | `SM_REMOTESESSION=RDP`，1920×1080，单显示器；见 `artifacts/rdp-probe/rdp-report.md` | 是目标类型的远程会话，不是早期本地控制台探测 | 不是所有 Windows/驱动/RDP 拓扑的通用证明 |
| BitBlt 小样本 | `bitblt.txt`：19.02 / 22.42 / 27.70 / 32.30 / 34.40ms；均值 **27.168ms**，中位 27.70ms | 单抓屏就接近/超过 30fps 的 33.3ms 帧预算 | 不是稳定生产负载的 P95 |
| BitBlt 30 次 | `final.txt`：940.5ms/30＝**31.35ms/帧** | 该次抓屏约 31.90 次/秒的上限 | 不含转换、编码、网络；不是端到端 fps |
| 转换与编码 | `bitblt.txt`：FromHbitmap **5.80ms**；JPEG Save 默认 **13.34ms**、82909 bytes | 当前链路的其他串行环节不可忽略 | 默认 JPEG 不能直接代替 q75 的耗时 |
| q75 编码 | `rdp-report.md`：5.818ms 均值、P95 9.760ms、164KB/帧 | 同一报告中的 q75 编码数字 | 不与 82909 bytes 默认 JPEG 混成同一帧；编码器输入内容不同，字节率不可直接拼接 |
| 有效性探测 | `capture-validity.txt`：25 次，均值 23.766ms、P95 32.88ms、最大 42.63ms；除首次外 `unique_colors=2` | 可记录系统调用耗时；存在“近乎单色”的画面证据 | **不能当真实 BGI 动态场景性能基线**；需带文字、游戏纹理、光标的截图/帧哈希验证内容 |
| DXGI/D3D | `dxgi.txt`：硬件建设备 `E_OUTOFMEMORY (0x8007000E)`；`retry.txt` 重试 `E_INVALIDARG (0x80070057)`；WARP 成功但 `final.txt` 中 DuplicateOutput 失败 | 本次会话下该 DXGI 路线不可用，且失败原因并不完全一致 | 不能据错误码就断言“显卡不存在”或“NVENC 一定不能编码” |
| MF 编码 | `rdp-report.md`：NVENC MFT 枚举到；WriteSample 0.238ms；40 帧 MP4 920KB | 编码器条目存在，CPU 侧提交有下界 | **不是端到端编码时间、真实硬件使用率或直播带宽**；不能据此承诺 60fps H.264 |
| 网页单帧 | Lead 补充：当前 HTTP JPEG 约 90KiB | 可用于条件化带宽计算 | 不是不同画面/质量/分辨率下的常数 |

### 1.2 对旧方案的裁决

| 旧说法 | 裁决 | 原因 |
|---|---|---|
| 抓屏不是大头、JPEG 编码才是最大头 | **推翻（本次 RDP 探测）** | 抓屏 22~31ms；q75 编码 5.818ms，默认 JPEG 13.34ms。仍需真实生产内容复核，但旧排序失去依据 |
| 2fps 录 + 60fps JPEG 播，62 次编码/秒可行 | **推翻** | 仅抓屏 60fps 就需要 60×31.35ms＝1881ms/秒的串行时间，已经超过一秒 |
| 共享抓屏可完全省掉直播成本 | **推翻** | 共享抓屏能避免双份 BitBlt；若两分支在同一到期点都编码，仍是两次 JPEG 和编码占用 |
| 脏矩形能省 90% 总采集 CPU | **不成立** | 它最多减编码/传输/解码；全屏 BitBlt 和比较仍存在。早期本地 `50.7%` 也不是 RDP 游戏样本 |
| 无观看者停直播后采集近零 | **有条件推翻** | 若录制 Ring 仍需 30fps，采集照样运行。只有降低录制速率/按需录制才能省这部分；需用户确认回放精度 |
| 复用位图/去 ToArray/尾帧 O(1) 是主要解法 | **推翻其优先级** | 有价值但不是几十毫秒级根因；不能代替抓屏与交互解耦 |
| 当前已有串行/失败退避/页面隐藏停止取帧 | **证实（代码事实）** | `docs/web-index.html:734-802`；但这些只省浏览器侧，不停录制线程 |
| 直连代理已有帧尾遍历与每帧重复发送风险 | **证实（代码事实）** | `SessionRecorder.cs:368-377` O(n) 取尾；`Relay/AgentWebServer.cs:229-246` 没有序号或 304 |

## 2. RDP 下真实 fps 上限

### 2.1 计算公式与预算

串行单线程可近似写为：

```text
frameMs = bitbltMs + cursorMs + fromHbitmapMs + jpegMs + otherMs
fpsUpperBound = 1000 / frameMs
```

这里的上限是**该次测量的算术上限**，不是协议保证。网络、浏览器解码、调度和超时还会降低实际有效 fps。

| 口径 | 计算 | 抓屏上限 / 含转换编码上限 |
|---|---|---|
| Lead 指定 22ms 抓屏 + q75 5.818ms + 转换 5.80ms | 1000÷33.618 | 45.45 / **29.75fps** |
| 27ms 抓屏 + q75 5.818ms + 转换 5.80ms | 1000÷38.618 | 37.04 / **25.89fps** |
| 30 次均值 31.35ms + q75 + 转换 | 1000÷42.968 | 31.90 / **23.27fps** |
| 22ms + 默认 JPEG 13.34ms + 转换 | 1000÷41.14 | 45.45 / **24.31fps**（不同质量口径，不能当 q75） |
| 27ms + 默认 JPEG + 转换 | 1000÷46.14 | 37.04 / **21.67fps**（同上） |

**结论：30fps 是当前路径的“靠近极限档”，不是有余量的默认档；60fps 不成立。** 帧率预算建议：

| 场景 | 新帧目标 | 说明 |
|---|---|---|
| 静止观看 | 0 次新图 + 心跳/条件 GET | 用序号避免重复传输/解码，不必伪装 1fps 新图 |
| 普通挂机/文字/局部 UI | 5~10fps | 优先省外网带宽和客户端解码；足够看状态变化 |
| 用户正在拖、点、转视角 | 短时 10~15fps，必要时 20fps | 是**预算目标**，不是保证；回落策略由输入停止/网络慢决定 |
| 取证录制 | 独立 2~5fps（待用户确认） | 若要求 30fps 连续回放，就接受抓屏/编码成本，不能同时宣称空闲近零 |
| 本地光标、触摸反馈、控件视觉 | 按浏览器 RAF/显示刷新率，最多 60Hz | **不抓屏、不编码**；这才是“跟手”的来源 |

### 2.2 30fps 的负载为什么不能用旧平均估算覆盖

以 31.35ms 抓屏计，30fps 仅抓屏就约 **94.05% 单核**；10fps 约 **31.35%**，15fps 约 **47.03%**（若每次调用大致占用一个核的推算，未含等待、其它步骤或并行）。若同线程再加 5.8ms 转换和 5.818ms 编码，42.968ms/帧对应 23.27fps；强行要求 30fps 只能积压、等待或丢帧，而不能创造更多 CPU 时间。多账号并行会争用 CPU/内存，不能把单账号上限乘账号数当成可保证容量。

## 3. 绕开 BitBlt 固定成本的候选

| 候选 | 是否绕开全屏 BitBlt | 已有证据 | 还缺什么 / 是否值得做 |
|---|---|---|---|
| **A. 全屏帧内同位置变化检测 + 帧序号** | 否；只是避免重复发送/解码 | 现有字节数判据不可靠；当前 `/live` 无序号 | **先做**。每次抓帧必须产生新 `capturedAt/frameSeq`；相同内容用可靠哈希/采样跳过编码/发送。降采样哈希会漏小光标，需用全分辨率校验/小块哈希或保留周期全帧。它不能省 BitBlt，不能说成采集近零 |
| **B. 输入驱动的立即抓帧** | 否；缩短“等下一周期”的等待 | 当前录制 Ring 与 `/live` 共用周期采集；点击/移动已直连代理 | **先做**。维护 `lastCaptureAt`，输入成功后排一次最新抓帧（可合并/去重）；输入突发只留最后一次，不做事件队列无限堆积 |
| **C. 预览降分辨率/按需抓窗口区域** | 可减少拷贝/编码像素，但仍用 BitBlt | 当前 `GetSystemMetrics(SM_CXSCREEN/CYSCREEN)` 全屏；尚无缩放质量实测 | **优先小实验**。预览可输出 960×540 等，录制保持原分辨率；文字/坐标可读性需人工验收。抓窗口需处理遮挡、黑边、DPI、前台窗口变化 |
| **D. `PrintWindow` 目标窗口** | 可能绕开全屏合成，但不是通用替代 | 本任务没有 RDP/游戏内容实测 | **需验证**。按窗口客户区抓帧、最小化/遮挡/硬件加速游戏兼容性逐一检查；失败即回退 BitBlt |
| **E. Windows.Graphics.Capture** | 目标是另一条采集管线 | 无本机 RDP 证据 | **需验证**。必须验证会话支持、权限/提示、D3D 可用性和代理框架依赖；不可从 API 名称推断可用 |
| **F. DXGI Desktop Duplication** | 设计上可，且可取 dirty/move rects | 本地控制台曾成功；本机 RDP 硬件设备与 WARP duplication 失败 | **本机当前路径出局**。除非环境变化后新探测成功，不安排实现；保留原始失败日志 |
| **G. MF H.264 实时编码** | 否；解决编码/码率，不是抓屏 | 仅 MFT 枚举/CPU 提交下界；真实会话 D3D 失败 | **后置需验证**。若仍走 BitBlt，应证明抓屏后系统内存到编码器的转换成本、端到端帧龄、硬件是否真正使用，不能只看 WriteSample |
| **H. 硬件/软件 JPEG 并行双管线** | 否 | 无双路实测 | **不推荐**。录制与直播同时到期时尽量只编一次或直播复用录制帧；双重编码加剧争抢，不能当性能优化 |

## 4. 画面帧率与交互跟手如何解耦

### 4.1 四层指标，不许混成一个 fps

```text
本地输入/视觉反馈  →  代理收到/执行输入  →  新一次屏幕采集  →  浏览器实际显示新帧
```

| 层 | 目标与做法 | 不是什么的证明 |
|---|---|---|
| 本地视觉 | `pointerdown/move` 立即画按压态、固定样式预测光标、触摸控件；RAF 最多 60Hz | 不证明远端收到，更不证明游戏已执行 |
| 输入通道 | 沿用直连 `/api/input`；move 按 20~30Hz 合并为最新值，click 已合并 Move+Click；`session/generation` 防迟到输入，取消/断线释放按键 | HTTP 响应成功不等于游戏逻辑已完成 |
| 代理反馈 | 需要准确位置时，输入响应可附带 `GetCursorPos` 与 `handledAt`，显示“已送达/未送达”；真实光标形状/热点需要独立元数据 | 本地预测位置不等于真实服务器位置；忙/按下状态要单独标识 |
| 远端画面 | 输入后立即触发一次抓屏；空闲停止新图；浏览端只解新 `frameSeq`；负载高时丢弃旧帧，保留最新帧 | 高刷新 CSS 不提高远端画面 fps；预测光标不能取代反馈画面 |

### 4.2 推荐最小管线

```text
输入事件 ──► /api/input ──► 返回 handledAt + 可选真实 cursor
    │                            │
    │                            └─► CaptureSoon（合并多次事件，最多一个待抓）
    │                                    │
    └─► 本地 RAF 光标/按压态              ▼
                                  同一抓屏结果
                                  ├─ 录制 Ring（2~5fps 或用户保真档）
                                  └─ 最新预览帧（5~10fps / 操作短时提速）

预览响应：{ frameSeq, capturedAt, width, height, jpeg }
浏览器：序号相同 → 不解码；序号新但已旧于本地新请求 → 不排队播放
```

具体改动边界：

1. `SessionRecorder` 只维护一次抓屏和 `lastBitmap/capturedAt/frameSeq`，不是两个各抓各的循环；Ring 保留历史，预览只引用最新帧。`frameSeq` 标识**采集事件**，与浏览器解码、是否变化是三个不同字段；不能把内容重复当作抓屏重复。
2. 直播帧无需独立高 fps JPEG；同到期点录制与预览一致时复用同一 JPEG。录制要求更高画质时才另编码，并记录该成本。直播需要而录制未到期时，抓一次供预览；录制到期而无人观看时只产录制帧，不生成额外直播帧。
3. `AgentWebServer` 在现有 `/live` 支持 `afterSeq`/ETag 或 `X-Frame-Seq`，同序号不发送图。新增/修改端点必须同时通过 `WebDirect.IsAllowedPath` 与鉴权，不另开匿名端点。
4. 本地光标可一直显示；预测失败时回退真实位置/最后可信状态。不要假装它消除了输入→游戏响应延迟。
5. 拉帧必须保留串行、取消和页面隐藏规则，并补 `visibilitychange/pageshow` 回前台恢复。排队策略是最新一帧，不是每帧可靠送达的录像队列。

### 4.3 真正的验收

- 在真实 BGI 动态画面上记录：输入产生时间、代理处理时间、`capturedAt`、浏览器 `img.onload`/Canvas 绘制时间，给 P50/P95。
- 10fps 画面下，本地反馈仍及时；反馈画面不出现明显旧序号堆积；锁屏/切后台/断线后无持续按键。
- 静止时网络只保留协商/心跳，重复帧不下载、不解码；恢复操作能触发一次新帧。
- 录制前后 N 秒时长正确、MP4 时间不加速、Ring 内容满足所选录制档；不允许为预览性能牺牲事故取证而未告知用户。

## 5. RDP 服务端能力利用：能借什么，不能借什么

### 5.1 配置不是使用证明

Lead 提供的本机策略线索包括 `AVC444ModePreferred=1`、`AVCHardwareEncodePreferred=1` 等。微软官方说明这些策略用于偏好 AVC444 与硬件编码；**这不自动证明当前会话真实使用硬件编码**。官方给出的核验入口是事件 `RemoteDesktopServices-RdpCoreCDV/RdpCoreTs`：170 可见硬件编码器启用，162 可见 `Avc444FullScreenProfile` 等（[GPU 加速与验证](https://learn.microsoft.com/en-us/azure/virtual-desktop/graphics-enable-gpu-acceleration)）。本任务未读取/修改这些事件或注册表，状态标为**待验证**。

`GraphicsProfile=2`、`ImageQuality=3` 的口头值映射尚未找到可核对的微软公开取值表；**不把它们写成已证实结论**。官方 WMI 的 `Win32_TSClientSetting.EncodeImageQuality` 与 `RemoteSessionProfile` 是另一套值域，不能直接套给这两个注册表 DWORD（[Win32_TSClientSetting](https://learn.microsoft.com/en-us/windows/win32/termserv/win32-tsclientsetting)）。

### 5.2 能不能直接拿现有 RDP 的编码输出？——当前 SoulLink 不行

官方 MS-RDPEGFX 描述的是服务端到客户端的图形管道：编码位图通过 RDP 消息交给客户端表面；AVC444 是其中一种编码格式（[图形管道](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rdpegfx/da5c75f9-cd99-450c-98c4-014a496942b0)、[AVC444 结构](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rdpegfx/844018a5-d717-4bc9-bddb-8b4d6be5dd3f)）。它**没有给当前会话内的普通代理提供“旁路读取现有已编码流”的 API**。

因此：

- 当前 SoulLink 代理继续自己抓屏再给网页，确实会与 RDP 自身编码形成重复工作；但配置 AVC 并不提供一条可直接接入的本地共享帧接口。
- 若要让网页直接消费原生 RDP 图形流，需要真正的 RDP 客户端/网关、会话/安全协商和客户端解码链路。官方 RD Web Client 是 RDS 部署组件，不是现有 HTTP 代理可下载复用的一个解码器（[RD Web Client 部署](https://learn.microsoft.com/en-us/windows-server/remote/remote-desktop-services/remote-desktop-web-client-admin)）。这是独立大型项目，不是最小性能优化，**不纳入本期**。
- 不建议把截包、解密、Hook 系统 RDP 服务当工程路线；这既不是官方提供的能力，也会扩大安全和维护风险。

### 5.3 RDP 仍有什么可利用

官方提供自建动态虚拟通道，服务端可用 `WTSVirtualChannelOpenEx`、客户端需安装 DVC 插件；适合低量、需跨 RDP 的控制/光标数据（[WTSVirtualChannelOpenEx](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsvirtualchannelopenex)、[.NET DVC 示例](https://learn.microsoft.com/en-us/samples/microsoft/rdp-dvc-plugin-samples/rdp-dvc-simple-plugin-dotnet/)）。**它不会把现有 AVC 流复制给我们，也不能直接被手机浏览器使用**，所以只能作为以后桌面端真实元数据通道的备选，不是本期依赖。

### 5.4 RDP 的帧率上限

微软文档说部分远程显示协议（包括 RDP）支持不超过 30fps；可在 `WinStations` 下配置 `DWMFRAMEINTERVAL=15` 把**上限**调为 60fps，但实际帧率仍取决于应用与硬件（[官方说明](https://learn.microsoft.com/en-us/troubleshoot/windows-server/remote/frame-rate-limited-to-30-fps)）。所以：

- 未设置该值不能推出实际桌面合成率必为 60Hz；需以实际帧龄/客户端计数测量。
- 即使改到 60fps，也不改变本文实测的 BitBlt 成本，也不等于网页能到 60fps。
- 本文不把 DWMFRAMEINTERVAL 修改作为行动项；注册表修改与重启不在本任务范围。

## 6. 手机外网带宽可行性

### 6.1 90KiB JPEG 的实际预算

`速率 = 90KiB × fps`；`Mbit/s = 90×1024×fps×8÷1,000,000`。以下不含少量 HTTP/TCP/IP 与隧道开销，故是**载荷下界**。

| 实际新帧率 | 载荷 MiB/s | 载荷 Mbit/s | 90KiB 单帧在 5Mbps 的纯传输时间（不随帧率改变） | 判定 |
|---|---:|---:|---:|---|
| 1fps | 0.088 | 0.737 | 147.5ms | 状态查看可行；不是流畅操作 |
| 5fps | 0.439 | 3.686 | 147.5ms | 接近占满 5Mbps，波动会明显排队 |
| 10fps | 0.879 | 7.373 | 147.5ms | 需 >7.37Mbps，且不宜跑满；典型建议 5fps 或更低预览分辨率 |
| 15fps | 1.318 | 11.059 | 147.5ms | 需稳定更高码率；不适合低带宽默认档 |
| 30fps | 2.637 | 22.118 | 147.5ms | 外网默认禁用；还会接近当前 RDP 采集上限 |
| 60fps | 5.273 | 44.237 | 147.5ms | **采集和带宽双重不成立** |

网络可支持的理论新帧率上界可写为 `min(采集上限, 可用比特率÷(90KiB×8), 客户端解码上限)`。例如纯载荷的 90KiB 帧在 2/5/10/20Mbps 分别需 **368.6/147.5/73.7/36.9ms**，对应 2.71/6.78/13.56/27.13fps；这还不含 RTT 和排队。

### 6.2 与官方 RDP 数字对比，而非冒用

微软给出的 DS/H.264 AVC444 场景估算中，30fps 视频约 **2.5~3.1Mbps**；网页/Word 等内容又不同（[DS 带宽指南](https://learn.microsoft.com/en-us/azure/virtual-desktop/rdp-bandwidth)）。这些数字可用于说明时间/区域编码的价值，**不可承诺 SoulLink 换 H.264 后一定达到该值**。

**手机默认建议**：静止无新图；普通操作 5fps；交互短时按需 10fps；若实测带宽不足，先降到 960×540/降低 JPEG 质量，再考虑 WebP 或 H.264。WebSocket 解决连接/推送/背压，不会把 90KiB 的 JPEG 变小。

## 7. 唯一行动顺序（投入产出排序）

人日是熟悉当前代码的开发者粗估，含真实场景验证；不是发布承诺。

| 顺序 / 投入 | 动作 | 为什么现在做 / 退出条件 |
|---|---|---|
| **1 / 0.5~1 人日** | 统一测量：BitBlt、光标、FromHbitmap、JPEG、socket、浏览器绘制；每次记录 frameSeq/capturedAt/字节/丢帧。真实 BGI 动态内容采样 | 现有探测近乎单色、口径不同，不能再直接决定大工程。拿不到可信样本就停在优化，不上 H.264 |
| **2 / 1~2 人日** | `frameSeq + afterSeq/ETag`，同帧不传/不解；预览与 Ring 解耦但共享抓屏；录制档独立且默认不破坏现有回放 | 最低风险，直接省重复流量/解码。验收序号、换账号、重连、无观看者；预览默认 5~10fps |
| **3 / 1~2 人日** | 输入驱动的 `CaptureSoon` + 预测光标/按压反馈 + 输入 generation 与取消释放 | 把跟手与抓屏频率分离，不追求 60fps 远端 JPEG。验收输入→新帧 P50/P95，预测与真实位置不混淆 |
| **4 / 1~2 人日** | 预览低分辨率/按需区域小实验；先复用现有 BitBlt，保留旧全尺寸回退 | 直接减像素和带宽；若文字不可读或坐标偏差，停止该档而不是上新协议 |
| **5 / 1~2 人日** | 真实场景比较降帧/缩放/变化检测/必要时脏矩形；共享同一次抓屏，禁止双份采集和双重编码 | 脏矩形只有在总成本（BitBlt+比较+编码+客户端）赢过全帧时保留；不是必做 |
| **6 / 仅新证据触发** | `PrintWindow` 或 Windows.Graphics.Capture 可行性 spike；失败立即回退 | 需要 RDP/游戏内容、遮挡、DPI、权限验证；不提前替换现有采集 |
| **7 / 暂不做** | DXGI、实时 MF H.264、WebRTC、RDP 网关/DVC 媒体方案 | 本机 RDP DXGI 已失败；H.264 不解决抓屏；WebRTC/网关/DVC 工程大且非手机网页捷径。只有当第 1~5 步仍达不到书面目标再立项 |

**不做的替代路线**：把 60fps 配置数字当性能优化；在无新帧时继续下载同一 JPEG；以本地 CSS 光标代替真实反馈验收；以硬件 MFT 枚举/WriteSample 证明实时硬件编码可用。

## 8. 已验证 / 需验证总表

### 已验证或代码可证

- 本机 RDP 的 BitBlt 22~34ms 小样本、30 次均值 31.35ms；当前路径转换/编码也串行发生。
- 本机 RDP 自建 D3D/DXGI duplication 路径失败；WARP duplication 也失败。
- 当前单一 Ring、单一 fps、直播取 Ring 尾帧；网页直连已有串行/退避，静止判据只看 JPEG 字节数。
- 官方 RDP 默认/上限及 DWMFRAMEINTERVAL 配置文档、AVC444 协议结构、DVC 插件机制、DS 带宽估算。
- 90KiB×帧率的码率算术；手机外网必须限实际新帧率。

### 需验证 / 决策

- 真实 BGI 游戏画面的采集分项、实际 fps、P95 帧龄、代理和浏览器 CPU；现有探测不是动态生产证据。
- 事件驱动 `CaptureSoon` 与共享位图/编码的正确性；Ring 回放时间与帧率；多账号争用。
- 2~5fps 录制是否满足用户的取证要求；不愿降级就保留更高录制帧率并承担成本。
- 降分辨率后文字可读性、触控坐标/DPI、局部变化检测漏检率；真实手机 RTT/可用上行/电池。
- RDP 事件 170/162 是否证明当前会话硬件 AVC 确实工作；这不改变“现有代理无法旁路取得其编码流”的结论。
- PrintWindow/WGC、H.264 端到端延迟、真实编码器/GPU 利用、硬件并发数；均未获当前 RDP 场景验证。

## 9. 参考与范围

- 原始数据：`artifacts/rdp-probe/bitblt.txt`、`final.txt`、`dxgi.txt`、`retry.txt`、`capture-validity.txt`、`rdp-report.md`。其中 `rdp-result.txt` 是另一次运行 `exitcode=3`，不能当成功结果引用；不同日志 session/内容有差异，本文不合并成伪精确统计。
- 代码：`Relay/SessionRecorder.cs`、`Relay/AgentWebServer.cs`、`docs/web-index.html`。
- 旧方案：`STREAM-PERF-AUDIT-20261122.md`、`DUAL-RATE-DESIGN-20261122.md`、`MEDIA-CAPABILITY-20261122.md`、`REVIEW-20261122.md`。
- 本轮没有修改 RDP 策略、注册表或生产配置，没有运行新增采集实验，没有改动业务代码/dist/git/发布。
