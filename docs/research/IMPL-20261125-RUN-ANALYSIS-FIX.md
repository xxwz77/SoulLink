# t51 运行分析页：ASC 排序 + json 重复归类修复 + 重复路线聚合 + 卡片式（3.3.49）

> 用户原话：「排序应该是从早到晚，数据识别有误，实际上全是路线。样式不好看。」

---

## 0. 结论摘要

| 验收项 | 结果 |
|---|---|
| 1) 排序 ASC（早→晚） | ✅ 实测 11-19 → 11-20 → 11-21 → 11-22 → 11-23 严格递增 |
| 2) json 不再同时记「路线」+「脚本」 | ✅ 采集器修复 + 实测「无冲突 ✓」 |
| 3) 重复路线聚合「执行 N 次」 | ✅ 「13 次执行 / 6 个不同名字」，可展开看每次明细 |
| 4) 卡片式 + 彩色标签 | ✅ 圆角卡片、pill 标签（蓝=时长/青=拾取/黄=摩拉） |
| 5) 双 TFM 0 error | ✅ |

---

## 1. ★ 问题 2 的真实根因（与任务描述的猜测不同）

任务描述猜测是「`script` 锚点重复了 `route`」。**实测不是**。

用 A4 20261124 日志（8114 行）扫描三类锚点：

    route 锚点（进入路线循环 route="X.json"）        : 10 次
    map   锚点（→ 开始执行地图追踪任务: "X.json"）   : 10 次   ← ★ 才是重复源
    script 锚点（→ 开始执行JS脚本/独立任务: "X"）    :  4 次

**按名字分**：

    .json 结尾的名字  →  route=10, map=10   （两者指向同一批文件）
    非 .json 的名字   →  script=4           （「圣遗物调查点纯激活」「自动秘境」「退出游戏」等真脚本）

**配对验证：10/10 全部配对**，每个 `route` 锚点后 12 行内必有一个同名 `map` 锚点：

    route L2437  07【额外】至冬-旧基捷城西盗罐者6.json  -> map L2449
    route L2953  08【额外】至冬-旧基捷城东盗罐者17.json -> map L2965
    …（10 条全配对）

**实测日志上下文**（L2437 → L2449）：

    L2437 [DEBUG] 进入路线循环，count=1，route="07【额外】…6.json"     ← route 锚点
    L2440 「第 1/2 队 派蒙屁屁」 当前进度，开始第 1/5 条线路: "07….json"
    L2449 → 开始执行地图追踪任务: "07【额外】…6.json"                  ← map 锚点（12 行后）
    L2452 [调试] 已注册 MultiplayerCoordinator 和 PathExecutor，路线: "07….json"

→ **同一个文件、同一次执行的两种阶段标记**，不是两次执行。

### 1.1 为什么原有代码没合并掉

`RunRecordCollector.cs` 里本来就有一个 `sameAsOpenRoute` 判断（L870-877），
但它在 **`ScanEvents` 扫描期**执行 —— 那时这批事件还没 `Apply`，
`OpenRuns` 里还是**上一批事件之后的状态**（route 窗口尚未打开）→ 判断永远 false。

### 1.2 修复（两处，都在 `RunRecordCollector.cs`，**未动 Store/IngestCursorStore**）

**① `StartRoute` 实时同名合并**：在 Apply 期判断，当前打开的 route 同名且不是纯脚本
→ 只 `Touch`（刷新 LastEventMs），不新建 record。

    if (run.Route != null
        && string.Equals(run.Route.Name, name, StringComparison.OrdinalIgnoreCase)
        && run.Route.Kind != "script"
        && kind != "route")          // route 锚点自身仍开新窗（它是真正的边界）
    {
        run.LastEventMs = Math.Max(run.LastEventMs, atMs);
        return;
    }

**② `map` 锚点归类修正**：名字以 `.json` 结尾 = **路线文件**，不是脚本。

    bool isRouteFile = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    string mapKind = isRouteFile ? "route" : "script";

这样即使某个 `map` 锚点单独出现（没有前置 `route`），也不会被误标成 script。

### 1.3 修复效果（真实日志实测）

    修复前：同一个 .json 同时出现 route 行 + script 行
    修复后：每个 run 的「同名多 kind」数量 = 0
            实测 24 个 run 全部 0 ✓
            byKind 只剩 script / route / cd_line 三类，各司其职

---

## 2. 问题 3：重复路线 —— 是真实多次执行，按 Lead 指示保留 + 界面聚合

实测（修复后，同一 run 内）：

    联机锄地-罐子：10【额外】至冬-旧基捷城西南盗罐者5.json  出现 5 次
                    08【额外】至冬-旧基捷城东盗罐者17.json   出现 4 次
                    99-ZZZZ996璃月-明蕴镇神像.json           出现 4 次

这与 Lead 核实的时间戳规律一致（间隔约 70 分钟，按计划循环）：

    20:57:27 / 22:07:27 / 23:17:27 / 00:28:19 ...   ← 真实跑了多轮

**处理原则（Lead 定稿）**：真实的多次运行全部保留（数据中心只采集不筛选），
界面用**聚合展示**让用户看懂。

