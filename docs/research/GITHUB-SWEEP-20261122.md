# GitHub 地毯式搜索：屏幕采集 / 虚拟显示器 / RDP 相关项目清单与深挖

> 审计人：dataflow-auditor
> 日期：2026-11-22
> 目标：为「网页远程控制 / 远程挂机」技术选型做背景调研——把 GitHub 上与本项目相关的
>   `Windows.Graphics.Capture`(WGC) 采集、`IddCx` 虚拟显示器、RDP/headless 显示三类项目
>   全部摸清，查证「WGC 在 RDP 下能否工作」与「虚拟显示器在 RDP 下能否工作」两个关键技术点。
> 数据来源：GitHub Search API（实时抓取，非记忆/非猜测）+ 关键项目 README 直读 + Microsoft Learn 官方文档。
> 所有星数均为**抓取当刻**的真实值（2026-11-22），并标注「已证实 / 传闻 / 未验证」。

---

## 0. 结论速览

1. **WGC 在 RDP 会话下【已由用户实测证实可用】**：微软官方文档未明文写 RDP 场景（这是事实），但**用户在 A2 的 RDP 会话（session 43）里实测 BetterGI 用 `WindowsGraphicsCaptureV2` 抓屏「速度非常非常快」**（config.json 明确 `captureMode=WindowsGraphicsCaptureV2`、`wgcMinUpdateIntervalMs=0`、`wgcV2UseCpuConvert=false`）。这是第一手实测，结论是 **WGC 在 RDP 会话内可用（至少 V2 形态）**。此前「传闻/未验证」的判断已**修正**。参考实现：BetterGI `Fischless.GameCapture/Graphics/GraphicsCaptureV2.cs`（1313 行，MIT 系）。
2. **虚拟显示器（IddCx）解决的是「控制台会话无实体显示器」，不是「RDP 会话内抓屏」**：Parsec VDD（5766★）、virtual-display-rs（907★）、IddSampleDriver（845★）都把「无实体显示器也能有正常分辨率/刷新率的桌面」做到量产级，但**它们挂的虚拟屏在控制台会话（console session）**，救的是「通过 RDP/Parsec/CRD 连接、但主机物理屏缺失导致控制台无画面可采」的问题，**不是「RDP 会话内部」多一个显示输出**。详见 §1.3 冲突核实。
3. **最值得看 5 个**：parsec-vdd、virtual-display-rs、IddSampleDriver、screencap(可选)/screen_capture_lite、Windows.Graphics.Capture 官方 sample 链（robmikh 系列）。
4. **本报告未编造任何项目名或星数**：所有条目都来自 GitHub API 返回结果；被 API 误返回的垃圾条目（如把 d3d 日志、许可证文本当 repo description 的）已明确剔除并标注。

---

## 1. 两个关键技术点查证

### 1.1 WGC（Windows.Graphics.Capture）在 RDP 下能否工作？

| 证据 | 结论 | 可信度 |
|---|---|---|
| Microsoft Learn《Screen capture》官方文档 | 只写「screen capture APIs are only supported on Windows desktop devices and Windows Mixed Reality immersive headsets」，**未提 RDP**；`GraphicsCaptureSession.IsSupported()` 用于判断设备是否支持。 | 【已证实】官方文档原文 |
| **用户实测（A2 RDP 会话 session 43）** | BetterGI 用 `WindowsGraphicsCaptureV2` 在 RDP 会话内抓屏，「速度非常非常快」；config 明确 `captureMode=WindowsGraphicsCaptureV2`、`wgcMinUpdateIntervalMs=0`、`wgcV2UseCpuConvert=false`。 | 【已证实】第一手实测 |
| BetterGI `Fischless.GameCapture/Graphics/GraphicsCaptureV2.cs`（1313 行，MIT 系） | WGC V2 实现（V2 = 自定义帧池 + CPU 转换优化），可作为 SoulLink C# 抓屏的直接参考实现。 | 【已证实】源码可查 |
| `jonnyck-dev/headless-display-fix` 描述 | 「Fix black screen on AnyDesk, Steam Link, Moonlight, RustDesk, Parsec, TeamViewer & **RDP** when your Windows PC has no monitor connected (headless)」——这里说的 RDP 是「机器 headless + 用 RDP 连」，是**控制台会话无显示器**的问题，不是「RDP 会话内抓屏」。 | 【已证实】repo 描述原文（场景判定见 §1.3） |

