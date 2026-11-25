# 米游社接入调研与设计：扫码登录 / 凭据存储 / 自动签到 / 日-月摩拉原石拉取

> 日期：2026-11-25
> 状态：调研 + 设计（本文档是后续实现任务的依据；本任务不写生产代码）
> 合规：**未实际登录任何米游社账号**（无凭据，也不该测）。全部接口来自开源项目源码与社区文档交叉核对，均属第三方逆向整理，**非米哈游官方文档，可能随时失效**。
> 前置：本文档是 docs/research/MIYOUSHE-RESEARCH-20261122.md 的落地细化（登录/Token 链/收益接口已在那份里核实过，此处直接引用并补齐「签到 + 落库 + 存储」三块）。
> 关联代码（只读核实，本任务不改任何 .cs）：Services/Crypto.cs（DPAPI SecretBox）、Services/ConfigStore.cs（凭据持久化入口）、Models/Models.cs:222 Account（账号模型）、Services/TickHub.cs（周期任务）、Services/AppIdentity.cs（DataDir）。

---

## 0. 结论摘要（TL;DR）

1. **纯 C# 走 HTTP 完全可行，无需 Node/Python 运行时**。扫码登录用米哈游通行证 passport 两个纯 HTTP 接口（createQRLogin / queryQRLoginStatus）即可，**不需要极验、不需要 WebView2**：生成二维码 → 轮询 → 确认后从 `Set-Cookie` 拿 ltoken_v2/ltuid_v2/cookie_token_v2。二维码图用现成 .NET 库（QRCoder/Zxing.Net）从 `url` 生成，也不依赖浏览器。
2. **自动签到**：国服原神签到走米游社 H5 luna 接口（`event/luna/hk4e`，act_id=e202311201442471）或旧 bbs_sign_reward 接口（act_id=e202009291139501），POST body `{act_id, region, uid}` + DS 头。返回码 0=成功、-5003=已签到（社区代码明确注释），其余需实测。
3. **摩拉/原石能拉到「当月获得 + 每日明细」**（旅行者札记 ys_ledger monthInfo/monthDetail），**拉不到「当前余额」**——余额只能靠月报累加估算，且月报需游戏等级≥10。
4. **凭据存储**：复用现有 `SecretBox`（Services/Crypto.cs，DPAPI CurrentUser 范围、无额外熵），每 Windows 账号一份加密 JSON，落 `AppIdentity.DataDir\miyoushe\<账号>.json`，不落明文、不进 dist\。
5. **风险**：接口非官方、可能随时改版/封接口/触发 -1034 极验风控；多账号自动签到/批量拉取有封号风险，须做成「用户自由开关 + 单账号串行限速 + 失败暂停」。

---

## 1. 扫码登录（米游社 / 米哈游通行证）

### 1.1 结论：纯 HTTP 能做

扫码登录**不需要** WebView2、不需要极验、不需要 Node/Python。两个 passport 接口就是纯 HTTP POST，C# 用 HttpClient 直接调。这是全流程最干净的登录方式（短信登录才要碰极验，见 §1.4）。

### 1.2 接口一：生成二维码

- **方法**：POST
- **URL**：`https://passport-api.miyoushe.com/account/ma-cn-passport/web/createQRLogin`
- **必需请求头**：`x-rpc-app_id: bll8iq97cem8`（网页端固定值）、`x-rpc-device_id: <任意 UUID>`
- **请求体**：无（空 body）。
- **返回**：`data.url`（二维码内容，含 `tk=` 参数，已经过 Unicode 转义）、`data.ticket`（轮询凭证，= url 里的 tk）。

返回示例结构（来源 UIGF-org/mihoyo-api-collect `hoyolab/login/qrcode_hoyolab.md`）：

```json
{
  "retcode": 0, "message": "OK",
  "data": {
    "url": "https://user.mihoyo.com/login-platform/mobile.html?expire=1693555708&tk=e8a6448c-6596-461c-884a-98fe84bd675b&token_types=4#/login/qr",
    "ticket": "e8a6448c-6596-461c-884a-98fe84bd675b"
  }
}
```

- 来源（三源交叉一致）：
  - UIGF-org/mihoyo-api-collect `hoyolab/login/qrcode_hoyolab.md`（CC-BY-NC）
  - seriaati/genshin.py `genshin/client/routes.py:285` `CREATE_QRCODE_URL`（MIT）
  - Snap.Hutao.Remastered `ApiEndpoints.csv`（MIT）

