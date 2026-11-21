# SoulLink 运行环境检查 / 安装引导
#
# 用途：SoulLink 2.19.0 起改为「框架依赖」发布，需要 .NET 11 桌面运行时。
#       用户双击 SoulLink.exe 之前，先跑一次这个脚本做检查。
#
# 为什么必须放在 exe 外面：
#   .NET 是「先启动运行时、再加载你的程序」的模型。
#   运行时缺失时，进程连 SoulLink 的 Main 都进不去，
#   所以检查逻辑不可能写在程序内部。
#
# 用法：右键 →「使用 PowerShell 运行」
#       或命令行： powershell -ExecutionPolicy Bypass -File check-runtime.ps1

$ErrorActionPreference = 'Stop'

# ★ 需要的最低主版本号，与 SoulLink.csproj 的 TargetFramework 保持一致。
#   csproj 里已配 RollForward=LatestMajor，装了 12、13 也能跑，这里只卡下限。
$MinMajor = 11
$Framework = 'Microsoft.WindowsDesktop.App'

Write-Host ''
Write-Host '============================================' -ForegroundColor Cyan
Write-Host '  SoulLink 运行环境检查' -ForegroundColor Cyan
Write-Host '============================================' -ForegroundColor Cyan
Write-Host ''

function Get-DesktopRuntimes {
    $result = @()
    try {
        $lines = & dotnet --list-runtimes 2>$null
        foreach ($line in $lines) {
            if ($line -match ('^' + [regex]::Escape($Framework) + '\s+(\d+)\.(\d+)\.(\d+)\s+\[(.+)\]')) {
                $result += [pscustomobject]@{
                    Major = [int]$Matches[1]
                    Minor = [int]$Matches[2]
                    Patch = [int]$Matches[3]
                    Text  = ('{0}.{1}.{2}' -f $Matches[1], $Matches[2], $Matches[3])
                }
            }
        }
    } catch { }
    return $result
}

$runtimes = @(Get-DesktopRuntimes)

if ($runtimes.Count -eq 0) {
    Write-Host '  未检测到任何 .NET 桌面运行时。' -ForegroundColor Yellow
} else {
    Write-Host '  已安装的 .NET 桌面运行时：' -ForegroundColor Gray
    foreach ($rt in ($runtimes | Sort-Object Major, Minor, Patch -Descending)) {
        if ($rt.Major -ge $MinMajor) {
            Write-Host ("    [OK] $Framework $($rt.Text)") -ForegroundColor Green
        } else {
            Write-Host ("    [旧] $Framework $($rt.Text)") -ForegroundColor DarkGray
        }
    }
}

Write-Host ''

$ok = @($runtimes | Where-Object { $_.Major -ge $MinMajor })

if ($ok.Count -gt 0) {
    $best = ($ok | Sort-Object Major, Minor, Patch -Descending)[0]
    Write-Host ("  √ 可以运行 SoulLink（将使用 {0} {1}）" -f $Framework, $best.Text) -ForegroundColor Green
    Write-Host ''
    Write-Host '  直接双击 SoulLink.exe 即可。' -ForegroundColor Gray
    Write-Host ''
    exit 0
}

Write-Host ("  × 缺少 .NET {0} 或更高版本的桌面运行时" -f $MinMajor) -ForegroundColor Red
Write-Host ''
Write-Host '  要去微软官网下载吗？' -ForegroundColor Yellow
Write-Host '    · .NET 桌面运行时 是微软官方组件，约 55 MB'
Write-Host '    · 装完之后再双击 SoulLink.exe'
Write-Host ''
$answer = Read-Host '  现在打开下载页？(Y/n)'

$url = 'https://dotnet.microsoft.com/download/dotnet/11.0/runtime'

if ($answer -eq '' -or $answer -match '^[Yy]') {
    Write-Host ''
    Write-Host ('  正在打开：' + $url) -ForegroundColor Cyan
    try { Start-Process $url } catch {
        Write-Host '  自动打开失败，请手动复制下面的地址到浏览器。' -ForegroundColor Yellow
        Write-Host ('  ' + $url) -ForegroundColor Gray
    }
    Write-Host ''
    Write-Host '  ★ 下载页上请选「.NET Desktop Runtime」，不要选「.NET Runtime」。' -ForegroundColor Yellow
    Write-Host '    Desktop Runtime 才包含 WPF / WinForms 需要的组件。' -ForegroundColor Yellow
} else {
    Write-Host ''
    Write-Host '  下载地址（复制到浏览器打开）：' -ForegroundColor Gray
    Write-Host ('  ' + $url) -ForegroundColor Gray
}

Write-Host ''
exit 1
