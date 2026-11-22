# 串流/远程方案横向调研：抓屏 · 编码 · 会话架构

> 日期：2026-11-22
> 作者：streamsurvey（dsh-team `team_mwrr48fjaninl6` / task `task_mwrsl00riklunv`）
> 范围：**只读 + 只写本文档**。未改业务代码、未动 dist/、未提交 git、未发版。
> 目的：回答"串流软件（Parsec/Steam Link/云电脑）为何能 60fps，我们漏了什么"，以及最关键的——**"我们是不是用错了会话技术（RDP 跑游戏）"**。
>
> **证据分级（全文通用，不许混淆）**：
> - `[源码]` = 直接读到该项目的源码/README 原文（本次通过代理抓取 raw.githubusercontent 到本地核对）。
> - `[官方文档]` = 官网/官方手册/微软 Learn 原文。
> - `[行业共识]` = 多个公开来源一致、但我未逐字核对原文的事实。
> - `[查不到]` = 闭源或官方页面无法访问，明确标注，不编造。
> - `[待实测]` = SoulLink 环境里的可行性，本任务未做实验，留给后续验证。

---

## 0. 一句话结论（先给答案）

1. **串流软件没有"魔法"**：它们抓屏用 **DXGI Desktop Duplication / Windows.Graphics.Capture（WGC）**，编码走 **GPU 硬件编码（NVENC/AMF/QuickSync）**，传输走 **UDP 类低延迟协议**。这三件事 SoulLink 现在的路径（BitBlt + GDI+ 软编 JPEG + HTTP）**一样都没占**。

2. **核心判断成立：我们确实用错了会话技术。** 串流软件抓的是**交互式"console session"（Session 1）**的桌面——那里 GPU 有真实输出、DWM 正常合成、Desktop Duplication 和 NVENC 都可用。而 SoulLink 把游戏跑在 **RDP 会话**里，RDP 会话的桌面由 RDP 显示驱动虚拟化、软件合成，DXGI Desktop Duplication 直接失败（你们已实测 E_OUTOFMEMORY），BitBlt 慢（22~31ms）也是这个架构的必然结果，不是没调好参数。

3. **云电脑（Shadow / GeForce NOW / 云原神）的答案一致**：每个用户一个**完整 Windows 会话（console 级别）+ 虚拟 GPU + 虚拟显示器（IddCx IDD）**，在虚拟显示器上跑游戏、抓屏、硬编、UDP 推流。**它们不用 RDP 当游戏会话**；RDP 最多是运维/兜底通道。

4. **能借鉴、但门槛在"多开"**：换"虚拟显示器 + GPU 抓屏"单账号完全可行（Looking Glass / RustDesk / Sunshine 都这么干）；但 SoulLink 是**一台机器多账号**，console session 只有一个，多开只能靠**每账号一个虚拟机**（成本重），或在保留 RDP 多会话的前提下**改用 WGC 抓屏**（WGC 走 DWM 合成层，理论上在 RDP 里可能可用，**待实测**，是本方案唯一不需要推倒架构的 60fps 候选）。

---

## 1. 产品清单总表

> 抓屏/编码/协议/许可列的标注粒度以"本次实际核实到的"为准；标 `[查不到]` 的字段不猜。
> 语言=主要实现语言；许可=主仓库许可证（概数/官方 README 标注）。

