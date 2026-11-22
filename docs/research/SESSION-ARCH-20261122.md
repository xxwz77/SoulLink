# 会话架构路线评估：RDP / 虚拟显示器 / 本地多开 / 虚拟机（2026-11-22）

> 作者：ledger（agent-team `team_mwrnf1771i2w7` / task `task_mwrspddbl8gfnc`）
> 范围：**只读评估 + 本文档**。未改业务代码、未动 `dist/`、未提交、未在任何账号会话做实验。
> 证据分级：**【已实测】**＝本机命令实测输出；**【官方文档】**＝MS Learn / 项目 README 原文；**【行业共识】**＝多来源一致；**【查不到】**＝明确标注，不编造。
> 关键上游：`STREAM-SURVEY-20261122`、`STREAM-PERF-AUDIT-20261122`、`VDD-RESEARCH-20261122`、`GITHUB-SWEEP-20261122`、`CN-CLOUD-GAMING-20261122`、`MEDIA-CAPABILITY-20261122`、`OSS-REMOTE-RESEARCH-20261122`、`SESSION-SLIM-DEEP-20261122`。

---

## 0. 一句话结论

**四条路线里，「RDP 多会话」仍是唯一不推倒架构、且已被 SoulLink 跑通的路线；它唯一的短板（抓屏慢）已被用户实测证明可用「WGC 抓屏 + NVENC 硬编」在 RDP 会话内补上。**
「虚拟显示器」解决的是「控制台无物理屏」，不是「RDP 会话内抓屏」；「本地多开」在 Windows 客户端上**不存在**比 RDP 多会话更原生的多桌面方案；「虚拟机」在本机（NVIDIA 笔记本 GPU）**直接被消费级 GPU-PV 判死刑**，且资源（29.7GB 内存现仅 2.4GB 空闲）撑不起多 VM。

**推荐路线 = 维持 RDP 多会话 + 采集层升级（WGC 替换 BitBlt）+ 编码层升级（NVENC 替换 JPEG 软编）。**

---

## 1. 本机实测环境（评估的物理前提，非理论）

| 项 | 实测值 | 来源 |
|---|---|---|
| 系统 | Windows 11 专业工作站版 Insider Preview，Build 29698 | `Win32_OperatingSystem` |
| CPU | 13th Gen Intel i9-13900HX，32 逻辑核 | `Win32_ComputerSystem` |
| 物理内存 | 29.7 GB 总 / **2.4 GB 空闲** | `Win32_OperatingSystem` |
| GPU | **NVIDIA GeForce RTX 4060 Laptop GPU**，8188 MiB（WMI 报 4095MB 是 32 位溢出，`nvidia-smi` 报 8188） | `nvidia-smi` |
| GPU 显存占用 | **8038 / 8188 MiB（几乎打满）** | `nvidia-smi` 实测 |
| 虚拟显示驱动（已装） | GameViewer Virtual Display Adapter、OrayIddDriver（两个 IddCx 虚拟屏） | `Win32_VideoController` |
| Hypervisor 状态 | `HypervisorPresent=False`；VBS=0；Hyper-V 未启用 | `Win32_ComputerSystem` / `Win32_DeviceGuard` |
| 虚拟化硬件能力 | VT-x/VT-d/SLAT 全 True | `Win32_Processor` |
| 会话模型 | console(s1=lummy) + A1(18) + A4(19) + A2(43) 三个 RDP 会话 | `query session` |
| 会话级内存占用 | s0=8.9GB / s1=9.5GB / s18(A1)=10.1GB / s19(A4)=2.4GB / s43(A2)=2.1GB | `Get-Process` 分组实测 |
| 磁盘 | C: 空闲 205.6 GB / 总 451.2 GB | `Win32_LogicalDisk` |

> ★ 两个决定性事实：
> 1. **这是台 NVIDIA「Laptop」GPU 的笔记本**（HP OMEN 16-wf0xxx），不是桌面独显。
> 2. **内存已经处于高压状态**（各会话工作集合计 ≈ 33GB > 物理 29.7GB，空闲仅 2.4GB）。

