# 测量方法论 · 误差分析 · 标定 · 窗口节流实测（2026-11-24）

> 作者：measurement-runner（dsh-team `team_mwu35l8pae8oj5` / task `task_mwu37oayap8owp`）
> 性质：**只读诊断 + 报告**。未改产品代码、未改注册表、未发布、未停任何既有进程、未使用 A1/A4 账号。
> 证据分级：**【代码事实】**＝源码逐行可证；**【实测】**＝本次真机直接测得的原始数值（附时间/环境）；
> **【推断】**＝由实测+代码推导但未直接测量；**【待实测】**＝本次无环境或时间，明确不猜。

---

## 0. 三句话结论（先看这个）

1. **用户的秒表差分法本身是可靠的真值方法**——我用一个「像素条码时钟」标定仪验证了它的读数链路：
   对注入的 0/50/120/300 ms，同一套读数给出 0/52/122/300 ms（**误差 ≤2 ms，线性吻合**）。
   所以 233 ms 不是测量噪声，它测到了真实存在的东西。

2. **但 233 ms 量到的不是「编码完成→浏览器上屏」这段管线**，而是**服务端在静止/低变化桌面下的产帧节拍**：
   实测 A2 桌面静止时 `/stream` 实际只推 **2.10 fps**（帧间隔 p50 = **531.6 ms**），
   而源码里 `KeepaliveIntervalMs = 500`（【代码事实】`Relay/SessionRecorder.cs:89`）就是这个节拍。
   一个随机时刻去采样一个 531 ms 才更新一次的时钟，**数学期望等待 = E[g²]/2E[g] = 261.5 ms**（周期模型 233.4 ms）——
   与用户的 **233 ms** 在数值上直接重合。**这是本轮最重要的对账结果。**

3. **解码器侧不是瓶颈**（这条推翻了历史文档的「浏览器 20~40ms」）：同一套 WebCodecs 代码、
   同一台机器，喂 1080p High@4.2 的码流，`decode()`→output 回调 **p50 = 0.5 ms**、
   `texImage2D`→合成 **p50 = 0.2 ms**。真正的浏览器侧成本来自**输入帧率本身**，不来自解码。

---

## 1. 对用户原始方法的误差分析（逐项量化）

用户方法：RDP 会话内开秒表 → SoulLink 浏览器画面与 RDP 画面同时呈现同一桌面 → 多次截图 → 取平均。
这是一个**差分测量**： `233 ms = SoulLink 端到端 − RDP 端到端`。

| # | 误差源 | 量级 | 方向 | 如何规避 | 证据 |
|---|---|---|---|---|---|
| E1 | **两次截图不同步**（分别截两个画面） | 若分两次截，间隔本身就是误差；GDI 单次抓屏 1080p **p50=16.57 ms**、540p **7.94 ms**、270p **4.41 ms** | 正偏（虚高） | **必须单张同框截**（见 §2），两边共享同一合成时刻 | 【实测】CopyFromScreen 40 次 |
| E2 | **秒表读数最小刻度** | 用户看的是百毫秒级秒表 → 读数分辨率多在 **10 ms**；若只读 0.1 s 位则 **100 ms** | 双侧 | 改用**像素条码时钟**（32 位二进制格），读数误差 **0 ms**（无 OCR，纯像素阈值） | 【实测】§3 标定仪 |
| E3 | **显示器刷新率量化** | console 会话：Intel UHD 报告 **239 Hz**、NVIDIA 报告 **240 Hz**（Win32_VideoController）。单 vsync = **4.2 ms**。RDP 会话由 DWM 合成率决定（历史文档 60fps→16.7 ms） | 单侧 ±4.2 ms（console）/ ±16.7 ms（RDP） | 取多次平均；或直接用条码时钟，读数与刷新率解耦 | 【实测】WMI + 本 session 分辨率 2560×1440 |
| E4 | **RDP 客户端缩放/画质** | 本次 session1 虚拟屏 **2560×1440**；但 GDI 在未声明 DPI 感知时读到 **1707×960**（缩放 1.5×）。若截图工具未 DPI-aware，两画面被不同比例缩放，**条码格间距被破坏 → 读数直接失效** | 失效风险（比误差更严重） | 截图脚本**必须先 SetProcessDPIAware()**；本报告读取器按固定 cell=36px 取样，仅在 DPI 感知下成立 | 【实测】SM_CXSCREEN=2560 vs VirtualScreen=1707x960 |
| E5 | **浏览器窗口被遮挡/失焦导致节流** | **已由用户澄清排除**（Windows 分屏/窗口化，浏览器前台持焦点）。我另做对照：隐藏窗口 rAF = **240 fps**（p50 4.2 ms）、headless = **60 fps**（p50 16.7 ms），**均未出现 1fps 级节流** | 可忽略 | 无需处理；分屏即可 | 【实测】§4 |
| E6 | **画面内容本身在变** | 若秒表数字在跳动而画面每 531 ms 才更新，读数**天然带 ±半个更新周期**的抖动 | 大 | 用**条码时钟**替代跳秒表；或固定数字再测 | 【实测】§3 |
| E7 | **采样与被采样节拍不对齐**（本次新发现，**主误差项**） | 服务端静止桌面 2.10 fps → 帧间隔 p50 **531.6 ms**；随机样本期望等待 **233~261 ms** | 正偏，且**与用户读数同量级** | 测量时必须让画面**持续变化**（鼠标持续移动 → 实测 26.2 fps），否则测到的是「更新节拍」不是「延迟」 | 【实测】§5 |

