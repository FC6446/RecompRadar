using RecompRadar.Core.Model;

namespace RecompRadar.App.ViewModels;

public sealed record KeyValueRow(string Name, string Value, string Evidence, string Note);

public sealed record SegmentRow(int Index, string Type, ulong Offset, ulong VirtualAddress, ulong FileSize, ulong MemorySize, string Flags, ulong Alignment);

public sealed record SectionRow(int Index, string Name, string Type, ulong Address, ulong Offset, ulong Size, string Flags, uint Link);

public sealed record SymbolRow(string Name, ulong Value, ulong Size, string Type, string Binding, string Visibility, string Section, string Table, string Kind);

public sealed record RegionRow(string Name, string Source, ulong Start, ulong End, ulong Size, string Permissions);

public sealed record DiagnosticRow(string Severity, string Code, string Message, string Explanation);

public sealed record ApiMatchRow(string Name, string Source, string Category, string ApiFamily, string Platforms, string Description);

public sealed record NameRow(string Name, string Detail);

public sealed record StepRow(int Number, string Title, string Description);

public sealed record QuestionRow(int Number, string Question, string Answer, IReadOnlyList<string> Basis);

public sealed record ReasonRow(string Name, string Value, string Evidence, string Note);

public sealed record CandidateRow(string Name, string Strength, IReadOnlyList<ReasonRow> Reasons, IReadOnlyList<string> Caveats);

public sealed record RuntimeRow(string Name, string Reason, string Evidence);

internal static class RowMapper
{
    public static string Evidence(Evidence evidence) => evidence.ToString();

    public static KeyValueRow ToRow(this Fact fact) => new(fact.Name, fact.Value, fact.Evidence.ToString(), fact.Note ?? string.Empty);

    public static ReasonRow ToReason(this Fact fact) => new(fact.Name, fact.Value, fact.Evidence.ToString(), fact.Note ?? string.Empty);

    public static DiagnosticRow ToRow(this Diagnostic d) => new(d.Severity.ToString(), d.Code, d.Message, d.Explanation ?? string.Empty);

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }
}
