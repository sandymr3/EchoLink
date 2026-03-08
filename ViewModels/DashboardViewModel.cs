using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoLink.Models;
using EchoLink.Services;

namespace EchoLink.ViewModels;

public partial class DashboardViewModel : ViewModelBase
{
    private readonly LoggingService _log = LoggingService.Instance;

    [ObservableProperty] private bool _isMeshOnline;
    [ObservableProperty] private string _tailscaleIp = "—";
    [ObservableProperty] private string _networkName = "EchoLink-Mesh";
    [ObservableProperty] private string _statusText = "Disconnected";
    [ObservableProperty] private bool _isRefreshing;

    public ObservableCollection<Device> Devices { get; } = [];

    public DashboardViewModel()
    {
        _log.Info("Dashboard initialized.");
        _ = RefreshNetworkAsync();
    }

    [RelayCommand]
    private async Task RefreshNetworkAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        _log.Info("Refreshing network status...");
        StatusText = "Checking...";

        try
        {
            var (selfIp, devices) = await TailscaleService.Instance.GetNetworkStatusAsync();

            Devices.Clear();
            foreach (var d in devices)
                Devices.Add(d);

            if (selfIp != null)
            {
                TailscaleIp = selfIp;
                IsMeshOnline = true;
                StatusText = "Connected";
                _log.Info($"Mesh online. IP: {TailscaleIp}, {devices.Count} device(s)");
            }
            else
            {
                TailscaleIp = "—";
                IsMeshOnline = false;
                StatusText = "Disconnected";
                _log.Warning("Could not retrieve Tailscale status.");
            }
        }
        catch (Exception ex)
        {
            StatusText = "Error";
            _log.Error($"Refresh failed: {ex.Message}");
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    private async Task CopyIpAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime dt
            && dt.MainWindow is { } window)
        {
            var clipboard = Avalonia.Controls.TopLevel.GetTopLevel(window)?.Clipboard;
            if (clipboard is not null)
                await clipboard.SetTextAsync(TailscaleIp);
        }
        _log.Info($"Copied IP {TailscaleIp} to clipboard.");
    }
}
