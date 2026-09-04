// Ported from shared/src/main/java/com/guardpulse/parentcontrol/shared/ControlProtocol.kt (data classes).
// NOTE: this file uses block-namespace style so it can also declare the netstandard2.0
// System.Runtime.CompilerServices.IsExternalInit shim in the GLOBAL namespace, which
// positional C# records require to compile when targeting netstandard2.0.
namespace GuardPulse.Protocol
{
    using System.Collections.Generic;

    /// <summary>Per-app policy rule. Map keys in Apps dictionaries are package names (decoded).</summary>
    public sealed record ControlAppRule(
        string PackageName,
        bool ManualBlocked,
        int? DailyLimitMinutes = null,
        int? SessionLimitMinutes = null,
        long? UpdatedAt = null);

    /// <summary>A named mode with its own app rules. Map key equals ModeId.</summary>
    public sealed record ControlMode(
        string ModeId,
        string Name,
        IReadOnlyDictionary<string, ControlAppRule> Apps,
        long? CreatedAt = null,
        long? UpdatedAt = null);

    /// <summary>Reference to the currently selected mode.</summary>
    public sealed record ControlActiveMode(
        string? ModeId,
        string? ModeName = null,
        long? ActivatedAt = null);

    /// <summary>Safe Mode window; when Enabled, Until must be a positive epoch-ms deadline.</summary>
    public sealed record ControlSafeMode(
        bool Enabled = false,
        long Until = 0,
        long? StartedAt = null,
        string? StartedBy = null);

    /// <summary>Stored PIN hash configuration (v1 legacy SHA-256 or v2 PBKDF2).</summary>
    public sealed record ControlPin(
        string Salt,
        string Hash,
        int Version = 1,
        string? Algorithm = null,
        int? Iterations = null,
        long? UpdatedAt = null);

    /// <summary>Allowed-hours window in device-local minutes; start &gt; end wraps past midnight.</summary>
    public sealed record ControlSchedule(
        bool Enabled = false,
        int StartMinute = 0,
        int EndMinute = 0);

    /// <summary>Whole-device daily screen-time budget in minutes.</summary>
    public sealed record ControlBudget(
        int DailyLimitMinutes);

    /// <summary>Hosts-file content filter categories.</summary>
    public sealed record ControlContentFilter(
        bool Social = false,
        bool Gambling = false,
        bool Adult = false,
        bool Gaming = false);

    /// <summary>When enabled, only inventoried/system apps may run.</summary>
    public sealed record ControlAllowlist(
        bool Enabled = false);

    /// <summary>User-defined domains blocked via hosts file (e.g. youtube.com).</summary>
    public sealed record ControlCustomBlockedDomains(
        IReadOnlyList<string> Domains);

    /// <summary>The V2 control snapshot stored at devices/{id}/control/v2.</summary>
    public sealed record ControlSnapshotV2(
        string RevisionId,
        long? UpdatedAt,
        string? UpdatedBy,
        IReadOnlyDictionary<string, ControlAppRule> Apps,
        IReadOnlyDictionary<string, ControlMode> Modes,
        ControlActiveMode? ActiveMode,
        ControlSafeMode SafeMode,
        ControlPin? Pin,
        ControlSchedule? Schedule = null,
        ControlBudget? Budget = null,
        ControlContentFilter? ContentFilter = null,
        ControlAllowlist? Allowlist = null,
        ControlCustomBlockedDomains? CustomBlockedDomains = null)
    {
        /// <summary>Convenience constructor mirroring the Kotlin defaults (empty maps, SafeMode off).</summary>
        public ControlSnapshotV2(string revisionId)
            : this(
                revisionId,
                UpdatedAt: null,
                UpdatedBy: null,
                new Dictionary<string, ControlAppRule>(),
                new Dictionary<string, ControlMode>(),
                ActiveMode: null,
                new ControlSafeMode(),
                Pin: null)
        {
        }

        /// <summary>
        /// When ActiveMode references an existing mode, the mode's apps replace the top-level apps;
        /// every default-locked package (settings sections + Windows bypass ids) is then filled in
        /// as manually blocked when absent. Otherwise the top-level apps are used (defaults still filled in).
        /// </summary>
        public IReadOnlyDictionary<string, ControlAppRule> EffectiveApps()
        {
            ControlMode? selectedMode = null;
            if (ActiveMode != null && ActiveMode.ModeId != null && Modes.TryGetValue(ActiveMode.ModeId, out var mode))
            {
                selectedMode = mode;
            }

            var source = selectedMode != null ? selectedMode.Apps : Apps;
            var result = new Dictionary<string, ControlAppRule>(source);
            foreach (var packageName in PolicyConstants.DefaultLockedPackages)
            {
                if (!result.ContainsKey(packageName))
                {
                    result[packageName] = new ControlAppRule(packageName, ManualBlocked: true);
                }
            }

            return result;
        }
    }
}

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Required by C# 9+ records/init accessors on netstandard2.0 (no extra file is allowed for this
    /// assembly, so the shim lives here). Must be declared in the global namespace.
    /// </summary>
    internal sealed class IsExternalInit
    {
    }
}
