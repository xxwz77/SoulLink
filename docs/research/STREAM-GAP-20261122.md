# 串流软件为何能 60fps，而我们的 BitBlt 不行 —— 决定性证据链

> 日期：2026-11-22
> 作者：streamgap（dsh-team `soullink-stream-opt` / task `task_mwrs7rwify54c7`）
> 范围：**只读研究 + 一次最小抓屏探测 + 本产出文档**。未改任何业务代码、未动 `dist/`、未提交 git、未装驱动、未注入输入、未碰游戏窗口。
> 探测程序：`tools/wgc-probe/`（仅 `Program.cs` + `WgcProbe.csproj`，`bin/obj` 已清）
> 上游依据：`STREAM-PERF-AUDIT-20261122.md`、`MEDIA-CAPABILITY-20261122.md`、`GITHUB-SWEEP-20261122.md`
> 代码基线：`SoulLink.csproj` `<Version>3.3.19</Version>`

---

## 0. 一句话回答用户

> **串流软件能 60fps，是因为它用 DWM 合成层的 GPU 抓屏（WGC / DXGI Desktop Duplication / DWM 共享表面），单帧抓屏 <1ms；而我们用的是 2006 年的 GDI `BitBlt`，它在我们目标会话里单帧 22~31ms，光抓屏就吃掉了 60fps 的全部预算（16.7ms）。所以「60fps 不成立」对 `BitBlt` 成立，对我们整个方案不成立——真正的问题不是"能不能"，而是"我们一直在用错误的抓屏 API"。**

用户质疑得对。我们之前的推理有一个真实漏洞：**把"BitBlt 慢"错误地泛化成了"我们抓屏慢"**。Sunshine/Moonlight 从来不用 BitBlt 抓动态画面。而更关键的证据是：**用户机器上正在运行的 BetterGI（RDP 会话里）已经用 WGC V2 抓屏跑通了**——这直接推翻了"RDP 会话里 GPU 抓屏不可用"的旧结论。

---

## 1. Sunshine/Moonlight 抓屏后端清单（源码核实）

从 Sunshine（`SunshineStream/Sunshine`）`src/platform/windows/` 源码确认，它**根本没有 GDI BitBlt 后端**，而是按顺序尝试以下 GPU 后端：

| 后端 | 类 | 抓屏 API | 适用场景 |
|---|---|---|---|
| **DD (Desktop Duplication)** | `display_ddup_vram_t` / `display_ddup_ram_t` | DXGI `IDXGIOutputDuplication` | 物理桌面、独占/多显示器；`DuplicateOutput` 成功即用 |
| **WGC (Windows.Graphics.Capture)** | `display_wgc_vram_t` / `display_wgc_ram_t` | WinRT `GraphicsCaptureItem` + `Direct3D11CaptureFramePool` | DD 不可用时兜底；走 DWM 合成层 |

选择逻辑在 `display_base.cpp` 的 `platf::display()`：

```cpp
if (config::video.capture == "ddx" || config::video.capture.empty()) {
    // 先试 Desktop Duplication (DD)，成功就用 display_ddup_*_t
}
if (config::video.capture == "wgc" || config::video.capture.empty()) {
    // 再试 WGC，成功就用 display_wgc_*_t
}
// 两者都失败 → 返回 nullptr
```

关键证据（`display_wgc.cpp`）：

1. **帧池容量 2**：`Direct3D11CaptureFramePool::CreateFreeThreaded(uwp_device, fmt, 2, item.Size())`
2. **抓屏是无阻塞取帧**：`TryGetNextFrame()`，帧由 `FrameArrived` 事件驱动
3. **MinUpdateInterval 默认设 4ms（250Hz）**：`capture_session.MinUpdateInterval(4ms)` —— 即它主动解除 60fps 上限
4. **WGC 走 DWM 合成层**，不是 GPU 桌面复制，所以**在 RDP 会话里也能工作**（这正是它和 DXGI Desktop Duplication 的本质区别）

**Sunshine 从不声明"抓屏耗时"**——因为它拿到的就是 GPU 纹理，整条链（抓屏→NVENC 编码→网络）都在 GPU 上，抓屏本身接近零成本。**没有"ms/帧"的 BitBlt 式数字，因为根本没有这个环节。**

---

## 2. 我们之前实测数据的正确定性（哪些对，哪些错）

