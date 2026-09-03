package com.guardpulse.parentcontrol.parent

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import com.google.firebase.auth.FirebaseAuth
import com.google.firebase.database.FirebaseDatabase
import com.guardpulse.parentcontrol.shared.ControlPin
import com.guardpulse.parentcontrol.shared.ControlProtocol
import com.guardpulse.parentcontrol.shared.ControlSnapshotV2
import com.guardpulse.parentcontrol.shared.FirebaseRuntime
import com.guardpulse.parentcontrol.shared.FirebaseServerClock
import com.guardpulse.parentcontrol.shared.PolicyConstants
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

private sealed interface ControlOperation {
    data class UpdatePolicy(val packageName: String, val policy: ParentPolicy) : ControlOperation
    data class SetPin(val pin: String) : ControlOperation
    data class CreateMode(val name: String) : ControlOperation
    data class RenameMode(val modeId: String, val name: String) : ControlOperation
    data class DeleteMode(val modeId: String, val activeModeId: String?) : ControlOperation
    data class UpdateModePolicy(
        val modeId: String,
        val packageName: String,
        val policy: ParentPolicy
    ) : ControlOperation
    data class SetActiveMode(val mode: ParentMode?) : ControlOperation
    data class StartSafeMode(val durationMinutes: Int) : ControlOperation
    data object StopSafeMode : ControlOperation
    data class UpdateBudget(val dailyLimitMinutes: Int?) : ControlOperation
    data class UpdateAllowlist(val enabled: Boolean) : ControlOperation
    data class UpdateCustomBlockedDomains(val domains: List<String>) : ControlOperation
}

class ParentSyncViewModel(application: Application) : AndroidViewModel(application) {
    private val auth = FirebaseAuth.getInstance()
    private val serverClock = FirebaseServerClock()
    private val selectionPrefs = application.getSharedPreferences("parent_sync", 0)
    private val firebaseStatus = FirebaseRuntime.initialize(application)
    private val database = firebaseStatus.takeIf { it.configured }
        ?.let { FirebaseDatabase.getInstance().reference }
    private val writer = database?.let { ParentRepository(it, auth, serverClock) }
    private val syncRepository = database?.let(::ParentSyncRepository)
    private val _state = MutableStateFlow(
        ParentSyncUiState(
            configured = firebaseStatus.configured,
            firebaseMessage = firebaseStatus.message,
            signedIn = auth.currentUser != null,
            message = firebaseStatus.message
        )
    )
    val state: StateFlow<ParentSyncUiState> = _state.asStateFlow()

    private var legacyPoliciesLoaded = false
    private var legacyModesLoaded = false
    private var legacyActiveModeLoaded = false
    private var legacySafeModeLoaded = false
    private var legacyPinLoaded = false
    private var controlExistenceLoaded = false
    private var legacyPin: ControlPin? = null
    private var legacyPolicies: Map<String, ParentPolicy> = emptyMap()
    private var legacyModes: List<ParentMode> = emptyList()
    private var legacyActiveMode: ActiveMode = ActiveMode()
    private var legacySafeMode: SafeModeState = SafeModeState()
    private var latestRuntimeStates: Map<String, ParentState> = emptyMap()
    private var migrationRequested = false
    private var pendingPairDeviceId: String? = selectionPrefs.getString("pendingPairDeviceId", null)
    private var pendingPairRequestId: String? = selectionPrefs.getString("pendingPairRequestId", null)
    private val pendingControlOperations = ArrayDeque<ControlOperation>()

    private val authListener = FirebaseAuth.AuthStateListener { firebaseAuth ->
        val signedIn = firebaseAuth.currentUser != null
        setState { it.copy(signedIn = signedIn, authBusy = false) }
        if (signedIn) {
            attachConnectionObserver()
            attachDeviceList()
            resumePairRequestObserver()
        } else {
            clearSignedOutState()
        }
    }

    init {
        serverClock.start()
        auth.addAuthStateListener(authListener)
        attachConnectionObserver()
        if (auth.currentUser != null) {
            attachDeviceList()
            resumePairRequestObserver()
        }
    }

