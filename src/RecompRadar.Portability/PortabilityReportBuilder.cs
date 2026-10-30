using RecompRadar.Core;
using RecompRadar.Core.Model;

namespace RecompRadar.Portability;

/// <summary>
/// Derives the technical portability report from an analysis result.
/// The report contains only statements that follow from the analysis; it never contains percentages or guaranteed outcomes.
/// </summary>
public static class PortabilityReportBuilder
{
    private static readonly string[] ConsolePlatforms = ["ps1", "ps2", "ps3", "ps4", "psp", "vita", "gamecube", "wii"];

    public static PortabilityReport Build(AnalysisResult analysis, PortabilityTarget target, DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(target);

        var warnings = analysis.Diagnostics.Where(d => d.Severity >= Severity.Warning).ToList();
        if (!analysis.Parsed || analysis.Elf is null || analysis.Architecture is null)
        {
            return BuildNotAnalyzable(analysis, target, generatedAt, warnings);
        }

        var elf = analysis.Elf;
        var arch = analysis.Architecture;
        var deps = analysis.Dependencies ?? new DependencyAnalysis();
        var platform = analysis.Platform ?? new PlatformAssessment();

        var consoleMatches = deps.ApiMatches.Concat(deps.HardwareHints).Where(IsConsole).ToList();
        var consoleFamilies = consoleMatches.Select(m => m.ApiFamily).Distinct(StringComparer.Ordinal).ToList();
        var consoleGraphics = consoleMatches.Where(m => m.Category == "Graphics").Select(m => m.ApiFamily).Distinct(StringComparer.Ordinal).ToList();
        var pcGraphics = deps.ApiMatches.Where(m => m.Category is "Graphics" or "Multimedia" && !IsConsole(m)).Select(m => m.ApiFamily).Distinct(StringComparer.Ordinal).ToList();
        var posixFamilies = deps.ApiMatches.Where(m => m.Category is "C runtime" or "POSIX / OS" or "Threading" or "Filesystem" or "Network" or "Dynamic loading" or "C++ runtime" && !IsConsole(m))
            .Select(m => m.ApiFamily).Distinct(StringComparer.Ordinal).ToList();

        var cpuTranslation = arch.Compatibility switch
        {
            NativeCompatibility.InstructionSetMatches => "Not required (instruction set matches the target)",
            NativeCompatibility.NotDirectlyCompatible => "Required",
            _ => "Unknown",
        };

        var systemApi = consoleFamilies.Count > 0
            ? $"Console-specific system names found: {Fmt.Join(consoleFamilies, 3, "; ")}. A runtime compatibility layer would be required"
            : posixFamilies.Count > 0
                ? $"Only standard C / POSIX names found: {Fmt.Join(posixFamilies, 3, "; ")}. Windows has no complete POSIX layer; per-function compatibility was not checked"
                : deps.HasSymbols ? "Unknown (no recognised system API names)" : "Unknown (no symbol information)";

        var graphics = consoleGraphics.Count > 0
            ? $"Console-specific graphics names found: {Fmt.Join(consoleGraphics, 3, "; ")}. These would need reimplementation on a PC graphics API"
            : pcGraphics.Count > 0
                ? $"Graphics or multimedia API names found: {Fmt.Join(pcGraphics, 3, "; ")}. These exist on PCs, but version and feature compatibility were not checked"
                : "Unknown (no graphics API names found)";

        var status = arch.Compatibility switch
        {
            NativeCompatibility.InstructionSetMatches => PortingStatus.InstructionSetMatches,
            NativeCompatibility.NotDirectlyCompatible => PortingStatus.TranslationRequired,
            _ => PortingStatus.FurtherInvestigationRequired,
        };

        var overall = status switch
        {
            PortingStatus.InstructionSetMatches => "Further investigation required (CPU instruction set matches; OS, library and hardware compatibility are open)",
            PortingStatus.TranslationRequired => "Further investigation required (CPU translation and a runtime layer would be needed)",
            _ => "Further investigation required",
        };

        var missing = BuildMissing(analysis, elf, deps, platform);
        var runtime = BuildRuntimeComponents(arch, elf, deps, consoleFamilies, consoleGraphics, pcGraphics);
        var steps = BuildWorkSteps(arch, elf, deps, consoleFamilies);

        var questions = new List<ReportQuestion>
        {
            new(1, "Which architecture does the program use?", $"{arch.Description}; ELF file. {platform.Summary}.",
                arch.Facts.Select(f => $"{f.Name}: {f.Value}").Append($"Target system: {platform.Summary} (inferred, not proven)").ToList()),
            new(2, $"Is the CPU code directly compatible with {target.DisplayName}?",
                arch.Compatibility switch
                {
                    NativeCompatibility.InstructionSetMatches => "The CPU instruction set matches the target. The file is still an ELF executable, so it cannot run on Windows as-is.",
                    NativeCompatibility.NotDirectlyCompatible => $"No. {arch.DisplayName} code cannot run on {target.Architecture} directly; translation or emulation is required.",
                    _ => "Unknown. The CPU architecture is not recognised.",
                },
                [$"e_machine = {elf.Header.MachineName}", $"target architecture = {target.Architecture}"]),
            new(3, "Which dependencies are visible?", DescribeDependencies(deps, elf),
                deps.Notes.Take(3).ToList()),
            new(4, "Which information is missing?", missing.Count == 0 ? "Nothing beyond the static limits listed in the disclaimer." : $"{missing.Count} gaps were identified; see the list below.",
                missing.Take(5).ToList()),
            new(5, "Which runtime components might be required?", runtime.Count == 0 ? "None could be derived from the analysis." : Fmt.Join(runtime.Select(r => r.Name), 8) + ".",
                runtime.Select(r => $"{r.Name}: {r.Reason}").ToList()),
            new(6, "Which work steps make sense for a native port?", $"{steps.Count} steps are suggested; see the work plan.",
                steps.Take(4).Select(s => s.Title).ToList()),
        };

        return new PortabilityReport
        {
            ToolVersion = ToolInfo.Version,
            GeneratedAtUtc = generatedAt,
            Target = target.DisplayName,
            SourceArchitecture = arch.Description,
            CpuTranslation = cpuTranslation,
            SystemApiCompatibility = systemApi,
            GraphicsCompatibility = graphics,
            OverallPortingStatus = overall,
            StatusCode = status,
            Questions = questions,
            Findings = BuildFindings(analysis, elf, arch, deps, platform),
            MissingInformation = missing,
            RuntimeComponents = runtime,
            WorkSteps = steps,
            Warnings = warnings,
        };
    }

