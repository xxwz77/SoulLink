$ErrorActionPreference='Stop'
$exitFile = Join-Path $env:TEMP 'SoulLink-Broker-install.exit'
Stop-Service SoulLink.DevBroker -Force -ErrorAction SilentlyContinue
sc.exe delete SoulLink.DevBroker | Out-Null
if (Get-Service SoulLink.DevBroker -ErrorAction SilentlyContinue) { throw '测试服务仍存在' }
$path = Join-Path $env:ProgramFiles 'SoulLinkDev'
if (Test-Path $path) { Remove-Item $path -Recurse -Force }
Write-Output '测试版服务已卸载'
Set-Content $exitFile 0
