# 安卓 APP 协议设计与工程脚手架（ANDROID-APP-DESIGN）

> 日期：2026-11-23（更新）
> 作者：mobile-lead（初版，task `task_mwry4ouu12okjbg`）→ gpu-zero-copy（本次更新，task `task_mwsvbxhnrfbhbs`）
> 范围：**只读分析 + 设计文档**，不改业务代码、不动 dist/、不提交、不发版、不生成任何 Android 工程文件（本任务是设计 + 脚手架方案，不落地代码）。
> ★ 本次更新：P0 画面从 JPEG 改为 H.264 `/stream`（JPEG 已否决），后端动作已全部就绪，补 APP/网页同构定位与双鼠标模式。
> 前置：`MOBILE-REMOTE-PLAN-20261122.md`（路线）、`MOBILE-CONTROLS-DESIGN-20261122.md`（摇杆/键位/中文）、`STREAM-PLAN-V2-20261122.md` + `MEDIA-CAPABILITY-20261122.md`（抓屏/编码实测）

---

## 0. 结论摘要（先看这里）

1. **本机已装完整 Android 开发环境**（见 §1，验收标准里的「未安装任何 SDK」与实际不符）：`D:\Android\sdk`（platform android-34 + build-tools 34.0.0 + platform-tools + cmdline-tools）、JDK 17（Temurin 17.0.21）、Gradle 8.9。**可直接开工，无需装 SDK**。

2. **★ 画面渲染（2026-11-23 更新）：P0 直接用 H.264 `/stream` + `MediaCodec` 解码 + `SurfaceView` 渲染。JPEG `/live` 已明确否决（太卡），不再是 P0 方案。**
   - 理由（代码事实）：`/stream` 返回的是 **H.264 Annex-B 裸流**（`Relay/AgentWebServer.cs` L426 `Content-Type: video/h264` + L428 `SendChunked`，逐 NAL 写出），不是 JPEG、不是 MP4/自适应码流。安卓侧用 `MediaCodec`（decoder）直接消费 Annex-B 裸流（`outputSurface` 绑 `SurfaceView.getHolder().surface`），这是最低延迟路径，与桌面网页端 `WebCodecs VideoDecoder` 完全同构（网页用 WebCodecs、APP 用 MediaCodec，都是「裸 H.264 → 硬解码器 → Surface」）。
   - ExoPlayer（Media3）**用不上**：它是视频播放器（MP4/HLS/DASH/RTSP），播不了「逐 NAL 的 Annex-B 裸流」。历史录像 `/file/*.mp4` 才用 ExoPlayer。

3. **协议完全复用现有 HTTP 接口，零后端改动**（§2）：`GET /api/sessions`（拿 `directPort`+`directToken`）→ `GET http://主机:directPort/stream?token=…`（H.264 裸流）→ `POST /api/input`（键鼠注入，`X-SoulLink-Token` 头）。历史录像走主程序 `/file/*.mp4`（可用 ExoPlayer 播，Range 206 已支持）。

3b. **★ APP 与网页同构（2026-11-23 更新）**：APP 与 `web-index.html` 走**同一套协议**——`/stream`（画面）+ `/api/input`（输入）+ `/api/sessions`（会话/端口/令牌）。**唯一的差异是「画面渲染/解码方式」**：网页用 WebCodecs `VideoDecoder`，APP 用 `MediaCodec`。除此之外的协议、鉴权（两套 base URL + Cookie/`X-SoulLink-Token`）、串行/丢旧帧/按需 IDR 重同步语义**完全一致**，可互相参照实现与联调。

3c. **★ 后端输入动作已全部就绪（2026-11-23 更新）**：`/api/input` 的 `keydown`/`keyup`/`text`/`moverel` **已实现并接线**（`Relay/AgentWebServer.cs` L496-499 的 switch 分发），不再是「P2 阻塞」。P2（摇杆 + 中文）不再依赖后端改动，可直接做。

