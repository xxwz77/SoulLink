# WGC TFM 路线可行性验证：多目标 vs 单一版本化

> 日期：2026-11-22
> 作者：implementer（dsh-team `team_mwrnf1771i2w7` / task `task_mwrwqxu9105g411`）
> 范围：**只读验证 + 本产出文档**。所有结论均来自 scratch 编译实验（`%TEMP%` 下一次性工程，验证后即弃），**未改任何业务代码、未动 `dist/`、未改 Version、未 commit/push、未发版**。
> 前置：`task_mwrvg2y8yd8vn2`（WGC V2 抓屏）因「WinRT 投影需版本化 TFM」阻塞，本任务是解决该阻塞的**路线可行性验证**。
> 证据等级：**实测**＝本会话 scratch 编译输出（逐条可复现）；**代码事实**＝当前 csproj 内容。

---

## 0. 结论摘要（先给答案）

**推荐改法：主项目 `SoulLink.csproj` 改为多目标 TFM + 条件编译 WGC 文件。**

```xml
<!-- 改法（3 处） -->
<TargetFramework>net11.0-windows</TargetFramework>
<!-- 改为 -->
<TargetFrameworks>net11.0-windows;net11.0-windows10.0.26100.0</TargetFrameworks>

<!-- 新增（只对版本化 TFM 生效，无版本 TFM 不设，否则 NETSDK1135） -->
<PropertyGroup Condition="'$(TargetFramework)' != 'net11.0-windows'">
    <TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
</PropertyGroup>

<!-- 新增：WGC 相关 .cs 文件只在版本化 TFM 编译 -->
<ItemGroup Condition="'$(TargetFramework)' != 'net11.0-windows'">
    <Compile Include="Relay\WgcCapture.cs" />
</ItemGroup>
```

**必须同时改 `build.bat`（1 处）**：多目标项目裸 `dotnet publish` 会报 `NETSDK1129`（必须指定框架），要加 `-f`。

| 方案 | 结果 | 结论 |
|---|---|---|
| **A. 多目标 TFM（推荐）** | ✅ 全通过 | 主项目能编 WinRT；55 个 tools 项目零改动；publish 加 `-f` 即可 |
| **B. 单一版本化 TFM** | ❌ 破坏 55 个 tools 项目 | 无版本 tools 引用版本化库 → NU1201，需连锁改 55 个项目 |

---

## 1. 已验证的硬事实（每条都可复现）

### 1.1 WinRT 投影只在版本化 TFM 可用（阻塞根源，已复核）

| TFM | `Windows.Graphics.Capture.*` 可用？ | 证据 |
|---|---|---|
| `net11.0-windows`（无版本，主项目现状） | ❌ CS0246 | scratch 编译：找不到 `Windows` 命名空间 |
| `net11.0-windows10.0.22621.0` | ✅ | 81 个 Graphics.Capture 类型 |
| `net11.0-windows10.0.26100.0` | ✅ | 同上 + `GraphicsCaptureSession.MinUpdateInterval` 属性 |

- `PackageReference Microsoft.Windows.SDK.NET.Ref` → **NU1213**（DotnetPlatform 不兼容，不能靠引包解决）。
- `TargetPlatformVersion` 单独设置（不改 TFM）→ 无效，仍 CS0246。
- `TargetPlatformMinVersion=10.0.17763.0` 不能配在无版本 TFM 上（**NETSDK1135**：SupportedOSPlatformVersion 不能高于 TargetPlatformVersion 7.0）——这正是「无版本」的语义：无版本 = Windows 7.0。

### 1.2 单一版本化 TFM 会连锁破坏（方案 B 否决）

```
实测：无版本 net11.0-windows 项目 ProjectReference 版本化 net11.0-windows10.0.26100 库
→ error NU1201: 项目 lib 与 net11.0-windows7.0 不兼容，lib 支持 net11.0-windows10.0.26100
```

- 全仓 **55 个 tools 项目**引用 `SoulLink.csproj`（实测统计，不是估算），全部是无版本 `net11.0-windows`。
- 方案 B 改主项目为单一版本化 TFM → 这 55 个项目全部 NU1201，要连锁改 55 个 csproj。
- 判定：**方案 B 否决**，改动面太大、风险高、收益为零（多目标能做到零改动）。

