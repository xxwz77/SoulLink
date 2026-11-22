# 米游社扫码/短信登录与收益数据获取研究（MIYOUSHE-RESEARCH）

> 日期：2026-11-22
> 作者：virtual-controls（agent-team）
> 范围：**只读研究 + 写本文档**。不改业务代码、不动 dist/、不提交、不发版。
> 合规：**未实际登录任何米游社账号**，全部结论来自开源项目源码与公开文档的交叉核对。以下接口均来自第三方社区整理（UIGF-org / Snap.Hutao / genshin.py），非米哈游官方文档，**可能随时失效**。
> 关联：SoulLink 是 Windows 多账号 RDP 挂机工具，本调研回答「能否在工具内做米游社扫码/短信登录 + 抓取原神收益数据（每日委托/树脂/深渊/月报收益）」。

---

## 0. 结论摘要（TL;DR）

1. **方案完全可行，且 C# 有旗舰级现成参考：Snap.Hutao（胡桃工具箱，Remastered 分支，MIT，~935 星，2026-11-22 仍在活跃提交）**。它用 C# 完整实现了「WebView2 内嵌登录页 + Cookie 提取 + DS 签名 + 收益数据（每日/深渊/月报）拉取」，是我们最值得对齐的工程参考。
2. **登录有两条成熟路线**：
   - **A（推荐，Snap.Hutao 同款）：WebView2 内嵌米游社/米哈游通行证登录页**，用户在页内扫码或短信登录，工具从 WebView2 CookieManager 读 `ltoken/ltuid/cookie_token` 等 Cookie。**无需自己实现扫码/短信的加密流程**——这是绕过「极验人机验证」这类难点的标准做法。
   - **B（自建，需自己扛人机验证）：直接调 REST 接口**。扫码登录（`createQRLogin`/`queryQRLoginStatus`）**不需要极验**，相对干净；但**短信登录必须先过极验 GT3/GT4 人机验证**（`create_mmt` → geetest → `create_mobile_captcha` → `login_by_mobilecaptcha`），这是自建短信登录的主要成本。
3. **收益数据能拿到**：实时便笺（`dailyNote`，树脂/委托/派遣/洞天宝钱/质变仪）、深渊（`spiralAbyss`）、开拓月历/收益（`ys_ledger/monthInfo`+`monthDetail`，原石/摩拉按月分类）、角色列表、基础信息。**拿不到**：不通过米哈游官方网页活动接口就无法拿的「游戏内实时货币余额」精确值——收益接口拿到的是「按月统计」，不是「当前原石数量」。
4. **关键加密参数**：所有游戏记录接口需要 **`DS` 动态签名头**（`salt`+时间戳+随机数+body/query → MD5）+ 固定请求头（`x-rpc-app_version/client_type/device_id`、`User-Agent`、`Referer`、`Origin`、`Host`）。`salt` 按客户端版本/平台变化，社区在 issue 里维护（**我们不应硬编码抄 salt，而应走 WebView2 路线规避或自行定位**）。
5. **风险**：米哈游 API 未公开、可能随时封接口/风控（返回 `-1034` 需要过极验挑战）；第三方整理多声明「仅学习测试、勿滥用、禁止商用」。SoulLink 若要产品化此功能，需自行评估合规与风控。

---

## 1. 开源项目清单

> 星数/语言/许可/推送时间均来自 GitHub API 实时查询（2026-11-22）。

