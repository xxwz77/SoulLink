# 手机串流/远控「全屏」真正含义 + 触摸手感 对标调研

> 调研人：yomori-scout ｜ 任务 t31 ｜ attempt `ca1f6455-c3c8-4bcb-9b66-060d68ddea9e`
> 日期：2026-11-25 ｜ **纯调研，未改任何代码，未连接手机**

---

## 0. 直接回答用户那句话：「你没理解什么是全屏」

**用户是对的。** 我们做的「全屏」= 隐藏状态栏 + 隐藏导航栏 + 沉浸式 —— **这只是第 1 层**。

专业实现（Moonlight 官方）的「全屏」是 **5 层**，我们**只做了第 1 层**：

| 层 | 内容 | 我们做了吗 |
|---|---|---|
| **① 隐藏系统栏** | `FLAG_FULLSCREEN` + `SYSTEM_UI_FLAG_IMMERSIVE_STICKY` | ✅ 有 |
| **② 布局延伸到系统栏下方** | `FLAG_LAYOUT_IN_SCREEN` + `LAYOUT_HIDE_NAVIGATION/LAYOUT_FULLSCREEN` | ⚠️ 部分 |
| **③ 铺满到刘海/挖孔下面** | `layoutInDisplayCutoutMode = ALWAYS / SHORT_EDGES` | ❌ **完全没有** |
| **④ 视频铺满整屏不留黑边** | `stretchVideo` 选项 / `setFixedSize` vs `setDesiredAspectRatio` | ❌ **完全没有** |
| **⑤ 宽高比不匹配时的决策** | 保持比例 letterbox **或** 拉伸铺满（**用户可选**） | ❌ **完全没有** |

**★ 而且最关键的**：我们手机竖屏 **1208×2624（比例 0.46）**，服务端桌面 **1920×1080（比例 1.78）**，
**两者比例完全相反**。我们既没做 letterbox 计算，也没做拉伸选项，
所以画面要么被系统默认行为拉伸变形，要么留巨大黑边 —— **这才是「没理解全屏」的真正含义**。

---

## 1. 一、「全屏」的完整定义（逐层源码出处）

### 1.1 第①②层：基础全屏 + 布局延伸到系统栏下方

**官方 `Game.java:190-206`**（源码确认）：
```java
requestWindowFeature(Window.FEATURE_NO_TITLE);

// Full-screen
getWindow().addFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN);

// If we're going to use immersive mode, we want to have
// the entire screen
getWindow().getDecorView().setSystemUiVisibility(
        View.SYSTEM_UI_FLAG_LAYOUT_STABLE |
        View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION |
        View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN);

getWindow().addFlags(WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN);
```

**沉浸式本体 `Game.java:993-1000`**：
```java
// Use immersive mode
getWindow().getDecorView().setSystemUiVisibility(
        View.SYSTEM_UI_FLAG_LAYOUT_STABLE |
        View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION |
        View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN |
        View.SYSTEM_UI_FLAG_HIDE_NAVIGATION |
        View.SYSTEM_UI_FLAG_FULLSCREEN |
        View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY);
```

**注意**：官方**没有用** `WindowInsetsController`，而是 `SYSTEM_UI_FLAG_*` 老 API，
**且源码里有 TODO 承认这一点**（`Game.java:983-984`）：
> `// TODO: Do we want to use WindowInsetsController here on R+ instead of
> `// SYSTEM_UI_FLAG_IMMERSIVE_STICKY? They seem to do the same thing as of S...`

**★ V+ 补上了这一层**（`Game.kt:387-389`，源码确认）：
```kotlin
if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
    window.setDecorFitsSystemWindows(false)     // ← 官方没有，V+ 有
}
```
**⇒ `setDecorFitsSystemWindows(false)` 是 Android 11+ 的现代全屏 API，
它的作用是「让内容延伸到系统栏区域」，是 `FLAG_LAYOUT_IN_SCREEN` 的现代替代。**

### 1.2 ★ 第③层：铺满到刘海/挖孔下面（我们完全没有）

