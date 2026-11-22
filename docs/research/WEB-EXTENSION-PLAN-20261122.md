# 网页端 BGI 数据访问与自动化流程编辑方案

> 日期：2026-11-22  
> 基线：3.3.19 交接文档及当前工作区源码  
> 范围：只读分析与设计；**未修改业务代码、未同步 dist、未提交 Git、未发版**  
> 用户需求：后期网页增加 BGI 数据访问、自动化流程编辑；两端 API **按需触发，不要持续不断**。

---

## 0. 结论先行

1. **BGI 数据全部走主程序，默认只拉取，不订阅、不推送。** 直接复用 `BgiLogCenter` 的内存快照与 `BgiDataCenter` 的缓存，不为网页新建日志扫描、FileSystemWatcher、TickHub 订阅或 WebSocket/SSE。只有实时画面沿用已有的显式观看会话机制。
2. **先只读，再 JSON 编辑，最后才做拖拽画布。** 推荐 P0 安全与编辑事务底座 → P1 BGI/流程只读 → P2 JSON 编辑 → P3 可视化画布。不要第一期就复刻 950 行 WPF 画布和 2750 行编辑器。
3. **网页编辑当前必须默认关闭。** 空口令允许匿名、明文 HTTP、代理 `Access-Control-Allow-Origin: *`，再叠加流程中的启动程序、结束进程、键鼠注入能力，会把“看画面”扩大成宿主操作/脚本执行面。鉴权分级、Origin 限制、服务端确认、审计与编辑事务未完成前，不应暴露任何流程写接口。
4. **并发采用乐观锁，不是网页优先，也不是最后保存者无条件赢。** 每个流程增加独立的 `editRevision`，网页和 WPF 都带基准版本保存；第一个合法提交成功，另一个得到 412 并重新合并。运行统计变化不得递增编辑版本，否则流程会永远保存冲突。
5. **前端选择原生 ES Module + 轻量 PowerShell 同步/检查，不引入 React/Vue。** 当前检出的 `docs/web-index.html` 实际为 1218 行（任务描述的 1111 行应来自较早快照），继续堆全局脚本会扩大“一行重复 const、整页失效”的故障半径。

### 推荐架构

```text
浏览器（点击页面/刷新按钮才发请求）
   │
   │ 主站：GET 只读数据；POST 显式编辑命令
   ▼
SoulLink 主程序 WebServer
   ├── WebAuth：read / edit 两个独立权限，默认关闭 edit
   ├── BgiWebProjection ──> BgiLogCenter 内存快照（不重新扫日志）
   │                    └─> BgiDataCenter 缓存（不强制 RefreshAll）
   └── FlowEditService ──> 克隆 → FlowValidator → 版本检查
                         → 单流程编辑门 → 备份 → 原子保存 → 事件通知

代理直连端口：仍只负责实时帧 / 键鼠 / info
             不增加 BGI 文件、流程文件或任意磁盘文件服务
```

---

## 1. 现状与硬约束

### 1.1 BGI 数据的真实来源

- `BgiDataCenter` 读取 `<BGI>\User\config.json`，并列出 `User\OneDragon\*.json` 与 `User\ScriptGroup\*.json` 的文件名；缓存 1 秒，超过后通过目录/文件 stamp 判断是否重读。
- `BgiLogCenter` 是日志唯一读取者。BGI 写 `<BGI>\log\better-genshin-impactYYYYMMDD.log`，每条 flush；主程序用 watcher 标记脏数据，再在既有 1 秒合并窗口内增量解析。网页不应再建立第二条读取链。
- 日志快照保留：当前组/任务/路线/进度、配置组状态、当日经验/摩拉/拾取、错误计数，以及最多 800 行、200,000 字符的尾窗。
- `BgiStatusParser` 仅做日志文本到结构化状态的解析；`BgiTaskPreparer` 是停止/清场等**控制路径**，不是新的统计数据源。本期 BGI 数据接口不得顺带暴露这些控制能力。

### 1.2 性能红线

- 不新增无条件周期任务；确实需要周期任务时只能复用 `TickHub`，重活离开 UI 线程。
- 不在网页 API 中调用 `RefreshAll`、枚举所有进程或扫描整天日志。`BgiLogWatcher` 的历史实测已经表明：几十 MB 日志和反复进程枚举会显著抬升 CPU/内存。
- 不因为没有网页请求而增加主程序工作量；没有请求时应是**零新增网络流量、零新增网页专用扫描**。
- 不在 UI 线程做文件 IO；保存、备份、读取配置均放后台，并做串行化。

### 1.3 当前 Web 约定及容易踩的坑

- 主站 `Services/WebServer.cs` 使用 HttpListener；除登录外经 `IsAuthorized` 检查 `soullink_sid` Cookie。**主站没有名为 `CheckToken` 的方法**；`CheckToken` 位于代理 `Relay/AgentWebServer.cs`。下文“接 CheckToken”指抽取统一鉴权入口，而不是误调用代理的私有方法。
- 主站 POST 前置白名单只放行 `/api/input`、`/api/settings`，其余 POST 在路由前直接 404。新增写接口必须同步修改 `WebServer.HandleAsync` 的白名单/路由表。
- 非 GET/HEAD/POST 会被 405；因此首期不引入 PUT/PATCH/DELETE，写操作使用明确命名的 POST 命令。
- 代理 CheckToken 接受 `X-SoulLink-Token` 或 `?token=`，后者只是 `<img>/<video>` 无法自定义请求头的兼容方案；流程编辑 API **禁止 URL token**。
- 页面运行时优先读 exe 同目录文件；修改源页面后仍需运行 `scripts/sync-webpages.ps1`，同步生成兜底代码和运行目录文件。本方案没有执行该脚本。

