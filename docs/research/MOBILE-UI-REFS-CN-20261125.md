# 中文远控/串流产品 UI 截图级调研 —— **部分受阻，如实报告**

> 调研人：yomori-scout ｜ 任务 t35 ｜ attempt `b89a5434-3888-4b03-91e3-4dde1f7fcfdc`
> 日期：2026-11-25 ｜ **未改代码 ｜ 未连接手机**

---

## 0. ⚠️ 先说结论：**「必须下载真实截图」这一条我没做到**

你要的是「截图级调研」，**我拿到的 18 张图全是 44×44 的图标，没有一张是界面截图**。
**我不会拿图标当截图去编造界面描述。** 下面如实说明卡在哪。

### 我验证过的事实（可复核）

| 尝试 | 结果 |
|---|---|
| `sunlogin.oray.com/download/` | HTTP 200，84 个图片 URL，**下载 18 张后检查尺寸全部 44×44~190×36（图标）** |
| `sunlogin.oray.com/news/` | HTTP 200，46 张图，**全部是导航栏图标 + 产品硬件图**（goods/ 目录） |
| `service.oray.com/question/285.html` | 标题是「**如何登录网站管理系统**」，与串流无关 |
| `uu.163.com` | HTTP 200，21 张图，**全是 brand logo（PS/Xbox/华硕/360…）+ 二维码 + 社交图标** |
| `www.todesk.com/help/` | HTTP 200 但**只有 3,563 字节**（SPA 骨架），1 张图 |
| `appgallery.huawei.com/app/C103647881` | HTTP 200 但**只有 3,012 字节**，**0 张图**（SPA，截图靠 JS 加载） |

### 技术根因（这是能力边界，不是没努力）

```
这些站的界面截图都是 SPA（单页应用）里由 JS 动态加载的：
  · Invoke-WebRequest / web_fetch 只拿到 HTML 骨架（3~60KB）
  · 静态 HTML 里的 <img> 只有「导航图标 / 品牌 logo / 二维码」
  · 真正的界面截图在 JS 渲染后才插入 DOM，静态抓取看不到
```

**另外验证到一条有用的技术事实**（供后续抓图用）：
向日葵 CDN 有**反盗链** —— 不带 `Referer` 返回 **403**，带 `Referer: https://sunlogin.oray.com/` 才 200。
（示例：单张 `res1.orayimg.com/sunlogin/1.0/img/806ab1c.png` 无 Referer→403，有 Referer→200 / 8,780B / image/png）

### 已下载的内容（**是图标，不是截图，如实标注**）

目录：`%TEMP%\sl-ui-refs\sunlogin\`（18 个 png）

| 文件 | 尺寸 | 性质 |
|---|---|---|
| sunlogin-01-806ab1c.png | 190×36 | 文字 logo |
| sunlogin-02~07 | 44×44 | 功能图标 |
| sunlogin-12~20 | 100×100 | 功能图标 |
| sunlogin-21~24 | 48×36 / 56×56 | 图标 |

**这些能用于「图标风格」参考，但回答不了你要的「工具栏布局/虚拟鼠标形态/键盘按钮位置」。**

---

## 1. 我仍然拿到的有价值结论（源码级，非截图）

### 1.1 ★ 关键发现：我们服务端**有**光标回传，网页端**画了**，安卓端**完全没有**

这是我读我们自己源码确认的，**直接对应你说的「虚拟鼠标不是固定触摸板，而是带光标的可移动鼠标形态」**：

| 层 | 现状 | 源码位置 |
|---|---|---|
| **服务端** | ✅ **有** `CursorEcho()`，每条 `/api/input` 响应附带 `cursor:{nx,ny,visible}` | `Relay/AgentWebServer.cs:721-741, 748-752` |
| | ✅ 用 `GetCursorPos()` 读宿主真实光标 | `:752, :844` |
| | ❌ **没有**独立的 `/api/cursor` 轮询端点（只在 input 响应里附带） | grep 无命中 |
| **网页端** | ✅ **画了** `#rcCursor` —— **十字准星 + 白色圆环** | `docs/web-index.html:346-353, 645` |
| | ✅ `consumeCursorEcho()` → `updateRemoteCursor()` 驱动 | `:1983-1990, 2171-2190` |
| **安卓端** | ❌ **完全没有光标概念**（grep `cursor/Cursor/drawCursor` 零命中） | 确认 |

**⇒ 这就是「差到没法用」的另一半：网页端用户看得见光标，安卓端用户完全看不见指针在哪。**

**而且网页端的光标形态是现成的**（`web-index.html:346-353`）：
```css
#rcCursor .rc-dot{position:absolute;left:-7px;top:-7px;width:14px;height:14px}
#rcCursor .rc-dot::before{left:6px;top:0;width:2px;height:14px}   /* 竖线 */
#rcCursor .rc-dot::after{left:0;top:6px;width:14px;height:2px}    /* 横线 → 十字 */
#rcCursor .rc-dot .rc-ring{position:absolute;inset:-4px;border:1.5px solid #fff;...}  /* 外圈 */
```

