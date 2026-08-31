package com.guardpulse.parentcontrol.parent

import android.os.Handler
import android.os.Looper
import com.google.firebase.database.DataSnapshot
import com.google.firebase.database.DatabaseError
import com.google.firebase.database.DatabaseReference
import com.google.firebase.database.Query
import com.google.firebase.database.ValueEventListener
import com.guardpulse.parentcontrol.shared.ControlPin
import com.guardpulse.parentcontrol.shared.ControlProtocol
import com.guardpulse.parentcontrol.shared.ControlSnapshotV2
import com.guardpulse.parentcontrol.shared.FirebasePaths
import com.guardpulse.parentcontrol.shared.PackageKeys
import com.guardpulse.parentcontrol.shared.PinHasher
import com.guardpulse.parentcontrol.shared.PolicyConstants
import com.guardpulse.parentcontrol.shared.SyncAppliedRevision
import com.guardpulse.parentcontrol.shared.SyncRuntimeState

class ParentSyncRepository(private val database: DatabaseReference) {
    interface DeviceObserver {
        fun onApps(value: Map<String, ParentApp>)
        fun onPolicies(value: Map<String, ParentPolicy>)
        fun onModes(value: List<ParentMode>)
        fun onActiveMode(value: ActiveMode)
        fun onSafeMode(value: SafeModeState)
        fun onPin(value: ControlPin?)
        fun onStates(value: Map<String, ParentState>)
        fun onSecurity(value: SecurityRuntime)
        fun onUnlockRequests(value: List<UnlockRequest>)
        fun onTamperEvents(value: List<TamperEvent>)
        fun onCommands(value: List<ParentCommand>)
        fun onActivityCurrent(value: DeviceActivity?)
        fun onActivityHistory(value: List<ActivityHistoryEntry>)
        fun onDesiredRevision(snapshot: DataSnapshot)
        fun onAppliedRevision(value: SyncAppliedRevision)
        fun onSyncRuntime(value: SyncRuntimeState)
        fun onControlV2(
            availability: ControlAvailability,
            value: ControlSnapshotV2?,
            error: String? = null
        )
        fun onBrowser(value: BrowserState?)
        fun onError(message: String)
    }

    private class Registration(
        val query: Query,
        val listener: ValueEventListener,
        private val onRemove: () -> Unit
    ) {
        fun remove() {
            query.removeEventListener(listener)
            onRemove()
        }
    }

    private val handler = Handler(Looper.getMainLooper())
    private val detailRegistrations = mutableListOf<Registration>()
    private val keptSynced = mutableListOf<Query>()
    private var deviceRegistration: Registration? = null
    private var connectionRegistration: Registration? = null
    private var pairingRegistration: Registration? = null
    private var currentUid: String? = null
    private var currentDeviceId: String? = null
    private var currentDevicesCallback: ((List<ParentDevice>) -> Unit)? = null
    private var currentErrorCallback: ((String) -> Unit)? = null
    private var currentObserver: DeviceObserver? = null
    private var retryDelayMs = INITIAL_RETRY_MS
    private val retryRunnable = Runnable(::reattach)
    private val retentionCleaner = ParentRetentionCleaner(database)

    fun observeConnection(onConnected: (Boolean) -> Unit) {
        connectionRegistration?.remove()
        connectionRegistration = register(database.root.child(".info/connected"), false, onError = {}) { snapshot ->
            onConnected(snapshot.getValue(Boolean::class.java) ?: false)
        }
    }

    fun observeDevices(
        uid: String,
        onDevices: (List<ParentDevice>) -> Unit,
        onError: (String) -> Unit
    ) {
        currentUid = uid
        currentDevicesCallback = onDevices
        currentErrorCallback = onError
        attachDeviceList()
    }

    fun observeDevice(deviceId: String, observer: DeviceObserver) {
        clearDeviceDetails()
        currentDeviceId = deviceId
        currentObserver = observer
        attachDeviceDetails()
    }

    fun clearSelectedDevice() {
        clearDeviceDetails()
    }

    fun refresh() {
        retryDelayMs = INITIAL_RETRY_MS
        reattach()
    }

