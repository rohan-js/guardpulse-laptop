package com.guardpulse.parentcontrol.shared

object PolicyConstants {
    data class SettingsSectionPolicy(
        val packageName: String,
        val label: String,
        val shortLabel: String,
        val key: String
    )

    const val TV_PACKAGE = "com.guardpulse.parentcontrol.tv"
    const val PARENT_PACKAGE = "com.guardpulse.parentcontrol.parent"
    const val SETTINGS_APPS_PACKAGE = "com.guardpulse.policy.settings_apps"
    const val SETTINGS_DEVELOPER_OPTIONS_PACKAGE = "com.guardpulse.policy.settings_developer_options"
    const val SETTINGS_SECURITY_RESTRICTIONS_PACKAGE = "com.guardpulse.policy.settings_security_restrictions"
    const val SETTINGS_ACCESSIBILITY_PACKAGE = "com.guardpulse.policy.settings_accessibility"
    const val SETTINGS_RESET_PACKAGE = "com.guardpulse.policy.settings_reset"
    const val DEPRECATED_SETTINGS_SECTIONS_PACKAGE = "com.guardpulse.policy.settings_sections"

    const val COMMAND_RESCAN_APPS = "rescanApps"
    const val COMMAND_RESET_TODAY = "resetToday"
    const val COMMAND_UNPAIR = "unpair"
    const val COMMAND_OPEN_SETUP = "openSetup"

    const val PLATFORM_ANDROID_TV = "androidTv"
    const val PLATFORM_WINDOWS = "windows"

    const val WINDOWS_TASK_MANAGER_PACKAGE = "guardpulse.windows.taskmgr"
    const val WINDOWS_COMMAND_LINE_PACKAGE = "guardpulse.windows.commandline"
    const val WINDOWS_REGISTRY_EDITOR_PACKAGE = "guardpulse.windows.registry"
    const val WINDOWS_SETTINGS_PACKAGE = "guardpulse.windows.settings"
    const val WINDOWS_INSTALLERS_PACKAGE = "guardpulse.windows.installers"

    const val BLOCK_REASON_MANUAL = "manual"
    const val BLOCK_REASON_DAILY_LIMIT = "dailyLimit"
    const val BLOCK_REASON_RISKY_SETTINGS = "riskySettings"
    const val BLOCK_REASON_NETWORK_FILTER_MISSING = "networkFilterMissing"
    const val BLOCK_REASON_SOURCE_LOCK = "sourceLock"
    const val BLOCK_REASON_SETTINGS_SECTION = "settingsSection"
    const val BLOCK_REASON_SCHEDULE = "schedule"
    const val BLOCK_REASON_BUDGET = "budget"
    const val BLOCK_REASON_NOT_APPROVED = "notApproved"

    const val ENFORCEMENT_DEVICE_OWNER = "deviceOwner"
    const val ENFORCEMENT_FALLBACK = "fallback"
    const val ENFORCEMENT_UNPROTECTED = "unprotected"

    const val UNLOCK_PENDING = "pending"
    const val UNLOCK_APPROVED = "approved"
    const val UNLOCK_DENIED = "denied"
    const val UNLOCK_EXPIRED = "expired"
    const val UNLOCK_APPROVAL_ONE_VISIT = "oneVisit"
    const val UNLOCK_APPROVAL_TIMED = "timed"

    const val SYNC_PROTOCOL_VERSION = 2
    const val SYNC_STATUS_APPLIED = "applied"
    const val SYNC_STATUS_FAILED = "failed"
    const val COMMAND_PENDING = "pending"
    const val COMMAND_RUNNING = "running"
    const val COMMAND_DONE = "done"
    const val COMMAND_FAILED = "failed"
    const val COMMAND_EXPIRED = "expired"

    const val PAIR_PENDING = "pending"
    const val PAIR_ACCEPTED = "accepted"
    const val PAIR_REJECTED = "rejected"
    const val PAIR_EXPIRED = "expired"
    const val PAIR_FAILED = "failed"

    const val REVISION_APP_POLICY = "appPolicy"
    const val REVISION_MODE_CREATE = "modeCreate"
    const val REVISION_MODE_UPDATE = "modeUpdate"
    const val REVISION_MODE_DELETE = "modeDelete"
    const val REVISION_MODE_POLICY = "modePolicy"
    const val REVISION_ACTIVE_MODE = "activeMode"
    const val REVISION_SAFE_MODE = "safeMode"
    const val REVISION_PIN = "pin"
    const val REVISION_MIGRATION = "migration"
    const val REVISION_SCHEDULE = "schedule"
    const val REVISION_BUDGET = "budget"
    const val REVISION_CONTENT_FILTER = "contentFilter"
    const val REVISION_ALLOWLIST = "allowlist"
    const val REVISION_CUSTOM_DOMAINS = "customBlockedDomains"

    const val TAMPER_ADMIN_DISABLE_REQUESTED = "adminDisableRequested"
    const val TAMPER_ADMIN_DISABLED = "adminDisabled"
    const val TAMPER_ACCESSIBILITY_DISABLED = "accessibilityDisabled"
    const val TAMPER_USAGE_ACCESS_MISSING = "usageAccessMissing"
    const val TAMPER_VPN_DISABLED = "vpnDisabled"
    const val TAMPER_RISKY_SETTINGS_OPENED = "riskySettingsOpened"
    const val TAMPER_PIN_RETRY_LOCKED = "pinRetryLocked"

