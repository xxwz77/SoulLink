# 击杀归类引擎实现（t17）：rule_version v4.1 + 两档归属 + 可重算汇总

> 日期：2026-11-25（t17，attempt d1c188aa）
> 依赖：t15 规则文档（MORA-KILL-ATTRIBUTION-20261125.md）+ t18 校准（MORA-CALIBRATION-20261125.md v4.1）
> 验证：tools/mora-verify（独立工程，隔离 %TEMP% DB）四场景 ALL-PASS；双 TFM Release 0 error

## 1. 模块位置与 rule_version 机制

- **规则模块**：`Services/MoraClassifier.cs`（~570 行）。`internal static class`，零 UI 依赖。
- **规则版本**：`MoraClassifier.RuleVersion = 41`（v4.1）。规则**数据驱动**：
  `EliteTierSet={200,400,600,800,1200,3000}`、`LegendaryEliteTiers={800,1200,3000}`、
  `LegendaryMinionThreshold=150` 全是常量表，调档位改这里，不动采集/解析代码。
- **版本共存**：结果表主键 `(entry_rowid, rule_version)`。重算=同版本 INSERT OR REPLACE 幂等；
  新版本号=新增一套结果，旧版本原样保留可对比（`QueryVersionsAsync` 列出全部版本）。
- **伴生模块**：`Services/MoraRouteWindowScanner.cs` —— 从 BGI 日志正文提取「进入路线循环 route="x.json"」
  →「脚本执行结束」秒级路线窗（RunRecordCollector 同源正则口径 + 同款文件名日期/单调跨日天轴）。

## 2. 类别判定（v4.1 自上而下，命中即止）

```
① single_morale ∈ {800,1200,3000}              → legendary_elite 传奇精英
② single_morale = 600 ∧ 路线名含「传奇」        → legendary_elite
③ single_morale = 600 ∧ 路线名含「精英」        → elite（普通精英）
④ single_morale ∈ {200,400,600}（其余情况）     → elite
⑤ single_morale ∉ 枚举集 且 >150               → legendary_minion 传奇小怪
⑥ 其余（≤150，含 0/负数）                      → minion 小怪
NULL single_morale：先试 morale÷count 整除近似（t18 §6.3，count 只做反推绝不做击杀数）；仍不可得 → unknown 不猜。
```
路线名只在 ②③ 用（600 细分）；**不因路线名无关键词否定 single_morale 判定**（t18 实测 A4 路线名无「精英/传奇」）。

## 3. kill_count 与汇总口径（用户裁定）

- `kill_count` **恒 1**，落库列注释明示；绝不拿 details.count 乘算。
- 汇总口径 = 各类别**记录条数**（`record_count`）；`total_morale` = 该组 num 之和（金额口径不受 kill 语义影响）。
- 汇总表 `my_mora_summary`：程序算好存好（分类跑完先删后写该账号该版本），查询端零减法。

## 4. 归属算法与两档置信度（t18 §Q5）

| 档 | 数据源 | 置信度 | 规则 |
|---|---|---|---|
| 路线文件级 route | 日志正文路线窗（进入路线循环→脚本执行结束，秒级） | medium | 分钟 [ts,ts+60s)∩窗：唯一命中→route；**多条=同分钟跨路线→route 不硬塞，mark=minute_overlap，回退项目档** |
| 项目级 project | BGI ExecutionRecords/YYYYMMDD.json（group_name+project_name+start/end，近 8 天） | high（唯一）/medium（重叠取最近） | ts∈[start,end]；0 条→unattributed |
| 都失败 | — | none | **unattributed，原始记录保留，绝不硬塞给最近路线** |

归属优先级：route > project > unattributed。窗口构建经 `WindowSourceForTest` 可注入（测试不读真实文件）；
生产读 `BgiPathResolver.GetBoundPath(account)` 下的 log\ExecutionRecords 与近 2 天日志。

> ★★ **minute_overlap 语义（Lead 已裁定保持，务必勿误读）**：同分钟跨多条路线窗时，
> 该记录**未**精确归属到路线（route_name=NULL），而是**回退到项目档**（attribution=project，
> confidence=high/medium）且 attr_mark=minute_overlap 保留。
> → 含义是「项目归属可信、路线细分失败」的证据链，**不是**「已精确归属到某条路线」。
> 消费方若只要路线级精确数据，应过滤 attr_mark='' 且 attribution=route 的行；
> 若接受项目级粒度，可直接用（mark 供再细分失败时溯源）。数据中心只留证据与置信度，不替消费方决策。

