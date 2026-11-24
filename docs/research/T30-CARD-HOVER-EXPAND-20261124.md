# T30 网页卡片悬停自动展开/折叠（方案 C，防误操作）

> 任务：t26（member `yomori-scout` / attempt `2912bad8-00fb-4e09-80f5-686ddba05573`）

---

## 0. 交付摘要

| 要求 | 实现 |
|---|---|
| 方案 C：鼠标在整卡内（含展开区）不折 | `mouseenter`/`mouseleave` 绑在 **`.ucard`** 上，不是 `.ucard-head` |
| 移入延迟（防连环展开） | **280ms** |
| 移出延迟（防误折叠） | **500ms** |
| 多卡片互斥 | 全局 `_hoverCard` + **额外扫一遍所有 `.ucard.open`** |
| 保留点击 | 点头部切换 + `__pinned` 固定 |
| 移动端仅点击 | `if (__canHover)` 整体包裹两个监听 |
| 触屏模拟悬停兜底 | `pointerdown` <200ms 时跳过 `mouseenter` |
| 过渡动画 | `grid-template-rows: 0fr→1fr` + `opacity`，**180ms**（不再用 `display`） |

---

## 1. 两个延迟各自防什么（任务要求说明理由）

### 移入延迟 = **280ms**

**防的是：鼠标快速划过一排卡片 → 连环展开 → 页面连续跳动。**这是最主要的误操作来源。

| 候选 | 后果 |
|---|---|
| < 200ms | 鼠标快速掠过 3~4 张卡片会**连续炸开**，每张都展开 180ms 动画，视觉非常乱 |
| > 400ms | 「想展开一张」会明显迟滞，用户会觉得网页卡 |
| **280ms** ✅ | 落在「掠过不触发」与「停留即响应」之间；恰好略大于动画时长(180ms)，用户看到的结果很连贯 |

### 移出延迟 = **500ms**

**防的是：用户从卡片移到旁边的按钮/滚动条时，指针短暂扫过卡片边缘 → 展开区闪一下又收起。**

**为什么比移入更长（不对称）**：
```
误折叠的代价 = 要重新悬停 + 再等 280ms 才展开   （慢！）
误展开的代价 = 多看 500ms 的展开态             （轻微！）
⇒ 代价不对称 → 偏向「宁可多开一会儿」
```

---

## 2. 多卡片同时展开怎么处理（任务要求说明）

### 实现分两层

**第一层：`_hoverCard` 全局唯一**（任务建议的做法）
· 悬停展开成功时记下当前卡；移入新卡时先 `__collapseHoverCard()` 收起旧的。

**第二层：额外扫一遍所有已展开卡片**（我在实现中发现必须补的）
```javascript
document.querySelectorAll('#users .ucard.open').forEach(function (other) {
  if (other === card) return;
  __cancelCardTimers(other);
  other.classList.remove('open');
  other.__pinned = false;
  if (_hoverCard === other) _hoverCard = null;
});
```

**为什么必须补这一层**（这是我实测发现的缺陷）：
```
点击固定的卡片**不占用 _hoverCard**（点击处会把它置空）
⇒ 只靠 _hoverCard 的第一层，管不到被固定的那张
⇒ 「固定 A 后悬停 B」→ A 和 B 同时展开 → 页面跳动
  ← 正是任务③明令禁止的现象
```
补上后实测：**任意时刻最多一张卡片展开**（见 §5 场景矩阵第 7 项）。

---

## 3. 点击展开后，移出是否自动折回？（任务要我判断）

### 结论：**分两种情形**

| 情形 | 行为 | 理由 |
|---|---|---|
| **点击后鼠标移出卡片** | **不折叠**（保持展开） | 点击 = 用户**显式要求**「钉住」这张卡去看/操作里面的按钮。若移出就折回，用户必须**一直把鼠标悬在卡片上**才能点按钮 —— 明显违背点击意图 |
| **点击后移入另一张卡片** | **折叠**（解除固定） | 任务③明确要求「A 移到 B 时 A 应该折叠，否则多张同时展开、页面跳动」。语义上「固定」= **相对当前这张卡**保持展开；注意力只有一处 |

**这个判断与该倾向一致**：「点击 = 显式要求固定，移出不再自动折叠，直到再次点击**或移入其他卡片**」。

---

## 4. 视觉：为什么用 `grid-template-rows` 而不是 `max-height`

```css
/* 原：硬切换，无法过渡 */
.ucard-body{display:none}  .ucard.open .ucard-body{display:block}

/* 新：可过渡 */
.ucard-body{display:grid;grid-template-rows:0fr;opacity:0;
             transition:grid-template-rows .18s ease,opacity .18s ease,padding .18s ease;
             padding:0 14px 0;border-top:1px solid transparent;overflow:hidden}
.ucard-body-inner{min-height:0;overflow:hidden}      /* ← 关键：让 grid 行能收缩到 0 */
.ucard.open .ucard-body{grid-template-rows:1fr;opacity:1;padding:0 14px 13px;border-top-color:var(--border)}
```

