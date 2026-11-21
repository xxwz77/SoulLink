#requires -Version 7
<#
.SYNOPSIS
    SoulLink 云端发布：上传 SoulLink.exe + latest.json 到 COS，并刷新 CDN。

.DESCRIPTION
    固定文件名发布：始终上传为 SoulLink.exe（不带版本号），
    用户桌面快捷方式永远有效，升级后不需要重建。

    凭据来自本机 COSBrowser 登录态（临时 STS），不把密钥写进脚本或仓库。

    用法：
        .\scripts\publish-release.ps1              # 只检查，不上传
        .\scripts\publish-release.ps1 -Push        # 正式发布
#>
param(
    # 正式推送（不加只检查）
    [switch]$Push,
    # ★ 全量包：包含所有程序文件，供首次安装 / 强制修复使用。
    #   不加此开关时只推送「与上一版相比有变化的文件」（增量包）。
    [switch]$Full,
    # 临时覆盖 docs/RELEASE-NOTES.md
    [string]$Notes = "",
    # 系统代理可用时使用（默认直连）
    [switch]$UseProxy
)

$ErrorActionPreference = "Stop"

if (-not $UseProxy) {
    $env:HTTP_PROXY = ""; $env:HTTPS_PROXY = ""
    $env:ALL_PROXY = "";  $env:NO_PROXY = "*"
    try { [System.Net.WebRequest]::DefaultWebProxy = $null } catch { }
}

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root

$bucket    = "COS_BUCKET_REDACTED"
$region    = "ap-shanghai"
$cosHost   = "$bucket.cos.$region.myqcloud.com"
$cdnBase   = "https://cdn.example.invalid/SoulLink"
$keyPrefix = "SoulLink"

$version = (& dotnet msbuild SoulLink.csproj -getProperty:Version -nologo).Trim()
if (-not $version) { throw "读不到 SoulLink.csproj 的 <Version>" }
Write-Host ""
Write-Host "  SoulLink 云端发布" -ForegroundColor Cyan
Write-Host "  版本：$version"
Write-Host ""

$dist = Join-Path $root "dist"
if (-not (Test-Path -LiteralPath $dist)) {
    throw "找不到 $dist，请先运行 build.bat 生成发布产物"
}

# ──────────────────────────────────────────────────────────────
#  打包内容：只收「程序文件」，绝不收用户数据。
#
#  dist\data\      用户配置、日志、备份      → 排除
#  dist\stable\    挂机测试版                → 排除
#  dist\agent\     ★ 手动代理的导出目录，不是运行必需品 → 排除
#                 里面的 SoulLink.Agent.exe 是某次「导出手动代理」
#                 留下的副本（当前是 2.10.0 自包含版的旧产物，64 MB）。
#                 打包它等于给用户发一个月前的程序，会覆盖掉新版本。
#                 正确做法：用户点「导出手动代理」时用**当前版本**重新生成。
#  *.old / *.log / *.bak                     → 排除
#
#  其余（主程序、更新器、Agent、tools、7za、脚本）全部收进包，
#  这样服务安装器、代理等一起升级，而不只是换个主程序。
# ──────────────────────────────────────────────────────────────
$includeDirs  = @('', 'tools', 'next')
$excludeNames = @('data', 'stable')
$excludeExt   = @('.log', '.bak', '.old', '.tmp', '.pdb')

$files = @()
foreach ($sub in $includeDirs) {
    $dir = if ($sub -eq '') { $dist } else { Join-Path $dist $sub }
    if (-not (Test-Path -LiteralPath $dir)) { continue }
    Get-ChildItem -LiteralPath $dir -File -ErrorAction SilentlyContinue |
        Where-Object { $excludeExt -notcontains $_.Extension.ToLower() } |
        ForEach-Object {
            $rel = if ($sub -eq '') { $_.Name } else { "$sub/$($_.Name)" }
            $files += [pscustomobject]@{ Rel = $rel; Full = $_.FullName }
        }
}
if ($files.Count -eq 0) { throw "没有收集到任何程序文件" }

# 主程序必须存在于全量集合中（增量包本身可能只含其他文件，属正常）
if (-not ($files | Where-Object { $_.Rel -eq 'SoulLink.exe' })) {
    throw "dist 里缺少 SoulLink.exe，请先运行 build.bat"
}