> ★★ **匹配率数字的适用范围**：§7 的 33%/42% 是**构造数据**（12 条人造记录 + 两个手工窗口）
> 下的验证输出，**不代表真实环境匹配率**。t18 §5.4 对真实数据的预估是项目级 >90%、
> 路线级 50~80%，但真实值要等 t21 凭据导入、真实账号全量数据跑过 ClassifyAsync 后重测。
> **任何后续文档/汇报不得把 33%/42% 当真实匹配率引用。**

## 5. 表结构（三类，均在 miyoushe.db，经 MiyousheStore.RunAsync 串行 gate）

- `my_mora_class`：明细（entry_rowid 引用原始行不复制 + rule_version + category + kill_count=1 +
  attribution/confidence/attr_mark + group/project/route + 冗余 ts/day/action/single/count/morale）。
- `my_mora_class_run`：每轮分类的统计（entries/classified/unknown/三档 hits/时间），可审计。
- `my_mora_summary`：(account,game_uid,day,rule_version,group_name,route_name) 五键四类计数+摩拉。
原始表 `my_ledger_entry` **一字不改**（归类是消费方规则不是事实）。

## 6. API 清单（全部鉴权后，WebServer.HandleMiyousheAsync）

| 方法 | 路径 | 参数 | 说明 |
|---|---|---|---|
| GET | /api/miyoushe/mora/rows | user, days=7, rule=41 | 归类明细（single_morale+路线→category/kill_count） |
| GET | /api/miyoushe/mora/summary | user, days=7, group?, route?, rule=41 | 汇总（四类记录条数+摩拉） |
| GET | /api/miyoushe/mora/versions | user | 已跑过的规则版本列表（对比） |
| POST | /api/miyoushe/mora/classify | user | 手动触发/重算（幂等） |

自动触发：`MiyousheService.RunForAccountAsync` 拉取完成（entries>0）后 fire-and-forget 调 ClassifyAsync，
失败不影响拉取（下次拉取或手动触发补上）。无 DispatcherTimer，无新增定时器。

## 7. 验证（tools/mora-verify，隔离 %TEMP% DB，四场景 ALL-PASS）

1) **边界**：200/400/600/800/1200/3000/151/150/149/0/-5/2147483647/null 全过；600×传奇路线/精英路线/A4 真实路线名三分支全过；
   fallback（morale÷count 整除 / 无 count→unknown）全过。
2) **端到端 12 条构造数据**：逐条列单见验证输出（single+route→category/kill=1/attribution/confidence）；
   kill_count 全 1 ✓；NULL→unknown 恰 1 ✓。
3) **归属两档**：route 档 5 条（07/08 盗罐者两条路线，medium）；project 档 4 条（high）；
   minute_overlap 2 条（同分钟跨两窗，route 不硬塞、回退项目档、mark 保留）；
   真 unattributed 3 条（凌晨无窗口/NULL，原始保留）。
   ⚠️ **33%/42% 是构造数据下的数字，不代表真实环境**（详见 §4 末尾两个声明框）；
   真实匹配率要等 t21 凭据导入后用真实账号全量数据重测（t18 §5.4 预估项目级 >90%、路线级 50~80%）。
4) **重算**：v40 人工旧行 + v4.1 重跑 → versions=40,41 共存，entry#1 双版本可查可对比。
5) **汇总**：四类+unknown 计数、record_count、摩拉总额与明细逐项一致（7823=7823）。

命令：`dotnet build SoulLink.csproj -c Release`（exit 0，双 TFM）；
`dotnet run --project tools/mora-verify -c Release`（ALL-PASS，输出全文见任务 update）。

## 8. 边界与诚实标注

- 真实账号全量数据的路线级匹配率、150~199 频次表、聚合粒度仍按 t18 §7 待真实账号验证（本实现不伪造）。
- minute_overlap 的处理=路线档细分失败回退项目档（mark 保留），符合 t18 §5.3「route_name=NULL」且比直接丢弃多保留一层信息；
  若 Lead 认为应一律 unattributed，改 `ClassifyCore` 归属优先级一处即可。
- `InternalsVisibleTo SoulLink.MoraVerify` 已加进 SoulLink.csproj（仅验证工程可见，发布不含该工程）。

## 9. Lead 裁定记录（2026-11-25）

- minute_overlap 语义（回退项目档 + mark 保留）经 Lead 裁定**保持现状**：比「一律 unattributed」
  多保留一层高置信信息，符合「数据中心只做采集归档、筛选规则归消费方」原则；消费方按 §4 声明框
  的口径自行取舍。
