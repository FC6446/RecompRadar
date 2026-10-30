using System.Text;
using System.Text.Json;
using RecompRadar.Core;
using RecompRadar.Core.Model;

namespace RecompRadar.Portability.Export;

public sealed record ExportDocument
{
    public string Schema { get; init; } = "recompradar.export/1";
    public string Tool { get; init; } = ToolInfo.Name;
    public string ToolVersion { get; init; } = ToolInfo.Version;
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public required AnalysisResult Analysis { get; init; }
    public required PortabilityReport Report { get; init; }
}

public sealed class JsonReportExporter : IReportExporter
{
    public string FormatId => "json";

    public string DisplayName => "JSON";

    public string FileExtension => ".json";

    public string Render(AnalysisBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var document = new ExportDocument
        {
            GeneratedAtUtc = bundle.Report.GeneratedAtUtc,
            Analysis = bundle.Analysis,
            Report = bundle.Report,
        };
        return JsonSerializer.Serialize(document, RecompRadarJson.Options);
    }
}

public sealed class MarkdownReportExporter : IReportExporter
{
    private const int MaxRows = 500;

    public string FormatId => "markdown";

    public string DisplayName => "Markdown";

    public string FileExtension => ".md";

    public string Render(AnalysisBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var a = bundle.Analysis;
        var r = bundle.Report;
        var sb = new StringBuilder();

        sb.AppendLine($"# RecompRadar report: {Esc(a.File.FileName)}");
        sb.AppendLine();
        sb.AppendLine($"> Static analysis only. {r.Disclaimer}");
        sb.AppendLine();

        sb.AppendLine("## Summary");
        sb.AppendLine();
        Table(sb, ["Property", "Value"],
        [
            ["Target", r.Target],
            ["Source architecture", r.SourceArchitecture],
            ["CPU translation", r.CpuTranslation],
            ["System API compatibility", r.SystemApiCompatibility],
            ["Graphics compatibility", r.GraphicsCompatibility],
            ["Overall porting status", r.OverallPortingStatus],
        ]);

        sb.AppendLine("## File");
        sb.AppendLine();
        Table(sb, ["Property", "Value"],
        [
            ["Name", a.File.FileName],
            ["Path", a.File.FullPath],
            ["Size", $"{Fmt.Size(a.File.SizeBytes)} ({a.File.SizeBytes} bytes)"],
            ["SHA-256", a.File.Sha256],
            ["Format", a.Format.DisplayName],
            ["Analyzed (UTC)", a.AnalyzedAtUtc.ToString("u")],
            ["RecompRadar version", a.ToolVersion],
        ]);

        sb.AppendLine("## Questions answered by this report");
        sb.AppendLine();
        foreach (var q in r.Questions)
        {
            sb.AppendLine($"**{q.Number}. {Esc(q.Question)}**");
            sb.AppendLine();
            sb.AppendLine(q.Answer);
            sb.AppendLine();
            foreach (var basis in q.Basis)
            {
                sb.AppendLine($"- {basis}");
            }

            if (q.Basis.Count > 0)
            {
                sb.AppendLine();
            }
        }

        sb.AppendLine("## Technical findings");
        sb.AppendLine();
        Table(sb, ["Finding", "Value", "Evidence"], r.Findings.Select(f => new[] { f.Name, f.Value, f.Evidence.ToString() }).ToList());

        if (a.Architecture is { } arch)
        {
            sb.AppendLine("## Architecture");
            sb.AppendLine();
            Table(sb, ["Property", "Value", "Evidence"], arch.Facts.Select(f => new[] { f.Name, f.Value, f.Evidence.ToString() }).ToList());
            sb.AppendLine($"- Native execution: **{arch.NativeExecutionStatus}**");
            sb.AppendLine($"- Required work: {arch.RequiredWork}");
            sb.AppendLine($"- Recompilation support in RecompRadar: {arch.RecompilationSupport}");
            foreach (var note in arch.TechnicalNotes)
            {
                sb.AppendLine($"- Note: {note}");
            }

            sb.AppendLine();
        }

        if (a.Platform is { } platform)
        {
            sb.AppendLine("## Target system (hints, not proof)");
            sb.AppendLine();
            sb.AppendLine($"**{platform.Summary}**");
            sb.AppendLine();
            sb.AppendLine($"_{platform.Disclaimer}_");
            sb.AppendLine();
            foreach (var c in platform.Candidates)
            {
                sb.AppendLine($"### {c.DisplayName} ({c.Strength} hint)");
                sb.AppendLine();
                foreach (var reason in c.Reasons)
                {
                    sb.AppendLine($"- {reason.Name}: {reason.Value} ({reason.Evidence}){(reason.Note is null ? "" : " - " + reason.Note)}");
                }

                foreach (var caveat in c.Caveats)
                {
                    sb.AppendLine($"- Caveat: {caveat}");
                }

                sb.AppendLine();
            }
        }

        if (a.Dependencies is { } deps)
        {
            sb.AppendLine("## Dependencies");
            sb.AppendLine();
            sb.AppendLine($"Shared libraries: {deps.Libraries.Count}; imported symbols: {deps.ImportedSymbols.Count}; exported dynamic symbols: {deps.ExportedSymbolCount}; knowledge base: {deps.KnowledgeBaseDescription}.");
            sb.AppendLine();
            if (deps.Libraries.Count > 0)
            {
                foreach (var lib in deps.Libraries)
                {
                    sb.AppendLine($"- `{lib}`");
                }

                sb.AppendLine();
            }

            if (deps.ApiMatches.Count > 0)
            {
                sb.AppendLine("### Known API names found");
                sb.AppendLine();
                Table(sb, ["Name", "Source", "Category", "API family", "Platform hints"],
                    deps.ApiMatches.Take(MaxRows).Select(m => new[] { m.Name, m.Kind.ToString(), m.Category, m.ApiFamily, string.Join(", ", m.PlatformHints) }).ToList());
                Truncated(sb, deps.ApiMatches.Count);
            }

            if (deps.HardwareHints.Count > 0)
            {
                sb.AppendLine("### Hardware access hints");
                sb.AppendLine();
                Table(sb, ["Name", "Source", "API family", "Description"],
                    deps.HardwareHints.Take(MaxRows).Select(m => new[] { m.Name, m.Kind.ToString(), m.ApiFamily, m.Description }).ToList());
            }

            foreach (var note in deps.Notes)
            {
                sb.AppendLine($"- {note}");
            }

            sb.AppendLine();
        }

        if (a.Elf is { } elf)
        {
            sb.AppendLine("## Memory layout");
            sb.AppendLine();
            Table(sb, ["Region", "Source", "Address", "Size", "Permissions"],
                elf.MemoryRegions.Take(MaxRows).Select(m => new[] { m.Name, m.Source, Fmt.Hex8(m.VirtualAddress), Fmt.Size(m.Size), m.Permissions }).ToList());
            sb.AppendLine("## Segments");
            sb.AppendLine();
            Table(sb, ["#", "Type", "Offset", "Virtual address", "File size", "Memory size", "Flags"],
                elf.Segments.Take(MaxRows).Select(s => new[] { s.Index.ToString(), s.TypeName, Fmt.Hex(s.Offset), Fmt.Hex8(s.VirtualAddress), s.FileSize.ToString(), s.MemorySize.ToString(), s.Permissions }).ToList());
            sb.AppendLine("## Sections");
            sb.AppendLine();
            Table(sb, ["#", "Name", "Type", "Address", "Offset", "Size", "Flags"],
                elf.Sections.Take(MaxRows).Select(s => new[] { s.Index.ToString(), s.Name, s.TypeName, Fmt.Hex8(s.Address), Fmt.Hex(s.Offset), s.Size.ToString(), s.FlagsText }).ToList());
            Truncated(sb, elf.Sections.Count);
        }

        sb.AppendLine("## Missing information");
        sb.AppendLine();
        foreach (var item in r.MissingInformation)
        {
            sb.AppendLine($"- {item}");
        }

        sb.AppendLine();
        sb.AppendLine("## Runtime components that might be required");
        sb.AppendLine();
        foreach (var c in r.RuntimeComponents)
        {
            sb.AppendLine($"- **{c.Name}** ({c.Evidence}): {c.Reason}");
        }

        sb.AppendLine();
        sb.AppendLine("## Suggested work plan");
        sb.AppendLine();
        foreach (var step in r.WorkSteps)
        {
            sb.AppendLine($"{step.Order}. **{step.Title}**{(string.IsNullOrEmpty(step.Description) ? "" : ": " + step.Description)}");
        }

        sb.AppendLine();
        sb.AppendLine("## Warnings and notes");
        sb.AppendLine();
        if (a.Diagnostics.Count == 0)
        {
            sb.AppendLine("None.");
        }

        foreach (var d in a.Diagnostics)
        {
            sb.AppendLine($"- **{d.Severity}** `{d.Code}`: {d.Message}{(string.IsNullOrEmpty(d.Explanation) ? "" : " _" + d.Explanation + "_")}");
        }

        return sb.ToString();
    }

    private static void Truncated(StringBuilder sb, int total)
    {
        if (total > MaxRows)
        {
            sb.AppendLine($"_Showing the first {MaxRows} of {total} rows. The JSON export contains all rows._");
            sb.AppendLine();
        }
    }

    private static string Esc(string text) =>
        text.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|");

    private static void Table(StringBuilder sb, string[] headers, IReadOnlyList<string[]> rows)
    {
        sb.AppendLine("| " + string.Join(" | ", headers) + " |");
        sb.AppendLine("|" + string.Concat(headers.Select(_ => " --- |")));
        foreach (var row in rows)
        {
            sb.AppendLine("| " + string.Join(" | ", row.Select(Esc)) + " |");
        }

        sb.AppendLine();
    }
}
