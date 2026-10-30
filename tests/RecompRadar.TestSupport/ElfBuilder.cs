using System.Text;

namespace RecompRadar.TestSupport;

public sealed record SegmentSpec
{
    public uint Type { get; init; } = 1;
    public uint Flags { get; init; } = 5;
    public ulong VirtualAddress { get; init; } = 0x400000;
    public byte[] Data { get; init; } = [];
    public ulong? MemorySize { get; init; }
    public ulong Align { get; init; } = 16;

    /// <summary>When set, a section with this name covering the segment data is added.</summary>
    public string? SectionName { get; init; }
}

public sealed record SymbolSpec
{
    public string Name { get; init; } = "";
    public ulong Value { get; init; }
    public ulong Size { get; init; }
    public byte Type { get; init; } = 2;
    public byte Binding { get; init; } = 1;
    public ushort SectionIndex { get; init; } = 1;

    public static SymbolSpec Function(string name, ulong value = 0, ulong size = 0) => new() { Name = name, Value = value, Size = size };

    public static SymbolSpec Object(string name, ulong value = 0, ulong size = 0) => new() { Name = name, Value = value, Size = size, Type = 1 };

    public static SymbolSpec Import(string name) => new() { Name = name, SectionIndex = 0, Type = 0 };
}

/// <summary>
/// Builds small, valid ELF files (ELF32/ELF64, little/big endian, any machine) for tests and samples.
/// The files are synthetic: they contain only the bytes the test supplies and no third-party code.
/// </summary>
public sealed class ElfBuilder
{
    private const ulong DynamicBase = 0x00600000;

    public bool Is64 { get; set; }

    public bool BigEndian { get; set; }

    public ushort Machine { get; set; } = 62;

    public ushort Type { get; set; } = 2;

    public byte OsAbi { get; set; }

    public byte AbiVersion { get; set; }

    public uint Flags { get; set; }

    public ulong? Entry { get; set; }

    public List<SegmentSpec> Segments { get; } = [];

    public List<SymbolSpec> Symbols { get; } = [];

    public List<SymbolSpec> DynamicSymbols { get; } = [];

    public List<string> NeededLibraries { get; } = [];

    public string? Interpreter { get; set; }

    public string? SharedObjectName { get; set; }

    /// <summary>Do not write a section header table (e_shnum = 0), as in a stripped file.</summary>
    public bool OmitSectionHeaders { get; set; }

    public static ElfBuilder X64Linux() => new() { Is64 = true, Machine = 62 };

    public ElfBuilder WithCode(byte[] code, ulong address = 0x400000, uint flags = 5)
    {
        Segments.Add(new SegmentSpec { Data = code, VirtualAddress = address, Flags = flags, SectionName = ".text" });
        return this;
    }

    public ElfBuilder WithData(byte[] data, ulong address = 0x500000, string name = ".rodata", uint flags = 4)
    {
        Segments.Add(new SegmentSpec { Data = data, VirtualAddress = address, Flags = flags, SectionName = name });
        return this;
    }

