# P1 消除 CPU 色彩转换开销：BgraToNv12 并行化（7.8ms → 2ms）

> 日期：2026-11-24
> 作者：deep-parsec（dsh-team `team_mwtjn0d77v8gtr` / member `m_mwtkvdn1e6jqik` / task `task_mwu0p85e3j4q05`）
> 性质：**产品代码改动**（Relay/SessionRecorder.cs，新增 BgraToNv12Parallel）+ 独立验证 + 真机实测。未碰挂机版/A1/A4/注册表。
> 证据分级：【实验】＝本次独立验证程序原始输出；【实测】＝真机 A2/session4 agent 日志（`C:\Users\a2\AppData\Local\Temp\soullink-relay-2064.log`，行级可回查）；【代码事实】＝源码可证。

---

## 0. 一句话结论

**BgraToNv12 从单线程标量改为行级并行（Parallel.For），convertMs 实测 7~8ms → 2ms（4 倍），且与原实现逐字节一致（6 尺寸 + 10 次重复 0 差异）。** 没有走 SIMD（AVX2 手写向量在 20 分钟时间盒内未达到逐字节等价，按 Lead 建议降级到方案 2：Parallel.For——风险最低、收益已达标）。AVX2 草稿保留在 `Relay\SimdBgraToNv12.cs.txt`（移出编译，未接入）。

---

## 1. 背景：为什么 convertMs 是必经之路

- session4（A2）拓扑：Intel UHD 抓屏（RDP 桌面合成在核显上）+ NVIDIA NVENC 编码 → **跨 GPU，零拷贝被智能路由正确禁用**（物理限制，Sunshine 同构行为）→ **CPU 转换是唯一路径**。
- 基线（E2E-LATENCY-BASELINE-20261124.md）：convertMs = **7.8ms**（鼠标场景均值），是三段（capture 8.4 / convert 7.8 / encode 4.6）中最大且最可控的一段。

## 2. 实现（Relay/SessionRecorder.cs）

### 2.1 结构

```
BgraToNv12(bgra, w, h, stride)
  ├─ w*h ≥ 640×480 且多核 → BgraToNv12Parallel（try/catch 静默降级）
  └─ 否则 / 异常 → BgraToNv12Scalar（原实现逐字保留）
```

### 2.2 并行策略（为什么逐字节安全）

| 平面 | 分区单位 | 写区间 | 冲突分析 |
|---|---|---|---|
| Y | 每行 y | `nv12[y*w, y*w+w)` | 行区间互不重叠 → 无写冲突【代码事实】 |
| UV | 每对行（y, y+1）→ 索引 yr | `nv12[ySize + yr*w, +w)` | 每对独立写整行 UV，互不重叠【代码事实】 |

- 行内公式**逐字复制**自标量版（同一整数系数、同一移位、同一截断）→ 数据流与标量完全同源。
- `MaxDegreeOfParallelism = min(核数, 8)`：防止多会话并发时线程池被单会话吃满（AGENTS.md 性能约束精神）。
- 小图（<640×480 像素量级）不值得开线程，直接标量（阈值与向 Lead 建议的方案一致）。
- Parallel.For 任何异常 → 静默降级标量，绝不进崩溃路径（与零拷贝同款防御）。

## 3. 验证

### 3.1 逐字节等价【实验】（独立验证程序，net11.0，Release，本机 32 核）

| 尺寸 | mismatch | 结论 |
|---|---|---|
| 1920×1080 | 0 | ✓ 逐字节一致 |
| 2560×1440 | 0 | ✓ |
| 1280×720 | 0 | ✓ |
| 640×480 | 0 | ✓（阈值边界尺寸） |
| 642×482 | 0 | ✓（非 16 倍数偶数） |
| 66×34 | 0 | ✓ |
| 10 次重复 1080p | 0 | ✓（并行调度随机性下稳定等价） |

### 3.2 性能【实验】

