using RecompRadar.Recompilation.IR;

namespace RecompRadar.Recompilation.Build;

public sealed record ValidationResult(bool Passed, IReadOnlyList<string> Differences, ExecutionResult Reference, RunReport Native);

/// <summary>
/// Compares a run of the built program with a run of the reference interpreter on the same input.
/// Passing means: same end state, same instruction count, same output and same architectural registers for this run.
/// It says nothing about other inputs or code paths that were not taken.
/// </summary>
public static class DifferentialValidator
{
    public static ValidationResult Compare(ExecutionResult reference, RunReport native)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(native);

        var differences = new List<string>();
        var referenceStatus = reference.Status.ToString();
        if (referenceStatus != native.Status)
        {
            differences.Add($"End state: reference {referenceStatus}, built program {native.Status}.");
        }

        if (reference.Status == ExecutionStatus.Exited && native.Status == "Exited" && reference.ExitCode != native.ExitCode)
        {
            differences.Add($"Exit code: reference {reference.ExitCode}, built program {native.ExitCode}.");
        }

        if (reference.InstructionsExecuted != native.InstructionsExecuted)
        {
            differences.Add($"Guest instructions executed: reference {reference.InstructionsExecuted:N0}, built program {native.InstructionsExecuted:N0}.");
        }

        if (!string.Equals(reference.Output, native.Output, StringComparison.Ordinal))
        {
            differences.Add($"Program output differs (reference {reference.Output.Length} characters, built program {native.Output.Length} characters){FirstDifference(reference.Output, native.Output)}.");
        }

        if (reference.Status != ExecutionStatus.Faulted && referenceStatus == native.Status)
        {
            // Temporaries and the scratch register are block-local and not part of the architectural state.
            for (var i = 0; i < Regs.TempBase; i++)
            {
                var expected = i < reference.Registers.Count ? reference.Registers[i] : 0;
                var actual = i < native.Registers.Count ? native.Registers[i] : 0;
                if (expected != actual)
                {
                    differences.Add($"Register {Regs.Name(i)}: reference 0x{expected:X8}, built program 0x{actual:X8}.");
                }
            }
        }

        if (reference.Status is ExecutionStatus.Faulted or ExecutionStatus.Stopped && referenceStatus == native.Status &&
            !string.Equals(reference.Message, native.Message, StringComparison.Ordinal))
        {
            differences.Add($"Message: reference \"{reference.Message}\", built program \"{native.Message}\".");
        }

        return new ValidationResult(differences.Count == 0, differences, reference, native);
    }

    private static string FirstDifference(string a, string b)
    {
        var length = Math.Min(a.Length, b.Length);
        for (var i = 0; i < length; i++)
        {
            if (a[i] != b[i])
            {
                return $"; first difference at character {i}";
            }
        }

        return $"; one output is a prefix of the other (shorter has {length} characters)";
    }
}
