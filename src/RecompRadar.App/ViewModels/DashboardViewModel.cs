using RecompRadar.Infrastructure;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RecompRadar.App.Services;
using RecompRadar.Core;
using RecompRadar.Core.Model;

namespace RecompRadar.App.ViewModels;

public sealed partial class DashboardViewModel : PageViewModel
{
    private readonly AnalysisSession _session;
    private readonly IProjectStore _store;
    private readonly IFileDialogService _dialogs;
    private readonly ILogger<DashboardViewModel> _logger;

    public DashboardViewModel(AnalysisSession session, IProjectStore store, IFileDialogService dialogs, ILogger<DashboardViewModel> logger)
        : base("dashboard", "Dashboard", "Static analysis of console executables for native porting research", "IconDashboard")
    {
        _session = session;
        _store = store;
        _dialogs = dialogs;
        _logger = logger;
        session.ProjectsChanged += async (_, _) => await RefreshAsync();
        _ = RefreshAsync();
    }

    public event EventHandler<string>? NavigationRequested;

    public AnalysisSession Session => _session;

    public ObservableCollection<ProjectSummary> RecentProjects { get; } = [];

    [ObservableProperty]
    private int _projectCount;

    [ObservableProperty]
    private string _lastAnalysisText = "No analysis yet";

    [ObservableProperty]
    private string _lastArchitectureText = "–";

    [ObservableProperty]
    private bool _hasProjects;

    public IReadOnlyList<string> SupportedFormats { get; } =
    [
        "ELF32 and ELF64, little and big endian",
        "Architectures: x86, x86-64, MIPS, PowerPC, ARM, ARM64, RISC-V, Cell SPU",
        "Executables, shared objects and relocatable objects",
    ];

    public IReadOnlyList<string> NotSupported { get; } =
    [
        "Encrypted or signed containers (SELF, ~PSP, PBP) – RecompRadar never decrypts anything",
        "PS-X EXE, DOL, XEX and PE files are recognised but not analyzed yet",
        "Disassembly beyond a small MIPS32 proof of concept",
    ];

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (_dialogs.PickFileToOpen() is { } path)
        {
            await _session.AnalyzeAsync(path);
        }
    }

    [RelayCommand]
    private async Task DropFilesAsync(string[]? paths)
    {
        if (paths is { Length: > 0 })
        {
            await _session.AnalyzeAsync(paths[0]);
        }
    }

    [RelayCommand]
    private async Task OpenProjectAsync(ProjectSummary? project)
    {
        if (project is not null)
        {
            await _session.OpenProjectAsync(project.Id);
        }
    }

    [RelayCommand]
    private void GoToProjects() => NavigationRequested?.Invoke(this, "projects");

    public async Task RefreshAsync()
    {
        try
        {
            var all = await _store.ListAsync();
            ProjectCount = all.Count;
            HasProjects = all.Count > 0;
            RecentProjects.Clear();
            foreach (var p in all.Take(5))
            {
                RecentProjects.Add(p);
            }

            if (all.Count > 0)
            {
                LastAnalysisText = $"{all[0].FileName} – {all[0].AnalyzedAtUtc.ToLocalTime():g}";
                LastArchitectureText = all[0].Architecture;
            }
            else
            {
                LastAnalysisText = "No analysis yet";
                LastArchitectureText = "–";
            }
        }
        catch (Exception ex) when (ex is ProjectStoreException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "The dashboard could not read the project list");
        }
    }
}
