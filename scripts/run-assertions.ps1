<#
.SYNOPSIS
  SoulLink 断言闸门 runner —— 把 tools/layout-check 下的断言工程串成可跑的闸门。

.DESCRIPTION
  分两层：
    快闸门（默认）  : 只跑 Compile-only 工程（不引用主工程），约 30 秒
    全量（-Full）   : 跑全部 90 个，约 5 分钟

  关键优化：ProjectReference 类工程每个都会重建整个 WPF 主工程（空转就要 7.6 秒），
  加 --no-dependencies 后从 7.2s 降到 0.8s。但 --no-dependencies 只编译不复制依赖，
  所以必须用默认 bin<name> 目录（那里 ResolveAssemblyReferences 会正常拷 dll），
  不能用 -o 指向别处，否则运行期会 FileNotFoundException（假失败）。

  设计纪律：
    · 临时文件绝不落在项目根（今天已两次踩坑：undefined\temp 被 SDK 通配符收进编译 → CS0017）
    · SKIP 与 PASS 严格分开统计，SKIP 绝不算 PASS（否则就是"假绿"）
    · 任一 FAIL / BUILD-FAIL 则整体退出码非 0

.PARAMETER Full
  跑全部 90 个而不是只跑快闸门层。约 5 分钟。

.PARAMETER Filter
  只跑名字匹配的工程（通配符，如 -Filter "*Broker*"）。

.PARAMETER Skip
  额外跳过的工程名（逗号分隔）。与内置跳过清单叠加。

.PARAMETER NoBuiltInSkip
  忽略内置跳过清单（用于排查"跳过项本身是不是坏了"）。慎用。

.PARAMETER DryRun
  只列出将要跑哪些工程，不实际执行。

.PARAMETER Json
  把结果以 JSON 输出到指定路径（基线报告用）。

.PARAMETER NoCleanup
  不清理本 runner 启动的测试进程。

  ★ 默认行为：runner 会记录启动前后的 PID 快照，结束时把「新增的」进程杀掉。
  只杀差集里的进程，绝不碰用户已有的 SoulLink 主程序 / stable 版。

  为什么需要它：2026-11-24 有断言（StableIdentityCheck / DupAgentProbe 等）
  会 Process.Start 拉起主程序测试副本，跑完没人清 → 用户桌面出现一个
  「SoulLink · 跨会话执行器」窗口，用户以为是主程序坏了。
  runner 的职责包括收尾，所以默认清理。

  需要保留现场排查时用 -NoCleanup。

.PARAMETER Clean
  跑之前先删掉每个工程的 bin<name> / obj<name>。

  ★ 什么时候需要它：--no-dependencies 只跳过"重建被引用的主工程"，
  但它仍然依赖 MSBuild 的增量判定。实测发现：如果源码被改回原样（mtime 变了
  但内容回到旧版本），增量判定可能保留**上一次的中间产物**，于是跑出"幽灵失败"——
  exe 里是旧代码的输出，源码却是新的。（2026-11-24 反向对照测试踩到，
  必须同时清 bin 和 obj 才消失。）

  正常开发不需要 -Clean（增量更快）；**反向对照 / 怀疑结果与源码不符时用它**。

.EXAMPLE
  .\scripts\run-assertions.ps1
  .\scripts\run-assertions.ps1 -Full
  .\scripts\run-assertions.ps1 -Filter "*Broker*" -NoBuiltInSkip
  .\scripts\run-assertions.ps1 -Json artifacts\assertions-baseline.json
#>
[CmdletBinding()]
param(
    [switch]$Full,
    [string]$Filter = "",
    [string]$Skip = "",
    [switch]$NoBuiltInSkip,
    [switch]$DryRun,
    [string]$Json = "",
    [switch]$Clean,
    [switch]$NoCleanup
)

$ErrorActionPreference = "Continue"
$ProgressPreference    = "SilentlyContinue"

