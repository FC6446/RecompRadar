using RecompRadar.BinaryAnalysis.Elf;
using RecompRadar.Core;
using RecompRadar.Core.Model;
using RecompRadar.TestSupport;

namespace RecompRadar.Tests;

public class ElfParserTests
{
    public static IEnumerable<object[]> Layouts()
    {
        // machine, is64, bigEndian, expected machine name fragment
        yield return [(ushort)3, false, false, "80386"];
        yield return [(ushort)62, true, false, "x86-64"];
        yield return [(ushort)8, false, false, "MIPS"];
        yield return [(ushort)8, false, true, "MIPS"];
        yield return [(ushort)8, true, true, "MIPS"];
        yield return [(ushort)20, false, true, "PowerPC"];
        yield return [(ushort)21, true, true, "PowerPC 64"];
        yield return [(ushort)40, false, false, "ARM"];
        yield return [(ushort)183, true, false, "AArch64"];
        yield return [(ushort)243, true, false, "RISC-V"];
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Parses_header_for_every_class_endianness_and_machine(ushort machine, bool is64, bool big, string machineNameFragment)
    {
        var bytes = new ElfBuilder { Machine = machine, Is64 = is64, BigEndian = big, Flags = 0x1234 }
            .WithCode([1, 2, 3, 4, 5, 6, 7, 8], 0x400000).Build();

        var elf = T.ParseOk(bytes);

        Assert.Equal(is64 ? ElfClass.Elf64 : ElfClass.Elf32, elf.Header.Class);
        Assert.Equal(big ? Endianness.Big : Endianness.Little, elf.Header.Endianness);
        Assert.Equal(machine, elf.Header.Machine);
        Assert.Contains(machineNameFragment, elf.Header.MachineName);
        Assert.Equal(2, elf.Header.Type);
        Assert.Equal(0x400000UL, elf.Header.Entry);
        Assert.Equal(0x1234u, elf.Header.Flags);
        Assert.Equal(1u, elf.Header.Version);
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Parses_segments_and_sections_for_every_layout(ushort machine, bool is64, bool big, string _)
    {
        var code = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var bytes = new ElfBuilder { Machine = machine, Is64 = is64, BigEndian = big }
            .WithCode(code, 0x400000)
            .WithData([9, 9, 9], 0x500000)
            .Build();

        var elf = T.ParseOk(bytes);

        Assert.Equal(2, elf.Segments.Count);
        var text = elf.Segments[0];
        Assert.Equal(1u, text.Type);
        Assert.Equal("PT_LOAD", text.TypeName);
        Assert.True(text.IsReadable);
        Assert.False(text.IsWritable);
        Assert.True(text.IsExecutable);
        Assert.Equal(0x400000UL, text.VirtualAddress);
        Assert.Equal(4UL, text.FileSize);
        Assert.Equal("R-X", text.Permissions);

        Assert.Contains(elf.Sections, s => s.Name == ".text" && s.IsExecutable && s.IsAllocated && s.Address == 0x400000);
        Assert.Contains(elf.Sections, s => s.Name == ".rodata" && !s.IsExecutable);
        Assert.Contains(elf.Sections, s => s.Name == ".shstrtab" && s.Type == 3);
        Assert.Equal(".text", elf.Sections[1].Name);

        // The bytes referenced by the executable segment must really be the code that was written.
        Assert.Equal(code, bytes.AsSpan((int)text.Offset, 4).ToArray());
        Assert.Contains(elf.ExecutableRegions, r => r.Source == "segment" && r.VirtualAddress == 0x400000);
        Assert.DoesNotContain(elf.ExecutableRegions, r => r.VirtualAddress == 0x500000);
        Assert.Equal(true, elf.EntryPointInExecutableRegion);
        Assert.Contains(".text", elf.EntryPointLocation);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Reads_symbols_with_binding_type_and_import_flag(bool is64, bool big)
    {
        var builder = new ElfBuilder { Is64 = is64, BigEndian = big, Machine = 8 };
        builder.WithCode([0, 0, 0, 0], 0x400000);
        builder.Symbols.AddRange(
        [
            SymbolSpec.Function("main", 0x400000, 4),
            SymbolSpec.Object("counter", 0x500000, 4),
            SymbolSpec.Import("printf"),
            new SymbolSpec { Name = "local_helper", Value = 0x400002, Binding = 0 },
        ]);

        var elf = T.ParseOk(builder.Build());

        Assert.True(elf.HasSymbolTable);
        Assert.False(elf.HasDynamicSymbolTable);
        Assert.Equal(4, elf.Symbols.Count);
        var main = Assert.Single(elf.Symbols, s => s.Name == "main");
        Assert.True(main.IsFunction);
        Assert.False(main.IsUndefined);
        Assert.Equal("GLOBAL", main.BindingName);
        Assert.Equal(0x400000UL, main.Value);
        Assert.Equal(4UL, main.Size);
        Assert.Equal("symtab", main.Table);
        Assert.Equal("OBJECT", elf.Symbols.Single(s => s.Name == "counter").TypeName);
        Assert.Equal("LOCAL", elf.Symbols.Single(s => s.Name == "local_helper").BindingName);

        var import = elf.Symbols.Single(s => s.Name == "printf");
        Assert.True(import.IsImport);
        Assert.Equal(1, elf.Symbols.Count(s => s.IsImport));
    }

    [Fact]
    public void Reads_dynamic_dependencies_from_sections()
    {
        var builder = ElfBuilder.X64Linux();
        builder.Interpreter = "/lib64/ld-linux-x86-64.so.2";
        builder.NeededLibraries.AddRange(["libc.so.6", "libm.so.6"]);
        builder.SharedObjectName = "libfoo.so.1";
        builder.DynamicSymbols.AddRange([SymbolSpec.Import("puts"), SymbolSpec.Function("exported_fn", 0x400000)]);
        builder.WithCode([0xC3]);

        var elf = T.ParseOk(builder.Build());

        Assert.True(elf.Dynamic.HasDynamicSection);
        Assert.True(elf.Dynamic.HasDynamicSegment);
        Assert.Equal(["libc.so.6", "libm.so.6"], elf.Dynamic.NeededLibraries);
        Assert.Equal("libfoo.so.1", elf.Dynamic.SharedObjectName);
        Assert.Equal("/lib64/ld-linux-x86-64.so.2", elf.Dynamic.Interpreter);
        Assert.True(elf.HasDynamicSymbolTable);
        Assert.Contains(elf.Symbols, s => s.Name == "puts" && s.IsImport && s.Table == "dynsym");
        Assert.Contains(elf.Dynamic.Entries, e => e.TagName == "DT_NEEDED" && e.StringValue == "libc.so.6");
    }

    [Fact]
    public void Finds_dynamic_libraries_even_without_section_headers()
    {
        var builder = ElfBuilder.X64Linux();
        builder.NeededLibraries.AddRange(["libz.so.1"]);
        builder.OmitSectionHeaders = true;
        builder.WithCode([0xC3]);

        var elf = T.ParseOk(builder.Build());

        Assert.False(elf.HasSectionHeaders);
        Assert.Empty(elf.Sections);
        Assert.True(elf.HasProgramHeaders);
        Assert.True(elf.Dynamic.HasDynamicSegment);
        Assert.False(elf.Dynamic.HasDynamicSection);
        Assert.Equal(["libz.so.1"], elf.Dynamic.NeededLibraries);
    }

    [Fact]
    public void Reports_missing_symbol_tables_without_failing()
    {
        var elf = T.ParseOk(ElfBuilder.X64Linux().WithCode([0xC3]).Build());

        Assert.False(elf.HasSymbolTable);
        Assert.False(elf.HasDynamicSymbolTable);
        Assert.Empty(elf.Symbols);
        Assert.False(elf.Dynamic.HasDynamicSection);
        Assert.Empty(elf.Dynamic.NeededLibraries);
    }

    [Fact]
    public void Handles_file_with_only_a_header()
    {
        var builder = ElfBuilder.X64Linux();
        builder.OmitSectionHeaders = true;
        var result = T.Parse(builder.Build());

        Assert.True(result.Success);
        Assert.True(T.Has(result.Diagnostics, "ELF.NO_TABLES"));
    }

    [Fact]
    public void Maps_vendor_specific_type_and_osabi_without_failing()
    {
        var bytes = new ElfBuilder { Is64 = true, Machine = 62, OsAbi = 9, Type = 0xFE10 }.WithCode([0xC3]).Build();

        var elf = T.ParseOk(bytes);

        Assert.Equal(0xFE10, elf.Header.Type);
        Assert.Contains("OS-specific", elf.Header.TypeName);
        Assert.Equal("FreeBSD", elf.Header.OsAbiName);
    }

    // ---- invalid input ----------------------------------------------------------------------------------------

    [Fact]
    public void Rejects_empty_input()
    {
        var result = T.Parse([]);

        Assert.False(result.Success);
        Assert.True(T.Has(result.Diagnostics, "ELF.TOO_SMALL"));
        Assert.All(result.Diagnostics, d => Assert.Equal(Severity.Error, d.Severity));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    public void Rejects_input_shorter_than_identification(int length)
    {
        var result = T.Parse(new byte[length]);

        Assert.False(result.Success);
        Assert.True(T.Has(result.Diagnostics, "ELF.TOO_SMALL"));
    }

    [Fact]
    public void Rejects_wrong_magic_with_helpful_explanation()
    {
        var result = T.Parse(System.Text.Encoding.ASCII.GetBytes("MZ this is not an ELF file at all"));

        Assert.False(result.Success);
        var d = Assert.Single(result.Diagnostics, x => x.Code == "ELF.BAD_MAGIC");
        Assert.False(string.IsNullOrWhiteSpace(d.Explanation));
    }

    [Theory]
    [InlineData(0, "ELF.BAD_CLASS")]
    [InlineData(3, "ELF.BAD_CLASS")]
    [InlineData(255, "ELF.BAD_CLASS")]
    public void Rejects_invalid_class(byte value, string code)
    {
        var bytes = ElfBuilder.X64Linux().WithCode([0xC3]).Build();
        bytes[4] = value;

        Assert.True(T.Has(T.Parse(bytes).Diagnostics, code));
        Assert.False(T.Parse(bytes).Success);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Rejects_invalid_endianness(byte value)
    {
        var bytes = ElfBuilder.X64Linux().WithCode([0xC3]).Build();
        bytes[5] = value;

        var result = T.Parse(bytes);

        Assert.False(result.Success);
        Assert.True(T.Has(result.Diagnostics, "ELF.BAD_DATA"));
    }

    [Theory]
    [InlineData(false, 51)]
    [InlineData(true, 63)]
    [InlineData(true, 20)]
    public void Rejects_truncated_header(bool is64, int length)
    {
        var bytes = new ElfBuilder { Is64 = is64 }.WithCode([0xC3]).Build();

        var result = T.Parse(bytes[..length]);

        Assert.False(result.Success);
        Assert.True(T.Has(result.Diagnostics, "ELF.TRUNCATED_HEADER"));
    }

    [Fact]
    public void Survives_truncation_at_every_possible_length()
    {
        foreach (var sample in SampleFiles.All().Where(s => s.FileName.EndsWith(".elf", StringComparison.Ordinal)))
        {
            for (var length = 0; length <= sample.Bytes.Length; length++)
            {
                var result = T.Parse(sample.Bytes[..length]);

                Assert.DoesNotContain(result.Diagnostics, d => d.Code == "ELF.INTERNAL_ERROR");
                if (length < 16)
                {
                    Assert.False(result.Success);
                }
            }
        }
    }

    [Fact]
    public void Reports_program_header_offset_outside_the_file()
    {
        var bytes = ElfBuilder.X64Linux().WithCode(new byte[32]).Build();
        BitConverter.GetBytes(ulong.MaxValue - 8).CopyTo(bytes, 0x20); // e_phoff

        var result = T.Parse(bytes);

        Assert.True(result.Success);
        Assert.Empty(result.Elf!.Segments);
        Assert.True(T.Has(result.Diagnostics, "ELF.PH_TABLE"));
    }

    [Fact]
    public void Reports_section_header_offset_overflowing_the_address_space()
    {
        var bytes = ElfBuilder.X64Linux().WithCode(new byte[32]).Build();
        BitConverter.GetBytes(ulong.MaxValue).CopyTo(bytes, 0x28); // e_shoff

        var result = T.Parse(bytes);

        Assert.True(result.Success);
        Assert.Empty(result.Elf!.Sections);
        Assert.True(T.Has(result.Diagnostics, "ELF.SH_TABLE"));
    }

    [Fact]
    public void Clamps_enormous_header_counts_to_what_fits_in_the_file()
    {
        var bytes = ElfBuilder.X64Linux().WithCode(new byte[32]).Build();
        BitConverter.GetBytes((ushort)0xFFFE).CopyTo(bytes, 0x38); // e_phnum
        BitConverter.GetBytes((ushort)0xFFFE).CopyTo(bytes, 0x3C); // e_shnum

        var result = T.Parse(bytes);

        Assert.True(result.Success);
        Assert.True(result.Elf!.Segments.Count < 100);
        Assert.True(T.Has(result.Diagnostics, "ELF.PH_TABLE"));
        Assert.True(T.Has(result.Diagnostics, "ELF.SH_TABLE"));
    }

    [Fact]
    public void Ignores_entry_sizes_that_are_too_small()
    {
        var bytes = ElfBuilder.X64Linux().WithCode(new byte[32]).Build();
        BitConverter.GetBytes((ushort)8).CopyTo(bytes, 0x36); // e_phentsize

        var result = T.Parse(bytes);

        Assert.True(result.Success);
        Assert.Empty(result.Elf!.Segments);
        Assert.True(T.Has(result.Diagnostics, "ELF.PH_TABLE"));
    }

    [Fact]
    public void Warns_about_segment_extending_beyond_the_file()
    {
        var bytes = ElfBuilder.X64Linux().WithCode(new byte[32]).Build();
        BitConverter.GetBytes(ulong.MaxValue / 2).CopyTo(bytes, 64 + 32); // p_filesz of the first program header
        BitConverter.GetBytes(ulong.MaxValue / 2).CopyTo(bytes, 64 + 40); // p_memsz

        var result = T.Parse(bytes);

        Assert.True(result.Success);
        Assert.True(T.Has(result.Diagnostics, "ELF.SEGMENT_RANGE"));
        Assert.All(result.Diagnostics.Where(d => d.Code == "ELF.SEGMENT_RANGE"), d => Assert.False(string.IsNullOrWhiteSpace(d.Explanation)));
    }

    [Fact]
    public void Warns_about_section_with_invalid_offset_and_size()
    {
        var bytes = ElfBuilder.X64Linux().WithCode(new byte[32]).Build();
        var shoff = (int)BitConverter.ToUInt64(bytes, 0x28);
        // Section 1 (.text): sh_offset at +24, sh_size at +32.
        // offset + size wraps around to a small number: only an overflow-safe range check notices that this is outside the file.
        BitConverter.GetBytes(ulong.MaxValue - 4).CopyTo(bytes, shoff + 64 + 24);
        BitConverter.GetBytes(16UL).CopyTo(bytes, shoff + 64 + 32);

        var result = T.Parse(bytes);

        Assert.True(result.Success);
        Assert.True(T.Has(result.Diagnostics, "ELF.SECTION_RANGE"));
    }

    [Fact]
    public void Does_not_wrap_around_when_a_segment_offset_is_near_the_end_of_the_address_space()
    {
        var bytes = ElfBuilder.X64Linux().WithCode(new byte[32]).Build();
        BitConverter.GetBytes(ulong.MaxValue - 4).CopyTo(bytes, 64 + 8);  // p_offset of the first program header
        BitConverter.GetBytes(16UL).CopyTo(bytes, 64 + 32);               // p_filesz
        BitConverter.GetBytes(16UL).CopyTo(bytes, 64 + 40);               // p_memsz

        var result = T.Parse(bytes);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "ELF.INTERNAL_ERROR");
        Assert.True(T.Has(result.Diagnostics, "ELF.SEGMENT_RANGE"));
    }

    [Fact]
    public void Does_not_wrap_around_when_the_section_name_table_lies_near_the_end_of_the_address_space()
    {
        var bytes = ElfBuilder.X64Linux().WithCode(new byte[32]).Build();
        var shoff = (int)BitConverter.ToUInt64(bytes, 0x28);
        var shstrndx = BitConverter.ToUInt16(bytes, 0x3E);
        var header = shoff + (shstrndx * 64);
        BitConverter.GetBytes(ulong.MaxValue - 4).CopyTo(bytes, header + 24); // sh_offset of .shstrtab
        BitConverter.GetBytes(16UL).CopyTo(bytes, header + 32);               // sh_size

        var result = T.Parse(bytes);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "ELF.INTERNAL_ERROR");
        Assert.True(T.Has(result.Diagnostics, "ELF.STRTAB_RANGE"));
        Assert.All(result.Elf!.Sections, s => Assert.Equal(string.Empty, s.Name)); // names could not be read, nothing invented
    }

    [Fact]
    public void Resolves_extended_section_count_stored_in_section_zero()
    {
        var bytes = ElfBuilder.X64Linux().WithCode(new byte[32]).WithData([1, 2], 0x500000).Build();
        var original = T.ParseOk(bytes);
        var shoff = (int)BitConverter.ToUInt64(bytes, 0x28);
        BitConverter.GetBytes((ushort)0).CopyTo(bytes, 0x3C); // e_shnum = 0
        BitConverter.GetBytes((ulong)original.Sections.Count).CopyTo(bytes, shoff + 32); // sh_size of section 0

        var elf = T.ParseOk(bytes);

        Assert.Equal(original.Sections.Count, elf.Sections.Count);
        Assert.Equal(original.Header.SectionHeaderCount, elf.Header.SectionHeaderCount);
    }

    [Fact]
    public void Warns_when_entry_point_is_not_mapped_or_not_executable()
    {
        var unmapped = ElfBuilder.X64Linux();
        unmapped.Entry = 0xDEAD0000;
        unmapped.WithCode([0xC3]);
        var data = ElfBuilder.X64Linux();
        data.Entry = 0x500000;
        data.WithCode([0xC3]).WithData([1, 2, 3, 4], 0x500000);

        var unmappedResult = T.Parse(unmapped.Build());
        var dataResult = T.Parse(data.Build());

        Assert.True(T.Has(unmappedResult.Diagnostics, "ELF.ENTRY_UNMAPPED"));
        Assert.Null(unmappedResult.Elf!.EntryPointLocation);
        Assert.True(T.Has(dataResult.Diagnostics, "ELF.ENTRY_NOT_EXECUTABLE"));
        Assert.Equal(false, dataResult.Elf!.EntryPointInExecutableRegion);
    }

    [Fact]
    public void Warns_about_overlapping_load_segments()
    {
        var builder = ElfBuilder.X64Linux();
        builder.WithCode(new byte[0x100], 0x400000).WithData(new byte[0x100], 0x400080);

        var result = T.Parse(builder.Build());

        Assert.True(T.Has(result.Diagnostics, "ELF.LOAD_OVERLAP"));
    }

    [Fact]
    public void Warns_when_file_size_exceeds_memory_size_of_load_segment()
    {
        var builder = ElfBuilder.X64Linux();
        builder.Segments.Add(new SegmentSpec { Data = new byte[64], MemorySize = 16, SectionName = ".text" });

        var result = T.Parse(builder.Build());

        Assert.True(T.Has(result.Diagnostics, "ELF.SEGMENT_SIZE"));
    }

    [Fact]
    public void Warns_when_load_segment_offset_and_address_are_not_congruent_modulo_the_alignment()
    {
        var builder = ElfBuilder.X64Linux();
        builder.Segments.Add(new SegmentSpec { Data = new byte[32], VirtualAddress = 0x400008, Align = 0x1000, SectionName = ".text" });

        var result = T.Parse(builder.Build());

        Assert.True(T.Has(result.Diagnostics, "ELF.SEGMENT_ALIGN"));
    }

    [Fact]
    public void Well_formed_files_produce_no_warnings()
    {
        var builder = ElfBuilder.X64Linux();
        builder.NeededLibraries.Add("libc.so.6");
        builder.Interpreter = "/lib64/ld-linux-x86-64.so.2";
        builder.Symbols.Add(SymbolSpec.Function("main", 0x400000, 1));
        builder.DynamicSymbols.Add(SymbolSpec.Import("puts"));
        builder.WithCode([0xC3]).WithData([1, 2, 3], 0x500000);

        var result = T.Parse(builder.Build());

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity >= Severity.Warning);
    }

    [Fact]
    public void Truncates_symbol_tables_that_exceed_the_configured_limit()
    {
        var builder = ElfBuilder.X64Linux();
        builder.WithCode([0xC3]);
        for (var i = 0; i < 500; i++)
        {
            builder.Symbols.Add(SymbolSpec.Function($"fn_{i}", 0x400000));
        }

        var elf = T.ParseOk(builder.Build(), AnalysisLimits.Default with { MaxSymbolsPerTable = 100 });

        Assert.True(elf.SymbolsTruncated);
        Assert.True(elf.Symbols.Count <= 100);
        Assert.Equal(501, elf.SymbolTableEntryCount); // 500 symbols plus the mandatory null entry, counted even though only some are read
    }

    [Fact]
    public void Handles_unterminated_string_table_entries()
    {
        var builder = ElfBuilder.X64Linux();
        builder.WithCode([0xC3]);
        builder.Symbols.Add(SymbolSpec.Function("last_symbol_name", 0x400000));
        var bytes = builder.Build();
        var elfBefore = T.ParseOk(bytes);
        var strtab = elfBefore.Sections.Single(s => s.Name == ".strtab");
        // Overwrite the final NUL of the string table with a letter so the last name runs off the end.
        bytes[(int)(strtab.Offset + strtab.Size - 1)] = (byte)'X';

        var elf = T.ParseOk(bytes);

        Assert.Contains(elf.Symbols, s => s.Name.StartsWith("last_symbol_name", StringComparison.Ordinal));
    }

    [Fact]
    public void Replaces_control_characters_in_names()
    {
        var builder = ElfBuilder.X64Linux();
        builder.WithCode([0xC3]);
        builder.Symbols.Add(SymbolSpec.Function("bad\u0007name\u001b[31m", 0x400000));

        var elf = T.ParseOk(builder.Build());

        Assert.Contains(elf.Symbols, s => s.Name == "bad?name?[31m");
    }

    [Fact]
    public void Ignores_trailing_data_after_the_last_structure()
    {
        var bytes = ElfBuilder.X64Linux().WithCode([0xC3]).Build();
        var padded = bytes.Concat(Enumerable.Repeat((byte)0xFF, 4096)).ToArray();

        var elf = T.ParseOk(padded);

        Assert.Equal(1, elf.Segments.Count(s => s.Type == 1));
    }

    [Fact]
    public void Random_mutations_never_crash_the_parser()
    {
        var random = new Random(20261030);
        var samples = SampleFiles.All().Where(s => s.FileName.EndsWith(".elf", StringComparison.Ordinal)).ToList();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < 3000; i++)
        {
            var sample = samples[i % samples.Count];
            var data = (byte[])sample.Bytes.Clone();
            var flips = random.Next(1, 24);
            for (var f = 0; f < flips; f++)
            {
                // Bias mutations toward the headers, where they matter most.
                var position = random.Next(0, 3) == 0 ? random.Next(data.Length) : random.Next(Math.Min(data.Length, 160));
                data[position] = (byte)random.Next(256);
            }

            if (random.Next(5) == 0)
            {
                data = data[..random.Next(data.Length + 1)];
            }

            var result = T.Parse(data);

            Assert.DoesNotContain(result.Diagnostics, d => d.Code == "ELF.INTERNAL_ERROR");
        }

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), $"Mutation run took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void Cancellation_is_honoured()
    {
        var bytes = ElfBuilder.X64Linux().WithCode([0xC3]).Build();
        using var stream = new MemoryStream(bytes);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => new ElfParser().Parse(stream, cts.Token));
    }

    [Fact]
    public void Requires_a_seekable_stream()
    {
        using var stream = new NonSeekableStream(new byte[100]);

        var result = new ElfParser().Parse(stream);

        Assert.False(result.Success);
        Assert.True(T.Has(result.Diagnostics, "ELF.STREAM"));
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
