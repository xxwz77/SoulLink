# 交接文档 · 视频流链路（2026-11-22 深夜）

> 日期：2026-11-22 23:50
> ⚠️ **本文档的「/stream 可用」结论已被修正** —— 实际在真实 Agent 里 `/stream` 连接被重置。
> 请优先看 [`HANDOFF-STREAM-STATUS-20261123.md`](HANDOFF-STREAM-STATUS-20261123.md)。
> 本文档的其余数据（抓屏/编码耗时、TE 方案、踩坑）仍然有效。

> 状态：**服务端视频流全链路已跑通**，代码已提交；**安卓 APP 未开始**
> 上一份交接：`HANDOFF-REMOTE-20261122.md`（同日傍晚，记录技术验证过程）

---

## 0. 一句话总结

**从「60fps 做不到」到「全链路跑通」**：
WGC 抓屏（2~8ms）+ NVENC 硬编码（6~8ms）+ HTTP `/stream`，端到端实测 **636~742ms**（其中 **500ms 是 ffplay 缓冲**，我们自己的链路只占 **12~20ms**）。
**安卓 APP 是最后一块，还没开始。**

---

## 1. 关键实测数据（可信）

### 1.1 抓屏方式对比（同一台机器，RDP 会话）

| 方式 | 单帧耗时 | 来源 |
|---|---|---|
| **BitBlt** | **17~34ms**（RDP 内更慢） | 多次实测 |
| **WGC CreateForWindow** | **2~8ms** | `stream-probe` 实测 |
| **WGC CreateForMonitor** | 12fps（被静止画面稀释） | `WGC-A1-DYNAMIC` |

**结论：WGC 比 BitBlt 快 4~10 倍，而且能抓到被遮挡的窗口。**

### 1.2 编码方式对比

| 方式 | 单帧耗时 | 流水线特性 |
|---|---|---|
| GDI+ JPEG | 5.8ms | 独立帧 |
| **软 H.264 MFT** | 4~8ms | **30 帧入只出 13 帧**（累积缓冲） |
| **NVENC 硬编异步** | 6~8ms | **1:1 即时产出**（315 入 → 314 出） |

**关键：硬编是异步的（`TransformAsyncUnlock` + 事件驱动），流水线深度浅得多。**

### 1.3 端到端延迟分解（硬编，A1 原神实测）

```
WGC 抓屏           0（GPU 侧推送，不阻塞）
staging ReadBack   2 ~ 8.7 ms     ← GPU→CPU 拷贝
画时间戳           1 ms
BGRA → NV12        2 ms
NVENC 编码         6 ~ 8 ms
本机 HTTP          < 1 ms
────────────────────────────────
我们的链路合计      约 12 ~ 20 ms
ffplay 缓冲        ≈ 500 ms       ← 大头，不是我们的问题
────────────────────────────────
端到端实测          636 ~ 742 ms
```

**重要结论：延迟瓶颈在播放器缓冲，不在服务端。**

### 1.4 资源占用（3 个账号估算）

| 项 | 现在（BitBlt+JPEG） | 未来（WGC+NVENC） |
|---|---|---|
| **CPU** | **357% 单核** | **15% 单核** |
| 内存 | 81 MB | 105 MB |
| 显存 | 0 | 120~210 MB |
| **带宽** | 46 Mbps | 8 Mbps |

**CPU 省 24 倍，带宽省 5.7 倍，代价是显存多 200MB。**

---

## 2. 已实现的组件

### 2.1 抓屏

| 文件 | 说明 |
|---|---|
| `Relay/IScreenCapture.cs` | 接口 |
| `Relay/BitBltCapture.cs` | 现有实现（搬迁，行为不变） |
| `Relay/WgcCapture.cs` | **WGC 实现** |

**WgcCapture 的关键特性：**

