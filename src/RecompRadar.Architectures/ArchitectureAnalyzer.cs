using RecompRadar.Core;
using RecompRadar.Core.Model;

namespace RecompRadar.Architectures;

/// <summary>
/// Turns the ELF header into an architecture assessment relative to a porting target.
/// Everything derived from header fields is marked Verified; conclusions about what a port requires follow from that.
/// </summary>
public static class ArchitectureAnalyzer
{
    public static ArchitectureAssessment Analyze(ElfHeader header, PortabilityTarget target)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(target);

        var is64 = header.Class == ElfClass.Elf64;
        var bits = is64 ? 64 : 32;
        var endian = header.Endianness == Endianness.Little ? "little endian" : "big endian";

        var facts = new List<Fact>
        {
            new("Binary format", "ELF", Evidence.Verified),
            new("ELF class", $"{bits}-bit", Evidence.Verified),
            new("Endianness", endian, Evidence.Verified),
            new("Machine (e_machine)", $"{header.MachineName} (0x{header.Machine:X})", Evidence.Verified),
            new("ELF type (e_type)", header.TypeName, Evidence.Verified),
            new("OS/ABI", $"{header.OsAbiName} (0x{header.OsAbi:X2})", Evidence.Verified),
            new("Flags (e_flags)", $"0x{header.Flags:X8}", Evidence.Verified),
        };

        var notes = new List<string>();
        CpuFamily family;
        string name;

        switch (header.Machine)
        {
            case ElfConstants.EM_X86_64:
                family = CpuFamily.X86_64;
                name = "x86-64";
                break;
            case ElfConstants.EM_386:
                family = CpuFamily.X86;
                name = "x86 (32-bit)";
                break;
            case ElfConstants.EM_MIPS:
                family = CpuFamily.Mips;
                name = "MIPS";
                facts.Add(new Fact("MIPS instruction set level", MipsFlags.ArchName(header.Flags), Evidence.Verified, "from EF_MIPS_ARCH"));
                facts.Add(new Fact("MIPS CPU variant", MipsFlags.MachName(header.Flags), Evidence.Verified, "from EF_MIPS_MACH"));
                facts.Add(MipsFlags.AbiFact(header.Flags, is64));
                var flagNames = MipsFlags.FlagNames(header.Flags).ToList();
                if (flagNames.Count > 0)
                {
                    facts.Add(new Fact("MIPS flags", string.Join(", ", flagNames), Evidence.Verified));
                }

                notes.Add("MIPS branches have delay slots: the instruction after a branch executes before the branch takes effect. A translator has to model this explicitly.");
                if (MipsFlags.Mach(header.Flags) == MipsFlags.MachR5900)
                {
                    notes.Add("The R5900 CPU variant flag is set. The R5900 extends MIPS III with 128-bit multimedia instructions and uses a non-IEEE floating-point unit; generic MIPS decoders do not cover these.");
                }

                break;
            case ElfConstants.EM_PPC:
            case ElfConstants.EM_PPC64:
                family = CpuFamily.PowerPc;
                name = "PowerPC";
                if (header.Machine == ElfConstants.EM_PPC64)
                {
                    var abi = header.Flags & 3;
                    facts.Add(new Fact("PowerPC 64-bit ABI", abi switch { 1 => "ELFv1", 2 => "ELFv2", _ => "not specified" }, abi is 1 or 2 ? Evidence.Verified : Evidence.Unknown, "from the low bits of e_flags"));
                }

                notes.Add("PowerPC has a fixed 4-byte instruction width. Some console variants add floating-point or vector instructions that generic PowerPC decoders do not cover.");
                break;
            case ElfConstants.EM_ARM:
                family = CpuFamily.Arm;
                name = "ARM (32-bit)";
                facts.Add(new Fact("ARM EABI version", ((header.Flags >> 24) & 0xFF).ToString(), Evidence.Verified, "from the top byte of e_flags"));
                notes.Add("32-bit ARM code may mix ARM and Thumb instruction sets; a decoder must track the mode.");
                break;
            case ElfConstants.EM_AARCH64:
                family = CpuFamily.Arm64;
                name = "ARM64 (AArch64)";
                notes.Add("AArch64 has a fixed 4-byte instruction width. Windows on ARM64 runs it natively; an x86-64 host would need translation.");
                break;
            case ElfConstants.EM_RISCV:
                family = CpuFamily.RiscV;
                name = "RISC-V";
                break;
            case ElfConstants.EM_SPU:
                family = CpuFamily.Other;
                name = "Cell SPU";
                notes.Add("SPU is the synergistic processor of the Cell Broadband Engine. It has its own instruction set and a local-store memory model.");
                break;
            default:
                family = CpuFamily.Unknown;
                name = header.Machine == 0 ? "Unknown (none)" : header.MachineName;
                break;
        }

        var targetFamily = ParseTargetFamily(target.Architecture);
        var compat = family switch
        {
            CpuFamily.Unknown => NativeCompatibility.Unknown,
            _ when targetFamily == CpuFamily.Unknown => NativeCompatibility.Unknown,
            CpuFamily.X86 or CpuFamily.X86_64 when targetFamily is CpuFamily.X86_64 => NativeCompatibility.InstructionSetMatches,
            _ when family == targetFamily => NativeCompatibility.InstructionSetMatches,
            _ => NativeCompatibility.NotDirectlyCompatible,
        };

        var (status, work) = compat switch
        {
            NativeCompatibility.InstructionSetMatches => (
                "CPU instruction set matches; the file is still an ELF executable and cannot run on Windows as-is",
                "No CPU translation. The ELF must be loaded or converted, and the operating-system interface, libraries, graphics, audio and input must be provided for the target."),
            NativeCompatibility.NotDirectlyCompatible => (
                "Not directly compatible",
                "Code translation (decoder, intermediate representation, translator) and a runtime compatibility layer for operating-system and hardware behavior."),
            _ => (
                "Unknown",
                "Cannot be determined: the CPU architecture is not recognised by RecompRadar."),
        };

        return new ArchitectureAssessment
        {
            Family = family,
            DisplayName = name,
            Description = $"{name} ({bits}-bit, {endian})",
            PointerBits = bits,
            Endianness = header.Endianness,
            BinaryFormat = "ELF",
            Facts = facts,
            HostArchitecture = target.Architecture,
            Compatibility = compat,
            NativeExecutionStatus = status,
            RequiredWork = work,
            TechnicalNotes = notes,
        };
    }

    private static CpuFamily ParseTargetFamily(string architecture) => architecture.ToLowerInvariant() switch
    {
        "x86-64" or "x64" or "amd64" => CpuFamily.X86_64,
        "arm64" or "aarch64" => CpuFamily.Arm64,
        _ => CpuFamily.Unknown,
    };
}
