using RecompRadar.Core.Model;
using RecompRadar.Recompilation;
using RecompRadar.Recompilation.Analysis;
using RecompRadar.Recompilation.Build;
using RecompRadar.Recompilation.Execution;
using RecompRadar.Recompilation.IR;
using RecompRadar.Recompilation.Mips;
using RecompRadar.TestSupport;

namespace RecompRadar.Tests;

/// <summary>The IR and the MIPS plugin work with 64-bit registers; 32-bit instructions sign-extend their results.</summary>
public class Int64Tests
{
    private sealed class ArrayMemory : IMemoryReader
    {
        private readonly byte[] _bytes;
        private readonly ulong _base;

        public ArrayMemory(byte[] bytes, ulong baseAddress)
        {
            _bytes = bytes;
            _base = baseAddress;
        }

        public bool TryRead(ulong address, Span<byte> buffer)
        {
            if (address < _base || address - _base + (ulong)buffer.Length > (ulong)_bytes.Length)
            {
                return false;
            }

            _bytes.AsSpan((int)(address - _base), buffer.Length).CopyTo(buffer);
            return true;
        }
    }

    private static IrBlock Translate(MipsProgram program)
    {
        var bytes = program.ToBytes(0x1000, bigEndian: false);
        return new MipsTranslator().TranslateBlock(new ArrayMemory(bytes, 0x1000), Endianness.Little, 0x1000);
    }

    private static IrOp OpOf(MipsProgram program) => Translate(program).Instructions[0].Op;

    private static string Run(MipsProgram program, RuntimeProfile profile = RuntimeProfile.RecompRadarTest, bool bigEndian = false)
    {
        var result = RefRun.RunElf(SampleFiles.Mips32Elf(program.ExitWith(0), bigEndian), 1_000_000, profile);
        Assert.True(result.Status == ExecutionStatus.Exited, result.Message);
        return result.Output;
    }

    [Fact]
    public void Thirty_two_bit_instructions_use_the_sign_extending_operations_and_sixty_four_bit_ones_do_not()
    {
        Assert.Equal(IrOp.AddW, OpOf(new MipsProgram().Addu(Reg.T0, Reg.T1, Reg.T2)));
        Assert.Equal(IrOp.AddW, OpOf(new MipsProgram().Addiu(Reg.T0, Reg.T1, 5)));
        Assert.Equal(IrOp.SubW, OpOf(new MipsProgram().Subu(Reg.T0, Reg.T1, Reg.T2)));
        Assert.Equal(IrOp.ShlW, OpOf(new MipsProgram().Sll(Reg.T0, Reg.T1, 3)));
        Assert.Equal(IrOp.ShrW, OpOf(new MipsProgram().Srl(Reg.T0, Reg.T1, 3)));
        Assert.Equal(IrOp.SarW, OpOf(new MipsProgram().Sra(Reg.T0, Reg.T1, 3)));
        Assert.Equal(IrOp.Add, OpOf(new MipsProgram().Daddu(Reg.T0, Reg.T1, Reg.T2)));
        Assert.Equal(IrOp.Add, OpOf(new MipsProgram().Daddiu(Reg.T0, Reg.T1, 5)));
        Assert.Equal(IrOp.Sub, OpOf(new MipsProgram().Dsubu(Reg.T0, Reg.T1, Reg.T2)));
        Assert.Equal(IrOp.Shl, OpOf(new MipsProgram().Dsll(Reg.T0, Reg.T1, 3)));
        Assert.Equal(IrOp.Shr, OpOf(new MipsProgram().Dsrl(Reg.T0, Reg.T1, 3)));
        Assert.Equal(IrOp.Sar, OpOf(new MipsProgram().Dsra(Reg.T0, Reg.T1, 3)));
        Assert.Equal(IrOp.LoadS32, OpOf(new MipsProgram().Lw(Reg.T0, 0, Reg.Sp)));
        Assert.Equal(IrOp.LoadU32, OpOf(new MipsProgram().Lwu(Reg.T0, 0, Reg.Sp)));
        Assert.Equal(IrOp.Load64, OpOf(new MipsProgram().Ld(Reg.T0, 0, Reg.Sp)));
        Assert.Equal(IrOp.Store64, OpOf(new MipsProgram().Sd(Reg.T0, 0, Reg.Sp)));
    }

    [Fact]
    public void The_thirty_two_extra_shift_forms_add_32_to_the_count()
    {
        var block = Translate(new MipsProgram().Dsll32(Reg.T0, Reg.T1, 4).Dsrl32(Reg.T0, Reg.T1, 5).Dsra32(Reg.T0, Reg.T1, 6));

        Assert.Equal(new ulong[] { 36, 37, 38 }, block.Instructions.Select(i => i.B.Value).ToArray());
        Assert.Equal(new[] { IrOp.Shl, IrOp.Shr, IrOp.Sar }, block.Instructions.Select(i => i.Op).ToArray());
    }

