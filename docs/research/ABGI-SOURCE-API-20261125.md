# ABGI 源码解包：米游社 API 真实实现与 archive.db 数据模型

> 日期：2026-11-25
> 来源：D:\AAA挂机专区\A1\autobgi（参考项目 ABGI 完整运行目录，只读分析，未运行其 exe）
> 方法：MihoyoBBSTools Python 源码精读 + auto-bgi.exe（Go，UPX 压缩）UPX 5.3.0 解包后字符串/结构体提取 + archive.db 只读 schema 查询
> 工作区副本：research/abgi-ref/MihoyoBBSTools/（33 文件 0.17 MB，已确认无任何真实凭据值）
> %TEMP% 分析产物（解包 exe、archive.db 副本、python 脚本）已全部删除（见 §9）

---

## 0. 对数据中心改造的影响（先读这节）

| # | 问题 | 结论 | 证据 |
|---|---|---|---|
| 1 | **t18 的 single_morale 疑问** | **有答案：mihoyo 旅行札记（ys_ledger）返回字段里就有 `action`(分类名)/`single_morale`(单次数值)/`count`(次数)/`morale`(合计) 四元组**。ABGI 的 archive.db 把它们原样存进 `MoraleRecord_two.mark` 的 `details` 数组与 `actions` 汇总数组。t18 直接照抄该字段语义即可 | 二进制字符串 `monster_count…single_morale` + mark 实测数据 + models/morale_income_book.go 的 json tag `json:"single_morale"` |
| 2 | **t14「米游社没有逐条记录」结论** | **被推翻**。旅行札记 monthDetail 返回**逐 action×档位**的明细（`details: [{action, single_morale, count, morale}]`），足够做「怪物档位归类」；ABGI 另存 `sources`（BGI 实时识别的逐条原始记录，带秒级时间戳）。两条路都通 | archive.db 月行 mark JSON（414 条 details）+ sources 数组（逐条 time/action_id/morale/count） |
| 3 | **t16 应改用什么接口** | CN：`GET https://hk4e-api.mihoyo.com/event/ys_ledger/monthDetail?uid=&region=&month=`；OS：`GET https://sg-hk4e-api.hoyolab.com/event/ysledgeros/month_info` + `month_detail`。Cookie 从 BetterGI 配置 `otherConfig.miyousheConfig.cookie` 拿（ABGI 的做法，SoulLink 同样场景可直接复用） | 二进制 URL 字符串（唯一出现 1 次）+ 中文错误串「BetterGI 配置缺少 otherConfig.miyousheConfig 节点」 |
| 4 | **t19 的 act_id 是否全拿到** | **是**。原神/星铁/绝区零/崩3/崩2/未定全部拿到（§2 表），国际服 5 个也齐 | setting.py:108-143 |
| 5 | **DS salt 漂移顾虑** | **部分解决**：ABGI 二进制里有 `xV8v4Qu54lUKrEYFZkJhB8cuOh9Asafs`（x4 so salt，注明「so提取 一般不会变」）与 verify_key `bll8iq97cem8`，双源确认；app salt（idMMaGY…）不在二进制里（Go 侧只走 so/凭证接口，不需要 app salt）。Go 的 DS 算法为 `md5(salt=%s&t=%d&r=%s&b=%s&q=)`（body+query 版） | 二进制 salt 字符串 + `salt=%s&t=%d&r=%s&b=%s&q=` 格式串 |
| 6 | **扫码登录** | Python 端**未找到**（login.py 只做 cookie 解析/刷新）；Go 端**有完整三接口**：createQRLogin / queryQRLoginStatus / exchangetls（§3）。t16 若要扫码，以 Go 侧 URL 为准 | 二进制字符串 |
| 7 | **收益统计口径（tier）** | ABGI 前端把 `single_morale ∈ {200,400,600,800,1200,3000}` 判为**精英怪**，其余正数击杀奖励归**小怪**；`action_id==37` 或 action 名含击杀类词 → 击杀奖励；含 BBOSS → 排除。上限 Q=3000。日/月汇总=按 period 聚合 + 详情桶化 | 二进制内嵌 JS（classifyMonsterReward 完整逻辑，§5） |
| 8 | **逐条摩拉的存法** | 两层：`MoraleRecord`（旧表，逐条：time/num/action）→ `MoraleRecord_two`（新表，**按日/月聚合成一行**，mark JSON 里带 actions 汇总 + details 档位桶 + sources 原始逐条）。启示：我们 DB 的 err/route 明细 + run 汇总三层设计与其同构，可放心 | §6 schema |
| 9 | **日界线** | ABGI 的日汇总 period=自然日（0 点切换）——与用户说的「BGI 日界线是 0 点，跨天=换日志文件」一致。它**不跨天续跑**（第二天整段独立），这正是我们要胜过它的点 | MoraleRecord_two period 值全部为 2026-11-01..21 自然日 |
| 10 | 我们没考虑到的 | ① 验证码（geetest）流程 ABGI 全套都有（§4.6）；② device-fp 接口 `public-data-api.mihoyo.com/device-fp/api/getFp`；③ 云游戏 wallet 签到；④ 签到批次+逐账号日志表（mys_sign_run_batches/logs）——对我们 t19 的签到执行记录直接可抄 | §4 各小节 |

