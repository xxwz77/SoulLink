# 摩拉逐条记录真实结构与归类规则校准（t18）

> 日期：2026-11-25
> 状态：校准报告（t18）。依据 t20 ABGI 解包 + 用户札记截图 + BGI ExecutionRecords 真实文件 + t16 已落库字段，**不依赖用户再扫码**。
> 关联：订正 t14（MIYOUSHE-INTEGRATION-20261125.md 已追加 §11）；对 t15（MORA-KILL-ATTRIBUTION-20261125.md）的归类规则做最终校准。

---

## 0. 一句话结论

monthDetail **确有逐条记录**，字段含 single_morale（单次摩拉）+ count（次数）+ morale（总额）+ action（途径）。因此 **600 的「600×1 vs 200×3」在数据层即可消解**：single_morale 直接给出单次值，命中枚举集 {200,400,600,800,1200,3000} 即精英。路线名判别**不再是必需信号**（但仍是可用的交叉校验）。同时发现一个**关键限制**：BGI 的 ExecutionRecords/YYYYMMDD.json 只到**项目/脚本级**（group_name + project_name），**不含路线文件名**，而摩拉记录的分钟时间戳与项目级时间窗做 join 时，一个项目内多路线无法进一步细分。

---

## 1. 逐条记录真实结构（Q1）

### 1.1 字段（来源：t20 ABGI 解包 + t16 已落库 my_ledger_entry）

monthDetail（国服）返回 details 数组，元素形如：

```json
{
  "action": "击杀怪物奖励",
  "single_morale": 600,
  "count": 1,
  "morale": 600
}
```

- 另附 time（分钟精度）、action_id（t16 落库；t20 称击杀判定 action_id=37 或击杀类词表）。
- t16 落库字段（IMPL-20261125-MIYOUSHE-CORE.md §5）：action_id/action/num、原始 time 文本、分钟级 ts_ms、来源、页内 source_ordinal；single_mora（兼容 single_morale）与 count **可空**——接口不返回就 NULL，不填 0。

### 1.2 一条记录对应什么

- **一条记录 = 一次「获取途径 × 单次摩拉 × 次数」的聚合桶**，不是每只怪的独立流水。
- 例：同分钟打 3 只 200 摩拉的怪，可能落成一条 {action:击杀怪物奖励, single_morale:200, count:3, morale:600}；也可能是三条 {single_morale:600, count:1, morale:600}——**取决于米游社侧聚合粒度**，这是唯一仍需真实账号确认的点（见 §7）。

### 1.3 时间戳精度

- **到分钟，无秒**（用户截图三列「时间」只到 13:40 / 13:38；t16 落库 ts_ms 分钟级一致）。
- 因此归属到「路线级」最多到分钟窗口，秒级细分不可得。

### 1.4 单日体量

- 截图样本约 9 条（一个片段）；ABGI 落库 mark JSON 有 **414 条档位桶**（t20，跨多天/多账号的累计档位，非单日）。单日单账号量级需真实账号确认（§7），按挂机强度估算在**数十到数百条**，分页 page_size=100 足够覆盖。

---

## 2. 数值分布与规则自洽（Q2 + 规则校准）

### 2.1 已确认的取值（截图 + t20 解包）

| 数值 | 类别（v4 规则） | 证据 |
|---|---|---|
| 600 | 精英（需看 single_morale / 路线名消解） | 用户截图多次出现 600 |
| 38 / 40 / 45 | 小怪（≤150） | 用户截图 |
| 200 / 400 / 600 / 800 / 1200 / 3000 | 精英档位集合 | t20 解包：精英 = 单次摩拉 ∈ {200,400,600,800,1200,3000}，上限 3000 |

### 2.2 规则自洽校验（用户 v4 六条 × 真实证据）

| 规则 | 是否有对应真实数据 | 结论 |
|---|---|---|
| ① 800/1200/3000 → 传奇精英 | ✅ t20 档位集合含此三值 | 自洽 |
| ② 600+路线名含传奇 → 传奇精英 | ⚠️ 见 §3 | **被 single_morale 消解后，路线名不再是必需** |
| ③ 600+路线名含精英 → 普通精英 | ⚠️ 见 §3 | 同上 |
| ④ 200/400/600 其余 → 普通精英 | ✅ t20 档位集合 | 自洽 |
| ⑤ 非枚举且>150 → 传奇小怪 | ⚠️ 见 §2.3 | 需真实频次验证 150~200 之间的值是否存在 |
| ⑥ 其余≤150 → 小怪 | ✅ 截图 38/40/45 | 自洽 |

### 2.3 规则未覆盖的风险值（需真实账号验证，§7）