    private static bool IsConsole(ApiMatch match) => match.PlatformHints.Any(p => ConsolePlatforms.Contains(p, StringComparer.Ordinal));

    private static string DescribeDependencies(DependencyAnalysis deps, ElfFile elf)
    {
        var parts = new List<string>();
        parts.Add(deps.Libraries.Count > 0 ? $"{deps.Libraries.Count} shared libraries ({Fmt.Join(deps.Libraries, 4)})" : "no shared libraries");
        parts.Add(deps.ImportedSymbols.Count > 0 ? $"{deps.ImportedSymbols.Count} imported symbols" : "no imported symbols");
        if (deps.ApiMatches.Count > 0)
        {
            parts.Add($"{deps.ApiMatches.Count} names matching known APIs");
        }

        if (deps.HardwareHints.Count > 0)
        {
            parts.Add($"{deps.HardwareHints.Count} hardware access hints");
        }

        var text = "Visible: " + string.Join("; ", parts) + ".";
        if (!deps.HasSymbols)
        {
            text += " The file has no symbol tables, so function-level dependencies are not visible.";
        }

        return text;
    }

    private static List<string> BuildMissing(AnalysisResult analysis, ElfFile elf, DependencyAnalysis deps, PlatformAssessment platform)
    {
        var missing = new List<string>();
        if (!elf.HasSymbolTable && !elf.HasDynamicSymbolTable)
        {
            missing.Add("Function and variable names: the file contains no symbol tables.");
        }

        if (!elf.HasSectionHeaders)
        {
            missing.Add("Section headers: they are absent, so the exact purpose of memory areas (code, data, read-only data) is not labelled.");
        }

        if (!elf.HasProgramHeaders)
        {
            missing.Add("Program headers: they are absent, so the memory layout the program expects at run time is unknown.");
        }

        if (elf.Header.Type == ElfConstants.ET_REL)
        {
            missing.Add("The file is a relocatable object, not a linked program; addresses are not final.");
        }

        if (platform.Candidates.Count == 0 || platform.Candidates[0].Strength < HintStrength.Strong)
        {
            missing.Add("The exact target system is not established. An ELF file alone does not identify a console.");
        }

        missing.Add("Instruction-level analysis: no code was disassembled or followed, so call sites, control flow and the actual use of imported APIs are unknown.");
        missing.Add("Assets, file formats, middleware and copy protection used by the program were not analyzed.");
        if (analysis.Diagnostics.Any(d => d.Code.StartsWith("ELF.SEGMENT_RANGE", StringComparison.Ordinal) || d.Code.StartsWith("ELF.SECTION_RANGE", StringComparison.Ordinal)))
        {
            missing.Add("Parts of the file lie outside its own size; the file may be truncated or damaged.");
        }

        return missing;
    }

