# 任务编辑统一页面设计：合并 ABGI 的 BgiConfig / BgiFlow / listGroups

> 日期：2026-11-25
> 状态：研究 + 设计（定稿）。本任务不写生产代码。
> 上游仓库：**kaedelcb/better-genshin-impact（茶包版）**，用户指定「首先要适配它」。
> 用户三个已定稿决策（2026-11-25 裁定，不再是开放问题）：
>   ① 布局 = **保留 BGI 心智模型但重排版**（不做像素级 1:1 复刻）；
>   ② 写 BGI = **先关 BGI → 写 → 重启**，且必须有提示（对齐 AutoBGI）；
>   ③ 版本判定 = **ProductVersion 含 lcb 即茶包版，不含即公版**，基础版本号单独解析。
> 证据来源：茶包版源码（%TEMP% 克隆 + git show 精读）、A1/A2/A3/A4 真实配置 JSON 差分、ABGI 三页 chunk、AutoBGI main.json。

---

## 0. 结论摘要（TL;DR）

1. **三页合并成一页可行且天然去重**：BgiFlow 是 272 字节空壳（import BgiConfig + designer-mode）；listGroups 的「编辑」= GroupDetailDialog；BgiConfig 的「脚本」+「配置组项目」= 同一 projects 的两种视图。
2. **BGI 原生布局真相（源码级）**：一条龙页 OneDragonFlowPage.xaml 是**单页**——左侧任务列表（拖拽排序 + 开关 + 右键菜单），右侧「配置下拉 + 新建/改名/删除按钮」下面是一串**分区标题 + 可折叠卡片（CardControl/CardExpander）**。View/Pages/OneDragon/*.xaml 是子页组件，但当前源码里已**注释掉**，改用内联卡片（子页方案被废弃）。
3. **配置组设置**（ScriptGroupConfigView.xaml）也是同款 CardExpander 结构：标题 + 副标题 + 右侧开关/下拉/输入框，字段级灰字说明。
4. **写盘规则（源码级）**：BGI 用 ScriptGroup.WriteToFileAtomically = 临时文件 + File.Move(overwrite:true)，无「运行中禁止写」保护——所以我们的「先关 BGI → 写 → 重启」正是补上这个缺口。
5. **版本能力集**：A1/A2/A3/A4 的 OneDragon 配置 top-level 字段**几乎完全一致**（96~97 键，唯一差异是 A1 多一个疑似误序列化的 EverySelectedValueListConverter 键），配置 JSON 里**没有** Version/Channel/Edition 标记 → 版本只能靠 exe ProductVersion 判 lcb。基础版本号（0.61.2/0.67.0/0.67.2）决定功能可用性，与「茶包/公版」两维。

---

## 1. 三页功能全清单与去重表

### 1.1 功能全清单（来源：Lead 拆包 + 本轮实读 chunk）

chunk（%TEMP%\autobgi-ref\，实读确认）：ListGroups-34fdc764.js、BgiConfig-7ba8c16f.js、BgiFlow-e803bb2c.js（272 字节空壳）、GroupDetailDialog-e8bdb4c9.js。

### 1.2 去重表（逐条判定）

| 功能 | 原页面 | 判定 | 理由 |
|---|---|---|---|
| 配置组列表 | listGroups | 保留（左树根节点） | 统一入口 |
| 新建/编辑/删除/复制 | listGroups | 保留（编辑=组详情，删除/复制并入组右键） | 编辑与 GroupDetailDialog 同一物，去重 |
| 启用项/项目数量/更新时间/排序 | listGroups | 保留为列表列 | |
| 分类（新增/编辑/成员上移下移/批量加入或替换） | listGroups | 保留为「分组」栏 | |
| 快捷组合（新增/编辑/组合成员/启动组合/存为组合） | listGroups | 保留为「组合」栏 | 组合=一次性多组启动，区别于一条龙编排 |
| 收藏/标记颜色/搜索/批量启动/启动所选 | listGroups | 保留（工具栏+列表列） | |
| 配置名称/保存/创建 | BgiConfig | 保留（顶部工具栏） | |
| 复制配置组 | BgiConfig | 合并到 listGroups「复制」 | 两页重复，去重 |
| 启动一条龙/启动当前一条龙配置 | BgiConfig | 保留（工具栏） | |
| 任务流（流程图/顺序/从此执行/起始任务必须开启/没有任务提示） | BgiConfig | 保留（右画布核心） | 对应 OneDragonFlowPage 左侧任务列表 + 右键「从此执行」 |
| 每日奖励/秘境/树脂/尘歌壶/地图追踪/地图传送/短距离移动/队伍 | BgiConfig | 保留（一条龙字段分组） | 对应 OneDragonFlowPage 右侧分区卡片 |
| 自动拾取名单（白/黑名单/模糊精确/基础/排除/优先/导入导出） | BgiConfig | 保留（独立 Tab「拾取名单」） | 跨两类，独立不重复 |
| 脚本 + 配置组项目（读取失败提示） | BgiConfig | 合并到 GroupDetailDialog | 同一 projects 的两种视图 |
| 旧版一条龙文件兼容提示 | BgiConfig | 保留为一条龙编辑区顶部横幅 | |
| 脚本项目（选 JS）/地图追踪路线（选路线 JSON）/配置组设置（字段校验标红） | GroupDetailDialog | 保留（组详情分区） | 对应 ScriptGroupProjectEditor + ScriptGroupConfigView |
| 未保存修改保护（放弃/继续/重新加载/草稿） | GroupDetailDialog | 保留（全局脏状态机） | 提升为全局 |
| 搜索文件夹或 JSON 文件 | GroupDetailDialog | 保留 | |
| BgiFlow 整页 | BgiFlow | 删除（空壳） | |

### 1.3 去重结论

三页去重后剩 3 编辑对象（配置组/一条龙/拾取名单）+ 组合。

---

## 2. BGI 原生界面观察（源码 + 截图双证据）

### 2.1 数据模型（源码级，茶包版 kaedelcb fork）

#### 配置组模型（BetterGenshinImpact/Core/Script/Group/）

- ScriptGroup.cs：{ Index:int, Name:string, Config:ScriptGroupConfig, Projects:ObservableCollection<ScriptGroupProject> }。保存 WriteToFileAtomically = 序列化到 .{name}.{guid}.tmp → File.Move(temp, full, overwrite:true)，UTF-8 无 BOM。序列化用 System.Text.Json（ToJson），反序列化用 Newtonsoft（FromJson）——两套序列化器并存，这是要小心的点。
- ScriptGroupConfig.cs：**4 个键**——PathingConfig:PathingPartyConfig、ShellConfig:ShellConfig、EnableShellConfig:bool、**autoPickConfig**（{whiteList,blackList}，实测 A1 联机锄地-混合精英.json 有）。真正的配置组级设置都在 PathingConfig。
  ★ 澄清：**「自动吃食物配置」不在 ScriptGroupConfig 里**，它是 `PathingPartyConfig.AutoEatConfig`（供 JS 脚本调用的 Buff 类食物配置），归属 PathingConfig 内，不是独立分区。ScriptGroupConfig 只有 3 个字段，配置组设置面板应严格对齐这 3 个（展开为 PathingConfig 的几十个字段 + ShellConfig 的 4 个字段），不要做出比 BGI 更臃肿的设置面板。
- ScriptGroupProject.cs（项目 = 配置组内的一项任务）：**13 个字段一个不能少**——{Index, Name, FolderName, Type, Status, Schedule, RunNum, NextFlag, TaskCenterTaskId, AllowJsHTTPHash, AllowJsNotification, JsScriptSettingsObject, SoloTaskSettingsObject}。★ 界面里 NextFlag（下次从这里执行）、TaskCenterTaskId、AllowJsHTTPHash/AllowJsNotification（JS 权限开关）**必须能看能改**，易漏。
  - Type 枚举（ScriptGroupProjectExtensions.TypeDescriptions）：Javascript→JS脚本、KeyMouse→键鼠脚本、Pathing→地图追踪、Shell→Shell。
  - Status：Enabled→启用 / Disabled→禁用。
  - Schedule：Daily→每日、EveryTwoDays→间隔两天、Monday~Sunday→每周一~日；不在枚举内 = 自定义 Cron。
  - 工厂方法：BuildKeyMouseProject(name)、BuildShellProject(command)、BuildPathingProject(name,folder)、ScriptGroupProject(ScriptProject)（JS）。
  - Run() 里写 ExecutionRecord（GroupName/ProjectName/Type/FolderName/StartTime/EndTime/IsSuccessful）——这就是之前 t18 读到的 ExecutionRecords 的来源。
  - **SoloTask（独立任务）**：type="SoloTask" 的项目带 `soloTaskSettingsObject`（如 enemyType/runTimes/ocrTimeout/fightTimeout/partyName/loopTillNoExpOrMora/disablePickup/use1000Stars，实测 A1 好感.json）+ `folderName=""`。独立任务枚举（GameTask/Model/IndependentTaskEnum.cs，源码只有 8 项，无中文注释）：AutoGeniusInvokation（七圣召唤）/ AutoWood（自动伐木）/ AutoFight（自动战斗）/ AutoDomain（自动秘境）/ AutoTrack（自动追踪）/ AutoTrackPath（自动追踪-路线）/ AutoMusicGame（自动音游）/ AutoPathing（自动寻路）。中文名来自 Dispatcher.cs 的 switch case 注释。

#### 一条龙模型（BetterGenshinImpact/Core/Config/OneDragonFlowConfig.cs）

- {Name, NextTaskId, TaskEnabledList:Dict<guid,bool>, TaskOrder:List<guid>, TaskDefinitions:Dict<guid,中文名>}（任务流 = TaskOrder 的 GUID 数组引用 TaskDefinitions 的 GUID→中文名字典）。★ **TaskDefinitions 不是空数组、也不来自内置常量**——它是「GUID → 中文名」字典（A2 实测 9 项，如 "1bba35cb-…":"异常处理"）；A1 累计 38 条任务名（背包扫描/彩蛋/等待至4-00/钓鱼/…），**无一是 IndependentTaskEnum 内置名**（全是脚本注册的自定义任务）。实现口径：**TaskDefinitions 优先取中文名，为空时才降级到内置常量表**。
- 分区字段（默认值）：合成树脂（CraftingBenchCountry=枫丹、AdventurersGuildCountry=枫丹、MinResinToKeep=0）、自动首领（AutoBossStrategyName=根据队伍自动选择、AutoBossRunCount=1、AutoBossReviveRetryCount=3、AutoBossTimeout=240）、每日奖励（DailyRewardPartyName）、尘歌壶（SereniteaPotTpType=地图传送、SecretTreasureObjects）、地脉花（LeyLineRunMonday~Sunday=true、LeyLineMondayType+Country~周日、LeyLineRunCount=0、LeyLineResinExhaustionMode、LeyLineOpenModeCountMin）、每周秘境（Monday~SundayPartyName/DomainName/SelectedValue + SundayDaySelectedValue/SundayEverySelectedValue/SundayWeeklySelectedValue）、完成后操作（CompletionAction）。
- 校验规则（源码里的 partial void On*Changed）：AutoBossRunCount<1 强制回 1；AutoBossTotalRunCountLimit/CompletedRunCount/ReviveRetryCount<0 强制回 0；AutoBossSpecifyRunCount=false 时清空 transient/fragile resin。→ 这些是源码级范围校验，我们要照抄。
- 「4 点起算」服务日边界：GetDomainConfig()/ShouldRunLeyLineToday()/GetLeyLineConfigForToday(fallback) 都用 serverTime.Hour>=4 ? serverTime : serverTime.AddDays(-1) 判「当天」——这是 BGI 的日界线语义，做逐日配置时要一致。
- GetLeyLineConfigForToday 对逐日字段为空的回退到「独立任务 AutoLeyLineOutcropConfig」——即逐日留空 = 用独立任务配置。

### 2.2 一条龙界面（OneDragonFlowPage.xaml，源码级）

**结构（单页）**：

| 区域 | 内容 | 源码依据 |
|---|---|---|
| 左侧任务列表 | ListView（拖拽排序 dd:DragDrop）+ 卡片模板（图标 + StatusColor 圆点 + 名称 + 右侧 ToggleSwitch IsEnabled）| 左 DockPanel，ItemTemplate 卡片 |
| 任务右键菜单 | 新增配置 / 删除 / 从此执行 / 清除标记 | ContextMenu 四项 |
| 任务工具栏 | 一键执行（Play 图标）+ 添加（Add 图标，弹出「选择增加的配置组（可多选）」Popup）| 左顶 Grid |
| 右侧顶部 | 「配置」标题 + 配置下拉 ComboBox（ConfigList）+ 新建/改名/删除三按钮 | 右 DockPanel 顶部 |
| 右侧正文 | ScrollViewer 内：分区标题（强调色 TextBlock）+ CardControl/CardExpander 卡片 | 逐分区 |

**分区清单（源码确认，含 Lead 未截图的部分）**：

| 分区 | 控件类型 | 关键字段 |
|---|---|---|
| 合成树脂 | CardControl | CraftingBenchCountry（下拉）+ MinResinToKeep（输入框） |
| 自动秘境 | CardExpander + 副标题「此处未覆盖的配置可在 独立任务-自动秘境 中配置」 | Monday~Sunday PartyName/DomainName/SelectedValue |
| 自动首领 | CardExpander | AutoBoss* 系列 |
| 地脉花 | CardExpander | LeyLine* 系列（一周表格） |
| 每日奖励 | CardControl | DailyRewardPartyName（好感队名输入框） |
| 尘歌壶配置 | CardControl + 「购买选择」Popup | SereniteaPotTpType + SecretTreasureObjects（8 个复选） |
| 完成后操作 | CardControl | CompletionAction（下拉） |

**关键架构答案（回答 Lead 的问题）**：View/Pages/OneDragon/*.xaml（Craft/DailyCommission/DailyReward/Domain/Forging/LeyLineBlossom/Mail/SereniteaPot/Tcg）曾是「右侧 ContentControl 按 ViewModel DataTemplate 切换子页」的组件，但**当前源码里这段已注释掉**，改成在 OneDragonFlowPage.xaml 内联 CardExpander 卡片。→ BGI 原生是「单页内可折叠卡片」，不是「多子页面」。截图 1/3 的「卡片同页」是对的。

### 2.3 配置组界面（ScriptGroupConfigView.xaml + ScriptGroupProjectEditor.xaml）

配置组设置（ScriptGroupConfigView.xaml）：单列 700px 宽 StackPanel，ui:CardExpander 分区，每分区 Header = 标题（Body 字号）+ 灰色副标题 + 右侧 ToggleSwitch。分区：
- 地图追踪行走配置（CardExpander，Icon Map24）：Enabled 开关 + 副标题「不启用则使用地图追踪内的条件配置」；展开后每字段 = 左「标题 + 灰色说明」右「开关/下拉/输入框」——自动拾取、自动吃药、切换队伍名称、切换前前往神像、生存位编号、生存位战技间隔、生存位长按、行走位编号、小道具间隔、低血量回复时机、不在某时执行、不在连续任务中显示。
- 自动吃食物配置（CardExpander）：默认攻击/冒险/防御料理名称（三个输入框）。
- Shell 执行配置（CardExpander，FlashSettings24）：EnableShellConfig 开关 + 禁用 Shell 任务 / Shell 超时 / 隐藏窗口 / 记录输出。

项目编辑器（ScriptGroupProjectEditor.xaml）：350px 弹窗，字段：状态（StatusDescriptions 下拉）、JS 通知权限（仅 IsJsScript 可见）、JS HTTP 权限（橙色警告文案 + 只读信息框）。

项目列表列（截图 6）：# / 名称 / 类型 / 启用状态；类型中文 = TypeDescriptions。右键菜单（截图 6）：添加JS脚本/添加地图追踪任务/添加键鼠脚本/添加Shell/添加独立任务/下一次任务从此处执行/修改通用配置/修改JS脚本自定义配置/打开所在目录/移除。

### 2.4 照搬 vs 不照搬

| 照搬 | 不照搬 |
|---|---|
| 配置组 = projects 列表 + CardExpander 设置分区 | WPF 的 ui:CardExpander 控件本身（网页用折叠卡片） |
| 一条龙 = 左任务列表 + 右分区卡片，单页 | 子页面 DataTemplate 方案（源码已注释废弃） |
| 字段级「标题 + 灰色副标题 + 右侧控件」布局 | 英文/代码字段名直出（中文标签） |
| 源码级范围校验（RunCount≥1、负值回 0） | 无 |
| 逐日配置 = 一周表格（LeyLine/Monday~Sunday） | 逐日折叠 7 块 |
| 4 点起算服务日语义 | 无 |

---

## 3. 合并后的信息架构（2 候选布局 + 推荐 + 与 BGI 三栏的对照）

### 3.1 候选 A：左树 + 右画布（推荐，收敛）

```
┌──────────────────────────────────────────────────────────┐
│ 顶部工具栏：账号下拉 | [配置组] [一条龙] [拾取名单] | 保存 | 启动 │
│ 版本栏：BGI 0.67.2 · 茶包版 (lcb.23.2-NexusBGI-fix3)      │
├──────────────┬───────────────────────────────────────────┤
│ 左树          │ 右编辑画布                                 │
│  ├ 配置组      │  （一条龙）左=任务列表(拖拽/开关/右键)      │
│  │  ├ 分类1   │            右=分区折叠卡片(合成树脂/秘境/…) │
│  ├ 一条龙      │  （配置组）左=项目列表(#/名称/类型/启用)   │
│  ├ 组合        │            右=CardExpander 设置分区         │
│  └ (拾取名单→Tab)│                                          │
└──────────────┴───────────────────────────────────────────┘
```

### 3.2 候选 B：上下分栏

- 上：配置组 + 一条龙双列表；下：编辑区。缺点：一条龙字段多、编辑区矮、上下跳。不推荐。

### 3.3 与 BGI 三栏结构的对照与最终推荐

BGI 一条龙页的真实结构其实是**两栏**（左任务列表 + 右配置卡片），不是「左导航 + 中列表 + 右卡片」三栏。用户截图里的「左侧导航」是 BGI 整个主窗口的页导航，不是一条龙页内部结构。所以：
- 推荐 A：保留 BGI「左列表 + 右卡片」的心智模型，但把「配置组/一条龙/组合」三类放进左树，顶部用 Tab 切编辑对象。
- 为什么比 BGI 原生更好：BGI 一条龙页和配置组页是分开的两页，要改一个配置组的脚本得离开一条龙页；我们的左树+右画布让两者在同一页内切换，且卡片布局、字段分组、一周表格、标红校验都对齐 BGI。
- 点击次数：编辑一条龙时想顺手改某配置组脚本 = 左树点该组（1）→ 右侧展开组详情（1）= 2 次点击；反向 = 组详情点「加入一条龙」→ 选一条龙拖到位 = 2~3 次。

---

## 4. 数据契约、写盘规则与并发风险

### 4.1 文件格式（源码级）

| 对象 | 文件 | 结构 |
|---|---|---|
| 配置组 | User/ScriptGroup/<名>.json | {Index, Name, Config{pathingConfig, shellConfig, enableShellConfig, autoPickConfig}, Projects[]} |
| 一条龙 | User/OneDragon/<名>.json | {Name, NextTaskId, TaskEnabledList, TaskOrder(GUID数组), TaskDefinitions(GUID→中文名字典), +分区扁平字段(共约97键)} |
| 全局/拾取 | User/config.json（autoPickConfig）+ pick_black_lists.txt/pick_fuzzy_black_lists.txt | |

★ **大小写（实测订正）**：真实文件字段**全是 PascalCase**（A2 实测 76 键，camelCase 键 = 0）。上表与全文示例里的 `pathingConfig`/`shellConfig` 等应读作 `PathingConfig`/`ShellConfig`/`EnableShellConfig`/`AutoPickConfig`、一条龙的 `TaskEnabledList`/`TaskOrder`/`AutoBossRunCount`/`LeyLineRunMonday` 等。**实现必须双命名兜底：大小写不敏感读取 + 原样写回**（不擅自改大小写）。

### 4.1.1 配置组 config 的完整嵌套结构（A1 实测，重写）

★ **结构错误订正（t32）**：文档早先把 config 写成「顶层四区」，漏了嵌套层级。真实嵌套是 autoFightConfig / autoEatConfig 等**嵌在 pathingConfig 内部**，不是顶层。以下逐层列出（键路径 = 真实文件层级，字段数 = 实测）：

```
配置组（User/ScriptGroup/<名>.json）顶层 5 键：
  index (number) / taskCenterStartTaskId (string|null) / name (string) / config (object) / projects (array)

config 顶层 4 键：
  pathingConfig (object, 45 键) / shellConfig (object, 4 键) / enableShellConfig (bool) / autoPickConfig (object, 2 键)

pathingConfig 45 键（顶层标量 + 6 个嵌套对象）：
  标量约 25 键：recoverTiming / enabled / autoPickEnabled / partyName /
    isVisitStatueBeforeSwitchParty / mainAvatarIndex / guardianAvatarIndex /
    guardianElementalSkillSecondInterval / guardianElementalSkillLongPress / onlyInTeleportRecover /
    jsScriptUseEnabled / soloTaskUseFightEnabled / skipDuring / useGadgetIntervalMs / autoSkipEnabled /
    autoRunEnabled / autoEatEnabled / redBloodSwitchOnly / hideOnRepeat / autoFightEnabled /
    distance / travelMode / mwkFlyEnabled / mwkFlyJumpDistance / useNewHurrySystem /
    switchToWalkEnabled / mwkJumpFlyEnabled / mwkJumpFlyDistance / mwkJumpFlyIntervalSeconds /
    mwkDisableSprintEnabled / mwkJumpFlySprintCount / approachStopDistance / recoverAvatarIndex /
    quicklySkip / combatScriptEndDelayMs / enablePickupTargetCount / disableAutoFetchDispatch 等
  嵌套对象：
    pathingConfig.autoEatConfig (8 键)
    pathingConfig.taskCycleConfig (5 键)
    pathingConfig.taskCompletionSkipRuleConfig (6 键)
    pathingConfig.preExecutionPriorityConfig (3 键)
    pathingConfig.autoFightConfig (47 键，含 useOfficialAutoFight 等茶包独有 + finishDetectConfig 子对象)
    pathingConfig.autoFightOfficialConfig (24 键，公版引擎配置)

shellConfig 4 键：disable / timeout / noWindow / output
enableShellConfig (bool)
autoPickConfig 2 键：whiteList / blackList

project（projects[] 每项）13 键：
  taskCenterTaskId / name / folderName / jsScriptSettingsObject / soloTaskSettingsObject /
  nextFlag / index / type / status / schedule / runNum / allowJsNotification / allowJsHTTPHash
```

**层级对照**：config.pathingConfig.autoFightConfig.useOfficialAutoFight 在真实文件里就是 config → pathingConfig → autoFightConfig → useOfficialAutoFight 四级嵌套，不是顶层「四区」平铺。集成方实测渲染 8 分区 129 字段，对应 pathingConfig 45 + autoFightConfig 47 + autoFightOfficialConfig 24 + 其余嵌套，远多于早期「四区 26 字段」的推导。


### 4.2 BGI 自己的写盘规则（源码级，决定我们怎么写）

- ScriptGroup.WriteToFileAtomically：临时文件 + File.Move(overwrite:true)，UTF-8 无 BOM。BGI 自身没有「运行中禁止写」保护。
- 一条龙的保存走同款 JSON 序列化（ConfigService），字段 Version 用于 schema 迁移（当前 = 1）。
- 结论：BGI 运行时会持有内存态，退出时写回；我们外部写文件会与它竞争。用户拍板的「先关 BGI → 写 → 重启」是正确且必要的。

### 4.3 字段级校验（源码级，照抄）

- AutoBossRunCount ≥ 1；AutoBossTotalRunCountLimit/CompletedRunCount/ReviveRetryCount ≥ 0；AutoBossSpecifyRunCount=false 时清 transient/fragile resin。
- 逐日字段为空 = 回退独立任务配置。
- 4 点起算服务日边界。

### 4.4 写入流程（用户定稿：先关 → 写 → 重启，必须有提示）

1. 前置提示：点保存先弹「即将关闭 BGI → 写入配置 → 重新启动 BGI」，列出受影响对象，要求确认。
2. 检测运行中任务：若有配置组/一条龙在跑，特别警告「现在中断会丢失当前进度」，给「仍然写入/取消」二选一，不得直接关。
3. 关闭 BGI：参照 AutoBGI 语义，能用脚本关就不强杀；设超时，关不掉回滚为不写并提示。
4. 备份：写前复制 .<name>.bak-<时间戳>，保留最近 3 份。
5. 写入：原子替换（临时文件 + Move），失败回滚备份。
6. 重启 BGI：恢复原任务；启动失败明确告知 + 手动启动指引，备份保留。
7. 全过程状态反馈：关闭中/备份中/写入中/重启中/完成/失败，失败指明停在哪步 + 当前系统状态。
8. 异常边界：写中崩溃、BGI 被手动关/开、多账号同时写。

---

## 5. 风险与回滚

| 风险 | 最坏情况 | 缓解/回滚 |
|---|---|---|
| BGI 起不来（写坏配置） | 挂机中断 | 写前备份 + 原子替换；回滚=从 .bak 复制回原路径 |
| 任务中断致游戏内损失 | 掉落/树脂损失 | 写前「运行中」警告 + 取消选项；默认不写正在跑的组 |
| 写入失败 | 半写坏文件 | 临时文件 + Move 原子性；失败保留备份 |
| 关 BGI 失败 | 写被覆盖 | 超时回滚为不写 + 提示手动关闭 |
| 重启失败 | 以为在跑实际没跑 | 明确「未启动」+ 手动启动指引 |

回滚步骤：从 .<name>.bak-* 恢复（重命名去前缀）。
不建议在此页面做的：正在挂机时改「正在跑的那个配置组/一条龙」——写前检测到应默认阻止并强提示。

---

## 6. 版本识别与能力集

### 6.1 识别方案（用户定稿）

- 数据源：<BGI根>\BetterGI.exe 的 VersionInfo.FileVersion / ProductVersion / CompanyName（实测 Company 全为 BetterGI）。
- 判定：ProductVersion 含 lcb（不分大小写）→ 茶包版；不含 → 公版；读不到 → 未知（按最小公版能力集）。
- 基础版本号（0.61.2/0.67.0/0.67.2）单独解析，决定功能可用性，与茶包/公版两维。
- 界面提示：BGI 0.67.2 · 茶包版 (lcb.23.2-NexusBGI-fix3)。
- 缓存/刷新：文件大小 + 最后写入时间 + 版本字符串做指纹，exe 更新后重读；每账号独立识别。

### 6.2 能力集（字段 × 版本矩阵，第一手实证）

**项目类型能力表（源码 TypeDescriptions + 5 套真实配置统计）**：

| 类型ID | 显示名 | 最低版本 | 依赖字段 | 添加菜单项 | 版本差异实证 |
|---|---|---|---|---|---|
| Javascript | JS脚本 | 全版本 | folderName（唯一标识）+ jsScriptSettingsObject | 添加JS脚本 | A1=55/A2=6/A3=6/A4=10/root=53 |
| Pathing | 地图追踪 | 全版本 | name（路线 JSON）+ folderName（相对目录） | 添加地图追踪任务 | A1=34/A2=3/A3=1/A4=4/root=13 |
| Shell | Shell | **≥0.67.0**（0.61.2 无） | name=命令 | 添加Shell | A1=6/A2=1/A3=1/A4=0/root=**0** |
| SoloTask | 独立任务 | **≥0.67.2 有 / 0.67.0 无**（A3 无） | folderName=独立任务名 + soloTaskSettingsObject | 添加独立任务 | A1=6/A2=1/A3=**0**/A4=5/root=12 |
| KeyMouse | 键鼠脚本 | 全版本 | name=文件名（=folderName） | 添加键鼠脚本 | （5 套均未出现，属可选） |

