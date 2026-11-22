# 虚拟显示器（IddCx VDD）方案深度研究：能否救活 RDP 内的 GPU 抓屏（2026-11-22）

> 日期：2026-11-22；作者：oss-remote（task_mwrson0vkk3vjo）；团队：soullink-stream-opt
> 范围：只读调研 + 本文档。**未安装任何驱动、未改业务代码、未动 dist、未提交。**
> 证据标注：**已证实**＝抓取源码/README/MS Learn 官方文档逐字核对；**需实测**＝逻辑推理成立但无本机端到端证据；**传闻**＝仅第三方描述。
> 网络：raw 源码走代理 `-x http://127.0.0.1:7897`；GitHub search API 走 `--noproxy "*"`。

---

## 0. 结论速览（先看这里）

1. **虚拟显示器（IddCx）能救活 GPU 抓屏，但它救的是「控制台会话（console session）」，不是「RDP 会话」**——这是本文最重要的一个认知修正。
2. **关键机制**：Windows 有两条独立的显示驱动栈：
   - **控制台会话（console/物理登录）**：用 IddCx 间接显示驱动，虚拟显示器真实出现在 GPU 输出里，DXGI Desktop Duplication / WGC `CreateForMonitor` 都能抓到。
   - **RDP 会话（远程会话）**：用 **RDPDD（RDP 显示驱动）** 而非 IddCx。**IddCx 1.4（Win10 19H2+）才新增「remote session ID drivers」支持**（MS Learn 官方文档，已证实）——这意味着「在 RDP 会话内挂 IddCx 虚拟显示器」是一个**独立的、较新的能力**，不是默认开箱即用的。
3. **对 SoulLink 的直接结论**：SoulLink 是「多账号 RDP 挂机」，游戏跑在 **RDP 会话里**。要救活 GPU 抓屏有两条路，**都要重新评估**：
   - **路线 A（在 RDP 会话内挂 IddCx 远程会话 ID 驱动）**：技术存在（IddCx 1.4），但需要**自研一个「remote session ID driver」**，现有开源项目（parsec-vdd / RustDeskIddDriver / IddSample）**都是 console session 的 IDD，不能直接用于 RDP 会话**。风险高、工程量大。
   - **路线 B（把游戏挪回控制台会话跑，RDP 只做控制通道）**：这是 Sunshine/Duo/Parsec 的成熟做法——游戏在 console session（可配 IddCx 虚拟屏），RDP 只用于「看/管理」。但 SoulLink 的多账号架构是「每账号一个 RDP 会话」，改成 console session 多开是**架构级改动**。
4. **最务实的建议**：**先在当前 RDP 会话内实测 WGC `CreateForWindow`（上一份文档结论）+ BitBlt 降级**，别急着上虚拟显示器。虚拟显示器方案的价值在「无物理屏的控制台串流」，对「RDP 会话内抓屏」不直接适用，除非走「换本地会话」或「自研远程会话 ID 驱动」两条高成本路线。

---

## 1. parsec-vdd 原理 / 源码 / 许可 / 安装要求（已证实）

### 1.1 是什么
- **nomi-san/parsec-vdd**（5,766★，C# 封装 + C 核心 API，MIT）：把 Parsec 官方 **Virtual Display Driver（VDD）** 独立成可单独使用的工具，不依赖 Parsec 客户端。
- VDD 用 **IddCx（Indirect Display Driver）** API 生成虚拟显示器，最高 **4K / 240Hz**（Parsec VDD 0.45 版）。

### 1.2 核心 API（`core/parsec-vdd.h`，359 行，已逐字核对）

| 元素 | 值 | 说明 |
|---|---|---|
| 设备硬件 ID | `Root\Parsec\VDA` | 设备节点标识 |
| 设备类 GUID | `{4d36e968-e325-11ce-bfc1-08002be10318}` | Display 类 |
| 适配器 GUID | `{00b41627-04c4-429e-a26e-0265cf50c8fa}` | 打开设备句柄用 |
| IOCTL ADD | `0x0022e004` | `VddAddDisplay()` 插虚拟屏，返回索引 |
| IOCTL REMOVE | `0x0022a008` | `VddRemoveDisplay(handle, index)` 拔屏（16-bit BE index） |
| IOCTL UPDATE | `0x0022a00c` | `VddUpdate()` **心跳 ping** |
| IOCTL VERSION | `0x0022e010` | `VddVersion()` 查驱动版本 |

