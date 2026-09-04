namespace GuardPulse.Protocol;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

public enum ControlParseStatus
{
    Missing,
    Valid,
    Invalid,
}

/// <summary>Outcome of parsing devices/{id}/control/v2. Invalid carries the first validation error.</summary>
public sealed record ControlParseResult(ControlParseStatus Status, ControlSnapshotV2? Snapshot, string? Error);

/// <summary>devices/{id}/sync/desired payload (only well-formed, known-kind revisions parse).</summary>
public sealed record SyncDesiredRevision(
    string RevisionId,
    string Kind,
    string? Target = null,
    long? RequestedAt = null,
    string? RequestedBy = null);

/// <summary>devices/{id}/sync/applied acknowledgement payload written by the agent after enforcement.</summary>
public sealed record SyncAppliedRevision(
    string? RevisionId = null,
    string? Status = null,
    long? AppliedAt = null,
    string? SessionId = null,
    string? Error = null);

/// <summary>
/// Parses and validates the V2 control snapshot. Ported from
/// shared/src/main/java/com/guardpulse/parentcontrol/shared/ControlProtocol.kt — same checks in the
/// same order with the same error messages, reading System.Text.Json instead of a Firebase DataSnapshot.
/// Value coercions mirror Firebase RTDB semantics (numeric strings become numbers, 0/1 become booleans,
/// and JSON null is treated as absent because null deletes keys in RTDB).
/// </summary>
public static class ControlProtocol
{
    private static readonly HashSet<string> RevisionKinds = new HashSet<string>
    {
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
        PolicyConstants.REVISION_CUSTOM_DOMAINS,
    };

    // 16 bytes -> 22 base64url chars; 32 bytes -> 43 base64url chars.
    private static readonly Regex SaltShape = new Regex("^[A-Za-z0-9_-]{22}$", RegexOptions.Compiled);
    private static readonly Regex HashShape = new Regex("^[A-Za-z0-9_-]{43}$", RegexOptions.Compiled);