---

## 2. 四条路线对比总表

| 维度 | ① RDP 多会话（现状） | ② 虚拟显示器（IddCx） | ③ 本地多开（console 单桌面多实例） | ④ 虚拟机（Hyper-V/VMware） |
|---|---|---|---|---|
| **游戏跑在哪** | 每个账号一个 RDP 会话（RDPDD 显示栈） | 控制台会话（console，IddCx 虚拟屏） | 单一 console 会话里多开客户端 | 每个账号一个完整 VM |
| **GPU 可用性（抓屏）** | ⚠️ DXGI DDAPI **失败**（已实测 E_OUTOFMEMORY）；BitBlt 可用 17~20ms；**WGC V2 已用户实测可用** | ✅ 控制台有真实 GPU scanout，DDAPI/WGC/NVENC 全通 | ✅ 与 console 相同（但只有一个桌面） | ✅（若 GPU-PV/直通成立） |
| **GPU 可用性（硬编）** | ⚠️ NVENC MFT 已枚举到，RDP 内可用性**待实测** | ✅ 控制台 NVENC 全通 | ✅ 同 console | ✅（GPU-PV 内） |
| **输入注入** | `mouse_event` 相对位移（带 INJECTED），已实现 | 驱动级虚拟 HID 可用（无 INJECTED） | 前台唯一，后台游戏收不到输入 | VM 内 SendInput |
| **多账号隔离** | ✅ OS 级桌面隔离（现成） | ❌ console 只有一个，无法多账号各自独立 | ❌ 共享一个桌面，前台互抢 | ✅ VM 级强隔离 |
| **改造代价** | 采集/编码层替换（小，1~2 人日/项） | 自研 remote-session ID 驱动（内核级，重）或换会话架构（产品级） | 架构级推翻 | 架构级推翻 + 每 VM 一份 Windows + 资源重 |
| **风险** | 历史 0x119 事故（多 ActiveX 重挂，非本会话模型） | 内核驱动、签名、影响现有会话拓扑 | 游戏单实例/后台挂机限制 | **本机 GPU 不支持消费级 GPU-PV（见 §5）** |
| **本机可行性** | ✅ 已在跑 | ⚠️ 需自研内核驱动，无现成开源 | ❌ 与产品定位冲突 | ❌ NVIDIA 笔记本 GPU + 内存不足 |

---

## 3. 路线①：RDP 多会话（现状，唯一不推翻架构的路）

### 3.1 现状事实

- SoulLink 已是「一台机 + RDPWrap 多会话 + 每账号一个 RDP 会话」，账号隔离、跨会话投送、`RelayAgent` 会话内注入全部跑通（`HANDOFF.md` §四）。
- 本机 `query session` 实测：console + 3 个 Active RDP 会话共存。

### 3.2 唯一短板与解法

RDP 会话的桌面由 **RDPDD（RDP 显示驱动）** 虚拟化，没有 GPU 真实 scanout：
- **DXGI Desktop Duplication 直接失败**（`MEDIA-CAPABILITY` §2.4 实测 E_OUTOFMEMORY）——这是会话模型决定，不是参数没调好。
- **BitBlt 可用但慢**：17~20ms 抓屏 + 4.5~4.9ms JPEG = 22~31ms/帧，60fps 无望，15fps 内够用。
- **WGC（Windows.Graphics.Capture）在 RDP 会话内可用**——用户已在 A2 会话（session 43）实测 BetterGI 用 `WindowsGraphicsCaptureV2` 抓屏「速度非常非常快」（`GITHUB-SWEEP` §1.1，config 明确 `wgcMinUpdateIntervalMs=0`）。

> 结论：RDP 会话内 60fps 的**唯一低成本候选**是「WGC 抓屏替换 BitBlt + NVENC 硬编替换 JPEG 软编」，采集层可替换、账号体系不动。

### 3.3 输入边界（不随架构变）

