# 部署验证 V2：新版本 4b5e99f 实测（2026-11-24）

> 日期：2026-11-24 上午
> 作者：impl-capture（dsh-team `team_mwtjn0d77v8gtr` / member `m_mwtkj5v0bq4cs9` / task `task_mwu20xkb9yaj54`）
> 性质：**只读验证 + 文档**。未改业务代码、未改注册表、未发布、未起新 agent、未碰挂机版/A1/A4。
> 证据等级：**【实测】**＝本次从真机日志/文件直取的原始数据；**【代码事实】**＝当前部署文件逐行/逐字节可证；
> **【引用】**＝本仓库前序文档，附出处；**【未实测】**＝本次无环境无法取得，明确标注不猜。
>
> ★ 关键口径：本次**未重跑受控三场景**（17768 测试 agent 已退出、端口 19081/19099 已无监听、token 未知，
> `/api/info` 返回 401）。实测指标来自新版本部署后**真实观看日志**（10:07:02–10:09:34，约 152 秒，
> 64 条「共享管线」行），与「受控三场景」口径不同，已在各处标注，**不混用**。

---

## 0. 部署确认

| 项 | 值 | 证据 |
|---|---|---|
| 部署文件 | `C:\Program Files\SoulLinkDev\SoulLink.Agent.exe` | 【实测】文件存在 |
| ProductVersion | **`3.3.34+4b5e99fc7be1e76bf604f2fae5b1f5b63a24a8dc`** | 【实测】`Get-Item().VersionInfo` |
| FileVersion | 3.3.34.0 | 【实测】同上 |
| 构建/部署时间 | 2026-11-24 10:05:01 | 【实测】文件 LastWriteTime |
| 测试 agent PID | 17768（session4/A2），10:06:21 启动、10:09 后退出 | 【实测】进程列表 + 日志 |

- 部署**成功且为新版本**：ProductVersion 后缀 hash 正是 `4b5e99f`，与任务目标一致。
- 日志 `10:07:01.268 [capture] DXGI-DDA 建立成功：1920x1080`，说明抓屏后端为 **DXGI-DDA**（非 BitBlt）。
- 注意：`git log` 显示 `4b5e99f` 这个 commit 本身是「团队红线 docs」提交（10:04:15）；部署 exe 的
  ProductVersion 取的是构建时 HEAD（=4b5e99f）。当前工作区 HEAD 已前进到 `45b1d2f`（「光标回传服务端实现」），
  比部署版本多一个提交——见 §5 剩余问题。

---

## 1. 实测帧率与管线指标（captureMs / convertMs / encodeMs）

来源：`C:\Users\A2\AppData\Local\Temp\soullink-relay-17768.log`，64 条 `[video] 共享管线` 行，
时间窗 10:07:02.708 → 10:09:34.708（152 秒），期间有观众在反复观看/断开。

| 指标 | n | 均值 | p50 | p90 | max | 判读 |
|---|---|---|---|---|---|---|
| **captureMs** | 64 | 20.2 | **8** | 68 | 88 | 长尾明显：p50=8ms 是 GPU 抓帧成本，p90/max 是 DDA 阻塞等帧（RDP 慢合成） |
| **convertMs** | 64 | 1.8 | **1** | 4 | 6 | **明显优于 baseline 的 7.8ms**，BGRA→NV12 并行化已生效 |
| **encodeMs** | 64 | 5.0 | **5** | 7 | 10 | NVENC-SDK 硬编，稳定 5ms 级 |
| **queueAgeMax** | 64 | 12.5 | **17** | 17 | 17 | 服务端 viewer 队列帧龄 17ms 封顶（队列深度 cap=2 生效，无拥塞） |
| **queueDrops** | 64 | 0 | 0 | 0 | 0 | 无丢帧 |
| **staticSkipped** | 全程 | 0 | — | — | 0 | 有观众时强制编码，不触发静止跳过（符合设计） |
| **keepalive** | 全程 | 0 | — | — | 0 | 同上 |

- 服务端单帧成本（p50）：capture 8 + convert 1 + encode 5 ≈ **14ms**；均值口径 ≈ 27ms。
- 服务端单帧成本（均值，含 capture 长尾）：20.2 + 1.8 + 5.0 ≈ **27ms**。
- 有效帧率：窗内 total 30→1920（+1890 帧）/ 152s ≈ **12.4 fps**。⚠️ 这是「观众频繁断开 + 静止桌面
  混合」的数值，**不能**代表动态场景 fps——断开期间画面静止，DDA 阻塞等帧拉低了平均值（captureMs 长尾同源）。

### 1.1 关键拓扑事实（决定零拷贝是否启用）

日志 `10:07:01.292` 起多处明确：

```
[gpu] 抓屏源=Intel(R) UHD Graphics(0x8086); 编码器=NVENC-SDK; 同GPU=false; 零拷贝=禁用
```