### 1.2 ★ Moonlight 的光标策略：**不画本地光标，而是切系统 PointerIcon**

**源码确认**（`AndroidPointerIconCaptureProvider.java` 全文 996B，已完整获取）：
```java
@Override
public void hideCursor() {
    super.hideCursor();
    targetView.setPointerIcon(PointerIcon.getSystemIcon(context, PointerIcon.TYPE_NULL));
}

@Override
public void showCursor() {
    super.showCursor();
    targetView.setPointerIcon(null);      // null = 恢复默认箭头
}
```

**⇒ 结论（源码确认）**：
· Moonlight **没有自绘光标**（grep `drawCursor`/`setCursor` 在 `StreamView`/`Game` 中零命中）；
· 它用 `setPointerIcon(TYPE_NULL)` **隐藏系统光标**（进入相对模式时），`setPointerIcon(null)` **恢复默认箭头**；
· **光标视觉由 Android 系统的 `PointerIcon` 提供**（默认是系统箭头），不是应用画的。

基类 `InputCaptureProvider.java`（全文 1,025B）只是标志位：
```java
public void showCursor() { isCursorVisible = true; }
public void hideCursor() { isCursorVisible = false; }
```

**对照**（`Game.java` 源码）：
```java
private boolean cursorVisible = false;
// Toggle cursor visibility
cursorVisible = !cursorVisible;
if (cursorVisible) { inputCaptureProvider.showCursor(); }
else { inputCaptureProvider.hideCursor(); }
```
（`Game.java:136, 1236-1246`）

**[推测]** 这意味着 Moonlight 在相对模式下**默认是看不到光标的**（隐藏了），
只有用户手动切换 `cursorVisible` 才显示 —— 因为 Moonlight 主打**游戏**（游戏内自带准星），而不是**桌面操控**。
**⇒ 这恰恰说明：我们做桌面操控，不能照抄 Moonlight 的「隐藏光标」策略，必须自己画可见指针。**

### 1.3 触控板模式：Moonlight 用**独立全屏透明 View** 实现

源码 `Game.java:244-245` 注释原文（已获取）：
> `// Listen for touch events on the background touch view to enable trackpad mode`
> `// to work on areas outside of the StreamView itself. We use a separate View`

配合 `activity_game.xml`（全文 1,905B，已获取）：
```xml
<View android:id="@+id/backgroundTouchView"
    android:layout_width="match_parent"
    android:layout_height="match_parent"
    android:layout_gravity="center" />
<com.limelight.ui.StreamView android:id="@+id/surfaceView" ... />
```

**⇒ 层级是「全屏透明 View（吃触摸）+ StreamView（画面）」，透明 View 在下、画面在上。**
这样「画面之外的区域」也能当触摸板 —— **这正是触控板模式的关键实现**。

---

## 2. 我没查到的（**不编，逐条列出**）

| 项 | 状态 | 原因 |
|---|---|---|
| 向日葵/ToDesk/UU **界面截图** | ❌ **未取得** | SPA + 反盗链 + 商店页也是 SPA |
| 它们的**工具栏布局/按钮排布** | ❌ 未取得 | 同上 |
| 它们的**虚拟鼠标形态** | ❌ 未取得 | 同上 |
| 它们的**键盘按钮位置** | ❌ 未取得 | 同上 |
| 官方**手势清单原文** | ❌ 未取得 | 帮助中心页面抓不到正文（SPA） |
| RustDesk 移动端界面 | ⚠️ **部分** | 只拿到目录结构（`flutter/lib/mobile/` 有 `pages/`、`widgets/`、`terminal_keyboard_utils.dart`），**未逐个读页面代码** |
| 云游戏（顺网/达龙/START/咪咕） | ❌ 未查 | 时间已用尽在截图获取上 |

**我不会把「[推测] 向日葵可能有一个底部工具栏」这种话写成结论。没有截图就没有发言权** —— 这是你这个任务的核心要求，我认。

---

## 3. 从已有源码能确定给设计的东西（不依赖截图）

### 3.1 虚拟鼠标：**我们有现成的服务端能力，缺的是安卓端渲染**

**这条不需要看任何截图就能确定**，因为是我们自己的代码：

| 能力 | 状态 |
|---|---|
| 服务端回传远端光标 `{nx,ny,visible}` | ✅ **已有**（`AgentWebServer.cs:721-741`） |
| 归一化坐标（与手机端渲染基准一致） | ✅ **已有**（相对抓屏矩形） |
| 光标可见性（`GetCursorPos` 失败时 `visible:false`） | ✅ **已有** |
| 网页端渲染参考实现 | ✅ **已有**（十字+圆环，`web-index.html:346-353`） |
| **安卓端渲染** | ❌ **缺** |

