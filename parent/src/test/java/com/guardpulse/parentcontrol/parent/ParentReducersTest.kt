package com.guardpulse.parentcontrol.parent

import com.guardpulse.parentcontrol.shared.DeviceFreshness
import com.guardpulse.parentcontrol.shared.PolicyConstants
import com.guardpulse.parentcontrol.shared.SyncAppliedRevision
import com.guardpulse.parentcontrol.shared.SyncDesiredRevision
import org.junit.Assert.assertEquals
import org.junit.Test

class ParentReducersTest {
    @Test
    fun pendingOfflineRevisionDoesNotBecomeApplied() {
        val status = deriveSyncStatus(
            phoneConnected = true,
            controlAvailability = ControlAvailability.VALID,
            protocolVersion = 2,
            desired = SyncDesiredRevision("new", PolicyConstants.REVISION_APP_POLICY),
            applied = SyncAppliedRevision(revisionId = "old", status = PolicyConstants.SYNC_STATUS_APPLIED),
            freshness = DeviceFreshness.OFFLINE
        )
        assertEquals(ParentSyncStatus.OFFLINE_PENDING, status)
    }

    @Test
    fun invalidControlDisablesNormalSynchronizationState() {
        val status = deriveSyncStatus(
            phoneConnected = true,
            controlAvailability = ControlAvailability.INVALID,
            protocolVersion = 2,
            desired = null,
            applied = SyncAppliedRevision(),
            freshness = DeviceFreshness.LIVE
        )
        assertEquals(ParentSyncStatus.FAILED, status)
    }

    @Test
    fun liveUsageExtrapolationStopsAfterTwentySeconds() {
        val state = ParentState(
            usageMsToday = 10_000L,
            usageCapturedAt = 100_000L,
            foregroundActive = true
        )
        assertEquals(15_000L, effectiveUsageMs(state, 105_000L))
        assertEquals(30_000L, effectiveUsageMs(state, 150_000L))
    }

    @Test
    fun runtimeConfirmationKeepsOnlyMatchingRevision() {
        val matching = matchingRuntimeStates(
            "new",
            mapOf(
                "a" to ParentState(controlRevisionId = "new"),
                "b" to ParentState(controlRevisionId = "old")
            )
        )
        assertEquals(setOf("a"), matching.keys)
    }
}
