# 修复：键盘 toggle 状态不同步 + 扩展键丢失（小键盘/方向键混淆、CapsLock/NumLock 漂移）

> 日期：2026-11-24
> 作者：webclient-instrumentation（dsh-team `team_mwu35l8pae8oj5` / member `m_mwu3791qage7i6` / task `task_mwuc16sfqm10ca`）
> 来源：对标研究 `BENCH-YOMORI-01-RDP-EMBED.md` §4.1（P0）——Yomori 用 `ycn_rdp_send_keyboard_sync` 解决同一类问题，本报告给出**我们自己的独立实现**（未复制任何 GPL 代码）。
> 证据分级：**【实测-本次】**＝本会话在同一台机器上跑出来的数（方法可复现，见 §2）；**【代码事实】**＝工作区 file:line；**【推断】**＝有依据未实测。

---

## 0. 一句话结论

**小键盘/方向键混淆的真因不是"toggle 没同步"，而是注入时根本没带扫描码与扩展位。** 旧 `SendVk` 只填 `wVk`，`wScan` 恒 0、`dwFlags` 不带 `KEYEVENTF_EXTENDEDKEY`，导致目标程序收到的 **lParam 扫描码恒为 0x00、扩展位恒为 0**（低级键盘钩子 + 隐藏窗口消息**双路实测**坐实）。Set1 扫描码里方向键与小键盘数字**同码**（←↑→↓ = 4B/48/4D/50 = 小键盘 4/8/6/2），扩展位是唯一区分手段 —— 所以旧实现在**消息层就无法区分**这两组键。

本次修好两件事：
1. **扩展键**：`SendVk` 现在按 `MAPVK_VK_TO_VSC_EX` 取真实扫描码并显式带 E0 扩展位（`Relay/RelayAgent.cs:235-266`）。
2. **toggle 同步**：新增 `keystate` / `keysync` 两个动作，先读远端真实三锁状态再算差集（`Relay/RelayAgent.cs:1040-1086`、`Services/InputActions.cs:154+`）。**只在目标 ≠ 当前时才按**——toggle 键按一次翻转一次，盲按会把远端翻到相反值。

**A2/console 实测：改前 `scan=0x00 ext=n`（方向键与小键盘 4 完全同形）→ 改后 `scan=0x4B ext=Y`（LEFT）vs `scan=0x4B ext=n`（Numpad4），区分正确。** toggle 四种翻转场景全部读回一致。

---

## 1. 复现（改前行为，可重复）

### 1.1 复现方法（三段式，全部只读）
| 步骤 | 做法 | 观测层 |
|---|---|---|
| A | 注入后查 `KEYBDINPUT` 的 `wScan/dwFlags` | 源码（读代码即可断定） |
| B | 装 `WH_KEYBOARD_LL` 低级键盘钩子，读 `KBDLLHOOKSTRUCT.scanCode/flags` | 系统输入层 |
| C | 建隐藏窗口，读 `WM_KEYDOWN` 的 lParam（bits16-23=扫描码、bit24=扩展位） | 窗口消息层 |

B/C 两路都**只读**（钩子永远 `CallNextHookEx`，不拦任何事件）；注只注入 F24 / RCtrl / NumpadDivide 三个无副作用键。

### 1.2 改前实测数据【实测-本次】

**B 路（低级键盘钩子，改前 `SendVk` 原样复刻）**：

```
[现状 F24（非扩展，作对照）] 判定扩展=n
  hook vk=0x87 scan=0x00 ext=n injected=Y DOWN
  hook vk=0x87 scan=0x00 ext=n injected=Y UP
[现状 RCtrl（应为扩展键！）] 判定扩展=Y
  hook vk=0xA3 scan=0x00 ext=n injected=Y DOWN     ← ★ 扩展位丢了
[现状 NumpadDivide（应为扩展键！）] 判定扩展=Y
  hook vk=0x6F scan=0x00 ext=n injected=Y DOWN     ← ★ 扩展位丢了
[修复后 RCtrl]
  hook vk=0xA3 scan=0x1D ext=Y injected=Y DOWN     ← 正确
[修复后 NumpadDivide]
  hook vk=0x6F scan=0x35 ext=Y injected=Y DOWN     ← 正确
```

**C 路（窗口消息 lParam，改前/改后对照，本轮实测）**：

