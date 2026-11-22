# 从 docs/web-index.html 重新生成 Services/WebPages.cs，并同步到 dist\
#
# 为什么要有这个脚本：WebPages.cs 是 C# 的 raw string literal，
# 手工同步极易忘记，表现为改了页面但程序里还是旧的（踩过两次）。
#
# 用法（在仓库根目录）：powershell -ExecutionPolicy Bypass -File scripts\sync-webpages.ps1

$ErrorActionPreference = 'Stop'

# 仓库根 = 本脚本所在目录的上一级。用脚本自身路径推导，不依赖 $PSScriptRoot。
$scriptPath = $MyInvocation.MyCommand.Path
$root = Split-Path -Parent (Split-Path -Parent $scriptPath)
Write-Host ('root = ' + $root)

$src = Join-Path $root 'docs\web-index.html'
$dst = Join-Path $root 'Services\WebPages.cs'

if (-not (Test-Path -LiteralPath $src)) { throw ('missing ' + $src) }

$body = [System.IO.File]::ReadAllText($src, [System.Text.Encoding]::UTF8)
if ($body.Contains('"""')) { throw 'web-index.html contains triple quote' }

$header = @'
using System;

namespace SoulLink.Services
{
    /// <summary>
    /// 网页界面（由 docs/web-index.html 生成，改样式请改那个文件）。
    ///
    /// ★ 生成命令：scripts\sync-webpages.ps1
    ///
    /// ★ 本常量只是**兜底**：主程序优先读 exe 同目录的 web-index.html，
    ///   这样改页面样式只需刷新浏览器，不用重编译、不用重启程序
    ///   （重启会断开所有 RDP 会话 —— 用户明确抱怨过）。
    /// </summary>
    internal static class WebPages
    {
        public const string Index = """
'@

$footer = @'
""";
    }
}
'@

$nl = [Environment]::NewLine
$out = $header + $nl + $body + $nl + $footer
[System.IO.File]::WriteAllText($dst, $out, (New-Object System.Text.UTF8Encoding($false)))
Write-Host ('OK ' + $dst)

# 同步到运行目录（主程序优先读这个文件）
$distDir = Join-Path $root 'dist'
if (Test-Path -LiteralPath $distDir) {
    $distHtml = Join-Path $distDir 'web-index.html'
    Copy-Item -LiteralPath $src -Destination $distHtml -Force
    Write-Host ('OK ' + $distHtml + '  <-- 刷新浏览器即可生效')
}