### 1.3 接口二：轮询扫码状态

- **方法**：POST
- **URL**：`https://passport-api.miyoushe.com/account/ma-cn-passport/web/queryQRLoginStatus`
- **请求头**：同上 `x-rpc-app_id` / `x-rpc-device_id`。
- **请求体**：`{"ticket": "<ticket>"}`
- **返回**：`data.status` 三态：`Created`(未扫) / `Scanned`(已扫) / `Confirmed`(已确认)。
- **关键**：`Confirmed` 后从**响应头 `Set-Cookie`** 取 `ltoken_v2`、`ltuid_v2`、`ltmid_v2`、`account_id_v2`、`cookie_token_v2`（完整登录态，无需再换 token）。`data.user_info` 里有 `aid`（米游社 UID）、`mid`（MiHoYo ID）。

返回码（来源同 §1.2）：`-3001` 请求头缺参数、`-3501` 二维码过期、`-3505` 用户取消扫码。

- 来源：UIGF-org `hoyolab/login/qrcode_hoyolab.md`；genshin.py `routes.py:286` `CHECK_QRCODE_URL`。

### 1.4 为什么不用短信登录（需极验）

短信登录链 `create_mmt → geetest(极验 GT3/GT4) → create_mobile_captcha → login_by_mobilecaptcha`（来源 UIGF `hoyolab/login/sms.md`、genshin.py `routes.py:281-283/288-291`）里，风控触发时要过极验滑块，服务端校验 `captcha_id/sign_token/pass_token`，**纯脚本极难通过，且绕过本身有服务条款风险**。所以：**本项目只做扫码登录，明确不做短信登录、不做密码登录**（也符合「不要自动输入密码/绕过验证码」的硬约束）。

### 1.5 备选：游戏内扫码（GameToken，原神 hk4e）

- 生成：POST `https://hk4e-sdk.mihoyo.com/hk4e_cn/combo/panda/qrcode/fetch` body `{app_id:4, device}`
- 轮询：POST `https://hk4e-sdk.mihoyo.com/hk4e_cn/combo/panda/qrcode/query` body `{app_id, device, ticket}`
- 确认后 `data.payload.raw` = `{"uid":"..","token":"<GameToken>"}`，需再 `getTokenByGameToken` 换 SToken（`https://api-takumi.mihoyo.com/account/ma-cn-session/app/getTokenByGameToken`）。

结论：**对签到/收益数据，米游社扫码（§1.2/1.3）已够用，GameToken 路线是备选，不采用**（多一跳换 token，无收益）。来源：UIGF `hoyolab/login/qrcode_hk4e.md`。

### 1.6 二维码图生成（C# 依赖）

`data.url` 是一段普通 URL 字符串。用 .NET 现成库把它画成 QR 图即可：**QRCoder**（MIT）或 **ZXing.Net**（Apache-2.0），均纯托管、无 Node/Python、可直接进 WPF。仓库当前无 QR 库（全仓 grep 无 ZXing/QRCoder 命中），实现任务需新增一个包引用。

---

## 2. Token 链与签到/收益所需 Cookie（引用 20261122 已核实结论）

| 凭证 | 字段名 | 作用 |
|---|---|---|
| LToken | `ltoken_v2` | 查账号信息/战报（配对 `ltuid_v2`、`ltmid_v2`） |
| Cookie Token | `cookie_token_v2` | **签到 / 收益 / 便笺接口的核心凭证** |
| Account ID | `account_id_v2` / `ltuid_v2` | 米游社 UID |
| MiHoYo ID | `mid` / `ltmid_v2` | 与 V2 token 配对 |
| SToken | `stoken_v2` | 社区操作 / 换 token（扫码可能不直接给，需换） |

扫码确认后 `Set-Cookie` 直接带齐 `ltoken_v2/ltuid_v2/ltmid_v2/cookie_token_v2/account_id_v2`，**签到和收益接口只用这些即可，无需额外换 token**。SToken 可选：若后续要做抽卡记录等，再从 cookie_token 换（接口 `https://api-takumi.mihoyo.com/auth/api/getCookieAccountInfoBySToken`，来源 UIGF `hoyolab/user/token.md`）。

