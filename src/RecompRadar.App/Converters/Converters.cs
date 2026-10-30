using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using RecompRadar.App.Services;

namespace RecompRadar.App.Converters;

internal static class BrushLookup
{
    public static Brush Get(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}

public sealed class InvertedBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>Visible when the string has text, collapsed when null or empty.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class EvidenceToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value as string) switch
    {
        "Verified" => BrushLookup.Get("SuccessBrush"),
        "Inferred" => BrushLookup.Get("WarningBrush"),
        _ => BrushLookup.Get("TextFaintBrush"),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value as string) switch
    {
        "Error" => BrushLookup.Get("DangerBrush"),
        "Warning" => BrushLookup.Get("WarningBrush"),
        _ => BrushLookup.Get("TextMutedBrush"),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Maps the report status ("ok", "warn", "bad") to the accent or soft background colour.</summary>
public sealed class StatusKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var soft = parameter as string == "soft";
        return (value as string) switch
        {
            "ok" => BrushLookup.Get(soft ? "SuccessSoftBrush" : "SuccessBrush"),
            "bad" => BrushLookup.Get(soft ? "DangerSoftBrush" : "DangerBrush"),
            _ => BrushLookup.Get(soft ? "WarningSoftBrush" : "WarningBrush"),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NotificationKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var soft = parameter as string == "soft";
        return value is NotificationKind kind
            ? kind switch
            {
                NotificationKind.Success => BrushLookup.Get(soft ? "SuccessSoftBrush" : "SuccessBrush"),
                NotificationKind.Warning => BrushLookup.Get(soft ? "WarningSoftBrush" : "WarningBrush"),
                NotificationKind.Error => BrushLookup.Get(soft ? "DangerSoftBrush" : "DangerBrush"),
                _ => BrushLookup.Get(soft ? "AccentSoftBrush" : "AccentBrush"),
            }
            : BrushLookup.Get("AccentBrush");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NotificationKindToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is NotificationKind kind
            ? kind switch
            {
                NotificationKind.Success => "IconCheck",
                NotificationKind.Warning => "IconWarning",
                NotificationKind.Error => "IconError",
                _ => "IconInfo",
            }
            : "IconInfo";
        return Application.Current?.TryFindResource(key) as Geometry ?? Geometry.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class IconKeyToGeometryConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string key && Application.Current?.TryFindResource(key) is Geometry geometry ? geometry : Geometry.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class PercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d ? $"{Math.Round(d * 100):0}%" : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
