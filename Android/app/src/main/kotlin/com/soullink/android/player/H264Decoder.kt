package com.soullink.android.player

import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Build
import android.view.Surface
import java.nio.ByteBuffer
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.concurrent.thread

/**
 * MediaCodec H.264 解码器封装（video/avc，outputSurface 绑 SurfaceView 的 surface）。
 *  - csd-0/csd-1 从 SPS/PPS 提取（去起始码的裸 NAL）
 *  - 输入用 AVCC 长度前缀打包
 *  - KEY_MAX_INPUT_SIZE 按 SPS 分辨率设置，大 IDR 不再因 input buffer 容量不足被整 AU 丢弃
 *  - 丢旧帧：积压超过 SKIP_QUEUE 丢 delta AU，只放行 key；达 SKIP_RESET_LIMIT 触发重同步
 *  - 解码循环输入侧按需 dequeue（队列有 AU 才要 input buffer），无空转 queueInputBuffer(0)
 *  - 帧 pacing：按到达节奏（EWMA）schedule 上屏（releaseOutputBuffer(idx, renderAtNs)），
 *    平滑网络抖动；最多提前一个帧间隔，迟到帧立即渲染不累积延迟
 * 对齐 web-index.html 的 WebCodecs 语义（SKIP_QUEUE / SKIP_RESET_LIMIT / 重连式重同步）。
 */
class H264Decoder(private val surface: Surface) {
    private var codec: MediaCodec? = null

    @Volatile var isConfigured = false
        private set

    private val inputQueue = ConcurrentLinkedQueue<Au>()
    private val running = AtomicBoolean(false)

    @Volatile private var pendingInFlight = 0
    @Volatile private var skippedAUs = 0
    @Volatile private var skippedTotal = 0

    // ---- 帧 pacing 状态 ----
    /** 上一次接受的 AU 到达时刻（nanoTime），用于测到达节奏。 */
    private var lastArrivalNs = 0L

    /** 到达节奏 EWMA（ns）。服务端无帧时间戳（裸 Annex-B 流），只能用客户端到达节奏估计。 */
    @Volatile private var frameIntervalNs = DEFAULT_FRAME_INTERVAL_NS

    /** 下一个应上屏的显示时刻（nanoTime 网格锚点）；0 = 待首帧锚定。 */
    private var nextRenderNs = 0L

    /** input buffer 容量不足导致的整 AU 丢弃计数（正常应为 0；KEY_MAX_INPUT_SIZE 已设）。 */
    @Volatile private var inputOverflowDrops = 0L

    /** 输出帧尺寸变化回调（INFO_OUTPUT_FORMAT_CHANGED）。 */
    var onFrame: ((width: Int, height: Int) -> Unit)? = null
    var onError: ((Throwable) -> Unit)? = null
    /** 解码积压到阈值、需要重同步（flush + 重新拉流拿 IDR）。 */
    var onNeedResync: (() -> Unit)? = null

    /** 近似 decodeQueue 深度 = 已入队未喂 + 已喂未输出。 */
    val queueDepth: Int get() = inputQueue.size + pendingInFlight

    /** 大 IDR 因容量不足被整丢的次数（诊断用，配合 inputOverflowDrops）。 */
    val overflowDrops: Long get() = inputOverflowDrops

