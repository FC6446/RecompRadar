using System.Text.Json;
using System.Text.Json.Serialization;
using RecompRadar.Core;

namespace RecompRadar.BinaryAnalysis.Dependencies;

public enum RuleKind
{
    Symbol,
    Library,
    String,
}

public enum MatchMode
{
    Exact,
    Prefix,
    Contains,
}

/// <summary>One entry of the knowledge base. Rules only describe names that are documented; they never invent behaviour.</summary>
public sealed class KnowledgeRule
{
    public string Id { get; set; } = "";
    public RuleKind Kind { get; set; }
    public MatchMode Match { get; set; } = MatchMode.Exact;
    public List<string> Patterns { get; set; } = [];
    public string Category { get; set; } = "";
    public string ApiFamily { get; set; } = "";
    public List<string> Platforms { get; set; } = [];
    public string Description { get; set; } = "";

    /// <summary>When true the match is reported as a hardware access hint.</summary>
    public bool Hardware { get; set; }
}

internal sealed class KnowledgeFile
{
    public int SchemaVersion { get; set; }
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public List<KnowledgeRule> Rules { get; set; } = [];
}

/// <summary>
/// Extensible database of known API, library and device names. The built-in file ships as an embedded resource;
/// additional *.json files from a user directory are merged in.
/// </summary>
public sealed class KnowledgeBase
{
    private const string ResourceName = "RecompRadar.Knowledge.builtin.json";
    private const long MaxFileBytes = 5L * 1024 * 1024;
    private const int MaxExtraFiles = 50;
    private const int MaxRules = 20_000;
    private const int MaxPatternLength = 256;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly Lazy<KnowledgeBase> BuiltInInstance = new(() => Build(null));

    private readonly Dictionary<string, List<KnowledgeRule>>[] _exact;
    private readonly List<(string Pattern, KnowledgeRule Rule)>[] _prefix;
    private readonly List<(string Pattern, KnowledgeRule Rule)>[] _contains;

    private KnowledgeBase(List<KnowledgeRule> rules, string description, List<string> warnings)
    {
        Rules = rules;
        Description = description;
        LoadWarnings = warnings;

        _exact = new Dictionary<string, List<KnowledgeRule>>[3];
        _prefix = new List<(string, KnowledgeRule)>[3];
        _contains = new List<(string, KnowledgeRule)>[3];
        for (var k = 0; k < 3; k++)
        {
            var comparer = (RuleKind)k == RuleKind.Library ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            _exact[k] = new Dictionary<string, List<KnowledgeRule>>(comparer);
            _prefix[k] = [];
            _contains[k] = [];
        }

        foreach (var rule in rules)
        {
            var k = (int)rule.Kind;
            foreach (var pattern in rule.Patterns)
            {
                switch (rule.Match)
                {
                    case MatchMode.Exact:
                        if (!_exact[k].TryGetValue(pattern, out var list))
                        {
                            _exact[k][pattern] = list = [];
                        }

                        list.Add(rule);
                        break;
                    case MatchMode.Prefix:
                        _prefix[k].Add((pattern, rule));
                        break;
                    default:
                        _contains[k].Add((pattern, rule));
                        break;
                }
            }
        }
    }

    public IReadOnlyList<KnowledgeRule> Rules { get; }
    public string Description { get; }
    public IReadOnlyList<string> LoadWarnings { get; }

    public static KnowledgeBase BuiltIn => BuiltInInstance.Value;

    /// <summary>Loads the built-in database and merges every valid *.json file from <paramref name="extraDirectory"/>.</summary>
    public static KnowledgeBase Load(string? extraDirectory) =>
        string.IsNullOrWhiteSpace(extraDirectory) ? BuiltIn : Build(extraDirectory);

    public IEnumerable<KnowledgeRule> Match(RuleKind kind, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        var k = (int)kind;
        var comparison = kind == RuleKind.Library ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (_exact[k].TryGetValue(text, out var exact))
        {
            foreach (var rule in exact)
            {
                yield return rule;
            }
        }

        foreach (var (pattern, rule) in _prefix[k])
        {
            if (text.StartsWith(pattern, comparison))
            {
                yield return rule;
            }
        }

        foreach (var (pattern, rule) in _contains[k])
        {
            if (text.Contains(pattern, comparison))
            {
                yield return rule;
            }
        }
    }

    private static KnowledgeBase Build(string? extraDirectory)
    {
        var warnings = new List<string>();
        var rules = new List<KnowledgeRule>();
        var builtIn = ReadBuiltIn();
        rules.AddRange(builtIn.Rules);
        var description = $"{builtIn.Name} {builtIn.Version}";

        if (!string.IsNullOrWhiteSpace(extraDirectory))
        {
            try
            {
                if (Directory.Exists(extraDirectory))
                {
                    foreach (var path in Directory.EnumerateFiles(extraDirectory, "*.json").Take(MaxExtraFiles))
                    {
                        TryMergeFile(path, rules, warnings);
                    }
                }
                else
                {
                    warnings.Add($"Extra knowledge directory not found: {extraDirectory}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not read extra knowledge directory '{extraDirectory}': {ex.Message}");
            }
        }

        return new KnowledgeBase(rules, $"{description} ({rules.Count} rules)", warnings);
    }

    private static KnowledgeFile ReadBuiltIn()
    {
        using var stream = typeof(KnowledgeBase).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is missing.");
        var file = JsonSerializer.Deserialize<KnowledgeFile>(stream, JsonOptions)
            ?? throw new InvalidOperationException("The built-in knowledge base is empty.");
        Normalize(file, new List<string>());
        return file;
    }

    private static void TryMergeFile(string path, List<KnowledgeRule> rules, List<string> warnings)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxFileBytes)
            {
                warnings.Add($"Skipped '{info.Name}': larger than {MaxFileBytes / (1024 * 1024)} MiB.");
                return;
            }

            using var stream = File.OpenRead(path);
            var file = JsonSerializer.Deserialize<KnowledgeFile>(stream, JsonOptions);
            if (file is null)
            {
                warnings.Add($"Skipped '{info.Name}': empty file.");
                return;
            }

            Normalize(file, warnings, info.Name);
            var room = MaxRules - rules.Count;
            if (file.Rules.Count > room)
            {
                warnings.Add($"'{info.Name}': only the first {Math.Max(room, 0)} rules were loaded (limit {MaxRules}).");
            }

            rules.AddRange(file.Rules.Take(Math.Max(room, 0)));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            warnings.Add($"Skipped '{Path.GetFileName(path)}': {ex.Message}");
        }
    }

    private static void Normalize(KnowledgeFile file, List<string> warnings, string? source = null)
    {
        file.Rules.RemoveAll(rule =>
        {
            rule.Patterns = rule.Patterns.Where(p => !string.IsNullOrEmpty(p) && p.Length <= MaxPatternLength).ToList();
            var valid = !string.IsNullOrWhiteSpace(rule.Id) && rule.Patterns.Count > 0;
            if (!valid && source is not null)
            {
                warnings.Add($"'{source}': rule '{rule.Id}' ignored (needs an id and at least one pattern of up to {MaxPatternLength} characters).");
            }

            return !valid;
        });
    }
}