    fun signIn(email: String, password: String) {
        if (!validateAuthInput(email, password)) return
        setState { it.copy(authBusy = true) }
        auth.signInWithEmailAndPassword(email.trim(), password)
            .addOnSuccessListener { setMessage("Signed in") }
            .addOnFailureListener { setMessage(it.message) }
            .addOnCompleteListener { setState { current -> current.copy(authBusy = false) } }
    }

    fun createAccount(email: String, password: String) {
        if (!validateAuthInput(email, password)) return
        setState { it.copy(authBusy = true) }
        auth.createUserWithEmailAndPassword(email.trim(), password)
            .addOnSuccessListener { setMessage("Account created") }
            .addOnFailureListener { setMessage(it.message) }
            .addOnCompleteListener { setState { current -> current.copy(authBusy = false) } }
    }

    fun signOut() {
        syncRepository?.close()
        auth.signOut()
    }

    fun selectDevice(deviceId: String) {
        selectionPrefs.edit().putString("selectedDeviceId", deviceId).apply()
        resetDeviceLoading(deviceId)
        attachDevice(deviceId)
    }

    fun createPairRequest(payload: String, manualDeviceId: String, manualCode: String) {
        val parsed = parsePairingPayload(payload)
        val deviceId = parsed.deviceId ?: manualDeviceId
        val secret = parsed.secret
        if (deviceId.isBlank()) {
            setMessage("Enter a TV device ID or paste the QR payload.")
            return
        }
        if (secret.isNullOrBlank() && manualCode.isBlank()) {
            setMessage("Enter the 6-digit code or paste the QR payload.")
            return
        }
        writer?.createPairRequest(
            deviceId,
            secret,
            manualCode,
            onSuccess = { requestId ->
                pendingPairDeviceId = deviceId
                pendingPairRequestId = requestId
                selectionPrefs.edit()
                    .putString("pendingPairDeviceId", deviceId)
                    .putString("pendingPairRequestId", requestId)
                    .apply()
                observePairRequest(deviceId, requestId)
                setMessage("Pair request sent; waiting for TV")
            },
            onError = ::setMessage
        )
    }

    fun updatePolicy(packageName: String, policy: ParentPolicy) {
        val current = state.value
        current.selectedDeviceId ?: return setMessage("Select a TV first")
        val app = current.apps[packageName]
        policyValidationMessage(app, policy)?.let { return setMessage(it) }
        submitControlOperation(ControlOperation.UpdatePolicy(packageName, policy))
    }

    fun setPin(pin: String) {
        state.value.selectedDeviceId ?: return setMessage("Select a TV before setting a PIN")
        pinValidationMessage(pin)?.let { return setMessage(it) }
        submitControlOperation(ControlOperation.SetPin(pin))
    }

    fun updateUnlock(
        request: UnlockRequest,
        status: String,
        approvalType: String? = null,
        approvalDurationMs: Long? = null
    ) {
        val deviceId = state.value.selectedDeviceId ?: return setMessage("Select a TV first")
        if (status == PolicyConstants.UNLOCK_APPROVED &&
            request.expiresAt != null &&
            serverClock.now() > request.expiresAt
        ) {
            writer?.updateUnlock(
                deviceId,
                request,
                PolicyConstants.UNLOCK_EXPIRED,
                onSuccess = { setMessage("Unlock request expired") },
                onError = ::setMessage
            )
            return
        }
        writer?.updateUnlock(
            deviceId,
            request,
            status,
            approvalType,
            approvalDurationMs,
            onSuccess = {
                setMessage(
                    when {
                        status == PolicyConstants.UNLOCK_APPROVED && approvalType == PolicyConstants.UNLOCK_APPROVAL_TIMED ->
                            "Unlock sent for ${(approvalDurationMs ?: 0L) / 60_000L} minutes; waiting for TV"
                        status == PolicyConstants.UNLOCK_APPROVED -> "One-visit unlock sent; waiting for TV"
                        else -> "Unlock denied"
                    }
                )
            },
            onError = ::setMessage
        )
    }

    fun createMode(name: String) {
        state.value.selectedDeviceId ?: return setMessage("Select a TV first")
        val trimmed = name.trim()
        modeNameValidationMessage(trimmed)?.let { return setMessage(it) }
        submitControlOperation(ControlOperation.CreateMode(trimmed))
    }