# ──────────────────────────────────────────────────────────────
#  增量包：只推送与「上一次发布」相比有变化的文件。
#
#  比对依据是 artifactspackageilelist.json —— 记录上次推送时
#  每个文件的 SHA256。发布脚本在打包时写入，-Full 时整体重建。
#
#  ★ 为什么用哈希而不是时间戳：
#    build.bat 每次都会重新生成 exe，时间戳必然变化，
#    但内容可能完全相同（只改了注释）。按哈希比对能避免无谓的推送。
# ──────────────────────────────────────────────────────────────
$fileListPath = Join-Path $root "artifacts\package\filelist.json"
$prev = @{}
if (Test-Path -LiteralPath $fileListPath) {
    try {
        $j = Get-Content -LiteralPath $fileListPath -Raw | ConvertFrom-Json
        foreach ($pr in $j.files.PSObject.Properties) { $prev[$pr.Name] = $pr.Value }
    } catch { $prev = @{} }
}

$current = @{}
foreach ($f in $files) {
    $current[$f.Rel] = (Get-FileHash -LiteralPath $f.Full -Algorithm SHA256).Hash.ToLower()
}

if ($Full) {
    Write-Host "  模式：全量包（所有程序文件）" -ForegroundColor Cyan
} else {
    $changed = $files | Where-Object {
        -not $prev.ContainsKey($_.Rel) -or $prev[$_.Rel] -ne $current[$_.Rel]
    }
    $removed = $prev.Keys | Where-Object { -not $current.ContainsKey($_) }
    $files = @($changed)
    Write-Host "  模式：增量包（仅变化的文件）" -ForegroundColor Cyan
    if ($removed) {
        Write-Host ("  注意：以下文件在上版存在，本版已移除（增量包不会删除它们）：" ) -ForegroundColor Yellow
        $removed | ForEach-Object { Write-Host ("    - " + $_) -ForegroundColor Yellow }
    }
    if ($files.Count -eq 0) {
        Write-Host "  没有文件变化，无需推送。" -ForegroundColor Green
        exit 0
    }
}


Write-Host ""
Write-Host "  打包内容（$($files.Count) 个文件）：" -ForegroundColor Cyan
$files | Sort-Object Rel | ForEach-Object { Write-Host ("    " + $_.Rel) }
Write-Host ""

# 用 7za 打 zip（自带工具；.NET 的 Compress-Archive 对大文件很慢）
$seven = Join-Path $root "tools\7za\7za.exe"
if (-not (Test-Path -LiteralPath $seven)) { throw "找不到 $seven" }

$pkgDir = Join-Path $root "artifacts\package"
if (Test-Path -LiteralPath $pkgDir) { Remove-Item -LiteralPath $pkgDir -Recurse -Force }
New-Item -ItemType Directory -Path $pkgDir -Force | Out-Null