- RDP 桌面合成在 **Intel UHD**（NVIDIA 无显示输出），编码器是 **NVENC-SDK**，两者**跨 GPU**。
- **零拷贝=禁用是物理限制的正确降级，不是缺陷**（与 `E2E-LATENCY-BASELINE-20261124.md` §0 结论一致）。
- 代价是 BGRA→NV12 必须走 CPU 转换，但实测 convertMs 已并行化压到 p50=1ms，此段不再是瓶颈。

---

## 2. 端到端延迟对比（444ms 基线）

### 2.1 本次能否直接测端到端？—— 不能，如实说明

端到端延迟需要「viewer 上屏 + 双时钟同框（北京时间对照法）」或「服务端随帧下发 captureAt + 浏览器帧龄埋点」。
本次两者都不具备：17768 已退出、无 token（`/api/info` 401）、无 viewer。且日志无端到端时间戳
（这是既有协议缺口，`AUDIT-20261124-05` §5 已定性）。

**所以本次不给出新的端到端数字。** 下面用「服务端单帧实测」+「前序 baseline」两层给出对比。

### 2.2 服务端单帧对比（本次实测 vs 444ms 时代）

| 段 | 444ms 时代（HANDOFF-VIDEO-LATENCY-GPU） | 本次 4b5e99f（实测 p50） | 变化 |
|---|---|---|---|
| captureMs | 42 | 8 | **↓ 5×**（探测锁死解除 + DDA 事件驱动） |
| convertMs | 8 | 1 | **↓ 8×**（BGRA→NV12 并行化） |
| encodeMs | 3~8 | 5 | 持平（都是 NVENC-SDK 硬编） |
| 合计 | ~53ms | ~14ms | 服务端单帧显著下降 |

### 2.3 端到端（引用前序，非本次新测）

`E2E-LATENCY-BASELINE-20261124.md`（HEAD=0854e5a，P0-A~H 后）实测：**端到端 85~130ms（典型 ~90ms，上界 ~140ms）**，
对比最初基线 444ms **降幅 3~5 倍**。

- 本次 4b5e99f 相对该 baseline 的**服务端**单帧：convertMs 7.8→1.8ms（进一步下降），captureMs 量级一致（8~20ms）。
- 因此**端到端理论上不劣于 85~130ms，可能略优**，但**无本次新端到端实测数字**，不臆断具体值。

---

## 3. 三项 P0 改进验证结果（帧边界 / 按需 IDR / 输入 WebSocket）

> 判定口径：**「代码存在」= 部署文件逐行/逐字节可证；「运行实证」= 日志/端点行为可证**。
> 三项 P0 在**代码/字符串层面全部确认存在**；运行实证受「无 token、agent 已退出」限制，逐项如实标注。

### P0-1 帧边界（streamVersion=2，4 字节 length-prefix）

**前端 `web-index.html`（部署于 C:\Program Files\SoulLinkDev\，149KB）**：
- `resolveStreamVersion()` 从 `/api/info` 读 `streamVersion`，`>=2` 即启用 framed 模式（:1002-1018）。
- framed 读取循环：读 `[4B 大端长度][数据]`，`frameLen = (f[0]<<24|f[1]<<16|f[2]<<8|f[3])>>>0`（:1482-1506）。
- 切 NAL 的 `complete=true` 分支：length-prefix 保证帧完整，末 NAL 也直接切出（:1119-1124）。

**agent 后端 `SoulLink.Agent.exe`（二进制字符串）**：含 `streamVersion`、`length-prefix`、`/stream`、`/api/info`。

**判定**：✅ **代码完整存在（前后端均齐）**。⚠️ **运行实证未取得**：`/api/info` 返回 401，无法确认
实际下发的 `streamVersion` 是否 `>=2`（即真机走的是 framed 还是裸 Annex-B）。

### P0-2 按需 IDR（/api/idr）

**前端**：`requestIdr()` POST `/api/idr`（客户端限流 500ms，:1022-1035）；`recoverViaIdr()` 解码错误先请求
IDR ≤2 次、reset 解码器 + reconfigure 再等新 IDR，失败才断流重连（:1285-1306）。

**agent 后端**：exe 字符串含 `/api/idr`。

**运行实证**：日志多次出现 `[video] 新观众按需 IDR：即使画面静止也强制编码，不再被去重跳过`
（10:07:01 / 10:07:29 / 10:08:18 / 10:08:57）。

**判定**：✅ **服务端「新观众按需 IDR」运行实证生效**（新订阅强制出 IDR 的日志确凿）。
⚠️ **客户端「解码失败主动请求 IDR」路径未端到端触发验证**（无 viewer）。

### P0-3 输入 WebSocket（/ws/input）

**前端**：`wsInputUrl()` 生成 `ws://…/ws/input`（:1613-1619）；move/click/wheel/key 优先走 WS
（低延迟 + 天然保序），HTTP POST `/api/input` 保留为降级；WS 握手失败 → 本次会话永久回退 HTTP（:1604-1619）。

