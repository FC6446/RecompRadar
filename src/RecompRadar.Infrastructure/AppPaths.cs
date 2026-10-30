namespace RecompRadar.Infrastructure;

/// <summary>Locations of RecompRadar's local data. Everything lives in one folder and can be redirected with RECOMPRADAR_DATA_DIR.</summary>
public sealed class AppPaths
{
    public const string DataDirectoryVariable = "RECOMPRADAR_DATA_DIR";

    public AppPaths(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public string Database => Path.Combine(Root, "recompradar.db");

    public string Settings => Path.Combine(Root, "settings.json");

    public string Logs => Path.Combine(Root, "logs");

    public string Knowledge => Path.Combine(Root, "knowledge");

    public static AppPaths Default()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        var root = string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecompRadar")
            : Path.GetFullPath(overridden);
        return new AppPaths(root);
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Knowledge);
    }
}
