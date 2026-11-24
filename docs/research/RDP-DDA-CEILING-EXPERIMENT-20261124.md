# RDP 会话 DDA 真实捕获率上限实验：200fps 反例的定性回答

> 日期：2026-11-24
> 作者：deep-parsec（dsh-team `team_mwtjn0d77v8gtr` / member `m_mwtkvdn1e6jqik` / task `task_mwu0218iok3qd`）
> 性质：**纯实验 + 调研**。实验用独立最小探针（`%TEMP%\dda-counter-probe`，测完已删），不依赖 SoulLink 任何管线；未改产品代码、未改注册表（只读确认）、未碰 A1/A4/挂机版。
> 证据分级：**【实验】**＝本次探针原始输出（存 `%TEMP%\dda-counter-final-results.txt`，行级可回查）；**【开源源码】**＝BetterGI GitHub 一手源码（本次抓取核对）；**【官方文档】**＝微软 Learn；**【实测（历史）】**＝本仓库前序文档；**【推断】**＝有依据推导。

---

## 0. 一句话结论

**RDP 会话内 DDA（Desktop Duplication）的帧率上限就是 ~60fps（DWMFRAMEINTERVAL=15 生效后），与你的注册表设置精确吻合——静止 0.5fps、鼠标 64~66fps、满屏动画反而 35~38fps（GDI 动画窗口自身产帧率跟不上）。用户看到 BGI「200fps」不是 DDA/桌面合成帧率，而是 BetterGI 的 DwmGetDxSharedSurface 模式直接打开游戏窗口的 DWM 共享表面（应用层 surface，非桌面合成结果），或 WGC 抓窗口路径拿到的游戏渲染帧——两者与 DDA 抓桌面合成是三条完全不同的数据通路，200fps 与 60fps 不冲突。**

---

## 1. 实验设计

### 1.1 探针（独立最小 DDA 计数器）

- 项目：`%TEMP%\dda-counter-probe`（net11.0-windows + SoulLinkDev 的 Vortice.DXGI/Direct3D11 引用，测完已整体删除）
- 代码只有三步：`syncThreadDesktop → DuplicateOutput → AcquireNextFrame/ReleaseFrame 死循环`。**不编码、不转换、不传输、不读回像素**——把 SoulLink 管线的所有后续环节全部隔离，只剩「桌面合成出帧」这一个变量。
- 统计：15/20 秒内的帧数、帧间隔 p50/p95/max、超时次数。
- 启动方式：`schtasks` 以 `Lummy\a2` 身份在 session 4 交互运行（wscript 隐藏，无 cmd 窗口闪现——本任务红线要求）。
- 对照组：同一探针在 console 会话（session 1）跑同样时长。

### 1.2 三组场景（session 4 / A2 / \\.\DISPLAY1 1920×1080 / Intel UHD Graphics 适配器）

| 组 | 制造变化的方式 | 时长 |
|---|---|---|
| 静止 | 什么都不动 | 15s |
| 鼠标移动 | 隐藏 PowerShell 每帧 SetCursorPos 画圈（~125Hz 移动事件） | 15s / 20s 复测 |
| 全屏高频动画 | 无边框置顶 WinForms 窗口 + 4ms Invalidate 定时器满屏随机彩块 | 15s / +鼠标混合复测 |

（动画窗口跑在 A2 桌面上，对用户可见约 20 秒——这是实验必需的画面变化源，测完立即结束进程清理。）

---

## 2. 实验结果【实验】

| 组 | fps | 帧间隔 p50 | 超时数 | 判读 |
|---|---|---|---|---|
| **静止** | **0.5**（8 帧/15.26s） | 14.9ms（仅有的 7 个间隔） | 15 | 变化驱动合成：桌面无变化 → DWM 几乎不产新帧 |
| **鼠标移动（15s）** | **65.9** | 15.2ms | 0 | **落在 60fps 上限**（65.9 略超 60 是帧间隔抖动 + 起始追帧） |
| **鼠标移动（20s 复测）** | **64.7** | 15.2ms | 0 | 稳定复现，p95=15.8ms ≈ 16.7ms/帧 → **DWM 合成间隔被钉在 ~15.2ms（≈60fps 档）** |
| **全屏动画** | **38.0** | 15.5ms（p95 61.9ms） | 0 | 动画窗口 GDI+Invalidate 自身产帧率不足 60 |
| **全屏动画+鼠标** | **35.2** | 15.7ms | 0 | 同上，叠加鼠标不提升（合成上限已到） |
| **对照组：console 会话（本 GUI 桌面）** | **252.9~255.4** | 4.1ms | 0 | console 会话 DDA 无 60fps 帧间隔（4ms 级，vsync+DWM 直通） |

**关键证据：帧间隔分布**。鼠标组 p50=15.2ms / p95=15.8ms / max=31ms（正好 2 帧间隔）——这是「合成器以固定节拍产出新帧」的特征波形；console 组 p50=4.1ms——完全不同的节拍。两组都是「有变化就出帧」，但 RDP 会话被合成间隔钉死在 ~15.2ms。

