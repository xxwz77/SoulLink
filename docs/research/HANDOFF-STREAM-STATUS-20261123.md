# 交接文档 · 视频流现状（2026-11-23 00:30）

> 状态：**服务端链路跑通，但 `/stream` 在真实代理里不可用**
> 上一份：`HANDOFF-VIDEO-20261122.md`（昨晚 23:50，记录了跑通过程）
> **本文档修正上一份的错误结论**

---

## 0. 最要紧的三句话

1. **`/stream` 在探针里能跑，在真实 Agent 里跑不了** —— 客户端收到 `An error occurred while sending the request`（连接被重置），无 HTTP 响应。
2. **`/live`（JPEG 轮询）可用**，实测 HTTP 200 + 63.6 KB。但用户明确拒绝这个方案（「我不要图片live，太卡了」）。
3. **网页播放器已改成优先 H.264**，但 `/stream` 不通，所以现在网页**看不到画面**。

---

## 1. 端口分配的坑（昨晚搞错了，现已实测确认）

### 实测结果

```
WebPort = 19080（配置值）

19080  = 主程序自己的 web 服务（Cookie 鉴权，浏览器登录页用）
         ★ netsh 显示注册进程是 SoulLink.exe，不是 Agent
19081  = A2 的 Agent 直连（token 鉴权）  ← acc_a2 = 索引 0 → base+0+1
19082  = A3 的 Agent 直连（A3 未连接时空闲）
```

### `WebDirect.PortFor` 的真实规则

```csharp
// Services/WebDirect.cs:51
int port = basePort + accountIndex + 1;   // ★ 有 +1
```

**所以我之前把 18080 改成 19080 是没必要的**：18080 是主程序 web 服务，A2 Agent 一直在 18081。

### 两套鉴权，别搞混

| 端口 | 鉴权方式 | 用途 |
|---|---|---|
| 主程序 web（19080） | **Cookie**（`ctx.Request.Cookies`） | 浏览器登录后管理 |
| Agent 直连（19081+） | **token**（`?token=` 或 `X-SoulLink-Token` 头） | 取画面、注入输入 |

**用 `?token=xxx` 去请求 19080 必然 401**，那是 Cookie 鉴权的主程序端口。

---

## 2. `/stream` 的真实实现（有问题，需要改）

### 当前实现（`Relay/AgentWebServer.cs:265-342`）

```
GetLatestJpeg()          ← 从 Ring 拿 JPEG（Ring 里存的就是 JPEG）
    ↓
DecodeJpegToNv12()       ← JPEG 解码成 BGRA，再转 NV12
    ↓
    ★ 这里的 BgraToNv12() 是**纯 C# 逐像素循环**，1080p 每帧约 10~20ms
    ↓
EncodeNv12()             ← H.264 编码
    ↓
stream.WriteAsync(nal)   ← 推给客户端
```

### 三个问题

**问题 1：`/stream` 直接失败（最紧急）**

现象：客户端拿不到任何 HTTP 响应，连接被重置。但 Agent 进程不崩，`/live` 仍正常。

未确定原因，可能的位置：
- `MfFrameEncoder.TryInitialize()` 抛异常
- `EncodeNv12()` 抛异常
- `SendChunked` + `async void` 生命周期问题
- 首次写响应头之前就失败，导致 HttpListener 直接断连

**排查建议**：给 `WriteStreamAsync` 加日志（我原本要加但被打断了），在 `Log()` 里输出每个阶段。

**问题 2：链路是「JPEG → 解码 → NV12 → 编码」，全程浪费**

这正是用户早先指出的问题：
> 「最好是直接采集视频流直接输出，转一手总是有资源浪费的，你觉得呢？」

**当时的判断是对的，但这版实现退回了那个问题。**

正确做法（设计过但未实施）：
- Ring 改成存**原始帧**或**已编码的 NAL**
- 让 `WgcCapture` 的输出直接进编码器，不经过 JPEG

**问题 3：15fps 硬编码**

```csharp
await Task.Delay(66);   // 约 15fps
```

应跟随游戏的真实帧率 / 观看需求。

---

## 3. 已确认可用的部分（实测）

