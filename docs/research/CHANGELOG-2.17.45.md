# SoulLink 2.17.45 · 数据中心重写 + 绑定语义收紧

发布时间：2026-11-21
文件版本：2.17.45.0

## 一句话

日志读取从「每秒把 88 MB 拼接后重解析一遍」改成「**单遍流式扫描 + 字节游标**」，挂机验证版在 A1 满速写日志时的 CPU 从 **87.5% 降到 12–13%**，工作集从 **1.2 GB 降到 390 MB**。

---

## 一、数据中心彻底重写（主因）

### 旧实现的问题（2.17.44 实测）

A1 的 BGI 日志每秒都在写，而 `AccountStateCenter` 每秒刷新一次，每次做四件极贵的事：

1. `BusinessDayData.Append` 把 `e.Version++`，**让整份投影缓存失效**；
2. `Filter` 把「今天 53 MB + 昨天 61 MB」拼成 **88 MB 字符串**；
3. `Split('\n')` 成 **150 万行**，逐行正则 + `DateTime.TryParse`；
4. `Tail(text, 400)` 再把 50 MB 拆一遍。

**代价**：88% 单核、1.1 GB 工作集、每秒约 1.1 GB 垃圾对象。

### 新实现

| | 旧 | 新 |
|---|---|---|
| 扫描方式 | 每 tick 全量重来 | **单遍流式 + 字节游标**，读过的字节永不再读 |
| 53 MB / 749k 行耗时 | 850–1,423 ms | **130–555 ms** |
| 单次分配 | 667–1,127 MB | **1 MB**（扫描本身） |
| 常驻内存 | ~231 MB 日志原文 | **~172 KB**（800 行尾窗） |
| 昨日日志 | 无条件读取（99 MB 常驻） | **完全不读** |

**新增文件**：

- `Services/BgiLogSnapshot.cs` —— 不可变快照（读侧无锁、不碰磁盘）
- `Services/BgiLogCenter.cs` —— 唯一日志读取点
- `Services/BgiDataDependency.cs` —— 「哪些节点依赖日志」的统一判定

**删除**：`Services/BusinessDayData.cs`（合并双业务日缓存的那个大户）

**合并**：原来 `CountDailyErrors`、`CountDailyPicksByGroup`、`DailyStatsCache.Calculate`、`ScriptGroupTracker.Update` 各自把同一份日志从头读一遍，现在一次扫描全部产出。

---

## 二、当前配置组：粘性快照 + 停止即清空

### 粘性（用户要求）

保留最近一次出现的配置组名，只有日志里出现新的才替换。**跨天滚动时继承** —— BGI 在 00:00 生成新日志、里面没有配置组行时，状态自然延续。

### 停止即清空（用户要求）

日志一旦出现停止锚点，**立刻清空运行状态**，不再保留：

```
配置组 "X" 执行结束
配置组 "X" 未成功完成，结果: Cancelled / Failed
取消执行配置组: "X"
检测到您配置的停止快捷键"F12"按下，停止当前执行任务
任务已停止 / 任务结束 / → "任务结束"
```

**快捷键停止走同一条路径**：BGI 把 F12 事件写进日志，我们读日志得到同样的结论。

**实测验证**（20261120 完整日志，99 次状态切换）：停止后 `CurrentGroup` 为空，直到下一条配置组行；跨天滚动时保持空。

顺带修掉一个坑：`取消执行配置组: "The operation was canceled."` 里的 "The operation was canceled." 会被当成组名，现在过滤掉了。

---

## 三、绑定语义收紧：未绑定 = 零 IO

### 问题根源

旧版 `BgiPathResolver.Resolve(persist: true)` 会**自动**把探测到的路径写进 `BgiPaths`，导致“用户是否显式绑定过”根本无法判断：

```
BgiPinnedUsers（手动绑定过的）= []        ← 一个都没有
BgiPaths（配置里的路径）      = [A1, A4]  ← 却都有路径
```

**光是打开「数据中心」页这个纯读动作，就会把路径写回 config.json。**

### 修复

1. 所有读取路径改 `persist: false`，**只有 `Pin()` 能写配置**；
2. 设置页的路径输入框原来**根本不会提交**（没有任何 LostFocus/KeyDown 处理），现在回车/失焦即绑定，清空即解绑；
3. 一次性迁移：只对「路径存在 **且** 该 BGI 正在运行」的账号自动补绑定（`Settings.BgiMigrated` 标记，只做一次）。