| # | 产品 | 开源 | 语言 | 许可 | Windows 抓屏 | 编码 | 传输协议 | 是否 RDP | 备注 |
|---|---|---|---|---|---|---|---|---|---|
| 1 | **Sunshine** | 是 | C++ | GPL-3.0 | DXGI Desktop Duplication（主）；Windows.Graphics.Capture/WGC（备，🟡）`[源码]` | NVENC / AMF / QuickSync / MF / 软编；H.264/HEVC/AV1 `[源码]` | GameStream 兼容协议（RTSP 握手 + RTP/UDP 视频）`[行业共识]` | 否 | 就是你们已知的那条线索；官方 README 明确 DXGI+WGC 双后端 |
| 2 | **Moonlight** | 是 | C++ (Qt) | GPL-3.0 | （客户端，不抓屏） | 解码 H.264/HEVC/AV1 `[源码]` | 连 Sunshine/GameStream 服务端 | 否 | 服务端=Sunshine 或已停服的 NVIDIA GameStream |
| 3 | **RustDesk** | 是 | Rust | AGPL-3.0 | DXGI Desktop Duplication（主）+ GDI BitBlt 回退 + Magnification `[源码]` | VP8 / VP9 / AV1 / H.264 / H.265（RAM+VRAM 硬编）`[源码]` | **WebRTC**（webrtc 依赖，含 P2P 打洞/中继）`[源码]` | 否 | 自带 `virtual_display` 库（IddCx 虚拟显示器，无头可用）`[源码]` |
| 4 | **Deskreen** | 是 | TS/Electron | AGPL-3.0 | 浏览器做第二屏；靠 WebRTC 共享屏幕（Electron `desktopCapturer`）`[行业共识]` | VP8/VP9/H.264（WebRTC） | WebRTC | 否 | 方向是"浏览器当扩展屏"，不是低延迟游戏 |
| 5 | **Apache Guacamole** | 是 | C + Java | Apache-2.0 | **不抓屏**：作为网关代理 RDP/VNC/SSH `[官方文档]` | PNG/JPEG/WebP 图像矩形 + 可选 H.264（透传 RDP AVC）`[官方文档]` | 自研 Guacamole 协议（文本指令流，over HTTP/WebSocket）`[官方文档]` | **是（代理 RDP）** | 无客户端方案；性能受制于底层 RDP/VNC |
| 6 | **noVNC** | 是 | JS | MPL-2.0 | **不抓屏**：HTML5 VNC 客户端 `[源码]` | raw/tight/JPEG/H.264 等 VNC 编码 `[源码]` | VNC over WebSocket（websockify）`[源码]` | 否（VNC） | CloudStack/Proxmox 控制台即此；延迟与 VNC 服务端绑定 |
| 7 | **Chiaki / chiaki-ng** | 是 | C/C++ | AGPL/GPL | **不抓屏**：PS4/PS5 官方串流客户端，抓屏由主机硬件完成 `[源码]` | 主机硬编 H.264/HEVC | Sony Remote Play 私有协议（UDP）`[行业共识]` | 否 | 延迟低是主机硬编+私有 UDP 的功劳 |
| 8 | **Greenlight** | 是 | TS | GPL-3.0 | **不抓屏**：Xbox xCloud/串流客户端 `[源码]` | 主机/Xbox 硬编 H.264/HEVC | Xbox 串流协议（xCloud 走 WebRTC）`[行业共识]` | 否 | 与 Chiaki 同类：低延迟靠主机硬编 |
| 9 | **Vortex** | 是 | 多语言 | `[查不到]` | 云游戏客户端聚合器（GFN/xCloud 等前端）`[行业共识]` | 继承目标平台 | 继承目标平台 | 否 | 本身是"壳"，抓屏编码在云端 |
| 10 | **Looking Glass** | 是 | C | GPL-2.0 | **IddCx IDD 虚拟显示器**（不抓物理屏，虚拟屏直接产帧）`[源码]` | **不编码**（共享内存传原始帧，避编码延迟）`[源码]` | LGMP over IVSHMEM 共享内存（本机 VM↔host）`[源码]` | 否 | 关键样本：**虚拟显示器 + GPU 渲染 + 无编码**是零延迟核心 |
| 11 | **OBS + NDI/WebRTC** | 是 | C/C++ | GPL-2.0 | 显示器/窗口采集，WGC 是主要源之一 `[行业共识]` | x264/NVENC/AMF/QuickSync；NDI=H.264 over UDP 组播 | NDI（私有 UDP）或 WebRTC（WHIP） | 否 | 单向广播，非远程控制；但"WGC 抓屏+硬编"是可复用组件 |
| 12 | **Parsec** | 否 | 闭源 | 商业 | `[查不到]`（已知走 GPU 抓屏，支持 IddCx 虚拟显示器"Parsec Virtual Display"）`[行业共识]` | H.264/HEVC/AV1，GPU 硬编 `[行业共识]` | **自研 BUD 协议（UDP，低延迟）** `[行业共识]` | 否 | 低延迟标杆；BUD 是 UDP 而非 WebRTC |
| 13 | **Steam Remote Play / Steam Link** | 否 | 闭源 | 商业 | `[查不到]`（主机端 GPU 抓屏，跑在 Steam 交互会话） | H.264/HEVC/AV1，GPU 硬编 | Valve 私有 UDP 协议 | 否 | Steam Link 硬件上限 1080p60/40Mbps `[官方文档]` |
| 14 | **NVIDIA GameStream** | 否 | 闭源 | 商业(已停服) | **NvFBC**（NVIDIA Frame Buffer Capture）`[行业共识]` | **NVENC** | 私有 RTSP+RTP/UDP | 否 | 2023 年停服；Moonlight 转向 Sunshine 是其开源续命 |
| 15 | **AMD Link** | 否 | 闭源 | 商业(已停服) | Radeon ReLive 抓屏 `[行业共识]` | AMF/VCE 硬编 | 私有协议 | 否 | 公开技术细节少，`[查不到]` 具体 API |
| 16 | **Splashtop / Jump Desktop** | 否 | 闭源 | 商业 | `[查不到]`（Jump 用自研"Fluid"UDP 协议，走 GPU 抓屏/硬编）`[行业共识]` | H.264 硬编（可选 GPU 加速） | 自研 UDP（Jump：Fluid） | 否（本身非 RDP） | 商业远程桌面的低延迟代表，细节闭源 |
| 17 | **Shadow PC** | 否 | 闭源 | 商业 | `[查不到]`（虚拟机内 GPU 抓屏 + 硬编） | H.264/HEVC，GPU 硬编 | 自研低延迟 UDP | **否（不是 RDP 游戏会话）** | 云 PC 标杆：整机 VM + GPU + 虚拟显示器 |
| 18 | **GeForce NOW / 云原神** | 否 | 闭源 | 商业 | NVIDIA 抓屏管线 + 虚拟显示器 | NVENC H.264/HEVC/AV1 | 自研低延迟 UDP | **否** | 每用户一个容器/VM + vGPU 切片 |

