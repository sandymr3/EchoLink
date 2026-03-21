using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Svg.Skia;
using Net.Codecrete.QrCodeGenerator;

namespace EchoLink.Converters;

public class QrCodeSvgConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text || string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            // 1. Generate the QR Code logical structure
            var qr = QrCode.EncodeText(text, QrCode.Ecc.Medium);
            
            // 2. Export the QR code as an SVG string (with a white background, black foreground)
            string svgString = qr.ToSvgString(4, "#000000", "#FFFFFF");

            // 3. Parse the SVG string using LoadFromStream
            using var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(svgString));
            var svgSource = SvgSource.LoadFromStream(stream);

            return new SvgImage { Source = svgSource };
        }
        catch (Exception ex)
        {
            Services.LoggingService.Instance.Error($"[QrCodeConverter] Failed to generate QR Code SVG: {ex.Message}");
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