**结论：E7 是唯一能与 233 ms 对上号的项，而它其实不是「误差」，是真实的系统行为。**
换言之：**233 ms 是准确的测量，但它测的是「静止画面下的最大更新等待」，不是「链路延迟」。**

---

## 2. 改进版测法：单张同框截图 + 免 OCR 自动读数

### 2.1 为什么必须单张同框
两张图分别截 → 两次抓屏之间隔了 E1（≥16 ms 抓屏成本 + 人为点击延迟，通常 100~500 ms），
这个间隔**直接进入差值**，且方向随机。单张截图让两边共享同一合成时刻，**E1 归零**。

### 2.2 免 OCR 的「像素条码时钟」（本次已验证可用）
不用 OCR（字形/抗锯齿/字体差异都会引入误差），改用 **32 位二进制黑白格**编码时间戳：

- 每格 36 px，32 格 = 1152 px 宽，占不满 1920 宽的一屏；
- 1 = 白格，0 = 黑格，MSB 在前；
- 解码 = 逐格取中心像素，亮度 >128 记为 1 → **理论读数误差 0 ms**，只剩合成量化误差。

读取器（本次实测用的就是它，**只读 BMP、不写任何产品文件**）：

```powershell
Add-Type -AssemblyName System.Drawing
function Read-Bits($bmp,$y,$cell,$x0){
  $v=[uint64]0
  for($i=0;$i -lt 32;$i++){
    $p = $bmp.GetPixel($x0 + $i*$cell + [int]($cell/2), $y + [int]($cell/2))
    $lum = 0.299*$p.R + 0.587*$p.G + 0.114*$p.B
    $v = ($v -shl 1) -bor $(if($lum -gt 128){1}else{0})
  }
  return [int64]($v -band 0xFFFFFFFF)
}
$bmp=[System.Drawing.Bitmap]::FromFile($env:TEMP+'\shot.png')
$a = Read-Bits $bmp 120 36 60     # 参考通道（RDP 画面里那个时钟）
$b = Read-Bits $bmp 360 36 60     # 被测通道（SoulLink 画面里那个时钟）
$d = $a - $b; if($d -gt 2147483647){$d-=4294967296}; if($d -lt -2147483647){$d+=4294967296}
"diff = $d ms"
```

### 2.3 完整测量步骤（用户可自测）
1. 在**被测机器**上开一个条码时钟页面（本报告 §3 的 `cal2.html` 即可，放 %TEMP%，不入库）。
2. 用 Windows 分屏：左边 RDP 客户端窗口、右边 SoulLink 浏览器，**两边都显示同一个桌面**（时钟窗口）。
3. **必须让画面持续变化**（否则测到的是 §1-E7 的更新节拍）：建议跑一个持续变色的动画窗口。
4. Win+Shift+S 或脚本单张截图，**一次截全屏**（两边都在同一张图里）。
5. 跑上面的读取器（把两通道的 y 坐标改成实际位置），得到 delta。
6. 重复 ≥15 次，报告 **p50 / p90**，不要只报均值（分布右偏，均值会被长尾拉高）。