    private static List<RuntimeComponent> BuildRuntimeComponents(
        ArchitectureAssessment arch,
        ElfFile elf,
        DependencyAnalysis deps,
        List<string> consoleFamilies,
        List<string> consoleGraphics,
        List<string> pcGraphics)
    {
        var list = new List<RuntimeComponent>();
        if (arch.Compatibility == NativeCompatibility.NotDirectlyCompatible)
        {
            list.Add(new RuntimeComponent("CPU translation or emulation layer",
                $"The code is {arch.DisplayName} and the target is {arch.HostArchitecture}.", Evidence.Verified));
        }

        if (elf.Segments.Any(s => s.Type == ElfConstants.PT_LOAD))
        {
            list.Add(new RuntimeComponent("Executable loader and guest memory map",
                $"The program expects its {elf.Segments.Count(s => s.Type == ElfConstants.PT_LOAD)} loadable segment(s) at fixed virtual addresses.", Evidence.Verified));
        }

        if (deps.Libraries.Count > 0)
        {
            list.Add(new RuntimeComponent("Library resolution",
                $"The file requests shared libraries: {Fmt.Join(deps.Libraries, 4)}.", Evidence.Verified));
        }

        if (consoleFamilies.Count > 0)
        {
            list.Add(new RuntimeComponent("Operating-system / system-call compatibility layer",
                $"Console-specific API names were found: {Fmt.Join(consoleFamilies, 3, "; ")}. The extent of their use is unknown.", Evidence.Verified));
        }

        if (consoleGraphics.Count > 0)
        {
            list.Add(new RuntimeComponent("Graphics API reimplementation", $"Console graphics API names were found: {Fmt.Join(consoleGraphics, 3, "; ")}.", Evidence.Verified));
        }
        else if (pcGraphics.Count > 0)
        {
            list.Add(new RuntimeComponent("Graphics / multimedia backend", $"Names of PC graphics or multimedia APIs were found: {Fmt.Join(pcGraphics, 3, "; ")}.", Evidence.Verified));
        }

        if (deps.HardwareHints.Count > 0)
        {
            list.Add(new RuntimeComponent("Hardware access emulation",
                $"References to hardware access were found: {Fmt.Join(deps.HardwareHints.Select(h => h.Name), 3)}.", Evidence.Verified));
        }

        if (arch.TechnicalNotes.Any(n => n.Contains("R5900", StringComparison.Ordinal)))
        {
            list.Add(new RuntimeComponent("Emulation of R5900-specific instructions (multimedia instructions, non-IEEE floating point)",
                "The CPU variant flag names the R5900.", Evidence.Verified));
        }

        return list;
    }

    private static List<WorkStep> BuildWorkSteps(ArchitectureAssessment arch, ElfFile elf, DependencyAnalysis deps, List<string> consoleFamilies)
    {
        var steps = new List<string[]>();
        var hasLoads = elf.Segments.Any(s => s.Type == ElfConstants.PT_LOAD);
        steps.Add(["Confirm the input", "Make sure the file is an unprotected executable you are entitled to analyze, and keep the SHA-256 for reference."]);
        steps.Add(hasLoads
            ? ["Build an executable loader", $"Map the {elf.Segments.Count(s => s.Type == ElfConstants.PT_LOAD)} loadable segment(s) at their virtual addresses and set up the stack and entry point ({Fmt.Hex(elf.Header.Entry)})."]
            : ["Establish the memory layout", "The file has no loadable segments; determine the intended load addresses from other sources."]);

        if (arch.Compatibility == NativeCompatibility.NotDirectlyCompatible)
        {
            steps.Add(["Implement an instruction decoder", $"Decode {arch.DisplayName} instructions. " + string.Join(" ", arch.TechnicalNotes)]);
            steps.Add(["Define an intermediate representation and lift code into it", "Translate decoded instructions into an architecture-neutral IR so that optimisations and a native backend can be shared."]);
        }
        else if (arch.Compatibility == NativeCompatibility.InstructionSetMatches)
        {
            steps.Add(["Decide how to run the code", "The CPU code needs no translation. Options are repackaging into a Windows executable or running it behind a loader that provides the system interfaces."]);
        }

        steps.Add(elf.HasSymbolTable || elf.HasDynamicSymbolTable
            ? ["Use symbols to recover functions", $"{elf.Symbols.Count(s => s.IsFunction && !s.IsUndefined)} function symbols are present and can seed function discovery."]
            : ["Discover functions without symbols", "The file is stripped. Function boundaries must be found by control-flow analysis."]);

        steps.Add(consoleFamilies.Count > 0
            ? ["Reimplement the system interfaces", $"Provide the behavior behind the API names found: {Fmt.Join(consoleFamilies, 4, "; ")}."]
            : ["Identify the system interfaces", "No recognised API names were found. Trace system calls and hardware accesses to learn what the program needs."]);

        if (arch.Compatibility == NativeCompatibility.NotDirectlyCompatible)
        {
            steps.Add(["Choose a native backend", $"Generate {arch.HostArchitecture} code from the IR (for example with LLVM), or begin with an interpreter to validate the translation."]);
        }

        steps.Add(["Validate with small test programs", "Start with self-made programs of known behavior and compare results instruction by instruction before attempting larger inputs."]);

        return steps.Select((s, i) => new WorkStep(i + 1, s[0], s[1])).ToList();
    }

