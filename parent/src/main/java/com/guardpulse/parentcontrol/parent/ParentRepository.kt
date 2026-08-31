package com.guardpulse.parentcontrol.parent

import com.google.firebase.auth.FirebaseAuth
import com.google.firebase.database.DatabaseReference
import com.google.firebase.database.ServerValue
import com.guardpulse.parentcontrol.shared.ControlPin
import com.guardpulse.parentcontrol.shared.FirebasePaths
import com.guardpulse.parentcontrol.shared.FirebaseServerClock
import com.guardpulse.parentcontrol.shared.PackageKeys
import com.guardpulse.parentcontrol.shared.PinHasher
import com.guardpulse.parentcontrol.shared.PolicyConstants

internal fun encodedPolicyValues(
    policies: Map<String, ParentPolicy>,
    valueFactory: (String, ParentPolicy) -> Map<String, Any?>
): Map<String, Map<String, Any?>> = policies.map { (packageName, policy) ->
    PackageKeys.encode(packageName) to valueFactory(packageName, policy)
}.toMap()

class ParentRepository(
    private val database: DatabaseReference,
    private val auth: FirebaseAuth = FirebaseAuth.getInstance(),
    private val serverClock: FirebaseServerClock = FirebaseServerClock()
) {
    private val controlWriteQueue = ArrayDeque<() -> Unit>()
    private var controlWriteInFlight = false

    init {
        serverClock.start()
    }

    fun createPairRequest(
        deviceId: String,
        secret: String?,
        manualCode: String,
        onSuccess: (String) -> Unit,
        onError: (String) -> Unit
    ) {
        val uid = requireUid(onError) ?: return
        val ref = database.child(FirebasePaths.pairRequests(deviceId)).push()
        val requestId = ref.key ?: return onError("Could not create pair request")
        ref.setValue(
            mapOf(
                "parentUid" to uid,
                "secret" to secret,
                "code" to manualCode.ifBlank { null },
                "createdAt" to ServerValue.TIMESTAMP,
                "expiresAt" to serverClock.now() + PolicyConstants.PAIRING_TTL_MS,
                "status" to PolicyConstants.COMMAND_PENDING
            )
        ).addOnSuccessListener { onSuccess(requestId) }
            .addOnFailureListener { onError(it.message ?: "Pair request failed") }
    }

    fun seedControlV2(
        deviceId: String,
        policies: Map<String, ParentPolicy>,
        modes: List<ParentMode>,
        activeMode: ActiveMode,
        safeMode: SafeModeState,
        pin: ControlPin?,
        onSuccess: () -> Unit,
        onError: (String) -> Unit
    ) {
        val uid = requireUid(onError) ?: return
        enqueueControlWrite {
            val revisionId = newRevisionId(deviceId)
            val control = mapOf<String, Any?>(
                "schemaVersion" to PolicyConstants.SYNC_PROTOCOL_VERSION,
                "revisionId" to revisionId,
                "updatedAt" to ServerValue.TIMESTAMP,
                "updatedBy" to uid,
                "apps" to encodedPolicyValues(policies, ::appPolicyValue),
                "modes" to modes.associate { mode -> mode.modeId to modeValue(mode) },
                "activeMode" to activeMode.modeId?.let {
                    mapOf(
                        "modeId" to it,
                        "modeName" to activeMode.modeName,
                        "activatedAt" to activeMode.activatedAt
                    )
                },
                "safeMode" to mapOf(
                    "enabled" to safeMode.enabled,
                    "until" to (safeMode.until ?: 0L),
                    "startedAt" to safeMode.startedAt,
                    "startedBy" to safeMode.startedBy
                ),
                "pin" to pin?.let {
                    mapOf(
                        "salt" to it.salt,
                        "hash" to it.hash,
                        "version" to it.version,
                        "algorithm" to it.algorithm,
                        "iterations" to it.iterations,
                        "updatedAt" to it.updatedAt
                    )
                }
            )
            val updates = mutableMapOf<String, Any?>(FirebasePaths.deviceControlV2(deviceId) to control)
            addDesiredRevision(updates, deviceId, revisionId, PolicyConstants.REVISION_MIGRATION, "control", uid)
            database.updateChildren(updates).finishQueuedWrite(onSuccess, onError, "Control migration failed")
        }
    }

    fun updatePolicy(
        deviceId: String,
        packageName: String,
        policy: ParentPolicy,
        onSuccess: () -> Unit,
        onError: (String) -> Unit
    ) {
        val value = appPolicyValue(packageName, policy)
        controlUpdate(deviceId, PolicyConstants.REVISION_APP_POLICY, packageName, onSuccess, onError) { updates ->
            updates[FirebasePaths.devicePolicyApp(deviceId, packageName)] = value
            updates[FirebasePaths.deviceControlV2App(deviceId, packageName)] = value
        }
    }

    fun setPin(
        deviceId: String,
        pin: String,
        onSuccess: () -> Unit,
        onError: (String) -> Unit
    ) {
        val hash = PinHasher.create(pin)
        val value = mapOf(
            "salt" to hash.salt,
            "hash" to hash.hash,
            "version" to hash.version,
            "algorithm" to hash.algorithm,
            "iterations" to hash.iterations,
            "updatedAt" to ServerValue.TIMESTAMP,
            "updatedBy" to auth.currentUser?.uid
        )
        controlUpdate(deviceId, PolicyConstants.REVISION_PIN, "pin", onSuccess, onError) { updates ->
            updates[FirebasePaths.deviceSecurityPin(deviceId)] = value
            updates[FirebasePaths.deviceControlV2Pin(deviceId)] = value
        }
    }

    /** Whole-device daily screen-time budget, 1..1440 minutes; null removes it. */
    fun updateBudget(
        deviceId: String,
        dailyLimitMinutes: Int?,
        onSuccess: () -> Unit,
        onError: (String) -> Unit
    ) {
        if (dailyLimitMinutes != null && dailyLimitMinutes !in 1..1440) {
            onError("Daily budget must be 1..1440 minutes")
            return
        }
        controlUpdate(deviceId, PolicyConstants.REVISION_BUDGET, "budget", onSuccess, onError) { updates ->
            updates[FirebasePaths.deviceControlV2Budget(deviceId)] = dailyLimitMinutes?.let {
                mapOf("dailyLimitMinutes" to it)
            }
        }
    }

    /** Allowlist mode: only inventoried/system apps may run. */
    fun updateAllowlist(
        deviceId: String,
        enabled: Boolean,
        onSuccess: () -> Unit,
        onError: (String) -> Unit
    ) {
        controlUpdate(deviceId, PolicyConstants.REVISION_ALLOWLIST, "allowlist", onSuccess, onError) { updates ->
            updates[FirebasePaths.deviceControlV2Allowlist(deviceId)] = mapOf("enabled" to enabled)
        }
    }

    /** Custom blocked domains (hosts-file): e.g. youtube.com. Empty clears it. */
    fun updateCustomBlockedDomains(
        deviceId: String,
        domains: List<String>,
        onSuccess: () -> Unit,
        onError: (String) -> Unit
    ) {
        val distinct = domains.map { it.trim().lowercase() }.filter { it.isNotBlank() }.distinct()
        if (distinct.size > 100) {
            onError("Too many domains (max 100)")
            return
        }
        controlUpdate(deviceId, PolicyConstants.REVISION_CUSTOM_DOMAINS, "customBlockedDomains", onSuccess, onError) { updates ->
            updates[FirebasePaths.deviceControlV2CustomBlockedDomains(deviceId)] = if (distinct.isEmpty()) null else distinct
        }
    }

    fun updateUnlock(
        deviceId: String,
        request: UnlockRequest,
        status: String,
        approvalType: String? = null,
        approvalDurationMs: Long? = null,
        onSuccess: () -> Unit,
        onError: (String) -> Unit
    ) {
        val value = mutableMapOf<String, Any?>(
            "status" to status,
            "updatedAt" to ServerValue.TIMESTAMP,
            "updatedBy" to auth.currentUser?.uid
        )
        if (approvalType != null) value["approvalType"] = approvalType
        if (approvalDurationMs != null) value["approvalDurationMs"] = approvalDurationMs
        database.child(FirebasePaths.deviceUnlockRequest(deviceId, request.requestId))
            .updateChildren(value)
            .complete(onSuccess, onError, "Unlock update failed")
    }

    fun createMode(
        deviceId: String,
        name: String,
        onSuccess: () -> Unit,
        onError: (String) -> Unit
    ) {
        val modeId = database.child(FirebasePaths.devicePolicyModes(deviceId)).push().key ?: run {
            onError("Could not create mode")
            return
        }
        val value = mapOf(
            "modeId" to modeId,
            "name" to name,
            "createdAt" to ServerValue.TIMESTAMP,
            "updatedAt" to ServerValue.TIMESTAMP,
            "updatedBy" to auth.currentUser?.uid
        )
        controlUpdate(deviceId, PolicyConstants.REVISION_MODE_CREATE, modeId, onSuccess, onError) { updates ->
            updates[FirebasePaths.devicePolicyMode(deviceId, modeId)] = value
            updates[FirebasePaths.deviceControlV2Mode(deviceId, modeId)] = value
        }
    }

    fun updateModeName(
        deviceId: String,
        modeId: String,
        name: String,
        onSuccess: () -> Unit,
        onError: (String) -> Unit
    ) {
        controlUpdate(deviceId, PolicyConstants.REVISION_MODE_UPDATE, modeId, onSuccess, onError) { updates ->
            listOf(
                FirebasePaths.devicePolicyMode(deviceId, modeId),
                FirebasePaths.deviceControlV2Mode(deviceId, modeId)
            ).forEach { path ->
                updates["$path/name"] = name
                updates["$path/updatedAt"] = ServerValue.TIMESTAMP
                updates["$path/updatedBy"] = auth.currentUser?.uid
            }
        }
    }

    fun updateModePolicy(
        deviceId: String,
        modeId: String,
        packageName: String,
        policy: ParentPolicy,
        onSuccess: () -> Unit,
        onError: (String) -> Unit
    ) {
        val value = appPolicyValue(packageName, policy)
        controlUpdate(deviceId, PolicyConstants.REVISION_MODE_POLICY, "$modeId:$packageName", onSuccess, onError) { updates ->
            updates[FirebasePaths.devicePolicyModeApp(deviceId, modeId, packageName)] = value
            updates[FirebasePaths.deviceControlV2ModeApp(deviceId, modeId, packageName)] = value
        }
    }

    fun deleteMode(
        deviceId: String,
        modeId: String,
        activeModeId: String?,
        onSuccess: () -> Unit,
        onError: (String) -> Unit
    ) {
        controlUpdate(deviceId, PolicyConstants.REVISION_MODE_DELETE, modeId, onSuccess, onError) { updates ->
            updates[FirebasePaths.devicePolicyMode(deviceId, modeId)] = null
            updates[FirebasePaths.deviceControlV2Mode(deviceId, modeId)] = null
            if (activeModeId == modeId) {
                updates[FirebasePaths.devicePolicyActiveMode(deviceId)] = null
                updates[FirebasePaths.deviceControlV2ActiveMode(deviceId)] = null
            }
        }
    }

    fun setActiveMode(
        deviceId: String,
        mode: ParentMode?,
        onSuccess: () -> Unit,
        onError: (String) -> Unit
    ) {
        val value = mode?.let {
            mapOf(
                "modeId" to it.modeId,
                "modeName" to it.name,
                "activatedAt" to ServerValue.TIMESTAMP,
                "updatedBy" to auth.currentUser?.uid
            )
        }
        controlUpdate(deviceId, PolicyConstants.REVISION_ACTIVE_MODE, mode?.modeId ?: "none", onSuccess, onError) { updates ->
            updates[FirebasePaths.devicePolicyActiveMode(deviceId)] = value
            updates[FirebasePaths.deviceControlV2ActiveMode(deviceId)] = value
        }
    }

    fun startSafeMode(
        deviceId: String,
        durationMinutes: Int,
        onSuccess: () -> Unit,
        onError: (String) -> Unit
    ) {
        val durationMs = durationMinutes.coerceIn(1, 1440) * 60_000L
        val value = mapOf(
            "enabled" to true,
            "until" to serverClock.now() + durationMs,
            "startedAt" to ServerValue.TIMESTAMP,
            "startedBy" to auth.currentUser?.uid
        )
        controlUpdate(deviceId, PolicyConstants.REVISION_SAFE_MODE, "enabled", onSuccess, onError) { updates ->
            updates[FirebasePaths.deviceSecuritySafeMode(deviceId)] = value
            updates[FirebasePaths.deviceControlV2SafeMode(deviceId)] = value
        }
    }

    fun stopSafeMode(
        deviceId: String,
        onSuccess: () -> Unit,
        onError: (String) -> Unit
    ) {
        val value = mapOf(
            "enabled" to false,
            "until" to 0L,
            "updatedAt" to ServerValue.TIMESTAMP,
            "updatedBy" to auth.currentUser?.uid
        )
        controlUpdate(deviceId, PolicyConstants.REVISION_SAFE_MODE, "disabled", onSuccess, onError) { updates ->
            updates[FirebasePaths.deviceSecuritySafeMode(deviceId)] = value
            updates[FirebasePaths.deviceControlV2SafeMode(deviceId)] = value
        }
    }

    fun sendCommand(
        deviceId: String,
        type: String,
        packageName: String?,
        onSuccess: () -> Unit,
        onError: (String) -> Unit
    ) {
        val uid = requireUid(onError) ?: return
        val value = mutableMapOf<String, Any?>(
            "type" to type,
            "requestedBy" to uid,
            "createdAt" to ServerValue.TIMESTAMP,
            "ttlMs" to PolicyConstants.commandTtlMs(type),
            "status" to PolicyConstants.COMMAND_PENDING
        )
        if (packageName != null) value["packageName"] = packageName
        database.child(FirebasePaths.deviceCommands(deviceId)).push().setValue(value)
            .complete(onSuccess, onError, "Command failed")
    }

    fun removePairedDevice(
        deviceId: String,
        onSuccess: () -> Unit,
        onError: (String) -> Unit
    ) {
        val uid = requireUid(onError) ?: return
        database.child(FirebasePaths.userDevice(uid, deviceId)).removeValue()
            .complete(onSuccess, onError, "Remove failed")
    }

    private fun controlUpdate(
        deviceId: String,
        kind: String,
        target: String,
        onSuccess: () -> Unit,
        onError: (String) -> Unit,
        mutate: (MutableMap<String, Any?>) -> Unit
    ) {
        val uid = requireUid(onError) ?: return
        enqueueControlWrite {
            val revisionId = newRevisionId(deviceId)
            val updates = mutableMapOf<String, Any?>()
            try {
                mutate(updates)
            } catch (e: Exception) {
                onError(e.message ?: "Control update failed")
                controlWriteInFlight = false
                startNextControlWrite()
                return@enqueueControlWrite
            }
            val controlPath = FirebasePaths.deviceControlV2(deviceId)
            updates["$controlPath/schemaVersion"] = PolicyConstants.SYNC_PROTOCOL_VERSION
            updates["$controlPath/revisionId"] = revisionId
            updates["$controlPath/updatedAt"] = ServerValue.TIMESTAMP
            updates["$controlPath/updatedBy"] = uid
            addDesiredRevision(updates, deviceId, revisionId, kind, target, uid)
            database.updateChildren(updates).finishQueuedWrite(onSuccess, onError, "Control update failed")
        }
    }

    private fun addDesiredRevision(
        updates: MutableMap<String, Any?>,
        deviceId: String,
        revisionId: String,
        kind: String,
        target: String,
        uid: String
    ) {
        updates[FirebasePaths.deviceSyncDesired(deviceId)] = mapOf(
            "revisionId" to revisionId,
            "kind" to kind,
            "target" to target,
            "requestedAt" to ServerValue.TIMESTAMP,
            "requestedBy" to uid
        )
    }

    private fun newRevisionId(deviceId: String): String {
        return database.child("${FirebasePaths.deviceSync(deviceId)}/revisionKeys").push().key
            ?: "${serverClock.now()}-${java.util.UUID.randomUUID()}"
    }

    private fun appPolicyValue(packageName: String, policy: ParentPolicy): Map<String, Any?> = mapOf(
        "packageKey" to PackageKeys.encode(packageName),
        "packageName" to packageName,
        "manualBlocked" to policy.manualBlocked,
        "dailyLimitMinutes" to policy.dailyLimitMinutes,
        "updatedAt" to ServerValue.TIMESTAMP
    )

    private fun modeValue(mode: ParentMode): Map<String, Any?> = mapOf(
        "modeId" to mode.modeId,
        "name" to mode.name,
        "createdAt" to mode.createdAt,
        "updatedAt" to mode.updatedAt,
        "apps" to encodedPolicyValues(mode.appPolicies, ::appPolicyValue)
    )

    private fun requireUid(onError: (String) -> Unit): String? {
        return auth.currentUser?.uid ?: run {
            onError("Sign in before changing TV controls")
            null
        }
    }

    private fun enqueueControlWrite(write: () -> Unit) {
        controlWriteQueue.addLast(write)
        startNextControlWrite()
    }

    private fun startNextControlWrite() {
        if (controlWriteInFlight) return
        val next = controlWriteQueue.removeFirstOrNull() ?: return
        controlWriteInFlight = true
        try {
            next()
        } catch (e: Exception) {
            controlWriteInFlight = false
            startNextControlWrite()
        }
    }

    private fun com.google.android.gms.tasks.Task<Void>.finishQueuedWrite(
        onSuccess: () -> Unit,
        onError: (String) -> Unit,
        fallbackMessage: String
    ) {
        addOnSuccessListener { onSuccess() }
            .addOnFailureListener { onError(it.message ?: fallbackMessage) }
            .addOnCompleteListener {
                controlWriteInFlight = false
                startNextControlWrite()
            }
    }

    private fun com.google.android.gms.tasks.Task<Void>.complete(
        onSuccess: () -> Unit,
        onError: (String) -> Unit,
        fallbackMessage: String
    ) {
        addOnSuccessListener { onSuccess() }
            .addOnFailureListener { onError(it.message ?: fallbackMessage) }
    }
}
