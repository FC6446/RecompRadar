using RecompRadar.Core.Model;
using RecompRadar.Recompilation;
using RecompRadar.Recompilation.Execution;
using RecompRadar.Recompilation.Mips;
using RecompRadar.TestSupport;

namespace RecompRadar.Tests;

/// <summary>Runs a program on the reference interpreter (parse, load, translate, interpret).</summary>
internal static class RefRun
{
    public static ExecutionResult Run(MipsProgram program, bool bigEndian = false, long maxInstructions = 5_000_000) =>
        RunElf(SampleFiles.Mips32Elf(program, bigEndian), maxInstructions);

    public static ExecutionResult RunElf(byte[] elfBytes, long maxInstructions = 5_000_000, RuntimeProfile profile = RuntimeProfile.RecompRadarTest)
    {
        var elf = T.ParseOk(elfBytes);
        var plugin = new MipsPocPlugin();
        using var stream = new MemoryStream(elfBytes);
        var machine = GuestMachine.FromImage(plugin.CreateLoader()!.Load(stream, elf), profile);
        return plugin.CreateBackend()!.Run(machine, plugin.CreateTranslator()!, plugin.CreateRuntime(profile)!, new ExecutionLimits(maxInstructions));
    }

    public static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);
}

public class MipsExtendedIsaTests
{
    private static MipsProgram Finish(MipsProgram p) => p.ExitWith(0);