| 项目 | 星数 | 语言 | 许可 | 最后推送 | 活跃 | 功能定位 |
|---|---|---|---|---|---|---|
| **SnapHutaoRemasteringProject/Snap.Hutao.Remastered** | ~935 | C# | **MIT** | 2026-11-22 | ✅ 活跃 | **旗舰**：原神工具箱，WebView2 登录 + Cookie/SToken + DS 签名 + 每日便笺/深渊/月历/抽卡记录，C# 全栈 |
| seriaati/genshin.py | ~485 | Python | **MIT** | 2026-11-16 | ✅ 活跃 | HoYoLAB/米游社 API 封装（asyncio+pydantic），routes.py 是权威端点清单，含扫码/短信/极验/战报/月历 |
| UIGF-org/mihoyo-api-collect | ~483 | Markdown | CC-BY-NC-4.0 | 2026-09-13 | 🗄 已归档 | **登录/鉴权文档权威来源**（扫码/短信/密码/Token 全流程 + DS 算法），已迁移至 mihoyo-api-sdk |
| UIGF-org/mihoyo-api-sdk | ~28 | Python | MIT | 2026-09-14 | ✅ | 上者的 TypeSpec SDK 继任 |
| genshindev/api | ~823 | TypeScript | OSL-3.0 | 2026-03-31 | ⚠ 低 | 原神粉丝 API（游戏数据，非登录/收益） |
| y1ndan/genshinhelper2 | ~532 | Python | GPL-3.0 | 2023-07-01 | 🗄 停滞 | 米游社签到小助手（签到/树脂提醒），GPL |
| ashleney/genshinstats | ~271 | Python | MIT | 2023-02-19 | 🗄 停滞 | 已弃用，指向 genshin.py |
| torikushiii/hoyoverse-api | ~54 | Rust | AGPL-3.0 | 2026-10-13 | ✅ | 高性能粉丝 API（AGPL，商用注意） |
| genshin-kit/genshin-kit-node | ~149 | TS | Apache-2.0 | 2023-03-06 | 🗄 归档 | 原神玩家信息查询封装 |

**结论**：
- **C# 参考 = Snap.Hutao.Remastered（MIT，可直接对齐架构，但抄代码需遵循 MIT 保留版权声明）**。
- **端点权威清单 = genshin.py 的 `routes.py` + Snap.Hutao 的 `ApiEndpoints.csv`（两者可交叉验证）**。
- **登录/鉴权权威文档 = UIGF-org/mihoyo-api-collect（CC-BY-NC，只能引用思路与事实，不可商用直接套用其文档文字）**。

---

## 2. 扫码登录完整流程

> 来源：`mihoyo-api-collect/hoyolab/login/qrcode_hoyolab.md` + `qrcode_hk4e.md`，与 genshin.py `routes.py` 的 `CREATE_QRCODE_URL`/`CHECK_QRCODE_URL` 交叉一致。

### 2.1 米游社扫码（推荐，SoulLink 场景）

**步骤**：

1. **生成二维码**（POST）
   ```
   https://passport-api.miyoushe.com/account/ma-cn-passport/web/createQRLogin
   请求头：x-rpc-app_id = bll8iq97cem8（固定），x-rpc-device_id = 任意 UUID
   ```
   返回 `data.url`（二维码内容，含 `tk=` 参数）+ `data.ticket`（查询凭证）。

2. **展示二维码**：把 `url` 编码成 QR 图（`tk` 即 `ticket`）。

3. **轮询状态**（POST）
   ```
   https://passport-api.miyoushe.com/account/ma-cn-passport/web/queryQRLoginStatus
   请求体：{"ticket": "<ticket>"}
   请求头：同上 x-rpc-app_id / x-rpc-device_id
   ```
   `data.status` 三态：`Created`(未扫) / `Scanned`(已扫) / `Confirmed`(已确认)。
   返回码：`-3501` 过期、`-3505` 用户取消。

4. **确认后取 Cookie**：`Confirmed` 后，从**响应头 `Set-Cookie`** 里拿 `ltoken_v2`、`ltuid_v2`、`ltmid_v2`、`account_id_v2`、`cookie_token_v2` 等（即完整登录态）。`data.user_info` 里有 `aid`（米游社 UID）、`mid`（MiHoYo ID）。

**关键点**：
- **扫码登录不需要极验人机验证**，是全流程最干净的登录方式。
- `app_id=bll8iq97cem8` 是网页端固定值。
- 有效期内二维码可复用；过期需重新 `createQRLogin`。

### 2.2 游戏内扫码（GameToken，原神 hk4e）

