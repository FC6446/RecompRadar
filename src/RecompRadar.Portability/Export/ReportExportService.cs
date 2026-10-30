using System.Text;
using RecompRadar.Core;
using RecompRadar.Core.Model;

namespace RecompRadar.Portability.Export;

/// <summary>An export failed for a reason the user can act on (for example a missing folder or a write-protected target).</summary>
public sealed class ExportException : Exception
{
    public ExportException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

public sealed class ReportExportService
{
    private readonly IReadOnlyList<IReportExporter> _exporters;

    public ReportExportService(IEnumerable<IReportExporter>? exporters = null)
    {
        _exporters = exporters?.ToList() ?? [new JsonReportExporter(), new MarkdownReportExporter(), new HtmlReportExporter()];
    }

    public IReadOnlyList<IReportExporter> Exporters => _exporters;

    public IReportExporter Get(string formatId) =>
        _exporters.FirstOrDefault(e => string.Equals(e.FormatId, formatId, StringComparison.OrdinalIgnoreCase))
        ?? throw new ExportException($"The export format '{formatId}' is not available.");

    /// <summary>Suggests a file name such as "firmware.elf.report.html".</summary>
    public string SuggestFileName(AnalysisBundle bundle, string formatId)
    {
        var exporter = Get(formatId);
        var stem = SanitizeFileName(bundle.Analysis.File.FileName);
        if (stem.Length == 0)
        {
            stem = "analysis";
        }

        return $"{stem}.report{exporter.FileExtension}";
    }

    /// <summary>Renders the report and writes it atomically (temporary file, then move). Returns the full path written.</summary>
    public async Task<string> ExportAsync(AnalysisBundle bundle, string formatId, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var exporter = Get(formatId);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ExportException("No target file was chosen for the export.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ExportException($"The export path is not valid: {ex.Message}", ex);
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            throw new ExportException($"The folder '{directory}' does not exist. Choose an existing folder for the export.");
        }

        var content = await Task.Run(() => exporter.Render(bundle), cancellationToken).ConfigureAwait(false);
        var temp = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temp, fullPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            throw new ExportException($"The report could not be written to '{fullPath}': {ex.Message}", ex);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        return fullPath;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort cleanup of the temporary file.
        }
    }
}