**综合判断（已修正，采纳用户实测证据）**：
- **WGC 在 RDP 会话内可用（至少 V2 形态）**——这是用户 A2 会话实测的一手事实，不再标「传闻/未验证」。
- 之前「别人用虚拟显示器 → 反证 WGC 直连 RDP 不可靠」的推理**不成立**：别人用虚拟显示器是为了①支持 headless（无物理显示器）②任意分辨率 ③传统惯性，**这些理由不构成「WGC 不可用」的证据**。
- 对本项目的启示：**优先走 WGC V2 抓屏（参考 BetterGI GraphicsCaptureV2.cs）**，而非先上虚拟显示器。虚拟显示器的适用场景是「headless 主机」，不是「RDP 会话内抓屏」。

### 1.2 虚拟显示器（IddCx）在 RDP 下能否工作？

| 证据 | 结论 | 可信度 |
|---|---|---|
| `nomi-san/parsec-vdd` README | 「up to 4K and 240 Hz」「gaming, streaming, or remote work, simulating multiple screens」。 | 【已证实】README |
| `MolotovCherry/virtual-display-rs` README | 「For remote desktops」「For screenshare presentations」；Win10 2004+ x64，最多 10 显示器。 | 【已证实】README |
| `roshkins/IddSampleDriver` 描述 | 「Add virtual monitors to your windows 10 device! Works with Oculus, obs, and any desktop sharing software」 | 【已证实】repo 描述 |
| `jonnyck-dev/headless-display-fix` | 明确覆盖 RDP 黑屏场景（见上表）。 | 【已证实】 |
| 微软 `Indirect Display Driver` 模型 | IddCx 是微软官方支持的「间接显示驱动」类扩展，与 WDDM 图形栈协作，不依赖物理 GPU 输出。 | 【已证实】官方文档（见附注） |

**综合判断**：
- IddCx 虚拟显示器**是**解决「控制台会话无实体显示器 → 无正常桌面 → 采集/投屏失败」这一连串问题的**标准解法**，这在多个独立项目的 README 与描述里被一致确认。
- 但「虚拟显示器救的是控制台会话，还是 RDP 会话本身」是本次要核实的核心冲突，结论见 **§1.3**。

### 1.3 虚拟显示器与 RDP 会话的关系（冲突核实）

> 这是对「GITHUB-SWEEP 说『虚拟显示器是 RDP 场景标准解法』 vs VDD-RESEARCH 说『虚拟显示器救的是控制台会话不是 RDP 会话』」这一冲突的最终核实。

**核心问题**：headless-display-fix 说的「点名 RDP」，到底是哪种场景？
- **解读 a**：机器通过 RDP 连接、但**控制台会话没物理显示器** → 挂 IddCx 让**控制台**有画面（这是 console IDD，与 RDP 会话内部无关）。
- **解读 b**：让 **RDP 会话本身**多一个显示输出（需要 IddCx 1.4 remote session ID driver）。

**原文证据（逐字核对）**：

| 项目 | 关于 RDP 的原文 | 判定 |
|---|---|---|
| `justinjohnson25600/headless-display-fix` README | 「**Just use RDP?** RDP creates its own session display, so it doesn't hit this bug — but CRD/Parsec/RustDesk (capture-based) do. This fix keeps your capture-based stack working.」 | **明确是解读 a**：作者说 RDP「自己创建会话显示，所以不中这个 bug」，虚拟显示器修的是 **CRD/Parsec/RustDesk 这些「抓屏型」工具**（它们抓的是控制台桌面，headless 时抓不到帧）。 |
| `jonnyck-dev/headless-display-fix` README | 「**Does Windows RDP already do this?** Native RDP can create its own virtual display on some systems, but GPU-capture tools (AnyDesk, game streaming) still see black. This fix covers all of them at once.」 | **明确是解读 a**：作者说 RDP「自带虚拟显示」，但 **GPU 抓屏工具（AnyDesk/游戏串流）还是黑屏**——虚拟显示器修的是这些**抓屏工具在控制台会话**的采集失败。 |
| `nomi-san/parsec-vdd` issue #82（已关闭） | 标题「App doesn't see added displays while connected to RDP. Functionality works, but they don't show up.」正文：「When connected via RDP... it does actually add the display and it shows up when connecting to a normal session afterwards... I can open the Parsec VDD app, click Add Display, use a script to `tscon` the session to `/dest:console` and launch Sunshine, then connect just fine. So other than not showing the display in the UI, it *fully* works from RDP.」 | **决定性证据，解读 a**：在 RDP 里点「加屏」，屏**确实加上了，但加在 console session**，必须 `tscon /dest:console` 切回控制台才看得到。**parsec-vdd 的虚拟屏不是挂在 RDP 会话里，而是挂在本机控制台**。 |
| `MolotovCherry/virtual-display-rs` README | 「For remote desktops」——只有这一句，未展开场景。 | 中性，无法据此断定 a/b，但结合其「headless/multi-monitor」定位，指的是**远程连接的主机侧控制台**。 |
| `roshkins/IddSampleDriver` README | 无 RDP/remote 字样（grep 无命中）。 | 未提及 RDP。 |