**两条硬结论（版本能力集第一手事实，非猜测）**：
1. `SoloTask` 不是全版本都有——0.67.0（A3）配置里完全无 SoloTask → 编辑器在 A3 上必须隐藏/禁用「添加独立任务」。
2. `Shell` 也不是全版本都有——0.61.2（root）无 Shell 项目 → 同理隐藏「添加 Shell」「Shell 执行配置」。
3. 同一渠道（都 lcb）内部 0.61.2/0.67.0/0.67.2 能力也不同 → 能力集矩阵必须两维（渠道 × 基础版本）。

**独立任务 8 枚举 → 中文名 → 界面表现**（来源 IndependentTaskEnum.cs + Dispatcher.cs switch 注释）：
| 枚举 | 中文名 | 界面（添加独立任务子菜单） |
|---|---|---|
| AutoGeniusInvokation | 七圣召唤 | 独立任务子项 |
| AutoWood | 自动伐木 | 独立任务子项 |
| AutoFight | 自动战斗 | 独立任务子项 |
| AutoDomain | 自动秘境 | 独立任务子项 |
| AutoTrack | 自动追踪 | 独立任务子项 |
| AutoTrackPath | 自动追踪-路线 | 独立任务子项 |
| AutoMusicGame | 自动音游 | 独立任务子项 |
| AutoPathing | 自动寻路 | 独立任务子项 |