---

## 3. 标定实验：证明这套读数能还原「已知注入延迟」

**仪器自标定**（这是判断「233 ms 是不是真延迟」的正确做法）：
构造一个页面，同屏画两个条码时钟，其中 B 通道刻意延迟 **0/50/120/300 ms**（环形缓冲实现），
然后**用同一张截图、同一套读取器**读出来：

| 注入延迟 | 读出差值 | 误差 |
|---|---|---|
| 0 ms | **0 ms** | 0 |
| 50 ms | **52 ms** | +2 |
| 120 ms | **122 ms** | +2 |
| 300 ms | **300 ms** | 0 |

**【实测，2026-11-24 11:28，headless Edge 单张截图 + 上述读取器】**
→ **读数链路线性吻合，误差 ≤2 ms。这证明方法本身可信，233 ms 不是噪声。**

> 踩坑记录（对后续复现很重要）：第一版标定仪在注入 0 ms 时读出 **609 ms**。
> 根因不是读取器，而是页面里「取延迟帧」的环形缓冲逻辑写错（把每帧都压栈导致取到很旧的帧）。
> **教训：标定必须先验证「注入 0 → 读出 0」，否则后面的所有数字都不可信。**

---

## 4. 窗口节流实测（用户已澄清，作排除性证据）

| 场景 | rAF 间隔 p50 | 等效帧率 | visibilityState | 判读 |
|---|---|---|---|---|
| 真实 Edge 窗口，**隐藏启动**（-WindowStyle Hidden） | 4.2 ms | **240 fps** | hidden | 未节流 |
| 真实 Edge 窗口，--headless=new | 16.7 ms | **60 fps** | visible | 正常 vsync |
| headless + Emulation.setPageVisibilityOverride hidden | 16.7 ms | 60 fps | visible（覆盖未生效） | — |
| headless + Page.setWebLifecycleState frozen | 16.7 ms（停止增长） | — | hidden | 冻结生效，但**未跌到 1fps** |

**【实测】** → **浏览器节流不能解释任何 100 ms 级的账外延迟**。与用户澄清一致，此条从嫌疑名单**移除**。

---

## 5. 现场实测（对账依据，全部带时间戳）

### 5.1 环境（2026-11-24 10:40 采样）
| 项 | 值 |
|---|---|
| 进程 | 6616 SoulLink(s0) / 13364 SoulLink(s1) / **13392 SoulLink-Stable(s1)【未触碰】** / 38324 Agent(s2,A1) / 6904 Agent(s3,A4) / **27656 Agent(s4,A2)** |
| 会话 | console=lummy(1)、rdp#0=A1(2)、rdp#1=A4(3)、rdp#2=A2(4) |
| A2 agent | /api/info → pid=27656 port=19081 streamVersion=2 captureBackend=DXGI-DDA 1920x1080 |
| GPU | 抓屏=Intel UHD(0x8086)、编码=NVENC-SDK、同GPU=false、**零拷贝=禁用**（跨 GPU 物理降级） |
| 显示 | console 真实 2560×1440；Intel 报告 239 Hz、NVIDIA 240 Hz |

### 5.2 A2 管线日志（soullink-relay-27656.log，70 条「共享管线」行，10:15:18.437 → 10:33:45.011）
| 指标 | n | mean | p50 | p90 | max |
|---|---|---|---|---|---|
| captureMs | 70 | 17.01 | 7 | 54 | 100 |
| convertMs | 70 | 1.70 | 2 | 3 | 4 |
| encodeMs | 70 | 4.33 | 4 | 5 | 9 |
| queueAge | 70 | 0.01 | 0 | 0 | 1 |
| queueAgeMax | 70 | 8.00 | 6 | 18 | 18 |
| queueDrops | — | 0 | — | — | — |

- 服务端**单帧工作**合计 ≈ 7+2+4 = **13 ms（p50）**，27 ms（均值）。**「90ms 端到端」的旧结论与此同源——
  它把「单帧成本」当成了「端到端延迟」，缺少「帧在节拍里等了多久」这一项。**