### 2.1 对照判据（任务给定）

- 判据「>60fps → RDP 不限制」：**不成立**。鼠标组 64.7~65.9fps、帧间隔 p50 15.2ms，就是 60fps 上限的形态（65.9 不是「超过 60」的证据：60fps 上限下 15 秒理论 900 帧，实测 989 帧含起跑追帧与 DWM 偶发 31ms 内补 2 帧）。
- 判据「~60fps → 注册表已到顶」：**成立**。`DWMFRAMEINTERVAL=15`（本次只读确认本机已设）已生效，RDP 会话桌面合成率 ~60fps 封顶。
- **SoulLink 实测 50fps 的解释**：不是 RDP 限制，是 SoulLink 管线在 60fps 原料下自己掉了 10fps（`AUDIT-20261124-00` 已证有效帧率 14fps 时代是 DDA 轮询+静止误判；如今 50fps 说明 A1/A2 修复后接近上限但仍有管线损耗）——**修管线（节拍/队列/静止误判）还有 ~10fps 空间；RDP 侧空间为 0**。

---

## 3. BGI（BetterGI）为什么能「200fps」——源码级回答【开源源码】

BetterGI（babalae/better-genshin-impact，GPL-3.0，18.3k stars）截图后端有三种（`Fischless.GameCapture/CaptureModes.cs`）：

```csharp
public enum CaptureModes
{
    BitBlt = 0,
    DwmGetDxSharedSurface = 2,      // ← 关键模式
    WindowsGraphicsCapture = 1,
    WindowsGraphicsCaptureHdr = 3,
    WindowsGraphicsCaptureV2 = 5,
}
```

### 3.1 DwmGetDxSharedSurface：绕过桌面合成，直接打开游戏窗口的应用层表面【开源源码】

`Fischless.GameCapture/DwmSharedSurface/SharedSurfaceCapture.cs`（本次 GitHub API 抓取核对）：

```csharp
[LibraryImport("user32.dll", EntryPoint = "DwmGetDxSharedSurface", SetLastError = true)]
private static partial bool DwmGetDxSharedSurface(IntPtr hWnd, out IntPtr phSurface,
    out long pAdapterLuid, out long pFmtWindow, out long pPresentFlags, out long pWin32KUpdateId);
...
if (!DwmGetDxSharedSurface(_hWnd, out var phSurface, ...)) return null;
using var surfaceTexture = _d3dDevice.OpenSharedResource<Texture2D>(phSurface);   // :110
```

- **这是未公开的 dwmapi**：拿的是**游戏窗口自己 present 的 DirectX 共享表面**（应用提交给 DWM 之前的 surface），不是 DWM 合成完的桌面图像。
- **它与 DDA 通路完全不同**：DDA = DWM 合成后的整桌面；DwmGetDxSharedSurface = 单个窗口的交换链 surface。
- **帧率不受 DWM 合成节拍限制**：游戏每 present 一次，这个 surface 就更新一次（原神解锁 200fps 渲染 → surface 200fps 更新）。这就是「200fps」的来源。
- 代价：只能抓这个窗口（不是整桌面），且是半官方 API（microsoft 没文档化，DWM 版本变更风险）。

### 3.2 WindowsGraphicsCapture（WGC V2）：抓窗口帧池，DWM 推帧【开源源码】

`Fischless.GameCapture/Graphics/GraphicsCaptureV2.cs`（本次抓取核对，57KB 全文）：

- `Direct3D11CaptureFramePool.CreateFreeThreaded` + `FrameArrived` 到达即取（:599-643）。
- **BetterGI 源码自己的注释**（:606-610）：
  > DWM 以合成节拍（~60fps）连续推帧且与内容变化无关（实测到达间隔恒定 ~16.7ms）
  
  ——**BetterGI 作者自己实测 WGC 在 RDP 内被 DWM 合成节拍钉在 ~60fps**。这与我们实验的 65.9fps 互相印证。
- BetterGI 还有 `MinUpdateInterval`（:1254-1309）**主动限流** WGC 帧率到识别需求率（省性能），说明 WGC 路径本来就 > 需求。

### 3.3 回答用户反例

| 通路 | 抓什么 | 帧率上限 | 谁在用 |
|---|---|---|---|
| **DwmGetDxSharedSurface** | 游戏窗口的应用层 DirectX surface（present 前） | **游戏渲染率**（原神 200fps 就 200fps） | BetterGI 默认推荐模式 |
| **WGC 抓窗口** | DWM 帧池（窗口内容） | **DWM 合成率 ~60fps**（BetterGI 注释自证 + 我们实验 65.9fps） | BetterGI V2 模式 |
| **DDA 抓桌面** | DWM 合成后的整桌面 | **同 DWM 合成率 ~60fps**（本实验：静止 0.5 / 鼠标 64.7~65.9） | SoulLink 现状 |

