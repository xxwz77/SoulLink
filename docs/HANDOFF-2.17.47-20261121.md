# SoulLink 交接文档 · 2.17.47（UI 渲染光栅化定位 / 卡片阴影按档位关闭）

> 时间：2026-11-21 02:20
> 版本：**2.17.47.0**（已构建到 `dist`，**未**替换挂机版）
> 性质：**性能定位 + 参数调整**，不是修 bug；视觉变化极小（见第 4 节）
> git：`e8dccc1`（含 2.17.46 `5342172`）

---

## 0. 一句话现状

挂机版 2.17.45 把**数据层**从 87.5% 压到 12–13%，但用户实测**恢复窗口后仍有 45–50% 单核**、
最小化只有 11.7% —— 说明剩下的大头在 WPF 渲染。

本轮把这块定位清楚了：

- 真正的热点是 **`CHwRasterizer::RasterizeEdges`**（WPF 在 CPU 上逐条边做扫描线填充），
  主线程采样中 `wpfgfx_cor3` 占 **54–67%**，而我们自己的代码只占 **1.78%**；
- 其中**卡片阴影 `DropShadowEffect` 单独占约 14 个单核百分点**（已按档位关闭）；
- 但**仍有约 24 点未定位**，见第 7 节。

---

## 1. 用户提出的问题（原文）

> 「UI渲染线程CPU占用高能改善吗？」
> 「不会用老机器，最老的是英特10代」
> 「深色主题下直接关阴影」「选项 B：换成预渲染九宫格」→ 后改为「那就 C 吧」（跟随档位）

---

## 2. 本轮的关键突破：拿到 WPF 原生符号

前两轮用 `dotnet-trace` 都只看到「6% 托管、其余都是 Wait」，**完全找不到热点**。原因是
`dotnet-trace` 默认 profile 只采托管栈，而问题全在原生图形层。

**正确做法（下次直接照抄）：**

```powershell
# 1) 用 Windows 自带的 WPR 抓原生 CPU 采样
wpr -start CPU -filemode
Start-Sleep -Seconds 10
wpr -stop .\p.etl          # 约 1.3 GB / 10 秒

# 2) 从模块里读出 PDB 的 GUID+Age（搜 RSDS 签名，偏移 +4 是 GUID，+20 是 Age）
#    wpfgfx_cor3.dll → PDB GUID / Age / 原始路径

# 3) 从 Microsoft 符号服务器直接下（不需要 symchk）
https://msdl.microsoft.com/download/symbols/wpfgfx_cor3.pdb/<GUID大写><Age>/wpfgfx_cor3.pdb

# 4) 放到本地缓存目录，用 TraceEvent 的 SymbolReader 解析
C:\symbols\wpfgfx_cor3.pdb\<GUID><Age>\wpfgfx_cor3.pdb
```

`TraceEvent` 的符号命名空间是 **`Microsoft.Diagnostics.Symbols`**（不是 `Microsoft.Diagnostics.Tracing.Symbols`），
解析入口是 `log.CodeAddresses.LookupSymbolsForModule(symReader, traceModuleFile)`，
且 **必须传 `TraceModuleFile`，不能传 `TraceLoadedModule`**。

**解析结果（主进程 51900，5827 个采样点）：**

| 模块 | 占比 |
|---|---|
| `wpfgfx_cor3` | **54.64%** |
| `ntoskrnl` | 19.72% |
| `nvd3dumx` | 6.73% |
| `ntdll` | 5.71% |
| `soullink`（我们自己的代码） | **1.78%** |

**符号化后的自身耗时 Top：**

```
20.13%  CHwRasterizer::RasterizeEdges          ← 边缘扫描转换
 2.87%  CHwTVertexBuffer<CD3DVertexXYZDUV2>::Builder::AddComplexScan
 2.03%  InitializeEdges
 1.96%  CHwTVertexBuffer<...>::ExpandVerticesFast
 1.22%  CSnappingFrame::SnapCoordinate
 0.89%  memcpy_repmovs
 0.84%  CHwRasterizer::ComputeTrapezoidsEndScan
 0.77%  QuickSortEdges
=== INCLUSIVE ===
78.53%  CPartitionThread::Run                 ← 主线程就是在跑分区渲染
```

**结论：WPF 在用 CPU 做矢量光栅化，不是 GPU 问题，也与我们自己的 C# 代码基本无关。**

---

