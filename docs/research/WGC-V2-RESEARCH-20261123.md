# WGC V2 帧完整性与撕裂规避机制调研（对齐 BGI）

> 日期：2026-11-23
> 作者：gpu-zero-copy（dsh-team `team_mwsomf2tj73xd7` / member `m_mwsosfpfjamcdf` / task `task_mwt0ro3xxuf1ji`）
> 性质：**纯调研 + 写 docs**。抓取 BGI 源码 + 微软官方文档，对照我们 WgcCapture，不改 Relay/Services、不 build、不碰运行进程。
> 依据：BGI `Fischless.GameCapture/Graphics/GraphicsCaptureV2.cs`（1313 行，GitHub 抓取存 `artifacts/GraphicsCaptureV2.cs`）+ 微软官方 `Direct3D11CaptureFramePool`/`TryGetNextFrame`/`ContentSize` 文档（learn.microsoft.com 直取）。

---

## 0. 一句话结论

**BGI WGC V2 的撕裂规避核心是「到达即取 + 单槽 stash + 帧池容量恒 2」——利用 WGC 的 FrameArrived 回调时机（DWM 写完、所有权刚释放）取帧即完整帧，不攒帧、不排队。** 我们 WgcCapture 已实现这个核心模型（CreateFreeThreaded + 容量2 + OnFrameArrived 排干留最新 + 只 stash 纹理引用），但缺 BGI V2 的四个增强：①帧池代数（防 Recreate 竞态）、②无新帧跳过读回（cache 时间戳）、③单飞闸门+在途计数（安全销毁 staging）、④GPU 打包 BGR24（compute shader 转色 -25% 回读量）。这些可作为「DXGI DDA 失败时 WGC 兜底路径」的增强。

---

## 1. BGI WGC V2 的帧完整性/撕裂规避机制（逐条拆解）

### 1.1 到达即取 + 单槽 stash（核心，`GraphicsCaptureV2.cs` L26-29, L54-66, L600-644）

```
帧池容量恒 = 2：
  1 个 buffer 被 stash 中的最新帧检出占用
  1 个始终留给 DWM 写下一帧合成
  （容量 1 会让 DWM 推帧等消费释放，不可取）

FrameArrived 回调（OnFrameArrived）：
  立即排干帧池（while TryGetNextFrame != null）
  只保留最新一帧挂到 _stashedFrame，被替换的旧帧立即 Dispose 归还
  回调只做帧检出/检入（WinRT 轻量操作），不触碰 D3D11 设备上下文
```

**关键证据（L609-611 注释）**：
> DWM 以合成节拍（~60fps）连续推帧且与内容变化无关（实测到达间隔恒定 ~16.7ms），
> 攒帧会让小容量池打满、DWM 等空闲 buffer，消费方读到的永远是几帧前的画面
> （三方同屏实测：取最旧 p50 122ms/p95 147ms，到达即取 p50 40ms/p95 65ms）。

**撕裂规避原理**：WGC 的 FrameArrived 回调时机 = DWM 完成帧合成、把所有权交给应用的那一刻。此时 `TryGetNextFrame` 取到的帧是**完整合成帧**（不会拿到 DWM 正写了一半的半成品），因为 DWM 写完才触发回调。这是 WGC 相对「自行定时读纹理」的根本优势——不需要锁、不需要忙等。

### 1.2 帧池代数（`poolGeneration`，防 Recreate 竞态，L61-66, L653-658, L683-686）

每次 `framePool.Recreate(...)` 后 `Interlocked.Increment(ref _poolGeneration)`。消费侧 `TryRenderNewFrameLocked` 先比对 `frameGeneration != _poolGeneration`，不匹配则丢弃旧池遗留帧——防止旧尺寸帧触发**反向 Recreate**（把新池建回旧尺寸）。

### 1.3 无新帧跳过读回（cache 时间戳，L89-92, L905-907, L975-981）

