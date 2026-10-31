# Recompilation framework (RecompRadar 3.0)

This document describes what the recompilation framework does **today**, how to use it, how it is verified, and what is
still missing. Every statement here is backed by code and tests in this repository; things that do not exist yet are
listed as such in [Status by milestone](#status-by-milestone).

> **Scope in one sentence:** RecompRadar can turn a *self-made* 32-bit MIPS program (the CPU used by the PlayStation 2)
> into a real Windows program, prove for a given run that the result behaves like a reference interpreter, and start it.
> It cannot run commercial console software, because none of the console's operating system, graphics, sound or input
> services exist yet, and it never will decrypt or bypass protection.

## What a build is, honestly

The pipeline translates guest machine code ahead of time into **C#**, builds that with the .NET SDK into a Windows
program (`<name>.exe` + `<name>.dll`), and the .NET JIT turns the translated code into x86-64 machine code when the program
starts. RecompRadar therefore labels such a program

> **Translated code with runtime (JIT)** – not "native".

The categories it distinguishes (`BuildCategory`) are never mixed up:

| Category | Meaning | Produced today |
| --- | --- | --- |
| Fully native (ahead-of-time machine code) | Guest code compiled to machine code before start, no JIT/interpreter for it, no runtime layer beyond the OS | No – no C/LLVM/MSVC toolchain is used (see [Why C#](#why-c-and-not-llvm)) |
| **Translated code with runtime (JIT)** | Guest code translated ahead of time to host code (C# → IL → JIT) plus a small bundled runtime | **Yes** |
| Native code with compatibility layer | Translated code on top of a re-implemented console OS/hardware layer | No – the layer does not exist |
| Interpreted | The program contains an interpreter | No (the interpreter is only the *reference* used for checking) |
| Unsupported | Nothing can be produced | Reported with reasons |

## The pipeline

`RecompilationPipeline.RunAsync` (library) – used by the **Recompiler & Build** page and by `recompradar-cli`:

| Stage | What happens | Fails when |
| --- | --- | --- |
| Read file | SHA-256, pick a plugin that can load and translate the file, map the ELF segments | no plugin supports the architecture; file unreadable |
| Analyze code | Recursive-descent discovery of blocks, functions, call graph, cross references; block-local constant propagation resolves `lui/ori` + `jr/jalr`; system call numbers are recovered | never (problems are findings) |
| Check compatibility | `CompatibilityEngine` combines header, analysis and the backend's module list | level *Unsupported* (unless an incomplete build was explicitly allowed) |
| Generate C# | One method per basic block + dispatcher, the memory image (`image.bin`, format `RRIMG1`), runtime templates, `IrSemantics.cs` copied verbatim | unsupported instructions without *allow incomplete* |
| Build with .NET | `dotnet build -c Release` of the generated project; no packages are downloaded; `Deterministic` is on | SDK missing, compile error, timeout |
| Validate | Run the built program and the reference interpreter on the same input; compare end state, exit code, instruction count, output and architectural registers | any difference → the program is *not trusted* and Play is blocked |
| Write report | `build-report.json` with decisions, hashes, stage timings, findings and the list of limitations | – |

The input file is only ever **read**. The only program that is ever started is the one RecompRadar just built, and only
by the explicit *Play* action (or `--run`).

### Generated project

```
<build folder>/
  <name>.csproj            net10.0, Optimize, Deterministic, TieredCompilation off
  Recompiled.cs            generated: dispatcher + one method per basic block (registers become locals)
  BuildInfo.cs             generated: tool version, source name and SHA-256
  image.bin                RRIMG1 container: entry point, endianness, segments with permissions
  GuestRuntime.cs          template: guest memory (alignment, mapping, permissions), CPU state, test system calls
  Program.cs               template: arguments, exit codes, run report
  IrSemantics.cs           the *same file* the reference interpreter is compiled from (division edge cases, FPU, system calls)
  out/<name>.exe           the built program
  build-report.json
```

The program accepts `--max-instructions <n>`, `--report <file.json>` and `--info`. Exit codes: the guest's own exit code
when it exits, 70 on a fault, 71 when it reaches an untranslatable instruction, 72 at the instruction limit, 64 on bad arguments.

### Why C# and not LLVM

The build machine for this project has the .NET SDK and no C/C++ compiler, no LLVM and no linker. Rather than pretend,
the backend generates C#, which the SDK compiles. The IR (`IrBlock`) is independent of that choice: an LLVM- or NativeAOT-based
backend can consume the same IR and would be the way to produce *fully native* programs (see milestone plan). Until then the
category stays "translated code with runtime (JIT)".

## How equivalence is checked

* **One definition of the arithmetic.** `Shared/IrSemantics.cs` (`Sem`, `PocSystemCalls`) is compiled into the reference
  interpreter *and* copied verbatim into every generated program. Division by zero, `INT_MIN / -1`, float conversions,
  float formatting and the test system calls cannot drift apart.
* **Differential validation per build** (stage *Validate*): same end state, exit code, guest instruction count, output text
  and all architectural registers (guest registers, HI, LO, FPU condition, FPU registers). On a fault only state, count and
  message are compared; the register dump of a faulting program is the state at the start of the faulting block.
* **Independent oracle.** `Ps2StyleSelfTest.ExpectedOutput()` computes the expected output of the self-test in plain C#,
  independently of the translator, so "interpreter and built program agree" cannot hide a shared bug.
* **Reproducibility.** The same input builds to the same `.dll` and `.exe` hashes, also from a different folder (tested).

Validation covers *one run*. It does not prove equivalence for other inputs or for code that run did not execute.

## Compatibility levels

`CompatibilityEngine` never invents a level. The levels are `Supported`, `PartiallySupported`, `Experimental`,
`Unsupported` and `Unknown`.

* **No input reaches `Supported` or `PartiallySupported` today** (a test asserts this).
* `Experimental`: the file is 32-bit MIPS, every reachable instruction can be translated and every system call is one of
  the five RecompRadar test calls. Builds and runs, but real PS2 software is not expected to work.
* `Unsupported`: with the reasons listed – unsupported instruction (named, with address), unimplemented system call (with
  number), no backend for the CPU, the backend exists only as a plan (PS3, PS4), or the chosen platform contradicts the CPU.
* **A manual platform choice never raises the level.** It only selects which backend is checked; choosing "PlayStation 2"
  for an x86-64 file is rejected with "A manual platform choice cannot change that".
* Hints from the detector are labelled *hint, not proof*; weak hints are ignored.

`ConsoleBackendRegistry` lists, per backend, every module with its state (*Implemented*, *Partial*, *Planned*,
*NotProvided*). The **Console Backends** page shows it; the same list drives the compatibility panel.

## Using it

**Windows app** – open a file on *Binary Analyzer*, then:

* **Disassembler**: functions, blocks, disassembly, IR, call graph, cross references, system calls and findings. A fixed
  panel states what is inferred or unknown.
* **Console Backends**: detection hints and module-by-module status of PS2, PS3, PS4 and generic MIPS32.
* **Recompiler & Build**: choose the target system, read the compatibility panel, press **Recompile**; when it succeeds
  press **Play** (**Stop** ends the program). When a button is disabled the reason is printed below it.

**Command line** (same pipeline):

```powershell
dotnet run --project tools/RecompRadar.Cli -c Release -- recompile samples/ps2_style_selftest.elf --platform ps2 --out build/selftest --run
```

Options: `--allow-incomplete` (translate what can be translated; untranslatable parts stop the program when reached),
`--no-validate`, `--run`. Exit code 0 = built (and, with `--run`, started), 1 = refused or failed, 2 = usage.

**Requirements:** the .NET SDK 10 must be installed to *build*; the built programs need the .NET 10 runtime. Nothing is downloaded.

## The self-test program

`samples/ps2_style_selftest.elf` (generated by `tests/RecompRadar.TestSupport/Homebrew.cs`, **self-made**, no third-party
code) is a 402-instruction MIPS32 program linked like a PS2 homebrew ELF (little endian, R5900 flag, entry at `0x00100000`).
It exercises recursion with stack frames (`fib`, `fact`, deep `sumrec`), arrays and sorting, byte-wise string handling,
HI/LO multiply and divide, conditional moves and single-precision floating point, and prints
its results through the RecompRadar test system calls. It executes about 453 650 guest instructions.

## Measured speed

`BenchmarkTests` (category `Benchmark`) runs a compute-bound program (recursive Fibonacci(30) and a 3 000 000-iteration
multiply/accumulate loop; 70.5 million guest instructions) on both engines. One run on the development machine
(Windows 11, .NET 10.0.14; timings vary by machine and load):

| Engine | Time | Guest instructions per second |
| --- | ---: | ---: |
| Reference interpreter (translated blocks cached) | 534 ms | 132 million |
| Built program (JIT), measured inside the program | 88 ms | 803 million |
| Built program, process wall clock (includes .NET start-up and JIT) | 204 ms | – |

Speed-up of the built program over the interpreter: about 6×. This is a statement about this program on this machine,
not a general performance claim.

## Known limitations

* The CPU subset: MIPS32 integer, HI/LO, `movz/movn`, `jalr`, `bltzal/bgezal`, single-precision FPU (IEEE-754; the R5900's
  FPU is *not* fully IEEE), `lwc1/swc1`, `cache/pref/sync` as no-ops. **Not**: branch-likely, `lwl/lwr/swl/swr`, `break`,
  64-bit R5900 instructions, 128-bit MMI, COP0/TLB, VU0/VU1, `add/sub` overflow traps.
* Control flow: register jumps are resolved only when the target is a constant inside the block; jump tables and function
  pointers loaded from memory are not resolved. Execution that reaches such unrecovered code faults with an explicit message
  (and validation catches the difference against the interpreter, which executes it).
* Self-modifying code and writes into executable memory are not supported and fault.
* Only the five RecompRadar test system calls exist (`$v0` = 1 exit, 2 byte, 3 integer, 4 float from `$f12`, 5 string).
  They are RecompRadar's own ABI and not any console's.
* No graphics, audio, input, files, threads, timers or interrupts.
* The generated dispatcher is one `switch`; programs with hundreds of thousands of blocks will be slow to build.

## Status by milestone

| Milestone | Scope | Status |
| --- | --- | --- |
| **A – PS2 recompiler (CPU)** | analysis, IR, translator, generator, Windows build, validation, self-test, compatibility engine, UI, CLI | **Implemented** for the subset above. Verified by 331 automated tests, including real builds. Output is translated code with runtime (JIT), not AOT machine code |
| **B – PS2 runtime** | EE kernel services, memory map, GS, pad, files | **Planned, not started.** Only the RecompRadar test runtime exists |
| **C – PS3 backend** | PowerPC 64 decoder/translator, LV2 services | **Planned, not started.** PPC64 ELF files are parsed for the portability report only |
| **D – PS4 backend** | x86-64 loader and Orbis system layer | **Planned, not started.** x86-64 ELF files are parsed for the report only |
| **E – Universal UI** | importer, disassembler, CFG, backend pages, build manager, debugger | **Partial.** Disassembler, Console Backends and Recompiler & Build exist; a separate importer, build manager and runtime debugger do not |
| **F – Extended compatibility** | VU/SPU/GPU, audio, more games | **Planned, not started** |
| Not provided, by design | decrypting SELF/EBOOT or any container, DRM circumvention, using console keys, firmware or BIOS, running commercial games | **Will not be built** |

## Plan for the remaining milestones

The order is chosen so that every step can be verified with self-made programs and an independent oracle, the way
milestone A was.

**B – PS2 runtime (next)**
1. 64-bit integer registers in the IR (R5900 GPRs are 128-bit, the low 64 bits are architecturally visible to normal code);
   extend decoder/translator/semantics and add the 64-bit and `lwl/lwr/swl/swr` instructions, branch-likely, `break`.
2. EE memory map (32 MB RAM, scratchpad, uncached/cached mirrors) in `GuestMemory`; `SYSCALL` dispatch by `$v1`.
3. Minimal EE kernel services behind `IRuntimeCompatibilityLayer`: threads, semaphores, timers; each with a self-made test program.
4. Pad input (XInput) and a file layer that loads data from a host folder (own content only).
5. A software GS output window for the simplest primitives, validated against reference images computed by the test.
6. Replace the test ABI in `Homebrew.cs` by the real syscall numbers once step 3 exists; keep the old ABI as a mode.

**C – PS3 backend**
1. IR with 64-bit registers (shared with B1), PPC64 decoder for the integer core, branch/link/count registers, big-endian memory.
2. Translator + generator reuse; differential validation against an interpreter for the same subset.
3. LV2 syscall subset for self-made homebrew; VMX/AltiVec and the SPU are separate later work.

**D – PS4 backend**
1. x86-64 loader for unprotected ELF; because the CPU is x86-64 the code can in principle run natively, so the work is
   isolation, the Orbis/FreeBSD syscall layer and the GPU – the first deliverable is a runner for self-made programs.

**E – UI**: importer page (own homebrew + project folders), build manager (history of builds with reports), trace/debugger
page that needs the runtime hooks from B.

**Fully native output:** add an LLVM- or NativeAOT-based backend behind `ICodeBackend` for machines that have the toolchain;
it would reuse the IR and the validation stage unchanged, and only then could the category "fully native" be used.
