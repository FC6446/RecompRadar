using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using RecompRadar.Core;
using RecompRadar.Core.Model;
using RecompRadar.Recompilation.Analysis;
using RecompRadar.Recompilation.Build;
using RecompRadar.Recompilation.Compatibility;
using RecompRadar.Recompilation.Execution;
using RecompRadar.Recompilation.Generation;

namespace RecompRadar.Recompilation.Pipeline;

public enum PipelineStage
{
    Prepare,
    Analyze,
    Compatibility,
    Generate,
    Build,
    Validate,
    Report,
}

public sealed record PipelineProgress(PipelineStage Stage, string Message);

public sealed record RecompileRequest(
    string SourcePath,
    ElfFile Elf,
    string OutputDirectory,
    PlatformAssessment? DetectedPlatform = null,
    string? PlatformOverride = null,
    bool AllowIncomplete = false,
    bool Validate = true,
    long ValidationInstructionLimit = 50_000_000,
    string? ProjectName = null,
    RecompilerPluginRegistry? Plugins = null);

public sealed record StageOutcome(PipelineStage Stage, bool Succeeded, TimeSpan Duration, string Summary);

public sealed class RecompileResult
{
    public required bool Success { get; init; }

    public required BuildCategory Category { get; init; }

    public string? FailureReason { get; init; }

    public string? SourceSha256 { get; init; }

    public ProgramGraph? Graph { get; init; }

    public CompatibilityAssessment? Compatibility { get; init; }

    public GenerationStats? Generation { get; init; }

    public BuildResult? Build { get; init; }

    public ValidationResult? Validation { get; init; }

    public string? ValidationSkippedReason { get; init; }

    public bool Incomplete { get; init; }

    public required IReadOnlyList<StageOutcome> Stages { get; init; }

    public string? ProjectDirectory { get; init; }

    public string? ExecutablePath { get; init; }

    public string? ReportPath { get; set; }

    /// <summary>Null when the program may be started; otherwise the exact reason it may not.</summary>
    public string? PlayBlockedReason
    {
        get
        {
            if (Build is not { Success: true } || ExecutablePath is null)
            {
                return FailureReason ?? "No program has been built yet.";
            }

            if (Validation is { Passed: false })
            {
                return "The built program behaves differently from the reference interpreter, so it is not started. " + string.Join(" ", Validation.Differences.Take(3));
            }

            return Compatibility?.RunBlockedReason;
        }
    }

    public bool CanPlay => PlayBlockedReason is null;
}

/// <summary>
/// The whole recompilation: check the file, analyze the code, assess compatibility, generate C#, build a Windows program,
/// validate it against the reference interpreter and write a build report. The input file is only read, never executed.
/// </summary>
public static class RecompilationPipeline
{
    public const string ReportFileName = "build-report.json";

