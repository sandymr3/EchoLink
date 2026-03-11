using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoLink.Models;
using EchoLink.Services;

namespace EchoLink.ViewModels;

public partial class RemoteControlViewModel : ViewModelBase
{
    private readonly LoggingService _log = LoggingService.Instance;

    [ObservableProperty] private Device? _selectedTarget;
    public ObservableCollection<Device> OnlineDevices { get; } = new();

    // Trackpad state
    [ObservableProperty] private double _pointerX;
    [ObservableProperty] private double _pointerY;
    [ObservableProperty] private string _trackpadStatus = "Trackpad ready";

    private double _lastX;
    private double _lastY;
    private bool   _isDragging;

    public RemoteControlViewModel()
    {
        _ = LoadDevicesAsync();
    }

    [RelayCommand]
    private async Task LoadDevicesAsync()
    {
        try
        {
            var (_, devices) = await TailscaleService.Instance.GetNetworkStatusAsync();
            OnlineDevices.Clear();
            foreach (var device in devices)
            {
                if (device.IsOnline && !device.IsSelf)
                {
                    OnlineDevices.Add(device);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[RemoteControl] Failed to load devices: {ex.Message}");
        }
    }

    partial void OnSelectedTargetChanged(Device? value)
    {
        _ = ConnectToTargetAsync(value);
    }

    private async Task ConnectToTargetAsync(Device? target)
    {
        if (target == null)
        {
            RemoteControlService.Instance.Disconnect();
            TrackpadStatus = "Disconnected";
            return;
        }

        TrackpadStatus = "Connecting...";
        string pkeyPath = new SshPairingService(TailscaleService.Instance).PrivateKeyPath;
        bool success = await RemoteControlService.Instance.ConnectToTargetAsync(target, pkeyPath, CancellationToken.None);
        
        TrackpadStatus = success ? "Connected" : "Failed to connect";
    }

    // ── Quick Actions ─────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LockScreenAsync() => await SendCommandAsync("Lock");

    [RelayCommand]
    private async Task RestartAsync() => await SendCommandAsync("Restart");

    [RelayCommand]
    private async Task ShutdownAsync() => await SendCommandAsync("Shutdown");

    private async Task SendCommandAsync(string action)
    {
        _log.Info($"Sending RC command: {action}");
        if (SelectedTarget != null)
        {
            await RemoteControlService.Instance.SendCommandAsync(action);
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

        TrackpadStatus = $"Δ({deltaX:+0.0;-0.0}, {deltaY:+0.0;-0.0})";

        if (SelectedTarget != null)
        {
            _ = RemoteControlService.Instance.SendMoveAsync(deltaX, deltaY);
        }
    }

    public void OnPointerReleased()
    {
        _isDragging    = false;
        TrackpadStatus = SelectedTarget != null ? "Connected" : "Disconnected";
    }
}
