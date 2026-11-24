# 审计 08 · RDP 基准侧延迟归因：RDP 自身画面延迟量级 + 233ms 中的物理/会话下限

> 日期：2026-11-24
> 作者：rdp-session-analyst（dsh-team `team_mwu35l8pae8oj5` / member `m_mwu3790mafbish` / task `task_mwu37oadalfa9t`）
> 性质：**只读审计**。未改任何产品代码/脚本/配置/版本号；未碰 `dist\stable\SoulLink-Stable.exe`；未使用 A1/A4 会话；只读观察 console(session1) 与 A2(session4) 的进程/日志/注册表。
> 证据分级：**【代码事实】**＝仓库源码 file:line；**【已有实测】**＝`docs/research/` 前序实测；**【本次实测】**＝本次只读查询（进程/会话/日志/注册表）；**【推断】**＝有依据推导；**【待验证】**＝无证据、需实测。

---

## 0. 一句话结论（先回答用户两个追问）

1. **（a）RDP 本身传画面当然有延迟，但不是"慢"的那一端。** RDP 在局域网同机的端到端延迟物理下限约 **20~38ms**，现实典型约 **20~60ms**（本机已设 `DWMFRAMEINTERVAL=15` → 合成节拍 60fps，p50=15.2ms 实测）。它低，是因为它**不做 CPU 回读、不做跨 GPU CPU 转换、不堆多帧 viewer 队列、客户端解码后直绘**。

2. **（b）233ms 是差分，不是 SoulLink 的绝对延迟。** 用户在同一 RDP 会话里同屏对照秒表，多次截图取平均：
   ```
   233ms = SoulLink(浏览器) 端到端 − RDP 端到端
   →  SoulLink 绝对端到端 = 233 + RDP自身延迟 ≈ 233 + (20~60) ≈ 253~293ms
   ```
   也就是说：**RDP 那 20~60ms 要从 233 里"加回去"，不是扣掉**。RDP 是这条测量的"零点基准"，它不是 233ms 的一部分，而是被差分式减掉的那部分。

3. **最核心的矛盾（账外延迟）**：服务端日志自报端到端 ~85~130ms（`E2E-LATENCY-BASELINE-20261124.md`），而真机差分 233ms 反推出的 SoulLink 绝对延迟约 253~293ms。两者之间差 **~120~160ms 是"日志没记账的延迟"**——它不在 agent 的 capture/convert/encode/queueAge 里（本次实测这些字段合计才 ~15~20ms），最可能的藏身处是**浏览器段**（HTTP chunked 拼包 + WebCodecs + 2D canvas `drawImage` GPU 回读 + 假 15fps 时间戳），这归 webclient-instrumentation 成员的份内，本报告只给出"它不在 RDP 侧、也不在服务端日志字段里"的排除性结论。

---

## 1. RDP 自身画面传输延迟的证据化估算（分段 + 证据等级）

### 1.1 合成节拍（DWM）【已有实测】

| 项 | 数值 | 证据 |
|---|---|---|
| RDP 会话 DWM 合成节拍 | **~60fps，帧间隔 p50=15.2ms** | `RDP-DDA-CEILING-EXPERIMENT-20261124.md` §2：mousemove 组 64.7~65.9fps，p50=15.2ms/p95=15.8ms/max=31ms |
| console 会话对照 | **~253fps，p50=4.1ms** | 同文档 §2 对照组 |
| 静止桌面 | **0.5fps**（8 帧/15.26s，变化驱动合成） | 同文档 §2 静止组 |
| 本机注册表现状 | `DWMFRAMEINTERVAL=15` 已设（RDP-Tcp） | 本次只读 `HKLM\...\Terminal Server\WinStations\RDP-Tcp` = 15 |
| 官方默认上限 | 30fps；DWMFRAMEINTERVAL=15 → 60fps 上限（非实际值） | KB2885213 + `RDP-FRAMERATE-AUTHORITY-20261124.md` §1 |
| 官方编码预算第二口径 | 平均编码时间须 <33ms → 支撑 ≤30fps | RemoteFX 计数器文档，同文档 §1.2 |

