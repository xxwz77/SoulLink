# 工作交接 · 2026-11-22 远程控制 + 联机大厅大改造

> 日期：2026-11-22 夜间
> 状态：**代码已改完、已编译通过，但未提交、未发版**；多个调研任务已交付；3 个实现任务仍在跑
> 版本：源码仍是 **3.3.19**（未改 Version）

---

## 0. 一句话总结

用户抱怨"引入后端服务后变卡、画面不流畅"，从这条线索深挖，最终**否定了 5 个错误假设**，确定了正确路线：
**不改架构，只把抓屏从 GDI BitBlt 换成 WGC V2、把 JPEG 软编换成硬件 H.264。**
同时顺手完成了联机大厅流量优化（40MB/天 → 0）、转视角延迟优化（去掉 114ms 浪费）、日志事件锁存、RDP 配置两个 Bug 修复。

---

## 1. 用户的核心诉求（原话摘录）

| 需求 | 原话 |
|---|---|
| 远程画面要流畅 | 「以最小的性能做到像远程控制一样的效果」 |
| 手机端要支持 | 「手机端也要支持（期望是做个 APP 出来）」 |
| 帧率期望 | 「最高 60fps 吧」 |
| 摇杆+虚拟键盘 | 「摇杆一定要，能流畅的玩游戏很重要」「有编辑模式，可以改位置，可以保存位置，可以删除添加」「半透明，可以修改备注」 |
| 转视角方式 | **「是直接滑动屏幕」「是任意非按钮位置都可以」**（否决了右摇杆） |
| 内存重要 | 「多开游戏很吃内存，内存很重要的」 |
| 反作弊约束 | 「原神多开有限制，现在是会话强制隔离」「不会封号，不允许启动」 |
| 日志触发语义 | 「只要流程在运行，每出现一次，条件达到就记住，用完就清空。有end当天不再判断」 |
| 网页扩展 | 后期要加 BGI 数据访问 + 自动化流程编辑 |
| 米游社 | 需要扫码/短信登录，获取摩拉原石收益 |
| 会话瘦身 | 「最好分账号，不能分账号的独立出来，让用户选择」「明确告知用户它们是干嘛的」 |

---

## 2. 最终确定的技术路线

```
不改架构（RDP 多会话是唯一可行的隔离方案）
  ├── 抓屏：BitBlt → WGC V2（参考 BetterGI，MIT 系）
  ├── 编码：JPEG 软编 → 硬件 H.264（NVENC 先行，AMF/QSV 留接口）
  ├── 转视角：moverel 相对位移（已实现，已删 Sleep 浪费）
  ├── 手机手势：点 / 滑转视角 / 长按拖（参数已查证）
  └── 瘦身：分账号 316MB + 全机 491MB（机器级策略）
```

### 被否定的 5 条路（都有实测/权威证据）

| 路线 | 否定原因 |
|---|---|
| **DXGI 抓屏** | RDP 会话内实测不可用（`E_OUTOFMEMORY` / `DuplicateOutput` 失败） |
| **虚拟显示器（IddCx）** | 只治 console 会话的 headless 黑屏，**与 RDP 会话内抓屏无关**（parsec-vdd issue #82 铁证） |
| **本地会话多开** | Windows 只有一个 console，绕不过单实例限制 + 输入只送前台 |
| **虚拟机 + GPU-PV** | Easy-GPU-PV README 明确「Laptops with NVIDIA GPUs are not supported」+ 内存不够 |
| **自研远程会话 ID 驱动** | 需写内核显示驱动，工程量和风险远超收益 |

---

## 3. 我犯过的错误（重要，避免重犯）

