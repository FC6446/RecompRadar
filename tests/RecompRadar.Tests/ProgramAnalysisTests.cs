using RecompRadar.Recompilation;
using RecompRadar.Recompilation.Analysis;
using RecompRadar.Recompilation.Mips;
using RecompRadar.TestSupport;

namespace RecompRadar.Tests;

internal static class Analyze
{
    public static ProgramGraph Elf(byte[] elfBytes, ProgramAnalyzerOptions? options = null)
    {
        var elf = T.ParseOk(elfBytes);
        var plugin = new MipsPocPlugin();
        using var stream = new MemoryStream(elfBytes);
        var image = plugin.CreateLoader()!.Load(stream, elf);
        var input = AnalysisInputFactory.FromImage(image, elf, plugin.CreateTranslator()!, plugin.NoReturnSystemCalls);
        return ProgramAnalyzer.Analyze(input, options);
    }

    public static ProgramGraph Program(MipsProgram program) => Elf(SampleFiles.Mips32Elf(program, bigEndian: false));
}

public class ProgramAnalysisTests
{
    private static readonly ProgramGraph SelfTest = Analyze.Elf(Ps2StyleSelfTest.BuildElf());

    private static ulong Address(string label) => Ps2StyleSelfTest.BuildProgram().AddressOf(label, Ps2StyleSelfTest.CodeAddress);

