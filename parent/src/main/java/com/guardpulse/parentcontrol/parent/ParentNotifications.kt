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

fun notifyOffline(context: Context, deviceLabel: String) {
    if (!canNotify(context)) return
    try {
        val notification = NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.ic_dialog_alert)
            .setContentTitle("Device offline")
            .setContentText("$deviceLabel has been offline for more than 5 minutes")
            .setStyle(NotificationCompat.BigTextStyle().bigText("$deviceLabel has been offline for more than 5 minutes"))
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_STATUS)
            .setAutoCancel(true)
            .build()
        NotificationManagerCompat.from(context).notify(nextOfflineId++, notification)
    } catch (_: SecurityException) {
    }
}
