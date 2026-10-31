using System.Text;

namespace RecompRadar.TestSupport;

/// <summary>
/// A self-made program (no third-party code) that exercises the 64-bit side of the R5900/MIPS III integer unit: 64-bit
/// arithmetic and shifts, 32-bit instructions that must sign-extend, 64-bit and partial loads and stores, 64-bit compares and
/// branches, multiply/divide on the low 32 bits, FPU register transfers, and jumps through a register with garbage in its upper half.
/// <see cref="ExpectedOutput"/> is computed independently in C# from the instruction definitions (MIPS64 semantics), not by running
/// the translator. 64-bit results are printed as two signed 32-bit integers, upper half first. The last section is a recursive
/// function written the way compilers for the PlayStation 2 write them (64-bit stack frame, <c>daddu</c> as "move").
/// </summary>
public static class Int64SelfTest
{
    public const ulong CodeAddress = 0x00100000;
    public const uint Data = 0x00300000;

    public static byte[] BuildElf()
    {
        var program = Build(new StringBuilder());
        var builder = new ElfBuilder { Machine = 8, Flags = 0x20924001 };
        builder.WithCode(program.ToBytes(CodeAddress, bigEndian: false), CodeAddress);
        builder.WithData(new byte[0x1000], Data, ".data", flags: 6);
        builder.Symbols.Add(SymbolSpec.Function("main", program.AddressOf("main", CodeAddress)));
        builder.Symbols.Add(SymbolSpec.Function("garbage_ra", program.AddressOf("garbage_ra", CodeAddress)));
        return builder.Build();
    }

    public static string ExpectedOutput()
    {
        var expected = new StringBuilder();
        Build(expected);
        return expected.ToString();
    }

    private static ulong Sx(uint value) => unchecked((ulong)(long)(int)value);

