using System.Text.Json;
using RecompRadar.Recompilation.Build;
using RecompRadar.Recompilation.Compatibility;
using RecompRadar.Recompilation.Pipeline;
using RecompRadar.TestSupport;

namespace RecompRadar.Tests;

internal static class PipelineTestHelper
{
    public static async Task<RecompileResult> RunAsync(
        byte[] elfBytes,
        string name,
        bool allowIncomplete = false,
        string? platform = null,
        bool validate = true,
        IProgress<PipelineProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(NativeTestHelper.Root, "pipeline", name);
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, name + ".elf");
        await File.WriteAllBytesAsync(sourcePath, elfBytes, cancellationToken);
        var elf = T.ParseOk(elfBytes);
        return await RecompilationPipeline.RunAsync(
            new RecompileRequest(sourcePath, elf, Path.Combine(directory, "build"), PlatformOverride: platform, AllowIncomplete: allowIncomplete, Validate: validate, ProjectName: name),
            progress,
            cancellationToken);
    }

    public static byte[] Elf(MipsProgram program) => SampleFiles.Mips32Elf(program, bigEndian: false);
}

/// <summary>The complete recompilation pipeline against real builds: success, refusal, incomplete builds and run-time behavior.</summary>
public class PipelineEndToEndTests
{
    [RequiresDotnetSdkFact]
    public async Task The_self_test_goes_through_every_stage_and_writes_a_build_report()
    {
        var messages = new List<PipelineProgress>();
        var result = await PipelineTestHelper.RunAsync(Ps2StyleSelfTest.BuildElf(), "full_pipeline", platform: "ps2", progress: new InlineProgress<PipelineProgress>(m => { lock (messages) { messages.Add(m); } }));

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(BuildCategory.TranslatedWithRuntime, result.Category);
        Assert.Equal(CompatibilityLevel.Experimental, result.Compatibility!.Level);
        Assert.True(result.Validation!.Passed, string.Join("; ", result.Validation.Differences));
        Assert.True(result.CanPlay, result.PlayBlockedReason);
        Assert.Equal(
            new[] { PipelineStage.Prepare, PipelineStage.Analyze, PipelineStage.Compatibility, PipelineStage.Generate, PipelineStage.Build, PipelineStage.Validate, PipelineStage.Report },
            result.Stages.Select(s => s.Stage).ToArray());
        Assert.All(result.Stages, s => Assert.True(s.Succeeded, s.Summary));
        Assert.Equal(result.Stages.Select(s => s.Stage).Distinct().Count(), messages.Select(m => m.Stage).Distinct().Count());

        using var report = JsonDocument.Parse(await File.ReadAllBytesAsync(result.ReportPath!));
        var root = report.RootElement;
        Assert.True(root.GetProperty("outcome").GetProperty("success").GetBoolean());
        Assert.Contains("JIT", root.GetProperty("outcome").GetProperty("categoryTitle").GetString(), StringComparison.Ordinal);
        Assert.Equal("experimental", root.GetProperty("compatibility").GetProperty("level").GetString(), ignoreCase: true);
        Assert.True(root.GetProperty("validation").GetProperty("passed").GetBoolean());
        Assert.Equal(result.Build!.ExecutableSha256, root.GetProperty("build").GetProperty("executableSha256").GetString());
        Assert.Equal(result.SourceSha256, root.GetProperty("source").GetProperty("sha256").GetString());
        Assert.Equal(8, root.GetProperty("analysis").GetProperty("functions").GetInt32());
        Assert.True(root.GetProperty("limitations").GetArrayLength() >= 4);
        Assert.DoesNotContain(Environment.UserName, await File.ReadAllTextAsync(result.ReportPath!), StringComparison.OrdinalIgnoreCase);
    }

    [RequiresDotnetSdkFact]
    public async Task A_program_with_an_unsupported_instruction_is_refused_before_anything_is_built()
    {
        var elf = PipelineTestHelper.Elf(new MipsProgram().Nop().Beql(Reg.T0, Reg.T1, "x").Nop().Label("x").ExitWith(0));

        var result = await PipelineTestHelper.RunAsync(elf, "refused");

        Assert.False(result.Success);
        Assert.Equal(BuildCategory.Unsupported, result.Category);
        Assert.Null(result.Build);
        Assert.Null(result.ExecutablePath);
        Assert.Contains("beql", result.FailureReason, StringComparison.Ordinal);
        Assert.False(result.CanPlay);
        Assert.Equal(PipelineStage.Compatibility, result.Stages.Last().Stage);
        Assert.False(File.Exists(Path.Combine(NativeTestHelper.Root, "pipeline", "refused", "build", "out", "refused.exe")));
    }