消费时若 stash 为空（到达回调已排干、无新帧），克隆缓存 `_cachedBgr` 直接返回——**零 GPU/PCIe/CvtColor 路径**。`_gpuFrameTs` 没变说明广播纹理时间戳未变，直接复用缓存，避免对旧内容重复 GPU 回读空转。

### 1.4 单飞闸门 + 在途计数（安全销毁，L83-87, L958-959, L1067-1074）

- `_captureGate` 串行化并发消费方（单飞）。
- `_activeCaptures` 计数「持锁外 Map/读回」的消费。Stop 与尺寸 Recreate 销毁 staging 前必须 `Monitor.PulseAll` 等其归零。

### 1.5 GPU 打包 BGR24（compute shader 转色，L93-99, L925-955, L1011-1036）

BGI V2 默认用 compute shader 把 BGRA 打包成 BGR24（= OpenCV CV_8UC3 内存布局），**CPU 免 CvtColor，回读量从 8.3MB 降到 6.2MB（-25%）**。CPU 转换是 `UseCpuConvert=true` 时的回退路径。

### 1.6 其它保护

- **Recreate 必须在帧池创建线程**（`RunOnWgcThread`，L676-687，跨线程抛 RPC_E_WRONG_THREAD）。
- **IsIconic 保护**（L668-671）：窗口最小化 ContentSize 缩成图标尺寸时不 Recreate，保留旧资源继续供图。
- **ContentSize 变化检测**（L662-664）：帧的 `ContentSize` 与 `_capSize` 不一致才 Recreate。

---

## 2. 微软官方 WGC 标准用法 vs BGI V2 差异

| 维度 | 微软官方标准 | BGI V2 |
|---|---|---|
| 帧池创建 | `CreateFreeThreaded`（1809+，跨线程） | 同（L387/396） |
| 帧池容量 | 官方未强制，sample 常用 2 | 恒 2（L28，1 stash + 1 DWM 写） |
| 取帧 | `TryGetNextFrame`（返回 null 表示无帧） | 同（到达回调内排干） |
| 尺寸变化 | 官方需手动 `Recreate` | Recreate + 帧池代数防竞态（增强） |
| 颜色转换 | 官方示例无（直接 Map BGRA） | GPU compute shader 打包 BGR24（增强） |
| 内容完整性 | 官方依赖 FrameArrived 时机 | 同（这是 WGC 的固有保证） |

**核心差异**：官方标准用法是「消费者主动轮询 TryGetNextFrame」；BGI V2 是「到达即取 + 单槽 stash + 消费侧按需读回」，把「帧池排干」从消费侧移到到达回调，配合帧池代数/缓存时间戳/单飞闸门，实现零积压 + 零空转。

---

## 3. 与我们 WgcCapture（538 行）的对照

### 3.1 已对齐的部分（核心模型一致）

| 机制 | 我们 WgcCapture | BGI V2 |
|---|---|---|
| CreateFreeThreaded | ✅ L123 | ✅ L387 |
| 帧池容量 2 | ✅ L126 | ✅ L28 |
| 到达即取（排干留最新） | ✅ OnFrameArrived L152-192 | ✅ L600-644 |
| 只 stash 纹理引用不读回 | ✅ L179-187（AddRef 持有 texPtr） | ✅ L628-636（stash frame） |
| 黑帧检测 | ✅ **IsMostlyBlack L271-285**（BGI 没有，我们有） | ❌ 无（靠 ContentSize 保护） |
| IsBorderRequired/IsCursorCaptureEnabled/MinUpdateInterval | ✅ L132-134 | ✅ L409-422 |

### 3.2 我们缺失的增强（可落地的 file:line 级建议）

