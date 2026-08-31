package com.guardpulse.parentcontrol.parent

import com.guardpulse.parentcontrol.shared.DeviceFreshness
import com.guardpulse.parentcontrol.shared.PolicyConstants
import com.guardpulse.parentcontrol.shared.SyncAppliedRevision
import com.guardpulse.parentcontrol.shared.SyncDesiredRevision

internal fun deriveSyncStatus(
    phoneConnected: Boolean,
    controlAvailability: ControlAvailability,
    protocolVersion: Int,
    desired: SyncDesiredRevision?,
    applied: SyncAppliedRevision,
    freshness: DeviceFreshness
): ParentSyncStatus = when {
    controlAvailability == ControlAvailability.INVALID -> ParentSyncStatus.FAILED
    !phoneConnected -> ParentSyncStatus.SENDING
    controlAvailability == ControlAvailability.VALID &&
        protocolVersion < PolicyConstants.SYNC_PROTOCOL_VERSION -> ParentSyncStatus.TV_UPDATE_REQUIRED
    desired?.revisionId != null &&
        applied.revisionId == desired.revisionId &&
        applied.status == PolicyConstants.SYNC_STATUS_FAILED -> ParentSyncStatus.FAILED
    desired?.revisionId != null &&
        applied.revisionId != desired.revisionId &&
        freshness == DeviceFreshness.OFFLINE -> ParentSyncStatus.OFFLINE_PENDING
    desired?.revisionId != null &&
        applied.revisionId != desired.revisionId &&
        freshness == DeviceFreshness.DELAYED -> ParentSyncStatus.DELAYED
    desired?.revisionId != null && applied.revisionId != desired.revisionId ->
        ParentSyncStatus.WAITING_FOR_TV
    desired?.revisionId != null && applied.revisionId == desired.revisionId ->
        ParentSyncStatus.APPLIED
    freshness == DeviceFreshness.DELAYED -> ParentSyncStatus.DELAYED
    else -> ParentSyncStatus.IDLE
}

internal fun matchingRuntimeStates(
    revisionId: String,
    runtimeStates: Map<String, ParentState>
): Map<String, ParentState> = runtimeStates.filterValues { it.controlRevisionId == revisionId }

internal fun effectiveUsageMs(state: ParentState, serverNow: Long): Long {
    if (!state.foregroundActive) return state.usageMsToday.coerceAtLeast(0L)
    val capturedAt = state.usageCapturedAt ?: return state.usageMsToday.coerceAtLeast(0L)
    val elapsed = (serverNow - capturedAt)
        .coerceIn(0L, PolicyConstants.FOREGROUND_USAGE_EXTRAPOLATION_MAX_MS)
    return (state.usageMsToday + elapsed).coerceAtLeast(0L)
}