茶包独有字段仍待上游全文 diff（本轮已确认 Shell 上游也有、SoloTask 是版本差异非渠道差异），实现任务做精确校验。

### 6.3 界面表现（不支持的能力）

- 结论：置灰 + 悬停说明（不是隐藏）。理由：隐藏让用户疑惑「为什么别人有我没有」，且高版本切低版本时无法解释字段去向。
- 打开编辑器顶部显示版本栏。
- 高版本切低版本检测到「不支持的字段」→ 提示，不静默丢弃。

### 6.4 写盘保护（能力集校验）

- 向低版本 BGI 写含高版本字段的配置，写前做能力集校验，发现不兼容字段 → 警告 +「保留/剔除」选择。

---

## 7. 与现有 SoulLink 代码的复用分析

| 文件 | 行数 | 职责 | 复用 |
|---|---|---|---|
| Views/FlowEditorWindow.xaml.cs | 2751 | SoulLink 自己 AutomationFlow PLC 编辑器 | 不能复用为 BGI 一条龙编辑器；仅借鉴标红校验/脏状态交互 |
| Views/MyDepartWindow.xaml.cs | 421 | BGI 配置组启动器（ListBgiConfigs/BgiPathOf） | 复用「列组 + 启动」 |
| Views/AutomationPage.xaml.cs | 57 | AutomationProfile 薄壳 | 与 BGI 配置组无关 |
| Services/BgiDataCenter.cs | 243 | 读 BGI 组名/一条龙名/快捷键 | 复用组名枚举/路径解析；新增读全文 + exe 版本识别 |

