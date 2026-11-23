# 数据中心唯一入口决策 + 数据库化评估

> 日期：2026-11-23
> 状态：决策存档（数据库化「后面再完善」，唯一入口规则「立即生效」）
> 触发：用户要求「数据中心改为数据库形式会更好吗？把整理出来的东西丢数据库，别的地方去数据库取数据，不用常驻内存」

---

## 0. 一句话结论

1. **数据库化暂缓**：当前数据量小，台账（RouteLedger）已经是「落盘 JSON + 后台写线程」，不是纯内存；今日统计（DailyStats）的真相源是 BGI 日志本身，本就不该建表。换 SQLite 收益为负。
2. **「唯一数据入口」立即生效**：所有派生数据（日志状态 / 今日收益 / 配置组 / 台账）只允许由数据中心（BgiLogCenter / AccountStateCenter / BgiDataCenter / DailyStatsCache / RouteLedger / ScriptGroupTracker）产生和维护；**其他地方一律从数据中心的内存快照或只读 HTTP API 按需取用**，禁止自行扫日志文件、自行枚举进程、自行递归扫目录。

---

## 1. 「数据中心」管辖范围（唯一真相源边界）

| 数据 | 唯一产生者 | 存储形态 | 取用方式 |
|---|---|---|---|
| BGI 日志 → 结构化状态 | `BgiLogCenter`（日志唯一读取者 + FileSystemWatcher 事件驱动） | 内存快照（800 行尾窗） | 快照 / API |
| 账号状态（会话/BGI PID/错误计数） | `AccountStateCenter` | 内存 | `Get()` / API |
| BGI 配置（路径/快捷键/组名） | `BgiDataCenter` | 内存缓存 1s | `Get()` / API |
| 今日收益（经验/摩拉/拾取） | `DailyStatsCache`（由 BgiLogCenter 喂） | 内存（日志可重建） | `Get()` / API |
| 配置组运行台账 | `RouteLedger`（由 BgiLogCenter.ProcessChunk 喂，后台线程落盘 JSON） | 落盘 JSON（`ledger\`） | `Query()` / API |
| 配置组状态机 | `ScriptGroupTracker` | 内存 | `All()` / API |

> 这些之外的「各自独立功能」（路线更新 RouteUpdater、BGI 备份 BgiUpdater、数据迁移 DataMigration、会话瘦身 SessionSlimmer）读的是**自己的文件/进程**，不属于数据中心管辖，不受本规则限制——它们本来就必须按需、后台执行，不是「数据中心的旁路」。

---

## 2. 唯一入口规则（防卡顿硬约束）

1. **禁止在 UI 线程 / 页面激活路径（如 SettingsPage.Activate）上**：扫日志、递归扫目录、枚举全部进程、起子进程（powershell/sc.exe/schtasks）。前例：`SETTINGS-PAGE-LAG-20261123.md`（进设置页 0.5~2s 卡顿）。
2. **新增数据访问 = 走数据中心缓存/API**，不新建第二条日志扫描链、不建新的 FileSystemWatcher、不新建周期任务、不在 API 里调 `RefreshAll` 或枚举全部进程（见 WEB-EXTENSION-PLAN §1.2 性能红线）。
3. **重活后台线程 + 按需触发**：只在收到请求（HTTP 请求 / 用户点击）时才取数，无请求 = 零新增 IO / 零新增进程。

---

## 3. 数据库化评估（何时才值得换 SQLite）

| 触发条件 | 现状 | 换 SQLite 的收益 |
|---|---|---|
| 数据跨天/跨月留存查询 | 台账保留有限天数，日志按天滚动 | 历史趋势查询变快 |
| 数据跨进程/跨机器共享 | `ledger\` JSON 单机文件 | SQLite 文件天然共享层 |
| 数据量大到 JSON/内存扛不住 | 现在量很小 | 索引 + 分页碾压 JSON 全量读 |

**结论：现在不上数据库。** 触发任一信号后，再走「SQLite 单文件 + schema 迁移 + 只读 API」专项。台账已是「落盘 + 后台写线程」，缺的只是「别的地方去取」的只读 API —— 已由 `GET /api/bgi/ledger` + `/api/bgi/stats` 交付。

---

## 4. 后续完善清单（按优先级）

1. ~~只读 API 暴露台账/今日收益~~ ✅ 已交付（3.3.21）
2. 审计现有「数据中心数据」读取旁路（本任务，见下方）
3. （未来）跨天历史查询变慢 → SQLite 迁移
4. （未来）网页后端独立进程 → SQLite 文件作共享层