## 3. 二分定位（走过的弯路，务必看）

用一个环境变量控制的隐藏开关做二分。**这里踩了三个坑，下次不要再踩：**

### 坑 1：诊断代码的调用时机错了
第一版把 `ApplyRenderDiagHides()` 放在构造函数开头，但 `_pages["launch"] = PageLaunch`
在后面才赋值 → `content` 分支拿到的是**空字典**，测出来「隐藏内容没影响」。
**教训：任何依赖控件引用的诊断代码，必须放在所有 `InitializeComponent` / 字段赋值之后。**

### 坑 2：`SideNavHost` 同时包含导航列和内容列
```xml
<Grid x:Name="SideNavHost">        <!-- 第 102 行 -->
    <ColumnDefinition Width="76" />   <!-- 导航 -->
    <ColumnDefinition Width="*" />    <!-- 所有 Page* 都在这里 -->
```
隐藏 `SideNavHost` 等于**把内容和导航一起隐藏**，所以「隐藏 nav 降 40 点」是假象。
**教训：二分前先看清控件的实际子树范围。**

### 坑 3：改资源字典对 `DynamicResource` 无效
`GlassCard` 的 `Effect` 是 `{DynamicResource CardShadow}`，改资源字典会被主题刷新覆盖。
**必须直接对已应用的 `UIElement.Effect` 置空，且在 `Loaded` 之后。**

### 正确的测量方法
每次测量：启动 → 等 18 秒（进入稳态）→ 采样 14 秒 → 计算 `TotalProcessorTime` 增量。
**必须交替 A/B 测**（有/无 交替），否则漂移会把差异淹没。

---

## 4. 最终结论与改动

### 4.1 卡片阴影（主改动）

`Services/ThemeManager.cs`：

```csharp
// 低档（EffectTier=2）→ 一律关闭
// 中/高档 → 亮色保留（BlurRadius 34→20）；深色始终关闭
int shadowTier = App.Config?.Settings?.EffectTier ?? 1;
bool shadowOn = shadowTier <= EffectTierLevel.Medium && !IsDark;
```

**实测数据（交替 A/B，2560×1368，LaunchPage，12 秒稳态）：**

| 配置 | CPU |
|---|---|
| 保留 `CardShadow`（`BlurRadius=34`） | 52.5% |
| 关闭阴影 | **38.2%** |
| **差值** | **约 14 个单核百分点** |

### 4.2 视觉验证（这是本轮最有价值的发现）

用逐像素 diff（放大 6 倍）对比「有阴影 / 无阴影」两张截图：

**深色主题下，阴影完全不可见。**

- `CardShadow` 深色参数：颜色 `#02040A`、不透明度 55%、`BlurRadius=34`
- 窗口背景 `#090919`
- **近黑叠近黑，对比度低到像素级都看不出卡片轮廓**

→ **这 14 个点一直在白算**。深色主题下关闭它，零视觉损失。

**亮色主题下可见但轻微**：颜色 `#6650508C`、不透明度 20%、奶白背景 `#FBF7FE`，
能看到一圈淡紫晕。保留，但把 `BlurRadius` 从 34 降到 20（模糊面积是半径² 量级）。

### 4.3 顺手修掉的一个矛盾

`Views/MainWindow.xaml`：`AllowsTransparency="True"` → `"False"`。

`MainWindow.xaml.cs:48-51` 的注释早就写着：

> **必须是非分层窗口**。实测：`AllowsTransparency=True` 的分层窗口里，RDP ActiveX
> 控件能连上、会话也是 Active，但**画面永远全黑**（WPF 空域限制在分层窗口下的表现）。

也就是说 **XAML 和代码意图矛盾了整整一段时间**。用户实测改后 RDP 画面正常。
（注意：这一项**对 CPU 没有影响**，别指望它省电，实测 50% → 50%。）

### 4.4 档位切换的配套修复

`Views/SettingsPage.xaml.cs` 的 `EffectTier_Changed` 原来只调 `ThemeManager.RefreshEffects()`，
**不会重设 `CardShadow` 资源** → 改了档位阴影不跟着变。补了 `ThemeManager.Apply()`。

---

## 5. 2.17.46 的渲染缓存（同批提交，效果被主因掩盖）

`Controls/DreamScene.cs` + `Controls/Backdrop.cs`：

