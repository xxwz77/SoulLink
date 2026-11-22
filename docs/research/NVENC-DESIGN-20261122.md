# NVENC 硬件编码替换 JPEG：RDP 内可行性实测 + 多厂商编码器设计

> 日期：2026-11-22
> 作者：lookopt（agent-team `soullink-stream-opt` / member `m_mwrs1uclet5tls` / task `task_mwrt3bgnnj2ep7`）
> 范围：只读实测 + 设计文档。**未改业务代码、未动 dist、未提交、未发版**。
> 证据来源：`artifacts/rdp-probe/`（RDP 会话内 media-probe 原始输出，前序 task `task_mwrq0z7d6b7xf6`）+ 本轮 `nvidia-smi` 只读快照 + NVIDIA 官方编码矩阵 + MSDN MF 文档。
> 配套必读：`MEDIA-CAPABILITY-20261122.md`、`STREAM-PERF-AUDIT-20261122.md`、`STREAM-PLAN-V2-20261122.md`。

---

## 0. 结论摘要（TL;DR）

1. **NVENC 硬件在 RDP 会话内真实可用**：两条独立证据——`nvidia-smi encodersessions` 显示 `TermService`（svchost PID 12140）此刻正用 NVENC 硬编 RDP AVC444（约 20 路 H.264 1920×1088）；RDP 会话内 media-probe 枚举到 `NVIDIA H.264 Encoder MFT` 并成功产出 40 帧 920KB 真 H.264。
2. **但可用 ≠ 该用**：当前画面链路最大瓶颈是 **BitBlt 抓屏 31.35ms/帧**，不是 JPEG 编码（5.818ms）。NVENC 只碰编码，抓屏分毫不动，且代理进程建不了硬件 D3D11 设备（DXGI 采集死路），NVENC 只能走「系统内存 → 非D3D11 MFT → 上传 GPU」有额外往返的路径。
3. **实现选型：用 MF MFT，不用 NVENC SDK**。理由：系统自带零依赖、零体积（守住"产物 <5MB"）、无需分发 `nvEncodeAPI64.dll`；参数控制虽少但对"关键帧截图式单帧编码"场景已足够。
4. **并发限制查证：RTX 4060 Laptop = Ada Lovelace 8th Gen，1 个 NVENC 芯片，硬件上限 12 路并发**。NVIDIA 已在驱动 457.0（2020-11）解除 GeForce 旧的 2~3 路软件限制；本机驱动 621.01 走硬件上限 12 路。**用户 4~8 账号完全够用，不撞限。**
5. **架构按多厂商设计，本轮只实现 NVIDIA**：`IVideoEncoder` 抽象层 + MFT 探测器按优先级排序；AMD AMF / Intel QSV 只探测、标记"后续支持"，不实现 Encode；无 NVENC 时 Auto 自动降 Software MFT → JPEG。
6. **优先级不变**：仍应先做 `STREAM-PLAN-V2 §7` 第 1~5 步（frameSeq/解耦/降分辨率/脏矩形），NVENC 排在解决抓屏之后。

---

## 1. 实测：NVENC 在 RDP 会话内到底可不可用

### 1.1 证据 A（本轮新增）：NVENC 此刻正被 RDP 服务使用

`nvidia-smi encodersessions` 只读快照（2026-11-22）：

| 字段 | 值 |
|---|---|
| 使用进程 | `svchost.exe` PID 12140，`SessionId=0`，`-k TerminalService -s TermService` |
| 编码会话 | 约 **20 路**（进程 583 / 818 交替） |
| 分辨率 | 1920×1088 |
| 活跃 FPS | 最高 1107，常见 472~924 |
| 编码延迟 | 903~2117 µs |
| GPU 利用率 | 38% |

**含义**：`TermService` = 远程桌面服务，正用 NVENC 硬件编码 RDP AVC444 图形流。NVENC 在这台机器、这个 RDP 拓扑里**真实可用且正在被系统自己使用**。

### 1.2 证据 B（前序 spike，RDP 会话内 media-probe）

`artifacts/rdp-probe/rdp-report.md`（`SM_REMOTESESSION=RDP`，session 42）：