    [RequiresDotnetSdkFact]
    public async Task An_incomplete_build_is_labelled_as_such_stops_at_the_unsupported_instruction_and_cannot_be_played()
    {
        var elf = PipelineTestHelper.Elf(new MipsProgram().PrintInt(Reg.T0).Beql(Reg.T0, Reg.T1, "x").Nop().Label("x").ExitWith(0));

        var result = await PipelineTestHelper.RunAsync(elf, "incomplete", allowIncomplete: true);

        Assert.True(result.Build!.Success, result.Build.FailureReason);
        Assert.True(result.Incomplete);
        Assert.True(result.Validation!.Passed, string.Join("; ", result.Validation.Differences));
        Assert.False(result.CanPlay);
        Assert.Contains("beql", result.PlayBlockedReason, StringComparison.Ordinal);

        var run = await ProgramLauncher.RunAsync(result.ExecutablePath!);
        Assert.Equal("Stopped", run.Report!.Status);
        Assert.Contains("beql", run.Report.Message, StringComparison.Ordinal);
        Assert.Equal(71, run.ExitCode);
        Assert.Equal("0\n", run.StandardOutput.ReplaceLineEndings("\n")); // everything before the unsupported instruction ran
    }

    [RequiresDotnetSdkTheory]
    [InlineData("unmapped_write", "Write of 4 byte(s) at unmapped address 0x12340000")]
    [InlineData("unaligned_read", "Unaligned 4-byte read at")]
    [InlineData("readonly_write", "Write to read-only memory at 0x00400000")]
    public async Task Memory_faults_stop_the_built_program_with_the_same_message_as_the_reference(string scenario, string expectedMessage)
    {
        var p = new MipsProgram().PrintInt(Reg.Zero);
        switch (scenario)
        {
            case "unmapped_write":
                p.Li32(Reg.T0, 0x12340000).Sw(Reg.T1, 0, Reg.T0);
                break;
            case "unaligned_read":
                p.Lw(Reg.T1, 1, Reg.Sp);
                break;
            default:
                p.Li32(Reg.T0, (uint)SampleFiles.CodeBase).Sw(Reg.T1, 0, Reg.T0);
                break;
        }

        p.ExitWith(0);
        var result = await PipelineTestHelper.RunAsync(PipelineTestHelper.Elf(p), scenario);

        Assert.True(result.Success, result.FailureReason + string.Join("; ", result.Validation?.Differences ?? []));
        Assert.Equal("Faulted", result.Validation!.Native.Status);
        Assert.Contains(expectedMessage, result.Validation.Native.Message, StringComparison.Ordinal);
        var run = await ProgramLauncher.RunAsync(result.ExecutablePath!);
        Assert.Equal(70, run.ExitCode);
        Assert.Contains(expectedMessage, run.StandardError, StringComparison.Ordinal);
        Assert.Equal("0\n", run.StandardOutput.ReplaceLineEndings("\n"));
    }

    [RequiresDotnetSdkFact]
    public async Task A_jump_to_code_that_static_analysis_cannot_see_is_caught_by_validation()
    {
        var hidden = SampleFiles.CodeBase + 0x40;
        var p = new MipsProgram()
            .Li32(Reg.T0, (uint)hidden).Sw(Reg.T0, 0, Reg.Sp).Lw(Reg.T1, 0, Reg.Sp)
            .Jr(Reg.T1).Nop()
            .ExitWith(1);
        while (p.ToBytes(SampleFiles.CodeBase, false).Length < 0x40)
        {
            p.Nop();
        }

        p.Label("hidden").PrintInt(Reg.Sp).ExitWith(0);
        Assert.Equal(hidden, p.AddressOf("hidden", SampleFiles.CodeBase));

        var result = await PipelineTestHelper.RunAsync(PipelineTestHelper.Elf(p), "hidden_target");

        Assert.True(result.Build?.Success == true, result.FailureReason);
        Assert.Single(result.Graph!.IndirectJumps);
        Assert.False(result.Validation!.Passed);
        Assert.False(result.Success);
        Assert.Equal("Faulted", result.Validation.Native.Status);
        Assert.Equal("Exited", result.Validation.Reference.Status.ToString());
        Assert.Contains("static analysis did not find", result.Validation.Native.Message, StringComparison.Ordinal);
        Assert.False(result.CanPlay);
        Assert.Contains("differently", result.PlayBlockedReason, StringComparison.Ordinal);
    }