| 场景 | 旧 | 新 | 提速 |
|---|---|---|---|
| 低档静止（每秒重绘 1 次） | 1.083 ms/次 | **0.001 ms/次** | 1905x |
| 高档动效（每帧 33ms） | 1.004 ms/帧 | **0.293 ms/帧** | 3x |

做法：静止档整帧烘焙成冻结 `DrawingGroup`；动效档把丝带 24 层描边、轨道画刷、
`EdgeShade`、`Grain` 全部缓存。中档恢复 10fps 动画（新增 `BackdropFrameMs`）。

> ⚠ 这 1 ms 的量级**被阴影的 30–40 ms 完全掩盖**，所以当时「看不出效果」是正常的。
> 它不是白做，但也不是主要收益。

---

## 6. 产物

| 项目 | 值 |
|---|---|
| 版本 | **2.17.47.0** |
| `dist/SoulLink.exe` | 67,499,010 字节 |
| SHA256 | `67406AA9A21B6103EFFD3409D5B084FD679252A719522693DA11B0979B8BEDDE` |
| git | `e8dccc1`（`5342172` = 2.17.46） |
| 远端 | `github.com/xxwz77/SoulLink` |

**构建命令（必须照抄）：**

```bat
dotnet publish -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:DebugType=none -o dist --nologo
```

---

## 7. 未解决：仍有约 24 个单核百分点

### 已知
- 隐藏整个 `SessionPanel`（LaunchPage 右侧「在线会话」卡片）→ 38% 降到 **12%**
- 但它的所有子元素单独隐藏**都无效**：
  - `SessionList`（列表控件本身）→ 无变化
  - `SessionList.ItemsSource = null`（清空数据）→ 无变化
  - 日志面板 `LogScroll` → 无变化
  - `GlowDot.Pulse` 禁用 → 无变化
  - 深色关闭 `DropShadowEffect` → 只降 14 点（已处理）

### 推测（未验证）
剩下的 24 点可能是：
1. `SessionPanel` 的 `GlassCard` 样式还有其他 Paint 开销（渐变画刷 `GlassFill` / `GlassStroke`）
2. 卡片内的 `ListBox` 虚拟化行为
3. `Grid` 三行布局（`Auto / * / Auto`）触发的重复测量

### 下一步建议
用同样的 WPR + 符号方法，但这次**在隐藏 SessionPanel 前后各抓一次**，
对比 `RasterizeEdges` 的采样数变化，能直接看出差在哪个绘制类别。

---

## 8. 教训

### 8.1 `dotnet-trace` 看不到 WPF 渲染问题
默认 profile 只采托管栈。WPF 渲染在原生层，必须用 **WPR + Microsoft 符号**。
前两轮我据此得出「只有 6% CPU 在用」的错误结论，浪费了时间。

### 8.2 不要在测量环境被污染时下结论
用户机器上**原神长期占 198% 单核 + 5.7 GB 内存**。
- 「阴影省 14 点」是**交替 A/B** 测出来的相对值，可信；
- 「52% → 38%」的**绝对值不可信**，需要在干净环境下重测。
> **规则：测 CPU 前先 `Get-Process | Sort-Object CPU -Descending | Select -First 8` 看清底噪。**

### 8.3 不要先改代码再找证据
我在定位过程中做了三次错误归因（背景重建 → 分层窗口 → 导航栏），
每次都是「看起来像」就动手。正确的是：**先拿采样证据，再改。**

### 8.4 代码注释与配置矛盾时要信注释
`AllowsTransparency` 那条：注释明确写了「必须非分层，否则 RDP 全黑」，
但 XAML 一直开着 —— 这种矛盾往往意味着**有人改了配置忘了改注释，或反之**，
应该当成 bug 处理。

### 8.5 深色主题的"隐形开销"值得专项排查
这次发现 `#02040A/55%` 的阴影在 `#090919` 上完全不可见，却一直占 14 个点。
**其他深色下的装饰性 Effect（`SoftGlow`、`GlowCyan`、`GlowViolet`）也值得同样验证一遍。**

---

## 9. 待办

1. **继续压剩下 24 点**（第 7 节）
2. **游戏关闭后重测绝对值**，确认 2.17.47 的真实收益
3. 验证深色主题下其他 `Effect`（`SoftGlow` / `GlowCyan` / `GlowViolet`）是否也是隐形开销
4. `dist/SoulLink.exe` 已更新到 2.17.47，但**挂机版仍是 2.17.45**，是否替换由用户决定
