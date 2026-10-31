namespace RecompRadar.Recompilation.Compatibility;

public enum ModuleState
{
    /// <summary>Works and is covered by tests, within the limits stated in the note.</summary>
    Implemented,

    /// <summary>Works for a documented subset only.</summary>
    Partial,

    /// <summary>Not written yet; planned for the named milestone.</summary>
    Planned,

    /// <summary>Deliberately not provided (for example because it would require circumventing copy protection).</summary>
    NotProvided,
}

public sealed record BackendModule(string Name, ModuleState State, string Note);

/// <summary>What RecompRadar can and cannot do for one target system. The module list is the single source for the UI and the documentation.</summary>
public sealed record ConsoleBackend(
    string Id,
    string DisplayName,
    string Architecture,
    string Milestone,
    string Summary,
    IReadOnlyList<BackendModule> Modules,
    string? PluginId)
{
    public bool HasWorkingPipeline => PluginId is not null;

    public int Count(ModuleState state) => Modules.Count(m => m.State == state);
}

public static class ConsoleBackendRegistry
{
    public const string Ps2 = "ps2";
    public const string Ps3 = "ps3";
    public const string Ps4 = "ps4";
    public const string GenericMips32 = "generic-mips32";

    private const string MipsPlugin = "recompradar.mips32-poc";

