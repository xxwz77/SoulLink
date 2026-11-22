# WGC 验证：CreateForWindow 在 RDP 会话内可用性与「每账号独立抓屏」

> 日期：2026-11-22
> 作者：implementer（dsh-team `team_mwrnf1771i2w7` / task `task_mwrq8c3w7ugpf2` 追加任务）
> 范围：**只读取证 + 设计结论**。不改业务代码、不动 `dist/`、不改 Version、不 commit/push、不发版。
> 触发：用户明确「选哪个号看哪个号」——UU 因 `ROOT\DISPLAY` 虚拟屏挂在系统层、只能显示当前 console 会话，所以「只能看最后登录用户」是架构决定而非 bug。SoulLink 要做到多账号任选，**`CreateForWindow` 不是备选，是必须成功的路径**。
> 证据等级：**实测（第一手）**＝本会话直接读到的进程/配置/日志；**代码事实**＝当前源码可证；**推断**＝需标注；**待实测**＝尚无数据。

---

## 0. 结论摘要（先给答案）

| 问题（父任务要求） | 结论 | 证据等级 |
|---|---|---|
| `CreateForWindow` 能不能用（实测） | **能。** 用户自己的 BetterGI 正在 A2 的 RDP 会话（session 43）里以 `Lummy\A2` 本人身份用 `WindowsGraphicsCaptureV2`（WGC V2，窗口级捕获）运行，并已绑定一个 1920×1080 的 Win32 窗口（句柄 1376540） | **实测（第一手，生产进程）** |
| 能不能达到「每个账号独立抓屏」 | **能，且架构天然满足。** 每个会话一个代理进程（`SoulLink.Agent.exe`），经 `CreateProcessAsUser` 以**该账号本人令牌**启动，抓自己会话里的游戏窗口即可，**不需要跨会话** | **代码事实 + 实测（进程列表）** |
| 全屏游戏窗口有没有已知限制 | **有，需逐游戏验证**（§4 详述）：独占全屏（exclusive fullscreen）可能抓不到或黑屏，无边框窗口/无边框全屏（borderless fullscreen）通常可抓；最小化/遮挡/跨屏时行为受限 | **行业共识 + 待实测** |
| 单帧耗时与 fps | **本轮拿不到权威数字**：session 43 当前**没有游戏进程在跑**（只有 BetterGI + 桌面），没有游戏窗口可测；且我（implementer，`lummy\lummy`）无 `SeTcbPrivilege`，无法自己 `CreateProcessAsUser` 投进 A2 会话 | **待实测（诚实边界）** |

---

## 1. 决定性事实：身份，不是 RDP 限制

### 1.1 早前的 `0x80070424` 是「身份不对」，不是「WGC 在 RDP 里不可用」

`docs/research/STREAM-GAP-20261122.md` §4 已记录：在 session 43 里以 `lummy` 身份跑 WGC 探测报 `0x80070424`（`ERROR_SERVICE_NOT_INSTALLED`），根因是 **DWM 按用户身份拉起 per-user `CaptureService_<hash>`，冒充身份（lummy）≠ 桌面属主（A2），服务起不来**。

本会话直接核实的身份事实：

```
whoami /priv（implementer 自己）：  SeDebugPrivilege + SeImpersonatePrivilege（无 SeTcbPrivilege）
BetterGI.exe  PID 163100  SessionId 43  UserName = Lummy\A2   ← 关键
SoulLink.Agent.exe  PID 80536  SessionId 43（A2 会话的代理，已在跑）
```

**结论**：WGC 在 RDP 会话内能否工作，取决于**进程身份是否等于目标会话桌面属主**，而非「RDP 本身禁用 WGC」。这个结论把早前「WGC 在 RDP 里不可用/会超时」的二手推断彻底纠正了。

### 1.2 第一手证据链（BetterGI = 用户自己的生产进程，比任何探测更有说服力）

| 证据 | 值 | 来源 |
|---|---|---|
| `captureMode` | `"WindowsGraphicsCaptureV2"` | `D:\AAA挂机专区\A2\BetterGI\User\config.json`（本会话直接读取） |
| 捕获目标 | `游戏运行环境已绑定：Win32Window，窗口句柄 1376540` | `D:\AAA挂机专区\A2\BetterGI\log\better-genshin-impact20261122.log` |
| 捕获尺寸 | `游戏大小1920x1080` | 同上日志 |
| 进程身份 | `BetterGI.exe` PID 163100，`Lummy\A2`，session 43 | `Get-Process -IncludeUserName` |
| 会话类型 | session 43 = `rdp-tcp#0 A2 Active`（RDP 会话） | `query session` |

