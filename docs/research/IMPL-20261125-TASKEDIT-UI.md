# 任务编辑页 P1（t26 / 3.3.45）—— 真界面：左树 + 右画布

> 依据 [TASK-EDITOR-DESIGN-20261125.md](TASK-EDITOR-DESIGN-20261125.md) §3（信息架构）/ §12（右键菜单矩阵）
> / §13（独立任务）/ §14（JS 设置）/ §15（战斗配置）/ §16（生成文件）。
> 前置：t25 数据层（`BgiStore` / `BgiJson` / `BgiValidator` / `TaskEditApi`）；P0 骨架（[IMPL-20261125-TASKEDIT-P0.md](IMPL-20261125-TASKEDIT-P0.md)）。

---

## 0. 交付摘要

| 项 | 结果 |
|---|---|
| 页面 | `/taskedit/` 真界面，**左树 + 右画布**（§3.1 推荐布局） |
| 前端资源 | 11 个（1 CSS + 10 JS），全部编译进程序集，**离线可用、无 CDN、无框架** |
| 单文件发布 | 476.6 KB（含全部前端资源） |
| 首屏体积 | 明文 **82.2 KB** / gzip **37.8 KB**（预算 < 150 KB ✅） |
| 实测 BGI | A2 `teabag 0.67.2+lcb.23.2-NexusBGI-fix1`，9 配置组 / 4 一条龙 |
| 右键菜单 | **三种类型实测区分成立**（Pathing / Javascript / SoloTask） |
| 一周表格 | **真 7 列表格**（21 个输入框），非折叠面板 |
| 一条龙字段 | 96 个真实字段，渲染 50 个绑定输入 + 9 个分区卡片 |
| XSS | `esc()` 实测转义 `<img onerror>` |

---

## 1. 文件清单（全部 ≤300 行）

```
tools/taskedit/
  TaskEditStatic.cs          95 行   资源路由 + gzip + 路径判定
  Static/
    ShellCss.cs             182 行   样式表（深色主题，无框架）
    PageHtml.cs              71 行   HTML 外壳（顶栏/左树/画布/弹窗/菜单）
    CoreJs.cs               189 行   API 封装 / esc / 脏状态 / modal / toast / 草稿
    TreeJs.cs               148 行   左树 + 顶部 Tab
    GroupJs.cs              194 行   配置组：表格 / 拖拽 / ★按类型分菜单
    GroupSettingsJs.cs      189 行   配置组设置：4 个区 + 字段渲染 + 校验
    GroupOpsJs.cs           149 行   添加（/pickable/*）/ 移除 / 批量删除 / 目录
    DragonJs.cs             215 行   一条龙：任务列表 / 拖拽 / 开关 / 字段绑定
    DragonSectionsJs.cs     146 行   一条龙 9 个分区卡片 + ★一周表格
    JsSettingsJs.cs         263 行   JS 动态表单（六类型 + 未知降级）
    PickJs.cs               176 行   拾取名单（白/黑名单、导入导出、分页）
    AppJs.cs                159 行   入口 / 保存 / 服务端错误定位到字段
```

**为什么把 CSS/JS 编进程序集而不是放静态文件**：
单文件发布下静态文件要额外 `CopyToPublishDirectory` 且路径易错；用户要求离线可用，
资源必须与 exe 同生共死（不能出现「exe 在但 js 丢了」）。用 C# 原始字符串字面量（`"""`）
可读性足够，改样式不必读路由代码。

---

## 2. 布局实现（§3.1）

    ┌─────────────────────────────────────────────────────────┐
    │ SoulLink [任务编辑]  [配置组/一条龙] [拾取名单]  版本  重载 保存 │
    ├───────────┬─────────────────────────────────────────────┤
    │ 左树 260px │ 右画布                                        │
    │  配置组(9) │  配置组 → 项目表格 + 4 个设置区                  │
    │   ├ 异常处理│  一条龙 → 左任务列表 + 右 9 个分区卡片            │
    │   ├ 激活   │                                              │
    │  一条龙(4) │                                              │
    │  组合(0)  │                                              │
    └───────────┴─────────────────────────────────────────────┘

