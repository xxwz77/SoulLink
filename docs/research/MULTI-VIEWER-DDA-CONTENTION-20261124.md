# 多观看者 DDA 争用诊断：不是“两个客户端必减半”，而是 SoulLink 的采集处理与分发路径

> 日期：2026-11-24；作者：`deep-sunshine`；任务：`task_mwu0iryo2t9ueu`。
> 范围：读取真实产品源码/日志，在授权的 A2 / session 4 用临时隐藏计划任务做独立 DDA、CPU 读回、共享 viewer 对照；没有修改产品代码、没有改注册表、没有使用 A1/A4、没有操作挂机版 PID 13392。
> 证据标记：【实测】=本报告临时程序直接计数；【日志实测】=A2 实际 agent 的 `viewers`、编码和队列帧龄；【源码事实】=本次读取工作区；【推断】=由对照得出但未做驱动级跟踪的原因。

## 0. 最重要的结论

1. **SoulLink 已经是共享 DDA。** 每个 agent 只有一份静态 `Capture`、一个 `SessionRecorder.Loop()`、一个编码器；`Viewers` 字典只保存各 viewer 的 NAL 待发队列。正常用户打开两个或三个观看窗口，**不会各建一个 DDA，也不会重复抓屏或编码**。
2. **独立 DDA 客户端数量本身不会造成 66→32fps。** 同一全屏动画下：1 个独立客户端 65.976fps；2 个分别 65.702/65.706fps；3 个分别 66.053/66.017/66.060fps。不能再用“两个 DDA 客户端互相 Present 降速”作为架构结论。
3. **约 32fps 是 SoulLink 采集/处理/同步路径的历史实测，不是 DDA 客户端数量的必然结果。** 独立探针不读回、不转换、不编码；SoulLink 当时与探针同时工作，双方每帧工作不同，不能只按“两个客户端”归因。
4. **当前 Intel 抓屏 + NVIDIA NVENC 走 CPU 路径，结构性开销仍然存在。** 每帧 DDA→staging→Map→CPU BGRA→CPU NV12→GPU NV12，1920×1080 的 BGRA 每帧约 8.29MB，且当前实现每帧新建 staging、byte[] 和 NV12 数组。这个开销需要消除，而不是增加 viewer 的队列。
5. **共享多 viewer 的另一个风险是慢消费者，不是抓屏复制。** 每个 HTTP viewer 有独立 Pending 队列，当前上限 2 帧，丢旧保新；`queueAge` 只含服务端待发等待，**不是浏览器到上屏的总延迟**。`WriteStreamAsync` 每路独立异步写出，慢 viewer 会丢帧，不应反向拖慢共享 DDA。
6. **实施优先级**：先修采集处理与测帧龄，保留共享管线；不要改成每 viewer 一个 DDA，也不要仅仅扩大队列“解决卡顿”。GPU 路径必须处理 Intel→NVIDIA 跨 GPU 的真实拓扑；不能把同 GPU 的共享纹理直通当作通用可用。

---

## 1. SoulLink 当前架构的源码确认

### 1.1 采集与编码只存在一份

`Relay/SessionRecorder.cs:12-41`：

```csharp
internal static class SessionRecorder
{
    private sealed class Viewer
    {
        public readonly object Sync = new();
        public readonly Queue<(byte[] Nal, DateTime At)> Pending = new();
        public bool Closed;
    }

    private static IScreenCapture Capture = CreateCapture();
    private static readonly object Gate = new();
    private static readonly List<VideoFrame> Ring = new();
    private static readonly Dictionary<int, Viewer> Viewers = new();
    private static Thread? _worker;
}
```

`Loop()` 是唯一采集编码线程；一帧 NAL 产生后，在 `:1027-1040` 遍历 `Viewers`，把**同一个 `byte[]` 引用**放进各队列。不是复制 BGRA，也不是每 viewer 编码一次。

