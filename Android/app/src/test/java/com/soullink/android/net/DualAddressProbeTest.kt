package com.soullink.android.net

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** T44 双地址探测纯逻辑测试。 */
class DualAddressProbeTest {

    // order
    @Test fun order_lan_both_localFirst() {
        assertEquals(listOf("192.168.0.101", "100.93.208.70"), DualAddressProbe.order(true, "192.168.0.101", "100.93.208.70"))
    }
    @Test fun order_notLan_remoteOnly() {
        assertEquals(listOf("100.93.208.70"), DualAddressProbe.order(false, "192.168.0.101", "100.93.208.70"))
    }
    @Test fun order_localBlank_remoteOnly() {
        assertEquals(listOf("100.93.208.70"), DualAddressProbe.order(true, "", "100.93.208.70"))
    }
    @Test fun order_remoteBlank_localOnly() {
        assertEquals(listOf("192.168.0.101"), DualAddressProbe.order(false, "192.168.0.101", ""))
    }
    @Test fun order_bothBlank_empty() {
        assertTrue(DualAddressProbe.order(true, "", "").isEmpty())
    }

    // shouldSwitch（阈值 2——ATM 注释：阈值 1 在 WiFi 必然抖）
    @Test fun switch_after2Fails() { assertEquals(true, DualAddressProbe.shouldSwitch(2)) }
    @Test fun noSwitch_1Fail() { assertEquals(false, DualAddressProbe.shouldSwitch(1)) }

    // prioritize（上次成功的提到队首）
    @Test fun prioritize_remoteLast_remoteFirst() {
        val o = listOf("192.168.0.101", "100.93.208.70")
        assertEquals(listOf("100.93.208.70", "192.168.0.101"),
            DualAddressProbe.prioritize(o, DualAddressProbe.Kind.REMOTE, "192.168.0.101", "100.93.208.70"))
    }
    @Test fun prioritize_localLast_unchanged() {
        val o = listOf("192.168.0.101", "100.93.208.70")
        assertEquals(o, DualAddressProbe.prioritize(o, DualAddressProbe.Kind.LOCAL, "192.168.0.101", "100.93.208.70"))
    }
}