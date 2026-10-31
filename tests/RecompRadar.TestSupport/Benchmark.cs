namespace RecompRadar.TestSupport;

/// <summary>
/// A compute-bound, self-made MIPS program for timing: recursive Fibonacci (calls, stack frames, branches) followed by a
/// multiply/accumulate loop (HI/LO). <see cref="ExpectedOutput"/> is computed independently in C#.
/// </summary>
public static class Ps2StyleBenchmark
{
    public const int FibN = 30;
    public const int LoopCount = 3_000_000;
    private const ulong CodeAddress = 0x00100000;

    public static byte[] BuildElf()
    {
        var p = new MipsProgram();
        p.Label("main")
            .Addiu(Reg.A0, Reg.Zero, FibN).Jal("fib").Nop().PrintInt(Reg.V0)
            .Li32(Reg.T0, 0).Li32(Reg.T1, 1).Li32(Reg.T2, LoopCount).Li32(Reg.T3, 31)
            .Label("loop")
            .Mult(Reg.T1, Reg.T3).Mflo(Reg.T1).Addu(Reg.T1, Reg.T1, Reg.T0)
            .Addiu(Reg.T0, Reg.T0, 1).Bne(Reg.T0, Reg.T2, "loop").Nop()
            .PrintInt(Reg.T1)
            .ExitWith(0);

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

        var builder = new ElfBuilder { Machine = 8, Flags = 0x20924001 };
        builder.WithCode(p.ToBytes(CodeAddress, bigEndian: false), CodeAddress);
        builder.Symbols.Add(SymbolSpec.Function("main", p.AddressOf("main", CodeAddress)));
        builder.Symbols.Add(SymbolSpec.Function("fib", p.AddressOf("fib", CodeAddress)));
        return builder.Build();
    }

    public static string ExpectedOutput()
    {
        long a = 0, b = 1;
        for (var i = 0; i < FibN; i++)
        {
            (a, b) = (b, a + b);
        }

        var acc = 1u;
        for (uint i = 0; i < LoopCount; i++)
        {
            acc = unchecked(acc * 31 + i);
        }

        return $"{a}\n{unchecked((int)acc)}\n";
    }
}
