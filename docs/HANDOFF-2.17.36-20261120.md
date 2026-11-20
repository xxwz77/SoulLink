# SoulLink 交接文档 · 2.17.36（修饰键可单独录入 + Esc 不再被"取消"占用）

> 时间：2026-11-20 18:45
> 版本：**2.17.36.0**（待定版；可信归档仍是 2.17.19）
> 性质：按键捕获控件的两个交互修正
> 用户要求：**界面操作由用户自己测，助手不做界面测试**

---

## 1. 用户反馈

> 「shift ctrl 无法单独录入，esc 需要增加一个单独按钮，因为现在 esc 是退出。」

两个都是 2.17.35 那版的设计缺陷。

## 2. 缺陷一：修饰键无法单独录入

### 原因

原来的逻辑是"**按下主键才结束**"：

```csharp
string mod = ModName(ev.Key);
if (mod.Length > 0)
{
    pressedMods.Add(ev.Key);
    keyBox.Text = "请按下要发送的按键…（当前 CTRL）";   // 只是预览
    return;                                            // ← 永远不结束
}
```

只按 `CTRL` / `SHIFT` / `ALT` 时永远停在"预览"，**没有任何提交时机**，
于是单独的 SHIFT、CTRL 根本录不进去。

### 修复：`PreviewKeyUp` 提交

```csharp
keyBox.PreviewKeyUp += (_, ev) =>
{
    string mod = ModName(ev.Key);
    if (mod.Length == 0) return;
    if (sawMainKey) return;                        // 组合键已在 KeyDown 处理
    if (pressedMods.Count != 1) return;            // 同时按住多个修饰键不算
    if ((DateTime.Now - lastModDownAt).TotalMilliseconds < 30) return;  // 与组合键的快速按下区分
    node.Set(p.Key, mod);   // CTRL / SHIFT / ALT / WIN
    StopListen();
};
```

判定要点：

* `sawMainKey` 记录本次监听有无按过主键 —— 有就说明是组合键，KeyUp 不再处理；
* 只认**单个**修饰键，避免"按住 CTRL+SHIFT 再松开"被误录成 CTRL；
* 30ms 门槛：组合键通常是"按下修饰键后立刻按下主键"，
  此时 KeyUp 发生在 `sawMainKey` 之后本来就不会触发，
  这个门槛只用于兜住异常极快的场景。

## 3. 缺陷二：Esc 被"取消"占用了

原来 `PreviewKeyDown` 里：

```csharp
if (ev.Key == Key.Escape) { StopListen(); return; }   // ESC 只能取消
```

意味着**用户永远录不进 Esc**，而 Esc 恰恰是最常用的"关闭/返回"键。

### 修复：取消改由独立按钮

* 删掉 `if (ev.Key == Key.Escape) { StopListen(); }`，Esc 变成普通可录按键；
* 捕获框下方新增两个胶囊按钮：

```
[取消录入]  [清空]
```

* 提示文字同步改为「请按下要发送的按键…（可单按 CTRL/SHIFT/ALT，或按「取消」退出）」。

## 4. 验证（真实 WPF，逐字复刻实现）

四个场景程序化驱动：

```
单按 CTRL   → 'CTRL'      ✓
单按 SHIFT  → 'SHIFT'     ✓
按 ESC      → 'ESC'       ✓
CTRL+A      → 'CTRL+A'    ✓

RESULT: PASS — 修饰键可单独录入、ESC 可录入、组合键正常
```

## 5. 产物

| 项目 | 值 |
|---|---|
| 版本 | 2.17.36.0 |
| `dist/SoulLink.exe` | 67,483,134 字节 · SHA256 `BD4F63A6473EEF717BDFE5BA6C6F1FE119F758783B402884BD487D1E70BBB7EF` |
| 注入代理 | 未改动（仍是 2.17.21） |
| 可信归档 | 仍是 `v2.17.19-reliable` |

## 6. 待办

1. **UI 自适应**（用户多次提到）：小窗口卡片挤压、按钮点不到。仍未动。
2. 符号键补丁在代理里尚未生效（见 2.17.34 交接第 6 节）。
3. 2.17.20 ~ 2.17.36 均未做可信归档。