`TrySubscribe()`（`:1090-1117`）只做：

```csharp
viewerId = ++_nextViewerId;
Viewers[viewerId] = new Viewer();
_forceKeyFrame = true;
RecomputeLifecycle();
// 如果已有 Ring，则取最近的 IDR 作 initial
StartCore(...); // 已运行时直接返回“共享 H.264 管线已在运行”
```

`AgentWebServer.WriteStreamAsync()` 每个 HTTP 请求独立调用 `TrySubscribe()` 和 `ReadViewerFrame(viewerId)`，并独立异步 `WriteAsync/FlushAsync`。所以多 viewer 共享 DDA，但每路网络消费者确实不同。

### 1.2 为什么“多 viewer 已共享”仍可能看到帧率降低

三层原因必须分开：

| 层 | 是否随 viewer 数增加 | 真实机制 |
|---|---|---|
| DDA/编码生产 | 不增加 | 只有一个 `Capture`/`Loop`/编码器 |
| 每 viewer 待发与网络写出 | 增加 | 各自 Pending 和 HTTP 异步写出，慢者会丢旧帧 |
| 共享 agent 与额外独立探针 | 不是正常 viewer 行为，但会争用 GPU/CPU/设备锁 | agent 还做 staging/CPU 转换/编码，额外探针额外占资源 |

如果基线采集本身已只能 30–50fps，再开 viewer 只是分发这些帧；客户端显示出的帧率不可能凭订阅而增加。测量应把 `Capture.FrameSequence`、编码 `total`、每个 viewer 实收 AU、客户端上屏分开。

---

## 2. 独立 DDA 客户端：1 / 2 / 3 个的完整复现实测

### 2.1 测量条件

- A2 RDP session 4，DDA 输出 Intel UHD、1920×1080、`refresh=66/1`。
- 全屏 WPF 动画，48 个持续移动矩形，约 65fps 自身 Rendering callback。
- 每个探针进程自己创建 D3D11 device 和 `IDXGIOutputDuplication`，连续 `AcquireNextFrame`、读取 `AccumulatedFrames`、立即 `ReleaseFrame`。
- **没有 staging、CPU Map、CPU 转换或编码**。各探针单独写结果，避免 stdout 被计划任务吞掉。
- 使用 A2 交互计划任务启动，不在主控会话弹窗。

### 2.2 结果

| 独立 DDA 客户端数 | 客户端 | 时长 | Acquire 成功/秒 | 累积桌面帧/秒 | 超时 | 错误 |
|---|---|---:|---:|---:|---:|---:|
| 1 | A | 15.005s | 990 / 65.976 | 990 / **65.976** | 0 | 0 |
| 2 | A | 15.007s | 987 / 65.768 | 986 / **65.702** | 1 | 0 |
| 2 | B | 15.006s | 986 / 65.706 | 986 / **65.706** | 1 | 0 |
| 3 | A | 15.003s | 992 / 66.119 | 991 / **66.053** | 1 | 0 |
| 3 | B | 15.011s | 993 / 66.150 | 991 / **66.017** | 1 | 0 |
| 3 | C | 15.002s | 992 / 66.126 | 991 / **66.060** | 0 | 0 |

**结论【实测】**：多个独立 DDA 客户端在这台机器和此负载下均可接近 66fps，既没有互斥串行到一半，也没有第三个客户端导致崩溃。

原始结果保存在 `%TEMP%\soullink-rdp-framerate\dda-isolated*.txt`，包括 PID、时间、设备和刷新率，不在工作区囤积二进制。

### 2.3 这对根因分析意味着什么

