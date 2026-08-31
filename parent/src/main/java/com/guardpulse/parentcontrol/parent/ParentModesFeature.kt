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

internal data class ModeRuleSummary(
    val lockedApps: Int,
    val dailyLimits: Int
)

internal fun modeNameValidationMessage(name: String): String? =
    if (name.trim().isBlank()) "Mode name cannot be empty" else null

internal fun modeRuleSummary(mode: ParentMode): ModeRuleSummary = ModeRuleSummary(
    lockedApps = mode.appPolicies.values.count { it.manualBlocked },
    dailyLimits = mode.appPolicies.values.count { it.dailyLimitMinutes != null }
)


@Composable
internal fun ModesCard(
    apps: Map<String, ParentApp>,
    states: Map<String, ParentState>,
    serverNow: Long,
    modes: List<ParentMode>,
    activeMode: ActiveMode,
    expandedModeId: String?,
    newModeName: String,
    onNewModeNameChange: (String) -> Unit,
    onCreateMode: () -> Unit,
    onToggleMode: (String) -> Unit,
    onRenameMode: (String, String) -> Unit,
    onDeleteMode: (ParentMode) -> Unit,
    onSetActiveMode: (ParentMode?) -> Unit,
    onUpdateModePolicy: (String, String, ParentPolicy) -> Unit
) {
    GuardCard {
        Text("One-Tap Modes", style = MaterialTheme.typography.titleLarge, color = GuardNavy, fontWeight = FontWeight.Bold)
        Text("Create named policy sets. When a mode is active, listed apps use the mode rules and unlisted apps are allowed.", color = TextMuted, modifier = Modifier.padding(top = 6.dp))
        Row(Modifier.padding(top = 14.dp), horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
            GuardTextField(
                value = newModeName,
                onValueChange = onNewModeNameChange,
                label = "New mode name",
                placeholder = "Study time",
                modifier = Modifier.weight(1f)
            )
            Button(
                onClick = onCreateMode,
                colors = ButtonDefaults.buttonColors(containerColor = GuardNavy),
                modifier = Modifier.height(58.dp)
            ) {
                Icon(Icons.Outlined.Add, contentDescription = null)
                Spacer(Modifier.width(6.dp))
                Text("Create")
            }
        }
        if (modes.isEmpty()) {
            Text("No custom modes yet.", color = TextMuted, modifier = Modifier.padding(top = 14.dp))
            return@GuardCard
        }
        Column(Modifier.padding(top = 14.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            modes.forEach { mode ->
                ModeSummaryRow(
                    apps = apps,
                    states = states,
                    serverNow = serverNow,
                    mode = mode,
                    expanded = expandedModeId == mode.modeId,
                    active = activeMode.modeId == mode.modeId,
                    onToggleMode = onToggleMode,
                    onRenameMode = onRenameMode,
                    onDeleteMode = onDeleteMode,
                    onSetActiveMode = onSetActiveMode,
                    onUpdateModePolicy = onUpdateModePolicy
                )
            }
        }
    }
}

@Composable
internal fun ModeSummaryRow(
    apps: Map<String, ParentApp>,
    states: Map<String, ParentState>,
    serverNow: Long,
    mode: ParentMode,
    expanded: Boolean,
    active: Boolean,
    onToggleMode: (String) -> Unit,
    onRenameMode: (String, String) -> Unit,
    onDeleteMode: (ParentMode) -> Unit,
    onSetActiveMode: (ParentMode?) -> Unit,
    onUpdateModePolicy: (String, String, ParentPolicy) -> Unit
) {
    var renameText by remember(mode.modeId, mode.name) { mutableStateOf(mode.name) }
    val usageNow by visibleUsageClock(serverNow)
    val summary = modeRuleSummary(mode)
    Column(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(10.dp))
            .background(if (active) SurfaceTint else SurfaceLight)
            .border(1.dp, if (active) ActionBlue.copy(alpha = 0.45f) else OutlineSoft, RoundedCornerShape(10.dp))
            .clickable { onToggleMode(mode.modeId) }
            .padding(12.dp)
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(
                Modifier
                    .weight(1f)
                    .clickable { onToggleMode(mode.modeId) }
                    .padding(end = 10.dp)
            ) {
                Text(mode.name, color = GuardNavy, fontWeight = FontWeight.Bold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(
                    "${summary.lockedApps} locked · ${summary.dailyLimits} limits",
                    color = TextMuted,
                    style = MaterialTheme.typography.labelMedium
                )
            }
            StatusLabel(if (expanded) "Open" else "Closed", if (expanded) ActionBlue else OutlineSoft)
            Spacer(Modifier.width(10.dp))
            Switch(
                checked = active,
                onCheckedChange = { enabled ->
                    onSetActiveMode(if (enabled) mode else null)
                }
            )
        }
        if (expanded) {
            Row(Modifier.fillMaxWidth().padding(top = 14.dp), horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
                GuardTextField(
                    value = renameText,
                    onValueChange = { renameText = it },
                    label = "Mode name",
                    placeholder = "Mode name",
                    modifier = Modifier.weight(1f)
                )
                Button(onClick = { onRenameMode(mode.modeId, renameText) }, colors = ButtonDefaults.buttonColors(containerColor = GuardNavy), modifier = Modifier.height(58.dp)) {
                    Text("Save")
                }
            }
            Row(Modifier.fillMaxWidth().padding(top = 10.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedButton(onClick = { onDeleteMode(mode) }, modifier = Modifier.weight(1f)) {
                    Text("Delete")
                }
            }
            Text("Mode App Rules", color = GuardNavy, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.Bold, modifier = Modifier.padding(top = 18.dp))
            apps.values
                .filter { it.blockable }
                .sortedBy { it.label.lowercase() }
                .forEach { app ->
                    ModeAppPolicyRow(
                        modeId = mode.modeId,
                        app = app,
                        policy = mode.appPolicies[app.packageName] ?: ParentPolicy(),
                        usageMsToday = modeUsageMs(app.packageName, states, usageNow),
                        onUpdateModePolicy = onUpdateModePolicy
                    )
                }
        }
    }
}

@Composable
internal fun ModeAppPolicyRow(
    modeId: String,
    app: ParentApp,
    policy: ParentPolicy,
    usageMsToday: Long,
    onUpdateModePolicy: (String, String, ParentPolicy) -> Unit
) {
    var limitText by remember(modeId, app.packageName, policy.dailyLimitMinutes) {
        mutableStateOf(policy.dailyLimitMinutes?.toString().orEmpty())
    }
    Column(
        Modifier
            .fillMaxWidth()
            .padding(top = 12.dp)
            .clip(RoundedCornerShape(10.dp))
            .background(SurfaceLight)
            .padding(12.dp)
    ) {
        val limitReached = policy.dailyLimitMinutes?.let { usageMsToday >= it * 60_000L } == true
        val usageLabel = policy.dailyLimitMinutes?.let { limit ->
            "${formatUsage(usageMsToday)} / $limit mins used today"
        } ?: "${formatUsage(usageMsToday)} used today"
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(app.label, color = GuardNavy, fontWeight = FontWeight.Bold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(app.packageName, color = TextMuted, style = MaterialTheme.typography.labelSmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
            Switch(
                checked = !policy.manualBlocked,
                onCheckedChange = { allowed ->
                    onUpdateModePolicy(modeId, app.packageName, policy.copy(manualBlocked = !allowed))
                }
            )
        }
        Row(
            modifier = Modifier
                .padding(top = 10.dp)
                .clip(RoundedCornerShape(8.dp))
                .background(if (limitReached) ErrorSoft else SurfaceTint)
                .padding(horizontal = 10.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Icon(
                Icons.Outlined.Security,
                contentDescription = null,
                tint = if (limitReached) AlertRed else TextMuted,
                modifier = Modifier.size(18.dp)
            )
            Spacer(Modifier.width(8.dp))
            Text(
                usageLabel,
                color = if (limitReached) AlertRed else TextMuted,
                style = MaterialTheme.typography.labelMedium
            )
        }
        Row(Modifier.padding(top = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
            OutlinedTextField(
                limitText,
                { limitText = it.filter(Char::isDigit).take(4) },
                label = { Text("Mode daily limit") },
                suffix = { Text("min") },
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                modifier = Modifier.weight(1f)
            )
            Button(
                onClick = {
                    onUpdateModePolicy(
                        modeId,
                        app.packageName,
                        policy.copy(dailyLimitMinutes = limitText.toIntOrNull()?.takeIf { it > 0 })
                    )
                },
                colors = ButtonDefaults.buttonColors(containerColor = GuardNavy)
            ) {
                Text("Save")
            }
            OutlinedButton(
                enabled = policy.dailyLimitMinutes != null,
                onClick = {
                    limitText = ""
                    onUpdateModePolicy(modeId, app.packageName, policy.copy(dailyLimitMinutes = null))
                }
            ) {
                Text("Clear")
            }
        }
    }
}
