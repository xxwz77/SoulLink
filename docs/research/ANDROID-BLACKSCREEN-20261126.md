# Android 黑屏与闪退排查 · 2026-11-26

审查基线 `6317427`，加本次 Android 解码器小修。优先确认两件事：故障时应用进程是否退出、测试画面变化时解码器是否持续产生输出。现有 PLAYING 状态只证明客户端识别了 IDR。没有本次故障设备的日志或退出记录，尚不能给具体闪退定因。

## 取证（仅授权测试手机 + A2/A3 测试源）

记录机型、系统版本、客户端构建、测试会话、屏幕方向、从点击连接到黑屏/返回桌面的时间，以及同时段另一客户端看同一测试画面的结果。以会变化的测试图案验画面，避免把静止桌面误判为卡死。PowerShell 两个窗口内先采日志，再复现一次；时间覆盖复现前后各 15 秒。不清空日志。以下命令为现场步骤，本次未执行。

~~~powershell
$s = '<测试手机序列号>'
adb -s $s shell dumpsys package com.soullink.android | Select-String 'versionName|versionCode'
adb -s $s shell pidof com.soullink.android
adb -s $s logcat -v threadtime -b main -b system -b crash -s SoulLinkPlayer SoulLinkLayout MediaCodec CCodec AndroidRuntime ActivityManager libc DEBUG > "$env:TEMP/soullink-android-local.txt"
~~~

