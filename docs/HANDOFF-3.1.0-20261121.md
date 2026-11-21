# SoulLink 交接文档 · 3.1.0（背景动画流畅度修复 + 渲染性能优化）

> 时间：2026-11-21 12:20
> 版本：客户端 **3.1.0.0**
> 分支：`main` @ `ef1aeaa`（已推送 origin）
> 性质：**纯客户端渲染优化**，无接口变更、无数据格式变更
> 触发：用户反馈「动画卡卡的」

---

## 0. 一页速览

| 项 | 值 |
|---|---|
| 高档帧率 | 30fps → **60fps**（跟随显示器刷新） |
| 中档帧率 | 10fps → **30fps** |
| 低档 | 保持静止，改位图缓存 + 取消周期重绘 |
| 改动文件 | 4 个（Backdrop.cs / DreamScene.cs / Models.cs / csproj） |
| 代码量 | +292 / -55 |
| 编译 | 0 错误 / 130 警告（与基线持平） |
| ⚠ CPU 代价 | **未测准**，见 §5 |

---

## 1. 问题定位

### 1.1 用户反馈

> 「动画卡卡的」

### 1.2 三个独立原因

排查后发现这不是单一问题，而是三层叠加：

| # | 问题 | 影响 |
|---|---|---|
| ① | 高档本来就是 30fps（代码里从没到过 60） | 缓慢流动的粒子/丝带能看出顿挫 |
| ② | **帧节奏抖动** | **真正的卡顿根源，见 §2** |
| ③ | 中档 10fps | 必然一跳一跳 |

---

## 2. ★ 核心：帧节奏抖动（最容易忽略的坑）

### 2.1 原实现

```csharp
if (now - _last < AnimationInterval(tier)) return;
_time += Math.Min((now - _last).TotalSeconds, .08);
_last = now;
InvalidateVisual();
```

### 2.2 问题

`CompositionTarget.Rendering` 按**显示器刷新率**回调。以 60Hz + 30fps 目标为例，
回调间隔 16.67ms，判断阈值 33ms：

```
第1帧  16.67 < 33  → 跳过
第2帧  33.34 ≥ 33  → 推进（实际间隔 33.34ms）
第3帧  16.67 < 33  → 跳过
第4帧  33.34 ≥ 33  → 推进
```

**名义上 30fps，但每次都要「等 2 帧才动」**。这本身还能接受。

真正的问题：

- 某一帧迟到（系统卡顿 20ms）→ 节奏被打乱 → 出现「该动没动、下一帧补上」的顿挫
- **144Hz 屏上更糟**：6.94ms × 5 = 34.7ms，**永远对不齐 33ms**，抖动更明显

### 2.3 修法：维护目标时刻，而不是判断流逝时间

```csharp
private TimeSpan _nextFrame;

if (intervalMs > 0) {
    if (now < _nextFrame) return;

    // ★ 用「目标时刻」而不是 now 累加，避免长期漂移；
    //   落后超过一个完整间隔时从 now 重新起算，不做补偿性跳帧
    var next = _nextFrame + TimeSpan.FromMilliseconds(intervalMs);
    _nextFrame = (next > now)
        ? next
        : now + TimeSpan.FromMilliseconds(intervalMs);
}
```

**为什么不做补偿性跳帧**：落后时立刻补一帧会让画面猛跳一下，
宁可重新起算保持平滑。

### 2.4 配套：恢复渲染时对齐时间基准

```csharp
if (canRender) {
    _last = _clock.Elapsed;
    _nextFrame = _last;   // ★ 否则从最小化恢复后第一帧会立刻推进，画面跳一下
    ApplyTierMode();
}
```

---

## 3. 帧率调整

### 3.1 改动

```csharp
public static int BackdropFrameMs(int tier) => tier switch
{
    High   => 0,     // 0 = 每帧推进，跟随显示器刷新（60Hz 屏 = 60fps）
    Medium => 33,    // 30fps（原 10fps）
    _      => 0,     // 低档：完全静止
};
```

> ⚠ 注意 `High => 0` 和 `Low => 0` 都返回 0，语义完全不同：
> 高档是「每帧推进」，低档是「静止」。所以判断静止必须用 `!Backdrop(tier)`，
> 不能只看 `BackdropFrameMs(tier) == 0`。
> 新增了 `EffectTierLevel.EveryFrame(tier)` 辅助判断。