    fun renameMode(modeId: String, name: String) {
        state.value.selectedDeviceId ?: return
        val trimmed = name.trim()
        modeNameValidationMessage(trimmed)?.let { return setMessage(it) }
        submitControlOperation(ControlOperation.RenameMode(modeId, trimmed))
    }

    fun deleteMode(modeId: String) {
        val current = state.value
        current.selectedDeviceId ?: return
        submitControlOperation(ControlOperation.DeleteMode(modeId, current.activeMode.modeId))
    }

    fun updateModePolicy(modeId: String, packageName: String, policy: ParentPolicy) {
        val current = state.value
        current.selectedDeviceId ?: return setMessage("Select a TV first")
        policyValidationMessage(current.apps[packageName], policy)?.let { return setMessage(it) }
        submitControlOperation(ControlOperation.UpdateModePolicy(modeId, packageName, policy))
    }

    fun setActiveMode(mode: ParentMode?) {
        state.value.selectedDeviceId ?: return setMessage("Select a TV first")
        submitControlOperation(ControlOperation.SetActiveMode(mode))
    }

    fun startSafeMode(durationMinutes: Int) {
        state.value.selectedDeviceId ?: return setMessage("Select a TV first")
        safeModeValidationMessage(durationMinutes)?.let { return setMessage(it) }
        submitControlOperation(ControlOperation.StartSafeMode(durationMinutes))
    }

    fun stopSafeMode() {
        state.value.selectedDeviceId ?: return setMessage("Select a TV first")
        submitControlOperation(ControlOperation.StopSafeMode)
    }

    fun updateBudget(dailyLimitMinutes: Int?) {
        state.value.selectedDeviceId ?: return setMessage("Select a TV first")
        if (dailyLimitMinutes != null && dailyLimitMinutes !in 1..1440) {
            return setMessage("Daily budget must be 1..1440 minutes")
        }
        submitControlOperation(ControlOperation.UpdateBudget(dailyLimitMinutes))
    }

    fun updateAllowlist(enabled: Boolean) {
        state.value.selectedDeviceId ?: return setMessage("Select a TV first")
        submitControlOperation(ControlOperation.UpdateAllowlist(enabled))
    }

    fun updateCustomBlockedDomains(domains: List<String>) {
        state.value.selectedDeviceId ?: return setMessage("Select a TV first")
        if (domains.size > 100) return setMessage("Too many domains (max 100)")
        submitControlOperation(ControlOperation.UpdateCustomBlockedDomains(domains))
    }

    fun sendCommand(type: String, packageName: String? = null) {
        val deviceId = state.value.selectedDeviceId ?: return setMessage("Select a TV first")
        writer?.sendCommand(
            deviceId,
            type,
            packageName,
            onSuccess = { setMessage("Command sent; waiting for TV") },
            onError = ::setMessage
        )
    }

    // Devices the parent asked to remove whose laptop has not confirmed yet
    // (meta.ownerUid still set). Kept in prefs so the state survives restarts.
    private val unpairPendingPrefs by lazy {
        application.getSharedPreferences("unpair_pending", android.content.Context.MODE_PRIVATE)
    }

    private fun unpairPendingIds(): Set<String> =
        unpairPendingPrefs.getStringSet("ids", emptySet())?.toSet().orEmpty()

    private fun markUnpairPending(deviceId: String) {
        unpairPendingPrefs.edit().putStringSet(
            "ids",
            unpairPendingIds() + deviceId
        ).apply()
    }

    private fun clearUnpairPending(deviceId: String) {
        unpairPendingPrefs.edit().putStringSet(
            "ids",
            unpairPendingIds() - deviceId
        ).apply()
    }

    fun removePairedDevice(deviceId: String) {
        val wasSelected = state.value.selectedDeviceId == deviceId
        markUnpairPending(deviceId)
        // Keep the device visible but flag it: the unpair only completes when the
        // laptop confirms (meta.ownerUid cleared) - otherwise re-pairing is blocked
        // by the pair-request rule and the kid keeps the old protections.
        setState {
            it.copy(
                devices = it.devices.map { d ->
                    if (d.deviceId == deviceId) d.copy(label = d.label + " (waiting for laptop)") else d
                }
            )
        }
        if (wasSelected) {
            selectionPrefs.edit().remove("selectedDeviceId").apply()
            syncRepository?.clearSelectedDevice()
        }
        writer?.removePairedDevice(
            deviceId,
            onSuccess = { setMessage("Remove requested; completes when the laptop confirms") },
            onError = { msg ->
                syncRepository?.refresh()
                setMessage(msg)
            }
        )
    }