---

## 2. BGI 数据清单与 API 形态

“单次量级”中，硬上限来自代码；名称数量、物品种类相关项只能给估算，当前没有逐账号生产数据实测，不能冒充基准值。

| 数据项 | 来源文件/内存来源 | 产生与更新频率 | 单次量级 | 适合持续推给网页？ | 建议 API |
|---|---|---|---|---|---|
| 绑定状态、BGI 是否运行、PID、日志活跃/空闲 | `BgiPathResolver`、`AccountStateCenter`、`BgiLogSnapshot` | 进程状态秒级；日志随任务写入，中心最多 1 秒合并一次 | 每账号几十至数百字节（估算） | **否**；状态可刷新 | `GET /api/bgi/accounts` 或单账号 summary |
| 当前配置组/上一组/停止时间、当前一条龙 | 当日 BGI 日志 → `BgiLogCenter` | 任务切换时变化 | 通常 <1 KB（估算） | 否 | summary，ETag |
| 任务名、轮次、角色、路线、进度、ETA、累计时长、当前动作 | 当日日志 → `BgiStatus` | 跟随日志，可能很频繁 | 通常 1~3 KB（估算） | 否；用户点详情/刷新才取 | summary 的 `status` 子对象 |
| 今日配置组运行记录、脚本数、开始/结束时间、失败原因 | 当日日志 → `ScriptGroupTracker` | 每个组开始/结束时 | 随组数增长；先限制 200 组 | 否 | `GET .../stats/today?include=groups` |
| 今日经验、摩拉、拾取合计、狗粮路线 | 当日日志 → `DailyStats` | 每次相关日志增量累计 | 计数很小；物品表随种类增长 | 否 | `GET .../stats/today` |
| 全天按配置组分类的物品明细 | `BgiLogSnapshot.PicksByGroup` | 拾取时增量累计 | 组数 × 物品种类；必须分页/限量 | 否 | today 接口 `include=picks&group=...`，按需展开 |
| 错误/警告/任务失败计数 | `BgiLogSnapshot` 顶层计数 | 随日志增量 | <200 字节 | 否 | summary；不要重复采用 `Status` 内另一套计数 |
| 最近日志尾窗 | `BgiLogSnapshot.Tail` | 高频；中心已增量维护 | **最多 800 行 / 200,000 字符**；JSON 约数百 KB 上限 | **不适合**；默认不自动跟随 | `GET .../logs?afterSeq=&limit=100`，硬上限 200 |
| 一条龙/配置组可选名称 | `User\OneDragon\*.json`、`User\ScriptGroup\*.json` → `BgiDataCenter` | 用户增删配置文件时 | N 个文件名；无现成数量上限，需响应限量 | 否 | `GET .../catalog`，分页/截断标记 |
| 停止快捷键及已识别快捷键 | `User\config.json` → `BgiDataCenter` | 用户修改 BGI 配置时 | 通常数 KB 内（估算） | 否 | 仅编辑节点选择器需要时经 catalog 取；敏感字段脱敏 |
| 原始整日日志、BGI exe/config/log 的宿主绝对路径 | 磁盘文件 | 文件可达几十 MB；历史样本 53 MB/749k 行、60 MB | 大；且泄漏目录信息 | **否** | **不提供**；如需历史导出另做受控异步任务 |
| 停止 BGI、清场、关闭游戏、启动配置组 | `BgiTaskPreparer`/`FlowRunner` 控制链 | 用户命令触发 | 非数据接口 | 不适用 | 不混入 BGI 只读 API；未来独立高危命令授权 |

### 2.1 实时性口径

BGI 日志 flush 和主程序 watcher 能让桌面数据接近实时，但这**不等于网页必须实时**。网页采用以下口径：

- 打开“BGI 数据”页：取一次账号摘要。
- 展开某账号：再取该账号 today/catalog；未展开不取。
- 点击“刷新”：条件 GET；数据未变返回 304。
- 日志默认只显示一页尾窗，用户再点“加载更新”才带 `afterSeq` 请求。
- 页面隐藏、切页、关闭详情：没有定时器需要停，因为根本没有 BGI 轮询循环。

如未来明确要求“网页日志实时跟随”，应作为单独开关评审；只能在可见、用户主动开启时用串行 `setTimeout + afterSeq`，不允许 `setInterval`、不允许隐藏页运行，也不能把它变成默认行为。

### 2.2 快照版本与增量的正确用法

- 摘要/统计：使用 ETag / `If-None-Match`，适合“内容没变就 304”。
- 日志：使用**逐行 seq**，适合追加读取；不能用文件字节 `Cursor` 充当行号，因为一次字节增量含多行。
- 配置名称列表：ETag + 限量即可，不为文件名列表维护事件流。
- 进程重启/日志换日：响应 `reset:true`，客户端丢弃旧 seq 后重新取最近一页。

