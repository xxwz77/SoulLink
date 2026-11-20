# SoulLink 交接文档 · 2.17.23 / 2.17.24（状态灯四色语义 + 失败一次后永久卡死）

> 时间：2026-11-20 16:45
> 版本：**2.17.24.0**（待定版；可信归档仍是 2.17.19）
> 性质：用户指定状态灯四色语义；**顺带挖出并修掉一个真正的引擎 bug**
> 用户要求：**界面操作由用户自己测，助手不做界面测试**

---

## 1. 用户指定的四色语义

| 颜色 | 含义 |
|---|---|
| **蓝** | 等待 |
| **黄** | 暂停 |
| **绿** | 执行成功 |
| **红** | 执行失败 |

## 2. 2.17.23：状态灯按四色重写

原来灯只反映"触点实时为真"，且「仅管理挂机账号」开启时会无条件变灰（见 2.17.22 交接）。
现在改成按**最近一次真实执行结果**着色，判定顺序：暂停 > 有结果 > 无结果=等待。

### 2.1 新增持久化字段（`Models/FlowModels.cs`）

```csharp
public bool? LastRunOk { get; set; }          // 最近一次动作执行结果（null=还没执行过）
public string LastRunError { get; set; }      // 失败原因，成功时清空
public string LastRunAction { get; set; }     // 被执行的动作标题
public DateTime? LastActionResultAt { get; set; }
```

**为什么要新字段**：`RunCount` / `LastRunAt` 只在"整轮收尾"时才写，
失败路径什么都不留，界面根本无从判断上次是成功还是失败。

### 2.2 引擎在每次动作执行后落盘（`Services/PlcScanEngine.cs`）

动作执行完立刻写 `LastRunOk / LastRunError / LastRunAction / LastActionResultAt`。
配合 `checkpoint` 每步保存，界面 3 秒轮询即可看到颜色变化。

### 2.3 卡片显示（`Views/LaunchPage.xaml.cs`）

灯色与摘要严格对应：

```
黄 → 暂停中
红 → 执行失败「模拟按键」：<原因> · 16:37:00
绿 → 执行成功「模拟按键」 · 16:37:00
蓝 → 等待：条件满足时自动执行 / 等待：只连了「手动」触点…
```

「仅管理挂机账号」不再影响灯与摘要（它们是**控制状态**，不是挂机目标的运行数据）。

---

## 3. 2.17.24：真正的 bug —— 失败一次后永久卡死

### 3.1 现象

部署 2.17.23 后灯一直是蓝/黄，`LastRunOk` 始终为空，`RunCount` 也不再增长。

### 3.2 根因

`PlcScanEngine` 里动作的执行条件是：

```csharp
if (input && selected && (manual || !previous || !flow.ActionResults.ContainsKey(n.Id)))
```

失败结果写进 `ActionResults` 之后：

* `PlcPower` 一直是 true（触点一直为真）→ `previous = true`；
* `ActionResults` 里有那条失败 → `ContainsKey = true`；
* `manual = false`（自动扫描）→ **三个条件全假，动作再也不会执行**。

而清理失败结果的那一行只在 `!input || !selected` 时触发；
「定时·间隔」这类触点的瞬时信号有 2 秒保留窗口，间隔 ≤2 秒时**每轮都读到真**，
永远等不到"断开"的时机 → **永久卡死**。

### 3.3 实测证据（真实引擎 + 真实流程，连扫 8 轮）

修复前：

```
第1轮: Triggered=False  PlcPower=True  ActionResults=1  msg=动作失败；断开后可重试
第2轮: Triggered=False  PlcPower=True  ActionResults=1  msg=动作失败；断开后可重试
...（8 轮完全一致，一次都没执行）
```

### 3.4 修复

失败的结果**不阻挡**下一次执行（每轮重试直到成功）；成功结果仍然阻挡，严格走上沿。

```csharp
bool lastFailed = flow.ActionResults.TryGetValue(n.Id, out var lastRes) && !lastRes.Ok;
if (input && selected && (manual || lastFailed || !previous || !flow.ActionResults.ContainsKey(n.Id)))
```

修复后（同样的真实流程）：

```
第1轮: Triggered=True Passed=False LastRunOk=False msg=执行失败：模拟按键 · 目标账号没有会话
...（每轮都真正执行）
```

### 3.5 真机结果（2.17.24 运行 40 秒后）

| 流程 | 灯 | 结果 |
|---|---|---|
| db9328b9（手动，已暂停） | **黄** | 暂停中 |
| 38212a3a（间隔 2s） | **绿** | 执行成功「模拟按键」 |
| 6c071938（间隔 3s） | **绿** | 执行成功「模拟按键」 |

---

## 4. 本轮改动清单

| 文件 | 改动 |
|---|---|
| `Models/FlowModels.cs` | 新增 `LastRunOk / LastRunError / LastRunAction / LastActionResultAt` |
| `Services/PlcScanEngine.cs` | 执行后落盘结果；**失败结果不再阻挡重试** |
| `Views/LaunchPage.xaml.cs` | 四色灯 + 与灯色一致的摘要文案 |

## 5. 产物

| 项目 | 值 |
|---|---|
| 版本 | 2.17.24.0 |
| `dist/SoulLink.exe` | 67,477,418 字节 · SHA256 `535D00ADF206E8D6BA0B3221900DF52C57FD7220116A28F7B7B1C91C9601ABE0` |
| 注入代理 | 未改动（2.17.21） |
| 可信归档 | 仍是 `v2.17.19-reliable` |

## 6. 待办

1. **UI 自适应**（用户两次提到）：小窗口卡片挤压、按钮点不到。未动。
2. 新建流程默认连接「手动」触点，用户会以为自动执行坏了。未改默认值（需用户决定）。
3. 2.17.20 ~ 2.17.24 均未做可信归档。
