# 对标研究 Yomori 之四：二次审计（增量结论与解冻建议）

> 日期：2026-11-24
> 作者：yomori-scout（dsh-team `soullink-p0-fix-yomori` / member `yomori-scout` / task `t4` / attempt `b3b1f86d-f129-43d4-8fd5-aabb5151609f`）
> 对象仓库：**<https://github.com/aitnr/yomori>**（public，**GPL-3.0**，`main`，fork of `yukikino001-ship-it/Anigame-script-manager`）
> 性质：**只读研究**。未改本仓库任何产品代码/脚本/配置/版本号；未碰 `dist\stable\SoulLink-Stable.exe`；未使用 A1/A4；未停启任何进程；未发布、未碰 COS/latest.json；未执行 git 写操作。临时目录只用 `%TEMP%\yomori-probe`。唯一写入 = 本文档 + `docs/research/README.md` 一行索引。
> **GPL-3.0 红线**：全文只做**机制描述 + 路径指认**，**未复制任何一行 Yomori 代码**（含 `.c/.h/.cs/.iss/.yml`）。文中出现的 Yomori 代码片段仅限**常量/属性名/构建配置项**这类接口事实，用于说明机制，不参与本仓库编译。详见 §7。

---

## 0. 一页结论（先给能动手的）

### 0.1 本次最重要的三条

**① 【推翻团队共识】P0-1 与 P0-2 是同一个根因，不是两个 Bug。**
我们有**两条互斥的版本判据**，在 3.3.39 的产物上**同时且必然**给出相反答案：

| 判据 | 我们读什么 | 3.3.39 实测 | 结果 |
|---|---|---|---|
| `Services/BrokerInstall.cs:68-97` `ReadAgentVersion()` | 托管元数据 `AssemblyMetadata("AgentVersion")` | 两个 exe **`PEReader.HasMetadata = False`** → 均返回 `null` | `ValidAgent` 恒 `false` → **投送恒 AGENT_INVALID** |
| `Services/AgentSync.cs:91-102,121-150` `Inspect()` | `FileVersionInfo.GetVersionInfo().FileVersion` | 两个 exe **均读出 `3.3.39.0`**（单文件发布**保留** Win32 版本资源） | 相等 → **恒 `EngineState.Ready`** |

⇒ **顶栏显示绿色「引擎就绪」的同一次运行里，Broker 每一次投送都回 `AGENT_INVALID`。** `MainWindow.xaml:128` 已经被改成「检测中…」了，但 `AgentSync.Inspect` 自己就会算出 `Ready` 并把「引擎就绪」写回去（`Views/MainWindow.xaml.cs:770-785`）。
**所以只修 XAML 默认值 = 修不好 P0-2**（HANDOFF §1.2 的四处根因里，第 1 条已经不是真因了）。

> ⚠️ **先读这句**：§0.1① 的分析在交付前**已被 core-fixer 采纳并落地**（判据已归一 + 增加随包 version 标记 + 部署时随 agent 复制）。
> **但修复尚未到产物**：`dist\SoulLink.Agent.version` 与 `C:\Program Files\SoulLinkDev\SoulLink.Agent.version` **实测都还不存在**。
> **务必先看 §8.5**（已落地清单 + 6 个仍未解决的缺口 G1~G6），否则会误以为 P0 已经修好。

**② 我们比「0 测试」强得多，但比 Yomori 弱在「没有闸门」。这话要改。**
BENCH-YOMORI-03 写「0 个测试项目」——**不准确**。我们有 **`tools/layout-check/` 下 90 个 `.csproj` 断言程序**（自带 `Check()` + 失败计数 + 退出码，如 `tools/layout-check/BrokerAuthCheck.cs:8-13,59`）。**真正缺的是三样**：① 没有**统一的 runner**（全仓 0 处脚本引用 layout-check，`build.bat` 也不跑）；② 不在**构建闸门**上（`SoulLink.csproj` 的 `AgentDepRedline` 是唯一挂进构建的检查）；③ 无 CI。
⇒ 「建 xUnit 起步」这条（冻结项 #7）**方向错了**：我们不需要 xUnit，我们需要把已有的 90 个断言**串起来 + 挂进构建**。成本从 1~2 人日降到 **0.5 人日**。

**③ Yomori 上游已冻结 6 天，「上次审计之后新增」的东西为零 —— 真正的增量在「上次漏掉的文件」。**
`aitnr/yomori` 最后提交 **2026-11-18**（`089d06c7`），本文抓取时（11-24）**无新提交**。它本身是 fork，上游 `yukikino001-ship-it/Anigame-script-manager` 最后推送 **2026-06-01**。
⇒ 「找新增」这条路是空的。**本次全部价值来自「上次三份报告没读的文件」**，其中最值钱的是三个**纯函数 + 可单测**的模型文件：`RdpClientStateMachine.cs`、`RdpErrorText.cs`、`AgentStartGate.cs` —— 这三份直接对应我们 **P0-1 / P0-3** 的修法。

### 0.2 按性价比排序的前 5 项（验收要求）

| # | 项 | 人日 | 解决什么 | 为何排这个位次 |
|---|---|---|---|---|
| **1** | **版本判据归一：`ValidAgent` 与 `AgentSync.Inspect` 合并成同一个纯函数，`ReadAgentVersion` 加「元数据 → FileVersion」回退链** | **0.5** | **P0-1 + P0-2 同时根治**（见 §2.1） | 唯一一条「改 0.5 天、同时消掉两个 P0、且不引入新文件」的改动。**不做这条，t1/t2 会各自修一半，症状换个样子回来** |
| **2** | **错误码 → 人话 的映射表 + 可恢复/不可恢复分类（`RdpErrorText` 模型）** | **0.5** | **P0-3**（不可恢复错误无脑重试） | 我们**已有 19 个 Broker 错误码**（§3.2），但**没有一个**被判「可恢复/不可恢复」。Yomori 用 `RdpErrorText` 把 30+ 个原因码逐个翻成人话并给**可执行指引**。纯查表，零风险 |
| **3** | **把 90 个 layout-check 断言串成 runner 并挂进 Release 构建** | **0.5** | 「成员说 build 0 error 不可信」（HANDOFF §F 的流程事故） | 资产**已有**，只缺 15 行 runner + 1 个 MSBuild Target。**这是唯一能防住本轮 P0 复发的机制** |
| **4** | **「判据分层」纪律 + 部署前「活跃任务不覆盖」守卫** | **1.0** | 升级打断挂机；两套口径打架 | Yomori 把它写成硬纪律（`AgentAutoDeploy.cs:141-146`：「两套口径迟早会一个说要更新一个说不用，那种矛盾最难查」）。**第 1 项就是踩了这条** |
| **5** | **安装/部署的「先判红再发」断言集（README 里那套 `throw`）** | **0.5** | 打包自检缺失 | Yomori `build.yml` 有 **34 处 `throw`**（§3.4）；我们 `build.bat` **0 处结构断言**。纯脚本，不碰产品代码 |

**合计 ≈ 3 人日**，其中 **3 条直接服务本轮三个 P0**。

### 0.3 与前 3 份报告的关系（不重复声明）

本文**不重复** BENCH-YOMORI-01（RDP 内嵌/画面链路）、02（agent 架构/看门狗）、03（运维/CI/测试）的既有结论。
本文的**新增部分**：§2（P0 根因的机制级修法）、§3（上次漏掉的文件：状态机/错误码表/启动闸门/判据分层/构建断言）、§4（冻结 13 项的解冻裁决）、§5（新候选）、§6（性价比与排期）、§7（GPL 红线复核）。

---

## 1. 取证方法与「新增变化」的核实结论

### 1.1 本次取证方式（与上次不同，可复现）

上次三份报告受限于 `raw.githubusercontent.com` 被阻断，只能走 Contents API 逐文件抓。本次打通了一条更省配额的路：

| 方式 | 结果 | 用途 |
|---|---|---|
| `codeload.github.com/AITNR/Yomori/tar.gz/refs/heads/main` + `tar.exe` 解到 `%TEMP%\yomori-probe\src` | **成功**，18.9 MB，304 个文件（仅 `docs/images/主界面.png` 因中文路径报 1 处 tar 警告，不影响源码） | **全文可 grep**，这是本次能发现「上次漏掉的东西」的关键 |
| `api.github.com/repos/aitnr/yomori/git/trees/main?recursive=1` | 343 条，`truncated:false` | 目录树核对 |
| `api.github.com` 常规接口 | **60 次配额在 3 次调用后耗尽**（`rate_limit` → `remaining:0`） | 说明为何不能靠 API 逐文件抓 |
| `raw.githubusercontent.com` / `ghfast.top` / `gh-proxy.com` / `raw.gitmirror.com` / `cdn.jsdelivr.net` | **全部 `fetch failed` / `Recv failure: Connection was reset`** | 镜像不可用 |
| `github.com/AITNR/Yomori/blob/...` HTML | 可抓但被截断（`truncated:true`，100 KB 上限） | 不可用于读源码 |