```csharp
// 1. CreateFreeThreaded（不是 Create）
Direct3D11CaptureFramePool.CreateFreeThreaded(device, format, 2, size);

// 2. 帧池容量必须 2（设 1 会让 DWM 等消费者）
// 3. FrameArrived 回调：排干帧池 + AddRef 保留最新纹理 + 置标志
//    ★ 绝不 ReadBack（每帧 ReadBack 要 2~5ms）
// 4. 专用泵线程（WinRT 对象跨线程抛 RPC_E_WRONG_THREAD）
// 5. MinUpdateInterval（版本检测后设，V2 特性）
// 6. IsBorderRequired=false / IsCursorCaptureEnabled=false
// 7. 降级链：WGC-Window → WGC-Monitor → BitLlt
// 8. 生命周期：引用计数（观众+录制）+ 30 秒宽限 + 释放 16MB 显存
```

### 2.2 编码

| 文件 | 说明 |
|---|---|
| `Services/IVideoEncoder.cs` | 接口 |
| `Services/MfH264Encoder.cs` | SinkWriter 版（写 MP4 文件，录像用） |
| `Services/MfFrameEncoder.cs` | **IMFTransform 版（逐帧出 NAL，直播用）** |

**MfFrameEncoder 关键点：**
- 用 **Vortice.MediaFoundation 3.8.3**（不是手写 vtable，绕开了 3 次翻车风险）
- **硬编异步**：`TransformAsyncUnlock` + `GetEvent` 轮询事件
- 输出**已经是 Annex-B**（首字节 `00 00 00 01 09`），不需要转换
- 支持软编/硬编切换（`-Soft` 参数）

### 2.3 传输

| 位置 | 说明 |
|---|---|
| `Relay/AgentWebServer.cs` | `/stream` 端点，chunked `video/h264` |
| `Services/WebDirect.cs` | `/stream` 加入白名单 |

**注意**：目前**只支持单客户端**，多客户端分发未做。

### 2.4 配置

| 配置项 | 说明 |
|---|---|
| `SOULLINK_CAPTURE_MODE` | 环境变量：0=BitLlt（默认），1=WGC |
| `VideoBitrateKbps` | 默认 8000（8 Mbps），可配 |

---

## 3. 技术决策记录（为什么这么做）

### 3.1 为什么用 WGC 而不是 DXGI

| 方案 | RDP 内 | 结论 |
|---|---|---|
| DXGI Desktop Duplication | ❌ `E_OUTOFMEMORY` / `DuplicateOutput` 失败 | 不可用 |
| **WGC** | ✅ 实测可用（BetterGI 也在用） | **采用** |

**关键：WGC 必须以目标账号本人身份运行**（代理经 `CreateProcessAsUser` 启动，天然满足）。
以错误身份运行会报 `0x80070424`（DWM 不拉起 per-user CaptureService）。

### 3.2 为什么用 Vortice.MediaFoundation 而不是手写 vtable

**手写 COM 接口在本项目翻车过 3 次：**
1. `IMFAttributes` 槽位错一个 → 全体后移 → `SetUINT32` 调到 `DeleteAllItems`（**返回 S_OK**）
2. `IMFSample` 标了 `[local]`，`.NET ComImport` 调用直接 `AccessViolation`
3. `IMFMediaBuffer` 的 IID 容易写错

**Vortice.MediaFoundation 验证过：`IMFTransform` / `ProcessInput` / `ProcessOutput` / `SetInputType` / `SetOutputType` 全有，缺口为零。**

**代价：945 KB 包（可接受）。**

### 3.3 为什么 TFM 是多目标 + 外置 DLL

| 项 | 值 |
|---|---|
| TFM | `net11.0-windows;net11.0-windows10.0.26100.0` |
| tools 项目影响 | **55 个项目零改动**（自动匹配无版本 TFM） |
| `Microsoft.Windows.SDK.NET.dll` | 25.99 MB（WinRT 投影，**必须外置**） |
| **exe 体积** | **4.88 MB**（原 4.84 MB） |
| **全量 zip** | **9.24 MB**（原 2.85 MB） |