- MiyousheService.cs 的 8 行触发 hook 交由 Lead 统一协调 merge（t19/t21 也在改 Miyoushe* 文件），
  t17 侧不再动该文件。
- 匹配率 33%/42% 明确标注为构造数据（§4/§7 双处声明），真实率待 t21 后重测。


---

## 10. t22 修正：action 前置过滤（v4.2，2026-11-25）

### 10.1 真实数据证明 sm∈W 不充分

t21/本次直接只读解析 `D:\AAA挂机专区\A1\autobgi\archive.db` 的 `MoraleRecord_two.mark`：
action 与 single_morale 是独立维度。宝箱奖励 200/1200、其他 3000 都命中 W，但不是击杀。
正确入口：**IsKillAction(action) && 后续 single_morale 数值规则**。非击杀统一 `non_kill`，不参与击杀路线归属。

`KillActions` 常量表（t20 解包 + t21 实测出现）：精英怪、小怪、击杀怪物奖励、普通精英怪、中级精英怪、高级精英怪。
未列 action 不做包含/模糊猜测。action/tier 交叉不一致写 `attr_mark`：`elite_action_tier_mismatch` 或 `non_kill_action_elite_tier`。

`RuleVersion=42`（v4.2）；旧无 action 数值纯函数保留作 v4.1 对照，生产写库全部走带 action 重载。
`(entry_rowid,rule_version)` 保证 v4.1/v4.2 共存，原始 `my_ledger_entry` 不改。

### 10.2 两层计数口径（不可混用）

- `count`：米游社 **action×single_morale 档位桶内次数**（精英桶常见 8~129），不是一只怪。
- `kill_count`：用户既定口径，**每条归类记录恒 1**；绝不拿 count 乘算击杀数。
- `record_count`：该组所有原始记录条数（含 non_kill/unknown）。
- 新增 `kill_record_count`：四类击杀记录条数（排除 non_kill/unknown），`non_kill` 单列。
- 汇总自动补列 `non_kill/kill_record_count`；原始记录守恒：record_count = 四类 + unknown + non_kill。

### 10.3 800 与 150 的数据依据

- **800 极稀有但合法**：55/86641=0.06%，必须留在 `EliteTierSet`，不能因低频删掉。
- **150 是安全界不是实际档位**：真实小怪最小 32，32~147 连续密集，148/149/150 全空档；
  <150 占 99.86% 次数（432687/433284）。保留 `>150` 规则，不宣称 150 在数据里真实出现。

### 10.4 真实 details 验证与口径差异

本次实读 31 行（10 month + 21 day），7762 个 details 桶。
任务中的精英怪 86641 / 宝箱200 26102 / 其他3000 922 对应**10个月行**；
21个日行另含 7376 / 2032 / 60，所以全库合计为 **94017 / 28134 / 982**。
captain 补充中的 26105 与本次月行实读 26102 相差3，文档以可复跑实读为准，不改数字迎合预设。

全库按 `count` 次数前后对比（同一真实 details，无伪造）：

| 类别 | v4.1（无action过滤） | v4.2 |
|---|---:|---:|
| elite | 136923 | 108400 |
| legendary_elite | 6267 | 5279 |
| legendary_minion | 25201 | 695 |
| minion | 589550 | 573618 |
| non_kill | 0 | 69949 |

精英怪全部次数保留击杀类别；宝箱200、宝箱1200、其他3000 全部 non_kill，0 次被算成精英。
除精英误算外，非击杀的小额/大额也从旧 minion/legendary_minion 移出，这是统一 action 入口的预期效果。
action/tier 诊断 66 个桶；只标诊断，不静默改原始事实。

### 10.5 回归与版本/汇总

- 200/400/600/800/1200/3000/151/150/149/0/-5/int.MaxValue/null 全部符合预期；kill_count 全 1。
- v4.1/v4.2 共存可查；宝箱 elite→non_kill、其他 legendary_elite→non_kill，精英怪类别跨版本不变。
- 隔离汇总样本：原始4=击杀2+non_kill2；count 不参与击杀数。
- 全部 **21个真实日桶**守恒；最大日样本 2026-11-07：raw198=kill108+non_kill90。
- `RESULT T22 ALL-PASS RuleVersion=42`；双 TFM Release 0 error。

旧 `tools/mora-verify` 的数值/归属/kill_count/汇总仍通过；其版本场景硬编码“当前=41”，
在 v4.2 下自然得到 versions=40,42 并报旧断言失败。未篡改旧测试制造全绿；t22 独立探针显式构造 41/42 完成共存验收。