    public static async Task<RecompileResult> RunAsync(RecompileRequest request, IProgress<PipelineProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stages = new List<StageOutcome>();
        var stopwatch = new Stopwatch();

        void Report(PipelineStage stage, string message) => progress?.Report(new PipelineProgress(stage, message));

        void Done(PipelineStage stage, bool ok, string summary)
        {
            stages.Add(new StageOutcome(stage, ok, stopwatch.Elapsed, summary));
            Report(stage, (ok ? "done: " : "failed: ") + summary);
            stopwatch.Restart();
        }

        RecompileResult Failed(string reason, ProgramGraph? graph = null, CompatibilityAssessment? compat = null, string? sha = null, BuildCategory? category = null) => new()
        {
            Success = false,
            Category = category ?? BuildCategory.Unsupported,
            FailureReason = reason,
            Graph = graph,
            Compatibility = compat,
            SourceSha256 = sha,
            Stages = stages,
        };

        stopwatch.Start();

        // ---- Prepare ----
        Report(PipelineStage.Prepare, "Reading the input file and selecting a backend");
        string sha256;
        IRecompilerPlugin? plugin;
        LoadedImage image;
        try
        {
            using var hashStream = File.OpenRead(request.SourcePath);
            sha256 = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            plugin = (request.Plugins ?? RecompilerPluginRegistry.Default).FindSupporting(request.Elf)
                .FirstOrDefault(p => p.CreateLoader() is not null && p.CreateTranslator() is not null);
            if (plugin is null)
            {
                Done(PipelineStage.Prepare, false, "no recompiler plugin supports this file");
                var compat = CompatibilityEngine.Assess(new CompatibilityInput(request.Elf, request.DetectedPlatform, request.PlatformOverride));
                return Failed(compat.RecompileBlockedReason ?? "No recompiler plugin supports this file.", null, compat, sha256);
            }

            image = LoadImage(plugin, request);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BinaryLoadException or InvalidOperationException)
        {
            Done(PipelineStage.Prepare, false, ex.Message);
            return Failed($"The file could not be loaded: {ex.Message}");
        }

        Done(PipelineStage.Prepare, true, $"{plugin.DisplayName}; {image.Segments.Count} segment(s), entry point 0x{image.EntryPoint:X8}");

        // ---- Analyze ----
        cancellationToken.ThrowIfCancellationRequested();
        Report(PipelineStage.Analyze, "Discovering blocks, functions and calls");
        var translator = plugin.CreateTranslator()!;
        var graph = await Task.Run(
            () => ProgramAnalyzer.Analyze(AnalysisInputFactory.FromImage(image, request.Elf, translator, plugin.NoReturnSystemCalls), null, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        Done(PipelineStage.Analyze, true,
            $"{graph.Functions.Count} function(s), {graph.Blocks.Count} block(s), {graph.InstructionCount} instruction(s); {graph.UnsupportedInstructions.Count} unsupported, {graph.IndirectJumps.Count} unresolved jump(s)");

        // ---- Compatibility ----
        var compatibility = CompatibilityEngine.Assess(new CompatibilityInput(request.Elf, request.DetectedPlatform, request.PlatformOverride, graph));
        var mayBuild = compatibility.CanRecompile || (request.AllowIncomplete && compatibility.CanRecompileIncomplete);
        Done(PipelineStage.Compatibility, mayBuild, $"{compatibility.Level}: {compatibility.Headline}");
        if (!mayBuild)
        {
            return Failed(compatibility.RecompileBlockedReason ?? "The compatibility check does not allow a build.", graph, compatibility, sha256);
        }

        var incomplete = !compatibility.CanRecompile;

        // ---- Generate ----
        cancellationToken.ThrowIfCancellationRequested();
        Report(PipelineStage.Generate, "Translating blocks to C#");
        var projectName = CSharpGenerator.SanitizeName(request.ProjectName ?? Path.GetFileNameWithoutExtension(request.SourcePath));
        GeneratedProject project;
        try
        {
            // The image is loaded again so that the program starts with the file's original memory contents.
            project = CSharpGenerator.Generate(
                graph,
                LoadImage(plugin, request),
                new GenerationOptions(projectName, Path.GetFileName(request.SourcePath), sha256, graph.Functions.Count > 0 ? ArchitectureName(request.Elf) : "unknown", ToolInfo.Version, incomplete));
        }
        catch (GenerationException ex)
        {
            Done(PipelineStage.Generate, false, ex.Message);
            return Failed(ex.Message, graph, compatibility, sha256);
        }

        Done(PipelineStage.Generate, true, $"{project.Stats.Blocks} block(s), {project.Stats.IrInstructions} IR instruction(s), {project.Stats.GeneratedSourceBytes:N0} bytes of C#");

        // ---- Build ----
        cancellationToken.ThrowIfCancellationRequested();
        Report(PipelineStage.Build, "Building with the .NET SDK");
        var buildLog = new Progress<string>(line => Report(PipelineStage.Build, line));
        var build = await ProjectBuilder.BuildAsync(project, request.OutputDirectory, buildLog, cancellationToken).ConfigureAwait(false);
        Done(PipelineStage.Build, build.Success, build.Success ? $"{Path.GetFileName(build.ExecutablePath)}, SHA-256 {build.ExecutableSha256![..16]}..." : build.FailureReason ?? "build failed");
        if (!build.Success)
        {
            var failed = new RecompileResult
            {
                Success = false,
                Category = BuildCategory.Unsupported,
                FailureReason = build.FailureReason,
                SourceSha256 = sha256,
                Graph = graph,
                Compatibility = compatibility,
                Generation = project.Stats,
                Build = build,
                Incomplete = incomplete,
                Stages = stages,
                ProjectDirectory = build.ProjectDirectory,
            };
            return failed;
        }

        // ---- Validate ----
        ValidationResult? validation = null;
        string? skipped = null;
        if (!request.Validate)
        {
            skipped = "Validation was switched off.";
            Done(PipelineStage.Validate, true, skipped);
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(PipelineStage.Validate, "Running the built program and the reference interpreter on the same input");
            var native = await ProgramLauncher.RunAsync(build.ExecutablePath!, request.ValidationInstructionLimit, TimeSpan.FromMinutes(2), null, null, cancellationToken)
                .ConfigureAwait(false);
            if (native.Report is null)
            {
                skipped = native.TimedOut
                    ? "The built program did not finish within two minutes, so it could not be compared."
                    : $"The built program did not write a run report (exit code {native.ExitCode}).";
                Done(PipelineStage.Validate, false, skipped);
            }
            else
            {
                var reference = await Task.Run(() => RunReference(plugin, request, request.ValidationInstructionLimit), cancellationToken).ConfigureAwait(false);
                validation = DifferentialValidator.Compare(reference, native.Report);
                Done(PipelineStage.Validate, validation.Passed,
                    validation.Passed
                        ? $"identical to the reference interpreter for this run ({reference.InstructionsExecuted:N0} guest instructions, status {reference.Status})"
                        : $"{validation.Differences.Count} difference(s): {validation.Differences[0]}");
            }
        }

        var success = validation?.Passed ?? !request.Validate;
        var result = new RecompileResult
        {
            Success = success,
            Category = BuildCategory.TranslatedWithRuntime,
            FailureReason = success ? null : validation is { Passed: false } ? "The built program differs from the reference interpreter." : skipped,
            SourceSha256 = sha256,
            Graph = graph,
            Compatibility = compatibility,
            Generation = project.Stats,
            Build = build,
            Validation = validation,
            ValidationSkippedReason = skipped,
            Incomplete = incomplete,
            Stages = stages,
            ProjectDirectory = build.ProjectDirectory,
            ExecutablePath = build.ExecutablePath,
        };

        // ---- Report ----
        var reportPath = Path.Combine(build.ProjectDirectory, ReportFileName);
        await File.WriteAllBytesAsync(reportPath, JsonSerializer.SerializeToUtf8Bytes(BuildReportWriter.Create(request, result), RecompRadarJson.Options), cancellationToken).ConfigureAwait(false);
        Done(PipelineStage.Report, true, ReportFileName);
        result.ReportPath = reportPath;
        return result;
    }

    private static LoadedImage LoadImage(IRecompilerPlugin plugin, RecompileRequest request)
    {
        using var stream = File.OpenRead(request.SourcePath);
        return plugin.CreateLoader()!.Load(stream, request.Elf);
    }

    private static ExecutionResult RunReference(IRecompilerPlugin plugin, RecompileRequest request, long limit)
    {
        var machine = GuestMachine.FromImage(LoadImage(plugin, request));
        return plugin.CreateBackend()!.Run(machine, plugin.CreateTranslator()!, plugin.CreateRuntime()!, new ExecutionLimits(limit));
    }

    private static string ArchitectureName(ElfFile elf) =>
        elf.Header.Machine == ElfConstants.EM_MIPS ? $"MIPS{(elf.Header.Class == ElfClass.Elf64 ? 64 : 32)}" : $"machine {elf.Header.Machine}";
}
