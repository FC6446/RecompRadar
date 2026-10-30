using System.Buffers.Binary;
using RecompRadar.Core.Model;
using RecompRadar.Recompilation.IR;

namespace RecompRadar.Recompilation.Mips;

/// <summary>
/// Lifts the MIPS32 integer subset into the 32-bit IR, one basic block at a time.
/// Branch delay slots are folded into the block: the condition (or jump target) is evaluated first, the delay-slot
/// instruction is translated next, and the branch takes effect afterwards, exactly as on the hardware.
/// </summary>
public sealed class MipsTranslator : ICodeTranslator
{
    private const int MaxBlockInstructions = 1024;
    private const int TempBase = 32;

    /// <summary>Writes to $zero are redirected here; this register is never read.</summary>
    public const int ScratchRegister = 63;

    public string Name => "MIPS32 integer subset to IR translator (proof of concept)";

    public IrBlock TranslateBlock(IMemoryReader memory, Endianness endianness, ulong address)
    {
        var block = new IrBlock { StartAddress = address, EndAddress = address };
        var pc = address;
        for (var i = 0; i < MaxBlockInstructions; i++)
        {
            if (!TryFetch(memory, endianness, pc, out var raw))
            {
                block.Terminator = new IrStop($"cannot read an instruction at 0x{pc:X8} (unmapped memory)");
                return block;
            }

            var terminator = TranslateInstruction(new MipsWord(raw), pc, block, memory, endianness, inDelaySlot: false);
            block.GuestInstructionCount++;
            if (terminator is not null)
            {
                block.Terminator = terminator;
                return block;
            }

            pc += 4;
            block.EndAddress = pc;
        }

        block.Terminator = new IrJump(pc, IsCall: false);
        return block;
    }

    private static bool TryFetch(IMemoryReader memory, Endianness endianness, ulong address, out uint raw)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (!memory.TryRead(address, bytes))
        {
            raw = 0;
            return false;
        }

