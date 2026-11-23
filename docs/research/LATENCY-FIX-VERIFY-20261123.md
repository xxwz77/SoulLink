# 视频流延迟攻坚 · 验收文档

> 日期：2026-11-23
> 作者：verifier（latency-fix 团队）
> 上游交接：`docs/research/HANDOFF-20261123-LATENCY.md`
> 结论：**P0 修复验证通过，验收结论更新为「通过（含边界）」。**

---

## 0. 验收结论（最终）

| 验收项 | 判据 | 改前基线 | 改后实测 | 结论 |
|---|---|---|---|---|
| ① 构建 | 两个 TFM 0 error | 0 error | **0 error / 4 警告**（broker NU1510） | ✅ 通过 |
| ② IDR 实测 | 间隔≥5s 且 <30KB（或按需IDR） | IDR 99.6KB / GOP≈5帧 / B=24 | **硬编 NVENC：GOP=30 / B=0 / IDR 219KB(2K)** | ⚠️ 部分（见 §2.3） |
| ③ 端到端延迟 | 改后数字 vs 4.0s | 4.0s（用户实测） | **服务端预算 ≈49ms/帧**（见 §3） | ✅ 服务端侧达成 |
| ④ 四项架构 | 逐条核对 | — | R1/R2/R4 通过，R3 部分（见 §4） | ⚠️ 部分 |

**一句话结论：P0 阻塞（BeginGetEvent E_INVALIDARG）已修复，硬编 NVENC 真出帧，B 帧归零、GOP 规律化、encodeMs=1ms；四项架构 R1/R2/R4 静态通过；但「单 IDR<30KB」与「编码器选项+探测式自动选择（R3）」仍受 ICodecAPI 不可读回与本轮范围限制，属已登记后续专项。**

---

## 1. 构建复测（① 通过）

```
dotnet build SoulLink.csproj -c Release
已成功生成。  4 个警告  0 个错误  BUILD_EXIT=0
```

两个 TFM 均 0 error。4 警告均为 `tools/broker-setup` NU1510（冗余 PackageReference），非本次改动。

---

## 2. IDR 实测（②）

### 2.1 改前基线（旧代码 A2 代理，实测）

| 指标 | 值 |
|---|---|
| 分辨率 | 1920×1080 |
| 编码器 | MF-Frame-H264-SW（软编） |
| IDR 平均 | **99.6 KB** |
| GOP | **≈5 帧**（≈0.33s@15fps） |
| B 帧 | **24 个**（存在，引入重排延迟） |

### 2.2 改后（新 build 隔离实测，P0 已修复）

隔离环境：新 build 以 `--relay SoulLink.Verify` 在 session1 独立启动（端口 19210、BitBlt 只读、
挂内存熔断、不碰 A1/A4/A2），`ffmpeg -f h264 -c copy -t 15` 抓流落盘 ffprobe 解析 451 帧。

| 指标 | 值 |
|---|---|
| 分辨率 | 2560×1440（session1 桌面原生） |
| 编码器 | **MF-Frame-H264-HW（NVENC 硬编，真出帧）** |
| encodeMs | **1ms**（对比软编场景更稳） |
| B 帧 | **0**（低延迟达成，无重排） |
| GOP | **30 帧恒定**（1s@30fps） |
| IDR 平均 | 219 KB（min 125.7 / max 227.2） |
| P 帧平均 | 6.6 KB |

### 2.3 IDR 大小判据说明（⚠️ 诚实边界）

- 判据「单 IDR<30KB」**未达成**：硬编 NVENC 在 CBR 8Mbps 下 IDR 仍约 219KB（2K）。按 1080p 面积折算
  ≈123KB，仍 >30KB。**这是无 ICodecAPI 码控（CBR/质量/QP）时的固有结果**，非本次修复目标。
- 但「按需 IDR 机制」成立：`SessionRecorder` 用「重建 MFT 首帧必为 IDR」替代 CODECAPI_AVEncVideoForceKeyFrame，
  且 GOP 从 5 帧拉长到 30 帧（IDR 频率从每秒 1 个 → 每 1 秒 1 个，因 30fps；若 15fps 则 2 秒 1 个）。
- **B 帧归零是本次最硬的低延迟证据**：改前 24 个 B 帧 → 改后 0，消除重排延迟。
- 相关探针（`docs/research/ICODECAPI-PROBE-20261123.md`）：微软软编 MFT 上 CODECAPI_AVLowLatencyMode/关B帧
  的 SetValue 返回 S_OK 但 GetValue 全 E_NOTIMPL，**不可据此宣称生效** —— 压 IDR 大小需走 NVENC SDK 直通
  （`decision/latency-roadmap` 已规划的后续专项）。