    private static string Output(MipsProgram p, bool big = false)
    {
        var result = RefRun.Run(Finish(p), big);
        Assert.Equal(ExecutionStatus.Exited, result.Status);
        return result.Output;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Multiply_fills_hi_and_lo_for_signed_and_unsigned_operands(bool big)
    {
        var p = new MipsProgram()
            .Li32(Reg.T0, 0xFFFFFFFE).Li32(Reg.T1, 3)             // -2 * 3 (signed) and 4294967294 * 3 (unsigned)
            .Mult(Reg.T0, Reg.T1).Mfhi(Reg.T2).Mflo(Reg.T3).PrintInt(Reg.T2).PrintInt(Reg.T3)
            .Multu(Reg.T0, Reg.T1).Mfhi(Reg.T2).Mflo(Reg.T3).PrintInt(Reg.T2).PrintInt(Reg.T3)
            .Li32(Reg.T4, 0x12345678).Mthi(Reg.T4).Mtlo(Reg.T4).Mfhi(Reg.T5).Mflo(Reg.T6).PrintInt(Reg.T5).PrintInt(Reg.T6);

        Assert.Equal("-1\n-6\n2\n-6\n305419896\n305419896\n", Output(p, big));
    }

    [Fact]
    public void Divide_gives_quotient_in_lo_and_remainder_in_hi_with_defined_edge_cases()
    {
        var p = new MipsProgram()
            .Li32(Reg.T0, unchecked((uint)-17)).Li32(Reg.T1, 5).Div(Reg.T0, Reg.T1).Mflo(Reg.T2).Mfhi(Reg.T3).PrintInt(Reg.T2).PrintInt(Reg.T3)
            .Li32(Reg.T0, 17).Li32(Reg.T1, unchecked((uint)-5)).Div(Reg.T0, Reg.T1).Mflo(Reg.T2).Mfhi(Reg.T3).PrintInt(Reg.T2).PrintInt(Reg.T3)
            .Li32(Reg.T0, 0xFFFFFFFF).Li32(Reg.T1, 2).Divu(Reg.T0, Reg.T1).Mflo(Reg.T2).Mfhi(Reg.T3).PrintInt(Reg.T2).PrintInt(Reg.T3)
            .Li32(Reg.T0, 0x80000000).Li32(Reg.T1, 0xFFFFFFFF).Div(Reg.T0, Reg.T1).Mflo(Reg.T2).Mfhi(Reg.T3).PrintInt(Reg.T2).PrintInt(Reg.T3)
            .Li32(Reg.T0, 9).Addiu(Reg.T1, Reg.Zero, 0).Div(Reg.T0, Reg.T1).Mflo(Reg.T2).Mfhi(Reg.T3).PrintInt(Reg.T2).PrintInt(Reg.T3)
            .Li32(Reg.T0, unchecked((uint)-9)).Div(Reg.T0, Reg.T1).Mflo(Reg.T2).PrintInt(Reg.T2);

        // -17/5 = -3 r -2; 17/-5 = -3 r 2; 0xFFFFFFFF/2 = 2147483647 r 1; INT_MIN/-1 = INT_MIN r 0;
        // division by zero: quotient -1 (positive dividend) / +1 (negative dividend), remainder = dividend.
        Assert.Equal("-3\n-2\n-3\n2\n2147483647\n1\n-2147483648\n0\n-1\n9\n1\n", Output(p));
    }

    [Fact]
    public void Conditional_moves_and_wrapping_add_sub_behave_like_the_manual_says()
    {
        var p = new MipsProgram()
            .Addiu(Reg.T0, Reg.Zero, 1).Addiu(Reg.T1, Reg.Zero, 2).Addiu(Reg.T2, Reg.Zero, 0)
            .Movz(Reg.T3, Reg.T1, Reg.T2).PrintInt(Reg.T3)        // t2 == 0 -> t3 = 2
            .Movn(Reg.T4, Reg.T1, Reg.T2).PrintInt(Reg.T4)        // t2 == 0 -> t4 untouched (0)
            .Movn(Reg.T5, Reg.T1, Reg.T0).PrintInt(Reg.T5)        // t0 != 0 -> t5 = 2
            .Movz(Reg.T6, Reg.T1, Reg.T0).PrintInt(Reg.T6)        // t0 != 0 -> t6 untouched (0)
            .Li32(Reg.T7, 0x7FFFFFFF).Add(Reg.T8, Reg.T7, Reg.T0).PrintInt(Reg.T8)   // wraps instead of trapping
            .Li32(Reg.T7, 0x80000000).Sub(Reg.T8, Reg.T7, Reg.T0).PrintInt(Reg.T8)
            .Addi(Reg.T9, Reg.Zero, -7).PrintInt(Reg.T9);

        Assert.Equal("2\n0\n2\n0\n-2147483648\n2147483647\n-7\n", Output(p));
    }

    [Fact]
    public void Jalr_and_the_linking_branches_write_the_return_address_and_return_correctly()
    {
        // The callee sits at instruction index 2, right behind the initial jump, so its address is known up front.
        var p = new MipsProgram()
            .J("main").Nop()
            .Label("callee").Addiu(Reg.V1, Reg.Zero, 42).Jr(Reg.Ra).Nop()
            .Label("main")
            .Li32(Reg.T9, (uint)SampleFiles.CodeBase + (4 * 2))
            .Jalr(Reg.Ra, Reg.T9).Nop()                                // call through a register; ra = address after the delay slot
            .PrintInt(Reg.V1).ExitWith(0);                             // after the return: print what the callee left in v1

        Assert.Equal(SampleFiles.CodeBase + (4 * 2), p.AddressOf("callee", SampleFiles.CodeBase));
        var result = RefRun.Run(p);

        Assert.Equal(ExecutionStatus.Exited, result.Status);
        Assert.Equal("42\n", result.Output);
    }

    [Fact]
    public void Bltzal_and_bgezal_branch_on_sign_and_always_link()
    {
        var p = new MipsProgram()
            .Addiu(Reg.T0, Reg.Zero, -1)
            .Bltzal(Reg.T0, "neg").Nop()                    // taken, links
            .PrintInt(Reg.V0)                               // reached after the call returns
            .Bgezal(Reg.T0, "never").Nop()                  // not taken, but still links ra
            .Subu(Reg.T1, Reg.Ra, Reg.Zero).Andi(Reg.T1, Reg.T1, 0xFF).PrintInt(Reg.T1)   // low byte of the new link address
            .ExitWith(0)
            .Label("neg").Addiu(Reg.V0, Reg.Zero, 5).Jr(Reg.Ra).Nop()
            .Label("never").Addiu(Reg.V0, Reg.Zero, 99).Jr(Reg.Ra).Nop();

        // Instruction indices: addiu 0, bltzal 1, nop 2, print 3-5, bgezal 6, nop 7. bgezal links to index 8 = base + 32.
        Assert.Equal("5\n32\n", Output(p));
    }

    [Fact]
    public void Floating_point_arithmetic_matches_ieee_single_precision()
    {
        var p = new MipsProgram()
            .Li32(Reg.T0, RefRun.Bits(7.5f)).Mtc1(Reg.T0, 1)
            .Li32(Reg.T0, RefRun.Bits(2.0f)).Mtc1(Reg.T0, 2)
            .AddS(3, 1, 2).PrintFloat(3)
            .SubS(3, 1, 2).PrintFloat(3)
            .MulS(3, 1, 2).PrintFloat(3)
            .DivS(3, 1, 2).PrintFloat(3)
            .SqrtS(3, 2).PrintFloat(3)
            .NegS(3, 1).PrintFloat(3)
            .AbsS(4, 3).PrintFloat(4)
            .MovS(5, 1).PrintFloat(5)
            .Li32(Reg.T0, RefRun.Bits(1f)).Mtc1(Reg.T0, 6).Li32(Reg.T0, RefRun.Bits(3f)).Mtc1(Reg.T0, 7).DivS(8, 6, 7).PrintFloat(8);

        Assert.Equal(
            $"9.5\n5.5\n15\n3.75\n{MathF.Sqrt(2f).ToString("R", System.Globalization.CultureInfo.InvariantCulture)}\n-7.5\n7.5\n7.5\n{(1f / 3f).ToString("R", System.Globalization.CultureInfo.InvariantCulture)}\n",
            Output(p));
    }

    [Fact]
    public void Float_integer_conversions_round_truncate_and_saturate_as_mips_specifies()
    {
        MipsProgram ConvertCase(MipsProgram p, float value, bool truncate) =>
            p.Li32(Reg.T0, RefRun.Bits(value)).Mtc1(Reg.T0, 1)
                .Then(truncate ? (q => q.TruncWS(2, 1)) : (q => q.CvtWS(2, 1)))
                .Mfc1(Reg.T1, 2).PrintInt(Reg.T1);

        var p = new MipsProgram();
        p = ConvertCase(p, 2.5f, truncate: false);     // round to nearest even -> 2
        p = ConvertCase(p, 3.5f, truncate: false);     // -> 4
        p = ConvertCase(p, -2.7f, truncate: true);     // toward zero -> -2
        p = ConvertCase(p, -2.5f, truncate: false);    // -> -2
        p = ConvertCase(p, 1e10f, truncate: true);     // overflow -> 0x7FFFFFFF
        p = ConvertCase(p, -1e10f, truncate: true);    // overflow -> 0x80000000
        p = ConvertCase(p, float.NaN, truncate: true); // NaN -> 0x7FFFFFFF
        p = p.Li32(Reg.T0, unchecked((uint)-5)).Mtc1(Reg.T0, 3).CvtSW(4, 3).PrintFloat(4);   // int -> float

        Assert.Equal("2\n4\n-2\n-2\n2147483647\n-2147483648\n2147483647\n-5\n", Output(p));
    }

    [Fact]
    public void Float_compares_set_the_flag_that_bc1t_and_bc1f_branch_on()
    {
        var p = new MipsProgram()
            .Li32(Reg.T0, RefRun.Bits(1.5f)).Mtc1(Reg.T0, 1).Li32(Reg.T0, RefRun.Bits(2.5f)).Mtc1(Reg.T0, 2)
            .CLtS(1, 2).Bc1t("a").Nop().Addiu(Reg.T1, Reg.Zero, 0).J("a_done").Nop().Label("a").Addiu(Reg.T1, Reg.Zero, 1).Label("a_done").PrintInt(Reg.T1)     // 1.5 < 2.5 -> 1
            .CLtS(2, 1).Bc1t("b").Nop().Addiu(Reg.T1, Reg.Zero, 0).J("b_done").Nop().Label("b").Addiu(Reg.T1, Reg.Zero, 1).Label("b_done").PrintInt(Reg.T1)     // 2.5 < 1.5 -> 0
            .CEqS(1, 1).Bc1f("c").Nop().Addiu(Reg.T1, Reg.Zero, 1).J("c_done").Nop().Label("c").Addiu(Reg.T1, Reg.Zero, 0).Label("c_done").PrintInt(Reg.T1)     // equal -> 1
            .CLeS(2, 1).Bc1f("d").Nop().Addiu(Reg.T1, Reg.Zero, 0).J("d_done").Nop().Label("d").Addiu(Reg.T1, Reg.Zero, 1).Label("d_done").PrintInt(Reg.T1)     // 2.5 <= 1.5 false -> bc1f taken -> 1
            .CLeS(1, 1).Bc1t("e").Nop().Addiu(Reg.T1, Reg.Zero, 0).J("e_done").Nop().Label("e").Addiu(Reg.T1, Reg.Zero, 1).Label("e_done").PrintInt(Reg.T1);    // equal -> <= true -> 1

        Assert.Equal("1\n0\n1\n1\n1\n", Output(p));
    }

    [Fact]
    public void Float_registers_move_bit_exactly_and_load_and_store_through_memory()
    {
        var p = new MipsProgram()
            .Li32(Reg.T0, 0x40490FDB).Mtc1(Reg.T0, 1)                       // pi as raw bits
            .Mfc1(Reg.T1, 1).PrintInt(Reg.T1)
            .Addiu(Reg.Sp, Reg.Sp, -16).Swc1(1, 4, Reg.Sp).Lw(Reg.T2, 4, Reg.Sp).PrintInt(Reg.T2)     // swc1 stores the raw bits
            .Li32(Reg.T3, 0x3FC00000).Sw(Reg.T3, 8, Reg.Sp).Lwc1(12, 8, Reg.Sp)                          // lwc1 loads 1.5f
            .Addiu(Reg.V0, Reg.Zero, 4).Syscall();

        Assert.Equal("1078530011\n1078530011\n1.5\n", Output(p));
    }

    [Fact]
    public void Unsupported_but_valid_instructions_are_reported_by_name()
    {
        var branchLikely = new MipsProgram().Label("l").Beql(Reg.Zero, Reg.Zero, "l").Nop();
        var withBreak = new MipsProgram().Break();

        var likely = RefRun.Run(branchLikely);
        var broke = RefRun.Run(withBreak);

        Assert.Equal(ExecutionStatus.Stopped, likely.Status);
        Assert.Contains("beql", likely.Message);
        Assert.Contains("branch-likely", likely.Message);
        Assert.Equal(ExecutionStatus.Stopped, broke.Status);
        Assert.Contains("break", broke.Message);
    }

    [Fact]
    public void Cache_pref_and_sync_are_accepted_as_no_operations()
    {
        var p = new MipsProgram().Word(0xBC000000).Word(0xCC000000).Word(0x0000000F).Addiu(Reg.T0, Reg.Zero, 8).PrintInt(Reg.T0);

        Assert.Equal("8\n", Output(p));
    }
}

public class Ps2StyleSelfTestTests
{
    [Fact]
    public void The_program_prints_exactly_what_the_independent_csharp_oracle_computes()
    {
        var result = RefRun.RunElf(Ps2StyleSelfTest.BuildElf());

        Assert.Equal(ExecutionStatus.Exited, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Ps2StyleSelfTest.ExpectedOutput(), result.Output);
        Assert.True(result.InstructionsExecuted > 20_000, $"Only {result.InstructionsExecuted} instructions ran.");
    }

    [Fact]
    public void The_program_is_a_valid_elf_with_the_r5900_flag_and_function_symbols()
    {
        var elf = T.ParseOk(Ps2StyleSelfTest.BuildElf());

        Assert.Equal(0x20924001u, elf.Header.Flags);
        Assert.Equal(Ps2StyleSelfTest.CodeAddress, elf.Header.Entry);
        Assert.Contains(elf.Symbols, s => s.Name == "sorttest" && s.IsFunction);
        Assert.Equal(8, elf.Symbols.Count(s => s.IsFunction));
    }
}

internal static class MipsProgramExtensions
{
    /// <summary>Applies an operation so conditional instruction choices can stay inside one fluent chain.</summary>
    public static MipsProgram Then(this MipsProgram program, Func<MipsProgram, MipsProgram> next) => next(program);
}
