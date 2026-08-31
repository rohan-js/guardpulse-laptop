package com.guardpulse.parentcontrol.parent

import android.graphics.Color as AndroidColor
import android.net.Uri
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.ActivityResultLauncher
import androidx.activity.viewModels
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Add
import androidx.compose.material.icons.outlined.Check
import androidx.compose.material.icons.outlined.Close
import androidx.compose.material.icons.outlined.Laptop
import androidx.compose.material.icons.outlined.Lock
import androidx.compose.material.icons.outlined.Refresh
import androidx.compose.material.icons.outlined.Search
import androidx.compose.material.icons.outlined.Security
import androidx.compose.material.icons.outlined.Tv
import androidx.compose.material3.AssistChip
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.core.view.WindowCompat
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.guardpulse.parentcontrol.shared.PolicyConstants
import com.guardpulse.parentcontrol.shared.ControlProtocol
import com.guardpulse.parentcontrol.shared.DeviceFreshness
import com.guardpulse.parentcontrol.shared.PinHasher
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import java.util.Locale

internal fun normalizeCustomDomainForUi(raw: String): String? {
    var s = raw.trim().lowercase()
    if (s.startsWith("http://")) s = s.substring(7)
    else if (s.startsWith("https://")) s = s.substring(8)
    s = s.trimEnd('/')
    if (s.isEmpty() || s.length > 253 || s.contains("..") || s.startsWith("-") || s.startsWith(".")) return null
    val domainPart = if (s.contains("/")) s.substringBefore("/") else s
    val pathPart = if (s.contains("/")) s.substringAfter("/") else ""
    val labels = domainPart.split(".")
    if (labels.size < 2) return null
    for (label in labels) {
        if (label.isEmpty() || label.length > 63 || label.startsWith("-") || label.endsWith("-")) return null
        for (ch in label) if (!(ch in 'a'..'z' || ch in '0'..'9' || ch == '-')) return null
    }
    val tld = labels.last()
    if (tld.length < 2 || tld.any { it !in 'a'..'z' }) return null
    if (pathPart.isNotEmpty()) {
        for (ch in pathPart) {
            if (!(ch in 'a'..'z' || ch in '0'..'9' || ch == '/' || ch == '-' || ch == '_' || ch == '.' || ch == '?' || ch == '=' || ch == '&')) return null
        }
    }
    return s
}

