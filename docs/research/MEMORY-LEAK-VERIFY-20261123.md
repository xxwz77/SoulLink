# 内存泄漏验证基座与修复后回归清单

> 日期：2026-11-23
> 作者：verifier（dsh-team `team_mws24h8h198lvov` / task `task_mws4ht1r65gx4q`）
> 范围：**只新增工具与文档**。未改任何业务代码、未改 csproj、未动 `dist/`、未提交 git。
> 配套工具：`tools/mem-probe/`（可构建）、`tools/stream-verify/`（可构建）

---

## 0. 这份文档解决什么问题

用户今晚电脑自动关机，怀疑与 `/stream` 内存泄漏有关。要修就必须能**证明修好了**——
没有量具和判据，「修好了」只能靠感觉。

本文交付三件东西：
1. **量具**：`tools/mem-probe`，按 PID 采样 WorkingSet / Private / HandleCount → CSV
2. **判据**：什么样的曲线算泄漏、什么算正常波动、采样多久才能定性
3. **基线 + 回归清单**：修复前跑一次留底，修复后逐条对照

---

## 1. 量具：`tools/mem-probe`

### 1.1 为什么专门写一个

| 原因 | 说明 |
|---|---|
| **不能用 `Process.HandleCount`** | AGENTS.md 性能约束第 3 条：它每次 3~5ms，挂在 1Hz 循环自己就吃掉 20% CPU。采样本是为了观测泄漏，采样器自己成了负载，数据就不可信。本工具用 `GetProcessHandleCount`（P/Invoke） |
| **不能按名字找进程** | 项目里按路径通配停进程，误杀过用户挂机的 `dist\stable\SoulLink-Stable.exe`。本工具**只接受一个明确 PID**，不做任何名字/路径匹配 |
| **必须有熔断** | 修复前实测泄漏 ~46~128 MB/s，几十秒就能吃几 GB。本机已占 60~71%（原神 + BetterGI）。不管的话，**观测行为本身就会制造事故** |

### 1.2 用法

```powershell
# 基本：对指定 PID 采 60 秒，每秒 1 次
tools\mem-probe\bin\Release\net11.0-windows\mem-probe.exe --pid <PID> --seconds 60

# 完整参数
tools\mem-probe\bin\Release\net11.0-windows\mem-probe.exe `
    --pid <PID> --seconds 300 --interval 2 --max-mb 1800 `
    --csv $env:TEMP\mem-baseline.csv

# 只采不判（长时间后台采样用）
... --baseline-only

# 自检判据本身是否可信（不采真实进程，6 项合成数据）
tools\mem-probe\bin\Release\net11.0-windows\mem-probe.exe --selftest
```

| 参数 | 说明 |
|---|---|
| `--pid` | 目标 PID（唯一必填） |
| `--seconds` | 采样时长，默认 60 |
| `--interval` | 采样间隔秒，默认 1.0 |
| `--csv` | CSV 路径，默认 `%TEMP%\mem-probe-<pid>-<HHmmss>.csv` |
| `--max-mb` | **熔断阈值**：WorkingSet 超过即停止并报告。内存紧张时建议 1500~2000 |
| `--baseline-only` | 只采不判 |
| `--quiet` | 只出结论，不打印逐点表 |

CSV 列：`elapsedSec,workingSetMB,privateMB,handleCount,cpuMs`

退出码：`0` 正常 / `1` 判定为泄漏 / `2` 参数错 / `3` 目标进程消失 / `4` 触发熔断

### 1.3 判据是怎么写的（写在代码里，避免文档与实现漂移）

**核心：只看后半段线性回归 + 拟合优度 R²，不看首尾差。**

为什么不能看首尾差 / 不能看短窗口：
- **启动爬坡是正常的**：前几秒有 JIT、缓存预热、GC 策略调整。交接文档也提醒过这一点。
- **GC 锯齿是正常的**：内存涨上去被回收，来回波动。首尾差可能恰好落在两个波峰/波谷上。
- 只看一段短窗口，很可能刚好截在爬坡段或回收点上 —— **「973MB 后平了」这类结论就是这个原因**。

| 判定 | 条件 | 含义 |
|---|---|---|
| **确定泄漏** | 后半段斜率 > 5 MB/s 且 R² > 0.9 | 稳定线性上涨，必修 |
| **疑似泄漏** | 斜率 > 0.5 MB/s 且 R² > 0.9 | 速率低但趋势稳，需延长采样确认 |
| **平稳** | \|斜率\| ≤ 0.5 MB/s | 无有效增长 |
| **下降（正常）** | 斜率 < −0.5 MB/s | GC 回收/缓存释放，不是泄漏 |
| **波动（无法定性）** | 斜率存在但 R² 很低 | 锯齿，不是单调上涨，需更长采样 |

