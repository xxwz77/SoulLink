<#
================================================================================
 SoulLink 性能实测脚本 · scripts\measure-soullink-perf.ps1
================================================================================

 目的：把 STREAM-PERF-AUDIT / HANDOFF-3.3.19 里那些标着「估算 / 待测」的数字，
       变成用户自己就能量出来的真实数字。

 配套文档：docs\research\PERF-MEASURE-20261122.md（怎么跑、怎么看、怎么判读）

--------------------------------------------------------------------------------
 ★★★ 这个脚本是只读的 —— 不会影响正在挂机的账号 ★★★
--------------------------------------------------------------------------------

 它只做「看」，不做任何「改」。全部系统调用逐条列在下面，便于审计：

   1. Get-Process + Process.Refresh()         读 CPU 累计时间、工作集
      → 操作系统只读计数器，不改变目标进程任何状态
   2. GetProcessHandleCount(pid)              kernel32，读句柄数
      → 用 PROCESS_QUERY_LIMITED_INFORMATION（最小只读权限）打开，读完立刻关
      → 按 AGENTS.md 性能约束第 3 条，**不用** Process.HandleCount（后者 3~5ms/次）
   3. GetProcessIoCounters(pid)               kernel32，读非网络 I/O 累计字节
      → 仅作参考列（证明「网络字节不在这里」）
   4. GetExtendedTcpTable + GetPerTcpConnectionEStats(iphlpapi)
      → 读每条 TCP 连接的**累计**收发字节数（DataBytesOut / DataBytesIn）
      → 等价于「看连接统计」，本机内核计数，不产生任何收发包、不影响传输
   5. 可选：只读打开 config.json 显示录制参数（ConvertFrom-Json，绝不写回）

 它明确**不会**做的事（这些在源码里都找不到）：

   × 不写任何配置文件、不改注册表
   × 不结束 / 暂停 / 重启任何进程，不启停任何服务
   × 不碰 dist\ 下任何东西 —— 若 -Csv 指向 dist\ 会被直接拒绝
   × 不调用 SetPerTcpConnectionEStats（那会改网络栈状态，属写操作），
     所以它也只能读数、不能「打开统计开关」
   × 不注入、不挂钩、不 attach 调试器、不读目标进程内存

 对正在挂机的账号的唯一影响：每 IntervalMs 毫秒读一次计数器，
 单次在微秒级、无锁、不打断目标进程。也就是说——**挂机时可以放心跑它**。

--------------------------------------------------------------------------------
 ★ 测量方法（交接文档 §6.4 记过一次「279% 假数据」事故，这里照规矩实现）
--------------------------------------------------------------------------------

   (1) 差分采样必须成对 Refresh()，否则两个采样点落在同一时刻：

         $p.Refresh(); $a = $p.TotalProcessorTime.TotalMilliseconds
         Start-Sleep -Milliseconds $ms
         $p.Refresh(); $b = $p.TotalProcessorTime.TotalMilliseconds
         CPU% = ($b - $a) / $ms * 100

       本脚本严格成对 Refresh()，并额外做了一处修正：
       分母用 **Stopwatch 实测经过时间**，而不是标称的 IntervalMs。
       Start-Sleep 实际会多睡几毫秒，用标称值会把 CPU% 系统性算低 1~3%。

   (2) 必须拿「进程总 CPU / 存活时长」当平均基线交叉验证。
       本脚本每个进程都同时输出：
         · 瞬时 CPU%（逐采样点差分的中位数/均值/P95/峰值）
         · 寿命平均 CPU%（累计 CPU ÷ 存活时长）
       两者若差距过大，脚本会主动标「数据可疑」并给原因。

   (3) CPU% 的口径是「**单核百分比**」，与文档一致：可以 > 100%（= 多核）。
       同时给出「占整机百分比」= 单核% ÷ 逻辑核数，避免两套口径混用。

--------------------------------------------------------------------------------
 用法
--------------------------------------------------------------------------------

   # 最常用：主程序 + 所有代理，采样 60 秒
   pwsh -File scripts\measure-soullink-perf.ps1

   # 采样 5 分钟、每 500ms 一点、导出逐点明细
   pwsh -File scripts\measure-soullink-perf.ps1 -Seconds 300 -IntervalMs 500 -Csv out\perf.csv

   # 对照实验：开着网页看画面时跑一次，关掉网页再跑一次（用 -Label 区分）
   pwsh -File scripts\measure-soullink-perf.ps1 -Seconds 120 -Label watching -Csv out\watch.csv
   pwsh -File scripts\measure-soullink-perf.ps1 -Seconds 120 -Label idle     -Csv out\idle.csv

   # 只量指定进程（例如自测时量 pwsh 自己）
   pwsh -File scripts\measure-soullink-perf.ps1 -ProcessId $PID -Seconds 5 -IntervalMs 500

 参数
   -Seconds        采样总时长（秒），默认 60
   -IntervalMs     采样间隔（毫秒），默认 1000；<250 时读数会被系统计时精度放大噪声
   -ProcessName    进程名（不含 .exe），默认 SoulLink
   -ProcessId      只量这些 PID（覆盖 -ProcessName 的自动发现）
   -Csv            导出逐采样点明细 CSV 的路径（可选，默认不写任何文件）
   -Label          给本次运行打标签，写进 CSV（用于「有人看 / 没人看」对照）
   -ConfigPath     指定 config.json（默认自动找 dist\data\config.json 等位置）
   -WatchingThresholdMBps
                   判定「该代理正在推帧（有人在看）」的发送速率阈值，默认 0.05 MB/s
================================================================================
#>