### 1.2 「上次审计之后新增」= 空（必须明说，避免后人重复劳动）

| 事实 | 证据 |
|---|---|
| 仓库最后提交 | **2026-11-18 08:00:40Z** `089d06c7`「feat(channels): 目标账户改为只许下拉选择」（`/commits?per_page=100` 首条） |
| 上次三份 BENCH 报告日期 | 2026-11-24（同日，晚于最后提交 6 天） |
| ⇒ 结论 | **上次审计之后仓库一个字节都没变**。所谓「新增/变化」不存在 |
| 仓库是 fork | `fork: true`；上游 `yukikino001-ship-it/Anigame-script-manager`（54 stars，`pushed_at` **2026-06-01**，更早） |
| 本仓自身 | 19 stars，`created 2026-09-25`，`pushed 2026-11-18`，52.6 MB |

⇒ **本次研究的定位从「找增量」改成「补漏 + 裁决解冻」**。下文 §3 全部是「上次三份报告读过但没提炼」或「根本没读过」的文件。

### 1.3 本次真正新读的文件（前次报告未涉及）

前次报告引用过的对方文件（`ycn_rdp.h`、`ycn_dirty_*.c`、`RdpD3DRenderer.cs`、`RdpDirtyPathStats.cs`、`RdpSessionService.cs` 部署段、`RdpBridge.cs`、各 `.patch`）**本文不再展开**。本文新增读取并提炼的是：

| 文件 | 字节 | 为何值钱 |
|---|---|---|
| `winui3/src/Yomori.App/Models/RdpClientStateMachine.cs` | ~3 KB | **纯函数状态机 + 显式非法迁移**，直接对标我们 P0-3 的「状态混乱」 |
| `winui3/src/Yomori.App/Models/RdpErrorText.cs` | ~5 KB | **30+ 原因码 → 人话 + 可执行指引**，直接对标 P0-3 |
| `winui3/src/Yomori.App/Services/AgentStartGate.cs` | ~5.5 KB | **「缺哪样补哪样」的双判据闸门**，直接对标 P0-2 的「5 态」设计 |
| `winui3/src/Yomori.App/Services/AgentPayloadIdentity.cs` | ~2.9 KB | 内容哈希判据（**上次 02 报告提过，但漏了「失败必须判不等」这条**） |
| `winui3/src/Yomori.App/Services/RdpSessionService.cs:1631-1692` | — | **明确写「读文件版本资源而不是程序集元数据」及理由** ← **这是 §2.1 的关键依据** |
| `winui3/src/Yomori.Agent/Yomori.Agent.csproj:50-52` | — | `<AgentVersion>` 直接喂 `<FileVersion>` ← **证明「退回 FileVersion」≠「退回主程序版本绑定」** |
| `.github/workflows/build.yml` | 28.7 KB | **34 处 `throw` 构建断言**（上次 03 报告只提了 ci.yml 的归档，漏了 build.yml 的断言集） |
| `.github/workflows/ci.yml` | 11 KB | 上次提过「每次构建归档独立 tag」，本文补「**不取消**并发」的取舍理由 |
| `winui3/tests/Yomori.LogChecks/Yomori.LogChecks.csproj` | 2.9 KB | **测试工程用 `<Compile Include="..\..\src\...">` 链接生产源码**，不复制 ← 这是我们的 runner 该抄的形态 |
| `winui3/tools/installer.iss:249-290` | — | 安装器侧的版本比对（**ISPP `#if` 对字符串比较不可靠**的踩坑） |
| `winui3/docs/rdp-hang-and-heap-corruption-2026-10-16.md` | 14.7 KB | **「锁内绝不 I/O、热路径绝不 I/O」**纪律的完整事故链 |
| `winui3/src/Yomori.App/Services/WorkingSetTrimScheduler.cs` + `WorkingSetTrimmer.cs` | ~9 KB | 「连接后延迟 10 秒回收工作集」的幂等调度器（§5.3 候选） |
| `winui3/docs/known-limits-and-roadmap.md` | 4 KB | **它自己承认的 12 条限制**（用于判断哪些机制它其实没做扎实） |
| `winui3/docs/rdp-avc444-aux-drop-plan-2026-11-14.md` | 34.7 KB | 上次引用过结论；本次补 `op=2` 的协议事实 |

---

## 2. 直接服务本轮三个 P0 的机制（最高价值部分）

### 2.1 【P0-1 + P0-2 同一根因】版本判据必须唯一，且必须用「读得到」的载体

#### 2.1.1 我们的实测事实（可复现）

| 实测项 | 命令/方法 | 结果 |
|---|---|---|
| 两个 exe 是否同一份 | `Get-FileHash -Algorithm SHA256` | **完全相同** `5D69C86DAA5AA8146D2D1923428FE4D8C6DFF43709C8D727C2B63ECBDB373332`（各 4,453,727 字节） |
| 是否单文件 bundle | 搜 `.NET bundle header signature`（32 字节魔数） | 命中 offset **68848**；`SoulLink.deps.json` 条目在文件尾（4453709）⇒ **确认是单文件 bundle** |
| 托管元数据 | `PEReader.HasMetadata` | **False**（两者）⇒ `ReadAgentVersion()` **必然返回 null** |
| Win32 版本资源 | `FileVersionInfo.GetVersionInfo().FileVersion` | **`3.3.39.0`**（两者）⇒ `AgentSync.Inspect` **必然判等** |

⇒ 两条判据的**输入互斥**：一条只能读到 `null`，另一条只能读到 `3.3.39.0`。**它们从设计上就不可能一致。**

#### 2.1.2 我们两侧的代码位置

| 侧 | 文件:行 | 行为 |
|---|---|---|
| 判据 A（投送闸门） | `Services/BrokerInstall.cs:40-44` | `sv/av` 都从 `ReadAgentVersion` 取；`av` 为 null → `return false` |
| 判据 A 实现 | `Services/BrokerInstall.cs:68-97` | `PEReader` + `GetMetadataReader` 扫 `AssemblyMetadataAttribute` |
| 判据 A 消费点 | `Services/BrokerService.cs:196-198` | `ValidAgent(exe, agent)` 假 → `Fail("AGENT_INVALID", ...)` |
| 判据 A 消费点 | `Services/RelayClient.cs:188`、`Services/SystemLauncher.cs:460` | 同判据 |
| 判据 B（界面） | `Services/AgentSync.cs:91-95` | `VersionOf` = `FileVersionInfo...FileVersion` |
| 判据 B 决策 | `Services/AgentSync.cs:138-147` | `File.Exists` + `VersionEquals(selfVer, agentVer)` → `Ready` |
| 判据 B 渲染 | `Views/MainWindow.xaml.cs:770-785` | `EngineState.Ready` → 绿点 + 「引擎就绪」 |

#### 2.1.3 Yomori 的选择（为什么它不是「退回绑定」）

Yomori **明确弃用托管元数据、改用文件版本资源**，理由原文（`winui3/src/Yomori.App/Services/RdpSessionService.cs:1669-1692`，我用自己的话转述，不复制）：

> 读的应当是**文件版本资源**而不是程序集元数据：安装器（Inno 的 `GetVersionNumbersString`）与主控端必须按同一口径判断，而**安装器根本读不到托管程序集属性**。两处数值来自 csproj 同一个属性，不会漂移。

**而解耦并没有因此丢失** —— 见 `winui3/src/Yomori.Agent/Yomori.Agent.csproj:50-52`：

```
<AgentVersion>2.1.5</AgentVersion>
<FileVersion>$(AgentVersion)</FileVersion>
<AssemblyVersion>$(AgentVersion).0</AssemblyVersion>
```

即：**agent 的 `FileVersion` 就是独立的 `AgentVersion`，与主程序 `<Version>` 无关。**
⇒ HANDOFF §1.1 里「**不可退回 FileVersion 强绑定**」这条**需要修正**：**可以退回 `FileVersion`，只要那个 `FileVersion` 由 `<AgentVersion>` 喂**。

#### 2.1.4 为什么这条比「随包分发 `SoulLink.Agent.version` 文本文件」更好

