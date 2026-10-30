using System.IO;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RecompRadar.App.Services;
using RecompRadar.Core;
using RecompRadar.Infrastructure;

namespace RecompRadar.App.ViewModels;

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly SettingsService _settings;
    private readonly IFileDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly NotificationService _notifications;

    public SettingsViewModel(
        SettingsService settings,
        AppPaths paths,
        IFileDialogService dialogs,
        IShellService shell,
        NotificationService notifications)
        : base("settings", "Settings", "Safety limits, saving behaviour and appearance", "IconSliders")
    {
        _settings = settings;
        Paths = paths;
        _dialogs = dialogs;
        _shell = shell;
        _notifications = notifications;
        LoadFrom(settings.Current);
    }

    public AppPaths Paths { get; }

    public string VersionText { get; } = ToolInfo.Version;

    public string RuntimeText { get; } = $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription}";

    [ObservableProperty]
    private string _maxFileSizeText = "2048";

    [ObservableProperty]
    private bool _autoSaveProjects = true;

    [ObservableProperty]
    private bool _enableAnimations = true;

    [ObservableProperty]
    private string _exportDirectory = string.Empty;

    [ObservableProperty]
    private string _extraKnowledgeDirectory = string.Empty;

    [ObservableProperty]
    private string _validationMessage = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [RelayCommand]
    private void Save()
    {
        if (!int.TryParse(MaxFileSizeText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var mib) || mib is < 1 or > 16_384)
        {
            ValidationMessage = "The file size limit must be a whole number between 1 and 16384 (MiB).";
            StatusMessage = string.Empty;
            return;
        }

        ValidationMessage = string.Empty;
        var updated = new AppSettings
        {
            MaxFileSizeMiB = mib,
            AutoSaveProjects = AutoSaveProjects,
            EnableAnimations = EnableAnimations,
            ExportDirectory = string.IsNullOrWhiteSpace(ExportDirectory) ? null : ExportDirectory.Trim(),
            ExtraKnowledgeDirectory = string.IsNullOrWhiteSpace(ExtraKnowledgeDirectory) ? null : ExtraKnowledgeDirectory.Trim(),
        };

        try
        {
            _settings.Update(updated);
            StatusMessage = $"Saved at {DateTime.Now:T}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notifications.Error("The settings could not be saved", ex.Message, "Check that the RecompRadar data folder is writable.");
        }
    }

    [RelayCommand]
    private void Reset()
    {
        LoadFrom(new AppSettings());
        Save();
    }

    [RelayCommand]
    private void BrowseExportDirectory()
    {
        if (_dialogs.PickFolder(ExportDirectory) is { } folder)
        {
            ExportDirectory = folder;
        }
    }

    [RelayCommand]
    private void BrowseKnowledgeDirectory()
    {
        if (_dialogs.PickFolder(ExtraKnowledgeDirectory) is { } folder)
        {
            ExtraKnowledgeDirectory = folder;
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => _shell.OpenFolder(Paths.Root);

    [RelayCommand]
    private void OpenLogFolder() => _shell.OpenFolder(Paths.Logs);

    [RelayCommand]
    private void OpenKnowledgeFolder() => _shell.OpenFolder(Paths.Knowledge);

    private void LoadFrom(AppSettings s)
    {
        MaxFileSizeText = s.MaxFileSizeMiB.ToString(CultureInfo.InvariantCulture);
        AutoSaveProjects = s.AutoSaveProjects;
        EnableAnimations = s.EnableAnimations;
        ExportDirectory = s.ExportDirectory ?? string.Empty;
        ExtraKnowledgeDirectory = s.ExtraKnowledgeDirectory ?? string.Empty;
    }
}