| 端点 | 端口 | 状态 | 证据 |
|---|---|---|---|
| `/api/info` | 19081 | ✅ | `{"ok":true,"account":"acc_a2","session":49,"pid":150660,"port":19081}` |
| `/live` | 19081 | ✅ | HTTP 200，`image/jpeg`，63.6 KB |
| `/stream` | 19081 | ❌ | 连接重置，无响应 |
| 主程序 web | 19080 | ⚠️ 监听中但无响应（可能要 Cookie） | |

### WGC 抓屏（生产可用）

`Relay/WgcCapture.cs` 已完整实现，且 `streamgap` 在 A1 会话实测抓到原神画面：
- `tools/wgc-probe/snapshot-wgc.png`（1920x1080，非黑 100%）
- 帧率 31.7fps = 游戏锁帧率

### H.264 编码器（探针可用）

`Services/MfFrameEncoder.cs`：
- Vortice.MediaFoundation 3.8.3，`IMFTransform` 逐帧编码
- 硬编异步（`TransformAsyncUnlock` + 事件驱动）
- 实测：抓屏 17.2ms + 编码 1.6ms（BitLlt 场景）/ 2~8ms + 6~8ms（WGC 场景）
- **输出已是 Annex-B**，不需转换

**但它在探针里跑通，在 AgentWebServer 里跑不通。** 差异待查。

---

## 4. 网页播放器现状（已改，但依赖不通的 /stream）

`docs/web-index.html` 已改：

```javascript
if (webCodecsSupported && !dirtyFrame) {
    startH264(cur, base, tok);    // 走 /stream
    return;
}
// 否则退回 JPEG 轮询
```

### 已知的两个缺陷（我这版写得不好）

**缺陷 1：`canvas.toDataURL()` 每帧重编码**

```javascript
f.src = canvas.toDataURL('image/jpeg', 0.8);   // ← 每帧一次 JPEG 编码
```

这等于把好不容易省下的带宽又花回去。正确做法是把 `<canvas>` 直接放进 DOM 替换 `<img>`。

**缺陷 2：`codec: 'avc1.4D4028'` 硬编码**

```javascript
decoder.configure({ codec: 'avc1.4D4028', ... })   // 写死了 Main Profile Level 4.0
```

实际编码器输出的 profile/level 可能不同，**这个不匹配会直接导致解码失败**。
应该从流的 SPS 里解析，或者跟服务端约定固定组合。

---

## 5. 环境踩坑（都是真的踩过）

### 5.1 A2 会话会漂移

```
实际观察：47 → 注销 → 49
```

**每次重连会话号都变**，所以不能把会话号写死。主程序通过 `AccountStateCenter` 动态查。

### 5.2 A2 卡在 Disc 状态时要注销

```powershell
query session                    # 看状态
logoff 47                        # 注销卡住的会话
```

用户原话：「重连次数太大，重连不上A2，需要先注销A2，ID会变」。

### 5.3 HTTP.sys 注册残留

进程崩溃后，端口可能仍显示被 `PID 4`（System）监听，但 `netsh http show urlacl` 看不到。

表现：
```
Get-NetTCPConnection -LocalPort 18080 -State Listen   → PID 4
netsh http show urlacl                                → 空
HttpListener.Start()                                  → 冲突错误
```

**解法：换端口，或用 `netsh http show servicestate view=session` 查注册进程。**

### 5.4 Broker 投送用的是 SoulLinkDev 目录

```
C:\Program Files\SoulLinkDev\SoulLink.Agent.exe   ← Broker 从这启动
```

**换 dist 的 exe 不会自动更新它**。要手动复制，或点界面上的「更新服务」。

### 5.5 停止进程时不要按路径通配

我犯过的错：
```powershell
# ✗ 错：这会连 dist\stable\ 一起停掉
Get-Process | Where-Object { $_.Path -like '*dist*' } | Stop-Process

# ✓ 对：精确匹配单个文件
Get-Process | Where-Object { $_.Path -eq 'E:\...\dist\SoulLink.exe' } | Stop-Process
```

**AGENTS.md 明确禁止触碰 `dist/stable/SoulLink-Stable.exe`（挂机测试版）。**

---

## 6. 下一步该做什么（按优先级）

### P0：查清 `/stream` 为什么不通

**最小排查步骤**：

1. 给 `WriteStreamAsync` 每个阶段加 `Log()`，确认走到哪一步
2. 特别确认 `MfFrameEncoder.TryInitialize()` 是否返回 false
3. 如果编码器初始化成功但推流失败，检查 `SendChunked` 的写法