`BgiLogCenter.Entry.Version` 已是内部可见变化版本，可作为实现基础，但需要只读投影，且重启后要带 bootId，不能把重启后的 1 与重启前的 1 当成同一版本。最稳妥的通用实现是对小型规范化 DTO 计算 SHA-256 ETag；日志另加持久于当前尾窗生命周期的 `seq`。

**快照注意点：** 当前 `BgiLogSnapshot` 缓存并非所有嵌套对象都深拷贝（如 `Status`、`Today`、组记录仍有共享引用）。Web 序列化不能直接枚举一份可能正被 Pump 更新的对象图；应在数据中心一致读取点生成仅含所需字段的 DTO，或把快照真正改成不可变对象。不得直接把 `App.Config`、完整 `BgiAccountData` 或快照原样返回。

---

## 3. 新增 API 设计

### 3.1 通用约定

- Base URL 仍为主站，例如 `/api/bgi/...`、`/api/flows/...`；**不经过账号代理端口**。
- JSON 使用 camelCase；枚举使用名称字符串，与 `ConfigStore` 的 `JsonStringEnumConverter` 一致；时间使用带偏移的 ISO 8601。
- 新接口使用真实 HTTP 状态码，并保持现有 `ok/error` 风格：`{ "ok": false, "code": "STALE_FLOW", "error": "流程已被其他端修改" }`。不要改变旧接口的响应结构。
- 成功读取：`Cache-Control: no-store`；流程详情另返回 `ETag: "7"`。日志/账号 ID 等只接受已配置账号，禁止路径参数。
- 请求体限制：BGI 无 POST；流程 validate/save 默认最大 256 KB，节点最多 128、连线最多 256、单参数值最多 4096 字符。超限 413，校验失败 422。
- 不返回异常堆栈、宿主路径、完整令牌或配置全文。

### 3.2 路由总表

| 方法 | 路径 | 权限 | 用途/增量 |
|---|---|---|---|
| GET | `/api/bgi/accounts` | read | 已绑定账号摘要列表；ETag，可按需只取摘要 |
| GET | `/api/bgi/accounts/{accountId}` | read | 单账号当前摘要；支持 If-None-Match |
| GET | `/api/bgi/accounts/{accountId}/stats/today` | read | 今日收益/拾取/组记录；include 控制展开 |
| GET | `/api/bgi/accounts/{accountId}/logs?afterSeq=18320&limit=100` | read | 有界尾窗增量；limit 默认 100、最大 200 |
| GET | `/api/bgi/accounts/{accountId}/catalog?kind=ScriptGroup&page=1&pageSize=100` | read | 流程选择器需要的一条龙/组名；不为所有账号预取 |
| GET | `/api/flows?accountId=acc_a2&page=1&pageSize=50` | read | 流程摘要，不返回全部节点与运行态字典 |
| GET | `/api/flows/catalog` | read | `FlowNodeCatalog` 的唯一目录投影；应用版本作 ETag |
| GET | `/api/flows/{flowId}` | read | 可编辑定义 + editRevision + 摘要；ETag |
| POST | `/api/flows/validate` | edit | 对候选克隆运行服务端 FlowValidator；不写盘、不运行 |
| POST | `/api/flow-confirmations` | edit + step-up | 为精确 payload 发一次性、短时确认凭证 |
| POST | `/api/flows` | edit + confirmation | 新建，默认 disabled；服务端生成 id/revision |
| POST | `/api/flows/{flowId}/save` | edit + confirmation | 保存定义，必须 If-Match |
| POST | `/api/flows/{flowId}/state` | edit + confirmation | 启用/暂停等状态命令，不与图形定义混写 |
| POST | `/api/flows/{flowId}/delete` | edit + confirmation | 删除，必须 If-Match，先备份 |
| GET | `/api/flows/{flowId}/revisions` | read | 备份元数据列表，不含完整配置 |
| POST | `/api/flows/{flowId}/restore` | edit + confirmation | 恢复成新 revision；不是直接覆盖 config.json |

所有 GET 都是拉取；没有 `/subscribe`、WebSocket、SSE 或 long-poll。validate 虽无副作用，但需要复杂 JSON 请求体，所以是 POST，**也必须加入白名单**。

### 3.3 BGI 摘要示例

请求：

```http
GET /api/bgi/accounts/acc_a2 HTTP/1.1
If-None-Match: "bgi-01J...-41-18320"
```

未变化：

```http
HTTP/1.1 304 Not Modified
ETag: "bgi-01J...-41-18320"
```

变化后的 200 响应：

```json
{
  "ok": true,
  "accountId": "acc_a2",
  "revision": "bgi-01J...-41-18320",
  "updatedAt": "2026-11-22T17:30:00.000+08:00",
  "bound": true,
  "running": true,
  "currentGroup": "CD采集-全采集",
  "groupRunning": true,
  "status": {
    "taskName": "每日采集",
    "routeFile": "03-霜仙花-示例路线",
    "routeProgress": "48/94",
    "elapsed": "1时13分03秒",
    "currentAction": "地图追踪"
  },
  "counts": { "errors": 2, "warnings": 18, "failures": 1, "picks": 270 },
  "latestLogSeq": 18320
}
```

### 3.4 日志增量示例

```http
GET /api/bgi/accounts/acc_a2/logs?afterSeq=18320&limit=2
```