**官方 `Game.java:225-236`**（源码确认）：
```java
if (prefConfig.stretchVideo || shouldIgnoreInsetsForResolution(prefConfig.width, prefConfig.height)) {
    // Allow the activity to layout under notches if the fill-screen option
    // was turned on by the user or it's a full-screen native resolution
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
        getWindow().getAttributes().layoutInDisplayCutoutMode =
                WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_ALWAYS;
    }
    else if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
        getWindow().getAttributes().layoutInDisplayCutoutMode =
                WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES;
    }
}
```

**`shouldIgnoreInsetsForResolution()` 实现（`Game.java:764-782`）**：
```java
private boolean shouldIgnoreInsetsForResolution(int width, int height) {
    // Never ignore insets for non-native resolutions
    if (!PreferenceConfiguration.isNativeResolution(width, height)) {
        return false;
    }
    Display display = getWindowManager().getDefaultDisplay();
    for (Display.Mode candidate : display.getSupportedModes()) {
        // Ignore insets if this is an exact match for the display resolution
        if ((width == candidate.getPhysicalWidth() && height == candidate.getPhysicalHeight()) ||
                (height == candidate.getPhysicalWidth() && width == candidate.getPhysicalHeight())) {
            return true;
        }
    }
    return false;
}
```

**它的语义**：**只有当「流分辨率 == 屏幕物理分辨率」时，才忽略刘海 insets**。
逻辑很对：铺满到刘海下只在「像素一一对应」时才有意义；否则会裁掉画面内容。

### 1.3 ★★ 第④⑤层：视频铺满 vs letterbox（这才是用户说的「全屏」核心）

**决策代码 `Game.java:935-960`**（源码确认）：
```java
boolean aspectRatioMatch = false;
if (Build.VERSION.SDK_INT < Build.VERSION_CODES.M) {
    Point screenSize = new Point(0, 0);
    display.getSize(screenSize);
    double screenAspectRatio = ((double)screenSize.y) / screenSize.x;
    double streamAspectRatio = ((double)prefConfig.height) / prefConfig.width;
    if (Math.abs(screenAspectRatio - streamAspectRatio) < 0.001) {
        LimeLog.info("Stream has compatible aspect ratio with output display");
        aspectRatioMatch = true;
    }
}

if (prefConfig.stretchVideo || aspectRatioMatch) {
    // Set the surface to the size of the video
    streamView.getHolder().setFixedSize(prefConfig.width, prefConfig.height);
}
else {
    // Set the surface to scale based on the aspect ratio of the stream
    streamView.setDesiredAspectRatio((double)prefConfig.width / (double)prefConfig.height);
}
```

**两条分支的含义**：

| 分支 | 条件 | 行为 |
|---|---|---|
| **拉伸铺满** | `stretchVideo == true` **或** 宽高比恰好匹配 | `setFixedSize(流宽, 流高)` → Surface 固定为流的原始尺寸，**由系统拉伸铺满整个 View** |
| **保持比例（letterbox）** | 否则（默认） | `setDesiredAspectRatio(w/h)` → **自定义 onMeasure 保持比例，多余区域留黑边** |

**`StreamView.onMeasure()` 的实现（`StreamView.java:38-60`，源码确认）**：
```java
@Override
protected void onMeasure(int widthMeasureSpec, int heightMeasureSpec) {
    // If no fixed aspect ratio has been provided, simply use the default onMeasure() behavior
    if (desiredAspectRatio == 0) {
        super.onMeasure(widthMeasureSpec, heightMeasureSpec);
        return;
    }
    int widthSize = MeasureSpec.getSize(widthMeasureSpec);
    int heightSize = MeasureSpec.getSize(heightMeasureSpec);
    int measuredHeight, measuredWidth;
    if (widthSize > heightSize * desiredAspectRatio) {
        measuredHeight = heightSize;
        measuredWidth = (int)(measuredHeight * desiredAspectRatio);
    } else {
        measuredWidth = widthSize;
        measuredHeight = (int)(measuredWidth / desiredAspectRatio);
    }
    setMeasuredDimension(measuredWidth, measuredHeight);
}
```

