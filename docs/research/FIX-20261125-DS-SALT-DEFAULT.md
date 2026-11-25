# 米游社 DS salt 默认值与算法自检（t38）

> 日期：2026-11-25。范围：Models.cs 默认值、MiyousheClient.cs 授权错误提示；未改 WebServer、taskedit、A1。

## 默认值与证据边界

- 国服 `MiyousheDsSalt` 默认 `xV8v4Qu54lUKrEYFZkJhB8cuOh9Asafs`。
  来源：t20 ABGI Go 二进制解包 + MihoyoBBSTools 的 x4 salt，双源确认，公开社区值。
- 国际服 `MiyousheOsDsSalt` 不伪造独立默认值。
  t20 参考 hoyo_checkin 仅要求 Referer/Cookie，未给出独立 x4 salt；空值时不强发 DS。
  如用户后续有可靠国际服 salt，可配置覆盖；需要 DS 时提示填 `MiyousheOsDsSalt`。
- 国服 salt 为空时提示“请到设置页填写 MiyousheDsSalt（国服 x4 salt）”，不再只说未配置。

x4 salt 是公开社区值，服务端可能作废。保留设置覆盖能力；作废后用户更新对应字段。
不自动抓取/逆向新 salt，不尝试绕过验证码。

## DS 固定向量

```text
salt=xV8v4Qu54lUKrEYFZkJhB8cuOh9Asafs
t=1700000000
r=123456
body={"act_id":"e202311201442471","region":"cn_gf01","uid":"100000001"}
query=bind_region=cn_gf01&bind_uid=100000001&month=11

INPUT_RAW=salt=xV8v4Qu54lUKrEYFZkJhB8cuOh9Asafs&t=1700000000&r=123456&b={...}&q=bind_region=cn_gf01&bind_uid=100000001&month=11
EXPECTED=1700000000,123456,34c58d4e6e9fa457a9fb557a7ef56dec
ACTUAL  =1700000000,123456,34c58d4e6e9fa457a9fb557a7ef56dec
PASS CreateDs 固定时间/随机数与 md5 公式一致
```

验证：`md5(salt={salt}&t={t}&r={r}&b={body}&q={query})`，query 按键排序。
固定 t/r 保证以后改坏公式会立即被探针发现。

## 可执行验证

- 空 JSON 反序列化真实 AppSettings：
  `EMPTY_JSON_SALT_NONEMPTY=True`；`EMPTY_JSON_OS_SALT_EMPTY=True`。
- 覆盖 salt 后生成签名改变，证明用户配置覆盖保留。
- 模拟授权接口（虚构 token、无真实账号）：默认 salt 的请求实际发出 1 次，
  DS 头存在，且没有“salt 为空”本地异常：`AUTH_MOCK requests=1 hasDs=True`。
- `RESULT T38 ALL-PASS`。
- 双 TFM Release 0 error。

本验证未声称真实账号接口成功；真实 DS 接受度仍需用户登录后按服务端响应验收。