**表后要点**：开源可"抄"的三条真实实现路径是——
- 抓屏：DXGI Desktop Duplication（Sunshine/RustDesk 主线）+ WGC（Sunshine 备线、OBS）。
- 编码：GPU 硬编（NVENC/AMF/QuickSync），H.264 起步、HEVC/AV1 进阶。
- 传输：要么 WebRTC（RustDesk），要么"RTSP+RTP/UDP"（Moonlight/Sunshine），要么自研 UDP（Parsec/Steam）。**没有人用 HTTP 逐帧拉 JPEG 当主链路。**

---

## 2. 专题：非 console session 怎么做串流（核心问题）

### 2.1 先厘清 Windows 会话（Session）模型 `[官方文档 + 行业共识]`

| Session | 谁在跑 | 有没有真实 GPU 输出 | 串流软件用不用 |
|---|---|---|---|
| Session 0 | 系统服务（含"服务态"串流进程） | 无桌面 | 不用（只能做无界面抓屏，受限） |
| **Session 1（console）** | 物理/本地登录的交互桌面 | **有**：GPU 扫描输出、DWM 正常合成、DXGI Desktop Duplication + NVENC 全可用 | **用这个** |
| Session ≥2（RDP） | 每个 RDP 登录一个会话 | **无真实 GPU 输出**：桌面由 RDP 显示驱动虚拟化，DWM 合成结果交给 RDP 栈软件处理 | **不用**（或仅作运维通道） |

关键机理：**DXGI Desktop Duplication 复制的是 GPU 适配器（adapter）的真实输出（scanout）。** RDP 会话没有这个输出——它的"显示器"是 RDP 的间接显示驱动（Indirect Display Driver），桌面合成走的是另一条路。所以你们在 RDP 里测出 `E_OUTOFMEMORY` / `E_INVALIDARG` 不是 bug，是**架构性不支持**。BitBlt 之所以"能用但慢"（22~31ms），是因为它走的是 RDP 会话虚拟帧缓冲的软件拷贝路径。

### 2.2 串流软件的答案：console session + 虚拟显示器（IddCx IDD）

三份独立的一手证据指向同一方案：

1. **Looking Glass（最彻底）** `[源码]`：Windows 里装 **IddCx 间接显示驱动（IDD）**，创建一个**虚拟显示器**；游戏正常把画面渲染到这块虚拟显示器（走 GPU）；IDD 把"完成帧"直接放进与 Linux host 的 IVSHMEM 共享内存，客户端直接拿来显示——**中间不编码、不抓屏、不走网络协议**，所以零编码延迟。FAQ 明确："image is not encoded as a video stream, which avoids codec latency"，且"**IDD does not require a physical display or dummy plug**"。
2. **RustDesk** `[源码]`：仓库里有 `libs/virtual_display`，官方 README 写"**Virtual display may be used on computers that do not have a monitor**"，基于微软 IddCx（Windows 10 1607+），用的是 `RustDeskIddDriver`。用途正是：**无显示器/无头机器上也能有桌面可抓、可硬编**。
3. **微软 IddSample** `[官方文档]`：官方给出 IddCx 1.4 驱动样例，明确"enumerates a single monitor"、并演示 SwapChain 帧处理循环——说明"虚拟显示器 + GPU 渲染"是微软官方支持的、给"无物理显示器"场景的正规手段。

