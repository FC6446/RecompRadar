using System.Net;
using System.Text;
using RecompRadar.Core;
using RecompRadar.Core.Model;

namespace RecompRadar.Portability.Export;

/// <summary>
/// Renders a single self-contained HTML file (inline CSS, no scripts, no external resources).
/// All file-derived text is HTML-encoded, so hostile symbol or section names cannot inject markup.
/// </summary>
public sealed class HtmlReportExporter : IReportExporter
{
    private const int MaxRows = 2000;

    public string FormatId => "html";

    public string DisplayName => "HTML";

    public string FileExtension => ".html";

    public string Render(AnalysisBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var a = bundle.Analysis;
        var r = bundle.Report;
        var sb = new StringBuilder(64 * 1024);

        sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append($"<title>RecompRadar report - {E(a.File.FileName)}</title>\n<style>\n{Css}\n</style>\n</head>\n<body>\n<main>\n");

        sb.Append("<header>\n");
        sb.Append($"<p class=\"eyebrow\">RecompRadar {E(a.ToolVersion)} - static analysis report</p>\n");
        sb.Append($"<h1>{E(a.File.FileName)}</h1>\n");
        sb.Append($"<p class=\"muted mono\">SHA-256 {E(a.File.Sha256)}</p>\n");
        sb.Append($"<p class=\"muted\">Analyzed {E(a.AnalyzedAtUtc.ToString("u"))} - {E(Fmt.Size(a.File.SizeBytes))} - target {E(r.Target)}</p>\n");
        sb.Append("</header>\n");

        sb.Append($"<section class=\"banner {StatusClass(r.StatusCode)}\"><strong>Overall porting status:</strong> {E(r.OverallPortingStatus)}</section>\n");

        sb.Append("<section class=\"cards\">\n");
        Card(sb, "Source architecture", r.SourceArchitecture);
        Card(sb, "CPU translation", r.CpuTranslation);
        Card(sb, "System API compatibility", r.SystemApiCompatibility);
        Card(sb, "Graphics compatibility", r.GraphicsCompatibility);
        Card(sb, "Target system (hint only)", a.Platform?.Summary ?? "Not determined");
        Card(sb, "File format", a.Format.DisplayName);
        sb.Append("</section>\n");

        Section(sb, "Questions answered by this report");
        foreach (var q in r.Questions)
        {
            sb.Append($"<div class=\"qa\"><h3>{q.Number}. {E(q.Question)}</h3><p>{E(q.Answer)}</p>");
            if (q.Basis.Count > 0)
            {
                sb.Append("<ul>");
                foreach (var basis in q.Basis)
                {
                    sb.Append($"<li>{E(basis)}</li>");
                }

                sb.Append("</ul>");
            }

            sb.Append("</div>\n");
        }

        Section(sb, "Technical findings");
        Table(sb, ["Finding", "Value", "Evidence"], r.Findings.Select(f => new[] { f.Name, f.Value, f.Evidence.ToString() }).ToList(), evidenceColumn: 2);

        if (a.Architecture is { } arch)
        {
            Section(sb, "Architecture");
            Table(sb, ["Property", "Value", "Evidence"], arch.Facts.Select(f => new[] { f.Name, f.Value, f.Evidence.ToString() }).ToList(), evidenceColumn: 2);
            sb.Append("<ul>");
            sb.Append($"<li><strong>Native execution:</strong> {E(arch.NativeExecutionStatus)}</li>");
            sb.Append($"<li><strong>Required work:</strong> {E(arch.RequiredWork)}</li>");
            sb.Append($"<li><strong>Recompilation support in RecompRadar:</strong> {E(arch.RecompilationSupport)}</li>");
            foreach (var note in arch.TechnicalNotes)
            {
                sb.Append($"<li>{E(note)}</li>");
            }

            sb.Append("</ul>\n");
        }

        if (a.Platform is { } platform)
        {
            Section(sb, "Target system (hints, not proof)");
            sb.Append($"<p><strong>{E(platform.Summary)}</strong></p><p class=\"muted\">{E(platform.Disclaimer)}</p>\n");
            foreach (var c in platform.Candidates)
            {
                sb.Append($"<details open><summary>{E(c.DisplayName)} <span class=\"badge {c.Strength.ToString().ToLowerInvariant()}\">{c.Strength} hint</span></summary>");
                Table(sb, ["Observation", "Value", "Evidence"], c.Reasons.Select(f => new[] { f.Name, f.Value, f.Evidence.ToString() }).ToList(), evidenceColumn: 2);
                if (c.Caveats.Count > 0)
                {
                    sb.Append("<ul>");
                    foreach (var caveat in c.Caveats)
                    {
                        sb.Append($"<li>{E(caveat)}</li>");
                    }

                    sb.Append("</ul>");
                }

                sb.Append("</details>\n");
            }
        }

        if (a.Dependencies is { } deps)
        {
            Section(sb, "Dependencies");
            sb.Append($"<p>{deps.Libraries.Count} shared libraries, {deps.ImportedSymbols.Count} imported symbols, {deps.ExportedSymbolCount} exported dynamic symbols. Knowledge base: {E(deps.KnowledgeBaseDescription)}.</p>\n");
            if (deps.Libraries.Count > 0)
            {
                Table(sb, ["Shared library"], deps.Libraries.Select(l => new[] { l }).ToList());
            }

            if (deps.ApiMatches.Count > 0)
            {
                sb.Append("<h3>Known API names found</h3>\n");
                Table(sb, ["Name", "Source", "Category", "API family", "Platform hints"],
                    deps.ApiMatches.Take(MaxRows).Select(m => new[] { m.Name, m.Kind.ToString(), m.Category, m.ApiFamily, string.Join(", ", m.PlatformHints) }).ToList());
                Truncated(sb, deps.ApiMatches.Count);
            }

            if (deps.HardwareHints.Count > 0)
            {
                sb.Append("<h3>Hardware access hints</h3>\n");
                Table(sb, ["Name", "Source", "API family", "Description"],
                    deps.HardwareHints.Take(MaxRows).Select(m => new[] { m.Name, m.Kind.ToString(), m.ApiFamily, m.Description }).ToList());
            }

            if (deps.ImportedSymbols.Count > 0)
            {
                sb.Append($"<details><summary>Imported symbols ({deps.ImportedSymbols.Count})</summary>");
                Table(sb, ["Symbol"], deps.ImportedSymbols.Take(MaxRows).Select(s => new[] { s }).ToList());
                Truncated(sb, deps.ImportedSymbols.Count);
                sb.Append("</details>\n");
            }

            sb.Append("<ul>");
            foreach (var note in deps.Notes)
            {
                sb.Append($"<li>{E(note)}</li>");
            }

            sb.Append("</ul>\n");
        }

        if (a.Elf is { } elf)
        {
            Section(sb, "Memory layout");
            Table(sb, ["Region", "Source", "Address", "Size", "Permissions"],
                elf.MemoryRegions.Take(MaxRows).Select(m => new[] { m.Name, m.Source, Fmt.Hex8(m.VirtualAddress), Fmt.Size(m.Size), m.Permissions }).ToList());
            sb.Append("<details><summary>Segments (" + elf.Segments.Count + ")</summary>");
            Table(sb, ["#", "Type", "Offset", "Virtual address", "File size", "Memory size", "Flags"],
                elf.Segments.Take(MaxRows).Select(s => new[] { s.Index.ToString(), s.TypeName, Fmt.Hex(s.Offset), Fmt.Hex8(s.VirtualAddress), s.FileSize.ToString(), s.MemorySize.ToString(), s.Permissions }).ToList());
            sb.Append("</details>\n");
            sb.Append("<details><summary>Sections (" + elf.Sections.Count + ")</summary>");
            Table(sb, ["#", "Name", "Type", "Address", "Offset", "Size", "Flags"],
                elf.Sections.Take(MaxRows).Select(s => new[] { s.Index.ToString(), s.Name, s.TypeName, Fmt.Hex8(s.Address), Fmt.Hex(s.Offset), s.Size.ToString(), s.FlagsText }).ToList());
            Truncated(sb, elf.Sections.Count);
            sb.Append("</details>\n");
        }

        Section(sb, "Missing information");
        List(sb, r.MissingInformation);

        Section(sb, "Runtime components that might be required");
        if (r.RuntimeComponents.Count == 0)
        {
            sb.Append("<p>None could be derived from the analysis.</p>\n");
        }
        else
        {
            Table(sb, ["Component", "Reason", "Evidence"], r.RuntimeComponents.Select(c => new[] { c.Name, c.Reason, c.Evidence.ToString() }).ToList(), evidenceColumn: 2);
        }

        Section(sb, "Suggested work plan");
        sb.Append("<ol>");
        foreach (var step in r.WorkSteps)
        {
            sb.Append($"<li><strong>{E(step.Title)}</strong>{(string.IsNullOrEmpty(step.Description) ? "" : ": " + E(step.Description))}</li>");
        }

        sb.Append("</ol>\n");

        Section(sb, "Warnings and notes");
        if (a.Diagnostics.Count == 0)
        {
            sb.Append("<p>None.</p>\n");
        }
        else
        {
            sb.Append("<ul class=\"diag\">");
            foreach (var d in a.Diagnostics)
            {
                sb.Append($"<li class=\"{d.Severity.ToString().ToLowerInvariant()}\"><strong>{d.Severity}</strong> <span class=\"mono\">{E(d.Code)}</span> {E(d.Message)}");
                if (!string.IsNullOrEmpty(d.Explanation))
                {
                    sb.Append($"<br><span class=\"muted\">{E(d.Explanation)}</span>");
                }

                sb.Append("</li>");
            }

            sb.Append("</ul>\n");
        }

        sb.Append($"<footer><p>{E(r.Disclaimer)}</p></footer>\n</main>\n</body>\n</html>\n");
        return sb.ToString();
    }

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    private static string StatusClass(PortingStatus status) => status switch
    {
        PortingStatus.NotAnalyzable => "bad",
        PortingStatus.InstructionSetMatches => "ok",
        _ => "warn",
    };