| 维度 | 元数据 | **FileVersion（Yomori 路线）** | .version 文本文件 |
|---|---|---|---|
| 单文件发布下可读 | ✗（实测 False） | **✓（实测 3.3.39.0）** | ✓ |
| 是否需要新文件 | 否 | **否** | **是（+1 个必须与 exe 同步的文件）** |
| 安装器能否同口径 | ✗ | **✓（Inno `GetVersionNumbersString`）** | ✓（但要多写一段读文件逻辑） |
| 更新器/打包是否要改 | — | **否（随 exe 走）** | **是（漏打包 = 静默降级成「每次都重铺」或「永远不更新」）** |
| 漂移风险 | 低 | **低（单一 csproj 属性）** | **中（两处产物，无校验就不同步）** |
| 是否丢解耦 | — | **不丢（FileVersion 由 AgentVersion 喂）** | 不丢 |

Yomori 自己的 csproj 注释就记了这个坑的反面（`Yomori.Agent.csproj:47-49`，转述）：**不能用 `$(Version)` 代替 `AgentVersion`**，因为 CI 传的是 `2.4.1-dev.184` 这种带预发布后缀的版本，`FileVersion` 只保留数字段 `2.4.1.0`，两个不同的 dev 构建会拿到同一个号。

⇒ **给 t1 的落地建议（三选一，按推荐度）**：

1. **【推荐，0.5 人日】回退链**：`ReadAgentVersion` 改成「先读 `AssemblyMetadata` → 读不到再读 `FileVersion` → 都读不到才判『需替换』」。当前产物两边都读到 `3.3.39.0` → **立刻恢复投送**，且**不新增文件、不改打包**。
2. **【中期，需先拆 agent 工程】csproj 给 agent 产物单独喂 `<FileVersion>$(AgentVersion).0</FileVersion>`**，然后**彻底删掉** `ReadAgentVersion` 的元数据分支，只留 `FileVersion`。这样与安装器/更新器天然同口径。前提是 agent 不再是主程序的字节副本（当前 `build.bat:94` 是 `copy /y`，见 HANDOFF §4.1）。
3. **【不推荐】`.version` 文本文件**：多一个必须同步的产物，且**没有校验就是静默失败**——正是本轮 P0 的同类病。

> ⚠️ **不管选哪条，`AgentSync.Inspect` 必须改成调用与 `ValidAgent` 同一个函数**。否则「引擎就绪」依旧是另一套算法算出来的，早晚再骗一次。见 §2.2。

### 2.2 【P0-2】「5 态」不是重点，「判据唯一 + 缺哪样补哪样」才是

我们现在有 5 态枚举（`Services/AgentSync.cs:11-23`：`Ready/NotInstalled/NotRunning/AgentNeedsUpdate/Error`），方向对了。**但它用的是判据 B，而投送用的是判据 A**——这就是 §2.1 的矛盾。

Yomori 的对应设计是 `winui3/src/Yomori.App/Services/AgentStartGate.cs`（139 行，**纯函数、零 WinUI 依赖、零 I/O 写**），机制我用自己的话说：

1. **两个正交判据**：`PayloadPresent`（副本在不在，扫 `agent`/`agent_b` 两槽）+ `AutoStartEnabled`（登录自启项在不在）。**不压成一个布尔**。
   - 它明确写了为什么要拆（`AgentStartGate.cs:10-13`，转述）：原来那个判据「只查公共启动目录的快捷方式在不在，等于把『副本已部署』和『登录自启已开启』压成了同一件事」。
2. **`ChooseAction()` 只回答「做什么」**：`Ready → None`；`PayloadPresent → EnableAutoStart`（轻量，只补自启）；否则 `Deploy`（重，整轮部署）。
   - 关键语义（`AgentStartGate.cs:92-95`，转述）：**缺副本时一步到位走部署（部署本身会连自启一起建好）；只缺自启时走轻量的「开启自启」，不重铺 73MB 副本。**
3. **文案与判据分离且都是纯函数**（`Title/PrimaryText/Body`），冒烟可按文字锚点断言。
4. **异常一律降级为「这个槽没有」**（`AgentStartGate.cs:66-69`），**绝不因为闸门把「开始执行」掀掉**（`AgentStartGate.cs:40`，转述）。

**对我们的意义**：我们的 `RepairCore`（`Services/AgentSync.cs:156-206`）已经是「先判等 → 不等才复制 → 复制后复校验」的好结构，**缺的是**：
- ⓐ 判据与 `ValidAgent` 不统一（§2.1）；
- ⓑ **只区分了「文件在不在」和「版本对不对」，没有把「自启/服务」拆成独立可补项**——用户遇到「agent 版本对但服务没运行」时，仍然要先看点的是哪个按钮；
- ⓒ `Inspect` 与 `RepairAsync` 各自算一遍版本，**代码里有两处 `VersionEquals` 调用点**（`AgentSync.cs:97-102` 与 `:200-203`），未来必然漂移。

⇒ 落地：**把「判据 + 决策」抽成一个纯函数静态类（照抄机制，不抄代码）**，`Inspect`/`ValidAgent`/`RepairCore` 三处都调它。**0.5 人日**，且能被 layout-check 单测（我们有现成脚手架，见 §3.3）。

### 2.3 【P0-3】不可恢复错误：Yomori 用「错误码 → 人话 + 可恢复性」双层表

#### 2.3.1 我们的现状（比预想的更差）

| 事实 | 证据 |
|---|---|
| 我们有 **19 个 Broker 错误码** | `AGENT_INVALID/BAD_RESPONSE/BROKER_CLOSED/COMMAND/CREATE_AGENT/DEPLOYMENT/INSTALL_INVALID/NOT_INSTALLED/NOT_RUNNING/OFFLINE/SERVICE_OUTDATED/SESSION/STOPPED/STOP_DENIED/TIMEOUT/UNAVAILABLE/VERIFY_AGENT/VERSION/AGENT_STARTED`（全仓 grep 归类） |
| **没有一个码被判「可恢复/不可恢复」** | 全仓无 `IsRecoverable`/`Retryable`/码表 |
| 重试循环只有一个总闸 | `Controls/RdpConsole.cs:795-856`：`_rebuildAttempt` ≤ 5，退避 2/4/8/16/30 s（`:820`）。**不区分 `AGENT_INVALID` 还是网络抖动** |
| 失败后给出的指引 | 只有一句固定文案 `"注入通道重建失败，请点「重新连接」"`（`RdpConsole.cs:814`） |
| 断开原因码 | 只**记日志**，从不映射成人话：`RdpConsole.cs:2388-2401` 把 `ExtendedDisconnectReason` 写进 `Debug()` |

#### 2.3.2 Yomori 的做法（`RdpErrorText.cs`，纯查表）

它把三类错误码各写了一张纯函数表（我按语义复述结构，不复制字符串）：

| 函数 | 输入域 | 粒度 |
|---|---|---|
| `DisconnectReason(int)` | 30+ 个断开原因码（含 `0/1/2/3/4`、`260/262/263/264`、`277/282`、`1288/1801`、`2308/2310/2312`、`2610`、`3334~3343`、`4615/4616/5639/6655/6919/7437`） | **每条都给「下一步做什么」**，不是只给现象 |
| `FatalError(int)` | 0~22 | 同上 |
| `LogonError(int)` | 负数码 -2~-7 | 同上 |
| 未知码 | 兜底 | `"连接已断开（原因码 N）。"` —— **永远有话说，绝不空白** |

**关键在于「文案里带动作」**。举三个它写得最典型的（转述其**语义**）：
- `282`：安全层协商失败 → **「通常是凭据不对，或目标没开 NLA」**（指向两个可检查项）；
- `3335`：不允许当前用户登录 → **「确认该账户有远程登录权限、原组成员关系未被移除、系统已开启远程桌面」**（三项清单）；
- `3338`：密码已过期 → **「请先在 Windows 里改一次密码并重新保存凭据」**（明确动作）。

#### 2.3.3 对我们的意义 + 落地

**这套东西的机制是「错误码 → (人话, 可执行动作, 可恢复性)」三元组**，是纯查表，**没有任何 GPL 风险**（错误码是微软公开文档的接口事实）。

落地建议（**0.5 人日**）：
1. 新建 `Services/BrokerErrorText.cs`（或扩 `BrokerProtocol`）：把 19 个码映射成 `(title, action, recoverable)`；
2. **分类口径**（这是 P0-3 的核心）：
   - **不可恢复**（停止自动重试，只给指引 + 保留手动入口）：`AGENT_INVALID`、`INSTALL_INVALID`、`SERVICE_OUTDATED`、`DEPLOYMENT`、`NOT_INSTALLED`；
   - **可恢复**（继续退避重试）：`NOT_RUNNING`、`TIMEOUT`、`BROKER_CLOSED`、`UNAVAILABLE`、`OFFLINE`（会话可能稍后上线）；
   - **需人工判断**：`VERIFY_AGENT`、`CREATE_AGENT`、`STOP_DENIED`。