    fun observePairRequest(
        deviceId: String,
        requestId: String,
        onValue: (PairRequestState?) -> Unit,
        onError: (String) -> Unit
    ) {
        pairingRegistration?.remove()
        pairingRegistration = register(
            database.child(FirebasePaths.pairRequest(deviceId, requestId)),
            keepSynced = false,
            onError = onError
        ) { snapshot ->
            onValue(
                if (!snapshot.exists()) null else PairRequestState(
                    deviceId = deviceId,
                    requestId = requestId,
                    status = snapshot.child("status").getValue(String::class.java)
                        ?: PolicyConstants.COMMAND_PENDING,
                    createdAt = snapshot.child("createdAt").getValue(Long::class.java),
                    expiresAt = snapshot.child("expiresAt").getValue(Long::class.java),
                    error = snapshot.child("error").getValue(String::class.java)
                )
            )
        }
    }

    fun clearPairRequestObserver() {
        pairingRegistration?.remove()
        pairingRegistration = null
    }

    fun close() {
        handler.removeCallbacksAndMessages(null)
        deviceRegistration?.remove()
        deviceRegistration = null
        connectionRegistration?.remove()
        connectionRegistration = null
        clearPairRequestObserver()
        clearDeviceDetails()
        currentUid = null
        currentDeviceId = null
        currentObserver = null
    }

    private fun attachDeviceList() {
        val uid = currentUid ?: return
        val callback = currentDevicesCallback ?: return
        deviceRegistration?.remove()
        val ref = database.child(FirebasePaths.userDevices(uid))
        deviceRegistration = register(ref, true, currentErrorCallback ?: {}) { snapshot ->
            retryDelayMs = INITIAL_RETRY_MS
            callback(snapshot.children.mapNotNull { child ->
                val deviceId = child.child("deviceId").getValue(String::class.java)
                    ?: child.key
                    ?: return@mapNotNull null
                ParentDevice(
                    deviceId = deviceId,
                    label = child.child("label").getValue(String::class.java) ?: deviceId,
                    lastSeen = child.child("lastSeen").getValue(Long::class.java),
                    online = child.child("online").getValue(Boolean::class.java) ?: false,
                    enforcementMode = child.child("enforcementMode").getValue(String::class.java)
                        ?: PolicyConstants.ENFORCEMENT_UNPROTECTED,
                    protectionHealthy = child.child("protectionHealthy").getValue(Boolean::class.java) ?: false
                )
            })
        }
    }