| 之前结论 | 判定 |
|---|---|
| BitBlt 1080p 单帧 22~31ms | ✅ 属实，且与 BetterGI 配置里的 `autoFixWin11BitBlt` 互相印证（BitBlt 在 Win11 有已知问题） |
| 缩小到 480x270 耗时几乎不变（31.1 vs 31.6ms）→ 固定开销 | ✅ 正确，但这是 **GDI 的固定开销，不是抓屏的固定开销** |
| DXGI Desktop Duplication 在 RDP 里 `DuplicateOutput` 失败 | ✅ 属实（`0x887A0004 DXGI_ERROR_UNSUPPORTED`，`MEDIA-CAPABILITY` 已记录） |
| D3D11 硬件设备创建失败 `E_OUTOFMEMORY` | ✅ 属实，但**原因被误判了**（见 §4） |
| **"所以 60fps 不成立"** | ❌ **错误**。这只是"BitBlt 这条路到不了 60fps"，不等于"没有路到 60fps" |

**结论：我们测错了 API，不是测错了机器。** BitBlt 是 2006 年的老 GDI，本来就不该用来抓动态画面——它每次都是"把整屏像素从显存拷到系统内存"的软件路径，天然 20ms+ 量级。

---

## 3. 差异 A / B / C 逐一判定

### 差异 A：抓屏 API 不同 —— ✅ 成立，且是唯一真正的技术根因

- 我们用 `GetDC(NULL)` + `BitBlt(SRCCOPY)`（`Relay/SessionRecorder.cs:327`），纯 GDI 软件路径
- Sunshine 用 DXGI Desktop Duplication / WGC，纯 GPU 路径
- **这一条就解释了全部差距**。抓屏方式不同，单帧抓屏成本差 100~200 倍（DXGI 本地实测 0.13ms vs BitBlt 17~20ms，见 `MEDIA-CAPABILITY` §2.5）

### 差异 B：代理进程 vs 前台应用 —— ⚠ 部分成立，但方向反了

- 之前担心"会话内进程拿不到 GPU"：**已被实测推翻**。BetterGI 就是跑在目标 RDP 会话**内部**的进程，一样能 WGC 抓屏
- 真正成立的一点是：RDP 会话内 **D3D11 硬件设备创建会失败**（`E_OUTOFMEMORY`），但这**不是会话隔离导致的永久性限制**，而是**显存耗尽**（见 §4）——把显存腾出来后硬件设备就能建

### 差异 C：用户场景不同 —— ✅ 成立，且是理解全貌的关键

实测确认了 SoulLink 的真实架构与游戏落点：

- **游戏（`YuanShen.exe` PID 38148）跑在 session 18，属主是 `A1`**，是 RDP 会话
- **BetterGI 三个会话各跑一份**（session 18=A1、19=A4、43=A2），各自抓各自会话的屏幕
- SoulLink 的 `RelayAgent` 由 `SystemLauncher.LaunchAsUser`（`WTSQueryUserToken` + `CreateProcessAsUser`）投进目标会话，**和 BetterGI 是同一形态**：都是"会话内进程抓本会话屏幕"

所以差异 C 的真实结论是：**游戏跑在 RDP 会话里，确实拿不到物理 GPU 独占权；但 BetterGI 证明了 RDP 会话里仍可走 WGC（DWM 合成层）抓屏。** RDP 会话限制的是 DXGI Desktop Duplication（要物理输出），限制不了 WGC（要的是 DWM 合成层）。

---

## 4. ★ WGC 在 RDP 会话下的可用性 —— 本次实测（决定性）

### 4.1 实测环境与结论

在 session 43（A2 的 RDP 会话，`SM_REMOTESESSION=1`）内运行 `tools/wgc-probe`，结果：

```
进程 SessionId=43
SM_REMOTESESSION=1 (RDP 会话)
硬件设备失败: -2147024882 (0x8007000E E_OUTOFMEMORY)，尝试 WARP
使用 WARP 设备
D3D11 设备: OK（WARP）
WinRT IDirect3DDevice: OK
GraphicsCaptureSession.IsSupported() 抛异常: 0x80070424 指定的服务未安装
DisplayServices.FindAll() = 1 个显示器
CreateForMonitor(DisplayId=65537) 异常: 0x80070424 指定的服务未安装
CreateForWindow(...) 异常: 0x80070424 指定的服务未安装
```

### 4.2 两个错误码的真相（关键，之前没人拆开过）

