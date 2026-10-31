using RecompRadar.Core;
using RecompRadar.Core.Model;
using RecompRadar.Recompilation.Analysis;
using RecompRadar.Recompilation.Build;
using RecompRadar.Semantics;

namespace RecompRadar.Recompilation.Compatibility;

public enum CompatibilityLevel
{
    /// <summary>Real-world software of this kind runs. No target reaches this level yet.</summary>
    Supported,

    /// <summary>Parts of real-world software run. No target reaches this level yet.</summary>
    PartiallySupported,

    /// <summary>Works for self-made test programs within documented limits; real software is not expected to work.</summary>
    Experimental,

    /// <summary>The program cannot be built or cannot run correctly with what exists today. The reasons are listed.</summary>
    Unsupported,

    /// <summary>There is not enough information to decide (for example the file is not an ELF).</summary>
    Unknown,
}

public enum NoteKind
{
    Blocker,
    Warning,
    Info,
}

public sealed record CompatibilityNote(NoteKind Kind, string Text);

public sealed record CompatibilityInput(
    ElfFile? Elf,
    PlatformAssessment? DetectedPlatform = null,
    string? PlatformOverride = null,
    ProgramGraph? Graph = null);

public sealed class CompatibilityAssessment
{
    public required CompatibilityLevel Level { get; init; }

    public required string Headline { get; init; }

    public string? PlatformId { get; init; }

    /// <summary>"detected hint (not proof)", "manual override" or "none".</summary>
    public required string PlatformSource { get; init; }

    public ConsoleBackend? Backend { get; init; }

    public required IReadOnlyList<CompatibilityNote> Notes { get; init; }

    /// <summary>True when a build can be started with the default options.</summary>
    public required bool CanRecompile { get; init; }

    public string? RecompileBlockedReason { get; init; }

    /// <summary>True when a build with "allow incomplete" can still translate the supported parts.</summary>
    public bool CanRecompileIncomplete { get; init; }

    /// <summary>Why a built program would not run correctly; null when nothing known stands in the way.</summary>
    public string? RunBlockedReason { get; init; }

    public BuildCategory ExpectedCategory { get; init; } = BuildCategory.Unsupported;

    /// <summary>The system interface a build would use: the PlayStation 2 profile for the PS2 backend, otherwise the RecompRadar test calls.</summary>
    public RuntimeProfile Profile { get; init; } = RuntimeProfile.RecompRadarTest;

    public IEnumerable<CompatibilityNote> Blockers => Notes.Where(n => n.Kind == NoteKind.Blocker);
}

/// <summary>
/// Decides what RecompRadar can honestly do with a file. The result is derived from the file's header, the code analysis
/// and the list of implemented modules. A manual platform choice never raises the level: it only selects which backend is checked.
/// </summary>
public static class CompatibilityEngine
{
    private static readonly uint[] ImplementedSystemCalls =
        [PocSystemCalls.Exit, PocSystemCalls.WriteByte, PocSystemCalls.WriteInt, PocSystemCalls.WriteFloat, PocSystemCalls.WriteString];