$root = Split-Path -Parent $PSScriptRoot
$checkRoot = Join-Path $root "tools\layout-check"
if (-not (Test-Path -LiteralPath $checkRoot)) {
    Write-Host "找不到断言工程目录：$checkRoot" -ForegroundColor Red
    exit 2
}

# ══════════════════════════════════════════════════════════════════════════
#  内置跳过清单
#
#  原则：这些工程失败不代表产品有问题，而是"本机环境不满足"。
#  如果把它们算成 FAIL，闸门一挂就是红的，人会习惯性忽略 —— 那是"假红"，
#  和"假绿"一样有害。所以必须显式 SKIP，并在汇总里单独计数。
#
#  每个条目都注明依据，便于以后环境变了重新评估。
# ══════════════════════════════════════════════════════════════════════════
$builtInSkip = [ordered]@{
    # ── 会读取 A1/A4 相关配置或会话（硬红线：闸门里绝不能碰）──────────────
    "AccountScopeCheck"   = "A1A4 红线：源码含 A1/A4 字面量"
    "BgiDataCenterCheck"  = "A1A4 红线：源码含 A1/A4 字面量"
    "EntryCheck"          = "A1A4 红线：源码含 A1/A4 字面量"
    "PlcConfigCheck"      = "A1A4 红线：源码含 A1/A4 字面量"

    # ── 需要管理员/提权 ────────────────────────────────────────────────────
    "BootstrapHostCheck"  = "需管理员：检查 SYSTEM 引导宿主"
    "OldPathProbe"        = "需管理员：探测旧路径权限"
    "UacDiag"             = "需管理员：UAC 诊断"
    "ElevationProbe"      = "需管理员：提权探测"
    "ElevTokenCheck"      = "需管理员：令牌提权校验"
    "ManifestDebug"       = "需管理员：清单调试"
    "ManifestPatchCheck"  = "需管理员：改写 exe 清单（且疑似真回归，待 Lead 判定）"
    "ProxyHostCheck"      = "需管理员：代理宿主权限"

    # ── 需要真机/活进程/活会话 ────────────────────────────────────────────
    "BrokerCheck"         = "需活 Broker 服务（且出现 0xC0000005，待 Lead 判定）"
    "BrokerDeployCheck"   = "需活服务 + 可投送会话"
    "LiveChannelCheck"    = "需活通道会话"
    "ManualAgentLiveCheck"= "需活手动代理进程"
    "A3ChannelCheck"      = "需 A3 会话（本机未登录）"
    "ChannelDetectCheck"  = "需活会话（且疑似真回归，待 Lead 判定）"
    "DogfoodEndToEnd"     = "端到端：需完整运行环境"

    # ── 2026-11-24（t22）新增：实证为环境依赖，非产品缺陷 ──────────────────
    #    依据：跑这些时它们会 Process.Start 拉起主程序测试副本 / 需要活代理，
    #    在无人值守的闸门里必然失败（且会留下假窗口，见 -NoCleanup 说明）。
    "AgentMutexCheck"     = "需活进程：手动代理启动后才可识别"
    "EventPipeCheck"      = "需活代理：验证接入（跑 26 秒后超时）"
    "ManualHotkeyCheck"   = "需活代理：验证接入（跑 26 秒后超时）"
    "ManualModeCheck"     = "需活代理：验证自动接入（跑 27 秒后超时）"
    "StableIdentityCheck" = "需 stable 可执行文件 + 会拉起主程序副本（假窗口来源）"
    "DogfoodRecordCheck"  = "读 A1 账号下的真实日志（硬编码 2026/11/17 数值）—— 红线 + 数据依赖"

    # ── 会创建可见窗口的断言（2026-11-24 t22 补，用户已被骚扰两次）──────────
    #    ★ 判据：源码里 new Window / new Application + Show/Run。
    #      这类东西在无人值守环境里跑 = 弹窗骚扰用户，必须默认跳过。
    #    ★ 注意：跳过是「防止打扰」，runner 的进程清理只是「兜底收尾」——
    #      清理发生在跑完之后，窗口在跑的过程中就已经骚扰了。两者不能互相替代。
    "FlowCanvasInputCheck"   = "需 GUI：Topmost 置顶窗口「SoulLink isolated mouse check」+ 模拟真实鼠标拖拽 + 写截图 → 会弹窗"
    "FlowEditorVisualCheck"  = "需 GUI：e.Show() + app.Run + 写 artifacts png → 会弹窗"
    # 注（2026-11-24 逐个核实）：以下三个虽然 new 了 Application/Window，但**不会弹窗**，故不跳过：
    #   · FlowEditorCheck      —— new FlowEditorWindow 但从不 Show()，读完状态即 Close()
    #   · FlowEditorLiveCheck  —— 只 app.Run() 跑消息循环，不创建/显示任何窗口，finally 里 Shutdown()
    #   · FlowCanvasCheck      —— 纯逻辑，无窗口

    # ── 需 GUI / 桌面会话 / 生成图片 ──────────────────────────────────────
    #  ★ 2026-11-24：GuideShot / GuideShot2 / GuideStepsShot / GuideBoundsCheck 已随 t21 删除，
    #    条目一并移除（留着会变成"跳过一个不存在的工程"，掩盖真实清单长度）。
    "BrokerThemeShot"     = "需 GUI：生成主题截图（e.Show + app.Run + 写 png）"
    "IconBuilder"         = "需 GUI：生成 ico"
}

