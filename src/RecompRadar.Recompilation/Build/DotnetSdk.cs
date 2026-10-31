using System.Diagnostics;

namespace RecompRadar.Recompilation.Build;

public sealed record DotnetSdkInfo(string DotnetPath, string Version);

/// <summary>Finds the .NET SDK that builds the generated programs. Nothing is installed or downloaded.</summary>
public static class DotnetSdk
{
    public const int MinimumMajorVersion = 10;

    private static readonly Lazy<DotnetSdkInfo?> Cached = new(Probe);

    public static DotnetSdkInfo? Locate() => Cached.Value;

    private static DotnetSdkInfo? Probe()
    {
        foreach (var candidate in Candidates())
        {
            var version = TryGetVersion(candidate);
            if (version is not null && Version.TryParse(version.Split('-')[0], out var parsed) && parsed.Major >= MinimumMajorVersion)
            {
                return new DotnetSdkInfo(candidate, version);
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(root))
        {
            yield return Path.Combine(root, ExecutableName);
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
        {
            yield return Path.Combine(programFiles, "dotnet", ExecutableName);
        }

        yield return "dotnet";
    }

    private static string ExecutableName => OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

    private static string? TryGetVersion(string dotnet)
    {
        try
        {
            var info = new ProcessStartInfo(dotnet, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
            info.Environment["DOTNET_NOLOGO"] = "1";
            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(15_000))
            {
                process.Kill();
                return null;
            }

            return process.ExitCode == 0 && output.Length > 0 ? output.Split('\n')[0].Trim() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
