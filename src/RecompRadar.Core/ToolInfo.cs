using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RecompRadar.Core;

public static class ToolInfo
{
    public const string Name = "RecompRadar";

    public static string Version { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        var attribute = typeof(ToolInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        var version = attribute?.InformationalVersion ?? typeof(ToolInfo).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }
}

/// <summary>Shared JSON settings for exports and for the project database.</summary>
public static class RecompRadarJson
{
    public static JsonSerializerOptions Options { get; } = Create(indented: true);
    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented) => new()
    {
        WriteIndented = indented,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        MaxDepth = 64,
    };
}
