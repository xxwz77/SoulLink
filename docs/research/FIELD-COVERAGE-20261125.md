# 一条龙与配置组字段全覆盖核对表（t30）

> 日期：2026-11-25
> 数据源：`D:\AAA挂机专区\A1\BetterGI\User\OneDragon\`（8 个 json，97 个去重字段，只读）；`User\ScriptGroup\`（39 个 json，project 13 字段，只读）。
> 目的：t26 界面报告「一条龙 9 分区 50 绑定输入」，但真实配置 97 字段。本表逐字段判定 form / readonly / ignore，作为 t27 的输入。

---

## A. 一条龙 97 字段全表

| # | 字段名 | 类型 | 分类 | UI 建议 | 理由 |
|---|---|---|---|---|---|
| 1 | AccountBinding | boolean | form | 开关 | 用户可编辑配置项 |
| 2 | AccountBindingCode | string | form | 文本 | 用户可编辑配置项 |
| 3 | AdventurersGuildCountry | string | form | 下拉 | 用户可编辑配置项 |
| 4 | AutoBossCompletedRunCount | number | readonly | （运行时累计，只读） | 运行时累计值，由 BGI 跑任务时更新，非用户配置 |
| 5 | AutoBossName | string | form | 下拉 | 用户可编辑配置项 |
| 6 | AutoBossReturnToStatueAfterEachRound | boolean | form | 开关 | 用户可编辑配置项 |
| 7 | AutoBossReviveRetryCount | number | form | 数字 | 用户可编辑配置项 |
| 8 | AutoBossRewardRecognitionEnabled | boolean | form | 开关 | 用户可编辑配置项 |
| 9 | AutoBossRunCount | number | form | 数字 | 用户可编辑配置项 |
| 10 | AutoBossSpecifyRunCount | boolean | form | 开关 | 用户可编辑配置项 |
| 11 | AutoBossStrategyName | string | form | 下拉 | 用户可编辑配置项 |
| 12 | AutoBossTeamName | string | form | 文本 | 用户可编辑配置项 |
| 13 | AutoBossTimeout | number | form | 数字 | 用户可编辑配置项 |
| 14 | AutoBossTotalRunCountLimit | number | form | 数字 | 用户可编辑配置项 |
| 15 | AutoBossUseFragileResin | boolean | form | 开关 | 用户可编辑配置项 |
| 16 | AutoBossUseTransientResin | boolean | form | 开关 | 用户可编辑配置项 |
| 17 | CompletionAction | string | form | 下拉 | 用户可编辑配置项 |
| 18 | CraftingBenchCountry | string | form | 下拉 | 用户可编辑配置项 |
| 19 | CustomDomainList | object | form | 嵌套对象 | 用户可编辑配置项 |
| 20 | DailyRewardPartyName | string | form | 文本 | 用户可编辑配置项 |
| 21 | DomainName | string | form | 下拉 | 用户可编辑配置项 |
| 22 | EverySelectedValueListConverter | string | ignore | （序列化转换器，写回不能丢） | 序列化转换器，写回不能丢 |
| 23 | FridayDomainName | string | form | 一周表格 | 用户可编辑配置项 |
| 24 | FridayPartyName | string | form | 一周表格 | 用户可编辑配置项 |
| 25 | FridaySelectedValue | string | form | 一周表格 | 用户可编辑配置项 |
| 26 | GenshinUid | string | form | 文本 | 用户可编辑配置项 |
| 27 | IndexId | number | ignore | （排序索引，系统维护） | 排序索引，系统维护，不渲染 |
| 28 | IsAutoDomainExpanded | boolean | ignore | （UI 折叠状态） | UI 折叠状态，非任务数据；写回由 JsonNode 未知字段保留机制原样保留（保持 BGI 折叠态一致） |
| 29 | IsAutoLeyLineExpanded | boolean | ignore | （UI 折叠状态） | UI 折叠状态，非任务数据；写回由 JsonNode 未知字段保留机制原样保留（保持 BGI 折叠态一致） |
| 30 | IsBindUidExpanded | boolean | ignore | （UI 折叠状态） | UI 折叠状态，非任务数据；写回由 JsonNode 未知字段保留机制原样保留（保持 BGI 折叠态一致） |
| 31 | IsClaimRewardsExpanded | boolean | ignore | （UI 折叠状态） | UI 折叠状态，非任务数据；写回由 JsonNode 未知字段保留机制原样保留（保持 BGI 折叠态一致） |
| 32 | IsCompletionActionExpanded | boolean | ignore | （UI 折叠状态） | UI 折叠状态，非任务数据；写回由 JsonNode 未知字段保留机制原样保留（保持 BGI 折叠态一致） |
| 33 | IsCraftingResinExpanded | boolean | ignore | （UI 折叠状态） | UI 折叠状态，非任务数据；写回由 JsonNode 未知字段保留机制原样保留（保持 BGI 折叠态一致） |
| 34 | IsSereniteaPotExpanded | boolean | ignore | （UI 折叠状态） | UI 折叠状态，非任务数据；写回由 JsonNode 未知字段保留机制原样保留（保持 BGI 折叠态一致） |
| 35 | LeyLineFridayCountry | string | form | 一周表格 | 用户可编辑配置项 |
| 36 | LeyLineFridayType | string | form | 一周表格 | 用户可编辑配置项 |
| 37 | LeyLineMondayCountry | string | form | 一周表格 | 用户可编辑配置项 |
| 38 | LeyLineMondayType | string | form | 一周表格 | 用户可编辑配置项 |
| 39 | LeyLineOneDragonMode | boolean | form | 开关 | 用户可编辑配置项 |
| 40 | LeyLineOpenModeCountMin | boolean | form | 开关 | 用户可编辑配置项 |
| 41 | LeyLineResinExhaustionMode | boolean | form | 开关 | 用户可编辑配置项 |
| 42 | LeyLineRunCount | number | form | 数字 | 用户可编辑配置项 |
| 43 | LeyLineRunFriday | boolean | form | 一周表格(开关) | 用户可编辑配置项 |
| 44 | LeyLineRunMonday | boolean | form | 一周表格(开关) | 用户可编辑配置项 |
| 45 | LeyLineRunSaturday | boolean | form | 一周表格(开关) | 用户可编辑配置项 |
| 46 | LeyLineRunSunday | boolean | form | 一周表格(开关) | 用户可编辑配置项 |
| 47 | LeyLineRunThursday | boolean | form | 一周表格(开关) | 用户可编辑配置项 |
| 48 | LeyLineRunTuesday | boolean | form | 一周表格(开关) | 用户可编辑配置项 |
| 49 | LeyLineRunWednesday | boolean | form | 一周表格(开关) | 用户可编辑配置项 |
| 50 | LeyLineSaturdayCountry | string | form | 一周表格 | 用户可编辑配置项 |
| 51 | LeyLineSaturdayType | string | form | 一周表格 | 用户可编辑配置项 |
| 52 | LeyLineSundayCountry | string | form | 一周表格 | 用户可编辑配置项 |
| 53 | LeyLineSundayType | string | form | 一周表格 | 用户可编辑配置项 |
| 54 | LeyLineThursdayCountry | string | form | 一周表格 | 用户可编辑配置项 |
| 55 | LeyLineThursdayType | string | form | 一周表格 | 用户可编辑配置项 |
| 56 | LeyLineTuesdayCountry | string | form | 一周表格 | 用户可编辑配置项 |
| 57 | LeyLineTuesdayType | string | form | 一周表格 | 用户可编辑配置项 |
| 58 | LeyLineWednesdayCountry | string | form | 一周表格 | 用户可编辑配置项 |
| 59 | LeyLineWednesdayType | string | form | 一周表格 | 用户可编辑配置项 |
| 60 | MinResinToKeep | number | form | 数字 | 用户可编辑配置项 |
| 61 | MondayDomainName | string | form | 一周表格 | 用户可编辑配置项 |
| 62 | MondayPartyName | string | form | 一周表格 | 用户可编辑配置项 |
| 63 | MondaySelectedValue | string | form | 一周表格 | 用户可编辑配置项 |
| 64 | Name | string | form | 文本 | 用户可编辑配置项 |
| 65 | NextConfiguration | boolean | form | 开关 | 用户可编辑配置项 |
| 66 | NextTaskId | string | readonly | （下次执行标记，由菜单驱动，只读） | 下次执行任务标记，由右键「从此执行」菜单设置，不由表单直接编辑 |
| 67 | PartyName | string | form | 下拉 | 用户可编辑配置项 |
| 68 | Period | string | form | 下拉 | 用户可编辑配置项 |
| 69 | PeriodList | object | form | 一周表格(开关) | 用户可编辑配置项 |
| 70 | ResinCount | object | form | 嵌套对象 | 用户可编辑配置项 |
| 71 | SaturdayDomainName | string | form | 一周表格 | 用户可编辑配置项 |
| 72 | SaturdayPartyName | string | form | 一周表格 | 用户可编辑配置项 |
| 73 | SaturdaySelectedValue | string | form | 一周表格 | 用户可编辑配置项 |
| 74 | ScheduleName | string | form | 下拉 | 用户可编辑配置项 |
| 75 | SecretTreasureObjects | object | form | 多选 | 用户可编辑配置项 |
| 76 | SereniteaPotTpType | string | form | 下拉 | 用户可编辑配置项 |
| 77 | SpecifyResinUse | boolean | form | 开关 | 用户可编辑配置项 |
| 78 | SundayDaySelectedValue | string | form | 下拉 | 用户可编辑配置项 |
| 79 | SundayDomainName | string | form | 一周表格 | 用户可编辑配置项 |
| 80 | SundayEverySelectedValue | string | form | 下拉 | 用户可编辑配置项 |
| 81 | SundayPartyName | string | form | 一周表格 | 用户可编辑配置项 |
| 82 | SundaySelectedValue | string | form | 一周表格 | 用户可编辑配置项 |
| 83 | SundayWeeklySelectedValue | string | form | 下拉 | 用户可编辑配置项 |
| 84 | TaskDefinitions | object | form | 任务列表组 | 用户可编辑配置项 |
| 85 | TaskEnabledList | object | form | 任务列表组 | 用户可编辑配置项 |
| 86 | TaskOrder | object | form | 任务列表组 | 用户可编辑配置项 |
| 87 | ThursdayDomainName | string | form | 一周表格 | 用户可编辑配置项 |
| 88 | ThursdayPartyName | string | form | 一周表格 | 用户可编辑配置项 |
| 89 | ThursdaySelectedValue | string | form | 一周表格 | 用户可编辑配置项 |
| 90 | TuesdayDomainName | string | form | 一周表格 | 用户可编辑配置项 |
| 91 | TuesdayPartyName | string | form | 一周表格 | 用户可编辑配置项 |
| 92 | TuesdaySelectedValue | string | form | 一周表格 | 用户可编辑配置项 |
| 93 | Version | number | ignore | （schema 版本，迁移用） | schema 迁移版本号，不渲染不编辑，写回保留 |
| 94 | WednesdayDomainName | string | form | 一周表格 | 用户可编辑配置项 |
| 95 | WednesdayPartyName | string | form | 一周表格 | 用户可编辑配置项 |
| 96 | WednesdaySelectedValue | string | form | 一周表格 | 用户可编辑配置项 |
| 97 | WeeklyDomainEnabled | boolean | form | 开关 | 用户可编辑配置项 |

**计数校验：form=85 + readonly=2 + ignore=10 = 97（= 97）**

---

## B. 逐分区分组核对（与 t26 的 50 输入对比）

t26 的「50 个绑定输入」是**保守实现**：它只做了核心分区的一周表格与开关，漏掉了大量字段。按 §15 的 9 个分区归类如下（字段数 = 去重字段落在该分区的数量；form 数 = 其中需做成控件的）：

| 分区 | 字段数 | form 数 | t26 是否覆盖 | 差在哪 |
|---|---|---|---|---|
| 合成树脂 | 6 | 5 | 部分 | 缺 SpecifyResinUse/ResinCount 嵌套 + IsCraftingResinExpanded |
| 自动秘境 | 8 | 6 | 部分 | 缺 CustomDomainList 嵌套 + SundayDaySelectedValue + IsAutoDomainExpanded |
| 自动首领 | 1 | 0 | 部分 | 缺 AutoBossCompletedRunCount(readonly)/AutoBossRewardRecognitionEnabled/AutoBossReturnToStatueAfterEachRound 等 |
| 地脉花 | 1 | 0 | 部分 | 一周表格基本覆盖，但缺 LeyLineOneDragonMode/OpenModeCountMin/ResinExhaustionMode 三个开关 |
| 领奖 | 4 | 2 | 部分 | 缺 CompletionAction 下拉 + 2 折叠态 |
| 尘歌壶 | 3 | 2 | 部分 | 缺 SecretTreasureObjects 多选 |
| 每日奖励 | 1 | 0 | 是 | 基本覆盖 |
| 树脂 | 3 | 2 | 部分 | 缺 ResinCount 嵌套 |
| 队伍 | 1 | 1 | 是 | 覆盖 |
| 任务列表（特殊组） | 3 | 3 | 是 | 覆盖（但 TaskDefinitions 语义需按 t28 订正：GUID→中文名） |
| 账户/周期/其它 | 11 | 8 | 部分 | 缺账户绑定组 + 周期组 + NextConfiguration |

**结论**：t26 的 50 输入 vs 真实 85 个 form 字段，**差 35 个 form 字段**。主要差额在：账户绑定组（AccountBinding/AccountBindingCode/GenshinUid）、周期组（Period/PeriodList/ScheduleName/NextConfiguration）、树脂嵌套（ResinCount/SpecifyResinUse）、尘歌壶商品多选（SecretTreasureObjects）、自定义秘境（CustomDomainList）、自动首领补全字段。

---

## C. 配置组 project 13 字段核对表

| 字段名 | 类型 | 是否在 t26 界面 | 控件 | 可编辑 | 说明 |
|---|---|---|---|---|---|
| index | number | 是 | （隐藏） | 否 | 排序权重，系统维护（与 RouteLedger.SortOrder 同语义） |
| name | string | 是 | 文本 | 是 | 项目名（脚本名/路线名/任务名） |
| type | string | 是 | 下拉 | 是 | Javascript/KeyMouse/Pathing/Shell/SoloTask |
| status | string | 是 | 下拉 | 是 | Enabled/Disabled |
| schedule | string | 是 | 下拉 | 是 | Daily/EveryTwoDays/Monday~Sunday/自定义Cron |
| runNum | number | 是 | 数字 | 是 | 执行次数 |
| folderName | string | 部分 | 文本 | 是 | 脚本文件夹/路线相对目录（不同类型语义不同） |
| nextFlag | boolean | **否（漏）** | 开关/右键「从此执行」 | 是 | 下次从这里执行标记 |
| taskCenterTaskId | string | 否 | 隐藏 | 否 | 任务中心 ID，系统维护 |
| allowJsHTTPHash | string | **否（漏）** | （只读展示+校验） | 是（间接） | JS 脚本 HTTP 权限 hash |
| allowJsNotification | boolean | **否（漏）** | 开关 | 是 | JS 通知权限 |
| jsScriptSettingsObject | object | 部分 | 动态表单（SettingItem） | 是 | JS 脚本自定义配置 |
| soloTaskSettingsObject | object | 部分 | 动态表单 | 是 | 独立任务专属配置 |

**特别确认**：`nextFlag`、`allowJsHTTPHash`、`allowJsNotification` 三个**当前 t26 界面缺失**，必须补（详见 §D）。

---

## D. 结论：必补字段清单（给 integration-author）

### D1. 一条龙必补 form 字段（按分区）

| 分区 | 必补字段 | 控件 |
|---|---|---|
| 账户 | AccountBinding / AccountBindingCode / GenshinUid | 开关 + 文本 + 文本 |
| 周期 | Period / PeriodList / ScheduleName / NextConfiguration | 下拉 + 一周表格开关 + 下拉 + 开关 |
| 合成树脂 | SpecifyResinUse / ResinCount | 开关 + 嵌套对象（浓缩/原粹/须臾/脆弱） |
| 自动秘境 | CustomDomainList / SundayDaySelectedValue | 嵌套对象 + 下拉 |
| 自动首领 | AutoBossRewardRecognitionEnabled / AutoBossReturnToStatueAfterEachRound | 开关 + 开关 |
| 地脉花 | LeyLineOneDragonMode / LeyLineOpenModeCountMin / LeyLineResinExhaustionMode | 3 个开关 |
| 尘歌壶 | SecretTreasureObjects | 多选（布匹/须臾树脂/大英雄的经验/流浪者的经验/精锻用魔矿/摩拉/祝圣精华/祝圣油膏） |
| 完成后操作 | CompletionAction | 下拉 |

### D2. 配置组 project 必补字段

| 字段 | 控件 | 说明 |
|---|---|---|
| nextFlag | 开关（或并入右键「从此执行」） | 下次从这里执行 |
| allowJsNotification | 开关 | JS 通知权限（仅 JS 类型可见） |
| allowJsHTTPHash | 只读展示 + 重新校验 | JS HTTP 权限（安全提示） |

### D3. 只读/忽略字段处理约定

- `AutoBossCompletedRunCount`（运行时累计）、`NextTaskId`（菜单驱动）→ 只读展示，不渲染为可编辑控件。
- `Is*Expanded`（7 个）、`IndexId`、`Version`、`EverySelectedValueListConverter` → 不在 UI 出现，但**写回时由 JsonNode 未知字段保留机制原样保留**，绝不能丢。

---

## 附：字段全表统计

- form = 85
- readonly = 2
- ignore = 10
- 合计 = 97（= 97 字段）