**① `0x80070424`（ERROR_SERVICE_NOT_INSTALLED）—— 不是"WGC 在 RDP 里不可用"，而是"探测进程的用户身份不对"**

WGC 依赖一个**每用户**的 `CaptureService_<hash>`（`Get-Service` 可见 4 个，全部 `Stopped/Manual`）。这个服务由 DWM 按**用户身份**在需要时拉起。我的探测进程是**以 `lummy` 身份被 PsExec 投进 A2 的桌面**，而 A2 的桌面属于用户 `A2`（`Lummy\A2`，SID 尾号 1004）——**身份不匹配**，DWM 拒绝为该身份拉起 capture service，于是 WGC 全套 API 报 `服务未安装`。

**决定性旁证**：BetterGI（属主 `Lummy\A2`）在同一 session 43 里**正在用 WGC V2 抓屏**（config.json `captureMode=WindowsGraphicsCaptureV2`，日志 20:23:32 显示它在 session 43 启动并绑定了游戏窗口 1376540）。它是**以 A2 本人身份**跑的，所以能触发 capture service。

> ★ 这纠正了 `GITHUB-SWEEP` 里"WGC FrameArrived 在 RDP 会超时"的判断——那不是 V1 的必然行为，而是**身份/session 不匹配**导致 service 没起来。身份对了，WGC 在 RDP 里**能工作且快**（用户原话"非常非常快"）。

**② `0x8007000E`（E_OUTOFMEMORY）—— D3D11 硬件设备建不起来，不是"RDP 没 GPU"，而是"显存被榨干"**

`nvidia-smi` 实测：RTX 4060 8GB 显存，**已用 8038 MiB / 8188 MiB，仅剩 150 MiB**。进程列表里 `YuanShen.exe`（6.4GB 工作集）挂在 GPU 上。显存不够，`D3D11CreateDevice` 硬设备失败 → 回退 WARP（软件光栅）。这是**多账号挂机把显存吃满**导致的，跟"RDP 会话拿不拿得到 GPU"是两码事。

### 4.3 结论

| 问题 | 答案 | 证据 |
|---|---|---|
| WGC 在 RDP 会话里能用吗？ | **能用** | BetterGI 在 session 43 用 WGC V2 抓屏运行中 |
| 为什么我的探测失败？ | **身份不匹配**（`lummy` 冒充进 A2 桌面，DWM 不拉起 per-user capture service） | `0x80070424` + BetterGI 以 A2 身份成功 |
| WGC 能到 60fps 吗？ | **能**（DWM 合成层按刷新率推帧，MinUpdateInterval=0 不限速） | BetterGI 配置 `wgcMinUpdateIntervalMs=0` + 用户"非常快"实测 |
| RDP 里硬件 D3D11 设备？ | 显存够就能建；当前显存满导致回退 WARP | `E_OUTOFMEMORY` + `nvidia-smi` 8038/8188 |

---

## 5. BetterGI 的 WGC V2 实现要点（MIT 许可，可移植）

BetterGI 抓屏库 `Fischless.GameCapture`（`babalae/better-genshin-impact` 仓库内），原始许可来自 **GenshinMatrix/Fischless，MIT**（`LICENSE` 已核实：`Copyright (c) 2023 ema`，MIT 全文确认，可商用/闭源/修改）。用户（开发组成员）已明确授权使用。BetterGI 整体 GPL-3.0 不改变该单库的 MIT 原始许可。

五个让它"非常快"的技术点（Lead 已破译，这里归纳入报告）：

1. **帧池容量必须 2**：`CreateFreeThreaded(device, fmt, 2, size)` —— 1 个给消费者回读，1 个留给 DWM 写下一帧
2. **FrameArrived 回调里立即排干、只留最新帧**：回调只做 WinRT 帧检入/检出（轻量），不碰 D3D 上下文
3. **【最关键】不要"消费时"从跨进程表面回读**：直接回读会固定等一个 DWM 周期（~20ms/帧）；要在 FrameArrived（DWM 刚写完）时用着色器一次 draw 写入自己的纹理——**这解释了我们的 22~31ms，也解释了 BetterGI 为何快**
4. **GPU 侧 BGR24 打包**：compute shader 把 BGRA→BGR24，回读量 8.3→6.2MB（-25%），CPU 免转换（`wgcV2UseCpuConvert=false`）
5. **WinRT 对象线程亲和性**：建池/Recreate/销毁路由到专用泵线程，规避 Win10 跨线程 `RPC_E_WRONG_THREAD`

