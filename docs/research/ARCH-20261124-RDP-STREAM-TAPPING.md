# 架构评估：能否直接拿 RDP 的画面（5 条路径可行性 + 延迟代价对比）

> 日期：2026-11-24
> 作者：transport-network-analyst（dsh-team `team_mwu35l8pae8oj5` / member `m_mwu37cxvahbb82` / task `task_mwu6ve1mjy4cy2`）
> 性质：**只读评估**。未改任何产品代码/配置/注册表；未碰 `dist\stable\SoulLink-Stable.exe`（PID 13392）；未用 A1/A4 账号；未启动/断开任何 RDP 连接。唯一写入 = 本文档。
>
> 证据分级：【代码事实】= 仓库源码 file:line；【本机实测】= 本次只读查询（进程/注册表/netstat）；【官方文档】= MS Learn / GitHub 一手抓取；【已有实测】= 仓库前序文档；【推断】= 有依据推导；【行业共识】= 多来源一致但无单一官方出处。

---

## 0. 先正面回答用户的质疑：「我们的主程序不是 RDP 吗？agent 不是先通过 RDP 才启动的吗？」

**不矛盾，因为 RDP 在 SoulLink 架构里只是「看画面的客户端」，不是「会话载体」：**

| 角色 | 实际机制 | 证据 |
|---|---|---|
| 主程序的 RDP | **纯客户端嵌入**：`Controls/RdpConsole.cs` 手写 AxHost（CLSID v9）承载 MsRdpClient ActiveX，连本机 3389 **看**某个会话画面，不创建会话 | 【代码事实】`RdpConsole.cs:19, 24, 38`；`RdpNonScriptable.cs`、`Views/ConsolePage.xaml.cs`、`ConsolePage.Tiles.cs` |
| agent 的启动 | **与 RDP 协议无关**：`Services/AutoStart.cs` 用 `schtasks /create /xml` 建计划任务（注释明确：用 XML 是因为 /sc onlogon 不支持「指定用户+最高权限」），以目标用户身份在其交互桌面启动——console(session1) 或 rdp-tcp#2(session4) 都能进 | 【代码事实】`AutoStart.cs` XML 计划任务 |
| 会话的真正载体 | Windows 会话管理器（smss/winlogon）+ **TermWrap 修补的 termsrv**（见 §1.B）——会话因「用户登录」而存在，不因 RDP 连接而存在；RDP 连接只是把已存在的会话「接到某个客户端显示」 | 【本机实测】4 会话并行（console=1 lummy + rdp-tcp#0/1/2 = A1/A4/A2）与 agent 进程表（session 2/3/4 各一） |

**一句话**：桌面会话是「登录」出来的，agent 是「计划任务」启动的，RDP 只是主程序「观看」某个已存在会话的一扇窗。所以「agent 自己当 RDP 服务端」这条路径的矛盾不在启动，而在**画面所有权**（§2 路径 2 详述）。

---

## 1. 关键事实基座（本机实测 + 代码事实）

### 1.A 现状链路里已经有一个「RDP 客户端拿画面」的活样本

netstat 实测（2026-11-24 12:25）：
```
TCP 127.0.0.1:49992  127.0.0.2:3389  ESTABLISHED  13392   ← dist\stable 挂机版（SoulLink-Stable）
TCP 127.0.0.1:63380  127.0.0.17:3389 ESTABLISHED  13364   ← 主程序
```
主程序与挂机版都在通过 **环回 RDP（127.0.0.N:3389）** 连接会话拿画面——这正是 `RdpHostProbe` 的设计场景：本机所有环回 RDP 客户端在服务端看来都是 127.0.0.1（WTSClientAddress 相同），所以「用会话号推 127.0.0.N」不成立，必须**实测**哪个 127.0.0.N 能落到目标会话（`Services/RdpHostProbe.cs:10-18` 注释原文）。

### 1.B RdpWrapService / RdpHostProbe 的角色（用户要求核实）