**关键点**：RDP 会话的 DWM 合成是"变化驱动 + 帧间隔预算"，合成节拍 p50=15.2ms 是 RDP 与 SoulLink **共同**的上游节拍（两者抓的都是 DWM 合成结果）。这一段的 0~16.7ms 在差分测量里**被抵消**，不进入 233ms。

### 1.2 抓屏 → 编码 → 传输 → 客户端（RDP 侧，为什么它低）【推断为主，标注】

RDP 图形管线（Direct Stream / RemoteFX）与 SoulLink Relay 的**机制级差异**，这是"RDP 为什么体感更跟手"的根：

| 环节 | RDP 做了什么 | SoulLink Relay 做了什么 | 证据 |
|---|---|---|---|
| 抓屏 | DWM 重定向面**直接交给 RDP 图形管线**，表面已在显存，**无 CPU 读回** | DDA `AcquireNextFrame` + staging `CopyResource` + `Map` + `Marshal.Copy` 把 8.3MB BGRA 读回 CPU（`DxgiDdaCapture.cs:428-472` ReadBackTexture） | 【代码事实】vs【推断】RDP 内部实现 |
| 色彩/格式 | 保持 GPU 原生格式，**无 CPU 转换** | 跨 GPU（Intel 抓屏 + NVIDIA 编码）→ 零拷贝禁用，CPU `BgraToNv12` 并行转换 1~2ms（`SessionRecorder.cs:1332-1414`） | 【代码事实】 |
| 编码 | **bitmap delta（静态/UI 区域）** + AVC444 满屏视频模式；官方预算 33ms/帧，实际硬编个位数 ms | 全帧 H.264 NVENC-SDK（`SessionRecorder.cs:1006` `EncodeNv12`），无 delta 路径 | 【官方文档】+【推断】 |
| 帧队列 | **无多帧 viewer 队列**，编完即发 | viewer.Pending 有界队列（深度 2，`SessionRecorder.cs:97-98`），帧龄 0~18ms | 【代码事实】vs【推断】 |
| 传输 | RDP 自有 TCP 流（混合位图/视频） | HTTP chunked 逐 NAL `WriteAsync+Flush`（`AgentWebServer.cs:481-503`），fetch 形态固有"拼包税" | 【代码事实】 |
| 客户端 | 解码后**直绘**，无攒帧、无假时间戳 | WebCodecs + 2D canvas `drawImage`（GPU→CPU→GPU 回读）+ `ts+=66666` 假 15fps 时间戳 | 【代码事实】`AUDIT-20261124-05-WEB-CLIENT-GAP.md` §1/§3 |

### 1.3 RDP 端到端延迟量化（LAN 同机）【推断 + 已有实测混合】

| 段 | 数值 | 等级 |
|---|---|---|
| DWM 合成等待 | 0~16.7ms（p50=15.2ms） | 已有实测 |
| RDP 编码（bitmap delta / AVC444） | ~2~10ms（官方预算 33ms，硬编实际个位数） | 推断 |
| 传输（LAN/回环） | 0.8~5ms | 已有实测（AUDIT-00：/live 0.8ms p95 4.6ms） |
| 客户端解码 + 直绘上屏 | 解码 2~5ms + 0~16.7ms vsync | 推断 |
| **RDP 端到端合计** | **物理下限 ~20~38ms，现实典型 ~20~60ms** | 推断（下限与 `HANDOFF-20261124-LATENCY-OPTIMIZED.md` §3.4 "1~2 vsync 16.7~33.4ms + 解码 2~5ms ≈ 20~38ms" 及 README "局域网端到端 <60ms" 口径一致） |

**结论：RDP 自身延迟 ≈ 20~60ms，这是 SoulLink 在这台机器上"永远消不掉"的那部分下限。**

---

## 2. 当前真实状态核对（本次实测，附时间戳）

> 观测时刻：2026-11-24 10:40（Asia/Shanghai）；日志末行时间戳 10:33:45。

### 2.1 进程与会话【本次实测】

