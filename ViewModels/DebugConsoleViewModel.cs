using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoLink.Models;
using EchoLink.Services;

namespace EchoLink.ViewModels;

public partial class DebugConsoleViewModel : ViewModelBase
{
    private readonly LoggingService _log = LoggingService.Instance;

    // Bind directly to the shared log service entries
    public ObservableCollection<LogEntry> LogEntries => _log.Entries;

    [ObservableProperty] private string _exportStatus = string.Empty;

    public DebugConsoleViewModel()
    {
        _log.Info("Debug Console ready.");
    }

    [RelayCommand]
    private void ClearLogs()
    {
        _log.Clear();
    }

    [RelayCommand]
    private async Task ExportLogsAsync()
    {
        try
        {
            var docPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var file    = Path.Combine(docPath, $"echolink_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            await File.WriteAllTextAsync(file, _log.ExportToText());
            ExportStatus = $"Exported → {file}";
            _log.Info($"Logs exported to: {file}");
        }
        catch (Exception ex)
        {
            ExportStatus = $"Export failed: {ex.Message}";
            _log.Error($"Log export error: {ex.Message}");
        }
    }
}