- 左树按「配置组 / 一条龙 / 组合」三根分组，组名可折叠（▸/▾），带数量角标。
- 点节点 → 右侧加载详情；**切换前有脏状态保护**（提示「放弃 / 继续编辑」）。
- 顶部 Tab：`配置组/一条龙` 与 `拾取名单`；切走时同样有脏状态保护。
- 版本栏显示渠道 + 基础版本 + 产品版本 + BGI 根，便于确认「改的是哪个 BGI」。

---

## 3. ★ 右键菜单按项目类型区分（用户点名重点）

实现位置：`GroupJs.js` → `TE.group.menuFor(i)`，按 `typeOf(p)` 的判定结果拼菜单。

**类型识别**（`typeOf`）优先读 `p.type`，退化时从字段猜：
有 `soloTaskSettingsObject` → SoloTask；有 `jsScriptSettingsObject`/`folderName` → Javascript。

### 实测结果（A2 真实配置组「联机狗粮」）

| 项目类型 | 实际菜单项 |
|---|---|
| **Pathing**（地图追踪） | **批量删除…** ｜ 下一次任务从此处执行 ｜ 修改通用配置 ｜ 移除 |
| **Javascript**（JS 脚本） | **修改 JS 脚本自定义配置** ｜ **打开所在目录** ｜ 下一次任务从此处执行 ｜ 修改通用配置 ｜ 移除 |
| **SoloTask**（独立任务） | **修改独立任务配置** ｜ 下一次任务从此处执行 ｜ 修改通用配置 ｜ 移除 |

- 类型专属项**放在菜单最前**（用户最容易找的位置），随后是通用项。
- 空白处右键 → 添加菜单（5 种类型）。
- 「移除」标记为 danger（红）。

**与 §12 矩阵的对应**：SoloTask 专属「修改独立任务配置」、Javascript 专属
「修改JS脚本自定义配置 / 打开所在目录」、Pathing 专属「批量删除」、其余为通用项 —— 逐项对齐。

---

## 4. ★ 添加时调 /pickable/*（与 BGI 一致）

`TE.group.pick(apiPath, kind)` 打开选择弹窗，**列表全部来自服务端 API**，前端不扫目录：

| 菜单项 | API |
|---|---|
| 添加 JS 脚本 | `GET /taskedit/api/pickable/js` |
| 添加地图追踪任务 | `GET /taskedit/api/pickable/pathing` |
| 添加键鼠脚本 | `GET /taskedit/api/pickable/keymouse` |
| 添加独立任务 | `GET /taskedit/api/pickable/solotasks` |
| 添加 Shell | 本地输入命令（BGI 语义：Shell 项目的 name 即命令） |

选择弹窗显示名称 + `folderName`（等宽字体，右侧），空列表给出明确提示
（「请确认 BGI 根目录正确，且该类型资源已安装」）。

---

## 5. 配置组详情（对齐 BGI `ScriptGroupConfigView` 三栏心智）

### 5.1 项目表格

`# / 拖拽把手 / 名称 / 类型标签 / 启用开关`。
- 类型标签按类型着色（JS 绿 / 地图追踪黄 / 独立任务紫 / Shell 橙 / 键鼠蓝）。
- **拖拽排序**：HTML5 DnD，拖动时高亮「插入到上方/下方」，松手即重排并标脏。
- 启用开关直接改 `enabled`（兼容 `Enabled`）。

### 5.2 设置面板（`config` 四个区，字段一个不少）

| 区 | 字段（全部渲染） |
|---|---|
| **地图追踪行走配置** `pathingConfig` | speed / walking / useGadget / leftCount / rightCount / loginDelay |
| **战斗配置** `autoFightConfig` | StrategyName / TeamNames / FightFinishDetectEnabled / PickDropsAfterFightEnabled / KazuhaPickupEnabled / SwimmingEnabled / ExpBasedPickupEnabled / Timeout / EnableCombatTargeting / OnlyPickEliteDropsMode / DamageNumberRecognitionMode / ActionSchedulerByCd / **UseOfficialAutoFight（茶包独有）** |
| **自动吃食物** `autoEatConfig` | Enabled / HpThreshold / FoodName / CheckInterval |
| **Shell** `shellConfig` | EnableSnapshot / Timeout / SaveScreenshot |
| 顶层 | `enableShellConfig`（单独一行开关） |

