using RecompRadar.BinaryAnalysis.Elf;
using RecompRadar.Core;
using RecompRadar.Core.Model;
using RecompRadar.Portability;

namespace RecompRadar.Tests;

public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "recompradar-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string Write(string name, byte[] bytes)
    {
        var path = File(name);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Temp folder cleanup is best effort.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class T
{
    public static ElfParseResult Parse(byte[] bytes, AnalysisLimits? limits = null)
    {
        using var stream = new MemoryStream(bytes);
        return new ElfParser(limits).Parse(stream);
    }

    public static ElfFile ParseOk(byte[] bytes, AnalysisLimits? limits = null)
    {
        var result = Parse(bytes, limits);
        Assert.True(result.Success, "Parse failed: " + string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
        return result.Elf!;
    }

    public static bool Has(IEnumerable<Diagnostic> diagnostics, string code) =>
        diagnostics.Any(d => d.Code == code);

    public static async Task<AnalysisBundle> AnalyzeAsync(byte[] bytes, string fileName = "test.elf", AnalysisOptions? options = null)
    {
        using var dir = new TempDir();
        var path = dir.Write(fileName, bytes);
        return await new AnalysisPipeline().AnalyzeAsync(path, options ?? new AnalysisOptions());
    }

    public static AnalysisBundle Analyze(byte[] bytes, string fileName = "test.elf", AnalysisOptions? options = null) =>
        AnalyzeAsync(bytes, fileName, options).GetAwaiter().GetResult();

    /// <summary>Finds the repository root (the folder that contains RecompRadar.sln).</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "RecompRadar.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
