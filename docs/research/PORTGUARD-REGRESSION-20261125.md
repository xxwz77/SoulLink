# 端口守卫回归测试（t41）—— 防止「内部端口只试绑回环」静默复发

> 日期：2026-11-25
> 工程：`tools/portguard-check/`（PortGuardCheck.csproj + PortGuardCheck.cs + Directory.Build.props）
> 接入：已登记进 `scripts/run-assertions.ps1`（快闸门 Compile-only 不跑它，因为它是 ProjectReference 工程，需 `-Full`；这是正确分类）

## 一、为什么必须有这个测试（因果链）

**故障现象**：用户输正确口令也进不去任何页面，直连回环 19081 也 401。

**根因**（t36 修复，2026-11-25）：
1. 主程序内部回环端口起点曾是 `WebPort+1`；
2. `WebDirectToAgent=true` 时代理按 `WebPort+账号序号+1` 绑定，序号 0 恰好也是 `WebPort+1`（= 19081）；
3. **Windows 允许 `0.0.0.0:19081` 与 `127.0.0.1:19081` 共存**（本测试实测复现：通配 HttpListener 已监听时，回环 HttpListener 仍能 `Start()` 成功）；
4. 代理先绑通配（成功）→ 主程序后绑回环（也成功）→ **实际请求被通配那条接走** → 应答「口令错误」还带 Agent 的响应头。

**为什么表现难以联想**：401（口令错误）与端口冲突毫无直觉关联。一旦判定被改回「只试绑回环」，bug 会**静默复发**，排查又得从 401 反推回端口——没有测试守门几乎必然复发。

## 二、修复的两道（本测试验证它，不改它）

- ① 内部端口起点 = `WebPort+62`，越过代理保留段 `[WebPort+1, WebPort+61]`（`WebDirectReservedSpan=61`）；
- ② 候选判定用 `IsPortFreeEverywhere` =「整机无任何监听者（IPv4+IPv6）」**且**「能绑回环」，而不是只试绑回环。

## 三、断言清单（4 条）

| # | 断言 | 验证内容 |
|---|---|---|
| 1 | `ResolveInternalPort(19080)` 选中端口不在 `[19081, 19141]` | 起点越过代理保留段 |
| 2 | 人为通配占用候选端口后，选择逻辑跳过它（改选下一个） | 候选判定确实查「整机监听」 |
| 3 | 通配占用但回环可绑 → `IsPortFreeEverywhere` 正确返回 false | **本 bug 核心**：只试绑回环会被骗 |
| 4 | 连续两次选择返回同一端口 | 幂等/稳定 |

## 四、接入方式

- 断言工程在 `tools/portguard-check/`，`Directory.Build.props` 设 `BaseOutputPath=bin$(MSBuildProjectName)`（与 layout-check 一致）。
- `scripts/run-assertions.ps1` 已改为扫两个目录（`tools/layout-check` + `tools/portguard-check`），并把这个目录的 `bin*` 路径加入测试进程清理白名单。
- 跑法：`pwsh -File scripts/run-assertions.ps1 -Full -Filter "*PortGuardCheck*"`（它是 ProjectReference 工程，只在 -Full 全量里跑）。

## 五、反向对照证明（测试真能抓 bug）

把 `IsPortFreeEverywhere` 临时改回「只试绑回环」的副本上跑，**断言 3c 正确 FAIL**（exit 1）：

```
resolved=19142
CanBindLoopback=True IsPortFreeEverywhere(buggy)=True
FAIL 断言3c: IsPortFreeEverywhere 应拒绝被通配占用的端口（buggy 会错误返回 True）
FAIL:1
```

即：回环可绑（`CanBindLoopback=True`）时，buggy 判定错误地把被通配占用的端口当成可用（`=True`），
测试立刻抓住这个错误并 FAIL。
