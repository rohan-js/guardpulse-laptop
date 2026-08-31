package com.guardpulse.parentcontrol.shared

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class PolicyDeciderTest {
    @Test
    fun manualBlockWinsOverDailyLimit() {
        val decision = PolicyDecider.decide(
            policy = DesiredAppPolicy(
                packageName = "com.video",
                manualBlocked = true,
                dailyLimitMinutes = 60
            ),
            usageMinutesToday = 60,
            alreadyDailyBlocked = false
        )

        assertTrue(decision.shouldBlock)
        assertEquals(PolicyConstants.BLOCK_REASON_MANUAL, decision.reason)
        assertTrue(decision.manualBlocked)
        assertTrue(decision.dailyLimitBlocked)
    }

    @Test
    fun dailyLimitBlocksWhenUsageReachesLimit() {
        val decision = PolicyDecider.decide(
            policy = DesiredAppPolicy("com.video", dailyLimitMinutes = 15),
            usageMinutesToday = 15,
            alreadyDailyBlocked = false
        )

        assertTrue(decision.shouldBlock)
        assertEquals(PolicyConstants.BLOCK_REASON_DAILY_LIMIT, decision.reason)
    }

    @Test
    fun positiveLimitOnly() {
        val normalized = DesiredAppPolicy("com.video", dailyLimitMinutes = 0).normalized()

        assertNull(normalized.dailyLimitMinutes)
    }
}