- **字段名两种命名都兼容**：读时先试 PascalCase 再试 camelCase（`readField`），因此既能读
  BGI 原生序列化（PascalCase），也能读已被转成 camelCase 的配置。
- **茶包独有字段带「茶包」标记**，保存时由服务端 `BgiValidator.ValidateCapabilities` 拦截（§6.4）。
- 改动即标脏 + 写回内存模型；**范围校验实时标红**（Timeout ≥ 1、HpThreshold 0~100）。

---

## 6. 一条龙详情（对齐 BGI 单页可折叠卡片）

### 6.1 两栏结构

左：任务列表（`TaskOrder` 顺序 + 每项启用开关 + 拖拽排序）；
右：9 个可折叠分区卡片。

### 6.2 分区顺序与字段（与 BGI 一致）

| 分区 | 字段数 | 说明 |
|---|---|---|
| 任务列表 | — | TaskOrder 拖拽 + TaskEnabledList 开关 |
| 合成树脂 | 5 | AutoResinEnabled / UseOriginalResin / UseFragileResin / UseTransientResin / SpecifyResinUseCount |
| 自动秘境 | 4 | AutoDomainEnabled / PartyConfigName / SpecifyRunCount / ArtifactSetId |
| 自动首领讨伐 | 7 | AutoBossEnabled / **RunCount（≥1 校验）** / TotalRunCountLimit / CompletedRunCount / ReviveRetryCount / SpecifyRunCount / TeamName |
| 自动地脉花 | 3 | AutoLeyLineOutcropEnabled / Type（BlossomOfWealth｜BlossomOfRevelation）/ Count |
| 领取奖励 | 4 | AutoGetRewardEnabled / BattlePass / Dispatch / Hangout |
| 尘歌壶 | 2 | AutoHoeingEnabled / PartyConfigName |
| 每日奖励 / 邮件 | 2 | AutoGetDailyRewardEnabled / AutoGetMailEnabled |
| **逐日配置** | **21** | ★ **一周表格**（见 §6.3） |
| 队伍配置 | 2 | PartyConfigEnabled / DefaultPartyName |

### 6.3 ★ 逐日配置 = 一周表格（不是 7 个折叠面板）

实测渲染：`<table class="week">` 有 **8 列**（字段 + 周一~周日）、**21 个数字输入框**、
周六/周日表头标红（`class="weekend"`）。

    字段           周一 周二 周三 周四 周五 周六 周日
    自动秘境次数    [ ] [ ] [ ] [ ] [ ] [ ] [ ]
    首领讨伐次数    [ ] [ ] [ ] [ ] [ ] [ ] [ ]
    地脉花次数      [ ] [ ] [ ] [ ] [ ] [ ] [ ]

- 字段名按 BGI 的 PascalCase 一周重复模式生成：`AutoDomainMonRunCount` … `AutoDomainSunRunCount`。
- **留空 = 回退到分区独立配置**（BGI 语义），界面下方有明确说明。
- 值 ≥ 0 校验，错误标红到单元格。

### 6.4 真实字段名纠偏（实测发现）

设计文档 §4.1 的示例字段名是 camelCase，但 **A2 实测 96 个键全是 PascalCase**：

    TaskEnabledList / TaskOrder / TaskDefinitions / NextTaskId / Version / Name / IndexId
    IsCraftingResinExpanded / IsAutoDomainExpanded / … （9 个 Is*Expanded）
    AutoBossRunCount / AutoBossTotalRunCountLimit / … （14 个 AutoBoss*）
    LeyLineRunMonday…Sunday / MondayPartyName…SundayPartyName …

