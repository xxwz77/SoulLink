# 单画布保护层 + 与现有应用的整合结论

> 2026-11-17 · 回答两个问题：① 保护层改成覆盖整个画布 ② 能否直接融入原应用
> 结论均以读代码 + 实测构建为依据，未启动 SoulLink 主程序、未连 RDP。

---

## 一、保护层改成「整块画布一个」—— 同意，而且更安全

### 1.1 你的提议直接命中了一条已知红线

事故报告把这条列为高风险：

> 「每个 RDP 视口高频创建/隐藏/重定位独立 HWND 浮层」

我原来的「每格一个保护层」正是这条。改成整块画布一个之后：

| | 每格一个浮层（原方案） | 整块画布一个（你的提议） |
|---|---|---|
| 浮层 HWND 数量 | N 个，随画面数增长 | **恒为 1** |
| 焦点切换时要做什么 | 拆掉/重建/重定位浮层 | **什么都不做** |
| 格子重排时 | 每个浮层都要重新对表 | 浮层尺寸只跟视口走 |

### 1.2 唯一的语义代价（已处理）

整块画布只有一个浮层，那「点第二格」会发生什么？如果不处理，用户点了 B 格，
焦点还停在 A 格，键鼠就投到 A 账号去了 —— 这在多开挂机里是**会造成误操作的 bug**。

处理方式（已实现）：**保护层这次点击既解锁、又选中鼠标所在的那一格**。

```csharp
// ConsolePage.xaml.cs · ShieldOverlay_MouseUp
if (CurrentLayoutMode != ConsoleLayoutMode.Single)
{
    var hit = HitTestTileUnderMouse();
    if (hit != null) SetFocusFromShield(hit);   // 只切焦点，不改布局、不重新上锁
}
_shieldReleased = true;
```

这一下点击仍被保护层**完整消费**，不会透传到远端 RDP —— 与单画面「首击不透传」完全一致。

### 1.3 已经落地的改动

- 删除 `ShieldAnchor` / `ShieldAnchorIsTile` 两个按格锚定的分支。
- 保护层构造、尺寸同步、ShowOverlay 全部回到 `ViewportHost`（整块视口）。
- 新增 `HitTestTileUnderMouse()`（走 `PointToScreen`，150% DPI 下不会偏）。
- 新增 `SetFocusFromShield()`：切焦点但**不**重新上锁、**不**重排布局。
- `OwnedOverlayWindow.SetAnchor()` 保留但不再被调用（后续若要再拆格可直接用）。

---

## 二、能否直接融入原应用？—— 能融入，但有三处必须改

### 2.1 先说结论

布局、配色、卡片圆角、标签卡样式都跟你现有 UI 一致（同一套 `StaticResource`），
所以**视觉上不用重新设计**。但有三个结构性问题是我这轮改动引入或暴露出来的，必须处理。

### 2.2 问题一：RDP 原生 HWND 会盖住左侧导航（我引入的回归，最严重）

**事实链**：

1. `RdpConsole` 内部是 `WindowsFormsHost` → 独立 HWND；
2. WPF 的 `Visibility.Collapsed` **不会**隐藏它的原生子窗口；
3. `MainWindow.SetActivePage()` 切页只做 `page.Visibility = Collapsed`；
4. 原代码在 `ConsolePage.IsVisibleChanged` 里用 `ViewportHost.Children.Clear()` 摘掉控件来规避；
5. 我为了「绝不重挂 ActiveX」把这句删了 → **切到启动台/设置时，RDP 会浮在所有页面之上**。

**修复方案**：

| 方案 | 做法 | 风险 |
|---|---|---|
| **A. 只改可见性，不减树** | 切走时把所有 Tile 的 `Chrome.Visibility = Collapsed`；`WindowsFormsHost` 在父级 `Collapsed` 时会同步 `Control.Visible` | 低，需实测 |
| B. 摘树再挂回 | 回到 `Children.Remove/Add` | **中高**，正是 0x119 的触发形态，不建议 |
| C. 原生 ShowWindow 兜底 | 对 `console.NativeViewportHandle` 调 `ShowWindow(SW_HIDE)` | 低，但要跟踪句柄变化 |

我倾向 **A + C 兜底**：先靠可见性，再在 `IsVisibleChanged` 时对原生句柄显式 `SW_HIDE`。
这件事必须先做，否则这版**没法用**。

### 2.3 问题二：画面区内的 WPF 浮层会被 RDP 吃掉（airspace）

`ConsolePage.xaml` 里有两个东西**叠在画面上**：

| 元素 | 位置 | 现状 |
|---|---|---|
| `InputText`（状态提示「已接管键鼠…」） | 画面区左下角 | **会被 RDP 原生窗口盖住，永远看不见** |
| `OptPanel`（灵敏度/热键浮层） | 右上角 | 同上；它默认 `Collapsed`，点了才出现 → 出现就被盖住 |

这不是我引入的（原本就如此），但多画面会把「浮层被画面遮住」变成常态，必须处理。

**两种就地改法（都无需动 MainWindow）**：

- **A. 移出画面区**：`InputText` 挪到画面区上方的标签条那一行；`OptPanel` 改成 `ViewOps` 里的下拉。
  优点：不增加 HWND，多画面下「上方一行状态栏」本来更合理。
- **B. 换成 `OwnedOverlayWindow`**：跟保护层同机制，能正确浮在 RDP 之上。缺点是多一个 HWND。

我倾向 **A**。

### 2.4 问题三：滚动条 / 外框避让

画面区是 `Border(圆角16) → ScrollViewer → Canvas`。

- 单画面：滚动条 `Disabled`，画面铺满圆角边框内，**与改动前一致**。
- 双列/网格：滚动条出现，宽度已在算布局时预留，不会来回闪。
- 圆角边框会裁掉滚动条右端一点；想更干净可给 `ScrollViewer` 加 `Padding="0,0,2,0"`。

MainWindow 侧**不需要任何改动**：`ConsolePage` 仍是 `Grid.Row=1 / Grid.Column=1` 的整块内容区。

---

## 三、还有一处隐藏风险

`UpdateShield()` 的判据里有 `ViewPage.Visibility == Visibility.Visible`。
现在 `ViewPage` 内部多了 `ScrollViewer`，但自身可见性语义没变，所以不受影响。
但要提醒：若后续为避让 airspace 去改 `ViewPage` 的 `Visibility`，保护层会跟着消失，
那一处要同步改 `UpdateShield` 的判据。

---

## 四、建议的下一步顺序

| 步 | 内容 | 为什么这个顺序 |
|---|---|---|
| 1 | **修 2.2（切页时隐藏 RDP 原生窗口）** | 不修就完全不能用，且是回归 |
| 2 | 修 2.3（`InputText` / `OptPanel` 移出画面区） | 不加会一直看不见状态 |
| 3 | Release 发布，做 2 个真实 RDP 同屏静置测试 | 判定同进程多 ActiveX 是否安全 |
| 4 | 通过后再上 4 个 | 事故报告的红线 |

---

## 五、本轮已做 / 未做

**已做**：

- 保护层改为整块画布一个，并补上「点哪格选哪格」的语义。
- 同步更新样张程序 `tools/view-demo`，它演示的就是单画布保护层。
- Release 构建 0 错误 0 警告；布局纯函数检查全部通过。

**未做（需要你确认后再动）**：

- 2.2 的切页隐藏修复。
- 2.3 的 `InputText` / `OptPanel` 位置调整。
- 未启动 SoulLink 主程序、未跑真实 RDP、未动 `dist/` 与 `dist/stable/`。
