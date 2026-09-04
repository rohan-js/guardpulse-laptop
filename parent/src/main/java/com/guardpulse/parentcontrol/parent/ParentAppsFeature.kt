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
import androidx.compose.material.icons.outlined.Lock
import androidx.compose.material.icons.outlined.Refresh
import androidx.compose.material.icons.outlined.Search
import androidx.compose.material.icons.outlined.Security
import androidx.compose.material.icons.outlined.Laptop
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

internal fun policyValidationMessage(app: ParentApp?, policy: ParentPolicy): String? = when {
    app?.blockable == false -> "This app is protected: ${app.protectedReason ?: "not blockable"}"
    policy.dailyLimitMinutes != null && policy.dailyLimitMinutes !in 1..1440 ->
        "Daily limit must be between 1 and 1440 minutes"
    policy.sessionLimitMinutes != null && policy.sessionLimitMinutes !in 1..1440 ->
        "Session limit must be between 1 and 1440 minutes"
    else -> null
}

internal fun confirmedAppState(
    hasConfirmedControl: Boolean,
    packageName: String,
    liveStates: Map<String, ParentState>,
    confirmedStates: Map<String, ParentState>
): ParentState {
    return if (hasConfirmedControl) {
        confirmedStates[packageName] ?: ParentState()
    } else {
        liveStates[packageName] ?: ParentState()
    }
}


@Composable
internal fun AppsTab(
    selectedDevice: ParentDevice?,
    selectedDeviceId: String?,
    loadingDeviceDetails: Boolean,
    apps: Map<String, ParentApp>,
    policies: Map<String, ParentPolicy>,
    states: Map<String, ParentState>,
    confirmedStates: Map<String, ParentState>,
    syncState: ParentSyncUiState,
    serverNow: Long,
    onUpdatePolicy: (String, ParentPolicy) -> Unit,
    onRescan: () -> Unit,
    onResetToday: (String) -> Unit
) {
    var query by remember { mutableStateOf("") }
    val filtered = apps.values
        .filter { it.label.contains(query, ignoreCase = true) || it.packageName.contains(query, ignoreCase = true) }
        .sortedBy { it.label.lowercase() }
    LazyColumn(
        verticalArrangement = Arrangement.spacedBy(14.dp),
        contentPadding = PaddingValues(18.dp)
    ) {
        item {
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
                Column {
                    Text("Managing Device", color = TextMuted, style = MaterialTheme.typography.labelMedium, fontWeight = FontWeight.Bold)
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Icon(Icons.Outlined.Laptop, contentDescription = null, tint = TextMuted)
                        Spacer(Modifier.width(8.dp))
                        Text(selectedDevice?.label ?: selectedDeviceId ?: "No laptop selected", color = GuardNavy, style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.Bold)
                    }
                }
                Button(onClick = onRescan, colors = ButtonDefaults.buttonColors(containerColor = ActionBlue), shape = RoundedCornerShape(50)) {
                    Icon(Icons.Outlined.Refresh, contentDescription = null)
                    Spacer(Modifier.width(8.dp))
                    Text("Rescan")
                }
            }
        }
        if (selectedDeviceId == null) {
            item {
                EmptyPanel("No laptop selected", "Select or pair a laptop before managing apps.")
            }
            return@LazyColumn
        }
        item {
            OutlinedTextField(
                query,
                { query = it },
                placeholder = { Text("Search apps...") },
                leadingIcon = { Icon(Icons.Outlined.Search, contentDescription = null) },
                shape = RoundedCornerShape(14.dp),
                singleLine = true,
                modifier = Modifier.fillMaxWidth().heightIn(min = 58.dp)
            )
        }
        if (loadingDeviceDetails) {
            item {
                EmptyPanel("Loading apps", "Waiting for the laptop to upload its app list.")
            }
            return@LazyColumn
        }
        if (apps.isEmpty()) {
            item {
                EmptyPanel("No apps yet", "Tap Rescan or open the Device Service agent on the laptop.")
            }
            return@LazyColumn
        }
        items(filtered) { app ->
            val policy = policies[app.packageName] ?: defaultParentPolicy(app.packageName)
            val liveState = states[app.packageName] ?: ParentState()
            val confirmedState = confirmedAppState(
                hasConfirmedControl = syncState.confirmedControl != null,
                packageName = app.packageName,
                liveStates = states,
                confirmedStates = confirmedStates
            )
            val pending = syncState.isAppPolicyPending(app.packageName)
            val requestedPolicy = syncState.desiredControl?.apps?.get(app.packageName)?.let { rule ->
                ParentPolicy(rule.manualBlocked, rule.dailyLimitMinutes, rule.sessionLimitMinutes)
            }
            AppPolicyCard(
                app,
                policy,
                confirmedState,
                liveState,
                pending,
                requestedPolicy,
                serverNow,
                onUpdatePolicy,
                onResetToday,
                browser = syncState.browser
            )
        }
    }
}