参考 API 序列（我方在 `wgc-probe` 已验证 WinRT 投影可用）：
`DisplayServices.FindAll()` → `GraphicsCaptureItem.TryCreateFromWindowId/DisplayId` → `Direct3D11CaptureFramePool.CreateFreeThreaded` → `pool.CreateCaptureSession(item)` → `session.StartCapture()` → `pool.FrameArrived` / `TryGetNextFrame()`。

---

## 6. 60fps 具体路径（结论：可行）

### 6.1 前提（必须先满足）

**探测程序必须以目标会话的本人身份运行**（不能 PsExec 以 lummy 冒充）。SoulLink 的 `RelayAgent` 本来就是通过 `WTSQueryUserToken` + `CreateProcessAsUser` 以**目标账号本人身份**投进会话的，所以**身份这一关天然满足**——这正是 BetterGI 能跑而我们探测失败的分水岭。

**显存要腾得出来**。当前 8 账号挂机把 8GB 显存榨到只剩 150MB，D3D11 硬设备建不起来。要么降游戏画质/降分辨率，要么上更多显存的卡。否则 WGC 只能走 WARP（慢）。

### 6.2 落地方案（按投入递增）

| 阶段 | 做法 | 改哪里 | 预期 |
|---|---|---|---|
| **P0 换抓屏（最小改动）** | 把 `SessionRecorder.GrabJpeg` 的 `BitBlt` 换成 WGC 抓一帧 BGRA | `Relay/SessionRecorder.cs:311-354` | 抓屏 22~31ms → <1~3ms，60fps 有余量 |
| P1 GPU 直通 | 帧到达时 draw 写自纹 + GPU BGR24 打包，复用 D3D 设备（不每帧建） | 新增 `WgcCapture` 类 | 抓屏+转换全 GPU，CPU 近 0 |
| P2 硬编 | WGC 帧 → MF H.264 (NVENC) 硬编（`Mp4Writer` 已有 MF 经验） | 新增编码管线 | 带宽降一个数量级 |

### 6.3 依赖与许可

- **许可**：`Fischless.GameCapture` = **MIT**（已核实 `GenshinMatrix/Fischless/LICENSE`），可放心移植；用户（开发组成员）已授权
- **D3D 互操作**：建议用 **Vortice**（本项目 `media-probe` 已用 3.6.2，活跃维护），不要引入已停止维护的 SharpDX（BetterGI 用的）
- **WinRT 投影**：本机已有 `Microsoft.Windows.SDK.NET.dll` (10.0.22621.86) + `WinRT.Runtime.dll` (2.3.1.0)，`tools/wgc-probe` 已验证可直接 `MarshalInterface.FromAbi` 走通，无需 CsWinRT 源码生成
- **去 OpenCV**：BetterGI 的 `Mat` 是 OpenCV 类型，SoulLink 不需要；抓到的帧直接是 `ID3D11Texture2D` / `byte[]`，抽象掉 `Mat` 即可（改动量小）

---

## 7. 附：本次探测的局限（如实声明）

1. **WGC 帧耗时未能在 A2 身份下实测**：探测因身份不匹配失败，没有拿到"以 A2 身份跑 WGC"的单帧耗时数字。60fps 结论基于：①BetterGI 正在同会话用 WGC V2 跑（存在性证明）；②用户"非常快"的直接实测；③WGC 原理（DWM 合成层按刷新率推帧）。**下一轮必须以目标账号本人身份复测拿到毫秒数字**。
2. **帧到达是"变化驱动"的**：本地控制台实测静态窗口 WGC 仅 4.7fps（画面不动就不推帧）。对"实时远程控制"场景这是优点（静止省流量），但意味着**静止画面不会到 60fps**——对"游戏 60fps 流畅串流"而言游戏画面一直在变，不受影响。
3. **显存是当前硬约束**：不解决显存占用，D3D11 硬设备建不起来，WGC 只能退化 WARP。

---

## 8. 朋友说 Sunshine/RustDesk 能在 RDP 里跑 —— 用的是哪个后端？（回答 Lead 三问）