| 组件 | 做什么 | 对本评估的影响 |
|---|---|---|
| **`Services/RdpWrapService.cs`** | 管理 **TermWrap（RDP Wrapper 家族）**：内嵌 TermWrap.dll/Zydis.dll/UmWrap.dll/EndpWrap.dll（`:66-72`），写 `TermService\Parameters\ServiceDll` 指向它（`:56-59`），重启 TermService 生效（`:49-52` 注释：ServiceDll 只在服务启动时读） | **本机已生效**【本机实测：ServiceDll = `C:\Program Files\RDP Wrap\TermWrap.dll`；svchost 同时加载 termwrap.dll + termsrv.dll】。它修补的是 termsrv 本身（解锁多用户并发 RDP——A1/A4/A2 三会话并行的根基）。**它不是自建 RDP 端点**，反而强化了「termsrv 是唯一会话世界」的现状：任何第三方 RDP 服务端都无法共享 TermWrap 维护的会话/图形栈 |
| **`Services/RdpHostProbe.cs`** | 探测「连过去能落到目标用户会话」的环回地址（候选 127.0.0.1~10 + 会话号，`:26-37`），缓存记住有效的 | 纯 RDP **客户端**辅助——为 `RdpConsole` 的 ActiveX 连接选地址。不影响「自建 RDP 端点」判断，但证明了环回 RDP 画面路径已在生产使用 |

**结论**：SoulLink 已经在用 TermWrap 解锁并发会话。自建 RDP 端点（IronRDP server 等）与此**正交且不兼容**——TermWrap 的价值在 termsrv 会话生态内，第三方端点拿不到这套会话的图形数据。

### 1.C 延迟基线（要被对比的数字）

- **RDP 自身端到端**（同机环回）：物理下限 20~38ms、典型 20~60ms（`AUDIT-20261124-08` §1；DWM 合成节拍 p50=15.2ms + RDP 图形管线无 CPU 回读 + 客户端直绘）。
- **SoulLink 现状服务端段**（DDA 抓屏→编码→入队→传输）：direct-probe 实测帧 gap p50≈55ms（出帧 ~16fps）+ agent 侧 capture(含等帧 8~89 波动)/convert 2/encode 5/queue 0~18 + 传输 1ms ≈ **典型 46~63ms**（`TOOL-20261124-DIRECT-PROBE.md` §3.1 + `AUDIT-08`），且浏览器段再加 25~45ms。

**判定基准**：任何「直接拿 RDP 画面」的路径，若总延迟 < 46~63ms 且不破坏现有 RDP 连接，才有价值。

---

## 2. 五条路径逐条评估

### 路径 1：主程序环回 RDP ActiveX 拿画面 → 再截取转推（「客户端二次中继」）

**机制**：主程序已有 `RdpConsole`（MsRdpClient ActiveX）连 127.0.0.N:3389 显示会话画面（§1.A 实证）；把该 ActiveX 窗口的画面截下来（BitBlt/DDA 抓本地窗口）→ NVENC 编码 → 推给网页 viewer。

**逐跳延迟测算【推断，逐项标注实测出处】**：

| 跳 | 耗时 | 出处 |
|---|---|---|
| RDP 传输到本地（termsrv→ActiveX 渲染） | 20~60ms（=RDP 自身端到端） | `AUDIT-08` §1 |
| 本地窗口截屏 | 5~31ms（BitBlt 27.2ms 实测 / DDA 亚 ms 但受本地 DWM 节拍） | `STREAM-PLAN-V2` §1.1 |
| 转换+编码 | 2+5ms | direct-probe 实测口径 |
| 推网页传输 | 1ms | 本机回环实测 |

→ 总计 **~28~98ms，典型 ~60ms**：**不比现状快**（46~63ms），因为叠了一整层 RDP 传输 + 二次压缩。
**可行性**：技术可行（组件全在），但延迟劣化 + 画质双重压缩损失（RDP 已经压缩过一次，再 H.264 有明显代际损失）。
**console 会话**：✓ 覆盖（ActiveX 连 console 的环回地址即可——lummy 会话画面本来就在主程序里）。
**影响现有 RDP 连接**：共享同一环回连接不新建会话（shadow/attach 语义另议），但多 viewer 观看同一 ActiveX 需要分发。
**判定：❌ 不做**（延迟不降反升 + 双重压缩）。