来源：UIGF `hoyolab/user/token.md` + genshin.py `routes.py` + Snap.Hutao `ApiEndpoints.csv`，与 MIYOUSHE-RESEARCH-20261122.md §4 一致。

---

## 3. 凭据存储（每账号独立，DPAPI 加密）

### 3.1 存储位置

```
<AppIdentity.DataDir>\miyoushe\<Windows账号>.json
```

- `AppIdentity.DataDir` = 运行时数据目录（便携模式 `exe\data\`，否则 `%LOCALAPPDATA%\<身份>\`，Services/AppIdentity.cs:74/96-124），**不在源码 dist\ 下，不随更新包覆盖**。
- 一个 Windows 账号（A1/A2/A3/A4）对应一份文件，天然按账号隔离。
- 账号名沿用 `RouteLedger.SafeAccount`（Services/RouteLedger.cs:157-161）的净化口径（非法文件名字符 → `_`），防路径注入。

### 3.2 加密：DPAPI，CurrentUser 范围（复用现有 SecretBox）

仓库已有 `SecretBox`（Services/Crypto.cs:9-72），走 Windows DPAPI `CryptProtectData`，**`dwFlags=0`（即 CurrentUser 范围）+ `pOptionalEntropy=IntPtr.Zero`（无额外熵）**，加密结果为 `dpapi:<Base64>`。凭据存储直接复用它，**不新增加密代码**。

**范围论证（为什么 CurrentUser，不用 LocalMachine）**：

| 方案 | 谁能解密 | 结论 |
|---|---|---|
| CurrentUser（`dwFlags=0`，现有 SecretBox） | 只有「运行 SoulLink 的那个 Windows 用户」在同一台机器上能解密 | ✅ 采用 |
| LocalMachine（`CRYPTPROTECT_LOCAL_MACHINE`） | 同一台机器上**任何** Windows 用户都能解密 | ❌ 拒绝：范围过宽 |

理由：SoulLink 是**单进程多账号管理器**——它以一个 Windows 用户身份运行（AGENTS.md：主控制台 session 1 / lummy），负责管理 A1/A2/A3/A4 四个挂机会话。米游社凭据由这一个主进程加密/解密，同一用户、同一台机器解密即可，不需要跨 Windows 用户共享。CurrentUser 恰好满足，且比 LocalMachine 更安全（本机其他用户也读不到）。

**注意**：主程序**不是**以 A1/A2/A3/A4 身份运行的（那些是挂机会话），所以不存在「换到 A1 会话就跑不起来」的问题——凭据只在主进程里解密使用。若未来主程序换 Windows 用户运行，CurrentUser DPAPI 解不开旧密文，属预期行为（密码体系同款：`Account.PasswordCipher` 已是 CurrentUser DPAPI，Models/Models.cs:226-233 + ConfigStore.cs:374-378）。

### 3.3 单文件 JSON 结构（整个文件加密成 dpapi 字符串落盘）

```json
{
  "ltoken_v2": "...", "ltuid_v2": "...", "ltmid_v2": "...",
  "cookie_token_v2": "...", "account_id_v2": "...",
  "stoken_v2": "", "mid": "",
  "aid": "<米游社UID>",
  "last_ok_at": "2026-11-25T12:00:00+08:00",
  "updated_at": "2026-11-25T12:00:00+08:00"
}
```

落盘形式 = `SecretBox.Protect(JsonSerializer.Serialize(obj))` → 文件内容只有一个 `dpapi:...` 字符串。**不含任何明文 cookie**。

### 3.4 凭据失效检测与重新扫码引导

| 信号 | 含义 | 动作 |
|---|---|---|
| 任意接口返回 `retcode = -10001`（或非 0 的鉴权类码） | cookie_token 失效/未登录 | 标记该账号「需重新登录」，界面上提示扫码 |
| 任意接口返回 `retcode = 1034`（或 10035/5003/10041，genshin.py `constants.py` GEETEST_RETCODES） | 触发极验风控 | 暂停该账号自动任务，引导用户重新扫码/人工处理 |
| 扫码轮询返回 `-3501` 过期 / `-3505` 取消 | 本次扫码失败 | 重新生成二维码 |
| HTTP 401/403 | 凭证被服务端拒 | 同上 |

检测点在每次签到/拉取前先调一个轻量校验（如 `getUserGameRolesByCookie` 或 `bbs_sign_reward/info`），非 0 即判定失效。失效后**不清除密文**（保留以便诊断），只在内存/状态里标记 `invalid`，等待用户重新扫码覆盖。

---

## 4. 自动签到（用户可自由开关）

### 4.1 签到接口（国服原神，两套可选，建议用 luna 新版）

**方案 A（genshin.py 现行，luna H5）**：

- 基础 URL：`https://api-takumi.mihoyo.com/event/luna/hk4e?act_id=e202311201442471`
- `GET .../info?act_id=e202311201442471&uid=<game_uid>&region=<server>`：查询今日是否已签、累计签到天数（`is_sign`/`total_sign_day`）。
- `POST .../sign`：执行签到，body `{"act_id":"e202311201442471","region":"<server>","uid":"<game_uid>"}`。
- `GET .../home?act_id=...`：当月签到奖励列表（`awards`）。
- 请求头（关键）：`x-rpc-signgame: hk4e`，`x-rpc-client_type: 5`、`x-rpc-app_version: 2.70.1`、`x-rpc-device_id`、`ds: <动态签名>`。
- 来源：seriaati/genshin.py `routes.py:228-235`（REWARD_URL chinese genshin）+ `components/daily.py`（request_daily_reward，header 与 ds 逻辑）。