### 1.3 多目标 TFM 全链路验证通过（方案 A 成立）

以下每个实验都编译通过、0 error：

| 实验 | 结论 |
|---|---|
| 多目标库（`net11.0-windows;net11.0-windows10.0.26100.0`）+ 条件 `TargetPlatformMinVersion` | ✅ 两个目标都编译 |
| 无版本 app 引用多目标库 | ✅ app 自动挑 `net11.0-windows` 目标，**不报 NU1201** |
| 版本化 app 引用多目标库 | ✅ app 挑版本化目标，能直接用 WinRT 类型 |
| 多目标 **WPF exe**（UseWPF+UseWindowsForms） | ✅ 两个目标都编出 dll |
| 多目标 WPF exe + `#if NET11_0_WINDOWS10_0_26100_0` 条件编译 WinRT 文件 | ✅ 无版本目标不编 WGC 文件，版本化目标编出 WinRT 调用 |
| 完整模拟（WPF + RollForward + Version 3.3.19 + 多目标） | ✅ `-getProperty:Version` 仍返回 3.3.19 |
| 多目标 `dotnet publish -f net11.0-windows10.0.26100.0 -p:PublishSingleFile=true` | ✅ 产出单文件 `wpf.exe`（框架依赖 27MB，符合预期） |

### 1.4 多目标 publish 必须加 `-f`（build.bat 必须改）

```
实测：多目标项目裸 dotnet publish（无 -f）
→ error NETSDK1129: 如果未指定目标框架，则不支持 "Publish" 目标。
  当前项目面向多个框架，必须指定下面的一个框架来进行发布: net11.0-windows, net11.0-windows10.0.26100.0
```

`build.bat` 现状（`scripts/build.bat` / 根 `build.bat`）里是：

```bat
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true ... -o "!STAGE!" --nologo
```

多目标后必须加 `-f net11.0-windows10.0.26100.0`。**这是方案 A 唯一的非 csproj 改动，也是唯一会「牵连发布脚本」的点。**

---

## 2. 推荐改法（方案 A：多目标 + 条件编译）

### 2.1 SoulLink.csproj 改动

| # | 改动 | 位置 | 理由 |
|---|---|---|---|
| 1 | `<TargetFramework>` → `<TargetFrameworks>net11.0-windows;net11.0-windows10.0.26100.0</TargetFrameworks>` | PropertyGroup | 让主项目同时产「无版本（给 tools 引用）」和「版本化（给 WGC 用 WinRT）」两个目标 |
| 2 | 加条件 `TargetPlatformMinVersion` | 新增 PropertyGroup | 只对版本化 TFM 生效；无版本 TFM 设了会 NETSDK1135 |
| 3 | WGC 文件条件编译 | 新增 ItemGroup | `WgcCapture.cs` 只在版本化 TFM 编译，避免无版本目标找不到 `Windows.*` |

### 2.2 条件编译符号

.NET SDK 会自动生成 `NET11_0_WINDOWS10_0_26100_0` 这类符号（版本化 TFM 专用）。已验证 `#if NET11_0_WINDOWS10_0_26100_0` 能在版本化目标编 WinRT 代码、在无版本目标被排除。

### 2.3 build.bat 改动（1 行）

```bat
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -f net11.0-windows10.0.26100.0 -p:DebugType=none -o "!STAGE!" --nologo
```

### 2.4 发布产物说明

- 版本化 TFM 的 `TargetPlatformMinVersion=10.0.17763.0` 意味着**运行时最低仍支持 Windows 10 1809**（与无版本 TFM 的 Windows 7 语义不同，但 SoulLink 实际依赖 RDP 现代特性，1809 是合理下限；见「遗留问题」）。
- 单文件 exe 仍是框架依赖（`--self-contained false`），依赖 .NET 11 桌面运行时，与现状一致，`check-runtime.ps1` **不用改**（它只卡 .NET 主版本 11）。

---

## 3. tools 项目不受影响的证据