    fun reconnect() {
        setMessage("Reconnecting...")
        auth.currentUser?.getIdToken(true)
            ?.addOnCompleteListener {
                syncRepository?.refresh()
                setMessage(if (it.isSuccessful) "Reconnected" else it.exception?.message)
            }
            ?: syncRepository?.refresh()
    }

    fun repairControlV2() {
        val current = state.value
        if (current.controlAvailability != ControlAvailability.INVALID) {
            return setMessage("Synchronized control does not require repair")
        }
        if (!legacyPoliciesLoaded || !legacyModesLoaded || !legacyActiveModeLoaded ||
            !legacySafeModeLoaded || !legacyPinLoaded
        ) {
            return setMessage("Legacy TV controls are still loading; reconnect and try again")
        }
        val deviceId = current.selectedDeviceId ?: return setMessage("Select a TV first")
        writer?.seedControlV2(
            deviceId,
            legacyPolicies,
            legacyModes,
            legacyActiveMode,
            legacySafeMode,
            legacyPin,
            onSuccess = { setMessage("Repair sent; waiting for TV validation") },
            onError = ::setMessage
        )
    }

    private fun attachDeviceList() {
        val uid = auth.currentUser?.uid ?: return
        setState { it.copy(loadingDevices = true) }
        syncRepository?.observeDevices(
            uid,
            onDevices = { devices ->
                // A device the parent removed disappears from users/{uid}/devices
                // immediately; while meta.ownerUid is still set (laptop has not
                // confirmed), re-attach the pending marker so the UI stays truthful.
                val pending = unpairPendingIds()
                val reconciled = devices.map { d ->
                    if (d.deviceId in pending) d.copy(label = d.label + " (waiting for laptop)") else d
                }
                // A pending id that vanished from the mirror entirely AND whose device
                // is gone means the laptop confirmed (its UnpairAsync deletes the mirror
                // entry too) - clear the marker.
                pending.forEach { id ->
                    if (devices.none { it.deviceId == id }) {
                        clearUnpairPending(id)
                    }
                }
                setState { current -> current.copy(devices = reconciled, loadingDevices = false) }
                val selected = preferredDeviceId(
                    devices = devices,
                    currentId = state.value.selectedDeviceId,
                    persistedId = selectionPrefs.getString("selectedDeviceId", null)
                )
                if (selected != null && selected != state.value.selectedDeviceId) {
                    selectDevice(selected)
                } else if (selected == null && state.value.selectedDeviceId != null) {
                    clearSelectedDevice()
                }
            },
            onError = ::setMessage
        )
    }

    private fun attachConnectionObserver() {
        syncRepository?.observeConnection { connected ->
            setState { it.copy(phoneConnected = connected) }
            if (connected && auth.currentUser != null) syncRepository.refresh()
        }
    }