### 路径 2：agent 自己当 RDP 服务端（FreeRDP shadow / IronRDP server 在 agent 进程内开端点）

**机制级矛盾（用户指出的，正面写死）**：

1. **「被 RDP 连入」= termsrv.dll 创建 rdp-tcp 会话**。agent 已经运行在 session4 **内部**（那是 termsrv 创建的会话桌面）；再叠加 FreeRDP shadow / IronRDP server，等于同一桌面出现**第二个 RDP 服务端**——它要么自己 DDA 抓屏（FreeRDP Win shadow 实测就是 `win_dxgi.c` DXGI 桌面复制 + `SendInput`，`server/shadow/Win/` 源码确认），要么用 WDS API（见路径 5），**都拿不到 termsrv 图形栈**（DWM 重定向 surface 是 termsrv 专属通道）。
2. **若 IronRDP server 另开会话**：它抓到的是自己新建会话的桌面，不是用户要看的 A2 挂机桌面——答非所问。
3. **console 会话（session1）在 Windows 上无法作为 RDP 服务端被连接**：RDP 服务端语义 = termsrv 监听 3389 接受入连并创建/重定向会话；console 会话是物理登录会话，Windows 不提供「把 console 暴露为 RDP 服务端」的原生机制（mstsc 连过去是接管/take over，见硬约束 §3.1）。**用户主号场景（lummy console）这条路直接覆盖不了。**
4. 与 TermWrap 冲突：本机 termsrv 已被 TermWrap 修补（§1.B），多用户会话生态全在 termsrv 内；第三方端点自成一体，图形数据无源。

**判定：❌ 不可行（机制级，非「有风险」）**——画面所有权、会话语义、console 覆盖三重矛盾。

### 路径 3：mstsc /shadow（微软原生会话镜像，绕过自建端点）

**机制**：`mstsc /shadow:<sessionID> [/control] [/noConsentPrompt]`（MS Learn mstsc 命令文档，本次 HTTP 200 抓取确认参数存在）——**由 termsrv 自己把已存在会话的图形流镜像给 shadow 客户端**，这正是「拿 RDP 协议层画面」的唯一官方通道，且**不经二次采集**。

**关键评估**：
- **画面来源 = termsrv 图形栈原生输出**（同 RDP 客户端视角，无 DDA、无二次压缩）→ 延迟与 RDP 客户端同级（20~60ms）。
- **console 会话覆盖**：✓ —— shadow 是 termsrv 对「已存在会话」的镜像，console(session1) 同样是已存在会话。**但**：shadow 到 console 的图形流走 RDP 编码管线，画面 = console 的 DWM 合成结果，可行【推断：termsrv shadow 语义不区分会话类型】。
- **但是**——SoulLink 要把 shadow 流**再转推给网页**：shadow 客户端（mstsc/FreeRDP client）解码后的画面要再抓屏→编码→推流 = **路径 1 的二次中继结构**，延迟叠加同源（~60ms+）。除非网页 viewer 直接就是 RDP 客户端（HTML5 RDP——见路径 4 备注）。
- **同 session 的 shadow 需要权限**：shadow 别人的会话需要管理员或组策略授权 + 被镜像用户同意（除非 /noConsentPrompt + 策略允许）——A2 用户会话被主程序 shadow 需要 SYSTEM/管理员身份 + `HKLM\SOFTWARE\Policies\...\Shadow` 策略配置【本机实测：该策略键当前未配置】。
- **「自连接 3389 是否踢人」硬约束（§3.2）也适用于 shadow**：shadow 是 attach 语义不踢人，但**普通 RDP 连接同账号同会话是接管语义**。

