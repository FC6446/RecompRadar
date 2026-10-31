using System.Buffers.Binary;
using RecompRadar.Core.Model;
using RecompRadar.Recompilation.IR;

namespace RecompRadar.Recompilation.Mips;

/// <summary>
/// Lifts the supported MIPS subset (MIPS32 integer, the 64-bit integer instructions of MIPS III/R5900, HI/LO and the
/// single-precision FPU) into the 64-bit IR, one basic block at a time.
/// 32-bit instructions use the "W" IR operations, which sign-extend their result to 64 bits like MIPS64 does; the 64-bit
/// instructions use the plain operations.
/// Branch delay slots are folded into the block: the condition (or jump target) is evaluated first, the delay-slot
/// instruction is translated next, and the branch takes effect afterwards, exactly as on the hardware.
/// </summary>
public sealed class MipsTranslator : ICodeTranslator
{
    private const int MaxBlockInstructions = 1024;
    private static readonly IReadOnlySet<ulong> NoLeaders = new HashSet<ulong>();

    // Temporaries live in the shared register layout; a block uses at most the first two.
    private const int Temp0 = Regs.TempBase;
    private const int Temp1 = Regs.TempBase + 1;

    /// <summary>Writes to $zero are redirected here; this register is never read.</summary>
    public const int ScratchRegister = Regs.Scratch;

    public string Name => "MIPS32/R5900 integer and FPU subset to IR translator";

    public IrBlock TranslateBlock(IMemoryReader memory, Endianness endianness, ulong address) =>
        TranslateBlock(memory, endianness, address, NoLeaders);

    public IrBlock TranslateBlock(IMemoryReader memory, Endianness endianness, ulong address, IReadOnlySet<ulong> leaders)
    {
        var block = new IrBlock { StartAddress = address, EndAddress = address };
        var pc = address;
        for (var i = 0; i < MaxBlockInstructions; i++)
        {
            // A branch target further down ends this block here so that blocks never overlap.
            if (i > 0 && leaders.Contains(pc))
            {
                block.Terminator = new IrJump(pc, IsCall: false);
                block.IsFallthrough = true;
                return block;
            }

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
                block.TerminatorAddress = pc;
                if (terminator is IrSyscall)
                {
                    block.EndAddress = pc + 4;
                }

                return block;
            }

            pc += 4;
            block.EndAddress = pc;
        }

        block.Terminator = new IrJump(pc, IsCall: false);
        block.IsFallthrough = true;
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

    private static IrValue Fpr(int index) => IrValue.Reg(Regs.Fpr(index));

    /// <summary>A sign-extended 16-bit immediate as a 64-bit constant.</summary>
    private static IrValue SImm(MipsWord w) => IrValue.Imm(unchecked((ulong)(long)w.SImm));

    private static void Emit(IrBlock block, ulong pc, IrOp op, int dest, IrValue a, IrValue b, int offset = 0) =>
        block.Instructions.Add(new IrInstruction(op, dest, a, b, offset, pc));

    private static IrTerminator Stop(string reason, uint raw, ulong pc) =>
        new IrStop(reason, MipsDecoder.DecodeWord(raw, pc).Mnemonic, pc, raw);

