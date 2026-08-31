package com.guardpulse.parentcontrol.shared

import com.google.firebase.database.DataSnapshot

data class ControlAppRule(
    val packageName: String,
    val manualBlocked: Boolean = false,
    val dailyLimitMinutes: Int? = null,
    val updatedAt: Long? = null
)

data class ControlMode(
    val modeId: String,
    val name: String,
    val apps: Map<String, ControlAppRule> = emptyMap(),
    val createdAt: Long? = null,
    val updatedAt: Long? = null
)

data class ControlActiveMode(
    val modeId: String,
    val modeName: String? = null,
    val activatedAt: Long? = null
)

data class ControlSafeMode(
    val enabled: Boolean = false,
    val until: Long = 0L,
    val startedAt: Long? = null,
    val startedBy: String? = null
)

data class ControlPin(
    val salt: String,
    val hash: String,
    val version: Int = PinHasher.LEGACY_VERSION,
    val algorithm: String? = null,
    val iterations: Int? = null,
    val updatedAt: Long? = null
)

data class ControlSchedule(
    val enabled: Boolean = false,
    val startMinute: Int = 0,
    val endMinute: Int = 0
) {
    fun toFirebaseMap(): Map<String, Any?> = mapOf(
        "enabled" to enabled,
        "startMinute" to startMinute,
        "endMinute" to endMinute
    )
}

data class ControlBudget(
    val dailyLimitMinutes: Int
) {
    fun toFirebaseMap(): Map<String, Any?> = mapOf(
        "dailyLimitMinutes" to dailyLimitMinutes
    )
}

data class ControlContentFilter(
    val social: Boolean = false,
    val gambling: Boolean = false,
    val adult: Boolean = false,
    val gaming: Boolean = false
) {
    fun toFirebaseMap(): Map<String, Any?> = mapOf(
        "social" to social,
        "gambling" to gambling,
        "adult" to adult,
        "gaming" to gaming
    )
}

data class ControlAllowlist(
    val enabled: Boolean = false
) {
    fun toFirebaseMap(): Map<String, Any?> = mapOf(
        "enabled" to enabled
    )
}

data class ControlCustomBlockedDomains(
    val domains: List<String>
)

data class ControlSnapshotV2(
    val revisionId: String,
    val updatedAt: Long? = null,
    val updatedBy: String? = null,
    val apps: Map<String, ControlAppRule> = emptyMap(),
    val modes: Map<String, ControlMode> = emptyMap(),
    val activeMode: ControlActiveMode? = null,
    val safeMode: ControlSafeMode = ControlSafeMode(),
    val pin: ControlPin? = null,
    val schedule: ControlSchedule? = null,
    val budget: ControlBudget? = null,
    val contentFilter: ControlContentFilter? = null,
    val allowlist: ControlAllowlist? = null,
    val customBlockedDomains: ControlCustomBlockedDomains? = null
) {
    fun effectiveApps(): Map<String, ControlAppRule> {
        val selectedMode = activeMode?.let { modes[it.modeId] }
        val result = (selectedMode?.apps ?: apps).toMutableMap()
        PolicyConstants.defaultLockedPackages.forEach { packageName ->
            result.putIfAbsent(packageName, ControlAppRule(packageName, manualBlocked = true))
        }
        return result
    }

    fun toFirebaseMap(): Map<String, Any?> = buildMap {
        put("schemaVersion", PolicyConstants.SYNC_PROTOCOL_VERSION)
        put("revisionId", revisionId)
        put("updatedAt", updatedAt)
        put("updatedBy", updatedBy)
        put("apps", apps.mapKeys { PackageKeys.encode(it.key) }
            .mapValues { (packageKey, value) -> value.toFirebaseMap(packageKey) })
        put("modes", modes.mapValues { it.value.toFirebaseMap() })
        put("activeMode", activeMode?.toFirebaseMap())
        put("safeMode", safeMode.toFirebaseMap())
        put("pin", pin?.toFirebaseMap())
        // Optional laptop controls: absent stays absent (null here would delete the node).
        schedule?.let { put("schedule", it.toFirebaseMap()) }
        budget?.let { put("budget", it.toFirebaseMap()) }
        contentFilter?.let { put("contentFilter", it.toFirebaseMap()) }
        allowlist?.let { put("allowlist", it.toFirebaseMap()) }
        customBlockedDomains?.let { put("customBlockedDomains", it.domains) }
    }
}

