# SoulLink 交接文档 · 2.17.31（流程名后显示今日成功/失败次数）

> 时间：2026-11-20 17:35
> 版本：**2.17.31.0**（待定版；可信归档仍是 2.17.19）
> 性质：自动化卡片名称行增加今日成功/失败计数（绿/红小字）
> 用户要求：**界面操作由用户自己测，助手不做界面测试**

---

## 1. 用户要求

> 「在流程名称后面，要小字显示今日成功次数与失败次数。成功用绿色数字，失败用红色数字」

## 2. 关键设计点：为什么不能用 RunCount

`RunCount` 是**累计值**（重启也不清，本机已到 1850 次），
直接拿它显示"今日"是错的。而且它只在**整轮收尾**时才 +1，失败路径根本不加。

所以新增独立计数：

```csharp
public string StatDayKey { get; set; } = "";   // 计数所属业务日
public int TodayOkCount { get; set; }         // 今日成功次数
public int TodayFailCount { get; set; }       // 今日失败次数

public void RecordRunResult(bool ok, DateTime now)
{
    string key = PlcLogic.DayKey(now, PlcLogic.HasEnd(this) ? PlcLogic.EndBoundaryHour(this) : 0);
    if (!string.Equals(StatDayKey, key, StringComparison.Ordinal))
    {
        StatDayKey = key;              // 换日 → 自动清零
        TodayOkCount = 0;
        TodayFailCount = 0;
    }
    if (ok) TodayOkCount++; else TodayFailCount++;
}
```

**日界跟随现有语义**（重要）：无 END 流程日界 00:00；有 END 流程用 END 节点的
`DayBoundaryHour`（通常 04:00）。这样"今日"与流程自己的业务日定义一致，
不会出现"凌晨 2 点跑的算今天还是昨天"的分歧。

## 3. 计数点

`Services/PlcScanEngine.cs` 里**每次动作真正执行完**的地方：

```csharp
flow.LastRunOk = result.Ok;
...
flow.RecordRunResult(result.Ok, time);   // ← 新增
```

与 `LastRunOk` 同处写入，保证 **成功次数 + 失败次数 = 今天真正执行的动作次数**。

## 4. 界面（`Views/LaunchPage.xaml.cs`）

名称行从单个 TextBlock 改成横向 StackPanel：

```
lummy 自动化   19 / 0
                ↑绿  ↑红
```

* 成功次数：`#41F0C0`（青绿），9.5px，SemiBold
* 失败次数：`#FF6B8B`（红），9.5px，SemiBold，前面带 ` / ` 分隔
* 悬停有 ToolTip：「今日成功 N 次」/「今日失败 N 次」
* **两者都为 0 时不显示**，避免新流程一上来挂两个 0 像报错

## 5. 验证（无 UI，真实引擎）

```
初始:            今日 成功=0 失败=0  StatDayKey=''            界面显示=（不显示）
成功 3 次后:      今日 成功=3 失败=0  StatDayKey='2026-11-20'  界面显示= 3 / 0
再失败 2 次后:    今日 成功=3 失败=2  StatDayKey='2026-11-20'  界面显示= 3 / 2

=== 换日（业务日变化）应自动清零 ===
跨日后第一次成功: 今日 成功=1 失败=0  StatDayKey='2026-11-20'  界面显示= 1 / 0

=== 日界语义 ===
有 END（日界 04:00），凌晨 02:00 执行 → StatDayKey=2026-11-19  ✓（算前一天）
无 END（日界 00:00），凌晨 02:00 执行 → StatDayKey=2026-11-20  ✓（算当天）
```

### 真机（部署 2.17.31 后 35 秒）

| 流程 | StatDayKey | 今日成功 | 今日失败 | RunCount（累计） |
|---|---|---|---|---|
| db9328b9（暂停） | （空） | 0 | 0 | 11 |
| 38212a3a | 2026-11-20 | **19** | 0 | 1850 |
| 6c071938 | 2026-11-20 | **17** | 0 | 1279 |

注意 `RunCount=1850` 而 `今日成功=19` —— 这正是"累计 vs 今日"的区别，
证明新计数没有复用 RunCount。

## 6. 产物

| 项目 | 值 |
|---|---|
| 版本 | 2.17.31.0 |
| `dist/SoulLink.exe` | 67,479,754 字节 · SHA256 `B0246A2E09F2F2830CC515E914AD4527D32E4606A29B972E5B17FA74CF8CEBF8` |
| 注入代理 | 未改动（2.17.21） |
| 可信归档 | 仍是 `v2.17.19-reliable` |

## 7. 待办

1. **UI 自适应**（用户多次提到）：小窗口卡片挤压、按钮点不到。仍未动。
2. 计数是"每次动作执行"而非"整轮流程完成"，若用户希望按"轮"统计需另行调整。
3. 2.17.20 ~ 2.17.31 均未做可信归档。
