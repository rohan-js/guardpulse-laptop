namespace GuardPulse.Protocol;

using System.Collections.Generic;

/// <summary>
/// Platform-neutral policy constants. Ported from
/// shared/src/main/java/com/guardpulse/parentcontrol/shared/PolicyConstants.kt.
/// Android-only package sets (source-lock, primary settings, always-protected, parent-visible)
/// are intentionally not ported; this agent needs the settings-section and Windows bypass ids.
/// Set-valued constants are exposed as <see cref="IReadOnlyCollection{T}"/> because
/// netstandard2.0 has no IReadOnlySet.
/// </summary>
public static class PolicyConstants
{
    public sealed record SettingsSectionPolicy(string PackageName, string Label, string ShortLabel, string Key);

    public const string SETTINGS_APPS_PACKAGE = "com.guardpulse.policy.settings_apps";
    public const string SETTINGS_DEVELOPER_OPTIONS_PACKAGE = "com.guardpulse.policy.settings_developer_options";
    public const string SETTINGS_SECURITY_RESTRICTIONS_PACKAGE = "com.guardpulse.policy.settings_security_restrictions";
    public const string SETTINGS_ACCESSIBILITY_PACKAGE = "com.guardpulse.policy.settings_accessibility";
    public const string SETTINGS_RESET_PACKAGE = "com.guardpulse.policy.settings_reset";
    public const string DEPRECATED_SETTINGS_SECTIONS_PACKAGE = "com.guardpulse.policy.settings_sections";

    public const string COMMAND_RESCAN_APPS = "rescanApps";
    public const string COMMAND_RESET_TODAY = "resetToday";
    public const string COMMAND_UNPAIR = "unpair";
    public const string COMMAND_OPEN_SETUP = "openSetup";

    public const string COMMAND_PENDING = "pending";
    public const string COMMAND_RUNNING = "running";
    public const string COMMAND_DONE = "done";
    public const string COMMAND_FAILED = "failed";
    public const string COMMAND_EXPIRED = "expired";

    public const string PLATFORM_ANDROID_TV = "androidTv";
    public const string PLATFORM_WINDOWS = "windows";

    // Windows bypass virtual app ids (default-locked).
    public const string WINDOWS_TASK_MANAGER_PACKAGE = "guardpulse.windows.taskmgr";
    public const string WINDOWS_COMMAND_LINE_PACKAGE = "guardpulse.windows.commandline";
    public const string WINDOWS_REGISTRY_EDITOR_PACKAGE = "guardpulse.windows.registry";
    public const string WINDOWS_SETTINGS_PACKAGE = "guardpulse.windows.settings";
    public const string WINDOWS_INSTALLERS_PACKAGE = "guardpulse.windows.installers";

    public const string BLOCK_REASON_MANUAL = "manual";
    public const string BLOCK_REASON_DAILY_LIMIT = "dailyLimit";
    public const string BLOCK_REASON_RISKY_SETTINGS = "riskySettings";
    public const string BLOCK_REASON_NETWORK_FILTER_MISSING = "networkFilterMissing";
    public const string BLOCK_REASON_SOURCE_LOCK = "sourceLock";
    public const string BLOCK_REASON_SETTINGS_SECTION = "settingsSection";
    public const string BLOCK_REASON_SCHEDULE = "schedule";
    public const string BLOCK_REASON_BUDGET = "budget";
    public const string BLOCK_REASON_NOT_APPROVED = "notApproved";

    public const string ENFORCEMENT_DEVICE_OWNER = "deviceOwner";
    public const string ENFORCEMENT_FALLBACK = "fallback";
    public const string ENFORCEMENT_UNPROTECTED = "unprotected";

    public const string UNLOCK_PENDING = "pending";
    public const string UNLOCK_APPROVED = "approved";
    public const string UNLOCK_DENIED = "denied";
    public const string UNLOCK_EXPIRED = "expired";
    public const string UNLOCK_APPROVAL_ONE_VISIT = "oneVisit";
    public const string UNLOCK_APPROVAL_TIMED = "timed";

    public const int SYNC_PROTOCOL_VERSION = 2;
    public const string SYNC_STATUS_APPLIED = "applied";
    public const string SYNC_STATUS_FAILED = "failed";

    public const string PAIR_PENDING = "pending";
    public const string PAIR_ACCEPTED = "accepted";
    public const string PAIR_REJECTED = "rejected";
    public const string PAIR_EXPIRED = "expired";
    public const string PAIR_FAILED = "failed";

    public const string REVISION_APP_POLICY = "appPolicy";
    public const string REVISION_MODE_CREATE = "modeCreate";
    public const string REVISION_MODE_UPDATE = "modeUpdate";
    public const string REVISION_MODE_DELETE = "modeDelete";
    public const string REVISION_MODE_POLICY = "modePolicy";
    public const string REVISION_ACTIVE_MODE = "activeMode";
    public const string REVISION_SAFE_MODE = "safeMode";
    public const string REVISION_PIN = "pin";
    public const string REVISION_MIGRATION = "migration";
    public const string REVISION_SCHEDULE = "schedule";
    public const string REVISION_BUDGET = "budget";
    public const string REVISION_CONTENT_FILTER = "contentFilter";
    public const string REVISION_ALLOWLIST = "allowlist";
    public const string REVISION_CUSTOM_DOMAINS = "customBlockedDomains";

    public const string TAMPER_ADMIN_DISABLE_REQUESTED = "adminDisableRequested";
    public const string TAMPER_ADMIN_DISABLED = "adminDisabled";
    public const string TAMPER_ACCESSIBILITY_DISABLED = "accessibilityDisabled";
    public const string TAMPER_USAGE_ACCESS_MISSING = "usageAccessMissing";
    public const string TAMPER_VPN_DISABLED = "vpnDisabled";
    public const string TAMPER_RISKY_SETTINGS_OPENED = "riskySettingsOpened";
    public const string TAMPER_PIN_RETRY_LOCKED = "pinRetryLocked";