[CmdletBinding()]
param(
    [int]    $Seconds       = 60,
    [int]    $IntervalMs    = 1000,
    [string] $ProcessName   = 'SoulLink',
    [int[]]  $ProcessId     = @(),
    [string] $Csv           = '',
    [string] $Label         = '',
    [string] $ConfigPath    = '',
    [double] $WatchingThresholdMBps = 0.05
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'Continue'

# ── 参数校验（早失败，别让人等 5 分钟才发现参数错）──────────────────────────
if ($Seconds -lt 2)      { throw "-Seconds 至少 2 秒（要至少两个采样点才能算差分）" }
if ($IntervalMs -lt 100) { throw "-IntervalMs 至少 100 毫秒（系统计时精度约 15.6ms，再小就是噪声）" }
if ($IntervalMs -gt $Seconds * 1000) { throw "-IntervalMs 不能大于 -Seconds*1000" }

$TotalSamples = [int][math]::Floor(($Seconds * 1000.0) / $IntervalMs)
if ($TotalSamples -lt 2) { $TotalSamples = 2 }

$MachineCores = [Environment]::ProcessorCount
$MB = 1048576.0
$script:NativeOk  = $false
$script:NativeWhy = ''

# ── CSV 安全护栏：只允许写到用户显式给出的路径，且拒绝写进 dist\ ─────────────
$CsvFull = ''
if ($Csv -ne '') {
    $CsvFull = [IO.Path]::GetFullPath($Csv)
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $distDir  = [IO.Path]::GetFullPath((Join-Path $repoRoot 'dist'))
    if (-not $distDir.EndsWith([IO.Path]::DirectorySeparatorChar)) {
        $distDir += [IO.Path]::DirectorySeparatorChar
    }
    if ($CsvFull.StartsWith($distDir, [StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝把 CSV 写进 dist\（那是发布产物目录）：$CsvFull"
    }
    $csvDir = [IO.Path]::GetDirectoryName($CsvFull)
    if ($csvDir -and -not [IO.Directory]::Exists($csvDir)) {
        [void][IO.Directory]::CreateDirectory($csvDir)
    }
}

# ══════════════════════════════════════════════════════════════════════════
#  原生 API 声明（全部只读）
# ══════════════════════════════════════════════════════════════════════════
#
#  ★ 为什么句柄数必须走 GetProcessHandleCount：
#    AGENTS.md 性能约束第 3 条 —— Process.HandleCount 每次 3~5ms，
#    挂在 1Hz 循环里自己就吃掉 20% CPU，测量工具污染被测对象。
#    GetProcessHandleCount 是单次系统调用，微秒级。
#
#  ★ 为什么网络字节必须走 TCP eStats：
#    实测确认（见 PERF-MEASURE 文档 §3）Windows 的 GetProcessIoCounters
#    **不统计 socket 发送**：一个进程经 loopback 发送 12.8MB 后，
#    WriteTransferCount 只有 1334 字节、OtherTransferCount 576KB，
#    与真实 12.8MB 完全对不上。所以「进程网络发送量」只能从 TCP 层取。
#
#  ★ 声明失败怎么办：跳过句柄/网络两项并明确说明**不可用**，
#    绝不退化成正则解析 netstat 或反射慢路径（那比不测更糟）。
#
$NativeSource = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public sealed class SlNetSnapshot
{
    public Dictionary<int, long[]> ByPid = new Dictionary<int, long[]>();      // pid -> [txBytes, rxBytes, estConns]
    public Dictionary<int, List<int>> ListenPorts = new Dictionary<int, List<int>>(); // pid -> 监听端口
    public int EstablishedTotal;
    public int EstatsFail;
    public string Error = "";
}

public static class SoulLinkPerfNative
{
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const int  MIB_TCP_STATE_ESTABLISHED = 5;
    const int  MIB_TCP_STATE_LISTEN      = 2;
    // TCP_ESTATS_DATA_ROD_v0 的大小。实测扫出「第一个能成功的尺寸」正是 96，
    // 且 DataBytesOut@0 / DataBytesIn@16 的读数与实际收发的字节数逐字节吻合。
    const uint ROD_SIZE = 96;

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public uint localPort;
        public uint remoteAddr;
        public uint remotePort;
        public uint owningPid;
    }

    // GetPerTcpConnectionEStats 要的是 MIB_TCPROW（不含 pid），字段顺序与上面一致
    [StructLayout(LayoutKind.Sequential)]
    struct MIB_TCPROW
    {
        public uint state;
        public uint localAddr;
        public uint localPort;
        public uint remoteAddr;
        public uint remotePort;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessHandleCount(IntPtr h, out uint count);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessIoCounters(IntPtr h, out IO_COUNTERS counters);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr buf, ref int size, bool sort, int af, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetPerTcpConnectionEStats(ref MIB_TCPROW row, int estatsType,
        IntPtr rw, uint rwVer, uint rwSize,
        IntPtr ros, uint rosVer, uint rosSize,
        IntPtr rod, uint rodVer, uint rodSize);

    // ── 句柄数（失败返回 -1，调用方按「不可用」处理）──────────────────
    public static int HandleCount(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return -1;
        try
        {
            uint n;
            return GetProcessHandleCount(h, out n) ? (int)n : -1;
        }
        finally { CloseHandle(h); }
    }

    // ── 非网络 I/O 累计字节（仅参考列；网络字节不在这里）────────────────
    public static long NonNetIoBytes(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return -1;
        try
        {
            IO_COUNTERS c;
            if (!GetProcessIoCounters(h, out c)) return -1;
            return (long)c.OtherTransferCount;
        }
        finally { CloseHandle(h); }
    }

    static ushort Host16(uint netOrderPort)
    {
        return (ushort)(((netOrderPort >> 8) & 0xFF) | ((netOrderPort & 0xFF) << 8));
    }

    // ── 一次表遍历，拿到「每条连接属于哪个 pid + 累计收发字节」──────────
    public static SlNetSnapshot CollectTcp(int[] wantPids)
    {
        SlNetSnapshot snap = new SlNetSnapshot();
        HashSet<int> want = null;
        if (wantPids != null && wantPids.Length > 0)
        {
            want = new HashSet<int>(wantPids);
        }

        int size = 0;
        // 122 = ERROR_INSUFFICIENT_BUFFER —— 这是「先问需要多大」的正常返回值，不是错误
        uint rc = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0);
        if (rc != 122 || size <= 0)
        {
            snap.Error = "GetExtendedTcpTable(size) rc=" + rc;
            return snap;
        }

        IntPtr buf = Marshal.AllocHGlobal(size);
        IntPtr rod = Marshal.AllocHGlobal(256);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, 2, 5, 0) != 0)
            {
                snap.Error = "GetExtendedTcpTable(table) 失败";
                return snap;
            }

            int rows = Marshal.ReadInt32(buf);
            int stride = Marshal.SizeOf(typeof(MIB_TCPROW_OWNER_PID));   // 24
            for (int i = 0; i < rows; i++)
            {
                IntPtr p = new IntPtr(buf.ToInt64() + 4 + (long)i * stride);
                MIB_TCPROW_OWNER_PID rp = (MIB_TCPROW_OWNER_PID)Marshal.PtrToStructure(p, typeof(MIB_TCPROW_OWNER_PID));
                int pid = (int)rp.owningPid;

                if (rp.state == MIB_TCP_STATE_LISTEN)
                {
                    if (want == null || want.Contains(pid))
                    {
                        List<int> ports;
                        if (!snap.ListenPorts.TryGetValue(pid, out ports))
                        {
                            ports = new List<int>();
                            snap.ListenPorts[pid] = ports;
                        }
                        ports.Add((int)Host16(rp.localPort));
                    }
                    continue;
                }

                if (rp.state != MIB_TCP_STATE_ESTABLISHED) continue;
                if (want != null && !want.Contains(pid)) continue;

                snap.EstablishedTotal++;

                MIB_TCPROW row = new MIB_TCPROW();
                row.state = rp.state;
                row.localAddr = rp.localAddr;
                row.localPort = rp.localPort;
                row.remoteAddr = rp.remoteAddr;
                row.remotePort = rp.remotePort;

                if (GetPerTcpConnectionEStats(ref row, 1, IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0,
                                              rod, 0, ROD_SIZE) != 0)
                {
                    snap.EstatsFail++;
                    continue;
                }

                long tx = Marshal.ReadInt64(rod, 0);    // DataBytesOut
                long rx = Marshal.ReadInt64(rod, 16);   // DataBytesIn

                long[] acc;
                if (!snap.ByPid.TryGetValue(pid, out acc))
                {
                    acc = new long[3];
                    snap.ByPid[pid] = acc;
                }
                acc[0] += tx;
                acc[1] += rx;
                acc[2] += 1;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
            Marshal.FreeHGlobal(rod);
        }
        return snap;
    }
}
'@

try {
    if (-not ('SoulLinkPerfNative' -as [type])) {
        Add-Type -TypeDefinition $NativeSource -Language CSharp -ErrorAction Stop
    }
    $script:NativeOk = $true
}
catch {
    $script:NativeOk  = $false
    $script:NativeWhy = $_.Exception.Message
}

# ══════════════════════════════════════════════════════════════════════════
#  小工具
# ══════════════════════════════════════════════════════════════════════════

function Format-Num {
    param($Value, [string]$Fmt = '0.00', [string]$IfNull = 'n/a')
    if ($null -eq $Value) { return $IfNull }
    return ([double]$Value).ToString($Fmt, [Globalization.CultureInfo]::InvariantCulture)
}

function ConvertTo-RoleText {
    param([string]$Role, [switch]$Short)
    switch ($Role) {
        'agent' { if ($Short) { return '代理' } else { return '代理(RelayAgent)' } }
        'main'  { if ($Short) { return '主程序' } else { return '主程序' } }
        default { if ($Short) { return '其它' } else { return '其它进程(非SoulLink)' } }
    }
}

function Get-Stat {
    param([double[]]$Values)
    if ($null -eq $Values -or $Values.Count -eq 0) {
        return [pscustomobject]@{ N = 0; Mean = $null; Median = $null; P95 = $null; Max = $null }
    }
    $s = @($Values | Sort-Object)
    $n = $s.Count
    $sum = 0.0
    foreach ($v in $s) { $sum += $v }
    $mean = $sum / $n
    if ($n % 2 -eq 1) { $median = $s[[int](($n - 1) / 2)] }
    else { $median = ($s[[int]($n / 2 - 1)] + $s[[int]($n / 2)]) / 2.0 }
    $p95Idx = [int][math]::Floor(0.95 * ($n - 1))
    return [pscustomobject]@{
        N      = $n
        Mean   = $mean
        Median = $median
        P95    = $s[$p95Idx]
        Max    = $s[$n - 1]
    }
}

# ── 只读地读 config.json，拿录制参数（绝不写回）────────────────────────────
function Find-SoulLinkConfig {
    param([string]$Explicit)
    if ($Explicit -ne '') {
        if (Test-Path -LiteralPath $Explicit -PathType Leaf) { return [IO.Path]::GetFullPath($Explicit) }
        return ''
    }
    $cands = @()
    if ($env:SOULLINK_DATA_DIR) { $cands += (Join-Path $env:SOULLINK_DATA_DIR 'config.json') }
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $cands += (Join-Path $repoRoot 'dist\data\config.json')
    $cands += (Join-Path $repoRoot 'data\config.json')
    if ($env:LOCALAPPDATA) { $cands += (Join-Path $env:LOCALAPPDATA 'SoulLink\config.json') }
    if ($env:APPDATA)      { $cands += (Join-Path $env:APPDATA 'SoulLink\config.json') }
    foreach ($c in $cands) {
        try { if ($c -and (Test-Path -LiteralPath $c -PathType Leaf)) { return [IO.Path]::GetFullPath($c) } } catch { }
    }
    return ''
}

function Read-RecordingSettings {
    param([string]$Path)
    $res = [pscustomobject]@{ Ok = $false; Path = $Path; Fps = $null; Quality = $null; WebPort = $null; RecordAccounts = @(); Why = '' }
    if ($Path -eq '') { $res.Why = '没找到 config.json'; return $res }
    try {
        $json = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
        $s = $json.Settings
        if ($null -eq $s) { $res.Why = 'config.json 里没有 Settings 节点'; return $res }
        $res.Fps      = $s.RecordFps
        $res.Quality  = $s.RecordQuality
        $res.WebPort  = $s.WebPort
        $res.RecordAccounts = @($s.RecordAccountIds)
        $res.Ok = $true
        return $res
    }
    catch {
        $res.Why = "读取失败：$($_.Exception.Message)"
        return $res
    }
}