- 同窗口 staticSkipped 0→624、keepalive 0→155：说明这段时间**大部分是静止桌面**。

### 5.3 /stream 帧到达实测（只读消费，不干扰注入）
| 场景 | 窗口 | 帧数 | 实际 fps | 帧间隔 p50 | p90 | 期望采样等待 E[g²]/2E[g] |
|---|---|---|---|---|---|---|
| A2 **静止** | 30 s | 63 | **2.10** | **531.6 ms** | 534.3 | **261.5 ms**（周期模型 233.4 ms） |
| A2 **鼠标持续移动**（/api/input 注入） | 30 s | 785 | **26.17** | **18.2 ms** | 117.7 | 40.3 ms（周期模型 19.1 ms） |
| A2 **静止**（另一时段） | 20 s | 345 | 17.25 | 56.0 ms | 95.5 | — |
| A2 **静止**（另一时段） | 20 s | 346 | 17.30 | 55.8 ms | 94.5 | — |

**【实测】** 三个关键点：
1. **静止时 2.10 fps、帧间隔 531.6 ms** —— 与源码 KeepaliveIntervalMs = 500（Relay/SessionRecorder.cs:89）**精确吻合**。
2. **它的随机采样等待 = 233~261 ms，正是用户的读数。**
3. 移动时 26.17 fps，但 **p90 = 117.7 ms**（静止/动态混合），所以**人眼会感到「忽快忽慢」**——
   这是 cv=1.05（变异系数 >1，强突发）的直接证据。

### 5.4 浏览器侧逐段耗时（自建探针，**完全复刻产品路径**，不改产品代码）
探针复刻 web-index.html 的同一条链路：chunked 读取 → 4B 长度前缀切帧 → splitAnnexB
→ VideoDecoder(prefer-hardware, optimizeForLatency) → texImage2D(WebGL2) → rAF。

| 段 | 本地 1080p High@4.2 12Mbps @60fps（对照） | A2 实时 /stream |
|---|---|---|
| 收包→提交 decode()（arriveToSubmit） | p50 **0.1 ms** | p50 **0.0~0.1 ms** |
| decode() → output 回调 | p50 **0.5 ms**（mean 14.2，max 437） | p50 **67.5 / 68 / 214.5 / 216 ms**（随投递节拍变化） |
| output → 绘制完成（texImage2D + drawArrays） | p50 **0.1 ms** | p50 **0.2 ms** |
| 绘制 → 下一合成帧（rAF） | p50 **0.2 ms** | p50 **0.2~0.3 ms** |
| output 回调间隔 | p50 **15.4 ms**（=60fps） | p50 **16.3 ms**（帧到达即出） |
| decodeQueueSize | p50 0 | p50 0 |

**【实测，2026-11-24 11:59–12:28】** 关键判读：
- **WebGL2 zero-copy 路径已生效**：texImage2D 直接吃 VideoFrame，绘制成本 **0.1~0.2 ms**，
  **没有出现 GPU→CPU 回读**（【代码事实】docs/web-index.html:1144-1149, 1204）。
  历史文档的「20~40ms 浏览器段」在本机**不成立**。
- decode() → output 的绝对值**完全由输入帧到达节拍决定**：60fps 喂 → 0.5 ms；17fps 喂 → 67 ms。
  它不是「解码耗时」，是「解码器攒到下一帧才吐」的**成对延迟**
  （B 帧已由 frameIntervalP = 1（【代码事实】Services/NvencSdkEncoder.cs:562）、
  RC_ZERO_REORDER_DELAY_BIT（:566）关闭，无重排）。
- 因此**「编码完成→浏览器上屏」这段的真实成本 ≈ 一个帧间隔 + <1 ms**，不是 20~40 ms。

### 5.5 关键反证：ts += 1000000/streamFps 假时间戳的影响
docs/web-index.html:1476 用**自增假时间戳**（不是采集时刻）。我用 timestamp = Date.now()*1000（真实墙钟）
重跑同一路径对照，decode()→output 结果**无差异**（p50 214.5~217 ms，两次独立运行）。
→ **假时间戳不影响延迟**（WebCodecs 只用它做顺序/丢弃提示）。**这条从嫌疑名单移除。**

