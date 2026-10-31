using System.IO;
using RecompRadar.Infrastructure;
using RecompRadar.Recompilation.Build;
using RecompRadar.Recompilation.Generation;
using RecompRadar.Recompilation.Pipeline;

namespace RecompRadar.App.Services;

/// <summary>Everything the Recompiler page needs from the build system, behind an interface so the page can be tested without building.</summary>
public interface IRecompilerService
{
    DotnetSdkInfo? Sdk { get; }

    string DefaultOutputDirectory(string fileName, string sha256);

    Task<RecompileResult> RecompileAsync(RecompileRequest request, IProgress<PipelineProgress> progress, CancellationToken cancellationToken);

    /// <summary>Starts a program that RecompRadar built (never an analyzed input file) and streams its output.</summary>
    Task<LaunchResult> RunAsync(string executablePath, Action<string> onStandardOutput, Action<string> onStandardError, CancellationToken cancellationToken);
}

public sealed class RecompilerService : IRecompilerService
{
    private readonly AppPaths _paths;

    public RecompilerService(AppPaths paths) => _paths = paths;

    public DotnetSdkInfo? Sdk => DotnetSdk.Locate();

    public string DefaultOutputDirectory(string fileName, string sha256) =>
        Path.Combine(_paths.Root, "Builds", $"{CSharpGenerator.SanitizeName(Path.GetFileNameWithoutExtension(fileName))}-{(sha256.Length >= 8 ? sha256[..8] : "nohash")}");

    public Task<RecompileResult> RecompileAsync(RecompileRequest request, IProgress<PipelineProgress> progress, CancellationToken cancellationToken) =>
        RecompilationPipeline.RunAsync(request, progress, cancellationToken);

    public Task<LaunchResult> RunAsync(string executablePath, Action<string> onStandardOutput, Action<string> onStandardError, CancellationToken cancellationToken) =>
        ProgramLauncher.RunAsync(executablePath, null, TimeSpan.FromMinutes(30), onStandardOutput, onStandardError, cancellationToken);
}