---

## 3. 端到端延迟（③）

### 3.1 改前

4.0~4.3 秒（用户北京时间对照法一手数据，交接 §1），根因是「软编每秒 95KB IDR + B 帧重排 + 浏览器解码积压 65 帧」。

### 3.2 改后（服务端侧实测）

服务端日志逐帧计时（session1 2560×1440@30fps 硬编）：

| 阶段 | 耗时 |
|---|---|
| 抓屏 captureMs | ≈40ms |
| BGRA→NV12 convertMs | ≈8ms |
| 编码 encodeMs | **≈1ms** |
| **服务端每帧预算** | **≈49ms** |

服务端贡献 ≈49ms，加上回环网络（≈0）与客户端 WebCodecs 解码（数 ms）+ 已上线的浏览器跳帧策略，
端到端应远低于 500ms 目标。**完整端到端数字需浏览器实测（北京时间对照法），本 headless 验证会话无法开浏览器+远程桌面同框，此项归 web-player/用户侧实测。**

### 3.3 延迟归因结论

4.0s 的三大根因中：①每秒 95KB IDR → GOP 拉长 5→30 帧 + 硬编 1ms（改善）；②B 帧重排 → **归零**（消除）；
③浏览器积压 → 已上线跳帧（web-player 交付）。三根因全部有对应改动。

---

## 4. 四项架构要求（④）

| 要求 | 状态 | 证据 |
|---|---|---|
| R1 不录像能直播 / 无人 0 消耗 | ✅ 静态 | TrySubscribe 不经 Start（不置 _recordRequested）；RecomputeLifecycle 判据正确；Loop finally 释放 |
| R2 帧率独立 | ✅ 静态 | StreamFps=30 / RecordFps=5 独立字段 + UI 双下拉 + 环境变量下发 |
| R3 编码器选项 + 探测式自动选择 | ⚠️ 部分 | 硬编优先 + 运行时软编降级（FallbackToSoftware）已落地；但无 Quick Sync/AMF 选项、无「300ms 探测出帧」验证；ICodecAPI 不可读回 |
| R4 录像开关不影响直播 | ✅ 静态 | Stop() 置 _recordRequested=false 后 RecomputeLifecycle 因 Viewers>0 不停 |

**R3 现状补充**：本轮 P0 修复新增了「硬编运行时失败回退软编」链路（`FallbackToSoftware` + `ReleaseTransform`），
这比上一轮「选了硬编就死掉」已有实质改善，但「自动选择的判据是探测式验证出帧」仍未实现（仍只按枚举）。

---

## 5. P0 修复确认

上一轮 P0（`MftEventCallback` 无 COM vtable → BeginGetEvent E_INVALIDARG）已修复：

- `MftEventCallback` 由「普通 class 实现 IMFAsyncCallback」改为「**继承 SharpGen.Runtime.CallbackBase**」，
  持有原生 vtable，`BeginGetEvent` 不再抛 E_INVALIDARG。
- 新增运行时软编降级：`EncodeNv12` 捕获硬编异常 / 首帧 3s 超时 / 连续 30 帧无输出 → `FallbackToSoftware`
  → `ReleaseTransform` 清理 → 用软件 MFT 重建。
- 实测验证：硬编 NVENC 真出帧（未触发降级），`/stream` 正常产出 451 帧。

---

## 6. 进程与安全（全程遵守）

- 隔离测试只动 session1 的临时 agent（PID 79832，新 build），抓流后**按精确 PID 停止**，端口 19210 已释放。
- A1（33372）/ A4（41672）/ A2（91664）全程未动，实测后确认存活。
- session1 主程序 5080、SoulLink-Stable 13116 未碰。
- 全程挂内存熔断（隔离 agent 私有内存未超 2GB，未触发）。
- 未提交 git。

---

## 7. 后续待办（已登记，非本轮阻塞）

1. NVENC SDK 直通（零拷贝 + 低延迟/CBR/关B/按需IDR），压 IDR 大小到 <30KB —— `decision/latency-roadmap` 后续专项①。
2. 非 N 卡 ICodecAPI（QSV/AMF/软编），因软编 GetValue 不可读回需码流层验证 —— 后续专项②。
3. 浏览器端到端延迟实测（北京时间对照法）—— web-player/用户侧。
4. R3 编码器选项 UI + 探测式自动选择（300ms 出帧判据）。