**方案 B（genshinhelper2 旧，bbs_sign_reward）**：

- 基础 URL：`https://api-takumi.mihoyo.com/event/bbs_sign_reward`，act_id=`e202009291139501`。
- `GET .../info?act_id=...&uid=..&region=..`、`POST .../sign` body `{act_id, region, uid}`、`GET .../home?act_id=...`。
- 来源：y1ndan/genshinhelper2 `genshinhelper/mihoyo.py:18-27`。

**建议用方案 A**（luna 是当前米游社签到活动接口，genshin.py 仍在维护更新 act_id；方案 B 的 act_id 是老活动）。

### 4.2 DS 动态签名（签到/收益都要）

算法（来源 genshin.py `utility/ds.py` + `constants.py` DS_SALT）：

```
t = 当前 Unix 秒； r = 6 位随机字母数字（或 100001~200000 随机数）
b = 若 body 非空则 json.dumps(body)，否则 ""
q = 若有 query 则按键排序拼 "k=v&k=v"，否则 ""
check = MD5("salt=" + salt + "&t=" + t + "&r=" + r + "&b=" + b + "&q=" + q)
DS 头 = t + "," + r + "," + check
```

签到用 `cn_signin` salt（社区值 `LyD1rXqMv2GJhnwdvCBjFOKGiKuLY3aO`，来源 genshin.py `constants.py` DS_SALT）。**不要硬编码**：salt 随客户端版本漂移（社区 issue 维护），实现时应做成可更新配置项，失败时提示「签名版本可能过期」。

### 4.3 返回码含义（诚实标注）

| retcode | 含义 | 证据 |
|---|---|---|
| 0 | 签到成功 | genshinhelper2 `core.py:146` 注释 + genshin.py 返回语义 |
| -5003 | 今日已签到（already checked in） | genshinhelper2 `core.py:147` 注释明确写 `-5003: already checked in` |
| -10001 | 未登录/cookie 失效（社区通用） | 社区整理（UIGF error_code 文档），**未在源码逐条核实，标「需实测」** |
| 1034 | 触发极验风控 | genshin.py `constants.py` GEETEST_RETCODES={10035,5003,10041,1034} |
| data.success==1 | 出现验证码（需要人工） | genshinhelper2 `core.py:139-141` |

「未绑定角色」：签到前应先调 `https://api-takumi.mihoyo.com/binding/api/getUserGameRolesByCookie?game_biz=hk4e_cn`（genshinhelper2 `core.py:45`）拿到 `game_uid/region`；拿不到角色列表 = 该米游社账号未绑定原神角色，签到无从进行。**该接口的「无角色」具体返回码未找到权威定义，实现时按「list 为空」判未绑定即可。**

### 4.4 定时策略（不新增 DispatcherTimer，走 TickHub）