    /** 从 SPS/PPS 提取 csd-0/csd-1，配置并启动解码器。 */
    fun configure(sps: Nal, pps: Nal): Boolean {
        if (isConfigured) return true
        return try {
            val info = H264SpsParser.parse(sps)
            val format = MediaFormat.createVideoFormat(
                MediaFormat.MIMETYPE_VIDEO_AVC,
                info?.width ?: 0,
                info?.height ?: 0,
            )
            format.setByteBuffer("csd-0", ByteBuffer.wrap(sps.bytes))
            format.setByteBuffer("csd-1", ByteBuffer.wrap(pps.bytes))
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
            }
            // KEY_MAX_INPUT_SIZE：不给时部分设备默认 input buffer 很小，大 IDR 塞不进
            // 会走 decoderLoop 的容量兜底被整 AU 丢弃 → 表现为「点击瞬间画面卡住」。
            // 按 SPS 分辨率推：宽×高（1080p≈2MB），下限 1MB、上限 8MB。
            val w = info?.width ?: 0
            val h = info?.height ?: 0
            val maxInputSize = (w * h).coerceIn(MIN_MAX_INPUT_SIZE, MAX_MAX_INPUT_SIZE)
            format.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, maxInputSize)
            val c = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
            c.configure(format, surface, null, 0)
            c.start()
            codec = c
            running.set(true)
            isConfigured = true
            thread(name = "h264-decoder") { decoderLoop(c) }
            true
        } catch (e: Exception) {
            onError?.invoke(e)
            false
        }
    }

    /** 喂入一个 AU；丢帧判定在此完成。true=接受，false=丢弃。 */
    fun feed(au: Au): Boolean {
        if (!isConfigured) return false
        if (queueDepth >= PlayerConstants.SKIP_QUEUE && !au.hasKey) {
            skippedAUs++
            skippedTotal++
            if (skippedAUs >= PlayerConstants.SKIP_RESET_LIMIT) {
                skippedAUs = 0
                onNeedResync?.invoke()
            }
            return false
        }
        if (au.hasKey) skippedAUs = 0
        // 到达节奏采样（只对被接受的 AU）：EWMA 平滑网络抖动，得到帧间隔估计。
        val nowNs = System.nanoTime()
        if (lastArrivalNs != 0L) {
            val delta = nowNs - lastArrivalNs
            if (delta in MIN_MEASURE_INTERVAL_NS..MAX_MEASURE_INTERVAL_NS) {
                frameIntervalNs = (frameIntervalNs * 9 + delta) / 10
            }
        }
        lastArrivalNs = nowNs
        inputQueue.add(au)
        return true
    }

    /** 清空输入队列 + codec.flush()（重同步用）。 */
    fun flush() {
        inputQueue.clear()
        pendingInFlight = 0
        skippedAUs = 0
        // pacing 网格与节奏采样一起重置，重连后重新锚定
        nextRenderNs = 0L
        lastArrivalNs = 0L
        try { codec?.flush() } catch (e: Exception) {}
    }

    fun release() {
        running.set(false)
        inputQueue.clear()
        pendingInFlight = 0
        nextRenderNs = 0L
        lastArrivalNs = 0L
        try {
            codec?.stop()
            codec?.release()
        } catch (e: Exception) {}
        codec = null
        isConfigured = false
    }

    private fun decoderLoop(c: MediaCodec) {
        val info = MediaCodec.BufferInfo()
        while (running.get()) {
            // 输入侧：只有队列里真有 AU 才去要 input buffer（旧实现无条件 dequeue 10ms
            // 再 queueInputBuffer(0) 空转，白引入最高 ~20ms/帧 的节拍抖动）。
            if (inputQueue.isNotEmpty()) {
                val inIndex = c.dequeueInputBuffer(DEQUEUE_TIMEOUT_US)
                if (inIndex >= 0) {
                    val au = inputQueue.poll()
                    if (au != null) {
                        val avcc = packAvcc(au)
                        val buf = c.getInputBuffer(inIndex)
                        if (buf != null && avcc.size <= buf.capacity()) {
                            buf.clear()
                            buf.put(avcc)
                            c.queueInputBuffer(inIndex, 0, avcc.size, au.timestampUs, 0)
                            pendingInFlight++
                        } else {
                            // KEY_MAX_INPUT_SIZE 已设，正常到不了这里；保底防崩并计数观察
                            inputOverflowDrops++
                            c.queueInputBuffer(inIndex, 0, 0, 0, 0)
                        }
                    } else {
                        c.queueInputBuffer(inIndex, 0, 0, 0, 0)
                    }
                }
                // 没抢到 input buffer：AU 留在队列里，下轮再喂（不丢）
            }

            val outIndex = c.dequeueOutputBuffer(info, DEQUEUE_TIMEOUT_US)
            when {
                outIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> {
                    val w = c.outputFormat.getInteger(MediaFormat.KEY_WIDTH)
                    val h = c.outputFormat.getInteger(MediaFormat.KEY_HEIGHT)
                    onFrame?.invoke(w, h)
                }
                outIndex == MediaCodec.INFO_TRY_AGAIN_LATER -> { /* noop：2ms 超时即天然休眠 */ }
                outIndex >= 0 -> {
                    if (pendingInFlight > 0) pendingInFlight--
                    c.releaseOutputBuffer(outIndex, computeRenderAtNs())
                }
            }
        }
    }

    /**
     * 计算本帧上屏时刻（nanoTime）：
     *  - 到达快于节奏（突发/抖动）→ 排到显示网格上，最多等待一个帧间隔（有界，不积延迟）；
     *  - 到达慢于节奏（RDP 慢合成/网络差）→ 立即渲染，网格顺延重锚定；
     *  - 首帧（flush/release 后）→ 立即渲染并锚定网格。
     */
    private fun computeRenderAtNs(): Long {
        val nowNs = System.nanoTime()
        if (nextRenderNs <= 0L) nextRenderNs = nowNs
        var renderAtNs = maxOf(nowNs, nextRenderNs)
        // 上界：最多提前一个帧间隔，避免 pacing 自身变成延迟源
        if (renderAtNs - nowNs > frameIntervalNs) renderAtNs = nowNs + frameIntervalNs
        // 网格锚定在实际渲染时刻上（自愈，不漂移不累积）
        nextRenderNs = renderAtNs + frameIntervalNs
        return renderAtNs
    }

    /** AU → AVCC 字节块（每个 NAL 前 4 字节大端长度）。 */
    private fun packAvcc(au: Au): ByteArray {
        var total = 0
        for (n in au.nals) total += 4 + n.size
        val out = ByteArray(total)
        var off = 0
        for (n in au.nals) {
            val len = n.size
            out[off] = (len ushr 24).toByte()
            out[off + 1] = (len ushr 16).toByte()
            out[off + 2] = (len ushr 8).toByte()
            out[off + 3] = len.toByte()
            System.arraycopy(n.bytes, 0, out, off + 4, len)
            off += 4 + len
        }
        return out
    }

    companion object {
        /** dequeue 超时从 10ms 降到 2ms：输入按需 dequeue 后这里只是空闲时的天然休眠。 */
        private const val DEQUEUE_TIMEOUT_US = 2_000L

        /** KEY_MAX_INPUT_SIZE 上下界（字节）。 */
        private const val MIN_MAX_INPUT_SIZE = 1 shl 20          // 1 MB
        private const val MAX_MAX_INPUT_SIZE = 8 shl 20          // 8 MB

        /** 无到达样本时的默认帧间隔：30fps（服务端 streamFps 默认 30）。 */
        private const val DEFAULT_FRAME_INTERVAL_NS = 33_333_333L

        /** 节奏采样的可信区间：低于 2ms 视为突发毛刺，高于 200ms 视为断流间隙，都不入 EWMA。 */
        private const val MIN_MEASURE_INTERVAL_NS = 2_000_000L
        private const val MAX_MEASURE_INTERVAL_NS = 200_000_000L
    }
}