```json
{
  "ok": true,
  "reset": false,
  "items": [
    { "seq": 18321, "at": "2026-11-22T17:30:01.000+08:00", "level": "INF", "text": "交互或拾取：\"霜仙花\"" },
    { "seq": 18322, "at": "2026-11-22T17:30:02.000+08:00", "level": "INF", "text": "当前进度：19个" }
  ],
  "nextAfterSeq": 18322,
  "hasMore": false
}
```

若 afterSeq 已滚出 800 行尾窗或进程已重启：

```json
{
  "ok": true,
  "reset": true,
  "reason": "sequence_unavailable",
  "items": [],
  "latestSeq": 19001,
  "retryUrl": "/api/bgi/accounts/acc_a2/logs?limit=100"
}
```

这不是完整历史日志协议；尾窗拿不到的旧数据不能假装还在。也不允许为了补分页而每次重扫几十 MB 原文件。

### 3.5 流程读取与保存示例

`GET /api/flows/flow_01` 返回：

```json
{
  "ok": true,
  "id": "flow_01",
  "revision": 7,
  "definition": {
    "name": "示例自动化",
    "accountId": "acc_a2",
    "nodes": [
      {
        "id": "n_schedule",
        "kind": "Condition",
        "type": "TrigInterval",
        "x": 100,
        "y": 100,
        "logic": "LD",
        "negated": false,
        "branchMode": "Parallel",
        "params": { "intervalSeconds": "600", "user": "EXAMPLE\\A2" }
      },
      {
        "id": "n_wait",
        "kind": "Action",
        "type": "ActDelay",
        "x": 100,
        "y": 300,
        "logic": "AND",
        "params": { "seconds": "10" }
      }
    ],
    "edges": [ { "from": "n_schedule", "to": "n_wait", "branch": "" } ]
  },
  "runtime": {
    "enabled": true,
    "paused": false,
    "lastRunOk": true,
    "todayOkCount": 2,
    "todayFailCount": 0
  }
}
```

保存请求：

```http
POST /api/flows/flow_01/save
Content-Type: application/json
If-Match: "7"
Origin: https://soullink-host.example:18080
```

```json
{
  "confirmationToken": "one-time-token-bound-to-payload",
  "definition": {
    "name": "示例自动化",
    "accountId": "acc_a2",
    "nodes": [ { "id": "n_schedule", "kind": "Condition", "type": "TrigInterval", "x": 100, "y": 100, "logic": "LD", "negated": false, "branchMode": "Parallel", "params": { "intervalSeconds": "900", "user": "EXAMPLE\\A2" } } ],
    "edges": []
  }
}
```

> 上例刻意展示一个校验失败的候选（条件未连动作），用于说明响应格式；真实保存应先修正后再提交。

```json
{
  "ok": false,
  "code": "FLOW_INVALID",
  "error": "流程校验未通过",
  "issues": [
    { "severity": "warning", "nodeId": "n_schedule", "paramKey": "", "message": "条件「定时·间隔」后面还没有连接动作" },
    { "severity": "error", "nodeId": null, "paramKey": "", "message": "还没有动作或「当天完成」模块" }
  ]
}
```

版本冲突返回 412，并只返回当前 revision/当前定义供重新合并：

```json
{
  "ok": false,
  "code": "STALE_FLOW",
  "error": "流程已在其他端修改；请比较后重新保存",
  "currentRevision": 8
}
```

创建成功用 201；保存成功用 200 + 新 ETag；未登录 401、有 read 但无 edit 403、大小超限 413、校验失败 422。不要在失败时仍返回 HTTP 200 让前端猜。

### 3.6 路由与鉴权落地方式

最小破坏做法不是再复制十段 `if`，而是将 POST 前置白名单与处理表一起改成显式路由项：

```text
路由项 = 方法 + 路径模板 + read/edit 权限 + handler
       → 先统一认证/Origin/大小限制
       → 再检查权限
       → 最后进入 handler
```

若暂不重构路由，至少把上表全部 POST 加入 `WebServer.cs` 的 POST 白名单；只加末尾 `else if` 无效，因为请求早已 404。推荐新建 `WebAuth.Require(ctx, "read"/"edit")`，内部保留现有 `IsAuthorized` 的 Cookie 兼容，再增加分级校验；不要把代理的 CheckToken 或 `WebPassword` 直接复用为流程编辑凭证。

---

## 4. 自动化流程网页编辑

### 4.1 数据模型与可编辑边界

`AutomationFlow` 同时装有两类数据：

| 类别 | 字段示例 | 网页如何处理 |
|---|---|---|
| 定义 | Id、Name、AccountId、Enabled、Nodes、Edges、EngineVersion | 仅白名单 DTO；Id/EngineVersion 服务端拥有，AccountId 创建后首期不可改 |
| 运行状态 | RunCount、RunComplete、PlcPower、ActionResults、WaitingActions、DelayUntil、ScheduleFireCount 等 | 只读摘要；**编辑保存不得清空或接受客户端提交** |

节点使用 `FlowNodeCatalog` 作为唯一目录，参数类型包括文本、数值、布尔、账号/动作/组选择器、按键捕获、图片坐标选择等。连线规则为单出口 PLC 图：禁自连/环/重复线，END 不得出边；触点的 LD/AND/OR、NOT、并行/选择分支都要保留。

