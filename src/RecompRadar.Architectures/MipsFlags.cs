using RecompRadar.Core.Model;

namespace RecompRadar.Architectures;

/// <summary>Decoding of the MIPS-specific e_flags bits as defined by the MIPS ELF ABI and binutils.</summary>
public static class MipsFlags
{
    public const uint ArchMask = 0xF0000000;
    public const uint MachMask = 0x00FF0000;
    public const uint AbiMask = 0x0000F000;

    public const uint ArchMips1 = 0x00000000;
    public const uint ArchMips3 = 0x20000000;

    /// <summary>E_MIPS_MACH_5900: the Toshiba R5900 "Emotion Engine" CPU variant.</summary>
    public const uint MachR5900 = 0x00920000;

    /// <summary>Machine code used by PSP toolchains for the Allegrex CPU. Other toolchains use the same value for other CPUs.</summary>
    public const uint MachAllegrexPspToolchain = 0x00A20000;

    public static uint Arch(uint flags) => flags & ArchMask;

    public static uint Mach(uint flags) => flags & MachMask;

    public static string ArchName(uint flags) => Arch(flags) switch
    {
        0x00000000 => "MIPS I",
        0x10000000 => "MIPS II",
        0x20000000 => "MIPS III",
        0x30000000 => "MIPS IV",
        0x40000000 => "MIPS V",
        0x50000000 => "MIPS32",
        0x60000000 => "MIPS64",
        0x70000000 => "MIPS32 release 2",
        0x80000000 => "MIPS64 release 2",
        0x90000000 => "MIPS32 release 6",
        0xA0000000 => "MIPS64 release 6",
        _ => $"unknown (0x{Arch(flags):X8})",
    };

    public static string MachName(uint flags) => Mach(flags) switch
    {
        0 => "not specified",
        MachR5900 => "R5900 (Emotion Engine)",
        MachAllegrexPspToolchain => "0x00A20000 (value used by PSP toolchains for Allegrex; not unique to it)",
        var other => $"0x{other:X8} (CPU variant code)",
    };

    public static IEnumerable<string> FlagNames(uint flags)
    {
        if ((flags & 0x1) != 0)
        {
            yield return "noreorder";
        }

        if ((flags & 0x2) != 0)
        {
            yield return "pic";
        }

        if ((flags & 0x4) != 0)
        {
            yield return "cpic";
        }

        if ((flags & 0x100) != 0)
        {
            yield return "32bitmode";
        }

        if ((flags & 0x200) != 0)
        {
            yield return "fp64";
        }

        if ((flags & 0x400) != 0)
        {
            yield return "nan2008";
        }
    }

    public static Fact AbiFact(uint flags, bool is64)
    {
        if ((flags & 0x20) != 0)
        {
            return new Fact("MIPS ABI", "N32", Evidence.Verified, "EF_MIPS_ABI2 flag set");
        }

        return (flags & AbiMask) switch
        {
            0x1000 => new Fact("MIPS ABI", "O32", Evidence.Verified, "EF_MIPS_ABI_O32 flag set"),
            0x2000 => new Fact("MIPS ABI", "O64", Evidence.Verified, "EF_MIPS_ABI_O64 flag set"),
            0x3000 => new Fact("MIPS ABI", "EABI32", Evidence.Verified, "EF_MIPS_ABI_EABI32 flag set"),
            0x4000 => new Fact("MIPS ABI", "EABI64", Evidence.Verified, "EF_MIPS_ABI_EABI64 flag set"),
            _ when is64 => new Fact("MIPS ABI", "N64", Evidence.Inferred, "ELF64 file without an explicit ABI flag"),
            _ => new Fact("MIPS ABI", "not specified in e_flags", Evidence.Unknown),
        };
    }
}