- **不能证明 DDA 设备锁存在“两个客户端自动一半”的规则**；实测反证了这个假设。
- Microsoft `AcquireNextFrame` 文档规定的是帧获取、ReleaseFrame 和 timeout 语义，没有“多客户端限半速”。参见 [官方 AcquireNextFrame](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgioutputduplication-acquirenextframe)。
- 本任务未做 ETW/D3D driver wait-chain 跟踪，**不声称已证明 DDA 内部完全无锁**。设备锁仍可能在线程/进程使用 D3D11 context 时存在，只是它不是客户端数的函数。
- 前一报告“agent + 独立探针约 32fps”只能视为当时组合路径的现象。要解释它必须比较 agent 的额外工作，而不是简单把探针数称为 viewer 数。

---

## 3. 为什么 SoulLink 的 DDA 与裸探针不一样：读回成本分离实测

### 3.1 同一动画、单个 DDA 客户端，只改变每帧处理

| 模式 | 时长 | Acquire/秒 | 累积桌面/秒 | staging+Map 平均 | CPU BGRA→NV12 平均 |
|---|---:|---:|---:|---:|---:|
| metadata-only | 15.010s | 989 / **65.891** | 989 / **65.891** | — | — |
| staging + Map + CPU 数组复制 | 15.007s | 989 / **65.903** | 990 / **65.969** | 5.840ms/帧（其中 Marshal.Copy 0.497ms） | — |
| 上述 + CPU NV12 转换 | 15.005s | 944 / **62.911** | 987 / **65.776** | 5.787ms/帧（其中 Marshal.Copy 0.525ms） | **5.446ms/帧** |

注意累积桌面与 Acquire 的差：转换路径少取了 43 次事件但桌面累计仍接近 66，说明客户端已开始合并/丢帧，不能用“合成器变慢”解释所有缺口。

静态桌面附加对照中，staging+Map 为 5.856ms，CPU 转换为 10.365ms；静态样本不是吞吐场景，不混入动态主表，只作为 CPU 成本随 JIT/负载变化的补充。

### 3.2 当前产品的额外开销

`Relay/DxgiDdaCapture.cs`：

- `CaptureBgra()` 在 `:265-295` 调 `AcquireNextFrame`，然后 `ReadBackTexture()`；
- `ReadBackTexture()` `:434-469` **每帧**创建 staging，`CopyResource`、`Map(READ)`、分配 `new byte[width*4*height]`、复制后 `Unmap` 和销毁；
- `TryCaptureBgraTexture()` 是另一条 GPU 路径，但当前 Intel 抓屏 + NVIDIA 编码不满足同 GPU 条件，实际日志明确 `同GPU=false; 零拷贝=禁用`；
- `SessionRecorder.Loop()` 再 `BgraToNv12()`，然后 NVENC `EncodeNv12()` 把 CPU NV12 上传到 GPU 输入纹理。

这条链含 GPU→CPU 和 CPU→GPU 两次跨越，不能拿只 Acquire/Release 的 66fps 当作 agent 吞吐。

**根因强度【推断，源码+对照支持】**：66→32 的主要候选是当前 agent 的 CPU 读回/转换/编码及帧所有权持有时长，可能叠加 D3D11 device/context 争用；**不是多客户端数量，也不是桌面 Present 自动减半**。需要用 GPU/线程 profiler 才能把锁等待再拆成具体驱动调用，不能凭空选一个锁名。

---

## 4. 共享 viewer：有效 1 / 2 / 3 观看端数据与延迟边界

### 4.1 用同一个 agent 测得的共享 viewer

下表只列能把 `viewers` 和同一 agent 的日志对应的区间；不同配置的 fps 不强行比较为 A/B。

