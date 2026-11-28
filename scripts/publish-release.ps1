#requires -Version 7
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PWD 'artifacts/github-release'),
    [string]$Tag,
    [switch]$ValidateNotesOnly
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
$work = $null

function Build-Package([string]$Project, [string]$Destination, [string[]]$Additional = @()) {
    dotnet publish $Project -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $Destination --nologo @Additional
    if ($LASTEXITCODE -ne 0) { throw "构建失败 $Project" }
}

function Get-Version([string]$Project, [string]$Property = 'Version') {
    $value = (& dotnet msbuild $Project "-getProperty:$Property" -nologo | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $value -cnotmatch '^\d+\.\d+\.\d+$') { throw "版本无效 $Project $Property" }
    return $value
}

function Assert-ReleaseNotes([string]$Path, [string]$Version) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "缺少更新说明 $Path" }
    $markdown = (Get-Content -LiteralPath $Path -Raw -Encoding utf8) -replace '(?s)<!--.*?-->', ''
    $sections = [regex]::Matches($markdown, '(?m)^##[ \t]+(.+?)[ \t]*\r?$')
    $expected = "v$Version"
    $matching = @($sections | Where-Object { $_.Groups[1].Value -ceq $expected })
    if ($matching.Count -ne 1 -or $sections[0].Groups[1].Value -cne $expected) {
        throw "请在 $Path 顶部添加唯一的「## $expected」版本章节，保留已有历史。"
    }

    $start = $sections[0].Index + $sections[0].Length
    $end = if ($sections.Count -gt 1) { $sections[1].Index } else { $markdown.Length }
    $content = $markdown.Substring($start, $end - $start) -split '\r?\n' | Where-Object {
        $line = $_.Trim() -replace '^(?:[-*+]|\d+\.)[ \t]+', ''
        $line = $line -replace '[*_`~]', ''
        $line -match '[\p{L}\p{N}]' -and $line -notmatch '^(?:#|>).*|^(?:TODO|TBD|待补充|待填写)[\p{P}\s]*$'
    }
    if (-not $content) { throw "$Path 的 $expected 章节需要填写本版实际更新内容。" }
}

function Copy-Required([string]$Source, [string]$Target) {
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf) -or (Get-Item -LiteralPath $Source).Length -le 0) {
        throw "缺少构建文件 $Source"
    }
    Copy-Item -LiteralPath $Source -Destination $Target
}

function Assert-Version([string]$Path, [string]$Name, [string]$Version) {
    $info = (Get-Item -LiteralPath $Path).VersionInfo
    if ($info.OriginalFilename -cne "$Name.dll" -or $info.FileVersion -cne "$Version.0") {
        throw "程序标识或版本错误 $Path"
    }
}

