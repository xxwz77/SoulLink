# SoulLink 交接文档 · 2.17.32（"今日完成"没出现 + 失败一次的真因）

> 时间：2026-11-20 17:50
> 版本：**2.17.32.0**（待定版；可信归档仍是 2.17.19）
> 性质：修 2.17.29 的两个真实缺陷（都是用户实际踩到的）
> 用户要求：**界面操作由用户自己测，助手不做界面测试**

---

## 1. 用户反馈

> 「我添加了一个今日执行一次的，没出现今日完成按钮，而且报失败一次。为什么」

流程实际配置（`d68f8069`）：

```
Condition/TrigInterval (intervalSeconds=5)
  → Action/ActKeyPress (user=A2, keys=0)
  → Terminate/ActEndFlow (DayBoundaryHour=4)
状态：DailyLatchKey="2026-11-20"  RunComplete=True
      今日成功=1  今日失败=1  LastRunOk=True
```

## 2. 缺陷一：动作**失败**也会写入「今日已完成」标记

### 2.1 现象

用户在流程**第一次执行就失败**（"目标账号没有会话"）之后，
流程当天就再也不跑了 —— 因为失败那轮已经写了 `DailyLatchKey`。

### 2.2 根因

`PlcScanEngine.ScanAsync` 的收尾判定：

```csharp
bool failed = selectedActions.Any(n => ActionResults.TryGetValue(n.Id, out var r) && !r.Ok)
    || flow.RunningActions.Values.Any(v => !v.Ok && v.Message == "结果待确认");
bool complete = selectedTerminals.Count > 0 && AllActivePathsComplete(...);
if (complete && !failed) { FlowRunLifecycle.CompleteRun(flow, time, hasEnd && !todayReopened); ... }
```

两个条件同时失守：

* `AllActivePathsComplete` 对动作节点只检查
  `results.TryGetValue(id, out r) && r.Ok`，**失败的结果同样在 ActionResults 里**，
  而 `selectedTerminals` 又只包含 END 节点 → "有结果就算走完" → `complete = true`；
* `failed` 依赖 `flow.WaitingActions`，而那一轮动作执行完后已经不在
  `WaitingActions` 中 → `failed = false`。

→ 动作明明失败了，却照常写今日标记、灯变"今日完成"、当天不再重试。

### 2.3 修复

直接以**实际执行结果**为准，不再绕：

```csharp
bool anyActionFailed = flow.ActionResults.Any(kv => !kv.Value.Ok);
if (complete && !failed && !anyActionFailed) { ... 才允许封盘 ... }
```

### 2.4 验证（真实引擎）

```
=== 动作必定失败 ===
  第1轮: IsDailyComplete=False  DailyLatchKey=''  今日失败=1  灯=非橙
  第2轮: IsDailyComplete=False  DailyLatchKey=''  今日失败=2  灯=非橙
  第3轮: IsDailyComplete=False  DailyLatchKey=''  今日失败=3  灯=非橙
  第4轮: IsDailyComplete=False  DailyLatchKey=''  今日失败=4  灯=非橙

=== 动作成功 ===
  第1轮: IsDailyComplete=True  DailyLatchKey='2026-11-20'  今日成功=1  灯=橙(今日完成)
  第2轮: IsDailyComplete=True  DailyLatchKey='2026-11-20'  今日成功=1  灯=橙(今日完成)
```

失败会一直重试（不会封盘），只有真正成功才锁存 —— 这才是「今日执行一次」的语义。

## 3. 缺陷二：「今日完成」根本不是一个按钮

2.17.29 把清除入口做成了**点那个 8px 的橙点**：

```csharp
var dotBtn = new Button { Width = 22, Height = 22, Content = dot, Background = Transparent, ... };
```

用户截图里那只是一个橙色小圆点，**没有文字、没有按钮外观**，完全看不出可以点。
用户原话就是"没出现今日完成按钮" —— 他要的是一个**按钮**。

### 修复

在按钮组最前面插入显式胶囊按钮（仅 `doneToday` 时出现）：

```
[✓ 今日完成]  [▶ 自动运行]  [⚡ 运行一次]  [✎]  [🗑]
   橙色描边+文字，点它清除今日标记
```

同时移除"点橙点"的隐藏交互，避免两处入口语义重复。

## 4. 历史数据清理

用户那个流程已经带着错误写入的 `DailyLatchKey="2026-11-20"`，
新逻辑不会主动回滚历史数据。**已在用户授权范围内清理一次**：

* 备份：`config.json.bak-before-clear-<HHmmss>`
* 清空该流程的 `DailyLatchKey / RunComplete / TodayOkCount / TodayFailCount / StatDayKey`
* 重启后 35 秒实测：成功 1 次 → `DailyLatchKey="2026-11-20"` → **「今日完成」按钮出现 = True**，失败 0 次

## 5. 产物

| 项目 | 值 |
|---|---|
| 版本 | 2.17.32.0 |
| `dist/SoulLink.exe` | 67,479,949 字节 · SHA256 `E6687697C8F0278B529E7D526BA8455C9B8F319EB532A38046A8987DA04D399D` |
| 注入代理 | 未改动（2.17.21） |
| 可信归档 | 仍是 `v2.17.19-reliable` |

## 6. 待办

1. **UI 自适应**（用户多次提到）：小窗口卡片挤压、按钮点不到。仍未动。
2. 2.17.20 ~ 2.17.32 均未做可信归档。
3. 历史错误锁存不会自动修复：老流程如果曾被"失败即封盘"写过标记，
   需要用户点一次「今日完成」按钮清除，或手动清 config。