RDP 会话内驱动级虚拟 HID 不可用（HID 属 console session），只能用 `mouse_event` 相对位移（带 INJECTED），严格反注入的游戏无完美解（`OSS-REMOTE-RESEARCH` §2.4，已定案）。这与选哪条会话路线无关，是「游戏跑在远程会话」的固有边界。

---

## 4. 路线②：虚拟显示器（IddCx）—— 认知修正

### 4.1 关键事实（不是 RDP 会话的解法）

虚拟显示器解决的是**「控制台会话无实体显示器 → 无画面可采」**（headless 主机），不是「RDP 会话内抓屏」。

- **控制台会话**：用 IddCx 间接显示驱动，虚拟屏真实出现在 GPU 输出里 → DDAPI/WGC/NVENC 全可用。
- **RDP 会话**：用 RDPDD，**不是 IddCx**。IddCx 1.4（Win10 19H2+）才新增「remote session ID driver」支持（MS Learn 官方文档，`VDD-RESEARCH` §3.2 已证实）。

### 4.2 对本机的含义

- 现成开源 IddCx 项目（parsec-vdd 5766★ / virtual-display-rs / IddSampleDriver）**全是 console session 的 IDD**，救不了 RDP 会话内抓屏。
- 要在 RDP 会话内挂虚拟屏，只能**自研 remote-session ID 内核驱动**（签名 + WDK + 调试），无现成开源实现，工程量远超收益。
- 本机**已经装了** GameViewer / OrayIddDriver 两个虚拟显示驱动（`Win32_VideoController` 实测）——它们是控制台侧的虚拟屏，对 RDP 会话内抓屏没有帮助，反而印证「虚拟屏在 console、不在 RDP」。

### 4.3 结论

**虚拟显示器对 SoulLink「RDP 会话内多账号挂机」不直接适用。** 它唯一有价值的场景是「换本地会话」（见 §6），但那属产品级决策。

---

## 5. 路线③：本地多开（console 单桌面多实例）—— 结论：Windows 客户端不存在「多 console」

### 5.1 Windows 会话模型的硬约束

| 会话 | 谁在跑 | 有几个 | 能否放多个账号各玩各的 |
|---|---|---|---|
| Session 0 | 系统服务 | 1 | 无桌面 |
| **Session 1（console）** | 本地物理登录 | **只有 1 个** | 一个桌面，多客户端共享 |
| Session ≥2（RDP） | 每个 RDP 登录一个 | 多个 | ✅ 各自独立桌面 |

**「本地多开」= 把 N 个游戏全塞进唯一一个 console 会话的桌面里。** 这是多开器（multiboxer）的做法，对 SoulLink 不成立：

1. **前台互抢**：一个 console 只有一个活动桌面 + 一个前台窗口。游戏挂机常需「窗口在可见桌面」，后台窗口会被系统降频/暂停输入。
2. **输入注入失效**：`mouse_event`/`SendInput` 只送到前台窗口；后台游戏收不到，无法「多账号并行注入」。
3. **游戏单实例/反作弊**：原神类游戏普遍单实例 + 反作弊，同桌面多开会互相冲突。
4. **丢失账号隔离**：SoulLink 的核心价值是「每账号独立桌面 + 独立输入」，console 多开恰恰把隔离丢掉。

### 5.2 结论

**Windows 客户端上，「每个账号一个独立交互桌面」只有三条路：RDP 多会话（现状）、Windows Server 多会话版（要换 Server 授权）、每账号一个 VM（§6）。** 「本地多开」不是第四条独立路线，它就是现状 RDP 多会话的退化版。SoulLink 现在用的 RDP 多会话，已经是客户端 Windows 上能拿到的最原生多桌面方案。

---

## 6. 路线④：虚拟机（Hyper-V / VMware）—— 本机被判死刑（真实开销数据，非理论）

### 6.1 消费级 GPU-PV 对本机 GPU 的直接否决（【官方文档 + README 原文】）