---

## 8. 分阶段实现建议（P0/P1/P2）

### P0（读 + 骨架）
- 统一「任务编辑」页 + 左树右画布 + 版本栏；只读加载配置组/一条龙/组详情/拾取名单；exe 版本识别（lcb/公版/未知）。
- 验收：只读浏览三类对象，字段与 BGI 文件一一对应，版本栏正确，不写任何文件。

### P1（编辑 + 保存 + 校验 + 写入流程，核心）
- 配置组详情编辑、一条龙编辑、拾取名单；字段级校验 + 标红；未保存保护。写入走 §4.4「先关 → 写 → 重启」全流程。
- 验收新增：
  - 模拟「BGI 正在跑任务时点保存」→ 弹中断警告，取消后不写任何文件；
  - 模拟「关闭 BGI 失败」→ 回滚提示且配置未改；
  - 模拟「写入成功但重启失败」→ 明确「BGI 未启动」+ 手动启动指引 + 备份保留；
  - 正常路径：写入 → 重启 → 任务恢复，全程状态可见。

### P2（启动 + 组合 + 版本能力集精修）
- 启动一条龙/配置组、快捷组合、复制/分类/收藏/排序、「从此执行」；能力集字段×版本矩阵落地（置灰+说明+写盘校验）。
- 验收：统一页内能启动/组合/分类，覆盖三页全集（除 BgiFlow 空壳），茶包独有字段正确置灰并写盘校验。

