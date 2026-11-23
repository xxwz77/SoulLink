# 交接文档 · 视频流延迟与撕裂攻坚收官（2026-11-23 下午）

> 状态：**代码全部提交+推送（3.3.33 / e9443c2），但最新修复尚未部署到 A2 真机**
> 上一份：`HANDOFF-20261123-LATENCY.md`（上午，4 秒延迟根因定位）

---

## 0. 三句话现状

1. **延迟 4s → ~453ms、撕裂根治、NVENC GPU 直通**，三大核心成果代码已全部完成并推送 GitHub。
2. **但最新修复（3.3.32/3.3.33）未部署到 A2**——A2 agent 还是旧代码（50560），所以你看到「一直连接中」「探测刷屏」「断线」都是旧代码表现。
3. **下一步第一件事：部署 3.3.33 到 A2 真机**，再验证。

---

## 1. 本轮完成的核心成果（对照原始痛点）

| 原始问题 | 最终结果 | 版本 |
|---|---|---|
| 4 秒端到端延迟 | ~453ms（NVENC SDK 硬编 + 按需 IDR + 30fps） | 3.3.20~3.3.25 |
| CPU截图→内存→GPU 两趟往返 | NVENC SDK D3D11 直通（IDR 99.6KB→37KB、B帧 24→0） | 3.3.21~3.3.23 |
| 屏幕撕裂黑块 | DXGI Desktop Duplication 根治（900 帧连续无撕裂） | 3.3.28 |
| 抓屏 17~31ms | DXGI DDA 0.13ms / WGC 2~8ms | 3.3.27~3.3.28 |
| 画质不可调 | preset/码率/帧率/vbv 自由档位 + 实时热更新 | 3.3.23~3.3.25 |
| 抓屏后端无法知道/切换 | 网页显示当前后端 + 手动热切（自动DDA→WGC→BitBlt） | 3.3.29 |
| 非 N 卡低延迟 | 手写 ICodecAPI + 码流实测（软 MFT 忽略属性，QSV/AMF 待验证） | 3.3.26 |

---

## 2. 关键技术突破（推翻旧结论）

1. **「RDP 建不了硬件 D3D11 → DXGI 死路」是误判**——旧实测显存耗尽（E_OUTOFMEMORY，只剩 10MB）导致瞬时失败，非架构限制。DXGI DDA 在 RDP 会话实测可用（DuplicateOutput 成功 + 18ms 出帧）。`DXGI-DDA-RDP-20261123.md`
2. **NVENC SDK 直通**（弃 MF 硬编的 D3D11 死路）：P/Invoke nvEncodeAPI64.dll（系统自带，零体积），rcParams 精确码控。`NVENC-SDK-*`、`GPU-ZERO-COPY-20261123.md`
3. **编码参数实时热更新**：不重投 agent、不重连 viewer，改码率/preset/vbv/帧率立即生效。`NVENC-SDK-QUALITY-PRESETS-20261123.md`

---

## 3. 抓屏后端最终形态

```
降级链（CaptureMode 0=自动 / 1=DDA / 2=WGC / 3=BitBlt）：
  DXGI Desktop Duplication（主选，0.13ms，无撕裂，独占全屏可抓）
    → WGC（次选，2~8ms，抓窗口，配黑帧过滤兜底）
    → BitBlt（最后兜底，17~31ms，会撕裂）
```

- **DXGI DDA 是根治撕裂的答案**（朋友 RDP 里阳光不撕裂，就是因为它用 DDA）；
- **BitBlt 必须保留**（独占全屏游戏 WGC 抓不到时兜底，虽撕裂但能看到画面）。

---

## 4. 已知待办（按优先级）

### P0：部署 3.3.33 到 A2（下一步第一件事）

- A2 agent 还是旧代码 50560（17:15 启动），3.3.32/3.3.33 修复未部署。
- 症状：网页「一直连接中」、[采用 DXGI-DDA] 每 30ms 刷屏、编码器不产帧（total 消失）、频繁断线。
- 待部署修复：探测锁定、IDR 死锁、error 自恢复、前端看门狗按帧计时。

### P0.5：操作卡 + 前端重连循环看不到画面（3.3.33 部署后新反馈，未定位）

