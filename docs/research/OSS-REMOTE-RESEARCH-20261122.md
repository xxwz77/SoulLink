# 开源远程/串流项目调研：转视角与低延迟抓屏（2026-11-22）

> 日期：2026-11-22；作者：oss-remote（task_mwrrxyhkdi6hxu）；团队：soullink-stream-opt
> 范围：只读调研 + 本文档。未改业务代码、未动 dist、未提交。
> 证据等级：**代码事实**＝已抓取源码逐行核对（file:line 可回查）；**实测**＝SoulLink 已有探测输出；**需验证**＝尚无 SoulLink 端到端证据。
> 网络：GitHub 元数据走 `curl.exe --noproxy "*"`（API 直接连通、rate limit 充足）；raw 源码走系统代理 `-x http://127.0.0.1:7897`（raw.githubusercontent 直连被 reset，代理 200）。

## 0. 结论速览（先看这里）

1. **转视角 = 相对位移，没有第二个答案。** 全行业（Sunshine/Moonlight/Parsec/Steam Link）一致用「相对位移」，绝对定位 `SetCursorPos` 在 3D 游戏里必然失效（游戏锁鼠标后读到的是位移量而非坐标）。SoulLink 的 `RelayAgent.Execute` `moverel`（L694）方向已正确，只差 HTTP 接线。
2. **注入层级有三级，SoulLink 只能落在最低一级：**
   - **驱动级（Sunshine 终极方案）**：libvirtualhid 虚拟 HID 设备，相对位移作为 HID 报告直达驱动层，不带 `LLMHF_INJECTED` 标志，Raw Input 游戏/反作弊友好。**但需要内核驱动 + 授权，且在 RDP 会话内不可用**（HID 设备属于 console session）。
   - **SendInput/mouse_event（SoulLink 现状）**：合成相对位移，进会话输入队列，**在 RDP 会话内可用**；带 INJECTED 标志，严格反注入的游戏会忽略。
   - **结论**：RDP 会话内驱动级注入走不通，`mouse_event(MOUSEEVENTF_MOVE)` 是唯一可行路径——它已实现，接 HTTP 即可先用起来。
3. **抓屏**：DXGI Desktop Duplication 在 RDP 下不可用（SoulLink 已实测 DuplicateOutput 失败）。**Windows.Graphics.Capture（WGC）是唯一「理论可用、尚未实测」的 GPU 路径**——它基于 DWM 合成层而非 GPU 桌面复制，在 RDP 会话内更可能工作，但需用 `CreateForWindow`/`CreateForMonitor` 的正确形态，且帧率可能被 DWM 限制。BitBlt 是当前兜底（22~31ms，60fps 无望，但 15fps 内够用）。
4. **低延迟**：Sunshine/Moonlight 的答案可总结为「无 B 帧 + 无限 GOP/按需 IDR + low-delay 标志 + 客户端丢旧帧 + RFI 而非 IDR 恢复」。这些是纯参数，SoulLink 用 Media Foundation 也能套。
5. **能抄的代码很少且都是 C++/Java（GPL-3.0 不可抄）**；C#/.NET 侧只有 WGC 官方样例（MIT，可抄）和 `nomi-san/parsec-vdd`（MIT，虚拟显示器）。**没有可直接抄进闭源的 C# 串流内核。**

---

## 1. 项目对比表

星数/语言/许可来自 GitHub API 实时查询（2026-11-22）。

