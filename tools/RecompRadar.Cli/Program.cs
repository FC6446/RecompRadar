using System.Globalization;
using RecompRadar.Core;
using RecompRadar.Core.Model;
using RecompRadar.Portability;
using RecompRadar.Recompilation.Build;
using RecompRadar.Recompilation.Pipeline;

// Command-line front end of the recompilation pipeline (the same code the Recompiler page uses).
//
//   recompradar-cli recompile <file.elf> [--out <folder>] [--platform ps2|ps3|ps4|generic-mips32]
//                             [--allow-incomplete] [--no-validate] [--run]
//
// The input file is only read. Only the program that RecompRadar builds is ever started, and only with --run.
// Exit codes: 0 success, 1 the build was refused or failed, 2 usage error.

const int Usage = 2;

if (args.Length < 2 || args[0] != "recompile")
{
    return PrintUsage();
}

var input = Path.GetFullPath(args[1]);
string? output = null;
string? platform = null;
var allowIncomplete = false;
var validate = true;
var run = false;
for (var i = 2; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--out" when i + 1 < args.Length:
            output = Path.GetFullPath(args[++i]);
            break;
        case "--platform" when i + 1 < args.Length:
            platform = args[++i];
            break;
        case "--allow-incomplete":
            allowIncomplete = true;
            break;
        case "--no-validate":
            validate = false;
            break;
        case "--run":
            run = true;
            break;
        default:
            return PrintUsage();
    }
}

if (!File.Exists(input))
{
    Console.Error.WriteLine($"The file '{input}' does not exist.");
    return Usage;
}

// The same analysis the app runs: it parses the ELF file and produces the platform hints (never proof) used by the compatibility check.
var bundle = await new AnalysisPipeline().AnalyzeAsync(input, new AnalysisOptions());
var analysis = bundle.Analysis;
if (analysis.Elf is null)
{
    Console.Error.WriteLine("The file could not be analyzed as an ELF file:");
    foreach (var d in analysis.Diagnostics.Where(d => d.Severity >= Severity.Warning))
    {
        Console.Error.WriteLine($"  {d.Code}: {d.Message}");
    }

    return 1;
}

output ??= Path.Combine(Environment.CurrentDirectory, "recompradar-build", Path.GetFileNameWithoutExtension(input));
Console.WriteLine($"RecompRadar {ToolInfo.Version}: recompiling {Path.GetFileName(input)} into {output}");
var progress = new Progress<PipelineProgress>(p =>
{
    if (p.Message.StartsWith("done: ", StringComparison.Ordinal) || p.Message.StartsWith("failed: ", StringComparison.Ordinal))
    {
        Console.WriteLine($"  [{p.Stage}] {p.Message}");
    }
});

var result = await RecompilationPipeline.RunAsync(
    new RecompileRequest(input, analysis.Elf, output, analysis.Platform, platform, allowIncomplete, validate),
    progress);
await Task.Delay(50); // lets the queued progress lines print before the summary

Console.WriteLine();
if (result.Compatibility is { } compatibility)
{
    Console.WriteLine($"Target system: {compatibility.PlatformId ?? "none assumed"} ({compatibility.PlatformSource}); runtime profile: {compatibility.Profile}");
    Console.WriteLine($"Compatibility: {compatibility.Headline}");
    foreach (var note in compatibility.Notes.Where(n => n.Kind != RecompRadar.Recompilation.Compatibility.NoteKind.Info))
    {
        Console.WriteLine($"  {note.Kind}: {note.Text}");
    }
}

if (!result.Success)
{
    Console.WriteLine($"Result: FAILED - {result.FailureReason}");
    return 1;
}

Console.WriteLine($"Result: built {result.Category.Title()}{(result.Incomplete ? " (incomplete)" : string.Empty)}");
Console.WriteLine($"  program : {result.ExecutablePath}");
Console.WriteLine($"  sha256  : {result.Build!.ExecutableSha256}");
Console.WriteLine($"  report  : {result.ReportPath}");
Console.WriteLine(result.Validation is { } v
    ? $"  checked : identical to the reference interpreter for this run ({v.Reference.InstructionsExecuted.ToString("N0", CultureInfo.InvariantCulture)} guest instructions)"
    : $"  checked : not validated ({result.ValidationSkippedReason})");

if (run)
{
    if (result.PlayBlockedReason is { } blocked)
    {
        Console.WriteLine($"Not started: {blocked}");
        return 1;
    }

    Console.WriteLine("--- program output ---");
    var launch = await ProgramLauncher.RunAsync(result.ExecutablePath!, null, TimeSpan.FromMinutes(5), Console.WriteLine, line => Console.Error.WriteLine("[stderr] " + line));
    Console.WriteLine($"--- exit code {launch.ExitCode}; {launch.Report?.InstructionsExecuted:N0} guest instructions in {launch.Report?.ElapsedMilliseconds:0.#} ms ---");
}

return 0;

static int PrintUsage()
{
    Console.Error.WriteLine("Usage: recompradar-cli recompile <file.elf> [--out <folder>] [--platform ps2|ps3|ps4|generic-mips32] [--allow-incomplete] [--no-validate] [--run]");
    return Usage;
}