``` 
生成：POST https://hk4e-sdk.mihoyo.com/hk4e_cn/combo/panda/qrcode/fetch   body:{app_id:4, device}
轮询：POST https://hk4e-sdk.mihoyo.com/hk4e_cn/combo/panda/qrcode/query   body:{app_id, device, ticket}
确认后 data.payload.raw = {"uid":"...","token":"<GameToken>"}
```
- 产出的是 **GameToken**（`uid`+`token`），需再经 `getTokenByGameToken` 换成 SToken（见 §4 Token 链）。
- 适用场景：想要游戏登录凭证（而非米游社社区凭证）。**对收益数据抓取而言，米游社扫码（2.1）已够用，GameToken 路线是备选**。

---

## 3. 短信登录完整流程（含极验人机验证）

> 来源：`mihoyo-api-collect/hoyolab/login/sms.md`，与 genshin.py `CREATE_MMT_URL`/`MOBILE_OTP_URL`/`MOBILE_LOGIN_URL` 交叉一致。

### 3.1 流程总览

```
① create_mmt（申请人机验证任务）→ mmt_key（+ 可能需要 geetest gt/challenge）
② 过极验（若 mmt_type=1）：完成滑块 → geetest_v4_data {lot_number, captcha_output, pass_token, gen_time, captcha_id, sign_token}
③ create_mobile_captcha（发短信）→ 用户收验证码
④ login_by_mobilecaptcha（登录）→ login_ticket（Login Ticket，30 分钟有效）
⑤ 用 login_ticket 换 ltoken/stoken（见 §4）
```

### 3.2 详细接口（国服 webapi.account.mihoyo.com）

| 步骤 | 方法 | 端点 | 关键参数 |
|---|---|---|---|
| ① 申请人机验证 | GET | `https://webapi.account.mihoyo.com/Api/create_mmt` | `scene_type=1`, `now`(秒时间戳), `reason`(URL编码), `action_type=login_by_mobile_captcha` |
| ③ 发短信 | POST | `https://webapi.account.mihoyo.com/Api/create_mobile_captcha` | `action_type=login/regist`, `mmt_key`, `geetest_v4_data`(JSON, URL编码), `mobile`, `t` |
| ④ 登录 | POST | `https://webapi.account.mihoyo.com/Api/login_by_mobilecaptcha` | `mobile`, `mobile_captcha`, `source=user.mihoyo.com`, `t` |

**① 返回 `mmt_data` 两种形态**：
- `mmt_type=0`（无需人机验证）：`mmt_data` 只有 `mmt_key`，直接进 ③。
- `mmt_type=1`（需人机验证）：`mmt_data` 含 `gt`(验证ID)、`challenge`、`mmt_key`、`new_captcha`、`risk_type=slide`。需用极验 SDK 完成滑块，得到 `geetest_v4_data`。

**④ 成功返回**：`data.account_info.weblogin_token`（= Login Ticket），也在响应 Cookie 中。

### 3.3 极验（GeeTest）要点与风险

- 米哈游短信登录的人机验证用的是 **极验（GeeTest）GT3/GT4**（`docs.geetest.com/gt4`）。
- **首次申请大概率无需验证**（`mmt_type=0`）；触发风控后要求验证。
- **自建短信登录的最大难点就在这里**：极验有服务端校验（`captcha_id`+`sign_token`+`pass_token` 必须与滑块行为匹配），纯脚本极难绕过，且绕过本身有法律/服务条款风险。
- **这就是为什么 Snap.Hutao 走 WebView2 内嵌登录页，而不自己发短信**——把极验交给浏览器/米哈游官方页面处理。

### 3.4 结论：短信登录该怎么做

- **推荐**：WebView2 加载米哈游通行证登录页（`https://user.mihoyo.com/#/login/captcha`），让用户在里面点「发送验证码」、过滑块、输码，工具只负责登录完成后从 CookieManager 取 Cookie。**完全不需要碰极验**。
- **不推荐**：自己实现 create_mmt → geetest → create_mobile_captcha → login_by_mobilecaptcha，除非能接受极验的维护成本与风控风险。

---

## 4. Token 链（登录态 → 收益数据所需 Cookie）

> 来源：`mihoyo-api-collect/hoyolab/user/token.md` + `other/authentication.md`，与 Snap.Hutao `ApiEndpoints.csv` 交叉一致。

