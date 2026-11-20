# SoulLink 交接文档 · 2.17.20（手动运行只能跑一次 / 模拟按键不发出去）

> 时间：2026-11-20 16:10
> 版本：**2.17.20.0**（**待定版**；可信归档仍是 2.17.19）
> 性质：修 2.17.19 及其之前一直存在的「自动化 ▶ 手动运行只生效一次」缺陷
> 结论：根因坐实、已修、已实测（按键真的发到 A2 会话，代理回「按键完成」）

---

## 1. 用户看到的现象

在启动页点「lummy 自动化」卡片的 ▶（ToolTip「立即运行一次」），弹窗：

```
执行结果
动作失败；断开后可重试

手动: 满足，继续
```

远端（A2 会话）没有收到按键。

**关键点：这条「失败」不是本次点击产生的**——它是 02:28 那次失败的**旧结果**被反复展示。

## 2. 真实的时间线（来自 config.json 的流程状态）

| 时间 | 发生了什么 |
|---|---|
| 02:28:34 | 用户第一次手动运行，动作执行失败并写入结果：`该会话没有已连接的注入通道（请先投送）`（当时确实没有通道） |
| 之后每一次扫描 / 每一次点 ▶ | 都**没有再执行**动作，只是把 02:28 的失败原样报出来 |
| 15:47 之后 | RDP 修复 + Broker 启动，通道已经「就绪」，但点 ▶ 仍然只报旧失败 |

## 3. 根因：手动触点的瞬时锁存永远不会「断开」

`Services/PlcScanEngine.cs` 的 `ReadContacts`：

```csharp
if (!liveOnly && live && IsMomentary(n)) flow.SatisfiedContacts.Add(n.Id);
bool effective = !liveOnly && IsMomentary(n) && flow.SatisfiedContacts.Contains(n.Id) ? true : live;
```

`IsMomentary` 把 `TrigManual` 也算作瞬时信号。但：

* 日志/定时/间隔这类信号的"事件"发生在扫描**之外**，锁存是必要的；
* **手动的"事件"就是这一次手动扫描本身**，可它被写进 `SatisfiedContacts` 后
  **再没有任何时机被清掉**（`Disarm` 不清、无 END 的流程也不会走 `CompleteRun`）。

于是：

1. 手动触点恒为「满足」→ 动作节点 `input` 恒为 true；
2. 动作节点 `PlcPower` 恒为 true、`ActionResults` 一直留着上次结果；
3. `PlcScanEngine` 的执行条件 `(!previous || !ActionResults.ContainsKey(n.Id))` 永远为 false
   → **动作再也不会执行**；
4. 每轮扫描都只走到 `r.Message = "动作失败；断开后可重试"`（步骤只剩「手动：满足，继续」）。
   而那句「断开后可重试」恰恰做不到——手动触点永远不会断开。

## 4. 修复（两处，都在 `Services/PlcScanEngine.cs`）

1. **手动触发不参与跨扫描锁存**（`ReadContacts`）：
   `TrigManual` 的瞬时值只在**本次手动扫描**内有效，不写入 `SatisfiedContacts`。
   → 手动触点在下一次普通扫描立刻「断开」，动作节点的 `PlcPower`/`ActionResults` 随之复位。
2. **手动运行必须重新执行**（动作执行条件）：
   `(manual || !previous || !ActionResults.ContainsKey(n.Id))`
   → 用户点「立即运行一次」就真的执行一次；普通调度扫描仍严格走上升沿，不会重复执行。

## 5. 验证

### 5.1 无 UI 复现 / 回归（真实 `PlcScanEngine`）

用真实 DLL 构造 `手动 → 模拟按键` 两节点流程，接触器返回"手动仅在 manual 扫描为真"：

| 阶段 | 修复前 | 修复后 |
|---|---|---|
| 第一次手动（无通道，失败） | 执行 1 次，失败 | 执行 1 次，失败 |
| 之后 2 次普通扫描 | `动作失败；断开后可重试`，触点仍满足，状态不变 | `手动：不满足，停止`，`PlcPower=false`，失败结果被清 |
| 第二次手动（通道已就绪） | **没有执行**（只有 1 次 write） | **执行第 2 次**，成功 |
| 第三次手动 | 没有执行 | **执行第 3 次**，成功 |

### 5.2 真实客户端（2.17.20）

1. 关闭 2.17.19 客户端 → 替换 `dist/SoulLink.exe` → 启动 2.17.20；
2. 启动后 RDP 自动重连成功（山客/会话 4、玄月/会话 5），16:04:19 两条通道投送成功；
   旧的失败状态被自动清掉（`ActionResults={}`、`PlcPower=false`）；
3. 用 UIAutomation 点 ▶（按钮 ToolTip「立即运行一次」）三次 → 弹窗均为：

```
执行结果
本轮已完成，等待条件重新满足后再次执行

手动: 满足，继续
执行「模拟按键」：按键完成
无 END：清空本轮状态并重新获取条件
```

4. `config.json` 里流程 `RunCount` 从 0 → 3、`LastRunAt=16:05:31`；
5. 动作返回「按键完成」来自**目标会话里的注入代理**（`RelayAgent` 的 `case "key"`
   调 `SendKeyCombo` 成功后回 Ok）→ 按键确实在 A2 会话里被注入。

证据图：`docs/flow-run-dialog-2.17.20.png`（弹窗）、`docs/flow-manual-run-2.17.20.png`（主界面）。

## 6. 产物

| 项目 | 值 |
|---|---|
| 版本 | 2.17.20.0（`SoulLink.csproj` 已升） |
| `dist/SoulLink.exe` | 67,476,450 字节 · SHA256 `B8FF54A1D1BD8E3AACF38D2A99974026502F5799005A81FA64ACB9B0DF0230A0` |
| 上一版备份 | `dist/SoulLink-2.17.19.exe`（可信版，同归档哈希） |
| 可信归档 | 仍是 `E:\SoulLink-SOURCE-ARCHIVE\v2.17.19-reliable\`（2.17.20 **尚未归档**） |
| 挂机版 | `dist/stable` 未改动 |

## 7. 备注 / 待确认

1. **2.17.20 未做可信归档**：如需把它也定版，按 `v2.17.19-reliable` 同样结构再出一份即可。
2. 「模拟按键」是**目标会话内的注入代理**（`RelayAgent`）用 `SendInput` 发到该会话的前台窗口；
   和 RDP 原生键盘是两条独立通路。代理回「按键完成」= 注入成功，但**是否被游戏接收**
   取决于目标会话当时的前台窗口。
3. 本次只改了 `Services/PlcScanEngine.cs`，**没有碰** 2.17.18 遗留的窗口内侧白带问题。
