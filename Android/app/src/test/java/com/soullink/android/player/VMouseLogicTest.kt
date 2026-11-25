package com.soullink.android.player

import org.junit.Assert.assertEquals
import org.junit.Test

/** P0-5 虚拟鼠标手柄纯逻辑单元测试。 */
class VMouseLogicTest {

    // 点击 vs 拖拽判定（< 12dp 且 < 500ms → click）
    @Test fun tap_smallMove_shortTime() { assertEquals(true, VMouseLogic.isTap(10f, 200L, 12f)) }
    @Test fun drag_bigMove() { assertEquals(false, VMouseLogic.isTap(30f, 200L, 12f)) }
    @Test fun drag_longTime() { assertEquals(false, VMouseLogic.isTap(5f, 600L, 12f)) }
    @Test fun tap_atBoundary_notTap() { assertEquals(false, VMouseLogic.isTap(12f, 200L, 12f)) }   // 12 >= 12 → 拖

    // 坐标换算（offset → 归一化，clamp 0~1）
    @Test fun norm_halfScreen() { assertEquals(0.5, VMouseLogic.offsetToNorm(540f, 1080f), 0.001) }
    @Test fun norm_clampOver() { assertEquals(1.0, VMouseLogic.offsetToNorm(1200f, 1080f), 0.001) }
    @Test fun norm_clampUnder() { assertEquals(0.0, VMouseLogic.offsetToNorm(-50f, 1080f), 0.001) }

    // 边界 clamp（控件中心不出屏）
    @Test fun clamp_minHalf() { assertEquals(100f, VMouseLogic.clampOffset(50f, 100f, 1080f), 0.01f) }
    @Test fun clamp_maxScreenMinusHalf() { assertEquals(980f, VMouseLogic.clampOffset(1050f, 100f, 1080f), 0.01f) }
    @Test fun clamp_inRange_unchanged() { assertEquals(540f, VMouseLogic.clampOffset(540f, 100f, 1080f), 0.01f) }

    // 滚轮累积（每 8dp=step 一格；余量保留）
    @Test fun wheel_16px_2steps() { val (s, r) = VMouseLogic.wheelAccum(16f, 8f); assertEquals(2, s); assertEquals(0f, r, 0.01f) }
    @Test fun wheel_20px_2steps_remain4() { val (s, r) = VMouseLogic.wheelAccum(20f, 8f); assertEquals(2, s); assertEquals(4f, r, 0.01f) }
    @Test fun wheel_7px_0steps() { val (s, _) = VMouseLogic.wheelAccum(7f, 8f); assertEquals(0, s) }
    @Test fun wheel_negative_1step() { val (s, _) = VMouseLogic.wheelAccum(-9f, 8f); assertEquals(-1, s) }
}