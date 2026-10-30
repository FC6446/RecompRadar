using RecompRadar.Core.Model;
using RecompRadar.Recompilation.IR;

namespace RecompRadar.Recompilation;

/// <summary>Read access to guest memory, independent of whether it is fully loaded or mapped on demand from a file.</summary>
public interface IMemoryReader
{
    /// <summary>Fills <paramref name="buffer"/> from guest memory. Returns false when any byte is unmapped.</summary>
    bool TryRead(ulong address, Span<byte> buffer);
}

public sealed record LoadedSegment(ulong VirtualAddress, byte[] Data, bool Readable, bool Writable, bool Executable);

/// <summary>A program image with its segments mapped at their virtual addresses.</summary>
public sealed class LoadedImage : IMemoryReader
{
    public required ulong EntryPoint { get; init; }
    public required CpuFamily Family { get; init; }
    public required Endianness Endianness { get; init; }
    public required IReadOnlyList<LoadedSegment> Segments { get; init; }

    public bool TryRead(ulong address, Span<byte> buffer)
    {
        foreach (var segment in Segments)
        {
            if (address < segment.VirtualAddress)
            {
                continue;
            }

            var delta = address - segment.VirtualAddress;
            if (delta <= (ulong)segment.Data.Length && (ulong)buffer.Length <= (ulong)segment.Data.Length - delta)
            {
                segment.Data.AsSpan((int)delta, buffer.Length).CopyTo(buffer);
                return true;
            }
        }

        return false;
    }
}

public sealed record LoaderLimits(long MaxSegmentBytes = 64L * 1024 * 1024, long MaxTotalBytes = 128L * 1024 * 1024);

public sealed class BinaryLoadException : Exception
{
    public BinaryLoadException(string message)
        : base(message)
    {
    }
}

/// <summary>Planned component 1: maps an executable file into a guest address space.</summary>
public interface IBinaryLoader
{
    string Name { get; }

    bool CanLoad(ElfFile elf);

    LoadedImage Load(Stream stream, ElfFile elf, LoaderLimits? limits = null);
}

public enum InstructionKind
{
    Other,
    Branch,
    Jump,
    Call,
    JumpRegister,
    Syscall,
    Unknown,
}

public sealed record DecodedInstruction(
    ulong Address,
    uint Raw,
    string Mnemonic,
    string Operands,
    InstructionKind Kind,
    bool HasDelaySlot,
    ulong? Target)
{
    public bool IsKnown => Kind != InstructionKind.Unknown;

    public override string ToString() => $"{Address:X8}:  {Raw:X8}  {Mnemonic} {Operands}".TrimEnd();
}

/// <summary>Planned component 2: turns machine code into a structured instruction description.</summary>
public interface IArchitectureDecoder
{
    string Name { get; }

    /// <summary>Decodes the instruction at <paramref name="address"/>, or returns null when the memory is not readable.</summary>
    DecodedInstruction? Decode(IMemoryReader memory, Endianness endianness, ulong address);
}

/// <summary>Planned component 4: lifts guest instructions into the intermediate representation.</summary>
public interface ICodeTranslator
{
    string Name { get; }

    /// <summary>Translates one basic block starting at <paramref name="address"/>. Never throws for undecodable code; the block ends with an <see cref="IrStop"/>.</summary>
    IrBlock TranslateBlock(IMemoryReader memory, Endianness endianness, ulong address);
}

public sealed record SyscallOutcome(bool Exit, int ExitCode = 0, string? Fault = null);

/// <summary>Planned component 5: provides operating-system and hardware behavior the guest program expects.</summary>
public interface IRuntimeCompatibilityLayer
{
    string Name { get; }

    SyscallOutcome HandleSyscall(Execution.GuestMachine machine);
}

public enum ExecutionStatus
{
    Exited,
    StepLimitReached,
    Faulted,
    Stopped,
}

public sealed record ExecutionLimits(long MaxGuestInstructions = 10_000_000);

public sealed record ExecutionResult(
    ExecutionStatus Status,
    int ExitCode,
    long InstructionsExecuted,
    string Output,
    string? Message,
    IReadOnlyList<uint> Registers);

/// <summary>
/// Planned component 6: consumes IR and produces behavior. The only backend today is a reference interpreter;
/// a native code generator (for example based on LLVM) would implement the same interface.
/// </summary>
public interface ICodeBackend
{
    string Name { get; }

    /// <summary>True only for backends that generate native machine code.</summary>
    bool IsNative { get; }

    ExecutionResult Run(Execution.GuestMachine machine, ICodeTranslator translator, IRuntimeCompatibilityLayer runtime, ExecutionLimits? limits = null);
}

[Flags]
public enum PluginCapabilities
{
    None = 0,
    Loader = 1,
    Decoder = 2,
    Translator = 4,
    Runtime = 8,
    Backend = 16,
}

/// <summary>A recompiler building block set for one architecture. Plugins are registered in-process; no assemblies are loaded automatically.</summary>
public interface IRecompilerPlugin
{
    string Id { get; }

    string DisplayName { get; }

    string Description { get; }

    PluginCapabilities Capabilities { get; }

    bool Supports(ElfFile elf);

    IBinaryLoader? CreateLoader();

    IArchitectureDecoder? CreateDecoder();

    ICodeTranslator? CreateTranslator();

    IRuntimeCompatibilityLayer? CreateRuntime();

    ICodeBackend? CreateBackend();
}

public sealed class RecompilerPluginRegistry
{
    private readonly List<IRecompilerPlugin> _plugins = [];

    public static RecompilerPluginRegistry Default { get; } = CreateDefault();

    public IReadOnlyList<IRecompilerPlugin> Plugins => _plugins;

    public void Register(IRecompilerPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        if (_plugins.Any(p => p.Id == plugin.Id))
        {
            throw new InvalidOperationException($"A plugin with id '{plugin.Id}' is already registered.");
        }

        _plugins.Add(plugin);
    }

    public IReadOnlyList<IRecompilerPlugin> FindSupporting(ElfFile elf) =>
        _plugins.Where(p =>
        {
            try
            {
                return p.Supports(elf);
            }
            catch (Exception)
            {
                return false;
            }
        }).ToList();

    private static RecompilerPluginRegistry CreateDefault()
    {
        var registry = new RecompilerPluginRegistry();
        registry.Register(new Mips.MipsPocPlugin());
        return registry;
    }
}
