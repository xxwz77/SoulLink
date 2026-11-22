# 零 JPEG 全链路检查脚本
#
# 用途：task_mws2jkdm1av20hw 验收时，一条命令给出「实时通路还剩几处 JPEG」。
# 与 JPEG-BASELINE.md 配套：基线是采集时的快照，本脚本是可复跑的判定。
#
# 用法（仓库根目录）：
#   powershell -ExecutionPolicy Bypass -File tools\stream-verify\check-no-jpeg.ps1
#   powershell ... -Verbose      # 额外列出每个命中点的行号
#
# 退出码：0 = 实时通路已零 JPEG；1 = 仍有残留（会打印残留清单）

param([switch]$Verbose)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not (Test-Path (Join-Path $root 'SoulLink.csproj'))) { throw '不在仓库根（找不到 SoulLink.csproj）' }

$fail = 0
function Section($t) { Write-Host ""; Write-Host "== $t" -ForegroundColor Cyan }

# 统计命中（返回行数组；无命中返回空）
function Hits($file, $pattern) {
    $p = Join-Path $root $file
    if (-not (Test-Path $p)) { return @() }
    return @(Select-String -Path $p -Pattern $pattern -AllMatches -ErrorAction SilentlyContinue)
}

function Verdict($ok, $label, $hits) {
    if ($ok) {
        Write-Host ("  PASS  " + $label) -ForegroundColor Green
    } else {
        Write-Host ("  FAIL  " + $label + "  → " + $hits.Count + " 处") -ForegroundColor Red
        if ($Verbose) { foreach ($h in $hits) { Write-Host ("          " + (Split-Path -Leaf $h.Path) + ":" + $h.LineNumber + "  " + $h.Line.Trim()) } }
        $script:fail++
    }
}

# ── A. 实时通路必须清零 ────────────────────────────────────────────────
Section "A1 抓屏不再产出 JPEG（接口 + 两个实现）"

# 接口方法名消失，或者仍在但实时通路不再调用它。
# 判定：IScreenCapture 里不再声明 CaptureJpeg
$h = Hits 'Relay/IScreenCapture.cs' 'CaptureJpeg'
Verdict ($h.Count -eq 0) 'IScreenCapture 接口不再声明 CaptureJpeg' $h

# 实时抓帧循环（SessionRecorder 的 worker）不得调用 Capture.CaptureJpeg
$h = Hits 'Relay/SessionRecorder.cs' 'Capture\.CaptureJpeg'
Verdict ($h.Count -eq 0) 'SessionRecorder 实时抓帧不再调用 CaptureJpeg' $h

Section "A2 环形缓冲不再只存 JPEG"

# Frame 结构体的字段名不应再是 Jpeg
$h = Hits 'Relay/SessionRecorder.cs' 'byte\[\]\s+Jpeg'
Verdict ($h.Count -eq 0) 'Ring 的 Frame 不再以 byte[] Jpeg 存帧' $h

Section "A3 推流不再解码 JPEG"

$h = Hits 'Relay/AgentWebServer.cs' 'DecodeJpegToNv12'
Verdict ($h.Count -eq 0) '/stream 不再有 DecodeJpegToNv12' $h

$h = Hits 'Relay/AgentWebServer.cs' 'GetLatestJpeg'
Verdict ($h.Count -eq 0) '/stream 推流路径不再取 GetLatestJpeg' $h

Section "A4 网页不再做 JPEG 重编码 / 不再有 JPEG 降级链"

foreach ($f in @('Services/WebPages.cs', 'docs/web-index.html')) {
    # ★ 只算**真实调用**，不算注释：本项目的历史注释里会写「旧版用 toDataURL…已移除」，
    #   那种说明性文字不构成 JPEG 调用。按行首非注释符号判断，避免误报。
    $raw = @(Select-String -Path (Join-Path $root $f) -Pattern 'toDataURL' -ErrorAction SilentlyContinue)
    $real = @($raw | Where-Object {
        $t = $_.Line.Trim()
        -not ($t.StartsWith('//') -or $t.StartsWith('*') -or $t.StartsWith('/*') -or $t.StartsWith('<!--'))
    })
    Verdict ($real.Count -eq 0) ($f + ' 不再 toDataURL（只计真实调用，注释不算）') $real

    # 「退回 JPEG」降级分支：匹配 console.warn 里的退回字样 或 显式 goto/return 降级
    $h = @(Select-String -Path (Join-Path $root $f) -Pattern '退回\s*JPEG|fallback.*jpeg|scheduleNext\(0\).*jpeg' -ErrorAction SilentlyContinue)
    $hr = @($h | Where-Object {
        $t = $_.Line.Trim()
        -not ($t.StartsWith('//') -or $t.StartsWith('*') -or $t.StartsWith('/*') -or $t.StartsWith('<!--'))
    })
    Verdict ($hr.Count -eq 0) ($f + ' 不再有「退回 JPEG」降级（只计真实调用）') $hr
}

Section "A5 两份网页必须一致（WebPages.cs 由 html 生成）"

$html = Join-Path $root 'docs\web-index.html'
$gen = Join-Path $root 'Services\WebPages.cs'
if ((Test-Path $html) -and (Test-Path $gen)) {
    $a = [System.IO.File]::ReadAllText($html, [System.Text.Encoding]::UTF8)
    $b = [System.IO.File]::ReadAllText($gen, [System.Text.Encoding]::UTF8)
    # WebPages.cs 是 html 原文嵌进 raw string，所以 html 全文应当是 gen 的子串
    if ($b.Contains($a)) {
        Write-Host "  PASS  WebPages.cs 已与 web-index.html 同步" -ForegroundColor Green
    } else {
        Write-Host "  FAIL  WebPages.cs 与 docs/web-index.html 不同步 → 跑 scripts\sync-webpages.ps1" -ForegroundColor Red
        $fail++
    }
} else {
    Write-Host "  SKIP  文件缺失，无法比对" -ForegroundColor Yellow
}

# ── B. 一次性功能：必须仍然存在（防"零 JPEG"改过头） ──────────────────
Section "B1 静态截图必须仍然可用（不能为了零 JPEG 删掉）"

$h = Hits 'Relay/RelayAgent.cs' 'case\s+"shot"'
Verdict ($h.Count -gt 0) 'shot 指令仍然存在' $h

$h = Hits 'Relay/SessionRecorder.cs' 'CaptureOnce'
Verdict ($h.Count -gt 0) 'CaptureOnce（单张截图）仍然存在' $h

Section "B2 录像回放必须仍然可用"

$h = Hits 'Relay/RelayAgent.cs' 'case\s+"save"'
Verdict ($h.Count -gt 0) 'save 指令仍然存在' $h

$h = Hits 'Relay/SessionRecorder.cs' 'SaveRecording'
Verdict ($h.Count -gt 0) 'SaveRecording 仍然存在' $h

# ── 结论 ──────────────────────────────────────────────────────────────
Write-Host ""
if ($fail -eq 0) {
    Write-Host "RESULT=PASS 实时通路已零 JPEG，且截图/录像未被误删" -ForegroundColor Green
    exit 0
} else {
    Write-Host ("RESULT=FAIL " + $fail + " 项不满足（明细见上；加 -Verbose 看行号）") -ForegroundColor Red
    exit 1
}
