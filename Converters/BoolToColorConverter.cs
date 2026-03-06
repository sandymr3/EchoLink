using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace EchoLink.Converters;

/// <summary>Converts a bool to one of two SolidColorBrushes.</summary>
public class BoolToColorConverter : IValueConverter
{
    public static readonly BoolToColorConverter MeshStatus = new()
    {
        TrueColor  = Color.Parse("#00E676"),
        FalseColor = Color.Parse("#FF5252")
    };

    public Color TrueColor  { get; set; } = Colors.Green;
    public Color FalseColor { get; set; } = Colors.Red;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool boolValue = value is bool b && b;
        var color = boolValue ? TrueColor : FalseColor;
        return new SolidColorBrush(color);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
