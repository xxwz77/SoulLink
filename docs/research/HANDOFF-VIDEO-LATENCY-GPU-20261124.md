# 交接文档 · 视频延迟 GPU 直通攻坚（2026-11-24 凌晨）

> 状态：**代码全部提交+推送（3.3.34），GPU 零拷贝已部署但未生效，延迟仍 444ms**
> 上一份：HANDOFF-VIDEO-LATENCY-DONE-20261123.md（4s→453ms 收官）

---

## 0. 三句话现状

1. **本轮做了大量正确方向的修复**：首帧 25s 死锁根治（25s→4s）、吞点击死循环、落点偏移、可见光标、码率/帧率上限放开、GPU 零拷贝（WGC+DDA 双后端直通 NVENC）。
2. **但核心目标未达成**：延迟仍 444ms（A1/A2 后曾降到 340，GPU 零拷贝后反而回到 444 且 DDA 后端未生效）。
3. **根因已定位但未闭环**：GPU 零拷贝代码正确（build 通过），但真机未生效——DDA 后端走的是 CPU 读回路径，零拷贝分支没跑起来。**下一步第一件事：查 agent 日志确认零拷贝为什么没生效**（PSNR 埋点 / "降级 CPU" 日志会说明卡点）。

---

## 1. 本轮已提交的修复（14 个 commit，全部推送）

| commit | 内容 | 状态 |
|---|---|---|
| 0d62882 | A3 帧率端到端对齐（WebServer clamp 30..60→1..120）+ 码率滑杆 25→500M + 版本 3.3.34 | ✅ 已部署 |
| a7da94e | Pointer Lock 锁定态 movementX/Y + ESC 一键菜单 + 自定义逃生门 | ✅ 已部署 |
| 521f27a | A2 帧到达即编码（抓屏前节流，消除失效定时节拍） | ✅ 已部署 |
| f13a873 | **首帧 25s 死锁根治**（forceKeyFrame 持续到 IDR 产出 + 探测链加速 + 双保险） | ✅ 已部署（25s→4s） |
| 91fc288 | **P0 吞点击死循环**（plLockPending 挂起 + 失败补发 click）+ **落点偏移**（norm 用 canvas 实际矩形）+ secure context 降级 | ✅ 已部署 |
| 468f2cf | **GPU 零拷贝**（WGC 纹理直通 NVENC：VideoProcessor 转 NV12 + 跨设备共享 + EncodeD3D11Texture） | ⚠️ 已部署但未生效 |
| 1d1a09a | 可见光标指示（绝对模式跟随 move） | ✅ 已部署 |
| c323239 | **零拷贝空转死循环修复**（zeroHandled 标志）+ **DDA 零拷贝接入**（私有 Shared 纹理 + ReleaseFrame 时序） | ⚠️ 已部署但未生效 |
| 4147798 | 安卓 App：输入接入 + 画面打通（directToken 鉴权 + 触屏手势 + SurfaceView/MediaCodec） | ✅ 已提交（assembleDebug 通过） |

研究文档（docs/research/）：
- CURSOR-AND-POINTER-LOCK-20261124.md —— 吞点击 + 落点 + 光标诊断
- FIRST-FRAME-25S-DIAGNOSIS-20261124.md —— 首帧死锁诊断
- GPU-ZERO-COPY-IMPL-PLAN-20261124.md —— GPU 零拷贝方案（阶段A）
- MATURE-GPU-ZERO-COPY-EXPERIENCE-20261124.md —— 成熟方案经验汇总
- DDA-TEXTURE-SHARED-HANDLE-20261124.md —— DDA 跨设备共享句柄研究

---

## 2. 核心问题：延迟 444ms 未降，GPU 零拷贝未生效

### 2.1 用户实测（2026-11-24 01:20）

- 延迟：**444ms**（A1/A2 后曾 340，GPU 零拷贝后回到 444）
- 当前后端：**DDA**（网页设置显示）
- 首帧：DDA 首次 4s，后续快（首帧死锁已修好，25s→4s）
- WGC：不出画面（零拷贝空转 bug，c323239 已修但未验证）
- 光标：没看见（可见光标已部署，待确认是否生效）

### 2.2 已定位的根因链

1. **GPU 零拷贝没生效**：DDA 后端仍走 CPU 读回路径（captureMs=42ms 的元凶），说明零拷贝分支没跑起来。
2. **可能卡点**（待查 agent 日志确认）：
   - VideoProcessor（ID3D11VideoDevice）在 RDP 会话内不可用 → 静默降级 CPU；
   - 跨设备 Shared 句柄拿不到 → fall through CPU；
   - 零拷贝分支的 gpuConverter 创建失败。
3. **关键诊断入口**：agent 日志里应有 "[video] 零拷贝色差 PSNR(Y) = XX dB"（成功）或 "[video] 零拷贝失败，降级 CPU：xxx"（失败）或 "[video] GPU 转换器不可用"（VideoProcessor 不可用）。

### 2.3 为什么日志没生成