data class SyncDesiredRevision(
    val revisionId: String,
    val kind: String,
    val target: String? = null,
    val requestedAt: Long? = null,
    val requestedBy: String? = null
)

data class SyncAppliedRevision(
    val revisionId: String? = null,
    val status: String? = null,
    val appliedAt: Long? = null,
    val sessionId: String? = null,
    val error: String? = null
)

data class SyncRuntimeState(
    val connected: Boolean = false,
    val sessionId: String? = null,
    val protocolVersion: Int = 0,
    val connectedAt: Long? = null,
    val lastPolicyReceivedAt: Long? = null,
    val lastPolicyAppliedAt: Long? = null,
    val lastStateWriteAt: Long? = null,
    val lastUsageWriteAt: Long? = null,
    val lastHeartbeatWriteAt: Long? = null,
    val lastInventoryWriteAt: Long? = null,
    val lastHealthWriteAt: Long? = null,
    val lastCommandWriteAt: Long? = null,
    val lastUnlockWriteAt: Long? = null,
    val lastTamperWriteAt: Long? = null,
    val lastSuccessAt: Long? = null,
    val lastFailedChannel: String? = null,
    val lastError: String? = null,
    val lastErrorAt: Long? = null,
    val inventoryRevision: String? = null
)

enum class DeviceFreshness { LIVE, DELAYED, OFFLINE }

object ControlProtocol {
    private val revisionKinds = setOf(
        PolicyConstants.REVISION_APP_POLICY,
        PolicyConstants.REVISION_MODE_CREATE,
        PolicyConstants.REVISION_MODE_UPDATE,
        PolicyConstants.REVISION_MODE_DELETE,
        PolicyConstants.REVISION_MODE_POLICY,
        PolicyConstants.REVISION_ACTIVE_MODE,
        PolicyConstants.REVISION_SAFE_MODE,
        PolicyConstants.REVISION_PIN,
        PolicyConstants.REVISION_MIGRATION,
        PolicyConstants.REVISION_SCHEDULE,
        PolicyConstants.REVISION_BUDGET,
        PolicyConstants.REVISION_CONTENT_FILTER,
        PolicyConstants.REVISION_ALLOWLIST,
        PolicyConstants.REVISION_CUSTOM_DOMAINS
    )

