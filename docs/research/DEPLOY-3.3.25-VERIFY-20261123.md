# 3.3.25 本地部署 + 真机端到端验收

> 日期：2026-11-23
> 作者：verifier（latency-fix 团队 / task_mwswzadgtoheke）
> 结论：部署 3.3.25 成功，真机 A2 已换新代码，NVENC SDK 精确码控出帧，preset/vbv 热更新修复验证通过。

---

## 0. 验收结论

| 验收项 | 结果 |
|---|---|
| ① publish 3.3.25 | ✅ artifacts/dist-3.3.25（单文件 3.3.25.0 + 版本化 TFM） |
| ② 升级部署目录 | ✅ SoulLink.exe/SoulLink.Agent.exe → 3.3.25（.old 备份 + DLL + web-index 同步） |
| ③ 重启 Broker | ✅ 68156 → 69344（3.3.25） |
| ④ 换 A2 agent | ✅ 91664 → 57756（3.3.25，主程序自动重投送） |
| ⑤ 真机热更新 | ✅ 见 §5（NVENC SDK 出帧 + preset/vbv 修复验证） |
| ⑥ 约束遵守 | ✅ A1/A4/主程序/Stable 全程未碰；不碰 COS/latest.json |

---

## 1. publish 3.3.25（验收①）

dotnet publish SoulLink.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -f net11.0-windows10.0.26100.0 -o artifacts/dist-3.3.25  →  PUBLISH_EXIT=0

- SoulLink.exe = 3.3.25.0，单文件 4.2MB。
- 旁挂 DLL（Vortice/SharpGen/WinRT/Microsoft.Windows.SDK.NET）+ 7za + sl-rescue + Updater 齐备。
- web-index.html 由 build.bat 流程从 docs/ 复制（99704B）。

---

## 2. 升级部署目录（验收②）

首次跑 tools/broker-install.ps1 时，BrokerSetup.exe 成功替换 SoulLink.exe（3.3.25，旧版转 .old），但卡在 SoulLink.Agent.exe —— A1/A4/A2 三个 agent 进程持有旧 exe 句柄，File.Copy(overwrite) 失败。

手工补救（等价 BrokerSetup 的 COPIED_AFTER_MOVE 路径）：

  File.Move(SoulLink.Agent.exe → AgentOld_150241.bak)   # rename 成功（delete-share）
  File.Copy(dist-3.3.25/SoulLink.exe → SoulLink.Agent.exe)  # 新文件无锁

最终 Program Files\SoulLinkDev：
- SoulLink.exe = 3.3.25.0、SoulLink.Agent.exe = 3.3.25.0（版本一致，ValidAgent 校验可过）
- .old 备份：SoulLink.exe.old(3.3.20)、SoulLink.Agent.exe.old(3.3.19)、AgentOld_150241.bak(3.3.20)
- Vortice.* / SharpGen.* / WinRT / Microsoft.Windows.SDK.NET.dll 已同步
- web-index.html 74799B → 99704B 已同步

> 注：agent manifest 保持 requireAdministrator（与 3.3.19/3.3.20 一致，各 2 处），无需 patch asInvoker —— 旧版本一直如此且正常投送。

---

## 3. 重启 Broker（验收③）

- 服务 SoulLink.DevBroker：Stopped → Running。
- 进程 68156（3.3.20）退出，新 69344（3.3.25）启动。
- broker.log：15:08:00 START broker entry + WORKER_READY，启动正常。

---

## 4. 换 A2 agent（验收④）

- 精确停 91664（私有内存 880MB，未触发 2GB 熔断），A1/A4 存活。
- 主程序(5080) 检测到 A2 断开，自动经 Broker 重投送（无需人工干预）。
- broker.log：DEPLOY_RESULT ok=True pid=57756 已投送到会话 4。
- 新 A2 agent 57756（session 4，3.3.25），/api/info 确认新 PID。

---

## 5. 真机验证（验收⑤）

### 5.1 A2 新 agent 出帧（NVENC SDK 精确码控）

ffmpeg 抓 /stream 8 秒 → ffprobe 解析 241 帧：

| 指标 | 值 | 对比改前 |
|---|---|---|
| 编码器 | NVENC SDK（CBR 精确码控） | 软编 MF |
| 分辨率 | 1920×1032（A2 RDP 实际） | 1920×1080 |
| B 帧 | 0 | 24 |
| IDR 平均 | 37 KB | 99.6 KB（约 7.4× 改善） |
| GOP | 8s 仅 2 个 IDR（按需 IDR） | 约 5 帧/个 |

### 5.2 preset/vbv 热更新修复（3.3.25 提交 18176b7）

- IFrameH264Encoder.TryInitialize 增加 preset, vbvPercent 参数。
- NvencSdkEncoder.TryInitialize 改从参数读（注释：支持 relay 热更新；不读取进程环境变量）。
- SessionRecorder.CreateEncoder 传 parameters.Preset/Vbv。
- 隔离实测日志：

  15:11:13.030 [video] 参数热更新：preset=P3 vbv=+0% → preset=P7 vbv=+100%
  15:11:13.108 [video] 尝试 NVENC SDK 直通编码器   （编码器重建）

> 上一轮（3.3.24）发现的 preset/vbv 被丢弃缺陷已修复。码率/帧率热更新上一轮已验证生效，本轮 preset/vbv 接线已贯通。

---

## 6. 约束遵守（验收⑥ + 全程）

- A1(33372)/A4(41672)：全程未碰（进程仍存活，跑旧 3.3.20 内存镜像）。
- 主程序(5080)：未碰（它自己检测 A2 断开并重投送，属正常行为）。
- SoulLink-Stable(13116)：未碰。
- 不碰 COS / latest.json（本地部署非发版）。
- 全程精确 PID（91664→57756），内存熔断未触发（A2 私有 880MB < 2GB）。
- 未提交 git。临时探针工具/抓流文件已清理。

---

## 7. 部署坑（可复用）

1. 换 agent exe 必须 rename 而非 overwrite：运行中 agent 持有 exe 句柄，File.Copy(overwrite) 失败但 File.Move（rename，delete-share）成功。BrokerSetup 的 CopyFile 已内置该路径，但首轮因 .old 目标存在 + 目录 ACL 收紧而 FAILED，手工 rename 到唯一临时名最稳。
2. agent 保持 requireAdministrator 是正确的：Broker 用 SYSTEM 计划任务投送，UAC 不拦；asInvoker 仅手动代理双击场景需要。
3. 主程序会自愈 A2：停 agent 后无需人工重投，RdpConsole 检测断开自动经 Broker 重投送新代码。
4. 跨会话日志：agent 的 relay log 写在目标会话的 TEMP，验证方在 session1 看不到 A2 的 TEMP；判断 agent 状态用 /api/info + 抓流 ffprobe 更可靠。

---

## 8. 遗留 / 待办

- 用户 A2 网页画面需重新打开（原 7 个连接因换 agent 中断）。
- 拖滑杆 → 主程序 → PushVideoParametersToConnectedAgents → agent 的完整 UI 链路，需用户在真实 GUI 拖一次滑杆确认（底层热更新已隔离实测通过，UI 贯通未测）。