| 项目 | 星数 | 语言 | 许可 | 活跃 | 角色 | 关键特性 | 对 SoulLink 价值 |
|---|---|---|---|---|---|---|---|
| **SunshineStream/Sunshine** | 43,736 | C++ | **GPL-3.0** | ✅ 极活跃（当日 push） | 服务端（GameStream host） | DXGI DDAPI + WGC 双后端；NVENC/QSV/AMF/MF/软件五编码器降级；libvirtualhid/ViGEm 输入；FEC 前向纠错 | **最高**（抓屏+编码+低延迟参数+输入注入的完整工程参考，不可抄代码） |
| **moonlight-stream/moonlight-qt** | 19,674 | C++ | **GPL-3.0** | ✅ 活跃 | 客户端（PC/Mac/Linux） | NVST 协议客户端；相对/绝对鼠标双模式；帧队列 | 高（客户端相对位移语义、缓冲策略） |
| **moonlight-stream/moonlight-android** | 7,572 | C/Java | **GPL-3.0** | ✅ 活跃 | 客户端（Android） | 完整 OSC 虚拟按键/摇杆；相对/绝对触摸 | 高（虚拟摇杆数学，不可抄代码） |
| **moonlight-stream/moonlight-common-c** | 587 | C | **GPL-3.0** | ✅ 活跃 | 协议内核 | NVST 包结构、丢帧/RFI 恢复、FEC 队列 | 高（协议与帧队列设计参考） |
| **nomi-san/parsec-vdd** | 5,766 | C# | **MIT** | ✅ 活跃 | 工具 | IddCx 间接显示驱动，免物理显示器跑高刷 | **高**（C#+MIT，虚拟显示器，解决 RDP 会话无物理屏的抓屏前提） |
| **robmikh/SimpleRecorder** | 253 | C# | **MIT** | 官方样例 | 样例 | WGC + Media Foundation 抓屏编码 | **高**（C#+MIT，唯一可抄的 WGC 参考） |
| **robmikh/Win32CaptureSample** | 430 | C++ | MIT | 官方样例 | 样例 | Win32 原生 WGC | 中（Win32 WGC 用法，C++） |
| **GameTechDev/PresentMon** | 2,695 | C++ | MIT | ✅ 活跃 | 工具 | 帧呈现性能分析 | 低（不是串流，但 DDA/WGC 参考） |
| **hecomi/uWindowCapture** | 586 | C++ | MIT | ✅ 活跃 | 库 | Unity 用 WGC/PrintWindow 抓窗口 | 中（WGC 实践，C++） |
| **fqlx/XboxKeyboardMouse** | 325 | C# | Apache-2.0 | 停更 | 工具 | Xbox 串流键鼠注入 | 低（方向是 Xbox 串流，非 RDP） |
| zezba9000/RemoteDesktop | 48 | C# | **GPL-3.0** | 停更 | 远程桌面 | 简单 P2P 远程桌面 | 低（GPL + 简陋） |
| KaramanisDev/RemoteDesktopOverNetwork | 37 | C# | MIT | 停更 | 远程桌面 | LZ4 + 屏幕变化检测 | 低（教学项目） |
| tunaemre/Remote-Controller-Windows | 13 | C# | Apache-2.0 | 停更 | 远程控制 | 键鼠模拟 + 安卓端 | 低（无画面流） |
| **Rainway 系** | — | — | — | **已死** | — | RainwayApp/spitfire（WebRTC datachannel，C++ Apache-2.0）是残骸 | 无（产品已死，仅 spitfire 库可参考 WebRTC datachannel） |
| Parsec 开源替代 | — | — | — | — | — | **无成熟 clone**；OpenParsec（Swift GPL-3.0）只覆盖 iOS 客户端；parsec-vdd 只是虚拟显示器组件 | 无完整替代 |

> 关键判断：**「Parsec 的开源替代」不存在**——Parsec 的核心（低延迟 BUD 协议 + 驱动级输入 + 专用 VDD）全部闭源，社区只有零散组件（parsec-vdd 的虚拟显示器、OpenParsec 的 iOS 客户端），没有一个能拼出完整服务端。Rainway 已死，spitfire 是唯一残存库（WebRTC datachannel，方向是浏览器传输，非串流内核）。

---

## 2. 转视角实现方案对比（用户最关心）

### 2.1 全行业共识：相对位移

游戏里「转视角」的本质是：游戏（尤其 FPS/3D）用 **Raw Input（`WM_INPUT` / `RIM_TYPEMOUSE`）读取鼠标的「相对位移量」（dx/dy），而不是光标坐标**。绝对定位 `SetCursorPos` 只会让游戏看到「光标坐标突变」，视角纹丝不动；把光标锁在屏幕中心 + 连续喂相对位移，游戏才会持续转动视角。

