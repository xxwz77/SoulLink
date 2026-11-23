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
 *  - 丢旧帧：积压超过 SKIP_QUEUE 丢 delta AU，只放行 key；达 SKIP_RESET_LIMIT 触发重同步
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

    /** 输出帧尺寸变化回调（INFO_OUTPUT_FORMAT_CHANGED）。 */
    var onFrame: ((width: Int, height: Int) -> Unit)? = null
    var onError: ((Throwable) -> Unit)? = null
    /** 解码积压到阈值、需要重同步（flush + 重新拉流拿 IDR）。 */
    var onNeedResync: (() -> Unit)? = null

    /** 近似 decodeQueue 深度 = 已入队未喂 + 已喂未输出。 */
    val queueDepth: Int get() = inputQueue.size + pendingInFlight

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
        inputQueue.add(au)
        return true
    }

    /** 清空输入队列 + codec.flush()（重同步用）。 */
    fun flush() {
        inputQueue.clear()
        pendingInFlight = 0
        skippedAUs = 0
        try { codec?.flush() } catch (e: Exception) {}
    }

    fun release() {
        running.set(false)
        inputQueue.clear()
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
                        c.queueInputBuffer(inIndex, 0, 0, 0, 0)
                    }
                } else {
                    c.queueInputBuffer(inIndex, 0, 0, 0, 0)
                }
            }

            val outIndex = c.dequeueOutputBuffer(info, DEQUEUE_TIMEOUT_US)
            when {
                outIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> {
                    val w = c.outputFormat.getInteger(MediaFormat.KEY_WIDTH)
                    val h = c.outputFormat.getInteger(MediaFormat.KEY_HEIGHT)
                    onFrame?.invoke(w, h)
                }
                outIndex == MediaCodec.INFO_TRY_AGAIN_LATER -> { /* noop */ }
                outIndex >= 0 -> {
                    if (pendingInFlight > 0) pendingInFlight--
                    c.releaseOutputBuffer(outIndex, true)
                }
            }
        }
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
        private const val DEQUEUE_TIMEOUT_US = 10_000L
    }
}
