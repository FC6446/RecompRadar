using CommunityToolkit.Mvvm.ComponentModel;

namespace RecompRadar.App.ViewModels;

public sealed record FunctionRow(ulong Entry, string Name, string Source, int Blocks, int Instructions, int Calls, string Flags)
{
    public string Address => $"0x{Entry:X8}";
}

public sealed record BlockRow(ulong Start, string Range, int Instructions, string Exit, string Successors);

public sealed record ListingLine(string Address, string Raw, string Text, string Note);

public sealed record FindingRow(string Severity, string Address, string Code, string Message);

public sealed record CallRow(string Caller, string Callee, string CallSite);

public sealed record XRefRow(string From, string FromFunction, string To, string ToFunction, string Kind);

public sealed record SyscallRow(string Address, string Number, string Meaning);

public sealed record ResolvedJumpRow(string Address, string Target, string Kind);

public sealed record NoteRow(string Kind, string Text);

public sealed record ModuleRow(string Name, string State, string Note);

public sealed record PlatformChoice(string? Id, string Label);

public sealed record BackendCardRow(string Id, string Title, string Architecture, string Milestone, string Summary, bool HasPipeline, string Counts, IReadOnlyList<ModuleRow> Modules);

public sealed partial class StageRow : ObservableObject
{
    public StageRow(string name)
    {
        Name = name;
        _status = "waiting";
    }

    public string Name { get; }

    [ObservableProperty]
    private string _status;

    [ObservableProperty]
    private string _summary = string.Empty;
}