### 3.2 单帧推进上限

```csharp
_time += Math.Min((now - _last).TotalSeconds, .08);
```

窗口被遮挡/系统卡顿后恢复时，不能让 `_time` 一次跳很远（画面会瞬移）。
0.08s 上限是原有逻辑，保留。

---

## 4. 渲染成本优化（配套改动）

降帧率省 CPU 换来的是观感损失，所以改用**降单帧复杂度**。

### 4.1 静止档：矢量 → 位图

原实现 `dc.DrawDrawing(冻结的 DrawingGroup)` —— **冻结只省「构建」，省不掉「栅格化」**。
每次重绘仍要把整个矢量场景（3 条丝带 × 24 层描边 + 76 粒子 + 56 刻度 + 边缘阴影）
重新光栅化一遍。

新实现 `TryDrawStaticBitmap()`：

```csharp
var visual = new DrawingVisual();
using (var inner = visual.RenderOpen())
    DrawScene(inner, width, height, 0, p, light);
var bmp = new RenderTargetBitmap(pw, ph, dpiX, dpiY, PixelFormats.Pbgra32);
bmp.Render(visual);
bmp.Freeze();
```

之后重绘只是一次 `DrawImage`（GPU 纹理绘制）。

**三个保护措施**：

| 保护 | 原因 |
|---|---|
| 按屏幕 DPI 放大位图 | 96 DPI 位图在高 DPI 屏上会糊 |
| 超 4096×4096 退回矢量 | 防显存吃爆 |
| `TryDrawStatic()` 作为兜底 | `RenderTargetBitmap` 失败时不会白屏 |

### 4.2 静止档：取消周期性重绘

原来低档每 1000ms 仍 `InvalidateVisual()` 一次，目的是「刷窗口外沿那圈 8px 残留色」。

现在 `StartStaticTimer()` 直接不建定时器 —— 因为**需要重绘的时机全都有明确来源**：

```
尺寸变化   → OnRenderSizeChanged    → InvalidateVisual
主题切换   → ThemeManager.RefreshAnimationState → InvalidateVisual
窗口激活/失焦 → OnWindowActivityChanged → InvalidateVisual
可见性变化 → IsVisibleChanged       → SyncAnimation
```

> ⚠ 如果将来「窗口外沿残留色」重新出现，说明某个触发点漏了，
> **应该去补那个触发点，而不是把定时器加回来**。

### 4.3 中档：降低单帧复杂度（DreamScene.Detail）

| 元素 | 高档(Detail=0) | 中档(Detail=1) | 最小(Detail=2) |
|---|---|---|---|
| 粒子（motes） | 76 | 28 | 12 |
| 星芒（Sparkle） | 每 13 个 | 每 13 个 | 跳过 |
| 轨道刻度（ticks） | 56 | 24 | 12 |
| 流星（ShootingStar） | ✓ | ✗ | ✗ |
| 心形星尘 | ✓ | ✗ | ✗ |

---

## 5. ⚠ CPU 代价未测准（重要）

### 5.1 测量环境被污染

测量期间机器上跑着 A1/A4 的挂机任务：

```
LoadPercentage: 15%
YuanShen                       CPU 47531s
BetterGI (A1/A4)               CPU 30739s
MultiplayerHoeingAssistant     CPU  8552s
```

**同一份代码、同样的配置，测出的数字在 27%～93% 之间波动，不可复现。**

### 5.2 唯一可信的观察

低档（静止）有 5 次稳定样本：

```
37.6% (37.4, 37.1, 37.9, 38.5, 37.6)
```

但由于**两次测量的机器负载不同**，不能据此声称「从 86.8% 降到 37.6%」。

### 5.3 待办

机器空闲时重测三个档位的 CPU，用 5 次采样取中位。
如果 60fps 代价过高，用户可在设置里降到「中档」（30fps）平衡。

---

## 6. 诊断方法论（可复用）

### 6.1 本次用到的工具

| 工具 | 用途 |
|---|---|
| `dotnet-trace` | 采样，但注意默认拿到的是 evented 事件不是 CPU 采样 |
| `dotnet-stack report -p <pid>` | 抓托管调用栈 |
| `Process.Threads[].TotalProcessorTime` | **逐线程 CPU 归因**（最有效） |
| `Process.TotalProcessorTime` 差值 | 进程级 CPU |

