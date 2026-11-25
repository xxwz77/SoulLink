# t54 【P0】负时长 bug —— 诊断取证（未修复，交接给下一任）

> 状态：**诊断做到一半，代码一行未改**（`RunRecordCollector.cs` 与备份逐字节相同）。
> 双 TFM 可编译。下面是已确认的事实、已排除的方向、和下一步该怎么查。

---

## 0. 一句话结论

**根因不在 `ResolveTs` 的跨日判定**（t44 的逻辑是对的），而在**「结束锚点用的是哪个时刻」**这条路径上。
两条线索指向 `CloseRouteLocked` 的 `endMs` 来源，需要继续验证。

---

## 1. 已确认的实测数据（真实库 `dist\data\run-db\run.db`，只读）

    run    总数 103    负时长 7
    route  总数 2802   负时长 57

**所有 64 条负时长记录的 `src_file` 都是同一个** `better-genshin-impact20261125.log`。

### 1.1 负时长有两种形态

**形态 A（占多数）：开始时刻被 +1 天，结束时刻正常**

    09-冰醋栗-至冬-赫斯珀利德斯之馆西南-4个
      start = 2026-11-26 10:01:00.110   ← ★ 日期是 11-26，但日志是 2026-11-25.log
      end   = 2026-11-25 10:01:51.731
      dur   = -86,348,379 ms ≈ -86,348 秒（≈ -24 小时 + 52 秒）

    01-鳅鳅宝玉-荻花洲-13个
      start = 2026-11-26 15:09:06.083   ← ★ 同样多了一天
      end   = 2026-11-25 15:10:53.083
      dur   = -86,293,000 ms

    assets\ArtifactsPath\度假村\前执行\9999纳塔-度假村T人点
      start = 2026-11-26 12:55:28.883   ← ★
      end   = 2026-11-25 12:55:34.013

**形态 B：同一天内，开始晚于结束（没有 +1 天）**

    03【额外】须弥-凡爷屋顶罐子1
      start = 2026-11-25 13:32:51.992
      end   = 2026-11-25 08:01:00.535
      dur   = -19,911 秒 ≈ -5.5 小时

    A03-紫晶块-稻妻-鸣神岛-绀田村02-3个.json
      start = 2026-11-25 16:50:12.652
      end   = 2026-11-25 10:43:03.673
      dur   = -22,029 秒

**run 表 7 条**：形态 A 与 B 都有（如 `CD采集-挖矿` start=11-25 15:12:08 / end=11-25 11:53:55 是形态 B；
`CD采集-额外狗粮` start=11-26 13:23:00 / end=11-25 18:25:46 是形态 A）。

---

## 2. 已排除的方向（不要再往这查）

### ❌ `ResolveTs` 的跨日判定本身（t44 那段）没问题

`Services/RunRecordCollector.cs:338-373` 现状：

    if (clock.LastRawMs != long.MinValue && raw < clock.LastRawMs - 1_000)
    {
        long back = clock.LastRawMs - raw;
        if (back >= 22*3600_000 && back <= 26*3600_000) { raw += 86_400_000; ... }  // 真跨日
        if (back < 12*3600_000) { ms = raw; return true; }                          // 乱序行，不加天
        clock.LastRawMs = long.MinValue;                                            // >26h 重置
    }

**只有回退 22~26 小时才 +1 天**，乱序行（<12h）保持原值。看起来是对的。

### ❌ 「换文件时 ResetForNewFile 重置基线」这个方向

`OnFileChanged`（:526-540）确实做了 `clock.LastRawMs = long.MinValue`，
但**所有负时长记录的 src_file 都是同一个 `20261125.log`**，没有跨文件 → 不是这条路径。

### ⚠️ 但仍有一个疑点值得查

形态 A 的 start 是 **11-26** 而文件是 **20261125.log**。
`raw = clock.FileDayMs + ts`，若要得到 11-26 必须 `FileDayMs` 已经是 11-26 或 `raw += 86_400_000`。
**下一步要查：`FileDayMs` 在什么时候被设成 11-26 的？**
可能路径：`OnFileChanged` 被用**次日**的文件名调用过（先处理 20261126.log 再回喂 20261125.log），
或 `Feed` 内部的时钟复用导致 FileDayMs 被后一份文件覆盖。

---

## 3. 下一步怎么查（给接手的人）

### 第 1 步：确认 `FileDayMs` 与 `raw +1 天` 哪个是真凶

在 `ResolveTs` 里临时加一行诊断日志（**只在本地调试用，别提交**）：

    // 在 raw += 86_400_000 前后各打一条
    Console.WriteLine("[T54] back=" + back + " raw=" + raw + " lastRaw=" + clock.LastRawMs
        + " fileDay=" + clock.FileDayMs);

