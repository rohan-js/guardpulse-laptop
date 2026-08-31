// Device Service host entry point: generic host + Windows Service lifetime,
// console + rolling file logging to %ProgramData%\GuardPulse\Laptop\logs\service-{yyyyMMdd}.log.

using GuardPulse.Agent.Core;
using GuardPulse.Agent.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// A Windows service starts with cwd = system32; content root must be next to the exe
// so agent-config.json resolves correctly.
builder.Environment.ContentRootPath = AppContext.BaseDirectory;

// UseWindowsService() equivalent for HostApplicationBuilder: runs as a Windows service
// when started by the SCM, as a plain console app otherwise.
builder.Services.AddWindowsService(options => { options.ServiceName = "GuardPulseDeviceService"; });

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System", LogLevel.Warning);
builder.Logging.AddProvider(FileLoggerProvider.Create());

builder.Services.AddHostedService<AgentHostedService>();

await builder.Build().RunAsync();

// --------------------------------------------------------------------- support

internal static class StatePaths
{
    public static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GuardPulse", "Laptop");

    public static string LogsDirectory => Path.Combine(Root, "logs");
}

internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly LogLevel _minimumLevel;

    private FileLoggerProvider(string directory, LogLevel minimumLevel)
    {
        _directory = directory;
        _minimumLevel = minimumLevel;
        Directory.CreateDirectory(directory);
    }

    public static FileLoggerProvider Create()
    {
        // File log defaults to Warning (quiet disk churn); agent-config.json
        // "logLevel" (trace/debug/information/warning/error) raises or lowers it.
        var level = LogLevel.Warning;
        try
        {
            var config = AgentConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "agent-config.json"));
            if (!string.IsNullOrWhiteSpace(config.LogLevel)
                && Enum.TryParse(config.LogLevel.Trim(), ignoreCase: true, out LogLevel parsed))
            {
                level = parsed;
            }
        }
        catch (Exception)
        {
            // missing/invalid config: keep the quiet default
        }

        return new FileLoggerProvider(StatePaths.LogsDirectory, level);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _directory, _minimumLevel);

    public void Dispose()
    {
        // nothing tracked
    }
}

internal sealed class FileLogger(string category, string directory, LogLevel minimumLevel) : ILogger
{
    private static readonly object Gate = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {category}: {formatter(state, exception)}" +
                       (exception is null ? "" : $"{Environment.NewLine}    {exception.GetType().Name}: {exception.Message.ReplaceLineEndings(" | ")}") +
                       Environment.NewLine;
            var path = Path.Combine(directory, $"service-{DateTime.Now:yyyyMMdd}.log");
            lock (Gate)
            {
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // logging must never crash the service
        }
    }
}
