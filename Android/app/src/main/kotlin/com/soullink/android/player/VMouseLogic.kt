package com.soullink.android.player

/**
 * 虚拟鼠标手柄纯逻辑（可单测）：点击/拖拽判定、坐标换算、滚轮累积、边界 clamp。
 * 交互模型（用户三次澄清）：拖中圆=移控件+光标一体；按住左弧+拖=真拖拽
 * （先不发 down，移动超阈值才 down；抬起未拖=click，拖了=up——防三次事件）。
 */
object VMouseLogic {

    /** 点击 vs 拖拽判定（标准做法）：移动 < 阈值 且 时长 < 上限 → click。 */
    fun isTap(movedPx: Float, elapsedMs: Long, thresholdPx: Float, maxMs: Long = 500L): Boolean =
        movedPx < thresholdPx && elapsedMs < maxMs

    /** 控件 offset(px) → 远端归一化坐标（clamp 0~1）。 */
    fun offsetToNorm(offsetPx: Float, screenPx: Float): Double =
        (offsetPx / screenPx).coerceIn(0f, 1f).toDouble()

    /** 控件拖动边界 clamp（控件中心不出屏）。 */
    fun clampOffset(offset: Float, halfSize: Float, screen: Float): Float =
        offset.coerceIn(halfSize, screen - halfSize)

    /**
     * 滚轮累积：滑动位移 px → 每 stepPx 一格 wheel(120)。
     * 返回 (本次应发格数, 剩余余量 px)——余量留到下次（防累计丢步）。
     */
    fun wheelAccum(totalDeltaPx: Float, stepPx: Float): Pair<Int, Float> {
        if (stepPx <= 0f) return 0 to 0f
        val steps = (totalDeltaPx / stepPx).toInt()
        val remain = totalDeltaPx - steps * stepPx
        return steps to remain
    }
}