- 规则 ⑤ 的「非枚举且 >150 → 传奇小怪」在 150~200 之间有一个**空档疑点**：若真实数据出现 151~199 的值（既非精英档、又 >150），会被判成传奇小怪——但「传奇小怪」是否真有 150~199 区间的摩拉，需要真实频次表确认。
- 若真实频次表显示 150~199 区间**为空**（传奇小怪真实下限更高，如 ≥200 且非标准档），则规则 ⑤ 的 >150 阈值可收紧，但**当前无证据，保持 v4 不变**。

---

## 3. 600 歧义：single_morale 直接消解（Q3）

### 3.1 结论

**600 的「600×1」vs「200×3」在数据层即可区分**，看 single_morale：

| single_morale | count | 判定 |
|---|---|---|
| 600 | 1 | 单次 600 → 精英（走规则 ④，或按路线名规则 ②/③ 细分传奇/普通） |
| 200 | 3 | 单次 200 → 精英（普通精英），count=3 只是次数，**不乘算击杀数** |
| 空/NULL | — | 接口没给 single_morale → 回退路线名判别（见 3.2） |

### 3.2 路线名判别降级为「可选交叉校验」

- t20 已确认 single_morale/count 存在，所以**路线名不再是必需信号**。
- **但**：a) 若某账号接口返回缺 single_morale（t16 明确可空），仍回退路线名；b) 路线名还能把「600 单次」进一步细分为传奇精英 vs 普通精英（规则 ②/③）。
- **★ 关键限制（实测）**：BGI ExecutionRecords/YYYYMMDD.json 的 project_name 是**脚本/项目名**（如「锄地一条龙（联机）」「圣遗物调查点纯激活」），**不含路线文件名**；路线文件名（如「07【额外】至冬-旧基捷城西盗罐者6.json」）只在日志正文「进入路线循环 route=…」里出现，且 A4 样本里的路线名**不含「传奇」「精英」字面量**（实测：07/08/09/10 盗罐者 6/17/5.json、99-明蕴镇神像.json，无「精英」「传奇」）。→ 意味着「路线名含精英/传奇」这个判别信号，在真实数据里**几乎用不上**，规则 ②/③ 的实际触发率预期极低（精确数值待 t17 用全量路线名统计）。

---

## 4. 覆盖范围（Q4）

| 项 | 结论 | 证据 |
|---|---|---|
| 是否所有账号都能拉 | 需**有效 cookie_token + 已绑定原神角色 + 游戏等级≥10**（札记门槛） | t14 已标等级<10 取不到；t16 -10001/401/403 → needs_login |
| 拉取时间范围上限 | monthDetail 按 month 参数（1~12）拉**单月**；历史可拉多月（跨年上限未证实，按 month 逐个拉） | genshin.py diary.py month 参数 |
| 权限/等级 | 等级<10 无札记数据（monthInfo/monthDetail 返回空或报错） | genshinhelper2 mihoyo.py:60-64 |

---

## 5. 与 BGI 时间窗对齐（Q5）+ 归属算法

### 5.1 BGI 时间窗的真实粒度（实测 ExecutionRecords）

ExecutionRecords/20261124.json 真实记录是**项目级**，含 group_name（配置组）、project_name（脚本/项目）、start_time/end_time（纳秒精度 ISO）、is_successful：

```json
{
  "type": "SoloTask",
  "group_name": "联机锄地-罐子",
  "project_name": "锄地一条龙（联机）",
  "start_time": "2026-11-24T21:45:17.8032867+08:00",
  "end_time": "2026-11-24T22:03:38.7414808+08:00",
  "is_successful": true
}
```

### 5.2 粒度结论（关键限制）

- ExecutionRecords **只到项目/脚本级**，**不含路线文件名**。一个「锄地一条龙（联机）」项目内部有 5 条路线（07/08/09/10/99），但 ExecutionRecords 只有一条项目记录。
- 路线文件名只能在**日志正文**「进入路线循环 route=…」里取（且只读核实：A4 日志里的路线名不含「精英/传奇」）。
- → **摩拉记录 → 项目级**可以唯一归属（时间戳落在唯一项目窗口内）；**摩拉记录 → 路线文件级**需要再叠加「日志正文的路线时间窗」（进入路线循环 ~ 脚本执行结束，秒级），但米游社时间只有分钟，且同分钟可能跨两条路线，存在**无法细分**的固有误差。

### 5.3 归属算法伪代码（两档归属）