| 方案 | 注入 API | 是否穿透 Raw Input | 是否带 INJECTED 标志 | RDP 会话内可用 | 反作弊友好 | SoulLink 现状 |
|---|---|---|---|---|---|---|
| **驱动级（虚拟 HID）** | 内核驱动写 HID 报告 | ✅ 是（真实设备语义） | ❌ 不带 | ❌（HID 属 console session） | ✅ 最好 | 无（需内核驱动+授权） |
| **SendInput** | `SendInput(MOUSEEVENTF_MOVE)` | ✅ 是 | ✅ 带 `LLMHF_INJECTED` | ✅ 可用 | ❌ 严格游戏会忽略 | 未用（现有代码用 mouse_event） |
| **mouse_event** | `mouse_event(MOUSEEVENTF_MOVE, dx, dy, 0, 0)` | ✅ 是 | ✅ 带（内部走 SendInput） | ✅ 可用 | ❌ 同上 | ✅ **已实现**（RelayAgent L703） |
| 绝对定位 | `SetCursorPos` | ❌ 否（只有坐标） | — | ✅ 可用 | — | ✅ 已实现（move 命令，用于点击） |

### 2.2 开源项目的具体实现（逐项核对）

#### Sunshine（服务端注入）—— 相对位移走「虚拟 HID 驱动」，不是 SendInput

链路：客户端相对鼠标包 → 平台无关分发 → 平台后端注入。

- 协议层：`src/input.cpp:2594-2602` `send_relative_mouse_packet()` 构造 `NV_REL_MOUSE_MOVE_PACKET`（magic `MOUSE_MOVE_REL_MAGIC_GEN5 = 0x07`，`deltaX/deltaY` 各 2 字节 int16）。
- 分发层：`src/input.cpp:808-825` `passthrough(PNV_REL_MOUSE_MOVE_PACKET)` → 调 `platf::move_mouse(input, deltaX, deltaY)`。
- **注入层（关键）**：`src/platform/virtualhid_input.cpp:900-903`：

  ```cpp
  void move_mouse(input_context_t &context, int delta_x, int delta_y) {
    if (context.mouse) {
      log_failure("submit libvirtualhid mouse movement",
                  context.mouse->move_relative(delta_x, delta_y));
    }
  }
  ```

  `context.mouse->move_relative()` 是 **libvirtualhid**（Sunshine 的虚拟 HID 后端）——把相对位移写成一个**虚拟鼠标 HID 设备的报告**，直达驱动层。`src/platform/windows/input.cpp:26` include `libvirtualhid/license.hpp`，`input.cpp:536-544` 检查 `requires_installed_driver` 和 `licensed()`——证明这是**需要安装内核驱动、且有授权门槛**的方案（Windows 上免费的鼠标/键盘虚拟 HID 驱动几乎没有；ViGEm 只做游戏手柄不做鼠标）。

- 绝对定位：`virtualhid_input.cpp:915-934` `abs_mouse()` → `context.mouse->move_absolute(x, y, w, h)`，同样走虚拟 HID。

**对 SoulLink 的启示**：Sunshine 之所以不直接用 `mouse_event`/`SendInput`，是为了①不产生光标位置突变②不给游戏读到 INJECTED 标志。但这条路的代价是内核驱动 + 授权 + console session。**SoulLink 在 RDP 会话内，虚拟 HID 设备根本不会被该会话看见**，所以这条路对 SoulLink 是死路，只能退回 `mouse_event` 相对位移。

#### Moonlight（客户端产生位移）

- PC 客户端 `moonlight-qt/app/streaming/input/mouse.cpp:104-178` `handleMouseMotionEvent()`：
  - 先把同一帧内的多个 `SDL_MOUSEMOTION` 事件**合并累加**（`xrel += event->xrel`，L104-117，省 CPU + 避免丢位移）。
  - 相对模式（L176-178）：`LiSendMouseMoveEvent(xrel, yrel)` —— 发相对位移。
  - 绝对模式（L122-175）：发 `LiSendMousePositionEvent`，并做视频区裁剪/边界钳制。
  - 结论：**转视角时 Moonlight 切「相对模式」，把系统鼠标相对位移直发服务端**，与 SoulLink `moverel` 语义一致。
- 移动端 `moonlight-android/.../touch/RelativeTouchContext.java:253-294`：单指拖动 → 累加 dx/dy → `conn.sendMouseMove(deltaX, deltaY)`（相对）或 `sendMouseMoveAsMousePosition`（绝对）。`TAP_MOVEMENT_THRESHOLD=20`、`TAP_DISTANCE_THRESHOLD=25`、`TAP_TIME_THRESHOLD=250`、`DRAG_TIME_THRESHOLD=650`（L87-91）用于区分「点击/拖动/滚动」。

### 2.3 「SetCursorPos 导致游戏鼠标被捕获」的坑怎么解决

