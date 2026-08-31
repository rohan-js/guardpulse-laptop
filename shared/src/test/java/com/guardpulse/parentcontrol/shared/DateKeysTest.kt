package com.guardpulse.parentcontrol.shared

import org.junit.Assert.assertTrue
import org.junit.Test

class DateKeysTest {
    @Test
    fun todayUsesStableLocalDayShape() {
        assertTrue(DateKeys.today().matches(Regex("\\d{4}-\\d{2}-\\d{2}")))
    }
}
