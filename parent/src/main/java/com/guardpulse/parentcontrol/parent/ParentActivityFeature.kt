package com.guardpulse.parentcontrol.parent

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Laptop
import androidx.compose.material.icons.outlined.Lock
import androidx.compose.material.icons.outlined.Public
import androidx.compose.material.icons.outlined.PlayCircle
import androidx.compose.material.icons.outlined.Timer
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FilterChipDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import java.util.concurrent.TimeUnit

private enum class ActivityWindow(val label: String, val days: Long) {
    TODAY("Today", 1L),
    WEEK("7 days", 7L),
    MONTH("30 days", 30L)
}

private enum class ActivityFreshness { LIVE, DELAYED, OFFLINE }

private fun activityFreshness(updatedAt: Long, now: Long): ActivityFreshness {
    if (updatedAt <= 0L) return ActivityFreshness.OFFLINE
    val age = (now - updatedAt).coerceAtLeast(0L)
    return when {
        age < 45_000L -> ActivityFreshness.LIVE
        age < 90_000L -> ActivityFreshness.DELAYED
        else -> ActivityFreshness.OFFLINE
    }
}

@Composable
internal fun ActivityTab(
    selectedDevice: ParentDevice?,
    selectedDeviceId: String?,
    loadingDeviceDetails: Boolean,
    activity: DeviceActivity?,
    history: List<ActivityHistoryEntry>,
    serverNow: Long,
    tamperEvents: List<TamperEvent> = emptyList(),
    browser: BrowserState? = null
) {
    val now by visibleUsageClock(serverNow)
    var windowIndex by remember { mutableIntStateOf(0) }
    val window = ActivityWindow.entries[windowIndex]
    val cutoff = now - TimeUnit.DAYS.toMillis(window.days)

    LazyColumn(
        verticalArrangement = Arrangement.spacedBy(18.dp),
        contentPadding = PaddingValues(18.dp)
    ) {
        item { GuardSectionTitle("Activity") }
        if (selectedDevice == null) {
            item {
                EmptyPanel("No laptop selected", "Select or pair a laptop to see its activity.")
            }
        } else if (loadingDeviceDetails && activity == null && history.isEmpty()) {
            item { EmptyPanel("Loading activity", "Reading laptop activity from Firebase...") }
        } else {
            item { NowOnDeviceCard(activity, now) }
            item { BrowserNowCard(browser, now) }
            item { WeeklyDigestCard(history, tamperEvents, now) }
            item { TodayBySiteCard(browser, now) }
            item {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    ActivityWindow.entries.forEachIndexed { index, entry ->
                        FilterChip(
                            selected = index == windowIndex,
                            onClick = { windowIndex = index },
                            label = { Text(entry.label) },
                            colors = FilterChipDefaults.filterChipColors(
                                selectedContainerColor = ActionBlue,
                                selectedLabelColor = Color.White
                            )
                        )
                    }
                }
            }
            val filtered = history.filter { it.startedAt >= cutoff }
            item {
                Text(
                    "History",
                    style = MaterialTheme.typography.titleMedium,
                    color = GuardNavy,
                    fontWeight = FontWeight.Bold
                )
            }
            if (filtered.isEmpty()) {
                item {
                    EmptyPanel("No history yet", "App usage sessions appear here as the laptop reports them.")
                }
            } else {
                items(filtered) { entry -> ActivityHistoryCard(entry) }
            }
        }
    }
}

