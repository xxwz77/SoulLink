# CDP 运行时抓证脚本（云原神/向日葵网页形态探针）
# 用法：
#   powershell -ExecutionPolicy Bypass -File scripts/cdp-probe.ps1 -Port 9222 [-Watch] [-WatchTimeoutMin 30] [-ProbeSeconds 20] [-Match 'ysh|mihoyo|sunlogin|oray']
#   -Port          CDP 调试端口（默认 9222）
#   -Watch         轮询 /json/list 直到出现 -Match 匹配的页面再抓（用户还没登录目标页时用）
#   -Match         目标页 URL/标题正则（默认 'ysh.mihoyo.com|sunlogin|oray'）
#   -ProbeSeconds  WS 帧/getStats 差分采样窗口秒数（默认 20）
# 输出：JSON 到 stdout + docs\research\cdp-probe-last.json
# 原理：System.Net.WebSockets.ClientWebSocket 直连 page 的 webSocketDebuggerUrl，
#       Runtime.evaluate / Runtime.queryObjects / Network.webSocketFrame* 全只读，不注入不登录。
param(
    [int]$Port = 9222,
    [switch]$Watch,
    [int]$WatchTimeoutMin = 30,
    [int]$ProbeSeconds = 20,
    [string]$Match = 'ysh\.mihoyo\.com|sunlogin|oray'
)
$ErrorActionPreference = 'Stop'

# ── HTTP 侧：枚举标签页 ──────────────────────────────
function Get-CdpTabs([int]$Port) {
    $resp = Invoke-RestMethod -Uri ("http://127.0.0.1:{0}/json/list" -f $Port) -TimeoutSec 5
    return @($resp | Where-Object { $_.type -eq 'page' -and $_.webSocketDebuggerUrl })
}
function Test-PortAlive([int]$Port) {
    try { Invoke-RestMethod -Uri ("http://127.0.0.1:{0}/json/version" -f $Port) -TimeoutSec 3 | Out-Null; return $true } catch { return $false }
}

if (-not (Test-PortAlive $Port)) {
    Write-Error "CDP 端口 $Port 未开放。请先以 --remote-debugging-port=$Port --user-data-dir=%TEMP%\edge-debug-profile 启动 Edge 并登录目标页。"
    exit 2
}

# ── Watch：等目标页出现 ──────────────────────────────
$deadline = (Get-Date).AddMinutes($WatchTimeoutMin)
$target = $null
while ($true) {
    $tabs = Get-CdpTabs $Port
    $target = $tabs | Where-Object { $_.url -match $Match -or $_.title -match $Match } | Select-Object -First 1
    if ($target) { break }
    if (-not $Watch) {
        Write-Host ("未找到匹配 -Match '{0}' 的页面。当前标签页：" -f $Match)
        $tabs | ForEach-Object { Write-Host ("  " + $_.title + " | " + $_.url) }
        exit 3
    }
    if ((Get-Date) -gt $deadline) { Write-Error "Watch 超时（$WatchTimeoutMin 分钟）无目标页"; exit 4 }
    Start-Sleep -Seconds 5
}
Write-Host ("目标页：" + $target.title + " | " + $target.url) -ForegroundColor Cyan

# ── WebSocket 侧：CDP 会话 ──────────────────────────
Add-Type -AssemblyName System.Net.Http
$ws = [System.Net.WebSockets.ClientWebSocket]::new()
$ct = [System.Threading.CancellationToken]::None
$ws.ConnectAsync([Uri]$target.webSocketDebuggerUrl, $ct).GetAwaiter().GetResult()
if ($ws.State -ne [System.Net.WebSockets.WebSocketState]::Open) { Write-Error "CDP ws 连接失败"; exit 5 }

$script:msgId = 0
$script:pending = [System.Collections.Concurrent.ConcurrentDictionary[int, object]]::new()
$script:wsFrames = [System.Collections.Concurrent.ConcurrentQueue[object]]::new()

