namespace RecompRadar.Core.Model;

public enum CpuFamily
{
    Unknown,
    X86,
    X86_64,
    Mips,
    PowerPc,
    Arm,
    Arm64,
    RiscV,
    Other,
}

public enum NativeCompatibility
{
    /// <summary>The CPU instruction set matches the host. This says nothing about OS, library or hardware compatibility.</summary>
    InstructionSetMatches,

    /// <summary>The CPU instruction set differs from the host and would need translation or emulation.</summary>
    NotDirectlyCompatible,

    Unknown,
}

public enum HintStrength
{
    Weak = 1,
    Moderate = 2,
    Strong = 3,
}

public sealed record FileIdentity(string FileName, string FullPath, long SizeBytes, string Sha256);

/// <summary>Result of recognising the container format of the input from magic numbers.</summary>
public sealed record FormatDetection
{
    /// <summary>Stable id such as "elf", "ps-x-exe", "sce-self", "unknown".</summary>
    public string Id { get; init; } = "unknown";

    public string DisplayName { get; init; } = "Unknown";

    /// <summary>True when RecompRadar can analyze this format structurally.</summary>
    public bool IsSupported { get; init; }

    public string Explanation { get; init; } = "";
}

public sealed record ArchitectureAssessment
{
    public CpuFamily Family { get; init; }
    public string DisplayName { get; init; } = "Unknown";
    public string Description { get; init; } = "";
    public int PointerBits { get; init; }
    public Endianness Endianness { get; init; }
    public string BinaryFormat { get; init; } = "ELF";

    /// <summary>Properties read from the file (machine, class, flags). All of these are verified.</summary>
    public IReadOnlyList<Fact> Facts { get; init; } = [];

    public string HostArchitecture { get; init; } = "x86-64";
    public NativeCompatibility Compatibility { get; init; }
    public string NativeExecutionStatus { get; init; } = "Unknown";
    public string RequiredWork { get; init; } = "";

    /// <summary>Notes about instruction set extensions or ABI details that matter for translation.</summary>
    public IReadOnlyList<string> TechnicalNotes { get; init; } = [];

    /// <summary>Whether RecompRadar ships a decoder/translator plugin for this architecture.</summary>
    public string RecompilationSupport { get; init; } = "None";
}

public sealed record PlatformCandidate
{
    public string PlatformId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public HintStrength Strength { get; init; }

    /// <summary>The individual observations behind the hint. Each says whether it was read from the file or inferred.</summary>
    public IReadOnlyList<Fact> Reasons { get; init; } = [];

    public IReadOnlyList<string> Caveats { get; init; } = [];
}

public sealed record PlatformAssessment
{
    public IReadOnlyList<PlatformCandidate> Candidates { get; init; } = [];

    /// <summary>One-line statement for the UI, e.g. "Possibly PlayStation 2".</summary>
    public string Summary { get; init; } = "Target system could not be determined";

    /// <summary>The target system is never proven by a plain ELF file; this is always stated.</summary>
    public string Disclaimer { get; init; } =
        "A recognised ELF format does not identify the console. These are hints derived from header fields, " +
        "memory layout and symbol names; they are not proof.";
}

public enum ApiMatchKind
{
    Library,
    ImportedSymbol,
    DefinedSymbol,
    String,
}

public sealed record ApiMatch
{
    public ApiMatchKind Kind { get; init; }

    /// <summary>The library name, symbol name or string that matched.</summary>
    public string Name { get; init; } = "";

    public string Category { get; init; } = "";

    /// <summary>The API or library family this belongs to, e.g. "C runtime", "OpenGL", "PlayStation Portable SCE kernel".</summary>
    public string ApiFamily { get; init; } = "";

    public IReadOnlyList<string> PlatformHints { get; init; } = [];
    public string Description { get; init; } = "";

    /// <summary>The knowledge-base entry that produced this match.</summary>
    public string RuleId { get; init; } = "";

    /// <summary>The match itself was read from the file; interpreting it as a platform hint is an inference.</summary>
    public Evidence Evidence { get; init; } = Evidence.Verified;

    public int Occurrences { get; init; } = 1;
}

public sealed record DependencyAnalysis
{
    public bool HasSymbols { get; init; }
    public bool HasDynamicSection { get; init; }
    public IReadOnlyList<string> Libraries { get; init; } = [];
    public IReadOnlyList<string> ImportedSymbols { get; init; } = [];
    public int ExportedSymbolCount { get; init; }
    public IReadOnlyList<ApiMatch> ApiMatches { get; init; } = [];
    public IReadOnlyList<ApiMatch> HardwareHints { get; init; } = [];

    /// <summary>Plain-language notes, such as "No symbol tables present".</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public int StringsScanned { get; init; }
    public string KnowledgeBaseDescription { get; init; } = "";
}

public sealed record AnalysisResult
{
    public int SchemaVersion { get; init; } = 1;
    public string ToolName { get; init; } = "RecompRadar";
    public string ToolVersion { get; init; } = "";
    public required FileIdentity File { get; init; }
    public DateTimeOffset AnalyzedAtUtc { get; init; }
    public FormatDetection Format { get; init; } = new();

    /// <summary>True when the file was parsed structurally (currently: ELF).</summary>
    public bool Parsed { get; init; }

    public ElfFile? Elf { get; init; }
    public ArchitectureAssessment? Architecture { get; init; }
    public PlatformAssessment? Platform { get; init; }
    public DependencyAnalysis? Dependencies { get; init; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];
}