| 错误判断 | 真相 | 教训 |
|---|---|---|
| 「60fps 不成立」 | 是 **BitBlt** 不行，WGC 单帧 <1ms | 别把单一 API 的结论泛化到整个技术类别 |
| 「Sunshine 在 RDP 里不行」 | 朋友实测**包能用**，我引的是 GitHub issue 二手信息 | **二手 issue ≠ 实测**，关键结论必须自测 |
| 「虚拟屏能救 GPU 抓屏」 | 只对 console 会话有效 | 分清会话层级 |
| 「显存只剩 10MB」 | 现在实测 **6464/8188，空闲 1493MB** | 那是瞬时高峰，不是常态 |
| 「注册表优化不完善」 | **根本没生效**（写用户分支，系统只读 HKLM） | 看 ADMX 的 `class` 属性 |
| 「CreateForMonitor 在 RDP 会失败」 | 来自 issue，未自测；朋友用 Sunshine 走这条能用 | 同上 |

**共同点：接受二手数据太快。关键数字必须自己复核。**

---

## 4. 已完成的代码改动（编译通过，未提交）

### 4.1 输入协议（`Services/InputActions.cs` + `Relay/`）

| 新增动作 | 说明 |
|---|---|
| `keydown` / `keyup` | 键盘按下/松开分离（原来 `key` 是点按式，摇杆没法"按住"） |
| `text` | Unicode 文本输入，正确处理 UTF-16 代理对，上限 1000 字符 |
| `moverel` | 相对位移（原本已实现，只是 HTTP 层没接线） |
| `releaseall` 扩展 | 断线时遍历按住的键逐个松开（防卡键） |

- `AgentWebServer.cs`：`InputDto` 加 `dx/dy/text`，switch 加四个 case
- 测试：`tools/layout-check/InputActionCheck.cs`，**ALL PASSED**

### 4.2 转视角优化

| 优化 | 效果 |
|---|---|
| 删除 `RelayAgent` 的 `Thread.Sleep(6)` 拆段 | **省 114ms**（原注释"大位移会被截断"经查证不成立） |
| `moverel` 改单向发送（`NoReply` 标志） | 省掉每帧 45~64ms 的 RTT |
| `LookInputQueue` 节流 20ms → 8ms | 50Hz → 125Hz |

### 4.3 手机手势（`docs/web-index.html`）

| 手势 | 行为 |
|---|---|
| 点一下 | 左键单击 |
| 按下后 250ms 内滑动 >10px | **转视角**（`moverel`） |
| 按住 250ms 后再滑 | 左键拖动 |

- `pointerType === 'touch'` 区分，鼠标/触控笔逻辑不变
- 参数依据：Hammer.js 251ms / @use-gesture 180ms / Android 400ms / iOS 500ms，取 250ms 折中

### 4.4 联机大厅（`Server/` + `Services/`）

| 改动 | 效果 |
|---|---|
| 按需访问（只有看页面/在房间才发请求） | **流量 40MB/天 → 0** |
| 发车人数 2/3/4 可配 | `Rooms` 表加 `DepartSize`，旧库自动迁移 |
| 默认地址清空 | 不再写死旧 IP `121.4.78.52` |

### 4.5 收益台账（`Services/RouteLedger.cs`）

- JSONL 追加写，按账号/日期分文件
- 归因规则用 **A1 账号 21 天真实日志验证，0 条收益脱窗**
- 挂载点：`BgiLogCenter.ProcessChunk`（不新增定时器）
- 日均 24 条记录 / 12KB，单账号一年 ~4.4MB

### 4.6 RDP 配置两个 Bug

| Bug | 根因 | 修复 |
|---|---|---|
| 删用户会回来 | 两处"删除用户"不联动（一个删配置、一个删 Windows 账户） | 删除后同步删配置 + 重新检测会话 |
| 通用配置不是主页 | 进页时 `_showGeneralSettings=false` → 走空状态 | `Activate()` 里首次进入时 `ShowGeneralSettings()` |

**文案已区分**：「移除此配置」vs「删除账户」

---

## 5. 已部署上线