    private fun attachDeviceDetails() {
        val deviceId = currentDeviceId ?: return
        val observer = currentObserver ?: return
        retentionCleaner.cleanup(deviceId)

        observe(FirebasePaths.deviceApps(deviceId), observer) { snapshot ->
            observer.onApps(snapshot.children.mapNotNull { child ->
                val packageName = child.packageName() ?: return@mapNotNull null
                if (packageName in PolicyConstants.deprecatedVirtualPolicyPackages) return@mapNotNull null
                packageName to ParentApp(
                    packageName = packageName,
                    label = child.child("label").getValue(String::class.java) ?: packageName,
                    blockable = child.child("blockable").getValue(Boolean::class.java) ?: false,
                    protectedReason = child.child("protectedReason").getValue(String::class.java)
                )
            }.toMap())
        }
        observe(FirebasePaths.devicePolicyApps(deviceId), observer) { snapshot ->
            observer.onPolicies(snapshot.children.mapNotNull { child ->
                val packageName = child.packageName() ?: return@mapNotNull null
                packageName to child.parentPolicy()
            }.toMap())
        }
        observe(FirebasePaths.devicePolicyModes(deviceId), observer) { snapshot ->
            observer.onModes(snapshot.children.mapNotNull { modeSnapshot ->
                val modeId = modeSnapshot.child("modeId").getValue(String::class.java)
                    ?: modeSnapshot.key
                    ?: return@mapNotNull null
                ParentMode(
                    modeId = modeId,
                    name = modeSnapshot.child("name").getValue(String::class.java) ?: "Mode",
                    appPolicies = modeSnapshot.child("apps").children.mapNotNull appRule@ { child ->
                        val packageName = child.packageName() ?: return@appRule null
                        packageName to child.parentPolicy()
                    }.toMap(),
                    createdAt = modeSnapshot.child("createdAt").getValue(Long::class.java),
                    updatedAt = modeSnapshot.child("updatedAt").getValue(Long::class.java)
                )
            }.sortedBy { it.name.lowercase() })
        }
        observe(FirebasePaths.devicePolicyActiveMode(deviceId), observer) { snapshot ->
            observer.onActiveMode(
                ActiveMode(
                    modeId = snapshot.child("modeId").getValue(String::class.java),
                    modeName = snapshot.child("modeName").getValue(String::class.java),
                    activatedAt = snapshot.child("activatedAt").getValue(Long::class.java)
                )
            )
        }
        observe(FirebasePaths.deviceSecuritySafeMode(deviceId), observer) { snapshot ->
            observer.onSafeMode(
                SafeModeState(
                    enabled = snapshot.child("enabled").getValue(Boolean::class.java) ?: false,
                    until = snapshot.child("until").getValue(Long::class.java),
                    startedAt = snapshot.child("startedAt").getValue(Long::class.java),
                    startedBy = snapshot.child("startedBy").getValue(String::class.java)
                )
            )
        }
        observe(FirebasePaths.deviceSecurityPin(deviceId), observer) { snapshot ->
            val salt = snapshot.child("salt").getValue(String::class.java)
            val hash = snapshot.child("hash").getValue(String::class.java)
            observer.onPin(
                if (!salt.isNullOrBlank() && !hash.isNullOrBlank()) {
                    ControlPin(
                        salt = salt,
                        hash = hash,
                        version = snapshot.child("version").getValue(Long::class.java)?.toInt()
                            ?: PinHasher.LEGACY_VERSION,
                        algorithm = snapshot.child("algorithm").getValue(String::class.java),
                        iterations = snapshot.child("iterations").getValue(Long::class.java)?.toInt(),
                        updatedAt = snapshot.child("updatedAt").getValue(Long::class.java)
                    )
                } else {
                    null
                }
            )
        }
        observe(FirebasePaths.deviceStateApps(deviceId), observer) { snapshot ->
            observer.onStates(snapshot.children.mapNotNull { child ->
                val packageName = child.packageName() ?: return@mapNotNull null
                packageName to child.parentState()
            }.toMap())
        }
        observe(ParentBrowserPaths.deviceStateBrowser(deviceId), observer) { snapshot ->
            val map = snapshot.value as? Map<Any?, Any?> ?: emptyMap()
            observer.onBrowser(BrowserState.fromMap(map))
        }
        observe(FirebasePaths.deviceSecurityRuntime(deviceId), observer) { snapshot ->
            observer.onSecurity(snapshot.securityRuntime())
        }
        observe(
            database.child(FirebasePaths.deviceUnlockRequests(deviceId))
                .orderByChild("createdAt")
                .limitToLast(30),
            observer,
            keepSynced = false
        ) { snapshot ->
            observer.onUnlockRequests(snapshot.children.mapNotNull { child ->
                UnlockRequest(
                    requestId = child.child("requestId").getValue(String::class.java)
                        ?: child.key
                        ?: return@mapNotNull null,
                    packageName = child.child("packageName").getValue(String::class.java) ?: "",
                    reason = child.child("reason").getValue(String::class.java) ?: "",
                    status = child.child("status").getValue(String::class.java) ?: "",
                    createdAt = child.child("createdAt").getValue(Long::class.java),
                    expiresAt = child.child("expiresAt").getValue(Long::class.java),
                    updatedAt = child.child("updatedAt").getValue(Long::class.java),
                    approvalType = child.child("approvalType").getValue(String::class.java),
                    approvalDurationMs = child.child("approvalDurationMs").getValue(Long::class.java),
                    tvApplyStatus = child.child("tvApplyStatus").getValue(String::class.java),
                    tvAppliedAt = child.child("tvAppliedAt").getValue(Long::class.java)
                )
            }.sortedByDescending { it.createdAt ?: 0L })
        }
        observe(
            database.child(FirebasePaths.deviceTamperEvents(deviceId))
                .orderByChild("createdAt")
                .limitToLast(50),
            observer,
            keepSynced = false
        ) { snapshot ->
            observer.onTamperEvents(snapshot.children.mapNotNull { child ->
                TamperEvent(
                    eventId = child.key ?: return@mapNotNull null,
                    type = child.child("type").getValue(String::class.java) ?: "",
                    message = child.child("message").getValue(String::class.java),
                    createdAt = child.child("createdAt").getValue(Long::class.java)
                )
            }.sortedByDescending { it.createdAt ?: 0L })
        }
        observe(
            database.child(FirebasePaths.deviceCommands(deviceId))
                .orderByChild("createdAt")
                .limitToLast(20),
            observer,
            keepSynced = false
        ) { snapshot ->
            observer.onCommands(snapshot.children.mapNotNull { child ->
                ParentCommand(
                    commandId = child.key ?: return@mapNotNull null,
                    type = child.child("type").getValue(String::class.java) ?: return@mapNotNull null,
                    packageName = child.child("packageName").getValue(String::class.java),
                    status = child.child("status").getValue(String::class.java)
                        ?: PolicyConstants.COMMAND_PENDING,
                    createdAt = child.child("createdAt").getValue(Long::class.java),
                    startedAt = child.child("startedAt").getValue(Long::class.java),
                    completedAt = child.child("completedAt").getValue(Long::class.java),
                    error = child.child("error").getValue(String::class.java)
                )
            }.sortedByDescending { it.createdAt ?: 0L })
        }
        observe(FirebasePaths.deviceSyncDesired(deviceId), observer) { snapshot ->
            observer.onDesiredRevision(snapshot)
        }
        observe(FirebasePaths.deviceSyncApplied(deviceId), observer) { snapshot ->
            observer.onAppliedRevision(
                SyncAppliedRevision(
                    revisionId = snapshot.child("revisionId").getValue(String::class.java),
                    status = snapshot.child("status").getValue(String::class.java),
                    appliedAt = snapshot.child("appliedAt").getValue(Long::class.java),
                    sessionId = snapshot.child("sessionId").getValue(String::class.java),
                    error = snapshot.child("error").getValue(String::class.java)
                )
            )
        }
        observe(FirebasePaths.deviceSyncRuntime(deviceId), observer) { snapshot ->
            observer.onSyncRuntime(snapshot.syncRuntime())
        }
        observe(FirebasePaths.deviceActivityCurrent(deviceId), observer) { snapshot ->
            observer.onActivityCurrent(if (snapshot.exists()) snapshot.deviceActivity() else null)
        }
        observe(
            database.child(FirebasePaths.deviceActivityHistory(deviceId))
                .orderByChild("startedAt")
                .limitToLast(500),
            observer,
            keepSynced = false
        ) { snapshot ->
            observer.onActivityHistory(snapshot.children.mapNotNull { child ->
                val id = child.child("id").getValue(String::class.java) ?: child.key
                    ?: return@mapNotNull null
                ActivityHistoryEntry(
                    id = id,
                    type = child.child("type").getValue(String::class.java) ?: "app",
                    appKey = child.child("appKey").getValue(String::class.java)
                        ?: child.child("packageName").getValue(String::class.java).orEmpty(),
                    appLabel = child.child("appLabel").getValue(String::class.java) ?: "",
                    title = child.child("title").getValue(String::class.java),
                    subtitle = child.child("subtitle").getValue(String::class.java),
                    startedAt = child.child("startedAt").getValue(Long::class.java) ?: 0L,
                    endedAt = child.child("endedAt").getValue(Long::class.java) ?: 0L,
                    lastPositionMs = child.child("lastPositionMs").getValue(Long::class.java),
                    durationMs = child.child("durationMs").getValue(Long::class.java),
                    playbackState = child.child("playbackState").getValue(String::class.java),
                    confidence = child.child("confidence").getValue(String::class.java),
                    captureSource = child.child("captureSource").getValue(String::class.java) ?: "agent",
                    url = child.child("url").getValue(String::class.java)
                )
            }.sortedByDescending { it.startedAt })
        }
        observe(FirebasePaths.deviceControlV2(deviceId), observer) { snapshot ->
            if (!snapshot.exists()) {
                observer.onControlV2(ControlAvailability.MISSING, null)
            } else {
                ControlProtocol.parse(snapshot)
                    .onSuccess { observer.onControlV2(ControlAvailability.VALID, it) }
                    .onFailure { error ->
                        observer.onControlV2(
                            ControlAvailability.INVALID,
                            null,
                            error.message ?: "Invalid synchronized TV control"
                        )
                    }
            }
        }
    }