# 7za 需要真实目录结构，先把要打包的文件镜像到临时目录
$stage = Join-Path $pkgDir "stage"
foreach ($f in $files) {
    $dst = Join-Path $stage ($f.Rel -replace '/', '\')
    $parent = Split-Path -Parent $dst
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    Copy-Item -LiteralPath $f.Full -Destination $dst -Force
}

# ★ 更新包名带版本号：内容与文件名一一对应，永不覆盖。
#   这样 CDN 缓存多久都是正确的 —— 不存在「清单是新的、包是旧的」。
#   文件名用户看不到（下载到临时目录解压后即删），不影响快捷方式。
$zipName = "update-$version.zip"
$zipPath = Join-Path $pkgDir $zipName
& $seven a -tzip -mx=5 "$zipPath" "$stage\*" -r | Out-Null
if (-not (Test-Path -LiteralPath $zipPath)) { throw "打包失败" }

# 7za 会把 stage 层级带进包，重新打一次让文件位于包根
Remove-Item -LiteralPath $zipPath -Force
Push-Location $stage
try {
    & $seven a -tzip -mx=5 "$zipPath" "*" -r | Out-Null
} finally { Pop-Location }

Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue

$zipBytes = [IO.File]::ReadAllBytes($zipPath)
$sha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLower()
$exeVer = (Get-Item -LiteralPath (Join-Path $dist "SoulLink.exe")).VersionInfo.FileVersion
if ($exeVer -and $exeVer -notlike "$version.*") {
    throw "产物版本 $exeVer 与工程版本 $version 不一致，请重新 build.bat"
}

# ---------- 更新说明 ----------
if (-not $Notes) {
    $notesFile = Join-Path $root "docs\RELEASE-NOTES.md"
    if (-not (Test-Path -LiteralPath $notesFile)) {
        throw "找不到 $notesFile，或用 -Notes 直接指定"
    }
    $raw = Get-Content -LiteralPath $notesFile -Raw
    # 只保留「## 本次更新」这个二级标题之后的正文。
    # 不能简单找第一个 '## '：模板顶部的说明里也可能出现该字样。
    $nlines = $raw -split "`r?`n"
    $startIdx = -1
    for ($i = 0; $i -lt $nlines.Count; $i++) {
        if ($nlines[$i] -match '^##\s*本次更新\s*$') { $startIdx = $i + 1; break }
    }
    if ($startIdx -lt 0) { throw 'docs/RELEASE-NOTES.md 里找不到 本次更新 标题' }
    $Notes = ($nlines[$startIdx..($nlines.Count - 1)] -join [char]13 + [string][char]10).Trim()
}
$manifest = [ordered]@{
    version     = $version
    notes       = $Notes
    file        = $zipName
    size        = $zipBytes.Length
    sha256      = $sha256
    publishedAt = (Get-Date).ToString("yyyy-MM-ddTHH:mm:sszzz")
    minRuntime  = "11.0"
    full        = [bool]$Full
}
$json = ($manifest | ConvertTo-Json -Depth 4)
$jsonBytes = [Text.Encoding]::UTF8.GetBytes($json)

Write-Host "  更新包：$zipPath"
Write-Host "  大小：$($zipBytes.Length) 字节"
Write-Host "  SHA256：$sha256"
Write-Host ""

if (-not $Push) {
    Write-Host "  未指定 -Push：仅检查，未上传。" -ForegroundColor Yellow
    Write-Host "  正式发布：.\scripts\publish-release.ps1 -Push"
    Write-Host ""
    Write-Host "  将要写入的清单：" -ForegroundColor DarkGray
    Write-Host $json -ForegroundColor DarkGray
    exit 0
}

# ---------- 凭据 ----------
$keysPath = Join-Path $env:APPDATA "cosbrowser\cosbrowser_keys.json"
if (-not (Test-Path -LiteralPath $keysPath)) {
    throw "找不到 COSBrowser 凭据：$keysPath"
}
$obj = Get-Content -LiteralPath $keysPath -Raw | ConvertFrom-Json
$enc = [Convert]::FromBase64String($obj.keys)
$aes = [System.Security.Cryptography.Aes]::Create()
$aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
$aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
$aes.Key = [Text.Encoding]::UTF8.GetBytes("cosbrowser_key_secret_xx")
$aes.IV  = [Text.Encoding]::UTF8.GetBytes("cosbrowser.infox")
$plain = [Text.Encoding]::UTF8.GetString($aes.CreateDecryptor().TransformFinalBlock($enc, 0, $enc.Length))
$session = (ConvertFrom-Json $plain).lastSession

$script:ak = $session.tmpSecretId
$script:sk = $session.tmpSecretKey
$script:token = $session.token
if (-not $script:ak -or -not $script:sk -or -not $script:token) {
    throw "COSBrowser 没有可用凭据，请打开并登录"
}

function Refresh-CosCredential {
    Write-Host "  凭据已过期，刷新中…" -ForegroundColor DarkGray
    $form = @{
        clientId    = $session.clientId
        clientToken = $session.clientToken
        userOpenId  = $session.userOpenId
        timestamp   = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    }
    $ra = @{
        Uri = "https://cos5.cloud.tencent.com/cosbrowser/api/oauth/credential"
        Method = "Post"; Body = $form
        ContentType = "application/x-www-form-urlencoded"; TimeoutSec = 20
    }
    if (-not $UseProxy) { $ra.NoProxy = $true }
    $resp = Invoke-RestMethod @ra
    if ($null -eq $resp -or $resp.code -ne 0) {
        throw "凭据刷新失败，请重新登录 COSBrowser"
    }
    $script:ak = $resp.data.credentials.tmpSecretId
    $script:sk = $resp.data.credentials.tmpSecretKey
    $script:token = $resp.data.credentials.token
    $script:session = $resp.data
    Write-Host "  凭据已刷新" -ForegroundColor DarkGray
}

$exp = [DateTimeOffset]::FromUnixTimeSeconds([long]$session.expiredTime).ToLocalTime()
if ($exp -lt (Get-Date).AddMinutes(3)) { Refresh-CosCredential }

# ---------- 工具 ----------
function New-Http {
    if ($UseProxy) { $c = [System.Net.Http.HttpClient]::new() }
    else {
        $h = [System.Net.Http.HttpClientHandler]::new()
        $h.UseProxy = $false
        $c = [System.Net.Http.HttpClient]::new($h)
    }
    $c.Timeout = [TimeSpan]::FromSeconds(180)
    return $c
}
function Encode-Part([string]$s) { [uri]::EscapeDataString($s) }
function Get-Sha1Hex([string]$s) {
    $h = [System.Security.Cryptography.SHA1]::Create()
    return (($h.ComputeHash([Text.Encoding]::UTF8.GetBytes($s)) | ForEach-Object { $_.ToString("x2") }) -join "")
}
function Get-HmacHex([string]$key, [string]$data) {
    $h = [System.Security.Cryptography.HMACSHA1]::new([Text.Encoding]::UTF8.GetBytes($key))
    return (($h.ComputeHash([Text.Encoding]::UTF8.GetBytes($data)) | ForEach-Object { $_.ToString("x2") }) -join "")
}

function Put-CosObject([string]$keyName, [byte[]]$body, [string]$contentType) {
    $segments = $keyName -split "/"
    $urlPath  = "/" + (($segments | ForEach-Object { Encode-Part $_ }) -join "/")
    $signPath = "/" + ($segments -join "/")

    # ★ 这里踩过一个坑：不能直接用 $body，因为 PowerShell 的函数参数
    #   在嵌套调用里会被子作用域的同名变量污染。先复制到局部变量再算哈希。
    $payload = [byte[]]$body
    if ($null -eq $payload) { throw "内部错误：$keyName 的内容为空（参数绑定失败）" }
    $len = $payload.Length
    $md5raw = [System.Security.Cryptography.MD5]::HashData($payload)
    $md5 = [Convert]::ToBase64String($md5raw)
    $headerNames = "content-length;content-md5;content-type;host;x-cos-channel-id"
    $headerVals = "content-length=$len" +
        "&content-md5=$(Encode-Part $md5)" +
        "&content-type=$(Encode-Part $contentType)" +
        "&host=$(Encode-Part $cosHost)" +
        "&x-cos-channel-id=cosbrowser_pc"

    $start = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - 60
    $end   = [Math]::Min([long]$script:session.expiredTime, $start + 3600)
    $keyTime = "$start;$end"
    $signKey = Get-HmacHex $script:sk $keyTime
    $httpString = "put`n$signPath`n`n$headerVals`n"
    $sig = Get-HmacHex $signKey ("sha1`n$keyTime`n" + (Get-Sha1Hex $httpString) + "`n")

    $auth = "q-sign-algorithm=sha1&q-ak=$($script:ak)&q-sign-time=$keyTime" +
        "&q-key-time=$keyTime&q-header-list=$headerNames" +
        "&q-url-param-list=&q-signature=$sig"

    $client = New-Http
    $req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Put, "https://$cosHost$urlPath")
    [void]$req.Headers.TryAddWithoutValidation("Authorization", $auth)
    [void]$req.Headers.TryAddWithoutValidation("x-cos-security-token", $script:token)
    [void]$req.Headers.TryAddWithoutValidation("x-cos-channel-id", "cosbrowser_pc")

    $content = [System.Net.Http.ByteArrayContent]::new($payload)
    [void]$content.Headers.TryAddWithoutValidation("Content-Type", $contentType)
    [void]$content.Headers.TryAddWithoutValidation("Content-MD5", $md5)
    $content.Headers.ContentLength = $len
    $req.Content = $content

    $resp = $client.SendAsync($req).GetAwaiter().GetResult()
    $code = [int]$resp.StatusCode
    if ($code -ne 200) {
        $detail = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        throw "上传失败 $keyName：HTTP $code`n$detail"
    }
    Write-Host "  OK  $keyName（$len 字节）" -ForegroundColor Green
}

