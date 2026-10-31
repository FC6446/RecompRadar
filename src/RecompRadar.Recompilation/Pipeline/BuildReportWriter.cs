using RecompRadar.Core;
using RecompRadar.Recompilation.Build;

namespace RecompRadar.Recompilation.Pipeline;

/// <summary>Builds the content of build-report.json: what was built, from what, how it was checked and what it cannot do.</summary>
internal static class BuildReportWriter
{
    public static object Create(RecompileRequest request, RecompileResult result)
    {
        var graph = result.Graph;
        var compat = result.Compatibility;
        return new
        {
            schemaVersion = 1,
            tool = new { name = ToolInfo.Name, version = ToolInfo.Version },
            createdUtc = DateTimeOffset.UtcNow,
            source = new { fileName = Path.GetFileName(request.SourcePath), sha256 = result.SourceSha256 },
            outcome = new
            {
                success = result.Success,
                category = result.Category,
                categoryTitle = result.Category.Title(),
                categoryExplanation = result.Category.Explanation(),
                incomplete = result.Incomplete,
                failureReason = result.FailureReason,
                playBlockedReason = result.PlayBlockedReason,
            },
            compatibility = compat is null ? null : new
            {
                level = compat.Level,
                headline = compat.Headline,
                platform = compat.PlatformId,
                runtimeProfile = compat.Profile,
                platformSource = compat.PlatformSource,
                backend = compat.Backend?.DisplayName,
                notes = compat.Notes.Select(n => new { kind = n.Kind, text = n.Text }),
            },
            analysis = graph is null ? null : new
            {
                entryPoint = $"0x{graph.EntryPoint:X8}",
                functions = graph.Functions.Count,
                blocks = graph.Blocks.Count,
                instructions = graph.InstructionCount,
                reachableBytes = graph.ReachableBytes,
                executableBytes = graph.ExecutableBytes,
                unsupportedInstructions = graph.UnsupportedInstructions.Select(u => new { address = $"0x{u.Address:X8}", u.Mnemonic, u.Reason }),
                indirectJumps = graph.IndirectJumps.Select(j => new { address = $"0x{j.Address:X8}", j.IsCall }),
                findings = graph.Findings.Take(100).Select(f => new { f.Severity, address = $"0x{f.Address:X8}", f.Code, f.Message }),
                truncated = graph.Truncated,
            },
            generation = result.Generation,
            build = result.Build is null ? null : new
            {
                sdkVersion = result.Build.SdkVersion,
                durationMs = (long)result.Build.Duration.TotalMilliseconds,
                executable = result.ExecutablePath is null ? null : Path.GetFileName(result.ExecutablePath),
                executableSha256 = result.Build.ExecutableSha256,
                assemblySha256 = result.Build.AssemblySha256,
                errors = result.Build.ErrorCount,
                warnings = result.Build.WarningCount,
            },
            validation = result.Validation is null
                ? new { performed = false, passed = (bool?)null, skippedReason = result.ValidationSkippedReason, differences = Array.Empty<string>(), referenceStatus = (string?)null, instructionsExecuted = (long?)null }
                : new { performed = true, passed = (bool?)result.Validation.Passed, skippedReason = (string?)null, differences = result.Validation.Differences.ToArray(), referenceStatus = (string?)result.Validation.Reference.Status.ToString(), instructionsExecuted = (long?)result.Validation.Reference.InstructionsExecuted },
            stages = result.Stages.Select(s => new { s.Stage, s.Succeeded, durationMs = (long)s.Duration.TotalMilliseconds, s.Summary }),
            limitations = new[]
            {
                "The program is translated CPU code compiled to .NET IL and JIT-compiled at start-up. It is not ahead-of-time machine code and needs the .NET 10 runtime.",
                "The runtime is small: RecompRadar test calls (exit, simple output) and, for the PlayStation 2 profile, the EE memory map, serial output and a few kernel calls. No graphics, sound, input, file, timer or interrupt services are provided.",
                "Control flow that static analysis could not recover (register jumps, jump tables) faults at run time with a message.",
                "Writes into executable memory (self-modifying code) are not supported and fault at run time.",
                "Validation compares one run of the built program with the reference interpreter. It does not prove equivalence for other inputs or paths.",
            },
        };
    }
}
