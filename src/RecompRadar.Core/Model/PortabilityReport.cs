namespace RecompRadar.Core.Model;

public enum PortingStatus
{
    /// <summary>The input could not be analyzed (unsupported, damaged or protected).</summary>
    NotAnalyzable,

    /// <summary>Not enough verified information to say more.</summary>
    FurtherInvestigationRequired,

    /// <summary>The CPU instruction set matches the target; OS/library/hardware compatibility is still open.</summary>
    InstructionSetMatches,

    /// <summary>The CPU instruction set differs from the target; translation plus a runtime layer would be needed.</summary>
    TranslationRequired,
}

public sealed record ReportQuestion(int Number, string Question, string Answer, IReadOnlyList<string> Basis);

public sealed record RuntimeComponent(string Name, string Reason, Evidence Evidence);

public sealed record WorkStep(int Order, string Title, string Description);

public sealed record PortabilityReport
{
    public int SchemaVersion { get; init; } = 1;
    public string ToolVersion { get; init; } = "";
    public DateTimeOffset GeneratedAtUtc { get; init; }

    public string Target { get; init; } = "Windows x86-64";
    public string SourceArchitecture { get; init; } = "Unknown";
    public string CpuTranslation { get; init; } = "Unknown";
    public string SystemApiCompatibility { get; init; } = "Unknown";
    public string GraphicsCompatibility { get; init; } = "Unknown";
    public string OverallPortingStatus { get; init; } = "Further investigation required";
    public PortingStatus StatusCode { get; init; } = PortingStatus.FurtherInvestigationRequired;

    public IReadOnlyList<ReportQuestion> Questions { get; init; } = [];
    public IReadOnlyList<Fact> Findings { get; init; } = [];
    public IReadOnlyList<string> MissingInformation { get; init; } = [];
    public IReadOnlyList<RuntimeComponent> RuntimeComponents { get; init; } = [];
    public IReadOnlyList<WorkStep> WorkSteps { get; init; } = [];
    public IReadOnlyList<Diagnostic> Warnings { get; init; } = [];

    public string Disclaimer { get; init; } =
        "RecompRadar performs static analysis only. It does not execute the analyzed file, does not decrypt protected " +
        "files and does not estimate compatibility percentages. Statements marked 'Inferred' are heuristics, not proof.";
}

/// <summary>An analysis together with the report derived from it. This is what gets exported and stored.</summary>
public sealed record AnalysisBundle(AnalysisResult Analysis, PortabilityReport Report);
