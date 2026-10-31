using System.Security.Cryptography;
using System.Text.RegularExpressions;
using RecompRadar.Recompilation.Generation;

namespace RecompRadar.Recompilation.Build;

/// <summary>What kind of program a build produced. The categories are never mixed up: the label says what actually runs.</summary>
public enum BuildCategory
{
    /// <summary>Guest code compiled ahead of time into machine code, with no emulation, JIT or interpreter for it, and no runtime layer beyond the host OS.</summary>
    FullyNative,

    /// <summary>Guest code translated ahead of time into host code (here: C#, compiled to .NET IL and JIT-compiled at run time) with bundled runtime support.</summary>
    TranslatedWithRuntime,

    /// <summary>Translated code plus a compatibility layer that re-implements the console's operating system and hardware interfaces.</summary>
    NativeWithCompatibilityLayer,

    /// <summary>The program carries an interpreter that executes the guest code.</summary>
    Interpreted,

    Unsupported,
}

public static class BuildCategoryText
{
    public static string Title(this BuildCategory category) => category switch
    {
        BuildCategory.FullyNative => "Fully native (ahead-of-time machine code)",
        BuildCategory.TranslatedWithRuntime => "Translated code with runtime (JIT)",
        BuildCategory.NativeWithCompatibilityLayer => "Native code with compatibility layer",
        BuildCategory.Interpreted => "Interpreted",
        _ => "Unsupported",
    };

    public static string Explanation(this BuildCategory category) => category switch
    {
        BuildCategory.FullyNative => "The guest code was compiled into machine code ahead of time; nothing emulates it.",
        BuildCategory.TranslatedWithRuntime =>
            "The guest code was translated ahead of time into C# and compiled to .NET IL. The .NET JIT turns it into machine code when the program starts, " +
            "so it needs the .NET 10 runtime. The program carries a small runtime layer for memory and the RecompRadar test system calls. It is not ahead-of-time machine code.",
        BuildCategory.NativeWithCompatibilityLayer => "Translated code runs on top of a re-implemented console system layer.",
        BuildCategory.Interpreted => "The program contains an interpreter that executes the guest code.",
        _ => "No program can be produced.",
    };
}

public sealed record BuildDiagnostic(string Severity, string Code, string Message, string? File, int Line);

public sealed class BuildResult
{
    public required bool Success { get; init; }

    public required string ProjectDirectory { get; init; }

    public string? ExecutablePath { get; init; }

    public string? AssemblyPath { get; init; }

    public string? ExecutableSha256 { get; init; }

    public string? AssemblySha256 { get; init; }

    public string? SdkVersion { get; init; }

    public TimeSpan Duration { get; init; }

    public IReadOnlyList<BuildDiagnostic> Diagnostics { get; init; } = [];

    public IReadOnlyList<string> Log { get; init; } = [];

    public string? FailureReason { get; init; }

    public int ErrorCount => Diagnostics.Count(d => d.Severity == "error");

    public int WarningCount => Diagnostics.Count(d => d.Severity == "warning");
}

/// <summary>Builds a generated project with the .NET SDK (<c>dotnet build</c>). No packages are downloaded for these projects.</summary>
public static partial class ProjectBuilder
{
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);

    public static async Task<BuildResult> BuildAsync(
        GeneratedProject project,
        string projectDirectory,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);

        var sdk = DotnetSdk.Locate();
        if (sdk is null)
        {
            return new BuildResult
            {
                Success = false,
                ProjectDirectory = projectDirectory,
                FailureReason = $"No .NET SDK {DotnetSdk.MinimumMajorVersion} or newer was found. Install it from https://dotnet.microsoft.com/download to build programs.",
            };
        }

        projectDirectory = Path.GetFullPath(projectDirectory);
        var outputDirectory = Path.Combine(projectDirectory, "out");
        if (Directory.Exists(outputDirectory))
        {
            Directory.Delete(outputDirectory, recursive: true);
        }

        project.WriteTo(projectDirectory);
        var lines = new List<string>();
        void Collect(string line)
        {
            lock (lines)
            {
                lines.Add(line);
            }

            log?.Report(line);
        }

        Collect($"dotnet {sdk.Version}: building {project.ProjectFile}");
        var environment = new Dictionary<string, string>
        {
            ["DOTNET_CLI_UI_LANGUAGE"] = "en",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["VSLANG"] = "1033",
        };
        var arguments = new[]
        {
            "build", project.ProjectFile, "-c", "Release", "-o", outputDirectory, "--nologo", "-v:minimal", "--disable-build-servers",
        };

        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(sdk.DotnetPath, arguments, projectDirectory, environment, Collect, Collect, BuildTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new BuildResult { Success = false, ProjectDirectory = projectDirectory, SdkVersion = sdk.Version, Log = lines, FailureReason = $"The .NET SDK could not be started: {ex.Message}" };
        }

        var diagnostics = ParseDiagnostics(lines);
        var exeName = project.ProjectName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
        var exePath = Path.Combine(outputDirectory, exeName);
        var dllPath = Path.Combine(outputDirectory, project.ProjectName + ".dll");

        string? failure = null;
        if (result.Cancelled)
        {
            failure = "The build was cancelled.";
        }
        else if (result.TimedOut)
        {
            failure = $"The build did not finish within {BuildTimeout.TotalMinutes:0} minutes and was stopped.";
        }
        else if (result.ExitCode != 0)
        {
            failure = $"The build failed with {diagnostics.Count(d => d.Severity == "error")} error(s) (dotnet exit code {result.ExitCode}).";
        }
        else if (!File.Exists(exePath) || !File.Exists(dllPath))
        {
            failure = "The build reported success but the expected program was not produced.";
        }

        return new BuildResult
        {
            Success = failure is null,
            ProjectDirectory = projectDirectory,
            ExecutablePath = failure is null ? exePath : null,
            AssemblyPath = failure is null ? dllPath : null,
            ExecutableSha256 = failure is null ? Sha256(exePath) : null,
            AssemblySha256 = failure is null ? Sha256(dllPath) : null,
            SdkVersion = sdk.Version,
            Duration = result.Duration,
            Diagnostics = diagnostics,
            Log = lines,
            FailureReason = failure,
        };
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    [GeneratedRegex(@"^(?:(?<file>.+?)\((?<line>\d+),\d+(?:,\d+,\d+)?\): )?(?<sev>error|warning) (?<code>[A-Za-z]+\d+): (?<msg>.*?)(?: \[[^\]]+\])?$")]
    private static partial Regex DiagnosticPattern();

    private static List<BuildDiagnostic> ParseDiagnostics(IEnumerable<string> lines)
    {
        var result = new List<BuildDiagnostic>();
        var seen = new HashSet<string>();
        foreach (var line in lines.ToArray())
        {
            var match = DiagnosticPattern().Match(line.Trim());
            if (!match.Success || !seen.Add(line.Trim()))
            {
                continue;
            }

            result.Add(new BuildDiagnostic(
                match.Groups["sev"].Value,
                match.Groups["code"].Value,
                match.Groups["msg"].Value,
                match.Groups["file"].Success ? Path.GetFileName(match.Groups["file"].Value) : null,
                match.Groups["line"].Success ? int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture) : 0));
        }

        return result;
    }
}
