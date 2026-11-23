package com.soullink.android.player

import android.os.SystemClock
import android.view.Surface
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import okhttp3.OkHttpClient
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/** 播放器状态（UI 回调用）。 */
enum class PlayerState { CONNECTING, PLAYING, RECONNECTING, STOPPED, ERROR }

/**
 * 播放器编排：拉流 → 解码 → 上屏 + 断流看门狗 + 退避重连 + 积压重同步。
 * 语义对齐 web-index.html（docs/web-index.html）：
 *  - 代际作废（h264Gen）：stop/重连换代，旧流回调静默退出
 *  - 看门狗以「解码帧」为准（STALL_MS）
 *  - 断流/积压 → 重连（重连 = 新订阅 = 服务端按需发 IDR）
 *  - 出首帧 → failStreak 清零
 */
class H264Player(
    private val streamUrl: String,
    private val token: String,
    surface: Surface,
    private val scope: CoroutineScope,
    private val client: OkHttpClient = defaultClient(),
) {
    private val decoder = H264Decoder(surface)
    private val running = AtomicBoolean(false)

    @Volatile private var generation = 0
    @Volatile private var failStreak = 0
    @Volatile private var firstKeyReceived = false
    @Volatile private var lastFrameAt = 0L
    private var currentReader: H264StreamReader? = null
    private var streamJob: Job? = null
    private var watchdogJob: Job? = null

    var onState: ((PlayerState) -> Unit)? = null
    var onError: ((Throwable) -> Unit)? = null

    fun start() {
        if (!running.compareAndSet(false, true)) return
        generation++
        val gen = generation
        onState?.invoke(PlayerState.CONNECTING)
        decoder.onNeedResync = { if (gen == generation) triggerReconnect(gen) }
        decoder.onError = { if (gen == generation) onError?.invoke(it) }
        streamJob = scope.launch(Dispatchers.IO) { streamLoop(gen) }
        watchdogJob = scope.launch { watchdog(gen) }
    }

    fun stop() {
        if (!running.compareAndSet(true, false)) return
        generation++
        currentReader?.cancel()
        streamJob?.cancel()
        watchdogJob?.cancel()
        decoder.release()
        onState?.invoke(PlayerState.STOPPED)
    }

    private suspend fun streamLoop(gen: Int) {
        while (running.get() && gen == generation) {
            val reader = H264StreamReader(client, streamUrl, token)
            currentReader = reader
            reader.onAu = { au -> if (gen == generation) handleAu(au) }
            reader.onError = { if (gen == generation) onError?.invoke(it) }
            reader.run()
            currentReader = null
            if (!running.get() || gen != generation) return
            // 断流/重同步后：退避重连（对齐 fail() + scheduleReconnect）
            failStreak++
            onState?.invoke(PlayerState.RECONNECTING)
            val delayMs = minOf(1000L shl failStreak.coerceAtMost(13), PlayerConstants.RECONNECT_MAX_DELAY_MS)
            delay(delayMs)
        }
    }

    private suspend fun watchdog(gen: Int) {
        while (running.get() && gen == generation) {
            delay(1000)
            if (!running.get() || gen != generation) return
            if (firstKeyReceived && lastFrameAt > 0 &&
                SystemClock.elapsedRealtime() - lastFrameAt >= PlayerConstants.STALL_MS) {
                triggerReconnect(gen)
            }
        }
    }

    private fun handleAu(au: Au) {
        if (!decoder.isConfigured) {
            val sps = au.nals.firstOrNull { it.isSps }
            val pps = au.nals.firstOrNull { it.isPps }
            if (sps != null && pps != null) {
                if (!decoder.configure(sps, pps)) return
            } else {
                return
            }
        }
        // 从 IDR 起播：configure 后首个 key 之前的 delta 丢弃（半个 GOP 缺参考帧）
        if (!firstKeyReceived) {
            if (!au.hasKey) return
            firstKeyReceived = true
            failStreak = 0
            onState?.invoke(PlayerState.PLAYING)
        }
        decoder.feed(au)
        lastFrameAt = SystemClock.elapsedRealtime()
    }

    private fun triggerReconnect(gen: Int) {
        if (gen != generation) return
        decoder.flush()
        firstKeyReceived = false
        currentReader?.cancel()
    }

    companion object {
        private fun defaultClient(): OkHttpClient = OkHttpClient.Builder()
            .connectTimeout(5, TimeUnit.SECONDS)
            // 流式长连接：禁用读超时，交给应用层看门狗（静止画面服务端无字节，读超时会误断）
            .readTimeout(0, TimeUnit.MILLISECONDS)
            .build()
    }
}