    private static List<Fact> BuildFindings(AnalysisResult analysis, ElfFile elf, ArchitectureAssessment arch, DependencyAnalysis deps, PlatformAssessment platform)
    {
        var exec = elf.ExecutableRegions.Where(r => r.Source == "segment").ToList();
        if (exec.Count == 0)
        {
            exec = elf.ExecutableRegions.ToList();
        }

        var facts = new List<Fact>
        {
            new("File format", analysis.Format.DisplayName, Evidence.Verified),
            new("Architecture", arch.Description, Evidence.Verified),
            new("Entry point", elf.Header.Entry == 0 ? "0x0" : $"{Fmt.Hex(elf.Header.Entry)}{(elf.EntryPointLocation is null ? "" : " in " + elf.EntryPointLocation)}", Evidence.Verified),
            new("Program headers (segments)", elf.HasProgramHeaders ? elf.Segments.Count.ToString() : "none", Evidence.Verified),
            new("Section headers", elf.HasSectionHeaders ? elf.Sections.Count.ToString() : "none (stripped or absent)", Evidence.Verified),
            new("Executable regions", exec.Count == 0 ? "none" : $"{exec.Count} ({Fmt.Size(Fmt.SaturatingSum(exec.Select(r => r.Size)))})", Evidence.Verified),
            new("Symbol tables", elf.HasSymbolTable || elf.HasDynamicSymbolTable
                ? $"{(elf.HasSymbolTable ? $".symtab ({elf.SymbolTableEntryCount})" : "")}{(elf.HasSymbolTable && elf.HasDynamicSymbolTable ? ", " : "")}{(elf.HasDynamicSymbolTable ? $".dynsym ({elf.DynamicSymbolEntryCount})" : "")}"
                : "none (stripped)", Evidence.Verified),
            new("Shared libraries", deps.Libraries.Count.ToString(), Evidence.Verified),
            new("Imported symbols", deps.ImportedSymbols.Count.ToString(), Evidence.Verified),
            new("Target system", platform.Summary, Evidence.Inferred, platform.Disclaimer),
        };

        return facts;
    }

    private static PortabilityReport BuildNotAnalyzable(AnalysisResult analysis, PortabilityTarget target, DateTimeOffset generatedAt, List<Diagnostic> warnings)
    {
        var reason = analysis.Format.IsSupported
            ? "The file could not be analyzed; see the warnings for the reason."
            : analysis.Format.Explanation;
        var notDetermined = "Not determined (file could not be analyzed)";

        string[] steps =
        [
            analysis.Format.IsSupported
                ? "Check the warnings and repair or replace the damaged file."
                : "Provide an unprotected ELF executable that you are entitled to analyze, or add a loader plugin for this format.",
            "RecompRadar does not decrypt, unpack or bypass protection of any file.",
        ];

        var questions = new List<ReportQuestion>
        {
            new(1, "Which architecture does the program use?", notDetermined, [reason]),
            new(2, $"Is the CPU code directly compatible with {target.DisplayName}?", notDetermined, []),
            new(3, "Which dependencies are visible?", notDetermined, []),
            new(4, "Which information is missing?", "Everything: the file was not analyzed.", [reason]),
            new(5, "Which runtime components might be required?", notDetermined, []),
            new(6, "Which work steps make sense for a native port?", "Resolve the input problem first.", steps),
        };

        return new PortabilityReport
        {
            ToolVersion = ToolInfo.Version,
            GeneratedAtUtc = generatedAt,
            Target = target.DisplayName,
            SourceArchitecture = "Unknown",
            CpuTranslation = "Unknown",
            SystemApiCompatibility = "Unknown",
            GraphicsCompatibility = "Unknown",
            OverallPortingStatus = "Not analyzable",
            StatusCode = PortingStatus.NotAnalyzable,
            Questions = questions,
            Findings = [new Fact("File format", analysis.Format.DisplayName, Evidence.Verified, analysis.Format.Explanation)],
            MissingInformation = [reason],
            WorkSteps = steps.Select((s, i) => new WorkStep(i + 1, s, "")).ToList(),
            Warnings = warnings,
        };
    }
}
