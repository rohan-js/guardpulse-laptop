namespace GuardPulse.Agent.Core;

using System.Text.Json;

/// <summary>Loads <c>agent-config.json</c>. Throws FileNotFoundException / InvalidDataException on failure.</summary>
public static class AgentConfigLoader
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static AgentConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("agent-config.json was not found.", path);
        }

        AgentConfig config;
        try
        {
            config = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), Options)
                     ?? throw new InvalidDataException($"agent-config.json at {path} is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"agent-config.json at {path} is not valid JSON.", ex);
        }

        if (IsBlank(config.ApiKey) || IsBlank(config.ProjectId) || IsBlank(config.DatabaseUrl))
        {
            throw new InvalidDataException($"agent-config.json at {path} must define apiKey, projectId and databaseUrl.");
        }

        // Coherence guard: every Firebase RTDB URL embeds the project id as the
        // first hostname label — firebaseio.com uses "<projectId>.firebaseio.com",
        // regional instances use "<projectId>-default-rtdb.<region>.firebasedatabase.app".
        // A mismatch — e.g. a US-project apiKey paired with a Singapore databaseUrl —
        // parses fine, signs in fine, and then fails EVERY cloud write with 401
        // while the service looks alive locally (the 2026-09-09 cutover incident).
        // Fail at load instead.
        var host = new Uri(config.DatabaseUrl).Host;
        if (!host.StartsWith(config.ProjectId + ".", StringComparison.OrdinalIgnoreCase)
            && !host.StartsWith(config.ProjectId + "-", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"agent-config.json at {path} is incoherent: databaseUrl host '{host}' does not belong to project '{config.ProjectId}'.");
        }

        return config with { DatabaseUrl = config.DatabaseUrl.TrimEnd('/') };
    }

    private static bool IsBlank(string? value)
    {
        return value == null || value.Trim().Length == 0;
    }
}
