using RecompRadar.Core;
using RecompRadar.Core.Model;
using RecompRadar.Recompilation;
using RecompRadar.Recompilation.Execution;
using RecompRadar.Recompilation.IR;
using RecompRadar.Recompilation.Loading;
using RecompRadar.Recompilation.Mips;
using RecompRadar.Recompilation.Preview;
using RecompRadar.TestSupport;

namespace RecompRadar.Tests;

public class MipsExecutionTests
{
    private const ulong Base = SampleFiles.CodeBase;

    private static ExecutionResult Run(MipsProgram program, bool bigEndian, long maxInstructions = 100_000)
    {
        var bytes = SampleFiles.Mips32Elf(program, bigEndian);
        var elf = T.ParseOk(bytes);
        var plugin = new MipsPocPlugin();
        using var stream = new MemoryStream(bytes);
        var machine = GuestMachine.FromImage(plugin.CreateLoader()!.Load(stream, elf));
        return plugin.CreateBackend()!.Run(machine, plugin.CreateTranslator()!, plugin.CreateRuntime()!, new ExecutionLimits(maxInstructions));
    }

    private static MipsProgram Print(MipsProgram p, int register) =>
        p.Move(Reg.A0, register).Addiu(Reg.V0, Reg.Zero, 3).Syscall();