**判定：△ 技术可行但结构上是「路径 1 的变体」**——shadow 只解决了「画面来源更原生」，转推网页的二次中继延迟仍在。若目标是**原生客户端直接看**（不经网页），shadow + mstsc 是零开发量的合规路径；若目标是网页 viewer，不划算。

### 路径 4：HTML5 RDP 客户端直接进网页（Guacamole / Myrtille / IronRDP-Web）

**机制**：网页 viewer 直接说 RDP 协议（Guacamole 的 guacd 做 RDP 客户端→转 WebSocket 自有协议；IronRDP 有 WebAssembly 目标——README 原文 "powers native binaries, WebAssembly modules, and C#/.NET clients, servers, and proxies"）。

**评估**：
- **画面来源原生**：RDP 客户端视角（同 mstsc），延迟 = RDP 传输 + 浏览器渲染 ≈ 20~60ms + 浏览器段——**这是五条路径里唯一能同时满足「原生画面源 + 网页直达」的**。
- **console 会话**：✓（RDP 客户端连 console 的环回地址=接管本地屏？不——Guacamole/IronRDP 以目标账号连接，行为同 mstsc：连接已登录账号 = 接管该会话；连接不同账号 = 新会话。**要看 console(lummy) 的画面仍需接管或 shadow**，见 §3.1 硬约束）。
- **代价**：这是一次 viewer 架构替换——丢掉 SoulLink 现有 WebCodecs/帧边界/按需 IDR 全套资产，引入 RDP 协议栈（guacd 常驻服务或 Rust→WASM），输入通道、剪贴板、光标全部重做。`LOW-LATENCY-DEEP-DIVE-20261124.md` 已实测 Guacamole 输入轮询 1000ms 等结构税（服务端轮询语义），其「低延迟」神话需逐项复验。
- **与现有 RDP 连接的影响**：新增一条客户端连接（环回），不踢人（不同客户端连不同会话）；但连**同一会话**第二个客户端默认接管。

**判定：△ 长期方向可行（若产品愿意换 viewer 底座），短期工程量大（1~3 个月级），延迟收益 = 省掉 SoulLink 服务端采集/编码段（~30~50ms），但 RDP 自身 20~60ms + 浏览器段照付**。总延迟 ≈ 现状 RDP 客户端的体感（20~60ms+浏览器渲染）——**对「接近 RDP 体感」的目标这是正确路线**。

### 路径 5：Windows Desktop Sharing API（RDPRAPI / IRDPSRAPISharingSession，微软官方桌面共享服务端）

**机制**：FreeRDP Win shadow 的 `win_wds.c` 揭示的微软官方 API（`Rdpcomapi.RDPSession` COM）——进程内创建「桌面共享会话」，**画面来源是 RDP 图形管线（Remote Assistance 同款）**，客户端用普通 RDP 协议连入。本机 COM 注册实证：`HKLM\SOFTWARE\Classes\Rdpcomapi.RDPSession` 存在【本机实测】。

**评估**：
- **画面来源**：RDP 编码管线原生输出（非 DDA）→ 延迟与 RDP 同级。**这是「agent 进程内当 RDP 服务端」的唯一合法形态**——但注意它与路径 2 的差别：WDS 不创建新会话，**共享的是本进程所在会话的桌面**。
- **console 覆盖**：✓/△——agent 若以 SYSTEM 服务模式跑在 session 0，WDS 可共享 console 桌面（FreeRDP `win_shadow.c` 的 `OpenInputDesktop` 跟随逻辑证明服务模式可达 console）；agent 以用户身份跑在 session4 则共享 session4。
- **代价**：①共享会话需要 viewer 端还是 RDP 客户端（网页 viewer 又回到路径 4 的底座问题）；②WDS API 微软已**多年未演进**（Remote Assistance 生态半废弃），栈的维护性差；③多 viewer、码率控制、帧率可观测性都要自己在 WDS 之上重建（远不如 SoulLink 现有 /stream 管线的可控性）；④**与 TermWrap 的关系**：WDS 不依赖 termsrv 的会话创建（自成共享端点），与 TermWrap 并存无直接冲突——但它输出的是 RDP 协议流，网页消费仍需 RDP 客户端中转。
- **判定：△ 理论可行、实证成本高**——延迟收益同路径 4，工程风险（API 老化、无社区参照、SoulLink 需重造 viewer 侧）大于收益。