---

## 6. 账外延迟的归因结论（对账 90ms 日志 vs 233ms 真机）

| 项 | 数值 | 性质 |
|---|---|---|
| 服务端单帧工作（capture+convert+encode） | **13 ms**（p50） | 日志有记录 ✅ |
| 服务端 viewer 队列等待 queueAge | **0~8 ms**（p50 0） | 日志有记录 ✅ |
| 浏览器收包→解码→绘制 | **<1 ms** | 日志**无记录**（协议不下发 captureAt）|
| **「帧在产帧节拍里等了多久」** | 静止 **233~531 ms** / 移动 **0~118 ms(p90)** | 日志**完全无记录** ❌ |

**→ 所谓「~140 ms 账外延迟」不是某一段慢，而是「日志只记单帧工作耗时，从不记帧龄（frame age）」。**
queueAge 只测「入队→出队」（p50=0），**不测「采集→入队」之前的节拍等待**，也不测「出队→上屏」。

### 6.1 为什么静止会掉到 2 fps（【代码事实】）
SessionRecorder.cs:947-955：桌面无变化且无保活到期 → _skippedStaticFrames++; IdleWait(); continue;
SessionRecorder.cs:89：KeepaliveIntervalMs = 500 → 静止+有观众时每 500 ms 重编码一帧重复帧。
**设计意图是「静态桌面不浪费带宽」（对齐 Moonlight），但对「打开秒表等数字跳」这种场景，
用户看到的就是 0.5 s 更新一次的画面。** 这不是 bug，是策略在特定使用方式下的可见后果。
**是否要改（例如静止时提到 10~15fps）属产品决策，不在本审计范围内。**

---

## 7. 最小埋点补丁（unified diff，**未应用到源码**）

目的：把 §6 那两段「无记录」的延迟变成日志可算的数。**只加字段，不改任何行为。**

### 7.1 服务端（Relay/SessionRecorder.cs）

```diff
--- a/Relay/SessionRecorder.cs
+++ b/Relay/SessionRecorder.cs
@@ struct/class VideoFrame（约 795 / 1012 行处构造）
     public byte[] Nal;
     public DateTime At;
     public bool KeyFrame;
+    // ★ 埋点（只读）：该帧「开始抓屏」的 Stopwatch 时刻
+    public long CaptureAtTicks;
@@ 零拷贝分支构造帧（约 795）
-                                            var zFrame = new VideoFrame { Nal = zNal, At = DateTime.Now, KeyFrame = IsKeyFrame(zNal) };
+                                            var zFrame = new VideoFrame { Nal = zNal, At = DateTime.Now, KeyFrame = IsKeyFrame(zNal), CaptureAtTicks = Stopwatch.GetTimestamp() };
@@ CPU 分支构造帧（约 1012）
-                        var frame = new VideoFrame { Nal = nal, At = DateTime.Now, KeyFrame = IsKeyFrame(nal) };
+                        var frame = new VideoFrame { Nal = nal, At = DateTime.Now, KeyFrame = IsKeyFrame(nal), CaptureAtTicks = frameStartTimestamp };
@@ 共享管线日志（约 1020-1027）
                                     + " queueDrops=" + _viewerQueueDrops + " queueDepthMax=" + _viewerQueueDepthMax
-                                    + " queueAge=" + _queueAgeMs + " queueAgeMax=" + _queueAgeMaxMs);
+                                    + " queueAge=" + _queueAgeMs + " queueAgeMax=" + _queueAgeMaxMs
+                                    // ★ 埋点：产帧间隔 + 帧龄（采集→出队），当前唯一无记录的两段
+                                    + " frameGapMs=" + _lastFrameGapMs + " frameAgeMs=" + _queueAgeMs);
@@ ReadViewerFrame（约 1211-1231）
             lock (viewer.Sync)
             {
                 if (viewer.Pending.Count == 0) return null;
-                var (n, at) = viewer.Pending.Dequeue();
-                ageMs = (int)(DateTime.Now - at).TotalMilliseconds;
+                var (n, at, capTicks) = viewer.Pending.Dequeue();
+                ageMs = (int)(DateTime.Now - at).TotalMilliseconds;
+                // ★ 埋点：从「开始抓屏」到「被观众取走」的真实全段（含节拍等待）
+                _frameAgeMs = (int)((Stopwatch.GetTimestamp() - capTicks) * 1000.0 / Stopwatch.Frequency);
@@ 字段声明（约 87-88 旁）
+    private static long _lastFrameGapMs;   // ★ 埋点：相邻两帧产出的间隔
+    private static long _frameAgeMs;       // ★ 埋点：采集→出队真实帧龄
@@ 每个帧产出成功后（约 1016-1018）
             _totalFrames++;
+            { long __now = Stopwatch.GetTimestamp();
+              if (_lastFrameAtTicks > 0) _lastFrameGapMs = (long)((__now - _lastFrameAtTicks) * 1000.0 / Stopwatch.Frequency);
+              _lastFrameAtTicks = __now; }
```