网页 DTO 不序列化完整 `AutomationFlow`，尤其禁止客户端通过 `RunComplete=false`、伪造 ActionResults 等方式借编辑器改变运行历史。Enabled/Paused 最好拆为独立 state 命令，避免“只挪一个节点”却意外启用流程。

### 4.2 三档方案对比

| 档位 | 功能 | 实现成本（相对估算） | 风险/限制 | 建议 |
|---|---|---|---|---|
| A. 只读查看 | 流程列表、只读 SVG/DOM 图、参数、校验结果、运行摘要 | **低~中**：目录/详情 API + 简单布局渲染；无写事务 | 只能看不能改；运行态刷新仍须手动 | **P1 必做**，先验证数据契约 |
| B. JSON 文本编辑 | 定义编辑器、格式化、schema/目录提示、服务端校验、差异预览、备份回滚 | **中**：关键是并发/原子保存/安全，不是 textarea | 对普通用户不友好；JSON 合法不等于图合法；不能信任前端校验 | **P2 推荐**，以完整事务为基础 |
| C. 可视化拖拽画布 | 节点拖动、连线、删除、参数面板、快捷键、坐标/按键选择器、缩放 | **高**：交互、触控、布局、冲突 diff、可访问性都要重做 | 图义偏差、误启用、触摸误操作、双端体验分叉；成本远高于“画 SVG” | **P3 再做**，复用 B 的全部 API，不另建保存通道 |

可视化若实施，推荐原生 SVG 连线 + HTML 节点，按 `FlowNodeCatalog` 驱动参数表单；拖拽只改本地草稿，点保存时一次性提交，不因每次 mousemove 发 API。无需引入重型图编辑器或前端框架。

### 4.3 双端并发：谁赢？

**裁决：先合法提交者赢；另一端明确失败并合并，绝不静默覆盖。**

1. 每次定义成功保存，该流程 `editRevision + 1`；运行统计、灯态、扫描游标变化不加版本。
2. WPF 和网页打开编辑器时记录基准 revision；二者保存都通过同一个 `FlowEditService`。
3. 保存时检查 `If-Match`/WPF 基准版本。相同才继续，不同返回 412/显示冲突。
4. 冲突界面提供“查看当前版本 / 保留我的草稿 / 下载或复制差异 / 基于新版本重新校验”，没有“强制覆盖”默认按钮。
5. 不采用“WPF 永远赢”或“网页永远赢”。节点级自动合并可后做，首期整份定义乐观锁更安全。

**WPF 前置改造不可省：** 当前 `FlowEditorWindow` 持有共享 `_flow`，画布拖动直接改 `Nodes/X/Y`，而调度器也枚举同一 `App.Config.Automations`。也就是说，未点保存的图可能已经影响运行。必须先让 WPF 编辑**分离的草稿克隆**，保存时才进入统一事务；否则网页加版本号也无法解决中途变更和共享对象竞争。

### 4.4 校验如何复用

- 服务端直接调用现有 `FlowValidator.Validate(candidateClone, App.Config)`；网页 JavaScript 不重新实现业务规则。
- `FlowGraphRules` 用于画布即时交互提示；最终仍以服务端完整校验为准。
- `FlowValidator` 内的 `EnsurePlcModel()` 会修改对象，因此只能在候选克隆上运行，不能拿 live flow 做“试试校验”。
- 返回结构化 Issue：`severity/nodeId/paramKey/target/message`，前端定位字段；warning 可显示，但 HasError 一律拒绝保存。
- 校验同时检查账号存在、BGI 路径、绑定日志依赖、快捷动作存在、时间/数字/正则、重复/悬空连线、环、END 数量等。
- 正则还需补运行时超时/长度限制，避免恶意 ReDoS；前端语法着色不是安全边界。

### 4.5 原子写盘与热加载

```text
候选 JSON
  │ 反序列化白名单 DTO；拒绝未知字段/重复 Id/超预算
  ▼
草稿克隆 → EnsurePlcModel（仅克隆）→ FlowValidator
  │
  ▼
进入该流程编辑门（调度器与 WPF/Web 共用）
  │ 再检查 editRevision；若正在扫描则等待完成或明确拒绝
  ▼
备份旧定义 → 只替换 live flow 的 Name/Nodes/Edges 等定义字段
  │ 保留运行态与对象身份
  ▼
同目录临时文件 flush → File.Replace 原子替换 config.json
  │ 失败：恢复内存旧定义，返回失败；不得宣称保存成功
  ▼
写审计 → FlowDefinitionChanged → WPF/调度器使用新定义
```

- 不需要从磁盘“重启式热加载”：主程序本来就在运行，提交内存事务后即生效，再发变更事件让打开的页面/编辑器感知。
- 调度器在编辑门内开始一次扫描，编辑事务不得与正在执行的流程交叉。首期可以返回 `FLOW_BUSY`，提示等待/先暂停；不要强行取消正在执行的动作。
- `FlowRunner` 目前只有流程级防重入，而且运行中会按状态差异 `ConfigStore.Save(cfg)`。统一事务必须兼容这个现存写入者，不能只给网页 handler 加一把私有锁。
- `ConfigStore.Save` 已采用同目录 tmp + File.Replace，但其 catch 中存在直接 `File.Copy(..., overwrite:true)` 降级。流程编辑要求严格原子性时不能沿用这个降级：替换失败就保留旧文件并失败返回。
- 保留 CreatedAt、运行锁存、定时游标、ActionResults；不得用反序列化后的整对象直接替换 live flow。