    fun parse(snapshot: DataSnapshot): Result<ControlSnapshotV2> = runCatching {
        require(snapshot.exists()) { "V2 control snapshot is missing" }
        val schemaVersion = snapshot.child("schemaVersion").getValue(Long::class.java)?.toInt()
        require(schemaVersion == PolicyConstants.SYNC_PROTOCOL_VERSION) {
            "Unsupported control schema: ${schemaVersion ?: "missing"}"
        }
        val revisionId = snapshot.child("revisionId").getValue(String::class.java)
            ?.takeIf { it.isNotBlank() }
            ?: error("Control revision is missing")
        val apps = parseApps(snapshot.child("apps"))
        // Lenient structural nodes: a malformed mode/activeMode/safeMode/pin must NOT poison the
        // whole snapshot. We drop the offending node to its safe default instead of throwing, which
        // would otherwise mark the entire control tree INVALID and freeze the Security tab.
        val modes = buildMap<String, ControlMode> {
            for (modeSnapshot in snapshot.child("modes").children) {
                try {
                    val encodedModeId = modeSnapshot.key ?: continue
                    val modeId = modeSnapshot.child("modeId").getValue(String::class.java) ?: encodedModeId
                    require(modeId.isNotBlank()) { "Mode ID is blank" }
                    require(modeId == encodedModeId) { "Mode key does not match mode ID" }
                    val name = modeSnapshot.child("name").getValue(String::class.java)
                        ?.trim()
                        ?.takeIf { it.isNotEmpty() }
                        ?: continue
                    put(
                        modeId,
                        ControlMode(
                            modeId = modeId,
                            name = name,
                            apps = parseApps(modeSnapshot.child("apps")),
                            createdAt = modeSnapshot.child("createdAt").getValue(Long::class.java),
                            updatedAt = modeSnapshot.child("updatedAt").getValue(Long::class.java)
                        )
                    )
                } catch (e: Exception) {
                    println("GuardPulse: control/v2 dropped malformed mode: ${e.message}")
                }
            }
        }
        val activeMode: ControlActiveMode? = try {
            val activeModeSnapshot = snapshot.child("activeMode")
            if (!activeModeSnapshot.exists()) {
                null
            } else {
                val modeId = activeModeSnapshot.child("modeId").getValue(String::class.java)?.takeIf { it.isNotBlank() }
                if (modeId == null || !modes.containsKey(modeId)) {
                    null
                } else {
                    ControlActiveMode(
                        modeId = modeId,
                        modeName = activeModeSnapshot.child("modeName").getValue(String::class.java),
                        activatedAt = activeModeSnapshot.child("activatedAt").getValue(Long::class.java)
                    )
                }
            }
        } catch (e: Exception) {
            println("GuardPulse: control/v2 dropped malformed activeMode: ${e.message}")
            null
        }
        val safeMode: ControlSafeMode = try {
            val safeModeSnapshot = snapshot.child("safeMode")
            if (!safeModeSnapshot.exists()) {
                ControlSafeMode()
            } else {
                val safeModeEnabled = safeModeSnapshot.child("enabled").getValue(Boolean::class.java)
                val safeModeUntil = safeModeSnapshot.child("until").getValue(Long::class.java)
                if (safeModeEnabled == null || safeModeUntil == null) {
                    ControlSafeMode()
                } else {
                    val safeMode = ControlSafeMode(
                        enabled = safeModeEnabled,
                        until = safeModeUntil,
                        startedAt = safeModeSnapshot.child("startedAt").getValue(Long::class.java),
                        startedBy = safeModeSnapshot.child("startedBy").getValue(String::class.java)
                    )
                    if (safeMode.enabled && safeMode.until <= 0L) ControlSafeMode() else safeMode
                }
            }
        } catch (e: Exception) {
            println("GuardPulse: control/v2 dropped malformed safeMode: ${e.message}")
            ControlSafeMode()
        }
        val pin: ControlPin? = try {
            val pinSnapshot = snapshot.child("pin")
            if (!pinSnapshot.exists()) {
                null
            } else {
                val salt = pinSnapshot.child("salt").getValue(String::class.java)?.takeIf { it.isNotBlank() }
                val hash = pinSnapshot.child("hash").getValue(String::class.java)?.takeIf { it.isNotBlank() }
                if (salt == null || hash == null) {
                    null
                } else {
                    val version = pinSnapshot.child("version").getValue(Long::class.java)?.toInt() ?: PinHasher.LEGACY_VERSION
                    val algorithm = pinSnapshot.child("algorithm").getValue(String::class.java)
                    val iterations = pinSnapshot.child("iterations").getValue(Long::class.java)?.toInt()
                    if (version != PinHasher.LEGACY_VERSION && version != PinHasher.CURRENT_VERSION) {
                        null
                    } else if (version == PinHasher.CURRENT_VERSION) {
                        if (algorithm != PinHasher.CURRENT_ALGORITHM || iterations == null
                            || iterations !in PinHasher.CURRENT_ITERATIONS..1_000_000) {
                            null
                        } else {
                            ControlPin(
                                salt = salt, hash = hash, version = version,
                                algorithm = algorithm, iterations = iterations,
                                updatedAt = pinSnapshot.child("updatedAt").getValue(Long::class.java)
                            )
                        }
                    } else {
                        ControlPin(
                            salt = salt, hash = hash, version = version,
                            algorithm = algorithm, iterations = iterations,
                            updatedAt = pinSnapshot.child("updatedAt").getValue(Long::class.java)
                        )
                    }
                }
            }
        } catch (e: Exception) {
            println("GuardPulse: control/v2 dropped malformed pin: ${e.message}")
            null
        }
        val schedule: ControlSchedule? = try {
            if (!snapshot.child("schedule").exists()) null else {
                val scheduleSnapshot = snapshot.child("schedule")
                val enabled = scheduleSnapshot.child("enabled").getValue(Boolean::class.java)
                    ?: error("Schedule enabled flag is missing")
                val startMinute = scheduleSnapshot.child("startMinute").getValue(Long::class.java)
                val endMinute = scheduleSnapshot.child("endMinute").getValue(Long::class.java)
                require(startMinute != null && endMinute != null) { "Schedule window is missing" }
                require(startMinute in 0L..1439L && endMinute in 0L..1439L) { "Schedule window is out of range" }
                ControlSchedule(enabled, startMinute.toInt(), endMinute.toInt())
            }
        } catch (_: Exception) { null }
        val budget: ControlBudget? = try {
            if (!snapshot.child("budget").exists()) null else {
                val budgetSnapshot = snapshot.child("budget")
                val minutes = budgetSnapshot.child("dailyLimitMinutes").getValue(Long::class.java)
                    ?: error("Budget limit is missing")
                require(minutes in 1L..1440L) { "Budget limit is out of range" }
                ControlBudget(minutes.toInt())
            }
        } catch (_: Exception) { null }
        val contentFilter: ControlContentFilter? = try {
            if (!snapshot.child("contentFilter").exists()) null else {
                val contentFilterSnapshot = snapshot.child("contentFilter")
                fun category(name: String, label: String): Boolean {
                    val child = contentFilterSnapshot.child(name)
                    return if (!child.exists()) false
                    else child.getValue(Boolean::class.java) ?: error("Content filter $label flag is invalid")
                }
                ControlContentFilter(
                    social = category("social", "social"),
                    gambling = category("gambling", "gambling"),
                    adult = category("adult", "adult"),
                    gaming = category("gaming", "gaming")
                )
            }
        } catch (_: Exception) { null }
        val allowlist: ControlAllowlist? = try {
            if (!snapshot.child("allowlist").exists()) null else {
                ControlAllowlist(
                    enabled = snapshot.child("allowlist").child("enabled").getValue(Boolean::class.java)
                        ?: error("Allowlist enabled flag is missing")
                )
            }
        } catch (_: Exception) { null }
        val customSnapshot = snapshot.child("customBlockedDomains")
        val customBlockedDomains = if (customSnapshot.exists()) {
            val rawChildren = customSnapshot.children.sortedBy { it.key?.toIntOrNull() ?: Int.MAX_VALUE }
            val seen = LinkedHashSet<String>()
            for (child in rawChildren) {
                val rawItem = child.getValue(String::class.java) ?: continue
                if (rawItem.isBlank()) continue
                val normalized = normalizeCustomDomainWithPath(rawItem) ?: continue
                seen.add(normalized)
                if (seen.size >= 100) break
            }
            if (seen.isEmpty()) null else ControlCustomBlockedDomains(seen.toList())
        } else {
            null
        }
        ControlSnapshotV2(
            revisionId = revisionId,
            updatedAt = snapshot.child("updatedAt").getValue(Long::class.java),
            updatedBy = snapshot.child("updatedBy").getValue(String::class.java),
            apps = apps,
            modes = modes,
            activeMode = activeMode,
            safeMode = safeMode,
            pin = pin,
            schedule = schedule,
            budget = budget,
            contentFilter = contentFilter,
            allowlist = allowlist,
            customBlockedDomains = customBlockedDomains
        )
    }