@Composable
internal fun AppPolicyCard(
    app: ParentApp,
    policy: ParentPolicy,
    state: ParentState,
    usageState: ParentState,
    pending: Boolean,
    requestedPolicy: ParentPolicy?,
    serverNow: Long,
    onUpdatePolicy: (String, ParentPolicy) -> Unit,
    onResetToday: (String) -> Unit,
    browser: BrowserState? = null
) {
    val usageNow by visibleUsageClock(serverNow)
    val usageMs = effectiveUsageMs(usageState, usageNow)
    var limitText by remember(app.packageName, policy.dailyLimitMinutes) {
        mutableStateOf(policy.dailyLimitMinutes?.toString().orEmpty())
    }
    var sessionLimitText by remember(app.packageName, policy.sessionLimitMinutes) {
        mutableStateOf(policy.sessionLimitMinutes?.toString().orEmpty())
    }
    var expanded by remember(app.packageName) { mutableStateOf(false) }
    var showBrowserTabs by remember(app.packageName) { mutableStateOf(false) }
    val sourceApp = app.packageName in PolicyConstants.sourceLockPackages
    val settingsApp = app.packageName in PolicyConstants.primarySettingsPackages
    val settingsSection = PolicyConstants.settingsSectionPolicy(app.packageName)
    val settingsSectionApp = settingsSection != null
    val settingsSectionName = settingsSection?.shortLabel ?: "Settings section"
    val lockControlled = app.blockable
    val networkBlocked = false
    val runtimeConfirmed = state.controlRevisionId != null
    val lockBlocked = if (runtimeConfirmed) {
        state.lockBlocked || (!app.blockable && state.fallbackLocked)
    } else {
        (lockControlled && (policy.manualBlocked || state.manualBlocked || state.dailyLimitBlocked)) ||
            (!app.blockable && state.fallbackLocked)
    }
    val sourceLocked = sourceApp && lockBlocked
    val settingsLocked = settingsApp && lockBlocked
    val settingsSectionsLocked = settingsSectionApp && lockBlocked
    val blocked = networkBlocked || lockBlocked
    val statusLabel = when {
        pending -> "Waiting for laptop"
        !app.blockable -> "Protected"
        sourceLocked -> "Live TV locked"
        settingsSectionsLocked -> "$settingsSectionName locked"
        settingsLocked -> "Settings locked"
        sourceApp -> "Live TV allowed"
        settingsSectionApp -> "$settingsSectionName allowed"
        settingsApp -> "Settings allowed"
        lockBlocked -> "App locked"
        state.dailyLimitBlocked -> "Daily limit lock"
        else -> "App allowed"
    }
    val statusColor = when {
        pending -> ActionBlue
        !app.blockable -> OutlineSoft
        blocked -> AlertRed
        else -> ActionBlue
    }
    GuardCard(modifier = Modifier.clickable { expanded = !expanded }) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Spacer(Modifier.width(5.dp).height(86.dp).clip(RoundedCornerShape(4.dp)).background(statusColor))
            Spacer(Modifier.width(14.dp))
            Box(
                Modifier.size(56.dp).clip(RoundedCornerShape(14.dp)).background(
                    when {
                        !app.blockable -> SurfaceTint
                        blocked -> ErrorSoft
                        else -> SurfaceTint
                    }
                ),
                contentAlignment = Alignment.Center
            ) {
                Icon(
                    if (!app.blockable) Icons.Outlined.Lock else Icons.Outlined.Laptop,
                    contentDescription = null,
                    tint = if (blocked) AlertRed else GuardNavy
                )
            }
            Spacer(Modifier.width(14.dp))
            Column(Modifier.weight(1f).padding(end = 10.dp)) {
                Text(
                    app.label,
                    color = GuardNavy,
                    style = MaterialTheme.typography.titleLarge,
                    fontWeight = FontWeight.Bold,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                StatusLabel(
                    statusLabel,
                    when {
                        !app.blockable -> OutlineSoft
                        blocked -> AlertRed
                        else -> ActionBlue
                    },
                    modifier = Modifier.padding(top = 5.dp)
                )
                Text(
                    app.packageName,
                    color = TextMuted,
                    style = MaterialTheme.typography.labelMedium,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.padding(top = 4.dp)
                )
                // Laptop browser visibility: only renders when this laptop's agent
                // reports a live tab snapshot.
                if (isBrowserApp(app.packageName, browser) && browser != null) {
                    BrowserNowLines(browser, modifier = Modifier.padding(top = 4.dp))
                    TextButton(
                        onClick = { showBrowserTabs = true },
                        contentPadding = PaddingValues(horizontal = 8.dp, vertical = 0.dp)
                    ) {
                        Text("View open tabs", color = ActionBlue, style = MaterialTheme.typography.labelMedium)
                    }
                }
                val reason = when {
                    pending && requestedPolicy?.manualBlocked == true -> "Lock requested; laptop confirmation pending"
                    pending -> "Unlock or limit change requested; laptop confirmation pending"
                    !app.blockable -> "Reason: ${app.protectedReason ?: "System critical"}"
                    sourceLocked && state.dailyLimitBlocked -> "Daily limit source lock"
                    sourceLocked && policy.manualBlocked -> "Live TV source locked by parent"
                    sourceLocked -> "Live TV source locked"
                    settingsSectionsLocked && policy.manualBlocked -> "$settingsSectionName locked by parent"
                    settingsSectionsLocked -> "$settingsSectionName locked"
                    settingsLocked && state.dailyLimitBlocked -> "Daily limit settings lock"
                    settingsLocked && policy.manualBlocked -> "Settings locked by parent"
                    settingsLocked -> "Settings locked"
                    lockBlocked && state.dailyLimitBlocked -> "Daily limit lock"
                    lockBlocked && policy.manualBlocked -> "Locked by parent"
                    lockBlocked -> "App locked"
                    state.dailyLimitBlocked -> "Daily limit lock"
                    policy.manualBlocked -> "Locked by parent"
                    policy.dailyLimitMinutes != null -> "Daily Limit Active (${policy.dailyLimitMinutes} mins)"
                    policy.sessionLimitMinutes != null -> "Session limit (${policy.sessionLimitMinutes} min)"
                    else -> null
                }
                reason?.let {
                    Text(
                        it,
                        color = if (blocked) AlertRed else TextMuted,
                        style = MaterialTheme.typography.labelSmall,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.padding(top = 2.dp)
                    )
                }
            }
            Switch(
                enabled = app.blockable,
                checked = !policy.manualBlocked,
                onCheckedChange = { allowed -> onUpdatePolicy(app.packageName, policy.copy(manualBlocked = !allowed)) }
            )
        }
        if (usageMs > 0 || state.dailyLimitBlocked) {
            Row(
                modifier = Modifier
                    .padding(start = 75.dp, top = 12.dp)
                    .clip(RoundedCornerShape(8.dp))
                    .background(if (state.dailyLimitBlocked) ErrorSoft else SurfaceTint)
                    .padding(12.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Icon(Icons.Outlined.Security, contentDescription = null, tint = if (state.dailyLimitBlocked) AlertRed else TextMuted)
                Spacer(Modifier.width(8.dp))
                Text("${formatUsage(usageMs)} used today", color = if (state.dailyLimitBlocked) AlertRed else TextMuted)
            }
        }
        if (expanded) {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(top = 16.dp)
                    .clip(RoundedCornerShape(10.dp))
                    .background(SurfaceLight)
                    .padding(14.dp)
            ) {
                Text(app.packageName, color = TextMuted, style = MaterialTheme.typography.bodySmall)
                Row(
                    Modifier
                        .fillMaxWidth()
                        .padding(top = 8.dp),
                    horizontalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    StatusChip(
                        when {
                            sourceLocked && policy.manualBlocked -> "Source locked by parent"
                            sourceApp -> if (policy.manualBlocked) "Source locked by parent" else "Source allowed by parent"
                            settingsSectionApp -> if (policy.manualBlocked) "$settingsSectionName locked by parent" else "$settingsSectionName allowed by parent"
                            settingsApp -> if (policy.manualBlocked) "Settings locked by parent" else "Settings allowed by parent"
                            policy.manualBlocked -> "Locked by parent"
                            else -> "Allowed by parent"
                        },
                        !policy.manualBlocked,
                        Modifier.weight(1f)
                    )
                    StatusChip(
                        when {
                            sourceLocked -> "Source lock active"
                            settingsSectionsLocked -> "$settingsSectionName lock active"
                            settingsLocked -> "Settings lock active"
                            lockBlocked -> "Screen lock active"
                            else -> "No screen lock"
                        },
                        !lockBlocked && (!networkBlocked || state.vpnApplied),
                        Modifier.weight(1f)
                    )
                }
                StatusChip(
                    "Mode: ${state.enforcementMode}",
                    state.enforcementMode != PolicyConstants.ENFORCEMENT_UNPROTECTED,
                    Modifier
                        .fillMaxWidth()
                        .padding(top = 6.dp)
                )
                Row(Modifier.padding(top = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                    OutlinedTextField(
                        limitText,
                        { limitText = it.filter(Char::isDigit).take(4) },
                        label = { Text("Daily Limit") },
                        suffix = { Text("min") },
                        enabled = app.blockable && !pending,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                        modifier = Modifier.weight(1f)
                    )
                    Spacer(Modifier.width(10.dp))
                    Button(enabled = app.blockable && !pending, onClick = {
                        onUpdatePolicy(app.packageName, policy.copy(dailyLimitMinutes = limitText.toIntOrNull()?.takeIf { it > 0 }))
                    }, colors = ButtonDefaults.buttonColors(containerColor = GuardNavy)) { Text("Save") }
                    Spacer(Modifier.width(8.dp))
                    OutlinedButton(enabled = app.blockable && !pending && policy.dailyLimitMinutes != null, onClick = {
                        limitText = ""
                        onUpdatePolicy(app.packageName, policy.copy(dailyLimitMinutes = null))
                    }) { Text("Clear") }
                }
                Row(Modifier.padding(top = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                    OutlinedTextField(
                        sessionLimitText,
                        { sessionLimitText = it.filter(Char::isDigit).take(4) },
                        label = { Text("Session limit") },
                        suffix = { Text("min") },
                        enabled = app.blockable && !pending,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                        modifier = Modifier.weight(1f)
                    )
                    Spacer(Modifier.width(10.dp))
                    Button(enabled = app.blockable && !pending, onClick = {
                        onUpdatePolicy(app.packageName, policy.copy(sessionLimitMinutes = sessionLimitText.toIntOrNull()?.takeIf { it > 0 }))
                    }, colors = ButtonDefaults.buttonColors(containerColor = GuardNavy)) { Text("Save") }
                    Spacer(Modifier.width(8.dp))
                    OutlinedButton(enabled = app.blockable && !pending && policy.sessionLimitMinutes != null, onClick = {
                        sessionLimitText = ""
                        onUpdatePolicy(app.packageName, policy.copy(sessionLimitMinutes = null))
                    }) { Text("Clear") }
                }
                TextButton(enabled = app.blockable && !pending, onClick = { onResetToday(app.packageName) }) { Text("Reset today") }
                state.lastError?.let { Text("Error: $it", color = AlertRed) }
            }
        }

        if (showBrowserTabs && browser != null) {
            BrowserTabListDialog(browser = browser, onDismiss = { showBrowserTabs = false })
        }
    }
}

@Composable
internal fun StatusChip(label: String, ok: Boolean, modifier: Modifier = Modifier) {
    AssistChip(
        onClick = {},
        label = { Text(label, maxLines = 1, overflow = TextOverflow.Ellipsis) },
        leadingIcon = {
            Icon(
                if (ok) Icons.Outlined.Check else Icons.Outlined.Security,
                contentDescription = null
            )
        },
        modifier = modifier
    )
}
