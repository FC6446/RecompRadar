# Recompiler plugin interfaces and the MIPS32 plugin

The `RecompRadar.Recompilation` project defines the interfaces a recompiler is built from and contains one working plugin
for 32-bit MIPS that exercises all of them. How the pieces fit into the pipeline (analysis, C# generation, Windows build,
validation) is described in [recompilation.md](recompilation.md).

**What the MIPS plugin is:** a loader, a decoder, a translator to a 32-bit IR, a reference interpreter and a code
generator for the MIPS32 integer, HI/LO and single-precision FPU subset. It is used for *self-made* test programs
(`samples/poc_*.elf`, `samples/ps2_style_selftest.elf`) and as the CPU part of the PlayStation 2 backend.

**What it is not:** it cannot run commercial software. It has no branch-likely instructions, no unaligned loads and stores,
no 64-bit or 128-bit (MMI) R5900 instructions, no coprocessor 0, no vector units and no console system interfaces. The
built programs are translated code on the .NET JIT, not ahead-of-time machine code.

## The six building blocks

| # | Block | Interface | PoC implementation |
| --- | --- | --- | --- |
| 1 | Binary loader | `IBinaryLoader` | `ElfImageLoader` – maps `PT_LOAD` segments, validates sizes, offsets and overlaps |
| 2 | Architecture decoder | `IArchitectureDecoder` | `MipsDecoder` – readable disassembly, unknown words are shown as `.word` |
| 3 | Intermediate representation | `IR.IrBlock`, `IrInstruction`, `IrTerminator` | 32-bit three-address code; 128 virtual registers (0-31 guest GPRs, HI/LO, FPU condition, 32 FPU registers as raw bits, block-local temporaries) |
| 4 | Code translator | `ICodeTranslator` | `MipsTranslator` – one basic block at a time, delay slots folded into the block |
| 5 | Runtime compatibility layer | `IRuntimeCompatibilityLayer` | `PocRuntime` – exit, write byte/integer/float/string (RecompRadar's own tiny ABI, not a console's); `Ps2Runtime` for the PlayStation 2 profile (EE memory map, serial output, 18 kernel calls) |
| 6 | Backend | `ICodeBackend` (`IsNative`), `CSharpGenerator` | `IrInterpreter` – reference interpreter, `IsNative == false`; `CSharpGenerator` produces a project that builds into a Windows program (translated code with runtime, not AOT machine code) |

A plugin groups these for one architecture:

```csharp
public interface IRecompilerPlugin
{
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }
    PluginCapabilities Capabilities { get; }   // which of the blocks it provides
    bool Supports(ElfFile elf);
    IBinaryLoader? CreateLoader();
    IArchitectureDecoder? CreateDecoder();
    ICodeTranslator? CreateTranslator();
    IRuntimeCompatibilityLayer? CreateRuntime();
    IRuntimeCompatibilityLayer? CreateRuntime(RuntimeProfile profile);   // Ps2Ee selects the PlayStation 2 runtime
    ICodeBackend? CreateBackend();

    // System call numbers that end the program, so the analysis does not treat the code behind them as reachable.
    // Optional: the default is an empty set.
    IReadOnlySet<uint> NoReturnSystemCalls { get; }
}
```

Register a plugin in-process:

```csharp
RecompilerPluginRegistry.Default.Register(new MyPowerPcPlugin());
```

Plugins are **not** discovered or loaded from disk automatically: loading third-party assemblies means running third-party
code, which RecompRadar does not do on its own.

## MIPS delay slots

The instruction after a MIPS branch executes before the branch takes effect. The translator therefore:

1. evaluates the branch condition (or reads the jump register) into a temporary **before** the delay slot,
2. translates the delay-slot instruction,
3. ends the block with the branch terminator.

This ordering is covered by tests: a delay slot that overwrites the register a branch just tested must not change the
branch decision, and a `jr` whose delay slot clobbers the jump register must still jump to the old value. A branch
inside a delay slot is refused with a clear message.

## Running the proof of concept from code

```csharp
var plugin = new MipsPocPlugin();
using var stream = File.OpenRead("samples/poc_mips32_be_sum.elf");
var elf = new ElfParser().Parse(stream).Elf!;
stream.Position = 0;
var image = plugin.CreateLoader()!.Load(stream, elf);
var machine = GuestMachine.FromImage(image);
var result = plugin.CreateBackend()!.Run(machine, plugin.CreateTranslator()!, plugin.CreateRuntime()!);
// result.Status == ExecutionStatus.Exited, result.Output == "55\n"
```

Safety limits: the loader refuses oversized or overlapping segments; the interpreter stops after a configurable number of
guest instructions; every invalid memory access becomes a `Faulted` result, never a host exception.

## Where it goes from here

See "Plan for the remaining milestones" in [recompilation.md](recompilation.md): 64-bit registers in the IR, the PS2 runtime
(EE kernel services, memory map, input, graphics), a PowerPC 64 plugin, an x86-64 loader, and a backend that produces
fully native code on machines that have a compiler toolchain. Each step is developed against small self-made programs first,
with an independent oracle, exactly like the MIPS plugin.