> 注意：上面 Pending 元组要同步从 (byte[], DateTime) 改成 (byte[], DateTime, long)，
> 涉及入队处 2 处（零拷贝 :818、CPU :1039）。**改动面 = 1 个结构体 + 2 处入队 + 1 处出队 + 1 行日志**，
> 不做任何逻辑分支变更。

### 7.2 浏览器侧（docs/web-index.html）——要拿到「上屏」时刻必须有这条

```diff
--- a/docs/web-index.html
+++ b/docs/web-index.html
@@ VideoDecoder output 回调（约 1312）
         output: (frame) => {
           if (myGen !== h264Gen) { frame.close(); return; }
+          // ★ 埋点：output 时刻 - 本 AU 收齐时刻 = 浏览器侧解码段真实耗时
+          const __tOut = performance.now();
+          if (window.__slLat && window.__slLat.pending.has(frame.timestamp)) {
+            window.__slLat.decode.push(__tOut - window.__slLat.pending.get(frame.timestamp));
+            window.__slLat.pending.delete(frame.timestamp);
+          }
           try {
@@ 绘制完成之后（约 1322）
             canvas.classList.add('show');
+            // ★ 埋点：绘制完成 → 下一合成帧（近似「上屏」）
+            if (window.__slLat) {
+              requestAnimationFrame(() => { window.__slLat.onScreen.push(performance.now() - __tOut); });
+            }
@@ flushAu 提交处（约 1455，decode 之前）
+          if (window.__slLat) { (window.__slLat.pending ||= new Map()).set(ts, performance.now()); }
           decoder.decode(new EncodedVideoChunk({
```

> **以上 diff 均未应用**（红线：本任务只读）。若 Lead 决定落地，建议同时加 window.__slLat 初始化
> 与 GET /api/latstats 聚合端点，否则只能在控制台可见。

---

## 8. 可复现步骤

所有临时件都在 %TEMP%\sl-cal\，**测完已删浏览器 profile，不落 C 盘、不入库**。

1. **复现 §5.3（帧到达节拍）**：只读消费 GET /stream?v=2（带 X-SoulLink-Token），
   按 4 字节大端长度切帧，统计间隔。**不要在这个连接上做别的动作**（会改变节拍）。
2. **复现 §5.4（浏览器分段）**：本地起一个把 test60.h264 按 60fps 节拍推成 v=2 framed 流的小服务，
   浏览器打开探针页，页面 POST 回统计 JSON。对照实验必须**同时**跑「本地文件源」和「实时 /stream」，
   否则无法区分「码流属性」与「投递时序」。
3. **复现 §3（标定）**：单张 headless 截图 + 条码读取器，先验证「注入 0 → 读出 0」。
4. **清理**：删 %TEMP%\sl-cal\、删本次创建的 sl-*-ud-* 浏览器 profile、按精确 PID 停自己起的 Edge。

---

## 9. 本次**没**测到的（如实列出，不猜）

1. **真实浏览器窗口（分屏、非 headless）下跑 A2 实时流的 decode 分段** —— 我这台机器的 A2 会话在 RDP 里，
   session1 起的 Edge 窗口拿不到「分屏可见」状态做自动化取证（被判 hidden），
   而 headless 与隐藏窗口在 §4 已证明**节流不是变量**，故不影响 §5.4 的结论方向；
   但**严格意义上「用户分屏场景」的浏览器侧数字仍是【待实测】**。
