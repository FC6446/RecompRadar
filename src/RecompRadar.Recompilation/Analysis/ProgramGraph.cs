using RecompRadar.Recompilation.IR;

namespace RecompRadar.Recompilation.Analysis;

public enum EdgeKind
{
    /// <summary>Execution runs into the next block (the block was only split because something jumps to the next address).</summary>
    Fallthrough,

    /// <summary>Conditional branch taken.</summary>
    Taken,

    Jump,

    /// <summary>Direct call. The callee is the target; execution is assumed to continue at the matching <see cref="CallReturn"/> edge.</summary>
    Call,

    /// <summary>Where execution continues after a call returns. This is an assumption: it holds when the callee returns normally.</summary>
    CallReturn,

    SyscallContinue,
}

public sealed record CfgEdge(ulong From, ulong To, EdgeKind Kind);

public enum BlockExit
{
    Fallthrough,
    Jump,
    Branch,
    Call,
    IndirectCall,
    Return,
    IndirectJump,
    Syscall,

    /// <summary>A system call that ends the program (for example exit); nothing runs after it.</summary>
    ProgramExit,
    Stop,
}

public sealed class CfgBlock
{
    public required ulong Start { get; init; }

    /// <summary>First address after the block (including a delay slot).</summary>
    public required ulong End { get; init; }

    public required IrBlock Ir { get; init; }

    public required BlockExit Exit { get; init; }

    public required IReadOnlyList<CfgEdge> Successors { get; init; }

    public int InstructionCount => Ir.GuestInstructionCount;

}

public enum FindingSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record AnalysisFinding(FindingSeverity Severity, ulong Address, string Code, string Message);

public sealed record UnsupportedInstruction(ulong Address, string Mnemonic, uint RawWord, string Reason);

public sealed record IndirectJump(ulong Address, bool IsCall, int GuestRegister);

/// <summary>A jump or call through a register whose value is a constant inside its block, so the target is known.</summary>
public sealed record ResolvedIndirectJump(ulong Address, ulong Target, bool IsCall);

/// <summary>Which calling convention a <c>syscall</c> instruction follows, judged from the registers set in its own block.</summary>
public enum SyscallAbi
{
    /// <summary>RecompRadar's test calls: number in $v0 ($v1 is 0 or not set in the block).</summary>
    RecompRadarTest,

    /// <summary>PlayStation 2 EE kernel call: a non-zero constant in $v1.</summary>
    Ps2Kernel,
}

/// <param name="Number">The call number when it can be determined from the code in the same block; otherwise null.</param>
public sealed record SyscallSite(ulong Address, uint? Number, SyscallAbi Abi = SyscallAbi.RecompRadarTest);

public sealed record FunctionInfo(
    ulong Entry,
    string Name,
    string NameSource,
    IReadOnlyList<ulong> BlockStarts,
    IReadOnlyList<ulong> Callees,
    int InstructionCount,
    bool HasIndirectJumps,
    bool HasIndirectCalls,
    bool IsRecursive);

public enum XRefKind
{
    Call,
    Jump,
    Branch,
}

public sealed record CrossReference(ulong From, ulong To, XRefKind Kind);

public sealed record CallGraphEdge(ulong Caller, ulong Callee, ulong CallSite);

/// <summary>The result of statically analyzing the reachable code of a program.</summary>
public sealed class ProgramGraph
{
    public required ulong EntryPoint { get; init; }

    public required IReadOnlyDictionary<ulong, CfgBlock> Blocks { get; init; }

    public required IReadOnlyList<FunctionInfo> Functions { get; init; }

    public required IReadOnlyList<CallGraphEdge> CallGraph { get; init; }

    public required IReadOnlyList<CrossReference> CrossReferences { get; init; }

    public required IReadOnlyList<UnsupportedInstruction> UnsupportedInstructions { get; init; }

    public required IReadOnlyList<IndirectJump> IndirectJumps { get; init; }

    public required IReadOnlyList<ResolvedIndirectJump> ResolvedIndirectJumps { get; init; }

    public required IReadOnlyList<SyscallSite> SyscallSites { get; init; }

    public required IReadOnlyList<AnalysisFinding> Findings { get; init; }

    /// <summary>Bytes of code that are reachable from the entry point and function symbols.</summary>
    public required ulong ReachableBytes { get; init; }

    public required ulong ExecutableBytes { get; init; }

    /// <summary>True when the block or instruction limit stopped the analysis early.</summary>
    public bool Truncated { get; init; }

    public int InstructionCount => Blocks.Values.Sum(b => b.InstructionCount);

    /// <summary>
    /// True when parts of the reachable code could not be translated or the analysis stopped early. Unresolved register jumps do not count:
    /// they only fault at run time when taken.
    /// </summary>
    public bool HasMissingCode =>
        UnsupportedInstructions.Count > 0 || Truncated || Findings.Any(f => f.Severity == FindingSeverity.Error);

    /// <summary>True when every reachable block could be translated and every control transfer has a known target.</summary>
    public bool IsFullyResolved =>
        UnsupportedInstructions.Count == 0 && IndirectJumps.Count == 0 && !Truncated && Findings.All(f => f.Severity != FindingSeverity.Error);
}