复现后 Ctrl+C，再查 pidof。Android 11+ 可查 `adb -s $s shell dumpsys activity exit-info com.soullink.android`，将 PID、时间、退出原因与日志对齐。原始日志限本地，分享前删除完整 URL、token、账号、地址和设备标识；启动日志现会输出含 token 查询参数的流地址。[H264Player:78](../../Android/app/src/main/kotlin/com/soullink/android/player/H264Player.kt#L78)、[SoulLinkApi:90](../../Android/app/src/main/kotlin/com/soullink/android/net/SoulLinkApi.kt#L90)

## 按首次缺失的证据分流

|现场观察|证据解释及下一步|
|---|---|
|应用退出或 PID 改变|找同 PID 的 AndroidRuntime FATAL EXCEPTION 与首个应用栈帧、native Fatal signal 或系统退出原因。运行期“解码线程结束”也可能仅该线程退出；release 期间捕获的 IllegalStateException 不能单独证明应用崩溃。[H264Decoder:240](../../Android/app/src/main/kotlin/com/soullink/android/player/H264Decoder.kt#L240)|
|进程存活，无首个 AU|先看是否出现“拉流开始”、HTTP 状态或 IOException。401/403 查测试直连凭据。HTTP 成功后仍需起始码和 AU 边界才会触发回调；单凭缺 AU 无法区分无字节与未攒齐。[H264StreamReader:41](../../Android/app/src/main/kotlin/com/soullink/android/player/H264StreamReader.kt#L41)、[AnnexB:103](../../Android/app/src/main/kotlin/com/soullink/android/player/AnnexB.kt#L103)|
|有 AU，无 SPS/PPS 或 IDR|初配要求在同一 AU 找到 SPS 和 PPS，随后只从 IDR 起播。看同次连接是否出现 800ms 起最多三次 IDR 请求、响应、10s 重连。HTTP 2xx 仅证明请求被接受。[H264Player:151](../../Android/app/src/main/kotlin/com/soullink/android/player/H264Player.kt#L151)、[H264Player:237](../../Android/app/src/main/kotlin/com/soullink/android/player/H264Player.kt#L237)|
|configure 失败，重进后更频繁|对齐“解码器初始化失败”原始异常、Surface 销毁时间以及所选 codec。区分资源不足、不支持的 format 和 Surface 无效。本次补丁覆盖显式释放缺口，见下。[H264Decoder:126](../../Android/app/src/main/kotlin/com/soullink/android/player/H264Decoder.kt#L126)|
|IDR / PLAYING 已出现但仍黑|IDR 到达即置 PLAYING；每次 feed 后刷新 lastFrameAt，即使 feed 拒绝 AU。持续收包而零输出可让 10s 看门狗一直判为活跃。decoderLoop 异常退出也未清 isConfigured。查 CSD 提交、overflowDrops、线程结束和同次连接的输出证据。[H264Player:258](../../Android/app/src/main/kotlin/com/soullink/android/player/H264Player.kt#L258)、[H264Player:158](../../Android/app/src/main/kotlin/com/soullink/android/player/H264Player.kt#L158)、[H264Decoder:150](../../Android/app/src/main/kotlin/com/soullink/android/player/H264Decoder.kt#L150)、[H264Decoder:265](../../Android/app/src/main/kotlin/com/soullink/android/player/H264Decoder.kt#L265)|
|有 codec 输出仍黑|releaseOutputBuffer(idx,true) 只证明提交给 Surface。核 Surface 是否已重建、布局实际宽高、测试源自身是否输出黑帧。UI 已设置播放器 onFrame，但播放器未转接 decoder.onFrame；该回调只在格式变化时发出。由此能解释比例未更新，单独不能解释全黑。[H264Decoder:334](../../Android/app/src/main/kotlin/com/soullink/android/player/H264Decoder.kt#L334)、[H264Player:69](../../Android/app/src/main/kotlin/com/soullink/android/player/H264Player.kt#L69)、[PlayerPlaceholderScreen:180](../../Android/app/src/main/kotlin/com/soullink/android/ui/player/PlayerPlaceholderScreen.kt#L180)|

## 最小修复与后续优先级

**已改解码器资源释放。** 旧代码在 configure/start/setVideoScalingMode 全部成功后才保存局部 codec；中途抛异常会绕过显式释放。退出时 stop 和 release 又在同一 try，stop 抛错会跳过 release。现在初始化失败先尝试释放局部实例并记录原异常，正常收尾也在 finally 尝试 release。反复进入失败路径可能占用设备有限的 codec 资源，是否为某台设备黑屏的诱因需实测。回退范围仅上述两段，无协议或存储迁移。[H264Decoder:126](../../Android/app/src/main/kotlin/com/soullink/android/player/H264Decoder.kt#L126)、[H264Decoder:203](../../Android/app/src/main/kotlin/com/soullink/android/player/H264Decoder.kt#L203)

**黑屏下一项改动应跟随输出证据。** 按每次订阅记录首 AU、首 IDR、首次输出提交、实际渲染回调和 Surface 销毁的时间，计数取快照，避免逐帧打日志。若确认有输入零输出，把 PLAYING 和首输出超时绑定本次订阅的有效输出；解码线程已退出时重建 decoder，仅取消 reader 后复用旧实例无助于恢复。若故障集中于退出/重入，复查 IO 配置与 UI stop 的交错，尤其 stop 取消协程但未等正在执行的 configure 返回。若集中在重同步，核 flush 后 CSD 提交状态及 flush 与 dequeue 的线程所有权。以上需独立改动及设备验证。[H264Player:113](../../Android/app/src/main/kotlin/com/soullink/android/player/H264Player.kt#L113)、[H264Player:269](../../Android/app/src/main/kotlin/com/soullink/android/player/H264Player.kt#L269)、[H264Decoder:183](../../Android/app/src/main/kotlin/com/soullink/android/player/H264Decoder.kt#L183)

## 验证记录和设备验收

本次在 Android 工程执行 `gradle --offline :app:compileDebugKotlin --console=plain`，Gradle 8.9 / JDK 17，24 秒 BUILD SUCCESSFUL（退出 0，2 项执行、13 项复用）。仅有已有的局部变量 o 初始化冗余警告。Android 源码差异已通过 git diff --check。本次未部署、未接入设备或生产会话，以下项目留给测试环境验收。

|场景|应记录及通过标准|
|---|---|
|同机同源前后对照，连续冷连接 10 次|记录成功次数及首个可见变化用时；每次均出现可辨识、持续更新的画面。超过 10s 标记失败并保留该次时间线，不能以 PLAYING 替代。|
|进出播放页、前后台及转屏各 10 次|每次重入均可见测试图案，进程无异常退出。退出后解码线程结束；重复测试前后比对测试应用 codec 资源，没有逐轮占用增长。|
|测试环境用调试替身令 configure/start/设置缩放或 stop 分别抛异常|每个已创建实例均尝试一次 release；保留原始异常；去掉故障后再次连接可播放。若设备无法触发，标为该分支未验。|
|测试源短时断流、恢复，另测持续供 AU 但解码零输出|前者记录一次完整的重连至可见首帧时间线；后者用于验证仍存的看门狗盲区，后续修复验收要求在首输出超时后恢复或呈现明确错误。|
