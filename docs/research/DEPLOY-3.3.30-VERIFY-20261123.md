# 3.3.30 部署 + 二次订阅修复闭环验收

> 日期：2026-11-23
> 作者：verifier（latency-fix 团队 / task_mwt1cdzzym3y7d）
> 结论：3.3.30 部署成功，二次订阅修复闭环验证通过（首次/二次/三次订阅均正常出帧，DDA 无撕裂）。

---

## 0. 验收结论

| 验收项 | 结果 |
|---|---|
| ① publish 3.3.30 | ✅ artifacts/dist-3.3.30（3.3.30.0） |
| ② 更新 dist+ProgramFiles | ✅ SoulLink.exe/Agent.exe → 3.3.30（.old 备份） |
| ③ 重启 Broker | ✅ 55652 → 34288（3.3.30） |
| ④ 重启主程序 | ✅ 47160 → 6872（3.3.30） |
| ⑤ 重投 A2 | ✅ 62376 → 73208（自动重投） |
| ⑥ 二次订阅修复 | ✅ 首次/二次/三次订阅均出帧 |
| ⑦ 无撕裂 | ✅ 598 帧连续 P 帧 |
| ⑧ 约束 | ✅ 不碰 Stable/A1/A4/Broker，不碰 COS |

---

## 1. publish + 部署目录（①②）

dotnet publish → artifacts/dist-3.3.30（3.3.30.0，版本化 TFM）。
dist/SoulLink.exe + Program Files SoulLink.exe/SoulLink.Agent.exe 均 rename 到 .3.3.29.bak 后换 3.3.30，
同步 Vortice/SharpGen/WinRT DLL + web-index.html(101987B) + 7za/sl-rescue/Updater。

---

## 2. 重启 Broker + 主程序 + A2（③④⑤）

- Broker：55652 → 34288（3.3.30），服务 Running。
- 主程序：47160（私有 348MB）精确停 → 6872（3.3.30）。
- A2：主程序 6872 会话监控自动重投 73208（session4，broker.log DEPLOY_RESULT）。

---

## 3. 二次订阅修复闭环（⑥，核心）

3.3.30 修复（提交 0955e5c）：Loop() finally 不再 Dispose 静态 Capture，解决 DDA 二次启动失败。

| 订阅轮次 | 抓流结果 | 帧型 |
|---|---|---|
| 首次 | ✅ 6.7MB | — |
| 二次（断开后重连） | ✅ 5.2MB | — |
| 三次 | ✅ 6.3MB | 598 帧 I=1 P=597 B=0 |

**结论**：二次/三次订阅均正常出帧，对比 3.3.29 二次订阅 5XX（状态=waiting 抓屏没有返回原始帧），
修复闭环验证通过。captureBackend 全程 = DXGI-DDA。

---

## 4. 无撕裂验证（⑦）

三次订阅抓流 10s：1920×1080，598 帧连续 P 帧（I=1 + P=597 + B=0），P 帧 avg 10.4KB，
尺寸稳定无异常跳变 = 画面连续无撕裂。DDA 从 GPU 合成缓冲直接复制，天然无撕裂。

---

## 5. 约束遵守（⑧ + 全程）

- Stable 13116 / A1 33372 / A4 41672：全程未碰（存活，跑 3.3.28 旧内存镜像）。
- Broker 34288（3.3.30）重启属部署流程，非碰挂机版。
- 不碰 COS / latest.json。
- 精确 PID（47160→6872、62376→73208），内存熔断未触发。
- 未提交 git。临时抓流文件已清理。

---

## 6. 遗留

- A1/A4 仍跑 3.3.28 旧 agent（由 Stable 3.3.20 投送，禁碰），未换 3.3.30。
- 用户 A2 网页需重新打开（换 agent 中断原连接）。
