# T25 松手不出帧：inputTrigger 被静止判定挡掉 + 两个前端 bug（2026-11-24）

> 任务：T25（dsh-team `soullink-p0-fix-yomori` / member `yomori-scout` / attempt `a668156a-f094-401e-98a8-aaf8b828ccb9`）

---

## 0. 结论

根因**与任务描述完全一致**，而且实际有 **三道** 闸门（不是两道）：

| # | 位置 | 原代码 | 问题 |
|---|---|---|---|
| 1 | `SessionRecorder.cs:774` | `if (!forceKeyFrame && !inputTrigger && !WaitUntilNextSlot(...))` | ✅ 这道**有** inputTrigger，只负责「进循环」 |
| 2 | `:804` | `if (forceKeyFrame || zChanged)` | ❌ 零拷贝分支漏了 inputTrigger |
| 3 | `:985` | `if (!forceKeyFrame && !desktopChanged && !keepaliveDue)` | ❌ CPU 分支漏了 —— **这是最隐蔽的一道** |
| 4 | `:930` | `if (bgra == null) { ... IdleWait(); continue; }` | ❌ 抓屏空转时**把 inputTrigger 白白吞掉** |

**因果链**：
```
用户松手 → NotifyInputActivity(urgent=true) 置 _inputActivityPending = true
  → :771 消费为 inputTrigger = true，:772 清空标志
  → :774 第一道闸门放行（能进循环）✓
  → 但 :985 第二道闸门按 desktopChanged 判：松手瞬间图标已就位、桌面静止
     → desktopChanged = false，keepaliveDue = false
     → **continue，inputTrigger 被丢弃**，一帧都没出
  → 前端只能等保活节拍（250ms）或下一次输入
  → 用户看到：「松手后两三秒才落位」「点击能触发、松开不行」
```

---

## 1. 关键设计问答（任务要求回答）

### Q：光标移动算不算「桌面变化」？

**不算，而且这正是本题的核心。**

| 事实 | 依据 |
|---|---|
| DXGI DDA 的 `FrameSequence` **在只有光标移动时不递增** | 光标是合成层，不属于桌面内容；`:792-793` 注释明确写「WGC 靠 FrameSequence 判定静止」 |
| 拖动时之所以流畅 | **图标真的在移动** → 桌面内容变化 → `FrameSequence` 递增 → `desktopChanged=true` → 正常出帧 |
| 松手那一瞬间 | 图标已就位 → **桌面静止** → `FrameSequence` 不增 → 被闸门挡住 → **一帧不出** |

**⇒ 输入活动必须能强制产出一帧，不能依赖 `FrameSequence`。** 这就是本次改动的设计依据。

---

## 2. 修复（后端三道 + 前端两个 bug）

### 2.1 后端：三处闸门（`Relay/SessionRecorder.cs`）

**① 零拷贝分支（:804）**
```csharp
bool zChanged = previousZeroSeq < 0 || curSeq != previousZeroSeq;
if (forceKeyFrame || zChanged || inputTrigger)   // ← 加 inputTrigger
```

**② CPU 分支静止跳过（:985）—— 最关键**
```csharp
if (!forceKeyFrame && !desktopChanged && !keepaliveDue && !inputTrigger)   // ← 加 !inputTrigger
{
    _skippedStaticFrames++;
    IdleWait();
    continue;
}
```

**③ 抓屏空转时重新武装（:934）**
```csharp
if (bgra == null || width <= 0 || height <= 0)
{
    if (forceKeyFrame) lock (Gate) { _forceKeyFrame = true; }   // 既有
    if (inputTrigger) _inputActivityPending = true;             // ← 新增，同法不吞
    IdleWait();
    continue;
}
```
第 ③ 条是任务要求的「逐行走一遍所有 `continue`」。**实际有 5 个 `continue`，逐个判定如下**：

| continue | 位置 | 是否会吞 inputTrigger | 处置 |
|---|---|---|---|
| 第一道闸门 | `:776` | 不会（此时 inputTrigger 必为 false 才走） | 不动 |
| `zeroHandled` | `:914` | 不会 —— 已成功产出 NAL | 不动 |
| `bgra == null` | `:930` | **会** —— 抓屏没拿到图就 continue | **已修（重新武装）** |
| 静止跳过 | `:983` | **会** —— 本次主修点 | **已修** |
| viewer.Closed | `:837/:1064` | 无关（内层循环，不是主循环的 continue） | 不动 |

### 2.2 前端 BUG-1：`preventDefault` 吞掉浏览器快捷键

| 位置 | 改动 |
|---|---|
| `docs/web-index.html:2401` 附近 | 加 `localShortcuts` 白名单判断，命中则 `return`（不注入、不 preventDefault） |