| viewer 数（同一 Capture） | 证据区间 | 生产端实际速率 | 队列延迟/丢帧 | 可用结论 |
|---|---|---:|---|---|
| 1 | PID 35400，09:44:49.645 total=30 → 09:45:11.557 total=1320 | 1290 / 21.912s = **58.86fps** | `queueDrops=0`，`queueAgeMax=13ms` | 共享 DDA，正常消费 |
| 2（同一 PID 35400） | 09:45:12.555 total=1380 → 09:45:14.055 total=1470 | 90 / 1.500s = **60.00fps** | 同上 | 第二 viewer 加入没有砍半 |
| 2（另一 run，PID 25292） | 09:47:12.159 total=690 → 09:47:14.160 total=750 | 60 / 2.001s = **29.99fps** | `queueDrops=0`，`queueAgeMax=12ms` | 配置不同但同样无共享降速 |
| 3 | **未取得同一版本的有效稳态样本** | 不填假数字 | 未取得带 `queueAge` 的对应记录 | 需后续在无人争用 19099 的窗口补测 |

PID 35400 的初始编码器日志明确 `fps=60`；双 viewer 加入前后的生产端仍是约 60fps。`total` 是共享编码输出，不是 viewer 各自抓屏次数。

### 4.2 每 viewer 实收 AU（共享 HTTP）

有效临时 HTTP 客户端测得：

| run / viewer | 实收窗口 | AU/字节 | 实收 AU/秒 | 首 AU 延迟 | 帧间间隔 |
|---|---|---:|---:|---:|---:|
| 单 viewer | 25.000s | 1480 / 18,959,466B | **59.160** | 0.4ms | 平均 16.903ms，p95 22.084ms |
| 30fps 配置的单 viewer | 25.004s | 755 / 12,827,303B | **30.155** | 0.4ms | 平均 33.162ms，p95 42.811ms |
| 增加第二 viewer 后的新增客户端（同 agent） | 5.806s | 179 / 3,024,994B | **30.657** | 0.4ms | 平均 32.619ms，p95 42.662ms |

首 AU 是 `GetResponse` 成功后收到第一个可解码 AU 的等待，**不包含此前 HTTP 请求排队、浏览器解码和上屏**。部分窗口由 agent 生命周期/端口被其他测试抢占而提前结束，已在结果中保留 IOException，不把短窗口夸大为长稳态。

### 4.3 “总延迟”到底测到了什么

当前源码中的 `queueAge` 在 `ReadViewerFrame()` 里计算：

```csharp
ageMs = (int)(DateTime.Now - at).TotalMilliseconds;
```

`at` 是 NAL 入 Ring 时的 `DateTime.Now`。所以：

```text
服务端待发等待 = queueAge
真实端到端 = 桌面时间戳 → 抓帧 → 转换 → 编码 → Pending 等待 → 网络 → 解码 → 上屏
```

**本报告不把 queueAge 当总延迟。** 本任务没有取得可核验的“同一张桌面时钟被三个浏览器各自拍到”的 1/2/3 viewer 光测延迟矩阵。严格意义的三 viewer 总延迟尚未完成，必须补，不得由 12ms 队列帧龄推导。

### 4.4 影响多 viewer 总延迟的其他因素

- 新 viewer 强制 IDR，短时间码率尖峰，但共享编码器不重建；
- 慢 viewer 的 Pending 满 2 后丢旧，故有效画面帧率会下降、队列等待仍有界；
- 浏览器各自解码和上屏可能在不同设备上差异很大，不能用一台本机 AU 接收间隔替代用户真实感受；
- HTTP 裸字节流没有端到端捕获时间戳。这是无法精确测总延迟的协议缺口，不只是缺一个日志行。

---

## 5. 根因判定：哪些假设被排除，哪些才应修