```
Id     SessionId ProcessName     StartTime
6616   0         SoulLink        2026/11/24 3:05:21   (Broker 服务)
13364  1         SoulLink        2026/11/24 3:08:32   (主程序, console)
13392  1         SoulLink-Stable 2026/11/24 3:05:24   (挂机测试版【禁碰】)
38324  2         SoulLink.Agent  2026/11/24 3:06:14   (A1, session 2)
6904   3         SoulLink.Agent  2026/11/24 3:06:25   (A4, session 3)
27656  4         SoulLink.Agent  2026/11/24 10:14:28  (A2, session 4)

query session:
  console    lummy  1  Active
  rdp-tcp#0  A1     2  Active
  rdp-tcp#1  A4     3  Active
  rdp-tcp#2  A2     4  Active
```

### 2.2 A2 agent relay 日志管线真实值【本次实测】

日志文件：`C:\Users\A2\AppData\Local\Temp\soullink-relay-27656.log`
启动行：`10:14:28.326 relay 启动 pid=27656 pipe=SoulLink_Relay_4_28740070 session=4`；`网页直连已启动 port=19081 account=acc_a2 session=4`

抓屏/编码拓扑（10:15:17 / 10:33:29 两处相同）：
```
[capture] DXGI-DDA 建立成功：1920x1080
[capture] DXGI-DDA 抓屏源=Intel(R) UHD Graphics(0x8086)
[video]  编码器=NVENC-SDK 配置码率=18000kbps MFT读回=18000kbps fps=60 尺寸=1920x1080
[gpu]    抓屏源=Intel(0x8086); 编码器=NVENC-SDK; 同GPU=false; 零拷贝=禁用
```

最近 8 条「共享管线」行（10:33:29.426 → 10:33:45.011）：

| 时间戳 | total | captureMs | convertMs | encodeMs | staticSkipped | keepalive | queueDepthMax | queueAge | queueAgeMax |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 10:33:29.426 | 1830 | 8 | 2 | 4 | 614 | 153 | 1 | 0 | 18 |
| 10:33:31.782 | 1860 | 10 | 1 | 3 | 622 | 155 | 1 | 0 | 18 |
| 10:33:33.162 | 1890 | **54** | 1 | 4 | 622 | 155 | 1 | 0 | 18 |
| 10:33:34.949 | 1920 | **42** | 1 | 3 | 624 | 155 | 1 | 0 | 18 |
| 10:33:36.625 | 1950 | 4 | 1 | 9 | 624 | 155 | 1 | 0 | 18 |
| 10:33:38.298 | 1980 | 9 | 1 | 4 | 624 | 155 | 1 | 0 | 18 |
| 10:33:40.056 | 2010 | **44** | 1 | 5 | 624 | 155 | 1 | 0 | 18 |
| 10:33:41.712 | 2040 | 5 | 1 | 5 | 624 | 155 | 1 | 0 | 18 |
| 10:33:43.299 | 2070 | 23 | 2 | 5 | 624 | 155 | 1 | 0 | 18 |
| 10:33:45.011 | 2100 | **70** | 2 | 5 | 624 | 155 | 1 | 0 | 18 |

**判读（必须注意）**：
- 当前桌面是**静止状态**：`staticSkipped` 持续增长（614→624）、`keepalive=155`、`queueDepthMax=1`、`queueAge=0`。fps 换算（total 1830→2100 用 15.6s）≈17fps，但这是**保活节拍（2fps 目标 + 零星真实帧）驱动的静止速率**，不是动态能力。
- captureMs 的高值（42/44/54/70）是 **DDA 阻塞等帧超时语义**（`AcquireNextFrame(100)` 在静止桌面等到接近超时返回 WAIT_TIMEOUT、复用上一帧），**不是抓屏工作耗时**——与 `E2E-LATENCY-BASELINE-20261124.md` "静止时 captureMs≈100 是阻塞等帧语义" 一致。
- 动态场景的真实工作成本见基线文档：captureMs p50=8.4(4~19) + convertMs 7.8 + encodeMs 4.6 ≈ 19.4ms（`E2E-LATENCY-BASELINE` §1/§2）；并行转换落地后 convertMs 已降到 1~2ms（本次日志实测，与 `BGRA-NV12-PARALLEL-20261124.md` "7~8→2ms" 一致）。

