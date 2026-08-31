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

internal data class PairingPayload(
    val deviceId: String?,
    val secret: String?
)

internal fun parsePairingPayload(payload: String): PairingPayload {
    if (payload.isBlank()) return PairingPayload(null, null)
    return runCatching {
        val uri = Uri.parse(payload)
        PairingPayload(
            deviceId = uri.getQueryParameter("deviceId"),
            secret = uri.getQueryParameter("secret")
        )
    }.getOrDefault(PairingPayload(null, null))
}

internal fun preferredDeviceId(
    devices: List<ParentDevice>,
    currentId: String?,
    persistedId: String?
): String? {
    return currentId?.takeIf { id -> devices.any { it.deviceId == id } }
        ?: persistedId?.takeIf { id -> devices.any { it.deviceId == id } }
        ?: devices.singleOrNull()?.deviceId
}


@Composable
internal fun DevicesTab(
    devices: List<ParentDevice>,
    loadingDevices: Boolean,
    selectedDeviceId: String?,
    onSelectDevice: (String) -> Unit,
    onRemoveDevice: (String) -> Unit,
    pairRequest: PairRequestState?,
    onPair: (String, String, String) -> Unit,
    onScanQr: () -> Unit
) {
    var payload by remember { mutableStateOf("") }
    var deviceId by remember { mutableStateOf("") }
    var codeDigits by remember { mutableStateOf(List(6) { "" }) }
    LazyColumn(
        verticalArrangement = Arrangement.spacedBy(18.dp),
        contentPadding = PaddingValues(18.dp)
    ) {
        item {
            GuardSectionTitle("Laptop Control")
            val selected = devices.firstOrNull { it.deviceId == selectedDeviceId }
            selected?.let {
                SelectedDeviceBanner(it)
            }
        }
        item {
            GuardCard {
                Text("Pair New Laptop", style = MaterialTheme.typography.titleLarge, color = GuardNavy, fontWeight = FontWeight.Bold)
                Text("Scan the QR code displayed on your laptop or enter details manually.", color = TextMuted, modifier = Modifier.padding(top = 8.dp))
                pairRequest?.let { request ->
                    StatusLabel(
                        "Pairing ${request.status}",
                        when (request.status) {
                            PolicyConstants.PAIR_ACCEPTED -> SuccessGreen
                            PolicyConstants.PAIR_PENDING -> ActionBlue
                            else -> AlertRed
                        },
                        Modifier.padding(top = 12.dp)
                    )
                }
                Button(
                    onClick = onScanQr,
                    colors = ButtonDefaults.buttonColors(containerColor = GuardNavy),
                    shape = RoundedCornerShape(8.dp),
                    modifier = Modifier.fillMaxWidth().padding(top = 18.dp).height(52.dp)
                ) {
                    Icon(Icons.Outlined.Add, contentDescription = null)
                    Spacer(Modifier.width(8.dp))
                    Text("Scan QR Code")
                }
                Text("OR MANUAL ENTRY", style = MaterialTheme.typography.labelSmall, color = TextMuted, fontWeight = FontWeight.Bold, modifier = Modifier.padding(top = 18.dp, bottom = 10.dp))
                GuardTextField(payload, { payload = it }, "QR payload", "guardpulse://pair?deviceId=...")
                Spacer(Modifier.height(12.dp))
                GuardTextField(deviceId, { deviceId = it }, "Device ID", "e.g. LAPTOP-9A8B7C")
                Text("6-Digit Code", style = MaterialTheme.typography.labelSmall, color = TextMuted, fontWeight = FontWeight.Bold, modifier = Modifier.padding(top = 14.dp, bottom = 6.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
                    codeDigits.forEachIndexed { index, digit ->
                        if (index == 3) Text("-", color = TextMuted)
                        OutlinedTextField(
                            value = digit,
                            onValueChange = { value ->
                                codeDigits = codeDigits.toMutableList().also { it[index] = value.filter(Char::isDigit).take(1) }
                            },
                            singleLine = true,
                            textStyle = MaterialTheme.typography.titleMedium.copy(textAlign = TextAlign.Center),
                            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                            modifier = Modifier.width(42.dp)
                        )
                    }
                }
                Button(
                    onClick = { onPair(payload, deviceId, codeDigits.joinToString("")) },
                    colors = ButtonDefaults.buttonColors(containerColor = ActionBlue),
                    shape = RoundedCornerShape(8.dp),
                    modifier = Modifier.fillMaxWidth().padding(top = 18.dp).height(52.dp)
                ) { Text("Connect Manually") }
            }
        }
        if (loadingDevices) {
            item {
                EmptyPanel("Loading laptops", "Reading paired laptops from Firebase...")
            }
        } else if (devices.isEmpty()) {
            item {
                EmptyPanel("No laptops paired", "Pair the laptop using the QR payload or manual code shown by the Device Service agent.")
            }
        }
        if (devices.isNotEmpty()) {
            item { GuardSectionTitle("Paired Devices", "${devices.count { it.online }} Active") }
            items(devices) { device ->
                DeviceCard(
                    device = device,
                    selected = device.deviceId == selectedDeviceId,
                    onSelectDevice = onSelectDevice,
                    onRemoveDevice = onRemoveDevice
                )
            }
        }
    }
}

@Composable
internal fun SelectedDeviceBanner(device: ParentDevice) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(top = 14.dp)
            .clip(RoundedCornerShape(12.dp))
            .background(GuardNavy)
            .padding(18.dp),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically
    ) {
        Column {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Outlined.Security, contentDescription = null, tint = Color.White)
                Spacer(Modifier.width(8.dp))
                Text(device.label, color = Color.White, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold)
            }
            Text(device.deviceId, color = Color.White.copy(alpha = 0.72f), style = MaterialTheme.typography.labelMedium, modifier = Modifier.padding(top = 8.dp))
        }
        StatusPill(if (device.online) "Online" else "Offline", device.online)
    }
}