---

## 1. 米游社账号体系与登录

### 1.1 Python 端（MihoyoBBSTools，cookie 驱动，无扫码）

| 事项 | 实现 | 出处 |
|---|---|---|
| 登录前提 | cookie 必填 + stoken 必填，缺则抛 CookieError/StokenError | login.py:16-34 |
| uid 提取 | 正则 `(account_id|ltuid|login_uid|ltuid_v2|account_id_v2)=(\d+)` 从 cookie 取 | login.py:47-54 |
| mid 提取 | `(account_mid_v2|ltmid_v2|mid)=…`；v2 stoken 必须带 mid | login.py:42-44, 98-106 |
| login_ticket→stoken | `GET {web_api}/auth/api/getMultiTokenByLoginTicket?login_ticket=&token_types=3&uid=`，取 `data.list[0].token`（login_ticket 只有半小时有效） | login.py:57-66, setting.py:77 |
| stoken→cookie_token | `GET {web_api}/auth/api/getCookieAccountInfoBySToken`，头带 `stuid=..;stoken=..[;mid=..]`，retcode!=0 → clear_stoken | login.py:69-82, setting.py:78 |
| cookie_token 刷新 | 失效时用上面的接口换新值，原地替换 cookie 串，保存配置 | login.py:85-95 |
| stoken→app token | `GET {passport_api}/account/ma-cn-session/app/getTokenBySToken` | setting.py:73 |
| 绑定角色列表 | `GET {web_api}/binding/api/getUserGameRolesByCookie?game_biz=<game_id>` → `data.list[].{nickname,game_uid,region}`；retcode -100 → 刷 cookie_token 重试一次 | account.py:29-43, setting.py:72 |

**失效判定**：retcode -100（cookie 失效）→ 尝试 update_cookie_token；stoken 换 token 失败 → clear_stoken + StokenError。

### 1.2 Go 端（ABGI，扫码三接口，二进制字符串级证据）

| 接口 | URL | 用途 |
|---|---|---|
| 创建二维码 | `POST https://passport-api.mihoyo.com/account/ma-cn-passport/app/createQRLogin` | 生成登录二维码 |
| 轮询扫码状态 | `POST https://passport-api.mihoyo.com/account/ma-cn-passport/app/queryQRLoginStatus` | 轮询确认/凭据交换 |
| 会话凭证 | `POST https://passport-api.mihoyo.com/account/ma-cn-session/app/exchangetls` | 换取登录态 |

请求体/返回结构在二进制里无法完整还原（Go 结构体 tag 只露出 `json:"token"`/`json:"mid"`/`json:"uid"`/`json:"uuid"`），端点已双源确认（出现 1 次，无重复变体）。t16 实现时抓包验证即可。

