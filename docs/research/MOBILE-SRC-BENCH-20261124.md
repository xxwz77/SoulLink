# 移动端「全屏 + 触摸」源码级实证（MOBILE-SRC-BENCH）

> 成员：verifier（代码级实证，与 yomori-scout 的产品对比并行）
> 日期：2026-11-24（00:00~00:25 执行）
> 对象：moonlight-stream/moonlight-android（master）+ Genymobile/scrcpy（master）
> 方法：动手克隆 + 编译（如实记录失败）+ 逐文件读源码（文件:行号）。

---

## 〇、编译结果（如实记录）

### Moonlight Android：编译失败（环境版本不匹配，非源码错误）

克隆：直连 github 被 reset（`Recv failure: Connection was reset`），ghproxy 克隆同样反复 reset、且残留 `.git` 锁。
**改用 codeload tarball 成功**：`https://codeload.github.com/moonlight-stream/moonlight-android/tar.gz/refs/heads/master`（17,277,593 字节），解压后 `app/build.gradle` 完整。

编译命令：`gradle assembleNonRootDebug --no-daemon`，失败在**构建配置评估阶段**（非编译源码）：

```
FAILURE: Build failed with an exception.
* Where:
Build file '...\moonlight-android-master\app\build.gradle' line: 1
* What went wrong:
A problem occurred evaluating project ':app'.
> Failed to apply plugin 'com.android.internal.version-check'.
>    Minimum supported Gradle version is 9.6.0. Current version is 8.9.
```

版本缺口（app/build.gradle 行号，已逐行核对）：

| 需求 | 我们本机 | 结论 | 出处 |
|---|---|---|---|
| AGP `com.android.tools.build:gradle:9.4.0`（要求 Gradle 9.6+） | Gradle 8.9 | 不满足 | 根 build.gradle:8 / app/build.gradle:1 |
| `compileSdk 37` | android-34 | 不满足 | app/build.gradle:8 |
| `ndkVersion 29.0.14206865` | 无 NDK | 不满足 | app/build.gradle:4 |
| `targetSdk 36` / `minSdk 21` | — | 需 SDK 36 平台 | app/build.gradle:11-12 |

**结论**：本机缺 Gradle 9.6 / compileSdk 37 / NDK 29 三样，无法编译。源码本身完整（tarball 提取 100% 成功），**不影响源码阅读与结论提取**。按任务硬性要求如实记录，不编造「编译成功」。

---

## 一、「全屏」到底要做什么（源码确认，逐项）

Moonlight 官方 `Game.java` 的全屏实现（app/src/main/java/com/limelight/Game.java）：

1. **隐藏标题栏/状态栏/导航栏**：
   - `Game.java:191 requestWindowFeature(Window.FEATURE_NO_TITLE)`
   - `Game.java:194 getWindow().addFlags(FLAG_FULLSCREEN)`
   - `Game.java:994-1000` 沉浸式：`SYSTEM_UI_FLAG_LAYOUT_STABLE | LAYOUT_HIDE_NAVIGATION | LAYOUT_FULLSCREEN | HIDE_NAVIGATION | FULLSCREEN | IMMERSIVE_STICKY`
2. **允许布局延伸到系统栏区域**（`LAYOUT_STABLE | LAYOUT_HIDE_NAVIGATION | LAYOUT_FULLSCREEN`，Game.java:198-201）+ `FLAG_LAYOUT_IN_SCREEN`（Game.java:203）。
3. **铺满到刘海/挖孔下面**（★我们缺）：
   - `Game.java:225` `if (stretchVideo || shouldIgnoreInsetsForResolution)` →
   - `Game.java:229-230` API R+：`layoutInDisplayCutoutMode = LAYOUT_IN_DISPLAY_CUTOUT_MODE_ALWAYS`
   - `Game.java:233-234` API P+：`LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES`
   - **注意：官方默认 `stretchVideo=false`（PreferenceConfiguration.java:74 `DEFAULT_STRETCH=false`）**，即默认不铺满刘海，只有用户开「拉伸/全屏」或原生分辨率时才对刘海铺满。
4. **视频铺满整屏 vs 保持比例**：
   - `Game.java:953` `if (stretchVideo || aspectRatioMatch)` → `setFixedSize(w,h)`（拉伸铺满，不留黑边）
   - `Game.java:957-959` else → `streamView.setDesiredAspectRatio(w/h)`（保持比例 letterbox，留黑边）
   - **用户说的「全屏」最可能是「stretchVideo 拉伸铺满」** —— 官方把它做成用户可选项，默认关。
