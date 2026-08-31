package com.guardpulse.parentcontrol.parent

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.PlayArrow
import androidx.compose.material.icons.outlined.Public
import androidx.compose.material.icons.outlined.Tv
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import java.nio.charset.StandardCharsets
import java.util.Base64

/**
 * Laptop-only Realtime Database paths for browser tab state.
 *
 * Deliberately kept in the parent feature package (not shared/) so TV code can
 * never reference them: only Windows laptop agents write `state/browser`.
 */
internal object ParentBrowserPaths {
    const val STATE_BROWSER = "state/browser"

    fun deviceStateBrowser(deviceId: String) = "devices/$deviceId/state/browser"
}

data class BrowserTabInfo(
    val title: String,
    val url: String? = null
)

data class BrowserState(
    val browser: String,
    val label: String? = null,
    val activeTab: String? = null,
    val activeUrl: String? = null,
    val tabCount: Int = 0,
    val tabs: List<BrowserTabInfo> = emptyList(),
    val domainsToday: Map<String, Long> = emptyMap(),
    val updatedAt: Long = 0L
) {
    companion object {
        /**
         * Defensive parser for the `devices/{deviceId}/state/browser` node.
         * Never throws; tolerates missing or wrongly-typed fields. Returns
         * null only when no browser executable is present (nothing to show).
         */
        fun fromMap(map: Map<*, *>): BrowserState? {
            return try {
                val browser = stringField(map, "browser") ?: return null
                BrowserState(
                    browser = browser,
                    label = stringField(map, "label"),
                    activeTab = stringField(map, "activeTab"),
                    activeUrl = stringField(map, "activeUrl"),
                    tabCount = (longField(map, "tabCount") ?: 0L).toInt().coerceAtLeast(0),
                    tabs = tabsFromMap(map),
                    domainsToday = domainsTodayFromMap(map),
                    updatedAt = longField(map, "updatedAt") ?: 0L
                )
            } catch (_: Exception) {
                null
            }
        }
    }
}

/**
 * Decodes a `domainsToday` key (BASE64URL of the domain, because RTDB keys
 * cannot contain '.'). Falls back to the raw key on any failure or when the
 * decoded bytes are not printable text.
 */
internal fun decodeDomainKey(key: String): String {
    if (key.isEmpty()) return key
    return try {
        val normalized = key.replace('-', '+').replace('_', '/')
        val padded = normalized + "=".repeat((4 - normalized.length % 4) % 4)
        val decoded = String(Base64.getDecoder().decode(padded), StandardCharsets.UTF_8)
        if (decoded.any { it.isISOControl() }) key else decoded
    } catch (_: Exception) {
        key
    }
}

/** Top sites by screen-time today, sorted by milliseconds descending. */
internal fun topDomains(browser: BrowserState, limit: Int = 8): List<Pair<String, Long>> {
    if (limit <= 0) return emptyList()
    val merged = LinkedHashMap<String, Long>()
    browser.domainsToday.forEach { (key, ms) ->
        val domain = decodeDomainKey(key)
        merged[domain] = (merged[domain] ?: 0L) + ms
    }
    return merged.entries
        .sortedByDescending { it.value }
        .take(limit)
        .map { it.key to it.value }
}

/** True when the given package is the laptop's current browser executable. */
internal fun isBrowserApp(packageName: String, browser: BrowserState?): Boolean {
    val current = browser ?: return false
    return packageName.equals(current.browser, ignoreCase = true)
}

@Composable
internal fun BrowserNowLines(browser: BrowserState, modifier: Modifier = Modifier) {
    Column(modifier = modifier, verticalArrangement = Arrangement.spacedBy(2.dp)) {
        Text(
            "On: ${browser.activeTab ?: browser.label ?: "Unknown"}",
            color = TextMuted,
            style = MaterialTheme.typography.labelSmall,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis
        )
        Text(
            if (browser.tabCount == 1) "1 tab open" else "${browser.tabCount} tabs open",
            color = TextMuted,
            style = MaterialTheme.typography.labelSmall,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis
        )
    }
}

/** A tab as displayed in the dialog: active entry first, then the reported rest. */
private data class DisplayTab(val title: String, val url: String?, val isActive: Boolean)

/**
 * Builds the display list for the tab dialog.
 *
 * The active tab is always entry #1, sourced from the snapshot's top-level
 * [BrowserState.activeTab]/[BrowserState.activeUrl] — those are the
 * authoritative fields, and the agent does not guarantee the active tab is
 * present in `tabs` (browser UIA strips truncate long titles; the array caps
 * at 25). Remaining reported tabs follow, skipping any that duplicate the
 * active entry by title or URL so it never appears twice.
 */
private fun browserDisplayList(browser: BrowserState): List<DisplayTab> {
    val result = ArrayList<DisplayTab>()
    val activeTitle = browser.activeTab
    val activeUrl = browser.activeUrl?.takeIf { it.isNotBlank() }
    if (!activeTitle.isNullOrBlank()) {
        result += DisplayTab(activeTitle, activeUrl, true)
    }
    browser.tabs.forEach { tab ->
        val isActive = (activeTitle != null && tab.title == activeTitle) ||
            (activeUrl != null && tab.url != null && tab.url == activeUrl)
        if (!isActive) {
            result += DisplayTab(tab.title, tab.url, false)
        }
    }
    return result
}