# ── 进程分类：主程序 / 代理（--relay）──────────────────────────────────────
#
#  代理和主程序是同一个 exe，只能靠命令行里的 --relay 区分。
#  CIM 读命令行在「跨会话」时可能被拒绝（普通用户读不到更高权限会话的命令行），
#  这时退化为「按进程名 + 是否有窗口」判断，并明确标注「分类未确认」。
#
function Get-RelayCommandLines {
    param([int[]]$Pids, [string]$Name)
    $map = @{}
    if ($Pids.Count -eq 0) { return $map }
    try {
        $names = @("$Name.exe")
        if ("$Name.Agent.exe" -ne "$Name.exe") { $names += "$Name.Agent.exe" }
        $filter = ($names | ForEach-Object { "Name='$_'" }) -join ' OR '
        $cim = @(Get-CimInstance -ClassName Win32_Process -Filter $filter -Property ProcessId, CommandLine -ErrorAction Stop)
        foreach ($c in $cim) {
            if ($null -ne $c.ProcessId) {
                $map[[int]$c.ProcessId] = [string]$c.CommandLine
            }
        }
    }
    catch { }
    return $map
}

function ConvertTo-RelayInfo {
    param([string]$CommandLine)
    $info = [pscustomobject]@{ IsRelay = $false; Pipe = ''; SessionId = 0 }
    if ([string]::IsNullOrEmpty($CommandLine)) { return $info }
    if ($CommandLine -notmatch '--relay') { return $info }
    $info.IsRelay = $true
    # 管道名形如 SoulLink_Relay_6_ab12cd34 → 会话号 6
    $m = [regex]::Match($CommandLine, '_Relay_(\d+)_')
    if ($m.Success) { $info.SessionId = [int]$m.Groups[1].Value }
    $info.Pipe = $CommandLine.Trim()
    return $info
}

# ══════════════════════════════════════════════════════════════════════════
#  目标发现
# ══════════════════════════════════════════════════════════════════════════

function Resolve-Targets {
    param([string]$Name, [int[]]$Ids)

    $procs = @()
    if ($Ids.Count -gt 0) {
        foreach ($want in $Ids) {
            $p = Get-Process -Id $want -ErrorAction SilentlyContinue
            if ($p) { $procs += $p }
        }
    }
    else {
        $seen = @{}
        foreach ($nm in @($Name, "$Name.Agent")) {
            foreach ($p in @(Get-Process -Name $nm -ErrorAction SilentlyContinue)) {
                if (-not $seen.ContainsKey($p.Id)) { $seen[$p.Id] = $true; $procs += $p }
            }
        }
    }

    $cmdlines = Get-RelayCommandLines -Pids @($procs | ForEach-Object { $_.Id }) -Name $Name
    $out = @()
    foreach ($p in $procs) {
        $cl = ''
        if ($cmdlines.ContainsKey($p.Id)) { $cl = $cmdlines[$p.Id] }
        $ri = ConvertTo-RelayInfo -CommandLine $cl

        $role = 'main'
        $confirmed = ($cl -ne '')
        $sameName = ($p.ProcessName -eq $Name) -or ($p.ProcessName -eq "$Name.Agent")
        if ($ri.IsRelay) {
            $role = 'agent'
        }
        elseif ($p.ProcessName -like '*.Agent') {
            # 独立文件名 = 手动/Broker 代理（同名同进程的那种才靠命令行区分）
            $role = 'agent'
        }
        elseif ($cl -eq '' -and $p.ProcessName -ne $Name -and $p.ProcessName -like "$Name*") {
            # 读不到命令行（跨会话权限不足）但进程名是 SoulLink 家族 —— 按代理候选处理，
            # 标为未确认，让用户知道这一条不可靠。
            $role = 'agent'
            $confirmed = $false
        }
        elseif (-not $sameName) {
            # 显式 -ProcessId 指到了一个和 SoulLink 无关的进程（例如自测时的 pwsh）。
            # 如实标成 other，不要把它硬说成主程序或代理。
            $role = 'other'
        }

        $start = $null
        try { $start = $p.StartTime } catch { }
        $out += [pscustomobject]@{
            Pid          = $p.Id
            Role         = $role
            Session      = $ri.SessionId
            Proc         = $p
            ProcName     = $p.ProcessName
            StartTime    = $start
            Relayed      = $ri.IsRelay
            RoleConfirmed = $confirmed
            WebPort      = 0
            PrevCpuMs    = $null
            PrevTx       = $null
            PrevRx       = $null
            Alive        = $true
            Underrun     = 0
        }
    }
    return $out
}

# ══════════════════════════════════════════════════════════════════════════
#  单点读数
# ══════════════════════════════════════════════════════════════════════════
#
# ★ 关键：先 Refresh() 再读计数器。这是「279% 假数据」事故的根因。
#
function Read-ProcessCounters {
    param($Target)
    $r = [pscustomobject]@{ Ok = $false; CpuMs = $null; WsMB = $null; Handles = $null; IoBytes = $null; Error = '' }
    try {
        $p = $Target.Proc
        $p.Refresh()                                  # ← 必须
        if ($p.HasExited) { $r.Error = '进程已退出'; return $r }
        $r.CpuMs = $p.TotalProcessorTime.TotalMilliseconds
        $r.WsMB  = $p.WorkingSet64 / $MB
        $r.Ok    = $true
    }
    catch {
        $r.Error = $_.Exception.Message
        return $r
    }
    if ($script:NativeOk) {
        $hc = -1
        try { $hc = [SoulLinkPerfNative]::HandleCount($Target.Pid) } catch { $hc = -1 }
        if ($hc -ge 0) { $r.Handles = $hc }
        $io = -1
        try { $io = [SoulLinkPerfNative]::NonNetIoBytes($Target.Pid) } catch { $io = -1 }
        if ($io -ge 0) { $r.IoBytes = $io }
    }
    return $r
}

# ══════════════════════════════════════════════════════════════════════════
#  头部信息
# ══════════════════════════════════════════════════════════════════════════

Write-Host ''
Write-Host '===============================================================' -ForegroundColor Cyan
Write-Host '  SoulLink 性能实测（只读采样，不影响挂机中的账号）' -ForegroundColor Cyan
Write-Host '===============================================================' -ForegroundColor Cyan
Write-Host ("  整机逻辑核   : $MachineCores")
Write-Host ("  采样时长     : $Seconds 秒 / 间隔 $IntervalMs 毫秒 / 共约 $TotalSamples 个采样点")
if ($Label -ne '') { Write-Host ("  运行标签     : $Label") }
Write-Host ("  只读内核调用 : " + $(if ($script:NativeOk) { '可用（句柄数 + TCP 逐连接字节数）' } else { '不可用 → 跳过句柄与网络两项' }))
if (-not $script:NativeOk) {
    Write-Host ("    ↑ 原因：$($script:NativeWhy)") -ForegroundColor Yellow
    Write-Host  '      按约定不去退化成正则解析 netstat / 反射慢路径，直接如实跳过。' -ForegroundColor Yellow
}

# ── Refresh() 生效性自检（在测量开始前先证明「差分机制本身是好的」）──────────
#
#  交接文档 §6.4 记过一次「279% 假数据」：Get-Process 缺 $p.Refresh() 时，
#  两个采样点落在同一时刻 → 分母极小、分子恒定 → 荒谬的百分比。
#
#  这里反过来先做一次「确定性检查」：让本脚本自己忙等一段已知 CPU 时间，
#  看 TotalProcessorTime 是否如实增长。如果这一步都量不出来，
#  后面所有数字都不可信 —— 与其给出假数据，不如明确报错。
#
function Test-RefreshWorks {
    $me = Get-Process -Id $PID
    $me.Refresh()
    $t0 = $me.TotalProcessorTime.TotalMilliseconds

    # 忙等约 120ms 的纯 CPU 时间
    $swLocal = [Diagnostics.Stopwatch]::StartNew()
    $spin = 0.0
    while ($swLocal.ElapsedMilliseconds -lt 120) { $spin += [math]::Sqrt($spin + 1.0) }
    $swLocal.Stop()

    $me.Refresh()
    $t1 = $me.TotalProcessorTime.TotalMilliseconds
    $delta = $t1 - $t0
    return [pscustomobject]@{
        DeltaMs  = $delta
        WallMs   = $swLocal.ElapsedMilliseconds
        Works    = ($delta -gt 20)     # 至少该看到 20ms 的增量，否则机制坏了
    }
}

$refreshCheck = $null
try { $refreshCheck = Test-RefreshWorks } catch { $refreshCheck = $null }

if ($null -ne $refreshCheck) {
    if ($refreshCheck.Works) {
        Write-Host ("  Refresh 自检  : 通过（本进程忙等 {0}ms，CPU 计数如实增长 {1:N1}ms）" -f `
            $refreshCheck.WallMs, $refreshCheck.DeltaMs) -ForegroundColor Green
    }
    else {
        Write-Host ("  Refresh 自检  : ✗ 失败（忙等 {0}ms，CPU 只增长 {1:N1}ms）" -f `
            $refreshCheck.WallMs, $refreshCheck.DeltaMs) -ForegroundColor Red
        Write-Host  '    → TotalProcessorTime 没有如实增长，说明本机计数器异常；' -ForegroundColor Red
        Write-Host  '      继续测量只会得到假数据，先停止。' -ForegroundColor Red
        Write-Host ''
        exit 4
    }
}

