using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoLink.Models;
using EchoLink.Services;
using Renci.SshNet;

namespace EchoLink.ViewModels;

public partial class SystemMonitorViewModel : ViewModelBase
{
    private readonly LoggingService _log = LoggingService.Instance;
    private const int Socks5Port = 1055;

    [ObservableProperty] private Device? _selectedDevice;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private string _statusText = "Select a device and click Connect";
    [ObservableProperty] private double _cpuUsage;
    [ObservableProperty] private double _ramUsage;
    [ObservableProperty] private string _ramDetailText = "— / —";
    [ObservableProperty] private string _lastUpdated = "—";
    [ObservableProperty] private string _cpuLabel = "CPU";
    [ObservableProperty] private string _ramLabel = "RAM";

    public ObservableCollection<Device> OnlineDevices { get; } = new();

    private SshClient? _sshClient;
    private ITelemetryStrategy? _strategy;
    private DispatcherTimer? _pollTimer;
    private volatile bool _isPolling;

    public SystemMonitorViewModel()
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
        RamDetailText = "— / —";
        LastUpdated = "—";

        try
        {
            var pairingService = new SshPairingService(TailscaleService.Instance);
            await pairingService.EnsureKeyPairAsync();

            var settings = SettingsService.Instance.Load();
            string username = settings.PeerUsernames.TryGetValue(SelectedDevice.IpAddress, out var u)
                ? u
                : Environment.UserName;

            int sshPort = SelectedDevice.Os?.Contains("android", StringComparison.OrdinalIgnoreCase) == true
                ? 2222
                : 22;

            _strategy = DetectStrategy(SelectedDevice);
            CpuLabel = $"CPU — {SelectedDevice.Name}";
            RamLabel = $"RAM — {SelectedDevice.Name}";

            var privateKeyFile = new PrivateKeyFile(pairingService.PrivateKeyPath);
            var connectionInfo = new ConnectionInfo(
                SelectedDevice.IpAddress, sshPort, username,
                ProxyTypes.Socks5, "127.0.0.1", Socks5Port, "", "",
                new PrivateKeyAuthenticationMethod(username, privateKeyFile));

            _sshClient = new SshClient(connectionInfo);
            await Task.Run(() => _sshClient.Connect());

            IsConnected = true;
            StatusText = $"Connected · polling every 10 s";
            _log.Info($"[SysMonitor] SSH connected to {SelectedDevice.Name}");

            // Run first poll immediately
            await PollTelemetryAsync();

            // Start poll timer — tick is skipped if previous poll is still running
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _pollTimer.Tick += async (_, _) => await PollTelemetryAsync();
            _pollTimer.Start();
        }
        catch (Exception ex)
        {
            StatusText = $"❌ {ex.Message}";
            _log.Error($"[SysMonitor] Connect failed: {ex.Message}");
            CleanupSsh();
        }
        finally
        {
            IsConnecting = false;
        }
    }

    [RelayCommand]
    private void Disconnect()
    {
        CleanupSsh();
        IsConnected = false;
        CpuUsage = 0;
        RamUsage = 0;
        RamDetailText = "— / —";
        LastUpdated = "—";
        CpuLabel = "CPU";
        RamLabel = "RAM";
        StatusText = "Disconnected";
        _log.Info("[SysMonitor] Disconnected.");
    }

    private async Task PollTelemetryAsync()
    {
        // Hang-test guard: skip tick if previous request hasn't finished
        if (_isPolling || _sshClient is null || _strategy is null) return;
        _isPolling = true;

        try
        {
            // Connection-drop check before attempting the command
            if (!_sshClient.IsConnected)
            {
                HandleConnectionDrop();
                return;
            }

            var snapshot = await _strategy.GetSnapshotAsync(_sshClient);

            // Marshal back to UI thread
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                CpuUsage = Math.Round(snapshot.CpuLoadPercentage, 1);
                RamUsage = Math.Round(snapshot.RamLoadPercentage, 1);
                RamDetailText = $"{snapshot.UsedRamDisplay} / {snapshot.TotalRamDisplay}";
                LastUpdated = $"Updated {DateTime.Now:HH:mm:ss}";
            });
        }
        catch (Exception ex)
        {
            // Detect broken SSH pipe
            bool isConnectionError =
                ex is Renci.SshNet.Common.SshConnectionException
                || ex is System.Net.Sockets.SocketException
                || ex is ObjectDisposedException
                || (_sshClient?.IsConnected == false);

            if (isConnectionError)
            {
                await Dispatcher.UIThread.InvokeAsync(HandleConnectionDrop);
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    StatusText = $"⚠ Poll failed: {ex.Message}");
                _log.Warning($"[SysMonitor] Poll error: {ex.Message}");
            }
        }
        finally
        {
            _isPolling = false;
        }
    }

    /// <summary>
    /// Called on the UI thread when the SSH connection is detected as broken.
    /// Stops the timer and updates UI to "Device Offline" state.
    /// </summary>
    private void HandleConnectionDrop()
    {
        _log.Warning("[SysMonitor] SSH connection lost — stopping poller.");
        CleanupSsh();
        IsConnected = false;
        StatusText = "⚠ Device Offline";
        LastUpdated = $"Lost {DateTime.Now:HH:mm:ss}";
    }

    private static ITelemetryStrategy DetectStrategy(Device device)
    {
        var os = device.Os ?? string.Empty;
        if (os.Contains("windows", StringComparison.OrdinalIgnoreCase))
            return new WindowsTelemetryStrategy();
        if (os.Contains("android", StringComparison.OrdinalIgnoreCase))
            return new AndroidTelemetryStrategy();
        return new LinuxTelemetryStrategy(); // Default: Linux, macOS, etc.
    }

    private void CleanupSsh()
    {
        _pollTimer?.Stop();
        _pollTimer = null;

        try { _sshClient?.Disconnect(); } catch { /* ignore */ }
        _sshClient?.Dispose();
        _sshClient = null;
        _strategy = null;
    }
}
