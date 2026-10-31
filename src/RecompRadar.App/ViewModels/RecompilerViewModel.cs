using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RecompRadar.App.Services;
using RecompRadar.Recompilation.Build;
using RecompRadar.Recompilation.Compatibility;
using RecompRadar.Recompilation.Pipeline;

namespace RecompRadar.App.ViewModels;

/// <summary>
/// Recompile and Play. Every button does what it says: Recompile runs the real pipeline (analysis, C# generation, .NET build,
/// validation); Play starts the program that was just built. When something is not possible the exact reason is shown.
/// </summary>
public sealed partial class RecompilerViewModel : PageViewModel
{
    private const int MaxLogLines = 400;
    private const int MaxRunLines = 5000;

    private readonly AnalysisSession _session;
    private readonly CodeAnalysisService _code;
    private readonly IRecompilerService _recompiler;
    private readonly IFileDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly NotificationService _notifications;
    private readonly ILogger<RecompilerViewModel> _logger;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private CancellationTokenSource? _buildCts;
    private CancellationTokenSource? _runCts;
    private RecompileResult? _lastResult;

    public RecompilerViewModel(
        AnalysisSession session,
        CodeAnalysisService code,
        IRecompilerService recompiler,
        IFileDialogService dialogs,
        IShellService shell,
        NotificationService notifications,
        ILogger<RecompilerViewModel> logger)
        : base("recompiler", "Recompiler & Build", "Translate the program into a Windows executable, check it, and run it", "IconBuild")
    {
        _session = session;
        _code = code;
        _recompiler = recompiler;
        _dialogs = dialogs;
        _shell = shell;
        _notifications = notifications;
        _logger = logger;

        PlatformChoices =
        [
            new PlatformChoice(null, "Auto (hints only)"),
            new PlatformChoice(ConsoleBackendRegistry.Ps2, "PlayStation 2"),
            new PlatformChoice(ConsoleBackendRegistry.Ps3, "PlayStation 3"),
            new PlatformChoice(ConsoleBackendRegistry.Ps4, "PlayStation 4"),
            new PlatformChoice(ConsoleBackendRegistry.GenericMips32, "Generic MIPS32"),
        ];
        _selectedPlatform = PlatformChoices[0];

        var sdk = recompiler.Sdk;
        SdkAvailable = sdk is not null;
        SdkText = sdk is null
            ? $"No .NET SDK {DotnetSdk.MinimumMajorVersion} or newer was found, so programs cannot be built. Install the SDK from https://dotnet.microsoft.com/download."
            : $"Builds use the .NET SDK {sdk.Version} found on this computer. Nothing is downloaded.";

        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AnalysisSession.Current))
            {
                ResetForNewFile();
            }
        };
        code.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CodeAnalysisService.Result) or nameof(CodeAnalysisService.IsRunning))
            {
                Reassess();
            }
        };
        ResetForNewFile();
    }

    public IReadOnlyList<PlatformChoice> PlatformChoices { get; }

    public ObservableCollection<NoteRow> Notes { get; } = [];

    public ObservableCollection<ModuleRow> Modules { get; } = [];

    public ObservableCollection<StageRow> Stages { get; } = [];

    public ObservableCollection<string> BuildLog { get; } = [];

    public ObservableCollection<KeyValueRow> ResultRows { get; } = [];

    public ObservableCollection<string> RunLines { get; } = [];

    public bool SdkAvailable { get; }

    public string SdkText { get; }

    [ObservableProperty]
    private bool _hasFile;

    [ObservableProperty]
    private string _fileText = string.Empty;

    [ObservableProperty]
    private PlatformChoice _selectedPlatform;

    [ObservableProperty]
    private CompatibilityLevel _level = CompatibilityLevel.Unknown;

    [ObservableProperty]
    private string _headline = string.Empty;

    [ObservableProperty]
    private string _platformSourceText = string.Empty;

    [ObservableProperty]
    private string _backendTitle = string.Empty;

    [ObservableProperty]
    private string _backendSummary = string.Empty;

    [ObservableProperty]
    private bool _validate = true;

    [ObservableProperty]
    private bool _allowIncomplete;

    [ObservableProperty]
    private bool _canOfferIncomplete;

    [ObservableProperty]
    private string _outputDirectory = string.Empty;

    [ObservableProperty]
    private bool _isBuilding;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _recompileDisabledReason = string.Empty;

    [ObservableProperty]
    private string _playDisabledReason = string.Empty;

    [ObservableProperty]
    private bool _hasBuildLog;

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private bool _resultSucceeded;

    [ObservableProperty]
    private string _resultTitle = string.Empty;

    [ObservableProperty]
    private string _resultCategory = string.Empty;

    [ObservableProperty]
    private string _resultExplanation = string.Empty;

    [ObservableProperty]
    private string _validationText = string.Empty;

    [ObservableProperty]
    private string _runSummary = string.Empty;

    public RecompileResult? LastResult => _lastResult;

    public CompatibilityAssessment? Compatibility { get; private set; }

    partial void OnSelectedPlatformChanged(PlatformChoice value) => Reassess();

    partial void OnAllowIncompleteChanged(bool value) => RefreshState();

    partial void OnOutputDirectoryChanged(string value) => RefreshState();

    partial void OnIsBuildingChanged(bool value) => RefreshState();

    partial void OnIsRunningChanged(bool value) => RefreshState();

    private void ResetForNewFile()
    {
        _lastResult = null;
        HasResult = false;
        Stages.Clear();
        BuildLog.Clear();
        HasBuildLog = false;
        RunLines.Clear();
        ResultRows.Clear();
        RunSummary = string.Empty;

        var analysis = _session.Current?.Analysis;
        HasFile = analysis is { Parsed: true };
        FileText = analysis is null
            ? string.Empty
            : $"{analysis.File.FileName} – {analysis.Architecture?.Description ?? "architecture unknown"}";
        OutputDirectory = analysis is { Parsed: true } && !string.IsNullOrEmpty(analysis.File.Sha256)
            ? _recompiler.DefaultOutputDirectory(analysis.File.FileName, analysis.File.Sha256)
            : string.Empty;
        Reassess();
    }

    private void Reassess()
    {
        Notes.Clear();
        Modules.Clear();
        var analysis = _session.Current?.Analysis;
        var assessment = CompatibilityEngine.Assess(new CompatibilityInput(
            analysis?.Elf,
            analysis?.Platform,
            SelectedPlatform.Id,
            _code.Result?.Graph));
        Compatibility = assessment;
        Level = assessment.Level;
        Headline = assessment.Headline;
        PlatformSourceText = assessment.PlatformId is null
            ? "No target system assumed."
            : $"Target system: {assessment.PlatformId} ({assessment.PlatformSource}).";
        foreach (var note in assessment.Notes)
        {
            Notes.Add(new NoteRow(note.Kind.ToString(), note.Text));
        }

        BackendTitle = assessment.Backend?.DisplayName ?? "No backend";
        BackendSummary = assessment.Backend?.Summary ?? assessment.RecompileBlockedReason ?? string.Empty;
        foreach (var module in assessment.Backend?.Modules ?? [])
        {
            Modules.Add(new ModuleRow(module.Name, module.State.ToString(), module.Note));
        }

        CanOfferIncomplete = assessment.CanRecompileIncomplete && !assessment.CanRecompile;
        if (!CanOfferIncomplete)
        {
            AllowIncomplete = false;
        }

        RefreshState();
    }

    private void RefreshState()
    {
        RecompileDisabledReason = ComputeRecompileDisabledReason() ?? string.Empty;
        PlayDisabledReason = ComputePlayDisabledReason() ?? string.Empty;
        RecompileCommand.NotifyCanExecuteChanged();
        PlayCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        CancelBuildCommand.NotifyCanExecuteChanged();
        OpenBuildFolderCommand.NotifyCanExecuteChanged();
        OpenReportCommand.NotifyCanExecuteChanged();
    }

    private string? ComputeRecompileDisabledReason()
    {
        if (IsBuilding)
        {
            return "A build is running.";
        }

        if (!HasFile)
        {
            return "Analyze an ELF file first (Binary Analyzer).";
        }

        // A definite "no" from the compatibility check is more precise than the generic state of the code analysis.
        if (Compatibility is { } c && !c.CanRecompile && !(AllowIncomplete && c.CanRecompileIncomplete) && c.Level != CompatibilityLevel.Unknown)
        {
            return (c.RecompileBlockedReason ?? "The compatibility check does not allow a build.") +
                   (c.CanRecompileIncomplete ? " You can switch on “Allow incomplete build” to translate the supported parts anyway." : string.Empty);
        }

        if (_code.IsRunning)
        {
            return "The code analysis is still running.";
        }

        if (_code.Result is null)
        {
            return _code.Status;
        }

        if (!SdkAvailable)
        {
            return SdkText;
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            return "Choose an output folder.";
        }

        return null;
    }

    private string? ComputePlayDisabledReason()
    {
        if (IsRunning)
        {
            return "The program is already running.";
        }

        if (IsBuilding)
        {
            return "A build is running.";
        }

        if (_lastResult is null)
        {
            return "Nothing has been built yet. Press Recompile first.";
        }

        return _lastResult.PlayBlockedReason;
    }

    private bool CanRecompile() => string.IsNullOrEmpty(RecompileDisabledReason);

    private bool CanPlay() => string.IsNullOrEmpty(PlayDisabledReason);

    private bool CanStop() => IsRunning;

    private bool CanCancelBuild() => IsBuilding;

    private bool HasBuildFolder() => _lastResult?.ProjectDirectory is { } folder && Directory.Exists(folder);

    private bool HasReport() => _lastResult?.ReportPath is { } report && File.Exists(report);

    [RelayCommand]
    private void BrowseOutput()
    {
        var picked = _dialogs.PickFolder(Directory.Exists(OutputDirectory) ? OutputDirectory : null);
        if (picked is not null)
        {
            OutputDirectory = picked;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRecompile))]
    private async Task RecompileAsync()
    {
        if (_code.Result is not { } code || _session.Current?.Analysis is not { } analysis)
        {
            return;
        }

        _buildCts = new CancellationTokenSource();
        IsBuilding = true;
        _lastResult = null;
        HasResult = false;
        Stages.Clear();
        BuildLog.Clear();
        HasBuildLog = false;
        ResultRows.Clear();
        RunLines.Clear();
        RunSummary = string.Empty;
        foreach (var stage in Enum.GetValues<PipelineStage>())
        {
            Stages.Add(new StageRow(StageName(stage)));
        }

        try
        {
            var request = new RecompileRequest(
                code.SourcePath,
                code.Elf,
                OutputDirectory,
                analysis.Platform,
                SelectedPlatform.Id,
                AllowIncomplete && CanOfferIncomplete,
                Validate,
                ProjectName: Path.GetFileNameWithoutExtension(analysis.File.FileName));
            var progress = new Progress<PipelineProgress>(OnProgress);
            var result = await _recompiler.RecompileAsync(request, progress, _buildCts.Token);
            _lastResult = result;
            ShowResult(result);
            if (result.Success)
            {
                _notifications.Success("Program built", $"{Path.GetFileName(result.ExecutablePath)} – {result.Category.Title()}.");
            }
            else
            {
                _notifications.Show(NotificationKind.Warning, "No usable program was produced", result.FailureReason ?? "The build did not succeed.");
            }
        }
        catch (OperationCanceledException)
        {
            _notifications.Info("Build cancelled", "The build was stopped. No program is available from this attempt.");
            ResultTitle = "Build cancelled";
            HasResult = true;
            ResultSucceeded = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(ex, "The recompilation failed unexpectedly");
            _notifications.Error("The build failed unexpectedly", "RecompRadar could not complete the build. Details were written to the log file.", ex.Message);
        }
        finally
        {
            IsBuilding = false;
            _buildCts?.Dispose();
            _buildCts = null;
            RefreshState();
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelBuild))]
    private void CancelBuild() => _buildCts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private async Task PlayAsync()
    {
        if (_lastResult?.ExecutablePath is not { } exe)
        {
            return;
        }

        _runCts = new CancellationTokenSource();
        IsRunning = true;
        RunLines.Clear();
        RunSummary = "Running…";
        try
        {
            var result = await _recompiler.RunAsync(
                exe,
                line => Post(() => AddRunLine(line)),
                line => Post(() => AddRunLine("[error] " + line)),
                _runCts.Token);
            RunSummary = DescribeRun(result);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Starting the built program failed");
            RunSummary = "The program could not be started: " + ex.Message;
            _notifications.Error("The program could not be started", ex.Message);
        }
        finally
        {
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _runCts?.Cancel();

    [RelayCommand(CanExecute = nameof(HasBuildFolder))]
    private void OpenBuildFolder()
    {
        if (_lastResult?.ProjectDirectory is { } folder)
        {
            _shell.OpenFolder(folder);
        }
    }

    [RelayCommand(CanExecute = nameof(HasReport))]
    private void OpenReport()
    {
        if (_lastResult?.ReportPath is { } report)
        {
            _shell.RevealFile(report);
        }
    }

    private void Post(Action action)
    {
        if (_ui is null)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }

    private void AddRunLine(string line)
    {
        if (RunLines.Count >= MaxRunLines)
        {
            RunLines.RemoveAt(0);
        }

        RunLines.Add(line);
    }

    private void OnProgress(PipelineProgress p)
    {
        var row = Stages.FirstOrDefault(s => s.Name == StageName(p.Stage));
        if (row is null)
        {
            return;
        }

        if (p.Message.StartsWith("done: ", StringComparison.Ordinal))
        {
            row.Status = "done";
            row.Summary = p.Message["done: ".Length..];
        }
        else if (p.Message.StartsWith("failed: ", StringComparison.Ordinal))
        {
            row.Status = "failed";
            row.Summary = p.Message["failed: ".Length..];
        }
        else if (p.Stage == PipelineStage.Build && row.Status == "running")
        {
            if (BuildLog.Count >= MaxLogLines)
            {
                BuildLog.RemoveAt(0);
            }

            BuildLog.Add(p.Message);
            HasBuildLog = true;
        }
        else
        {
            row.Status = "running";
            row.Summary = p.Message;
        }
    }

    private void ShowResult(RecompileResult result)
    {
        // The pipeline's own stage list is authoritative; the live progress may lag behind it.
        foreach (var outcome in result.Stages)
        {
            var row = Stages.FirstOrDefault(s => s.Name == StageName(outcome.Stage));
            if (row is not null)
            {
                row.Status = outcome.Succeeded ? "done" : "failed";
                row.Summary = outcome.Summary;
            }
        }

        foreach (var row in Stages.Where(s => s.Status is "waiting" or "running"))
        {
            row.Status = "skipped";
            row.Summary = string.Empty;
        }

        HasResult = true;
        ResultSucceeded = result.Success;
        ResultTitle = result.Success
            ? (result.Incomplete ? "Incomplete program built" : "Program built")
            : result.Build is { Success: true } ? "Program built but not trusted" : "No program was produced";
        ResultCategory = result.Success || result.Build?.Success == true ? result.Category.Title() : BuildCategory.Unsupported.Title();
        ResultExplanation = result.Success || result.Build?.Success == true
            ? result.Category.Explanation() + (result.Incomplete ? " Parts that cannot be translated stop the program when they are reached." : string.Empty)
            : result.FailureReason ?? string.Empty;
        ValidationText = result.Validation switch
        {
            { Passed: true } v => $"Matches the reference interpreter for this run: same end state ({v.Reference.Status}), output, instruction count ({v.Reference.InstructionsExecuted:N0}) and registers. This does not prove equivalence for other inputs or paths.",
            { } v => "Differs from the reference interpreter: " + string.Join(" ", v.Differences.Take(4)),
            null => result.ValidationSkippedReason ?? "Not validated.",
        };

        ResultRows.Clear();
        if (result.Build is { } build)
        {
            ResultRows.Add(new KeyValueRow("Build folder", build.ProjectDirectory, "Verified", string.Empty));
            if (result.ExecutablePath is not null)
            {
                ResultRows.Add(new KeyValueRow("Program", result.ExecutablePath, "Verified", "A real Windows program. It needs the .NET 10 runtime."));
                ResultRows.Add(new KeyValueRow("Program SHA-256", build.ExecutableSha256 ?? string.Empty, "Verified", "The same input builds to the same file."));
            }

            ResultRows.Add(new KeyValueRow(".NET SDK", build.SdkVersion ?? string.Empty, "Verified", $"{build.Duration.TotalSeconds:0.0} s, {build.WarningCount} warning(s), {build.ErrorCount} error(s)"));
        }

        if (result.Generation is { } g)
        {
            ResultRows.Add(new KeyValueRow("Translated", $"{g.Functions} function(s), {g.Blocks} block(s), {g.GuestInstructions} guest instruction(s)", "Verified", $"{g.GeneratedSourceBytes:N0} bytes of C#"));
        }

        if (result.ReportPath is not null)
        {
            ResultRows.Add(new KeyValueRow("Build report", result.ReportPath, "Verified", "JSON with every decision, check and limitation."));
        }

        RefreshState();
    }

    private static string DescribeRun(LaunchResult result)
    {
        if (result.Cancelled)
        {
            return "Stopped by you.";
        }

        if (result.TimedOut)
        {
            return "Stopped after the time limit (30 minutes).";
        }

        if (result.Report is { } report)
        {
            var rate = report.ElapsedMilliseconds > 0 ? $", {report.InstructionsExecuted / report.ElapsedMilliseconds / 1000.0:0.0} M guest instructions/s" : string.Empty;
            var message = string.IsNullOrEmpty(report.Message) ? string.Empty : $" – {report.Message}";
            return string.Create(CultureInfo.CurrentCulture, $"{report.Status}{message}. Exit code {result.ExitCode}; {report.InstructionsExecuted:N0} guest instructions in {report.ElapsedMilliseconds:0.#} ms{rate}.");
        }

        return $"The program ended with exit code {result.ExitCode} and wrote no run report.";
    }

    private static string StageName(PipelineStage stage) => stage switch
    {
        PipelineStage.Prepare => "Read file",
        PipelineStage.Analyze => "Analyze code",
        PipelineStage.Compatibility => "Check compatibility",
        PipelineStage.Generate => "Generate C#",
        PipelineStage.Build => "Build with .NET",
        PipelineStage.Validate => "Validate",
        _ => "Write report",
    };
}