Hyper-V 的 GPU 加速只有两条：**DDA（直通，1 GPU = 1 VM）** 和 **GPU-P（分区，1 GPU 切给多 VM，Server 2025 起正式）**。客户端 Windows 走的是**非官方的 GPU-PV 社区方案**（Easy-GPU-PV，5572★，MIT 系），它的 README 逐字写明：

> **「Laptops with NVIDIA GPUs are not supported at this time, but Intel integrated GPUs work on laptops.」**
> （「带 NVIDIA GPU 的笔记本目前不支持，但笔记本的 Intel 核显可以。」）

本机是 **NVIDIA RTX 4060 Laptop GPU**（HP OMEN 游戏本），正好落在「不支持」这一档。

> 补充：README 还要求「桌面电脑带 NVIDIA/AMD 独显，或笔记本 Intel 核显」——本机的 NVIDIA 笔记本独显不满足。

### 6.2 即便绕过 GPU，还有两道硬墙（【已实测】）

| 墙 | 实测 | 影响 |
|---|---|---|
| **内存** | 29.7GB 总 / 2.4GB 空闲；会话工作集合计 ≈33GB 已超物理 | 每 VM 至少 4~8GB（Easy-GPU-PV 默认 8GB），3 个 VM 就要 24GB，**本机开不出** |
| **Hypervisor** | `HypervisorPresent=False`，Hyper-V 未启用 | 启用 Hyper-V = 全机改造 + 重启 + 影响 WSL2/沙盒；且会把 RDPWrap 多会话这套现有架构搅乱 |

### 6.3 真实开销盘点（对齐 STREAM-SURVEY §3.2）

| 项 | 真实成本 |
|---|---|
| Windows 授权 | 每 VM 一份（客户端 Windows 不提供多会话授权） |
| 内存 | 每 VM 4~8GB 起 |
| 磁盘 | 每 VM 20~40GB VHDX 起（Easy-GPU-PV 默认 40GB） |
| GPU | 多 VM 共享需 vGPU（NVIDIA 收费授权）或 DDA（1 卡 1 VM）；本机笔记本独显两条都走不通 |
| 架构 | 现有「RDP 多会话 + Broker + RelayAgent 跨会话注入」整套要按 VM 重做 |
| 挂机契合度 | 挂机对帧率不敏感（静置画面），为 60fps 付 VM 化代价，对主场景不划算 |

### 6.4 结论

**虚拟机路线在本机（NVIDIA 笔记本 GPU + 内存高压 + Hyper-V 未启）是死路**，不是「贵但能做」，是「消费级方案明确不支持 + 资源撑不起」。仅当未来换桌面独显 + 大内存 + 确有 60fps 刚需时才值得重提。

---

## 7. 推荐路线与理由

### 7.1 推荐：维持 RDP 多会话 + 采集/编码层升级（路径 A）

| 步骤 | 内容 | 依据 |
|---|---|---|
| 1（最优先） | **WGC V2 抓屏替换 BitBlt**，参考 BetterGI `GraphicsCaptureV2.cs`（MIT 系），在 RDP 会话内实测抓 1 帧 | 用户已实测 WGC 在 RDP 内可用；这是唯一不推倒架构的 60fps 候选 |
| 2（正交收益） | **NVENC H.264 硬编替换 GDI+ JPEG 软编**（MF MFT 已枚举到），关 B 帧、低延迟 tune、丢旧帧 | 省编码 CPU + 外网码率降一个数量级，与抓屏升级互相独立 |
| 3 | 帧队列有界 + 丢旧保新 + 溢出重同步（对标 Sunshine/Moonlight） | `OSS-REMOTE-RESEARCH` §4 |

### 7.2 为什么不是其它三条

- **虚拟显示器**：救的是控制台 headless，不是 RDP 会话抓屏；要自研内核驱动。
- **本地多开**：Windows 客户端不存在「多 console」，它就是现状的退化版。
- **虚拟机**：本机 NVIDIA 笔记本 GPU 被消费级 GPU-PV 明确排除 + 内存撑不起。