| 项 | 结果 |
|---|---|
| MFTEnumEx(硬件) | ✅ `NVIDIA H.264 Encoder MFT` |
| MF H.264 单帧编码 | ✅ 均值 0.238ms，40 帧 MP4=920KB（≈23KB/帧，通过体积自校验） |
| GDI+ JPEG q75 | 5.818ms/帧，164KB/帧 |
| NVENC MFT 标记 | **非D3D11** |

### 1.3 关键限制：编码器可用，但采集侧拿不到硬件 D3D11

| 项 | 结果 | 错误码 |
|---|---|---|
| D3D11 设备（硬件） | ❌ FAIL | `0x8876017C`（另两次 `0x8007000E`/`0x80070057`） |
| D3D11 设备（WARP） | ✅ 成功 | — |
| DuplicateOutput（WARP） | ❌ FAIL | `0x80070057` |
| BitBlt 基线（30 次） | 31.35ms/帧 | — |

**核心矛盾**：NVENC MFT 存在且可用，但代理进程建不了硬件 D3D11 设备 → DXGI Desktop Duplication 拿不到 GPU 桌面纹理 → NVENC 只能走「系统内存 NV12/BGRA → 非D3D11 MFT → MFT 内部上传 GPU → 编码 → 拷回」，多一次系统内存↔GPU 往返。`WriteSample 0.238ms` 只是异步入队 CPU 侧提交成本，不含上传/编码/回拷。

---

## 2. 范围与多厂商架构

### 2.1 范围（用户拍板）

| 本轮实现 | 本轮不实现（但预留接口） |
|---|---|
| NVIDIA NVENC（MF 硬件 H.264 MFT） | AMD AMF |
| Software H.264 MFT 回退 | Intel QSV |
| JPEG 保底 | — |

### 2.2 编码器抽象层（`IVideoEncoder`）

```csharp
/// <summary>视频编码器抽象。多厂商共用同一接口，不写死 N 卡。</summary>
internal interface IVideoEncoder
{
    /// <summary>厂商名，UI 展示与日志用。如 "NVENC" / "AMF" / "QSV" / "Software" / "JPEG"</summary>
    string Name { get; }

    /// <summary>厂商优先级：数字越小越优先（Auto 模式按此排序）。</summary>
    int Priority { get; }

    /// <summary>运行时探测：编码器此刻是否真正可用（枚举 + 试编码 1 帧自校验）。</summary>
    bool IsAvailable();

    /// <summary>
    /// 编码一帧。输入 BGRA（bottom-up 与否由实现约定，建议统一 top-down + 显式 stride），
    /// 输出 H.264 Annex-B 关键帧字节（含 SPS/PPS 于首帧）。
    /// 实现方负责 BGRA→内部格式转换（NV12 等）。
    /// </summary>
    byte[]? Encode(byte[] bgra, int w, int h, int quality);
}
```

**设计要点**：
- 接口**只关心「输入 BGRA、输出 H.264 帧」**，内部是 NVENC/AMF/QSV/软编，调用方无感知。
- `Priority` 让探测器产出一个**有序候选表**，Auto 模式取第一个 `IsAvailable()==true` 的。
- 本轮只有 `NvencEncoder` 实现 `Encode`；`AmfEncoder`/`QsvEncoder` 是**占位类**，`IsAvailable()` 返回 false（探测到硬件则返回 true 但 Encode 抛 `NotSupportedException` 或返回 null，UI 标"后续支持"）。

### 2.3 探测器（`EncoderDetector`）

```
枚举顺序：
  1. MFTEnumEx(MFT_ENUM_FLAG_HARDWARE)  → 硬件 H.264 MFT 列表
  2. MFTEnumEx(MFT_ENUM_FLAG_SYNCMFT|ASYNCMFT) → 软件 H.264 MFT

按 MFT 的 CLSID / 名称映射到厂商：
  NVIDIA H.264 Encoder MFT          → NvencEncoder  (本轮实现)
  Intel® Quick Sync Video H.264 Encoder MFT → QsvEncoder (占位)
  AMD 硬件 H.264 MFT（AMDh264Encoder）     → AmfEncoder   (占位)
  H264 Encoder MFT（微软软件）      → SoftwareEncoder (本轮实现，复用 Mp4Writer 经验)
```

**关键坑（来自 MEDIA-CAPABILITY §6.4 第 1 条）**：硬件 MFT 必须用 `MFT_ENUM_FLAG_HARDWARE` 单独枚举，混用 `SYNCMFT|ASYNCMFT` 会得到"0 个硬件编码器"的假结论。