---

## 2. 游戏签到（国服 + 国际服）act_id 全表

### 2.1 国服（setting.py + gamecheckin.py）

| 游戏 | game_id(米游社) | config key | act_id | 签到接口（base） | 特殊请求头 | 出处 |
|---|---|---|---|---|---|---|
| 原神 | hk4e_cn | genshin | **e202311201442471** | api-takumi.mihoyo.com/event/luna | Origin=act.mihoyo.com + x-rpc-signgame: hk4e | setting.py:117, gamecheckin.py:209-214 |
| 星穹铁道 | hkrpg_cn | honkai_sr | **e202304121516551** | 同上 | Origin=act.mihoyo.com | setting.py:120, gamecheckin.py:217-221 |
| 绝区零 | nap_cn | zzz | **e202406242138391** | act-nap-api.mihoyo.com/event/luna/zzz | Origin=act.mihoyo.com + X-Rpc-Signgame: zzz | setting.py:123-127, gamecheckin.py:224-232 |
| 崩坏3 | bh3_cn | honkai3rd | **e202306201626331** | api-takumi…/event/luna | Referer=webstatic…/signin/bh3 | setting.py:111, gamecheckin.py:191-197 |
| 崩坏2 | bh2_cn | honkai2 | **e202203291431091** | 同上 | Referer=…/signin/bh2 | setting.py:108, gamecheckin.py:182-188 |
| 未定事件簿 | nxx_cn | tears_of_themis | **e202202251749321** | 同上 | Referer=…/signin/nxx | setting.py:114, gamecheckin.py:200-206 |

三件套（luna 协议，zh-cn）：
- 奖励列表 `GET {base}/home?lang=zh-cn&act_id=` → `data.awards[]`（name/cnt）
- 签到状态 `GET {base}/info?lang=zh-cn&act_id=&region=&uid=` → `data.{is_sign,first_bind,total_sign_day,today}`
- 签到 `POST {base}/sign` body `{"act_id":…,"region":…,"uid":…}`（zzz 只带 act_id，region/uid 可选兼容）→ retcode 0 且 `data.success==0`=成功；**-5003=已签过**；触发验证码时返回 `data.gt`+`data.challenge`，需 geetest 三头（x-rpc-challenge/x-rpc-validate/x-rpc-seccode=`validate|jordan`）重试；429=同 IP 限流（sleep 10s）。出处：gamecheckin.py:71-127, 158-176；hoyo_checkin.py:8。

### 2.2 国际服（HoYoLAB，hoyo_checkin.py）

| 游戏 | event_base_url | act_id | 出处 |
|---|---|---|---|
| 原神 | **sg-hk4e-api.hoyolab.com/event/sol** | e202102251931481 | hoyo_checkin.py:91-95, setting.py:139 |
| 星穹铁道 | **sg-public-api.hoyolab.com/event/luna/os** | e202303301540311 | hoyo_checkin.py:98-102, setting.py:140 |
| 崩坏3 | **sg-public-api.hoyolab.com/event/mani** | e202110291205111 | hoyo_checkin.py:105-109, setting.py:141 |
| 未定事件簿 | **sg-public-api.hoyolab.com/event/luna/os** | e202202281857121 | hoyo_checkin.py:112-116, setting.py:142 |
| 绝区零 | **sg-act-nap-api.hoyolab.com/event/luna/zzz/os** | e202406031448091 | hoyo_checkin.py:118-122, setting.py:143 |

国际服请求头极简：Referer=https://act.hoyolab.com/ + Cookie；zzz 加 x-rpc-signgame:zzz。已签 retcode 同为 -5003（hoyo_checkin.py:8,72）。
二进制侧另有 `sg-act-public-api.hoyolab.com/event/sol/sign?lang=zh-tw`（ABGI Go 端自己的 OS 签到路径，双源确认 sol 域名）。

