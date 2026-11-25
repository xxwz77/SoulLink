package com.soullink.android.player

import android.os.SystemClock
import android.util.Log
import android.view.Surface
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.IOException
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/** 播放器状态（UI 回调用）。 */
enum class PlayerState { CONNECTING, PLAYING, RECONNECTING, STOPPED, ERROR }

/** 统一日志 tag：adb logcat -s SoulLinkPlayer 直接看播放链路。 */
private const val TAG = "SoulLinkPlayer"

/**
 * 播放器编排：拉流 → 解码 → 上屏 + 断流看门狗 + 退避重连 + 积压重同步。
 * 语义对齐 web-index.html（docs/web-index.html）：
 *  - 代际作废（h264Gen）：stop/重连换代，旧流回调静默退出
 *  - 看门狗以「解码帧」为准（STALL_MS）
 *  - 断流/积压 → 重连（重连 = 新订阅 = 服务端按需发 IDR）
 *  - 出首帧 → failStreak 清零
 *
 * ★ T35（2026-11-24）：修「连上之后黑屏」——首关键帧死锁（与网页端 T26 同一 bug）。
 *   服务端 NVENC 无限 GOP：只为「新观众订阅 / urgent 输入 / forceKeyFrame」发 IDR，
 *   保活帧不是 IDR。连上时错过订阅那一次 IDR → firstKeyReceived 永远 false →
 *   handleAu 全部丢弃 → 黑屏（旧看门狗对「首帧永不出现」不做任何事）。
 *   修法三板斧（严格对齐网页端 T26）：
 *   1. 主动请求 IDR：等首帧超 [PlayerConstants.IDR_REQUEST_DELAY_MS]（~800ms）时
 *      POST /api/idr（服务端已存在的强制关键帧端点），最多
 *      [PlayerConstants.IDR_REQUEST_MAX_RETRIES] 次，每次再等一拍；
 *   2. 看门狗补「从未出画面」分支：连接后 [PlayerConstants.FIRST_FRAME_DEADLOCK_MS]
 *      （10s）仍无 firstKey → 整链重连（重连 = 新订阅 = 服务端按需发 IDR，从机制上兜底）；
 *   3. 全链路日志（tag=SoulLinkPlayer）：拉流开始/首个 AU/首个 IDR/SPS+PPS 解析/
 *      configure 成败/decode 错误/IDR 请求/重连触发——出问题不再靠猜。
 */