**参考**：`tools/stream-probe/` 里有能跑通的实现，对比差异。

### P1：修「JPEG→解码→NV12→编码」的浪费

**目标链路**：
```
WgcCapture（GPU 纹理 / BGRA）
    ↓ 不经过 JPEG
MfFrameEncoder
    ↓
推流 + 存 Ring
```

**关键改动**：`Ring` 的数据结构要改。现在存 JPEG，应该改成存原始帧或已编码 NAL。

**注意**：改了 Ring，录像保存逻辑（`Mp4Writer`）也要跟着改。

### P2：修网页播放器两个缺陷

1. 用 `<canvas>` 直接替换 `<img>`，去掉 `toDataURL`
2. 从 SPS 解析 profile/level，或跟服务端约定固定值

### P3：安卓 APP

设计已完成：`docs/research/ANDROID-APP-DESIGN-20261122.md`
- Kotlin，SurfaceView + MediaCodec
- 环境齐备（`D:\Android\sdk`，JDK 17，Gradle 8.9）

**但 `/stream` 不通之前，APP 也做不了。**

---

## 7. 关键代码位置

| 文件 | 作用 |
|---|---|
| `Relay/WgcCapture.cs` | WGC 抓屏（完整，含 V2 特性 + 降级 + 生命周期） |
| `Relay/BitBltCapture.cs` | BitLlt 兜底 |
| `Relay/IScreenCapture.cs` | 抓屏接口 |
| `Services/MfFrameEncoder.cs` | H.264 逐帧编码（IMFTransform + Vortice） |
| `Services/MfH264Encoder.cs` | H.264 写 MP4（SinkWriter，录像用） |
| `Relay/SessionRecorder.cs` | 环形缓冲 / 录像 / 抓屏调度 |
| `Relay/AgentWebServer.cs:265` | **`/stream` 实现（有问题）** |
| `Services/RelayClient.cs:673` | `CaptureEnv` —— 环境变量下发 |
| `Services/WebDirect.cs:45` | `PortFor` —— 端口分配规则 |
| `docs/web-index.html` | 网页（含 WebCodecs 播放器） |
| `tools/stream-probe/` | **能跑通的探针**，改 `/stream` 时对照它 |

---

## 8. 配置项

```json
{
  "CaptureMode": 1,              // 0=BitLlt 1=WGC 2=WGC-Monitor
  "VideoBitrateKbps": 8000,      // 8 Mbps
  "WebPort": 19080,              // 主程序 web 服务端口；Agent 用 +1 +索引
  "WebDirectToAgent": true,
  "WebPassword": "test123",      // Agent 直连的 token
  "RecordAccountIds": ["acc_a2"],
  "RecordFps": 5
}
```

---

## 9. 本次会话的教训

**我犯的错（如实记录）**：

| 错误 | 后果 |
|---|---|
| 按路径通配停进程 | **误停了挂机版 `SoulLink-Stable`**（它自己重启了，没造成损失） |
| 以为 Agent 直连端口 = `WebPort` | 白改了端口（18080→19080），实际 `PortFor` 有 `+1` |
| 以为 `19080` 是 Agent 端口 | 用 token 去测主程序的 Cookie 端口，一直 401，排查方向错了很久 |
| 没验证 `/stream` 就跑完整链路 | 现在整条链路是断的 |

**用户指出的问题（都是对的）**：

| 用户说 | 实际 |
|---|---|
| 「我没有界面设置吗」 | 确实没有，`CaptureMode` 只是配置字段 |
| 「电脑网页好像也在走老逻辑」 | 确实，网页一直是 JPEG 轮询 |
| 「我不要图片live，太卡了」 | `/live` 虽然通，但方案不可接受 |
| 「重连次数太大，需要先注销A2」 | 注销后会话 47→49 才恢复 |

---

## 10. 生产环境禁令

- ⚠ **不要动 `dist/stable/`**（挂机测试版，AGENTS.md 明令禁止）
- ⚠ **不要用 A1 / A4 账号**（用户禁止）
- ⚠ **不要动 A1 的游戏和 BetterGI**
- ✅ A2 可用，用完要注销
- ✅ Staging 目录：`E:\Documents\deepseek-harness\SoulLink\dist`