    public byte[] Build()
    {
        var ehSize = Is64 ? 64 : 52;
        var phSize = Is64 ? 56 : 32;
        var shSize = Is64 ? 64 : 40;
        var symSize = Is64 ? 24 : 16;
        var dynEntrySize = Is64 ? 16 : 8;

        var hasDynamic = NeededLibraries.Count > 0 || DynamicSymbols.Count > 0 || SharedObjectName is not null || Interpreter is not null;

        // ---- dynamic block (dynstr, dynsym, dynamic) ----
        byte[] dynstr = [], dynsym = [], dynamic = [];
        ulong dynstrRel = 0, dynsymRel = 0, dynamicRel = 0, dynBlockLength = 0;
        if (hasDynamic)
        {
            var strings = new StringTable();
            var neededOffsets = NeededLibraries.Select(strings.Add).ToList();
            var sonameOffset = SharedObjectName is null ? 0 : strings.Add(SharedObjectName);
            dynsym = BuildSymbolTable(DynamicSymbols, strings, symSize);
            dynstr = strings.ToBytes();

            dynstrRel = 0;
            dynsymRel = Align((ulong)dynstr.Length, 8);
            dynamicRel = Align(dynsymRel + (ulong)dynsym.Length, 8);

            var entries = new Writer(BigEndian, Is64);
            foreach (var neededNameOffset in neededOffsets)
            {
                entries.Dyn(1, (ulong)neededNameOffset);
            }

            if (SharedObjectName is not null)
            {
                entries.Dyn(14, (ulong)sonameOffset);
            }

            entries.Dyn(5, DynamicBase + dynstrRel);
            entries.Dyn(10, (ulong)dynstr.Length);
            entries.Dyn(6, DynamicBase + dynsymRel);
            entries.Dyn(11, (ulong)symSize);
            entries.Dyn(0, 0);
            dynamic = entries.ToArray();
            dynBlockLength = dynamicRel + (ulong)dynamic.Length;
        }

        // ---- symbol table ----
        var symStrings = new StringTable();
        var symtab = Symbols.Count > 0 ? BuildSymbolTable(Symbols, symStrings, symSize) : [];
        var strtab = symStrings.ToBytes();

        // ---- program headers ----
        var segments = Segments.ToList();
        var interp = Interpreter is null ? null : Encoding.ASCII.GetBytes(Interpreter + "\0");
        var phCount = segments.Count + (interp is null ? 0 : 1) + (hasDynamic ? 2 : 0);

        // ---- file layout ----
        ulong offset = (ulong)ehSize;
        var phOffset = phCount > 0 ? offset : 0;
        offset += (ulong)(phCount * phSize);

        var segmentOffsets = new List<ulong>();
        foreach (var s in segments)
        {
            offset = Align(offset, 16);
            segmentOffsets.Add(offset);
            offset += (ulong)s.Data.Length;
        }

        offset = Align(offset, 16);
        var interpOffset = offset;
        offset += (ulong)(interp?.Length ?? 0);

        offset = Align(offset, 16);
        var dynBlockOffset = offset;
        offset += dynBlockLength;

        offset = Align(offset, 8);
        var symtabOffset = offset;
        offset += (ulong)symtab.Length;
        var strtabOffset = offset;
        offset += (ulong)strtab.Length;

        // ---- sections ----
        var shstr = new StringTable();
        var sections = new List<SectionRecord> { new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0) };
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            if (s.SectionName is null)
            {
                continue;
            }

            ulong flags = 2;
            if ((s.Flags & 2) != 0)
            {
                flags |= 1;
            }

            if ((s.Flags & 1) != 0)
            {
                flags |= 4;
            }