    public static CompatibilityAssessment Assess(CompatibilityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var notes = new List<CompatibilityNote>();

        if (input.Elf is null)
        {
            return Finish(CompatibilityLevel.Unknown, "Unknown - only ELF files can be analyzed for recompilation.", null, "none", null, notes,
                canRecompile: false, "No parsed ELF file is loaded.", incomplete: false, null);
        }

        var elf = input.Elf;
        var (platformId, platformSource) = ChoosePlatform(input);
        var cpu = Describe(elf);
        notes.Add(new CompatibilityNote(NoteKind.Info, platformId is null
            ? "No target system is assumed. A plain ELF file does not identify the console."
            : platformSource == "manual override"
                ? $"Target system chosen manually: {platformId}. The choice selects the backend that is checked; it does not change what the file contains or what RecompRadar can do."
                : $"Target system hint: {platformId} (derived from header fields and symbols; a hint, not proof)."));

        ConsoleBackend? backend;
        if (platformId is not null)
        {
            backend = ConsoleBackendRegistry.Find(platformId);
            if (backend is null)
            {
                notes.Add(new CompatibilityNote(NoteKind.Blocker,
                    $"RecompRadar has no backend for '{platformId}'. Backends that exist: {string.Join(", ", ConsoleBackendRegistry.All.Select(b => b.Id))}."));
                return Finish(CompatibilityLevel.Unsupported, $"Unsupported - no backend for '{platformId}'.", platformId, platformSource, null, notes,
                    false, $"No backend exists for '{platformId}'.", false, "No backend exists for this system.");
            }
        }
        else
        {
            backend = cpu.IsMips32 ? ConsoleBackendRegistry.Find(ConsoleBackendRegistry.GenericMips32) : null;
        }

        if (backend is null)
        {
            var reason = $"No backend exists for {cpu.Name}.";
            notes.Add(new CompatibilityNote(NoteKind.Blocker, reason));
            return Finish(CompatibilityLevel.Unsupported, $"Unsupported - {reason}", platformId, platformSource, null, notes, false, reason, false, reason);
        }

        var required = backend.Id switch
        {
            ConsoleBackendRegistry.Ps2 or ConsoleBackendRegistry.GenericMips32 => cpu.IsMips32,
            ConsoleBackendRegistry.Ps3 => cpu.IsPowerPc64,
            ConsoleBackendRegistry.Ps4 => cpu.IsX8664,
            _ => false,
        };
        if (!required)
        {
            var reason = $"The {backend.DisplayName} backend needs {backend.Architecture} code, but this file contains {cpu.Name}. A manual platform choice cannot change that.";
            notes.Add(new CompatibilityNote(NoteKind.Blocker, reason));
            return Finish(CompatibilityLevel.Unsupported, "Unsupported - the file's CPU does not match the chosen system.", platformId, platformSource, backend, notes,
                false, reason, false, reason);
        }

        if (!backend.HasWorkingPipeline)
        {
            var reason = $"The {backend.DisplayName} backend is not implemented yet (milestone {backend.Milestone}). {backend.Summary}";
            notes.Add(new CompatibilityNote(NoteKind.Blocker, reason));
            return Finish(CompatibilityLevel.Unsupported, $"Unsupported - {backend.DisplayName} is planned, not implemented.", platformId, platformSource, backend, notes,
                false, reason, false, reason);
        }

        if (backend.Id == ConsoleBackendRegistry.Ps2 && elf.Header.Endianness != Endianness.Little)
        {
            notes.Add(new CompatibilityNote(NoteKind.Warning, "The PlayStation 2 CPU is little-endian, but this file is big-endian; it is unlikely to be a PS2 program. It is translated as generic MIPS32."));
        }

        if (backend.Id == ConsoleBackendRegistry.Ps2)
        {
            notes.Add(new CompatibilityNote(NoteKind.Info,
                "The PlayStation 2 runtime is a subset: the EE memory map, serial text output and a few kernel calls (threads, semaphores, sleep/wakeup). No graphics, sound, controller, file, interrupt or timer services exist, so real PlayStation 2 software cannot run."));
            foreach (var segment in elf.Segments.Where(s => s.Type == ElfConstants.PT_LOAD && s.MemorySize > 0))
            {
                var fits = segment.VirtualAddress <= uint.MaxValue &&
                           (ulong)Ps2Memory.ToPhysical((uint)segment.VirtualAddress) + segment.MemorySize <= Ps2Memory.RamSize;
                if (!fits)
                {
                    var text = $"Segment #{segment.Index} (0x{segment.VirtualAddress:X}, {segment.MemorySize:N0} bytes) does not fit into the 32 MiB of EE RAM, so the PlayStation 2 memory map cannot hold this program.";
                    notes.Add(new CompatibilityNote(NoteKind.Blocker, text));
                    return Finish(CompatibilityLevel.Unsupported, "Unsupported - the program does not fit the PlayStation 2 memory map.", platformId, platformSource, backend, notes,
                        false, text, false, text);
                }
            }
        }
        else
        {
            notes.Add(new CompatibilityNote(NoteKind.Info,
                "Only the RecompRadar test runtime exists (exit and simple output). No console kernel, graphics, sound, input or file services are available, so real console software cannot run."));
        }

        return AssessCode(input.Graph, backend, platformId, platformSource, notes);
    }