| 项 | 状态 |
|---|---|
| 服务器 | `42.192.114.250:5080`（腾讯云轻量，Ubuntu 26.04） |
| 服务端版本 | **2.1.0** |
| 响应时间 | 38ms |
| 运行方式 | systemd，用户 `soullink`（非 root），内存上限 1G |
| 流量监控 | vnstat + 每日 08:00 日报 + 每小时阈值告警（102400 MB/月） |
| 运维脚本 | `/usr/local/bin/soullink-ops.sh`（status/backup/rollback/traffic/alarm） |
| 防火墙 | 5080 已放行（用户操作） |
| 客户端 | **需手动填地址**：设置 → 服务器 → `http://42.192.114.250:5080` |

**部署踩坑记录**：单文件自解压需要可写目录 → 建 `/home/soullink` + `/opt/soullink-server/cache`；Windows ssh 传 bash 命令会因 CRLF/引号出错 → 改为本地写 .sh 上传再执行。

---

## 6. 正在跑的任务（未完成）

| 任务 | 负责人 | 状态 |
|---|---|---|
| **WGC 三种形态实测（以 A2 身份）** | streamgap | 刚激活，**这是最后一个关键数字** |
| NVENC 调研（收窄到 N 卡） | lookopt | 跑着 |
| 注册表策略修复（写 HKLM） | rdpfix | 跑着 |
| 日志事件锁存 | loglatch | 跑着 |
| 会话瘦身说明页面 | （排队） | — |
| 米游社调研 | （排队） | — |

### 关键待办：WGC 实测

**必须做到的事**：
1. 用 **Interactive 计划任务以 A2 身份**启动探测（不能以 lummy 冒充，会报 `0x80070424`）
2. 测三种形态：`CreateForWindow` / `CreateForMonitor` / `BitBlt`
3. 记录单帧耗时，判断能否进 16.7ms

---

## 7. 关键实测数据（可信）

### 7.1 当前性能基线

| 项 | 数值 | 来源 |
|---|---|---|
| BitBlt 抓屏（RDP 内 1920x1080） | **22~31ms/帧** | Lead 实测 |
| 完整链路（抓屏+转换+JPEG q75） | **33.8ms/帧 → 29.6fps 上限** | Lead 实测 |
| 单帧大小 | 90~164 KB | 实测 |
| 空闲 CPU（无录制） | 3.31% 单核（整机 0.1%） | Lead 实测 |
| 整机 | 32 核 / 29.7GB 内存 / RTX 4060 Laptop 8GB | 实测 |
| **四会话工作集合计** | **约 33GB > 物理 29.7GB**（已在交换） | 实测 |

### 7.2 会话 43（A2）资源明细

总计 **56 进程 / 2018 MB**：

| 进程 | 内存 | 可否处理 |
|---|---|---|
| BetterGI | 184.8 MB | 挂机软件本体，必需 |
| explorer | 179.6 MB | **用户明确要求保留**（连进去要能操作） |
| StartMenuExperienceHost | 123.3 MB | 开始菜单本体，保留 |
| **SearchHost** | **110.1 MB** | 可关（+ webview2 共 **392MB**） |
| PhoneExperienceHost | 97.9 MB | 可关 |
| TextInputHost | 87.5 MB | 保留（打字） |
| **Widgets** | **55 MB** | 可关（+ 子进程共 **99MB**） |
| explorer | ... | |
| GameViewerServer（网易UU） | 44 MB | 已关自启动 |
| dwm / csrss / svchost | — | **绝对不能动** |

### 7.3 瘦身收益（实测）

| 层 | 手段 | 每会话 | 4 账号 |
|---|---|---|---|
| **第一层**（可分账号，默认开） | 杀进程 | **316 MB** | 1.26 GB |
| **第二层**（HKLM 机器级，默认关） | 关搜索+小组件 | **491 MB** | 1.96 GB |

### 7.4 注册表策略实测结论（perfmeasure 受控实验）