5. **屏幕比例 ≠ 视频比例**：`StreamView.onMeasure`（ui/StreamView.java:30-60）—— 按 `desiredAspectRatio` 计算「宽或高对齐后另一维按比例」，即 letterbox 居中，不拉伸、不裁剪。**scrcpy 提供三档**：`SC_RENDER_FIT_LETTERBOX`（默认，保持比例留黑边）/ `SC_RENDER_FIT_STRETCHED`（拉伸铺满）/ `SC_RENDER_FIT_UNSCALED`（1:1 不缩放）（options.h:251-255）。

**★ 逐项结论**：
- 隐藏状态/导航栏：我们已做；
- **铺满刘海（cutout ALWAYS）**：官方默认不做、作为「拉伸」选项的一部分；我们缺；
- **拉伸铺满 vs 比例**：官方默认保持比例（letterbox），「拉伸铺满」是显式选项；**用户要的「全屏」如果指「不留黑边」，对应 stretchVideo=true 或 scrcpy 的 STRETCHED**；
- **比例不一致处理**：moonlight 用 letterbox（StreamView.onMeasure），scrcpy 用三档 render_fit；**两者都默认「保持比例留黑边」，拉伸是选项，不是默认**。

---

## 二、移动端视频渲染该用什么 View（源码确认）

### Moonlight：SurfaceView（自定义 StreamView extends SurfaceView）

- `ui/StreamView.java:9` `public class StreamView extends SurfaceView`；
- 宽高比通过 `onMeasure` 覆写（StreamView.java:30-60）；
- 解码输出：`MediaCodecDecoderRenderer.java:519 configure(format, renderTarget.getSurface(), ...)`，`renderTarget` 是 `SurfaceHolder`（:70）。

### 各 View 在旋转/缩放/全屏的坑（综合 moonlight/scrcpy 源码）：

| View | 旋转 | 缩放/裁剪铺满 | 全屏 | 结论 |
|---|---|---|---|---|
| SurfaceView | 不随窗口自动变换（需手动 setRotation/调整尺寸） | 难做「裁剪铺满」（Surface 尺寸由系统按固定尺寸分配，要裁剪得在测量里自己算，moonlight 用 onMeasure 只做 letterbox 不做裁剪） | 直接，activity 全屏即可 | moonlight 选择它（StreamView.java:9） |
| TextureView | 支持 View 变换矩阵（setTransform 旋转/缩放/平移，易做裁剪铺满） | **最容易实现「裁剪铺满」**：用一个 matrix scale 到 cover | 直接 | scrcpy 用 SDL Texture（等价 TextureView 的纹理缩放，screen.c:311 SDL_RenderTexture） |
| GLSurfaceView | 需自己管 EGL 上下文与变换 | 需写 shader/matrix | 直接 | 游戏引擎场景，串流不必 |

**★ 结论**：
- **「保持比例留黑边」用 SurfaceView 足够**（moonlight 官方做法）；
- **「裁剪铺满（不留黑边、裁掉超出部分）」用 TextureView 最容易**（setTransform 一个 matrix 即可，SurfaceView 做不到干净的裁剪）；
- scrcpy 的桌面端等价做法是 SDL_Texture + `SC_RENDER_FIT_STRETCHED` 拉伸（screen.c:212-217），裁剪铺满在移动端对应「TextureView + scale-to-cover matrix」。

---

## 三、触摸手感的正确做法（源码确认）

### 3.1 绝对 vs 相对：moonlight 默认「相对」（远程桌面触控），绝对可切换

- 两套实现：`binding/input/touch/AbsoluteTouchContext.java` 与 `RelativeTouchContext.java`（touch/ 目录，源码确认）。
- **默认 `absoluteMouseMode=false`**（PreferenceConfiguration.java:104 `DEFAULT_ABSOLUTE_MOUSE_MODE=false`）→ 用 RelativeTouchContext。
- **相对模式（默认）**：touchMove 发增量 `sendMouseMove(deltaX, deltaY)`（RelativeTouchContext.java:207-211），不锁绝对坐标，适合「把手指当触摸板拖动光标」。
- **绝对模式（可选项）**：touchMove 发 `sendMousePosition(x,y,w,h)`（AbsoluteTouchContext.java:113-116 `updatePosition`），适合「手指点到哪光标到哪」（远程桌面最跟手）。

### 3.2 阈值（源码常量，直接可抄）

