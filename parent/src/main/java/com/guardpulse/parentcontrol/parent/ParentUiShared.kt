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
import androidx.compose.material.icons.outlined.Timeline
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
import androidx.compose.runtime.produceState
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
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import kotlinx.coroutines.delay

internal data class ConfirmAction(
    val title: String,
    val body: String,
    val confirmLabel: String,
    val destructive: Boolean = false,
    val onConfirm: () -> Unit
)

internal val GuardNavy = Color(0xFF031636)
internal val GuardNavySoft = Color(0xFF1A2B4C)
internal val ActionBlue = Color(0xFF316BF3)
internal val SurfaceLight = Color(0xFFF8F9FF)
internal val SurfaceCard = Color(0xFFFFFFFF)
internal val SurfaceTint = Color(0xFFE5EEFF)
internal val OutlineSoft = Color(0xFFC5C6CF)
internal val TextMuted = Color(0xFF44474E)
internal val AlertRed = Color(0xFFBA1A1A)
internal val ErrorSoft = Color(0xFFFFDAD6)
internal val SuccessGreen = Color(0xFF10B981)
internal val SuccessSoft = Color(0xFFDCFCE7)

@Composable
internal fun visibleUsageClock(initialNow: Long) =
    produceState(initialValue = initialNow, key1 = Unit) {
        val serverOffset = initialNow - System.currentTimeMillis()
        while (true) {
            value = System.currentTimeMillis() + serverOffset
            delay(1_000L)
        }
    }

internal fun defaultParentPolicy(packageName: String): ParentPolicy {
    return if (PolicyConstants.isDefaultLocked(packageName)) {
        ParentPolicy(manualBlocked = true)
    } else {
        ParentPolicy()
    }
}

internal fun formatUsage(usageMs: Long): String {
    val totalSeconds = usageMs.coerceAtLeast(0L) / 1_000L
    val hours = totalSeconds / 3_600L
    val minutes = (totalSeconds % 3_600L) / 60L
    val seconds = totalSeconds % 60L
    return when {
        hours > 0L -> "${hours}h ${minutes}m"
        minutes > 0L -> "${minutes}m ${seconds}s"
        else -> "${seconds}s"
    }
}

internal fun modeUsageMs(
    packageName: String,
    states: Map<String, ParentState>,
    serverNow: Long
): Long {
    if (packageName in PolicyConstants.settingsSectionLockPackages) {
        return PolicyConstants.primarySettingsPackages.maxOfOrNull { settingsPackage ->
            states[settingsPackage]?.let { effectiveUsageMs(it, serverNow) } ?: 0L
        } ?: 0L
    }
    return states[packageName]?.let { effectiveUsageMs(it, serverNow) } ?: 0L
}

internal fun formatTimestamp(value: Long?): String {
    if (value == null || value <= 0L) return "unknown"
    return SimpleDateFormat("dd MMM, h:mm a", Locale.getDefault()).format(Date(value))
}

internal fun formatAge(value: Long?, now: Long = System.currentTimeMillis()): String {
    if (value == null || value <= 0L) return "unknown"
    val minutes = ((now - value).coerceAtLeast(0L) / 60_000L).coerceAtLeast(0L)
    return when {
        minutes < 1L -> "just now"
        minutes == 1L -> "1 min"
        minutes < 60L -> "$minutes mins"
        else -> "${minutes / 60L}h ${minutes % 60L}m"
    }
}

internal fun unlockApprovalLabel(request: UnlockRequest): String {
    if (request.status == PolicyConstants.UNLOCK_PENDING) return "waiting"
    return when (request.approvalType) {
        PolicyConstants.UNLOCK_APPROVAL_TIMED ->
            "${(request.approvalDurationMs ?: 0L) / 60_000L} minutes"
        PolicyConstants.UNLOCK_APPROVAL_ONE_VISIT, null -> "one visit"
        else -> request.approvalType
    }
}


@Composable
internal fun GuardCard(
    modifier: Modifier = Modifier,
    content: @Composable ColumnScope.() -> Unit
) {
    Card(
        colors = CardDefaults.cardColors(containerColor = SurfaceCard),
        border = BorderStroke(1.dp, OutlineSoft.copy(alpha = 0.6f)),
        shape = RoundedCornerShape(14.dp),
        elevation = CardDefaults.cardElevation(defaultElevation = 2.dp),
        modifier = modifier.fillMaxWidth()
    ) {
        Column(Modifier.fillMaxWidth().padding(20.dp), content = content)
    }
}

@Composable
internal fun GuardSectionTitle(title: String, trailing: String? = null) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(title, style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.Bold, color = GuardNavy)
        trailing?.let {
            Text(
                it,
                style = MaterialTheme.typography.labelMedium,
                color = TextMuted,
                modifier = Modifier
                    .clip(RoundedCornerShape(8.dp))
                    .background(SurfaceTint)
                    .padding(horizontal = 10.dp, vertical = 5.dp)
            )
        }
    }
}