3d. **★ 双鼠标模式（月光式绝对/相对）已落地，作为 APP 远程操作参照（2026-11-23 更新）**：网页端已实现「绝对定位（`move`）+ 相对位移（`moverel`）」双模式（`web-index.html` L1234 注释「相对 = 月光语义：单指滑动 → moverel 转视角；单指点一下 → 绝对定位 + 左键点击」）。APP 的摇杆转视角直接复用 `moverel`（8ms 合并节流，见 `web-index.html` L1282-1291），点击走 `move`+`click`。这是 Sunshine/Moonlight 的「相对位移转视角」标准做法，已在桌面网页验证，APP 照抄即可。

4. **技术栈（已定）**：**Kotlin**（用户拍板）。View 层**混合**：**游戏画面/视频/摇杆用传统 View + `SurfaceView`**（MediaCodec 输出的标准接收端，用户倾向），**设置/账号列表用 Jetpack Compose**。网络 OkHttp，异步 Coroutines/Flow。Min SDK 26（Android 8.0），Target/Compile 34。详细取舍见 §3.3。

---

## 1. 环境实测（修正验收假设）

验收标准写「确认本机是否有 Android SDK；未安装任何 SDK」。**实测结果相反：已装齐。**

| 项 | 实测值 | 路径/命令 |
|---|---|---|
| Android SDK | ✅ 已装 | `D:\Android\sdk`（`ANDROID_HOME`=该路径） |
| 平台 | android-34 | `D:\Android\sdk\platforms\android-34` |
| Build Tools | 34.0.0 | `D:\Android\sdk\build-tools\34.0.0` |
| platform-tools (adb) | ✅ 已装 | `D:\Android\sdk\platform-tools\adb.exe` |
| cmdline-tools (sdkmanager) | ✅ 已装 | `D:\Android\sdk\cmdline-tools\latest\bin\sdkmanager.bat` |
| JDK | ✅ Temurin 17.0.21 | `C:\Program Files\Eclipse Adoptium\jdk-17.0.21.9-hotspot`（`JAVA_HOME` 已设） |
| Gradle | ✅ 8.9（Kotlin 1.9.23） | `D:\Android\gradle-8.9\bin\gradle.bat`（`GRADLE_HOME` 已设） |
| NDK | ❌ 未装 | 无 `D:\Android\sdk\ndk`（本阶段不需要，见 §7 未决） |
| 模拟器/真机 | 未检测 | 需 `adb devices` 或 Android Studio 建 AVD |

> 结论：**本任务产出的工程脚手架可在本机直接 `gradle assembleDebug`**（前提是后续实现阶段装 Gradle Wrapper，见 §3）。

---

## 2. 客户端协议定义 + 时序图

### 2.1 现有接口契约（已逐行核对，复用不改）

