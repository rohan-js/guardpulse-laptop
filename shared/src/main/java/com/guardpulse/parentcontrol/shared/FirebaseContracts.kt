package com.guardpulse.parentcontrol.shared

object FirebaseContracts {
    const val FIELD_PACKAGE_NAME = "packageName"
    const val FIELD_MANUAL_BLOCKED = "manualBlocked"
    const val FIELD_DAILY_LIMIT_MINUTES = "dailyLimitMinutes"
    const val FIELD_UPDATED_AT = "updatedAt"
    const val FIELD_UPDATED_BY = "updatedBy"
    const val FIELD_STATUS = "status"
    const val FIELD_TYPE = "type"
    const val FIELD_REQUEST_ID = "requestId"
    const val FIELD_CREATED_AT = "createdAt"
    const val FIELD_EXPIRES_AT = "expiresAt"
    const val FIELD_REASON = "reason"
    const val FIELD_APPROVAL_TYPE = "approvalType"
    const val FIELD_APPROVAL_DURATION_MS = "approvalDurationMs"

    const val MAX_DAILY_LIMIT_MINUTES = 24 * 60
    const val PIN_LENGTH = 6
    const val HASH_MIN_LENGTH = 32
    const val HASH_MAX_LENGTH = 128

    val commandTypes = setOf(
        PolicyConstants.COMMAND_RESCAN_APPS,
        PolicyConstants.COMMAND_RESET_TODAY,
        PolicyConstants.COMMAND_UNPAIR,
        PolicyConstants.COMMAND_OPEN_SETUP
    )

    val unlockStatuses = setOf(
        PolicyConstants.UNLOCK_PENDING,
        PolicyConstants.UNLOCK_APPROVED,
        PolicyConstants.UNLOCK_DENIED,
        PolicyConstants.UNLOCK_EXPIRED
    )

    val unlockApprovalTypes = setOf(
        PolicyConstants.UNLOCK_APPROVAL_ONE_VISIT,
        PolicyConstants.UNLOCK_APPROVAL_TIMED
    )

    val timedUnlockDurationsMs = setOf(
        PolicyConstants.UNLOCK_15_MINUTES_MS,
        PolicyConstants.UNLOCK_30_MINUTES_MS
    )

    val enforcementModes = setOf(
        PolicyConstants.ENFORCEMENT_DEVICE_OWNER,
        PolicyConstants.ENFORCEMENT_FALLBACK,
        PolicyConstants.ENFORCEMENT_UNPROTECTED
    )
}

data class DesiredAppPolicy(
    val packageName: String,
    val manualBlocked: Boolean = false,
    val dailyLimitMinutes: Int? = null
) {
    fun normalized(): DesiredAppPolicy {
        return copy(dailyLimitMinutes = dailyLimitMinutes?.takeIf { it > 0 })
    }

    fun isValid(): Boolean {
        return packageName.isNotBlank() &&
            (dailyLimitMinutes == null || dailyLimitMinutes in 1..FirebaseContracts.MAX_DAILY_LIMIT_MINUTES)
    }
}

data class AppliedAppState(
    val packageName: String,
    val requestedSuspended: Boolean,
    val suspended: Boolean,
    val manualBlocked: Boolean,
    val dailyLimitBlocked: Boolean,
    val networkBlocked: Boolean,
    val vpnApplied: Boolean,
    val vpnActive: Boolean,
    val lockBlocked: Boolean,
    val lockReason: String?,
    val vpnLastError: String?,
    val blockReason: String?,
    val enforcementMode: String,
    val fallbackLocked: Boolean,
    val usageMinutesToday: Long,
    val dailyLimitMinutes: Int?,
    val blockable: Boolean,
    val lastError: String?
)

data class ProtectionRuntime(
    val enforcementMode: String,
    val deviceOwner: Boolean,
    val deviceAdmin: Boolean,
    val deviceAdminSetupAvailable: Boolean,
    val accessibility: Boolean,
    val usageAccess: Boolean,
    val vpnPrepared: Boolean,
    val vpnActive: Boolean,
    val vpnBlockedCount: Int,
    val vpnLastError: String?,
    val backgroundUnrestricted: Boolean,
    val pinConfigured: Boolean,
    val protectionHealthy: Boolean,
    val lastForegroundPackage: String?,
    val lastSyncError: String?
)

data class PolicyDecision(
    val shouldBlock: Boolean,
    val reason: String?,
    val manualBlocked: Boolean,
    val dailyLimitBlocked: Boolean
)

object PolicyDecider {
    fun decide(
        policy: DesiredAppPolicy,
        usageMinutesToday: Long,
        alreadyDailyBlocked: Boolean
    ): PolicyDecision {
        val normalized = policy.normalized()
        val dailyLimitBlocked = alreadyDailyBlocked ||
            (normalized.dailyLimitMinutes != null && usageMinutesToday >= normalized.dailyLimitMinutes)
        return when {
            normalized.manualBlocked -> PolicyDecision(
                shouldBlock = true,
                reason = PolicyConstants.BLOCK_REASON_MANUAL,
                manualBlocked = true,
                dailyLimitBlocked = dailyLimitBlocked
            )
            dailyLimitBlocked -> PolicyDecision(
                shouldBlock = true,
                reason = PolicyConstants.BLOCK_REASON_DAILY_LIMIT,
                manualBlocked = false,
                dailyLimitBlocked = true
            )
            else -> PolicyDecision(
                shouldBlock = false,
                reason = null,
                manualBlocked = false,
                dailyLimitBlocked = false
            )
        }
    }
}