@Composable
internal fun StatusPill(label: String, ok: Boolean, modifier: Modifier = Modifier) {
    val bg = if (ok) SuccessSoft else ErrorSoft
    val fg = if (ok) Color(0xFF166534) else AlertRed
    Row(
        modifier = modifier
            .clip(RoundedCornerShape(50))
            .background(bg)
            .padding(horizontal = 10.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(6.dp)
    ) {
        Box(Modifier.size(7.dp).clip(CircleShape).background(fg))
        Text(label, color = fg, style = MaterialTheme.typography.labelMedium, fontWeight = FontWeight.Bold)
    }
}

@Composable
internal fun MetaTile(label: String, value: String, ok: Boolean? = null, modifier: Modifier = Modifier) {
    val tileColor = if (ok == true) SuccessSoft else SurfaceLight
    Column(
        modifier = modifier
            .clip(RoundedCornerShape(10.dp))
            .background(tileColor)
            .border(1.dp, if (ok == true) SuccessGreen.copy(alpha = 0.25f) else OutlineSoft.copy(alpha = 0.5f), RoundedCornerShape(10.dp))
            .padding(12.dp)
    ) {
        Text(label.uppercase(Locale.US), color = TextMuted, style = MaterialTheme.typography.labelSmall, fontWeight = FontWeight.Bold)
        Text(value, color = if (ok == true) Color(0xFF065F46) else GuardNavy, style = MaterialTheme.typography.labelLarge, fontWeight = FontWeight.Bold, modifier = Modifier.padding(top = 4.dp))
    }
}

@Composable
internal fun EmptyPanel(title: String, detail: String) {
    GuardCard {
        Column(horizontalAlignment = Alignment.CenterHorizontally, modifier = Modifier.fillMaxWidth().padding(vertical = 16.dp)) {
            Icon(Icons.Outlined.Laptop, contentDescription = null, tint = OutlineSoft, modifier = Modifier.size(48.dp))
            Text(title, color = GuardNavy, fontWeight = FontWeight.Bold, modifier = Modifier.padding(top = 12.dp))
            Text(detail, color = TextMuted, textAlign = TextAlign.Center, modifier = Modifier.padding(top = 6.dp))
        }
    }
}

@Composable
internal fun ConfirmDialog(
    action: ConfirmAction,
    onDismiss: () -> Unit,
    onConfirm: () -> Unit
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(action.title, color = GuardNavy, fontWeight = FontWeight.Bold) },
        text = { Text(action.body, color = TextMuted) },
        confirmButton = {
            Button(
                onClick = onConfirm,
                colors = ButtonDefaults.buttonColors(
                    containerColor = if (action.destructive) AlertRed else ActionBlue
                )
            ) {
                Text(action.confirmLabel)
            }
        },
        dismissButton = {
            OutlinedButton(onClick = onDismiss) {
                Text("Cancel")
            }
        },
        containerColor = SurfaceCard
    )
}

@Composable
internal fun TopBar(selectedDeviceId: String?, onSignOut: () -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(SurfaceLight)
            .padding(horizontal = 18.dp, vertical = 14.dp),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically
    ) {
        Column {
            Text("GuardPulse Laptop", style = MaterialTheme.typography.headlineSmall, color = GuardNavy, fontWeight = FontWeight.Bold)
            Text(selectedDeviceId ?: "No laptop selected", color = TextMuted, style = MaterialTheme.typography.labelMedium)
        }
        Row(verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = {}) { Icon(Icons.Outlined.Security, contentDescription = "Protection", tint = GuardNavy) }
            IconButton(onClick = {}) { Icon(Icons.Outlined.Lock, contentDescription = "Settings", tint = GuardNavy) }
            TextButton(onClick = onSignOut) { Text("Sign out", color = AlertRed) }
        }
    }
}

@Composable
internal fun BottomNav(selected: Int, onSelect: (Int) -> Unit) {
    val labels = listOf("Devices", "Activity", "Apps", "Security", "Events")
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(SurfaceTint)
            .padding(horizontal = 6.dp, vertical = 10.dp),
        horizontalArrangement = Arrangement.SpaceAround,
        verticalAlignment = Alignment.CenterVertically
    ) {
        labels.forEachIndexed { index, label ->
            val active = selected == index
            Column(
                modifier = Modifier
                    .clip(RoundedCornerShape(22.dp))
                    .background(if (active) ActionBlue else Color.Transparent)
                    .clickable { onSelect(index) }
                    .padding(horizontal = 12.dp, vertical = 8.dp),
                horizontalAlignment = Alignment.CenterHorizontally
            ) {
                Icon(
                    imageVector = when (index) {
                        0 -> Icons.Outlined.Laptop
                        1 -> Icons.Outlined.Timeline
                        2 -> Icons.Outlined.Add
                        3 -> Icons.Outlined.Security
                        else -> Icons.Outlined.Lock
                    },
                    contentDescription = label,
                    tint = if (active) Color.White else TextMuted
                )
                Text(label, color = if (active) Color.White else GuardNavy, style = MaterialTheme.typography.labelSmall, fontWeight = if (active) FontWeight.Bold else FontWeight.Medium)
            }
        }
    }
}

@Composable
internal fun StatusLabel(label: String, color: Color, modifier: Modifier = Modifier) {
    Text(
        label.uppercase(Locale.US),
        color = if (color == OutlineSoft) TextMuted else color,
        style = MaterialTheme.typography.labelSmall,
        fontWeight = FontWeight.Bold,
        maxLines = 1,
        overflow = TextOverflow.Ellipsis,
        modifier = modifier
            .clip(RoundedCornerShape(6.dp))
            .background(if (color == OutlineSoft) SurfaceTint else color.copy(alpha = 0.16f))
            .border(1.dp, color.copy(alpha = 0.35f), RoundedCornerShape(6.dp))
            .padding(horizontal = 8.dp, vertical = 4.dp)
    )
}

@Composable
internal fun RuntimeRow(label: String, value: String, ok: Boolean) {
    Row(
        Modifier.fillMaxWidth().padding(top = 14.dp),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(label, color = GuardNavy)
        StatusPill(value, ok)
    }
}

@Composable
internal fun Panel(title: String, content: @Composable ColumnScope.() -> Unit) {
    GuardCard {
        Text(title, style = MaterialTheme.typography.titleMedium, color = GuardNavy, fontWeight = FontWeight.Bold)
        Spacer(Modifier.height(8.dp))
        content()
    }
}