### 2.3 云游戏签到（cloudgames.py）

| 游戏 | URL | 头 | 返回 |
|---|---|---|---|
| 云原神 CN | api-cloudgame.mihoyo.com/hk4e_cg_cn/wallet/wallet/get | x-rpc-combo_token | data.free_time.free_time / send_freetime、play_card.short_msg、coin.coin_num |
| 云绝区零 CN | cg-nap-api.mihoyo.com/nap_cn/cg/wallet/wallet/get | 同上 | 同上 |
| 云原神 OS | sg-cg-api.hoyoverse.com/hk4e_global/cg/wallet/wallet/get | 同上 | 同上 |

出处：cloudgames.py:64-88, setting.py:130-147。retcode -100=token 失效/防沉迷。

---

## 3. DS 签名与请求头（t14 salt 疑虑的答案）

### 3.1 四个 salt 与 verify_key（setting.py:1-14）

| 常量 | 值 | 说明 |
|---|---|---|
| mihoyobbs_salt | idMMaGYmVgPzh3wxmWudUXKUPGidO7GM | java 提取，**跟随版本更新**（对应 version 2.93.1） |
| mihoyobbs_salt_web | G1ktdwFL4IyGkHuuWSmz0wUe9Db9scyK | 手机网页端 salt |
| mihoyobbs_salt_x4 | xV8v4Qu54lUKrEYFZkJhB8cuOh9Asafs | **so 提取，一般不会变**（★ 二进制双源确认在） |
| mihoyobbs_salt_x6 | t0qEgfub6cvueAPgR5m9aQWWVciEer7v | so 提取 |
| mihoyobbs_verify_key | bll8iq97cem8 | 部分接口要带（★ 二进制双源确认在） |

版本对应关系：salt 与 `mihoyobbs_version=2.93.1` 相互对应（setting.py:11）→ **app salt 确实会随版本漂移，但 x4/x6（so 层）稳定** —— t14 的「salt 漂移」顾虑只需盯 app salt。

### 3.2 两种 DS 算法（tools.py:42-70）

```python
# get_ds(web)：签到/角色列表等
c = md5(f'salt={salt}&t={t}&r={r}')           # r = 6位小写字母数字
DS = f"{t},{r},{c}"

# get_ds2(query, body)：社区写操作（点赞/签到帖）
c = md5(f'salt={salt_x6}&t={t}&r={r}&b={body}&q={query}')  # r = 100001..200000
DS = f"{t},{r},{c}"
```
Go 端格式串 `salt=%s&t=%d&r=%s&b=%s&q=`（二进制）与 get_ds2 同构 —— 双源确认。

### 3.3 请求头全集

游戏签到头（setting.py:50-65）：Accept、DS、x-rpc-channel=miyousheluodi、Origin=webstatic.mihoyo.com、
x-rpc-app_version=2.93.1、UA(含 miHoYoBBS/2.93.1)、x-rpc-client_type=5(mobile web)、Referer、
Accept-Encoding、Accept-Language、X-Requested-With=com.mihoyo.hyperion、Cookie、x-rpc-device_id。
社区头（mihoyobbs.py:28-47）：另加 x-rpc-sys_version=12、x-rpc-device_name/model/fp、
x-rpc-verify_key=bll8iq97cem8、x-rpc-csm_source=discussion、UA=okhttp/4.9.3、client_type=2(安卓)。
设备 id 生成：`uuid3(NAMESPACE_URL, cookie)`（tools.py:73-80）。
Go 端另有 device-fp 获取接口：`https://public-data-api.mihoyo.com/device-fp/api/getFp`（二进制）。

---

## 4. 社区任务（mihoyobbs.py）

