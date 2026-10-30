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
}

/// <summary>
/// Decoder for a deliberately small integer subset of MIPS32 (no FPU, no HI/LO, no coprocessors, no branch-likely).
/// Anything outside that subset is reported as an unknown word instead of being guessed.
/// </summary>
public sealed class MipsDecoder : IArchitectureDecoder
{
    public string Name => "MIPS32 integer subset decoder (proof of concept)";

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

    internal static DecodedInstruction DecodeWord(uint raw, ulong pc)
    {
        var w = new MipsWord(raw);
        DecodedInstruction Make(string mnemonic, string operands, InstructionKind kind = InstructionKind.Other, bool delay = false, ulong? target = null) =>
            new(pc, raw, mnemonic, operands, kind, delay, target);

        static string R(int i) => MipsRegisters.Name(i);
        DecodedInstruction Unknown() => Make(".word", $"0x{raw:X8}", InstructionKind.Unknown);

        string Mem(string m) => $"{R(w.Rt)}, {w.SImm}({R(w.Rs)})";

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
                    case 0x0C: return Make("syscall", "", InstructionKind.Syscall);
                    case 0x21: return Make("addu", $"{R(w.Rd)}, {R(w.Rs)}, {R(w.Rt)}");
                    case 0x23: return Make("subu", $"{R(w.Rd)}, {R(w.Rs)}, {R(w.Rt)}");
                    case 0x24: return Make("and", $"{R(w.Rd)}, {R(w.Rs)}, {R(w.Rt)}");
                    case 0x25: return Make("or", $"{R(w.Rd)}, {R(w.Rs)}, {R(w.Rt)}");
                    case 0x26: return Make("xor", $"{R(w.Rd)}, {R(w.Rs)}, {R(w.Rt)}");
                    case 0x27: return Make("nor", $"{R(w.Rd)}, {R(w.Rs)}, {R(w.Rt)}");
                    case 0x2A: return Make("slt", $"{R(w.Rd)}, {R(w.Rs)}, {R(w.Rt)}");
                    case 0x2B: return Make("sltu", $"{R(w.Rd)}, {R(w.Rs)}, {R(w.Rt)}");
                    default: return Unknown();
                }

            case 0x1C:
                return w.Funct == 0x02 ? Make("mul", $"{R(w.Rd)}, {R(w.Rs)}, {R(w.Rt)}") : Unknown();
            case 0x01:
            {
                var target = w.BranchTarget(pc);
                return w.Rt switch
                {
                    0 => Make("bltz", $"{R(w.Rs)}, 0x{target:X8}", InstructionKind.Branch, true, target),
                    1 => Make("bgez", $"{R(w.Rs)}, 0x{target:X8}", InstructionKind.Branch, true, target),
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
            {
                var target = w.BranchTarget(pc);
                return Make("beq", $"{R(w.Rs)}, {R(w.Rt)}, 0x{target:X8}", InstructionKind.Branch, true, target);
            }

            case 0x05:
            {
                var target = w.BranchTarget(pc);
                return Make("bne", $"{R(w.Rs)}, {R(w.Rt)}, 0x{target:X8}", InstructionKind.Branch, true, target);
            }

            case 0x06 when w.Rt == 0:
            {
                var target = w.BranchTarget(pc);
                return Make("blez", $"{R(w.Rs)}, 0x{target:X8}", InstructionKind.Branch, true, target);
            }

            case 0x07 when w.Rt == 0:
            {
                var target = w.BranchTarget(pc);
                return Make("bgtz", $"{R(w.Rs)}, 0x{target:X8}", InstructionKind.Branch, true, target);
            }

            case 0x09: return Make("addiu", $"{R(w.Rt)}, {R(w.Rs)}, {w.SImm}");
            case 0x0A: return Make("slti", $"{R(w.Rt)}, {R(w.Rs)}, {w.SImm}");
            case 0x0B: return Make("sltiu", $"{R(w.Rt)}, {R(w.Rs)}, {w.SImm}");
            case 0x0C: return Make("andi", $"{R(w.Rt)}, {R(w.Rs)}, 0x{w.Imm:X}");
            case 0x0D: return Make("ori", $"{R(w.Rt)}, {R(w.Rs)}, 0x{w.Imm:X}");
            case 0x0E: return Make("xori", $"{R(w.Rt)}, {R(w.Rs)}, 0x{w.Imm:X}");
            case 0x0F: return Make("lui", $"{R(w.Rt)}, 0x{w.Imm:X}");
            case 0x20: return Make("lb", Mem("lb"));
            case 0x21: return Make("lh", Mem("lh"));
            case 0x23: return Make("lw", Mem("lw"));
            case 0x24: return Make("lbu", Mem("lbu"));
            case 0x25: return Make("lhu", Mem("lhu"));
            case 0x28: return Make("sb", Mem("sb"));
            case 0x29: return Make("sh", Mem("sh"));
            case 0x2B: return Make("sw", Mem("sw"));
            default: return Unknown();
        }
    }
}