# 用户额外跳过
$userSkip = @{}
if ($Skip.Trim().Length -gt 0) {
    foreach ($s in ($Skip -split ",")) {
        $t = $s.Trim()
        if ($t.Length -gt 0) { $userSkip[$t] = "用户通过 -Skip 指定" }
    }
}

if ($NoBuiltInSkip) {
    Write-Host "[注意] -NoBuiltInSkip：内置跳过清单已禁用，环境依赖项会真实执行（可能假红）" -ForegroundColor Yellow
    $builtInSkip = [ordered]@{}
}

# ══════════════════════════════════════════════════════════════════════════
#  收集工程
# ══════════════════════════════════════════════════════════════════════════
$allProjs = Get-ChildItem -LiteralPath $checkRoot -Filter *.csproj -File | Sort-Object Name

$plan = @()
foreach ($p in $allProjs) {
    $name = [IO.Path]::GetFileNameWithoutExtension($p.Name)
    $text = Get-Content -Raw -LiteralPath $p.FullName
    $isRef = $text -match "<ProjectReference"

    # 快闸门 = 只要 Compile-only
    if (-not $Full -and $isRef) { continue }

    if ($Filter.Trim().Length -gt 0 -and $name -notlike $Filter) { continue }

    $skipReason = $null
    if ($builtInSkip.Contains($name)) { $skipReason = $builtInSkip[$name] }
    elseif ($userSkip.ContainsKey($name)) { $skipReason = $userSkip[$name] }

    $plan += [pscustomobject]@{
        Name   = $name
        Path   = $p.FullName
        IsRef  = $isRef
        Skip   = $skipReason
    }
}

if ($plan.Count -eq 0) {
    Write-Host "没有匹配的断言工程（Filter=[$Filter]）" -ForegroundColor Yellow
    exit 2
}

$mode = if ($Full) { "全量" } else { "快闸门（Compile-only）" }
Write-Host ""
Write-Host "═══ SoulLink 断言闸门 ═══" -ForegroundColor Cyan
Write-Host ("  模式      : " + $mode)
Write-Host ("  工程数    : " + $plan.Count + $(if ($Filter) { "（Filter=$Filter）" } else { "" }))
Write-Host ("  跳过        : " + (($plan | Where-Object { $_.Skip }).Count))
if ($DryRun) { Write-Host "  DryRun    : 只枚举不执行" -ForegroundColor Yellow }
Write-Host ""