SoulLink 已经踩过并解决（`Relay/RelayAgent.cs:711-714` 的注释是现成证据）：

- **坑**：旧实现「光标贴边就 `SetCursorPos` 回屏幕中心」，游戏看到光标位置突变，表现为「画面被猛地拽一下」。
- **解决**：相对位移模式下**完全不做回中/贴边纠正**，只发连续相对位移。
- Moonlight 同款：相对模式下 `updatePointerRegionLock()`（`mouse.cpp:285-336`）直接 early-return，不碰光标位置。

### 2.4 核心问题：RDP 会话里相对位移能不能送到游戏？

**能送，但有边界，结论分三层：**

1. **能送到会话内游戏**：RDP 会话有自己的输入队列，`mouse_event(MOUSEEVENTF_MOVE)` 在该会话内注入的相对位移会进队列，会话内跑的游戏能读到。这正是 SoulLink 现有 `moverel` 的工作原理（`RelayAgent` 部署在目标会话内，注释 L19 明确「必须**在目标会话内部**调用 mouse_event/SetCursorPos」）。
2. **能送但带 INJECTED 标志**：合成输入的相对位移会被标记 `LLMHF_INJECTED`。**读 Raw Input 但不检查该标志的游戏**（大多数普通游戏）正常转视角；**主动拒绝注入输入的游戏/反作弊**（少数）会忽略。
3. **驱动级注入不可用**：RDP 会话内无法挂虚拟 HID 设备（HID 属 console session），Sunshine 的终极方案在 SoulLink 场景下不成立。

**行动含义**：先用现有 `moverel`（mouse_event 相对位移）接上 HTTP，满足「读 Raw Input 的普通游戏」；对「严格反注入的游戏」在 RDP 会话内没有完美解，这是 RDP 架构的硬边界，需在文档里明示（不要给用户画饼）。

---

## 3. 抓屏方案对比（WGC vs DXGI vs BitBlt）

### 3.1 三种方案在 RDP 会话下的可用性

| 方案 | 原理 | RDP 会话可用性 | 实测/依据 | 帧率上限 | 备注 |
|---|---|---|---|---|---|
| **BitBlt（GDI）** | CPU 从显存拷贝桌面位图 | ✅ **可用** | SoulLink 实测：单抓屏 22.42~34.40ms，均值 27.168ms，30 次均值 31.35ms（`STREAM-PLAN-V2` §1.1） | ~32 抓屏/秒（不含编码） | 当前兜底；CPU 密集，60fps 无望 |
| **DXGI Desktop Duplication（DDAPI）** | GPU 桌面复制，零拷贝显存帧 | ❌ **不可用** | SoulLink 实测：硬件 D3D 设备 `E_OUTOFMEMORY`；`DuplicateOutput` 失败（`STREAM-PLAN-V2` §1.1） | 理论 60fps+ | 需要「真实 GPU 桌面复制」，RDP 会话无此通道 |
| **Windows.Graphics.Capture（WGC）** | DWM 合成层捕获（WinRT） | ⚠️ **待实测（理论可用）** | 尚无 SoulLink 证据 | 理论 60fps，但受 DWM 限制 | 见 3.2 |

### 3.2 Sunshine 的 WGC 后端（这是 SoulLink 该抄的思路）

`src/platform/windows/display.h:760-806` 声明 `wgc_capture_t`，实现 `src/platform/windows/display_wgc.cpp:103-174` `wgc_capture_t::init()`：

1. `winrt::GraphicsCaptureSession::IsSupported()` 检查（L108）。
2. **用 `IGraphicsCaptureItemInterop::CreateForMonitor` 绑定显示器**（L129-133）。—— 注意：RDP 会话可能没有物理 monitor，此形态可能失败，需改 `CreateForWindow` 或 `GraphicsCapturePicker` 捕获窗口。
3. `Direct3D11CaptureFramePool::CreateFreeThreaded(device, DXGI_FORMAT_B8G8R8A8_UNORM, 2, item.Size())`（L142）——**帧池只开 2 帧**，意味着「只留最新一帧，旧帧被覆盖」。
4. `MinUpdateInterval(4ms)`（L159-160，即 250Hz 上限）、`IsBorderRequired(false)`（L150，去掉黄框）、`IsCursorCaptureEnabled(bool)`（L244-253，游标合成）。
5. 生产者/消费者：`on_frame_arrived`（L181-199）→ `TryGetNextFrame` → 存到**单槽 `produced_frame`**（覆盖旧帧）→ 条件变量唤醒捕获线程；`next_frame`（L205-234）阻塞等帧。