| 尺寸 | 标量 | 并行 | 加速 |
|---|---|---|---|
| 1920×1080 | 5.10ms | **1.27ms** | **4.0x** |
| 2560×1440 | 8.32ms | **2.14ms** | **3.9x** |

（注：标量在本机基线 5.10ms，低于 A2 真机的 7.8ms——A2 会话 CPU 与本测量进程竞争；加速比一致。）

### 3.3 真机 A2/session4【实测】

测试方法：独立测试 agent（端口 19077，token testtoken123，fps=60，隐藏 wscript 启动于 session4）+ `/stream` 拉流 + 隐藏鼠标移动器/全屏动画，读 agent relay 日志 `共享管线` 行（方法与 E2E-LATENCY-BASELINE §5 同款）。

| 场景 | convertMs（前，基线文档） | convertMs（后，本次） | 备注 |
|---|---|---|---|
| 鼠标移动 20s | 7.8 | **2**（40+ 行全部 =2） | captureMs/encodeMs 不变，queueAge 正常 |
| 全屏动画 15s | 8.0 | **2**（8 行全部 =2） | staticSkipped 不增长，无花屏 |

### 3.4 build

SessionRecorder.cs + 并行实现：**0 error**（本文件范围内；共享 build 的 AgentWebServer.cs 错误属另一成员的并发改动，非本任务文件）。SIMD 草稿 `Relay\SimdBgraToNv12.cs.txt` 已移出编译（团队阻塞事故的教训：未完成代码不入共享 csproj）。

## 4. 未走 SIMD 的决策记录（给后续任务参考）

1. **AVX2 手写向量路径**：数值域已证明可逐字节等价（Y 值域 [0,218]、UV [16,239] 都在 byte 内），但 UnpackLow/ShiftLeftLogical128BitLane 的 lane 语义与预期相反（诊断 dump 证实），20 分钟时间盒内未收敛 → 按 Lead 建议降级。草稿与全部坑记录在 `Relay\SimdBgraToNv12.cs.txt`：
   - `Vector256.LoadUnsafe(ref arr[0], (nuint)idx)` / `Vector128.StoreUnsafe` 是唯一可用的数组访问形式（`Avx.LoadVector256(arr, idx)` 不存在）
   - 负系数转 u16 位模式要 `unchecked((ushort)(short)-74)`
   - **UnpackLow(v, zero) 是字节交织不是「取低半」**——lane 布局按交错排布，水平归约的方向假设全部要反转
   - 若后续重启 SIMD：从「诊断 dump 每步 lane」开始，别信脑内布局
2. **System.Numerics.Vector<T>**（Lead 方案 1）未尝试：byte 元素的 Vector<T> 对 UV 2x2 下采样仍需手工拆装，预计工作量与收益比不如并行版。
3. **当前收益已达标**（7.8→2ms ≤ 2ms 目标），SIMD 理论上限（~0.5ms）的额外收益 <1.5ms，不值得再投入。

## 5. 后续影响

- E2E 延迟预算：convertMs 段 7.8→2ms，**端到端再省 ~6ms**（85~130ms → 79~124ms 量级）。
- 单帧管线工作：capture 6 + convert 2 + encode 5 ≈ 13ms → 理论帧率上限 ~77fps（RDP 合成 60fps 封顶不再被管线拖累）。
- 剩余瓶颈排序（不变）：RDP 合成率 60fps > 浏览器段 20~40ms > capture 等帧。

## 6. 清理记录

测试 agent（PID 2064，端口 19077）、计划任务（SimdAgentTest/2、SimdMouseMove、SimdAnim）、`%TEMP%\simd-agent-test`、`%TEMP%\par-verify`、`%TEMP%\simd-api-test` 全部删除/终止。AVX2 诊断项目已删。`Relay\SimdBgraToNv12.cs.txt` 保留（SIMD 草稿+坑记录，未接入编译）。