Push-Location $root
try {
    $version = Get-Version SoulLink.csproj
    if ($Tag -and $Tag -cne "v$version") { throw "标签应为 v$version" }
    $notes = 'docs/RELEASE-NOTES.md'
    Assert-ReleaseNotes $notes $version
    if ($ValidateNotesOnly) {
        Write-Host "已核对 v$version 更新说明及历史文件。"
        return
    }
    $agentVersion = Get-Version Agent/SoulLink.Agent.csproj
    $brokerVersion = Get-Version Agent/SoulLink.Broker.csproj
    if ((Get-Version SoulLink.csproj AgentVersion) -ne $agentVersion) { throw '主程序和代理的版本要求不一致' }
    $work = [IO.Directory]::CreateTempSubdirectory('SoulLink-Release-')
    $core = Join-Path $work.FullName 'core'
    $client = Join-Path $work.FullName 'client'
    $agent = Join-Path $work.FullName 'agent'
    $broker = Join-Path $work.FullName 'broker'
    $built = Join-Path $work.FullName 'tools'
    New-Item -Type Directory $core, $agent, $broker, $built, "$core/tools", $output -Force | Out-Null

    Build-Package SoulLink.csproj $client @('-f', 'net11.0-windows10.0.26100.0', '-p:BuildReleaseTools=false')
    Build-Package Agent/SoulLink.Agent.csproj "$built/agent" @('-p:BaseIntermediateOutputPath=obj/agent-release/')
    Build-Package Agent/SoulLink.Broker.csproj "$built/broker" @('-p:BaseIntermediateOutputPath=obj/broker-release/')
    Build-Package tools/updater/SoulLink.Updater.csproj "$built/updater"
    Build-Package tools/rescue/Rescue.csproj "$built/rescue"
    Build-Package tools/watchdog/SoulLink.Watchdog.csproj "$built/watchdog"
    Build-Package tools/broker-setup/SoulLink.BrokerSetup.csproj "$built/broker-setup"
    Build-Package tools/taskedit/SoulLink.TaskEdit.csproj "$built/taskedit"

    Assert-Version "$client/SoulLink.exe" SoulLink $version
    Assert-Version "$built/agent/SoulLink.Agent.exe" SoulLink.Agent $agentVersion
    Assert-Version "$built/broker/SoulLink.Broker.exe" SoulLink.Broker $brokerVersion
    foreach ($name in 'SoulLink.exe','SoulLink.Agent.required.version','Microsoft.Windows.SDK.NET.dll','WinRT.Runtime.dll') {
        Copy-Required "$client/$name" $core
    }
    Get-ChildItem -LiteralPath $client -File | Where-Object {
        $_.Name -match '^(Vortice\..+|SharpGen\..+|Microsoft\.Windows\.UI\.Xaml|e_sqlite3)\.dll$'
    } | ForEach-Object { Copy-Required $_.FullName $core }
    Copy-Required 'tools/7za/7za.exe' $core
    Copy-Required 'README.md' $core
    Copy-Required 'THIRD-PARTY-NOTICES.txt' $core
    Copy-Required 'scripts/check-runtime.ps1' $core
    Copy-Required 'docs/web-index.html' $core
    Copy-Required "$built/updater/SoulLink.Updater.exe" $core
    Copy-Required "$built/rescue/sl-rescue.exe" $core
    Copy-Required "$built/watchdog/sl-watchdog.exe" $core
    Copy-Required "$built/taskedit/SoulLink.TaskEdit.exe" $core
    Copy-Required "$built/taskedit/SoulLink.TaskEdit.version" $core
    Copy-Required "$built/broker-setup/SoulLink.BrokerSetup.exe" "$core/tools"
    Copy-Required "$built/agent/SoulLink.Agent.exe" $agent
    Copy-Required "$built/agent/SoulLink.Agent.version" $agent
    Copy-Required "$built/broker/SoulLink.Broker.exe" $broker

    $assets = [ordered]@{
        "update-$version.zip" = $core
        "agent-$agentVersion.zip" = $agent
        "broker-$brokerVersion.zip" = $broker
    }
    foreach ($name in $assets.Keys) {
        $target = Join-Path $output $name
        Compress-Archive -Path "$($assets[$name])/*" -DestinationPath $target -CompressionLevel Optimal -Force
    }

    Get-ChildItem -LiteralPath $agent, $broker -File | Copy-Item -Destination $core
    $full = "SoulLink-$version-win-x64.zip"
    Compress-Archive -Path "$core/*" -DestinationPath (Join-Path $output $full) -CompressionLevel Optimal -Force
    foreach ($name in @($assets.Keys) + $full) {
        $file = Get-Item -LiteralPath (Join-Path $output $name)
        Write-Host "$($file.Name)  $($file.Length) bytes"
    }
    Copy-Required $notes $output
}
finally {
    Pop-Location
    if ($null -ne $work -and $work.Parent.FullName -eq [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) -and
        $work.Name.StartsWith('SoulLink-Release-', [StringComparison]::Ordinal)) { $work.Delete($true) }
}
