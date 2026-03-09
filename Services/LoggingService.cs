using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using EchoLink.Models;

namespace EchoLink.Services;

public sealed class LoggingService
{
    private static readonly LoggingService _instance = new();
    public static LoggingService Instance => _instance;

    public ObservableCollection<LogEntry> Entries { get; } = [];

    private readonly string? _logFilePath;
    private readonly object _fileLock = new();

    private LoggingService()
    {
        // On Windows there is no terminal (WinExe), so write a rolling log file
        // under %LOCALAPPDATA%\EchoLink\ for debugging.
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EchoLink");
            Directory.CreateDirectory(dir);
            _logFilePath = Path.Combine(dir, "echolink_debug.log");

            // Write a fresh session header so runs are easy to separate.
            File.AppendAllText(_logFilePath,
                $"\n--- EchoLink session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---\n",
                Encoding.UTF8);
        }
        catch
        {
            // If we can't create the log file, just skip file logging silently.
            _logFilePath = null;
        }
    }

    public void Log(string message, LogLevel level = LogLevel.Info)
    {
        var entry = new LogEntry(level, message, DateTime.Now);

        // Print to console for Logcat/Terminal visibility
        Console.WriteLine(entry.FormattedMessage);

        // Always append to the log file first (no UI thread dependency).
        WriteToFile(entry.FormattedMessage);

        // Update the observable collection on the UI thread for in-app display.
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

    public string? LogFilePath => _logFilePath;

    private void WriteToFile(string line)
    {
        if (_logFilePath == null) return;
        try
        {
            lock (_fileLock)
                File.AppendAllText(_logFilePath, line + "\n", Encoding.UTF8);
        }
        catch { /* silently skip if write fails */ }
    }
}