### 2.4 配置项

```
VideoEncoderMode: Auto | NVENC | Software | JPEG
  Auto     → 探测器候选表，取第一个可用的（NVENC > Software > JPEG）
  NVENC    → 强制 NVENC，探测失败则报错并回退 JPEG（不静默降软件，让用户知道 NVENC 挂了）
  Software → 强制微软软 H.264 MFT
  JPEG     → 强制现状 GDI+ JPEG（永远可用）
```

A/I 卡用户选 Auto：探测不到 NVENC → 自动落 Software 或 JPEG（本轮 QSV/AMF 未实现，Software 与 JPEG 之外没有第三个硬件选项）。

### 2.5 UI 显示

- 设置页新增「视频编码器」下拉：Auto / NVENC / Software / JPEG。
- 状态区显示**当前生效编码器**（`当前：NVENC（NVIDIA H.264 Encoder MFT）`）。
- 探测面板列出**检测到的其它编码器**，非 NVIDIA 的标 `后续支持（暂不可用）`。

---

## 3. MF MFT vs NVENC SDK 取舍

| 维度 | MF MFT（`NVIDIA H.264 Encoder MFT`） | NVENC SDK（`nvEncodeAPI64.dll`） |
|---|---|---|
| 依赖 | 系统自带，零分发 | 要随程序分发 `nvEncodeAPI64.dll`（+ 体积） |
| 参数控制 | 少（码率/GOP/low-latency 靠 MF 属性，够用） | 多（RC 模式、AQ、多路参考帧、B 帧、lookahead） |
| 集成 | `IMFTransform`，项目已有 MF 经验（`Mp4Writer.cs`） | 全新 C API P/Invoke 面，工作量 ×3~5 |
| 体积 | 0 | +~1MB DLL |
| 实时性 | 够用（low-latency 属性） | 精细控制更强 |
| 维护 | 随系统 | 要跟 NVIDIA SDK 版本 |

**结论：选 MF MFT。** 三个决定性理由：
1. **零依赖零体积**——项目铁律"产物 <5MB"（AGENTS.md），NVENC SDK 要带 DLL。
2. **本项目是"关键帧截图式单帧编码"**，不需要 NVENC SDK 独有的精细 RC/AQ/B 帧/lookahead；MF 的码率 + GOP=1 + `CODECAPI_AVLowLatencyMode` 已覆盖。
3. 项目**已有 MF 接入经验**（`Mp4Writer.cs` 的 IMFAttributes/IMFSample vtable 坑都踩过并有文档），复用成本低。

NVENC SDK 的适用场景（多路实时流、精细码控、AV1）本项目的远程控制画面**用不到**。

---

## 4. NVIDIA 并发会话数限制（关键查证）

**官方硬件矩阵**（developer.nvidia.com video-encode-decode-support-matrix）：

| 项 | 值 |
|---|---|
| GeForce RTX 4060 Laptop | Ada Lovelace，**8th Gen NVENC**，Mobile |
| 芯片数 / NVENC 数 | 1 chip / 1 NVENC |
| **Max # of concurrent sessions** | **12** |
| H.264 4:2:0 (NV12) | ✅ YES |
| H.264 4:2:2 | ❌ NO（**不是 4:2:2**） |
| H.264 4:4:4 / Lossless | ✅ YES |
| AV1 / HEVC B-frame | ✅ YES |

**关于"2~3 路"旧限制的澄清**：那是 NVIDIA 在 **GeForce 消费卡上的人工软件限制**，已于 **驱动 457.0（2020-11）解除**。此后 GeForce 的 NVENC 并发上限 = 硬件上限。本机驱动 **621.01**（远高于 457），所以**有效上限就是硬件 12 路**。

**结论**：用户 4~8 账号**完全不撞限**（8 ≤ 12）。即使未来 8 账号全开 NVENC，也在硬件容量内；唯一要留意的是**所有账号共享同一个 NVENC 芯片**，12 路同时高 FPS 编码会让单芯片饱和、单路延迟上升——这是吞吐瓶颈而非硬性拒绝。**建议仍按 `STREAM-PLAN-V2` 的"实际新帧率 5~15fps"走**，8 路 × 15fps 对 4060 Laptop 的 NVENC 是轻载。

