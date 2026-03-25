using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoLink.Models;
using EchoLink.Services;
using EchoLink.Services.Auth;

namespace EchoLink.ViewModels;

public partial class DashboardViewModel : ViewModelBase
{
    private readonly LoggingService _log = LoggingService.Instance;
    private readonly SshPairingService _pairingService;

    [ObservableProperty] private bool _isMeshOnline;
    [ObservableProperty] private string _tailscaleIp = "—";
    [ObservableProperty] private string _networkName = "EchoLink-Mesh";
    [ObservableProperty] private string _statusText = "Disconnected";
    [ObservableProperty] private bool _isRefreshing;

    [ObservableProperty] private string _guestInvitePin = "";
    [ObservableProperty] private bool _isInviteVisible;
    [ObservableProperty] private string _inviteStatusText = "";
    [ObservableProperty] private bool _isPairingInvite; 

    [ObservableProperty] private bool _isJoinVisible;
    [ObservableProperty] private string _joinPin = "";
    [ObservableProperty] private string _joinStatusText = "";

    public ObservableCollection<Device> EcosystemDevices { get; } = new();
    public ObservableCollection<Device> GuestDevices { get; } = new();
    public ObservableCollection<Device> OtherDevices { get; } = new();

    public DashboardViewModel()
    {
        _pairingService = new SshPairingService(TailscaleService.Instance);
        _pairingService.PairingCompleted += OnPairingCompleted;
        
        _log.Info("Dashboard initialized.");
        _ = InitializeDashboardAsync();
    }

    private async Task InitializeDashboardAsync()
    {
        await TailscaleService.Instance.CleanupDuplicateNodesAsync();
        await RefreshNetworkAsync();
    }

