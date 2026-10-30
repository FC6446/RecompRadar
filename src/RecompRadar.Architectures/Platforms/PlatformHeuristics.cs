using RecompRadar.Core;
using RecompRadar.Core.Model;

namespace RecompRadar.Architectures.Platforms;

/// <summary>Collects weighted observations for one platform candidate.</summary>
internal sealed class EvidenceBuilder
{
    private readonly List<(HintStrength Strength, Fact Fact)> _items = [];

    public bool HasAny => _items.Count > 0;

    public void Add(HintStrength strength, string name, string value, Evidence evidence, string? note = null) =>
        _items.Add((strength, new Fact(name, value, evidence, note)));

    /// <summary>Adds one observation for the knowledge-base matches that name this platform.</summary>
    public void AddApiMatches(PlatformContext context, string platformId)
    {
        var matches = context.Dependencies.ApiMatches.Concat(context.Dependencies.HardwareHints)
            .Where(m => m.PlatformHints.Contains(platformId, StringComparer.Ordinal))
            .ToList();
        foreach (var group in matches.GroupBy(m => m.Kind))
        {
            var names = group.Select(m => m.Name).Distinct(StringComparer.Ordinal).ToList();
            var sample = string.Join(", ", names.Take(4)) + (names.Count > 4 ? $", … (+{names.Count - 4} more)" : "");

            // Names shared by three or more platforms say little; strings are the weakest kind of evidence.
            var shared = group.All(m => m.PlatformHints.Count >= 3);
            var strength = group.Key == ApiMatchKind.String || shared ? HintStrength.Weak : HintStrength.Moderate;
            var label = group.Key switch
            {
                ApiMatchKind.Library => "Library names",
                ApiMatchKind.ImportedSymbol => "Imported symbol names",
                ApiMatchKind.DefinedSymbol => "Symbol names defined in the file",
                _ => "Strings in data sections",
            };

            Add(strength, label, sample, Evidence.Verified,
                "The names are present in the file; the association with this platform comes from the knowledge base and is a hint, not proof.");
        }
    }

    public PlatformCandidate? Build(string id, string displayName, params string[] caveats)
    {
        if (_items.Count == 0)
        {
            return null;
        }

        return new PlatformCandidate
        {
            PlatformId = id,
            DisplayName = displayName,
            Strength = _items.Max(i => i.Strength),
            Reasons = _items.OrderByDescending(i => i.Strength).Select(i => i.Fact).ToList(),
            Caveats = caveats,
        };
    }
}

internal static class LayoutHelper
{
    public static IReadOnlyList<ElfSegment> Loads(ElfFile elf) =>
        elf.Segments.Where(s => s.Type == ElfConstants.PT_LOAD && s.MemorySize > 0).ToList();

    public static bool AllLoadsWithin(ElfFile elf, ulong low, ulong highExclusive)
    {
        var loads = Loads(elf);
        return loads.Count > 0 && loads.All(l => l.VirtualAddress >= low && End(l) <= highExclusive);
    }

    public static ulong End(ElfSegment s) => s.VirtualAddress > ulong.MaxValue - s.MemorySize ? ulong.MaxValue : s.VirtualAddress + s.MemorySize;

    public static bool IsMips32Little(PlatformContext c) =>
        c.Architecture.Family == CpuFamily.Mips && c.Elf.Header.Class == ElfClass.Elf32 && c.Elf.Header.Endianness == Endianness.Little;

    public static bool IsPpc32Big(PlatformContext c) =>
        c.Architecture.Family == CpuFamily.PowerPc && c.Elf.Header.Machine == ElfConstants.EM_PPC && c.Elf.Header.Endianness == Endianness.Big;
}

public sealed class Ps1Heuristic : IPlatformHeuristic
{
    public string PlatformId => "ps1";