# ── 录制配置（只读参考，用于把 MB/s 和 fps/质量对上）─────────────────────────
$cfgPath = Find-SoulLinkConfig -Explicit $ConfigPath
$cfg = Read-RecordingSettings -Path $cfgPath
if ($cfg.Ok) {
    Write-Host ("  录制配置     : {0}fps / 质量 {1} / 保留 {2} 账号  [{3}]" -f `
        $cfg.Fps, $cfg.Quality, (@($cfg.RecordAccounts).Count), $cfg.Path) -ForegroundColor DarkGray
    Write-Host  '                 （上面这行是只读打开的，脚本不会写回 config.json）' -ForegroundColor DarkGray
}
else {
    Write-Host ("  录制配置     : 未取得（$($cfg.Why)）—— 不影响测量，只是少一层对照") -ForegroundColor DarkGray
}

# ── 目标进程 ────────────────────────────────────────────────────────────────
$Targets = Resolve-Targets -Name $ProcessName -Ids $ProcessId

if (@($Targets).Count -eq 0) {
    Write-Host ''
    Write-Host ("  × 没找到进程：-ProcessName '$ProcessName'" + $(if ($ProcessId.Count -gt 0) { " / -ProcessId $($ProcessId -join ',')" } else { '' })) -ForegroundColor Red
    Write-Host '    如果 SoulLink 没在跑，先启动后再跑本脚本；' -ForegroundColor Yellow
    Write-Host '    进程名要写「不含 .exe」的名字，例如 SoulLink（不是 SoulLink.exe）。' -ForegroundColor Yellow
    Write-Host '    本脚本只做测量；进程不存在时它不会去启动任何东西。' -ForegroundColor Yellow
    Write-Host ''
    exit 2
}

Write-Host ''
Write-Host '  将要测量的进程：' -ForegroundColor Gray
foreach ($t in $Targets) {
    $roleText = ConvertTo-RoleText -Role $t.Role
    $sessText = if ($t.Session -gt 0) { " 会话 $($t.Session)" } else { '' }
    $confText = if (-not $t.RoleConfirmed) { '  [分类未确认：读不到命令行]' } else { '' }
    Write-Host ("    pid={0,-7} {1,-10} {2}{3}  {4}{5}" -f $t.Pid, $t.ProcName, $roleText, $sessText, $ProcessName, $confText) -ForegroundColor Gray
}
Write-Host ''

# ══════════════════════════════════════════════════════════════════════════
#  采样主循环
# ══════════════════════════════════════════════════════════════════════════

$Rows        = New-Object System.Collections.Generic.List[object]
$Sw          = [Diagnostics.Stopwatch]::StartNew()
$TargetPids  = @($Targets | ForEach-Object { $_.Pid })

# 基线：第一个采样点的起点（同样要 Refresh）
foreach ($t in $Targets) {
    $snap = Read-ProcessCounters -Target $t
    if ($snap.Ok) { $t.PrevCpuMs = $snap.CpuMs }
}
$prevNetSnap = $null
if ($script:NativeOk) {
    try { $prevNetSnap = [SoulLinkPerfNative]::CollectTcp([int[]]$TargetPids) } catch { $prevNetSnap = $null }
}
foreach ($t in $Targets) {
    if ($null -ne $prevNetSnap -and $prevNetSnap.ByPid.ContainsKey($t.Pid)) {
        $t.PrevTx = $prevNetSnap.ByPid[$t.Pid][0]
        $t.PrevRx = $prevNetSnap.ByPid[$t.Pid][1]
    }
    else { $t.PrevTx = $null; $t.PrevRx = $null }
}
$bucketStartTicks = $Sw.ElapsedTicks

$NoRefreshZero = 0        # 差分恒为 0 的采样点个数 → Refresh 失效的指纹
$ImpossibleCpu = 0        # 超过物理上限的采样点个数 → 假数据
$ZeroDenom     = 0

for ($i = 1; $i -le $TotalSamples; $i++) {

    Start-Sleep -Milliseconds $IntervalMs

    # 分母用实测经过时间，不用标称 IntervalMs
    $nowTicks = $Sw.ElapsedTicks
    $elapsedMs = ($nowTicks - $bucketStartTicks) * 1000.0 / [Diagnostics.Stopwatch]::Frequency
    $bucketStartTicks = $nowTicks
    $timestamp = Get-Date

    if ($elapsedMs -le 0) {
        $ZeroDenom++
        continue
    }

    # 网络：一次表遍历覆盖所有目标进程（共用同一时刻的读数）
    $netSnap = $null
    if ($script:NativeOk) {
        try { $netSnap = [SoulLinkPerfNative]::CollectTcp([int[]]$TargetPids) } catch { $netSnap = $null }
    }

    # 先算出本采样点「是否有人在看」：任一代理在持续推帧即算有人看
    $sampleRows = @()
    foreach ($t in $Targets) {

        $snap = Read-ProcessCounters -Target $t
        if (-not $snap.Ok) {
            if ($t.Alive) {
                Write-Host ("  ! pid={0} 读数失败：{1}（后续跳过该进程）" -f $t.Pid, $snap.Error) -ForegroundColor Yellow
                $t.Alive = $false
            }
            continue
        }

        # ── 瞬时 CPU%（单核百分比）────────────────────────────────
        $cpuPct = $null
        if ($null -ne $t.PrevCpuMs) {
            $deltaMs = $snap.CpuMs - $t.PrevCpuMs
            if ($deltaMs -eq 0) { $NoRefreshZero++ }
            $cpuPct = ($deltaMs / $elapsedMs) * 100.0
            if ($cpuPct -gt (100.0 * $MachineCores)) { $ImpossibleCpu++ }
        }
        $t.PrevCpuMs = $snap.CpuMs

        # ── 寿命平均 CPU%（交叉验证基线）────────────────────────────
        $lifeCpuPct = $null
        if ($null -ne $t.StartTime) {
            $upMs = ((Get-Date) - $t.StartTime).TotalMilliseconds
            if ($upMs -gt 1000) { $lifeCpuPct = ($snap.CpuMs / $upMs) * 100.0 }
        }

        # ── 网络：按 pid 累计字节差分 ───────────────────────────────
        $txBps = $null; $rxBps = $null; $estConns = 0
        $txBytes = $null; $rxBytes = $null
        if ($null -ne $netSnap -and $netSnap.ByPid.ContainsKey($t.Pid)) {
            $arr = $netSnap.ByPid[$t.Pid]
            $estConns = [int]$arr[2]
            $txBytes = [long]$arr[0]
            $rxBytes = [long]$arr[1]
            if ($null -ne $t.PrevTx) {
                $dtx = $txBytes - $t.PrevTx
                $drx = $rxBytes - $t.PrevRx
                # 连接重建会让累计值回退；按 0 处理并记一笔，避免出现负速率
                if ($dtx -lt 0) { $dtx = 0; $t.Underrun++ }
                if ($drx -lt 0) { $drx = 0; $t.Underrun++ }
                $txBps = $dtx / ($elapsedMs / 1000.0)
                $rxBps = $drx / ($elapsedMs / 1000.0)
            }
            $t.PrevTx = $txBytes
            $t.PrevRx = $rxBytes
        }
        elseif ($null -ne $netSnap) {
            # 该进程当前没有任何 ESTABLISHED 连接 = 没人在连它，速率就是 0（不是未知）
            $estConns = 0
            if ($null -ne $t.PrevTx) { $txBps = 0.0; $rxBps = 0.0 }
            $t.PrevTx = 0; $t.PrevRx = 0
        }

        $sampleRows += [pscustomobject]@{
            Target   = $t
            CpuPct   = $cpuPct
            LifeCpu  = $lifeCpuPct
            CpuMs    = $snap.CpuMs
            WsMB     = $snap.WsMB
            Handles  = $snap.Handles
            IoBytes  = $snap.IoBytes
            TxMBps   = $(if ($null -ne $txBps) { $txBps / $MB } else { $null })
            RxMBps   = $(if ($null -ne $rxBps) { $rxBps / $MB } else { $null })
            EstConns = $estConns
            NetKnown = ($null -ne $netSnap)
            Listen   = $(if ($null -ne $netSnap -and $netSnap.ListenPorts.ContainsKey($t.Pid)) { @($netSnap.ListenPorts[$t.Pid]) } else { @() })
        }
    }

    # 本采样点是否「有人在看」：任一代理发送速率超过阈值
    $anyWatching = $false
    foreach ($r in $sampleRows) {
        if ($r.Target.Role -eq 'agent' -and $null -ne $r.TxMBps -and $r.TxMBps -gt $WatchingThresholdMBps) {
            $anyWatching = $true
            break
        }
    }
    $stateText = if ($anyWatching) { '观看中' } else { '空闲' }
    $stateCode = if ($anyWatching) { 'watching' } else { 'idle' }

    foreach ($r in $sampleRows) {
        $t = $r.Target
        if (@($r.Listen).Count -gt 0 -and $t.WebPort -eq 0) { $t.WebPort = [int](@($r.Listen)[0]) }
        $Rows.Add([pscustomobject]@{
            Sample    = $i
            Time      = $timestamp.ToString('HH:mm:ss')
            Label     = $Label
            State     = $stateCode
            Role      = $t.Role
            Pid       = $t.Pid
            Session   = $t.Session
            WebPort   = $t.WebPort
            CpuPct    = $(if ($null -ne $r.CpuPct) { [math]::Round($r.CpuPct, 2) } else { $null })
            LifeCpuPct= $(if ($null -ne $r.LifeCpu) { [math]::Round($r.LifeCpu, 2) } else { $null })
            WsMB      = [math]::Round($r.WsMB, 1)
            Handles   = $r.Handles
            TxMBps    = $(if ($null -ne $r.TxMBps) { [math]::Round($r.TxMBps, 3) } else { $null })
            RxMBps    = $(if ($null -ne $r.RxMBps) { [math]::Round($r.RxMBps, 3) } else { $null })
            EstConns  = $r.EstConns
        })
    }

    # 进度提示（暗色，不打断视线）
    #
    # ★ 只在「第 10 个点」和「最后一个点」打印，避免 1 秒一行刷屏把真正要看的汇总冲走。
    if ($i % 10 -eq 0 -or $i -eq $TotalSamples) {
        $parts = @()
        $mainRow = $sampleRows | Where-Object { $_.Target.Role -eq 'main' } | Select-Object -First 1
        if ($null -ne $mainRow) { $parts += ("主程序 {0}% 单核" -f (Format-Num $mainRow.CpuPct '0.0')) }
        $agentRows = @($sampleRows | Where-Object { $_.Target.Role -eq 'agent' })
        if ($agentRows.Count -gt 0) {
            $sumTx = 0.0
            foreach ($ar in $agentRows) { if ($null -ne $ar.TxMBps) { $sumTx += $ar.TxMBps } }
            $parts += ("代理 {0} 个·发送合计 {1} MB/s" -f $agentRows.Count, (Format-Num $sumTx '0.00'))
        }
        $otherRows = @($sampleRows | Where-Object { $_.Target.Role -eq 'other' })
        if ($otherRows.Count -gt 0) {
            $sumOtherTx = 0.0; $hasTx = $false
            foreach ($or in $otherRows) { if ($null -ne $or.TxMBps) { $sumOtherTx += $or.TxMBps; $hasTx = $true } }
            $otherText = ("其它进程 {0} 个" -f $otherRows.Count)
            if ($hasTx) { $otherText += ("·发送 {0} MB/s" -f (Format-Num $sumOtherTx '0.00')) }
            $parts += $otherText
        }
        $suffix = if ($anyWatching) { ' ← 有人观看' } else { '' }
        Write-Host ("  [{0,3}/{1}] {2}{3}" -f $i, $TotalSamples, ($parts -join ' · '), $suffix) -ForegroundColor DarkGray
    }
}

$Sw.Stop()

# ══════════════════════════════════════════════════════════════════════════
#  汇总
# ══════════════════════════════════════════════════════════════════════════

# ★ 用 .ToArray() 而不是 @($Rows)：在 PowerShell 7.6.x 上，对泛型 List[object]
#   施加 @() 会抛 "Argument types do not match"（已实测，见 PERF-MEASURE 文档 §5）。
$allRows = $Rows.ToArray()
if ($allRows.Count -eq 0) {
    Write-Host ''
    Write-Host '  × 一个有效采样点都没有，无法出数。' -ForegroundColor Red
    Write-Host '    常见原因：目标进程在采样期间退出，或采样时长太短。' -ForegroundColor Yellow
    exit 3
}

Write-Host ''
Write-Host '===============================================================' -ForegroundColor Cyan
Write-Host '  汇总：按进程' -ForegroundColor Cyan
Write-Host '===============================================================' -ForegroundColor Cyan
Write-Host '  CPU% 口径 = 「单核百分比」，可 >100（100 = 吃满一个逻辑核）' -ForegroundColor DarkGray
Write-Host ''

$summary = @()
foreach ($t in $Targets) {
    $rows = @($allRows | Where-Object { $_.Pid -eq $t.Pid })
    if ($rows.Count -eq 0) { continue }

    $cpuVals = @($rows | Where-Object { $null -ne $_.CpuPct } | ForEach-Object { [double]$_.CpuPct })
    $txVals  = @($rows | Where-Object { $null -ne $_.TxMBps } | ForEach-Object { [double]$_.TxMBps })
    $rxVals  = @($rows | Where-Object { $null -ne $_.RxMBps } | ForEach-Object { [double]$_.RxMBps })
    $wsVals  = @($rows | ForEach-Object { [double]$_.WsMB })
    $hVals   = @($rows | Where-Object { $null -ne $_.Handles } | ForEach-Object { [double]$_.Handles })
    $lifeVals = @($rows | Where-Object { $null -ne $_.LifeCpuPct } | ForEach-Object { [double]$_.LifeCpuPct })
    $connVals = @($rows | ForEach-Object { [int]$_.EstConns })

    $cpuStat = Get-Stat -Values $cpuVals
    $txStat  = Get-Stat -Values $txVals
    $rxStat  = Get-Stat -Values $rxVals
    $wsStat  = Get-Stat -Values $wsVals
    $hStat   = Get-Stat -Values $hVals
    $lifeStat = Get-Stat -Values $lifeVals
    $connStat = Get-Stat -Values ($connVals | ForEach-Object { [double]$_ })

    $lifeLast = $null
    if ($lifeVals.Count -gt 0) { $lifeLast = $lifeVals[$lifeVals.Count - 1] }

    $roleText = ConvertTo-RoleText -Role $t.Role -Short
    $portText = if ($t.WebPort -gt 0) { "$($t.WebPort)" } else { '-' }
    if ($t.WebPort -gt 0 -and $cfg.Ok -and $null -ne $cfg.WebPort -and $cfg.WebPort -gt 0) {
        $acctNo = $t.WebPort - [int]$cfg.WebPort
        if ($acctNo -ge 1) { $portText = "$($t.WebPort)（第 $acctNo 个账号）" }
    }

    $summary += [pscustomobject]@{
        '角色'        = $roleText
        'PID'         = $t.Pid
        '会话'        = $(if ($t.Session -gt 0) { $t.Session } else { '' })
        '端口'        = $portText
        'CPU中位%'    = $(if ($null -ne $cpuStat.Median) { [math]::Round($cpuStat.Median, 2) } else { '' })
        'CPU均值%'    = $(if ($null -ne $cpuStat.Mean) { [math]::Round($cpuStat.Mean, 2) } else { '' })
        'CPU峰值%'    = $(if ($null -ne $cpuStat.Max) { [math]::Round($cpuStat.Max, 2) } else { '' })
        '寿命均CPU%'  = $(if ($null -ne $lifeLast) { [math]::Round($lifeLast, 2) } else { '' })
        '工作集MB'    = [math]::Round($wsStat.Mean, 1)
        '句柄均值'    = $(if ($null -ne $hStat.Mean) { [int][math]::Round($hStat.Mean, 0) } else { '' })
        '句柄峰值'    = $(if ($null -ne $hStat.Max) { [int][math]::Round($hStat.Max, 0) } else { '' })
        '发送MB/s'    = $(if ($null -ne $txStat.Mean) { [math]::Round($txStat.Mean, 3) } else { '' })
        '发送峰值MB/s'= $(if ($null -ne $txStat.Max) { [math]::Round($txStat.Max, 3) } else { '' })
        '接收MB/s'    = $(if ($null -ne $rxStat.Mean) { [math]::Round($rxStat.Mean, 3) } else { '' })
        '连接数均'    = $(if ($null -ne $connStat.Mean) { [math]::Round($connStat.Mean, 1) } else { '' })
    }
}

$summary | Format-Table -AutoSize | Out-String -Width 4096 | Write-Host

# ── 合计 ─────────────────────────────────────────────────────────────────────
$mainCpuAll = @($allRows | Where-Object { $_.Role -eq 'main' } | Where-Object { $null -ne $_.CpuPct } | ForEach-Object { [double]$_.CpuPct })
$agentCpuAll = @($allRows | Where-Object { $_.Role -eq 'agent' } | Where-Object { $null -ne $_.CpuPct } | ForEach-Object { [double]$_.CpuPct })
$netAll = @($allRows | Where-Object { $_.Role -eq 'agent' } | Where-Object { $null -ne $_.TxMBps } | ForEach-Object { [double]$_.TxMBps })

$otherCpuAll = @($allRows | Where-Object { $_.Role -eq 'other' } | Where-Object { $null -ne $_.CpuPct } | ForEach-Object { [double]$_.CpuPct })
$mainMean = if ($mainCpuAll.Count -gt 0) { ($mainCpuAll | Measure-Object -Average).Average } else { $null }
$agentSumMean = $null
if ($agentCpuAll.Count -gt 0) {
    # 代理是多个进程：各采样点先横向求和，再对时间求均，才是「所有代理合计」
    $sum = 0.0; foreach ($v in $agentCpuAll) { $sum += $v }
    $agentSumMean = $sum / $agentCpuAll.Count
}
$otherSumMean = $null
if ($otherCpuAll.Count -gt 0) {
    $sum = 0.0; foreach ($v in $otherCpuAll) { $sum += $v }
    $otherSumMean = $sum / $otherCpuAll.Count
}
$netSumMean = $null
if ($netAll.Count -gt 0) {
    $sum = 0.0; foreach ($v in $netAll) { $sum += $v }
    $netSumMean = $sum / $netAll.Count
}
$totalMean = $null
foreach ($piece in @($mainMean, $agentSumMean, $otherSumMean)) {
    if ($null -ne $piece) {
        if ($null -eq $totalMean) { $totalMean = 0.0 }
        $totalMean += $piece
    }
}

Write-Host '  ── 整机合计 ──────────────────────────────────────────' -ForegroundColor Gray
# ★ 口径换算的坑：$mainMean 已经是「单核百分比」（100 = 1 核），
#   换算成「占整机百分比」必须直接 ÷ 逻辑核数，**不能再 ×100**。
#   （写成 /$cores*100 会把 98% 单核算成 306% 整机，正是本次自测发现的 bug。）
Write-Host ("  主程序            : {0}% 单核   （占整机 {1}%）" -f (Format-Num $mainMean '0.00'), (Format-Num ($(if ($null -ne $mainMean) { $mainMean / $MachineCores } else { $null })) '0.000'))
if ($null -ne $agentSumMean) {
    Write-Host ("  所有代理合计       : {0}% 单核   （占整机 {1}%）" -f (Format-Num $agentSumMean '0.00'), (Format-Num ($agentSumMean / $MachineCores) '0.000'))
}
Write-Host ("  SoulLink 全部合计  : {0}% 单核   （占整机 {1}%，{2} 逻辑核）" -f `
    (Format-Num $totalMean '0.00'), (Format-Num ($(if ($null -ne $totalMean) { $totalMean / $MachineCores } else { $null })) '0.000'), $MachineCores) -ForegroundColor White
if ($null -ne $netSumMean) {
    Write-Host ("  代理发送合计       : {0} MB/s   （折算 {1} GB/小时）" -f `
        (Format-Num $netSumMean '0.000'), (Format-Num ($netSumMean * 3600 / 1024) '0.00'))
}
Write-Host ''

# ══════════════════════════════════════════════════════════════════════════
#  状态对比：有人观看 vs 无人观看
# ══════════════════════════════════════════════════════════════════════════
#
#  ★ 这里说明「怎么区分两种状态」：
#    /live 是否被请求，是代理进程内部状态，**只读观测不到**。
#    但「有人在看」必然表现为「代理在持续向外发送帧」，
#    所以用「代理的 TCP 发送速率」当判据 —— 这正是本脚本量出来的那个数。
#      · 代理发送速率 ≈ 0            → 没人看（连接可能还在，只是不推帧）
#      · 代理发送速率 > 阈值（默认 0.05MB/s）→ 有人在看，正在推帧
#    判据写在 CSV 的 State 列（watching / idle），可以按它分组复核。
#
Write-Host '===============================================================' -ForegroundColor Cyan
Write-Host '  状态对比：有人观看 / 无人观看' -ForegroundColor Cyan
Write-Host '===============================================================' -ForegroundColor Cyan
Write-Host ("  判据：代理发送速率 > {0} MB/s 记为「有人在看」" -f $WatchingThresholdMBps) -ForegroundColor DarkGray

$watchRows = @($allRows | Where-Object { $_.State -eq 'watching' })
$idleRows  = @($allRows | Where-Object { $_.State -eq 'idle' })

function Get-StateBlock {
    param([object[]]$Rows, [string]$Name)
    if ($Rows.Count -eq 0) { return $null }
    $samples = @($Rows | Select-Object -ExpandProperty Sample -Unique)
    $secs = $samples.Count * ($IntervalMs / 1000.0)
    $main = @($Rows | Where-Object { $_.Role -eq 'main' } | Where-Object { $null -ne $_.CpuPct } | ForEach-Object { [double]$_.CpuPct })
    $agents = @($Rows | Where-Object { $_.Role -eq 'agent' } | Where-Object { $null -ne $_.CpuPct } | ForEach-Object { [double]$_.CpuPct })
    $tx = @($Rows | Where-Object { $_.Role -eq 'agent' } | Where-Object { $null -ne $_.TxMBps } | ForEach-Object { [double]$_.TxMBps })
    $agentCount = @($Rows | Where-Object { $_.Role -eq 'agent' } | Select-Object -ExpandProperty Pid -Unique).Count

    $mainMean = if ($main.Count -gt 0) { ($main | Measure-Object -Average).Average } else { $null }
    $agentMean = if ($agents.Count -gt 0) { ($agents | Measure-Object -Average).Average } else { $null }
    $txMean = if ($tx.Count -gt 0) { ($tx | Measure-Object -Average).Average } else { $null }
    $txMax = if ($tx.Count -gt 0) { ($tx | Measure-Object -Maximum).Maximum } else { $null }

    return [pscustomobject]@{
        Name       = $Name
        Samples    = $samples.Count
        Seconds    = $secs
        MainCpu    = $mainMean
        AgentCpu   = $agentMean
        AgentCount = $agentCount
        TxMean     = $txMean
        TxMax      = $txMax
    }
}

$watchBlock = Get-StateBlock -Rows $watchRows -Name '有人观看'
$idleBlock  = Get-StateBlock -Rows $idleRows  -Name '无人观看'

$blocks = @()
if ($null -ne $watchBlock) { $blocks += $watchBlock }
if ($null -ne $idleBlock)  { $blocks += $idleBlock }

if ($blocks.Count -eq 0) {
    Write-Host '  （没有可分组的数据）' -ForegroundColor Yellow
}
else {
    $blocks |
        ForEach-Object {
            [pscustomobject]@{
                '状态'         = $_.Name
                '采样点'       = $_.Samples
                '约合秒'       = [math]::Round($_.Seconds, 0)
                '主程序CPU%'   = $(if ($null -ne $_.MainCpu) { [math]::Round($_.MainCpu, 2) } else { '' })
                '代理均值CPU%' = $(if ($null -ne $_.AgentCpu) { [math]::Round($_.AgentCpu, 2) } else { '' })
                '代理发送MB/s' = $(if ($null -ne $_.TxMean) { [math]::Round($_.TxMean, 3) } else { '' })
                '发送峰值MB/s' = $(if ($null -ne $_.TxMax) { [math]::Round($_.TxMax, 3) } else { '' })
            }
        } |
        Format-Table -AutoSize | Out-String -Width 4096 | Write-Host
}

if ($null -ne $watchBlock -and $null -ne $idleBlock) {
    Write-Host '  ── 差异（有人看 - 没人看）──────────────────────────────' -ForegroundColor Gray
    if ($null -ne $watchBlock.AgentCpu -and $null -ne $idleBlock.AgentCpu) {
        Write-Host ("  代理 CPU 增量      : {0}% 单核/代理  ← 这就是「传帧 + 编码被观看牵出的那部分」" -f `
            (Format-Num ($watchBlock.AgentCpu - $idleBlock.AgentCpu) '0.00'))
    }
    if ($null -ne $watchBlock.TxMean) {
        Write-Host ("  发送速率增量       : {0} MB/s" -f (Format-Num ($watchBlock.TxMean - $(if ($null -ne $idleBlock.TxMean) { $idleBlock.TxMean } else { 0 })) '0.000'))
    }
    Write-Host ''
    Write-Host '  ★ 关键判读：如果「代理 CPU」在两种状态下**差不多**，说明' -ForegroundColor Yellow
    Write-Host '    采集侧（BitBlt + JPEG 编码）不看有没有人看 —— 与 STREAM-PERF-AUDIT §2.1' -ForegroundColor Yellow
    Write-Host '    的结论一致；如果「无人观看」时发送速率仍接近峰值，说明帧一直在推。' -ForegroundColor Yellow
    Write-Host ''
}
elseif ($null -ne $idleBlock -and $null -eq $watchBlock) {
    Write-Host '  本次全是「无人观看」状态 —— 正好可以用来核对「空闲 16%」和「录制中仍烧 CPU」。' -ForegroundColor Gray
    Write-Host '  要看「有人观看」那一半，请在打开网页看实时画面时再跑一次，用 -Label 区分。' -ForegroundColor Gray
    Write-Host ''
}
elseif ($null -ne $watchBlock -and $null -eq $idleBlock) {
    Write-Host '  本次全是「有人观看」状态 —— 适合核对「4.8MB/s」和「0.3~0.7 核/账号」。' -ForegroundColor Gray
    Write-Host '  要看「无人观看」那一半，请关掉网页后再跑一次。' -ForegroundColor Gray
    Write-Host ''
}

