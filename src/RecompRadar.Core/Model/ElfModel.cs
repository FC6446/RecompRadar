namespace RecompRadar.Core.Model;

public enum ElfClass
{
    Elf32 = 1,
    Elf64 = 2,
}

public enum Endianness
{
    Little = 1,
    Big = 2,
}

public sealed record ElfHeader
{
    public ElfClass Class { get; init; }
    public Endianness Endianness { get; init; }
    public byte IdentVersion { get; init; }
    public byte OsAbi { get; init; }
    public string OsAbiName { get; init; } = "";
    public byte AbiVersion { get; init; }
    public ushort Type { get; init; }
    public string TypeName { get; init; } = "";
    public ushort Machine { get; init; }
    public string MachineName { get; init; } = "";
    public uint Version { get; init; }
    public ulong Entry { get; init; }
    public ulong ProgramHeaderOffset { get; init; }
    public ulong SectionHeaderOffset { get; init; }
    public uint Flags { get; init; }
    public ushort HeaderSize { get; init; }
    public ushort ProgramHeaderEntrySize { get; init; }
    public uint ProgramHeaderCount { get; init; }
    public ushort SectionHeaderEntrySize { get; init; }
    public uint SectionHeaderCount { get; init; }
    public uint SectionNameStringIndex { get; init; }
}

public sealed record ElfSegment
{
    public int Index { get; init; }
    public uint Type { get; init; }
    public string TypeName { get; init; } = "";
    public uint Flags { get; init; }
    public ulong Offset { get; init; }
    public ulong VirtualAddress { get; init; }
    public ulong PhysicalAddress { get; init; }
    public ulong FileSize { get; init; }
    public ulong MemorySize { get; init; }
    public ulong Alignment { get; init; }

    public bool IsReadable => (Flags & 4) != 0;
    public bool IsWritable => (Flags & 2) != 0;
    public bool IsExecutable => (Flags & 1) != 0;
    public string Permissions => $"{(IsReadable ? 'R' : '-')}{(IsWritable ? 'W' : '-')}{(IsExecutable ? 'X' : '-')}";
}

public sealed record ElfSection
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public uint Type { get; init; }
    public string TypeName { get; init; } = "";
    public ulong Flags { get; init; }
    public ulong Address { get; init; }
    public ulong Offset { get; init; }
    public ulong Size { get; init; }
    public uint Link { get; init; }
    public uint Info { get; init; }
    public ulong AddressAlign { get; init; }
    public ulong EntrySize { get; init; }

    public bool IsAllocated => (Flags & 0x2) != 0;
    public bool IsWritable => (Flags & 0x1) != 0;
    public bool IsExecutable => (Flags & 0x4) != 0;

    /// <summary>True when the section occupies bytes in the file (not SHT_NULL / SHT_NOBITS).</summary>
    public bool HasFileData => Type != 0 && Type != 8;

    public string FlagsText =>
        $"{(IsWritable ? 'W' : '-')}{(IsAllocated ? 'A' : '-')}{(IsExecutable ? 'X' : '-')}";
}

public sealed record ElfSymbol
{
    public string Name { get; init; } = "";
    public ulong Value { get; init; }
    public ulong Size { get; init; }
    public byte Type { get; init; }
    public string TypeName { get; init; } = "";
    public byte Binding { get; init; }
    public string BindingName { get; init; } = "";
    public byte Visibility { get; init; }
    public string VisibilityName { get; init; } = "";
    public ushort SectionIndex { get; init; }

    /// <summary>"symtab" or "dynsym".</summary>
    public string Table { get; init; } = "";

    public bool IsUndefined => SectionIndex == 0;

    /// <summary>An undefined, named symbol that is neither a file nor a section marker: a reference to something external.</summary>
    public bool IsImport => IsUndefined && Name.Length > 0 && Type != 3 && Type != 4;

    public bool IsFunction => Type == 2;
}

public sealed record ElfDynamicEntry
{
    public long Tag { get; init; }
    public string TagName { get; init; } = "";
    public ulong Value { get; init; }
    public string? StringValue { get; init; }
}

public sealed record ElfDynamicInfo
{
    public bool HasDynamicSegment { get; init; }
    public bool HasDynamicSection { get; init; }
    public string? Interpreter { get; init; }
    public IReadOnlyList<string> NeededLibraries { get; init; } = [];
    public string? SharedObjectName { get; init; }
    public string? RPath { get; init; }
    public string? RunPath { get; init; }
    public IReadOnlyList<ElfDynamicEntry> Entries { get; init; } = [];
}

public sealed record MemoryRegion
{
    public string Name { get; init; } = "";

    /// <summary>"segment" or "section".</summary>
    public string Source { get; init; } = "";

    public ulong VirtualAddress { get; init; }
    public ulong Size { get; init; }
    public ulong? FileOffset { get; init; }
    public ulong FileSize { get; init; }
    public bool Readable { get; init; }
    public bool Writable { get; init; }
    public bool Executable { get; init; }

    public ulong EndAddress => VirtualAddress > ulong.MaxValue - Size ? ulong.MaxValue : VirtualAddress + Size;
    public string Permissions => $"{(Readable ? 'R' : '-')}{(Writable ? 'W' : '-')}{(Executable ? 'X' : '-')}";
}

public sealed record ElfFile
{
    public required ElfHeader Header { get; init; }
    public IReadOnlyList<ElfSegment> Segments { get; init; } = [];
    public IReadOnlyList<ElfSection> Sections { get; init; } = [];
    public IReadOnlyList<ElfSymbol> Symbols { get; init; } = [];
    public ElfDynamicInfo Dynamic { get; init; } = new();
    public IReadOnlyList<MemoryRegion> MemoryRegions { get; init; } = [];
    public IReadOnlyList<MemoryRegion> ExecutableRegions { get; init; } = [];

    public bool HasProgramHeaders { get; init; }
    public bool HasSectionHeaders { get; init; }

    /// <summary>A .symtab section with readable content is present.</summary>
    public bool HasSymbolTable { get; init; }

    /// <summary>A .dynsym section with readable content is present.</summary>
    public bool HasDynamicSymbolTable { get; init; }

    public int SymbolTableEntryCount { get; init; }
    public int DynamicSymbolEntryCount { get; init; }
    public bool SymbolsTruncated { get; init; }

    /// <summary>Where the entry point lies (e.g. "segment #0, section .text") or null when it could not be located.</summary>
    public string? EntryPointLocation { get; init; }

    public bool? EntryPointInExecutableRegion { get; init; }
}