### 4.6 备份、回滚与写坏恢复

- 每次提交前将**旧流程定义 + revision + 时间 + 来源**写到配置同级的 `flow-backups/{flowId}/`；这是用户数据，不进仓库、不进 dist、不进发布包。
- 每个流程保留最近 20 版且至少 30 天，在后续保存时顺带清理，不新增清理定时器；元数据列表供网页查看。
- 恢复走正常 `validate → If-Match → backup → atomic save` 事务，生成 revision N+1；不允许网页直接上传覆盖 config.json。
- 若候选写坏：保存前已拒绝；若保存 IO 失败：恢复内存旧定义，旧磁盘配置不动；若用户逻辑写错但结构合法：选择历史 revision 恢复。
- 备份不是运行状态回滚机。恢复旧定义不能撤销已经启动的进程、键鼠或游戏操作，界面必须明确说明。
- 启动时仍有 `ConfigStore.Load` 的解析/迁移兜底；`config-before-flow-migration.json` 不能替代逐流程历史。

---

## 5. 前端扩展策略

### 5.1 单文件 vs 模块

| 方案 | 优点 | 代价/风险 | 结论 |
|---|---|---|---|
| 继续单文件 | 现有同步链最简单；一个文件即可兜底 | 全局 const/let 冲突、重复 id、任一语法错误整页失效；BGI + 流程后继续膨胀；评审困难 | 仅适合当前远程查看页，不再承载新功能 |
| 原生 ES Module + 简单同步检查 | 浏览器原生支持；作用域隔离；可按页懒加载；无框架/运行时依赖 | 需静态资源路由、manifest、模块语法和浏览器测试；仍是多文件 IO | **推荐** |
| 框架 + 完整前端构建 | 生态和组件多 | 引入 npm 供应链、构建产物、体积与维护成本；远超当前需求 | 不推荐 |

### 5.2 推荐目录

```text
docs/web/
  index.html                    # 纯 HTML 骨架，无内联业务脚本
  styles/
    tokens.css                 # 现有亮暗主题变量
    shell.css                  # 顶栏、导航、通用控件
    accounts.css               # 现有账号/画面/录像
    bgi.css                    # BGI 摘要、统计、日志
    flows.css                  # 流程列表/只读图/编辑器
  js/
    main.js                    # 唯一入口，初始化 shell/router
    core/
      api.js                   # fetch、错误、ETag、AbortController
      dom.js                   # 安全 DOM 构造，不拼未转义 innerHTML
      router.js                # hash 路由，兼容 ?view=账号Id
      theme.js                 # 主题初始化失败不阻断其它模块
    features/accounts/
      page.js
      live.js                  # 仅保留现有显式拉帧循环
      replay.js
    features/bgi/
      page.js                  # 点击后动态 import 和取数
      summary.js
      logs.js                  # afterSeq，无 setInterval
    features/flows/
      page.js
      read-only-graph.js       # P1
      editor-json.js           # P2
      editor-graph.js          # P3 才出现
```

主站增加严格 `/assets/...` 静态路由：只允许映射到 exe 同目录的 web 资源根，规范化后再次检查根目录，扩展名白名单 `.js/.css/.map/.svg`，正确 MIME + nosniff；不能复用 `/file/`，也不能允许任意磁盘路径。

`sync-webpages.ps1` 扩展为：

1. 校验 manifest 与 import 闭包，运行 `node --check`（若构建环境提供 Node）及重复 id/元素引用检查。
2. 复制整个受控资源目录到运行目录；**不复制 src 外的任意文件**。
3. 生成 `WebPages.cs` 的资源字典/manifest 兜底，不再只靠一个巨大 raw string；注意 binary/文本编码与缓存。
4. 真正的 headless 浏览器打开主页面和每个 hash 页，收集 console/pageerror/404；不能只 dump DOM 看骨架。

运行时磁盘读取的开发便利保留；内置资源只作为资源缺失时的版本一致兜底。部署仍需同步，不能宣称改源文件后线上自动更新。

### 5.3 页面组织与按需策略

- 顶栏增加“账号 / BGI 数据 / 自动化”标签，hash 路由支持刷新和分享；`?view=` 独立画面模式保持兼容，且不加载 BGI/流程模块。
- BGI/flows 页面首次激活才动态 import；切页只隐藏 DOM，不触发后台取数。
- 所有数据区有“刷新”按钮与最后更新时间，不伪装成实时。
- 编辑器使用本地草稿和显式保存；不要自动保存、不要鼠标移动写 API。
- 默认使用 `textContent`/DOM 节点渲染 BGI 日志和流程参数，避免日志内容变成 HTML 注入。

---

## 6. 安全与权限模型（阻塞项）

### 6.1 当前风险裁决