**关键设计**：WGC 用「单帧槽 + 覆盖旧帧」实现天然的**丢旧帧策略**（下游编码慢时，新帧直接顶掉旧帧，不排队积压延迟）。

**后端选择顺序** `src/platform/windows/display_base.cpp:1029-1064` `display()`：先试 `ddx`（DDAPI），失败再试 `wgc`，两者都失败返回 null。`display_wgc_ram_t`（软件编码）vs `display_wgc_vram_t`（硬件编码）由 `hwdevice_type` 决定。

### 3.3 WGC 在 RDP 下能不能用？（诚实结论）

- **DXGI DDAPI 失败 ≠ WGC 失败**：两者是独立 API。DDAPI 要求「真实 GPU 桌面复制通道」，RDP 会话没有；WGC 走 DWM 合成层，RDP 会话有自己的 DWM，因此 **WGC 在 RDP 会话内理论上更可能工作**（OBS 的窗口捕获在 RDP 下部分可用是旁证，但 OBS 用的是 hook 而非纯 WGC）。
- **风险点**：`CreateForMonitor` 在无物理显示器的 RDP 会话下可能失败（没有 `Monitor` 句柄）；需改用 `CreateForWindow(HWND)` 或 `GraphicsCapturePicker`。
- **待办**：SoulLink 应写一个最小 WGC 探测（C# 可直接用 `Windows.Graphics.Capture` + `GraphicsCapturePicker`/`CreateForWindow`，见 §6 SimpleRecorder），在 RDP 会话内实测 `IsSupported()` 与能否拿到帧。这是**下一步最值得花 1 小时做的实验**。

### 3.4 给 SoulLink 的抓屏建议

- **保底 BitBlt**：当前已跑通，15fps 内够用，不要推翻。
- **攻坚 WGC**：RDP 会话内唯一可能的 GPU 抓屏路径。C# 用 `Windows.Graphics.Capture`（`GraphicsCaptureItem.CreateForWindow` + `Direct3D11CaptureFramePool`），参考 `robmikh/SimpleRecorder`（MIT，可抄）。
- **搭配 parsec-vdd（MIT，C#）**：`nomi-san/parsec-vdd` 用 IddCx 在 Windows 上创建**虚拟显示器**。若 RDP 会话因「无物理屏」导致 WGC/DDA 都失败，挂一个虚拟显示器可能同时救活 DDA 和 WGC 的 `CreateForMonitor` 路径。这是「免物理屏跑串流」的业界标准答案（Parsec/Sunshine 都依赖它），**值得作为 B 计划验证**。

---

## 4. 低延迟关键设计

### 4.1 帧队列：丢旧帧，不排队

- **Sunshine（服务端）**：WGC 帧池开 2 + 单槽覆盖（`display_wgc.cpp:142, 181-199`），下游编码慢时新帧顶掉旧帧。
- **Moonlight（客户端）**：`moonlight-common-c/src/VideoDepacketizer.c:88-90` `LbqInitializeLinkedBlockingQueue(&decodeUnitQueue, 15)` —— 解码单元队列上限 **15 帧**；溢出时（L534-563）**flush 整条队列 + 请求 IDR**，绝不无限排队。丢帧计数 `CONSECUTIVE_DROP_LIMIT=120`（L56），连续丢满 120 帧强制请求 IDR 重同步（L151-161）。

**结论**：帧队列必须**有界 + 丢旧保新 + 溢出即重同步**，宁可花屏一瞬也不积累延迟。SoulLink 的 JPEG 链路目前是「取尾帧 O(1)」（`STREAM-PLAN-V2` 已指出 `SessionRecorder.cs:368-377` 取尾），语义上已经是「丢旧帧」，方向正确。

### 4.2 编码参数（Sunshine `src/video.cpp` 逐项）