| 接口 | 方法 | 位置 | 请求 | 响应 |
|---|---|---|---|---|
| 会话列表 | GET | 主程序 `WebServer.cs` L360 → `ApiSessions` L726 | Cookie（登录后） | `{ok, version, themeDark, directToken, recordFps, recordQuality, recordKeepSeconds, sessions:[{id,user,nick,label,sessionId,bound,online,directPort,screen,...}]}` |
| 登录 | GET | 主程序 `WebServer.cs` L330 `/api/login?pw=…` | 口令 | 成功发 `Set-Cookie: soullink_sid=…`（1 年有效期） |
| 实时帧（画面） | GET | 代理 `AgentWebServer.cs` L271 `/stream` | `?token=` 或 `X-SoulLink-Token` | **video/h264** Annex-B 裸流（chunked，逐 NAL） |
| 键鼠注入 | POST | 代理 `AgentWebServer.cs` L182 `/api/input` | `X-SoulLink-Token` + JSON `{action,x,y,button,key,text,dx,dy,delta,count}` | `{ok, error}`（action 已支持 click/down/up/move/wheel/key/**keydown/keyup/text/moverel**） |
| 身份回显 | GET | 代理 L163 `/api/info` | token | `{ok,account,session,pid,port}` |
| 历史录像 | GET | 主程序 `WebServer.cs` L364 `/file/<rel>` | Cookie | `video/mp4`，支持 Range 206（`WriteStreamWithRange` L487） |

> ★ 关键差异（与桌面网页一致）：`/api/sessions` 走**主程序端口** + Cookie 鉴权；`/stream`、`/api/input`、`/api/info` 走**代理端口** + `X-SoulLink-Token`（`directToken` 由 sessions 下发）。安卓端必须维护**两套 base URL + 两套鉴权**。

### 2.2 登录时序

```
 安卓APP              主程序(:WebPort)                代理(:directPort)
   │  GET /api/login?pw=**** ──────────►
   │  ◄──────── 200 + Set-Cookie:soullink_sid ────┘
   │  GET /api/sessions (Cookie) ──────►
   │  ◄──────── { directToken, sessions:[{directPort,...}] } ──┘
   │  （选账号 acc）→ 记下 directPort + directToken
   │  GET http://host:directPort/stream?token=... ───────────►
   │  ◄──────────────── video/h264 裸流（chunked）───────────┘
```

### 2.3 画面解码时序（H.264 裸流 + MediaCodec，对齐网页端 WebCodecs）

```
 App 解码循环（MediaCodec decoder，outputSurface 绑 SurfaceView）
   │  GET /stream?token=... ──►  代理（chunked video/h264）
   │  ◄── Annex-B NAL 字节流（逐 NAL 到达）──┘
   │  攒 AU（access unit）：SPS/PPS/SEI/新 VCL = 上一个 AU 结束
   │    （网页端 web-index.html L962-966 同款 AU 攒批逻辑，APP 照抄）
   │  MediaCodec.queueInputBuffer(编码帧) → dequeueOutputBuffer → release 上屏
   │  ★ 丢旧帧 + 按需 IDR：decodeQueue 积压 → 丢 delta AU，等下一个 IDR
   │    （网页端 L983-1004 SKIP_QUEUE=5 / SKIP_RESET_LIMIT=60 同款语义）
```

> 要点：**AU 攒批**（WebCodecs/MediaCodec 都要求完整 access unit，逐 NAL 喂会解不出）、**丢旧帧**（积压时丢 delta、只放行 key）、**按需 IDR 重同步**（积压到阈值 reset + 等下个 IDR）——三者是 `web-index.html` L960-1004 已有的语义，APP 用 MediaCodec 等价实现，否则黑屏/花屏/累积延迟。
### 2.4 输入时序（点击 / 摇杆 / 键位）

```
 触摸事件 → 手势映射器（MOBILE-CONTROLS-DESIGN §1/§2）
   │  单指点/拖 → {action:"click"/"down"/"up"/"move", x,y, button}
   │  双指滚轮 → {action:"wheel", delta}
   │  摇杆方向 → {action:"keydown"/"keyup", key:"W+D"}  (已就绪)
   │  转视角   → {action:"moverel", dx, dy}            (已就绪, 8ms 合并节流)
   │  中文     → {action:"text", text:"..."}            (已就绪)
   ▼
  POST http://host:directPort/api/input
     Headers: X-SoulLink-Token, Content-Type: application/json
     Body: {action, x, y, button, key, delta, count, text?}
```

> ★ 摇杆/中文的 `keydown`/`keyup`/`text` 以及转视角 `moverel` **已全部实现接线**（`Relay/AgentWebServer.cs` L496-499）。APP 无需预留协议缺口，直接按现有动作开发；`moverel` 是摇杆转视角的正确动作（相对位移，月光式）。

---

## 3. 技术栈推荐（Kotlin 已定）

### 3.1 核心结论表

| 层 | 推荐 | 理由 |
|---|---|---|
| 语言 | **Kotlin**（用户拍板，不再纠结 Java/传统 View） | 协程/Flow 天然契合「串行拉流 + 丢旧帧」 |
| 网络 | **OkHttp** | 细粒度控制连接池/超时/拦截器；自带 token 拦截器；比 HttpURLConnection 好测 |
| 异步 | **Coroutines + Flow** | 单协程串行拉帧天然实现「上一帧没回不再发」 |
| 视频解码（当前 H.264 裸流） | **MediaCodec（硬解码器）** | 见 §3.2 |
| 视频播放（历史录像） | **ExoPlayer（Media3）** | `/file/*.mp4` 是完整 MP4 + Range 206，ExoPlayer 直接播 |
| **View 层** | **混合**：游戏画面/视频渲染用**传统 View + SurfaceView**；设置/账号列表用 **Compose** | 见 §3.3 |

### 3.3 ★ View 层取舍（Kotlin 前提下，用户倾向传统 View + SurfaceView）

**结论：画面/视频/摇杆浮层用传统 View + `SurfaceView`，设置/账号列表等静态界面用 Compose，混合开发。**

| 场景 | 推荐 | 理由 |
|---|---|---|
| **实时画面渲染** | **传统 View + `SurfaceView`**（或 `TextureView`） | `SurfaceView` 是 MediaCodec/`MediaPlayer` 输出的标准接收端，双缓冲、独立 Surface 层、渲染不卡 UI 线程。Compose 里包 SurfaceView 要 `AndroidView` 桥接，多一层复杂度且帧同步/生命周期要自己管 |
| **未来 H.264 流** | **SurfaceView**（MediaCodec 输出目标） | 裸 MediaCodec 的 `outputSurface` 直接绑 `SurfaceView.getHolder().surface`，这是最低延迟路径；ExoPlayer 也是绑 SurfaceView/TextureView 的 |
| **虚拟摇杆/键位浮层** | **传统 View**（自定义 `View` 重写 `onTouchEvent` + `onDraw`） | 摇杆是高频触摸 + 自定义绘制，传统 `onTouchEvent` 拿原始 `MotionEvent` 最直接、无 Compose 手势系统开销 |
| **登录/设置/账号列表** | **Jetpack Compose** | 静态表单/列表，声明式最快，Material3 现成组件 |
| **混合边界** | 主容器用 `Fragment`/`View`，静态页用 `ComposeView` 嵌入 | 业界成熟做法：一个 Activity，画面区传统 View，设置区 `setContent { }` |

> ★ 用户说「界面部分（设置、账号列表）可以用 Compose」——与本结论一致。**核心原则：跟手/低延迟相关的（画面、摇杆）走传统 View + SurfaceView，与跟手无关的（表单、列表）走 Compose。**

### 3.2 ★ ExoPlayer vs MediaCodec 的取舍（验收重点）

**结论：当前（JPEG 阶段）两者都不用，用 `BitmapFactory.decodeStream`；未来（H.264 流阶段）用 ExoPlayer 或 MediaCodec，首选 ExoPlayer（Media3）。**

| 维度 | ExoPlayer (Media3) | MediaCodec（裸） | 说明 |
|---|---|---|---|
| 适用输入 | 完整视频流（MP4/HLS/DASH/RTSP） | 裸编码帧（H.264 Annex-B） | **单帧图片（JPEG/PNG/WebP）** |
| 当前 `/stream`（H.264 Annex-B 裸流） | ❌ 播不了（非 MP4/HLS/DASH 封装流） | ✅ **直接用**（decoder 吃 Annex-B） | ❌ 不适用（非图片） |
| 历史 `/file/*.mp4` | ✅ 直接播（Range 206 已支持） | 需要自己解封装+喂帧 | ❌ 只能解单帧 |
| 实时 `/stream`（H.264 裸流） | ❌ 无封装层，ExoPlayer 播不了 | ✅ **首选**（outputSurface 直绑 SurfaceView，最低延迟） | ❌ 不适用 |
| 复杂度 | 中（API 高层） | 高（底层状态机） | 低 |
| 低延迟控制 | 中（可调 buffer，但有封装层） | 高（完全可控） | 高（就是逐帧） |

**分阶段结论**：
- **P0（当前，H.264 `/stream`）**：`MediaCodec`（H.264 decoder）`outputSurface` 绑 `SurfaceView.getHolder().surface`，喂 Annex-B NAL → 硬解码 → Surface 上屏。**首选 MediaCodec，不是 ExoPlayer**（ExoPlayer 播不了裸 Annex-B 流）。这是对「现有 `/stream` 是 H.264 裸流」这一代码事实的唯一正确映射。
- **P1（历史录像）**：`/file/*.mp4` 是完整 MP4 + Range 206，**用 ExoPlayer（Media3）**直接播。这是 ExoPlayer 在本项目唯一的使用场景。

---

## 4. 工程结构目录树

```
SoulLinkAndroid/
├─ settings.gradle.kts              # 含 pluginManagement / dependencyResolutionManagement
├─ build.gradle.kts                 # 顶层（plugins apply false）
├─ gradle.properties                # android.useAndroidX=true, jvmargs
├─ gradle/
│  ├─ wrapper/gradle-wrapper.properties   # 锁定 Gradle 8.9
│  └─ libs.versions.toml            # 版本目录（version catalog）
├─ gradlew / gradlew.bat            # wrapper（用本机 D:\Android\gradle-8.9 生成）
├─ local.properties                 # sdk.dir=D:\Android\sdk （不入库，gitignore）
├─ app/
│  ├─ build.gradle.kts              # 模块配置（见下）
│  └─ src/main/
│     ├─ AndroidManifest.xml        # INTERNET / 网络明文 / 横竖屏
│     ├─ kotlin/com/soullink/android/
│     │  ├─ MainActivity.kt
│     │  ├─ ui/
│     │  │  ├─ theme/Theme.kt
│     │  │  ├─ login/LoginScreen.kt           # Compose
│     │  │  ├─ session/SessionListScreen.kt   # Compose
│     │  │  └─ viewer/
│     │  │     ├─ ViewerFragment.kt           # 传统 View 容器（画面+摇杆）
│     │  │     ├─ SurfaceRenderView.kt        # 传统 View：SurfaceView 承载画面
│     │  │     ├─ VirtualControlOverlay.kt    # 传统 View：摇杆/键位浮层(onTouchEvent+onDraw)
│     │  │     └─ ReplayView.kt               # ExoPlayer + SurfaceView
│     │  ├─ net/
│     │  │  ├─ SoulLinkApi.kt       # OkHttp 客户端 + 两套 base URL
│     │  │  ├─ AuthInterceptor.kt   # Cookie（主程序） + X-SoulLink-Token（代理）
│     │  │  └─ dto/Dtos.kt          # SessionDto/InputAction/... 
│     │  ├─ stream/
│     │  │  ├─ H264Stream.kt        # 串行拉流协程 + AU 攒批 + 丢旧帧 + 退避
│     │  │  └─ H264Decoder.kt       # MediaCodec decoder 封装 → SurfaceView 上屏
│     │  ├─ input/
│     │  │  ├─ InputSender.kt       # POST /api/input
│     │  │  ├─ GestureMapper.kt     # 触摸 → InputAction（摇杆/点按/双指）
│     │  │  ├─ Joystick.kt          # 摇杆数学（MOBILE-CONTROLS-DESIGN §1）
│     │  │  └─ VirtualKeys.kt       # 虚拟修饰键/键位布局
│     │  ├─ controls/
│     │  │  ├─ LayoutConfig.kt      # 布局 JSON 解析（schema 见 MOBILE-CONTROLS-DESIGN §3）
│     │  │  └─ LayoutEditor.kt      # 编辑模式
│     │  └─ util/ConnectivityWatcher.kt  # online/offline/visibility → 断线释放
│     └─ res/  (values/ drawable/ xml/network_security_config.xml)
```

### 4.1 `app/build.gradle.kts`（要点）

```kotlin
plugins { id("com.android.application"); id("org.jetbrains.kotlin.android") }
android {
  namespace = "com.soullink.android"
  compileSdk = 34
  defaultConfig { minSdk = 26; targetSdk = 34 }
  buildFeatures { compose = true }   // 只给静态界面用；画面/摇杆走传统 View，不强制全 Compose
}
dependencies {
  implementation(platform("androidx.compose:compose-bom:2024.x"))
  implementation("androidx.compose.ui:ui"); implementation("androidx.compose.material3:material3")
  implementation("androidx.compose.ui:ui-tooling-preview")
  implementation("com.squareup.okhttp3:okhttp:4.12.0")
  implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.8.x")
  implementation("androidx.media3:media3-exoplayer:1.4.x")   // 历史录像 + SurfaceView
  implementation("androidx.media3:media3-ui:1.4.x")          // PlayerView（可选）
  implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.x")
}
```

---

## 5. 核心类骨架（Kotlin 伪代码）

### 5.1 `SoulLinkApi`（两套 base URL）

```kotlin
class SoulLinkApi(host: String, mainPort: Int) {
    private val mainBase = "http://$host:$mainPort"        // 主程序
    // 代理 base 运行时按 directPort 拼
    private val client = OkHttpClient.Builder()
        .connectTimeout(5, SECONDS).readTimeout(10, SECONDS).build()

    suspend fun login(pw: String): Boolean            // GET /api/login?pw=（WebServer.cs L330 是 GET，非 POST）
    suspend fun sessions(): SessionListDto            // GET /api/sessions（带 Cookie）
    fun streamUrl(directPort: Int, token: String) = "http://$host:$directPort/stream?token=$token"
    suspend fun input(directPort: Int, token: String, a: InputAction): Boolean
    suspend fun info(directPort: Int, token: String): InfoDto
    fun replayUrl(path: String) = "$mainBase/file/$path"   // ExoPlayer 播
}
```

### 5.2 `H264Stream`（串行拉流 + AU 攒批 + 丢旧帧 + MediaCodec，对齐网页 WebCodecs）

```kotlin
class H264Stream(private val api: SoulLinkApi, private val scope: CoroutineScope) {
    // MediaCodec H.264 decoder；outputSurface 绑 SurfaceView.getHolder().surface
    private val decoder = MediaCodec.createDecoderByType("video/avc")
    @Volatile var running = false

    fun start(port: Int, token: String) {
        running = true
        scope.launch(Dispatchers.IO) {
            // 读 /stream chunked 流 → 攒 AU（SPS/PPS/新 VCL = AU 边界）
            //   → queueInputBuffer(编码帧, 时间戳) → dequeueOutputBuffer → release 上屏
            // 丢旧帧 + 按需 IDR：decodeQueue 积压丢 delta AU，reset 后等下个 IDR
        }
    }
    fun stop() { running = false; decoder.stop(); decoder.release() }
}
```

> ★ 语义与网页端完全同构：网页 WebCodecs 的「AU 攒批 + SKIP_QUEUE 丢 delta + reset 等下个 IDR」（`web-index.html` L960-1004），APP 用 MediaCodec 等价实现。唯一的区别是解码器 API（WebCodecs vs MediaCodec），协议/丢帧/重同步语义一字不差。
### 5.3 `SurfaceRenderView`（传统 View，承载画面）

```kotlin
class SurfaceRenderView(context: Context) : SurfaceView(context), SurfaceHolder.Callback {
    // ★ 当前：holder.surface 直接作为 MediaCodec 的 outputSurface（硬解码上屏）
    // SurfaceView 双缓冲：上屏不卡 UI 线程，丢帧即覆盖旧帧
    // 未来扩展：如需叠加虚拟摇杆/键位，用 TextureView 或单独浮层
}
```

### 5.4 `InputAction` + `InputSender`（动作全部已就绪）

```kotlin
sealed class InputAction {
    data class Click(x: Double, y: Double, button: String = "left") : InputAction()
    data class Down(button: String) : InputAction()
    data class Up(button: String) : InputAction()
    data class Move(x: Double, y: Double) : InputAction()          // 绝对定位
    data class MoveRel(dx: Int, dy: Int) : InputAction()           // ★ 相对位移（转视角，月光式）
    data class Wheel(delta: Int) : InputAction()
    data class Key(combo: String) : InputAction()
    data class KeyDown(combo: String) : InputAction()              // ★ 已就绪（摇杆按住）
    data class KeyUp(combo: String) : InputAction()                // ★ 已就绪（摇杆松开）
    data class Text(text: String) : InputAction()                  // ★ 已就绪（中文输入）
}
// InputSender.input(directPort, token, action) 序列化为 JSON POST，带 X-SoulLink-Token
// ★ 摇杆转视角发 MoveRel（映射 moverel），点击发 Move+Click；keydown/keyup/text 已接后端
```

> ★ 相对位移 `moverel` 是摇杆转视角的正确动作（Sunshine/Moonlight 标准做法）：游戏读 Raw Input 相对位移量，绝对定位对 3D 游戏无效。网页端已实现（`web-index.html` L1282-1291 的 lookDelta + 8ms 合并节流），APP 的 Joystick 输出直接映射 `MoveRel`。
### 5.5 `Joystick`（MOBILE-CONTROLS-DESIGN §1 数学的 Kotlin 版，自定义 View 重写 onTouchEvent）

```kotlin
class JoystickView(context: Context) : View(context) {
    // 重写 onTouchEvent：ACTION_DOWN/MOVE/UP → joystickVector() → 回调方向/幅度
    val joystick = Joystick(radiusPx, deadzonePx)
    fun vector(cx: Float, cy: Float, tx: Float, ty: Float): JoystickVector {
        val dx = tx - cx; val dy = ty - cy
        val m = hypot(dx, dy)
        if (m <= deadzonePx) return JoystickVector(Sector.CENTER, 0f, 0f, 0f)
        val mag = ((m - deadzonePx) / (radiusPx - deadzonePx)).coerceIn(0f, 1f)
        return JoystickVector(sector(dx/m, dy/m), mag, dx/m, dy/m)
    }
    // sector(): atan2 → 8 方向（见 MOBILE-CONTROLS-DESIGN §1.3）
}
```

### 5.6 `ConnectivityWatcher`（断线/锁屏释放，对齐 MOBILE-CONTROLS-DESIGN §6）

```kotlin
class ConnectivityWatcher(private val onLost: () -> Unit) {
    // 监听 Lifecycle ON_PAUSE/ON_STOP + ConnectivityManager network callback
    // 触发 onLost → InputSender.releaseAllHeld()（发 keyup/up）
}
```

---

## 6. 与现有接口的对接方式（逐条）

| 现有接口 | 安卓端对接 | 鉴权 | 备注 |
|---|---|---|---|
| `/api/login` | OkHttp POST，保存 `Set-Cookie` 到 CookieJar | 口令 | 复用桌面网页登录逻辑 |
| `/api/sessions` | GET，解析 `directPort`/`directToken` | Cookie | 拿到代理 base + token |
| `/stream` | `H264Stream` 串行 GET，`MediaCodec` 解码 + SurfaceView | `?token=` | **H.264 Annex-B 裸流（chunked）** |
| `/api/input` | `InputSender` POST JSON | `X-SoulLink-Token` 头 | click/down/up/move/wheel/key/keydown/keyup/moverel/text 全已实现 |
| `/api/info` | 可选：连接后 GET 校验 `account` 匹配 | token | 调试/防串号 |
| `/file/*.mp4` | ExoPlayer `MediaItem.fromUri` | Cookie | Range 206 已支持，直接播 |

> ★ **防串号**：`/api/sessions` 返回的 `directPort` 按账号分配（`WebDirect.cs` L45-53），安卓端必须用**选中账号的 directPort + 对应 directToken**，不能拿 A 账号的 token 打 B 账号的端口（`/api/info` 的 `account` 字段可校验）。

---

## 7. 分阶段计划

| 阶段 | 内容 | 依赖 | 交付 |
|---|---|---|---|
| **P0（脚手架 + 能看）** | 工程骨架（§4）+ 登录 + 会话列表 + 单账号画面（**H.264 `/stream` + MediaCodec**）+ 串行/AU攒批/丢旧帧 | 本机已装 SDK | 能 `assembleDebug` + 真机看实时 H.264 画面 |
| **P1（能操作）** | 手势映射（点击/拖动/滚轮）+ 键位布局（`LayoutConfig`）+ 虚拟摇杆 + 编辑模式 | 已有 click/move/wheel | 手机能点、拖、滚 |
| **P2（摇杆 + 中文）** | `keydown`/`keyup`/`text`/`moverel` 动作对接 + 断线释放 | **后端动作已全部就绪**，无阻塞 | 摇杆移动/视角（moverel 相对位移）+ 中文输入 |
| **P3（历史录像）** | ExoPlayer 播 `/file/*.mp4` | 无 | 看回放 |
| ~~P4（可选）~~ | ~~H.264 实时流~~ 已并入 P0（`/stream` 已上线） | 无 | 无需再立项 |

---

## 8. 未决问题（需 Lead 决策）

1. ~~后端动作何时做~~ **已解决（2026-11-23）**：`keydown`/`keyup`/`text`/`moverel` 已全部实现接线（`Relay/AgentWebServer.cs` L496-499）。P2（摇杆 + 中文）无后端阻塞，可直接开发。
2. **工程放哪个目录**：已确认目录 `mobile/SoulLinkAndroid/`，但**暂缓建骨架**（2026-11-23 更新）。等画面/摇杆实现任务启动时再落地，避免「不囤积构建产物」的 AGENTS.md 整洁规则被过早破坏。
3. **明文 HTTP 问题**：安卓 9+ 默认禁止明文 HTTP（`cleartextTraffic`），且 `/stream`/`/api/input` 是明文 + token。局域网内需 `network_security_config` 放行明文；外网必须走 §（MOBILE-REMOTE-PLAN §5.4）的隧道/TLS。**是否先按「局域网明文」做（加 cleartext 白名单），外网安全留到发布前？**
4. **NDK 未装**：本阶段 JPEG 解码用 Java 层 `BitmapFactory`，不需要 NDK。仅当未来要做自研 H.264 低延迟解码或 FEC 时才需要，届时再装。

---

## 9. 相关文档

- `MOBILE-REMOTE-PLAN-20261122.md` —— 路线 / 手势 / 最小改动集（含 `text`/`moverel`/CORS/令牌）
- `MOBILE-CONTROLS-DESIGN-20261122.md` —— 摇杆数学 / 键位 JSON / 中文上屏 / 断线释放
- `STREAM-PLAN-V2-20261122.md` + `MEDIA-CAPABILITY-20261122.md` —— 抓屏/编码实测（JPEG vs H.264）
- `NVENC-SDK-INTEGRATION-20261123.md` + `NVENC-SDK-RCPARAMS-20261123.md` + `NVENC-SDK-QUALITY-PRESETS-20261123.md` —— 后端 H.264 硬编（NVENC SDK 直通）已就绪，`/stream` 输出 H.264 Annex-B
- `REMOTE-ACCESS-OPTIONS-20261123.md` §1.3 —— 后端 4 动作（keydown/keyup/text/moverel）已核实全部实现
- `MATURE-LATENCY-PLAYBOOK-20261123.md` —— 采集节拍/编码参数/解码丢帧/输入解耦的成熟做法（APP 的 MediaCodec 丢帧语义对照）
- `Relay/AgentWebServer.cs`、`Services/WebServer.cs` —— HTTP 接口实现
- `AGENTS.md` —— 性能约束 / 工作区整洁