        raw = endianness == Endianness.Little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);
        return true;
    }

    private static IrValue Src(int guest) => guest == 0 ? IrValue.Imm(0) : IrValue.Reg(guest);

    private static int Dest(int guest) => guest == 0 ? ScratchRegister : guest;

    private static void Emit(IrBlock block, ulong pc, IrOp op, int dest, IrValue a, IrValue b, int offset = 0) =>
        block.Instructions.Add(new IrInstruction(op, dest, a, b, offset, pc));

    /// <summary>
    /// Translates one instruction. Returns a terminator when the instruction ends the block, otherwise null.
    /// For control-flow instructions the delay slot is translated here and counted in the block.
    /// </summary>
    private static IrTerminator? TranslateInstruction(MipsWord w, ulong pc, IrBlock block, IMemoryReader memory, Endianness endianness, bool inDelaySlot)
    {
        IrTerminator DelaySlotBranch() => new IrStop($"a branch inside a delay slot at 0x{pc:X8} is not supported");

        IrTerminator? WithDelaySlot(Func<IrTerminator> terminator)
        {
            // The caller has already emitted everything that must be evaluated before the delay slot.
            if (!TryFetch(memory, endianness, pc + 4, out var slotRaw))
            {
                return new IrStop($"cannot read the delay slot at 0x{pc + 4:X8} (unmapped memory)");
            }

            var slot = TranslateInstruction(new MipsWord(slotRaw), pc + 4, block, memory, endianness, inDelaySlot: true);
            block.GuestInstructionCount++;
            block.EndAddress = pc + 8;
            return slot ?? terminator();
        }

        IrTerminator Unsupported() =>
            new IrStop($"unsupported instruction 0x{w.Raw:X8} ({MipsDecoder.DecodeWord(w.Raw, pc).Mnemonic}) at 0x{pc:X8}");

        switch (w.Op)
        {
            case 0:
                switch (w.Funct)
                {
                    case 0x00:
                        if (w.Raw != 0)
                        {
                            Emit(block, pc, IrOp.Shl, Dest(w.Rd), Src(w.Rt), IrValue.Imm((uint)w.Sa));
                        }

                        return null;
                    case 0x02:
                        Emit(block, pc, IrOp.Shr, Dest(w.Rd), Src(w.Rt), IrValue.Imm((uint)w.Sa));
                        return null;
                    case 0x03:
                        Emit(block, pc, IrOp.Sar, Dest(w.Rd), Src(w.Rt), IrValue.Imm((uint)w.Sa));
                        return null;
                    case 0x04:
                        Emit(block, pc, IrOp.Shl, Dest(w.Rd), Src(w.Rt), Src(w.Rs));
                        return null;
                    case 0x06:
                        Emit(block, pc, IrOp.Shr, Dest(w.Rd), Src(w.Rt), Src(w.Rs));
                        return null;
                    case 0x07:
                        Emit(block, pc, IrOp.Sar, Dest(w.Rd), Src(w.Rt), Src(w.Rs));
                        return null;
                    case 0x08:
                    {
                        if (inDelaySlot)
                        {
                            return DelaySlotBranch();
                        }

                        Emit(block, pc, IrOp.Move, TempBase, Src(w.Rs), default);
                        return WithDelaySlot(() => new IrJumpRegister(TempBase));
                    }

                    case 0x09:
                    {
                        if (inDelaySlot)
                        {
                            return DelaySlotBranch();
                        }

                        Emit(block, pc, IrOp.Move, TempBase, Src(w.Rs), default);
                        Emit(block, pc, IrOp.Const, Dest(w.Rd), IrValue.Imm((uint)(pc + 8)), default);
                        return WithDelaySlot(() => new IrJumpRegister(TempBase));
                    }

                    case 0x0C:
                        return inDelaySlot ? new IrStop($"a syscall inside a delay slot at 0x{pc:X8} is not supported") : new IrSyscall(pc + 4);
                    case 0x21:
                        Emit(block, pc, IrOp.Add, Dest(w.Rd), Src(w.Rs), Src(w.Rt));
                        return null;
                    case 0x23:
                        Emit(block, pc, IrOp.Sub, Dest(w.Rd), Src(w.Rs), Src(w.Rt));
                        return null;
                    case 0x24:
                        Emit(block, pc, IrOp.And, Dest(w.Rd), Src(w.Rs), Src(w.Rt));
                        return null;
                    case 0x25:
                        Emit(block, pc, IrOp.Or, Dest(w.Rd), Src(w.Rs), Src(w.Rt));
                        return null;
                    case 0x26:
                        Emit(block, pc, IrOp.Xor, Dest(w.Rd), Src(w.Rs), Src(w.Rt));
                        return null;
                    case 0x27:
                        Emit(block, pc, IrOp.Nor, Dest(w.Rd), Src(w.Rs), Src(w.Rt));
                        return null;
                    case 0x2A:
                        Emit(block, pc, IrOp.SetLtSigned, Dest(w.Rd), Src(w.Rs), Src(w.Rt));
                        return null;
                    case 0x2B:
                        Emit(block, pc, IrOp.SetLtUnsigned, Dest(w.Rd), Src(w.Rs), Src(w.Rt));
                        return null;
                    default:
                        return Unsupported();
                }

            case 0x1C when w.Funct == 0x02:
                Emit(block, pc, IrOp.Mul, Dest(w.Rd), Src(w.Rs), Src(w.Rt));
                return null;

            case 0x01 when w.Rt is 0 or 1:
            {
                if (inDelaySlot)
                {
                    return DelaySlotBranch();
                }

                var taken = w.BranchTarget(pc);
                Emit(block, pc, IrOp.SetLtSigned, TempBase, Src(w.Rs), IrValue.Imm(0));
                if (w.Rt == 1)
                {
                    // bgez: not (rs < 0)
                    Emit(block, pc, IrOp.SetEq, TempBase, IrValue.Reg(TempBase), IrValue.Imm(0));
                }

                return WithDelaySlot(() => new IrBranch(TempBase, taken, pc + 8));
            }

            case 0x02:
            case 0x03:
            {
                if (inDelaySlot)
                {
                    return DelaySlotBranch();
                }

                var target = w.JumpTarget(pc);
                var isCall = w.Op == 0x03;
                if (isCall)
                {
                    Emit(block, pc, IrOp.Const, 31, IrValue.Imm((uint)(pc + 8)), default);
                }

                return WithDelaySlot(() => new IrJump(target, isCall));
            }

            case 0x04:
            case 0x05:
            {
                if (inDelaySlot)
                {
                    return DelaySlotBranch();
                }

                var taken = w.BranchTarget(pc);
                Emit(block, pc, w.Op == 0x04 ? IrOp.SetEq : IrOp.SetNe, TempBase, Src(w.Rs), Src(w.Rt));
                return WithDelaySlot(() => new IrBranch(TempBase, taken, pc + 8));
            }

            case 0x06 when w.Rt == 0:
            case 0x07 when w.Rt == 0:
            {
                if (inDelaySlot)
                {
                    return DelaySlotBranch();
                }

                var taken = w.BranchTarget(pc);
                if (w.Op == 0x07)
                {
                    // bgtz: 0 < rs
                    Emit(block, pc, IrOp.SetLtSigned, TempBase, IrValue.Imm(0), Src(w.Rs));
                }
                else
                {
                    // blez: not (0 < rs)
                    Emit(block, pc, IrOp.SetLtSigned, TempBase, IrValue.Imm(0), Src(w.Rs));
                    Emit(block, pc, IrOp.SetEq, TempBase, IrValue.Reg(TempBase), IrValue.Imm(0));
                }

                return WithDelaySlot(() => new IrBranch(TempBase, taken, pc + 8));
            }

            case 0x09:
                Emit(block, pc, IrOp.Add, Dest(w.Rt), Src(w.Rs), IrValue.Imm((uint)w.SImm));
                return null;
            case 0x0A:
                Emit(block, pc, IrOp.SetLtSigned, Dest(w.Rt), Src(w.Rs), IrValue.Imm((uint)w.SImm));
                return null;
            case 0x0B:
                Emit(block, pc, IrOp.SetLtUnsigned, Dest(w.Rt), Src(w.Rs), IrValue.Imm((uint)w.SImm));
                return null;
            case 0x0C:
                Emit(block, pc, IrOp.And, Dest(w.Rt), Src(w.Rs), IrValue.Imm(w.Imm));
                return null;
            case 0x0D:
                Emit(block, pc, IrOp.Or, Dest(w.Rt), Src(w.Rs), IrValue.Imm(w.Imm));
                return null;
            case 0x0E:
                Emit(block, pc, IrOp.Xor, Dest(w.Rt), Src(w.Rs), IrValue.Imm(w.Imm));
                return null;
            case 0x0F:
                Emit(block, pc, IrOp.Const, Dest(w.Rt), IrValue.Imm(w.Imm << 16), default);
                return null;
            case 0x20:
                Emit(block, pc, IrOp.LoadS8, Dest(w.Rt), Src(w.Rs), default, w.SImm);
                return null;
            case 0x21:
                Emit(block, pc, IrOp.LoadS16, Dest(w.Rt), Src(w.Rs), default, w.SImm);
                return null;
            case 0x23:
                Emit(block, pc, IrOp.Load32, Dest(w.Rt), Src(w.Rs), default, w.SImm);
                return null;
            case 0x24:
                Emit(block, pc, IrOp.LoadU8, Dest(w.Rt), Src(w.Rs), default, w.SImm);
                return null;
            case 0x25:
                Emit(block, pc, IrOp.LoadU16, Dest(w.Rt), Src(w.Rs), default, w.SImm);
                return null;
            case 0x28:
                Emit(block, pc, IrOp.Store8, 0, Src(w.Rs), Src(w.Rt), w.SImm);
                return null;
            case 0x29:
                Emit(block, pc, IrOp.Store16, 0, Src(w.Rs), Src(w.Rt), w.SImm);
                return null;
            case 0x2B:
                Emit(block, pc, IrOp.Store32, 0, Src(w.Rs), Src(w.Rt), w.SImm);
                return null;
            default:
                return Unsupported();
        }
    }
}