> ★ 这串证据直接回答「CreateForWindow 能不能用（实测）」：**能用**。BetterGI 的 `WindowsGraphicsCaptureV2` 是窗口级捕获（`Win32Window` + 句柄），不是 `CreateForMonitor` 抓整个显示器。它跑在 RDP 会话内、以 A2 本人身份运行、绑定了一个 1920×1080 窗口 —— 这正是我们要的形态。
>
> ★ 诚实措辞：`WindowsGraphicsCaptureV2` 是 BetterGI 自己的 V2 封装（自定义帧池 + CPU 转换优化，参考 `Fischless.GameCapture/Graphics/GraphicsCaptureV2.cs`，MIT 系），**不是**字面直接调 `GraphicsCaptureItem.CreateForWindow`。但「窗口句柄 1376540 + 1920×1080 + captureMode 明写 WGC V2」足以证明它走的是 WGC **窗口级**捕获，而非显示器级。

---

## 2. 「每个账号独立抓屏」——架构天然满足（关键问题 1/2 的最终答案）

### 2.1 UU 的架构为什么做不到「选哪个看哪个」

```
UU 虚拟屏：ROOT\DISPLAY\0000 / 0001  ← 软件虚拟设备，挂系统层，不属于任何会话
         → 只有「当前 console 会话」的画面
         → Windows console 会话只有一个
         → 谁最后登录 console 就切给谁
         → 必然「只能看最后一个登录用户」（架构决定，非 bug）
```

### 2.2 SoulLink 为什么天然能做到

| 事实 | 证据 |
|---|---|
| 每个会话一个代理进程 | `SoulLink.Agent.exe`：session 43 PID 80536、session 18 PID 38880、session 19 PID 24620（`Get-CimInstance Win32_Process`） |
| 代理以**目标账号本人令牌**启动 | `Services/ProxyHost.cs:11`「代理是用 `CreateProcessAsUser` 以『目标会话的普通用户令牌』启动的」 |
| 窗口句柄是**会话内**的 | 每个代理进程运行在自己会话，`EnumWindows`/`CreateForWindow` 只能看到**本会话**的窗口 |

**因此**：

- **不需要跨会话抓屏**（跨会话 HWND 本来就无效，但架构不需要它）。
- 每个代理抓**自己会话里的游戏窗口** → 用户选哪个账号，就通过哪个账号的代理取帧 → **天然实现「选哪个号看哪个号」**。
- 这正是父任务理解的关键点（「SoulLink 架构是每个会话一个代理进程，所以每个代理抓自己会话里的窗口就行，不需要跨会话」）——**理解正确，代码事实可证**。

### 2.3 一个必须正视的边界：显存是共享的，不随会话隔离

`nvidia-smi` 当前快照（本会话实测）：

```
NVIDIA GeForce RTX 4060 Laptop GPU, 8188 MiB 总, 6569 MiB 用, 1388 MiB 空闲
```

- 早期文档说的「显存只剩 10MB / D3D 硬件设备建设失败 `E_OUTOFMEMORY`」是**当时的瞬时状态**，现在空闲 1388 MiB —— 说明显存压力随挂机负载波动。
- WGC **抓屏本身走 DWM 合成层，不一定需要建独立 D3D11 硬件设备**（BetterGI 正在跑就是证据），但若 WGC V2 用「CPU 转换优化」路径（`wgcV2UseCpuConvert`），可以在显存紧张时降低显存占用。
- **多账号并发抓屏时，显存是所有会话共享的**：N 个代理同时 WGC 抓帧，显存压力叠加，这是「每账号独立抓屏」在工程上唯一的真实瓶颈（不是抓屏 API 做不到，是资源池共享）。

---

## 3. CreateForWindow vs CreateForMonitor（父任务要求明确回答）

| 形态 | 抓取对象 | 能否实现「选哪个看哪个」 | 在本环境的状态 |
|---|---|---|---|
| `CreateForWindow(HWND)` | **单个窗口** | ✅ **能**（窗口属于会话，代理抓本会话窗口） | **BetterGI 正在用（窗口句柄 1376540）** |
| `CreateForMonitor(HMONITOR)` | 整个显示器 | ❌ **不能**（显示器在 RDP 会话里要么是 RDP 虚拟显示、要么映射到 console，与 UU 同病） | **未实测**（本会话没跑 CreateForMonitor） |

**结论（父任务核心问题的直接回答）**：

> **`CreateForWindow` 不是备选，是必须成功的路径 —— 且它已被用户自己的 BetterGI 以 A2 本人身份在 RDP 会话内实测跑通。** `CreateForMonitor` 在「多账号任选」这个需求下**不成立**（它抓的是显示器，等价于 UU「只能看当前画面」的架构），只能作为「单窗口抓不到时的兜底」或「单账号控制台串流」场景，不能作为多账号主路径。

