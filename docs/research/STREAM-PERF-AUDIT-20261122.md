# 画面链路性能根因审计 + 最小成本远程控制内核设计

> 日期：2026-11-22
> 作者：architect（dsh-team `soullink-stream-opt` / task `task_mwrnfr1b6lyrq`）
> 范围：**只读分析 + 本产出文档**。未改任何业务代码、未动 dist/、未提交 git、未发版。
> 代码基线：`SoulLink.csproj` `<Version>3.3.19</Version>`。
> 配套必读：`HANDOFF-3.3.19-20261122.md`、`PERF-3.3.4-20261122.md`、`WEB-DIRECT-3.3.5-20261122.md`。

---

## 修订记录（Revision Log）

| 版本 | 日期 | 修订内容 |
|---|---|---|
| v1.0 | 2026-11-22 | 初版（task `task_mwrnfr1b6lyrq`） |
| v1.1 | 2026-11-22 | 评审修订（task `task_mwrnqfe01z3woh`）：①重估"无人即停"与录制的冲突 + 空闲信号跨进程接线成本（§4.3）；②澄清 13~16ms 是"BitBlt+JPEG 编码**一次**"合并值（`WebServer.cs:136-137`），非 BitBlt 单独耗时（§1.2/§2.2/§4.5）；③修正两处技术断言：`HttpListener` 默认 HTTP/1.1 keep-alive（非 `Connection: close`）、`HttpListenerContext.AcceptWebSocketAsync()` 原生可用（非需手写 101 握手）（§2.2/§4.4）；④所有无实测的量化数字降级为"估算"并标注来源/待测（§2.2）；⑤光标方案按三档区分（帧内绘制 / 本地 CSS 预测 / 独立通道）（§4.6） |

---

## 0. 一句话结论

当前"卡"的根因不是某一跳慢，而是**采集侧用 `BitBlt 全屏抓图 + GDI+ JPEG 软编`这个组合，在 30fps/质量75 下被"录制驱动"持续空转**；浏览器端又用 `<img>`/`fetch+Blob` **每帧全图重解码**。二者叠加，把"本该是零成本的静止画面"变成了**每账号每秒 4.8MB 网络（交接给定）+ 估算约一个核的常驻 CPU（待实测）**。要做到"像远程控制一样"的最小代价方案，**不是把 30fps 全屏 JPEG 优化得更快，而是换掉"全帧 JPEG 拉取"这个范式**：脏矩形 + 光标远端渲染 + 静止停止采集，三招即可在**不引入 WebRTC 的前提下**把带宽砍 10~50 倍、把服务端 CPU 降到近零、把交互延迟压到光标跟手。

---

## 1. 现状：一帧从"屏幕像素"到"浏览器显示"的完整链路

### 1.1 链路拓扑（谁在画面路径上）

```
[目标 RDP 会话内的代理进程 RelayAgent]
        │  SessionRecorder.Loop()  按 RecordFps 持续抓帧（录制驱动，与有无观看者无关）
        │
        ▼
  GetDC(NULL) ──► CreateCompatibleBitmap ──► BitBlt(SRCCOPY)  ──► DrawIconEx(光标)
        │          (7.9MB 位图 @1080p)                            (GDI 位图 hBmp)
        ▼
  Image.FromHbitmap(hBmp)  ──►  bmp.Save(ms, JPEG, quality)  ──►  ms.ToArray()
        │                          (GDI+ 软编 JPEG)                  (byte[] ~160KB)
        ▼
  Ring.Enqueue(Frame{Jpeg})   内存环形缓冲（只存压缩字节，30 秒 ≈ 37MB/账号）
        │
        ▼
  [代理进程内 AgentWebServer  :1080N]
        │  GET /live  → SessionRecorder.GetLatestJpeg() → OutputStream.WriteAsync(jpeg)
        │  (HttpListener，每请求 new Task.Run；HTTP/1.1 keep-alive，同源 fetch 复用连接)
        ▼
  [局域网 HTTP]  ← 浏览器 fetch(url, {cache:"no-store"}) 每帧一个独立请求
        │
        ▼
  fetch → r.blob() → URL.createObjectURL(b) → <img src=objUrl>
        │        浏览器 JPEG 解码（每帧全图）
        ▼
  <img> 光栅化合成 → 屏幕
```

### 1.2 逐跳列表（拷贝 / 编码 / 解码 / 落盘）

