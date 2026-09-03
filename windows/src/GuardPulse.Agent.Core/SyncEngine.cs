namespace GuardPulse.Agent.Core;

using System.Text.Json;
using System.Text.Json.Nodes;
using GuardPulse.Protocol;

/// <summary>
/// Ordered V2 synchronization, ported from the TV TvSyncEngine/TvSyncService rules
/// (docs/reliability-architecture.md):
///  - only complete VALID control revisions are enforced; malformed or removed
///    control never weakens the last valid local snapshot (fail-closed);
///  - a 20ms debounce coalesces bursts; the newest complete revision wins;
///  - the applied acknowledgement is written only after the host confirms durable
///    enforcement, and only while the revision is still the latest control revision;
///  - a fresh sessionId is minted on every reconnect and recorded in sync/runtime.
/// </summary>
public sealed class SyncEngine
{
    private const int ControlDebounceMs = 20;
    private const int DesiredSettleRetryMs = 25;
    private const int DesiredSettleMaxAttempts = 40; // ~1s, then apply the validated snapshot anyway
    private const int ReapplyRetryMs = 5_000;
    // REST fallback for the control/v2 SSE stream: when the stream is silently dead a
    // parent control write would otherwise never be seen. Polls the node directly and
    // feeds the same handler; the raw-content dedup skips identical snapshots. Fast
    // (5s) while degraded so a parent lock lands in seconds; skipped entirely while
    // the stream is confirmed healthy - SSE pushes make the poll redundant there.
    private const int ControlPollIntervalMs = 5_000;
    private static readonly TimeSpan SseHealthyWindow = TimeSpan.FromSeconds(10);
    // Bounded retries for the sync/applied ack so a transient PATCH failure (rules or
    // transport) never strands the parent on "Waiting for laptop" forever.
    private const int AckMaxAttempts = 3;
    private const string SnapshotSecretKey = "snapshot.v2";
    private const string AppliedSecretKey = "applied.v2";

    private readonly IFirebaseClient _firebase;
    private readonly ISecretStore _secrets;
    private readonly string _deviceId;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly List<IDisposable> _streams = new();

    private ControlSnapshotV2? _pendingSnapshot;
    private SyncDesiredRevision? _pendingDesired;
    private long _pendingControlGen;
    private long _pendingDesiredGen;
    private bool _v2Activated;
    private string? _lastValidRevision;
    private string? _lastAppliedRevision;
    private string? _lastAppliedSessionId;
    private string? _pendingAppliedRevision;
    private string? _lastRejectKey;
    private ControlSnapshotV2? _lastDispatchedSnapshot;
    private string? _pendingRaw;
    private string? _lastDispatchedRaw;
    private string? _currentControlRaw;
    private long _serverOffsetMs;
    private int _dispatchGeneration;
    private bool _started;
    private int _streamConnected; // 1 while .info/connected last reported true
    private DeviceRegistrar? _registrar;
    // Last time the .info/connected stream confirmed connectivity; gates the
    // control catch-up poll (redundant while the push stream is healthy).
    private DateTime _lastStreamHealthyUtc = DateTime.MinValue;

    public SyncEngine(IFirebaseClient firebase, ISecretStore secrets, string deviceId, TimeProvider time)
    {
        _firebase = firebase ?? throw new ArgumentNullException(nameof(firebase));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("Device id must be provided.", nameof(deviceId));
        }

        _deviceId = deviceId;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        SessionId = Guid.NewGuid().ToString("D");

        // Fail-closed offline protection: restore the last valid snapshot (and its
        // acknowledgement markers) from the encrypted local store before any listener runs.
        var stored = _secrets.Get(SnapshotSecretKey);
        if (stored != null)
        {
            var result = ControlProtocol.Parse(stored);
            if (result.Status == ControlParseStatus.Valid && result.Snapshot != null)
            {
                LastValidSnapshot = result.Snapshot;
                _lastValidRevision = result.Snapshot.RevisionId;
                _v2Activated = true;
            }
        }

