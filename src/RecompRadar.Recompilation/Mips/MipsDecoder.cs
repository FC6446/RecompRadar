using System.Buffers.Binary;
using RecompRadar.Core.Model;

namespace RecompRadar.Recompilation.Mips;

/// <summary>Field extraction shared by the decoder and the translator.</summary>
internal readonly struct MipsWord
{
    public MipsWord(uint raw)
    {
        Raw = raw;
    }

    public uint Raw { get; }
    public uint Op => Raw >> 26;
    public int Rs => (int)((Raw >> 21) & 31);
    public int Rt => (int)((Raw >> 16) & 31);
    public int Rd => (int)((Raw >> 11) & 31);
    public int Sa => (int)((Raw >> 6) & 31);
    public uint Funct => Raw & 63;
    public uint Imm => Raw & 0xFFFF;
    public int SImm => (short)(Raw & 0xFFFF);
    public uint JumpIndex => Raw & 0x03FFFFFF;

    // COP1 field names: fmt = rs, ft = rt, fs = rd, fd = sa.
    public int Fmt => Rs;
    public int Ft => Rt;
    public int Fs => Rd;
    public int Fd => Sa;

    public ulong BranchTarget(ulong pc) => unchecked((ulong)((long)pc + 4 + (SImm * 4L))) & 0xFFFFFFFF;

    public ulong JumpTarget(ulong pc) => ((pc + 4) & 0xF0000000) | ((ulong)JumpIndex << 2);
}

public static class MipsRegisters
{
    private static readonly string[] Names =
    [
        "$zero", "$at", "$v0", "$v1", "$a0", "$a1", "$a2", "$a3",
        "$t0", "$t1", "$t2", "$t3", "$t4", "$t5", "$t6", "$t7",
        "$s0", "$s1", "$s2", "$s3", "$s4", "$s5", "$s6", "$s7",
        "$t8", "$t9", "$k0", "$k1", "$gp", "$sp", "$fp", "$ra",
    ];

    public static string Name(int index) => Names[index & 31];

    public static string Fpr(int index) => $"$f{index & 31}";
}

/// <summary>
/// Decoder for the integer (including the 64-bit MIPS III instructions of the R5900), HI/LO and single-precision FPU subset
/// that RecompRadar can translate. Instructions that are valid but not translated (for example branch-likely or the unaligned
/// and 128-bit memory instructions) are still named, so reports can say exactly what is missing; everything else is shown as
/// an unknown word instead of being guessed.
/// </summary>
public sealed class MipsDecoder : IArchitectureDecoder
{
    public string Name => "MIPS32/R5900 integer and FPU subset decoder";

    public DecodedInstruction? Decode(IMemoryReader memory, Endianness endianness, ulong address)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (!memory.TryRead(address, bytes))
        {
            return null;
        }