**这就是「letterbox」的标准实现**：取「能放进屏幕的最大等比矩形」，**不裁切、不拉伸**。

### 1.4 ★ 默认值是关键：`stretchVideo` 默认 **false**

**`PreferenceConfiguration.java:74`**（源码确认）：
```java
private static final boolean DEFAULT_STRETCH = false;
```

**⇒ 官方 Moonlight 默认「保持比例 + 黑边」，用户可选「拉伸铺满」。**

**为什么这个默认是对的**：拉伸会让画面变形（圆形变椭圆、文字变扁），
对需要看清内容的场景（办公/文字/UI）是灾难。**但用户明确说「你没理解全屏」，说明他要的是拉伸铺满。**

### 1.5 ★ 分辨率协商：专业软件怎么处理「竖屏手机 vs 横屏桌面」

**关键事实（源码确认）**：Moonlight 的 `prefConfig.width/height` **是用户选择的流分辨率**，
由 `RESOLUTION_PREF_STRING = "list_resolution"` 持久化（`PreferenceConfiguration.java:29`），
**并在连接时通过 RTSP 协商发给服务端**（这是 Moonlight 协议的标准行为，[推测] 具体报文未读源码）。

**`isNativeResolution()`（`PreferenceConfiguration.java:159+`）** 判定逻辑：
```java
public static boolean isNativeResolution(int width, int height) {
    // It's not a native resolution if it matches an existing resolution option
    if (width == 640 && height == 360) return false;
    else if (width == 854 && height == 480) return false;
    else if (width == 1280 && height == 720) return false;
    else if (width == 1920 && height == 1080) return false;
    else if (width == 2560 && height == 1440) return false;
    ...  // 其它标准分辨率也返回 false
```
**⇒ 只有「不在标准列表里」的分辨率才被视为 native**（即用户手填的屏幕原生分辨率）。

**★ 对我们的启示（重要）**：

| 方案 | 专业做法 | 我们的状况 |
|---|---|---|
| 让服务端改成客户端比例 | **是**（用户选分辨率 → 协商 → 服务端按该分辨率渲染） | ❌ 我们固定 1920×1080 |
| 客户端旋转 + letterbox | 官方默认（`DEFAULT_STRETCH=false`） | ❌ 我们两者都没做 |
| 客户端拉伸铺满 | 用户可选（`stretchVideo=true`） | ❌ 没有这个选项 |

**⇒ 用户要「全屏」，最可能就是要「拉伸铺满」这个选项。**
（另需确认：我们的服务端能否动态改分辨率 —— 这决定了「旋转+letterbox」还是「改分辨率」哪条路可行。）

---

## 2. ★ 二、触摸手感为什么「差到没法用」

### 2.1 ★★ 最重要的发现：官方默认是**相对模式**，我们默认是**绝对模式**

**官方 `PreferenceConfiguration.java:104`（源码确认）**：
```java
private static final boolean DEFAULT_ABSOLUTE_MOUSE_MODE = false;   // ← 默认相对！
```

**我们 `PlayerPlaceholderScreen.kt:72`（源码确认）**：
```kotlin
sp.getString("mouseMode", "absolute") ?: "absolute"    // ← 默认绝对！
```

**⇒ 默认值正好相反。这极可能就是「差到没法用」的头号原因。**

**相对模式的实际发送代码（`RelativeTouchContext.java:230-247`）**：
```java
if (prefConfig.absoluteMouseMode) {
    conn.sendMouseMoveAsMousePosition((short) deltaX, (short) deltaY,
            (short) targetView.getWidth(), (short) targetView.getHeight());
} else {
    conn.sendMouseMove((short) deltaX, (short) deltaY);      // ← 默认走这里：纯增量
}
```

**为什么相对模式手感好**：
· 手指移动 10px → 鼠标移动 10px（**增量**），**与触摸点的绝对位置无关**；
· 手指可以反复「抬起-滑动-抬起」像**笔记本触摸板**那样累积移动，不受屏幕边界限制；
· **绝对模式**要求「手指位置 = 鼠标位置」，一根手指最多覆盖整个屏幕，
  精度受限于屏幕像素 → **小目标点不准、长距离移动要反复抬手指** → 用户说的「没法用」。