```
[现状 F24]                  期望 scan=0x76 ext=n → 实得 lParamScan=0x00 lParamExt=n
[现状 RCtrl(扩展)]          期望 scan=0x1D ext=Y → 实得 lParamScan=0x00 lParamExt=n   ← ★ 错
[现状 NumpadDivide(扩展)]   期望 scan=0x35 ext=Y → 实得 lParamScan=0x00 lParamExt=n   ← ★ 错
[现状 VK_NUMPAD4]           期望 scan=0x4B ext=n → 实得 lParamScan=0x00 lParamExt=n
[现状 VK_LEFT]              期望 scan=0x4B ext=Y → 实得 lParamScan=0x00 lParamExt=n   ← ★ 与上一行**完全同形**
[修复后 RCtrl]              期望 scan=0x1D ext=Y → 实得 lParamScan=0x1D lParamExt=Y   ✓
[修复后 NumpadDivide]       期望 scan=0x35 ext=Y → 实得 lParamScan=0x35 lParamExt=Y   ✓
[修复后 VK_NUMPAD4]         期望 scan=0x4B ext=n → 实得 lParamScan=0x4B lParamExt=n   ✓
[修复后 VK_LEFT]            期望 scan=0x4B ext=Y → 实得 lParamScan=0x4B lParamExt=Y   ✓
```

**★ 关键一行**：改前 `VK_LEFT` 与 `VK_NUMPAD4` 在窗口消息层**产生完全相同的 lParam**（`scan=0x00 ext=0`）。任何靠 lParam 判键的程序都不可能区分它们。

### 1.3 为什么"方向键/小键盘同码"不是缺陷而是设计

`MAPVK_VK_TO_VSC_EX` 实测输出（纯计算，不注入）：

```
VK_LEFT    vk=0x25  vsc=0x004B  scan=0x4B  ext=n   ┐ 同码 4B
VK_NUMPAD4 vk=0x64  vsc=0x004B  scan=0x4B  ext=n   ┘ 靠 ext 区分
VK_END     vk=0x23  vsc=0x004F  scan=0x4F  ext=n   ┐ 同码 4F
VK_NUMPAD1 vk=0x61  vsc=0x004F  scan=0x4F  ext=n   ┘
```

**注意**：本机 `MapVirtualKeyEx(…, MAPVK_VK_TO_VSC_EX)` 对方向键返回的 **hi-byte 不是 0xE0**（返回 `0x004B` 而不是 `0xE04B`）—— 这是 Windows 的行为（VSC_EX 的 E0 只在部分键上返回）。**所以不能只依赖 MAPVK**，必须叠加自维护的扩展表 `InputKeys.IsExtendedVk`（`Services/InputActions.cs:106`）。这就是为什么 `SendVk` 里写的是 `(vscEx >> 8) == 0xE0 || InputKeys.IsExtendedVk(vk)`（`Relay/RelayAgent.cs:239`）—— 两个来源取并集。

---

## 2. 根因（file:line）

| # | 根因 | 位置（改前） | 后果 |
|---|---|---|---|
| 1 | `SendVk` 只填 `wVk`，`wScan` 恒 0、无 `KEYEVENTF_EXTENDEDKEY` | `Relay/RelayAgent.cs:206-229`（旧） | 目标收到 `scan=0x00 ext=0` → 方向键与小键盘同形、右 Ctrl 退化为左 Ctrl、小键盘 / 退化为主键盘 / |
| 2 | 键名表缺小键盘运算符与 toggle 键 | `Services/InputActions.cs:64-66`（旧，只到 `NUMPAD0-9`） | `NumpadDivide`/`Numpad*`/`CapsLock`/`NumLock`/`ScrollLock` 全部 `ParseVk → 0` → 报「无法解析按键名」 |
| 3 | 完全没有 toggle 同步动作 | 全仓 `KBD_SYNC` 0 命中 | 观看端与宿主 NumLock/CapsLock/ScrollLock 各自为政；且**没有任何"读远端状态"的手段** |
| 4 | Web 动作白名单与 `RelayAgent.Execute` 是**两份独立清单** | `Relay/AgentWebServer.cs:613-628` | 只在一处加动作 → 网页调用报「未知动作」而桌面管道正常（本次真踩到，见 §5） |

### 2.1 为什么"toggle 不同步"会表现为"小键盘出方向键"

