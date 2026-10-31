using System.Text.Json;
using RecompRadar.Core.Model;
using RecompRadar.Recompilation;
using RecompRadar.Recompilation.Analysis;
using RecompRadar.Recompilation.Build;
using RecompRadar.Recompilation.Compatibility;
using RecompRadar.Recompilation.Generation;
using RecompRadar.TestSupport;

namespace RecompRadar.Tests;

internal static class Ps2Elf
{
    public const ulong Code = 0x00100000;
    public const uint Data = 0x00300000;

    /// <summary>A little-endian MIPS32 ELF with the R5900 flag, code at 0x00100000 and a 4 KiB writable data segment at 0x00300000.</summary>
    public static byte[] Build(MipsProgram program, params string[] functionSymbols)
    {
        var builder = new ElfBuilder { Machine = 8, Flags = 0x20924001 };
        builder.WithCode(program.ToBytes(Code, bigEndian: false), Code);
        builder.WithData(new byte[0x1000], Data, ".data", flags: 6);
        foreach (var name in functionSymbols)
        {
            builder.Symbols.Add(SymbolSpec.Function(name, program.AddressOf(name, Code)));
        }

        return builder.Build();
    }

    /// <summary>The analysis input exactly as the pipeline builds it.</summary>
    public static ProgramGraph Analyze(byte[] elfBytes)
    {
        var elf = T.ParseOk(elfBytes);
        var plugin = new RecompRadar.Recompilation.Mips.MipsPocPlugin();
        using var stream = new MemoryStream(elfBytes);
        var image = plugin.CreateLoader()!.Load(stream, elf);
        return ProgramAnalyzer.Analyze(AnalysisInputFactory.FromImage(image, elf, plugin.CreateTranslator()!, plugin.NoReturnSystemCalls));
    }
}

public class Ps2AnalysisAndCompatibilityTests
{
    private static CompatibilityAssessment Assess(byte[] elfBytes, string? platform = "ps2") =>
        CompatibilityEngine.Assess(new CompatibilityInput(T.ParseOk(elfBytes), null, platform, Ps2Elf.Analyze(elfBytes)));

    [Fact]
    public void Kernel_calls_are_recognised_by_a_constant_in_v1_and_test_calls_by_v0()
    {
        var p = new MipsProgram().Ee(0x20).PrintIntEe(Reg.T0).Addiu(Reg.V0, Reg.Zero, 5).Syscall().Ee(4);
        var graph = Ps2Elf.Analyze(Ps2Elf.Build(p));

        Assert.Equal(
            new[] { (SyscallAbi.Ps2Kernel, (uint?)0x20), (SyscallAbi.RecompRadarTest, 3u), (SyscallAbi.RecompRadarTest, 5u), (SyscallAbi.Ps2Kernel, 4u) },
            graph.SyscallSites.Select(s => (s.Abi, s.Number)).ToArray());
    }

    [Fact]
    public void Code_behind_exit_and_exit_thread_is_not_treated_as_reachable()
    {
        var p = new MipsProgram().Ee(Ps2KernelNumbers.KExit).Nop().Nop().Nop().Nop();
        var graph = Ps2Elf.Analyze(Ps2Elf.Build(p));

        Assert.Equal(graph.ExecutableBytes - 16, graph.ReachableBytes);

        var thread = new MipsProgram().Ee(Ps2KernelNumbers.ExitThread).Nop().Nop();
        Assert.True(Ps2Elf.Analyze(Ps2Elf.Build(thread)).ReachableBytes < 24);
    }

    [Fact]
    public void The_kernel_self_test_is_experimental_with_the_ps2_runtime_profile()
    {
        var elf = Ps2KernelSelfTest.BuildElf();

        var a = Assess(elf);

        Assert.Equal(CompatibilityLevel.Experimental, a.Level);
        Assert.Equal(RuntimeProfile.Ps2Ee, a.Profile);
        Assert.Equal(ConsoleBackendRegistry.Ps2, a.Backend!.Id);
        Assert.Contains(a.Notes, n => n.Kind == NoteKind.Info && n.Text.Contains("kernel call site(s) use implemented calls", StringComparison.Ordinal));
        Assert.Contains(a.Notes, n => n.Kind == NoteKind.Info && n.Text.Contains("cannot run", StringComparison.Ordinal));
        Assert.Null(a.RunBlockedReason);
    }