---

## 3. 三项硬约束查证

### 3.1 console 会话无法 RDP 连接（用户主号场景）

- **Windows 会话模型**：console(session1) 是物理登录会话；RDP 入连由 termsrv 创建 rdp-tcp 会话。**用 RDP 客户端连 console 的等价操作是「接管」**：同一账号（lummy）从 RDP 连入 → Windows 把 console 会话重定向到 RDP（本地屏锁定/切换到登录界面）——不是「并行观看」【行业共识 + 微软单会话原则；`SESSION-ARCH-20261122.md` 的会话模型实测旁证】。
- **例外**：`mstsc /shadow:1` 是**唯一**不接管的观看方式（attach 镜像语义），但需要权限+同意（路径 3）。
- **对五条路径的影响**：路径 1 ✓（主程序 ActiveX 连 console 环回——但注意这同样会触发接管语义！§1.A 的两条 ESTABLISHED 连接是「主程序接管了某会话」的形态：13364 连 127.0.0.17 端口看 A2 会话画面是 shadow 还是接管，需按 `RdpHostProbe` 的 Remember 语义个案确认【待验证】）；路径 2 ❌；路径 3 ✓；路径 4 ✓/△；路径 5 ✓/△。

### 3.2 自连接 3389 是否踢人

- **同账号 + 已有会话**：接管（take over）——原客户端被断开，会话重定向到新客户端【行业共识：Windows 单用户单会话原则】。
- **同账号 + shadow 模式**（/shadow:sessionID）：不踢，attach 镜像。
- **不同账号**：新会话并行（本机 A1/A4/A2 三账号并行实证✓【本机实测 query session】）。
- **本机现状**：13392/13364 的环回连接**没有踢掉谁**（四个会话全部 Active）——因为 TermWrap 解锁了多会话，且这些连接的落点语义由 `RdpHostProbe` 实测决定【代码事实 `RdpHostProbe.cs:10-18`】。
- **结论**：自连不必然踢人，但**同账号非 shadow 自连必接管**——「拿画面」路径必须走 shadow 或不同账号，否则破坏正在挂机的会话。

### 3.3 第三方叠加 RDP 服务端是否与 termsrv 冲突

- **端口层面不冲突**：第三方端点监听独立端口（如 3390）即可，TCP 层并存无碍。
- **会话/图形层面无共享**：termsrv 的图形栈（DWM 重定向 surface、bitmap delta、AVC444）是内核/服务私有通道，第三方端点**拿不到**——FreeRDP Win shadow 的现实是退回 DDA 抓屏（`win_dxgi.c`）或 WDS API（`win_wds.c`），IronRDP server 同理（server skeleton 需要自备采集）。**「叠加服务端」不会破坏 termsrv，但也拿不到 termsrv 的画面**——两者是平行的两套采集，不是上下游。
- **本机特有**：TermWrap 已替换 ServiceDll（§1.B）——它 hook termsrv 内部；第三方端点不经过 TermWrap，行为不受其影响也不受其益。

---

## 4. 路径对比总表