---

## 3. 分段表（三栏：差分中抵消 / SoulLink 独有 / RDP 独有）

> 单位 ms，均为 LAN 同机、1920×1080、60fps 配置、A2(session4) 场景的量级。标 ★ 的进 233ms；标 ▲ 的要加回绝对延迟；其余在差分中抵消。

| 段 | 共有（差分中抵消） | SoulLink 独有（★ 直接进 233ms） | RDP 独有（▲ 加回绝对延迟） | 证据等级 |
|---|---|---|---|---|
| 桌面内容 / DWM 合成等待 | **0~16.7ms**（60fps 节拍 p50=15.2ms） | — | — | 已有实测 |
| 抓屏 | — | ★ DDA Acquire 阻塞 + CPU 读回 8.3MB：**4~19ms(p50≈8，动态) / 静止 42~70ms 为超时语义** | ▲ 无（重定向面直通 GPU 管线，0 CPU 读回） | 代码事实 + 本次实测 |
| 色彩转换 | — | ★ BGRA→NV12 CPU 并行 **1~2ms**（跨 GPU 无零拷贝，物理限制） | ▲ 无（保持 GPU 原生格式） | 代码事实 |
| 编码 | — | ★ NVENC-SDK 全帧 H.264 **3~9ms**（跨 GPU 经系统内存上传） | ▲ bitmap delta + AVC444 **~2~10ms**（33ms 官方预算） | 本次实测 + 官方/推断 |
| 服务端队列 | — | ★ viewer.Pending 深度 2：**0~18ms**（queueAge/queueAgeMax） | ▲ 无多帧队列（编完即发） | 代码事实 + 本次实测 |
| 传输 | **0.8~5ms**（LAN 同机） | ★ HTTP chunked 拼包税（fetch 形态固有） | ▲ RDP 自有 TCP 流 | 已有实测 + 推断 |
| 客户端解码 | — | ★ WebCodecs + **2D canvas drawImage GPU 回读**（账外 ~140ms 最大嫌疑） | ▲ RDP 客户端硬解直绘 **2~5ms** | 推断 |
| 上屏合成 | **0~16.7ms vsync** | — | — | 推断 |

### 3.1 三栏汇总（直接回答"233 里有多少不可消、多少可优化"）

| 栏目 | 量级 | 定性 |
|---|---|---|
| **SoulLink 独有（★）** | 抓屏 ~8 + 转换 1~2 + 编码 3~9 + 队列 0~18 + 浏览器段 ≈ **合计约 20~40ms（服务端账内）+ 浏览器段账外 ~120~160ms** | **可优化**（尤其浏览器段账外延迟） |
| **RDP 独有（▲，加回绝对）** | **~20~60ms** | **物理/会话层下限，SoulLink 永远消不掉** |
| **共有（抵消）** | DWM 等待 0~16.7 + 传输 0.8~5 + vsync 0~16.7 | 差分里两边都有，不进 233 |

**量化结论**：
- **233ms 里"物理/会话层下限"占 0ms**——差分测量把共有段和 RDP 下限都抵消了，233ms **全部**是 SoulLink 相对 RDP 多出来的部分。
- 但换到**绝对延迟**口径：SoulLink 绝对 ≈ 233 + 20~60 = **253~293ms**，其中 **20~60ms 是 RDP 也逃不掉的物理下限（不可消）**，剩余 **~233ms 是 SoulLink 可优化部分**。
- 可优化部分里，**服务端账内只约 20~40ms**（抓屏+转换+编码+队列，都有日志字段），**~120~160ms 是账外延迟**——它不在 captureMs/convertMs/encodeMs/queueAge 里，是服务端日志与真机差分对不上的根因，必须由浏览器段埋点（rVFC + 服务端随帧下发 capturedAt）来定位。

---

## 4. 被测对象一致性核对（233ms 成立的前提）