**核心交互模型**（`core/parsec-vdd.h` + `docs/VDD_LIBRARY_USAGE.md`）：
- `OpenDeviceHandle(adapterGuid)` → `CreateFileA(devicePath, ...)` 打开驱动句柄。
- `VddAddDisplay(handle)` 插屏 → `VddUpdate(handle)` 立即 ping。
- **必须每 ~100ms ping 一次**（`VddUpdate`），否则驱动**约 1 秒后自动拔掉所有虚拟屏**——这是驱动的看门狗，防止崩溃主机残留虚拟屏。
- 最多 16 屏/适配器（官方 header 保守限 8 避免插拔延迟）。
- 加屏后**没有直接改分辨率的 API**——要用 Win32 Display API（`ChangeDisplaySettingsEx`）改模式。

### 1.3 许可（已证实，逐字核对 LICENSE + header 版权头）
- **MIT**（`LICENSE`：Copyright (c) 2023 Nguyen Duy）。核心头文件 `core/parsec-vdd.h` 顶部是 **BSD 2-Clause** 版权头。
- **但驱动本体（mm.dll / mm.inf / nefconw.exe）是 Parsec 的闭源二进制**，从 `builds.parsec.app` 下载，**不是 MIT**。MIT/BSD 只覆盖「封装层代码」。
- **SoulLink 含义**：封装层可安全借鉴/移植；驱动二进制需要重新评估 Parsec 的授权（README 未给驱动本体的明确许可，仅提供下载链接）。

### 1.4 安装要求（已证实，README 逐字）
- 需要**管理员权限**装内核驱动（`nefconw.exe` 安装 `mm.inf`）。
- 三个驱动版本：0.38（Win10 1607，IddCx 1.0，废弃易崩）、0.41（Win10 19H2，IddCx 1.4，稳定）、0.45（Win10 21H2，IddCx 1.5，色彩更好但部分系统不工作）。
- 也支持 Windows Server 2019+。
- **静默安装**：`parsec-vdd-0.45.0.0.exe /S`。
- **无 HDR**、**自定义分辨率仅 5 个预设**（`HKLM\SOFTWARE\Parsec\vdd` 注册表）。

### 1.5 已知限制/坑（已证实，README 逐字）
1. **与 Parsec Privacy Mode 冲突**：会关掉主屏、打断远程会话，需清 `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Connectivity` 注册表。
2. **Win10 连接性注册表 quirk**：中间屏拔掉后剩余组合回默认模式，需「从右到左」顺序拔屏规避（issue #23）。
3. **无头登录前不可用**：GUI 进程需交互会话（session 0 隔离），需自登录或 `ParsecVDA-Always-Connected` 服务版 fork。

---

## 2. 同类方案对比表（已证实 README/许可）

| 项目 | IddCx 版本 | 签名 | 游戏 | HDR | 硬件光标 | 可调模式 | 许可 | 语言 | 说明 |
|---|---|---|---|---|---|---|---|---|---|
| **nomi-san/parsec-vdd** | 1.5 | ✅ | ✅ | ❌ | ✅ | 🆗 | MIT（封装）/ 驱动闭源 | C#+C | 最成熟，4K/240Hz，最多 3 屏（Teams/Warp 限制） |
| **virtual-display-rs** (MolotovCherry) | 1.5 | ❌ | ✅ | ❌ | ✅ | ✅ | MIT | Rust | 开源全栈 IddCx 驱动，功能最全（含 Controller） |
| **itsmikethetech/Virtual-Display-Driver (HDR)** | 1.10 | ✅ | ✅ | ✅ | ✅ | ✅ | 见 repo | C++ | 唯一支持 HDR 的开源 IDD，Win11 23H2+ |
| **roshkins/IddSampleDriver** | 1.2 | ❌ | 🆗 | ❌ | ❌ | 🆗 | MIT | C++ | 官方 IddCx 样例衍生 |
| **RustDeskIddDriver** (fufesou) | 1.2 | ❌ | ❌ | ❌ | ❌ | 🆗 | MS-PL | C++ | RustDesk 的 IddCx，3 个 IOCTL 接口清晰 |
| usbmmidd_v2 (Amyuni) | N/A | ✅ | ❌ | ❌ | ❌ | 🆗 | 闭源免费 | — | 老牌虚拟屏，性能弱 |

> 对比表来源：parsec-vdd README 自带的「Comparison with other IDDs」表（已证实），补充了语言/许可列。

**关键**：以上**全部是 console session 的 IDD**，没有任何一个声称支持「RDP 会话内直接挂虚拟屏」。parsec-vdd 的用途明确写的是「headless hosts / cloud VMs / 无物理显示器」——即 console session 场景。