3. `ScheduleInputRebuild`（`RdpConsole.cs:803`）入口加一行：`if (!BrokerErrorText.IsRecoverable(code)) ` → 停止退避，改走「指引 + 保留恢复入口」；
4. **保留 HANDOFF §1.3 的全部要求**（30~60 s 周期重查、修好自动恢复、`_rebuildAttempt` 归零）。

> **Yomori 还有一条更硬的纪律值得抄**（`RdpClientStateMachine.cs`）：**非法状态迁移显式保持原状态并返回 `false`**（`Transition` 用 `switch` 穷举合法边，`_ => state`；并额外提供 `IsValid(from, evt)` 给单测）。这比我们「散落的 `if (IsConnected) return;`」强——**迁移表本身就是文档 + 单测目标**。0.25 人日可加在我们 `RdpConsole` 的连接状态上（当前状态散在 `_hasConnected`、`IsConnected`、`_rebuildAttempt` 三处）。

---

## 3. 上次漏掉的东西（本次真正的增量）

### 3.1 判据分层纪律（Yomori 写成硬注释，我们踩了同一个坑）

`winui3/src/Yomori.App/Services/AgentAutoDeploy.cs:141-146`（转述）：

> 「只用于日志里说清『从哪个版本换到哪个版本』，**不参与判定** —— 判定统一走 `IsAgentPayloadCurrent`，**两套口径迟早会一个说『要更新』一个说『不用』，那种矛盾最难查**。」

它把判据分了三层且**各管一事**：
| 层 | 管什么 |
|---|---|
| `AgentStartGate` | 副本在不在 + 自启在不在（拆两个独立判据，缺哪样做哪样） |
| `AgentPayloadIdentity` | 逐字节一致才复用槽位（`<主名>.dll/.deps.json/.runtimeconfig.json` + 有 `.pri` 就也比，`FixedTimeEquals`；**读不出来一律判不等** —— `AgentPayloadIdentity.cs:75-79`，注释原文「Unverifiable copies must not be reported as current.」） |
| `AgentVersion` | 「要不要替换」的**唯一**判据（读不出按「需替换」，`RdpSessionService.cs:1645-1647`） |

**我们现状**：`BrokerInstall.ValidAgent`（判据 A）、`AgentSync.Inspect`（判据 B）、`AgentSync.IsAgentCurrent`（判据 B 的副本）——**三处各自算**。这正是 §2.1 的病根。
**落地**：§2.1 + §2.2 合并做，**0.5 人日**。**这条必须做，不是可选。**

### 3.2 构建期断言集：Yomori 的 `build.yml` 有 34 处 `throw`，我们 `build.bat` 有 0 处

| 项 | Yomori | SoulLink 现状 |
|---|---|---|
| 构建期硬断言 | **34 处 `throw`**（`build.yml`） | `build.bat`：**0 处**结构断言（只有 `if errorlevel` 之类的常规检查） |
| 断言覆盖的东西 | 原生运行库 ABI、解码输出、代理载荷存在性、`installer.iss` 的 `DefaultDirName`/`UsePreviousAppDir=no`/`--deploy-agent` 接线/`Excludes: agent\*`/`Check: ShouldInstallAgent`/中文语言文件存在且 >5000 字节/安装包 >2 MB/清单是 `requireAdministrator` | —— |
| 最关键的设计思想 | **「两种写法都不会报错，只能等到有人量安装耗时才发现」** → 所以把「判据是否接线」本身做成断言 | 我们**没有**这类断言 |

> ⚠️ 特别值得抄的一条（`build.yml:443-453` 的语义）：**「代理端版本号的唯一来源是 csproj 的 `<AgentVersion>`，安装器靠它判断『{app}\agent 要不要重写』。读不到就判红：判据缺失时安装器会退回『每次都写』，等于静默退回旧行为。」**
> 我们 `SoulLink.csproj` 已有 `CheckAgentVersion` Target（`:15-17`，只查非空）。**建议再加一条**：断言 `ReadAgentVersion`/`FileVersion` 的**回退链**在打出来的产物上真的能读出值——**这正是本轮 P0 的防线**。