---

## 9. 决策定稿记录（用户 2026-11-25 最终裁定，全部已定稿）

| # | 议题 | 定稿 | 依据 |
|---|---|---|---|
| 1 | 页面形态 | **网页**（与数据中心/miyoushe 同源） | 推荐默认 |
| 2 | 仿 BGI 原生粒度 | 保留心智模型重排版（字段分组/一周表格/标红校验对齐，配色排版自定） | 用户裁定 B |
| 3 | 拾取名单归属 | 独立 Tab | 推荐默认 |
| 4 | BGI 运行时编辑策略 | 先关 → 写 → 重启 + 提示 | 用户裁定 B |
| 5 | 一条龙与组合边界 | 组合保留独立概念 | 推荐默认 |
| 6 | 字段校验权威来源 | 源码范围校验 + 字段实际约束逐步补齐 | 源码级 |
| 7 | 1:1 复刻 vs 重排版 | 保留心智模型重排版 | 用户裁定 B |
| 8 | 写 BGI 策略 | 先关 → 写 → 重启 + 提示 | 用户裁定 B |
| 9 | 茶包独有字段 | 能力集两档（茶包/公版）+ 基础版本号；Shell 上游也有 | 源码 + 5 套配置差分 |
| 10 | 独立进程形式 | **SoulLink.TaskEdit.exe**（复用 Agent 框架依赖+单文件发布），监听 AppSettings.WebPort | 用户确认推荐 |
| 11 | 控制面通道 | **IPC 命名管道**（AppIdentity.IpcPipeName） | 用户确认推荐 |
| 12 | 关/启 BGI 编排 | **独立服务编排 + 主程序执行** | 用户确认推荐 |
| 13 | 二次确认 | **要，且分级** | 用户确认推荐 |
| 14 | 茶包战斗引擎开关字段名 | 交实现任务（对比两版配置差异） | 用户确认 |
| 15 | 远程鉴权 | **仅回环免密、非回环强制令牌** | 用户确认推荐 |
| 16 | 远程可编辑范围 | **可编辑所有账号**（体验与本机一致） | 用户确认 |
| 17 | 对外端口 | **使用 AppSettings.WebPort**（用户已配好的那个），不新开 18800 | 用户裁定 |

## 10. Is*Expanded 字段的处理结论

- 沿用读，不沿用写：读 Is*Expanded 以便默认展开态与 BGI 一致。
- 折叠状态不算配置变更：保存时回写这些字段（保持与 BGI 一致、不触发 dirty），但用户点折叠/展开不计入未保存修改。
- 与 BGI 同时打开的互相覆盖：折叠状态被 BGI 退出写回覆盖，属低风险（只是展开偏好，非任务数据），接受。

---


---

## 11. 进程与部署架构（数据面 / 控制面分离，用户硬约束）

### 11.1 用户两条架构决策

1. 「主程序只给网页一个地址，网页要读取东西写入东西不用经过主程序。网页自己读写修改本地文件。避免造成主程序卡顿。」
2. 「另外网页需要关闭或者启动，可以通过 api 向主程序发送通知。这个不属于网页的功能。」

→ 准确含义：**配置读写（数据面）不经过主程序**；**启停（控制面）必须经过主程序**（只有主程序知道会话/进程/授权状态）。浏览器 JS 不能直接写本地文件，所以「网页自己读写」=「由独立本地服务读写，而不是主程序」。

### 11.2 组件关系图

```
                 ┌─────────────────────────────────────────────┐
                 │  浏览器（任务编辑页，前端 JS 渲染表单/菜单）  │
                 └───────┬──────────────────────┬──────────────┘
                  数据面 │（粗实线，高频）        │ 控制面（细虚线，低频）
                         ▼                      ▼
              ┌──────────────────┐      ┌──────────────────────────┐
              │ 独立本地服务      │      │ 主程序（WPF 进程）        │
              │ SoulLink.TaskEdit│      │ · 会话/进程/授权状态       │
              │  · 读写 JSON/txt  │      │ · 启动/停止配置组/一条龙  │
              │  · 校验/备份      │      │ · 关闭/启动 BGI           │
              │  · 原子写         │      │ · 只读状态查询（内存快照） │
              └───────┬──────────┘      └──────────┬───────────────┘
                      │                             │
                      ▼                             ▼
              ┌──────────────────┐            ┌─────────────┐
              │ 本地文件          │            │ BGI 进程 / 会话 │
              │ User/ScriptGroup/ │            └─────────────┘
              │ User/OneDragon/   │
              │ JS settings…      │
              └──────────────────┘
              │
              │  主程序只连一条虚线：给地址 + 令牌（不碰配置内容）
              ▼
        （主程序 → 浏览器：网页地址 + 鉴权令牌，仅此而已）
```

### 11.3 职责矩阵

| 职责 | 网页 | 独立服务 | 主程序 | 归类 |
|---|---|---|---|---|
| 读配置 / 写配置 / 校验 / 备份 / 原子写 | ✗ | ✅ | ✗ | 数据面（独立服务） |
| 编辑界面、表单渲染、菜单/右键 | ✅ | ✗ | ✗ | 网页 |
| 启动/停止配置组、一条龙、BGI、切换会话 | 触发器（发通知） | 编排顺序 | ✅ 执行 | 控制面（主程序） |
| 关闭/启动 BGI（写配置前） | ✗ | 请求「请关/启 BGI」 | ✅ 实际关闭/启动并回执 | 控制面 |
| 查询当前运行状态 | 显示 | ✗ | ✅ 只读内存快照（不新增文件 IO） | 控制面 |
| FileSystemWatcher（文件变更监听） | ✗ | ✅ | ✗ | 数据面 |

**「关 BGI 写配置」归类结论**：独立服务检测到需要写 → 请求主程序「请关闭 BGI」→ 主程序关闭并回执 → 独立服务写入 → 请求主程序「请启动 BGI」。**关/启动作归主程序，编排顺序归独立服务**。

### 11.4 托管方案评估（候选 A/B/C）

| 候选 | 描述 | 能否「主程序不参与配置读写」 | 取舍 |
|---|---|---|---|
| A 主程序内 HttpListener | 现有 WebServer 模式，主程序在中间 | ❌ 否，主程序进程内做 IO | 违背用户硬约束 |
| B 独立进程 SoulLink.TaskEdit.exe | 专托管任务编辑页 + 本地文件读写 API | ✅ 是 | **推荐** |
| C 扩展 WebDirectToAgent | 复用 Agent 端口映射 | ⚠️ 半解耦 | Agent 职责是画面/输入，混入配置读写易耦合 |