- **无 B 帧**：`ctx->max_b_frames = 0`（L2152，注释「B-frames delay decoder output, so never use them」）。
- **无限 GOP + 按需 IDR**：`ctx->gop_size = INT_MAX`、`keyint_min = INT_MAX`（L2161-2164，注释「infinite GOP length since I-frames are generated on demand」）；只有不支持按需 IDR 的 Media Foundation 才退化为固定 `gop_size = 120`（L2156-2159，~2 秒@60fps）。
- **低延迟标志**：`ctx->flags |= AV_CODEC_FLAG_CLOSED_GOP | AV_CODEC_FLAG_LOW_DELAY`（L2178）；`ctx->flags2 |= AV_CODEC_FLAG2_FAST`（L2180）。
- **NVENC**：`zerolatency=1`、`tune=NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY`、可配 `preset`（L853-857）。
- **QSV/AMF/MF/软件**：同样走 CBR + 低延迟（L933-1286，`mediafoundation` 用 `rate_control=cbr`）。
- **码率**：CBR（`rc_max_rate = rc_min_rate = bitrate`，L2314-2323，注释「Ensure rc_max_bitrate != bit_rate to force VBR mode」说明默认 VBR、拉满到 CBR）。

**对 SoulLink 的启示**（Media Foundation / NVENC MFT）：MF 是 SoulLink 已在用的编码栈（`STREAM-PLAN-V2` 提到 NVENC MFT 已枚举到）。MF 参数映射：B 帧关闭、GOP 调大（或低延迟模式）、`MFVideoUsage`/低延迟 profile、CBR。关键帧间隔（keyframe interval）要**调大**（避免周期性大 IDR 帧卡顿），配合「丢帧后主动请求关键帧」。

### 4.3 客户端缓冲策略

- Moonlight 端到端延迟控制靠「解码单元队列 15 帧上限 + 溢出丢旧 + IDR/RFI 重同步」。
- **RFI（参考帧失效）优于请求 IDR**：`VideoDepacketizer.c:128-164` `dropFrameState()` —— 丢帧后若能走 RFI（`waitingForRefInvalFrame`）就只失效参考帧，否则请求完整 IDR（大帧、易拥塞）。注释 L1187「Requesting an IDR frame while the network is unstable will just contribute to congestion collapse」。
- **丢帧检测**：`connectionDetectedFrameLoss(startFrameNumber, frameIndex)` 上报，服务端据此触发 RFI/IDR（Sunshine 侧 `stream.cpp:1221-1235` `IDX_REQUEST_IDR_FRAME` / `IDX_INVALIDATE_REF_FRAMES` 处理）。
- **FEC 前向纠错**：Sunshine 发视频时用 Reed-Solomon FEC（`stream.cpp:801-950` `fec::encode`，`fec_percentage` 可配，L1685），允许丢包不重传。

---

## 5. 可借鉴的具体代码位置（file:line）

> 标注许可。**GPL-3.0 的（Sunshine/Moonlight）只能借鉴「思想/参数/结构」，不得复制源码进闭源 SoulLink**；MIT/Apache 的可直接参考。

### 5.1 转视角（相对位移）

| 位置 | 内容 | 许可 | 用途 |
|---|---|---|---|
| `Relay/RelayAgent.cs:693-724` | SoulLink 已实现的 `moverel`（大位移拆段 + 余数补发） | 自有 | **现状基线，接 HTTP 即用** |
| `Relay/RelayAgent.cs:38,82-83` | `mouse_event` P/Invoke + `MOUSEEVENTF_MOVE` 常量 | 自有 | 已就位 |
| Sunshine `src/input.cpp:2594-2602` | `NV_REL_MOUSE_MOVE_PACKET` 相对包构造（deltaX/deltaY int16） | GPL | 协议字段设计参考 |
| Sunshine `src/input.cpp:808-825` | 相对包 → `platf::move_mouse` 分发 | GPL | 分发语义参考 |
| Sunshine `src/platform/virtualhid_input.cpp:900-903` | `move_relative()` 驱动级注入 | GPL+专有驱动 | **证明驱动级方案，RDP 不可用** |
| moonlight-qt `app/streaming/input/mouse.cpp:104-178` | 多事件合并累加 + 相对/绝对切换 | GPL | 「合并同帧位移」技巧可借鉴思想 |
| moonlight-android `touch/RelativeTouchContext.java:253-294` | 触屏拖动 → 相对位移 | GPL | 移动端手势→位移映射思想 |

### 5.2 抓屏