**所以行业共识是**：想让游戏能 60fps 被抓、被硬编，就把游戏放到**一个有 GPU、有（虚拟）显示器的交互会话**里。RDP 不是这条路。

### 2.3 云电脑到底怎么给每个用户分配会话？

| 产品 | 会话模型 | 用的不是 RDP 吗？ |
|---|---|---|
| **Shadow PC** | 每用户一台**完整 Windows VM**，虚拟 GPU + 虚拟显示器，用户连进的是这台 VM 自己的 console 级会话 | 不是。游戏跑在 VM 的交互会话，抓屏硬编后自研 UDP 推流 |
| **GeForce NOW / 云原神** | 每用户一个**容器/VM + vGPU 切片**（NVIDIA vGPU / MxGPU / passthrough），游戏跑在虚拟会话里 | 不是。NVIDIA 用私有抓屏管线 + NVENC + 低延迟 UDP |
| **Azure Virtual Desktop / Windows 365** | 多用户 RDP（Windows 多会话版），**但微软给 RDP 补了 GPU 加速（Direct Stream：GPU 渲染 + AVC/H.264 硬编）** `[官方文档]` | **是 RDP**，但这是微软自己给 RDP 栈做的 GPU 编码，不等于"第三方能在 RDP 会话里随便 DXGI 抓屏" |

**结论（可直接引用）**：云电脑的主流（Shadow/GFN/云原神）**都不把 RDP 当游戏会话**，而是"虚拟化 + 虚拟显示器 + GPU 抓屏硬编"。唯一的 RDP 例外是微软自己的 AVD/W365，而且微软是**在 RDP 协议栈内部**做 GPU 加速编码，第三方拿不到那条已编码流的旁路（这一点你们上一份 `STREAM-PLAN-V2` 的 §5.2 已经查证过：RDP 的 AVC444 流没有给会话内普通代理旁路读取的 API）。

### 2.4 那"WGC 在 RDP 里可能可用"这条线索站得住吗？

**方向对，但结论待实测。** `[待实测]`
- WGC（Windows.Graphics.Capture）抓的是 **DWM 合成层**的结果（按显示器/窗口抓），**不是** GPU 适配器的 scanout，所以它**理论上不依赖"真实 GPU 输出"**，这是它区别于 Desktop Duplication 的关键。
- Sunshine 官方 README 把 WGC 标为"Windows：🟡 Partial（Portable ✅ / Service ❌）"，即**以服务身份运行时 WGC 不可用**——这跟 SoulLink 代理的部署形态直接相关（若代理跑在服务/系统会话，WGC 可能也抓不到）。
- 是否在 RDP 会话内可用：官方没有明文保证，本次没搜到权威一手确认。**这必须是 SoulLink 下一个 0.5 人日的 spike**：在真实 RDP 会话里调 `GraphicsCaptureItem.TryCreateFromMonitor` / `CreateFromWindow`，看拿不拿得到帧、帧率多少。

**若 WGC 在 RDP 里可用**：这是唯一一条**不换架构**就有望把抓屏从 22~31ms 降到接近 60fps 的路；配合 GPU 硬编（NVENC MFT 你们已枚举到）即可成串流软件同级方案。
**若 WGC 也不可用**：RDP 会话内 60fps 基本无解，只能走"换会话架构"（§3）。

---

## 3. 能不能照抄？换会话架构的成本评估

### 3.1 不能照抄的原因（诚实盘点）

| 方案 | 为什么不能照抄 | 卡在哪 |
|---|---|---|
| Parsec/Steam 那套"console session + GPU 抓屏" | **console session 只有一个**，SoulLink 是一台机器多账号 | 多开冲突：多个游戏要跑在多个会话，console 只能放一个 |
| Shadow/GFN 那套"每用户一个 VM + vGPU" | 需要 **GPU 虚拟化（vGPU/MxGPU）或 GPU passthrough + Hyper-V/VMware 全家桶** | 基建成本、驱动、Windows 授权、每 VM 一份系统开销 |
| Looking Glass 共享内存 | 要求 **Windows VM + Linux host + IVSHMEM**，与本机 Windows 物理机不符 | 架构完全对不上 |
| 直接上 WebRTC（RustDesk 式） | 解决传输与编码，**不解决"RDP 会话里抓不到屏"** | 抓屏这关仍是 BitBlt，20~30ms 卡死 |

