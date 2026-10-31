using System.Security.Cryptography;
using RecompRadar.Recompilation;
using RecompRadar.Recompilation.Analysis;
using RecompRadar.Recompilation.Build;
using RecompRadar.Recompilation.Execution;
using RecompRadar.Recompilation.Generation;
using RecompRadar.Recompilation.Mips;
using RecompRadar.TestSupport;

namespace RecompRadar.Tests;

/// <summary>Marks tests that build a real Windows program with the .NET SDK; they are skipped (and reported as skipped) without an SDK.</summary>
public sealed class RequiresDotnetSdkFactAttribute : FactAttribute
{
    public RequiresDotnetSdkFactAttribute()
    {
        if (DotnetSdk.Locate() is null)
        {
            Skip = "No .NET SDK 10 is installed, so no program can be built.";
        }
    }
}

/// <summary>Theory variant of <see cref="RequiresDotnetSdkFactAttribute"/>.</summary>
public sealed class RequiresDotnetSdkTheoryAttribute : TheoryAttribute
{
    public RequiresDotnetSdkTheoryAttribute()
    {
        if (DotnetSdk.Locate() is null)
        {
            Skip = "No .NET SDK 10 is installed, so no program can be built.";
        }
    }
}

/// <summary>Reports synchronously on the calling thread (unlike <see cref="Progress{T}"/>, which posts to the thread pool and may reorder).</summary>
internal sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;

    public InlineProgress(Action<T> handler) => _handler = handler;

    public void Report(T value) => _handler(value);
}

internal sealed class BuiltProgram
{
    public required ProgramGraph Graph { get; init; }

    public required GeneratedProject Project { get; init; }

    public required BuildResult Build { get; init; }

    public required string Directory { get; init; }

    public required byte[] Elf { get; init; }
}

internal static class NativeTestHelper
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "recompradar-tests", Guid.NewGuid().ToString("N")[..8]);

    static NativeTestHelper()
    {
        // Test classes run in parallel and share this folder, so it is removed once, when the test process ends.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
    }

    public static LoadedImage Load(byte[] elfBytes, out Core.Model.ElfFile elf)
    {
        elf = T.ParseOk(elfBytes);
        using var stream = new MemoryStream(elfBytes);
        return new MipsPocPlugin().CreateLoader()!.Load(stream, elf);
    }

    public static GeneratedProject Generate(byte[] elfBytes, string name, bool allowIncomplete = false, ProgramGraph? graph = null, RuntimeProfile profile = RuntimeProfile.RecompRadarTest)
    {
        var image = Load(elfBytes, out var elf);
        var plugin = new MipsPocPlugin();
        graph ??= ProgramAnalyzer.Analyze(AnalysisInputFactory.FromImage(image, elf, plugin.CreateTranslator()!, plugin.NoReturnSystemCalls));
        return CSharpGenerator.Generate(graph, Load(elfBytes, out _), new GenerationOptions(
            name, name + ".elf", Convert.ToHexString(SHA256.HashData(elfBytes)).ToLowerInvariant(), "MIPS32", "test", allowIncomplete, profile));
    }

    public static async Task<BuiltProgram> BuildAsync(byte[] elfBytes, string name, bool allowIncomplete = false, string? subfolder = null, RuntimeProfile profile = RuntimeProfile.RecompRadarTest)
    {
        var image = Load(elfBytes, out var elf);
        var plugin = new MipsPocPlugin();
        var graph = ProgramAnalyzer.Analyze(AnalysisInputFactory.FromImage(image, elf, plugin.CreateTranslator()!, plugin.NoReturnSystemCalls));
        var project = Generate(elfBytes, name, allowIncomplete, graph, profile);
        var directory = subfolder is null ? Path.Combine(Root, name) : Path.Combine(Root, subfolder, name);
        var build = await ProjectBuilder.BuildAsync(project, directory);
        return new BuiltProgram { Graph = graph, Project = project, Build = build, Directory = directory, Elf = elfBytes };
    }

    public static ExecutionResult Reference(byte[] elfBytes, long maxInstructions = 50_000_000, RuntimeProfile profile = RuntimeProfile.RecompRadarTest) => RefRun.RunElf(elfBytes, maxInstructions, profile);

    public static void Cleanup()
    {
        // Set RECOMPRADAR_KEEP_TEST_OUTPUT=1 to inspect the generated projects and programs after a run.
        if (Environment.GetEnvironmentVariable("RECOMPRADAR_KEEP_TEST_OUTPUT") == "1")
        {
            return;
        }

        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp files of a test run; the OS cleans them up eventually.
        }
    }
}

public sealed class SelfTestBuildFixture : IAsyncLifetime
{
    internal BuiltProgram? Program { get; private set; }

    public async Task InitializeAsync()
    {
        if (DotnetSdk.Locate() is not null)
        {
            Program = await NativeTestHelper.BuildAsync(Ps2StyleSelfTest.BuildElf(), "ps2_selftest");
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

/// <summary>
/// Milestone A, end to end: a self-made PS2-style MIPS program is analyzed, translated, built into a real Windows program,
/// started, and its output is compared with an independent C# oracle and with the reference interpreter.
/// </summary>
public class PS2SelfTestEndToEndTests : IClassFixture<SelfTestBuildFixture>
{
    private readonly SelfTestBuildFixture _fixture;

    public PS2SelfTestEndToEndTests(SelfTestBuildFixture fixture) => _fixture = fixture;

    private BuiltProgram Program => _fixture.Program ?? throw new InvalidOperationException("The program was not built.");

    [RequiresDotnetSdkFact]
    public void The_generated_project_builds_into_a_windows_program()
    {
        var build = Program.Build;
        Assert.True(build.Success, build.FailureReason + Environment.NewLine + string.Join(Environment.NewLine, build.Log.TakeLast(30)));
        Assert.Equal(0, build.ErrorCount);
        Assert.EndsWith(".exe", build.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(build.ExecutablePath));
        Assert.Equal(64, build.ExecutableSha256!.Length);
    }

    [RequiresDotnetSdkFact]
    public async Task The_built_program_prints_exactly_what_the_independent_oracle_expects()
    {
        var run = await ProgramLauncher.RunAsync(Program.Build.ExecutablePath!);

        Assert.False(run.TimedOut);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(Ps2StyleSelfTest.ExpectedOutput(), run.StandardOutput.ReplaceLineEndings("\n"));
        Assert.Equal(string.Empty, run.StandardError);
        Assert.NotNull(run.Report);
        Assert.Equal("Exited", run.Report!.Status);
    }

    [RequiresDotnetSdkFact]
    public async Task The_built_program_behaves_like_the_reference_interpreter()
    {
        var run = await ProgramLauncher.RunAsync(Program.Build.ExecutablePath!);
        var reference = NativeTestHelper.Reference(Program.Elf);

        var validation = DifferentialValidator.Compare(reference, run.Report!);

        Assert.True(validation.Passed, string.Join(Environment.NewLine, validation.Differences));
        Assert.True(reference.InstructionsExecuted > 100_000, "the self-test should be a non-trivial program");
    }

    [RequiresDotnetSdkFact]
    public async Task The_instruction_limit_stops_the_built_program_like_the_reference()
    {
        const long limit = 5_000;
        var run = await ProgramLauncher.RunAsync(Program.Build.ExecutablePath!, maxInstructions: limit);
        var reference = NativeTestHelper.Reference(Program.Elf, limit);

        Assert.Equal("StepLimitReached", run.Report!.Status);
        Assert.True(DifferentialValidator.Compare(reference, run.Report).Passed);
    }
}