if ($DryRun) {
    foreach ($e in $plan) {
        $tag = if ($e.Skip) { "SKIP" } else { if ($e.IsRef) { "REFP" } else { "COMP" } }
        $why = if ($e.Skip) { "  <- " + $e.Skip } else { "" }
        Write-Host ("  [{0}] {1}{2}" -f $tag, $e.Name, $why)
    }
    Write-Host ""
    Write-Host "（DryRun 结束，未执行任何构建）" -ForegroundColor Yellow
    exit 0
}

# ══════════════════════════════════════════════════════════════════════════
#  执行
# ══════════════════════════════════════════════════════════════════════════
# ══════════════════════════════════════════════════════════════════════════
#  进程快照（清理用）—— 记录"我自己启动了哪些测试进程"
#
#  只取差集：结束时的 PID 集合 减去 开始时的。绝不碰用户原有的进程
#  （尤其 SoulLink.exe 主程序、dist\stable 的挂机版）。
# ══════════════════════════════════════════════════════════════════════════
function Get-ProcessIdSet {
    $set = @{}
    foreach ($p in Get-Process -ErrorAction SilentlyContinue) { $set[$p.Id] = $p.ProcessName }
    return $set
}
$pidBefore = if ($NoCleanup) { @{} } else { Get-ProcessIdSet }
if (-not $NoCleanup) {
    Write-Host ("  进程快照：启动前 " + $pidBefore.Count + " 个进程") -ForegroundColor DarkGray
    Write-Host ""
}

$results = [System.Collections.Generic.List[object]]::new()
$tAll = Get-Date