    /// <summary>
    /// Translates one instruction. Returns a terminator when the instruction ends the block, otherwise null.
    /// For control-flow instructions the delay slot is translated here and counted in the block.
    /// </summary>
    private static IrTerminator? TranslateInstruction(MipsWord w, ulong pc, IrBlock block, IMemoryReader memory, Endianness endianness, bool inDelaySlot)
    {
        IrTerminator DelaySlotBranch() =>
            Stop($"a branch inside a delay slot at 0x{pc:X8} is not supported", w.Raw, pc);

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

        IrTerminator Unsupported()
        {
            var decoded = MipsDecoder.DecodeWord(w.Raw, pc);
            var detail = MipsDecoder.UntranslatedDetail(w.Raw) is { } text ? $" ({text})" : string.Empty;
            return new IrStop($"unsupported instruction 0x{w.Raw:X8} ({decoded.Mnemonic}) at 0x{pc:X8}{detail}", decoded.Mnemonic, pc, w.Raw);
        }

        // Conditional branch: evaluate the condition into Temp0, run the delay slot, then branch.
        IrTerminator? Conditional(Action emitCondition, ulong taken, ulong link = 0)
        {
            if (inDelaySlot)
            {
                return DelaySlotBranch();
            }

            emitCondition();
            if (link != 0)
            {
                Emit(block, pc, IrOp.Const, 31, IrValue.Imm32((uint)link), default);
            }

            return WithDelaySlot(() => new IrBranch(Temp0, taken, pc + 8));
        }

        IrTerminator? Op3(IrOp op)
        {
            Emit(block, pc, op, Dest(w.Rd), Src(w.Rs), Src(w.Rt));
            return null;
        }

        IrTerminator? Shift(IrOp op, int amount = -1, bool byRegister = false)
        {
            Emit(block, pc, op, Dest(w.Rd), Src(w.Rt), byRegister ? Src(w.Rs) : IrValue.Imm((ulong)(amount >= 0 ? amount : w.Sa)));
            return null;
        }

        switch (w.Op)
        {
            case 0:
                switch (w.Funct)
                {
                    case 0x00:
                        return w.Raw != 0 ? Shift(IrOp.ShlW) : null;
                    case 0x02:
                        return Shift(IrOp.ShrW);
                    case 0x03:
                        return Shift(IrOp.SarW);
                    case 0x04:
                        return Shift(IrOp.ShlW, byRegister: true);
                    case 0x06:
                        return Shift(IrOp.ShrW, byRegister: true);
                    case 0x07:
                        return Shift(IrOp.SarW, byRegister: true);
                    case 0x08:
                    {
                        if (inDelaySlot)
                        {
                            return DelaySlotBranch();
                        }

                        Emit(block, pc, IrOp.Move, Temp0, Src(w.Rs), default);
                        return WithDelaySlot(() => new IrJumpRegister(Temp0, w.Rs));
                    }

                    case 0x09:
                    {
                        if (inDelaySlot)
                        {
                            return DelaySlotBranch();
                        }

                        Emit(block, pc, IrOp.Move, Temp0, Src(w.Rs), default);
                        Emit(block, pc, IrOp.Const, Dest(w.Rd), IrValue.Imm32((uint)(pc + 8)), default);
                        return WithDelaySlot(() => new IrJumpRegister(Temp0, w.Rs, IsCall: true, ReturnAddress: pc + 8));
                    }

                    case 0x0A:
                        return Op3(IrOp.MoveIfZero);
                    case 0x0B:
                        return Op3(IrOp.MoveIfNonZero);
                    case 0x0C:
                        return inDelaySlot ? Stop($"a syscall inside a delay slot at 0x{pc:X8} is not supported", w.Raw, pc) : new IrSyscall(pc + 4);
                    case 0x0D:
                        return Stop($"break instruction at 0x{pc:X8}", w.Raw, pc);
                    case 0x0F:
                        return null;
                    case 0x10:
                        Emit(block, pc, IrOp.Move, Dest(w.Rd), IrValue.Reg(Regs.Hi), default);
                        return null;
                    case 0x11:
                        Emit(block, pc, IrOp.Move, Regs.Hi, Src(w.Rs), default);
                        return null;
                    case 0x12:
                        Emit(block, pc, IrOp.Move, Dest(w.Rd), IrValue.Reg(Regs.Lo), default);
                        return null;
                    case 0x13:
                        Emit(block, pc, IrOp.Move, Regs.Lo, Src(w.Rs), default);
                        return null;
                    case 0x14:
                        return Shift(IrOp.Shl, byRegister: true);
                    case 0x16:
                        return Shift(IrOp.Shr, byRegister: true);
                    case 0x17:
                        return Shift(IrOp.Sar, byRegister: true);
                    case 0x18:
                    case 0x19:
                        // 32-bit multiply of the low halves: LO and HI get the sign-extended halves of the 64-bit product.
                        // The R5900 variant with a non-zero rd also copies the low word to rd.
                        Emit(block, pc, IrOp.MulW, Temp1, Src(w.Rs), Src(w.Rt));
                        Emit(block, pc, w.Funct == 0x18 ? IrOp.MulHiS : IrOp.MulHiU, Regs.Hi, Src(w.Rs), Src(w.Rt));
                        Emit(block, pc, IrOp.Move, Regs.Lo, IrValue.Reg(Temp1), default);
                        if (w.Rd != 0)
                        {
                            Emit(block, pc, IrOp.Move, w.Rd, IrValue.Reg(Temp1), default);
                        }

                        return null;
                    case 0x1A:
                    case 0x1B:
                        Emit(block, pc, w.Funct == 0x1A ? IrOp.DivS : IrOp.DivU, Temp1, Src(w.Rs), Src(w.Rt));
                        Emit(block, pc, w.Funct == 0x1A ? IrOp.RemS : IrOp.RemU, Regs.Hi, Src(w.Rs), Src(w.Rt));
                        Emit(block, pc, IrOp.Move, Regs.Lo, IrValue.Reg(Temp1), default);
                        return null;
                    case 0x20:
                    case 0x21:
                        // add traps on signed overflow on real hardware; that trap is not modelled (wrapping addition).
                        return Op3(IrOp.AddW);
                    case 0x22:
                    case 0x23:
                        return Op3(IrOp.SubW);
                    case 0x24:
                        return Op3(IrOp.And);
                    case 0x25:
                        return Op3(IrOp.Or);
                    case 0x26:
                        return Op3(IrOp.Xor);
                    case 0x27:
                        return Op3(IrOp.Nor);
                    case 0x2A:
                        return Op3(IrOp.SetLtSigned);
                    case 0x2B:
                        return Op3(IrOp.SetLtUnsigned);
                    case 0x2C:
                    case 0x2D:
                        // dadd traps on signed overflow on real hardware; that trap is not modelled (wrapping addition).
                        return Op3(IrOp.Add);
                    case 0x2E:
                    case 0x2F:
                        return Op3(IrOp.Sub);
                    case 0x38:
                        return Shift(IrOp.Shl);
                    case 0x3A:
                        return Shift(IrOp.Shr);
                    case 0x3B:
                        return Shift(IrOp.Sar);
                    case 0x3C:
                        return Shift(IrOp.Shl, w.Sa + 32);
                    case 0x3E:
                        return Shift(IrOp.Shr, w.Sa + 32);
                    case 0x3F:
                        return Shift(IrOp.Sar, w.Sa + 32);
                    default:
                        return Unsupported();
                }

            case 0x1C when w.Funct == 0x02:
                return Op3(IrOp.MulW);

            case 0x01 when w.Rt is 0 or 1 or 0x10 or 0x11:
            {
                var negative = w.Rt is 0 or 0x10;
                return Conditional(
                    () =>
                    {
                        Emit(block, pc, IrOp.SetLtSigned, Temp0, Src(w.Rs), IrValue.Imm(0));
                        if (!negative)
                        {
                            Emit(block, pc, IrOp.SetEq, Temp0, IrValue.Reg(Temp0), IrValue.Imm(0)); // bgez: not (rs < 0)
                        }
                    },
                    w.BranchTarget(pc),
                    link: w.Rt >= 0x10 ? pc + 8 : 0);
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
                    Emit(block, pc, IrOp.Const, 31, IrValue.Imm32((uint)(pc + 8)), default);
                }

                return WithDelaySlot(() => new IrJump(target, isCall));
            }

            case 0x04:
            case 0x05:
                return Conditional(() => Emit(block, pc, w.Op == 0x04 ? IrOp.SetEq : IrOp.SetNe, Temp0, Src(w.Rs), Src(w.Rt)), w.BranchTarget(pc));

            case 0x06 when w.Rt == 0:
                // blez: not (0 < rs)
                return Conditional(
                    () =>
                    {
                        Emit(block, pc, IrOp.SetLtSigned, Temp0, IrValue.Imm(0), Src(w.Rs));
                        Emit(block, pc, IrOp.SetEq, Temp0, IrValue.Reg(Temp0), IrValue.Imm(0));
                    },
                    w.BranchTarget(pc));
            case 0x07 when w.Rt == 0:
                return Conditional(() => Emit(block, pc, IrOp.SetLtSigned, Temp0, IrValue.Imm(0), Src(w.Rs)), w.BranchTarget(pc));

            case 0x08:
            case 0x09:
                Emit(block, pc, IrOp.AddW, Dest(w.Rt), Src(w.Rs), SImm(w));
                return null;
            case 0x0A:
                Emit(block, pc, IrOp.SetLtSigned, Dest(w.Rt), Src(w.Rs), SImm(w));
                return null;
            case 0x0B:
                Emit(block, pc, IrOp.SetLtUnsigned, Dest(w.Rt), Src(w.Rs), SImm(w));
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
                Emit(block, pc, IrOp.Const, Dest(w.Rt), IrValue.Imm32(w.Imm << 16), default);
                return null;

            case 0x18:
            case 0x19:
                // daddi traps on signed overflow on real hardware; that trap is not modelled (wrapping addition).
                Emit(block, pc, IrOp.Add, Dest(w.Rt), Src(w.Rs), SImm(w));
                return null;

            case 0x11:
                return TranslateCop1(w, pc, block, inDelaySlot, Conditional, Unsupported);

            case 0x20:
                Emit(block, pc, IrOp.LoadS8, Dest(w.Rt), Src(w.Rs), default, w.SImm);
                return null;
            case 0x21:
                Emit(block, pc, IrOp.LoadS16, Dest(w.Rt), Src(w.Rs), default, w.SImm);
                return null;
            case 0x23:
                Emit(block, pc, IrOp.LoadS32, Dest(w.Rt), Src(w.Rs), default, w.SImm);
                return null;
            case 0x24:
                Emit(block, pc, IrOp.LoadU8, Dest(w.Rt), Src(w.Rs), default, w.SImm);
                return null;
            case 0x25:
                Emit(block, pc, IrOp.LoadU16, Dest(w.Rt), Src(w.Rs), default, w.SImm);
                return null;
            case 0x27:
                Emit(block, pc, IrOp.LoadU32, Dest(w.Rt), Src(w.Rs), default, w.SImm);
                return null;
            case 0x37:
                Emit(block, pc, IrOp.Load64, Dest(w.Rt), Src(w.Rs), default, w.SImm);
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
            case 0x3F:
                Emit(block, pc, IrOp.Store64, 0, Src(w.Rs), Src(w.Rt), w.SImm);
                return null;
            case 0x2F: // cache
            case 0x33: // pref
                return null; // cache behaviour is not modelled; both are architecturally invisible to correct programs
            case 0x31:
                Emit(block, pc, IrOp.LoadU32, Regs.Fpr(w.Ft), Src(w.Rs), default, w.SImm);
                return null;
            case 0x39:
                Emit(block, pc, IrOp.Store32, 0, Src(w.Rs), Fpr(w.Ft), w.SImm);
                return null;
            default:
                return Unsupported();
        }
    }

    private static IrTerminator? TranslateCop1(
        MipsWord w,
        ulong pc,
        IrBlock block,
        bool inDelaySlot,
        Func<Action, ulong, ulong, IrTerminator?> conditional,
        Func<IrTerminator> unsupported)
    {
        switch (w.Fmt)
        {
            case 0: // mfc1: the 32-bit value is sign-extended into the 64-bit register
                Emit(block, pc, IrOp.Sext32, Dest(w.Rt), Fpr(w.Fs), default);
                return null;
            case 4: // mtc1: the low 32 bits go into the floating-point register
                Emit(block, pc, IrOp.Zext32, Regs.Fpr(w.Fs), Src(w.Rt), default);
                return null;
            case 8 when w.Rt is 0 or 1: // bc1f / bc1t
            {
                var wantTrue = w.Rt == 1;
                return conditional(
                    () =>
                    {
                        Emit(block, pc, wantTrue ? IrOp.Move : IrOp.SetEq, Temp0, IrValue.Reg(Regs.Fcc), wantTrue ? default : IrValue.Imm(0));
                    },
                    w.BranchTarget(pc),
                    0);
            }

            case 16:
                switch (w.Funct)
                {
                    case 0x00: EmitFpu(block, pc, IrOp.FAdd, w); return null;
                    case 0x01: EmitFpu(block, pc, IrOp.FSub, w); return null;
                    case 0x02: EmitFpu(block, pc, IrOp.FMul, w); return null;
                    case 0x03: EmitFpu(block, pc, IrOp.FDiv, w); return null;
                    case 0x04: Emit(block, pc, IrOp.FSqrt, Regs.Fpr(w.Fd), Fpr(w.Fs), default); return null;
                    case 0x05: Emit(block, pc, IrOp.FAbs, Regs.Fpr(w.Fd), Fpr(w.Fs), default); return null;
                    case 0x06: Emit(block, pc, IrOp.Move, Regs.Fpr(w.Fd), Fpr(w.Fs), default); return null;
                    case 0x07: Emit(block, pc, IrOp.FNeg, Regs.Fpr(w.Fd), Fpr(w.Fs), default); return null;
                    case 0x0D: Emit(block, pc, IrOp.TruncWS, Regs.Fpr(w.Fd), Fpr(w.Fs), default); return null;
                    case 0x24: Emit(block, pc, IrOp.CvtWS, Regs.Fpr(w.Fd), Fpr(w.Fs), default); return null;
                    case 0x30: Emit(block, pc, IrOp.Const, Regs.Fcc, IrValue.Imm(0), default); return null;
                    case 0x32: Emit(block, pc, IrOp.FEq, Regs.Fcc, Fpr(w.Fs), Fpr(w.Ft)); return null;
                    case 0x34:
                    case 0x3C: Emit(block, pc, IrOp.FLt, Regs.Fcc, Fpr(w.Fs), Fpr(w.Ft)); return null;
                    case 0x36:
                    case 0x3E: Emit(block, pc, IrOp.FLe, Regs.Fcc, Fpr(w.Fs), Fpr(w.Ft)); return null;
                    default: return unsupported();
                }

            case 20 when w.Funct == 0x20: // cvt.s.w
                Emit(block, pc, IrOp.CvtSW, Regs.Fpr(w.Fd), Fpr(w.Fs), default);
                return null;
            default:
                return unsupported();
        }
    }

    private static void EmitFpu(IrBlock block, ulong pc, IrOp op, MipsWord w) =>
        Emit(block, pc, op, Regs.Fpr(w.Fd), Fpr(w.Fs), Fpr(w.Ft));
}