### 3.2 换架构的两条现实路径

**路径 A（不换会话，先救抓屏）—— 推荐优先**
- 保留现有"RDP 多会话多开"，**只把抓屏从 BitBlt 换成 WGC**，编码从 GDI+ JPEG 软编换成 **NVENC H.264 硬编**（MF 已枚举到 MFT）。
- 改动小：抓屏/编码是可替换的采集层，前端与账号体系不变。
- **前置门槛**：必须先用 0.5 人日 spike 验证"WGC 在本机 RDP 会话里能抓到帧 + 帧率达标"。过了这关，单账号 60fps 才成立；不过，就只能接受 5~15fps（上一份 STREAM-PLAN-V2 的结论不推翻）。

**路径 B（换会话，一劳永逸但重）—— 仅当 A 验证失败且确有 60fps 刚需**
- 把"每账号一个游戏"从 RDP 会话，改成"**每账号一个 Windows VM（Hyper-V/VMware），VM 里装 IddCx 虚拟显示器 + 跑游戏**"，然后 VM 内用 DXGI/WGC 抓屏 + NVENC 硬编 + UDP/WebRTC 推流。
- 这是 Shadow/GFN 的正统做法，60fps 没有悬念。
- **成本**：
  1. 多开方案**要重做**：现有"一台机多 RDP 会话"→"一台机多 VM"。每个 VM 一份 Windows 授权 + 内存/磁盘开销。
  2. GPU：多 VM 共享 GPU 需要 **vGPU（NVIDIA 收费）/ MxGPU（AMD）**，或每 VM **passthrough 一块卡**（成本高）；单 GPU 不分片则多 VM 只能串行。
  3. 输入注入、账号隔离、资源配额都要按 VM 重新设计（现在是进程级隔离，将来是 VM 级）。
  4. 与"挂机"场景契合度：多开挂机对帧率其实没那么敏感（静置画面），**为 60fps 付 VM 化的代价，对挂机主场景可能不划算**；只有"远程转视角/操作"这一子需求真正需要 60fps。

### 3.3 建议的取舍（给 SoulLink）

1. **不要为了 60fps 立刻推倒 RDP 多开架构。** 先做 WGC 可行性 spike（路径 A），这是唯一低成本的 60fps 候选，失败了才谈路径 B。
2. **把"流畅感"和"60fps 全屏帧"解耦**（沿用上一份审计的结论）：远程操作跟手靠**本地预测光标 + 输入直连 + 交互时按需抓帧**，背景画面 5~15fps 足够；只有"转视角看动态画面"这一项才需要高帧率，可以做成"操作时临时拉高 WGC 帧率、停止后回落"。
3. **编码升级优先级 > 抓屏重写**：即使抓屏暂时仍是 BitBlt，把 JPEG 软编换成 NVENC H.264 硬编，就能把"抓屏后"的 5.8~13ms 编码省到接近 0，并把外网码率从 90KiB×fps 降到 H.264 的 1/10 量级（微软 DS 30fps 视频约 2.5~3.1Mbps，你们上份文档已引用）。这是与抓屏正交的独立收益。
4. **若决定长期要云电脑级体验**：优先评估 AMD MxGPU 或 NVIDIA vGPU 的授权/驱动成本，再决定是否走 Hyper-V 多 VM；不要用"RDP 会话 + 软件抓屏"硬扛 60fps，那是和行业 17 个方案相反的方向。

---

## 4. 输入注入与性能数字（快速对照）