**推荐 B（定稿）**：独立进程 `SoulLink.TaskEdit.exe`（复用 Agent 工程「框架依赖 + 单文件」发布模式）。**由它监听 `AppSettings.WebPort`（用户已配好的对外端口），主程序不再绑 `http://+:`，改为只绑回环 `http://127.0.0.1:<内部端口>/`**。主程序只「启动它 / 给地址 / 传递令牌」，完全不参与 HTTP/配置读写。

### 11.5 通知 API（控制面，网页/独立服务 → 主程序）

- **通道**：优先复用现有 IPC 管道（`AppIdentity.IpcPipeName`，Services/AppIdentity.cs:156）；主程序不开新的配置读写 HTTP 端点。
- **指令清单**：启动配置组 / 停止配置组 / 启动一条龙 / 停止一条龙 / 关闭 BGI / 启动 BGI / 查询当前运行状态。
- **幂等与并发**：重复点启动 → 主程序按「已有任务在跑」返回 busy；两页面同时发 → 主程序串行队列（FIFO）。
- **回执与状态**：异步 + 状态回执（成功/失败/超时）；网页轮询或订阅状态，失败原因回给网页。
- **鉴权**：令牌（主程序生成、每次启动/按需刷新），独立服务持令牌调用；主程序校验来源。
- **超时降级**：主程序没响应/已退出 → 网页显示「主程序未运行/无权限」，启动按钮置灰，不假装成功。

### 11.6 生命周期与端口

- 独立服务**按需启动**（用户打开网页时），随主程序退出时停止（或空闲超时自停）。
- **对外端口 = `AppSettings.WebPort`**（Models/Models.cs:663，默认 0=不启动；用户已配好的那个）。独立服务独占监听 `http://+:<WebPort>/`（所有网卡），用户路由器/openP2P 只转发这一个端口。
- **主程序只绑回环** `http://127.0.0.1:<内部端口>/`，不可从外部访问。
- 崩溃：主程序检测独立服务心跳，崩溃则重启或提示用户。
- 崩溃：主程序检测独立服务心跳，崩溃则重启或提示用户。

### 11.7 数据一致性（原子写 + 与 BGI/主程序并发）

- 原子写：临时文件 + File.Move(overwrite:true)（对齐 BGI 自己的 WriteToFileAtomically）。
- 写前检测 BGI 是否在跑（独立服务查 BGI 进程，或经主程序查询）；在跑则走 §4.4 先关→写→重启。
- 主程序数据中心只读快照，与独立服务写文件用「文件共享读」共存；大 JSON 解析在独立服务，不阻塞主程序 UI。

### 11.8 性能红线（写进设计约束）

- 主程序 UI 线程**零文件 IO**（配置读写全部独立服务）。
- 大 JSON（一条龙 100+ 字段、配置组 11KB+）解析在独立服务。
- FileSystemWatcher 放独立服务，不在主程序加。
- 页面首屏加载目标：独立服务预索引 + 前端增量渲染（具体数值待实现基准测，不拍脑袋）。

---

## 12. 项目右键菜单矩阵（按项目类型，源码 + 截图双证据）

> 用户点名重点：「独立任务右键是修改独立任务，js 右键是修改 js 配置」「地图追踪右键还有批量删除」——右键菜单**按项目类型不同**。

### 12.1 项目类型 × 可用菜单项矩阵

| 菜单项 | Javascript | Pathing(地图追踪) | KeyMouse | Shell | SoloTask(独立任务) | 证据 |
|---|---|---|---|---|---|---|
| 添加JS脚本 / 添加地图追踪任务 / 添加键鼠脚本 / 添加Shell / 添加独立任务 | 通用（列表空白处右键） | 同左 | 同左 | 同左 | 同左 | ABGI GroupDetailDialog + 截图4 |
| 下一次任务从此处执行 | ✅ | ✅ | ✅ | ✅ | ✅ | ScriptGroupProject.NextFlag + 截图4 |
| 修改通用配置 | ✅ | ✅ | ✅ | ✅ | ✅ | 截图4（= 打开配置组设置 ScriptGroupConfigView） |
| 修改独立任务配置 | ✗ | ✗ | ✗ | ✗ | ✅ | 截图4/5（SoloTask 专属，弹 SoloTaskSettingsObject 表单） |
| 修改JS脚本自定义配置 | ✅ | ✗ | ✗ | ✗ | ✗ | 截图6（Javascript 专属，弹 JsScriptSettingsObject 动态表单） |
| 打开所在目录 | ✅ | ✗ | ✗ | ✗ | ✗ | 截图6（Javascript 专属） |
| 批量删除 | ✗ | ✅（地图追踪专属） | ✗ | ✗ | ✗ | 用户点名 + 截图4 |
| 移除 | ✅ | ✅ | ✅ | ✅ | ✅ | 通用 |

### 12.2 对应源码工厂方法

| 类型 | 工厂方法（ScriptGroupProject.cs） | 关键字段 |
|---|---|---|
| Javascript | ScriptGroupProject(ScriptProject) | folderName=脚本文件夹唯一标识，jsScriptSettingsObject=动态表单 |
| Pathing | BuildPathingProject(name, folder) | name=路线 JSON，folderName=相对目录 |
| KeyMouse | BuildKeyMouseProject(name) | name=文件名（=folderName） |
| Shell | BuildShellProject(command) | name=命令 |
| SoloTask | （配置里 type=SoloTask） | folderName=独立任务名，soloTaskSettingsObject=专属配置 |

---

## 13. 独立任务来源（内置 8 项 + JS 注册的自定义项）

### 13.1 内置 8 项（IndependentTaskEnum.cs，无中文注释）

| 枚举 | 中文名（Dispatcher.cs switch） |
|---|---|
| AutoGeniusInvokation | 七圣召唤 |
| AutoWood | 自动伐木 |
| AutoFight | 自动战斗 |
| AutoDomain | 自动秘境 |
| AutoTrack | 自动追踪 |
| AutoTrackPath | 自动追踪-路线 |
| AutoMusicGame | 自动音游 |
| AutoPathing | 自动寻路 |

### 13.2 自定义独立任务（截图1 出现「联机锄地」「好盛任务自动完成」等）

- 这些**不在 IndependentTaskEnum 里**，是 JS 脚本注册的自定义独立任务。`SoloTask` 类 = `{Name, Config(object)}`（SoloTask.cs），Name 是独立任务名，Config 是脚本对象。
- 来源：JS 脚本通过 `Dispatcher.RunTask(SoloTask)` 注册（Dispatcher.cs 的 switch 只覆盖内置 8 项，**自定义名走 default 分支**）。
- 实测 A1 `好感.json` 的 SoloTask 项目：`{name:好感任务自动完成, type:SoloTask, soloTaskSettingsObject:{enemyType:盗宝团, runTimes:10, partyName:好感, …}}`——**自定义独立任务名 + soloTaskSettingsObject 结构由注册它的脚本决定**。
- **「添加独立任务」下拉框的数据源**：内置 8 项 + 扫描发现的 JS 注册独立任务。注册/发现机制需实现任务进一步定位（Dispatcher 的注册入口），**本轮不臆断，标注「需实现任务精确核实注册表来源」**。

---

## 14. JS 设置 schema（每个 JS 的设置项目，用户硬需求）

### 14.1 schema 来源（源码级）

- `ScriptProject.cs`：JS 脚本 = `manifest.json`（`Manifest` 类），其中 `SettingsUi` 字段指向一个 JSON 文件，`LoadSettingItems()` 读它得到 `List<SettingItem>`。
- `Manifest.cs` 字段：`ManifestVersion/Name/Version/BgiVersion/Description/Authors/Main/SettingsUi/Scripts/Library/SavedFiles/HttpAllowedUrls`。
- `ScriptProject.LoadSettingUi(context)` 遍历 SettingItem → 动态生成 WPF 控件（ScrollViewer + StackPanel）。