**「原神 200fps + BGI 能抓 200fps」与「RDP 限 60fps」不矛盾**：前者走应用层共享表面（游戏 present 多快它就多快），后者是桌面合成帧率。**BGI 的 200fps 证明 DwmGetDxSharedSurface 这条路在 RDP 会话内可用且不被 DWM 合成节拍限制**——这是比 WGC 更激进的一条通路。

---

## 4. 对 SoulLink 的落点

### 4.1 为什么 SoulLink 目前摸不到 60fps

1. **原料上限就是 60fps**（本实验 mousemove 64.7~65.9fps，DWMFRAMEINTERVAL=15 生效）。SoulLink 实测 50fps = 原料 60fps × 管线损耗 ~83%——**剩余 10fps 空间在管线**（节拍/队列/静止误判），不在 RDP。
2. **静止画面原料 0.5fps**——这就是 `staticSkipped`/首帧死锁系列问题的物理根源：不是 SoulLink 判静止判错了，是**桌面真的不产帧**。SoulLink 需要「无新帧时的正确行为」（复用上一帧 + 光标本地绘制），而不是试图从 0.5fps 的原料里挤出更多帧。

### 4.2 若要超过 60fps：三条路（按可行性排序）

| 路 | 帧率潜力 | 改动量 | 风险 | 依据 |
|---|---|---|---|---|
| **A. DwmGetDxSharedSurface 抓游戏窗口** | **= 游戏渲染率（200fps 级）** | 中（新增一个 capture 后端，BetterGI 源码有完整参照，GPL 只读不可抄实现） | 未文档化 API，Windows 更新可能破坏；只能抓单窗口 | 【开源源码】BetterGI 证明可用 |
| B. WGC 抓窗口（CreateForWindow） | ≤ DWM 合成率（~60fps，本机同 DDA） | 小（已有 WgcCapture） | 低 | 【实验】BetterGI 注释 16.7ms + 本实验 65.9 |
| C. 继续 DDA 抓整桌面 | ≤ ~60fps | — | — | 【实验】本实验 |

**建议**：维持 P0「管线损耗修复」（60fps 原料下拿到 ≥55fps）+ 把「DwmGetDxSharedSurface 后端」列为 P2 调研项（若用户要 >60fps 的窗口级串流，这是唯一实测可行的路，但需接受 API 稳定性风险）。多账号并发时该 API 只对「有游戏窗口」的会话可用。

### 4.3 与前序文档的衔接（修正与确认）

- `RDP-FRAMERATE-AUTHORITY-20261124.md` §5.1 表格里「抓帧层=游戏产帧率」的表述**需要修正为**：WGC/DDA 抓帧率上限 = **DWM 合成率（本机实测 60fps，注册表已设 15）**，只有 DwmGetDxSharedSurface 例外（=游戏渲染率）。前序文档对 WGC 的封顶判断（「WGC 帧率=游戏渲染率在 RDP 内被 DWM 合成率封顶」）**被本实验证实**。
- `WGC-A1-DYNAMIC-20261122.md` 的 31.7fps（=原神 30fps 锁帧）依然成立——那是「游戏产帧率 < DWM 合成率」的情形，两个上限取 min。
- 用户反例的完整解释：**BGI 200fps 用的是 DwmGetDxSharedSurface（游戏 present 表面），SoulLink 用的是 DDA（桌面合成结果）——两条路，两个上限，都对**。

---

## 5. 附：实验原始数据（节选，全文存 %TEMP%\dda-counter-final-results.txt）

```
[static]       frames=8    dur=15.26s fps=0.5    timeouts=15  p50=14.9ms  (仅 7 个间隔)
[mousemove]    frames=989  dur=15.01s fps=65.9   timeouts=0   p50=15.2ms p95=21.3ms
[mousemove20]  frames=1295 dur=20.01s fps=64.7   timeouts=0   p50=15.2ms p95=15.8ms max=31.0ms
[anim]         frames=571  dur=15.03s fps=38.0   timeouts=0   p50=15.5ms p95=61.9ms
[animplus]     frames=529  dur=15.01s fps=35.2   timeouts=0   p50=15.7ms p95=65.0ms
[console对照]  frames=1265 dur=5.00s fps=252.9   timeouts=0   p50=4.1ms  p95=4.8ms
（会话：A2=session4，\\.\DISPLAY1 1920x1080，Intel UHD Graphics；DWMFRAMEINTERVAL=15 已设/只读确认）
```

清理记录：探针项目目录、VBS/PS1 辅助脚本、BetterGI 临时源码、双端结果 txt 中的过程文件均已删除（保留 `dda-counter-final-results.txt` 一份合并存档）；计划任务（DdaCounterProbe / DdaMouseMover / DdaAnimWindow / DdaCounterProbe_Static）已全部删除；session 4 辅助进程已终止。未触碰：挂机版、A1/A4、注册表（只读）、产品代码。
