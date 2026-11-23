package com.soullink.android.ui.player

import android.view.SurfaceHolder
import android.view.SurfaceView
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import com.soullink.android.net.SoulLinkApi
import com.soullink.android.net.dto.SessionDto
import com.soullink.android.player.H264Player
import com.soullink.android.player.InputAction
import com.soullink.android.player.InputSender
import com.soullink.android.player.LookThrottler
import com.soullink.android.player.PlayerState
import kotlinx.coroutines.launch
import okhttp3.OkHttpClient
import java.util.concurrent.TimeUnit

/**
 * 播放页：真实输入链路（InputSender 发 /api/input）+ 画面（SurfaceView + MediaCodec）。
 *
 * ★ 手势语义对齐 docs/web-index.html 相对模式（REL_TAP_PX=10、8ms 合并、wheel 中点 Y/8）：
 *   - 单指滑动 → moverel 转视角（复用 LookThrottler 8ms 合并）
 *   - 单指点一下（累计位移 < TAP_PX）→ click 左键（绝对定位 + 左键）
 *   - 双指 → 中点 Y 变化 / 8 → wheel delta
 *   - 双指点一下（累计位移 < TAP_PX）→ click 右键
 *
 * ★ 输入发送：down/up/click 走 send（阻塞保序），moverel/wheel 走 sendNoWait（高频 fire-and-forget）。
 */
@Composable
fun PlayerPlaceholderScreen(
    host: String,
    session: SessionDto,
    api: SoulLinkApi,
    directToken: String,
    onBack: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    var playerState by remember { mutableStateOf(PlayerState.CONNECTING) }
    var errorText by remember { mutableStateOf<String?>(null) }
    var player by remember { mutableStateOf<H264Player?>(null) }

    val streamUrl = api.streamUrl(session.directPort, directToken)
    val inputBase = "http://$host:${session.directPort}"

    // 输入发送器：X-SoulLink-Token 头 + body 带 sid（复用 net/SoulLinkApi 的 directPort 契约）
    val inputSender = remember(inputBase, directToken, session.sessionId) {
        InputSender(
            client = defaultInputClient(),
            baseUrl = inputBase,
            token = directToken,
            sessionId = session.sessionId.toString(),
        )
    }

    // 转视角位移合并节流（8ms，对齐 web lookDelta/flushLook）
    val lookThrottler = remember(inputSender) {
        LookThrottler(scope) { dx, dy ->
            if (dx != 0 || dy != 0) inputSender.sendNoWait(InputAction.MoveRel(dx, dy))
        }
    }

    Column(Modifier.fillMaxSize()) {
        TextButton(onClick = onBack) { Text("← 返回列表") }
        Text("账号：${session.label.ifBlank { session.user }}", Modifier.padding(horizontal = 16.dp))
        Text("状态：$playerState", Modifier.padding(horizontal = 16.dp))
        errorText?.let { Text(it, Modifier.padding(horizontal = 16.dp), color = MaterialTheme.colorScheme.error) }

        // ── 画面 + 手势触摸层 ──
        Box(Modifier.fillMaxSize().padding(8.dp).background(Color.Black)) {
            // SurfaceView 承载 MediaCodec 输出；surface 就绪时惰性启动播放器
            AndroidView(
                factory = { ctx ->
                    SurfaceView(ctx).apply {
                        holder.addCallback(object : SurfaceHolder.Callback {
                            override fun surfaceCreated(holder: SurfaceHolder) {
                                if (player == null) {
                                    player = H264Player(
                                        streamUrl = streamUrl,
                                        token = directToken,
                                        surface = holder.surface,
                                        scope = scope,
                                        client = defaultStreamClient(),
                                    ).also { p ->
                                        p.onState = { playerState = it }
                                        p.onError = { errorText = it.message ?: "播放错误" }
                                        p.start()
                                    }
                                }
                            }
                            override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {}
                            override fun surfaceDestroyed(holder: SurfaceHolder) {
                                player?.stop()
                                player = null
                            }
                        })
                    }
                },
                modifier = Modifier.fillMaxSize(),
            )

            // 手势层：覆盖 SurfaceView（SurfaceView 是传统 View，不吃 Compose 手势）
            TouchGestureLayer(
                onLookDelta = { dx, dy -> lookThrottler.add(dx, dy) },
                onClick = { nx, ny, button ->
                    scope.launch { inputSender.send(InputAction.Click(nx, ny, button)) }
                    Unit
                },
                onWheel = { delta -> inputSender.sendNoWait(InputAction.Wheel(delta)) },
                modifier = Modifier.fillMaxSize(),
            )
        }
    }

    DisposableEffect(Unit) {
        onDispose {
            player?.stop()
            player = null
        }
    }
}

