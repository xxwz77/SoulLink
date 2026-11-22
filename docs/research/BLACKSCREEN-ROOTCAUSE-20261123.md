# 画面全黑根因判定：WGC 抓窗口却按整屏尺寸建帧池（2026-11-23）

> 日期：2026-11-23 02:15
> 作者：verifier（dsh-team `team_mws24h8h198lvov`）
> 起因：**一个事实分歧必须查清** —— 我实测 agent 抓屏全黑，stream-core 在 A1 实测是彩色。
> 两个结论不可能同时无条件成立，必有一个是条件差异。
> 本文档给出判定结果，并**更正我自己此前的一个错误结论**。

---

## 0. 结论

**两个结论都对，只是条件不同。**

- **全黑只发生在 WGC 后端**，BitBlt 后端完全正常（画面、颜色都对）。
- 根因：`WgcCapture` 用 `GetForegroundWindow()` 抓**单个窗口**，
  却用 `GetSystemMetrics()` 的**整屏物理像素**去建帧池。
  帧池远大于实际窗口时，超出部分全 0 → 整帧黑。
- 所以这个缺陷**随前台窗口大小漂移**：窗口接近全屏时正常（A1 抓原神），
  窗口明显小于整屏时全黑（我的测试环境）。

### 我此前的错误结论（已推翻）

我曾在 `STREAM-VERIFY-20261123.md` 初版写：

> 「根因是 `RelayAgent.cs:523` 的 `SetProcessDPIAware()` 把进程降级为 system-aware，
> 导致 `GetSystemMetrics` 返回缩放尺寸，抓屏尺寸错位 → 全黑。」

**这是错的。** 错在：
1. 我只测了 WGC 一个后端，就下了**进程级**结论，没做 BitBlt 对照。
2. `SetProcessDPIAware()` 确实存在于 523 行，但它**不是**本次全黑的原因。

**教训**：只有一个后端的失败，不能推断为进程级问题。**必须做后端间的对照实验。**

---

## 1. 决定性对照实验【实测】

### 1.1 方法

同一会话（session 1）、同一时刻、同一桌面内容，三种抓法并排比较：

| 组 | 抓法 | 说明 |
|---|---|---|
| A | 本进程 `Graphics.CopyFromScreen` | **绕过 agent 的全部抓屏代码**，作为"桌面确实有内容"的基准 |
| B | agent + **BitBlt** 后端（`SOULLINK_CAPTURE_MODE=0`） | 老实现 |
| C | agent + **WGC** 后端（`SOULLINK_CAPTURE_MODE=1`） | 新实现 |

每组都对输出图做**量化**分析：Y 均值（BT.601 亮度）、非黑像素占比、RGB 三通道均值。
不能只看"黑不黑"——要区分「整帧严格 0x00」和「有内容但极暗」。

### 1.2 结果

| 抓法 | 输出尺寸 | Y 均值 | 非黑占比 | R/G/B 均值 | 判定 |
|---|---|---|---|---|---|
| A `CopyFromScreen` | 1707x960 | **247.4** | **100.0 %** | 247/247/248 | 有内容 |
| B agent **BitBlt** | 2560x1440 | **243.4** | **100.0 %** | 246/242/245 | **有内容** |
| C agent **WGC** | 2560x1368 | **0.0** | **0.0 %** | 0/0/0 | **全黑** |

**判定输出**：
```
★ 本进程能抓到内容（非黑 100.0%）。
★ WGC 黑、BitBlt 正常 → 问题只在 WGC 实现（与 stream-core 的 A1 彩色结论一致）。
RESULT=BLACK-WGC-ONLY 仅 WGC 黑
```

**结论：排除了进程级假说。** 若是进程级（DPI 上下文/桌面访问/会话归属）问题，B 和 C 都会黑。

### 1.3 人眼确认

- `B-bitblt-live.jpg`（514 KB）：**完整桌面、颜色正常**，能看到 IDE 界面、中文、图标。
- `C-wgc-live.jpg`（55 KB）：**纯白/纯黑一片**，无任何内容。

两者文件体积差 9 倍，也侧面印证：一个有丰富内容，一个没有。

---

## 2. 根因【代码事实】

### 2.1 尺寸来源不一致

| 位置 | 代码 | 说明 |
|---|---|---|
| `WgcCapture.cs:207-211` | `IntPtr hwnd = GetForegroundWindow();`<br>`TryCreateFromWindowId(...)` | 抓**单个前台窗口** |
| `WgcCapture.cs:115` | `int w = GetSystemMetrics(0), h = GetSystemMetrics(1);` | 取**整屏物理像素** |
| `WgcCapture.cs:118-123` | `CreateFreeThreaded(..., new SizeInt32 { Width = w, Height = h })` | 用整屏尺寸建帧池 |
| `WgcCapture.cs:287` / `:306` | 同上（`CaptureOnce` 路径） | 同一错误重复一次 |

### 2.2 实测参数对照

| 项 | 实测值 |
|---|---|
| 前台窗口 | `DeepSeek Harness`，矩形 `-7,-7 → 1714,920` = **1721x927** |
| `GetSystemMetrics` 返回（agent 是 system-aware） | **2560x1440** |
| 实际建出的帧池 | **2560x1368** |

帧池 **2560x1368** 远大于抓取目标 **1721x927** →
超出窗口的区域没有内容，全 0 → 整帧呈现为黑。

### 2.3 代码从未比对 `item.Size`

全文件 grep：

```
$ Select-String -Path Relay/WgcCapture.cs -Pattern "item.Size|\.Size\.Width"
（零命中）
```

WGC 通过 `GraphicsCaptureItem.Size` 提供了**目标真实尺寸**，但代码完全没用它。