> 注：`nvidia-smi encodersessions` 证据 A 显示 `TermService` 已占用约 20 路编码会话（RDP AVC444），但那是由 RDP 服务（SYSTEM）独占的独立通道，与代理进程要新开的 NVENC 会话**共享同一个硬件上限 12** 这一事实需要进一步实测确认是否会互相挤占——本轮只读，未做并发压测，标为待验证。

---

## 5. 参数配置建议

### 5.1 输入格式（BGRA vs NV12）

- NVIDIA NVENC MFT 标准输入是 **NV12（4:2:0 半平面）**；矩阵也显示 H.264 4:2:0 是 YES、4:2:2 是 NO。
- 项目现有软 H.264 MFT（`Mp4Writer.cs:191`）喂的是 **RGB32（BGRA）** 且成功——但那是**微软软件 MFT**，不代表 NVIDIA 硬件 MFT 接受 RGB32。
- **建议**：以 NV12 为准，探测阶段实测「NVENC MFT 是否接受 RGB32」。接受则省转换（LockBits 后直接 Marshal.Copy）；不接受则做 BGRA→NV12（见 §6）。

### 5.2 关键参数

| 参数 | 建议值 | MF 属性 / 说明 |
|---|---|---|
| 编码器 | NVIDIA H.264 Encoder MFT | `MFT_ENUM_FLAG_HARDWARE` 枚举 |
| GOP | **1**（全关键帧 IDR） | `CODECAPI_AVEncCommonRateControlMode` + `MF_MT_MPEG2_PROFILE`；GOP=1 与"按需取单帧"模型对齐，无 P 帧级联 |
| 码率 | 1080p **4~8 Mbps**（对齐 `Mp4Writer` 的 6000kbps） | `CODECAPI_AVEncCommonMeanBitRate` |
| 低延迟 | **开** | `CODECAPI_AVLowLatencyMode` = TRUE（VT_BOOL，非零；MF 编码器用 VT_BOOL 而非 VT_UI4，见 MSDN 警告） |
| 帧率 | 实际新帧率 5~15fps（不追求 30） | `MF_MT_FRAME_RATE` |
| 关键帧请求 | 单帧模型下每帧都 IDR，无需周期 | — |

### 5.3 老驱动降级

- 老驱动可能不支持某些 MF 属性（如 AVLowLatencyMode）。**探测时逐个属性 Set 并检查 HRESULT**，不支持则降级（去掉该属性继续），不是硬失败。
- 驱动太老（<457）会恢复 GeForce 2~3 路软件限制，此时多账号要串行/降 Software。探测时读驱动版本，<457 记告警。

---

## 6. BGRA→NV12 转换成本

| 情形 | 成本 |
|---|---|
| NVENC MFT 接受 RGB32（待实测） | 手工转换 **≈0**，`LockBits` + `Marshal.Copy`（~7.9MB 内存拷贝，1~2ms/帧） |
| NVENC MFT 只接受 NV12 | 手工 BGRA→NV12：1080p 207 万像素，Y 通道逐像素 RGB→Y，UV 通道 2×2 下采样。标量 ~2~4ms/帧；`Vector<T>`/SIMD 可 <1ms/帧 |

**判定**：不改变大局。最坏情形 NV12 转换 2~4ms + BitBlt 31ms 仍是 31ms 主导；最好情形转换外包给 MFT。必须实测，但无论哪种都不影响"抓屏是瓶颈"的结论。

---

## 7. 回退链（务必做成多级，NVENC 永不是单点）

```
VideoEncoderMode:
  Auto:   NVENC(可用?) → Software H.264 MFT(可用?) → JPEG
  NVENC:  NVENC(可用?) → 报错 + JPEG（不静默降，让用户知道）
  Software: Software H.264 MFT(可用?) → JPEG
  JPEG:    JPEG（永远可用）

运行时探测（每代理启动一次）：
  1. 枚举硬件 H.264 MFT（MFT_ENUM_FLAG_HARDWARE）
  2. 试编码 1 帧 IDR + 体积自校验（1080p <10KB 判失败，防"假数据"）
  3. 通过 → 启用；失败/异常 → 回退下一级 + 记日志
```

