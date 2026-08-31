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

        return config with { DatabaseUrl = config.DatabaseUrl.TrimEnd('/') };
    }

    private static bool IsBlank(string? value)
    {
        return value == null || value.Trim().Length == 0;
    }
}
