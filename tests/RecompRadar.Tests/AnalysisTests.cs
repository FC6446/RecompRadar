using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RecompRadar.Architectures;
using RecompRadar.BinaryAnalysis;
using RecompRadar.BinaryAnalysis.Dependencies;
using RecompRadar.Core;
using RecompRadar.Core.Model;
using RecompRadar.Portability;
using RecompRadar.TestSupport;

namespace RecompRadar.Tests;

public class FormatDetectorTests
{
    private static byte[] Padded(params byte[] head) => head.Concat(new byte[64]).ToArray();

    [Fact]
    public void Recognises_elf() => Assert.True(FormatDetector.Detect(Padded(0x7F, (byte)'E', (byte)'L', (byte)'F')).IsSupported);

    [Theory]
    [InlineData("PS-X EXE", "ps-x-exe")]
    [InlineData("~PSP", "psp-encrypted")]
    [InlineData("SCE\0", "sce-self")]
    [InlineData("MZ", "pe")]
    public void Recognises_known_foreign_formats_but_does_not_claim_support(string magic, string id)
    {
        var detection = FormatDetector.Detect(Encoding.ASCII.GetBytes(magic).Concat(new byte[64]).ToArray());

        Assert.Equal(id, detection.Id);
        Assert.False(detection.IsSupported);
        Assert.False(string.IsNullOrWhiteSpace(detection.Explanation));
    }

    [Fact]
    public void Recognises_pbp_and_orbis_self_and_macho()
    {
        Assert.Equal("pbp", FormatDetector.Detect(Padded(0, (byte)'P', (byte)'B', (byte)'P')).Id);
        Assert.Equal("sce-self-orbis", FormatDetector.Detect(Padded(0x4F, 0x15, 0x3D, 0x1D)).Id);
        Assert.Equal("mach-o", FormatDetector.Detect(Padded(0xCF, 0xFA, 0xED, 0xFE)).Id);
        Assert.Equal("mach-o-fat-or-java", FormatDetector.Detect(Padded(0xCA, 0xFE, 0xBA, 0xBE)).Id);
    }

    [Theory]
    [InlineData("~PSP")]
    [InlineData("SCE\0")]
    public void Protected_formats_state_that_nothing_is_decrypted(string magic)
    {
        var detection = FormatDetector.Detect(Encoding.ASCII.GetBytes(magic).Concat(new byte[64]).ToArray());

        Assert.Contains("does not decrypt", detection.Explanation);
    }

    [Fact]
    public void Unknown_and_empty_input_is_reported_as_unknown()
    {
        Assert.Equal("unknown", FormatDetector.Detect(Encoding.ASCII.GetBytes("hello world")).Id);
        Assert.Equal("unknown", FormatDetector.Detect([]).Id);
        Assert.Equal("unknown", FormatDetector.Detect([0x7F]).Id);
    }
}

public class ArchitectureAnalyzerTests
{
    private static ArchitectureAssessment Assess(ElfBuilder builder, PortabilityTarget? target = null)
    {
        var elf = T.ParseOk(builder.WithCode([0, 0, 0, 0]).Build());
        return ArchitectureAnalyzer.Analyze(elf.Header, target ?? PortabilityTarget.WindowsX64);
    }

    [Fact]
    public void Mips_is_not_directly_compatible_with_x64()
    {
        var a = Assess(new ElfBuilder { Machine = 8, BigEndian = true, Flags = 0x50001000 });

        Assert.Equal(CpuFamily.Mips, a.Family);
        Assert.Equal("MIPS (32-bit, big endian)", a.Description);
        Assert.Equal(NativeCompatibility.NotDirectlyCompatible, a.Compatibility);
        Assert.Equal("Not directly compatible", a.NativeExecutionStatus);
        Assert.Equal("x86-64", a.HostArchitecture);
        Assert.Contains("Code translation", a.RequiredWork);
        Assert.Contains(a.Facts, f => f.Name == "MIPS instruction set level" && f.Value == "MIPS32" && f.Evidence == Evidence.Verified);
        Assert.Contains(a.Facts, f => f.Name == "MIPS ABI" && f.Value == "O32");
        Assert.Contains(a.TechnicalNotes, n => n.Contains("delay slot"));
    }