| 位置 | 内容 | 许可 | 用途 |
|---|---|---|---|
| Sunshine `src/platform/windows/display_wgc.cpp:103-174` | WGC 初始化（IsSupported/CreateForMonitor/帧池2/MinUpdateInterval4ms） | GPL | WGC 完整流程参考 |
| Sunshine `src/platform/windows/display_wgc.cpp:181-234` | 单槽覆盖 + 条件变量生产消费 | GPL | 丢旧帧结构参考 |
| Sunshine `src/platform/windows/display_base.cpp:1029-1064` | 后端选择顺序（ddx→wgc） | GPL | 降级链设计参考 |
| **robmikh/SimpleRecorder `MainPage.xaml.cs` + `RecordingPage.xaml.cs` + `D3DDeviceManager.cs`** | **C# WGC + MF 抓屏编码完整样例** | **MIT** | **可直接参考/移植** |
| **nomi-san/parsec-vdd** | **C# IddCx 虚拟显示器** | **MIT** | **可直接用，解决无物理屏** |
| Sunshine `src/platform/windows/misc.cpp:247-264` | `syncThreadDesktop`（OpenInputDesktop+SetThreadDesktop） | GPL | **关键**：抓屏前同步到输入桌面，RDP/服务会话必须 |

### 5.3 低延迟编码参数

| 位置 | 内容 | 许可 | 用途 |
|---|---|---|---|
| Sunshine `src/video.cpp:2152` | `max_b_frames = 0` | GPL | 参数（思想） |
| Sunshine `src/video.cpp:2156-2165` | 无限 GOP / MF 固定 120 | GPL | 参数（思想） |
| Sunshine `src/video.cpp:2178-2180` | LOW_DELAY + FAST 标志 | GPL | 参数（思想） |
| Sunshine `src/video.cpp:853-857` | NVENC zerolatency + ULTRA_LOW_LATENCY | GPL | 参数（思想） |
| moonlight-common-c `src/VideoDepacketizer.c:88-90, 534-563` | 帧队列 15 + 溢出丢旧 + IDR | GPL | 队列设计（思想） |
| moonlight-common-c `src/VideoDepacketizer.c:128-164` | RFI 优于 IDR 恢复 | GPL | 恢复策略（思想） |

### 5.4 虚拟按键/摇杆（移动端）

> 已由 `VIRTUAL-CONTROLS-RESEARCH-20261122.md` 详细覆盖，此处只列工程要点。

| 位置 | 内容 | 许可 | 用途 |
|---|---|---|---|
| moonlight-android `virtual_controller/AnalogStick.java:256-281` | 摇杆极坐标 + 死区 + `timeoutDeadzone=150ms` 死区粘滞 | GPL | 摇杆数学（思想） |
| moonlight-android `virtual_controller/VirtualController.java:201-214` | 手柄状态重传（25/50/75ms 防丢包） | GPL | 状态可靠性（思想） |
| moonlight-android `virtual_controller/RightAnalogStick.java:17-24` | 右摇杆轴 → `0x7FFE` 量化 | GPL | 轴量化（思想） |

---

## 6. 许可协议风险提示

| 项目 | 许可 | 能否用于 SoulLink | 说明 |
|---|---|---|---|
| **Sunshine** | **GPL-3.0** | ❌ **不可抄代码** | 抓屏/编码/输入的实现细节可借鉴「思想与参数」，但**任何源码复制都会让 SoulLink 被迫 GPL** |
| **moonlight-qt / -android / -common-c** | **GPL-3.0** | ❌ **不可抄代码** | 同上；协议字段/队列容量/摇杆数学是「思想」，可借鉴不可搬运 |
| **robmikh/SimpleRecorder** | **MIT** | ✅ 可抄 | **唯一可安全参考的 C# WGC+MF 完整样例** |
| **nomi-san/parsec-vdd** | **MIT** | ✅ 可用 | C# 虚拟显示器，可直接集成或参考 |
| **robmikh/Win32CaptureSample** | MIT | ✅ 可参考 | C++ WGC（若走 C++/CLI 桥） |
| **hecomi/uWindowCapture** | MIT | ✅ 可参考 | C++ WGC 实践 |
| zezba9000/RemoteDesktop | GPL-3.0 | ❌ 不可抄 | 且简陋 |
| KaramanisDev/RemoteDesktopOverNetwork | MIT | ✅ 可参考 | 但只有 LZ4+变化检测，价值低 |
| tunaemre/Remote-Controller-Windows | Apache-2.0 | ✅ 可参考 | 键鼠模拟，无画面流 |

