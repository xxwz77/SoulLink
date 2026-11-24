# 实现设计：agent 版本与主程序版本解耦（只读设计规格，未施工）

> 日期：2026-11-24
> 作者：rdp-session-analyst（dsh-team team_mwu35l8pae8oj5 / member m_mwu3790mafbish / task task_mwuby7uuq2hveb）
> 状态：仅完成 S1 只读勘察 + 设计规格，未改任何产品代码/版本号。
> 原因：本任务要求改 SoulLink.csproj（加 AgentVersion 元数据 + 升 3.3.39）、改 BrokerInstall.ValidAgent、改迁移逻辑——是实现类任务，与本成员红线冲突。
> 证据分级：【代码事实】= 源码 file:line；【对标证据】= BENCH-YOMORI-02；【设计】= 待实现成员的方案。

---

## 0. 一句话结论（给 Lead）

这是「Yomori §4 动作 1（AgentVersion 解耦，ROI 最高）」的实现任务，不是只读审计。本成员不能改产品代码/版本号，只交付下面的可执行设计规格。请转给有写权限的实现成员，或明确授权本成员临时扩权（workspace-write + 豁免版本号红线）。

---

## 1. 现状（代码事实，file:line 可回查）

| 事实 | 位置 |
|---|---|
| agent 就是主 exe 副本（改名 + manifest asInvoker） | Services/ManualAgent.cs:148-188（File.Copy + ManifestPatcher.MakeAsInvoker） |
| 版本判据 = 主程序 FileVersion 完全一致 | Services/BrokerInstall.cs:21-31 ValidAgent |
| 判定需要替换的入口 | Services/BrokerInstall.cs:44-49 AgentFor |
| 投送前校验（2 处） | Services/BrokerService.cs:196-198；Services/SystemLauncher.cs:459-460 |
| 主程序当前版本 | SoulLink.csproj:1 Version=3.3.38 |
| 身份判定按文件名 | Services/AppIdentity.cs:175-189 IsAgentExecutable |
| 主程序单文件发布体积 | build.bat:42 注释：框架依赖单文件 3.3MB |

问题：ValidAgent 拿「主程序 FileVersion」当 agent 版本，主程序每次升版本号（3.3.38→3.3.39），即便 agent 载荷一个字节没动，也判不一致 → AgentFor 返回空 → 投送前校验失败/触发重铺，挂机中的 agent 被顶掉重启。

---

## 2. 对标：Yomori AgentVersionInfo（BENCH-YOMORI-02-AGENT-ARCH.md）

| 项 | Yomori 做法 | SoulLink 落点 |
|---|---|---|
| 单一来源 | Yomori.Agent.csproj 加 AgentVersion=2.1.5，写 AssemblyMetadata + 文件版本资源两处；代码无常量 | SoulLink.csproj 加 AgentVersion + AssemblyMetadata |
| 解耦理由 | 主程序每次发版号变，代理载荷大多没动，用主程序版本判是否替换 = 无谓搬运 + 顶掉常驻代理 | 本报告 §1 问题同构 |
| 读不出语义 | Same() 任一侧读不出判不同；IsAtLeast() 读不出返回 false | ValidAgent 改：读不出判需替换 |
| 忘 +1 检测 | csproj 注释警告 + 冒烟断言盯着 AgentVersion 存在 | 见 §4 |
| 身份判定 | 主控端无 AgentVersion 元数据读空串 | 我们保留按文件名（AppIdentity:175-189），不抄 |

---

## 3. 目标设计（供实现成员照做）

### 3.1 引入 agent 独立版本标识（单一来源在 csproj）

SoulLink.csproj 加（不改 Version 语义，AgentVersion 独立）：

```xml
<PropertyGroup>
  <!-- agent 独立版本：只有 Relay/ 或 agent 用 Services 改动时才 +1；
       与主程序 Version 解耦：主程序发版不触发 agent 重铺 -->
  <AgentVersion>1.0.0</AgentVersion>
</PropertyGroup>
<ItemGroup>
  <AssemblyMetadata Include="AgentVersion" Value="$(AgentVersion)" />
</ItemGroup>
```

### 3.2 读 agent 版本

新增 helper（或 ValidAgent 内联）：读 AssemblyMetadata("AgentVersion")；读不出（老 agent 无该元数据）→ 判需替换。

### 3.3 改 ValidAgent（BrokerInstall.cs:21-31）

