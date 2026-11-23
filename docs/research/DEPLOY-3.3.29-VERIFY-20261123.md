# 3.3.29 部署 + 主程序重启 + DDA 生效验收

> 日期：2026-11-23
> 作者：verifier（latency-fix 团队 / task_mwt0tjc1xw9k5k）
> 结论：主程序重启加载 3.3.29、监控修复生效（A2 自动重投）、DDA 首次出帧成功无撕裂；
>   但发现 DDA「停止→重启」二次启动失败缺陷（需修复）。

---

## 0. 验收结论

| 验收项 | 结果 |
|---|---|
| ① 重启主程序 | ✅ 45088 → 47160（3.3.29） |
| ② 监控修复 | ✅ A2 停后自动重投 62376 |
| ③ captureBackend | ✅ /api/info 返回 DXGI-DDA |
| ④ 撕裂黑块消失 | ✅ 900 帧连续 P 帧无撕裂（首次出帧） |
| ⑤ 约束 | ✅ 不碰 Stable/A1/A4/Broker，不碰 COS |
| ⚠️ 缺陷 | DDA 停止→重启二次启动失败（抓屏无帧） |

---

## 1. 重启主程序（验收①）

- 精确停 45088（私有 333MB），Stable 13116 未受影响。
- 重新启动 dist/SoulLink.exe（3.3.29），新 PID 47160。

---

## 2. 监控修复验证（验收②）

主程序 47160 启动后，会话监控自动检测 A2 掉线并重投：

  rdp-debug: 16:46:43 会话监控：「山客」会话不可用（Disc），主动发起 RDP 连接
             16:46:54 会话监控：通道投送成功 山客
  broker.log: DEPLOY_RESULT ok=True pid=62376

**结论**：3.3.29 的会话监控停摆 bug 修复生效，停 agent 后自动重投（上一轮 3.3.28 停摆）。

---

## 3. DDA 生效验证（验收③）

A2 /api/info 返回（新增 captureBackend 字段）：

  {"ok":true,"account":"acc_a2","session":4,"pid":62376,"port":19081,"captureBackend":"DXGI-DDA"}

**结论**：A2 抓屏后端 = DXGI-DDA（3.3.29 语义下 CaptureMode=1 = 显式 DDA）。

---

## 4. 抓流验证撕裂黑块消失（验收④）

### 首次出帧（成功，无撕裂）

ffmpeg 抓 /stream 15s → ffprobe 解析：

| 指标 | 值 |
|---|---|
| 分辨率 | 1920×1080（DDA 完整全屏，对比 WGC 的 1920×1032） |
| 帧数 | 900 |
| 帧型 | I=1 + P=899 连续（B=0） |
| P 帧 avg | 10.3KB，p50=9.7KB，p95=13.9KB |
| captureMs | 2~33ms（DDA 快速抓屏） |

**结论**：DDA 从 GPU 合成缓冲直接复制，天然无撕裂。900 帧连续 P 帧、尺寸稳定，
对比 3.3.27 WGC/BitBlt 的撕裂黑块（21/100 帧），DDA 根治撕裂。

### 二次启动（失败，缺陷）

viewer 断开后管线停止，重新订阅 /stream 时 5XX：

  首帧等待超时：状态=waiting 帧=0 采集=DXGI-DDA 错误=抓屏没有返回原始帧

---

## 5. 缺陷：DDA「停止→重启」二次启动失败

### 5.1 根因（代码级，已定位）

1. SessionRecorder.Capture 是静态字段，仅在初始化时 = CreateCapture()（:31），热切时才重建（:371）。
2. Loop() 的 finally 在管线停止时 Capture.Dispose()（:485），DxgiDdaCapture._disposed=true。
3. StartCore 重新启动 worker 时不重建 Capture（:315-320 只 new Thread）。
4. 新 worker 调 CaptureBgra → DxgiDdaCapture.CaptureBgra 首行 if (_disposed) return null（:155）。
5. 结果：状态=waiting 抓屏没有返回原始帧 → 首帧超时 → 5XX。

### 5.2 修复建议（给 stream-core）

- 管线重新启动时（StartCore 或 Loop 开头）重建 Capture：if (Capture is IDisposable disposed) 重建，
  或在 finally 里不 Dispose 静态 Capture（改为只释放内部 duplication 资源、保留可重建状态）。
- DxgiDdaCapture 增加 ReInit/Reset 方法，或 Dispose 后允许 Start() 重新建立（当前 _disposed 一票否决）。

---

## 6. 约束遵守（验收⑤ + 全程）

- Stable 13116 / A1 33372 / A4 41672 / Broker 55652：全程未碰。
- 不碰 COS / latest.json。
- 精确 PID（45088→47160、86120→62376），内存熔断未触发。
- 未提交 git。

---

## 7. 遗留

- DDA 二次启动失败缺陷待 stream-core 修复（§5.2），修复后需复测「viewer 断开→重新订阅」场景。
- A1/A4 仍跑 3.3.28 旧 agent（由 Stable 3.3.20 投送，禁碰），未换 3.3.29。