function Get-Sha256Hex([string]$s) {
    $h = [System.Security.Cryptography.SHA256]::Create()
    return (($h.ComputeHash([Text.Encoding]::UTF8.GetBytes($s)) | ForEach-Object { $_.ToString("x2") }) -join "")
}
function Get-HmacBytes([byte[]]$key, [string]$data) {
    $h = [System.Security.Cryptography.HMACSHA256]::new($key)
    return $h.ComputeHash([Text.Encoding]::UTF8.GetBytes($data))
}

function Clear-CdnCache([string[]]$paths) {
    if (-not $paths -or $paths.Count -eq 0) { return }
    $cdnHost = "cdn.tencentcloudapi.com"
    $service = "cdn"; $action = "PurgePathCache"; $apiVer = "2018-06-06"; $rgn = "ap-guangzhou"
    $q = [char]34
    $pathJson = ($paths | ForEach-Object { $q + $_ + $q }) -join ','
    $payload = "{`"Paths`":[`" + $pathJson + `"],`"FlushType`":`"flush`"}"
    Write-Host ("  CDN 刷新：" + ($paths -join ', ')) -ForegroundColor DarkGray

    $ts = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $date = [DateTimeOffset]::UtcNow.ToString("yyyy-MM-dd")
    $ct = "application/json; charset=utf-8"
    $canonHeaders = "content-type:$ct`nhost:$cdnHost`nx-tc-action:$($action.ToLower())`n"
    $signed = "content-type;host;x-tc-action"
    $canonReq = "POST`n/`n`n$canonHeaders`n$signed`n$(Get-Sha256Hex $payload)"
    $scope = "$date/$service/tc3_request"
    $toSign = "TC3-HMAC-SHA256`n$ts`n$scope`n$(Get-Sha256Hex $canonReq)"
    $kDate = Get-HmacBytes ([Text.Encoding]::UTF8.GetBytes("TC3$($script:sk)")) $date
    $kSvc  = Get-HmacBytes $kDate $service
    $kSign = Get-HmacBytes $kSvc "tc3_request"
    $sig = ((Get-HmacBytes $kSign $toSign) | ForEach-Object { $_.ToString("x2") }) -join ""
    $auth = "TC3-HMAC-SHA256 Credential=$($script:ak)/$scope, SignedHeaders=$signed, Signature=$sig"

    $client = New-Http
    $client.Timeout = [TimeSpan]::FromSeconds(30)
    $req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, "https://$cdnHost/")
    [void]$req.Headers.TryAddWithoutValidation("Authorization", $auth)
    [void]$req.Headers.TryAddWithoutValidation("X-TC-Action", $action)
    [void]$req.Headers.TryAddWithoutValidation("X-TC-Version", $apiVer)
    [void]$req.Headers.TryAddWithoutValidation("X-TC-Timestamp", "$ts")
    [void]$req.Headers.TryAddWithoutValidation("X-TC-Region", $rgn)
    [void]$req.Headers.TryAddWithoutValidation("X-TC-Token", $script:token)
    $req.Content = [System.Net.Http.StringContent]::new($payload, [Text.Encoding]::UTF8, "application/json")

    $resp = $client.SendAsync($req).GetAwaiter().GetResult()
    $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if ([int]$resp.StatusCode -ne 200) {
        Write-Warning "  CDN 刷新失败：HTTP $([int]$resp.StatusCode) $body"
        return
    }
    $tid = (ConvertFrom-Json $body).Response.TaskId
    Write-Host "  CDN 刷新任务：$tid（约 1~3 分钟生效）" -ForegroundColor DarkGray
}