@Composable
private fun NowOnDeviceCard(activity: DeviceActivity?, now: Long) {
    GuardCard {
        Text("Now on laptop", style = MaterialTheme.typography.titleMedium, color = GuardNavy, fontWeight = FontWeight.Bold)
        if (activity == null || activity.appLabel.isBlank()) {
            Text(
                "Waiting for the laptop to report its current app.",
                color = TextMuted,
                modifier = Modifier.padding(top = 8.dp)
            )
            return@GuardCard
        }
        val freshness = activityFreshness(activity.updatedAt, now)
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier.fillMaxWidth().padding(top = 12.dp)
        ) {
            Box(
                Modifier.size(52.dp).clip(RoundedCornerShape(12.dp)).background(GuardNavy),
                contentAlignment = Alignment.Center
            ) {
                Icon(Icons.Outlined.Laptop, contentDescription = null, tint = Color.White, modifier = Modifier.size(28.dp))
            }
            Spacer(Modifier.width(14.dp))
            Column(Modifier.weight(1f)) {
                Text(
                    activity.appLabel,
                    style = MaterialTheme.typography.titleLarge,
                    color = GuardNavy,
                    fontWeight = FontWeight.Bold,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                Text(
                    activity.appKey,
                    color = TextMuted,
                    style = MaterialTheme.typography.labelSmall,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
            }
            StatusPill(
                when (freshness) {
                    ActivityFreshness.LIVE -> "Live"
                    ActivityFreshness.DELAYED -> "Delayed"
                    ActivityFreshness.OFFLINE -> "Offline"
                },
                freshness == ActivityFreshness.LIVE
            )
        }
        if (activity.overlayState == "locked") {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier
                    .padding(top = 12.dp)
                    .clip(RoundedCornerShape(8.dp))
                    .background(ErrorSoft)
                    .padding(horizontal = 10.dp, vertical = 6.dp)
            ) {
                Icon(Icons.Outlined.Lock, contentDescription = null, tint = AlertRed, modifier = Modifier.size(16.dp))
                Spacer(Modifier.width(6.dp))
                Text("PIN lock wall active", color = AlertRed, style = MaterialTheme.typography.labelMedium, fontWeight = FontWeight.Bold)
            }
        }
        Row(Modifier.fillMaxWidth().padding(top = 14.dp), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            MetaTile(
                "Session",
                formatUsage((now - activity.appStartedAt).coerceAtLeast(0L)),
                null,
                Modifier.weight(1f)
            )
            MetaTile(
                "Updated",
                formatAge(activity.updatedAt, now),
                freshness == ActivityFreshness.LIVE,
                Modifier.weight(1f)
            )
        }
    }
}

@Composable
private fun BrowserNowCard(browser: BrowserState?, now: Long) {
    if (browser == null) return
    var showBrowserTabs by remember { mutableStateOf(false) }
    val fresh = now - browser.updatedAt < 5 * 60_000L
    GuardCard {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier.fillMaxWidth()
        ) {
            Text(
                "Browsing now",
                style = MaterialTheme.typography.titleMedium,
                color = GuardNavy,
                fontWeight = FontWeight.Bold,
                modifier = Modifier.weight(1f)
            )
            Text(
                browser.label ?: "Browser",
                color = TextMuted,
                style = MaterialTheme.typography.labelSmall,
                modifier = Modifier.padding(end = 8.dp)
            )
            StatusPill(if (fresh) "Live" else "Stale", ok = fresh)
        }
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier.fillMaxWidth().padding(top = 12.dp)
        ) {
            Box(
                Modifier.size(52.dp).clip(RoundedCornerShape(12.dp)).background(SurfaceTint),
                contentAlignment = Alignment.Center
            ) {
                Icon(Icons.Outlined.Public, contentDescription = null, tint = ActionBlue, modifier = Modifier.size(28.dp))
            }
            Spacer(Modifier.width(14.dp))
            Column(Modifier.weight(1f)) {
                Text(
                    browser.activeTab ?: browser.label ?: "Browsing",
                    style = MaterialTheme.typography.titleLarge,
                    color = GuardNavy,
                    fontWeight = FontWeight.Bold,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                if (!browser.activeUrl.isNullOrBlank()) {
                    Text(
                        browser.activeUrl,
                        color = TextMuted,
                        style = MaterialTheme.typography.labelSmall,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                }
            }
        }
        MetaTile(
            "Tabs open",
            "${browser.tabCount}",
            null,
            Modifier.fillMaxWidth().padding(top = 14.dp)
        )
        Row(
            Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.End
        ) {
            TextButton(onClick = { showBrowserTabs = true }) {
                Text("View all tabs", color = ActionBlue, style = MaterialTheme.typography.labelMedium)
            }
        }
    }
    if (showBrowserTabs && browser != null) {
        BrowserTabListDialog(browser = browser, onDismiss = { showBrowserTabs = false })
    }
}

@Composable
private fun TodayBySiteCard(browser: BrowserState?, now: Long) {
    if (browser == null) return
    val domains = topDomains(browser)
    if (domains.isEmpty()) return
    GuardCard {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier.fillMaxWidth()
        ) {
            Text(
                "Today by site",
                style = MaterialTheme.typography.titleMedium,
                color = GuardNavy,
                fontWeight = FontWeight.Bold,
                modifier = Modifier.weight(1f)
            )
            Text(
                "${domains.size} sites",
                style = MaterialTheme.typography.labelMedium,
                color = TextMuted,
                modifier = Modifier
                    .clip(RoundedCornerShape(8.dp))
                    .background(SurfaceTint)
                    .padding(horizontal = 10.dp, vertical = 5.dp)
            )
        }
        domains.forEach { (domain, ms) ->
            Row(Modifier.fillMaxWidth().padding(top = 6.dp), horizontalArrangement = Arrangement.SpaceBetween) {
                Text(domain, color = GuardNavy, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
                Text(formatUsage(ms), color = TextMuted, style = MaterialTheme.typography.labelSmall)
            }
        }
    }
}