    private fun observe(
        path: String,
        observer: DeviceObserver,
        keepSynced: Boolean = true,
        onData: (DataSnapshot) -> Unit
    ) {
        val ref = database.child(path)
        observe(ref, observer, keepSynced, onData)
    }

    private fun observe(
        query: Query,
        observer: DeviceObserver,
        keepSynced: Boolean = true,
        onData: (DataSnapshot) -> Unit
    ) {
        detailRegistrations += register(query, keepSynced, observer::onError) { snapshot ->
            retryDelayMs = INITIAL_RETRY_MS
            onData(snapshot)
        }
    }

    private fun register(
        query: Query,
        keepSynced: Boolean,
        onError: (String) -> Unit,
        onData: (DataSnapshot) -> Unit
    ): Registration {
        if (keepSynced) {
            query.keepSynced(true)
            keptSynced += query
        }
        // The laptop can push many writes to the same node in a short burst
        // (e.g. one PutAsync per app to state/apps, forced every ~15s). Each
        // write delivers the FULL subtree to onDataChange, so re-parsing on
        // every event is O(N^2) on the main thread and freezes the UI. Debounce
        // per-query so only the latest snapshot is parsed per quiet window; the
        // final state is identical (latest wins) but the work collapses to ~1.
        @Suppress("REDUNDANT_INITIALIZER")
        var pending: Runnable? = null
        val listener = object : ValueEventListener {
            override fun onDataChange(snapshot: DataSnapshot) {
                // Debounce: keep only the most recent snapshot. Cancel any
                // not-yet-run callback and reschedule against the latest data so
                // a burst of writes (e.g. one PutAsync per app) collapses to a
                // single re-parse instead of O(N^2) on the main thread.
                pending?.let { handler.removeCallbacks(it) }
                pending = Runnable { onData(snapshot) }
                handler.postDelayed(pending!!, SYNC_DEBOUNCE_MS)
            }

            override fun onCancelled(error: DatabaseError) {
                pending?.let { handler.removeCallbacks(it) }
                pending = null
                onError(error.message)
                scheduleRetry()
            }
        }
        query.addValueEventListener(listener)
        return Registration(query, listener) {
            pending?.let { handler.removeCallbacks(it) }
        }
    }

