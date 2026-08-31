namespace GuardPulse.Agent.Core;

/// <summary>
/// agent-config.json values (installer copies the file next to the binaries).
/// apiKey/projectId/databaseUrl are required; the rest is optional seeding.
/// </summary>
public sealed record AgentConfig(
    string ApiKey,
    string ProjectId,
    string DatabaseUrl,
    string? DeviceId = null,
    string? RefreshToken = null,
    string? ExemptAccount = null,
    string? LogLevel = null);
