namespace RecompRadar.Core;

/// <summary>Numeric ELF constants shared between the parser, the architecture catalog and plugins.</summary>
public static class ElfConstants
{
    public const ushort EM_386 = 3;
    public const ushort EM_MIPS = 8;
    public const ushort EM_PPC = 20;
    public const ushort EM_PPC64 = 21;
    public const ushort EM_SPU = 23;
    public const ushort EM_ARM = 40;
    public const ushort EM_X86_64 = 62;
    public const ushort EM_AARCH64 = 183;
    public const ushort EM_RISCV = 243;

    public const ushort ET_REL = 1;
    public const ushort ET_EXEC = 2;
    public const ushort ET_DYN = 3;
    public const ushort ET_CORE = 4;

    public const uint PT_LOAD = 1;
    public const uint PT_DYNAMIC = 2;
    public const uint PT_INTERP = 3;

    /// <summary>OS-specific e_type range (ET_LOOS..ET_HIOS).</summary>
    public static bool IsOsSpecificType(ushort type) => type is >= 0xFE00 and <= 0xFEFF;
}