    public PlatformCandidate? Evaluate(PlatformContext c)
    {
        if (!LayoutHelper.IsMips32Little(c) || MipsFlags.Arch(c.Elf.Header.Flags) != MipsFlags.ArchMips1 || MipsFlags.Mach(c.Elf.Header.Flags) != 0)
        {
            return null;
        }

        var b = new EvidenceBuilder();
        b.Add(HintStrength.Weak, "CPU", "32-bit little-endian MIPS I", Evidence.Verified,
            "The PlayStation 1 CPU is a MIPS R3000A-class processor, but many other systems use the same instruction set level.");
        if (LayoutHelper.AllLoadsWithin(c.Elf, 0x80000000, 0x80200000))
        {
            b.Add(HintStrength.Moderate, "Memory layout", "All loadable segments lie in 0x80000000–0x801FFFFF", Evidence.Verified,
                "This is the cached kernel-segment window over the PlayStation 1's 2 MiB of main RAM.");
        }

        b.AddApiMatches(c, PlatformId);
        return b.Build(PlatformId, "PlayStation 1",
            "Retail PlayStation 1 executables use the PS-X EXE format, not ELF. ELF files appear in development toolchains, which usually convert them to PS-X EXE.");
    }
}

public sealed class Ps2Heuristic : IPlatformHeuristic
{
    public string PlatformId => "ps2";

    public PlatformCandidate? Evaluate(PlatformContext c)
    {
        if (!LayoutHelper.IsMips32Little(c))
        {
            return null;
        }

        var h = c.Elf.Header;
        var b = new EvidenceBuilder();
        if (MipsFlags.Mach(h.Flags) == MipsFlags.MachR5900)
        {
            b.Add(HintStrength.Strong, "CPU variant flag", "e_flags names the MIPS R5900 (Emotion Engine)", Evidence.Verified,
                "Toolchains for the PlayStation 2 set this flag. It is a property of the compiled code, so it does not prove the console by itself.");
        }
        else if (MipsFlags.Arch(h.Flags) == MipsFlags.ArchMips3)
        {
            b.Add(HintStrength.Weak, "Instruction set level", "MIPS III", Evidence.Verified,
                "The R5900 is based on MIPS III, but MIPS III is used by many other systems.");
        }

        if (h.Type == 0xFF80)
        {
            b.Add(HintStrength.Moderate, "ELF type", "0xFF80 (processor-specific)", Evidence.Verified,
                "This type value is used by relocatable modules for the PlayStation 2's I/O processor (IOP).");
        }

        if (b.HasAny && LayoutHelper.Loads(c.Elf).Count > 0 && LayoutHelper.Loads(c.Elf).Min(l => l.VirtualAddress) == 0x00100000)
        {
            b.Add(HintStrength.Weak, "Memory layout", "Lowest loadable address is 0x00100000", Evidence.Verified,
                "0x00100000 is the usual start address of PlayStation 2 programs.");
        }

        if (b.HasAny)
        {
            b.AddApiMatches(c, PlatformId);
        }
        else if (c.Dependencies.ApiMatches.Any(m => m.PlatformHints.Contains(PlatformId)))
        {
            b.AddApiMatches(c, PlatformId);
        }

        return b.Build(PlatformId, "PlayStation 2",
            "The R5900 has 128-bit multimedia instructions and Vector Unit coprocessors that generic MIPS decoders do not cover.",
            "The I/O processor (IOP) runs separate MIPS code with its own modules.");
    }
}

public sealed class PspHeuristic : IPlatformHeuristic
{
    public string PlatformId => "psp";

