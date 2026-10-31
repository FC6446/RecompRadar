using RecompRadar.Core.Model;
using RecompRadar.Recompilation;
using RecompRadar.Recompilation.Analysis;
using RecompRadar.Recompilation.Build;
using RecompRadar.Recompilation.Compatibility;
using RecompRadar.Recompilation.Mips;
using RecompRadar.TestSupport;

namespace RecompRadar.Tests;

public class CompatibilityTests
{
    private static CompatibilityAssessment AssessMips(MipsProgram program, string? platformOverride = null, bool bigEndian = false, bool withGraph = true)
    {
        var bytes = SampleFiles.Mips32Elf(program, bigEndian);
        var elf = T.ParseOk(bytes);
        return CompatibilityEngine.Assess(new CompatibilityInput(elf, null, platformOverride, withGraph ? Analyze.Elf(bytes) : null));
    }

    private static MipsProgram Hello() => new MipsProgram().PrintInt(Reg.T0).ExitWith(0);

    private static ElfFile OtherElf(ushort machine, bool is64, bool bigEndian)
    {
        var builder = new ElfBuilder { Machine = machine, Is64 = is64, BigEndian = bigEndian };
        builder.WithCode(new byte[16], 0x400000);
        return T.ParseOk(builder.Build());
    }

    [Fact]
    public void A_clean_self_made_program_is_experimental_and_can_be_built_and_played()
    {
        var a = AssessMips(Hello());

        Assert.Equal(CompatibilityLevel.Experimental, a.Level);
        Assert.True(a.CanRecompile);
        Assert.Null(a.RecompileBlockedReason);
        Assert.Null(a.RunBlockedReason);
        Assert.Equal(BuildCategory.TranslatedWithRuntime, a.ExpectedCategory);
        Assert.Equal(ConsoleBackendRegistry.GenericMips32, a.Backend!.Id);
        Assert.Contains(a.Notes, n => n.Kind == NoteKind.Info && n.Text.Contains("No console kernel", StringComparison.Ordinal));
    }

    [Fact]
    public void The_ps2_style_self_test_is_still_only_experimental()
    {
        var bytes = Ps2StyleSelfTest.BuildElf();
        var a = CompatibilityEngine.Assess(new CompatibilityInput(T.ParseOk(bytes), null, "ps2", Analyze.Elf(bytes)));

        Assert.Equal(CompatibilityLevel.Experimental, a.Level);
        Assert.Equal("ps2", a.PlatformId);
        Assert.Equal("manual override", a.PlatformSource);
        Assert.Equal(ConsoleBackendRegistry.Ps2, a.Backend!.Id);
    }

    [Fact]
    public void No_input_reaches_supported_or_partially_supported()
    {
        var levels = new List<CompatibilityLevel>
        {
            AssessMips(Hello()).Level,
            AssessMips(Hello(), "ps2").Level,
            AssessMips(new MipsProgram().Beql(Reg.T0, Reg.T1, "x").Nop().Label("x").ExitWith(0)).Level,
            CompatibilityEngine.Assess(new CompatibilityInput(OtherElf(21, true, true), null, "ps3")).Level,
            CompatibilityEngine.Assess(new CompatibilityInput(OtherElf(62, true, false), null, "ps4")).Level,
            CompatibilityEngine.Assess(new CompatibilityInput(null)).Level,
        };

        Assert.DoesNotContain(CompatibilityLevel.Supported, levels);
        Assert.DoesNotContain(CompatibilityLevel.PartiallySupported, levels);
    }