    [RequiresDotnetSdkFact]
    public async Task A_constant_indirect_jump_is_resolved_and_the_built_program_matches_the_reference()
    {
        var p = new MipsProgram()
            .Li32(Reg.T0, (uint)SampleFiles.CodeBase + 0x40)
            .Jr(Reg.T0).Nop()
            .ExitWith(1);
        while (p.ToBytes(SampleFiles.CodeBase, false).Length < 0x40)
        {
            p.Nop();
        }

        p.PrintInt(Reg.Sp).ExitWith(0);

        var result = await PipelineTestHelper.RunAsync(PipelineTestHelper.Elf(p), "resolved_jump");

        Assert.True(result.Success, result.FailureReason + string.Join("; ", result.Validation?.Differences ?? []));
        Assert.Single(result.Graph!.ResolvedIndirectJumps);
        Assert.Equal(0, result.Validation!.Native.ExitCode);
    }

    [RequiresDotnetSdkFact]
    public async Task The_instruction_limit_and_the_timeout_stop_a_program_that_never_ends()
    {
        var p = new MipsProgram().Label("spin").J("spin").Nop();
        var result = await PipelineTestHelper.RunAsync(PipelineTestHelper.Elf(p), "spin", validate: false);
        Assert.True(result.Success, result.FailureReason);

        var limited = await ProgramLauncher.RunAsync(result.ExecutablePath!, maxInstructions: 10_000);
        Assert.Equal("StepLimitReached", limited.Report!.Status);
        Assert.Equal(72, limited.ExitCode);

        var started = DateTime.UtcNow;
        var timedOut = await ProgramLauncher.RunAsync(result.ExecutablePath!, timeout: TimeSpan.FromSeconds(2));
        Assert.True(timedOut.TimedOut);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        var cancelled = await ProgramLauncher.RunAsync(result.ExecutablePath!, timeout: TimeSpan.FromSeconds(30), cancellationToken: cts.Token);
        Assert.True(cancelled.Cancelled);
        Assert.Null(cancelled.Report);
    }

    [RequiresDotnetSdkFact]
    public async Task The_same_input_builds_to_identical_files_in_different_folders()
    {
        var elf = Ps2StyleSelfTest.BuildElf();
        var first = await NativeTestHelper.BuildAsync(elf, "repro", subfolder: "a");
        var second = await NativeTestHelper.BuildAsync(elf, "repro", subfolder: "b");

        Assert.True(first.Build.Success && second.Build.Success);
        Assert.NotEqual(first.Directory, second.Directory);
        Assert.Equal(first.Build.AssemblySha256, second.Build.AssemblySha256);
        Assert.Equal(first.Build.ExecutableSha256, second.Build.ExecutableSha256);
    }

    [Fact]
    public async Task Cancelling_before_the_start_throws_and_leaves_no_output()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var elf = PipelineTestHelper.Elf(new MipsProgram().ExitWith(0));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PipelineTestHelper.RunAsync(elf, "cancelled", cancellationToken: cts.Token));

        Assert.False(Directory.Exists(Path.Combine(NativeTestHelper.Root, "pipeline", "cancelled", "build")));
    }

    [Fact]
    public async Task A_file_without_a_backend_is_refused_with_the_reason()
    {
        var builder = new ElfBuilder { Machine = 62, Is64 = true };
        builder.WithCode(new byte[16], 0x400000);

        var result = await PipelineTestHelper.RunAsync(builder.Build(), "x64", platform: "ps4");

        Assert.False(result.Success);
        Assert.Equal(BuildCategory.Unsupported, result.Category);
        Assert.Null(result.Build);
        Assert.Contains("no recompiler plugin", result.Stages[0].Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not implemented", result.FailureReason, StringComparison.Ordinal);
    }
}
