# 米游社核心接入（t16）交付

> 日期：2026-11-25；版本：3.3.42  
> 范围：每 Windows 账号一套米游社凭据、扫码、原神签到、旅行者札记原始流水。  
> 不含：其他游戏/社区任务/HoYoLAB 完整实现（t19），击杀归类（t17），网页可视化（t13）。

## 1. 文件与入口

- `MiyousheCredentialStore.cs`：每账号 `DataDir/miyoushe/<账号>.json` DPAPI 密文。
- `MiyousheStore.cs`：`DataDir/miyoushe/miyoushe.db`，WAL/busy_timeout/参数化 SQL/自动迁移。
- `MiyousheClient.cs`：passport QR、角色、luna 签到、ys_ledger 月报与逐条明细。
- `MiyousheService.cs`：QR 会话、串行执行、状态、限速退避。
- `MiyousheHost.cs:22`：`TickHub.RegisterBackground("MiyousheHost.Daily",60,...)`；`App.xaml.cs:704` 启动，幂等，无新 DispatcherTimer。
- `MiyousheCatalog.cs`：游戏/区域表和 `ISignInTask`，为 t19 扩展预留。
- `WebServer.cs:321,376,820`：全部在现有网页鉴权之后；仅数据 API，不加主程序 UI。

选择网页 API 的理由：项目已有 HttpListener/口令会话，扫码图片可直接由网页显示；不引入 WebView2，主程序不做文件/网络 IO，后续网页页面只消费这些接口。

## 2. API 清单

均要求 SoulLink 网页登录鉴权；请求/响应不会包含 cookie/stoken。

| 方法/路径 | 作用 |
|---|---|
| POST `/api/miyoushe/login/begin` body `{account}` | 创建 passport QR，返回 session/状态/PNG base64，不返回 ticket/url |
| GET `/api/miyoushe/login/poll?session=` | Created/Scanned/Confirmed；Confirmed 从 Set-Cookie 取凭据并 DPAPI 保存 |
| GET `/api/miyoushe/login/status?user=` | 未登录/已存储/需重扫，uid 与 aid 掩码 |
| POST `/api/miyoushe/settings` | `enabled/hour/salt/account`；响应只有 `saltConfigured`，不回显 salt |
| POST `/api/miyoushe/run` body `{account,action:"sign"/"ledger"/"both"}` | 单账号手动签到/拉取；串行限速 |
| GET `/api/miyoushe/pull/status` | 拉取状态，不含凭据 |
| GET `/api/miyoushe/archive?user=&limit=` | 原始 entries、days、months、signs、roles、credentialState；无筛选归类 |

QR 必须由用户用米游社 App 扫描并手机确认。没有密码/短信登录、极验绕过、自动点击或手动 Cookie 导入旁路。

## 3. 凭据安全与范围论证

复用 `SecretBox` 的 Windows DPAPI **CurrentUser**（`dwFlags` 不含 `CRYPTPROTECT_LOCAL_MACHINE`）。
主进程以一个 Windows 用户管理 A1-A4，因此只需该用户同机解密；LocalMachine 会允许本机其他用户解密，范围过宽。
整个凭据 JSON 加密为 `dpapi:<Base64>`，原子替换。读取强制 `dpapi:` 前缀，禁止 SecretBox 的旧密码明文回退用于米游社。
DB 只存 `my_cred_state`，不存 cookie。日志/异常是固定文本，不带 URI、请求 Cookie、Set-Cookie 或响应正文。

实测：虚构 cookie/ltoken/stoken 经 DPAPI 保存并完整取回；密文+2 个 SQLite DB 共 3 文件，UTF-8/UTF-16/hex 组合扫描 12 次，**0 命中**，文件前缀 `dpapi:`。探针模拟 API/状态/日志序列化也 grep 不到虚构 token。

## 4. 调度、开关、退避

