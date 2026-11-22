# 安卓 APP 协议设计与工程脚手架（ANDROID-APP-DESIGN）

> 日期：2026-11-22
> 作者：mobile-lead（agent-team / task `task_mwry4ouu12okjbg`）
> 范围：**只读分析 + 设计文档**，不改业务代码、不动 dist/、不提交、不发版、不生成任何 Android 工程文件（本任务是设计 + 脚手架方案，不落地代码）。
> 前置：`MOBILE-REMOTE-PLAN-20261122.md`（路线）、`MOBILE-CONTROLS-DESIGN-20261122.md`（摇杆/键位/中文）、`STREAM-PLAN-V2-20261122.md` + `MEDIA-CAPABILITY-20261122.md`（抓屏/编码实测）

---

## 0. 结论摘要（先看这里）

1. **本机已装完整 Android 开发环境**（见 §1，验收标准里的「未安装任何 SDK」与实际不符）：`D:\Android\sdk`（platform android-34 + build-tools 34.0.0 + platform-tools + cmdline-tools）、JDK 17（Temurin 17.0.21）、Gradle 8.9。**可直接开工，无需装 SDK**。

2. **画面渲染的硬结论：当前阶段 ExoPlayer 用不上，用 `BitmapFactory`/`ImageDecoder` + 自绘 `ImageView`（或 `SurfaceView`）即可；MediaCodec 是为「未来 H.264 流」预留的升级点，不是现在。**
   - 理由（代码事实）：现有 `/live` 返回的是**单帧 JPEG 图片**（`Relay/AgentWebServer.cs` L176-179 → `SessionRecorder.GetLatestJpeg()`，`Content-Type: image/jpeg`），**不是 H.264/MP4/自适应码流**。ExoPlayer（Media3）是视频播放器，播不了「逐帧 JPEG HTTP 端点」。
   - 等价于桌面网页端 `web-index.html` L770-787 的 `fetch → blob → objectURL → img.src`，安卓侧用 `OkHttp` + `BitmapFactory.decodeStream` + `ImageView.postInvalidate` 即可，成本最低。

3. **协议完全复用现有 HTTP 接口，零后端改动**（§2）：`GET /api/sessions`（拿 `directPort`+`directToken`）→ `GET http://主机:directPort/live?token=…`（JPEG）→ `POST /api/input`（键鼠注入，`X-SoulLink-Token` 头）。历史录像走主程序 `/file/*.mp4`（可用 ExoPlayer 播，Range 206 已支持）。

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
| 实时帧 | GET | 代理 `AgentWebServer.cs` L176 `/live` | `?token=` 或 `X-SoulLink-Token` | **image/jpeg** 单帧（内存缓冲） |
| 键鼠注入 | POST | 代理 `AgentWebServer.cs` L182 `/api/input` | `X-SoulLink-Token` + JSON `{action,x,y,button,key,delta,count}` | `{ok, error}` |
| 身份回显 | GET | 代理 L163 `/api/info` | token | `{ok,account,session,pid,port}` |
| 历史录像 | GET | 主程序 `WebServer.cs` L364 `/file/<rel>` | Cookie | `video/mp4`，支持 Range 206（`WriteStreamWithRange` L487） |

> ★ 关键差异（与桌面网页一致）：`/api/sessions` 走**主程序端口** + Cookie 鉴权；`/live`、`/api/input`、`/api/info` 走**代理端口** + `X-SoulLink-Token`（`directToken` 由 sessions 下发）。安卓端必须维护**两套 base URL + 两套鉴权**。

### 2.2 登录时序

```
 安卓APP              主程序(:WebPort)                代理(:directPort)
   │  GET /api/login?pw=**** ──────────►
   │  ◄──────── 200 + Set-Cookie:soullink_sid ────┘
   │  GET /api/sessions (Cookie) ──────►
   │  ◄──────── { directToken, sessions:[{directPort,...}] } ──┘
   │  （选账号 acc）→ 记下 directPort + directToken
   │  GET http://host:directPort/live?token=... ─────────────►
   │  ◄──────────────── image/jpeg 帧 ─────────────────────┘
```