**放行清单**：`F5` / `F12` / `Ctrl+R` / `Ctrl+Shift+R` / `Ctrl+Shift+I` / `Ctrl+W` / `Ctrl+T` / `Ctrl+I`。
**只按修饰键**（Ctrl/Alt/Shift/Meta）时也不拦截（原本已 return）。

### 2.3 前端 BUG-2：`decoder.decode(...).catch` 在 closed 态炸 TypeError

| 位置 | 改动 |
|---|---|
| `docs/web-index.html:1455` | `const decP = decoder.decode(...)`；判空后再 `decP.catch(...)` |

**根因**：decoder 处于 `closed`/`error` 态时 `decode()` 返回 **undefined**，
于是 `undefined.catch` 抛 `Cannot read properties of undefined (reading 'catch')`。
**这个报错每帧都在刷** —— 既污染 console，也说明解码链在异常态空转，会掩盖真问题。

**改法**：
```javascript
const decP = decoder.decode(chunk);
if (decP && typeof decP.catch === 'function') { decP.catch(...); }
else if (!decP) { /* 计数 + 限频日志，不刷屏 */ }
```

---

## 3. 绝对不许破坏的三条（逐条核验）

### ① 静止时不编码的省 CPU 语义 —— **未破坏**

**证据（机制层）**：
```
:771  bool inputTrigger = _inputActivityPending;
:772  _inputActivityPending = false;        // 一次性标志，消费即清空
```
⇒ 放行只影响**当前这一拍**；下一拍 `inputTrigger` 必为 false，静止时仍走 `_skippedStaticFrames++` 路径。
**不会**让静止画面持续编码。

### ② 保活节拍（250ms）—— **未动**
`:93  private static readonly int KeepaliveIntervalMs = ReadKeepaliveMs();` 原样；`:1097` 的 `_keepaliveNextAt` 计算原样。
129

### ③ 按需 IDR（forceKeyFrame）—— **未动**
`ConsumeKeyFrameRequest()` / `RequestKeyFrame()` / 编码器重建逻辑全部原样；只是在放行条件里**并列**加了 `inputTrigger`。

---

## 4. 验证：**能做的做了，做不到的如实说**

### 4.1 已完成的验证

| 项 | 结果 |
|---|---|
| `dotnet build -c Release` | **0 error** |
| 三处闸门代码核验 | 全部含 `inputTrigger`（L804 / L934 / L985） |
| 静止省 CPU 路径仍在 | `_skippedStaticFrames++` 保留（L987） |
| 保活 / IDR 未动 | 已核（见 §3） |
| 前端修正在三处同步 | `docs/web-index.html` / `Services/WebPages.cs` / `dist/web-index.html` 均含 `localShortcuts`(2 处) 与 `const decP` |

### 4.2 ⚠️ 没能拿到「松手→新帧」的实测数字（如实说明）

**原因**：
1. 本任务明确要求**不主动发输入**（会操控用户的鼠标键盘）—— 我没有发任何 move/up/click/key；
2. A2 agent（PID 10088）的日志写在 **A2 自己的 `%TEMP%`** 里（`RelayAgent.cs:710`），
   我以 Lummy 身份**读不到** A2 会话的 TEMP，这是权限隔离，属预期；
3. 要我测就必须自己造输入 —— 与红线冲突。

**所以本任务的 1/2/3 三组数字我没有，也不编。**

**替代证据（代码级，可复核）**：
· 修改前的行为是**确定性的**：松手时 `desktopChanged=false` + `keepaliveDue=false` → `continue`，
  该路径在 :985 明确 `continue`，**必然**一帧不出。这不是概率问题，是代码路径问题；
· 修改后同一路径多了 `!inputTrigger` 条件，`inputTrigger=true` 时**必然不会** continue。

**建议的验收方式（由用户手动，我只读日志）**：
1. 让用户拖动图标后松手；
2. 我从 agent 日志取 `[video] ... inputTrigger=M` 这一行的增量（每 30 帧打一行）；
3. 或让用户在前端 console 里看 `pointerup` 与下一个 `requestVideoFrameCallback` 的时间差。

---

## 5. 边界自查

改了 2 个源文件：`Relay/SessionRecorder.cs`（三处闸门）、`docs/web-index.html`（两个前端 bug）
+ 自动生成的 `Services/WebPages.cs` / `dist/web-index.html`（由 `scripts/sync-webpages.ps1` 生成）。
**未主动发送任何输入类请求**（未碰 move/down/up/click/key/wheel）；未对 19080/19081 发任何请求；
未替换 Program Files 的共享 agent（A1/A4/A2 共用，属红线）；未碰 `dist\stable`；未发布、未碰 COS；未 git commit；未弹窗；未用 A1/A4。

---

**文档作者**：yomori-scout
**最后更新**：2026-11-24