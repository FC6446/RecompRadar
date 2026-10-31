using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RecompRadar.App.Services;
using RecompRadar.Recompilation.Compatibility;

namespace RecompRadar.App.ViewModels;

/// <summary>
/// Console detection and backend status. Detection shows hints for the current file; the backend cards list, module by module,
/// what exists, what is partial and what is only planned. Nothing here is a promise.
/// </summary>
public sealed partial class ConsolesViewModel : PageViewModel
{
    private readonly AnalysisSession _session;

    public ConsolesViewModel(AnalysisSession session)
        : base("consoles", "Console Backends", "Detected target system and what each backend can actually do", "IconConsole")
    {
        _session = session;
        foreach (var backend in ConsoleBackendRegistry.All)
        {
            Backends.Add(new BackendCardRow(
                backend.Id,
                backend.DisplayName,
                backend.Architecture,
                $"Milestone {backend.Milestone}",
                backend.Summary,
                backend.HasWorkingPipeline,
                $"{backend.Count(ModuleState.Implemented)} implemented, {backend.Count(ModuleState.Partial)} partial, {backend.Count(ModuleState.Planned)} planned, {backend.Count(ModuleState.NotProvided)} not provided",
                backend.Modules.Select(m => new ModuleRow(m.Name, m.State.ToString(), m.Note)).ToList()));
        }

        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AnalysisSession.Current))
            {
                RebuildDetection();
            }
        };
        RebuildDetection();
    }

    public ObservableCollection<BackendCardRow> Backends { get; } = [];

    public ObservableCollection<CandidateRow> Candidates { get; } = [];

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private string _detectionSummary = "No file has been analyzed yet.";

    [ObservableProperty]
    private string _detectionDisclaimer = string.Empty;

    private void RebuildDetection()
    {
        Candidates.Clear();
        if (_session.Current?.Analysis is not { Parsed: true } analysis || analysis.Platform is not { } platform)
        {
            HasResult = false;
            DetectionSummary = "No file has been analyzed yet, or the file could not be parsed.";
            DetectionDisclaimer = string.Empty;
            return;
        }

        HasResult = true;
        DetectionSummary = platform.Summary;
        DetectionDisclaimer = platform.Disclaimer;
        foreach (var c in platform.Candidates)
        {
            Candidates.Add(new CandidateRow(c.DisplayName, $"{c.Strength} hint", c.Reasons.Select(r => r.ToReason()).ToList(), c.Caveats));
        }
    }
}