AbsoluteTouchContext.java：
- 长按判定：`LONG_PRESS_TIME_THRESHOLD=650ms`、`LONG_PRESS_DISTANCE_THRESHOLD=30px`（:60-61）
- 双击：`DOUBLE_TAP_TIME_THRESHOLD=250ms`、`DOUBLE_TAP_DISTANCE_THRESHOLD=60px`（:63-64）
- 按下死区（防抖动误判为拖动）：`TOUCH_DOWN_DEAD_ZONE_TIME_THRESHOLD=100ms`、`..._DISTANCE_THRESHOLD=20px`（:66-67）

RelativeTouchContext.java：
- 点击 vs 拖动：`TAP_MOVEMENT_THRESHOLD=20px`、`TAP_DISTANCE_THRESHOLD=25px`、`TAP_TIME_THRESHOLD=250ms`（:78-80）
- 长按开始拖动：`DRAG_TIME_THRESHOLD=650ms`（:81）
- 双指滚动速度因子：`SCROLL_SPEED_FACTOR=5`（相对）/`=3`（绝对）（RelativeTouchContext.java:83 / AbsoluteTouchContext.java:56）

### 3.3 节流 / 平滑 / 预测

- **moonlight 客户端不做节流**（每次 touchMove 都发，靠服务端/输入通道批量聚合）；**没有本地指针预测/平滑**（源码无 predictor/smoothing/velocity 相关代码）。
- **我们的 8ms 节流已比 moonlight 更激进**（moonlight 不节流、全量发 delta，靠 ENet 可靠通道保证顺序）；问题不在「节流不够」，而在我们「每个 move 一条独立 HTTP POST 无序」（AUDIT-06 §2.3 已诊断）。
- **点击长按拖动阈值**：moonlight 用「按下死区 20px/100ms → 超 20px 判定为 move/拖动」的两段式，而不是单阈值。

### 3.4 scrcpy 的触摸（远控正确做法佐证）

- scrcpy 触摸 = 直接注入 Android 触摸事件（`SC_CONTROL_MSG_TYPE_INJECT_TOUCH_EVENT`，input_manager.c:384），坐标归一化 0~1（`SDL touch event coordinates are normalized in the range [0;1]`，input_manager.c:799）。
- **绝对坐标 + 归一化 0~1**（不是像素），服务端按设备分辨率换算 —— 这正是远控软件的「正确做法」，对应我们「相对值 0~1」的现状但**我们缺了 down/up 保序**。

---

## 四、可落地结论（A/B/C 三档）

### A 档（今天可做，改布局/配置）

| 项 | 解决症状 | 代价 | 出处 |
|---|---|---|---|
| 加 `layoutInDisplayCutoutMode = SHORT_EDGES/ALWAYS`（API 28+/30+） | 画面不铺满刘海，顶部留黑条 | 半小时 | Game.java:225-234 |
| 视频渲染加「stretchVideo」拉伸铺满选项（默认关，对应用户「全屏」） | 黑边 | 1 小时 | Game.java:953 + StreamView.onMeasure |
| 触摸加「绝对模式」开关（默认相对），对应远控最跟手 | 远控落点不准 | 2 小时 | AbsoluteTouchContext.java 整套 + PreferenceConfiguration |
| 点击/长按/拖动阈值照抄 | 点击误判拖动、长按无右键 | 1 小时 | Absolute/RelativeTouchContext 常量 |

### B 档（一两天，改触摸协议）

| 项 | 解决症状 | 代价 | 出处 |
|---|---|---|---|
| down/up 保序（入单队列，同 moonlight 输入通道） | 拖动落点偏移、up 丢失 | 1 天 | 参照 moonlight InputStream.c 批量聚合 + ENet 可靠 |
| 双指滚动手势（pointerCount==2 → scroll） | 手机滚动难用 | 半天 | RelativeTouchContext.java:213-215 |

### C 档（架构级，缓）

| 项 | 解决症状 | 代价 | 出处 |
|---|---|---|---|
| 换 TextureView + scale-to-cover 做「裁剪铺满」 | 极致全屏无黑边（裁掉不拉伸） | 大 | scrcpy render_fit 三档 + TextureView matrix |

---

## 五、编译/环境诚实记录与红线

- 编译失败卡在：Gradle 8.9 < 要求的 9.6（AGP 9.4.0）+ compileSdk 37（本机 34）+ 无 NDK 29。**已如实记录，未编造成功**。
- 未改 SoulLink 代码；未连接用户手机；临时目录 `%TEMP%\ml-bench` / `ml-src` / `sunshine-src` / `moonlight-src` 已全部删除。
- 源码通过 codeload tarball（成功）+ raw.githubusercontent + GitHub API contents 三种方式取得，结论均可复现（文件:行号）。