| # | 路径 | 可行性 | 额外延迟（vs 现状 46~63ms） | 工作量 | 风险 | console 会话 | 影响现有 RDP 连接 |
|---|---|---|---|---|---|---|---|
| 1 | ActiveX 截取二次中继 | ✅ 技术可行 | **+15~35ms**（总 ~60~98ms，劣化） | 小（组件全在） | 双重压缩画质损失 | ✓（连 console 环回，但注意接管语义） | 新增环回连接（shadow 语义则不踢） |
| 2 | agent 当 RDP 服务端（FreeRDP shadow/IronRDP server） | ❌ **机制级不可行** | —（无画面源） | — | 画面所有权/会话语义/console 覆盖三重矛盾 | ❌ 无法覆盖 | 自成端点但不共享 termsrv 图形 |
| 3 | mstsc /shadow 转推 | △ 可行但=路径1变体 | 同路径 1（+RDP 传输后二次中继） | 小~中 | 权限/同意策略配置；网页转推仍二次压缩 | ✓（shadow attach 语义） | 不踢（attach）；需策略授权 |
| 4 | HTML5 RDP viewer（Guacamole/IronRDP-Web） | △ 长期可行 | **−30~−50ms**（省服务端采集/编码段；RDP 20~60ms+浏览器照付）→ 总 ≈ RDP 客户端体感 | **大**（1~3 月，换 viewer 底座） | 丢现有 WebCodecs 资产；Guacamole 结构税需复验（1000ms 轮询实测在案） | ✓/△（连 lummy 需接管或 shadow） | 新增客户端连接 |
| 5 | Windows Desktop Sharing API（RDPRAPI） | △ 理论可行 | 同路径 4 量级（原生 RDP 图形源） | **大**（WDS 之上重建 viewer+多 viewer+码率控制） | API 老化半废弃；无社区参照；与网页消费需 RDP 客户端中转 | ✓/△（SYSTEM 服务模式可达 console） | 不冲突（自成端点） |

---

## 5. 明确回答：直接拿 RDP 画面能否比现状 46~63ms 更快？

**能，但只有一条路值得走，且不是「叠加采集」而是「换画面源」：**

1. **「拿 RDP 画面」本身（RDP 客户端视角）= 20~60ms**（`AUDIT-08`）——低于现状 46~63ms 的**服务端采集段**，因为 RDP 不做 CPU 回读、不做跨 GPU 转换、不堆 viewer 队列。这就是 RDP 体感跟手的根。
2. **但任何「拿 RDP 画面 → 再转推网页」的二次中继结构（路径 1/3）都会把延迟叠回 60~98ms**——RDP 传输 20~60ms + 截屏 5~31ms + 编码 7ms + 推流 1ms，比现状更慢。**二次中继不省延迟。**
3. **唯一真正省延迟的形态 = viewer 直接说 RDP（路径 4/5）**：总延迟 ≈ RDP 传输 20~60ms + 浏览器渲染 20~40ms ≈ **40~100ms，典型 ~60ms** vs 现状 SoulLink 绝对端到端 ~253~293ms（`AUDIT-08` 差分口径 233ms+RDP 自身）——**潜在降幅 3~4 倍**。代价是换 viewer 底座（1~3 个月）。
4. **现状 46~63ms 里真正可压的是采集节奏**（direct-probe 实测服务端只出 ~16fps、capture 含等帧 8~89ms 波动）——这属于现有 DDA 管线优化（P0 已排），**不需要动 RDP 架构**就能把 46~63 压向 ~25~35ms（若出帧提到 30fps+），叠加浏览器段优化后绝对端到端可到 ~120~150ms。这是「不动架构」的保守收益。

---

## 6. 推荐结论

**短期（本周）**：不动 RDP 架构。P0 = 修服务端出帧节奏（16fps→30fps+，direct-probe 已定位 capture 节拍问题），收益直接进 46~63ms 的主项。
**中期（若用户要「RDP 级体感」）**：评估 **路径 4（HTML5 RDP viewer，首选 IronRDP-Web 或自研 WASM 客户端连 shadow 流）**——这是唯一「原生画面源 + 网页直达」的结构；先做 2 周 spike 验证 IronRDP WASM 连本机 3389（shadow 模式）的真实延迟与帧率，再决定是否换底座。
**否决**：路径 1/2/3（二次中继劣化、机制不可行、变体无增益）。
**路径 5（WDS API）**：留作 spike 备选（画面源同路径 4），API 老化风险高，不建议首选。