    const val HEARTBEAT_INTERVAL_MS = 30_000L
    const val USAGE_SCAN_INTERVAL_MS = 60_000L
    const val FOREGROUND_USAGE_UPLOAD_INTERVAL_MS = 10_000L
    const val FOREGROUND_USAGE_EXTRAPOLATION_MAX_MS = 20_000L
    const val PAIRING_TTL_MS = 10 * 60_000L
    const val TEMP_UNLOCK_MS = 10 * 60_000L
    const val UNLOCK_15_MINUTES_MS = 15 * 60_000L
    const val UNLOCK_30_MINUTES_MS = 30 * 60_000L
    const val SAFE_MODE_DURATION_MS = 30 * 60_000L
    const val TAMPER_EVENT_THROTTLE_MS = 15 * 60_000L
    const val COMMAND_OPEN_SETUP_TTL_MS = 60_000L
    const val COMMAND_STANDARD_TTL_MS = 5 * 60_000L
    const val COMMAND_UNPAIR_TTL_MS = 10 * 60_000L

    fun commandTtlMs(type: String): Long = when (type) {
        COMMAND_OPEN_SETUP -> COMMAND_OPEN_SETUP_TTL_MS
        COMMAND_UNPAIR -> COMMAND_UNPAIR_TTL_MS
        else -> COMMAND_STANDARD_TTL_MS
    }

    val sourceLockPackages = setOf(
        "com.android.tv"
    )

    val sourceLockRuntimePackages = sourceLockPackages + setOf(
        "com.droidlogic.tvinput"
    )

    val primarySettingsPackages = setOf(
        "com.android.tv.settings",
        "com.android.settings"
    )

    val settingsSectionPolicies = listOf(
        SettingsSectionPolicy(
            packageName = SETTINGS_APPS_PACKAGE,
            label = "Settings: Apps",
            shortLabel = "Apps",
            key = "settings-apps"
        ),
        SettingsSectionPolicy(
            packageName = SETTINGS_DEVELOPER_OPTIONS_PACKAGE,
            label = "Settings: Developer options",
            shortLabel = "Developer options",
            key = "settings-developer-options"
        ),
        SettingsSectionPolicy(
            packageName = SETTINGS_SECURITY_RESTRICTIONS_PACKAGE,
            label = "Settings: Security & restrictions",
            shortLabel = "Security & restrictions",
            key = "settings-security-restrictions"
        ),
        SettingsSectionPolicy(
            packageName = SETTINGS_ACCESSIBILITY_PACKAGE,
            label = "Settings: Accessibility",
            shortLabel = "Accessibility",
            key = "settings-accessibility"
        ),
        SettingsSectionPolicy(
            packageName = SETTINGS_RESET_PACKAGE,
            label = "Settings: Reset",
            shortLabel = "Reset",
            key = "settings-reset"
        )
    )

    val settingsSectionLockPackages = settingsSectionPolicies.map { it.packageName }.toSet()

    val windowsBypassPackages = setOf(
        WINDOWS_TASK_MANAGER_PACKAGE,
        WINDOWS_COMMAND_LINE_PACKAGE,
        WINDOWS_REGISTRY_EDITOR_PACKAGE,
        WINDOWS_SETTINGS_PACKAGE,
        WINDOWS_INSTALLERS_PACKAGE
    )

    val virtualPolicyPackages = settingsSectionLockPackages + windowsBypassPackages

    val deprecatedVirtualPolicyPackages = setOf(
        DEPRECATED_SETTINGS_SECTIONS_PACKAGE
    )

    val parentVisibleLockPackages = sourceLockPackages + primarySettingsPackages + settingsSectionLockPackages + windowsBypassPackages

    val defaultLockedPackages = settingsSectionLockPackages + windowsBypassPackages

    val alwaysProtectedPackages = setOf(
        TV_PACKAGE,
        "com.android.systemui",
        "com.android.settings",
        "com.android.tv.settings",
        "com.android.packageinstaller",
        "com.google.android.packageinstaller",
        "com.android.documentsui",
        "com.android.permissioncontroller",
        "com.google.android.permissioncontroller",
        "com.google.android.gms",
        "com.google.android.gsf",
        "com.google.android.tvlauncher",
        "com.google.android.apps.tv.launcherx"
    )

    val riskySettingsPackages = emptySet<String>()

    fun isDefaultLocked(packageName: String): Boolean = packageName in defaultLockedPackages

    fun settingsSectionPolicy(packageName: String): SettingsSectionPolicy? {
        return settingsSectionPolicies.firstOrNull { it.packageName == packageName }
    }

    fun settingsSectionPolicyForKey(key: String): SettingsSectionPolicy? {
        return settingsSectionPolicies.firstOrNull { it.key == key }
    }

    fun sourceLockPolicyPackage(packageName: String): String? {
        return if (packageName in sourceLockRuntimePackages) sourceLockPackages.first() else null
    }
}