**底线**：SoulLink 的串流内核（抓屏 + 编码 + 输入注入）必须**自研 C#**，只借鉴 GPL 项目的「公开参数/结构/设计思想」。可安全引入的第三方代码仅限 MIT/Apache 的 C# WGC 样例（SimpleRecorder）与虚拟显示器（parsec-vdd）。**不得**引入任何 GPL-3.0 源码，也不得依赖无明确许可（NOASSERTION）的仓库。

---

## 7. 给 SoulLink 的 3 条最具体建议

### 建议 1（最高优先级）：先把 `moverel` 接上 HTTP，转视角立即可用

- **现状**：`RelayAgent.Execute` L694 已实现 `moverel` 相对位移（大位移拆段 + 余数补发），`AgentWebServer.cs:306` 只解析了 `Move`（move 命令的联动标志），**HTTP 层没有 `moverel` 端点接线**。
- **动作**：在 `AgentWebServer.cs` 增加 `moverel` 命令解析（`dx`/`dy` → `req.Dx`/`req.Dy`），透传到 `Execute` 的 `case "moverel"`。
- **为什么是它**：这是「读 Raw Input 的普通 3D 游戏」在 RDP 会话内唯一可行且已就位的路径；驱动级注入（Sunshine libvirtualhid）在 RDP 内不可用，无需投入。
- **明示边界**：对主动拒绝 INJECTED 输入的严格反作弊游戏，RDP 会话内无完美解（架构硬边界），文档如实告知用户，不画饼。

### 建议 2：花 1 小时写最小 WGC 探测，验证 RDP 会话内能否 GPU 抓屏

- **背景**：DXGI DDAPI 已实测 RDP 不可用；WGC 是唯一理论可用的 GPU 路径，但需实测。
- **动作**：C# 用 `Windows.Graphics.Capture`（`GraphicsCaptureItem.CreateForWindow(HWND)`，**不要用 `CreateForMonitor`**——RDP 会话可能无物理屏）+ `Direct3D11CaptureFramePool`，在 RDP 会话内跑 `IsSupported()` + 抓 1 帧验证。参考 `robmikh/SimpleRecorder`（MIT，可抄）与 Sunshine `display_wgc.cpp:103-174` 的流程。
- **成功则**：替换 BitBlt，帧率上限从 ~32 抓屏/秒提到 DWM 上限，60fps 才有戏。
- **失败则**：回到 BitBlt 保底 + 尝试 `nomi-san/parsec-vdd`（MIT，C#）挂虚拟显示器再试 WGC/DDA（Parsec/Sunshine 免物理屏的标准答案）。

### 建议 3：编码参数照抄 Sunshine 的低延迟套路（纯参数，无许可风险）

对 Media Foundation / NVENC MFT 编码做三件事，立竿见影降延迟：

1. **关 B 帧**（MF 对应低延迟 profile / `MFVideoUsage`），B 帧会额外引入解码器重排延迟。
2. **GOP 调大 + 支持「丢帧后主动请求关键帧」**：避免周期性大 IDR 帧的卡顿尖峰；服务端暴露「请求关键帧」能力，客户端检测到丢帧时触发（对标 Sunshine 按需 IDR + Moonlight 的 `LiRequestIdrFrame`）。
3. **帧队列有界 + 丢旧保新 + 溢出重同步**：客户端解码/渲染队列设上限（Moonlight 用 15），满则丢最旧帧并请求关键帧，绝不无限排队积累延迟。

---

## 附：关键结论的一句话总结

- **转视角最佳方案**：`mouse_event(MOUSEEVENTF_MOVE)` 相对位移（SoulLink `RelayAgent` L694 已实现，接 HTTP 即可）；驱动级虚拟 HID（Sunshine 方案）在 RDP 会话内不可用。
- **抓屏最佳方案**：短期 BitBlt 保底（22~31ms），中期实测 WGC（`CreateForWindow` 形态，RDP 会话内唯一可能的 GPU 路径），B 计划用 `parsec-vdd`（MIT）挂虚拟显示器救活 GPU 抓屏。
- **能借鉴的代码**：GPL 的 Sunshine/Moonlight 只能借「参数/结构/思想」；可安全移植的是 `robmikh/SimpleRecorder`（MIT，C# WGC+MF）与 `nomi-san/parsec-vdd`（MIT，C# 虚拟显示器）。