### 2.3 画面拉流时序（串行 + 丢旧帧，对齐网页端 L754-802）

```
  App 渲染循环（单协程，永不并发拉帧）
   │  while(liveRunning):
   │    GET /live?token=...&t=now  ──►  代理
   │    ◄── JPEG bytes ──┘
   │    解码 Bitmap → 覆盖 latestBitmap（丢旧帧，不排队）
   │    ImageView.postInvalidate()
   │    delay = nextDelayMs()   # 失败退避 1s→2s→4s→8s；静止降频 1fps
   │    sleep(delay)
```

> 要点：**串行**（上一帧未回绝不再发）、**失败退避**、**静止降频**（连续 N 帧字节数相同 → 1fps）——三者是 `web-index.html` L700-802 已有的语义，安卓端必须等价实现，否则打爆代理/浪费带宽（AGENTS.md「按需取数」）。

### 2.4 输入时序（点击 / 摇杆 / 键位）

```
 触摸事件 → 手势映射器（MOBILE-CONTROLS-DESIGN §1/§2）
   │  单指点/拖 → {action:"click"/"down"/"up"/"move", x,y, button}
   │  双指滚轮 → {action:"wheel", delta}
   │  摇杆方向 → {action:"keydown"/"keyup", key:"W+D"}  (需后端新增,见 §7)
   │  中文     → {action:"text", text:"..."}            (需后端新增,见 §7)
   ▼
  POST http://host:directPort/api/input
     Headers: X-SoulLink-Token, Content-Type: application/json
     Body: {action, x, y, button, key, delta, count, text?}
```

> ★ 摇杆/中文的 `keydown`/`keyup`/`text` 是**尚未存在的后端动作**（`MOBILE-CONTROLS-DESIGN-20261122.md` §7 已列 4 项「确定」协议缺口）。**本安卓 APP 设计先按「未来协议」写接口层，用 `InputAction` 枚举预留，未接后端前可先跑「点击/移动/滚轮」三个已有动作。**

---

## 3. 技术栈推荐（Kotlin 已定）

### 3.1 核心结论表

| 层 | 推荐 | 理由 |
|---|---|---|
| 语言 | **Kotlin**（用户拍板，不再纠结 Java/传统 View） | 协程/Flow 天然契合「串行拉流 + 丢旧帧」 |
| 网络 | **OkHttp** | 细粒度控制连接池/超时/拦截器；自带 token 拦截器；比 HttpURLConnection 好测 |
| 异步 | **Coroutines + Flow** | 单协程串行拉帧天然实现「上一帧没回不再发」 |
| 图像解码（当前 JPEG） | **BitmapFactory / ImageDecoder** | 见 §3.2 |
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

| 维度 | ExoPlayer (Media3) | MediaCodec（裸） | BitmapFactory/ImageDecoder（当前） |
|---|---|---|---|
| 适用输入 | 完整视频流（MP4/HLS/DASH/RTSP） | 裸编码帧（H.264 Annex-B） | **单帧图片（JPEG/PNG/WebP）** |
| 当前 `/live`（JPEG 单帧） | ❌ 播不了（非视频流） | ❌ 用不上（非编码帧） | ✅ **直接用** |
| 历史 `/file/*.mp4` | ✅ 直接播（Range 206 已支持） | 需要自己解封装+喂帧 | ❌ 只能解单帧 |
| 未来 H.264 实时流 | ✅ 首选（自动处理解封装/音视频/缓冲/丢帧） | 可用但工作量大（手动 Annex-B 解析 + 喂帧 + 时间戳） | ❌ 不适用 |
| 复杂度 | 中（API 高层） | 高（底层状态机） | 低 |
| 低延迟控制 | 中（可调 buffer，但有封装层） | 高（完全可控） | 高（就是逐帧） |

