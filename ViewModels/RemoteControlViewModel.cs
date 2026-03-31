using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoLink.Models;
using EchoLink.Services;
using EchoLink.Services.UnifiedProtocol;
using EchoLink.Services.RemoteControl;

namespace EchoLink.ViewModels;

public partial class RemoteControlViewModel : ViewModelBase
{
    private readonly LoggingService _log = LoggingService.Instance;
    private readonly DesktopKeyboardSender? _keyboardSender;

    [ObservableProperty] private Device? _selectedTarget;
    public ObservableCollection<Device> OnlineDevices { get; } = new();

    // Trackpad state
    [ObservableProperty] private double _pointerX;
    [ObservableProperty] private double _pointerY;
    [ObservableProperty] private string _trackpadStatus = "Trackpad ready";
    [ObservableProperty] private string _audioStatus = "Audio idle";
    [ObservableProperty] private bool _isAudioStreaming;
    [ObservableProperty] private bool _isAudioConnecting;
    [ObservableProperty] private bool _isAudioSetupDialogVisible;
    [ObservableProperty] private bool _isAudioSetupChecking;
    [ObservableProperty] private string _audioSetupDialogErrorText = string.Empty;
    [ObservableProperty] private bool _isTrackpadExpanded;

    public string TrackpadExpandIcon => IsTrackpadExpanded ? "▼" : "▲";
    public string TrackpadExpandText => IsTrackpadExpanded ? "Hide Trackpad" : "Show Trackpad";

    // PC Keyboard routing state
    [ObservableProperty] private bool _isPcKeyboardRoutingEnabled;
    [ObservableProperty] private string _pcKeyboardStatus = "PC Keyboard routing disabled";

    private double _lastX;
    private double _lastY;
    private bool   _isDragging;

    public bool CanStartAudio => !IsAudioStreaming && !IsAudioConnecting;
    public bool CanStopAudio => IsAudioStreaming || IsAudioConnecting;

    public string AudioSetupDialogTitle => "System Audio Setup Required";
    public string AudioSetupDialogMessage =>
        "To stream high-quality system audio from Windows, EchoLink requires the free VB-Cable Virtual Audio Device. Please install it on the target PC.";
    public string AudioSetupContinueButtonText => IsAudioSetupChecking ? "Checking..." : "Continue to audio streaming";
    public bool CanContinueAudioSetup => !IsAudioSetupChecking;
    public bool CanCancelAudioSetup => !IsAudioSetupChecking;
    public bool HasAudioSetupDialogError => !string.IsNullOrWhiteSpace(AudioSetupDialogErrorText);

    // Android→PC Keyboard state (phone types, PC receives)
    private bool _isResetting = false;
    private string _previousText = " ";
    public Action? RequestKeyboardReset { get; set; }