# 接收泵：一个后台任务持续读，分发 id 响应与事件
# 接收泵（同步协作式）：Send-Cdp 等待响应与 Start-Sleep 窗口里泵消息
$script:rxBuffer = New-Object byte[] 1048576
$script:rxTask = $null    # 单一在途 ReceiveAsync（复用，避免重复发起）
function Pump-CdpMessages([int]$MaxMs = 200) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $MaxMs -and $ws.State -eq 'Open') {
        if (-not $script:rxTask) {
            $script:rxTask = $ws.ReceiveAsync([ArraySegment[byte]]::new($script:rxBuffer), [System.Threading.CancellationToken]::None)
        }
        $remain = [Math]::Max(30, $MaxMs - [int]$sw.ElapsedMilliseconds)
        $t = $script:rxTask
        if (-not $t.Wait($remain)) { return }   # 窗口内没消息：留着下次继续等
        $script:rxTask = $null
        $text = [Text.Encoding]::UTF8.GetString($script:rxBuffer, 0, $t.Result.Count)
        $obj = $text | ConvertFrom-Json
        if ($obj.id) {
            $done = $null
            if ($script:pending.TryRemove([int]$obj.id, [ref]$done)) {
                $done.Result = $obj
                try { $done.Sem.Release() } catch {}
            }
        } elseif ($obj.method -eq 'Network.webSocketFrameSent' -or $obj.method -eq 'Network.webSocketFrameReceived') {
            $script:wsFrames.Enqueue([pscustomobject]@{
                dir = if ($obj.method -like '*Sent') { 'sent' } else { 'recv' }
                t   = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
                len = ($obj.params.response.payloadData | Measure-Object -Property Length).Count
            })
        }
    }
}

