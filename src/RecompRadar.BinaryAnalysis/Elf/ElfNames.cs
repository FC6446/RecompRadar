using System.Globalization;

namespace RecompRadar.BinaryAnalysis.Elf;

/// <summary>Neutral, format-level names for ELF constants. Console-specific interpretation lives elsewhere.</summary>
public static class ElfNames
{
    private static readonly Dictionary<ushort, string> Machines = new()
    {
        [0] = "None",
        [2] = "SPARC",
        [3] = "Intel 80386 (x86)",
        [4] = "Motorola 68000",
        [8] = "MIPS",
        [10] = "MIPS RS3000 little-endian",
        [15] = "HP PA-RISC",
        [18] = "SPARC32+",
        [20] = "PowerPC",
        [21] = "PowerPC 64-bit",
        [22] = "IBM S/390",
        [23] = "Cell SPU",
        [40] = "ARM",
        [42] = "Renesas SuperH",
        [43] = "SPARC V9",
        [50] = "Intel IA-64",
        [62] = "AMD x86-64",
        [83] = "Atmel AVR",
        [94] = "Tensilica Xtensa",
        [105] = "TI MSP430",
        [183] = "ARM 64-bit (AArch64)",
        [243] = "RISC-V",
        [247] = "Linux BPF",
        [258] = "LoongArch",
    };

    private static readonly Dictionary<byte, string> OsAbis = new()
    {
        [0] = "UNIX System V",
        [1] = "HP-UX",
        [2] = "NetBSD",
        [3] = "Linux (GNU)",
        [4] = "GNU Hurd",
        [6] = "Solaris",
        [7] = "AIX",
        [8] = "IRIX",
        [9] = "FreeBSD",
        [10] = "Tru64 UNIX",
        [12] = "OpenBSD",
        [13] = "OpenVMS",
        [64] = "ARM EABI / bare metal",
        [97] = "ARM",
        [255] = "Standalone (embedded)",
    };

    private static readonly Dictionary<uint, string> SegmentTypes = new()
    {
        [0] = "PT_NULL",
        [1] = "PT_LOAD",
        [2] = "PT_DYNAMIC",
        [3] = "PT_INTERP",
        [4] = "PT_NOTE",
        [5] = "PT_SHLIB",
        [6] = "PT_PHDR",
        [7] = "PT_TLS",
        [0x6474e550] = "PT_GNU_EH_FRAME",
        [0x6474e551] = "PT_GNU_STACK",
        [0x6474e552] = "PT_GNU_RELRO",
        [0x6474e553] = "PT_GNU_PROPERTY",
        [0x70000000] = "PT_MIPS_REGINFO",
        [0x70000001] = "PT_MIPS_RTPROC / PT_ARM_EXIDX",
        [0x70000002] = "PT_MIPS_OPTIONS",
        [0x70000003] = "PT_MIPS_ABIFLAGS",
    };

    private static readonly Dictionary<uint, string> SectionTypes = new()
    {
        [0] = "SHT_NULL",
        [1] = "SHT_PROGBITS",
        [2] = "SHT_SYMTAB",
        [3] = "SHT_STRTAB",
        [4] = "SHT_RELA",
        [5] = "SHT_HASH",
        [6] = "SHT_DYNAMIC",
        [7] = "SHT_NOTE",
        [8] = "SHT_NOBITS",
        [9] = "SHT_REL",
        [10] = "SHT_SHLIB",
        [11] = "SHT_DYNSYM",
        [14] = "SHT_INIT_ARRAY",
        [15] = "SHT_FINI_ARRAY",
        [16] = "SHT_PREINIT_ARRAY",
        [17] = "SHT_GROUP",
        [18] = "SHT_SYMTAB_SHNDX",
        [0x6ffffff6] = "SHT_GNU_HASH",
        [0x6ffffffd] = "SHT_GNU_verdef",
        [0x6ffffffe] = "SHT_GNU_verneed",
        [0x6fffffff] = "SHT_GNU_versym",
        [0x70000006] = "SHT_MIPS_REGINFO",
        [0x7000000d] = "SHT_MIPS_OPTIONS",
        [0x7000002a] = "SHT_MIPS_ABIFLAGS",
    };