伪代码：
  path 校验不变；
  sv = ReadAgentVersion(source); av = ReadAgentVersion(agent);
  if (sv 空 || av 空) return false;   // 读不出判需替换（老 agent 无元数据）
  return sv == av;

### 3.4 老 agent 平滑迁移

老 agent（无 AgentVersion 元数据）ReadAgentVersion 返回 null → ValidAgent false → AgentFor 返回空 → 走现有重铺逻辑。这就是「判为需替换并平滑迁移」的既有路径，无需新迁移代码。

### 3.5 主程序升版本但 agent 未变时不重铺（代码路径证明）

- 主程序 Version 3.3.38→3.3.39，但 AgentVersion 保持 1.0.0；
- ValidAgent 不再看 FileVersion，只看 AgentVersion → source 与 agent 都是 1.0.0 → true → 不重铺；
- 只有 Relay/ 或 agent Services 改动、AgentVersion +1 时，才 false → 重铺。

---

## 4. 忘记递增的检测手段（设计）

Yomori 用冒烟断言盯着 AgentVersion 存在。SoulLink 落点二选一：
1. 构建期断言（推荐）：主 csproj 加 Target，检测 agent 相关文件最新变更未 +1 时 warning（软提示）。
2. 冒烟测试：tools/layout-check/ 加 Check，断言 AssemblyMetadata("AgentVersion") 非空（参照 BrokerCheck.cs / AgentNameCheck.cs 写法）。

注意：方案 1 依赖 git 变更历史，本仓库红线禁成员擅自 git；实现时由 Lead 决定，推荐纯文件检查的方案 2。

---

## 5. 版本号与构建

- 主程序 Version 升 3.3.39（由 Lead 统一升，本成员不升）；
- AgentVersion 首次设 1.0.0；
- dotnet build -c Release 0 error；构建后清 bin/obj。

---

## 6. 可复现自证步骤（实现成员完成后执行）

1. dotnet build -c Release 0 error；
2. ManualAgent.Export 导出 agent，读 AssemblyMetadata("AgentVersion") == 1.0.0；
3. 只升主程序 Version（不改 AgentVersion），重跑 ValidAgent(source, agent) == true（不重铺）；
4. 改一个 Relay 文件 + AgentVersion +1，ValidAgent == false（需替换）；
5. 删掉 agent 的 AgentVersion 元数据模拟老版本，ValidAgent == false（平滑迁移触发）。

---

## 7. 本成员停在此处的原因 + 转交

本任务要求改 SoulLink.csproj / BrokerInstall.cs / 迁移逻辑 / 升 3.3.39，属产品代码 + 版本号写入，违反本成员只读红线。已请求 Lead：转实现成员，或明确授权扩权。

---

## 8. 实施记录（2026-11-24，Lead 授权后执行）

### 8.1 改动清单

| 文件 | 改动 |
|---|---|
| SoulLink.csproj | ① Version 3.3.38 → 3.3.39（Lead 授权本次由我改）；② 加 AgentVersion=1.0.0（独立于 Version，单一来源）；③ 加 AssemblyMetadata Include="AgentVersion"；④ 加 CheckAgentVersion Target（BeforeTargets=CoreCompile，AgentVersion 为空即 error = 忘 +1 检测的编译期守卫） |
| Services/BrokerInstall.cs | ValidAgent 改为读 AssemblyMetadata("AgentVersion")（新 helper ReadAgentVersion，用 System.Reflection.Metadata PEReader 零加载读元数据，不锁文件、不触发同 identity 冲突）；路径校验保留；读不出判需替换 |

### 8.2 构建结果

- dotnet build -c Release **0 error**（166 warning 为既有存量，非本次引入）。
- 注：另一次 build 报 MSB3027（SoulLink.exe 被 PID 49504 主程序实例锁定）是并发成员启动的验证实例占用输出 exe，与本次代码无关；0 error 的 build 已成功。

### 8.3 5 步自证（实测结果）

用 %TEMP% 独立探针（构建后已清理，未留 .cs 在工作区）读 SoulLink.dll 的 AssemblyMetadata("AgentVersion")：