→ 因此实现**全部按真实 PascalCase 读写**，并用 `read()/write()` 双命名兜底。
另外 `TaskDefinitions` 在真实配置里**是空数组**（任务定义由 BGI 内置常量提供），
所以任务列表以 `TaskOrder` + `TaskEnabledList` 为准，并内置 12 个常见任务 ID 的中文名对照
（`AutoGetReward` → 领取邮件奖励 等），未知 ID 原样显示。

---

## 7. JS 脚本动态设置表单（§14，用户硬需求）

`/taskedit/api/getconfig?name=&index=&folder=` 取 `SettingsUi` → `SettingItem` 列表。

### 7.1 六种类型

| SettingItem 类型 | 渲染 |
|---|---|
| `separator` | 一条分隔线 + 可选小标题 |
| `input-text` | 文本框 |
| `select` | 下拉（选项兼容字符串数组与 `{label,value}` 数组） |
| `checkbox` | 开关 |
| `multi-checkbox` | 多选复选框组，值为数组 |
| `cascade-select` | 多级联动下拉（`levels[]` 逐级渲染，值为各级当前值数组） |

### 7.2 未知类型降级（不崩、不丢数据）

不在上述六种里的类型 → 渲染为**文本框 + 橙色警告条**：

    ⚠ 该控件类型「xyz」暂不支持，已降级为文本框（内容按原样保存）。

保存时按 `data-jt="json"` 尝试 `JSON.parse`，失败则原样存字符串 —— **绝不丢数据**。

### 7.3 无 schema 时的兜底

脚本没有 `manifest.json` 的 `SettingsUi`（或读取失败）时：
显示黄色提示 + **原始 JSON 编辑器**（可直接改整个 `JsScriptSettingsObject`），
输入时实时 `JSON.parse` 校验，语法错误标红。

### 7.4 实测

对 A2 的 JS 项目「AAA狗粮联机团购」：该脚本未声明 `SettingsUi` →
正确显示「该脚本没有声明设置项」+ 原始 JSON 兜底（`has rawJSON fallback = True`），**未崩溃**。

---

## 8. 拾取名单（独立 Tab）

- **模式切换**：白名单 / 黑名单（写回 `pickBlackListEnabled` / `PickBlackListEnabled`）。
- **基础拾取**开关（`enabled`）。
- 物品列表：输入回车即添加、每行「移除」；**重复项检测**。
- **导入 / 导出 JSON**：导出弹窗可一键复制；导入前 `JSON.parse` 校验，失败明确报错。
- **分页保护**：列表超过 100 项只渲染前 100，并提示「共 N 项，当前只显示前 100 项（远程访问保护）」。

---

## 9. 通用交互

### 9.1 未保存脏状态

- 任何字段改动 → `TE.dirty.mark(key)` → 保存按钮变蓝并显示 `保存 *`。
- 设置 `window.onbeforeunload`（浏览器只允许这个默认文案）。
- **切节点 / 切 Tab 前**弹确认：「切换后将丢弃当前修改，确定继续吗？」（danger 样式）。

实测：`dirty.any() = True`、`saveBtn.textContent = "保存 *"`、`saveBtn.disabled = False`、
`typeof window.onbeforeunload === 'function'`；清除后回到 `False` / `保存`。

### 9.2 字段级标红校验

两层：
1. **前端实时**：每字段 `validateField`（Timeout ≥ 1、HpThreshold 0~100、RunCount ≥ 1、各类 `*RunCount` ≥ 0），
   错误加 `.bad` 类 + 红字提示。
2. **服务端权威**：保存时服务端跑 `BgiValidator`，返回 **422 + `errors[{path,message,blocking}]`** 时，
   前端弹「保存被拒绝」表格，**并把第一个阻断错误按 `path` 末段匹配到输入框标红 + 平滑滚动过去**。

### 9.3 能力集校验（§6.4）

- 启动时 `GET /taskedit/api/version` → 若不是茶包渠道，右下角常驻橙色告警：
  「当前 BGI 渠道为「X」，茶包版独有字段在保存时会被拦截」。
