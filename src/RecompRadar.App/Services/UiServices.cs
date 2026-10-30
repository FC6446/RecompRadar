using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using RecompRadar.Core;

namespace RecompRadar.App.Services;

public interface IFileDialogService
{
    string? PickFileToOpen();

    string? PickSavePath(string suggestedFileName, string filter, string? initialDirectory);

    string? PickFolder(string? initialDirectory);
}

public sealed class WpfFileDialogService : IFileDialogService
{
    public string? PickFileToOpen()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose an executable file to analyze",
            Filter = "ELF and other executables|*.elf;*.prx;*.so;*.o;*.axf;*.out;*.bin;*|All files|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickSavePath(string suggestedFileName, string filter, string? initialDirectory)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export report",
            FileName = suggestedFileName,
            Filter = filter,
            OverwritePrompt = true,
            AddExtension = true,
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickFolder(string? initialDirectory)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder" };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}

/// <summary>Opens Explorer for report and data folders. It never opens or runs an analyzed file.</summary>
public interface IShellService
{
    void RevealFile(string path);

    void OpenFolder(string path);
}

public sealed class ShellService : IShellService
{
    public void RevealFile(string path)
    {
        if (File.Exists(path))
        {
            Start("explorer.exe", $"/select,\"{path}\"");
        }
        else if (Path.GetDirectoryName(path) is { } directory && Directory.Exists(directory))
        {
            OpenFolder(directory);
        }
    }

    public void OpenFolder(string path)
    {
        if (Directory.Exists(path))
        {
            Start("explorer.exe", $"\"{path}\"");
        }
    }

    private static void Start(string fileName, string arguments) =>
        Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = true });
}

public interface IDialogService
{
    bool Confirm(string title, string message);
}

public sealed class WpfDialogService : IDialogService
{
    public bool Confirm(string title, string message) =>
        MessageBox.Show(Application.Current?.MainWindow, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
}

public enum NotificationKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>Banner shown at the bottom of the window. Errors carry a plain-language explanation and stay until dismissed.</summary>
public sealed partial class NotificationService : ObservableObject
{
    private CancellationTokenSource? _hide;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private NotificationKind _kind;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private string _details = string.Empty;

    public void Show(NotificationKind kind, string title, string message, string? details = null)
    {
        void Apply()
        {
            _hide?.Cancel();
            Kind = kind;
            Title = title;
            Message = message;
            Details = details ?? string.Empty;
            IsVisible = true;
            if (kind is NotificationKind.Info or NotificationKind.Success)
            {
                var cts = _hide = new CancellationTokenSource();
                _ = HideLaterAsync(cts.Token);
            }
        }

        if (Application.Current?.Dispatcher is { HasShutdownStarted: false } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(Apply);
        }
        else
        {
            Apply();
        }
    }

    public void Error(string title, string message, string? details = null) => Show(NotificationKind.Error, title, message, details);

    public void Success(string title, string message) => Show(NotificationKind.Success, title, message);

    public void Info(string title, string message) => Show(NotificationKind.Info, title, message);

    [RelayCommand]
    private void Dismiss()
    {
        _hide?.Cancel();
        IsVisible = false;
    }

    private async Task HideLaterAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(7), token);
            IsVisible = false;
        }
        catch (OperationCanceledException)
        {
            // A newer message replaced this one.
        }
    }
}

/// <summary>Holds the current settings so every page sees changes immediately.</summary>
public sealed partial class SettingsService : ObservableObject
{
    private readonly ISettingsStore _store;

    public SettingsService(ISettingsStore store)
    {
        _store = store;
        _current = store.Load();
    }

    [ObservableProperty]
    private AppSettings _current;

    public void Update(AppSettings settings)
    {
        _store.Save(settings);
        Current = settings;
    }
}
