using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Formatting.Compact;

namespace RecompRadar.Infrastructure;

public static class LoggingSetup
{
    /// <summary>
    /// Creates a logger factory that writes structured JSON lines (one event per line, Serilog compact format)
    /// to a daily rolling file in the logs folder. Message-template properties such as {FileName} become JSON fields.
    /// </summary>
    public static ILoggerFactory CreateLoggerFactory(AppPaths paths, bool verbose = false)
    {
        Directory.CreateDirectory(paths.Logs);
        var configuration = new LoggerConfiguration()
            .MinimumLevel.Is(verbose ? Serilog.Events.LogEventLevel.Debug : Serilog.Events.LogEventLevel.Information)
            .Enrich.WithProperty("Application", "RecompRadar")
            .WriteTo.File(
                new CompactJsonFormatter(),
                Path.Combine(paths.Logs, "recompradar-.jsonl"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true);

        var logger = configuration.CreateLogger();
        return LoggerFactory.Create(builder => builder.AddSerilog(logger, dispose: true));
    }
}
