# 零 JPEG 全链路基线快照（HEAD ae270a9，2026-11-23 00:46 采集）
#
# 用途：task_mws2jkdm1av20hw（JPEG 全链路下线）的验收基线。
# 每条都是「改前确实存在的 JPEG 编解码调用点」。改完之后逐条核对：
#   · 实时通路里的必须**全部消失**
#   · 静态截图（一次性功能）允许保留，但必须在报告里单独列出并说明理由
#
# ⚠ 采集方式：grep -n 'Jpeg|JPEG|jpeg' Relay/*.cs Services/*.cs
#    不是猜的，是 HEAD 上的真实结果。

## A. 实时通路（必须清零）

### A1. 抓屏产出 JPEG —— 采集侧
Relay/IScreenCapture.cs:28          `byte[]? CaptureJpeg(int quality, out int width, out int height);`  ← 接口签名本身就是 JPEG
Relay/BitBltCapture.cs:116          `public byte[]? CaptureJpeg(...)` ← GDI+ JPEG 编码
Relay/BitBltCapture.cs:71-76        `JpegCodec()` 找 ImageFormat.Jpeg 编码器
Relay/BitBltCapture.cs:142          `bmp.Save(ms, JpegCodec(), ep)`
Relay/WgcCapture.cs:242             `public byte[]? CaptureJpeg(...)` ← WGC 也转 JPEG
Relay/WgcCapture.cs:391-414         `EncodeJpeg()` + `JpegCodecStatic()` + `bmp.Save(ms, JpegCodecStatic(), ep)`

### A2. 环形缓冲存 JPEG —— 数据源
Relay/SessionRecorder.cs:112-117    `Frame { public byte[] Jpeg; }` ← Ring 只存 JPEG
Relay/SessionRecorder.cs:137        `bytes += f.Jpeg.Length`
Relay/SessionRecorder.cs:222        `var jpeg = Capture.CaptureJpeg(quality, out cw, out ch);`
Relay/SessionRecorder.cs:229        `Ring.Enqueue(new Frame { Jpeg = jpeg, At = now })`
Relay/SessionRecorder.cs:251        `GetLatestJpeg()`
Relay/SessionRecorder.cs:258,271    返回 `f.Jpeg`

### A3. 推流前 JPEG 解码 —— 最浪费的一跳
Relay/AgentWebServer.cs:294-301     启动时等 `GetLatestJpeg()`
Relay/AgentWebServer.cs:316         `Log("[stream] 首帧 ... jpeg=" + jpeg.Length)`
Relay/AgentWebServer.cs:336         `cur = SessionRecorder.GetLatestJpeg()`
Relay/AgentWebServer.cs:339         `DecodeJpegToNv12(cur)` ← 每帧解码 JPEG
Relay/AgentWebServer.cs:363-378     `DecodeJpegToNv12()` 本体（Bitmap→LockBits→BgraToNv12）

### A4. 网页 /live 端点 —— JPEG 轮询本体
Relay/AgentWebServer.cs:263-276     `WriteLiveAsync()` 返回 `image/jpeg`
Relay/AgentWebServer.cs:18          注释 `GET /live 最新一帧 JPEG`
Services/WebDirect.cs:65            `/live` 在白名单里

### A5. 网页播放器 —— JSON 降级链
Services/WebPages.cs:888            `f.src = canvas.toDataURL('image/jpeg', 0.8);` ← 每帧重编码
Services/WebPages.cs:827            `console.warn('[H264] 解码错误，退回 JPEG:', e);`
Services/WebPages.cs:892            `console.warn('[H264] 流断开，退回 JPEG:', e);`
Services/WebPages.cs:928-933        注释「失败自动退回 JPEG 轮询」+ 降级分支
Services/WebPages.cs:409            `<select id="selQuality" title="JPEG 质量...">`
docs/web-index.html:872             同一处 `toDataURL`
docs/web-index.html:811,876,918     同一批「退回 JPEG」分支
docs/web-index.html:409             同一处 selQuality

## B. 一次性功能（允许保留，但必须单独说明）

### B1. 静态截图
Relay/SessionRecorder.cs:479        `CaptureOnce()` 里 `Capture.CaptureJpeg(...)`
Relay/SessionRecorder.cs:493        `File.WriteAllBytes(file, jpeg)` ← 存 .jpg
Relay/RelayAgent.cs:997-1011        `case "frame"` 取最新 JPEG 落盘
Services/WebServer.cs:1032          `image/jpeg` Content-Type（主程序给浏览器发图）

### B2. 录像回放 / 转码兜底
Relay/SessionRecorder.cs:333-425    「转成 MP4 并删除 JPEG 序列」，失败则保留 frame-*.jpg
Relay/SessionRecorder.cs:409,425    `File.WriteAllBytes(... + ".jpg", ...)`
Services/Mp4Writer.cs:113-240       `TryEncode(IReadOnlyList<string> jpegFiles, ...)` JPEG 序列→MP4
Services/Mp4Writer.cs:158,207-209   Image.FromFile / LoadAsBgraBottomUp
Services/WebServer.cs:455           `.jpg / .jpeg => image/jpeg` MIME
Services/WebServer.cs:556-576       录制成 MP4 后不能再靠数 .jpg 得帧数

### B3. 主程序侧的兼容读取
Services/WebServer.cs:136          注释「每个命令在代理侧要 BitBlt + JPEG 编码一次」

## C. 判定口径（验收时照这个打勾）

| # | 检查项 | 通过条件 |
|---|---|---|
| C1 | `grep -c 'CaptureJpeg' Relay/*.cs` | 0（接口与两个实现都不再有） |
| C2 | `grep 'DecodeJpegToNv12' Relay/AgentWebServer.cs` | 0 |
| C3 | `SessionRecorder.Ring` 的 Frame 字段 | 不再是 `byte[] Jpeg`（H.264 NAL 或原始帧） |
| C4 | `/stream` 推流循环里是否出现 `GetLatestJpeg` | 0 |
| C5 | `WebPages.cs` / `web-index.html` 里 `toDataURL` | 0 |
| C6 | 网页里「退回 JPEG」字样 | 0（或只剩注释且无代码分支） |
| C7 | `/live` 端点 | 要么删除，要么明确保留但不在自动播放链上 |
| C8 | 静态截图仍工作 | `case "shot"` / `/shot` 实测能出 JPG（**必须验，别把功能改没了**） |
| C9 | 录像回放仍工作 | 能出 replay.mp4（**必须验**） |

## D. 注意事项（给改的人，别踩）

1. **B 类不是"顺手也能删"**：截图和录像回放是用户明确要的功能，删了就是回归。
   验收时我会专门验 C8 / C9 —— 「零 JPEG」不等于「删掉所有跟 JPEG 有关的代码」。
2. `Mp4Writer` 吃的是 **JPEG 文件序列**；实时链路改原生 H.264 后，
   录像路径要么改成吃 NAL，要么保留一条「内存帧→JPEG 文件→MP4」的离线兜底。
   两种都行，但**必须在文档里写明选了哪种**，否则下一个人接手会误删。
3. `Services/WebPages.cs` 是生成的，改它没用 —— 改 `docs/web-index.html`
   然后跑 `scripts/sync-webpages.ps1`。只改一边 = 改了但用户看不到。
