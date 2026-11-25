# t40 交付：米游社 device_id 按账号持久化（风控修复）

> 日期：2026-11-25（t40，attempt 0325a3ea）。P1：每次扫码换新 Guid 是典型风控特征。

## 1. device_id 持久化（核心）

- **取值顺序**（`MiyousheCredentialStore.GetOrCreateDeviceIdAsync(account)`）：
  ① 已有凭据里的 `DeviceId`（老账号**不换设备**——t40 硬要求 3）→ ② `<DataDir>\miyoushe\<账号>.device`
  落盘文件 → ③ 首次生成 Guid(D) 并**立即落盘**；
- **凭据删除不删 device 文件**（同一台机器还是这台设备，与真实客户端一致）；手动重置位置已写入
  代码注释：删 `<DataDir>\miyoushe\<账号>.device`；
- 接线：`MiyousheService.BeginQrAsync` 先取持久化 device 再调 `CreateQrAsync(ct, device)`；
  `PollQrAsync` 的无凭据兜底也改走 `GetOrCreateDeviceIdAsync`（原 `new Credential{DeviceId=NewGuid}` 落不了盘）；
- **敏感度与凭据同级**：grep 证明 device/DeviceId 在 WebServer 响应 0 处、Miyoushe* 日志/异常调用 0 处；
  `MiyousheLoginStatus`/`MiyousheQrSession` 字段均不含 device。

## 2. x-rpc-device_fp：未实现（诚实说明）

t20 解包只拿到 `device-fp/api/getFp` 的**端点 URL**（ABGI-SOURCE-API §3.3），**请求/响应契约未捕获**
（Go 二进制字符串层证据）。按任务指示「没有可靠算法→不伪造」：盲目 POST 该端点本身就是探测行为，
伪造随机 fp 比缺头更糟。**结论：fp 跳过，只做 device_id 持久化。**（若后续拿到真实抓包契约，
在 AddCommon 追加一行即可——位置已在注释里预留思路。）

## 3. 实测验证（tools/t40-verify，真实接口 + 头捕获）

| 断言 | 结果 |
|---|---|
| 同账号两次 CreateQr → x-rpc-device_id 完全相同（85be120a…） | PASS（修复前每次新 Guid） |
| 两次都拿到真实 ticket（b4a3254f… / cf2676b2…，retcode=0 路径） | PASS |
| device 头恰 1 个（配合 t37 的 app_id 单发） | PASS |
| `<DataDir>\miyoushe\A4.device` 落盘且内容与请求头一致 | PASS |
| 重启模拟（重读文件）→ 仍是同一 device | PASS |
| 不同账号（A2=a29b5ff1…）不同 device，不共用 | PASS |

## 4. 构建

双 TFM `-f net11.0-windows` / `-f net11.0-windows10.0.26100.0` 均 0 error。
改动：MiyousheCredentialStore.cs（device 文件+取值链）、MiyousheClient.cs（CreateQr 加 device 参数、
Poll 兜底）、MiyousheService.cs（BeginQr 接线）——全部 in-scope；未碰 WebServer/taskedit/RunRecord*；
A1 零触碰；验证工程 bin/obj 已清。