---

## 3. 虚拟显示器在 RDP 会话的可用性（关键查证）

### 3.1 核心事实：RDP 会话用 RDPDD，不用 IddCx（已证实）

Windows 的显示驱动栈按会话类型分：
- **控制台会话（console）**：物理 GPU + 物理显示器 / IddCx 间接显示器。
- **RDP 会话（远程会话）**：**RDPDD（RDP Display Driver）**，一个独立的、只存在于远程会话的显示驱动，画面通过 RDP 协议传给客户端。

### 3.2 关键证据：IddCx 1.4 才支持「远程会话 ID 驱动」（已证实，MS Learn 官方）

MS Learn《IddCx Versions》（`windows-driver-docs-pr/display/iddcx-versions.md`，2026-04-01 版）明确记载：

| IddCx 版本 | IddCxGetVersion 返回值 | 变更 |
|---|---|---|
| **1.4** | 0x1400 | **Add support for remote session ID drivers**, EvtIddCxMonitorGetPhysicalSize, IddCxAdapterSetRenderAdapter, IddCxAdapterDisplayConfigUpdate |

- IddCx 1.4 随 **Windows 10 19H2（build 18363）** 及 Server 1909 提供。
- 这证明：**在远程会话内挂 IddCx 显示器是 Win10 19H2+ 才引入的独立能力**，需要写一个「remote session ID driver」，而不是复用 console session 的 IDD。

### 3.3 现有开源项目能否直接用于 RDP 会话？—— 不能（需实测确认，倾向否定）

| 项目 | 目标会话 | 能否用于 RDP 会话 | 依据 |
|---|---|---|---|
| parsec-vdd | console | ❌ 需实测，倾向否 | 设计为「无物理屏的控制台串流」，IOCTL 目标驱动挂在 console 显示栈 |
| RustDeskIddDriver | console | ❌ 同上 | 明确用于 RustDesk「被控端无显示器」场景 |
| virtual-display-rs | console | ❌ 同上 | README 场景为 headless/多屏 |
| **自研 remote session ID driver** | RDP | ✅ 理论可行 | 需按 IddCx 1.4 的 remote session ID driver 规范写驱动 |

### 3.4 结论

- **「在 RDP 会话内直接挂一个现成虚拟显示器（parsec-vdd 等）救活 GPU 抓屏」这条路，没有现成开源实现，且大概率不成立**——因为 RDP 会话的显示栈是 RDPDD，console IDD 插不进去。
- IddCx 1.4 的「remote session ID driver」提供了理论通道，但**自研内核驱动**是远超「调研」范畴的工程。
- **「虚拟显示器救活 GPU 抓屏」的正确场景是控制台会话**：无物理显示器 → 挂 IddCx 虚拟屏 → 游戏/抓屏工具在 console session 里看到真实 GPU 桌面 → DDA/WGC 全部可用。这是 Sunshine/Duo/Parsec 的成熟路径，**不是 RDP 会话内**。

---

## 4. 副作用评估（已证实 + 需实测）

### 4.1 安装驱动的副作用（已证实，README）

| 副作用 | 严重度 | 说明 |
|---|---|---|
| **需要管理员权限** | 高 | 装内核驱动必须 admin + 可能触发 UAC/签名策略 |
| **需签名驱动** | 高 | 未签名 IDD（virtual-display-rs 等）在 Win10 1607+ 需关闭 Secure Boot / 测试签名模式；parsec-vdd 用 Parsec 官方签名驱动免此问题 |
| **看门狗拔屏** | 中 | 必须持续 ping（~100ms），进程崩溃即拔屏——对 SoulLink 意味着要有一个常驻 ping 线程 |
| **与 Parsec Privacy Mode 冲突** | 中 | 会黑主屏、断远程会话 |
| **Win10 多屏排列 quirk** | 中 | 拔屏顺序不当导致显示模式回退 |
| **无 HDR / 自定义分辨率受限** | 低 | 对游戏画面色彩有影响，5 个预设够用 |
| **影响现有账号会话** | **需实测** | 挂虚拟屏会改变控制台会话的显示器拓扑，可能影响已登录的物理桌面/其他 RDP 会话 |

### 4.2 对 SoulLink 多账号架构的特定副作用（需实测）

- SoulLink 是「每账号一个 RDP 会话」。如果走「换本地会话」路线，虚拟屏挂在**控制台会话**，与 RDP 会话的账号隔离关系需要重新梳理。
- 挂虚拟屏后，物理显示器 + 虚拟屏共存，Windows 桌面拓扑变化，可能触发已有 `RdpConsole` 的分辨率协商/热更新逻辑（`UpdateSessionDisplaySettings`）。

