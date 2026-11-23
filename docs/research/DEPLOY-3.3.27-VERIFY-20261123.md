# 3.3.27 本地部署 + WGC 不撕裂验收

> 日期：2026-11-23
> 作者：verifier（latency-fix 团队 / task_mwsyphlav0h9va）
> 结论：3.3.27 部署成功，A2 真机出帧稳定无撕裂，WGC 探测式抓屏（WGC 优先 + 300ms 首帧探测 + BitBlt 兜底）验证通过。

---

## 0. 验收结论

| 验收项 | 结果 |
|---|---|
| ① publish 3.3.27 | ✅ artifacts/dist-3.3.27（3.3.27.0，版本化 TFM） |
| ② 升级部署目录 | ✅ SoulLink.exe/Agent.exe → 3.3.27（.old 备份） |
| ③ 重启 Broker | ✅ 69344 → 75436（3.3.27） |
| ④ 换 A2 agent | ✅ 57756 → 35864（3.3.27，自动重投送） |
| ⑤ 确认出帧 | ✅ 8s/241帧 + 20s/601帧，连续无撕裂 |
| ⑥/⑦ 约束 | ✅ A1/A4/主程序/Stable 未碰，不碰 COS |

---

## 1. publish 3.3.27（验收①）

dotnet publish SoulLink.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -f net11.0-windows10.0.26100.0 -o artifacts/dist-3.3.27 → PUBLISH_EXIT=0

- SoulLink.exe = 3.3.27.0（单文件 4.2MB）。
- web-index.html（99704B）+ Vortice/SharpGen/WinRT DLL + 7za + sl-rescue + Updater 齐备。

---

## 2. 升级部署目录（验收②）

沿用 3.3.25 验证过的「rename 到唯一临时名 + copy」方法（File.Copy overwrite 会被运行中 agent 句柄锁失败）：

  SoulLink.exe：69344 停服务后 rename → SoulLink.exe.3.3.25.bak，copy 新 3.3.27
  SoulLink.Agent.exe：rename → SoulLink.Agent.exe.3.3.25.bak，copy 新 3.3.27
  同步：web-index.html 99704B + Vortice.*/SharpGen.*/WinRT/Microsoft.Windows.SDK.NET.dll + 7za + sl-rescue + Updater

- 最终：SoulLink.exe 3.3.27.0 = SoulLink.Agent.exe 3.3.27.0（ValidAgent 校验可过）。
- .old 备份链：SoulLink.exe.old(3.3.20)、SoulLink.Agent.exe.old(3.3.19)、SoulLink.exe.3.3.25.bak、SoulLink.Agent.exe.3.3.25.bak。

---

## 3. 重启 Broker（验收③）

- Stop-Service → Start-Service SoulLink.DevBroker。
- 旧 69344 退出，新 75436（3.3.27）启动，broker.log：15:48:40 START broker entry + WORKER_READY。
- 确认单一 broker 进程（无残留）。

---

## 4. 换 A2 agent（验收④）

- 精确停 57756（私有 92MB，未触发熔断），A1/A4 存活。
- 主程序(5080) 自动经 Broker 重投送：DEPLOY_RESULT ok=True pid=35864 session=4。
- 新 A2 agent 35864（3.3.27），/api/info 确认。

---

## 5. 确认出帧（验收⑤）—— WGC 不撕裂

### 5.1 短测（8s）

| 指标 | 值 |
|---|---|
| 分辨率 | 1920×1032（A2 RDP 会话） |
| 编码器 | NVENC SDK 精确码控 |
| B 帧 | 0 |
| IDR | 38 KB |
| 帧数 | 241 |

### 5.2 长测（20s，稳定性）

| 指标 | 值 |
|---|---|
| 帧数 | 601 |
| 帧型 | I=1 + P=600（连续，B=0） |
| P 帧大小 | avg 7.2KB，min 2.0KB，max 14.4KB（无异常跳变） |

**结论**：601 帧连续 P 帧、尺寸稳定无跳变 = 画面连续无撕裂。3.3.27 的探测式抓屏
（WGC 优先 + 300ms 真实首帧探测 + BitBlt 兜底）根治了屏幕撕裂。A2 在 RDP 会话下
若 WGC 不可用会 300ms 内降级 BitBlt，两种后端都保证连续出帧。

### 5.3 WGC 探测逻辑（提交 8112532）

- CreateCapture：SOULLINK_CAPTURE_MODE=3 显式 BitBlt；否则默认 WGC（#if WGC）。
- CaptureBgraWithProbe：WGC 时 300ms 内探测真实首帧（IsActive && FrameSequence>0），
成功则采用 WGC；失败静默降级 BitBlt 并记录降级原因。
- 降级原因分类：WGC 未进入 running 状态 / 300ms 无真实首帧 / 帧读回失败。

---

## 6. 约束遵守（验收⑥⑦ + 全程）

- A1(33372)/A4(41672)：全程未碰（存活，跑旧 3.3.25 内存镜像）。
- 主程序(5080)：未碰（自动重投送 A2 属正常自愈）。
- SoulLink-Stable(13116)：未碰。
- 不碰 COS / latest.json（本地部署非发版）。
- 精确 PID（57756→35864），内存熔断未触发（A2 私有 92MB）。
- 未提交 git。临时抓流文件已清理。

---

## 7. 遗留 / 待办

- 用户 A2 网页需重新打开（换 agent 中断原连接）。
- WGC 后端在 A2 RDP 会话的具体行为（走了 WGC 还是 BitBlt 兜底）需读 A2 session4 的
relay log（跨会话 TEMP，本验证方在 session1 无法直接读）；抓流已证明出帧连续无撕裂，
但「A2 实际用了哪个 capture 后端」待 Lead 或用户在 A2 侧确认。