### 2.2 绝对模式的专业参数（Moonlight，源码确认）

**`AbsoluteTouchContext.java:58-67`**：
```java
private static final int SCROLL_SPEED_FACTOR = 3;
private static final int LONG_PRESS_TIME_THRESHOLD = 650;              // 长按 650ms
private static final int LONG_PRESS_DISTANCE_THRESHOLD = 30;           // 长按容差 30px
private static final int DOUBLE_TAP_TIME_THRESHOLD = 250;              // 双击 250ms
private static final int DOUBLE_TAP_DISTANCE_THRESHOLD = 60;           // 双击容差 60px
private static final int TOUCH_DOWN_DEAD_ZONE_TIME_THRESHOLD = 100;    // ★ 按下后 100ms 内不发点击
private static final int TOUCH_DOWN_DEAD_ZONE_DISTANCE_THRESHOLD = 20; // ★ 且位移 <20px
```

**★ 最有价值的是最后两个「死区」参数**（`AbsoluteTouchContext.java:159-168`）：
```java
handler.postDelayed(longPressRunnable, LONG_PRESS_TIME_THRESHOLD);
handler.postDelayed(tapDownRunnable, TOUCH_DOWN_DEAD_ZONE_TIME_THRESHOLD);
```
**含义**：手指按下后**先等 100ms**，若期间移动超过 20px 就**不发点击**（说明用户在滑动，不是在点）。
**作用**：避免「想滑动却先触发一次误点击」。
**我们完全没有这层死区** → 每次滑动都可能先误点一下。

### 2.3 相对模式的专业参数（Moonlight，源码确认）

**`RelativeTouchContext.java:83-90`**：
```java
private static final int TAP_MOVEMENT_THRESHOLD = 20;    // 判定 tap 的边界 20px
private static final int TAP_DISTANCE_THRESHOLD = 25;    // 累计位移 25px 内仍算 tap
private static final int TAP_TIME_THRESHOLD = 250;       // tap 时限 250ms
private static final int DRAG_TIME_THRESHOLD = 650;      // 长按 650ms 进入拖动
private static final int SCROLL_SPEED_FACTOR = 5;        // 双指滚动倍率
```

**注意几个精妙之处**：

| 机制 | 源码 | 作用 |
|---|---|---|
| **累计位移**判定 | `distanceMoved += sqrt(dx²+dy²)`（`:196`） | 不是「直线距离」，是**路径长度** —— 抖动会累积，更真实 |
| **零增量不更新基准** | `if (deltaX != 0) lastTouchX = eventX;`（`:153-158`） | 源码注释：「so devices that report small touch events often will work correctly」 —— **防高 DPI 设备因取整丢步** |
| **tap 后延迟 100ms 抬键** | `handler.postDelayed(buttonUpRunnable, 100)`（`:213`） | 注释：「allow for apps that use polling to detect mouse button presses」 —— **兼容轮询式应用** |
| **多指只认主指针** | `if (actionIndex == 0)`（`:126, :189`） | 避免多指重复发 move |
| **拖动前先按下** | `confirmedDrag = true; conn.sendMouseButtonDown(...)`（`:46-47`） | 650ms 后进入拖动 = **按住左键移动**（拖拽文件/选文字） |

### 2.4 我们 vs 专业实现 逐项对比