**外置方法（踩过坑）：**
```xml
<!-- ✗ 错误：ExcludeFromSingleFile 不能写裸 DLL 名 -->
<!-- ✓ 正确：Target 里 ResolvedFileToPublish Update -->
<Target Name="ExcludeWinRTFromSingleFile" AfterTargets="ResolveFileToPublish" BeforeTargets="_ComputeFilesToBundle">
  <ItemGroup>
    <ResolvedFileToPublish Update="@(ResolvedFileToPublish)"
                           Condition="'%(Filename)%(Extension)' == 'Microsoft.Windows.SDK.NET.dll'">
      <ExcludeFromSingleFile>true</ExcludeFromSingleFile>
    </ResolvedFileToPublish>
  </ItemGroup>
</Target>
```

---

## 4. 踩过的坑（改之前必读）

### 4.1 WGC 相关

| 坑 | 症状 | 解法 |
|---|---|---|
| **回调里 ReadBack** | 每帧 2~5ms 白费 | 只保存纹理引用，需要时才读回 |
| **纹理不 AddRef** | 拿到失效对象 | 帧池复用纹理，必须 AddRef |
| **帧池容量 1** | DWM 等消费者，卡死 | **必须 2** |
| **跨线程调 WinRT** | `RPC_E_WRONG_THREAD` | 专用泵线程 |
| **`Marshal.Release(nativeObj.ThisPtr)`** | `0xC0000005` | RCW 拥有引用，不能手动 Release |

### 4.2 编码相关

| 坑 | 症状 | 解法 |
|---|---|---|
| **硬编 MFT 同步调用** | `MF_E_TRANSFORM_ASYNC_LOCKED` | 先 `TransformAsyncUnlock`，再事件驱动 |
| **软编流水线累积** | 30 帧入只出 13 帧 | 换硬编 |
| **Vortice 无 ICodecAPI** | 设不了 `AVLowLatencyMode` | 需手写 ~50 行接口（未做） |

### 4.3 HTTP 相关

| 坑 | 症状 | 解法 |
|---|---|---|
| **HttpListener prefix 不以 / 结尾** | 路由不匹配 | prefix 必须 `http://+:port/` |
| **accept 线程任务每轮重建** | **客户端永远连不上** | `GetContextAsync` 任务必须跨循环保留 |

### 4.4 构建相关

| 坑 | 症状 | 解法 |
|---|---|---|
| **增量 build 报假错误** | `CS1061` 说方法不存在，实际存在 | **`dotnet build -t:Rebuild`** |
| **`ExcludeFromSingleFile` 写裸 DLL 名** | 不生效 | 用 `ResolvedFileToPublish Update` |

---

## 5. 未完成的部分

### 5.1 安卓 APP（最重要）

**设计文档已完成**：`docs/research/ANDROID-APP-DESIGN-20261122.md`

| 项 | 状态 |
|---|---|
| 技术栈 | **Kotlin**（已定） |
| 画面 | 传统 View + **SurfaceView**（低延迟） |
| 设置/列表 | Jetpack Compose |
| 解码 | **MediaCodec**（P0 用 JPEG 逐帧，P2 才上 H.264） |
| 环境 | **本机已装齐 SDK**（`D:\Android\sdk`，android-34，JDK 17，Gradle 8.9） |

**可以直接 `gradle assembleDebug` 出 APK。**

### 5.2 多客户端分发

现在 `/stream` **只支持单客户端**。

需要做：
- 每个客户端一个有界队列
- 慢客户端丢旧帧但**不能丢关键帧**
- 新客户端从下一个 IDR 开始推（含 SPS/PPS）

### 5.3 其他

| 项 | 说明 |
|---|---|
| `CODECAPI_AVLowLatencyMode` | Vortice 没绑定，需手写 ~50 行 |
| Ring 改存 H.264 | 现在 Ring 存 JPEG，录像走 SinkWriter 重编码 |
| GPU 纹理直通 NVENC | 现在要 staging ReadBack（2~8.7ms），理论可省 |
| 网页端 H.264 播放 | 用 WebCodecs（设计已定，未实现） |

---

## 6. 下一步建议

### 优先级 1：安卓 APP