| 假设 | 判定 | 证据 |
|---|---|---|
| 每 viewer 都建一个 DDA | **错误** | `SessionRecorder.Capture` 静态唯一；`Viewers` 只存 NAL 队列 |
| 两个 DDA 客户端自动从 66 降到 32 | **被反证** | 独立 2 客户端均 65.70fps，3 客户端均约 66.03fps |
| DDA Acquire 本身必有设备锁，导致线性减半 | **未成立** | 无 API/实测支持；锁是否影响须按使用方式测量 |
| 当前 agent 的 CPU 读回/转换/编码造成吞吐瓶颈 | **主要方向** | 源码 CPU 路径 + 动画下 conversion Acquire 降到 62.91fps + 历史 agent/探针组合约 32fps |
| 多 viewer 的共享 DDA 因队列数量而降速 | **无证据** | 共享 PID 的 1→2 viewer 仍约 60fps、queueDrops=0 |
| 慢 viewer 本身会丢帧 | **成立，设计上允许** | 每 viewer 有界 Pending，慢者丢旧，不应改共享生产端 |
| 所有用户延迟都由 DDA 争用导致 | **错误** | 浏览器/网络/解码/协议时间戳缺失另行存在 |

**所以：** SoulLink 不需要为“每 viewer 重复抓屏”重做架构，因为它已经共享。真正的工作是：减少共享采集每帧的 CPU 工作、缩短帧所有权持有时长、保留各 viewer 独立背压，并补齐真实帧龄观测。

---

## 6. 可实施方案与预期收益

### P0：先修采集处理，避免“假多客户端问题”

1. **`DxgiDdaCapture.ReadBackTexture()`：缓存 staging 和 BGRA buffer，禁止每帧新建纹理。**
   - 按尺寸/格式复用 staging，复用 `byte[]`；失败重建，不每帧 Dispose。
   - 保留 `CopyResource→Map→Unmap`，先量化其真实 stall，不能只删除 `Map` 却说 CPU 路径变快。
   - 预期收益【推断】：消除每帧 GPU 对象创建/销毁与约 8.3MB LOH 分配，降低 GC 抖动和帧龄；**这不是零拷贝**，不能承诺 66fps。
   - 验证：一分钟稳态 `gen2 GC/allocated bytes/captureMs`、FrameSequence/秒与 agent total/秒。

2. **跨 GPU 的 Intel 抓屏→NVIDIA 编码：不要无条件启用 D3D11 shared texture 直通。**
   - 当前日志显示 `同GPU=false` 时禁用是合理保护；跨 device 不等于同 GPU，D3D11 纹理共享也不能绕过 Intel→NVIDIA 的物理搬运。
   - 可行方向：用硬件支持的 GPU cross-adapter 路径（先核实 D3D11 支持），或受控的 staging readback + pinned/pool buffer + 异步 fence，避免每帧同步等待；无法证明收益前保留 CPU 回退。
   - 同 GPU 的 PS+NV12 直通是另一条路径，不要把其成功外推到当前 Intel/NVIDIA 组合。
   - 预期收益【推断】：消除同步读回停顿，保持 66fps 供给尽量变成真实编码帧；需要专门 A/B，不保证实现零拷贝。

3. **把 `FrameSequence`、编码 `total`、每 viewer AU/上屏分别作为验收指标。**
   - `FrameSequence` 用 `AccumulatedFrames`，鼠标-only 更新另计，不能每次都 `max(1,…)` 算真实 present。
   - 预期收益【推断】：能直接识别“桌面 66、采集 48、每 viewer 30”究竟丢在哪段，避免再次把 `total` 当合成率。

### P1：保留共享 viewer，修慢消费者和真实延迟

4. **保留当前单 `Capture` + 单编码器 + 每 viewer Pending；不要改成每 viewer 一个 DDA。**
   - 每 viewer 的慢消费只应丢自己的旧帧；不要为了“保证所有帧都到”而无界排队。
   - 预期收益：多 viewer 不增加抓屏/编码成本，内存和最大等待有界。

5. **把 `queueAgeMs` 从全局单值扩成每 viewer 指标，并记录独立 viewerId 的出队年龄、丢帧和实收 AU。**
   - 当前 `_queueAgeMs/_queueAgeMaxMs` 是全局最后一个出队 viewer，无法分辨慢 viewer。
   - 预期收益【推断】：定位“哪一路浏览器慢”，而不是误伤共享采集。
   - 建议验收：1/2/3 viewer 同开 60s，记录每路 AU 速率、queueDrops、queueAge p50/p95/max；不要求每人相同网络延迟。

