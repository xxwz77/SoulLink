# 发布前自检：程序版本必须与分发的 exe 一致，避免"提交说明写了 2.4.0、面板还是 2.3.0"。
param([string]$Exe = "dist\SoulLink.exe")
$ErrorActionPreference = 'Stop'
$proj = [xml](Get-Content (Join-Path $PSScriptRoot '..\SoulLink.csproj') -Raw)
$want = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $want) { Write-Output 'FAIL: csproj 没有 <Version>'; exit 1 }
if (-not (Test-Path $Exe)) { Write-Output "FAIL: 找不到 $Exe"; exit 1 }
$got = (Get-Item $Exe).VersionInfo.ProductVersion
Write-Output ("csproj=" + $want + "  exe=" + $got)
if ($got -notlike "$want*") { Write-Output 'FAIL: 版本不一致，禁止发布'; exit 1 }
Write-Output 'OK: 版本一致'