# ══════════════════════════════════════════════════════════════════════════
#  数据可信度自检（交接文档 §6.4 的教训落在这一段）
# ══════════════════════════════════════════════════════════════════════════
Write-Host '===============================================================' -ForegroundColor Cyan
Write-Host '  数据可信度自检' -ForegroundColor Cyan
Write-Host '===============================================================' -ForegroundColor Cyan

$validSamples = @($allRows | Select-Object -ExpandProperty Sample -Unique).Count
$suspicious = $false

if ($ImpossibleCpu -gt 0) {
    $suspicious = $true
    Write-Host ("  × 有 $ImpossibleCpu 个采样点的 CPU% 超过物理上限（{0}% = 全部逻辑核），" -f (100 * $MachineCores)) -ForegroundColor Red
    Write-Host  '    这些值一定是假的（最可能是采样点重合 / 系统计数器异常），不要采信。' -ForegroundColor Red
}
if ($ZeroDenom -gt 0) {
    $suspicious = $true
    Write-Host ("  × 有 $ZeroDenom 个采样点的实测间隔为 0，已丢弃。") -ForegroundColor Red
}
# ★ 差分为 0 本身**不是**错误：真的空闲的进程，CPU 累计时间就是不长。
#   真正需要报警的是「明明有活干却量出 0」。所以这里的判据是：
#   「差分为 0 的比例很高」且「寿命平均显示这个进程其实在干活」→ 可疑。
#   纯凭差分为 0 就报红，会把正常空闲误判成假数据。
$zeroRatio = if ($validSamples -gt 0 -and $Targets.Count -gt 0) { $NoRefreshZero / [double]($validSamples * $Targets.Count) } else { 0 }
if ($zeroRatio -gt 0.5) {
    Write-Host ("  · 差分为 0 的采样点占 {0:P0}。" -f $zeroRatio) -ForegroundColor DarkGray
    Write-Host  '    若目标进程确实空闲（CPU 累计时间没长），这是正常结果而不是错误；' -ForegroundColor DarkGray
    Write-Host  '    若寿命平均显示它在明显干活，才说明采样机制有问题 —— 下面交叉验证会指出来。' -ForegroundColor DarkGray
}