            sections.Add(new SectionRecord((uint)shstr.Add(s.SectionName), 1, flags, s.VirtualAddress, segmentOffsets[i], (ulong)s.Data.Length, 0, 0, 16, 0));
        }

        if (interp is not null)
        {
            sections.Add(new SectionRecord((uint)shstr.Add(".interp"), 1, 2, 0, interpOffset, (ulong)interp.Length, 0, 0, 1, 0));
        }

        if (hasDynamic)
        {
            var dynstrIndex = sections.Count + 1;
            sections.Add(new SectionRecord((uint)shstr.Add(".dynsym"), 11, 2, DynamicBase + dynsymRel, dynBlockOffset + dynsymRel, (ulong)dynsym.Length, (uint)dynstrIndex, 1, 8, (ulong)symSize));
            sections.Add(new SectionRecord((uint)shstr.Add(".dynstr"), 3, 2, DynamicBase + dynstrRel, dynBlockOffset + dynstrRel, (ulong)dynstr.Length, 0, 0, 1, 0));
            sections.Add(new SectionRecord((uint)shstr.Add(".dynamic"), 6, 3, DynamicBase + dynamicRel, dynBlockOffset + dynamicRel, (ulong)dynamic.Length, (uint)dynstrIndex, 0, 8, (ulong)dynEntrySize));
        }

        if (symtab.Length > 0)
        {
            var strtabIndex = sections.Count + 1;
            sections.Add(new SectionRecord((uint)shstr.Add(".symtab"), 2, 0, 0, symtabOffset, (ulong)symtab.Length, (uint)strtabIndex, 1, 8, (ulong)symSize));
            sections.Add(new SectionRecord((uint)shstr.Add(".strtab"), 3, 0, 0, strtabOffset, (ulong)strtab.Length, 0, 0, 1, 0));
        }

        var shstrIndex = sections.Count;
        var shstrNameOffset = (uint)shstr.Add(".shstrtab");
        var shstrBytes = shstr.ToBytes();
        var shstrOffset = offset;
        offset += (ulong)shstrBytes.Length;
        sections.Add(new SectionRecord(shstrNameOffset, 3, 0, 0, shstrOffset, (ulong)shstrBytes.Length, 0, 0, 1, 0));

        offset = Align(offset, 8);
        var shOffset = OmitSectionHeaders ? 0UL : offset;

        // ---- write ----
        var w = new Writer(BigEndian, Is64);
        var firstExecutable = segments.FirstOrDefault(s => s.Type == 1 && (s.Flags & 1) != 0);
        var entry = Entry ?? firstExecutable?.VirtualAddress ?? 0;

        w.Bytes(0x7F, (byte)'E', (byte)'L', (byte)'F', Is64 ? (byte)2 : (byte)1, BigEndian ? (byte)2 : (byte)1, 1, OsAbi, AbiVersion, 0, 0, 0, 0, 0, 0, 0);
        w.U16(Type);
        w.U16(Machine);
        w.U32(1);
        w.Addr(entry);
        w.Addr(phOffset);
        w.Addr(shOffset);
        w.U32(Flags);
        w.U16((ushort)ehSize);
        w.U16((ushort)(phCount > 0 ? phSize : 0));
        w.U16((ushort)phCount);
        w.U16((ushort)(OmitSectionHeaders ? 0 : shSize));
        w.U16((ushort)(OmitSectionHeaders ? 0 : sections.Count));
        w.U16((ushort)(OmitSectionHeaders ? 0 : shstrIndex));

        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            w.ProgramHeader(s.Type, s.Flags, segmentOffsets[i], s.VirtualAddress, (ulong)s.Data.Length, s.MemorySize ?? (ulong)s.Data.Length, s.Align);
        }

        if (interp is not null)
        {
            w.ProgramHeader(3, 4, interpOffset, 0, (ulong)interp.Length, (ulong)interp.Length, 1);
        }

        if (hasDynamic)
        {
            w.ProgramHeader(1, 6, dynBlockOffset, DynamicBase, dynBlockLength, dynBlockLength, 16);
            w.ProgramHeader(2, 6, dynBlockOffset + dynamicRel, DynamicBase + dynamicRel, (ulong)dynamic.Length, (ulong)dynamic.Length, 8);
        }

        for (var i = 0; i < segments.Count; i++)
        {
            w.PadTo((int)segmentOffsets[i]);
            w.Raw(segments[i].Data);
        }

        if (interp is not null)
        {
            w.PadTo((int)interpOffset);
            w.Raw(interp);
        }

        if (hasDynamic)
        {
            w.PadTo((int)dynBlockOffset);
            w.Raw(dynstr);
            w.PadTo((int)(dynBlockOffset + dynsymRel));
            w.Raw(dynsym);
            w.PadTo((int)(dynBlockOffset + dynamicRel));
            w.Raw(dynamic);
        }

        if (symtab.Length > 0)
        {
            w.PadTo((int)symtabOffset);
            w.Raw(symtab);
            w.PadTo((int)strtabOffset);
            w.Raw(strtab);
        }

        w.PadTo((int)shstrOffset);
        w.Raw(shstrBytes);

        if (!OmitSectionHeaders)
        {
            w.PadTo((int)shOffset);
            foreach (var s in sections)
            {
                w.SectionHeader(s);
            }
        }

        return w.ToArray();
    }

    private byte[] BuildSymbolTable(List<SymbolSpec> symbols, StringTable strings, int symSize)
    {
        var w = new Writer(BigEndian, Is64);
        w.Raw(new byte[symSize]); // null symbol
        foreach (var s in symbols)
        {
            var nameOffset = (uint)strings.Add(s.Name);
            var info = (byte)((s.Binding << 4) | (s.Type & 0xF));
            if (Is64)
            {
                w.U32(nameOffset);
                w.Bytes(info, 0);
                w.U16(s.SectionIndex);
                w.U64(s.Value);
                w.U64(s.Size);
            }
            else
            {
                w.U32(nameOffset);
                w.U32((uint)s.Value);
                w.U32((uint)s.Size);
                w.Bytes(info, 0);
                w.U16(s.SectionIndex);
            }
        }

        return w.ToArray();
    }

    private static ulong Align(ulong value, ulong alignment) => (value + alignment - 1) / alignment * alignment;

    internal readonly record struct SectionRecord(uint Name, uint Type, ulong Flags, ulong Address, ulong Offset, ulong Size, uint Link, uint Info, ulong AddrAlign, ulong EntSize);

    private sealed class StringTable
    {
        private readonly List<byte> _bytes = [0];
        private readonly Dictionary<string, int> _offsets = [];

        public int Add(string text)
        {
            if (text.Length == 0)
            {
                return 0;
            }

            if (_offsets.TryGetValue(text, out var existing))
            {
                return existing;
            }

            var offset = _bytes.Count;
            _bytes.AddRange(Encoding.UTF8.GetBytes(text));
            _bytes.Add(0);
            _offsets[text] = offset;
            return offset;
        }

        public byte[] ToBytes() => [.. _bytes];
    }

    internal sealed class Writer
    {
        private readonly List<byte> _bytes = [];
        private readonly bool _big;
        private readonly bool _is64;

        public Writer(bool big, bool is64)
        {
            _big = big;
            _is64 = is64;
        }

        public void Bytes(params byte[] values) => _bytes.AddRange(values);

        public void Raw(byte[] values) => _bytes.AddRange(values);

        public void U16(ushort v) => Put(v, 2);

        public void U32(uint v) => Put(v, 4);

        public void U64(ulong v) => Put(v, 8);

        public void Addr(ulong v) => Put(v, _is64 ? 8 : 4);

        public void Dyn(long tag, ulong value)
        {
            Put((ulong)tag, _is64 ? 8 : 4);
            Put(value, _is64 ? 8 : 4);
        }

        public void PadTo(int length)
        {
            while (_bytes.Count < length)
            {
                _bytes.Add(0);
            }
        }

        public void ProgramHeader(uint type, uint flags, ulong offset, ulong vaddr, ulong filesz, ulong memsz, ulong align)
        {
            if (_is64)
            {
                U32(type);
                U32(flags);
                U64(offset);
                U64(vaddr);
                U64(vaddr);
                U64(filesz);
                U64(memsz);
                U64(align);
            }
            else
            {
                U32(type);
                U32((uint)offset);
                U32((uint)vaddr);
                U32((uint)vaddr);
                U32((uint)filesz);
                U32((uint)memsz);
                U32(flags);
                U32((uint)align);
            }
        }

        public void SectionHeader(SectionRecord s)
        {
            U32(s.Name);
            U32(s.Type);
            Addr(s.Flags);
            Addr(s.Address);
            Addr(s.Offset);
            Addr(s.Size);
            U32(s.Link);
            U32(s.Info);
            Addr(s.AddrAlign);
            Addr(s.EntSize);
        }

        public byte[] ToArray() => [.. _bytes];

        private void Put(ulong value, int size)
        {
            for (var i = 0; i < size; i++)
            {
                var shift = _big ? (size - 1 - i) * 8 : i * 8;
                _bytes.Add((byte)(value >> shift));
            }
        }
    }
}

