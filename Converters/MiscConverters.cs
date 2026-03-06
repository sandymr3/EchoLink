using System.Globalization;
using Avalonia.Data.Converters;

namespace EchoLink.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    public static readonly BoolToVisibilityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b;
}

public class InvertedBoolConverter : IValueConverter
{
    public static readonly InvertedBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;
}

public class LogLevelToColorConverter : IValueConverter
{
    public static readonly LogLevelToColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() ?? "#00E5FF";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
