using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using RecompRadar.App.Services;
using RecompRadar.App.ViewModels;
using RecompRadar.Core;
using RecompRadar.Infrastructure;
using RecompRadar.Portability;
using RecompRadar.Portability.Export;
using RecompRadar.TestSupport;

namespace RecompRadar.App.Tests;

/// <summary>All WPF tests share one collection so they never run in parallel.</summary>
[CollectionDefinition("WPF", DisableParallelization = true)]
public sealed class WpfCollection;

/// <summary>Runs an async test on an STA thread with a WPF dispatcher, the environment the view models are designed for.</summary>
internal static class Sta
{
    public static void Run(Func<Task> test)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            Task task;
            try
            {
                task = test();
            }
            catch (Exception ex)
            {
                error = ex;
                return;
            }

            task.ContinueWith(
                t =>
                {
                    error = t.Exception?.GetBaseException();
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
                },
                TaskScheduler.Default);
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}

internal sealed class FakeFileDialogs : IFileDialogService
{
    public string? OpenPath { get; set; }

    public string? SavePath { get; set; }

    public string? Folder { get; set; }

    public string? LastSuggestedName { get; private set; }

    public string? PickFileToOpen() => OpenPath;

    public string? PickSavePath(string suggestedFileName, string filter, string? initialDirectory)
    {
        LastSuggestedName = suggestedFileName;
        return SavePath;
    }

    public string? PickFolder(string? initialDirectory) => Folder;
}

internal sealed class FakeShell : IShellService
{
    public List<string> Revealed { get; } = [];

    public List<string> Opened { get; } = [];

    public void RevealFile(string path) => Revealed.Add(path);

    public void OpenFolder(string path) => Opened.Add(path);
}

internal sealed class FakeConfirm : IDialogService
{
    public bool Answer { get; set; }

    public int Asked { get; private set; }

    public bool Confirm(string title, string message)
    {
        Asked++;
        return Answer;
    }
}

internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "recompradar-apptests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string Sample(string sampleName)
    {
        var bytes = SampleFiles.All().Single(s => s.FileName == sampleName).Bytes;
        var path = File(sampleName);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>The real application services wired together like in <c>App.xaml.cs</c>, but with temporary storage and fake dialogs.</summary>
internal sealed class Harness : IDisposable
{
    private readonly TempFolder _data = new();

    public Harness()
    {
        Paths = new AppPaths(_data.Path);
        Paths.EnsureCreated();
        Notifications = new NotificationService();
        SettingsStore = new JsonSettingsStore(Paths.Settings);
        Settings = new SettingsService(SettingsStore);
        Store = new SqliteProjectStore(Paths.Database);
        Session = new AnalysisSession(new AnalysisPipeline(), Store, Settings, Paths, Notifications, NullLogger<AnalysisSession>.Instance);

        Dialogs = new FakeFileDialogs();
        Shell = new FakeShell();
        Confirm = new FakeConfirm();
        var export = new ReportExportService();

        Dashboard = new DashboardViewModel(Session, Store, Dialogs, NullLogger<DashboardViewModel>.Instance);
        Analyzer = new BinaryAnalyzerViewModel(Session, Dialogs, Notifications);
        Architecture = new ArchitectureViewModel(Session, NullLogger<ArchitectureViewModel>.Instance);
        Dependencies = new DependenciesViewModel(Session);
        Report = new ReportViewModel(Session, export, Dialogs, Shell, Settings, Notifications);
        Projects = new ProjectsViewModel(Store, Session, Confirm, Shell, Notifications, NullLogger<ProjectsViewModel>.Instance);
        SettingsPage = new SettingsViewModel(Settings, Paths, Dialogs, Shell, Notifications);
        Main = new MainViewModel(Session, Notifications, Dashboard, Analyzer, Architecture, Dependencies, Report, Projects, SettingsPage);
    }

    public AppPaths Paths { get; }

    public NotificationService Notifications { get; }

    public JsonSettingsStore SettingsStore { get; }

    public SettingsService Settings { get; }

    public SqliteProjectStore Store { get; }

    public AnalysisSession Session { get; }

    public FakeFileDialogs Dialogs { get; }

    public FakeShell Shell { get; }

    public FakeConfirm Confirm { get; }

    public DashboardViewModel Dashboard { get; }

    public BinaryAnalyzerViewModel Analyzer { get; }

    public ArchitectureViewModel Architecture { get; }

    public DependenciesViewModel Dependencies { get; }

    public ReportViewModel Report { get; }

    public ProjectsViewModel Projects { get; }

    public SettingsViewModel SettingsPage { get; }

    public MainViewModel Main { get; }

    public string DataPath(string name) => _data.File(name);

    public string Sample(string sampleName) => _data.Sample(sampleName);

    public void Dispose() => _data.Dispose();

    /// <summary>Waits (while pumping the dispatcher) until a condition holds.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The expected state was not reached in time.");
            }

            await Task.Delay(25);
        }
    }
}
