namespace RecompRadar.Core.Model;

/// <summary>Metadata of a stored analysis. The analyzed file itself is never copied; only its path and hash are kept.</summary>
public sealed record ProjectSummary
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string FileName { get; init; } = "";
    public string FilePath { get; init; } = "";
    public long FileSizeBytes { get; init; }
    public string Sha256 { get; init; } = "";
    public DateTimeOffset AnalyzedAtUtc { get; init; }
    public string Architecture { get; init; } = "";
    public string FileFormat { get; init; } = "";
    public string OverallStatus { get; init; } = "";

    /// <summary>Short summary of warnings and notes from the analysis.</summary>
    public string Hints { get; init; } = "";

    /// <summary>Free-text notes written by the user.</summary>
    public string Notes { get; init; } = "";

    /// <summary>Path of the most recently exported report, if any.</summary>
    public string? ReportPath { get; init; }
}

public sealed record ProjectRecord(ProjectSummary Summary, AnalysisBundle Bundle);