### 14.2 SettingItem 六种类型（SettingItem.cs，动态表单的类型全集）

| Type | 控件 | 字段 | 说明 |
|---|---|---|---|
| separator | Separator | — | 分隔线 |
| input-text | TextBox | Label/Default | 文本输入 |
| select | ComboBox | Options/Default | 单选下拉 |
| checkbox | CheckBox | Default(bool) | 布尔勾选 |
| multi-checkbox | WrapPanel 多 CheckBox | Options/Default(List<string>) | 多选 |
| cascade-select | CascadeSelector | CascadeOptions/Default | 级联选择 |

通用字段：`Name`（绑定到 JsScriptSettingsObject 的键）、`Type`、`Label`（中文标签）、`Options`、`CascadeOptions`、`Default`。

### 14.3 我们要复刻的动态表单

- 读 `manifest.json` 的 `SettingsUi` → 读该 JSON → 按 SettingItem 六种类型渲染前端表单控件（select→下拉、multi-checkbox→多选组、cascade-select→级联、checkbox→开关、input-text→输入框、separator→分隔线）。
- 绑定目标 = `ScriptGroupProject.JsScriptSettingsObject`（ExpandoObject，落库为 JSON 对象）。
- **精准生成**：保存时按 SettingItem 的 Name 序列化回 JsScriptSettingsObject，`CleanInvalidSettingsValues` 会清理 multi-checkbox 里不在 Options 的值（ScriptGroupProject.cs 源码）——我们也照做。

---

## 15. 战斗配置与茶包差异（AutoFightConfig，源码级）

### 15.1 AutoFightConfig 全字段（GameTask/AutoFight/AutoFightConfig.cs）

| 字段 | 默认值 | 说明 |
|---|---|---|
| StrategyName | 根据队伍自动选择 | 战斗策略（脚本仓库下拉） |
| TeamNames | "" | 强制指定队伍（逗号分隔） |
| FightFinishDetectEnabled | true | 自动检测战斗结束 |
| ActionSchedulerByCd | "" | 根据技能CD优化出招人员（大文本框） |
| OnlyPickEliteDropsMode | Closed | 只拾取精英掉落（Closed/AllowAutoPickupForNonElite/DisableAutoPickupForNonElite） |
| FightFinishDetectConfig.* | 见下 | 战斗结束检测子配置 |
| PickDropsAfterFightEnabled | true | 战后扫描掉落 |
| KazuhaPickupEnabled | true | 万叶拾取 |
| SwimmingEnabled | true | 游泳检测 |
| ExpBasedPickupEnabled | false | 基于经验值判断战后拾取 |
| Timeout | 120 | 战斗超时（秒） |
| EnableCombatTargeting | false | 战斗中持续索敌 |
| DamageNumberRecognitionMode | Color | 伤害数字识别（Disabled/Ocr/Color） |

FightFinishDetectConfig 子字段：FastCheckEnabled / RotateFindEnemyEnabled / FastCheckParams / CheckAfterSwitchAvatar / CheckEndDelay / BeforeDetectDelay / RotaryFactor(1-13,默认12) / PaimonEndCheckEnabled / BlockCheckBeforeBattleSeconds 等（源码级）。

### 15.2 茶包差异（截图3 的「使用公版自动战斗引擎」开关）

- 截图3 的「使用公版自动战斗引擎」开关**不在 AutoFightConfig.cs 里**，是茶包版新增字段（公版 BGI 无此开关）。
- **这是能力集「茶包独有」的第一个确凿例子**，但确切字段名需进一步定位（可能在 PathingPartyConfig 或茶包新增的 wrapper 字段），**本轮标注「需实现任务精确定位字段名」**，不臆断。
- 结论：战斗配置面板必须按「渠道 × 基础版本」能力集渲染，「使用公版自动战斗引擎」开关只对茶包版显示。

---


## 16.0 反面教材与「字段真实性」核对方法（硬性要求）

★ **禁止从示例推导字段名**：t26 曾照文档示例编造了 24 个一条龙字段（AutoResinEnabled / AutoDomainEnabled / AutoBossEnabled 等），真实配置里根本不存在，现已改正。**任何字段名都必须以真实配置文件为唯一依据**——文档里的字段名是「已核实的记录」，不是「可以照抄的模板」；实现时若发现某字段不在真实文件里，先核实真实文件，不要臆造。

**如何核对字段真实性（命令/步骤）**：
1. 枚举真实键集合（只读，不写）：
   ```powershell
   # 一条龙：所有 OneDragon json 的去重顶层键
   Get-ChildItem 'D:\AAA挂机专区\A1\BetterGI\User\OneDragon\*.json' | ForEach-Object {
     (Get-Content $_.FullName -Raw | ConvertFrom-Json).PSObject.Properties.Name } | Sort-Object -Unique
   ```
2. 嵌套键：对单个文件递归展开（config → pathingConfig → autoFightConfig …），每个嵌套对象单独列键。
3. 新增字段前，先确认它在「真实键集合」里；不在 → 要么是 BGI 新版新增（需拿新版文件核实），要么是臆造（删）。
4. 字段名大小写以真实文件为准（全 PascalCase）；读取大小写不敏感、写回原样。

## 16. 精准生成文件（字节级兼容，用户硬需求）

### 16.1 每个可编辑对象的文件路径

| 对象 | 文件路径 | 序列化 |
|---|---|---|
| 配置组 | User/ScriptGroup/<名>.json | ScriptGroup.ToJson（System.Text.Json）+ WriteToFileAtomically |
| 一条龙 | User/OneDragon/<名>.json | OneDragonFlowConfig（ConfigService JSON） |
| JS 脚本设置 | 存于 ScriptGroupProject.JsScriptSettingsObject（在配置组 JSON 内，不单独成文件） | ExpandoObject → JSON 对象 |
| 独立任务配置 | 存于 ScriptGroupProject.SoloTaskSettingsObject（在配置组 JSON 内） | 同配置组 |
| 拾取名单 | User/config.json（autoPickConfig）+ User/pick_black_lists.txt、pick_fuzzy_black_lists.txt | txt 逐行 |

### 16.2 序列化规则（对齐 BGI，字节级兼容）

- 编码：UTF-8 无 BOM（ScriptGroup.cs `Utf8WithoutBom`）。
- 原子写：临时文件 `.{name}.{guid}.tmp` → File.Move(overwrite:true)。
- 字段顺序：与 C# 类声明顺序一致（System.Text.Json 默认按属性声明序）。
- null：System.Text.Json 默认**忽略 null**（配置组 ToJson）；但「配置组」反序列化用 Newtonsoft（FromJson），两套序列化器并存 → 我们生成时要**同时兼容两套**（关键：字段名大小写、枚举值字符串、bool 序列化）。
- Index 字段维护：配置组 `Index` 是排序权重（A1 实测 1~10+），新建/排序时按 BGI 规则维护（重排连续值，对齐 RouteLedger.SortOrder 语义）。
- **必须能 BGI 无损读取**：生成结果用 BGI 自己的 `FromJson` 反序列化不报错、字段齐全、类型正确（实现任务的验收点）。

### 16.3 备份策略（用户 2026-11-25 定稿：主程序目录 /data，独立目录）

