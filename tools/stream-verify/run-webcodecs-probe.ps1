# 用无头 Edge 跑 webcodecs-probe.html，把「网页真的在解码」变成可判定证据。
#
# 为什么需要自动化：WebCodecs 是否真的跑起来，靠人眼看截图无法区分
#   「解码成功画上去了」和「解码器报错但页面还是黑底」。
#
# ★ 为什么不走 CDP：第一版用 CDP + 手写 WebSocket，ReceiveAsync 会抛
#   AggregateException 且难以稳定复现。改成「页面把结果 POST 回本地服务」——
#   页面自己就知道结论，回传一个 JSON 即可，链路短、没有协议脆弱点。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File tools\stream-verify\run-webcodecs-probe.ps1 `
#       -Base http://127.0.0.1:19095 -Token test123 -Ms 6000
#
# 前置：需要有一个正在出帧的 agent（用 stream-verify.exe serve 模式代持宿主）。

param(
    [Parameter(Mandatory = $true)][string]$Base,
    [string]$Token = '',
    [int]$Ms = 6000,
    [int]$TimeoutSec = 60,
    [string]$Edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$probe = Join-Path $here 'webcodecs-probe.html'
if (-not (Test-Path $probe)) { throw "找不到 $probe" }
if (-not (Test-Path $Edge)) { throw "找不到 Edge：$Edge" }

$staticPort = Get-Random -Minimum 21000 -Maximum 21999
$pageUrl = "http://127.0.0.1:$staticPort/webcodecs-probe.html?base=$([uri]::EscapeDataString($Base))&token=$([uri]::EscapeDataString($Token))&ms=$Ms&report=1"
Write-Host "验证页 = $pageUrl"

$userData = Join-Path $env:TEMP ('edge-probe-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$rtFile = Join-Path $env:TEMP ('wcprobe-result-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.json')

# ── 静态服务：GET 给页面，POST /report 收结论 ─────────────────────────
$serverJob = Start-Job -ArgumentList $staticPort, $probe, $rtFile -ScriptBlock {
    param($port, $file, $rtFile)
    $l = New-Object System.Net.HttpListener
    $l.Prefixes.Add("http://127.0.0.1:$port/")
    $l.Start()
    $bytes = [System.IO.File]::ReadAllBytes($file)
    while ($l.IsListening) {
        try {
            $ctx = $l.GetContext()
            $path = $ctx.Request.Url.AbsolutePath
            if ($ctx.Request.HttpMethod -eq 'POST' -and $path -eq '/report') {
                $sr = New-Object System.IO.StreamReader($ctx.Request.InputStream, [System.Text.Encoding]::UTF8)
                $body = $sr.ReadToEnd()
                $sr.Close()
                [System.IO.File]::WriteAllText($rtFile, $body, (New-Object System.Text.UTF8Encoding($false)))
                $ctx.Response.StatusCode = 204
                $ctx.Response.Close()
                continue
            }
            # 其余一律给页面（也处理 favicon）
            $ctx.Response.StatusCode = 200
            $ctx.Response.ContentType = 'text/html; charset=utf-8'
            $ctx.Response.ContentLength64 = $bytes.Length
            $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
            $ctx.Response.Close()
        } catch { break }
    }
}
Start-Sleep -Milliseconds 800

$edgeProc = $null
try {
    $edgeProc = Start-Process -FilePath $Edge -ArgumentList @(
        '--headless=new'
        "--user-data-dir=$userData"
        '--no-first-run'
        '--no-default-browser-check'
        '--disable-gpu'
        '--autoplay-policy=no-user-gesture-required'
        '--disable-features=Translate,MediaRouter'
        $pageUrl
    ) -PassThru -WindowStyle Hidden
    Write-Host "Edge PID = $($edgeProc.Id)"

    # 等页面回传结论
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $json = $null
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        if (Test-Path $rtFile) {
            Start-Sleep -Milliseconds 300     # 等写完
            try {
                $txt = [System.IO.File]::ReadAllText($rtFile)
                if ($txt.Trim().Length -gt 2) { $json = $txt | ConvertFrom-Json; break }
            } catch { }
        }
        if ($edgeProc.HasExited) { Write-Host "（Edge 提前退出，code=$($edgeProc.ExitCode)）" -ForegroundColor Yellow }
    }

    if (-not $json) {
        Write-Host ''
        Write-Host "RESULT=FAIL 页面在 $TimeoutSec 秒内没有回传结论" -ForegroundColor Red
        Write-Host '（可能：WebCodecs 不可用 / /stream 无帧 / 页面脚本异常）' -ForegroundColor Yellow
        exit 1
    }

    Write-Host ''
    Write-Host '== 页面内实测结果（浏览器真实执行）==' -ForegroundColor Cyan
    Write-Host ("  WebCodecs 支持    : " + $json.webCodecs)
    Write-Host ("  HTTP 状态         : " + $json.status)
    Write-Host ("  收到字节          : " + $json.bytes)
    Write-Host ("  NAL 数            : " + $json.nalCount)
    Write-Host ("  关键帧数          : " + $json.keyFrameCount)
    Write-Host ("  从 SPS 解析 codec : " + $json.codec)
    Write-Host ("  解码器已配置      : " + $json.configured)
    Write-Host ("  解出帧数          : " + $json.decodedFrames)
    Write-Host ("  首帧尺寸          : " + $json.firstFrameW + 'x' + $json.firstFrameH)
    Write-Host ("  已画到 canvas     : " + $json.drewToCanvas)
    Write-Host ("  canvas 非全黑     : " + $json.nonBlank)
    if ($json.error) { Write-Host ("  错误              : " + $json.error) -ForegroundColor Yellow }

    $fail = 0
    function V($ok, $label) {
        if ($ok) { Write-Host "  PASS  $label" -ForegroundColor Green }
        else { Write-Host "  FAIL  $label" -ForegroundColor Red; $script:fail++ }
    }
    V $json.webCodecs '浏览器支持 WebCodecs（VideoDecoder 存在）'
    V ($json.status -eq 200) '/stream 返回 200'
    V ($json.bytes -gt 1024) '收到实质数据（>1KB）'
    V $json.configured '解码器按流内 SPS 成功配置'
    V ($json.decodedFrames -gt 0) '真的解出了帧（不只是字节流过）'
    V $json.drewToCanvas '帧被画进 canvas'
    V $json.nonBlank 'canvas 非全黑（画面内容真实）'

    Write-Host ''
    if ($fail -eq 0) { Write-Host 'RESULT=PASS WebCodecs 端到端可用' -ForegroundColor Green; exit 0 }
    else { Write-Host ("RESULT=FAIL " + $fail + " 项不满足") -ForegroundColor Red; exit 1 }
}
finally {
    try { if ($edgeProc -and -not $edgeProc.HasExited) { Stop-Process -Id $edgeProc.Id -Force } } catch { }
    try { Stop-Job $serverJob -ErrorAction SilentlyContinue; Remove-Job $serverJob -Force -ErrorAction SilentlyContinue } catch { }
    Start-Sleep -Milliseconds 500
    try { if (Test-Path $userData) { Remove-Item $userData -Recurse -Force -ErrorAction SilentlyContinue } } catch { }
    try { if (Test-Path $rtFile) { Remove-Item $rtFile -Force -ErrorAction SilentlyContinue } } catch { }
}