**界面实现**：

    路线 / 脚本明细  13 次执行 / 6 个不同名字

    07【额外】至冬-旧基捷城西盗罐者6.json  [路线] [执行 3 次]
      总耗时 1分16秒 | 拾取 7 | 平均 25秒/次 | 展开 3 次明细
    08【额外】至冬-旧基捷城东盗罐者17.json [路线] [执行 3 次]
      总耗时 3分14秒 | 拾取 18 | 平均 1分5秒/次 | 展开 3 次明细
    锄地一条龙（联机）                      [脚本]
      总耗时 5分23秒 | 展开 1 次明细

展开某张卡（实测）：

    第 1 次  23:20:20  0秒      中断  0 拾取
    第 2 次  23:20:20  1分16秒  成功  7 拾取  幸运儿×4、游医×3
    第 3 次  23:29:01  0秒      中断  0 拾取

→ 既真实（每次都列出）又不眼花（默认聚合）。

**根因说明**：`MakeRouteId(runId, kind, name, startMs)` 的 key 里**含 startAtMs**，
所以每次进入同一路线都会生成不同 route_id → 落多条。这是**设计如此**
（同一路线跑多轮应算多次），不是 bug，因此**未改存储层**。

---

## 3. 问题 1：排序改 ASC

改 5 处默认值（`'start-desc'` → `'start-asc'`）：

| 位置 | 作用 |
|---|---|
| `RA.f` 初始值 | 页面首次进入 |
| `RA.f = {...}`（`raResetFilter`） | 点「重置」 |
| `const s = f.sort \|\| ...` | 排序实际生效处 |
| `raSaveFilter` 的 hash 判断 | 只在非默认时写 hash |
| `raLoadFilterFromHash` | 从 hash 恢复 |

排序选项也改了文案，让人一看就懂：

    ['start-asc', '开始时间 ↑ 早→晚']
    ['start-desc', '开始时间 ↓ 晚→早']

**实测界面顺序**（15 张卡片）：

    罐子激活      11-19 04:01:25
    联机锄地-罐子 11-19 23:14:56
    罐子激活      11-20 04:01:20
    联机锄地-罐子 11-20 19:41:14
    罐子激活      11-21 04:01:29
    罐子激活      11-22 04:01:14
    联机锄地-罐子 11-22 22:23:12
    罐子激活      11-23 04:01:19
    …严格递增 ✓

---

## 4. 问题 4：卡片式样式

**运行记录**：表格 → 卡片（`.ra-runcards` / `.ra-rcard`）

    每张卡：
      头部：[▸] 配置组名  [状态徽标] [跨天]        11-19 04:01:25 → 04:10:43
      标签：[9分18秒] [拾取 73] [3 脚本] [1,234 摩拉/分] [56,789 经验]

**彩色 pill 标签**（`.ra-tag` + 修饰类）：

| 类 | 颜色 | 用途 |
|---|---|---|
| `.ra-tag-dur` | 蓝 `rgb(143,184,255)` | 时长 |
| `.ra-tag-pick` | 青 `rgb(65,240,192)` | 拾取数 |
| `.ra-tag-mora` | 黄 | 摩拉/分 |
| `.ra-tag-dim` | 半透明 | 辅助信息 |

**路线明细**：表格 → 3 列自适应卡片网格（`.ra-cards`，`auto-fill minmax(258px,1fr)`），
展开时 `grid-column:1/-1` 占满整行。

**实测计算样式**：

    .ra-tag        → borderRadius 9px, padding 2px 9px, fontSize 10.5px
    .ra-tag-dur    → rgb(143, 184, 255)
    .ra-tag-pick   → rgb(65, 240, 192)
    .ra-rcard      → bg rgb(14,21,38), borderRadius 10px, border rgb(37,49,75)

全部沿用现有 CSS 变量（`--card2`/`--border`/`--txt`/`--accent`），
**未引 CDN、未引框架**；窄屏（<620px）卡片单列、时间换行。

---

## 5. 改动清单

| 文件 | 改动 |
|---|---|
| `Services/RunRecordCollector.cs` | ① `StartRoute` 加实时同名合并；② `map` 锚点按 `.json` 后缀归类（约 +30 行） |
| `docs/web-index.html` | ① 排序默认 ASC（5 处）；② 运行记录表格 → 卡片；③ 路线明细 → 聚合卡片网格；④ 新增 `raRouteCardsHtml`/`raUniqueNames`；⑤  CSS（约 +80 行） |
| `SoulLink.csproj` | 3.3.48 → **3.3.49** |

**未动**（硬约束）：`Services/IngestCursorStore.cs`、`RunRecordStore.cs`（含 Purge）、`Views/`。

---

## 6. 测试方法

用 **A4 真实日志**（`D:\AAA挂机专区\A4\BetterGI\log`，只读）喂隔离库
（`SOULLINK_DATA_DIR` → `%TEMP%\t51-ui\data`）：

    24 runs / 各 run 13~20 条路线记录 / 同名多 kind = 0

**A1/A4 目录未被写入**；测试副本按精确路径删除。