@Composable
internal fun DeviceCard(
    device: ParentDevice,
    selected: Boolean,
    onSelectDevice: (String) -> Unit,
    onRemoveDevice: (String) -> Unit
) {
    GuardCard(
        modifier = Modifier
            .border(if (selected) 2.dp else 1.dp, if (selected) GuardNavy else OutlineSoft, RoundedCornerShape(14.dp))
            .clickable { onSelectDevice(device.deviceId) }
    ) {
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
            Box(Modifier.size(64.dp).clip(RoundedCornerShape(12.dp)).background(GuardNavy), contentAlignment = Alignment.Center) {
                Icon(Icons.Outlined.Laptop, contentDescription = null, tint = Color.White, modifier = Modifier.size(34.dp))
            }
            Spacer(Modifier.width(16.dp))
            Column(Modifier.weight(1f)) {
                Text(device.label, style = MaterialTheme.typography.titleLarge, color = GuardNavy, fontWeight = FontWeight.Bold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(device.deviceId, color = TextMuted, style = MaterialTheme.typography.labelMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
        Row(Modifier.fillMaxWidth().padding(top = 16.dp), horizontalArrangement = Arrangement.spacedBy(10.dp), verticalAlignment = Alignment.CenterVertically) {
            if (selected) {
                StatusPill("Active", true, Modifier.weight(1f))
            } else {
                OutlinedButton(
                    onClick = { onSelectDevice(device.deviceId) },
                    modifier = Modifier.weight(1f)
                ) {
                    Text("Select")
                }
            }
            OutlinedButton(
                onClick = { onRemoveDevice(device.deviceId) },
                modifier = Modifier.weight(1f),
                border = BorderStroke(1.dp, AlertRed)
            ) {
                Icon(Icons.Outlined.Close, contentDescription = null, tint = AlertRed)
                Spacer(Modifier.width(6.dp))
                Text("Remove", color = AlertRed)
            }
        }
        Row(Modifier.fillMaxWidth().padding(top = 14.dp), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            MetaTile("Mode", device.enforcementMode, device.enforcementMode != PolicyConstants.ENFORCEMENT_UNPROTECTED, Modifier.weight(1f))
            MetaTile("Health", if (device.protectionHealthy) "Healthy" else "Needs setup", device.protectionHealthy, Modifier.weight(1f))
        }
        MetaTile("Last seen", formatTimestamp(device.lastSeen), device.online, Modifier.fillMaxWidth().padding(top = 12.dp))
    }
}
