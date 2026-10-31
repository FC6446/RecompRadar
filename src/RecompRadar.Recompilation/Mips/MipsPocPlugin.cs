using RecompRadar.Core;
using RecompRadar.Core.Model;
using RecompRadar.Recompilation.Execution;
using RecompRadar.Recompilation.Loading;
using RecompRadar.Semantics;

namespace RecompRadar.Recompilation.Mips;

/// <summary>
/// Recompiler plugin for 32-bit MIPS: it decodes and translates the integer, HI/LO and single-precision FPU subset, analyzes the control flow,
/// can run programs on a reference interpreter and can generate Windows programs. It is meant for self-made test programs and the PS2 CPU pipeline;
/// it has no 128-bit multimedia instructions, no vector units, no coprocessor 0 and no console system interfaces, so it cannot run commercial software.
/// </summary>
public sealed class MipsPocPlugin : IRecompilerPlugin
{
    /// <summary>The PoC system calls that end the program (used by the code analysis).</summary>
    public IReadOnlySet<uint> NoReturnSystemCalls { get; } = new HashSet<uint> { PocSystemCalls.Exit };

    public string Id => "recompradar.mips32-poc";

    public string DisplayName => "MIPS32 subset (integer, HI/LO, single-precision FPU)";

    public string Description =>
        "Decodes and translates the integer, HI/LO and single-precision FPU subset of 32-bit MIPS into a 32-bit IR, runs it on a reference interpreter " +
        "and can generate a Windows executable from it. Intended for self-made test programs. No branch-likely instructions, no unaligned loads/stores, " +
        "no 128-bit multimedia (MMI) or vector unit instructions, no console system interfaces.";

    public PluginCapabilities Capabilities =>
        PluginCapabilities.Loader | PluginCapabilities.Decoder | PluginCapabilities.Translator | PluginCapabilities.Runtime | PluginCapabilities.Backend;

    public bool Supports(ElfFile elf) =>
        elf.Header.Machine == ElfConstants.EM_MIPS && elf.Header.Class == ElfClass.Elf32;

    public IBinaryLoader? CreateLoader() => new ElfImageLoader();

    public IArchitectureDecoder? CreateDecoder() => new MipsDecoder();

    public ICodeTranslator? CreateTranslator() => new MipsTranslator();

    public IRuntimeCompatibilityLayer? CreateRuntime() => new PocRuntime();

    public IRuntimeCompatibilityLayer? CreateRuntime(RuntimeProfile profile) => profile switch
    {
        RuntimeProfile.Ps2Ee => new Ps2Runtime(),
        _ => new PocRuntime(),
    };

    public ICodeBackend? CreateBackend() => new IrInterpreter();
}
