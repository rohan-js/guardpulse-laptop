package com.guardpulse.parentcontrol.parent

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.activity.ComponentActivity
import androidx.core.app.ActivityCompat
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat

const val CHANNEL_ID = "guardpulse_alerts"
private const val CHANNEL_NAME = "GuardPulse Alerts"

private var nextUnlockId = 3001
private var nextTamperId = 4001
private var nextOfflineId = 5001

fun ensureChannel(context: Context) {
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
        val channel = NotificationChannel(
            CHANNEL_ID,
            CHANNEL_NAME,
            NotificationManager.IMPORTANCE_HIGH
        ).apply {
            description = "Alerts for unlock requests, tamper events, and offline devices"
        }
        val nm = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.createNotificationChannel(channel)
    }
}

fun maybeRequestPermission(activity: ComponentActivity) {
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
        val granted = ContextCompat.checkSelfPermission(
            activity,
            Manifest.permission.POST_NOTIFICATIONS
        ) == PackageManager.PERMISSION_GRANTED
        if (!granted) {
            ActivityCompat.requestPermissions(
                activity,
                arrayOf(Manifest.permission.POST_NOTIFICATIONS),
                1001
            )
        }
    }
}

private fun canNotify(context: Context): Boolean {
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
        return ContextCompat.checkSelfPermission(
            context,
            Manifest.permission.POST_NOTIFICATIONS
        ) == PackageManager.PERMISSION_GRANTED
    }
    return NotificationManagerCompat.from(context).areNotificationsEnabled()
}

fun notifyUnlockRequest(context: Context, request: UnlockRequest) {
    if (!canNotify(context)) return
    try {
        val notification = NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.ic_dialog_alert)
            .setContentTitle("Unlock request")
            .setContentText("${request.packageName.ifBlank { "App" }}: ${request.reason.ifBlank { "needs approval" }}")
            .setStyle(
                NotificationCompat.BigTextStyle()
                    .bigText("${request.packageName} — ${request.reason} (expires ${formatTimestamp(request.expiresAt)})")
            )
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_ALARM)
            .setAutoCancel(true)
            .build()
        NotificationManagerCompat.from(context).notify(nextUnlockId++, notification)
    } catch (_: SecurityException) {
    }
}

fun notifyTamper(context: Context, event: TamperEvent) {
    if (!canNotify(context)) return
    try {
        val notification = NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.ic_dialog_alert)
            .setContentTitle("Tamper alert: ${event.type.ifBlank { "Event" }}")
            .setContentText(event.message ?: "Protection event reported")
            .setStyle(NotificationCompat.BigTextStyle().bigText(event.message ?: event.type))
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_ALARM)
            .setAutoCancel(true)
            .build()
        NotificationManagerCompat.from(context).notify(nextTamperId++, notification)
    } catch (_: SecurityException) {
    }
}

/**
 * Two-tier device-gone-dark alert:
 *  - SToppedRed (online:false without stoppedBy=parentPin): the protection was
 *    stopped outside a PIN-verified uninstall — treat as possible removal.
 *  - StoppedByPin (stoppedBy=parentPin): removed WITH the parent PIN — calm card,
 *    no alarm (this is the parent's own action).
 *  - Stale (no online:false, just silent >24h): amber "not reporting" — laptops
 *    sleep for hours, so only a full day of silence warrants a nudge.
 */
enum class OfflineSeverity { StoppedRed, StoppedByPin, Stale }

fun notifyOffline(
    context: Context,
    deviceLabel: String,
    severity: OfflineSeverity = OfflineSeverity.StoppedRed,
    lastSeen: Long? = null
) {
    if (!canNotify(context)) return
    val whenText = lastSeen?.let { "Last contact ${formatTimestamp(it)}" }
    val (title, text, category) = when (severity) {
        OfflineSeverity.StoppedByPin -> Triple(
            "Protection removed",
            "$deviceLabel was uninstalled using your parent PIN" + (whenText?.let { " — $it" } ?: "") + ".",
            NotificationCompat.CATEGORY_STATUS
        )
        OfflineSeverity.StoppedRed -> Triple(
            "POSSIBLE REMOVAL",
            "Protection was stopped on $deviceLabel outside a PIN-verified uninstall" +
                (whenText?.let { " — $it" } ?: "") + ". Check the device.",
            NotificationCompat.CATEGORY_ALARM
        )
        OfflineSeverity.Stale -> Triple(
            "Device not reporting",
            "$deviceLabel has not reported for more than 24 hours" + (whenText?.let { " — $it" } ?: "") + ".",
            NotificationCompat.CATEGORY_STATUS
        )
    }
    try {
        val notification = NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.ic_dialog_alert)
            .setContentTitle(title)
            .setContentText(text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(text))
            .setPriority(if (severity == OfflineSeverity.StoppedRed) NotificationCompat.PRIORITY_HIGH else NotificationCompat.PRIORITY_DEFAULT)
            .setCategory(category)
            .setAutoCancel(true)
            .build()
        NotificationManagerCompat.from(context).notify(nextOfflineId++, notification)
    } catch (_: SecurityException) {
    }
}