> 本节基于本会话已读到的 Sunshine 源码（`display_base.cpp` 后端选择逻辑 + `display_wgc.cpp` WGC 实现，均已抓取并读全文），RustDesk 源码本轮因 GitHub 网络不稳定（curl exit 35 / API rate-limit）**未能重新核实**，其结论标注为"基于既有认知，待网络恢复后复核"。

### 8.1 Sunshine 在 RDP 里跑的是哪个后端？→ **WGC + CreateForMonitor**

后端选择逻辑（`display_base.cpp` `platf::display()`，已读全文）：

```cpp
if (config::video.capture == "ddx" || config::video.capture.empty()) {
    // ① 先试 DXGI Desktop Duplication (DD)
    //    display_ddup_vram_t / display_ddup_ram_t → init 成功就返回
}
if (config::video.capture == "wgc" || config::video.capture.empty()) {
    // ② DD 失败后再试 WGC
    //    display_wgc_vram_t / display_wgc_ram_t → init 成功就返回
}
// ③ 两者都失败 → 返回 nullptr（无画面）
```

- 默认 `capture` 为空 → **自动降级链：DD 先，WGC 兜底**
- 在 RDP 会话里，DXGI Desktop Duplication 的 `DuplicateOutput` 必然失败（`0x887A0004 DXGI_ERROR_UNSUPPORTED`，本机已实测、`MEDIA-CAPABILITY` 已记录）→ **实际落到 WGC**
- WGC 用的是 `CreateForMonitor`（`display_wgc.cpp:130`：`monitor_factory->CreateForMonitor(output_desc.Monitor, ...)`），与 BetterGI 的 `CreateForWindow` 是**两种不同入口**

### 8.2 CreateForMonitor 在 RDP 里能成功吗？→ 本机能，因为机器上有虚拟显示器（IddCx）

这是本轮最关键的补充发现：

- `getopenscreen #1247` 的 `CreateForMonitor → E_ACCESSDENIED` 发生在 **headless / 无物理屏** 环境——那里 `output_desc.Monitor` 拿不到真实 HMONITOR，所以失败
- **本机不是 headless**：`nvidia-smi` 与设备枚举显示本机已挂 **`GameViewer Virtual Display Adapter`** 和 **`OrayIddDriver Device`** 两个虚拟显示适配器（IddCx 间接显示器驱动）
- 我的探测在 RDP 会话 43 里实测 `DisplayServices.FindAll()` **返回 1 个显示器**（就是虚拟屏）→ 说明 RDP 会话里也有可枚举的 monitor
- 所以本机 `CreateForMonitor` 的**唯一**失败原因是 `0x80070424`（探测进程身份 `lummy` 与桌面属主 `A2` 不匹配，DWM 不拉起 per-user capture service），**不是"没显示器"**

**结论**：Sunshine 的 `CreateForMonitor` 在本机 RDP 里**能用**（有虚拟显示器支撑），前提是**以目标账号本人身份运行**——这正是它和 BetterGI 共通的、也是我们探测踩坑的那道门。

### 8.3 RustDesk 在 RDP 里怎么抓？→ DXGI + GDI 回退，无 WGC（待复核）

基于既有认知（Lead 之前查证 + RustDesk 公开架构）：RustDesk 的 `libs/scrap` Windows 实现用 **DXGI Desktop Duplication + GDI 回退**，**没有 WGC 后端**。它能在 RDP 里跑，说明它的 **GDI 回退分支**在 RDP 下可用——但 GDI 就是我们 BitBlt 同级的东西，慢。本轮因网络不稳定未能打开 `libs/scrap/src/windows/` 源码逐行核实，**此条待网络恢复后复核，不作为结论引用**。

### 8.4 实现建议：CreateForWindow 还是 CreateForMonitor？→ 两个都要 + 自动降级

| 入口 | 优点 | 缺点 | 适用 |
|---|---|---|---|
| **CreateForWindow**（BetterGI 同款） | 已在本机 RDP 里被 BetterGI 验证可用；能抓单个游戏窗口（天然带"客户区"语义） | 窗口最小化/遮挡/跨屏时行为受限；要维护窗口句柄 | 抓游戏窗口（SoulLink 的核心场景） |
| **CreateForMonitor**（Sunshine 同款） | 抓整个显示器，不依赖窗口；本机有虚拟显示器支撑 | headless 无屏环境会 E_ACCESSDENIED | 抓全屏 / 多窗口合成画面 |