```
# 档一：项目级归属（高置信，推荐先做）
对每条米游社摩拉记录 rec（ts = 分钟）:
  在 ExecutionRecords 里找覆盖 ts 的项目窗口 (group_name, project_name, start, end)
  若唯一命中 1 条 → route_project = project_name；confidence = high
  若命中 0 条 → route_project = NULL；unattributed（BGI 没跑/时间对不上/延迟未拉全）
  若命中 >1 条（重叠窗口）→ 取 start_time 最大（最近）那条；confidence = medium

# 档二：路线文件级归属（需日志正文路线窗，低置信，尽力而为）
  在日志正文里建 (route_name, route_start_s, route_end_s) 秒级窗口表
  把 rec 的分钟 [ts, ts+60s) 与路线窗口求交:
    唯一命中 1 条 → route_name = 该路线；confidence = medium
    命中 0 条 → route_name = NULL
    命中 >1 条（同分钟跨路线）→ route_name = NULL；mark='minute_overlap'（无法细分）
```

### 5.4 匹配率预估（诚实标注为估计，需 t17 全量验证）

- **项目级归属匹配率预期高（>90%）**：挂机时段 BGI 日志与 ExecutionRecords 都有，项目窗口覆盖明确。
- **路线文件级匹配率预期中等（约 50~80%）**：受「米游社时间只有分钟 + 同分钟跨路线 + 路线名无精英/传奇标记 + 1 小时延迟」多重限制，能精确到单条路线的比例有限。
- 上述为**基于真实文件结构的估计**，非实测数值，需 t17 用全量数据跑出真实匹配率（已列入 §7）。

---

## 6. 最终确认版归类规则表（v4.1，与 v4 的差异说明）

### 6.1 与 v4 的差异

- **唯一实质变化**：600 的消解**优先用 single_morale**（数据层），路线名规则 ②/③ 降级为「single_morale 缺失时的回退 + 细分传奇/普通的可选交叉校验」。
- 其余规则、kill_count 恒 1、record_count 汇总口径、enum 集合 {200,400,600,800,1200,3000} **全部不变**（t20 解包与用户 v4 完全一致）。

### 6.2 最终分类伪代码（替换 t15 §3.1）

```csharp
public const int RuleVersion = 4;

public static MoraCategory Classify(long? singleMora, long? count, long fallbackMoraValue, string routeName)
{
    string route = routeName ?? "";
    // 优先用 single_morale；缺失回退 mora_value（总额，见 6.3）
    long value = singleMora ?? fallbackMoraValue;

    if (value == 800 || value == 1200 || value == 3000) return LegendaryElite;      // ①
    if (value == 600 && route.Contains("传奇")) return LegendaryElite;              // ②（可选细分）
    if (value == 600 && route.Contains("精英") && !route.Contains("传奇"))
        return Elite;                                                               // ③（可选细分）
    if (value == 200 || value == 400 || value == 600) return Elite;                 // ④
    bool isEliteValue = value==200||value==400||value==600||value==800||value==1200||value==3000;
    if (value > 150 && !isEliteValue) return LegendaryMinion;                       // ⑤
    return Minion;                                                                  // ⑥
}
```

### 6.3 single_morale 缺失时的回退

- 若 single_morale 为 NULL：用 morale / count（总额÷次数）做近似单次值（仅当 count>0 且整除时可信）；仍不可得则回退 t15 的「总额 mora_value + 路线名」原规则。
- **注意**：count 只用于「反推单次值」，**绝不用于乘算击杀数**（用户裁定 kill_count 恒 1）。

---

## 7. 需真实账号验证的点（仅剩这几项，其余已定稿）

1. **single_morale/count 的聚合粒度**：同分钟 3 只 200 摩拉的怪，接口是给 {200,3,600} 一条，还是 {600,1,600} 三条、还是拆 3 条 {200,1,200}？——决定「一条记录」到底对应几只怪，及 single_morale 是否恒等于真实单次值。
2. **真实数值频次表**：全量跑一遍，确认 150~199 区间是否为空（决定规则 ⑤ 的 >150 阈值是否需要收紧）、是否出现标准集之外的其它值。
3. **路线文件级归属真实匹配率**：用全量日志正文路线窗 × 米游社分钟时间戳跑出实际命中率（§5.4 是估计）。
4. **历史月份可拉范围**：month 参数能否跨多年回拉（影响「历史补全」能力）。
5. **各账号覆盖差异**：等级<10 的账号、未绑角色的账号，monthDetail 实际返回什么（空 details / 报错码）。

---

## 8. 订正与降级说明（D 项）

- 若某账号确实拉不到逐条记录（等级<10 / 无角色 / cookie 失效），**如实标记 needs_login 或 no_data，绝不用 monthInfo 月汇总伪造明细**（t16 已保证：monthDetail 原始行与 monthInfo 月事实并存、不互相代替，采集不过滤不归类）。
- 归属失败（unattributed）的记录保留原始行 + 标记，不做「摊到最近路线」的伪造。