        var applied = _secrets.Get(AppliedSecretKey);
        if (applied != null)
        {
            try
            {
                using var document = JsonDocument.Parse(applied);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    _lastAppliedRevision = ReadString(root, "revisionId");
                    _lastAppliedSessionId = ReadString(root, "sessionId");
                }
            }
            catch (JsonException)
            {
                // ignore unreadable markers; the first new revision rewrites them
            }
        }
    }

    /// <summary>Current sync session; renewed with a fresh Guid on every reconnect.</summary>
    public string SessionId { get; private set; }

    /// <summary>Newest VALID control snapshot (also restored across restarts); enforced offline.</summary>
    public ControlSnapshotV2? LastValidSnapshot { get; private set; }

    /// <summary>Revision last acknowledged as applied this session (null until the first ack).</summary>
    public string? LastAppliedRevision
    {
        get { lock (_gate) { return _lastAppliedRevision; } }
    }

    /// <summary>Raw JSON of the newest VALID control/v2 snapshot (null until the first valid snapshot).</summary>
    public string? CurrentControlRaw
    {
        get { lock (_gate) { return _currentControlRaw; } }
    }

    /// <summary>Owner uid discovered while registering the device (null while unpaired).</summary>
    public string? OwnerUid => _registrar?.OwnerUid;

    /// <summary>
    /// True while the .info/connected stream last reported connectivity (or it flipped
    /// positive and no negative report arrived since). Reflects real SSE stream health
    /// for the heartbeat's sync/runtime write; false before the first connect event.
    /// </summary>
    public bool IsStreamConnected => Volatile.Read(ref _streamConnected) == 1;

    /// <summary>Fired when a VALID snapshot should be enforced; the host then calls NotifyEnforcementAppliedAsync.</summary>
    public event Action<ControlSnapshotV2>? ControlApplied;

    /// <summary>Fired with "revisionId: reason" when control is malformed or was removed; last valid stays enforced.</summary>
    public event Action<string>? ControlRejected;

    /// <summary>Fired with the path + exception when one of the engine's SSE streams errors; reconnection is internal.</summary>
    public event Action<string, Exception>? StreamError;

    /// <summary>Raw JSON of the pairRequests node (value-event semantics).</summary>
    public event Action<string>? PairRequestReceived;

    /// <summary>Raw JSON of the devices/{id}/commands node (value-event semantics).</summary>
    public event Action<string>? CommandReceived;

    public event Action<bool>? ConnectionChanged;

    /// <summary>Authenticates, registers the device and attaches the value streams.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }
        }

        await _firebase.SignInAsync(ct).ConfigureAwait(false);

        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        try
        {
            _serverOffsetMs = await _firebase.FetchServerTimeOffsetMsAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // offset stays 0; refreshed on reconnect and hourly
        }

        _registrar = new DeviceRegistrar(_firebase, _deviceId);
        try
        {
            await _registrar.RegisterAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // registration is best-effort (Kotlin registerDevice is fire-and-forget)
        }

        StartStream(".info/connected", HandleConnectedData, ct);
        // Targeted child streams instead of one device-root stream: the root re-delivered
        // the ENTIRE device node on every self-write (heartbeat, state upload, sync acks),
        // forcing a full re-download + reparse 2-6x/min. Child streams only deliver what
        // the agent actually consumes; self-writes to state/heartbeat/sync produce no echo.
        // PairRequests stays separate (only while unpaired; broad auth fan-out), and
        // unlockRequests already has its own stream in the host.
        StartStream(FirebasePaths.DeviceControlV2(_deviceId), HandleControlData, ct);
        StartStream(FirebasePaths.DeviceSyncDesired(_deviceId), HandleDesiredData, ct);
        StartStream(FirebasePaths.DeviceCommands(_deviceId), raw => CommandReceived?.Invoke(raw), ct);
        StartStream(FirebasePaths.PairRequests(_deviceId), raw => PairRequestReceived?.Invoke(raw), ct);

        _ = RefreshOffsetLoopAsync(ct);
        _ = ControlPollLoopAsync(ct);
    }

    /// <summary>
    /// PATCHes sync/applied {revisionId,status:"applied",appliedAt,sessionId} — guarded so it only
    /// acks while revisionId is still the latest control revision and was not already acked this session.
    /// Throws on transport failure so the host can report NotifyEnforcementFailedAsync instead.
    /// </summary>
    public async Task NotifyEnforcementAppliedAsync(string revisionId)
    {
        if (string.IsNullOrWhiteSpace(revisionId))
        {
            return;
        }

        string sessionId;
        lock (_gate)
        {
            if (_lastValidRevision != revisionId)
            {
                return; // a newer control revision exists; an older ack must not overwrite it
            }

            if (_lastAppliedRevision == revisionId && _lastAppliedSessionId == SessionId)
            {
                return; // already acknowledged in this session
            }

            sessionId = SessionId;
            _pendingAppliedRevision = revisionId;
        }

        var payload = new JsonObject
        {
            ["revisionId"] = revisionId,
            ["status"] = PolicyConstants.SYNC_STATUS_APPLIED,
            ["appliedAt"] = ServerTimestamp(),
            ["sessionId"] = sessionId,
            // Clear any error left by a previous failed ack of an older revision.
            ["error"] = null,
        };
        var json = payload.ToJsonString();

        // Retry the ack a bounded number of times: the dispatch dedup never re-applies
        // this revision, so a single transient PATCH failure would otherwise leave the
        // parent on "Waiting for laptop" until the next control write.
        var lastError = (Exception?)null;
        for (var attempt = 1; attempt <= AckMaxAttempts; attempt++)
        {
            try
            {
                await _firebase.PatchAsync(FirebasePaths.DeviceSyncApplied(_deviceId), json, CancellationToken.None).ConfigureAwait(false);
                lastError = null;
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt < AckMaxAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt), CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        if (lastError != null)
        {
            // Let the host report NotifyEnforcementFailedAsync (the parent sees a clear
            // "rejected" state instead of a silent hang) only after retries are exhausted.
            throw lastError;
        }

        lock (_gate)
        {
            if (_lastValidRevision == revisionId)
            {
                _lastAppliedRevision = revisionId;
                _lastAppliedSessionId = sessionId;
                if (_pendingAppliedRevision == revisionId)
                {
                    _pendingAppliedRevision = null;
                }

                PersistAppliedLocked();
            }
        }
    }

    /// <summary>Writes sync/applied {status:"failed", error} for the given revision (bounded error text).</summary>
    public async Task NotifyEnforcementFailedAsync(string revisionId, string error)
    {
        if (string.IsNullOrWhiteSpace(revisionId))
        {
            return;
        }

        if (string.IsNullOrEmpty(error))
        {
            error = "unknown error";
        }

        var payload = new JsonObject
        {
            ["revisionId"] = revisionId,
            ["status"] = PolicyConstants.SYNC_STATUS_FAILED,
            ["appliedAt"] = ServerTimestamp(),
            ["sessionId"] = SessionId,
            ["error"] = Truncate(error, 200),
        };

        try
        {
            await _firebase.PatchAsync(FirebasePaths.DeviceSyncApplied(_deviceId), payload.ToJsonString(), CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // reporting a failure must never crash the engine
        }

        lock (_gate)
        {
            if (_pendingAppliedRevision == revisionId)
            {
                _pendingAppliedRevision = null;
            }
        }

        ScheduleDispatch(ReapplyRetryMs);
    }

    /// <summary>Local clock estimate: DateTime.UtcNow ms + the cached server time offset.</summary>
    public long ServerNowMs()
    {
        return _time.GetUtcNow().ToUnixTimeMilliseconds() + Volatile.Read(ref _serverOffsetMs);
    }


    /// <summary>
    /// Returns an error when the parsed snapshot lost a node the merged root contains
    /// (a mode whose name is blank or whose key does not equal its modeId, or an
    /// activeMode pointing at a mode that does not exist) — null when nothing was dropped.
    /// </summary>
    private static string? FindSilentlyDroppedNode(JsonObject merged, ControlSnapshotV2 parsed)
    {
        if (merged.TryGetPropertyValue("modes", out var modesNode) && modesNode is JsonObject mergedModes)
        {
            foreach (var (modeId, modeNode) in mergedModes)
            {
                if (modeNode is JsonObject && !parsed.Modes.ContainsKey(modeId))
                {
                    return "Mode '" + modeId + "' was rejected: it needs a non-empty name and its id must match its key.";
                }
            }
        }

        if (merged.TryGetPropertyValue("activeMode", out var activeNode) && activeNode is JsonObject mergedActive
            && mergedActive.TryGetPropertyValue("modeId", out var activeIdNode)
            && activeIdNode is JsonValue activeIdValue
            && activeIdValue.TryGetValue<string>(out var wantedId)
            && !string.IsNullOrWhiteSpace(wantedId))
        {
            var active = parsed.ActiveMode?.ModeId;
            if (!string.Equals(active, wantedId, StringComparison.Ordinal))
            {
                return "Cannot activate mode '" + wantedId + "': no mode with that id exists.";
            }
        }

        return null;
    }


    /// <summary>Finds the id of a non-expired pending unlock request for the given raw package name.</summary>
    private static string? FindPendingRequestId(string? raw, string packageName, long nowMs)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw!.Trim(), "null", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var request = prop.Value;
                if (request.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!string.Equals(ReadString(request, "status"), PolicyConstants.UNLOCK_PENDING, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!string.Equals(ReadString(request, "packageName"), packageName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var expiresAt = ReadLong(request, "expiresAt");
                if (expiresAt != null && expiresAt <= nowMs)
                {
                    continue; // expired pending requests are never approvable
                }

                return prop.Name;
            }
        }
        catch (JsonException)
        {
            // best effort: treat unreadable nodes as "no matching request"
        }

        return null;
    }

    private static JsonObject EmptyControlRoot()
    {
        return new JsonObject
        {
            ["schemaVersion"] = PolicyConstants.SYNC_PROTOCOL_VERSION,
            ["revisionId"] = NewRevisionId(),
            ["safeMode"] = new JsonObject { ["enabled"] = false, ["until"] = 0 },
            ["apps"] = new JsonObject(),
            ["modes"] = new JsonObject(),
        };
    }

    private static string NewRevisionId() => Guid.NewGuid().ToString("N");

    private static void MergeInto(JsonObject target, JsonObject patch)
    {
        foreach (var pair in patch)
        {
            if (pair.Value is null || pair.Value.GetValueKind() == JsonValueKind.Null)
            {
                // RTDB semantics: null deletes the key (and everything below it).
                target.Remove(pair.Key);
            }
            else if (pair.Value is JsonObject patchChild && target[pair.Key] is JsonObject targetChild)
            {
                MergeInto(targetChild, patchChild);
            }
            else
            {
                target[pair.Key] = pair.Value?.DeepClone();
            }
        }
    }

    // ----------------------------------------------------------------- streams

    private void StartStream(string path, Action<string?> onData, CancellationToken ct)
    {
        try
        {
            var stream = _firebase.StreamAsync(
                path,
                onData,
                error => StreamError?.Invoke(path, error),
                ct);
            lock (_gate)
            {
                _streams.Add(stream);
            }
        }
        catch
        {
            // StreamAsync only fails synchronously on programming errors; the
            // client reconnects on its own for transport failures.
        }
    }

    private async Task RefreshOffsetLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromHours(1), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                _serverOffsetMs = await _firebase.FetchServerTimeOffsetMsAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // keep the cached offset
            }
        }
    }

    /// <summary>
    /// REST fallback for the control/v2 stream. The SSE connection can die silently
    /// (see RtdbFirebaseClient's connect timeout), and without a fallback a parent
    /// control write would never reach enforcement. Polls the node directly and feeds
    /// the exact same handler as the stream; the raw-content dedup in DispatchAsync
    /// skips identical snapshots, so a healthy stream causes no duplicate work.
    /// </summary>
    private async Task ControlPollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ControlPollIntervalMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // SSE confirmed healthy recently? The stream pushes every change
            // instantly - a GET now would be redundant traffic. Skip this tick.
            if (_time.GetUtcNow().UtcDateTime - _lastStreamHealthyUtc < SseHealthyWindow)
            {
                continue;
            }

            try
            {
                var raw = await _firebase.GetAsync(FirebasePaths.DeviceControlV2(_deviceId), ct).ConfigureAwait(false);
                HandleControlData(raw);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // transient REST failure; the next poll or the stream retries
            }
        }
    }

    private void HandleConnectedData(string? raw)
    {
        if (raw == null || !bool.TryParse(raw.Trim(), out var connected))
        {
            return;
        }

        if (connected)
        {
            _lastStreamHealthyUtc = _time.GetUtcNow().UtcDateTime;
            Volatile.Write(ref _streamConnected, 1);
            lock (_gate)
            {
                SessionId = Guid.NewGuid().ToString("D");
            }

            ConnectionChanged?.Invoke(true);
            _ = WriteRuntimeAsync(connected: true);
        }
        else
        {
            Volatile.Write(ref _streamConnected, 0);
            ConnectionChanged?.Invoke(false);
            _ = WriteRuntimeAsync(connected: false);
        }
    }

    private async Task WriteRuntimeAsync(bool connected)
    {
        try
        {
            if (connected)
            {
                try
                {
                    _serverOffsetMs = await _firebase.FetchServerTimeOffsetMsAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // keep cached offset
                }
            }

            JsonObject payload = connected
                ? new JsonObject
                {
                    ["connected"] = true,
                    ["sessionId"] = SessionId,
                    ["protocolVersion"] = PolicyConstants.SYNC_PROTOCOL_VERSION,
                    ["connectedAt"] = ServerTimestamp(),
                    // No connectedVia: the deployed sync/runtime rules whitelist rejects
                    // unknown keys ($other:false), so including it failed every connect
                    // write and left sessionId/connected stale until the next heartbeat.
                }
                : new JsonObject
                {
                    ["connected"] = false,
                    ["disconnectedAt"] = ServerTimestamp(),
                };

            await _firebase.PatchAsync(FirebasePaths.DeviceSyncRuntime(_deviceId), payload.ToJsonString(), CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // runtime presence is best-effort; the next connect replay rewrites it
        }
    }

    private void HandleControlData(string? raw)
    {
        var result = ControlProtocol.Parse(raw ?? "null");
        if (result.Status == ControlParseStatus.Valid && result.Snapshot != null)
        {
            var snapshot = result.Snapshot;
            lock (_gate)
            {
                // The valid snapshot becomes durable immediately (fail-closed offline
                // protection) even before the debounce decides on dispatch/ack.
                _pendingSnapshot = snapshot;
                _v2Activated = true;
                _lastValidRevision = snapshot.RevisionId;
                LastValidSnapshot = snapshot;
                _pendingRaw = raw;
                _currentControlRaw = raw;
                try
                {
                    _secrets.Set(SnapshotSecretKey, raw!);
                }
                catch
                {
                    // secret store hiccup; in-memory snapshot still enforces
                }

                _dispatchGeneration++;
                _pendingControlGen = _dispatchGeneration;
            }

            ScheduleDispatch(ControlDebounceMs);
            return;
        }

        if (result.Status == ControlParseStatus.Missing)
        {
            // Node deleted: never weaken protection. The last valid revision is still
            // being enforced — nothing failed — so no failed ack is written for it
            // (that would make sync health report the healthy revision as broken).
            string? revision;
            bool activated;
            lock (_gate)
            {
                revision = _lastValidRevision;
                activated = _v2Activated;
            }

            if (activated && revision != null)
            {
                Reject(revision, "V2 control snapshot was removed", notifyFailed: false);
            }

            return;
        }

        // Invalid: keep enforcing the last valid snapshot, report the offending revision.
        Reject(ExtractRevisionId(raw), result.Error ?? "Invalid V2 control snapshot");
    }

    private void HandleDesiredData(string? raw)
    {
        lock (_gate)
        {
            _pendingDesired = ControlProtocol.ParseDesired(raw ?? "null");
            _dispatchGeneration++;
            _pendingDesiredGen = _dispatchGeneration;
        }

        ScheduleDispatch(ControlDebounceMs);
    }

    private void Reject(string? revisionId, string error, bool notifyFailed = true)
    {
        var message = string.IsNullOrEmpty(revisionId) ? error : revisionId + ": " + error;
        string rejectKey;
        lock (_gate)
        {
            rejectKey = (revisionId ?? "<null>") + "|" + error + "|" + notifyFailed;
            if (_lastRejectKey == rejectKey)
            {
                return; // SSE replays of the same bad payload must not spam acks
            }

            _lastRejectKey = rejectKey;
        }

        ControlRejected?.Invoke(message);
        if (notifyFailed && !string.IsNullOrWhiteSpace(revisionId))
        {
            _ = NotifyEnforcementFailedAsync(revisionId, error);
        }
    }

    // ---------------------------------------------------------------- dispatch

    private void ScheduleDispatch(int delayMs)
    {
        var generation = Volatile.Read(ref _dispatchGeneration);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                if (generation != Volatile.Read(ref _dispatchGeneration))
                {
                    return; // a newer control/desired event rescheduled the dispatch
                }

                await DispatchAsync(generation).ConfigureAwait(false);
            }
            catch
            {
                // dispatch must never crash the engine; rejection paths log via events
            }
        });
    }

    private async Task DispatchAsync(int generation)
    {
        const int maxSettleAttempts = DesiredSettleMaxAttempts;
        for (var attempt = 0; ; attempt++)
        {
            ControlSnapshotV2? snapshot;
            SyncDesiredRevision? desired;
            long desiredGen;
            long controlGen;
            lock (_gate)
            {
                snapshot = _pendingSnapshot;
                desired = _pendingDesired;
                desiredGen = _pendingDesiredGen;
                controlGen = _pendingControlGen;
            }

            if (snapshot == null)
            {
                return;
            }

            // Desired and control can arrive a few SSE frames apart; wait for the
            // matching desired revision before acknowledging (TV "revision settle") —
            // but only when the desired actually belongs to this control arrival. A
            // desired that predates the pending snapshot is stale and would
            // otherwise burn the whole settle window (~1s) on every such write.
            if (ShouldWaitForDesired(desired, snapshot.RevisionId, desiredGen, controlGen))
            {
                if (attempt < maxSettleAttempts)
                {
                    await Task.Delay(DesiredSettleRetryMs).ConfigureAwait(false);
                    if (generation != Volatile.Read(ref _dispatchGeneration))
                    {
                        return;
                    }

                    continue;
                }

                // Settle timeout: control is validated and locally enforced already;
                // apply + ack anyway so the parent UI can recover from a stuck desired.
            }

            lock (_gate)
            {
                // The engine writes state/applied back into the same devices/{id} node it
                // listens to, so every apply echoes the full node (incl. control/v2) back as
                // a NEW snapshot object. Dedup on the control/v2 RAW content (not object
                // reference) so an identical echo is skipped, while a genuine parent change
                // (different raw/revision) still dispatches, and a same-revision correction
                // with different content still re-dispatches (raw differs).
                if (ReferenceEquals(snapshot, _lastDispatchedSnapshot) ||
                    (_pendingRaw != null && _pendingRaw == _lastDispatchedRaw))
                {
                    return; // this exact state was already dispatched
                }

                _lastDispatchedSnapshot = snapshot;
                _lastDispatchedRaw = _pendingRaw;
            }

            ControlApplied?.Invoke(snapshot);
            return;
        }
    }

    /// <summary>
    /// True only when a desired revision arrived after the pending control (a parent
    /// write still in flight whose two SSE frames may land apart). A desired that
    /// predates the pending control is stale — and waiting for it would delay
    /// every such apply by ~1s.
    /// </summary>
    internal static bool ShouldWaitForDesired(
        SyncDesiredRevision? desired, string snapshotRevisionId, long desiredGeneration, long controlGeneration)
    {
        return desired != null
            && desired.RevisionId != snapshotRevisionId
            && desiredGeneration > controlGeneration;
    }

    // ---------------------------------------------------------------- helpers

    private void PersistAppliedLocked()
    {
        var payload = new JsonObject
        {
            ["revisionId"] = _lastAppliedRevision,
            ["sessionId"] = _lastAppliedSessionId,
        };
        try
        {
            _secrets.Set(AppliedSecretKey, payload.ToJsonString());
        }
        catch
        {
            // markers only dedupe acks; in-memory copies remain authoritative
        }
    }

    private static string? ExtractRevisionId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            return ReadString(document.RootElement, "revisionId");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement parent, string name)
    {
        if (parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        return null;
    }

    private static long? ReadLong(JsonElement parent, string name)
    {
        if (parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static JsonObject ServerTimestamp()
    {
        return new JsonObject { [".sv"] = "timestamp" };
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