然后用 **A4 真实日志 20261125.log**（`D:\AAA挂机专区\A4\BetterGI\log\`，**只读**）
喂隔离库复现，抓出 64 条里的任意一条（建议拿 `09-冰醋栗-至冬-赫斯珀利德斯之馆西南-4个`，
它在 10:01 左右），看那天到底 +1 没 +1、`fileDay` 是不是 11-26。

### 第 2 步：重点看 `CloseRouteLocked` 的 endMs 来源

负时长 = start 被推进了 but end 停在旧值（形态 A 的 -86348 ≈ -86400 + 52 **说明 end 是对的、start 多了 1 天**）。

要看的是：

- `StartRoute`（:1415+）→ `startMs = atMs > 0 ? atMs : NowMs()`
- `CloseRouteLocked` / `EndRoute` 传进去的 `endMs` 是哪个变量
- **t51 的合并逻辑**（:1429-1444）在 `run.Route.Kind != "script" && kind != "route"` 时 **直接 return**，
  只更新 `run.LastEventMs`，**没有更新 `run.Route.StartAtMs`** —— 已确认这条不会造成 +1 天。

### 第 3 步：如果确认是 `raw += 86_400_000` 误触发

那说明 `back` 落在了 22~26h 区间但实际不是午夜跨越。
**建议修法**：跨日判定再加一条约束 ——
「+1 天」只在 `LastRawMs` 对应的**当日时刻**接近一天末尾（如 > 20:00）**且** `raw` 的当日时刻接近一天开头（如 < 04:00）时才允许：

    int lastHour = (int)(clock.LastRawMs % 86_400_000 / 3_600_000);
    int curHour  = (int)(raw % 86_400_000 / 3_600_000);
    bool looksLikeMidnightCross = lastHour >= 20 && curHour <= 4;
    if (back >= 22*3600_000 && back <= 26*3600_000 && looksLikeMidnightCross) { raw += 86_400_000; ... }

这样「10:01 → 10:01」这种绝不可能被判成跨日。

### 第 4 步：加防线（任务明确要求，还没做）

写入 `end_at_ms < start_at_ms` 时**记警告日志**，不要静默写库：

- 位置：`RunRecordStore.AddRoute` / `AddRun`（或 `CloseRouteLocked` / `CloseRunLocked` 收尾处）
- 内容：账号/run_id/名字/start/end/差值/src_file
- 用现有 `App.AppendErrorLog` 或 `LogBus.Warn`，**去重限流**（同一 run 只报一次）

### 第 5 步：清理已有 64 条脏数据

建议**不要盲删**，先按这个 SQL 捞出来人工确认再处理：

    -- 形态 A：start 日期比 src_file 的日期晚 → start 减 1 天
    SELECT route_id, name, start_at_ms, end_at_ms, src_file
    FROM route WHERE end_at_ms IS NOT NULL AND end_at_ms < start_at_ms;

    -- 修法（形态 A，且 end 与 start 的当日时刻只差几秒~几分钟）：
    UPDATE route SET start_at_ms = start_at_ms - 86400000
    WHERE end_at_ms < start_at_ms
      AND (end_at_ms - (start_at_ms - 86400000)) BETWEEN 0 AND 3600000;

    -- 修完后必须校验
    SELECT COUNT(*) FROM route WHERE end_at_ms < start_at_ms;   -- 期望 0
    SELECT COUNT(*) FROM run   WHERE end_at_ms < start_at_ms;   -- 期望 0

> ⚠️ 形态 B（同一天、start 晚 5.5 小时）**不能**用减一天修，那是另一类问题
> （可能是乱序行拿错基线）。这 64 条里形态 B 占少数，建议单独分析后再处理。

---

## 4. 已确认没有问题的东西（别重复踩）

| 项 | 结论 |
|---|---|
| t51 的改动 | 32 行（`StartRoute` 同名合并 + `map` 锚点按 `.json` 归类），与负时长**无关**，见 `git diff` |
| t44 的跨日修复 | `ResolveTs` 22~26h 判据逻辑正确，不是本次根因 |
| `IngestCursorStore.cs` | 未动 |
| `RunRecordStore.PurgeNoiseRawEvents` | 未动 |
| `Views/` | 未动 |
| 真实库 | **只读**打开（`mode=ro`），未写入任何数据 |

---

## 5. 复现环境（如果是新成员接手，可以这样快速起来）

    # 1) 隔离库，别碰 dist/data
    $T = "$env:TEMP\t54-repro"; mkdir $T
    $env:SOULLINK_DATA_DIR = $T

    # 2) 用 A4 真实日志（只读）喂一遍
    #    20261125.log 就是产生负时长的那份

    # 3) 查
    sqlite3 "$T\run-db\run.db" "SELECT COUNT(*) FROM route WHERE end_at_ms < start_at_ms"

---

## 6. 本次已交付的产物

- 本文档（诊断结论 + 取证数据 + 下一步 5 步）
- 无代码改动