    public static IReadOnlyList<ConsoleBackend> All { get; } =
    [
        new ConsoleBackend(
            Ps2, "PlayStation 2 (Emotion Engine, MIPS R5900)", "MIPS32 little-endian (R5900)", "A (CPU pipeline) done, B (runtime) started",
            "The CPU pipeline works for self-made programs: analysis, translation, a real Windows program and validation against a reference interpreter. " +
            "The runtime is a start: EE memory map, serial text output and a few kernel calls. There is no graphics, sound, input or file layer, " +
            "and the R5900's 64-bit and 128-bit instructions are missing, so no real PS2 software can run.",
            [
                new("ELF loader", ModuleState.Implemented, "Maps the loadable segments of a 32-bit MIPS ELF. No IRX/IOP modules, no relocation of dynamic objects."),
                new("Instruction decoder", ModuleState.Partial, "MIPS32 integer instructions, HI/LO, conditional moves, jump-and-link, single-precision FPU. Missing: branch-likely, unaligned loads/stores, 64-bit R5900 instructions, 128-bit MMI, COP0, VU0 macro mode."),
                new("IR translator", ModuleState.Partial, "Same subset as the decoder. Branch delay slots are folded into blocks; add/sub overflow traps are not modelled; the FPU uses IEEE-754 single precision, not the R5900's reduced-precision FPU."),
                new("Control-flow analysis", ModuleState.Implemented, "Basic blocks, functions, call graph and cross references by recursive descent. Register jumps and jump tables are reported, not resolved."),
                new("Code generator and Windows build", ModuleState.Implemented, "Translates blocks to C#, builds a Windows program with the .NET SDK. The result is translated code on the .NET JIT, not ahead-of-time machine code."),
                new("Validation against reference interpreter", ModuleState.Implemented, "Compares end state, output, instruction count and registers for one run."),
                new("RecompRadar test runtime", ModuleState.Implemented, "Own system call ABI (exit, write byte/int/float/string); in the PS2 profile it is used when $v1 is 0, a number the real kernel does not use. This is not a PS2 interface."),
                new("EE memory map", ModuleState.Partial, "32 MiB RAM with the KSEG0/KSEG1 mirrors, the 16 KiB scratchpad and the serial port. All other I/O registers, TLB, caches and the uncached-accelerated mirror are not modelled; executable segments are write-protected."),
                new("Serial text output", ModuleState.Implemented, "A byte stored to SIO_TXFIFO (0x1000F180) is printed; SIO_ISR reads as ready. Addresses from ps2sdk's sio.h."),
                new("EE kernel services (threads, semaphores, sleep/wakeup)", ModuleState.Partial, "18 calls with numbers from ps2sdk's syscallnr.h (CreateThread, StartThread, ExitThread, SleepThread, WakeupThread, CreateSema, SignalSema, WaitSema, KExit, ...). Priority scheduling without time slicing or interrupts; return values follow convention and were not compared with real hardware. Not implemented: SetupThread/SetupHeap, i-variants, timers, alarms, interrupt handlers, DMA, ReferThreadStatus."),
                new("Graphics Synthesizer (GS) and GIF/DMA", ModuleState.Planned, "Milestone B/F. A software or Direct3D/Vulkan output is not started."),
                new("Vector units VU0/VU1", ModuleState.Planned, "Milestone F."),
                new("IOP, SIF and IRX modules", ModuleState.Planned, "Milestone B/F."),
                new("Sound (SPU2)", ModuleState.Planned, "Milestone F."),
                new("Controller input", ModuleState.Planned, "Milestone B."),
                new("Disc and file system access", ModuleState.Planned, "Only for unprotected, self-made content. Commercial disc images are not supported."),
            ],
            MipsPlugin),

        new ConsoleBackend(
            Ps3, "PlayStation 3 (Cell, PowerPC 64)", "PowerPC 64-bit big-endian + SPU", "C",
            "Not implemented. RecompRadar can parse an unprotected PowerPC 64 ELF for the portability report; nothing can be translated or run.",
            [
                new("ELF analysis (portability report only)", ModuleState.Implemented, "Header, segments, symbols and dependencies are read. No loader for recompilation."),
                new("PowerPC 64 decoder and VMX/AltiVec", ModuleState.Planned, "Milestone C."),
                new("PowerPC 64 to IR translator and backend", ModuleState.Planned, "Milestone C; 64-bit registers need an IR extension."),
                new("SPU (Cell SPE) support", ModuleState.Planned, "Milestone F."),
                new("LV2 kernel services", ModuleState.Planned, "Milestone C."),
                new("RSX graphics", ModuleState.Planned, "Milestone F."),
                new("Encrypted or signed executables (SELF)", ModuleState.NotProvided, "RecompRadar does not decrypt anything and handles no keys. Only unprotected homebrew ELF files are in scope."),
            ],
            null),

        new ConsoleBackend(
            Ps4, "PlayStation 4 (x86-64, Orbis OS)", "x86-64", "D",
            "Not implemented. The CPU is x86-64, so code could in principle run natively, but there is no loader, no system layer and no graphics layer.",
            [
                new("ELF analysis (portability report only)", ModuleState.Implemented, "Header, segments, symbols and dependencies are read."),
                new("x86-64 decoder and native mapping", ModuleState.Planned, "Milestone D."),
                new("Orbis (FreeBSD-based) system call layer", ModuleState.Planned, "Milestone D."),
                new("GPU (GNM/GCN) translation", ModuleState.Planned, "Milestone F."),
                new("Encrypted or signed executables (SELF, PFS)", ModuleState.NotProvided, "RecompRadar does not decrypt anything and handles no keys. Only unprotected, self-made test programs are in scope."),
            ],
            null),

        new ConsoleBackend(
            GenericMips32, "Generic MIPS32 (no console assumed)", "MIPS32 little- or big-endian", "A",
            "The same CPU pipeline as the PS2 backend for MIPS32 programs without a console hint, such as RecompRadar's own test programs.",
            [
                new("CPU pipeline (loader, decoder, translator, analysis, build, validation)", ModuleState.Partial, "Same subset as the PS2 backend."),
                new("RecompRadar test runtime", ModuleState.Implemented, "Own system call ABI (exit, write byte/int/float/string)."),
                new("Operating system or console services", ModuleState.NotProvided, "None; a generic MIPS32 file has no defined system interface."),
            ],
            MipsPlugin),
    ];

    public static ConsoleBackend? Find(string? id) => All.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));
}
