# SoulLink 交接文档 · 2.17.34（模拟按键改为"按键捕获"录入）

> 时间：2026-11-20 18:15
> 版本：**2.17.34.0**（待定版；可信归档仍是 2.17.19）
> 性质：模拟按键节点不再让用户手打字面量，改为按下真实按键录入
> 用户要求：**界面操作由用户自己测，助手不做界面测试**

---

## 1. 用户要求

> 「按键录入不应该是文本框，用户按什么键就是什么键，也可以组合键录入，这样才对。」

原来 `ActKeyPress` 的 `keys` 是普通 `TextBox`，用户必须**知道格式**
（`F12` / `CTRL+A` / `NUMPAD3`）并手打，拼错一个字母就静默失败。
这不是"录入"，是"考记忆"。

## 2. 改动

### 2.1 新增参数类型 `FlowParamKind.KeyCapture`（`Models/FlowNodeCatalog.cs`）

```csharp
KeyCapture = 10
```

`ActKeyPress` 的 `keys` 参数从 `Text` 改成 `KeyCapture`，
描述改为「点一下输入框，然后直接按下要发送的按键（支持组合键）」。

### 2.2 编辑器里的捕获控件（`Views/FlowEditorWindow.xaml.cs`）

一个只读观感的 `Border`（不是 TextBox）：

| 状态 | 显示 |
|---|---|
| 空 | 灰字「点击这里，然后按下按键（可组合键）」 |
| 监听中 | 青字「请按下要发送的按键…（Esc 取消）」，边框变青 |
| 已录入 | 白字显示 `CTRL+A`，边框变绿 |

交互细节：

* 点击 → 进入监听，**吞掉所有按键**（`PreviewKeyDown` 里 `ev.Handled = true`），防止画布响应；
* 只按修饰键时实时预览「当前 CTRL」，不着急结束；
* 按下主键 → 把 `已按修饰键 + 主键` 拼成 `CTRL+ALT+X` 写回节点并结束；
* **Esc** 取消、**Backspace** 清空；
* 失去焦点自动退出监听（避免"以为在录、其实已经退出"）。

### 2.3 键名映射 `KeyNameOf`（与发送端同一套词表）

```
A-Z / 0-9 / NUMPAD0-9 / F1-F24
ENTER ESC SPACE TAB BACKSPACE DELETE INSERT HOME END PAGEUP PAGEDOWN UP DOWN LEFT RIGHT
- = , . / ; ' [ ] \ `
```

## 3. 顺带堵死一个静默失败

编辑器会产出 `-` `=` `/` 这些**字符**，但
`RelayAgent.ParseVk` 原来只认字母数字/F 键/修饰键 —— 符号键一个都没有。
结果是：**界面录进去了、发送端返回 0，静默不发**（最坑的失败形态）。

已在 `ParseVk` 补齐 11 个符号键的 `VK_OEM_*` 映射。

## 4. 验证（无 UI）

编辑器产出 39 个按键名 → **发送端全部可解析（39/39，失败 0）**：

```
可解析=39  失败=0

组合键拼装：
  1              → 0x31            ✓
  F12            → 0x7B            ✓
  CTRL+A         → 0x11,0x41       ✓
  CTRL+ALT+DELETE→ 0x11,0x12,0x2E  ✓
  SHIFT+F5       → 0x10,0x74       ✓
  CTRL+-         → 0x11,0xBD       ✓
```

参数类型确认：
```
param user  kind=UserPicker
param keys  kind=KeyCapture   ← 已改
param times kind=Number
```

真机部署后流程照常执行（38212a3a 成功 583 / 6c071938 成功 424 / d68f8069 成功 1，失败均 0），
`error.log` 无新增。

## 5. 产物

| 项目 | 值 |
|---|---|
| 版本 | 2.17.34.0 |
| `dist/SoulLink.exe` | 67,482,250 字节 · SHA256 `4A5406DEB86DFB70F3EA0A59A73280EAB6F022F953B3D9D64F92FEB04625718B` |
| 注入代理 | **未改动**（仍是 2.17.21）→ 见下方注意 |
| 可信归档 | 仍是 `v2.17.19-reliable` |

## 6. ⚠ 注意：按键发送在代理侧，本次改了代理代码

`ParseVk` 在 `RelayAgent` 里，属于**注入代理**（`C:\Program Files\SoulLinkDev\SoulLink.Agent.exe`）。
本次只换了 `dist\SoulLink.exe`，**代理仍是 2.17.21**，
所以符号键（`-` `=` `/` 等）的补丁**尚未生效**。

字母/数字/F 键/修饰键原本就支持，不受影响。
若用户需要录符号键，必须先更新安装目录的代理（流程见 2.17.21 交接第 5 节）：
停服务 → 换 `SoulLink.exe`/`SoulLink.Agent.exe` → `MakeAsInvoker` → 启服务 → 重启客户端。

## 7. 待办

1. **UI 自适应**（用户多次提到）：小窗口卡片挤压、按钮点不到。仍未动。
2. 代理未升级（见第 6 节）。
3. 2.17.20 ~ 2.17.34 均未做可信归档。