| 跳 | 动作 | 位置 | 内存拷贝 | 落盘 | 量化（1080p 单帧） |
|---|---|---|---|---|---|
| ① | **抓屏** `GetDC(NULL)` 拿屏幕 DC | `Relay/SessionRecorder.cs:321` | 无（取句柄） | 否 | ~0.1ms |
| ② | **建兼容位图** `CreateCompatibleBitmap(w,h)` | `SessionRecorder.cs:324` | 分配 **7.9MB** GDI 位图（BGRA 8.29MB） | 否 | 分配开销 |
| ③ | **全屏拷贝** `BitBlt(SRCCOPY)` | `SessionRecorder.cs:327` | 屏幕显存→系统内存 **7.9MB 拷贝** | 否 | **待测**（业界 1080p BitBlt 常 <5ms；"13~16ms"是 `WebServer.cs:136-137` 注释里"BitBlt+JPEG 编码一次"的**合并值**，不是 BitBlt 单独耗时） |
| ④ | **光标叠加** `DrawIconEx` | `SessionRecorder.cs:337`→`DrawCursorOn:308` | 局部小区域写入 | 否 | ~0.1ms（每帧还 GetIconInfo+DeleteObject 2 个句柄，已修泄漏） |
| ⑤ | **GDI 位图→托管位图** `Image.FromHbitmap(hBmp)` | `SessionRecorder.cs:339` | 又一份 **~7.9MB**（GDI→GDI+） | 否 | 数 ms |
| ⑥ | **JPEG 软编** `bmp.Save(ms, JPEG, q)` | `SessionRecorder.cs:343` | 编码在托管堆上产出压缩字节 | 否 | **估算** 10~40ms（依赖内容；GDI+ 单线程软编，未实测） |
| ⑦ | **ToArray 复制** `ms.ToArray()` | `SessionRecorder.cs:344` | 再拷一次 ~160KB（可优化掉） | 否 | ~0.05ms |
| ⑧ | **入环形缓冲** `Ring.Enqueue` | `SessionRecorder.cs:240` | 引用计数（byte[] 引用） | 否 | ~0 |
| ⑨ | **HTTP 取帧** `GetLatestJpeg()` 遍历找最后一帧 | `SessionRecorder.cs:374` | 引用 | 否 | O(n)，n=30s×fps≈900，遍历取尾帧可优化 |
| ⑩ | **socket 写出** `OutputStream.WriteAsync(jpeg)` | `AgentWebServer.cs:244` | 内核缓冲拷贝 ~160KB | 否 | 网络发送 |
| ⑪ | **网络传输** HTTP 响应 | — | TCP 分段 | 否 | 局域网 <1ms + 每帧 HTTP 头+握手开销 |
| ⑫ | **浏览器 fetch** `r.blob()` | `web-index.html:771` | 响应体→Blob | 否 | — |
| ⑬ | **createObjectURL** | `web-index.html:783` | Blob 引用 | 否 | — |
| ⑭ | **浏览器 JPEG 解码** `<img>` 光栅化 | `web-index.html:784` | 解码→纹理 | 否 | **全图重解码**（每帧完整 JPEG） |
| ⑮ | **合成上屏** | 浏览器 | 纹理上传 GPU | 否 | — |

> ★ **关键发现：整条链路上没有任何一处"帧间复用"**——每一帧都从 7.9MB 原始位图开始，全屏 BitBlt → 全图 JPEG 编 → 全图 JPEG 解。这是"静止画面也持续烧 CPU"的结构性根因（交接文档定性描述为"吃满一个核"，具体量级待 §2.3 实测）。

### 1.3 "后端服务"是什么、是否在画面路径上

- `Server/Program.cs` + `Server/Lobby.cs` 是**联机大厅服务端**（Ubuntu/5080 端口，SQLite），只做房间/座位/发车配置，**与实时画面、代理、录制完全无关**（`Program.cs:1-226` 全是 `/api/rooms`、`/api/depart-config`、`/api/heartbeat`）。
- 用户反馈的"引入后端服务后有点卡"，**不是** `Server/` 这个大厅在画面路径上造成的（它不在路径上），而是同一时期引入的**代理持续抓屏 + 网页直连取帧**这套链路带来的常驻开销。二者时间上撞在一起，但因果上要区分：`Server/` 只影响心跳（`LobbyConnection.cs:58` 每 10s 一次 DispatcherTimer），**不在画面热路径**。
- 主程序 `Services/WebServer.cs` 在直连开启后，实时画面**也不在路径上**（`/api/live` 只是旧转发兜底，`web-index.html:759` 直连时代理 `/live`）。主程序只提供 `/api/sessions` 映射表一次。

---

## 2. 卡顿的真实构成（CPU 五分解）

### 2.1 先分清两个正交维度（评审追问 1 的正面回答）

| 维度 | 谁驱动 | 何时发生 | 代码位置 | 无人观看时是否还在烧 |
|---|---|---|---|---|
| **采集侧开销** | 录制循环 | 只要账号在 `RecordAccountIds` 里、代理被下发过 `record` 命令，就**一直抓** | `SessionRecorder.Loop()` `SessionRecorder.cs:222-257` | **是** |
| **传输/解码侧开销** | 浏览器拉帧 | 只有浏览器打开 `?view=` 且未暂停/未隐藏 | `web-index.html:754-802`（`pullFrame`） | 否（页面隐藏即停，`document.hidden` 拦截） |

**结论（可直接引用）**：采集侧**不看有没有人观看**。`Start()` 之后 `Loop()` 是 `while(!_stopRequested)` 死循环，`SessionRecorder.cs:226`，每帧 `GrabJpeg` 一次，跟 `IsLiveActive()`（`WebServer.cs:165`，那个"10 秒没请求就算没人看"的标志）**没有任何接线**——`IsLiveActive`/`MarkLiveActivity` 定义了却没人消费（全仓 grep 只有定义和自身，无调用方驱动采集停止）。所以：

> **空闲时若账号仍处于"录制中"，采集侧依旧 30fps 全屏 BitBlt + JPEG 软编（估算约 0.3~0.7 核/账号，待实测）。** "按需取数"（HANDOFF §1.2）只省了**传输与浏览器解码**，没省**采集**。这是"引入后仍觉得卡"的一个独立、尚未被修掉的来源。

### 2.2 五份 CPU 量级（从代码能推出的估算）

