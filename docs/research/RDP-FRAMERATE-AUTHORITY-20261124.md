# RDP 帧率与渲染后端权威查证：微软官方限制、策略组、GPU vs CPU

> 日期：2026-11-24
> 作者：deep-parsec（dsh-team `team_mwtjn0d77v8gtr` / member `m_mwtkvdn1e6jqik` / task `task_mwtm69mzi808zr`）
> 性质：**纯查证 + 文档，未改任何产品代码、未碰运行进程、未写注册表**（本机注册表只读检查）。
>
> **证据分级**：
> - 【官方文档】＝微软 Learn / Microsoft Q&A 页面原文（本次 HTTP 200 直接抓取，URL 附后）。
> - 【实测】＝本仓库 `docs/research/` 前序实测（附文档出处）。
> - 【本机只读】＝本次用 PowerShell 只读查询本机注册表/策略值（不修改）。
> - 【推断】＝有依据的推导，推导过程写明。
> - 【未查到】＝一手来源无法访问或不存在，明确标注。

---

## 0. 一句话结论

**微软官方白纸黑字确认：RDP 远程会话默认帧率上限 30fps；可通过注册表 `DWMFRAMEINTERVAL` 把上限提到 60fps，但那是"上限"不是"实际值"——实际帧率由「应用产帧率 × DWM 合成率 × RDP 编码管线预算（官方口径：平均编码时间须 <33ms → 上限 30fps）」共同决定。** 对 SoulLink：**RDP 会话内帧率天花板 ≈ 60fps（需改注册表+重启），且只有「游戏自己渲染 60fps + 事件驱动取帧（WGC）」时才摸得到；当前 DDA 轮询实测只有 ~14fps，瓶颈是"RDP 会话 DWM 合成是变化驱动"而非协议上限本身。**

---

## 1. 问题 (1)：Windows RDP 是否有官方帧率上限？——**有，30fps（默认）**

### 1.1 官方原文（KB 2885213）

