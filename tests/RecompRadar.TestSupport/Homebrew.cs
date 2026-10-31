using System.Globalization;
using System.Text;

namespace RecompRadar.TestSupport;

/// <summary>
/// A self-made, PS2-style test program written directly in MIPS machine code (no third-party code). It exercises
/// recursion with stack frames, arrays and sorting, byte-wise string handling, HI/LO multiply and divide, conditional moves
/// and single-precision floating point, and reports everything through RecompRadar's PoC system call ABI.
/// <see cref="ExpectedOutput"/> computes the correct output independently in C#, so the program can be verified against it.
/// </summary>
public static class Ps2StyleSelfTest
{
    public const ulong CodeAddress = 0x00100000;
    public const uint DataAddress = 0x00500000;

    // Data layout (all inside one writable segment).
    private const uint StringTable = DataAddress + 0x000;
    private const uint SourceText = DataAddress + 0x200;
    private const uint ReverseBuffer = DataAddress + 0x300;
    private const uint SortArray = DataAddress + 0x400;
    private const uint FloatBuffer = DataAddress + 0x500;
    private const int DataSize = 0x1000;
    private const string Text = "Hello, PS2 world!";

    private static readonly string[] Labels =
    [
        "RecompRadar PS2-style selftest\n",
        "fib(20) = ",
        "fact(10) = ",
        "sum(1..1000) = ",
        "math:\n",
        "sort:\n",
        "strings:\n",
        "float:\n",
    ];

    public static uint StringAddress(int index) => StringTable + (uint)Labels.Take(index).Sum(l => l.Length + 1);

    /// <summary>The program as a little-endian MIPS32 ELF with the R5900 CPU flag, linked at the usual PS2 start address.</summary>
    public static byte[] BuildElf()
    {
        var data = new byte[DataSize];
        var cursor = 0;
        foreach (var label in Labels)
        {
            Encoding.ASCII.GetBytes(label).CopyTo(data, cursor);
            cursor += label.Length + 1;
        }

        Encoding.ASCII.GetBytes(Text).CopyTo(data, (int)(SourceText - DataAddress));

        var program = BuildProgram();
        var builder = new ElfBuilder { Machine = 8, Flags = 0x20924001 };
        builder.WithCode(program.ToBytes(CodeAddress, bigEndian: false), CodeAddress);
        builder.WithData(data, DataAddress, ".data", flags: 6);
        foreach (var name in new[] { "main", "fib", "fact", "sumrec", "math", "sorttest", "strtest", "floattest" })
        {
            builder.Symbols.Add(SymbolSpec.Function(name, program.AddressOf(name, CodeAddress)));
        }

        return builder.Build();
    }