    fun parseDesired(snapshot: DataSnapshot): SyncDesiredRevision? {
        val revisionId = snapshot.child("revisionId").getValue(String::class.java)
            ?.takeIf { it.isNotBlank() }
            ?: return null
        val kind = snapshot.child("kind").getValue(String::class.java)
            ?.takeIf { it in revisionKinds }
            ?: return null
        return SyncDesiredRevision(
            revisionId = revisionId,
            kind = kind,
            target = snapshot.child("target").getValue(String::class.java),
            requestedAt = snapshot.child("requestedAt").getValue(Long::class.java),
            requestedBy = snapshot.child("requestedBy").getValue(String::class.java)
        )
    }

    fun freshness(connected: Boolean, lastSeen: Long?, now: Long): DeviceFreshness {
        val age = lastSeen?.let { (now - it).coerceAtLeast(0L) } ?: Long.MAX_VALUE
        return when {
            !connected || age > 90_000L -> DeviceFreshness.OFFLINE
            age > 45_000L -> DeviceFreshness.DELAYED
            else -> DeviceFreshness.LIVE
        }
    }

    private fun parseApps(snapshot: DataSnapshot): Map<String, ControlAppRule> {
        val result = mutableMapOf<String, ControlAppRule>()
        for (appSnapshot in snapshot.children) {
            try {
                val encodedKey = appSnapshot.key ?: continue
                val packageName = appSnapshot.child("packageName").getValue(String::class.java) ?: continue
                if (PackageKeys.encode(packageName) != encodedKey) continue
                val manualBlocked = appSnapshot.child("manualBlocked").getValue(Boolean::class.java) ?: continue
                val limitSnapshot = appSnapshot.child("dailyLimitMinutes")
                val limit = if (limitSnapshot.exists()) {
                    val raw = limitSnapshot.getValue(Long::class.java) ?: continue
                    if (raw !in 1L..1440L) continue
                    raw.toInt()
                } else null
                result[packageName] = ControlAppRule(
                    packageName = packageName,
                    manualBlocked = manualBlocked,
                    dailyLimitMinutes = limit,
                    updatedAt = appSnapshot.child("updatedAt").getValue(Long::class.java)
                )
            } catch (_: Exception) { continue }
        }
        return result
    }
}

