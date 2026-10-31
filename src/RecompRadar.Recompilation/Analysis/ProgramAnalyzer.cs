using RecompRadar.Core.Model;
using RecompRadar.Recompilation.IR;
using RecompRadar.Recompilation.Mips;

namespace RecompRadar.Recompilation.Analysis;

public sealed record AnalysisInput(
    IMemoryReader Memory,
    Endianness Endianness,
    ICodeTranslator Translator,
    ulong EntryPoint,
    IReadOnlyList<(ulong Start, ulong End)> ExecutableRanges,
    IReadOnlyDictionary<ulong, string> FunctionSymbols,
    IReadOnlySet<uint>? NoReturnSystemCalls = null)
{
    /// <summary>True when the call is known to end the program or the calling thread, so the code behind the syscall is not its continuation.</summary>
    public bool IsNoReturn(SyscallAbi abi, uint? number) => number is { } n && (abi == SyscallAbi.Ps2Kernel
        ? RecompRadar.Semantics.Ps2Kernel.NeverReturns(unchecked((int)n))
        : NoReturnSystemCalls?.Contains(n) == true);
}

public sealed record ProgramAnalyzerOptions(int MaxBlocks = 100_000, int MaxInstructions = 2_000_000);

/// <summary>
/// Recursive-descent code discovery: starting at the entry point and at function symbols it translates basic blocks,
/// follows every direct control transfer, splits blocks at jump targets and derives functions, a call graph and cross references.
/// Whatever it cannot follow (register jumps, jump tables, unsupported instructions) is reported, never guessed.
/// </summary>
public static class ProgramAnalyzer
{
    public static ProgramGraph Analyze(AnalysisInput input, ProgramAnalyzerOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        options ??= new ProgramAnalyzerOptions();

        var findings = new List<AnalysisFinding>();
        var leaders = new HashSet<ulong>();
        var blocks = new SortedList<ulong, IrBlock>();
        var queue = new Queue<ulong>();
        var resolved = new Dictionary<ulong, ulong>();
        var truncated = false;
        var instructionBudget = 0;

        bool InExecutable(ulong address) => input.ExecutableRanges.Any(r => address >= r.Start && address < r.End);

        void AddLeader(ulong address, ulong from)
        {
            if (!InExecutable(address))
            {
                findings.Add(new AnalysisFinding(FindingSeverity.Warning, from, "TARGET_NOT_EXECUTABLE",
                    $"The control transfer at 0x{from:X8} goes to 0x{address:X8}, which is not inside executable memory. It is not translated."));
                return;
            }

            if (!leaders.Add(address))
            {
                return;
            }

            // A new leader inside an existing block splits it: the block is translated again, now ending in front of the leader.
            if (FindContaining(blocks, address) is { } containing && containing.StartAddress != address)
            {
                blocks.Remove(containing.StartAddress);
                queue.Enqueue(containing.StartAddress);
            }

            queue.Enqueue(address);
        }

        AddLeader(input.EntryPoint, input.EntryPoint);
        foreach (var (address, _) in input.FunctionSymbols)
        {
            if (InExecutable(address))
            {
                AddLeader(address, address);
            }
        }

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var address = queue.Dequeue();
            if (blocks.ContainsKey(address))
            {
                continue;
            }

            if (blocks.Count >= options.MaxBlocks || instructionBudget >= options.MaxInstructions)
            {
                truncated = true;
                findings.Add(new AnalysisFinding(FindingSeverity.Warning, address, "LIMIT_REACHED",
                    $"The analysis limit ({options.MaxBlocks:N0} blocks / {options.MaxInstructions:N0} instructions) was reached; the result is incomplete."));
                break;
            }

            var block = input.Translator.TranslateBlock(input.Memory, input.Endianness, address, leaders);
            blocks[address] = block;
            instructionBudget += block.GuestInstructionCount;

            switch (block.Terminator)
            {
                case IrJump j:
                    AddLeader(j.Target, block.TerminatorAddress);
                    if (j.IsCall)
                    {
                        AddLeader(block.EndAddress, block.TerminatorAddress);
                    }

                    break;
                case IrBranch b:
                    AddLeader(b.Taken, block.TerminatorAddress);
                    AddLeader(b.NotTaken, block.TerminatorAddress);
                    break;
                case IrJumpRegister r:
                    if (r.IsCall)
                    {
                        AddLeader(r.ReturnAddress, block.TerminatorAddress);
                    }

                    // A register jump whose value is a constant inside the block (for example lui/ori then jr) is a direct jump.
                    if ((r.IsCall || r.GuestRegister != 31) && BlockConstants.AtEnd(block).TryGetValue(r.Register, out var target))
                    {
                        resolved[block.StartAddress] = target;
                        AddLeader(target, block.TerminatorAddress);
                    }

                    break;
                case IrSyscall s when !IsNoReturnSite(input, block):
                    AddLeader(s.Continuation, block.TerminatorAddress);
                    break;
            }
        }