    [Fact]
    public void Immediates_are_sign_extended_to_64_bits_where_the_architecture_says_so()
    {
        var block = Translate(new MipsProgram().Addiu(Reg.T0, Reg.Zero, -1).Ori(Reg.T0, Reg.Zero, 0xFFFF).Lui(Reg.T1, 0x8000).Sltiu(Reg.T2, Reg.T0, -1));

        Assert.Equal(0xFFFFFFFFFFFFFFFFUL, block.Instructions[0].B.Value);        // addiu: sign-extended
        Assert.Equal(0xFFFFUL, block.Instructions[1].B.Value);                    // ori: zero-extended
        Assert.Equal(0xFFFFFFFF80000000UL, block.Instructions[2].A.Value);       // lui: the 32-bit result, sign-extended
        Assert.Equal(0xFFFFFFFFFFFFFFFFUL, block.Instructions[3].B.Value);        // sltiu compares with the sign-extended immediate
    }

    [Fact]
    public void The_self_test_prints_what_the_instruction_definitions_require_in_both_profiles()
    {
        var elf = Int64SelfTest.BuildElf();

        var flat = RefRun.RunElf(elf, 1_000_000);
        var ps2 = RefRun.RunElf(elf, 1_000_000, RuntimeProfile.Ps2Ee);

        Assert.Equal(ExecutionStatus.Exited, flat.Status);
        Assert.Equal(Int64SelfTest.ExpectedOutput(), flat.Output);
        Assert.Equal(ExecutionStatus.Exited, ps2.Status);
        Assert.Equal(Int64SelfTest.ExpectedOutput(), ps2.Output);
        for (var i = 0; i < Regs.TempBase; i++)
        {
            if (i != Reg.Sp)                                                           // the stack pointers differ by profile
            {
                Assert.Equal(flat.Registers[i], ps2.Registers[i]);
            }
        }
    }