@Composable
private fun CustomBlockedSitesCard(
    syncState: ParentSyncUiState,
    onUpdateCustomBlockedDomains: (List<String>) -> Unit,
    onConfirmAction: (String, String, String, Boolean, () -> Unit) -> Unit
) {
    val customControlsEnabled = syncState.controlAvailability == ControlAvailability.VALID
    val currentCustom = syncState.confirmedControl?.customBlockedDomains ?: syncState.desiredControl?.customBlockedDomains
    var customInput by remember(currentCustom) { mutableStateOf("") }
    var customDomains by remember(currentCustom) { mutableStateOf(currentCustom?.domains ?: emptyList()) }
    val normalizedInput = remember(customInput) { normalizeCustomDomainForUi(customInput) }
    val canAdd = customControlsEnabled && normalizedInput != null && normalizedInput !in customDomains && customDomains.size < 100
    GuardCard {
        Text("Custom Blocked Sites & URLs", style = MaterialTheme.typography.titleLarge, color = GuardNavy, fontWeight = FontWeight.Bold)
        Text("Block specific websites and URL paths (e.g. youtube.com or youtube.com/shorts). Works across all browsers. Max 100.", color = TextMuted, modifier = Modifier.padding(top = 6.dp))
        if (currentCustom != null && currentCustom.domains.isNotEmpty()) {
            Text("Current: ${currentCustom.domains.joinToString(", ")}", color = TextMuted, style = MaterialTheme.typography.labelMedium, modifier = Modifier.padding(top = 8.dp))
        } else {
            Text("Current: none", color = TextMuted, style = MaterialTheme.typography.labelMedium, modifier = Modifier.padding(top = 8.dp))
        }
        if (customDomains.isNotEmpty()) {
            @OptIn(androidx.compose.foundation.layout.ExperimentalLayoutApi::class)
            androidx.compose.foundation.layout.FlowRow(modifier = Modifier.fillMaxWidth().padding(top = 12.dp), horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                customDomains.forEach { domain -> AssistChip(onClick = { customDomains = customDomains.filter { it != domain } }, label = { Text(domain) }, trailingIcon = { Icon(Icons.Outlined.Close, contentDescription = "Remove $domain", modifier = Modifier.size(16.dp)) }) } }
        }
        Row(Modifier.fillMaxWidth().padding(top = 12.dp), horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
            OutlinedTextField(value = customInput, onValueChange = { customInput = it.lowercase().filter { ch -> ch in 'a'..'z' || ch in '0'..'9' || ch == '.' || ch == '-' || ch == '/' || ch == ':' }.take(253) }, label = { Text("Add domain") }, placeholder = { Text("youtube.com") }, enabled = customControlsEnabled, singleLine = true, modifier = Modifier.weight(1f))
            Button(onClick = { val n = normalizedInput; if (n != null && n !in customDomains && customDomains.size < 100) { customDomains = customDomains + n; customInput = "" } }, enabled = canAdd, colors = ButtonDefaults.buttonColors(containerColor = GuardNavy), modifier = Modifier.height(58.dp)) { Icon(Icons.Outlined.Add, contentDescription = null); Spacer(Modifier.width(4.dp)); Text("Add") }
        }
        if (customDomains.size >= 100) Text("Maximum 100 domains.", color = AlertRed, style = MaterialTheme.typography.labelSmall, modifier = Modifier.padding(top = 6.dp))
        Row(Modifier.fillMaxWidth().padding(top = 12.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            Button(onClick = { val pending = normalizedInput; val finalDomains = if (pending != null && pending !in customDomains && customDomains.size < 100) customDomains + pending else customDomains; if (pending != null && pending !in customDomains && customDomains.size < 100) { customDomains = finalDomains; customInput = "" }; onConfirmAction("Update blocked sites?", if (finalDomains.isEmpty()) "All custom site blocks will be removed." else "Blocked: ${finalDomains.joinToString(", ")}", "Save", false) { onUpdateCustomBlockedDomains(finalDomains) } }, enabled = customControlsEnabled, colors = ButtonDefaults.buttonColors(containerColor = GuardNavy), shape = RoundedCornerShape(50), modifier = Modifier.weight(1f).height(52.dp)) { Text("Save") }
            OutlinedButton(onClick = { customDomains = emptyList(); onUpdateCustomBlockedDomains(emptyList()) }, enabled = customControlsEnabled && (currentCustom?.domains?.isNotEmpty() == true || customDomains.isNotEmpty()), modifier = Modifier.weight(1f).height(52.dp)) { Text("Clear") }
        }
        if (!customControlsEnabled) Text("Synchronized control unavailable - changes are disabled.", color = AlertRed, style = MaterialTheme.typography.labelSmall, modifier = Modifier.padding(top = 8.dp))
    }
}

internal fun pinValidationMessage(pin: String): String? =
    if (pin.matches(Regex("\\d{6}"))) null else "PIN must be 6 digits"

internal fun safeModeValidationMessage(durationMinutes: Int): String? =
    if (durationMinutes in 1..1440) null
    else "Safe Mode duration must be between 1 and 1440 minutes"

internal fun isPendingUnlock(request: UnlockRequest, now: Long): Boolean {
    return request.status == PolicyConstants.UNLOCK_PENDING &&
        (request.expiresAt == null || now <= request.expiresAt)
}


@Composable
internal fun SyncHealthCard(
    state: ParentSyncUiState,
    onReconnect: () -> Unit,
    onRepairControl: () -> Unit
) {
    val selectedDevice = state.devices.firstOrNull { it.deviceId == state.selectedDeviceId }
    val protocolReady = state.syncRuntime.protocolVersion >= PolicyConstants.SYNC_PROTOCOL_VERSION
    val tvConnected = if (protocolReady) state.syncRuntime.connected else selectedDevice?.online == true
    val freshness = ControlProtocol.freshness(tvConnected, selectedDevice?.lastSeen, state.serverNow)
    val desired = state.desiredRevision
    val applied = state.appliedRevision
    val syncStatus = deriveSyncStatus(
        phoneConnected = state.phoneConnected,
        controlAvailability = state.controlAvailability,
        protocolVersion = state.syncRuntime.protocolVersion,
        desired = desired,
        applied = applied,
        freshness = freshness
    )
    val statusText = if (!state.phoneConnected) {
        if (desired?.revisionId != null && applied.revisionId != desired.revisionId) {
            "Phone offline - writes queued"
        } else {
            "Phone offline"
        }
    } else when (syncStatus) {
        ParentSyncStatus.SENDING -> "Phone offline - writes queued"
        ParentSyncStatus.WAITING_FOR_TV -> "Waiting for laptop"
        ParentSyncStatus.APPLIED -> "Applied"
        ParentSyncStatus.DELAYED -> "Laptop connection delayed"
        ParentSyncStatus.OFFLINE_PENDING -> "Laptop offline - change pending"
        ParentSyncStatus.FAILED -> "Laptop rejected latest change"
        ParentSyncStatus.TV_UPDATE_REQUIRED -> "Laptop update required"
        else -> when (freshness) {
            DeviceFreshness.LIVE -> "Synchronized"
            DeviceFreshness.DELAYED -> "Laptop connection delayed"
            DeviceFreshness.OFFLINE -> "Laptop offline"
        }
    }
    val healthy = state.phoneConnected && (syncStatus == ParentSyncStatus.APPLIED ||
        (syncStatus == ParentSyncStatus.IDLE && freshness == DeviceFreshness.LIVE)
    )
    GuardCard {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text("Synchronization", style = MaterialTheme.typography.titleLarge, color = GuardNavy, fontWeight = FontWeight.Bold)
                StatusLabel(
                    statusText,
                    when {
                        healthy -> SuccessGreen
                        syncStatus == ParentSyncStatus.WAITING_FOR_TV || syncStatus == ParentSyncStatus.DELAYED -> ActionBlue
                        else -> AlertRed
                    },
                    modifier = Modifier.padding(top = 8.dp)
                )
            }
            IconButton(onClick = onReconnect) {
                Icon(Icons.Outlined.Refresh, contentDescription = "Reconnect", tint = ActionBlue)
            }
        }
        RuntimeRow("Phone Firebase", if (state.phoneConnected) "Connected" else "Offline", state.phoneConnected)
        RuntimeRow(
            "Laptop connection",
            when (freshness) {
                DeviceFreshness.LIVE -> "Live"
                DeviceFreshness.DELAYED -> "Delayed"
                DeviceFreshness.OFFLINE -> "Offline"
            },
            freshness == DeviceFreshness.LIVE
        )
        RuntimeRow("Sync protocol", if (protocolReady) "V2" else "Legacy", protocolReady)
        state.syncRuntime.lastPolicyAppliedAt?.let {
            RuntimeRow("Policy applied", formatTimestamp(it), true)
        }
        state.syncRuntime.lastUsageWriteAt?.let {
            RuntimeRow("Usage updated", formatTimestamp(it), true)
        }
        state.syncRuntime.lastInventoryWriteAt?.let {
            RuntimeRow("Inventory updated", formatTimestamp(it), true)
        }
        state.commands.firstOrNull()?.let { command ->
            RuntimeRow(
                "Latest command",
                "${command.type}: ${command.status}",
                command.status == PolicyConstants.COMMAND_DONE
            )
            command.error?.let { Text(it, color = AlertRed, modifier = Modifier.padding(top = 6.dp)) }
        }
        val currentRuntimeError = state.syncRuntime.lastError.takeIf {
            (state.syncRuntime.lastErrorAt ?: 0L) >= (state.syncRuntime.lastSuccessAt ?: 0L)
        }
        val error = applied.error ?: currentRuntimeError
        error?.let { Text(it, color = AlertRed, modifier = Modifier.padding(top = 10.dp)) }
        if (state.controlAvailability == ControlAvailability.INVALID) {
            Text(
                state.controlError ?: "The synchronized control snapshot is malformed.",
                color = AlertRed,
                modifier = Modifier.padding(top = 10.dp)
            )
            OutlinedButton(
                onClick = onRepairControl,
                modifier = Modifier.fillMaxWidth().padding(top = 10.dp),
                border = BorderStroke(1.dp, AlertRed)
            ) {
                Text("Repair synchronized control", color = AlertRed)
            }
        }
    }
}

