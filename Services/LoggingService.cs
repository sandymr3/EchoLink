using System.Collections.ObjectModel;
using EchoLink.Models;

namespace EchoLink.Services;

public sealed class LoggingService
{
    private static readonly LoggingService _instance = new();
    public static LoggingService Instance => _instance;

    public ObservableCollection<LogEntry> Entries { get; } = [];

    private LoggingService() { }

    public void Log(string message, LogLevel level = LogLevel.Info)
    {
        var entry = new LogEntry(level, message, DateTime.Now);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Entries.Add(entry));
    }

    public void Info(string message)    => Log(message, LogLevel.Info);
    public void Warning(string message) => Log(message, LogLevel.Warning);
    public void Error(string message)   => Log(message, LogLevel.Error);
    public void Debug(string message)   => Log(message, LogLevel.Debug);

    public void Clear() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Entries.Clear());

    public string ExportToText() =>
        string.Join(Environment.NewLine, Entries.Select(e => e.FormattedMessage));
}