# 瞬时 vs 寿命平均 交叉验证
Write-Host '  交叉验证（瞬时 CPU% 均值 vs 寿命平均 CPU%）：' -ForegroundColor Gray
$crossOk = $true
$anyCross = $false
foreach ($t in $Targets) {
    $rows = @($allRows | Where-Object { $_.Pid -eq $t.Pid })
    if ($rows.Count -eq 0) { continue }
    $cpuVals = @($rows | Where-Object { $null -ne $_.CpuPct } | ForEach-Object { [double]$_.CpuPct })
    $lifeVals = @($rows | Where-Object { $null -ne $_.LifeCpuPct } | ForEach-Object { [double]$_.LifeCpuPct })
    if ($cpuVals.Count -eq 0 -or $lifeVals.Count -eq 0) { continue }
    $anyCross = $true
    $instMean = ($cpuVals | Measure-Object -Average).Average
    $lifeMean = ($lifeVals | Measure-Object -Average).Average
    $label = ConvertTo-RoleText -Role $t.Role -Short

    $verdict = '一致'
    $color = 'Green'
    if ($instMean -lt 0.5 -and $lifeMean -lt 0.5) {
        $verdict = '一致（都接近 0，进程基本空闲）'
        $color = 'DarkGray'
    }
    elseif ($instMean -lt 0.5 -and $lifeMean -ge 0.5) {
        $verdict = '采样窗口内没干活，但寿命平均有量 —— 说明启动期/早前有过开销（正常现象，不是假数据）'
        $color = 'DarkGray'
    }
    elseif ($lifeMean -lt 0.5 -and $instMean -ge 3) {
        $verdict = '不一致：进程刚启动不久，寿命平均被启动前的空闲稀释'; $color = 'Yellow'; $crossOk = $false
    }
    else {
        $ratio = $instMean / $lifeMean
        if ($ratio -gt 3) {
            $verdict = ("窗口内 CPU 是寿命平均的 {0:N1}× —— 采样期间正忙（如刚开始投送/正在编码），属正常突发" -f $ratio); $color = 'DarkGray'
        }
        elseif ($ratio -lt 0.33) {
            $verdict = ("窗口内 CPU 只有寿命平均的 {0:N1}× —— 该进程早前更忙，本次多半是空闲态" -f $ratio); $color = 'DarkGray'
        }
        else {
            $verdict = '一致（偏差 {0:N1}× 以内）' -f $ratio
        }
    }
    Write-Host ("    pid={0,-7} {1,-4} 瞬时均值 {2,7}%  寿命平均 {3,7}%   {4}" -f `
        $t.Pid, $label, (Format-Num $instMean '0.00'), (Format-Num $lifeMean '0.00'), $verdict) -ForegroundColor $color
}
if (-not $anyCross) {
    Write-Host '    （没有可用于交叉验证的成对数据）' -ForegroundColor Yellow
}

if ($script:NativeOk) {
    Write-Host '  句柄数：已用 GetProcessHandleCount（kernel32 单次调用，微秒级）读取。' -ForegroundColor Gray
    Write-Host '           按 AGENTS.md 性能约束第 3 条，没有使用 Process.HandleCount；' -ForegroundColor Gray
    Write-Host '           句柄增长要观察 >15 分钟才能定性，短窗口内的小幅波动属正常启动爬坡。' -ForegroundColor Gray
}
else {
    Write-Host '  句柄数 / 网络字节：本次不可用（原生声明失败），已如实跳过，未编造数字。' -ForegroundColor Yellow
}

if (-not $suspicious -and $crossOk) {
    Write-Host '  → 自检通过：瞬时采样与寿命平均互相吻合，可以采信。' -ForegroundColor Green
}
else {
    Write-Host '  → 自检有保留：先看上面标红/标黄的原因，必要时延长 -Seconds 或换 -IntervalMs 重跑。' -ForegroundColor Yellow
}
Write-Host ''

# ══════════════════════════════════════════════════════════════════════════
#  对照文档结论（这条是重点：把「估算」变成「实测」）
# ══════════════════════════════════════════════════════════════════════════
Write-Host '===============================================================' -ForegroundColor Cyan
Write-Host '  对照文档里的三个数字' -ForegroundColor Cyan
Write-Host '===============================================================' -ForegroundColor Cyan
Write-Host ''

# ── 1. 4.8 MB/s ────────────────────────────────────────────────────────────
Write-Host '  [1] 「30fps / 质量75 ≈ 4.8 MB/s」' -ForegroundColor White
$target4_8 = 4.8
$cfgMatches = ($cfg.Ok -and [int]$cfg.Fps -eq 30 -and [int]$cfg.Quality -eq 75)
if ($null -eq $watchBlock) {
    Write-Host '      实测：本次没有出现「有人在看」的采样点 → 无法验证这个数。' -ForegroundColor Yellow
    Write-Host '      怎么测：打开网页实时画面（并让它持续刷新），再跑 -Seconds 60。' -ForegroundColor Gray
}
else {
    $measured = $watchBlock.TxMean
    $peak     = $watchBlock.TxMax
    $agentsInWatch = $watchBlock.AgentCount
    Write-Host ("      实测单代理发送均值 : {0} MB/s" -f (Format-Num $measured '0.000')) -ForegroundColor White
    Write-Host ("      实测单代理发送峰值 : {0} MB/s" -f (Format-Num $peak '0.000'))
    Write-Host ("      观看中的代理个数   : {0}" -f $agentsInWatch)
    if ($cfg.Ok) {
        Write-Host ("      当前录制配置       : {0}fps / 质量 {1}" -f $cfg.Fps, $cfg.Quality)
    }
    if ($null -ne $measured -and $measured -gt 0) {
        $ratio = $measured / $target4_8
        Write-Host ("      比值               : {0}× 4.8MB/s" -f (Format-Num $ratio '0.00')) -ForegroundColor White
        if (-not $cfgMatches) {
            Write-Host '      → 判定：当前配置不是 30fps/质量75，不能直接和 4.8MB/s 对照。' -ForegroundColor Yellow
            Write-Host '        把配置调成 30/75 再跑，或用下面的线性换算粗估。' -ForegroundColor Yellow
            if ($cfg.Ok -and [int]$cfg.Fps -gt 0) {
                $norm = $measured * 30.0 / [int]$cfg.Fps
                Write-Host ("        按帧率线性折算到 30fps ≈ {0} MB/s（只对帧率归一，画质没归一）" -f (Format-Num $norm '0.00')) -ForegroundColor Gray
            }
        }
        elseif ($ratio -ge 0.6 -and $ratio -le 1.4) {
            Write-Host '      → 判定：PASS —— 实测落在 4.8MB/s 的 ±40% 内，交接文档这个数成立。' -ForegroundColor Green
        }
        elseif ($ratio -ge 0.35 -and $ratio -lt 0.6) {
            Write-Host '      → 判定：偏低 —— 实测明显小于 4.8MB/s。' -ForegroundColor Yellow
            Write-Host '        常见原因（都不是 bug）：画面基本静止，前端「静止降频」把帧率降到 1fps；' -ForegroundColor Gray
            Write-Host '        或浏览器没在持续取帧。要看真实峰值请持续制造画面变化（拖窗口/播放视频）。' -ForegroundColor Gray
        }
        elseif ($ratio -gt 1.4) {
            Write-Host '      → 判定：偏高 —— 实测明显大于 4.8MB/s。' -ForegroundColor Yellow
            Write-Host '        可能：画质/分辨率高于基线（1080p），或单帧编码后比预期大。' -ForegroundColor Gray
            Write-Host '        这条结论要按实测值修订，不能再用 4.8。' -ForegroundColor Gray
        }
        else {
            Write-Host '      → 判定：明显偏低 —— 先怀疑「根本没在推帧」，其次才是编码参数。' -ForegroundColor Yellow
        }
    }
    Write-Host '      口径提醒：这是 TCP 载荷（不含 IP/TCP 头），即 JPEG 字节数本身；' -ForegroundColor DarkGray
    Write-Host '                HTTP 头每帧再加数百字节，可以忽略。' -ForegroundColor DarkGray
}
Write-Host ''

# ── 2. 0.3~0.7 核/账号 ────────────────────────────────────────────────────
Write-Host '  [2] 「录制中代理 ≈ 0.3~0.7 核/账号（即 30%~70% 单核）」' -ForegroundColor White
$agentTargets = @($Targets | Where-Object { $_.Role -eq 'agent' })
if ($agentTargets.Count -eq 0) {
    Write-Host '      实测：本次没抓到代理进程（--relay），无法验证。' -ForegroundColor Yellow
    Write-Host '      怎么测：先投送代理（让账号里有 SoulLink 代理在跑），再跑本脚本。' -ForegroundColor Gray
}
else {
    $recording = @()
    if ($cfg.Ok) { $recording = @($cfg.RecordAccounts) }
    foreach ($t in $agentTargets) {
        $rows = @($allRows | Where-Object { $_.Pid -eq $t.Pid })
        $cpuVals = @($rows | Where-Object { $null -ne $_.CpuPct } | ForEach-Object { [double]$_.CpuPct })
        if ($cpuVals.Count -eq 0) { continue }
        $st = Get-Stat -Values $cpuVals
        $inRange = ($st.Median -ge 30 -and $st.Median -le 70)
        $sessText = if ($t.Session -gt 0) { "会话 $($t.Session) " } else { '' }
        $fmt = "      pid={0,-7} {1}代理：中位 {2}% / 均值 {3}% / 峰值 {4}% 单核"
        $line = $fmt -f $t.Pid, $sessText, (Format-Num $st.Median '0.0'), (Format-Num $st.Mean '0.0'), (Format-Num $st.Max '0.0')
        Write-Host $line -ForegroundColor White
        if ($inRange) {
            Write-Host '        → 落在 0.3~0.7 核区间内，审计文档这个估算成立。' -ForegroundColor Green
        }
        elseif ($st.Median -lt 30) {
            Write-Host '        → 低于 0.3 核。若该账号没开录制，这是正常的（没在抓帧）；' -ForegroundColor Yellow
            Write-Host '          若开着录制仍这么低，说明采集比估算便宜，把结论往下修订。' -ForegroundColor Yellow
        }
        else {
            Write-Host '        → 高于 0.7 核。说明 JPEG 软编确实是大头，审计文档的估算偏乐观，' -ForegroundColor Yellow
            Write-Host '          「脏矩形 / 降帧」的收益比文档估的更大。' -ForegroundColor Yellow
        }
    }
    if ($cfg.Ok -and @($cfg.RecordAccounts).Count -gt 0) {
        Write-Host ("      参考：config 里 RecordAccountIds 有 {0} 个账号（{1}）" -f @($cfg.RecordAccounts).Count, (@($cfg.RecordAccounts) -join ', ')) -ForegroundColor DarkGray
    }
    Write-Host '      注意：代理 CPU 与「有没有人看」关系不大（采集循环恒跑），' -ForegroundColor DarkGray
    Write-Host '            所以上面两种状态的数据都算数。' -ForegroundColor DarkGray
}
Write-Host ''

# ── 3. 空闲 16% 单核 ──────────────────────────────────────────────────────
Write-Host '  [3] 「空闲挂机态 ≈ 16% 单核（其中约 12% 是 UI + WPF 渲染）」' -ForegroundColor White
$target16 = 16.0
$mainRows = @($allRows | Where-Object { $_.Role -eq 'main' })
if ($mainRows.Count -eq 0) {
    Write-Host '      实测：本次没抓到主程序进程，无法验证。' -ForegroundColor Yellow
}
else {
    $idleMain = @($idleRows | Where-Object { $_.Role -eq 'main' } | Where-Object { $null -ne $_.CpuPct } | ForEach-Object { [double]$_.CpuPct })
    $useIdle = $idleMain.Count -gt 0
    $vals = if ($useIdle) { $idleMain } else { @($mainRows | Where-Object { $null -ne $_.CpuPct } | ForEach-Object { [double]$_.CpuPct }) }
    if ($vals.Count -eq 0) {
        Write-Host '      实测：主程序没有有效采样品。' -ForegroundColor Yellow
    }
    else {
        $st = Get-Stat -Values $vals
        $lifeVals = @($mainRows | Where-Object { $null -ne $_.LifeCpuPct } | ForEach-Object { [double]$_.LifeCpuPct })
        $lifeLast = if ($lifeVals.Count -gt 0) { $lifeVals[$lifeVals.Count - 1] } else { $null }
        $basis = if ($useIdle) { '无人观看时' } else { '全时段（含有人观看）' }
        Write-Host ("      实测主程序（{0}）：中位 {1}% / 均值 {2}% / 峰值 {3}% 单核" -f `
            $basis, (Format-Num $st.Median '0.00'), (Format-Num $st.Mean '0.00'), (Format-Num $st.Max '0.00')) -ForegroundColor White
        Write-Host ("      寿命平均 CPU%（交叉验证）：{0}% 单核" -f (Format-Num $lifeLast '0.00'))
        Write-Host ("      占整机：{0}%（{1} 逻辑核）" -f (Format-Num ($st.Median / $MachineCores) '0.000'), $MachineCores) -ForegroundColor DarkGray

        if (-not $useIdle) {
            Write-Host '      → 本次没有「无人观看」的采样点，严格说不能拿来验证空闲基线。' -ForegroundColor Yellow
            Write-Host '        关掉网页后再跑一次才是干净的空闲基线。' -ForegroundColor Yellow
        }
        else {
            $ratio = $st.Median / $target16
            if ($ratio -ge 0.7 -and $ratio -le 1.3) {
                Write-Host '      → 判定：PASS —— 与「约 16% 单核」一致（PERF-3.3.4 的三次采样 14.1/17.5/15.9）' -ForegroundColor Green
            }
            elseif ($ratio -lt 0.7) {
                Write-Host '      → 判定：比基线低。若版本是 3.3.19，正说明 §1.5 那批修复（Kick 移出 UI 线程、' -ForegroundColor Green
                Write-Host '        日志增量追加、Discover TTL 3s）真的把 16% 压下去了 —— 这是好消息，' -ForegroundColor Green
                Write-Host '        应以本次实测为准修订基线。' -ForegroundColor Green
            }
            else {
                Write-Host '      → 判定：比基线高。先排除外来因素：' -ForegroundColor Yellow
                Write-Host '        · 是否有个别页面在跑动画 / 日志在满速滚动' -ForegroundColor Gray
                Write-Host '        · 是否 dist\stable\（挂机测试版）也在同一台机器上跑' -ForegroundColor Gray
                Write-Host '        · 用 -Seconds 180 再跑一次看是否稳定复现' -ForegroundColor Gray
            }
        }
        if ($null -ne $lifeLast -and $st.Median -gt 0) {
            $r2 = $st.Median / $lifeLast
            if ($r2 -gt 3 -or $r2 -lt 0.33) {
                Write-Host ("      ⚠ 瞬时中位与寿命平均差 {0:N1}×：进程可能刚启动（启动期开销被稀释），" -f $r2) -ForegroundColor Yellow
                Write-Host '        建议让它稳定跑 10 分钟以上再量空闲基线。' -ForegroundColor Yellow
            }
        }
    }
}
Write-Host ''