> 基线环境：i9-13900HX，32 逻辑核；1080p；"30fps/质量75 ≈ 4.8MB/s" 是交接文档给定值。
>
> ★ **本节所有 CPU/带宽数字均为代码推导估算（未实测），不构成测量结论**。全文中可当作"实测"的只有三处来源：`WebServer.cs:136-137` 注释的"BitBlt+JPEG 一次 13~16ms"、`WebServer.cs:677` 注释的"relay 往返 45~64ms"、`WebServer.cs:1000` 注释的"relay 往返约 3ms"。其余一律是估算，待 §2.3 实测后才能定值。

| 分项 | 量级（每账号、录制中） | 依据 | 备注 |
|---|---|---|---|
| **抓屏（GDI BitBlt）** | **估算** ~5~15% 单核 | `SessionRecorder.cs:327`。★ 13~16ms 是 `WebServer.cs:136-137` 注释里"BitBlt + JPEG 编码**一次**"的**合并值**，**不是 BitBlt 单独耗时**；BitBlt 单独通常更便宜（待测）。30fps×16ms(合并) ≈ 0.5 核 | `CreateCompatibleBitmap` 每帧重新分配 7.9MB（`SessionRecorder.cs:324`）是隐藏大头——**位图应复用** |
| **JPEG 编码** | **估算** 20~60% 单核（动态内容时更高，未实测） | `SessionRecorder.cs:343` `bmp.Save(ms, JpegCodec(), ep)` 走 GDI+ 纯软编 | 这是**最大头**，且与画面内容强相关 |
| **网络传输** | ~0（本机/局域网，CPU 占用低） | `AgentWebServer.cs:244` | ★ `HttpListener` 默认 HTTP/1.1 **keep-alive**（`HttpListenerResponse.KeepAlive` 默认 true），同源 `fetch` 会**复用连接**，**不是每帧 TCP 握手/慢启动**；真实开销是每请求数百字节头 + 每请求一次 `Task.Run` 调度（`AgentWebServer.cs:121`），属**延迟抖动**而非吞吐型 CPU |
| **浏览器解码** | **估算** 约 1 个核（30fps 全图，未实测） | `web-index.html:784` `<img>` 每帧全图 JPEG 解码 | 与交接"吃满一个核"定性吻合；是**用户主观卡顿**的主要来源之一 |
| **UI 线程** | 空闲 ~12% 单核（与画面无关，实测于 PERF-3.3.4） | `PERF-3.3.4 §0`：UI 线程 + WPF 渲染线程 | 画面链路本身不在 WPF UI 线程（代理是独立进程），此份是**背景噪声**，与"监控画面卡"无直接因果，但会抢同一个物理核 |

**总账**：单个"录制中"账号，采集侧（抓屏+编码）≈ **估算 0.3~0.7 核（待实测）**；一个观看者再加浏览器解码 ≈ **估算 1 核（待实测）**。3 个账号全录 + 1 人观看 ≈ **估算 2~3 核常驻**，且这些核是"静止画面也在烧"。

### 2.3 可实际执行的测量方法（照做即可定位）

#### (A) 代理进程采集侧 CPU（PowerShell，注意 `Refresh()` 坑）

```powershell
# 找到录制中的代理进程（--relay）
Get-CimInstance Win32_Process -Filter "name='SoulLink.exe'" |
  Where-Object { $_.CommandLine -like '*--relay*' } | Select ProcessId, CommandLine

# 差分采样（★ 必须 Refresh，见 PERF-3.3.4 §1）
function Sample([int]$procId, $ms=3000) {
  $p = Get-Process -Id $procId; $p.Refresh()
  $a = $p.TotalProcessorTime.TotalMilliseconds
  Start-Sleep -Milliseconds $ms
  $p.Refresh(); $b = $p.TotalProcessorTime.TotalMilliseconds
  [math]::Round(($b - $a) / $ms * 100, 1)
}
# 交叉验证：进程总 CPU / 存活时长 作平均基线
```

#### (B) 打点定位（ETW / Stopwatch 位置，仅建议在**你自己的诊断分支**里加，不影响本审计的只读约束）

| 打点 | 位置 | 看什么 |
|---|---|---|
| `t1` BitBlt 前后 | `SessionRecorder.cs:327` 两侧 | 抓屏耗时（BitBlt **单独**，与 `t3` 的 JPEG 编码**分开**测，避免再混成"合并值"） |
| `t2` `Image.FromHbitmap` 前后 | `SessionRecorder.cs:339` | GDI→GDI+ 转换耗时 |
| `t3` `bmp.Save` 前后 | `SessionRecorder.cs:343` | **JPEG 编码耗时（大头）** |
| `t4` `GetLatestJpeg` 内 | `SessionRecorder.cs:374` | 取尾帧遍历是否随 Ring 增长变慢 |
| `t5` `OutputStream.WriteAsync` | `AgentWebServer.cs:244` | 写出与背压 |

用 `dotnet-trace`（本项目实测 CPU sampling 要显式 `--providers`）：

```powershell
dotnet-trace collect -p <代理PID> --providers Microsoft-DotNETCore-SampleProfiler --duration 00:00:15 -o sl.nettrace
dotnet-trace convert sl.nettrace --format Speedscope
```

#### (C) 浏览器侧