    [Fact]
    public void A_manual_platform_choice_does_not_change_the_level()
    {
        var automatic = AssessMips(Hello());
        var forced = AssessMips(Hello(), "ps2");

        Assert.Equal(automatic.Level, forced.Level);
        Assert.Equal(automatic.CanRecompile, forced.CanRecompile);
        Assert.Equal("manual override", forced.PlatformSource);
        Assert.Contains(forced.Notes, n => n.Text.Contains("does not change what the file contains", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unsupported_instruction_blocks_the_build_and_names_the_instruction()
    {
        var a = AssessMips(new MipsProgram().Nop().Beql(Reg.T0, Reg.T1, "x").Nop().Label("x").ExitWith(0));

        Assert.Equal(CompatibilityLevel.Unsupported, a.Level);
        Assert.False(a.CanRecompile);
        Assert.True(a.CanRecompileIncomplete);
        Assert.Contains("beql", a.RecompileBlockedReason, StringComparison.Ordinal);
        Assert.Contains("0x00400004", a.RunBlockedReason, StringComparison.Ordinal);
        Assert.Equal(BuildCategory.Unsupported, a.ExpectedCategory);
    }

    [Fact]
    public void An_unimplemented_system_call_blocks_play_with_the_call_number()
    {
        var program = new MipsProgram().Addiu(Reg.V0, Reg.Zero, 99).Syscall().ExitWith(0);

        var a = AssessMips(program);

        Assert.Equal(CompatibilityLevel.Unsupported, a.Level);
        Assert.Contains("99", a.RunBlockedReason, StringComparison.Ordinal);
        Assert.Contains("not implemented", a.RunBlockedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_system_call_number_that_is_not_constant_is_a_warning_not_a_blocker()
    {
        var program = new MipsProgram().Lw(Reg.V0, 0, Reg.Sp).Syscall().ExitWith(0);

        var a = AssessMips(program);

        Assert.Equal(CompatibilityLevel.Experimental, a.Level);
        Assert.Contains(a.Notes, n => n.Kind == NoteKind.Warning && n.Text.Contains("cannot be determined", StringComparison.Ordinal));
    }

    [Fact]
    public void Indirect_jumps_that_cannot_be_resolved_are_a_warning()
    {
        var program = new MipsProgram().Lw(Reg.T0, 0, Reg.Sp).Jr(Reg.T0).Nop().ExitWith(0);

        var a = AssessMips(program);

        Assert.Equal(CompatibilityLevel.Experimental, a.Level);
        Assert.Contains(a.Notes, n => n.Kind == NoteKind.Warning && n.Text.Contains("through a register", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_an_analysis_the_assessment_is_provisional()
    {
        var a = AssessMips(Hello(), withGraph: false);

        Assert.Equal(CompatibilityLevel.Experimental, a.Level);
        Assert.Contains("provisional", a.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void Powerpc_without_a_hint_has_no_backend()
    {
        var a = CompatibilityEngine.Assess(new CompatibilityInput(OtherElf(21, true, true)));

        Assert.Equal(CompatibilityLevel.Unsupported, a.Level);
        Assert.Null(a.Backend);
        Assert.False(a.CanRecompile);
        Assert.False(a.CanRecompileIncomplete);
        Assert.Contains("64-bit PowerPC", a.RecompileBlockedReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ps3", (ushort)21, true, true, "milestone C")]
    [InlineData("ps4", (ushort)62, true, false, "milestone D")]
    public void Planned_consoles_are_reported_as_not_implemented_even_when_chosen_manually(string platform, ushort machine, bool is64, bool big, string milestone)
    {
        var a = CompatibilityEngine.Assess(new CompatibilityInput(OtherElf(machine, is64, big), null, platform));

        Assert.Equal(CompatibilityLevel.Unsupported, a.Level);
        Assert.Equal(platform, a.Backend!.Id);
        Assert.False(a.CanRecompile);
        Assert.False(a.CanRecompileIncomplete);
        Assert.Contains(milestone, a.RecompileBlockedReason, StringComparison.Ordinal);
        Assert.Contains("not implemented", a.RecompileBlockedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_platform_choice_that_contradicts_the_cpu_is_rejected()
    {
        var a = CompatibilityEngine.Assess(new CompatibilityInput(OtherElf(62, true, false), null, "ps2"));

        Assert.Equal(CompatibilityLevel.Unsupported, a.Level);
        Assert.Contains("cannot change that", a.RecompileBlockedReason, StringComparison.Ordinal);
        Assert.Contains("x86-64", a.RecompileBlockedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_platform_without_any_backend_is_named()
    {
        var a = AssessMips(Hello(), "psp");

        Assert.Equal(CompatibilityLevel.Unsupported, a.Level);
        Assert.Contains("psp", a.RecompileBlockedReason, StringComparison.Ordinal);
        Assert.Null(a.Backend);
    }

    [Fact]
    public void A_detected_hint_is_labelled_as_a_hint_not_proof()
    {
        var bytes = SampleFiles.Mips32Elf(Hello(), bigEndian: false);
        var detected = new PlatformAssessment
        {
            Candidates = [new PlatformCandidate { PlatformId = "ps2", DisplayName = "PlayStation 2", Strength = HintStrength.Strong }],
        };

        var a = CompatibilityEngine.Assess(new CompatibilityInput(T.ParseOk(bytes), detected, null, Analyze.Elf(bytes)));

        Assert.Equal("ps2", a.PlatformId);
        Assert.Equal("detected hint (not proof)", a.PlatformSource);
        Assert.Equal(CompatibilityLevel.Experimental, a.Level);
    }

    [Fact]
    public void A_weak_hint_is_ignored()
    {
        var bytes = SampleFiles.Mips32Elf(Hello(), bigEndian: false);
        var detected = new PlatformAssessment
        {
            Candidates = [new PlatformCandidate { PlatformId = "ps2", Strength = HintStrength.Weak }],
        };

        var a = CompatibilityEngine.Assess(new CompatibilityInput(T.ParseOk(bytes), detected));

        Assert.Null(a.PlatformId);
        Assert.Equal("none", a.PlatformSource);
    }

    [Fact]
    public void A_big_endian_file_chosen_as_ps2_gets_a_warning()
    {
        var a = AssessMips(Hello(), "ps2", bigEndian: true);

        Assert.Contains(a.Notes, n => n.Kind == NoteKind.Warning && n.Text.Contains("little-endian", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_file_is_unknown_not_unsupported()
    {
        var a = CompatibilityEngine.Assess(new CompatibilityInput(null));

        Assert.Equal(CompatibilityLevel.Unknown, a.Level);
        Assert.False(a.CanRecompile);
    }

    [Fact]
    public void The_backend_registry_never_claims_more_than_exists()
    {
        Assert.True(ConsoleBackendRegistry.Find("ps2")!.HasWorkingPipeline);
        Assert.False(ConsoleBackendRegistry.Find("ps3")!.HasWorkingPipeline);
        Assert.False(ConsoleBackendRegistry.Find("ps4")!.HasWorkingPipeline);
        Assert.Equal(new MipsPocPlugin().Id, ConsoleBackendRegistry.Find("ps2")!.PluginId);

        foreach (var backend in ConsoleBackendRegistry.All.Where(b => !b.HasWorkingPipeline))
        {
            Assert.DoesNotContain(backend.Modules, m => m.State is ModuleState.Partial && !m.Name.Contains("analysis", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(backend.Modules, m => m.Name.Contains("code generator", StringComparison.OrdinalIgnoreCase) && m.State == ModuleState.Implemented);
        }

        // The PS2 backend has no graphics, sound, input or vector units yet; every such module must say so.
        var ps2 = ConsoleBackendRegistry.Find("ps2")!;
        foreach (var name in new[] { "Graphics Synthesizer", "Vector units", "IOP", "Sound", "Controller input" })
        {
            Assert.Equal(ModuleState.Planned, ps2.Modules.Single(m => m.Name.Contains(name, StringComparison.Ordinal)).State);
        }

        // What exists is partial or small, and says so.
        Assert.Equal(ModuleState.Partial, ps2.Modules.Single(m => m.Name.StartsWith("EE kernel", StringComparison.Ordinal)).State);
        Assert.Equal(ModuleState.Partial, ps2.Modules.Single(m => m.Name == "EE memory map").State);
        Assert.Equal(ModuleState.Implemented, ps2.Modules.Single(m => m.Name == "Serial text output").State);
    }

    [Fact]
    public void Build_categories_have_distinct_honest_titles()
    {
        var titles = Enum.GetValues<BuildCategory>().Select(c => c.Title()).ToList();

        Assert.Equal(titles.Count, titles.Distinct().Count());
        Assert.Contains("JIT", BuildCategory.TranslatedWithRuntime.Title(), StringComparison.Ordinal);
        Assert.Contains("not ahead-of-time machine code", BuildCategory.TranslatedWithRuntime.Explanation(), StringComparison.Ordinal);
        Assert.DoesNotContain("native", BuildCategory.TranslatedWithRuntime.Title(), StringComparison.OrdinalIgnoreCase);
    }
}

public class IndirectJumpResolutionTests
{
    [Fact]
    public void A_constant_jump_target_built_with_lui_and_ori_becomes_a_direct_jump()
    {
        var p = new MipsProgram()
            .Li32(Reg.T0, (uint)SampleFiles.CodeBase + 0x20)
            .Jr(Reg.T0).Nop()
            .ExitWith(1);
        while (p.ToBytes(SampleFiles.CodeBase, false).Length < 0x20)
        {
            p.Nop();
        }

        p.PrintInt(Reg.T0).ExitWith(0);

        var graph = Analyze.Program(p);

        var resolved = Assert.Single(graph.ResolvedIndirectJumps);
        Assert.False(resolved.IsCall);
        Assert.Equal(SampleFiles.CodeBase + 0x20, resolved.Target);
        Assert.Empty(graph.IndirectJumps);
        Assert.Contains(SampleFiles.CodeBase + 0x20, graph.Blocks.Keys);
        Assert.True(graph.IsFullyResolved);
    }

    [Fact]
    public void A_constant_function_pointer_call_is_resolved_and_creates_a_function()
    {
        var p = new MipsProgram()
            .Li32(Reg.T0, (uint)SampleFiles.CodeBase + 0x40)
            .Jalr(Reg.Ra, Reg.T0).Nop()
            .ExitWith(0);
        while (p.ToBytes(SampleFiles.CodeBase, false).Length < 0x40)
        {
            p.Nop();
        }

        p.Jr(Reg.Ra).Nop();

        var graph = Analyze.Program(p);

        var call = Assert.Single(graph.ResolvedIndirectJumps);
        Assert.True(call.IsCall);
        Assert.Contains(graph.Functions, f => f.Entry == SampleFiles.CodeBase + 0x40);
        Assert.Contains(graph.CallGraph, e => e.Callee == SampleFiles.CodeBase + 0x40);
    }

    [Fact]
    public void A_target_loaded_from_memory_stays_unresolved()
    {
        var p = new MipsProgram().Lw(Reg.T0, 0, Reg.Sp).Jr(Reg.T0).Nop().ExitWith(0);

        var graph = Analyze.Program(p);

        Assert.Single(graph.IndirectJumps);
        Assert.Empty(graph.ResolvedIndirectJumps);
    }

    [Fact]
    public void A_jump_target_that_depends_on_a_conditional_move_stays_unresolved()
    {
        var p = new MipsProgram()
            .Li32(Reg.T0, (uint)SampleFiles.CodeBase).Lw(Reg.T1, 0, Reg.Sp)
            .Movz(Reg.T0, Reg.T0, Reg.T1)
            .Jr(Reg.T0).Nop().ExitWith(0);

        var graph = Analyze.Program(p);

        Assert.Single(graph.IndirectJumps);
    }

    [Fact]
    public void Block_constants_follow_arithmetic_but_not_memory()
    {
        var p = new MipsProgram()
            .Addiu(Reg.T0, Reg.Zero, 5).Sll(Reg.T1, Reg.T0, 2).Addu(Reg.T2, Reg.T0, Reg.T1)
            .Lw(Reg.T3, 0, Reg.Sp).Addu(Reg.T4, Reg.T3, Reg.T0)
            .ExitWith(0);

        var graph = Analyze.Program(p);
        var constants = BlockConstants.AtEnd(graph.Blocks[SampleFiles.CodeBase].Ir);

        Assert.Equal(5u, constants[Reg.T0]);
        Assert.Equal(20u, constants[Reg.T1]);
        Assert.Equal(25u, constants[Reg.T2]);
        Assert.False(constants.ContainsKey(Reg.T3));
        Assert.False(constants.ContainsKey(Reg.T4));
    }
}
