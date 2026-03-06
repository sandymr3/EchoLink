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

    public ObservableCollection<Device> Devices { get; } =
    [
        new Device { Name = "Gautam-Desktop", IpAddress = "100.64.10.1", IsOnline = true,  DeviceType = "Desktop", Os = "Windows 11" },
        new Device { Name = "Gautam-Phone",   IpAddress = "100.64.10.2", IsOnline = true,  DeviceType = "Phone",   Os = "Android 14" },
        new Device { Name = "Gautam-Laptop",  IpAddress = "100.64.10.3", IsOnline = false, DeviceType = "Laptop",  Os = "Ubuntu 24.04" },
    ];

    public DashboardViewModel()
    {
        _log.Info("Dashboard initialized.");
    }

    [RelayCommand]
    private async Task RefreshNetworkAsync()
    {
        _log.Info("Refreshing network status...");
        StatusText = "Checking...";
        await Task.Delay(1200); // simulate network call

        IsMeshOnline = true;
        TailscaleIp  = "100.64.10.1";
        StatusText   = "Connected";
        _log.Info($"Mesh online. IP: {TailscaleIp}");
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