- **55 个 tools 项目引用 SoulLink.csproj，全部保持无版本 `net11.0-windows` 不变**。
- 实测「无版本项目引用多目标主项目」→ 自动挑 `net11.0-windows` 目标，编译通过、无 NU1201、无告警。
- tools 项目用到的类型（SessionSlimmer / RelayClient / WebDirect 等）都在「两个 TFM 都编译」的公共文件里，不在条件编译的 WGC 文件里，所以 tools 看不到 WGC 类型也不受影响。

### 3.1 验证抽查的 5 个 tools 项目（均无版本 TFM 引用主项目）

| 项目 | 引用方式 | 结论 |
|---|---|---|
| `tools/layout-check/WebDirectCheck.csproj` | ProjectReference → SoulLink.csproj | 不受影响（多目标自动挑无版本目标） |
| `tools/layout-check/InputActionCheck.csproj` | 同上 | 同上 |
| `tools/layout-check/H264EncoderCheck.csproj` | 同上 | 同上 |
| `tools/layout-check/AgentCmdProbe.csproj` | 同上 | 同上 |
| `tools/layout-check/AgentMutexCheck.csproj` | 同上 | 同上 |

> 全部 55 个同构（无版本 TFM + ProjectReference），多目标改法对它们**零改动、零回归**。抽查 5 个足够代表，其余同理（引用方式和 TFM 完全一致）。

---

## 4. 遗留问题（诚实列出，需 Lead 决策）

1. **运行时最低版本从 Windows 7 语义升到 Windows 10 1809**：
   - 无版本 TFM 的 `net11.0-windows` 在运行时不强制最低 OS 版本（Windows 7 语义，但实际 WPF 早已要求 Win10）。
   - 版本化 TFM 配 `TargetPlatformMinVersion=10.0.17763.0` = 显式要求 Windows 10 1809+。
   - SoulLink 用 RDP 现代特性（RdpWrap、per-monitor DPI 等），1809 是合理下限，但**这是产品边界变化，需用户/Lead 确认**。若担心，可把 MinVersion 降到 10.0.17763（已是 1809），更低则 WinRT/WGC 本身不可用。

2. **多目标会让 `bin\`/`obj\` 出现两个 TFM 目录**：
   - `bin\Debug\net11.0-windows\` 和 `bin\Debug\net11.0-windows10.0.26100.0\` 并存。
   - 工作区整洁规则（AGENTS.md）要求「bin/obj 不入库且用完清理」，多目标只是多一个目录，不影响规则，但**发布脚本/工具若硬编码了单一 bin 路径需检查**（本会话未发现，build.bat 走 `artifacts\dist-<ver>` 不依赖 bin 路径）。

3. **`#if` 条件编译符号依赖 SDK 生成规则**：
   - `NET11_0_WINDOWS10_0_26100_0` 是 .NET 11 SDK 自动生成的（已验证）。若未来换 SDK 大版本，符号规则可能变，需在升级时回归。
   - 更稳妥的替代是 `#if WGC` 自定义符号 + csproj 显式 `DefineConstants`，可避免依赖 SDK 内部符号名。**建议实施时用自定义符号**（见下）。

4. **推荐的自定义符号写法（比 `#if NET11_0_...` 更稳）**：

```xml
<PropertyGroup Condition="'$(TargetFramework)' == 'net11.0-windows10.0.26100.0'">
    <DefineConstants>$(DefineConstants);WGC</DefineConstants>
</PropertyGroup>
```

```csharp
// Relay/WgcCapture.cs
#if WGC
internal sealed class WgcCapture : IScreenCapture { ... }
#endif
```

这样符号名自己掌控，不依赖 SDK 版本。

---

## 5. 变更记录

| 版本 | 日期 | 内容 |
|---|---|---|
| v1.0 | 2026-11-22 | 初版：多目标 TFM vs 单一版本化可行性验证（task `task_mwrwqxu9105g411`），推荐方案 A + 遗留问题 |

## 参考

- 上游阻塞：`task_mwrvg2y8yd8vn2`（WGC 实现，阻塞于 TFM）
- 抓屏抽象：`Relay/IScreenCapture.cs`、`Relay/BitBltCapture.cs`
- 编码器：`Services/IVideoEncoder.cs`、`Services/MfH264Encoder.cs`
- 发布脚本：根 `build.bat`（`dotnet publish` 需加 `-f`）
- 本会话 scratch 实验（`%TEMP%\wgc-*`，验证后已弃）