# ---------- 上传：先 exe，再清单 ----------
Write-Host "  开始上传…" -ForegroundColor Cyan

Put-CosObject "$keyPrefix/$zipName" $zipBytes "application/zip"

$probeUrl = "$cdnBase/$zipName"
$ok = $false
for ($i = 1; $i -le 5; $i++) {
    try {
        $h = Invoke-WebRequest -Uri ($probeUrl + "?probe=" + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() + "-$i") -Method Head -TimeoutSec 25 -UseBasicParsing -ErrorAction Stop
        $rawLen = $h.Headers["Content-Length"]
        if ($rawLen -is [array]) { $rawLen = $rawLen[0] }
        $got = 0; [void][int]::TryParse([string]$rawLen, [ref]$got)
        if ([int]$h.StatusCode -eq 200 -and $got -eq $zipBytes.Length) {
            Write-Host "  下载校验通过（$got 字节）" -ForegroundColor Green
            $ok = $true; break
        }
        Write-Warning "  校验未通过：HTTP $([int]$h.StatusCode) / $got 字节（期望 $($zipBytes.Length)）"
    } catch {
        Write-Warning "  探测失败（第 $i 次）：$($_.Exception.Message)"
    }
    Start-Sleep -Seconds 5
}
if (-not $ok) {
    throw "更新包在 CDN 上不可访问，已中止；未写入 latest.json，现有用户不受影响。"
}

