using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RecompRadar.App.Services;
using RecompRadar.Core;
using RecompRadar.Core.Model;
using RecompRadar.Infrastructure;

namespace RecompRadar.App.ViewModels;

public sealed partial class ProjectsViewModel : PageViewModel
{
    private readonly IProjectStore _store;
    private readonly AnalysisSession _session;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly NotificationService _notifications;
    private readonly ILogger<ProjectsViewModel> _logger;
    private List<ProjectSummary> _all = [];

    public ProjectsViewModel(
        IProjectStore store,
        AnalysisSession session,
        IDialogService dialogs,
        IShellService shell,
        NotificationService notifications,
        ILogger<ProjectsViewModel> logger)
        : base("projects", "Projects", "Saved analyses – reopen them without the original file being analyzed again", "IconFolder")
    {
        _store = store;
        _session = session;
        _dialogs = dialogs;
        _shell = shell;
        _notifications = notifications;
        _logger = logger;
        session.ProjectsChanged += async (_, _) => await RefreshAsync();
        _ = RefreshAsync();
    }

    public ObservableCollection<ProjectSummary> Projects { get; } = [];

    [ObservableProperty]
    private ProjectSummary? _selected;

    [ObservableProperty]
    private string _editName = string.Empty;

    [ObservableProperty]
    private string _editNotes = string.Empty;

    [ObservableProperty]
    private string _filter = string.Empty;

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private string _countText = string.Empty;

    [ObservableProperty]
    private bool _fileMissing;

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnSelectedChanged(ProjectSummary? value)
    {
        EditName = value?.Name ?? string.Empty;
        EditNotes = value?.Notes ?? string.Empty;
        FileMissing = value is not null && !File.Exists(value.FilePath);
        OpenSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        SaveChangesCommand.NotifyCanExecuteChanged();
        RevealReportCommand.NotifyCanExecuteChanged();
    }

    private bool HasSelection() => Selected is not null;

    private bool HasReport() => Selected?.ReportPath is { Length: > 0 };

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            _all = (await _store.ListAsync()).ToList();
            ApplyFilter();
        }
        catch (ProjectStoreException ex)
        {
            _logger.LogError(ex, "Listing projects failed");
            _notifications.Error("Projects could not be loaded", ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task OpenSelectedAsync()
    {
        if (Selected is { } project)
        {
            await _session.OpenProjectAsync(project.Id);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteSelectedAsync()
    {
        if (Selected is not { } project)
        {
            return;
        }

        if (!_dialogs.Confirm("Delete project", $"Delete the saved analysis “{project.Name}”?\n\nOnly RecompRadar's stored result is removed. The analyzed file and any exported reports are not touched."))
        {
            return;
        }

        try
        {
            await _store.DeleteAsync(project.Id);
            if (_session.CurrentProjectId == project.Id)
            {
                _session.CurrentProjectId = null;
            }

            await RefreshAsync();
            _session.NotifyProjectsChanged();
        }
        catch (ProjectStoreException ex)
        {
            _notifications.Error("The project could not be deleted", ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task SaveChangesAsync()
    {
        if (Selected is not { } project)
        {
            return;
        }

        try
        {
            if (!string.Equals(EditName.Trim(), project.Name, StringComparison.Ordinal))
            {
                await _store.RenameAsync(project.Id, EditName);
            }

            if (!string.Equals(EditNotes, project.Notes, StringComparison.Ordinal))
            {
                await _store.UpdateNotesAsync(project.Id, EditNotes);
            }

            var id = project.Id;
            await RefreshAsync();
            Selected = Projects.FirstOrDefault(p => p.Id == id);
            _notifications.Success("Project updated", "Name and notes were saved.");
        }
        catch (ProjectStoreException ex)
        {
            _notifications.Error("The changes could not be saved", ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(HasReport))]
    private void RevealReport()
    {
        if (Selected?.ReportPath is { } path)
        {
            _shell.RevealFile(path);
        }
    }

    private void ApplyFilter()
    {
        var selectedId = Selected?.Id;
        var text = Filter.Trim();
        var shown = _all.Where(p => text.Length == 0
            || p.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            || p.FileName.Contains(text, StringComparison.OrdinalIgnoreCase)
            || p.Architecture.Contains(text, StringComparison.OrdinalIgnoreCase)
            || p.Sha256.StartsWith(text, StringComparison.OrdinalIgnoreCase)).ToList();

        Projects.Clear();
        foreach (var p in shown)
        {
            Projects.Add(p);
        }

        IsEmpty = _all.Count == 0;
        CountText = _all.Count == 0 ? "No saved projects" : shown.Count == _all.Count ? $"{_all.Count} project(s)" : $"{shown.Count} of {_all.Count} project(s)";
        Selected = selectedId is { } id ? Projects.FirstOrDefault(p => p.Id == id) : null;
    }
}