- **不新增任何 DispatcherTimer**。新增 `TickHub.RegisterBackground("MiyousheSign.Cleanup", 60, ...)` 之类订阅，每拍检查「今天该账号是否已执行过、是否到配置时刻」（TickHub.cs:95-107 的 RegisterBackground 语义）。
- **开关与执行时刻**：`AppSettings`（Models/Models.cs:327）新增配置块，如 `MiyousheSign { Enabled, Hour }`（默认 Enabled=false、Hour=8）。到点触发，每账号**每日最多一次**，执行后在内存标记当天已跑（重启由落库的 sign_log 去重，见 §6 表结构，按 `(account, sign_date)` 幂等）。
- **多账号串行 + 限速**：按账号逐个签到，账号之间间隔 ≥2~5 秒（社区经验：短时高频并发易触发 1034 极验；genshin.py 有 `ratelimit.py` + `@managers.no_multi` 防并发）。**绝不并发打接口**。
- 每次结果写 `my_sign_log`（成功/失败/已签 + retcode + message + 领取奖励名）。

---

## 5. 每日 / 每月摩拉、原石数据拉取

### 5.1 能拉到什么（诚实清单）

| 数据 | 接口 | 粒度 | 能拿到？ |
|---|---|---|---|
| **当月获得原石/摩拉 + 分类汇总 + 环比** | `ys_ledger/monthInfo` | 月 | ✅ |
| **每日明细（原石/摩拉，逐笔带时间）** | `ys_ledger/monthDetail` | 日/笔 | ✅（type=1 原石 / type=2 摩拉） |
| 实时便笺（树脂/委托/派遣/洞天宝钱） | `game_record/.../dailyNote` | 实时 | ✅ |
| 战报索引 / 深渊 / 角色列表 | `game_record/.../index` / `spiralAbyss` / `character/list` | 期/实时 | ✅ |
| **当前原石/摩拉余额（精确值）** | —— | —— | ❌ **无此端点** |
| 当日打了多少只怪 / 击杀数 / 掉落 | —— | —— | ❌ **米游社没有** |

**关键诚实结论**：米游社只有「旅行者札记」的**按月收支统计**（本月获得多少原石/摩拉 + 按活动/每日/深渊/邮件等分类），**没有「账户当前余额」端点**。原石余额只能靠月报逐月累加**估算**，且月报不覆盖全部来源，不能当精确余额。

### 5.2 旅行者札记（ys_ledger）接口

- 月汇总：`GET https://hk4e-api.mihoyo.com/event/ys_ledger/monthInfo?bind_uid=<uid>&bind_region=<region>&month=<n>`（`month` = 月份数字，1~12；`0` 或缺省=当月）。
- 月明细：`GET https://hk4e-api.mihoyo.com/event/ys_ledger/monthDetail?bind_uid=<uid>&bind_region=<region>&month=<n>&type=<1|2>&current_page=<p>&page_size=100`（type=1 原石 / 2 摩拉）。
- 需要 Cookie：`cookie_token`；需要请求头 DS。
- 返回结构（来源 genshin.py `models/genshin/diary.py`）：
  - `month_data.current_primogems/current_mora`（本月获得）、`last_primogems/last_mora`（上月）、`primogem_rate/mora_rate`（环比）、`group_by`（分类汇总）。
  - `day_data.current_primogems/current_mora`（今日获得）。
  - monthDetail → `list[]` 每项 `{action_id, action, time, num}`（逐笔）。
- **限制**：旅行者札记需**游戏等级≥10**才可见（genshinhelper2 `mihoyo.py:60-64` 注释：等级不足会取不到 month_data）。
- 来源：genshin.py `routes.py:179-197`（INFO_LEDGER_URL / DETAIL_LEDGER_URL chinese genshin）+ `components/diary.py`；genshinhelper2 `mihoyo.py:31`。

### 5.3 实时便笺（dailyNote）

- `GET https://api-takumi-record.mihoyo.com/game_record/app/genshin/api/dailyNote?role_id=<uid>&server=<region>`，需 cookie_token + DS。
- 返回：`current_resin/max_resin`、`current_home_coin/max_home_coin`、`finished_task_num/total_task_num`、派遣、质变仪、洞天宝钱等（来源 genshin.py `models/genshin/chronicle/notes.py`）。**与摩拉/原石无关，但可作为「日常状态」辅助。**

### 5.4 与 BGI 日志摩拉快照对账