Put-CosObject "$keyPrefix/latest.json" $jsonBytes "application/json"

# ──────────────────────────────────────────────────────────────
#  记录本次推送后的完整文件清单，供下次做增量比对。
#  ★ 必须在上传成功后才写：上传失败时清单不能前进，
#    否则下次计算增量会把没推上去的文件当成"已推送"而漏掉。
# ──────────────────────────────────────────────────────────────
try {
    $all = @{}
    foreach ($f in $files) { $all[$f.Rel] = $current[$f.Rel] }
    if (-not $Full) { foreach ($k in $prev.Keys) { if (-not $all.ContainsKey($k)) { $all[$k] = $prev[$k] } } }
    $fl = [ordered]@{
        version = $version
        files   = $all
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $fileListPath) -Force | Out-Null
    ($fl | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $fileListPath -Encoding UTF8
    Write-Host "  文件清单已更新：$fileListPath" -ForegroundColor DarkGray
} catch {
    Write-Warning "  文件清单写入失败：$($_.Exception.Message)"
}
# ★ 只刷新清单。版本化的 zip 内容不可变，无需 purge。
# ★ 清理历史遗留的固定名包。它内容是旧的，留着只会让人误以为是最新版。
#   版本化包（update-<ver>.zip）不受影响。
try {
    $delClient = New-Http
    $delClient.Timeout = [TimeSpan]::FromSeconds(20)
    $st2 = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - 60
    $en2 = [Math]::Min([long]$script:session.expiredTime, $st2 + 3600)
    $kt2 = "$st2;$en2"
    $sk2 = Get-HmacHex $script:sk $kt2
    $sp2 = "/$keyPrefix/update.zip"
    $hv2 = "host=$(Encode-Part $cosHost)"
    $hs2 = "delete" + [char]10 + $sp2 + [char]10 + [char]10 + $hv2 + [char]10
    $sig2 = Get-HmacHex $sk2 ("sha1" + [char]10 + $kt2 + [char]10 + (Get-Sha1Hex $hs2) + [char]10)
    $auth2 = "q-sign-algorithm=sha1&q-ak=$($script:ak)&q-sign-time=$kt2&q-key-time=$kt2&q-header-list=host&q-url-param-list=&q-signature=$sig2"
    $reqDel = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Delete, "https://$cosHost$sp2")
    [void]$reqDel.Headers.TryAddWithoutValidation("Authorization", $auth2)
    [void]$reqDel.Headers.TryAddWithoutValidation("x-cos-security-token", $script:token)
    $respDel = $delClient.SendAsync($reqDel).GetAwaiter().GetResult()
    $codeDel = [int]$respDel.StatusCode
    if ($codeDel -eq 204 -or $codeDel -eq 200) { Write-Host "  已删除历史固定名包 update.zip" -ForegroundColor DarkGray }
    elseif ($codeDel -eq 404) { Write-Host "  历史固定名包不存在，跳过" -ForegroundColor DarkGray }
    else { Write-Warning "  清理历史包失败：HTTP $codeDel" }
} catch {
    Write-Warning "  清理历史包失败：$($_.Exception.Message)"
}
Clear-CdnCache @("$cdnBase/latest.json")

Write-Host ""
try {
    $body = (Invoke-WebRequest -Uri "$cdnBase/latest.json?v=$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())" -TimeoutSec 20 -UseBasicParsing -ErrorAction Stop).Content
    $remoteVer = (ConvertFrom-Json $body).version
    if ($remoteVer -eq $version) {
        Write-Host "  CDN 已生效：latest.json -> $remoteVer" -ForegroundColor Green
    } else {
        Write-Warning "  CDN 仍是旧版本：$remoteVer（期望 $version）"
        Write-Warning "  缓存未刷新，通常 1~5 分钟生效；不要重复推送。"
    }
} catch {
    Write-Warning "  自检失败：$($_.Exception.Message)"
}

Write-Host ""
Write-Host "  发布完成：$version" -ForegroundColor Green
Write-Host "  用户下次启动会看到更新提示。"
Write-Host ""
