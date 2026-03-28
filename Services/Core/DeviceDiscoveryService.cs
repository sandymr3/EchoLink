using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EchoLink.Models;

namespace EchoLink.Services;

/// <summary>
/// Centralized device discovery and caching service.
/// Provides consistent device filtering across all features.
///
/// Filtering Rules:
/// - Devices from the same account (matching UserId) are always included
/// - Explicitly paired devices (in PeerUsernames) are included
/// - Devices from other accounts that aren't paired are excluded
/// </summary>
public class DeviceDiscoveryService
{
    private static readonly Lazy<DeviceDiscoveryService> _instance = new(() => new DeviceDiscoveryService());
    public static DeviceDiscoveryService Instance => _instance.Value;

    private readonly LoggingService _log = LoggingService.Instance;
    private readonly SettingsService _settings = SettingsService.Instance;

    private List<Device> _cachedDevices = new();
    private string _selfUserId = "";
    private string _selfIpAddress = "";
    private readonly object _lock = new();
    
    // Debouncing to prevent infinite refresh loops
    private DateTime _lastRefreshTime = DateTime.MinValue;
    private readonly TimeSpan _refreshDebounce = TimeSpan.FromSeconds(2);
    private bool _isRefreshing;

    /// <summary>
    /// Event fired when device list changes.
    /// ViewModels can subscribe to this instead of polling.
    /// </summary>
    public event Action? DeviceListChanged;

    /// <summary>
    /// The User ID of the local device (from Tailscale account).
    /// Used for filtering devices belonging to the same account.
    /// </summary>
    public string SelfUserId => _selfUserId;

    /// <summary>
    /// The Tailscale IP address of the local device.
    /// </summary>
    public string SelfIpAddress => _selfIpAddress;

    /// <summary>
    /// Gets all cached devices (includes self, unfiltered).
    /// For most use cases, prefer GetDevicesForFeature() or GetPeerDevices().
    /// </summary>
    public IReadOnlyList<Device> CachedDevices
    {
        get
        {
            lock (_lock)
            {
                return new List<Device>(_cachedDevices).AsReadOnly();
            }
        }
    }

    /// <summary>
    /// Gets the self device from the cache.
    /// </summary>
    public Device? GetSelfDevice()
    {
        lock (_lock)
        {
            return _cachedDevices.FirstOrDefault(d => d.IsSelf);
        }
    }

    /// <summary>
    /// Gets all peer devices (excludes self) that are eligible for features.
    /// Eligible devices are:
    /// - Devices from the same account (matching UserId)
    /// - Explicitly paired devices (in PeerUsernames)
    /// </summary>
    public List<Device> GetPeerDevices()
    {
        lock (_lock)
        {
            return _cachedDevices
                .Where(d => !d.IsSelf && IsEligibleDevice(d))
                .ToList();
        }
    }

    /// <summary>
    /// Gets online peer devices eligible for features.
    /// Same filtering as GetPeerDevices(), plus IsOnline check.
    /// </summary>
    public List<Device> GetOnlinePeerDevices()
    {
        lock (_lock)
        {
            return _cachedDevices
                .Where(d => !d.IsSelf && d.IsOnline && IsEligibleDevice(d))
                .ToList();
        }
    }

    /// <summary>
    /// Gets all paired devices (excludes self).
    /// This includes both same-account devices and explicitly paired devices from other accounts.
    /// </summary>
    public List<Device> GetPairedDevices()
    {
        lock (_lock)
        {
            return _cachedDevices
                .Where(d => !d.IsSelf && d.IsPaired)
                .ToList();
        }
    }

    /// <summary>
    /// Refreshes the device list from Tailscale.
    /// Should be called on app startup and when user manually refreshes.
    /// Automatically fires DeviceListChanged event.
    /// 
    /// Debounced to prevent infinite refresh loops (max once per 2 seconds).
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        // Debounce: prevent rapid-fire refreshes
        lock (_lock)
        {
            if (_isRefreshing)
            {
                _log.Debug("[DeviceDiscovery] Refresh already in progress, skipping");
                return;
            }
            
            var timeSinceLastRefresh = DateTime.UtcNow - _lastRefreshTime;
            if (timeSinceLastRefresh < _refreshDebounce)
            {
                _log.Debug($"[DeviceDiscovery] Debouncing refresh (only {(int)timeSinceLastRefresh.TotalMilliseconds}ms since last)");
                return;
            }
            
            _isRefreshing = true;
            _lastRefreshTime = DateTime.UtcNow;
        }

        try
        {
            var (selfIp, devices) = await TailscaleService.Instance.GetNetworkStatusAsync(ct: ct);

            lock (_lock)
            {
                _selfIpAddress = selfIp ?? "";
                _cachedDevices = devices;

                // Capture self user ID for consistent filtering
                _selfUserId = devices.FirstOrDefault(d => d.IsSelf)?.UserId ?? "";

                // Log device discovery for debugging (only on first refresh or when count changes)
                _log.Info($"[DeviceDiscovery] Refreshed: {devices.Count} total devices, SelfUserId={_selfUserId}, SelfIp={_selfIpAddress}");
                foreach (var device in devices)
                {
                    _log.Debug($"[DeviceDiscovery]   - {device.Name} ({device.IpAddress}): UserId={device.UserId}, IsPaired={device.IsPaired}, IsOnline={device.IsOnline}, IsSelf={device.IsSelf}");
                }
            }

            DeviceListChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Error($"[DeviceDiscovery] Refresh failed: {ex.Message}");
        }
        finally
        {
            lock (_lock)
            {
                _isRefreshing = false;
            }
        }
    }

    /// <summary>
    /// Checks if a device is eligible to be shown in feature lists.
    /// A device is eligible if it has IsPaired = true.
    /// 
    /// The IsPaired flag is set by TailscaleService.ParseDevice() based on:
    /// - Same account (matching UserId), OR
    /// - Explicitly paired (IP in PeerUsernames)
    /// 
    /// We trust this flag instead of re-checking PeerUsernames to avoid
    /// showing irrelevant devices that happen to have IPs in PeerUsernames.
    /// </summary>
    private bool IsEligibleDevice(Device device)
    {
        // Trust the IsPaired flag calculated by TailscaleService
        return device.IsPaired;
    }

    /// <summary>
    /// Gets devices specifically for clipboard sharing.
    /// Same as GetPeerDevices() but can be extended with clipboard-specific logic.
    /// </summary>
    public List<Device> GetClipboardShareDevices()
    {
        return GetPeerDevices();
    }

    /// <summary>
    /// Gets devices for remote control, macros, and system monitor.
    /// Same as GetOnlinePeerDevices() but can be extended with feature-specific logic.
    /// </summary>
    public List<Device> GetFeatureTargetDevices()
    {
        return GetOnlinePeerDevices();
    }
}