**⇒ 这是最低成本、最高收益的一步**：服务端已经在每条 input 响应里回了光标，
安卓端只要「解析 `cursor` 字段 + 画一个十字/圆环」，**就能让用户看见指针**。**不需要新协议、不需要新端点。**

**但要注意一个已知问题**（我们自己的代码注释已记录，`web-index.html:2450-2452`）：
> [基于我方注释] `rcCursor` 靠服务端 cursor 回传驱动，但**首次点击后没有 move 事件 → 没有新的 cursor 回传 → rcCursor 停在旧位置或干脆没显示**

**⇒ 若只在 input 响应里附带光标，则「不动手指就没有光标更新」** ——
因为**相对模式下不发 move 请求就没有响应**。
**[推测]** 这印证了桌面操控场景需要**独立的 `/api/cursor` 轮询端点**（低频，如 10~30Hz），
而不是搭在 input 响应上。这正是我在 §1.1 发现「没有独立端点」的意义所在。

### 3.2 触控板模式：Moonlight 的「独立背景 View」结构可直接借鉴

**源码确认**（§1.3）：`backgroundTouchView`（全屏透明）在下、`StreamView` 在上。
**[推测]** 这样「画面外区域也能滑」——因为透明 View 铺满整屏。

### 3.3 键盘：**没有拿到任何产品的一手实现**

**只拿到 RustDesk 的 `terminal_keyboard_utils.dart`（651B）**，但那是**终端专用键盘**（Esc/Tab/Ctrl/方向键/Home/End），
不是串流软键盘。**不足以支撑设计**。

---

## 4. 我的建议：这件事需要换方法

**结论：用「静态网页抓取」拿不到 SPA 里的界面截图。要完成你这个任务，需要下列之一：**

| 方案 | 可行性 | 说明 |
|---|---|---|
| **① 你直接发截图** | ★★★ **最快最准** | 你手上就有向日葵/ToDesk/UU 的 App，截 3~5 张（播放页/工具栏/虚拟鼠标/键盘）给我 |
| **② 本地安装 APK 后 adb 截图** | ★★ 但受限 | 我**不能连你的手机**（你的红线）。若允许我在**模拟器**里装 APK 截图，可行 —— 但需要模拟器环境 |
| **③ 找带静态截图的页面** | ★ 低 | 部分第三方教程站/知乎文章会把截图**以静态 img 直接嵌在 HTML 里**，但那需要能搜到这些页面（我当前无可用搜索 API） |
| **④ 只做开源产品** | ★★ 中 | RustDesk 有源码而非截图；但它的**移动端 UI 代码**能给出「原生视觉语言」的一手依据 |

**我倾向 ①**：你截图 3 分钟，胜过我在 SPA 上瞎撞 30 分钟。
**我不会为了交差去编造「向日葵的工具栏在底部」这种没有依据的话。**

---

## 5. 本轮已下载文件的完整路径（供你查看，**是图标不是截图**）

```
%TEMP%\sl-ui-refs\sunlogin\
  sunlogin-01-806ab1c.png     190x36    文字 logo
  sunlogin-02-591eaa7.png     44x44     功能图标
  sunlogin-03-fbe978e.png     44x44     功能图标
  sunlogin-04-80906df.png     44x44     功能图标
  sunlogin-05-a4b3a73.png     44x44     功能图标
  sunlogin-07-fc00245.png     44x44     功能图标
  sunlogin-12-931228f.png     100x100   功能图标
  sunlogin-13-678492d.png     100x100   功能图标
  sunlogin-14-8d42f2d.png     100x100   功能图标
  sunlogin-15-275016b.png     100x100   功能图标
  sunlogin-16-ad02308.png     100x100   功能图标
  sunlogin-17-be483de.png     100x100   功能图标
  sunlogin-18-45aa955.png     100x100   功能图标
  sunlogin-19-4ef4578.png     100x100   功能图标
  sunlogin-20-751d737.png     100x100   功能图标
  sunlogin-21-28240f3.png     48x36     图标
  sunlogin-22-dd9c751.png     56x56     图标
  sunlogin-24-4b26b68.png     56x56     图标
```

**（等你确认方法后，这些图标目录我会清理掉）**

---

## 6. 边界自查

· **未改任何代码**；
· **未连接用户手机**（未执行任何 adb 命令）；
· 截图尝试全部记录 URL 与失败原因，**未伪造**；
· 下载的 18 个文件在 `%TEMP%\sl-ui-refs\`（**不在 SoulLink 工作区**），**已确认全部是图标**，等指示后清理；
· 报告中所有我方代码结论均给 `文件:行号`；推测已标 `[推测]`。

---

**文档作者**：yomori-scout
**最后更新**：2026-11-25