    public static MipsProgram BuildProgram()
    {
        var p = new MipsProgram();

        // ---- main ----
        p.Label("main");
        p.PrintString(StringAddress(0));
        p.PrintString(StringAddress(1)).Addiu(Reg.A0, Reg.Zero, 20).Jal("fib").Nop().PrintInt(Reg.V0);
        p.PrintString(StringAddress(2)).Addiu(Reg.A0, Reg.Zero, 10).Jal("fact").Nop().PrintInt(Reg.V0);
        p.PrintString(StringAddress(3)).Addiu(Reg.A0, Reg.Zero, 1000).Jal("sumrec").Nop().PrintInt(Reg.V0);
        p.PrintString(StringAddress(4)).Jal("math").Nop();
        p.PrintString(StringAddress(5)).Jal("sorttest").Nop();
        p.PrintString(StringAddress(6)).Jal("strtest").Nop();
        p.PrintString(StringAddress(7)).Jal("floattest").Nop();
        p.ExitWith(0);

        // ---- fib(n): naive double recursion with a stack frame ----
        p.Label("fib")
            .Addiu(Reg.Sp, Reg.Sp, -16).Sw(Reg.Ra, 12, Reg.Sp).Sw(Reg.S0, 8, Reg.Sp).Sw(Reg.S1, 4, Reg.Sp)
            .Move(Reg.S0, Reg.A0)
            .Slti(Reg.T0, Reg.S0, 2).Beq(Reg.T0, Reg.Zero, "fib_rec").Nop()
            .Move(Reg.V0, Reg.S0).J("fib_out").Nop()
            .Label("fib_rec")
            .Addiu(Reg.A0, Reg.S0, -1).Jal("fib").Nop().Move(Reg.S1, Reg.V0)
            .Addiu(Reg.A0, Reg.S0, -2).Jal("fib").Nop().Addu(Reg.V0, Reg.V0, Reg.S1)
            .Label("fib_out")
            .Lw(Reg.S1, 4, Reg.Sp).Lw(Reg.S0, 8, Reg.Sp).Lw(Reg.Ra, 12, Reg.Sp).Addiu(Reg.Sp, Reg.Sp, 16).Jr(Reg.Ra).Nop();

        // ---- fact(n): recursion with HI/LO multiply ----
        p.Label("fact")
            .Addiu(Reg.Sp, Reg.Sp, -8).Sw(Reg.Ra, 4, Reg.Sp).Sw(Reg.S0, 0, Reg.Sp)
            .Move(Reg.S0, Reg.A0)
            .Slti(Reg.T0, Reg.S0, 2).Bne(Reg.T0, Reg.Zero, "fact_base").Nop()
            .Addiu(Reg.A0, Reg.S0, -1).Jal("fact").Nop()
            .Mult(Reg.S0, Reg.V0).Mflo(Reg.V0).J("fact_out").Nop()
            .Label("fact_base").Addiu(Reg.V0, Reg.Zero, 1)
            .Label("fact_out")
            .Lw(Reg.S0, 0, Reg.Sp).Lw(Reg.Ra, 4, Reg.Sp).Addiu(Reg.Sp, Reg.Sp, 8).Jr(Reg.Ra).Nop();

        // ---- sumrec(n) = n + sumrec(n - 1): deep recursion ----
        p.Label("sumrec")
            .Addiu(Reg.Sp, Reg.Sp, -8).Sw(Reg.Ra, 4, Reg.Sp).Sw(Reg.S0, 0, Reg.Sp)
            .Move(Reg.S0, Reg.A0)
            .Beq(Reg.S0, Reg.Zero, "sumrec_base").Nop()
            .Addiu(Reg.A0, Reg.S0, -1).Jal("sumrec").Nop().Addu(Reg.V0, Reg.V0, Reg.S0).J("sumrec_out").Nop()
            .Label("sumrec_base").Move(Reg.V0, Reg.Zero)
            .Label("sumrec_out")
            .Lw(Reg.S0, 0, Reg.Sp).Lw(Reg.Ra, 4, Reg.Sp).Addiu(Reg.Sp, Reg.Sp, 8).Jr(Reg.Ra).Nop();

        // ---- math: HI/LO, signed and unsigned division, movz/movn, gcd by repeated division ----
        p.Label("math")
            .Li32(Reg.T0, 123456789).Li32(Reg.T1, 987).Mult(Reg.T0, Reg.T1).Mflo(Reg.T2).PrintInt(Reg.T2)
            .Li32(Reg.T0, 0x7FFFFFFF).Mult(Reg.T0, Reg.T0).Mfhi(Reg.T2).PrintInt(Reg.T2)
            .Li32(Reg.T0, unchecked((uint)-17)).Li32(Reg.T1, 5).Div(Reg.T0, Reg.T1).Mflo(Reg.T2).Mfhi(Reg.T3).PrintInt(Reg.T2).PrintInt(Reg.T3)
            .Li32(Reg.T0, 0xFFFFFFF0).Li32(Reg.T1, 16).Divu(Reg.T0, Reg.T1).Mflo(Reg.T2).PrintInt(Reg.T2)
            .Addiu(Reg.T0, Reg.Zero, 7).Addiu(Reg.T1, Reg.Zero, 9).Addiu(Reg.T2, Reg.Zero, 0).Movz(Reg.T0, Reg.T1, Reg.T2).PrintInt(Reg.T0)
            .Addiu(Reg.T0, Reg.Zero, 7).Movn(Reg.T0, Reg.T1, Reg.T2).PrintInt(Reg.T0)
            .Li32(Reg.T0, 1071).Li32(Reg.T1, 462)
            .Label("gcd_loop")
            .Beq(Reg.T1, Reg.Zero, "gcd_done").Nop()
            .Div(Reg.T0, Reg.T1).Mfhi(Reg.T2).Move(Reg.T0, Reg.T1).Move(Reg.T1, Reg.T2).J("gcd_loop").Nop()
            .Label("gcd_done").PrintInt(Reg.T0)
            .Jr(Reg.Ra).Nop();

        // ---- sorttest: fill 32 values with an LCG, bubble sort, print the first 8, the maximum and a checksum ----
        p.Label("sorttest")
            .Li32(Reg.T0, SortArray).Addiu(Reg.T1, Reg.Zero, 0).Li32(Reg.T2, 12345).Li32(Reg.T3, 1103515245)
            .Label("fill")
            .Sltiu(Reg.T4, Reg.T1, 32).Beq(Reg.T4, Reg.Zero, "fill_done").Nop()
            .Mul(Reg.T2, Reg.T2, Reg.T3).Addiu(Reg.T2, Reg.T2, 12345)
            .Srl(Reg.T4, Reg.T2, 16).Andi(Reg.T4, Reg.T4, 0x7FFF)
            .Sll(Reg.T5, Reg.T1, 2).Addu(Reg.T5, Reg.T5, Reg.T0).Sw(Reg.T4, 0, Reg.T5)
            .Addiu(Reg.T1, Reg.T1, 1).J("fill").Nop()
            .Label("fill_done")
            .Addiu(Reg.T6, Reg.Zero, 31)
            .Label("outer")
            .Blez(Reg.T6, "sorted").Nop()
            .Addiu(Reg.T1, Reg.Zero, 0)
            .Label("inner")
            .Slt(Reg.T4, Reg.T1, Reg.T6).Beq(Reg.T4, Reg.Zero, "inner_done").Nop()
            .Sll(Reg.T5, Reg.T1, 2).Addu(Reg.T5, Reg.T5, Reg.T0)
            .Lw(Reg.T7, 0, Reg.T5).Lw(Reg.T8, 4, Reg.T5)
            .Slt(Reg.T9, Reg.T8, Reg.T7).Beq(Reg.T9, Reg.Zero, "noswap").Nop()
            .Sw(Reg.T8, 0, Reg.T5).Sw(Reg.T7, 4, Reg.T5)
            .Label("noswap").Addiu(Reg.T1, Reg.T1, 1).J("inner").Nop()
            .Label("inner_done").Addiu(Reg.T6, Reg.T6, -1).J("outer").Nop()
            .Label("sorted")
            .Addiu(Reg.T1, Reg.Zero, 0)
            .Label("print_loop")
            .Sltiu(Reg.T4, Reg.T1, 8).Beq(Reg.T4, Reg.Zero, "print_done").Nop()
            .Sll(Reg.T5, Reg.T1, 2).Addu(Reg.T5, Reg.T5, Reg.T0).Lw(Reg.T7, 0, Reg.T5).PrintInt(Reg.T7)
            .Addiu(Reg.T1, Reg.T1, 1).J("print_loop").Nop()
            .Label("print_done")
            .Lw(Reg.T7, 124, Reg.T0).PrintInt(Reg.T7)
            .Addiu(Reg.T1, Reg.Zero, 0).Addiu(Reg.T8, Reg.Zero, 0)
            .Label("checksum")
            .Sltiu(Reg.T4, Reg.T1, 32).Beq(Reg.T4, Reg.Zero, "checksum_done").Nop()
            .Sll(Reg.T5, Reg.T1, 2).Addu(Reg.T5, Reg.T5, Reg.T0).Lw(Reg.T7, 0, Reg.T5).Mul(Reg.T7, Reg.T7, Reg.T1).Addu(Reg.T8, Reg.T8, Reg.T7)
            .Addiu(Reg.T1, Reg.T1, 1).J("checksum").Nop()
            .Label("checksum_done").PrintInt(Reg.T8)
            .Jr(Reg.Ra).Nop();

        // ---- strtest: strlen, byte-wise reverse copy, print the copy, count vowels ----
        p.Label("strtest")
            .Li32(Reg.T0, SourceText).Li32(Reg.T1, ReverseBuffer).Move(Reg.T2, Reg.T0)
            .Label("strlen")
            .Lbu(Reg.T3, 0, Reg.T2).Beq(Reg.T3, Reg.Zero, "strlen_done").Nop().Addiu(Reg.T2, Reg.T2, 1).J("strlen").Nop()
            .Label("strlen_done").Subu(Reg.T4, Reg.T2, Reg.T0).PrintInt(Reg.T4)
            .Addiu(Reg.T5, Reg.Zero, 0)
            .Label("reverse")
            .Slt(Reg.T6, Reg.T5, Reg.T4).Beq(Reg.T6, Reg.Zero, "reverse_done").Nop()
            .Subu(Reg.T6, Reg.T4, Reg.T5).Addiu(Reg.T6, Reg.T6, -1).Addu(Reg.T6, Reg.T6, Reg.T0).Lbu(Reg.T7, 0, Reg.T6)
            .Addu(Reg.T8, Reg.T1, Reg.T5).Sb(Reg.T7, 0, Reg.T8).Addiu(Reg.T5, Reg.T5, 1).J("reverse").Nop()
            .Label("reverse_done")
            .Addu(Reg.T8, Reg.T1, Reg.T4).Sb(Reg.Zero, 0, Reg.T8)
            .Move(Reg.A0, Reg.T1).Addiu(Reg.V0, Reg.Zero, 5).Syscall()
            .Addiu(Reg.A0, Reg.Zero, 10).Addiu(Reg.V0, Reg.Zero, 2).Syscall()
            .Addiu(Reg.T5, Reg.Zero, 0).Move(Reg.T2, Reg.T0)
            .Label("vowels")
            .Lbu(Reg.T3, 0, Reg.T2).Beq(Reg.T3, Reg.Zero, "vowels_done").Nop()
            .Ori(Reg.T3, Reg.T3, 0x20)
            .Addiu(Reg.T6, Reg.Zero, 97).Beq(Reg.T3, Reg.T6, "is_vowel").Nop()
            .Addiu(Reg.T6, Reg.Zero, 101).Beq(Reg.T3, Reg.T6, "is_vowel").Nop()
            .Addiu(Reg.T6, Reg.Zero, 105).Beq(Reg.T3, Reg.T6, "is_vowel").Nop()
            .Addiu(Reg.T6, Reg.Zero, 111).Beq(Reg.T3, Reg.T6, "is_vowel").Nop()
            .Addiu(Reg.T6, Reg.Zero, 117).Beq(Reg.T3, Reg.T6, "is_vowel").Nop()
            .J("vowel_next").Nop()
            .Label("is_vowel").Addiu(Reg.T5, Reg.T5, 1)
            .Label("vowel_next").Addiu(Reg.T2, Reg.T2, 1).J("vowels").Nop()
            .Label("vowels_done").PrintInt(Reg.T5)
            .Jr(Reg.Ra).Nop();

        // ---- floattest: series, sqrt, conversions, compares and branches on the FPU flag, load/store ----
        p.Label("floattest")
            .Li32(Reg.T0, 0x3F800000).Mtc1(Reg.T0, 4)
            .Li32(Reg.T0, 0x40C00000).Mtc1(Reg.T0, 6)
            .Mtc1(Reg.Zero, 0).Addiu(Reg.T1, Reg.Zero, 1)
            .Label("series")
            .Sltiu(Reg.T2, Reg.T1, 51).Beq(Reg.T2, Reg.Zero, "series_done").Nop()
            .Mtc1(Reg.T1, 1).CvtSW(1, 1).MulS(2, 1, 1).DivS(3, 4, 2).AddS(0, 0, 3)
            .Addiu(Reg.T1, Reg.T1, 1).J("series").Nop()
            .Label("series_done")
            .PrintFloat(0)
            .MulS(5, 0, 6).SqrtS(5, 5).PrintFloat(5)
            .TruncWS(7, 5).Mfc1(Reg.T3, 7).PrintInt(Reg.T3)
            .Li32(Reg.T0, 0x40000000).Mtc1(Reg.T0, 8).CLtS(0, 8).Bc1t("less").Nop()
            .Addiu(Reg.T4, Reg.Zero, 0).J("less_done").Nop()
            .Label("less").Addiu(Reg.T4, Reg.Zero, 1)
            .Label("less_done").PrintInt(Reg.T4)
            .NegS(9, 0).AbsS(10, 9).CEqS(10, 0).Bc1f("not_equal").Nop()
            .Addiu(Reg.T4, Reg.Zero, 1).J("equal_done").Nop()
            .Label("not_equal").Addiu(Reg.T4, Reg.Zero, 0)
            .Label("equal_done").PrintInt(Reg.T4)
            .Li32(Reg.T5, FloatBuffer).Swc1(0, 0, Reg.T5).Lwc1(11, 0, Reg.T5).PrintFloat(11)
            .Jr(Reg.Ra).Nop();

        return p;
    }