    [Fact]
    public void Self_test_program_has_exactly_the_eight_known_functions()
    {
        var names = SelfTest.Functions.Select(f => f.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "fact", "fib", "floattest", "main", "math", "sorttest", "strtest", "sumrec" }, names);
        Assert.All(SelfTest.Functions, f => Assert.Equal("symbol", f.NameSource));
        Assert.Equal(Ps2StyleSelfTest.CodeAddress, SelfTest.EntryPoint);
    }

    [Fact]
    public void Self_test_call_graph_matches_the_program_structure()
    {
        var calls = SelfTest.CallGraph.Select(e => (e.Caller, e.Callee)).ToHashSet();
        foreach (var callee in new[] { "fib", "fact", "sumrec", "math", "sorttest", "strtest", "floattest" })
        {
            Assert.Contains((Address("main"), Address(callee)), calls);
        }

        foreach (var recursive in new[] { "fib", "fact", "sumrec" })
        {
            Assert.Contains((Address(recursive), Address(recursive)), calls);
        }

        Assert.Equal(new[] { "fact", "fib", "sumrec" }, SelfTest.Functions.Where(f => f.IsRecursive).Select(f => f.Name).OrderBy(n => n).ToArray());
        Assert.Empty(SelfTest.Functions.Single(f => f.Name == "math").Callees);
    }

    [Fact]
    public void Self_test_is_fully_resolved_and_covers_all_code()
    {
        Assert.True(SelfTest.IsFullyResolved);
        Assert.Empty(SelfTest.IndirectJumps);
        Assert.Empty(SelfTest.UnsupportedInstructions);
        Assert.False(SelfTest.Truncated);
        Assert.Equal(SelfTest.ExecutableBytes, SelfTest.ReachableBytes);
        Assert.Equal((int)(SelfTest.ReachableBytes / 4), SelfTest.InstructionCount);
    }

    [Fact]
    public void Self_test_blocks_never_overlap_and_all_edges_point_to_blocks()
    {
        var ordered = SelfTest.Blocks.Values.OrderBy(b => b.Start).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            Assert.True(ordered[i - 1].End <= ordered[i].Start, $"block 0x{ordered[i - 1].Start:X} overlaps 0x{ordered[i].Start:X}");
        }

        foreach (var edge in ordered.SelectMany(b => b.Successors))
        {
            Assert.True(SelfTest.Blocks.ContainsKey(edge.To), $"edge to 0x{edge.To:X} has no block");
        }

        Assert.All(ordered, b => Assert.Equal(b.Start, b.Ir.StartAddress));
    }

    [Fact]
    public void Self_test_syscall_sites_all_have_a_known_number()
    {
        Assert.NotEmpty(SelfTest.SyscallSites);
        Assert.All(SelfTest.SyscallSites, s => Assert.NotNull(s.Number));
        Assert.Contains(SelfTest.SyscallSites, s => s.Number == 1);
        Assert.Contains(SelfTest.SyscallSites, s => s.Number == 5);
    }

    [Fact]
    public void Self_test_returns_are_recognised_and_not_reported_as_unresolved()
    {
        var returns = SelfTest.Blocks.Values.Count(b => b.Exit == BlockExit.Return);
        Assert.True(returns >= 7, $"expected at least 7 returns, found {returns}");
        Assert.DoesNotContain(SelfTest.Findings, f => f.Code is "INDIRECT_JUMP" or "INDIRECT_CALL");
    }

    [Fact]
    public void Cross_references_list_every_call_site()
    {
        var fibCallers = SelfTest.CrossReferences.Where(x => x.To == Address("fib") && x.Kind == XRefKind.Call).ToList();
        Assert.Equal(3, fibCallers.Count); // main plus the two recursive calls inside fib
    }

    [Fact]
    public void Jump_through_a_register_is_reported_as_unresolved()
    {
        var p = new MipsProgram()
            .Lw(Reg.T0, 0, Reg.Sp)
            .Jr(Reg.T0).Nop()
            .ExitWith(0);

        var graph = Analyze.Program(p);

        var jump = Assert.Single(graph.IndirectJumps);
        Assert.False(jump.IsCall);
        Assert.Equal(Reg.T0, jump.GuestRegister);
        Assert.False(graph.IsFullyResolved);
        Assert.Contains(graph.Findings, f => f.Code == "INDIRECT_JUMP" && f.Severity == FindingSeverity.Warning);
    }

    [Fact]
    public void Call_through_a_register_is_reported_and_execution_continues_after_it()
    {
        var p = new MipsProgram()
            .Lw(Reg.T0, 0, Reg.Sp)
            .Jalr(Reg.Ra, Reg.T0).Nop()
            .ExitWith(0);

        var graph = Analyze.Program(p);

        var call = Assert.Single(graph.IndirectJumps);
        Assert.True(call.IsCall);
        Assert.Contains(graph.Findings, f => f.Code == "INDIRECT_CALL");
        Assert.Single(graph.SyscallSites); // the code after the call is still analysed
    }

    [Fact]
    public void Unsupported_instruction_is_listed_by_name_with_its_address()
    {
        var p = new MipsProgram().Nop().Beql(Reg.T0, Reg.T1, "x").Nop().Label("x").ExitWith(0);

        var graph = Analyze.Program(p);

        var unsupported = Assert.Single(graph.UnsupportedInstructions);
        Assert.Equal("beql", unsupported.Mnemonic);
        Assert.Equal(SampleFiles.CodeBase + 4, unsupported.Address);
        Assert.False(graph.IsFullyResolved);
        Assert.Contains(graph.Findings, f => f.Code == "UNSUPPORTED_INSTRUCTION" && f.Severity == FindingSeverity.Error);
    }

    [Fact]
    public void Jump_outside_executable_memory_is_reported_not_followed()
    {
        // j 0x00900000: an address that is not mapped at all.
        var graph = Analyze.Program(new MipsProgram().Word(0x08000000 | (0x00900000 >> 2)).Nop().ExitWith(0));

        Assert.Contains(graph.Findings, f => f.Code == "TARGET_NOT_EXECUTABLE");
        var jump = Assert.Single(graph.Blocks.Values, b => b.Exit == BlockExit.Jump);
        Assert.Empty(jump.Successors);
    }

    [Fact]
    public void Code_that_runs_off_the_end_of_the_segment_is_reported()
    {
        var graph = Analyze.Program(new MipsProgram().Nop().Nop());

        Assert.Contains(graph.Findings, f => f.Code == "CODE_STOPS" && f.Severity == FindingSeverity.Error);
        Assert.False(graph.IsFullyResolved);
    }

    [Fact]
    public void Unreachable_code_is_not_counted_as_reachable()
    {
        var p = new MipsProgram().ExitWith(0).Nop().Nop().Nop().Nop();

        var graph = Analyze.Program(p);

        Assert.True(graph.ReachableBytes < graph.ExecutableBytes);
        Assert.Equal(graph.ExecutableBytes - 16, graph.ReachableBytes);
    }

    [Fact]
    public void A_branch_into_the_middle_of_a_block_splits_that_block()
    {
        var p = new MipsProgram()
            .Addiu(Reg.T0, Reg.Zero, 3)
            .Label("loop")
            .Addiu(Reg.T0, Reg.T0, -1)
            .Bne(Reg.T0, Reg.Zero, "loop").Nop()
            .ExitWith(0);

        var graph = Analyze.Program(p);

        var loopStart = p.AddressOf("loop", SampleFiles.CodeBase);
        Assert.Contains(loopStart, graph.Blocks.Keys);
        Assert.Equal(SampleFiles.CodeBase, graph.Blocks[SampleFiles.CodeBase].Start);
        Assert.Equal(loopStart, graph.Blocks[SampleFiles.CodeBase].End);
        Assert.Equal(BlockExit.Fallthrough, graph.Blocks[SampleFiles.CodeBase].Exit);
        Assert.Equal(BlockExit.Branch, graph.Blocks[loopStart].Exit);
        Assert.Contains(graph.Blocks[loopStart].Successors, e => e.Kind == EdgeKind.Taken && e.To == loopStart);
    }

    [Fact]
    public void Analysis_limit_marks_the_result_as_incomplete()
    {
        var graph = Analyze.Elf(Ps2StyleSelfTest.BuildElf(), new ProgramAnalyzerOptions(MaxBlocks: 3));

        Assert.True(graph.Truncated);
        Assert.False(graph.IsFullyResolved);
        Assert.Contains(graph.Findings, f => f.Code == "LIMIT_REACHED");
    }

    [Fact]
    public void Functions_without_symbols_are_named_after_their_address_and_discovered_from_calls()
    {
        var p = new MipsProgram().Jal("f").Nop().ExitWith(0);
        p.Label("f").Jr(Reg.Ra).Nop();

        var graph = Analyze.Program(p);

        var f = graph.Functions.Single(x => x.Name != "_entry");
        Assert.Equal($"sub_{p.AddressOf("f", SampleFiles.CodeBase):X8}", f.Name);
        Assert.Equal("call target", f.NameSource);
        Assert.Equal("_entry", graph.Functions.Single(x => x.Entry == SampleFiles.CodeBase).Name);
    }

    [Fact]
    public void Analysis_works_for_big_endian_programs_too()
    {
        var p = new MipsProgram().Jal("f").Nop().ExitWith(0);
        p.Label("f").Jr(Reg.Ra).Nop();

        var graph = Analyze.Elf(SampleFiles.Mips32Elf(p, bigEndian: true));

        Assert.Equal(2, graph.Functions.Count);
        Assert.True(graph.IsFullyResolved);
    }
}