class H264Player(
    private val streamUrl: String,
    private val token: String,
    surface: Surface,
    private val scope: CoroutineScope,
    private val idrUrl: String = "",
    private val client: OkHttpClient = defaultClient(),
) {
    private val decoder = H264Decoder(surface)
    private val running = AtomicBoolean(false)

    @Volatile private var generation = 0
    @Volatile private var failStreak = 0
    @Volatile private var firstKeyReceived = false
    @Volatile private var lastFrameAt = 0L
    /** 本次连接（当前代际内最近一次 reader.run()）开始时刻；0 = 未开始。 */
    @Volatile private var connectAt = 0L
    /** 收到的 AU 总数（诊断「frames=N 后断开」这类日志）。 */
    @Volatile private var auCount = 0L
    private var currentReader: H264StreamReader? = null
    private var streamJob: Job? = null
    private var watchdogJob: Job? = null

    var onState: ((PlayerState) -> Unit)? = null
    var onError: ((Throwable) -> Unit)? = null
    /** 输出帧尺寸回调（INFO_OUTPUT_FORMAT_CHANGED）：UI 消费宽高算等比缩放（S5）。 */
    var onFrame: ((width: Int, height: Int) -> Unit)? = null

    fun start() {
        if (!running.compareAndSet(false, true)) return
        generation++
        val gen = generation
        Log.i(TAG, "start gen=$gen stream=$streamUrl idr=${if (idrUrl.isEmpty()) "(无)" else "已配置"}")
        onState?.invoke(PlayerState.CONNECTING)
        // T35b (Moonlight): decoder consecutive-drop threshold -> request IDR (flush + /api/idr),
        //   NOT full reconnect. Reconnect stays only as watchdog last resort.
        decoder.onNeedIdr = { if (gen == generation) requestIdrForResync(gen) }
        decoder.onNeedResync = { if (gen == generation) triggerReconnect(gen, "decode积压重同步（保留兜底）") }

        // T35b: fetch /api/info once for stream fps (adaptive SKIP thresholds); fallback 30.
        if (idrUrl.isNotEmpty()) {
            scope.launch(Dispatchers.IO) {
                try {
                    val infoUrl = idrUrl.substring(0, idrUrl.length - "/api/idr".length) + "/api/info"
                    val req = Request.Builder().url(infoUrl).get()
                        .apply { if (token.isNotEmpty()) header("X-SoulLink-Token", token) }.build()
                    client.newBuilder().connectTimeout(3, TimeUnit.SECONDS).readTimeout(3, TimeUnit.SECONDS).build()
                        .newCall(req).execute().use { resp ->
                            if (resp.isSuccessful) {
                                val body = resp.body?.string() ?: ""
                                val obj = org.json.JSONObject(body)
                                val actual = obj.optInt("actualFps", 0)
                                val configured = obj.optInt("configuredFps", 0)
                                val fps = if (actual > 0) actual else if (configured > 0) configured else 30
                                decoder.streamFps = fps
                                Log.i(TAG, "streamFps=$fps (actual=$actual configured=$configured) skipQueue=${PlayerConstants.skipQueue(fps)} dropLimit=${PlayerConstants.consecutiveDropLimit(fps)}")
                            }
                        }
                } catch (e: Exception) {
                    Log.w(TAG, "/api/info 获取失败（用默认 30fps 阈值）: ${e.message}")
                }
            }
        }
        streamJob = scope.launch(Dispatchers.IO) { streamLoop(gen) }
        watchdogJob = scope.launch { watchdog(gen) }
    }

    fun stop() {
        if (!running.compareAndSet(true, false)) return
        generation++
        Log.i(TAG, "stop（用户离开/页面销毁）auCount=$auCount firstKey=$firstKeyReceived")
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
            connectAt = SystemClock.elapsedRealtime()
            auCount = 0
            firstKeyReceived = false
            idrAttempts = 0
            Log.i(TAG, "拉流开始 gen=$gen connectAt=$connectAt")
            reader.onAu = { au -> if (gen == generation) handleAu(au) }
            reader.onError = { if (gen == generation) { Log.w(TAG, "流错误: ${it.message}"); onError?.invoke(it) } }
            reader.run()
            currentReader = null
            Log.w(TAG, "拉流结束 gen=$gen auCount=$auCount firstKey=$firstKeyReceived cancelled=${!running.get()}")
            if (!running.get() || gen != generation) return
            // 断流/重同步后：退避重连（对齐 fail() + scheduleReconnect）
            failStreak++
            onState?.invoke(PlayerState.RECONNECTING)
            Log.w(TAG, "退避重连 gen=$gen failStreak=$failStreak")
            val delayMs = minOf(1000L shl failStreak.coerceAtMost(13), PlayerConstants.RECONNECT_MAX_DELAY_MS)
            delay(delayMs)
        }
    }

    /** 本代际已发过的 IDR 请求数（重连/换代时归零）。 */
    @Volatile private var idrAttempts = 0

    private suspend fun watchdog(gen: Int) {
        while (running.get() && gen == generation) {
            delay(200)
            if (!running.get() || gen != generation) return
            val now = SystemClock.elapsedRealtime()

            // ── 分支①：已有画面 → 原有停滞检测（STALL_MS）──
            if (firstKeyReceived && lastFrameAt > 0 &&
                now - lastFrameAt >= PlayerConstants.STALL_MS) {
                triggerReconnect(gen, "画面停滞 ${PlayerConstants.STALL_MS / 1000}s")
                continue
            }

            // ── 分支②：★ T35「从未出画面」——首帧死锁检测 ──
            //    连接后 FIRST_FRAME_DEADLOCK_MS 仍无任何关键帧 → 黑屏死锁（错过订阅 IDR
            //    且服务端无限 GOP 不再发）→ 重连（重连 = 新订阅 = 服务端按需发 IDR）。
            if (!firstKeyReceived && connectAt > 0 &&
                now - connectAt >= PlayerConstants.FIRST_FRAME_DEADLOCK_MS) {
                triggerReconnect(gen, "首帧死锁：${PlayerConstants.FIRST_FRAME_DEADLOCK_MS / 1000}s 无关键帧")
                continue
            }

            // ── 分支③：★ T35 主动请求 IDR ──
            //    等首帧超过 IDR_REQUEST_DELAY_MS（800ms）且还没收到关键帧 →
            //    POST /api/idr 强制服务端立即发 IDR；最多 IDR_REQUEST_MAX_RETRIES 次，
            //    每次请求后同样再等一拍才发下一次（给网络留往返时间）。
            if (!firstKeyReceived && connectAt > 0 && idrUrl.isNotEmpty() &&
                now - connectAt >= PlayerConstants.IDR_REQUEST_DELAY_MS * (idrAttempts + 1) &&
                idrAttempts < PlayerConstants.IDR_REQUEST_MAX_RETRIES) {
                val attempt = ++idrAttempts
                Log.i(TAG, "等首帧 ${now - connectAt}ms 无关键帧 → 请求强制 IDR（第 $attempt/${PlayerConstants.IDR_REQUEST_MAX_RETRIES} 次）")
                requestForcedIdr()
            }
        }
    }

    /** 上次重同步式 IDR 请求时刻（elapsedRealtime）——防抖：1s 内不重复请求。 */
    @Volatile private var lastResyncIdrAt = 0L

    /**
     * T35b（对齐 Moonlight）：解码连续丢帧达阈值后的恢复动作 = flush + 请求 IDR，
     * 不重连。防抖 1s：feed() 的丢帧计数在 flush 后清零，不会高频触发；
     * 双保险防住连续两次阈值命中间隔过短的情况。
     */
    private fun requestIdrForResync(gen: Int) {
        if (gen != generation) return
        val now = SystemClock.elapsedRealtime()
        if (now - lastResyncIdrAt < 1_000L) return
        lastResyncIdrAt = now
        Log.i(TAG, "解码丢帧达阈值 → flush + 请求 IDR（不重连，对齐 Moonlight）")
        if (idrUrl.isEmpty()) return   // 未配置 IDR 端点：仅 flush（等下一个自然关键帧）
        requestForcedIdr()
    }

    /** POST /api/idr（失败静默：只记日志，不中断播放链路）。 */
    private fun requestForcedIdr() {
        try {
            val body = "{}".toRequestBody("application/json".toMediaType())
            val request = Request.Builder()
                .url(idrUrl)
                .post(body)
                .apply { if (token.isNotEmpty()) header("X-SoulLink-Token", token) }
                .build()
            // 用独立短超时调用：IDR 请求绝不能占用流式连接，也不能把播放器拖死
            client.newBuilder()
                .connectTimeout(3, TimeUnit.SECONDS)
                .readTimeout(3, TimeUnit.SECONDS)
                .build()
                .newCall(request)
                .enqueue(object : okhttp3.Callback {
                    override fun onFailure(call: okhttp3.Call, e: IOException) {
                        Log.w(TAG, "IDR 请求失败: ${e.message}")
                    }

                    override fun onResponse(call: okhttp3.Call, response: okhttp3.Response) {
                        response.use {
                            if (it.isSuccessful) Log.i(TAG, "IDR 请求成功（HTTP ${it.code}），服务端将立即发关键帧")
                            else Log.w(TAG, "IDR 请求被拒（HTTP ${it.code}）")
                        }
                    }
                })
        } catch (e: Exception) {
            Log.w(TAG, "IDR 请求异常: ${e.message}")
        }
    }

    private fun handleAu(au: Au) {
        if (auCount == 0L) Log.i(TAG, "收到首个 AU（nalCount=${au.nals.size} hasKey=${au.hasKey}）")
        auCount++

        if (!decoder.isConfigured) {
            val sps = au.nals.firstOrNull { it.isSps }
            val pps = au.nals.firstOrNull { it.isPps }
            if (sps != null && pps != null) {
                Log.i(TAG, "SPS+PPS 到达：sps=${sps.size}B pps=${pps.size}B，configure 解码器")
                if (!decoder.configure(sps, pps)) {
                    Log.e(TAG, "configure 失败（decoder.configure 返回 false）")
                    return
                }
                Log.i(TAG, "configure 成功（解码器已启动）")
            } else {
                // 未带 SPS/PPS 的流头：等下一个 AU（无限 GOP 流里参数集通常随 IDR 或首帧出现）
                if (auCount <= 3) Log.i(TAG, "AU 无 SPS/PPS（au #$auCount），等待参数集")
                return
            }
        }
        // 从 IDR 起播：configure 后首个 key 之前的 delta 丢弃（半个 GOP 缺参考帧）
        if (!firstKeyReceived) {
            if (!au.hasKey) return
            firstKeyReceived = true
            failStreak = 0
            Log.i(TAG, "收到首个关键帧（IDR，au #$auCount）→ 起播（等待 ${SystemClock.elapsedRealtime() - connectAt}ms）")
            onState?.invoke(PlayerState.PLAYING)
        }
        decoder.feed(au)
        lastFrameAt = SystemClock.elapsedRealtime()
    }

    private fun triggerReconnect(gen: Int, reason: String) {
        if (gen != generation) return
        Log.w(TAG, "触发重连 gen=$gen 原因：$reason")
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