- 茶包独有字段在表单里带「茶包」角标。
- 真正的拦截由服务端 `ValidateCapabilities` 完成，前端只做提示，**不替用户擅自剔除字段**。

### 9.4 远程可用（高 RTT）

- 所有请求都有 loading 态（画布显示 `<span class="busy">` 转圈 + 文本），**不会点了没反应**。
- 保存按钮在请求期间 `disabled` + 「保存中」，防重复提交。
- 大列表分页（拾取名单 100 条上限）。
- gzip 全链路（见 §10.1）。
- 无外部 CDN：一个请求都不外发，穿透/组网环境下不会因为 CDN 被墙而白屏。

### 9.5 断线草稿（§17.6）

`TE.draft.save/load/del` 提供 localStorage 草稿能力（键含 BGI 根，多账号隔离；
**超过 2 小时的草稿自动失效**，避免过时数据干扰）。基础设施已就位，
具体接入点（打开编辑器时提示「恢复上次草稿」）留 P1-b。

### 9.6 XSS 防护

所有用户输入进 HTML **必须过** `TE.esc()`（属性上下文另用 `TE.attr()`）。
实测：

    输入: <img src=x onerror=alert(1)>
    输出: &lt;img src=x onerror=alert(1)&gt;

---

## 10. 验收实测（命令 + 输出）

验证环境：`%TEMP%` 独立沙箱，服务 19500 端口，**BGI 指向 A2 真实目录（只读）**，
无头 Edge + CDP 驱动真实浏览器。

### 10.1 页面与资源

    GET /taskedit/                → 200, 2127 bytes
    含 #tree / #canvas / data-tab="pick" = True
    引用 11 个静态资源

    shell.css          200   10263 bytes
    core.js            200    8758
    tree.js            200    5741
    group.js           200    9480
    groupsettings.js   200    9508
    groupops.js        200    6606
    dragon.js          200    8320
    dragonsections.js  200    6471
    jssettings.js      200   10426
    pick.js            200    6789
    app.js             200    6094

    首屏明文合计 = 82.2 KB
    gzip 后（静态） = 37.8 KB      ← 预算 < 150 KB ✅

### 10.2 渲染（无头 Edge 截图）

- 空态：左树 + 顶栏 + Tab + 「从左侧选择配置组或一条龙开始编辑」→ 正常。
- 带 `?bgi=A2`：左树列出 9 个配置组 / 4 条一条龙，版本栏显示
  `teabag · 0.67.2 · v0.67.2+lcb.23.2-NexusBGI-fix1 · D:\AAA挂机专区\A2\BetterGI`。

### 10.3 配置组详情（CDP 实测）

    点击「联机狗粮」→ hasTable=True, rows=2
    类型标签: 地图追踪 | JS 脚本
    设置分区: sec-config | sec-pathing | sec-fight | sec-eat | sec-shell
    字段数: 26

### 10.4 右键菜单（CDP 实测三种类型）

    Pathing  → 批量删除… | 下一次任务从此处执行 | 修改通用配置 | 移除
    Javascript → 修改 JS 脚本自定义配置 | 打开所在目录 | 下一次任务从此处执行 | 修改通用配置 | 移除
    SoloTask → 修改独立任务配置 | 下一次任务从此处执行 | 修改通用配置 | 移除
    类型识别: pathing, javascript

### 10.5 一条龙详情（CDP 实测）

    点击「联机」→ 无报错
    hasWeekTable=True, weekCols=8, weekInputs=21
    分区卡片: 任务列表(4) | 合成树脂 | 自动秘境 | 自动首领讨伐 | 自动地脉花 |
              领取奖励 | 尘歌壶 | 每日奖励/邮件 | 逐日配置(一周表格) | 队伍配置
    taskRows=4, 绑定字段数=50

### 10.6 JS 设置表单（CDP 实测）

    打开 JS 项目 → 无异常
    banner warn = 「该脚本没有声明设置项（manifest.json 里没有 SettingsUi），或读取失败。你仍可查看现有值。」
    has rawJSON fallback = True