    private static CompatibilityAssessment AssessCode(ProgramGraph? graph, ConsoleBackend backend, string? platformId, string platformSource, List<CompatibilityNote> notes)
    {
        if (graph is null)
        {
            notes.Add(new CompatibilityNote(NoteKind.Info, "The code has not been analyzed yet, so this assessment only reflects the file header. Run the analysis for a definite answer."));
            return Finish(CompatibilityLevel.Experimental, "Experimental (provisional) - builds only self-made MIPS32 test programs.", platformId, platformSource, backend, notes,
                true, null, true, null);
        }

        var blockers = new List<string>();
        if (graph.UnsupportedInstructions.Count > 0)
        {
            var grouped = graph.UnsupportedInstructions.GroupBy(u => u.Mnemonic).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Take(6)
                .Select(g => $"{g.Key} x{g.Count()}");
            var first = graph.UnsupportedInstructions[0];
            var text = $"{graph.UnsupportedInstructions.Count} instruction site(s) cannot be translated (first: {first.Mnemonic} at 0x{first.Address:X8}; {string.Join(", ", grouped)}). The program would stop there.";
            blockers.Add(text);
            notes.Add(new CompatibilityNote(NoteKind.Blocker, text));
        }

        foreach (var finding in graph.Findings.Where(f => f.Code == "CODE_STOPS"))
        {
            blockers.Add(finding.Message);
            notes.Add(new CompatibilityNote(NoteKind.Blocker, finding.Message));
        }

        if (graph.Truncated)
        {
            const string text = "The analysis limit was reached, so parts of the program were not analyzed.";
            blockers.Add(text);
            notes.Add(new CompatibilityNote(NoteKind.Blocker, text));
        }

        if (graph.IndirectJumps.Count > 0)
        {
            notes.Add(new CompatibilityNote(NoteKind.Warning,
                $"{graph.IndirectJumps.Count} jump/call site(s) go through a register and their targets are not known statically. If one of them reaches code that was not recovered, the program faults with a message at run time."));
        }

        if (graph.Findings.Any(f => f.Code == "TARGET_NOT_EXECUTABLE"))
        {
            notes.Add(new CompatibilityNote(NoteKind.Warning, "Some jumps go outside executable memory; those paths fault at run time."));
        }

        var isPs2 = backend.Id == ConsoleBackendRegistry.Ps2;
        var unknownNumbers = graph.SyscallSites.Where(s => s.Abi == SyscallAbi.RecompRadarTest && s.Number is null).ToList();
        var unimplemented = graph.SyscallSites.Where(s => s.Abi == SyscallAbi.RecompRadarTest && s.Number is { } n && !ImplementedSystemCalls.Contains(n)).ToList();
        if (unimplemented.Count > 0)
        {
            var numbers = string.Join(", ", unimplemented.Select(s => s.Number!.Value).Distinct().OrderBy(n => n).Take(8));
            var text = $"{unimplemented.Count} system call site(s) use test call number(s) {numbers}, which are not implemented. RecompRadar's test calls are 1 to 5 (number in $v0).";
            blockers.Add(text);
            notes.Add(new CompatibilityNote(NoteKind.Blocker, text));
        }

        var kernelCalls = graph.SyscallSites.Where(s => s.Abi == SyscallAbi.Ps2Kernel).ToList();
        if (kernelCalls.Count > 0 && !isPs2)
        {
            var names = string.Join(", ", kernelCalls.Select(s => KernelCallName(s.Number!.Value)).Distinct().OrderBy(n => n).Take(6));
            var text = $"{kernelCalls.Count} system call site(s) use PlayStation 2 kernel calls ({names}), but the {backend.DisplayName} backend has no kernel. Choose PlayStation 2 as the target system.";
            blockers.Add(text);
            notes.Add(new CompatibilityNote(NoteKind.Blocker, text));
        }
        else if (kernelCalls.Count > 0)
        {
            var missing = kernelCalls.Where(s => !Ps2Kernel.IsImplemented(unchecked((int)s.Number!.Value))).ToList();
            if (missing.Count > 0)
            {
                var names = string.Join(", ", missing.Select(s => KernelCallName(s.Number!.Value)).Distinct().OrderBy(n => n).Take(8));
                var text = $"{missing.Count} system call site(s) use PlayStation 2 kernel calls that are not implemented: {names}. Implemented are: {string.Join(", ", Ps2Kernel.ImplementedCalls().Select(n => Ps2Kernel.NameOf(n)))}.";
                blockers.Add(text);
                notes.Add(new CompatibilityNote(NoteKind.Blocker, text));
            }
            else
            {
                notes.Add(new CompatibilityNote(NoteKind.Info,
                    $"{kernelCalls.Count} kernel call site(s) use implemented calls ({string.Join(", ", kernelCalls.Select(s => KernelCallName(s.Number!.Value)).Distinct().OrderBy(n => n))}). Their return values follow common convention and were not compared with real hardware."));
            }
        }

        if (unknownNumbers.Count > 0)
        {
            notes.Add(new CompatibilityNote(NoteKind.Warning,
                $"{unknownNumbers.Count} system call site(s) have a call number that cannot be determined statically; unknown numbers fault at run time."));
        }

        if (blockers.Count > 0)
        {
            var reason = string.Join(" ", blockers);
            return Finish(CompatibilityLevel.Unsupported, "Unsupported - this program cannot run correctly with the current backend.", platformId, platformSource, backend, notes,
                false, "Building is blocked because the program cannot run correctly: " + reason, true, reason);
        }

        notes.Add(new CompatibilityNote(NoteKind.Info,
            $"{graph.Functions.Count} function(s), {graph.Blocks.Count} block(s), {graph.InstructionCount} instruction(s) analyzed; every reachable instruction can be translated and every system call is implemented."));
        return Finish(CompatibilityLevel.Experimental, backend.Id == ConsoleBackendRegistry.Ps2
                ? "Experimental - builds and runs self-made MIPS32 programs that use the RecompRadar test calls or the implemented PlayStation 2 kernel calls."
                : "Experimental - builds and runs self-made MIPS32 programs that use the RecompRadar test system calls.", platformId, platformSource, backend, notes,
            true, null, true, null);
    }

