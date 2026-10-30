using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using RecompRadar.Core;
using RecompRadar.Core.Model;
using RecompRadar.Infrastructure;

namespace RecompRadar.App.Services;

/// <summary>
/// The one place that runs analyses and holds the result every page shows. Pages observe <see cref="Current"/>;
/// the work itself runs on background threads so the window stays responsive.
/// </summary>
public sealed partial class AnalysisSession : ObservableObject
{
    private readonly IAnalysisPipeline _pipeline;
    private readonly IProjectStore _store;
    private readonly SettingsService _settings;
    private readonly AppPaths _paths;
    private readonly NotificationService _notifications;
    private readonly ILogger<AnalysisSession> _logger;
    private CancellationTokenSource? _cts;

    public AnalysisSession(
        IAnalysisPipeline pipeline,
        IProjectStore store,
        SettingsService settings,
        AppPaths paths,
        NotificationService notifications,
        ILogger<AnalysisSession> logger)
    {
        _pipeline = pipeline;
        _store = store;
        _settings = settings;
        _paths = paths;
        _notifications = notifications;
        _logger = logger;
    }

    [ObservableProperty]
    private AnalysisBundle? _current;

    [ObservableProperty]
    private Guid? _currentProjectId;

    [ObservableProperty]
    private string? _lastExportPath;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _stage = string.Empty;

    /// <summary>Raised on the UI thread after an analysis or a project was loaded into <see cref="Current"/>.</summary>
    public event EventHandler? ResultLoaded;

    /// <summary>Raised after the list of stored projects changed.</summary>
    public event EventHandler? ProjectsChanged;

    public async Task AnalyzeAsync(string path)
    {
        if (IsBusy)
        {
            _notifications.Info("Analysis in progress", "Please wait for the current analysis to finish or cancel it first.");
            return;
        }

        _cts = new CancellationTokenSource();
        IsBusy = true;
        Progress = 0;
        Stage = "Starting";
        try
        {
            var settings = _settings.Current;
            var options = new AnalysisOptions
            {
                Limits = settings.ToLimits(),
                ExtraKnowledgeDirectory = string.IsNullOrWhiteSpace(settings.ExtraKnowledgeDirectory) ? _paths.Knowledge : settings.ExtraKnowledgeDirectory,
            };
            var progress = new Progress<AnalysisProgress>(p =>
            {
                Stage = p.Stage;
                Progress = p.Fraction;
            });

            var bundle = await _pipeline.AnalyzeAsync(path, options, progress, _cts.Token);
            Current = bundle;
            CurrentProjectId = null;
            LastExportPath = null;
            Progress = 1;
            Stage = "Done";

            if (bundle.Analysis.Parsed && settings.AutoSaveProjects)
            {
                await SaveCurrentAsync(silent: true);
            }

            ResultLoaded?.Invoke(this, EventArgs.Empty);
            AnnounceOutcome(bundle);
        }
        catch (OperationCanceledException)
        {
            _notifications.Info("Analysis cancelled", "The analysis was stopped. Nothing was changed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Analysis of {Path} failed unexpectedly", Path.GetFileName(path));
            _notifications.Error("The analysis failed unexpectedly",
                "Something went wrong inside RecompRadar. Your file was not modified. The details were written to the log file.",
                ex.Message);
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Cancel() => _cts?.Cancel();

    public async Task<bool> SaveCurrentAsync(bool silent = false)
    {
        if (Current is not { } bundle)
        {
            return false;
        }

        try
        {
            var summary = await _store.SaveAsync(bundle, null);
            CurrentProjectId = summary.Id;
            ProjectsChanged?.Invoke(this, EventArgs.Empty);
            if (!silent)
            {
                _notifications.Success("Project saved", $"“{summary.Name}” was added to your projects.");
            }

            return true;
        }
        catch (ProjectStoreException ex)
        {
            _logger.LogError(ex, "Saving the project failed");
            _notifications.Error("The analysis could not be saved", ex.Message, "The analysis result is still shown and can be exported.");
            return false;
        }
    }

    public async Task OpenProjectAsync(Guid id)
    {
        try
        {
            var record = await _store.GetAsync(id);
            if (record is null)
            {
                _notifications.Error("Project not found", "This project no longer exists. It may have been deleted.");
                ProjectsChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            Current = record.Bundle;
            CurrentProjectId = id;
            LastExportPath = record.Summary.ReportPath;
            ResultLoaded?.Invoke(this, EventArgs.Empty);
        }
        catch (ProjectStoreException ex)
        {
            _logger.LogError(ex, "Opening project {ProjectId} failed", id);
            _notifications.Error("The project could not be opened", ex.Message);
        }
    }

    public async Task RecordExportAsync(string path)
    {
        LastExportPath = path;
        if (CurrentProjectId is { } id)
        {
            try
            {
                await _store.SetReportPathAsync(id, path);
                ProjectsChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (ProjectStoreException ex)
            {
                _logger.LogWarning(ex, "Could not remember the report path for project {ProjectId}", id);
            }
        }
    }

    public void NotifyProjectsChanged() => ProjectsChanged?.Invoke(this, EventArgs.Empty);

    private void AnnounceOutcome(AnalysisBundle bundle)
    {
        var analysis = bundle.Analysis;
        if (analysis.Parsed)
        {
            var warnings = analysis.Diagnostics.Count(d => d.Severity == Severity.Warning);
            _notifications.Success("Analysis complete",
                warnings == 0
                    ? $"{analysis.File.FileName}: {analysis.Architecture?.Description}."
                    : $"{analysis.File.FileName}: {analysis.Architecture?.Description}. {warnings} warning(s) – see the Diagnostics tab.");
            return;
        }

        var first = analysis.Diagnostics.FirstOrDefault(d => d.Severity >= Severity.Warning);
        _notifications.Show(NotificationKind.Warning, "This file could not be analyzed",
            first?.Message ?? "The file format is not supported.", first?.Explanation);
    }
}
