package com.soullink.android.player

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * P0-4 摇杆逻辑单元测试（阶段一：零设备验证）。
 * 覆盖：8 方向映射 / 死区 / 切换防双键 / 回中。
 */
class JoystickLogicTest {

    private val keys = listOf("W", "D", "S", "A")   // [上,右,下,左]

    // 8 方向映射
    @Test fun pushUp_returnsW() { assertEquals("W", keys[JoystickLogic.dirKeyIndex(JoystickLogic.dirFromAngle(90.0))]) }
    @Test fun pushRight_returnsD() { assertEquals("D", keys[JoystickLogic.dirKeyIndex(JoystickLogic.dirFromAngle(0.0))]) }
    @Test fun pushDown_returnsS() { assertEquals("S", keys[JoystickLogic.dirKeyIndex(JoystickLogic.dirFromAngle(-90.0))]) }
    @Test fun pushLeft_returnsA() { assertEquals("A", keys[JoystickLogic.dirKeyIndex(JoystickLogic.dirFromAngle(180.0))]) }
    @Test fun pushUpLeft_returnsW() { assertEquals("W", keys[JoystickLogic.dirKeyIndex(JoystickLogic.dirFromAngle(135.0))]) }   // 左上 y 主导 → W
    @Test fun pushDownLeft_returnsA() { assertEquals("A", keys[JoystickLogic.dirKeyIndex(JoystickLogic.dirFromAngle(-135.0))]) }   // 左下 x 主导 → A
    @Test fun pushUpRight_returnsD() { assertEquals("D", keys[JoystickLogic.dirKeyIndex(JoystickLogic.dirFromAngle(45.0))]) }   // 右上 x 主导 → D
    @Test fun pushDownRight_returnsD() { assertEquals("D", keys[JoystickLogic.dirKeyIndex(JoystickLogic.dirFromAngle(-45.0))]) }   // 右下 x 主导 → D

    // 死区
    @Test fun deadzoneInside25_noKey() { assertEquals(-1, JoystickLogic.applyDeadzone(0.25, 0.30)) }
    @Test fun deadzoneOutside35_sendsKey() { assertEquals(1, JoystickLogic.applyDeadzone(0.35, 0.30)) }
    @Test fun deadzoneBoundary30_sendsKey() { assertEquals(1, JoystickLogic.applyDeadzone(0.30, 0.30)) }   // 边界=有效推力（< 死区才忽略）

    // 切换防双键（先 KeyUp 旧再 KeyDown 新）
    @Test fun wToA_upWthenDownA() {
        val (up, down) = JoystickLogic.transitionKeys(0, 3, keys)
        assertEquals("W", up); assertEquals("A", down)
    }
    @Test fun dToS_upDthenDownS() {
        val (up, down) = JoystickLogic.transitionKeys(1, 2, keys)
        assertEquals("D", up); assertEquals("S", down)
    }
    @Test fun diagUpRightToDownLeft_DtoA() {
        val (up, down) = JoystickLogic.transitionKeys(7, 5, keys)
        assertEquals("D", up); assertEquals("A", down)
    }
    @Test fun toCenter_upOnly() {
        val (up, down) = JoystickLogic.transitionKeys(0, -1, keys)
        assertEquals("W", up); assertNull(down)
    }
    @Test fun fromCenter_downOnly() {
        val (up, down) = JoystickLogic.transitionKeys(-1, 1, keys)
        assertNull(up); assertEquals("D", down)
    }

    // 回中 → KeyUp 全部
    @Test fun releaseAll_returnsAll4Keys() { assertEquals(keys, JoystickLogic.releaseAllKeys(keys)) }
}