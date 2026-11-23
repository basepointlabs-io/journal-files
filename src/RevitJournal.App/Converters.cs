using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using RevitJournal.Core.Rules;

namespace RevitJournal.App;

internal static class Palette
{
    public static readonly Brush Red = Freeze("#B3261E");
    public static readonly Brush Orange = Freeze("#B4530A");
    public static readonly Brush Amber = Freeze("#8A6100");
    public static readonly Brush Blue = Freeze("#1F55C9");
    public static readonly Brush Green = Freeze("#1F7A45");
    public static readonly Brush Gray = Freeze("#7F8FA8");

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

public sealed class SeverityBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Severity.Critical => Palette.Red,
        Severity.High => Palette.Orange,
        Severity.Medium => Palette.Amber,
        Severity.Low => Palette.Blue,
        _ => Palette.Gray,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Fill of a meter: the accent blue while fine, orange or red when the value is a problem.</summary>
public sealed class MeterBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value as string) switch
    {
        "crash" => Palette.Red,
        "warning" => Palette.Orange,
        _ => Palette.Blue,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Colour of a figure: ordinary text while fine, orange when it needs watching, red when it is a problem.</summary>
public sealed class ValueInkConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value as string) switch
    {
        "crash" => Palette.Red,
        "warning" => Palette.Orange,
        _ => System.Windows.Application.Current?.TryFindResource("Ink") as Brush ?? Brushes.Black,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value as string) switch
    {
        "crash" or "error" => Palette.Red,
        "warning" or "abnormal" => Palette.Orange,
        "exit" => Palette.Amber,
        "running" => Palette.Blue,
        "ok" => Palette.Green,
        _ => Palette.Gray,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