    [Fact]
    public void X86_64_matches_the_instruction_set_but_still_needs_system_work()
    {
        var a = Assess(ElfBuilder.X64Linux());

        Assert.Equal(CpuFamily.X86_64, a.Family);
        Assert.Equal(NativeCompatibility.InstructionSetMatches, a.Compatibility);
        Assert.Contains("cannot run on Windows as-is", a.NativeExecutionStatus);
        Assert.Contains("No CPU translation", a.RequiredWork);
    }

    [Fact]
    public void R5900_flag_is_decoded_and_noted()
    {
        var a = Assess(new ElfBuilder { Machine = 8, Flags = 0x20924001 });

        Assert.Contains(a.Facts, f => f.Name == "MIPS CPU variant" && f.Value.Contains("R5900"));
        Assert.Contains(a.Facts, f => f.Name == "MIPS instruction set level" && f.Value == "MIPS III");
        Assert.Contains(a.Facts, f => f.Name == "MIPS ABI" && f.Value == "EABI64");
        Assert.Contains(a.TechnicalNotes, n => n.Contains("R5900"));
    }

    [Fact]
    public void Unknown_machine_is_reported_as_unknown_not_guessed()
    {
        var a = Assess(new ElfBuilder { Machine = 0x9999 });

        Assert.Equal(CpuFamily.Unknown, a.Family);
        Assert.Equal(NativeCompatibility.Unknown, a.Compatibility);
        Assert.Equal("Unknown", a.NativeExecutionStatus);
    }

    [Fact]
    public void Compatibility_depends_on_the_chosen_target()
    {
        var onX64 = Assess(new ElfBuilder { Is64 = true, Machine = 183 });
        var onArm = Assess(new ElfBuilder { Is64 = true, Machine = 183 }, new PortabilityTarget("Windows", "ARM64"));

        Assert.Equal(NativeCompatibility.NotDirectlyCompatible, onX64.Compatibility);
        Assert.Equal(NativeCompatibility.InstructionSetMatches, onArm.Compatibility);
    }

    [Fact]
    public void PowerPc64_abi_version_is_read_from_flags()
    {
        var a = Assess(new ElfBuilder { Is64 = true, BigEndian = true, Machine = 21, Flags = 2 });

        Assert.Contains(a.Facts, f => f.Name == "PowerPC 64-bit ABI" && f.Value == "ELFv2");
    }
}

public class PlatformAnalyzerTests
{
    private static AnalysisResult AnalyzeSample(string fileName)
    {
        var sample = SampleFiles.All().Single(s => s.FileName == fileName);
        return T.Analyze(sample.Bytes, fileName).Analysis;
    }

    [Theory]
    [InlineData("synthetic_mips32_le_r5900_flag.elf", "Possibly PlayStation 2 (strong hint)")]
    [InlineData("synthetic_mips32_le_psp_prx_like.elf", "Possibly PlayStation Portable (PSP) (moderate hint)")]
    [InlineData("synthetic_ppc64_be_ps3_like.elf", "Possibly PlayStation 3 (strong hint)")]
    [InlineData("synthetic_x86_64_ps4_like.elf", "Possibly PlayStation 4 (strong hint)")]
    [InlineData("synthetic_ppc32_be_gamecube_like.elf", "Possibly Nintendo GameCube or Nintendo Wii (moderate hint)")]
    public void Console_like_samples_get_graded_hints(string file, string expectedSummary)
    {
        var result = AnalyzeSample(file);

        Assert.Equal(expectedSummary, result.Platform!.Summary);
        Assert.Contains("not proof", result.Platform.Disclaimer);
        Assert.All(result.Platform.Candidates, c =>
        {
            Assert.NotEmpty(c.Reasons);
            Assert.All(c.Reasons, r => Assert.NotEqual(Evidence.Unknown, r.Evidence));
            Assert.NotEmpty(c.Caveats);
        });
    }