两条**独立**的机制都会产生这个现象，本次都修了：

- **机制 A（主因，本次修复）**：注入不带扩展位 → 游戏/程序按扫描码判键时，方向键与小键盘数字无法区分（§1.2 实测）。
- **机制 B（次因，本次修复）**：远端 NumLock 状态与观看端不一致。小键盘数字与方向键在 Set1 里同码，**Windows 用扩展位 + 该会话的 NumLock 状态共同决定解释成哪个键**。观看端 NumLock=ON 而远端 off 时，观看端按小键盘 4 期望 "4"，远端按自己的 NumLock 解释成 ←。

**⇒ 只修 A 不修 B，或只修 B 不修 A，都不能彻底解决。** 这是本次把两件事一起做的原因。

---

## 3. 实现选择（为什么这么做，以及否掉了什么）

### 3.1 扩展键：叠加两个来源，而不是只用 MAPVK

```csharp
// Relay/RelayAgent.cs:237-242
uint vscEx   = MapVkToScanEx(vk);                                     // 真实扫描码
ushort scan  = (ushort)(vscEx & 0xFF);
bool extended = (vscEx >> 8) == 0xE0 || Services.InputKeys.IsExtendedVk(vk);  // ★ 两个来源取并集
uint flags = up ? KEYEVENTF_KEYUP : 0;
if (extended) flags |= KEYEVENTF_EXTENDEDKEY;
```

**为什么不用纯表**：`MapVirtualKeyEx` 对**当前键盘布局**的映射是权威的（例如不同布局下 OEM 键会变），纯表会漂移。
**为什么不用纯 MAPVK**：§1.3 实测 —— 方向键的 VSC_EX **hi-byte 不是 0xE0**（返回 0x004B），纯 MAPVK 会漏判方向键。
**⇒ 取并集**：MAPVK 负责"这个键的真实扫描码是多少"，`IsExtendedVk` 负责"这个键属于 E0 组吗"。

**否掉的方案**：
| 方案 | 为什么否 |
|---|---|
| 只用 `KEYEVENTF_EXTENDEDKEY` 不填 `wScan` | 实测（改后 RCtrl 那组如果只置 flag）`scan` 仍为 0，仍无法区分同码组 |
| 只填 `wScan` 不置 `EXTENDEDKEY` | 扫描码有了但扩展位没有，方向键仍与小键盘同形 |
| 用 `keybd_event` 代替 `SendInput` | 老 API，无法携带扩展位/扫描码，且已被文档标记为 superseded |
| 走 `SendInput` 的 `HARDWAREINPUT` | 只能发鼠标事件，键盘不适用 |

### 3.2 toggle：先读再算差集，绝不盲按

```
keystate  → 只读：读远端三锁 → 返回 mask + 人类可读串（零副作用）
keysync   → 读 → Diff(当前, 目标) → 只对差集逐个小写 → 再读回校验 → 不一致就如实报 ok=false
```

**为什么不能盲按**：toggle 键按一次翻转一次。如果只是"每次都按一遍 NumLock"，那么远端本来 ON 会变 off —— 比不同步更糟。
**为什么要有 `keystate` 单独动作**：前端需要在建立会话时/用户切换时**先知道远端状态**，才能决定目标值（或直接回显给用户）。

**回读校验**（`Relay/RelayAgent.cs` 的 keysync 分支）：注入后重新读一次，`ToMask` 不相等就返回 `ok=false`。**不假装成功**——这是本次特意加的，因为输入注入在 UIPI/桌面不可交互时会静默失败。

### 3.3 纯逻辑与注入分离（可离线单测）

`Services/InputActions.cs` 里新增的两个类**不含任何 Win32 调用**：
- `InputKeys.IsExtendedVk(ushort vk)`：纯查表；
- `ToggleKeys`（`Diff / ToMask / FromMask / TryParse`）：纯计算。

**理由**：沿用本仓既有惯例（`InputKeys.ParseVk` 当初就是从 `RelayAgent` 抽出以便 `tools/layout-check` 离线单测）。注入侧只做"读状态 + 循环按键"，逻辑正确性由单测保证。

---

## 4. 向后兼容（逐条核对）