服务端已经完整（`/stream` 能推流），**APP 是最后一块**。

**先做 P0**：
- 拉 JPEG 单帧（现有 `/live`）→ `SurfaceView` 绘制
- 基本触摸（点/滑/长按）
- 账号列表

**P2 再切 H.264 流**（需要 MediaCodec）。

### 优先级 2：多客户端分发

如果在正式使用中需要多人同时看。

### 优先级 3：延迟优化

- 手写 `ICodecAPI` 设 `AVLowLatencyMode`
- GPU 纹理直通（省 2~8.7ms）

**但注意：当前 636ms 里 500ms 是 ffplay 缓冲，服务端优化收益有限。**

---

## 7. 生产环境注意（勿踩）

- ⚠ **不要动 `dist/stable/`**（挂机测试版）
- ⚠ **不要用 A1/A4 账号**（用户明确禁止）
- ⚠ **A2 可用，用完要注销**
- ⚠ **不要动用户正在跑的进程**（游戏、BetterGI、SoulLink）
- ✅ 测试桌面：前台显示放桌面 3，需要焦点的放桌面 2

---

## 8. 环境信息

| 项 | 值 |
|---|---|
| 机器 | Win11 29698 / i9-13900HX 32 核 / 29.7GB / RTX 4060 Laptop 8GB |
| 会话 | console(lummy,1) + A1(18,有原神) + A2(43,空闲) |
| ffmpeg | `C:\Users\91244\AppData\Local\Temp\ff-dl\x\ffmpeg-master-latest-win64-gpl\bin\` |
| Android SDK | `D:\Android\sdk`（android-34 + build-tools 34.0.0） |
| JDK | 17 Temurin |
| Gradle | 8.9 |
| 服务器 | 42.192.114.250:5080（Ubuntu 26.04，2.1.0） |
| SDK | .NET 11.0.100（`global.json` 锁定） |

---

## 9. 相关文档

### 视频流相关（按时间顺序）

1. `STREAM-PERF-AUDIT-20261122.md` —— 最初的链路分析（部分结论已被推翻）
2. `MEDIA-CAPABILITY-20261122.md` —— 媒体能力探测
3. `STREAM-GAP-20261122.md` —— **为什么串流软件能 60fps**
4. `WGC-VERIFY-20261122.md` —— WGC 在 RDP 可用（身份是关键）
5. `WGC-A1-DYNAMIC-20261122.md` —— 帧率实测（31.7fps = 游戏锁帧）
6. `WGC-SNAPSHOT-20261122.md` —— **非黑帧实证（含截图）**
7. `WGC-TFM-VERIFY-20261122.md` —— TFM 方案验证
8. `VIDEO-STREAM-RESEARCH-20261122.md` —— 视频流方案对比
9. `NVENC-DESIGN-20261122.md` —— 硬件编码设计
10. **`STREAM-LATENCY-TEST-20261122.md`** —— **延迟实测（软硬对比 + 耗时分解）**

### 其他重要文档

- `ANDROID-APP-DESIGN-20261122.md` —— 安卓 APP 设计
- `HANDOFF-REMOTE-20261122.md` —— 同日的技术验证过程（含 6 个错误）
- `REVIEW-20261122.md` —— 早期方案的对抗性评审

---

## 10. 给继任者的话

**这个项目最大的教训：不要用二手信息下结论。**

今天犯的错（记录在 `HANDOFF-REMOTE-20261122.md`）：

| 错误 | 真相 |
|---|---|
| 「60fps 不成立」 | 是 BitBlt 不行，WGC 可以 |
| 「Sunshine 在 RDP 里不行」 | 用户朋友实测能用，引的是 GitHub issue 二手信息 |
| 「虚拟屏能救 GPU 抓屏」 | 只对 console 会话有效 |
| 「显存只剩 10MB」 | 是瞬时状态，实际常有 1.5GB 空闲 |
| 「CreateForMonitor 在 RDP 会失败」 | 也是二手信息，未自测 |

**关键数字必须自己复核，尤其是别人告诉你的「不行」。**