    private fun attachDevice(deviceId: String) {
        syncRepository?.observeDevice(deviceId, object : ParentSyncRepository.DeviceObserver {
            override fun onApps(value: Map<String, ParentApp>) {
                setState { it.copy(apps = value, loadingDeviceDetails = false) }
            }

            override fun onPolicies(value: Map<String, ParentPolicy>) {
                legacyPoliciesLoaded = true
                legacyPolicies = value
                setState { current ->
                    if (current.confirmedControl == null) current.copy(policies = value) else current
                }
                maybeSeedControlV2()
            }

            override fun onModes(value: List<ParentMode>) {
                legacyModesLoaded = true
                legacyModes = value
                setState { current ->
                    if (current.confirmedControl == null) current.copy(modes = value) else current
                }
                maybeSeedControlV2()
            }

            override fun onActiveMode(value: ActiveMode) {
                legacyActiveModeLoaded = true
                legacyActiveMode = value
                setState { current ->
                    if (current.confirmedControl == null) current.copy(activeMode = value) else current
                }
                maybeSeedControlV2()
            }

            override fun onSafeMode(value: SafeModeState) {
                legacySafeModeLoaded = true
                legacySafeMode = value
                setState { current ->
                    if (current.confirmedControl == null) current.copy(safeMode = value) else current
                }
                maybeSeedControlV2()
            }

            override fun onPin(value: ControlPin?) {
                legacyPinLoaded = true
                legacyPin = value
                maybeSeedControlV2()
            }

            override fun onStates(value: Map<String, ParentState>) {
                latestRuntimeStates = value
                setState { it.copy(states = value) }
                promoteConfirmedRuntimeStates()
            }
            override fun onSecurity(value: SecurityRuntime) = setState { it.copy(security = value) }
            override fun onUnlockRequests(value: List<UnlockRequest>) = setState { it.copy(unlockRequests = value) }
            override fun onTamperEvents(value: List<TamperEvent>) = setState { it.copy(tamperEvents = value) }
            override fun onCommands(value: List<ParentCommand>) = setState { it.copy(commands = value) }
            override fun onBrowser(value: BrowserState?) = setState { it.copy(browser = value) }
            override fun onActivityCurrent(value: DeviceActivity?) = setState { it.copy(activityCurrent = value) }
            override fun onActivityHistory(value: List<ActivityHistoryEntry>) = setState { it.copy(activityHistory = value) }

            override fun onDesiredRevision(snapshot: com.google.firebase.database.DataSnapshot) {
                setState { it.copy(desiredRevision = ControlProtocol.parseDesired(snapshot)) }
            }

            override fun onAppliedRevision(value: com.guardpulse.parentcontrol.shared.SyncAppliedRevision) {
                setState { it.copy(appliedRevision = value) }
                promoteConfirmedControl()
            }

            override fun onSyncRuntime(value: com.guardpulse.parentcontrol.shared.SyncRuntimeState) {
                setState { it.copy(syncRuntime = value, serverNow = serverClock.now()) }
            }

            override fun onControlV2(
                availability: ControlAvailability,
                value: ControlSnapshotV2?,
                error: String?
            ) {
                controlExistenceLoaded = true
                setState {
                    it.copy(
                        controlV2Exists = availability != ControlAvailability.MISSING,
                        controlAvailability = availability,
                        controlError = error,
                        desiredControl = value
                    )
                }
                when (availability) {
                    ControlAvailability.VALID -> {
                        migrationRequested = true
                        flushPendingControlOperations()
                        promoteConfirmedControl()
                    }
                    ControlAvailability.MISSING -> maybeSeedControlV2()
                    ControlAvailability.INVALID -> setMessage(
                        "TV control is invalid. Mutations are disabled until it is repaired."
                    )
                    ControlAvailability.UNKNOWN -> Unit
                }
            }

            override fun onError(message: String) = setMessage(message)
        })
    }

    private fun maybeSeedControlV2() {
        val current = state.value
        if (migrationRequested || current.controlV2Exists || !controlExistenceLoaded) return
        if (!legacyPoliciesLoaded || !legacyModesLoaded || !legacyActiveModeLoaded ||
            !legacySafeModeLoaded || !legacyPinLoaded
        ) return
        val deviceId = current.selectedDeviceId ?: return
        migrationRequested = true
        writer?.seedControlV2(
            deviceId,
            legacyPolicies,
            legacyModes,
            legacyActiveMode,
            legacySafeMode,
            legacyPin,
            onSuccess = { setMessage("TV controls upgraded; waiting for TV acknowledgement") },
            onError = {
                migrationRequested = false
                setMessage(it)
            }
        )
    }