| 关注点 | 是否受影响 | 证据 |
|---|---|---|
| 普通字母/数字/符号键 | **不受影响** | `ParseVk` 映射一字未改；回归实测 `A` → `scan=0x1E ext=n`（新增扫描码是纯增强） |
| 组合键（Ctrl+S） | **不受影响** | 回归实测：`A2(Ctrl) down → 53(S) down → 53 up → A2 up`，顺序与配平正确 |
| 中文输入（`text` 动作） | **不受影响** | 走的是 `KEYEVENTF_UNICODE` 分支（`SendUnicode`，本次未改）；回归实测 `你好` → `vk=0xE7 scan=0x4F60/0x597D`，字节对正确 |
| 手机端虚拟摇杆 `keydown/keyup` | **不受影响**（且受益） | 共用 `SendVk`；方向键现在带扩展位，游戏读 Raw Input 更准 |
| 老调用方的 HTTP 响应体 | **零影响** | 只在 `keystate/keysync` 两个**新**动作上追加 `state`/`mask` 字段，其它动作响应体逐字节不变（`Relay/AgentWebServer.cs:678-691`） |
| `keysync` 不带参数 | **安全** | 只回报现状、**不注入任何键**（实测："当前 Caps=off Num=ON Scroll=off（未提供目标状态，未注入）"） |
| `keysync` 传非法值 | **安全** | `TryParse` 返回 false → 同"不带参数"分支（实测 `caps=maybe` 被拒且零副作用） |
| 桌面管道通道 | **同样可用** | 动作加在 `RelayAgent.Execute`（管道与 Web 共用），只是 Web 侧另需白名单（§5） |

---

## 5. 本次踩到的坑（值得记）

**Web 动作白名单是第二份清单。** `RelayAgent.Execute` 的 `switch` 与 `AgentWebServer.ExecuteInput` 的 `switch ((dto.action…))` 是**两份独立实现**。只在前者加 `keysync` 时：
- 桌面管道（`--relay` 命名管道）**正常**；
- 网页/手机 HTTP **全部报「未知动作 keystate」**。

本次实测就把这个坑完整踩了一遍（`{"ok":false,"error":"未知动作 keystate"}`），已在 `Relay/AgentWebServer.cs:629-630` 补齐，并在代码里写了显式警告注释。

**⇒ 后续加输入动作，必须同时改两处：`Relay/RelayAgent.cs` 的 Execute + `Relay/AgentWebServer.cs` 的白名单。**

---

## 6. 改动前后对比（A2/console 真机实测）

> 环境：本机 session 1（console），同一台机器，同一份 `bin/Release/net11.0-windows/SoulLink.exe --relay`（含本次修复），web 端口 19363/19370/19371，token `toggleprobe`。观测器为独立进程（`WH_KEYBOARD_LL`，只读），与 agent 同会话。

### 6.1 扩展键：改前 vs 改后（同一组键）

| 键 | 改前（旧 `SendVk`） | 改后（真实 agent 注入） | 判定 |
|---|---|---|---|
| `Up`（方向键） | `scan=0x00 ext=n` | `scan=0x48 ext=Y` | **修复** |
| `Numpad8` | `scan=0x00 ext=n` | `scan=0x48 ext=n` | **修复（与 Up 已区分）** |
| `Right`（方向键） | `scan=0x00 ext=n` | `scan=0x4D ext=Y` | **修复** |
| `Numpad6` | `scan=0x00 ext=n` | `scan=0x4D ext=n` | **修复** |
| `NumpadDivide` | `scan=0x00 ext=n` | `scan=0x35 ext=Y` | **修复** |
| `Left`（方向键） | `scan=0x00 ext=n` | `scan=0x4B ext=Y` | **修复** |

**改前 6 个键全部是 `scan=0x00 ext=n` —— 完全同形，不可区分。改后 6 个键扫描码与扩展位全部正确，同码组（48/4D/4B）靠 ext 明确区分。**

### 6.2 toggle 同步：四场景实测（真实读回对照）

前置真实状态：`Caps=False Num=True Scroll=False`