    private fun scheduleRetry() {
        handler.removeCallbacks(retryRunnable)
        handler.postDelayed(retryRunnable, retryDelayMs)
        retryDelayMs = (retryDelayMs * 2).coerceAtMost(MAX_RETRY_MS)
    }

    private fun reattach() {
        attachDeviceList()
        currentDeviceId?.let {
            val observer = currentObserver ?: return@let
            clearDeviceDetails(clearSelection = false)
            currentObserver = observer
            currentDeviceId = it
            attachDeviceDetails()
        }
    }

    private fun clearDeviceDetails(clearSelection: Boolean = true) {
        detailRegistrations.forEach { it.remove() }
        detailRegistrations.clear()
        keptSynced.forEach { it.keepSynced(false) }
        keptSynced.clear()
        if (clearSelection) {
            currentDeviceId = null
            currentObserver = null
        }
    }

    private fun DataSnapshot.packageName(): String? {
        return normalizedPackageName(
            encodedKey = key,
            storedPackageName = child("packageName").getValue(String::class.java)
        )
    }

    private fun DataSnapshot.deviceActivity(): DeviceActivity = DeviceActivity(
        runtimeApp = child("runtimeApp").getValue(String::class.java).orEmpty(),
        appKey = child("appKey").getValue(String::class.java)
            ?: child("packageName").getValue(String::class.java).orEmpty(),
        appLabel = child("appLabel").getValue(String::class.java).orEmpty(),
        appStartedAt = child("appStartedAt").getValue(Long::class.java) ?: 0L,
        overlayState = child("overlayState").getValue(String::class.java) ?: "none",
        mediaAvailable = child("mediaAvailable").getValue(Boolean::class.java) ?: false,
        mediaTitle = child("mediaTitle").getValue(String::class.java),
        mediaSubtitle = child("mediaSubtitle").getValue(String::class.java),
        playbackState = child("playbackState").getValue(String::class.java) ?: "unknown",
        positionMs = child("positionMs").getValue(Long::class.java),
        durationMs = child("durationMs").getValue(Long::class.java),
        positionCapturedAt = child("positionCapturedAt").getValue(Long::class.java),
        playbackSpeed = child("playbackSpeed").getValue(Float::class.java) ?: 0f,
        mediaStartedAt = child("mediaStartedAt").getValue(Long::class.java),
        mediaConfidence = child("mediaConfidence").getValue(String::class.java),
        captureSource = child("captureSource").getValue(String::class.java) ?: "agent",
        updatedAt = child("updatedAt").getValue(Long::class.java) ?: 0L
    )

