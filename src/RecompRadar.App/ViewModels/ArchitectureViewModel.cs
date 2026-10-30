using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using RecompRadar.App.Services;
using RecompRadar.Core.Model;
using RecompRadar.Recompilation.Preview;

namespace RecompRadar.App.ViewModels;

public sealed partial class ArchitectureViewModel : PageViewModel
{
    private readonly AnalysisSession _session;
    private readonly ILogger<ArchitectureViewModel> _logger;
    private int _generation;

    public ArchitectureViewModel(AnalysisSession session, ILogger<ArchitectureViewModel> logger)
        : base("architecture", "Architecture Inspector", "CPU architecture, target-system hints and what a port would need", "IconChip")
    {
        _session = session;
        _logger = logger;
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AnalysisSession.Current))
            {
                Rebuild();
            }
        };
        Rebuild();
    }

    public AnalysisSession Session => _session;

    public ObservableCollection<KeyValueRow> SummaryRows { get; } = [];

    public ObservableCollection<KeyValueRow> FactRows { get; } = [];

    public ObservableCollection<CandidateRow> Candidates { get; } = [];

    public ObservableCollection<string> TechnicalNotes { get; } = [];

    public ObservableCollection<string> PreviewLines { get; } = [];

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private string _targetSummary = string.Empty;

    [ObservableProperty]
    private string _targetDisclaimer = string.Empty;

    [ObservableProperty]
    private string _recompilationSupport = string.Empty;

    [ObservableProperty]
    private string _previewTitle = string.Empty;

    [ObservableProperty]
    private string _previewNote = string.Empty;

    [ObservableProperty]
    private bool _hasPreview;

    private void Rebuild()
    {
        SummaryRows.Clear();
        FactRows.Clear();
        Candidates.Clear();
        TechnicalNotes.Clear();
        PreviewLines.Clear();
        HasPreview = false;
        PreviewNote = string.Empty;
        var generation = ++_generation;

        if (_session.Current?.Analysis is not { Parsed: true, Architecture: { } arch } analysis)
        {
            HasResult = false;
            return;
        }

        HasResult = true;
        var platform = analysis.Platform;

        SummaryRows.Add(new KeyValueRow("Architecture", arch.DisplayName, "Verified", arch.Description));
        SummaryRows.Add(new KeyValueRow("Binary Format", arch.BinaryFormat, "Verified", string.Empty));
        SummaryRows.Add(new KeyValueRow("Target System", platform?.Summary ?? "Not determined", "Inferred", "A hint derived from header fields and names – never proof."));
        SummaryRows.Add(new KeyValueRow("Host Architecture", arch.HostArchitecture, "Verified", "The chosen porting target."));
        SummaryRows.Add(new KeyValueRow("Native Execution", arch.NativeExecutionStatus, "Verified", string.Empty));
        SummaryRows.Add(new KeyValueRow("Required Work", arch.RequiredWork, "Verified", string.Empty));

        foreach (var f in arch.Facts)
        {
            FactRows.Add(f.ToRow());
        }

        foreach (var n in arch.TechnicalNotes)
        {
            TechnicalNotes.Add(n);
        }

        TargetSummary = platform?.Summary ?? "Not determined";
        TargetDisclaimer = platform?.Disclaimer ?? string.Empty;
        RecompilationSupport = arch.RecompilationSupport;
        foreach (var c in platform?.Candidates ?? [])
        {
            Candidates.Add(new CandidateRow(c.DisplayName, $"{c.Strength} hint", c.Reasons.Select(r => r.ToReason()).ToList(), c.Caveats));
        }

        _ = LoadPreviewAsync(analysis, generation);
    }

    private async Task LoadPreviewAsync(AnalysisResult analysis, int generation)
    {
        var elf = analysis.Elf;
        if (elf is null)
        {
            return;
        }

        try
        {
            var path = analysis.File.FullPath;
            var result = await Task.Run(() =>
            {
                if (!File.Exists(path) || new FileInfo(path).Length != analysis.File.SizeBytes)
                {
                    return (Preview: (PreviewResult?)null, Note: "The original file is no longer available at its saved location (or has changed), so the entry point preview cannot be shown.");
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return (Preview: EntryPointPreview.Create(elf, stream), Note: string.Empty);
            });

            if (generation != _generation)
            {
                return;
            }

            if (result.Preview is { } preview)
            {
                PreviewTitle = $"{preview.PluginName} – first instructions at the entry point";
                foreach (var line in preview.Instructions)
                {
                    PreviewLines.Add(line.ToString());
                }

                PreviewNote = preview.Note ?? string.Empty;
                HasPreview = preview.Instructions.Count > 0 || !string.IsNullOrEmpty(preview.Note);
            }
            else
            {
                PreviewNote = result.Note;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogInformation(ex, "Entry point preview unavailable");
            if (generation == _generation)
            {
                PreviewNote = "The entry point preview could not read the original file.";
            }
        }
    }
}
