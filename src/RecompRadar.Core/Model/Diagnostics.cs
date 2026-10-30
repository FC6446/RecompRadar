namespace RecompRadar.Core.Model;

public enum Severity
{
    Info,
    Warning,
    Error,
}

/// <summary>How a statement in a result was established.</summary>
public enum Evidence
{
    /// <summary>Read directly from the file structure (header fields, tables, strings).</summary>
    Verified,

    /// <summary>Derived by a heuristic. Plausible, but not proven by the file alone.</summary>
    Inferred,

    /// <summary>Could not be determined.</summary>
    Unknown,
}

/// <summary>A finding about the input or the analysis itself, with a human-readable explanation.</summary>
public sealed record Diagnostic(Severity Severity, string Code, string Message, string? Explanation = null);

/// <summary>A single named property of the analyzed binary, tagged with how it was established.</summary>
public sealed record Fact(string Name, string Value, Evidence Evidence, string? Note = null);