    private fun DataSnapshot.parentPolicy(): ParentPolicy {
        return ParentPolicy(
            manualBlocked = child("manualBlocked").getValue(Boolean::class.java) ?: false,
            dailyLimitMinutes = child("dailyLimitMinutes").getValue(Long::class.java)
                ?.toInt()
                ?.takeIf { it in 1..1440 }
        )
    }

    private fun DataSnapshot.parentState(): ParentState = ParentState(
        suspended = child("suspended").getValue(Boolean::class.java) ?: false,
        requestedSuspended = child("requestedSuspended").getValue(Boolean::class.java) ?: false,
        manualBlocked = child("manualBlocked").getValue(Boolean::class.java) ?: false,
        dailyLimitBlocked = child("dailyLimitBlocked").getValue(Boolean::class.java) ?: false,
        networkBlocked = child("networkBlocked").getValue(Boolean::class.java) ?: false,
        vpnApplied = child("vpnApplied").getValue(Boolean::class.java) ?: false,
        vpnActive = child("vpnActive").getValue(Boolean::class.java) ?: false,
        lockBlocked = child("lockBlocked").getValue(Boolean::class.java) ?: false,
        lockReason = child("lockReason").getValue(String::class.java),
        vpnLastError = child("vpnLastError").getValue(String::class.java),
        fallbackLocked = child("fallbackLocked").getValue(Boolean::class.java) ?: false,
        enforcementMode = child("enforcementMode").getValue(String::class.java)
            ?: PolicyConstants.ENFORCEMENT_UNPROTECTED,
        blockReason = child("blockReason").getValue(String::class.java),
        usageMinutesToday = child("usageMinutesToday").getValue(Long::class.java) ?: 0L,
        usageMsToday = child("usageMsToday").getValue(Long::class.java)
            ?: (child("usageMinutesToday").getValue(Long::class.java) ?: 0L) * 60_000L,
        usageCapturedAt = child("usageCapturedAt").getValue(Long::class.java),
        foregroundActive = child("foregroundActive").getValue(Boolean::class.java) ?: false,
        foregroundStartedAt = child("foregroundStartedAt").getValue(Long::class.java),
        controlRevisionId = child("controlRevisionId").getValue(String::class.java),
        updatedAt = child("updatedAt").getValue(Long::class.java),
        lastError = child("lastError").getValue(String::class.java)
    )