    // 30s: the phone's online/offline badge can only be as fresh as the last
    // heartbeat, so 60s meant a live laptop could read "offline" for a minute.
    public const long HEARTBEAT_INTERVAL_MS = 30_000L;
    public const long USAGE_SCAN_INTERVAL_MS = 60_000L;
    public const long FOREGROUND_USAGE_UPLOAD_INTERVAL_MS = 10_000L;
    public const long FOREGROUND_USAGE_EXTRAPOLATION_MAX_MS = 20_000L;
    public const long PAIRING_TTL_MS = 10 * 60_000L;
    public const long TEMP_UNLOCK_MS = 10 * 60_000L;
    public const long UNLOCK_15_MINUTES_MS = 15 * 60_000L;
    public const long UNLOCK_30_MINUTES_MS = 30 * 60_000L;
    public const long SAFE_MODE_DURATION_MS = 30 * 60_000L;
    public const long TAMPER_EVENT_THROTTLE_MS = 15 * 60_000L;
    public const long COMMAND_OPEN_SETUP_TTL_MS = 60_000L;
    public const long COMMAND_STANDARD_TTL_MS = 5 * 60_000L;
    public const long COMMAND_UNPAIR_TTL_MS = 10 * 60_000L;

    public const int PIN_LENGTH = 6;
    public const int MAX_DAILY_LIMIT_MINUTES = 1440;

    public static IReadOnlyList<SettingsSectionPolicy> SettingsSectionPolicies { get; }

    public static IReadOnlyCollection<string> SettingsSectionLockPackages { get; }

    public static IReadOnlyCollection<string> WindowsBypassPackages { get; }

    public static IReadOnlyCollection<string> DeprecatedVirtualPolicyPackages { get; }

    /// <summary>settingsSectionLockPackages + windowsBypassPackages; filled in as blocked by EffectiveApps().</summary>
    public static IReadOnlyCollection<string> DefaultLockedPackages { get; }

    private static readonly HashSet<string> DefaultLockedSet;
    private static readonly HashSet<string> WindowsBypassSet;
    private static readonly HashSet<string> SettingsSectionLockSet;

    static PolicyConstants()
    {
        SettingsSectionPolicies = new[]
        {
            new SettingsSectionPolicy(SETTINGS_APPS_PACKAGE, "Settings: Apps", "Apps", "settings-apps"),
            new SettingsSectionPolicy(SETTINGS_DEVELOPER_OPTIONS_PACKAGE, "Settings: Developer options", "Developer options", "settings-developer-options"),
            new SettingsSectionPolicy(SETTINGS_SECURITY_RESTRICTIONS_PACKAGE, "Settings: Security & restrictions", "Security & restrictions", "settings-security-restrictions"),
            new SettingsSectionPolicy(SETTINGS_ACCESSIBILITY_PACKAGE, "Settings: Accessibility", "Accessibility", "settings-accessibility"),
            new SettingsSectionPolicy(SETTINGS_RESET_PACKAGE, "Settings: Reset", "Reset", "settings-reset"),
        };

        var settingsSectionLockPackages = new HashSet<string>();
        foreach (var policy in SettingsSectionPolicies)
        {
            settingsSectionLockPackages.Add(policy.PackageName);
        }

        var windowsBypassPackages = new HashSet<string>
        {
            WINDOWS_TASK_MANAGER_PACKAGE,
            WINDOWS_COMMAND_LINE_PACKAGE,
            WINDOWS_REGISTRY_EDITOR_PACKAGE,
            WINDOWS_SETTINGS_PACKAGE,
            WINDOWS_INSTALLERS_PACKAGE,
        };

        var defaultLockedPackages = new HashSet<string>(settingsSectionLockPackages);
        defaultLockedPackages.UnionWith(windowsBypassPackages);

        SettingsSectionLockPackages = settingsSectionLockPackages;
        WindowsBypassPackages = windowsBypassPackages;
        DeprecatedVirtualPolicyPackages = new HashSet<string> { DEPRECATED_SETTINGS_SECTIONS_PACKAGE };
        DefaultLockedPackages = defaultLockedPackages;

        SettingsSectionLockSet = settingsSectionLockPackages;
        WindowsBypassSet = windowsBypassPackages;
        DefaultLockedSet = defaultLockedPackages;
    }

    public static long CommandTtlMs(string type)
    {
        switch (type)
        {
            case COMMAND_OPEN_SETUP: return COMMAND_OPEN_SETUP_TTL_MS;
            case COMMAND_UNPAIR: return COMMAND_UNPAIR_TTL_MS;
            default: return COMMAND_STANDARD_TTL_MS;
        }
    }

    public static bool IsDefaultLocked(string packageName)
    {
        return DefaultLockedSet.Contains(packageName);
    }

    public static bool IsWindowsBypassPackage(string packageName)
    {
        return WindowsBypassSet.Contains(packageName);
    }

    public static bool IsSettingsSectionLockPackage(string packageName)
    {
        return SettingsSectionLockSet.Contains(packageName);
    }

    public static SettingsSectionPolicy? SettingsSectionPolicyFor(string packageName)
    {
        foreach (var policy in SettingsSectionPolicies)
        {
            if (policy.PackageName == packageName)
            {
                return policy;
            }
        }

        return null;
    }

    public static SettingsSectionPolicy? SettingsSectionPolicyForKey(string key)
    {
        foreach (var policy in SettingsSectionPolicies)
        {
            if (policy.Key == key)
            {
                return policy;
            }
        }

        return null;
    }
}