**采样多久才能定性**：
- **≥ 60 秒**：能定性「陡峭泄漏」（>5 MB/s）。本工具的 `EnoughDuration` 门槛设在这里。
- **≥ 15 分钟**：才能定性**低速泄漏**（<1 MB/s）和句柄缓慢增长。
  依据是 AGENTS.md「句柄增长要观察 >15 分钟才能定性，启动爬坡是正常的」——**内存同理**。
- 短窗口（<60s）只能作为「发现问题」的下界，**不能作为「没问题」的证明**。

### 1.4 判据自检（`--selftest` 6/6 通过）

判定工具自己出错是最难发现的失败模式：如果判据把所有曲线都判成「正常」，
修复前后跑两次都是 OK，看起来「修好了」，实际什么都没验证。

所以工具内置自检，用 6 组合成数据验证：

| # | 合成场景 | 期望 | 实测结果 |
|---|---|---|---|
| 1 | 陡峭线性上涨 120 MB/s | LEAK | ✅ 确定泄漏（R²=1.000） |
| 2 | 缓慢线性上涨 1 MB/s | LEAK | ✅ 疑似泄漏（R²=1.000） |
| 3 | 平稳 + ±1MB 抖动 | OK | ✅ 平稳（R²=0.001） |
| 4 | 锯齿波动（涨了又回落） | OK | ✅ 波动无法定性（R²=0.049） |
| 5 | 启动爬坡后平稳 | OK | ✅ 平稳（R²=0.045） |
| 6 | 持续下降 | OK | ✅ 下降正常（R²=1.000） |

复跑：`tools\mem-probe\bin\Release\net11.0-windows\mem-probe.exe --selftest`

---

## 2. 修复前基线（本次实测，留底对照）

**重要前提**：本次基线用的是 **SoulLink.dll 时间戳 01:44:07** 的构建，
晚于 `Services/MfFrameEncoder.cs` 的 01:39:50 —— 即**包含当时已加入的 `using var` 修复**。
所以下面的数字是「**修了 `using var` 之后**」的状态。

### 2.1 环境

| 项 | 值 |
|---|---|
| 会话 | session 1（console 桌面） |
| 抓屏后端 | WGC（`recordstatus` 里 `capture=WGC`） |
| 分辨率 | 约 1734×927（当次会话实际分辨率） |
| 采集 fps | 15（代理配置） |
| 客户端 | 1 个 `/stream` 长连接 |
| 系统内存 | 采样前占用 **60.7%**（30313 MB 总量，可用约 12 GB） |
| 熔断阈值 | 1800 MB（保护本机，不是软件限制） |

### 2.2 实测曲线

| t(s) | WorkingSet | Private | Handles |
|---:|---:|---:|---:|
| 0 | 247.4 | 340.7 | 2499 |
| 4 | 472.8 | 1049.4 | 9255 |
| 8 | 662.4 | 1677.0 | 15390 |
| 12 | 888.7 | 2339.9 | 21532 |
| 16 | 1074.4 | 2943.5 | 27536 |
| 20 | 1292.0 | 3570.3 | 33404 |
| 24 | 1478.4 | 4148.7 | 39000 |
| 28 | 1653.8 | 4728.6 | 44616 |
| 32 | 1823.8 | 5234.2 | 49492 |

### 2.3 判定

```
采样跨度      : 32 s（17 点，间隔 2.0 s）— 因触发 1800MB 熔断提前结束
WorkingSet    : 247 → 1824 MB（净变化 +1576 MB）
Private       : 341 → 5234 MB
Handles       : 2499 → 49492（净变化 +46993）
后半段斜率    : 46.34 MB/s（R²=0.998）
判定          : 确定泄漏
RESULT=LEAK
```

**结论：`using var` 修复没有解决泄漏。**

### 2.4 关键线索：句柄增长 1454 个/秒

这是本次基线最有价值的一条观察，必须单独强调：

| 指标 | 速率 |
|---|---|
| WorkingSet | 48.8 MB/s |
| Handles | **1454.9 个/秒** |

**为什么这条重要**：
- **COM 对象泄漏（RCW 不释放）吃的是 Private Bytes，不是句柄。** 句柄是内核对象计数
  （Event / Mutex / File / Section / Thread / Key 等），两者是不同维度。