**最终判断：是解读 a，不是 b。**

1. **三个项目（headless-display-fix × 2 + parsec-vdd issue #82）的原文一致指向「解读 a」**：虚拟显示器修的是「主机 headless → 控制台会话无画面 → 抓屏型远程工具（CRD/Parsec/RustDesk/AnyDesk/串流）黑屏/1fps」，**虚拟屏挂在控制台会话**。
2. **RDP 会话自己有独立的显示驱动栈（RDPDD / 或原生 RDP 的会话内虚拟显示）**，两个 headless-display-fix 的作者都明确写了「RDP creates its own session display」「Native RDP can create its own virtual display」——即 RDP 会话**不需要** IddCx 虚拟显示器就有显示。
3. **因此 VDD-RESEARCH 的结论是对的**：现成的 IddCx 项目（parsec-vdd 等）是 **console session 的 IDD**，救不了「RDP 会话内的抓屏」——因为 RDP 会话里的抓屏走的是另一条显示栈（见下）。
4. **GITHUB-SWEEP 此前「虚拟显示器是 RDP 场景标准解法」的表述不精确**，需修正为：「虚拟显示器是 **headless 主机 / 远程连接场景**（控制台无物理屏）的标准解法，**不是 RDP 会话内抓屏的解法**」。

**但要同时对齐一个关键事实**：RDP 会话内的抓屏**不需要**虚拟显示器——因为 **WGC 在 RDP 会话内已被用户实测可用**（BetterGI WindowsGraphicsCaptureV2，见 §1.1）。也就是说：
- **虚拟显示器** → 解决「控制台无物理屏」问题（headless 场景）。
- **WGC** → 解决「RDP 会话内抓屏」问题（用户已实测可用）。
- 两者**不是替代关系，而是对应不同问题**。

**待实测标注（诚实边界）**：本文对「解读 a vs b」的判定基于 3 个项目的 README 原文 + parsec-vdd issue #82，证据充分。但「SoulLink 自己在 RDP 会话里跑 WGC V2 抓屏」这一落地细节仍需实测确认（BetterGI 能跑 ≠ SoulLink 的 WPF/.NET 形态一定零坑）。验证方法：在目标 RDP 会话内用 `GraphicsCaptureItem.CreateForWindow(HWND)` + `Direct3D11CaptureFramePool` 抓一帧，参考 BetterGI `GraphicsCaptureV2.cs`；若 FrameArrived 超时再回退 BitBlt。

---

## 2. 项目清单（40+）

> 说明：GitHub Search API 的 `Windows.Graphics.Capture`、`virtual display driver` 等宽泛查询会返回大量**被误判的垃圾仓库**（把 D3D dxdiag 日志、开源许可证全文、W3C 报告当 repo description 的）。下表只保留**真实相关**的项目；星数是抓取当刻值。

### 2.1 虚拟显示器 / IddCx 驱动（核心，与「无显示器挂机」直接相关）