- **Performance 面板 → 录制 → 看 Main 线程火焰图**：`Decode` / `ImageDecoder` / `JPEG` 相关的 self time 占比；每帧一个 `fetch` → `createObjectURL` → `<img>` 的完整 task 时长。
- **Network 面板**：`/live` 请求的 **TTFB vs Content Download**；`Connection ID` 是否复用（`HttpListener` 默认 keep-alive，同源应复用）；若 TTFB 抖动大 → 服务端每请求 `Task.Run` 调度/取帧竞争；若 Download 稳定但总时长高 → 带宽/解码。
- **Performance → Rendering → FPS meter**：观察静止画面是否仍 ~30 次重绘（本应降到 1fps 的重绘）。
- 关键对照实验：**把 `selFps` 从 30 调到 10 再看浏览器 CPU**——若 CPU 线性下降，坐实"解码是瓶颈"。

---

## 3. 已有优化的边界（评审追问 2/3 的正面回答）

> 写本节是为了**不把已有优化重复计一次**，也让"新方案相对基线还能省多少"有明确锚点。

### 3.1 现状已存在、且计入基线的优化

| 已有优化 | 位置 | 效果 |
|---|---|---|
| **网页直连代理**（主程序不出流量） | `AgentWebServer.cs` + `web-index.html:759` | 砍掉"代理写盘→主程序轮询→主程序转发"这一跳（原最多 600ms 延迟） |
| **取数侧按需 + 串行调度** | `web-index.html:734-802` | `setTimeout` 串行，上一帧没回不发下一帧，请求不堆积 |
| **失败退避** 1s→2s→4s→8s | `web-index.html:728` | 连接失败时不空转 |
| **静止降频**（连续 6 帧字节数相同 → 1fps） | `web-index.html:729,778` | 静止时把**传输/解码**降到 1fps |
| **Move+Click 合并一次往返** | `RelayAgent.cs:419` `Move` 字段 + `AgentWebServer.cs:298` | 点击省一次 RTT（45~64ms） |
| **光标叠加进画面** | `SessionRecorder.cs:337` | 画面能看见鼠标 |
| **暂停/页面隐藏/换账号停拉** | `web-index.html:757` + `frameGen` | 停止传输与解码 |
| **内存环形缓冲、只存 JPEG** | `SessionRecorder.cs:131-247` | 避免持续落盘（388GB/天 → 0 写入） |

### 3.2 现状**不存在**、本审计新提出的（相对基线的增量）

| 新提出 | 相对基线还能省多少 | 对应候选 |
|---|---|---|
| 采集侧"无人观看实时分支降频"（录制分支保留） | 空闲时把实时采集分支 0.3~0.7 核 → 近 0（录制若仍需 30fps 则录制开销保留） | §4.3 / §5 P0 |
| 抓屏脏矩形/帧差，只编码变化区域 | 静止画面编码量 160KB → 几 KB，编码 CPU 减 90%+ | §4.1(a) |
| 光标远端渲染（画面不画光标，网页自绘） | 让画面可降到 5~10fps 而交互跟手，间接省 3~6 倍带宽+CPU | §4.6(f) |
| 更稳的静止判据（帧哈希/像素采样） | 避免误判导致的"该降没降/不该降降了" | §3.3 |
| 复用兼容位图、去掉 `ms.ToArray()` 二次拷贝、尾帧 O(1) | 采集侧常数级省几 ms/帧 | §4.1(a) |
| WebP/质量-分辨率自适应 | 同画质带宽再降 30%（相对 JPEG） | §4.2(b) |
| WebSocket 长连接（MJPEG-over-WS） | 去掉每帧 HTTP 头 + 建连抖动 | §4.4(d) |
| DXGI Desktop Duplication + MF H.264 硬编 | 抓屏省掉 BitBlt（待测值）+ 编码走 GPU，带宽再降一个数量级 | §4.5(e) |
| WebRTC（H.264 硬编 + NACK/PLI + 拥塞控制） | 带宽/延迟/交互全面最优，但工程量最大 | §4.7(c) |

### 3.3 静止判据可靠性评估（评审追问 2）

现状判据 `web-index.html:778`：`if (b.size === lastFrameBytes) stillStreak++`。

- **原理缺陷**：JPEG 字节数相等 ≠ 画面相同。暗色画面、轻微噪声、光标闪烁、时间戳/水印变化，都可能在**字节数恰好相等**时仍被误判为"静止"（该降频的没降，白烧 CPU）；反之，画面其实静止但 JPEG 编码的 padding/量化细节让字节数抖动 ±1，会导致 `stillStreak` 永远到不了 6，**该降频的没降**。二者方向都可能错。
- **实际后果**：误判"静止"→ 降频到 1fps → 用户看到本该流畅的画面一顿一顿（交互卡）；漏判"变化"→ 静止画面维持 30fps 全图解码（白烧 1 核）。
- **更稳替代（按实现成本递增）**：
  1. **帧哈希**：代理侧对 JPEG 前做**降采样像素校验和**（如 64×36 灰度的 CRC/累加和），存进 `Frame` 一并暴露给前端（或服务端直接比）。成本 ~0，可靠度大幅提升。
  2. **服务端脏矩形统计**：既然候选 (a) 已经要算脏矩形，顺带把"本帧变化像素占比"作为元数据下发，前端据此决定降频阈值——判据从"字节数"变成"真正动了多少像素"。
  3. 前端至少改一处：字节数相等**且**连续 N 帧时间戳正常推进（排除网络卡死导致的重复帧）才计 stillStreak。

---

## 4. 候选方案逐一评估

> 每个方案给：收益（带宽/CPU/延迟三方面量化）、实现量（人日）、风险、兼容度。

### 4.1 (a) 帧差 / 脏矩形编码（Dirty Rectangles）★ 收益/成本比最高

