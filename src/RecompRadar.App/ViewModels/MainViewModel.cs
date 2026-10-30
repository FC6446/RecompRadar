using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RecompRadar.App.Services;
using RecompRadar.Core;

namespace RecompRadar.App.ViewModels;

public abstract partial class PageViewModel : ObservableObject
{
    protected PageViewModel(string key, string title, string subtitle, string iconKey)
    {
        Key = key;
        Title = title;
        Subtitle = subtitle;
        IconKey = iconKey;
    }

    public string Key { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public string IconKey { get; }
}

public sealed partial class NavItem : ObservableObject
{
    public NavItem(PageViewModel page)
    {
        Page = page;
    }

    public PageViewModel Page { get; }

    public string Title => Page.Title;

    public string IconKey => Page.IconKey;

    [ObservableProperty]
    private bool _isSelected;
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AnalysisSession _session;

    public MainViewModel(
        AnalysisSession session,
        NotificationService notifications,
        DashboardViewModel dashboard,
        BinaryAnalyzerViewModel analyzer,
        ArchitectureViewModel architecture,
        DependenciesViewModel dependencies,
        ReportViewModel report,
        ProjectsViewModel projects,
        SettingsViewModel settings)
    {
        _session = session;
        Notifications = notifications;
        Session = session;
        NavItems =
        [
            new NavItem(dashboard),
            new NavItem(analyzer),
            new NavItem(architecture),
            new NavItem(dependencies),
            new NavItem(report),
            new NavItem(projects),
            new NavItem(settings),
        ];

        foreach (var item in NavItems)
        {
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(NavItem.IsSelected) && item.IsSelected)
                {
                    CurrentPage = item.Page;
                }
            };
        }

        _currentPage = dashboard;
        NavItems[0].IsSelected = true;

        // After a result was loaded (new analysis or opened project) show the analyzer page.
        session.ResultLoaded += (_, _) => Navigate("analyzer");
        dashboard.NavigationRequested += (_, key) => Navigate(key);
    }

    public IReadOnlyList<NavItem> NavItems { get; }

    public NotificationService Notifications { get; }

    public AnalysisSession Session { get; }

    public string VersionText { get; } = $"Version {ToolInfo.Version}";

    [ObservableProperty]
    private PageViewModel _currentPage;

    public void Navigate(string key)
    {
        var item = NavItems.FirstOrDefault(n => n.Page.Key == key);
        if (item is not null)
        {
            item.IsSelected = true;
            foreach (var other in NavItems.Where(n => n != item))
            {
                other.IsSelected = false;
            }
        }
    }

    [RelayCommand]
    private void CancelAnalysis() => _session.Cancel();

    /// <summary>Files dropped anywhere on the window are analyzed (only the first one if several are dropped).</summary>
    [RelayCommand]
    private async Task DropFilesAsync(string[]? paths)
    {
        if (paths is not { Length: > 0 })
        {
            return;
        }

        if (paths.Length > 1)
        {
            Notifications.Info("Several files dropped", $"Only the first file ({Path.GetFileName(paths[0])}) is analyzed. Analyze the others one after another.");
        }

        await _session.AnalyzeAsync(paths[0]);
    }
}
