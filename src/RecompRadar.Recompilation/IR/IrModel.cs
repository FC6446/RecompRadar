using System.Text;

namespace RecompRadar.Recompilation.IR;

/// <summary>
/// Layout of the virtual register file shared by translators, the reference interpreter and the code generators.
/// Registers 0-31 mirror the 32 general-purpose guest registers; floating-point registers hold raw 32-bit IEEE-754 bits.
/// </summary>
public static class Regs
{
    public const int Hi = 32;
    public const int Lo = 33;

    /// <summary>The FPU condition flag (set by c.* compares, read by bc1t/bc1f).</summary>
    public const int Fcc = 34;

    public const int FprBase = 64;
    public const int TempBase = 100;
    public const int TempCount = 27;

    /// <summary>Writes to $zero are redirected here; this register is never read.</summary>
    public const int Scratch = 127;

    public const int Count = 128;

    public static int Fpr(int index) => FprBase + index;

    public static string Name(int index) => index switch
    {
        < 32 => $"r{index}",
        Hi => "hi",
        Lo => "lo",
        Fcc => "fcc",
        >= FprBase and < FprBase + 32 => $"f{index - FprBase}",
        >= TempBase and < TempBase + TempCount => $"t{index - TempBase}",
        Scratch => "scratch",
        _ => $"x{index}",
    };
}

public enum IrOp
{
    Const,
    Move,
    Add,
    Sub,
    Mul,
    And,
    Or,
    Xor,
    Nor,
    Shl,
    Shr,
    Sar,
    SetEq,
    SetNe,
    SetLtSigned,
    SetLtUnsigned,
    LoadS8,
    LoadU8,
    LoadS16,
    LoadU16,
    Load32,
    Store8,
    Store16,
    Store32,

    // multiply / divide
    MulHiS,
    MulHiU,
    DivS,
    RemS,
    DivU,
    RemU,

    /// <summary>Dest = A when B == 0.</summary>
    MoveIfZero,

    /// <summary>Dest = A when B != 0.</summary>
    MoveIfNonZero,

    // single-precision floating point (operands are raw bits)
    FAdd,
    FSub,
    FMul,
    FDiv,
    FSqrt,
    FAbs,
    FNeg,
    CvtSW,
    CvtWS,
    TruncWS,
    FEq,
    FLt,
    FLe,
}

/// <summary>An operand of the 32-bit IR: a virtual register or an immediate.</summary>
public readonly record struct IrValue(bool IsImmediate, uint Value)
{
    public static IrValue Reg(int index) => new(false, (uint)index);

    public static IrValue Imm(uint value) => new(true, value);

    public override string ToString() => IsImmediate ? $"0x{Value:X}" : RegisterName((int)Value);

    public static string RegisterName(int index) => Regs.Name(index);
}

/// <summary>
/// Three-address instruction. For stores, A is the base address, B the value and Offset the displacement.
/// For loads, A is the base address and Offset the displacement. Unary operations ignore B.
/// </summary>
public sealed record IrInstruction(IrOp Op, int Dest, IrValue A, IrValue B, int Offset, ulong GuestAddress)
{
    public static bool IsUnary(IrOp op) =>
        op is IrOp.Const or IrOp.Move or IrOp.FSqrt or IrOp.FAbs or IrOp.FNeg or IrOp.CvtSW or IrOp.CvtWS or IrOp.TruncWS;

    public static bool IsLoad(IrOp op) => op is IrOp.LoadS8 or IrOp.LoadU8 or IrOp.LoadS16 or IrOp.LoadU16 or IrOp.Load32;

    public static bool IsStore(IrOp op) => op is IrOp.Store8 or IrOp.Store16 or IrOp.Store32;

    public override string ToString() => Op switch
    {
        _ when IsStore(Op) => $"{Op.ToString().ToLowerInvariant()} [{A} + {Offset}] = {B}",
        _ when IsLoad(Op) => $"{IrValue.RegisterName(Dest)} = {Op.ToString().ToLowerInvariant()} [{A} + {Offset}]",
        _ when IsUnary(Op) && Op is IrOp.Const or IrOp.Move => $"{IrValue.RegisterName(Dest)} = {A}",
        _ when IsUnary(Op) => $"{IrValue.RegisterName(Dest)} = {Op.ToString().ToLowerInvariant()} {A}",
        _ => $"{IrValue.RegisterName(Dest)} = {Op.ToString().ToLowerInvariant()} {A}, {B}",
    };
}

public abstract record IrTerminator;

public sealed record IrJump(ulong Target, bool IsCall) : IrTerminator;

public sealed record IrBranch(int ConditionRegister, ulong Taken, ulong NotTaken) : IrTerminator;

/// <param name="Register">The IR register holding the target (a block-local copy of the guest register).</param>
/// <param name="GuestRegister">The guest register the target came from, or -1 when unknown ($ra = 31 marks a function return).</param>
/// <param name="IsCall">True for jump-and-link-register (a call through a pointer).</param>
/// <param name="ReturnAddress">For calls: the address execution continues at after the callee returns.</param>
public sealed record IrJumpRegister(int Register, int GuestRegister = -1, bool IsCall = false, ulong ReturnAddress = 0) : IrTerminator;

public sealed record IrSyscall(ulong Continuation) : IrTerminator;

/// <summary>The block cannot continue (undecodable or unsupported instruction, unreadable memory).</summary>
public sealed record IrStop(string Reason, string? Mnemonic = null, ulong Address = 0, uint RawWord = 0) : IrTerminator;

public sealed class IrBlock
{
    public ulong StartAddress { get; init; }

    public ulong EndAddress { get; set; }

    public int GuestInstructionCount { get; set; }

    public List<IrInstruction> Instructions { get; } = [];

    public IrTerminator Terminator { get; set; } = new IrStop("block not terminated");

    public string Format()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"block 0x{StartAddress:X8}..0x{EndAddress:X8} ({GuestInstructionCount} guest instructions)");
        foreach (var instruction in Instructions)
        {
            sb.AppendLine($"  {instruction}    ; guest 0x{instruction.GuestAddress:X8}");
        }

        sb.AppendLine($"  {FormatTerminator(Terminator)}");
        return sb.ToString();
    }

    private static string FormatTerminator(IrTerminator t) => t switch
    {
        IrJump j => $"{(j.IsCall ? "call" : "jump")} 0x{j.Target:X8}",
        IrBranch b => $"branch {IrValue.RegisterName(b.ConditionRegister)} ? 0x{b.Taken:X8} : 0x{b.NotTaken:X8}",
        IrJumpRegister r => $"{(r.IsCall ? "call" : "jump")} {IrValue.RegisterName(r.Register)}{(r.GuestRegister == 31 ? " (return)" : string.Empty)}",
        IrSyscall s => $"syscall, continue at 0x{s.Continuation:X8}",
        IrStop stop => $"stop: {stop.Reason}",
        _ => t.ToString() ?? string.Empty,
    };
}