    /// <summary>
    /// Parses the raw JSON of devices/{id}/control/v2. "null", empty and blank inputs are Missing
    /// (the REST API returns "null" when the node does not exist); malformed or rule-violating
    /// payloads are Invalid with the first failing check's message.
    /// </summary>
    public static ControlParseResult Parse(string json)
    {
        if (IsMissing(json))
        {
            return new ControlParseResult(ControlParseStatus.Missing, null, null);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (Exception ex) when (ex is JsonException || ex is ArgumentException)
        {
            return new ControlParseResult(ControlParseStatus.Invalid, null, "Control payload is not valid JSON: " + ex.Message);
        }

        using (document)
        {
            try
            {
                var snapshot = ParseSnapshot(document.RootElement);
                return new ControlParseResult(ControlParseStatus.Valid, snapshot, null);
            }
            catch (ControlValidationException ex)
            {
                return new ControlParseResult(ControlParseStatus.Invalid, null, ex.Message);
            }
        }
    }

    /// <summary>
    /// Parses devices/{id}/sync/desired. Returns null when absent, malformed, or when the revisionId
    /// is blank or the kind is not one of the known revision kinds (matches the Kotlin parseDesired).
    /// </summary>
    public static SyncDesiredRevision? ParseDesired(string json)
    {
        if (IsMissing(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var revisionId = GetString(root, "revisionId");
            if (revisionId == null || revisionId.Trim().Length == 0)
            {
                return null;
            }

            var kind = GetString(root, "kind");
            if (kind == null || !RevisionKinds.Contains(kind))
            {
                return null;
            }

            return new SyncDesiredRevision(
                revisionId,
                kind,
                GetString(root, "target"),
                GetLong(root, "requestedAt"),
                GetString(root, "requestedBy"));
        }
        catch (Exception ex) when (ex is JsonException || ex is ArgumentException)
        {
            return null;
        }
    }

    private static ControlSnapshotV2 ParseSnapshot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Fail("Control snapshot is not an object");
        }

        var schemaVersion = GetLong(root, "schemaVersion");
        if (schemaVersion != PolicyConstants.SYNC_PROTOCOL_VERSION)
        {
            var printed = schemaVersion?.ToString(CultureInfo.InvariantCulture) ?? "missing";
            throw Fail("Unsupported control schema: " + printed);
        }

        var revisionId = GetString(root, "revisionId");
        if (revisionId == null || revisionId.Trim().Length == 0)
        {
            throw Fail("Control revision is missing");
        }

        var apps = ParseApps(GetChild(root, "apps"));

        // Lenient structural nodes: a malformed mode/activeMode/safeMode/pin must NOT poison the
        // whole snapshot. We drop the offending node to its safe default instead of throwing, which
        // would otherwise mark the entire control tree Invalid and silently ignore the parent's changes.
        var modesElement = GetChild(root, "modes");
        var modes = new Dictionary<string, ControlMode>();
        if (modesElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var modeProperty in modesElement.EnumerateObject())
            {
                var encodedModeId = modeProperty.Name;
                var modeElement = modeProperty.Value;
                if (modeElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                // Structural mode fields are lenient: a malformed id/key/name drops just this mode.
                var modeId = GetString(modeElement, "modeId") ?? encodedModeId;
                if (modeId.Trim().Length == 0 || modeId != encodedModeId)
                {
                    continue;
                }

                var name = GetString(modeElement, "name")?.Trim();
                if (name == null || name.Length == 0)
                {
                    continue;
                }

                // App rules are parsed strictly (a bad entry fails the whole snapshot, exactly like
                // the top-level apps node) so this must NOT be swallowed by mode-level leniency.
                var modeApps = ParseApps(GetChild(modeElement, "apps"));

                modes[modeId] = new ControlMode(
                    modeId,
                    name,
                    modeApps,
                    GetLong(modeElement, "createdAt"),
                    GetLong(modeElement, "updatedAt"));
            }
        }

        ControlActiveMode? activeMode = null;
        if (Exists(root, "activeMode"))
        {
            try
            {
                var activeModeElement = GetChild(root, "activeMode");
                var modeId = GetString(activeModeElement, "modeId");
                // Missing/blank modeId or an unknown mode is dropped (activeMode left null)
                // instead of failing the whole snapshot.
                if (modeId != null && modeId.Trim().Length > 0 && modes.ContainsKey(modeId))
                {
                    activeMode = new ControlActiveMode(
                        modeId,
                        GetString(activeModeElement, "modeName"),
                        GetLong(activeModeElement, "activatedAt"));
                }
            }
            catch (ControlValidationException)
            {
                // Drop a malformed activeMode.
            }
        }

        ControlSafeMode safeMode;
        try
        {
            if (!Exists(root, "safeMode"))
            {
                safeMode = new ControlSafeMode(); // default: disabled
            }
            else
            {
                var safeModeElement = GetChild(root, "safeMode");
                var safeModeEnabled = GetBool(safeModeElement, "enabled");
                if (safeModeEnabled == null)
                {
                    safeMode = new ControlSafeMode(); // default: disabled
                }
                else
                {
                    var safeModeUntil = GetLong(safeModeElement, "until");
                    if (safeModeUntil == null)
                    {
                        safeMode = new ControlSafeMode(); // default: disabled
                    }
                    else
                    {
                        var candidate = new ControlSafeMode(
                            safeModeEnabled.Value,
                            safeModeUntil.Value,
                            GetLong(safeModeElement, "startedAt"),
                            GetString(safeModeElement, "startedBy"));
                        // Tolerate an invalid expiry window as disabled rather than failing the snapshot.
                        safeMode = (candidate.Enabled && candidate.Until <= 0) ? new ControlSafeMode() : candidate;
                    }
                }
            }
        }
        catch (ControlValidationException)
        {
            safeMode = new ControlSafeMode(); // default: disabled
        }

        ControlPin? pin = null;
        if (Exists(root, "pin"))
        {
            try
            {
                var pinElement = GetChild(root, "pin");
                var salt = GetString(pinElement, "salt");
                if (salt == null || salt.Trim().Length == 0 || !SaltShape.IsMatch(salt))
                {
                    // drop malformed pin (was Fail on missing/invalid salt)
                }
                else
                {
                    var hash = GetString(pinElement, "hash");
                    if (hash == null || hash.Trim().Length == 0 || !HashShape.IsMatch(hash))
                    {
                        // drop malformed pin (was Fail on missing/invalid hash)
                    }
                    else
                    {
                        var version = (int)(GetLong(pinElement, "version") ?? PinHasher.LEGACY_VERSION);
                        if (version != PinHasher.LEGACY_VERSION && version != PinHasher.CURRENT_VERSION)
                        {
                            // drop malformed pin (was Fail("Unsupported PIN hash version"))
                        }
                        else
                        {
                            var algorithm = GetString(pinElement, "algorithm");
                            var iterations = GetLong(pinElement, "iterations");
                            if (version == PinHasher.CURRENT_VERSION
                                && (algorithm != PinHasher.ALGORITHM
                                    || iterations == null
                                    || iterations < PinHasher.MIN_V2_ITERATIONS
                                    || iterations > 1_000_000))
                            {
                                // drop malformed pin (was Fail on algorithm/iterations)
                            }
                            else
                            {
                                pin = new ControlPin(
                                    salt,
                                    hash,
                                    version,
                                    algorithm,
                                    iterations == null ? null : (int?)iterations.Value,
                                    GetLong(pinElement, "updatedAt"));
                            }
                        }
                    }
                }
            }
            catch (ControlValidationException)
            {
                // Drop a malformed pin.
            }
        }

        ControlSchedule? schedule = null;
        if (Exists(root, "schedule"))
        {
            var scheduleElement = GetChild(root, "schedule");
            var scheduleEnabled = GetBool(scheduleElement, "enabled");
            if (scheduleEnabled == null)
            {
                throw Fail("Schedule enabled flag is missing");
            }

            var startMinute = GetLong(scheduleElement, "startMinute");
            var endMinute = GetLong(scheduleElement, "endMinute");
            if (startMinute == null || endMinute == null)
            {
                throw Fail("Schedule window is missing");
            }

            if (startMinute < 0 || startMinute > 1439 || endMinute < 0 || endMinute > 1439)
            {
                throw Fail("Schedule window is out of range");
            }

            schedule = new ControlSchedule(scheduleEnabled.Value, (int)startMinute.Value, (int)endMinute.Value);
        }

        ControlBudget? budget = null;
        if (Exists(root, "budget"))
        {
            var budgetMinutes = GetLong(GetChild(root, "budget"), "dailyLimitMinutes");
            if (budgetMinutes == null)
            {
                throw Fail("Budget limit is missing");
            }

            if (budgetMinutes < 1 || budgetMinutes > 1440)
            {
                throw Fail("Budget limit is out of range");
            }

            budget = new ControlBudget((int)budgetMinutes.Value);
        }

        ControlContentFilter? contentFilter = null;
        if (Exists(root, "contentFilter"))
        {
            var filterElement = GetChild(root, "contentFilter");
            contentFilter = new ControlContentFilter(
                OptionalBool(filterElement, "social", "Content filter social flag is invalid"),
                OptionalBool(filterElement, "gambling", "Content filter gambling flag is invalid"),
                OptionalBool(filterElement, "adult", "Content filter adult flag is invalid"),
                OptionalBool(filterElement, "gaming", "Content filter gaming flag is invalid"));
        }

        ControlAllowlist? allowlist = null;
        if (Exists(root, "allowlist"))
        {
            var allowlistEnabled = GetBool(GetChild(root, "allowlist"), "enabled");
            if (allowlistEnabled == null)
            {
                throw Fail("Allowlist enabled flag is missing");
            }

            allowlist = new ControlAllowlist(allowlistEnabled.Value);
        }

        ControlCustomBlockedDomains? customBlockedDomains = null;
        if (Exists(root, "customBlockedDomains"))
        {
            var arr = GetChild(root, "customBlockedDomains");
            if (arr.ValueKind == JsonValueKind.Array)
            {
                var domains = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    var raw = item.GetString()?.Trim() ?? "";
                    if (raw.Length == 0) continue;
                    // Path-aware: preserves URL paths like youtube.com/shorts (path entries are
                    // routed to browser policy, not hosts). Old NormalizeCustomDomain stripped paths.
                    var normalized = NormalizeCustomDomainWithPath(raw);
                    if (normalized == null) continue;
                    if (seen.Add(normalized))
                    {
                        domains.Add(normalized);
                        if (domains.Count >= 100) break;
                    }
                }

                if (domains.Count > 0)
                {
                    customBlockedDomains = new ControlCustomBlockedDomains(domains);
                }
            }
        }