### 4.1 Cookie/Token 全家福

| 凭证 | 字段名 | 作用 |
|---|---|---|
| Login Ticket | `login_ticket` | 登录即时凭证，**30 分钟有效**，用于换 LToken/SToken |
| LToken | `ltoken`(V1) / `ltoken_v2`(V2) | 查游戏账号信息、战报（与 `ltuid`/`ltuid_v2`、`ltmid_v2` 配对） |
| SToken | `stoken`(V1) / `stoken_v2`(V2) | 社区操作、换 Cookie Token / Game Token / Action Ticket |
| Cookie Token | `cookie_token` / `cookie_token_v2` | 收益接口（dailyNote/index/abyss/ledger）的核心凭证 |
| Account ID | `account_id_v2`/`ltuid`/`stuid`/`login_uid`… | 米游社 UID |
| MiHoYo ID | `mid`/`ltmid_v2` | 与 V2 token 配对 |
| Game Token | `game_token` | 游戏登录凭证 |
| Auth Key / Action Ticket | `authkey`/`ticket` | 抽卡记录、客服等特定接口 |
| Hk4e Token | `e_hk4e_token` | 原神网页活动标识 |

### 4.2 换 token 链路

```
扫码登录(2.1) ──→ Set-Cookie: ltoken_v2, ltuid_v2, ltmid_v2, cookie_token_v2, account_id_v2
短信登录(3.x) ──→ login_ticket ──→ getMultiTokenByLoginTicket(ltoken/stoken) ──→ …
```
关键交换接口（国服）：
- LoginTicket→LToken/SToken：`https://api-takumi.mihoyo.com/auth/api/getMultiTokenByLoginTicket?login_ticket=..&uid=..&token_types=3`
- SToken→Cookie Token：`https://api-takumi.mihoyo.com/auth/api/getCookieAccountInfoBySToken`
- SToken→Game Token：`https://api-takumi.mihoyo.com/auth/api/getGameToken`
- Cookie Token→Hk4e Token：`https://api-takumi.mihoyo.com/common/badge/v1/login/account`

**对收益数据最省事**：扫码登录确认后**响应 Set-Cookie 直接带齐 `ltoken_v2/ltuid_v2/cookie_token_v2`**，无需额外换 token；短信登录则要先用 login_ticket 换一轮。

---

## 5. 收益数据接口清单（能拿什么 / 拿不到什么）

> 来源：Snap.Hutao `GameRecordClient.cs` + `ApiEndpoints.csv`，与 genshin.py `routes.py`（`RECORD_URL`/`INFO_LEDGER_URL`/`DETAIL_LEDGER_URL`）交叉一致。国服根域 `api-takumi-record.mihoyo.com/game_record/app/genshin/api/`。

| 数据 | 端点（国服） | 需要凭证 | 返回内容 |
|---|---|---|---|
| **实时便笺** | `/dailyNote?role_id=<uid>&server=<region>` | cookie_token | 树脂(当前/上限)、每日委托完成数、派遣、洞天宝钱、质变仪状态 |
| **战报索引** | `/index?role_id=<uid>&server=<region>` | cookie_token | 基础信息 + 统计 + 探索度 + 家园 |
| **深渊** | `/spiralAbyss?schedule_type=<1|2>&role_id=..&server=..` | cookie_token | 当期/上期深渊成绩 |
| **角色列表** | `/character/list`（POST body `{role_id, server}`） | cookie_token | 角色列表 |
| **月历收益(月报)** | `https://hk4e-api.mihoyo.com/event/ys_ledger/monthInfo?month=<n>&bind_uid=<uid>&bind_region=<region>` | cookie_token | 当月原石/摩拉收入分类汇总 |
| **月报明细** | `https://hk4e-api.mihoyo.com/event/ys_ledger/monthDetail?...` | cookie_token | 每日明细 |
| 基础信息 | `/roleBasicInfo?role_id=..&server=..` | cookie_token | 昵称/等级/头像 |
| 七圣召唤/巨史/新深渊 | `/role_combat`、`/hard_challenge` | cookie_token | 额外玩法数据 |

