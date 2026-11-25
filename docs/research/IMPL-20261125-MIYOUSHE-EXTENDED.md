# 米游社扩展签到（t19）交付

> 日期：2026-11-25；版本：3.3.43  
> 在 t16 骨架上扩展：双区域、全游戏配置、社区子任务、开关矩阵、批次逐项日志。  
> **没有实际登录账号或执行真实签到/社区写操作**；公开活动验证与模拟执行分别列证。

## 1. 结构与扩展方式

- `MiyousheCatalog.cs`：区域/游戏/社区/讨论区配置；`Validate()` 缺项自检。
- `MiyousheTaskClient.cs`：统一游戏/社区 HTTP 子任务；不实现验证码/挑战。
- `MiyousheBatchService.cs`：批次编排、开关/不参与、幂等、限速与指数退避。
- `MiyousheStore.cs`：`my_sign_batch` + `my_sign_task`；参数化 SQL，schema v4 自动建表。
- `MiyousheCredentialStore.cs`：同一 Windows 账号下按区域 DPAPI 密文：`A1.cn.json/A1.os.json`；旧 `A1.json` 继续作为 CN 冷备读取。
- `MiyousheClient/Service`：扫码、Cookie 名称、app_id、Referer、DS salt 按区域配置；t16 流水拉取仍保留。
- `MiyousheHost.cs:22` TickHub 60s 节拍，按账号区域读凭据；无新 DispatcherTimer。

扩展新游戏：向 `Games` 加 `MiyousheGameProfile`，填 region/endpoint/act_id/verification；未实测必须 `ActVerified=false`。新区域：加 `RegionProfile` 与独立 salt，扩展凭据 region 值，不复制 DB/批次代码。新社区任务：加 `CommunityProfile` 与请求构造。

## 2. 配置表与在线核实

区域：CN（米游社，v2 Cookie 名、cn salt）/ OS（HoYoLAB，v1 Cookie 名、os salt）。
游戏组合固定 2×5=10：原神/星铁/绝区零/崩3/崩2 × CN/OS。社区：like/share/read/forum。

公开 `home?act_id=` 请求结果（无 Cookie，不签到）：

| 项目 | act_id | 在线结果 | 执行状态 |
|---|---|---|---|
| CN 星铁 | e202304121516551 | retcode=0 awards=30 | 可启用 |
| CN 崩3 | e202306201626331 | retcode=0 awards=30 | 可启用 |
| CN 崩2 | e202203291431091 | retcode=0 awards=30 | 可启用 |
| OS 原神 | e202102251931481 | retcode=0 awards=30 | 可启用 |
| OS 星铁 | e202303301540311 | retcode=0 awards=30 | 可启用 |
| OS 崩3 | e202110291205111 | retcode=0 awards=30 | 可启用 |

**在线未核实清单（编排层强制跳过，绝不发签到请求）：**
- CN 原神 e202311201442471：home 实测 -500001；act_id 有双源码参考，但当前活动未核实。
- CN 绝区零 e202406242138391：home 实测 -500001。
- OS 绝区零 e202406031448091：专用域名和统一 public 域名均实测 -500001。
- OS 崩2：未找到可靠 act_id，配置为空。
- 社区任务：URL/参数来自 MIT 源码参考，未用真实账号验证，默认关闭。
- OS QR 创建：本机 HttpRequestException，真实 HoYoLAB 登录链路未验证。
- 所有“可启用”仅表示公开活动响应核实，**不表示真实账号签到已验证**。

## 3. 开关矩阵与配置

`MiyousheSignEnabled` 总开关默认 false；`MiyousheTaskEnabled[task@region]` 分项默认 false；
`MiyousheExcludedTasks` 键 `account|task@region` 为不参与列表；`MiyousheAccountRegions` 选择 CN/OS。
总开关关→整批 skipped 且客户端零调用；单游戏关/不参与→仅该项 skipped，其余继续；未核实项始终 skipped。
讨论区 `MiyousheForumIds` 至少一个，否则 forum 子任务 skipped“请至少选择一个讨论区”。
定时成功/already 的任务按日幂等，重启不会重复执行。

## 4. 批次日志与 API

`my_sign_batch(batch_id,account,state,started_at_ms,ended_at_ms)`；
`my_sign_task(batch_id,account,region,task_id,game_uid,state,retcode,detail,attempts,duration_ms,ts_ms)`。
每个账号/游戏/社区子任务独立 success/already/skipped/failed/risk/needs_login，尝试次数与耗时落库。

鉴权 API：
- GET `/api/miyoushe/catalog`：配置、验证状态、缺项。
- POST `/api/miyoushe/sign/run`：单账号批次。
- GET `/api/miyoushe/sign/logs?user=&limit=`：逐项日志。
- POST `/api/miyoushe/settings`：region/task/enabled/excluded/forums/delaySeconds/maxRetries/backoffSeconds/osSalt。
- POST `/api/miyoushe/login/begin` 可带 region；凭据/salt 永不在响应/日志出现。

## 5. 限速与退避

参考项目社区任务使用 3–8s 间隔；本实现为降低风控取**默认 12s**，允许 8–120s；账号串行，不并发。
社区失败：首次+最多 2 次重试（可配 0–3），指数退避 `min(base×2^retry,1800)`，默认 base=30s，即 **30→60→120s**。
429/1034/10035/5003/10041 直接 risk 停止，不自动解验证码，不消费重试绕过风控。

隔离探针：失败 3 次即停，虚拟时钟曲线 30/60s，尝试=3；多已核实子任务 Stopwatch 间隔样本≥20ms（虚拟等待），对应生产配置 12s。**未拿毫秒虚拟测试冒充真实网络速率**。

## 6. 验证命令与输出

`dotnet run --project Probe.csproj -c Release`（%TEMP%，直接链接生产源，真实 DPAPI + 模拟子任务）：
```text
PASS 配置表完整：2区域/10游戏组合/4社区任务
PASS 缺任一游戏配置自检能报出
PASS 双区域 Cookie 名称独立配置
PASS 同账号区域凭据独立加密保存取回
PASS 国际服凭据仍强制 DPAPI
PASS 总开关关：全部不执行，客户端零调用
PASS 单游戏关/不参与仅跳过该项，其余已核实项执行
PASS 未核实 act_id 项明确跳过且不发请求
PASS 批次每账号每子任务结果/尝试/耗时可查询
PASS 失败与已跳过状态严格区分
PASS 国际服使用区域凭据和区域客户端
PASS 讨论区未选择时明确跳过
PASS 失败3次即停，实测退避曲线30/60秒
PASS 社区失败不超过首次+2次重试
PASS 虚拟秒实测请求间隔≥20ms（对应生产配置12秒，实际网络默认8-120秒）
PASS 批次日志与异常不含凭据明文
RESULT T19 ALL CHECKS PASSED
```
`dotnet build SoulLink.csproj -c Release`：双 TFM 0 error（见任务 commandsRun 最终输出）。

## 7. 必须用户手动验收

1. 用本人账号分别在 CN/OS 手机扫码确认，凭据只落 DPAPI 密文。
2. 开启单个已核实游戏做一次真实签到，检查成功/already/首次绑定/风控。
3. 未核实活动由用户拿到当前有效 home 响应后，再更新 ActVerified，不可只改 act_id 强行启用。
4. 社区任务逐项开启，确认看帖3/点赞5/分享1/讨论区选择及风控退避；不自动处理验证码。
5. OS QR/国际服端到端需在可达网络环境验证，当前只实现骨架，不能宣称已通过真实链路。