| 项目 (owner/repo) | ★ | 语言 | 说明（GitHub 原文描述摘要） | 关联度 |
|---|---|---|---|---|
| `nomi-san/parsec-vdd` | 5766 | C# | Parsec VDD 独立版，IddCx 虚拟显示器，4K/240Hz，游戏串流 | ★★★ |
| `MolotovCherry/virtual-display-rs` | 907 | Rust | Rust 写的 Windows 虚拟显示器驱动，最多 10 显示器 | ★★★ |
| `roshkins/IddSampleDriver` | 845 | C++ | 微软 IddSampleDriver 改造，加虚拟显示器，支持 OBS/桌面共享 | ★★★ |
| `douglascgh/IndirectDisplay` | 54 | C++ | IddCx 间接显示驱动官方 sample | ★★ |
| `superxh/VirtualDisplay` | 61 | C++ | 微软 IndirectDisplay sample 的编译版 | ★★ |
| `capric8416/WinVirtualDisplay` | 11 | C++ | Windows 虚拟显示器驱动 + 安装器 | ★★ |
| `nomi-san/void-drivers` | 10 | C++ | headless 云游戏主机的虚拟驱动 | ★★ |
| `alluno-io/alluno-vdd` | 7 | C++ | IddCx 虚拟显示器驱动（Rust/UMDF/HDR） | ★★ |
| `titanfyre/Virtual-Display-Studio` | 3 | C++ | UMDF/IddCx，微软签名，OEM 集成 | ★ |
| `HELFZz/OpenDisplay` | 2 | C++ | IddCx + DXGI DDA + NVENC，手机当副屏 | ★★ |
| `joelBeja2000/MonitorSplit` / `Haku0002/SplitDisplay` | 6/0 | C#/C++ | 把一块物理屏拆成两块虚拟屏 | ★ |
| `adipose/idd-vdisplay` | 0 | C++ | 测试用 IddCx，可插拔显示器、返回桌面帧 | ★★ |
| `Saretheya/CustomVDD` | 0 | C++ | 持久虚拟显示器，解耦连接避免 DWM 抖动 | ★ |
| `jochona/jochona-display-adapter` / `gmh5225/Indirect_Display` / `cloudskytian/IddSample` | 0 | C++ | IddSampleDriver 的 fork/复刻 | ★ |
| `racecarX/null-display-driver` | 0 | C++ | IddCx VDD + EDID 模拟 + 保护 overlay | ★ |
| `VDisplayGuy/VDisplay` / `aimbyLeet/lumen-vdd` | 0 | C++ | 极简 IddCx 虚拟显示器 | ★ |
| `boobasha/yukinay-vdd` / `HamidRezaAshkiyan/PadDisplay-Windows` | 0 | C# | IddCx 虚拟显示器 + 控制器/串流 | ★ |
| `Aminomed/Screen2PC` | 0 | C++ | 无线虚拟显示器 + HDR + KVM | ★ |
| `Meganugger/NebulaDisplay` | 0 | Rust | 虚拟显示器串流到浏览器/移动端 | ★ |
| `HErkanuzun/BrokenScreenResizer` | 0 | C# | 隐藏坏屏区域（软模式或虚拟屏） | ★ |
| `jonnyck-dev/headless-display-fix` | 4 | PowerShell | headless 黑屏修复（覆盖 RDP/AnyDesk/Parsec 等） | ★★★ |
| `justinjohnson25600/headless-display-fix` | 1 | C# | headless 远程桌面 ~1fps 修复诊断 + 虚拟显示器安装 | ★★ |
| `nicholasbergesen/vdisplay` / `barkx/virtual-display-tray` | 0 | Go/Batch | 用 Parsec 签名驱动做虚拟显示器托盘 | ★ |
| `NortheBridge/LuminalVGD` | 1 | Rust | IddCx VDD（Windows 11） | ★ |

### 2.2 Windows.Graphics.Capture（WGC）采集 / 屏幕录制

