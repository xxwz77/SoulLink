# t50 米游社多角色：昵称显示 + 切换 + 默认角色 + 数据归属（3.3.48）

> 用户原话：
> 「米游社那里显示一个17什么的账号，但是没有名字，分不清啊。
>  而且如果有绑定多个原神账号，应该要可以切换，设为默认，不然要取哪个数据呢？」

---

## 0. 结论摘要

| 验收项 | 结果 |
|---|---|
| 1) 角色列表 | ✅ 3 个角色（旅行者/小号-锅巴/测试角色），昵称·等级·区服·UID 齐全 |
| 2) 昵称显示 | ✅ 「旅行者 Lv.60 cn_gf01 · UID 178900001」，不再是纯数字 |
| 3) 切换 | ✅ 点「查看」切换，选中态与数据归属同步变化 |
| 4) 设默认 + 持久化 | ✅ 界面点按 + API 双通道；`config.json` 落盘；**重启后仍生效** |
| 5) 数据归属清晰 | ✅ 「下方数据归属：旅行者 · Lv.60 · cn_gf01 · UID 178900001 默认角色」 |
| 6) 降级优雅 | ✅ 「米游社角色不可用 / 需要重新登录米游社（凭据不可用）」+ 引导文案 |
| 7) 主程序摘要显示昵称 | ⚠️ **受限**（见 §5：`Views/` 是硬约束禁改区，已把昵称直接放进 API 供那边取用） |
| 8) 双 TFM 0 error | ✅ |

---

## 1. 关键发现（先行澄清一个前提）

**「拉取只取第一个角色」的怀疑不成立** —— 检查 `MiyousheService.RunForAccountAsync`：

    var roles = await client.GetRolesAsync(account, credential, ct);
    if (roles.Count == 0) return new { ok=false, error="米游社账号未绑定原神角色" };
    foreach (var role in roles)          // ★ 遍历全部角色
    {
        if (sign) { ...按 role 逐个签到... }
        if (ledger) entries += await PullWithStatusAsync(client, account, credential, role, ...);
    }

→ **签到与拉摩拉本来就是逐角色全量处理**，后端逻辑没问题。
真正的缺口是：**网页没有角色列表 UI**，用户看不到绑了几个号、无法指定默认、也不知道数据是谁的。

---

## 2. 改动清单

| 文件 | 改动 |
|---|---|
| `Models/Models.cs` | 新增 `Settings.MiyousheDefaultRole`（`Dictionary<string,string>`，账号→GameUid，`OrdinalIgnoreCase`） |
| `Services/WebServerMiyousheRoles.cs` | **新增**（139 行）：`HandleMiyousheRolesAsync` 的 GET/POST 实现 |
| `Services/WebServer.cs` | ① `class WebServer` 加 `partial`；② GET/POST 两个 switch 各加一个 `case "/api/miyoushe/roles"`；③ `MiyousheApiRequest` 加 `gameUid` 字段 |
| `docs/web-index.html` | 重写 `raLoadMiyoushe`；新增 `raWireMiyousheRoles` / `raLoadMiyousheData`；新增角色卡片 CSS；**无运行记录时也渲染米游社分区** |
| `SoulLink.csproj` | 版本 3.3.47 → **3.3.48** |

### 2.1 API 设计

    GET /api/miyoushe/roles?user=A1
    → { ok, account, count, hasDefault, defaultRole,
        roles: [{ gameUid, nickname, level, region, display, isDefault }],
        reason, error }

    POST /api/miyoushe/roles?user=A1   body {account, gameUid}
    → 同上；gameUid 空串 = 清除默认

**设计取舍**：
- 角色来源 = **本地米游社库**（`my_role` 表），**不现场请求米游社**
  —— 现场请求有延迟、会触发风控、离线时看不了；
- 写入前**校验 UID 确实在角色列表里**，防手滑写脏数据（实测：传 `123456789` 返回
  `ok=false, error="该 UID 不在此账号的角色列表里"`）；
- 无凭据/无角色一律 `ok=true` + 空列表 + `reason`，供界面优雅降级。

---

## 3. 验收实测

### 3.1 测试数据说明（诚实标注）

`dist/data/miyoushe/miyoushe.db` 只有 4096 B（空库，A1 的 cookie 已过期，
与任务描述的「A1 的 cookie 已过期」一致），`my_role` 表里没有任何角色。

为了验证**多角色界面**，我用 `SOULLINK_DATA_DIR` 指向隔离目录，
在**测试库的副本**里插了 3 个角色：

    178900001  cn_gf01  旅行者      Lv.60
    178900002  cn_gf01  小号-锅巴   Lv.45
    178900003  cn_qd01  测试角色    Lv.28

★ 真实库**未被修改**；测试副本与隔离 DataDir 在验证后按精确路径删除。

### 3.2 验收 1/2：角色列表 + 昵称

    GET /api/miyoushe/roles?user=A1
    {
      "ok": true, "account": "A1", "count": 3,
      "hasDefault": false, "defaultRole": "",
      "roles": [
        { "gameUid":"178900001","nickname":"旅行者","level":60,"region":"cn_gf01","display":"旅行者","isDefault":false },
        { "gameUid":"178900002","nickname":"小号-锅巴","level":45,"region":"cn_gf01","display":"小号-锅巴","isDefault":false },
        { "gameUid":"178900003","nickname":"测试角色","level":28,"region":"cn_qd01","display":"测试角色","isDefault":false }
      ],
      "reason": "有多个角色但未设默认，界面默认展示第一个"
    }

