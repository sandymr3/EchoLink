using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoLink.Models;
using EchoLink.Services;
using EchoLink.Services.UnifiedProtocol;

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
    [ObservableProperty] private string _audioStatus = "Audio idle";
    [ObservableProperty] private bool _isAudioStreaming;

    private double _lastX;
    private double _lastY;
    private bool   _isDragging;

    // Keyboard state
    private bool _isResetting = false;
    private string _previousText = " ";
    public Action? RequestKeyboardReset { get; set; }

    public RemoteControlViewModel()
    {
        _ = LoadDevicesAsync();
        
        // Subscribe to device discovery events - just update UI from cached data
        DeviceDiscoveryService.Instance.DeviceListChanged += () =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => LoadDevicesAsync());
        };
    }

    [RelayCommand]
    private async Task LoadDevicesAsync()
    {
        try
        {
            // Get feature target devices from DeviceDiscoveryService (already filtered and cached)
            // Dashboard controls the actual RefreshAsync call
            var devices = DeviceDiscoveryService.Instance.GetFeatureTargetDevices();
            
            OnlineDevices.Clear();
            foreach (var device in devices)
            {
                OnlineDevices.Add(device);
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
        await AudioStreamingService.Instance.StopAllAsync();
        IsAudioStreaming = false;
        AudioStatus = "Audio idle";
        ResetKeyboardTrap();

        if (target == null)
        {
            RemoteControlService.Instance.Disconnect();
            TrackpadStatus = "Disconnected";
            return;
        }

        TrackpadStatus = "Connecting...";
        bool success = await RemoteControlService.Instance.ConnectToTargetAsync(target, CancellationToken.None);
        
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
        if (SelectedTarget == null)
        {
            AudioStatus = "Select a target device first";
            return;
        }

        try
        {
            await AudioStreamingService.Instance.StopAllAsync();

            bool sendOk;

            if (OperatingSystem.IsAndroid())
            {
                // Send Android mic via low-latency unified protocol over mesh
                sendOk = await AudioStreamingService.Instance.StartMicrophoneSendAsync(SelectedTarget);

                IsAudioStreaming = sendOk;
                AudioStatus = sendOk
                    ? "Mic + playback active"
                    : "Audio start failed";
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
            IsAudioStreaming = false;
            AudioStatus = $"Audio error: {ex.Message}";
            _log.Error($"[RemoteControl] Audio start failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task StopAudioAsync()
    {
        await AudioStreamingService.Instance.StopAllAsync();
        IsAudioStreaming = false;
        AudioStatus = "Audio stopped";
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