来源：[Frame rate is limited to 30 FPS in Windows-based remote sessions](https://learn.microsoft.com/en-us/troubleshoot/windows-server/remote/frame-rate-limited-to-30-fps)（本次 HTTP 200 直取全文）

> **Summary**: Some remote display protocols, including Remote Desktop Protocol (RDP), don't support a frame rate that is greater than 30 frames per second (FPS).
>
> **Setting frame rates**: To work around the issue, create a **DWMFRAMEINTERVAL** entry in registry subkey `HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations`... Type 15 in the Value data box... **This sets the maximum frame rate to 60 FPS**.
>
> **Note**: The registry entry ... sets the **maximum** frame rate limit that the remote display protocol can deliver to the remote session client to 60 FPS. **This setting does not set the actual frame rate** for the remote session client. The actual frame rate depends on other factors such as application and computer hardware resources.

要点逐条：
| 项 | 官方口径 | 等级 |
|---|---|---|
| 默认上限 | **30fps**（RDP 类远程显示协议不支持更高） | 【官方文档】 |
| 提升方法 | `HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations` 下 `DWMFRAMEINTERVAL`（DWORD，十进制） | 【官方文档】 |
| 数值语义 | 值 15 → 上限 60fps（即"15 个 DWM 帧间隔"，每帧 1/60s） | 【官方文档】+【推断：数值 15 = 60Hz 下 DWM 每 15 个合成间隔放行一次全量帧，机制细节官方未展开，标注推断】 |
| 语义边界 | 只改"上限"，不保证"实际帧率"；实际取决于应用与硬件资源 | 【官方文档】 |
| 生效条件 | 重启计算机 | 【官方文档】 |

### 1.2 第二个官方口径：RDP 编码管线自身 33ms 预算

来源：[Diagnose graphics performance issues Remote Desktop（RemoteFX Graphics 性能计数器文档）](https://learn.microsoft.com/en-us/azure/virtual-desktop/remotefx-graphics-performance-counters)（本次 HTTP 200 直取全文）

> **Average Encoding Time should be under 33 ms.** ... **Because DS supports an Average Encoding Time of 33 ms, it supports an input frame rate up to 30 frames/second.** Note that 33 ms is the maximum supported frame rate. In many cases, the frame rate experienced by the user will be lower, **depending on how often a frame is provided to DS by the source**.

> 注：DS（Direct Stream）是文档对 RDP 图形管线的现称（旧文写 RDP/RemoteFX）。文档 Applies to 为 AVD/Windows 365，但计数器与编码管线机制同源于 RDS TermServ 栈【推断：AVD 就是托管 RDS；计数器名"RemoteFX Graphics"即历史 RDP 图形栈】。

**这解释了 30fps 的来源：1/30s = 33.3ms 编码预算，RDP 图形管线（编码为同步过程）按此预算设计。**【官方文档】+【推断：33ms↔30fps 的换算关系是官方原文自己给出的】

### 1.3 DWMFRAMEINTERVAL 实际效果的用户实证（Microsoft Q&A）

- [Q&A：23H2 升级后设了 DWMFRAMEINTERVAL=15 仍 30fps](https://learn.microsoft.com/en-us/answers/questions/1403785/after-upgrading-to-23h2-the-frame-rate-is-still-on)（HTTP 200 直取）：多个用户报告**仅设 DWMFRAMEINTERVAL 不生效**；微软支持给出的补充答案：若装了 RD Session Host 角色，还需 `fQueryUserConfigFromDC=1`（RCM legacy 模型）并重启 Remote Desktop Service。→ 说明该值受 RDS 会话协商层（RCM）牵制，不是单一开关【官方文档（Q&A 支持答案）】。
- [Q&A：Mac 客户端 30fps 限制](https://learn.microsoft.com/en-us/answers/questions/2082846/rdp-frame-rate-limitation-on-m1-macbook-air-runnin)（HTTP 200 直取）：用户反馈 DWMFRAMEINTERVAL 在 Windows 客户端有效（"it works for win"）、Mac 客户端无效——**上限还受客户端协议实现限制**【官方文档（Q&A 用户反馈，非官方承诺）】。

### 1.4 本机现状【本机只读】

- `HKLM\...\Terminal Server\WinStations`：**无 DWMFRAMEINTERVAL 值** → 本机当前处于官方默认路径（**上限 30fps**）。本研究只读未修改。

---

## 2. 问题 (2)：会话内桌面合成率由什么决定？

### 2.1 官方机制描述

来源：[Desktop Window Manager（DWM）overview](https://learn.microsoft.com/en-us/windows/win32/dwm/dwm-overview)（本次 HTTP 200 直取全文）：

> When desktop composition is enabled, individual windows no longer draw directly to the screen... their drawing is **redirected to off-screen surfaces in video memory, which are then rendered into a desktop image and presented on the display**. Desktop composition is performed by DWM.

会话模型来源：[Remote and console terminal differences](https://learn.microsoft.com/en-us/windows/win32/termserv/consoles-vs-terminals)（HTTP 200 直取）：

> A remote terminal... created by protocol providers (RDP, Citrix, VMware...) that integrate with the Remote Desktop Services Interface. The input/output devices associated with this are considered "remote."

### 2.2 结论（分级标注）

| 因素 | 作用 | 等级 |
|---|---|---|
| **物理显示器刷新率** | 决定 console 会话 DWM 的合成节拍（60Hz → 16.7ms/vsync） | 【推断】（DWM 文档只说"presented on the display"，节拍与刷新率的关系是图形栈常识；官方文档无一句话直说"RDP 会话合成率=显示器刷新率"，故标推断） |
| **RDP 会话的"虚拟刷新率"** | RDP 会话没有物理显示器；DWM 合成节拍由 **TermServ/DWM 协商的帧间隔（DWMFRAMEINTERVAL，默认 30fps）** 决定，物理显示器刷新率不直接约束 RDP 会话 | 【推断，依据强】依据 = DWMFRAMEINTERVAL 的存在本身（它就是 RDP 会话里 DWM 的合成间隔参数）+ 本仓库实测（见 §4） |
| RDP 协议协商 | 会话建立时协商图形管线参数（编码模式、色彩、带宽自适应） | 【官方文档】（rdp-properties/graphics-encoding 文档体系） |
| 策略组 | 可改变编码/GPU 路径（见 §3），**没有一个策略直接叫"帧率"** | 【官方文档】（政策列表核对，见 §3.4） |
| 应用产帧率 | **最终实际帧率的第一个必要条件**：源（游戏/视频）产多少帧，管线才有多少帧可送 | 【官方文档】（RemoteFX 计数器文档原文"depending on how often a frame is provided to DS by the source"） |

**综合（回答"由什么决定"）**：RDP 会话内的桌面合成率 = min(应用产帧率, DWM 合成间隔（DWMFRAMEINTERVAL，默认 30fps）, RDP 编码管线吞吐（33ms/帧预算）)——每一项都是必要条件。物理显示器刷新率决定的是 console 会话，不直接决定 RDP 会话。

---

## 3. 问题 (3)：策略组 RDP 相关设置的作用与取值差异

### 3.1 官方政策清单（来源均为本次 HTTP 200 直取）

**主来源**：[Enable GPU acceleration for Azure Virtual Desktop](https://learn.microsoft.com/en-us/azure/virtual-desktop/graphics-enable-gpu-acceleration)

官方原文要点（GPO 路径 `Computer Configuration > Policies > Administrative Templates > Windows Components > Remote Desktop Services > Remote Desktop Session Host > Remote Session Environment`）：

| 策略 | 官方作用描述 | 对帧率的影响 | 等级 |
|---|---|---|---|
| **Use hardware graphics adapters for all Remote Desktop Services sessions** | **GPU 加速应用渲染**：让会话内应用用 GPU 渲染（WDDM 路径），而不是默认 **"remote sessions are rendered with the CPU and don't use available GPUs"**（官方原文：默认远程会话用 CPU 渲染、不用 GPU） | 间接：GPU 渲染更快 → 应用产帧率上限提高；不直接改 DWM 合成间隔 | 【官方文档】 |
| **Configure H.264/AVC hardware encoding for Remote Desktop connections** | **GPU 加速帧编码**（RDP 管线内部 AVC 编码走硬件） | 间接：降低 Average Encoding Time（33ms 预算内吞吐更大），但**上限仍受 DWMFRAMEINTERVAL 与 30fps 编码预算约束** | 【官方文档】 |
| **Prioritize H.264/AVC 444 Graphics mode for Remote Desktop connections** | 优先 444 色度（文本画质）；官方 Note：**只勾 444 不勾硬件编码 → 会落到"software/CPU-based H.264/AVC full-screen video encoding"** | 画质/带宽取向，不直接改帧率 | 【官方文档】 |
| **Configure H.265/HEVC hardware encoding**（AVD 节点下） | HEVC 硬编；两者同开时 HEVC 优先 | 同上，编码效率 | 【官方文档】 |
| Configure image quality for RemoteFX Adaptive Graphics（配套策略） | 画质档位（本机 ImageQuality=3） | 无直接帧率关系 | 【官方文档（chroma 文档提到配套勾选）】+【本机只读】 |

**关键官方原文**（graphics-enable-gpu-acceleration）：
> By default, Azure Virtual Desktop remote sessions are **rendered with the CPU** and don't use available GPUs.
> Checking the box to Prioritize H.264 Graphics mode **without** checking the box to Configure hardware encoding can result in **software/CPU-based** H.264/AVC full-screen video encoding.

### 3.2 满屏视频模式（mixed-mode vs full-screen）

来源：[Graphics encoding over Direct Stream](https://learn.microsoft.com/en-us/azure/virtual-desktop/graphics-encoding)（HTTP 200 直取）

> A full-screen video profile **provides a higher frame rate and better user experience**, but uses more network bandwidth and resources... It benefits applications such as 3D modeling, CAD/CAM, or video playback and editing.

→ 官方确认「全屏视频模式（AVC/H.264 或 HEVC 编码整屏）帧率更高」——这正对应 SoulLink 场景（游戏画面），也说明**为什么 SoulLink 的 DDA/WGC 抓帧 + NVENC 独立管线在帧率潜力上优于 RDP 自带 mixed-mode（文本/图像分codec）路径**【官方文档 + 推断】。

### 3.3 没有找到的（诚实标注）

- **策略组里没有一个直接命名"frame rate / 帧率"的 RDP 策略**：`learn.microsoft.com` search API 检索 "frame rate policy RDP" 未命中任何 GPO 定义页；帧率上限只存在于注册表 `DWMFRAMEINTERVAL`（非 GPO）。相关 GPO 定义页（如 use-hardware-graphics-adapters...html）在 learn.microsoft.com 上 **404 / 未查到**（多个候选 URL 均 404；AVD 指南页是这些策略最完整的官方描述载体）。【官方文档（AVD 指南）+ 未查到（单独 GPO 定义页）】
- "Set priority for graphics rendering"（GPU vs CPU 渲染优先级）单独定义页：**未查到**（404）。其存在与作用在 AVD 指南的政策组合描述中有间接覆盖。

### 3.4 本机当前策略值【本机只读】

`HKLM\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services`（只读查询，未修改）：

| 值 | 现值 | 对应含义 |
|---|---|---|
| `bEnumerateHWBeforeSW` | 1 | 硬件图形适配器优先（≈"Use hardware graphics adapters"等效启用） |
| `AVC444ModePreferred` | 1 | AVC444 优先 |
| `AVCHardwareEncodePreferred` | 1 | AVC 硬件编码优先 |
| `fEnableWddmDriver` | 1 | RDP 会话内启用 WDDM GPU 路径 |
| `GraphicsProfile` | 2 | 图形质量档位 |
| `ImageQuality` | 3 | RemoteFX 画质档 |

→ **本机已经把「GPU 渲染 + AVC 硬编 + 444」全开**（这是 SoulLink 团队此前配置的）。也就是说：本机 RDP 会话内 GPU 路径已启用，但**帧率仍受 DWMFRAMEINTERVAL 默认 30fps 上限与源产帧率约束**——与本仓库实测（§4）一致。

---

## 4. 问题 (4)：DDA 在 RDP 会话内的行为与物理会话的差异

### 4.1 官方文档口径

来源：[Desktop Duplication API](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api)（本次 HTTP 200 直取全文）：

- DDA 复制的是**桌面图像**（"provides remote access to a desktop image"、"frame-by-frame updates to the desktop"），格式恒为 `B8G8R8A8_UNORM`。
- 更新语义：**变化驱动**（dirty rects / move rects），"The operating system starts to accumulate the updates by coalescing them" 当积累溢出时合并区域。
- **该官方页面完全没有提及 remote session / RDP / Terminal Services**（关键词计数 = 0，本次逐词核对）——即：**微软官方文档对"DDA 在 RDP 会话内的行为"没有任何正式承诺**，既没说可用也没说不可用。【官方文档 + 未查到（RDP 行为的官方描述）】

### 4.2 本仓库实测（这是"权威"部分的事实来源）

| 实测 | 结果 | 出处 | 等级 |
|---|---|---|---|
| DDA 在 RDP 会话内 `DuplicateOutput` | **成功**（A2 会话 SessionId=4，输出 1920×1080；旧结论"E_OUTOFMEMORY 死路"被推翻，根因是显存瞬时耗尽） | `DXGI-DDA-RDP-20261123.md` §1-2 | 实测 |
| DDA 在 RDP 内出帧节奏 | **~14~15fps**（60fps 配置下：日志 30 帧耗时 1.98s=15.2fps、2.12s=14.2fps；`AcquireNextFrame` 频繁 WAIT_TIMEOUT） | `AUDIT-20261124-00-EVIDENCE-BASELINE.md` §1.3 | 实测 |
| DDA 在 console 会话（物理桌面） | 单帧 0.132ms，理论上限 7596fps（仅采集） | `MEDIA-CAPABILITY-20261122.md` §5 | 实测 |
| WGC 在 RDP 会话抓游戏窗口 | **31.7fps = 游戏自身 30fps 锁帧**（帧哈希 125/127 不同，非重复帧）；本地控制台动态画面 58.5fps | `WGC-A1-DYNAMIC-20261122.md`、`WGC-SNAPSHOT-20261122.md` | 实测 |

### 4.3 差异机理（实测 + 推断）

| 维度 | console 会话 | RDP 会话 | 依据 |
|---|---|---|---|
| DWM 合成节拍 | 物理显示器 vsync（60Hz → 16.7ms/帧，DDA 跟着满速出帧） | **DWM 合成是变化驱动**，且节拍受 DWMFRAMEINTERVAL（默认 30fps）约束；桌面静止时几乎不产新帧 | DDA 文档（变化驱动）【官方】+ RDP 14fps vs console 7596fps 实测对照【实测】+ 30fps 上限【官方】 |
| DDA 出帧率 | 与 GPU 合成吞吐同级（亚毫秒） | **被"DWM 何时合成出一张新桌面"限死**：`AcquireNextFrame` 只在 DWM 产出新帧时返回；RDP 内实测 ~14fps（比 30fps 上限还低，因为静止区域多、合成节拍慢） | 实测 + 推断（机制解释） |
| 抓游戏窗口（WGC） | 游戏渲染率 | **游戏渲染率**（窗口持续渲染 → DWM 为该窗口持续合成 → WGC 持续拿到帧；实测 31.7fps 精确匹配游戏 30fps 锁帧） | 实测（WGC-A1-DYNAMIC 结论 1/2） |

**核心差异一句话**：console 会话里 DWM 按 vsync 满速合成；RDP 会话里 DWM 只在"有变化 + 帧间隔预算"时合成——DDA/WGC 抓的是 DWM 的产物，所以**RDP 会话内的抓帧率上限 = RDP 会话的 DWM 合成率**，而后者默认 30fps 封顶、且实际往往更低（变化驱动）。

---

## 5. 问题 (5)：给 SoulLink 的明确结论

### 5.1 RDP 会话里帧率天花板

**分三层回答（每层都是必要条件）：**

| 层 | 天花板 | 可突破性 | 依据 |
|---|---|---|---|
| 协议/DWM 层（DWM 合成率） | **默认 30fps；改 `DWMFRAMEINTERVAL=15`（+重启，可能还需 RCM 配合）后上限 60fps**。**未查到** 60fps 以上（如 120fps）的官方支持路径——文档只演示了 15→60 | 改注册表可 30→60；>60 无官方口径 | 【官方文档 KB2885213 + Q&A】 |
| RDP 编码管线层 | 官方设计预算 33ms/帧（≈30fps）；**这条约束的是"RDP 自身送屏管线"**。SoulLink 自建管线（DDA/WGC + NVENC → HTTP/WS）**不走 RDP 的编码管线**，此层对 SoulLink 无约束 | 不适用（SoulLink 绕开了这条管线） | 【官方文档 RemoteFX 计数器】+【推断：SoulLink 管线旁路性质】 |
| 抓帧层（SoulLink 实际瓶颈） | = RDP 会话 DWM 合成率 = min(30 或 60fps 注册表上限, **应用产帧率**, 变化驱动合成节奏)。**当前实测 DDA 只有 ~14fps** | ① 抓游戏窗口（WGC CreateForWindow）→ 帧率=游戏渲染率（实测 31.7fps 匹配 30fps 锁帧）；② 事件驱动取帧（帧到达即取，非 33ms 轮询）；③ 想过 30fps 必须改 DWMFRAMEINTERVAL | 【实测】WGC-A1-DYNAMIC + AUDIT-00 + 【官方文档】 |

### 5.2 能否突破？——**能，但分两步，且每步都有边界**

1. **30fps → 60fps：可以**（`DWMFRAMEINTERVAL=15` + 重启；注意 Q&A 实证"可能还要 fQueryUserConfigFromDC=1"，且客户端也要支持——Windows 客户端实证有效，Mac 客户端实证无效）。**但注意**：SoulLink 抓帧不经过 RDP 客户端显示，约束只在**服务端 DWM 合成侧**，客户端限制对 SoulLink 无关【推断：SoulLink 抓的是会话内 DWM 产物，与 RDP 客户端解码无关】。
2. **60fps 以上：无官方支持路径**（文档只演示到 60fps；未查到更高配置的先例）。**且改注册表是全局的**（影响所有 RDP 会话的 DWM 合成 CPU 开销），8 账号并发时 60fps 合成的 CPU/GPU 开销需评估【推断：开销随会话数线性叠加，需实测】。
3. **真正的第一优先不是改上限，而是先把"60fps 配置下实际 14fps"修到 30fps**：换 WGC 抓游戏窗口（帧率=游戏渲染率，已实测）或事件驱动取帧——**不改一行注册表就能把有效帧率从 14fps 提到 30fps**（游戏产 30fps 时）。改 DWMFRAMEINTERVAL 是"游戏产 60fps 之后"才需要的第二步。

### 5.3 决策建议（给 Lead）

| 优先级 | 动作 | 依据 |
|---|---|---|
| **P0** | 抓帧换 WGC CreateForWindow（游戏窗口）/事件驱动——先把 14fps → 30fps（不动注册表） | 实测 WGC-A1-DYNAMIC（31.7fps 匹配游戏锁帧）；DDA 轮询 14fps 是当前名存实亡的根因 |
| P1 | 游戏侧解锁 60fps（原神 30 锁帧是产帧率瓶颈）后，评估 `DWMFRAMEINTERVAL=15`+重启（需用户授权：全局注册表 + 重启；8 账号并发开销需评估） | 官方 KB2885213；Q&A 实证需配合 RCM 键 |
| 不做 | 期望 RDP 会话内 >60fps：无官方路径 | 未查到 |
| 不做 | 把 RDP 自身 AVC444 流旁路给 SoulLink 用（前序已查证无 API） | STREAM-PLAN-V2 §5.2（前序） |

### 5.4 与既有结论的关系（不推翻、只补权威依据）

- `STREAM-PLAN-V2-20261122.md` §5.4 已引用 30fps/DWMFRAMEINTERVAL——本文补充了**第二官方口径（33ms 编码预算）**、**Q&A 实证（RCM 牵制、客户端差异）**、**本机注册表现值（未设 DWMFRAMEINTERVAL → 默认 30fps 路径）**。
- `STREAM-SURVEY-20261122.md` §2 "RDP 会话 DWM 合成是变化驱动/软件合成"——本文用 DDA 官方文档的变化驱动语义 + console/RDP 实测对照（7596fps vs 14fps）佐证。
- `WGC-A1-DYNAMIC` "WGC 帧率=游戏渲染率"——本文确认其上限约束是 DWMFRAMEINTERVAL 30fps：**游戏锁 60fps 且不改注册表时，WGC 在 RDP 内也会被 DWM 合成率压到 ≤30fps**【推断：WGC 帧池由 DWM 合成喂帧，DWM 不合成就没有新帧；本地 console 58.5fps 是 vsync 路径】——这是对前序"WGC 帧率=游戏渲染率"结论在 RDP 内的一个重要修正边界：**该等式在 RDP 内的封顶是 DWM 合成率**。

---

## 6. 参考来源清单（全部本次 HTTP 200 直取，可回查）

| URL | 用途 |
|---|---|
| https://learn.microsoft.com/en-us/troubleshoot/windows-server/remote/frame-rate-limited-to-30-fps | 30fps 上限 + DWMFRAMEINTERVAL 官方原文 |
| https://learn.microsoft.com/en-us/azure/virtual-desktop/remotefx-graphics-performance-counters | 33ms 编码预算 → 30fps；Output/Input Frames 计数器 |
| https://learn.microsoft.com/en-us/azure/virtual-desktop/graphics-enable-gpu-acceleration | 策略组三设置（GPU 渲染/AVC 硬编/444）官方描述 + "默认 CPU 渲染" 原文 |
| https://learn.microsoft.com/en-us/azure/virtual-desktop/graphics-encoding | mixed-mode vs full-screen video（后者帧率更高） |
| https://learn.microsoft.com/en-us/azure/virtual-desktop/graphics-chroma-value-increase-4-4-4 | 444 色度策略配置 |
| https://learn.microsoft.com/en-us/windows/win32/dwm/dwm-overview | DWM 合成机制 |
| https://learn.microsoft.com/en-us/windows/win32/termserv/consoles-vs-terminals | console vs remote terminal 会话模型 |
| https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api | DDA 变化驱动语义（无 RDP 承诺） |
| https://learn.microsoft.com/en-us/answers/questions/1403785/... | DWMFRAMEINTERVAL 实效 Q&A（RCM 牵制） |
| https://learn.microsoft.com/en-us/answers/questions/2082846/... | 客户端差异 Q&A（win 有效/mac 无效） |

**未查到（诚实标注）**：
- 单独的 GPO 定义页（"Use hardware graphics adapters..."、 AVC444 策略的 learn.microsoft.com policy-settings 页）——全部 404；最完整官方描述在 AVD 指南页（已引用）。
- DWMFRAMEINTERVAL > 60fps（如 120fps）的任何官方或社区实证。
- DDA 官方文档中对 RDP 会话行为的任何描述（关键词计数 0）。

**本机只读检查**：`HKLM\...\WinStations`（无 DWMFRAMEINTERVAL）+ `HKLM\...\Policies\Terminal Services`（GPU 三策略已开）。未做任何写操作。
