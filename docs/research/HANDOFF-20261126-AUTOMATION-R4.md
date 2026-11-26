# SoulLink 交接文档 — 自动化实时数据 + 会话操作 + 日志续行（2026-11-26 R4 终版）

> 日期：2026-11-26 18:40
> 版本：v3.3.50（`dist\SoulLink.exe`，已更新并重启）
> 前序：[HANDOFF-20261126-DATACENTER-R3.md](HANDOFF-20261126-DATACENTER-R3.md)
> 本文取代当天 17:35 的 R4 初稿（那一版第④项「未解决」，现已查明）。

---

## 0. 一句话现状

自动化「日志包含 / 配置组状态」**不触发**的根因已查明并修复：自动化读的是 `AccountState` 里每 30 秒才同步一次的副本，现改为直接读数据中心（`BgiLogCenter`）的版本缓存快照，新鲜度约 1 秒。用户已用「日志打印机」循环流程实测可触发。

| # | 事项 | 状态 |
|---|---|---|
| ① | 跳过重扫后配置组状态从 SQLite 灌回 Tracker | ✅ 完成 |
| ② | 新建自动化不再默认塞「定时·间隔」 | ✅ 完成 |
| ③ | 会话操作执行器（登录/注销/重连） | ✅ 编译通过，**未在 A2 实测** |
| ④ | 日志续行（无时间戳行）参与时间窗匹配 | ✅ 完成 |
| ⑤ | **自动化读实时快照（本轮核心）** | ✅ 用户实测可触发 |
| ⑥ | 启动配置组/一条龙成功时显示「清场」过程（含停止快捷键） | ✅ 完成 |
| ⑦ | 「今日成功/失败次数」按业务日显示 | ✅ 编译通过，**未实测跨日** |

---

## 1. ⑤ 根因与修法（最重要）

### 1.1 根因

`FlowRunner.ReadContactAsync` 读日志类/配置组类条件用的是 `AccountState.RecentLog / LogTail / Groups`。这些是 `AccountStateCenter.RefreshAccount` 从 `BgiLogCenter.Snapshot` **拷过去的副本**，而该中心空闲时只 **30 秒**刷一次（`IdleRefreshMs=30000`；启动后 60 秒、Kick 后 45 秒内才 1 秒）。

后果：「日志包含」时间窗只有 5 秒时，几乎没有一次刷新落在窗内，**一次都没读到「满足」** → `PlcScanEngine` 的瞬时锁存（`SatisfiedContacts`）没东西可锁 → 永不触发。配置里 `SatisfiedContacts: []` 恒为空正是证据。

> 注意：锁存本身没问题。`CondLogContains` 在 `IsMomentary` 名单里，只要某次扫描读到满足就会锁住、等动作完成才清。问题是**根本没读到**。

配置组状态略有不同：`Groups` 里的 `ScriptGroupRun` 是 Tracker 内部同一对象，直接被原地改 `State`，所以多数时候碰巧是实时的；但「今天首次出现 / Reset 后 / 启动灌库晚于快速期」三种情况仍滞后 30 秒，且是无锁数据竞争，不能依赖。

### 1.2 修法（不新增任何文件读取）

| 文件 | 改动 |
|---|---|
| `Services/BgiLogSnapshot.cs` | 新增 `TailText`：快照不可变且按 Version 缓存，同版本只拼一次，所有流程共用 |
| `Services/FlowRunner.cs` | 新增 `LiveLog(accountId,user)` → `BgiLogCenter.Snapshot`（回退 `SnapshotByUser`）。日志包含/不包含/有报错/日志匹配/配置组触发/配置组状态改读它；取不到时回退 `AccountState`。`GroupMatches` 签名改为 `(n, state, live, now)` |
| `Services/AccountStateCenter.cs` | `BuildTailText` 复用 `snap.TailText` |

设计原则（用户硬性要求）：**日志只由数据中心读一次**；自动化只读缓存快照（日志没变时 `Snapshot()` 返回同一对象，成本=一次字典查询）。

### 1.3 不要做的事

- 不要把 `AccountStateCenter` 改成 1 秒刷新：每轮要查会话 + 枚举 BGI 进程，当初就是为此降到 30 秒。
- 不要让自动化自己读日志文件。
- 不要订阅 `BgiLogCenter.Changed` 触发整轮刷新（BGI 狂刷日志时会频繁触发重活）。

---

## 2. ⑥ 启动前清场过程可见

`FlowRunner.RunActionAsync` 的 `ActStartOneDragon / ActStartGroups`：`BgiStartGuard.BeforeStartAsync` 的结果原来**只在失败时显示**，成功被丢弃，用户误以为没按「停止当前脚本」快捷键。

**实际每次都按了**（A2 日志可证：`检测到您配置的停止快捷键"NumPad9"按下，停止当前执行任务` 紧跟在配置组「执行结束」之后，随后新 BGI 进程启动）。现成功时返回 `清场：<p.Message>\n<启动结果>`。

