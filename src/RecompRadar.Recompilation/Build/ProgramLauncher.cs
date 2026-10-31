using System.Text;
using System.Text.Json;

namespace RecompRadar.Recompilation.Build;

/// <summary>The run report a generated program writes with <c>--report</c>.</summary>
public sealed record RunReport(
    string Status,
    int ExitCode,
    long InstructionsExecuted,
    string? Message,
    string Output,
    IReadOnlyList<ulong> Registers,
    double ElapsedMilliseconds = 0);

public sealed record LaunchResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool Cancelled,
    TimeSpan Duration,
    RunReport? Report);

/// <summary>Starts a program that RecompRadar built. Only programs that RecompRadar generated itself are ever started; analyzed input files are never executed.</summary>
public static class ProgramLauncher
{
    public static async Task<LaunchResult> RunAsync(
        string executablePath,
        long? maxInstructions = null,
        TimeSpan? timeout = null,
        Action<string>? onStandardOutput = null,
        Action<string>? onStandardError = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("The program to start does not exist.", executablePath);
        }

        var reportPath = Path.Combine(Path.GetTempPath(), $"recompradar-report-{Guid.NewGuid():N}.json");
        var arguments = new List<string> { "--report", reportPath };
        if (maxInstructions is { } limit)
        {
            arguments.Add("--max-instructions");
            arguments.Add(limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        try
        {
            var result = await ProcessRunner.RunAsync(
                executablePath,
                arguments,
                Path.GetDirectoryName(Path.GetFullPath(executablePath))!,
                null,
                line =>
                {
                    lock (stdout)
                    {
                        stdout.Append(line).Append('\n');
                    }

                    onStandardOutput?.Invoke(line);
                },
                line =>
                {
                    lock (stderr)
                    {
                        stderr.Append(line).Append('\n');
                    }

                    onStandardError?.Invoke(line);
                },
                timeout ?? TimeSpan.FromSeconds(60),
                cancellationToken).ConfigureAwait(false);

            return new LaunchResult(result.ExitCode, stdout.ToString(), stderr.ToString(), result.TimedOut, result.Cancelled, result.Duration, ReadReport(reportPath));
        }
        finally
        {
            try
            {
                File.Delete(reportPath);
            }
            catch (IOException)
            {
                // A leftover temp file is harmless.
            }
        }
    }

    private static RunReport? ReadReport(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            return new RunReport(
                root.GetProperty("status").GetString() ?? "Unknown",
                root.GetProperty("exitCode").GetInt32(),
                root.GetProperty("instructionsExecuted").GetInt64(),
                root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String ? message.GetString() : null,
                root.GetProperty("output").GetString() ?? string.Empty,
                root.GetProperty("registers").EnumerateArray().Select(e => e.GetUInt64()).ToList(),
                root.TryGetProperty("elapsedMilliseconds", out var elapsed) ? elapsed.GetDouble() : 0);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IOException)
        {
            return null;
        }
    }
}