    private static readonly Dictionary<long, string> DynamicTags = new()
    {
        [0] = "DT_NULL",
        [1] = "DT_NEEDED",
        [2] = "DT_PLTRELSZ",
        [3] = "DT_PLTGOT",
        [4] = "DT_HASH",
        [5] = "DT_STRTAB",
        [6] = "DT_SYMTAB",
        [7] = "DT_RELA",
        [8] = "DT_RELASZ",
        [9] = "DT_RELAENT",
        [10] = "DT_STRSZ",
        [11] = "DT_SYMENT",
        [12] = "DT_INIT",
        [13] = "DT_FINI",
        [14] = "DT_SONAME",
        [15] = "DT_RPATH",
        [16] = "DT_SYMBOLIC",
        [17] = "DT_REL",
        [18] = "DT_RELSZ",
        [19] = "DT_RELENT",
        [20] = "DT_PLTREL",
        [21] = "DT_DEBUG",
        [22] = "DT_TEXTREL",
        [23] = "DT_JMPREL",
        [24] = "DT_BIND_NOW",
        [25] = "DT_INIT_ARRAY",
        [26] = "DT_FINI_ARRAY",
        [27] = "DT_INIT_ARRAYSZ",
        [28] = "DT_FINI_ARRAYSZ",
        [29] = "DT_RUNPATH",
        [30] = "DT_FLAGS",
        [32] = "DT_PREINIT_ARRAY",
        [33] = "DT_PREINIT_ARRAYSZ",
        [0x6ffffef5] = "DT_GNU_HASH",
        [0x6ffffffb] = "DT_FLAGS_1",
        [0x6ffffff0] = "DT_VERSYM",
        [0x6ffffffc] = "DT_VERDEF",
        [0x6ffffffd] = "DT_VERDEFNUM",
        [0x6ffffffe] = "DT_VERNEED",
        [0x6fffffff] = "DT_VERNEEDNUM",
        [0x70000001] = "DT_MIPS_RLD_VERSION",
        [0x70000005] = "DT_MIPS_FLAGS",
        [0x70000006] = "DT_MIPS_BASE_ADDRESS",
        [0x7000000a] = "DT_MIPS_LOCAL_GOTNO",
        [0x70000011] = "DT_MIPS_SYMTABNO",
        [0x70000013] = "DT_MIPS_GOTSYM",
    };

    public static string MachineName(ushort machine) =>
        Machines.TryGetValue(machine, out var name) ? name : Hex("Unknown machine", machine);

    public static string OsAbiName(byte osAbi) =>
        OsAbis.TryGetValue(osAbi, out var name) ? name : Hex("Unknown OS/ABI", osAbi);

    public static string TypeName(ushort type) => type switch
    {
        0 => "ET_NONE (no type)",
        1 => "ET_REL (relocatable object)",
        2 => "ET_EXEC (executable)",
        3 => "ET_DYN (shared object / PIE)",
        4 => "ET_CORE (core dump)",
        >= 0xFE00 and <= 0xFEFF => Hex("OS-specific type", type),
        >= 0xFF00 => Hex("Processor-specific type", type),
        _ => Hex("Unknown type", type),
    };

    public static string SegmentTypeName(uint type) =>
        SegmentTypes.TryGetValue(type, out var name) ? name : Hex("PT", type);

    public static string SectionTypeName(uint type) =>
        SectionTypes.TryGetValue(type, out var name) ? name : Hex("SHT", type);

    public static string DynamicTagName(long tag) =>
        DynamicTags.TryGetValue(tag, out var name) ? name : $"DT_0x{tag:X}";

    public static string SymbolTypeName(byte type) => type switch
    {
        0 => "NOTYPE",
        1 => "OBJECT",
        2 => "FUNC",
        3 => "SECTION",
        4 => "FILE",
        5 => "COMMON",
        6 => "TLS",
        10 => "GNU_IFUNC",
        _ => $"TYPE_{type}",
    };

    public static string SymbolBindingName(byte binding) => binding switch
    {
        0 => "LOCAL",
        1 => "GLOBAL",
        2 => "WEAK",
        10 => "GNU_UNIQUE",
        _ => $"BIND_{binding}",
    };

    public static string SymbolVisibilityName(byte visibility) => visibility switch
    {
        0 => "DEFAULT",
        1 => "INTERNAL",
        2 => "HIDDEN",
        3 => "PROTECTED",
        _ => $"VIS_{visibility}",
    };

    private static string Hex(string label, uint value) =>
        string.Create(CultureInfo.InvariantCulture, $"{label} (0x{value:X})");
}