- BGI 日志锚点「成功识别到摩拉数值: 61555438」是**游戏内当前结余（余额快照）**，绝对量、递增（Services/DailyStatsCache.cs:64-67 注释）。
- 米游社 monthDetail 给的是**当月获得（增量）**，不是余额。
- **对账方法**：相邻两日 BGI 余额快照之差 ≈ 当日净变化（获得 − 消耗）；米游社 monthDetail 的「当日获得摩拉」是净变化的上界之一（不含消耗）。所以只能做**趋势/下限核对**（例如：米游社报当日获得 10 万摩拉，BGI 余额却跌了 → 说明消耗 > 获得，属正常；反之若 BGI 余额涨的远超米游社获得，说明有未计入来源）。**不能精确对账**，文档与实现都要明确这点，不要给用户「精确对账」的错觉。

---

## 6. 数据落库表结构建议（SQLite，复用 run-db 或独立库均可）

建议独立单文件 `<DataDir>\miyoushe\miyoushe.db`（与运行记录库 `run-db\run.db` 分离，职责不同、可独立清理；PRAGMA 同 WAL+busy_timeout=5000+synchronous=NORMAL）。

```sql
CREATE TABLE IF NOT EXISTS my_role (
  account      TEXT NOT NULL,            -- Windows 账号（主程序内的账号标识）
  game_uid     TEXT NOT NULL,            -- 原神 UID
  region       TEXT NOT NULL,            -- 服务器 cn_gf01 等
  nickname     TEXT NOT NULL DEFAULT '',
  level        INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (account, game_uid)
);

CREATE TABLE IF NOT EXISTS my_sign_log (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  account      TEXT NOT NULL,
  game_uid     TEXT NOT NULL DEFAULT '',
  sign_date    TEXT NOT NULL,            -- yyyy-MM-dd（幂等去重键）
  state        TEXT NOT NULL,            -- success / already / failed / needs_login / risk
  retcode      INTEGER NOT NULL DEFAULT 0,
  message      TEXT NOT NULL DEFAULT '',
  reward_name  TEXT NOT NULL DEFAULT '',
  reward_cnt   INTEGER NOT NULL DEFAULT 0,
  ts_ms        INTEGER NOT NULL,
  UNIQUE (account, game_uid, sign_date)
);

CREATE TABLE IF NOT EXISTS my_ledger_month (
  account       TEXT NOT NULL,
  game_uid      TEXT NOT NULL,
  month         INTEGER NOT NULL,        -- 1~12
  current_primogems INTEGER NOT NULL DEFAULT 0,
  current_mora     INTEGER NOT NULL DEFAULT 0,
  last_primogems   INTEGER NOT NULL DEFAULT 0,
  last_mora        INTEGER NOT NULL DEFAULT 0,
  primogem_rate    INTEGER NOT NULL DEFAULT 0,
  mora_rate        INTEGER NOT NULL DEFAULT 0,
  categories    TEXT NOT NULL DEFAULT '{}',  -- group_by JSON
  fetched_at_ms INTEGER NOT NULL,
  PRIMARY KEY (account, game_uid, month),
);

CREATE TABLE IF NOT EXISTS my_ledger_day (
  account   TEXT NOT NULL,
  game_uid  TEXT NOT NULL,
  day       TEXT NOT NULL,               -- yyyy-MM-dd
  kind      TEXT NOT NULL,               -- primogem / mora
  amount    INTEGER NOT NULL DEFAULT 0,
  action_id INTEGER NOT NULL DEFAULT 0,
  action    TEXT NOT NULL DEFAULT '',
  ts_ms     INTEGER NOT NULL,
  PRIMARY KEY (account, game_uid, day, kind, action_id, ts_ms),
);

CREATE TABLE IF NOT EXISTS my_cred_state (
  account  TEXT PRIMARY KEY,
  state    TEXT NOT NULL DEFAULT 'ok',   -- ok / needs_login / risk / expired
  detail   TEXT NOT NULL DEFAULT '',
  updated_at_ms INTEGER NOT NULL
);
```

- `my_sign_log` 的 `(account, game_uid, sign_date)` 唯一键 = 幂等去重（重启重跑不重复签到）。
- `my_ledger_month` 用 `(account, game_uid, month)` 主键，`fetched_at_ms` 供增量刷新判断。
- `my_ledger_day` 主键含 `ts_ms`，逐笔幂等。
- `my_cred_state` 只存状态不存 cookie（cookie 在 §3 的加密 JSON），界面/定时器据此决定是否跳过。

---

## 7. 风险与合规