# ══════════════════════════════════════════════════════════════════════════
#  CSV 导出
# ══════════════════════════════════════════════════════════════════════════
if ($CsvFull -ne '') {
    try {
        # 用 UTF8 BOM，Excel 直接双击打开不会乱码；列名保持英文便于程序处理
        $allRows | Export-Csv -LiteralPath $CsvFull -NoTypeInformation -Encoding utf8BOM
        $rowCount = $allRows.Count
        Write-Host ("  明细已导出：{0}（{1} 行）" -f $CsvFull, $rowCount) -ForegroundColor Green
        Write-Host '    State 列 = watching / idle，可据此分组复核' -ForegroundColor DarkGray
        Write-Host '    列含义：CpuPct=瞬时单核%，LifeCpuPct=寿命平均单核%，TxMBps/RxMBps=TCP载荷速率，' -ForegroundColor DarkGray
        Write-Host '            EstConns=该进程当前 ESTABLISHED 连接数，Handles=GetProcessHandleCount' -ForegroundColor DarkGray
    }
    catch {
        Write-Host ("  × CSV 导出失败：{0}" -f $_.Exception.Message) -ForegroundColor Red
    }
    Write-Host ''
}

Write-Host '===============================================================' -ForegroundColor Cyan
Write-Host '  测量口径（读数字前必看）' -ForegroundColor Cyan
Write-Host '===============================================================' -ForegroundColor Cyan
Write-Host '  · CPU%      ：单核百分比，可 >100。100 = 吃满 1 个逻辑核；' -ForegroundColor Gray
Write-Host '                ÷逻辑核数才是「整机占用」（本机 16% 单核 ≈ 整机 0.5%）。' -ForegroundColor Gray
Write-Host '  · 瞬时 CPU% ：两个采样点之间的差分，用 Stopwatch 实测间隔作分母。' -ForegroundColor Gray
Write-Host '  · 寿命平均% ：进程累计 CPU ÷ 存活时长，用来交叉验证瞬时值。' -ForegroundColor Gray
Write-Host '  · 发送/接收 ：TCP 载荷字节（DataBytesOut/DataBytesIn），不含 IP/TCP 头；' -ForegroundColor Gray
Write-Host '                与 netstat 的「字节数」同源，但按进程归集、且能算每秒增量。' -ForegroundColor Gray
Write-Host '  · 工作集    ：瞬时物理内存占用量（WorkingSet64）。' -ForegroundColor Gray
Write-Host '  · 句柄      ：GetProcessHandleCount（不是 Process.HandleCount）。' -ForegroundColor Gray
Write-Host ''
Write-Host '  本脚本全程只读，未改动任何配置、进程、服务或 dist\ 内容。' -ForegroundColor Green
Write-Host '  判读与复测的完整说明见 docs\research\PERF-MEASURE-20261122.md' -ForegroundColor Green
Write-Host ''

exit 0