---

## 3. 为什么这个缺陷是"偶发"的（解释两人结论相反）

| 条件 | 帧池 vs 窗口 | 结果 |
|---|---|---|
| 前台窗口 ≈ 全屏（游戏最大化） | 两者接近 | **正常**（A1 抓原神彩色） |
| 前台窗口明显小于整屏 | 帧池远大于窗口 | **全黑**（我的测试环境） |
| 前台是桌面/无窗口 | `GetForegroundWindow` 可能返回桌面窗口或不适用 | 不确定 |

**这不是"谁测错了"，是条件不同。**

> 补充：`TryCreateFromWindow()` 失败时会 `?? TryCreateFromMonitor()` 降级，
> 但"成功抓到窗口"和"抓到的是不是用户想看的"是两件事——
> 抓窗口的语义是"只看这一个窗口"，而用户期望的往往是"看整个屏幕"。

---

## 4. 修复建议（属 stream-core 的文件，我未修改）

| 优先级 | 建议 | 理由 | 风险 |
|---|---|---|---|
| **1** | 帧池尺寸改用 `item.Size.Width/Height` | WGC 自己报的目标真实尺寸，天然正确；无论抓窗口还是抓屏都对 | 低 |
| **2** | 若意图是"抓整个屏幕"，改用 `CreateForMonitor` + 显示器真实尺寸 | 现在用 `GetForegroundWindow` 意味着**用户一切换窗口就抓错目标**，这是功能性问题不只是尺寸问题 | 低 |
| **3** | 加自检：连续 N 帧全 0 记明确日志 | 现在**静默产出黑帧**，用户只看到黑屏、日志里什么都没有，排查成本极高 | 低 |

### 4.1 关于 `SetProcessDPIAware()`

它**不是**本次黑屏的原因，但仍有隐患：
- `app.manifest` 已声明 `<dpiAwareness>permonitorv2,permonitor</dpiAwareness>`。
- 运行时再调旧 API `SetProcessDPIAware()` 可能把 per-monitor-v2 **降级**为 system-aware，
  且**返回值没检查**（`try { SetProcessDPIAware(); } catch { }`）。
- 影响：`GetSystemMetrics` 在有缩放的屏幕上返回物理像素而非逻辑像素
  —— 这正是帧池尺寸变成 2560x1440 的来源之一。
- **建议**：确认它是否必要；若必须，改用 `SetProcessDpiAwarenessContext` 并**检查返回值**。

---

## 5. 复跑方法

工具：`tools/stream-verify/` 的 `blackscreen` 模式（本次新增）。

```powershell
$root = 'E:\Documents\deepseek-harness\SoulLink'
Set-Location $root

# 1) 构建
dotnet build SoulLink.csproj -c Release -f net11.0-windows10.0.26100.0 --nologo -v q
dotnet build tools\stream-verify\StreamVerify.csproj -c Release --nologo -v q

# 2) 暂存独立 agent（★ 产物在「不带 win-x64」那级；runtimeconfig/deps 必须原名）
$src = "$root\bin\Release\net11.0-windows10.0.26100.0"
$dst = "$env:TEMP\sv-blacktest"
if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
New-Item -ItemType Directory -Path $dst -Force | Out-Null
Copy-Item "$src\SoulLink.exe" (Join-Path $dst 'SoulLink.Agent.exe') -Force
Get-ChildItem $src -Filter '*.dll' | ForEach-Object { Copy-Item $_.FullName $dst -Force }
Copy-Item "$src\SoulLink.runtimeconfig.json" $dst -Force
Copy-Item "$src\SoulLink.deps.json"          $dst -Force

# 3) 三组对照实验（自带 1500MB 内存熔断）
& "$root\tools\stream-verify\bin\Release\net11.0-windows\stream-verify.exe" blackscreen `
    --dir $dst --port 19095 --token test123
```

**判读**：
- `RESULT=BLACK-WGC-ONLY` → 本文档描述的缺陷（WGC 尺寸错）
- `RESULT=BLACK-BITBLT-ONLY` → BitBlt 实现问题
- `RESULT=BLACK-AGENT-WIDE` → 真的进程级问题（那时才该怀疑 DPI/会话）
- `RESULT=OK BOTH-FINE` → 两种后端都正常（说明之前的全黑是当时桌面无内容）
- `RESULT=INCONCLUSIVE` → 当前桌面本身没内容，实验无效，换有内容的桌面重跑

> ⚠ 实验**先**测本进程 `CopyFromScreen` 作为基准。
> 如果它自己也是黑的，说明桌面当时没内容，这时**任何** agent 都抓不到东西，
> 必须换条件重测，不能拿这个结果下结论。

---

## 6. 本次实验顺带修掉的工具 bug

| Bug | 现象 | 修正 |
|---|---|---|
| `ProbeHost` 把 `SOULLINK_CAPTURE_MODE` **硬编码为 "1"** | 我此前的"设 `CAPTURE_MODE=0` 做 BitBlt 对照"**根本没生效**，实际跑的还是 WGC。这直接导致我得出"设 0 仍报 WGC"的错误观察 | 改为可传参（`captureMode`） |

**这个 bug 差点让我把结论彻底搞错** —— 如果没修它，我就无法做真正的 BitBlt 对照，
也就不可能推翻自己之前的 DPI 结论。工具的正确性必须先用对照实验验证。

---

## 7. 相关文档

- `STREAM-VERIFY-20261123.md` —— 端到端验收（§4 已同步更正）
- `MEMORY-LEAK-VERIFY-20261123.md` —— 内存泄漏验证基座
- `ZERO-JPEG-VERIFY-20261123.md` —— 零 JPEG 验收