**agent 后端**：exe 字符串含 `/ws/input`。

**判定**：✅ **代码完整存在（前后端均齐）**。⚠️ **运行实证未取得**：无 token 无法完成 WS 握手，
无法确认真机实际走 WS 还是回退 HTTP。

### 三项 P0 汇总

| P0 | 代码存在 | 运行实证 | 未生效/未验证原因 |
|---|---|---|---|
| 帧边界 length-prefix | ✅ | ⚠️ 未验证 | 401 无 token，无法读 /api/info 的 streamVersion |
| 按需 IDR | ✅ | ✅ 服务端侧已实证 | 客户端「解码失败请求 IDR」未触发验证 |
| 输入 WebSocket | ✅ | ⚠️ 未验证 | 无 token 无法 WS 握手 |

**未生效的必须如实说明**：**没有任何一项被证明「未生效」**；但三项里只有「服务端按需 IDR」拿到运行实证，
其余两项是「代码齐全、运行行为未端到端验证」——原因是本次无 token/agent 已退出，属于**验证环境缺口**，
不是「发现代码缺陷」。

---

## 4. 无弹窗 / 断流 / 花屏确认

- **无弹窗**：本次全程只读文件 + 只读进程/端口查询，未启动任何进程，未触发任何 UI。日志亦无弹窗相关痕迹。
- **断流**：⚠️ **有 6 次「客户端断开」**（`HttpListenerException` 1229×1、22×5，`frames=550/39/44/1/174/1119`）。
  这是 viewer 端 socket 层异常（网络连接被关闭/设备不识别命令），**不是 agent 崩溃**（agent 进程持续运行，
  后续管线行正常续写），但会触发前端自动重连。**如实列为待查项**（§5）。
- **花屏**：日志无花屏/解码错误记录；服务端 `queueDrops=0`、关键帧头保护保留（宁超深不花屏）。**无花屏证据**，
  但画面是否真无花屏需 viewer 侧确认，本日志层面**未见**花屏。

---

## 5. 剩余问题清单

1. **跨 GPU 零拷贝物理禁用**（Intel 抓屏 + NVENC 编码）：正确降级，非缺陷；convertMs 已靠 CPU 并行化压到 1ms，
   零拷贝收益有限，**非当前延迟瓶颈**。无需在本机纠结。
2. **captureMs 长尾**（p90=68ms、max=88ms）：RDP 慢合成 + DDA 阻塞等帧，60fps 配置被 DWM 合成封顶
   （`RDP-FRAMERATE-AUTHORITY-20261124.md` 已定性）。这是服务端延迟的上限来源。
3. **6 次客户端断开**（HttpListenerException 1229/22）：需查 viewer 端为何频繁断开（前序
   `AUDIT-20261124-00` 提过「前端断流重连循环」是体感卡顿放大器）。
4. **无端到端帧龄埋点**：协议层无随帧 captureAt，端到端延迟只能靠双时钟同框，无法从日志量化（既有缺口）。
5. **部署版本落后工作区一个提交**：部署 exe ProductVersion=+4b5e99f（构建时 HEAD=4b5e99f），而当前工作区
   HEAD=`45b1d2f`（「光标回传服务端实现——之前只做了前端消费端，服务端从未回传 cursor 字段」）。若光标回传
   重要，需重新构建部署。
6. **本次验证口径受限**：未重跑受控三场景（无 token/agent 已退出），实测指标是「真实观看日志」而非
   「静止/鼠标/动画各 15s 受控」。若需受控数字，需 Lead 授权起独立 19099 agent（隐藏 wscript 投送 session4）。

---

## 6. 证据索引

| 结论 | 证据 |
|---|---|
| 版本 = 3.3.34+4b5e99f，已部署 | exe `Get-Item().VersionInfo`；文件 LastWriteTime 10:05:01 |
| 抓屏 DXGI-DDA、编码 NVENC-SDK、跨 GPU 零拷贝禁用 | `soullink-relay-17768.log:8-15` |
| 管线指标 64 行统计 | 同日志「共享管线」行，§1 已列分布 |
| 服务端按需 IDR 生效 | 同日志「新观众按需 IDR」×4 处 |
| 三项 P0 前端代码 | `C:\Program Files\SoulLinkDev\web-index.html` :1002-1018 / :1022-1035 / :1285-1306 / :1482-1506 / :1613-1619 |
| 三项 P0 后端字符串 | exe ASCII/UTF8 检索 `/api/idr` `/ws/input` `streamVersion` `length-prefix` 命中 |
| 端到端 baseline（引用） | `docs/research/E2E-LATENCY-BASELINE-20261124.md` §2（85~130ms） |
| 444ms 基线（引用） | `docs/research/HANDOFF-VIDEO-LATENCY-GPU-20261124.md` §2.1 |