    [Fact]
    public void Kernel_calls_without_the_ps2_target_are_refused_with_the_instruction_to_choose_it()
    {
        var a = Assess(Ps2KernelSelfTest.BuildElf(), platform: null);

        Assert.Equal(CompatibilityLevel.Unsupported, a.Level);
        Assert.Equal(RuntimeProfile.RecompRadarTest, a.Profile);
        Assert.Contains("Choose PlayStation 2", a.RecompileBlockedReason, StringComparison.Ordinal);
        Assert.Contains("CreateSema (0x40)", a.RecompileBlockedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_kernel_call_that_is_not_implemented_is_named_with_its_number()
    {
        var elf = Ps2Elf.Build(new MipsProgram().Ee(Ps2KernelNumbers.SetupThread).Ee(Ps2KernelNumbers.KExit));

        var a = Assess(elf);

        Assert.Equal(CompatibilityLevel.Unsupported, a.Level);
        Assert.Contains("SetupThread (0x3C)", a.RunBlockedReason, StringComparison.Ordinal);
        Assert.Contains("not implemented", a.RunBlockedReason, StringComparison.Ordinal);
        Assert.True(a.CanRecompileIncomplete);
        Assert.False(a.CanRecompile);
    }

    [Fact]
    public void An_unknown_kernel_call_number_is_reported_as_unknown()
    {
        var a = Assess(Ps2Elf.Build(new MipsProgram().Ee(0x7F).Ee(Ps2KernelNumbers.KExit)));

        Assert.Equal(CompatibilityLevel.Unsupported, a.Level);
        Assert.Contains("unknown call 0x7F", a.RunBlockedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_program_that_does_not_fit_the_32_mebibytes_of_ram_is_refused()
    {
        var builder = new ElfBuilder { Machine = 8, Flags = 0x20924001 };
        builder.WithCode(new MipsProgram().Ee(4).ToBytes(0x40000000, false), 0x40000000);

        var a = CompatibilityEngine.Assess(new CompatibilityInput(T.ParseOk(builder.Build()), null, "ps2"));

        Assert.Equal(CompatibilityLevel.Unsupported, a.Level);
        Assert.Contains("32 MiB of EE RAM", a.RecompileBlockedReason, StringComparison.Ordinal);
        Assert.False(a.CanRecompileIncomplete);
    }

    [Fact]
    public void The_generic_backend_keeps_the_test_profile()
    {
        var bytes = SampleFiles.Mips32Elf(new MipsProgram().PrintInt(Reg.T0).ExitWith(0), bigEndian: false);

        var a = CompatibilityEngine.Assess(new CompatibilityInput(T.ParseOk(bytes), null, null, Analyze.Elf(bytes)));

        Assert.Equal(RuntimeProfile.RecompRadarTest, a.Profile);
        Assert.Equal(CompatibilityLevel.Experimental, a.Level);
    }
}

/// <summary>Kernel call numbers as the tests write them in assembly (the values are pinned against ps2sdk in <c>Ps2KernelTests</c>).</summary>
internal static class Ps2KernelNumbers
{
    public const int KExit = 4;
    public const int ExitThread = 0x23;
    public const int SetupThread = 0x3C;
}

/// <summary>The PlayStation 2 profile end to end: real builds compared with the reference interpreter.</summary>
public class Ps2PipelineEndToEndTests
{
    [RequiresDotnetSdkFact]
    public async Task The_kernel_self_test_builds_runs_and_matches_the_hand_derived_output_and_the_interpreter()
    {
        var result = await PipelineTestHelper.RunAsync(Ps2KernelSelfTest.BuildElf(), "ps2_kernel_selftest", platform: "ps2");

        Assert.True(result.Success, result.FailureReason + string.Join("; ", result.Validation?.Differences ?? []));
        Assert.Equal(RuntimeProfile.Ps2Ee, result.Compatibility!.Profile);
        Assert.True(result.Validation!.Passed, string.Join("; ", result.Validation.Differences));
        Assert.Equal(Ps2KernelSelfTest.ExpectedOutput(), result.Validation.Reference.Output);
        Assert.True(result.CanPlay, result.PlayBlockedReason);

        var run = await ProgramLauncher.RunAsync(result.ExecutablePath!);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Ps2KernelSelfTest.ExpectedOutput(), run.StandardOutput.ReplaceLineEndings("\n"));
        Assert.Equal(string.Empty, run.StandardError);

        using var report = JsonDocument.Parse(await File.ReadAllBytesAsync(result.ReportPath!));
        Assert.Equal("Ps2Ee", report.RootElement.GetProperty("compatibility").GetProperty("runtimeProfile").GetString());
    }

    private static MipsProgram ThreadReturnProgram(uint threadEntry)
    {
        // main starts a thread of higher priority; the thread prints 'T' and simply returns (into the kernel's exit address);
        // main then prints 'M' and ends the program.
        var p = new MipsProgram();
        p.Label("main");
        p.Li32(Reg.T0, Ps2Elf.Data + 0x100).Sw(Reg.Zero, 0x10, Reg.T0).Sw(Reg.Zero, 0x14, Reg.T0);                      // gp 0, priority 0
        p.Li32(Reg.T1, threadEntry).Sw(Reg.T1, 4, Reg.T0);
        p.Li32(Reg.T1, Ps2Elf.Data + 0xC00).Sw(Reg.T1, 8, Reg.T0).Li32(Reg.T1, 0x400).Sw(Reg.T1, 0xC, Reg.T0);
        p.Move(Reg.A0, Reg.T0).Ee(0x20).Move(Reg.S0, Reg.V0);
        p.Move(Reg.A0, Reg.S0).Addiu(Reg.A1, Reg.Zero, 0).Ee(0x22);
        p.Addiu(Reg.A0, Reg.Zero, 'M').Jal("putc").Nop();
        p.Move(Reg.A0, Reg.Zero).Ee(4);
        p.Label("thread").Move(Reg.S1, Reg.Ra).Addiu(Reg.A0, Reg.Zero, 'T').Jal("putc").Nop().Move(Reg.Ra, Reg.S1).Jr(Reg.Ra).Nop();   // keep the return address across the call
        p.Label("putc").Li32(Reg.T0, 0xB000F180).Sb(Reg.A0, 0, Reg.T0).Jr(Reg.Ra).Nop();
        return p;
    }

    [RequiresDotnetSdkFact]
    public async Task A_thread_function_that_returns_ends_its_thread_in_the_built_program_too()
    {
        var entry = (uint)ThreadReturnProgram(0).AddressOf("thread", Ps2Elf.Code);       // layout does not depend on the value
        var program = ThreadReturnProgram(entry);

        var result = await PipelineTestHelper.RunAsync(Ps2Elf.Build(program, "main", "thread", "putc"), "ps2_thread_return", platform: "ps2");

        Assert.True(result.Success, result.FailureReason + string.Join("; ", result.Validation?.Differences ?? []));
        Assert.Equal("TM", result.Validation!.Reference.Output);
        Assert.Equal(ExecutionStatus.Exited, result.Validation.Reference.Status);
        var run = await ProgramLauncher.RunAsync(result.ExecutablePath!);
        Assert.Equal("TM\n", run.StandardOutput.ReplaceLineEndings("\n"));       // the launcher reports output line by line
        Assert.Equal(0, run.ExitCode);
    }

    [RequiresDotnetSdkTheory]
    [InlineData("io_register", "I/O register 0x1000F000")]
    [InlineData("outside_ram", "unmapped address 0x12340000")]
    [InlineData("self_modifying", "self-modifying code is not supported")]
    [InlineData("deadlock", "deadlock")]
    [InlineData("unimplemented", "SetupThread")]
    public async Task Faults_in_the_ps2_profile_have_the_same_message_in_the_built_program_and_the_interpreter(string scenario, string expected)
    {
        var p = new MipsProgram();
        switch (scenario)
        {
            case "io_register":
                p.Li32(Reg.T0, 0xB000F000).Sw(Reg.Zero, 0, Reg.T0);
                break;
            case "outside_ram":
                p.Li32(Reg.T0, 0x12340000).Lw(Reg.T1, 0, Reg.T0);
                break;
            case "self_modifying":
                p.Li32(Reg.T0, (uint)Ps2Elf.Code).Sw(Reg.Zero, 0, Reg.T0);
                break;
            case "deadlock":
                p.Ee(0x32);                                              // SleepThread with no other thread
                break;
            default:
                p.Ee(0x3C);                                              // SetupThread: known but not implemented
                break;
        }

        p.Ee(4);
        var result = await PipelineTestHelper.RunAsync(Ps2Elf.Build(p), "ps2_fault_" + scenario, platform: "ps2", allowIncomplete: scenario == "unimplemented");

        Assert.True(result.Success, result.FailureReason + string.Join("; ", result.Validation?.Differences ?? []));
        Assert.Equal("Faulted", result.Validation!.Native.Status);
        Assert.Contains(expected, result.Validation.Native.Message, StringComparison.Ordinal);
        Assert.Equal(result.Validation.Reference.Message, result.Validation.Native.Message);
    }

    [RequiresDotnetSdkFact]
    public async Task An_unimplemented_kernel_call_is_refused_by_default_and_the_reason_names_the_call()
    {
        var result = await PipelineTestHelper.RunAsync(Ps2Elf.Build(new MipsProgram().Ee(0x3C).Ee(4)), "ps2_refused", platform: "ps2");

        Assert.False(result.Success);
        Assert.Null(result.Build);
        Assert.Contains("SetupThread (0x3C)", result.FailureReason, StringComparison.Ordinal);
    }
}
