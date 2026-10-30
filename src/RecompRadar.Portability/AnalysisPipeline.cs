using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RecompRadar.Architectures;
using RecompRadar.Architectures.Platforms;
using RecompRadar.BinaryAnalysis;
using RecompRadar.BinaryAnalysis.Dependencies;
using RecompRadar.BinaryAnalysis.Elf;
using RecompRadar.Core;
using RecompRadar.Core.Model;
using RecompRadar.Recompilation;

namespace RecompRadar.Portability;

/// <summary>
/// Orchestrates the static analysis of one file. The file is only read, never executed, and any problem with the input
/// becomes a diagnostic in the result instead of an exception.
/// </summary>
public sealed class AnalysisPipeline : IAnalysisPipeline
{
    private readonly ILogger _logger;
    private readonly IReadOnlyList<IPlatformHeuristic> _extraHeuristics;
    private readonly RecompilerPluginRegistry _plugins;
    private readonly TimeProvider _time;

    public AnalysisPipeline(
        ILogger<AnalysisPipeline>? logger = null,
        IEnumerable<IPlatformHeuristic>? extraHeuristics = null,
        RecompilerPluginRegistry? plugins = null,
        TimeProvider? timeProvider = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _extraHeuristics = extraHeuristics?.ToList() ?? [];
        _plugins = plugins ?? RecompilerPluginRegistry.Default;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<AnalysisBundle> AnalyzeAsync(
        string path,
        AnalysisOptions options,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var started = Stopwatch.StartNew();
        var diagnostics = new DiagnosticCollector();
        var now = _time.GetUtcNow();
        var identity = new FileIdentity(Path.GetFileName(path ?? string.Empty), path ?? string.Empty, 0, string.Empty);

        try
        {
            progress?.Report(new AnalysisProgress("Checking file", 0.02));
            if (!TryValidateFile(path, options.Limits, diagnostics, out var fullPath, out var size))
            {
                return Finish(identity, now, new FormatDetection(), null, null, null, null, diagnostics, options, started);
            }

            identity = new FileIdentity(Path.GetFileName(fullPath), fullPath, size, string.Empty);
            _logger.LogInformation("Analysis started for {FileName} ({SizeBytes} bytes)", identity.FileName, size);

            await using var stream = new FileStream(fullPath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                BufferSize = 64 * 1024,
                Options = FileOptions.None,
            });

            var hashProgress = new InlineProgress<double>(f => progress?.Report(new AnalysisProgress("Computing SHA-256", 0.05 + (f * 0.45))));
            var hash = await FileHasher.ComputeSha256Async(stream, hashProgress, cancellationToken).ConfigureAwait(false);
            identity = identity with { Sha256 = hash };

            progress?.Report(new AnalysisProgress("Identifying format", 0.52));
            var head = new byte[(int)Math.Min(FormatDetector.HeadBytesNeeded, size)];
            stream.Position = 0;
            await stream.ReadExactlyAsync(head, cancellationToken).ConfigureAwait(false);
            var format = FormatDetector.Detect(head);
            _logger.LogInformation("Format of {Sha256} detected as {FormatId}", hash, format.Id);

            if (!format.IsSupported)
            {
                diagnostics.Warn("FORMAT.UNSUPPORTED", $"Unsupported format: {format.DisplayName}.", format.Explanation);
                return Finish(identity, now, format, null, null, null, null, diagnostics, options, started);
            }

            progress?.Report(new AnalysisProgress("Parsing ELF structures", 0.58));
            var parse = await Task.Run(() => new ElfParser(options.Limits).Parse(stream, cancellationToken), cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(parse.Diagnostics);
            if (parse.Elf is null)
            {
                return Finish(identity, now, format, null, null, null, null, diagnostics, options, started);
            }

            var elf = parse.Elf;
            progress?.Report(new AnalysisProgress("Determining architecture", 0.72));
            var architecture = ArchitectureAnalyzer.Analyze(elf.Header, options.Target);
            var support = _plugins.FindSupporting(elf);
            architecture = architecture with
            {
                RecompilationSupport = support.Count == 0
                    ? "None (no decoder or translator plugin for this architecture)"
                    : string.Join("; ", support.Select(p => p.DisplayName)),
            };

            progress?.Report(new AnalysisProgress("Analyzing dependencies", 0.8));
            var knowledge = KnowledgeBase.Load(options.ExtraKnowledgeDirectory);
            var dependencies = await Task.Run(
                () => new DependencyAnalyzer(knowledge, options.Limits).Analyze(elf, stream, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            progress?.Report(new AnalysisProgress("Evaluating platform hints", 0.92));
            var platform = new PlatformAnalyzer(_extraHeuristics).Analyze(new PlatformContext(elf, architecture, dependencies), diagnostics);

            AddStructuralNotes(elf, diagnostics);

            progress?.Report(new AnalysisProgress("Building report", 0.97));
            return Finish(identity, now, format, elf, architecture, dependencies, platform, diagnostics, options, started);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Analysis of {FileName} was cancelled", identity.FileName);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Reading {FileName} failed", identity.FileName);
            diagnostics.Error("FILE.READ_FAILED", $"The file could not be read: {ex.Message}",
                "The file may be locked by another program, on a disconnected drive, or you may lack permission to read it.");
            return Finish(identity, now, new FormatDetection(), null, null, null, null, diagnostics, options, started);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected failure while analyzing {FileName}", identity.FileName);
            diagnostics.Error("ANALYSIS.INTERNAL_ERROR", $"Unexpected failure ({ex.GetType().Name}): {ex.Message}",
                "This is a bug in RecompRadar. The details are in the log file; please report them together with the file's SHA-256.");
            return Finish(identity, now, new FormatDetection(), null, null, null, null, diagnostics, options, started);
        }
    }

    /// <summary>Reports synchronously on the calling thread, unlike <see cref="Progress{T}"/> which posts to a captured context and may reorder.</summary>
    private sealed class InlineProgress<TValue>(Action<TValue> report) : IProgress<TValue>
    {
        public void Report(TValue value) => report(value);
    }

    private static bool TryValidateFile(string? path, AnalysisLimits limits, DiagnosticCollector diagnostics, out string fullPath, out long size)
    {
        fullPath = string.Empty;
        size = 0;
        if (string.IsNullOrWhiteSpace(path))
        {
            diagnostics.Error("FILE.NO_PATH", "No file was selected.", "Choose or drop an executable file to analyze.");
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            diagnostics.Error("FILE.BAD_PATH", $"The path is not valid: {ex.Message}", "Check the path for unsupported characters or an excessive length.");
            return false;
        }

        if (Directory.Exists(fullPath))
        {
            diagnostics.Error("FILE.IS_DIRECTORY", "The selected item is a folder, not a file.", "Select the executable file itself.");
            return false;
        }

        FileInfo info;
        try
        {
            info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                diagnostics.Error("FILE.NOT_FOUND", $"The file '{fullPath}' does not exist.", "It may have been moved, renamed or deleted, or the drive is not connected.");
                return false;
            }

            size = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Error("FILE.INACCESSIBLE", $"The file cannot be accessed: {ex.Message}", "Check that you have permission to read it.");
            return false;
        }

        if (size == 0)
        {
            diagnostics.Error("FILE.EMPTY", "The file is empty (0 bytes).", "An empty file contains no code. The copy or download may have failed.");
            return false;
        }

        if (size > limits.MaxFileSizeBytes)
        {
            diagnostics.Error("FILE.TOO_LARGE", $"The file is {Fmt.Size(size)}, which is above the configured limit of {Fmt.Size(limits.MaxFileSizeBytes)}.",
                "Raise the file size limit in Settings if you are sure this is an executable and not a disc image or archive.");
            return false;
        }

        return true;
    }

    private static void AddStructuralNotes(ElfFile elf, DiagnosticCollector diagnostics)
    {
        if (!elf.HasSymbolTable && !elf.HasDynamicSymbolTable)
        {
            diagnostics.Info("ELF.NO_SYMBOLS", "The file has no symbol tables.",
                "It is stripped, so function names are unavailable. Architecture, segments and entry point are still reliable.");
        }

        if (!elf.HasSectionHeaders && elf.HasProgramHeaders)
        {
            diagnostics.Info("ELF.NO_SECTIONS", "The file has program headers but no section headers.",
                "Section headers are optional at run time and are often removed. Segments still describe what is loaded.");
        }

        if (elf.Header.Type == ElfConstants.ET_REL)
        {
            diagnostics.Info("ELF.RELOCATABLE", "The file is a relocatable object, not a finished program.",
                "Addresses are not final until it is linked.");
        }
    }

    private AnalysisBundle Finish(
        FileIdentity identity,
        DateTimeOffset now,
        FormatDetection format,
        ElfFile? elf,
        ArchitectureAssessment? architecture,
        DependencyAnalysis? dependencies,
        PlatformAssessment? platform,
        DiagnosticCollector diagnostics,
        AnalysisOptions options,
        Stopwatch started)
    {
        var result = new AnalysisResult
        {
            ToolVersion = ToolInfo.Version,
            File = identity,
            AnalyzedAtUtc = now,
            Format = format,
            Parsed = elf is not null,
            Elf = elf,
            Architecture = architecture,
            Platform = platform,
            Dependencies = dependencies,
            Diagnostics = diagnostics.Build(),
        };

        var report = PortabilityReportBuilder.Build(result, options.Target, now);
        _logger.LogInformation(
            "Analysis finished for {FileName} in {ElapsedMs} ms: parsed={Parsed}, architecture={Architecture}, status={Status}, warnings={WarningCount}",
            identity.FileName,
            started.ElapsedMilliseconds,
            result.Parsed,
            architecture?.DisplayName ?? "n/a",
            report.StatusCode,
            report.Warnings.Count);
        return new AnalysisBundle(result, report);
    }
}
