# SoulLink H.264 播放器自测页

**目的**：不依赖服务端 `/stream`，用已知的合法 H.264 Annex-B 文件验证
`docs/web-index.html` 里的播放器解码逻辑（splitAnnexB 切流 → SPS 解析 →
decoder.configure → decode → canvas 上屏）是否正确。

## 为什么「零复制」：运行时抽取

`docs/web-index.html` 是嵌入 C# raw string 的自包含页面（Services/WebPages.cs
生成），**不能**引用外部 .js（Agent 只服务这一个 HTML，服务端代码不归本工具改）。
因此自测页不复制播放器代码，而是**在浏览器里实时从服务器取 docs/web-index.html
原文，用与词法等价的括号配平提取器抽出 7 个播放器函数原样 eval 装配**：

- 被测代码 === 线上代码，零漂移由构造保证（不是靠人工同步）。
- 页面上会显示被抽代码的 SHA256，证据可核对。
- 若 web-index.html 里函数改名/改签名，自测页立刻报「未找到函数」而不是测一份旧代码。

抽出的函数（与线上同名同源）：
`splitAnnexB` / `avcCodecFromSps` / `startStream` / `abortStream` / `stopH264` / `setPh` / `scheduleReconnect`

自测桩（仅自测页有，不属于播放器）：
- `directBase()/directToken()` 桩让 `startStream` 的 fetch 打到本地 `/stream`；
- `VideoDecoder` 计数包装器记录 configure 参数与输出帧数（不改播放器逻辑）；
- `/stream` 由 server.mjs 提供：把 .h264 文件按 ~700 字节分块、3ms 间隔推送，
  完整模拟真实网络的 chunk 边界（exercises pending 缓存逻辑）。

## 生成测试文件（ffmpeg）

ffmpeg 路径：`C:\Users\91244\AppData\Local\Temp\ff-dl\x\ffmpeg-master-latest-win64-gpl\bin\ffmpeg.exe`

红/绿/蓝竖条 + 一个移动白块（白块制造帧间运动，验证 delta 帧解码；
RGB 块用于肉眼分辨颜色是否正确——服务端曾出黑白 bug）：

```powershell
& 'C:\Users\91244\AppData\Local\Temp\ff-dl\x\ffmpeg-master-latest-win64-gpl\bin\ffmpeg.exe' `
  -y -f lavfi -i 'color=c=black:s=640x360:d=3:r=10' `
  -vf "drawbox=x=40:y=40:w=160:h=280:color=red:t=fill,drawbox=x=240:y=40:w=160:h=280:color=green:t=fill,drawbox=x=440:y=40:w=160:h=280:color=blue:t=fill,drawbox=x='mod(t*80,320)':y=300:w=40:h=40:color=white:t=fill" `
  -c:v libx264 -profile:v baseline -x264-params 'keyint=10:min-keyint=10:scenecut=0' `
  -f h264 "$env:TEMP\sl-selftest.h264"
```

说明：baseline profile 与服务端软编 MFT 输出接近；keyint=10 每 10 帧一个 IDR；
30 帧 / 3 秒 / 640x360。

## 运行自测

```powershell
# 1. 起本地服务（仓库根目录执行）
node tools\player-selftest\server.mjs 8614 "$env:TEMP\sl-selftest.h264" . "$env:TEMP\sl-selftest-evidence.json"

# 2. 浏览器打开
http://127.0.0.1:8614/
```

页面自动执行：抽取 → NAL 预扫描 → startStream 全流程 → 上屏 → 判定。
页面顶部出现绿色 **PASS**（且画面显示红绿蓝条 + 白块）即通过；
证据 JSON 会 POST 回 server 写入上面指定的文件（含 codec 串、解码帧数、
分辨率、被测代码 SHA256、错误列表）。

无头模式取证（可选）：

```powershell
& 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe' `
  --headless=new --disable-gpu --no-sandbox --window-size=1000,1100 `
  --virtual-time-budget=30000 `
  --screenshot="$env:TEMP\sl-selftest.png" http://127.0.0.1:8614/
```

## 注意

- 测试 .h264 与截图/evidence 都放 `%TEMP%`，不入库（工作区规则）。
- `server.mjs` 的 `/stream` 是**一次性**的（第二次请求 404），避免页面重连逻辑
  在自测里循环；`/sample` 可重复取（预扫描用）。
- 自测页不属于 WebPages.cs 的收录范围；若改了 `docs/web-index.html` 本体，
  仍必须跑 `scripts\sync-webpages.ps1`。
