package com.soullink.android.player

import android.os.SystemClock
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * 转视角位移合并节流（对齐 web-index.html lookDelta/flushLook：8ms 窗口合并发 moverel）。
 */
class LookThrottler(
    private val scope: CoroutineScope,
    private val onSend: (dx: Int, dy: Int) -> Unit,
) {
    private var accDx = 0
    private var accDy = 0
    private var lastSent = 0L
    private var timer: Job? = null

    @Synchronized
    fun add(dx: Int, dy: Int) {
        accDx += dx
        accDy += dy
        val now = SystemClock.elapsedRealtime()
        if (now - lastSent >= PlayerConstants.INPUT_THROTTLE_MS) {
            lastSent = now
            timer?.cancel(); timer = null
            flush()
        } else if (timer == null) {
            timer = scope.launch {
                delay(PlayerConstants.INPUT_THROTTLE_MS - (now - lastSent))
                timer = null
                lastSent = SystemClock.elapsedRealtime()
                flush()
            }
        }
    }

    @Synchronized
    fun flush() {
        if (accDx == 0 && accDy == 0) return
        val dx = accDx
        val dy = accDy
        accDx = 0
        accDy = 0
        onSend(dx, dy)
    }
}

/**
 * 绝对移动 latest-wins + 8ms 节流（对齐 web-index.html sendMoveThrottled）。
 */
class MoveThrottler(
    private val scope: CoroutineScope,
    private val onSend: (nx: Double, ny: Double) -> Unit,
) {
    private var pendingX = 0.0
    private var pendingY = 0.0
    private var hasPending = false
    private var lastSent = 0L
    private var timer: Job? = null

    @Synchronized
    fun add(nx: Double, ny: Double) {
        pendingX = nx
        pendingY = ny
        hasPending = true
        pump()
    }

    @Synchronized
    private fun pump() {
        if (!hasPending) return
        val now = SystemClock.elapsedRealtime()
        val wait = PlayerConstants.INPUT_THROTTLE_MS - (now - lastSent)
        if (wait > 0) {
            if (timer == null) {
                timer = scope.launch {
                    delay(wait)
                    timer = null
                    pump()
                }
            }
            return
        }
        hasPending = false
        lastSent = SystemClock.elapsedRealtime()
        onSend(pendingX, pendingY)
    }

    /**
     * ★ T41g 修「操作快落点偏移」：立即发出压着的最终位置（绕过 8ms 窗口），
     *   然后发 up——保证 up 带的最终坐标不会被迟到的旧 move 覆盖。
     *   场景：快速拖动结束，up 与 throttler 里的 pending 竞速。
     */
    @Synchronized
    fun flushNow() {
        timer?.cancel()
        timer = null
        if (hasPending) {
            hasPending = false
            lastSent = SystemClock.elapsedRealtime()
            onSend(pendingX, pendingY)
        }
    }
}

/**
 * 摇杆向量 → 相对位移增量（moverel）。
 * 摇杆输出归一化方向 (nx, ny)，乘以每 tick 位移系数（sensitivityPx）得像素级增量。
 */
object JoystickMapper {
    data class Vector(val dx: Int, val dy: Int)

    fun toDelta(nx: Float, ny: Float, sensitivityPx: Float): Vector {
        val dx = (nx * sensitivityPx).toInt()
        val dy = (ny * sensitivityPx).toInt()
        return Vector(dx, dy)
    }
}
