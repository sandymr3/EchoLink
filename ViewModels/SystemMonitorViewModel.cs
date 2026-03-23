using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoLink.Models;
using EchoLink.Services;
using System.Threading.Tasks;
using System;

namespace EchoLink.ViewModels;

public partial class SystemMonitorViewModel : ViewModelBase, IDisposable
{
    private readonly LoggingService _log = LoggingService.Instance;

    [ObservableProperty] private Device? _selectedDevice;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private string _statusText = "Select a device and click Connect";
    
    // UI Properties for Metrics
    [ObservableProperty] private double _cpuUsage;
    [ObservableProperty] private double _ramUsage;
    [ObservableProperty] private double _diskUsage;
    [ObservableProperty] private string _ramDetailText = "— / —";
    [ObservableProperty] private string _diskDetailText = "— / —";
    [ObservableProperty] private string _batteryText = "—";
    [ObservableProperty] private string _uptimeText = "—";
    [ObservableProperty] private string _lastUpdated = "—";
    
    [ObservableProperty] private string _cpuLabel = "CPU";
    [ObservableProperty] private string _ramLabel = "RAM";
    [ObservableProperty] private string _diskLabel = "Disk";

    public ObservableCollection<Device> OnlineDevices { get; } = new();

    private DispatcherTimer? _pollTimer;
    private volatile bool _isPolling;

    public SystemMonitorViewModel()
    {
        _ = LoadDevicesAsync();
        SystemMonitorService.Instance.SnapshotReceived += OnSnapshotReceived;
    }

    private void OnSnapshotReceived(TelemetrySnapshot snapshot)
    {
        if (!IsConnected) return;

        Dispatcher.UIThread.Post(() =>
        {
            CpuUsage = Math.Round(snapshot.CpuLoadPercentage, 1);
            RamUsage = Math.Round(snapshot.RamLoadPercentage, 1);
            DiskUsage = Math.Round(snapshot.DiskLoadPercentage, 1);
            
            RamDetailText = $"{snapshot.UsedRamDisplay} / {snapshot.TotalRamDisplay}";
            DiskDetailText = $"{snapshot.UsedDiskDisplay} / {snapshot.TotalDiskDisplay}";
            
            UptimeText = snapshot.UptimeDisplay;
            
            if (snapshot.BatteryPercentage >= 0)
            {
                BatteryText = $"{snapshot.BatteryPercentage:F0}% {(snapshot.IsCharging ? "⚡" : "🔋")}";
            }
            else
            {
                BatteryText = "N/A";
            }

            LastUpdated = $"Updated {DateTime.Now:HH:mm:ss}";
            _isPolling = false; // Reset lock since we got a response
        });
    }

    [RelayCommand]
    private async Task LoadDevicesAsync()
    {
        try
        {
            var (_, devices) = await TailscaleService.Instance.GetNetworkStatusAsync();
            OnlineDevices.Clear();
            foreach (var d in devices)
                if (d.IsOnline && !d.IsSelf)
                    OnlineDevices.Add(d);
        }
        catch (Exception ex)
        {
            _log.Error($"[SysMonitor] Load devices failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (SelectedDevice is null) return;

        IsConnecting = true;
        StatusText = $"Connecting to {SelectedDevice.Name}…";
        CpuUsage = 0;
        RamUsage = 0;
        DiskUsage = 0;
        RamDetailText = "— / —";
        DiskDetailText = "— / —";
        BatteryText = "—";
        UptimeText = "—";
        LastUpdated = "—";

        try
        {
            var pairingService = new SshPairingService(TailscaleService.Instance);
            await pairingService.EnsureKeyPairAsync();

            string privateKeyPath = pairingService.PrivateKeyPath;

            bool connected = await SystemMonitorService.Instance.ConnectAsync(SelectedDevice, privateKeyPath, CancellationToken.None);
            if (!connected)
            {
                throw new Exception("Failed to establish Unified Protocol tunnel for System Monitor.");
            }

            CpuLabel = $"CPU — {SelectedDevice.Name}";
            RamLabel = $"RAM — {SelectedDevice.Name}";
            DiskLabel = $"Disk — {SelectedDevice.Name}";

            IsConnected = true;
            StatusText = $"Connected · polling every 10 s";
            _log.Info($"[SysMonitor] Unified Protocol connected to {SelectedDevice.Name}");

            // Start poll timer
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _pollTimer.Tick += async (_, _) => await PollTelemetryAsync();
            _pollTimer.Start();
            
            // Run first poll immediately
            await PollTelemetryAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"❌ {ex.Message}";
            _log.Error($"[SysMonitor] Connect failed: {ex.Message}");
            Disconnect();
        }
        finally
        {
            IsConnecting = false;
        }
    }

    [RelayCommand]
    private void Disconnect()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
        
        IsConnected = false;
        CpuUsage = 0;
        RamUsage = 0;
        DiskUsage = 0;
        RamDetailText = "— / —";
        DiskDetailText = "— / —";
        BatteryText = "—";
        UptimeText = "—";
        LastUpdated = "—";
        CpuLabel = "CPU";
        RamLabel = "RAM";
        DiskLabel = "Disk";
        StatusText = "Disconnected";
        _isPolling = false;
        
        // We don't forcefully close the UnifiedClient here because it's shared.
        _log.Info("[SysMonitor] Disconnected.");
    }

    private async Task PollTelemetryAsync()
    {
        if (_isPolling || !IsConnected) return;
        _isPolling = true; // Wait for response before allowing another poll

        try
        {
            await SystemMonitorService.Instance.RequestSnapshotAsync();
            
            // Timeout logic: if we don't get a response in 5 seconds, unlock the poller
            _ = Task.Run(async () =>
            {
                await Task.Delay(5000);
                _isPolling = false;
            });
        }
        catch (Exception ex)
        {
            _log.Warning($"[SysMonitor] Request error: {ex.Message}");
            _isPolling = false;
        }
    }

    public void Dispose()
    {
        SystemMonitorService.Instance.SnapshotReceived -= OnSnapshotReceived;
        _pollTimer?.Stop();
    }
}