    public PlatformCandidate? Evaluate(PlatformContext c)
    {
        if (!LayoutHelper.IsMips32Little(c))
        {
            return null;
        }

        var h = c.Elf.Header;
        var b = new EvidenceBuilder();
        if (h.Type == 0xFFA0)
        {
            b.Add(HintStrength.Moderate, "ELF type", "0xFFA0 (processor-specific)", Evidence.Verified,
                "This type value is used by PSP PRX modules.");
        }

        if (MipsFlags.Mach(h.Flags) == MipsFlags.MachAllegrexPspToolchain)
        {
            b.Add(HintStrength.Moderate, "CPU variant flag", "e_flags machine code 0x00A20000", Evidence.Verified,
                "PSP toolchains use this code for the Allegrex CPU; the same code can mean something else in other toolchains.");
        }

        if (LayoutHelper.AllLoadsWithin(c.Elf, 0x08800000, 0x0C000000))
        {
            b.Add(HintStrength.Moderate, "Memory layout", "All loadable segments lie in 0x08800000–0x0BFFFFFF", Evidence.Verified,
                "This is the user-memory range of the PSP's main RAM.");
        }

        b.AddApiMatches(c, PlatformId);
        return b.Build(PlatformId, "PlayStation Portable (PSP)",
            "Retail PSP executables are encrypted ('~PSP'); an unencrypted ELF or PRX comes from homebrew or a decrypted dump that you must be entitled to analyze.",
            "The Allegrex CPU adds a vector floating-point unit (VFPU) that generic MIPS decoders do not cover.");
    }
}

public sealed class Ps3Heuristic : IPlatformHeuristic
{
    public string PlatformId => "ps3";

    public PlatformCandidate? Evaluate(PlatformContext c)
    {
        var h = c.Elf.Header;
        var b = new EvidenceBuilder();
        if (h.Machine == ElfConstants.EM_PPC64 && h.Class == ElfClass.Elf64 && h.Endianness == Endianness.Big)
        {
            b.Add(HintStrength.Weak, "CPU", "64-bit big-endian PowerPC", Evidence.Verified,
                "The PlayStation 3's PPU is a 64-bit big-endian PowerPC core, but so are many other systems.");
            if (h.OsAbi == 0x66)
            {
                b.Add(HintStrength.Strong, "OS/ABI byte", "0x66", Evidence.Verified,
                    "PlayStation 3 PPU executables carry this non-standard OS/ABI value. It is a strong hint, not proof of the console.");
            }
        }
        else if (h.Machine == ElfConstants.EM_SPU)
        {
            b.Add(HintStrength.Moderate, "CPU", "Cell SPU", Evidence.Verified,
                "SPU code exists in the Cell Broadband Engine (used by the PlayStation 3 and some IBM systems).");
        }
        else
        {
            return null;
        }

        b.AddApiMatches(c, PlatformId);
        return b.Build(PlatformId, "PlayStation 3",
            "Retail PlayStation 3 executables are signed and encrypted (SELF). Only an unencrypted ELF can be analyzed here.",
            "The Cell processor combines a PowerPC core with SPUs, which are a different instruction set.");
    }
}

public sealed class Ps4Heuristic : IPlatformHeuristic
{
    public string PlatformId => "ps4";

    public PlatformCandidate? Evaluate(PlatformContext c)
    {
        var h = c.Elf.Header;
        if (c.Architecture.Family != CpuFamily.X86_64)
        {
            return null;
        }

        var b = new EvidenceBuilder();
        var freeBsd = h.OsAbi == 9;
        var sceType = h.Type is 0xFE00 or 0xFE10 or 0xFE18;
        if (freeBsd && sceType)
        {
            b.Add(HintStrength.Strong, "OS/ABI and ELF type", $"FreeBSD OS/ABI with OS-specific type 0x{h.Type:X4}", Evidence.Verified,
                "This combination is characteristic of PlayStation 4 executables and modules. It is a strong hint, not proof of the console.");
        }
        else
        {
            if (freeBsd)
            {
                b.Add(HintStrength.Weak, "OS/ABI", "FreeBSD", Evidence.Verified, "The PlayStation 4's operating system is derived from FreeBSD, but ordinary FreeBSD programs have the same value.");
            }

            if (sceType)
            {
                b.Add(HintStrength.Moderate, "ELF type", $"0x{h.Type:X4} (OS-specific)", Evidence.Verified, "This OS-specific type value is used by PlayStation 4 executables and modules.");
            }
        }

        b.AddApiMatches(c, PlatformId);
        return b.Build(PlatformId, "PlayStation 4",
            "Retail PlayStation 4 executables are signed and encrypted (SELF). Only an unencrypted ELF can be analyzed here.",
            "The CPU code is x86-64 and therefore instruction-set compatible with PCs. The porting effort lies in the operating system, the GPU interface (GNM) and the system libraries, not in CPU translation.");
    }
}