---

## 5. 「换本地会话」方向评估

### 5.1 什么是「换本地会话」
放弃「游戏跑在 RDP 会话里」，改为「游戏跑在控制台会话（console session），用 IddCx 虚拟屏提供 GPU 桌面，RDP/自研协议只做控制与观看通道」。

### 5.2 这是 Sunshine/Duo/Parsec 的成熟架构（已证实）

| 方案 | 会话模型 | 抓屏 | 输入 | 对 SoulLink 参考价值 |
|---|---|---|---|---|
| Sunshine + Moonlight | console（可无头 + IddCx 虚拟屏） | DDA/WGC（console） | 虚拟 HID/SendInput（console） | **标准答案**，但单用户为主 |
| DuoStream/Duo | 多座位（每用户独立桌面，IddCx 虚拟屏 + RdpIddCapture） | RDP 抓屏 hook | RDP 输入 | **多用户 + 虚拟屏 + RDP 抓屏**，最接近 SoulLink |
| Parsec | console（专用 VDD 虚拟屏） | 专用低延迟协议 | 驱动级输入 | 闭源，只借思想 |

### 5.3 对 SoulLink 的成本评估

| 维度 | 评估 |
|---|---|
| **契合度** | 高——转视角（相对位移）、GPU 抓屏（DDA/WGC）、低延迟在 console session 全部是成熟方案 |
| **改动量** | **极大**——SoulLink 现有「RDP 会话多开」架构要改成「console session 多开」，涉及账号会话模型、输入注入位置、抓屏位置、Broker 调度 |
| **多账号** | DuoStream/Duo 证明「console 多座位 + 虚拟屏 + RDP 抓屏」可行，但 Duo 是闭源二进制 + 付费解锁多实例 |
| **结论** | **不推荐短期做**。这是一个「产品形态改变」级别的决策，不是「调研后顺手补个功能」 |

---

## 6. 给 SoulLink 的建议

### 建议 1（现在做）：先在当前 RDP 会话内实测 WGC `CreateForWindow`，别碰虚拟显示器
- 虚拟显示器救不了「RDP 会话内」的 GPU 抓屏（RDP 会话用 RDPDD 而非 IddCx，见 §3）。
- 真正该先验证的是：RDP 会话里 WGC 的 `CreateForWindow(HWND)` 能否抓到游戏窗口帧（上一份 `OSS-REMOTE-RESEARCH` §3.2 的结论：窗口形态更可能成功，但 FrameArrived 可能超时，必须带超时 + BitBlt 降级）。
- 这是**零风险、1 小时**的实验，结果直接决定要不要继续往下走。

### 建议 2（若实测 WGC 失败，且用户能接受「本地挂机」）：评估「换本地会话」的可行性
- 如果业务上允许「游戏在本地控制台会话跑、远程只看/控制」，则虚拟显示器（parsec-vdd，MIT 封装）+ Sunshine 式抓屏是**成熟答案**。
- 但这是**产品形态级决策**（§5.3），必须 Lead 拍板，不能擅自改架构。

### 建议 3（不推荐，仅供记录）：自研「远程会话 ID 驱动」
- IddCx 1.4 支持 remote session ID driver（Win10 19H2+），理论上能在 RDP 会话内挂 IddCx 屏救活 GPU 抓屏。
- 但**没有任何现成开源实现**，需要自研内核显示驱动（签名、WDK、调试），工程量和风险远超收益。**除非 Lead 明确要求，否则不做。**

---

## 附：与前面两份文档的衔接与修正

- 前面 `GITHUB-SWEEP` §3 说「虚拟显示器可用，是标准解法」——**本文做了更精确的限定**：可用，但场景是**控制台会话（无头主机）**，不是 RDP 会话内。
- `OSS-REMOTE-RESEARCH` §3.4 建议「parsec-vdd 挂虚拟显示器救活 GPU 抓屏」——**本文修正为**：这条 B 计划只对「换本地会话」路线有效，对「RDP 会话内抓屏」不直接有效。
- 三份文档叠加后的**统一结论**：
  1. 转视角 = `moverel` 相对位移，接 HTTP 即可（最优先）。
  2. RDP 会话内抓屏 = 先实测 WGC `CreateForWindow` + BitBlt 降级（次优先）。
  3. GPU 抓屏的真正出路 = 「换本地会话 + IddCx 虚拟屏」，属产品级决策，需 Lead 拍板。
