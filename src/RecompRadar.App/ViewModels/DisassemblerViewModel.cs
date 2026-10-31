using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using RecompRadar.App.Services;
using RecompRadar.Recompilation.Analysis;
using RecompRadar.Semantics;

namespace RecompRadar.App.ViewModels;

/// <summary>
/// Shows what the code analysis recovered: functions, basic blocks, disassembly, IR, call graph, cross references and system calls.
/// Everything that is a guess or could not be followed is listed as such.
/// </summary>
public sealed partial class DisassemblerViewModel : PageViewModel
{
    private readonly CodeAnalysisService _code;
    private ProgramGraph? _graph;
    private Dictionary<ulong, string> _functionOfBlock = [];

    public DisassemblerViewModel(CodeAnalysisService code)
        : base("disassembler", "Disassembler", "Control flow: blocks, functions, call graph and cross references recovered from the code", "IconSearch")
    {
        _code = code;
        _functionsView = CollectionViewSource.GetDefaultView(Array.Empty<FunctionRow>());
        code.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CodeAnalysisService.Result) or nameof(CodeAnalysisService.Status) or nameof(CodeAnalysisService.IsRunning))
            {
                Rebuild();
            }
        };
        Rebuild();
    }

    public CodeAnalysisService Code => _code;

    public ObservableCollection<KeyValueRow> Summary { get; } = [];

    public ObservableCollection<string> Uncertainties { get; } = [];

    public ObservableCollection<FunctionRow> Functions { get; } = [];

    public ObservableCollection<BlockRow> Blocks { get; } = [];

    public ObservableCollection<ListingLine> Listing { get; } = [];

    public ObservableCollection<FindingRow> Findings { get; } = [];

    public ObservableCollection<CallRow> Calls { get; } = [];

    public ObservableCollection<XRefRow> CrossReferences { get; } = [];

    public ObservableCollection<SyscallRow> Syscalls { get; } = [];

    public ObservableCollection<ResolvedJumpRow> ResolvedJumps { get; } = [];

    [ObservableProperty]
    private ICollectionView _functionsView;

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private string _functionFilter = string.Empty;

    [ObservableProperty]
    private FunctionRow? _selectedFunction;

    [ObservableProperty]
    private BlockRow? _selectedBlock;

    [ObservableProperty]
    private string _irText = string.Empty;

    [ObservableProperty]
    private string _blockTitle = "Select a function and a block to see its disassembly.";

    partial void OnFunctionFilterChanged(string value) => FunctionsView.Refresh();

    partial void OnSelectedFunctionChanged(FunctionRow? value)
    {
        Blocks.Clear();
        SelectedBlock = null;
        if (value is null || _graph is null || _code.Result is null)
        {
            return;
        }

        var function = _graph.Functions.FirstOrDefault(f => f.Entry == value.Entry);
        if (function is null)
        {
            return;
        }

        foreach (var start in function.BlockStarts)
        {
            var block = _graph.Blocks[start];
            Blocks.Add(new BlockRow(
                start,
                $"0x{block.Start:X8} – 0x{block.End:X8}",
                block.InstructionCount,
                ExitText(block),
                string.Join(", ", block.Successors.Select(s => $"{KindText(s.Kind)} 0x{s.To:X8}"))));
        }

        SelectedBlock = Blocks.FirstOrDefault();
    }

    partial void OnSelectedBlockChanged(BlockRow? value)
    {
        Listing.Clear();
        IrText = string.Empty;
        if (value is null || _graph is null || _code.Result is not { } result)
        {
            BlockTitle = "Select a function and a block to see its disassembly.";
            return;
        }

        var block = _graph.Blocks[value.Start];
        BlockTitle = $"Block 0x{block.Start:X8} – 0x{block.End:X8}: {block.InstructionCount} instruction(s), ends with {ExitText(block).ToLowerInvariant()}";
        var previousHasDelay = false;
        for (var address = block.Start; address < block.End; address += 4)
        {
            var decoded = result.Decoder?.Decode(result.Image, result.Image.Endianness, address);
            if (decoded is null)
            {
                Listing.Add(new ListingLine($"0x{address:X8}", "--------", "(not readable)", string.Empty));
                continue;
            }

            var note = previousHasDelay ? "delay slot" : !decoded.IsKnown ? "not decoded: outside the supported subset" : string.Empty;
            Listing.Add(new ListingLine($"0x{address:X8}", decoded.Raw.ToString("X8", System.Globalization.CultureInfo.InvariantCulture), $"{decoded.Mnemonic} {decoded.Operands}".TrimEnd(), note));
            previousHasDelay = decoded.HasDelaySlot;
        }

        if (block.Ir.IsFallthrough)
        {
            Listing.Add(new ListingLine(string.Empty, string.Empty, $"falls through into 0x{block.End:X8}", "the next address is a jump target, so the block ends here"));
        }

        IrText = block.Ir.Format();
    }

    private void Rebuild()
    {
        Summary.Clear();
        Uncertainties.Clear();
        Functions.Clear();
        Blocks.Clear();
        Listing.Clear();
        Findings.Clear();
        Calls.Clear();
        CrossReferences.Clear();
        Syscalls.Clear();
        ResolvedJumps.Clear();
        SelectedFunction = null;
        SelectedBlock = null;
        IrText = string.Empty;
        _graph = null;
        _functionOfBlock = [];

        if (_code.Result is not { } result)
        {
            HasResult = false;
            return;
        }

        var graph = result.Graph;
        _graph = graph;
        HasResult = true;

        // The lowest function entry owns a block that several functions share.
        foreach (var function in graph.Functions.OrderByDescending(f => f.Entry))
        {
            foreach (var start in function.BlockStarts)
            {
                _functionOfBlock[start] = function.Name;
            }
        }

        var coverage = graph.ExecutableBytes == 0 ? 0 : 100.0 * graph.ReachableBytes / graph.ExecutableBytes;
        Summary.Add(new KeyValueRow("Decoder / translator", result.Plugin.DisplayName, "Verified", "The plugin that supports this architecture."));
        Summary.Add(new KeyValueRow("Entry point", $"0x{graph.EntryPoint:X8}", "Verified", "Read from the ELF header."));
        Summary.Add(new KeyValueRow("Functions", graph.Functions.Count.ToString("N0"), "Inferred", "Starts come from the entry point, symbols and call targets; the end of a function is where its control flow stops."));
        Summary.Add(new KeyValueRow("Basic blocks", graph.Blocks.Count.ToString("N0"), "Verified", "Straight-line code between jump targets and branches."));
        Summary.Add(new KeyValueRow("Instructions", graph.InstructionCount.ToString("N0"), "Verified", "Decoded from the file's bytes, delay slots included."));
        Summary.Add(new KeyValueRow("Code reached", $"{graph.ReachableBytes:N0} of {graph.ExecutableBytes:N0} bytes ({coverage:0.#} %)", "Inferred", "The rest is data, padding or code that direct control flow never reaches."));
        Summary.Add(new KeyValueRow("Register jumps resolved", graph.ResolvedIndirectJumps.Count.ToString("N0"), "Inferred", "Target is a constant inside the block (for example lui/ori then jr)."));
        Summary.Add(new KeyValueRow("Register jumps unresolved", graph.IndirectJumps.Count.ToString("N0"), "Verified", "Targets are not known statically; jump tables are not analyzed."));
        Summary.Add(new KeyValueRow("Unsupported instructions", graph.UnsupportedInstructions.Count.ToString("N0"), "Verified", "Instruction words the translator does not handle."));

        Uncertainties.Add("Function boundaries are inferred. Code that is only reached through register jumps or function pointers stored in memory is not found.");
        if (graph.IndirectJumps.Count > 0)
        {
            Uncertainties.Add($"{graph.IndirectJumps.Count} jump/call site(s) go through a register and could not be resolved (listed under Findings). The control-flow graph is incomplete behind them.");
        }

        if (graph.UnsupportedInstructions.Count > 0)
        {
            var first = graph.UnsupportedInstructions[0];
            Uncertainties.Add($"{graph.UnsupportedInstructions.Count} instruction site(s) cannot be translated (first: {first.Mnemonic} at 0x{first.Address:X8}). Control flow stops there in this analysis.");
        }

        if (graph.Truncated)
        {
            Uncertainties.Add("The analysis limit was reached; the result is incomplete.");
        }

        Uncertainties.Add("Calls are assumed to return to the instruction after their delay slot. Code that changes its return address is not detected.");

        foreach (var f in graph.Functions)
        {
            var flags = new List<string>();
            if (f.IsRecursive)
            {
                flags.Add("recursive");
            }

            if (f.HasIndirectJumps)
            {
                flags.Add("register jumps");
            }

            if (f.HasIndirectCalls)
            {
                flags.Add("register calls");
            }

            Functions.Add(new FunctionRow(f.Entry, f.Name, f.NameSource, f.BlockStarts.Count, f.InstructionCount, f.Callees.Count, string.Join(", ", flags)));
        }

        FunctionsView = CollectionViewSource.GetDefaultView(Functions);
        FunctionsView.Filter = o => o is FunctionRow row &&
            (string.IsNullOrWhiteSpace(FunctionFilter) ||
             row.Name.Contains(FunctionFilter, StringComparison.OrdinalIgnoreCase) ||
             row.Address.Contains(FunctionFilter, StringComparison.OrdinalIgnoreCase));

        foreach (var finding in graph.Findings)
        {
            Findings.Add(new FindingRow(finding.Severity.ToString(), finding.Address == 0 ? string.Empty : $"0x{finding.Address:X8}", finding.Code, finding.Message));
        }

        var nameOf = graph.Functions.ToDictionary(f => f.Entry, f => f.Name);
        foreach (var edge in graph.CallGraph.Distinct())
        {
            Calls.Add(new CallRow(nameOf.GetValueOrDefault(edge.Caller, $"0x{edge.Caller:X8}"), nameOf.GetValueOrDefault(edge.Callee, $"0x{edge.Callee:X8}"), $"0x{edge.CallSite:X8}"));
        }

        foreach (var x in graph.CrossReferences)
        {
            CrossReferences.Add(new XRefRow($"0x{x.From:X8}", FunctionAt(graph, x.From), $"0x{x.To:X8}", FunctionAt(graph, x.To), x.Kind.ToString().ToLowerInvariant()));
        }

        foreach (var s in graph.SyscallSites)
        {
            Syscalls.Add(new SyscallRow($"0x{s.Address:X8}", s.Number is { } n ? (s.Abi == SyscallAbi.Ps2Kernel ? $"0x{n:X}" : n.ToString()) : "unknown", SyscallMeaning(s)));
        }

        foreach (var j in graph.ResolvedIndirectJumps)
        {
            ResolvedJumps.Add(new ResolvedJumpRow($"0x{j.Address:X8}", $"0x{j.Target:X8}", j.IsCall ? "call" : "jump"));
        }

        SelectedFunction = Functions.FirstOrDefault();
    }

    private string FunctionAt(ProgramGraph graph, ulong address)
    {
        foreach (var block in graph.Blocks.Values)
        {
            if (address >= block.Start && address < block.End)
            {
                return _functionOfBlock.GetValueOrDefault(block.Start, string.Empty);
            }
        }

        return string.Empty;
    }

    private static string SyscallMeaning(SyscallSite site)
    {
        if (site.Abi == SyscallAbi.Ps2Kernel)
        {
            var number = unchecked((int)site.Number!.Value);
            var name = Ps2Kernel.NameOf(number);
            return name is null
                ? "PlayStation 2 kernel call with an unknown number (not implemented)"
                : Ps2Kernel.IsImplemented(number)
                    ? $"PlayStation 2 kernel call {name} (implemented as a subset)"
                    : $"PlayStation 2 kernel call {name} (not implemented)";
        }

        return site.Number switch
        {
            PocSystemCalls.Exit => "RecompRadar test call: exit",
            PocSystemCalls.WriteByte => "RecompRadar test call: write byte",
            PocSystemCalls.WriteInt => "RecompRadar test call: write integer",
            PocSystemCalls.WriteFloat => "RecompRadar test call: write float",
            PocSystemCalls.WriteString => "RecompRadar test call: write string",
            null => "number is not a constant in this block",
            _ => "not implemented by the RecompRadar runtime (test calls are 1 to 5)",
        };
    }

    private static string ExitText(CfgBlock block) => block.Exit switch
    {
        BlockExit.Fallthrough => "Fall-through",
        BlockExit.Jump => "Jump",
        BlockExit.Branch => "Conditional branch",
        BlockExit.Call => "Call",
        BlockExit.IndirectCall => "Call through register (unresolved)",
        BlockExit.Return => "Return",
        BlockExit.IndirectJump => "Jump through register (unresolved)",
        BlockExit.Syscall => "System call",
        BlockExit.ProgramExit => "Program exit",
        _ => "Stop (cannot continue)",
    };

    private static string KindText(EdgeKind kind) => kind switch
    {
        EdgeKind.Fallthrough => "next",
        EdgeKind.Taken => "taken",
        EdgeKind.Jump => "jump",
        EdgeKind.Call => "call",
        EdgeKind.CallReturn => "after call",
        _ => "continue",
    };
}