**替代方案（若不接受换底座）**：维持现有 WebCodecs 管线，集中火力：①出帧节奏 16→30fps（P0）；②浏览器段 rVFC 埋点 + SKIP_QUEUE 调优（P1）——绝对端到端预期 ~120~150ms，虽达不到 RDP 体感但成本最低。

---

## 7. 证据来源清单

| 来源 | 位置 | 用途 |
|---|---|---|
| `Controls/RdpConsole.cs:19, 24, 38` | 源码 | 主程序 RDP=纯客户端 ActiveX |
| `Services/AutoStart.cs` | 源码 | agent 经计划任务启动，与 RDP 无关 |
| `Services/RdpWrapService.cs:14-72, 56-59` | 源码 | TermWrap 安装/ServiceDll 语义 |
| 本机注册表 `TermService\Parameters\ServiceDll` | 本机实测 | TermWrap 已生效（`C:\Program Files\RDP Wrap\TermWrap.dll`） |
| 本机 svchost 模块表 | 本机实测 | termwrap.dll + termsrv.dll 同载 |
| `Services/RdpHostProbe.cs:10-18, 26-37` | 源码 | 环回地址实测逻辑、127.0.0.N 会话落点 |
| netstat 3389 ESTABLISHED（13392/13364） | 本机实测 | 环回 RDP 在用实证 |
| MS Learn mstsc 命令页 | learn.microsoft.com/windows-server/administration/windows-commands/mstsc（HTTP 200） | /shadow 参数存在 |
| FreeRDP `server/shadow/Win/`（win_dxgi.c / win_shadow.c / win_wds.c） | GitHub API 抓取源码 | shadow 的真实捕获源=DDA+WDS，非 termsrv 图形栈 |
| IronRDP README | GitHub API | Rust RDP 实现、WASM/.NET 目标、server skeleton |
| 本机 COM `Rdpcomapi.RDPSession` | 本机实测 | WDS API 注册存在 |
| 本机 `query session` / netstat 3389 | 本机实测 | 4 会话并行 + 环回连接 |
| `AUDIT-20261124-08-RDP-BASELINE-VS-BROWSER.md` | 仓库 | RDP 自身 20~60ms、233ms 差分口径 |
| `TOOL-20261124-DIRECT-PROBE.md` §3.1 | 仓库 | 现状服务端段 46~63ms、16fps 出帧 |
| `LOW-LATENCY-DEEP-DIVE-20261124.md` | 仓库 | Guacamole 1000ms 轮询结构税 |
| `SESSION-ARCH-20261122.md` | 仓库 | 会话模型、四路线评估 |
| `RDP-WINDOWING-RESEARCH-SUMMARY-20261117.md` / `RDP-SPLIT-INCIDENT-20261117.md` | 仓库 | ActiveX 重挂事故史（0x119 内核 bugcheck）——路径 1 的工程风险背景 |
| `STREAM-PLAN-V2-20261122.md` §1.1 | 仓库 | BitBlt 27.2ms 实测 |

---

## 8. 待验证清单（不猜）

| 项 | 状态 |
|---|---|
| 主程序 13364 连 127.0.0.17 看的是 shadow 还是接管语义 | 待验证（需 `RdpHostProbe.Remember` 缓存或抓 termsrv 会话事件） |
| /shadow 在本机（TermWrap 修补后的 termsrv）上的实际行为 | 待验证（TermWrap hook 后 shadow 路径是否完好无一手证据） |
| IronRDP WASM 连本机 3389 的真实延迟/帧率 | 待验证（建议 2 周 spike） |
| WDS API 在 Win11 29698 上的共享质量与延迟 | 待验证 |
| RDP shadow 的组策略（Shadow=1/2）配置后是否免 consent | 本机策略键未配置，行为待测 |

---

## 9. 边界遵守自查

只读评估：未改产品代码/配置/注册表；未碰 `dist\stable\SoulLink-Stable.exe`（PID 13392，仅 netstat 观察其连接）；未使用 A1/A4 账号；未启动/断开任何 RDP 连接（3389 仅只读 netstat/注册表/服务状态查询）。唯一写入 = 本文档。