    private fun resetDeviceLoading(deviceId: String) {
        legacyPoliciesLoaded = false
        legacyModesLoaded = false
        legacyActiveModeLoaded = false
        legacySafeModeLoaded = false
        legacyPinLoaded = false
        controlExistenceLoaded = false
        legacyPin = null
        legacyPolicies = emptyMap()
        legacyModes = emptyList()
        legacyActiveMode = ActiveMode()
        legacySafeMode = SafeModeState()
        latestRuntimeStates = emptyMap()
        migrationRequested = false
        pendingControlOperations.clear()
        setState {
            it.copy(
                selectedDeviceId = deviceId,
                apps = emptyMap(),
                policies = emptyMap(),
                states = emptyMap(),
                confirmedStates = emptyMap(),
                browser = null,
                modes = emptyList(),
                activeMode = ActiveMode(),
                safeMode = SafeModeState(),
                security = SecurityRuntime(),
                unlockRequests = emptyList(),
                tamperEvents = emptyList(),
                commands = emptyList(),
                activityCurrent = null,
                activityHistory = emptyList(),
                desiredRevision = null,
                appliedRevision = com.guardpulse.parentcontrol.shared.SyncAppliedRevision(),
                desiredControl = null,
                confirmedControl = null,
                syncRuntime = com.guardpulse.parentcontrol.shared.SyncRuntimeState(),
                controlV2Exists = false,
                controlAvailability = ControlAvailability.UNKNOWN,
                controlError = null,
                loadingDeviceDetails = true
            )
        }
    }

    private fun clearSelectedDevice() {
        selectionPrefs.edit().remove("selectedDeviceId").apply()
        pendingControlOperations.clear()
        syncRepository?.clearSelectedDevice()
        setState {
            it.copy(
                selectedDeviceId = null,
                apps = emptyMap(),
                policies = emptyMap(),
                states = emptyMap(),
                confirmedStates = emptyMap(),
                browser = null,
                modes = emptyList(),
                activeMode = ActiveMode(),
                safeMode = SafeModeState(),
                security = SecurityRuntime(),
                unlockRequests = emptyList(),
                tamperEvents = emptyList(),
                commands = emptyList(),
                activityCurrent = null,
                activityHistory = emptyList(),
                desiredRevision = null,
                appliedRevision = com.guardpulse.parentcontrol.shared.SyncAppliedRevision(),
                desiredControl = null,
                confirmedControl = null,
                syncRuntime = com.guardpulse.parentcontrol.shared.SyncRuntimeState(),
                controlV2Exists = false,
                controlAvailability = ControlAvailability.UNKNOWN,
                controlError = null,
                loadingDeviceDetails = false
            )
        }
    }

    private fun clearSignedOutState() {
        selectionPrefs.edit()
            .remove("selectedDeviceId")
            .remove("pendingPairDeviceId")
            .remove("pendingPairRequestId")
            .apply()
        pendingPairDeviceId = null
        pendingPairRequestId = null
        setState {
            ParentSyncUiState(
                configured = firebaseStatus.configured,
                firebaseMessage = firebaseStatus.message,
                phoneConnected = it.phoneConnected,
                serverNow = serverClock.now(),
                message = it.message
            )
        }
    }

    private fun validateAuthInput(email: String, password: String): Boolean {
        authValidationMessage(email, password)?.let {
            setMessage(it)
            return false
        }
        return true
    }

    private fun resumePairRequestObserver() {
        val deviceId = pendingPairDeviceId ?: return
        val requestId = pendingPairRequestId ?: return
        observePairRequest(deviceId, requestId)
    }

    private fun observePairRequest(deviceId: String, requestId: String) {
        syncRepository?.observePairRequest(
            deviceId,
            requestId,
            onValue = { request ->
                setState { it.copy(pairRequest = request) }
                when (request?.status) {
                    PolicyConstants.PAIR_ACCEPTED -> {
                        clearPersistedPairRequest()
                        setMessage("TV pairing confirmed")
                        attachDeviceList()
                    }
                    PolicyConstants.PAIR_REJECTED -> {
                        clearPersistedPairRequest()
                        setMessage(request.error ?: "TV rejected the pairing request")
                    }
                    PolicyConstants.PAIR_EXPIRED -> {
                        clearPersistedPairRequest()
                        setMessage("Pairing request expired")
                    }
                    PolicyConstants.PAIR_FAILED -> {
                        clearPersistedPairRequest()
                        setMessage(request.error ?: "TV pairing failed")
                    }
                }
            },
            onError = ::setMessage
        )
    }

    private fun clearPersistedPairRequest() {
        pendingPairDeviceId = null
        pendingPairRequestId = null
        selectionPrefs.edit()
            .remove("pendingPairDeviceId")
            .remove("pendingPairRequestId")
            .apply()
        syncRepository?.clearPairRequestObserver()
    }

    private fun controlSent() = setMessage("Sent to TV; waiting for acknowledgement")

