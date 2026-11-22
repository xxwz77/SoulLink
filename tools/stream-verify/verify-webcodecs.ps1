# 一站式：起「假宿主 + 独立 agent」→ 无头 Edge 跑 WebCodecs 验证 → 清理。
#
# ★ 为什么必须一个脚本里做完：
#   上一版把 serve 放在 Start-Job 里，而每次 pwsh 调用都是一个新进程，
#   进程一退出 job 就没了，浏览器去连时 agent 已经不在了 —— 报
#   「Failed to fetch」，看起来像 CORS 或网页问题，其实是服务没起来。
#   这个脚本用 Start-Process 起真实进程，全部在本脚本生命周期内，结束时精确收尾。
#
# 安全边界：
#   · 只启动/结束本脚本自己创建的 PID（存下来精确 Kill），绝不按名字/路径通配
#   · 只用 19095 这类空闲端口，不碰 19080/19081、不碰 dist\stable\、不碰会话 1 主程序
#
# 用法（仓库根目录）：
#   powershell -ExecutionPolicy Bypass -File tools\stream-verify\verify-webcodecs.ps1 `
#       -AgentDir $env:TEMP\sv-agent-test -Port 19095 -Token test123

param(
    [Parameter(Mandatory = $true)][string]$AgentDir,
    [int]$Port = 19095,
    [string]$Token = 'test123',
    [int]$Ms = 6000
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = Split-Path -Parent (Split-Path -Parent $here)
$svExe = Join-Path $here 'bin\Release\net11.0-windows\stream-verify.exe'
if (-not (Test-Path $svExe)) { throw "先构建：dotnet build `"$here\StreamVerify.csproj`" -c Release" }

$serveProc = $null
$serveLog = Join-Path $env:TEMP ('sv-serve-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.log')

try {
    # ── ① 后台起 serve（真实进程，记下 PID）─────────────────────────
    Write-Host "启动 serve（agent 目录 $AgentDir，端口 $Port）…"
    $serveProc = Start-Process -FilePath $svExe -ArgumentList @(
        'serve', '--dir', $AgentDir, '--port', "$Port", '--token', $Token, '--seconds', '300'
    ) -PassThru -WindowStyle Hidden -RedirectStandardOutput $serveLog -RedirectStandardError "$serveLog.err"
    Write-Host "  serve PID = $($serveProc.Id)"

    # ── ② 等它就绪（轮询端口 + 缓冲有帧）────────────────────────────
    $ready = $false
    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 800
        try {
            $r = Invoke-WebRequest "http://127.0.0.1:$Port/api/info?token=$Token" -UseBasicParsing -TimeoutSec 3
            if ($r.StatusCode -eq 200) { $ready = $true; break }
        } catch { }
        if ($serveProc.HasExited) { throw "serve 提前退出（code=$($serveProc.ExitCode)）" }
    }
    if (-not $ready) { throw "serve 45 秒内没有在 :$Port 上就绪" }
    Write-Host "  /api/info 就绪"

    # 等环形缓冲有帧，否则 /stream 会 404，浏览器测出来是「没画面」而非「解码失败」
    $frameDeadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $frameDeadline) {
        try {
            $live = Invoke-WebRequest "http://127.0.0.1:$Port/live?token=$Token" -UseBasicParsing -TimeoutSec 3
            if ($live.StatusCode -eq 200 -and $live.RawContentLength -gt 1000) {
                Write-Host ("  /live 有帧（" + $live.RawContentLength + " 字节）")
                break
            }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Milliseconds 500

    # ── ③ 跑浏览器验证 ───────────────────────────────────────────────
    & powershell -ExecutionPolicy Bypass -File (Join-Path $here 'run-webcodecs-probe.ps1') `
        -Base "http://127.0.0.1:$Port" -Token $Token -Ms $Ms
    $code = $LASTEXITCODE

    exit $code
}
catch {
    Write-Host ("ERROR " + $_.Exception.Message) -ForegroundColor Red
    if ($serveProc) {
        try {
            if (Test-Path $serveLog) { Write-Host '---- serve 输出 ----'; Get-Content $serveLog | Select-Object -Last 40 }
        } catch { }
    }
    exit 3
}
finally {
    # 精确收拾自己起的进程
    try { if ($serveProc -and -not $serveProc.HasExited) { Stop-Process -Id $serveProc.Id -Force } } catch { }
    Start-Sleep -Milliseconds 400
    try { if (Test-Path $serveLog) { Remove-Item $serveLog -Force -ErrorAction SilentlyContinue } } catch { }
    try { if (Test-Path "$serveLog.err") { Remove-Item "$serveLog.err" -Force -ErrorAction SilentlyContinue } } catch { }
}