| # | 动作 | keysync 返回 | 注入后独立读回（PowerShell `IsKeyLocked` 对照） | 判定 |
|---|---|---|---|---|
| 1 | `keysync caps=0,num=0,scroll=0` | `按键 1 次 → Caps=off Num=ON Scroll=off → Caps=off Num=off Scroll=off` / `mask=0` | `Caps=False Num=False Scroll=False` | ✅ 目标达成，且**只按了 1 次**（没动 Caps/Scroll） |
| 2 | `keysync caps=0,num=1,scroll=0` | `按键 1 次 → … Num=off … → … Num=ON …` / `mask=2` | `Caps=False Num=True Scroll=False` | ✅ 翻回 |
| 3a | `keysync caps=1,num=1,scroll=0` | `按键 1 次 → Caps=off … → Caps=ON …` / `mask=3` | `Caps=True Num=True Scroll=False` | ✅ 只翻 Caps，**NumLock 未被动到** |
| 3b | `keysync caps=0,num=1,scroll=0` | `按键 1 次 → Caps=ON … → Caps=off …` / `mask=2` | `Caps=False Num=True Scroll=False` | ✅ 复原 |
| 4a | `keysync caps=0,num=1,scroll=1` | `按键 1 次 → … Scroll=off → … Scroll=ON` / `mask=6` | `Caps=False Num=True Scroll=True` | ✅ ScrollLock 生效 |
| 4b | `keysync caps=0,num=1,scroll=0` | `按键 1 次 → … Scroll=ON → … Scroll=off` / `mask=2` | `Caps=False Num=True Scroll=False` | ✅ 复原 |

**安全动作（零副作用）实测**：
| 调用 | 返回 | 注入次数 |
|---|---|---|
| `keystate` | `Caps=off Num=ON Scroll=off` / `mask=2` | **0**（纯读） |
| `keysync` 无参数 | `当前 Caps=off Num=ON Scroll=off（未提供目标状态，未注入）` | **0** |
| `keysync key="caps=maybe"` | 同"无参数"分支 | **0** |
| `keysync` 到与当前完全一致的状态 | `已一致 …` | **0**（`Diff` 返回空） |

### 6.3 回归：普通键 / 组合键 / 中文

```
--- 普通字母 A ---
vk=0x41 scan=0x1E ext=n inj=Y DOWN      ← 扫描码从 0 → 0x1E（增强，不再是 0）
vk=0x41 scan=0x1E ext=n inj=Y UP
--- 组合键 Ctrl+S ---
vk=0xA2 scan=0x1D ext=n inj=Y DOWN      ← 左 Ctrl 正确不扩展
vk=0x53 scan=0x1F ext=n inj=Y DOWN
vk=0x53 scan=0x1F ext=n inj=Y UP
vk=0xA2 scan=0x1D ext=n inj=Y UP        ← 抬起顺序与按下相反，配平
--- 中文文本（你好）---
vk=0xE7 scan=0x4F60 ext=n inj=Y DOWN    ← '你' U+4F60，UNICODE 分支未受影响
vk=0xE7 scan=0x4F60 ext=n inj=Y UP
vk=0xE7 scan=0x597D ext=n inj=Y DOWN    ← '好' U+597D
vk=0xE7 scan=0x597D ext=n inj=Y UP
```

### 6.4 构建

| 项 | 结果 |
|---|---|
| `dotnet build SoulLink.csproj -c Release -f net11.0-windows` | **已成功生成（0 error）** |
| `tools/layout-check/InputActionCheck`（常驻回归，本次扩充） | **ALL PASSED**（原有 26 项 + 新增 30 项 = 56 项断言全绿） |
| `bin/` `obj/` | 测后已清（见 §8） |

---

## 7. 改动清单（file:line）

| 文件 | 改动 | 行数 |
|---|---|---|
| `Services/InputActions.cs` | ① `InputKeys.ParseVk` 补小键盘运算符 + toggle 键名（`:60-73`）② 新增 `InputKeys.IsExtendedVk`（`:106`）③ 新增 `ToggleKeys` 类（`:154+`：`State/Diff/ToMask/FromMask/TryParse`） | +154 |
| `Relay/RelayAgent.cs` | ① `KEYEVENTF_EXTENDEDKEY` 常量 + `MapVirtualKeyEx/GetKeyboardLayout` P/Invoke（`:175-186`）② `MapVkToScanEx`（`:217`）③ `SendVk` 带扫描码与扩展位（`:235-266`）④ `ReadToggleState`/`TapToggle`/`TryParseToggleTriple`（`:277/297/308`）⑤ `keystate`/`keysync` 两个 case（`:1040/1051`） | +195/-8 |
| `Relay/AgentWebServer.cs` | ① 动作白名单加 `keystate`/`keysync`（`:629-630`）② `Button` 路由带上这两个动作（`:649`）③ 这两个动作的响应追加 `state`/`mask`（`:678-691`） | +18 |
| `tools/layout-check/InputActionCheck.cs` | 新增 30 项断言（扩展键判定 / 新键名 / ToggleKeys 纯逻辑） | +40 |