配置 `MiyousheSignEnabled=false` 默认关闭，`MiyousheSignHour=8`，`MiyousheSignAccounts` 可账号级覆盖；`MiyousheDsSalt` 默认空，必须鉴权接口配置，不硬编码可能过期的社区 salt。
TickHub 每 60 拍检查；关闭/未到小时直接返回，零网络。每账号每日成功/已签到只执行一次，由 `(account,game_uid,sign_date)` 幂等；失败允许更新/重试。
账号和分页串行，默认随机 **2–5 秒** 间隔；HttpClient 20s 超时，响应限制 4 MB。
- -10001 / HTTP 401/403 → `needs_login`，保留密文，提示重新扫码。
- 1034/10035/5003/10041 → `risk`，至少 24h 暂停，人工处理，不绕验证码。
- 其他失败按 `MiyousheRetryMinutes`（默认 60，1–1440）退避。
- QR -3501 过期 / -3505 取消 → 引导重新生成。

## 5. 表结构与原始事实

`my_role`、`my_sign_log`、`my_ledger_entry`、`my_ledger_day`、`my_ledger_month`、`my_cred_state`、`my_pull_log`。

原始明细来自 `GET https://hk4e-api.mihoyo.com/event/ys_ledger/monthDetail?bind_uid=&bind_region=&month=&type=1|2&current_page=&page_size=100`；
月事实来自 `monthInfo`。每条 list 项保存：原始 JSON、`action_id/action/num`、原始 `time` 文本、分钟级 `ts_ms`、来源、拉取时刻、页内 `source_ordinal`。
`single_mora`（兼容原始 `single_morale`）和 `count` 可空；**接口不返回就 NULL，不填 0，不推断次数/击杀**。
同分钟多条（含同 action、同 num、同 JSON）通过内容+页内序号保留，不按时间去重。日/月事实并存，不代替原始行。
采集不按 action/金额过滤，不划分精英/小怪。札记延迟约一小时，时间只到分钟；t17 消费时必须保留这个误差边界。

每页一个事务，成功立即提交；第二页失败时第一页保留，批次记 failed，续拉去重。状态含 queued/running/inserted/skipped/failed。
旧库自动迁移：能补列的补列，旧主键表先改名冷备再 COPY；旧月数据无年份时使用 `legacy-unknown-year-MM`，不伪造年份。原冷备表不删除。

## 6. 验证证据

`dotnet build SoulLink.csproj -c Release`：两个 TFM 均 **0 error**（334 个既有警告）。
临时 console 直接链接生产源，DPAPI 用真实 Windows CryptProtectData，网络用无凭证模拟器：
- DPAPI 密文/解密/账号隔离/QR PNG。
- QR Created、Scanned、Confirmed、过期；未登录分支。
- 关闭签到零账号/网络；开启到点执行；重复 Tick 幂等；is_sign 与 -5003 均记 already。
- 模拟明细 4 条入库 4 条；同分钟 600/40/38 全保留；原始 JSON/action/num 完整；缺 single/count 为 NULL；11/25 分钟解析正确。
- 月缺字段 NULL，日汇总两资源不互相覆盖；重跑不重复。
- 第二页失败保留第一页并 failed，重跑不重复；完全相同原始行两条均保留；旧库迁移通过。
- API/状态/日志无明文凭据。

真实无账号探测（只发公开请求，不登录、不保存票据）：
```text
REAL_QR HTTP=200 retcode=0 dataFields=url,ticket ticket/url withheld
REAL_LEDGER_NOAUTH HTTP=200 retcode=-100 dataFields=
```
QR 公开创建端点实测可用；无凭据明细端点按预期拒绝，**没有真实账目 JSON，不冒充已验证**。
真实字段名 `time/action/action_name/num/single_morale/count` 是否存在，仍需用户本人扫码后拉取核实；截图只能证明页面展示逐条，不等于 API 已实测。

## 7. 手动验收与后续

1. 用户配置有效 DS salt；在鉴权网页调用 begin，用手机米游社扫码并明确确认，再 poll 到 Confirmed。
2. 拉取一次，用原始 API 对照札记页面，特别核实 `single_morale/count`；缺失应保持 NULL。
3. 手机已签到时执行签到，应记录 already；新一天确认每日一次与多账号 2–5s 串行。
4. 过期 cookie/风控只做重新扫码/人工处理引导；不做验证码自动化。

t19：向 `MiyousheCatalog` 添加区域/游戏，新增 `ISignInTask` 实现，并在单账号凭据内部按区域扩展；不变成一 Windows 账号多凭据主体。
t17：只读 `my_ledger_entry` 原始事实，另行版本化归类；不得回写修改原始行。