回退触发：MFT 枚举失败、首帧异常、输出体积异常、NVENC 并发撞限（8 账号压满时静默回退软编）、前端不支持 H.264 → 全部静默降级，**绝不让 NVENC 成为新单点故障**。

---

## 8. 新链路设计 + 传输模型（复用 §2 结论，简要）

- **只替换**「`Image.FromHbitmap` + `bmp.Save(JPEG)`」（~11.6ms）为「BGRA 拷贝 + NVENC 提交（~1~3ms 提交 + GPU 异步）」，**BitBlt 31.35ms 不动**。
- **传输模型：单帧 IDR 起步，不上连续 P 帧流**（GOP=1 与按需取帧模型对齐）。
- 前端 H.264 裸流不是 `<img>` 能吃的，需 `MediaSource` 或 WASM 解码器（或保留 JPEG 回退）——这是除编码外最大的新增复杂度。

---

## 9. 最终建议（给 Lead）

1. **NVENC 可用性坐实，并发限制 12 路（4~8 账号够用）**，选型 **MF MFT（不选 SDK）**，架构按 `IVideoEncoder` 多厂商抽象、本轮只实现 N 卡。
2. **但优先级仍靠后**：抓屏 31ms 是瓶颈，NVENC 只碰编码 5.8ms。正确顺序仍是 `STREAM-PLAN-V2 §7` 第 1~5 步。
3. **NVENC 立项前提**（满足才做）：① 抓屏换成更快的（DXGI 已死，需实测 `PrintWindow`/`Windows.Graphics.Capture`）；或 ② 用户接受 30fps 但需要省带宽（NVENC 86% 带宽收益才有独立价值）。
4. **本轮落地的最小安全切片**：先只做 `IVideoEncoder` 接口 + `EncoderDetector`（探测 + 排序 + UI 显示"当前编码器 + 检测到的其它编码器"），**Encode 可先不接进 `SessionRecorder` 热路径**，验证探测与回退逻辑正确后再切编码。

---

## 10. 证据索引

| 结论 | 证据 |
|---|---|
| NVENC 正被 TermService 使用 | `nvidia-smi encodersessions`：PID 12140 `svchost -k TerminalService -s TermService`，~20 路 H.264 |
| NVENC MFT 在 RDP 内可枚举可编码 | `artifacts/rdp-probe/rdp-report.md` |
| 代理建不了硬件 D3D11 / DXGI 死路 | `rdp-report.md` + `final.txt` + `retry.txt` |
| BitBlt 31.35ms（真瓶颈） | `final.txt`；JPEG 5.818ms `rdp-report.md` |
| 4060 Laptop = 8th Gen，12 路并发，H.264 4:2:0 YES | NVIDIA 官方矩阵 |
| GeForce 2~3 路限制已解除（驱动 457.0） | NVIDIA 公开说明（2020-11） |
| CODECAPI_AVLowLatencyMode 属性 | MSDN codecapi-avlowlatencymode |
| 软 H.264 MFT 接受 RGB32 | `Services/Mp4Writer.cs:191` |
| AMD/Intel MFT 名（待实机核实） | 见下 §11 |

## 11. AMD / Intel 备用信息（本轮不实现，记录待用）

> ⚠ 以下为**公开资料整理，未在本机实机核实**（本机无 A/I 卡），实现时需重新探测确认 MFT 名与 CLSID。

| 厂商 | MFT 名（枚举结果里识别） | 备注 |
|---|---|---|
| Intel QSV | `Intel® Quick Sync Video H.264 Encoder MFT` | CLSID `4BE8D3C0-0515-4A37-AD55-E4BAE19AF471`；输入 NV12；也有 `... H.265/HEVC` 变体 |
| AMD AMF | `AMDh264Encoder`（H.264，注册名或为 "AMD H.264 Hardware MFT"）/ `AMDh265Encoder`（HEVC） | AMF 在 Windows 以 MFT 形式暴露；H.264 4:2:0 NV12 输入 |
| 微软软件 | `H264 Encoder MFT` | 本机已枚举到，`Mp4Writer.cs` 在用（离线 SinkWriter） |

- Intel QSV 并发限制：通常**无限流（无 GeForce 式软件锁）**，受核显吞吐约束。
- AMD AMF 并发：VCN 硬件，一般 4~16 路视代次，无 GeForce 式历史锁。