function Send-Cdp([string]$Method, [hashtable]$Params = @{}) {
    $script:msgId++
    $sem = [System.Threading.SemaphoreSlim]::new(0, 1)
    $state = [pscustomobject]@{ Result = $null; Sem = $sem }
    $script:pending[$script:msgId] = $state
    $msg = @{ id = $script:msgId; method = $Method; params = $Params } | ConvertTo-Json -Depth 6 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($msg)
    $ws.SendAsync([ArraySegment[byte]]::new($bytes), [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $ct).GetAwaiter().GetResult()
    $deadlineCdp = ([DateTime]::Now).AddSeconds(15)
    while (-not $sem.Wait(100)) {
        Pump-CdpMessages 100
        if ([DateTime]::Now -gt $deadlineCdp) { throw "CDP 调用超时: $Method" }
    }
    if ($state.Result.error) { throw ("CDP 错误: " + $state.Result.error.message) }
    return $state.Result.result
}
function Eval-Js([string]$Expression) {
    $r = Send-Cdp 'Runtime.evaluate' @{ expression = $Expression; returnByValue = $true; awaitPromise = $true }
    return $r.result.value
}

# ── 探针组（全部只读）────────────────────────────────
try { Send-Cdp 'Network.enable' | Out-Null } catch {}

$probe = [ordered]@{
    capturedAt = (Get-Date).ToString('s')
    page       = @{ title = $target.title; url = $target.url }
    browser    = (Invoke-RestMethod ("http://127.0.0.1:{0}/json/version" -f $Port)).Browser
}

# ① video/canvas 形态
$probe.videoForms = Eval-Js @'
(() => {
  const out = { videos: [], bigCanvas: 0 };
  document.querySelectorAll('video').forEach((v, i) => out.videos.push({
    srcObject: v.srcObject ? (v.srcObject.constructor.name + ':' + v.srcObject.getTracks().map(t => t.kind).join(',')) : null,
    src: v.src ? String(v.src).slice(0, 100) : null, w: v.videoWidth, h: v.videoHeight, paused: v.paused
  }));
  document.querySelectorAll('canvas').forEach(c => { if (c.width >= 600 && c.height >= 300) out.bigCanvas++; });
  return JSON.stringify(out);
})()
'@

# ② 支持度
$probe.support = Eval-Js @'
(() => JSON.stringify({
  WebTransport: typeof WebTransport !== 'undefined', WebSocket: typeof WebSocket !== 'undefined',
  VideoDecoder: typeof VideoDecoder !== 'undefined', EncodedVideoChunk: typeof EncodedVideoChunk !== 'undefined',
  rVFC: 'requestVideoFrameCallback' in HTMLVideoElement.prototype,
  pointerLock: 'requestPointerLock' in Element.prototype, keyboardLock: !!(navigator.keyboard && navigator.keyboard.lock)
}))()
'@

# ③ 实例计数（queryObjects）
function Get-InstanceCount([string]$Ctor) {
    try {
        $p = Send-Cdp 'Runtime.evaluate' @{ expression = "$Ctor.prototype"; objectGroup = 'probe' }
        if (-not $p.result.objectId) { return -1 }
        $q = Send-Cdp 'Runtime.queryObjects' @{ prototypeObjectId = $p.result.objectId }
        $d = [string]$q.objects.description
        if ($d -match '(\d+)') { return [int]$Matches[1] }
        return 0
    } catch { return -1 } finally { try { Send-Cdp 'Runtime.releaseObjectGroup' @{ objectGroup = 'probe' } | Out-Null } catch {} }
}
$probe.instances = [ordered]@{}
foreach ($c in 'RTCPeerConnection', 'WebTransport', 'WebSocket', 'VideoDecoder', 'RTCDataChannel') { $probe.instances[$c] = Get-InstanceCount $c }

# ④ PC 详情 + getStats（若有实例）
if ($probe.instances.RTCPeerConnection -gt 0) {
    $p = Send-Cdp 'Runtime.evaluate' @{ expression = 'RTCPeerConnection.prototype'; objectGroup = 'pc' }
    $q = Send-Cdp 'Runtime.queryObjects' @{ prototypeObjectId = $p.result.objectId }
    $detail = Send-Cdp 'Runtime.callFunctionOn' @{
        objectId = $q.objects.objectId
        functionDeclaration = @'
async function() {
  const out = [];
  for (const pc of this) {
    const o = { connectionState: pc.connectionState, signaling: pc.signalingState, senders: pc.getSenders().length, receivers: pc.getReceivers().map(r => r.track ? r.track.kind + ':' + r.track.id : 'null') };
    try {
      const stats = await pc.getStats(null);
      const rows = [];
      stats.forEach(r => {
        if (['inbound-rtp', 'candidate-pair', 'codec'].includes(r.type)) {
          const k = ['kind','mimeType','frameWidth','frameHeight','framesPerSecond','framesDecoded','bytesReceived','packetsLost','nackCount','pliCount','currentRoundTripTime','jitter','jitterBufferDelay','jitterBufferEmittedCount','freezeCount','totalFreezesDuration','protocol','candidateType','address','nominated','state'];
          const s = {}; for (const key of k) if (r[key] !== undefined) s[key] = r[key];
          rows.push({ type: r.type, s });
        }
      });
      o.stats = rows;
      o.dataChannels = [];
      // DataChannel 计数（挂在此 PC 上）
      try { for (const dc of pc.getTransceivers ? [] : []) {} } catch (e) {}
    } catch (e) { o.statsErr = String(e); }
    out.push(o);
  }
  return JSON.stringify(out);
}
'@
        returnByValue = $true; awaitPromise = $true
    }
    $probe.pcDetail = $detail.result.value
}

# ⑤ DataChannel 详情
if ($probe.instances.RTCDataChannel -gt 0) {
    $p = Send-Cdp 'Runtime.evaluate' @{ expression = 'RTCDataChannel.prototype'; objectGroup = 'dc' }
    $q = Send-Cdp 'Runtime.queryObjects' @{ prototypeObjectId = $p.result.objectId }
    $d = Send-Cdp 'Runtime.callFunctionOn' @{
        objectId = $q.objects.objectId
        functionDeclaration = 'function() { return JSON.stringify(this.map(dc => ({ label: dc.label, state: dc.readyState, ordered: dc.ordered }))); }'
        returnByValue = $true
    }
    $probe.dataChannels = $d.result.value
}

# ⑥ WebSocket 实例 URL
if ($probe.instances.WebSocket -gt 0) {
    $p = Send-Cdp 'Runtime.evaluate' @{ expression = 'WebSocket.prototype'; objectGroup = 'wsi' }
    $q = Send-Cdp 'Runtime.queryObjects' @{ prototypeObjectId = $p.result.objectId }
    $d = Send-Cdp 'Runtime.callFunctionOn' @{
        objectId = $q.objects.objectId
        functionDeclaration = 'function() { return JSON.stringify(this.map(w => ({ url: (w.url || "").slice(0, 160), state: w.readyState }))); }'
        returnByValue = $true
    }
    $probe.webSockets = $d.result.value
}

# ⑦ Pointer Lock 现场状态
$probe.pointerLock = Eval-Js @'
(() => JSON.stringify({
  locked: !!document.pointerLockElement, target: document.pointerLockElement ? document.pointerLockElement.tagName : null,
  fullscreen: !!document.fullscreenElement
}))()
'@

# ⑧ 活跃窗口：WS 帧节奏 + getStats 差分
$stats0 = $null
if ($probe.instances.RTCPeerConnection -gt 0) {
    $p = Send-Cdp 'Runtime.evaluate' @{ expression = 'RTCPeerConnection.prototype'; objectGroup = 'pc2' }
    $q = Send-Cdp 'Runtime.queryObjects' @{ prototypeObjectId = $p.result.objectId }
    $s0 = Send-Cdp 'Runtime.callFunctionOn' @{
        objectId = $q.objects.objectId
        functionDeclaration = 'async function() { for (const pc of this) { const st = await pc.getStats(null); let v = null; st.forEach(r => { if (r.type === "inbound-rtp" && r.kind === "video") v = { bytes: r.bytesReceived, frames: r.framesDecoded }; }); return JSON.stringify({ v }); } }'
        returnByValue = $true; awaitPromise = $true
    }
    $stats0 = $s0.result.value
}
$frameCount0 = $script:wsFrames.Count
Write-Host ("采样 {0}s ..." -f $ProbeSeconds) -ForegroundColor Yellow
    $pumpEnd = ([DateTime]::Now).AddSeconds($ProbeSeconds)
    while ([DateTime]::Now -lt $pumpEnd) { Pump-CdpMessages 250 }
$frameCount1 = $script:wsFrames.Count
$delta = [ordered]@{ seconds = $ProbeSeconds; wsSent = 0; wsRecv = 0; videoMbps = $null; videoFps = $null }
$all = @($script:wsFrames.ToArray())
$delta.wsSent = @($all | Where-Object dir -eq 'sent').Count
$delta.wsRecv = @($all | Where-Object dir -eq 'recv').Count
if ($stats0) {
    $p = Send-Cdp 'Runtime.evaluate' @{ expression = 'RTCPeerConnection.prototype'; objectGroup = 'pc3' }
    $q = Send-Cdp 'Runtime.queryObjects' @{ prototypeObjectId = $p.result.objectId }
    $s1 = Send-Cdp 'Runtime.callFunctionOn' @{
        objectId = $q.objects.objectId
        functionDeclaration = 'async function() { for (const pc of this) { const st = await pc.getStats(null); let v = null; st.forEach(r => { if (r.type === "inbound-rtp" && r.kind === "video") v = { bytes: r.bytesReceived, frames: r.framesDecoded, fps: r.framesPerSecond }; }); return JSON.stringify({ v }); } }'
        returnByValue = $true; awaitPromise = $true
    }
    $v0 = ($stats0 | ConvertFrom-Json).v; $v1 = ($s1.result.value | ConvertFrom-Json).v
    if ($v0 -and $v1) {
        $delta.videoMbps = [math]::Round(($v1.bytes - $v0.bytes) * 8 / $ProbeSeconds / 1e6, 2)
        $delta.videoFps = [math]::Round(($v1.frames - $v0.frames) / $ProbeSeconds, 1)
    }
}
$probe.activeWindow = $delta

# 收尾
try { Send-Cdp 'Network.disable' | Out-Null } catch {}
try { $ws.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, 'done', $ct).GetAwaiter().GetResult() } catch {}

# ── 输出 ──────────────────────────────────────────────
$json = $probe | ConvertTo-Json -Depth 8
$outPath = Join-Path $PSScriptRoot "..\docs\research\cdp-probe-last.json"
$json | Out-File -FilePath $outPath -Encoding utf8
Write-Host $json
Write-Host ("已落盘 " + $outPath) -ForegroundColor Green