    private void OnPairingCompleted()
    {
        _log.Info("[Dashboard] Remote device confirmed pairing. Refreshing...");
        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            CloseGuestInvite();
            _ = RefreshNetworkAsync();
        });
    }

    [RelayCommand]
    private async Task GenerateGuestInviteAsync()
    {
        IsInviteVisible = true;
        IsPairingInvite = false;
        InviteStatusText = "Generating PIN...";
        GuestInvitePin = "";

        var (pin, expiresIn) = await MiddlewareClient.Instance.GenerateGuestPinAsync();
        
        if (!string.IsNullOrEmpty(pin))
        {
            GuestInvitePin = pin;
            InviteStatusText = $"Guest PIN: {pin}\nValid for {expiresIn} minutes";
            _log.Info($"[Dashboard] Generated Guest PIN: {pin}");
        }
        else
        {
            InviteStatusText = "Failed to generate PIN.";
        }
    }

    [RelayCommand]
    private async Task GeneratePairingInviteAsync()
    {
        IsInviteVisible = true;
        IsPairingInvite = true;
        InviteStatusText = "Generating PIN...";
        GuestInvitePin = "";

        try
        {
            string pubKey = await _pairingService.GetMyPublicKeyAsync();
            var data = new MiddlewareClient.PairData
            {
                IpAddress = TailscaleIp,
                PublicKey = pubKey,
                Hostname = Environment.MachineName
            };

            var (pin, expiresIn) = await MiddlewareClient.Instance.CreatePairingPinAsync(data);
            
            if (!string.IsNullOrEmpty(pin))
            {
                GuestInvitePin = pin;
                InviteStatusText = $"Pairing PIN: {pin}\nShare this with another EchoLink user.";
                _log.Info($"[Dashboard] Generated Pairing PIN: {pin}");
            }
            else
            {
                InviteStatusText = "Failed to generate PIN.";
            }
        }
        catch (Exception ex)
        {
            InviteStatusText = "Error generating PIN.";
            _log.Error($"[Dashboard] Pairing PIN error: {ex.Message}");
        }
    }

    [RelayCommand]
    private void CloseGuestInvite()
    {
        IsInviteVisible = false;
        GuestInvitePin = "";
    }

    [RelayCommand]
    private void ShowJoinPopup()
    {
        IsJoinVisible = true;
        JoinPin = "";
        JoinStatusText = "Enter the 6-digit PIN from the other device.";
    }

    [RelayCommand]
    private void CloseJoinPopup()
    {
        IsJoinVisible = false;
        JoinPin = "";
    }

    [RelayCommand]
    private async Task SubmitJoinPinAsync()
    {
        if (string.IsNullOrWhiteSpace(JoinPin) || JoinPin.Length < 6)
        {
            JoinStatusText = "Please enter a valid 6-digit PIN.";
            return;
        }

        JoinStatusText = "Joining...";
        try
        {
            var hostData = await MiddlewareClient.Instance.ClaimPairingPinAsync(JoinPin);
            if (hostData != null && !string.IsNullOrEmpty(hostData.IpAddress))
            {
                _log.Info($"[Dashboard] Claimed pairing PIN. Host IP: {hostData.IpAddress}, Hostname: {hostData.Hostname}");
                
                // 1. Trust host public key and save info
                await _pairingService.TrustPublicKeyAsync(hostData.IpAddress, "echolink-mesh", hostData.PublicKey);

                // 2. Send "Pairing Complete" handshake to Host to close their PIN window
                await _pairingService.SendPairingCompleteAsync(hostData.IpAddress);

                JoinStatusText = "Success!";
                await Task.Delay(1000);
                CloseJoinPopup();
                _ = RefreshNetworkAsync();
            }
            else
            {
                JoinStatusText = "Invalid or expired PIN.";
            }
        }
        catch (Exception ex)
        {
            JoinStatusText = "Error joining device.";
            _log.Error($"[Dashboard] Join PIN error: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RefreshNetworkAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        StatusText = "Checking...";

        try
        {
            var state = await TailscaleService.Instance.GetBackendStateAsync();
            if (state == "Starting" || state == "NeedsLogin")
            {
               await Task.Delay(2000); 
               state = await TailscaleService.Instance.GetBackendStateAsync();
            }

            if (state != "Running")
            {
                TailscaleIp = "—";
                IsMeshOnline = false;
                StatusText = state == "Starting" ? "Connecting..." : "Disconnected";
                return;
            }

            string? selfIp = null;
            var devices = new List<Device>();

            for (int attempt = 1; attempt <= 4; attempt++)
            {
                (selfIp, devices) = await TailscaleService.Instance.GetNetworkStatusAsync();
                if (selfIp != null) break;
                await Task.Delay(2000);
            }

            if (selfIp != null)
            {
                TailscaleIp = selfIp;
                IsMeshOnline = true;
                StatusText = "Connected";
                
                var selfNode = devices.FirstOrDefault(d => d.IsSelf);
                bool isGuestNode = selfNode?.Tags?.Contains("tag:guest") ?? false;

                EcosystemDevices.Clear();
                GuestDevices.Clear();
                OtherDevices.Clear();

                foreach (var d in devices)
                {
                    if (d.IsSelf || d.UserId == selfNode?.UserId)
                    {
                        d.Section = DeviceSection.Ecosystem;
                        if (!isGuestNode) EcosystemDevices.Add(d);
                    }
                    else if (d.Tags != null && d.Tags.Contains("tag:guest"))
                    {
                        d.Section = DeviceSection.Guests;
                        if (!isGuestNode) GuestDevices.Add(d);
                    }
                    else if (d.IsPaired)
                    {
                        d.Section = DeviceSection.OtherDevices;
                        OtherDevices.Add(d);
                    }

                    if (isGuestNode)
                    {
                        if (d.IsPaired && !d.IsSelf)
                        {
                            d.Section = DeviceSection.OtherDevices;
                            OtherDevices.Add(d);
                        }
                    }
                }
            }
            else
            {
                TailscaleIp = "—";
                IsMeshOnline = false;
                StatusText = "Disconnected";
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
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime sv)
        {
             var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(sv.MainView);
             if (topLevel?.Clipboard != null) await topLevel.Clipboard.SetTextAsync(TailscaleIp);
        }
        else if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime dt && dt.MainWindow is { } window)
        {
            var clipboard = Avalonia.Controls.TopLevel.GetTopLevel(window)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(TailscaleIp);
        }
    }

    [RelayCommand]
    private async Task RemoveDeviceAsync(Device device)
    {
        if (device == null || string.IsNullOrWhiteSpace(device.IpAddress) || device.IsSelf) 
            return;

        try
        {
            _log.Info($"[Dashboard] Manually removing device: {device.Name} ({device.IpAddress})");
            bool success = await TailscaleService.Instance.RemoveNodeAsync(device.IpAddress);
            if (success)
            {
                await RefreshNetworkAsync();
            }
        }
        catch (Exception ex) 
        { 
            _log.Error($"[Dashboard] Remove error: {ex.Message}"); 
        }
    }

    [RelayCommand]
    private async Task UnpairDeviceAsync(Device device)
    {
        if (device == null || string.IsNullOrWhiteSpace(device.IpAddress)) return;

        try
        {
            await _pairingService.UntrustPublicKeyAsync(device.IpAddress);
            _log.Info($"[Dashboard] Unpaired device: {device.Name} ({device.IpAddress})");
            await RefreshNetworkAsync();
        }
        catch (Exception ex)
        {
            _log.Error($"[Dashboard] Unpair failed: {ex.Message}");
        }
    }
}