### 界面表现

| 场景 | 显示 |
|---|---|
| 未绑定 | 「BGI 未绑定」（琥珀色），**运行状态总览里直接不显示该账号** |
| 已绑定但没跑 | 「今天运行过，当前已退出」/「今日还未运行 BetterGI」 |
| 已绑定且已停止 | 「已停止（上一个：XXX）· 23:41:02 停止」 |
| 已绑定正在跑 | 「A1 · 思 · CD采集-全采集」+ 轮次/路线 |

**设置页按钮**：去掉了「自动」按钮（改造后不存在“自动识别”这条路径，留着会误导），改成「从进程取」+「解绑」。状态列从 `手动/自动/记忆` 改为 **`已绑定/未绑定`**。

**日志类条件**：`FlowEditorWindow` 的目标用户下拉里不列出未绑定账号；`FlowValidator` 对已有流程引用未绑定账号时直接判校验失败。

---

## 四、顺带修掉的持续性开销

| 位置 | 问题 | 修复 |
|---|---|---|
| `LaunchPage` 概览/状态 | 每 3 秒 `TailLinesDeep(4000 行 / 16MB)` 重新解析 | 改读快照（0 磁盘 IO） |
| `LaunchPage` 全天统计 | 额外跑 2 次全文件扫描 | 合并进单遍扫描 |
| `DataCenterPage` | 每秒无条件 `Children.Clear()` 重建整页 | 渲染签名去重 |
| `LaunchPage` 自动化卡片 | 每秒重建全部卡片 | 渲染签名去重 |
| `SessionService.QueryAsync` | 每轮起 **2 个子进程**（query.exe + qwinsta） | 合并成 1 次 qwinsta |
| `BgiPathResolver.Resolve` | 每 3 秒每账号重新枚举进程 | 1 秒缓存 + Pin/Unpin 失效 |
| `BgiDataCenter.Read` | 缓存命中仍扫 3 个目录算 Stamp | 1 秒内直接命中 |

> ⚠️ 踩过的坑：给 `DataCenterPage` 加渲染签名时把 `st.UpdatedAt.Ticks` 也放进去了，而它每轮刷新都是 `DateTime.Now`，签名每秒都变 → 去重完全失效，CPU 从 13% 反弹到 52%。**签名里绝不能放每次刷新都会变的字段。**

---

## 五、日界调整

- **日志派生数据（配置组 / 收益 / 拾取 / 报错）：硬性 00:00**（BGI 自己按天滚动，这是唯一自洽的界）
- **狗粮记录：保持 04:00**（来自 `DogfoodRecordReader` 读的独立 records 文件，不随日志滚动）

副作用：`BusinessDayData.GetView(id, 4)` 与 `GetView(id, 0)` 的双份缓存消失，`flow.DayBoundaryHour` 从此只影响狗粮条件。

---

## 六、构建

```bat
dotnet publish -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:DebugType=none -o dist
```

> ⚠️ 必须带 `EnableCompressionInSingleFile=true` 和 `DebugType=none`，否则产物 141.8 MB（正常 64.4 MB）。

> ⚠️ `SoulLink.csproj` 的 `DefaultItemExcludes` 已加入 `backups\**` —— 源码备份目录里是完整 .cs 副本，不排除会被通配符编译进来造成类型重复定义。

---

## 七、已知但本次未处理

1. **CPU 仍有 ~45–50%**，但已确认不是数据层（`BgiLogCenter.Tick` 仅 0.31%）：最小化窗口时掉到 11.7%，恢复后立刻回到 53% —— 全是 WPF 渲染。
2. **新身份首次启动 exit=1**（`--identity=XXX` 且无配置时）。**旧版 2.17.44 同样复现**，是既有问题，与本次改动无关。

---

## 八、实测对比（同一时刻，A1/A4 两个 BGI 都在满速写日志）

| 指标 | 2.17.44 | **2.17.45** |
|---|---|---|
| CPU | 64.9–88% 单核 | **12.4–13.3%** |
| 工作集 | 410–1,288 MB | **387–393 MB** |
| 私有内存 | 750–1,575 MB | **435 MB** |
| 磁盘读取 | 每秒几十 MB | **0 MB/s** |
| 数据中心常驻 | ~231 MB | **~172 KB** |