    [Fact]
    public void The_whole_register_is_kept_not_just_its_low_half()
    {
        var result = RefRun.Run(new MipsProgram().Li64(Reg.S0, 0x1122334455667788UL).Daddiu(Reg.S1, Reg.S0, 1).ExitWith(0));

        Assert.Equal(0x1122334455667788UL, result.Registers[Reg.S0]);
        Assert.Equal(0x1122334455667789UL, result.Registers[Reg.S1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Doubleword_stores_and_loads_follow_the_byte_order_of_the_program(bool big)
    {
        var p = new MipsProgram()
            .Li64(Reg.T0, 0x0102030405060708UL)
            .Addiu(Reg.Sp, Reg.Sp, -16).Sd(Reg.T0, 0, Reg.Sp)
            .Lw(Reg.T1, 0, Reg.Sp).Lw(Reg.T2, 4, Reg.Sp).Ld(Reg.T3, 0, Reg.Sp)
            .PrintInt(Reg.T1).PrintInt(Reg.T2).Print64(Reg.T3);

        var output = Run(p, bigEndian: big);

        var first = big ? 0x01020304 : 0x05060708;
        var second = big ? 0x05060708 : 0x01020304;
        Assert.Equal($"{first}\n{second}\n16909060\n84281096\n", output);
    }

    [Fact]
    public void A_misaligned_doubleword_access_faults_with_the_address()
    {
        var read = RefRun.Run(new MipsProgram().Addiu(Reg.Sp, Reg.Sp, -16).Ld(Reg.T0, 4, Reg.Sp).ExitWith(0));
        var write = RefRun.Run(new MipsProgram().Addiu(Reg.Sp, Reg.Sp, -16).Sd(Reg.T0, 2, Reg.Sp).ExitWith(0));

        Assert.Equal(ExecutionStatus.Faulted, read.Status);
        Assert.Contains("Unaligned 8-byte read at 0x7FFEFFE4", read.Message, StringComparison.Ordinal);
        Assert.Contains("Unaligned 8-byte write at 0x7FFEFFE2", write.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_doubleword_access_next_to_the_end_of_memory_does_not_wrap()
    {
        var p = new MipsProgram().Li32(Reg.T0, 0x7FFFFFF8).Ld(Reg.T1, 0, Reg.T0).ExitWith(0);

        var result = RefRun.Run(p);

        Assert.Equal(ExecutionStatus.Faulted, result.Status);
        Assert.Contains("Read of 8 byte(s) at unmapped address 0x7FFFFFF8", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dmult", "64-bit multiply and divide")]
    [InlineData("lq", "128-bit loads and stores")]
    [InlineData("ldl", "unaligned loads and stores")]
    [InlineData("lwl", "unaligned loads and stores")]
    public void Recognised_but_untranslated_instructions_are_refused_by_name_with_the_reason(string mnemonic, string reason)
    {
        var p = mnemonic switch
        {
            "dmult" => new MipsProgram().Dmult(Reg.T0, Reg.T1),
            "lq" => new MipsProgram().Lq(Reg.T0, 0, Reg.Sp),
            "ldl" => new MipsProgram().Ldl(Reg.T0, 0, Reg.Sp),
            _ => new MipsProgram().Lwl(Reg.T0, 0, Reg.Sp),
        };

        var result = RefRun.Run(p.ExitWith(0));

        Assert.Equal(ExecutionStatus.Stopped, result.Status);
        Assert.Contains($"({mnemonic})", result.Message, StringComparison.Ordinal);
        Assert.Contains(reason, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_decoder_names_the_new_instructions_readably()
    {
        var p = new MipsProgram().Daddu(Reg.T0, Reg.T1, Reg.T2).Dsll32(Reg.T0, Reg.T1, 4).Ld(Reg.T0, 8, Reg.Sp).Sd(Reg.T0, -8, Reg.Sp).Daddiu(Reg.T0, Reg.T1, -3);
        var memory = new ArrayMemory(p.ToBytes(0x1000, false), 0x1000);
        var decoder = new MipsPocPlugin().CreateDecoder()!;

        var text = Enumerable.Range(0, 5).Select(i =>
        {
            var d = decoder.Decode(memory, Endianness.Little, 0x1000 + (ulong)(i * 4))!;
            return $"{d.Mnemonic} {d.Operands}";
        }).ToArray();

        Assert.Equal(new[] { "daddu $t0, $t1, $t2", "dsll32 $t0, $t1, 4", "ld $t0, 8($sp)", "sd $t0, -8($sp)", "daddiu $t0, $t1, -3" }, text);
    }

    [Fact]
    public void Constant_propagation_follows_64_bit_values_and_jumps_use_the_low_half()
    {
        var target = SampleFiles.CodeBase + 0x40;
        var p = new MipsProgram().Li64(Reg.T0, 0xDEAD000000000000UL | target).Jr(Reg.T0).Nop().ExitWith(1);
        while (p.ToBytes(SampleFiles.CodeBase, false).Length < 0x40)
        {
            p.Nop();
        }

        p.PrintInt(Reg.Sp).ExitWith(0);

        var graph = Analyze.Program(p);

        var resolved = Assert.Single(graph.ResolvedIndirectJumps);
        Assert.Equal(target, resolved.Target);
        Assert.Empty(graph.IndirectJumps);
    }

    [Fact]
    public void System_call_numbers_ignore_the_upper_half_of_the_register()
    {
        var p = new MipsProgram().Li64(Reg.V1, 0xFFFFFFFF00000020UL).Syscall();

        var graph = Analyze.Program(p);

        var site = Assert.Single(graph.SyscallSites);
        Assert.Equal(SyscallAbi.Ps2Kernel, site.Abi);
        Assert.Equal(0x20u, site.Number);
    }
}

public class Int64EndToEndTests
{
    [RequiresDotnetSdkTheory]
    [InlineData(null, "RecompRadarTest")]
    [InlineData("ps2", "Ps2Ee")]
    public async Task The_built_program_matches_the_oracle_and_the_interpreter_in_both_profiles(string? platform, string profile)
    {
        var result = await PipelineTestHelper.RunAsync(Int64SelfTest.BuildElf(), "int64_" + (platform ?? "flat"), platform: platform);

        Assert.True(result.Success, result.FailureReason + string.Join("; ", result.Validation?.Differences ?? []));
        Assert.Equal(profile, result.Compatibility!.Profile.ToString());
        Assert.True(result.Validation!.Passed, string.Join("; ", result.Validation.Differences));
        Assert.Equal(Int64SelfTest.ExpectedOutput(), result.Validation.Reference.Output);

        var run = await ProgramLauncher.RunAsync(result.ExecutablePath!);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Int64SelfTest.ExpectedOutput(), run.StandardOutput.ReplaceLineEndings("\n"));
    }

    [RequiresDotnetSdkFact]
    public async Task All_64_bits_of_the_registers_are_compared_between_the_built_program_and_the_interpreter()
    {
        var elf = SampleFiles.Mips32Elf(new MipsProgram().Li64(Reg.S0, 0x1122334455667788UL).Li64(Reg.S1, 0x8000000000000000UL).ExitWith(0), bigEndian: false);

        var built = await NativeTestHelper.BuildAsync(elf, "int64_registers");
        var run = await ProgramLauncher.RunAsync(built.Build.ExecutablePath!);

        Assert.Equal(0x1122334455667788UL, run.Report!.Registers[Reg.S0]);
        Assert.Equal(0x8000000000000000UL, run.Report.Registers[Reg.S1]);
        Assert.True(DifferentialValidator.Compare(NativeTestHelper.Reference(elf), run.Report).Passed);
    }
}
