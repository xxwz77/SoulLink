package com.soullink.android.player

/**
 * 播放器/输入链路常量。
 * 数值对齐 web-index.html（docs/web-index.html）已验证的语义参数。
 */
object PlayerConstants {
    /** 画面看门狗：出过首帧后超过该毫秒数无新解码帧 → 判为断流（对齐 STALL_MS=10000）。 */
    const val STALL_MS = 10_000L

    /** decode 队列积压阈值：超过即进入丢帧模式，只放行关键帧（对齐 SKIP_QUEUE）。 */
    const val SKIP_QUEUE = 2

    /** 连续丢帧达该值 → 重同步（flush + 重新拉流拿 IDR）（对齐 SKIP_RESET_LIMIT）。 */
    const val SKIP_RESET_LIMIT = 10

    /** 退避重连上限（ms）（对齐 Math.min(1000*2^n, 8000)）。 */
    const val RECONNECT_MAX_DELAY_MS = 8_000L

    /** 输入合并节流窗口（对齐 LOOK_HZ_MS / MOVE_MIN_INTERVAL_MS = 8ms）。 */
    const val INPUT_THROTTLE_MS = 8L

    /** 按住心跳间隔（对齐 web 端 ensureHeldHeartbeat，代理 WebIdleReleaseMs=5000 > 2×2000）。 */
    const val HELD_HEARTBEAT_MS = 2_000L
}