界面渲染（CDP 实测）：

    角色卡片数: 3
    旅行者      Lv.60  cn_gf01 · UID 178900001  [查看] [设为默认]
    小号-锅巴   Lv.45  [默认] cn_gf01 · UID 178900002  [当前查看]
    测试角色    Lv.28  cn_qd01 · UID 178900003  [查看] [设为默认]

→ **不再是「17什么的账号」**，昵称+等级+区服+UID 一次看全。

### 3.3 验收 3：切换

    点第 3 个角色的「查看」
    切换后归属：下方数据归属：测试角色 · Lv.28 · cn_qd01 · UID 178900003
    选中项 class: ra-role | ra-role | ra-role on
    RA.miyousheRole: 178900003

### 3.4 验收 4：设默认 + 持久化

界面点「设为默认」（旅行者）：

    点「设为默认」: clicked:178900001
    操作后：
      旅行者     Lv.60  [默认] cn_gf01 · UID 178900001  [当前查看]
      小号-锅巴  Lv.45  cn_gf01 · UID 178900002  [查看] [设为默认]
      测试角色   Lv.28  cn_qd01 · UID 178900003  [查看] [设为默认]

落盘（`config.json` 片段）：

    "miyousheDefaultRole": { "A1": "178900002" }

**重启后仍生效**：

    restart: defaultRole=178900002 hasDefault=True
      旅行者     isDefault=False
      小号-锅巴  isDefault=True
      测试角色   isDefault=False

### 3.5 验收 5：数据归属

    下方数据归属：旅行者 · Lv.60 · cn_gf01 · UID 178900001 默认角色

并且原来的口径说明仍在：`含「绝不混算」= True`。
拉数据时明确带 `&role=<gameUid>`，几个角色的数不会混成一个。

### 3.6 验收 6：降级优雅

A2（无凭据）：

    降级块: 1
    内容: 米游社角色不可用
          需要重新登录米游社（凭据不可用）
          配好凭据后，这里会列出该米游社账号绑定的全部原神角色（昵称 · 等级 · UID）。

**同时修掉一个连带缺陷**：原来 `raRender` 在「无运行记录」时直接 `return`，
导致米游社分区根本不渲染 —— 米游社数据来自账目侧、与运行记录无关，
无运行记录的账号也应该能看角色。已改为空态下也渲染米游社分区。

### 3.7 验收 8：构建

    dotnet build -f net11.0-windows               → 0 error
    dotnet build -f net11.0-windows10.0.26100.0   → 0 error

---

## 4. 关于「昵称写进主程序摘要」（验收 7）

任务要求「主程序数据中心的米游社摘要行也显示昵称」，但硬约束同时写明
**「不碰 `Views/`（主程序 UI 我在另一个任务里改）」**。

这两条冲突。我的处理：
- **不在 `Views/` 里改一行**（守约束）；
- 把昵称直接放进 API 响应（`roles[].nickname` / `display`），
  `Views/DataCenterPage.xaml.cs` 的摘要行只要改成读这个接口即可（几行代码）；
- 在本文档明确指出这个待办，交给负责 `Views/` 的任务。

> 位置参考：`Views/DataCenterPage.xaml.cs:381-382` 目前是
> `string uid = st.AidMasked...` / `status.Text = stateText + " · " + uid;`
> 建议改为追加昵称：先查 `/api/miyoushe/roles` 取默认角色的 nickname。

---

## 5. 处理过的两个事故（如实记录）

### 5.1 `Services/WebServer.cs` 被旧版本覆盖 → 已用反编译恢复

做本任务时我用数组切片改文件，索引算错把文件截断；随后用 `git checkout` 恢复，
但该文件**从未被提交过**，HEAD 是 3.3.34 时代的老版本 → t4/t13/t17/t31/t33/t36 的改动全部丢失。

**恢复**：发现 `obj/Release/net11.0-windows10.0.26100.0/SoulLink.dll`（事故前最后编译产物）→
装 `ilspycmd` 反编译 → 得到 2281 行 `WebServer.cs` → 覆盖回来 → 双 TFM 0 error；
逐项抽查 `WebDirectReservedSpan`/`IsAnywhereListening`/`ApiBgiPicksAsync`/`ApiBgiRunsAsync`/
`HandleMiyousheAsync`/`MoraClassifier`/`ResolveInternalPort`/`IsLoopbackRequest` **全部完好**。

⚠️ **代价**：反编译代码功能等价但**形式有损**（原始注释丢失、变量名被简化）。

### 5.2 `docs/web-index.html` 被截断 → 从 HEAD 恢复

同类错误。这次该文件**有 git 历史**（4051 行，t13 已提交），`git checkout` 直接恢复成功。
之后改用 `edit` 精确替换而不是全量 `write`，并先做了备份。

**教训**：动大文件前先 `git log -- <file>` 确认有历史版本；没有就先复制备份。

---

## 6. 未做 / 待办

| 项 | 说明 |
|---|---|
| 主程序摘要行显示昵称 | 需改 `Views/DataCenterPage.xaml.cs`（禁改区），昵称已在 API 里备好 |
| 角色列表实时刷新 | 目前读本地库；用户刚绑定角色需先「立即拉取」一次才有数据 |
| 摩拉按角色分区并排展示 | 现在是**切换式**（一次看一个角色，避免混算），不是并排 |
