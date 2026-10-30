using RecompRadar.Core;
using RecompRadar.Core.Model;

namespace RecompRadar.Architectures.Platforms;

/// <summary>Runs all platform heuristics and summarises the hints. The result is never presented as a conclusive identification.</summary>
public sealed class PlatformAnalyzer
{
    private readonly IReadOnlyList<IPlatformHeuristic> _heuristics;

    public PlatformAnalyzer(IEnumerable<IPlatformHeuristic>? additional = null)
    {
        _heuristics = [.. BuiltIn(), .. additional ?? []];
    }

    public static IReadOnlyList<IPlatformHeuristic> BuiltIn() =>
    [
        new Ps1Heuristic(),
        new Ps2Heuristic(),
        new Ps3Heuristic(),
        new Ps4Heuristic(),
        new PspHeuristic(),
        new GameCubeHeuristic(),
        new WiiHeuristic(),
        new LinuxHeuristic(),
    ];

    public PlatformAssessment Analyze(PlatformContext context, DiagnosticCollector? diagnostics = null)
    {
        var candidates = new List<PlatformCandidate>();
        foreach (var heuristic in _heuristics)
        {
            try
            {
                if (heuristic.Evaluate(context) is { } candidate)
                {
                    candidates.Add(candidate);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                diagnostics?.Warn("PLATFORM.HEURISTIC_FAILED", $"Platform heuristic '{heuristic.PlatformId}' failed: {ex.Message}",
                    "The heuristic was skipped. The other results are not affected.");
            }
        }

        candidates = candidates.OrderByDescending(c => c.Strength).ThenBy(c => c.DisplayName, StringComparer.Ordinal).ToList();
        return new PlatformAssessment { Candidates = candidates, Summary = Summarize(candidates) };
    }

    private static string Summarize(IReadOnlyList<PlatformCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return "Target system could not be determined (no console-specific indicators found)";
        }

        var top = candidates[0].Strength;
        var leaders = candidates.Where(c => c.Strength == top).Select(c => c.DisplayName).ToList();
        var strength = top switch
        {
            HintStrength.Strong => "strong hint",
            HintStrength.Moderate => "moderate hint",
            _ => "weak hint",
        };

        return $"Possibly {string.Join(" or ", leaders)} ({strength})";
    }
}
