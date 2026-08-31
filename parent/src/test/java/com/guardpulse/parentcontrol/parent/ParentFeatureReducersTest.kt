package com.guardpulse.parentcontrol.parent

import com.guardpulse.parentcontrol.shared.PolicyConstants
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ParentFeatureReducersTest {
    @Test
    fun featureValidationIsDeterministic() {
        assertEquals("Enter an email address", authValidationMessage("", "123456"))
        assertNull(authValidationMessage("parent@example.com", "123456"))
        assertEquals("PIN must be 6 digits", pinValidationMessage("123"))
        assertNull(pinValidationMessage("123456"))
        assertNull(safeModeValidationMessage(30))
        assertEquals(
            "Safe Mode duration must be between 1 and 1440 minutes",
            safeModeValidationMessage(0)
        )
    }

    @Test
    fun selectionNeverKeepsAnUnpairedDevice() {
        val devices = listOf(
            ParentDevice(deviceId = "tv-a", label = "TV A", lastSeen = null),
            ParentDevice(deviceId = "tv-b", label = "TV B", lastSeen = null)
        )
        assertEquals("tv-b", preferredDeviceId(devices, "removed", "tv-b"))
        assertNull(preferredDeviceId(devices, "removed", "also-removed"))
    }

    @Test
    fun confirmedStateDoesNotFallBackToUnacknowledgedRuntime() {
        val live = ParentState(lockBlocked = true, controlRevisionId = "pending")
        val confirmed = confirmedAppState(
            hasConfirmedControl = true,
            packageName = "com.video",
            liveStates = mapOf("com.video" to live),
            confirmedStates = emptyMap()
        )
        assertFalse(confirmed.lockBlocked)
    }

    @Test
    fun modeAndUnlockSummariesUseCurrentData() {
        val mode = ParentMode(
            modeId = "study",
            name = "Study",
            appPolicies = mapOf(
                "com.video" to ParentPolicy(manualBlocked = true),
                "com.game" to ParentPolicy(dailyLimitMinutes = 30)
            )
        )
        assertEquals(ModeRuleSummary(1, 1), modeRuleSummary(mode))
        assertTrue(
            isPendingUnlock(
                UnlockRequest(
                    requestId = "request",
                    packageName = "com.video",
                    reason = PolicyConstants.BLOCK_REASON_MANUAL,
                    status = PolicyConstants.UNLOCK_PENDING,
                    createdAt = 500L,
                    expiresAt = 2_000L
                ),
                now = 1_000L
            )
        )
    }

    @Test
    fun migrationKeepsRealPackageNameInsideEncodedKey() {
        val values = encodedPolicyValues(
            mapOf("com.video" to ParentPolicy(manualBlocked = true))
        ) { packageName, policy ->
            mapOf(
                "packageName" to packageName,
                "manualBlocked" to policy.manualBlocked
            )
        }
        assertEquals("com.video", values["Y29tLnZpZGVv"]?.get("packageName"))
    }

    @Test
    fun legacyDoubleEncodedPackageNameUsesThePathKeyAsAuthority() {
        assertEquals(
            "com.video",
            normalizedPackageName(
                encodedKey = "Y29tLnZpZGVv",
                storedPackageName = "Y29tLnZpZGVv"
            )
        )
        assertEquals(
            "com.video",
            normalizedPackageName(
                encodedKey = "Y29tLnZpZGVv",
                storedPackageName = "com.video"
            )
        )
    }
}