- 句柄速率 ÷ 约 15fps ≈ **每帧约 97 个内核句柄**。
- 每帧新建近百个内核句柄且从不 Close —— 说明**除了 COM 引用计数，还有别的资源在漏**。

**所以至少有两个独立问题**：
1. 内存（Private Bytes）—— 可能是 COM 未释放，也可能包含每帧 `new byte[]` 的 LOH 压力
2. 句柄（内核对象）—— 与 COM 无关的另一条漏点

> ⚠ 修的时候**必须分别验证两个指标**。只看内存降下来了就宣布修好，会漏掉句柄那条线
> —— 句柄耗尽（默认每进程上限约 1000 万，但受系统限制）同样会让进程崩溃。

---

## 3. 复跑命令（修复后逐条照做）

### 3.1 前置：正确暂存 agent 产物（两个已踩过的坑）

```powershell
$root = 'E:\Documents\deepseek-harness\SoulLink'
Set-Location $root

# ★ 必须构建，且注意：产物在「不带 win-x64」的那一级目录！
#   bin\Release\net11.0-windows10.0.26100.0\win-x64\SoulLink.dll 是旧产物（实测 0:13 vs 新 1:44）
dotnet build SoulLink.csproj -c Release -f net11.0-windows10.0.26100.0 --nologo -v q

$src = "$root\bin\Release\net11.0-windows10.0.26100.0"     # ← 注意不是 win-x64
$dst = "$env:TEMP\sv-agent-verify"
if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
New-Item -ItemType Directory -Path $dst -Force | Out-Null

Copy-Item -Path "$src\SoulLink.exe" -Destination (Join-Path $dst 'SoulLink.Agent.exe') -Force
Get-ChildItem -Path $src -Filter '*.dll' | ForEach-Object { Copy-Item $_.FullName -Destination $dst -Force }

# ★ 这两个文件必须保持原名，不能改成 SoulLink.Agent.*
#   改名会导致 hostpolicy.dll not found（agent 直接起不来）
Copy-Item -Path "$src\SoulLink.runtimeconfig.json" -Destination $dst -Force
Copy-Item -Path "$src\SoulLink.deps.json"          -Destination $dst -Force

# 确认用的是新产物
Get-Item "$dst\SoulLink.dll" | Select-Object LastWriteTime, Length
```

### 3.2 起独立 agent 并采样（一条命令跑完，自动收尾）

```powershell
$root = 'E:\Documents\deepseek-harness\SoulLink'
Set-Location $root
$sv  = "$root\tools\stream-verify\bin\Release\net11.0-windows\stream-verify.exe"
$mp  = "$root\tools\mem-probe\bin\Release\net11.0-windows\mem-probe.exe"
$log = "$env:TEMP\sv-verify-serve.log"
Remove-Item $log, "$log.err" -Force -ErrorAction SilentlyContinue

# serve 模式同时扮演「宿主」（建 relay 管道 + 下发 record）和「客户端」
# 没有它 /stream 会一直 404「缓冲里没有帧」，会被误判成功能坏了
$serve = Start-Process -FilePath $sv -ArgumentList @(
    'serve','--dir',"$env:TEMP\sv-agent-verify",'--port','19095','--token','test123','--seconds','360'
) -PassThru -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError "$log.err"

# 从输出里取 agent PID
$agentPid = $null
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 700
    $txt = Get-Content $log -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
    if ($txt -and $txt -match 'SERVE_READY.*pid=(\d+)') { $agentPid = [int]$Matches[1]; break }
}
if (-not $agentPid) { Write-Host 'serve 未就绪'; Get-Content $log -Encoding UTF8; return }
Write-Host "agent PID = $agentPid"

# 采样：300 秒，2 秒一次，超过 1800MB 自动熔断（保护本机）
& $mp --pid $agentPid --seconds 300 --interval 2 --max-mb 1800 `
      --csv "$env:TEMP\mem-after-fix.csv"