- **备份根**：`<AppIdentity.DataDir>\backup\`，子目录 `scriptgroup\` / `onedragon\` / `picklist\`。
- **文件名**：`<原名>.yyyyMMdd-HHmmss.bak`；保留最近 **N=10** 份（可配置），超出删最旧。
- **绝不写进 BGI 的 `User\ScriptGroup` / `User\OneDragon`**（用户生产目录，BGI 自己已积累 43 个 .bak，我们不能再往那堆）。
- **附带说明**：`data\` 本来就被更新包排除（AGENTS.md 规则），备份不会被打包带走。

---

---

## 17. 远程访问与性能（端口转发 / openP2P / 组网，与本机体验一致）

> 用户要求：用户会端口转发、openP2P、组网访问这个网页，体验必须与本机一致。

### 17.1 网络定位

- **一个端口访问整个网页**：独立服务 `SoulLink.TaskEdit.exe` 独占监听 `AppSettings.WebPort`（`http://+:<WebPort>/`），数据中心、米游社、任务编辑、未来所有页面全经这同一端口；用户路由器/openP2P 只转发这一个端口。
- 独立服务处理两类请求：`/taskedit/*`（自处理，读写本地 JSON/txt）；其余（`/`、`/api/bgi/*`、`/api/miyoushe/*`、`/api/sessions`、`/api/status` 等）**反向代理**到主程序回环内部端口。
- **控制面链路不暴露 IPC**：远程用户点「启动配置组」= 浏览器 → 独立服务（远程可达）→ IPC → 主程序（本机）。IPC 本身不出本机，无需改造，链路依然成立。

### 17.2 强制鉴权

- **结论：强制口令/令牌，不允许「没设密码就放行」**。现有 WebServer 的 `IsAuthorized` 在 `PasswordEnabled()==false` 时直接 return true（远程可写却无口令），必须去掉这个短路。
- 方案：首次启动独立服务生成随机令牌并显示，用户输入一次后 cookie 持久化（复用现有 IssueSession 机制）；**仅回环 127.0.0.1 访问时可免密，非回环一律强制**。
- 防暴力：连续失败 N 次（如 5 次）后锁定 60 秒或指数退避延迟。

### 17.3 压缩（gzip/deflate）

- 现有 WebServer 完全没有 gzip/deflate（grep 0 命中）→ 是「远程体验不一致」最大来源。
- 必须加 gzip（按 `Accept-Encoding` 判断），JSON API 与页面都压。
- 预估收益：10 MB JSON 可压到 ~1 MB（配置/路线/拾取数据高度重复，压缩率高）。

### 17.4 分页与增量

- 远程链路 RTT 高，**禁止一次返回全部数据**。
- 路线/拾取/报错列表分页（每页 50~100 条），详情按需加载。

### 17.5 传输体积预算

- 首屏 HTML+CSS+JS 总量目标 < 500 KB（gzip 后 < 150 KB）；单次 API 响应上限 1 MB（gzip 后 ~200 KB）。
- 依据：远程 3G/4G 下 1 MB 响应约 1~3 秒可接受；超过则分页。

### 17.6 断线与重连

- 请求超时重试；断线后重连恢复；长操作（读写配置、关 BGI）要有进度 + 幂等，不能断线导致「配置写一半」。
- 表单草稿本地暂存（localStorage），断网 30 秒恢复后界面自动重连且不丢已填内容。

### 17.7 心跳与保活

- **需要心跳**：穿透工具（openP2P/Tailscale 空闲断连常见）需要保活。前端每 30 秒发轻量心跳，独立服务侧也可对穿透隧道做 keepalive。

### 17.8 安全边界（远程访问的额外确认/禁止）

- 远程点「关闭 BGI」→ 二次确认 + 显示当前任务状态（正在跑什么）。
- 是否允许远程编辑所有账号 vs 限定账号 → **默认限定到已授权账号，待用户拍板**。

### 17.9 防火墙

- **单端口规则**：独立服务启动时调 `WebFirewall.EnsureAllowed(WebPort)`，只放行 WebPort 一个端口（不再是端口范围）。内部回环端口（主程序）无需放行（回环不出防火墙）。

### 17.10 与本机体验一致的验收

- 远程与本机加载同一页面，功能完全一致（不阉割，除非用户明确要求）。
- 远程首屏加载目标：gzip 后 < 500 KB，3G/4G 下 < 5 秒可交互。
- 大列表（7 天路线、上千条拾取）远程翻页流畅（每页 < 100 条）。
- 断网 30 秒恢复，界面自动重连、表单草稿不丢。

---

---

## 17.11 已知限制：独立服务实例生命周期

- **主程序重启后独立服务的 key/实例生命周期**：当前按方案 b —— **不复用旧实例，结束旧实例起新的**。
- **旧版本实例无 `/taskedit/rebind` 时会撞 Mutex**（单实例互斥体名按身份隔离），需手动结束旧实例再启动。
- 这不是本任务要修，仅记录（实现任务若加 `/rebind` 热切换需注意与单实例互斥体的配合）。

---

## 18. 请求路由表与反向代理风险（最终技术判断）

### 18.1 请求路由表（哪些 → 独立服务自处理，哪些 → 反代主程序）

| 前缀/路径 | 处理者 | 说明 |
|---|---|---|
| `/taskedit/*`（或新路径前缀） | **独立服务自处理** | 任务编辑：读写本地 JSON/txt（数据面） |
| `/api/bgi/*`（ledger/stats） | 反代 → 主程序 127.0.0.1 内部端口 | 数据中心查询 |
| `/api/miyoushe/*`（含 /mora/*） | 反代 → 主程序 | 米游社 |
| `/api/sessions` / `/api/status` / `/api/live` | 反代 → 主程序 | 会话/状态 |
| `/`（首页 HTML） | 反代 → 主程序 | 现有主页 |
| `/api/pagever` / 其他现有 API | 反代 → 主程序 | 现有页面 |

### 18.2 反向代理的技术风险与结论

| 场景 | 风险 | 处理 |
|---|---|---|
| 流式画面 `/stream`、`WriteStreamWithRange` | 大响应流式转发，独立服务若缓冲会爆内存 | 独立服务**零拷贝流式转发**（chunked 透传，不整段缓存），仅透传响应头 |
| WebSocket 升级（若未来有） | HttpListener 不原生支持 WS 升级转发 | 当前无 WS 页面；未来需要则独立服务改为 Kestrel 或主程序保留回环直连 |
| 大响应（录像/截图） | 内存/延迟 | 流式转发 + gzip 仅对 JSON/文本，不对二进制 |
| 鉴权 | 反代时令牌丢失 | 独立服务在反代前校验令牌，通过后加内部回环信任头/内部令牌转发 |

**结论：反代方案可行**。独立服务用 HttpListener 做**流式透明转发**（不整段缓冲），JSON/文本加 gzip，二进制/流式不做压缩；鉴权在独立服务统一拦截。若未来上 WebSocket，再评估升级为 Kestrel（此时也无需改「一个端口」架构）。

---
## 附：来源标注

- 茶包版源码（%TEMP%\bgi-fork-probe，git show 精读）：Core/Script/Group/{ScriptGroup,ScriptGroupConfig,ScriptGroupProject}.cs、Core/Config/OneDragonFlowConfig.cs、View/Pages/OneDragonFlowPage.xaml、View/Pages/View/ScriptGroupConfigView.xaml、View/Windows/Editable/ScriptGroupProjectEditor.xaml。
- 上游对照：babalae/better-genshin-impact ScriptGroupProject.cs（GitHub API，确认 Shell 上游也有）。
- 版本差分：A1/A2/A3/A4 User/OneDragon/*.json top-level 键集合实测（96~97 键，配置 JSON 无版本标记）。
- ABGI chunk：%TEMP%\autobgi-ref\（BgiFlow 272 字节空壳实读）。
- AutoBGI：D:\AAA挂机专区\A1\autobgi\main.json（Control.BgiCloseAction/BgiCloseScriptManagementType，只读）。
- SoulLink：FlowEditorWindow/MyDepartWindow/AutomationPage/BgiDataCenter（只读）。
- 未操作 A1/A4 会话、未反编译 exe、未运行 auto-bgi.exe。
