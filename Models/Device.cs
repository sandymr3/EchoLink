using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace EchoLink.Models;

public enum DeviceSection { Ecosystem, Guests, OtherDevices }

public partial class Device : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _ipAddress = string.Empty;
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private string _deviceType = "Desktop"; // "Desktop" | "Phone" | "Laptop"
    [ObservableProperty] private string _os = string.Empty;
    [ObservableProperty] private string _lastSeen = string.Empty;
    [ObservableProperty] private bool _isSelf;
    [ObservableProperty] private bool _isPaired;
    [ObservableProperty] private int _sftpPort = 22; // Default to 22
    
    [ObservableProperty] private string _userId = string.Empty;
    [ObservableProperty] private List<string> _tags = new();
    [ObservableProperty] private DeviceSection _section = DeviceSection.Ecosystem;

    public string StatusLabel => IsSelf ? "This device" : (IsOnline ? "Online" : "Offline");
    public string DeviceIcon => DeviceType switch
    {
        "Phone"  => "📱",
        "Laptop" => "💻",
        _        => "🖥️"
    };
}