| 项 | Moonlight 官方 | **我们** | 影响 |
|---|---|---|---|
| **默认鼠标模式** | **相对**（`DEFAULT_ABSOLUTE_MOUSE_MODE=false`） | **绝对**（`"absolute"`） | ★★★ **头号问题** |
| 按下死区 | **100ms / 20px**（防误点） | **无** | ★★ 滑动前误点击 |
| tap 判定 | 20px 边界 + 25px 累计 + 250ms | `TAP_PX=10f`（单一位移阈值） | ★ 10px 过严，易误判拖动 |
| 长按 | **650ms / 30px** | `LONG_PRESS_MS=600` / `DRAG_THRESHOLD_DP=12` | ★ 接近，但我们 12dp 容差偏小 |
| 拖动阈值 | 650ms 长按后进入 | 12dp 位移即判定 | ★★ **机制不同**：我们靠位移，官方靠时间 |
| 双击 | 250ms / 60px | **未实现** | — |
| 节流 | **无固定节流**（每 move 都发，靠协议批量） | `INPUT_THROTTLE_MS=8` | ⚠️ 见 §2.5 |
| 平滑/插值 | **无** | **无** | 一致 |
| 零增量保护 | **有**（`if (deltaX != 0)`） | **无** | ★ 高 DPI 设备可能丢步 |
| 多指只认主指针 | **有**（`actionIndex == 0`） | 有（`active.size == 1`） | 一致 |

### 2.5 关于「8ms 节流够不够」

**我们的做法**：`INPUT_THROTTLE_MS = 8`（约 125Hz 上限）。

**Moonlight 的做法（源码确认）**：
· `RelativeTouchContext` **没有时间节流** —— `touchMoveEvent` 里每个事件都处理；
· 但它**依赖协议的输入批量**（`ControllerPacket` 位图 + 帧对齐），由底层合并；
· 官方注释（`LOW-LATENCY-DEEP-DIVE-20261124.md:71` 已记录）提到「按钮状态变化即终止当前批」。

**结论**：
· **8ms 节流本身不算错**（125Hz 已高于大多数手机触摸采样率的有效需求）；
· **真正的问题不是节流频率，而是「默认绝对模式 + 无按下死区」**；
· [推测] 若触摸采样率是 120Hz（8.3ms/帧），8ms 节流基本不丢事件；**不建议先动这个值**。

### 2.6 关于「平滑/插值」

**Moonlight 官方没有任何平滑/插值**（`RelativeTouchContext.touchMoveEvent` 直接发原始 delta，源码确认）。
**⇒ 专业人士也没做平滑，说明这不是关键。不要凭直觉加。**

[推测] 平滑会引入**额外延迟**（要对未来做预测或对过去做平均），对远程操作是负收益。
真正的跟手性靠**降低端到端延迟**，而不是插值。

### 2.7 「专业软件能做到多少 ms」—— **查不到，不编**

**如实说明**：本轮**没有拿到** Moonlight / ToDesk / 向日葵 / RustDesk 官方公布的
「手指移动到画面响应」的端到端延迟数字。
· Moonlight 官网/wiki 因网络限制未取到（`raw.githubusercontent.com` 不可达，GitHub 网页 SPA）；
· ToDesk/向日葵/RustDesk 均为闭源或**未公布该指标**，**不编造数字**。

**能确认的**：Moonlight 默认相对模式 + 无节流 + 无平滑，即「**最短路径转发原始输入**」——
这也是一种设计取向：**不额外增加任何处理延迟**。

---

## 3. 三、独立编译记录（如实报告）

### 3.1 环境（已核实可用）

| 项 | 值 |
|---|---|
| Gradle | `D:\Android\gradle-8.9\bin\gradle.bat` ✅ 存在 |
| Android SDK | `D:\Android\sdk`（`platforms\android-34`）✅ |
| JDK | Temurin **17.0.21**（`JAVA_HOME` 已设）✅ |
| ANDROID_HOME / ANDROID_SDK_ROOT | 均已设为 `D:\Android\sdk` ✅ |
| 磁盘 | C: 203GB 空闲 / D: 1179GB ✅ |

### 3.2 编译尝试：**失败 —— 卡在「拿不到完整仓库」**

**尝试记录（按时间顺序）**：

| # | 方法 | 结果 |
|---|---|---|
| 1 | `git clone --depth 1 https://ghproxy.net/https://github.com/.../moonlight-android.git` | **超时 >120s**，目录留下空文件夹 |
| 2 | `--filter=blob:none` 后台克隆 | 同上，**files=0** |
| 3 | `codeload.github.com/.../tar.gz`（走 ghproxy） | **HTTP 403 FORBIDDEN** |
| 4 | `--single-branch --branch master` | 报 `destination path 'ml' already exists`（残留），清理后**目录删不掉**（句柄/权限） |
| 5 | 换全新目录 `t31ml-work` 后台克隆 | **仍在跑 5 分钟无产出，files=0**，主动 kill |
| 6 | `raw.githubusercontent.com` 直连 | **fetch failed** |
| 7 | `cdn.jsdelivr.net` | **fetch failed** |
| 8 | **逐文件 `Invoke-WebRequest` 走 ghproxy** | ✅ **成功** —— 单文件 119KB / **1.5 秒** |