| 现状 | 证据/影响 | 对流程编辑的裁决 |
|---|---|---|
| WebPassword 为空即不鉴权 | 主站 IsAuthorized 直接 true；代理 CheckToken 空 token 也 true | **禁止开启编辑**，不得只靠页面隐藏按钮 |
| 主站/代理使用 HTTP | 令牌、Cookie、流程参数可能被同链路窃听/篡改 | 非 loopback 编辑前必须提供 HTTPS 或受控安全通道；公网暴露不支持 |
| 代理 CORS 为 `*` | `AgentWebServer` 允许任意 Origin，并允许 token/content-type 头 | 改为精确 Origin 白名单；主站写 API 同时校验 Origin/CSRF |
| 主站 Cookie 一年有效，未标 HttpOnly | 凭据长期暴露，XSS 可读取 | read/edit 会话分离；HttpOnly；编辑短时、可撤销；HTTPS 时 Secure |
| 流程能启动程序、杀进程、模拟输入、执行 BGI 动作 | 编辑不是普通配置修改，可能形成宿主脚本/命令执行 | 默认 `WebFlowEditEnabled=false`，独立高危授权、确认与审计 |

主站当前未设置 Access-Control-Allow-Origin；需要收窄的 `*` 实际在代理。修复时两边都要做：主站继续保持不授予跨源读取，并对所有写请求做 Origin/CSRF 检查；代理只允许明确配置的主站 Origin。**不能靠 CORS 代替鉴权，CORS 也不阻止非浏览器直接 POST。**

### 6.2 令牌从哪里来、怎么轮换

- 在本机 WPF 安全设置页用密码学 RNG 生成两个独立至少 256-bit 随机秘密：`read` 与 `edit`。编辑秘密只在首次生成/轮换时展示一次，之后不可通过网页 API 读取。
- 服务端保存哈希或受 DPAPI 等本机保护的凭据，不写明文进日志；编辑权限另需独立口令做 step-up。不得把 `WebAuthToken`、`WebPassword` 或代理 URL token 当编辑令牌。
- 主站 Cookie 可维持现有登录体验，但 read/edit 使用不同会话；编辑会话建议 10~15 分钟、只存内存、绑定权限代次。`HttpOnly; SameSite=Strict; Path=/api`；HTTPS 下加 `Secure`。
- 轮换：本机一键“轮换网页只读凭据”“撤销全部编辑会话”；修改主口令同时撤销全部。编辑凭据带 `generation`，旧 generation 立即 401/403；无需等待过期。
- 流程 API 接受认证请求头/安全 Cookie，**拒绝 `?token=`**。媒体 URL 的长期查询串 token 后续改短时媒体票据，不扩大给 JSON/流程 API。
- 未设置非空主口令/编辑口令、未显式开启编辑开关时，写接口 fail closed。read 旧行为可暂时兼容，但编辑绝不继承“空=匿名”。

### 6.3 CORS 与请求来源收窄

- 默认同源：主站只接受自身受信任 Origin 的写请求；无 Origin 的非浏览器客户端必须走显式 token 客户端通道，不能借 Cookie 放行。
- 代理通过主程序下发/配置 `SOULLINK_WEB_ORIGINS` 等精确白名单；仅当 Origin 完整匹配 scheme + host + port 才回写该 Origin，并加 `Vary: Origin`。
- 禁止 `*`、`null`、后缀匹配、根据 Host 头临时拼一个信任 Origin；多 LAN 地址要显式配置。
- `Access-Control-Allow-Methods` 只列该端点实际方法；headers 只留 Content-Type 与必要认证头；不开启跨域 Cookie credentials。
- 所有主站 POST 要求 `Content-Type: application/json`、同源 Origin、CSRF/确认凭证；这样即使第三方页面能发简单请求，也不能通过服务端检查。
- 响应加 CSP（至少 `object-src 'none'; base-uri 'none'; frame-ancestors 'none'`，模块脚本仅 self）、`X-Content-Type-Options: nosniff`、`Referrer-Policy: no-referrer`。

### 6.4 权限分层

| 权限 | 允许 | 禁止 |
|---|---|---|
| viewer/read | 现有画面/录像（按现有授权）、BGI 摘要/尾窗、流程只读 | 所有写操作、获取编辑秘密、备份正文下载 |
| editor/edit | validate、创建/保存草稿定义、查看校验和历史元数据 | 匿名使用、绕过 revision、直接写 config、默认启用高危流程 |
| operator/step-up | 启用/恢复/删除、包含启动程序/结束进程/键鼠等危险节点的激活 | 无确认执行、跨账号越权 |

再按账号 scope 限制可编辑流程，不能只靠客户端传 accountId。文件路径/进程类节点建议再分单独高危授权；最低限度也应在危险差异预览中逐项标红。

### 6.5 二次确认必须在后端

前端弹窗只是体验，不能证明权限。服务端方案：

1. `POST /api/flows/validate` 得到结构化结果与候选 SHA-256。
2. 前端展示名称/节点/连线/危险动作差异，用户确认；需要 step-up 时重新输入编辑口令。
3. `POST /api/flow-confirmations` 提交完整候选摘要，服务端生成随机一次性 confirmationId/token，绑定编辑会话、方法、flowId、baseRevision、payloadHash，60 秒过期。
4. save/state/delete/restore 携带该 token；服务端重新计算 payloadHash、检查版本和权限，使用后立即作废。payload 改一个字节也必须重新确认。
5. 启用流程、恢复、删除及新增/修改危险节点必须 step-up；仅保存禁用草稿也至少要有一次性确认，不能只信任 `confirm:true` 字段。