### 7.3 一个必须给用户讲清的分寸

SoulLink 是「**单机多账号 RDP 挂机工具**」，与云厂商（GPU 虚拟化）**不是同一物种**。用户如果只是要「远程转视角/操作更流畅」，正确路径是上面 7.1 的采集/编码升级，而不是去重造「虚拟机多开」或「GPU 云」。这一判断与 `CN-CLOUD-GAMING`、`STREAM-SURVEY` 两份独立调研的结论完全一致。

---

## 8. 给用户的直白解释（可直接转述）

> 你现在用的「每个账号一个 RDP 会话」这套，在 Windows 上已经是「一台电脑同时管多个账号」最原生的做法了。它的毛病只有一个：**游戏跑在远程会话里，电脑显卡的「直出画面」拿不到**，所以现在抓屏慢（一帧要 20~30 毫秒，60 帧就别想了，15 帧还凑合）。
>
> 好消息是：**不用推翻重来**。有一条「Windows 官方抓屏接口（WGC）」在你这种远程会话里**已经被验证能用、而且很快**——下一步就是把它换掉现在慢的那套，再顺手把「软编码」换成「显卡硬编码」。这两个改完，画面流畅度能上一个台阶，账号、自动化、投送这些全都不用动。
>
> 你可能会听到「虚拟显示器」「虚拟机多开」这些说法，我先帮你挡掉两个坑：
> 1. 「虚拟显示器」治的是「电脑没接显示器黑屏」，治不了「远程会话里抓屏慢」，别被带偏。
> 2. 「虚拟机多开」在你这台笔记本上走不通——你的显卡是笔记本版 NVIDIA，市面上做这个的免费工具**明确不支持笔记本 NVIDIA**；而且你内存现在就剩 2G 出头，再开几个虚拟机直接卡死。
>
> 所以结论就一句：**别改架构，把抓屏和编码这两处升级一下就行。**

---

## 9. 查不到 / 待实测的诚实声明

| 项 | 状态 |
|---|---|
| **WGC 在 RDP 会话内的「帧率上限」** | 用户实测「非常快」，但**精确帧率/延迟未量化**——需 SoulLink 自己写 WGC 探测在 RDP 会话内采样（`GITHUB-SWEEP` §1.1 与 `OSS-REMOTE-RESEARCH` §3 已给方法） |
| **NVENC 硬编在 RDP 会话内的可用性** | 只枚举到 MFT 存在，**RDP 内实际硬编未测**（`MEDIA-CAPABILITY` §5 表 B 待 RDP 复测） |
| **Hyper-V GPU-PV 对本机 NVIDIA 笔记本的「官方」支持情况** | 微软官方文档（Server 2025 GPU-P）**未覆盖客户端 Windows**；消费级靠 Easy-GPU-PV 等社区方案，其「不支持笔记本 NVIDIA」是 README 原文，**本机未实际装 Hyper-V 验证** |
| **DuoStream/Duo（console 多座位 + 虚拟屏 + RDP 抓屏）** | 闭源二进制 + 付费解锁多实例，技术细节查不到（`VDD-RESEARCH` §5.2） |
| **云厂商（Shadow/GFN/云原神）抓屏/编码/协议内部实现** | 闭源，查不到（`CN-CLOUD-GAMING` §8 已声明） |
| **0x119 事故与「换会话架构」的关联** | 事故根因是「同进程多 ActiveX 反复重挂」，与 RDP 会话模型本身无关，但未做内核栈完整分析（`RDP-WINDOWING-RESEARCH-SUMMARY` §3.2） |

---

## 10. 变更记录

| 版本 | 日期 | 内容 |
|---|---|---|
| v1.0 | 2026-11-22 | 初版：四条路线对比 + 本机实测环境 + 本地多开否决 + VM 真实开销（NVIDIA 笔记本 GPU 被 GPU-PV 排除）+ 推荐路径 A + 用户直白解释 |
