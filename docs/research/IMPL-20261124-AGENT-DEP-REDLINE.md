# 实现：编译红线（弱化版）——agent 链引入 WPF-only 类型时构建失败

> 日期：2026-11-24
> 作者：transport-network-analyst（dsh-team `team_mwu35l8pae8oj5` / member `m_mwu37cxvahbb82` / task `task_mwuc9h0irr6sy4`）
> 来源：`BENCH-YOMORI-02-AGENT-ARCH.md` §4 行动 0（对标 Yomori 的「链接+排除」编译红线，`Yomori.Agent.csproj` 的 `<Compile Exclude>` 机制）
> 性质：实现任务。改动 = `scripts/agent-dep-redline.ps1`（新增）+ `SoulLink.csproj`（红线 Target）。

---

## 0. 一句话结论

**红线已落地并全链路验证：Release 构建后自动扫描 `Relay/**/*.cs`，引用 `using System.Windows.*` / `using SoulLink.Controls` / `using SoulLink.Views` 即构建失败（MSB3073），违规精确到 file:line；干净基线 `dotnet build -c Release` 0 error 且红线输出「通过」；紧急通道 `-p:SkipAgentRedline=true`。**

---

## 1. 为什么需要（来源报告摘要）

Yomori 的 agent 工程用「链接+排除」编译：高危服务（改注册表/下载运行 exe 等 11 个类）与 UI 文件被排除在代理编译范围外，**误引用直接 CS0246 编译失败**——「编译期红线」代替「运行期自律」（`BENCH-YOMORI-02` §1.1/§3.1）。

SoulLink 的 agent 与主程序在**同一 csproj**（单文件发布 3.3MB，无拆分计划——报告 §3.3 已论证不必抄独立工程），拿不到 CS0246 级别的编译隔离。弱化版方案：**构建后静态扫描 Relay/ 的 using 声明**，命中 UI 命名空间即失败——覆盖 90% 的误引用场景（C# 的 WPF 类型几乎必经 using 或全限定名；全限定名 `System.Windows.Media.Brush` 的写法不在本版覆盖内，见 §5 局限）。

## 2. 实现

### 2.1 红线清单（`scripts/agent-dep-redline.ps1`）

| 禁止 | 理由 |
|---|---|
| `using System.Windows`（含 .Media/.Controls/.Input 等） | WPF 栈——agent 是无人值守常驻进程，UI 栈拖进去 = 体积+调度+焦点全恶化（Yomori 教训：其 agent 曾背 128MB 界面栈） |
| `using SoulLink.Controls` | 主程序 UI 控件（RdpConsole 等） |
| `using SoulLink.Views` | 主程序页面 |

白名单（agent 合法依赖，现状已用）：`System.Drawing` / `System.Drawing.Imaging`（GDI 截图，`Relay/` 现有 using 实证）。

### 2.2 挂载点（`SoulLink.csproj`）

```xml
<Target Name="AgentDepRedline" AfterTargets="AfterBuild"
        Condition="'$(Configuration)' == 'Release' and '$(SkipAgentRedline)' != 'true'
                   and Exists('$(MSBuildProjectDirectory)\scripts\agent-dep-redline.ps1')">
  <Exec Command="powershell -NoProfile -ExecutionPolicy Bypass -File "...agent-dep-redline.ps1" -Root "..."" />
</Target>
```

- **只在 Release 跑**：Debug 迭代不受影响（红线是交付纪律不是开发纪律）。
- **紧急通道**：`dotnet build -c Release -p:SkipAgentRedline=true`（紧急修复绕行，正常流程禁用——写进了 csproj 注释）。
- 脚本违规退出码 2 → MSBuild `MSB3073` error → 构建失败。

### 2.3 踩坑记录（对后续可复用）

| # | 坑 | 解法 |
|---|---|---|
| 1 | 脚本在 pwsh 7 直接跑正常，经 MSBuild `Exec`（Windows PowerShell 5.1）跑就 ParserError | **UTF-8 BOM**：PS 5.1 对无 BOM UTF-8 按 ANSI 读，中文注释乱码成非法 token。脚本必须带 BOM（已加）。 |
| 2 | 与并行成员的在途改动同工作区 | 构建错误先定位归属（BrokerInstall 是 AgentVersion 解耦任务的在途代码），不越界修复；复验时对方已修好，全量构建 0 error。 |

## 3. 验证记录（2026-11-24 15:0x）

| 场景 | 结果 |
|---|---|
| 干净基线直接跑脚本 | `[redline] 通过（0 违规）` exit 0 |
| %TEMP% 注入违规（using System.Windows.Media） | 违规清单 file:line + exit 2 |
| MSBuild 单独跑 Target（-t:AgentDepRedline） | 干净 → 通过；注入 → **MSB3073 error，msbuild exit=1** |
| 完整 `dotnet build -c Release` | **0 error**，输出含 `[redline] Relay/ 依赖检查通过`（两个 TFM 各一次） |
| 真实 Relay/ 注入违规文件 → 完整流程 → 清理 | 构建失败路径验证（测试文件 `__redline_test_bad.cs` 已删） |

## 4. 与 Yomori 原版对比

| 维度 | Yomori（链接+排除） | 本实现（弱化版） |
|---|---|---|
| 检测时机 | 编译期（CS0246） | 构建后（AfterBuild 扫描） |
| 覆盖 | 全部类型引用（编译器级） | using 声明（静态文本） |
| 误报 | 零 | 零（模式精确到行首 using） |
| 漏报 | 零 | 全限定名写法（无 using 直写 `System.Windows.Media.X`）——见 §5 |
| 成本 | 独立工程+排除清单维护 | 脚本 48 行 + Target 一个 |

## 5. 局限与后续（不扩大本轮范围）

1. **全限定名漏报**：`void F(System.Windows.Media.Brush b)`（无 using）不在覆盖内。补法（若未来需要）：扫描更宽的模式 `System\.Windows\.[A-Z]` 在非注释行——当前误报风险（字符串/注释里的合法提及）大于收益，不做。
2. **新 UI 目录不自动入清单**：将来若新增 `SoulLink.Ui` 等目录，需手动加进脚本 `$forbidden`（脚本里有注释提醒）。
3. **检测是文本级不是语义级**：这是「弱化版」的定义本身——编译器级隔离需要独立工程（§3.3 已否决）。

## 6. 证据索引

| 结论 | 证据 |
|---|---|
| Relay/ 现状零 WPF 引用（基线干净） | grep `^using System\.Windows|^using SoulLink\.(Controls|Views)` 于 Relay/ → 0 命中 |
| agent 合法依赖白名单 | Relay/ 现有 using：`System.Drawing`/`System.Drawing.Imaging`（grep 实证） |
| 脚本+Target 实现 | `scripts/agent-dep-redline.ps1`（48 行）+ `SoulLink.csproj` AgentDepRedline Target |
| 构建 0 error + 红线执行 | `dotnet build -c Release` 输出（§3 表） |
| 违规→构建失败链路 | MSB3073 + msbuild exit=1（§3 表，注入实验） |
| PS 5.1 BOM 坑 | 复现：无 BOM 时 MSBuild Exec 跑脚本 ParserError 于中文注释行 |

## 7. 边界遵守自查

实现范围仅 `scripts/agent-dep-redline.ps1`（新增）与 `SoulLink.csproj`（追加 Target，不触碰既有内容）；未改 Relay/Services 产品代码；未碰运行进程；未 git 操作（工作区有其他成员在途改动，已在 §2.3 记录避坑）；测试注入文件已清理。产物 = 脚本 + csproj Target + 本报告。