    private fun DataSnapshot.securityRuntime(): SecurityRuntime = SecurityRuntime(
        enforcementMode = child("enforcementMode").getValue(String::class.java)
            ?: PolicyConstants.ENFORCEMENT_UNPROTECTED,
        deviceOwner = child("deviceOwner").getValue(Boolean::class.java) ?: false,
        deviceAdmin = child("deviceAdmin").getValue(Boolean::class.java) ?: false,
        deviceAdminSetupAvailable = child("deviceAdminSetupAvailable").getValue(Boolean::class.java) ?: true,
        accessibility = child("accessibility").getValue(Boolean::class.java) ?: false,
        usageAccess = child("usageAccess").getValue(Boolean::class.java) ?: false,
        vpnPrepared = child("vpnPrepared").getValue(Boolean::class.java) ?: false,
        vpnActive = child("vpnActive").getValue(Boolean::class.java) ?: false,
        vpnBlockedCount = child("vpnBlockedCount").getValue(Long::class.java)?.toInt() ?: 0,
        vpnLastError = child("vpnLastError").getValue(String::class.java),
        backgroundUnrestricted = child("backgroundUnrestricted").getValue(Boolean::class.java) ?: false,
        pinConfigured = child("pinConfigured").getValue(Boolean::class.java) ?: false,
        pinHashVersion = child("pinHashVersion").getValue(Long::class.java)?.toInt() ?: 0,
        protectionHealthy = child("protectionHealthy").getValue(Boolean::class.java) ?: false,
        lastForegroundPackage = child("lastForegroundPackage").getValue(String::class.java),
        lastSyncError = child("lastSyncError").getValue(String::class.java),
        safeModeActive = child("safeModeActive").getValue(Boolean::class.java) ?: false,
        safeModeUntil = child("safeModeUntil").getValue(Long::class.java),
        activeModeId = child("activeModeId").getValue(String::class.java),
        activeModeName = child("activeModeName").getValue(String::class.java),
        updatedAt = child("updatedAt").getValue(Long::class.java)
    )

    private fun DataSnapshot.syncRuntime(): SyncRuntimeState = SyncRuntimeState(
        connected = child("connected").getValue(Boolean::class.java) ?: false,
        sessionId = child("sessionId").getValue(String::class.java),
        protocolVersion = child("protocolVersion").getValue(Long::class.java)?.toInt() ?: 0,
        connectedAt = child("connectedAt").getValue(Long::class.java),
        lastPolicyReceivedAt = child("lastPolicyReceivedAt").getValue(Long::class.java),
        lastPolicyAppliedAt = child("lastPolicyAppliedAt").getValue(Long::class.java),
        lastStateWriteAt = child("lastStateWriteAt").getValue(Long::class.java),
        lastUsageWriteAt = child("lastUsageWriteAt").getValue(Long::class.java),
        lastHeartbeatWriteAt = child("lastHeartbeatWriteAt").getValue(Long::class.java),
        lastInventoryWriteAt = child("lastInventoryWriteAt").getValue(Long::class.java),
        lastHealthWriteAt = child("lastHealthWriteAt").getValue(Long::class.java),
        lastCommandWriteAt = child("lastCommandWriteAt").getValue(Long::class.java),
        lastUnlockWriteAt = child("lastUnlockWriteAt").getValue(Long::class.java),
        lastTamperWriteAt = child("lastTamperWriteAt").getValue(Long::class.java),
        lastSuccessAt = child("lastSuccessAt").getValue(Long::class.java),
        lastFailedChannel = child("lastFailedChannel").getValue(String::class.java),
        lastError = child("lastError").getValue(String::class.java),
        lastErrorAt = child("lastErrorAt").getValue(Long::class.java),
        inventoryRevision = child("inventoryRevision").getValue(String::class.java)
    )

    companion object {
        private const val INITIAL_RETRY_MS = 5_000L
        private const val MAX_RETRY_MS = 5 * 60_000L
        private const val SYNC_DEBOUNCE_MS = 50L
    }
}
internal fun normalizedPackageName(encodedKey: String?, storedPackageName: String?): String? {
    val key = encodedKey?.takeIf { it.isNotBlank() }
    val decoded = key?.let { runCatching { PackageKeys.decode(it) }.getOrNull() }
    val stored = storedPackageName?.takeIf { it.isNotBlank() }
    if (stored != null && key != null &&
        runCatching { PackageKeys.encode(stored) }.getOrNull() == key
    ) {
        return stored
    }
    return decoded ?: stored
}