**做法**：`BitBlt` 前用 `GetUpdateRect`（对窗口）或**保留上一帧位图做逐块对比**（对全屏桌面），只把变化的矩形区域重编为 JPEG 发给前端，前端把新矩形贴到画布上（Canvas）。

- **收益**
  - 带宽：挂机/桌面类画面变化区域通常 <5%，静止帧 ≈ 0；**静态桌面带宽从 4.8MB/s → <0.5MB/s（10 倍+）**；游戏画面全屏变化时退化为全帧（无收益但有上限）。
  - CPU：编码只编变化块，**编码 CPU 降 90%+**；抓屏仍需全屏 BitBlt（BitBlt 本身不贵，贵的是编码），若配合"静止停抓"可进一步省抓屏。
  - 延迟：变化块小，单帧传输/解码更快 → 有效延迟下降。
- **实现量**：**3~5 人日**（代理侧块对比 + 前端 Canvas 贴块；复用兼容位图、去掉 ToArray 二次拷贝顺带做）。
- **风险**：中低。块拼接的边界、全屏变化时回退全帧、光标闪烁区域抖动的平滑，需处理；**不影响现有 `<img>` 单帧路径**（可做成"变化块 + 全帧兜底"双模式）。
- **兼容度**：高。可先在代理 `/live` 加 `/live?dirty=1` 返回变化块协议，前端渐进接入；`<img>` 路径保留为回退。

### 4.2 (b) WebP/AVIF 替代 JPEG，或质量-分辨率自适应

- **收益**：WebP 同画质体积比 JPEG 再省 ~30%（交接文档 §7 也点名）；AVIF 更多但编码 CPU 高（GDI+ 不支持 AVIF，需额外库）。质量自适应（按网络 RTT/画面变化率动态调 quality）能把动态场景带宽再压 30~50%。
- **实现量**：WebP **1~2 人日**（.NET 需引入 `SixLabors.ImageSharp`/`SkiaSharp` 或用 WIC 的 WebP 编码器——**注意 GDI+ 原生不含 WebP 编码器**，会引入依赖体积，与项目"产物 <5MB"约束需核对）。AVIF **3~5 人日**且依赖更重。
- **风险**：中。新编解码依赖体积、浏览器兼容（WebP 全主流支持，AVIF 也支持但解码 CPU 更高）。**治标不治本**——只降带宽，不解决"每帧全图编解码"的结构问题。
- **兼容度**：高（前端 `<img>` 天然支持 WebP，改 Content-Type 即可，但需同步改 `Mp4Writer` 不受影响）。

### 4.3 采集侧"无人观看即停/降频"（本审计新增，非任务清单原候选但 P0 必做）

> ★ 修订：本方案**不是**无脑"停抓"，必须先厘清它和**录制**的冲突，以及空闲信号要**跨进程**才能接进代理的真实成本。下面拆开讲。

**冲突点：实时观看 ≠ 录制，二者共用同一采集循环**
- 现状：`SessionRecorder` 只有一个 `Loop()`（`SessionRecorder.cs:222-257`），一个 `_fps`（`SessionRecorder.cs:142`）。实时观看（`/live`）和录制回放（`RecordStartAsync`）吃的是**同一路 30fps 帧**。
- 所以"没人看就停抓"会**同时停掉录制**——而录制可能仍需在无人观看时持续进行（回放需求）。**不能简单在 `IsLiveActive()==false` 时 `_stopRequested=true`**。

**正确的分层（P0 应这样做，而非"停抓"）**：
1. **采集/观看解耦**（§4.8-1）：把录制帧率（`RecordFps`，可 30fps 高质）与实时观看帧率（独立，默认 10fps）拆成两个节奏。实时通道仅在有人看时才产出低帧率帧；录制通道独立保留。
2. **无人观看时的动作**：停的是**实时采集分支**，不是录制分支；且建议"降到 1~2fps 而非完全停"，保留切回时立刻有帧。

**空闲信号的跨进程接线成本（修订：比初版估计贵）**：
- 代理与主程序是**两个进程**（代理运行在目标 RDP 会话，主程序在交互桌面）。`IsLiveActive()`/`_lastLiveRequest` 目前活在**主程序** `WebServer.cs:146-165`；而直连模式下 `/live` 请求由**代理** `AgentWebServer.cs` 直接接，主程序**看不到**这些请求。
- 因此要拿到"有没有人在看"这个信号，有两条路，各有成本：
  - (A) **代理自统计**：代理的 `AgentWebServer` 自己记录"最近一次 `/live` 请求时间"，直接在代理内做"超过 N 秒无人看 → 实时分支降频"。**无需跨进程**，成本最低（改 `AgentWebServer` 加一个时间戳 + 一个降频信号给 `SessionRecorder`）。★ 推荐。
  - (B) **主程序下发**：主程序轮询/推送活跃状态给代理，走既有的 `RelayClient` 命名管道命令。成本更高（新增协议命令 + 主程序需统计直连请求，但直连请求又不在主程序上，还得反过来让代理上报 → 绕一圈）。
- **结论**：走 (A) 代理自统计，把 `IsLiveActive` 的语义**下沉到代理进程**（或复制一份），而不是复用主程序那个没人消费的 `IsLiveActive`。实现量因此从初版的 0.5~1 人日修正为 **1~2 人日**（含实时/录制双循环拆分）。

