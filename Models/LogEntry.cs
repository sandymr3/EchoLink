using Avalonia.Media;

namespace EchoLink.Models;

public enum LogLevel { Info, Warning, Error, Debug }

public record LogEntry(
    LogLevel Level,
    string Message,
    DateTime Timestamp)
{
    public string FormattedMessage =>
        $"[{Timestamp:HH:mm:ss}] [{Level.ToString().ToUpperInvariant()}] {Message}";

    public IBrush LevelBrush => Level switch
    {
        LogLevel.Error   => new SolidColorBrush(Color.Parse("#FF5252")),
        LogLevel.Warning => new SolidColorBrush(Color.Parse("#FFD740")),
        LogLevel.Debug   => new SolidColorBrush(Color.Parse("#B3B3B3")),
        _                => new SolidColorBrush(Color.Parse("#00E5FF"))
    };
}