**落地（§0.2 第 5 项，0.5 人日）**：在 `build.bat` 末尾加 3~5 条 `if not ... exit /b 1`：
1. agent 与主程序**不再是同一份哈希**时（拆工程后）断言两者 `FileVersion` 各自正确；
2. 断言 `dist\SoulLink.Agent.exe` 的版本能被读出来（`powershell -c "(Get-Item ...).VersionInfo.FileVersion"` 非空）；
3. 断言 `dist\` 里必需的 dll 齐全（`Microsoft.Windows.SDK.NET.dll` 等，且体积 > 阈值——防「外置失败被塞回单文件导致启动闪退」）；
4. 断言 `latest.json` 未被 `build.bat` 触碰（发布红线）；
5. 断言 `web-index.html` 与 `Services/WebPages.cs` 内嵌版本一致（我们已有 `scripts/sync-webpages.ps1`，加一条比对即可）。

### 3.3 测试形态：我们不需要 xUnit，需要「链接生产源码 + runner + 闸门」

#### 3.3.1 修正 BENCH-YOMORI-03 的「0 个测试项目」

**实测**：`tools/layout-check/` 下有 **90 个 `.csproj`**，每个是一个独立控制台断言程序，形态统一：

```
// tools/layout-check/BrokerAuthCheck.cs:8-13,59（我方代码）
private static int _fails;
private static void Check(bool ok, string name) {
    Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
    if (!ok) _fails++;
}
public static int Main() { ... return _fails; }
```

即：**自带断言 + 失败计数 + 退出码**，已经是标准测试形态。**17 个已构建出 exe**（说明确实跑过）。

#### 3.3.2 真正缺的三样（这才是差距）

| 缺什么 | 证据 | Yomori 对应 |
|---|---|---|
| **没有统一 runner** | 全仓 `scripts/`、`build.bat` **0 处**引用 `layout-check`（grep 实测） | `dotnet run --project ...` 逐项列在 README |
| **不在构建闸门上** | `SoulLink.csproj` 里唯一挂进构建的检查是 `AgentDepRedline`（`:36-38`） | `ci.yml` → `build.yml` 每个 job 都 `throw` 判红 |
| **无 CI** | `.github/` **不存在**（实测 ABSENT） | 4 条 workflow |

#### 3.3.3 落地（§0.2 第 3 项，0.5 人日）

1. 新建 `scripts/run-checks.ps1`：遍历 `tools/layout-check/*.csproj`，`dotnet run -c Release`，收集退出码，任一非 0 → 汇总并 `exit 1`；支持 `-Filter` 与 `-Skip`（有些检查需要真机/管理员/A1A4，必须能跳）；
2. `SoulLink.csproj` 加 `<Target Name="RunChecks" AfterTargets="AfterBuild" Condition="'$(Configuration)'=='Release' and '$(SkipChecks)'!='true'">`；
3. **形态照 Yomori 的 `Yomori.LogChecks.csproj:13-37`**：测试工程用 `<Compile Include="..\..\src\...">` **链接生产源码**（不复制），`EnableDefaultCompileItems=false`。我们现有的 `ProjectReference Include="..\..\SoulLink.csproj"`（`AgentCmdProbe.csproj`）也行，但**链接**形态更快且能测 `internal`。

> ⚠️ 别照抄 Yomori 的 `net8.0-windows`：我们是 `net11.0-windows`。
> ⚠️ 我们已有的 `ProjectReference` 形态会让每个 check 都拉起整个 WPF 主工程，**90 个全跑可能很慢**——runner 要做**并行 + 分组**，否则没人会等。

### 3.4 其它上次漏掉但值得知道的（低优先，仅记录）

| 项 | Yomori 证据 | 我们的对照 | 判定 |
|---|---|---|---|
| **「不取消」并发取舍** | `ci.yml:27-31`：`cancel-in-progress: false`，理由「取消了那一版就没有归档了」 | 无 CI | **借鉴思想**：归档语义要求不取消；对我们是「构建产物归档」的取舍，非必需 |
| **工作集延迟回收** | `WorkingSetTrimScheduler.cs`：**最后一次连接动作后静置 10 秒**才 `EmptyWorkingSet`（`SettleSeconds=10`，`:20`）；「连接时不刷，等于把刚写热的页立刻赶走」；任务执行中跳过但**仍记时间戳**（`:197-204`，防日志被刷满）；**用一次性定时而非短周期轮询**（`:154-159`，「10 秒等待若按 200ms 轮询，会往 UI 线程插 50 次队」） | 我们**无** `EmptyWorkingSet`（全仓 0 命中），只有 `SessionSlimmer` 读 `WorkingSet64` 做展示（`Services/SessionSlimmer.cs:286,364`） | **候选（§5.3）**：我们的痛点恰是「每会话 47 MB × N + 服务 61 MB」（HANDOFF §4.2），回收工作集是**零风险纯加法** |
| **ISPP `#if` 对字符串比较不可靠** | `installer.iss:265-268`：写成 `#if AgentVersion != ''` 时整段被静默丢掉，编译照样成功，「判据就这么没了，只有 ISCC 的 Hint 里能看出端倪」 | 我们无 Inno | **教训**：**「判据被静默跳过」是所有构建脚本的通病** → 必须用**运行时**判断 + 断言（呼应 §3.2） |
| **锁内绝不 I/O、热路径绝不 I/O** | `rdp-hang-and-heap-corruption-2026-10-16.md:99-107`：诊断写盘（`fopen/fprintf/fclose`）放在 `g_lock` 内、每帧级触发 → 588 MB 文件 + UI 线程抢同一把锁 → 整窗卡死；修法是**64 槽内存环形缓冲，只在崩溃路径落盘** | **我们有同族风险**：`Services/AsyncLog.cs`/`LogBus` 若在热路径同步写盘（需审计，本次未做） | **建议审计（§5.4）**：这是**唯一可能真降卡顿**的一条，且与我们 `AUDIT-20261124-07` D15/D16/D18（UI 线程同步 I/O）同源 |
| **自检位掩码的「反向对照」** | `rdp-hang-and-heap-corruption-2026-10-16.md:186-188`：「把同一组断言接到**修复前**的弱判据上 → `0x20` 失败（rc=`0x1F`）。这证明这两条断言不是空摆设」 | 我们 90 个 check 都只测「现在对不对」，**没有一个测「旧行为会被抓到」** | **借鉴**：给本轮 P0 写一条**反向对照**断言（喂一个 `HasMetadata=False` 的假 exe，断言 `ValidAgent` 不再误判） |
| **它自己承认的限制** | `known-limits-and-roadmap.md`：真机 GUI 验证未全覆盖（沙箱跑不了 GUI，只靠 `dotnet build` + 770+ 项源码级冒烟）；**「多画面每页最多 4 路」**；**「一次最多 8 条通道并行」**；**「真断线告警最迟 ~90 秒」**；**「桥的 command.json/status.json 仍是 PascalCase，与 config.json 的 snake_case 不一致」** | 我们有类似的「靠真机实测」依赖 | **别自轻**：它的 770+ 断言**同样是源码级**、同样没跑真机。我们的 layout-check 90 项 + 真机实测**并不落后**，弱的是闸门 |

---

## 4. 冻结 13 项的解冻裁决（验收要求 ③）

对 HANDOFF §6 的 13 项逐条裁决。**判据 = 「是否服务本轮三个 P0 的根治」+ 「成本是否 ≤1 人日」+ 「是否有确定的验证方式」**。

| # | 项 | 原估 | **裁决** | 理由（含证据） |
|---|---|---|---|---|
| 1 | 编译红线（agent 引入 WPF 类型时告警） | 0.5 天 | **✅ 保持已解冻/已完成** | 已落地：`SoulLink.csproj:36-38` + `scripts/agent-dep-redline.ps1`；有实证（`IMPL-20261124-AGENT-DEP-REDLINE.md`）。**不要再动** |
| 2 | 双槽 agent 热替换 | 2 天 | **❌ 继续冻结** | 依赖「真正的独立 agent 工程」（HANDOFF §4.3），而拆工程本身未做。**前置条件未满足**，先做会变成给一个 4.4 MB 的副本槽加复杂度。**且本轮 P0 与它无关** |
| 3 | 外置进程看门狗 + 假死 MiniDump | 1~2 人日 | **✅ 保持已解冻/已完成** | `tools/watchdog/` 已落地并对标了 Yomori 的三态判据（`IMPL-20261124-WATCHDOG.md`）。**本次补一条**：Yomori 的 `WorkingSetTrimScheduler` 式「一次性定时而非轮询」纪律与我们 `TickHub` 规范一致，**看门狗探测节拍应走 `TickHub.RegisterBackground`**（`Services/TickHub.cs:95`），别再新增 `DispatcherTimer`（AGENTS.md 性能约束 1） |
| 4 | 更新残留清理（pending-cleanup.json） | 0.5 人日 | **⚠️ 解冻，但降级为 P2** | 与三个 P0 无关；且**我们不是`待验证`而是`未验证现状`**——BENCH-03 自己写的是「【推断，待实测确认无兜底】」。**先花 15 分钟确认现状，再决定要不要做**。别为一个没确认的问题写代码 |
| 5 | 更新检查 6 态（补 LocalNewer 防回退） | 0.5 人日 | **❌ 继续冻结** | **我们的发布模型与它相反**：`RELEASE-RULES.md` 是「先传 exe → HEAD 校验 → 才写 latest.json」，**顺序红线本身就是为了防回退**。`LocalNewer` 是为「用户自编译/远端被回退」设计的，**我们没有自编译用户**。**这条对标是错的**，应正式作废 |
| 6 | 最小 CI + 构建归档 tag | 1~1.5 人日 | **❌ 继续冻结（但拆出 §3.2 的构建断言先做）** | 我们是**单机开发 + 手工发布**，没有多人 PR 流程，GitHub Actions 归档**对当前工作流零收益**（且发布必须走 `publish-release.ps1`，CI 不能替代）。**但 §0.2 第 5 项（build.bat 断言）要单独做**，它是 CI 里真正值钱的那 20% |
| 7 | 起步测试项目（xUnit） | 1~2 人日 | **🔁 解冻，但换形态 + 降本 → 0.5 人日** | **BENCH-03 的事实前提是错的**（见 §3.3）：我们已有 90 个断言程序，**不需要 xUnit**，需要 runner + 闸门。**这是本次最重要的裁决变更** |
| 8 | broker 投送「活跃任务不覆盖」守卫 | 0.5~1 人日 | **✅ 解冻（合并进 §0.2 第 4 项）** | 直接服务「升级不打断挂机」，机制清晰（`AgentAutoDeploy.cs:116-121`：先扫所有桥的 `status.json`，有活跃就整轮跳过，退出码 `ExitAgentBusy=2`）。**注意它的坑**（`AgentAutoDeploy.cs:166-169` 转述）：不能只看 `Phase`，还要看**心跳新鲜度**——否则注销账户后 `status.json` 永远停在 `running`，**之后所有部署都会被永久跳过** |
| 9 | 前端「等关键帧」兜底（≈15 行） | 0.25 人日 | **✅ 保持解冻（但优先级低于 P0）** | BENCH-01 §4.2B 已论证：我们有闸门、只缺兜底帧数。**纯前端、零风险**，可在 P0 修完后立刻做 |
| 10 | 按会话暂停编码 / 遮挡闸门接呈现 | 1~1.75 人日 | **❌ 继续冻结** | 收益是「省 CPU」，**不解决任何 P0**；且 BENCH-01 §4.2A 已指出需要跨仓库新增 agent 命令，**历史上改编码节拍出过「画面卡住」事故**。等 P0 收敛、且有真机回归窗口时再说 |
| 11 | 诊断按 reason 分桶 | 0.25 人日 | **⚠️ 解冻，但改成「按 §2.3 的错误码分桶」** | 原来的「丢帧分桶」收益偏低；**更高价值的是把 19 个 Broker 错误码按 `recoverable` 分桶**——这直接是 P0-3 的观测面 |
| 12 | ⭐ 重新审视帧节拍模型 | 待评估 | **❌ 继续冻结** | HANDOFF §2 已闭环：233 ms = 静止保活节拍（500 ms）的采样等待，数学期望 261.5 ms 与实测重合。**「变化驱动 vs 固定节拍」是产品语义决策（静止桌面要不要保活），不是技术缺陷**。且 §2.3 已把保活节拍调到 250 ms 可调（3.3.37）。**没有新证据前不要重开** |
| 13 | （隐含）安装/部署的构建断言 | — | **🆕 新增解冻项（本文提出，0.5 人日）** | 见 §3.2。Yomori 有 34 处 `throw`，我们 0 处。**这是本轮 P0 唯一能防止复发的静态防线** |

### 4.1 一句话总括

- **解冻 4 项**：#7（换形态）、#8、#11（换靶子）、#13（新增）；
- **保持已完成 2 项**：#1、#3；
- **继续冻结 5 项**：#2、#5、#6、#10、#12；
- **降级待确认 1 项**：#4；
- **保持解冻但低优先 1 项**：#9。

### 4.2 应正式作废的 1 项

**#5「更新检查 6 态」**：它的前提（存在自编译用户 / 远端可能被回退）在我们的发布模型下不成立，**且 `RELEASE-RULES.md` 的「先传包→HEAD 校验→写清单」顺序红线已经把「回退」这条路物理封死**。建议在下一份交接里**正式标记为「不适用，不实现」**，避免第三次被提起。

---

## 5. 新增候选（前 3 份报告没有的）

### 5.1 【新增】「反向对照」断言纪律 —— 0.1 人日

Yomori 在修 heap corruption 时做了一件很少人做的事（`rdp-hang-and-heap-corruption-2026-10-16.md:186-188`，转述）：**把新写的断言接到「修复前」的旧实现上跑一遍，确认它会红**，以此证明「断言不是空摆设」。

**我们现状**：90 个 layout-check 全部只验证「现在是对的」，**没有一个验证「旧 bug 会被抓到」**。
**落地**：给 §2.1 的修法写一条 —— 传一个 `HasMetadata=False` 的路径给 `ReadAgentVersion`，断言回退链能读到 `FileVersion`；再断言**旧实现**（只看元数据）会返回 `null`。这样这条断言以后能持续防回归。

### 5.2 【新增】「部署闸门不掀掉开始执行」原则 —— 已在 §2.2 内含

`AgentStartGate.cs:40` 的转述原则：**「只读文件系统，不写盘、不查注册表 —— 任何时候都不该因为闸门把『开始执行』掀掉」**。
**对我们的意义**：`AgentSync.RepairAsync` 在 `EnsureEngineAsync`（`MainWindow.xaml.cs:808-826`）里**启动时自动跑**，且会 `Process.Kill()` 占用 agent 的进程（`AgentSync.cs:209-235`）。**这是「闸门顺手杀了正在跑任务的 agent」的潜在风险**——与 §#8 的「活跃任务不覆盖」正好是同一件事的两个入口。**建议合并修**。

### 5.3 【新增】连接后延迟回收工作集 —— 0.5 人日

见 §3.4 表。**理由**：HANDOFF §4.2 的实测（服务 61 MB + agent 47 MB×2）说明内存是我们的实际痛点，而 `EmptyWorkingSet` 是**纯加法、零风险**（只影响常驻页，不改提交量——`WorkingSetTrimmer.cs:33` 转述）。
**必须照抄它的三条纪律**：
1. **延迟 10 秒**（`SettleSeconds`），别在连接瞬间刷；
2. **任务执行中跳过，但仍更新时间戳**（否则空闲巡检查每轮重来、日志被刷满）；
3. **用一次性定时器 + `TickHub.RegisterBackground`**，不要短周期轮询。

### 5.4 【新增】热路径同步 I/O 审计（可能真降卡顿）—— 0.5 人日（审计）

见 §3.4 表。Yomori 的 588 MB / 整窗卡死那条链，与 `AGENTS.md` 性能约束 3（`Process.HandleCount` 每次 3~5 ms）同源。
**建议**：只审计 `Services/AsyncLog.cs`、`Services/LogBus.cs`、`Relay/` 下所有 `File.Append`/`StreamWriter` 调用点，确认是否有「锁内/热路径同步写盘」。**先只出清单，不改行为**。

### 5.5 【新增】第三方许可证声明 —— 0.25 人日

**事实**：`dist\` 里分发 **9 个第三方 dll**（`Microsoft.Windows.SDK.NET.dll` 27 MB、`Vortice.*` ×5、`SharpGen.*` ×2、`WinRT.Runtime.dll`）+ `7za.exe`（1.29 MB），但仓库里 **0 个 `THIRD-PARTY-NOTICES` / `LICENSE` 文件**（实测 glob 为空）。
Yomori 的做法（`build.yml:315-319`，转述）：**许可证原文从 CI 的 vcpkg 安装目录抓，生成 `THIRD-PARTY-NOTICES.txt` 打进包 ——「Apache-2.0 §4 要求分发时随附许可证副本」**。
**对我们**：Vortice 是 MIT/ Apache-2.0、7-Zip 是 LGPL（**注意：7za.exe 以独立 exe 分发，LGPL 合规，但不能静态链接**）。**这是一条真实的法律风险，不是学术问题**，成本极低。**建议列为 P2，但别永久搁置。**

---

## 6. 性价比排序与排期建议

### 6.1 前 5 项（与 §0.2 一致，补验证方式）

| 序 | 项 | 人日 | 验收方式（必须可复现） |
|---|---|---|---|
| 1 | 版本判据归一 + 回退链 | **0.5** | ① `ReadAgentVersion(dist\SoulLink.Agent.exe)` 返回非空；② `ValidAgent` 与 `AgentSync.Inspect` 对同一组输入给出**一致**结论（用 layout-check 断言成对验证）；③ A2 投送实测成功 |
| 2 | Broker 错误码人话表 + 可恢复分类 | **0.5** | ① 19 个码全部有映射（断言无遗漏）；② `AGENT_INVALID` 走「不重试」分支、`TIMEOUT` 走「重试」分支（单测）；③ 日志只记一次不可恢复错误 |
| 3 | 90 个断言串 runner + 挂构建 | **0.5** | ① `run-checks.ps1` 跑完输出「N 通过 / M 失败」；② 人为把一条断言改错 → 构建失败（**反向对照**）；③ Release 构建耗时增量 < 60 s |
| 4 | 判据分层 + 活跃任务不覆盖 | **1.0** | ① 部署前扫到 `running`+心跳新鲜 → 跳过并留日志；② `running` 但心跳陈旧 → **不跳过**（防永久跳过）；③ 升级实测不打断 A2 挂机 |
| 5 | build.bat 构建断言集 | **0.5** | ① 删掉 `dist\` 里一个必需 dll → 构建失败；② agent 版本读不出 → 构建失败；③ 正常构建不误报 |

### 6.2 建议顺序

```
1（0.5d，P0-1+P0-2 根治）
  → 5（0.5d，给 1 加静态防线，防复发）
  → 2（0.5d，P0-3）
  → 3（0.5d，给 1/2/5 全部加上"被推翻时会红"的保险）
  → 4（1.0d，不打断挂机）
  → 9（0.25d，前端兜底）
  → 5.3/5.4/5.5（P2，视窗口）
```

**理由**：1 是唯一「同时消两个 P0」的改动，必须最先；5 紧跟 1（否则 1 的修法本身没有防线）；3 放在 1/2 之后才有东西可测。

### 6.3 明确不做（避免第三次被提起）

| 项 | 为什么不做 |
|---|---|
| 换 FreeRDP / 抄 `ycn_*.c` | BENCH-01 §3.3 已定：与 TermWrap 会话所有权冲突、9~13 会话成本、GPL 传染、不解决帧供给痛点 |
| 脏区增量编码 | BENCH-01 §4.3 已实测：上限 ~2.5 ms/帧，且 NVENC 喂不进矩形补丁 |
| AVC444 低色度 | 我们连 FreeRDP 都没有；它自己折腾 3 份计划书 + 1 次撤回只换来 −5.6% CPU |
| WinUI 3 迁移 | 瓶颈不在 UI，且会毁掉单文件 3.3 MB 优势 |
| 更新检查 6 态（#5） | 见 §4.2，**前提不成立** |
| GitHub Actions CI（#6） | 无 PR 流程，发布红线禁止绕过 `publish-release.ps1`；只做 §3.2 的本地断言 |
| 框架级单测（xUnit） | 见 §3.3，**形态不对**，我们已有 90 个断言 |

---

## 7. GPL-3.0 红线复核（验收要求 ④）

### 7.1 事实（本次重新核实）

| 项 | 结果 |
|---|---|
| 仓库根 `LICENSE` | 35,149 字节，**GPL v3 标准全文** |
| 许可证（API） | `license.spdx_id = "GPL-3.0"` |
| 被 GPL 覆盖的范围 | `winui3/src/Yomori.RdpNative/*.c/.h`、全部 `.cs`、5 个 FreeRDP patch、`winui3/tests/*.c/.py`、`winui3/tools/*.ps1/.py/.iss`、`.github/workflows/*.yml`、`docs/*.md` |
| 上游依赖许可 | FreeRDP/WinPR/OpenSSL/zlib/cjson/FFmpeg（`winui3/third_party/licenses/` 有清单）——**上游许可不解除 Yomori 自有代码的 GPL** |

### 7.2 本文做了什么 / 没做什么

| 行为 | 是否发生 |
|---|---|
| 把 Yomori 的 `.c/.h/.cs/.iss/.yml/.ps1/.py` 复制进本仓库 | **没有** |
| 把它的常量/属性名抄进本仓库源码 | **没有**（本文档只**提及** `AgentVersion`、`FileVersion`、`ExitAgentBusy` 等**接口事实**用于说明机制） |
| 逐行翻译它的实现 | **没有**。§2 全部是「我们自己怎么写」的方向描述 |
| 阅读文档/注释，提炼**思想/不变量/失败语义**，用我们自己的话重述 | **有**（这是允许的：思想与接口不受版权保护） |
| 本文档进编译 | **不会**（`.md` 不参与 `dotnet build`；`DefaultItemExcludes` 已排除 docs 类路径） |
| 把 GPL 代码下载到工作区 | **没有**。tarball 与解压结果**全部在 `%TEMP%\yomori-probe`**，工作区 `git status` 无新增二进制 |

### 7.3 三种常见误区的明确回答

| 误区 | 回答 |
|---|---|
| 「错误码表是它写的，抄了就是侵权？」 | **不是。** RDP 错误码（`282`/`3335`/`3338` 等）是**微软公开文档的接口事实**，任何实现都可以映射。它写的中文文案**不能抄**，但「哪些码对应哪类问题」是事实 |
| 「版本判据用 `FileVersion` 是抄它的？」 | **不是。** 「用文件版本资源判断是否需要替换」是**通用工程实践**，不是 Yomori 独创表达。我们只是因为**自己实测出元数据读不到**才改，与它的代码无关 |
| 「90 个断言串 runner 是抄它的测试工程？」 | **不是。** 「跑测试并让失败阻断构建」是 CI 的通用定义。**我们用的是自己已有的 90 个项目**，形态（`ProjectReference`）也是我们自己的 |

### 7.4 落地时的强制要求

任何按本文落地的 PR/提交，**必须在代码注释里注明「机制来源」**，格式建议：

```
// 机制来源（仅思想，无代码复制）：Yomori (GPL-3.0) 的 <文件>:<行>，
// 见 docs/research/BENCH-YOMORI-04-RESEARCH-20261124.md §X.Y
```

这与 BENCH-01 §7.3 的决策树一致：**要思想不要代码 → 允许且无需许可动作，但必须留痕**；**要抄任何一行 → 禁止**。

---

## 8. 证据索引（一表可回查）

### 8.1 对方证据（仓库路径，全部实际读取）

| 结论 | 文件 | 关键位置 |
|---|---|---|
| 上游已冻结 / fork 关系 | `api.github.com/repos/aitnr/yomori` | `fork:true`、`pushed_at:2026-11-18`、`parent:yukikino001-ship-it/Anigame-script-manager`（`pushed_at:2026-06-01`） |
| 最后提交 | `/commits?per_page=100` | 首条 `089d06c7` 2026-11-18T08:00:40Z |
| **读文件版本资源而非元数据（关键）** | `winui3/src/Yomori.App/Services/RdpSessionService.cs` | `:1669-1692`（`ReadAgentExeVersion` 的完整理由） |
| **`AgentVersion` 直接喂 `FileVersion`（关键）** | `winui3/src/Yomori.Agent/Yomori.Agent.csproj` | `:50-52` |
| `AgentVersion` 写入程序集元数据 | 同上 | `:64-66` |
| 「不能用 `$(Version)` 代替」 | 同上 | `:47-49` |
| 判据分层纪律（「两套口径最难查」） | `winui3/src/Yomori.App/Services/AgentAutoDeploy.cs` | `:141-146` |
| 活跃任务不覆盖 + 心跳新鲜度 | 同上 | `:116-121`、`:166-169` |
| 退出码约定 | 同上 | `:40-43` |
| 双判据启动闸门 | `winui3/src/Yomori.App/Services/AgentStartGate.cs` | `:19-28`、`:42-84`、`:92-104`、`:40` |
| 内容哈希「读不出判不等」 | `winui3/src/Yomori.App/Services/AgentPayloadIdentity.cs` | `:75-79` |
| 版本读取语义（读不出判不同） | `winui3/src/Yomori.App/Models/AgentVersionInfo.cs` | `:93-116`、`:118-143` |
| 状态机（纯函数 + 非法迁移保持原状态） | `winui3/src/Yomori.App/Models/RdpClientStateMachine.cs` | 全文（`Transition` / `IsValid`） |
| **错误码 → 人话 + 动作** | `winui3/src/Yomori.App/Models/RdpErrorText.cs` | `DisconnectReason`/`FatalError`/`LogonError` 三张表 |
| 构建断言集（34 处 throw） | `.github/workflows/build.yml` | `:404-500`（installer 断言段）、`:443-453`（AgentVersion 唯一来源） |
| CI 每次构建归档 | `.github/workflows/ci.yml` | `:44-169`、`:27-31`（不取消并发） |
| 测试工程「链接生产源码」形态 | `winui3/tests/Yomori.LogChecks/Yomori.LogChecks.csproj` | `:13-37` |
| ISPP `#if` 字符串比较坑 | `winui3/tools/installer.iss` | `:265-268`、`:257-290` |
| 锁内绝不 I/O（588 MB 事故） | `winui3/docs/rdp-hang-and-heap-corruption-2026-10-16.md` | `:99-107`、`:186-188`（反向对照） |
| 工作集延迟回收 | `winui3/src/Yomori.App/Services/WorkingSetTrimScheduler.cs` + `WorkingSetTrimmer.cs` | `Scheduler:20,71-113,154-159,197-204`；`Trimmer:33` |
| 它自己承认的限制 | `winui3/docs/known-limits-and-roadmap.md` | `:6-44` |
| AVC444 `op=2` 协议事实 | `winui3/docs/rdp-avc444-aux-drop-plan-2026-11-14.md` | `:22,51,69` |
| 许可证 | `LICENSE` | 35,149 B，GPL v3 |

### 8.2 我方证据（文件:行，全部实测）

| 结论 | 文件:行 |
|---|---|
| 判据 A（元数据）实现 | `Services/BrokerInstall.cs:68-97` |
| 判据 A 决策与方向性注释 | `Services/BrokerInstall.cs:31-51` |
| 判据 A 消费（服务侧） | `Services/BrokerService.cs:196-198` |
| 判据 A 消费（客户端侧） | `Services/RelayClient.cs:188`、`Services/SystemLauncher.cs:460` |
| 判据 B（FileVersion）实现 | `Services/AgentSync.cs:91-95` |
| 判据 B 决策 | `Services/AgentSync.cs:101-102`、`:138-147` |
| 判据 B 复校验（第二处） | `Services/AgentSync.cs:200-203` |
| 5 态枚举 | `Services/AgentSync.cs:11-23` |
| 修复逻辑（含 Kill 占用进程） | `Services/AgentSync.cs:156-235` |
| 重查事件 | `Services/AgentSync.cs:47-58` |
| 顶栏渲染 5 态 | `Views/MainWindow.xaml.cs:770-785` |
| 启动时自动修复（会 Kill agent） | `Views/MainWindow.xaml.cs:788-835` |
| XAML 默认值（已改中性） | `Views/MainWindow.xaml:128` |
| 重查订阅/实现 | `Views/MainWindow.xaml.cs:118`、`:710-726` |
| 设置页只探 health、不含 agent 判定 | `Views/SettingsPage.xaml.cs:522-571` |
| 设置页「修复注入代理」按钮 | `Views/SettingsPage.xaml.cs:493-514` |
| 重试循环（不区分可恢复） | `Controls/RdpConsole.cs:795-856`（`_rebuildAttempt` `:796`，退避 `:820`） |
| 失败指引固定文案 | `Controls/RdpConsole.cs:814` |
| 断开原因只记日志 | `Controls/RdpConsole.cs:2388-2401` |
| Broker 错误码全集 | `Services/BrokerProtocol.cs:298-326`、`Services/BrokerService.cs:128-227` |
| 协议/程序版本分离（我们已做对） | `Services/BrokerProtocol.cs:44-64`、`:130-187` |
| agent = 主程序字节副本 | `build.bat:94-96`（实测 SHA256 相同） |
| 单文件 bundle 证据 | `dist\SoulLink.exe` bundle 头 @68848；`PEReader.HasMetadata=False`；`FileVersion=3.3.39.0` |
| `<AgentVersion>` 已在 csproj | `SoulLink.csproj`（`<AgentVersion>1.0.0</AgentVersion>`） |
| 已有 AgentVersion 非空守卫 | `SoulLink.csproj:15-17`（`CheckAgentVersion` Target） |
| 已有编译红线 | `SoulLink.csproj:36-38` + `scripts/agent-dep-redline.ps1` |
| 已有外置看门狗接线 | `SoulLink.csproj:21-29` |
| 90 个断言项目 | `tools/layout-check/*.csproj`（实测 90 个；17 个有构建产物） |
| 断言项目形态 | `tools/layout-check/BrokerAuthCheck.cs:8-13,59`；`AgentCmdProbe.csproj` |
| **无 runner** | `scripts/`、`build.bat` 中 `layout-check` **0 命中** |
| **无 CI** | `.github/` **不存在** |
| **无第三方许可声明** | 全仓 `*THIRD*`/`*NOTICE*`/`*LICENSE*` **0 命中**；`dist\` 分发 9 个第三方 dll + 7za.exe |
| TickHub 登记入口（替代 DispatcherTimer） | `Services/TickHub.cs:60,95` |
| 无 `EmptyWorkingSet` | 全仓 0 命中；仅 `Services/SessionSlimmer.cs:286,364` 读 `WorkingSet64` 做展示 |
| 单实例用 Mutex | `Services/Ipc.cs:34-55`、`Services/AppIdentity.cs:154` |
| 双锁分层（我们已有类似实践） | `Services/ManualAgent.cs:202-238`（命名 Mutex 判活） |

---

## 8.5 【交付时点补充】core-fixer 已按 §2.1 路线①落地，本报告据此更新判断

> 本节写于交付前最后一次核对（2026-11-24 15:5x）。**团队工作树已变动**，为免后人拿本文当"待办"而重复劳动，如实记录现状 + 剩余缺口。

### 8.5.1 已落地（core-fixer，工作树未提交）

| 项 | 位置 | 与本文建议的关系 |
|---|---|---|
| 随包 version 标记文件 | `Services/BrokerInstall.cs:18,21-26`（`AgentVersionFileName = "SoulLink.Agent.version"`） | **本文 §2.1.4 的方案③（我曾判"不推荐"）**，但已补齐我担心的两个缺口，见下 |
| 读取回退链 | `BrokerInstall.cs:78-121`：**先 version 文件 → 再 PE 元数据** | 与 §2.1.4 推荐的"回退链"思路一致（顺序相反，但同样保证**两条路都读**） |
| 构建期写出标记 | `SoulLink.csproj:42-47`（`WriteAgentVersionToOutput` / `WriteAgentVersionToPublish`，AfterBuild + AfterPublish 双写） | 正是我担心的"漏打包 = 静默失败"的**正面解法**：写进构建而非手工放 |
| dist 复制 | `build.bat:96-97` | 同上 |
| 部署时随 agent 复制标记 | `Services/AgentSync.cs:196-215`（`CopyAgentVersionMarker`，含注释"version 文件必须跟着 agent 走"） | **这一条最关键**——正是我判"方案③不推荐"的唯一理由，已被解决 |
| 服务安装器同步标记 | `tools/broker-setup/Program.cs:139`（`SyncFile` 同步 `SoulLink.Agent.version`） | 覆盖了"Program Files 侧漏文件"这条路 |
| **判据归一（本文 §2.2 的核心诉求）** | `Services/AgentSync.cs:92-96,139` —— `IsAgentCurrent` 与 `Inspect` 现在都调 `BrokerInstall.ValidAgent` | ✅ **§2.1 的根本病灶已消除**：不再有两套互斥判据 |
| 三方版本显示 | `Services/AgentSync.cs:118-122`（"当前程序 X · 服务 Y · 注入代理 Z"） | ✅ 对应 HANDOFF §1.2 的要求 |

### 8.5.2 我实测验证过的两条（避免误判为"已修好"）

| 验证项 | 方法 | 结果 |
|---|---|---|
| UTF-8 BOM 会不会破坏版本解析 | 按 `WriteLinesToFile Encoding="UTF-8"` 的实际行为造一个带 BOM 的 `1.0.0` 文件，走 `ReadAllText().Trim()` → `Version.TryParse` | **安全**：BOM 被 `ReadAllText` 吃掉，`Trim()` 后是 `1.0.0`，首字符 `0x0031`，解析成功。**这条不必改** |
| `WriteAgentVersionToOutput` 用的 `$(TargetDir)` 路径 | 读 `SoulLink.csproj:42-43` | `AfterBuild` 阶段 `$(TargetDir)` 已定义且带尾斜杠，拼接 `SoulLink.Agent.version` 正确（`AfterPublish` 用 `$(PublishDir)` 同理） |

### 8.5.3 仍未解决 / 需 verifier 与后续任务跟进的缺口（**这才是本节的用处**）

| # | 缺口 | 证据（实测） | 后果 | 归谁 |
|---|---|---|---|---|
| G1 | **`dist\SoulLink.Agent.version` 当前不存在** | 实测 `dist\SoulLink.Agent.version` → **ABSENT**（`dist\SoulLink.exe` 仍是 3.3.39 旧构建） | 说明**还没有重新跑过 build.bat**。修复只在源码里，产物未更新。**不重跑构建就测投送必然还是 AGENT_INVALID** | verifier / t5 |
| G2 | **`C:\Program Files\SoulLinkDev` 下也没有标记文件** | 实测 `SoulLink.Agent.version` → **ABSENT**（该目录同时堆了 **19 个 `.bak` / `.old` 的 agent 副本**，最大 4.4 MB × 19） | 重跑构建后**必须再走一次「修复注入代理」或重装服务**，标记才会落到 Program Files。只重构建不够 | verifier / t5 |
| G3 | **`RepairCore` 的 `CopyAgentVersionMarker` 吞掉所有异常** | `Services/AgentSync.cs:214`：`try { File.Copy(src, dst, true); } catch { }` | 复制失败（Program Files 权限/占用）时**静默无标记**，随后 `ValidAgent` 判假 → 用户看到「修复失败」但拿不到原因。**建议至少 `LogBus.Warn` 一行**（这是本轮 P0 同类病：静默失败） | t1 收尾 / 后续 |
| G4 | **服务侧标记缺失时仍靠 PE 元数据兜底** | `BrokerInstall.cs:97-99`：元数据分支保留，单文件下 `HasMetadata=False` → 返回 null | `ValidAgent` 的 `sv`（服务自述版本）在**服务本体也是单文件**时同样读不到 → 走 `"服务无标识 → 放行"` 分支（`:53`）。**功能上可接受**（放行即能用），但意味着"服务低于最低 agent 版本"这个拦截**实际上永远不会触发**。属已知取舍，建议在注释里点明，别让人以为它在工作 | 记录即可 |
| G5 | **`AgentSync.StopAgentsLocking` 仍会 Kill 正在跑任务的 agent** | `Services/AgentSync.cs:218-244` | §5.2 指出的风险**未变**：启动时 `EnsureEngineAsync` 自动修复 → 可能顶掉 A2 正在挂机的 agent。**与冻结项 #8「活跃任务不覆盖」是同一个修法**，建议合并 | §0.2 第 4 项 |
| G6 | **19 个 `.bak` 副本留在 Program Files** | 实测目录列表 | 违反 AGENTS.md「工作区整洁」精神；且 `StopAgentsLocking` 按路径精确匹配，这些**不会被误杀**（好），但它们让"哪个才是当前的"难以肉眼判断。**建议安装器/修复逻辑顺手清理** | 低优先 |

> **结论**：§2.1 的**根因**（两套互斥判据）已由 core-fixer 消除，本文 §0.1① 的判断成立且已被采纳。但**修复尚未到产物**（G1/G2），**且有一处静默失败**（G3）。**不要把"源码改了"当成"P0 修好了"** —— 这正是 HANDOFF §F 记的流程事故（"轻信成员 build 0 error"）。

---

## 9. 边界遵守自查

未改本仓库任何产品代码/脚本/配置/版本号；未碰 `dist\stable\SoulLink-Stable.exe`（只读其 `FileVersion` 做对照）；未使用 A1（session 2）/ A4（session 3）；未停/启任何进程；未弹窗；未改注册表；未发布、未碰 COS / `latest.json`；未执行任何 git 写操作；`bin/`/`obj/`/`dist/` 未改动；**未在项目根新建任何目录**；临时文件全部在 **`%TEMP%\yomori-probe`**（tarball 18.9 MB + 解压 304 文件）。

外网访问：`codeload.github.com`（下载 tarball）、`api.github.com`（3 次，配额耗尽）、`github.com`（1 次，被截断）。**均只读**。

**未把任何 GPL 代码复制进本仓库**（§7.2）。唯一写入 = 本文档 + `docs/research/README.md` 一行索引。

---

**文档作者**：yomori-scout
**最后更新**：2026-11-24
**任务**：dsh-team `soullink-p0-fix-yomori` / `t4` / attempt `b3b1f86d-f129-43d4-8fd5-aabb5151609f`
