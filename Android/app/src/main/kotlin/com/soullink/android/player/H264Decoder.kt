package com.soullink.android.player

import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Build
import android.util.Log
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

    /** ★ T35b：当前流帧率（SKIP 阈值自适应用）。拿不到 /api/info 就默认 30。 */
    @Volatile var streamFps: Int = 30

    private val inputQueue = ConcurrentLinkedQueue<Au>()
    private val running = AtomicBoolean(false)

    /** ★ T35b：解码线程引用（release 时 interrupt/join 兜底，消除 stop 竞态）。 */
    @Volatile private var decodeThread: Thread? = null

    /** ★ T36 方案 2：待码流内提交的参数集（configure 保存；csd-0/csd-1 已弃用）。 */
    @Volatile private var pendingSps: ByteArray? = null
    @Volatile private var pendingPps: ByteArray? = null
    /** CODEC_CONFIG 是否已提交（一次）；该 buffer 不产生输出帧，不计 pendingInFlight。 */
    @Volatile private var csdSubmitted = false

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
    /**
     * ★ T35b（对齐 Moonlight VideoDepacketizer.c CONSECUTIVE_DROP_LIMIT=120）：
     * 连续丢帧达阈值 → 主动请求 IDR（flush + /api/idr），**不重连**。
     * 重连代价大（重订阅+等 IDR+release 竞态风险），官方做法是先请求 IDR。
     */
    var onNeedIdr: (() -> Unit)? = null

    /** 近似 decodeQueue 深度 = 已入队未喂 + 已喂未输出。 */
    val queueDepth: Int get() = inputQueue.size + pendingInFlight

    /** 大 IDR 因容量不足被整丢的次数（诊断用，配合 inputOverflowDrops）。 */
    val overflowDrops: Long get() = inputOverflowDrops

    /** 从 SPS/PPS 提取 csd-0/csd-1，配置并启动解码器。 */
    fun configure(sps: Nal, pps: Nal): Boolean {
        if (isConfigured) return true
        return try {
            val info = H264SpsParser.parse(sps)
            // ★ T35b（对齐 Moonlight MediaCodecDecoderRenderer.java:1441-1455）：
            //   SPS level_idc 下修（720p→31 / 1080p→42，只降不升）。部分解码器按
            //   level 估算 input buffer 容量，服务端高 level 反而让大 IDR 被判
            //   容量不足整 AU 丢弃（官方也这么干）。csd-0 必须用下修后的 SPS。
            val spsForCsd = H264SpsParser.downgradeLevelIdc(sps, info?.width ?: 0, info?.height ?: 0) ?: sps
            // T36 diag/fix: parse() null must never yield 0x0 format — createVideoFormat(AVC,0,0)
            // can configure "ok" yet never output. csd-0 is authoritative; 1920x1080 placeholder.
            val w = info?.width ?: 0
            val h = info?.height ?: 0
            val (fw, fh) = if (w > 0 && h > 0) w to h else 1920 to 1080
            val format = MediaFormat.createVideoFormat(
                MediaFormat.MIMETYPE_VIDEO_AVC,
                fw,
                fh,
            )
            // T36 plan 2 (fully aligned with Moonlight MediaCodecDecoderRenderer :1589-1614):
            // NO csd-0/csd-1 — parameter sets are submitted in-band on first feed
            // (BUFFER_FLAG_CODEC_CONFIG). Moonlight has 0 setByteBuffer(csd) calls.
            pendingSps = spsForCsd.bytes.copyOf()
            pendingPps = pps.bytes.copyOf()
            csdSubmitted = false
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
            }
            // Moonlight has KEY_FRAME_RATE; we lacked it.
            format.setInteger(MediaFormat.KEY_FRAME_RATE, streamFps.coerceAtLeast(1))
            // KEY_MAX_INPUT_SIZE：不给时部分设备默认 input buffer 很小，大 IDR 塞不进
            // 会走 decoderLoop 的容量兜底被整 AU 丢弃 → 表现为「点击瞬间画面卡住」。
            // 按 SPS 分辨率推：宽×高（1080p≈2MB），下限 1MB、上限 8MB。
            val maxInputSize = (fw * fh).coerceIn(MIN_MAX_INPUT_SIZE, MAX_MAX_INPUT_SIZE)
            format.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, maxInputSize)
            // ★ T36 决定性试验（方案 B+C，对齐 Moonlight 解码器选择/降级链）：
            //   系统统计铁证 c2.qti.avc.decoder 吃 6 帧不吐 1 帧（DQin:1/1 Render:0），
            //   而码流本身合法（Lead 抓包 59KB 验证：SPS/PPS/IDR/P 全标准）。
            //   试验：USE_SOFTWARE_DECODER=true 强制走 OMX.google 软解（能出画面 → 高通硬解
            //   问题坐实，后续做「硬解优先失败降级软解」；不能出 → 换路排查码流兼容）。
            val c = if (USE_SOFTWARE_DECODER) {
                createSoftwareDecoder()
            } else {
                MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
            }
            c.configure(format, surface, null, 0)
            c.start()
            // Moonlight configureAndStartDecoder :487-537: set scaling mode AFTER start()
            c.setVideoScalingMode(MediaCodec.VIDEO_SCALING_MODE_SCALE_TO_FIT)
            codec = c
            running.set(true)
            isConfigured = true
            decodeThread = thread(name = "h264-decoder") { decoderLoop(c) }
            // T36 diag（保留①：parse 结果与宽高——装机验证用）
            Log.i(TAG, "configure parse=" + if (info != null) ("ok w=" + w + " h=" + h + " level=" + info.levelIdc + " decoder=" + c.name) else "NULL")
            true
        } catch (e: Exception) {
            onError?.invoke(e)
            false
        }
    }

    /** 喂入一个 AU；丢帧判定在此完成。true=接受，false=丢弃。 */
    fun feed(au: Au): Boolean {
        if (!isConfigured) {
            return false
        }
        val fps = streamFps
        if (queueDepth >= PlayerConstants.skipQueue(fps) && !au.hasKey) {
            skippedAUs++
            skippedTotal++
            // ★ T35b：连丢达 consecutiveDropLimit → 请求 IDR（flush+强制关键帧），
            //   不再直接 onNeedResync 整链重连（对齐 Moonlight：IDR 优先、重连兜底）。
            if (skippedAUs >= PlayerConstants.consecutiveDropLimit(fps)) {
                skippedAUs = 0
                Log.w("SoulLinkPlayer", "连续丢帧 ${skippedTotal}（队列深度 $queueDepth ≥ ${PlayerConstants.skipQueue(fps)}）→ flush + 请求 IDR（不再整链重连）")
                flush()
                onNeedIdr?.invoke()
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
        // ★ T35b 修闪退：先打断阻塞中的 dequeue 再 stop——decoderLoop 的 catch 也能
        //   兜住，但先 interrupt 让线程退出更干净（双重保险）。
        try { decodeThread?.interrupt() } catch (_: Exception) {}
        try { decodeThread?.join(100) } catch (_: Exception) {}
        try {
            codec?.stop()
            codec?.release()
        } catch (e: Exception) {
            // stop/release 竞态下的 IllegalStateException 是预期噪音（解码线程可能
            // 恰在 dequeue 中），静默即可——decoderLoop 侧已区分正常/异常。
        }
        decodeThread = null
        codec = null
        isConfigured = false
    }

    /**
     * T36 trial (plan C simplified, aligned with Moonlight MediaCodecHelper):
     * Pick an AVC decoder from MediaCodecList - prefer Google software decoders
     * (OMX.google / c2.android), exclude Qualcomm c2.qti (eats frames, renders 0).
     */
    private fun createSoftwareDecoder(): MediaCodec {
        return try {
            val list = android.media.MediaCodecList(android.media.MediaCodecList.REGULAR_CODECS)
            val avc = list.codecInfos.filter { info ->
                !info.isEncoder && info.supportedTypes.any { it.equals(MediaFormat.MIMETYPE_VIDEO_AVC, ignoreCase = true) }
            }
            val soft = avc.firstOrNull { it.name.contains("google", true) || it.name.contains("c2.android", true) }
            if (soft != null) {
                Log.i(TAG, "选择软解：" + soft.name + "（候选 " + avc.size + " 个，排除 c2.qti）")
                MediaCodec.createByCodecName(soft.name)
            } else {
                Log.w(TAG, "未找到 Google 软解，退回 createDecoderByType（候选 " + avc.size + " 个）")
                MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
            }
        } catch (e: Exception) {
            Log.w(TAG, "软解选择失败退回默认：" + e.message)
            MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
        }
    }

    private fun decoderLoop(c: MediaCodec) {
        val info = MediaCodec.BufferInfo()
        try {
            decoderLoopInner(c, info)
        } catch (e: InterruptedException) {
            // release() 里 interrupt 兜底打断阻塞中的 dequeue：正常退出，静默。
            if (running.get()) Log.w("SoulLinkPlayer", "解码循环被中断但仍在运行（异常态）：${e.message}")
        } catch (e: IllegalStateException) {
            // ★ T35b 修闪退：release()/stop() 与解码线程的竞态——codec.stop() 会让
            //   阻塞中的 dequeueInput/OutputBuffer 抛 IllegalStateException
            //   （"Pending dequeue ... request cancelled"）。此时 running 已置 false
            //   = 正常收尾路径，**不刷错误日志**；running 仍 true 才是真异常。
            if (running.get()) {
                Log.e("SoulLinkPlayer", "解码循环异常（运行中）：${e.message}")
                onError?.invoke(e)
            } else {
                Log.i("SoulLinkPlayer", "解码循环正常退出（release 竞态，静默收尾）")
            }
        } catch (e: Exception) {
            if (running.get()) {
                Log.e("SoulLinkPlayer", "解码循环异常（运行中）：${e.message}")
                onError?.invoke(e)
            } else {
                Log.i("SoulLinkPlayer", "解码循环正常退出（收尾期异常被吞：${e.javaClass.simpleName}）")
            }
        } finally {
            Log.i("SoulLinkPlayer", "解码线程结束（skippedTotal=$skippedTotal overflowDrops=$inputOverflowDrops）")
        }
    }

    private fun decoderLoopInner(c: MediaCodec, info: MediaCodec.BufferInfo) {
        while (running.get()) {

            // ★ T36 方案 2（Moonlight :1589-1614）：首次喂帧前把 SPS/PPS 以
            //   BUFFER_FLAG_CODEC_CONFIG 提交（Annex-B：起始码+SPS+起始码+PPS）。
            //   ★ 不计 pendingInFlight——CODEC_CONFIG buffer 不产生输出帧，
            //   计了会让「等输出」计数永远多 1，watchdog 误判停滞。
            if (!csdSubmitted) {
                val sps = pendingSps
                val pps = pendingPps
                if (sps != null && pps != null) {
                    val cfg = ByteArray(4 + sps.size + 4 + pps.size)
                    var o = 0
                    cfg[0] = 0; cfg[1] = 0; cfg[2] = 0; cfg[3] = 1
                    System.arraycopy(sps, 0, cfg, 4, sps.size)
                    o = 4 + sps.size
                    cfg[o] = 0; cfg[o + 1] = 0; cfg[o + 2] = 0; cfg[o + 3] = 1
                    System.arraycopy(pps, 0, cfg, o + 4, pps.size)
                    val cfgIdx = c.dequeueInputBuffer(10_000)
                    if (cfgIdx >= 0) {
                        val cfgBuf = c.getInputBuffer(cfgIdx)
                        if (cfgBuf != null) {
                            cfgBuf.clear()
                            cfgBuf.put(cfg)
                            c.queueInputBuffer(cfgIdx, 0, cfg.size, 0, MediaCodec.BUFFER_FLAG_CODEC_CONFIG)
                            csdSubmitted = true
                            Log.i(TAG, "CSD 已码流内提交（CODEC_CONFIG，" + cfg.size + "B，不计 pending）")
                        } else {
                            c.queueInputBuffer(cfgIdx, 0, 0, 0, 0)
                        }
                    }
                }
            }
            // 输入侧：只有队列里真有 AU 才去要 input buffer（旧实现无条件 dequeue 10ms
            // 再 queueInputBuffer(0) 空转，白引入最高 ~20ms/帧 的节拍抖动）。
            if (inputQueue.isNotEmpty()) {
                val inIndex = c.dequeueInputBuffer(DEQUEUE_TIMEOUT_US)
                if (inIndex >= 0) {
                    val au = inputQueue.poll()
                    if (au != null) {
                        val avcc = packAnnexB(au)
                        if (avcc.isEmpty()) {
                            // 纯参数集 AU（SPS/PPS 已由 csd 提供）：空输入，直接归还 buffer
                            c.queueInputBuffer(inIndex, 0, 0, 0, 0)
                        } else {
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
                    // ★ T36（黑屏根因②验证，对齐 Moonlight）：releaseOutputBuffer(idx, true)
                    //   立即上屏。原 computeRenderAtNs() pacing 若时钟异常会永久排队不上屏
                    //   （也是「pendingInFlight 涨满不出帧」的表现）。先 true 验证出画面，
                    //   确认后再决定是否恢复 pacing。
                    c.releaseOutputBuffer(outIndex, true)
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

    /**
     * AU → Annex-B 字节块（每个 NAL 前是 4 字节起始码 00 00 00 01）。
     * ★ T36 黑屏根因（Lead 对照 Moonlight MediaCodecDecoderRenderer.java:1698-1714 实锤）：
     *   csd-0/csd-1 是 Annex-B（SPS 带起始码），MediaCodec 因此期望输入也是 Annex-B。
     *   旧 packAvcc 写的是 AVCC（4 字节大端长度前缀）——长度字节被当 NAL header 解析，
     *   全是垃圾 → 0 输出（软解 Qin:6 DQin:6/6 Render:0；硬解 DQin:1/1 即放弃；
     *   DQout 永远 0、pendingInFlight 涨满卡死 = 解码器从不归还输出 buffer）。
     *   Moonlight 官方：nextInputBuffer.put(decodeUnitData) 原样塞 Annex-B，
     *   replaySps 里明确「Write the Annex B header」。
     * ★ 参数集剥离（上批）：csd-0/csd-1 已提供 SPS/PPS，码流不再重复给。
     *   纯参数集 AU（剥离后空）返回空数组，调用方归还 buffer。
     */
    private fun packAnnexB(au: Au): ByteArray {
        val vcl = au.nals.filter { !it.isSps && !it.isPps }
        if (vcl.isEmpty()) return EMPTY_BYTES   // 纯参数集 AU：无 VCL/SEI 内容，不喂
        var total = 0
        for (n in vcl) total += 4 + n.size      // 4 字节 Annex-B 起始码
        val out = ByteArray(total)
        var off = 0
        for (n in vcl) {
            val len = n.size
            // ★ Annex-B 4 字节起始码（不是 AVCC 长度前缀！）
            out[off] = 0; out[off + 1] = 0; out[off + 2] = 0; out[off + 3] = 1
            System.arraycopy(n.bytes, 0, out, off + 4, len)
            off += 4 + len
        }
        return out
    }

    companion object {
        private val EMPTY_BYTES = ByteArray(0)
        private const val TAG = "SoulLinkPlayer"

        /** ★ T36 试验开关：true = 强制软解（验证高通硬解问题）；验证后改回 false 走降级链。 */
        private const val USE_SOFTWARE_DECODER = false

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
