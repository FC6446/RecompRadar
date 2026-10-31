using System.Diagnostics;
using System.Globalization;
using RecompRadar.Recompilation.Build;
using RecompRadar.TestSupport;
using Xunit.Abstractions;

namespace RecompRadar.Tests;

/// <summary>
/// Measures the built program against the reference interpreter on a compute-bound program. The numbers are printed to the test
/// output (run with <c>--logger "console;verbosity=detailed"</c>); only correctness and "faster than the interpreter" are asserted,
/// because absolute timings depend on the machine.
/// </summary>
[Trait("Category", "Benchmark")]
public class BenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public BenchmarkTests(ITestOutputHelper output) => _output = output;

    [RequiresDotnetSdkFact]
    public async Task The_built_program_is_correct_and_faster_than_the_reference_interpreter()
    {
        var elf = Ps2StyleBenchmark.BuildElf();
        var built = await NativeTestHelper.BuildAsync(elf, "benchmark");
        Assert.True(built.Build.Success, built.Build.FailureReason);

        var clock = Stopwatch.StartNew();
        var reference = NativeTestHelper.Reference(elf, 2_000_000_000);
        clock.Stop();
        var referenceMs = clock.Elapsed.TotalMilliseconds;

        var run = await ProgramLauncher.RunAsync(built.Build.ExecutablePath!, timeout: TimeSpan.FromMinutes(2));

        Assert.Equal(Ps2StyleBenchmark.ExpectedOutput(), reference.Output);
        Assert.Equal(Ps2StyleBenchmark.ExpectedOutput(), run.StandardOutput.ReplaceLineEndings("\n"));
        Assert.True(DifferentialValidator.Compare(reference, run.Report!).Passed);

        var nativeMs = run.Report!.ElapsedMilliseconds;
        var instructions = reference.InstructionsExecuted;
        static string Mips(long count, double ms) => (count / ms / 1000.0).ToString("F1", CultureInfo.InvariantCulture);

        _output.WriteLine($"guest instructions executed: {instructions:N0}");
        _output.WriteLine($"reference interpreter : {referenceMs,9:F1} ms  ({Mips(instructions, referenceMs)} M guest instructions/s)");
        _output.WriteLine($"built program (JIT)   : {nativeMs,9:F1} ms  ({Mips(instructions, nativeMs)} M guest instructions/s, measured inside the program, start-up excluded)");
        _output.WriteLine($"process wall clock    : {run.Duration.TotalMilliseconds,9:F1} ms  (includes .NET start-up and JIT)");
        _output.WriteLine($"speed-up              : {referenceMs / nativeMs:F1}x");

        Assert.True(nativeMs < referenceMs, "the built program should be faster than the reference interpreter");
    }
}
