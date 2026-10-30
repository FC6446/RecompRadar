using System.Text;

namespace RecompRadar.Recompilation.IR;

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
}

/// <summary>An operand of the 32-bit integer IR: a virtual register or an immediate. Registers 0-31 mirror the guest registers.</summary>
public readonly record struct IrValue(bool IsImmediate, uint Value)
{
    public static IrValue Reg(int index) => new(false, (uint)index);

    public static IrValue Imm(uint value) => new(true, value);

    public override string ToString() => IsImmediate ? $"0x{Value:X}" : RegisterName((int)Value);

    public static string RegisterName(int index) => index < 32 ? $"r{index}" : $"t{index - 32}";
}

/// <summary>
/// Three-address instruction. For stores, A is the base address, B the value and Offset the displacement.
/// For loads, A is the base address and Offset the displacement.
/// </summary>
public sealed record IrInstruction(IrOp Op, int Dest, IrValue A, IrValue B, int Offset, ulong GuestAddress)
{
    public override string ToString() => Op switch
    {
        IrOp.Const => $"{IrValue.RegisterName(Dest)} = {A}",
        IrOp.Move => $"{IrValue.RegisterName(Dest)} = {A}",
        IrOp.Store8 or IrOp.Store16 or IrOp.Store32 => $"{Op.ToString().ToLowerInvariant()} [{A} + {Offset}] = {B}",
        IrOp.LoadS8 or IrOp.LoadU8 or IrOp.LoadS16 or IrOp.LoadU16 or IrOp.Load32 =>
            $"{IrValue.RegisterName(Dest)} = {Op.ToString().ToLowerInvariant()} [{A} + {Offset}]",
        _ => $"{IrValue.RegisterName(Dest)} = {Op.ToString().ToLowerInvariant()} {A}, {B}",
    };
}

public abstract record IrTerminator;

public sealed record IrJump(ulong Target, bool IsCall) : IrTerminator;

public sealed record IrBranch(int ConditionRegister, ulong Taken, ulong NotTaken) : IrTerminator;

public sealed record IrJumpRegister(int Register) : IrTerminator;

public sealed record IrSyscall(ulong Continuation) : IrTerminator;

/// <summary>The block cannot continue (undecodable or unsupported instruction, unreadable memory).</summary>
public sealed record IrStop(string Reason) : IrTerminator;

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
        IrJumpRegister r => $"jump {IrValue.RegisterName(r.Register)}",
        IrSyscall s => $"syscall, continue at 0x{s.Continuation:X8}",
        IrStop stop => $"stop: {stop.Reason}",
        _ => t.ToString() ?? string.Empty,
    };
}