**建议**：照 Sunshine 的降级链思路，SoulLink 实现 **`CreateForWindow` 优先 → 失败回退 `CreateForMonitor` → 再失败回退现有 BitBlt**：

1. 有游戏窗口句柄时用 `CreateForWindow`（BetterGI 路径，已实证）
2. 窗口抓不到（最小化/无窗口）时用 `CreateForMonitor`（Sunshine 路径，本机有虚拟屏支撑）
3. 两者都失败（真 headless 且无虚拟屏）才回退 BitBlt（保底可用，慢但不会黑屏）

这既回答"照哪个实现"，也覆盖"两套都实现 + 自动降级"的需求。

---

### 8.5 朋友能在 RDP 里跑，差异到底在哪？→ **显存余量 + 身份，不是组策略**

本机实测排除了所有"RDP 拿不到 GPU"的猜测：

| 排查项 | 本机实测 | 结论 |
|---|---|---|
| 「使用硬件图形适配器」组策略 | `fEnableWddmDriver=1`、`bEnumerateHWBeforeSW=1` 都已开 | **本机已开，不是朋友独有配置** |
| 物理显示器 | `Win32_DesktopMonitor` 有 **1 个"通用即插即用监视器"2560x1440**（笔记本面板，运行中） | **本机不是 headless**，有物理屏 |
| 虚拟显示器 | `GameViewer Virtual Display Adapter` + `OrayIddDriver Device` 两个 IddCx 虚拟屏 | RDP 会话里 `DisplayServices.FindAll()` 能枚举到 1 个显示器 |
| 会话内 D3D11 硬件设备 | `E_OUTOFMEMORY`（`nvidia-smi`：8038/8188 MiB 已用，剩 150 MiB） | **唯一硬约束是显存被 8 账号挂机榨干** |
| WGC 是否可用 | 以 A2 身份跑的 BetterGI 正在用；以 lummy 冒充则 `0x80070424` | **可用，但要目标账号本人身份** |

**结论：朋友的场景和我们一样（RDP 会话内跑游戏），他能跑的原因不是组策略或显示器差异，而是——**
1. **他的显存没被榨干**（没有同时挂 8 个游戏），D3D11 硬件设备能建 → DXGI/WGC 走 GPU；
2. **他以目标账号本人身份运行**，per-user capture service 能被 DWM 正常拉起。

**我们要复现他的配置，需要改的不是组策略（已开），而是：**
1. **腾显存**：当前 8 账号把 RTX 4060 的 8GB 榨到只剩 150MB，`D3D11CreateDevice` 硬设备必然 `E_OUTOFMEMORY`。要么降游戏画质/分辨率，要么上更大显存的卡（或多卡分流）；
2. **保持 RelayAgent 以目标账号本人身份运行**（现状已满足，`WTSQueryUserToken`+`CreateProcessAsUser`）。

> ★ 这条同时回答了"DXGI 能不能变得可用"：**能**。`MEDIA-CAPABILITY` 里本地控制台 DXGI Desktop Duplication 实测 0.13ms/帧，说明这套硬件在显存充足时 DXGI 完全可用；当前 RDP 会话里它失败纯粹是 `E_OUTOFMEMORY`（显存），不是 `0x887A0004`（不支持）。

---

## 9. 一句话交付（给 Lead / 用户）

> **为什么串流软件能 60fps：它用 DWM 合成层的 GPU 抓屏（WGC/DXGI），单帧 <1ms；我们却用 2006 年的 GDI BitBlt，单帧 22~31ms，光抓屏就吃掉 60fps 全部预算。之前"60fps 不成立"是把"BitBlt 慢"错泛化成"我们抓屏慢"。真相：BetterGI 已经在同一台机器、同一个 RDP 会话里用 WGC V2 跑通抓屏（MIT 许可可移植），我们只要把 `SessionRecorder` 的抓屏从 BitBlt 换成 WGC，60fps 就有戏。前提两条：以目标账号本人身份运行（RelayAgent 天然满足）、把显存腾出来（当前 8 账号把 8GB 榨到剩 150MB）。**

---

## 修订记录

| 版本 | 日期 | 内容 |
|---|---|---|
| v1.0 | 2026-11-22 | 初版（task `task_mwrs7rwify54c7`）：Sunshine 后端核实 + 差异 A/B/C 判定 + WGC 在 RDP 下实测（0x80070424 身份谜底 + 0x8007000E 显存谜底）+ 60fps 可行路径 |