/**
 * 触屏手势层：单指滑动=moverel、单指点一下=click 左键、双指=wheel、双指点一下=click 右键。
 * 对齐 docs/web-index.html 相对模式：relPoints（Map）+ relWasTwo + relMaxMove + relPinchY。
 */
@Composable
private fun TouchGestureLayer(
    onLookDelta: (dx: Int, dy: Int) -> Unit,
    onClick: (nx: Double, ny: Double, button: String) -> Unit,
    onWheel: (delta: Int) -> Unit,
    modifier: Modifier = Modifier,
) {
    Box(
        modifier.pointerInput(Unit) {
            awaitEachGesture {
                val down = awaitFirstDown(requireUnconsumed = false)
                val downPos = down.position
                var totalDx = 0f
                var totalDy = 0f
                var wasTwo = false
                var pinchY: Float? = null
                var lastX = downPos.x
                var lastY = downPos.y

                // 收集本次手势所有 pointer move/up，直到全部抬起
                while (true) {
                    val event = awaitPointerEvent()
                    val changes = event.changes
                    val active = changes.filter { it.pressed }

                    if (active.size >= 2) wasTwo = true

                    if (active.isEmpty()) {
                        // 全部抬起：判定 tap 或收尾
                        val maxMove = kotlin.math.hypot(totalDx, totalDy)
                        val nx = (downPos.x / size.width.toFloat()).toDouble().coerceIn(0.0, 1.0)
                        val ny = (downPos.y / size.height.toFloat()).toDouble().coerceIn(0.0, 1.0)
                        if (maxMove < TAP_PX) {
                            onClick(nx, ny, if (wasTwo) "right" else "left")
                        }
                        break
                    }

                    if (active.size >= 2) {
                        // 双指：中点 Y 变化 / 8 → wheel
                        val ys = active.map { it.position.y }
                        val midY = ys.average().toFloat()
                        if (pinchY != null) {
                            val steps = kotlin.math.round((midY - pinchY) / 8f).toInt()
                            if (steps != 0) onWheel(steps * 120)
                        }
                        pinchY = midY
                    } else if (active.size == 1) {
                        // 单指：位移 → moverel
                        val change = active.first()
                        val pos = change.position
                        val dx = (pos.x - lastX).toInt()
                        val dy = (pos.y - lastY).toInt()
                        lastX = pos.x
                        lastY = pos.y
                        if (dx != 0 || dy != 0) {
                            totalDx += dx
                            totalDy += dy
                            onLookDelta(dx, dy)
                        }
                    }
                }
            }
        },
    )
}

private const val TAP_PX = 10f

private fun defaultInputClient(): OkHttpClient = OkHttpClient.Builder()
    .connectTimeout(5, TimeUnit.SECONDS)
    .readTimeout(10, TimeUnit.SECONDS)
    .build()

private fun defaultStreamClient(): OkHttpClient = OkHttpClient.Builder()
    .connectTimeout(5, TimeUnit.SECONDS)
    .readTimeout(0, TimeUnit.MILLISECONDS)
    .build()