| 任务 | 接口 | 方法/参数 | 出处 |
|---|---|---|---|
| 任务状态 | {bbs_api}/apihub/wapi/getUserMissionsState?point_sn=myb | GET；返回 can_get_points/already_received_points/total_points + states[]（mission_id 58签到/59看帖/60点赞/61分享，is_get_award/happened_times） | mihoyobbs.py:106-149, setting.py:79 |
| 讨论区签到 | {bbs_api}/apihub/app/api/signIn | POST body `{"gids":"<分区id>"}`，DS=get_ds2("", body)；1034=验证码；-100=stoken 失效 | mihoyobbs.py:169-201, setting.py:80 |
| 帖子列表 | {bbs_api}/post/api/getForumPostList?forum_id=&is_good=false&is_hot=false&page_size=20&sort_type=1 | GET | mihoyobbs.py:152-167, setting.py:81 |
| 看帖 | {bbs_api}/post/api/getPostFull?post_id= | GET | mihoyobbs.py:204-209, setting.py:82 |
| 点赞/取消 | {bbs_api}/apihub/sapi/upvotePost | POST `{"post_id":..,"is_cancel":bool}`；1034 触发验证码重试 | mihoyobbs.py:212-246, setting.py:84 |
| 分享 | {bbs_api}/apihub/api/getShareConf?entity_id=&entity_type=1 | GET | mihoyobbs.py:249-259, setting.py:83 |
| 验证码 | {bbs_api}/misc/api/createVerification?is_high=true → gt/challenge；verifyVerification POST {geetest_challenge, geetest_validate, geetest_seccode=validate|jordan} → data.challenge 用于 x-rpc-challenge | mihoyobbs.py:82-103, setting.py:85-86 |
| 分区表 | 崩3=1/26，原神=2/26，崩2=3/30，未定=4/37，大别野=5/34，星铁=6/52，绝区零=8/57，因缘精灵=9/948，星布谷地=10/950 | setting.py:17-27 |

签到游戏侧验证码（gamecheckin.py:110-124）：返回 gt+challenge → 过验证 → 三头 x-rpc-challenge / x-rpc-validate / x-rpc-seccode=`{validate}|jordan`。

---

## 5. 摩拉（旅行札记）—— t18 关键答案

### 5.1 ABGI 的拉取路径（Go，双源）

- **CN**：`GET https://hk4e-api.mihoyo.com/event/ys_ledger/monthDetail?uid=<uid>&region=<region>&month=<yyyy-MM>`
  （二进制 URL 唯一出现 1 次；参数名 uid/region/month 来自 Go 结构体 tag json:"uid"/json:"region"/json:"month"）
- **OS**：`GET https://sg-hk4e-api.hoyolab.com/event/ysledgeros/month_info` + `month_detail`（分页：错误串「国际服札记的月份、收入类型或页码无效」→ OS 侧有 月/收入类型/页码 三参数）
- Cookie 来源：**BetterGI 配置的 `otherConfig.miyousheConfig.cookie`**（错误串「BetterGI 配置缺少 otherConfig.miyousheConfig 节点」为直接证据）—— SoulLink 想拿同一份 cookie 就是这个位置。
- ABGI 前端再经本地服务 `GET /api/BagStatistics/Morale?type=day|month|year&date=…&refresh_hour=…` 与 `/api/BagStatistics/MoraleIncomeBook` 读取。

### 5.2 返回字段（核心问题：single_morale / count 存在）

`mark` JSON（ABGI 落库后的原样结构，实测 2026-09 月行）：

```json
{
  "total_count": 74959,
  "total_morale": 19165058,
  "actions":  [ {"action":"精英怪","count":10951,"morale":6039600}, {"action":"小怪","count":57832,"morale":3602201}, … ],
  "details":  [ {"action":"任务奖励","single_morale":30000,"count":2,"morale":60000}, … ],
  "days":     [ …（月行拆日）… ],
  "sources":  [ {"time":"2026-11-14 02:46:58","action_id":37,"action":"击杀怪物奖励","morale":36,"count":1}, … ]
}
```

→ **`single_morale` 与 `count` 都在**（details 数组的逐档位桶）；`actions` 是按 action 名的总汇。t18 拿字段语义直接照抄。
注意：`current_day` 等「日切换」字段**未在 ABGI 二进制出现**（json tag 无）——它按本地自然日（0 点）自己切。