    private static MipsProgram Exit(MipsProgram p, int code = 0) =>
        p.Addiu(Reg.A0, Reg.Zero, code).Addiu(Reg.V0, Reg.Zero, 1).Syscall();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Sum_program_prints_55_on_both_endiannesses(bool bigEndian)
    {
        var result = Run(SampleFiles.SumProgram(), bigEndian);

        Assert.Equal(ExecutionStatus.Exited, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("55\n", result.Output);
        Assert.Equal(49, result.InstructionsExecuted); // 3 setup + 10 iterations x 4 (incl. delay slot) + 6 for print and exit
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Function_call_with_delay_slots_returns_to_the_caller(bool bigEndian)
    {
        var result = Run(SampleFiles.CallProgram(), bigEndian);

        Assert.Equal(ExecutionStatus.Exited, result.Status);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal("42\n", result.Output);
    }

    [Fact]
    public void Delay_slot_of_a_taken_branch_executes_and_the_skipped_instruction_does_not()
    {
        var p = new MipsProgram()
            .Addiu(Reg.T0, Reg.Zero, 1)
            .Beq(Reg.Zero, Reg.Zero, "target")
            .Addiu(Reg.T0, Reg.T0, 5)
            .Addiu(Reg.T0, Reg.T0, 100)
            .Label("target");
        Exit(Print(p, Reg.T0));

        var result = Run(p, bigEndian: true);

        Assert.Equal("6\n", result.Output);
    }

    [Fact]
    public void Delay_slot_of_a_not_taken_branch_also_executes()
    {
        var p = new MipsProgram()
            .Addiu(Reg.T0, Reg.Zero, 1)
            .Bne(Reg.Zero, Reg.Zero, "target")
            .Addiu(Reg.T0, Reg.T0, 5)
            .Addiu(Reg.T0, Reg.T0, 100)
            .Label("target");
        Exit(Print(p, Reg.T0));

        Assert.Equal("106\n", Run(p, bigEndian: false).Output);
    }

    [Fact]
    public void Branch_condition_is_evaluated_before_the_delay_slot_changes_the_register()
    {
        var p = new MipsProgram()
            .Addiu(Reg.T0, Reg.Zero, 1)
            .Bne(Reg.T0, Reg.Zero, "taken")
            .Addiu(Reg.T0, Reg.Zero, 0) // delay slot clears the register the branch tested
            .Addiu(Reg.A1, Reg.Zero, 111)
            .J("print")
            .Nop()
            .Label("taken")
            .Addiu(Reg.A1, Reg.Zero, 222)
            .Label("print");
        Exit(Print(p, Reg.A1));

        Assert.Equal("222\n", Run(p, bigEndian: true).Output);
    }

    [Fact]
    public void Jump_register_target_is_read_before_the_delay_slot()
    {
        var p = new MipsProgram()
            .Lui(Reg.T0, 0x0040)
            .Ori(Reg.T0, Reg.T0, 0x0018) // address of the instruction labelled "target" (index 6)
            .Jr(Reg.T0)
            .Addiu(Reg.T0, Reg.Zero, 0) // delay slot overwrites the register holding the target
            .Addiu(Reg.A1, Reg.Zero, 1)
            .Nop()
            .Label("target")
            .Addiu(Reg.A1, Reg.Zero, 99);
        Exit(Print(p, Reg.A1));

        Assert.Equal("99\n", Run(p, bigEndian: false).Output);
    }

    [Fact]
    public void Arithmetic_logic_and_shift_instructions_have_correct_semantics()
    {
        var p = new MipsProgram()
            .Addiu(Reg.T0, Reg.Zero, -1)
            .Addiu(Reg.T1, Reg.Zero, 1);
        Print(p.Slt(Reg.T2, Reg.T0, Reg.T1), Reg.T2);       // -1 < 1 signed            -> 1
        Print(p.Sltu(Reg.T2, Reg.T0, Reg.T1), Reg.T2);      // 0xFFFFFFFF < 1 unsigned  -> 0
        Print(p.Slti(Reg.T2, Reg.T0, 0), Reg.T2);           // -1 < 0                   -> 1
        Print(p.Sltiu(Reg.T2, Reg.T1, -1), Reg.T2);         // 1 < 0xFFFFFFFF          -> 1
        Print(p.Sra(Reg.T2, Reg.T0, 4), Reg.T2);            // arithmetic shift         -> -1
        Print(p.Srl(Reg.T2, Reg.T0, 28), Reg.T2);           // logical shift            -> 15
        Print(p.Sll(Reg.T2, Reg.T1, 31), Reg.T2);           // 0x80000000               -> -2147483648
        Print(p.Nor(Reg.T2, Reg.Zero, Reg.Zero), Reg.T2);   // ~0                       -> -1
        Print(p.Mul(Reg.T2, Reg.T0, Reg.T0), Reg.T2);       // (-1) * (-1)              -> 1
        Print(p.Subu(Reg.T2, Reg.Zero, Reg.T1), Reg.T2);    // 0 - 1                    -> -1
        Print(p.Xori(Reg.T2, Reg.T1, 3), Reg.T2);           // 1 ^ 3                    -> 2
        Print(p.Andi(Reg.T2, Reg.T0, 0xF0F0), Reg.T2);      // zero-extended immediate  -> 61680
        Print(p.Ori(Reg.T2, Reg.Zero, 0x8000), Reg.T2);     // zero-extended immediate  -> 32768
        Print(p.Addiu(Reg.T2, Reg.Zero, -32768), Reg.T2);   // sign-extended immediate  -> -32768
        Print(p.Lui(Reg.T2, 0x8000), Reg.T2);               // 0x80000000               -> -2147483648
        Exit(p);

        var result = Run(p, bigEndian: true);

        Assert.Equal(ExecutionStatus.Exited, result.Status);
        Assert.Equal("1\n0\n1\n1\n-1\n15\n-2147483648\n-1\n1\n-1\n2\n61680\n32768\n-32768\n-2147483648\n", result.Output);
    }

    [Theory]
    [InlineData(true, "-2\n254\n-2\n65534\n305419896\n18\n")]
    [InlineData(false, "-2\n254\n-2\n65534\n305419896\n120\n")]
    public void Loads_and_stores_honour_size_sign_extension_and_endianness(bool bigEndian, string expected)
    {
        var p = new MipsProgram().Addiu(Reg.T0, Reg.Zero, -2);
        p.Sb(Reg.T0, 0, Reg.Sp).Sh(Reg.T0, 4, Reg.Sp);
        p.Li32(Reg.T3, 0x12345678).Sw(Reg.T3, 8, Reg.Sp);
        Print(p.Lb(Reg.T1, 0, Reg.Sp), Reg.T1);
        Print(p.Lbu(Reg.T1, 0, Reg.Sp), Reg.T1);
        Print(p.Lh(Reg.T1, 4, Reg.Sp), Reg.T1);
        Print(p.Lhu(Reg.T1, 4, Reg.Sp), Reg.T1);
        Print(p.Lw(Reg.T1, 8, Reg.Sp), Reg.T1);
        Print(p.Lb(Reg.T1, 8, Reg.Sp), Reg.T1); // first byte in memory: 0x12 (big endian) or 0x78 (little endian)
        Exit(p);

        Assert.Equal(expected, Run(p, bigEndian).Output);
    }

    [Fact]
    public void Writes_to_the_zero_register_are_ignored()
    {
        var p = new MipsProgram().Addiu(Reg.Zero, Reg.Zero, 5);
        Print(p, Reg.Zero);
        Exit(p);

        Assert.Equal("0\n", Run(p, bigEndian: true).Output);
    }

    [Fact]
    public void Exit_code_is_returned()
    {
        var p = new MipsProgram();
        Exit(p, 3);

        var result = Run(p, bigEndian: false);

        Assert.Equal(ExecutionStatus.Exited, result.Status);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public void Infinite_loops_stop_at_the_instruction_limit()
    {
        var p = new MipsProgram().Label("spin").Beq(Reg.Zero, Reg.Zero, "spin").Nop();

        var result = Run(p, bigEndian: true, maxInstructions: 1000);

        Assert.Equal(ExecutionStatus.StepLimitReached, result.Status);
        Assert.InRange(result.InstructionsExecuted, 1000, 1100);
    }

    [Fact]
    public void Unsupported_instructions_stop_execution_with_a_clear_message_instead_of_guessing()
    {
        var p = new MipsProgram().Addiu(Reg.T0, Reg.Zero, 1).Word(0x46000000); // COP1 (floating point) is outside the supported subset

        var result = Run(p, bigEndian: true);

        Assert.Equal(ExecutionStatus.Stopped, result.Status);
        Assert.Contains("unsupported instruction 0x46000000", result.Message);
        Assert.Contains($"0x{Base + 4:X8}", result.Message);
    }

    [Fact]
    public void Memory_faults_are_reported_not_thrown()
    {
        var unmapped = Run(new MipsProgram().Lw(Reg.T0, 0, Reg.Zero), bigEndian: true);
        var unaligned = Run(new MipsProgram().Lw(Reg.T0, 2, Reg.Sp), bigEndian: true);
        var readOnly = Run(new MipsProgram().Lui(Reg.T0, 0x0040).Sw(Reg.T0, 0, Reg.T0), bigEndian: true);

        Assert.Equal(ExecutionStatus.Faulted, unmapped.Status);
        Assert.Contains("unmapped address", unmapped.Message);
        Assert.Equal(ExecutionStatus.Faulted, unaligned.Status);
        Assert.Contains("Unaligned", unaligned.Message);
        Assert.Equal(ExecutionStatus.Faulted, readOnly.Status);
        Assert.Contains("read-only", readOnly.Message);
    }

    [Fact]
    public void Unknown_system_calls_fault_instead_of_being_ignored()
    {
        var result = Run(new MipsProgram().Addiu(Reg.V0, Reg.Zero, 99).Syscall(), bigEndian: true);

        Assert.Equal(ExecutionStatus.Faulted, result.Status);
        Assert.Contains("Unimplemented system call 99", result.Message);
    }

    [Fact]
    public void A_branch_inside_a_delay_slot_is_refused()
    {
        var p = new MipsProgram().Label("a").Beq(Reg.Zero, Reg.Zero, "a").Beq(Reg.Zero, Reg.Zero, "a").Nop();

        var result = Run(p, bigEndian: true);

        Assert.Equal(ExecutionStatus.Stopped, result.Status);
        Assert.Contains("delay slot", result.Message);
    }

    [Fact]
    public void Running_off_the_end_of_the_code_stops_cleanly()
    {
        var result = Run(new MipsProgram().Nop().Nop(), bigEndian: true);

        Assert.Equal(ExecutionStatus.Stopped, result.Status);
        Assert.Contains("unmapped", result.Message);
    }
}

public class MipsTranslatorAndDecoderTests
{
    private static LoadedImage Image(MipsProgram p, bool bigEndian, ulong address = SampleFiles.CodeBase) => new()
    {
        EntryPoint = address,
        Family = CpuFamily.Mips,
        Endianness = bigEndian ? Endianness.Big : Endianness.Little,
        Segments = [new LoadedSegment(address, p.ToBytes(address, bigEndian), true, false, true)],
    };

    [Fact]
    public void Translator_folds_the_delay_slot_into_the_block_and_resolves_both_targets()
    {
        var image = Image(SampleFiles.SumProgram(), bigEndian: true);

        var block = new MipsTranslator().TranslateBlock(image, Endianness.Big, SampleFiles.CodeBase);

        Assert.Equal(7, block.GuestInstructionCount); // 3 setup + 2 loop body + bne + delay slot
        var branch = Assert.IsType<IrBranch>(block.Terminator);
        Assert.Equal(SampleFiles.CodeBase + 12, branch.Taken);        // label "loop"
        Assert.Equal(SampleFiles.CodeBase + 5 * 4 + 8, branch.NotTaken); // after the delay slot
        Assert.Equal(IrOp.SetNe, block.Instructions.Last().Op);
        Assert.Contains("branch", block.Format());
    }

    [Fact]
    public void Translator_ends_blocks_at_syscalls_and_calls()
    {
        var image = Image(SampleFiles.CallProgram(), bigEndian: false);
        var translator = new MipsTranslator();

        var first = translator.TranslateBlock(image, Endianness.Little, SampleFiles.CodeBase);
        var call = Assert.IsType<IrJump>(first.Terminator);
        Assert.True(call.IsCall);
        Assert.Contains(first.Instructions, i => i.Op == IrOp.Const && i.Dest == 31 && i.A.Value == SampleFiles.CodeBase + 4 + 8);

        var afterCall = translator.TranslateBlock(image, Endianness.Little, SampleFiles.CodeBase + 12);
        Assert.IsType<IrSyscall>(afterCall.Terminator);
    }

    [Fact]
    public void Translator_stops_on_unreadable_memory()
    {
        var image = Image(new MipsProgram().Nop(), bigEndian: true);

        var block = new MipsTranslator().TranslateBlock(image, Endianness.Big, 0x10000000);

        var stop = Assert.IsType<IrStop>(block.Terminator);
        Assert.Contains("unmapped", stop.Reason);
    }

    [Fact]
    public void Decoder_produces_readable_assembly()
    {
        var p = new MipsProgram()
            .Addiu(Reg.T0, Reg.Zero, 10)
            .Lw(Reg.V0, 4, Reg.Sp)
            .Sw(Reg.A0, -8, Reg.Sp)
            .Addu(Reg.A0, Reg.T0, Reg.T1)
            .Sll(Reg.T1, Reg.T0, 2)
            .Label("l")
            .Beq(Reg.T0, Reg.T1, "l")
            .Nop()
            .Jal("l")
            .Jr(Reg.Ra)
            .Syscall()
            .Word(0x46000000);
        var image = Image(p, bigEndian: true);
        var decoder = new MipsDecoder();
        var text = Enumerable.Range(0, 11).Select(i => decoder.Decode(image, Endianness.Big, SampleFiles.CodeBase + (ulong)(i * 4))!).ToList();

        Assert.Equal("addiu $t0, $zero, 10", $"{text[0].Mnemonic} {text[0].Operands}");
        Assert.Equal("lw $v0, 4($sp)", $"{text[1].Mnemonic} {text[1].Operands}");
        Assert.Equal("sw $a0, -8($sp)", $"{text[2].Mnemonic} {text[2].Operands}");
        Assert.Equal("addu $a0, $t0, $t1", $"{text[3].Mnemonic} {text[3].Operands}");
        Assert.Equal("sll $t1, $t0, 2", $"{text[4].Mnemonic} {text[4].Operands}");
        Assert.Equal(InstructionKind.Branch, text[5].Kind);
        Assert.True(text[5].HasDelaySlot);
        Assert.Equal(SampleFiles.CodeBase + 5 * 4, text[5].Target);
        Assert.Equal("nop", text[6].Mnemonic);
        Assert.Equal(InstructionKind.Call, text[7].Kind);
        Assert.Equal(InstructionKind.JumpRegister, text[8].Kind);
        Assert.Equal(InstructionKind.Syscall, text[9].Kind);
        Assert.Equal(InstructionKind.Unknown, text[10].Kind);
        Assert.Equal(".word", text[10].Mnemonic);
        Assert.False(text[10].IsKnown);
        Assert.Null(decoder.Decode(image, Endianness.Big, 0x10));
    }

    [Fact]
    public void Decoder_reads_both_byte_orders_identically()
    {
        var program = SampleFiles.SumProgram();
        var big = Image(program, true);
        var little = Image(program, false);
        var decoder = new MipsDecoder();

        for (ulong i = 0; i < (ulong)program.Count; i++)
        {
            var a = decoder.Decode(big, Endianness.Big, SampleFiles.CodeBase + (i * 4))!;
            var b = decoder.Decode(little, Endianness.Little, SampleFiles.CodeBase + (i * 4))!;
            Assert.Equal(a.Raw, b.Raw);
            Assert.Equal(a.Mnemonic, b.Mnemonic);
        }
    }
}

public class LoaderAndPluginTests
{
    [Fact]
    public void Loader_maps_segments_and_zero_fills_the_bss_part()
    {
        var builder = new ElfBuilder { Machine = 8 };
        builder.Segments.Add(new SegmentSpec { Data = [1, 2, 3, 4], MemorySize = 16, VirtualAddress = 0x1000, Flags = 6 });
        var bytes = builder.Build();
        var elf = T.ParseOk(bytes);
        using var stream = new MemoryStream(bytes);

        var image = new ElfImageLoader().Load(stream, elf);

        var segment = Assert.Single(image.Segments);
        Assert.Equal(16, segment.Data.Length);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 0, 0, 0, 0 }, segment.Data[..8]);
        Assert.True(segment.Writable);
        Assert.False(segment.Executable);
        Assert.Equal(CpuFamily.Mips, image.Family);
    }

    [Fact]
    public void Loader_refuses_oversized_overlapping_and_out_of_file_segments()
    {
        var huge = new ElfBuilder { Machine = 8 };
        huge.Segments.Add(new SegmentSpec { Data = [1], MemorySize = 1UL << 40 });
        var overlap = new ElfBuilder { Machine = 8 };
        overlap.WithCode(new byte[64], 0x1000).WithData(new byte[64], 0x1020);
        var outside = new ElfBuilder { Machine = 8 };
        outside.WithCode(new byte[64], 0x1000);
        var outsideBytes = outside.Build();
        BitConverter.GetBytes(0x10000000u).CopyTo(outsideBytes, 52 + 4); // p_offset (ELF32) of the first segment

        foreach (var bytes in new[] { huge.Build(), overlap.Build(), outsideBytes })
        {
            var elf = T.ParseOk(bytes);
            using var stream = new MemoryStream(bytes);
            Assert.Throws<BinaryLoadException>(() => new ElfImageLoader().Load(stream, elf));
        }
    }

    [Fact]
    public void Loader_requires_loadable_segments()
    {
        var builder = new ElfBuilder { Machine = 8 };
        var bytes = builder.Build();
        var elf = T.ParseOk(bytes);
        using var stream = new MemoryStream(bytes);

        Assert.False(new ElfImageLoader().CanLoad(elf));
        Assert.Throws<BinaryLoadException>(() => new ElfImageLoader().Load(stream, elf));
    }

    [Fact]
    public void Stream_reader_reads_through_the_load_mapping_without_loading_segments()
    {
        var builder = new ElfBuilder { Machine = 8 };
        builder.Segments.Add(new SegmentSpec { Data = [0xAA, 0xBB, 0xCC, 0xDD], MemorySize = 8, VirtualAddress = 0x2000, Flags = 5 });
        var bytes = builder.Build();
        var elf = T.ParseOk(bytes);
        using var stream = new MemoryStream(bytes);
        var reader = new StreamImageReader(stream, elf);

        var buffer = new byte[6];
        Assert.True(reader.TryRead(0x2002, buffer));
        Assert.Equal(new byte[] { 0xCC, 0xDD, 0, 0, 0, 0 }, buffer);
        Assert.False(reader.TryRead(0x2004, new byte[8]));   // runs past the segment
        Assert.False(reader.TryRead(0x1FFF, new byte[2]));   // starts before it
        Assert.False(reader.TryRead(0x9000, new byte[1]));   // not mapped
    }

    [Fact]
    public void Registry_offers_the_mips_plugin_only_for_matching_files()
    {
        var mips = T.ParseOk(SampleFiles.Mips32Elf(SampleFiles.SumProgram(), true));
        var x64 = T.ParseOk(ElfBuilder.X64Linux().WithCode([0xC3]).Build());
        var mips64 = T.ParseOk(new ElfBuilder { Is64 = true, Machine = 8 }.WithCode([0, 0, 0, 0]).Build());

        var registry = RecompilerPluginRegistry.Default;

        Assert.Single(registry.FindSupporting(mips));
        Assert.Empty(registry.FindSupporting(x64));
        Assert.Empty(registry.FindSupporting(mips64));
        var plugin = registry.FindSupporting(mips)[0];
        Assert.Equal(PluginCapabilities.Loader | PluginCapabilities.Decoder | PluginCapabilities.Translator | PluginCapabilities.Runtime | PluginCapabilities.Backend, plugin.Capabilities);
        Assert.False(plugin.CreateBackend()!.IsNative);
        Assert.Contains("No FPU", plugin.Description);
    }

    private sealed class DummyPlugin(string id, bool throwOnSupports = false) : IRecompilerPlugin
    {
        public string Id => id;

        public string DisplayName => "Dummy";

        public string Description => "";

        public PluginCapabilities Capabilities => PluginCapabilities.None;

        public bool Supports(ElfFile elf) => throwOnSupports ? throw new InvalidOperationException("broken plugin") : true;

        public IBinaryLoader? CreateLoader() => null;

        public IArchitectureDecoder? CreateDecoder() => null;

        public ICodeTranslator? CreateTranslator() => null;

        public IRuntimeCompatibilityLayer? CreateRuntime() => null;

        public ICodeBackend? CreateBackend() => null;
    }

    [Fact]
    public void Registry_accepts_new_plugins_rejects_duplicates_and_survives_broken_ones()
    {
        var registry = new RecompilerPluginRegistry();
        var elf = T.ParseOk(ElfBuilder.X64Linux().WithCode([0xC3]).Build());

        registry.Register(new DummyPlugin("a"));
        registry.Register(new DummyPlugin("broken", throwOnSupports: true));

        Assert.Throws<InvalidOperationException>(() => registry.Register(new DummyPlugin("a")));
        Assert.Equal(["a"], registry.FindSupporting(elf).Select(p => p.Id));
    }

    [Fact]
    public void Entry_point_preview_decodes_the_start_of_the_program()
    {
        var bytes = SampleFiles.Mips32Elf(SampleFiles.SumProgram(), true);
        var elf = T.ParseOk(bytes);
        using var stream = new MemoryStream(bytes);

        var preview = EntryPointPreview.Create(elf, stream, maxInstructions: 8)!;

        Assert.Equal(8, preview.Instructions.Count);
        Assert.Equal("addiu", preview.Instructions[0].Mnemonic);
        Assert.Equal(SampleFiles.CodeBase, preview.Instructions[0].Address);
        Assert.Equal(0, preview.UnknownCount);
        Assert.Null(preview.Note);
    }

    [Fact]
    public void Entry_point_preview_flags_words_outside_the_supported_subset()
    {
        var program = new MipsProgram().Nop().Word(0x46000000).Word(0x70000000);
        var bytes = SampleFiles.Mips32Elf(program, false);
        var elf = T.ParseOk(bytes);
        using var stream = new MemoryStream(bytes);

        var preview = EntryPointPreview.Create(elf, stream)!;

        Assert.Equal(2, preview.UnknownCount);
        Assert.Contains("outside the supported instruction subset", preview.Note);
    }

    [Fact]
    public void Entry_point_preview_is_not_offered_without_a_decoder_or_executable_entry()
    {
        var x64Bytes = ElfBuilder.X64Linux().WithCode([0xC3]).Build();
        var x64 = T.ParseOk(x64Bytes);
        var badEntry = new ElfBuilder { Machine = 8, Entry = 0xDEAD0000 };
        badEntry.WithCode(new byte[16], 0x400000);
        var badBytes = badEntry.Build();
        var bad = T.ParseOk(badBytes);

        using var s1 = new MemoryStream(x64Bytes);
        using var s2 = new MemoryStream(badBytes);

        Assert.Null(EntryPointPreview.Create(x64, s1));
        var none = EntryPointPreview.Create(bad, s2)!;
        Assert.Empty(none.Instructions);
        Assert.Contains("No preview", none.Note);
    }
}