当前 agent 78240 起来后没写 relay 日志（soullink-relay-78240.log 不存在）。**这是下一步排查的第一线索**：要么 viewer 没触发，要么日志机制在零拷贝改动后异常。需在真机上「点击观看」触发一次，再看日志。

---

## 3. 环境与铁律（沿用，不变）

### 运行进程（2026-11-24 01:20）

```
SoulLink-Stable.exe  pid=13116  session=1  diststable   <- 挂机测试版【禁碰】
SoulLink.exe         pid=56568  session=0  --broker     <- Broker 服务
SoulLink.exe         pid=102172 session=1  dist        <- 主程序（3.3.34）
SoulLink.Agent.exe   pid=78240  session=4  Program Files <- 测试 agent（3.3.34，DDA 后端）
```

### 铁律（逐条有事故背景）

1. **绝不碰 dist/stable/SoulLink-Stable.exe**（挂机测试版）
2. **不用 A1/A4 做输入控制**（挂机测试账号，只读抓屏）
3. **不重启 session1 主程序**（断 RDP）——部署需重启时须用户授权
4. **停进程只用精确 PID**
5. **不发布、不碰 COS、不改 latest.json**
6. **A1/A4 账号不需要投送与代理**（挂机测试稳定性用，别动）

---

## 4. 协作约定与教训（本轮新增）

1. **性能优化必须先对照成熟项目数据流**：用户第一天就要求参考 Sunshine（个位数延迟），但团队绕了远路——A1/A2/A3 都在「CPU 读回→CPU转换→上传GPU」的冤枉路内部优化，没触及「数据该不该落 CPU」这个根本。教训：先问「数据在哪个设备之间流动」，再问「什么时机处理」。
2. **用户对架构方向的一手直觉要第一时间验证**：「显卡不能直接输出吗」直接点破 GPU 直通正解，但被当成随口一提。
3. **GPU 零拷贝这种底层改造，必须先真机验证再部署**：build 通过就部署，导致「WGC 不出画面」「DDA 零拷贝未生效」两个回归。
4. **诊断会话号/连接状态以主界面实时值为准**，不要翻历史日志旧值（曾误判「会话号跳变 48→46→47」，实际主界面一直 4）。
5. **成员并发改同一文件必须协调**：GPU 零拷贝和首帧修复都改 SessionRecorder.cs，靠「先方案后实现 + 分文件 owner + 串行提交」避免覆盖。
6. **每次 commit 必 push**（用户约定）。
7. **代码改了必须部署到真机才算闭环**。

---

## 5. 下一步（按优先级，不要继续派任务，等用户决定）

### P0：查 GPU 零拷贝为什么没生效（延迟 444 的根因）

- 真机「点击观看」触发一次，看 agent 日志：
  - 有 "[video] 零拷贝色差 PSNR" → 零拷贝生效了，延迟没降是别的原因（浏览器端？）；
  - 有 "[video] 零拷贝失败，降级 CPU：xxx" → 卡在 xxx（VideoProcessor / Shared 句柄 / 其他）；
  - 有 "[video] GPU 转换器不可用" → VideoProcessor 在 RDP 内不可用，需换「同 device 路线A」（Sunshine 做法：DDA duplication 直接建在 NVENC device 上，免跨设备共享）。
- engineer-ds2 的 DDA-TEXTURE-SHARED-HANDLE 研究已指出「路线A 同 device 免共享」是更优解，可作为下一轮方向。

### P1：WGC 后端验证（c323239 空转修复后）

- WGC 不出画面是空转 bug（已修 c323239），需真机验证 WGC 现在能不能出画面。

### P2：agent 坐标换算基准修复（落点偏移的 agent 侧半）

- 网页端 norm 已修（91fc288），但 agent 侧 AgentWebServer.cs:483-486 仍用 GetSystemMetrics（主屏尺寸）做换算，应改为 SessionRecorder 实际抓屏尺寸（_width/_height）。需给 SessionRecorder 加公开访问器（等零拷贝稳定后再做）。

### P3：安卓 App 真机联调

- 脚手架 + 播放器核心 + 输入接入已提交（assembleDebug 通过），可出 APK 在真机（骁龙8GEN3）联调，对标月光 5-12ms。

### P4：可选后续

- GPU 零拷贝「路线A 同 device」（免跨设备共享，Sunshine 做法）
- 完整 GPU 零拷贝（WGC 纹理 → GPU 着色器转 NV12 → NVENC，免 VideoProcessor 依赖）

---

## 6. 关键研究文档索引（本轮新增，都在 docs/research/）

- CURSOR-AND-POINTER-LOCK-20261124.md —— 吞点击死循环 + 落点偏移 + 可见光标方案
- FIRST-FRAME-25S-DIAGNOSIS-20261124.md —— 首帧 25s 死锁（确定性死锁）
- GPU-ZERO-COPY-IMPL-PLAN-20261124.md —— GPU 零拷贝实现方案
- MATURE-GPU-ZERO-COPY-EXPERIENCE-20261124.md —— Sunshine/OBS/月光 经验汇总
- DDA-TEXTURE-SHARED-HANDLE-20261124.md —— DDA 跨设备共享句柄研究