@Composable
internal fun SecurityTab(
    selectedDeviceId: String?,
    loadingDeviceDetails: Boolean,
    apps: Map<String, ParentApp>,
    states: Map<String, ParentState>,
    modes: List<ParentMode>,
    activeMode: ActiveMode,
    safeMode: SafeModeState,
    security: SecurityRuntime,
    unlockRequests: List<UnlockRequest>,
    syncState: ParentSyncUiState,
    onSetPin: (String) -> Unit,
    onApproveUnlock: (UnlockRequest, String, Long?) -> Unit,
    onDenyUnlock: (UnlockRequest) -> Unit,
    onCreateMode: (String) -> Unit,
    onRenameMode: (String, String) -> Unit,
    onDeleteMode: (String) -> Unit,
    onUpdateModePolicy: (String, String, ParentPolicy) -> Unit,
    onSetActiveMode: (ParentMode?) -> Unit,
    onStartSafeMode: (Int) -> Unit,
    onStopSafeMode: () -> Unit,
    onUpdateBudget: (Int?) -> Unit,
    onUpdateAllowlist: (Boolean) -> Unit,
    onUpdateCustomBlockedDomains: (List<String>) -> Unit,
    onConfirmAction: (String, String, String, Boolean, () -> Unit) -> Unit,
    onOpenTvSetup: () -> Unit,
    onReconnect: () -> Unit,
    onRepairControl: () -> Unit
) {
    var pin by remember { mutableStateOf("") }
    var newModeName by remember { mutableStateOf("") }
    var expandedModeId by remember(modes) {
        mutableStateOf(modes.firstOrNull { it.modeId == activeMode.modeId }?.modeId)
    }
    var safeModeCustomMinutes by remember { mutableStateOf("") }
    val safeModeActive = safeMode.isActive(syncState.serverNow)
    val safeModeUntil = safeMode.until
    LazyColumn(verticalArrangement = Arrangement.spacedBy(18.dp), contentPadding = PaddingValues(18.dp)) {
        item {
            Text("Security Settings", style = MaterialTheme.typography.headlineSmall, color = GuardNavy, fontWeight = FontWeight.Bold)
            Text("Manage protection layers and review pending requests.", color = TextMuted, modifier = Modifier.padding(top = 4.dp))
        }
        if (selectedDeviceId == null) {
            item {
                EmptyPanel("No laptop selected", "Select or pair a laptop before changing security settings.")
            }
            return@LazyColumn
        }
        if (loadingDeviceDetails) {
            item {
                EmptyPanel("Loading security", "Reading laptop protection health from Firebase...")
            }
        }
        item {
            GuardCard {
                Text("Protection Health", style = MaterialTheme.typography.titleLarge, color = GuardNavy, fontWeight = FontWeight.Bold)
                RuntimeRow("Enforcement Mode", security.enforcementMode, security.enforcementMode != PolicyConstants.ENFORCEMENT_UNPROTECTED)
                RuntimeRow(if (security.deviceAdminSetupAvailable) "Device Admin" else "Device Admin unavailable", if (security.deviceAdmin) "Active" else if (!security.deviceAdminSetupAvailable) "Unavailable" else "Needs setup", security.deviceAdmin || !security.deviceAdminSetupAvailable)
                RuntimeRow("Accessibility", if (security.accessibility) "Active" else "Needs action", security.accessibility)
                RuntimeRow("Usage Access", if (security.usageAccess) "Active" else "Needs action", security.usageAccess)
                RuntimeRow(
                    "Network Filter",
                    "Not required for app locks",
                    true
                )
                RuntimeRow(
                    "Background Access",
                    if (security.backgroundUnrestricted) "Unrestricted" else "Battery restricted",
                    security.backgroundUnrestricted
                )
                RuntimeRow("PIN", if (security.pinConfigured) "Configured" else "Missing", security.pinConfigured)
                if (security.pinConfigured && security.pinHashVersion in 0 until PinHasher.CURRENT_VERSION) {
                    Text(
                        "PIN security upgrade required. Set a new PIN below to upgrade protection.",
                        color = AlertRed,
                        modifier = Modifier.padding(top = 8.dp)
                    )
                }
                RuntimeRow("Healthy", if (security.protectionHealthy) "Healthy" else "Needs setup", security.protectionHealthy)
                RuntimeRow("Active Mode", activeMode.modeName ?: "Normal policy", activeMode.modeId != null)
                RuntimeRow(
                    "Safe Mode",
                    if (safeModeActive) "Active until ${formatTimestamp(safeModeUntil)}" else "Off",
                    !safeModeActive
                )
                if (security.enforcementMode == PolicyConstants.ENFORCEMENT_FALLBACK) {
                    Text(
                        "Fallback mode protects via Accessibility and PIN gate. It is not uninstall-proof.",
                        color = GuardNavy,
                        modifier = Modifier
                            .padding(top = 12.dp)
                            .clip(RoundedCornerShape(10.dp))
                            .background(SurfaceTint)
                            .padding(14.dp)
                    )
                }
                security.lastSyncError?.let { Text("Last sync error: $it", color = AlertRed, modifier = Modifier.padding(top = 10.dp)) }
                security.lastForegroundPackage?.let { Text("Last foreground: $it", color = TextMuted, modifier = Modifier.padding(top = 8.dp)) }
            }
        }
        item {
            SyncHealthCard(syncState, onReconnect) {
                onConfirmAction(
                    "Repair synchronized control?",
                    "This replaces the malformed V2 control with the last valid legacy-compatible policy.",
                    "Repair",
                    true,
                    onRepairControl
                )
            }
        }
        item {
            GuardCard {
                Text("Emergency Safe Mode", style = MaterialTheme.typography.titleLarge, color = GuardNavy, fontWeight = FontWeight.Bold)
                Text(
                    if (safeModeActive) {
                        "All app and bypass-tool locks are paused until ${formatTimestamp(safeModeUntil)}."
                    } else {
                        "Pause all laptop PIN locks for a chosen duration without disabling sync, inventory, or health reporting."
                    },
                    color = TextMuted,
                    modifier = Modifier.padding(top = 6.dp)
                )
                if (safeModeActive) {
                    Button(
                        onClick = {
                            onConfirmAction(
                                "Deactivate Safe Mode?",
                                "All laptop locks will resume immediately.",
                                "Deactivate",
                                true,
                                onStopSafeMode
                            )
                        },
                        colors = ButtonDefaults.buttonColors(containerColor = AlertRed),
                        shape = RoundedCornerShape(50),
                        modifier = Modifier.fillMaxWidth().padding(top = 14.dp).height(52.dp)
                    ) {
                        Icon(Icons.Outlined.Security, contentDescription = null)
                        Spacer(Modifier.width(8.dp))
                        Text("Deactivate Safe Mode")
                    }
                } else {
                    Row(Modifier.fillMaxWidth().padding(top = 14.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        listOf(15, 30, 60, 120).forEach { minutes ->
                            OutlinedButton(
                                onClick = {
                                    onConfirmAction(
                                        "Start Safe Mode?",
                                        "All laptop PIN locks will pause for $minutes minutes, until ${formatTimestamp(syncState.serverNow + minutes * 60_000L)}.",
                                        "Start",
                                        true
                                    ) { onStartSafeMode(minutes) }
                                },
                                modifier = Modifier.weight(1f)
                            ) {
                                Text("${minutes}m")
                            }
                        }
                    }
                    Row(Modifier.fillMaxWidth().padding(top = 10.dp), horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
                        OutlinedTextField(
                            safeModeCustomMinutes,
                            { safeModeCustomMinutes = it.filter(Char::isDigit).take(4) },
                            label = { Text("Custom minutes") },
                            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                            singleLine = true,
                            modifier = Modifier.weight(1f)
                        )
                        Button(
                            onClick = {
                                val minutes = safeModeCustomMinutes.toIntOrNull()
                                if (minutes != null && minutes in 1..1440) {
                                    onConfirmAction(
                                        "Start Safe Mode?",
                                        "All laptop PIN locks will pause for $minutes minutes, until ${formatTimestamp(syncState.serverNow + minutes * 60_000L)}.",
                                        "Start",
                                        true
                                    ) { onStartSafeMode(minutes) }
                                }
                            },
                            enabled = safeModeCustomMinutes.toIntOrNull()?.let { it in 1..1440 } == true,
                            colors = ButtonDefaults.buttonColors(containerColor = AlertRed),
                            modifier = Modifier.height(58.dp)
                        ) {
                            Text("Start")
                        }
                    }
                    Text("Custom duration must be 1 to 1440 minutes.", color = TextMuted, style = MaterialTheme.typography.labelSmall, modifier = Modifier.padding(top = 6.dp))
                }
            }
        }
        item {
            val budgetControlsEnabled = syncState.controlAvailability == ControlAvailability.VALID
            val currentBudget = syncState.confirmedControl?.budget ?: syncState.desiredControl?.budget
            var budgetText by remember(currentBudget) { mutableStateOf(currentBudget?.dailyLimitMinutes?.toString().orEmpty()) }
            val budgetParsed = budgetText.toIntOrNull()
            val budgetValid = budgetText.isEmpty() || (budgetParsed != null && budgetParsed in 1..1440)
            GuardCard {
                Text("Daily Screen-Time Budget", style = MaterialTheme.typography.titleLarge, color = GuardNavy, fontWeight = FontWeight.Bold)
                Text(
                    "Set a whole-device daily screen-time limit in minutes. Empty clears the budget.",
                    color = TextMuted,
                    modifier = Modifier.padding(top = 6.dp)
                )
                Text(
                    currentBudget?.let { "Current: ${it.dailyLimitMinutes} minutes per day" } ?: "Current: No budget",
                    color = TextMuted,
                    style = MaterialTheme.typography.labelMedium,
                    modifier = Modifier.padding(top = 8.dp)
                )
                OutlinedTextField(
                    value = budgetText,
                    onValueChange = { budgetText = it.filter(Char::isDigit).take(4) },
                    label = { Text("Minutes per day") },
                    placeholder = { Text("e.g. 120") },
                    enabled = budgetControlsEnabled,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth().padding(top = 14.dp)
                )
                if (!budgetValid) {
                    Text("Enter 1 to 1440 or leave empty to clear.", color = AlertRed, style = MaterialTheme.typography.labelSmall, modifier = Modifier.padding(top = 6.dp))
                } else {
                    Text("Valid range is 1 to 1440. Empty clears the budget.", color = TextMuted, style = MaterialTheme.typography.labelSmall, modifier = Modifier.padding(top = 6.dp))
                }
                Row(Modifier.fillMaxWidth().padding(top = 12.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    Button(
                        onClick = {
                            val parsed = budgetText.toIntOrNull()
                            if (budgetText.isEmpty()) {
                                onUpdateBudget(null)
                            } else if (parsed != null && parsed in 1..1440) {
                                onUpdateBudget(parsed)
                            }
                        },
                        enabled = budgetControlsEnabled && budgetValid,
                        colors = ButtonDefaults.buttonColors(containerColor = GuardNavy),
                        shape = RoundedCornerShape(50),
                        modifier = Modifier.weight(1f).height(52.dp)
                    ) {
                        Text("Save")
                    }
                    OutlinedButton(
                        onClick = {
                            budgetText = ""
                            onUpdateBudget(null)
                        },
                        enabled = budgetControlsEnabled,
                        modifier = Modifier.weight(1f).height(52.dp)
                    ) {
                        Text("Clear")
                    }
                }
                if (!budgetControlsEnabled) {
                    Text("Synchronized control unavailable - changes are disabled.", color = AlertRed, style = MaterialTheme.typography.labelSmall, modifier = Modifier.padding(top = 8.dp))
                }
            }
        }
        item {
            CustomBlockedSitesCard(syncState, onUpdateCustomBlockedDomains, onConfirmAction)
        }
        item {
            val allowlistControlsEnabled = syncState.controlAvailability == ControlAvailability.VALID
            val currentAllowlist = syncState.confirmedControl?.allowlist ?: syncState.desiredControl?.allowlist
            val allowlistEnabled = currentAllowlist?.enabled ?: false
            GuardCard {
                Text("Allowlist Mode", style = MaterialTheme.typography.titleLarge, color = GuardNavy, fontWeight = FontWeight.Bold)
                Text(
                    "Only inventoried apps + Windows system apps will run. Unknown apps will be blocked.",
                    color = if (allowlistEnabled) AlertRed else TextMuted,
                    modifier = Modifier.padding(top = 6.dp)
                )
                Text(
                    "Current: ${if (allowlistEnabled) "Enabled" else "Disabled"}",
                    color = TextMuted,
                    style = MaterialTheme.typography.labelMedium,
                    modifier = Modifier.padding(top = 8.dp)
                )
                Row(
                    Modifier.fillMaxWidth().padding(top = 14.dp),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text("Allowlist", color = GuardNavy, fontWeight = FontWeight.Bold)
                    Switch(
                        checked = allowlistEnabled,
                        onCheckedChange = { enabled ->
                            onConfirmAction(
                                if (enabled) "Enable allowlist mode?" else "Disable allowlist mode?",
                                "Only inventoried apps + Windows system apps will run. Unknown apps will be blocked.",
                                if (enabled) "Enable" else "Disable",
                                enabled
                            ) { onUpdateAllowlist(enabled) }
                        },
                        enabled = allowlistControlsEnabled
                    )
                }
                if (!allowlistControlsEnabled) {
                    Text("Synchronized control unavailable - changes are disabled.", color = AlertRed, style = MaterialTheme.typography.labelSmall, modifier = Modifier.padding(top = 8.dp))
                }
            }
        }
        item {
            ModesCard(
                apps = apps,
                states = states,
                serverNow = syncState.serverNow,
                modes = modes,
                activeMode = activeMode,
                expandedModeId = expandedModeId,
                newModeName = newModeName,
                onNewModeNameChange = { newModeName = it },
                onCreateMode = {
                    onCreateMode(newModeName)
                    newModeName = ""
                },
                onToggleMode = { modeId -> expandedModeId = if (expandedModeId == modeId) null else modeId },
                onRenameMode = onRenameMode,
                onDeleteMode = { mode ->
                    onConfirmAction(
                        "Delete ${mode.name}?",
                        "This permanently removes the mode and its app rules.",
                        "Delete",
                        true
                    ) {
                        if (expandedModeId == mode.modeId) expandedModeId = null
                        onDeleteMode(mode.modeId)
                    }
                },
                onSetActiveMode = onSetActiveMode,
                onUpdateModePolicy = onUpdateModePolicy
            )
        }
        item {
            GuardCard {
                Text("Laptop Setup Access", style = MaterialTheme.typography.titleLarge, color = GuardNavy, fontWeight = FontWeight.Bold)
                Text("Open the hidden setup screen on the selected laptop. The laptop will require the parent PIN before showing setup.", color = TextMuted, modifier = Modifier.padding(top = 6.dp))
                Button(
                    onClick = onOpenTvSetup,
                    colors = ButtonDefaults.buttonColors(containerColor = ActionBlue),
                    shape = RoundedCornerShape(50),
                    modifier = Modifier.fillMaxWidth().padding(top = 14.dp).height(52.dp)
                ) {
                    Icon(Icons.Outlined.Laptop, contentDescription = null)
                    Spacer(Modifier.width(8.dp))
                    Text("Open Laptop Setup")
                }
            }
        }
        item {
            GuardCard {
                Text("Parent PIN", style = MaterialTheme.typography.titleLarge, color = GuardNavy, fontWeight = FontWeight.Bold)
                if (security.pinConfigured && security.pinHashVersion in 0 until PinHasher.CURRENT_VERSION) {
                    Text(
                        "Your existing PIN still works, but it uses legacy hashing. Set it again to upgrade security.",
                        color = AlertRed,
                        modifier = Modifier.padding(top = 8.dp)
                    )
                }
                Row(Modifier.fillMaxWidth().padding(top = 18.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    repeat(6) { index ->
                        Box(
                            Modifier.weight(1f).height(54.dp).clip(RoundedCornerShape(8.dp)).background(SurfaceLight).border(1.dp, OutlineSoft, RoundedCornerShape(8.dp)),
                            contentAlignment = Alignment.Center
                        ) {
                            Text(if (pin.length > index || security.pinConfigured) "*" else "", color = GuardNavy, style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.Bold)
                        }
                    }
                }
                GuardTextField(
                    value = pin,
                    onValueChange = { pin = it.filter(Char::isDigit).take(6) },
                    label = "New 6-digit PIN",
                    placeholder = "Enter PIN",
                    visualTransformation = PasswordVisualTransformation(),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    modifier = Modifier.padding(top = 14.dp)
                )
                Button(
                    onClick = {
                        onConfirmAction(
                            if (security.pinConfigured) "Change parent PIN?" else "Set parent PIN?",
                            "This PIN controls the laptop lock wall and protected setup access.",
                            if (security.pinConfigured) "Change PIN" else "Set PIN",
                            false
                        ) { onSetPin(pin) }
                    },
                    colors = ButtonDefaults.buttonColors(containerColor = GuardNavy),
                    shape = RoundedCornerShape(50),
                    modifier = Modifier.fillMaxWidth().padding(top = 14.dp).height(52.dp)
                ) {
                    Icon(Icons.Outlined.Lock, contentDescription = null)
                    Spacer(Modifier.width(8.dp))
                    Text(if (security.pinConfigured) "Change PIN" else "Set PIN")
                }
            }
        }
        item { GuardSectionTitle("Pending Requests") }
        items(unlockRequests.filter { isPendingUnlock(it, System.currentTimeMillis()) }) { request ->
            GuardCard(modifier = Modifier.border(2.dp, ActionBlue.copy(alpha = 0.35f), RoundedCornerShape(14.dp))) {
                val appLabel = apps[request.packageName]?.label ?: request.packageName
                Row(horizontalArrangement = Arrangement.spacedBy(14.dp)) {
                    Box(Modifier.size(56.dp).clip(RoundedCornerShape(12.dp)).background(AlertRed), contentAlignment = Alignment.Center) {
                        Icon(Icons.Outlined.Laptop, contentDescription = null, tint = Color.White)
                    }
                    Column(Modifier.weight(1f)) {
                        Text(appLabel, color = GuardNavy, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold)
                        Text(request.packageName, color = TextMuted, style = MaterialTheme.typography.labelMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Text(request.reason, color = AlertRed)
                        Text("Age: ${formatAge(request.createdAt)}", color = TextMuted, style = MaterialTheme.typography.labelMedium)
                        Text("Requested: ${formatTimestamp(request.createdAt)}", color = TextMuted, style = MaterialTheme.typography.labelMedium)
                        Text("Expires: ${formatTimestamp(request.expiresAt)}", color = TextMuted, style = MaterialTheme.typography.labelMedium)
                        Text("Approval: ${unlockApprovalLabel(request)}", color = TextMuted, style = MaterialTheme.typography.labelMedium)
                    }
                }
                Row(Modifier.padding(top = 14.dp), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    OutlinedButton(onClick = { onDenyUnlock(request) }, modifier = Modifier.weight(1f)) {
                        Icon(Icons.Outlined.Close, contentDescription = null)
                        Spacer(Modifier.width(8.dp))
                        Text("Deny")
                    }
                    Button(
                        onClick = {
                            onApproveUnlock(
                                request,
                                PolicyConstants.UNLOCK_APPROVAL_ONE_VISIT,
                                null
                            )
                        },
                        colors = ButtonDefaults.buttonColors(containerColor = ActionBlue),
                        modifier = Modifier.weight(1f)
                    ) {
                        Icon(Icons.Outlined.Check, contentDescription = null)
                        Spacer(Modifier.width(8.dp))
                        Text("One Visit")
                    }
                }
                Row(Modifier.padding(top = 10.dp), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    Button(
                        onClick = {
                            onApproveUnlock(
                                request,
                                PolicyConstants.UNLOCK_APPROVAL_TIMED,
                                PolicyConstants.UNLOCK_15_MINUTES_MS
                            )
                        },
                        colors = ButtonDefaults.buttonColors(containerColor = GuardNavy),
                        modifier = Modifier.weight(1f)
                    ) {
                        Text("15 Minutes")
                    }
                    Button(
                        onClick = {
                            onApproveUnlock(
                                request,
                                PolicyConstants.UNLOCK_APPROVAL_TIMED,
                                PolicyConstants.UNLOCK_30_MINUTES_MS
                            )
                        },
                        colors = ButtonDefaults.buttonColors(containerColor = GuardNavy),
                        modifier = Modifier.weight(1f)
                    ) {
                        Text("30 Minutes")
                    }
                }
            }
        }
    }
}

@Composable
internal fun EventsTab(tamperEvents: List<TamperEvent>) {
    LazyColumn(verticalArrangement = Arrangement.spacedBy(14.dp), contentPadding = PaddingValues(18.dp)) {
        item {
            Text("Events", style = MaterialTheme.typography.headlineSmall, color = GuardNavy, fontWeight = FontWeight.Bold)
            Text("Tamper and protection events from the selected laptop.", color = TextMuted, modifier = Modifier.padding(top = 4.dp))
        }
        if (tamperEvents.isEmpty()) {
            item {
                EmptyPanel("No events", "Tamper and protection events will appear here.")
            }
        }
        items(tamperEvents) { event ->
            val critical = event.type.contains("disabled", ignoreCase = true) || event.type.contains("risky", ignoreCase = true)
            GuardCard(modifier = Modifier.border(1.dp, if (critical) AlertRed.copy(alpha = 0.35f) else OutlineSoft, RoundedCornerShape(14.dp))) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Box(Modifier.size(42.dp).clip(CircleShape).background(if (critical) ErrorSoft else SurfaceTint), contentAlignment = Alignment.Center) {
                        Icon(Icons.Outlined.Security, contentDescription = null, tint = if (critical) AlertRed else GuardNavy)
                    }
                    Spacer(Modifier.width(12.dp))
                    Column {
                        Text(event.type.ifBlank { "Event" }, color = GuardNavy, fontWeight = FontWeight.Bold)
                        Text(event.message ?: "No details", color = TextMuted)
                        Text("Time: ${formatTimestamp(event.createdAt)}", color = TextMuted, style = MaterialTheme.typography.bodySmall)
                    }
                }
            }
        }
    }
}