    private static CompatibilityAssessment Finish(
        CompatibilityLevel level,
        string headline,
        string? platformId,
        string platformSource,
        ConsoleBackend? backend,
        List<CompatibilityNote> notes,
        bool canRecompile,
        string? recompileBlocked,
        bool incomplete,
        string? runBlocked) => new()
    {
        Level = level,
        Headline = headline,
        PlatformId = platformId,
        PlatformSource = platformSource,
        Backend = backend,
        Notes = notes,
        CanRecompile = canRecompile,
        RecompileBlockedReason = canRecompile ? null : recompileBlocked,
        CanRecompileIncomplete = incomplete && backend?.HasWorkingPipeline == true,
        RunBlockedReason = runBlocked,
        ExpectedCategory = canRecompile ? BuildCategory.TranslatedWithRuntime : BuildCategory.Unsupported,
        Profile = backend?.Id == ConsoleBackendRegistry.Ps2 ? RuntimeProfile.Ps2Ee : RuntimeProfile.RecompRadarTest,
    };

    private static string KernelCallName(uint number)
    {
        var n = unchecked((int)number);
        return Ps2Kernel.NameOf(n) is { } name ? $"{name} (0x{number:X})" : $"unknown call 0x{number:X}";
    }

    private static (string? Id, string Source) ChoosePlatform(CompatibilityInput input)
    {
        if (!string.IsNullOrWhiteSpace(input.PlatformOverride) && !string.Equals(input.PlatformOverride, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return (input.PlatformOverride.Trim().ToLowerInvariant(), "manual override");
        }

        var top = input.DetectedPlatform?.Candidates
            .Where(c => c.Strength >= HintStrength.Moderate)
            .OrderByDescending(c => c.Strength)
            .FirstOrDefault();
        return top is null ? (null, "none") : (top.PlatformId, "detected hint (not proof)");
    }

    private readonly record struct CpuKind(string Name, bool IsMips32, bool IsPowerPc64, bool IsX8664);

    private static CpuKind Describe(ElfFile elf)
    {
        var machine = elf.Header.Machine;
        var is64 = elf.Header.Class == ElfClass.Elf64;
        return machine switch
        {
            ElfConstants.EM_MIPS when !is64 => new CpuKind("32-bit MIPS", true, false, false),
            ElfConstants.EM_MIPS => new CpuKind("64-bit MIPS", false, false, false),
            ElfConstants.EM_PPC64 => new CpuKind("64-bit PowerPC", false, true, false),
            ElfConstants.EM_PPC => new CpuKind("32-bit PowerPC", false, false, false),
            ElfConstants.EM_X86_64 => new CpuKind("x86-64", false, false, true),
            _ => new CpuKind($"machine type {machine}", false, false, false),
        };
    }
}