**【本次实测，一致】**：RDP 会话侧，`query session` 显示 `rdp-tcp#2 = A2 = session 4（Active）`；浏览器串流侧，A2 agent 日志 `relay 启动 pid=27656 session=4` + `网页直连 port=19081 account=acc_a2 session=4`。**只要用户当时 RDP 窗口登录的是 A2 账号（窗口标题栏可见用户名），两者就是同一个 session 4 桌面，233ms 差分成立。**

**验证方法（若需复核）**：
1. `qwinsta` / `query session` 看 RDP 窗口对应哪个 session（RDP 窗口标题栏用户名 = A1/A2/A4）。
2. 浏览器页账号选择器当前账号（acc_a1/acc_a2/acc_a4）对应哪个 agent 端口（19079/19081/19083，见 `HANDOFF-20261124-LATENCY-OPTIMIZED.md` §10）。
3. 两者 session 号必须一致。若 RDP 看的是 A1(session2)、浏览器串的是 A2(session4)，则 233ms 是**跨桌面**的无效差分，需重测。
4. 额外风险：秒表在 RDP 会话内渲染时，**RDP 会话 DWM 合成率只有 ~60fps（p50=15.2ms）**，秒表数字本身以 ~16.7ms 粒度跳变；两画面取平均能压掉抖动，但单次截图的量化误差 ≥1 帧间隔（~16.7ms），多次取平均可接受（用户已按"多次截图取平均"操作）。

---

## 5. 结论（回答用户的 (a)(b) 与"233 里多少不可消"）

1. **RDP 有延迟，约 20~60ms（LAN 同机），物理下限 ~20ms。** 它低是因为：DWM 重定向面直通 RDP 图形管线（无 CPU 回读）、保持 GPU 原生格式（无 CPU 转换）、bitmap delta 而非全帧 H.264 GOP、客户端解码后直绘（无多帧 viewer 队列）。这是 SoulLink 与 RDP 的**机制差异**，不是"RDP 有什么黑科技"。

2. **233ms 是差分，方向必须反过来读**：SoulLink 绝对端到端 = 233 + RDP延迟 ≈ 253~293ms。RDP 那 20~60ms 不是"233 里能扣掉的部分"，而是"要加回绝对延迟的部分"。

3. **233ms 里：物理/会话层下限占 0ms（差分已抵消）；全部 233ms 都是 SoulLink 可优化部分**。但可优化部分中服务端账内只占 ~20~40ms，**~120~160ms 是账外延迟**，与"服务端自报 ~90ms"严重不符——这是本轮最核心矛盾，落在浏览器段（非 RDP 侧、非服务端日志字段），需 webclient-instrumentation 用 rVFC + 帧时间戳埋点定位。

4. **RDP 侧空间 = 0**：帧率上限（DWMFRAMEINTERVAL=15 → 60fps）已到顶（`RDP-DDA-CEILING-EXPERIMENT` 实测 p50=15.2ms），抓帧原料 60fps、SoulLink 当前动态有效 ~48~50fps（`RDP-FRAMERATE-BENCHMARK` §5），帧率缺口 ~10fps 在 SoulLink 管线不在 RDP。RDP 这一侧"没有可挖的延迟红利"。

---

## 6. 证据边界（诚实标注）

- RDP 内部各段（重定向面直通、bitmap delta、客户端直绘）的**毫秒数**是【推断】，机制依据来自官方文档（KB2885213 / RemoteFX 计数器 / DWM overview / DDA 文档）与本仓库实测对照，**未在 RDP 客户端内部埋点**。
- RDP 端到端 20~60ms 是【推断】区间，非本次直接实测；要拿到 RDP 的精确毫秒数需在 RDP 客户端侧做同款秒表差分（本任务只读边界不允许，也未分配）。
- 账外 ~120~160ms 的**具体落点未实测**（属 webclient-instrumentation 成员范围），本报告仅给出"不在 RDP 侧、不在服务端日志字段"的排除性结论。
- 本次未测 A1/A4 会话、未测真实游戏动态场景下的差分；以上量级均以 A2(session4) 静止/保活现状 + 动态基线文档为据。