★ 关于「CreateForMonitor 在 RDP 里会 E_ACCESSDENIED」：这是父任务明确指出「我说 Sunshine 不行是二手 issue，未自测」的那条。**本会话也没有跑 CreateForMonitor 实测**，因此对它的结论只写「未实测」，不写「会失败」也不写「能用」。诚实立场：不以二手 issue 否定，也不以未测的乐观假设肯定。

---

## 4. 全屏游戏窗口的已知限制（父任务问题 3）

### 4.1 三类窗口形态的 WGC 可捕获性

| 形态 | WGC `CreateForWindow` 能否抓到 | 备注 |
|---|---|---|
| 窗口模式 / 无边框窗口（borderless） | ✅ 通常可抓 | 主流游戏默认或推荐形态，BetterGI 1920×1080 即此类 |
| 无边框全屏（borderless fullscreen） | ✅ 通常可抓 | DWM 仍参与合成，WGC 可拿到 |
| **独占全屏（exclusive fullscreen）** | ⚠️ **可能抓不到 / 黑屏 / 帧不更新** | 独占全屏绕过 DWM 合成，WGC 依赖 DWM，是已知风险区（行业共识） |

### 4.2 其它已知限制（行业共识 + 部分代码事实）

| 限制 | 表现 | 应对 |
|---|---|---|
| 窗口最小化 | WGC 可能不推帧（无新画面） | 抓取前恢复窗口 / 提示用户别最小化 |
| 窗口被遮挡 | WGC 仍抓该窗口自身内容（DWM 合成层按窗口源） | 一般无碍，但某些 DWM 优化下可能黑 |
| 窗口跨屏移动 | 句柄不变但坐标/尺寸变 | 重设捕获尺寸 |
| 独占全屏切换 | 捕获会话可能失效需重建 | 监听窗口消息，重建 `GraphicsCaptureSession` |
| per-user CaptureService 未拉起 | `0x80070424` | **必须以目标账号本人身份运行**（架构已满足） |

> ★ 诚实声明：以上「独占全屏抓不到」是**行业共识级**风险（OBS/Sunshine 社区广泛记录），本会话**没有**在 RDP 会话里逐一实测（session 43 当前无游戏进程）。实施时必须针对目标游戏（原神）逐形态验证。

---

## 5. 单帧耗时与 fps —— 本轮拿不到权威数字（诚实边界）

父任务要求「实测两种形态单帧耗时 + fps」。本会话**无法完成**，原因是三个硬约束，逐一说明：

### 5.1 session 43 当前没有游戏进程（最大障碍）

```
session 43 进程列表（本会话实测，已去重）：
  BetterGI.exe / explorer.exe / dwm.exe / ctfmon.exe / msedgewebview2.exe ...
  ★ 没有任何游戏进程（无 YuanShen / Genshin / 原神 等）
```

BetterGI 日志里的「游戏大小 1920×1080、窗口句柄 1376540」是**更早时刻**的绑定记录；当前 session 43 里游戏本体已退出。**没有游戏窗口，就测不到「游戏窗口抓屏耗时」** —— 这不是探针写不出来，是没有测量对象。

### 5.2 我无法以 A2 身份投送探针

- 我（implementer）身份 = `lummy\lummy`（console session 1），权限只有 `SeDebugPrivilege + SeImpersonatePrivilege`，**没有 `SeTcbPrivilege`**。
- 跨会话 `CreateProcessAsUser` 需要 `SeTcbPrivilege`（`Services/SystemLauncher.cs:572`「WTSQueryUserToken 需要 SeTcbPrivilege」），这正是主程序走「SYSTEM 计划任务引导进程」的原因（`SystemLauncher.cs:26-28`）。
- 计划任务 `schtasks /RU A2 /IT` 需要 A2 已交互登录 + 我有该身份；而账号 A1-A4 是**无密码本地账号**（项目「投送走 SYSTEM + WTSQueryUserToken，不用凭据」`Models/Models.cs:230`），`schtasks /RU A2` 无凭据也会失败。

**结论**：探针的正确投送方式，是复用现有「SYSTEM 引导进程 + `CreateProcessAsUser`」通道（即主程序已有的投送链路），而不是我这个 implementer 子会话直接投。这属于**实施阶段**要做的事，不是本读研子任务能当场完成的。

### 5.3 已确证的部分（可替代「耗时」的部分证据）

虽然没有帧耗时数字，但**存在性证据是强的**：