    /// <summary>The output the program must produce, computed independently in plain C# (not by running any guest code).</summary>
    public static string ExpectedOutput()
    {
        var sb = new StringBuilder();
        void Line(object value) => sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append('\n');
        string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);

        static long Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);

        sb.Append(Labels[0]);
        sb.Append(Labels[1]); Line(Fib(20));
        sb.Append(Labels[2]); Line(Enumerable.Range(1, 10).Aggregate(1L, (a, b) => a * b));
        sb.Append(Labels[3]); Line(Enumerable.Range(1, 1000).Sum());

        sb.Append(Labels[4]);
        Line(unchecked((int)(123456789u * 987u)));
        Line((int)(((long)0x7FFFFFFF * 0x7FFFFFFF) >> 32));
        Line(-17 / 5);
        Line(-17 % 5);
        Line((int)(0xFFFFFFF0u / 16u));
        Line(9);
        Line(7);
        Line(21);

        sb.Append(Labels[5]);
        var values = new uint[32];
        uint x = 12345;
        for (var i = 0; i < 32; i++)
        {
            x = unchecked((x * 1103515245u) + 12345u);
            values[i] = (x >> 16) & 0x7FFF;
        }

        Array.Sort(values);
        for (var i = 0; i < 8; i++)
        {
            Line((int)values[i]);
        }

        Line((int)values[31]);
        uint checksum = 0;
        for (var i = 0; i < 32; i++)
        {
            checksum = unchecked(checksum + (values[i] * (uint)i));
        }

        Line((int)checksum);

        sb.Append(Labels[6]);
        Line(Text.Length);
        sb.Append(new string(Text.Reverse().ToArray())).Append('\n');
        Line(Text.Count(c => "aeiouAEIOU".Contains(c)));

        sb.Append(Labels[7]);
        float sum = 0f;
        for (var n = 1; n <= 50; n++)
        {
            var f = (float)n;
            sum += 1f / (f * f);
        }

        var pi = MathF.Sqrt(sum * 6f);
        sb.Append(F(sum)).Append('\n');
        sb.Append(F(pi)).Append('\n');
        Line((int)MathF.Truncate(pi));
        Line(sum < 2f ? 1 : 0);
        Line(1);
        sb.Append(F(sum)).Append('\n');
        return sb.ToString();
    }
}
