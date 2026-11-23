# 3.3.31 部署 + 前端三修复验收

> 日期：2026-11-23
> 作者：verifier（latency-fix 团队 / task_mwt1tpa1z61m9k）
> 结论：3.3.31 部署成功，前端三修复（热切自动重连/拉流自愈/顶栏收敛）已部署到真机；
>   但 A2 出帧出现卡顿（帧数极少），与 config 变化（StreamFps 30→60、bitrate 8000→25000、vbv 0→70%）强相关，需 Lead 关注。

---

## 0. 验收结论

| 验收项 | 结果 |
|---|---|
| ① publish 3.3.31 | ✅ artifacts/dist-3.3.31（3.3.31.0） |
| ② 更新部署目录 | ✅ dist + Program Files（web-index 104722B + exe） |
| ③ 重启 Broker+主程序+A2 | ✅ 34288→75760、6872→68528、73208→50560 |
| ④ 前端新代码 | ✅ scheduleReconnect(2)/STALL_MS(3)/flex-wrap(9) 已部署 |
| ⑤ 约束 | ✅ 不碰 Stable/A1/A4，不碰 COS |
| ⚠️ 出帧卡顿 | A2 出帧极少（3~9 帧后停），疑似 60fps/25000kbps 配置问题 |

---

## 1. publish + 部署目录（①②）

dotnet publish → artifacts/dist-3.3.31（3.3.31.0）。
dist + Program Files → 3.3.31，web-index.html 101987B → 104722B，
同步 Vortice/SharpGen/WinRT DLL + 7za/sl-rescue/Updater。

---

## 2. 重启 Broker + 主程序 + A2（③）

- Broker：34288 → 75760（3.3.31）。
- 主程序：6872（336MB）→ 68528（3.3.31）。
- A2：主程序自动重投 73208 → 50560（session4）。

---

## 3. 前端三修复确认（④）

3.3.31 提交 60f64a3（前端热切自动重连 + 拉流自愈 + 顶栏收敛）：

| 修复 | 标记 | Program Files web-index |
|---|---|---|
| 热切自动重连 | scheduleReconnect | ✅ 2 处 |
| 拉流自愈 | STALL_MS(10s 无数据断流重连) | ✅ 3 处 |
| 顶栏收敛 | flex-wrap | ✅ 9 处 |

- docs/web-index.html 与 Services/WebPages.cs 双份同步（scheduleReconnect 2=2、STALL_MS 3=3）。
- Program Files web-index.html = 104722B，含上述全部标记。

---

## 4. A2 出帧卡顿（⚠️ 需 Lead 关注）

### 4.1 现象

A2 agent 50560 管线建立成功（DDA + NVENC 编码器），但 Loop 长时间无帧：

  17:16:33 [capture] DXGI-DDA 建立成功：1920x1080
  17:16:33 [video] 编码器=NVENC-SDK 配置码率=25000kbps fps=60
  （此后约 7.5 分钟无「共享管线 total=」日志）
  17:24:01 三个 viewer 断开 frames=3/5/9（零星帧）

### 4.2 根因分析（非 3.3.31 引入）

3.3.31 只改前端（web-index/WebPages/csproj/文档），后端与 3.3.30 完全一致。
出帧卡顿与 config 变化强相关：

| 配置 | 3.3.30 部署时 | 3.3.31 部署时 |
|---|---|---|
| StreamFps | 30 | 60 |
| RecordFps | 5 | 30 |
| VideoBitrateKbps | 8000 | 25000 |
| VbvBufferIncreasePercent | 0 | 70 |

3.3.30 部署时同样 DDA 能出 900 帧连续；3.3.31 部署时 config 变为 60fps/25000kbps/70%vbv 后出帧极少。
疑似 DDA 在 60fps 高负载 + RDP 会话下 AcquireNextFrame 卡顿，或 25000kbps CBR 码控在 DDA 场景的编码器内部问题。

### 4.3 建议

- 由 Lead/用户确认是否将 config 回退到 30fps/8000kbps/0%vbv 复测出帧；
- 或单独排查 60fps 下 DDA/NVENC 的帧率瓶颈。

---

## 5. 约束遵守（⑤ + 全程）

- Stable 13116 / A1 33372 / A4 41672：全程未碰（存活）。
- 不碰 COS / latest.json。
- 精确 PID（6872→68528、73208→50560）。
- 未提交 git。挂住的 ffmpeg 进程已清理，临时文件已删。

---

## 6. 遗留

- A2 出帧卡顿需 Lead 决策（回退 config 或排查 60fps DDA）。
- 前端三修复已部署，但需用户浏览器刷新加载新 web-index 才能验证 UI 效果。