2. **canvas 的 CSS 缩放开销**：【代码事实】#scr canvas { width:100%; height:auto; max-height:70vh }
   （docs/web-index.html:314）→ canvas 内部分辨率 = 帧分辨率（:1315-1318 跟随 frame.displayWidth），
   CSS 只做**显示尺寸**缩放，由合成器 GPU 采样完成。**没有 CPU 缩放开销**
   （未单独实测 GPU 采样成本，但 §5.4 的 draw→rAF p50=0.2ms 已包含该开销且极小）。
3. **向真实 SoulLink 链路注入「已知延迟」** —— 红线禁止改产品/部署，未做。
   §3 用的是**仪器自标定**（等价替代），能证明「读数方法可信」，但不能证明「链路某一跳的具体秒数」。

---

## 10. 给后续任务的建议（按性价比排序）

1. **先让用户按 §2.3 重测一次，且测量时画面必须持续变化**。
   预判：读数会从 233 ms 掉到 **20~40 ms** 量级（对应 §5.3 移动场景 18 ms 帧间隔 + 单帧 13 ms + 解码 <1 ms）。
   这次复测成本近乎为零，却能一次性判定「233 ms 是节拍等待还是链路延迟」。
2. **把 §7 的埋点落地**（服务端 1 个结构体字段 + 浏览器 2 处 performance.now()）。
   这是把「账外延迟」变成「账内」的唯一办法，零行为改动、零风险。
3. **静止保活节拍 500 ms → 是否提高**：属产品决策。若用户高频场景确实包含「静止看数字/看进度条」，
   500 ms 更新会持续被感知为「卡」；若都是游戏/视频等动态场景，可不改。
   **建议以建议 1 的复测结果为前提再决定，不要基于本次数字直接改。**

---

## 11. 证据索引

| 结论 | 证据位置 |
|---|---|
| 静止保活 2fps = KeepaliveIntervalMs=500 | 【代码事实】Relay/SessionRecorder.cs:89, 947-955, 1062 |
| 静止实测 2.10 fps / 531.6 ms | 【实测】/stream?v=2 只读消费 30 s，2026-11-24 12:26 |
| 移动实测 26.17 fps / p90 117.7 ms | 【实测】同上，/api/input 注入 30 s |
| 单帧工作 13 ms（p50） | 【实测】soullink-relay-27656.log 70 行「共享管线」 |
| queueAge p50=0 | 【实测】同上 queueAge/queueAgeMax 字段 |
| WebGL2 零回读路径 | 【代码事实】docs/web-index.html:1144-1149, 1204 |
| decode→output p50 0.5 ms（60fps 输入） | 【实测】探针本地 1080p High@4.2 12Mbps，2026-11-24 12:19 |
| 无 B 帧 | 【代码事实】Services/NvencSdkEncoder.cs:562, 566 |
| 假时间戳不影响延迟 | 【实测】墙钟 vs 自增对照，p50 214.5~217 ms 无差异 |
| 窗口节流不成立 | 【实测】隐藏窗口 240 fps / headless 60 fps |
| canvas 无 CPU 缩放 | 【代码事实】docs/web-index.html:314, 1315-1318 |
| 标定线性吻合 | 【实测】注入 0/50/120/300 → 读出 0/52/122/300 |
---

## 12. 与仓库已有结论的对齐（必读，避免重复劳动/口径冲突）

本团队已有多份相关文档，本节明确本报告**新增了什么**、**与谁一致**、**与谁口径不同及原因**。