**能拿到**：
- ✅ 树脂、每日委托、派遣、洞天宝钱、质变仪（dailyNote）——挂机工具最关心的「日常状态」。
- ✅ 深渊成绩、角色列表、探索度。
- ✅ **月度原石/摩拉收入**（ys_ledger）——「收益」的月粒度统计，可按分类（每日/活动/深渊/邮件等）细分。
- ✅ 抽卡记录（gacha，另走 authkey，见 genshin.py `GACHA_URL`）。

**拿不到（诚实声明）**：
- ❌ **「当前原石余额」精确值**：米游社公开接口只有「月度收支统计」，没有「账户当前余额」端点。余额只能从月报累加估算。
- ❌ 未经游戏内/官方网页活动授权的更细数据。
- ❌ 任何接口都可能被风控 `-1034`（要求过极验挑战）挡住，需在请求里补 `x-rpc-challenge`（Snap.Hutao `RetryIf1034Async` 处理）。

---

## 6. C# 集成方案

### 6.1 架构对齐 Snap.Hutao.Remastered（推荐）

```
WebView2 内嵌登录页（米哈游通行证）
   └─ 用户扫码/短信登录
   └─ CookieManager 读取 ltoken_v2/ltuid_v2/cookie_token_v2/account_id_v2
HttpClient + 请求构建器
   ├─ 固定头：User-Agent / Referer / Origin / Host / x-rpc-app_version / x-rpc-client_type / x-rpc-device_id / X-Requested-With
   ├─ DS 签名（DataSignAlgorithm）
   └─ 调 dailyNote / index / spiralAbyss / ys_ledger
```

### 6.2 DS 动态签名（C# 可直接对齐的实现）

Snap.Hutao 的 `DataSignAlgorithm.cs`（MIT）核心逻辑（**思想，非逐字抄**）：

```
t = 当前 Unix 秒
r = 随机串
dsContent = $"salt={salt}&t={t}&r={r}"
if (需要 body/query):  dsContent += $"&b={body}&q={query}"
check = MD5(dsContent)
DS头 = $"{t},{r},{check}"
```

**注意**：
- `salt` 按「客户端版本 + 平台 + client_type」变化，社区在 issue 维护（`Kamisato-Ayaka-233/mihoyo-api-collect` issue #1）。
- **不建议硬编码 salt 到产品里**：salt 会随客户端版本更新而失效。Snap.Hutao 通过 WebView2 拿到的是「网页端」Cookie，调的是「网页端」接口，salt 相对稳定，但仍是社区逆向所得。
- **我们若走 WebView2 路线，DS 签名仍是必经之路**（收益接口要 DS 头）。方案：对齐 Snap.Hutao 的 `DataSignAlgorithm` + 自维护 salt 配置（可更新，不写死）。

### 6.3 网页端 vs APP 端请求头

- 网页端：`x-rpc-client_type=4`、`Referer=https://www.miyoushe.com`、`x-rpc-app_id=bll8iq97cem8`。
- APP 端：`x-rpc-client_type=2`、`Referer=https://app.mihoyo.com`、`X-Requested-With=com.mihoyo.hyperion`、UA 带 `miHoYoBBS/2.x`。
- **收益接口用网页端凭证即可**，UA 用移动版 `Mozilla/5.0 (Linux; Android 13; ...) ... miHoYoBBS/2.51.1` 或 Snap.Hutao 的 `HoyolabOptions.MobileUserAgent`。

### 6.4 最小可行实现路径（给 SoulLink）

1. 内嵌 WebView2（或复用现有浏览器控件）加载 `https://user.mihoyo.com/#/login/qr`（扫码）或 `#/login/captcha`（短信）。
2. 登录完成后，从 WebView2 `CoreWebView2CookieManager` 读 `ltoken_v2`/`ltuid_v2`/`ltmid_v2`/`cookie_token_v2`/`account_id_v2`，持久化到 SoulLink 账号体系。
3. 用 Cookie 调 `/dailyNote` 等接口（带 DS 头），返回树脂/委托/深渊/月报。
4. `-1034` 时提示用户重新登录或过验证。