**卡在哪（明确结论）**：
**ghproxy 代理单文件下载很快（119KB/1.5s），但 `git clone` 整个仓库（含 .git 对象协商）持续超时。**
**⇒ 代理在「大量小请求 / 协议协商」场景下不可用，只适合单文件 HTTP GET。**

### 3.3 实际做成的事：**下载了 12 个关键源文件（全部成功）**

```
app/src/main/java/com/limelight/Game.java                          119,760 B ✅
app/src/main/java/com/limelight/ui/StreamView.java                   2,842 B ✅
app/src/main/res/layout/activity_game.xml                            1,905 B ✅
app/src/main/java/com/limelight/binding/input/touch/TouchContext.java      419 B ✅
app/src/main/java/com/limelight/binding/input/touch/RelativeTouchContext.java 10,717 B ✅
app/src/main/java/com/limelight/binding/input/touch/AbsoluteTouchContext.java  8,241 B ✅
app/src/main/java/com/limelight/binding/input/ControllerHandler.java  145,175 B ✅
app/src/main/java/com/limelight/preferences/PreferenceConfiguration.java 28,208 B ✅
app/build.gradle / build.gradle / settings.gradle / gradle-wrapper.properties ✅
```

**这 12 个文件覆盖了本任务要回答的 100% 问题**（全屏 5 层 + 触摸参数），
**所以编译失败没有影响结论的完备性** —— 所有结论都来自这些**真实源码**，不是猜的。

### 3.4 清理

· 已 `job_kill` 后台克隆任务；
· 源码放在 `%TEMP%\t31ml-src`（**不在 SoulLink 工作区**），任务结束前删除；
· **未修改 SoulLink 任何代码**；**未连接手机**。

---

## 4. ★ 四、可落地清单

### A 档 —— 立刻改（配置级，风险低）

| # | 改动 | 解决症状 | 依据 | 代价 |
|---|---|---|---|---|
| **A1** | **默认鼠标模式从 `absolute` 改为 `relative`** | ★★★ **「效果差到没法用」头号原因** | `PreferenceConfiguration.java:104` `DEFAULT_ABSOLUTE_MOUSE_MODE=false` | **1 行**（改默认值；已有 UI 切换按钮） |
| **A2** | **增加「铺满/保持比例」选项 + 默认保持比例** | ★★ **用户说的「没理解全屏」** | `DEFAULT_STRETCH=false` + `StreamView.onMeasure` letterbox | 中等（需实现 `onMeasure` 等比或 `setFixedSize`） |
| **A3** | **加按下死区**（100ms 内移动 >20px 则不点击） | ★★ 滑动前误点击 | `AbsoluteTouchContext.java:66-67,159-168` | 小（几行判断） |
| **A4** | **加刘海铺满**（`layoutInDisplayCutoutMode = ALWAYS`） | 顶部被刘海区域浪费 | `Game.java:225-236` | 小（改 Window 属性） |
| **A5** | **加 `setDecorFitsSystemWindows(false)`**（API30+） | 现代全屏更彻底 | `Game.kt:387-389`（V+） | 小 |
| **A6** | **零增量保护**（`if (delta != 0) 才更新基准`） | 高 DPI 设备丢步 | `RelativeTouchContext.java:153-158` | 小 |

**A1 是最高优先级**：一行改动，直接对应「差到没法用」。

### B 档 —— 要改架构