**分阶段结论**：
- **P0（当前，JPEG）**：`BitmapFactory.decodeStream(body.byteStream())` → 覆盖 `Bitmap` → `ImageView.postInvalidate()`。**不需要 ExoPlayer 也不需要 MediaCodec**。这是对「现有 `/live` 是 JPEG」这一代码事实的唯一正确映射。
- **P1（未来，若 H.264 上线）**：`/live` 从 JPEG 换成 H.264 裸流或 fMP4 后，**首选 ExoPlayer（Media3）的 `MediaSource` 直连**；只有需要极致低延迟且愿意自己维护解复用/喂帧时，才降到裸 MediaCodec。STREAM-PLAN-V2 §7 明确 H.264/WebRTC 是「后置需验证」，所以 MediaCodec 是**预留项，不是现在**。

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
│     │  │  ├─ LiveStream.kt        # 串行拉帧协程 + 丢旧帧 + 退避
│     │  │  └─ JpegDecoder.kt       # BitmapFactory 封装 → SurfaceView 绘制
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
    fun liveUrl(directPort: Int, token: String) = "http://$host:$directPort/live?token=$token"
    suspend fun input(directPort: Int, token: String, a: InputAction): Boolean
    suspend fun info(directPort: Int, token: String): InfoDto
    fun replayUrl(path: String) = "$mainBase/file/$path"   // ExoPlayer 播
}
```

### 5.2 `LiveStream`（串行拉帧 + 丢旧帧 + 退避，对齐网页 L700-802）

```kotlin
class LiveStream(private val api: SoulLinkApi, private val scope: CoroutineScope) {
    @Volatile var running = false
    private val _frame = MutableStateFlow<Bitmap?>(null)
    val frame: StateFlow<Bitmap?> = _frame