### 5.3 档位归类（classifyMonsterReward，二进制内嵌 JS 还原）

```
精英档位 W = [200, 400, 600, 800, 1200, 3000]   （Q=3000 为单次上限）
击杀判定:  action_id == 37 或 action ∈ {击杀类词表}
排除:      action 含 "BBOSS"（世界 Boss 不计入击杀收益）
归精英:    single_morale 命中 W 档位
归小怪:    其余正数击杀奖励
```
分类标签（5 类）：击杀怪物奖励 / 宝箱 / 任务 / 探索 / 其他收益。
实测佐证：精英 details 恰好只有 {3000,1200,600,200}×count；小怪是 32..147 的散值（如 147×4、146×3）。

### 5.4 ABGI 摩拉统计的口径

- 日行（record_type=day，period=自然日）：当天 actions/details/sources 全量。
- 月行（record_type=month，period=yyyy-MM）：整月聚合 + days 拆日 + sources 全月原始（mark 可到 13 MB）。
- 去重：sources 逐条带秒级 time；日/月行各写一行（同 period 幂等 upsert）；不跨天续跑（见 §0.9）。

---

## 6. archive.db 完整 schema（68.9 MB，gorm 生成，只读查询）

与「数据中心改造」直接相关的三张表：

### 6.1 MoraleRecord（旧版，逐条摩拉）

```sql
CREATE TABLE MoraleRecord (id INTEGER PK AUTOINCREMENT, time TEXT, num INTEGER, action TEXT)
```
实测 3551 行：time=`2026-01-07 22:26:52`、num=36、action=`击杀怪物奖励` —— **逐条实时记录**（BGI 识别回调写入）。

### 6.2 MoraleRecord_two（新版，日/月聚合，★ 我们最该借鉴的）

```sql
CREATE TABLE MoraleRecord_two (
  id INTEGER PK AUTOINCREMENT,
  uid TEXT,                    -- 米游社 uid
  name TEXT,                   -- 账号名
  time TEXT,                   -- 行创建时刻（period 当日 00:00:00）
  num INTEGER,                 -- total_morale（当期总摩拉）
  action TEXT,                 -- '日汇总' / '月汇总'
  record_type TEXT,            -- 'day' | 'month'
  period TEXT,                 -- '2026-11-14' | '2026-09'
  detail_count INTEGER,        -- total_count（当期总条数）
  mark TEXT,                   -- §5.2 的完整 JSON（actions+details+days+sources）
  action_id INTEGER
)
```
实测 31 行（21 day + 10 month）：日行 num≈50万-113万、detail_count≈2500；月行 detail_count≈7.5万、mark 最大 13 MB。
**设计启示**：`record_type+period+uid` 构成天然主键候选；明细桶（details）与汇总（actions）分层；原始逐条（sources）作为 JSON 存档而非独立表——查询按日/月走索引、明细按需展开。对比我们的 route/err 明细表 + run 汇总表三层：**方向一致，ABGI 更省表、我们把明细升为一等公民更利于查询，两种都成立**。

### 6.3 RedBloodRecord（回血/死亡事件 —— 他们的「逐路线事件」样本）

```sql
CREATE TABLE RedBloodRecord (
  id INTEGER PK AUTOINCREMENT,
  event_time DATETIME,         -- '2026-11-24 16:38:37.2257905+08:00'（带纳秒+时区）
  group_name TEXT,             -- '联机锄地-混合精英' —— 配置组名直接入库
  route_name TEXT,             -- '00-E014枫丹利亚姆传奇高厄.json' —— 路线名直接入库
  event_type TEXT,             -- 'heal' | 'death'
  heal_count INTEGER DEFAULT 0,
  death_count INTEGER DEFAULT 0,
  created_at DATETIME,
  source TEXT                  -- 'bgi'（来源标记）
)
```
实测 32 行，source 全部=bgi。**这就是「配置组×路线×事件」的最小落地形态**：不建外键、组名路线名直接冗余存、事件类型枚举、计数列分开 —— 与我们 run/route/err 的自然键设计相比更简单但无幂等键（重复喂会重复计数）。我们的 route_id 确定性自然键设计更优。

