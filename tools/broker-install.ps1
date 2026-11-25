#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$SourceExe)
$ErrorActionPreference = 'Stop'
$log = Join-Path $env:TEMP 'SoulLink-Broker-install.log'

# 2026-11-23 重写：本脚本不再手工复制 exe / 改清单 / 建服务，
# 这些逻辑的唯一正源是 tools\broker-setup\SoulLink.BrokerSetup.exe（--install <source>）。
# 原因：旧脚本手工 Copy-Item 只带 SoulLink.exe 与 SoulLink.Agent.exe，
# 漏掉旁挂运行库（Vortice.* / SharpGen.Runtime* / WinRT.Runtime /
# Microsoft.Windows.SDK.NET.dll）与 web-index.html、7za.exe、tools\，
# 导致真实 Agent 的 /stream 因加载不到 Vortice.MediaFoundation 而连接重置。
# 现在这些同步逻辑已固化进 BrokerSetup 的 SyncRuntimeFiles，脚本只做校验 + 转发。
try {
    if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw '需要管理员终端'
    }
    if (-not $SourceExe) { throw '缺少 -SourceExe' }
    $source = (Resolve-Path -LiteralPath $SourceExe).Path
    $leaf = Split-Path $source -Leaf
    if ($leaf -ne 'SoulLink.exe' -and $leaf -ne 'SoulLink-Stable.exe') {
        throw '仅允许 SoulLink.exe / SoulLink-Stable.exe'
    }

    $tool = Join-Path (Split-Path $source -Parent) 'tools\SoulLink.BrokerSetup.exe'
    if (-not (Test-Path -LiteralPath $tool)) {
        throw "缺少独立安装器：$tool"
    }

    & $tool --install $source
    if ($LASTEXITCODE -ne 0) {
        throw "独立安装器返回 $LASTEXITCODE；请查看 $log 与 $env:TEMP\SoulLink-Broker-install-current.log"
    }

    Write-Output 'SoulLink.DevBroker 已安装/更新并启动（含旁挂运行库与 web-index.html）'
    exit 0
}
catch {
    try { [IO.File]::AppendAllText($log, $_.Exception.ToString() + [Environment]::NewLine, [Text.UTF8Encoding]::new($false)) } catch { }
    Write-Error $_
    exit 1
}