foreach ($e in $plan) {
    if ($e.Skip) {
        Write-Host ("  SKIP        {0,-26} {1}" -f $e.Name, $e.Skip) -ForegroundColor DarkGray
        $results += [pscustomobject]@{
            Name = $e.Name; Status = "SKIP"; Exit = 0; Seconds = 0.0;
            Detail = $e.Skip; Output = ""
        }
        continue
    }

    $t0 = Get-Date

    # -Clean：先删 bin<name> / obj<name>，避免增量判定保留上一次的中间产物。
    # 见 .PARAMETER Clean 的说明 —— 这是 2026-11-24 反向对照测试踩出来的。
    if ($Clean) {
        $projDir = Split-Path -Parent $e.Path
        foreach ($sub in @(("bin" + $e.Name), ("obj" + $e.Name))) {
            $target = Join-Path $projDir $sub
            if (Test-Path -LiteralPath $target) {
                Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    # --no-dependencies：不重建被引用的主工程（7.2s -> 0.8s）
    # 不用 -o：默认 bin<name> 目录才会正常复制依赖 dll
    $bout = & dotnet build $e.Path -c Release --nologo -v q --no-dependencies 2>&1
    $bcode = $LASTEXITCODE

    if ($bcode -ne 0) {
        $secs = ((Get-Date) - $t0).TotalSeconds
        $errLine = ($bout | Select-String -Pattern ": error " | Select-Object -First 1)
        $detail = if ($errLine) { $errLine.Line.Trim() } else { "build failed (exit $bcode)" }
        if ($detail.Length -gt 150) { $detail = $detail.Substring(0, 150) }
        Write-Host ("  BUILD-FAIL  {0,-26} {1,6:N1}s  {2}" -f $e.Name, $secs, $detail) -ForegroundColor Red
        $results += [pscustomobject]@{
            Name = $e.Name; Status = "BUILD-FAIL"; Exit = $bcode; Seconds = [math]::Round($secs,1);
            Detail = $detail; Output = ($bout | Select-Object -Last 20) -join "`n"
        }
        continue
    }

    # 找 exe：默认 bin<工程名> 目录
    $binDir = Join-Path (Split-Path -Parent $e.Path) ("bin" + $e.Name)
    $exe = $null
    if (Test-Path -LiteralPath $binDir) {
        $exe = Get-ChildItem -LiteralPath $binDir -Recurse -Filter *.exe -File -ErrorAction SilentlyContinue |
               Sort-Object LastWriteTime -Descending | Select-Object -First 1
    }

    if (-not $exe) {
        $secs = ((Get-Date) - $t0).TotalSeconds
        Write-Host ("  NO-EXE      {0,-26} {1,6:N1}s  bin 目录里没有 exe" -f $e.Name, $secs) -ForegroundColor Red
        $results += [pscustomobject]@{
            Name = $e.Name; Status = "BUILD-FAIL"; Exit = -1; Seconds = [math]::Round($secs,1);
            Detail = "构建成功但未产出 exe（bin=$binDir）"; Output = ""
        }
        continue
    }

    $rout = & $exe.FullName 2>&1
    $rcode = $LASTEXITCODE
    $secs  = ((Get-Date) - $t0).TotalSeconds

    if ($rcode -eq 0) {
        Write-Host ("  PASS        {0,-26} {1,6:N1}s" -f $e.Name, $secs) -ForegroundColor Green
        $results += [pscustomobject]@{
            Name = $e.Name; Status = "PASS"; Exit = 0; Seconds = [math]::Round($secs,1);
            Detail = ""; Output = ($rout | Select-Object -Last 5) -join "`n"
        }
    }
    else {
        $failLine = ($rout | Select-String -Pattern "FAIL" | Select-Object -First 1)
        $detail = if ($failLine) { $failLine.Line.Trim() } else { "exit $rcode" }
        if ($detail.Length -gt 150) { $detail = $detail.Substring(0, 150) }
        Write-Host ("  FAIL        {0,-26} {1,6:N1}s  exit={2}  {3}" -f $e.Name, $secs, $rcode, $detail) -ForegroundColor Red
        $results += [pscustomobject]@{
            Name = $e.Name; Status = "FAIL"; Exit = $rcode; Seconds = [math]::Round($secs,1);
            Detail = $detail; Output = ($rout | Select-Object -Last 20) -join "`n"
        }
    }
}

$totalSecs = ((Get-Date) - $tAll).TotalSeconds

# ══════════════════════════════════════════════════════════════════════════
#  汇总
# ══════════════════════════════════════════════════════════════════════════
$nPass = @($results | Where-Object { $_.Status -eq "PASS" }).Count
$nFail = @($results | Where-Object { $_.Status -eq "FAIL" }).Count
$nBf   = @($results | Where-Object { $_.Status -eq "BUILD-FAIL" }).Count
$nSkip = @($results | Where-Object { $_.Status -eq "SKIP" }).Count

Write-Host ""
Write-Host "═══ 汇总 ═══" -ForegroundColor Cyan
Write-Host ("  PASS        : " + $nPass) -ForegroundColor Green
Write-Host ("  FAIL        : " + $nFail)  -ForegroundColor $(if ($nFail -gt 0) { "Red" } else { "Gray" })
Write-Host ("  BUILD-FAIL  : " + $nBf)    -ForegroundColor $(if ($nBf -gt 0) { "Red" } else { "Gray" })
Write-Host ("  SKIP        : " + $nSkip)  -ForegroundColor DarkGray
Write-Host ("  总耗时      : " + [math]::Round($totalSecs,1) + " 秒") -ForegroundColor Cyan

if ($nFail -gt 0) {
    Write-Host "  FAIL 明细：" -ForegroundColor Red
    $results | Where-Object { $_.Status -eq "FAIL" } | ForEach-Object {
        Write-Host ("    - {0}  exit={1}  {2}" -f $_.Name, $_.Exit, $_.Detail)
    }
}
if ($nBf -gt 0) {
    Write-Host "  BUILD-FAIL 明细：" -ForegroundColor Red
    $results | Where-Object { $_.Status -eq "BUILD-FAIL" } | ForEach-Object {
        Write-Host ("    - {0}  {1}" -f $_.Name, $_.Detail)
    }
}

# ── JSON 基线报告 ─────────────────────────────────────────────────────────
if ($Json.Trim().Length -gt 0) {
    $jsonPath = if ([IO.Path]::IsPathRooted($Json)) { $Json } else { Join-Path $root $Json }
    $dir = Split-Path -Parent $jsonPath
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $report = [ordered]@{
        generatedAt = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
        mode        = $mode
        filter      = $Filter
        totalSeconds= [math]::Round($totalSecs,1)
        summary     = [ordered]@{
            pass = $nPass; fail = $nFail; buildFail = $nBf; skip = $nSkip; total = $results.Count
        }
        results     = $results
    }
    ($report | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $jsonPath -Encoding UTF8
    Write-Host ("  基线报告： " + $jsonPath) -ForegroundColor Cyan
}

# ══════════════════════════════════════════════════════════════════════════
#  清理本 runner 启动的测试进程
#
#  ★ 只杀差集（结束时新增的 PID）。用户原有的 SoulLink 进程一个都不碰。
#  ★ 再额外兜一层：只处理进程名属于本仓库测试产物的（断言 exe / SoulLink 测试副本），
#    避免误杀同 PID 复用的无关进程。
# ══════════════════════════════════════════════════════════════════════════
if (-not $NoCleanup) {
    $pidAfter = Get-ProcessIdSet
    $newPids = @()
    foreach ($id in $pidAfter.Keys) {
        if (-not $pidBefore.ContainsKey($id)) { $newPids += $id }
    }

    if ($newPids.Count -eq 0) {
        Write-Host "  进程清理：无残留（未启动任何常驻测试进程）" -ForegroundColor DarkGray
    }
    else {
        Write-Host ""
        Write-Host ("  进程清理：发现 " + $newPids.Count + " 个本次新增进程，逐个核对后清理") -ForegroundColor Yellow
        $killed = 0; $kept = 0
        foreach ($id in $newPids) {
            $proc = Get-Process -Id $id -ErrorAction SilentlyContinue
            if (-not $proc) { continue }
            $pn = $proc.ProcessName
            # 白名单式判断：只清理明显属于测试产物的
            $isTestArtifact = $false
            try {
                $path = $proc.Path
                if ($path) {
                    # 断言 exe 都在 tools\layout-check\bin* 下
                    if ($path -like "*\tools\layout-check\bin*") { $isTestArtifact = $true }
                    # 或本仓库下被复制出来做测试的 SoulLink 副本（非 Program Files 里那个服务用的）
                    elseif ($path -like "*\layout-check\*") { $isTestArtifact = $true }
                }
            } catch { }

            # 绝不碰 dist\stable（红线）与 Program Files 的服务本体
            $isProtected = $false
            try {
                $path2 = $proc.Path
                if ($path2) {
                    if ($path2 -like "*\dist\stable\*") { $isProtected = $true }
                    if ($path2 -like "*Program Files\SoulLinkDev\*") { $isProtected = $true }
                }
            } catch { }

            if ($isProtected) {
                Write-Host ("    PROTECTED  pid=" + $id + " " + $pn + "（红线，不动）") -ForegroundColor Magenta
                $kept++
                continue
            }
            if ($isTestArtifact) {
                try {
                    Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
                    Write-Host ("    killed     pid=" + $id + " " + $pn)
                    $killed++
                } catch {
                    Write-Host ("    FAILED     pid=" + $id + " " + $pn + " : " + $_.Exception.Message) -ForegroundColor Red
                }
            }
            else {
                Write-Host ("    保留       pid=" + $id + " " + $pn + "（非测试产物，不动）") -ForegroundColor DarkGray
                $kept++
            }
        }
        Write-Host ("  进程清理：已清理 " + $killed + " 个，保留 " + $kept + " 个") -ForegroundColor Cyan
    }
}

# ── 退出码：任一 FAIL/BUILD-FAIL 即非 0（SKIP 不算失败）──────────────────
$bad = $nFail + $nBf
if ($bad -gt 0) {
    Write-Host ""
    Write-Host ("闸门失败：" + $bad + " 个工程不是 PASS（SKIP 不计入）") -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "闸门通过：全部 PASS 或 SKIP" -ForegroundColor Green
exit 0