private fun weekStartMillis(now: Long): Long {
    val cal = java.util.Calendar.getInstance()
    cal.timeInMillis = now
    cal.firstDayOfWeek = java.util.Calendar.MONDAY
    cal.set(java.util.Calendar.DAY_OF_WEEK, java.util.Calendar.MONDAY)
    cal.set(java.util.Calendar.HOUR_OF_DAY, 0)
    cal.set(java.util.Calendar.MINUTE, 0)
    cal.set(java.util.Calendar.SECOND, 0)
    cal.set(java.util.Calendar.MILLISECOND, 0)
    if (cal.timeInMillis > now) cal.add(java.util.Calendar.WEEK_OF_YEAR, -1)
    return cal.timeInMillis
}

@Composable
private fun WeeklyDigestCard(history: List<ActivityHistoryEntry>, tamperEvents: List<TamperEvent>, now: Long) {
    val weekStart = weekStartMillis(now)
    val weekHistory = history.filter { it.startedAt >= weekStart }
    val weekTamper = tamperEvents.count { (it.createdAt ?: 0L) >= weekStart }
    GuardCard {
        Text("This week", style = MaterialTheme.typography.titleMedium, color = GuardNavy, fontWeight = FontWeight.Bold)
        Text("Since • ${formatTimestamp(weekStart)}", color = TextMuted, style = MaterialTheme.typography.labelSmall, modifier = Modifier.padding(top = 4.dp))
        if (weekHistory.isEmpty() && weekTamper == 0) {
            Text("No activity this week yet.", color = TextMuted, modifier = Modifier.padding(top = 12.dp))
            return@GuardCard
        }
        val totalMs = weekHistory.sumOf { (it.endedAt - it.startedAt).coerceAtLeast(0L) }
        Row(Modifier.fillMaxWidth().padding(top = 12.dp), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            MetaTile("Total time", formatUsage(totalMs), null, Modifier.weight(1f))
            MetaTile("Tamper", weekTamper.toString(), null, Modifier.weight(1f))
        }
        if (weekHistory.isNotEmpty()) {
            val top = weekHistory.groupBy { it.appLabel.ifBlank { it.appKey }.ifBlank { "Unknown" } }
                .mapValues { (_, v) -> v.sumOf { (it.endedAt - it.startedAt).coerceAtLeast(0L) } }
                .entries.sortedByDescending { it.value }.take(3)
            Text("Top apps", color = GuardNavy, style = MaterialTheme.typography.labelSmall, fontWeight = FontWeight.Bold, modifier = Modifier.padding(top = 14.dp))
            top.forEach { (label, ms) ->
                Row(Modifier.fillMaxWidth().padding(top = 6.dp), horizontalArrangement = Arrangement.SpaceBetween) {
                    Text(label, color = GuardNavy, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
                    Text(formatUsage(ms), color = TextMuted, style = MaterialTheme.typography.labelSmall)
                }
            }
        }
    }
}

@Composable
private fun ActivityHistoryCard(entry: ActivityHistoryEntry) {
    GuardCard {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(
                Modifier.size(40.dp).clip(RoundedCornerShape(10.dp)).background(SurfaceTint),
                contentAlignment = Alignment.Center
            ) {
                Icon(
                    when (entry.type) {
                        "tab" -> Icons.Outlined.Public
                        "media" -> Icons.Outlined.PlayCircle
                        else -> Icons.Outlined.Timer
                    },
                    contentDescription = null,
                    tint = GuardNavy,
                    modifier = Modifier.size(22.dp)
                )
            }
            Spacer(Modifier.width(12.dp))
            Column(Modifier.weight(1f)) {
                Text(
                    entry.title ?: entry.appLabel.ifBlank { entry.appKey },
                    color = GuardNavy,
                    fontWeight = FontWeight.Bold,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                Text(
                    if (entry.type == "media") "Media in ${entry.appLabel}" else entry.appLabel.ifBlank { entry.appKey },
                    color = TextMuted,
                    style = MaterialTheme.typography.labelSmall,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                if (!entry.url.isNullOrBlank()) {
                    Text(
                        entry.url,
                        color = TextMuted,
                        style = MaterialTheme.typography.labelSmall,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                }
            }
            Column(horizontalAlignment = Alignment.End) {
                Text(formatUsage((entry.endedAt - entry.startedAt).coerceAtLeast(0L)), color = GuardNavy, style = MaterialTheme.typography.labelMedium, fontWeight = FontWeight.Bold)
                Text(formatTimestamp(entry.startedAt), color = TextMuted, style = MaterialTheme.typography.labelSmall)
            }
        }
    }
}

