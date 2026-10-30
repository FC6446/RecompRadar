using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RecompRadar.App.Services;
using RecompRadar.Core.Model;

namespace RecompRadar.App.ViewModels;

public sealed partial class BinaryAnalyzerViewModel : PageViewModel
{
    private const int MaxDisplayedSymbols = 200_000;

    private readonly AnalysisSession _session;
    private readonly IFileDialogService _dialogs;
    private readonly NotificationService _notifications;

    public BinaryAnalyzerViewModel(AnalysisSession session, IFileDialogService dialogs, NotificationService notifications)
        : base("analyzer", "Binary Analyzer", "ELF header, segments, sections, symbols and memory regions", "IconAnalyzer")
    {
        _session = session;
        _dialogs = dialogs;
        _notifications = notifications;
        _symbolsView = CollectionViewSource.GetDefaultView(Array.Empty<SymbolRow>());
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

    public ObservableCollection<KeyValueRow> HeaderRows { get; } = [];

    public ObservableCollection<SegmentRow> Segments { get; } = [];

    public ObservableCollection<SectionRow> Sections { get; } = [];

    public ObservableCollection<RegionRow> Regions { get; } = [];

    public ObservableCollection<DiagnosticRow> Diagnostics { get; } = [];

    [ObservableProperty]
    private ICollectionView _symbolsView;

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private bool _isParsed;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private string _sha256 = string.Empty;

    [ObservableProperty]
    private string _sizeText = string.Empty;

    [ObservableProperty]
    private string _formatText = string.Empty;

    [ObservableProperty]
    private string _architectureText = string.Empty;

    [ObservableProperty]
    private string _entryText = string.Empty;

    [ObservableProperty]
    private string _problemTitle = string.Empty;

    [ObservableProperty]
    private string _problemText = string.Empty;

    [ObservableProperty]
    private string _symbolSummary = string.Empty;

    [ObservableProperty]
    private string _symbolFilter = string.Empty;

    [ObservableProperty]
    private bool _importsOnly;

    [ObservableProperty]
    private bool _noSymbols;

    partial void OnSymbolFilterChanged(string value) => SymbolsView.Refresh();

    partial void OnImportsOnlyChanged(bool value) => SymbolsView.Refresh();

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
        if (paths is not { Length: > 0 })
        {
            return;
        }

        if (paths.Length > 1)
        {
            _notifications.Info("Several files dropped", $"Only the first file ({Path.GetFileName(paths[0])}) is analyzed. Analyze the others one after another.");
        }

        await _session.AnalyzeAsync(paths[0]);
    }

    [RelayCommand]
    private void Cancel() => _session.Cancel();

