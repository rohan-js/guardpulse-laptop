package com.guardpulse.parentcontrol.shared

import org.junit.Assert.assertEquals
import org.junit.Test

class ControlProtocolTest {
    @Test
    fun connectedDeviceUsesHeartbeatFreshnessThresholds() {
        val now = 100_000L

        assertEquals(DeviceFreshness.LIVE, ControlProtocol.freshness(true, now - 44_999L, now))
        assertEquals(DeviceFreshness.DELAYED, ControlProtocol.freshness(true, now - 45_001L, now))
        assertEquals(DeviceFreshness.OFFLINE, ControlProtocol.freshness(true, now - 90_001L, now))
    }

    @Test
    fun disconnectedOrMissingHeartbeatIsOffline() {
        val now = 100_000L

        assertEquals(DeviceFreshness.OFFLINE, ControlProtocol.freshness(false, now, now))
        assertEquals(DeviceFreshness.OFFLINE, ControlProtocol.freshness(true, null, now))
    }
}