@Composable
internal fun BrowserTabListDialog(browser: BrowserState, onDismiss: () -> Unit) {
    val domains = remember(browser) { topDomains(browser) }
    val displayTabs = remember(browser) { browserDisplayList(browser) }
    AlertDialog(
        onDismissRequest = onDismiss,
        containerColor = SurfaceCard,
        title = {
            Column {
                Text(
                    "${browser.label ?: "Browser"} — tabs",
                    color = GuardNavy,
                    style = MaterialTheme.typography.titleLarge,
                    fontWeight = FontWeight.Bold,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                Text(
                    "${browser.tabCount} tabs open",
                    color = TextMuted,
                    style = MaterialTheme.typography.labelSmall
                )
            }
        },
        text = {
            Column(
                modifier = Modifier.verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(10.dp)
            ) {
                if (displayTabs.isEmpty()) {
                    Text(
                        "No tabs reported yet.",
                        color = TextMuted,
                        style = MaterialTheme.typography.bodySmall
                    )
                }
                displayTabs.forEachIndexed { index, tab ->
                    Row(verticalAlignment = Alignment.Top) {
                        Text(
                            "${index + 1}.",
                            color = GuardNavy,
                            style = MaterialTheme.typography.bodyMedium,
                            fontWeight = FontWeight.Bold,
                            modifier = Modifier.width(28.dp)
                        )
                        Column(Modifier.weight(1f)) {
                            Text(
                                tab.title,
                                color = GuardNavy,
                                style = MaterialTheme.typography.bodyMedium,
                                fontWeight = if (tab.isActive) FontWeight.Bold else FontWeight.Medium
                            )
                            tab.url?.let {
                                Text(
                                    it,
                                    color = TextMuted,
                                    style = MaterialTheme.typography.labelSmall
                                )
                            }
                        }
                        if (tab.isActive) {
                            Spacer(Modifier.width(8.dp))
                            StatusLabel("Active", ActionBlue)
                        }
                    }
                }
                Text(
                    "Today by site",
                    color = GuardNavy,
                    style = MaterialTheme.typography.titleSmall,
                    fontWeight = FontWeight.Bold,
                    modifier = Modifier.padding(top = 6.dp)
                )
                if (domains.isEmpty()) {
                    Text(
                        "No site usage recorded yet.",
                        color = TextMuted,
                        style = MaterialTheme.typography.bodySmall
                    )
                } else {
                    domains.forEach { (domain, ms) ->
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Text(
                                domain,
                                color = GuardNavy,
                                style = MaterialTheme.typography.bodyMedium,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                                modifier = Modifier.weight(1f)
                            )
                            Spacer(Modifier.width(8.dp))
                            Text(
                                formatTabDuration(ms),
                                color = TextMuted,
                                style = MaterialTheme.typography.labelSmall
                            )
                        }
                    }
                }
            }
        },
        confirmButton = {
            TextButton(onClick = onDismiss) {
                Text("Close", color = ActionBlue)
            }
        }
    )
}

@Composable
internal fun BrowserTimelineIcon(type: String): ImageVector = when (type) {
    "tab" -> Icons.Outlined.Public
    "media" -> Icons.Outlined.PlayArrow
    else -> Icons.Outlined.Tv
}

private fun stringField(map: Map<*, *>, key: String): String? =
    (map[key] as? String)?.trim()?.takeIf { it.isNotEmpty() }

private fun longField(map: Map<*, *>, key: String): Long? = when (val raw = map[key]) {
    is Number -> raw.toLong()
    is String -> raw.trim().toLongOrNull()
    else -> null
}

private fun tabsFromMap(map: Map<*, *>): List<BrowserTabInfo> {
    val raw = map["tabs"] as? List<*> ?: return emptyList()
    return raw.mapNotNull { entry ->
        when (entry) {
            is Map<*, *> -> BrowserTabInfo(
                title = stringField(entry, "title") ?: "Untitled",
                url = stringField(entry, "url")
            )
            is String -> BrowserTabInfo(title = entry)
            else -> null
        }
    }
}

private fun domainsTodayFromMap(map: Map<*, *>): Map<String, Long> {
    val raw = map["domainsToday"] as? Map<*, *> ?: return emptyMap()
    val result = LinkedHashMap<String, Long>()
    raw.forEach { (key, value) ->
        val domainKey = key as? String ?: return@forEach
        val ms = when (value) {
            is Number -> value.toLong()
            is String -> value.toLongOrNull()
            else -> null
        } ?: return@forEach
        if (ms > 0L) result[domainKey] = ms
    }
    return result
}

private fun formatTabDuration(ms: Long): String {
    val totalSeconds = ms.coerceAtLeast(0L) / 1_000L
    val hours = totalSeconds / 3_600L
    val minutes = (totalSeconds % 3_600L) / 60L
    val seconds = totalSeconds % 60L
    return when {
        hours > 0L -> "${hours}h ${minutes}m"
        minutes > 0L -> "${minutes}m ${seconds}s"
        else -> "${seconds}s"
    }
}