# 精确收尾：只杀自己启动的 PID
try { if (-not $serve.HasExited) { Stop-Process -Id $serve.Id -Force } } catch { }
Start-Sleep -Milliseconds 800
Get-Process -Id $agentPid -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
```

> ⚠ **收尾纪律**：只按记录下来的 PID 结束进程。
> **绝不要** `Get-Process | Where Path -like '*dist*' | Stop-Process`
> —— 项目里这么写误杀过用户挂机的 `dist\stable\SoulLink-Stable.exe`。

### 3.3 三个播放/取证工具的完整路径

| 工具 | 完整路径 |
|---|---|
| ffprobe | `C:\Users\91244\AppData\Local\Temp\ff-dl\x\ffmpeg-master-latest-win64-gpl\bin\ffprobe.exe` |
| ffmpeg | `C:\Users\91244\AppData\Local\Temp\ff-dl\x\ffmpeg-master-latest-win64-gpl\bin\ffmpeg.exe` |
| ffplay | `C:\Users\91244\AppData\Local\Temp\ff-dl\x\ffmpeg-master-latest-win64-gpl\bin\ffplay.exe` |

**用 ffprobe 验 /stream 产出的是合规 H.264**：

```powershell
$ffprobe = 'C:\Users\91244\AppData\Local\Temp\ff-dl\x\ffmpeg-master-latest-win64-gpl\bin\ffprobe.exe'

# 先用 stream-verify 的 run 模式把码流抓到文件（它内部已经调用 ffprobe）
& "$root\tools\stream-verify\bin\Release\net11.0-windows\stream-verify.exe" run `
    --dir "$env:TEMP\sv-agent-verify" --port 19095 --token test123 --seconds 8 `
    --ffprobe $ffprobe

# 或者手动对已有码流文件验
& $ffprobe -hide_banner -v error `
    -show_entries stream=codec_name,profile,level,width,height,avg_frame_rate `
    -show_entries format=size `
    -of default $env:TEMP\stream-verify-19095\stream.h264
```

**期望输出**（修复前实测值，供对照）：
```
codec_name=h264
profile=Main
level=40
width=1734  height=927      ← 跟随会话实际分辨率，不是固定 1920x1080
avg_frame_rate=25/1         ← MFT 输出帧率
```

**用 ffplay 人眼确认画面**（可选）：
```powershell
$ffplay = 'C:\Users\91244\AppData\Local\Temp\ff-dl\x\ffmpeg-master-latest-win64-gpl\bin\ffplay.exe'
& $ffplay -fflags nobuffer -flags low_delay -framedrop `
    "$env:TEMP\stream-verify-19095\stream.h264"
```
> ⚠ ffplay 自身有约 500ms 缓冲（交接文档实测）。**它播放的数字不能代表浏览器体验**，
> 只能用于「画面是否正常、颜色是否正确」的人眼判断。

---

## 4. 修复后回归检查清单

> 逐条打勾。**任何一条不通过都不能宣布修好。**
> 「实测」列填实际数字，不要写「正常」「看起来没问题」。

### 4.1 内存与句柄（本任务核心）

| # | 检查项 | 怎么验 | 通过标准 | 实测 |
|---|---|---|---|---|
| R1 | **WorkingSet 不再线性上涨** | `mem-probe --seconds 300 --interval 2 --max-mb 1800` | 判定为「平稳」或「下降」，斜率 ≤ 0.5 MB/s | |
| R2 | **Private Bytes 不再线性上涨** | 同上，看 CSV 的 `privateMB` 列 | 300 秒净增长 < 100 MB | |
| R3 | **句柄数不再暴涨** | 同上，看 `handleCount` 列 | 300 秒净增长 < 500 个（当前基线是 +46993） | |
| R4 | **不触发熔断** | 采样能自然跑满 300 秒 | 不出现「!! 熔断」 | |
| R5 | **长时观察（低速泄漏）** | `--baseline-only --seconds 900`（15 分钟） | 15 分钟净增长 < 300 MB，且无稳定上升趋势 | |
| R6 | **空闲不涨**（对照） | 起 agent 但不发 record，采 60 秒 | 平稳（修复前已实测：空闲是平的） | |

**判定陷阱提醒**：
- 采样 <60 秒的数据**不能用来证明「没问题」**，只能用来发现问题。
- 看到「涨到一个值后平了」，要确认不是**恰好截在 GC 回收点**——用 300 秒 + R² 才算数。

### 4.2 功能未回归

| # | 检查项 | 怎么验 | 通过标准 | 实测 |
|---|---|---|---|---|
| R7 | **/api/info 正常** | `Invoke-WebRequest "http://127.0.0.1:19095/api/info?token=test123"` | HTTP 200，JSON 含 `"ok":true` | |
| R8 | **/stream 能出流** | `stream-verify run ... --ffprobe <路径>` | HTTP 200，字节数 > 1KB，Annex-B 起始码 `00 00 00 01` | |
| R9 | **ffprobe 认得出 H.264** | 见 §3.3 | `codec_name=h264` | |
| R10 | **profile/level 与网页匹配** | ffprobe 看 profile/level；对照网页 `decoder.configure` 的 codec | 网页硬编码 `avc1.4D4028` = Main 4.0 → **level 必须是 40**（曾测到 50，会解码失败） | |
| R11 | **帧率达到预期** | `mem-probe` 的 cpuMs 或 `stream-verify delay` | 推流 ≥ 10 fps（修复前实测 13.56 fps）；**注意 `Task.Delay(66)` 和 30 秒自断是否还在** | |
| R12 | **颜色正确** | ffplay 播放或页面截图人眼比对 | 不能出现绿屏/紫屏/偏色（NV12 转换错误的典型症状） | |
| R13 | **画面能解码上屏** | 浏览器打开页面 / ffplay | 有画面，不是黑屏 | |

### 4.3 错误可诊断（这条以前不通过）

| # | 检查项 | 怎么验 | 通过标准 | 实测 |
|---|---|---|---|---|
| R14 | **缺 DLL 时给明确中文 500** | 暂存目录里删掉 `Vortice.MediaFoundation.dll`，请求 `/stream` | **HTTP 500** + 中文正文「缺少视频运行库」；**不能是连接重置** | |
| R15 | **失败写进日志** | 看 `%TEMP%\soullink-relay-<pid>.log` | 有 `请求失败` 或 `视频服务` 记录 | |
| R16 | **缺少帧时给 404 而非重置** | 不调 record 直接请求 `/stream` | HTTP 404 + 「缓冲里没有帧」 | |

> 修复前实测：R14 **不通过**（仍是「远程主机强迫关闭了一个现有的连接」），R15 **不通过**（日志无记录）。
> 用 `stream-verify nolib` 一条命令复跑这项：
> ```powershell
> & "$root\tools\stream-verify\bin\Release\net11.0-windows\stream-verify.exe" nolib `
>     --dir "$env:TEMP\sv-agent-verify" --port 19096 --token test123
> ```