    public RemoteControlViewModel()
    {
        // Initialize PC→Android keyboard sender
        _keyboardSender = new DesktopKeyboardSender();
        _keyboardSender.RoutingStateChanged += OnRoutingStateChanged;
        _keyboardSender.Start();

        _ = LoadDevicesAsync();

        // Subscribe to device discovery events - just update UI from cached data
        DeviceDiscoveryService.Instance.DeviceListChanged += () =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => LoadDevicesAsync());
        };
    }

    private void OnRoutingStateChanged(bool isActive)
    {
        IsPcKeyboardRoutingEnabled = isActive;
        PcKeyboardStatus = isActive
            ? "PC Keyboard routing ACTIVE - Press Ctrl+Alt+K to disable"
            : "PC Keyboard routing disabled - Press Ctrl+Alt+K to enable";

        _log.Info($"[RemoteControl] PC Keyboard routing: {(isActive ? "ENABLED" : "DISABLED")}");
    }

    [RelayCommand]
    private async Task LoadDevicesAsync()
    {
        try
        {
            // Get feature target devices from DeviceDiscoveryService (already filtered and cached)
            // Dashboard controls the actual RefreshAsync call
            var devices = DeviceDiscoveryService.Instance.GetFeatureTargetDevices();
            
            UpdateDeviceCollection(OnlineDevices, devices);
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

    partial void OnIsTrackpadExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(TrackpadExpandIcon));
        OnPropertyChanged(nameof(TrackpadExpandText));
    }

    [RelayCommand]
    private void ToggleTrackpad()
    {
        IsTrackpadExpanded = !IsTrackpadExpanded;
    }

    private async Task ConnectToTargetAsync(Device? target)
    {
        await AudioStreamingService.Instance.StopAllAsync();
        IsAudioStreaming = false;
        IsAudioConnecting = false;
        IsAudioSetupDialogVisible = false;
        IsAudioSetupChecking = false;
        AudioSetupDialogErrorText = string.Empty;
        AudioStatus = "Audio idle";
        ResetKeyboardTrap();

        if (target == null)
        {
            RemoteControlService.Instance.Disconnect();
            TrackpadStatus = "Disconnected";
            return;
        }

        // Get the freshest IP from DeviceDiscoveryService using NodeId
        var freshDevice = DeviceDiscoveryService.Instance.CachedDevices
            .FirstOrDefault(d => d.NodeId == target.NodeId) ?? target;
        
        if (string.IsNullOrEmpty(freshDevice.IpAddress))
        {
            TrackpadStatus = "No IP available for target";
            _log.Error($"[RemoteControl] No IP available for {target.Name} (NodeId: {target.NodeId})");
            return;
        }

        TrackpadStatus = "Connecting...";
        bool success = await RemoteControlService.Instance.ConnectToTargetAsync(freshDevice, CancellationToken.None);

        TrackpadStatus = success
            ? "Connected"
            : "Failed to connect via Unified Protocol";
    }

    // ── Keyboard Diffing Engine ───────────────────────────────────────────────

    public void ProcessKeyboardTextChange(string newText)
    {
        if (_isResetting || !UnifiedProtocolClient.Instance.IsConnected) return;

        newText ??= "";

        // 1. Safety net: If user deletes everything
        if (string.IsNullOrEmpty(newText))
        {
            _ = UnifiedProtocolClient.Instance.SendKeyboardControlKeyAsync(8, CancellationToken.None); // Backspace
            ResetKeyboardTrap();
            return;
        }

        // 2. Find the Common Prefix
        int commonLength = 0;
        int minLength = Math.Min(_previousText.Length, newText.Length);
        while (commonLength < minLength && _previousText[commonLength] == newText[commonLength])
        {
            commonLength++;
        }

        // 3. Calculate Deletions (Backspaces)
        int backspacesNeeded = _previousText.Length - commonLength;
        for (int i = 0; i < backspacesNeeded; i++)
        {
            _ = UnifiedProtocolClient.Instance.SendKeyboardControlKeyAsync(8, CancellationToken.None); 
        }

        // 4. Calculate Additions
        string charsToAdd = newText.Substring(commonLength);
        if (!string.IsNullOrEmpty(charsToAdd))
        {
            // INTERCEPT THE ENTER KEY
            // Because AcceptsReturn="True", Enter shows up as a newline
            if (charsToAdd == "\n" || charsToAdd == "\r\n")
            {
                _ = UnifiedProtocolClient.Instance.SendKeyboardControlKeyAsync(13, CancellationToken.None); // VK Code for Enter
            }
            else if (charsToAdd.Contains("\n"))
            {
                 // Edge case: Sometimes Gboard sends text AND a newline together
                 string cleanedText = charsToAdd.Replace("\n", "").Replace("\r", "");
                 if (!string.IsNullOrEmpty(cleanedText)) 
                     _ = UnifiedProtocolClient.Instance.SendKeyboardTextAsync(cleanedText, CancellationToken.None);
                 _ = UnifiedProtocolClient.Instance.SendKeyboardControlKeyAsync(13, CancellationToken.None); // Send the Enter key after the text
            }
            else
            {
                _ = UnifiedProtocolClient.Instance.SendKeyboardTextAsync(charsToAdd, CancellationToken.None);
            }
        }

        // Update state
        _previousText = newText;

        // 5. Safe Memory Flush
        // We NO LONGER reset on every spacebar! 
        // We let the string grow so Gboard can reach back and edit past words.
        // We only reset if the string gets absurdly long (> 200 chars) to prevent lag,
        // and only when we are safely at the end of a word (space).
        if (_previousText.Length > 200 && newText.EndsWith(" "))
        {
            ResetKeyboardTrap();
        }
    }

    public void ResetKeyboardTrap()
    {
        _isResetting = true;
        _previousText = " ";
        RequestKeyboardReset?.Invoke();
        _isResetting = false;


    }

    [RelayCommand]
    private async Task StartAudioAsync()
    {
        if (IsAudioConnecting)
        {
            return;
        }

        if (SelectedTarget == null)
        {
            AudioStatus = "Select a target device first";
            return;
        }

        try
        {
            await AudioStreamingService.Instance.StopAllAsync();
            IsAudioStreaming = false;
            IsAudioConnecting = false;

            bool sendOk;

            if (OperatingSystem.IsAndroid() || OperatingSystem.IsLinux())
            {
                // Sender-side guard: only run VB-Cable preflight when the target is Windows.
                IsAudioConnecting = true;
                AudioStatus = "Connecting...";
                IsAudioSetupDialogVisible = false;
                IsAudioSetupChecking = false;
                AudioSetupDialogErrorText = string.Empty;

                if (!ShouldRunWindowsAudioPreflight(SelectedTarget))
                {
                    sendOk = await StartNormalAudioStreamingAsync();
                    IsAudioConnecting = false;
                    IsAudioStreaming = sendOk;
                    AudioStatus = sendOk ? "Mic + playback active" : "Audio start failed";
                }
                else
                {
                    var preflightResult = await AudioStreamingService.Instance.SendAudioPreflightAsync(SelectedTarget);
                    if (preflightResult == AudioStreamingService.AudioPreflightResult.Ready)
                    {
                        sendOk = await StartNormalAudioStreamingAsync();
                        IsAudioConnecting = false;
                        IsAudioStreaming = sendOk;
                        AudioStatus = sendOk ? "Mic + playback active" : "Audio start failed";
                    }
                    else if (preflightResult == AudioStreamingService.AudioPreflightResult.Missing)
                    {
                        IsAudioConnecting = false;
                        IsAudioStreaming = false;
                        AudioStatus = "System audio setup required";
                        IsAudioSetupDialogVisible = true;
                        AudioSetupDialogErrorText = string.Empty;
                    }
                    else
                    {
                        IsAudioConnecting = false;
                        IsAudioStreaming = false;
                        AudioStatus = "Audio preflight failed";
                    }
                }
            }
            else
            {
                // Desktop: audio receive is handled by unified protocol handler.
                sendOk = await AudioStreamingService.Instance.StartLoopbackSendAsync(SelectedTarget);

                IsAudioStreaming = sendOk;
                AudioStatus = sendOk
                    ? "System audio streaming active"
                    : "Audio start failed";
            }
        }
        catch (Exception ex)
        {
            IsAudioConnecting = false;
            IsAudioStreaming = false;
            AudioStatus = $"Audio error: {ex.Message}";
            _log.Error($"[RemoteControl] Audio start failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task StopAudioAsync()
    {
        await AudioStreamingService.Instance.StopAllAsync();
        IsAudioConnecting = false;
        IsAudioStreaming = false;
        IsAudioSetupDialogVisible = false;
        IsAudioSetupChecking = false;
        AudioSetupDialogErrorText = string.Empty;
        AudioStatus = "Audio stopped";
    }

    [RelayCommand]
    private void OpenAudioSetupLink()
    {
        const string url = "https://vb-audio.com/Cable/";
        OpenBrowser(url);
    }

    [RelayCommand]
    private async Task ContinueAudioStreamingAsync()
    {
        if (!IsAudioSetupDialogVisible || SelectedTarget == null || IsAudioSetupChecking)
        {
            return;
        }

        IsAudioSetupChecking = true;
        AudioSetupDialogErrorText = string.Empty;

        try
        {
            if (!ShouldRunWindowsAudioPreflight(SelectedTarget))
            {
                IsAudioSetupDialogVisible = false;
                IsAudioConnecting = true;
                AudioStatus = "Connecting...";

                bool directSendOk = await StartNormalAudioStreamingAsync();
                IsAudioConnecting = false;
                IsAudioStreaming = directSendOk;
                AudioStatus = directSendOk ? "Mic + playback active" : "Audio start failed";
                return;
            }

            var recheckResult = await AudioStreamingService.Instance.SendAudioPreflightAsync(SelectedTarget);
            if (recheckResult == AudioStreamingService.AudioPreflightResult.Ready)
            {
                IsAudioSetupDialogVisible = false;
                IsAudioConnecting = true;
                AudioStatus = "Connecting...";

                bool sendOk = await StartNormalAudioStreamingAsync();
                IsAudioConnecting = false;
                IsAudioStreaming = sendOk;
                AudioStatus = sendOk ? "Mic + playback active" : "Audio start failed";
            }
            else if (recheckResult == AudioStreamingService.AudioPreflightResult.Missing)
            {
                IsAudioStreaming = false;
                AudioSetupDialogErrorText = "VB-Cable is still not detected on the target PC. Please ensure it is installed, set as the default playback device, and try again.";
                AudioStatus = "System audio setup required";
            }
            else
            {
                IsAudioStreaming = false;
                AudioSetupDialogErrorText = "Re-check failed due to a connection error. Please retry.";
                AudioStatus = "Audio preflight failed";
            }
        }
        finally
        {
            IsAudioSetupChecking = false;
        }
    }

    private static bool ShouldRunWindowsAudioPreflight(Device target)
    {
        return string.Equals(target.Os, "Windows", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void CancelAudioSetup()
    {
        IsAudioSetupDialogVisible = false;
        IsAudioSetupChecking = false;
        AudioSetupDialogErrorText = string.Empty;
        IsAudioConnecting = false;
        IsAudioStreaming = false;
        AudioStatus = "Audio setup cancelled";
    }

    private async Task<bool> StartNormalAudioStreamingAsync()
    {
        if (SelectedTarget == null)
        {
            return false;
        }

        if (OperatingSystem.IsAndroid())
        {
            return await AudioStreamingService.Instance.StartMicrophoneSendAsync(SelectedTarget);
        }

        return await AudioStreamingService.Instance.StartLoopbackSendAsync(SelectedTarget);
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("cmd", $"/c start {url.Replace("&", "^&")}") { CreateNoWindow = true });
                return;
            }

            if (OperatingSystem.IsLinux())
            {
                Process.Start("xdg-open", url);
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", url);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"[RemoteControl] Failed to open browser: {ex.Message}");
        }
    }

    partial void OnIsAudioStreamingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartAudio));
        OnPropertyChanged(nameof(CanStopAudio));
    }

    partial void OnIsAudioConnectingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartAudio));
        OnPropertyChanged(nameof(CanStopAudio));
    }

    partial void OnIsAudioSetupCheckingChanged(bool value)
    {
        OnPropertyChanged(nameof(AudioSetupContinueButtonText));
        OnPropertyChanged(nameof(CanContinueAudioSetup));
        OnPropertyChanged(nameof(CanCancelAudioSetup));
    }

    partial void OnAudioSetupDialogErrorTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasAudioSetupDialogError));
    }

    // ── PC Keyboard Routing (PC → Android) ────────────────────────────────────

    [RelayCommand]
    private void TogglePcKeyboardRouting()
    {
        _keyboardSender?.ToggleRouting();
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

        // 🔒 Don't update PointerX/PointerY during drag - this prevents trackpad UI shaking
        // We only need delta positions, not absolute positions for the remote control

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
