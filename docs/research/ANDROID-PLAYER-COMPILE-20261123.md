# 安卓播放器核心接入与编译说明（ANDROID-PLAYER-COMPILE）

> 日期：2026-11-23
> 作者：viewer-auditor（dsh-team task `task_mwtc3525q1k26`）
> 范围：仅新增 `Android/app/src/main/kotlin/com/soullink/android/player/` 包，未动 Relay/、未动 docs/web-index.html、未动根工程文件。

## 0. 结论

player/ 包 9 个 .kt 文件已用**本机 Gradle 缓存里的 Kotlin 1.9.24 编译器**（kotlin-compiler-embeddable）+ android-34 android.jar + okhttp/okio 完整编译通过：**0 error、0 warning**（jvm-target 17）。

## 1. 文件清单（Android/app/src/main/kotlin/com/soullink/android/player/）

| 文件 | 职责 | 对齐 web-index.html |
|---|---|---|
| PlayerConstants.kt | STALL_MS/SKIP_QUEUE/SKIP_RESET_LIMIT/退避上限/8ms 节流 | STALL_MS=10000、SKIP_QUEUE、SKIP_RESET_LIMIT、LOOK_HZ_MS |
| AnnexB.kt | AnnexB 切 NAL（起始码扫描）+ AU 攒批 | splitAnnexB / flushAu |
| H264SpsParser.kt | SPS 解析 profile/level/宽高（含 High profile 扩展） | avcCodecFromSps |
| H264StreamReader.kt | OkHttp 流式 GET /stream + 逐字节切 NAL 攒 AU | reader.read() 循环 |
| H264Decoder.kt | MediaCodec video/avc + csd-0/csd-1 + AVCC 打包 + 丢帧/重同步 | WebCodecs decoder |
| H264Player.kt | 编排：拉流→解码→上屏 + 看门狗 + 退避重连 + 代际作废 | startStream 全链路 |
| InputAction.kt | 输入动作定义 | /api/input body |
| InputSender.kt | POST /api/input（send 保序 / sendNoWait 高频） | postInputRaw |
| InputThrottler.kt | 转视角 8ms 合并 + move latest-wins 节流 + 摇杆向量 | lookDelta/flushLook/sendMoveThrottled |

## 2. 接入脚手架（对接现有 Android/ 工程）

现有 `Android/` 工程（mature-researcher 建）已具备：AGP 8.5.2 + Kotlin 1.9.23 + Compose + OkHttp 4.12.0 + coroutines-android 1.8.1。player/ 包只依赖以下已有/标准 API，**无需改 build.gradle.kts**：

- android.media.MediaCodec / MediaFormat / android.view.Surface / android.os.SystemClock —— android.jar 内置
- okhttp3.* —— 已在 app/build.gradle.kts 声明
- kotlinx.coroutines.* —— 已声明
- org.json.JSONObject —— Android 内置

### 2.1 最小接线（在 ui/viewer 层，由脚手架侧完成）

```kotlin
// SurfaceView 的 holder.surface 就是 MediaCodec 的 outputSurface
surfaceView.holder.addCallback(object : SurfaceHolder.Callback {
    override fun surfaceCreated(h: SurfaceHolder) {
        player = H264Player(
            streamUrl = "http://$host:$directPort/stream?token=$directToken",
            token = directToken,
            surface = h.surface,
            scope = lifecycleScope,
        )
        player.onState = { st -> /* 更新 UI 状态 */ }
        player.start()
    }
    override fun surfaceChanged(h: SurfaceHolder, f: Int, w: Int, h2: Int) {}
    override fun surfaceDestroyed(h: SurfaceHolder) { player?.stop() }
})

// 输入：转视角（摇杆 → moverel，8ms 合并）
val look = LookThrottler(lifecycleScope) { dx, dy ->
    inputSender.sendNoWait(InputAction.MoveRel(dx, dy))
}
// 点按 = 绝对定位 + 左键点击
inputSender.send(InputAction.Click(nx, ny, "left"))
```

### 2.2 正式编译命令（mature-researcher 的根工程就绪后）

```powershell
# 本机已装 JDK17 + Gradle 8.9 + Android SDK android-34 + AGP 8.5.2（见 ANDROID-APP-DESIGN §1）
cd Android
# 若尚无 gradlew，先：gradle wrapper --gradle-version 8.9
.\gradlew.bat :app:compileDebugKotlin
```

> 本任务**未**跑 assembleDebug（根工程由 mature-researcher 并行建，避免覆盖）。语法级验证已用 kotlin-compiler-embeddable 1.9.24 独立完成，等价 `:app:compileDebugKotlin` 的 Kotlin 编译阶段。

## 3. 语义对齐要点（与网页 WebCodecs 完全同构）

1. **AU 攒批**：SPS/PPS/新 VCL = 上一个 AU 结束；只有 VCL 算「AU 有图像」。
2. **从 IDR 起播**：configure 后首个 key 之前的 delta 丢弃（半个 GOP 缺参考帧）。
3. **丢旧帧**：decodeQueue ≥ SKIP_QUEUE(2) 且非 key → 丢 delta AU；连续丢达 SKIP_RESET_LIMIT(10) → 触发重同步（flush + 断流重连拿 IDR）。
4. **重连式重同步**：断流/积压 → 退避重连（1s→8s 封顶），重连 = 新订阅 = 服务端按需发 IDR（无限 GOP 下本地 reset 是死路，故不 reset，直接重连）。
5. **看门狗以「解码帧」为准**：STALL_MS=10s 无新帧 → 断流重连（首帧前不启用）。
6. **代际作废**：generation 计数，stop/重连 +1，旧拉流/回调静默退出。

## 4. 待接/未决（交 Lead / 脚手架侧）

- **SurfaceView 生命周期**：surfaceCreated/surfaceDestroyed 与 player.start()/stop() 绑定；锁屏/切后台时 stop 释放解码器（对齐网页 visibilitychange 断流）。
- **双 base URL + 两套鉴权**：/api/sessions 走主程序 Cookie，/stream、/api/input 走代理端口 + X-SoulLink-Token（ANDROID-APP-DESIGN §2.1），player 包假定调用方已拿到 streamUrl/token。
- **明文 HTTP**：Manifest 已 usesCleartextTraffic=true（局域网直连），外网需 TLS/隧道。