        return BuildGraph(input, blocks, resolved, findings, truncated);
    }

    private static IrBlock? FindContaining(SortedList<ulong, IrBlock> blocks, ulong address)
    {
        var keys = blocks.Keys;
        int lo = 0, hi = keys.Count - 1, best = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (keys[mid] <= address)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (best < 0)
        {
            return null;
        }

        var block = blocks.Values[best];
        return address < block.EndAddress ? block : null;
    }

    private static ProgramGraph BuildGraph(AnalysisInput input, SortedList<ulong, IrBlock> irBlocks, Dictionary<ulong, ulong> resolved, List<AnalysisFinding> findings, bool truncated)
    {
        var blocks = new Dictionary<ulong, CfgBlock>();
        var unsupported = new List<UnsupportedInstruction>();
        var indirect = new List<IndirectJump>();
        var resolvedJumps = new List<ResolvedIndirectJump>();
        var syscalls = new List<SyscallSite>();
        var xrefs = new List<CrossReference>();

        foreach (var (start, ir) in irBlocks)
        {
            var edges = new List<CfgEdge>();
            BlockExit exit;
            var at = ir.TerminatorAddress;
            switch (ir.Terminator)
            {
                case IrJump j when ir.IsFallthrough:
                    exit = BlockExit.Fallthrough;
                    edges.Add(new CfgEdge(start, j.Target, EdgeKind.Fallthrough));
                    break;
                case IrJump { IsCall: false } j:
                    exit = BlockExit.Jump;
                    edges.Add(new CfgEdge(start, j.Target, EdgeKind.Jump));
                    xrefs.Add(new CrossReference(at, j.Target, XRefKind.Jump));
                    break;
                case IrJump call:
                    exit = BlockExit.Call;
                    edges.Add(new CfgEdge(start, call.Target, EdgeKind.Call));
                    edges.Add(new CfgEdge(start, ir.EndAddress, EdgeKind.CallReturn));
                    xrefs.Add(new CrossReference(at, call.Target, XRefKind.Call));
                    break;
                case IrBranch b:
                    exit = BlockExit.Branch;
                    edges.Add(new CfgEdge(start, b.Taken, EdgeKind.Taken));
                    edges.Add(new CfgEdge(start, b.NotTaken, EdgeKind.Fallthrough));
                    xrefs.Add(new CrossReference(at, b.Taken, XRefKind.Branch));
                    break;
                case IrJumpRegister { IsCall: true } r when resolved.TryGetValue(start, out var callee):
                    exit = BlockExit.Call;
                    edges.Add(new CfgEdge(start, callee, EdgeKind.Call));
                    edges.Add(new CfgEdge(start, r.ReturnAddress, EdgeKind.CallReturn));
                    xrefs.Add(new CrossReference(at, callee, XRefKind.Call));
                    resolvedJumps.Add(new ResolvedIndirectJump(at, callee, true));
                    break;
                case IrJumpRegister { IsCall: false, GuestRegister: not 31 } when resolved.TryGetValue(start, out var destination):
                    exit = BlockExit.Jump;
                    edges.Add(new CfgEdge(start, destination, EdgeKind.Jump));
                    xrefs.Add(new CrossReference(at, destination, XRefKind.Jump));
                    resolvedJumps.Add(new ResolvedIndirectJump(at, destination, false));
                    break;
                case IrJumpRegister { IsCall: true } r:
                    exit = BlockExit.IndirectCall;
                    edges.Add(new CfgEdge(start, r.ReturnAddress, EdgeKind.CallReturn));
                    indirect.Add(new IndirectJump(at, true, r.GuestRegister));
                    findings.Add(new AnalysisFinding(FindingSeverity.Warning, at, "INDIRECT_CALL",
                        $"Call through register {RegisterName(r.GuestRegister)} at 0x{at:X8}: the callee is not known statically, so it is not translated."));
                    break;
                case IrJumpRegister { GuestRegister: 31 }:
                    exit = BlockExit.Return;
                    break;
                case IrJumpRegister r:
                    exit = BlockExit.IndirectJump;
                    indirect.Add(new IndirectJump(at, false, r.GuestRegister));
                    findings.Add(new AnalysisFinding(FindingSeverity.Warning, at, "INDIRECT_JUMP",
                        $"Jump through register {RegisterName(r.GuestRegister)} at 0x{at:X8}: the possible targets are not known (jump-table analysis is not implemented)."));
                    break;
                case IrSyscall s:
                {
                    var (abi, number) = ClassifySyscall(ir);
                    syscalls.Add(new SyscallSite(at, number, abi));
                    if (input.IsNoReturn(abi, number))
                    {
                        exit = BlockExit.ProgramExit;
                    }
                    else
                    {
                        exit = BlockExit.Syscall;
                        edges.Add(new CfgEdge(start, s.Continuation, EdgeKind.SyscallContinue));
                    }

                    break;
                }
                case IrStop stop:
                    exit = BlockExit.Stop;
                    if (stop.Mnemonic is not null)
                    {
                        unsupported.Add(new UnsupportedInstruction(stop.Address, stop.Mnemonic, stop.RawWord, stop.Reason));
                        findings.Add(new AnalysisFinding(FindingSeverity.Error, stop.Address, "UNSUPPORTED_INSTRUCTION", stop.Reason));
                    }
                    else
                    {
                        findings.Add(new AnalysisFinding(FindingSeverity.Error, ir.EndAddress, "CODE_STOPS", stop.Reason));
                    }

                    break;
                default:
                    exit = BlockExit.Stop;
                    break;
            }

            blocks[start] = new CfgBlock { Start = start, End = ir.EndAddress, Ir = ir, Exit = exit, Successors = edges };
        }

        // Edges to addresses that have no block (outside executable memory) are dropped; the transfer was already reported.
        foreach (var block in blocks.Values.ToList())
        {
            var valid = block.Successors.Where(e => blocks.ContainsKey(e.To)).ToList();
            if (valid.Count != block.Successors.Count)
            {
                blocks[block.Start] = new CfgBlock { Start = block.Start, End = block.End, Ir = block.Ir, Exit = block.Exit, Successors = valid };
            }
        }

        var functions = DiscoverFunctions(input, blocks, out var callGraph);
        var reachable = blocks.Values.Aggregate(0UL, (sum, b) => sum + (b.End - b.Start));
        var executable = input.ExecutableRanges.Aggregate(0UL, (sum, r) => sum + (r.End - r.Start));

        findings.Insert(0, new AnalysisFinding(FindingSeverity.Info, input.EntryPoint, "ASSUMED_RETURNS",
            "Calls are assumed to return to the instruction after the delay slot. Code that changes its return address or never returns is not detected."));

        return new ProgramGraph
        {
            EntryPoint = input.EntryPoint,
            Blocks = blocks,
            Functions = functions,
            CallGraph = callGraph,
            CrossReferences = xrefs.OrderBy(x => x.To).ThenBy(x => x.From).ToList(),
            UnsupportedInstructions = unsupported.OrderBy(u => u.Address).ToList(),
            IndirectJumps = indirect.OrderBy(i => i.Address).ToList(),
            ResolvedIndirectJumps = resolvedJumps.OrderBy(i => i.Address).ToList(),
            SyscallSites = syscalls.OrderBy(s => s.Address).ToList(),
            Findings = findings,
            ReachableBytes = reachable,
            ExecutableBytes = executable,
            Truncated = truncated,
        };
    }

    private static string RegisterName(int guest) => guest is >= 0 and < 32 ? MipsRegisters.Name(guest) : "(unknown)";

    /// <summary>
    /// Decides which convention a syscall follows and which call number it carries, from constants set in the same block:
    /// a non-zero constant in $v1 is a PlayStation 2 kernel call; otherwise the number is $v0 (RecompRadar test call).
    /// </summary>
    private static (SyscallAbi Abi, uint? Number) ClassifySyscall(IrBlock block)
    {
        var constants = BlockConstants.AtEnd(block);
        if (constants.TryGetValue(3, out var v1) && v1 != 0)
        {
            return (SyscallAbi.Ps2Kernel, v1);
        }

        return (SyscallAbi.RecompRadarTest, constants.TryGetValue(2, out var v0) ? v0 : null);
    }

    private static bool IsNoReturnSite(AnalysisInput input, IrBlock block)
    {
        var (abi, number) = ClassifySyscall(block);
        return input.IsNoReturn(abi, number);
    }

    private static List<FunctionInfo> DiscoverFunctions(AnalysisInput input, Dictionary<ulong, CfgBlock> blocks, out List<CallGraphEdge> callGraph)
    {
        var entries = new SortedDictionary<ulong, string>();
        void AddEntry(ulong address, string source)
        {
            if (blocks.ContainsKey(address) && !entries.ContainsKey(address))
            {
                entries[address] = source;
            }
        }

        AddEntry(input.EntryPoint, "entry point");
        foreach (var (address, _) in input.FunctionSymbols)
        {
            AddEntry(address, "symbol");
        }

        foreach (var block in blocks.Values)
        {
            foreach (var edge in block.Successors.Where(e => e.Kind == EdgeKind.Call))
            {
                AddEntry(edge.To, "call target");
            }
        }

        var names = new Dictionary<ulong, (string Name, string Source)>();
        foreach (var (address, source) in entries)
        {
            names[address] = input.FunctionSymbols.TryGetValue(address, out var symbol)
                ? (symbol, "symbol")
                : (address == input.EntryPoint ? "_entry" : $"sub_{address:X8}", source);
        }

        var membership = new Dictionary<ulong, List<ulong>>();
        var callees = new Dictionary<ulong, SortedSet<ulong>>();
        callGraph = [];
        foreach (var entry in entries.Keys)
        {
            var seen = new HashSet<ulong>();
            var stack = new Stack<ulong>();
            stack.Push(entry);
            callees[entry] = [];
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!seen.Add(current) || !blocks.TryGetValue(current, out var block))
                {
                    continue;
                }

                foreach (var edge in block.Successors)
                {
                    if (edge.Kind == EdgeKind.Call)
                    {
                        callees[entry].Add(edge.To);
                        callGraph.Add(new CallGraphEdge(entry, edge.To, block.Ir.TerminatorAddress));
                    }
                    else
                    {
                        stack.Push(edge.To);
                    }
                }
            }

            membership[entry] = seen.Where(blocks.ContainsKey).OrderBy(a => a).ToList();
        }

        bool Reaches(ulong from, ulong target)
        {
            var visited = new HashSet<ulong>();
            var stack = new Stack<ulong>(callees[from]);
            while (stack.Count > 0)
            {
                var next = stack.Pop();
                if (next == target)
                {
                    return true;
                }

                if (visited.Add(next) && callees.TryGetValue(next, out var more))
                {
                    foreach (var m in more)
                    {
                        stack.Push(m);
                    }
                }
            }

            return false;
        }

        var result = new List<FunctionInfo>();
        foreach (var (entry, _) in entries)
        {
            var memberBlocks = membership[entry].Select(a => blocks[a]).ToList();
            result.Add(new FunctionInfo(
                entry,
                names[entry].Name,
                names[entry].Source,
                membership[entry],
                callees[entry].ToList(),
                memberBlocks.Sum(b => b.InstructionCount),
                memberBlocks.Any(b => b.Exit == BlockExit.IndirectJump),
                memberBlocks.Any(b => b.Exit == BlockExit.IndirectCall),
                Reaches(entry, entry)));
        }

        return result;
    }
}