6. **视频帧携带捕获 QPC 或单调时钟，在 viewer 回报展示时刻/光测窗口做端到端测量。**
   - 当前 `At=DateTime.Now` 只有编码入队时刻，无法代表桌面捕获时刻；网络裸流也无法只从 AU 得出总延迟。
   - 预期收益【推断】：能把 `capture→encode→queue→network→decode→display` 分开，完成本报告尚未取得的三 viewer 总延迟矩阵。
   - 在协议改动前，可用同一时钟图片 + 三个隐藏浏览器并排抓屏做光测；不能拿服务端 queueAge 替代。

### P2：有边界时再做

7. **CPU→NV12 改 SIMD/线程池/缓冲复用。**
   - 本探针同公式从静态 10.365ms 到动画 5.446ms，说明 JIT/负载影响明显；优化前必须使用同一公式、同一 buffer 做基准。
   - 预期收益【推断】：减少 CPU 每帧工作，但优先级低于去掉往返与每帧分配。

8. **如确需多份诊断纹理，做“单 DDA 生产、订阅元数据/纹理”，不是另开 duplication。**
   - 实测独立客户端没有降速，因此这不是修当前 bug 的必要条件；只在设备锁或重复初始化成为实测热点时采用。
   - 预期收益：减少重复初始化和测量侧干扰，但不是 66→32 的通用解。

---

## 7. 建议的下一轮最短验证矩阵

| 场景 | 要测的数 | 通过条件（建议，不是实测承诺） |
|---|---|---|
| 单 DDA，metadata | AccumulatedFrames/秒 | 桌面约 66fps 作为供给基准 |
| 单 agent，1 viewer | FrameSequence、编码 total、AU、queueAge、上屏光测 | 能解释每一段损耗，不只报一个 fps |
| 同 agent，2 viewers | 同上，每 viewer 独立 | 共享生产不因订阅腰斩；慢者只丢自己帧 |
| 同 agent，3 viewers | 同上，每 viewer 独立 | 无队列无界增长，能给出真实总延迟 |
| 2 个独立 agent（若业务允许） | 每 agent 的 FrameSequence、CPU/GPU/锁等待 | 客户端不必然降速；若变慢按工作负载与锁剖分 |
| 跨 GPU CPU 回退 vs 优化路径 | 同帧源/同码率/同窗口 | 用 A/B 数据决定，不能将同 GPU 结果外推 |

**三 viewer 真实端到端延迟必须在无人抢占 A2 直连端口和没有额外探针的窗口补做。** 本任务期间 09:50 后同会话另有 `SoulLink.Test.Simd` 测试 agent，19099 被争用，我没有终止其他成员任务；这不影响独立 DDA 反证，但阻碍了有效的三 viewer HTTP 总延迟测量。

---

## 8. 测量环境与证据边界

- 独立 DDA 原始数据：`%TEMP%\soullink-rdp-framerate\dda-isolated*.txt`；只取帧元数据，不做像素读回。
- 读回对照：`readback-animation-metadata.txt`、`readback-animation-staging.txt`、`readback-animation-convert.txt`。
- 共享 viewer 原始 AU：同目录 `host-v*.txt`；服务端日志 PID 35400、25292 等。
- 部分 HTTP run 被 agent 生命周期/并发测试打断，原文保留 IOException；只有时间能对齐 `viewers` 的记录进入主表。
- 没有改动产品文件；临时 `.cs/.csproj/.ps1` 在 `%TEMP%\soullink-multi-viewer`，编译输出只用于 A2 隐藏计划任务，完成后删除任务/进程。
- 本文没有三 viewer 的有效光测总延迟，也没有驱动级设备锁结论；这两点已明确标注，不以推测替代。
