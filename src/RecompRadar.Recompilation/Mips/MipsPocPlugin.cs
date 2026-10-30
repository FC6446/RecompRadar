using RecompRadar.Core;
using RecompRadar.Core.Model;
using RecompRadar.Recompilation.Execution;
using RecompRadar.Recompilation.Loading;

namespace RecompRadar.Recompilation.Mips;

/// <summary>
/// Educational proof of concept for 32-bit MIPS. It decodes and translates a small integer subset and can execute
/// self-made test programs on a reference interpreter. It cannot run commercial software: it has no FPU, no HI/LO,
/// no coprocessors and no console system interfaces.
/// </summary>
public sealed class MipsPocPlugin : IRecompilerPlugin
{
    public string Id => "recompradar.mips32-poc";

    public string DisplayName => "MIPS32 proof of concept (integer subset)";

    public string Description =>
        "Decodes and translates a small integer subset of 32-bit MIPS into a 32-bit IR and runs it on a reference interpreter. " +
        "Intended for self-made test programs. No FPU, no HI/LO registers, no coprocessors, no console system interfaces.";

    public PluginCapabilities Capabilities =>
        PluginCapabilities.Loader | PluginCapabilities.Decoder | PluginCapabilities.Translator | PluginCapabilities.Runtime | PluginCapabilities.Backend;

    public bool Supports(ElfFile elf) =>
        elf.Header.Machine == ElfConstants.EM_MIPS && elf.Header.Class == ElfClass.Elf32;

    public IBinaryLoader? CreateLoader() => new ElfImageLoader();

    public IArchitectureDecoder? CreateDecoder() => new MipsDecoder();

    public ICodeTranslator? CreateTranslator() => new MipsTranslator();

    public IRuntimeCompatibilityLayer? CreateRuntime() => new PocRuntime();

    public ICodeBackend? CreateBackend() => new IrInterpreter();
}
