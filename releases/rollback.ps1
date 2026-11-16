# 一键回退到已验证的可靠版本 v1.9.1-reliable
# 用法：右键“使用 PowerShell 运行”，或  powershell -ExecutionPolicy Bypass -File rollback.ps1
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$tag  = 'v1.9.1-reliable'
$src  = Join-Path $repo "releases\$tag"
$dst  = Join-Path $repo 'dist'

Write-Host "=== SoulLink 回退到 $tag ===" -ForegroundColor Cyan

if (-not (Test-Path $src)) { throw "归档不存在：$src" }

# 1. 先结束正在运行的实例，避免文件占用
$running = Get-CimInstance Win32_Process -Filter "Name='SoulLink.exe'" -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "[1/4] 结束运行中的 SoulLink ..."
    $running | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 800
} else {
    Write-Host "[1/4] 无运行实例"
}

# 2. 释放光标约束（防止上次异常残留）
Write-Host "[2/4] 释放光标约束 ..."
try {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RB { [DllImport("user32.dll")] public static extern bool ClipCursor(IntPtr r); }
'@ -ErrorAction SilentlyContinue
    [void][RB]::ClipCursor([IntPtr]::Zero)
} catch { }

# 3. 恢复二进制
Write-Host "[3/4] 还原 dist 下的文件 ..."
New-Item -ItemType Directory -Path $dst -Force | Out-Null
Copy-Item (Join-Path $src 'SoulLink.exe')  $dst -Force
Copy-Item (Join-Path $src 'sl-rescue.exe') $dst -Force

# 4. 校验
Write-Host "[4/4] 校验 ..."
Get-ChildItem $dst -Filter '*.exe' | Select-Object Name, @{N='MB';E={[math]::Round($_.Length/1MB,1)}}, LastWriteTime | Format-Table -AutoSize

$hash = (Get-FileHash (Join-Path $dst 'SoulLink.exe') -Algorithm SHA256).Hash
Write-Host "SHA256 = $hash" -ForegroundColor Green
Write-Host "回退完成：$tag" -ForegroundColor Green



