| 项 | 结论 |
|---|---|
| 策略键位置 | `DisableSearch` → `HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search`<br>`DisableWidgetsBoard` → `HKLM\SOFTWARE\Policies\Microsoft\Dsh` |
| 写 HKCU 有效吗 | **无效**（ADMX `class=Machine`，只读 HKLM）—— 现有代码写错了位置 |
| 进程层生效 | ✅ 写策略 + 杀进程 → **60 秒不回来，无需注销** |
| **UI 层生效** | ⚠ **需重启 explorer 或下次登录**，搜索框不会立刻消失 |
| 影响范围 | 任务栏搜索框消失 / Win+S 失效 / 触摸板手势失效 / **开始菜单打字搜索失效**；**开始菜单本体正常** |
| 回滚 | **只删值不删键**（`Dsh` 空键管理员也删不掉，`reg delete` 报 Access denied） |
| 实现坑 | PowerShell `New-Item`/`Remove-Item` 会报 Unauthorized，**必须用 `reg add`/`reg delete`** |

---

## 8. 关键代码位置（改动清单）

| 文件 | 改了什么 |
|---|---|
| `Services/InputActions.cs` | **新增** —— 键名解析、Unicode 拆分 |
| `Relay/RelayAgent.cs` | `SendUnicode`、`PressedKeys`、`ReleaseAllKeys`、`keydown/keyup/text` case、删 `Thread.Sleep` |
| `Relay/AgentWebServer.cs` | `InputDto` 加 `dx/dy/text`、switch 加四 case |
| `Services/RelayClient.cs` | `SendAsyncNoReply`、`MoveRelAsync` |
| `Services/LookInputQueue.cs` | 节流 20ms → 8ms |
| `Services/SessionInput.cs` | `LookRawAsync` |
| `Services/RouteLedger.cs` | **新增** —— 收益台账 |
| `Services/BgiLogCenter.cs` | 加一行 `RouteLedger.Feed` |
| `Views/ConsolePage.xaml.cs` | 默认选中通用设置、删除文案区分 |
| `Views/RemoteAdminPage.xaml.cs` | 删除账户时同步删配置、文案区分 |
| `docs/web-index.html` | 手机手势状态机 |
| `Server/*` | 发车人数、按需访问、schema 迁移 |
| `Services/LobbyApi.cs` | `DefaultBaseUrl = ""`（不再写死旧 IP） |

**测试**：`tools/layout-check/InputActionCheck.cs`（ALL PASSED）、`LogRetriggerCheck.cs`

---

## 9. 未验证 / 风险

| 项 | 状态 |
|---|---|
| **WGC 在 SoulLink 里能否用** | ⏳ 实测中（朋友用 Sunshine 可以，BetterGI 也在用） |
| WGC 单帧耗时 | ⏳ 待测 |
| NVENC 在 RDP 里能否用 | ⏳ 待测 |
| **NVENC 并发会话数限制** | ⏳ 待查（消费级 N 卡历史 2~3 路，新版 5~8 路，用户有 4~8 个号） |
| 手机手势手感 | ⏳ 需真机验证 |
| 转视角实机效果 | ⏳ 需真机验证（未在 A2 会话里跑过） |
| 收益台账端到端 | ⏳ 归因已验证，但真实挂机跑一晚的 JSONL 内容未复核 |
| 注册表策略端到端 | 进程层已实测；UI 层已实测；**但未在挂机账号上长期验证** |

---

## 10. 下一轮该做什么

### 最高优先
1. **拿到 WGC 实测数字** —— 决定 60fps 能否成立
2. **查清 NVENC 并发限制** —— 决定多账号能否都用硬编

### 次优先
3. 实现两套：WGC 抓屏（`CreateForWindow` 优先 → `CreateForMonitor` → BitBlt）
4. 实现 NVENC（先只做 N 卡，架构留 A/I 卡位置）
5. 实现会话瘦身两层（`SlimSession` + `ApplyMachinePolicy`）+ 说明页面