private fun normalizeCustomDomain(raw: String): String? {
    var s = raw.trim().lowercase()
    if (s.startsWith("http://")) s = s.substring(7)
    else if (s.startsWith("https://")) s = s.substring(8)
    val slash = s.indexOf('/')
    if (slash >= 0) s = s.substring(0, slash)
    s = s.trimEnd('.')
    if (s.isEmpty() || s.length > 253 || s.contains("..") || s.startsWith("-") || s.startsWith(".")) return null
    val labels = s.split(".")
    if (labels.size < 2) return null
    for (label in labels) {
        if (label.isEmpty() || label.length > 63 || label.startsWith("-") || label.endsWith("-")) return null
        for (ch in label) if (!(ch in 'a'..'z' || ch in '0'..'9' || ch == '-')) return null
    }
    val tld = labels.last()
    if (tld.length < 2 || tld.any { it !in 'a'..'z' }) return null
    return s
}

/**
 * Path-preserving variant for customBlockedDomains entries.
 * Mirrors ParentSecurityFeature.kt normalizeCustomDomainForUi: validates the
 * domain labels, then if a '/' is present validates trailing path chars
 * (a-z 0-9 / - _ . ? = &) and retains the full "domain[/path]" string.
 * Kept alongside normalizeCustomDomain for any non-path callers.
 */
private fun normalizeCustomDomainWithPath(raw: String): String? {
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

private fun ControlAppRule.toFirebaseMap(packageKey: String): Map<String, Any?> = mapOf(
    "packageKey" to packageKey,
    "packageName" to packageName,
    "manualBlocked" to manualBlocked,
    "dailyLimitMinutes" to dailyLimitMinutes,
    "updatedAt" to updatedAt
)

private fun ControlMode.toFirebaseMap(): Map<String, Any?> = mapOf(
    "modeId" to modeId,
    "name" to name,
    "createdAt" to createdAt,
    "updatedAt" to updatedAt,
    "apps" to apps.mapKeys { PackageKeys.encode(it.key) }
        .mapValues { (packageKey, value) -> value.toFirebaseMap(packageKey) }
)

private fun ControlActiveMode.toFirebaseMap(): Map<String, Any?> = mapOf(
    "modeId" to modeId,
    "modeName" to modeName,
    "activatedAt" to activatedAt
)

private fun ControlSafeMode.toFirebaseMap(): Map<String, Any?> = mapOf(
    "enabled" to enabled,
    "until" to until,
    "startedAt" to startedAt,
    "startedBy" to startedBy
)

private fun ControlPin.toFirebaseMap(): Map<String, Any?> = mapOf(
    "salt" to salt,
    "hash" to hash,
    "version" to version,
    "algorithm" to algorithm,
    "iterations" to iterations,
    "updatedAt" to updatedAt
)
