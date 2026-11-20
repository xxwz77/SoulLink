# SoulLink 交接文档 · 2.17.49（已绑定却显示「BGI 未绑定」）

> 时间：2026-11-21 03:36
> 版本：**2.17.49.0**（已替换 `dist\SoulLink.exe`）
> 性质：**修 bug**，用户实测反馈
> git：本文档提交之前的工作树

---

## 0. 一句话现状

启动台「正在监控以下挂机账号」的卡片里，**明明已绑定了 BGI 路径的 A2 / A3 都显示「BGI 未绑定」**。
根因是一行查表用错了 key —— 把界面展示名当成了 Windows 用户名。已修，并加了一层兜底。

| 用户描述 | 根因 | 修复 |
|---|---|---|
| 「为什么会显示没绑定，已经绑定了的」 | `GetByUser("A2 · 山客")` 永远返回 null → `bound=false` | 行里保存真实用户名 + 查表用真实名 + 查表函数兼容展示名 |

---

## 1. 根因

`Views/LaunchPage.xaml.cs` 的 `OverviewRow` 只有一个字段 `User`，而它是**展示名**：

```csharp
string shown = string.IsNullOrWhiteSpace(rm)
    ? s.UserName : (s.UserName + " · " + rm);     // → "A2 · 山客"
var row = new OverviewRow { User = shown, SessionId = s.Id };
```

十几行之后，同一个文件用它去查状态：

```csharp
var accountState = AccountStateCenter.GetByUser(row.User);   // ← 传的是 "A2 · 山客"
...
bool bound = accountState?.Bound == true;                     // null → false
string txt = !bound ? "BGI 未绑定" : ...;                      // ← 用户看到的
```

`GetByUser` 里是 `s.UserName == userName` 精确比较，`"A2" == "A2 · 山客"` 为假，
返回 `null`；调用方把 `null` 解释成"没有状态 = 没绑定"，于是**已绑定被写成未绑定**。

同文件里还有两处用 `row.User.Split('·')[0].Trim()` 临时救场（读今天日志、取快照），
这类"从展示串里反推 key"的写法本身就是隐患 —— 备注名里一旦带 `·` 就会切错。

### 实测（探针输出）

```
acc=A2  row.User='A2 · 山客'
    GetByUser(row.User)  => null          ← 旧实现，直接导致误判
    GetByUser(UserName)  => Bound=True
acc=A3  row.User='A3 · 玄月'
    GetByUser(row.User)  => null
    GetByUser(UserName)  => Bound=True
```

配置侧完全正常（`BgiPinnedUsers = A2,A3`，`BgiDataCenter` 对两个账号都能读到 9 / 6 个配置组，
`AccountState.Bound = True`）——**问题只在界面这一层**。

---

## 2. 修复

### 2.1 行结构里区分「展示名」和「真实名」

```csharp
private sealed class OverviewRow
{
    public string User = "";       // "A2 · 山客"，只用于显示
    public string UserName = "";   // "A2"，所有查表只用它
    ...
}
```

建行和每次刷新备注名时都同步 `UserName = vm.Session.UserName`（备注名改了不会污染它）。

### 2.2 三处取值改用 `UserName`

| 位置 | 原来 | 现在 |
|---|---|---|
| 查账号状态（bound 判定） | `GetByUser(row.User)` | `GetByUser(row.UserName)` |
| 读今天有没有日志 | `LoadBgiPath(row.User.Split('·')[0])` | `LoadBgiPath(row.UserName)` |
| 取日志快照 | `SnapshotByUser(row.User.Split('·')[0])` | `SnapshotByUser(row.UserName)` |

### 2.3 查表函数加兜底（防御，不是鼓励传展示名）

`AccountStateCenter.GetByUser` 精确匹配不到时，把入参按 `·` / 空格切出第一段再比一次：

```csharp
// "A2 · 山客" / "A2 山客" / "A2·山客" 都还原成 "A2"
internal static string BareName(string shown) { ... }
```

这样即使以后别处又传了展示名，也不会再被误判成"未绑定"。

---

## 3. 验证

新增 `tools/layout-check/BoundLookupCheck.cs`（可重复执行）：

```
已绑定账号 = A2/山客,A3/玄月
ok   A2 用真实用户名查到且 Bound=true
ok   展示名 'A2 · 山客' 也能命中（不再误判为未绑定）
ok   展示名 'A2 山客' 也能命中（不再误判为未绑定）
ok   展示名 'A2·山客' 也能命中（不再误判为未绑定）
ok   A3 用真实用户名查到且 Bound=true
ok   展示名 'A3 · 玄月' 也能命中（不再误判为未绑定）
ok   展示名 'A3 玄月' 也能命中（不再误判为未绑定）
ok   展示名 'A3·玄月' 也能命中（不再误判为未绑定）
ok   不存在的账号仍然返回 null
ALL PASSED
```

真实界面渲染验证（`OverviewRenderCheck`，喂入 A2/A3 会话后读卡片文本）：

```
行数 = 2
   user='A2 · 山客'  userName='A2'  line2='今日还未运行 BetterGI'
ok   行里保存了真实用户名（A2）
ok   已绑定账号不再显示「BGI 未绑定」
   user='A3 · 玄月'  userName='A3'  line2='今日还未运行 BetterGI'
ok   行里保存了真实用户名（A3）
ok   已绑定账号不再显示「BGI 未绑定」
ALL PASSED
```

修后第二行的文案是「今日还未运行 BetterGI」（灰）—— 这才是**已绑定但今天没跑**的正确说法；
未绑定的账号仍然显示琥珀色「BGI 未绑定」，两种状态不再混淆。

回归：`GroupHintCheck` / `FlowEditorCheck` / `PathPickerCheck` / `BgiDataCenterCheck` 全部 ALL PASSED。

---

## 4. 产物

| 项目 | 值 |
|---|---|
| 版本 | **2.17.49.0** |
| 成品 | `dist\SoulLink.exe` · 67,501,525 字节（已替换） |
| SHA256 | `15AC2143F9E5A66E8A016C284B24340C038F7FD0C5DD726DE43F96D21FF88957` |
| 旧版留档 | `dist\SoulLink-2.17.48-before-boundfix.exe` |
| 暂存 | `artifacts\dist-2.17.49\` |

冒烟测试：隔离身份启动 → `LaunchView activated` → `main window shown: True`，无 `error.log`。

---

## 5. 教训

### 5.1 展示名和主键必须分开存
界面行标题是"用户名 · 备注名"，但只要有人拿它当 key 去查表，就一定会出事。
**规则：实体里凡是既要有显示名又要能查表的，就把两个字段都存下来，别用 `Split` 反推。**

### 5.2 `null` 不等于 "false"
`accountState?.Bound == true` 把"查不到"和"未绑定"混成了一个结果。
**规则：查不到要能和"查到了但是否"区分开** —— 这里至少在文案上区分了三种状态
（未绑定 / 今天没跑 / 运行过已退出），但底层的 null 语义仍然值得后续再收紧。

### 5.3 截图里的"未绑定"其实配置是好的
排查时先查了配置（`BgiPinnedUsers`）和数据层（`BgiDataCenter` / `AccountState.Bound`），
两边都正常 —— 这直接把范围锁死在界面层，省掉了改数据层的时间。
**先分层验证，再改代码。**