### 4.4 鉴权

| # | 检查项 | 怎么验 | 通过标准 | 实测 |
|---|---|---|---|---|
| R17 | **错误 token 返回 401** | `/api/info?token=wrong` | HTTP 401 | |
| R18 | **无 token 返回 401** | `/api/info`（不带参数、不带头） | HTTP 401 | |
| R19 | **正确 token 通过** | `/api/info?token=test123` 或 `X-SoulLink-Token` 头 | HTTP 200 | |
| R20 | **白名单外路径 404** | `/file/something` | HTTP 404（`WebDirect.IsAllowedPath` 只放行 4 个路径） | |

### 4.5 收尾纪律（每次都检查）

| # | 检查项 | 通过标准 |
|---|---|---|
| R21 | 没有按名字/路径通配杀进程 | 全程只用记录的 PID |
| R22 | `dist\stable\` 未被触碰 | `Get-Item dist\stable\SoulLink-Stable.exe` 时间戳未变 |
| R23 | session 1 主程序未重启 | 主程序 PID 未变 |
| R24 | A1/A4 未使用 | 未连过 |
| R25 | 临时目录已清 | `%TEMP%\sv-agent-*` 可留（下次复用），但不要留在工作区内 |

---

## 5. 当前状态与待办

| 项 | 状态 |
|---|---|
| 量具 `tools/mem-probe` | ✅ 可用，`--selftest` 6/6 |
| 判据 | ✅ 已定义并自检 |
| 修复前基线 | ✅ 已留底（`%TEMP%\mem-baseline-300s.csv`） |
| **内存泄漏是否修复** | ❌ **未修复**（用含 `using var` 的新构建实测：46.3 MB/s，R²=0.998） |
| **句柄泄漏** | ❌ **新发现，未处理**（+46993 / 32 秒，1454 个/秒） |
| 缺 DLL 诊断（R14/R15） | ❌ 修复前不通过，待复验 |
| 回归清单 | ✅ 本文 §4，共 24 项 |

**下一步**：等 stream-core 修完，用 §3.2 同一条命令复跑，与 §2.2 曲线逐点对照。

---

## 6. 相关文档

- `HANDOFF-STREAM-STATUS-20261123.md` —— `/stream` 状态与端口/鉴权坑
- `LATENCY-BENCHMARK-20261123.md` —— 延迟分段实测（采集 12.14fps / 推流 13.56fps）
- `STREAM-LATENCY-TEST-20261122.md` —— 软编/硬编端到端延迟实测
- `STREAM-PERF-AUDIT-20261122.md` —— 链路逐跳分解与「无帧间复用」根因