1. **接口非官方公开**：米游社 API 无官方文档，全部来自社区逆向（UIGF 已归档、注释「项目已过时」，接口漂移快）。任何接口随时可能改版/封禁/加风控，**实现需可降级**（失败不崩、下次重试）。
2. **风控与封号**：短时高频、多账号并发、自动化批量签到/拉取都可能触发 -1034 极验，严重可封号。缓解：单账号串行、≥2~5s 间隔、每日一次、遇 1034 立即暂停并引导人工。
3. **凭据安全**：cookie_token 等同登录态，必须走 §3 的 DPAPI CurrentUser 加密落盘，绝不进日志、不进错误报告、不进 dist\、不进更新包。
4. **许可**：Snap.Hutao.Remastered / genshin.py = MIT（可参考，抄代码保留版权声明）；UIGF mihoyo-api-collect = CC-BY-NC（禁止商业，只能引用事实）；genshinhelper2 = GPL-3.0（不可抄入闭源）。**本设计只引 URL/参数/算法事实，不抄任何 GPL/CC-BY-NC 代码或文档文字**。
5. **明确不做**：不实现自动输入密码、不绕过验证码/极验、不模拟短信登录。扫码必须由用户自己在手机端（米游社 App）扫描并确认。
6. **合规边界**：本地单机保存自己账号的凭据属常见客户端行为，但「多账号自动签到」是否触碰米哈游用户协议需用户自行评估；本功能必须**默认关闭**、用户显式开启。

---

## 8. 实现拆分建议

### 8.1 能立刻做（无阻塞，纯 C#）

**任务 M1：扫码登录 + 凭据落盘（DPAPI）**
- 内容：passport createQRLogin/queryQRLoginStatus 两个 HTTP 调用 + QRCoder/Zxing.Net 生成二维码 + 从 Set-Cookie 提取 ltoken_v2/ltuid_v2/ltmid_v2/cookie_token_v2/account_id_v2 + SecretBox 加密写入 `<DataDir>\miyoushe\<账号>.json` + 失效检测。
- 验收：能生成二维码；轮询三态正确；Confirmed 后 cookie 字段齐全并 DPAPI 落盘（明文不出现在任何文件）；无 Node/Python 依赖；dotnet build 0 error。

**任务 M2：签到引擎（开关 + TickHub 定时 + 结果落库）**
- 内容：luna 签到接口（info/sign）+ DS 签名（cn_signin salt 做成可更新配置）+ `AppSettings.MiyousheSign{Enabled,Hour}` + TickHub 后台订阅每日触发 + 单账号串行限速 + `my_sign_log` 落库（幂等）。
- 验收：每日一次、多账号串行、0/-5003 区分正确、1034 暂停并标记 needs_login；不新增 DispatcherTimer；重跑不重复签到。

**任务 M3：旅行者札记拉取 + 落库 + BGI 对账**
- 内容：ys_ledger monthInfo/monthDetail 拉取 → `my_ledger_month`/`my_ledger_day` 落库；与 BGI 摩拉快照做趋势/下限对账（标注「非精确对账」）；等级<10 空数据容错。
- 验收：能拉到当月原石/摩拉与每日明细；等级<10 不崩、记录空态；对账逻辑输出「差异超阈值」提示而非「精确差值」。

### 8.2 有阻塞 / 需注意（不算硬阻塞，但必须提前决策）

1. **DS salt 漂移**：salt 是社区逆向值，随客户端版本失效。阻塞点：需定期更新 salt 或运行时探测。→ 实现时做成配置项 + 失败提示，不写死。
2. **接口实时可用性**：本文所有接口最后核实时间 2026-09~11，**均未在本机实测**（无凭据、也不该测）。M1 验收前需用户提供一次真实扫码确认跑通。
3. **WebView2 取舍**：本设计用纯 HTTP 扫码，**不需要 WebView2**（比 20261122 那份的「WebView2 推荐」更轻）。若未来要兼容短信登录才需要 WebView2，本轮不做。

### 8.3 建议任务数与顺序

建议 **3 个实现任务**：M1（登录+存储）→ M2（签到）→ M3（拉取+对账）。M1 是 M2/M3 的前置（都要 cookie_token）。每个任务独立可验收、可回滚，均不碰 UI（用户没要求界面，与运行记录库一致，界面展示另议）。

---

## 9. 来源汇总