| 方案 | 问题 |
|---|---|
| `display` | **属性本身不可动画**，无法过渡 |
| `max-height` | 必须**写死一个「够大」的值**。写小了夹断内容，写大了折叠时有明显延迟（前 80% 时间在走空余量） |
| **`grid-template-rows: 0fr→1fr`** ✅ | **不需要预知内容高度**，现代做法；配合内层 `min-height:0` 才能正确收缩 |

**新增了 `.ucard-body-inner` 包裹层** —— 没有它，折叠态下内容仍会撑出高度（grid 子项默认 `min-height:auto`）。

---

## 5. 逻辑验证：场景矩阵（用等价状态机跑真实时序）

把实现抽成等价状态机（同样的延时、同样的分支）跑真实 `setTimeout`：

| # | 操作 | 结果 | 预期 | ✓ |
|---|---|---|---|---|
| 1 | 悬停 A 100ms 后离开 | `A=closed` | 不展开（防掠过） | ✓ |
| 2 | 悬停 A 350ms | `A=OPEN` | 展开 | ✓ |
| 3 | 离开 A 600ms | `A=closed` | 折叠 | ✓ |
| 4 | 悬停 A → 悬停 B | `A=closed B=OPEN` | 互斥 | ✓ |
| 5 | 点击 A（固定）后离开 | `A=OPEN pinned=true` | 固定不折 | ✓ |
| 6 | 固定 A 后悬停 B | `A=closed B=OPEN` | 互斥（任务③） | ✓ |
| 7 | 再点击 A | `A=closed pinned=false` | 解除固定 | ✓ |
| 8 | **任意时刻已展开卡片数** | **= 1** | ≤1 | ✓ |

---

## 6. 不许破坏的（逐条核验）

| 项 | 状态 |
|---|---|
| `.uact-btn` 的 `e.stopPropagation()` | **保留**（L984）—— 点按钮不会冒泡到 head 触发折叠 |
| hash 路由跳转 | **保留**（L986 `location.hash = act === 'remote' ? ...`） |
| 移动端点击展开 | **保留**（点击逻辑在 `__canHover` 分支**之外**） |
| `.ucard-head` 点击语义 | **保留**（仍绑在 head 上，点展开区内容不会误折叠） |
| `.ucard{overflow:hidden}` | 原本就有，正好裁剪过渡 —— **未改动** |

### 额外修掉的一个隐患（渲染重建导致的状态泄漏）
`renderUsers()` 每次 `box.innerHTML = ''` 重建全部卡片，旧 `setTimeout` 会在**已移除的节点**上继续跑，
且 `_hoverCard` 会指向脱离文档的节点 → 「互斥」对空气作用，新卡片反而收不起上一张。
**已在重建前清理**：
```javascript
if (_hoverCard) { __cancelCardTimers(_hoverCard); _hoverCard = null; }
box.querySelectorAll('.ucard').forEach(__cancelCardTimers);
box.innerHTML = '';
```

---

## 7. 改动清单（file:line）

| 文件:行 | 改动 |
|---|---|
| `docs/web-index.html:189-200` | CSS：`display` → `grid-template-rows` 过渡 + `.ucard-body-inner` |
| `:798-859` | `__canHover` 探测、两个延迟常量、`_hoverCard`、`__cancelCardTimers`、`__collapseHoverCard` |
| `:861-869` | `renderUsers()` 重建前清理定时器与全局引用 |
| `:924-935` | 卡片 HTML 加 `.ucard-body-inner` 包裹层 |
| `:936-955` | 点击 handler：取消定时器 + `__pinned` 切换 + 互斥 |
| `:957-990` | 悬停 handler：进/出延迟、触屏兜底、互斥扫全部 open |

**只改了 `docs/web-index.html`**（+ 自动生成的 `Services/WebPages.cs` / `dist/web-index.html`）。**未改任何 C# 逻辑。**

---

## 8. 验证

| 项 | 结果 |
|---|---|
| 前端 JS 语法（**107,214 字符**整块 `new Function()` 解析） | **通过** |
| 三处同步 | `docs` / `WebPages.cs` / `dist` 均 `inner=3 hoverIn=3 grid-css=1` |
| `dotnet build -c Release` | **0 error** |
| 状态机场景矩阵 8 项 | **全部符合预期** |

**无法实测的部分**：悬停/点击是**真实鼠标交互**，我按红线**不发任何输入**（会操控用户键鼠），
所以视觉动画的实际观感、真实浏览器下的 hover 行为**未实测**。
逻辑层已用等价状态机 + 真实 `setTimeout` 验证（§5），视觉层需用户刷新页面确认。

---

## 9. 边界自查

只改 `docs/web-index.html` + 2 个自动生成文件；**未部署**；**未发任何输入**；
未碰 `dist\stable`；未发布、未碰 COS、未 git commit、未弹窗、未用 A1/A4；未改任何产品 C# 逻辑。

---

**文档作者**：yomori-scout
**最后更新**：2026-11-24