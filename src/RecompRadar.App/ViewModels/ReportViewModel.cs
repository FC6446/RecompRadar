using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RecompRadar.App.Services;
using RecompRadar.Core.Model;
using RecompRadar.Portability.Export;

namespace RecompRadar.App.ViewModels;

public sealed partial class ReportViewModel : PageViewModel
{
    private readonly AnalysisSession _session;
    private readonly ReportExportService _export;
    private readonly IFileDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly SettingsService _settings;
    private readonly NotificationService _notifications;

    public ReportViewModel(
        AnalysisSession session,
        ReportExportService export,
        IFileDialogService dialogs,
        IShellService shell,
        SettingsService settings,
        NotificationService notifications)
        : base("report", "Portability Report", "What a native Windows port would require, based on the analysis", "IconReport")
    {
        _session = session;
        _export = export;
        _dialogs = dialogs;
        _shell = shell;
        _settings = settings;
        _notifications = notifications;
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AnalysisSession.Current))
            {
                Rebuild();
            }
            else if (e.PropertyName is nameof(AnalysisSession.CurrentProjectId) or nameof(AnalysisSession.LastExportPath))
            {
                RefreshStateProperties();
            }
        };
        Rebuild();
    }

    public AnalysisSession Session => _session;

    public ObservableCollection<KeyValueRow> SummaryRows { get; } = [];

    public ObservableCollection<QuestionRow> Questions { get; } = [];

    public ObservableCollection<KeyValueRow> Findings { get; } = [];

    public ObservableCollection<string> MissingInformation { get; } = [];

    public ObservableCollection<RuntimeRow> RuntimeComponents { get; } = [];

    public ObservableCollection<StepRow> WorkSteps { get; } = [];

    public ObservableCollection<DiagnosticRow> Warnings { get; } = [];

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _overallStatus = string.Empty;

    /// <summary>"ok", "warn" or "bad" – drives the colour of the status banner.</summary>
    [ObservableProperty]
    private string _statusKind = "warn";

    [ObservableProperty]
    private string _disclaimer = string.Empty;

    public bool IsSaved => _session.CurrentProjectId is not null;

    public bool HasExport => !string.IsNullOrEmpty(_session.LastExportPath);

    public string ExportPathText => _session.LastExportPath is { } p ? $"Last export: {p}" : string.Empty;

    private bool CanSave() => _session.Current is not null && !IsSaved;

    private bool CanReveal() => HasExport;

    [RelayCommand]
    private Task ExportJsonAsync() => ExportAsync("json");

    [RelayCommand]
    private Task ExportMarkdownAsync() => ExportAsync("markdown");

    [RelayCommand]
    private Task ExportHtmlAsync() => ExportAsync("html");

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveProjectAsync() => _session.SaveCurrentAsync();

    [RelayCommand(CanExecute = nameof(CanReveal))]
    private void RevealExport()
    {
        if (_session.LastExportPath is { } path)
        {
            _shell.RevealFile(path);
        }
    }

    private void RefreshStateProperties()
    {
        OnPropertyChanged(nameof(IsSaved));
        OnPropertyChanged(nameof(HasExport));
        OnPropertyChanged(nameof(ExportPathText));
        SaveProjectCommand.NotifyCanExecuteChanged();
        RevealExportCommand.NotifyCanExecuteChanged();
    }

    private async Task ExportAsync(string format)
    {
        if (_session.Current is not { } bundle)
        {
            return;
        }

        var exporter = _export.Get(format);
        var filter = $"{exporter.DisplayName} (*{exporter.FileExtension})|*{exporter.FileExtension}";
        var initial = _settings.Current.ExportDirectory;
        if (initial is null && _session.LastExportPath is { } last)
        {
            initial = Path.GetDirectoryName(last);
        }

        var path = _dialogs.PickSavePath(_export.SuggestFileName(bundle, format), filter, initial);
        if (path is null)
        {
            return;
        }

        try
        {
            var written = await _export.ExportAsync(bundle, format, path);
            await _session.RecordExportAsync(written);
            _notifications.Success("Report exported", written);
        }
        catch (ExportException ex)
        {
            _notifications.Error("The report could not be exported", ex.Message, "Choose another folder or file name and try again.");
        }
    }

    private void Rebuild()
    {
        SummaryRows.Clear();
        Questions.Clear();
        Findings.Clear();
        MissingInformation.Clear();
        RuntimeComponents.Clear();
        WorkSteps.Clear();
        Warnings.Clear();
        RefreshStateProperties();

        if (_session.Current is not { } bundle)
        {
            HasResult = false;
            return;
        }

        var r = bundle.Report;
        HasResult = true;
        FileName = bundle.Analysis.File.FileName;
        OverallStatus = r.OverallPortingStatus;
        Disclaimer = r.Disclaimer;
        StatusKind = r.StatusCode switch
        {
            PortingStatus.NotAnalyzable => "bad",
            PortingStatus.InstructionSetMatches => "ok",
            _ => "warn",
        };

        SummaryRows.Add(new KeyValueRow("Target", r.Target, string.Empty, string.Empty));
        SummaryRows.Add(new KeyValueRow("Source Architecture", r.SourceArchitecture, string.Empty, string.Empty));
        SummaryRows.Add(new KeyValueRow("CPU Translation", r.CpuTranslation, string.Empty, string.Empty));
        SummaryRows.Add(new KeyValueRow("System API Compatibility", r.SystemApiCompatibility, string.Empty, string.Empty));
        SummaryRows.Add(new KeyValueRow("Graphics Compatibility", r.GraphicsCompatibility, string.Empty, string.Empty));
        SummaryRows.Add(new KeyValueRow("Overall Porting Status", r.OverallPortingStatus, string.Empty, string.Empty));

        foreach (var q in r.Questions)
        {
            Questions.Add(new QuestionRow(q.Number, q.Question, q.Answer, q.Basis));
        }

        foreach (var f in r.Findings)
        {
            Findings.Add(f.ToRow());
        }

        foreach (var m in r.MissingInformation)
        {
            MissingInformation.Add(m);
        }

        foreach (var c in r.RuntimeComponents)
        {
            RuntimeComponents.Add(new RuntimeRow(c.Name, c.Reason, c.Evidence.ToString()));
        }

        foreach (var s in r.WorkSteps)
        {
            WorkSteps.Add(new StepRow(s.Order, s.Title, s.Description));
        }

        foreach (var w in r.Warnings)
        {
            Warnings.Add(w.ToRow());
        }
    }
}