- [seriaati/genshin.py](https://github.com/seriaati/genshin.py)（MIT）：`genshin/client/routes.py`、`genshin/client/components/daily.py`、`genshin/client/components/diary.py`、`genshin/utility/ds.py`、`genshin/constants.py`、`genshin/models/genshin/diary.py`、`genshin/models/genshin/chronicle/notes.py`。
- [UIGF-org/mihoyo-api-collect](https://github.com/UIGF-org/mihoyo-api-collect)（CC-BY-NC-4.0）：`hoyolab/login/qrcode_hoyolab.md`、`hoyolab/login/qrcode_hk4e.md`、`hoyolab/login/sms.md`、`hoyolab/user/token.md`。
- [y1ndan/genshinhelper2](https://github.com/y1ndan/genshinhelper2)（GPL-3.0）：`genshinhelper/mihoyo.py`、`genshinhelper/core.py`、`genshinhelper/utils.py`。
- [SnapHutaoRemasteringProject/Snap.Hutao.Remastered](https://github.com/SnapHutaoRemasteringProject/Snap.Hutao.Remastered)（MIT）：`ApiEndpoints.csv`、`GameRecordClient.cs`、`DataSignAlgorithm.cs`（架构对齐参考，本轮未逐行抓取，沿用 20261122 已核实结论）。
- 仓库内只读代码：`Services/Crypto.cs`（DPAPI）、`Services/ConfigStore.cs:374-378`、`Models/Models.cs:222-233`、`Services/TickHub.cs:95-107`、`Services/AppIdentity.cs:74/96-124`、`Services/RouteLedger.cs:157-161`、`Services/DailyStatsCache.cs:64-67`。

---

## 10. 未找到 / 待实测（不编）

- ❌ 「当前原石/摩拉余额」端点（米游社公开接口无此端点）。
- ❌ 「未绑定角色」的精确 retcode（社区未统一，实现按 `getUserGameRolesByCookie` list 为空判定）。
- ❌ 各接口当前（2026-11-25）实时可用性（无凭据，未实测；接口已明确标注「可能随时失效」）。
- ❌ 除 0/-5003 外的签到返回码权威定义（仅 -5003 有 genshinhelper2 源码注释，其余标「需实测」）。
- ❌ DS salt 的当前有效值（社区 issue 维护，未抓取，也不建议硬编码）。


---

## 11. 订正（2026-11-25 · t18 校准触发）

> 本节追加于 t18，订正本文档 §5 与 §0 里关于「摩拉逐条记录」的错误结论。

### 11.1 原结论错在哪

本文档 §5.1 曾断言「拿不到『当前原石余额』精确值」是对的，但 §5 与 §0 里「米游社只有月度收支统计、**没有逐条击杀记录**」这个说法是**错的**。用户本人已明确否定：「有逐条的摩拉记录」。

### 11.2 真实情况

- 米游社「旅行者札记」的 monthDetail 接口**返回逐条记录**：details 数组，元素形如 `{action, single_morale, count, morale}`，附时间（分钟精度）。
- 证据链：① 用户提供札记页面截图（三列：时间/获取途径/获取数量，逐条存在）；② t20 对 ABGI 解包确认 single_morale/count/morale 字段真实存在，落库 mark JSON 有 414 条档位桶；③ t16 已按此实现 monthDetail 全分页原始落库（my_ledger_entry，保留原始 time/action/num/single_mora/count）。
- 接口（国服）：GET `https://hk4e-api.mihoyo.com/event/ys_ledger/monthDetail?bind_uid=<uid>&bind_region=<region>&month=<n>&type=1|2&current_page=<p>&page_size=100`（type=1 原石 / type=2 摩拉）。

### 11.3 修正后的正确结论

| 项 | 订正后结论 |
|---|---|
| 逐条摩拉记录 | ✅ **有**（monthDetail details[]，一条 = 一次获取/击杀聚合桶） |
| 当前余额 | ❌ 仍**无余额端点**，只能月报累加估算（这条不变） |
| 单次击杀摩拉 | ✅ 字段 single_morale（单次值）+ count（次数）+ morale（总额） |
| 时间精度 | ⚠️ 到**分钟**，无秒（截图 + t16 落库 ts_ms 分钟级一致） |

### 11.4 对下游的影响

- t17 消费原始记录、t18 校准归类规则，都应基于「monthDetail 有逐条记录」这一正确前提。
- 600 的「600×1 vs 200×3」歧义在**数据层即可消解**：看 single_morale（单次值），不必靠路线名猜测（详见 MORA-CALIBRATION-20261125.md）。
