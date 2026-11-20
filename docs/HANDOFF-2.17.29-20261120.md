# SoulLink 交接文档 · 2.17.29（END 节点：今日完成显示橙色，点击可清除今日标记）

> 时间：2026-11-20 17:10
> 版本：**2.17.29.0**（待定版；可信归档仍是 2.17.19）
> 性质：状态灯增加第五色「今日完成」，并让该状态可点击清除
> 用户要求：**界面操作由用户自己测，助手不做界面测试**

---

## 1. 用户要求

> 「若存在 END 节点，且标记为今日已执行的任务，需要增加橙色，今日完成，点击可以清除今日标记」

## 2. 现状（改之前）

* `PlcLogic.IsDailyComplete(flow, now)` 早就存在：
  `HasEnd(flow) && flow.DailyLatchKey == DayKey(now, EndBoundaryHour(flow))`；
* 有 END 的流程跑完后当天不再扫描，扫描函数直接返回
  `"今日已完成，日界 04:00 重置"`；
* **但界面完全没体现**：灯还是按"上次成功=绿"显示，
  用户看不出"这个流程今天已经不会再跑了"。
* 编辑器里有个「今天运行这个流程」勾选框可以清标记，但主界面没有入口。

## 3. 本次改动（`Views/LaunchPage.xaml.cs`）

### 3.1 五色灯

| 颜色 | 含义 | 判定 |
|---|---|---|
| 🟡 黄 | 暂停 | `Paused \|\| !Enabled` |
| 🟠 **橙** | **今日完成** | `IsDailyComplete`（**优先级仅次于暂停**） |
| 🔴 红 | 刚失败 | 保鲜期内且 `LastRunOk == false` |
| 🟢 绿 | 刚成功 | 保鲜期内且 `LastRunOk == true` |
| 🔵 蓝 | 等待 | 其余 |

**橙色优先级高于绿/红**："今天到此为止"是确定终止态，比"刚刚成功"更该被看到，
否则用户会以为还在跑、白等一天。

### 3.2 橙色灯可点击 → 清除今日标记

只在 `doneToday && !paused` 时，把灯包一层透明的 `IconButton`（22×22）作为点击区：

```csharp
dotBtn.Click += (_, __) => ClearTodayMark(f);
```

**为什么挂在灯上而不是再加按钮**：橙色只在这一种状态下出现，点它 = 清标记，语义自洽；
卡片横向已经很挤（自动开关 + 运行一次 + 编辑 + 删除），不再加按钮。

`ClearTodayMark(f)` 做的事：

```csharp
PlcLogic.ClearDailyLatch(f);      // 清 END 锁存
f.RunComplete = false;
f.ToggleTodayRequested = null;    // 清掉未消费的切换请求，避免下一轮又被写回
f.RunResetRequested = false;
FlowRunLifecycle.StartRun(f, DateTime.Now);   // 复位 PlcPower/ActionResults 等本轮状态
ConfigStore.Save(App.Config);     // 立即落盘
LogBus.Info("已清除今日标记", ...);
```

> ★ 必须一并复位本轮状态：只清 `DailyLatchKey` 会让 `PlcPower`/`ActionResults`
> 保持"已完成"的形状，可能清完仍然不动（2.17.24 修过类似的"失败结果卡死"）。

### 3.3 摘要文案

| 状态 | 文案 |
|---|---|
| 今日完成 | `今日已完成 · 点击橙点可清除标记，今天重新运行` |
| 保鲜期内成功/失败 | 原来的 `执行成功/失败「X」 · 时间` |
| 已过期 | `等待下一次 · 上次成功 16:37:00` |

## 4. 验证（无 UI，真实引擎）

用一个「间隔 2 秒 → 等待 0 秒 → END」的流程（动作选「等待」是刻意的：
不依赖会话/注入通道，必定成功，才能真正走到 END）：

```
有 END 节点 = True   IsDailyComplete 初始 = False   灯 = 蓝(等待)

=== 跑到 END ===
  第1轮: IsDailyComplete=True  DailyLatchKey='2026-11-20'  灯=橙(今日完成)
  第2轮: IsDailyComplete=True  DailyLatchKey='2026-11-20'  灯=橙(今日完成)

=== 今日完成后再扫描 ===
  Triggered=False  msg=今日已完成，日界 04:00 重置        ← 确认不再执行

=== 模拟点击橙点 → 清除今日标记 ===
  清除后 IsDailyComplete=False   灯=蓝(等待)              ← 确认回到等待

=== 清除后重新自动执行 ===
  第1轮: Triggered=True   IsDailyComplete=True  灯=橙      ← 跑完又锁存
  第2轮: Triggered=False  IsDailyComplete=True  灯=橙      ← 又停止
```

完整闭环：**蓝 → 跑完变橙 → 点橙清标记 → 回蓝 → 再跑 → 又变橙**。

> 第一次探针用「模拟按键」动作一直失败（A2 会话不在快照里），根本到不了 END，
> 灯一直是红 —— 这说明"验证 END 逻辑必须用必定成功的动作"，已记在本文档。

## 5. 产物

| 项目 | 值 |
|---|---|
| 版本 | 2.17.29.0 |
| `dist/SoulLink.exe` | 67,478,562 字节 · SHA256 `89B166C99FF1B68E408B630DB69417500193211F241E725B6B601B32FEAC1299` |
| 注入代理 | 未改动（2.17.21） |
| 可信归档 | 仍是 `v2.17.19-reliable` |

## 6. 待办

1. **UI 自适应**（用户多次提到）：小窗口卡片挤压、按钮点不到。仍未动。
2. 2.17.20 ~ 2.17.29 均未做可信归档。
