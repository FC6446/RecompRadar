using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using RecompRadar.App.Services;
using RecompRadar.App.ViewModels;
using RecompRadar.Core;
using RecompRadar.Infrastructure;
using RecompRadar.Portability;
using RecompRadar.Portability.Export;
using RecompRadar.Recompilation.Build;
using RecompRadar.Recompilation.Pipeline;
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

/// <summary>Stands in for the build system so that page tests neither run the .NET SDK nor start programs.</summary>
internal sealed class FakeRecompiler : IRecompilerService
{
    private readonly string _root;

    public FakeRecompiler(string root) => _root = root;

    public DotnetSdkInfo? Sdk { get; set; } = new("dotnet", "10.0.0");

    public RecompileRequest? LastRequest { get; private set; }

    public int RecompileCalls { get; private set; }

    public int RunCalls { get; private set; }

    public string? LastRunPath { get; private set; }

    /// <summary>When set, the build waits for this gate (so tests can observe the running state).</summary>
    public TaskCompletionSource? BuildGate { get; set; }

    /// <summary>When true, a started program keeps running until it is stopped.</summary>
    public bool HoldRun { get; set; }

    public Func<RecompileRequest, RecompileResult>? ResultFactory { get; set; }

    public string DefaultOutputDirectory(string fileName, string sha256) => Path.Combine(_root, "Builds", Path.GetFileNameWithoutExtension(fileName));

    public async Task<RecompileResult> RecompileAsync(RecompileRequest request, IProgress<PipelineProgress> progress, CancellationToken cancellationToken)
    {
        LastRequest = request;
        RecompileCalls++;
        progress.Report(new PipelineProgress(PipelineStage.Prepare, "Reading the input file"));
        if (BuildGate is not null)
        {
            await BuildGate.Task.WaitAsync(cancellationToken);
        }

        return (ResultFactory ?? Succeeded)(request);
    }

    public async Task<LaunchResult> RunAsync(string executablePath, Action<string> onStandardOutput, Action<string> onStandardError, CancellationToken cancellationToken)
    {
        RunCalls++;
        LastRunPath = executablePath;
        onStandardOutput("hello from the built program");
        onStandardError("a warning");
        if (HoldRun)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new LaunchResult(-1, string.Empty, string.Empty, false, true, TimeSpan.Zero, null);
            }
        }

        return new LaunchResult(0, "hello from the built program\n", "a warning\n", false, false, TimeSpan.FromMilliseconds(5),
            new RunReport("Exited", 0, 1234, null, "hello from the built program\n", new ulong[128], 1.5));
    }

    public RecompileResult Succeeded(RecompileRequest request)
    {
        var directory = Path.Combine(_root, "Builds", "fake");
        Directory.CreateDirectory(directory);
        var exe = Path.Combine(directory, "fake.exe");
        File.WriteAllText(exe, "not a real program");
        var report = Path.Combine(directory, "build-report.json");
        File.WriteAllText(report, "{}");
        return new RecompileResult
        {
            Success = true,
            Category = BuildCategory.TranslatedWithRuntime,
            SourceSha256 = new string('a', 64),
            Compatibility = RecompRadar.Recompilation.Compatibility.CompatibilityEngine.Assess(
                new RecompRadar.Recompilation.Compatibility.CompatibilityInput(request.Elf, null, request.PlatformOverride)),
            Build = new BuildResult { Success = true, ProjectDirectory = directory, ExecutablePath = exe, ExecutableSha256 = new string('b', 64), SdkVersion = "10.0.0", Duration = TimeSpan.FromSeconds(2) },
            Stages = [new StageOutcome(PipelineStage.Prepare, true, TimeSpan.Zero, "ok"), new StageOutcome(PipelineStage.Build, true, TimeSpan.Zero, "built")],
            ProjectDirectory = directory,
            ExecutablePath = exe,
            ReportPath = report,
        };
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
        CodeService = new CodeAnalysisService(Session, NullLogger<CodeAnalysisService>.Instance);
        Recompiler = new FakeRecompiler(Paths.Root);
        Disassembler = new DisassemblerViewModel(CodeService);
        Consoles = new ConsolesViewModel(Session);
        RecompilerPage = new RecompilerViewModel(Session, CodeService, Recompiler, Dialogs, Shell, Notifications, NullLogger<RecompilerViewModel>.Instance);
        Dependencies = new DependenciesViewModel(Session);
        Report = new ReportViewModel(Session, export, Dialogs, Shell, Settings, Notifications);
        Projects = new ProjectsViewModel(Store, Session, Confirm, Shell, Notifications, NullLogger<ProjectsViewModel>.Instance);
        SettingsPage = new SettingsViewModel(Settings, Paths, Dialogs, Shell, Notifications);
        Main = new MainViewModel(Session, Notifications, Dashboard, Analyzer, Architecture, Disassembler, Consoles, RecompilerPage, Dependencies, Report, Projects, SettingsPage);
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

    public CodeAnalysisService CodeService { get; }

    public FakeRecompiler Recompiler { get; }

    public DisassemblerViewModel Disassembler { get; }

    public ConsolesViewModel Consoles { get; }

    public RecompilerViewModel RecompilerPage { get; }

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