### 6.2 ★ 最有效的一招：XAML 元素对照实验

把 `AuroraBackdrop` 临时换成空 `Canvas`，重编译对比：

```
有 AuroraBackdrop    92.8% 单核
换成空 Canvas        24.5% 单核
→ 背景元素单独吃掉 68 个单核百分点
```

**这比任何 profiler 都直接**：一次对照就锁定了责任方，不用逐层分析调用栈。

### 6.3 逐线程归因识别渲染线程

```powershell
$proc = Get-Process -Id $pid
# 两次快照算增量
$proc.Threads | ...TotalProcessorTime
```

本次结果：

```
TID 180308  4266ms / 8s   State=Running   ← 75%，WPF 渲染线程
TID 160504  1156ms        State=Wait      ← 主线程（Dispatcher.GetMessage）
```

**主线程在消息循环等待，托管代码几乎不干活** —— 说明瓶颈在非托管的 WPF 光栅化层，
不要再去翻 C# 代码找热点了。

### 6.4 踩过的测量坑

| 坑 | 现象 | 教训 |
|---|---|---|
| 函数名撞 PowerShell 内置 `Measure` | 参数报错 | 自定义函数加前缀（`Test-TierCpu`） |
| 改名后配置目录不匹配 | 进程启动后 40s 超时 | `AppIdentity` 按 **exe 文件名**取身份，改名后配置目录跟着变 |
| 冷启动未预热 | 数字虚高 | 预热 30s、采样 20s 起 |
| 单次采样 | 27%～93% 波动 | **必须多次取中位** |

---

## 7. 验证记录

### 7.1 编译

```
客户端 3.1.0：0 错误 / 130 警告（与 3.0.0 基线持平）
```

### 7.2 功能验证（三个档位）

```
high 60fps       alive=True  window=True  errorlog=False
medium 30fps     alive=True  window=True  errorlog=False
low static       alive=True  window=True  errorlog=False
```

---

## 8. 改动文件

| 文件 | 行数 | 内容 |
|---|---|---|
| `Controls/Backdrop.cs` | +136 | 帧节奏修复、位图路径接入、静止档取消定时器 |
| `Controls/DreamScene.cs` | +166 | `TryDrawStaticBitmap`、`Detail` 分级、矢量兜底 |
| `Models/Models.cs` | +43 | 档位定义与描述修正、`EveryFrame` |
| `SoulLink.csproj` | +1/-1 | 版本号 3.1.0 |

---

## 9. 交接注意

### 9.1 边界约定（用户明确要求）

> **未授权不得触碰挂机测试版（`dist/stable/SoulLink-Stable.exe`）**
> **未授权不得使用 A1 / A4 账号**

这是硬约束。任何测试、清理、替换操作都要先确认。

### 9.2 如果下次还要调帧率

改 `EffectTierLevel.BackdropFrameMs()` 一处即可，`AuroraBackdrop.OnFrame` 会自动适配
（`intervalMs > 0` 走限帧，`== 0` 走每帧推进）。

### 9.3 如果画面出现「窗口外沿圈」

见 §4.2，去补触发点，别加回定时器。

---

## 10. 教训

### 10.1 「帧率低」和「帧节奏乱」是两回事

用户说「卡」，第一反应是提帧率。但本次真正的根源是**帧节奏抖动**：
名义 30fps，实际是「等 2 帧动 1 帧」的不规则推进，在 144Hz 屏上完全对不齐。
**优化流畅度先查节奏，再查帧率。**

### 10.2 冻结 DrawingGroup 省不掉栅格化

`Freeze()` 只让对象不可变、可跨线程复用，**不缓存光栅化结果**。
要省栅格化必须转成 `RenderTargetBitmap`（位图）或 `DrawingImage`。

### 10.3 对照实验比 profiler 快

把可疑元素换成空实现，一次编译就能证明它是不是元凶。
比逐层分析调用栈可靠得多。

### 10.4 测量环境不干净，数字就是废的

本次三次测量同一份代码得出 27% / 78% / 93%，全部不可信。
**采信数据前先看机器负载**，否则会基于错误结论做出错误优化。

### 10.5 省 CPU 不要靠砍帧率

砍帧率的代价直接体现在观感上（用户会立刻投诉卡）。
正确做法是**降单帧复杂度**（Detail 分级）或**换渲染路径**（矢量→位图）。