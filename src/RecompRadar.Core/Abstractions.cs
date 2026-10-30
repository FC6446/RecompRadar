using RecompRadar.Core.Model;

namespace RecompRadar.Core;

public sealed record AnalysisProgress(string Stage, double Fraction);

/// <summary>The system a port would target. Reports are always written relative to this target.</summary>
public sealed record PortabilityTarget(string OperatingSystem, string Architecture)
{
    public static PortabilityTarget WindowsX64 { get; } = new("Windows", "x86-64");
    public string DisplayName => $"{OperatingSystem} {Architecture}";
}

public sealed record AnalysisOptions
{
    public AnalysisLimits Limits { get; init; } = AnalysisLimits.Default;
    public PortabilityTarget Target { get; init; } = PortabilityTarget.WindowsX64;

    /// <summary>Optional extra directory with knowledge-base JSON files (*.json) to merge with the built-in database.</summary>
    public string? ExtraKnowledgeDirectory { get; init; }
}

/// <summary>Runs the full static analysis of one file: identification, parsing, architecture, dependencies, report.</summary>
public interface IAnalysisPipeline
{
    Task<AnalysisBundle> AnalyzeAsync(
        string path,
        AnalysisOptions options,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Everything a platform heuristic may look at.</summary>
public sealed record PlatformContext(
    ElfFile Elf,
    ArchitectureAssessment Architecture,
    DependencyAnalysis Dependencies);

/// <summary>
/// Extension point for recognising a target system from verified binary properties.
/// Implementations must only return candidates they can justify with <see cref="PlatformCandidate.Reasons"/>.
/// </summary>
public interface IPlatformHeuristic
{
    string PlatformId { get; }
    PlatformCandidate? Evaluate(PlatformContext context);
}

/// <summary>Renders an analysis bundle into a file format.</summary>
public interface IReportExporter
{
    string FormatId { get; }
    string DisplayName { get; }
    string FileExtension { get; }
    string Render(AnalysisBundle bundle);
}

public interface IProjectStore
{
    Task<ProjectSummary> SaveAsync(AnalysisBundle bundle, string? name, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectSummary>> ListAsync(CancellationToken cancellationToken = default);
    Task<ProjectRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> RenameAsync(Guid id, string name, CancellationToken cancellationToken = default);
    Task<bool> UpdateNotesAsync(Guid id, string notes, CancellationToken cancellationToken = default);
    Task<bool> SetReportPathAsync(Guid id, string reportPath, CancellationToken cancellationToken = default);
}

public sealed record AppSettings
{
    public int MaxFileSizeMiB { get; init; } = 2048;
    public bool AutoSaveProjects { get; init; } = true;
    public bool EnableAnimations { get; init; } = true;
    public string? ExportDirectory { get; init; }
    public string? ExtraKnowledgeDirectory { get; init; }

    public AnalysisLimits ToLimits() =>
        AnalysisLimits.Default with { MaxFileSizeBytes = Math.Clamp(MaxFileSizeMiB, 1, 16_384) * 1024L * 1024L };
}

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}
