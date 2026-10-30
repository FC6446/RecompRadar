namespace RecompRadar.Core;

/// <summary>
/// Hard bounds that keep the analyzer safe against hostile or damaged input.
/// Exceeding a limit produces a diagnostic and a partial result, never a crash or an unbounded allocation.
/// </summary>
public sealed record AnalysisLimits
{
    public long MaxFileSizeBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public int MaxProgramHeaders { get; init; } = 65_535;
    public int MaxSectionHeaders { get; init; } = 65_535;
    public int MaxSymbolsPerTable { get; init; } = 500_000;
    public int MaxDynamicEntries { get; init; } = 4_096;
    public long MaxStringTableBytes { get; init; } = 64L * 1024 * 1024;
    public long MaxStringScanBytes { get; init; } = 64L * 1024 * 1024;
    public int MaxStringLength { get; init; } = 512;
    public int MaxKnowledgeMatches { get; init; } = 5_000;

    public static AnalysisLimits Default { get; } = new();
}