    private static void Section(StringBuilder sb, string title) => sb.Append($"<h2>{E(title)}</h2>\n");

    private static void Card(StringBuilder sb, string title, string value) =>
        sb.Append($"<div class=\"card\"><div class=\"card-title\">{E(title)}</div><div class=\"card-value\">{E(value)}</div></div>\n");

    private static void List(StringBuilder sb, IReadOnlyList<string> items)
    {
        sb.Append("<ul>");
        foreach (var item in items)
        {
            sb.Append($"<li>{E(item)}</li>");
        }

        sb.Append("</ul>\n");
    }

    private static void Truncated(StringBuilder sb, int total)
    {
        if (total > MaxRows)
        {
            sb.Append($"<p class=\"muted\">Showing the first {MaxRows} of {total} rows. The JSON export contains all rows.</p>\n");
        }
    }

    private static void Table(StringBuilder sb, string[] headers, IReadOnlyList<string[]> rows, int evidenceColumn = -1)
    {
        sb.Append("<div class=\"table-wrap\"><table><thead><tr>");
        foreach (var h in headers)
        {
            sb.Append($"<th>{E(h)}</th>");
        }

        sb.Append("</tr></thead><tbody>");
        foreach (var row in rows)
        {
            sb.Append("<tr>");
            for (var i = 0; i < row.Length; i++)
            {
                if (i == evidenceColumn)
                {
                    sb.Append($"<td><span class=\"badge {E(row[i]).ToLowerInvariant()}\">{E(row[i])}</span></td>");
                }
                else
                {
                    sb.Append($"<td>{E(row[i])}</td>");
                }
            }

            sb.Append("</tr>");
        }

        sb.Append("</tbody></table></div>\n");
    }