- **收益**：空闲时实时采集分支 0.3~0.7 核/账号 → 近 0（**只影响实时分支，录制若仍需 30fps 则录制开销保留**——这是与录制冲突的诚实表述）。
- **风险**：中低。需定义"活跃"阈值（3~5s 无请求降到 1~2fps）；实时/录制双循环要保证共享的 `Gate` 锁与 `Ring` 结构线程安全（现有 `Gate` 已存在）。
- **兼容度**：高，纯增量。

### 4.4 (d) MJPEG-over-WebSocket 长连接改造

> ★ 修订：纠正两处技术断言——① `HttpListener` 默认就是 HTTP/1.1 keep-alive，同源 `fetch` **已经会复用连接**，不是"每帧 `Connection: close` 重新握手"；② `HttpListenerContext` 原生提供 `AcceptWebSocketAsync()`，**不需要手写 101 握手**。

- **收益**：去掉每帧一次 HTTP 请求头（数百字节）+ 每请求一次 `Task.Run` 调度（`AgentWebServer.cs:121`）。带宽省 ~5~10%（HTTP 头占比小），**延迟抖动下降**（从"请求-响应轮询"变"服务端主动推流"，消除前端 `setTimeout` 串行调度带来的帧间隔波动，`web-index.html:734`）。
- **实现量**：**1~2 人日**（代理侧用 `HttpListenerContext.AcceptWebSocketAsync(subProtocol)` 升级连接，`SessionRecorder` 有新帧就 `WebSocket.SendAsync` 推；前端 `new WebSocket(...)` + `createImageBitmap`，均有标准 API）。★ 比初版 2~3 人日下调，因为不需要手写握手。
- **风险**：中低。`AcceptWebSocketAsync` 需在 `HandleAsync` 里识别 `IsWebSocketRequest` 分支；WS 帧分片/背压（观看者慢时队列堆积）要设上限。
- **兼容度**：中高。可与 (a)/(f) 叠加；单账号单端口模型不变，只是协议从"轮询 HTTP"变"推送流"。

### 4.5 (e) Windows 原生采集：DXGI Desktop Duplication + MF H.264 硬编

- **收益**：
  - 抓屏：Desktop Duplication 直接拿 `IDXGIOutputDuplication` 帧，**省掉 BitBlt（单独耗时待测）**，且天然给**脏矩形/移动区域**（`DXGI_OUTDUPL_MOVE_RECTS`/`DirtyRects`）——和 (a) 是同一理念的原生实现。
  - 编码：MF H.264 硬编（项目已有 `Mp4Writer.cs` 的 MF 接入经验，但那是**软编/离线**）走 GPU，编码 CPU 近 0，带宽再降一个数量级（H.264 帧间压缩，1080p 桌面 <1Mbps）。
  - 延迟：端到端可到 <100ms。
- **实现量**：**8~15 人日**（DXGI Duplication 的 P/Invoke 结构体、旋转/多显示器、`AcquireNextFrame` 超时重试、MF H.264 实时编码管线 vs 项目现有离线 `Mp4Writer` 差异大；且**代理运行在 RDP 会话内**——RDP 会话下 Desktop Duplication 行为与物理桌面不同，需实测）。
- **风险**：**高**。RDP 会话里 `IDXGIOutputDuplication` 常拿不到真实桌面（会话 0/非交互桌面全黑，HANDOFF §1.3 已证 BitBlt 是当时唯一可行路径），需先在目标环境验证可行性再投入。
- **兼容度**：中。抓屏与编码都可替换 `GrabJpeg`，前端不变（仍是拿帧）。

### 4.6 (f) 光标方案：三档区分（修订，避免混为一谈）

> ★ 修订：初版把"光标"当成一件事，实际上有**三种不同成本/收益的实现**，必须分档。现状是第 0 档。

| 档位 | 做法 | 画面帧率下限 | 交互跟手度 | 实现量 | 说明 |
|---|---|---|---|---|---|
| **0 现状** | 光标 `DrawIconEx` **画进画面帧**（`SessionRecorder.cs:337`） | 被录制帧率绑架（5fps 光标就一顿一顿，HANDOFF §4.2） | 差 | — | 光标更新率 = `RecordFps` |
| **1 本地 CSS 预测** | 画面**不再画光标**，网页 `pointermove` 位置**本地即时**画 CSS 光标（`web-index.html:887` 已有节流+归一化） | 可降到 5~10fps | 好（本地 60fps） | **0.5~1 人日** | 纯前端 + 一个代理开关；本地光标是"预测位"，画面延迟时与实际位置有毫秒级偏差 |
| **2 独立光标通道** | 代理侧 `GetCursorInfo` 的位置/形状走**独立轻量消息**（不经画面帧），前端据此渲染**真实**光标 | 可降到 1~5fps | 最好（真实位置） | **2~4 人日** | 需新增通道协议（WS/HTTP 单独路由），光标形状（icon）也要传或预置；与 WebSocket 改造天然配套 |

- **收益（档 1 即可命中"最小成本远程控制"）**：画面可降到 5~10fps 而交互仍跟手——光标本地 60fps，画面背景 5~10fps。间接把带宽+解码 CPU 再降 3~6 倍。
- **实现量**：**档 1 约 0.5~1 人日**（推荐先上）；档 2 约 2~4 人日（与 (d) WebSocket 叠加时成本摊薄）。
- **风险**：低。光标热点/DPI 已在前端 `norm()`（`web-index.html:822-831`）处理；档 1 的"预测位"偏差与 RDP 客户端渲染光标同理，可接受。
- **兼容度**：高，纯前端 + 一个代理开关（`DrawCursorOn` 加参数）。

