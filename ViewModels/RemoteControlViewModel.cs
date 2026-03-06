using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoLink.Services;

namespace EchoLink.ViewModels;

public partial class RemoteControlViewModel : ViewModelBase
{
    private readonly LoggingService _log = LoggingService.Instance;

    // Trackpad state
    [ObservableProperty] private double _pointerX;
    [ObservableProperty] private double _pointerY;
    [ObservableProperty] private string _trackpadStatus = "Trackpad ready";

    private double _lastX;
    private double _lastY;
    private bool   _isDragging;

    private static readonly string CommandInbox =
        Path.Combine(AppContext.BaseDirectory, "command_inbox");

    // ── Quick Actions ─────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LockScreenAsync() => await SendCommandAsync("lock");

    [RelayCommand]
    private async Task RestartAsync() => await SendCommandAsync("restart");

    [RelayCommand]
    private async Task ShutdownAsync() => await SendCommandAsync("shutdown");

    private async Task SendCommandAsync(string action)
    {
        _log.Info($"Sending command: {action}");
        try
        {
            Directory.CreateDirectory(CommandInbox);
            var payload = new { action, timestamp = DateTime.UtcNow };
            var json    = JsonSerializer.Serialize(payload);
            var file    = Path.Combine(CommandInbox, $"{action}_{DateTime.UtcNow:yyyyMMddHHmmss}.json");
            await File.WriteAllTextAsync(file, json);
            _log.Info($"Command '{action}' written to inbox: {file}");
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to send command '{action}': {ex.Message}");
        }
    }

    // ── Trackpad ──────────────────────────────────────────────────────────────

    public void OnPointerPressed(double x, double y)
    {
        _isDragging  = true;
        _lastX       = x;
        _lastY       = y;
        TrackpadStatus = "Pointer pressed";
    }

    public void OnPointerMoved(double x, double y)
    {
        if (!_isDragging) return;

        double deltaX = x - _lastX;
        double deltaY = y - _lastY;
        _lastX = x;
        _lastY = y;

        PointerX = x;
        PointerY = y;

        // Stream deltas (placeholder — would send via TCP/UDP in production)
        TrackpadStatus = $"Δ({deltaX:+0.0;-0.0}, {deltaY:+0.0;-0.0})";
        _log.Debug($"Trackpad move: dx={deltaX:F1}, dy={deltaY:F1}");
    }

    public void OnPointerReleased()
    {
        _isDragging    = false;
        TrackpadStatus = "Pointer released";
    }
}
