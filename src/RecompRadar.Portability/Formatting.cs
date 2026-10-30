using System.Globalization;

namespace RecompRadar.Portability;

internal static class Fmt
{
    public static string Hex(ulong value) => string.Create(CultureInfo.InvariantCulture, $"0x{value:X}");

    public static string Hex8(ulong value) => string.Create(CultureInfo.InvariantCulture, $"0x{value:X8}");

    public static string Size(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.##} {units[unit]}");
    }

    public static string Size(ulong bytes) => Size(bytes > long.MaxValue ? long.MaxValue : (long)bytes);

    public static ulong SaturatingSum(IEnumerable<ulong> values)
    {
        ulong total = 0;
        foreach (var v in values)
        {
            total = total > ulong.MaxValue - v ? ulong.MaxValue : total + v;
        }

        return total;
    }

    public static string Join(IEnumerable<string> items, int max = 6, string separator = ", ")
    {
        var list = items.ToList();
        var shown = string.Join(separator, list.Take(max));
        return list.Count > max ? $"{shown}{separator}… (+{list.Count - max} more)" : shown;
    }
}