顺序固定：停止当前脚本 → 关闭 BGI → 确认退出 → 启动（见 `BgiStartGuard` 注释，用户 2026-11 口述逻辑）。

---

## 3. ⑦ 今日成功/失败次数

`AutomationFlow.RecordRunResult` 虽有 `StatDayKey`，但**只在下次执行动作时才清零**；界面直接读 `TodayOkCount/TodayFailCount`，当天没再执行的流程会显示几天前的数字（实测 11-20 的流程仍显示 780/559）。

修法：`FlowModels.cs` 新增 `CurrentStatDayKey(now)` 与 `TodayCounts(now)`（存的日期不是当前业务日 → (0,0)）；`LaunchPage.xaml.cs` 改读 `TodayCounts`。业务日口径不变：有 END 用 END 日界，否则 00:00。**配置里存的旧数字不改**，下次执行由 `RecordRunResult` 清零。

---

## 4. ①②③④ 简述（初稿内容保留）

- **①** `ScriptGroupTracker.Seed` 纯拷贝重建；`BgiLogCenter.ResetForNewFile` 在 `persisted>0` 时异步 `SeedGroupsFromDbAsync` 把 SQLite 今日 run 灌回内存。根因：R2/R3「断点续传跳过重扫」后 `Groups.Reset()` 清空内存而「走 SQLite 重建」从未实现。
- **②** `FlowEditorWindow` 构造函数删掉「新建且空画布时塞 `TrigInterval`」。
- **③** 枚举 `ActSessionLogin=47 / Logoff=48 / Reconnect=49`；`FlowRunner.SessionOpBridge` 委托由 `MainWindow` 指向 `ConsolePage.RunSessionOpAsync`。⚠ 走 RDP 控件，需账号配置 RDP 地址+密码；**注销不可恢复**，只在 A2/A3 验证，勿动 A1/A4。
- **④** `PlcLogMatcher.Scan`：带时间戳行更新基线（时刻+级别），续行沿用上一行时间戳/级别参与时间窗。BGI 脚本打点（如「循环测试」）是续行，旧逻辑整体跳过。

---

## 5. 用户使用层面的坑（排查时先看这里）

1. **匹配词撞上一条龙名**：用户的一条龙叫「循环测试」，「日志包含」匹配词也是 `循环测试`。BGI 每次启动都写 `参数指定的一条龙配置：循环测试`，条件立刻再次满足 → 流程每 ~8 秒触发一次，**新 BGI 还没跑起配置组就被下一次触发杀掉**（A2 日志 18:22 起只到「启用任务总数量: 1」）。建议匹配词改 `日志打印完成` 或 `第 3/3 轮`。
2. **循环测试流程会自触发**：动作=再跑「日志打印机」，而它又打出匹配词。用户已确认是有意的循环测试，无需处理；测试其它场景记得接 END 或禁用。
3. 「日志包含」只看时间窗、**用完不清空**；要「出现一次触发一次」用「日志匹配」(`TrigLogMatch`，出现即锁存，动作成功后清空)。
4. 配置里 5 秒窗口 + 「配置组已完成」比最后一次打点晚约 1 秒，两个条件同时成立仅约 4 秒——实时数据修好前基本读不到，修好后可行但仍偏紧。

---

## 6. 环境（实测 18:35）

| 程序 | 状态 |
|---|---|
| `dist\SoulLink.exe` | dev，PID 38804，session 1，owner lummy，v3.3.50 |
| `dist\stable\SoulLink-Stable.exe` | **未碰** |
| A1 / A4（session 2/3） | **未碰** |

替换 exe 的注意：`Stop-Process` 后进程**不会立刻释放文件句柄**，必须循环重试 `Copy-Item`（本轮第一次直接复制失败，旧版被我重新拉起了一次）。

---

## 7. 协作红线（不变）

- A1 / A4 一切不动；停进程用精确 PID，先核 owner。
- `dist\stable\SoulLink-Stable.exe` 不动。
- `D:\AAA挂机专区\A1\BetterGI\User\ScriptGroup\` 只读。
- 构建：`dotnet build SoulLink.csproj -c Debug`；发布主程序：`dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -f net11.0-windows10.0.26100.0`，产物复制到 `dist\SoulLink.exe`。
- 云端发布仍只走 `scripts/publish-release.ps1 -Full -Push`（本轮**未**发云端）。

---

## 8. 下一步建议

1. 在 A2 实测会话操作（登录/重连；注销谨慎）。
2. 观察 `今日成功/失败` 次数过 04:00/00:00 日界后是否归零。
3. 若觉得自动化每秒扫描日志条件有 CPU 压力：按「快照 Version 未变且时间窗未到期则复用上次判定」做缓存（需处理时间窗到期）。
4. 用户若要对外发版：先升版本、走 `publish-release.ps1 -Full -Push`。

---

> 本文档由 2026-11-26 第四轮会话整理。
> **如果只读一段，读 §1（根因）和 §5（用户侧的坑）。**
