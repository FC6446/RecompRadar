using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using RecompRadar.Core.Model;
using RecompRadar.Recompilation;
using RecompRadar.Recompilation.Analysis;

namespace RecompRadar.App.Services;

/// <summary>The code structure recovered from the analyzed file, together with everything needed to show it.</summary>
public sealed record CodeAnalysisResult(
    string SourcePath,
    ElfFile Elf,
    LoadedImage Image,
    IRecompilerPlugin Plugin,
    IArchitectureDecoder? Decoder,
    ProgramGraph Graph);

/// <summary>
/// Runs the control-flow analysis (blocks, functions, call graph) for the file that is currently analyzed.
/// It re-reads the file from disk, never starts it, and reports clearly why nothing can be shown when that is not possible.
/// </summary>
public sealed partial class CodeAnalysisService : ObservableObject
{
    private readonly AnalysisSession _session;
    private readonly ILogger<CodeAnalysisService> _logger;
    private readonly RecompilerPluginRegistry _plugins;
    private CancellationTokenSource? _cts;
    private int _generation;

    public CodeAnalysisService(AnalysisSession session, ILogger<CodeAnalysisService> logger, RecompilerPluginRegistry? plugins = null)
    {
        _session = session;
        _logger = logger;
        _plugins = plugins ?? RecompilerPluginRegistry.Default;
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AnalysisSession.Current))
            {
                _ = RunAsync();
            }
        };
        Status = "No file has been analyzed yet.";
    }

    [ObservableProperty]
    private CodeAnalysisResult? _result;

    [ObservableProperty]
    private bool _isRunning;

    /// <summary>Why there is no result, or a short summary when there is one.</summary>
    [ObservableProperty]
    private string _status = string.Empty;

    public async Task RunAsync()
    {
        CancelRunning();
        var generation = ++_generation;
        Result = null;

        if (_session.Current?.Analysis is not { } analysis)
        {
            Status = "No file has been analyzed yet.";
            return;
        }

        if (analysis.Elf is null)
        {
            Status = "The file was not parsed as an ELF file, so there is no code to analyze.";
            return;
        }

        var elf = analysis.Elf;
        var plugin = _plugins.FindSupporting(elf).FirstOrDefault(p => p.CreateLoader() is not null && p.CreateTranslator() is not null);
        if (plugin is null)
        {
            Status = $"No decoder and translator exists for {analysis.Architecture?.DisplayName ?? "this architecture"}. Nothing is guessed; the code is not shown.";
            return;
        }

        var path = analysis.File.FullPath;
        if (!File.Exists(path))
        {
            Status = "The original file is no longer at its recorded location, and the code analysis needs it. Analyze the file again to inspect its code.";
            return;
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsRunning = true;
        Status = "Analyzing code…";
        try
        {
            var computed = await Task.Run(
                () =>
                {
                    using var stream = File.OpenRead(path);
                    var image = plugin.CreateLoader()!.Load(stream, elf);
                    var translator = plugin.CreateTranslator()!;
                    var graph = ProgramAnalyzer.Analyze(AnalysisInputFactory.FromImage(image, elf, translator, plugin.NoReturnSystemCalls), null, cts.Token);
                    return new CodeAnalysisResult(path, elf, image, plugin, plugin.CreateDecoder(), graph);
                },
                cts.Token);

            if (generation == _generation)
            {
                Result = computed;
                Status = $"{computed.Graph.Functions.Count} function(s), {computed.Graph.Blocks.Count} block(s), {computed.Graph.InstructionCount} instruction(s) analyzed with {plugin.DisplayName}.";
            }
        }
        catch (OperationCanceledException)
        {
            // A newer analysis replaced this one.
        }
        catch (Exception ex) when (ex is BinaryLoadException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Code analysis of {File} failed", Path.GetFileName(path));
            if (generation == _generation)
            {
                Status = $"The code could not be analyzed: {ex.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
            }

            if (generation == _generation)
            {
                IsRunning = false;
            }
        }
    }

    private void CancelRunning()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The previous analysis has already finished and released its token.
        }
    }
}