    fun start(port: Int, token: String) {
        running = true
        scope.launch(Dispatchers.IO) {
            var fail = 0; var still = 0; var lastBytes = -1
            while (running) {
                val b = try {
                    api.liveBytes(port, token)       // GET /live 返回 ByteArray
                } catch (e: Exception) { fail++; null }
                if (b == null) { delay(backoff(fail)); continue }
                fail = 0
                if (b.size == lastBytes) still++ else still = 0
                lastBytes = b.size
                _frame.value = JpegDecoder.decode(b) // 丢旧帧：直接覆盖
                delay(nextDelay(still))              // 静止≥6 → 1000ms
            }
        }
    }
    fun stop() { running = false }
}
```

> ★ `_frame` 用 `StateFlow<Bitmap?>` 供 **Compose 界面预览** 或 **传统 View 通过 `collect` 回调**消费；真正上屏的是 `SurfaceRenderView`（§5.4），不阻塞 UI 线程。

### 5.3 `SurfaceRenderView`（传统 View，承载画面）

```kotlin
class SurfaceRenderView(context: Context) : SurfaceView(context), SurfaceHolder.Callback {
    // 收集 LiveStream.frame → Canvas.drawBitmap(bmp, matrix, paint) 画到 surface
    // SurfaceView 双缓冲：draw 不卡 UI 线程，丢帧即覆盖旧帧
    // 未来 H.264：holder.surface 直接作为 MediaCodec 的 outputSurface
}
```

### 5.4 `InputAction` + `InputSender`（预留 keydown/keyup/text）

```kotlin
sealed class InputAction {
    data class Click(x: Double, y: Double, button: String = "left") : InputAction()
    data class Down(button: String) : InputAction()
    data class Up(button: String) : InputAction()
    data class Move(x: Double, y: Double) : InputAction()
    data class Wheel(delta: Int) : InputAction()
    data class Key(combo: String) : InputAction()           // 已有
    data class KeyDown(combo: String) : InputAction()        // ★ 后端待新增
    data class KeyUp(combo: String) : InputAction()          // ★ 后端待新增
    data class Text(text: String) : InputAction()            // ★ 后端待新增
}
// InputSender.input(directPort, token, action) 序列化为 JSON POST，带 X-SoulLink-Token
```

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
| `/live` | `LiveStream` 串行 GET，`BitmapFactory.decodeStream` | `?token=` | **JPEG 单帧，不是视频** |
| `/api/input` | `InputSender` POST JSON | `X-SoulLink-Token` 头 | 已有 click/down/up/move/wheel/key |
| `/api/info` | 可选：连接后 GET 校验 `account` 匹配 | token | 调试/防串号 |
| `/file/*.mp4` | ExoPlayer `MediaItem.fromUri` | Cookie | Range 206 已支持，直接播 |

> ★ **防串号**：`/api/sessions` 返回的 `directPort` 按账号分配（`WebDirect.cs` L45-53），安卓端必须用**选中账号的 directPort + 对应 directToken**，不能拿 A 账号的 token 打 B 账号的端口（`/api/info` 的 `account` 字段可校验）。

---

## 7. 分阶段计划

| 阶段 | 内容 | 依赖 | 交付 |
|---|---|---|---|
| **P0（脚手架 + 能看）** | 工程骨架（§4）+ 登录 + 会话列表 + 单账号画面拉流（JPEG）+ 串行/退避 | 本机已装 SDK | 能 `assembleDebug` + 真机看实时画面 |
| **P1（能操作）** | 手势映射（点击/拖动/滚轮）+ 键位布局（`LayoutConfig`）+ 虚拟摇杆 + 编辑模式 | 已有 click/move/wheel | 手机能点、拖、滚 |
| **P2（摇杆 + 中文，依赖后端）** | `keydown`/`keyup`/`text` 动作对接 + 断线释放 | **后端先加 3 个动作**（`MOBILE-CONTROLS-DESIGN` §7） | 摇杆移动/视角 + 中文输入 |
| **P3（历史录像）** | ExoPlayer 播 `/file/*.mp4` | 无 | 看回放 |
| **P4（可选）** | H.264 实时流（若 STREAM-PLAN-V2 后置项立项） | 后端换 `/live` | 换 ExoPlayer/MediaCodec 渲染 |

---

## 8. 未决问题（需 Lead 决策）

1. **后端 3 个动作何时做**：`keydown`/`keyup`/`text` 是摇杆按住 + 中文输入的硬前提（`MOBILE-CONTROLS-DESIGN` §7 已列「确定」）。安卓端已用 `InputAction` 枚举预留，但 **P2 阶段必须等后端先落地**，否则摇杆只能「点按式」（体验差）。
2. **工程放哪个目录**：本任务只出设计文档，未建工程。落地时工程目录建议 `mobile/SoulLinkAndroid/`（不在 `docs/research/`）。是否现在建骨架，需你拍板（涉及「不囤积构建产物」的 AGENTS.md 整洁规则）。
3. **明文 HTTP 问题**：安卓 9+ 默认禁止明文 HTTP（`cleartextTraffic`），且 `/live`/`/api/input` 是明文 + token。局域网内需 `network_security_config` 放行明文；外网必须走 §（MOBILE-REMOTE-PLAN §5.4）的隧道/TLS。**是否先按「局域网明文」做（加 cleartext 白名单），外网安全留到发布前？**
4. **NDK 未装**：本阶段 JPEG 解码用 Java 层 `BitmapFactory`，不需要 NDK。仅当未来要做自研 H.264 低延迟解码或 FEC 时才需要，届时再装。

---

## 9. 相关文档

- `MOBILE-REMOTE-PLAN-20261122.md` —— 路线 / 手势 / 最小改动集（含 `text`/`moverel`/CORS/令牌）
- `MOBILE-CONTROLS-DESIGN-20261122.md` —— 摇杆数学 / 键位 JSON / 中文上屏 / 断线释放
- `STREAM-PLAN-V2-20261122.md` + `MEDIA-CAPABILITY-20261122.md` —— 抓屏/编码实测（JPEG vs H.264）
- `Relay/AgentWebServer.cs`、`Services/WebServer.cs` —— HTTP 接口实现
- `AGENTS.md` —— 性能约束 / 工作区整洁
