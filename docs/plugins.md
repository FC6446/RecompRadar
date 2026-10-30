# Recompiler plugin interfaces and the MIPS32 proof of concept

RecompRadar is an analysis tool today. The `RecompRadar.Recompilation` project defines the interfaces a future
recompiler would be built from, and contains one small, working proof of concept that exercises all of them.

**What the proof of concept is:** a decoder, a translator to a 32-bit integer IR and a reference interpreter for a small
integer subset of MIPS32, used to run *self-made* test programs (see `samples/poc_*.elf`).

**What it is not:** it cannot run commercial software. It has no FPU, no HI/LO registers (`mult`, `div`, `mfhi`), no
coprocessors, no branch-likely instructions, no unaligned loads, no console system interfaces, and it is not a native code
generator. The UI does not offer any "run" or "recompile" action, because none would be honest.

## The six building blocks

| # | Block | Interface | PoC implementation |
| --- | --- | --- | --- |
| 1 | Binary loader | `IBinaryLoader` | `ElfImageLoader` – maps `PT_LOAD` segments, validates sizes, offsets and overlaps |
| 2 | Architecture decoder | `IArchitectureDecoder` | `MipsDecoder` – readable disassembly, unknown words are shown as `.word` |
| 3 | Intermediate representation | `IR.IrBlock`, `IrInstruction`, `IrTerminator` | 32-bit three-address code; 64 virtual registers (0-31 mirror the guest registers) |
| 4 | Code translator | `ICodeTranslator` | `MipsTranslator` – one basic block at a time, delay slots folded into the block |
| 5 | Runtime compatibility layer | `IRuntimeCompatibilityLayer` | `PocRuntime` – exit, write byte, write integer (RecompRadar's own tiny ABI, not a console's) |
| 6 | Native compiler backend | `ICodeBackend` (`IsNative`) | `IrInterpreter` – reference interpreter, `IsNative == false` |

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
    ICodeBackend? CreateBackend();
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
var image = plugin.CreateLoader()!.Load(stream, elf);
var machine = GuestMachine.FromImage(image);
var result = plugin.CreateBackend()!.Run(machine, plugin.CreateTranslator()!, plugin.CreateRuntime()!);
// result.Status == ExecutionStatus.Exited, result.Output == "55\n"
```

Safety limits: the loader refuses oversized or overlapping segments; the interpreter stops after a configurable number of
guest instructions; every invalid memory access becomes a `Faulted` result, never a host exception.

## Where a real recompiler would go from here

1. More decoders (MIPS III + R5900 MMI, PowerPC with paired singles, ARM/Thumb).
2. A richer IR (64-bit values, floating point, vector operations, explicit memory model).
3. Static control-flow recovery and function discovery (uses the symbols the analyzer already reads).
4. A native backend (for example LLVM IR emission) behind the existing `ICodeBackend`.
5. Per-platform runtime layers (operating-system calls, graphics, audio, input) behind `IRuntimeCompatibilityLayer`.

Each step can be developed against small self-made programs first, exactly like the proof of concept.