    [Fact]
    public void Plain_linux_program_is_not_mistaken_for_a_console()
    {
        var result = AnalyzeSample("synthetic_x86_64_linux_dynamic.elf");

        Assert.DoesNotContain(result.Platform!.Candidates, c => c.PlatformId is "ps1" or "ps2" or "ps3" or "ps4" or "psp" or "gamecube" or "wii");
        Assert.Contains(result.Platform.Candidates, c => c.PlatformId == "linux");
    }

    [Fact]
    public void File_without_indicators_is_left_undetermined()
    {
        var result = AnalyzeSample("synthetic_aarch64_stripped.elf");

        Assert.Empty(result.Platform!.Candidates);
        Assert.StartsWith("Target system could not be determined", result.Platform.Summary);
    }

    [Fact]
    public void Ps4_like_file_reports_that_the_cpu_needs_no_translation()
    {
        var result = AnalyzeSample("synthetic_x86_64_ps4_like.elf");

        Assert.Equal(NativeCompatibility.InstructionSetMatches, result.Architecture!.Compatibility);
        var ps4 = result.Platform!.Candidates.Single(c => c.PlatformId == "ps4");
        Assert.Contains(ps4.Caveats, c => c.Contains("not in CPU translation"));
    }

    [Fact]
    public void Weak_evidence_alone_never_produces_a_strong_hint()
    {
        // A plain MIPS I little-endian ELF only matches on the CPU and must stay a weak hint.
        var bytes = new ElfBuilder { Machine = 8, Flags = 0 }.WithCode([0, 0, 0, 0], 0x00400000).Build();

        var result = T.Analyze(bytes).Analysis;

        var ps1 = Assert.Single(result.Platform!.Candidates, c => c.PlatformId == "ps1");
        Assert.Equal(HintStrength.Weak, ps1.Strength);
        Assert.Contains("weak hint", result.Platform.Summary);
    }
}

public class DependencyAnalyzerTests
{
    [Fact]
    public void Lists_libraries_imports_and_matches_from_the_file()
    {
        var sample = SampleFiles.All().Single(s => s.FileName == "synthetic_x86_64_linux_dynamic.elf");

        var deps = T.Analyze(sample.Bytes).Analysis.Dependencies!;

        Assert.Equal(["libc.so.6", "libm.so.6", "libSDL2-2.0.so.0", "libGL.so.1"], deps.Libraries);
        Assert.Contains("printf", deps.ImportedSymbols);
        Assert.Contains("SDL_Init", deps.ImportedSymbols);
        Assert.True(deps.HasSymbols);
        Assert.True(deps.HasDynamicSection);
        Assert.Contains(deps.ApiMatches, m => m.Name == "printf" && m.Kind == ApiMatchKind.ImportedSymbol && m.Category == "C runtime");
        Assert.Contains(deps.ApiMatches, m => m.Name == "SDL_Init" && m.ApiFamily.Contains("SDL"));
        Assert.Contains(deps.ApiMatches, m => m.Name == "glClear" && m.Category == "Graphics");
        Assert.Contains(deps.ApiMatches, m => m.Name == "libSDL2-2.0.so.0" && m.Kind == ApiMatchKind.Library);
        Assert.All(deps.ApiMatches, m => Assert.Equal(Evidence.Verified, m.Evidence));
    }

    [Fact]
    public void Reports_hardware_hints_from_imports_and_strings()
    {
        var sample = SampleFiles.All().Single(s => s.FileName == "synthetic_x86_64_linux_dynamic.elf");

        var deps = T.Analyze(sample.Bytes).Analysis.Dependencies!;

        Assert.Contains(deps.HardwareHints, h => h.Name == "ioctl" && h.Kind == ApiMatchKind.ImportedSymbol);
        Assert.Contains(deps.HardwareHints, h => h.Name == "/dev/fb0" && h.Kind == ApiMatchKind.String);
        Assert.DoesNotContain(deps.ApiMatches, m => m.Name == "ioctl");
        Assert.True(deps.StringsScanned > 0);
    }

