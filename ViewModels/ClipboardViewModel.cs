using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoLink.Models;
using EchoLink.Services;

namespace EchoLink.ViewModels;

public partial class ClipboardViewModel : ViewModelBase
{
    private readonly LoggingService _log = LoggingService.Instance;

    [ObservableProperty] private bool _isAutoSyncEnabled;
    [ObservableProperty] private string _statusText = "Idle";

    public ObservableCollection<ClipboardEntry> History { get; } =
    [
        new ClipboardEntry("Hello from EchoLink!",           "Gautam-Desktop", DateTime.Now.AddMinutes(-2)),
        new ClipboardEntry("https://github.com/fosshack2026", "Gautam-Phone",   DateTime.Now.AddMinutes(-8)),
        new ClipboardEntry("Meeting at 3 PM — don't forget!", "Gautam-Laptop",  DateTime.Now.AddMinutes(-45)),
    ];

    partial void OnIsAutoSyncEnabledChanged(bool value)
    {
        StatusText = value ? "Auto-Sync active" : "Auto-Sync paused";
        _log.Info($"Clipboard auto-sync {(value ? "enabled" : "disabled")}.");
    }

    [RelayCommand]
    private async Task PushClipboardAsync()
    {
        _log.Info("Pushing current clipboard to peers...");
        StatusText = "Pushing...";
        await Task.Delay(600);
        var entry = new ClipboardEntry("[local clipboard content]", "This Device", DateTime.Now);
        History.Insert(0, entry);
        StatusText = "Pushed to all peers.";
        _log.Info("Clipboard pushed.");
    }

    [RelayCommand]
    private async Task CopyEntryAsync(ClipboardEntry? entry)
    {
        if (entry is null) return;

        var app = Avalonia.Application.Current;
        if (app?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime dt
            && dt.MainWindow is { } window)
        {
            var clipboard = Avalonia.Controls.TopLevel.GetTopLevel(window)?.Clipboard;
            if (clipboard is not null)
                await clipboard.SetTextAsync(entry.Content);
        }

        StatusText = "Copied to clipboard!";
        _log.Info($"Copied entry from {entry.SourceDevice}.");
    }

    [RelayCommand]
    private void ClearHistory()
    {
        History.Clear();
        StatusText = "History cleared.";
        _log.Info("Clipboard history cleared.");
    }
}
