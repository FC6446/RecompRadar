using RecompRadar.Core.Model;

namespace RecompRadar.Core;

/// <summary>
/// Collects diagnostics while capping how many are kept per code, so a heavily damaged file cannot
/// flood the result with thousands of identical warnings.
/// </summary>
public sealed class DiagnosticCollector
{
    private const int MaxPerCode = 10;

    private readonly List<Diagnostic> _items = [];
    private readonly Dictionary<string, int> _counts = [];

    public void Add(Severity severity, string code, string message, string? explanation = null)
    {
        _counts.TryGetValue(code, out var seen);
        _counts[code] = seen + 1;
        if (seen < MaxPerCode)
        {
            _items.Add(new Diagnostic(severity, code, message, explanation));
        }
    }

    public void Info(string code, string message, string? explanation = null) => Add(Severity.Info, code, message, explanation);

    public void Warn(string code, string message, string? explanation = null) => Add(Severity.Warning, code, message, explanation);

    public void Error(string code, string message, string? explanation = null) => Add(Severity.Error, code, message, explanation);

    public void AddRange(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var d in diagnostics)
        {
            Add(d.Severity, d.Code, d.Message, d.Explanation);
        }
    }

    public IReadOnlyList<Diagnostic> Build()
    {
        var result = new List<Diagnostic>(_items);
        foreach (var (code, count) in _counts)
        {
            if (count > MaxPerCode)
            {
                result.Add(new Diagnostic(Severity.Info, code, $"{count - MaxPerCode} more findings with code {code} were not listed."));
            }
        }

        return result;
    }
}
