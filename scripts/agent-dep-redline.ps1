# agent-dep-redline.ps1 —— 编译红线（弱化版）：Relay/（agent 链）引入 WPF-only 类型时构建失败
# 来源：BENCH-YOMORI-02-AGENT-ARCH.md §4 行动 0（对标 Yomori 的「链接+排除」编译红线）
# 机制：扫描 Relay/**/*.cs 的 using 与类型引用；违规 → 退出码 2（被 csproj Target 转为构建失败）
# 白名单：System.Drawing / System.Drawing.Imaging（GDI 截图，agent 合法依赖）
param(
    [string]$Root = (Split-Path $PSScriptRoot -Parent),
    [switch]$WarnOnly   # 只打印不失败（本地开发宽松模式）
)

$ErrorActionPreference = 'Stop'
$relayDir = Join-Path $Root 'Relay'
if (-not (Test-Path $relayDir)) { Write-Output '[redline] Relay/ 不存在，跳过'; exit 0 }

# 红线清单：agent 链禁止引用的命名空间/类型前缀
$forbidden = @(
    'using System.Windows',          # WPF（System.Windows.*：Media/Controls/Input/Input 等）
    'using SoulLink.Controls',       # 主程序 UI 控件（RdpConsole 等）
    'using SoulLink.Views'           # 主程序页面
)

$violations = New-Object System.Collections.Generic.List[string]
Get-ChildItem $relayDir -Filter '*.cs' -Recurse | ForEach-Object {
    $file = $_
    $lineNo = 0
    foreach ($line in [System.IO.File]::ReadLines($file.FullName)) {
        $lineNo++
        $trimmed = $line.Trim()
        foreach ($pat in $forbidden) {
            if ($trimmed.StartsWith($pat, [System.StringComparison]::Ordinal)) {
                # System.Windows.Forms 与 System.Windows 分别判（Forms 也是 UI 栈，agent 不该碰）
                $violations.Add("$($file.Name):$lineNo : $trimmed")
            }
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Output ''
    Write-Output '════ 编译红线违规（agent 链引入 UI 依赖）════'
    Write-Output '来源: BENCH-YOMORI-02-AGENT-ARCH.md §4 —— agent 是每账户常驻进程，'
    Write-output '       引入 WPF/WinForms 控件会把 UI 栈拖进无人值守进程（Yomori 的编译期红线教训）。'
    foreach ($v in $violations) { Write-Output "  $v" }
    Write-Output '════════════════════════════════════════════'
    if ($WarnOnly) { Write-Warning "agent 依赖红线违规 $($violations.Count) 处（WarnOnly，不阻断）"; exit 0 }
    exit 2
}
Write-Output '[redline] Relay/ 依赖检查通过（0 违规）'
exit 0