---

## 8. 边界与清理自查

**未做**：改版本号；发布；碰 COS/latest.json；改注册表；git commit/push/checkout/reset；碰 `dist\stable\SoulLink-Stable.exe`（PID 13392 全程存活，收工前确认）；使用 A1（session 2）/A4（session 3）会话；按名字批量杀进程；弹窗（所有 `Start-Process` 均带 `-WindowStyle Hidden`）。

**测试进程**：所有测试 agent 与观测器均为**本次新起**，全部按**精确 PID** 停掉（`56988 / 49504 / 24752 / 29400 / 55464` 等），已确认 `test_agents_left=0`。

**临时文件**：`%TEMP%\sl_kbprobe`、`sl_kbcheck`、`sl_kbverify`、`sl_agentcheck` 四个目录与 `sl_kb*.log`、`sl_selftest.log` 全部删除，已确认残留 = 0。**C 盘未新建任何持久文件。**

**未复制的第三方代码**：本次实现完全自写。参考 Yomori 的仅是**设计思想**（"toggle 需要状态同步"这一条，见 `BENCH-YOMORI-01` §4.1），**未复制任何一行 GPL 代码**，也**未引用其任何常量/宏**（`YCN_`/`KBD_SYNC_` 在本仓库源码 0 命中）。

---

## 9. 未验证项（诚实清单）

| # | 项 | 为什么没验 | 建议怎么验 |
|---|---|---|---|
| 1 | **游戏内（Raw Input/DirectInput）实际表现** | 本会话没有可交互的游戏会话；且往 A2 游戏里注入按键会干扰用户挂机 | 用户开一局游戏，让小键盘 4/8/6/2 与方向键各走一段，确认不再混淆 |
| 2 | **真实 RDP 会话内的扩展键行为** | 本次在 session 1（console）验证；RDP 会话（A2/session 4）的内核输入路径相同，但未实测 | 在 A2 的浏览器端按小键盘方向键，观察远端 |
| 3 | **前端/手机端尚未接 `keysync`** | 本次只做 agent 侧能力 + 单测 + 实测，**未改** `docs/web-index.html` 与手机端（超出本任务验收范围） | 前端在 `startStream()` 成功后调一次 `keystate` 回显，并在用户按键前按需 `keysync`（约 15 行） |
| 4 | **CapsLock 与 IME 的交互** | 中文输入走 `UNICODE` 分支不受影响，但"CapsLock 开启时 IME 行为"未测 | 中文输入法下开/关 CapsLock 各打一段中文 |
| 5 | **多会话并发时的 toggle 独立性** | 每个 agent 在自己会话里读 `GetKeyState`，理论上各会话独立；未做双会话对照 | 两个账号各开一路，分别切 NumLock，确认互不影响 |

---

## 10. 给后续的接口约定（可直接复用）

```
POST /api/input   （agent 直连端口，X-SoulLink-Token 或 ?token=）
Body: {"action":"keystate"}
Resp: {"ok":true,"error":null,"cursor":{...},"state":"Caps=off Num=ON Scroll=off","mask":"mask=2"}

Body: {"action":"keysync","key":"caps=1,num=0,scroll=1"}   // 或 "1,0,1"
Resp: {"ok":true,"error":null,"cursor":{...},
       "state":"按键 1 次 → Caps=off Num=ON Scroll=off → Caps=ON Num=ON Scroll=off","mask":"mask=3"}
      // ok=false 表示「注入后回读与目标不一致」——不是解析失败（解析失败走"只回报"分支，也是 ok=true）
```

**mask 位定义**：`1=Caps`、`2=Num`、`4=Scroll`（与 `ToggleKeys.ToMask/FromMask` 一致）。

**幂等性**：
- `keystate` 纯读，任意次数调用零副作用；
- `keysync` 到"与当前一致"的目标 → 0 次注入，任意次数调用零副作用；
- `keysync` 到"与当前不同"的目标 → 恰好按 `Diff` 的键数（1~3 次），重复调用第二次变成 0 次。