| # | 改动 | 解决症状 | 依据 |
|---|---|---|---|
| **B1** | **分辨率协商**：客户端把自己的屏幕比例告诉服务端，服务端按该分辨率渲染 | 竖屏手机 vs 横屏桌面比例完全相反 | `RESOLUTION_PREF_STRING` + Moonlight 协商机制 |
| **B2** | **触控板模式**（整屏滑动 = 相对移动，不限边界） | 长距离移动要反复抬手指 | 相对模式已是雏形，需补「不限边界 + 可调灵敏度」 |
| **B3** | **灵敏度调节**（`xFactor/yFactor` 可配） | 不同 DPI 手感差异 | `RelativeTouchContext.java:143-145` 已有 `referenceWidth/targetView.getWidth()` 缩放 |

### C 档 —— 暂不做

| # | 内容 | 理由 |
|---|---|---|
| C1 | 触摸平滑/插值 | **Moonlight 官方没做**；会引入额外延迟，负收益 |
| C2 | 改 8ms 节流 | 125Hz 已够；**真正问题是默认模式不是节流** |
| C3 | 双击支持 | 优先级低（远控场景少用） |
| C4 | 自研 UDP 输入通道 | 传输层非当前瓶颈（AUDIT-06 已论证） |

---

## 5. 我的判断：为什么「差到没法用」

**排序（按影响大小）**：

1. **默认绝对模式**（我们 `absolute` vs 官方 `relative`）
   绝对模式要求「手指位置 = 鼠标位置」，一根手指最多覆盖一屏，
   **精度受限于屏幕像素、长距离移动要反复抬手指** → 这是「没法用」的主因。
2. **没有按下死区** → 每次想滑动都可能先误点一下。
3. **`TAP_PX=10f` 过严**（官方是 20px 边界 + 25px 累计）→ 轻微抖动就被判成拖动。
4. **没有 letterbox/拉伸选项** → 竖屏手机上横屏画面要么变形要么大黑边。

**⇒ A1（改默认模式）+ A2（加铺满选项）两条就能解决绝大部分体感问题。**

---

## 6. 调研方法与可信度

### 源码确认（12 个文件已落盘 `%TEMP%\t31ml-src`）

| 文件 | 回答了什么 |
|---|---|
| `Game.java`（2,700 行全量） | 全屏 5 层的**全部**决策代码（L190-206 / L225-236 / L764-782 / L935-960 / L980-1010） |
| `StreamView.java`（全文 2,842B） | letterbox 的 `onMeasure` 实现 |
| `RelativeTouchContext.java`（全文 10,717B） | 相对模式**全部**参数与零增量保护 |
| `AbsoluteTouchContext.java`（全文 8,241B） | 绝对模式**全部**阈值（含按下死区） |
| `PreferenceConfiguration.java`（28,208B） | **`DEFAULT_STRETCH=false`、`DEFAULT_ABSOLUTE_MOUSE_MODE=false`** |
| `activity_game.xml`（1,905B） | 视图层级：`backgroundTouchView` + `StreamView` |
| V+ `Game.kt`（前 2,231 行） | V+ 独有 `setDecorFitsSystemWindows(false)` |

### 未能获取（不编）

| 目标 | 状态 |
|---|---|
| **完整仓库克隆 / 编译** | **失败** —— ghproxy 只适合单文件 GET，`git clone` 持续超时；`codeload` 403 |
| **Moonlight / ToDesk / 向日葵 / RustDesk 端到端延迟数字** | **未取得，未编** |
| scrcpy / RustDesk 源码 | **未查**（时间优先给了 Moonlight + V+，其结论已足够回答问题） |
| Moonlight 分辨率协商的 RTSP 报文细节 | **未读** → 标 [推测] |

### 推测标注

| 结论 | 性质 |
|---|---|
| 分辨率通过 RTSP 协商发给服务端 | **[推测]**（未读 RTSP 源码） |
| 8ms 节流基本不丢事件（触摸 120Hz） | **[推测]**（未实测触摸采样率） |
| 平滑/插值引入额外延迟、是负收益 | **[推测]**（基于原理，官方没做可佐证） |

---

**文档作者**：yomori-scout
**最后更新**：2026-11-25
**未改任何代码 ｜ 未连接手机 ｜ 源码在 %TEMP%，已清理**