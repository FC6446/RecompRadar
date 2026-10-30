using RecompRadar.Core;
using RecompRadar.Core.Model;

namespace RecompRadar.BinaryAnalysis.Dependencies;

/// <summary>
/// Lists the libraries and imported symbols of an ELF file and matches names (libraries, symbols, strings)
/// against the knowledge base. Only names that are really present in the file are reported.
/// </summary>
public sealed class DependencyAnalyzer
{
    private const int MaxDisplayNameLength = 120;

    private readonly KnowledgeBase _knowledge;
    private readonly AnalysisLimits _limits;

    public DependencyAnalyzer(KnowledgeBase knowledge, AnalysisLimits? limits = null)
    {
        _knowledge = knowledge ?? throw new ArgumentNullException(nameof(knowledge));
        _limits = limits ?? AnalysisLimits.Default;
    }

    public DependencyAnalysis Analyze(ElfFile elf, Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(elf);
        ArgumentNullException.ThrowIfNull(stream);

        var notes = new List<string>();
        var libraries = elf.Dynamic.NeededLibraries.Distinct(StringComparer.Ordinal).ToList();
        var imports = elf.Symbols.Where(s => s.IsImport).Select(s => s.Name).Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        var exported = elf.Symbols
            .Where(s => s.Table == "dynsym" && !s.IsUndefined && s.Name.Length > 0 && s.Binding is 1 or 2 && s.Visibility is 0 or 3)
            .Select(s => s.Name).Distinct(StringComparer.Ordinal).Count();

        var apiMatches = new Dictionary<(ApiMatchKind, string, string), ApiMatch>();
        var hardwareMatches = new Dictionary<(ApiMatchKind, string, string), ApiMatch>();
        var truncated = false;

        void AddMatches(ApiMatchKind kind, RuleKind ruleKind, string text)
        {
            foreach (var rule in _knowledge.Match(ruleKind, text))
            {
                var target = rule.Hardware ? hardwareMatches : apiMatches;
                var name = text.Length > MaxDisplayNameLength ? text[..MaxDisplayNameLength] + "…" : text;
                var key = (kind, name, rule.Id);
                if (target.TryGetValue(key, out var existing))
                {
                    target[key] = existing with { Occurrences = existing.Occurrences + 1 };
                }
                else if (apiMatches.Count + hardwareMatches.Count >= _limits.MaxKnowledgeMatches)
                {
                    truncated = true;
                }
                else
                {
                    target[key] = new ApiMatch
                    {
                        Kind = kind,
                        Name = name,
                        Category = rule.Category,
                        ApiFamily = rule.ApiFamily,
                        PlatformHints = rule.Platforms,
                        Description = rule.Description,
                        RuleId = rule.Id,
                        Evidence = Evidence.Verified,
                    };
                }
            }
        }

        foreach (var library in libraries)
        {
            AddMatches(ApiMatchKind.Library, RuleKind.Library, library);
        }

        cancellationToken.ThrowIfCancellationRequested();
        foreach (var import in imports)
        {
            AddMatches(ApiMatchKind.ImportedSymbol, RuleKind.Symbol, import);
        }

        var defined = elf.Symbols
            .Where(s => !s.IsUndefined && s.Type == 2 && s.Name.Length > 0)
            .Select(s => s.Name).Distinct(StringComparer.Ordinal);
        foreach (var name in defined)
        {
            AddMatches(ApiMatchKind.DefinedSymbol, RuleKind.Symbol, name);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var stringsScanned = StringScanner.Scan(elf, stream, _limits, cancellationToken, text => AddMatches(ApiMatchKind.String, RuleKind.String, text));

        // ---- plain-language notes ----
        var hasSymbols = elf.Symbols.Count > 0;
        if (!elf.HasSymbolTable && !elf.HasDynamicSymbolTable)
        {
            notes.Add("No symbol tables (.symtab or .dynsym) were found. The file is stripped or uses a symbol format RecompRadar does not read, " +
                      "so function names and imports are unavailable. Only embedded strings could be checked for hints.");
        }
        else if (hasSymbols && imports.Count == 0 && libraries.Count == 0)
        {
            notes.Add("Symbols are present but no imported symbols or shared libraries were found. The program appears to be statically linked or self-contained.");
        }

        if (!elf.Dynamic.HasDynamicSection && !elf.Dynamic.HasDynamicSegment)
        {
            notes.Add("The file has no dynamic section. It is statically linked, a relocatable object, or uses a console-specific linking format, " +
                      "so no shared-library dependencies are listed. This does not mean the program has no dependencies on its target system.");
        }
        else if (libraries.Count == 0)
        {
            notes.Add("The dynamic section does not list any needed libraries.");
        }

        if (stringsScanned == 0)
        {
            notes.Add("No readable strings were found in the data sections, or the file has no data sections that could be scanned.");
        }

        if (elf.SymbolsTruncated)
        {
            notes.Add($"A symbol table was larger than the safety limit ({_limits.MaxSymbolsPerTable:N0} entries per table); the symbol list is partial.");
        }

        if (truncated)
        {
            notes.Add($"More than {_limits.MaxKnowledgeMatches:N0} knowledge-base matches were found; the list was cut off.");
        }

        notes.Add("Names show what the file references, not what the program actually calls at run time. Call sites were not analyzed.");

        foreach (var warning in _knowledge.LoadWarnings)
        {
            notes.Add($"Knowledge base: {warning}");
        }

        return new DependencyAnalysis
        {
            HasSymbols = hasSymbols,
            HasDynamicSection = elf.Dynamic.HasDynamicSection || elf.Dynamic.HasDynamicSegment,
            Libraries = libraries,
            ImportedSymbols = imports,
            ExportedSymbolCount = exported,
            ApiMatches = Order(apiMatches.Values),
            HardwareHints = Order(hardwareMatches.Values),
            Notes = notes,
            StringsScanned = stringsScanned,
            KnowledgeBaseDescription = _knowledge.Description,
        };
    }

    private static List<ApiMatch> Order(IEnumerable<ApiMatch> matches) =>
        matches.OrderBy(m => m.Category, StringComparer.Ordinal)
            .ThenBy(m => m.ApiFamily, StringComparer.Ordinal)
            .ThenBy(m => m.Kind)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .ToList();
}