public abstract class NintendoPowerPcHeuristic : IPlatformHeuristic
{
    public abstract string PlatformId { get; }

    protected abstract bool IsWii { get; }

    public PlatformCandidate? Evaluate(PlatformContext c)
    {
        if (!LayoutHelper.IsPpc32Big(c))
        {
            return null;
        }

        var wiiOnlyLayout = LayoutHelper.AllLoadsWithin(c.Elf, 0x90000000, 0x94000000);
        var wiiOnlyApi = c.Dependencies.ApiMatches.Any(m => m.PlatformHints.Count == 1 && m.PlatformHints[0] == "wii");
        var wiiSpecific = wiiOnlyLayout || wiiOnlyApi;
        if (!IsWii && wiiSpecific)
        {
            return null;
        }

        var b = new EvidenceBuilder();
        b.Add(HintStrength.Weak, "CPU", "32-bit big-endian PowerPC", Evidence.Verified,
            "GameCube and Wii use PowerPC 750-family CPUs (Gekko, Broadway), but many other systems use 32-bit big-endian PowerPC too.");
        if (LayoutHelper.AllLoadsWithin(c.Elf, 0x80000000, 0x81800000))
        {
            b.Add(HintStrength.Moderate, "Memory layout", "All loadable segments lie in 0x80000000–0x817FFFFF", Evidence.Verified,
                "This is the cached address window over the 24 MiB main memory of the GameCube and the Wii's first memory bank.");
        }

        if (IsWii && wiiOnlyLayout)
        {
            b.Add(HintStrength.Moderate, "Memory layout", "All loadable segments lie in 0x90000000–0x93FFFFFF", Evidence.Verified,
                "This is the address window of the Wii's second memory bank, which the GameCube does not have.");
        }

        b.AddApiMatches(c, PlatformId);
        return b.Build(PlatformId, IsWii ? "Nintendo Wii" : "Nintendo GameCube",
            "GameCube and Wii games use the DOL format, not ELF. ELF files appear with homebrew toolchains (for example devkitPPC), which convert them to DOL.",
            "GameCube and Wii executables often cannot be told apart from the ELF alone.",
            "The CPUs add paired-single floating-point instructions that generic PowerPC decoders do not cover.");
    }
}

public sealed class GameCubeHeuristic : NintendoPowerPcHeuristic
{
    public override string PlatformId => "gamecube";

    protected override bool IsWii => false;
}

public sealed class WiiHeuristic : NintendoPowerPcHeuristic
{
    public override string PlatformId => "wii";

    protected override bool IsWii => true;
}

/// <summary>Recognises ordinary Linux programs, so that a non-console file is not mistaken for one.</summary>
public sealed class LinuxHeuristic : IPlatformHeuristic
{
    public string PlatformId => "linux";

    public PlatformCandidate? Evaluate(PlatformContext c)
    {
        var h = c.Elf.Header;
        if (h.OsAbi is not (0 or 3))
        {
            return null;
        }

        var b = new EvidenceBuilder();
        var interpreter = c.Elf.Dynamic.Interpreter;
        if (interpreter is not null && interpreter.Contains("ld-linux", StringComparison.Ordinal))
        {
            b.Add(HintStrength.Strong, "Program interpreter", interpreter, Evidence.Verified, "The Linux dynamic loader is requested.");
        }

        b.AddApiMatches(c, PlatformId);
        return b.Build(PlatformId, "Linux program (not a console binary)",
            "A Linux program is not a console binary; this is reported so that ordinary PC programs are not mistaken for console files.");
    }
}
