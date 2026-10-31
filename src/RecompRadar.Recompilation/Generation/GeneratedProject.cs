namespace RecompRadar.Recompilation.Generation;

public sealed record GenerationOptions(
    string ProjectName,
    string SourceName,
    string SourceSha256,
    string GuestArchitecture,
    string GeneratorVersion,

    /// <summary>When false, generation fails if the analysis found unsupported instructions or unresolved errors.</summary>
    bool AllowIncomplete = false,

    /// <summary>The system interface the program runs against (memory map and meaning of <c>syscall</c>).</summary>
    RuntimeProfile Profile = RuntimeProfile.RecompRadarTest);

public sealed record GenerationStats(
    int Functions,
    int Blocks,
    int GuestInstructions,
    int IrInstructions,
    int UnsupportedSites,
    int UnresolvedIndirectJumps,
    int GeneratedSourceBytes);

/// <summary>The files of a generated .NET project (relative path to content) and statistics about the translation.</summary>
public sealed class GeneratedProject
{
    public required string ProjectName { get; init; }

    public required string ProjectFile { get; init; }

    public required IReadOnlyDictionary<string, byte[]> Files { get; init; }

    public required GenerationStats Stats { get; init; }

    /// <summary>Writes all files below <paramref name="directory"/> (created if needed). Existing files with the same name are replaced.</summary>
    public void WriteTo(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var (relative, content) in Files)
        {
            var path = Path.GetFullPath(Path.Combine(directory, relative));
            if (!path.StartsWith(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Refusing to write outside the project directory: {relative}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }
    }
}