        var word = endianness == Endianness.Little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);
        return DecodeWord(word, address);
    }

    /// <summary>Mnemonics that are decoded but have no translation yet; the translator reports them as unsupported.</summary>
    internal static bool IsDecodedButUntranslated(uint raw) => UntranslatedDetail(raw) is not null;

    /// <summary>Why a recognised instruction is not translated, or null when it is translated or not recognised at all.</summary>
    internal static string? UntranslatedDetail(uint raw)
    {
        var w = new MipsWord(raw);
        if (w.Op is 0x14 or 0x15 or 0x16 or 0x17
            || (w.Op == 0x01 && w.Rt is 0x02 or 0x03 or 0x12 or 0x13)
            || (w.Op == 0x11 && w.Fmt == 8 && w.Rt is 2 or 3))
        {
            return "branch-likely instructions are not supported yet";
        }

        if (w.Op is 0x1A or 0x1B or 0x22 or 0x26 or 0x2A or 0x2C or 0x2D or 0x2E)
        {
            return "unaligned loads and stores are not supported yet";
        }

        if (w.Op is 0x1E or 0x1F)
        {
            return "128-bit loads and stores are not supported; only the low 64 bits of the R5900 registers are modelled";
        }

        if (w.Op == 0 && w.Funct is 0x1C or 0x1D or 0x1E or 0x1F)
        {
            return "64-bit multiply and divide are not supported yet";
        }

        return null;
    }

    internal static DecodedInstruction DecodeWord(uint raw, ulong pc)
    {
        var w = new MipsWord(raw);

        DecodedInstruction Make(string mnemonic, string operands, InstructionKind kind = InstructionKind.Other, bool delay = false, ulong? target = null) =>
            new(pc, raw, mnemonic, operands, kind, delay, target);

        static string R(int i) => MipsRegisters.Name(i);
        static string Fp(int i) => MipsRegisters.Fpr(i);
        DecodedInstruction Unknown() => Make(".word", $"0x{raw:X8}", InstructionKind.Unknown);
        string Mem() => $"{R(w.Rt)}, {w.SImm}({R(w.Rs)})";
        string R3() => $"{R(w.Rd)}, {R(w.Rs)}, {R(w.Rt)}";
        string Branch2(string mnemonic, ulong target) => $"{R(w.Rs)}, {R(w.Rt)}, 0x{target:X8}";

        switch (w.Op)
        {
            case 0:
                if (raw == 0)
                {
                    return Make("nop", "");
                }

                switch (w.Funct)
                {
                    case 0x00: return Make("sll", $"{R(w.Rd)}, {R(w.Rt)}, {w.Sa}");
                    case 0x02: return Make("srl", $"{R(w.Rd)}, {R(w.Rt)}, {w.Sa}");
                    case 0x03: return Make("sra", $"{R(w.Rd)}, {R(w.Rt)}, {w.Sa}");
                    case 0x04: return Make("sllv", $"{R(w.Rd)}, {R(w.Rt)}, {R(w.Rs)}");
                    case 0x06: return Make("srlv", $"{R(w.Rd)}, {R(w.Rt)}, {R(w.Rs)}");
                    case 0x07: return Make("srav", $"{R(w.Rd)}, {R(w.Rt)}, {R(w.Rs)}");
                    case 0x08: return Make("jr", R(w.Rs), InstructionKind.JumpRegister, true);
                    case 0x09: return Make("jalr", w.Rd == 31 ? R(w.Rs) : $"{R(w.Rd)}, {R(w.Rs)}", InstructionKind.JumpRegister, true);
                    case 0x0A: return Make("movz", R3());
                    case 0x0B: return Make("movn", R3());
                    case 0x0C: return Make("syscall", "", InstructionKind.Syscall);
                    case 0x0D: return Make("break", "");
                    case 0x0F: return Make("sync", "");
                    case 0x14: return Make("dsllv", $"{R(w.Rd)}, {R(w.Rt)}, {R(w.Rs)}");
                    case 0x16: return Make("dsrlv", $"{R(w.Rd)}, {R(w.Rt)}, {R(w.Rs)}");
                    case 0x17: return Make("dsrav", $"{R(w.Rd)}, {R(w.Rt)}, {R(w.Rs)}");
                    case 0x10: return Make("mfhi", R(w.Rd));
                    case 0x11: return Make("mthi", R(w.Rs));
                    case 0x12: return Make("mflo", R(w.Rd));
                    case 0x13: return Make("mtlo", R(w.Rs));
                    case 0x18: return Make("mult", w.Rd == 0 ? $"{R(w.Rs)}, {R(w.Rt)}" : R3());
                    case 0x19: return Make("multu", w.Rd == 0 ? $"{R(w.Rs)}, {R(w.Rt)}" : R3());
                    case 0x1A: return Make("div", $"{R(w.Rs)}, {R(w.Rt)}");
                    case 0x1B: return Make("divu", $"{R(w.Rs)}, {R(w.Rt)}");
                    case 0x1C: return Make("dmult", $"{R(w.Rs)}, {R(w.Rt)}");
                    case 0x1D: return Make("dmultu", $"{R(w.Rs)}, {R(w.Rt)}");
                    case 0x1E: return Make("ddiv", $"{R(w.Rs)}, {R(w.Rt)}");
                    case 0x1F: return Make("ddivu", $"{R(w.Rs)}, {R(w.Rt)}");
                    case 0x20: return Make("add", R3());
                    case 0x21: return Make("addu", R3());
                    case 0x22: return Make("sub", R3());
                    case 0x23: return Make("subu", R3());
                    case 0x24: return Make("and", R3());
                    case 0x25: return Make("or", R3());
                    case 0x26: return Make("xor", R3());
                    case 0x27: return Make("nor", R3());
                    case 0x2A: return Make("slt", R3());
                    case 0x2B: return Make("sltu", R3());
                    case 0x2C: return Make("dadd", R3());
                    case 0x2D: return Make("daddu", R3());
                    case 0x2E: return Make("dsub", R3());
                    case 0x2F: return Make("dsubu", R3());
                    case 0x38: return Make("dsll", $"{R(w.Rd)}, {R(w.Rt)}, {w.Sa}");
                    case 0x3A: return Make("dsrl", $"{R(w.Rd)}, {R(w.Rt)}, {w.Sa}");
                    case 0x3B: return Make("dsra", $"{R(w.Rd)}, {R(w.Rt)}, {w.Sa}");
                    case 0x3C: return Make("dsll32", $"{R(w.Rd)}, {R(w.Rt)}, {w.Sa}");
                    case 0x3E: return Make("dsrl32", $"{R(w.Rd)}, {R(w.Rt)}, {w.Sa}");
                    case 0x3F: return Make("dsra32", $"{R(w.Rd)}, {R(w.Rt)}, {w.Sa}");
                    default: return Unknown();
                }

            case 0x1C:
                return w.Funct == 0x02 ? Make("mul", R3()) : Unknown();
            case 0x01:
            {
                var target = w.BranchTarget(pc);
                var ops = $"{R(w.Rs)}, 0x{target:X8}";
                return w.Rt switch
                {
                    0x00 => Make("bltz", ops, InstructionKind.Branch, true, target),
                    0x01 => Make("bgez", ops, InstructionKind.Branch, true, target),
                    0x02 => Make("bltzl", ops, InstructionKind.Branch, true, target),
                    0x03 => Make("bgezl", ops, InstructionKind.Branch, true, target),
                    0x10 => Make("bltzal", ops, InstructionKind.Call, true, target),
                    0x11 => Make("bgezal", ops, InstructionKind.Call, true, target),
                    0x12 => Make("bltzall", ops, InstructionKind.Call, true, target),
                    0x13 => Make("bgezall", ops, InstructionKind.Call, true, target),
                    _ => Unknown(),
                };
            }

            case 0x02:
            {
                var target = w.JumpTarget(pc);
                return Make("j", $"0x{target:X8}", InstructionKind.Jump, true, target);
            }

            case 0x03:
            {
                var target = w.JumpTarget(pc);
                return Make("jal", $"0x{target:X8}", InstructionKind.Call, true, target);
            }

            case 0x04:
            case 0x05:
            case 0x14:
            case 0x15:
            {
                var target = w.BranchTarget(pc);
                var name = w.Op switch { 0x04 => "beq", 0x05 => "bne", 0x14 => "beql", _ => "bnel" };
                return Make(name, Branch2(name, target), InstructionKind.Branch, true, target);
            }

            case 0x06 or 0x07 or 0x16 or 0x17 when w.Rt == 0:
            {
                var target = w.BranchTarget(pc);
                var name = w.Op switch { 0x06 => "blez", 0x07 => "bgtz", 0x16 => "blezl", _ => "bgtzl" };
                return Make(name, $"{R(w.Rs)}, 0x{target:X8}", InstructionKind.Branch, true, target);
            }

            case 0x08: return Make("addi", $"{R(w.Rt)}, {R(w.Rs)}, {w.SImm}");
            case 0x09: return Make("addiu", $"{R(w.Rt)}, {R(w.Rs)}, {w.SImm}");
            case 0x0A: return Make("slti", $"{R(w.Rt)}, {R(w.Rs)}, {w.SImm}");
            case 0x0B: return Make("sltiu", $"{R(w.Rt)}, {R(w.Rs)}, {w.SImm}");
            case 0x0C: return Make("andi", $"{R(w.Rt)}, {R(w.Rs)}, 0x{w.Imm:X}");
            case 0x0D: return Make("ori", $"{R(w.Rt)}, {R(w.Rs)}, 0x{w.Imm:X}");
            case 0x0E: return Make("xori", $"{R(w.Rt)}, {R(w.Rs)}, 0x{w.Imm:X}");
            case 0x0F: return Make("lui", $"{R(w.Rt)}, 0x{w.Imm:X}");
            case 0x18: return Make("daddi", $"{R(w.Rt)}, {R(w.Rs)}, {w.SImm}");
            case 0x19: return Make("daddiu", $"{R(w.Rt)}, {R(w.Rs)}, {w.SImm}");
            case 0x1A: return Make("ldl", Mem());
            case 0x1B: return Make("ldr", Mem());
            case 0x1E: return Make("lq", Mem());
            case 0x1F: return Make("sq", Mem());
            case 0x11: return DecodeCop1(w, raw, pc);
            case 0x20: return Make("lb", Mem());
            case 0x21: return Make("lh", Mem());
            case 0x23: return Make("lw", Mem());
            case 0x22: return Make("lwl", Mem());
            case 0x24: return Make("lbu", Mem());
            case 0x25: return Make("lhu", Mem());
            case 0x26: return Make("lwr", Mem());
            case 0x27: return Make("lwu", Mem());
            case 0x28: return Make("sb", Mem());
            case 0x29: return Make("sh", Mem());
            case 0x2A: return Make("swl", Mem());
            case 0x2B: return Make("sw", Mem());
            case 0x2C: return Make("sdl", Mem());
            case 0x2D: return Make("sdr", Mem());
            case 0x2E: return Make("swr", Mem());
            case 0x2F: return Make("cache", "");
            case 0x31: return Make("lwc1", $"{Fp(w.Ft)}, {w.SImm}({R(w.Rs)})");
            case 0x33: return Make("pref", "");
            case 0x37: return Make("ld", Mem());
            case 0x39: return Make("swc1", $"{Fp(w.Ft)}, {w.SImm}({R(w.Rs)})");
            case 0x3F: return Make("sd", Mem());
            default: return Unknown();
        }
    }

    private static DecodedInstruction DecodeCop1(MipsWord w, uint raw, ulong pc)
    {
        DecodedInstruction Make(string mnemonic, string operands, InstructionKind kind = InstructionKind.Other, bool delay = false, ulong? target = null) =>
            new(pc, raw, mnemonic, operands, kind, delay, target);

        static string Fp(int i) => MipsRegisters.Fpr(i);
        DecodedInstruction Unknown() => Make(".word", $"0x{raw:X8}", InstructionKind.Unknown);
        var three = $"{Fp(w.Fd)}, {Fp(w.Fs)}, {Fp(w.Ft)}";
        var two = $"{Fp(w.Fd)}, {Fp(w.Fs)}";
        var compare = $"{Fp(w.Fs)}, {Fp(w.Ft)}";

        switch (w.Fmt)
        {
            case 0: return Make("mfc1", $"{MipsRegisters.Name(w.Rt)}, {Fp(w.Fs)}");
            case 4: return Make("mtc1", $"{MipsRegisters.Name(w.Rt)}, {Fp(w.Fs)}");
            case 8:
            {
                var target = w.BranchTarget(pc);
                var name = w.Rt switch { 0 => "bc1f", 1 => "bc1t", 2 => "bc1fl", 3 => "bc1tl", _ => null };
                return name is null ? Unknown() : Make(name, $"0x{target:X8}", InstructionKind.Branch, true, target);
            }

            case 16:
                return w.Funct switch
                {
                    0x00 => Make("add.s", three),
                    0x01 => Make("sub.s", three),
                    0x02 => Make("mul.s", three),
                    0x03 => Make("div.s", three),
                    0x04 => Make("sqrt.s", two),
                    0x05 => Make("abs.s", two),
                    0x06 => Make("mov.s", two),
                    0x07 => Make("neg.s", two),
                    0x0D => Make("trunc.w.s", two),
                    0x24 => Make("cvt.w.s", two),
                    0x30 => Make("c.f.s", compare),
                    0x32 => Make("c.eq.s", compare),
                    0x34 or 0x3C => Make("c.lt.s", compare),
                    0x36 or 0x3E => Make("c.le.s", compare),
                    _ => Unknown(),
                };
            case 20:
                return w.Funct == 0x20 ? Make("cvt.s.w", two) : Unknown();
            default:
                return Unknown();
        }
    }
}