    private void Rebuild()
    {
        HeaderRows.Clear();
        Segments.Clear();
        Sections.Clear();
        Regions.Clear();
        Diagnostics.Clear();
        SymbolFilter = string.Empty;
        ImportsOnly = false;

        if (_session.Current is not { } bundle)
        {
            HasResult = false;
            IsParsed = false;
            SymbolsView = CollectionViewSource.GetDefaultView(Array.Empty<SymbolRow>());
            return;
        }

        var analysis = bundle.Analysis;
        HasResult = true;
        IsParsed = analysis.Parsed;
        FileName = analysis.File.FileName;
        FilePath = analysis.File.FullPath;
        Sha256 = string.IsNullOrEmpty(analysis.File.Sha256) ? "(not computed)" : analysis.File.Sha256;
        SizeText = analysis.File.SizeBytes > 0 ? $"{RowMapper.FormatSize(analysis.File.SizeBytes)} ({analysis.File.SizeBytes:N0} bytes)" : "–";
        FormatText = analysis.Format.DisplayName;
        ArchitectureText = analysis.Architecture?.Description ?? "Unknown";
        EntryText = analysis.Elf is { } e0 ? $"0x{e0.Header.Entry:X}" : "–";

        foreach (var d in analysis.Diagnostics)
        {
            Diagnostics.Add(d.ToRow());
        }

        if (!analysis.Parsed)
        {
            var first = analysis.Diagnostics.FirstOrDefault(d => d.Severity >= Severity.Warning);
            ProblemTitle = "This file could not be analyzed";
            ProblemText = first is null ? analysis.Format.Explanation : $"{first.Message} {first.Explanation}".Trim();
            SymbolsView = CollectionViewSource.GetDefaultView(Array.Empty<SymbolRow>());
            SymbolSummary = string.Empty;
            NoSymbols = false;
            return;
        }

        ProblemTitle = string.Empty;
        ProblemText = string.Empty;
        var elf = analysis.Elf!;
        var h = elf.Header;

        HeaderRows.Add(new KeyValueRow("Class", h.Class == ElfClass.Elf64 ? "ELF64 (64-bit)" : "ELF32 (32-bit)", "Verified", string.Empty));
        HeaderRows.Add(new KeyValueRow("Endianness", h.Endianness == Endianness.Little ? "Little endian" : "Big endian", "Verified", string.Empty));
        HeaderRows.Add(new KeyValueRow("Machine", $"{h.MachineName}  (0x{h.Machine:X})", "Verified", string.Empty));
        HeaderRows.Add(new KeyValueRow("Type", $"{h.TypeName}  (0x{h.Type:X})", "Verified", string.Empty));
        HeaderRows.Add(new KeyValueRow("OS/ABI", $"{h.OsAbiName}  (0x{h.OsAbi:X2}), ABI version {h.AbiVersion}", "Verified", string.Empty));
        HeaderRows.Add(new KeyValueRow("Entry point", $"0x{h.Entry:X}", "Verified", elf.EntryPointLocation ?? "not inside a loadable region"));
        HeaderRows.Add(new KeyValueRow("Flags", $"0x{h.Flags:X8}", "Verified", string.Empty));
        HeaderRows.Add(new KeyValueRow("Program headers", $"{h.ProgramHeaderCount} declared, {elf.Segments.Count} readable · offset 0x{h.ProgramHeaderOffset:X} · entry size {h.ProgramHeaderEntrySize}", "Verified", string.Empty));
        HeaderRows.Add(new KeyValueRow("Section headers", elf.HasSectionHeaders ? $"{h.SectionHeaderCount} declared, {elf.Sections.Count} readable · offset 0x{h.SectionHeaderOffset:X} · entry size {h.SectionHeaderEntrySize}" : (h.SectionHeaderCount == 0 ? "none (stripped or absent)" : $"{h.SectionHeaderCount} declared, none readable"), "Verified", string.Empty));
        HeaderRows.Add(new KeyValueRow("ELF version", $"{h.Version} (identification {h.IdentVersion})", "Verified", string.Empty));
        if (elf.Dynamic.Interpreter is { } interpreter)
        {
            HeaderRows.Add(new KeyValueRow("Program interpreter", interpreter, "Verified", string.Empty));
        }

        foreach (var s in elf.Segments)
        {
            Segments.Add(new SegmentRow(s.Index, s.TypeName, s.Offset, s.VirtualAddress, s.FileSize, s.MemorySize, s.Permissions, s.Alignment));
        }

        foreach (var s in elf.Sections)
        {
            Sections.Add(new SectionRow(s.Index, s.Name, s.TypeName, s.Address, s.Offset, s.Size, s.FlagsText, s.Link));
        }

        foreach (var r in elf.MemoryRegions)
        {
            Regions.Add(new RegionRow(r.Name, r.Source, r.VirtualAddress, r.EndAddress, r.Size, r.Permissions));
        }

        var symbols = elf.Symbols.Take(MaxDisplayedSymbols)
            .Select(s => new SymbolRow(s.Name, s.Value, s.Size, s.TypeName, s.BindingName, s.VisibilityName,
                s.SectionIndex switch { 0 => "UND", 0xFFF1 => "ABS", 0xFFF2 => "COMMON", var i => i.ToString() },
                s.Table, s.IsImport ? "Import" : s.IsUndefined ? "Undefined" : "Defined"))
            .ToList();
        var view = new ListCollectionView(symbols);
        view.Filter = o =>
        {
            var row = (SymbolRow)o;
            if (ImportsOnly && row.Kind != "Import")
            {
                return false;
            }

            return SymbolFilter.Length == 0 || row.Name.Contains(SymbolFilter, StringComparison.OrdinalIgnoreCase);
        };
        SymbolsView = view;

        NoSymbols = !elf.HasSymbolTable && !elf.HasDynamicSymbolTable;
        SymbolSummary = NoSymbols
            ? "This file has no symbol tables (stripped). Function names are not available."
            : $"{elf.Symbols.Count:N0} symbols read ({(elf.HasSymbolTable ? $".symtab {elf.SymbolTableEntryCount:N0}" : string.Empty)}{(elf.HasSymbolTable && elf.HasDynamicSymbolTable ? ", " : string.Empty)}{(elf.HasDynamicSymbolTable ? $".dynsym {elf.DynamicSymbolEntryCount:N0}" : string.Empty)})"
              + (elf.Symbols.Count > MaxDisplayedSymbols ? $"; the first {MaxDisplayedSymbols:N0} are shown" : string.Empty)
              + (elf.SymbolsTruncated ? "; a safety limit cut the table short" : string.Empty);
    }
}
