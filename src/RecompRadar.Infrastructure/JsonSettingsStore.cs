using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RecompRadar.Core;

namespace RecompRadar.Infrastructure;

public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly ILogger _logger;

    public JsonSettingsStore(string path, ILogger<JsonSettingsStore>? logger = null)
    {
        _path = path;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }

            var info = new FileInfo(_path);
            if (info.Length > 1024 * 1024)
            {
                _logger.LogWarning("Settings file {Path} is unexpectedly large ({Length} bytes); using defaults", _path, info.Length);
                return new AppSettings();
            }

            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize<AppSettings>(stream, RecompRadarJson.Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Settings could not be read from {Path}; using defaults", _path);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, RecompRadarJson.Options));
        File.Move(temp, _path, overwrite: true);
    }
}