| # | BGI V2 机制 | 我们缺失 | 建议落点 | 优先级 |
|---|---|---|---|---|
| 1 | 帧池代数（防 Recreate 竞态） | ❌ 我们 Recreate 在 `CaptureOnce` 临时抓帧里无代数，常驻路径无 Recreate（窗口尺寸变化时 `_stashedWidth/Height` 会漂移但池不重建） | `WgcCapture.cs` 加 `_poolGeneration`，OnFrameArrived 里 `frame.ContentSize` 变化时 Recreate + 自增 | **高** |
| 2 | 无新帧跳过读回（cache 时间戳） | ❌ 我们 `CaptureBgra` 每次 `ReadBackTexture`（即使 stash 纹理没变也读回） | 加 `_stashedSeq`/`_gpuFrameTs`，CaptureBgra 里 seq 没变就复用 `_lastBgra` | **高**（省 GPU 回读，静止画面 CPU 降） |
| 3 | 单飞闸门 + 在途计数 | ⚠️ 我们有 `_stashLock` 但无「读回在途」保护，Stop 时可能销毁正被读回的 staging | 加 `_activeCaptures` 计数 + Stop 前等待归零 | **中** |
| 4 | GPU 打包 BGR24 | ❌ 我们直接读回 BGRA（8.3MB） | 可选：加 compute shader 打包（需 SharpDX/Vortice compute shader，工作量中） | **低**（收益 -25% 回读，非必须） |
| 5 | ContentSize 变化 Recreate | ⚠️ 我们 OnFrameArrived 里记录 `_stashedWidth/Height` 但不 Recreate 池 | 见 #1 | **高** |

### 3.3 我们的独有优势（BGI V2 没有）

- **IsMostlyBlack 黑帧检测**（L271-285）：WGC 抓窗口小于整屏时可能黑帧，我们有降采样黑帧检测主动丢弃（BGI 靠 IsIconic + ContentSize 保护，没有像素级黑帧判断）。这条比 BGI 更早发现「WGC 帧池尺寸 bug」（BLACKSCREEN-ROOTCAUSE-20261123.md 的根因）。

---

## 4. 结论：能否作为「DXGI DDA 失败时 WGC 兜底」的增强

**能，且建议做前两条（帧池代数 + 无新帧跳过读回），成本低、收益明确。**

| 建议 | 收益 | 成本 | 风险 |
|---|---|---|---|
| ① 帧池代数 + ContentSize Recreate | 窗口尺寸变化时不再漂移/黑帧，防 Recreate 竞态 | 1 人日 | 低（纯状态机，对齐 BGI） |
| ② 无新帧跳过读回 | 静止画面零 GPU 回读，CPU 显著下降（挂机场景核心收益） | 0.5 人日 | 低（seq 比对，对齐 DxgiDdaCapture 的 _lastBgra 复用） |
| ③ 单飞闸门 + 在途计数 | Stop/Recreate 安全 | 0.5 人日 | 低 |
| ④ GPU 打包 BGR24 | 回读量 -25% | 1~2 人日（compute shader） | 中（shader 正确性） |

> ★ 优先级建议：**① + ② 先做**（对齐 DDA 的 _lastBgra 复用 + BGI 的代数模型，成本最低收益最高）；③ 随 ① 一起做；④ 可选（BGRA 回读在当前 1920×1080 下 8.3MB 不是瓶颈，-25% 收益有限）。

---

## 5. 产物清单

| 路径 | 说明 |
|---|---|
| `artifacts/GraphicsCaptureV2.cs` | BGI WGC V2 源码（1313 行，MIT 许可，只读参考） |
| 本报告 | `docs/research/WGC-V2-RESEARCH-20261123.md` |

## 6. 许可声明

BGI 是 MIT 许可（better-genshin-impact），`Fischless.GameCapture` 属其子项目。本文档只做**机制调研**，不抄代码；上述 file:line 建议是我们自己的实现思路，对照的是「到达即取/帧池代数/缓存时间戳」这些**通用设计模式**（Sunshine/官方 sample 同款），不构成代码复制。