### 6.6 审计日志写哪里、记什么

- 写到用户配置目录下受控的 `logs/web-audit-yyyyMMdd.jsonl`（以实际 ConfigPath 所在用户数据根为准），绝不写仓库或 dist。
- JSONL 追加写并串行化；保存编辑时若审计不可用，编辑事务应失败关闭，不能无审计成功。
- 字段：UTC、requestId、来源 IP、凭据 Id 的不可逆指纹、权限、action、accountId/flowId、base/new revision、payload SHA-256、校验结果、结果码、失败原因、确认 Id 指纹。
- 不记录 token、口令、Cookie、完整配置正文、程序参数或日志敏感正文；需要内容时从受控备份读取。
- 每条日志限长，按日保留 30~90 天，保存时顺带清理；不为清理新增定时器。审计能追责，不等于防宿主管理员篡改，界面不要夸大。

**最终门槛：P0 全部完成并通过 401/403/CSRF/轮换/审计测试前，流程写接口即使编译进去也保持服务端关闭；推荐第一期只在 loopback/HTTPS 受控环境开启。**

---

## 7. 分期与验收

| 阶段 | 交付 | 前置/退出条件 |
|---|---|---|
| P0 安全与事务底座 | read/edit 分级、编辑默认关、Origin/CSRF、确认/审计；WPF 草稿隔离；统一 FlowEditService/版本/备份 | 未做 P0 不上任何写 API；并发与 IO 失败测试通过 |
| P1 只读能力 | 模块化前端；BGI summary/today/logs/catalog；流程列表/只读图；全部按需 | 无 BGI 轮询/推送；隐藏页零新增请求；未绑定账号零新增 IO；304/seq reset 正确 |
| P2 JSON 编辑 | schema 表单辅助、validate、diff、If-Match 保存、启停/删除/恢复 | 双端同 revision；412 不覆盖；原子保存失败可恢复；权限与审计全通过 |
| 可选 P3 可视化 | SVG+DOM 拖拽、连线、参数面板、触控与快捷键 | 复用 P2 API；无额外保存通道；无 mousemove 网络写；双端图义一致 |

### 必测清单

- POST 白名单：每个新写路由实际到 handler；未注册仍 404，未授权 401、read-only 403。
- GET/ETag：未变 304；账号隔离；日志 limit 上限、残行、跨天/重启 reset；无原始大文件扫描。
- 校验：未知字段、重复 Id、环、孤儿、END、非法正则、超长 body；validate 不改 live flow。
- 并发：WPF/Web 同版本保存只有一个成功；运行统计不写 editRevision；编辑中调度不取半成品。
- 崩溃/磁盘：tmp 写失败、Replace 失败、备份失败、审计失败均不能假成功；恢复不重置 RunComplete/定时游标。
- 安全：空口令/明文非 loopback 下编辑始终关闭；跨 Origin、表单 POST、重放 confirmation、过期 edit token 均失败。
- 前端：逐模块语法检查、所有路由真实渲染、console 无错误、缺失元素不拖垮整页；改源文件后必须走同步流程。

---

## 8. 明确不做与未决项

- 不把 BGI 日志、原始配置文件或流程文件搬到代理服务；不让网页请求触发整日日志扫描。
- 不在 P1/P2 引入前端框架、WebSocket/SSE、自动保存、强制覆盖或多端复杂自动合并。
- 不提供任意文件下载、任意程序路径上传或直接 config.json 写入接口。
- 当前没有各账号真实组数/物品种类/名称数量的统计，API 的响应预算与分页仍需用脱敏生产样本校准。
- 现有 WPF 编辑器与 FlowRunner 的共享对象写入是并发底座的前置改造，不是网页单方面能补救的问题。
- HTTPS 的局域网证书分发、账号级授权 UI、危险节点的细粒度策略需要产品配置决策；在这些决策落地前维持编辑默认关闭。

---

## 9. 主要代码依据

- `Services/BgiDataCenter.cs`：配置/名称缓存、stamp、Refresh 与 Clone。
- `Services/BgiLogCenter.cs`、`Services/BgiLogSnapshot.cs`：唯一日志读取、增量游标、1 秒合并、尾窗预算、快照版本。
- `Services/BgiLogWatcher.cs`：日志来源、读取预算、历史大日志成本；`Services/BgiStatusParser.cs`：结构化状态字段。
- `Services/BgiTaskPreparer.cs`：高危停止/清场控制边界，不作为数据 API。
- `Models/FlowModels.cs`、`FlowNodeCatalog.cs`、`FlowGraphRules.cs`：定义与运行态混合模型、目录、图规则。
- `Services/FlowValidator.cs`、`FlowRunner.cs`、`ScheduleRunner.cs`、`ConfigStore.cs`：校验副作用、运行写配置、调度共享对象、原子保存现状。
- `Controls/FlowCanvas.cs`、`Views/FlowEditorWindow.xaml.cs`：WPF 拖拽直接改共享对象、显式保存与预览。
- `Services/WebServer.cs`、`Relay/AgentWebServer.cs`：主站 Cookie/POST 白名单、代理 CheckToken/CORS。
- `docs/web-index.html`、`scripts/sync-webpages.ps1`：单文件全局脚本、按需取数、运行时磁盘读取与同步链。