### 4.7 (c) WebRTC（H.264/VP8 硬编 + NACK/PLI + 拥塞控制）——最大候选

- **收益**：**带宽/延迟/交互的最优解**。H.264 硬编（MF）+ NACK/PLI 丢包重传 + 拥塞控制，端到端延迟 <100ms，1080p 桌面 <1Mbps，支持多观看者（SFU 或逐观众编码）。
- **实现量**：**20~40 人日**起步。现成库：
  - **SIPSorcery**（.NET）：有 RTCPeerConnection/DTLS/SRTP/WebRTC 信令，但**媒体编解码要自己接**（可用 MF H.264），适合代理侧。
  - **MediaSoup**（Node/C++）：SFU 网关，需引入 Node 运行时或独立服务进程，与本项目"自包含单 exe、免安装"冲突大。
  - **Windows Media Foundation**：H.264 硬编能力项目已有（`Mp4Writer.cs`），但那是离线 SinkWriter，实时 RTP 打包/时间戳/NACK 反馈要全新实现。
  - 浏览器端 `WebRTC` API 零依赖（`RTCPeerConnection` 内建），但**代理侧没有现成 .NET WebRTC 媒体栈**，这是最大成本。
- **风险**：**高**。DTLS 握手、NAT/STUN（本项目是局域网，NAT 问题小）、`SetSessionDescription` 信令通道、与"一账号一端口 HttpListener"架构的整合、以及**RDP 会话内硬编可用性**（同 (e) 的验证前置）。
- **兼容度**：低~中。是范式替换，不是增量；但可作为 P2 终态，(a)+(f)+(d) 的中间态已能覆盖用户 90% 需求。

### 4.8 其它更优路径（我发现的）

1. **采集侧分级**（最关键）：把"录制（回放用，需 30fps 高质）"和"实时观看（远程控制，5~15fps 足矣）"**解耦成两条独立采集节奏**。当前两者共用一套 `RecordFps/Quality`（`SessionRecorder` 单一循环），实时观看被录制的 30fps 绑架。解耦后实时通道独立低帧率，录制通道按需高帧率。
2. **尾帧 O(1) + 复用位图**：`GetLatestJpeg` 当前 `foreach` 遍历整个 Ring 取最后一帧（`SessionRecorder.cs:374`），30s×30fps=900 元素每次遍历；`CreateCompatibleBitmap` 每帧重新分配（`SessionRecorder.cs:324`）。改成"记录 last 引用 + 复用 hBmp"，采集侧常数级优化。
3. **`<img>` → `createImageBitmap` + Canvas 2D**：前端当前每帧 `URL.createObjectURL` + `<img>`（`web-index.html:783-787`），可用 `createImageBitmap(blob)` 直接进 Worker/Canvas，省一次解码回主线程，并解锁脏矩形贴块。

---

## 5. 推荐最终架构 + 分阶段落地路线

### 5.1 数据流（ASCII）

```
┌────────────────────────────────────────────────────────────────────────┐
│ 目标 RDP 会话内 · RelayAgent 进程                                      │
│                                                                        │
│  [采集]   GetDC ──► BitBlt ──► (复用 hBmp)                              │
│             │                        │                                │
│             │                   [脏矩形检测] 与上一帧块对比               │
│             │                        │  变化块 + 变化像素占比元数据        │
│             │                        ▼                                │
│  [编码]   只对变化块 JPEG/WebP 软编（或 P2 换 MF H.264 硬编）             │
│             │                                                        │
│  [光标]   独立：GetCursorInfo 位置走轻量通道，不进画面                     │
│             │                                                        │
│  [分级]   录制缓冲（30fps 高质，回放用） ≠ 实时流（5~15fps，观看用）        │
│             │                                                        │
│  [传输]   MJPEG-over-WebSocket 长连接 :1080N  /live （推流）              │
└─────────────┬──────────────────────────────────────────────────────────┘
              │  (局域网，TCP)
              ▼
┌────────────────────────────────────────────────────────────────────────┐
│ 浏览器                                                                │
│  WebSocket.onmessage ──► createImageBitmap(blob) ──► Canvas 2D 贴块     │
│                                                              │        │
│  pointermove ──► 本地 CSS 光标即时绘制（60fps，不等画面帧）               │
│  pointerdown/up ──► POST /api/input 直连代理注入                        │
└────────────────────────────────────────────────────────────────────────┘
```

### 5.2 目标指标（相对当前 30fps/质量75/4.8MB/s）

> ★ 下表除"当前"列部分来自交接文档给定值（4.8MB/s）外，其余**均为目标/估算，非实测**；落地后须按 §2.3 实测回填。

| 指标 | 当前 | P0 后（目标） | P1 后（目标） | P2 后（终态目标） |
|---|---|---|---|---|
| 端到端延迟 | 估算 100~600ms（含每帧请求+解码） | 估算 100~200ms | 估算 60~150ms | **目标 <100ms** |
| 带宽 | 4.8 MB/s（交接给定） | 估算 ~1.5 MB/s（解耦+降帧） | 估算 ~0.3 MB/s（脏矩形+WebP） | **目标 <0.15 MB/s（H.264）** |
| 服务端采集 CPU/账号 | 估算 0.3~0.7 核 | 估算 ~0.05 核（空闲近 0，仅实时分支） | 估算 ~0.02 核 | **目标 <0.02 核（硬编）** |
| 可并发观看人数 | 估算 1~2（每观看者 +1 核解码） | 估算 3~5 | 估算 5~10 | **目标 10+（复用一帧多播）** |