    [Fact]
    public void Does_not_invent_matches_for_unknown_names()
    {
        var builder = ElfBuilder.X64Linux();
        builder.WithCode([0xC3]);
        builder.Symbols.AddRange([SymbolSpec.Function("my_private_function"), SymbolSpec.Function("zzz_helper"), SymbolSpec.Import("totally_unknown_import")]);

        var deps = T.Analyze(builder.Build()).Analysis.Dependencies!;

        Assert.Contains("totally_unknown_import", deps.ImportedSymbols);
        Assert.Empty(deps.ApiMatches);
        Assert.Empty(deps.HardwareHints);
    }

    [Fact]
    public void Says_clearly_when_there_are_no_symbols()
    {
        var sample = SampleFiles.All().Single(s => s.FileName == "synthetic_aarch64_stripped.elf");

        var deps = T.Analyze(sample.Bytes).Analysis.Dependencies!;

        Assert.False(deps.HasSymbols);
        Assert.Empty(deps.ImportedSymbols);
        Assert.Contains(deps.Notes, n => n.Contains("No symbol tables"));
        Assert.Contains(deps.Notes, n => n.Contains("no dynamic section"));
    }

    [Fact]
    public void Matches_symbols_defined_in_statically_linked_programs()
    {
        var sample = SampleFiles.All().Single(s => s.FileName == "synthetic_mips32_le_r5900_flag.elf");

        var deps = T.Analyze(sample.Bytes).Analysis.Dependencies!;

        Assert.Contains(deps.ApiMatches, m => m.Name == "SifInitRpc" && m.Kind == ApiMatchKind.DefinedSymbol && m.PlatformHints.Contains("ps2"));
        Assert.Contains(deps.ApiMatches, m => m.Name == "sceGsResetPath" && m.PlatformHints.Contains("ps2"));
        Assert.Contains(deps.ApiMatches, m => m.Kind == ApiMatchKind.String && m.Name.StartsWith("cdrom0:", StringComparison.Ordinal));
    }

    [Fact]
    public void Supports_an_extra_knowledge_directory()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("custom.json"), """
            { "schemaVersion": 1, "name": "custom", "version": "1",
              "rules": [ { "id": "custom.rule", "kind": "symbol", "match": "prefix", "patterns": ["acme_"], "category": "Middleware", "apiFamily": "Acme engine", "platforms": [], "description": "Acme middleware" } ] }
            """);
        var builder = ElfBuilder.X64Linux();
        builder.WithCode([0xC3]);
        builder.Symbols.Add(SymbolSpec.Import("acme_init"));

        var deps = T.Analyze(builder.Build(), options: new AnalysisOptions { ExtraKnowledgeDirectory = dir.Path }).Analysis.Dependencies!;

        Assert.Contains(deps.ApiMatches, m => m.Name == "acme_init" && m.ApiFamily == "Acme engine" && m.RuleId == "custom.rule");
    }
}

public class KnowledgeBaseTests
{
    [Fact]
    public void Built_in_database_is_loaded_and_documented()
    {
        var kb = KnowledgeBase.BuiltIn;

        Assert.True(kb.Rules.Count > 50);
        Assert.Empty(kb.LoadWarnings);
        Assert.Contains("rules", kb.Description);
        Assert.All(kb.Rules, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Id));
            Assert.NotEmpty(r.Patterns);
            Assert.False(string.IsNullOrWhiteSpace(r.Category));
            Assert.False(string.IsNullOrWhiteSpace(r.Description));
        });
        Assert.Equal(kb.Rules.Count, kb.Rules.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void Matching_is_case_sensitive_for_symbols_and_insensitive_for_libraries()
    {
        var kb = KnowledgeBase.BuiltIn;

        Assert.NotEmpty(kb.Match(RuleKind.Symbol, "printf"));
        Assert.Empty(kb.Match(RuleKind.Symbol, "PRINTF"));
        Assert.NotEmpty(kb.Match(RuleKind.Library, "LIBC.SO.6"));
        Assert.Empty(kb.Match(RuleKind.Symbol, ""));
    }

    [Fact]
    public void Broken_or_invalid_extra_files_are_skipped_with_a_warning()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("broken.json"), "{ this is not json");
        File.WriteAllText(dir.File("invalid-rule.json"), """{ "rules": [ { "id": "", "kind": "symbol", "patterns": ["x"] }, { "id": "ok", "kind": "symbol", "patterns": ["good_name"], "category": "C", "apiFamily": "F", "description": "d" } ] }""");

        var kb = KnowledgeBase.Load(dir.Path);

        Assert.True(kb.LoadWarnings.Count >= 2);
        Assert.NotEmpty(kb.Match(RuleKind.Symbol, "printf"));
        Assert.NotEmpty(kb.Match(RuleKind.Symbol, "good_name"));
    }

    [Fact]
    public void Missing_extra_directory_only_produces_a_warning()
    {
        var kb = KnowledgeBase.Load(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N")));

        Assert.Single(kb.LoadWarnings);
        Assert.NotEmpty(kb.Match(RuleKind.Symbol, "printf"));
    }
}