### 收尾
6. **所有改动统一提交**（现在未 commit，22 个修改文件 + 30 个新文件）
7. 升版本号（现在还是 3.3.19）
8. 走 `publish-release.ps1 -Full -Push` 发版

---

## 11. 构建注意事项（重要）

### WPF 增量构建会报假错误

跑 `dotnet build` 如果看到这类错误：

```
error CS1061: "ProcessPage"未包含"OpenSlimHelp_Click"的定义
error CS1061: "LogTriggerState"未包含"LatchedSeq"的定义
```

**先别急着改代码**，先跑：

```powershell
dotnet build SoulLink.csproj -c Debug -t:Rebuild
```

WPF 的临时生成项目（`SoulLink_<随机>_wpftmp.csproj`）会缓存旧状态，增量构建会报出源码里明明存在的符号"不存在"。

**本会话实测**：增量报 4 个 CS1061，`-t:Rebuild` 后 **0 错误**。

---

## 12. 生产环境注意事项（勿踩）

- ⚠ **不要动 `dist/stable/`**（挂机测试版，未授权）
- ⚠ **不要用 A1/A4 账号**（用户明确禁止）
- ⚠ **不要动用户生产 `dist/data/config.json`**
- ✅ A2 可用（用户授权），用完要注销
- ✅ 测试桌面：前台显示放桌面 3，需要焦点的放桌面 2
- ⚠ **不要结束用户正在跑的进程**（游戏、BetterGI、SoulLink）

---

## 12. 相关文档索引

### 最该先看的
- `docs/research/STREAM-GAP-20261122.md` —— **回答了「为什么串流软件能 60fps」**
- `docs/research/SESSION-ARCH-20261122.md` —— **四路线对比 + 为什么不改架构**
- `docs/research/SESSION-SLIM-DEEP-20261122.md` —— 瘦身两层设计

### 技术细节
- `STREAM-PLAN-V2-20261122.md` —— RDP 内 fps 上限测算
- `DUAL-RATE-DESIGN-20261122.md` —— 录制/直播双速率
- `MOBILE-CONTROLS-DESIGN-20261122.md` —— 摇杆数学 + 布局 JSON
- `VIRTUAL-CONTROLS-RESEARCH-20261122.md` —— 手势参数（有源码依据）
- `NVENC-DESIGN-20261122.md` —— 硬编设计
- `LOOK-OPTIMIZE-20261122.md` —— 转视角延迟分解

### 调研与审计
- `OSS-REMOTE-RESEARCH-20261122.md` —— Sunshine/Moonlight 源码分析
- `GITHUB-SWEEP-20261122.md` —— 40+ 项目清单
- `VDD-RESEARCH-20261122.md` —— 虚拟显示器为什么不行
- `CN-CLOUD-GAMING-20261122.md` —— 国内云游戏怎么做的
- `AUTOMATION-BUG-20261122.md` —— 「日志只触发一次」根因
- `DATAFLOW-AUDIT-20261122.md` —— 数据中心旁路审计
- `ROUTE-LEDGER-DESIGN-20261122.md` —— 收益台账

### 部署
- `LOBBY-OPTIMIZE-20261122.md` —— 流量优化
- `LOBBY-DEPLOY-2.1.0-20261122.md` —— 部署实况

---

## 13. 环境信息

| 项 | 值 |
|---|---|
| 机器 | Win11 29698 / i9-13900HX 32 核 / 29.7GB / RTX 4060 Laptop 8GB |
| 会话 | console(lummy,1) + A1(18) + A4(19) + A2(43) |
| 虚拟显示器 | GameViewer（网易UU，ROOT\DISPLAY\0000）+ OrayIddDriver（向日葵，ROOT\DISPLAY\0001）—— 都已闲置 |
| SDK | .NET 11.0.100（global.json 锁定） |
| 服务器 | 42.192.114.250:5080（Ubuntu 26.04，2.1.0） |
| SSH 方式 | `SSH_ASKPASS` + `tools/ssh-run/askpass.cmd`（密码不进命令行历史） |
