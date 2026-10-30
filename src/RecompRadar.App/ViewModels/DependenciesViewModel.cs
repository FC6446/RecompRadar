using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using RecompRadar.App.Services;
using RecompRadar.Core.Model;

namespace RecompRadar.App.ViewModels;

public sealed partial class DependenciesViewModel : PageViewModel
{
    private readonly AnalysisSession _session;

    public DependenciesViewModel(AnalysisSession session)
        : base("dependencies", "Dependencies", "Libraries, imported symbols and recognised API names", "IconLink")
    {
        _session = session;
        _importsView = CollectionViewSource.GetDefaultView(Array.Empty<NameRow>());
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AnalysisSession.Current))
            {
                Rebuild();
            }
        };
        Rebuild();
    }

    public AnalysisSession Session => _session;

    public ObservableCollection<NameRow> Libraries { get; } = [];

    public ObservableCollection<ApiMatchRow> ApiMatches { get; } = [];

    public ObservableCollection<ApiMatchRow> HardwareHints { get; } = [];

    public ObservableCollection<string> Notes { get; } = [];

    [ObservableProperty]
    private ICollectionView _importsView;

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private bool _noSymbols;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _importFilter = string.Empty;

    [ObservableProperty]
    private string _knowledgeBase = string.Empty;

    partial void OnImportFilterChanged(string value) => ImportsView.Refresh();

    private void Rebuild()
    {
        Libraries.Clear();
        ApiMatches.Clear();
        HardwareHints.Clear();
        Notes.Clear();
        ImportFilter = string.Empty;

        if (_session.Current?.Analysis is not { Parsed: true, Dependencies: { } deps })
        {
            HasResult = false;
            ImportsView = CollectionViewSource.GetDefaultView(Array.Empty<NameRow>());
            return;
        }

        HasResult = true;
        NoSymbols = !deps.HasSymbols;
        KnowledgeBase = deps.KnowledgeBaseDescription;
        var libraryFamilies = deps.ApiMatches.Where(m => m.Kind == ApiMatchKind.Library)
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().ApiFamily, StringComparer.OrdinalIgnoreCase);
        foreach (var lib in deps.Libraries)
        {
            Libraries.Add(new NameRow(lib, libraryFamilies.TryGetValue(lib, out var family) ? family : "not in the knowledge base"));
        }

        var importFamilies = deps.ApiMatches.Where(m => m.Kind == ApiMatchKind.ImportedSymbol)
            .GroupBy(m => m.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().ApiFamily, StringComparer.Ordinal);
        var imports = deps.ImportedSymbols.Select(s => new NameRow(s, importFamilies.TryGetValue(s, out var f) ? f : string.Empty)).ToList();
        var view = new ListCollectionView(imports)
        {
            Filter = o => ImportFilter.Length == 0 || ((NameRow)o).Name.Contains(ImportFilter, StringComparison.OrdinalIgnoreCase),
        };
        ImportsView = view;

        foreach (var m in deps.ApiMatches)
        {
            ApiMatches.Add(ToRow(m));
        }

        foreach (var m in deps.HardwareHints)
        {
            HardwareHints.Add(ToRow(m));
        }

        foreach (var n in deps.Notes)
        {
            Notes.Add(n);
        }

        Summary = $"{deps.Libraries.Count} shared libraries · {deps.ImportedSymbols.Count:N0} imported symbols · {deps.ApiMatches.Count:N0} recognised API names · {deps.HardwareHints.Count} hardware hints";
    }

    private static ApiMatchRow ToRow(ApiMatch m) => new(
        m.Name,
        m.Kind switch
        {
            ApiMatchKind.Library => "Library",
            ApiMatchKind.ImportedSymbol => "Import",
            ApiMatchKind.DefinedSymbol => "Defined symbol",
            _ => "String",
        },
        m.Category,
        m.ApiFamily,
        string.Join(", ", m.PlatformHints),
        m.Description);
}