public class PortabilityReportTests
{
    private static AnalysisBundle Sample(string name) =>
        T.Analyze(SampleFiles.All().Single(s => s.FileName == name).Bytes, name);

    [Fact]
    public void Mips_report_uses_only_findings_and_asks_for_further_investigation()
    {
        var report = Sample("poc_mips32_be_sum.elf").Report;

        Assert.Equal("Windows x86-64", report.Target);
        Assert.Equal("MIPS (32-bit, big endian)", report.SourceArchitecture);
        Assert.Equal("Required", report.CpuTranslation);
        Assert.StartsWith("Further investigation required", report.OverallPortingStatus);
        Assert.Equal(PortingStatus.TranslationRequired, report.StatusCode);
        Assert.StartsWith("Unknown", report.SystemApiCompatibility);
        Assert.StartsWith("Unknown", report.GraphicsCompatibility);
        Assert.Equal(6, report.Questions.Count);
        Assert.Equal(Enumerable.Range(1, 6), report.Questions.Select(q => q.Number));
        Assert.Contains(report.RuntimeComponents, c => c.Name.Contains("CPU translation"));
        Assert.NotEmpty(report.WorkSteps);
        Assert.Contains(report.MissingInformation, m => m.Contains("Function and variable names"));
    }

    [Fact]
    public void Report_never_contains_percentages_or_guarantees()
    {
        foreach (var sample in SampleFiles.All())
        {
            var report = T.Analyze(sample.Bytes, sample.FileName).Report;
            var text = JsonSerializer.Serialize(report, RecompRadarJson.Options);

            Assert.DoesNotContain("%", text);
            Assert.DoesNotContain("guarantee", text.Replace("guaranteed outcomes", string.Empty), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("fully compatible", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("will run", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void X64_report_states_that_translation_is_not_required_but_work_remains()
    {
        var report = Sample("synthetic_x86_64_linux_dynamic.elf").Report;

        Assert.StartsWith("Not required", report.CpuTranslation);
        Assert.Equal(PortingStatus.InstructionSetMatches, report.StatusCode);
        Assert.Contains("C / POSIX", report.SystemApiCompatibility);
        Assert.Contains("Graphics or multimedia", report.GraphicsCompatibility);
        Assert.Contains(report.RuntimeComponents, c => c.Name == "Library resolution");
        Assert.Contains(report.RuntimeComponents, c => c.Name == "Hardware access emulation");
        Assert.DoesNotContain(report.RuntimeComponents, c => c.Name.Contains("CPU translation"));
    }

    [Fact]
    public void Console_api_names_lead_to_a_runtime_layer_requirement()
    {
        var report = Sample("synthetic_mips32_le_psp_prx_like.elf").Report;

        Assert.Contains("Console-specific", report.SystemApiCompatibility);
        Assert.Contains("Console-specific graphics", report.GraphicsCompatibility);
        Assert.Contains(report.RuntimeComponents, c => c.Name.Contains("system-call compatibility layer"));
    }

    [Fact]
    public void Unsupported_and_damaged_files_are_not_analyzable_and_say_why()
    {
        var text = T.Analyze(Encoding.ASCII.GetBytes("just some text"), "x.bin").Report;
        var truncated = Sample("corrupt_truncated_header.elf").Report;

        foreach (var report in new[] { text, truncated })
        {
            Assert.Equal(PortingStatus.NotAnalyzable, report.StatusCode);
            Assert.Equal("Not analyzable", report.OverallPortingStatus);
            Assert.Equal("Unknown", report.SourceArchitecture);
            Assert.Equal(6, report.Questions.Count);
            Assert.NotEmpty(report.MissingInformation);
            Assert.NotEmpty(report.Warnings);
        }
    }

    [Fact]
    public void Warnings_from_the_analysis_are_carried_into_the_report()
    {
        var report = Sample("corrupt_bad_offsets.elf").Report;

        Assert.Contains(report.Warnings, w => w.Code.StartsWith("ELF.", StringComparison.Ordinal));
        Assert.Contains(report.MissingInformation, m => m.Contains("Program headers") || m.Contains("Section headers"));
    }

    [Fact]
    public void Target_platform_is_always_listed_as_not_proven()
    {
        var report = Sample("synthetic_mips32_le_r5900_flag.elf").Report;

        var finding = Assert.Single(report.Findings, f => f.Name == "Target system");
        Assert.Equal(Evidence.Inferred, finding.Evidence);
        Assert.Contains(report.Questions[0].Basis, b => b.Contains("inferred, not proven"));
    }
}

public class AnalysisPipelineTests
{
    private sealed class RecordingProgress : IProgress<AnalysisProgress>
    {
        public List<AnalysisProgress> Items { get; } = [];

        public void Report(AnalysisProgress value) => Items.Add(value);
    }

    private static readonly AnalysisOptions Options = new();

    [Fact]
    public async Task Computes_hash_size_and_name_of_the_analyzed_file()
    {
        var bytes = SampleFiles.All().Single(s => s.FileName == "poc_mips32_be_sum.elf").Bytes;
        using var dir = new TempDir();
        var path = dir.Write("sum.elf", bytes);

        var bundle = await new AnalysisPipeline().AnalyzeAsync(path, Options);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bundle.Analysis.File.Sha256);
        Assert.Equal(bytes.Length, bundle.Analysis.File.SizeBytes);
        Assert.Equal("sum.elf", bundle.Analysis.File.FileName);
        Assert.Equal(Path.GetFullPath(path), bundle.Analysis.File.FullPath);
        Assert.True(bundle.Analysis.Parsed);
        Assert.Equal("elf", bundle.Analysis.Format.Id);
        Assert.Equal(ToolInfo.Version, bundle.Analysis.ToolVersion);
    }

    [Fact]
    public async Task Never_modifies_the_analyzed_file()
    {
        using var dir = new TempDir();
        var path = dir.Write("a.elf", SampleFiles.All().First().Bytes);
        var before = (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path));

        await new AnalysisPipeline().AnalyzeAsync(path, Options);

        Assert.Equal(before.Item1, File.ReadAllBytes(path));
        Assert.Equal(before.Item2, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task Missing_file_becomes_a_diagnostic_not_an_exception()
    {
        var bundle = await new AnalysisPipeline().AnalyzeAsync(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N") + ".elf"), Options);

        Assert.False(bundle.Analysis.Parsed);
        var d = Assert.Single(bundle.Analysis.Diagnostics, x => x.Code == "FILE.NOT_FOUND");
        Assert.Equal(Severity.Error, d.Severity);
        Assert.False(string.IsNullOrWhiteSpace(d.Explanation));
        Assert.Equal(PortingStatus.NotAnalyzable, bundle.Report.StatusCode);
    }

    [Fact]
    public async Task Directory_empty_path_and_empty_file_are_rejected_clearly()
    {
        using var dir = new TempDir();
        var empty = dir.Write("empty.elf", []);
        var pipeline = new AnalysisPipeline();

        Assert.Contains((await pipeline.AnalyzeAsync(dir.Path, Options)).Analysis.Diagnostics, d => d.Code == "FILE.IS_DIRECTORY");
        Assert.Contains((await pipeline.AnalyzeAsync("", Options)).Analysis.Diagnostics, d => d.Code == "FILE.NO_PATH");
        Assert.Contains((await pipeline.AnalyzeAsync(null!, Options)).Analysis.Diagnostics, d => d.Code == "FILE.NO_PATH");
        Assert.Contains((await pipeline.AnalyzeAsync(empty, Options)).Analysis.Diagnostics, d => d.Code == "FILE.EMPTY");
    }

    [Fact]
    public async Task Files_above_the_size_limit_are_refused_before_reading()
    {
        using var dir = new TempDir();
        var path = dir.Write("big.elf", SampleFiles.All().First().Bytes);

        var bundle = await new AnalysisPipeline().AnalyzeAsync(path, new AnalysisOptions { Limits = AnalysisLimits.Default with { MaxFileSizeBytes = 100 } });

        Assert.Contains(bundle.Analysis.Diagnostics, d => d.Code == "FILE.TOO_LARGE");
        Assert.False(bundle.Analysis.Parsed);
        Assert.Equal(string.Empty, bundle.Analysis.File.Sha256);
    }

    [Fact]
    public async Task Unsupported_formats_are_identified_but_not_parsed()
    {
        using var dir = new TempDir();
        var psx = dir.Write("game.exe", Encoding.ASCII.GetBytes("PS-X EXE").Concat(new byte[2040]).ToArray());
        var text = dir.Write("readme.txt", Encoding.ASCII.GetBytes("plain text, not an executable"));
        var pipeline = new AnalysisPipeline();

        var a = await pipeline.AnalyzeAsync(psx, Options);
        var b = await pipeline.AnalyzeAsync(text, Options);

        Assert.Equal("ps-x-exe", a.Analysis.Format.Id);
        Assert.False(a.Analysis.Parsed);
        Assert.Contains(a.Analysis.Diagnostics, d => d.Code == "FORMAT.UNSUPPORTED" && d.Severity == Severity.Warning);
        Assert.False(string.IsNullOrEmpty(a.Analysis.File.Sha256));
        Assert.Equal("unknown", b.Analysis.Format.Id);
        Assert.Contains(b.Analysis.Diagnostics, d => d.Code == "FORMAT.UNSUPPORTED");
    }

    [Fact]
    public async Task A_cancelled_token_stops_the_analysis()
    {
        using var dir = new TempDir();
        var path = dir.Write("a.elf", SampleFiles.All().First().Bytes);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AnalysisPipeline().AnalyzeAsync(path, Options, null, cts.Token));
    }

    [Fact]
    public async Task Reports_increasing_progress_up_to_completion()
    {
        using var dir = new TempDir();
        var path = dir.Write("a.elf", SampleFiles.All().First().Bytes);
        var progress = new RecordingProgress();

        await new AnalysisPipeline().AnalyzeAsync(path, Options, progress);

        Assert.NotEmpty(progress.Items);
        Assert.Contains(progress.Items, p => p.Stage.Contains("Parsing"));
        Assert.All(progress.Items, p => Assert.InRange(p.Fraction, 0.0, 1.0));
        Assert.Equal(progress.Items.Select(p => p.Fraction).OrderBy(f => f), progress.Items.Select(p => p.Fraction));
    }

    [Fact]
    public async Task A_file_locked_by_another_program_gives_a_readable_error()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Mandatory file locking exists on Windows only.
        }

        using var dir = new TempDir();
        var path = dir.Write("locked.elf", SampleFiles.All().First().Bytes);
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var bundle = await new AnalysisPipeline().AnalyzeAsync(path, Options);

        Assert.Contains(bundle.Analysis.Diagnostics, d => d.Code == "FILE.READ_FAILED" && !string.IsNullOrWhiteSpace(d.Explanation));
    }

    [Fact]
    public async Task Handles_unicode_file_names()
    {
        using var dir = new TempDir();
        var path = dir.Write("Übung_日本語 test.elf", SampleFiles.All().First().Bytes);

        var bundle = await new AnalysisPipeline().AnalyzeAsync(path, Options);

        Assert.True(bundle.Analysis.Parsed);
        Assert.Equal("Übung_日本語 test.elf", bundle.Analysis.File.FileName);
    }
}