| 步骤 | 场景 | 实测输出 | 结论 |
|---|---|---|---|
| 1 | 主程序 Version 升 3.3.39 | csproj Version=3.3.39 + 两 TFM dll 均读到 AgentVersion=1.0.0 | 版本号与元数据都已写入 |
| 2 | 导出 agent 读元数据 | 探针读 SoulLink.dll（agent 是同源副本）→ AgentVersion=1.0.0 | 元数据可读 |
| 3 | 主程序升版但 AgentVersion 未变 | Valid(src=1.0.0, agent=1.0.0) = true 不重铺 | 不触发重铺 |
| 4 | AgentVersion 不同（模拟 +1，9.9.9） | Valid(src=1.0.0, agent=9.9.9) = false 需替换 | 判需替换 |
| 5 | 老 agent 无元数据 | Valid(src=1.0.0, 老 dll 无元数据) = false 需替换 (av=null) | 平滑迁移触发 |

### 8.4 边界遵守

- 未碰 PID 13392（SoulLink-Stable）、未用 A1/A4、未杀任何进程（49504 是并发成员实例，未动）；未 git commit；未改注册表；未动 dist\stable\。
- TEMP 探针（agentver-probe / plain-dll / agentver-mismatch）已全部删除。

### 8.5 遗留

- bin/obj 未清：因 bin\Release\net11.0-windows\SoulLink.exe 被 PID 49504 锁定，且这是并发成员正在使用的构建产物，本成员不清（避免打断他人验证）。待该实例退出后由 Lead/实现成员统一清。
- 忘 +1 检测目前是存在性硬卡（AgentVersion 为空即 error），改了源码没 +1 的内容级检测需 git 基线，归 Lead 统一提交时做（见设计 §4 方案 2 的冒烟断言）。
---

## 9. 版本矩阵与兼容性（v2，修 3.3.38/3.3.39 混搭故障）

### 9.1 真实故障记录（设计依据）

2026-11-24 Lead 现场复现：Program Files\SoulLinkDev 下 SoulLink.exe=3.3.38（服务本体，旧）与 SoulLink.Agent.exe=3.3.39（agent，新）混搭，全部投送失败，日志报 AGENT_INVALID。根因：旧 ValidAgent 用 FileVersion 完全相等作为判据，服务与 agent 版本死绑定——只升级 agent 不升级服务必然全线失败。

**修复后语义**：ValidAgent 不再比 FileVersion，改为「agent 的独立 AgentVersion >= 服务本体要求的 AgentVersion」，且对「谁无元数据」做方向性区分（见 §9.2）。

### 9.2 兼容矩阵（实测，%TEMP% 探针跑过）

| 组合 | 服务本体 | agent | 判据结果 | 说明 |
|---|---|---|---|---|
| A | 1.0.0 | 1.0.0 | ALLOW | 版本一致，正常 |
| B | 1.0.0 | 1.1.0 | ALLOW | agent 更新于服务要求，允许（独立升级） |
| C | 1.1.0 | 1.0.0 | **REJECT agent 过低** | agent 低于服务要求的最低 agent 版本，拒绝投送 |
| D | **无元数据（老服务 3.3.38）** | 1.0.0 | **ALLOW 老服务不校验** | ★ 修复本次故障的关键：老服务投送新 agent 放行 |
| E | 1.0.0 | 无元数据（老 agent） | **REJECT 老 agent 无元数据** | 老 agent 判需替换 → 重铺平滑迁移 |
| F | 无元数据 | 无元数据 | REJECT 老 agent 无元数据 | 都要替换 |

### 9.3 升级顺序约束

- **不再有「服务与 agent 版本必须同步」的死绑定**。允许：agent 先升（组合 B/D）、服务先升（组合 C 会短暂拒绝投送直到 agent 跟上）。
- **唯一仍存在的约束**：服务要求的最低 agent 版本（AgentVersion）一旦提升，**旧 agent 会被拒绝**直到重铺到位（组合 C）。这是「保守拒绝」，防旧代理把新字段误判。
- 实际升级顺序建议：**先铺 agent（或同批铺），后升服务**，避免组合 C 的窗口。

### 9.4 老版本迁移

- 老 agent（无 AgentVersion 元数据）→ ValidAgent 返回 false → AgentFor 返回空 → 走现有重铺逻辑（BrokerService 停服务换文件时序）。
- 老服务（无 AgentVersion 元数据）→ 放行（不校验 agent 版本），投送照常。这是向后兼容：升级到带元数据的新服务前，旧服务仍能正常投送任意 agent。

### 9.5 安全边界不变

- agent 路径仍锁定 AgentExe（固定目录 + 系统 ACL），不放开到任意路径（ValidAgent 第一行路径校验保留）。
- 版本比较只在「路径校验通过」之后进行；读不出/解析失败一律保守拒绝（组合 C/E/F 的语义）。