    private const string Css = """
        :root { --bg:#f6f7f9; --fg:#1b1f27; --muted:#5d6675; --card:#ffffff; --line:#dfe3ea; --accent:#2f6fed;
                --ok:#1a7f37; --warn:#9a6700; --bad:#cf222e; }
        @media (prefers-color-scheme: dark) {
          :root { --bg:#0f1115; --fg:#e6e8ee; --muted:#9aa3b2; --card:#171a21; --line:#2a2f3a; --accent:#6b9bff;
                  --ok:#3fb950; --warn:#d29922; --bad:#f85149; }
        }
        * { box-sizing: border-box; }
        body { margin:0; background:var(--bg); color:var(--fg); font:15px/1.5 "Segoe UI", system-ui, sans-serif; }
        main { max-width: 1100px; margin: 0 auto; padding: 32px 20px 64px; }
        h1 { margin:.1em 0 .2em; font-size: 28px; word-break: break-all; }
        h2 { margin: 36px 0 12px; font-size: 20px; border-bottom: 1px solid var(--line); padding-bottom: 6px; }
        h3 { margin: 18px 0 6px; font-size: 16px; }
        .eyebrow { margin:0; color:var(--accent); font-weight:600; letter-spacing:.04em; text-transform:uppercase; font-size:12px; }
        .muted { color: var(--muted); }
        .mono { font-family: Consolas, "Cascadia Mono", monospace; font-size: 13px; word-break: break-all; }
        .banner { margin: 20px 0; padding: 14px 18px; border-radius: 12px; border: 1px solid var(--line); background: var(--card); border-left: 6px solid var(--warn); }
        .banner.ok { border-left-color: var(--ok); } .banner.bad { border-left-color: var(--bad); }
        .cards { display:grid; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr)); gap: 12px; }
        .card { background: var(--card); border:1px solid var(--line); border-radius: 12px; padding: 14px 16px; }
        .card-title { color: var(--muted); font-size: 12px; text-transform: uppercase; letter-spacing:.04em; }
        .card-value { margin-top: 4px; font-weight: 600; }
        .qa { background: var(--card); border:1px solid var(--line); border-radius: 12px; padding: 4px 16px 10px; margin: 10px 0; }
        .table-wrap { overflow-x:auto; margin: 8px 0 14px; border:1px solid var(--line); border-radius: 10px; background: var(--card); }
        table { border-collapse: collapse; width: 100%; font-size: 13.5px; }
        th, td { text-align:left; padding: 7px 12px; border-bottom: 1px solid var(--line); vertical-align: top; word-break: break-word; }
        th { color: var(--muted); font-weight: 600; background: color-mix(in srgb, var(--card) 85%, var(--line)); }
        tr:last-child td { border-bottom: 0; }
        .badge { display:inline-block; padding: 1px 9px; border-radius: 999px; font-size: 12px; border:1px solid var(--line); }
        .badge.verified, .badge.strong { color: var(--ok); border-color: var(--ok); }
        .badge.inferred, .badge.moderate { color: var(--warn); border-color: var(--warn); }
        .badge.unknown, .badge.weak { color: var(--muted); }
        details { margin: 10px 0; } summary { cursor: pointer; font-weight: 600; }
        ul.diag { list-style:none; padding:0; } ul.diag li { padding: 8px 12px; margin: 6px 0; border:1px solid var(--line); border-left: 5px solid var(--muted); border-radius: 8px; background: var(--card); }
        ul.diag li.warning { border-left-color: var(--warn); } ul.diag li.error { border-left-color: var(--bad); }
        footer { margin-top: 40px; color: var(--muted); font-size: 13px; border-top: 1px solid var(--line); padding-top: 12px; }
        """;
}