    /// <summary>Assembles the program and, in the same pass, writes what it must print into <paramref name="expected"/>.</summary>
    private static MipsProgram Build(StringBuilder expected)
    {
        void P64(ulong v) => expected.Append(unchecked((int)(uint)(v >> 32))).Append('\n').Append(unchecked((int)(uint)v)).Append('\n');
        void PInt(long v) => expected.Append(unchecked((int)v)).Append('\n');

        var p = new MipsProgram();
        p.Label("main");

        // ---- 1. 64-bit addition and subtraction, wrapping ----
        const ulong a = 0x123456789ABCDEF0UL;
        const ulong b = 0x0FEDCBA987654321UL;
        p.Li64(Reg.T0, a).Li64(Reg.T1, b).Daddu(Reg.T2, Reg.T0, Reg.T1).Print64(Reg.T2);
        P64(unchecked(a + b));
        p.Dsubu(Reg.T2, Reg.T0, Reg.T1).Print64(Reg.T2);
        P64(unchecked(a - b));
        p.Li64(Reg.T0, ulong.MaxValue).Daddiu(Reg.T2, Reg.T0, 2).Print64(Reg.T2);
        P64(1);
        p.Daddiu(Reg.T2, Reg.Zero, -2).Print64(Reg.T2);
        P64(ulong.MaxValue - 1);

        // ---- 2. 32-bit instructions sign-extend their result to 64 bits ----
        p.Li32(Reg.T0, 0x7FFFFFFF).Addiu(Reg.T1, Reg.Zero, 1).Addu(Reg.T2, Reg.T0, Reg.T1).Print64(Reg.T2);
        P64(Sx(0x80000000));
        p.Li32(Reg.T0, 0x80000000).Subu(Reg.T2, Reg.T0, Reg.T1).Print64(Reg.T2);
        P64(0x7FFFFFFF);
        p.Li32(Reg.T0, 0x40000000).Sll(Reg.T2, Reg.T0, 1).Print64(Reg.T2);
        P64(Sx(0x80000000));
        p.Li32(Reg.T0, 0x80000000).Srl(Reg.T2, Reg.T0, 4).Print64(Reg.T2);
        P64(0x08000000);
        p.Li32(Reg.T0, 0x80000000).Sra(Reg.T2, Reg.T0, 4).Print64(Reg.T2);
        P64(Sx(0xF8000000));
        p.Lui(Reg.T0, 0x8000).Ori(Reg.T0, Reg.T0, 1).Print64(Reg.T0);
        P64(Sx(0x80000001));
        p.Andi(Reg.T1, Reg.T0, 0xFFFF).Print64(Reg.T1);
        P64(1);

        // ---- 3. 64-bit shifts, by constant and by register (the count uses 6 bits) ----
        const ulong v = 0x8000000000000001UL;
        p.Li64(Reg.S0, v);
        p.Dsll(Reg.T2, Reg.S0, 1).Print64(Reg.T2);
        P64(v << 1);
        p.Dsrl(Reg.T2, Reg.S0, 1).Print64(Reg.T2);
        P64(v >> 1);
        p.Dsra(Reg.T2, Reg.S0, 1).Print64(Reg.T2);
        P64(unchecked((ulong)((long)v >> 1)));
        p.Dsll32(Reg.T2, Reg.S0, 0).Print64(Reg.T2);
        P64(v << 32);
        p.Dsrl32(Reg.T2, Reg.S0, 4).Print64(Reg.T2);
        P64(v >> 36);
        p.Dsra32(Reg.T2, Reg.S0, 0).Print64(Reg.T2);
        P64(unchecked((ulong)((long)v >> 32)));
        p.Addiu(Reg.T3, Reg.Zero, 65);
        p.Dsllv(Reg.T2, Reg.S0, Reg.T3).Print64(Reg.T2);
        P64(v << 1);
        p.Dsrlv(Reg.T2, Reg.S0, Reg.T3).Print64(Reg.T2);
        P64(v >> 1);
        p.Dsrav(Reg.T2, Reg.S0, Reg.T3).Print64(Reg.T2);
        P64(unchecked((ulong)((long)v >> 1)));

        // ---- 4. 64-bit and partial memory access (little-endian bytes 01 00 00 00 00 00 00 80) ----
        p.Li32(Reg.S1, Data + 0x40).Sd(Reg.S0, 0, Reg.S1).Ld(Reg.T2, 0, Reg.S1).Print64(Reg.T2);
        P64(v);
        p.Lwu(Reg.T2, 4, Reg.S1).Print64(Reg.T2);
        P64(0x80000000UL);
        p.Lw(Reg.T2, 4, Reg.S1).Print64(Reg.T2);
        P64(Sx(0x80000000));
        p.Lb(Reg.T2, 7, Reg.S1).Print64(Reg.T2);
        P64(unchecked((ulong)(long)(sbyte)0x80));
        p.Lbu(Reg.T2, 7, Reg.S1).Print64(Reg.T2);
        P64(0x80);
        p.Lh(Reg.T2, 6, Reg.S1).Print64(Reg.T2);
        P64(unchecked((ulong)(long)(short)0x8000));
        p.Lhu(Reg.T2, 6, Reg.S1).Print64(Reg.T2);
        P64(0x8000);
        p.Sw(Reg.S0, 8, Reg.S1).Ld(Reg.T2, 8, Reg.S1).Print64(Reg.T2);         // sw stores the low word only
        P64(1);

        // ---- 5. compares and branches look at all 64 bits ----
        p.Li64(Reg.T0, 0x8000000000000000UL).Addiu(Reg.T1, Reg.Zero, 1);
        p.Slt(Reg.T2, Reg.T0, Reg.T1).PrintIntEe(Reg.T2);
        PInt(1);
        p.Sltu(Reg.T2, Reg.T0, Reg.T1).PrintIntEe(Reg.T2);
        PInt(0);
        p.Slti(Reg.T2, Reg.T0, -1).PrintIntEe(Reg.T2);
        PInt(1);
        p.Sltiu(Reg.T2, Reg.T0, -1).PrintIntEe(Reg.T2);
        PInt(1);
        p.Li64(Reg.T3, ulong.MaxValue).Sltiu(Reg.T2, Reg.T3, -1).PrintIntEe(Reg.T2);
        PInt(0);
        p.Li64(Reg.T0, 0x100000003UL).Addiu(Reg.T1, Reg.Zero, 3).Addiu(Reg.T2, Reg.Zero, 1).Bne(Reg.T0, Reg.T1, "ne64").Nop()
            .Addiu(Reg.T2, Reg.Zero, 0).Label("ne64").PrintIntEe(Reg.T2);                // the low words are equal, the full values are not
        PInt(1);
        p.Li64(Reg.T0, 0x100000000UL).Addiu(Reg.T2, Reg.Zero, 1).Bgtz(Reg.T0, "gt64").Nop()
            .Addiu(Reg.T2, Reg.Zero, 0).Label("gt64").PrintIntEe(Reg.T2);                // the low word is 0, the value is positive
        PInt(1);
        p.Li64(Reg.T0, 0x8000000000000005UL).Addiu(Reg.T2, Reg.Zero, 1).Bltz(Reg.T0, "lt64").Nop()
            .Addiu(Reg.T2, Reg.Zero, 0).Label("lt64").PrintIntEe(Reg.T2);                // the low word is positive, the value is negative
        PInt(1);
        p.Li64(Reg.T0, 0x100000000UL).Addiu(Reg.T3, Reg.Zero, 77).Addiu(Reg.T4, Reg.Zero, 5);
        p.Movz(Reg.T3, Reg.T4, Reg.T0).PrintIntEe(Reg.T3);                               // t0 is not zero: no move
        PInt(77);
        p.Movn(Reg.T3, Reg.T4, Reg.T0).PrintIntEe(Reg.T3);
        PInt(5);

        // ---- 6. multiply and divide use the low 32 bits and sign-extend HI and LO ----
        p.Li64(Reg.T0, 0x100000003UL).Addiu(Reg.T1, Reg.Zero, 5).Mult(Reg.T0, Reg.T1).Mflo(Reg.T2).Mfhi(Reg.T3).Print64(Reg.T2).Print64(Reg.T3);
        P64(15);
        P64(0);
        p.Li64(Reg.T0, 0xFFFFFFFFUL).Multu(Reg.T0, Reg.T0).Mflo(Reg.T2).Mfhi(Reg.T3).Print64(Reg.T2).Print64(Reg.T3);
        P64(1);                                    // 0xFFFFFFFF * 0xFFFFFFFF = 0xFFFFFFFE00000001
        P64(Sx(0xFFFFFFFE));
        p.Li64(Reg.T0, 0x100000007UL).Addiu(Reg.T1, Reg.Zero, 2).Div(Reg.T0, Reg.T1).Mflo(Reg.T2).Mfhi(Reg.T3).Print64(Reg.T2).Print64(Reg.T3);
        P64(3);
        P64(1);
        p.Addiu(Reg.T0, Reg.Zero, -7).Div(Reg.T0, Reg.T1).Mflo(Reg.T2).Mfhi(Reg.T3).Print64(Reg.T2).Print64(Reg.T3);
        P64(Sx(unchecked((uint)-3)));
        P64(Sx(unchecked((uint)-1)));

        // ---- 7. FPU registers hold 32 bits: mtc1 keeps the low word, mfc1 sign-extends ----
        p.Li32(Reg.T0, 0x3F800000).Mtc1(Reg.T0, 0).Mfc1(Reg.T1, 0).Print64(Reg.T1);
        P64(0x3F800000);
        p.Li32(Reg.T0, 0xBF800000).Mtc1(Reg.T0, 0).Mfc1(Reg.T1, 0).Print64(Reg.T1);
        P64(Sx(0xBF800000));
        p.Li64(Reg.T0, 0xAAAAAAAA40490FDBUL).Mtc1(Reg.T0, 0).Mfc1(Reg.T1, 0).Print64(Reg.T1);
        P64(0x40490FDB);

        // ---- 8. a jump through a register uses only its low 32 bits ----
        p.Jal("garbage_ra").Nop().Addiu(Reg.T2, Reg.Zero, 7).PrintIntEe(Reg.T2);
        PInt(7);

        // ---- 9. a function in the style of compiled PlayStation 2 code: 64-bit stack frame, "move" as daddu, restore in the delay slot ----
        p.Addiu(Reg.A0, Reg.Zero, 15).Jal("fib64").Nop().PrintIntEe(Reg.V0);
        PInt(610);

        p.ExitWith(0);

        p.Label("fib64")
            .Daddiu(Reg.Sp, Reg.Sp, -32).Sd(Reg.Ra, 16, Reg.Sp).Sd(Reg.S0, 8, Reg.Sp).Sd(Reg.S1, 0, Reg.Sp)
            .Daddu(Reg.S0, Reg.A0, Reg.Zero)
            .Slti(Reg.T0, Reg.S0, 2).Beq(Reg.T0, Reg.Zero, "fib64_rec").Nop()
            .Daddu(Reg.V0, Reg.S0, Reg.Zero).J("fib64_out").Nop()
            .Label("fib64_rec")
            .Daddiu(Reg.A0, Reg.S0, -1).Jal("fib64").Nop().Daddu(Reg.S1, Reg.V0, Reg.Zero)
            .Daddiu(Reg.A0, Reg.S0, -2).Jal("fib64").Nop().Addu(Reg.V0, Reg.V0, Reg.S1)
            .Label("fib64_out")
            .Ld(Reg.S1, 0, Reg.Sp).Ld(Reg.S0, 8, Reg.Sp).Ld(Reg.Ra, 16, Reg.Sp).Jr(Reg.Ra).Daddiu(Reg.Sp, Reg.Sp, 32);

        p.Label("garbage_ra").Li32(Reg.T8, 0x7FFF).Dsll32(Reg.T8, Reg.T8, 0).Or(Reg.Ra, Reg.Ra, Reg.T8).Jr(Reg.Ra).Nop();
        return p;
    }
}