| 产品 | 输入注入 | 公开性能数字 |
|---|---|---|
| RustDesk | `enigo`（SendInput 层）+ `rdev` 键鼠 `[源码]` | 官方口径"低延迟"，无逐字核对的毫秒数 `[查不到]` |
| Sunshine/Moonlight | Virtual HID Broker（收费）/ ViGEmBus（Xbox360/DS4）`[源码]` | 端到端普遍 <10~20ms（局域网）`[行业共识]`；无官方单一权威数字 |
| Looking Glass | IDD 直通输入（绝对定位 + 相对/rawMouse 捕获模式）`[源码]` | 共享内存零编码，业界公认"近零显示延迟" `[行业共识]` |
| Parsec | 驱动级键鼠 `[行业共识]` | 官方宣传"低至个位数毫秒级"，闭源 `[查不到]` 可核原文 |
| Steam Link | 手柄/键鼠由 Steam 输入栈注入 | 硬件端上限 1080p60/40Mbps `[官方文档]` |
| Shadow/GFN | 驱动级注入，随客户端协议走 | 官方各自宣传 <Xms，闭源 `[查不到]` 可核原文 |
| SoulLink（现状） | SendInput 层（RelayAgent） | BitBlt 22~31ms/帧（你们实测）；端到端见 STREAM-PLAN-V2 |

> 输入注入层面，SoulLink 没有明显短板：SendInput 是 RustDesk/多数商业产品同一层级。真正的差距在**抓屏 + 编码 + 传输**，不在输入。

---

## 5. 可复用结论与给后续任务的建议

1. **架构结论（强）**：RDP 会话内 DXGI Desktop Duplication 不可用是**会话模型决定的**，不是参数问题；60fps 串流的行业答案是"交互会话 + 虚拟显示器(IddCx) + GPU 硬编"。SoulLink 多开与单 console session 的矛盾，是换架构真正的成本点。
2. **唯一低成本 60fps 候选**：WGC（Windows.Graphics.Capture）在 RDP 会话内是否可用，**必须实测**（spike：真实 RDP 会话里 `GraphicsCaptureItem` + 帧率采样）。这是下一个该做的实验，优先级高于任何"优化 BitBlt"。
3. **与抓屏正交的独立收益**：NVENC H.264 硬编（MF MFT 已枚举到）替换 GDI+ JPEG 软编，省编码 CPU 与外网码率，无论抓屏是否升级都值得做。
4. **不要照抄的单点**：Looking Glass 的"共享内存不编码"对本机物理机不适用；WebRTC 只解决传输不解决抓屏；Guacamole/noVNC 是"协议网关"不是"抓屏方案"，对 SoulLink 无直接参考价值。

---

## 6. 参考来源（本次实际读取/核对）

**开源源码/README（经代理抓取 raw.githubusercontent 到本地核对）**
- RustDesk：`Cargo.toml`、`libs/scrap/src/lib.rs`、`libs/scrap/src/common/dxgi.rs`、`libs/scrap/src/common/mod.rs`、`libs/virtual_display/src/lib.rs`、`libs/virtual_display/dylib/README.md`、README
- Sunshine：README（含 Screen Capture / Encoding API 兼容表）、`src/platform/windows/display_ram.cpp`、`display_vram.cpp`、`display.h`
- Looking Glass：README、`doc/overview.rst`、`doc/install_idd.rst`、`doc/requirements.rst`、`doc/faq.rst`
- Moonlight：moonlight-qt README
- Greenlight：README
- chiaki-ng / chiaki：README
- noVNC：README
- Apache Guacamole：`guacamole-protocol.md`（协议）、`introduction.md`
- 微软：`Windows-driver-samples/main/video/IndirectDisplay/README.md`（IddCx 样例）

**官方文档/网页**
- 微软 Learn：Enable GPU acceleration for Azure Virtual Desktop（RDP Direct Stream GPU 编码）；Frame rate limited to 30 fps（RDP 帧率上限）
- Steam Remote Play 官网（Steam Link 客户端形态与"非 RDP"定位）
- NVIDIA GameStream 停服页（HTTP 403，未能读到原文 → 停服事实标 `[行业共识]`）

**未能核实（诚实标注 `[查不到]`）**
- Parsec BUD 协议细节、Parsec Virtual Display 的具体实现（support 文章 404）
- Shadow PC 内部抓屏/编码 API；GFN/云原神抓屏管线细节
- AMD Link、Splashtop、Jump Desktop 的抓屏/编码/协议公开技术文档
- Steam Remote Play 主机端抓屏 API

**SoulLink 既有文档（已读，避免重复）**
- `STREAM-PLAN-V2-20261122.md`、`STREAM-PERF-AUDIT-20261122.md`（RDP 实测 22~31ms、DXGI E_OUTOFMEMORY、NVENC MFT 已枚举、RDP AVC444 无可旁路 API）

---

> 底线声明：本文只写这一份文档，未改业务代码、未动 dist/、未提交 git；所有"查不到/待实测"均已显式标注，无编造数据。