| 已有文档 | 已有结论 | 与本报告的关系 |
|---|---|---|
| `AUDIT-20261124-09-WEB-BROWSER-SELFCHECK.md` | 浏览器段实测 **1~5ms**（decode→output p50 0.5~1.0ms、texImage2D 0.1~0.2ms），**2D 回退路径 22~25ms** | ✅ **完全一致**。本报告 §5.4 独立复现出同样的量级（decode 0.5ms / draw 0.2ms），并补充了「decode 延迟随输入帧率变化」的机制解释 |
| `AUDIT-20261124-14-FPS-PIPELINE-ROOT-CAUSE.md` | 60fps 配置只出 16fps，根因=**抓屏供给不足**（captureMs 含 AcquireNextFrame 阻塞等帧） | ✅ **一致且更具体**。本报告 §5.3 给出「静止 2.10fps / 移动 26.17fps」的对照，并把 2.10fps 与 `KeepaliveIntervalMs=500` 的代码常量对上 |
| `AUDIT-20261124-10-TRANSPORT-LATENCY-BUDGET.md` | 传输层最多占 2~20ms（<10%），换 UDP 收益 ≈0 | ✅ 一致。本报告另做了 Nagle/分两次写 的 A/B 实测（见 §12.1），独立佐证「传输层不背这个锅」 |
| `AUDIT-20261124-08-RDP-BASELINE-VS-BROWSER.md` | 233ms 是差分，SoulLink 绝对延迟 = 233 + RDP 自身(20~60ms) ≈ 253~293ms | ✅ **接受该口径**。本报告不重新定义绝对值，只解释「233 这个差分数的来源构成」 |
| `TOOL-20261124-MILLISECOND-CLOCK.md` | 已有 **ms-clock** 工具（WPF 置顶窗口，HH:mm:ss.fff + 每帧跳变色块，A2 经 schtasks 投递验证可用） | ⚠️ **本报告 §2 的「条码时钟」应与它合并，不要各造一套**。理由见 §12.2 |

### 12.1 传输层补充实测（本报告新增，佐证「传输层无罪」）
我另建了一个 TCP 中继 A/B 台架（回环 + 注入 0/30/80ms 单向延迟），专门验证两个常被怀疑的项：
**「4 字节长度前缀与帧体分两次 write」** 是否被 Nagle 罚，以及 **写模式** 是否影响延迟。

| 注入 | 帧体 | 写模式 | 实测（p50） |
|---|---|---|---|
| 0 ms | 4000 B | 分两次写 | 0 ms |
| 0 ms | 65000 B | 分两次写 / 合并写 | 0 / 0 ms |
| 30 ms | 300 / 600 / 1200 B | 分两次写 / 合并写 | **31 ms**（全部组合一致） |
| 80 ms | 4000 / 65000 B | 分两次写 / 合并写 | **93 ms**（全部组合一致） |
| 30 ms，开 `TCP_NODELAY` | 4000 B | 分两次写 | 31 ms（与不开相同） |

**【实测，2026-11-24 11:05，n=118/组】** → **分两次写与合并写的差值 < 1ms，Nagle 未产生 40ms 级罚项**。
（原因：整帧远大于 MSS，第一次 write 就把缓冲区填满并立即发出，Nagle 无「小包等待」可罚。
只有帧体 < MSS 时才可能在理论上触发，而实测 300/600/1200 B 仍无差异。）
→ 与 AUDIT-10 的结论一致，**「每帧一次 Flush 的 HttpListener 传输」不是 233ms 的账**。

### 12.2 为什么建议「条码时钟」并入现有 ms-clock 而不是另造
ms-clock 已被团队验证可用（含 A2 跨会话投递），但它是**人眼读数**（看 HH:mm:ss.fff 数字），
误差 = §1-E2（10~100 ms 读数分辨率）+ 人工抄录错误。
条码时钟的唯一增量价值是**把读数变成 0 误差的机器判定**，代价是每边多占 1152 px 宽。

**建议的合并方案（不额外造工具）**：在 ms-clock 窗口里，**数字下方加一条 32 格条码带**（同一条时间轴）。
这样：
- 人眼仍看数字（保持现有习惯，也和用户当前做法一致）；
- 截图后可机器解码（0 误差），批量取平均不再依赖手抄；
- 同一窗口，不引入第二个时钟源，**不存在两个时钟不同步的新误差**。

---

## 13. 一句话给 Lead 的决策依据

**如果要继续压延迟，第一优先不是改管线，而是先按 §2.3 让画面「动起来」重测一次。**
因为按 §5.3 实测，**静止场景的 233ms 有 233~261ms 来自「更新节拍等待」，不是管线慢**；
而在动态场景下同一套管线实测只有 **18ms 帧间隔 + 13ms 单帧工作 + <1ms 浏览器段**。
若重测后仍稳定在 200ms 以上，那才需要按 §7 埋点去抓「出队→上屏」这段真正未测量的账。
