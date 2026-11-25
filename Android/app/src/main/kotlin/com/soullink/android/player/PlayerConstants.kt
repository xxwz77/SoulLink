package com.soullink.android.player

/**
 * 播放器/输入链路常量。
 * 数值对齐 web-index.html（docs/web-index.html）已验证的语义参数。
 */
object PlayerConstants {
    /** 画面看门狗：出过首帧后超过该毫秒数无新解码帧 → 判为断流（对齐 STALL_MS=10000）。 */
    const val STALL_MS = 10_000L

    /**
     * ★ T35b（对齐官方）：decode 队列积压阈值 → 帧率自适应函数。
     * 旧常量 SKIP_QUEUE=2 在 60fps 下过激 3 倍（积压 3 帧 ≈50ms 正常抖动就进丢帧模式）。
     * 官方（网页端 web-index.html 已修版本 + Moonlight 同比例）：
     *   max(4, round(fps/10))——60fps→6，30fps→4，120fps→12。
     */
    fun skipQueue(fps: Int): Int = maxOf(4, (if (fps > 0) fps else 30) / 10)

    /**
     * ★ T35b（对齐 Moonlight VideoDepacketizer.c CONSECUTIVE_DROP_LIMIT=120）：
     * 连续丢帧达该值 → 请求 IDR（flush + POST /api/idr，不重连）。
     * 旧常量 SKIP_RESET_LIMIT=10 在 60fps 下 ≈166ms 就触发（官方 ≈2 秒）。
     * 官方比例：60fps→120；fps*2，下限 30（30fps→60 也比旧的 10 宽 6 倍）。
     */
    fun consecutiveDropLimit(fps: Int): Int = if (fps > 0) fps * 2 else 120

    /** 退避重连上限（ms）（对齐 Math.min(1000*2^n, 8000)）。 */
    const val RECONNECT_MAX_DELAY_MS = 8_000L

    /** 输入合并节流窗口（对齐 LOOK_HZ_MS / MOVE_MIN_INTERVAL_MS = 8ms）。 */
    const val INPUT_THROTTLE_MS = 8L

    /** 按住心跳间隔（对齐 web 端 ensureHeldHeartbeat，代理 WebIdleReleaseMs=5000 > 2×2000）。 */
    const val HELD_HEARTBEAT_MS = 2_000L

    const val LONG_PRESS_MS = 650L    // A3 对齐 Moonlight RelativeTouchContext DRAG_TIME=650ms
    const val DRAG_THRESHOLD_DP = 12
    const val TAP_TIME_MS = 250L        // A3：tap 时间窗（对齐 TAP_TIME=250ms）
    const val TAP_MOVEMENT_DP = 7       // ★ 单位修正：官方 20px 在 3x 屏 ≈ 6.7dp；7dp 密度无关
    const val TAP_PATH_DP = 8           // ★ 官方 25px@3x ≈ 8dp

    /** T35: wait this long (ms) for the first key frame before requesting a forced IDR. */
    const val IDR_REQUEST_DELAY_MS = 800L

    /** T35: max number of IDR requests while waiting for the first key frame. */
    const val IDR_REQUEST_MAX_RETRIES = 3

    /** T35: no key frame at all this long (ms) after connect -> reconnect (new subscribe = server sends IDR). */
    const val FIRST_FRAME_DEADLOCK_MS = 10_000L
}