**用户反馈（2026-11-23 18:10，A2 已部署 3.3.33）**：
1. 缩小浏览器/拖动内容很卡，且「鼠标放开了远端没放开」（不确定）——疑似输入注入 up 事件丢失；
2. 网页画面看不到 RDP，一直连接中。

**日志证据（soullink-relay-96588.log）**：
- 前端「断开→重连→断开」循环：18:06:56断开→18:08:45重连→18:09:22断开→18:09:33重连→18:09:37断开→18:10:00断开，每次重连触发「新观众按需IDR」；
- staticSkipped 数值极大且持续增长（1956→2986→3458）：操作画面时仍被判「静止去重跳过」，疑似 DDA 抓帧未及时更新（画面变了但 SequenceEqual 判没变）。

**待查方向**：
1. 输入注入 up 事件丢失（down/up 成对性 + 断线释放）；
2. 前端重连循环为何不收敛；
3. DDA 抓帧在「操作画面」时是否及时产出新帧（staticSkipped 异常高）。

> 已记录 team 记忆 `todo/input-stuck-and-stream-reconnect-loop`。

### P1：编码器根因修复验证（已修代码，未真机验）

3.3.32 修了 4 个 bug（见 `ENCODER-REBUILD-HANG-20261123.md`）：
1. 静止去重 + 新观众要 IDR 冲突 → 死循环（「操作卡/画面不动」主因）
2. error 状态无自恢复
3. DDA 探测未锁定（每帧刷日志）
4. total 日志缺活性探针

### P2：设置页 UI 整理（已记录，未做）

- 系统优化区 + Defender 排除区「放错地方 + 要整齐干净」（用户反馈）。

### P3：可选后续

- WGC V2 增强（DDA 兜底优化，调研完成 `WGC-V2-RESEARCH-20261123.md`）
- 完整 GPU 零拷贝（纹理直通不落 CPU）
- APP（设计已对齐 H.264，工程暂缓）

---

## 5. 环境与铁律（沿用，不变）

### 运行进程（2026-11-23 18:00）

```
SoulLink         68528  session 1  dist/SoulLink.exe       <- 主程序（3.3.31）
SoulLink         75760  session 0  Program Files          <- Broker
SoulLink-Stable  13116  session 1  dist/stable            <- 挂机测试版【禁碰】
SoulLink.Agent   33372  session 2  Program Files          <- A1【禁碰】
SoulLink.Agent   41672  session 3  Program Files          <- A4【禁碰】
SoulLink.Agent   50560  session 4  Program Files          <- A2（旧代码，待重投）
```

### 铁律（逐条有事故背景）

1. **绝不碰 dist/stable/SoulLink-Stable.exe**（挂机测试版）
2. **不用 A1/A4 做输入控制**（只读抓屏）
3. **不重启 session1 主程序**（断 RDP）——但部署 3.3.33 必须重启，需用户授权
4. **停进程只用精确 PID**
5. **不发布、不碰 COS、不改 latest.json**

---

## 6. 协作约定（本轮新增）

1. **每次 commit 必 push**（用户约定，已存团队记忆 decision/commit-push-rule）
2. **前端改动必须截图验证渲染**（CSS 回归教训：改 CSS 只验语法不够）
3. **代码改了必须部署到真机才算闭环**（本轮「改了没部署」是最大教训）

---

## 7. 关键研究文档索引（本轮新增，都在 docs/research/）

- `GPU-ZERO-COPY-20261123.md` —— GPU 直通路线（唯一路线=NVENC SDK）
- `NVENC-SDK-PROBE / INTEGRATION / RCPARAMS / QUALITY-PRESETS` —— NVENC 全链路
- `DXGI-DDA-RDP / DXGI-DDA-CAPTURE` —— 撕裂根治（推翻死路结论）
- `MATURE-LATENCY-PLAYBOOK` —— Sunshine/OBS/Moonlight 延迟做法对照
- `MF-LOWLATENCY / ICODECAPI-PROBE` —— 非 N 卡低延迟（软 MFT 忽略属性的硬事实）
- `ENCODER-REBUILD-HANG` —— 编码器频繁重建根因
- `WGC-V2-RESEARCH` —— BGI WGC V2 帧完整性做法
- `SETTINGS-PAGE-LAG` —— 设置页卡顿归因
- `DATA-CENTER-SINGLE-SOURCE / AUDIT` —— 数据中心唯一入口
- 各 `DEPLOY-3.3.x-VERIFY` —— 部署验收记录