- BetterGI 用 WGC V2 在 RDP 内**长期运行中**（`wgcMinUpdateIntervalMs=0` 不限速、`wgcV2UseCpuConvert=false` 走 GPU 直通），用户原话「速度非常非常快」。
- WGC 原理：抓 DWM 合成层，帧到达是**变化驱动**（画面动才推帧），静止时几乎不占带宽 —— 对「挂机监控」是优点，对「60fps 流畅串流」则要求游戏画面持续变化（游戏本就在动，不受影响）。
- 单帧成本量级（参考，非本会话实测）：WGC 是 GPU 侧抓帧，无 BitBlt 的 22~31ms CPU 拷贝，行业共识为「个位数 ms 级、接近零成本」。

### 5.4 下一轮要拿到数字，必须做（留给实施/下一次验证）

1. 在**有游戏运行**的 RDP 会话内，以**目标账号本人身份**（复用主程序投送链路）跑 WGC 探针；
2. `CreateForWindow` 抓游戏窗口 → 记录 `FrameArrived` 帧间隔的 p50/p95/最大、是否拿到有效 BGRA 帧；
3. 同一会话对比 `CreateForMonitor`（若可用）的单帧耗时；
4. 测独占全屏 vs 无边框全屏两种形态的可捕获性。

---

## 6. 问朋友的清单（父任务「次优先」——若复测仍失败才用）

若下一轮以 A2 身份 + 有游戏运行时复测 WGC 仍失败，用以下清单向朋友（Sunshine「包能用」实测者）确认，避免「上下文污染」：

| # | 问题 | 为什么问 |
|---|---|---|
| 1 | 你的游戏是**本机控制台会话里跑**，还是**RDP 会话里跑**，还是**用 Sunshine 连进去看**？ | 决定他测的是不是我们的场景（RDP 会话内抓游戏窗口） |
| 2 | 你的 RDP 用什么客户端连（mstsc / Sunshine 自己 / 其它）？ | 若用 Sunshine 连，可能根本不是 RDP 会话的画面，而是 console 画面 |
| 3 | Sunshine 配置里 `capture` 设的是什么（dxgi / wgc / 自动）？ | 决定他走的是 DDAPI 还是 WGC |
| 4 | 机器接物理显示器了吗？有没有虚拟显示器（IddCx）？ | `CreateForMonitor` 依赖显示器存在 |
| 5 | 能稳定跑多久、多少帧？是「能出画面」还是「60fps 长时间稳定」？ | 区分「基本可用」与「长期挂机 60fps」 |
| 6 | 显卡型号 / 显存多大 / 同时挂几个游戏？ | 显存是共享资源，多开会撞 |

---

## 7. 与现有采集/编码设计的衔接

- **抓屏**：`SessionRecorder.GrabJpeg()`（`Relay/SessionRecorder.cs:311-354`）的 BitBlt 是回退终点，不动；新增 WGC `CreateForWindow` 抓帧作为**优先路径**，失败回退 BitBlt。降级链：`CreateForWindow` → （可选 `CreateForMonitor` 兜底单窗口抓不到时）→ `BitBlt`。
- **编码**：WGC 抓出的 BGRA 帧 → NVENC 等硬件 H.264（见 `NVENC-DESIGN-20261122.md`）→ 再不行回退 GDI+ JPEG。两篇文档共同构成「抓屏升级 + 编码升级」的完整设计。
- **身份约束（最重要，两条文档通用）**：WGC 与硬件 MFT 都要求**以目标账号本人身份**运行 —— 现有 `CreateProcessAsUser` 投送链路天然满足，实施时**不要**改成 `PsExec`/`schtasks /RU` 冒充身份（那会重现 `0x80070424`）。

---

## 8. 变更记录

| 版本 | 日期 | 内容 |
|---|---|---|
| v1.0 | 2026-11-22 | 初版：WGC CreateForWindow 可用性取证 + 每账号独立抓屏结论 + 全屏窗口限制 + 诚实边界（无游戏进程/无 TCB 权限导致本轮无帧耗时数字），task `task_mwrq8c3w7ugpf2` 追加任务 |

## 参考

- 取证对象（本会话直接读取）：`D:\AAA挂机专区\A2\BetterGI\User\config.json`、`D:\AAA挂机专区\A2\BetterGI\log\better-genshin-impact20261122.log`、`Get-Process -IncludeUserName`、`Get-CimInstance Win32_Process`、`nvidia-smi`、`query session`、`whoami /priv`
- 代码：`Services/ProxyHost.cs`、`Services/SystemLauncher.cs`、`Relay/SessionRecorder.cs`、`Models/Models.cs`
- 上游文档：`STREAM-GAP-20261122.md`（0x80070424 身份谜底）、`GITHUB-SWEEP-20261122.md`（BetterGI WGC V2 参考实现）、`OSS-REMOTE-RESEARCH-20261122.md`（CreateForMonitor/CreateForWindow 差异）、`VDD-RESEARCH-20261122.md`（虚拟显示器适用场景）、`NVENC-DESIGN-20261122.md`（本任务另产，编码侧）