        return new ControlSnapshotV2(
            revisionId,
            GetLong(root, "updatedAt"),
            GetString(root, "updatedBy"),
            apps,
            modes,
            activeMode,
            safeMode,
            pin,
            schedule,
            budget,
            contentFilter,
            allowlist,
            customBlockedDomains);
    }

    private static bool RequiredBool(JsonElement parent, string name, string message)
    {
        return GetBool(parent, name) ?? throw Fail(message);
    }

    /// <summary>Absent categories default to false; present-but-non-boolean values are invalid.</summary>
    private static bool OptionalBool(JsonElement parent, string name, string message)
    {
        if (!Exists(parent, name))
        {
            return false;
        }

        return GetBool(parent, name) ?? throw Fail(message);
    }

    private static Dictionary<string, ControlAppRule> ParseApps(JsonElement element)
    {
        var result = new Dictionary<string, ControlAppRule>();
        if (element.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var appProperty in element.EnumerateObject())
        {
            var encodedKey = appProperty.Name;
            var appElement = appProperty.Value;

            var packageName = GetString(appElement, "packageName");
            if (packageName == null)
            {
                throw Fail("App package is missing");
            }

            if (PackageKeys.Encode(packageName) != encodedKey)
            {
                throw Fail("App policy key does not match package");
            }

            var packageKey = GetString(appElement, "packageKey");
            if (packageKey != null && packageKey != encodedKey)
            {
                throw Fail("App package key does not match policy key");
            }

            var manualBlocked = GetBool(appElement, "manualBlocked");
            if (manualBlocked == null)
            {
                throw Fail("App manualBlocked flag is missing");
            }

            int? dailyLimitMinutes = null;
            if (Exists(appElement, "dailyLimitMinutes"))
            {
                var raw = GetLong(appElement, "dailyLimitMinutes");
                if (raw == null)
                {
                    throw Fail("App daily limit is invalid");
                }

                if (raw < 1 || raw > PolicyConstants.MAX_DAILY_LIMIT_MINUTES)
                {
                    throw Fail("App daily limit is out of range");
                }

                dailyLimitMinutes = (int)raw.Value;
            }

            int? sessionLimitMinutes = null;
            if (Exists(appElement, "sessionLimitMinutes"))
            {
                var raw = GetLong(appElement, "sessionLimitMinutes");
                if (raw == null)
                {
                    throw Fail("App session limit is invalid");
                }

                if (raw < 1 || raw > PolicyConstants.MAX_DAILY_LIMIT_MINUTES)
                {
                    throw Fail("App session limit is out of range");
                }

                sessionLimitMinutes = (int)raw.Value;
            }

            result[packageName] = new ControlAppRule(
                packageName,
                manualBlocked.Value,
                dailyLimitMinutes,
                sessionLimitMinutes,
                GetLong(appElement, "updatedAt"));
        }

        return result;
    }

    private static bool IsMissing(string json)
    {
        return json == null
            || json.Trim().Length == 0
            || string.Equals(json.Trim(), "null", StringComparison.Ordinal);
    }

    private static bool Exists(JsonElement parent, string name)
    {
        return parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out var value)
            && value.ValueKind != JsonValueKind.Null;
    }

    private static JsonElement GetChild(JsonElement parent, string name)
    {
        if (Exists(parent, name))
        {
            return parent.GetProperty(name);
        }

        return default; // JsonValueKind.Undefined; every reader below treats it as absent.
    }

    private static string? GetString(JsonElement parent, string name)
    {
        if (!Exists(parent, name))
        {
            return null;
        }

        var value = parent.GetProperty(name);
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString();
            case JsonValueKind.Number:
                return value.GetRawText();
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            default:
                return null;
        }
    }

    private static long? GetLong(JsonElement parent, string name)
    {
        if (!Exists(parent, name))
        {
            return null;
        }

        var value = parent.GetProperty(name);
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.TryGetInt64(out var integer) ? integer : (long)value.GetDouble();
            case JsonValueKind.String:
                return long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;
            case JsonValueKind.True:
                return 1L;
            case JsonValueKind.False:
                return 0L;
            default:
                return null;
        }
    }

    private static bool? GetBool(JsonElement parent, string name)
    {
        if (!Exists(parent, name))
        {
            return null;
        }

        var value = parent.GetProperty(name);
        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.String:
                switch (value.GetString())
                {
                    case "true": return true;
                    case "false": return false;
                    default: return null;
                }
            case JsonValueKind.Number:
                if (value.TryGetInt64(out var integer))
                {
                    if (integer == 1)
                    {
                        return true;
                    }

                    if (integer == 0)
                    {
                        return false;
                    }
                }

                return null;
            default:
                return null;
        }
    }

    private static string? NormalizeCustomDomain(string raw)
    {
        var s = raw.Trim().ToLowerInvariant();
        if (s.StartsWith("http://", StringComparison.Ordinal))
        {
            s = s.Substring(7);
        }
        else if (s.StartsWith("https://", StringComparison.Ordinal))
        {
            s = s.Substring(8);
        }

        var slash = s.IndexOf('/');
        if (slash >= 0)
        {
            s = s.Substring(0, slash);
        }

        s = s.TrimEnd('.');
        if (s.Length == 0 || s.Length > 253)
        {
            return null;
        }

        if (s.Contains("..") || s.StartsWith("-") || s.StartsWith("."))
        {
            return null;
        }

        var labels = s.Split('.');
        if (labels.Length < 2)
        {
            return null;
        }

        foreach (var label in labels)
        {
            if (label.Length == 0 || label.Length > 63)
            {
                return null;
            }

            if (label.StartsWith("-") || label.EndsWith("-"))
            {
                return null;
            }

            foreach (var ch in label)
            {
                if (!((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-'))
                {
                    return null;
                }
            }
        }

        var tld = labels[labels.Length - 1];
        if (tld.Length < 2)
        {
            return null;
        }

        foreach (var ch in tld)
        {
            if (ch < 'a' || ch > 'z')
            {
                return null;
            }
        }

        return s;
    }

    /// <summary>
    /// Path-preserving variant for customBlockedDomains. Mirrors
    /// ParentSecurityFeature.kt normalizeCustomDomainForUi: validates domain labels,
    /// retains the URL path (e.g. "youtube.com/shorts") and validates path chars
    /// (a-z 0-9 / - _ . ? = &). Entries with a path are later routed to browser
    /// policy rather than hosts. Kept alongside NormalizeCustomDomain for any
    /// non-path callers.
    /// </summary>
    private static string? NormalizeCustomDomainWithPath(string raw)
    {
        var s = raw.Trim().ToLowerInvariant();
        if (s.StartsWith("http://", StringComparison.Ordinal))
        {
            s = s.Substring(7);
        }
        else if (s.StartsWith("https://", StringComparison.Ordinal))
        {
            s = s.Substring(8);
        }

        s = s.TrimEnd('/');
        if (s.Length == 0 || s.Length > 253)
        {
            return null;
        }

        if (s.Contains("..") || s.StartsWith("-") || s.StartsWith("."))
        {
            return null;
        }

        var slash = s.IndexOf('/');
        string domainPart;
        string pathPart;
        if (slash >= 0)
        {
            domainPart = s.Substring(0, slash);
            pathPart = s.Substring(slash + 1);
        }
        else
        {
            domainPart = s;
            pathPart = "";
        }

        var labels = domainPart.Split('.');
        if (labels.Length < 2)
        {
            return null;
        }

        foreach (var label in labels)
        {
            if (label.Length == 0 || label.Length > 63)
            {
                return null;
            }

            if (label.StartsWith("-") || label.EndsWith("-"))
            {
                return null;
            }

            foreach (var ch in label)
            {
                if (!((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-'))
                {
                    return null;
                }
            }
        }

        var tld = labels[labels.Length - 1];
        if (tld.Length < 2)
        {
            return null;
        }

        foreach (var ch in tld)
        {
            if (ch < 'a' || ch > 'z')
            {
                return null;
            }
        }

        if (pathPart.Length > 0)
        {
            foreach (var ch in pathPart)
            {
                if (!((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '/' || ch == '-' || ch == '_' || ch == '.' || ch == '?' || ch == '=' || ch == '&'))
                {
                    return null;
                }
            }
        }

        return s;
    }

    private static ControlValidationException Fail(string message)
    {
        return new ControlValidationException(message);
    }

    private sealed class ControlValidationException : Exception
    {
        public ControlValidationException(string message)
            : base(message)
        {
        }
    }
}