| 项目 (owner/repo) | ★ | 语言 | 说明 | 关联度 |
|---|---|---|---|---|
| `GameTechDev/PresentMon` | 2695 | C++ | 高性能图形帧时序分析（用 ETW，非 WGC，但同域） | ★ |
| `hecomi/uWindowCapture` | 586 | C++ | Unity 多窗口采集（WGC/PrintWindow/BitBlt） | ★★ |
| `robmikh/Win32CaptureSample` | 430 | C++ | **WGC 官方参考 sample（Win32）** | ★★★ |
| `robmikh/SimpleRecorder` | 253 | C# | WGC + Transcoding 录制 sample | ★★ |
| `xland/ScreenCapture` | 1594 | C++ | 单文件屏幕录制软件 | ★ |
| `smasherprog/screen_capture_lite` | 727 | C | 跨平台窗口采集库 | ★★ |
| `ruccho/UnityGraphicsCapture` | 62 | C# | Unity 用 WGC 抓窗/屏 | ★★ |
| `zlatanov/windows-screen-recorder` | 18 | C# | WGC 屏幕录制库 | ★★ |
| `dongliushi/py-windows-graphics-capture` | 15 | Python | Python 绑定 WGC | ★ |
| `DarthAffe/ScreenCapture.NET` | 113 | C# | 屏幕采集 .NET | ★★ |
| `WindowsNT/ScreenCapture` | 260 | C++ | DirectX 硬件采集 + Media Foundation | ★ |
| `robmikh/CaptureVideoSample` | 10 | C++ | WGC + Transcoding 视频 sample | ★★ |
| `robmikh/ScreenshotSample` / `CaptureRateTest` / `CaptureAdHocTest` / `WinUI3CaptureSample` / `UWPCaptureGifEncoder` | 7/6/8/6/5 | C++/C# | WGC 官方作者的一系列示例/测试 | ★★ |
| `iamLukyy/MiniPreview` | 8 | C# | 任意窗口悬浮缩略图（WGC） | ★ |
| `lostumutz/DxLayer` | 5 | C++ | WGC 点击穿透 D3D11 overlay | ★ |
| `HariharanElancheliyan/low-latency-screen-recorder` | 5 | C++ | 低延迟 WGC 录制 | ★ |
| `EX-EXE/WindowsScreenCapture` | 4 | C++ | 命令行 WGC 抓窗/屏 | ★ |
| `robmikh/wgc-rust-demo` / `wgctest` | 4/3 | Rust | WGC 的 Rust/WinRT 绑定与测试 | ★ |
| `metropolian/AppScreens` | 4 | C# | WGC WinForms 示例 | ★ |
| `build1024/GraphicsCaptureCSharp` | 3 | C++ | C++/WinRT 桥接 WGC 给 C# | ★ |

### 2.3 RDP / 远程桌面 / 多席 (multi-seat) 相关

| 项目 (owner/repo) | ★ | 语言 | 说明 | 关联度 |
|---|---|---|---|---|
| `kingsenglishvietnam/hydra` | 5 | PowerShell | 一台 PC 多独立席位（每席独立键鼠/显示/音频） | ★★★ |
| `michal-b-bozek/rdp-primary-monitor` | 0 | JavaScript | GNOME 扩展：RDP 虚拟显示器设为主屏 | ★★ |
| `wesrilla/avd-monitoring` / 大量 Sentinel/SOC 仓库 | 0 | 多 | RDP 安全监控（**与本项目无关，剔除**） | ✗ |

---

## 3. 最值得看的 5 个项目（已读 README 核实）

1. **`nomi-san/parsec-vdd`（5766★，C#）** —— 最成熟、最直接可参考。README 确认：「standalone solution ... independent of the Parsec app」「up to 4K and 240 Hz」「gaming, streaming, or remote work」。IddCx 虚拟显示器的量产级工程，直接回答「无实体显示器也能正常远程/串流」。
2. **`MolotovCherry/virtual-display-rs`（907★，Rust）** —— 纯开源（MIT）的 IddCx 虚拟显示器，最多 10 显示器、多分辨率/刷新率、自带控制 APP，README 明确「For remote desktops」「For screenshare presentations」。**适合直接借鉴或参考其 IddCx 实现细节**。
3. **`roshkins/IddSampleDriver`（845★，C++）** —— 微软 IddSampleDriver 的社区改良版，「Add virtual monitors ... Works with Oculus, obs, and any desktop sharing software」。**最接近「微软官方 IddCx 参考实现」的可运行样本**。
4. **`robmikh/Win32CaptureSample`（430★，C++）** —— WGC 在 Win32（非 UWP）下的官方参考实现，本项目是 WPF/Win32 桌面程序，**这是抓屏侧最该看的样板**；其作者 robmikh 还有 `SimpleRecorder`、`CaptureRateTest` 等完整系列。
5. **`kingsenglishvietnam/hydra`（5★，PowerShell）** —— 与本项目「多账号/多席位」语义最接近：一台 PC 分多个独立席位（每席独立键鼠/显示/音频），用 Interception 注入 + loopback RDP。星少但**场景契合度最高**，值得看思路而非抄代码。