---

## 7. 风险与合规提示

1. **接口非官方公开**：米游社 API 无官方文档，全部来自社区逆向整理，**可能随时改版/封接口/加风控**。`mihoyo-api-collect` 已因「项目已过时」归档，说明接口漂移快。
2. **极验人机验证**：短信登录与风控触发后都要过极验。走 WebView2 让用户自己过，是最稳的做法；自建脚本绕过极验有法律与服务条款风险，**明确不做**。
3. **许可**：
   - Snap.Hutao.Remastered = MIT（可参考架构/思想，抄代码需保留版权声明）。
   - genshin.py = MIT（同上）。
   - UIGF-org/mihoyo-api-collect = **CC-BY-NC 4.0（禁止商业使用）**——只能引用事实与思路，不能商用套用其文档。
   - genshinhelper2 / hoyoverse-api = GPL-3.0 / AGPL-3.0（不可抄入闭源）。
4. **滥用/合规**：第三方整理普遍声明「仅学习测试、请勿滥用」。SoulLink 若产品化「多账号自动签到/抓取收益」，需自行评估是否触碰米哈游用户协议（自动签到/批量账号有封号风险）。
5. **账号安全**：Cookie（尤其 SToken/Cookie Token）等于登录态，SoulLink 存储时需与现有密码体系同级别加密，避免泄露。

---

## 8. 已证实 / 需验证 / 查不到 标注

### 8.1 已证实（多源交叉一致）
- ✅ 扫码登录两接口（`createQRLogin`/`queryQRLoginStatus`）URL、参数、返回结构（mihoyo-api-collect ↔ genshin.py ↔ Snap.Hutao 三源一致）。
- ✅ 短信登录四接口（`create_mmt`/`create_mobile_captcha`/`login_by_mobilecaptcha`）与极验 `geetest_v4_data` 字段（mihoyo-api-collect 文档详实）。
- ✅ Token 链（login_ticket→ltoken/stoken→cookie_token）各接口 URL（三源一致）。
- ✅ 收益接口：`dailyNote`/`index`/`spiralAbyss`/`character/list`/`ys_ledger monthInfo/monthDetail` 的 URL 与所需 Cookie（Snap.Hutao ↔ genshin.py 一致）。
- ✅ DS 签名算法（`salt=..&t=..&r=..&b=..&q=..` → MD5 → `t,r,md5`）（mihoyo-api-collect 文档 ↔ Snap.Hutao C# 源码一致）。

### 8.2 需验证（未在本机实测）
- ⚠ 各接口的**实时可用性**（文档最后更新 2026-09，米哈游是否已改版未实测）。
- ⚠ `salt` 当前有效值（社区 issue 维护，需运行时获取/更新，未抓取具体值——**也不应硬编码**）。
- ⚠ `-1034` 风控挑战的触发频率（Snap.Hutao 有处理逻辑，但我们未实测触发条件）。

### 8.3 查不到 / 未覆盖
- ❌ 「当前原石余额」端点（米游社公开接口只有月报统计，无余额端点）。
- ❌ 国际服（HoYoLAB）的扫码/短信细节（文档多只覆盖国服，国际服标注「未知」）。
- ❌ 米哈游官方接口文档（不存在公开官方文档）。
- ❌ 精确的 salt 列表（未抓取，属社区逆向成果，不宜抄入文档）。

---

## 9. 相关文档与来源

- [SnapHutaoRemasteringProject/Snap.Hutao.Remastered](https://github.com/SnapHutaoRemasteringProject/Snap.Hutao.Remastered)（MIT，C# 参考）
- [seriaati/genshin.py](https://github.com/seriaati/genshin.py)（MIT，`genshin/client/routes.py`、`docs/authentication.md`、`docs/battle_chronicle.md`）
- [UIGF-org/mihoyo-api-collect](https://github.com/UIGF-org/mihoyo-api-collect)（CC-BY-NC，`hoyolab/login/*.md`、`hoyolab/user/token.md`、`other/authentication.md`）
- 极验文档：https://docs.geetest.com/gt4/apirefer/api/web