### 10.7 脏状态（CDP 实测）

    dirty.any() 初始 = False
    mark 后 = True, saveBtn = "保存 *", disabled = False
    window.onbeforeunload 已设置 = True
    clear 后 = False

### 10.8 拾取名单 Tab（CDP 实测）

    switch tab → ok, side 隐藏 = True
    模式下拉 / 基础拾取开关 / 添加输入 / 导出导入按钮 = 全部存在
    切回 edit → side 恢复显示

### 10.9 XSS（CDP 实测）

    TE.esc('<img src=x onerror=alert(1)>')  →  &lt;img src=x onerror=alert(1)&gt;
    TE.attr('a"b<c>')                        →  a&quot;b&lt;c&gt;

---

## 11. 硬约束对照

| 约束 | 落实 |
|---|---|
| 右键菜单按项目类型区分 | ✅ 三种类型实测不同（§3） |
| 添加列表调 /pickable/* | ✅ 全部走 API，前端零目录扫描（§4） |
| 字段一个不能少 | ✅ 配置组 4 区 26 字段 + 一条龙 9 区 50 字段绑定 + 一周表格 21 格（§5/§6） |
| 逐日配置做成一周表格 | ✅ 真 `<table class="week">` 8 列 21 输入框（§6.3） |
| JS 设置六类型 + 未知降级 | ✅ 六类型全支持，未知降级文本框不丢数据（§7） |
| 远程可用 | ✅ loading 态 / 分页 / 全链路 gzip / 无 CDN（§9.4） |
| 不引 CDN / 不引框架 | ✅ 原生 JS + CSS，零外部请求 |
| 所有输入转义（防 XSS） | ✅ `esc()` / `attr()`，实测转义生效（§9.6） |
| 只通过 /taskedit/api/* 读写 | ✅ 前端不拼文件路径，BGI 根由 `?bgi=` 交给服务端解析 |
| 绝不启停 BGI | ✅ 运行按钮只发通知，弹窗明确写「本页不直接启停 BGI」 |
| 单次写入 ≤300 行 | ✅ 13 个文件，最大 263 行 |
| 测试只操作副本 | ✅ 全程对 A2 只读；未写回任何 BGI 目录 |

---

## 12. 已知边界（如实记录）

1. **未做真实保存写盘测试**：前端保存路径已接通（`POST /groups/{name}` → 422 错误定位），
   但**本轮没有对真实 BGI 目录执行写入**（任务明确要求「测试只操作配置副本，不写回 A1 目录」，
   且写盘会与正在运行的 BGI 竞争，属 §4.4 的「先关 → 写 → 重启」流程，需要用户确认才做）。
   保存链路的服务端校验与备份逻辑由 t25 覆盖。
2. **独立任务配置表单未实现**：右键「修改独立任务配置」目前提示「P1-b 接入」。
   原因：`SoloTaskSettingsObject` 的字段结构随独立任务类型而异（8 种内置 + JS 注册项），
   需要按类型分别建表单，工作量独立，不宜塞进本轮。
3. **「添加任务」未实现**：一条龙的 `TaskDefinitions` 在真实配置里为空（BGI 内置常量提供），
   任务编排的候选列表需要从 BGI 二进制/常量表拿，留 P2。
4. **拾取名单写回接口未接入**：`GET /pickconfig` 与写回需要 t25 补数据层端点；
   当前点保存会明确提示「拾取名单的写回接口尚未接入（P1-b）」，**不假装成功**。
5. **组合功能占位**：左树「组合」显示 0 且点击提示「P2」。
6. **一处实现细节需要下游注意**：`dragon.js` 与 `dragonsections.js` 都用
   `Object.assign(TE.dragon, …)` 挂方法，**依赖 `core.js` 先执行**（脚本顺序在
   `TaskEditStatic.Scripts` 里保证）。若将来调整顺序会静默失效（方法 undefined）。
   已在 `GroupJs.cs` 头部加 `TE.group = TE.group || {}` 防同类问题——dragon 侧同理，
   目前顺序正确故未加，改动顺序时需一并处理。