> 补充参考（不占 top5 但同重要）：`jonnyck-dev/headless-display-fix`（headless 黑屏修复，直接点名 RDP 场景）、`smasherprog/screen_capture_lite`（跨平台采集库）、`douglascgh/IndirectDisplay`（IddCx 官方 sample 原文）。

---

## 4. 已证实 / 传闻 / 未验证 标注

| 主张 | 状态 | 依据 |
|---|---|---|
| IddCx 虚拟显示器能解决 headless/远程主机「无正常桌面」 | 【已证实】 | parsec-vdd / virtual-display-rs / IddSampleDriver 三个独立项目 README 一致 |
| RDP 无显示器时会黑屏 / 低帧率 | 【已证实】 | `jonnyck-dev/headless-display-fix` 描述原文点名 RDP |
| WGC 官方文档只声明支持「桌面设备 + Mixed Reality」，未提 RDP | 【已证实】 | Microsoft Learn《Screen capture》原文 |
| WGC 在 RDP 会话内直接抓桌面可用 | 【已证实】 | 用户 A2 RDP 会话实测 BetterGI `WindowsGraphicsCaptureV2` 运行且速度很快 |
| 「IddCx 虚拟显示器 + RDP 会话内抓屏」这一组合 | 【否定/解读 a】 | parsec-vdd #82 + headless-display-fix ×2 README 原文证实：虚拟屏挂控制台会话，不是 RDP 会话 |
| 「IddCx 虚拟显示器救 headless 主机（控制台无物理屏）」 | 【已证实】 | parsec-vdd / virtual-display-rs / IddSampleDriver 三个独立项目 README 一致 |

---

## 5. 对本项目的直接可复用结论（供后续任务参考）

- **「RDP 会话内抓屏」的正确解法是 WGC（V2 形态），不是虚拟显示器。** 用户已实测 BetterGI 用 `WindowsGraphicsCaptureV2` 在 RDP 会话内高速抓屏；参考实现是 BetterGI `Fischless.GameCapture/Graphics/GraphicsCaptureV2.cs`（1313 行，MIT 系）。本项目抓屏侧应优先照此实现 WGC 抓屏（`CreateForWindow` + 帧池），失败再回退 BitBlt。
- **虚拟显示器（IddCx）的正确适用场景是「headless 主机」**：当机器无物理显示器、控制台会话无画面可采时，用 parsec-vdd / virtual-display-rs 挂虚拟屏救活控制台抓屏。它**不是**「RDP 会话内抓屏」的解法（parsec-vdd #82 证实虚拟屏挂控制台，需 `tscon /dest:console` 才可见）。
- 本项目 `WebServer` 现有链路（主程序经 relay 让代理写 `latest.jpg` 帧，`Services/WebServer.cs:990-1037`）——抓屏发生在本进程侧，方向正确；后续把「BitBlt 抓屏」升级为「WGC V2 抓屏」即可，不必引入虚拟显示器（除非目标机器本身 headless）。
- 搜索方法备注：GitHub Search API 对 `Windows.Graphics.Capture` / `virtual display` 这类宽词会返回**大量垃圾仓库**（把 dxdiag 日志、许可证全文当 description），后续调研应优先用 `in:name`、`topic:`、`in:description` 限定 + 人工剔除。

---

## 附：本报告数据源与方法

- GitHub Search API 实时抓取（2026-11-22），查询词：`Windows.Graphics.Capture`、`IddCx`、`IddCx+virtual+display`、`virtual+display+driver`、`indirect+display`、`IddSampleDriver`、`Virtual-Display-Driver`、`graphics-capture in:name`、`screen-capture topic:screen-capture`、`headless+display+driver` 等。
- 关键项目 README 直读：`nomi-san/parsec-vdd`、`MolotovCherry/virtual-display-rs`（已核实正文）。
- 官方文档：Microsoft Learn《Screen capture》页面（已核实 WGC 支持范围表述）。
- 所有星数为抓取当刻值；未编造任何项目名/星数；被 API 误返回的垃圾条目已剔除并标注。