### 6.4 其余表（一句话带过，与数据中心弱相关）

BackpackRecord(27265行，背包材料快照：name/time/type/num)、BackpackStatistics(material)、
mys_sign_run_batches(id,trigger,status,account_count,success_count,failed_count,summary,started_at,finished_at)
+ mys_sign_run_logs(batch_id,account_id,account_name,trigger,status,summary,output,started_at,finished_at)
（★ 签到执行「批次+逐账号日志」两层记录，对 t19 签到执行记录直接可抄）、
script_group_runtime(group_name,last_started_at)（组运行时状态）、
gacha 系（accounts/records）、character_plan 系、TaskCron、abgi_team_connections 等（见 §8 后期功能情报）。

---

## 7. ABGI 内嵌服务 API 面（Go，供 t16/t18 参考调用形态）

`/api/BagStatistics/Morale`（type=day|month|year&date&refresh_hour）、`/api/BagStatistics/MoraleIncomeBook`、
`/api/BagStatistics/getBagInfo`、`/api/mys/morale-status`、`/api/mys/accounts` CRUD、
`/api/BagStatistics/moraleJobs/:jobId`、`/api/MoraleMonsterBook`。前端 identityParams() 带 uid/region 选择。

---

## 8. 后期功能情报（顺手记录，不做实现依据，每条 3-5 行）

- **配置组编辑**：script_group_collection(+member 外键 CASCADE)、script_group_preference、character_script_editor_states(group_name PK, draft_json, material_hash)。编辑=草稿 JSON+素材哈希防冲突。
- **一条龙编辑**：oneLong/oneLongPlan 全套 API（add/delete/detail/list/run/update）、OneDragonLog/GroupProgress/Resume 三表。
- **脚本/路线管理**：script_entries(path,arguments,success_exit_codes,queue_mode)、script_queues(+items CASCADE)、CDCollectionManagement 的 importRoutes/exportRoutes/toggleRoutes/blacklists。
- **推送**：push.py 20.9 KB，支持 pushplus/ftqq/tg/wecom/dingrobot/feishubot/bark/pushdeer/gotify/smtp/webhook/qmsg/discord/wintoast/wxpusher/serverchan3 十六渠道。
- **多账号**：main_multi.py + config/accounts/<hash>.yaml 每账号一文件；ABGI Go 侧 mys/accounts CRUD 更完整。
- **AI 陪跑**：abgiAi/*（战斗策略 agent、JS agent、记忆）+ skills 表（user_prompt_template/keywords/trigger_type）。
- **多机协同**：abgiSSE/*（websocket 房间）+ abgi_team_connections（api_key_ciphertext 加密存储）。

---

## 9. 清理与体积证据

- 工作区新增：`research/abgi-ref/MihoyoBBSTools/`（33 文件，**0.17 MB**）+ 本文档（约 30 KB）→ 合计 **< 0.5 MB**，远低于 50 MB 限额。
- %TEMP% 删除：`abgi-unpack/`（含 84 MB 解包 exe、68.9 MB archive.db 副本、upx.zip）与 8 个分析用 .py 脚本 → `Test-Path $env:TEMP\abgi-unpack` = **False**（2026-11-25 验证）。
- 原始目录 `D:\AAA挂机专区\A1\autobgi` 只读未动（git status 干净，仅新增 research/docs 文件）。
- 复制内容脱敏核查：所有 `.py`/模板文件中出现的 cookie/stoken 均为**键名或空值**（无真实凭据值）；config/accounts/*.yaml（含真实 cookie）**未复制**；archive.db 样例数据里的 uid/name 在本文档中均已省略或标注 <redacted>。