    private fun promoteConfirmedControl() {
        val current = state.value
        val desired = current.desiredControl ?: return
        val applied = current.appliedRevision
        if (applied.revisionId != desired.revisionId ||
            applied.status != PolicyConstants.SYNC_STATUS_APPLIED
        ) return
        setState {
            it.copy(
                confirmedControl = desired,
                policies = desired.toParentPolicies(),
                modes = desired.toParentModes(),
                activeMode = desired.toParentActiveMode(),
                safeMode = desired.toParentSafeMode(),
                confirmedStates = matchingRuntimeStates(
                    desired.revisionId,
                    latestRuntimeStates
                )
            )
        }
    }

    private fun promoteConfirmedRuntimeStates() {
        val confirmedRevision = state.value.confirmedControl?.revisionId ?: return
        val matching = matchingRuntimeStates(confirmedRevision, latestRuntimeStates)
        if (matching.isEmpty()) return
        setState { it.copy(confirmedStates = matching) }
    }

    private fun submitControlOperation(operation: ControlOperation) {
        when (state.value.controlAvailability) {
            ControlAvailability.VALID -> executeControlOperation(operation)
            ControlAvailability.INVALID -> {
                setMessage("Control changes are disabled until synchronized control is repaired")
            }
            ControlAvailability.MISSING,
            ControlAvailability.UNKNOWN -> {
                pendingControlOperations.addLast(operation)
                setMessage("Preparing synchronized TV controls; your change is queued")
                maybeSeedControlV2()
            }
        }
    }

    private fun flushPendingControlOperations() {
        while (pendingControlOperations.isNotEmpty()) {
            executeControlOperation(pendingControlOperations.removeFirst())
        }
    }

    private fun executeControlOperation(operation: ControlOperation) {
        val deviceId = state.value.selectedDeviceId ?: return setMessage("Select a TV first")
        val repository = writer ?: return setMessage("Firebase is unavailable")
        when (operation) {
            is ControlOperation.UpdatePolicy ->
                repository.updatePolicy(deviceId, operation.packageName, operation.policy, ::controlSent, ::setMessage)
            is ControlOperation.SetPin ->
                repository.setPin(deviceId, operation.pin, ::controlSent, ::setMessage)
            is ControlOperation.CreateMode ->
                repository.createMode(deviceId, operation.name, ::controlSent, ::setMessage)
            is ControlOperation.RenameMode ->
                repository.updateModeName(deviceId, operation.modeId, operation.name, ::controlSent, ::setMessage)
            is ControlOperation.DeleteMode ->
                repository.deleteMode(
                    deviceId,
                    operation.modeId,
                    operation.activeModeId,
                    ::controlSent,
                    ::setMessage
                )
            is ControlOperation.UpdateModePolicy ->
                repository.updateModePolicy(
                    deviceId,
                    operation.modeId,
                    operation.packageName,
                    operation.policy,
                    ::controlSent,
                    ::setMessage
                )
            is ControlOperation.SetActiveMode ->
                repository.setActiveMode(deviceId, operation.mode, ::controlSent, ::setMessage)
            is ControlOperation.StartSafeMode ->
                repository.startSafeMode(deviceId, operation.durationMinutes, ::controlSent, ::setMessage)
            ControlOperation.StopSafeMode ->
                repository.stopSafeMode(deviceId, ::controlSent, ::setMessage)
            is ControlOperation.UpdateBudget ->
                repository.updateBudget(deviceId, operation.dailyLimitMinutes, ::controlSent, ::setMessage)
            is ControlOperation.UpdateAllowlist ->
                repository.updateAllowlist(deviceId, operation.enabled, ::controlSent, ::setMessage)
            is ControlOperation.UpdateCustomBlockedDomains ->
                repository.updateCustomBlockedDomains(deviceId, operation.domains, ::controlSent, ::setMessage)
        }
    }

    private fun setMessage(message: String?) = setState { it.copy(message = message) }

    private inline fun setState(transform: (ParentSyncUiState) -> ParentSyncUiState) {
        _state.value = transform(_state.value)
    }

    override fun onCleared() {
        auth.removeAuthStateListener(authListener)
        syncRepository?.close()
        serverClock.stop()
        super.onCleared()
    }
}
