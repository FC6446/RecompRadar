# Architecture

RecompRadar is a layered .NET 10 solution. The analysis core has no UI or database dependency, so everything except the
WPF shell builds and is tested on any platform.

```
                          +---------------------------+
                          |  RecompRadar.App (WPF)    |   Windows only
                          |  Views / ViewModels / DI  |
                          +------+--------+-----------+
                                 |        |
              +------------------+        +-------------------+
              v                                               v
  +-----------------------+                       +-------------------------+
  | RecompRadar.Portability|                      | RecompRadar.Infrastructure|
  | AnalysisPipeline       |                      | SqliteProjectStore        |
  | PortabilityReportBuilder|                     | JsonSettingsStore         |
  | JSON/Markdown/HTML export|                    | Serilog (JSON lines)      |
  +----+-------+-------+---+                      +-------------+-------------+
       |       |       |                                        |
       v       v       v                                        |
+-----------+ +-----------------+ +---------------------+       |
| BinaryAnalysis | Architectures | Recompilation        |       |
| ELF parser     | CPU catalog   | plugin interfaces,   |       |
| dependencies   | platform      | IR, MIPS32 PoC       |       |
| knowledge base | heuristics    |                      |       |
+-------+--------+------+--------+----------+-----------+       |
        \               |                   /                   /
         \              v                  /                   /
          +--------->  RecompRadar.Core  <-------------------+
                       models, interfaces, limits
```

| Project | Responsibility | Depends on |
| --- | --- | --- |
| `RecompRadar.Core` | Result model (`AnalysisResult`, `ElfFile`, `PortabilityReport`, ...), interfaces (`IAnalysisPipeline`, `IProjectStore`, `IPlatformHeuristic`, `IReportExporter`), `AnalysisLimits`, diagnostics | nothing |
| `RecompRadar.BinaryAnalysis` | Bounds-checked ELF parser, format detection, string scanner, knowledge base, dependency analyzer | Core |
| `RecompRadar.Architectures` | CPU architecture assessment, MIPS flag decoding, platform heuristics (PS1/PS2/PS3/PS4/PSP/GameCube/Wii/Linux) | Core |
| `RecompRadar.Recompilation` | Plugin interfaces for a future recompiler, 32-bit IR, MIPS32 integer-subset decoder/translator, reference interpreter | Core |
| `RecompRadar.Portability` | `AnalysisPipeline` orchestration, report builder, exporters | Core, BinaryAnalysis, Architectures, Recompilation |
| `RecompRadar.Infrastructure` | SQLite project store, settings, paths, structured logging | Core |
| `RecompRadar.App` | WPF shell: MVVM view models, dark theme, drag and drop | all of the above |
| `tests/RecompRadar.TestSupport` | `ElfBuilder` and a MIPS mini-assembler that create synthetic test programs | - |
| `tests/RecompRadar.Tests` | xUnit tests | all libraries |
| `tools/RecompRadar.SampleGenerator` | Writes the files in `samples/` | TestSupport |

## Analysis flow

`AnalysisPipeline.AnalyzeAsync(path, options, progress, cancellationToken)`:

1. **Validate the path.** Missing file, directory, empty file, file above the size limit: each becomes a diagnostic with a
   plain-language explanation. No exception reaches the caller.
2. **Hash.** SHA-256 is computed in 1 MiB blocks (progress is reported).
3. **Identify the format** from magic numbers. Known but unsupported formats (PS-X EXE, SELF, `~PSP`, PBP, PE, Mach-O,
   archives) are named and explained; nothing is decrypted or unpacked.
4. **Parse the ELF** (`ElfParser`) into an `ElfFile`: header, segments, sections, symbols, dynamic information, memory
   regions, entry point location. All offsets and sizes are validated with overflow-safe arithmetic; damaged files give
   partial results plus diagnostics.
5. **Architecture** (`ArchitectureAnalyzer`): CPU family, ISA details from `e_flags`, compatibility with the target.
6. **Dependencies** (`DependencyAnalyzer`): libraries, imports, exports, knowledge-base matches on library names, symbol
   names and strings in data sections.
7. **Platform hints** (`PlatformAnalyzer`): every `IPlatformHeuristic` contributes candidates with graded strength.
8. **Report** (`PortabilityReportBuilder`): the six questions, findings, missing information, runtime components, work plan.

The whole result is an immutable record graph, so it can be shown, serialised (JSON), stored (SQLite) and exported
without any further analysis.

## The evidence model

Every statement carries an `Evidence` value:

* **Verified** – read from the file or following directly from it (header fields, tables, strings that exist).
* **Inferred** – a heuristic conclusion (for example "possibly PlayStation 2"). Shown with a strength: weak, moderate or
  strong. A strong hint is still never proof; the UI, the reports and the JSON always say so.
* **Unknown** – could not be determined.

The platform heuristics never claim to identify a console. An ELF file carries no console identifier; the heuristics
combine header flags, memory layout, symbol and string names, and report each observation separately with its caveats.

## Safety rules enforced in code

* Input is only read (`FileAccess.Read`, shared read), never executed or loaded as code.
* `AnalysisLimits` bounds file size, header counts, symbol counts, string table sizes and scan volume.
* The parser converts any unexpected exception into an `ELF.INTERNAL_ERROR` diagnostic instead of crashing; tests assert
  that this code never appears, including 3 000 random byte mutations of the sample files.
* HTML export encodes all file-derived text and contains no scripts or external resources.
* The project database stores analysis results (compressed JSON) and the path and hash of the analyzed file, never a
  copy of the file.
* Plugins are registered in-process. RecompRadar does not load assemblies from disk by itself.

## Extension points

| To add ... | Implement / edit | Where it is picked up |
| --- | --- | --- |
| More known API, library or device names | JSON files in the knowledge folder (see [knowledge-base.md](knowledge-base.md)) | `KnowledgeBase.Load` |
| A platform heuristic | `IPlatformHeuristic` | `new PlatformAnalyzer(extraHeuristics)` / `AnalysisPipeline(extraHeuristics: ...)` |
| An export format | `IReportExporter` | `new ReportExportService(exporters)` |
| A recompiler for another architecture | `IRecompilerPlugin` (see [plugins.md](plugins.md)) | `RecompilerPluginRegistry.Register` |
| Another project store | `IProjectStore` | DI registration in `App.xaml.cs` |