### 5.3 分阶段落地

**P0（一周内，纯增量、零范式破坏）**
1. **采集/观看解耦 + 无人观看实时分支降频**：把录制帧率与实时观看帧率拆成两路；代理侧（`AgentWebServer`）**自统计**"最近 `/live` 请求时间"，超时把**实时分支**降到 1~2fps（录制分支独立保留）。→ 消除空闲卡顿，且不破坏录制。详见 §4.3。
2. **复用兼容位图 + 尾帧 O(1) + 去 ToArray 二次拷贝**。→ 采集侧常数级省。
3. **前端 `createImageBitmap` + 更稳静止判据（帧哈希）**。→ 修掉 §3.3 误判。
4. **光标档 1（本地 CSS 预测，不画进画面）**：画面降到 10fps 也能交互跟手。→ 立即可让用户调低实时帧率而不觉顿挫。

**P1（2~4 周，范式优化）**
5. **脏矩形编码**（§4.1a）：代理侧块对比 + 变化块协议，前端 Canvas 贴块。→ 静止带宽 10 倍降。
6. **光标远端渲染**（§4.6f）：画面不画光标，前端本地 CSS 光标。→ 5~10fps 即可交互。
7. **MJPEG-over-WebSocket**（§4.4d）：长连接推流，去每帧 HTTP 抖动。
8. **WebP 编码**（§4.2b，若依赖体积可接受）。→ 带宽再省 30%。

**P2（终态，按需投入）**
9. **DXGI Desktop Duplication + MF H.264 硬编**（§4.5e）：**先在 RDP 会话内做可行性 spike**（1~2 天验证能否拿到桌面帧），可行再投入。→ 带宽/CPU 最优。
10. **WebRTC 全栈**（§4.7c）：仅当需要"多观看者 + 公网/弱网"时上；局域网内部场景 (5)+(6)+(7) 已覆盖 90% 需求。

---

## 6. "最小性能做到远程控制效果"的关键取舍

### 6.1 为什么不该追求 30fps 全屏 JPEG

1. **JPEG 是帧内编码，没有时间维度**。30fps 意味着每帧都是独立的"从 7.9MB 原始位图重新压缩一次 + 浏览器重新解一次"，静止画面 30 次全是重复劳动。远程桌面的核心洞察是：**绝大部分时间只有鼠标和局部区域在动**。
2. **30fps 全屏 JPEG 把最贵的 CPU 环节（软编 + 软解）按"最坏情况"持续付费**，而实际需要流畅的只有光标和点击反馈。
3. **"远程控制"体验 ≠ "视频"体验**。视频要 30fps 连续画面；远程控制要的是**输入→反馈的低延迟闭环**，画面本身可以低帧率，只要光标跟手、点击有即时反馈。

### 6.2 应该追求什么

| 追求 | 手段 | 效果 |
|---|---|---|
| **光标跟手** | 远端渲染光标（本地 60fps CSS） | 交互延迟与画面帧率解耦 |
| **只传变化** | 脏矩形/帧差 | 静止近乎零带宽零 CPU |
| **低延迟输入反馈** | 输入直连代理 + 已合并 Move+Click | 点击 45~64ms 级往返 |
| **画面够用就好** | 5~15fps 背景 + 变化区域优先刷新 | 省 3~6 倍 CPU/带宽 |
| **没人看不烧** | 采集按观看者活跃度降频/停 | 空闲归零 |

### 6.3 一句话方法论

> **把"持续全帧 JPEG 直播"替换成"变化区域推送 + 本地渲染光标 + 采集按需"**，用 1/10 的 CPU 和 1/30 的带宽，换来和 30fps 全屏 JPEG 几乎一样的"远程控制"主观体验——因为远程控制的"流畅"来自输入闭环，不来自背景画面的帧率。

---

## 7. 附：本审计落点索引（可追溯）

| 结论 | 代码位置 |
|---|---|
| 采集侧循环不看观看者 | `Relay/SessionRecorder.cs:226`（`while(!_stopRequested)`）+ `:234` `GrabJpeg` |
| `IsLiveActive` 已定义无人消费 | `Services/WebServer.cs:165`（grep 无采集侧调用） |
| 全屏 BitBlt | `Relay/SessionRecorder.cs:327` |
| 每帧重新分配 7.9MB 位图 | `Relay/SessionRecorder.cs:324` |
| GDI+ JPEG 软编 | `Relay/SessionRecorder.cs:343` |
| ToArray 二次拷贝 | `Relay/SessionRecorder.cs:344` |
| 尾帧 O(n) 遍历 | `Relay/SessionRecorder.cs:374` |
| HTTP 每请求 Task.Run | `Relay/AgentWebServer.cs:121` |
| socket 写出 | `Relay/AgentWebServer.cs:244` |
| 浏览器每帧全图解码 | `docs/web-index.html:784` |
| 静止判据（字节数相等） | `docs/web-index.html:778` |
| 失败退避/串行 | `docs/web-index.html:728,734` |
| Move+Click 已合并 | `Relay/RelayAgent.cs:419`、`Relay/AgentWebServer.cs:298` |
| 大厅服务不在画面路径 | `Server/Program.cs:1-226`（全是 rooms/heartbeat） |
| 主程序 `/api/live` 仅转发兜底 | `Services/WebServer.cs:985-1040`（直连时代理 `/live`） |
